using System.Text.Json;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Atas;
using TradeAgent.Core;
using TradeAgent.Gateway;
using TradeAgent.Security;
using TradeAgent.TradeCli;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// The vertical slice the whole product is built around:
/// agent -> trade -> pipe -> gateway -> connector -> account, and the answer all the way back.
/// </summary>
public class GatewayThroughPipeTests
{
    static string NewPipe() => "ta-it-" + Guid.NewGuid().ToString("n")[..12];

    [Fact]
    public async Task An_agent_can_read_the_account_and_place_an_order_over_the_pipe()
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var _ = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();

        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        var status = await client.SendAsync(new IpcRequest { Op = Ops.Status });
        Assert.True(status.Ok);

        var buy = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Buy,
            RequestId = "it-buy-1",
            Args = Args(("symbol", "ES"), ("quantity", "1"))
        });
        Assert.True(buy.Ok, Json.Write(buy.Error));

        var positions = await client.SendAsync(new IpcRequest { Op = Ops.Positions });
        Assert.True(positions.Ok);
        Assert.Contains("ES", Json.Write(positions.Data));

        Assert.Equal(ExecutionState.FILLED, gw.GetRequest("it-buy-1")!.State);
        Assert.Equal(1, conn.Broker.CountByClientOrderId(TradingGateway.ClientOrderIdFor("it-buy-1")));
    }

    [Fact]
    public async Task The_pipe_refuses_a_caller_with_the_wrong_token()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _2 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, "the-real-token", pipe);
        server.Start();

        await using var raw = new RawPipe(pipe);
        await raw.ConnectAsync();
        var hello = await raw.SendAsync(new IpcRequest { Op = Ops.Hello, Token = "not-the-token" });
        Assert.False(hello.Ok);
        Assert.Equal(nameof(ErrorCode.IPC_UNAUTHENTICATED), hello.Error!.Code);
    }

    [Fact]
    public async Task The_pipe_refuses_any_operation_before_a_successful_hello()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _2 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, "tok", pipe);
        server.Start();

        await using var raw = new RawPipe(pipe);
        await raw.ConnectAsync();
        var reply = await raw.SendAsync(new IpcRequest { Op = Ops.Buy, Args = Args(("symbol", "ES"), ("quantity", "1")) });
        Assert.False(reply.Ok);
        Assert.Equal(nameof(ErrorCode.IPC_UNAUTHENTICATED), reply.Error!.Code);
    }

    [Fact]
    public async Task Operator_authority_is_not_reachable_from_the_agent_channel()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _2 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();

        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        foreach (var op in new[] { "mode", "set-mode", "stop", "enable", "live", "activate-live", "approve", "risk" })
        {
            var reply = await client.SendAsync(new IpcRequest { Op = op });
            Assert.False(reply.Ok, $"'{op}' should not exist on the agent channel");
            Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), reply.Error!.Code);
        }
        Assert.Equal(TradingMode.PAPER, gw.Settings.Mode);
    }

    [Fact]
    public async Task The_schema_describes_the_commands_the_cli_actually_offers()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _2 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();

        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);
        var reply = await client.SendAsync(new IpcRequest { Op = Ops.Schema });

        Assert.True(reply.Ok);
        var text = Json.Write(reply.Data);
        foreach (var op in new[] { Ops.Status, Ops.Buy, Ops.Sell, Ops.Cancel, Ops.CancelAll, Ops.Close, Ops.CloseAll, Ops.Quote })
            Assert.Contains($"\"{op}\"", text);
        // The agent must be told what UNKNOWN means without having to read our source.
        Assert.Contains("never means the order failed", text);
    }

    internal static Dictionary<string, JsonElement> Args(params (string, string)[] pairs) =>
        pairs.ToDictionary(p => p.Item1, p => JsonSerializer.SerializeToElement(p.Item2));
}

/// <summary>A pipe client that skips the handshake, so authentication itself can be tested.</summary>
sealed class RawPipe(string pipe) : IAsyncDisposable
{
    System.IO.Pipes.NamedPipeClientStream? _p;
    StreamReader? _r;
    StreamWriter? _w;

    public async Task ConnectAsync()
    {
        _p = new System.IO.Pipes.NamedPipeClientStream(".", pipe, System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous);
        await _p.ConnectAsync(10_000);
        _r = new StreamReader(_p, System.Text.Encoding.UTF8, false, 8192, true);
        _w = new StreamWriter(_p, new System.Text.UTF8Encoding(false), 8192, true) { AutoFlush = true };
    }

    public async Task<IpcResponse> SendAsync(IpcRequest r)
    {
        await _w!.WriteLineAsync(Json.Write(r));
        var line = await _r!.ReadLineAsync() ?? throw new IOException("closed");
        return Json.Read<IpcResponse>(line)!;
    }

    public async ValueTask DisposeAsync()
    {
        if (_w is not null) await _w.DisposeAsync();
        _r?.Dispose();
        if (_p is not null) await _p.DisposeAsync();
    }
}

/// <summary>
/// Drives the real built trade assembly as a child process. This is the closest a macOS build host
/// gets to the shipped CLI: same bytes the publish step packages, invoked the way an agent invokes it.
/// </summary>
public class TradeCliTests
{
    [Fact]
    public async Task The_cli_reports_status_as_json_an_agent_can_parse()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _2 = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure());  // default pipe from TRADEAGENT_PIPE
        server.Start();

        var (code, stdout, stderr) = await Build.RunTradeAsync("status", "--json");
        Assert.Equal(0, code);

        using var doc = JsonDocument.Parse(stdout);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), stderr);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("PAPER", data.GetProperty("mode").GetString());
        Assert.True(data.GetProperty("execution_available").GetBoolean());
    }

    [Fact]
    public async Task The_cli_places_an_order_and_retrying_the_same_request_id_does_not_place_a_second()
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var _2 = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure());
        server.Start();

        var first = await Build.RunTradeAsync("buy", "ES", "1", "--request-id", "cli-idem-1", "--json");
        Assert.Equal(0, first.Code);
        var second = await Build.RunTradeAsync("buy", "ES", "1", "--request-id", "cli-idem-1", "--json");
        Assert.Equal(0, second.Code);

        // Two CLI invocations, one order at the broker.
        Assert.Equal(1, conn.Broker.CountByClientOrderId(TradingGateway.ClientOrderIdFor("cli-idem-1")));
        Assert.Single(conn.Broker.Orders);
    }

    [Fact]
    public async Task The_cli_explains_a_refusal_in_words_and_exits_nonzero()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _2 = db;
        gw.StopAiTrading("test");
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure());
        server.Start();

        var (code, stdout, _) = await Build.RunTradeAsync("buy", "ES", "1", "--json");
        Assert.Equal(1, code);
        using var doc = JsonDocument.Parse(stdout);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        var err = doc.RootElement.GetProperty("error");
        Assert.Equal("AI_TRADING_STOPPED", err.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(err.GetProperty("user_message").GetString()));
    }

    [Fact]
    public async Task The_cli_fails_clearly_when_the_service_is_not_running()
    {
        // No gateway started: the agent must get a structured answer, not a stack trace.
        var (code, stdout, _) = await Build.RunTradeAsync("status", "--json");
        Assert.Equal(1, code);
        using var doc = JsonDocument.Parse(stdout);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("IPC_UNAVAILABLE", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}

/// <summary>
/// Exercises the ATAS connector against a stub that speaks the real bridge protocol. ATAS itself is
/// unverified until this runs on Windows against the platform — see docs/RESEARCH-REQUIRED.md.
/// </summary>
public class AtasProtocolTests
{
    static string NewPipe() => "ta-bridge-" + Guid.NewGuid().ToString("n")[..12];

    [Fact]
    public async Task Capabilities_and_accounts_come_from_the_bridge_handshake()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        await connector.ConnectAsync();

        await using var bridge = new StubBridge(pipe);
        await bridge.ConnectAsync();
        await WaitUntil(async () => await connector.IsConnectedAsync());

        Assert.True(connector.Capabilities.ReconciliationProvable);
        Assert.True(connector.Capabilities.IsPaper);
        var accounts = await connector.GetAccountsAsync();
        Assert.Equal("ATAS-SIM", accounts.Single().Id);
    }

    [Fact]
    public async Task An_order_reaches_the_bridge_carrying_the_client_order_id()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        await connector.ConnectAsync();
        await using var bridge = new StubBridge(pipe);
        await bridge.ConnectAsync();
        await WaitUntil(async () => await connector.IsConnectedAsync());

        var order = await connector.PlaceOrderAsync(new PlaceOrderCommand("TA-abc", "ATAS-SIM", "ES",
            OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, null));

        Assert.Equal("TA-abc", bridge.Placed.Single().ClientOrderId);
        Assert.Equal("TA-abc", order.ClientOrderId);
        Assert.Equal(ExecutionState.FILLED, order.State);
    }

    [Fact]
    public async Task A_bridge_speaking_the_wrong_protocol_version_is_refused_outright()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(5));
        await connector.ConnectAsync();

        await using var bridge = new StubBridge(pipe, new BridgeHello
        {
            BridgeProtocolVersion = Versions.BridgeProtocolVersion + 99,
            SupportsClientOrderId = true, SupportsOrderHistory = true
        });
        await bridge.ConnectAsync();
        await Task.Delay(500);

        Assert.False(await connector.IsConnectedAsync());
        Assert.Equal(HealthState.FAILED, await connector.GetHealthAsync());
        // And it must be refused as indefinite-but-unusable, not silently half-trusted.
        await Assert.ThrowsAsync<ConnectorTransportException>(() => connector.GetAccountsAsync());
    }

    [Fact]
    public async Task A_silent_bridge_times_out_as_indefinite_rather_than_as_a_rejection()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromMilliseconds(600));
        await connector.ConnectAsync();
        await using var bridge = new StubBridge(pipe) { AnswerRpcs = false };
        await bridge.ConnectAsync();
        await WaitUntil(async () => await connector.IsConnectedAsync());

        // A timeout must never look like "the broker said no" — that distinction decides whether
        // the gateway reconciles or writes the order off.
        await Assert.ThrowsAsync<ConnectorTransportException>(() => connector.GetAccountsAsync());
    }

    [Fact]
    public async Task Losing_the_bridge_marks_the_connector_failed()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(5));
        await connector.ConnectAsync();
        var bridge = new StubBridge(pipe);
        await bridge.ConnectAsync();
        await WaitUntil(async () => await connector.IsConnectedAsync());

        await bridge.DisposeAsync();
        await WaitUntil(async () => !await connector.IsConnectedAsync());
        Assert.Equal(HealthState.FAILED, await connector.GetHealthAsync());
    }

    static async Task WaitUntil(Func<Task<bool>> condition, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("condition was not met in time");
    }
}

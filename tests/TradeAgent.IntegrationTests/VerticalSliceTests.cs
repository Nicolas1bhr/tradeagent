using System.IO.Pipes;
using System.Text;
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

    /// <summary>
    /// THE HOLE THIS CLOSES: a peer on the bridge pipe that has proved nothing must not be able to
    /// grant itself autonomous live trading by describing itself favourably.
    ///
    /// <c>Capabilities</c> is derived from the hello; <c>ReconciliationProvable</c> is
    /// <c>SupportsClientOrderId &amp;&amp; SupportsOrderHistory</c>; and TradingGateway consults exactly
    /// that property before it will permit LIVE_AUTONOMOUS. So a hello KEPT from an unproved peer is
    /// operator authority reachable from a pipe, which the product forbids outright. The assertion
    /// on <c>ReconciliationProvable</c> by name is the point of the test: one that only checked that
    /// the hello was discarded would go on passing a refactor that kept the capabilities elsewhere.
    ///
    /// AND IT MUST NOT TURN ON A CLOCK. <c>AuthGrace</c> is set to half an hour here, so a refusal
    /// that waited for it could not possibly land inside this test — only one decided at the hello
    /// can, which is the whole difference between a refusal and a display-only reading.
    ///
    /// CATCHES: keeping <c>_hello</c> before the authentication check; checking authentication only
    /// on the heartbeat's refreshed frame; and any grace period smuggled into the refusal.
    /// </summary>
    [Fact]
    public async Task A_peer_that_proved_nothing_cannot_unlock_autonomy_however_it_describes_itself()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(5))
        {
            AuthGrace = TimeSpan.FromMinutes(30)
        };
        await connector.ConnectAsync();

        await using var rogue = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await rogue.ConnectAsync(10_000);
        await using var w = new StreamWriter(rogue, new UTF8Encoding(false)) { AutoFlush = true };

        // Everything an over-trusting connector could be talked into believing, at the exact
        // protocol version this build speaks so that the version gate cannot be what refuses it —
        // sent by something that has offered no proof of anything at all.
        await w.WriteLineAsync(Json.Write(new BridgeFrame
        {
            Op = BridgeOps.Hello,
            Data = JsonSerializer.SerializeToElement(new BridgeHello
            {
                BridgeProtocolVersion = Versions.BridgeProtocolVersion,
                BridgeVersion = "9.9.9-rogue", AtasVersion = "8.0.14.397", AccountId = "ATAS-SIM",
                IsSimulated = false,          // a LIVE account, which is the case that matters
                SupportsClientOrderId = true,
                SupportsOrderHistory = true,
                SupportsModify = true,
                SupportsClosePosition = true
            }, Json.Options)
        }));

        // Wait until the hello has been PROCESSED, whichever way it was decided — connected means it
        // was served, a named peer means it was refused. Waiting only for the refusal would make a
        // build that serves an unproved peer fail with "condition was not met in time", which says
        // nothing about autonomy; this way such a build fails on the assertion below, with the
        // capability it granted written into the message.
        await WaitUntil(async () => await connector.IsConnectedAsync() || connector.Unauthenticated is not null);

        // The gate itself, named. Not one of the four claims got through.
        Assert.False(connector.Capabilities.ReconciliationProvable,
            "a peer that proved nothing made ReconciliationProvable true, which is the property " +
            "TradingGateway consults before permitting LIVE_AUTONOMOUS: anything holding this pipe " +
            "can now unlock autonomous live trading by claiming two booleans");
        Assert.False(connector.Capabilities.SupportsClientOrderId);
        Assert.False(connector.Capabilities.SupportsOrderHistory);
        Assert.Null(connector.Bridge);
        Assert.False(await connector.IsConnectedAsync());
        Assert.Equal(HealthState.FAILED, await connector.GetHealthAsync());
        await Assert.ThrowsAsync<ConnectorTransportException>(() => connector.GetAccountsAsync());

        // A refusal nobody can read costs a session every time. It has to be a sentence on the
        // status row, it has to name what claimed to be there, and it has to say which failures
        // this is NOT: a bridge DLL built without ATAS support, the wrong Strategies folder and a
        // chart strategy restored stopped are all SILENCE on this pipe — and this pipe answered.
        var said = connector.StatusDetail!;
        Assert.Contains("did not authenticate", said);
        Assert.Contains("9.9.9-rogue", said);
        Assert.Contains("failed to load", said);
        Assert.Contains("Strategies folder", said);
        Assert.Contains("restored stopped", said);
        Assert.Contains("press Reinstall the bridge", said);
    }

    /// <summary>
    /// The same unlock, one frame to the left — and the reason refusing only the hello is not enough.
    ///
    /// A heartbeat carries a whole BridgeHello: that is how a capability proved after the handshake
    /// reaches this end at all (see BridgeRoundTripTests). So a peer that never says hello, and is
    /// therefore never refused for saying one, can offer the capabilities on a heartbeat instead. If
    /// that frame is adopted, ReconciliationProvable goes true and autonomous live trading is
    /// unlocked by something that proved nothing — with the connector still reporting FAILED, which
    /// is what makes it easy to miss.
    ///
    /// CATCHES: gating the hello on authentication and forgetting the refresh frame.
    /// </summary>
    [Fact]
    public async Task A_peer_that_proved_nothing_cannot_unlock_autonomy_through_a_heartbeat_either()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(5));
        await connector.ConnectAsync();

        await using var rogue = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await rogue.ConnectAsync(10_000);
        await using var w = new StreamWriter(rogue, new UTF8Encoding(false)) { AutoFlush = true };

        // No hello at any point, so the hello refusal never gets a chance to fire.
        for (var i = 0; i < 5; i++)
            await w.WriteLineAsync(Json.Write(new BridgeFrame
            {
                Op = BridgeOps.Heartbeat,
                Data = JsonSerializer.SerializeToElement(new BridgeHello
                {
                    BridgeProtocolVersion = Versions.BridgeProtocolVersion,
                    BridgeVersion = "9.9.9-rogue", AtasVersion = "8.0.14.397", AccountId = "ATAS-SIM",
                    IsSimulated = false,
                    SupportsClientOrderId = true,
                    SupportsOrderHistory = true
                }, Json.Options)
            }));

        // Long enough that every frame above has certainly been read and dispatched.
        await Task.Delay(500);

        Assert.False(connector.Capabilities.ReconciliationProvable,
            "a peer that proved nothing made ReconciliationProvable true through the heartbeat's " +
            "refreshed capability frame, so the hello refusal can simply be walked around");
        Assert.Null(connector.Bridge);
        Assert.False(await connector.IsConnectedAsync());
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

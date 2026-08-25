using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Atas;
using TradeAgent.Core;
using Xunit;

// These tests bind real named pipes, so they must not run concurrently with each other.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace TradeAgent.Tests.Integration;

/// <summary>Locates build outputs so a test can drive the real CLI assembly rather than its source.</summary>
public static class Build
{
    public static string RepoRoot { get; } = Find();

    static string Find()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "src"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("could not find the repository root");
    }

    public static string DotnetHost =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";

    /// <summary>The built trade CLI assembly. Newest wins, so a rebuilt binary is always the one tested.</summary>
    public static string TradeCliDll
    {
        get
        {
            var dir = Path.Combine(RepoRoot, "src", "TradeAgent.TradeCli", "bin");
            var hit = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "trade.dll", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                : null;
            return hit ?? throw new InvalidOperationException($"trade.dll not found under {dir}; build TradeAgent.TradeCli first");
        }
    }

    public static async Task<(int Code, string Out, string Err)> RunTradeAsync(params string[] args)
    {
        var psi = new ProcessStartInfo(DotnetHost)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        psi.ArgumentList.Add(TradeCliDll);
        foreach (var a in args) psi.ArgumentList.Add(a);
        // The child inherits TRADEAGENT_HOME / TRADEAGENT_PIPE from this test process.
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await p.WaitForExitAsync(cts.Token);
        return (p.ExitCode, await so, await se);
    }
}

/// <summary>
/// Stands in for the component loaded inside ATAS, speaking the real bridge protocol over a real
/// pipe. It cannot prove ATAS itself works, but it does prove the protocol, the RPC plumbing, the
/// capability handshake and the version gate are correct.
/// </summary>
public sealed class StubBridge : IAsyncDisposable
{
    readonly string _pipe;
    readonly BridgeHello _hello;
    readonly CancellationTokenSource _cts = new();
    NamedPipeClientStream? _client;
    StreamWriter? _w;
    Task? _loop;

    public List<PlaceOrderCommand> Placed { get; } = [];
    public List<OrderInfo> Book { get; } = [];
    public bool AnswerRpcs { get; set; } = true;

    public StubBridge(string pipe, BridgeHello? hello = null)
    {
        _pipe = pipe;
        _hello = hello ?? new BridgeHello
        {
            BridgeProtocolVersion = Versions.BridgeProtocolVersion,
            BridgeVersion = "0.1.0-stub", AtasVersion = "stub", AccountId = "ATAS-SIM",
            IsSimulated = true, SupportsClientOrderId = true, SupportsOrderHistory = true,
            SupportsModify = true, SupportsClosePosition = true
        };
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _client = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await _client.ConnectAsync(10_000, ct);
        _w = new StreamWriter(_client, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };
        await Send(new { v = Versions.BridgeProtocolVersion, op = BridgeOps.Hello, data = _hello });
        _loop = Task.Run(() => Loop(_cts.Token));
    }

    Task Send(object o) => _w!.WriteLineAsync(Json.Write(o));

    async Task Loop(CancellationToken ct)
    {
        var reader = new StreamReader(_client!, new UTF8Encoding(false), false, 8192, leaveOpen: true);
        string? line;
        while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct)) is not null)
        {
            var f = Json.Read<BridgeFrame>(line);
            if (f?.Op is null || f.Id is null || !AnswerRpcs) continue;

            object? data = f.Op switch
            {
                BridgeOps.Accounts => new[] { new AccountInfo("ATAS-SIM", "ATAS simulation", "USD", 50_000m, 50_000m, 0m, true, true) },
                BridgeOps.Instruments => new[] { new InstrumentInfo("ES", "E-mini S&P", "CME", 0.25m, 12.5m, 50m) },
                BridgeOps.Quote => new QuoteInfo("ES", 4300m, 4300.25m, 4300.1m, 5, 5, DateTimeOffset.UtcNow),
                BridgeOps.Positions => Array.Empty<PositionInfo>(),
                BridgeOps.Orders => Book.ToArray(),
                BridgeOps.Executions => Array.Empty<ExecutionInfo>(),
                BridgeOps.Place => Place(f),
                _ => null
            };
            await Send(new { v = Versions.BridgeProtocolVersion, id = f.Id, ok = true, data });
        }
    }

    object Place(BridgeFrame f)
    {
        var cmd = f.Data!.Value.Deserialize<PlaceOrderCommand>(Json.Options)!;
        Placed.Add(cmd);
        var order = new OrderInfo($"ATAS-{Placed.Count}", cmd.ClientOrderId, cmd.AccountId, cmd.Symbol,
            cmd.Side, cmd.Type, cmd.Quantity, cmd.Quantity, cmd.LimitPrice, cmd.StopPrice,
            ExecutionState.FILLED, null, DateTimeOffset.UtcNow);
        Book.Add(order);
        return order;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_w is not null) await _w.DisposeAsync();
        if (_client is not null) await _client.DisposeAsync();
        _cts.Dispose();
    }
}

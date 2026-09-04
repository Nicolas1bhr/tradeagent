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
///
/// IT AUTHENTICATES FOR REAL, and there is no way to make it skip that. It reads the same
/// <c>bridge.auth</c> the real bridge reads (<see cref="BridgePipeAuth.ReadForClient"/>), challenges
/// the pipe owner over a nonce it chose, and checks the answer before it says hello — because the
/// connector now refuses a hello from a peer that presented no proof. The alternative was a
/// test-only way past that refusal, and an escape hatch in an authentication path is exactly the
/// kind of thing that ships.
/// </summary>
public sealed class StubBridge : IAsyncDisposable
{
    readonly string _pipe;
    readonly BridgeHello _hello;
    readonly BridgeCredential? _fixedCredential;
    readonly CancellationTokenSource _cts = new();
    NamedPipeClientStream? _client;
    StreamReader? _r;
    StreamWriter? _w;
    Task? _loop;

    public List<PlaceOrderCommand> Placed { get; } = [];
    public List<OrderInfo> Book { get; } = [];
    public bool AnswerRpcs { get; set; } = true;

    /// <summary>
    /// Whether the handshake hello is sent at all. False lets a test put a frame on the wire from a
    /// peer that has AUTHENTICATED and said nothing about its protocol — which is the state the
    /// event gate has to refuse.
    /// </summary>
    public bool SendHello { get; set; } = true;

    public StubBridge(string pipe, BridgeHello? hello = null, BridgeCredential? credential = null)
    {
        _pipe = pipe;
        _fixedCredential = credential;
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

        // ONE READER for the whole connection, held in a field and shared with Loop(). The peer
        // answers the challenge and then sits waiting, so a second reader created for the command
        // loop would silently discard whatever the first had already buffered — the same trap
        // BridgeServer and tools/probe both carry a comment about.
        _r = new StreamReader(_client, new UTF8Encoding(false), false, 8192, leaveOpen: true);

        await Authenticate(ct);
        if (SendHello) await Send(new { v = Versions.BridgeProtocolVersion, op = BridgeOps.Hello, data = _hello });
        // The loop ends when the far end goes away, which for a refused peer is immediately. Its
        // exception is the disconnection, not a fault worth surfacing.
        _loop = Task.Run(async () => { try { await Loop(_cts.Token); } catch (Exception) { } });
    }

    /// <summary>
    /// The bridge's half of the handshake, done the way BridgeServer does it: challenge the pipe
    /// owner over a nonce chosen here, then require the matching server-role proof back. A stub that
    /// merely sent the challenge and carried on would let a connector which never answered look
    /// authenticated, which is the one thing this must not be able to do.
    /// </summary>
    async Task Authenticate(CancellationToken ct)
    {
        var cred = _fixedCredential ?? BridgePipeAuth.ReadForClient()
            ?? throw new InvalidOperationException(
                $"no bridge credential at {BridgePipeAuth.CredentialFile}; the pipe owner publishes " +
                "it before it accepts a connection, so reaching this means it never did");

        var nonce = BridgePipeAuth.NewNonce();
        await Send(new
        {
            v = Versions.BridgeProtocolVersion,
            op = BridgePipeAuth.Challenge,
            data = new { nonce, proof = BridgePipeAuth.Proof(cred.Secret, BridgePipeAuth.BridgeRole, nonce) }
        });

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        string? line;
        while ((line = await _r!.ReadLineAsync(deadline.Token)) is not null)
        {
            BridgeFrame? f;
            try { f = Json.Read<BridgeFrame>(line); } catch (JsonException) { continue; }
            if (f?.Op == BridgePipeAuth.Refused)
                throw new InvalidOperationException($"the pipe owner refused this stub bridge: {f.Error}");
            if (f?.Op != BridgePipeAuth.Response) continue;

            var proof = f.Data.HasValue && f.Data.Value.TryGetProperty("proof", out var p) ? p.GetString() : null;
            if (!BridgePipeAuth.ProofMatches(cred.Secret, BridgePipeAuth.ServerRole, nonce, proof))
                throw new InvalidOperationException("the pipe owner answered the challenge with the wrong proof");
            return;
        }
        throw new InvalidOperationException("the pipe owner closed the connection without authenticating");
    }

    /// <summary>
    /// Raises a bridge event the way the real bridge does. Nothing else in the suite sent one, which
    /// is how an authenticated peer speaking a refused protocol kept its event channel.
    /// </summary>
    public Task RaiseEvent(string name, object payload) =>
        Send(new { v = Versions.BridgeProtocolVersion, @event = name, data = payload });

    /// <summary>A hello of the caller's choosing, at any time — including after one was refused.</summary>
    public Task SaySomethingElse(BridgeHello hello) =>
        Send(new { v = Versions.BridgeProtocolVersion, op = BridgeOps.Hello, data = hello });

    /// <summary>
    /// A heartbeat carrying a whole hello, which is how a capability proved after the handshake
    /// reaches the connector — and the frame a refused peer used to re-enter through.
    /// </summary>
    public Task Heartbeat(BridgeHello hello) =>
        Send(new { v = Versions.BridgeProtocolVersion, op = BridgeOps.Heartbeat, data = hello });

    Task Send(object o) => _w!.WriteLineAsync(Json.Write(o));

    async Task Loop(CancellationToken ct)
    {
        var reader = _r!;
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

    /// <summary>
    /// TEARDOWN SURVIVES A CONNECTION THE FAR END ALREADY CLOSED, which since round 7 is the ordinary
    /// end of a refused peer: the connector drops a bridge whose protocol it cannot speak, so this
    /// stub's writer flushes into a pipe that is already broken. A harness that throws while tidying
    /// up fails the test for the very behaviour the test is asserting.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await Quietly(async () => { if (_w is not null) await _w.DisposeAsync(); });
        try { _r?.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
        await Quietly(async () => { if (_client is not null) await _client.DisposeAsync(); });
        _cts.Dispose();
    }

    static async Task Quietly(Func<Task> step)
    {
        try { await step(); } catch (IOException) { } catch (ObjectDisposedException) { }
    }
}

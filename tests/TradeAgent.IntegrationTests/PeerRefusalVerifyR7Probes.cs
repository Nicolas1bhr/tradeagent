using TradeAgent.Connectors.Atas;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// U14 round-7 ADVERSARIAL-VERIFY probes, carried from round 6 (V1's own acceptance) plus round 7b. For a protocol-mismatched
/// peer the read loop keeps `return true`, so the peer is not dropped — it is left parked on the pipe,
/// read by nobody. `CreateServer()` builds the pipe with **maxNumberOfServerInstances = 1**
/// (`AtasConnector.cs:220` and `:223`) and the accept loop only creates the next instance after the
/// inner read loop ends. The question the brief asks: can a fixed bridge still get in?
/// </summary>
public class PeerRefusalVerifyR7Probes
{
    static string NewPipe() => "ta-park7-" + Guid.NewGuid().ToString("n")[..12];

    static async Task Wait(Func<bool> c, int ms)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline) { if (c()) return; await Task.Delay(50); }
        throw new TimeoutException("condition was not met in time");
    }

    /// <summary>Authenticates the way StubBridge does, then leaves the connection open and silent.</summary>
    static async Task<(System.IO.Pipes.NamedPipeClientStream Client, StreamWriter W)> ParkAsync(string pipe, int protocolVersion)
    {
        var client = new System.IO.Pipes.NamedPipeClientStream(
            ".", pipe, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        await client.ConnectAsync(10_000);
        var w = new StreamWriter(client, new System.Text.UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };
        var r = new StreamReader(client, new System.Text.UTF8Encoding(false), false, 8192, leaveOpen: true);

        var cred = BridgePipeAuth.ReadForClient()!;
        var nonce = BridgePipeAuth.NewNonce();
        await w.WriteLineAsync(Json.Write(new
        {
            v = Versions.BridgeProtocolVersion,
            op = BridgePipeAuth.Challenge,
            data = new { nonce, proof = BridgePipeAuth.Proof(cred.Secret, BridgePipeAuth.BridgeRole, nonce) }
        }));
        string? line;
        while ((line = await r.ReadLineAsync()) is not null)
            if (Json.Read<BridgeFrame>(line)?.Op == BridgePipeAuth.Response) break;

        await w.WriteLineAsync(Json.Write(new BridgeFrame
        {
            Op = BridgeOps.Hello,
            Data = System.Text.Json.JsonSerializer.SerializeToElement(
                new BridgeHello { BridgeProtocolVersion = protocolVersion, BridgeVersion = "0.1.1", AtasVersion = "6.1.2.3" },
                Json.Options)
        }));
        return (client, w);
    }

    /// <summary>
    /// THE ACCEPTANCE: a refused peer that never speaks again must not keep the fixed bridge out.
    /// The row tells the operator to "reinstall the add-on" — this asserts that doing so works.
    /// </summary>
    [Fact]
    public async Task A_parked_refused_peer_does_not_keep_a_fixed_bridge_off_the_pipe()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        await connector.ConnectAsync();
        await using var _1 = connector;

        // A version-2 peer arrives, is refused, and then simply stops talking. It does not disconnect.
        var (parked, _) = await ParkAsync(pipe, 2);
        await Wait(() => connector.Incompatible is not null, 10_000);
        Assert.Equal(2, connector.Incompatible!.ReportedProtocolVersion);

        // The operator does what the row says and a current bridge dials in.
        await using var fixedBridge = new StubBridge(pipe);
        var connected = true;
        try { await fixedBridge.ConnectAsync(); }
        catch (Exception) { connected = false; }

        var arrived = true;
        try { await Wait(() => connector.Bridge is not null, 6_000); }
        catch (TimeoutException) { arrived = false; }

        parked.Dispose();

        Assert.True(connected && arrived,
            $"a parked refused peer kept the fixed bridge off the pipe: clientConnected={connected} "
            + $"helloAccepted={arrived} Bridge={(connector.Bridge is null ? "null" : "SET")} "
            + $"Incompatible={(connector.Incompatible is null ? "null" : "reported=" + connector.Incompatible.ReportedProtocolVersion)} "
            + $"StatusDetail=\"{connector.StatusDetail}\"");
    }

    /// <summary>The control: with the refused peer GONE, the fixed bridge is accepted at once.</summary>
    [Fact]
    public async Task A_fixed_bridge_is_accepted_once_the_refused_peer_disconnects()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        await connector.ConnectAsync();
        await using var _1 = connector;

        var (parked, _) = await ParkAsync(pipe, 2);
        await Wait(() => connector.Incompatible is not null, 10_000);
        parked.Dispose();                       // ATAS restarts to pick up the reinstalled add-on

        await using var fixedBridge = new StubBridge(pipe);
        await fixedBridge.ConnectAsync();
        await Wait(() => connector.Bridge is not null, 10_000);
        Assert.Equal(Versions.BridgeProtocolVersion, connector.Bridge!.BridgeProtocolVersion);
    }

    /// <summary>
    /// ROUND 7B, THE HALF THE RULE DOES NOT COVER: a stale refusal masking a live one.
    ///
    /// `Drop` no longer clears `_incompatible` at all — only an accepted hello does (`:442`). But
    /// `_unauthenticated` is sticky for the same reason, and `StatusDetail` is
    /// `_incompatible?.ToString() ?? Unauthenticated?.ToString()` (`:120`), which prefers the OLDER
    /// of the two. So after a v2 refusal, a peer that later fails AUTHENTICATION is reported with the
    /// previous peer's protocol message: the operator reinstalls the add-on, the reinstall does not
    /// authenticate, and the row goes on saying "reinstall the add-on".
    /// </summary>
    [Fact]
    public async Task A_live_refusal_is_not_masked_by_a_stale_one()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        await connector.ConnectAsync();
        await using var _1 = connector;

        // 1. A version-2 bridge is refused. This is the state round 7b makes permanent.
        var (v2, _) = await ParkAsync(pipe, 2);
        await Wait(() => connector.Incompatible is not null, 10_000);
        var stale = connector.StatusDetail;
        Assert.Contains("protocol 2", stale);
        v2.Dispose();

        // 2. The operator reinstalls. The new bridge reaches the pipe but cannot prove itself —
        //    a wrong TRADEAGENT_HOME or a stale bridge.auth, both documented failure modes. It
        //    presents NO proof and says hello.
        await Task.Delay(200);
        using var impostor = new System.IO.Pipes.NamedPipeClientStream(
            ".", pipe, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        await impostor.ConnectAsync(10_000);
        var w = new StreamWriter(impostor, new System.Text.UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };
        try
        {
            await w.WriteLineAsync(Json.Write(new BridgeFrame
            {
                Op = BridgeOps.Hello,
                Data = System.Text.Json.JsonSerializer.SerializeToElement(
                    new BridgeHello { BridgeProtocolVersion = Versions.BridgeProtocolVersion, BridgeVersion = "0.1.2", AtasVersion = "6.1.2.3" },
                    Json.Options)
            }));
        }
        catch (IOException) { }

        await Wait(() => connector.Unauthenticated is not null, 10_000);

        // THE INVARIANT: the sentence the operator reads describes the peer that is there NOW.
        var live = connector.StatusDetail;
        Assert.True(live is not null && !live.Contains("protocol 2", StringComparison.Ordinal),
            $"a stale protocol refusal is masking a live authentication refusal.\n"
            + $"  StatusDetail now = \"{live}\"\n"
            + $"  Incompatible     = {(connector.Incompatible is null ? "null" : "reported=" + connector.Incompatible.ReportedProtocolVersion)}\n"
            + $"  Unauthenticated  = \"{connector.Unauthenticated}\"");
    }
}


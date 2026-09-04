using System.IO.Pipes;
using System.Text;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Atas;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE ROW DESCRIBES THE PEER THAT IS THERE NOW — a test per cell of the precedence table, in the
/// shipped suite.
///
/// Three of the four cases below are the round-9 VERIFIER's probes, lifted verbatim from
/// `u14-verify-r9-probes` (R9-3). They lived in a probe class the shipped suite filtered out, so the
/// mutant they caught — an explicit credential refusal always outranking the derived reading,
/// whatever the stamps say — survived `BridgeRoundTripTests`, `PeerRefusalVerifyR7Probes` and
/// `VerticalSliceTests` and was caught only in a verifier's worktree. A cell that is argued in a
/// table and tested nowhere is a cell that is unpinned, so they are here.
///
/// The fourth is Codex F38, and it is the reason the round has a directive about this at all: a
/// CURRENT connection must always yield a status of its own that is newer than any marker. A peer
/// that authenticated and has not said hello yet was reported as NOTHING, so the row went on
/// describing the connection before it — telling an operator to reinstall an add-on that had already
/// been replaced by the program now sitting on the pipe.
/// </summary>
public class PeerRowTests
{
    static string NewPipe() => "ta-row10-" + Guid.NewGuid().ToString("n")[..12];

    /// <summary>A well-formed secret that is not this installation's — a stale bridge.auth.</summary>
    static readonly string WrongSecret = new('a', 64);

    static async Task Wait(Func<bool> c, int ms = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline) { if (c()) return; await Task.Delay(50); }
        throw new TimeoutException("condition was not met in time");
    }

    /// <summary>A raw client that opens the pipe and does nothing else.</summary>
    static async Task<NamedPipeClientStream> SilentAsync(string pipe)
    {
        var c = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await c.ConnectAsync(10_000);
        return c;
    }

    /// <summary>Answers the challenge (correctly or not) and optionally says a hello at that version.</summary>
    static async Task<(NamedPipeClientStream Client, StreamWriter W)> PeerAsync(
        string pipe, bool goodProof, int? helloVersion)
    {
        var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(10_000);
        var w = new StreamWriter(client, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };
        var r = new StreamReader(client, new UTF8Encoding(false), false, 8192, leaveOpen: true);

        var cred = BridgePipeAuth.ReadForClient()!;
        var nonce = BridgePipeAuth.NewNonce();
        var proof = goodProof
            ? BridgePipeAuth.Proof(cred.Secret, BridgePipeAuth.BridgeRole, nonce)
            : BridgePipeAuth.Proof(WrongSecret, BridgePipeAuth.BridgeRole, nonce);
        await w.WriteLineAsync(Json.Write(new
        {
            v = Versions.BridgeProtocolVersion,
            op = BridgePipeAuth.Challenge,
            data = new { nonce, proof }
        }));

        if (goodProof)
        {
            string? line;
            while ((line = await r.ReadLineAsync()) is not null)
                if (Json.Read<BridgeFrame>(line)?.Op == BridgePipeAuth.Response) break;
        }

        if (helloVersion is { } v)
            await w.WriteLineAsync(Json.Write(new BridgeFrame
            {
                Op = BridgeOps.Hello,
                Data = System.Text.Json.JsonSerializer.SerializeToElement(
                    new BridgeHello
                    {
                        BridgeProtocolVersion = v,
                        BridgeVersion = "0.0.9", AtasVersion = "6.1.2.3", AccountId = "ATAS-SIM"
                    }, Json.Options)
            }));
        return (client, w);
    }

    // ------------------------------------------------------------------ target 3

    /// <summary>
    /// THE PERMUTATION THE BOUNCE BRIEF NAMES AND NOTHING BUILDS: a silent peer that LATER speaks v2.
    /// The derived silence is stamped with the moment the connection began, so the peer's own protocol
    /// refusal — written after it arrived — has to outrank its earlier silence. Within one connection,
    /// which is the half the ACROSS-connections fix must not have broken.
    /// </summary>
    [Fact]
    public async Task A_silent_peer_that_later_speaks_v2_reports_the_protocol_refusal()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(5))
        {
            AuthGrace = TimeSpan.FromMilliseconds(300),
            HeartbeatTimeout = TimeSpan.FromSeconds(30)      // long enough that nothing is dropped
        };
        await connector.ConnectAsync();

        using var peer = await SilentAsync(pipe);
        await Wait(() => connector.Unauthenticated is not null);
        Assert.Contains("neither proved itself nor said", connector.StatusDetail!);

        // The SAME connection now speaks: it proves itself and announces protocol 2.
        var w = new StreamWriter(peer, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };
        var r = new StreamReader(peer, new UTF8Encoding(false), false, 8192, leaveOpen: true);
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
                new BridgeHello
                {
                    BridgeProtocolVersion = 2,
                    BridgeVersion = "0.0.9", AtasVersion = "6.1.2.3", AccountId = "ATAS-SIM"
                }, Json.Options)
        }));

        await Wait(() => connector.Incompatible is not null);
        Assert.Contains("speaks protocol 2", connector.StatusDetail!);
        Assert.DoesNotContain("neither proved itself nor said", connector.StatusDetail!);
    }

    /// <summary>
    /// THE CELL THE CLASS-CLOSURE TABLE ARGUES AND DOES NOT TEST: an explicit CREDENTIAL refusal from
    /// a previous connection versus the derived silence of the peer that is there now. Codex's F32
    /// CHECK says in as many words "an older explicit `_unauthenticated` marker behaves similarly";
    /// the builder's new test uses a PROTOCOL marker, and the table's credential row names no test for
    /// the across-connections case.
    /// </summary>
    [Fact]
    public async Task A_newly_arrived_silent_peer_is_not_masked_by_the_previous_peers_auth_failure()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(5))
        {
            AuthGrace = TimeSpan.FromMilliseconds(300),
            HeartbeatTimeout = TimeSpan.FromSeconds(30)
        };
        await connector.ConnectAsync();

        // A peer that answers the challenge WRONG, and is dropped for it.
        var (wrong, _) = await PeerAsync(pipe, goodProof: false, helloVersion: null);
        await Wait(() => connector.Unauthenticated is not null);
        Assert.Contains("could not prove", connector.StatusDetail!);
        wrong.Dispose();

        // A different program takes the pipe and says nothing whatever.
        using var quiet = await SilentAsync(pipe);
        await Wait(() => connector.StatusDetail?.Contains("neither proved itself nor said") == true);

        var row = connector.StatusDetail!;
        Assert.Contains("neither proved itself nor said", row);
        Assert.DoesNotContain("could not prove", row);
    }

    /// <summary>
    /// AND THE OTHER DIRECTION OF THE SAME CELL: this peer's OWN credential refusal must outrank its
    /// own earlier silence, or a peer that sat still and then failed the challenge would be reported
    /// as merely quiet.
    /// </summary>
    [Fact]
    public async Task A_silent_peer_that_later_fails_the_challenge_reports_the_credential_refusal()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(5))
        {
            AuthGrace = TimeSpan.FromMilliseconds(300),
            HeartbeatTimeout = TimeSpan.FromSeconds(30)
        };
        await connector.ConnectAsync();

        using var peer = await SilentAsync(pipe);
        await Wait(() => connector.Unauthenticated is not null);
        Assert.Contains("neither proved itself nor said", connector.StatusDetail!);

        var w = new StreamWriter(peer, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };
        var nonce = BridgePipeAuth.NewNonce();
        await w.WriteLineAsync(Json.Write(new
        {
            v = Versions.BridgeProtocolVersion,
            op = BridgePipeAuth.Challenge,
            data = new
            {
                nonce,
                proof = BridgePipeAuth.Proof(WrongSecret, BridgePipeAuth.BridgeRole, nonce)
            }
        }));

        await Wait(() => connector.StatusDetail?.Contains("could not prove") == true);
        Assert.DoesNotContain("neither proved itself nor said", connector.StatusDetail!);
    }


    /// <summary>
    /// Codex F38. A PEER THAT HAS AUTHENTICATED AND NOT SAID HELLO IS A STATE, NOT AN ABSENCE.
    ///
    /// The derived reading required <c>!_authenticated</c>, so a peer that proved itself and then
    /// went quiet produced NO reading at all — and with nothing of its own to report, the row fell
    /// back to the marker left by the peer BEFORE it and named a protocol mismatch belonging to a
    /// program that had already gone. The operator is sent to reinstall an add-on while the add-on
    /// that replaced it is sitting on the pipe waiting to be diagnosed.
    ///
    /// "No status" is not a state: a current connection always yields something newer than any
    /// marker, and for this one the honest sentence is that it is connected and its hello has not
    /// arrived.
    /// </summary>
    [Fact]
    public async Task An_authenticated_peer_that_has_not_said_hello_is_not_masked_by_the_previous_peers_refusal()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(5))
        {
            AuthGrace = TimeSpan.FromMilliseconds(300),
            HeartbeatTimeout = TimeSpan.FromSeconds(30)
        };
        await connector.ConnectAsync();

        // A peer speaking a protocol this build does not, refused and dropped.
        var (old, _) = await PeerAsync(pipe, goodProof: true, helloVersion: 2);
        await Wait(() => connector.Incompatible is not null);
        Assert.Contains("speaks protocol 2", connector.StatusDetail!);
        old.Dispose();

        // The replacement: it proves itself and then says nothing more.
        var (fresh, _w) = await PeerAsync(pipe, goodProof: true, helloVersion: null);
        using var _1 = fresh;

        await Wait(() => connector.StatusDetail?.Contains("has not said hello") == true);
        var row = connector.StatusDetail!;
        Assert.DoesNotContain("speaks protocol 2", row);
        Assert.Null(connector.Bridge);          // nothing it claimed unlocked anything
    }

    /// <summary>
    /// THE OTHER DIRECTION, without which the test above is satisfied by a row that says
    /// "waiting for the hello" for ever: the hello arrives, and the row goes quiet.
    /// </summary>
    [Fact]
    public async Task A_peer_that_says_a_compatible_hello_clears_the_row()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(5))
        {
            AuthGrace = TimeSpan.FromMilliseconds(300),
            HeartbeatTimeout = TimeSpan.FromSeconds(30)
        };
        await connector.ConnectAsync();

        var (peer, w) = await PeerAsync(pipe, goodProof: true, helloVersion: null);
        using var _1 = peer;
        await Wait(() => connector.StatusDetail?.Contains("has not said hello") == true);

        await w.WriteLineAsync(Json.Write(new BridgeFrame
        {
            Op = BridgeOps.Hello,
            Data = System.Text.Json.JsonSerializer.SerializeToElement(
                new BridgeHello
                {
                    BridgeProtocolVersion = Versions.BridgeProtocolVersion,
                    BridgeVersion = "0.1.1", AtasVersion = "6.1.2.3", AccountId = "ATAS-SIM"
                }, Json.Options)
        }));

        await Wait(() => connector.Bridge is not null);
        Assert.Null(connector.StatusDetail);
        Assert.Null(connector.Unauthenticated);
        Assert.Null(connector.Incompatible);
    }
}

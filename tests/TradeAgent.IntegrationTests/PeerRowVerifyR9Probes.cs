using System.IO.Pipes;
using System.Text;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Atas;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// ROUND-9 ADVERSARIAL VERIFY — targets 3 and 7 against `AtasConnector`.
///
/// Target 3: the row describes the peer that is there NOW. The builder's class-closure table claims
/// every PAIR of the three reportable states is ordered. Two of its six cells are argued rather than
/// tested — "explicit credential vs derived silence, ACROSS two connections" cites no test, and "a
/// silent peer that later speaks v2" (the bounce brief's third permutation) is not built anywhere.
/// Both are driven here.
///
/// Target 7: the heartbeat predicate on every turn — a legitimately quiet-but-beating bridge kept for
/// MINUTES at shipped values, which is the direction the round-9 change could have broken.
/// </summary>
public class PeerRowVerifyR9Probes
{
    static string NewPipe() => "ta-row9-" + Guid.NewGuid().ToString("n")[..12];

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

    // ------------------------------------------------------------------ target 7

    /// <summary>
    /// TARGET 7's LAST QUESTION — the direction the round-9 change could have broken. The predicate is
    /// now asked on EVERY turn of the read loop rather than only when the poll wins, so a bridge that
    /// is legitimately quiet must survive it for as long as it keeps beating. Shipped values: a 15 s
    /// timeout against 5 s beats, and NO other traffic at all, for over two minutes.
    /// </summary>
    [Fact]
    public async Task A_quiet_bridge_that_only_beats_is_kept_for_minutes_at_shipped_values()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(15), connector.HeartbeatTimeout);
        await connector.ConnectAsync();
        await using var _1 = connector;

        var hello = new BridgeHello
        {
            BridgeProtocolVersion = Versions.BridgeProtocolVersion,
            BridgeVersion = "0.1.1", AtasVersion = "6.1.2.3", AccountId = "ATAS-SIM"
        };
        await using var quiet = new StubBridge(pipe, hello);
        await quiet.ConnectAsync();
        await Wait(() => connector.Bridge is not null);

        // 26 beats at the shipped 5 s interval: 130 s, more than eight whole heartbeat windows.
        for (var beat = 0; beat < 26; beat++)
        {
            await Task.Delay(5_000);
            await quiet.Heartbeat(hello);
            Assert.NotNull(connector.Bridge);
        }

        Assert.Equal("0.1.1", connector.Bridge!.BridgeVersion);
        Assert.Equal(HealthState.READY, await connector.GetHealthAsync());
        Assert.Null(connector.StatusDetail);
    }

    /// <summary>
    /// AND THE HANDSHAKE IS NOT DROPPED BY THE NEW QUESTION. The predicate is asked after EVERY
    /// dispatched frame now, including the auth frame — which does not write `_lastHeartbeat`. If it
    /// were measured from the last beat alone, a peer arriving after a long gap would be dropped
    /// between its challenge and its hello. It is floored at the arrival instant instead; this is what
    /// says so.
    /// </summary>
    [Fact]
    public async Task A_peer_that_arrives_long_after_the_last_beat_still_completes_its_handshake()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10)) { HeartbeatTimeout = TimeSpan.FromSeconds(1) };
        await connector.ConnectAsync();
        await using var _1 = connector;

        // Three whole heartbeat windows in which nothing has ever connected: _lastHeartbeat is
        // DateTimeOffset.MinValue and stays there.
        await Task.Delay(3_000);

        var (peer, _) = await PeerAsync(pipe, goodProof: true, helloVersion: Versions.BridgeProtocolVersion);
        await Wait(() => connector.Bridge is not null, 5_000);
        Assert.Equal("0.0.9", connector.Bridge!.BridgeVersion);
        peer.Dispose();
    }
}

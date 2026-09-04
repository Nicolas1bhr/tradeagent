using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Atas;
using TradeAgent.Core;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// ADVERSARIAL VERIFY round 6, leg [2]. Liveness-as-answer, attacked from both sides.
/// Not proposed for the branch.
/// </summary>
public class VerifyR6Probes(ITestOutputHelper o)
{
    static string NewPipe() => "ta-vr6-" + Guid.NewGuid().ToString("n")[..12];
    static BridgeCredential Cred() => new(new string('a', 64), Environment.ProcessPath ?? "");

    static async Task Wait(Func<Task<bool>> c, int ms = 20_000)
    {
        var d = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < d) { if (await c()) return; await Task.Delay(25); }
        throw new TimeoutException("condition was not met in time");
    }

    /// <summary>
    /// R6P1 (target 1a). The wedged shape: reads NOTHING, but its heartbeat task keeps running —
    /// which is what a frozen ATAS read loop looks like, since BridgeServer.StartHeartbeat is its
    /// own Task.Run (BridgeServer.cs:251). Twelve randomised phases against the shipped 5 s beat.
    /// PASSES if any phase is kept.
    /// </summary>
    [Fact]
    public async Task R6P1_a_wedged_but_heartbeating_peer_is_dropped_at_every_phase()
    {
        var kept = 0;
        for (var i = 0; i < 12; i++)
        {
            var pipe = NewPipe();
            await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), Cred());
            Assert.Equal(TimeSpan.FromSeconds(2), connector.EmergencyDeadline);
            await connector.ConnectAsync();
            await using var peer = await Peer.Connect(pipe, Cred().Secret, PeerMode.HeartbeatOnly);
            await Wait(async () => await connector.IsConnectedAsync());
            await Task.Delay(Random.Shared.Next(100, 5100));   // random phase against the 5 s beat

            Exception? ex = null;
            var t = Stopwatch.StartNew();
            try { await connector.CancelAllOrdersAsync("ATAS-X"); } catch (Exception e) { ex = e; }
            t.Stop();
            await Task.Delay(150);
            var connected = await connector.IsConnectedAsync();
            if (connected) kept++;
            o.WriteLine($"  phase {i,2}: {(int)t.Elapsed.TotalMilliseconds,5} ms  connected={connected,-5} " +
                        $"beats={peer.FramesSent} bytes_read={peer.BytesRead}  " +
                        $"verdict={(ex!.Message.Contains("busy") ? "BUSY-kept" : "not-responding-dropped")}");
        }
        o.WriteLine($"KEPT = {kept} of 12  (round 5 measured 6 of 12 kept)");
        Assert.True(kept > 0, $"dropped at all 12 phases — the wedged peer is no longer kept");
    }

    /// <summary>
    /// R6P2 (target 1c, and the sharp edge of the new rule). A bridge that READS, ANSWERS, and is
    /// simply SLOW — it answers this very cancel-all at 2.5 s — on an otherwise QUIET connection.
    ///
    /// BridgeServer handles frames strictly sequentially (`while (…ReadLineAsync…) await
    /// HandleFrame(…)`, BridgeServer.cs:130-131), so nothing else can answer while it works on ours.
    /// `PeerAnsweredSince(startedAt)` therefore has nothing to observe. PASSES if a healthy bridge
    /// is dropped for being slow.
    /// </summary>
    [Theory]
    [InlineData(2500)]
    [InlineData(3500)]
    public async Task R6P2_a_healthy_but_slow_bridge_on_a_quiet_connection(int answerAfterMs)
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), Cred());
        await connector.ConnectAsync();
        await using var peer = await Peer.Connect(pipe, Cred().Secret, PeerMode.AnswersSlowly, answerAfterMs);
        await Wait(async () => await connector.IsConnectedAsync());
        await Task.Delay(200);

        var t = Stopwatch.StartNew();
        Exception? ex = null;
        try { await connector.CancelAllOrdersAsync("ATAS-X"); } catch (Exception e) { ex = e; }
        t.Stop();
        await Task.Delay(300);
        var connected = await connector.IsConnectedAsync();

        o.WriteLine($"answer_after={answerAfterMs} ms  elapsed={(int)t.Elapsed.TotalMilliseconds} ms");
        o.WriteLine($"   frames read by peer={peer.FramesRead}  answers sent={peer.FramesSent}");
        o.WriteLine($"   connected_after={connected}");
        o.WriteLine($"   msg={ex?.Message}");

        Assert.False(connected,
            $"a bridge that read our frame and answered it {answerAfterMs} ms later was KEPT — the new rule tolerates slow");
    }

    /// <summary>
    /// R6P3 (target 1d). Reads everything, answers nothing, does not even heartbeat.
    /// The stated new consequence. PASSES if it is kept.
    /// </summary>
    [Fact]
    public async Task R6P3_a_bridge_that_reads_and_never_answers_is_dropped()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), Cred());
        await connector.ConnectAsync();
        await using var peer = await Peer.Connect(pipe, Cred().Secret, PeerMode.ReadsButMute);
        await Wait(async () => await connector.IsConnectedAsync());
        await Task.Delay(200);

        var t = Stopwatch.StartNew();
        Exception? ex = null;
        try { await connector.CancelAllOrdersAsync("ATAS-X"); } catch (Exception e) { ex = e; }
        t.Stop();
        await Task.Delay(300);
        var connected = await connector.IsConnectedAsync();
        o.WriteLine($"elapsed={(int)t.Elapsed.TotalMilliseconds} ms  bytes_read={peer.BytesRead}  connected_after={connected}");
        o.WriteLine($"   msg={ex?.Message}");
        Assert.True(connected, "a reads-but-mute bridge was dropped — the stated new consequence holds");
    }

    /// <summary>
    /// R6P4 (target 1c, the direction that CAN keep). Concurrent traffic: the peer answers every
    /// frame except the cancel-all, and ordinary reads keep flowing across the window, so an answer
    /// really does arrive while the emergency waits. PASSES if that bridge is dropped.
    /// </summary>
    [Fact]
    public async Task R6P4_an_answer_to_some_other_rpc_inside_the_window_keeps_the_connection()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), Cred());
        await connector.ConnectAsync();
        await using var peer = await Peer.Connect(pipe, Cred().Secret, PeerMode.AnswersAllButCancelAll);
        await Wait(async () => await connector.IsConnectedAsync());

        using var stop = new CancellationTokenSource();
        var chatter = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { await connector.GetAccountsAsync(stop.Token); } catch (Exception) { }
                try { await Task.Delay(150, stop.Token); } catch (Exception) { return; }
            }
        });
        await Task.Delay(400);
        var answeredBefore = peer.FramesSent;

        var t = Stopwatch.StartNew();
        Exception? ex = null;
        try { await connector.CancelAllOrdersAsync("ATAS-X"); } catch (Exception e) { ex = e; }
        t.Stop();
        var answeredDuring = peer.FramesSent - answeredBefore;
        await stop.CancelAsync();
        try { await chatter; } catch (Exception) { }
        var connected = await connector.IsConnectedAsync();

        o.WriteLine($"elapsed={(int)t.Elapsed.TotalMilliseconds} ms  answers during the window={answeredDuring}");
        o.WriteLine($"   connected_after={connected}   msg={ex?.Message}");
        Assert.True(answeredDuring > 0, "FIXTURE: no answer arrived during the window, so this measured nothing");
        Assert.False(connected, "the answering bridge was KEPT — the keep direction holds");
    }

    public enum PeerMode { HeartbeatOnly, AnswersSlowly, ReadsButMute, AnswersAllButCancelAll }

    /// <summary>A peer whose single variable is what it does with the frames it is sent.</summary>
    public sealed class Peer : IAsyncDisposable
    {
        readonly NamedPipeClientStream _p;
        readonly CancellationTokenSource _stop = new();
        readonly List<Task> _tasks = [];
        PeerMode _mode;
        int _answerAfterMs;
        long _read, _sent, _framesRead;
        public long BytesRead => Interlocked.Read(ref _read);
        public long FramesSent => Interlocked.Read(ref _sent);
        public long FramesRead => Interlocked.Read(ref _framesRead);

        Peer(string pipe) => _p = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);

        public static async Task<Peer> Connect(string pipe, string secret, PeerMode mode, int answerAfterMs = 0)
        {
            var peer = new Peer(pipe) { _mode = mode, _answerAfterMs = answerAfterMs };
            await peer._p.ConnectAsync(10_000);
            var nonce = BridgePipeAuth.NewNonce();
            await peer.Write(new { v = Versions.BridgeProtocolVersion, op = BridgePipeAuth.Challenge,
                data = new { nonce, proof = BridgePipeAuth.Proof(secret, BridgePipeAuth.BridgeRole, nonce) } });
            var answer = Json.Read<BridgeFrame>(await peer.ReadLine())!;
            Assert.True(answer.Op == BridgePipeAuth.Response, $"handshake refused: {answer.Op} {answer.Error}");
            await peer.Write(new { v = Versions.BridgeProtocolVersion, op = BridgeOps.Hello,
                data = new BridgeHello { BridgeProtocolVersion = Versions.BridgeProtocolVersion,
                    AccountId = "ATAS-X", IsSimulated = true } });

            if (mode == PeerMode.HeartbeatOnly) peer._tasks.Add(Task.Run(peer.Beat));
            else peer._tasks.Add(Task.Run(peer.Serve));
            return peer;
        }

        async Task Beat()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await Task.Delay(5000, _stop.Token);          // the shipped HeartbeatInterval
                    await Write(new { v = Versions.BridgeProtocolVersion, op = BridgeOps.Heartbeat });
                    Interlocked.Increment(ref _sent);
                }
            }
            catch (Exception) { }
        }

        /// <summary>Reads every frame; what it does next is the experiment.</summary>
        async Task Serve()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var line = await ReadLine(_stop.Token);
                    Interlocked.Add(ref _read, line.Length);
                    Interlocked.Increment(ref _framesRead);
                    if (_mode == PeerMode.ReadsButMute) continue;

                    var f = Json.Read<BridgeFrame>(line);
                    if (f?.Id is null) continue;
                    if (_mode == PeerMode.AnswersAllButCancelAll && f.Op == BridgeOps.CancelAll) continue;
                    if (_mode == PeerMode.AnswersSlowly) await Task.Delay(_answerAfterMs, _stop.Token);

                    await Write(new { v = Versions.BridgeProtocolVersion, id = f.Id, ok = true,
                        data = JsonSerializer.SerializeToElement(Array.Empty<string>()) });
                    Interlocked.Increment(ref _sent);
                }
            }
            catch (Exception) { }
        }

        Task Write(object frame) => _p.WriteAsync(Encoding.UTF8.GetBytes(Json.Write(frame) + "\n")).AsTask();

        async Task<string> ReadLine(CancellationToken ct = default)
        {
            var buf = new byte[8192];
            var ms = new MemoryStream();
            while (true)
            {
                var n = ct == default
                    ? await _p.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(10))
                    : await _p.ReadAsync(buf, ct);
                if (n == 0) throw new IOException("closed");
                var nl = Array.IndexOf(buf, (byte)'\n', 0, n);
                if (nl >= 0) { ms.Write(buf, 0, nl); return Encoding.UTF8.GetString(ms.ToArray()); }
                ms.Write(buf, 0, n);
            }
        }

        /// <summary>Hold, cancel and AWAIT every background task before the handle goes — the
        /// round-6 Windows fixture rule, applied to my own fixture too.</summary>
        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            foreach (var t in _tasks) { try { await t; } catch (Exception) { } }
            await _p.DisposeAsync();
        }
    }
}

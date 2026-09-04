using System.IO.Pipes;
using System.Text;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Atas;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// VERIFIER ROUND 8, TARGET 9 — the F23 idle poll, both directions.
///
/// The drop is decided by `PeerHasGoneQuiet()` (`AtasConnector.cs:189`), which reads `_lastHeartbeat`
/// — a field written in exactly two places (`:557` an accepted hello, `:574` a heartbeat frame) and by
/// NOTHING else. But it is CONSULTED only when the idle poll wins the race against the pending read
/// (`:276-281`). Those are two different questions, so the guard has a reachability hole: a peer that
/// completes ANY line more often than `IdlePoll` never lets the poll win, and is therefore never asked
/// whether it has gone quiet.
/// </summary>
public class PipeLivenessVerifyR8Probes
{
    static string NewPipe() => "ta-live8-" + Guid.NewGuid().ToString("n")[..12];

    static async Task Wait(Func<bool> c, int ms)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline) { if (c()) return; await Task.Delay(50); }
        throw new TimeoutException("condition was not met in time");
    }

    /// <summary>Authenticates and says a compatible hello, then leaves the writer in the caller's hand.</summary>
    static async Task<(NamedPipeClientStream Client, StreamWriter W)> HandshakeAsync(string pipe)
    {
        var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(10_000);
        var w = new StreamWriter(client, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };
        var r = new StreamReader(client, new UTF8Encoding(false), false, 8192, leaveOpen: true);

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
                    BridgeProtocolVersion = Versions.BridgeProtocolVersion,
                    BridgeVersion = "0.1.1", AtasVersion = "6.1.2.3"
                }, Json.Options)
        }));
        return (client, w);
    }

    /// <summary>
    /// THE CONTROL — the builder's own case, in my harness: a peer that says nothing at all after the
    /// handshake IS dropped and the instance recycles.
    /// </summary>
    [Fact]
    public async Task CONTROL_a_silent_peer_is_dropped_and_a_second_bridge_gets_in()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10)) { HeartbeatTimeout = TimeSpan.FromSeconds(1) };
        await connector.ConnectAsync();
        await using var _1 = connector;

        var (silent, _) = await HandshakeAsync(pipe);
        await Wait(() => connector.Bridge is not null, 10_000);

        await Task.Delay(3_000);                      // three windows of silence

        await using var live = new StubBridge(pipe, new BridgeHello
        {
            BridgeProtocolVersion = Versions.BridgeProtocolVersion,
            BridgeVersion = "0.1.2", AtasVersion = "6.1.2.3", AccountId = "ATAS-SIM"
        });
        await live.ConnectAsync();                    // ONE dial, no retry helper
        await Wait(() => connector.Bridge?.BridgeVersion == "0.1.2", 10_000);
        silent.Dispose();
    }

    /// <summary>
    /// THE PROBE — the same peer, dribbling one meaningless frame faster than the idle poll. It never
    /// sends a heartbeat, so `_lastHeartbeat` is frozen at the handshake and the row goes stale; but
    /// the poll never wins the race, so `PeerHasGoneQuiet()` is never asked and the peer keeps the only
    /// server instance there is. This is F23's own harm, in a peer that writes a newline now and then.
    /// </summary>
    [Fact]
    public async Task A_peer_that_dribbles_any_frame_keeps_the_only_pipe_instance()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10)) { HeartbeatTimeout = TimeSpan.FromSeconds(1) };
        await connector.ConnectAsync();
        await using var _1 = connector;

        var (dribbler, w) = await HandshakeAsync(pipe);
        await Wait(() => connector.Bridge is not null, 10_000);

        // IdlePoll here is 333 ms. One frame every 200 ms, for five heartbeat windows.
        using var stop = new CancellationTokenSource();
        var noise = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await w.WriteLineAsync(Json.Write(new { v = Versions.BridgeProtocolVersion, op = "ping" }));
                    await Task.Delay(200, stop.Token);
                }
            }
            catch (Exception) { }
        });

        await Task.Delay(5_000);                      // five heartbeat windows, no heartbeat sent

        // The health row already says the peer is not there. If the drop worked, the instance would
        // have recycled and this one dial would get in.
        var health = await connector.GetHealthAsync();
        var live = new StubBridge(pipe, new BridgeHello
        {
            BridgeProtocolVersion = Versions.BridgeProtocolVersion,
            BridgeVersion = "0.1.2", AtasVersion = "6.1.2.3", AccountId = "ATAS-SIM"
        });
        var connected = true;
        try { await live.ConnectAsync(); }
        catch (Exception) { connected = false; }

        stop.Cancel();
        await noise;
        try { await live.DisposeAsync(); } catch (Exception) { }
        dribbler.Dispose();

        Assert.True(connected && connector.Bridge?.BridgeVersion == "0.1.2",
            $"a dribbling peer with a frozen heartbeat held the single pipe instance: health={health}, "
            + $"connected={connected}, bridge={connector.Bridge?.BridgeVersion ?? "<none>"}, "
            + $"detail={connector.StatusDetail ?? "<none>"}");
    }

    /// <summary>
    /// TARGET 9's FIRST HALF, MEASURED AT SHIPPED VALUES — what keeps a legitimately quiet bridge
    /// alive is its heartbeat and nothing else: `HeartbeatTimeout` 15 s against
    /// `BridgeServer.HeartbeatInterval` 5 s, a 3x margin, and order traffic is irrelevant to it.
    /// No orders, no quotes, no events for three windows.
    /// </summary>
    [Fact]
    public async Task A_quiet_bridge_that_only_beats_is_not_dropped_at_shipped_values()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));   // HeartbeatTimeout = 15 s, shipped
        Assert.Equal(TimeSpan.FromSeconds(15), connector.HeartbeatTimeout);
        await connector.ConnectAsync();
        await using var _1 = connector;

        await using var quiet = new StubBridge(pipe, new BridgeHello
        {
            BridgeProtocolVersion = Versions.BridgeProtocolVersion,
            BridgeVersion = "0.1.1", AtasVersion = "6.1.2.3", AccountId = "ATAS-SIM"
        });
        await quiet.ConnectAsync();
        await Wait(() => connector.Bridge is not null, 10_000);

        // Nine beats at the shipped 5 s interval: 45 s, three whole heartbeat windows, no other traffic.
        for (var beat = 0; beat < 9; beat++)
        {
            await Task.Delay(5_000);
            await quiet.Heartbeat(new BridgeHello
            {
                BridgeProtocolVersion = Versions.BridgeProtocolVersion,
                BridgeVersion = "0.1.1", AtasVersion = "6.1.2.3", AccountId = "ATAS-SIM"
            });
        }

        Assert.Equal("0.1.1", connector.Bridge!.BridgeVersion);
        Assert.Equal(HealthState.READY, await connector.GetHealthAsync());
    }

    /// <summary>
    /// TARGET 9's SECOND HALF — does the poll race lose or duplicate a frame? Every gap between frames
    /// is longer than `IdlePoll`, so the poll wakes empty between every one of them and the pending
    /// read is carried across. Twelve order events, each with a distinct identifier.
    /// </summary>
    [Fact]
    public async Task The_idle_poll_neither_loses_nor_duplicates_a_frame_across_its_wakeups()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10)) { HeartbeatTimeout = TimeSpan.FromSeconds(2) };
        var seen = new List<string>();
        connector.OrderChanged += o => { lock (seen) seen.Add(o.ConnectorOrderId); };
        await connector.ConnectAsync();
        await using var _1 = connector;

        await using var bridge = new StubBridge(pipe, new BridgeHello
        {
            BridgeProtocolVersion = Versions.BridgeProtocolVersion,
            BridgeVersion = "0.1.1", AtasVersion = "6.1.2.3", AccountId = "ATAS-SIM"
        });
        await bridge.ConnectAsync();
        await Wait(() => connector.Bridge is not null, 10_000);

        // IdlePoll here is 666 ms; the gap is 800 ms, so a poll wakes empty inside every gap.
        for (var i = 0; i < 12; i++)
        {
            await Task.Delay(800);
            await bridge.Heartbeat(new BridgeHello
            {
                BridgeProtocolVersion = Versions.BridgeProtocolVersion,
                BridgeVersion = "0.1.1", AtasVersion = "6.1.2.3", AccountId = "ATAS-SIM"
            });
            await bridge.RaiseEvent(BridgeEvents.Order, new OrderInfo(
                $"ORD-{i:D2}", $"TA-{i:D2}", "ATAS-SIM", "ES", OrderSide.Buy, OrderType.Limit,
                1m, 0m, 4200m, null, ExecutionState.WORKING, null, DateTimeOffset.UtcNow));
        }

        await Wait(() => { lock (seen) return seen.Count >= 12; }, 10_000);
        lock (seen)
        {
            Assert.Equal(12, seen.Count);
            Assert.Equal(12, seen.Distinct().Count());
        }
    }
}

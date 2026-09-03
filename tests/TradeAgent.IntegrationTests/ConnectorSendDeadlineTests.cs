using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using TradeAgent.AtasBridge;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Atas;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using TradeAgent.TradeCli;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// The connector's own send path against a bridge that stops reading.
///
/// bbcd36e gave <see cref="BridgeServer.SendRaw"/> a write deadline after the bridge froze on
/// Windows. This is the SAME defect facing the other way, and it was left in place: the connector
/// writes an RPC through <c>WriteLineAsync</c> with no deadline, and the RPC timeout it does have
/// only starts once that write has returned. So the timeout that is supposed to bound an order
/// cannot bound the part of it that hangs.
///
/// It is worse than one stuck order. Every writer shares <c>_sendGate</c>, and the gate is taken
/// before the write, so one peer that stops reading parks the first caller in the write and every
/// caller after it on the semaphore — a cancel, a close, a cancel-all. The frames a person reaches
/// for when something has gone wrong are exactly the ones queued behind the frame that is stuck.
///
/// WHY THE VOLUME. On macOS the pipe is a Unix socket with about 16 KiB of kernel buffer, so a
/// single small RPC lands and the stall is invisible; on Windows the same stall is available on far
/// less. Enough ordinary RPCs to overrun the buffer is the shape that shows it on both, and it is
/// not a contrived one: a gateway reconciling against a bridge that has stopped reading does this
/// by itself.
/// </summary>
public class ConnectorSendDeadlineTests
{
    static string NewPipe() => "ta-csd-" + Guid.NewGuid().ToString("n")[..12];

    /// <summary>
    /// Arithmetic, not measured: an <c>accounts</c> frame is about 55 bytes on the wire, so 2000 of
    /// them is roughly 110 KB against the ~16 KiB the macOS kernel will hold for a reader that never
    /// comes. Deliberately far past it rather than near it — this test must fail because the write
    /// has no deadline, never because the buffer happened to be a size nobody measured.
    /// </summary>
    const int Calls = 2000;

    /// <summary>Long enough that a working deadline (1s) plus a working RPC timeout (1s) fits several times over.</summary>
    static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    static BridgeCredential Cred() => new(new string('a', 64), Environment.ProcessPath ?? "");

    /// <summary>
    /// An order that cannot land must come back as a TRANSPORT failure and never as a rejection.
    /// Safety rule 3 on <c>IAtasAdapter</c>: a definite broker refusal is the only thing allowed to
    /// read as definite, because "this order does not exist" has to be provable to be worth saying.
    /// A write that is still sitting in a socket is the most indefinite state there is.
    /// </summary>
    [Fact]
    public async Task Rpcs_to_a_bridge_that_stopped_reading_end_rather_than_hang()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(1), Cred())
        {
            WriteTimeout = TimeSpan.FromSeconds(1)
        };
        await connector.ConnectAsync();

        await using var peer = await BridgePeer.Stalled(pipe, Cred().Secret);
        await Wait(async () => await connector.IsConnectedAsync());

        // From here the peer reads nothing at all.
        var timer = Stopwatch.StartNew();
        var calls = Enumerable.Range(0, Calls).Select(_ => connector.GetAccountsAsync()).ToArray();

        var finished = true;
        try { await Task.WhenAll(calls).WaitAsync(Bound); }
        catch (TimeoutException) { finished = false; }
        catch (Exception) { /* every call failing is the expected outcome; the shape is asserted below */ }
        timer.Stop();

        var stuck = calls.Count(c => !c.IsCompleted);
        Assert.True(finished,
            $"{stuck} of {Calls} calls never finished within {Bound.TotalSeconds:0}s against a bridge that stopped reading " +
            "— an order with no deadline on its write, and every frame behind it stuck on the same gate");

        Assert.All(calls, c =>
        {
            Assert.True(c.IsFaulted, "a call to a bridge that never read anything reported success");
            var ex = c.Exception!.InnerException;
            Assert.True(ex is ConnectorTransportException,
                $"a frame that may or may not have reached ATAS surfaced as {ex?.GetType().Name}, " +
                "which the gateway would read as definite");
        });

        // AND THE SENTENCE THE ORDINARY PeerStalled BRANCH PRODUCES, which nothing read until now.
        // The caller holding the gate when the write deadline fires is the one that gets it, so it
        // is deterministic rather than incidental. Round 4's verifier swapped the two ordinary
        // sentences (mutant M14) — telling a merely busy bridge it had stopped reading and a dead
        // one that the connection was still up — and the whole suite stayed green. That is the same
        // class 9e50559 fixed on the emergency path: a healthy peer libelled as dead, one branch
        // over, with nobody reading the words.
        var messages = calls.Where(c => c.IsFaulted).Select(c => c.Exception!.InnerException!.Message).ToList();
        Assert.Contains(messages, m => m.Contains("did not read") && m.Contains("accounts"));
    }

    /// <summary>
    /// ONE order, ONE writer, and nothing queued behind it — so only the deadline ON THE WRITE can
    /// end this. It is the case the volume test above does NOT cover: with many callers, the SECOND
    /// one timing out on the send gate is enough to close the pipe and free the first, so the write
    /// deadline can be deleted and that test still passes. It is deleted here and this one hangs.
    ///
    /// The order is large because it carries a large comment, which is free text the agent writes.
    /// That is a legal order, and one frame of it is bigger than the socket buffer, so this single
    /// write is the whole demonstration.
    /// </summary>
    [Fact]
    public async Task One_order_larger_than_the_buffer_still_ends_when_nothing_is_queued_behind_it()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(1), Cred())
        {
            WriteTimeout = TimeSpan.FromSeconds(1)
        };
        await connector.ConnectAsync();

        await using var peer = await BridgePeer.Stalled(pipe, Cred().Secret);
        await Wait(async () => await connector.IsConnectedAsync());

        // Arithmetic, not measured: 64 KiB of comment is four times the ~16 KiB the macOS kernel
        // holds for a reader that never comes, so this frame cannot land in the buffer alone.
        var order = new PlaceOrderCommand("TA-stall-1", "ATAS-STALLED", "ES", OrderSide.Buy,
            OrderType.Market, 1m, null, null, TimeInForce.Day, new string('c', 64 * 1024));

        var call = connector.PlaceOrderAsync(order);
        var ended = await Task.WhenAny(call, Task.Delay(Bound)) == call;
        Observe([call]);

        Assert.True(ended,
            $"a single order to a bridge that stopped reading never came back within {Bound.TotalSeconds:0}s — " +
            "the RPC timeout cannot help, because it does not start until the write returns");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => call);
        Assert.True(ex is ConnectorTransportException,
            $"an order that may or may not have reached ATAS surfaced as {ex.GetType().Name}");
    }

    /// <summary>
    /// Shutdown does not wait on a stalled writer either. <c>DisposeAsync</c> runs when TradeAgent
    /// closes, and a peer that has stopped reading must not be able to hold the app open.
    /// </summary>
    [Fact]
    public async Task Disposing_the_connector_does_not_wait_on_a_bridge_that_stopped_reading()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(1), Cred())
        {
            WriteTimeout = TimeSpan.FromSeconds(1)
        };
        await connector.ConnectAsync();

        await using var peer = await BridgePeer.Stalled(pipe, Cred().Secret);
        await Wait(async () => await connector.IsConnectedAsync());

        var flooding = Enumerable.Range(0, Calls).Select(_ => connector.GetAccountsAsync()).ToArray();
        Observe(flooding);

        var timer = Stopwatch.StartNew();
        await connector.DisposeAsync();
        timer.Stop();

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10),
            $"DisposeAsync took {timer.Elapsed.TotalSeconds:0.0}s against a bridge that stopped reading");
    }

    /// <summary>
    /// The other direction, and the one a careless deadline breaks: a real bridge that reads its
    /// frames is still answered, and the answers are still correct. A deadline short enough to bite
    /// a healthy peer, or a drop on the wrong branch, fails here and passes everything above.
    /// </summary>
    [Fact]
    public async Task A_bridge_that_reads_is_still_answered()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        await connector.ConnectAsync();
        var adapter = new LoopbackAtasAdapter();
        await using var bridge = new BridgeServer(adapter, pipe);
        bridge.Start();
        await Wait(async () => await connector.IsConnectedAsync());

        // Several in a row, so the gate is genuinely handed on rather than merely taken once.
        for (var i = 0; i < 25; i++)
            Assert.NotEmpty(await connector.GetAccountsAsync().WaitAsync(TimeSpan.FromSeconds(10)));

        var quote = await connector.GetQuoteAsync("ES").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("ES", quote!.Symbol);
    }

    /// <summary>
    /// A CALLER GIVING UP MID-WRITE MUST NOT LEAVE THE CONNECTOR WEDGED.
    ///
    /// Found by review of a0aa1a7. The wait was cancellable but the write was not, so a cancelled
    /// caller released the send gate with its frame still going into a StreamWriter every other
    /// caller shares. The next caller then interleaved with a half-written frame, and the connector
    /// sat there with Connected still true and no reconnect, failing every later frame for ever.
    ///
    /// Latent in the shipped product — only shutdown and connector-swap tokens reach this path today
    /// — which is exactly why it is worth a test now rather than after something else reaches it.
    /// The write state is unknown, so the connection ends the way a timeout ends it.
    /// </summary>
    [Fact]
    public async Task A_caller_cancelling_mid_write_drops_the_connection_instead_of_wedging_it()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(1), Cred())
        {
            WriteTimeout = TimeSpan.FromSeconds(30)   // long, so cancellation gets there first
        };
        await connector.ConnectAsync();

        await using var peer = await BridgePeer.Stalled(pipe, Cred().Secret);
        await Wait(async () => await connector.IsConnectedAsync());

        // One order big enough that it cannot land in the socket buffer, then the caller gives up.
        var order = new PlaceOrderCommand("TA-cancel-1", "ATAS-STALLED", "ES", OrderSide.Buy,
            OrderType.Market, 1m, null, null, TimeInForce.Day, new string('c', 64 * 1024));
        using var cts = new CancellationTokenSource();
        var call = connector.PlaceOrderAsync(order, cts.Token);
        await Task.Delay(300);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => call);

        // The connection is GONE, not merely reported as unhappy. A connector that still calls
        // itself connected here is the wedged state: nothing reconnects, every frame fails.
        await Wait(async () => !await connector.IsConnectedAsync(), 5_000);
        Assert.False(await connector.IsConnectedAsync());
    }

    /// <summary>
    /// OUR OWN SEND QUEUE IS NOT THE PEER'S FAULT.
    ///
    /// Found by review of a0aa1a7 (Codex). The deadline started before the send gate was acquired,
    /// so it timed this process's backlog as well as the bridge's reading: enough concurrent RPCs
    /// and a perfectly healthy, actively-reading bridge was declared stalled and disconnected.
    ///
    /// A real <see cref="BridgeServer"/> on the other end, a deliberately tiny deadline, and enough
    /// concurrent traffic that callers must queue behind each other. Some of those calls are
    /// allowed to fail — they are UNKNOWN to their own caller, which is honest — but the CONNECTION
    /// must survive, and the bridge must still answer afterwards.
    /// </summary>
    [Fact]
    public async Task Local_queueing_under_load_does_not_disconnect_a_healthy_bridge()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(5))
        {
            WriteTimeout = TimeSpan.FromMilliseconds(50)   // tiny on purpose: the gate WILL be contended
        };
        await connector.ConnectAsync();
        var adapter = new LoopbackAtasAdapter();
        await using var bridge = new BridgeServer(adapter, pipe);
        bridge.Start();
        await Wait(async () => await connector.IsConnectedAsync());

        // Big frames on purpose. Small ones land in the socket buffer in microseconds and never
        // contend the gate at all, so the test would pass without ever exercising the thing it is
        // about — verified: with small frames the mutant that drops on gate expiry survived this.
        var fat = new string('s', 128 * 1024);
        var calls = Enumerable.Range(0, 300).Select(_ => connector.GetQuoteAsync(fat)).ToArray();
        try { await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (Exception) { /* individual callers may time out; the connection is what is on trial */ }
        Observe(calls);

        // The contention has to be REAL for the rest of this to mean anything: if nothing ever hit
        // the bound, the connection surviving proves only that nothing happened.
        Assert.Contains(calls, c => c.IsFaulted);

        // AND THE SENTENCE THE ORDINARY Busy BRANCH PRODUCES. Instrumentation in the round-4 verify
        // counted this branch entered 1015 times in one run of this class with no test reading what
        // it says, which is how mutant M14 (the two ordinary sentences swapped) survived the whole
        // suite. The words matter for the same reason they matter on the emergency path: one of
        // them sends a person to look at a dead bridge and the other tells them to wait.
        var busy = calls.Where(c => c.IsFaulted).Select(c => c.Exception!.InnerException!.Message).ToList();
        Assert.Contains(busy, m => m.Contains("to be sent and was not") && m.Contains("still up"));
        Assert.DoesNotContain(busy, m => m.Contains("did not read"));

        Assert.True(await connector.IsConnectedAsync(),
            "a bridge that was reading everything was disconnected because THIS process queued its own frames");
        Assert.NotNull(await connector.GetQuoteAsync("ES").WaitAsync(TimeSpan.FromSeconds(10)));
    }

    /// <summary>
    /// THE DEADLINES THESE TESTS REASON ABOUT ARE THE ONES THE PRODUCT SHIPS.
    ///
    /// Every other test in this unit sets a short deadline so it can run in seconds. That is fine
    /// only while the shipped default is what the reasoning assumes — and nothing was checking. This
    /// pins the three of them by name, so changing a default breaks a test instead of silently
    /// invalidating every duration quoted in the build record.
    /// </summary>
    [Fact]
    public void The_deadlines_these_tests_reason_about_are_the_ones_the_product_ships()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), new AtasConnector(NewPipe()).WriteTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), new BridgeServer(new LoopbackAtasAdapter(), NewPipe()).WriteTimeout);

        using var db = TestEnv.NewDb();
        var gw = new TradingGateway(db, new Connectors.Fake.FakeConnector(new Connectors.Fake.FakeBroker()), new HealthRegistry());
        Assert.Equal(TimeSpan.FromSeconds(10), new GatewayPipeServer(gw, "tok", NewPipe()).WriteTimeout);
    }

    /// <summary>
    /// AN EMERGENCY DOES NOT WAIT TEN SECONDS. At shipped defaults, nothing shortened.
    ///
    /// Measured before this change: a cancel-all queued behind one stalled write took 9.76 s, and for
    /// all of it the owner had a screen that said nothing while trying to stop. Emergency operations
    /// now get two seconds for the send gate and then take the connection down, so the answer — even
    /// though it is a bad answer — arrives while a person is still looking at it.
    /// </summary>
    [Fact]
    public async Task An_emergency_cancel_all_behind_a_stalled_write_fails_fast_and_says_why()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), Cred());   // all deadlines shipped
        Assert.Equal(TimeSpan.FromSeconds(2), connector.EmergencyDeadline);
        await connector.ConnectAsync();

        await using var peer = await BridgePeer.Stalled(pipe, Cred().Secret);
        await Wait(async () => await connector.IsConnectedAsync());

        var stuck = connector.PlaceOrderAsync(new PlaceOrderCommand("TA-emerg-1", "ATAS-STALLED", "ES",
            OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, new string('c', 128 * 1024)));
        Observe([stuck]);
        await Task.Delay(250);

        var timer = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => connector.CancelAllOrdersAsync("ATAS-STALLED"));
        timer.Stop();

        Assert.True(ex is ConnectorTransportException, $"an emergency cancel surfaced as {ex.GetType().Name}");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(6),
            $"the emergency cancel-all took {timer.Elapsed.TotalSeconds:0.00}s — it is still queueing behind the stalled write");

        // A sentence the owner can act on, not a stack trace.
        Assert.Contains("not responding", ex.Message);
        Assert.Contains("NOT confirmed", ex.Message);
        Assert.Contains("ATAS", ex.Message);

        // And the stalled connection really is gone, which is what frees the gate and starts the retry.
        await Wait(async () => !await connector.IsConnectedAsync(), 5_000);
    }

    /// <summary>
    /// THE SAME ACT GETS THE SAME URGENCY WHOEVER ASKED.
    ///
    /// The first version of the fast path keyed on the operator's own button, so the agent's
    /// cancel-all — which the gateway sweeps into per-order <c>Cancel</c> legs — fell through to the
    /// full deadline. Measured: 9707 ms per agent leg against 2006 ms for the button, and the legs
    /// run in sequence, so an agent cancelling N orders through a stalled bridge waited ~10N seconds
    /// to be told nothing.
    ///
    /// EACH ONE ALONE, on its own stalled bridge, and that is the point of the shape. The first
    /// version of this test fired both at once: the button expired at two seconds and dropped the
    /// connection, which freed the leg — so the leg looked fast while still being classified as
    /// ordinary, and reverting the classification did not fail anything. Measured by mutation, not
    /// by reading it.
    /// </summary>
    [Theory]
    [InlineData("leg")]      // BridgeOps.Cancel — one order of an agent's cancel-all sweep
    [InlineData("button")]   // BridgeOps.CancelAll — the operator's own control
    public async Task A_cancellation_fails_fast_on_a_stalled_bridge_whoever_issued_it(string caller)
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), Cred());   // shipped deadlines
        await connector.ConnectAsync();

        await using var peer = await BridgePeer.Stalled(pipe, Cred().Secret);
        await Wait(async () => await connector.IsConnectedAsync());

        var stuck = connector.PlaceOrderAsync(new PlaceOrderCommand("TA-intent-1", "ATAS-STALLED", "ES",
            OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, new string('c', 128 * 1024)));
        Observe([stuck]);
        await Task.Delay(250);

        var timer = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => caller == "leg"
            ? connector.CancelOrderAsync("FB-1")
            : connector.CancelAllOrdersAsync("ATAS-STALLED"));
        timer.Stop();

        Assert.True(ex is ConnectorTransportException,
            $"a cancellation of unknown outcome surfaced as {ex.GetType().Name}");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(6),
            $"the '{caller}' cancellation took {timer.Elapsed.TotalSeconds:0.00}s behind a stalled write — it is still on the full deadline");
        Assert.Contains("NOT confirmed", ex.Message);
    }

    /// <summary>
    /// A BUSY BRIDGE IS NOT A DEAD ONE, and an emergency must not say it is.
    ///
    /// The first version of the fast path dropped unconditionally on gate expiry and told the owner
    /// the bridge was not responding — which was false whenever the bridge was reading everything
    /// and the queue was ours. Reproduced by the review: 1500 concurrent 900 KiB RPCs and one
    /// cancel-all returned in 2.01 s having disconnected a bridge that was perfectly healthy.
    ///
    /// THE MIRROR OF THE TEST ABOVE, ONE VARIABLE CHANGED: this peer READS. Shipped deadlines, one
    /// oversized write holding the send gate, one emergency queued behind it — identical in every
    /// other respect — so the only thing that can move the verdict from "not responding, dropped"
    /// to "busy, still up" is the single question the connector asks on gate expiry: did the writer
    /// holding the gate get anywhere while we waited.
    ///
    /// WHY THE FIXTURE IS A PACED READER AND NOT A PILE OF TRAFFIC. The first version of this test
    /// fired 400 concurrent 512 KiB RPCs at a real BridgeServer and assumed they would still be
    /// draining two seconds later. That assumption is a bound in the WRONG DIRECTION — it needs the
    /// machine to be slow enough — and it does not hold here. Measured on this box, 2026-09-03:
    /// 73 of the 400 already finished at 312 ms, all 400 at about 1.02 s, and the cancel-all took
    /// the gate at 0.71 s and returned SENT, so the expiry branch this test exists to pin was never
    /// reached and the test failed on ThrowsAny with no exception thrown. A peer that accepts at
    /// most 8 KiB every 200 ms bounds the drain from BELOW instead — 40 KiB/s is a wall-clock
    /// ceiling a faster box cannot beat, it can only reach the next sleep sooner. Measured with that
    /// pace, same day: the 512 KiB order's last byte was accepted at 12.95 s, against the 2 s the
    /// emergency waits. The gate is still held, and still moving, for the whole of that window.
    ///
    /// The emergency still fails: its frame was never sent, so its outcome is honestly unknown. But
    /// it must say BUSY, and it must leave the connection up so the retry it advises has somewhere
    /// to go.
    /// </summary>
    [Fact]
    public async Task An_emergency_behind_a_busy_but_healthy_bridge_says_busy_and_does_not_drop_it()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), Cred());   // all deadlines shipped
        Assert.Equal(TimeSpan.FromSeconds(2), connector.EmergencyDeadline);
        await connector.ConnectAsync();

        // The far end is never the problem in this test: it accepts every byte it is offered. It
        // just does not accept them quickly, which is what keeps OUR write holding the gate.
        await using var peer = await BridgePeer.ReadingSlowly(pipe, Cred().Secret);
        await Wait(async () => await connector.IsConnectedAsync());

        // 512 KiB against a 40 KiB/s ceiling: over ten seconds of writing, against two of waiting.
        var stuck = connector.PlaceOrderAsync(new PlaceOrderCommand("TA-busy-1", "ATAS-READING", "ES",
            OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, new string('c', 512 * 1024)));
        Observe([stuck]);

        // Wait for the write to be genuinely in flight instead of sleeping a guessed interval. The
        // handshake is a few hundred bytes, so 32 KiB accepted can only be the order going out.
        await Wait(() => Task.FromResult(peer.BytesRead >= 32 * 1024));
        var acceptedBefore = peer.BytesRead;

        var timer = Stopwatch.StartNew();
        Exception? ex = null;
        try { await connector.CancelAllOrdersAsync("ATAS-READING"); }
        catch (Exception e) { ex = e; }
        timer.Stop();
        var acceptedDuring = peer.BytesRead - acceptedBefore;

        // THE FIXTURE'S OWN PREMISE, ASSERTED FIRST AND SEPARATELY FROM THE VERDICT.
        //
        // Both of these are conditions this test CREATES, and neither is a claim about the product.
        // Without them a fixture that failed to contend the gate reports a product defect instead of
        // reporting itself, which is exactly what the 400-RPC version did: it came back in 0.71 s
        // off a free gate and said "no exception was thrown". Deliberately NOT asserted on the
        // in-flight write's own state — a drop on the wrong branch ends that write too, so it would
        // diagnose a real product defect as a broken fixture.
        Assert.True(timer.Elapsed >= connector.EmergencyDeadline - TimeSpan.FromMilliseconds(100),
            $"the emergency came back in {timer.Elapsed.TotalSeconds:0.00}s, short of the {connector.EmergencyDeadline.TotalSeconds:0}s emergency deadline — " +
            "it was never queued behind anything, so this run measured nothing about gate EXPIRY");
        Assert.True(acceptedDuring > 0,
            "the peer accepted no bytes while the emergency waited — that is the STALLED case, not the busy one this test is about");

        Assert.NotNull(ex);
        Assert.True(ex is ConnectorTransportException, $"surfaced as {ex.GetType().Name}");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(6),
            $"the emergency took {timer.Elapsed.TotalSeconds:0.00}s");

        // The honest sentence, and NOT the one that sends a person hunting a dead bridge.
        Assert.Contains("busy", ex.Message);
        Assert.Contains("NOT confirmed", ex.Message);
        Assert.DoesNotContain("not responding", ex.Message);

        // And the bridge is still there, which is what makes "try again" advice worth giving.
        Assert.True(await connector.IsConnectedAsync(),
            "a bridge that was reading everything we sent it was disconnected by an emergency");
    }

    /// <summary>
    /// A PEER MOVING SLOWER THAN ONE WRITE CHUNK PER EMERGENCY WINDOW IS STILL MOVING.
    ///
    /// Codex F4 on d25dbb4, and it is the same class as the defect round 4 fixed: progress was
    /// recorded only when a whole 8 KiB `WriteAsync` completed, so the signal's RESOLUTION was the
    /// chunk size. A peer accepting 1 KiB every 400 ms is reading continuously and would finish an
    /// 8 KiB chunk inside the ordinary 10 s write budget — but it completes NO chunk inside the 2 s
    /// an emergency waits, so the emergency read "no chunk finished" as "the bridge has stopped"
    /// and dropped a healthy connection. The busy fixture could not see this: it accepts a whole
    /// 8 KiB every 200 ms, which is comfortably one chunk per window.
    ///
    /// MEASURED, because Codex's own numbers land on the safe side here and the finding is real one
    /// step further down. A drain sweep against the 8 KiB chunk, 2026-09-03, macOS Unix socket:
    ///
    ///   2.50 KiB/s (1 KiB/400 ms) → 5120 B accepted in the window → busy, kept
    ///   1.25 KiB/s (1 KiB/800 ms) → 2048 B accepted in the window → NOT RESPONDING, DROPPED
    ///   0.63 KiB/s (1 KiB/1600 ms) → 1024 B accepted in the window → NOT RESPONDING, DROPPED
    ///
    /// A peer that took two kilobytes off us while we watched, and was still reading when we hung
    /// up on it, told the owner it had stopped responding.
    ///
    /// The arithmetic, stated rather than left implicit: the chunk size IS the resolution, so a peer
    /// slower than one chunk per <see cref="AtasConnector.EmergencyDeadline"/> is misread. At 8 KiB
    /// that boundary is 4 KiB/s; at 1 KiB it is 512 B/s. It cannot be removed, only moved — a peer
    /// slow enough is indistinguishable from a dead one inside two seconds, and round 4 took that
    /// trade deliberately. What is not acceptable is a boundary an ordinary slow reader sits on the
    /// wrong side of.
    /// </summary>
    [Fact]
    public async Task A_peer_reading_below_one_chunk_per_window_is_busy_and_not_dropped()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), Cred());   // shipped
        await connector.ConnectAsync();

        // 1 KiB every 800 ms: 1.25 KiB/s, measured above as the first rate on the WRONG side of the
        // 8 KiB boundary — ~2 KiB accepted while the emergency waits, and no chunk completed.
        await using var peer = await BridgePeer.ReadingSlowly(pipe, Cred().Secret, 1024, TimeSpan.FromMilliseconds(800));
        await Wait(async () => await connector.IsConnectedAsync());

        var stuck = connector.PlaceOrderAsync(new PlaceOrderCommand("TA-subchunk-1", "ATAS-READING", "ES",
            OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, new string('c', 512 * 1024)));
        Observe([stuck]);
        await Wait(() => Task.FromResult(peer.BytesRead >= 4 * 1024));
        var acceptedBefore = peer.BytesRead;

        var timer = Stopwatch.StartNew();
        Exception? ex = null;
        try { await connector.CancelAllOrdersAsync("ATAS-READING"); }
        catch (Exception e) { ex = e; }
        timer.Stop();
        var acceptedDuring = peer.BytesRead - acceptedBefore;

        // The premise: it really was accepting bytes throughout, and really did make the emergency
        // wait out its deadline.
        Assert.True(timer.Elapsed >= connector.EmergencyDeadline - TimeSpan.FromMilliseconds(100),
            $"the emergency came back in {timer.Elapsed.TotalSeconds:0.00}s — it was not queued behind anything");
        Assert.True(acceptedDuring > 0,
            "the peer accepted nothing while the emergency waited, so this is the stalled case and measures nothing");
        Assert.True(acceptedDuring < 8 * 1024,
            $"the peer accepted {acceptedDuring} bytes — a whole chunk or more, so the sub-chunk boundary was never walked");

        Assert.NotNull(ex);
        Assert.Contains("busy", ex.Message);
        Assert.DoesNotContain("not responding", ex.Message);
        Assert.True(await connector.IsConnectedAsync(),
            "a bridge accepting bytes throughout was dropped because no whole chunk finished in the window");
    }

    /// <summary>
    /// AN EMERGENCY IS TWO SECONDS OF WAITING, NOT TWO SECONDS OF QUEUEING.
    ///
    /// Verifier V2 on d25dbb4, and it is the shape f518251 was written for in its most likely real
    /// form: ATAS frozen, the owner presses stop, NOTHING ELSE IN FLIGHT. The gate is free, so the
    /// emergency's ~100-byte frame lands in the socket buffer, the write returns Sent, and the
    /// caller then served the ORDINARY ten-second reply timeout. Measured: 10005 ms, the generic
    /// "ATAS did not answer 'cancel-all' within 10s" with no instruction in it, and the dead
    /// connection left UP so the reconnect that would restore service never started. Five times the
    /// wait the two seconds exist to prevent, on the path the feature exists for — and no test in
    /// the unit reached it, because every emergency test parked a 128 KiB write first.
    ///
    /// Nothing is parked here. That is the entire fixture.
    /// </summary>
    [Fact]
    public async Task An_emergency_on_an_idle_stalled_bridge_answers_in_two_seconds_and_drops_it()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), Cred());   // all deadlines shipped
        Assert.Equal(TimeSpan.FromSeconds(2), connector.EmergencyDeadline);
        await connector.ConnectAsync();

        await using var peer = await BridgePeer.Stalled(pipe, Cred().Secret);
        await Wait(async () => await connector.IsConnectedAsync());

        var timer = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => connector.CancelAllOrdersAsync("ATAS-STALLED"));
        timer.Stop();

        Assert.True(ex is ConnectorTransportException, $"surfaced as {ex.GetType().Name}");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(6),
            $"the emergency took {timer.Elapsed.TotalSeconds:0.00}s with a FREE gate — the deadline is still only on the queue");

        // The sentence an owner can act on, not the one written for a log.
        Assert.Contains("not responding", ex.Message);
        Assert.Contains("NOT confirmed", ex.Message);
        Assert.Contains("ATAS", ex.Message);
        Assert.DoesNotContain("did not answer", ex.Message);

        // And the dead connection is gone, which is what starts the redial the message promises.
        await Wait(async () => !await connector.IsConnectedAsync(), 5_000);
    }

    /// <summary>
    /// THE OTHER DIRECTION, AND THE ONE A CARELESS DEADLINE BREAKS: a bridge that answers is
    /// answered. Shortening the emergency's reply wait to what is left of two seconds must not make
    /// a working emergency fail — the whole point of the fast path is that stop WORKS.
    /// </summary>
    [Fact]
    public async Task An_emergency_against_a_healthy_bridge_still_gets_its_answer()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        await connector.ConnectAsync();
        var adapter = new LoopbackAtasAdapter();
        await using var bridge = new BridgeServer(adapter, pipe);
        bridge.Start();
        await Wait(async () => await connector.IsConnectedAsync());

        var timer = Stopwatch.StartNew();
        var cancelled = await connector.CancelAllOrdersAsync("ATAS-LOOPBACK").WaitAsync(TimeSpan.FromSeconds(10));
        timer.Stop();

        Assert.NotNull(cancelled);
        Assert.True(timer.Elapsed < connector.EmergencyDeadline,
            $"an emergency against a healthy bridge took {timer.Elapsed.TotalSeconds:0.00}s");
        Assert.True(await connector.IsConnectedAsync());
    }

    /// <summary>
    /// A BRIDGE THAT IS ALIVE BUT NOT ANSWERING THIS ONE OPERATION IS NOT A DEAD BRIDGE.
    ///
    /// The round-4 busy/stalled distinction, applied AFTER the wire as well as before it. This peer
    /// reads everything we send and heartbeats throughout; it simply never answers the cancel-all.
    /// The caller still fails at the deadline and still reports UNKNOWN — its frame went out and
    /// was not acknowledged, which is the most indefinite state there is — but the connection is
    /// left up, because dropping something that is plainly running costs a reconnect and buys
    /// nothing.
    ///
    /// The keep-signal is ANY frame, not a heartbeat specifically: the bridge heartbeats every 5 s,
    /// so a healthy connection is routinely silent for longer than an emergency waits, and a rule
    /// that needed a heartbeat inside the window would drop healthy bridges.
    /// </summary>
    [Fact]
    public async Task An_emergency_a_live_bridge_does_not_answer_is_unknown_but_not_a_drop()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), Cred());
        await connector.ConnectAsync();

        await using var peer = await BridgePeer.ReadingAndHeartbeating(pipe, Cred().Secret);
        await Wait(async () => await connector.IsConnectedAsync());

        var timer = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => connector.CancelAllOrdersAsync("ATAS-BEATING"));
        timer.Stop();

        Assert.True(ex is ConnectorTransportException, $"surfaced as {ex.GetType().Name}");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(6),
            $"the emergency took {timer.Elapsed.TotalSeconds:0.00}s");
        Assert.Contains("busy", ex.Message);
        Assert.Contains("NOT confirmed", ex.Message);
        Assert.DoesNotContain("not responding", ex.Message);
        Assert.True(await connector.IsConnectedAsync(),
            "a bridge that was reading everything and heartbeating throughout was dropped");
    }

    /// <summary>
    /// THE AGENT'S CANCEL-ALL, THROUGH THE REAL GATEWAY, WHICH IS WHERE THE TIME WAS ACTUALLY GOING.
    ///
    /// Codex F11 on d25dbb4. The test that claimed to measure "the agent's sweep leg" called
    /// <c>AtasConnector.CancelOrderAsync</c> directly and skipped everything the gateway does first:
    /// a real cancel-all reads the working orders (an ordinary <c>orders</c> RPC), and each leg then
    /// resolves its target by reading orders again, before the two-second emergency frame it was
    /// hurrying to send ever gets a turn. At shipped deadlines the prerequisite read alone served
    /// the full ten seconds — so the measured 2002 ms was real and was measuring the wrong thing.
    ///
    /// This one goes over the IPC pipe, through <see cref="GatewayPipeServer"/> and
    /// <see cref="TradingGateway"/>, onto a real <see cref="AtasConnector"/> whose bridge has
    /// stopped reading, with one 128 KiB write already holding the connector's send gate — so the
    /// prerequisite read has to queue for that gate exactly as the cancel frame would.
    /// </summary>
    [Fact]
    public async Task An_agent_cancel_all_through_the_real_gateway_fails_fast_on_a_stalled_bridge()
    {
        var bridgePipe = NewPipe();
        await using var connector = new AtasConnector(bridgePipe, TimeSpan.FromSeconds(10), Cred());   // shipped
        await connector.ConnectAsync();
        await using var peer = await BridgePeer.Stalled(bridgePipe, Cred().Secret);
        await Wait(async () => await connector.IsConnectedAsync());

        using var db = TestEnv.NewDb();
        var gw = new TradingGateway(db, connector, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = "ATAS-STALLED";   // so the ONE prerequisite read is the orders list
        });

        var ipcPipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), ipcPipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, ipcPipe);

        var stuck = connector.PlaceOrderAsync(new PlaceOrderCommand("TA-f11-hold", "ATAS-STALLED", "ES",
            OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, new string('c', 128 * 1024)));
        Observe([stuck]);
        await Task.Delay(250);

        var timer = Stopwatch.StartNew();
        var reply = await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "f11-sweep" })
            .WaitAsync(TimeSpan.FromSeconds(40));
        timer.Stop();

        Assert.False(reply.Ok, "the sweep reported success against a bridge that has read nothing");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(6),
            $"cancel-all through the real gateway took {timer.Elapsed.TotalSeconds:0.00}s — the prerequisite orders read is still on the ordinary deadline");

        // And it is the emergency's own sentence that comes back, not a generic transport failure:
        // the read inherited the operation's urgency, so it also inherited its words.
        Assert.Contains("NOT confirmed", reply.Error!.Message);
    }

    /// <summary>
    /// The other half of the decision: ORDINARY traffic keeps the full deadline. A quote arriving
    /// late costs nothing, and a caller that is merely queued has no business tearing down a
    /// connection. Shipped values, so this one really does take about ten seconds.
    ///
    /// THE HOLDER READS, and that is a correction rather than a detail. With a stalled holder this
    /// test never reached a gate-expiry branch at all: the holder's own write deadline fired at 10 s
    /// and dropped the connection, which FREED the ordinary caller, so what it measured was the
    /// holder's drop and what it read was the generic "could not reach the ATAS bridge" wrapper —
    /// not the classification under test (round-4 verify, F3, instrumented). A holder that keeps
    /// reading outlives the ordinary caller's own 10 s gate wait, so the caller expires on ITS OWN
    /// bound and the sentence asserted below is the one its own branch produced.
    /// </summary>
    [Theory]
    [InlineData("read")]           // a quote arriving late costs nothing
    [InlineData("place")]          // and an order that OPENS risk has no claim on an emergency path at all
    [InlineData("place-in-scope")] // not even nested inside a risk-reducing operation (see below)
    public async Task An_ordinary_op_behind_a_stalled_write_still_gets_the_full_deadline(string kind)
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), Cred());
        await connector.ConnectAsync();

        await using var peer = await BridgePeer.ReadingSlowly(pipe, Cred().Secret);
        await Wait(async () => await connector.IsConnectedAsync());

        var stuck = connector.PlaceOrderAsync(new PlaceOrderCommand("TA-ordinary-1", "ATAS-READING", "ES",
            OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, new string('c', 512 * 1024)));
        Observe([stuck]);
        await Wait(() => Task.FromResult(peer.BytesRead >= 32 * 1024));

        // "place-in-scope" is the guard on the ambient deadline added for F11. That scope exists so
        // the READS a cancel-all or a close must do first stop being served the ordinary ten
        // seconds — and the gateway implements close as a PLACE of an offsetting order, so without
        // an explicit exclusion at the connector an agent close-all would acquire the emergency
        // deadline for its orders through the back door. Carrying intent that far is F5's decision
        // and another unit's file; it must not happen here by accident.
        using var scope = kind == "place-in-scope" ? RiskReducingScope.Begin() : null;

        var timer = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => kind == "read"
            ? connector.GetAccountsAsync()
            : connector.PlaceOrderAsync(new PlaceOrderCommand("TA-ordinary-2", "ATAS-READING", "ES",
                OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, null)));
        timer.Stop();

        Assert.True(ex is ConnectorTransportException);
        Assert.True(timer.Elapsed > TimeSpan.FromSeconds(5),
            $"an ordinary '{kind}' gave up after {timer.Elapsed.TotalSeconds:0.00}s — it took the emergency path it is not entitled to");
        Assert.DoesNotContain("NOT confirmed", ex.Message);

        // The branch it actually reached, in its own words: queued behind our own traffic, so the
        // connection is untouched. Asserting the sentence is what makes the swap mutant fail here
        // rather than only in the load test.
        Assert.Contains("to be sent and was not", ex.Message);
        Assert.Contains("still up", ex.Message);
        Assert.True(await connector.IsConnectedAsync(),
            "an ordinary caller expiring on the send gate took the connection down with it");
    }

    // ---------------------------------------------------------------- helpers

    static void Observe(IEnumerable<Task> tasks)
    {
        foreach (var t in tasks) _ = t.ContinueWith(x => _ = x.Exception, TaskScheduler.Default);
    }

    static async Task Wait(Func<Task<bool>> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("condition was not met in time");
    }

    /// <summary>
    /// A peer that authenticates as this installation's bridge, says a compatible hello so the
    /// connector marks itself connected, and then either never reads another byte or reads
    /// everything at a pace it is given.
    ///
    /// It authenticates for real. Everything the tests measure happens on the far side of a
    /// handshake the connector accepted, which is the point: this is not an impostor being refused,
    /// it is the legitimate bridge — either stopped (a suspended ATAS, a blocked strategy thread, a
    /// machine that went to sleep) or merely slow.
    ///
    /// THE TWO FACTORIES ARE THE EXPERIMENT. <see cref="Stalled"/> and <see cref="ReadingSlowly"/>
    /// differ in exactly one thing — whether bytes are accepted — and that is the one question the
    /// emergency path asks before it decides to drop a connection or keep it. Anything else that
    /// differed between the two fixtures would be a second explanation for a different verdict.
    /// </summary>
    sealed class BridgePeer : IAsyncDisposable
    {
        /// <summary>
        /// The paced reader's ceiling: at most <see cref="PaceBytes"/> accepted, then this long doing
        /// nothing. 8 KiB per 200 ms is 40 KiB/s AT MOST, and it is a WALL-CLOCK bound — a faster
        /// machine cannot beat it, it can only arrive at the delay sooner. That direction is the
        /// whole point (see <see cref="ReadingSlowly"/>).
        /// </summary>
        static readonly TimeSpan Pace = TimeSpan.FromMilliseconds(200);
        const int PaceBytes = 8192;

        readonly NamedPipeClientStream _p;
        readonly CancellationTokenSource _stop = new();
        long _read;

        BridgePeer(string pipe) =>
            _p = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);

        /// <summary>
        /// Bytes accepted since the handshake. This is the fact the connector's emergency path keys
        /// on — <c>_lastWriteProgressAt</c> moves only when the peer takes bytes — so a test can
        /// assert the CONDITION it claims to have created rather than assuming it.
        /// </summary>
        public long BytesRead => Interlocked.Read(ref _read);

        /// <summary>Handshakes, then never reads another byte.</summary>
        public static Task<BridgePeer> Stalled(string pipe, string secret) =>
            ConnectAndSayHello(pipe, secret, "ATAS-STALLED", null, PaceBytes);

        /// <summary>
        /// Reads everything as fast as it arrives and heartbeats every 250 ms, but answers no RPC.
        ///
        /// A bridge whose ATAS side is wedged while its pipe side is perfectly alive. It is the
        /// case that separates "not responding" from "busy" once the frame has already gone out:
        /// nothing is coming back for THIS operation, but something over there is plainly running.
        /// </summary>
        public static async Task<BridgePeer> ReadingAndHeartbeating(string pipe, string secret)
        {
            var peer = await ConnectAndSayHello(pipe, secret, "ATAS-BEATING", TimeSpan.Zero, PaceBytes);
            _ = Task.Run(peer.Heartbeats);
            return peer;
        }

        async Task Heartbeats()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await Task.Delay(250, _stop.Token);
                    await WriteAsync(new { v = Versions.BridgeProtocolVersion, op = BridgeOps.Heartbeat });
                }
            }
            catch (Exception) { /* the test ending is how this always ends */ }
        }

        /// <summary>
        /// Handshakes, then reads everything it is offered — slowly, at <see cref="Pace"/>.
        ///
        /// WHY A PACED READER AND NOT A PILE OF TRAFFIC. To exercise gate EXPIRY the send gate has
        /// to still be held two seconds from now, and a fixture that gets there by queueing enough
        /// work needs the machine to be SLOW ENOUGH — a bound in the wrong direction, which is
        /// exactly how the first version of the busy test came to pass when it was written and fail
        /// on a quieter box. A reader that sleeps between reads bounds the drain from BELOW instead:
        /// no machine can make it finish sooner, so "still writing, still moving" is a guarantee.
        /// </summary>
        public static Task<BridgePeer> ReadingSlowly(string pipe, string secret) =>
            ConnectAndSayHello(pipe, secret, "ATAS-READING", Pace, PaceBytes);

        /// <summary>
        /// The same, at a pace the caller names. Used to walk the CHUNK BOUNDARY: a peer that
        /// accepts less than one of our write chunks per emergency window is progressing, and was
        /// being read as stopped.
        /// </summary>
        public static Task<BridgePeer> ReadingSlowly(string pipe, string secret, int bytes, TimeSpan pace) =>
            ConnectAndSayHello(pipe, secret, "ATAS-READING", pace, bytes);

        static async Task<BridgePeer> ConnectAndSayHello(string pipe, string secret, string accountId, TimeSpan? pace, int bytes)
        {
            var peer = new BridgePeer(pipe);
            await peer._p.ConnectAsync(10_000);

            var nonce = BridgePipeAuth.NewNonce();
            await peer.WriteAsync(new
            {
                v = Versions.BridgeProtocolVersion,
                op = BridgePipeAuth.Challenge,
                data = new { nonce, proof = BridgePipeAuth.Proof(secret, BridgePipeAuth.BridgeRole, nonce) }
            });

            var answer = Json.Read<BridgeFrame>(await peer.ReadLineAsync(TimeSpan.FromSeconds(10)))!;
            Assert.True(answer.Op == BridgePipeAuth.Response,
                $"the connector did not accept the handshake: {answer.Op} {answer.Error}");

            await peer.WriteAsync(new
            {
                v = Versions.BridgeProtocolVersion,
                op = BridgeOps.Hello,
                data = new BridgeHello
                {
                    BridgeProtocolVersion = Versions.BridgeProtocolVersion,
                    AccountId = accountId,
                    IsSimulated = true
                }
            });

            // Started only AFTER the handshake, so BytesRead counts nothing but the traffic a test
            // put on the wire itself.
            if (pace is { } p) _ = Task.Run(() => peer.Pump(p, bytes));
            return peer;
        }

        async Task Pump(TimeSpan pace, int bytes)
        {
            var buf = new byte[bytes];
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var n = await _p.ReadAsync(buf, _stop.Token);
                    if (n == 0) return;
                    Interlocked.Add(ref _read, n);
                    await Task.Delay(pace, _stop.Token);
                }
            }
            catch (Exception)
            {
                // The connector closing the pipe, or the test finishing, is how this always ends.
            }
        }

        Task WriteAsync(object frame) =>
            _p.WriteAsync(Encoding.UTF8.GetBytes(Json.Write(frame) + "\n")).AsTask();

        async Task<string> ReadLineAsync(TimeSpan bound)
        {
            var buf = new byte[8192];
            var ms = new MemoryStream();
            while (true)
            {
                var n = await _p.ReadAsync(buf).AsTask().WaitAsync(bound);
                if (n == 0) throw new IOException("the connector closed the connection before the line ended");
                var nl = Array.IndexOf(buf, (byte)'\n', 0, n);
                if (nl >= 0) { ms.Write(buf, 0, nl); return Encoding.UTF8.GetString(ms.ToArray()); }
                ms.Write(buf, 0, n);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _p.DisposeAsync();
        }
    }
}

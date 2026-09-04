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
        Assert.Equal(TimeSpan.FromSeconds(30), new AtasConnector(NewPipe()).FrameTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), new AtasConnector(NewPipe()).EmergencyDeadline);
        Assert.Equal(TimeSpan.FromSeconds(10), new BridgeServer(new LoopbackAtasAdapter(), NewPipe()).WriteTimeout);

        using var db = TestEnv.NewDb();
        var gw = new TradingGateway(db, new Connectors.Fake.FakeConnector(new Connectors.Fake.FakeBroker()), new HealthRegistry());
        Assert.Equal(TimeSpan.FromSeconds(10), new GatewayPipeServer(gw, "tok", NewPipe()).WriteTimeout);
    }

    /// <summary>
    /// A DEADLINE THAT HAS PASSED HAS NOTHING LEFT — NOT ONE MILLISECOND.
    ///
    /// Codex round-8 F4. The connector's <c>Left</c> handed a caller past its absolute deadline a
    /// fresh millisecond, borrowing the "never zero" rule from <c>Remaining</c>, which is a
    /// RELATIVE budget and keeps it for a reason of its own. On an absolute deadline the
    /// millisecond is a race the gate or the reply can win after the instant the operation promised
    /// to be over — and a millisecond is in any case too short to measure anything, which is the
    /// same argument that already makes a leg reached after the deadline fail BEFORE the send gate
    /// instead of queueing for its millisecond and judging the bridge on what moved in it.
    ///
    /// Pinned as arithmetic because that is what it is: the annotation is erased, the wait is
    /// milliseconds, and no end-to-end timing can tell one millisecond from zero. The behaviour
    /// that depends on it is asserted by the rest of this class, which still passes.
    /// </summary>
    [Fact]
    public void An_absolute_deadline_that_has_passed_leaves_nothing_not_a_millisecond()
    {
        var now = Environment.TickCount64;

        Assert.Equal(TimeSpan.Zero, RiskReducingScope.LeftUntil(now - 5_000));
        Assert.Equal(TimeSpan.Zero, RiskReducingScope.LeftUntil(now - 1));
        Assert.Equal(TimeSpan.Zero, RiskReducingScope.LeftUntil(now));

        // The other direction, so a mutant that returns zero for everything is not a passing one.
        var ahead = RiskReducingScope.LeftUntil(now + 5_000);
        Assert.InRange(ahead, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5));

        // And it is the scope's own deadline this is about: a budget of nothing is already spent.
        using (RiskReducingScope.Begin(TimeSpan.Zero))
            Assert.Equal(TimeSpan.Zero, RiskReducingScope.LeftUntil(RiskReducingScope.DeadlineAt!.Value));
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
    /// AN EMERGENCY IS TWO SECONDS OF WAITING, NOT TWO SECONDS OF QUEUEING — AND NOT TWO SECONDS OF
    /// EVIDENCE ABOUT THE CONNECTION EITHER.
    ///
    /// Verifier V2 on d25dbb4 was the first half: with the gate FREE — a frozen ATAS, the owner
    /// presses stop, nothing else in flight — the ~100-byte frame landed in the socket buffer, the
    /// write returned Sent, and the caller then served the ORDINARY ten-second reply timeout.
    /// Measured: 10005 ms and the generic "ATAS did not answer" sentence. That is fixed and this
    /// test still pins it: the caller is answered in about two seconds, with the owner's words.
    ///
    /// THE SECOND HALF IS ROUND 7 (F-E) AND IT IS THE PART THAT MOVED. Rounds 6 dropped the
    /// connection on the caller's clock, at two seconds, which is a judgement the caller's clock
    /// cannot support: `BridgeServer` handles frames one at a time, so a bridge in the middle of a
    /// slow synchronous ATAS call has our frame in hand and can emit nothing at all — silence at two
    /// seconds is what a BUSY bridge looks like as well as a dead one. So the connection is judged
    /// on the deadline this system already uses for "did not answer", `_timeout`, and this peer —
    /// which reads nothing and says nothing, ever — is dropped when that runs out.
    ///
    /// Both clocks are asserted: the caller at ~2 s, still connected a second later, dropped by the
    /// grace.
    /// </summary>
    [Fact]
    public async Task An_emergency_on_an_idle_stalled_bridge_answers_in_two_seconds_and_drops_it_at_the_grace()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), Cred());   // all deadlines shipped
        Assert.Equal(TimeSpan.FromSeconds(2), connector.EmergencyDeadline);
        await connector.ConnectAsync();

        await using var peer = await BridgePeer.Stalled(pipe, Cred().Secret);
        await Wait(async () => await connector.IsConnectedAsync());

        var timer = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => connector.CancelAllOrdersAsync("ATAS-STALLED"));
        var caller = timer.Elapsed;

        Assert.True(ex is ConnectorTransportException, $"surfaced as {ex.GetType().Name}");
        Assert.InRange(caller, TimeSpan.FromMilliseconds(1900), TimeSpan.FromSeconds(6));
        Assert.Contains("NOT confirmed", ex.Message);
        Assert.Contains("ATAS", ex.Message);
        Assert.DoesNotContain("did not answer", ex.Message);

        // At two seconds nothing is known that would justify a teardown, and nothing has been torn
        // down. This assertion is the whole of F-E: it fails on round 6's rule.
        await Task.Delay(1000);
        Assert.True(await connector.IsConnectedAsync(),
            $"the connection was dropped on the caller's clock, at {caller.TotalSeconds:0.00}s — a bridge working on a slow ATAS call looks exactly like this");

        // And then the grace runs out, and it goes.
        await Wait(async () => !await connector.IsConnectedAsync(), 15_000);
        Assert.InRange(timer.Elapsed, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// A BRIDGE THAT ANSWERS LATE IS A BRIDGE, AND ITS ANSWER IS NOT THROWN AWAY.
    ///
    /// Verifier finding F-E, measured: a peer that had read our frame and answered it at 2500 ms or
    /// 3500 ms was disconnected at ~2000 ms and told the owner it was not responding. `frames read by
    /// peer=1` — it was working on us. The cause is documented in this repo rather than
    /// hypothesised: `BridgeServer` handles frames strictly sequentially, and `BridgeProtocol.cs`
    /// records that the obsolete synchronous ATAS call sites "cannot be given a deadline, so a block
    /// inside one wedges the bridge's frame loop". A >2 s synchronous call is a state this unit
    /// already expects.
    ///
    /// The caller is still answered at two seconds — that bound is untouched, and asserted here. The
    /// connection survives, and the late answer is DELIVERED rather than dropped on the floor:
    /// keeping a connection because the bridge answered, and then discarding the answer, would be
    /// incoherent. Whether the gateway settles a request on one is U2c-1's to decide.
    /// </summary>
    [Theory]
    [InlineData(2500)]
    [InlineData(3500)]
    public async Task An_emergency_a_bridge_answers_late_keeps_it_and_records_the_answer(int answerAfterMs)
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), Cred());   // shipped
        await connector.ConnectAsync();

        await using var peer = await BridgePeer.AnsweringAfter(
            pipe, Cred().Secret, TimeSpan.FromMilliseconds(answerAfterMs));
        await Wait(async () => await connector.IsConnectedAsync());

        var timer = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => connector.CancelAllOrdersAsync("ATAS-ANSWERING"));
        var caller = timer.Elapsed;

        // The caller's bound is unchanged, which is half the decision.
        Assert.InRange(caller, TimeSpan.FromMilliseconds(1900), TimeSpan.FromSeconds(6));
        Assert.Contains("busy", ex.Message);
        Assert.DoesNotContain("not responding", ex.Message);

        // OUTCOME FIRST (verifier F-G): the first clause is what happened to the order and where to
        // look, not a claim about the pipe. After the grace change this sentence is what every
        // emergency reads at two seconds, including one against a bridge that is already dead.
        Assert.StartsWith("'cancel-all' is NOT confirmed — check your positions and orders in ATAS.", ex.Message);

        // The answer arrives after the caller has gone, and is recorded rather than discarded.
        await Wait(() => Task.FromResult(connector.LateAnswers > 0), 15_000);
        Assert.Equal(1, connector.LateAnswers);

        // And nothing is left registered. An entry is removed by the answer, by the race check or by
        // the grace expiring; one that only ever grew would be the leak Codex F3 named.
        await Wait(() => Task.FromResult(connector.AwaitingLateAnswer == 0), 15_000);

        // And the connection is still there once the grace it would have been judged by has passed,
        // which is the other half.
        await Wait(() => Task.FromResult(timer.Elapsed > TimeSpan.FromSeconds(11)), 20_000);
        Assert.True(await connector.IsConnectedAsync(),
            $"a bridge that answered at {answerAfterMs} ms was disconnected anyway");
    }

    /// <summary>
    /// A GRACE THAT ENDS BECAUSE THE BRIDGE WENT AWAY STILL HAS TO CLEAR UP AFTER ITSELF.
    ///
    /// Codex round-8 F2, and its own check. When the caller gives up at two seconds the request is
    /// parked in <c>_abandoned</c> and the connection's verdict is deferred to the grace. The waiter
    /// removed the entry on the TIMEOUT path only — and both of the other ways that wait can end
    /// took the same exit. <c>Drop</c> faults every pending request, so a disconnect during the
    /// grace, and disposal, each made the waiter return without removing anything: the id stayed in
    /// the dictionary for the life of the process and <c>AwaitingLateAnswer</c> never came back to
    /// zero.
    ///
    /// It is a leak of a few dozen bytes per abandoned emergency, which is why it is LOW. What makes
    /// it worth closing is what the number is FOR: it is the only external evidence that the
    /// deferred verdict cleans up, so a counter that can stick at one for a reason nobody intended
    /// stops being able to prove anything about the ones that do.
    /// </summary>
    [Theory]
    [InlineData("the bridge disconnects")]
    [InlineData("the connector is disposed")]
    public async Task Nothing_is_left_awaiting_a_late_answer_when_the_grace_ends_early(string how)
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), Cred());   // shipped grace
        await using var _1 = connector;
        await connector.ConnectAsync();

        // Reads everything, heartbeats, answers nothing: the frame goes OUT and is never answered,
        // which is the only shape that reaches the grace at all.
        var peer = await BridgePeer.ReadingAndHeartbeating(pipe, Cred().Secret);
        await using var _2 = peer;
        await Wait(async () => await connector.IsConnectedAsync());

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => connector.CancelAllOrdersAsync("ATAS-GRACE-END"));
        Assert.Contains("NOT confirmed", ex.Message);

        // The premise: it really is parked, so what follows is about the exit and not about a
        // request that was never registered.
        Assert.Equal(1, connector.AwaitingLateAnswer);

        // Now end the grace early, the two ways it can end early.
        if (how == "the bridge disconnects")
        {
            await peer.DisposeAsync();
            await Wait(async () => !await connector.IsConnectedAsync(), 15_000);
        }
        else
        {
            await connector.DisposeAsync();
        }

        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (connector.AwaitingLateAnswer != 0 && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(connector.AwaitingLateAnswer == 0,
            $"{connector.AwaitingLateAnswer} request(s) still awaiting a late answer after {how} — " +
            "the waiter took an exit that removes nothing, so the entry is there for good");
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
    /// A BRIDGE THAT IS ANSWERING IS NOT A DEAD BRIDGE, EVEN WHEN THIS ONE OPERATION IS LATE.
    ///
    /// The keep half of the round-4 busy/stalled distinction, applied after the wire — and rewritten
    /// in round 6 around the signal that actually carries it. It used to use a peer that read
    /// everything and heartbeated but answered nothing, which passed for the wrong reason: the rule
    /// then kept ANY peer that had sent a frame, heartbeats included, and that is verifier finding
    /// F-B — a wedged ATAS beats while its read loop is frozen, so the verdict was a coin flip on
    /// heartbeat phase.
    ///
    /// So the fixture now demonstrates the thing the verdict is about. This peer reads every frame
    /// and answers all of them EXCEPT the cancel-all, and the test keeps ordinary traffic flowing
    /// across the window so answers really are arriving while the emergency waits. The caller still
    /// fails and still reports UNKNOWN — its frame went out and was not acknowledged — but the
    /// connection stays up, because a bridge that is returning answers is one the advised retry can
    /// actually run on.
    /// </summary>
    [Fact]
    public async Task An_emergency_a_busy_bridge_has_not_answered_yet_is_unknown_but_not_a_drop()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), Cred());
        await connector.ConnectAsync();

        await using var peer = await BridgePeer.AnsweringAllBut(pipe, Cred().Secret, BridgeOps.CancelAll);
        await Wait(async () => await connector.IsConnectedAsync());

        // Ordinary traffic that IS answered, for as long as the emergency waits. Without it the peer
        // is merely capable of answering; with it, it demonstrably is.
        using var chatter = new CancellationTokenSource();
        var answered = 0;
        var stop = 0;
        var talking = Task.Run(async () =>
        {
            // NOT `chatter.Token` on the request itself. Cancelling a request whose write is in
            // flight drops the connection by design, so passing the teardown's token to the chatter
            // would let the cleanup destroy the very state this test is about.
            while (Volatile.Read(ref stop) == 0)
            {
                try { await connector.GetAccountsAsync(); Interlocked.Increment(ref answered); }
                catch (Exception) { /* the emergency's own failure must not end the chatter */ }
                try { await Task.Delay(150, chatter.Token); } catch (Exception) { return; }
            }
        });

        await Wait(() => Task.FromResult(Volatile.Read(ref answered) > 0));
        var answeredBefore = Volatile.Read(ref answered);

        var timer = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => connector.CancelAllOrdersAsync("ATAS-ANSWERING"));
        timer.Stop();

        // THE VERDICT IS READ HERE, BEFORE THIS TEST TOUCHES ANYTHING — and that is a correction,
        // not a tidy-up. What is on trial is what the EMERGENCY did to the connection, and the
        // teardown below can end it by itself: cancelling the chatter while one of its writes is in
        // flight takes `WriteFrame`'s deliberate "a half-written frame on a shared writer ends the
        // connection" path. Measured on this connector: cancelling an RPC after the peer had taken
        // 24576 bytes of it drops the connection every time. Read after the teardown, this assertion
        // was a race between the emergency's verdict and the test's own cleanup — one the Mac
        // usually wins and a loaded Windows box does not, which is where it was caught (round 10's
        // box run: "a bridge that was answering requests throughout was dropped", passing alone two
        // and a half minutes later on the same binaries).
        var connectedAtTheVerdict = await connector.IsConnectedAsync();

        // And the chatter is stopped by a FLAG rather than by cancelling a request in flight, so the
        // teardown cannot drop the connection at all. Only the sleep between requests is cancelled.
        Volatile.Write(ref stop, 1);
        await chatter.CancelAsync();
        try { await talking; } catch (Exception) { /* torn down with the test */ }

        // The premise: answers really were coming back while the emergency was outstanding.
        Assert.True(Volatile.Read(ref answered) > answeredBefore,
            "no request was answered while the emergency waited, so this is the wedged case and not the busy one");

        Assert.True(ex is ConnectorTransportException, $"surfaced as {ex.GetType().Name}");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(6),
            $"the emergency took {timer.Elapsed.TotalSeconds:0.00}s");
        Assert.Contains("busy", ex.Message);
        Assert.Contains("NOT confirmed", ex.Message);
        Assert.DoesNotContain("not responding", ex.Message);
        Assert.True(connectedAtTheVerdict,
            "a bridge that was answering requests throughout was dropped");
    }

    /// <summary>
    /// A BRIDGE THAT TALKS BUT DOES NOT LISTEN IS NOT ALIVE IN THE DIRECTION THAT MATTERS.
    ///
    /// Verifier finding F-B: the rule kept the connection whenever ANY frame had arrived, and a
    /// heartbeat is a frame — but `BridgeServer.StartHeartbeat` runs on its own `Task.Run`,
    /// independent of the frame read loop a freeze wedges, so a wedged ATAS beats over a connection
    /// that consumes nothing. Measured at the shipped 5 s interval: KEPT in 6 of 12 runs, the verdict
    /// decided by heartbeat phase. A coin flip on whether stop works.
    ///
    /// Twelve phases across that interval, and the answer has to be the same for all of them. Since
    /// round 7 the grace is `_timeout` rather than `EmergencyDeadline`, so every phase now has at
    /// least one heartbeat inside the judging window — which makes this test STRONGER than when it
    /// was written: there is no phase left in which the peer is silent by luck, so the twelve cases
    /// all turn on heartbeats being refused as evidence.
    ///
    /// The cost of that change is here in plain sight: this peer is detected at the grace, about ten
    /// seconds, where round 6 detected it at about two. The CALLER is not delayed by it — asserted.
    /// </summary>
    [Theory]
    [InlineData(0)] [InlineData(400)] [InlineData(800)] [InlineData(1200)]
    [InlineData(1600)] [InlineData(2000)] [InlineData(2400)] [InlineData(2800)]
    [InlineData(3200)] [InlineData(3600)] [InlineData(4000)] [InlineData(4400)]
    public async Task A_bridge_that_only_heartbeats_is_dropped_whatever_the_heartbeat_phase(int phaseMs)
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), Cred());   // all deadlines shipped
        Assert.Equal(TimeSpan.FromSeconds(2), connector.EmergencyDeadline);
        await connector.ConnectAsync();

        await using var peer = await BridgePeer.HeartbeatingButNotReading(
            pipe, Cred().Secret, TimeSpan.FromMilliseconds(phaseMs));
        await Wait(async () => await connector.IsConnectedAsync());

        var timer = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => connector.CancelAllOrdersAsync("ATAS-WEDGED"));
        var caller = timer.Elapsed;

        // The fixture's own premise: it read nothing at all, so any liveness it appears to have is
        // coming from the thread a freeze does not stop.
        Assert.Equal(0, peer.BytesRead);

        // The caller's bound is untouched by the grace.
        Assert.InRange(caller, TimeSpan.FromMilliseconds(1900), TimeSpan.FromSeconds(6));
        Assert.Contains("NOT confirmed", ex.Message);

        // And it is dropped when the grace runs out, whatever its heartbeats say.
        await Wait(async () => !await connector.IsConnectedAsync(), 15_000);
        Assert.InRange(timer.Elapsed, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(15));
        Assert.True(peer.HeartbeatsSent > 0,
            "the peer never beat, so this phase did not test whether a heartbeat can save a wedged bridge");
    }

    /// <summary>
    /// THE VERDICT ITSELF, WATCHED — the busy bridge is still there AFTER the liveness judge has run.
    ///
    /// Codex round-11 FINDING 1. The busy-bridge test above reads its verdict at the CALLER's
    /// deadline, about two seconds, and disposes the connector before the grace expires. The judge
    /// that decides whether to keep or drop the connection runs at the END of the grace, on
    /// `PeerAnsweredSince` — so forcing that method to return false leaves every test in the suite
    /// green while a bridge that was answering throughout gets torn down. The keep half of the rule
    /// was stated, relied on and never observed.
    ///
    /// This one observes it, and the way it does so is by making the grace SHORT rather than by
    /// waiting ten seconds: the grace is what is left of the ordinary RPC deadline, so a three-second
    /// connector puts the verdict about a second after the caller's two. The chatter keeps answers
    /// arriving across the whole window — its own count is asserted, before and after — so the peer
    /// really is the busy case and not the wedged one, and the connection is read AFTER the judge has
    /// had its say.
    ///
    /// THE OTHER DIRECTION IS ALREADY IN THIS CLASS: a peer that only heartbeats is dropped at the
    /// grace, at twelve phases of the shipped heartbeat interval. Between them both answers
    /// `PeerAnsweredSince` can give are observed.
    /// </summary>
    [Fact]
    public async Task A_bridge_that_keeps_answering_survives_the_liveness_verdict_not_just_the_caller()
    {
        var pipe = NewPipe();
        // Three seconds of ordinary RPC deadline, so the grace after the two-second emergency is
        // about one — the verdict lands inside this test instead of after it.
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(3), Cred());
        Assert.Equal(TimeSpan.FromSeconds(2), connector.EmergencyDeadline);
        await connector.ConnectAsync();

        await using var peer = await BridgePeer.AnsweringAllBut(pipe, Cred().Secret, BridgeOps.CancelAll);
        await Wait(async () => await connector.IsConnectedAsync());

        var stop = 0;
        var answered = 0;
        using var chatter = new CancellationTokenSource();
        var talking = Task.Run(async () =>
        {
            while (Volatile.Read(ref stop) == 0)
            {
                try { await connector.GetAccountsAsync(); Interlocked.Increment(ref answered); }
                catch (Exception) { /* the emergency's own failure must not end the chatter */ }
                try { await Task.Delay(120, chatter.Token); } catch (Exception) { return; }
            }
        });

        await Wait(() => Task.FromResult(Volatile.Read(ref answered) > 0));

        var timer = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<Exception>(() => connector.CancelAllOrdersAsync("ATAS-ANSWERING"));
        var atTheCallersDeadline = timer.Elapsed;
        var answeredWhenTheCallerGaveUp = Volatile.Read(ref answered);

        // PAST THE GRACE. The judge is armed when the caller gives up and fires `Remaining(startedAt,
        // rpc timeout)` later; waiting to twice the RPC deadline from the START of the emergency puts
        // this assertion unambiguously after it.
        while (timer.Elapsed < TimeSpan.FromSeconds(6)) await Task.Delay(100);

        var connectedAfterTheVerdict = await connector.IsConnectedAsync();
        var answeredAtTheEnd = Volatile.Read(ref answered);

        Volatile.Write(ref stop, 1);
        await chatter.CancelAsync();
        try { await talking; } catch (Exception) { /* torn down with the test */ }

        // THE PREMISE, IN THE WINDOW THE JUDGE IS ABOUT: answers were still arriving while the grace
        // ran, so this is the busy case and a drop here would be a bridge that was serving.
        Assert.True(answeredWhenTheCallerGaveUp > 0,
            "nothing was answered while the emergency waited, so this is the wedged case");
        Assert.True(answeredAtTheEnd > answeredWhenTheCallerGaveUp,
            "no request was answered between the caller's deadline and the verdict, so the judge had " +
            "nothing to keep the connection for and this test proves nothing");

        // The caller's own bound is untouched by any of it.
        Assert.InRange(atTheCallersDeadline, TimeSpan.FromMilliseconds(1900), TimeSpan.FromSeconds(4));

        Assert.True(connectedAfterTheVerdict,
            "a bridge that was answering throughout was dropped when the liveness judge ran");
    }

    /// <summary>
    /// TWO SECONDS IS THE WHOLE CALL, NOT TWO SECONDS PER PHASE.
    ///
    /// Codex C1 (HIGH). The emergency's frame budget was computed at the call site, before the gate
    /// wait, and then started against a NEW clock the moment the gate was acquired — so a call could
    /// spend nearly the whole deadline queueing and then be handed a fresh one for its write. The
    /// promised two-second end-to-end ceiling was false, and the way to see it is Codex's own check:
    /// hold the gate until just under two seconds, then release it into a pipe with no room.
    ///
    /// That is what this peer does. It drains at a fixed rate until it has taken (frame − buffer)
    /// bytes and then stops for good: the holder's write completes — the kernel has the rest — the
    /// gate is released at about 1.8 s, and the emergency inherits a full buffer that will never
    /// drain again. Under one clock it is cut at the caller's deadline; under two it is cut two
    /// seconds after the gate, near four.
    ///
    /// The message is the premise as well as the verdict: "still being sent" is the FrameIncomplete
    /// branch, so it proves the call got past the gate and into the write rather than expiring on
    /// the queue, which is the only arrangement in which C1 is observable at all.
    /// </summary>
    [Fact]
    public async Task An_emergency_spends_one_budget_across_the_gate_and_the_write()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), Cred());   // all shipped
        Assert.Equal(TimeSpan.FromSeconds(2), connector.EmergencyDeadline);
        await connector.ConnectAsync();

        // 8 KiB every 80 ms ≈ 100 KiB/s, stopping just after 152 KiB — so the ~153 KiB holder frame
        // is fully accepted at about 1.5 s, the gate is released there, and nothing is read again.
        await using var peer = await BridgePeer.ReadingThenStopping(
            pipe, Cred().Secret, 152 * 1024, 8192, TimeSpan.FromMilliseconds(80));
        await Wait(async () => await connector.IsConnectedAsync());

        var holder = connector.PlaceOrderAsync(new PlaceOrderCommand("TA-c1-hold", "ATAS-HALFWAY", "ES",
            OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, new string('c', 150 * 1024)));
        Observe([holder]);
        await Wait(() => Task.FromResult(peer.BytesRead >= 8 * 1024));

        // The emergency's OWN frame is oversized on purpose. A cancel-all is normally a hundred
        // bytes, which the socket buffer swallows whether or not the far end is reading — so a small
        // frame can only ever measure the gate. Sixty-four kilobytes cannot land in an eight-kilobyte
        // buffer that nobody is draining, which is what makes the write cost real time and puts both
        // phases of the call on trial in one measurement.
        var timer = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => connector.CancelAllOrdersAsync(new string('a', 64 * 1024)));
        timer.Stop();

        Assert.True(ex is ConnectorTransportException, $"surfaced as {ex.GetType().Name}");
        Assert.Contains("still being sent", ex.Message);   // it reached the write; the gate was not what expired
        Assert.Contains("NOT confirmed", ex.Message);
        Assert.True(timer.Elapsed > TimeSpan.FromSeconds(1),
            $"the emergency returned in {timer.Elapsed.TotalSeconds:0.00}s — it never queued, so this measures nothing");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3),
            $"the emergency took {timer.Elapsed.TotalSeconds:0.00}s against a two-second promise — the gate wait and the write each started their own clock");
    }

    /// <summary>
    /// EVERY CALL INSIDE ONE OPERATION SHARES ITS DEADLINE — measured on the CONNECTOR, not on the
    /// simulator.
    ///
    /// The round-8 acceptance for F1 runs through the gateway onto `FakeConnector`, which honours
    /// the ambient deadline itself — so it cannot tell whether `AtasConnector` does. It did not:
    /// reverting the connector's half of the fix left all of those tests green (mutant M-F1a
    /// survived, measured). This is the test that reaches it.
    ///
    /// Two risk-reducing calls inside one scope against a bridge that reads nothing, with the send
    /// gate already held. Under one shared deadline the first spends the budget and the second is
    /// refused at once; under a budget per call each waits its own two seconds and the operation
    /// costs twice what it promised.
    /// </summary>
    [Fact]
    public async Task Two_emergency_calls_inside_one_operation_share_its_deadline()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), Cred());   // all shipped
        await connector.ConnectAsync();
        // A peer that READS, so the gate holder makes progress and the first call is answered "busy"
        // with the connection kept — otherwise the first call drops the bridge and the second fails
        // as "not connected", which measures nothing about deadlines.
        await using var peer = await BridgePeer.ReadingSlowly(pipe, Cred().Secret);
        await Wait(async () => await connector.IsConnectedAsync());

        var stuck = connector.PlaceOrderAsync(new PlaceOrderCommand("TA-share-hold", "ATAS-READING", "ES",
            OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, new string('c', 512 * 1024)));
        Observe([stuck]);
        await Wait(() => Task.FromResult(peer.BytesRead >= 32 * 1024));

        using var scope = RiskReducingScope.Begin(connector.EmergencyDeadline);
        var timer = Stopwatch.StartNew();
        var first = await Assert.ThrowsAnyAsync<Exception>(() => connector.CancelOrderAsync("FB-1"));
        var afterFirst = timer.Elapsed;
        var second = await Assert.ThrowsAnyAsync<Exception>(() => connector.CancelAllOrdersAsync("ATAS-READING"));
        timer.Stop();

        Assert.True(await connector.IsConnectedAsync(),
            "the bridge was reading throughout and was dropped anyway, so the second call never reached the deadline path");

        // The premise: the first call really did spend the operation's budget on the gate.
        Assert.True(afterFirst >= TimeSpan.FromMilliseconds(1900),
            $"the first call returned in {afterFirst.TotalSeconds:0.00}s — it never queued, so the second had a budget to inherit anyway");

        Assert.True(timer.Elapsed < TimeSpan.FromMilliseconds(3500),
            $"two calls in one operation took {timer.Elapsed.TotalSeconds:0.00}s — each is still starting its own two seconds");
        Assert.Contains("NOT confirmed", first.Message);
        Assert.Contains("NOT confirmed", second.Message);
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
        // the read inherited the operation's urgency. It did NOT inherit an order's words, which is
        // verifier finding F-D — the sweep died on its `orders` READ, and telling the owner that
        // 'orders' is "NOT confirmed" and to go and check their positions in ATAS sends them hunting
        // for something that was never sent. What they need is the opposite fact.
        Assert.Contains("not responding", reply.Error!.Message);
        Assert.Contains("'orders'", reply.Error!.Message);
        Assert.Contains("Nothing was placed or cancelled", reply.Error!.Message);
        Assert.DoesNotContain("NOT confirmed", reply.Error!.Message);
        Assert.DoesNotContain("check your positions", reply.Error!.Message);
    }

    /// <summary>
    /// A PROGRESS BUDGET IS NOT A BOUND, AND SOMETHING HAS TO BE.
    ///
    /// Codex F2, the structural half. <see cref="AtasConnector.WriteTimeout"/> is spent per chunk and
    /// RESET by every chunk the peer accepts — which is exactly what makes it a stalled-peer detector
    /// and exactly what stops it bounding anything. A legal order near the 1 MiB frame cap is a
    /// thousand chunks, so a peer that accepts one just inside the budget each time keeps the write
    /// alive for a thousand times the budget. <c>WorstCaseOrderPath</c> counted ONE WriteTimeout for
    /// the whole write, <c>GatewayPipeServer.HandlerDrainTimeout</c> was derived from that claim, and
    /// so the drain could expire on an order that was still legitimately in progress and abandon it
    /// DISPATCHING — the state cc7006e and 02aad9a exist to prevent.
    ///
    /// The fixture is built so that ONLY the whole-frame ceiling can end this write: the peer accepts
    /// a chunk every 200 ms against a 2 s per-chunk budget, so the progress budget is never close to
    /// expiring, and at that rate the 512 KiB frame needs about a hundred seconds. It must end at the
    /// ceiling instead, and say which bound it was.
    /// </summary>
    [Fact]
    public async Task A_write_that_keeps_making_progress_is_still_bounded_in_total()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(1), Cred())
        {
            WriteTimeout = TimeSpan.FromSeconds(2),    // per chunk, and never reached here
            FrameTimeout = TimeSpan.FromSeconds(3)     // the total, and the only thing that can end it
        };
        await connector.ConnectAsync();
        await using var peer = await BridgePeer.ReadingSlowly(pipe, Cred().Secret, 1024, TimeSpan.FromMilliseconds(200));
        await Wait(async () => await connector.IsConnectedAsync());

        // ~5 KiB/s against 512 KiB: a hundred seconds of steady, unbroken progress.
        var timer = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => connector.PlaceOrderAsync(
            new PlaceOrderCommand("TA-ceiling-1", "ATAS-READING", "ES", OrderSide.Buy, OrderType.Market,
                1m, null, null, TimeInForce.Day, new string('c', 512 * 1024))));
        timer.Stop();

        Assert.True(ex is ConnectorTransportException, $"surfaced as {ex.GetType().Name}");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10),
            $"the write ran for {timer.Elapsed.TotalSeconds:0.00}s — the per-chunk budget was reset forever and nothing bounded the total");
        Assert.True(timer.Elapsed >= TimeSpan.FromSeconds(3) - TimeSpan.FromMilliseconds(200),
            $"the write ended after {timer.Elapsed.TotalSeconds:0.00}s, before the ceiling — some other bound fired and this measures nothing");

        // The right accusation: it was being read the whole time, it was simply never going to finish.
        Assert.Contains("still being sent", ex.Message);
        Assert.DoesNotContain("did not read", ex.Message);

        // Half a frame is in a writer every caller shares, so the connection cannot be reused.
        await Wait(async () => !await connector.IsConnectedAsync(), 5_000);
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

    /// <summary>
    /// THE SAME FAILURE, TWO DIFFERENT THINGS TO TELL THE OWNER.
    ///
    /// Verifier finding F-D. A prerequisite read inherits the emergency deadline — F11's point, and
    /// it stands — and it was inheriting the WORDING with it: "'accounts' is NOT confirmed … check
    /// your positions and orders in ATAS". Both halves are wrong for a read. Nothing about an
    /// accounts request needs confirming, because it never asked the broker to do anything; and
    /// sending the owner to hunt through ATAS for an order that was never placed is the opposite of
    /// the service these sentences exist to perform. f518251 wrote them because they are the
    /// sentence that sends a person to the right place — one that sends them to the wrong place is
    /// worse than a stack trace, because they will believe it.
    ///
    /// Both kinds, same stalled bridge, same held gate, one variable: what the op does.
    /// </summary>
    [Theory]
    [InlineData("cancel", true)]     // mutating: it may have reached the broker
    [InlineData("read", false)]      // not mutating: it cannot have changed anything
    public async Task An_emergency_says_confirm_only_when_something_could_have_been_changed(string kind, bool mutating)
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), Cred());
        await connector.ConnectAsync();
        await using var peer = await BridgePeer.Stalled(pipe, Cred().Secret);
        await Wait(async () => await connector.IsConnectedAsync());

        var stuck = connector.PlaceOrderAsync(new PlaceOrderCommand("TA-fd-hold", "ATAS-STALLED", "ES",
            OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, new string('c', 128 * 1024)));
        Observe([stuck]);
        await Task.Delay(250);

        // A read only reaches the emergency path because it is INSIDE a risk-reducing operation,
        // which is exactly the case F11 created and F-D is about.
        using var scope = mutating ? null : RiskReducingScope.Begin();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => kind == "cancel"
            ? connector.CancelOrderAsync("FB-1")
            : connector.GetAccountsAsync());

        Assert.Contains("not responding", ex.Message);
        if (mutating)
        {
            Assert.Contains("NOT confirmed", ex.Message);
            Assert.Contains("check your positions and orders in ATAS", ex.Message);
            Assert.DoesNotContain("Nothing was placed", ex.Message);
        }
        else
        {
            Assert.Contains("Nothing was placed or cancelled", ex.Message);
            Assert.Contains("could not be read", ex.Message);
            Assert.DoesNotContain("NOT confirmed", ex.Message);
            Assert.DoesNotContain("check your positions", ex.Message);
        }
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
    // ------------------ what the CONNECTOR knows about where a frame got to (round 10, F4 / F-1)

    /// <summary>
    /// THE SHIPPED CONNECTOR REPORTS `NothingWritten` FOR EVERY REFUSAL THAT NEVER TOOK THE SEND GATE.
    ///
    /// The three ways an emergency can fail without a byte of its frame existing, all on the real
    /// `AtasConnector` over a real pipe. They are DIFFERENT facts about the far end — the operation
    /// was already over, our own backlog was in the way, the peer had stopped reading — and the SAME
    /// fact about the frame: the gate was never ours, so nothing was written.
    ///
    /// It matters because the gateway cannot tell them apart. `TradingGateway` maps every
    /// `ConnectorTransportException` to UNKNOWN, so a sweep leg that took one of these branches came
    /// back `sent-not-confirmed` — an order to go and reconcile something that does not exist, with
    /// a flag that pauses trading (verifier round-9 F-1). The distinction only exists down here.
    /// </summary>
    [Fact]
    public async Task A_refusal_that_never_took_the_send_gate_reports_that_nothing_was_written()
    {
        // 1. THE OPERATION WAS ALREADY OVER when this leg's turn came — the branch round 8 added so
        //    that a leg reached after the deadline would not judge the bridge on one millisecond.
        {
            var pipe = NewPipe();
            await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), Cred());
            await connector.ConnectAsync();
            await using var peer = await BridgePeer.ReadingAndHeartbeating(pipe, Cred().Secret);
            await Wait(async () => await connector.IsConnectedAsync());

            var record = new TransportRecord();
            using (TransportLedger.Attach(record))
            using (RiskReducingScope.Begin(TimeSpan.Zero))
            {
                var ex = await Assert.ThrowsAnyAsync<Exception>(() => connector.CancelOrderAsync("FB-1"));
                Assert.Contains("not sent", ex.Message);
            }
            Assert.Equal(TransportOutcome.NothingWritten, record.Outcome);
        }

        // 2. BUSY — our own backlog held the gate until the emergency's deadline passed. The peer is
        //    reading throughout, which is what makes this the busy case and not the stalled one.
        {
            var pipe = NewPipe();
            await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), Cred());
            await connector.ConnectAsync();
            await using var peer = await BridgePeer.ReadingSlowly(pipe, Cred().Secret);
            await Wait(async () => await connector.IsConnectedAsync());

            var stuck = connector.PlaceOrderAsync(new PlaceOrderCommand("TA-gate-busy", "ATAS-READING", "ES",
                OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, new string('c', 512 * 1024)));
            Observe([stuck]);
            await Wait(() => Task.FromResult(peer.BytesRead >= 32 * 1024));
            var acceptedBefore = peer.BytesRead;

            var record = new TransportRecord();
            using (TransportLedger.Attach(record))
            {
                var ex = await Assert.ThrowsAnyAsync<Exception>(() => connector.CancelAllOrdersAsync("ATAS-READING"));
                Assert.Contains("busy", ex.Message);
            }

            // The fixture's own premise: without contention this measured nothing about the gate.
            Assert.True(peer.BytesRead - acceptedBefore > 0,
                "the peer accepted no bytes while the emergency waited — that is the stalled case, not the busy one");
            Assert.Equal(TransportOutcome.NothingWritten, record.Outcome);
        }

        // 3. THE PEER HAD STOPPED READING and the gate expired on it — a different accusation, the
        //    same fact about our frame.
        {
            var pipe = NewPipe();
            await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), Cred());
            await connector.ConnectAsync();
            await using var peer = await BridgePeer.HeartbeatingButNotReading(pipe, Cred().Secret, TimeSpan.Zero);
            await Wait(async () => await connector.IsConnectedAsync());

            var stuck = connector.PlaceOrderAsync(new PlaceOrderCommand("TA-gate-stalled", "ATAS-WEDGED", "ES",
                OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, new string('c', 512 * 1024)));
            Observe([stuck]);
            await Task.Delay(300);   // the write is in flight and the peer is taking nothing

            var record = new TransportRecord();
            using (TransportLedger.Attach(record))
                await Assert.ThrowsAnyAsync<Exception>(() => connector.CancelAllOrdersAsync("ATAS-WEDGED"));

            Assert.Equal(TransportOutcome.NothingWritten, record.Outcome);
        }
    }

    /// <summary>
    /// AND THE OTHER TWO STATES, so "nothing was written" is a measurement rather than the only
    /// answer this connector knows how to give.
    ///
    /// A frame that was answered reports `ReplyReceived` — whatever the answer was — and a frame the
    /// peer never answered reports `PossiblyWritten`, because it went out and nothing can recall it.
    /// </summary>
    [Fact]
    public async Task An_answered_frame_reports_a_reply_and_an_unanswered_one_reports_it_may_have_landed()
    {
        // Answered.
        {
            var pipe = NewPipe();
            await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), Cred());
            await connector.ConnectAsync();
            await using var peer = await BridgePeer.AnsweringAllBut(pipe, Cred().Secret, "nothing-is-muted");
            await Wait(async () => await connector.IsConnectedAsync());

            var record = new TransportRecord();
            using (TransportLedger.Attach(record))
                await connector.CancelOrderAsync("FB-1").WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(TransportOutcome.ReplyReceived, record.Outcome);
        }

        // Sent, and never answered.
        {
            var pipe = NewPipe();
            await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), Cred());
            await connector.ConnectAsync();
            await using var peer = await BridgePeer.AnsweringAllBut(pipe, Cred().Secret, BridgeOps.Cancel);
            await Wait(async () => await connector.IsConnectedAsync());

            var record = new TransportRecord();
            using (TransportLedger.Attach(record))
                await Assert.ThrowsAnyAsync<Exception>(() => connector.CancelOrderAsync("FB-1"));

            Assert.Equal(TransportOutcome.PossiblyWritten, record.Outcome);
        }
    }

    /// <summary>
    /// A FRAME THE PEER HAS ALREADY READ WHOLE IS NOT `not-sent`, WHATEVER ENDS THE WAIT FOR ITS
    /// ANSWER — and this is the DANGEROUS direction (Codex round-10 F2).
    ///
    /// The transport state used to be written down at the moment a REPLY arrived, or at one of the
    /// enumerated ways the send could fail. Between those two moments the frame is fully on the far
    /// side and nothing has been recorded, so any exit that was not in the list left the record
    /// EMPTY — and an empty record means "no mutating call was ever attempted", which the mapper
    /// reads as <c>not-sent</c>. Caller cancellation is such an exit: the reply wait's catch is
    /// filtered <c>when (!ct.IsCancellationRequested)</c> precisely so a caller's own cancellation
    /// passes through it.
    ///
    /// The result is the worst report this system can produce: the owner is told nothing was sent
    /// for a cancel that IS at the broker. `not-sent` is the one word that carries no reconciliation
    /// and no pause — it is an assurance, and an assurance is the thing that must never be produced
    /// by an absence of information.
    ///
    /// So the fix is not another arm on that catch. An ATTEMPT is recorded when a mutating call
    /// starts, and a record that was attempted and never reported reads
    /// <see cref="TransportOutcome.PossiblyWritten"/> — the fail-closed answer — for every exit,
    /// including ones nobody has enumerated yet.
    ///
    /// The peer here reads the whole frame and withholds its reply (Codex's own check), the scope
    /// is deliberately wide so the emergency deadline cannot be what ends the wait, and the count of
    /// muted frames is the premise asserted rather than assumed.
    /// </summary>
    [Fact]
    public async Task A_frame_the_peer_read_whole_is_not_reported_as_never_sent_when_its_caller_gives_up()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), Cred());
        await connector.ConnectAsync();
        await using var peer = await BridgePeer.AnsweringAllBut(pipe, Cred().Secret, BridgeOps.Cancel);
        await Wait(async () => await connector.IsConnectedAsync());

        using var caller = new CancellationTokenSource();
        var record = new TransportRecord();
        Task cancel;

        // THIRTY SECONDS, so the thing that ends the wait is unambiguously the caller. At the
        // shipped two-second deadline the reply timeout would record `PossiblyWritten` on its own
        // and the test would pass over the defect it is about.
        using (TransportLedger.Attach(record))
        using (RiskReducingScope.Begin(TimeSpan.FromSeconds(30)))
            cancel = connector.CancelOrderAsync("FB-1", caller.Token);

        await Wait(() => Task.FromResult(peer.MutedFramesSeen > 0));
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => cancel.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(TransportOutcome.PossiblyWritten, record.Outcome);

        // AND THE WORD THE OWNER READS, which is the whole reason the state is recorded. The record
        // for such a leg is UNKNOWN — `TradingGateway` settles every ambiguous connector failure
        // that way — so this is the combination a real sweep leg arrives at the mapper with.
        Assert.Equal("sent-not-confirmed", GatewayPipeServer.LegWordFor(ExecutionState.UNKNOWN, record.Outcome));
    }

    /// <summary>
    /// A CALLER THAT GIVES UP RELEASES ITS SLOT, AND THE ANSWER THAT ARRIVES ANYWAY IS COUNTED.
    ///
    /// Verifier round-11 L-1, measured: `_pending` went 0 -> 1 -> 1 across a cancelled emergency and
    /// `AwaitingLateAnswer` stayed at 0. The reply wait's catch is filtered
    /// <c>when (!ct.IsCancellationRequested)</c> — deliberately, so a caller's own cancellation is
    /// not mistaken for a reply timeout — and that filter also skipped the `_pending.TryRemove` every
    /// other exit performs. Two costs, and the second is the one that matters: the entry grew by one
    /// per cancelled emergency within a connection, and because the id never reached `_abandoned`, an
    /// answer arriving for it was delivered to a `TaskCompletionSource` nobody awaited and counted in
    /// NEITHER `LateAnswers` NOR the late-answer event — the two counters round 9's F2 exists to keep
    /// honest.
    ///
    /// The exit now goes through the same bounded machinery every other abandoned request uses, with
    /// one difference that is stated rather than inherited: IT PASSES NO VERDICT ON THE CONNECTION.
    /// A reply TIMEOUT is evidence about the bridge; a caller cancelling for its own reasons — the
    /// app closing, an operator pressing stop — is evidence about nothing at all, and tearing a
    /// working bridge down on it is the round-6 mistake in a new place.
    /// </summary>
    [Fact]
    public async Task A_caller_that_cancels_an_emergency_releases_its_slot_and_still_counts_a_late_answer()
    {
        // ANSWERED LATE. The peer answers everything, 1.5 s after it reads it, and the caller gives
        // up long before that — so the answer is unambiguously a late one.
        {
            var pipe = NewPipe();
            await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(5), Cred());
            await connector.ConnectAsync();
            await using var peer = await BridgePeer.AnsweringAfter(pipe, Cred().Secret, TimeSpan.FromMilliseconds(1500));
            await Wait(async () => await connector.IsConnectedAsync());

            Assert.Equal(0, connector.PendingRequests);

            using var caller = new CancellationTokenSource();
            Task cancel;
            using (RiskReducingScope.Begin(TimeSpan.FromSeconds(30)))
                cancel = connector.CancelOrderAsync("FB-1", caller.Token);

            await Wait(() => Task.FromResult(connector.PendingRequests == 1));
            await caller.CancelAsync();
            await Assert.ThrowsAnyAsync<Exception>(() => cancel.WaitAsync(TimeSpan.FromSeconds(10)));

            // The slot is still held — on purpose, and that is what makes the count below possible.
            Assert.Equal(1, connector.AwaitingLateAnswer);

            await Wait(() => Task.FromResult(connector.LateAnswers == 1), 10_000);
            Assert.Equal(1, connector.LateAnswers);
            Assert.Equal(0, connector.PendingRequests);
            Assert.Equal(0, connector.AwaitingLateAnswer);
            Assert.True(await connector.IsConnectedAsync(),
                "a bridge that answered was dropped because a caller of ours gave up");
        }

        // NEVER ANSWERED. Both counters still return to zero when the grace runs out, and the
        // connection is left exactly where the caller found it.
        {
            var pipe = NewPipe();
            await using var connector = new AtasConnector(pipe, TimeSpan.FromMilliseconds(800), Cred());
            await connector.ConnectAsync();
            await using var peer = await BridgePeer.AnsweringAllBut(pipe, Cred().Secret, BridgeOps.Cancel);
            await Wait(async () => await connector.IsConnectedAsync());

            using var caller = new CancellationTokenSource();
            Task cancel;
            using (RiskReducingScope.Begin(TimeSpan.FromSeconds(30)))
                cancel = connector.CancelOrderAsync("FB-1", caller.Token);

            await Wait(() => Task.FromResult(peer.MutedFramesSeen > 0));
            await caller.CancelAsync();
            await Assert.ThrowsAnyAsync<Exception>(() => cancel.WaitAsync(TimeSpan.FromSeconds(10)));

            await Wait(() => Task.FromResult(connector.PendingRequests == 0), 10_000);
            Assert.Equal(0, connector.PendingRequests);
            Assert.Equal(0, connector.AwaitingLateAnswer);
            Assert.Equal(0, connector.LateAnswers);

            // NO VERDICT. Our own cancellation is not evidence about the bridge.
            Assert.True(await connector.IsConnectedAsync(),
                "the connection was judged on a cancellation that came from this side");
        }
    }

    /// <summary>
    /// A CALLER WHO GIVES UP WHILE STILL QUEUED FOR THE SEND GATE SENT NOTHING, AND SAYING OTHERWISE
    /// COSTS A RECONCILIATION FOR A FRAME THAT NEVER EXISTED (Codex round-10 F1).
    ///
    /// Every OTHER way out of the gate wait already reports `NothingWritten` and can prove it — the
    /// operation was already over, our own backlog was in the way, the peer had stopped reading. The
    /// gate is a semaphore, so the frame is not built and not one byte of it can exist until the
    /// wait returns TRUE. Cancellation is simply a fourth way of not getting it.
    ///
    /// It was the one exit that took the outer catch instead, which records the fail-closed answer
    /// for everything it cannot identify — right as a default and wrong here, because this exit CAN
    /// be identified. The cost is not cosmetic: `PossiblyWritten` makes the leg
    /// <c>sent-not-confirmed</c>, which sets `needs_reconciliation` and pauses all further execution
    /// on an order the connector never touched.
    ///
    /// The gate is held by an oversized placement against a peer that reads nothing, so the cancel
    /// is unambiguously still queued — and the peer's byte count is the premise, asserted.
    /// </summary>
    [Fact]
    public async Task A_cancellation_that_never_got_the_send_gate_reports_that_nothing_was_written()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), Cred());
        await connector.ConnectAsync();
        await using var peer = await BridgePeer.HeartbeatingButNotReading(pipe, Cred().Secret, TimeSpan.Zero);
        await Wait(async () => await connector.IsConnectedAsync());

        var stuck = connector.PlaceOrderAsync(new PlaceOrderCommand("TA-gate-cancelled", "ATAS-WEDGED", "ES",
            OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, new string('c', 512 * 1024)));
        Observe([stuck]);
        await Task.Delay(300);   // the placement owns the gate and the peer is taking nothing
        var acceptedBefore = peer.BytesRead;

        using var caller = new CancellationTokenSource();
        var record = new TransportRecord();
        Task cancel;

        // THIRTY SECONDS, so the gate wait cannot expire on its own: the only thing that can end
        // this call is the caller's token, which is what the test is about.
        using (TransportLedger.Attach(record))
        using (RiskReducingScope.Begin(TimeSpan.FromSeconds(30)))
            cancel = connector.CancelOrderAsync("FB-1", caller.Token);

        await Task.Delay(200);
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => cancel.WaitAsync(TimeSpan.FromSeconds(10)));

        // The premise: the peer took nothing while the cancel was queued, so the gate was still
        // held by the placement and this frame was never begun.
        Assert.Equal(acceptedBefore, peer.BytesRead);
        Assert.Equal(TransportOutcome.NothingWritten, record.Outcome);
        Assert.Equal("not-sent", GatewayPipeServer.LegWordFor(ExecutionState.UNKNOWN, record.Outcome));
    }

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
            peer.Track(Task.Run(() => peer.Heartbeats(TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250))));
            return peer;
        }

        /// <summary>
        /// A WEDGED BRIDGE: it heartbeats at the shipped interval and never reads a byte.
        ///
        /// This is not a contrived shape. <c>BridgeServer.StartHeartbeat</c> runs on its own
        /// <c>Task.Run</c>, independent of the frame read loop, so a freeze inside ATAS that wedges
        /// the loop leaves the heartbeat running — the connection looks alive and consumes nothing.
        /// <paramref name="phase"/> is how long it waits before the first one, which is what decides
        /// whether a heartbeat lands inside an emergency's two-second window.
        /// </summary>
        public static async Task<BridgePeer> HeartbeatingButNotReading(string pipe, string secret, TimeSpan phase)
        {
            var peer = await ConnectAndSayHello(pipe, secret, "ATAS-WEDGED", null, PaceBytes);
            peer.Track(Task.Run(() => peer.Heartbeats(phase, ShippedHeartbeatInterval)));
            return peer;
        }

        /// <summary><c>BridgeServer.HeartbeatInterval</c>, read from that file, not guessed.</summary>
        public static readonly TimeSpan ShippedHeartbeatInterval = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Drains at a paced rate until it has taken <paramref name="thresholdBytes"/>, then stops
        /// reading for good.
        ///
        /// It exists to release the send gate at a chosen moment INTO A FULL BUFFER. A writer's
        /// frame completes when the kernel has taken the last of it, not when the peer has read it,
        /// so stopping at (frame size − buffer) hands the next caller the gate and a pipe with no
        /// room in it — which is the only way to make the gate wait and the write both cost real
        /// time in one call.
        /// </summary>
        public static async Task<BridgePeer> ReadingThenStopping(
            string pipe, string secret, int thresholdBytes, int bytes, TimeSpan pace)
        {
            var peer = await ConnectAndSayHello(pipe, secret, "ATAS-HALFWAY", null, PaceBytes);
            peer.Track(Task.Run(() => peer.PumpUntil(thresholdBytes, bytes, pace)));
            return peer;
        }

        async Task PumpUntil(int threshold, int bytes, TimeSpan pace)
        {
            var buf = new byte[bytes];
            try
            {
                while (!_stop.IsCancellationRequested && BytesRead < threshold)
                {
                    var n = await _p.ReadAsync(buf, _stop.Token);
                    if (n == 0) return;
                    Interlocked.Add(ref _read, n);
                    await Task.Delay(pace, _stop.Token);
                }
            }
            catch (Exception) { /* the test ending is how this always ends */ }
        }

        /// <summary>
        /// A BRIDGE THAT IS PLAINLY SERVING, with one operation outstanding: it reads every frame and
        /// answers all of them except <paramref name="mute"/>.
        ///
        /// This is the shape the "busy" verdict exists for — the read loop is running, answers are
        /// coming back, and one request is simply late. It is what separates that from a wedged
        /// bridge, which produces heartbeats and no answers at all.
        /// </summary>
        public static async Task<BridgePeer> AnsweringAllBut(string pipe, string secret, string mute)
        {
            var peer = await ConnectAndSayHello(pipe, secret, "ATAS-ANSWERING", null, PaceBytes);
            peer.Track(Task.Run(() => peer.AnswerEverythingBut(mute, TimeSpan.Zero)));
            return peer;
        }

        /// <summary>
        /// A BRIDGE THAT IS WORKING ON OUR FRAME AND ANSWERS IT LATE.
        ///
        /// It reads everything and answers everything, just not within two seconds. This is not a
        /// contrived peer: <c>BridgeServer</c> handles frames strictly sequentially, so a bridge in
        /// the middle of a slow synchronous ATAS call looks exactly like this — it has our frame in
        /// hand and can emit nothing at all until it is done with it.
        /// </summary>
        public static async Task<BridgePeer> AnsweringAfter(string pipe, string secret, TimeSpan delay)
        {
            var peer = await ConnectAndSayHello(pipe, secret, "ATAS-ANSWERING", null, PaceBytes);
            peer.Track(Task.Run(() => peer.AnswerEverythingBut(null, delay)));
            return peer;
        }

        async Task AnswerEverythingBut(string? mute, TimeSpan delay)
        {
            var buf = new byte[8192];
            var pending = new MemoryStream();
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var n = await _p.ReadAsync(buf, _stop.Token);
                    if (n == 0) return;
                    Interlocked.Add(ref _read, n);
                    pending.Write(buf, 0, n);

                    // Line framing, because a read boundary is not a frame boundary.
                    var all = pending.ToArray();
                    var from = 0;
                    for (var i = 0; i < all.Length; i++)
                    {
                        if (all[i] != (byte)'\n') continue;
                        var line = Encoding.UTF8.GetString(all, from, i - from);
                        from = i + 1;
                        BridgeFrame? f;
                        try { f = Json.Read<BridgeFrame>(line); } catch (Exception) { continue; }
                        if (f?.Id is null) continue;
                        if (f.Op == mute)
                        {
                            // COUNTED, because "the peer read the WHOLE frame" is the premise of the
                            // reply-wait tests and a byte count cannot assert it. A frame that parses
                            // is a frame that arrived complete, newline and all.
                            Interlocked.Increment(ref _muted);
                            continue;
                        }
                        var answer = new { v = Versions.BridgeProtocolVersion, id = f.Id, ok = true, data = Array.Empty<object>() };
                        if (delay <= TimeSpan.Zero) { await WriteAsync(answer); continue; }

                        // Answered on its own schedule, so the read loop is free to carry on — which
                        // is the one way this differs from a real BridgeServer and the difference
                        // does not matter here: what is on trial is what OUR end concludes from
                        // silence, and this end sees silence either way.
                        Track(Task.Run(async () =>
                        {
                            try { await Task.Delay(delay, _stop.Token); await WriteAsync(answer); }
                            catch (Exception) { /* torn down with the test */ }
                        }));
                    }
                    pending = new MemoryStream();
                    pending.Write(all, from, all.Length - from);
                }
            }
            catch (Exception) { /* the test ending is how this always ends */ }
        }

        long _beats;
        long _muted;

        /// <summary>
        /// Complete frames of the MUTED op this peer has read and deliberately not answered. It is
        /// the evidence that the frame is entirely on the far side, which is what separates "the
        /// caller gave up while the frame was still going out" from "the caller gave up waiting for
        /// an answer to a frame that had fully landed".
        /// </summary>
        public long MutedFramesSeen => Interlocked.Read(ref _muted);

        /// <summary>Heartbeats this peer has put on the wire since the handshake.</summary>
        public long HeartbeatsSent => Interlocked.Read(ref _beats);

        async Task Heartbeats(TimeSpan phase, TimeSpan interval)
        {
            try
            {
                await Task.Delay(phase, _stop.Token);
                while (!_stop.IsCancellationRequested)
                {
                    await WriteAsync(new { v = Versions.BridgeProtocolVersion, op = BridgeOps.Heartbeat });
                    Interlocked.Increment(ref _beats);
                    await Task.Delay(interval, _stop.Token);
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
            if (pace is { } p) peer.Track(Task.Run(() => peer.Pump(p, bytes)));
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

        readonly List<Task> _background = [];
        void Track(Task t) { lock (_background) _background.Add(t); }

        /// <summary>
        /// STOP WRITING BEFORE THE PIPE GOES AWAY, and wait for it to have stopped.
        ///
        /// Cancelling and disposing in the same breath leaves a background writer mid-<c>WriteAsync</c>
        /// on a handle that is being closed underneath it. On macOS that surfaces as a caught
        /// exception and nothing more; on Windows a named pipe is a real kernel object with an
        /// overlapped write in flight, and this fixture reproduced it: with the twelve heartbeat
        /// phases running alongside two other test hosts, `dotnet test TradeAgent.sln` aborted with
        /// "Test host process crashed" — twice, at 234 and 209 tests — and was green with the same
        /// twelve excluded. So the tasks are held, cancelled, and AWAITED first.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            Task[] running;
            lock (_background) running = [.. _background];
            if (running.Length > 0)
            {
                try { await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception) { /* cancelled or faulted: either way it is no longer writing */ }
            }
            await _p.DisposeAsync();
        }
    }
}

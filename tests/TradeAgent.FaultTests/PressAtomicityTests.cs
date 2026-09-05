using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// ONE EMERGENCY PRESS AT A TIME, AND THE REFUSAL IS THE SAME STEP AS THE FIRST DURABLE ROW
/// (REVIEW 2026-09-05 finding 2, executed as probe P10; Codex F6).
///
/// <c>RefuseWhileAPressIsOpen</c> READ the store and <c>OpenPressRow</c> WROTE it, with a connector
/// round trip in between and nothing holding the two together — no lock, no transaction, no
/// uniqueness the second writer could lose on. Two callers arriving together both passed the check,
/// both captured the same position, both passed the drift re-read (neither fill had landed yet) and
/// both sent a market close. P10 measured it: a long 2 became SHORT 2 and both presses answered
/// "ok". <c>OperatorCloseAllAsync</c> has two entry points in this repo — the Dashboard button and
/// <c>GatewayHost/Program.cs</c>, a SECOND PROCESS over the same database — so an in-process lock
/// alone would not have settled it either.
///
/// These are BARRIER tests because that is the only way to observe the window: both presses are held
/// inside the position read they have already passed the check to reach, then released together.
/// What is measured is the WIRE.
///
/// TWO GUARDS STAND IN THAT WINDOW, AND EITHER OF THEM REFUSING IS THE PRODUCT BEING RIGHT. Between
/// the barrier and the wire a press does two things, in this order: it re-reads the position it
/// captured, and it writes the row that claims the control. So the second press is refused by
/// whichever it reaches first, and which one that is depends on how the machine schedules two flows:
///
///   - the DRIFT RE-READ, when the first press's fill has already landed — "ES was 2 when you
///     pressed and is 0 now", and nothing is sent for that instrument;
///   - the ATOMIC CLAIM, when it has not — "close-all sent at HH:MM; resolve it first", the insert
///     losing its <c>NOT EXISTS</c>, and nothing sent at all.
///
/// Asserting one of those sentences is asserting a schedule. The first test did, and it held on this
/// Mac, on ubuntu and on windows; on the slower macos-latest runner the first press's fill landed
/// first, and CI run 33958941039 failed <c>Assert.Single()</c> ON THE REFUSAL WHILE ITS OWN LOG
/// SHOWED THE PRODUCT RIGHT — one close on the wire, the account flat, one press row. So the race
/// test now asserts the invariants and names both refusals, which is true on any scheduler, and the
/// schedule that broke it gets a deterministic test of its own, where the connector seam decides the
/// order rather than the machine.
/// </summary>
public class PressAtomicityTests(ITestOutputHelper log)
{
    /// <summary>
    /// Long enough that nothing sane reaches it, short enough that a hang FAILS instead of hanging.
    /// Every wait in this file is bounded by it; a press that spent this long would in any case have
    /// blown the connector's two-second emergency budget and answered with the deadline instead.
    /// </summary>
    static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    static async Task<(TradingGateway Gw, RecordingConnector Conn, Database Db)> Ready()
    {
        var db = TestEnv.NewDb();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker()));
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        return (gw, conn, db);
    }

    /// <summary>
    /// One of the two presses, as the seam sees it. The seam runs INSIDE the gateway's connector
    /// calls, where there is nothing to tell one flow from the other — so each press carries its own
    /// tag on the execution context, and every ordering these tests need is stated in terms of it.
    /// </summary>
    sealed class Press(string name)
    {
        public string Name { get; } = name;

        /// <summary>Position reads this press has made: 1 is its capture, 2 its drift re-read.</summary>
        public int Reads;

        /// <summary>Completed when this press has answered — whether it sent, drifted or was refused.</summary>
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Press Other = null!;
    }

    static readonly AsyncLocal<Press?> Pressing = new();

    static (Press A, Press B) TwoPresses()
    {
        var a = new Press("A");
        var b = new Press("B") { Other = a };
        a.Other = b;
        return (a, b);
    }

    /// <summary>
    /// WHERE BOTH PRESSES MEET, and a real two-party barrier rather than a poll: neither leaves its
    /// capture read until both are inside it. The poll this replaces could time out and let the test
    /// run anyway, which turns "the presses raced" from something the test establishes into
    /// something it assumes.
    /// </summary>
    sealed class CaptureBarrier
    {
        int _arrived;
        readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool BothArrived => Volatile.Read(ref _arrived) >= 2;

        public async Task ArriveAsync()
        {
            if (Interlocked.Increment(ref _arrived) == 2) _both.SetResult();
            await _both.Task.WaitAsync(Guard);
        }
    }

    /// <summary>
    /// Presses the button under a tag, and turns every way it can answer into one string: the ok
    /// summary, the refusal code and sentence, or the exception. A refusal is an ANSWER here and not
    /// a failure — which of the two the person gets is the whole subject of these tests.
    /// </summary>
    static async Task<string> PressAsync(TradingGateway gw, Press p)
    {
        Pressing.Value = p;
        try { var o = await gw.OperatorCloseAllAsync(); return $"ok — {o.Summary}"; }
        catch (GatewayDeniedException ex) { return $"{ex.Code} — {ex.Message}"; }
        catch (Exception ex) { return $"{ex.GetType().Name}: {ex.Message}"; }
        finally { p.Done.TrySetResult(); }
    }

    /// <summary>The atomic claim refusing, in the words <c>docs/CONTRACTS.md</c> promises.</summary>
    static bool RefusedByTheClaim(string answer) =>
        answer.StartsWith("EMERGENCY_PRESS_UNRESOLVED") &&
        answer.Contains("close-all sent at") && answer.Contains("resolve it first");

    /// <summary>The drift re-read refusing: what it captured is not what is there, so nothing goes.</summary>
    static bool RefusedByTheDriftReRead(string answer) =>
        answer.Contains("Nothing was sent for 1 of them, because what is there changed after you pressed") &&
        answer.Contains("ES was 2 when you pressed and is 0 now");

    /// <summary>
    /// AND THE THIRD SCHEDULE, for completeness rather than because it has been seen: the second
    /// press's CAPTURE read is itself served after the first press's fill, so there is nothing to
    /// capture and the press ends before either guard is reached. It is the same news to the person
    /// — nothing was sent, and what they wanted done is done — and a test that leaves it out is
    /// asserting a schedule again, one step earlier.
    /// </summary>
    static bool NothingWasOpen(string answer) => answer.Contains("There was nothing open to close");

    /// <summary>Every way the second press can truthfully answer, all of them meaning NOTHING WAS SENT.</summary>
    static bool SentNothing(string answer) =>
        RefusedByTheClaim(answer) || RefusedByTheDriftReRead(answer) || NothingWasOpen(answer);

    /// <summary>The press that DID send: its answer is its own record, still waiting for the owner.</summary>
    static bool Sent(string answer) =>
        answer.StartsWith("ok — 1 of 1 record(s)") && answer.Contains("still waiting for you");

    /// <summary>What the two presses did, as the wire and the store saw it. This log is the evidence.</summary>
    List<ExecutionRequest> WhatHappened(TradingGateway gw, RecordingConnector conn, string[] outcomes)
    {
        log.WriteLine($"press A : {outcomes[0]}");
        log.WriteLine($"press B : {outcomes[1]}");
        log.WriteLine($"close calls on the wire : {conn.Closes}");
        foreach (var o in conn.Broker.Orders)
            log.WriteLine($"  {o.ConnectorOrderId} {o.Side} {o.Quantity} {o.Symbol} {o.State} coid={o.ClientOrderId}");
        var after = conn.Broker.Positions.FirstOrDefault();
        log.WriteLine($"position after  : {(after is null ? "flat" : $"{after.Symbol} {after.Quantity}")}");
        var rows = gw.Requests.Query("request_id LIKE 'op-close-%'");
        log.WriteLine($"press rows      : {rows.Count} -> {string.Join(", ", rows.Select(r => r.RequestId))}");
        return rows;
    }

    /// <summary>
    /// WHAT MUST BE TRUE OF A DOUBLE PRESS WHICHEVER GUARD REFUSED, and the reason a refusal that
    /// arrives by the other route is not a failure: one close reached the wire, the account is flat
    /// rather than reversed, one press wrote rows and they are all one nonce.
    /// </summary>
    static void AssertTheInvariants(RecordingConnector conn, List<ExecutionRequest> rows)
    {
        Assert.Equal(1, conn.Closes);
        Assert.Equal(2, conn.Broker.Orders.Count);          // the opening buy and one closing sell
        var after = conn.Broker.Positions.FirstOrDefault();
        Assert.True(after is null || after.Quantity == 0m,
            $"expected the account flat, it holds {after?.Quantity}");
        Assert.Single(rows);
        Assert.Single(rows.Select(r => r.RequestId.Split('-')[2]).Distinct());
    }

    /// <summary>
    /// P10, TURNED THE RIGHT WAY UP. Two presses released together against one long 2: one set of
    /// wire calls, one press row, the account FLAT rather than reversed, and the other press refused
    /// with nothing sent — by whichever of the two guards it reached first, which is a property of
    /// the machine and not of the product.
    /// </summary>
    [Fact]
    public async Task Two_close_all_presses_released_together_send_one_close_and_refuse_the_other()
    {
        var (gw, conn, db) = await Ready();
        using var _1 = db;

        await gw.PlaceAsync(new AgentContext("a"), "pa-open", TestEnv.Buy("ES", 2m));
        var before = conn.Broker.Positions.Single();
        log.WriteLine($"position before : {before.Symbol} {before.Quantity}");

        // Both presses are parked inside the capture read — past the early check, before any row
        // exists — and released together. NOTHING ELSE IS ORDERED: what happens after the barrier is
        // the race this test is about.
        var (a, b) = TwoPresses();
        var barrier = new CaptureBarrier();
        conn.Seam = async kind =>
        {
            if (Pressing.Value is { } p && kind == RecordingConnector.HeldCall.Positions && ++p.Reads == 1)
                await barrier.ArriveAsync();
        };

        var outcomes = await Task.WhenAll(Task.Run(() => PressAsync(gw, a)), Task.Run(() => PressAsync(gw, b)));
        log.WriteLine($"both inside the capture read : {barrier.BothArrived}");
        var rows = WhatHappened(gw, conn, outcomes);

        Assert.True(barrier.BothArrived, "the presses were not both inside the capture read");
        AssertTheInvariants(conn, rows);

        // The other press sent nothing, and said so in the words of whatever it ran into first.
        // Naming one of them asserts a schedule: see the note on this class, and CI run 33958941039,
        // where the product did every one of the things above and the test failed anyway.
        var refused = Assert.Single(outcomes, SentNothing);
        log.WriteLine($"refused by      : {(RefusedByTheClaim(refused) ? "the atomic claim"
                                          : RefusedByTheDriftReRead(refused) ? "the drift re-read"
                                          : "its own capture read, which found nothing open")}");
        Assert.Single(outcomes, Sent);

        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE OTHER GUARD ON ITS OWN — the slow runner's schedule, made deliberate. This is CI run
    /// 33958941039 reproduced: press B's drift re-read is held until press A's close has FILLED, so
    /// B finds a flat account where it captured a long 2, and is refused there rather than at the
    /// claim. Nothing is sent for the instrument, no row is written for it, and the answer names the
    /// two numbers — which is the whole of what the person needs to press again.
    /// </summary>
    [Fact]
    public async Task The_drift_re_read_refuses_the_second_press_when_the_first_fill_landed_first()
    {
        var (gw, conn, db) = await Ready();
        using var _1 = db;

        await gw.PlaceAsync(new AgentContext("a"), "pa-open", TestEnv.Buy("ES", 2m));
        log.WriteLine($"position before : {conn.Broker.Positions.Single().Quantity}");

        var (a, b) = TwoPresses();
        var barrier = new CaptureBarrier();

        // BOTH HALVES OF THE SLOW RUNNER'S SCHEDULE, and the second half is why the barrier alone is
        // not enough: it releases both presses INTO their capture read, so a press that then loses
        // the processor for the whole of the other one comes back to a flat account and ends at
        // "there was nothing open to close" — a different true answer, and not the one under test.
        // So A's fill waits until B is demonstrably past its capture (B signals from its re-read),
        // and B's re-read then waits for that fill. Neither wait can outlast the other.
        var bCaptured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.Seam = async kind =>
        {
            if (Pressing.Value is not { } p) return;

            if (kind == RecordingConnector.HeldCall.Positions && ++p.Reads == 1)
            {
                await barrier.ArriveAsync();
                return;
            }

            if (kind == RecordingConnector.HeldCall.Close && p.Name == "A")
            {
                await bCaptured.Task.WaitAsync(Guard);
                return;
            }

            if (kind == RecordingConnector.HeldCall.Positions && p.Reads == 2 && p.Name == "B")
            {
                bCaptured.TrySetResult();
                var until = DateTimeOffset.UtcNow + Guard;
                while (conn.Broker.Positions.Any(x => x.Quantity != 0) && DateTimeOffset.UtcNow < until)
                    await Task.Delay(1);
                log.WriteLine("press B re-reads the position with the book " +
                              (conn.Broker.Positions.Any(x => x.Quantity != 0) ? "STILL UNCHANGED" : "flat"));
            }
        };

        var outcomes = await Task.WhenAll(Task.Run(() => PressAsync(gw, a)), Task.Run(() => PressAsync(gw, b)));
        var rows = WhatHappened(gw, conn, outcomes);

        Assert.True(barrier.BothArrived, "the presses were not both inside the capture read");
        AssertTheInvariants(conn, rows);

        Assert.True(RefusedByTheDriftReRead(outcomes[1]), $"press B was not refused by the drift re-read: {outcomes[1]}");
        Assert.True(Sent(outcomes[0]), $"press A did not send: {outcomes[0]}");

        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE OTHER DIRECTION, because a guard that refuses everything would pass the tests above. A
    /// press whose records the owner has resolved leaves the control usable, and the next press
    /// reaches the wire.
    /// </summary>
    [Fact]
    public async Task A_press_after_the_previous_one_is_resolved_still_reaches_the_wire()
    {
        var (gw, conn, db) = await Ready();
        using var _1 = db;

        await gw.PlaceAsync(new AgentContext("a"), "pa-open-1", TestEnv.Buy("ES", 2m));
        await gw.OperatorCloseAllAsync();
        log.WriteLine($"first press  : close calls {conn.Closes}");
        Assert.Equal(1, conn.Closes);

        // The owner reads the card and confirms every line of it. `ForceResolve` deliberately does
        // not recompute ExecutionCapability, so the card refreshes health afterwards; this is that.
        foreach (var r in gw.Requests.Query("request_id LIKE 'op-close-%'"))
            gw.ForceResolve(r.RequestId, r.State, "checked in ATAS");
        await gw.RefreshHealthAsync();
        log.WriteLine($"unresolved press after the owner confirmed : " +
                      $"{gw.UnresolvedPressNonce(TradingGateway.ClosePress) ?? "none"}");
        Assert.Null(gw.UnresolvedPressNonce(TradingGateway.ClosePress));

        await gw.PlaceAsync(new AgentContext("a"), "pa-open-2", TestEnv.Buy("ES", 1m));
        var outcome = await gw.OperatorCloseAllAsync();
        log.WriteLine($"second press : close calls {conn.Closes} — {outcome.Summary}");

        Assert.Equal(2, conn.Closes);
        await gw.DisposeAsync();
    }
}

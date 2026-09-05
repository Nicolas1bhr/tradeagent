using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Fault;

// =================================================================================================
// U2c-1b — ONE SHOT, PAUSE, HUMAN.
//
// The emergency controls used to be a retry loop: a press held a nonce in memory, a second press
// repeated it, and a restart reconstructed the press from the store so the button would keep
// repeating. Six of Codex's round-3 findings were about that machinery rather than about the
// emergency — F11 (a WORKING close does not pause AI trading while the position is open), F12 (a
// definitely failed close holds the press forever), F13 (a restart drops a terminal press with a
// non-flat position), F14 (completion reads the CURRENT account, not the record's).
//
// The replacement is not a better retry. A press WRITES ITS RECORDS, sends the wire calls, and from
// that moment trading is paused; the owner resolves the records through the card; the next press is
// a fresh decision. There is no press object, no nonce to reuse and nothing to reconstruct — the
// durable records ARE the press.
//
// Every test here asserts both directions: the unsafe outcome is refused AND an ordinary press
// still reaches the wire.
// =================================================================================================
public class EmergencyPressTests
{
    /// <summary>A position that is open, with the close left resting on the book rather than filling.</summary>
    static async Task<(TradingGateway Gw, RecoveryConnector C, Database Db)> WithAnOpenPosition(decimal qty = 2m)
    {
        var (gw, c, db) = await Recovery.Ready();
        await gw.PlaceAsync(AgentContext.Operator, "pos-1", TestEnv.Buy(qty: qty));
        c.Inner.Faults.Fill = FillBehaviour.LeaveWorking;         // the close will rest, not fill
        return (gw, c, db);
    }

    static List<ExecutionRequest> PressRows(TradingGateway gw, string kind) =>
        gw.Requests.Query("request_id LIKE $p", ("$p", $"{kind}-%"));

    /// <summary>Resolves every record of a press the way the Dashboard card does.</summary>
    static void ResolveThroughTheCard(TradingGateway gw, string kind, string note = "checked in ATAS")
    {
        foreach (var r in PressRows(gw, kind).Where(r => r.NeedsReconciliation))
            gw.ForceResolve(r.RequestId, r.State, note);
    }

    // ---------------------------------------------------------------- item 1 — a press is its records

    /// <summary>
    /// F11. A market close that RESTS on the book has flattened nothing, and until the owner has
    /// been told what became of it there is an open position the software cannot account for. The
    /// old code settled it WORKING and unflagged, so `HasUnconfirmedWork` was false and the AI was
    /// authorised to trade over an emergency that had not finished.
    ///
    /// Both directions: trading is authorised before the press, and authorised again once the
    /// owner has resolved the press's records through the card.
    /// </summary>
    [Fact]
    public async Task A_working_close_from_a_press_pauses_trading_until_the_owner_resolves_it()
    {
        var (gw, c, db) = await WithAnOpenPosition();
        using var dbh = db;

        Assert.True(gw.TryAuthorizeExecution(new AgentContext("a"), out _));   // the ordinary path works

        await gw.OperatorCloseAllAsync();

        Assert.Equal(1, c.Closes);                                             // the wire WAS touched
        var rows = PressRows(gw, TradingGateway.ClosePress);
        Assert.All(rows, r => Assert.True(r.NeedsReconciliation, $"{r.RequestId} is {r.State} and unflagged"));
        Assert.True(gw.HasUnconfirmedWork());
        Assert.False(gw.TryAuthorizeExecution(new AgentContext("a"), out _));

        ResolveThroughTheCard(gw, TradingGateway.ClosePress);
        await gw.RefreshHealthAsync();
        Assert.False(gw.HasUnconfirmedWork());
        Assert.True(gw.TryAuthorizeExecution(new AgentContext("a"), out _));
    }

    /// <summary>
    /// The same rule for cancel-all, whose per-order cancels come back CANCELLED — a DEFINITE and
    /// entirely successful answer. It still pauses: the press is not over because the wire calls
    /// returned, it is over when the owner has seen what they did.
    /// </summary>
    [Fact]
    public async Task A_successful_cancel_all_press_also_pauses_until_it_is_resolved()
    {
        var (gw, c, db) = await Recovery.Ready(new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var dbh = db;
        await gw.PlaceAsync(AgentContext.Operator, "ca-1", TestEnv.Buy());

        await gw.OperatorCancelAllAsync();

        Assert.True(gw.HasUnconfirmedWork());
        Assert.False(gw.TryAuthorizeExecution(new AgentContext("a"), out _));

        ResolveThroughTheCard(gw, TradingGateway.CancelPress);
        await gw.RefreshHealthAsync();
        Assert.True(gw.TryAuthorizeExecution(new AgentContext("a"), out _));
    }

    /// <summary>
    /// F13, from the other side. The press survives a restart because it was never in memory: a new
    /// gateway over the same store reads the same flagged records and refuses to trade over them.
    /// </summary>
    [Fact]
    public async Task A_restart_finds_the_press_in_the_store_and_stays_paused()
    {
        var (gw, _, db) = await WithAnOpenPosition();
        using var dbh = db;
        var press = await gw.OperatorCloseAllAsync();

        var (fresh, _, _) = await Recovery.Ready(db: db);
        Assert.True(fresh.HasUnconfirmedWork());
        Assert.False(fresh.TryAuthorizeExecution(new AgentContext("a"), out _));
        // The same press, read out of the store rather than reconstructed into an object.
        Assert.Equal(press.Nonce, fresh.UnresolvedPressNonce(TradingGateway.ClosePress));
    }

    /// <summary>
    /// A press is its OWN business, in both directions. An unrelated unconfirmed order neither
    /// completes it nor is completed by it — the fault that let one press's records be released by
    /// somebody else's record settling, and that locked the control over an unrelated order.
    /// </summary>
    [Fact]
    public async Task A_press_is_judged_by_its_own_records_and_not_by_unrelated_work()
    {
        var (gw, c, db) = await Recovery.Ready();
        using var dbh = db;
        await gw.PlaceAsync(AgentContext.Operator, "own-pos", TestEnv.Buy("ES", 2m));

        // Something unrelated is unconfirmed — an agent order that never came back. It rests on the
        // book rather than filling, so it adds no position for the press to find.
        c.Inner.Faults.Fill = FillBehaviour.LeaveWorking;
        c.ThrowAfterPlace = new ConnectorTransportException("connection lost after the order was accepted");
        await gw.PlaceAsync(new AgentContext("a"), "own-unrelated",
            new PlaceIntent("NQ", OrderSide.Buy, OrderType.Limit, 1m, 1m, null, TimeInForce.Day, null));
        c.ThrowAfterPlace = null;
        c.Inner.Faults.Fill = FillBehaviour.FillImmediately;
        Assert.True(gw.HasUnconfirmedWork());

        var press = await gw.OperatorCloseAllAsync();     // the escape hatch still works while paused
        Assert.Single(press.Targets);
        Assert.Empty(c.Inner.Broker.Positions);

        // Its own record filled and the position is flat, so nothing but the owner's confirmation is
        // outstanding on THIS press — and the unrelated order does not add to that count.
        Assert.Equal(1, press.Unresolved);
        foreach (var t in press.Targets) gw.ForceResolve(t.RequestId, t.State, "checked in ATAS: it filled");
        Assert.True((await gw.PressOutcomeAsync(TradingGateway.ClosePress, press.Nonce)).Complete);

        // ...and resolving the press did not lift the pause the other record is holding.
        Assert.True(gw.HasUnconfirmedWork());
        Assert.False(gw.TryAuthorizeExecution(new AgentContext("a"), out _));
    }
}

// =================================================================================================
// Item 2 — a second press while one is unresolved is refused
// =================================================================================================
public class SecondPressRefusedTests
{
    /// <summary>
    /// F12, and its opposite. A press that is still the owner's refuses the next one — with the
    /// sentence that says which control and when — and a press that has been resolved does not.
    ///
    /// The old machinery reused the nonce instead: a second press found the first press's terminal
    /// row through `TryCreate` and sent nothing, FOREVER, so a definitely failed close could never
    /// be pressed past. There is no retry now, and the way out is the card.
    /// </summary>
    [Fact]
    public async Task A_second_close_all_is_refused_while_the_first_is_unresolved()
    {
        var (gw, c, db) = await Recovery.Ready();
        using var dbh = db;
        await gw.PlaceAsync(AgentContext.Operator, "sp-1", TestEnv.Buy(qty: 2m));
        c.Inner.Faults.Fill = FillBehaviour.LeaveWorking;

        await gw.OperatorCloseAllAsync();
        Assert.Equal(1, c.Closes);

        var refused = await Assert.ThrowsAsync<GatewayDeniedException>(() => gw.OperatorCloseAllAsync());
        Assert.Equal(ErrorCode.EMERGENCY_PRESS_UNRESOLVED, refused.Code);
        Assert.StartsWith("close-all sent at ", refused.Message);
        Assert.EndsWith("; resolve it first", refused.Message);
        Assert.Equal(1, c.Closes);                       // and nothing more went to the wire

        // THE OTHER DIRECTION. Resolved through the card, the next press is a fresh decision that
        // really does send — including over a close the platform DEFINITELY refused, which is the
        // shape that used to hold the control forever.
        foreach (var r in gw.Requests.Query("request_id LIKE 'op-close-%'"))
            gw.ForceResolve(r.RequestId, r.State, "checked in ATAS: the close is resting");
        c.Inner.Faults.Fill = FillBehaviour.FillImmediately;

        var second = await gw.OperatorCloseAllAsync();
        Assert.Equal(2, c.Closes);
        Assert.NotEmpty(second.Targets);
    }

    /// <summary>
    /// PER KIND, and that is deliberate: an unresolved cancel-all must never be able to stop
    /// somebody flattening a position. They are different decisions and are refused separately.
    /// </summary>
    [Fact]
    public async Task An_unresolved_cancel_all_does_not_block_close_all()
    {
        var (gw, c, db) = await Recovery.Ready(new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var dbh = db;
        await gw.PlaceAsync(AgentContext.Operator, "pk-1", TestEnv.Buy());
        c.Inner.Faults.Fill = FillBehaviour.FillImmediately;
        await gw.PlaceAsync(AgentContext.Operator, "pk-2", TestEnv.Buy("NQ", 1m));

        await gw.OperatorCancelAllAsync();
        await Assert.ThrowsAsync<GatewayDeniedException>(() => gw.OperatorCancelAllAsync());

        var closes = await gw.OperatorCloseAllAsync();     // the money-reducing button is not blocked
        Assert.Single(closes.Targets);
        Assert.Empty(c.Inner.Broker.Positions);
    }
}

// =================================================================================================
// Item 3 — per-order cancels, and a close that re-reads the position it was aimed at
// =================================================================================================
public class PressReachesTheWireOnItsOwnTermsTests
{
    /// <summary>
    /// F9. Cancel-all is one cancel per CAPTURED order. The account-wide sweep acted on orders the
    /// person never saw — including any that arrived after the press — and could be reconciled
    /// against nothing.
    /// </summary>
    [Fact]
    public async Task Cancel_all_sends_one_cancel_per_captured_order_and_no_account_wide_sweep()
    {
        var (gw, c, db) = await Recovery.Ready(new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var dbh = db;
        var a = await gw.PlaceAsync(AgentContext.Operator, "po-1", TestEnv.Buy());
        var b = await gw.PlaceAsync(AgentContext.Operator, "po-2", TestEnv.Buy("NQ"));

        var cancelled = new List<string>();
        c.OnCancelledId = cancelled.Add;

        await gw.OperatorCancelAllAsync();

        Assert.Equal(0, c.CancelAlls);                                   // the sweep is not on the wire
        Assert.Equal(2, cancelled.Count);
        Assert.Contains(a.ConnectorOrderId!, cancelled);
        Assert.Contains(b.ConnectorOrderId!, cancelled);
        Assert.DoesNotContain(c.Inner.Broker.Orders, o => o.State == ExecutionState.WORKING);
    }

    /// <summary>
    /// F10. The press captured a size and turned it into a MARKET order for that size. If the
    /// position changed in between — a fill landed, another window flattened it — that order is
    /// wrong in the direction that opens exposure, so it is not sent and the owner presses again.
    ///
    /// Both directions: a stable position still goes to the wire (every other test here relies on
    /// that), and the drifted one is named rather than silently skipped.
    /// </summary>
    [Fact]
    public async Task A_close_is_not_sent_when_the_position_changed_after_the_press()
    {
        var (gw, c, db) = await Recovery.Ready();
        using var dbh = db;
        await gw.PlaceAsync(AgentContext.Operator, "dr-1", TestEnv.Buy("ES", 2m));

        // The position halves between the capture and the wire call — the read the press does
        // immediately before sending is the one that sees it.
        var seen = 0;
        c.BeforePositionsRead = () =>
        {
            if (++seen == 2) c.Inner.Broker.Accept(new PlaceOrderCommand("BY-HAND", c.Inner.Broker.AccountId,
                "ES", OrderSide.Sell, OrderType.Market, 1m, null, null, TimeInForce.Day, null),
                FillBehaviour.FillImmediately);
        };

        var press = await gw.OperatorCloseAllAsync();

        Assert.Equal(0, c.Closes);                        // nothing was sent for it
        Assert.Empty(press.Targets);                      // and no record was written for it either
        Assert.Contains("changed after you", press.Summary);
        Assert.Contains("ES was 2 when you pressed and is 1 now", press.Summary);
        Assert.Contains(c.Inner.Broker.Positions, p => p.Symbol == "ES" && p.Quantity == 1m);
    }
}

// =================================================================================================
// Item 5 — the operator's own press gets the emergency fast path (Codex C3)
// =================================================================================================

/// <summary>
/// `RiskReducingScope` was opened by the PIPE SERVER, so only an agent's `cancel-all` got the
/// emergency bound. The button and the CLI went through `TradingGateway` directly and inherited
/// nothing: every read the press has to do first — the positions it captures, the position it checks
/// before each close — started its own ordinary deadline, and the person holding the button waited
/// out the whole of a stalled bridge.
///
/// The scope belongs where the intent is known, which is inside the emergency methods themselves.
/// Then all three callers get it, and so does every read they do on the way.
/// </summary>
public class OperatorPressIsAnEmergencyTests
{
    /// <summary>Comfortably longer than the 2 s emergency budget, so an unbounded press is obvious.</summary>
    const int StalledMs = 1200;

    /// <summary>
    /// THE PRESS IS MEASURED AGAINST ITS OWN CLOCK, NOT AGAINST THE TEST'S — and that correction is
    /// what U-press-budget is.
    ///
    /// These two tests used to assert a wall-clock ceiling on the whole call: "under 3 s", "under
    /// 4 s". A wall clock measures the fixture's injected latency and the runner's scheduling as
    /// well as the promise, and on 2026-09-05 it charged the product for both — CI run 33952871991,
    /// ubuntu-latest: "the press took 3.4s against a 2s emergency budget", green on windows and
    /// macos at the same sha and unrepeated in the ten runs since. Measured afterwards on all three
    /// hosted runners: the press's own cost, every write-ahead row and the activity line included,
    /// is 5-7 ms on ubuntu, 6-11 ms on macos, 34-40 ms on windows, and all three connector calls are
    /// on the emergency clock. There is no step of this press to make faster.
    ///
    /// So the promise is asserted instead of the stopwatch. The press opens ONE absolute deadline
    /// and hands it to every call it makes; this reads that deadline back out of the scope, from
    /// inside the press, and asserts what the press did about it: it RETURNED, it returned within a
    /// handler's own overhead of its deadline rather than of the test's start, it told the owner the
    /// outcome is NOT confirmed, and nothing it sent after the deadline reached the book. The
    /// shipped 2 s is untouched — this widens nothing, it stops measuring the wrong clock.
    /// </summary>
    static void TheStalledPressGaveUpOnItsOwnDeadline(long returnedAt, long? deadlineAt, string what)
    {
        // Non-null is itself the assertion that the read was inside the scope: a step that opened
        // its own budget, or none, is what the scope exists to prevent.
        Assert.NotNull(deadlineAt);
        var overrun = returnedAt - deadlineAt.Value;

        // What a press costs BEYOND its connector calls is a handler's overhead — local SQLite
        // writes, in `docs/CONTRACTS.md`'s own term H — and never the connector budget E, which
        // bounds the calls. Worst seen on a hosted runner: 40 ms.
        Assert.True(overrun < GatewayPipeServer.HandlerOverhead.TotalMilliseconds,
            $"{what} returned {overrun} ms after the deadline the press itself opened, against " +
            $"{GatewayPipeServer.HandlerOverhead.TotalSeconds:0}s of handler overhead; the emergency budget was not the thing that ran out");
    }

    [Fact]
    public async Task Close_all_gives_up_on_a_stalled_platform_inside_the_emergency_budget()
    {
        var (gw, c, db) = await Recovery.Ready();
        using var dbh = db;
        await gw.PlaceAsync(AgentContext.Operator, "st-1", TestEnv.Buy("ES", 2m));

        // The bridge stalls only now, so the setup above is not the thing being measured.
        c.Inner.Faults.LatencyMs = StalledMs;

        long? deadlineAt = null;
        c.BeforePositionsRead = () => deadlineAt ??= RiskReducingScope.DeadlineAt;

        var press = await gw.OperatorCloseAllAsync();
        TheStalledPressGaveUpOnItsOwnDeadline(Environment.TickCount64, deadlineAt, "close-all");

        // ...and the owner is told, in the words the card uses, rather than being told it worked.
        var row = Assert.Single(gw.Requests.Query("request_id LIKE 'op-close-%'"));
        Assert.Equal(ExecutionState.UNKNOWN, row.State);
        Assert.True(row.NeedsReconciliation);
        Assert.Contains(press.Targets, t => t.Outcome == "not confirmed — check ATAS");
        Assert.False(gw.TryAuthorizeExecution(new AgentContext("a"), out _));
    }

    /// <summary>
    /// THE READ BEFORE THE CLOSE IS PART OF THE EMERGENCY, not a prelude to it. This is the half the
    /// connector cannot classify for itself: `positions` is an ordinary RPC whatever it is nested in,
    /// and the scope is the only thing that says otherwise.
    /// </summary>
    [Fact]
    public async Task The_position_read_before_the_close_inherits_the_scope()
    {
        var (gw, c, db) = await Recovery.Ready();
        using var dbh = db;
        await gw.PlaceAsync(AgentContext.Operator, "st-2", TestEnv.Buy("ES", 2m));

        var deadlines = new List<long?>();
        c.BeforePositionsRead = () => deadlines.Add(RiskReducingScope.DeadlineAt);

        await gw.OperatorCloseAllAsync();

        Assert.NotEmpty(deadlines);
        Assert.All(deadlines, d => Assert.NotNull(d));
        // One deadline for the whole press, not a fresh budget per read: that is what stops the
        // promise scaling with the number of positions.
        Assert.Single(deadlines.Distinct());
    }

    /// <summary>
    /// THE OTHER DIRECTION. A healthy platform is not slowed down or refused by any of this: the
    /// scope only ever WIDENS urgency, and the ordinary press still closes the position.
    /// </summary>
    [Fact]
    public async Task A_healthy_press_is_untouched_by_the_scope()
    {
        var (gw, c, db) = await Recovery.Ready();
        using var dbh = db;
        await gw.PlaceAsync(AgentContext.Operator, "st-3", TestEnv.Buy("ES", 2m));

        var press = await gw.OperatorCloseAllAsync();

        Assert.Single(press.Targets);
        Assert.Equal(ExecutionState.FILLED, press.Targets[0].State);
        Assert.Empty(c.Inner.Broker.Positions);
    }

    [Fact]
    public async Task Cancel_all_gives_up_on_a_stalled_platform_inside_the_emergency_budget()
    {
        var (gw, c, db) = await Recovery.Ready(new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var dbh = db;
        await gw.PlaceAsync(AgentContext.Operator, "st-4", TestEnv.Buy());
        c.Inner.Faults.LatencyMs = StalledMs;

        long? deadlineAt = null;
        c.BeforePositionsRead = () => deadlineAt ??= RiskReducingScope.DeadlineAt;

        var press = await gw.OperatorCancelAllAsync();
        TheStalledPressGaveUpOnItsOwnDeadline(Environment.TickCount64, deadlineAt, "cancel-all");

        // AND IT SENT NOTHING AFTER THE BUDGET. The cancel's turn came with less than its latency
        // left, so it was stopped mid-call and never reached the book: the order is still working,
        // the owner is told the outcome is not confirmed, and trading stays paused over it. Without
        // this, a press that cancelled everything a second past the promise would pass a test named
        // for giving up inside it.
        Assert.Contains(c.Inner.Broker.Orders, o => !OrderStateMachine.IsTerminal(o.State));
        Assert.Contains(press.Targets, t => t.Outcome == "not confirmed — check ATAS");
        Assert.False(gw.TryAuthorizeExecution(new AgentContext("a"), out _));
    }

    /// <summary>
    /// THE ONE THING THAT CAN STILL TURN A RUNNER'S LATENESS INTO A PRODUCT OVERRUN, and it was the
    /// instrument rather than the press.
    ///
    /// `FakeConnector` decided whether its injected latency fitted in what was left of the budget
    /// and then slept the whole of it — a PREDICTION, correct only while the machine delivers a
    /// timer on time. Under a runner that delivers it late the prediction is wrong and nothing
    /// re-checks: the simulator answers past the deadline it had just promised to honour, having
    /// never taken the expired branch, and the press it is measuring is charged for the lateness.
    /// The shipped `AtasConnector` clips instead — `Left(deadlineAt)` on the write, `CancelAfter` on
    /// the reply — so this is the simulator being held to what the real one does.
    ///
    /// Both directions: the late wait is stopped at the deadline AND a wait that runs late but still
    /// finishes inside the budget is not disturbed.
    /// </summary>
    [Fact]
    public async Task A_wait_the_simulator_predicted_would_fit_is_still_stopped_by_the_deadline()
    {
        var (gw, c, db) = await Recovery.Ready(new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var dbh = db;
        await gw.PlaceAsync(AgentContext.Operator, "st-5", TestEnv.Buy());

        // 1.2 s of declared latency delivered 2.2 s late, which is what it takes to reproduce the
        // number ubuntu measured at 88617a0: the orders read answers at 3.4 s against a 2 s budget
        // the simulator had already decided 1.2 s would fit inside. The substitute still honours the
        // token — this is a LATE wait, not an uninterruptible one.
        c.Inner.Faults.LatencyMs = StalledMs;
        c.Inner.Faults.Wait = (d, ct) => Task.Delay(d + TimeSpan.FromMilliseconds(2200), ct);

        long? deadlineAt = null;
        c.BeforePositionsRead = () => deadlineAt ??= RiskReducingScope.DeadlineAt;

        var press = await gw.OperatorCancelAllAsync();
        TheStalledPressGaveUpOnItsOwnDeadline(Environment.TickCount64, deadlineAt, "a press whose simulator ran late");
        Assert.Contains(press.Targets, t => t.Outcome == "not confirmed — check ATAS");

        // THE OTHER DIRECTION. A wait that runs late and still lands inside the budget is left
        // alone — the deadline only ever stops a wait, it never shortens a healthy one.
        var (gw2, c2, db2) = await Recovery.Ready();
        using var dbh2 = db2;
        await gw2.PlaceAsync(AgentContext.Operator, "st-6", TestEnv.Buy("ES", 2m));
        c2.Inner.Faults.LatencyMs = 100;
        c2.Inner.Faults.Wait = (d, ct) => Task.Delay(d + TimeSpan.FromMilliseconds(150), ct);

        var healthy = await gw2.OperatorCloseAllAsync();
        Assert.Single(healthy.Targets);
        Assert.Equal(ExecutionState.FILLED, healthy.Targets[0].State);
        Assert.Empty(c2.Inner.Broker.Positions);
    }
}

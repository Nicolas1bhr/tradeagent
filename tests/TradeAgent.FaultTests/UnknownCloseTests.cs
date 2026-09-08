using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

// =================================================================================================
// U-unknown-close — an UNKNOWN close on an instrument can no longer be doubled
//
// U-press-inflight closed the DISPATCHING half of this race and stated the half it left open:
// `Stores.TryCreateFlagged`'s `$wire` clause refuses a press leg only while another request on the
// instrument is DISPATCHING, and UNKNOWN was deliberately excluded because refusing the WHOLE press
// on it re-imposes the pause the button exists to bypass.
//
// So an agent's close that the broker ACCEPTED with the acknowledgement lost sits UNKNOWN in the
// book with its order resting at the platform. Close All reads the position — still long 2, because
// nothing has filled — sizes a market sell 2 and sends it; both fill; long 2 becomes SHORT 2, after
// the owner pressed the control whose whole purpose is to flatten. The agent's own second `close`
// doubles the same way: `RefuseAStaleCloseOrThrow` compares the position to what this close was
// sized from, and while the first close is still resting those two agree.
//
// The fix is per LEG and never per press: the press SETTLES the unresolved order before it sends —
// reads it back by client order id, cancels it if it is still working, and only then closes on the
// re-read position — and refuses that one leg when it cannot, while every other instrument still
// closes. The agent's close and the agent's reduce are refused outright with CLOSE_UNRESOLVED.
//
// NOT IN `Timing`. Every test here judges what the press DID rather than how long it took, and the
// one that runs the deadline out does it with 2 × 1200 ms of declared latency inside a 2000 ms
// budget: a slow runner only makes the deadline pass sooner, and no runner can make two 1.2 s waits
// fit in 2 s. There is no wall clock in an assertion below.
//
// THAT LAST SENTENCE WAS NOT TRUE OF THE FIXTURE, and U-press-settle-win is what it cost. A press
// that judges what the product DID still has to reach the step that does it, and everything between
// the deadline opening and the leg going out is durable SQLite at `synchronous=FULL`. On
// windows-latest at the U-archive-win merge (run 34187380076) the budget was gone before the leg's
// turn, the leg was refused — correctly — and the book ended `ES 2`. `PressBudget` is what takes
// the runner's disk back out of the verdict; the numbers are argued there.
// =================================================================================================

static class Unresolved
{
    /// <summary>
    /// THE OPERATION BUDGET FOR EVERY FIXTURE HERE WHOSE VERDICT IS THE BOOK RATHER THAN THE
    /// TWO-SECOND PROMISE — which is three of the four presses below, and until U-press-settle-win
    /// none of them said so.
    ///
    /// They took the simulator's default two seconds by accident, and that is a wall clock kept by
    /// the RUNNER inside the verdict of tests about what a press cancelled and what it then closed.
    /// A close-all press makes at least four durable commits between the instant its deadline opens
    /// and the instant its leg reaches the wire — the composite row, the two transitions
    /// `SettleTheUnresolved` writes, and the leg's own write-ahead insert — and a press whose
    /// deadline goes inside any of them sends nothing for that instrument and leaves a flagged
    /// record, which is the contract and is the answer that failed
    /// <see cref="PressSettlesAnUnknownCloseTests.A_press_cancels_the_unknown_close_before_it_closes_and_the_book_ends_flat"/>.
    ///
    /// MEASURED, on a throwaway harness over 24 presses of that test's own fixture, 8 per runner
    /// (U-press-settle-win, CI run 34188680175): the press reached its leg and sent it every time on
    /// every runner, so nothing here races. What varied was the budget left of the 2000 ms at the
    /// leg — ubuntu 1993-1997 ms, macos 1916-1998, windows 1687-1765 — and all of the spend was
    /// record-keeping: on windows the two settle transitions cost 78-141 ms and the leg's
    /// write-ahead 79-172, against 20 bare one-row commits on the same disk measuring med 16-47 and
    /// max 47-110 ms. `gcPause` was 0 and a 20 ms tick arrived at 32-47 ms, so the process was
    /// running: it is the disk. The outlier that closes a 2 s budget in one commit is not in that
    /// run; it is in U-press-win-3's, on the same runner image — ten bare one-row commits measured
    /// 16-2234 ms.
    ///
    /// Twenty seconds takes that clock out of the verdict entirely: no fixture here waits on
    /// anything but the press itself, and thirteen of the worst commit ever measured on this runner
    /// image still fit inside it. Nothing is loosened — an assertion that used to hold still holds,
    /// byte for byte, and the fixture that IS about the budget keeps the simulator's two seconds.
    /// </summary>
    public static readonly TimeSpan PressBudget = TimeSpan.FromSeconds(20);

    /// <summary>
    /// A long position, and an agent's close of it that the broker ACCEPTED and never acknowledged:
    /// the order is RESTING at the platform and the record is UNKNOWN.
    ///
    /// `LeaveWorking` is what makes it the dangerous shape rather than a harmless one. A close that
    /// FILLED before the acknowledgement was lost has already flattened the position, so the press
    /// finds nothing to close and there is nothing to double; a close that RESTS leaves the position
    /// open, so the press sizes a second one against it and both of them fill.
    /// </summary>
    /// <param name="alsoOpen">
    /// A second instrument, opened BEFORE the close is lost. Order matters: the lost close pauses
    /// trading, so a position opened after it would be refused by the gate rather than by anything
    /// this file is about.
    /// </param>
    /// <param name="budget">
    /// The emergency budget the press that follows will run under. Null is the simulator's own two
    /// seconds; <see cref="PressBudget"/> is what a fixture whose verdict is the book takes, and why.
    /// </param>
    public static async Task<(TradingGateway Gw, RecoveryConnector C, Database Db, ExecutionRequest Lost)> WithALostClose(
        string symbol = "ES", decimal qty = 2m, string? alsoOpen = null, TimeSpan? budget = null)
    {
        var (gw, c, db) = await Recovery.Ready(emergencyBudget: budget);
        c.SortPositionsBySymbol = true;
        await gw.PlaceAsync(new AgentContext("ai"), $"uc-open-{symbol}", TestEnv.Buy(symbol, qty));
        if (alsoOpen is not null)
            await gw.PlaceAsync(new AgentContext("ai"), $"uc-open-{alsoOpen}", TestEnv.Buy(alsoOpen, qty));

        c.Inner.Faults.Fill = FillBehaviour.LeaveWorking;      // the close RESTS at the broker
        c.Inner.Faults.DropAfterBrokerAccept = 1;              // ...and the acknowledgement is lost
        var lost = await gw.CloseAsync(new AgentContext("ai"), $"uc-lost-{symbol}", symbol);
        c.Inner.Faults.Fill = FillBehaviour.FillImmediately;

        Assert.Equal(ExecutionState.UNKNOWN, lost!.State);
        return (gw, c, db, lost);
    }

    /// <summary>What the owner's card does when they have looked and still cannot say what happened.</summary>
    public static async Task ResolveWithoutAnOutcome(TradingGateway gw, string requestId)
    {
        gw.ForceResolve(requestId, ExecutionState.UNKNOWN, "checked in ATAS: still cannot tell");
        await gw.RefreshHealthAsync();
    }

    public static string Book(RecoveryConnector c) =>
        string.Join(" | ", c.Inner.Broker.Orders.Select(o => $"{o.ConnectorOrderId} {o.Side} {o.Quantity} {o.State}"));

    public static async Task<string> Pos(RecoveryConnector c) =>
        string.Join(" ", (await c.GetPositionsAsync(c.Inner.Broker.AccountId)).Select(p => $"{p.Symbol} {p.Quantity}"));
}

// =================================================================================================
// Item 1 — the press settles the unresolved order before it sends, per leg
// =================================================================================================

public class PressSettlesAnUnknownCloseTests(ITestOutputHelper Out)
{
    /// <summary>
    /// (a) THE REVERSAL, AND ITS OPPOSITE. The agent's close is resting at the broker and its record
    /// is UNKNOWN; the press reads that order back, cancels it, settles the record from the
    /// platform's own answer, and only then closes what is actually there.
    ///
    /// The book is filled afterwards the way a real one would fill a resting market order — which is
    /// the step that produces the reversal when the guard is not there, and is a no-op once the
    /// order has been cancelled.
    ///
    /// <see cref="Unresolved.PressBudget"/>: the verdict is the flat book, so the press must reach
    /// its leg, and on the default two seconds that took a Windows runner's disk fitting four
    /// durable commits inside them.
    /// </summary>
    [Fact]
    public async Task A_press_cancels_the_unknown_close_before_it_closes_and_the_book_ends_flat()
    {
        var (gw, c, db, lost) = await Unresolved.WithALostClose(budget: Unresolved.PressBudget);
        using var dbh = db;

        Out.WriteLine($"before the press        : {await Unresolved.Pos(c)} — {Unresolved.Book(c)}");

        var press = await gw.OperatorCloseAllAsync();
        Out.WriteLine($"press                   : {press.Summary}");

        // A real book fills a resting market order. If the press left it working, this is the fill
        // that reverses the position.
        var resting = c.Inner.Broker.Orders.First(o => o.ClientOrderId == lost.ClientOrderId);
        c.Inner.Broker.FillWorking(resting.ConnectorOrderId);

        Out.WriteLine($"the lost close's record : {gw.GetRequest(lost.RequestId)!.State}");
        Out.WriteLine($"orders at the broker    : {Unresolved.Book(c)}");
        Out.WriteLine($"position at the end     : {await Unresolved.Pos(c)}");

        // FLAT, and exactly one closing order actually filled.
        Assert.DoesNotContain(await c.GetPositionsAsync(c.Inner.Broker.AccountId), p => p.Symbol == "ES" && p.Quantity != 0);
        Assert.Equal(1, c.Inner.Broker.Orders.Count(o => o.Side == OrderSide.Sell && o.State == ExecutionState.FILLED));

        // The unresolved record was settled from the platform, not left for the position to hit.
        Assert.Equal(ExecutionState.CANCELLED, gw.GetRequest(lost.RequestId)!.State);
        Assert.False(gw.GetRequest(lost.RequestId)!.NeedsReconciliation);

        // ...and the press itself is still the owner's to resolve.
        Assert.Single(press.Targets);
        Assert.True(gw.HasUnconfirmedWork());
        await gw.DisposeAsync();
    }

    /// <summary>
    /// (b) THE UNANSWERABLE CASE, AND THE HALF OF THE PRESS THAT STILL HAPPENS. The platform will not
    /// serve the history the unresolved order would be read out of, so the press cannot settle it and
    /// cannot know whether it is live — and a close sent on top of it could reverse the position. ES
    /// is refused, ES's row is still written so the card names it, and NQ is still closed.
    ///
    /// <see cref="Unresolved.PressBudget"/>: NQ's close is the half of the verdict that has to reach
    /// the wire, and it is the SECOND leg — behind ES's refusal and its flagged row.
    /// </summary>
    [Fact]
    public async Task A_leg_whose_unknown_close_cannot_be_read_is_refused_and_the_other_instrument_is_closed()
    {
        var (gw, c, db, lost) = await Unresolved.WithALostClose(alsoOpen: "NQ", budget: Unresolved.PressBudget);
        using var dbh = db;

        c.Inner.Faults.HideOrderHistory = true;      // the read the settle needs cannot be served
        var press = await gw.OperatorCloseAllAsync();
        Out.WriteLine($"press                   : {press.Summary}");
        Out.WriteLine($"orders at the broker    : {Unresolved.Book(c)}");

        c.Inner.Faults.HideOrderHistory = false;
        Out.WriteLine($"position at the end     : {await Unresolved.Pos(c)}");

        // Nothing was sent for ES and the position is untouched.
        Assert.Equal(1, c.Closes);
        Assert.Contains(await c.GetPositionsAsync(c.Inner.Broker.AccountId), p => p.Symbol == "ES" && p.Quantity == 2m);
        Assert.DoesNotContain(await c.GetPositionsAsync(c.Inner.Broker.AccountId), p => p.Symbol == "NQ" && p.Quantity != 0);

        // The card names the instrument, and the row for it says nothing was sent.
        var es = Assert.Single(press.Targets, t => t.Target == "ES");
        Assert.Equal(ExecutionState.CREATED, es.State);
        Assert.False(es.Resolved);
        Assert.Contains("ES", press.Summary);
        Assert.Contains("may still be open", press.Summary);
        Assert.Contains(lost.RequestId, press.Summary);
        Assert.Equal(ExecutionState.FILLED, Assert.Single(press.Targets, t => t.Target == "NQ").State);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// (b, second reading) THE READ THAT RUNS THE PRESS'S OWN DEADLINE OUT. Same refusal, reached the
    /// other way: the platform is stalled, so the settle's read is stopped by the deadline the press
    /// opened rather than answered. Nothing is sent, and the leg is refused rather than left to guess.
    ///
    /// THE ONE FIXTURE HERE THAT KEEPS THE SIMULATOR'S TWO SECONDS, because the budget is what it is
    /// about: 2 × 1200 ms of declared latency cannot fit in 2000 ms on any runner, and a slower one
    /// only makes the deadline pass sooner. <see cref="Unresolved.PressBudget"/> would delete it.
    /// </summary>
    [Fact]
    public async Task A_settle_that_runs_out_of_the_presss_deadline_refuses_the_leg_and_sends_nothing()
    {
        var (gw, c, db, lost) = await Unresolved.WithALostClose();
        using var dbh = db;

        // 1.2 s a call against the 2 s emergency budget: the captured-positions read and the settle's
        // order read cannot both fit, whatever the runner is doing.
        c.Inner.Faults.LatencyMs = 1200;

        var press = await gw.OperatorCloseAllAsync();
        Out.WriteLine($"press                   : {press.Summary}");
        c.Inner.Faults.LatencyMs = 0;
        Out.WriteLine($"orders at the broker    : {Unresolved.Book(c)}");
        Out.WriteLine($"position at the end     : {await Unresolved.Pos(c)}");

        Assert.Equal(0, c.Closes);
        Assert.Contains(await c.GetPositionsAsync(c.Inner.Broker.AccountId), p => p.Symbol == "ES" && p.Quantity == 2m);
        Assert.Contains(lost.RequestId, press.Summary);
        Assert.Equal(ExecutionState.CREATED, Assert.Single(press.Targets).State);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE OTHER DIRECTION, and it is the one this guard must not break. An UNKNOWN record on the
    /// instrument that would move the position the OTHER way — an unconfirmed BUY under a long — is
    /// not something a sell can double, and the press closes exactly as it always did. It is the
    /// shape `Confirming_one_outcome_does_not_lift_another_requests_pause` presses over.
    ///
    /// <see cref="Unresolved.PressBudget"/>: "closes exactly as it always did" is a close on the
    /// wire, so this fixture needs the press to reach its leg for the same reason the first one does.
    /// </summary>
    [Fact]
    public async Task An_unknown_order_on_the_other_side_does_not_hold_the_press_up()
    {
        var (gw, c, db) = await Recovery.Ready(emergencyBudget: Unresolved.PressBudget);
        using var dbh = db;
        await gw.PlaceAsync(new AgentContext("ai"), "os-open", TestEnv.Buy("ES", 2m));

        c.Inner.Faults.Fill = FillBehaviour.LeaveWorking;
        c.ThrowAfterPlace = new ConnectorTransportException("connection lost after the order was accepted");
        var buy = await gw.PlaceAsync(new AgentContext("ai"), "os-buy",
            new PlaceIntent("ES", OrderSide.Buy, OrderType.Limit, 1m, 1m, null, TimeInForce.Day, null));
        c.ThrowAfterPlace = null;
        c.Inner.Faults.Fill = FillBehaviour.FillImmediately;
        Assert.Equal(ExecutionState.UNKNOWN, buy.State);

        var press = await gw.OperatorCloseAllAsync();
        Out.WriteLine($"press                   : {press.Summary}");

        Assert.Equal(1, c.Closes);
        Assert.Equal(ExecutionState.FILLED, Assert.Single(press.Targets).State);
        Assert.DoesNotContain(await c.GetPositionsAsync(c.Inner.Broker.AccountId), p => p.Symbol == "ES" && p.Quantity != 0);
        await gw.DisposeAsync();
    }
}

// =================================================================================================
// Item 2 — the agent's own close, and its reduce, are refused while an UNKNOWN one is unsettled
//
// NOT AT RISK FROM THE RUNNER'S DISK, and swept under U-press-settle-win's marks to say so rather
// than assumed. No test in this class presses anything: they call `CloseAsync` and `PlaceAsync`
// directly, which open no `RiskReducingScope`, so `RiskReducingScope.DeadlineAt` is null throughout
// and `FakeConnector.HonourTheOperationDeadline` returns before it waits on anything. There is no
// operation deadline for a slow commit to run into, and none of these fixtures declares a latency.
// The three whose verdict is a refusal never reach the wire at all; the two closes that do reach it
// (`An_opening_order_and_another_instrument_are_untouched`,
// `An_outcome_for_the_unknown_record_lifts_the_refusal`) are ordinary agent orders under no clock.
// So they keep the simulator's default budget: giving them `PressBudget` would change nothing and
// would say, falsely, that something here depends on it.
// =================================================================================================

public class AgentCloseOverAnUnknownCloseTests(ITestOutputHelper Out)
{
    /// <summary>
    /// (c) TWO CLOSES IN THE BOOK, REFUSED. The owner has confirmed the unconfirmed card WITHOUT
    /// being able to say what happened — the one route by which an UNKNOWN record stops pausing
    /// trading while still being UNKNOWN — so the gate lets the agent through and the position is
    /// unchanged, which is exactly what makes the stale-close check agree with it.
    /// </summary>
    [Fact]
    public async Task The_agents_second_close_is_refused_while_the_first_is_still_unknown()
    {
        var (gw, c, db, lost) = await Unresolved.WithALostClose();
        using var dbh = db;
        await Unresolved.ResolveWithoutAnOutcome(gw, lost.RequestId);
        Assert.True(gw.TryAuthorizeExecution(new AgentContext("ai"), out _));   // the gate is open again

        var refused = await Assert.ThrowsAsync<GatewayDeniedException>(
            () => gw.CloseAsync(new AgentContext("ai"), "uc-second", "ES"));
        Out.WriteLine($"agent close             : {refused.Code} — {refused.Message}");
        Out.WriteLine($"orders at the broker    : {Unresolved.Book(c)}");

        Assert.Equal(ErrorCode.CLOSE_UNRESOLVED, refused.Code);
        Assert.Contains(lost.RequestId, refused.Message);
        Assert.Equal(2, c.Inner.Broker.Orders.Count);        // the open and the first close. No second one.
        Assert.Null(gw.GetRequest("uc-second"));
        await gw.DisposeAsync();
    }

    /// <summary>
    /// A REDUCE THAT IS NOT A CLOSE OBEYS THE SAME RULE. It is sized against the same position and
    /// the unresolved order moves it the same way, so a sell under a long is refused whether or not
    /// the agent called it a close.
    /// </summary>
    [Fact]
    public async Task An_agents_reduce_on_the_same_side_is_refused_too()
    {
        var (gw, c, db, lost) = await Unresolved.WithALostClose();
        using var dbh = db;
        await Unresolved.ResolveWithoutAnOutcome(gw, lost.RequestId);

        var refused = await Assert.ThrowsAsync<GatewayDeniedException>(
            () => gw.PlaceAsync(new AgentContext("ai"), "uc-reduce",
                new PlaceIntent("ES", OrderSide.Sell, OrderType.Market, 1m, null, null, TimeInForce.Day, null)));
        Out.WriteLine($"agent reduce            : {refused.Code} — {refused.Message}");

        Assert.Equal(ErrorCode.CLOSE_UNRESOLVED, refused.Code);
        Assert.Equal(2, c.Inner.Broker.Orders.Count);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// BOTH OTHER DIRECTIONS, because a guard that refused these would be useless. An order that can
    /// only ADD to the position is not sized from it and cannot be doubled by the unresolved one; and
    /// another instrument is another book entirely.
    /// </summary>
    [Fact]
    public async Task An_opening_order_and_another_instrument_are_untouched()
    {
        var (gw, c, db, lost) = await Unresolved.WithALostClose();
        using var dbh = db;
        await Unresolved.ResolveWithoutAnOutcome(gw, lost.RequestId);

        var added = await gw.PlaceAsync(new AgentContext("ai"), "uc-add", TestEnv.Buy("ES", 1m));
        Assert.Equal(ExecutionState.FILLED, added.State);

        await gw.PlaceAsync(new AgentContext("ai"), "uc-nq", TestEnv.Buy("NQ", 1m));
        var closed = await gw.CloseAsync(new AgentContext("ai"), "uc-nq-close", "NQ");
        Out.WriteLine($"NQ close                : {closed?.State.ToString() ?? "null"}");
        Assert.Equal(ExecutionState.FILLED, closed!.State);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// AND THE WAY OUT IS REAL. An outcome for the unresolved record — the owner saying on the card
    /// what the platform showed them — lifts the refusal, and the close goes out as it always did.
    /// A refusal with no route out of it would be a worse defect than the one it prevents.
    /// </summary>
    [Fact]
    public async Task An_outcome_for_the_unknown_record_lifts_the_refusal()
    {
        var (gw, c, db, lost) = await Unresolved.WithALostClose();
        using var dbh = db;
        await Unresolved.ResolveWithoutAnOutcome(gw, lost.RequestId);
        await Assert.ThrowsAsync<GatewayDeniedException>(
            () => gw.CloseAsync(new AgentContext("ai"), "uc-blocked", "ES"));

        // The owner looked again and can now say what happened to it.
        c.Inner.Broker.Cancel(c.Inner.Broker.Orders.First(o => o.ClientOrderId == lost.ClientOrderId).ConnectorOrderId);
        gw.ForceResolve(lost.RequestId, ExecutionState.CANCELLED, "checked in ATAS: it never worked");
        await gw.RefreshHealthAsync();

        var closed = await gw.CloseAsync(new AgentContext("ai"), "uc-after", "ES");
        Out.WriteLine($"agent close after        : {closed?.State.ToString() ?? "null"}");
        Assert.Equal(ExecutionState.FILLED, closed!.State);
        Assert.DoesNotContain(await c.GetPositionsAsync(c.Inner.Broker.AccountId), p => p.Symbol == "ES" && p.Quantity != 0);
        await gw.DisposeAsync();
    }
}

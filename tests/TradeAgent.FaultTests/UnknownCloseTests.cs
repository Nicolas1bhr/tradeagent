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
// =================================================================================================

static class Unresolved
{
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
    public static async Task<(TradingGateway Gw, RecoveryConnector C, Database Db, ExecutionRequest Lost)> WithALostClose(
        string symbol = "ES", decimal qty = 2m, string? alsoOpen = null)
    {
        var (gw, c, db) = await Recovery.Ready();
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
    /// </summary>
    [Fact]
    public async Task A_press_cancels_the_unknown_close_before_it_closes_and_the_book_ends_flat()
    {
        var (gw, c, db, lost) = await Unresolved.WithALostClose();
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
    /// </summary>
    [Fact]
    public async Task A_leg_whose_unknown_close_cannot_be_read_is_refused_and_the_other_instrument_is_closed()
    {
        var (gw, c, db, lost) = await Unresolved.WithALostClose(alsoOpen: "NQ");
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
    /// </summary>
    [Fact]
    public async Task An_unknown_order_on_the_other_side_does_not_hold_the_press_up()
    {
        var (gw, c, db) = await Recovery.Ready();
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

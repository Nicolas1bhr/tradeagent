using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

// =================================================================================================
// U-press-inflight — a close is never sized from a position an order already on the wire is moving
//
// REVIEW 2026-09-05b finding 2 (probes P6/P6b) and Codex F3 are the same class, reached from the two
// ends of one race. The emergency press's atomicity covered PRESS ROWS and its drift re-read
// compared POSITIONS; an agent's `close` is an ordinary `execution_request` inside the connector
// call, so the re-read still showed ES 2, the press captured 2 and sent a market sell 2 while the
// agent's sell 2 was on the wire. Long 2 became SHORT 2 after the owner pressed the control whose
// whole purpose is to flatten.
//
// The fix is one statement in each direction over the set the gateway already keeps:
//   - the press's write-ahead insert refuses a leg while a DISPATCHING request that can move the
//     position exists on that instrument — an order a handler is inside the connector call for
//     (item 1);
//   - the agent's own close re-reads the position at dispatch, inside the gate, and is refused when
//     it moved (item 2).
// =================================================================================================
//
// -------------------------------------------------------------------------------------------------
// THE BUDGET EVERY PRESS BELOW RUNS UNDER, and why it is not the simulator's default two seconds.
//
// windows-latest went red on `CancelAllAgainstOpenWorkTests.An_agent_modify_inside_the_connector_
// call_does_not_survive_the_cancel_all_press` in CI run 34771155930, at the `U-referee-2` merge
// `45719b2` — a sha that touched nothing on the order path — with `Assert.Empty() Failure:
// Collection was not empty / Collection: [OrderInfo { ConnectorOrderId = FB-1 ... State = WORKING }]`
// and the test's own `press: 1 of 1 record(s) from this press are still waiting for you` /
// `working at the end: 1`. ONE record, and it is the press's own write-ahead row with no leg row
// behind it: `OperatorCancelAllAsync` opens its deadline, commits that row, and its `GetOrdersAsync`
// was then refused because the budget had already gone — "TradeAgent could not read your working
// orders, so nothing was cancelled". The press refused honestly; the fixture's verdict is the book.
//
// JUDGED RATHER THAN ASSUMED, on this Mac in Release, by cutting this fixture's budget to what the
// runner's disk leaves: 100, 50, 20, 10 and 5 ms all PASS — this disk carries the press from its
// deadline to the wire in under 5 ms — and 1 ms reproduces the CI's assertion and all five of its
// standard-output lines byte for byte, with the press's single row UNKNOWN and unresolved. The disk
// is the third appearance of `U-press-win-3`'s finding: one `synchronous=FULL` commit on
// windows-latest has been measured at 2234 ms, which is a whole emergency budget inside one write.
//
// So every fixture here takes `Unresolved.PressBudget` (20 s) — or `Unresolved.PressBudgetFor(legs)`
// where the press's legs have been counted — whose argument and measurements are
// written at its declaration. NO FIXTURE IN THIS FILE HAS THE TWO-SECOND PROMISE AS ITS VERDICT —
// not one asserts a duration, a deadline, or a refusal at one — so none is left on the default, and
// nothing here joins `Timing`: no verdict below needs the RUNNER to keep a wall clock.
// -------------------------------------------------------------------------------------------------

static class InFlight
{
    public static async Task<string> Pos(ITradingConnector c, string accountId) =>
        string.Join(" ", (await c.GetPositionsAsync(accountId)).Select(p => $"{p.Symbol} {p.Quantity}"));
}

// =================================================================================================
// Item 1 — a press leg is refused while open work exists against its instrument
// =================================================================================================

public class PressWaitsOnOpenWorkTests(ITestOutputHelper Out)
{
    /// <summary>
    /// P6, LIFTED. Same setup, opposite assertion: the press must send NOTHING for an instrument the
    /// gateway itself has an order on the wire for, the agent's close must be the only close that
    /// reaches the broker, and the position must end FLAT rather than reversed.
    ///
    /// <see cref="Unresolved.PressBudget"/>: the verdict is the book and the summary the press wrote,
    /// so the press has to reach the leg that waits. A budget that goes in the capture read throws
    /// out of the press instead, and the sentence this asserts is never composed.
    /// </summary>
    [Fact]
    public async Task An_agent_close_in_flight_stops_the_press_sending_a_second_close_for_that_instrument()
    {
        var (gw, c, db, _) = await Stranded.Ready(emergencyBudget: Unresolved.PressBudget);
        using var dbh = db;

        await gw.PlaceAsync(new AgentContext("ai"), "p6-open", TestEnv.Buy(qty: 2m));
        Out.WriteLine($"position before         : {await InFlight.Pos(c, c.Inner.Broker.AccountId)}");

        // The agent's own close, held inside the connector call.
        var release = new TaskCompletionSource();
        c.HangPlaceBeforeTheBroker = release;
        var agentClose = gw.CloseAsync(new AgentContext("ai"), "p6-agent", "ES");
        await c.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Out.WriteLine($"agent close             : on the wire, record {gw.GetRequest("p6-agent")?.State.ToString() ?? "none"}");

        var press = await gw.OperatorCloseAllAsync();
        Out.WriteLine($"press                   : {press.Summary}");
        Out.WriteLine($"orders after the press  : {c.Inner.Broker.Orders.Count}");

        release.SetResult();
        var leg = await agentClose;
        Out.WriteLine($"agent close answered    : {leg?.State.ToString() ?? "null"}");
        Out.WriteLine($"orders at the broker    : {c.Inner.Broker.Orders.Count} " +
                      string.Join(" ", c.Inner.Broker.Orders.Select(o => $"{o.ConnectorOrderId} {o.Side} {o.Quantity} {o.State}")));
        Out.WriteLine($"position at the end     : {await InFlight.Pos(c, c.Inner.Broker.AccountId)}");

        // The open and the agent's close. Nothing from the press.
        Assert.Equal(2, c.Inner.Broker.Orders.Count);
        Assert.Equal(ExecutionState.FILLED, leg!.State);
        Assert.DoesNotContain(await c.GetPositionsAsync(c.Inner.Broker.AccountId), p => p.Symbol == "ES" && p.Quantity != 0);
        Assert.Contains("1 leg waited on an order still on the wire", press.Summary);
        Assert.Contains("p6-agent", press.Summary);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// P6b, LIFTED UNCHANGED. The other direction still holds: with the press's flagged row already
    /// on disk, `U-gates`' re-check at the dispatch gate refuses the agent's close.
    ///
    /// <see cref="Unresolved.PressBudget"/>: the press's own close is one of the two orders counted
    /// at the end, so this fixture needs the press to reach the wire, not merely to record itself.
    /// </summary>
    [Fact]
    public async Task A_press_row_on_disk_still_refuses_the_agents_close()
    {
        var (gw, c, db, _) = await Stranded.Ready(emergencyBudget: Unresolved.PressBudget);
        using var dbh = db;

        await gw.PlaceAsync(new AgentContext("ai"), "p6b-open", TestEnv.Buy(qty: 2m));
        var press = await gw.OperatorCloseAllAsync();
        Out.WriteLine($"press                   : {press.Summary}");
        Out.WriteLine($"orders after the press  : {c.Inner.Broker.Orders.Count}");

        var refused = await Assert.ThrowsAsync<GatewayDeniedException>(
            () => gw.CloseAsync(new AgentContext("ai"), "p6b-agent", "ES"));
        Out.WriteLine($"agent close             : {refused.Code} — {refused.Message}");
        Out.WriteLine($"orders at the end       : {c.Inner.Broker.Orders.Count}");

        Assert.Equal(ErrorCode.TRADING_PAUSED_UNRECONCILED, refused.Code);
        Assert.Equal(2, c.Inner.Broker.Orders.Count);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE GUARD IS PER INSTRUMENT, NOT PER PRESS. A press over two positions must still flatten the
    /// one nothing is in flight on — an emergency control that gives up on every symbol because one
    /// of them is busy would be a worse failure than the one this unit fixes.
    ///
    /// <see cref="Unresolved.PressBudgetFor"/> AT TWO LEGS, measured at the capture (ES and NQ):
    /// "the other instrument is still closed" is a close that reaches the broker, and NQ's leg is the
    /// second of two — the last thing a press this size does. Two legs is 34 s there against 20 s.
    /// </summary>
    [Fact]
    public async Task The_other_instruments_of_the_same_press_are_still_closed()
    {
        var (gw, c, db, _) = await Stranded.Ready(emergencyBudget: Unresolved.PressBudgetFor(2));
        using var dbh = db;

        await gw.PlaceAsync(new AgentContext("ai"), "two-es", TestEnv.Buy(qty: 2m));
        await gw.PlaceAsync(new AgentContext("ai"), "two-nq", TestEnv.Buy("NQ", 2m));

        var release = new TaskCompletionSource();
        c.HangPlaceBeforeTheBroker = release;
        var agentClose = gw.CloseAsync(new AgentContext("ai"), "two-agent", "ES");
        await c.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var press = await gw.OperatorCloseAllAsync();
        Out.WriteLine($"press                   : {press.Summary}");
        release.SetResult();
        await agentClose;
        Out.WriteLine($"position at the end     : {await InFlight.Pos(c, c.Inner.Broker.AccountId)}");

        Assert.Single(press.Targets);
        Assert.Equal("NQ", press.Targets[0].Target);
        Assert.Contains("1 leg waited on an order still on the wire", press.Summary);
        await gw.DisposeAsync();
    }
}

// =================================================================================================
// Item 3 — the same question asked of CANCEL ALL, and the answer, with the probes that settle it
//
// A close leg computes something from a reading: it turns a position into a side and a size and
// sends a MARKET order for them, so a reading that is stale by one in-flight fill makes the press
// itself add exposure — long 2 to short 2 (finding 2). A CANCEL leg computes nothing. It names an
// order the press captured and asks the platform to stop it, and there is no quantity, no side and
// no derived number anywhere in it, so there is no reading for an in-flight request to make stale.
// The two probes below run the two verbs that could race one — an agent's modify and an agent's
// cancel of the same order, each held INSIDE the connector call, which is the shape finding 2 needs
// — and neither leaves an order working, neither sends anything the owner did not ask for, and in
// both the agent's own record ends in a state that does not claim its change took effect.
//
// What is READ rather than run, and stated as read: on the shipped ATAS bridge a modify that arrives
// after the cancel is refused outright — `AtasStrategyAdapter.Modify` throws
// `AtasRejectedException("order has already finished and cannot be modified; nothing was submitted")`
// for an order in `Done` or `Failed` (`:1596`) — so it does not put a replacement order on a book the
// press has already swept. That file compiles only on Windows and nothing here executed it.
// =================================================================================================

public class CancelAllAgainstOpenWorkTests(ITestOutputHelper Out)
{
    /// <summary>
    /// A resting limit order at the broker, and a gateway whose press runs under
    /// <see cref="Unresolved.PressBudget"/>: both verdicts below are the book the press left behind —
    /// nothing working, one order, CANCELLED — and every one of them is downstream of a cancel leg
    /// that has to reach the wire. This is the fixture the windows red landed on.
    /// </summary>
    static async Task<(TradingGateway Gw, HangingReadConnector C, Database Db, ExecutionRequest Resting)> Resting(string id)
    {
        var (gw, c, db) = await SlowRead.Ready(Unresolved.PressBudget);
        c.Inner.Faults.Fill = FillBehaviour.LeaveWorking;
        var resting = await gw.PlaceAsync(new AgentContext("ai"), id,
            new PlaceIntent("ES", OrderSide.Buy, OrderType.Limit, 2m, 100m, null, TimeInForce.Day, null));
        return (gw, c, db, resting);
    }

    [Fact]
    public async Task An_agent_modify_inside_the_connector_call_does_not_survive_the_cancel_all_press()
    {
        var (gw, c, db, resting) = await Resting("c3a-open");
        using var dbh = db;

        var release = new TaskCompletionSource();
        c.HangModify = release;
        var modify = gw.ModifyAsync(new AgentContext("ai"), "c3a-modify", resting.ConnectorOrderId!, null, 101m, null);
        await c.ModifyReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Out.WriteLine($"agent modify            : on the wire, record {gw.GetRequest("c3a-modify")?.State.ToString() ?? "none"}");

        var press = await gw.OperatorCancelAllAsync();
        Out.WriteLine($"press                   : {press.Summary}");

        release.SetResult();
        try { Out.WriteLine($"agent modify answered   : {(await modify).State}"); }
        catch (Exception ex) { Out.WriteLine($"agent modify answered   : {ex.GetType().Name}: {ex.Message}"); }

        Out.WriteLine($"orders at the broker    : " +
                      string.Join(" ", c.Inner.Broker.Orders.Select(o => $"{o.ConnectorOrderId} {o.Side} {o.Quantity} {o.State}")));
        var working = await c.GetOrdersAsync(c.Inner.Broker.AccountId, false, null);
        Out.WriteLine($"working at the end      : {working.Count}");
        Out.WriteLine($"the modify's record     : {gw.GetRequest("c3a-modify")!.State}, flagged={gw.GetRequest("c3a-modify")!.NeedsReconciliation}");

        // The press did what it was pressed for, and the modify claims nothing.
        Assert.Empty(working);
        Assert.Single(c.Inner.Broker.Orders);
        Assert.Equal(ExecutionState.CANCELLED, c.Inner.Broker.Orders[0].State);
        Assert.NotEqual(ExecutionState.WORKING, gw.GetRequest("c3a-modify")!.State);
        await gw.DisposeAsync();
    }

    [Fact]
    public async Task An_agent_cancel_inside_the_connector_call_does_not_leave_the_order_working()
    {
        var (gw, c, db, resting) = await Resting("c3b-open");
        using var dbh = db;

        var release = new TaskCompletionSource();
        c.HangFirstCancel = release;
        var cancel = gw.CancelAsync(new AgentContext("ai"), "c3b-cancel", resting.ConnectorOrderId!);
        await c.CancelReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Out.WriteLine($"agent cancel            : on the wire, record {gw.GetRequest("c3b-cancel")?.State.ToString() ?? "none"}");

        var press = await gw.OperatorCancelAllAsync();
        Out.WriteLine($"press                   : {press.Summary}");

        release.SetResult();
        try { Out.WriteLine($"agent cancel answered   : {(await cancel).State}"); }
        catch (Exception ex) { Out.WriteLine($"agent cancel answered   : {ex.GetType().Name}: {ex.Message}"); }

        Out.WriteLine($"orders at the broker    : " +
                      string.Join(" ", c.Inner.Broker.Orders.Select(o => $"{o.ConnectorOrderId} {o.Side} {o.Quantity} {o.State}")));
        var working = await c.GetOrdersAsync(c.Inner.Broker.AccountId, false, null);
        Out.WriteLine($"working at the end      : {working.Count}");

        Assert.Empty(working);
        Assert.Single(c.Inner.Broker.Orders);
        await gw.DisposeAsync();
    }
}

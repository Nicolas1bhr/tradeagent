using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// THE OWNER'S CLOSE ALL ASKS THE PLATFORM ABOUT A SAME-SIDE MARKET ORDER IN FLIGHT BEFORE EACH LEG SENDS
/// (<c>U-press-close-once</c>).
///
/// <para><c>U-close-once</c> refused the agent's close while an earlier market order moving the position the same way has
/// no final answer; the emergency presses did not read that rule, so a Close all pressed while such an order rested sized a
/// second close beside it and both filled: a long 2 became a short 2 after the control whose purpose is to flatten (seat P's
/// probe at <c>bdf5affa</c>: "sells at the wire : 1 before the press, 2 after", "pos [ES -2]"). Each of the owner's legs now
/// puts every such row through <c>SettleAnOrderInFlightAsync</c> first, and its answer decides: settled with nothing filled,
/// the close as before; listed live, cancelled and asked again; another press's own row, waited on; undecided, refused and
/// told — and closed over only on a LATER press, the owner's explicit second; settled with a fill, nothing sent.</para>
///
/// <para>Over <see cref="InFlightSettleTests"/>' harness — <see cref="RecordingConnector"/> and the built-in simulator, on a
/// gateway clock the test moves, an hour behind the machine's — with the press budget sized by its legs
/// (<see cref="Unresolved.PressBudgetFor"/>): every verdict here is the book and the records, never how long a press took.
/// No venue is reached and no real money is involved.</para>
/// </summary>
public class PressCloseOnceTests(ITestOutputHelper log)
{
    static readonly AgentContext Ai = new("ai");

    /// <summary>A gateway over the simulator whose emergency budget is sized for <paramref name="legs"/>.</summary>
    internal static Task<InFlightSettleTests.Harness> Ready(int legs, Database? db = null, FakeBroker? broker = null,
        MovableClock? clock = null, bool? closesCarryTheId = null) =>
        InFlightSettleTests.Ready(db, new RecordingConnector(new FakeConnector(broker ?? new FakeBroker())
        {
            EmergencyBudget = Unresolved.PressBudgetFor(legs)
        }) { ClosesCarryTheId = closesCarryTheId }, clock);

    internal static decimal Held(RecordingConnector c, string symbol = "ES") =>
        c.Broker.Positions.FirstOrDefault(p => p.Symbol == symbol)?.Quantity ?? 0m;

    internal static string Book(RecordingConnector c) =>
        string.Join(" | ", c.Broker.Orders.Select(o => $"{o.ConnectorOrderId} {o.Side} {o.Quantity} {o.Symbol} {o.State}"));

    internal static string Pos(RecordingConnector c) =>
        string.Join(" ", c.Broker.Positions.Select(p => $"{p.Symbol} {p.Quantity}"));

    /// <summary>PRICE ARRIVES: every order still resting at the platform fills, as a real book's would.</summary>
    internal static void PriceArrives(RecordingConnector c)
    {
        foreach (var o in c.Broker.Orders.Where(o => o.State == ExecutionState.WORKING).ToList())
            c.Broker.FillWorking(o.ConnectorOrderId);
    }

    internal static int SellsFilled(RecordingConnector c) =>
        c.Broker.Orders.Count(o => o.Side == OrderSide.Sell && o.State == ExecutionState.FILLED);

    static List<ExecutionRequest> Rows(InFlightSettleTests.Harness h, string kind, string nonce) =>
        h.Gw.Requests.Query("request_id LIKE $p", ("$p", $"{kind}-{nonce}%"));

    /// <summary>
    /// THE OWNER ANSWERS ONE PRESS ON THE DASHBOARD, as the card lets him (<c>DashboardView.BuildUnconfirmedRow</c>): a row
    /// the platform answered, its own state ("Our record is right"); a leg that sent nothing, "No order exists" —
    /// <c>CANCELLED</c>. Then the health pass the card runs after it.
    /// </summary>
    static async Task AnswerOnTheCard(InFlightSettleTests.Harness h, string kind, string nonce)
    {
        foreach (var r in Rows(h, kind, nonce).Where(r => r.NeedsReconciliation))
            h.Gw.ForceResolve(r.RequestId, r.State == ExecutionState.CREATED ? ExecutionState.CANCELLED : r.State,
                "checked in ATAS (test)");
        await h.Gw.RefreshHealthAsync();
    }

    internal static string Said(TradingGateway.PressOutcome p) =>
        $"{p.Summary} [{string.Join("; ", p.Targets.Select(t => $"{t.RequestId} {t.Target} {t.State} resolved {t.Resolved}: {t.Outcome}"))}]";

    // ------------------------------------------------------------------------------------------------- (b) listed live

    /// <summary>
    /// (b) A CLOSE THE PLATFORM STILL LISTS WORKING IS CANCELLED FIRST, AND THE PRESS SENDS THE ONE CLOSE.
    ///
    /// <para>The agent's market close of a long 2 rests at the platform: <c>WORKING</c>, unflagged. The owner presses Close
    /// all. The leg asks the platform's order list about it — listed <c>WORKING</c>, a live answer — cancels it there, asks
    /// again, reads it <c>CANCELLED</c> with nothing filled and settles the row so, re-reads the position, 2, and sends its
    /// close. Price then arrives and fills whatever still rests: nothing does. One sell filled, and the account is flat.</para>
    ///
    /// <para><b>RED on the base</b>: the press sizes a second sell 2 beside the resting one and both fill — ES −2, and the
    /// agent's row still <c>WORKING</c>. <b>The mutant</b> — item 1's call removed — is the same.</para>
    /// </summary>
    [Fact]
    public async Task A_close_the_platform_still_lists_working_is_cancelled_first_and_the_press_sends_the_one_close()
    {
        var h = await Ready(legs: 1);
        using var dbh = h.Db;
        var first = await InFlightSettleTests.AWorkingClose(h, "pcb");

        var sellsBefore = h.C.Broker.Orders.Count(o => o.Side == OrderSide.Sell);
        var press = await h.Gw.OperatorCloseAllAsync();
        var sellsAfter = h.C.Broker.Orders.Count(o => o.Side == OrderSide.Sell);
        var agent = h.Gw.GetRequest(first.RequestId)!;
        var atThePlatform = h.C.Broker.Orders.Single(o => o.ClientOrderId == first.ClientOrderId).State;
        PriceArrives(h.C);

        log.WriteLine($"the press            : {Said(press)}");
        log.WriteLine($"sells at the wire    : {sellsBefore} before the press, {sellsAfter} after");
        log.WriteLine($"the agent's close    : {agent.State}, flagged {agent.NeedsReconciliation}, at the platform {atThePlatform} — {agent.LastError ?? "-"}");
        log.WriteLine($"the book             : {Book(h.C)}");
        log.WriteLine($"position at the end  : {Pos(h.C)}");

        Assert.Equal(0m, Held(h.C));
        Assert.Equal(1, SellsFilled(h.C));
        Assert.Equal(1, h.C.Closes);
        Assert.Equal(ExecutionState.CANCELLED, atThePlatform);
        Assert.Equal(ExecutionState.CANCELLED, agent.State);
        Assert.False(agent.NeedsReconciliation);
        Assert.Equal(ExecutionState.FILLED, Assert.Single(press.Targets).State);
        await h.Gw.DisposeAsync();
    }

    /// <summary>
    /// (b) AND ONE THAT WILL NOT CANCEL IS NEVER CLOSED OVER, ON ANY PRESS.
    ///
    /// <para>The same resting close, on a platform that refuses every cancel — definitely, which is the platform saying the
    /// order is still live. Press 1 asks, cancels, is refused, asks again and still reads it <c>WORKING</c>: ES is refused,
    /// with a flagged row naming the order, and nothing is sent. The owner answers that press on the Dashboard and presses
    /// again: the same, and still nothing is sent — an order the platform lists live is never closed over, however many
    /// presses. Price then arrives: the agent's own close fills, and the account is flat.</para>
    ///
    /// <para><b>RED on the base</b>: press 1 sends a close beside the resting one — two sells, and ES −2 once price arrives.</para>
    /// </summary>
    [Fact]
    public async Task A_close_the_platform_lists_live_that_will_not_cancel_is_never_closed_over_on_any_press()
    {
        var h = await Ready(legs: 1);
        using var dbh = h.Db;
        var first = await InFlightSettleTests.AWorkingClose(h, "pcl");
        h.C.Faults.RefuseCancel = 100;

        var p1 = await h.Gw.OperatorCloseAllAsync();
        var closesAfter1 = h.C.Closes;
        var es1 = p1.Targets.FirstOrDefault(t => t.Target == "ES");
        log.WriteLine($"press 1              : {Said(p1)}");
        log.WriteLine($"closes at the wire   : {closesAfter1}; position {Pos(h.C)}");

        Assert.Equal(0, closesAfter1);
        Assert.NotNull(es1);
        Assert.Equal(ExecutionState.CREATED, es1!.State);
        Assert.False(es1.Resolved);
        Assert.Contains(first.RequestId, p1.Summary, StringComparison.Ordinal);
        Assert.Contains(first.RequestId, es1.Outcome, StringComparison.Ordinal);
        Assert.Contains("answer this press on the Dashboard and press again", p1.Summary, StringComparison.Ordinal);
        Assert.Contains("answer this press on the Dashboard and press again", es1.Outcome, StringComparison.Ordinal);

        await AnswerOnTheCard(h, TradingGateway.ClosePress, p1.Nonce);
        var p2 = await h.Gw.OperatorCloseAllAsync();
        var closesAfter2 = h.C.Closes;
        log.WriteLine($"press 2              : {Said(p2)}");

        h.C.Faults.RefuseCancel = 0;
        PriceArrives(h.C);
        log.WriteLine($"the book             : {Book(h.C)}");
        log.WriteLine($"position at the end  : {Pos(h.C)}");

        Assert.Equal(0, closesAfter2);
        var es2 = Assert.Single(p2.Targets, t => t.Target == "ES");
        Assert.Equal(ExecutionState.CREATED, es2.State);
        Assert.Contains(first.RequestId, p2.Summary, StringComparison.Ordinal);
        Assert.Equal(1, SellsFilled(h.C));
        Assert.Equal(0m, Held(h.C));
        await h.Gw.DisposeAsync();
    }

    // ------------------------------------------------------------------------------------------ (c) another press's row

    /// <summary>
    /// (c) A CLOSE ANOTHER PRESS STILL HAS IN FLIGHT IS WAITED ON, AND NEVER CLOSED OVER.
    ///
    /// <para>The loss budget's flatten closed ES and its close RESTS at the platform: <c>WORKING</c>, its own row, flagged
    /// and the owner's to answer. The owner presses Close all. The ES leg waits on that row — never asks the platform about
    /// it, never cancels it, never closes beside it — writes nothing and names it. He answers the flatten on the Dashboard
    /// (the card offers one answer for a close the platform holds working: "Our record is right — it is working") and presses
    /// again: still nothing is sent beside it, and the leg names it again. Price arrives: the flatten's close fills and the
    /// account is flat.</para>
    ///
    /// <para><b>RED on the base</b>: press 1 sends a second close beside the flatten's — two sells, ES −2 once price arrives
    /// (seat P's probe: "closes=2").</para>
    /// </summary>
    [Fact]
    public async Task A_close_another_press_still_has_in_flight_is_waited_on_and_never_closed_over()
    {
        var h = await Ready(legs: 1);
        using var dbh = h.Db;
        await h.Gw.PlaceAsync(Ai, "pcc-open", TestEnv.Buy("ES", 2m));

        // THE FLATTEN, ITS CLOSE RESTING: a closure recorded for today, and the health pass that flattens it.
        h.C.Faults.Fill = FillBehaviour.LeaveWorking;
        await AClosureIsRecordedAndFlattened(h);
        h.C.Faults.Fill = FillBehaviour.FillImmediately;
        var flatten = Assert.Single(h.Gw.Requests.Query("request_id LIKE $p AND intent = 'PLACE'",
            ("$p", $"{TradingGateway.BudgetClosePress}-%")));
        log.WriteLine($"the flatten's close  : {flatten.RequestId} {flatten.State}, flagged {flatten.NeedsReconciliation}");
        Assert.Equal(ExecutionState.WORKING, flatten.State);

        var closes = h.C.Closes;
        var p1 = await h.Gw.OperatorCloseAllAsync();
        var closesAfter1 = h.C.Closes;
        log.WriteLine($"press 1              : {Said(p1)}");
        log.WriteLine($"closes at the wire   : {closes} before, {closesAfter1} after");

        Assert.Equal(closes, closesAfter1);
        Assert.Empty(p1.Targets);
        Assert.Contains(flatten.RequestId, p1.Summary, StringComparison.Ordinal);
        Assert.Contains("waiting for your answer on the Dashboard", p1.Summary, StringComparison.Ordinal);

        // HE ANSWERS THE FLATTEN ON THE DASHBOARD, and presses again.
        foreach (var kind in new[] { TradingGateway.BudgetClosePress, TradingGateway.BudgetCancelPress })
            foreach (var r in h.Gw.Requests.Query("request_id LIKE $p AND needs_reconciliation = 1", ("$p", $"{kind}-%")))
                h.Gw.ForceResolve(r.RequestId, r.State, "checked in ATAS: still working (test)");
        var p2 = await h.Gw.OperatorCloseAllAsync();
        var closesAfter2 = h.C.Closes;
        log.WriteLine($"press 2              : {Said(p2)}");

        PriceArrives(h.C);
        log.WriteLine($"the book             : {Book(h.C)}");
        log.WriteLine($"position at the end  : {Pos(h.C)}");

        Assert.Equal(closes, closesAfter2);
        Assert.Empty(p2.Targets);
        Assert.Contains(flatten.RequestId, p2.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("waiting for your answer on the Dashboard", p2.Summary, StringComparison.Ordinal);
        Assert.Contains("cancel that order at your platform and press again", p2.Summary, StringComparison.Ordinal);
        Assert.Equal(1, SellsFilled(h.C));
        Assert.Equal(0m, Held(h.C));
        await h.Gw.DisposeAsync();
    }

    /// <summary>
    /// A CLOSURE OF TODAY ON THIS PLATFORM, MODE AND ACCOUNT, written as the loss watch writes one, and the health pass
    /// whose sweep flattens a closure with no outcome beside it (<c>LossFlattenTests</c>' killed-flatten fixture).
    /// </summary>
    internal static async Task AClosureIsRecordedAndFlattened(InFlightSettleTests.Harness h)
    {
        var at = h.Clock.GetUtcNow();
        var account = h.C.Broker.AccountId;
        h.Db.SetKv(LossBreach.DayKey(account, at), Json.Write(new LossBreachRecord
        {
            Account = account, Connector = h.C.Id, Mode = TradingMode.PAPER,
            Day = LossBreach.Stamp(at), FirstSeenAt = at, ConfirmedAt = at,
            FirstPull = 1, ConfirmingPull = 2, Loss = 2_000m, DayBudget = 1_000m, Currency = "USD",
            Why = "TradeAgent closed today to new risk (test)."
        }));
        await h.Gw.RefreshHealthAsync();
        Assert.NotNull(h.Gw.FlattenToday(account));
    }

    /// <summary>
    /// THE GUARD, GREEN BEFORE AND AFTER: THE LOSS FLATTEN OVER THE AGENT'S WORKING CLOSE STILL SENDS ONE CLOSE. The app's
    /// legs cancel every working order on a position they close at the platform first (<c>CancelWorkingOrdersAsync</c>),
    /// and the owner's ask is not added to them: the agent's close is cancelled there, the flatten's close fills, price
    /// arrives and fills nothing, and the account is flat with one sell filled.
    /// </summary>
    [Fact]
    public async Task The_loss_flatten_over_the_agents_working_close_still_sends_one_close()
    {
        var h = await Ready(legs: 1);
        using var dbh = h.Db;
        var first = await InFlightSettleTests.AWorkingClose(h, "pcg");

        await AClosureIsRecordedAndFlattened(h);
        var closes = h.C.Closes;
        PriceArrives(h.C);

        log.WriteLine($"the flatten          : {h.Gw.FlattenToday(h.C.Broker.AccountId)!.Why}");
        log.WriteLine($"the book             : {Book(h.C)}");
        log.WriteLine($"position at the end  : {Pos(h.C)}");

        Assert.Equal(1, closes);
        Assert.Equal(ExecutionState.CANCELLED, h.C.Broker.Orders.Single(o => o.ClientOrderId == first.ClientOrderId).State);
        Assert.Equal(1, SellsFilled(h.C));
        Assert.Equal(0m, Held(h.C));
        await h.Gw.DisposeAsync();
    }

    // -------------------------------------------------------------------------------------------------- (d) undecided

    /// <summary>
    /// (d) AN ORDER THE PLATFORM CANNOT ACCOUNT FOR IS CLOSED OVER ONLY ON THE OWNER'S SECOND PRESS.
    ///
    /// <para>On a platform of ATAS's kind — its closes do not carry the id they are handed, so absence decides nothing — the
    /// agent's market close of a long 2 ES rests, and after a restart the platform lists neither it nor a fill under its id;
    /// the owner holds ES 2 and NQ 1. Two arms: the row still <c>WORKING</c>, unflagged (the platform's answer is
    /// <c>Unlisted</c>), and the row past the clock, handed to the owner by the sweep (<c>RECONCILING</c>, flagged).</para>
    ///
    /// <para>Press 1: ES is refused — a flagged row, nothing sent, naming the order — and NQ is closed. The owner answers that
    /// press on the Dashboard and presses again: the order is still undecided, an earlier press told him, and ES is closed
    /// over it, named. The account is flat.</para>
    ///
    /// <para><b>RED on the base</b>: press 1 closes ES beside the order it cannot account for.</para>
    /// </summary>
    [Fact]
    public async Task An_order_the_platform_cannot_account_for_is_closed_over_only_on_the_owners_second_press()
    {
        foreach (var handedOver in new[] { false, true })
        {
            var (h, first) = await AnUnlistedCloseBesideNq(handedOver ? "pcd-handed" : "pcd-unlisted");
            using var dbh = h.Db;
            if (handedOver)
            {
                h.Clock.Advance(h.Stale + TimeSpan.FromSeconds(1));
                await InFlightSettleTests.Pass(h.Gw);
            }
            var row = h.Gw.GetRequest(first.RequestId)!;
            log.WriteLine($"[{(handedOver ? "handed over" : "unlisted")}] the agent's close : {row.State}, flagged {row.NeedsReconciliation}");
            Assert.Equal(handedOver ? ExecutionState.RECONCILING : ExecutionState.WORKING, row.State);

            var p1 = await h.Gw.OperatorCloseAllAsync();
            var closesAfter1 = h.C.Closes;
            var es1 = p1.Targets.FirstOrDefault(t => t.Target == "ES");
            log.WriteLine($"  press 1            : {Said(p1)}");
            log.WriteLine($"  after press 1      : closes {closesAfter1}; position {Pos(h.C)}");

            Assert.Equal(1, closesAfter1);
            Assert.Equal(2m, Held(h.C));
            Assert.Equal(0m, Held(h.C, "NQ"));
            Assert.NotNull(es1);
            Assert.Equal(ExecutionState.CREATED, es1!.State);
            Assert.False(es1.Resolved);
            Assert.Contains(first.RequestId, es1.Outcome, StringComparison.Ordinal);
            Assert.Contains(first.RequestId, p1.Summary, StringComparison.Ordinal);
            Assert.Contains("press Close all again, and it closes ES over that order", es1.Outcome, StringComparison.Ordinal);
            Assert.Contains("ES ends the other way by up to 2", es1.Outcome, StringComparison.Ordinal);
            Assert.Contains("the position ends the other way by up to its size", p1.Summary, StringComparison.Ordinal);

            await AnswerOnTheCard(h, TradingGateway.ClosePress, p1.Nonce);
            var p2 = await h.Gw.OperatorCloseAllAsync();
            log.WriteLine($"  press 2            : {Said(p2)}");
            log.WriteLine($"  position at the end: {Pos(h.C)}");

            Assert.Equal(2, h.C.Closes);
            Assert.Equal(ExecutionState.FILLED, Assert.Single(p2.Targets, t => t.Target == "ES").State);
            Assert.Contains(first.RequestId, p2.Summary, StringComparison.Ordinal);
            Assert.Contains("Closed over", p2.Summary, StringComparison.Ordinal);
            Assert.Equal(0m, Held(h.C));
            Assert.Equal(0m, Held(h.C, "NQ"));
            await h.Gw.DisposeAsync();
        }
    }

    /// <summary>
    /// The agent's market close of a long 2 ES RESTING, then a restart onto the same database and clock, to a platform of
    /// ATAS's kind whose book lists neither that close nor a fill under its id, and holds the owner's ES 2 and NQ 1.
    /// </summary>
    static async Task<(InFlightSettleTests.Harness H, ExecutionRequest First)> AnUnlistedCloseBesideNq(string name)
    {
        var before = await Ready(legs: 2);
        var first = await InFlightSettleTests.AWorkingClose(before, name);
        await before.Gw.DisposeAsync();

        var broker = new FakeBroker();
        foreach (var (symbol, qty) in new[] { ("ES", 2m), ("NQ", 1m) })
            broker.Accept(new PlaceOrderCommand($"owner-{name}-{symbol}", broker.AccountId, symbol, OrderSide.Buy,
                OrderType.Market, qty, null, null, TimeInForce.Day, null), FillBehaviour.FillImmediately);
        return (await Ready(legs: 2, before.Db, broker, before.Clock, closesCarryTheId: false), first);
    }

    // -------------------------------------------------------------------------------------- (e) settled with a fill

    /// <summary>
    /// (e) A FILL THE POSITION READ DOES NOT SHOW YET SENDS NOTHING, AND THE NEXT PRESS CLOSES WHAT IS LEFT.
    ///
    /// <para>Under a long 2 the agent's market sell 1 rests, then fills at the platform with its update lost: the row stays
    /// <c>WORKING</c>, the book holds ES 1, and the platform's position read still answers 2 — it trails the order list
    /// (<see cref="RecordingConnector.PositionsTrail"/>: the read and the close's own sizing both answer the stale 2, as an
    /// ATAS position object that has not taken the fill would). Press 1 asks about the sell, reads it <c>FILLED</c> 1 and
    /// settles it so, and sends nothing for ES: the position changed after the press and its read may not show it yet. The
    /// read catches up; press 2 closes 1, and the account is flat.</para>
    ///
    /// <para><b>RED on the base</b>: press 1 sizes its close from the stale 2 and ES ends −1.</para>
    /// </summary>
    [Fact]
    public async Task A_fill_the_position_read_does_not_show_yet_sends_nothing_and_the_next_press_closes_what_is_left()
    {
        var h = await Ready(legs: 1);
        using var dbh = h.Db;
        await h.Gw.PlaceAsync(Ai, "pce-open", TestEnv.Buy("ES", 2m));
        h.C.Faults.Fill = FillBehaviour.LeaveWorking;
        var sell = await h.Gw.PlaceAsync(Ai, "pce-sell",
            new PlaceIntent("ES", OrderSide.Sell, OrderType.Market, 1m, null, null, TimeInForce.Day, null));
        h.C.Faults.Fill = FillBehaviour.FillImmediately;
        Assert.Equal(ExecutionState.WORKING, sell.State);

        // IT FILLS AT THE PLATFORM, ITS UPDATE IS LOST, AND THE POSITION READ STILL SAYS 2.
        h.C.PositionsTrail = h.C.Broker.Positions;
        h.C.Broker.FillWorking(sell.ConnectorOrderId!);

        var p1 = await h.Gw.OperatorCloseAllAsync();
        var row = h.Gw.GetRequest(sell.RequestId)!;
        log.WriteLine($"press 1              : {Said(p1)}");
        log.WriteLine($"the agent's sell     : {row.State}, filled {row.FilledQuantity} — {row.LastError ?? "-"}");
        log.WriteLine($"after press 1        : closes {h.C.Closes}; the book {Pos(h.C)}");

        Assert.Equal(0, h.C.Closes);
        Assert.Equal(1m, Held(h.C));
        Assert.Empty(p1.Targets);
        Assert.Equal(ExecutionState.FILLED, row.State);
        Assert.Equal(1m, row.FilledQuantity);
        Assert.Contains(sell.RequestId, p1.Summary, StringComparison.Ordinal);

        // THE READ CATCHES UP, AND HE PRESSES AGAIN.
        h.C.PositionsTrail = null;
        var p2 = await h.Gw.OperatorCloseAllAsync();
        log.WriteLine($"press 2              : {Said(p2)}");
        log.WriteLine($"the book             : {Book(h.C)}");
        log.WriteLine($"position at the end  : {Pos(h.C)}");

        Assert.Equal(1, h.C.Closes);
        Assert.Equal(ExecutionState.FILLED, Assert.Single(p2.Targets).State);
        Assert.Equal(1m, h.C.Broker.Orders.Last(o => o.Side == OrderSide.Sell).Quantity);
        Assert.Equal(0m, Held(h.C));
        await h.Gw.DisposeAsync();
    }
}

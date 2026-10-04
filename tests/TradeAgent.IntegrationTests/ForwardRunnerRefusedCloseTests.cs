using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Paper;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// AN ORDER REFUSED BEFORE THE WIRE IS OVER (<c>U-runner-refused-close</c>): it no longer reads as an order in
/// flight, a refused exit on declared bars goes out again on its next minute while its decision is fresh, a rule
/// exit takes the run's resting stop and target off the book before it goes, and no stop or target of the run is
/// left working on a minute whose books read flat.
///
/// <para>Same harness as the rest of this class: the envelope through the two-witness card, the allocation and the
/// deployment by the app's own sweeps, the minutes into <c>forward_bar</c> and to the paper connector through the
/// shipped adapter, and every order through <c>PlaceAsync</c>. A gate is staged for one pass with
/// <c>TradingGateway.InstallInProgress</c>, the update window's own refusal, which is decided before anything is
/// written: no request row, nothing sent. No venue is reached.</para>
/// </summary>
public partial class ForwardRunnerTests
{
    /// <summary>One pass of the runner with the update window's refusal standing for exactly that pass.</summary>
    static async Task<ForwardRunState> RefusedPassAsync(Rig rig)
    {
        rig.Gw.InstallInProgress = () => true;
        try { return Assert.Single(await rig.Runner.AdvanceAsync()); }
        finally { rig.Gw.InstallInProgress = null; }
    }

    /// <summary>The order at the wire a run's operation put there, by the client order id it carries.</summary>
    static async Task<OrderInfo> OrderOf(Rig rig, DeploymentOpRow op) =>
        Assert.Single(await Wire(rig), o => o.ClientOrderId == TradingGateway.ClientOrderIdFor(op.RequestId));

    // ---------------------------------------------------------------- (a) a refused close is over

    /// <summary>
    /// (a) THE MAXIMUM HOLD'S SECOND CLOSE, REFUSED <c>POSITION_MOVED</c> OVER A ROW STILL <c>CREATED</c>, IS
    /// NOT AN ORDER IN FLIGHT — AND THE RUN ENTERS AGAIN.
    ///
    /// <para>(f)'s twelve minutes and two more. Minute 10's close fills at minute 12's open, so the pass that
    /// first sees it replays minute 11 as live with the books still long, the maximum hold asks again, and the
    /// gateway's stale-close read refuses that close: the venue is already flat. Nothing left the process
    /// — <c>DISPATCHING</c> is durable before the wire — so the row stays <c>CREATED</c> and is never sent.
    /// The run's books counted it as an order in flight for ever after, and the evaluator suppressed every entry
    /// while flat: the run was frozen with its envelope's slot held.</para>
    ///
    /// <para><b>RED on the base</b>: minute 12's second pass reads <c>pending=True</c> and nothing is written
    /// after <c>+11#0</c>, which is what (f) pinned. <b>Mutant (i)</b> — a refused operation over a
    /// <c>CREATED</c> row counted in flight again — goes red here.</para>
    /// </summary>
    [Fact]
    public async Task A_close_refused_before_the_wire_is_not_in_flight_and_the_run_enters_again()
    {
        await using var rig = await ReadyAsync(
            ProgramText("stop percent 5\ntarget percent 1\nmax_hold_bars 3\n", "entry when close > 99"));

        (decimal Open, decimal High, decimal Low, decimal Close)[] minutes =
        [
            (99m, 100m, 98m, 100m),             //  1 the entry signals from a close of 100: stop 95, target 101
            (100m, 100.5m, 99.5m, 100m),        //  2 the minute already in progress
            (100m, 100.6m, 99.6m, 100.2m),      //  3 the entry fills at this open; protection goes on
            (100.5m, 100.9m, 100.2m, 100.8m),   //  4 nothing touched
            (101m, 101.5m, 99.9m, 100m),        //  5 the target fills, the stop is cancelled, a second entry signals
            (100m, 100.2m, 99.8m, 100m),        //  6 the minute already in progress
            (100m, 100.3m, 99.7m, 100.1m),      //  7 the second entry fills; protection goes on again
            (100.1m, 100.4m, 99.9m, 100.2m),    //  8 held one bar
            (100.2m, 100.5m, 100m, 100.3m),     //  9 held two
            (100.3m, 100.6m, 100.1m, 100.4m),   // 10 held three: both protective orders cancelled, closed at market
            (100.4m, 100.7m, 100.2m, 100.5m),   // 11 the close is in flight
            (100.5m, 100.8m, 100.3m, 100.6m),   // 12 the close fills at this open; minute 11's second close is refused
            (100.6m, 100.9m, 100.4m, 100.7m),   // 13 the minute already in progress when minute 12's entry went out
            (100.8m, 101m, 100.6m, 100.9m),     // 14 that entry fills at this open
        ];

        ForwardRunState? twelve = null;
        for (var i = 0; i < minutes.Length; i++)
        {
            var (open, high, low, close) = minutes[i];
            rig.Bar(i + 1, open, high, low, close);
            await rig.Gw.RefreshHealthAsync();
            Assert.Single(await rig.Runner.AdvanceAsync());
            await Position(rig);
            var second = Assert.Single(await rig.Runner.AdvanceAsync());
            if (i + 1 == 12) twelve = second;
        }

        Show(log, rig);
        log.WriteLine($"m12 b: {Said(rig, twelve!)}");
        var ops = rig.Gw.Deployments.OpsOf(rig.Deployment.Id);

        // THE PREMISE: minute 11's close was refused over a row that never left CREATED.
        var refused = Assert.Single(ops, o => Said(rig, o.RequestId) == "+11#0");
        Assert.Equal(DeploymentOpKind.Flatten, refused.Kind);
        Assert.Equal(DeploymentOpState.Refused, refused.State);
        Assert.Contains(ErrorCode.POSITION_MOVED.ToString(), refused.Answer);
        Assert.Equal(ExecutionState.CREATED, rig.Gw.Requests.Get(refused.RequestId)!.State);

        // NO ORDER IN FLIGHT ON MINUTE 12: the refusal sent nothing and is over.
        Assert.False(twelve!.Account!.OrderPending);

        // AND THE PROGRAM ENTERS BY ITS RULES AGAIN: an entry written on minute 12, under its own id, filled at
        // minute 14's open — the first open after it went out.
        var entry = Assert.Single(ops, o => o.Kind == DeploymentOpKind.Entry && MinuteOf(rig, o) == 12);
        var order = await OrderOf(rig, entry);
        Assert.Equal(OrderSide.Buy, order.Side);
        Assert.Equal(ExecutionState.FILLED, order.State);
        var fill = Assert.Single(await rig.Conn.GetExecutionsAsync(PaperConnector.TheAccount, null),
            x => x.ClientOrderId == order.ClientOrderId);
        Assert.Equal(100.8m, fill.Price);
        Assert.Equal(1m, await Position(rig));
    }

    // ---------------------------------------------------------------- (b) a stale entry is over

    /// <summary>
    /// (b) AN ENTRY REFUSED <c>DECISION_EXPIRED</c> — the refusal (d) pins, between the write-ahead row and the
    /// wire — DOES NOT STOP THE RUN ENTERING ON A FRESH SIGNAL LATER.
    ///
    /// <para><b>RED on the base</b>: the stale entry's row is <c>CREATED</c> for ever, the books read it as an
    /// order in flight at every later minute, and the fresh signal on minute 13 is suppressed: nothing is written.
    /// <b>Mutant (i)</b> goes red here too.</para>
    /// </summary>
    [Fact]
    public async Task A_stale_entry_refused_DECISION_EXPIRED_does_not_stop_the_run_entering_again()
    {
        await using var rig = await ReadyAsync(ProgramText());

        // THE BAR CLOSED AND THEN THE APP WAS BUSY FOR TEN MINUTES: refused, definite, nothing sent.
        rig.Bar(1, 99m, 101m, 98m, 101m);
        rig.Clock.At = rig.Origin.AddMinutes(12);
        await rig.Runner.AdvanceAsync();

        var stale = Assert.Single(rig.Gw.Deployments.OpsOf(rig.Deployment.Id));
        Assert.Equal(DeploymentOpState.Refused, stale.State);
        Assert.Contains(ErrorCode.DECISION_EXPIRED.ToString(), stale.Answer);
        Assert.Equal(ExecutionState.CREATED, rig.Gw.Requests.Get(stale.RequestId)!.State);

        // THE MINUTES IT WAS BUSY THROUGH, none of them a signal (100 is not above 100), and then one that is.
        rig.Minutes(2, 12, _ => 100m);
        await rig.Gw.RefreshHealthAsync();
        await rig.Runner.AdvanceAsync();

        var signalled = await MinuteAsync(rig, 13, 100m, 102m, 99.5m, 101m);
        Show(log, rig);
        log.WriteLine($"after minute 13: {Said(rig, signalled)}");

        var entry = Assert.Single(rig.Gw.Deployments.OpsOf(rig.Deployment.Id),
            o => o.Kind == DeploymentOpKind.Entry && MinuteOf(rig, o) == 13);

        // AND IT FILLS LIKE ANY OTHER: minute 14 already in progress, minute 15's open.
        await MinuteAsync(rig, 14, 101m, 101.5m, 100.5m, 101m);
        await MinuteAsync(rig, 15, 101.5m, 102m, 101m, 101.8m);
        Show(log, rig);

        var order = await OrderOf(rig, entry);
        Assert.Equal(ExecutionState.FILLED, order.State);
        var fill = Assert.Single(await rig.Conn.GetExecutionsAsync(PaperConnector.TheAccount, null));
        Assert.Equal(101.5m, fill.Price);
        Assert.Equal(1m, await Position(rig));
    }

    // ---------------------------------------------------------------- (d) the refused exit, again

    /// <summary>
    /// (d) AN HOURLY PROGRAM'S EXIT REFUSED BEFORE THE WIRE GOES OUT AGAIN ON THE NEXT MINUTE, UNDER THAT MINUTE'S
    /// OWN ID AND WITH ITS DECISION UNCHANGED — not at a later hour's close.
    ///
    /// <para>The entry was decided on the 12:00 hour, so the evaluator's own one-bar hold covers the 13:00 hour's
    /// close; the 14:00 hour is the first an exit can be decided on. It closes at 89 and the program exits, and the
    /// update window refuses that pass. Minute 180 closes no hour, the books still read long, and the run's latest
    /// entry, exit or flatten is that refused exit: its intent is read back from its operation, sized from the books
    /// and sent as an exit under minute 180's id. Its decision is minute 179's, a minute old against bounds of an
    /// hour, and the gateway judges it again. Minute 181 is in progress and the exit fills at minute 182's open;
    /// from then on the latest exit is not a refused one and nothing more is sent.</para>
    ///
    /// <para><b>RED on the base</b>: there is no <c>+180</c> — on declared bars the evaluator is not asked again
    /// until the 15:00 hour, and its one-bar hold suppresses an exit there too. <b>Mutant (iii)</b> — sent again
    /// after ANY refused exit rather than the latest — goes red here: minute 181, replayed live once the exit has
    /// filled, writes a third exit.</para>
    /// </summary>
    [Fact]
    public async Task A_refused_exit_on_declared_bars_is_sent_again_next_minute_while_its_decision_is_fresh()
    {
        await using var rig = await ReadyAsync(HourlyText());

        await EnteredAsync(rig);
        rig.Minutes(62, HourClose(3) - 1, _ => 100m);
        await rig.Gw.RefreshHealthAsync();
        Assert.Single(await rig.Runner.AdvanceAsync());
        Assert.Equal(1m, await Position(rig));

        // THE 14:00 HOUR CLOSES AT 89, AND THE PASS THAT DECIDES ON IT IS REFUSED.
        rig.Bar(HourClose(3), 100m, 100m, 89m, 89m);
        await rig.Gw.RefreshHealthAsync();
        await RefusedPassAsync(rig);

        for (var minute = HourClose(3) + 1; minute <= HourClose(3) + 4; minute++)
            await MinuteAsync(rig, minute, 89m, 89.5m, 88.5m, 89m);
        Show(log, rig);

        // TWO EXITS IN ALL: minute 179's, refused, and minute 180's, sent and filled.
        var exits = rig.Gw.Deployments.OpsOf(rig.Deployment.Id).Where(o => o.Kind == DeploymentOpKind.Exit).ToList();
        foreach (var x in exits) log.WriteLine($"exit {Said(rig, x.RequestId)} {x.State} — {x.Answer}");
        Assert.Equal(["+179#0", "+180#0"], exits.Select(x => Said(rig, x.RequestId)));

        var (first, again) = (exits[0], exits[1]);
        Assert.Equal(DeploymentOpState.Refused, first.State);
        Assert.Contains(ErrorCode.UPDATE_INSTALL_IN_PROGRESS.ToString(), first.Answer);
        Assert.Null(rig.Gw.Requests.Get(first.RequestId));

        // UNDER ITS OWN CLIENT ORDER ID, AT THE WIRE ONCE, FILLED...
        Assert.Equal(DeploymentOpState.Resolved, again.State);
        var order = await OrderOf(rig, again);
        Assert.Equal(OrderSide.Sell, order.Side);
        Assert.Equal(ExecutionState.FILLED, order.State);
        Assert.Equal(1m, order.FilledQuantity);

        // ...WITH THE DECISION IT WAS SENT AGAIN FOR: the 14:00 hour, its bounds unchanged.
        var decided = Json.Read<PlaceIntent>(first.IntentJson)!.Decision!;
        Assert.Equal(decided, Json.Read<PlaceIntent>(again.IntentJson)!.Decision);
        Assert.Equal(rig.Origin.AddHours(2), decided.BarOpen);
        Assert.Equal(rig.Origin.AddHours(3), decided.BarClose);

        Assert.Equal(0m, await Position(rig));
    }
}

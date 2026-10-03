using System.Globalization;
using System.Text;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Paper;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE RUNNER ON DECLARED BARS (<c>U-timeframe-b</c>): a program that declares <c>bars 1h</c> has its rules
/// stepped once per closed hour, on the bar the resampler builds, while everything that protects a position —
/// the stop and the target placed when the entry fills, the loser cancelled when the other fills, the
/// maximum hold — still acts on the minute, exactly as it does for a one-minute program.
///
/// <para>Same harness as the rest of this class: the envelope through the two-witness card, the allocation
/// and the deployment by the app's own sweeps, the minutes into <c>forward_bar</c> and to the paper connector
/// through the shipped adapter, and every order through <c>PlaceAsync</c>. No venue is reached.</para>
/// </summary>
public partial class ForwardRunnerTests
{
    /// <summary>
    /// AN HOURLY PROGRAM'S HEAD: the three execution bounds on the hour, so a decision taken at an hour's close
    /// is fresh for the minutes it takes to reach the wire.
    /// </summary>
    const string HourHead =
        "instrument BTCUSDT\ntimezone UTC\nbars 1h\ntimeframe 1h\ndata_freshness 2h\nmax_decision_age 1h\n"
        + "size fixed 1\n";

    static string HourlyText(string declarations = "", string entry = "entry when close > 99") =>
        HourHead + declarations + "exit when close < 90\n" + entry + "\n";

    /// <summary>
    /// THE HOURS OF THESE RUNS. The deployment starts at the origin, 12:00, and the first minute it reads is
    /// 12:01 — strictly after its start — so minute <c>60·h − 1</c> is the last of an hour: 59 closes the 12:00
    /// hour (the partial bar the run saw from its start), 119 the 13:00 hour, 179 the 14:00, 239 the 15:00.
    /// </summary>
    static int HourClose(int hour) => 60 * hour - 1;

    /// <summary>
    /// ONE CLOSED MINUTE, THE CONNECTOR ASKED — which settles its book on that minute — AND ONE PASS OF THE
    /// RUNNER: what the app does when a minute closes.
    /// </summary>
    static async Task<ForwardRunState> MinuteAsync(Rig rig, int minute, decimal open, decimal high, decimal low,
        decimal close)
    {
        rig.Bar(minute, open, high, low, close);
        await rig.Gw.RefreshHealthAsync();
        return Assert.Single(await rig.Runner.AdvanceAsync());
    }

    /// <summary>A minute that opens, trades and closes at one price.</summary>
    static Task<ForwardRunState> FlatMinuteAsync(Rig rig, int minute, decimal price) =>
        MinuteAsync(rig, minute, price, price, price, price);

    /// <summary>Which minute after the origin an operation was written on.</summary>
    static int MinuteOf(Rig rig, DeploymentOpRow op) => (int)(op.BarOpenTime - rig.Origin).TotalMinutes;

    /// <summary>
    /// AN HOURLY RUN WITH ITS ENTRY FILLED: flat minutes at 100 to the 12:00 hour's close, where
    /// <c>entry when close &gt; 99</c> is asked for the first time and signals; minute 60 is the minute already
    /// in progress when the order went out, and the entry fills at minute 61's open, 100. Answers the run state
    /// after the one pass that followed minute 61's close.
    /// </summary>
    static async Task<ForwardRunState> EnteredAsync(Rig rig)
    {
        rig.Minutes(1, HourClose(1) - 1, _ => 100m);
        Assert.Null(Assert.Single(await rig.Runner.AdvanceAsync()).Ended);
        Assert.Empty(rig.Gw.Deployments.OpsOf(rig.Deployment.Id));

        await FlatMinuteAsync(rig, HourClose(1), 100m);
        var entry = Assert.Single(rig.Gw.Deployments.OpsOf(rig.Deployment.Id));
        Assert.Equal(DeploymentOpKind.Entry, entry.Kind);
        Assert.Equal(HourClose(1), MinuteOf(rig, entry));

        await FlatMinuteAsync(rig, 60, 100m);
        Assert.Equal(0m, await Position(rig));

        return await MinuteAsync(rig, 61, 100m, 100.4m, 99.8m, 100.2m);
    }

    // ---------------------------------------------------------------- (a) the declared clock

    /// <summary>
    /// (a) THE RULES OF A `bars 1h` DEPLOYMENT ARE ASKED ONCE PER HOUR, ON THE HOUR THE RESAMPLER BUILT.
    ///
    /// <para>The program enters when the close is above its two-bar mean, and the prices rise every minute: on
    /// minutes it would be warm at minute 2 and enter there. On hours it is warm at the 13:00 hour's close and
    /// enters there, once. The runner is asked after every minute, as the app asks it, and the evaluator's own
    /// count of bars moves at the two hour closes and at no other minute; the order names the hour it was
    /// decided on, 13:00 to 14:00.</para>
    ///
    /// <para><b>RED on the base</b> (<c>3dff4294</c>): the runner ended the deployment before stepping a bar —
    /// "this build's paper runner evaluates every minute".</para>
    /// </summary>
    [Fact]
    public async Task A_bars_1h_deployment_steps_its_rules_once_per_hour()
    {
        await using var rig = await ReadyAsync(HourlyText("indicator m = sma(close, 2)\n", "entry when close > m"));

        var moved = new List<(int Minute, long Bars)>();
        var bars = 0L;
        ForwardRunState? state = null;
        for (var minute = 1; minute <= 150; minute++)
        {
            var price = 100m + minute * 0.01m;
            state = await MinuteAsync(rig, minute, price, price + 0.005m, price - 0.005m, price);
            Assert.Null(state.Ended);
            var now = state.State!.Counters.Bars;
            if (now != bars) moved.Add((minute, now));
            bars = now;
        }

        Show(log, rig);
        log.WriteLine($"the evaluator's bars moved at: {string.Join(", ", moved)}");
        log.WriteLine($"counters: {state!.State!.Counters}");

        // TWICE IN 150 MINUTES, AND AT THE TWO HOUR CLOSES. The first hour is the partial bar the run saw from
        // its start — 59 of its 60 minutes, the minute opening at the deployment's own start not being one of
        // the run's — and it says so in the evaluator's own count of missing minutes.
        Assert.Equal([(HourClose(1), 1L), (HourClose(2), 2L)], moved);
        Assert.Equal(2, state.BarsReplayed);
        Assert.Equal(1, state.State.Counters.MissingMinutes);
        Assert.Equal(rig.Origin.AddMinutes(150), state.LastBar);

        // ONE ENTRY, WRITTEN ON THE MINUTE THE 13:00 HOUR CLOSED, AND DECIDED ON THAT HOUR.
        var entry = Assert.Single(rig.Gw.Deployments.OpsOf(rig.Deployment.Id));
        Assert.Equal(DeploymentOpKind.Entry, entry.Kind);
        Assert.Equal(HourClose(2), MinuteOf(rig, entry));

        var decision = Json.Read<PlaceIntent>(entry.IntentJson)!.Decision!;
        Assert.Equal(rig.Origin.AddHours(1), decision.BarOpen);
        Assert.Equal(rig.Origin.AddHours(2), decision.BarClose);

        // AND IT FILLED ON THE MINUTE, at the first open after it went out: minute 121's.
        var fill = Assert.Single(await rig.Conn.GetExecutionsAsync(PaperConnector.TheAccount, null));
        Assert.Equal(100m + 121 * 0.01m, fill.Price);
        Assert.Equal(1m, await Position(rig));
    }

    // ---------------------------------------------------------------- (b) protection on the minute

    /// <summary>
    /// (b) THE STOP AND THE TARGET OF AN HOURLY RUN GO TO THE VENUE IN THE PASS THAT FOLLOWS THE MINUTE THE
    /// ENTRY FILLED ON — not at the next hour's close, 58 minutes later.
    ///
    /// <para><b>RED on the base</b>: the run was ended before its first bar. <b>Mutant (i)</b> — the protection
    /// placed only on a minute a declared bar closes on — goes red here: nothing rests at the venue after the
    /// fill, and the position is unprotected for the rest of the hour.</para>
    /// </summary>
    [Fact]
    public async Task Protection_is_placed_within_one_minute_of_the_entry_fill()
    {
        await using var rig = await ReadyAsync(HourlyText("stop percent 5\ntarget percent 1\n"));

        var state = await EnteredAsync(rig);
        Show(log, rig);
        log.WriteLine($"after minute 61: {state.Account}");

        Assert.Equal(1m, await Position(rig));
        var fill = Assert.Single(await rig.Conn.GetExecutionsAsync(PaperConnector.TheAccount, null));
        Assert.Equal(100m, fill.Price);

        // BOTH RESTING AT THE VENUE NOW, 58 minutes before the hour closes, at the distances the program
        // declared from the hour's close of 100, measured from the price paid...
        var resting = (await Wire(rig))
            .Where(o => o.State is ExecutionState.WORKING or ExecutionState.ACKNOWLEDGED).ToList();
        foreach (var o in resting) log.WriteLine($"resting {o.Type} {o.Side} stop={o.StopPrice} limit={o.LimitPrice}");
        Assert.Equal(2, resting.Count);

        // ...AND WRITTEN ON MINUTE 61, the minute the entry filled on, by the pass that followed it.
        var ops = rig.Gw.Deployments.OpsOf(rig.Deployment.Id);
        var stop = Assert.Single(ops, o => o.Kind == DeploymentOpKind.Stop);
        var target = Assert.Single(ops, o => o.Kind == DeploymentOpKind.Target);
        Assert.Equal(61, MinuteOf(rig, stop));
        Assert.Equal(61, MinuteOf(rig, target));
        Assert.Contains(resting, o => o.Type == OrderType.Stop && o.StopPrice == 95m
                                      && o.ClientOrderId == TradingGateway.ClientOrderIdFor(stop.RequestId));
        Assert.Contains(resting, o => o.Type == OrderType.Limit && o.LimitPrice == 101m
                                      && o.ClientOrderId == TradingGateway.ClientOrderIdFor(target.RequestId));
    }

    // ---------------------------------------------------------------- (c) the loser, on the minute

    /// <summary>
    /// (c) WHEN THE TARGET FILLS, THE STOP IS CANCELLED IN THE PASS THAT FOLLOWS — AND A FALL THROUGH IT LATER
    /// IN THE SAME HOUR OPENS NO PAPER SHORT.
    ///
    /// <para>A resting sell stop under no position is an order that OPENS a short the moment it fires: the
    /// paper book turns a held quantity of zero into minus one. So the loser goes in the pass after the minute
    /// the winner filled on, and the price then falls to 94, a dollar under the stop, with most of the hour still
    /// to run.</para>
    ///
    /// <para><b>RED on the base</b>: the run was ended before its first bar. <b>Mutant (ii)</b> — the cancel
    /// made only on a minute a declared bar closes on — goes red here: the stop is still working at the fall,
    /// fills, and the account is short one.</para>
    /// </summary>
    [Fact]
    public async Task The_losing_protective_order_is_cancelled_within_one_minute_and_no_paper_short_opens()
    {
        await using var rig = await ReadyAsync(HourlyText("stop percent 5\ntarget percent 1\n"));

        await EnteredAsync(rig);
        var ops = rig.Gw.Deployments.OpsOf(rig.Deployment.Id);
        var stop = Assert.Single(ops, o => o.Kind == DeploymentOpKind.Stop);
        var target = Assert.Single(ops, o => o.Kind == DeploymentOpKind.Target);

        // MINUTE 62 WAS ALREADY IN PROGRESS WHEN THE PROTECTION WENT OUT, so the first minute either order can
        // fill on is 63. The target fills there, at its own level; then the price falls through where the stop
        // was, minute after minute, inside the same hour.
        await MinuteAsync(rig, 62, 100.2m, 100.6m, 100.1m, 100.5m);
        Assert.Equal(1m, await Position(rig));
        await MinuteAsync(rig, 63, 100.5m, 101.2m, 100.4m, 101m);
        for (var minute = 64; minute <= 70; minute++)
            await MinuteAsync(rig, minute, 96m, 96.5m, 94m, 95.5m);
        Show(log, rig);

        // NO PAPER SHORT: two executions in all, the entry and the target, and nothing held.
        Assert.Equal(0m, await Position(rig));
        Assert.Equal(2, (await rig.Conn.GetExecutionsAsync(PaperConnector.TheAccount, null)).Count);

        // BECAUSE THE STOP WAS CANCELLED ON MINUTE 63, by the pass that followed it — before minute 64 closed.
        var cancel = Assert.Single(rig.Gw.Deployments.OpsOf(rig.Deployment.Id),
            o => o.Kind == DeploymentOpKind.Cancel);
        Assert.Equal(63, MinuteOf(rig, cancel));
        Assert.True(cancel.CreatedAt < rig.Origin.AddMinutes(65), $"the cancel was written at {cancel.CreatedAt:u}");

        var wire = await Wire(rig);
        Assert.Equal(ExecutionState.FILLED,
            Assert.Single(wire, o => o.ClientOrderId == TradingGateway.ClientOrderIdFor(target.RequestId)).State);
        var stopOrder = Assert.Single(wire, o => o.ClientOrderId == TradingGateway.ClientOrderIdFor(stop.RequestId));
        Assert.Equal(ExecutionState.CANCELLED, stopOrder.State);
        Assert.Equal(0m, stopOrder.FilledQuantity);
    }

    // ---------------------------------------------------------------- (d) the maximum hold, in hours

    /// <summary>
    /// (d) `max_hold_bars 2` ON AN HOURLY PROGRAM IS TWO HOURS: the position is closed at the close of the
    /// second hour after the hour the entry filled in, before the evaluator is asked — as the backtest takes it.
    ///
    /// <para>The entry fills at minute 61, inside the 13:00 hour. It is held through the 14:00 hour's close
    /// (held one) and closed at the 15:00 hour's, minute 239. No stop and no target, and an entry rule that
    /// stays true: the maximum hold is the only thing that can end this position, and an evaluator asked before
    /// it would read a position the limit had reached.</para>
    ///
    /// <para><b>RED on the base</b>: the run was ended before its first bar. Counted in minutes, the limit
    /// closes the position at minute 63.</para>
    /// </summary>
    [Fact]
    public async Task Max_hold_counts_declared_bars()
    {
        await using var rig = await ReadyAsync(HourlyText("max_hold_bars 2\n"));

        await EnteredAsync(rig);
        Assert.Equal(1m, await Position(rig));

        // EVERY MINUTE UP TO THE ONE BEFORE THE 15:00 HOUR CLOSES: no close of any kind.
        rig.Minutes(62, HourClose(4) - 1, _ => 100m);
        var held = Assert.Single(await rig.Runner.AdvanceAsync());
        Show(log, rig);
        log.WriteLine($"before the 15:00 hour closes: {held.Account}");

        Assert.DoesNotContain(rig.Gw.Deployments.OpsOf(rig.Deployment.Id),
            o => o.Kind is DeploymentOpKind.Flatten or DeploymentOpKind.Exit);
        Assert.Equal(PositionSide.Long, held.Account!.Position);
        Assert.Equal(1, held.Account.BarsSinceEntry);

        // THE 15:00 HOUR CLOSES: two hours held, and the runner's own close goes out on that minute.
        var state = await FlatMinuteAsync(rig, HourClose(4), 100m);
        Show(log, rig);
        log.WriteLine($"at the 15:00 hour's close: {state.Account}; counters {state.State!.Counters}");

        var ops = rig.Gw.Deployments.OpsOf(rig.Deployment.Id);
        var flatten = Assert.Single(ops, o => o.Kind == DeploymentOpKind.Flatten);
        Assert.Equal(HourClose(4), MinuteOf(rig, flatten));
        Assert.DoesNotContain(ops, o => o.Kind == DeploymentOpKind.Exit);

        // AND THE EVALUATOR, ASKED AFTERWARDS, READ A FLAT ACCOUNT: one intent in the whole run, the entry.
        Assert.Equal(PositionSide.Flat, state.Account!.Position);
        Assert.Equal(1, state.State.Counters.Intents);
        Assert.Contains(await Wire(rig), o =>
            o.ClientOrderId == TradingGateway.ClientOrderIdFor(flatten.RequestId) && o.Side == OrderSide.Sell);
    }

    // ---------------------------------------------------------------- (e) a restart mid-hour

    /// <summary>
    /// (e) A RESTART IN THE MIDDLE OF AN HOUR RESUMES WITH THE STATE AN UNINTERRUPTED RUN HAS, AND SENDS NOTHING
    /// TWICE — past one page of bars, so the paging of <c>U-runner-forward</c> is under it too.
    ///
    /// <para>The hourly program enters when an hour closes above 100, and the first that does is the 168th,
    /// whose last minute is 10,079 — past the ten thousand bars one read of the ledger answers. One process
    /// sends the entry at that close and dies before any answer. Thirty-one minutes close while it is down:
    /// the next hour is half formed when a new process comes up over the same database and the same paper book.
    /// It rebuilds the run from the deployment's start — every page of minutes into a resampler of its own — and
    /// must arrive at exactly what a control run that never stopped arrives at over the same minutes: the same
    /// state, the same operations under the same ids, the same orders at the wire, the hour still forming at
    /// the same place; and, at that hour's close, the same answer again.</para>
    ///
    /// <para>A guard: nothing in the runner's process survives a pass, so there was never a resampler to lose.
    /// What it would catch is one kept — across passes, or from the cursor instead of the start.</para>
    /// </summary>
    [Fact]
    public async Task A_restart_mid_hour_resumes_with_the_same_state_and_no_duplicate_op()
    {
        var program = HourlyText("stop percent 5\ntarget percent 1\n", "entry when close > 100");
        var origin = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
        var entryClose = HourClose(168);                     // 10,079
        Assert.True(entryClose > ForwardRuns.BarsPerPage);

        // ---- the control: one process, never stopped
        await using var control = await ReadyAsync(program, origin: origin);
        await UpToTheEntryAsync(control, entryClose);
        control.Minutes(entryClose + 1, entryClose + 31, _ => 100m);
        await control.Gw.RefreshHealthAsync();
        var uninterrupted = Assert.Single(await control.Runner.AdvanceAsync());

        // ---- the process that died with the entry at the wire and no answer for it
        var db = TestEnv.NewDb();
        var book = Path.Combine(TestEnv.Home, $"paper-restart-hour-{Guid.NewGuid():n}.db");
        string entryRequest;
        {
            await using var first = await ReadyAsync(program, book, db, origin);
            await UpToTheEntryAsync(first, entryClose);
            var entry = Assert.Single(first.Gw.Deployments.OpsOf(first.Deployment.Id));
            entryRequest = entry.RequestId;
            Assert.Equal(DeploymentOpState.Dispatched, entry.State);
        }

        // ---- thirty-one minutes later, a new process over the same database and the same book
        await using var second = await ReadyAsync(program, book, db, origin);
        second.Minutes(entryClose + 1, entryClose + 31, _ => 100m);
        await second.Gw.RefreshHealthAsync();
        var resumed = Assert.Single(await second.Runner.AdvanceAsync());
        Show(log, second);
        log.WriteLine($"control : {Said(control, uninterrupted)}");
        log.WriteLine($"resumed : {Said(second, resumed)}");

        // THE SAME STATE — the evaluator's counters, the account, the bars stepped and the minute reached — and
        // the same book: one entry, under the id the dead process sent, filled once, with its protection.
        Assert.Equal(Said(control, uninterrupted), Said(second, resumed));
        Assert.Equal(168, resumed.BarsReplayed);
        Assert.Equal(await Ledger(control), await Ledger(second));
        Assert.Equal(entryRequest,
            Assert.Single(second.Gw.Deployments.OpsOf(second.Deployment.Id), o => o.Kind == DeploymentOpKind.Entry).RequestId);
        Assert.Single(await second.Conn.GetExecutionsAsync(PaperConnector.TheAccount, null));
        Assert.Equal(1m, await Position(second));

        // AND THE HOUR THAT WAS HALF FORMED AT THE RESTART CLOSES IN BOTH WITH THE SAME ANSWER.
        foreach (var rig in new[] { control, second })
        {
            rig.Minutes(entryClose + 32, HourClose(169), _ => 100.5m);
            await rig.Gw.RefreshHealthAsync();
        }

        var controlAtClose = Assert.Single(await control.Runner.AdvanceAsync());
        var resumedAtClose = Assert.Single(await second.Runner.AdvanceAsync());
        Assert.Equal(169, resumedAtClose.BarsReplayed);
        Assert.Equal(Said(control, controlAtClose), Said(second, resumedAtClose));
        Assert.Equal(await Ledger(control), await Ledger(second));
    }

    /// <summary>
    /// Flat minutes at 100 to the minute before <paramref name="close"/>, then that minute closing at 101 — the
    /// hour's close, above 100 — and the runner's pass that sends the entry.
    /// </summary>
    static async Task UpToTheEntryAsync(Rig rig, int close)
    {
        rig.Minutes(1, close - 1, _ => 100m);
        Assert.Null(Assert.Single(await rig.Runner.AdvanceAsync()).Ended);
        await MinuteAsync(rig, close, 100m, 101m, 100m, 101m);
    }

    /// <summary>A run's operations and its orders at the wire, with the deployment's hash taken out of every id.</summary>
    static async Task<string> Ledger(Rig rig)
    {
        var said = new StringBuilder();
        foreach (var op in rig.Gw.Deployments.OpsOf(rig.Deployment.Id))
            said.Append(CultureInfo.InvariantCulture, $"op {Said(rig, op.RequestId)} {op.Kind} {op.State} — {op.Answer}\n");
        foreach (var order in (await Wire(rig)).OrderBy(o => o.ClientOrderId, StringComparer.Ordinal))
            said.Append(CultureInfo.InvariantCulture,
                $"wire {Said(rig, order.ClientOrderId ?? "-")} {order.Side} {order.Type} {order.Quantity} stop={order.StopPrice?.ToString(CultureInfo.InvariantCulture) ?? "-"} limit={order.LimitPrice?.ToString(CultureInfo.InvariantCulture) ?? "-"} filled={order.FilledQuantity} {order.State}\n");
        return said.ToString();
    }

    // ---------------------------------------------------------------- (f) the one-minute guard

    /// <summary>
    /// THE ONE-MINUTE PROGRAM, PASS BY PASS, AS THE RUNNER BEFORE THIS UNIT ANSWERED IT.
    ///
    /// <para>An entry, its protection, the target filling and the stop cancelled, a second entry, the maximum
    /// hold cancelling both protective orders and closing at market, and the evaluator asked after every one of
    /// them. Every pass's run state, every operation (its kind, the minute it was written on, its sequence, its
    /// state, its order row's state and its answer) and every order at the wire are written into one
    /// transcript, and the transcript is the one the runner at <c>3dff4294</c> produced — captured there,
    /// before a line of this unit was written. A one-minute program's declared bar is the minute, and nothing
    /// about it may move.</para>
    ///
    /// <para><b>It pins what that runner did, all of it</b> — including the second close it wrote on minute 11,
    /// which the gateway refused <c>POSITION_MOVED</c> and whose order row stays <c>CREATED</c>, so the run's
    /// books read an order in flight from then on. That is behaviour this unit does not touch; a unit that
    /// changes it changes this transcript on purpose and says so.</para>
    /// </summary>
    [Fact]
    public async Task A_bars_1m_deployment_behaves_exactly_as_before()
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
            (100.5m, 100.8m, 100.3m, 100.6m),   // 12 the close fills at this open
        ];

        var transcript = new StringBuilder();
        for (var i = 0; i < minutes.Length; i++)
        {
            var (open, high, low, close) = minutes[i];
            rig.Bar(i + 1, open, high, low, close);
            await rig.Gw.RefreshHealthAsync();
            var first = Assert.Single(await rig.Runner.AdvanceAsync());
            await Position(rig);
            var second = Assert.Single(await rig.Runner.AdvanceAsync());
            transcript.Append(CultureInfo.InvariantCulture, $"m{i + 1} a: {Said(rig, first)}\n");
            transcript.Append(CultureInfo.InvariantCulture, $"m{i + 1} b: {Said(rig, second)}\n");
        }

        foreach (var op in rig.Gw.Deployments.OpsOf(rig.Deployment.Id))
            transcript.Append(CultureInfo.InvariantCulture,
                $"op {Said(rig, op.RequestId)} {op.Kind} {op.State} request={rig.Gw.Requests.Get(op.RequestId)?.State.ToString() ?? "-"} — {op.Answer}\n");

        foreach (var order in (await Wire(rig)).OrderBy(o => o.ClientOrderId, StringComparer.Ordinal))
            transcript.Append(CultureInfo.InvariantCulture,
                $"wire {Said(rig, order.ClientOrderId ?? "-")} {order.Side} {order.Type} {order.Quantity} stop={order.StopPrice?.ToString(CultureInfo.InvariantCulture) ?? "-"} limit={order.LimitPrice?.ToString(CultureInfo.InvariantCulture) ?? "-"} filled={order.FilledQuantity} {order.State}\n");

        transcript.Append(CultureInfo.InvariantCulture, $"position {await Position(rig)}\n");

        var said = transcript.ToString();
        log.WriteLine(said);
        Assert.Equal(OneMinuteTranscript, said);
    }

    /// <summary>What the runner at <c>3dff4294</c> answered for (f), pass by pass. See the test.</summary>
    const string OneMinuteTranscript = """
        m1 a: replayed=1 skipped=0 last=+1 ended=- account=Flat/0@0 equity=5000000 capital=5000000 pending=False held=0 counters=bars:1 missing:0 gaps:0 warm:0 undef:0 eval:1 intents:1 outside:0 whilePending:0 unsized:0
        m1 b: replayed=1 skipped=0 last=+1 ended=- account=Flat/0@0 equity=5000000 capital=5000000 pending=True held=0 counters=bars:1 missing:0 gaps:0 warm:0 undef:0 eval:1 intents:0 outside:0 whilePending:1 unsized:0
        m2 a: replayed=2 skipped=0 last=+2 ended=- account=Flat/0@0 equity=5000000 capital=5000000 pending=True held=0 counters=bars:2 missing:0 gaps:0 warm:0 undef:0 eval:2 intents:0 outside:0 whilePending:2 unsized:0
        m2 b: replayed=2 skipped=0 last=+2 ended=- account=Flat/0@0 equity=5000000 capital=5000000 pending=True held=0 counters=bars:2 missing:0 gaps:0 warm:0 undef:0 eval:2 intents:0 outside:0 whilePending:2 unsized:0
        m3 a: replayed=3 skipped=0 last=+3 ended=- account=Long/1.000@100 equity=5000000.2000 capital=5000000 pending=False held=0 counters=bars:3 missing:0 gaps:0 warm:0 undef:0 eval:3 intents:1 outside:0 whilePending:1 unsized:0
        m3 b: replayed=3 skipped=0 last=+3 ended=- account=Long/1.000@100 equity=5000000.2000 capital=5000000 pending=False held=0 counters=bars:3 missing:0 gaps:0 warm:0 undef:0 eval:3 intents:1 outside:0 whilePending:1 unsized:0
        m4 a: replayed=4 skipped=0 last=+4 ended=- account=Long/1.000@100 equity=5000000.8000 capital=5000000 pending=False held=1 counters=bars:4 missing:0 gaps:0 warm:0 undef:0 eval:4 intents:1 outside:0 whilePending:1 unsized:0
        m4 b: replayed=4 skipped=0 last=+4 ended=- account=Long/1.000@100 equity=5000000.8000 capital=5000000 pending=False held=1 counters=bars:4 missing:0 gaps:0 warm:0 undef:0 eval:4 intents:1 outside:0 whilePending:1 unsized:0
        m5 a: replayed=5 skipped=0 last=+5 ended=- account=Flat/0@0 equity=5000001.000 capital=5000000 pending=False held=0 counters=bars:5 missing:0 gaps:0 warm:0 undef:0 eval:5 intents:2 outside:0 whilePending:1 unsized:0
        m5 b: replayed=5 skipped=0 last=+5 ended=- account=Flat/0@0 equity=5000001.000 capital=5000000 pending=True held=0 counters=bars:5 missing:0 gaps:0 warm:0 undef:0 eval:5 intents:1 outside:0 whilePending:2 unsized:0
        m6 a: replayed=6 skipped=0 last=+6 ended=- account=Flat/0@0 equity=5000001.000 capital=5000000 pending=True held=0 counters=bars:6 missing:0 gaps:0 warm:0 undef:0 eval:6 intents:1 outside:0 whilePending:3 unsized:0
        m6 b: replayed=6 skipped=0 last=+6 ended=- account=Flat/0@0 equity=5000001.000 capital=5000000 pending=True held=0 counters=bars:6 missing:0 gaps:0 warm:0 undef:0 eval:6 intents:1 outside:0 whilePending:3 unsized:0
        m7 a: replayed=7 skipped=0 last=+7 ended=- account=Long/1.000@100 equity=5000001.1000 capital=5000000 pending=False held=0 counters=bars:7 missing:0 gaps:0 warm:0 undef:0 eval:7 intents:2 outside:0 whilePending:2 unsized:0
        m7 b: replayed=7 skipped=0 last=+7 ended=- account=Long/1.000@100 equity=5000001.1000 capital=5000000 pending=False held=0 counters=bars:7 missing:0 gaps:0 warm:0 undef:0 eval:7 intents:2 outside:0 whilePending:2 unsized:0
        m8 a: replayed=8 skipped=0 last=+8 ended=- account=Long/1.000@100 equity=5000001.2000 capital=5000000 pending=False held=1 counters=bars:8 missing:0 gaps:0 warm:0 undef:0 eval:8 intents:2 outside:0 whilePending:2 unsized:0
        m8 b: replayed=8 skipped=0 last=+8 ended=- account=Long/1.000@100 equity=5000001.2000 capital=5000000 pending=False held=1 counters=bars:8 missing:0 gaps:0 warm:0 undef:0 eval:8 intents:2 outside:0 whilePending:2 unsized:0
        m9 a: replayed=9 skipped=0 last=+9 ended=- account=Long/1.000@100 equity=5000001.3000 capital=5000000 pending=False held=2 counters=bars:9 missing:0 gaps:0 warm:0 undef:0 eval:9 intents:2 outside:0 whilePending:2 unsized:0
        m9 b: replayed=9 skipped=0 last=+9 ended=- account=Long/1.000@100 equity=5000001.3000 capital=5000000 pending=False held=2 counters=bars:9 missing:0 gaps:0 warm:0 undef:0 eval:9 intents:2 outside:0 whilePending:2 unsized:0
        m10 a: replayed=10 skipped=0 last=+10 ended=- account=Flat/0@0 equity=5000001.4000 capital=5000000 pending=True held=0 counters=bars:10 missing:0 gaps:0 warm:0 undef:0 eval:10 intents:2 outside:0 whilePending:3 unsized:0
        m10 b: replayed=10 skipped=0 last=+10 ended=- account=Flat/0@0 equity=5000001.4000 capital=5000000 pending=True held=0 counters=bars:10 missing:0 gaps:0 warm:0 undef:0 eval:10 intents:2 outside:0 whilePending:3 unsized:0
        m11 a: replayed=11 skipped=0 last=+11 ended=- account=Long/1.000@100 equity=5000001.5000 capital=5000000 pending=True held=4 counters=bars:11 missing:0 gaps:0 warm:0 undef:0 eval:11 intents:2 outside:0 whilePending:4 unsized:0
        m11 b: replayed=11 skipped=0 last=+11 ended=- account=Long/1.000@100 equity=5000001.5000 capital=5000000 pending=True held=4 counters=bars:11 missing:0 gaps:0 warm:0 undef:0 eval:11 intents:2 outside:0 whilePending:4 unsized:0
        m12 a: replayed=12 skipped=0 last=+12 ended=- account=Flat/0@0 equity=5000001.5000 capital=5000000 pending=False held=0 counters=bars:12 missing:0 gaps:0 warm:0 undef:0 eval:12 intents:3 outside:0 whilePending:4 unsized:0
        m12 b: replayed=12 skipped=0 last=+12 ended=- account=Flat/0@0 equity=5000001.5000 capital=5000000 pending=True held=0 counters=bars:12 missing:0 gaps:0 warm:0 undef:0 eval:12 intents:3 outside:0 whilePending:4 unsized:0
        op +1#0 entry resolved request=FILLED — FILLED
        op +3#0 stop resolved request=CANCELLED — WORKING — resting at the venue
        op +3#1 target resolved request=FILLED — WORKING — resting at the venue
        op +5#0 cancel resolved request=CANCELLED — CANCELLED
        op +5#1 entry resolved request=FILLED — FILLED
        op +7#0 stop resolved request=CANCELLED — WORKING — resting at the venue
        op +7#1 target resolved request=CANCELLED — WORKING — resting at the venue
        op +10#0 cancel resolved request=CANCELLED — CANCELLED
        op +10#1 cancel resolved request=CANCELLED — CANCELLED
        op +10#2 flatten resolved request=FILLED — FILLED
        op +11#0 flatten refused request=CREATED — nothing was sent: POSITION_MOVED — BTCUSDT was 1.000 when this close was sized and is 0 now, so Sell 1.000 would not flatten it; nothing was sent. Ask again with a new request id.
        wire +1#0 Buy Market 1.000 stop=- limit=- filled=1.000 FILLED
        wire +3#0 Sell Stop 1.000 stop=95 limit=- filled=0 CANCELLED
        wire +3#1 Sell Limit 1.000 stop=- limit=101 filled=1.000 FILLED
        wire +5#1 Buy Market 1.000 stop=- limit=- filled=1.000 FILLED
        wire +7#0 Sell Stop 1.000 stop=95 limit=- filled=0 CANCELLED
        wire +7#1 Sell Limit 1.000 stop=- limit=101 filled=0 CANCELLED
        wire +10#2 Sell Market 1.000 stop=- limit=- filled=1.000 FILLED
        position 0

        """;

    /// <summary>
    /// ONE PASS'S RUN STATE IN ONE LINE, every figure written invariantly — a culture that writes a decimal
    /// comma must not make a guard of behaviour fail on formatting.
    /// </summary>
    static string Said(Rig rig, ForwardRunState state)
    {
        var a = state.Account;
        var c = state.State?.Counters;
        var last = state.LastBar is { } l ? FormattableString.Invariant($"+{(l - rig.Origin).TotalMinutes}") : "-";
        var account = a is null
            ? "account=-"
            : FormattableString.Invariant(
                $"account={a.Position}/{a.Quantity}@{a.AverageFillPrice} equity={a.Equity} capital={a.StrategyCapital} pending={a.OrderPending} held={a.BarsSinceEntry}");
        var counters = c is null
            ? "counters=-"
            : FormattableString.Invariant(
                $"counters=bars:{c.Bars} missing:{c.MissingMinutes} gaps:{c.GapRuns} warm:{c.WarmingUpEvents} undef:{c.UndefinedEvents} eval:{c.EvaluatedEvents} intents:{c.Intents} outside:{c.EntriesOutsideTimeFilters} whilePending:{c.SignalsWhilePending} unsized:{c.EntriesWithoutSize}");

        return FormattableString.Invariant(
            $"replayed={state.BarsReplayed} skipped={state.BarsSkipped} last={last} ended={state.Ended ?? "-"} {account} {counters}");
    }

    /// <summary>
    /// A REQUEST OR CLIENT ORDER ID WITH THE DEPLOYMENT'S HASH TAKEN OUT AND THE MINUTE MADE RELATIVE:
    /// <c>dp-&lt;12&gt;-&lt;minute&gt;-&lt;seq&gt;</c> becomes <c>+&lt;minutes after the origin&gt;#&lt;seq&gt;</c>.
    /// </summary>
    static string Said(Rig rig, string id)
    {
        var request = id.StartsWith("TA-", StringComparison.Ordinal) ? id[3..] : id;
        var parts = request.Split('-');
        if (parts.Length != 4 || parts[0] != "dp") return id;

        var origin = rig.Origin.ToUnixTimeSeconds() / 60;
        var minute = long.Parse(parts[2], CultureInfo.InvariantCulture);
        return FormattableString.Invariant($"+{minute - origin}#{parts[3]}");
    }
}

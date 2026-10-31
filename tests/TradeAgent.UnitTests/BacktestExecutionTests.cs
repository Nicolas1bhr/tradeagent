using TradeAgent.Core.Data;
using TradeAgent.Core.Strategy;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEM 2 — THE DECLARED EXECUTION MODEL.
///
/// <para><b>The one that matters most.</b> An order learnt at minute n fills at the OPEN of the first minute at or
/// after n+2 — the minute paper fills it on (<c>U-backtest-parity</c>). A backtest that filled at the signal bar's
/// own close would be buying at a price the decision had not been taken at yet, and one that filled at n+1's open
/// at a price paper never pays: both are wrong in the flattering direction on every single trade — which is the
/// single most common way a backtest lies. Every fixture below is built so that the readings give visibly
/// different numbers, rather than numbers that differ in the fourth decimal; the minute between a decision and its
/// fill is in each, marked "placed".</para>
///
/// <para><b>And the ordering nobody can derive from the data.</b> A bar whose low touched the stop and
/// whose high touched the target counts as the STOP. Bars carry no intrabar ordering
/// (`docs/COUNCIL.md`, "Data"), so which came first is unknowable and the conservative reading is the
/// only honest one. `Conservative_ordering...` is the fixture, and swapping the two branches turns its
/// loss into a profit of the same size — which is the mutant this class was watched against.</para>
/// </summary>
public class BacktestExecutionTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Start = new(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);

    static StrategyProgram Program(string text)
    {
        var parse = StrategyParser.Parse(text);
        Assert.True(parse.Ok, parse.Why);
        return parse.Program!;
    }

    /// <summary>Bars from explicit OHLC rows, one minute apart, so nothing about a fixture is implicit.</summary>
    static IReadOnlyList<KlineBar> Bars(params decimal[][] rows) =>
        [.. rows.Select((r, i) => new KlineBar(Start.AddMinutes(i), r[0], r[1], r[2], r[3], 1m))];

    static ExecutionModel Model(
        decimal? fees = null, decimal? slippage = null, decimal? increment = null, decimal? capital = null)
    {
        var declared = ExecutionModel.Declare(fees, slippage, increment, capital);
        Assert.True(declared.Ok, declared.Why);
        return declared.Model!;
    }

    static BacktestRequest Request(ExecutionModel? model = null) =>
        new(7, "sha-of-the-normalised-file", model ?? ExecutionModel.Frictionless);

    BacktestResult RunOn(StrategyProgram program, IReadOnlyList<KlineBar> bars, ExecutionModel? model = null)
    {
        var result = Backtest.Run(program, Request(model), bars);
        log.WriteLine(result.Trace.Text);
        return result;
    }

    /// <summary>Enter above 100, leave below 90. No protection, so only the fill rule is in play.</summary>
    static StrategyProgram EnterAbove100() => Program("""
        instrument BTCUSDT
        size fixed 2
        exit when close < 90
        entry when close > 100
        """);

    /// <summary>
    /// A BAR THAT TOUCHED BOTH THE STOP AND THE TARGET COUNTS AS THE STOP.
    ///
    /// <para>Bar 2 opens at 110, runs down to 95 and up to 125; the stop is at 99 and the target at
    /// 121, so both were touched inside one bar and the data does not say in which order. The stop is
    /// taken, and the trade is a loss of 11. Taking the target instead makes it a profit of 11 — same
    /// fixture, opposite sign.</para>
    /// </summary>
    [Fact]
    public void Conservative_ordering_counts_the_stop_on_a_bar_that_touched_both_it_and_the_target()
    {
        var program = Program("""
            instrument BTCUSDT
            size fixed 1
            stop percent 10
            target percent 10
            exit when close < 1
            entry when close > 100
            """);

        var result = RunOn(program, Bars(
            [100m, 111m, 99m, 110m],        // close 110 > 100: enter. stop 99, target 121
            [110m, 110m, 110m, 110m],       // the entry is placed
            [110m, 111m, 109m, 110m],       // the fill at 110: the levels, measured from it, are 99 and 121
            [110m, 111m, 109m, 110m],       // protection is placed
            [110m, 125m, 95m, 110m],        // resting, BOTH are touched on this bar
            [50m, 51m, 49m, 50m]));         // nothing fires here

        var trade = Assert.Single(result.Trades);
        Assert.Equal(-11m, trade.Pnl);                        // +11 if the target is taken instead
        Assert.Equal(99m, trade.ExitPrice);
        Assert.Equal(ExitReason.Stop, trade.Reason);
    }

    /// <summary>
    /// A BAR THAT OPENED THROUGH THE STOP FILLS AT THAT OPEN, NOT AT THE STOP.
    ///
    /// <para>A market that gapped past a resting stop does not fill you at it. The stop is at 99 and
    /// the bar opens at 80: the position is gone at 80.</para>
    /// </summary>
    [Fact]
    public void A_bar_that_opened_through_the_stop_fills_there_and_not_at_the_stop()
    {
        var program = Program("""
            instrument BTCUSDT
            size fixed 1
            stop percent 10
            exit when close < 1
            entry when close > 100
            """);

        var result = RunOn(program, Bars(
            [100m, 111m, 99m, 110m],        // enter; stop at 99
            [110m, 111m, 109m, 110m],       // the entry is placed
            [110m, 111m, 109m, 110m],       // the fill at 110, nothing touched
            [110m, 111m, 109m, 110m],       // the stop is placed
            [80m, 81m, 75m, 78m],           // resting: opens at 80, straight through the stop
            [78m, 79m, 77m, 78m]));

        var trade = Assert.Single(result.Trades);
        Assert.Equal(ExitReason.Stop, trade.Reason);
        Assert.Equal(80m, trade.ExitPrice);
        Assert.Equal(-30m, trade.Pnl);
    }

    /// <summary>
    /// THE FEE IS CHARGED ON EVERY FILL AND THE SLIPPAGE IS ADVERSE IN BOTH DIRECTIONS.
    ///
    /// <para>Every figure here is exact in decimal and was worked out by hand: buy 2 at
    /// <c>100 * 1.0005 = 100.05</c>, fee <c>200.10 * 0.001 = 0.2001</c>; sell 2 at
    /// <c>120 * 0.9995 = 119.94</c>, fee <c>239.88 * 0.001 = 0.23988</c>. Gross
    /// <c>(119.94 - 100.05) * 2 = 39.78</c>, fees <c>0.43998</c>, net <c>39.34002</c>.</para>
    /// </summary>
    [Fact]
    public void Slippage_is_adverse_on_both_fills_and_the_fee_is_charged_on_each_of_them()
    {
        var result = RunOn(EnterAbove100(), Bars(
            [100m, 102m, 99m, 101m],
            [101m, 102m, 100m, 101m],       // the entry is placed
            [100m, 105m, 95m, 101m],        // the entry fills at 100 * 1.0005
            [101m, 102m, 88m, 89m],
            [89m, 90m, 88m, 89m],           // the exit is placed
            [120m, 125m, 115m, 119m]),      // the exit fills at 120 * 0.9995
            Model(fees: 0.001m, slippage: 0.0005m));

        var trade = Assert.Single(result.Trades);
        Assert.Equal(100.05m, trade.EntryPrice);
        Assert.Equal(119.94m, trade.ExitPrice);
        Assert.Equal(39.78m, trade.Pnl);
        Assert.Equal(0.43998m, trade.Fees);
        Assert.Equal(39.34002m, trade.Net);

        // The cash the run ends on is the capital plus exactly that net.
        var lastBar = result.Trace.Of(BacktestEventKind.Exit).Last();
        Assert.Equal(10_000m + 39.34002m, lastBar.Cash);
    }

    /// <summary>
    /// A QUANTITY IS ROUNDED DOWN TO THE RUN'S DECLARED INCREMENT, AND ZERO IS NO TRADE WITH THE REASON.
    ///
    /// <para>The dataset carries no increment, so the run's declared one is the only one there is. A
    /// quarter of ten thousand at forty thousand a unit is 0.125: whole units make that nothing, a
    /// hundredth makes it 0.12. Nothing is ever rounded UP — that would be a position nobody asked
    /// for — and the zero case is a line in the trace naming the increment rather than a trade that
    /// silently did not happen.</para>
    /// </summary>
    [Fact]
    public void A_size_that_rounds_down_to_nothing_is_no_trade_and_the_reason_names_the_increment()
    {
        var program = Program("""
            instrument BTCUSDT
            size capital_fraction 0.5
            exit when close < 1
            entry when close > 100
            """);

        var bars = Bars(
            [40_000m, 40_100m, 39_900m, 40_000m],
            [40_000m, 40_100m, 39_900m, 40_000m],
            [40_000m, 40_100m, 39_900m, 40_000m]);

        var wholeUnits = RunOn(program, bars, Model(increment: 1m));
        Assert.Empty(wholeUnits.Trades);
        Assert.Empty(wholeUnits.Trace.Of(BacktestEventKind.Fill));
        var refused = wholeUnits.Trace.Of(BacktestEventKind.NoTrade).First();
        Assert.Contains("rounds down to", refused.Reason);
        Assert.Contains("increment of 1", refused.Reason);
        Assert.Equal(0.125m, refused.Quantity);              // what the sizing actually came to

        var hundredths = RunOn(program, bars, Model(increment: 0.01m));
        var fill = hundredths.Trace.Of(BacktestEventKind.Fill).First();
        Assert.Equal(0.12m, fill.Quantity);                  // DOWN from 0.125, never up
    }

    /// <summary>
    /// THE MAXIMUM HOLDING TIME IS THE BACKTEST'S PROTECTION, AND THE EVALUATOR EMITS NOTHING FOR IT.
    ///
    /// <para>It is checked before the evaluator is asked anything on that bar, and from then until its market close
    /// fills the account the evaluator reads is FLAT with that close pending, so its own <c>max_hold_bars</c> branch
    /// cannot fire a second exit and no entry is taken while the close is in flight. One intent came out of this whole
    /// run — the entry — and the exit is protection's.</para>
    ///
    /// <para>That ordering is what makes the declared maximum a promise: on a bar the evaluator cannot
    /// evaluate at all — warming up, a value undefined, an interpreter fault — the position still
    /// closes.</para>
    /// </summary>
    [Fact]
    public void The_maximum_holding_time_is_enforced_by_protection_and_not_by_an_intent()
    {
        var program = Program("""
            instrument BTCUSDT
            size fixed 1
            max_hold_bars 2
            exit when close < 1
            entry when close > 100
            """);

        var result = RunOn(program, Bars(
            [100m, 111m, 99m, 110m],        // enter
            [110m, 111m, 109m, 110m],       // the entry is placed
            [110m, 111m, 109m, 110m],       // fills at 110; held 0
            [111m, 113m, 110m, 112m],       // held 1 — under the limit
            [113m, 116m, 98m, 99m],         // held 2 — protection sends a market close at this bar's close
            [99m, 100m, 98m, 99m],          // the close is placed; the books still hold the position
            [99m, 99m, 97m, 98m]));         // and it fills at this minute's OPEN, 99 — never at a close

        var trade = Assert.Single(result.Trades);
        Assert.Equal(ExitReason.MaxHoldBars, trade.Reason);
        Assert.Equal(99m, trade.ExitPrice);
        Assert.Equal(-11m, trade.Pnl);

        // ONE intent in the whole run, and it was the entry.
        Assert.Equal(1, result.Counters.Intents);
        var signal = Assert.Single(result.Trace.Of(BacktestEventKind.Signal));
        Assert.Equal(nameof(IntentKind.Enter), signal.Status);
        Assert.DoesNotContain(result.Trace.Of(BacktestEventKind.Signal),
            e => e.Reason == nameof(IntentCause.MaxHoldBars));
    }

    /// <summary>
    /// A SIGNAL THE WINDOW ENDED ON NEVER FILLED, AND THAT IS RECORDED RATHER THAN DROPPED.
    ///
    /// <para>There is no next bar to open at. A run that silently swallowed it would report a strategy
    /// that took no trade where it in fact took a decision nothing could execute.</para>
    /// </summary>
    [Fact]
    public void A_signal_on_the_last_bar_has_no_open_to_fill_at_and_the_trace_says_so()
    {
        var result = RunOn(EnterAbove100(), Bars([100m, 102m, 99m, 101m]));

        Assert.Empty(result.Trades);
        Assert.Equal(1, result.Counters.Intents);
        var refused = Assert.Single(result.Trace.Of(BacktestEventKind.NoTrade));
        Assert.Contains($"window ended before {Start.AddMinutes(2):O}", refused.Reason);
    }

    /// <summary>
    /// AN ENTRY THE DECLARED CAPITAL CANNOT PAY FOR IS NO TRADE, WITH BOTH NUMBERS IN THE REASON.
    ///
    /// <para>Declared capital has to bound something or it is decoration. Two units at about 100 is
    /// 200 and the account has 100, so nothing is bought and the equity curve stays arithmetically
    /// possible — a run that had gone on spending would have reported a result on an account that
    /// cannot exist.</para>
    /// </summary>
    [Fact]
    public void An_entry_the_declared_capital_cannot_pay_for_is_no_trade_and_the_reason_names_both_figures()
    {
        var result = RunOn(EnterAbove100(), Bars(
            [100m, 102m, 99m, 101m],
            [101m, 102m, 100m, 101m],       // the entry is placed
            [100m, 105m, 95m, 101m]),       // it would fill at this open, 100: two at 100 is 200
            Model(capital: 100m));

        Assert.Empty(result.Trades);
        Assert.Empty(result.Trace.Of(BacktestEventKind.Fill));

        var refused = result.Trace.Of(BacktestEventKind.NoTrade).First();
        Assert.Contains("declared capital cannot pay", refused.Reason);
        Assert.Contains("2 at 100", refused.Reason);            // what it would have cost
        Assert.Contains("100 is what is left", refused.Reason);  // and what there was

        // The rule is still true on the next bar it is evaluated on, so the refusal happens again
        // rather than the run quietly giving up on a strategy that keeps asking.
        Assert.Equal(2, result.Counters.Intents);
    }

    /// <summary>
    /// THE MODEL IS DECLARED, AND A DECLARATION THAT CANNOT MEAN WHAT IT SAYS IS REFUSED IN WORDS.
    ///
    /// <para>The fee case is the one worth having: <c>--fees 0.1</c> from an agent that meant a tenth
    /// of a percent would charge a hundred times the real cost and lose the run on the declaration
    /// rather than on the strategy. The refusal says which reading it took.</para>
    /// </summary>
    [Fact]
    public void A_declaration_that_cannot_mean_what_it_says_is_refused_and_the_refusal_reads()
    {
        Assert.Contains("basis points", ExecutionModel.Declare(fees: 0.1m).Why);
        Assert.Contains("cannot be negative", ExecutionModel.Declare(fees: -0.001m).Why);
        Assert.Contains("adverse by definition", ExecutionModel.Declare(slippage: -0.5m).Why);
        Assert.Contains("a different market", ExecutionModel.Declare(slippage: 0.9m).Why);
        Assert.Contains("every size would round to nothing", ExecutionModel.Declare(increment: 0m).Why);
        Assert.Contains("can buy nothing", ExecutionModel.Declare(capital: 0m).Why);
        Assert.Contains("more than this product is for", ExecutionModel.Declare(capital: 2_000_000_000m).Why);

        foreach (var refused in new[]
                 {
                     ExecutionModel.Declare(fees: 0.1m), ExecutionModel.Declare(increment: -1m),
                     ExecutionModel.Declare(capital: -5m)
                 })
        {
            Assert.False(refused.Ok);
            Assert.Null(refused.Model);
        }

        // AND THE CANONICAL FORM NORMALISES, so one declaration spelled two ways is one run and not
        // two: 0.0010 and 0.001 are the same fee.
        Assert.Equal(Model(fees: 0.001m).Canonical, Model(fees: 0.0010m).Canonical);
        Assert.Equal("fees=0.001;slippage=0.0005;increment=0.01;capital=10000",
            Model(fees: 0.001m, slippage: 0.0005m, increment: 0.01m, capital: 10_000m).Canonical);

        // The default declares no friction at all, and says so rather than implying a venue's fee.
        Assert.False(ExecutionModel.Frictionless.Frictionful);
        Assert.Equal("fees=0;slippage=0;increment=1;capital=10000", ExecutionModel.Frictionless.Canonical);
    }

    // ---- THE MINUTE PAPER FILLS ON (U-backtest-parity) ---------------------------------------------------------
    //
    // The paper runner reads minute X after it has closed and places what it decided inside X+1, and the paper venue
    // fills a market order only on a minute that OPENS after it was placed — so an order learnt at minute X fills at
    // the open of the first minute opening at or after X+2. Protection the runner places on the pass after the entry's
    // fill minute F rests from F+2, and an exit or the maximum hold decided at X takes it off inside X+1. Every test
    // below is RED on the base (`d55784e4`), where the backtest filled at X+1's open, let protection fire from F
    // itself, closed the maximum hold at X's own close and measured a relative level from the signal's close.

    /// <summary>
    /// (a) A SIGNAL FILLS AT THE OPEN AFTER THE MINUTE IN PROGRESS — the minute it is placed in — and never in it.
    ///
    /// <para>The entry is learnt at 00:00 and placed inside 00:01, so 00:01's open (200) is a price paper never pays;
    /// it fills at 00:02's, 210. The exit learnt at 00:03 fills at 00:05's open, 320, not 00:04's 300. And on the
    /// exit's fill minute the account is flat at the close, so the entry rule signals again — and that order is due at
    /// 00:07, which this window does not reach.</para>
    ///
    /// <para><b>RED on the base:</b> entry 00:01 @200, exit 00:04 @300. <b>RED through the mutant</b> "fill at X+1
    /// again".</para>
    /// </summary>
    [Fact]
    public void A_signal_fills_at_the_open_after_the_minute_in_progress()
    {
        var result = RunOn(EnterAbove100(), Bars(
            [100m, 102m, 99m, 101m],        // 00:00 close 101 > 100: the entry is learnt here
            [200m, 205m, 195m, 201m],       // 00:01 the minute it is placed in: nothing fills at 200
            [210m, 215m, 205m, 211m],       // 00:02 it fills HERE, at 210
            [211m, 212m, 88m, 89m],         // 00:03 close 89 < 90: the exit is learnt here
            [300m, 305m, 295m, 299m],       // 00:04 placed: nothing fills at 300
            [320m, 325m, 315m, 321m]));     // 00:05 it fills HERE, at 320 — and the entry signals again

        var trade = Assert.Single(result.Trades);
        Assert.Equal((Start.AddMinutes(2), 210m), (trade.EntryBar, trade.EntryPrice));
        Assert.Equal((Start.AddMinutes(5), 320m), (trade.ExitBar, trade.ExitPrice));
        Assert.Equal(ExitReason.Rule, trade.Reason);
        Assert.Equal(2m, trade.Quantity);
        Assert.Equal(220m, trade.Pnl);                       // (320 - 210) * 2; at the signal's own close it would be 101
        Assert.Equal(BacktestOutcome.COMPLETED, result.Outcome);

        var fill = Assert.Single(result.Trace.Of(BacktestEventKind.Fill));
        Assert.Equal((3L, Start.AddMinutes(2), 210m), (fill.Ordinal, fill.Bar, fill.Price!.Value));

        Assert.Equal([(1L, "Enter"), (4L, "Exit"), (6L, "Enter")],
            result.Trace.Of(BacktestEventKind.Signal).Select(s => (s.Ordinal, s.Status!)));

        var refused = Assert.Single(result.Trace.Of(BacktestEventKind.NoTrade));
        Assert.Contains($"ended before {Start.AddMinutes(7):O}", refused.Reason);
    }

    /// <summary>
    /// (b) A STOP TOUCHED BEFORE IT RESTS AT THE VENUE NEVER FIRES.
    ///
    /// <para>The entry fills at 00:02's open, 100; the stop is placed by the pass after 00:02 closes, inside 00:03, so
    /// it rests from 00:04. Its level is five percent below the FILL, 94.95, not below the signal's close (95.95).
    /// 00:01, 00:02 and 00:03 all reach 94 and none of them fires it — on 00:01 there is no position, and on the other
    /// two there is no stop at the venue yet. 00:05 reaches 90 with the stop resting: it fires, at 94.95.</para>
    ///
    /// <para><b>RED on the base:</b> filled at 00:01 and stopped on 00:01 at 95.95. <b>RED through the mutant</b>
    /// "protection from F": stopped on 00:02.</para>
    /// </summary>
    [Fact]
    public void A_stop_touched_before_it_rests_at_the_venue_never_fires()
    {
        var program = Program("""
            instrument BTCUSDT
            size fixed 1
            stop percent 5
            entry when close > 100
            """);

        var result = RunOn(program, Bars(
            [100m, 102m, 99m, 101m],        // 00:00 the entry is learnt; five percent under 101 is 95.95
            [101m, 101m, 94m, 100m],        // 00:01 placed; reaches 94 with nothing held
            [100m, 100m, 94m, 99m],         // 00:02 fills at 100, the stop is 94.95 — reached, not yet resting
            [99m, 99m, 94m, 98m],           // 00:03 the stop is placed inside this minute — reached, not resting
            [98m, 99m, 97m, 98m],           // 00:04 resting from here: untouched
            [98m, 99m, 90m, 92m]));         // 00:05 reached with the stop resting: it fires at 94.95

        var trade = Assert.Single(result.Trades);
        Assert.Equal((Start.AddMinutes(2), 100m), (trade.EntryBar, trade.EntryPrice));
        Assert.Equal(ExitReason.Stop, trade.Reason);
        Assert.Equal((Start.AddMinutes(5), 94.95m), (trade.ExitBar, trade.ExitPrice));
    }

    /// <summary>
    /// (c) A MAXIMUM HOLD CLOSES AT MARKET TWO MINUTES ON, AND ENTERS AGAIN ON ITS FILL MINUTE.
    ///
    /// <para>The paper runner's rhythm, +2/+2/+0: the entry learnt at 00:00 fills at 00:02 (112); the two-bar hold is
    /// reached at 00:04's close and its market close, placed inside 00:05, fills at 00:06's open (116) — never at
    /// 00:04's close. On 00:04 and 00:05 the account the evaluator reads is FLAT with the close IN FLIGHT, as the
    /// runner's <c>RunBooks.Flat</c> is, so the entry rule that is true on both is held; on 00:06 the close has filled
    /// and the entry signals again. The second hold is reached at 00:10 and its close is due at 00:12, which the window
    /// does not reach: the position is open at the end, outside net pnl, and the trace says so.</para>
    ///
    /// <para>The evaluator emits nothing for the hold: two intents in the run, both entries.</para>
    ///
    /// <para><b>RED on the base:</b> entry 00:01 @111, closed at 00:03's close @113, entered again at 00:03.
    /// <b>RED through the mutant</b> <c>OrderPending: false</c> at the maximum hold: entered again at 00:04.</para>
    /// </summary>
    [Fact]
    public void A_maximum_hold_closes_at_market_two_minutes_on_and_enters_again_on_its_fill_minute()
    {
        var program = Program("""
            instrument BTCUSDT
            size fixed 1
            max_hold_bars 2
            entry when close > 100
            """);

        var bars = Enumerable.Range(0, 12)
            .Select(i => new KlineBar(Start.AddMinutes(i), 110m + i, 111m + i, 109m + i, 110m + i, 1m))
            .ToList();
        var result = RunOn(program, bars);

        var trade = Assert.Single(result.Trades);
        Assert.Equal((Start.AddMinutes(2), 112m), (trade.EntryBar, trade.EntryPrice));
        Assert.Equal((Start.AddMinutes(6), 116m), (trade.ExitBar, trade.ExitPrice));
        Assert.Equal(ExitReason.MaxHoldBars, trade.Reason);

        Assert.Equal([Start, Start.AddMinutes(6)], result.Trace.Of(BacktestEventKind.Signal).Select(s => s.Bar));
        Assert.All(result.Trace.Of(BacktestEventKind.Signal), s => Assert.Equal(nameof(IntentKind.Enter), s.Status));
        Assert.Equal(2, result.Counters.Intents);
        Assert.Equal([Start.AddMinutes(2), Start.AddMinutes(8)], result.Trace.Of(BacktestEventKind.Fill).Select(f => f.Bar));

        // THE SECOND HOLD'S CLOSE IS DUE PAST THE WINDOW: the position stays open, and the words say why.
        Assert.True(result.Metrics.PositionOpenAtEnd);
        var refused = Assert.Single(result.Trace.Of(BacktestEventKind.NoTrade));
        Assert.Contains($"ended before {Start.AddMinutes(12):O}", refused.Reason);
        Assert.Contains("maximum hold", refused.Reason);
        Assert.Contains("still open at the end", refused.Reason);
    }

    /// <summary>
    /// (d) AN ENTRY ON THE MINUTE AN EXIT FILLS IS TAKEN — the guard against the over-fix.
    ///
    /// <para>An order the evaluator itself emitted is held by the evaluator for one event, and that covers the minute
    /// it is placed in; the backtest does not ALSO report it pending, or the evaluator's held intent would outlive the
    /// fill and swallow the entry the runner makes on the exit's fill minute. The exit learnt at 00:03 fills at 00:05's
    /// open; 00:05 closes flat above 100, so the entry signals there and fills at 00:07.</para>
    ///
    /// <para><b>RED through the mutant</b> "<c>OrderPending</c> set for an evaluator order in flight": the entry slips
    /// to 00:06 and fills at 00:08.</para>
    /// </summary>
    [Fact]
    public void An_entry_on_the_minute_an_exit_fills_is_taken()
    {
        var result = RunOn(EnterAbove100(), Bars(
            [100m, 102m, 99m, 101m],        // 00:00 entry learnt
            [101m, 102m, 100m, 101m],       // 00:01 placed
            [102m, 103m, 101m, 102m],       // 00:02 fills at 102
            [102m, 103m, 88m, 89m],         // 00:03 exit learnt
            [89m, 90m, 88m, 89m],           // 00:04 placed; closes under 100
            [120m, 125m, 119m, 121m],       // 00:05 the exit fills at 120, and the close is above 100: entry learnt
            [121m, 122m, 120m, 121m],       // 00:06 placed
            [130m, 131m, 129m, 130m],       // 00:07 fills at 130
            [130m, 131m, 129m, 130m]));

        Assert.Equal([Start, Start.AddMinutes(3), Start.AddMinutes(5)],
            result.Trace.Of(BacktestEventKind.Signal).Select(s => s.Bar));
        Assert.Equal([(Start.AddMinutes(2), 102m), (Start.AddMinutes(7), 130m)],
            result.Trace.Of(BacktestEventKind.Fill).Select(f => (f.Bar, f.Price!.Value)));
        Assert.Equal((Start.AddMinutes(5), 120m), (result.Trades[0].ExitBar, result.Trades[0].ExitPrice));
    }

    /// <summary>
    /// (e) A STOP OR A TARGET EXIT STILL ENTERS ON ITS OWN BAR. Protection is an order already at the venue: it fills
    /// on the minute it is touched, and that minute closes flat with no order of the run in flight, so an entry rule
    /// true at its close signals there, on the paper runner and in the backtest alike. Green on the base as well.
    /// </summary>
    [Fact]
    public void A_stop_or_target_exit_still_enters_on_its_own_bar()
    {
        foreach (var (level, exit, reason) in new[]
                 {
                     ("stop percent 5", new[] { 101m, 102m, 90m, 105m }, ExitReason.Stop),
                     ("target percent 5", new[] { 101m, 110m, 100m, 105m }, ExitReason.Target)
                 })
        {
            var program = Program($"""
                instrument BTCUSDT
                size fixed 1
                {level}
                entry when close > 100
                """);

            var result = RunOn(program, Bars(
                [100m, 102m, 99m, 101m],    // 00:00 entry learnt
                [101m, 102m, 100m, 101m],
                [101m, 102m, 100m, 101m],
                [101m, 102m, 100m, 101m],
                [101m, 102m, 100m, 101m],
                exit,                       // 00:05 the level is touched, the minute closes at 105
                [105m, 106m, 104m, 105m],
                [106m, 107m, 105m, 106m]));

            var first = result.Trades[0];
            Assert.Equal(reason, first.Reason);
            Assert.Equal(Start.AddMinutes(5), first.ExitBar);

            var again = result.Trace.Of(BacktestEventKind.Signal).ElementAt(1);
            Assert.Equal((Start.AddMinutes(5), nameof(IntentKind.Enter)), (again.Bar, again.Status!));
        }
    }

    /// <summary>
    /// (h) AN ORDER DUE AFTER THE WINDOW IS NO TRADE, IN WORDS. The entry learnt at 00:00 is due at 00:02's open and
    /// the window's last minute is 00:01: nothing fills, and the line names the minute it was due and why.
    ///
    /// <para><b>RED on the base:</b> it filled at 00:01's open.</para>
    /// </summary>
    [Fact]
    public void An_order_due_after_the_window_is_no_trade_in_words()
    {
        var result = RunOn(EnterAbove100(), Bars(
            [100m, 102m, 99m, 101m],
            [200m, 205m, 195m, 201m]));

        Assert.Empty(result.Trace.Of(BacktestEventKind.Fill));
        Assert.Empty(result.Trades);
        var refused = Assert.Single(result.Trace.Of(BacktestEventKind.NoTrade));
        Assert.Equal(
            $"the run's window ended before {Start.AddMinutes(2):O}, the first minute this order could fill at: an order "
            + "is placed inside the minute after the one it was decided on and fills at the open of the minute after that, "
            + "as on paper",
            refused.Reason);
        Assert.Equal(Start, refused.Bar);
    }

    /// <summary>
    /// (i) A RELATIVE LEVEL IS MEASURED FROM THE FILL, AND A FIXED ONE STAYS WHERE IT WAS DECLARED — what the paper
    /// runner does with a percent or an ATR level (ruling Q1); its re-anchoring of a FIXED level is a suspected runner
    /// defect of its own (<c>U-runner-fixed-levels</c>), not copied here.
    ///
    /// <para>The signal closes at 101 and the entry fills at 110. Five and ten percent of 101 are 5.05 and 10.1, so the
    /// relative program's stop and target rest at 104.95 and 120.1: 00:04's 115 is not its target and 00:05's 104
    /// is its stop. The fixed program's target, 111.1, is where it was declared: 00:04 reaches it.</para>
    ///
    /// <para><b>RED on the base</b> (levels from the signal's close): the relative program exits on its target at
    /// 111.1. <b>RED through the mutant</b> "levels absolute from the signal's close" — the same.</para>
    /// </summary>
    [Fact]
    public void A_relative_level_is_measured_from_the_fill_and_a_fixed_one_stays_where_it_was_declared()
    {
        var bars = Bars(
            [100m, 102m, 99m, 101m],        // 00:00 entry learnt at 101: 95.95 and 111.1 as declared
            [101m, 101m, 101m, 101m],       // 00:01 placed
            [110m, 110m, 110m, 110m],       // 00:02 fills at 110
            [110m, 110m, 110m, 110m],       // 00:03 protection placed
            [110m, 115m, 110m, 112m],       // 00:04 resting: 111.1 reached, 120.1 not
            [112m, 113m, 104m, 105m]);      // 00:05 104.95 reached

        var relative = RunOn(Program("""
            instrument BTCUSDT
            size fixed 1
            stop percent 5
            target percent 10
            entry when close > 100
            """), bars);
        var fixedLevels = RunOn(Program("""
            instrument BTCUSDT
            size fixed 1
            stop fixed 95.95
            target fixed 111.1
            entry when close > 100
            """), bars);

        var measured = relative.Trades[0];
        Assert.Equal((ExitReason.Stop, Start.AddMinutes(5), 104.95m), (measured.Reason, measured.ExitBar, measured.ExitPrice));

        var declared = fixedLevels.Trades[0];
        Assert.Equal((ExitReason.Target, Start.AddMinutes(4), 111.1m), (declared.Reason, declared.ExitBar, declared.ExitPrice));
    }
}

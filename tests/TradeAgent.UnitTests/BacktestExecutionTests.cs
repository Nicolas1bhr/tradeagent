using TradeAgent.Core.Data;
using TradeAgent.Core.Strategy;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEM 2 — THE DECLARED EXECUTION MODEL.
///
/// <para><b>The one that matters most.</b> An intent from bar n fills at bar n+1's OPEN. A backtest
/// that filled at the signal bar's own close would be buying at a price the decision had not been
/// taken at yet, and the result would be wrong in the flattering direction on every single trade —
/// which is the single most common way a backtest lies. Every fixture below is built so that the two
/// readings give visibly different numbers, rather than numbers that differ in the fourth decimal.</para>
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
    /// A SIGNAL FILLS AT THE NEXT BAR'S OPEN, NOT AT THE CLOSE THAT PRODUCED IT.
    ///
    /// <para>The fixture makes the difference impossible to miss: the signal bar closes at 101 and the
    /// next bar opens at 200. A fill at the signal's own close would be 101.</para>
    /// </summary>
    [Fact]
    public void A_signal_fills_at_the_next_bars_open_and_never_at_the_close_that_produced_it()
    {
        var result = RunOn(EnterAbove100(), Bars(
            [100m, 102m, 99m, 101m],        // bar 1: close 101 > 100 — the entry signal
            [200m, 205m, 195m, 201m],       // bar 2: it fills HERE, at 200
            [201m, 202m, 88m, 89m],         // bar 3: close 89 < 90 — the exit signal
            [300m, 305m, 295m, 299m]));     // bar 4: it fills HERE, at 300

        var trade = Assert.Single(result.Trades);
        Assert.Equal(200m, trade.EntryPrice);
        Assert.Equal(300m, trade.ExitPrice);
        Assert.Equal(2m, trade.Quantity);
        Assert.Equal(ExitReason.Rule, trade.Reason);
        Assert.Equal(200m, trade.Pnl);                       // (300 - 200) * 2
        Assert.Equal(Start.AddMinutes(1), trade.EntryBar);   // the bar it filled on, not the one it was decided on
        Assert.Equal(Start.AddMinutes(3), trade.ExitBar);

        // The trace says the same thing, and it is where every figure about this run comes from.
        var fill = Assert.Single(result.Trace.Of(BacktestEventKind.Fill));
        Assert.Equal(2L, fill.Ordinal);
        Assert.Equal(200m, fill.Price);

        // The intent was stamped with the bar it was DECIDED on, one bar earlier.
        var signal = result.Trace.Of(BacktestEventKind.Signal).First();
        Assert.Equal(1L, signal.Ordinal);
        Assert.Equal(BacktestOutcome.COMPLETED, result.Outcome);
    }

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
            [110m, 125m, 95m, 110m],        // the fill at 110, then BOTH are touched on this bar
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
            [110m, 111m, 109m, 110m],       // the fill at 110, nothing touched
            [80m, 81m, 75m, 78m],           // opens at 80, straight through the stop
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
            [100m, 105m, 95m, 101m],        // the entry fills at 100 * 1.0005
            [101m, 102m, 88m, 89m],
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
    /// <para>It is checked before the evaluator is asked anything on that bar, so the account the
    /// evaluator reads is already flat and its own <c>max_hold_bars</c> branch cannot fire a second
    /// exit. One intent came out of this whole run — the entry — and the exit is protection's.</para>
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
            [110m, 111m, 109m, 110m],       // fills at 110; held 0
            [111m, 113m, 110m, 112m],       // held 1 — under the limit
            [113m, 116m, 98m, 99m]));       // held 2 — protection closes at this bar's CLOSE, 99

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
        Assert.Contains("window ended before the next bar", refused.Reason);
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
            [100m, 105m, 95m, 101m],
            [101m, 102m, 100m, 101m]),
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
}

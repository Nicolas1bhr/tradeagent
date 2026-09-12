using TradeAgent.Core.Data;
using TradeAgent.Core.Strategy;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEM 3 — THE TRACE, AND THE METRICS COMPUTED FROM IT ALONE.
///
/// <para><b>The drawdown is the one worth the fixture.</b> Measured on closed trades it is 0 for a run
/// whose only position is still deeply under water — the most flattering answer arithmetic can give,
/// and the one a reader would act on. Measured on EQUITY, the open trade marked at every bar's close,
/// the same run reports the fall it really had. <c>Drawdown_is_measured_on_equity...</c> is red under
/// the closed-trades reading, by 61 against 0.</para>
///
/// <para><b>And the metrics have one input.</b> <see cref="BacktestMetrics.Of"/> takes the trace and
/// nothing else — no program, no model, no counter the runner kept — so a figure cannot be read off
/// the program's text. The mutant this class was watched against does exactly that: exposure bars
/// taken from the program's warm-up instead of counted, which is 7 where the truth is 5.</para>
/// </summary>
public class BacktestMetricsTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Start = new(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);

    static StrategyProgram Program(string text)
    {
        var parse = StrategyParser.Parse(text);
        Assert.True(parse.Ok, parse.Why);
        return parse.Program!;
    }

    static IReadOnlyList<KlineBar> Bars(params decimal[][] rows) =>
        [.. rows.Select((r, i) => new KlineBar(Start.AddMinutes(i), r[0], r[1], r[2], r[3], 1m))];

    static ExecutionModel Model(
        decimal? fees = null, decimal? slippage = null, decimal? increment = null, decimal? capital = null)
    {
        var declared = ExecutionModel.Declare(fees, slippage, increment, capital);
        Assert.True(declared.Ok, declared.Why);
        return declared.Model!;
    }

    BacktestResult RunOn(StrategyProgram program, IReadOnlyList<KlineBar> bars, ExecutionModel? model = null)
    {
        var result = Backtest.Run(
            program, new BacktestRequest(7, "sha", model ?? ExecutionModel.Frictionless), bars);
        log.WriteLine(result.Trace.Text);
        return result;
    }

    /// <summary>Enter above 100, leave below 60. No protection: the dip has to be survived, not stopped out.</summary>
    static StrategyProgram EnterAbove100() => Program("""
        instrument BTCUSDT
        size fixed 1
        exit when close < 60
        entry when close > 100
        """);

    /// <summary>
    /// EIGHT BARS WITH ONE COMPLETE TRADE AND A DEEP DIP INSIDE IT.
    ///
    /// <para>Hand-computed, with 10,000 of capital and no friction: the entry fills at bar 4's open of
    /// 100, so cash is 9,900 for the rest of the trade and equity is <c>9,900 + close</c> while it is
    /// open. Equity by bar: 10,000 · 10,000 · 10,000 · 10,000 · 9,970 · 10,020 · 9,959 · 10,010. The
    /// peak reaches 10,020 at bar 6 and the trough after it is 9,959 at bar 7, so the worst fall is
    /// <b>61</b>. The single closed trade is 100 in and 110 out: gross 10, a win.</para>
    /// </summary>
    static IReadOnlyList<KlineBar> OneTradeWithADip() => Bars(
        [95m, 96m, 94m, 95m],           // 1  flat, nothing fires
        [95m, 96m, 94m, 95m],           // 2  flat
        [100m, 102m, 99m, 101m],        // 3  close 101 > 100: the entry signal
        [100m, 101m, 99m, 100m],        // 4  it fills at 100. equity 10,000
        [100m, 100m, 69m, 70m],         // 5  the dip. equity 9,970
        [70m, 121m, 69m, 120m],         // 6  equity 10,020 — the peak
        [120m, 121m, 58m, 59m],         // 7  close 59 < 60: the exit signal. equity 9,959 — the trough
        [110m, 111m, 90m, 95m]);        // 8  it fills at 110. equity 10,010, flat, and nothing re-enters

    /// <summary>
    /// THE DRAWDOWN IS MEASURED ON EQUITY, AND THE OPEN TRADE IS IN IT.
    ///
    /// <para>61 is the fall from bar 6's 10,020 to bar 7's 9,959, and bar 7 is a bar the position was
    /// still open on. Measured on closed trades this run's drawdown is 0: nothing had closed yet.</para>
    /// </summary>
    [Fact]
    public void Drawdown_is_measured_on_equity_including_the_open_trade_and_not_on_closed_trades()
    {
        var metrics = RunOn(EnterAbove100(), OneTradeWithADip()).Metrics;

        Assert.Equal(61m, metrics.MaxDrawdown);
        Assert.Equal(10_010m, metrics.FinalEquity);
        Assert.False(metrics.PositionOpenAtEnd);
    }

    /// <summary>
    /// EVERY OTHER FIGURE OF THE SAME RUN, AND EVERY ONE OF THEM COUNTED FROM THE TRACE.
    ///
    /// <para>Exposure is bars 4 to 8 — five — because the position was open at some point in each of
    /// them, the bar it filled on and the bar it was sold on included. It is not <c>bars - warm-up</c>,
    /// which is 7 here, and that difference is the mutant this class exists for.</para>
    /// </summary>
    [Fact]
    public void Every_metric_is_counted_from_the_trace_and_the_exposure_is_not_the_programs_warm_up()
    {
        var result = RunOn(EnterAbove100(), OneTradeWithADip());
        var metrics = result.Metrics;

        Assert.Equal(8, metrics.Bars);
        Assert.Equal(5, metrics.ExposureBars);              // bars 4,5,6,7,8 — not 8 - 1
        Assert.Equal(2, metrics.Signals);                   // the entry and the exit
        Assert.Equal(1, metrics.Fills);
        Assert.Equal(1, metrics.Trades);
        Assert.Equal(1, metrics.Wins);
        Assert.Equal(1m, metrics.WinRate);
        Assert.Equal(10m, metrics.GrossPnl);
        Assert.Equal(0m, metrics.Fees);
        Assert.Equal(10m, metrics.NetPnl);
        Assert.Equal(0, metrics.Faults);
        Assert.Equal(0, metrics.Gaps);
        Assert.Equal(0, metrics.MissingMinutes);

        // AND THE SAME FIGURES COME OUT OF THE TRACE ON ITS OWN. Nothing about the program, the model
        // or the run's own counters is needed to reach them. (Field by field rather than by record
        // equality: a record compares its `Missing` list by REFERENCE, so two separately computed
        // metrics are never equal however identical they read.)
        var again = BacktestMetrics.Of(new BacktestTrace(result.Trace.Events));
        Assert.Equal(metrics.Bars, again.Bars);
        Assert.Equal(metrics.ExposureBars, again.ExposureBars);
        Assert.Equal(metrics.MaxDrawdown, again.MaxDrawdown);
        Assert.Equal(metrics.NetPnl, again.NetPnl);
        Assert.Equal(metrics.Missing.Select(g => g.Field), again.Missing.Select(g => g.Field));
    }

    /// <summary>
    /// NET PNL IS AFTER FEES, AND THE GROSS IS BESIDE IT RATHER THAN INSTEAD OF IT.
    ///
    /// <para>A strategy that is profitable before costs and unprofitable after them is a different
    /// finding from one that loses either way, and a single net figure cannot tell a reader which they
    /// have. The numbers are item 2's, recomputed here through the metrics: gross 39.78, fees 0.43998
    /// on the two fills, net 39.34002.</para>
    /// </summary>
    [Fact]
    public void Net_pnl_is_after_the_fees_of_both_fills_and_the_gross_is_reported_beside_it()
    {
        var metrics = RunOn(Program("""
            instrument BTCUSDT
            size fixed 2
            exit when close < 90
            entry when close > 100
            """), Bars(
            [100m, 102m, 99m, 101m],
            [100m, 105m, 95m, 101m],
            [101m, 102m, 88m, 89m],
            [120m, 125m, 115m, 119m]),
            Model(fees: 0.001m, slippage: 0.0005m)).Metrics;

        Assert.Equal(39.78m, metrics.GrossPnl);
        Assert.Equal(0.43998m, metrics.Fees);
        Assert.Equal(39.34002m, metrics.NetPnl);
        Assert.Equal(1, metrics.Trades);
    }

    /// <summary>
    /// A RUN THAT CLOSED NOTHING HAS NO WIN RATE, AND A POSITION LEFT OPEN IS SAID OUT LOUD.
    ///
    /// <para>A win rate of 0% over no trade is a fact about nothing and reads as a strategy that lost
    /// every time. And the open position's result is NOT in net pnl — it is in the equity the drawdown
    /// is measured on — which is the one sentence that keeps the two figures from contradicting each
    /// other in a reader's head.</para>
    /// </summary>
    [Fact]
    public void A_run_that_closed_nothing_has_a_labelled_dash_for_its_win_rate_and_names_the_open_trade()
    {
        var metrics = RunOn(EnterAbove100(), Bars(
            [100m, 102m, 99m, 101m],        // the entry signal
            [100m, 101m, 99m, 100m],        // it fills at 100
            [100m, 100m, 69m, 70m],         // 30 under water, and the window ends here
            [70m, 91m, 69m, 90m])).Metrics;

        Assert.Equal(0, metrics.Trades);
        Assert.Null(metrics.WinRate);
        Assert.Equal(BacktestMetrics.Dash, BacktestMetrics.Show(metrics.WinRate));
        Assert.True(metrics.PositionOpenAtEnd);

        Assert.Contains(metrics.Missing, g => g.Field == "win rate" && g.Why.Contains("nothing at all"));
        Assert.Contains(metrics.Missing, g => g.Field == "net pnl" && g.Why.Contains("still open"));

        // The drawdown is the open trade's: 10,000 down to 9,900 + 70.
        Assert.Equal(30m, metrics.MaxDrawdown);
        Assert.Equal(9_990m, metrics.FinalEquity);
    }

    /// <summary>
    /// GAPS AND FAULTS ARE COUNTED, AND EACH GETS A LABELLED DASH OF ITS OWN.
    ///
    /// <para>A backtest across a dead hour looks exactly like one across a live hour, and the only
    /// difference is this number. A faulted run's figures are real for the bars before the halt and
    /// mean nothing after it, which is what the gap says rather than leaving a reader to notice that
    /// the window was short.</para>
    /// </summary>
    [Fact]
    public void A_gap_and_a_fault_are_both_counted_and_both_named_in_the_missing_list()
    {
        // One minute simply absent: the dataset counts its gaps and fills none, and so does this.
        var withAGap = new[]
        {
            new KlineBar(Start, 95m, 96m, 94m, 95m, 1m),
            new KlineBar(Start.AddMinutes(1), 95m, 96m, 94m, 95m, 1m),
            new KlineBar(Start.AddMinutes(4), 95m, 96m, 94m, 95m, 1m)
        };

        var gapped = RunOn(EnterAbove100(), withAGap).Metrics;
        Assert.Equal(1, gapped.Gaps);
        Assert.Equal(2, gapped.MissingMinutes);
        Assert.Equal(3, gapped.Bars);
        Assert.Contains(gapped.Missing, g => g.Field == "coverage" && g.Why.Contains("no bar"));

        // A bar out of order is a defined fault: the run halts, and the figures stop being about the
        // window that was asked for.
        var outOfOrder = new[]
        {
            new KlineBar(Start, 95m, 96m, 94m, 95m, 1m),
            new KlineBar(Start.AddMinutes(1), 95m, 96m, 94m, 95m, 1m),
            new KlineBar(Start, 95m, 96m, 94m, 95m, 1m)
        };

        var faulted = RunOn(EnterAbove100(), outOfOrder);
        Assert.Equal(BacktestOutcome.FAULTED, faulted.Outcome);
        Assert.Equal(1, faulted.Metrics.Faults);
        Assert.Contains(faulted.Metrics.Missing,
            g => g.Field == "the end of the window" && g.Why.Contains("halted at bar"));
    }

    /// <summary>
    /// A WINDOW WITH NO BAR IN IT MEASURES NOTHING, AND SAYS SO IN ONE LINE RATHER THAN IN ZEROS.
    ///
    /// <para>Every figure would otherwise be 0 — no trades, no drawdown, no loss — which is a perfect
    /// result for a run that never happened.</para>
    /// </summary>
    [Fact]
    public void A_window_with_no_bars_measures_nothing_and_every_figure_is_a_labelled_dash()
    {
        var metrics = RunOn(EnterAbove100(), []).Metrics;

        Assert.Equal(0, metrics.Bars);
        Assert.Null(metrics.NetPnl);
        Assert.Null(metrics.MaxDrawdown);
        Assert.Null(metrics.WinRate);
        Assert.Null(metrics.FinalEquity);
        var only = Assert.Single(metrics.Missing);
        Assert.Equal("every figure", only.Field);
        Assert.Contains("nothing was measured at all", only.Why);
    }
}

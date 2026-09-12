using TradeAgent.Core.Data;
using TradeAgent.Core.Strategy;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEM 3 — A MISSING MINUTE IS NOT A BAR, AND NOTHING IS ANSWERED BEFORE WARM-UP.
///
/// <para>`docs/COUNCIL.md` requires "missing data reported and never filled" and "bounded lookbacks,
/// explicit warm-up". Both failures are SILENT, which is the whole reason they are tested here rather
/// than left to a reviewer: a five-bar mean over a window with a hole in it is a number of exactly the
/// right shape, and a fifty-bar average asked on bar thirty is the mean of thirty bars wearing the
/// other one's name. Neither shows up in a result. What shows up is trades the promoted program would
/// never have taken, attached to its id.</para>
///
/// <para>`KlineNormaliser` refuses to fill a gap when it WRITES a dataset; the evaluator refuses to
/// fill one when it READS it, and counts them again on the way past — because the dataset's count is
/// over the whole file and a run is over a window.</para>
/// </summary>
public class EvaluatorBarIntakeTests
{
    static StrategyProgram Program(string text)
    {
        var parse = StrategyParser.Parse(text);
        Assert.True(parse.Ok, parse.Why);
        return parse.Program!;
    }

    /// <summary>A five-bar mean, one exit and one entry: the smallest program that reads a window.</summary>
    static StrategyProgram FiveBarMean() => Program("""
        instrument BTCUSDT
        indicator avg = sma(close, 5)
        size fixed 1
        exit when close < avg
        entry when close > avg
        """);

    static AccountReading Flat(EvaluationState state, KlineBar bar) => AccountReading.Flat(10_000m);

    /// <summary>
    /// THE GAP. Bar 20 of the fixture is removed, so the five-bar mean on bar 23 is the mean of the
    /// five bars that EXIST — 103.50, 102.00, 99.00, 97.50, 96.00 — and not of four bars and a
    /// stand-in. The missing minute advances the lookback by nothing and is counted and reported.
    /// </summary>
    [Fact]
    public void A_missing_minute_advances_no_lookback_and_is_counted_rather_than_filled()
    {
        var bars = SyntheticBars.Forty.Where((_, i) => i != 20).ToList();
        var upTo = bars.TakeWhile(b => b.OpenTime <= SyntheticBars.Forty[23].OpenTime).ToList();

        var run = StrategyEvaluator.Run(FiveBarMean(), upTo, Flat);

        var existing = new[] { 18, 19, 21, 22, 23 }.Select(i => SyntheticBars.Forty[i].Close).ToList();
        Assert.Equal(5, existing.Count);
        Assert.Equal(99.60m, existing.Sum() / 5m);

        Assert.Null(run.FaultReason);
        Assert.Equal(99.60m, run.State.IndicatorValue("avg"));
        Assert.Equal(existing.Sum() / 5m, run.State.IndicatorValue("avg"));

        Assert.Equal(23, run.Counters.Bars);                    // 24 minutes, one of them empty
        Assert.Equal(1, run.Counters.MissingMinutes);
        Assert.Equal(1, run.Counters.GapRuns);

        var gap = Assert.Single(run.GapRuns);
        Assert.Equal(SyntheticBars.Forty[20].OpenTime, gap.From);
        Assert.Equal(SyntheticBars.Forty[20].OpenTime, gap.To);
        Assert.Equal(1, gap.Minutes);
    }

    /// <summary>
    /// The bar BEFORE the gap and the bar after it are consecutive to the evaluator — `close[1]` on
    /// bar 21 is bar 19, an extra minute earlier in wall-clock time. That is what "a gap advances no
    /// lookback" means, and the gap count is what tells a reader the window is wider than it looks.
    /// </summary>
    [Fact]
    public void The_bar_before_a_gap_is_the_previous_bar_and_the_run_says_how_wide_that_was()
    {
        var bars = SyntheticBars.Forty.Where((_, i) => i != 20).Take(21).ToList();

        var run = StrategyEvaluator.Run(FiveBarMean(), bars, Flat);

        Assert.Equal(SyntheticBars.Forty[21].OpenTime, run.State.LastBar);
        Assert.Equal(21, run.Counters.Bars);       // twenty-two minutes, one of them empty
        Assert.Equal(1, run.Counters.MissingMinutes);
        Assert.Equal(TimeSpan.FromMinutes(2), run.State.LastBar!.Value - SyntheticBars.Forty[19].OpenTime);
    }

    /// <summary>
    /// EVALUATION BEFORE THE WARM-UP IS REFUSED. A twenty-bar mean is asked nothing on bar nineteen —
    /// the event is consumed, the indicator is fed, and no rule is evaluated — and the counters say
    /// how many events that was.
    /// </summary>
    [Fact]
    public void Nothing_is_evaluated_before_the_programs_own_warm_up()
    {
        var program = Program("""
            instrument BTCUSDT
            indicator avg = sma(close, 20)
            size fixed 1
            exit when close < avg
            entry when close > avg
            """);

        Assert.Equal(20, program.WarmUpBars);

        var state = EvaluationState.Start(program);
        var statuses = new List<EvaluationStatus>();

        foreach (var bar in SyntheticBars.Forty)
        {
            var outcome = StrategyEvaluator.Step(state, bar, AccountReading.Flat(10_000m));
            statuses.Add(outcome.Status);

            if (outcome.Status == EvaluationStatus.WarmingUp)
                Assert.Null(state.IndicatorValue("avg"));
        }

        Assert.Equal(19, statuses.Count(s => s == EvaluationStatus.WarmingUp));
        Assert.All(statuses.Take(19), s => Assert.Equal(EvaluationStatus.WarmingUp, s));
        Assert.DoesNotContain(EvaluationStatus.WarmingUp, statuses.Skip(19));
        Assert.Equal(19, state.Counters.WarmingUpEvents);
        Assert.Equal(21, state.Counters.EvaluatedEvents);
        Assert.False(state.Faulted);
    }

    /// <summary>
    /// Bars in ascending order is an INPUT CONTRACT, and breaking it is a defined outcome rather than
    /// an exception: a run fed the same minute twice counts one lookback twice, and a run fed them
    /// backwards answers from a bar it has already passed.
    /// </summary>
    [Fact]
    public void A_bar_out_of_order_or_repeated_halts_the_run_with_a_reason()
    {
        var state = EvaluationState.Start(FiveBarMean());
        var account = AccountReading.Flat(10_000m);

        Assert.Equal(EvaluationStatus.WarmingUp,
            StrategyEvaluator.Step(state, SyntheticBars.Forty[5], account).Status);

        var backwards = StrategyEvaluator.Step(state, SyntheticBars.Forty[3], account);

        Assert.Equal(EvaluationStatus.Faulted, backwards.Status);
        Assert.Contains("is not after the bar at", backwards.FaultReason!, StringComparison.Ordinal);
        Assert.Contains("ascending order", backwards.FaultReason!, StringComparison.Ordinal);
        Assert.Null(backwards.Intent);

        // A faulted state stays faulted, and the next bar is not evaluated either.
        var after = StrategyEvaluator.Step(state, SyntheticBars.Forty[9], account);
        Assert.Equal(EvaluationStatus.Faulted, after.Status);
        Assert.Equal(backwards.FaultReason, after.FaultReason);
        Assert.Equal(1, state.Counters.Bars);
    }

    [Fact]
    public void The_same_bar_twice_is_the_same_fault()
    {
        var state = EvaluationState.Start(FiveBarMean());
        var account = AccountReading.Flat(10_000m);

        StrategyEvaluator.Step(state, SyntheticBars.Forty[0], account);
        var again = StrategyEvaluator.Step(state, SyntheticBars.Forty[0], account);

        Assert.Equal(EvaluationStatus.Faulted, again.Status);
        Assert.Contains("never twice", again.FaultReason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A bar that is not a whole number of intervals after the one before it is not part of this
    /// series at all — two datasets spliced, or a five-minute candle in a one-minute run — and a gap
    /// count computed over it would be arithmetic about nothing.
    /// </summary>
    [Fact]
    public void A_bar_off_the_interval_grid_halts_the_run()
    {
        var state = EvaluationState.Start(FiveBarMean());
        var account = AccountReading.Flat(10_000m);
        var first = SyntheticBars.Forty[0];

        StrategyEvaluator.Step(state, first, account);
        var offGrid = StrategyEvaluator.Step(
            state, first with { OpenTime = first.OpenTime.AddSeconds(90) }, account);

        Assert.Equal(EvaluationStatus.Faulted, offGrid.Status);
        Assert.Contains("whole number of", offGrid.FaultReason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE COUNT IS NEVER BOUNDED; THE LIST IS. A dead venue can leave tens of thousands of runs and
    /// the report is read by a person, so the list stops at
    /// <see cref="KlineNormaliser.MaxGapRunsListed"/> and says it was cut short — the same split the
    /// dataset ledger makes.
    /// </summary>
    [Fact]
    public void Many_gaps_are_all_counted_and_the_listed_runs_are_bounded()
    {
        var start = new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);
        var everyOtherMinute = Enumerable.Range(0, 61)
            .Select(i => new KlineBar(start.AddMinutes(i * 2), 100m, 101m, 99m, 100m, 1m))
            .ToList();

        var run = StrategyEvaluator.Run(FiveBarMean(), everyOtherMinute, Flat);

        Assert.Null(run.FaultReason);
        Assert.Equal(61, run.Counters.Bars);
        Assert.Equal(60, run.Counters.MissingMinutes);
        Assert.Equal(60, run.Counters.GapRuns);
        Assert.Equal(KlineNormaliser.MaxGapRunsListed, run.GapRuns.Count);
        Assert.True(run.State.GapRunsTruncated);
    }

    /// <summary>
    /// A LONG GAP IS ONE RUN OF MANY MINUTES. An hour with no bars is one hole an hour wide, not
    /// sixty holes, and the run that reports it says both numbers.
    /// </summary>
    [Fact]
    public void An_hour_with_no_bars_is_one_run_of_fifty_nine_minutes()
    {
        var start = new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);
        var bars = Enumerable.Range(0, 10).Select(i => new KlineBar(start.AddMinutes(i), 100m, 101m, 99m, 100m, 1m))
            .Append(new KlineBar(start.AddMinutes(69), 100m, 101m, 99m, 100m, 1m))
            .ToList();

        var run = StrategyEvaluator.Run(FiveBarMean(), bars, Flat);

        Assert.Equal(11, run.Counters.Bars);
        Assert.Equal(59, run.Counters.MissingMinutes);
        Assert.Equal(1, run.Counters.GapRuns);

        var gap = Assert.Single(run.GapRuns);
        Assert.Equal(start.AddMinutes(10), gap.From);
        Assert.Equal(start.AddMinutes(68), gap.To);
        Assert.Equal(59, gap.Minutes);
    }
}

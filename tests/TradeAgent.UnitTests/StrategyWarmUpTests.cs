using TradeAgent.Core.Strategy;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// HOW MANY CLOSED BARS BEFORE THIS PROGRAM MAY BE ASKED ANYTHING.
///
/// <para>`docs/COUNCIL.md` requires warm-up to be EXPLICIT: "bounded lookbacks, explicit warm-up,
/// missing data reported and never filled". The reason is the failure it prevents. A 50-bar average
/// evaluated on bar 30 is not a 50-bar average — it is the mean of thirty bars, wearing the name of
/// the other thing — and every signal it produces is a signal the strategy never expressed. Nothing
/// in the numbers looks wrong; the backtest simply contains trades that the promoted program would
/// not have taken, and the evidence they produce is evidence about a different program.</para>
///
/// <para>So the number is computed here, stated on the frozen program, and hashed into its id; and
/// `U-runner-2` refuses to evaluate a bar before it. One bar short is the whole defect, which is why
/// the mutant for this item is `max − 1`.</para>
/// </summary>
public class StrategyWarmUpTests
{
    static StrategyProgram Parsed(string source)
    {
        var parse = StrategyParser.Parse(source);
        Assert.True(parse.Ok, $"expected a program, got: {parse.Why}");
        return parse.Program!;
    }

    static int WarmUp(params string[] middle) =>
        Parsed(string.Join("\n", ["instrument BTCUSDT", .. middle])).WarmUpBars;

    [Fact]
    public void A_twenty_bar_average_is_warm_after_twenty_bars()
    {
        Assert.Equal(20, WarmUp(
            "indicator fastma = sma(close, 20)",
            "size fixed 1",
            "exit when close < fastma",
            "entry when close > fastma"));
    }

    /// <summary>
    /// EACH INDICATOR'S OWN WARM-UP, from the table in `docs/STRATEGY-LANGUAGE.md`. `rsi` and `atr`
    /// need one bar more than their period because both are over CHANGES between bars: fourteen
    /// changes take fifteen bars.
    /// </summary>
    [Theory]
    [InlineData("sma(close, 20)", 20)]
    [InlineData("ema(close, 20)", 20)]
    [InlineData("rsi(close, 14)", 15)]
    [InlineData("atr(14)", 15)]
    [InlineData("highest(high, 30)", 30)]
    [InlineData("lowest(low, 30)", 30)]
    public void Each_indicator_warms_up_over_its_own_period(string call, int expected)
    {
        Assert.Equal(expected, WarmUp(
            $"indicator ind = {call}", "size fixed 1", "exit when close < 1", "entry when close > 1"));
    }

    [Fact]
    public void An_opening_range_accumulator_is_warm_on_its_first_closed_bar()
    {
        Assert.Equal(1, WarmUp(
            "indicator orh = opening_range_high()",
            "opening_range 09:30-10:00",
            "size fixed 1",
            "exit when close < orh",
            "entry when close > orh"));
    }

    /// <summary>The maximum over everything declared, which is the one number the evaluator can wait on.</summary>
    [Fact]
    public void The_warm_up_is_the_deepest_lookback_in_the_program()
    {
        Assert.Equal(50, WarmUp(
            "indicator fastma = sma(close, 20)",
            "indicator slowma = sma(close, 50)",
            "size fixed 1",
            "exit when close < fastma",
            "entry when close > slowma"));

        // A DECLARED INDICATOR NOBODY READS still counts: it is part of the program, the interpreter
        // keeps it, and a program that became warm sooner because a rule stopped mentioning it would
        // change its own warm-up on an edit that changed nothing else.
        Assert.Equal(200, WarmUp(
            "indicator unused = sma(close, 200)",
            "size fixed 1",
            "exit when close < 1",
            "entry when close > 1"));
    }

    /// <summary>
    /// HISTORY DEEPENS IT, AND SO DOES A CROSSING — nesting included, which is the half a warm-up
    /// computed from indicator periods alone would miss. `fastma[2]` needs the average AND two bars
    /// more of it; `crosses_above(a, b)` reads both sides on this bar and on the one before.
    /// </summary>
    [Fact]
    public void History_and_crossings_deepen_the_warm_up()
    {
        Assert.Equal(4, WarmUp("size fixed 1", "exit when close < 1", "entry when close[3] > open"));

        Assert.Equal(12, WarmUp(
            "indicator mid = sma(close, 10)",
            "size fixed 1",
            "exit when close < 1",
            "entry when mid[2] > close"));

        Assert.Equal(11, WarmUp(
            "indicator mid = sma(close, 10)",
            "size fixed 1",
            "exit when close < 1",
            "entry when crosses_above(close, mid)"));

        // Nested: the crossing is inside an `and`, over a history reference of an indicator.
        Assert.Equal(13, WarmUp(
            "indicator mid = sma(close, 10)",
            "size fixed 1",
            "exit when close < 1",
            "entry when crosses_above(close, mid[2]) and volume > 100"));
    }

    /// <summary>
    /// THE STOP'S OWN ATR PERIOD COUNTS. `stop atr 2 14` measures a distance at the entry bar, so a
    /// program that entered before its ATR was warm would place a stop off a number that is not the
    /// number the program asked for — and the stop is the whole of what risk sizing divides by.
    /// </summary>
    [Fact]
    public void The_stops_own_atr_period_counts_even_with_no_indicator_declared()
    {
        Assert.Equal(15, WarmUp(
            "size risk_fraction 0.01",
            "stop atr 2 14",
            "exit when close < 1",
            "entry when close > 1"));
    }

    [Fact]
    public void A_program_that_reads_no_history_is_warm_after_one_bar()
    {
        Assert.Equal(1, WarmUp("size fixed 1", "exit when false", "entry when true"));
        Assert.Equal(1, WarmUp("size fixed 1", "exit when close < 100", "entry when close > 200"));
    }

    /// <summary>
    /// The frozen program STATES it, in the form it is hashed in: a build that changed its mind about
    /// how warm is warm enough would produce a different id rather than the same id over a different
    /// meaning.
    /// </summary>
    [Fact]
    public void The_canonical_form_states_the_warm_up()
    {
        var p = Parsed("""
            instrument BTCUSDT
            const fast = 20
            const slow = 50
            indicator fastma = sma(close, fast)
            indicator slowma = sma(close, slow)
            size fixed 1
            exit when crosses_below(fastma, slowma)
            entry when crosses_above(fastma, slowma)
            """);

        Assert.Equal(51, p.WarmUpBars);                     // the 50-bar average, plus the crossing's own bar
        Assert.Contains("\nwarmup 51\n", p.Canonical, StringComparison.Ordinal);
    }

    /// <summary>
    /// A WARM-UP OVER THE LIMIT IS A REFUSAL, not a program that waits forever. The limit is one
    /// number for the period and for the warm-up (`StrategyLimits.MaxLookbackBars`), so a period at
    /// the limit inside a crossing — which needs one bar more — is a program that could never be warm
    /// enough to act.
    /// </summary>
    [Fact]
    public void A_warm_up_over_the_limit_is_refused()
    {
        var parse = StrategyParser.Parse(string.Join("\n", [
            "instrument BTCUSDT",
            $"indicator slowma = sma(close, {StrategyLimits.MaxLookbackBars})",
            "size fixed 1",
            "exit when close < slowma",
            "entry when crosses_above(close, slowma)"]));

        Assert.False(parse.Ok, "expected a refusal, got a program");
        Assert.Contains(StrategyLimits.MaxLookbackBars.ToString(), parse.Why, StringComparison.Ordinal);
        Assert.Contains((StrategyLimits.MaxLookbackBars + 1).ToString(), parse.Why, StringComparison.Ordinal);
    }
}

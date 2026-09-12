using System.Text;
using TradeAgent.Core.Strategy;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// WHY A TEXT IS NOT A PROGRAM, AND THAT THE ANSWER ALWAYS NAMES THE LINE.
///
/// <para>The writer of a strategy is a cheap model with no terminal, no debugger and no second
/// opinion: the refusal IS the whole of what it gets to work from. A refusal that says "invalid
/// program" costs a turn and produces a guess; one that says `line 4: a period must be above 0, and
/// sma(close, 0) asks for 0` is a repair. So every case below asserts the line as well as the
/// reason.</para>
///
/// <para>And the last test is the one that makes the rest safe to rely on: the parser is TOTAL. A
/// program arrives as a file an agent wrote, and `CLAUDE.md` says material the agent produces is
/// data — so 8 KiB of brackets, a binary file dropped in the wrong folder and 5,000 nested
/// parentheses are refusals, not exceptions, and certainly not a stack overflow that no `catch` can
/// see.</para>
/// </summary>
public class StrategyRefusalTests
{
    /// <summary>A complete, valid program with one line replaced, so that exactly one thing is wrong.</summary>
    static string With(params string[] lines) => string.Join("\n", lines);

    static StrategyRefusal Refused(string source)
    {
        var parse = StrategyParser.Parse(source);
        Assert.False(parse.Ok, "expected a refusal, got a program");
        Assert.NotNull(parse.Refusal);
        return parse.Refusal;
    }

    static void Parses(string source)
    {
        var parse = StrategyParser.Parse(source);
        Assert.True(parse.Ok, $"expected a program, got: {parse.Why}");
    }

    const string Instrument = "instrument BTCUSDT";
    const string Size = "size fixed 1";
    const string Exit = "exit when close < 1";
    const string Entry = "entry when close > 1";

    /// <summary>
    /// A PERIOD OF ZERO. The mean of no bars is not a number, and an indicator that quietly answered
    /// one anyway would be a program whose backtest is a different program's backtest.
    /// </summary>
    [Theory]
    [InlineData("indicator a = sma(close, 0)", 2)]
    [InlineData("indicator a = sma(close, -5)", 2)]
    [InlineData("indicator a = ema(close, 0)", 2)]
    [InlineData("indicator a = atr(0)", 2)]
    [InlineData("indicator a = highest(high, 0)", 2)]
    public void A_period_at_or_below_zero_is_refused_naming_the_line(string line, int expected)
    {
        var refusal = Refused(With(Instrument, line, Size, Exit, Entry));

        Assert.Equal(expected, refusal.Line);
        Assert.Contains("0", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_period_of_zero_is_refused_wherever_a_period_is_written()
    {
        // Through a constant, which is where a period is most likely to be zero by accident.
        Assert.Equal(3, Refused(With(Instrument, "const p = 0", "indicator a = sma(close, p)", Size, Exit, Entry)).Line);

        // On the stop's own ATR period, and on the holding time, which are periods too.
        Assert.Equal(3, Refused(With(Instrument, "indicator a = atr(14)", "stop atr 2 0", Size, Exit, Entry)).Line);
        Assert.Equal(2, Refused(With(Instrument, "max_hold_bars 0", Size, Exit, Entry)).Line);
    }

    [Fact]
    public void An_undeclared_name_is_refused_naming_the_line()
    {
        var inAPeriod = Refused(With(Instrument, "indicator a = sma(close, slowp)", Size, Exit, Entry));
        Assert.Equal(2, inAPeriod.Line);
        Assert.Contains("slowp", inAPeriod.Reason, StringComparison.Ordinal);
        Assert.Contains("constant", inAPeriod.Reason, StringComparison.Ordinal);

        var inACondition = Refused(With(Instrument, Size, Exit, "entry when close > drift"));
        Assert.Equal(4, inACondition.Line);
        Assert.Contains("drift", inACondition.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A TYPE MISMATCH. Two types is all there is, which is what makes every mismatch findable at
    /// parse time rather than on the bar where a number was asked whether it was true.
    /// </summary>
    [Theory]
    [InlineData("entry when close")]                        // a number where a condition belongs
    [InlineData("entry when close + 1")]
    [InlineData("entry when not close")]                    // `not` over a number
    [InlineData("entry when close > 1 and volume")]         // `and` over a number
    [InlineData("entry when (close > 1) + 2 > 3")]          // arithmetic over a condition
    [InlineData("entry when close > (volume > 1)")]         // comparison against a condition
    [InlineData("entry when crosses_above(close, true)")]   // a crossing of a true/false value
    [InlineData("entry when -true")]
    public void A_type_mismatch_is_refused_naming_the_line(string rule)
    {
        var refusal = Refused(With(Instrument, Size, Exit, rule));
        Assert.Equal(4, refusal.Line);
    }

    [Fact]
    public void A_true_false_constant_cannot_be_compared_with_a_price()
    {
        var refusal = Refused(With(Instrument, "const usestop = true", Size, Exit, "entry when close > usestop"));

        Assert.Equal(5, refusal.Line);
        Assert.NotEqual("", refusal.Reason);
    }

    /// <summary>
    /// SIZING IS STATED EXACTLY ONCE. None is undefined; two is ambiguous, and which of the two wins
    /// would be a fact about the order of the lines rather than about the strategy.
    /// </summary>
    [Fact]
    public void Sizing_that_is_undefined_or_ambiguous_is_refused()
    {
        var undefined = Refused(With(Instrument, Exit, Entry));
        Assert.Equal(0, undefined.Line);                    // nothing on a line is wrong: a line is missing
        Assert.Contains("size", undefined.Reason, StringComparison.Ordinal);
        Assert.StartsWith("program:", undefined.Text, StringComparison.Ordinal);

        var ambiguous = Refused(With(Instrument, Size, "size capital_fraction 0.5", Exit, Entry));
        Assert.Equal(3, ambiguous.Line);
        Assert.Contains("twice", ambiguous.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// RISK SIZING WITH NOTHING TO MEASURE RISK AGAINST. `risk_fraction` is a fraction of equity
    /// risked over the STOP DISTANCE; with no stop there is no distance, so the quantity is a
    /// division by nothing. The refusal names the size line, because that is the line that has to
    /// change (or the stop that has to be added).
    /// </summary>
    [Fact]
    public void Risk_sizing_with_no_stop_is_refused_naming_the_size_line()
    {
        var refusal = Refused(With(Instrument, "size risk_fraction 0.01", Exit, Entry));

        Assert.Equal(2, refusal.Line);
        Assert.Contains("stop", refusal.Reason, StringComparison.Ordinal);

        // The same program with a stop is a program.
        Parses(With(Instrument, "size risk_fraction 0.01", "stop percent 1", Exit, Entry));
    }

    /// <summary>
    /// EVERY LIMIT IS A NUMBER IN `StrategyLimits` AND A REFUSAL THAT CITES IT. The point of the
    /// bound is not that a big program is slow: `docs/COUNCIL.md` requires the runner to bound
    /// program size, parse work, lookback and per-event computation, and a bound nobody can read off
    /// as a set is one somebody has to rediscover from a timeout.
    /// </summary>
    [Fact]
    public void A_lookback_over_the_limit_is_refused_naming_the_line()
    {
        var refusal = Refused(With(
            Instrument, $"indicator a = sma(close, {StrategyLimits.MaxLookbackBars + 1})", Size, Exit, Entry));

        Assert.Equal(2, refusal.Line);
        Assert.Contains(StrategyLimits.MaxLookbackBars.ToString(), refusal.Reason, StringComparison.Ordinal);

        Parses(With(Instrument, $"indicator a = sma(close, {StrategyLimits.MaxLookbackBars})", Size, Exit, Entry));
    }

    [Fact]
    public void A_history_depth_over_the_limit_is_refused_naming_the_line()
    {
        var refusal = Refused(With(Instrument, Size, Exit, $"entry when close[{StrategyLimits.MaxHistoryDepth + 1}] > 1"));

        Assert.Equal(4, refusal.Line);
        Assert.Contains(StrategyLimits.MaxHistoryDepth.ToString(), refusal.Reason, StringComparison.Ordinal);

        Parses(With(Instrument, Size, Exit, $"entry when close[{StrategyLimits.MaxHistoryDepth}] > 1"));
    }

    [Fact]
    public void A_rule_count_over_the_limit_is_refused_naming_the_line()
    {
        var lines = new List<string> { Instrument, Size };
        for (var i = 0; i < StrategyLimits.MaxRules; i++) lines.Add(Exit);
        lines.Add(Entry);                                   // one rule past the limit

        var refusal = Refused(With([.. lines]));
        Assert.Equal(lines.Count, refusal.Line);
        Assert.Contains(StrategyLimits.MaxRules.ToString(), refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_node_count_over_the_limit_is_refused_naming_the_line_that_passed_it()
    {
        // Sixteen conjuncts is 63 nodes and fits the line-length limit; four such rules pass 200.
        var wide = string.Join(" and ", Enumerable.Repeat("close > 1", 16));
        var lines = new[] { Instrument, Size, $"exit when {wide}", $"exit when {wide}", $"exit when {wide}", $"entry when {wide}" };

        var refusal = Refused(With(lines));
        Assert.Equal(6, refusal.Line);
        Assert.Contains(StrategyLimits.MaxNodes.ToString(), refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_condition_that_nests_past_the_limit_is_refused_naming_the_line()
    {
        var deep = new string('(', StrategyLimits.MaxExpressionDepth + 2) + "close"
            + new string(')', StrategyLimits.MaxExpressionDepth + 2);

        var refusal = Refused(With(Instrument, Size, Exit, $"entry when {deep} > 1"));
        Assert.Equal(4, refusal.Line);
        Assert.Contains(StrategyLimits.MaxExpressionDepth.ToString(), refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_program_too_long_or_too_wide_is_refused()
    {
        var tooManyLines = new List<string> { Instrument, Size, Exit, Entry };
        while (tooManyLines.Count <= StrategyLimits.MaxLines) tooManyLines.Add("# filler");
        var lines = Refused(With([.. tooManyLines]));
        Assert.Contains(StrategyLimits.MaxLines.ToString(), lines.Reason, StringComparison.Ordinal);

        var wide = Refused(With(Instrument, Size, Exit, "# " + new string('x', StrategyLimits.MaxLineLength)));
        Assert.Equal(4, wide.Line);
        Assert.Contains(StrategyLimits.MaxLineLength.ToString(), wide.Reason, StringComparison.Ordinal);

        var big = new StringBuilder();
        while (big.Length <= StrategyLimits.MaxSourceBytes) big.Append("# ").Append('x', 200).Append('\n');
        var bytes = Refused(big.ToString());
        Assert.Contains(StrategyLimits.MaxSourceBytes.ToString(), bytes.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE PARSER IS TOTAL. Not "handles bad input well": for every string there is either a program
    /// or a refusal, and the deep-nesting case is here rather than only above because a stack
    /// overflow is the one failure a `catch` cannot turn into a refusal — which is why the depth is
    /// checked on the way DOWN.
    /// </summary>
    [Fact]
    public void Nothing_throws_out_of_the_parser_for_any_input()
    {
        var inputs = new List<string?>
        {
            null, "", " ", "\n", "\r\n\r\n", "\t\t", "#", "# only a comment",
            "instrument", "instrument ", "const", "const =", "const x =", "indicator x =",
            "size", "stop", "target", "weekdays", "entry_window", "opening_range", "session_exit",
            "exit", "entry", "exit when", "entry when ", "entry when (", "entry when )",
            "entry when close >", "entry when > close", "entry when close ==", "entry when close = 1",
            "entry when close ! 1", "entry when close[", "entry when close[]", "entry when close[-1]",
            "entry when close[1.5]", "entry when crosses_above(close)", "entry when crosses_above(close,)",
            "﻿instrument BTCUSDT", "instrument BTCUSDT ", "instrument éèê",
            "ENTRY WHEN CLOSE > 1", "entry when \"close\" > 1", "entry when close > 1e9",
            new string('(', 5_000), "entry when " + new string('(', 5_000) + "close",
            new string('x', 9_000), string.Join("\n", Enumerable.Repeat("entry when close > 1", 500)),
            "ÿ", "entry when close > 0.0.0.1", "instrument BTCUSDT\nsize fixed 0",
        };

        foreach (var input in inputs)
        {
            var parse = StrategyParser.Parse(input);
            Assert.True(parse.Ok ^ (parse.Refusal is not null),
                $"neither a program nor a refusal, or both, for: {Show(input)}");
            if (!parse.Ok) Assert.NotEqual("", parse.Why);
        }

        // The one input that is a program: everything above is a refusal, and this proves the
        // harness is not passing because `Parse` refuses unconditionally.
        Parses(With(Instrument, Size, Exit, Entry));
    }

    static string Show(string? input) =>
        input is null ? "<null>" : input.Length > 40 ? input[..40] + "…" : input;
}

using TradeAgent.Core.Strategy;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE LANGUAGE: TEXT IN, A TYPED PROGRAM OUT — AND WHAT CANNOT BE SPELLED IN IT AT ALL.
///
/// <para>`docs/COUNCIL.md`, "The strategy language, v1": programs declare typed constants, indicator
/// expressions and ordered rules, and the list of things they may never do is long — shell, files,
/// network, imports, clocks, randomness, recursion, unbounded loops, model calls, training,
/// multi-instrument, shorting, leverage. The second half of this class is that list, one case each,
/// because "the AST has no node for it" is a claim about a type and this is how a claim about a type
/// is checked: try to write it and watch the parser name the line.</para>
///
/// <para>The refusal DETAIL — periods, types, limits, sizing — is `StrategyRefusalTests`. This class
/// is about what a program is.</para>
/// </summary>
public class StrategyLanguageTests
{
    /// <summary>The day-one moving-average crossover, which `docs/COUNCIL.md:164` requires the language to express.</summary>
    const string Crossover = """
        # ma-crossover: long while the fast average is above the slow one.
        instrument BTCUSDT
        timezone UTC
        const fast = 20
        const slow = 50
        indicator fastma = sma(close, fast)
        indicator slowma = sma(close, slow)
        size fixed 1
        stop percent 1.5
        exit when crosses_below(fastma, slowma)
        entry when crosses_above(fastma, slowma)
        """;

    static StrategyProgram Parsed(string source)
    {
        var parse = StrategyParser.Parse(source);
        Assert.True(parse.Ok, $"expected a program, got: {parse.Why}");
        return parse.Program!;
    }

    static StrategyRefusal Refused(string source)
    {
        var parse = StrategyParser.Parse(source);
        Assert.False(parse.Ok, "expected a refusal, got a program");
        return parse.Refusal!;
    }

    [Fact]
    public void A_crossover_program_parses_into_the_typed_program_it_means()
    {
        var p = Parsed(Crossover);

        Assert.Equal("BTCUSDT", p.Instrument);
        Assert.Equal(Crossover, p.Source);

        // Constants are declared, ordered by name, and inlined into the periods that used them.
        Assert.Equal(["fast", "slow"], p.Constants.Select(c => c.Name));
        Assert.All(p.Constants, c => Assert.Equal(ValueKind.Number, c.Type));

        Assert.Equal(
            [new IndicatorDecl("fastma", IndicatorKind.Sma, BarSeries.Close, 20),
             new IndicatorDecl("slowma", IndicatorKind.Sma, BarSeries.Close, 50)],
            p.Indicators);

        Assert.Equal(new Sizing(SizingKind.FixedQuantity, 1m), p.Sizing);
        Assert.Equal(new StopRule(StopKind.Percent, 1.5m, 0), p.Stop);
        Assert.Equal(TargetRule.None, p.Target);
        Assert.Null(p.MaxHoldBars);
        Assert.Equal("UTC", p.Time.TimeZone);
        Assert.Equal(Weekdays.All, p.Time.Days);
        Assert.Empty(p.Time.EntryWindows);
        Assert.Null(p.Time.OpeningRange);
        Assert.Null(p.Time.SessionExit);

        // Ordered rules, the exit first, and the crossing is one node over two indicator references.
        Assert.Equal([RuleKind.Exit, RuleKind.Entry], p.Rules.Select(r => r.Kind));
        Assert.Equal(
            new CrossExpr(CrossDirection.Below, new IndicatorRef("fastma", 0), new IndicatorRef("slowma", 0)),
            p.Rules[0].Condition);
        Assert.Equal(
            new CrossExpr(CrossDirection.Above, new IndicatorRef("fastma", 0), new IndicatorRef("slowma", 0)),
            p.Rules[1].Condition);
    }

    /// <summary>
    /// EVERY INDICATOR THE LANGUAGE HAS, AND A MISSPELLING OF ONE.
    ///
    /// The misspelling is the case that matters. A parser that accepted `smaa(close, 20)` as some
    /// generic node would hand back a program whose author believes it holds a 20-bar average and
    /// whose behaviour is whatever the fall-through did — and the program would be promoted, hashed
    /// and backtested under that belief.
    /// </summary>
    [Fact]
    public void Every_indicator_has_a_name_and_a_misspelt_name_is_refused_naming_the_line()
    {
        var p = Parsed("""
            instrument BTCUSDT
            indicator a = sma(close, 10)
            indicator b = ema(close, 10)
            indicator c = rsi(close, 14)
            indicator d = atr(14)
            indicator e = highest(high, 20)
            indicator f = lowest(low, 20)
            indicator g = opening_range_high()
            indicator h = opening_range_low()
            opening_range 09:30-10:00
            size fixed 1
            exit when close < a
            entry when close > a
            """);

        Assert.Equal(
            [IndicatorKind.Sma, IndicatorKind.Ema, IndicatorKind.Rsi, IndicatorKind.Atr,
             IndicatorKind.Highest, IndicatorKind.Lowest, IndicatorKind.OpeningRangeHigh, IndicatorKind.OpeningRangeLow],
            p.Indicators.Select(i => i.Kind));
        Assert.Equal(BarSeries.High, p.Indicators.Single(i => i.Name == "e").Source);
        Assert.Equal(new TimeWindow(new TimeOfDay(9, 30), new TimeOfDay(10, 0)), p.Time.OpeningRange);

        // Nothing else in this program is wrong: one letter is, and one letter is the whole test.
        var refusal = Refused("""
            instrument BTCUSDT
            indicator slow = smaa(close, 20)
            size fixed 1
            exit when close < slow
            entry when close > slow
            """);

        Assert.Equal(2, refusal.Line);
        Assert.Contains("smaa", refusal.Reason, StringComparison.Ordinal);
        Assert.Equal($"line 2: {refusal.Reason}", refusal.Text);
    }

    /// <summary>
    /// THE EXPRESSION GRAMMAR: arithmetic, comparison, Boolean, crossing, bounded history — and
    /// precedence, because `close > open + 1 and volume > 0` has exactly one reading and a program
    /// that was read the other way would trade on a condition nobody wrote.
    /// </summary>
    [Fact]
    public void Arithmetic_comparison_boolean_crossing_and_bounded_history_parse_into_the_nodes_they_mean()
    {
        var p = Parsed("""
            instrument BTCUSDT
            indicator mid = sma(close, 10)
            size fixed 1
            exit when not (close > mid) or volume < 100
            entry when close > open + (high - low) * 0.5 and close[2] >= mid[1]
            """);

        Assert.Equal(
            new BinaryExpr(BinaryOp.Or,
                new UnaryExpr(UnaryOp.Not,
                    new BinaryExpr(BinaryOp.Greater, new SeriesRef(BarSeries.Close, 0), new IndicatorRef("mid", 0))),
                new BinaryExpr(BinaryOp.Less, new SeriesRef(BarSeries.Volume, 0), new NumberLiteral(100m))),
            p.Rules[0].Condition);

        Assert.Equal(
            new BinaryExpr(BinaryOp.And,
                new BinaryExpr(BinaryOp.Greater,
                    new SeriesRef(BarSeries.Close, 0),
                    new BinaryExpr(BinaryOp.Add,
                        new SeriesRef(BarSeries.Open, 0),
                        new BinaryExpr(BinaryOp.Multiply,
                            new BinaryExpr(BinaryOp.Subtract, new SeriesRef(BarSeries.High, 0), new SeriesRef(BarSeries.Low, 0)),
                            new NumberLiteral(0.5m)))),
                new BinaryExpr(BinaryOp.GreaterOrEqual, new SeriesRef(BarSeries.Close, 2), new IndicatorRef("mid", 1))),
            p.Rules[1].Condition);
    }

    /// <summary>
    /// SIZING, STOPS, TARGETS, HOLDING TIME AND THE TIME FILTERS, all of them typed rather than
    /// text, because `U-runner-2` acts on these fields and a string it has to re-read is a second
    /// parser in a second place.
    /// </summary>
    [Fact]
    public void Sizing_protection_and_the_time_filters_parse_into_typed_fields()
    {
        var p = Parsed("""
            instrument BTCUSDT
            timezone America/New_York
            const atrperiod = 14
            indicator tr = atr(atrperiod)
            size risk_fraction 0.01
            stop atr 2 atrperiod
            target percent 3
            max_hold_bars 120
            weekdays mon,tue,wed,thu,fri
            entry_window 10:00-15:30
            session_exit 15:55
            exit when close < 1
            entry when close > 2
            """);

        Assert.Equal(new Sizing(SizingKind.EquityRiskFraction, 0.01m), p.Sizing);
        Assert.Equal(new StopRule(StopKind.EntryAtr, 2m, 14), p.Stop);
        Assert.Equal(new TargetRule(TargetKind.Percent, 3m), p.Target);
        Assert.Equal(120, p.MaxHoldBars);
        Assert.Equal("America/New_York", p.Time.TimeZone);
        Assert.Equal(Weekdays.Mon | Weekdays.Tue | Weekdays.Wed | Weekdays.Thu | Weekdays.Fri, p.Time.Days);
        Assert.Equal([new TimeWindow(new TimeOfDay(10, 0), new TimeOfDay(15, 30))], p.Time.EntryWindows);
        Assert.Equal(new TimeOfDay(15, 55), p.Time.SessionExit);
        Assert.Null(p.Time.OpeningRange);

        Assert.Equal(new Sizing(SizingKind.CapitalFraction, 0.25m), Parsed("""
            instrument BTCUSDT
            size capital_fraction 0.25
            exit when close < 1
            entry when close > 1
            """).Sizing);
    }

    /// <summary>
    /// COUNCIL'S "NEVER:" LINE, ONE CASE EACH.
    ///
    /// <para>Every one of these is refused because the AST HAS NO NODE for it, not because a name is
    /// on a blocklist: there is no statement, no declaration of a function, no loop, no import, no
    /// general call, no second instrument, no side and no leverage factor anywhere in
    /// `StrategyAst.cs`. A blocklist is a list somebody has to keep up to date as the vocabulary
    /// grows; an absent node kind stays absent.</para>
    ///
    /// <para>The refusal names a line in every case, because the writer of the program is a cheap
    /// model that has to fix it from the message alone.</para>
    /// </summary>
    [Theory]
    // shell, files, network
    [InlineData("run(\"cmd.exe /c dir\")")]
    [InlineData("read_file(\"c:/keys.txt\")")]
    [InlineData("entry when http_get(\"https://example.com/signal\") > 0")]
    // imports, functions, statements, loops
    [InlineData("import ta.trend")]
    [InlineData("def signal(x) = x + 1")]
    [InlineData("for i in 1 to 10: entry when close > i")]
    [InlineData("while close > 0: exit when true")]
    // clocks and randomness
    [InlineData("entry when now() > 100")]
    [InlineData("entry when random() > 0.5")]
    // model calls and training
    [InlineData("entry when ask_model(\"is this a breakout\")")]
    [InlineData("train logistic on close")]
    // a second instrument, read inside an expression
    [InlineData("entry when close(ETHUSDT) > 100")]
    // shorting and leverage
    [InlineData("short when close < 100")]
    [InlineData("leverage 3")]
    public void Nothing_on_the_never_line_can_be_spelled(string line)
    {
        var refusal = Refused($"""
            instrument BTCUSDT
            size fixed 1
            exit when close < 1
            entry when close > 1
            {line}
            """);

        Assert.Equal(5, refusal.Line);
        Assert.NotEqual("", refusal.Reason);
    }

    /// <summary>
    /// THE SAME LINE AGAIN, WHERE THE OFFENCE IS THE DECLARATION ITSELF rather than a word the
    /// language does not have: a second instrument on the instrument line, a size that is leverage
    /// because the fraction is above one, a side on an entry, an indicator defined over itself.
    /// </summary>
    [Theory]
    [InlineData("instrument BTCUSDT,ETHUSDT", "instrument")]     // multi-instrument
    [InlineData("size capital_fraction 2.5", "1")]               // leverage, spelled as a fraction
    [InlineData("size risk_fraction 1.5", "1")]                  // leverage, the other fraction
    [InlineData("entry short when close < 100", "short")]        // shorting
    [InlineData("indicator a = a", "sma")]                       // an indicator over itself
    public void The_declarations_that_exist_cannot_be_widened_into_one(string line, string fragment)
    {
        var refusal = Refused($"""
            {line}
            instrument BTCUSDT
            size fixed 1
            exit when close < 1
            entry when close > 1
            """);

        Assert.Equal(1, refusal.Line);
        Assert.Contains(fragment, refusal.Reason, StringComparison.Ordinal);
    }
}

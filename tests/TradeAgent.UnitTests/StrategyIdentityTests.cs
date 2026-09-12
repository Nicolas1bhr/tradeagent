using TradeAgent.Core;
using TradeAgent.Core.Strategy;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ONE MEANING, ONE ID — AND THE SOURCE KEPT BESIDE IT.
///
/// <para>`docs/COUNCIL.md` makes a strategy's identity "SHA-256 over the canonical typed program,
/// parameters and semantic-version manifest, source retained", and the referee's protocol is built on
/// it: lineage by hash, submissions charged against a campaign-wide trial budget, results attached to
/// a version. A hash over the SOURCE would put comments, spacing, letter case and declaration order
/// inside the identity — so one strategy, saved twice by a model that reformatted its own output,
/// would be two submissions with two budgets and two sets of results nobody may pool. That is the
/// defect this class exists to prevent, and the four spellings below are how it is checked.</para>
///
/// <para>The other direction matters as much: a program that MEANS something different must not share
/// an id. A period, an instrument, a rule's order, a sizing rule — each of them changes the answer.</para>
/// </summary>
public class StrategyIdentityTests
{
    const string Plain = """
        instrument BTCUSDT
        const fast = 20
        indicator fastma = sma(close, fast)
        size fixed 1
        stop percent 1.5
        exit when crosses_below(close, fastma)
        entry when close > fastma and volume > 100
        """;

    static StrategyProgram Parsed(string source)
    {
        var parse = StrategyParser.Parse(source);
        Assert.True(parse.Ok, $"expected a program, got: {parse.Why}");
        return parse.Program!;
    }

    /// <summary>
    /// THE CANONICAL FORM, WRITTEN OUT. Pinned as text rather than described, because it is an input
    /// to a hash that other builds will compare against: a change to this layout is a change to every
    /// id in every installation, and it should be impossible to make one without this line failing.
    /// </summary>
    [Fact]
    public void The_canonical_form_is_typed_ordered_and_free_of_the_source_text()
    {
        var p = Parsed(Plain);

        Assert.Equal("""
            program/1
            instrument BTCUSDT
            zone UTC
            days all
            ind fastma=sma(close,20)
            size fixed:1
            stop percent:1.5
            target none
            hold none
            warmup 21
            exit (crosses_below close @fastma)
            entry (and (> close @fastma) (> volume 100))

            """.ReplaceLineEndings("\n"), p.Canonical);

        Assert.Equal("fast=number:20", p.Parameters);
        Assert.Equal("language=1;indicators=1;calendar=1", p.Manifest);
        Assert.Equal($"language={StrategyVersions.LanguageVersion};indicators=" +
                     $"{StrategyVersions.IndicatorSemanticsVersion};calendar={StrategyVersions.CalendarVersion}",
                     p.Manifest);

        // The formula, recomputed here rather than trusted: COUNCIL's three parts, in order.
        Assert.Equal(Sha256Hex.Of($"{p.Canonical}\n{p.Parameters}\n{p.Manifest}"), p.StrategyId);
        Assert.Equal(64, p.StrategyId.Length);
    }

    /// <summary>
    /// FOUR SPELLINGS OF ONE PROGRAM, ONE ID. Whitespace, comments, the order two constants were
    /// declared in, letter case, Windows line endings and a trailing zero on a percentage — none of
    /// them is part of what the strategy does.
    /// </summary>
    [Fact]
    public void Every_spelling_of_one_program_reaches_one_id()
    {
        var plain = Parsed(Plain);

        var spellings = new[]
        {
            // spacing, blank lines and trailing whitespace
            "instrument   BTCUSDT   \n\n   const fast   =   20\n\nindicator fastma = sma( close , fast )\n"
            + "size    fixed   1\nstop   percent   1.5\n\nexit when crosses_below( close , fastma )\n"
            + "   entry when close > fastma and volume > 100   \n\n",

            // comments, including one on a declaration's own line
            "# a 20-bar average, bought above and sold below\ninstrument BTCUSDT   # the one instrument\n"
            + "const fast = 20\nindicator fastma = sma(close, fast)   # the average\nsize fixed 1\n"
            + "stop percent 1.5\nexit when crosses_below(close, fastma)\nentry when close > fastma and volume > 100\n",

            // the declarations in a different order, and the stop written 1.50
            "size fixed 1\nstop percent 1.50\nexit when crosses_below(close, fastma)\n"
            + "entry when close > fastma and volume > 100\nindicator fastma = sma(close, fast)\nconst fast = 20\n"
            + "instrument BTCUSDT\n",

            // upper case, CRLF, and a byte-order mark, which is what a Windows editor leaves behind
            "\uFEFFINSTRUMENT BTCUSDT\r\nCONST FAST = 20\r\nINDICATOR FASTMA = SMA(CLOSE, FAST)\r\n"
            + "SIZE FIXED 1\r\nSTOP PERCENT 1.5\r\nEXIT WHEN CROSSES_BELOW(CLOSE, FASTMA)\r\n"
            + "ENTRY WHEN CLOSE > FASTMA AND VOLUME > 100\r\n",
        };

        foreach (var spelling in spellings)
        {
            var p = Parsed(spelling);
            Assert.Equal(plain.Canonical, p.Canonical);
            Assert.Equal(plain.Parameters, p.Parameters);
            Assert.Equal(plain.StrategyId, p.StrategyId);

            // And the source is kept exactly as it arrived, comments and all, beside the canonical form.
            Assert.Equal(spelling, p.Source);
        }
    }

    /// <summary>
    /// A CONSTANT'S NAME IS A PARAMETER, ITS POSITION IS NOT. Two programs that differ only in the
    /// ORDER of their constants are one program; two that differ in a constant's VALUE are two, even
    /// though their structure is identical — which is exactly what a parameter sweep is.
    /// </summary>
    [Fact]
    public void A_parameter_is_part_of_the_identity_and_its_position_is_not()
    {
        var twenty = Parsed(Plain);
        var thirty = Parsed(Plain.Replace("const fast = 20", "const fast = 30"));

        Assert.NotEqual(twenty.StrategyId, thirty.StrategyId);
        Assert.NotEqual(twenty.Parameters, thirty.Parameters);
        Assert.NotEqual(twenty.Canonical, thirty.Canonical);       // the period is resolved into the form

        var reordered = Parsed("""
            instrument BTCUSDT
            const slow = 50
            const fast = 20
            indicator fastma = sma(close, fast)
            indicator slowma = sma(close, slow)
            size fixed 1
            exit when crosses_below(fastma, slowma)
            entry when crosses_above(fastma, slowma)
            """);
        var declaredTheOtherWayRound = Parsed("""
            instrument BTCUSDT
            const fast = 20
            const slow = 50
            indicator slowma = sma(close, slow)
            indicator fastma = sma(close, fast)
            size fixed 1
            exit when crosses_below(fastma, slowma)
            entry when crosses_above(fastma, slowma)
            """);

        Assert.Equal(reordered.StrategyId, declaredTheOtherWayRound.StrategyId);
        Assert.Equal("fast=number:20\nslow=number:50", reordered.Parameters);
    }

    /// <summary>
    /// WHAT MUST CHANGE THE ID. Each of these is a different strategy, and a shared id would pool
    /// their results, their trial budget and their lineage.
    /// </summary>
    [Fact]
    public void A_program_that_means_something_else_gets_another_id()
    {
        var baseline = Parsed(Plain).StrategyId;
        var seen = new HashSet<string> { baseline };

        foreach (var (what, changed) in new[]
        {
            ("the instrument", Plain.Replace("BTCUSDT", "ETHUSDT")),
            ("the period", Plain.Replace("const fast = 20", "const fast = 21")),
            ("the indicator", Plain.Replace("sma(close, fast)", "ema(close, fast)")),
            ("the series", Plain.Replace("sma(close, fast)", "sma(high, fast)")),
            ("the sizing", Plain.Replace("size fixed 1", "size capital_fraction 0.5")),
            ("the stop", Plain.Replace("stop percent 1.5", "stop percent 1.6")),
            ("a target", Plain.Replace("stop percent 1.5", "stop percent 1.5\ntarget percent 3")),
            ("a holding limit", Plain.Replace("stop percent 1.5", "stop percent 1.5\nmax_hold_bars 60")),
            ("the zone", Plain.Replace("instrument BTCUSDT", "instrument BTCUSDT\ntimezone Europe/Berlin")),
            ("the weekdays", Plain.Replace("instrument BTCUSDT", "instrument BTCUSDT\nweekdays mon,tue,wed,thu,fri")),
            ("a comparison", Plain.Replace("close > fastma and", "close >= fastma and")),
            ("a history depth", Plain.Replace("volume > 100", "volume[1] > 100")),
            ("a crossing's direction", Plain.Replace("crosses_below(close, fastma)", "crosses_above(close, fastma)")),
            ("the constant's name", Plain.Replace("fast", "quick")),
        })
        {
            var id = Parsed(changed).StrategyId;
            Assert.True(seen.Add(id), $"{what} did not change the id");
        }
    }

    /// <summary>
    /// RULE ORDER IS MEANING. Two exits that test different things fire in the order they are
    /// written, so a canonical form that sorted them would make two strategies one.
    /// </summary>
    [Fact]
    public void The_order_of_the_rules_is_part_of_the_identity()
    {
        var oneWay = Parsed("""
            instrument BTCUSDT
            size fixed 1
            exit when close < 100
            exit when volume < 10
            entry when close > 200
            """);
        var theOther = Parsed("""
            instrument BTCUSDT
            size fixed 1
            exit when volume < 10
            exit when close < 100
            entry when close > 200
            """);

        Assert.NotEqual(oneWay.StrategyId, theOther.StrategyId);
        Assert.NotEqual(oneWay.Canonical, theOther.Canonical);
    }
}

using TradeAgent.Core;
using TradeAgent.Core.Strategy;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE TWO GUARDS `bars` HAD TO KEEP (<c>U-timeframe-a</c> item 1): a program that declares no <c>bars</c>
/// keeps its id and its golden vector, and a stored program that uses <c>bars</c> as a NAME keeps parsing
/// to the text it always had.
///
/// <para>Written against what the base already had — the parser, the canonical text, the ids and the
/// golden vectors' own check — and nothing this unit added, so they compile and run on the base too:
/// (e) is RED there, at the <c>bars 1m</c> parse; (f) is green there, as a guard is.</para>
/// </summary>
public class DeclaredBarsGuardTests(ITestOutputHelper log)
{
    const string Body = """
        instrument BTCUSDT
        size fixed 1
        stop percent 2
        indicator m = sma(close, 20)
        exit when close < m
        entry when close > m
        """;

    static StrategyProgram Parsed(string text)
    {
        var parse = StrategyParser.Parse(text);
        Assert.True(parse.Ok, parse.Why);
        return parse.Program!;
    }

    /// <summary>The ids `DayOneStrategyTests` pins for the three shipped programs, restated as the contract.</summary>
    static readonly Dictionary<string, string> ShippedIds = new(StringComparer.Ordinal)
    {
        ["ma-crossover.strategy"] = "3b3364734ea97e715479476ffd9992ade4074bfd52bc1a9220ef4b7605ffbd42",
        ["opening-range-breakout.strategy"] = "70ec1a6e45dc45096995564fc11d76f24f13c5ae156beab05aa9d1639ee7d26d",
        ["rsi-mean-reversion.strategy"] = "16d6192f798908637d3ae246056f4e2d65e0231531604202204783e62760b624"
    };

    /// <summary>
    /// (e) GUARD — A PROGRAM WITHOUT `bars` KEEPS ITS ID AND ITS GOLDEN VECTOR.
    ///
    /// <para>The three shipped programs parse to the ids pinned for them before this unit, their canonical
    /// texts carry no <c>bars</c> line, and each with <c>bars 1m</c> added is the same text and the same id.
    /// Then the golden vectors' own check runs — fifteen programs backtested through this build's
    /// <c>Backtest.Run</c> and compared, trace sha and metrics sha, with the pins
    /// <c>EvaluationGoldenVectorTests</c> holds beside <c>Referee.EvaluatorVersion</c> — so this guard cannot
    /// hold while what a v1 program does over those bars has moved. Calling that check rather than copying its
    /// pins keeps one set of pins: a re-pin there is a re-pin here.</para>
    ///
    /// <para><b>RED on the base</b> at the <c>bars 1m</c> parse. A canonical form that wrote <c>bars 60s</c>
    /// for every program goes red on the first id.</para>
    /// </summary>
    [Fact]
    public void A_program_without_bars_keeps_its_id_and_its_golden_vector()
    {
        foreach (var name in DayOnePrograms.Names)
        {
            var text = DayOnePrograms.Text(name);
            var program = Parsed(text);
            var declared = Parsed(text.TrimEnd('\n') + "\nbars 1m\n");

            log.WriteLine($"{name,-34} {program.StrategyId}");
            Assert.Equal(ShippedIds[name], program.StrategyId);
            Assert.DoesNotContain("\nbars ", program.Canonical, StringComparison.Ordinal);
            Assert.Equal(program.Canonical, declared.Canonical);
            Assert.Equal(program.StrategyId, declared.StrategyId);
            Assert.Equal(
                Sha256Hex.Of($"{program.Canonical}\n{program.Parameters}\n{StrategyVersions.Manifest}"),
                program.StrategyId);
        }

        Assert.Equal(ShippedIds.Keys.Order(StringComparer.Ordinal), DayOnePrograms.Names.Order(StringComparer.Ordinal));

        new EvaluationGoldenVectorTests(log).Changed_evaluation_output_without_a_version_bump_fails_the_golden_vectors();
    }

    /// <summary>
    /// (f) GUARD — A STORED PROGRAM WITH A CONSTANT NAMED `bars` STILL PARSES, TO THE TEXT IT ALWAYS HAD.
    ///
    /// <para>The language had no <c>bars</c> before this unit, so a stored program may use the word as a
    /// name; reserving it would turn a recorded version into a refusal and orphan every result recorded
    /// against it. The canonical text is pinned whole, so a change that kept the parse but moved the id is
    /// caught too.</para>
    ///
    /// <para><b>Green on the base</b>, as a guard is. <b>Mutant (iii)</b> — <c>bars</c> added to the reserved
    /// names — goes red here: "`bars` already means something in this language".</para>
    /// </summary>
    [Fact]
    public void A_stored_program_with_a_const_named_bars_still_parses()
    {
        var stored = StrategyParser.Parse("""
            instrument BTCUSDT
            const bars = 20
            indicator avg = sma(close, bars)
            size fixed 1
            stop percent 2
            exit when close < avg
            entry when close > avg and close[1] < bars * 10
            """);

        Assert.True(stored.Ok, stored.Why);
        var p = stored.Program!;
        Assert.Equal("""
            program/1
            instrument BTCUSDT
            zone UTC
            timeframe none
            freshness none
            decisionage none
            days all
            ind avg=sma(close,20)
            size fixed:1
            stop percent:2
            target none
            hold none
            warmup 20
            exit (< close @avg)
            entry (and (> close @avg) (< close[1] (* 20 10)))

            """.ReplaceLineEndings("\n"), p.Canonical);
        Assert.Equal("bars=number:20", p.Parameters);

        var indicator = StrategyParser.Parse(Body.Replace("indicator m =", "indicator bars =").Replace("> m", "> bars")
            .Replace("< m", "< bars"));
        Assert.True(indicator.Ok, indicator.Why);
    }
}

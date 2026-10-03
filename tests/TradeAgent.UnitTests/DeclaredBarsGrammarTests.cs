using TradeAgent.Core;
using TradeAgent.Core.Strategy;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// `bars 1h` — THE BAR A PROGRAM DECLARES IT IS EVALUATED ON, AS GRAMMAR AND AS IDENTITY
/// (<c>U-timeframe-a</c> item 1).
///
/// <para><b>Additive, and provably so.</b> A program that declares no <c>bars</c> is evaluated on every
/// closed minute, as every program before this unit was, and its canonical text, its id and its golden
/// vector do not move: the canonical form writes the <c>bars</c> line only when it is declared and is not
/// one minute, and neither <c>Header</c> nor the manifest changes. <c>bars</c> is a declaration only as the
/// first word of a line and is NOT a reserved name, so a stored program that says <c>const bars = 20</c>
/// keeps parsing to the program it always was.</para>
///
/// <para><b>RED on the base</b> (<c>298bb36</c>) for every test here but the guard (f): <c>bars</c> was
/// not a declaration, so each <c>bars</c> line was refused with "is not a declaration this language
/// has".</para>
/// </summary>
public class DeclaredBarsGrammarTests(ITestOutputHelper log)
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

    [Theory]
    [InlineData("1m", 1)]
    [InlineData("5m", 5)]
    [InlineData("15m", 15)]
    [InlineData("30m", 30)]
    [InlineData("1h", 60)]
    [InlineData("4h", 240)]
    [InlineData("1d", 1440)]
    public void Each_of_the_seven_bars_parses_as_a_declaration(string bars, int minutes)
    {
        var program = Parsed($"{Body}\nbars {bars}");

        Assert.Equal(TimeSpan.FromMinutes(minutes), program.Bars);
        Assert.Equal(bars, StrategyBars.Spelled(program.Bars));
    }

    /// <summary>
    /// A BAR OFF THE LIST IS REFUSED, ON ITS LINE, WITH THE SEVEN NAMED — and so is a malformed one. A
    /// list is a refusal a model can read and correct; a grid only one program uses is a bar nobody else's
    /// results can be compared with.
    /// </summary>
    [Theory]
    [InlineData("2h")]
    [InlineData("3m")]
    [InlineData("2d")]
    [InlineData("1w")]
    [InlineData("0m")]
    [InlineData("90s")]
    [InlineData("1.5h")]
    [InlineData("-1h")]
    [InlineData("h")]
    [InlineData("1h 4h")]
    [InlineData("99999999999999999999h")]
    [InlineData("")]
    public void A_bar_off_the_list_is_refused_on_its_line_with_the_seven_named(string bars)
    {
        var parse = StrategyParser.Parse($"{Body}\nbars {bars}");

        log.WriteLine(parse.Why);
        Assert.False(parse.Ok);
        Assert.Equal(7, parse.Refusal!.Line);
        Assert.Contains(StrategyBars.List, parse.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void Bars_declared_twice_is_refused_rather_than_one_line_silently_winning()
    {
        var parse = StrategyParser.Parse($"{Body}\nbars 1h\nbars 4h");

        Assert.False(parse.Ok);
        Assert.Equal(8, parse.Refusal!.Line);
        Assert.Contains("`bars` is declared twice", parse.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// ONE MEANING, ONE TEXT: `bars 1m` IS THE PROGRAM THAT DECLARES NOTHING, and `bars 60m` is `bars 1h`.
    /// </summary>
    [Fact]
    public void Bars_1m_is_the_program_that_declares_none_and_bars_60m_is_bars_1h()
    {
        var none = Parsed(Body);
        var minute = Parsed($"{Body}\nbars 1m");
        var seconds = Parsed($"{Body}\nbars 60s");
        var hour = Parsed($"{Body}\nbars 1h");
        var sixty = Parsed($"bars 60m\n{Body}");

        Assert.Equal(StrategyBars.OneMinute, none.Bars);
        Assert.Equal(none.Canonical, minute.Canonical);
        Assert.Equal(none.StrategyId, minute.StrategyId);
        Assert.Equal(none.StrategyId, seconds.StrategyId);
        Assert.Equal(hour.Canonical, sixty.Canonical);
        Assert.Equal(hour.StrategyId, sixty.StrategyId);
    }

    /// <summary>
    /// A PROGRAM ON HOURLY BARS IS ANOTHER PROGRAM: its canonical form carries the bar, under the same
    /// header, and its id is not its minute twin's.
    /// </summary>
    [Fact]
    public void A_program_on_hourly_bars_is_another_id_and_its_canonical_form_says_which_bar()
    {
        var minute = Parsed(Body);
        var hour = Parsed($"{Body}\nbars 1h");
        var day = Parsed($"{Body}\nbars 1d");

        log.WriteLine(hour.Canonical);
        Assert.NotEqual(minute.StrategyId, hour.StrategyId);
        Assert.NotEqual(hour.StrategyId, day.StrategyId);
        Assert.StartsWith($"{StrategyCanonical.Header}\ninstrument BTCUSDT\nzone UTC\nbars 3600s\ntimeframe none\n",
            hour.Canonical, StringComparison.Ordinal);
        Assert.Contains("\nbars 86400s\n", day.Canonical, StringComparison.Ordinal);
        Assert.DoesNotContain("\nbars ", minute.Canonical, StringComparison.Ordinal);
        Assert.Equal(StrategyVersions.Manifest, hour.Manifest);
    }

    /// <summary>
    /// `bars` IS A DECLARATION ONLY AS A LINE'S FIRST WORD, and the refusal for an unknown first word names
    /// it among the declarations. In a condition it is a name like any other — undeclared, it is refused as
    /// one.
    /// </summary>
    [Fact]
    public void Bars_is_a_declaration_only_as_the_first_word_of_a_line()
    {
        var unknown = StrategyParser.Parse($"{Body}\nbar 1h");
        var inCondition = StrategyParser.Parse(Body.Replace("entry when close > m", "entry when close > bars"));

        Assert.False(unknown.Ok);
        Assert.Contains("The declarations are: instrument, timezone, bars, timeframe", unknown.Why, StringComparison.Ordinal);
        Assert.False(inCondition.Ok);
        Assert.Contains("`bars` is not a bar series, a declared constant or a declared indicator", inCondition.Why,
            StringComparison.Ordinal);
    }

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
            Assert.Equal(StrategyBars.OneMinute, program.Bars);
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
    /// caught too. And a program may say both — <c>bars 1h</c> and <c>const bars = 3</c> — because a
    /// declaration is a line's first word and a name never is.</para>
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
        Assert.Equal(StrategyBars.OneMinute, p.Bars);
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

        var both = StrategyParser.Parse($"bars 1h\nconst bars = 3\n{Body.Replace("entry when close > m", "entry when close > m + bars")}");
        Assert.True(both.Ok, both.Why);
        Assert.Equal(TimeSpan.FromHours(1), both.Program!.Bars);
        Assert.Equal("bars=number:3", both.Program.Parameters);
    }
}

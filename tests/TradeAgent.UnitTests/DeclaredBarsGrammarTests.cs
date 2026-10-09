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
/// <para><b>RED on the base</b> by not compiling — <c>StrategyBars</c> and <c>StrategyProgram.Bars</c> did
/// not exist — and, read as text, because <c>bars</c> was not a declaration: each <c>bars</c> line was
/// refused with "is not a declaration this language has". The two guards, (e) and (f), are in
/// <c>DeclaredBarsGuardTests</c>, written against the base's own API so that they run there too.</para>
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
    /// THE ROLE THAT WRITES PROGRAMS IS TOLD `bars` EXISTS AND WHY: its instructions say what `bars 1h`
    /// does and that minute-scale turnover dies on costs, and the language reference it is handed —
    /// <c>docs/STRATEGY-LANGUAGE.md</c>, copied into its home — has the declaration in its grammar and says
    /// how it relates to <c>timeframe</c>. A declaration the writer is never shown is one it never uses.
    /// </summary>
    [Fact]
    public void The_role_that_writes_programs_is_told_bars_exists_and_why_minute_turnover_dies_on_fees()
    {
        // The guide (U-canon), which carries the research procedures word for word.
        var mission = TradeAgent.AgentRuntime.Canon.Guide(new TradeAgent.AgentRuntime.WorkspaceContext(
            "Practice simulator", ConnectorIsPaper: true, "SIM-1", TradingMode.PAPER,
            ExecutionAvailable: true, null, new RiskPolicy { InstrumentAllowlist = ["ES"] },
            ConnectorIsBuiltInSimulator: false, Role: CouncilRoles.Research), TradeAgent.AgentRuntime.RuntimeClass.Cli);

        Assert.Contains("`bars 1h` makes a program decide once an hour instead of every minute", mission,
            StringComparison.Ordinal);
        Assert.Contains("minute-scale turnover dies on costs", mission, StringComparison.Ordinal);

        var reference = File.ReadAllText(Path.Combine(DayOnePrograms.RepoRoot(), "docs", "STRATEGY-LANGUAGE.md"))
            .ReplaceLineEndings("\n");
        Assert.Contains("| \"bars\" BARS", reference, StringComparison.Ordinal);
        Assert.Contains($"BARS        := \"1m\" | \"5m\" | \"15m\" | \"30m\" | \"1h\" | \"4h\" | \"1d\"", reference,
            StringComparison.Ordinal);
        Assert.Contains("**`bars` and `timeframe` are independent.**", reference, StringComparison.Ordinal);
    }

    /// <summary>
    /// A PROGRAM MAY SAY BOTH — `bars 1h` AND `const bars = 3` — because a declaration is a line's first word
    /// and a name never is.
    /// </summary>
    [Fact]
    public void A_program_may_declare_bars_and_name_a_constant_bars()
    {
        var both = StrategyParser.Parse($"bars 1h\nconst bars = 3\n{Body.Replace("entry when close > m", "entry when close > m + bars")}");

        Assert.True(both.Ok, both.Why);
        Assert.Equal(TimeSpan.FromHours(1), both.Program!.Bars);
        Assert.Equal("bars=number:3", both.Program.Parameters);
        Assert.Contains("entry (> close (+ @m 3))", both.Program.Canonical, StringComparison.Ordinal);
    }
}

namespace TradeAgent.Tests;

/// <summary>
/// THE ONE PLACE THE THREE DAY-ONE PROGRAMS LIVE, named once so that no test can read a second copy.
///
/// <para>They used to be fixtures beside <c>DayOneStrategyTests</c>, which made them test material: a
/// model that had to write a program never saw one. They are now content of
/// <c>TradeAgent.AgentRuntime</c>, shipped into every role's home on every start, and the tests read
/// the same bytes the product ships rather than a copy kept beside them. A second copy anywhere is
/// what <c>The_programs_the_parser_tests_read_are_the_shipped_files</c> refuses.</para>
/// </summary>
internal static class DayOnePrograms
{
    /// <summary>
    /// The list the PRODUCT ships, not a copy of it: a program added to
    /// <see cref="TradeAgent.AgentRuntime.ResearchLibrary.Programs"/> and not to the repository is
    /// caught by <c>The_programs_the_parser_tests_read_are_the_shipped_files</c> rather than ignored.
    /// </summary>
    public static string[] Names => TradeAgent.AgentRuntime.ResearchLibrary.Programs;

    /// <summary>The checkout this assembly was built from, found by the solution file above it.</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "TradeAgent.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException(
            $"no TradeAgent.sln above {AppContext.BaseDirectory}");
    }

    /// <summary>The directory the app ships them FROM — the single copy in the repository.</summary>
    public static string Dir =>
        System.IO.Path.Combine(RepoRoot(), "src", "TradeAgent.AgentRuntime", "Strategies");

    public static string At(string name) => System.IO.Path.Combine(Dir, name);

    /// <summary>
    /// READ WITH LF, WHATEVER THE CHECKOUT WROTE. <c>.gitattributes</c> pins <c>*.strategy</c> to LF, so
    /// this is belt as well as braces — but the assertions that use these bytes compare them to a
    /// <c>.md</c> file and split them on a bare '\n'. A file carrying '\r' fails both while spelling
    /// exactly the same program, which makes the failure a report about the checkout rather than about
    /// the language. CI run 34719212649 is what that looks like: windows-latest only (its
    /// <c>core.autocrlf=true</c>), three cases of one theory, <c>Sub-string not found</c>.
    /// </summary>
    public static string Text(string name) => File.ReadAllText(At(name)).ReplaceLineEndings("\n");

    /// <summary>The shipped breakout's file name, which <see cref="V1Text"/> answers the fixture for.</summary>
    public const string Breakout = "opening-range-breakout.strategy";

    /// <summary>
    /// THE SHIPPED BREAKOUT'S TEXT BEFORE <c>U-size-cap</c>, BYTE FOR BYTE — a FIXTURE now, under its old id
    /// <c>70ec1a6e…</c>. The shipped file gained <c>max_capital_fraction 0.95</c> on its size line, a declared re-pin of its
    /// id; this is the v1 text that <c>DeclaredBarsGuardTests</c> and <c>FeatureProgramGrammarTests</c> go on proving keeps
    /// its id, and <c>SizeCapTests</c> runs it beside the shipped file. Its SHA-256 is the old file's,
    /// <c>ca509cf4…</c>, asserted there. A string rather than a <c>.strategy</c> file, so there is still one copy of each
    /// shipped program in the repository (<c>The_programs_the_parser_tests_read_are_the_shipped_files</c>).
    /// </summary>
    public const string BreakoutV1 = """
        # An opening-range breakout with ATR risk sizing and a time stop.
        instrument BTCUSDT
        timezone America/New_York
        timeframe 1m
        data_freshness 2m
        max_decision_age 30s
        const atrperiod = 14
        const atrmultiple = 2
        const riskfraction = 0.01
        indicator rangehigh = opening_range_high()
        indicator rangelow = opening_range_low()
        indicator truerange = atr(atrperiod)
        size risk_fraction riskfraction
        stop atr atrmultiple atrperiod
        max_hold_bars 120
        weekdays mon,tue,wed,thu,fri
        opening_range 09:30-10:00
        entry_window 10:00-15:30
        session_exit 15:55
        exit when low < rangelow
        entry when close > rangehigh

        """;

    /// <summary>
    /// THE V1 TEXT OF A DAY-ONE PROGRAM — the text whose id was pinned before any v2 declaration existed: the shipped file,
    /// but for the breakout, whose v1 text is <see cref="BreakoutV1"/>.
    /// </summary>
    public static string V1Text(string name) =>
        string.Equals(name, Breakout, StringComparison.Ordinal) ? BreakoutV1 : Text(name);
}

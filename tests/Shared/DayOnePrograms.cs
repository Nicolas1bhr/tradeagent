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
}

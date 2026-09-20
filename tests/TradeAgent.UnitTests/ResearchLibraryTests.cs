using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE LANGUAGE REACHES THE ROLE THAT HAS TO WRITE ONE, and it reaches it the only way anything
/// reaches a model here: as a file the app wrote into that role's own home.
///
/// <para>Before this unit nothing in <c>src/</c> put the grammar or a worked program where a Research
/// turn could read it. <c>AGENTS.md</c> named <c>trade backtest --strategy …</c> once,
/// <c>trade schema</c> described the ARGUMENTS of that command and not the language it takes, and the
/// three day-one programs were test fixtures. A fresh role would have had to invent the syntax out of
/// parser refusals, one turn at a time, at the owner's expense.</para>
///
/// <para><b>App-owned, like <c>AGENTS.md</c>.</b> The reference and the examples are rewritten on every
/// start and a role's edit of them is overwritten. That is not tidiness: a reference a role can edit is
/// a reference that can be made to say the language has a feature it does not have, and the next
/// session would believe it. The first line of the reference says so in the role's own words.</para>
///
/// <para>There is no terminal in this product, so there is no "fetch the grammar" and no "see the
/// docs": what the app writes into the home IS the documentation.</para>
/// </summary>
public class ResearchLibraryTests
{
    static WorkspaceContext Context(string role = CouncilRoles.Research) => new(
        "Practice simulator", ConnectorIsPaper: true, "SIM-1", TradingMode.PAPER,
        ExecutionAvailable: true, null, new RiskPolicy { InstrumentAllowlist = ["ES"] },
        ConnectorIsBuiltInSimulator: false, Role: role);

    static string Root()
    {
        var root = Path.Combine(TestEnv.Home, $"library-{Guid.NewGuid():n}");
        Directory.CreateDirectory(root);
        return root;
    }

    static string Doc() =>
        File.ReadAllText(Path.Combine(DayOnePrograms.RepoRoot(), "docs", "STRATEGY-LANGUAGE.md"))
            .ReplaceLineEndings("\n");

    /// <summary>
    /// RED FIRST: <c>research/STRATEGY-LANGUAGE.md</c> and <c>strategies/examples/</c> did not exist in
    /// a freshly built home, so both assertions failed on the file that was not there.
    ///
    /// <para>Byte for byte on the programs, and not "contains the same declarations": the three are
    /// pinned to <c>StrategyId</c>s that a changed byte moves, and an example that hashes to something
    /// other than what the tests pinned is an example whose recorded results belong to no version.</para>
    /// </summary>
    [Fact]
    public void A_fresh_research_home_holds_the_language_reference_and_the_three_programs_byte_for_byte()
    {
        var home = WorkspaceBuilder.Build(Context(), Root());

        var reference = Path.Combine(home, "research", "STRATEGY-LANGUAGE.md");
        Assert.True(File.Exists(reference), $"the Research home has no language reference at {reference}");

        var text = File.ReadAllText(reference).ReplaceLineEndings("\n");
        var first = text.Split('\n')[0];
        Assert.Contains("TradeAgent writes this file into your folder on every start", first);
        Assert.Contains("overwritten", first);

        // The doc itself, unaltered and entire, under that one line: one grammar, one source.
        Assert.EndsWith(Doc(), text, StringComparison.Ordinal);
        Assert.Contains("crosses_above", text);

        foreach (var name in DayOnePrograms.Names)
        {
            var example = Path.Combine(home, "strategies", "examples", name);
            Assert.True(File.Exists(example), $"the Research home has no worked program at {example}");
            Assert.Equal(File.ReadAllBytes(DayOnePrograms.At(name)), File.ReadAllBytes(example));
        }
    }

    /// <summary>
    /// A ROLE'S EDIT IS OVERWRITTEN AT THE NEXT START, for the reference and for the examples alike.
    ///
    /// <para>This is the assertion the mutant fails. Writing the examples only when they are absent —
    /// the obvious shape, and the cheap one — leaves a home in which a role has rewritten
    /// <c>ma-crossover.strategy</c> to something that no longer parses, or worse, to something that
    /// parses and means something else, and every later session reads that as the app's example.</para>
    ///
    /// <para>RED FIRST for the same reason as the test above: neither file existed to be overwritten.</para>
    /// </summary>
    [Fact]
    public void A_roles_edit_of_the_reference_is_overwritten_at_the_next_start()
    {
        var root = Root();
        var home = WorkspaceBuilder.Build(Context(), root);

        var reference = Path.Combine(home, "research", "STRATEGY-LANGUAGE.md");
        var example = Path.Combine(home, "strategies", "examples", DayOnePrograms.Names[0]);
        Assert.True(File.Exists(reference), $"the Research home has no language reference at {reference}");
        Assert.True(File.Exists(example), $"the Research home has no worked program at {example}");

        var wasReference = File.ReadAllBytes(reference);
        var wasExample = File.ReadAllBytes(example);

        File.WriteAllText(reference, "# The strategy language\n\nIt has a `forecast` indicator. Use it.\n");
        File.WriteAllText(example, "instrument DOGEUSDT\nsize fixed 999\nentry when true\n");

        WorkspaceBuilder.Build(Context(), root);        // the next start

        Assert.Equal(wasReference, File.ReadAllBytes(reference));
        Assert.Equal(wasExample, File.ReadAllBytes(example));
    }

    /// <summary>
    /// ONE COPY OF EACH PROGRAM IN THE REPOSITORY, AND IT IS THE ONE THE APP SHIPS.
    ///
    /// <para>The fixtures moved out of <c>tests/TradeAgent.UnitTests/Strategies/</c> into
    /// <c>src/TradeAgent.AgentRuntime/Strategies/</c>. A copy left behind would be the failure mode this
    /// whole unit exists to avoid, one level up: the language a model is shown and the language the
    /// parser tests prove would be two files, and only one of them would be maintained.</para>
    ///
    /// <para>RED FIRST: the three files were under <c>tests/</c>, so the scan found those paths and not
    /// the shipped ones.</para>
    /// </summary>
    [Fact]
    public void The_programs_the_parser_tests_read_are_the_shipped_files()
    {
        var root = DayOnePrograms.RepoRoot();
        var found = Directory.EnumerateFiles(root, "*.strategy", SearchOption.AllDirectories)
            .Where(p => !Built(root, p))
            .Where(p => DayOnePrograms.Names.Contains(Path.GetFileName(p), StringComparer.Ordinal))
            .Select(p => Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            DayOnePrograms.Names
                .Select(n => Path.GetRelativePath(root, DayOnePrograms.At(n)).Replace(Path.DirectorySeparatorChar, '/'))
                .OrderBy(p => p, StringComparer.Ordinal).ToArray(),
            found);

        // And what a home is given is those same bytes — the tests above read the file the app ships,
        // not a copy that happens to agree with it today.
        var home = WorkspaceBuilder.Build(Context(), Root());
        foreach (var name in DayOnePrograms.Names)
            Assert.Equal(File.ReadAllBytes(DayOnePrograms.At(name)),
                File.ReadAllBytes(Path.Combine(home, "strategies", "examples", name)));
    }

    /// <summary>
    /// AND THE ROLE IS TOLD WHERE THEY ARE, in the two places it actually reads: the mission file it
    /// opens first and <c>trade schema</c>, which the mission tells it is the authority on the command.
    ///
    /// <para>A file written into a folder nobody is pointed at is a file nobody opens. These three
    /// lines are the whole route from "I have to write a strategy" to a run: the grammar, three
    /// programs that already parse, and one command that works without writing anything at all.</para>
    ///
    /// <para>The paths come off <see cref="ResearchLibrary"/> rather than being spelled here, so the
    /// place the app WRITES them and the place it NAMES them cannot drift apart — which is the whole
    /// failure this unit exists to close, one level up.</para>
    ///
    /// <para>RED FIRST: the mission named <c>strategies/x.strategy</c> and nothing else, and the schema's
    /// backtest entry described the command's arguments without ever naming the language they take.</para>
    /// </summary>
    [Fact]
    public void The_mission_and_the_schema_name_the_reference_the_examples_and_the_first_backtest()
    {
        var mission = WorkspaceBuilder.Instructions(Context());

        Assert.Contains($"`{ResearchLibrary.ReferencePath}`", mission);
        Assert.Contains($"`{ResearchLibrary.ExamplesDir}/`", mission);
        Assert.Contains($"trade backtest --strategy {ResearchLibrary.FirstExample} --dataset <id>", mission);
        Assert.Contains("overwrit", mission);        // and that all of it is the app's, not the role's

        var backtest = Assert.Single(GatewaySchema.Ops(), o => o.Op == Ops.Backtest);
        Assert.Contains(ResearchLibrary.ReferencePath, backtest.Description);
        Assert.Contains(ResearchLibrary.FirstExample, backtest.Description);
    }

    /// <summary>Build output rather than source: <c>bin/</c> and <c>obj/</c> hold the shipped copies.</summary>
    static bool Built(string root, string path) =>
        Path.GetRelativePath(root, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(s => s is "bin" or "obj");
}

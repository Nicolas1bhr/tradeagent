using System.Text.RegularExpressions;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE PRE-CHECK'S LIST NAMES REAL, QUICK, STRUCTURAL CLASSES (<c>U-ci-precheck</c> item 1). <c>tools/precheck.sh</c> runs exactly the classes
/// <c>tools/structural-tests.txt</c> lists, before a CI dispatch. A misspelt or moved class would run nothing there, and a filter that matches
/// nothing passes in <c>dotnet test</c>; a Timing or Cell class would make the pre-check slow, or red for a reason that is no structural one.
/// So each line names a class that exists in the project its name says (<c>TradeAgent.Tests.Unit</c> → <c>tests/TradeAgent.UnitTests</c>),
/// declared in that namespace, holding at least one test, carrying no <c>Category</c> Timing or Cell trait on any part, and says why it is
/// structural; and no class is listed twice.
/// </summary>
public class StructuralTestsListTests
{
    static readonly Dictionary<string, string> Folders = new(StringComparer.Ordinal)
    {
        ["Unit"] = "TradeAgent.UnitTests",
        ["Integration"] = "TradeAgent.IntegrationTests",
        ["Fault"] = "TradeAgent.FaultTests",
    };

    [Fact]
    public void Every_listed_class_exists_holds_a_test_and_is_neither_timing_nor_cell()
    {
        var root = DayOnePrograms.RepoRoot();
        var list = File.ReadAllText(Path.Combine(root, "tools", "structural-tests.txt"));
        var problems = Problems(list, Path.Combine(root, "tests"));
        Assert.True(problems.Count == 0, "tools/structural-tests.txt: " + string.Join("; ", problems));
        Assert.True(Entries(list).Count >= 10, "the list holds fewer than ten classes");
    }

    /// <summary>The guard names each way a line can be wrong, and passes a right one.</summary>
    [Fact]
    public void The_guard_names_a_misspelt_class_a_timing_one_a_cell_one_a_silent_line_and_a_twice_listed_one()
    {
        using var scratch = TestEnv.NewScratch("structural-list");
        var unit = Path.Combine(scratch.Dir, "TradeAgent.UnitTests");
        Directory.CreateDirectory(unit);
        File.WriteAllText(Path.Combine(unit, "Sample.cs"), """
            namespace TradeAgent.Tests.Unit;
            public class GoodGuardTests { [Fact] public void A() { } }
            [Trait("Category", "Timing")]
            public class SlowTests { [Fact] public void A() { } }
            public partial class CellPartTests { [Fact] public void A() { } }
            public class HelperOnly { }
            // [Trait("Category", "Cell")] on a comment changes nothing:
            public class CommentedTests { [Fact] public void A() { var s = "Trait(\"Category\", \"Cell\")"; } }
            """);
        File.WriteAllText(Path.Combine(unit, "Sample.Cell.cs"), """
            namespace TradeAgent.Tests.Unit;
            [Trait("Category", "Cell")]
            public partial class CellPartTests { }
            """);
        File.WriteAllText(Path.Combine(unit, "Elsewhere.cs"), """
            namespace TradeAgent.Tests.Integration;
            public class WrongNamespaceTests { [Fact] public void A() { } }
            """);
        const string list = """
            # a comment
            TradeAgent.Tests.Unit.GoodGuardTests  reads src/
            TradeAgent.Tests.Unit.CommentedTests  reads src/
            TradeAgent.Tests.Unit.GoodGaurdTests  misspelt
            TradeAgent.Tests.Unit.SlowTests  timing
            TradeAgent.Tests.Unit.CellPartTests  a cell on its other part
            TradeAgent.Tests.Unit.HelperOnly  holds no test
            TradeAgent.Tests.Unit.WrongNamespaceTests  declared under another segment
            TradeAgent.Tests.Elsewhere.GoodGuardTests  no such project
            TradeAgent.Tests.Unit.GoodGuardTests  twice
            TradeAgent.Tests.Unit.GoodGuardTests
            """;

        var problems = Problems(list, scratch.Dir);

        Assert.Equal(
        [
            "TradeAgent.Tests.Unit.GoodGaurdTests: no such class in tests/TradeAgent.UnitTests",
            "TradeAgent.Tests.Unit.SlowTests: carries the Timing category",
            "TradeAgent.Tests.Unit.CellPartTests: carries the Cell category",
            "TradeAgent.Tests.Unit.HelperOnly: holds no test",
            "TradeAgent.Tests.Unit.WrongNamespaceTests: no such class in tests/TradeAgent.UnitTests",
            "TradeAgent.Tests.Elsewhere.GoodGuardTests: no test project for 'Elsewhere'",
            "TradeAgent.Tests.Unit.GoodGuardTests: listed twice",
            "TradeAgent.Tests.Unit.GoodGuardTests: listed twice",
            "TradeAgent.Tests.Unit.GoodGuardTests: says no why",
        ], problems);
    }

    /// <summary>The list's lines that name a class: the name and the why after it ('#' lines and blank lines aside), as precheck.sh reads them.</summary>
    static List<(string Name, string Why)> Entries(string list) =>
        [.. list.Replace("\r", "", StringComparison.Ordinal).Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split((char[])[' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries))
            .Select(p => (p[0], p.Length > 1 ? p[1].Trim() : ""))];

    // The two categories the pre-check never runs, as a test declares them. Read BEFORE comments and strings are blanked, then made an
    // identifier, so the same words in a comment or a string literal are blanked with it.
    static readonly Regex Category = new(@"Trait\s*\(\s*""Category""\s*,\s*""(Timing|Cell)""\s*\)", RegexOptions.CultureInvariant);

    internal static List<string> Problems(string list, string testsRoot)
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var scanned = new Dictionary<string, Dictionary<string, (string Namespaces, string Text)>>(StringComparer.Ordinal);
        foreach (var (name, why) in Entries(list))
        {
            if (!seen.Add(name)) problems.Add($"{name}: listed twice");
            if (why.Length == 0) { problems.Add($"{name}: says no why"); continue; }
            var parts = name.Split('.');
            if (parts.Length != 4 || parts[0] != "TradeAgent" || parts[1] != "Tests") { problems.Add($"{name}: not TradeAgent.Tests.<Segment>.<Class>"); continue; }
            if (!Folders.TryGetValue(parts[2], out var folder)) { problems.Add($"{name}: no test project for '{parts[2]}'"); continue; }
            if (!scanned.TryGetValue(folder, out var types)) scanned[folder] = types = Types(Path.Combine(testsRoot, folder));
            if (!types.TryGetValue(parts[3], out var type) || !type.Namespaces.Contains($"|TradeAgent.Tests.{parts[2]}|", StringComparison.Ordinal))
            {
                problems.Add($"{name}: no such class in tests/{folder}");
                continue;
            }
            if (!ProcessWideEnvironmentTests.Test.IsMatch(type.Text)) problems.Add($"{name}: holds no test");
            foreach (var category in new[] { "Timing", "Cell" })
                if (type.Text.Contains("Category" + category, StringComparison.Ordinal)) problems.Add($"{name}: carries the {category} category");
        }
        return problems;
    }

    /// <summary>Every top-level type under <paramref name="dir"/> (bin and obj aside), its parts read together: the namespaces it is declared in, and its attributes and body.</summary>
    static Dictionary<string, (string Namespaces, string Text)> Types(string dir)
    {
        var types = new Dictionary<string, (string Namespaces, string Text)>(StringComparer.Ordinal);
        if (!Directory.Exists(dir)) return types;
        var sep = Path.DirectorySeparatorChar;
        foreach (var file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{sep}bin{sep}", StringComparison.Ordinal) || file.Contains($"{sep}obj{sep}", StringComparison.Ordinal)) continue;
            var code = InvariantRegistryTests.Strip(Category.Replace(File.ReadAllText(file), m => " Category" + m.Groups[1].Value + " "), keepStrings: false);
            var ns = string.Concat(Regex.Matches(code, @"\bnamespace\s+([\w.]+)").Select(m => "|" + m.Groups[1].Value + "|"));
            foreach (var (name, attributes, body) in ProcessWideEnvironmentTests.TopLevelTypes(code))
                types[name] = types.TryGetValue(name, out var t) ? (t.Namespaces + ns, t.Text + "\n" + attributes + "\n" + body) : (ns, attributes + "\n" + body);
        }
        return types;
    }
}

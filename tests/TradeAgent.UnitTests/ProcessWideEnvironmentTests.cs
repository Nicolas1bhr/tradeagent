using System.Text.RegularExpressions;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A TEST CLASS THAT SETS A PROCESS-WIDE VARIABLE RUNS ALONE (<c>U-ci-precheck</c> item 4). <c>Environment.SetEnvironmentVariable</c>
/// changes the environment of the whole test process, and xUnit runs the classes of one assembly in parallel, so while one class
/// holds a made-up value another class's child process inherits it: <c>AgentEnvironmentTests</c> set DOTNET_ROOT to
/// <c>/where/the/app/found/DOTNET_ROOT</c> while the puppet lives and the launcher tests start framework-dependent programs, which
/// then cannot find their runtime (the U-main-red-3 card), and <c>RuntimeResolutionTests</c> rewrote PATH under every other class's
/// launches. Such a class names a collection whose definition says <c>DisableParallelization = true</c>: xUnit runs those after the
/// parallel classes, one at a time, so no other class runs beside it. A class that only gives a CHILD its environment
/// (<c>ProcessStartInfo.Environment</c>) changes nothing process-wide and is not asked. A module initializer that sets the
/// process's home once, before any test runs, is no test class and is not asked either.
/// </summary>
public class ProcessWideEnvironmentTests
{
    static readonly string[] Assemblies = ["TradeAgent.UnitTests", "TradeAgent.IntegrationTests", "TradeAgent.FaultTests", "TradeAgent.UiTests"];

    [Fact]
    public void Every_test_class_that_sets_a_process_wide_variable_runs_alone()
    {
        var root = Path.Combine(DayOnePrograms.RepoRoot(), "tests");
        var offenders = Assemblies.SelectMany(a => Offenders(Path.Combine(root, a)).Select(c => $"{a}: {c}")).ToList();
        Assert.True(offenders.Count == 0,
            "these test classes set a process-wide environment variable while other classes run beside them; name a collection whose " +
            "definition disables parallelization (VendorOverrideFiles.Name): " + string.Join(", ", offenders));
    }

    /// <summary>
    /// THE SCAN READS WHAT IT CLAIMS: a setter outside a lone collection is named, the same class in one is not, a partial class's
    /// collection on its other part counts, a child's own environment is no setter, a class with no test is not asked, and a nested
    /// helper's setter belongs to the class that holds it.
    /// </summary>
    [Fact]
    public void The_scan_names_a_setter_outside_a_lone_collection_and_nothing_else()
    {
        using var scratch = TestEnv.NewScratch("process-wide-env");
        Directory.CreateDirectory(scratch.Dir);
        File.WriteAllText(Path.Combine(scratch.Dir, "Lone.cs"), """
            namespace Sample;
            [CollectionDefinition(Name, DisableParallelization = true)]
            public sealed class Lone { public const string Name = "lone"; }
            [CollectionDefinition(Name)]
            public sealed class Crowd { public const string Name = "crowd"; }
            """);
        File.WriteAllText(Path.Combine(scratch.Dir, "Tests.cs"), """
            namespace Sample;
            public class Loose { [Fact] public void A() { Environment.SetEnvironmentVariable("X", "1"); } }
            [Collection(Lone.Name)]
            public class Alone { [Fact] public void A() { Environment.SetEnvironmentVariable("X", "1"); } }
            [Collection(Crowd.Name)]
            public class InACrowd { [Fact] public void A() { Environment.SetEnvironmentVariable("X", "1"); } }
            public class ChildOnly { [Fact] public void A() { var p = new ProcessStartInfo("x"); p.Environment["X"] = "1"; } }
            static class Initializer { [ModuleInitializer] internal static void Init() => Environment.SetEnvironmentVariable("X", "1"); }
            public class Nested { [Fact] public void A() { } sealed class Helper { void B() => Environment.SetEnvironmentVariable("X", "1"); } }
            // public class Commented { [Fact] public void A() { Environment.SetEnvironmentVariable("X", "1"); } }
            public class Words { [Fact] public void A() { var s = "Environment.SetEnvironmentVariable(\"X\", \"1\")"; } }
            [Collection(Lone.Name)]
            public partial class Split { }
            """);
        File.WriteAllText(Path.Combine(scratch.Dir, "Split.cs"), """
            namespace Sample;
            public partial class Split { [Fact] public void A() { Environment.SetEnvironmentVariable("X", "1"); } }
            """);

        Assert.Equal(["InACrowd", "Loose", "Nested"], Offenders(scratch.Dir));
    }

    static readonly Regex Lone = new(@"CollectionDefinition\s*\([^)]*DisableParallelization\s*=\s*true", RegexOptions.CultureInvariant);
    static readonly Regex Uses = new(@"\bCollection\s*\(\s*(\w+)\s*\.\s*Name\s*\)", RegexOptions.CultureInvariant);
    // An attribute whose name ends in Fact or Theory ([Fact], [UnixFact], [OnlyOnFact(...)], "Fact, Trait(...)"). No nested quantifier: one
    // with them took this scan 31 minutes over the four projects.
    internal static readonly Regex Test = new(@"(?:Fact|Theory)\s*[\](,]", RegexOptions.CultureInvariant);
    static readonly Regex Sets = new(@"\bEnvironment\s*\.\s*SetEnvironmentVariable\s*\(", RegexOptions.CultureInvariant);

    /// <summary>
    /// The test classes under <paramref name="dir"/> (bin and obj aside) that set a process-wide variable and name no collection that
    /// runs alone, by name, sorted. Every top-level type's parts are read together (a partial class may carry its collection on one
    /// file and its tests on another), comments and string literals blanked first (<see cref="InvariantRegistryTests.Strip"/>).
    /// </summary>
    internal static List<string> Offenders(string dir)
    {
        var types = new Dictionary<string, (string Attributes, string Body)>(StringComparer.Ordinal);
        var sep = Path.DirectorySeparatorChar;
        foreach (var file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{sep}bin{sep}", StringComparison.Ordinal) || file.Contains($"{sep}obj{sep}", StringComparison.Ordinal)) continue;
            foreach (var (name, attributes, body) in TopLevelTypes(InvariantRegistryTests.Strip(File.ReadAllText(file), keepStrings: false)))
                types[name] = types.TryGetValue(name, out var seen) ? (seen.Attributes + "\n" + attributes, seen.Body + "\n" + body) : (attributes, body);
        }

        var lone = types.Where(t => Lone.IsMatch(t.Value.Attributes)).Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
        return [.. types
            .Where(t => Test.IsMatch(t.Value.Body) && Sets.IsMatch(t.Value.Body))
            .Where(t => !Uses.Matches(t.Value.Attributes).Any(m => lone.Contains(m.Groups[1].Value)))
            .Select(t => t.Key)
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Every type declared outside any other type in <paramref name="code"/>: its name, what stands before it since the last
    /// <c>;</c>, <c>{</c> or <c>}</c> (its attributes), and its braces' contents (nested types included).
    /// </summary>
    internal static IEnumerable<(string Name, string Attributes, string Body)> TopLevelTypes(string code)
    {
        var scopes = new Stack<(string? Type, int Open, string Attributes)>();
        string? pending = null;
        string? lastWord = null;
        var boundary = 0;
        var attributesEnd = 0;
        var i = 0;
        while (i < code.Length)
        {
            var ch = code[i];
            if (char.IsLetter(ch) || ch == '_' || ch == '@')
            {
                var start = i;
                while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] == '_' || code[i] == '@')) i++;
                var word = code[start..i];
                if (pending is null && lastWord is "class" or "struct" or "record" or "interface" && !scopes.Any(s => s.Type is not null))
                {
                    pending = word;
                    attributesEnd = start;
                }
                lastWord = word;
                continue;
            }
            switch (ch)
            {
                case '{':
                    scopes.Push((pending, i, pending is null ? "" : code[boundary..attributesEnd]));
                    pending = null;
                    boundary = i + 1;
                    break;
                case '}':
                    if (scopes.Count > 0)
                    {
                        var (type, open, attributes) = scopes.Pop();
                        if (type is not null && !scopes.Any(s => s.Type is not null)) yield return (type, attributes, code[(open + 1)..i]);
                    }
                    boundary = i + 1;
                    break;
                case ';':
                    if (pending is not null && !scopes.Any(s => s.Type is not null)) { yield return (pending, code[boundary..attributesEnd], ""); pending = null; }
                    boundary = i + 1;
                    break;
            }
            i++;
        }
    }
}

using System.Xml.Linq;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>Reads this CI run's results: only the build's <c>proof-record</c> job has them, and it fails unless this row ran and passed.</summary>
public sealed class RunResultsFactAttribute : FactAttribute
{
    public const string Variable = "TA_RUN_RESULTS";

    public RunResultsFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"reads a whole CI run's results: the proof-record job names them in {Variable}";
    }
}

/// <summary>
/// WHAT RAN, MEASURED (R29 F-E1): the results of one CI run, a folder per job as the run's artifacts unpack — <c>test-results-ubuntu-latest</c>,
/// <c>test-results-macos-latest</c>, <c>test-results-windows-latest-shard-N</c>. A test ran on a platform when it has at least one row
/// there and every row Passed, so a theory half skipped did not run. Timing's rescue is read as CI reads it: where the retry's file stands
/// beside the first attempt's, the retry is the answer.
/// </summary>
public static class RunResults
{
    static readonly XNamespace Trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    static ProofPlatform PlatformOf(string job) =>
        job.Contains("ubuntu", StringComparison.Ordinal) ? ProofPlatform.Linux
        : job.Contains("windows", StringComparison.Ordinal) ? ProofPlatform.Windows
        : job.Contains("macos", StringComparison.Ordinal) ? ProofPlatform.MacOS
        : ProofPlatform.None;

    /// <summary>Each test as <c>Class.Method</c>, with the platforms it ran and passed on, and the platforms the folder holds results for.</summary>
    public static (IReadOnlyDictionary<string, ProofPlatform> Ran, ProofPlatform Reported) Read(string folder)
    {
        var rows = new Dictionary<string, Dictionary<ProofPlatform, List<string>>>(StringComparer.Ordinal);
        var reported = ProofPlatform.None;
        foreach (var job in Directory.GetDirectories(folder))
        {
            var platform = PlatformOf(Path.GetFileName(job));
            if (platform == ProofPlatform.None) continue;
            foreach (var file in Directory.GetFiles(job, "results*.trx", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file) == "results-timing.trx"
                    && File.Exists(Path.Combine(Path.GetDirectoryName(file)!, "results-timing-retry.trx"))) continue;
                reported |= platform;
                var trx = XDocument.Load(file);
                var names = trx.Descendants(Trx + "UnitTest").ToDictionary(
                    u => (string)u.Attribute("id")!,
                    u =>
                    {
                        var m = u.Element(Trx + "TestMethod")!;
                        return $"{((string)m.Attribute("className")!).Split('.', '+')[^1]}.{(string)m.Attribute("name")!}";
                    }, StringComparer.Ordinal);
                foreach (var result in trx.Descendants(Trx + "UnitTestResult"))
                {
                    var name = names[(string)result.Attribute("testId")!];
                    if (!rows.TryGetValue(name, out var byPlatform)) rows[name] = byPlatform = [];
                    if (!byPlatform.TryGetValue(platform, out var outcomes)) byPlatform[platform] = outcomes = [];
                    outcomes.Add((string)result.Attribute("outcome")!);
                }
            }
        }
        var ran = rows.ToDictionary(
            r => r.Key,
            r => r.Value.Where(p => p.Value.All(o => o == "Passed")).Aggregate(ProofPlatform.None, (all, p) => all | p.Key),
            StringComparer.Ordinal);
        return (ran, reported);
    }

    /// <summary>
    /// WHERE THE RECORD AND THE RUN DISAGREE, both directions, one line each: empty only when every test in <paramref name="named"/> ran and
    /// passed exactly where <paramref name="runsOn"/> says, and the run reported all three platforms.
    /// </summary>
    public static List<string> Disagreements(IEnumerable<string> named, Func<string, ProofPlatform> runsOn, string folder)
    {
        var (ran, reported) = Read(folder);
        var problems = new List<string>();
        var missing = InvariantRegistry.Everywhere & ~reported;
        if (missing != ProofPlatform.None) problems.Add($"this run has no results from {missing}");
        foreach (var test in named.Distinct(StringComparer.Ordinal))
        {
            var measured = ran.GetValueOrDefault(test, ProofPlatform.None);
            if (measured != runsOn(test))
                problems.Add($"{test}: the record says {runsOn(test)}, this run measured {measured}");
        }
        return problems;
    }
}

/// <summary>
/// THE RECORD IS WHAT RAN (R29 F-E1, the half a source scan cannot see). <see cref="InvariantRegistry.NotEverywhere"/> says where each named test
/// does not run, and the page reads advisory there; this compares that with one CI run's results and is red on any difference, so a named
/// test that stopped running on a platform — skipped, filtered out, moved to a job that does not run — cannot go on backing a claim there.
/// </summary>
public class InvariantRunsTests
{
    const string Held = "HeldTests.Everywhere_it_should";
    const string Unix = "HeldTests.Skipped_on_windows";

    static ProofPlatform Record(string test) => test == Unix ? ProofPlatform.Linux | ProofPlatform.MacOS : InvariantRegistry.Everywhere;

    /// <summary>
    /// THE ONE COMPARISON AGAINST THE REAL RUN. The proof-record job runs this row alone and reads from its own results that it ran and
    /// passed: anywhere else it is skipped, and says so.
    /// </summary>
    [RunResultsFact]
    public void The_record_of_where_each_named_test_runs_is_what_this_run_measured()
    {
        var folder = Environment.GetEnvironmentVariable(RunResultsFactAttribute.Variable)!;
        var named = InvariantRegistry.All.SelectMany(i => i.Tests.Concat(i.Seats.SelectMany(s => s.Tests))).Distinct(StringComparer.Ordinal).ToList();
        Assert.NotEmpty(named);
        var problems = RunResults.Disagreements(named, InvariantRegistry.RunsOn, folder);
        Assert.True(problems.Count == 0, "the page would say what did not happen — change the test or InvariantRegistry.NotEverywhere:\n" + string.Join("\n", problems));
        Assert.True(RunResults.Read(folder).Ran.Count > 1000, "too few tests in this run's results to be the whole run");
    }

    [Fact]
    public void A_record_that_says_what_ran_agrees_with_the_run() =>
        InFolder(dir =>
        {
            Job(dir, "test-results-ubuntu-latest", (Held, "Passed"), (Unix, "Passed"));
            Job(dir, "test-results-macos-latest", (Held, "Passed"), (Unix, "Passed"));
            Job(dir, "test-results-windows-latest-shard-2", (Unix, "NotExecuted"));
            Job(dir, "test-results-windows-latest-shard-4", (Held, "Passed"));
            Assert.Empty(RunResults.Disagreements([Held, Unix], Record, dir));
        });

    /// <summary>EVERY WAY A TEST STOPS RUNNING READS AS ONE: skipped, failed, gone from a platform, half of a theory, or a whole platform missing.</summary>
    [Fact]
    public void A_test_that_did_not_run_where_the_record_says_turns_the_run_red() =>
        InFolder(dir =>
        {
            Job(dir, "test-results-ubuntu-latest", (Held, "NotExecuted"), (Unix, "Passed"));
            Job(dir, "test-results-macos-latest", (Unix, "Passed"), (Unix, "NotExecuted"));
            Job(dir, "test-results-windows-latest-shard-1", (Held, "Failed"));
            var problems = RunResults.Disagreements([Held, Unix], Record, dir);
            Assert.Equal(
                [$"{Held}: the record says Linux, Windows, MacOS, this run measured None", $"{Unix}: the record says Linux, MacOS, this run measured Linux"],
                problems);
        });

    [Fact]
    public void A_platform_with_no_results_is_no_proof_for_it() =>
        InFolder(dir =>
        {
            Job(dir, "test-results-ubuntu-latest", (Held, "Passed"));
            Job(dir, "test-results-macos-latest", (Held, "Passed"));
            var problems = RunResults.Disagreements([Held], Record, dir);
            Assert.Equal(["this run has no results from Windows", $"{Held}: the record says Linux, Windows, MacOS, this run measured Linux, MacOS"], problems);
        });

    /// <summary>THE OTHER DIRECTION: a test the record holds back from a platform that did run there is a stale record, and stale in either direction is a lie.</summary>
    [Fact]
    public void A_test_that_runs_where_the_record_says_it_does_not_turns_the_run_red() =>
        InFolder(dir =>
        {
            foreach (var job in new[] { "test-results-ubuntu-latest", "test-results-macos-latest", "test-results-windows-latest-shard-3" })
                Job(dir, job, (Unix, "Passed"));
            Assert.Equal([$"{Unix}: the record says Linux, MacOS, this run measured Linux, Windows, MacOS"], RunResults.Disagreements([Unix], Record, dir));
        });

    /// <summary>A RESCUED TIMING TEST RAN: CI answers with the retry, so this does; without a retry a failed first attempt stands.</summary>
    [Fact]
    public void The_timing_retry_answers_for_the_first_attempt() =>
        InFolder(dir =>
        {
            var folder = Path.GetDirectoryName(Job(dir, "test-results-ubuntu-latest", (Held, "Failed")))!;
            File.Move(Directory.GetFiles(folder).Single(), Path.Combine(folder, "results-timing.trx"));
            File.Move(Job(dir, "test-results-ubuntu-latest", (Held, "Passed")), Path.Combine(folder, "results-timing-retry.trx"));
            Assert.Equal(ProofPlatform.Linux, RunResults.Read(dir).Ran[Held]);

            File.Delete(Path.Combine(folder, "results-timing-retry.trx"));
            Assert.Equal(ProofPlatform.None, RunResults.Read(dir).Ran[Held]);
        });

    /// <summary>A nested class is named as the registry names it, by the class that encloses the test.</summary>
    [Fact]
    public void A_nested_class_is_read_by_its_own_name() =>
        InFolder(dir =>
        {
            Job(dir, "test-results-ubuntu-latest", ("Outer+Inner.Runs", "Passed"));
            Assert.Equal(ProofPlatform.Linux, RunResults.Read(dir).Ran["Inner.Runs"]);
        });

    static void InFolder(Action<string> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "run-results-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { body(dir); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>One job's results, shaped as a real trx: tests in a table, results pointing at them, the class under its namespace.</summary>
    static string Job(string dir, string job, params (string Test, string Outcome)[] rows)
    {
        XNamespace t = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var folder = Path.Combine(dir, job, "TestResults");
        Directory.CreateDirectory(folder);
        var ids = rows.Select(r => r.Test).Distinct(StringComparer.Ordinal).ToDictionary(n => n, _ => Guid.NewGuid().ToString(), StringComparer.Ordinal);
        var file = Path.Combine(folder, $"results-{Guid.NewGuid():N}.trx");
        new XDocument(new XElement(t + "TestRun",
            new XElement(t + "Results", rows.Select(r => new XElement(t + "UnitTestResult", new XAttribute("testId", ids[r.Test]), new XAttribute("outcome", r.Outcome)))),
            new XElement(t + "TestDefinitions", ids.Select(i => new XElement(t + "UnitTest", new XAttribute("id", i.Value),
                new XElement(t + "TestMethod", new XAttribute("className", "TradeAgent.Tests.Unit." + i.Key[..i.Key.LastIndexOf('.')]),
                    new XAttribute("name", i.Key[(i.Key.LastIndexOf('.') + 1)..]))))))).Save(file);
        return file;
    }
}

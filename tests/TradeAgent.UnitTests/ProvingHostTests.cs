using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Core.Proving;
using TradeAgent.Core.Strategy;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE PROVING HOST, AS A CHILD PROCESS (<c>U-proving-ground-c</c>; <c>docs/ORCHESTRATION.md</c> § 2.14, § 7; VISION § 3's earning rule,
/// route 2): a test writes a run — three small synthetic markets (a planted edge, noise, a pre-fee trap) and two configurations at
/// equal budgets, the floor and one scripted agent — starts <c>TradeAgent.ProvingHost</c> in a fresh proving home on a pipe of its own,
/// and reads the scores it wrote into <c>proving.db</c>. SIMULATED: process skill on synthetic markets, never market truth, never
/// intelligence.
/// </summary>
public class ProvingHostTests(ProvingHostTests.Ladder ladder, ITestOutputHelper log) : IClassFixture<ProvingHostTests.Ladder>
{
    internal static readonly DateTimeOffset Start = new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
    const int Days = 14, HoldoutDay = 7;

    /// <summary>The noise and trap markets' first days: each a day after the market before it ends, so no market's bars reach another's holdout.</summary>
    const int NoiseDay = Days + 1, TrapDay = 2 * Days + 2;
    const long EdgeDataset = 1, NoiseDataset = 2, TrapDataset = 3;
    /// <summary>The last instant a market starting <paramref name="startDay"/> days into the run may be researched to: an hour and a minute before its cutoff.</summary>
    static string ResearchFrom(int startDay) => Start.AddDays(startDay).ToString("O");

    /// <summary>See <see cref="ResearchFrom"/>: the window's end.</summary>
    static string ResearchTo(int startDay) => Start.AddDays(startDay + HoldoutDay).AddMinutes(-61).ToString("O");

    // ---- the host, started --------------------------------------------------------------------------------------------------

    static string HostDll
    {
        get
        {
            var dll = Path.GetFullPath(typeof(ProvingHostTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "ProvingHostDll").Value!);
            Assert.True(File.Exists(dll), $"the proving host was not built: no {dll}");
            return dll;
        }
    }

    /// <summary>What one start of the host did.</summary>
    public sealed record Ran(int Code, string Out, string Err)
    {
        public override string ToString() => $"exit {Code}\n{Out}\n--- stderr ---\n{Err}";
    }

    /// <summary>Starts the host with <paramref name="args"/>, its home and pipe as given (null leaves the variable unset), and waits.</summary>
    internal static Ran Host(string? home, string? pipe, params string[] args)
    {
        var start = new ProcessStartInfo(Puppet.DotnetHost)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(HostDll);
        foreach (var a in args) start.ArgumentList.Add(a);
        if (home is null) start.Environment.Remove("TRADEAGENT_HOME"); else start.Environment["TRADEAGENT_HOME"] = home;
        if (pipe is null) start.Environment.Remove("TRADEAGENT_PIPE"); else start.Environment["TRADEAGENT_PIPE"] = pipe;
        start.Environment.Remove(Security.AgentGrants.Variable);
        if (string.IsNullOrEmpty(start.Environment.TryGetValue("DOTNET_ROOT", out var root) ? root : null))
            start.Environment["DOTNET_ROOT"] = Puppet.DotnetRoot;
        using var p = Process.Start(start)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromMinutes(25)))
        {
            p.Kill(entireProcessTree: true);
            Assert.Fail("the proving host did not exit within 25 minutes");
        }
        return new Ran(p.ExitCode, stdout.Result, stderr.Result);
    }

    static string Pipe() => "ta-proving-" + Guid.NewGuid().ToString("n")[..12];

    // ---- a run, as data -----------------------------------------------------------------------------------------------------

    /// <summary>A program over the bare signal: long while it is above 0.0030 (the referee-power tests' program).</summary>
    internal static string Program(string symbol) =>
        PlantedEdgeTests.Program(symbol, ProvingSources.Signal, ProvingSources.ValueField, "x > 0.0030", "x <= 0.0030").Trim();

    /// <summary>The version id the app gives <paramref name="text"/>: its content hash, as a proving home parses it.</summary>
    internal static string VersionOf(string text)
    {
        var parsed = StrategyParser.Parse(text, HomeKind.Proving);
        Assert.True(parsed.Ok, parsed.Why);
        return parsed.Program!.StrategyId;
    }

    /// <summary>
    /// THE SCRIPTED AGENT'S TWO TURNS. The brief's: the edge program written, backtested before the cutoff and sent for a verdict; the
    /// same program on the pre-fee trap's instrument, backtested and sent for a verdict; its usage reported; an answer. Then the turn its
    /// verdicts wake it for: it reads them (10,000 in, 1,000 out = 0.028) and answers.
    /// </summary>
    internal static string[][] AgentScript(long input = 100_000, long output = 20_000)
    {
        string[] one =
        [
            "write strategies/edge.strategy " + Program("SYNA"),
            $"trade backtest --strategy strategies/edge.strategy --dataset {EdgeDataset} --from {ResearchFrom(0)} --to {ResearchTo(0)}",
            $"trade verdict --version {VersionOf(Program("SYNA"))} --dataset {EdgeDataset}",
            "write strategies/trap.strategy " + Program("SYNC"),
            $"trade backtest --strategy strategies/trap.strategy --dataset {TrapDataset} --from {ResearchFrom(TrapDay)} --to {ResearchTo(TrapDay)}",
            $"trade verdict --version {VersionOf(Program("SYNC"))} --dataset {TrapDataset}",
            $"usage {input} 0 {output}",
            "final I measured one hypothesis on two instruments and asked for both verdicts."
        ];
        return [one, ["usage 10000 0 1000", "final I read both verdicts: one passed, the trap's did not."]];
    }

    /// <summary>The chair's one turn, played whenever the kernel wakes it (a promotion's boundary): 10,000 in and 1,000 out = 0.028.</summary>
    internal static readonly string[][] ChairScript = [["usage 10000 0 1000", "final I read the promotion; nothing to deploy from a proving home."]];

    /// <summary>What one chair turn costs at the owner's rate.</summary>
    const decimal ChairTurn = 0.028m;

    internal static JsonObject Configuration(string name, string kind, decimal envelope = 100m, string[][]? script = null,
        int trials = 10, int verdicts = 2, int deadline = 900)
    {
        static JsonArray Turns(string[][] turns) => new([.. turns.Select(t => (JsonNode?)new JsonArray([.. t.Select(s => (JsonNode?)s)]))]);
        var positions = new JsonArray();
        if (script is not null)
        {
            positions.Add(new JsonObject { ["id"] = CouncilRoles.Research, ["unit"] = OrgIds.Research, ["script"] = Turns(script) });
            positions.Add(new JsonObject { ["id"] = CouncilRoles.Operations, ["unit"] = OrgIds.Operations, ["script"] = Turns(ChairScript) });
        }
        return new JsonObject
        {
            ["version"] = ProvingConfiguration.Version, ["name"] = name, ["kind"] = kind,
            ["chart"] = new JsonObject
            {
                ["units"] = script is null ? new JsonArray() : new JsonArray(OrgIds.Root, OrgIds.Research, OrgIds.Operations),
                ["positions"] = positions
            },
            ["ai_envelope"] = envelope, ["trial_budget"] = trials, ["verdict_budget"] = verdicts, ["deadline_seconds"] = deadline
        };
    }

    /// <summary>The three small markets every configuration of a run works on: a planted edge, noise, a pre-fee trap.</summary>
    static JsonArray Markets() =>
    [
        new JsonObject { ["seed"] = "701", ["instrument"] = "SYNA", ["days"] = Days, ["holdout_from_day"] = HoldoutDay, ["beta_ticks"] = 12 },
        new JsonObject { ["seed"] = "702", ["instrument"] = "SYNB", ["start_day"] = NoiseDay, ["days"] = Days, ["holdout_from_day"] = HoldoutDay },
        new JsonObject { ["seed"] = "703", ["instrument"] = "SYNC", ["start_day"] = TrapDay, ["days"] = Days, ["holdout_from_day"] = HoldoutDay, ["beta_ticks"] = 2, ["trap"] = "pre-fee-only" }
    ];

    /// <summary>A fresh proving home under <paramref name="dir"/>, the run file naming <paramref name="configurations"/>, and its path.</summary>
    internal static (string Home, string RunFile) Prepare(string dir, params JsonObject[] configurations)
    {
        var home = ProvingHome.Create(Path.Combine(dir, "home")).Dir;
        var files = new JsonArray();
        foreach (var c in configurations)
        {
            var file = Path.Combine(dir, (string)c["name"]! + ".json");
            File.WriteAllText(file, c.ToJsonString());
            files.Add(file);
        }
        var run = Path.Combine(dir, "run.json");
        File.WriteAllText(run, new JsonObject
        {
            ["version"] = 1, ["start"] = Start.ToString("O"), ["markets"] = Markets(), ["configurations"] = files
        }.ToJsonString());
        return (home, run);
    }

    /// <summary>The scores of the newest run in <paramref name="home"/>'s <c>proving.db</c>.</summary>
    internal static IReadOnlyList<ProvingScore> Scores(string home)
    {
        using var store = new ProvingStore(Path.Combine(home, ProvingStore.FileName));
        return store.Scores(store.LastRun() ?? throw new InvalidOperationException("no run was scored"));
    }

    /// <summary>
    /// THE LADDER'S ONE RUN, shared by the class: the floor and the scripted agent, at equal budgets, on the three markets.
    /// </summary>
    public sealed class Ladder : IDisposable
    {
        readonly TestEnv.ScratchDir _scratch = TestEnv.NewScratch("proving-ladder");
        readonly Lazy<(string Home, Ran Ran)> _run;

        public Ladder() => _run = new(() =>
        {
            var (home, run) = Prepare(_scratch.Dir,
                Configuration("floor", "floor"),
                Configuration("one-agent", "seats", script: AgentScript()));
            return (home, Host(home, Pipe(), "run", "--run", run));
        });

        public string Home => _run.Value.Home;
        public Ran Ran => _run.Value.Ran;

        public IReadOnlyList<ProvingScore> Scores()
        {
            Assert.True(Ran.Code == 0, Ran.ToString());
            return ProvingHostTests.Scores(Home);
        }

        public void Dispose() => _scratch.Dispose();
    }

    // ---- the tests ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (a) A PUPPET CONFIGURATION RUNS ONLY IN THE PROVING HOST AND NEVER IN THE TEST ASSEMBLY'S HOME. The host started in this
    /// assembly's own home — the one every puppet life here runs in, which carries no proving mark — is refused in words naming it,
    /// before any store is opened there; and the ladder's agent ran in a configuration home of its own under the proving home, whose
    /// launches are in THAT home's ledger, a child process's, never this assembly's.
    /// </summary>
    [Fact]
    public void A_puppet_configuration_runs_only_in_the_proving_host_and_never_in_the_test_assemblys_home()
    {
        using var scratch = TestEnv.NewScratch("proving-a");
        Directory.CreateDirectory(scratch.Dir);
        var config = Path.Combine(scratch.Dir, "agent.json");
        File.WriteAllText(config, Configuration("one-agent", "seats", script: AgentScript()).ToJsonString());
        var before = Directory.Exists(Path.Combine(TestEnv.Home, "configurations"));

        var refused = Host(TestEnv.Home, Pipe(), "apply", "--configuration", config, "--scripts", scratch.Dir, "--puppet", Puppet.Dll,
            "--trade", Puppet.TradeCliDir, "--report", Path.Combine(scratch.Dir, "report.json"));
        log.WriteLine(refused.ToString());
        Assert.Equal(2, refused.Code);
        Assert.Contains("A proving ground runs only in a home marked as one", refused.Err, StringComparison.Ordinal);
        Assert.Contains(TestEnv.Home, refused.Err, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(scratch.Dir, "report.json")), "a refused host wrote a report");
        Assert.Equal(before, Directory.Exists(Path.Combine(TestEnv.Home, "configurations")));

        // THE REAL GATEWAY'S PIPE IS REFUSED TOO, in a marked proving home.
        var home = ProvingHome.Create(Path.Combine(scratch.Dir, "home")).Dir;
        var pipe = Host(home, Paths.DefaultPipeName, "run", "--run", Path.Combine(scratch.Dir, "none.json"));
        Assert.Equal(2, pipe.Code);
        Assert.Contains("is the real gateway's", pipe.Err, StringComparison.Ordinal);

        // THE LADDER'S AGENT RAN IN ITS OWN CONFIGURATION HOME, a child of the proving home.
        Assert.Equal(0, ladder.Ran.Code);
        var agentHome = Path.Combine(ladder.Home, "configurations", "2-one-agent", "state", "tradeagent.db");
        using var db = new Database(agentHome, HomeKind.Proving);
        var attempts = new AiAttemptStore(db).Between(Start.AddYears(-1), DateTimeOffset.UtcNow.AddDays(1));
        Assert.Contains(attempts, a => a.Role == CouncilRoles.Research && a.State == AiAttemptState.ENDED && a.SeatClass == AgentRuntime.SeatPlan.PuppetClass);
    }

    /// <summary>
    /// (b) A CONFIGURATION IS REFUSED BY A HOME NOT MARKED PROVING. The writer, handed this assembly's own kind of database — a real
    /// home's, unmarked — refuses in words and writes nothing: no setting changed, no script written.
    /// </summary>
    [Fact]
    public void A_configuration_is_refused_by_a_home_not_marked_proving()
    {
        using var scratch = TestEnv.NewScratch("proving-b");
        var (config, why) = ProvingConfiguration.Parse(Configuration("one-agent", "seats", envelope: 7m, script: AgentScript()).ToJsonString());
        Assert.True(config is not null, why);
        using var db = new Database(Path.Combine(scratch.Dir, "real.db"));
        Assert.Equal(HomeKind.Real, db.Kind);
        var scripts = Path.Combine(scratch.Dir, "scripts");
        var touched = false;

        var refused = Assert.Throws<InvalidOperationException>(() =>
            ProvingConfigurationWriter.Apply(db, config, scripts, role => role, _ => touched = true));
        log.WriteLine(refused.Message);
        Assert.Contains("is applied only to a home marked 'proving'", refused.Message, StringComparison.Ordinal);
        Assert.False(touched, "the writer changed a setting in a real home");
        Assert.False(Directory.Exists(scripts), "the writer wrote a script for a real home");

        // A PROVING HOME TAKES IT: the envelope set, the research position's script written under its key.
        var home = ProvingHome.Create(Path.Combine(scratch.Dir, "home"));
        using var proving = new Database(home.DatabaseFile, HomeKind.Proving);
        decimal? cap = null;
        var applied = ProvingConfigurationWriter.Apply(proving, config, scripts, role => "key-" + role,
            change => { var s = new TradeAgentSettings(); change(s); cap = s.AiDailyCostCap; });
        Assert.Equal(7m, cap);
        Assert.True(File.Exists(Path.Combine(scripts, "key-research.json")));
        Assert.Equal(2, applied.Scripts.Count);
    }

    /// <summary>A configuration file of another version, or one naming a position beyond the founding chart, is refused in words.</summary>
    [Fact]
    public void A_configuration_of_another_version_or_beyond_the_founding_chart_is_refused_never_guessed()
    {
        var v2 = Configuration("x", "floor");
        v2["version"] = 2;
        var (none, why) = ProvingConfiguration.Parse(v2.ToJsonString());
        Assert.Null(none);
        Assert.Contains("format version 2, and this build reads version 1 only", why, StringComparison.Ordinal);

        var critic = Configuration("critic", "seats", script: AgentScript());
        critic["chart"]!["positions"]![0]!["id"] = "pos-critic";
        (none, why) = ProvingConfiguration.Parse(critic.ToJsonString());
        Assert.Null(none);
        Assert.Contains("founding chart's positions only", why, StringComparison.Ordinal);

        var extra = Configuration("x", "floor");
        extra["temperature"] = 1;
        (none, why) = ProvingConfiguration.Parse(extra.ToJsonString());
        Assert.Null(none);
        Assert.Contains("'temperature'", why, StringComparison.Ordinal);
    }

    /// <summary>
    /// (c) THE FLOOR FINDS THE EXPRESSIBLE PLANTED EDGES AND PASSES NOTHING ON NOISE. Its closed grammar, through the unchanged backtest
    /// and referee, PASSES a version on the planted edge's market, none on noise and none on the pre-fee trap; every trial and verdict
    /// it spent was charged by the campaigns, within their budgets; it spent no AI.
    /// </summary>
    [Fact]
    public void The_floor_finds_the_expressible_planted_edges_and_passes_nothing_on_noise()
    {
        var floor = ladder.Scores().Single(s => s.Kind == "floor");
        log.WriteLine(floor.Sentence);
        Assert.Equal((1, 1), (floor.PlantedFound, floor.PlantedExpressible));
        Assert.Equal(0, floor.FalseDiscoveries);
        Assert.Equal(0, floor.TrapsFallenFor);
        Assert.InRange(floor.TrialsSpent, 3, 3 * floor.TrialBudget);
        Assert.InRange(floor.VerdictsSpent, 1, 3 * floor.VerdictBudget);
        Assert.Equal(0m, floor.AiDollarsMetered);
        Assert.Equal(0, floor.EnvelopeRefusals);
        Assert.NotNull(floor.FirstPassSeconds);
    }

    /// <summary>
    /// (d) THE SCRIPTED AGENT'S SCORE EQUALS WHAT ITS SCRIPT DID: one hypothesis on two instruments — found 1, false 0, the pre-fee
    /// trap's verdict counted (spent, not passed), two trials and two verdicts, and its AI spend what the meter priced its usage steps at
    /// the owner's rate: the brief's turn (100,000 in at 2/M, 20,000 out at 8/M = 0.36), the turn its verdicts woke it for (0.028) and the
    /// chair's one turn on the PASS's boundary (0.028) = 0.416, well inside the envelope.
    /// </summary>
    [Fact]
    public void The_scripted_agents_score_equals_what_its_script_did()
    {
        var agent = ladder.Scores().Single(s => s.Kind == "seats");
        log.WriteLine(agent.Sentence);
        Assert.Equal((1, 1, 0, 0, 1), (agent.PlantedFound, agent.PlantedExpressible, agent.FalseDiscoveries, agent.TrapsFallenFor, agent.TrapVerdicts));
        Assert.Equal((2, 2), (agent.TrialsSpent, agent.VerdictsSpent));
        using var db = new Database(Path.Combine(ladder.Home, "configurations", "2-one-agent", "state", "tradeagent.db"), HomeKind.Proving);
        var attempts = new AiAttemptStore(db).Between(Start.AddYears(-1), DateTimeOffset.UtcNow.AddDays(1));
        Assert.Equal((2, 1), (attempts.Count(a => a.Role == CouncilRoles.Research), attempts.Count(a => a.Role == CouncilRoles.Operations)));
        Assert.All(attempts, a => Assert.Equal(AiAttemptState.ENDED, a.State));
        Assert.Equal(0.36m + 0.028m + ChairTurn, agent.AiDollarsMetered);
        Assert.Equal(0, agent.EnvelopeRefusals);
        Assert.NotNull(agent.FirstPassSeconds);
    }

    /// <summary>
    /// (e) TWO CONFIGURATIONS START FROM BYTE-IDENTICAL HOMES AT EQUAL BUDGETS: the SHA-256 over each one's copied stores is the same,
    /// and so is each budget; and a run whose configurations differ in any budget is refused before anything is seeded.
    /// </summary>
    [Fact]
    public void Two_configurations_start_from_byte_identical_homes_at_equal_budgets()
    {
        var scores = ladder.Scores();
        Assert.Equal(2, scores.Count);
        Assert.Equal(scores[0].HomeSha256, scores[1].HomeSha256);
        Assert.Equal((scores[0].TrialBudget, scores[0].VerdictBudget, scores[0].AiEnvelope, scores[0].DeadlineSeconds),
            (scores[1].TrialBudget, scores[1].VerdictBudget, scores[1].AiEnvelope, scores[1].DeadlineSeconds));

        using var scratch = TestEnv.NewScratch("proving-e");
        var (home, run) = Prepare(scratch.Dir, Configuration("floor", "floor"), Configuration("one-agent", "seats", trials: 11, script: AgentScript()));
        var refused = Host(home, Pipe(), "run", "--run", run);
        log.WriteLine(refused.ToString());
        Assert.Equal(2, refused.Code);
        Assert.Contains("do not work at equal budgets", refused.Err, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(home, "configurations")), "an unequal run was seeded and copied");
        Assert.False(File.Exists(Path.Combine(home, ProvingStore.FileName)), "an unequal run was scored");
    }

    /// <summary>
    /// (f) AN AGENT PAST ITS ENVELOPE IS REFUSED AND SCORED SO. Its brief's turn reports usage the owner's rate prices past the root's
    /// whole envelope; its verdicts wake it, and the PASS's boundary wakes the chair, and the meter admits no further turn for either —
    /// the loop launches nothing, the wakes stay unconsumed in the configuration home's own ledger — and the score counts two envelope
    /// refusals and exactly the spend the meter priced.
    /// </summary>
    [Fact]
    public void An_agent_past_its_envelope_is_refused_and_scored_so()
    {
        using var scratch = TestEnv.NewScratch("proving-f");
        // 3,000,000 in at 2/M and 1,000,000 out at 8/M = 14 against an envelope of 10.
        var (home, run) = Prepare(scratch.Dir, Configuration("spender", "seats", envelope: 10m, script: AgentScript(3_000_000, 1_000_000)));
        var ran = Host(home, Pipe(), "run", "--run", run);
        log.WriteLine(ran.ToString());
        Assert.True(ran.Code == 0, ran.ToString());
        var score = Assert.Single(Scores(home));
        Assert.Equal(2, score.EnvelopeRefusals);
        Assert.Equal(14m, score.AiDollarsMetered);   // the brief's turn alone: the turns after it were refused by the same envelope
        Assert.Equal(10m, score.AiEnvelope);

        using var db = new Database(Path.Combine(home, "configurations", "1-spender", "state", "tradeagent.db"), HomeKind.Proving);
        Assert.Single(new AiAttemptStore(db).Between(Start.AddYears(-1), DateTimeOffset.UtcNow.AddDays(1)));
    }

    /// <summary>
    /// (g) THE SCORE STORE IS READ BY NO EVIDENCE PATH (SIM-001): <see cref="ProvingStore"/>, its scorer and its truth reader are types
    /// of the proving namespace the existing scan forbids every evidence-path file to name; and outside that namespace and the host,
    /// no product source names <c>proving.db</c> or its tables.
    /// </summary>
    [Fact]
    public void The_score_store_is_read_by_no_evidence_path()
    {
        var types = typeof(ProvingGround).Assembly.GetTypes().Where(t => t.Namespace == typeof(ProvingGround).Namespace).Select(t => t.Name).ToHashSet();
        foreach (var t in new[] { nameof(ProvingStore), nameof(ProvingScorer), nameof(MarketTruth), nameof(ProvingScore), nameof(ProvingConfigurationWriter) })
            Assert.Contains(t, types);

        var root = RepoRoot();
        var proving = Path.Combine(root, "src", "TradeAgent.Core", "Proving") + Path.DirectorySeparatorChar;
        var named = new Regex(@"proving\.db|proving_score|proving_run|proving_meta");
        var found = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(proving, StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => named.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f)).ToList();
        Assert.Empty(found);
    }

    /// <summary>
    /// ITEM 1: THE PACKAGING SCRIPTS' OUTPUT CONTAINS NO PROVING HOST OR PUPPET FILE. Every project a packaging script publishes is
    /// under src/, and none of them reaches a tests/ project through its references, however deep — so neither program can be in a
    /// stage those publishes write.
    /// </summary>
    [Fact]
    public void The_packaging_scripts_publish_no_proving_host_or_puppet()
    {
        var root = RepoRoot();
        var published = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var script in new[] { "packaging/build.ps1", "packaging/build-linux.sh", "tools/mac-bundle.sh", "tools/linux-host.sh" }
                     .Where(s => File.Exists(Path.Combine(root, s))))
            foreach (Match m in Regex.Matches(File.ReadAllText(Path.Combine(root, script)), @"[\w./\\-]+\.csproj"))
                published.Add(m.Value.Replace('\\', '/').TrimStart('.', '/'));
        Assert.NotEmpty(published);
        Assert.Contains("src/TradeAgent.App/TradeAgent.App.csproj", published);

        foreach (var project in published)
        {
            Assert.StartsWith("src/", project, StringComparison.Ordinal);
            foreach (var reached in Closure(Path.Combine(root, project)))
            {
                var rel = Path.GetRelativePath(root, reached).Replace('\\', '/');
                Assert.False(rel.StartsWith("tests/", StringComparison.Ordinal), $"{project} reaches {rel}");
                Assert.DoesNotContain("ProvingHost", rel, StringComparison.Ordinal);
                Assert.DoesNotContain("Puppet", rel, StringComparison.Ordinal);
            }
        }
        log.WriteLine("published: " + string.Join(", ", published));
    }

    /// <summary>
    /// ITEM 5: ONE LIFE OF THE LADDER — the floor and one agent at equal budgets on the same three markets — printed, both rows, for
    /// this platform's report.
    /// </summary>
    [Fact]
    public void One_life_of_the_ladder_prints_both_score_rows()
    {
        log.WriteLine(ladder.Ran.Out);
        var scores = ladder.Scores();
        Assert.Equal(["floor", "seats"], scores.Select(s => s.Kind));
        foreach (var s in scores)
        {
            Assert.StartsWith(ProvingScore.Label, s.Sentence, StringComparison.Ordinal);
            log.WriteLine($"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}: {s.Sentence}");
        }
    }

    static IEnumerable<string> Closure(string csproj)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var todo = new Stack<string>([Path.GetFullPath(csproj)]);
        while (todo.Count > 0)
        {
            var p = todo.Pop();
            if (!seen.Add(p)) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(p), @"<ProjectReference\s+Include=""([^""]+)"""))
                todo.Push(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(p)!, m.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar))));
        }
        return seen;
    }

    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "src"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("could not find the repository root");
    }
}

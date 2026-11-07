using System.Text.Json;
using System.Text.Json.Nodes;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Core.Proving;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE REST OF THE FIRST LADDER, AS PUPPET CONFIGURATIONS (<c>U-proving-ground-d</c> item 1; ORCHESTRATION § 2.14: "a deterministic search
/// with no model as the floor, one agent, an agent with a critic, a headed team — at equal budgets on the same markets"). Each richer chart
/// is chartered and hired through the kernel's own writers in its configuration's home; every seat is a puppet whose turns are its
/// script. What a score says is what the script did, through the real kernel — SIMULATED, plumbing, never intelligence.
/// </summary>
public partial class ProvingHostTests
{
    /// <summary>The AI envelope every configuration of the ladder works under.</summary>
    internal const decimal Envelope = 100m;

    /// <summary>The verdict class a PASS on a proving market gets, as the referee writes it there.</summary>
    internal const string Pass = PromotionVerdict.PaperEligible;

    /// <summary>A value as one word of a puppet step: JSON with its quotes kept.</summary>
    internal static string Word(object value) => JsonSerializer.Serialize(value).Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>A date every forecast of a run resolves by: well after any verdict, well inside the vocabulary's horizon.</summary>
    static string By() => DateTimeOffset.UtcNow.AddDays(30).ToString("O");

    /// <summary>
    /// A ROUTINE DECISION WITH A FORECAST PER PROGRAM (<c>trade decision record … --forecasts</c>): that each program's version gets the PASS
    /// class, at the stated probability — written before its verdict is asked, so the app's resolver settles it from the referee's record.
    /// </summary>
    internal static string Judging(string choice, params (string Program, decimal P)[] forecasts) =>
        $"trade decision record \"{choice}\" \"the backtests before the cutoff are what I can judge by\" --options \"{Word(new[] { choice, "spend no verdict" })}\" --forecasts "
        + Word(forecasts.Select(f => new { predicate = ForecastVocabulary.VerdictClass, subject = VersionOf(f.Program), target = Pass, by = By(), probability = f.P }));

    /// <summary>A report-shaped task mandate to <paramref name="holder"/> (a template token), due in a week, with a small allocation.</summary>
    static string Assign(string holder, string goal) =>
        $"trade mandate assign {{position:{holder}}} \"{goal}\" --kind {MandateKind.Task} --shape {MandateShape.Report} --due {DateTimeOffset.UtcNow.AddDays(7):O} --allocation 5";

    /// <summary>The mandate this turn's wake names, as the seat was told it.</summary>
    const string MyMandate = "{prompt:(man-[0-9a-f]+)}";

    static string[] Ack(string what) => ["usage 2000 0 200", $"final {what}"];

    static JsonObject Seat(string id, string unit, string[][] script, bool head = false)
    {
        var o = new JsonObject
        {
            ["id"] = id, ["unit"] = unit,
            ["script"] = new JsonArray([.. script.Select(t => (JsonNode?)new JsonArray([.. t.Select(x => (JsonNode?)x)]))])
        };
        if (head) o["head"] = true;
        return o;
    }

    static JsonObject Chart(string name, JsonArray charters, params JsonObject[] positions) => new()
    {
        ["version"] = ProvingConfiguration.Version, ["name"] = name, ["kind"] = "seats",
        ["chart"] = new JsonObject
        {
            ["units"] = new JsonArray(OrgIds.Root, OrgIds.Research, OrgIds.Operations),
            ["charters"] = charters,
            ["positions"] = new JsonArray([.. positions])
        },
        ["ai_envelope"] = Envelope, ["trial_budget"] = 10, ["verdict_budget"] = 2, ["deadline_seconds"] = 900
    };

    static JsonObject Team(string id, decimal? verdictShare = null)
    {
        var o = new JsonObject { ["id"] = id, ["kind"] = OrgUnitKind.Team, ["parent"] = OrgIds.Research, ["ai_share"] = OrgBounds.ExperimentMaxShare };
        if (verdictShare is { } v) o["verdict_share"] = v;
        return o;
    }

    /// <summary>
    /// AN AGENT WITH A CRITIC. The research position tests the same two candidates the one agent does, then — before spending a verdict —
    /// gives its critic, a position in a review team it chartered, a mandate to review them. The critic re-runs the trap's candidate and
    /// delivers that it does not clear the venue's cost; the agent, woken by the delivery, forecasts and asks a verdict on the edge's
    /// candidate alone.
    /// </summary>
    internal static JsonObject CriticConfiguration()
    {
        var edge = VersionOf(Program("SYNA"));
        var trap = VersionOf(Program("SYNC"));
        string[][] agent =
        [
            Ack("I read my critic's activation."),
            [
                "write strategies/edge.strategy " + Program("SYNA"),
                $"trade backtest --strategy strategies/edge.strategy --dataset {EdgeDataset} --from {ResearchFrom(0)} --to {ResearchTo(0)}",
                "write strategies/trap.strategy " + Program("SYNC"),
                $"trade backtest --strategy strategies/trap.strategy --dataset {TrapDataset} --from {ResearchFrom(TrapDay)} --to {ResearchTo(TrapDay)}",
                Assign("critic", $"review my two candidates before I spend a verdict: {edge} on dataset {EdgeDataset} and {trap} on dataset {TrapDataset}"),
                "usage 100000 0 20000",
                "final I measured one hypothesis on two instruments and asked my critic to review both."
            ],
            [
                Judging("ask a verdict on the SYNA candidate only", (Program("SYNA"), 0.7m)),
                $"trade verdict --version {edge} --dataset {EdgeDataset}",
                "usage 10000 0 1000",
                "final My critic found the SYNC candidate's edge does not clear costs; I asked one verdict."
            ],
            ["usage 10000 0 1000", "final I read the verdict."]
        ];
        string[][] critic =
        [
            Ack("I read my activation."),
            [
                "write strategies/trap.strategy " + Program("SYNC"),
                $"trade backtest --strategy strategies/trap.strategy --dataset {TrapDataset} --from {ResearchFrom(TrapDay)} --to {ResearchTo(TrapDay)}",
                $"trade mandate deliver {MyMandate} \"the SYNA candidate holds; the SYNC candidate's net is gone at the venue's cost, so it deserves no verdict\"",
                "usage 20000 0 4000",
                "final I reviewed both and delivered."
            ],
            Ack("Nothing more to review.")
        ];
        return Chart("critic", [Team("review")],
            Seat(CouncilRoles.Research, OrgIds.Research, agent),
            Seat("critic", "review", critic),
            Seat(CouncilRoles.Operations, OrgIds.Operations, ChairScript));
    }

    /// <summary>
    /// A HEADED TEAM: a head and two executors in a team the research position chartered. The brief goes to the head, which gives each
    /// executor a mandate — the edge's market to one, the trap's to the other. Each tests its candidate, forecasts its verdict, asks it
    /// and delivers. The first executor also tests a market no mandate named (the off-path exam), forecasts and asks its verdict, and
    /// PROPOSES the finding to its head citing the version — so the finding outlives its context.
    /// </summary>
    internal static JsonObject HeadedConfiguration()
    {
        var edge = VersionOf(Program("SYNA"));
        var trap = VersionOf(Program("SYNC"));
        var off = VersionOf(Program("SYND"));
        string[][] head =
        [
            Ack("I read my activation."),
            [
                Assign("exec-1", $"test whether the bare signal leads SYNA's next hour on dataset {EdgeDataset}, and ask its verdict if it does"),
                Assign("exec-2", $"test whether the bare signal leads SYNC's next hour on dataset {TrapDataset}, and ask its verdict if it does"),
                "usage 50000 0 10000",
                "final I split the brief between my two executors."
            ],
            ["usage 10000 0 1000", "final I read what my team sent."]
        ];
        string[][] first =
        [
            Ack("I read my activation."),
            [
                "write strategies/edge.strategy " + Program("SYNA"),
                $"trade backtest --strategy strategies/edge.strategy --dataset {EdgeDataset} --from {ResearchFrom(0)} --to {ResearchTo(0)}",
                Judging("ask the SYNA candidate's verdict", (Program("SYNA"), 0.7m)),
                $"trade verdict --version {edge} --dataset {EdgeDataset}",
                "write strategies/off.strategy " + Program("SYND"),
                $"trade backtest --strategy strategies/off.strategy --dataset {OffPathDataset} --from {ResearchFrom(OffPathDay)} --to {ResearchTo(OffPathDay)}",
                Judging("ask the SYND candidate's verdict, beside my mandate", (Program("SYND"), 0.6m)),
                $"trade verdict --version {off} --dataset {OffPathDataset}",
                $"trade mandate propose {MyMandate} \"beside my mandate: the bare signal leads SYND's next hour too, and I asked its verdict\" --refs "
                    + Word(new[] { new { kind = OrgRefKind.Version, id = off } }),
                $"trade mandate deliver {MyMandate} \"the SYNA candidate clears the venue's cost before the cutoff; I asked its verdict\"",
                "usage 60000 0 12000",
                "final I tested my market, and one beside it."
            ],
            ["usage 10000 0 1000", "final I read my verdicts."]
        ];
        string[][] second =
        [
            Ack("I read my activation."),
            [
                "write strategies/trap.strategy " + Program("SYNC"),
                $"trade backtest --strategy strategies/trap.strategy --dataset {TrapDataset} --from {ResearchFrom(TrapDay)} --to {ResearchTo(TrapDay)}",
                Judging("ask the SYNC candidate's verdict", (Program("SYNC"), 0.3m)),
                $"trade verdict --version {trap} --dataset {TrapDataset}",
                $"trade mandate deliver {MyMandate} \"the SYNC candidate barely clears its cost before the cutoff; I asked its verdict\"",
                "usage 40000 0 8000",
                "final I tested my market."
            ],
            ["usage 10000 0 1000", "final I read my verdict."]
        ];
        return Chart("headed", [Team("desk", verdictShare: 0.5m)],
            Seat("head", "desk", head, head: true),
            Seat("exec-1", "desk", first),
            Seat("exec-2", "desk", second),
            Seat(CouncilRoles.Research, OrgIds.Research, [Ack("I read my team's activations."), ["usage 2000 0 200", "final Nothing for me."]]),
            Seat(CouncilRoles.Operations, OrgIds.Operations, ChairScript));
    }
}

public partial class ProvingHostTests
{
    /// <summary>A configuration's home in the ladder's run.</summary>
    Database HomeOf(string folder) => new(Path.Combine(TheLadder.Home, "configurations", folder, "state", "tradeagent.db"), HomeKind.Proving);

    static List<AiAttempt> AttemptsIn(Database db) => [.. new AiAttemptStore(db).Between(Start.AddYears(-1), DateTimeOffset.UtcNow.AddDays(1))];

    /// <summary>The <c>org_event</c> rows of <paramref name="kind"/>: the position or unit each names and who decided it.</summary>
    static List<(string Unit, string? Position, string Decider)> Events(Database db, string kind) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT unit_id, position_id, decider FROM org_event WHERE kind=$k ORDER BY id", ("$k", kind));
        using var r = c.ExecuteReader();
        var list = new List<(string, string?, string)>();
        while (r.Read()) list.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2)));
        return list;
    });

    /// <summary>
    /// (h) AN AGENT WITH A CRITIC, CHARTERED AND RUN THROUGH THE KERNEL (<c>U-proving-ground-d</c> item 1). In its configuration's home the
    /// research position chartered the review team — a <c>charter</c> decision, a <c>chartered</c> event it decided — hired the critic into
    /// it, and the owner's press activated it: no row seeded. The agent's mandate to its critic was delivered, and the agent, woken by the
    /// delivery, spent ONE verdict — on the edge's candidate, which PASSED — where the one agent spent two: the trap's verdict was saved.
    /// Its spend is every seat's turns, the critic's and its activation's included, at the owner's rate.
    /// </summary>
    [Fact]
    public void An_agent_with_a_critic_is_chartered_through_the_kernel_and_spends_one_verdict_where_one_agent_spends_two()
    {
        var critic = TheLadder.Scores().Single(s => s.Configuration == "critic");
        Log.WriteLine(critic.Sentence);
        Assert.Equal((1, 1, 0, 0, 0), (critic.PlantedFound, critic.PlantedExpressible, critic.FalseDiscoveries, critic.TrapsFallenFor, critic.TrapVerdicts));
        Assert.Equal((2, 1), (critic.TrialsSpent, critic.VerdictsSpent));
        Assert.Equal(0, critic.EnvelopeRefusals);

        using var db = HomeOf("3-critic");
        var org = new OrgStore(db);
        var chartered = Assert.Single(Events(db, OrgEventKind.Chartered));
        Assert.Equal(CouncilRoles.Research, chartered.Decider);
        var team = org.Units().Single(u => u.Id == chartered.Unit);
        Assert.Equal((OrgUnitKind.Team, OrgIds.Research), (team.Kind, team.ParentId));
        var hired = Assert.Single(Events(db, OrgEventKind.Hired));
        Assert.Equal((team.Id, CouncilRoles.Research), (hired.Unit, hired.Decider));
        Assert.Equal(OrgDecider.Owner, Assert.Single(Events(db, OrgEventKind.Activated)).Decider);
        var seat = hired.Position!;
        Assert.Equal(OrgPositionStatus.Active, org.Position(seat)!.Status);

        var mandate = Assert.Single(org.MandatesOf(seat));
        Assert.Equal((CouncilRoles.Research, seat), (mandate.Decider, mandate.Holder));
        Assert.Single(org.Deliveries(mandate.Id));

        var attempts = AttemptsIn(db);
        Assert.All(attempts, a => Assert.Equal(AiAttemptState.ENDED, a.State));
        Assert.Equal(2, attempts.Count(a => a.Role == seat));
        Assert.True(attempts.Count(a => a.Role == CouncilRoles.Research) >= 3, "the agent did not turn for its activation, the brief and the delivery");
        Assert.Equal(attempts.Sum(a => a.Cost!.Value), critic.AiDollarsMetered);

        // ITS ONE FORECAST: 0.7 on the PASS — Brier 0.09 against the base rate's 0.25.
        Assert.Equal((1, 1), (critic.Calibration.Scored, critic.Calibration.Hits));
        Assert.Equal(0.09, critic.Calibration.Brier!.Value, 9);
        Assert.Equal(0.25, critic.Calibration.BaseBrier!.Value, 9);
    }

    /// <summary>
    /// (i) A HEADED TEAM, CHARTERED AND RUN THROUGH THE KERNEL. The research position chartered the desk with a verdict share, hired its head
    /// and two executors (the head appointed), the owner's press activated each. The brief went to the head, which gave each executor a
    /// mandate; each delivered, and asked its own verdict under the desk's share: the edge's PASSED, the trap's did not.
    /// </summary>
    [Fact]
    public void A_headed_team_is_chartered_through_the_kernel_and_its_head_splits_the_brief_between_two_executors()
    {
        var headed = TheLadder.Scores().Single(s => s.Configuration == "headed");
        Log.WriteLine(headed.Sentence);
        Assert.Equal((1, 1, 0, 0, 1), (headed.PlantedFound, headed.PlantedExpressible, headed.FalseDiscoveries, headed.TrapsFallenFor, headed.TrapVerdicts));
        Assert.Equal((3, 3), (headed.TrialsSpent, headed.VerdictsSpent));
        Assert.Equal(0, headed.EnvelopeRefusals);

        using var db = HomeOf("4-headed");
        var org = new OrgStore(db);
        var team = org.Units().Single(u => u.Id == Assert.Single(Events(db, OrgEventKind.Chartered)).Unit);
        Assert.Equal(0.5m, decimal.Parse(team.VerdictShare!, System.Globalization.CultureInfo.InvariantCulture));
        var hires = Events(db, OrgEventKind.Hired);
        Assert.Equal(3, hires.Count);
        Assert.All(hires, h => Assert.Equal((team.Id, CouncilRoles.Research), (h.Unit, h.Decider)));
        Assert.Equal(3, Events(db, OrgEventKind.Activated).Count(e => e.Decider == OrgDecider.Owner));
        var head = team.HeadPositionId!;
        var executors = hires.Select(h => h.Position!).Where(p => p != head).ToList();
        Assert.Equal(2, executors.Count);
        foreach (var executor in executors)
        {
            var mandate = Assert.Single(org.MandatesOf(executor));
            Assert.Equal((head, executor), (mandate.Decider, mandate.Holder));
            Assert.Single(org.Deliveries(mandate.Id));
        }
        var verdictsBy = db.Read(_ =>
        {
            using var c = db.Cmd("SELECT r.role FROM strategy_promotion p JOIN strategy_run r ON r.version_id = p.version_id WHERE r.role <> 'referee' GROUP BY p.id, r.role");
            using var r = c.ExecuteReader();
            var roles = new List<string>();
            while (r.Read()) roles.Add(r.GetString(0));
            return roles;
        });
        Assert.All(verdictsBy, role => Assert.Contains(role, executors));

        var attempts = AttemptsIn(db);
        Assert.All(attempts, a => Assert.Equal(AiAttemptState.ENDED, a.State));
        Assert.Equal(attempts.Sum(a => a.Cost!.Value), headed.AiDollarsMetered);
        Assert.All(executors.Append(head), p => Assert.True(attempts.Count(a => a.Role == p) >= 2, $"{p} did not turn for its activation and its work"));

        // THREE FORECASTS: 0.7 on SYNA (yes), 0.6 on the off-path SYND (yes), 0.3 on the trap (no) — Brier (0.09 + 0.16 + 0.09) / 3.
        Assert.Equal((3, 2), (headed.Calibration.Scored, headed.Calibration.Hits));
        Assert.Equal(0.34 / 3, headed.Calibration.Brier!.Value, 9);
        Assert.True(headed.Calibration.Brier < headed.Calibration.BaseBrier, headed.Calibration.Words);
    }

    /// <summary>
    /// (j) THE OFF-PATH EXAM (<c>U-proving-ground-d</c> item 3; VISION § 1 test 9, § 6.4). A market no brief names holds a planted edge. The
    /// headed team's first executor tested it beside its mandate, got a PASS, and PROPOSED the finding to its head citing the version —
    /// a claim that woke its decider once — so it scores found and reported. The floor's search reaches every campaign and finds it, but
    /// tells no one; the one agent and the agent with a critic never looked. None of it counts among the brief's planted edges.
    /// </summary>
    [Fact]
    public void The_off_path_exam_scores_an_executor_that_finds_and_reports_an_edge_its_mandate_did_not_ask_for()
    {
        var scores = TheLadder.Scores().ToDictionary(s => s.Configuration);
        foreach (var s in scores.Values) Log.WriteLine(s.Sentence);
        Assert.All(scores.Values, s => Assert.Equal(1, s.OffPathMarkets));
        Assert.Equal((1, 0), (scores["floor"].OffPathFound, scores["floor"].OffPathReported));
        Assert.Equal((0, 0), (scores["one-agent"].OffPathFound, scores["one-agent"].OffPathReported));
        Assert.Equal((0, 0), (scores["critic"].OffPathFound, scores["critic"].OffPathReported));
        Assert.Equal((1, 1), (scores["headed"].OffPathFound, scores["headed"].OffPathReported));
        Assert.All(scores.Values, s => Assert.Equal(1, s.PlantedExpressible));

        using var db = HomeOf("4-headed");
        var org = new OrgStore(db);
        var off = VersionOf(Program("SYND"));
        var proposal = org.MandatesOpen().Rows.SelectMany(m => org.MandateEvents(m.Mandate.Id)).Concat(
                org.Positions().SelectMany(p => org.MandatesOf(p.Id)).SelectMany(m => org.MandateEvents(m.Id)))
            .First(e => e.Kind == MandateEventKind.Proposed);
        Assert.Contains($"{OrgRefKind.Version}:{off}", proposal.Evidence);
        using var json = System.Text.Json.JsonDocument.Parse(scores["headed"].Sources);
        Assert.Single(json.RootElement.GetProperty("off_path").GetProperty("reports").EnumerateArray());
    }

    /// <summary>
    /// (k) CALIBRATION IS THE APP'S OWN SCORE, RECORDED AS A MEASUREMENT (<c>U-proving-ground-d</c> item 2). Each configuration's row in
    /// proving.db carries the sums the app's scorecards compute at read in that configuration's home — every position's forecasts resolved
    /// by the app's resolver from the referee's records, each beside its base rate — and the row says SIMULATED by the file's CHECK.
    /// </summary>
    [Fact]
    public void Each_configurations_calibration_in_proving_db_is_the_apps_own_scorecard_beside_its_base_rate()
    {
        foreach (var (score, folder) in TheLadder.Scores().Zip(new[] { "1-floor", "2-one-agent", "3-critic", "4-headed" }))
        {
            using var db = HomeOf(folder);
            var cards = new OrgStore(db).Positions().Select(p => new Scorecards(db).Of(p.Id)).ToList();
            var scored = cards.Sum(c => c.Total.Scored);
            Assert.Equal(scored, score.Calibration.Scored);
            Assert.Equal(cards.Sum(c => c.Total.BrierSum), score.Calibration.BrierSum, 9);
            Assert.Equal(cards.Sum(c => c.Total.BaseBrierSum), score.Calibration.BaseBrierSum, 9);
            Assert.Equal(cards.Sum(c => c.Scores.Count(x => x.Outcome)), score.Calibration.Hits);
            Log.WriteLine($"{score.Configuration}: calibration {score.Calibration.Words}");
        }
        using var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(TheLadder.Home, ProvingStore.FileName)};Mode=ReadOnly;Pooling=False");
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM proving_score WHERE label <> 'SIMULATED' OR forecasts_scored IS NULL";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }

    /// <summary>
    /// (l) A RICHER CHART SPENDS FROM THE SAME ENVELOPE (BUDGET-001). A headed team whose head's brief turn reports usage the owner's rate
    /// prices past its team's share of the envelope: the meter admits no further turn for the team, so both executors' mandate wakes stay
    /// unconsumed in the configuration home's own ledger — two envelope refusals, of CHARTERED positions, counted in the score — and the
    /// spend is exactly what the meter priced.
    /// </summary>
    [Fact]
    public void A_headed_team_past_its_envelope_is_refused_for_its_chartered_seats_and_scored_so()
    {
        using var scratch = TestEnv.NewScratch("proving-l");
        var spender = HeadedConfiguration();
        spender["name"] = "headed-spender";
        // 3,000,000 in at 2/M and 1,000,000 out at 8/M = 14, past the team's share of the envelope.
        spender["chart"]!["positions"]![0]!["script"]![1]![2] = "usage 3000000 0 1000000";
        var (home, run) = Prepare(scratch.Dir, spender);
        var ran = Host(home, Pipe(), "run", "--run", run);
        Log.WriteLine(ran.ToString());
        Assert.True(ran.Code == 0, ran.ToString());
        var score = Assert.Single(Scores(home));
        Log.WriteLine(score.Sentence);
        Log.WriteLine(File.ReadAllText(Path.Combine(home, "configurations", "1-headed-spender", "apply.out.txt")));

        using var db = new Database(Path.Combine(home, "configurations", "1-headed-spender", "state", "tradeagent.db"), HomeKind.Proving);
        var org = new OrgStore(db);
        var team = org.Units().Single(u => u.Id == Assert.Single(Events(db, OrgEventKind.Chartered)).Unit);
        var executors = org.Positions().Where(p => p.UnitId == team.Id && p.Id != team.HeadPositionId).Select(p => p.Id).ToList();
        Assert.Equal(2, executors.Count);
        db.Read(_ => { using var c = db.Cmd("SELECT * FROM mission_event ORDER BY created_at"); using var r = c.ExecuteReader(); while (r.Read()) Log.WriteLine("DEBUGEV " + string.Join(" | ", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "null" : r.GetValue(i).ToString()))); return 0; });   // DEBUG-REMOVE
        using var sources = System.Text.Json.JsonDocument.Parse(score.Sources);
        var refusedRoles = sources.RootElement.GetProperty("refused").EnumerateArray().Select(r => r.GetProperty("role").GetString()!).ToList();
        Assert.Equal(executors.Order(), refusedRoles.Where(executors.Contains).Order());
        Assert.True(score.EnvelopeRefusals >= 2, score.Sentence);
        var attempts = AttemptsIn(db);
        Assert.All(executors, e => Assert.Equal(1, attempts.Count(a => a.Role == e)));   // their activation's turn alone
        Assert.Equal(attempts.Sum(a => a.Cost!.Value), score.AiDollarsMetered);
        Assert.Equal(0, score.VerdictsSpent);
    }
}

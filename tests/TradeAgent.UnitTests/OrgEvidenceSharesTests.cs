using System.Globalization;
using System.Text;
using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// EVERY UNIT'S ENVELOPE IN THREE DIMENSIONS (<c>U-org-verbs-e</c>, O6 part e; ORCHESTRATION § 2.4; ORGANISATION § 3): beside its AI
/// share, a unit's TRIAL and VERDICT shares — fractions of a campaign's own budgets — are charged at the call that spends them
/// (<see cref="CampaignStore.RegisterTrial"/>, <see cref="CampaignStore.ChargeVerdict"/>), against the chain the launch was RECORDED
/// under (<c>strategy_run.attempt</c> / <c>strategy_verdict.attempt_id</c> ⋈ <c>ai_attempt_funding</c>), AFTER the campaign's own
/// gates, which run first and unchanged (EVID-001). Store-level: real ledger rows, no model, no pipe.
/// </summary>
public class OrgEvidenceSharesTests
{
    static readonly DateTimeOffset At = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Cutoff = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A team under <c>div-research</c>, as the charter verb would leave it, with the shares given.</summary>
    const string Team = "unit-evidence-team";

    static void GivenTeam(Database db, string? trialShare, string? verdictShare) => db.Write(_ =>
    {
        using var c = db.Cmd("""
            INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, envelope_share, created_at, trial_share, verdict_share)
            VALUES($id, $parent, 'team', NULL, 'active', '0.5', $at, $t, $v)
            """, ("$id", Team), ("$parent", OrgIds.Research), ("$at", Sql.T(At)), ("$t", trialShare), ("$v", verdictShare));
        return c.ExecuteNonQuery();
    });

    /// <summary>A launch admitted under <paramref name="funding"/> — its chain recorded with it, as admission records it.</summary>
    static string Attempt(Database db, params string[] funding)
    {
        var id = $"turn-{Guid.NewGuid():n}"[..17];
        Assert.True(new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = id, StartedAt = At, Role = CouncilRoles.Research, ReservedCost = 0m, SeatClass = SeatClasses.Harness,
            BillingClass = BillingClasses.Key, Funding = funding
        }).Admitted);
        return id;
    }

    internal static DatasetRecord Held(Database db, string sha = "aa11")
    {
        var store = new DatasetStore(db);
        var id = store.Record(new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, sha == "aa11" ? "v1" : "v-" + sha, 12, 12, [],
            Path.Combine(Paths.Data, $"not-read-here-{Guid.NewGuid():n}.csv"), sha, 1000,
            Cutoff.AddDays(-300), Cutoff.AddDays(60), 0, [], false, 0, 0, 0, At,
            DatasetState.ACCEPTED, null, []));
        Assert.True(store.SetHoldout(id, Cutoff, EvaluationClass.Research).Ok);
        return store.ById(id)!;
    }

    internal static CampaignRow Opened(Database db, DatasetRecord set, int trials, int verdicts)
    {
        var opened = new CampaignStore(db).Open("BTCUSDT 1m v1", set, trials, verdicts, At);
        Assert.True(opened.Ok, opened.Why);
        return opened.Campaign!;
    }

    internal static string Version(Database db, string id)
    {
        new StrategyStore(db).RecordVersion(new StrategyVersionRow(
            id, "instrument BTCUSDT\n", "canonical", "{}", StrategyStore.InterpreterBuild,
            ParseVerdict.Accepted, 0, At, CouncilRoles.Research, "attempt-version"));
        return id;
    }

    /// <summary>A run of <paramref name="version"/> made by the launch <paramref name="attempt"/> — the run row carries it.</summary>
    internal static string Run(Database db, DatasetRecord set, string version, string runId, string attempt)
    {
        new StrategyStore(db).RecordRun(new StrategyRunRow(
            runId, version, set.Id, set.NormalisedSha256, Cutoff.AddDays(-10), Cutoff,
            "frictionless", BacktestOutcome.COMPLETED.ToString(), null,
            10, 1, 1, 1, 1, 1, 0, 0, 1m, 0m, 1m, 0m, "sha", At, CouncilRoles.Research, attempt), []);
        return runId;
    }

    // ---- item 2: trials --------------------------------------------------------------------------------------------

    /// <summary>
    /// A UNIT'S TRIALS RUN OUT BEFORE THE CAMPAIGN'S, AND THE CAMPAIGN'S GATE RUNS FIRST. The team holds 0.5 of a 4-trial campaign:
    /// its launches' third run is refused in words naming the team, its share, its use and the campaign's budget while the campaign
    /// still has room, and a launch funded outside the team is not touched by it. Once the CAMPAIGN is spent, the team's next run is
    /// refused in the campaign's own words — its gate ran first, unchanged. RED at base: the third run was registered. The watched
    /// mutant (the unit gate before the campaign's) answers the last refusal in the unit's words.
    /// </summary>
    [Fact]
    public void A_units_trials_run_out_before_the_campaigns_and_the_campaign_gate_runs_first()
    {
        using var db = TestEnv.NewDb();
        var campaigns = new CampaignStore(db);
        var set = Held(db);
        var campaign = Opened(db, set, trials: 4, verdicts: 2);
        GivenTeam(db, trialShare: "0.5", verdictShare: null);
        var team = Attempt(db, Team, OrgIds.Research, OrgIds.Root);
        var outside = Attempt(db, OrgIds.Research, OrgIds.Root);
        var version = Version(db, "version-trials");

        TrialRegistered Register(string runId, string attempt) =>
            campaigns.RegisterTrial(campaign.Id, version, Run(db, set, version, runId, attempt), EvaluationClass.Research, At);

        Assert.True(Register("run-t0", team).Ok);
        Assert.True(Register("run-t1", team).Ok);

        // THE TEAM'S SHARE IS SPENT, THE CAMPAIGN'S IS NOT.
        var spent = Register("run-t2", team);
        Assert.False(spent.Ok, "a third trial was registered past the team's half of a 4-trial campaign");
        foreach (var words in new[] { Team, "0.5", "2 of", $"{campaign.TrialBudget}" })
            Assert.Contains(words, spent.Why, StringComparison.Ordinal);
        Assert.False(campaigns.Registered(campaign.Id, version, "run-t2"));
        Assert.Equal(2, campaigns.TrialsCharged(campaign.Id));

        // A LAUNCH FUNDED OUTSIDE THE TEAM SPENDS THE CAMPAIGN'S ROOM, AND NOTHING OF THE TEAM'S.
        Assert.True(Register("run-o0", outside).Ok);
        Assert.True(Register("run-o1", outside).Ok);
        Assert.Equal(4, campaigns.TrialsCharged(campaign.Id));

        // THE CAMPAIGN IS SPENT: ITS OWN GATE ANSWERS FIRST, in its own words, never the unit's.
        var full = Register("run-t3", team);
        Assert.False(full.Ok);
        Assert.Contains($"has registered all {campaign.TrialBudget} of its research trials", full.Why, StringComparison.Ordinal);
        Assert.DoesNotContain(Team, full.Why, StringComparison.Ordinal);

        // A FIXTURE RUN IS STILL FREE, the team's included.
        Assert.True(campaigns.RegisterTrial(campaign.Id, version, Run(db, set, version, "run-f0", team), EvaluationClass.Fixture, At).Ok);
    }
    /// <summary>
    /// FIX PASS (the Codex review's P1 on <c>RegisterTrial</c>): A UNIT'S SHARE IS GATED IN EVERY CAMPAIGN THE INSERT CHARGES. The
    /// family was first charged to campaign A, so a run of it over campaign B's dataset is charged to A as well: once the team has
    /// spent its half of A, that run is refused in A's words for the team, and A is charged nothing more. RED before the fix: only
    /// B, the run's own campaign, was gated, and the run was registered — a third trial against A past the team's two.
    /// </summary>
    [Fact]
    public void A_units_share_is_gated_in_the_home_campaign_the_insert_also_charges()
    {
        using var db = TestEnv.NewDb();
        var campaigns = new CampaignStore(db);
        var (setA, setB) = (Held(db, "aa11"), Held(db, "bb22"));
        var a = Opened(db, setA, trials: 4, verdicts: 2);
        var b = Opened(db, setB, trials: 10, verdicts: 2);
        GivenTeam(db, trialShare: "0.5", verdictShare: null);
        var team = Attempt(db, Team, OrgIds.Research, OrgIds.Root);
        var version = Version(db, "version-home");

        Assert.True(campaigns.RegisterTrial(a.Id, version, Run(db, setA, version, "run-h0", team), EvaluationClass.Research, At).Ok);
        Assert.True(campaigns.RegisterTrial(a.Id, version, Run(db, setA, version, "run-h1", team), EvaluationClass.Research, At).Ok);

        var elsewhere = campaigns.RegisterTrial(b.Id, version, Run(db, setB, version, "run-h2", team), EvaluationClass.Research, At);
        Assert.False(elsewhere.Ok, "a run charged to the team's spent home campaign was registered through another campaign");
        Assert.Contains(Team, elsewhere.Why, StringComparison.Ordinal);
        Assert.Contains($"campaign {a.Id}'s", elsewhere.Why, StringComparison.Ordinal);
        Assert.Equal(2, campaigns.TrialsCharged(a.Id));
        Assert.Equal(0, campaigns.TrialsCharged(b.Id));
    }

    /// <summary>
    /// FIX PASS (the Codex review's P1 on <c>ChargeVerdict</c>): A UNIT'S VERDICTS ARE COUNTED AS THE CAMPAIGN COUNTS ITS OWN — every
    /// judgement over the same held-back months, any campaign's — so switching campaigns never replenishes a delegated count. Two
    /// campaigns over the same months, 4 judgements each, the team a quarter: one judgement under A, and the second under B is refused.
    /// RED before the fix: only A's renewal lineage was counted, and B charged it.
    /// </summary>
    [Fact]
    public void A_units_verdicts_are_counted_across_campaigns_over_the_same_months()
    {
        using var db = TestEnv.NewDb();
        var campaigns = new CampaignStore(db);
        var a = Opened(db, Held(db, "aa11"), trials: 10, verdicts: 4);
        var b = Opened(db, Held(db, "bb22"), trials: 10, verdicts: 4);
        GivenTeam(db, trialShare: null, verdictShare: "0.25");
        var team = Attempt(db, Team, OrgIds.Research, OrgIds.Root);

        Assert.True(campaigns.ChargeVerdict(a.Id, Version(db, "version-over-a"), At, team).Ok);
        var switched = campaigns.ChargeVerdict(b.Id, Version(db, "version-over-b"), At, team);
        Assert.False(switched.Ok, "a second campaign over the same months replenished the team's delegated judgement");
        Assert.Contains(Team, switched.Why, StringComparison.Ordinal);
        Assert.False(campaigns.VerdictCharged(b.Id, "version-over-b"));
    }

    // ---- item 3: verdicts ------------------------------------------------------------------------------------------

    static string? AskedBy(Database db, long campaign, string version) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT attempt_id FROM strategy_verdict WHERE campaign_id=$c AND version_id=$v", ("$c", campaign), ("$v", version));
        return c.ExecuteScalar() is string s ? s : null;
    });

    /// <summary>
    /// A VERDICT IS CHARGED TO THE ASKER'S CHAIN AND REFUSED PAST ITS UNIT'S SHARE. The team holds 0.25 of a 4-judgement campaign — one
    /// judgement: its first is charged and the row records the launch that asked it; its second is refused in words naming the team,
    /// its share, its use and the campaign's budget, nothing written; the version already judged still answers Ok and spends nothing
    /// (unchanged); a launch funded outside the team, and an in-process ask with no launch (NULL, funded by no unit), are charged as
    /// before; once the campaign is spent its own words answer first. RED at base: <c>ChargeVerdict</c> took no launch.
    /// </summary>
    [Fact]
    public void A_verdict_is_charged_to_the_askers_chain_and_refused_past_its_units_share()
    {
        using var db = TestEnv.NewDb();
        var campaigns = new CampaignStore(db);
        var set = Held(db);
        var campaign = Opened(db, set, trials: 10, verdicts: 4);
        GivenTeam(db, trialShare: null, verdictShare: "0.25");
        var team = Attempt(db, Team, OrgIds.Research, OrgIds.Root);
        var outside = Attempt(db, OrgIds.Research, OrgIds.Root);
        var v = Enumerable.Range(0, 6).Select(i => Version(db, $"version-verdict-{i}")).ToArray();

        var first = campaigns.ChargeVerdict(campaign.Id, v[0], At, team);
        Assert.True(first.Ok, first.Why);
        Assert.Equal(team, AskedBy(db, campaign.Id, v[0]));

        var second = campaigns.ChargeVerdict(campaign.Id, v[1], At, team);
        Assert.False(second.Ok, "a second judgement was charged past the team's quarter of a 4-judgement campaign");
        foreach (var words in new[] { Team, "0.25", "1 of", $"{campaign.VerdictBudget}" })
            Assert.Contains(words, second.Why, StringComparison.Ordinal);
        Assert.False(campaigns.VerdictCharged(campaign.Id, v[1]));

        // ALREADY JUDGED: answered Ok and free, as before — the charge was taken before the computation.
        var again = campaigns.ChargeVerdict(campaign.Id, v[0], At, team);
        Assert.True(again.Ok, again.Why);
        Assert.Equal(1, again.Spent);

        // OUTSIDE THE TEAM, AND NO LAUNCH AT ALL: charged as before; the row says who asked, or nobody.
        Assert.True(campaigns.ChargeVerdict(campaign.Id, v[1], At, outside).Ok);
        Assert.Equal(outside, AskedBy(db, campaign.Id, v[1]));
        Assert.True(campaigns.ChargeVerdict(campaign.Id, v[2], At).Ok);
        Assert.Null(AskedBy(db, campaign.Id, v[2]));
        Assert.True(campaigns.ChargeVerdict(campaign.Id, v[3], At, outside).Ok);

        // THE CAMPAIGN IS SPENT: ITS OWN GATE ANSWERS FIRST.
        var full = campaigns.ChargeVerdict(campaign.Id, v[4], At, team);
        Assert.False(full.Ok);
        Assert.Contains($"has spent all {campaign.VerdictBudget} of its final judgements", full.Why, StringComparison.Ordinal);
        Assert.DoesNotContain(Team, full.Why, StringComparison.Ordinal);
    }
    // ---- item 4: who spends verdicts -------------------------------------------------------------------------------

    static readonly DateTimeOffset Bar0 = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    const int HoldoutAtBar = 60;
    /// <summary>A program the referee will judge: it declares its three execution bounds (<c>RefereeVerdictTests</c>' losing one).</summary>
    const string ProgramText = "instrument BTCUSDT\nsize fixed 1\ntimeframe 1m\ndata_freshness 2m\nmax_decision_age 30s\n"
                               + "exit when close < 97\nentry when close > 103\n";

    /// <summary>The team head: an id shaped as the app mints one.</summary>
    const string TeamHead = "pos-e1a2b3c4d5e6";

    /// <summary>A gateway with real bars on disk, a real cutoff and a real campaign, as <c>CampaignLedgerTests</c> builds it.</summary>
    static async Task<(TradingGateway Gw, Database Db, DatasetRecord Set, CampaignRow Campaign)> Campaigning(int verdicts)
    {
        var (gw, _, db) = await TestEnv.Ready(s => { s.CampaignTrialBudget = 10; s.CampaignVerdictBudget = verdicts; });
        var dir = BinanceArchive.DatasetDir("BTCUSDT");
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        var raw = Path.Combine(dir, "raw", $"BTCUSDT-{Guid.NewGuid():n}.zip");
        File.WriteAllText(raw, "a stand-in for the vendor's monthly archive");
        var csv = Path.Combine(dir, $"{Guid.NewGuid():n}.csv");
        var text = new StringBuilder().Append(KlineNormaliser.Header).Append('\n');
        for (var i = 0; i < 120; i++)
        {
            var close = 96m + i % 10;
            text.Append(CultureInfo.InvariantCulture,
                $"{Bar0.AddMinutes(i).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{close},{close + 1m},{close - 1m},{close},1.00\n");
        }
        File.WriteAllText(csv, text.ToString());
        var id = gw.Datasets.Record(new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 1, ["2025-09"],
            csv, DatasetStore.Sha256(csv)!, 120, Bar0, Bar0.AddMinutes(119), 0, [], false,
            0, 0, 0, Bar0, DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-08", "https://127.0.0.1/x.zip", DatasetStore.Sha256(raw)!,
                DatasetStore.Sha256(raw)!, new FileInfo(raw).Length, Bar0, KlineTimeUnit.Microseconds, raw)]));
        var (held, campaign) = gw.SetHoldout(id, Bar0.AddMinutes(HoldoutAtBar), EvaluationClass.Research);
        Assert.True(held.Ok, held.Why);
        return (gw, db, gw.Datasets.ById(id)!, campaign!);
    }

    /// <summary>A team under <c>div-research</c> headed by <see cref="TeamHead"/>, active, with no verdict share.</summary>
    static void HeadTheTeam(Database db) => db.Write(_ =>
    {
        using (var u = db.Cmd("""
            INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, envelope_share, created_at)
            VALUES($id, $parent, 'team', NULL, 'active', '0.5', $at)
            """, ("$id", Team), ("$parent", OrgIds.Research), ("$at", Sql.T(At))))
            u.ExecuteNonQuery();
        using (var p = db.Cmd("""
            INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES($id, $unit, $home, 'active', $at)
            """, ("$id", TeamHead), ("$unit", Team), ("$home", $"org/{TeamHead}"), ("$at", Sql.T(At))))
            p.ExecuteNonQuery();
        using var h = db.Cmd("UPDATE org_unit SET head_position_id=$p WHERE id=$id", ("$p", TeamHead), ("$id", Team));
        return h.ExecuteNonQuery();
    });

    /// <summary>A launch of <paramref name="role"/> admitted under its chain as the chart reads it now.</summary>
    static string LaunchOf(Database db, string role) => AttemptAs(db, role, [.. new OrgStore(db).FundingLine(role).Select(u => u.Id)]);

    static string AttemptAs(Database db, string role, string[] funding)
    {
        var id = $"turn-{Guid.NewGuid():n}"[..17];
        Assert.True(new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = id, StartedAt = DateTimeOffset.UtcNow, Role = role, ReservedCost = 0m, SeatClass = SeatClasses.Harness,
            BillingClass = BillingClasses.Key, Funding = funding
        }).Admitted);
        return id;
    }

    /// <summary>A completed run of the one program, by <paramref name="role"/> under <paramref name="attempt"/>: its version id.</summary>
    static string Measured(TradingGateway gw, DatasetRecord set, string role, string attempt, string program = ProgramText)
    {
        var dir = Path.Combine(Paths.RoleHome(role), "strategies");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "evidence.strategy"), program);
        return gw.Backtests.Run(AgentContext.ForAgent("agent", role, attempt),
            new BacktestAsk("strategies/evidence.strategy", set.Id, Bar0, Bar0.AddMinutes(HoldoutAtBar - 1), 0.001m, Increment: 1m)).Result.VersionId;
    }

    static IpcRequest VerdictFrame(string version, long dataset) => new()
    {
        Op = Ops.Verdict, Session = "evidence", RequestId = "evidence-" + Guid.NewGuid().ToString("n")[..12],
        Args = new Dictionary<string, JsonElement>
        {
            ["version"] = JsonSerializer.SerializeToElement(version), ["dataset"] = JsonSerializer.SerializeToElement(dataset)
        }
    };

    /// <summary>
    /// A TEAM HEAD WITHOUT A DELEGATED SHARE IS REFUSED A VERDICT (ORGANISATION § 2 :101, :108 — "only division heads spend verdicts";
    /// a team "unless the division delegates a count"): refused in words naming who may, with nothing charged; once its division
    /// gives the team a verdict share, the same ask is no longer refused for who it is, and the charge is the team head's launch's —
    /// its recorded chain. RED at base: the team head's ask was charged at once.
    /// </summary>
    [Fact]
    public async Task A_team_head_without_a_delegated_share_is_refused_a_verdict()
    {
        var (gw, db, set, campaign) = await Campaigning(verdicts: 4);
        await using var _gw = gw;
        using var _db = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure());
        HeadTheTeam(db);
        var launch = LaunchOf(db, TeamHead);
        var version = Measured(gw, set, TeamHead, launch);

        var refused = await server.CallAsync(VerdictFrame(version, set.Id), TeamHead, launch);
        Assert.False(refused.Ok, "a team head with no delegated verdict share was charged a judgement");
        foreach (var words in new[] { "division", "verdict share", Team })
            Assert.Contains(words, refused.Error!.Message, StringComparison.Ordinal);
        Assert.False(gw.Campaigns.VerdictCharged(campaign.Id, version));

        // ITS DIVISION DELEGATES A COUNT: no longer refused for who it is, and charged to the launch that asked.
        db.Write(_ =>
        {
            using var c = db.Cmd("UPDATE org_unit SET verdict_share='0.25' WHERE id=$id", ("$id", Team));
            return c.ExecuteNonQuery();
        });
        var served = await server.CallAsync(VerdictFrame(version, set.Id), TeamHead, launch);
        Assert.True(served.Error is null || !served.Error.Message.Contains("verdict share", StringComparison.Ordinal), served.Error?.Message);
        Assert.True(gw.Campaigns.VerdictCharged(campaign.Id, version));
        Assert.Equal(launch, AskedBy(db, campaign.Id, version));
    }

    /// <summary>
    /// GUARD: THE LEGACY PAIR ASK VERDICTS AS BEFORE — <c>research</c> heads <c>div-research</c>, a division, and its NULL verdict share
    /// is no cap: its ask is charged to the launch that asked, never refused for who it is.
    /// </summary>
    [Fact]
    public async Task The_legacy_pair_ask_verdicts_as_before()
    {
        var (gw, db, set, campaign) = await Campaigning(verdicts: 2);
        await using var _gw = gw;
        using var _db = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure());
        var launch = LaunchOf(db, CouncilRoles.Research);
        var version = Measured(gw, set, CouncilRoles.Research, launch);

        var asked = await server.CallAsync(VerdictFrame(version, set.Id), CouncilRoles.Research, launch);
        Assert.True(asked.Error is null || !asked.Error.Message.Contains("verdict share", StringComparison.Ordinal), asked.Error?.Message);
        Assert.True(gw.Campaigns.VerdictCharged(campaign.Id, version));
        Assert.Equal(launch, AskedBy(db, campaign.Id, version));
    }
    // ---- item 7: a zero cap in a ceiling the charter does not draw on drops nothing ----------------------------------

    /// <summary>The chief: the root's head, as only a test may seat it.</summary>
    const string Chief = "pos-c41ef0c41ef0";

    static void SeatTheChief(Database db) => db.Write(_ =>
    {
        using (var p = db.Cmd("""
            INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES($id, $root, $home, 'active', $at)
            """, ("$id", Chief), ("$root", OrgIds.Root), ("$home", $"org/{Chief}"), ("$at", Sql.T(At))))
            p.ExecuteNonQuery();
        using var h = db.Cmd("UPDATE org_unit SET head_position_id=$p WHERE id=$root", ("$p", Chief), ("$root", OrgIds.Root));
        return h.ExecuteNonQuery();
    });

    /// <summary>A launch of <paramref name="role"/> on <paramref name="seatClass"/>, its assurance recorded with it.</summary>
    static string LaunchOn(Database db, string role, string seatClass)
    {
        var id = $"turn-{Guid.NewGuid():n}"[..17];
        var admission = new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = id, StartedAt = DateTimeOffset.UtcNow, Role = role, ReservedCost = 0.10m, SeatClass = seatClass,
            Assurance = SeatClasses.AssuranceOf(seatClass) is { } a ? SeatClasses.Name(a) : null
        });
        Assert.True(admission.Admitted, admission.Refusal);
        return id;
    }

    static IpcRequest Frame(string op, params (string Key, object Value)[] args) => new()
    {
        Op = op, Session = "evidence", RequestId = "evidence-" + Guid.NewGuid().ToString("n")[..12],
        Args = args.ToDictionary(a => a.Key, a => JsonSerializer.SerializeToElement(a.Value))
    };

    static string UnitAdmissible() => JsonSerializer.Serialize(new[]
    {
        new { predicate = ForecastVocabulary.UnitAdmissible, target = ForecastVocabulary.Admitted, by = DateTimeOffset.UtcNow.AddDays(45).ToString("O"), probability = 0.4m }
    });

    static IpcRequest ExperimentDivision() => Frame(Ops.OrgCharter, ("kind", OrgUnitKind.Division), ("ai_share", 0.1m),
        ("rationale", "a bounded experiment division"), ("forecasts", UnitAdmissible()), ("collapse_by", DateTimeOffset.UtcNow.AddDays(60).ToString("O")));

    /// <summary>
    /// The re-review's setting, through the REAL meter: the legacy pair's CLI seats bill the PLAN, whose capacity is 0; the chief's seat
    /// is the app's harness, billed to the KEY under a money cap of 10; the split is 0.3 to operations and 0.6 to research.
    /// </summary>
    static async Task<(TradingGateway Gw, Database Db, GatewayPipeServer Server)> ZeroPlan(bool chiefOnKey)
    {
        var (gw, _, db) = await TestEnv.Ready();
        SeatTheChief(db);
        var meter = new TurnMeter(db, () => 10m, runtimeId: () => "codex", live: new LiveAttempts(),
            share: role => role == CouncilRoles.Research ? 0.6m : 0.3m,
            declaredSeat: role => chiefOnKey && role == Chief ? SeatClasses.Harness : SeatClasses.Cli,
            planCap: () => 0m, signIn: () => BillingClasses.Plan);
        gw.AiEnvelopes = meter.EnvelopesFor;
        gw.AiEnvelopesIn = meter.EnvelopesIn;
        return (gw, db, new GatewayPipeServer(gw, IpcToken.Ensure()));
    }

    static int Divisions(Database db) => new OrgStore(db).Units().Count(u => u.Kind == OrgUnitKind.Division);

    /// <summary>
    /// A ZERO CAP IN ONE CEILING KEEPS THE LEGACY SHARE THE OTHER CEILING SIZES (the Codex re-review of <c>U-org-verbs-c</c> fix 1, P2):
    /// the chief's charter draws on the KEY, where the split sizes the legacy divisions at 0.3 and 0.6, so a valid 0.1 experiment
    /// division is chartered — the zero plan capacity the legacy seats bill drops nothing. RED at base: the legacy shares were read in
    /// the plan's ceiling alone, where a zero cap sizes nothing, and the charter was refused.
    /// </summary>
    [Fact]
    public async Task A_zero_cap_in_one_ceiling_keeps_the_legacy_share_the_other_ceiling_sizes()
    {
        var (gw, db, server) = await ZeroPlan(chiefOnKey: true);
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        var before = Divisions(db);

        var made = await server.CallAsync(ExperimentDivision(), Chief, LaunchOn(db, Chief, SeatClasses.Harness));
        Assert.True(made.Ok, made.Error?.Message);
        Assert.Equal(before + 1, Divisions(db));
    }

    /// <summary>
    /// ITS FAIL-CLOSED TWIN: a charter that draws on the PLAN — the chief's seat billed there too — where the zero cap sizes no legacy
    /// division, is refused in words naming the Safety page's split, nothing written.
    /// </summary>
    [Fact]
    public async Task A_zero_cap_in_the_ceiling_a_charter_draws_on_still_refuses_it()
    {
        var (gw, db, server) = await ZeroPlan(chiefOnKey: false);
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        var before = Divisions(db);

        var refused = await server.CallAsync(ExperimentDivision(), Chief, LaunchOn(db, Chief, SeatClasses.Harness));
        Assert.False(refused.Ok, "a charter was written that its own ceiling could not size");
        Assert.Contains("the split on the Safety page", refused.Error!.Message, StringComparison.Ordinal);
        Assert.Equal(before, Divisions(db));
    }

    // ---- U-org-autonomy item 3 (R25 D6): the chief spends a verdict on a version authored in its subtree ----------------------

    /// <summary>
    /// THE CHIEF SPENDS A VERDICT ON A VERSION AUTHORED IN ITS SUBTREE: a team head two units below it authored and measured the version;
    /// the chief — who measured nothing — asks, and the judgement is charged to the CHIEF's launch, judged on the author's own completed
    /// run. The team head itself, with no delegated share, is still refused (every other verdict rule unchanged). RED at base: the chief
    /// was refused, measured on its own runs ("has never completed a run of version …").
    /// </summary>
    [Fact]
    public async Task The_chief_spends_a_verdict_on_a_version_authored_in_its_subtree()
    {
        var (gw, db, set, campaign) = await Campaigning(verdicts: 4);
        await using var _gw = gw;
        using var _db = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure());
        HeadTheTeam(db);
        SeatTheChief(db);
        var authored = LaunchOf(db, TeamHead);
        var version = Measured(gw, set, TeamHead, authored);

        var refused = await server.CallAsync(VerdictFrame(version, set.Id), TeamHead, authored);
        Assert.False(refused.Ok, "a team head with no delegated verdict share was charged a judgement");
        Assert.False(gw.Campaigns.VerdictCharged(campaign.Id, version));

        var chief = LaunchOf(db, Chief);
        var served = await server.CallAsync(VerdictFrame(version, set.Id), Chief, chief);
        Assert.True(served.Ok, served.Error?.Message);
        Assert.True(gw.Campaigns.VerdictCharged(campaign.Id, version));
        Assert.Equal(chief, AskedBy(db, campaign.Id, version));
    }

    /// <summary>
    /// THE CAMPAIGN'S BUDGET BINDS THE CHIEF AS IT BINDS EVERYONE: with the campaign's one judgement spent on another version, the chief's
    /// ask on its subtree's version is refused before anything is computed, nothing more charged. And a version this installation does not
    /// hold is refused in words. RED at base: the chief was refused on its own (absent) runs, never reaching the budget.
    /// </summary>
    [Fact]
    public async Task The_chiefs_verdict_is_held_to_the_campaigns_budget()
    {
        var (gw, db, set, campaign) = await Campaigning(verdicts: 1);
        await using var _gw = gw;
        using var _db = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure());
        HeadTheTeam(db);
        SeatTheChief(db);
        var research = LaunchOf(db, CouncilRoles.Research);
        var spent = Measured(gw, set, CouncilRoles.Research, research, ProgramText.Replace("close < 97", "close < 96", StringComparison.Ordinal));
        Assert.True((await server.CallAsync(VerdictFrame(spent, set.Id), CouncilRoles.Research, research)).Ok);

        var authored = LaunchOf(db, TeamHead);
        var version = Measured(gw, set, TeamHead, authored);
        var chief = LaunchOf(db, Chief);
        var refused = await server.CallAsync(VerdictFrame(version, set.Id), Chief, chief);
        // THE REFEREE'S OWN REFUSAL, ANSWERED AS IT ALWAYS IS — a reply whose 'why' says the judgements are spent — and nothing charged.
        Assert.Contains("final judgements", refused.Ok ? Json.Write(refused.Data) : refused.Error!.Message, StringComparison.Ordinal);
        Assert.False(gw.Campaigns.VerdictCharged(campaign.Id, version), "the chief spent past the campaign's budget");

        var unknown = await server.CallAsync(VerdictFrame(new string('a', 64), set.Id), Chief, chief);
        Assert.False(unknown.Ok);
        Assert.Contains("no version", unknown.Error!.Message, StringComparison.Ordinal);
    }
}

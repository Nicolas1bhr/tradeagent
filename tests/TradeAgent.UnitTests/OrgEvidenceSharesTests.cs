using System.Globalization;
using System.Text;
using System.Text.Json;
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

    static DatasetRecord Held(Database db)
    {
        var store = new DatasetStore(db);
        var id = store.Record(new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 12, [],
            Path.Combine(Paths.Data, $"not-read-here-{Guid.NewGuid():n}.csv"), "aa11", 1000,
            Cutoff.AddDays(-300), Cutoff.AddDays(60), 0, [], false, 0, 0, 0, At,
            DatasetState.ACCEPTED, null, []));
        Assert.True(store.SetHoldout(id, Cutoff, EvaluationClass.Research).Ok);
        return store.ById(id)!;
    }

    static CampaignRow Opened(Database db, DatasetRecord set, int trials, int verdicts)
    {
        var opened = new CampaignStore(db).Open("BTCUSDT 1m v1", set, trials, verdicts, At);
        Assert.True(opened.Ok, opened.Why);
        return opened.Campaign!;
    }

    static string Version(Database db, string id)
    {
        new StrategyStore(db).RecordVersion(new StrategyVersionRow(
            id, "instrument BTCUSDT\n", "canonical", "{}", StrategyStore.InterpreterBuild,
            ParseVerdict.Accepted, 0, At, CouncilRoles.Research, "attempt-version"));
        return id;
    }

    /// <summary>A run of <paramref name="version"/> made by the launch <paramref name="attempt"/> — the run row carries it.</summary>
    static string Run(Database db, DatasetRecord set, string version, string runId, string attempt)
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
    static string Measured(TradingGateway gw, DatasetRecord set, string role, string attempt)
    {
        var dir = Path.Combine(Paths.RoleHome(role), "strategies");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "evidence.strategy"), ProgramText);
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
}

using TradeAgent.Core.Strategy;
using System.Text;
using System.Globalization;
using System.Reflection;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEMS 3, 4 AND 5 — THE CAMPAIGN, THE TRIALS CHARGED AGAINST IT, AND THE SCARCITY OF A VERDICT.
///
/// <para><b>What was wrong before this.</b> <c>grep -rn campaign src</c> found doc-comments and nothing
/// else: no campaign, no scoring policy, no trial count and no verdict count existed, so a thousand
/// backtests of one program cost nothing at all and nothing anywhere said what the standard was going to
/// be. `docs/COUNCIL.md`:131-134 asks for all four — "registered submissions charged against a
/// campaign-wide trial budget that survives team replacement, with campaign renewal authorised by code
/// so no new campaign resets holdout access ... the scoring policy fixed before a campaign ... final
/// evaluation scarce because every verdict leaks".</para>
///
/// <para><b>The three mutants this class exists to catch.</b> A renewal that mints a fresh id with no
/// <c>renewed_from</c>, so a renewed campaign starts with untouched holdout access. A trial keyed by the
/// ATTEMPT, so a restart resets the budget. A verdict charged AFTER the run, so the leak has already
/// happened by the time the budget refuses.</para>
/// </summary>
public class CampaignLedgerTests
{
    static readonly DateTimeOffset At = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    static readonly DateTimeOffset Cutoff = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A dataset row with a real cutoff on it, written by the store the owner's window calls.</summary>
    static DatasetRecord Held(Database db, string pair = "BTCUSDT",
        string evaluationClass = EvaluationClass.Research)
    {
        var store = new DatasetStore(db);
        var record = new DatasetRecord(
            0, BinanceArchive.Source, pair, BinanceArchive.Interval, "v1", 12, 12, [],
            Path.Combine(Paths.Data, $"not-read-here-{Guid.NewGuid():n}.csv"), "aa11", 1000,
            Cutoff.AddDays(-300), Cutoff.AddDays(60), 0, [], false, 0, 0, 0, At,
            DatasetState.ACCEPTED, null, []);

        var id = store.Record(record);
        Assert.True(store.SetHoldout(id, Cutoff, evaluationClass).Ok);
        return store.ById(id)!;
    }

    static CampaignRow Opened(Database db, DatasetRecord? over = null, int trials = 3, int verdicts = 2)
    {
        var opened = new CampaignStore(db).Open("BTCUSDT 1m v1", over ?? Held(db), trials, verdicts, At);
        Assert.True(opened.Ok, opened.Why);
        return opened.Campaign!;
    }

    // ---- item 3: the campaign ---------------------------------------------------------------------

    /// <summary>
    /// THE SCORING POLICY TEXT AND ITS SHA ARE FIXED AT OPEN, ON THE ROW.
    ///
    /// <para>Copied rather than referenced, because precommitment is the point: a policy read from a
    /// constant at judging time could change between the hypothesis and the verdict, and
    /// `docs/COUNCIL.md`:212 names exactly that — "that criteria, budgets and assessments were fixed
    /// before the outcome was known" — as unrecoverable if it is not recorded at the time.</para>
    /// </summary>
    [Fact]
    public void A_campaign_fixes_its_scoring_policy_and_its_sha_at_open()
    {
        using var db = TestEnv.NewDb();
        var set = Held(db);
        var campaign = Opened(db, set);

        Assert.Equal(CampaignPolicy.V1, campaign.ScoringPolicy);
        Assert.Equal(Sha256Hex.Of(CampaignPolicy.V1), campaign.ScoringPolicySha256);
        Assert.Equal(64, campaign.ScoringPolicySha256.Length);
        Assert.Equal(set.Id, campaign.HoldoutDatasetId);
        Assert.Equal(Cutoff, campaign.HoldoutFrom);
        Assert.Null(campaign.RenewedFrom);
        Assert.True(campaign.IsOpen);

        // Read back off the row, not out of the object that was just written.
        var row = new CampaignStore(db).ById(campaign.Id)!;
        Assert.Equal(campaign.ScoringPolicySha256, row.ScoringPolicySha256);
        Assert.Equal(CampaignPolicy.V1, row.ScoringPolicy);
    }

    /// <summary>
    /// ONE CAMPAIGN PER HOLDOUT DATASET. A second one over the same months would be a second trial
    /// budget over evidence that has already been peeked at as many times as the first allowed.
    /// </summary>
    [Fact]
    public void There_is_one_campaign_per_holdout_dataset_and_a_second_open_is_refused()
    {
        using var db = TestEnv.NewDb();
        var store = new CampaignStore(db);
        var set = Held(db);
        var first = Opened(db, set);

        var second = store.Open("again", set, 10, 10, At);

        Assert.False(second.Ok, "a second campaign was opened over one holdout");
        Assert.Contains($"campaign {first.Id} is already open", second.Why, StringComparison.Ordinal);
        Assert.Single(store.All());

        // AND THE DATABASE ITSELF REFUSES, not only the C# above it. Two presses racing must not be able
        // to make two campaigns, so the rule is a partial unique index; this writes straight past the
        // store to prove the index is what is holding, rather than the check in front of it.
        var direct = Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => db.Write(_ =>
        {
            using var c = db.Cmd("""
                INSERT INTO strategy_campaign(name, scoring_policy, scoring_policy_sha256, trial_budget,
                                              verdict_budget, holdout_dataset_id, holdout_from, opened_at,
                                              renewed_from, closed_at)
                VALUES('race','p','sha',1,1,$ds,$cut,$at,NULL,NULL)
                """, ("$ds", set.Id), ("$cut", Cutoff.ToString("O")), ("$at", At.ToString("O")));
            return c.ExecuteNonQuery();
        }));

        Assert.Contains("UNIQUE constraint failed", direct.Message, StringComparison.Ordinal);
    }

    /// <summary>A campaign is about a holdout, so a dataset that holds nothing back has none.</summary>
    [Fact]
    public void A_campaign_over_a_dataset_that_holds_nothing_back_is_refused()
    {
        using var db = TestEnv.NewDb();
        var record = new DatasetRecord(
            0, BinanceArchive.Source, "ETHUSDT", BinanceArchive.Interval, "v1", 12, 12, [],
            Path.Combine(Paths.Data, "nothing.csv"), "bb22", 10, At, At, 0, [], false, 0, 0, 0, At,
            DatasetState.ACCEPTED, null, []);
        var open = new DatasetStore(db).Record(record);

        var refused = new CampaignStore(db).Open("nothing", record with { Id = open }, 5, 5, At);

        Assert.False(refused.Ok);
        Assert.Contains("holds nothing back", refused.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// RENEWAL CARRIES <c>renewed_from</c>, THE PARENT'S HOLDOUT AND THE PARENT'S POLICY. It buys
    /// ATTEMPTS and nothing else — `docs/COUNCIL.md`:132, "campaign renewal authorised by code so no new
    /// campaign resets holdout access".
    ///
    /// <para>The lineage is what the verdict budget is counted over (item 5), so a renewal that lost its
    /// parent would be a campaign whose holdout access started untouched. That is the mutant.</para>
    /// </summary>
    [Fact]
    public void A_renewal_carries_the_parent_and_the_parents_holdout_and_policy()
    {
        using var db = TestEnv.NewDb();
        var store = new CampaignStore(db);
        var set = Held(db);
        var parent = Opened(db, set);

        var renewed = store.Renew(parent.Id, trialBudget: 7, verdictBudget: 1, At.AddDays(30));

        Assert.True(renewed.Ok, renewed.Why);
        var child = renewed.Campaign!;

        Assert.NotEqual(parent.Id, child.Id);
        Assert.Equal(parent.Id, child.RenewedFrom);
        Assert.Equal(parent.HoldoutDatasetId, child.HoldoutDatasetId);
        Assert.Equal(parent.HoldoutFrom, child.HoldoutFrom);
        Assert.Equal(parent.ScoringPolicy, child.ScoringPolicy);
        Assert.Equal(parent.ScoringPolicySha256, child.ScoringPolicySha256);
        Assert.Equal(7, child.TrialBudget);

        // The parent is closed, which is what keeps "one open campaign per holdout" true through a
        // renewal — and the lineage is the chain the verdict budget is counted over.
        Assert.False(store.ById(parent.Id)!.IsOpen);
        Assert.Equal([child.Id, parent.Id], store.Lineage(child.Id));
        Assert.Equal(child.Id, store.OpenForDataset(set.Id)!.Id);
    }

    /// <summary>A campaign already closed is renewed once. Twice would be two children of one parent.</summary>
    [Fact]
    public void A_closed_campaign_is_not_renewed_twice()
    {
        using var db = TestEnv.NewDb();
        var store = new CampaignStore(db);
        var parent = Opened(db);

        Assert.True(store.Renew(parent.Id, 5, 5, At).Ok);
        var again = store.Renew(parent.Id, 5, 5, At);

        Assert.False(again.Ok);
        Assert.Contains("was already closed", again.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE OWNER'S ONE PRESS WRITES BOTH ROWS, and the budgets come off the settings AS THEY STAND.
    ///
    /// <para>This is the path the card on the Data page takes — <c>TradingGateway.SetHoldout</c> — so the
    /// invariant "a holdout always has a campaign" is proved where it is enforced rather than where it is
    /// described. A second press moves the cutoff and leaves the campaign alone: its trial history is the
    /// whole point, and a fresh campaign would reset a count that must survive a team's replacement.</para>
    /// </summary>
    [Fact]
    public async Task The_owners_press_sets_the_cutoff_and_opens_one_campaign_with_the_settings_budgets()
    {
        var (gw, _, db) = await TestEnv.Ready(s =>
        {
            s.CampaignTrialBudget = 42;
            s.CampaignVerdictBudget = 2;
        });
        using var _1 = db;

        var record = new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 12, [],
            Path.Combine(Paths.Data, "nothing-read.csv"), "cc33", 10, At.AddDays(-1), At, 0, [], false,
            0, 0, 0, At, DatasetState.ACCEPTED, null, []);
        var id = gw.Datasets.Record(record);

        var (done, campaign) = gw.SetHoldout(id, Cutoff, EvaluationClass.Research);

        Assert.True(done.Ok, done.Why);
        Assert.NotNull(campaign);
        Assert.Equal(42, campaign!.TrialBudget);
        Assert.Equal(2, campaign.VerdictBudget);
        Assert.Equal(Cutoff, campaign.HoldoutFrom);
        Assert.Equal("BTCUSDT 1m v1", campaign.Name);

        // The second press moves the cutoff later and does NOT open a second campaign.
        var (moved, same) = gw.SetHoldout(id, Cutoff.AddDays(1), EvaluationClass.Research);
        Assert.True(moved.Ok, moved.Why);
        Assert.Equal(campaign.Id, same!.Id);
        Assert.Single(gw.Campaigns.All());
        Assert.Equal(Cutoff.AddDays(1), gw.Datasets.ById(id)!.HoldoutFrom);

        // And a refused press opens nothing at all.
        var (back, none) = gw.SetHoldout(id, Cutoff.AddDays(-10), EvaluationClass.Research);
        Assert.False(back.Ok);
        Assert.Null(none);
        Assert.Single(gw.Campaigns.All());
    }

    /// <summary>
    /// A BUDGET FROM AN UNREADABLE SETTINGS ROW IS ZERO, not the shipped allowance. The same reading
    /// <see cref="TradeAgentSettings.AiDailyCostCap"/> has, for the same reason: a row nobody could read
    /// must never be the event that takes a limit off.
    /// </summary>
    [Fact]
    public void An_unreadable_settings_row_allows_no_attempt_at_all()
    {
        var unreadable = TradeAgentSettings.Unreadable();

        Assert.Equal(0, unreadable.CampaignTrialBudget);
        Assert.Equal(0, unreadable.CampaignVerdictBudget);

        // The shipped defaults, stated here so that a change to either is a change to this test.
        var shipped = new TradeAgentSettings();
        Assert.Equal(200, shipped.CampaignTrialBudget);
        Assert.Equal(3, shipped.CampaignVerdictBudget);
    }

    // ---- item 4: the trials -----------------------------------------------------------------------

    static readonly DateTimeOffset Bar0 = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Minutes into the fixture dataset at which the owner's holdout begins.</summary>
    const int HoldoutAtBar = 60;

    const string ProgramText = "instrument BTCUSDT\nsize fixed 1\nexit when close < 97\nentry when close > 103\n";

    /// <summary>
    /// A GATEWAY WITH REAL BARS ON DISK, A REAL CUTOFF AND A REAL CAMPAIGN — the whole path the pipe
    /// takes, minus the pipe. 120 one-minute bars, the last 60 held back, so every window used below
    /// ends before the cutoff and nothing here is refused by the holdout instead of by the budget.
    /// </summary>
    static async Task<(TradingGateway Gw, Database Db, DatasetRecord Set, CampaignRow Campaign)> Campaigning(
        int trials = 3, int verdicts = 2, string evaluationClass = EvaluationClass.Research, int bars = 120)
    {
        var (gw, _, db) = await TestEnv.Ready(s =>
        {
            s.CampaignTrialBudget = trials;
            s.CampaignVerdictBudget = verdicts;
        });

        var dir = BinanceArchive.DatasetDir("BTCUSDT");
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        var raw = Path.Combine(dir, "raw", $"BTCUSDT-{Guid.NewGuid():n}.zip");
        File.WriteAllText(raw, "a stand-in for the vendor's monthly archive");

        var csv = Path.Combine(dir, $"{Guid.NewGuid():n}.csv");
        var text = new StringBuilder().Append(KlineNormaliser.Header).Append('\n');
        for (var i = 0; i < bars; i++)
        {
            var close = 96m + i % 10;
            text.Append(CultureInfo.InvariantCulture,
                $"{Bar0.AddMinutes(i).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{close},{close + 1m},{close - 1m},{close},1.00\n");
        }
        File.WriteAllText(csv, text.ToString());

        var record = new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 1, ["2025-09"],
            csv, DatasetStore.Sha256(csv)!, bars, Bar0, Bar0.AddMinutes(bars - 1), 0, [], false,
            0, 0, 0, Bar0, DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-08", "https://127.0.0.1/x.zip", DatasetStore.Sha256(raw)!,
                DatasetStore.Sha256(raw)!, new FileInfo(raw).Length, Bar0, KlineTimeUnit.Microseconds, raw)]);

        var id = gw.Datasets.Record(record);
        var (held, campaign) = gw.SetHoldout(id, Bar0.AddMinutes(HoldoutAtBar), evaluationClass);
        Assert.True(held.Ok, held.Why);
        Assert.NotNull(campaign);

        return (gw, db, gw.Datasets.ById(id)!, campaign!);
    }

    /// <summary>The same program text in a role's own folder. The same text is the same version.</summary>
    static string GivenProgram(string role, string name = "campaign.strategy", string? text = null)
    {
        var dir = Path.Combine(Paths.RoleHome(role), "strategies");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), text ?? ProgramText);
        return "strategies/" + name;
    }

    static BacktestRan Run(TradingGateway gw, DatasetRecord set, string path,
        string role = CouncilRoles.Research, string attempt = "attempt-1", int toBar = HoldoutAtBar - 1) =>
        gw.Backtests.Run(
            AgentContext.ForAgent("agent", role, attempt),
            // The increment is DECLARED: this fixture's dataset records no venue, and TradeAgent will
            // not invent one (`VenueIncrementTests`). 1 is what every run here used before the
            // catalogue existed, so no figure and no run id in this class moves.
            new BacktestAsk(path, set.Id, Bar0, Bar0.AddMinutes(toBar), 0.001m, Increment: 1m));

    /// <summary>
    /// EVERY REGISTERED RESEARCH RUN COSTS ONE TRIAL — and the row says what it cost without naming who
    /// asked. Red first: a thousand backtests of one version cost nothing at all, because no table
    /// counted them.
    /// </summary>
    [Fact]
    public async Task Every_registered_research_run_is_charged_one_trial_against_its_campaign()
    {
        var (gw, db, set, campaign) = await Campaigning();
        using var _1 = db;

        var ran = Run(gw, set, GivenProgram(CouncilRoles.Research));

        Assert.Equal(1, gw.Campaigns.TrialsCharged(campaign.Id));
        var trial = Assert.Single(gw.Campaigns.Trials(campaign.Id));
        Assert.Equal(campaign.Id, trial.CampaignId);
        Assert.Equal(ran.Result.VersionId, trial.VersionId);
        Assert.Equal(ran.Result.RunId, trial.RunId);
        Assert.Equal(EvaluationClass.Research, trial.Kind);
        Assert.True(trial.Charged);

        // AND THE TABLE ITSELF CARRIES NO ROLE AND NO ATTEMPT. Their absence is the guarantee — a column
        // that exists is a column a later change could key on — so it is asserted against the schema and
        // not only against the type.
        var columns = db.Read(_ =>
        {
            using var c = db.Cmd("SELECT name FROM pragma_table_info('strategy_trial')");
            var names = new List<string>();
            using var r = c.ExecuteReader();
            while (r.Read()) names.Add(r.GetString(0));
            return names;
        });

        Assert.Equal(
            ["campaign_id", "version_id", "run_id", "kind", "charged", "registered_at",
             // Schema 21: which campaign the trial was CHARGED to beside the one it PEEKED at, and
             // which of the two pots of the trial budget it came out of. Still nothing about who
             // asked — that is the property this list is asserted for.
             "charged_to", "exploration"],
            columns);
    }

    /// <summary>
    /// THE SAME QUESTION ASKED BY A REPLACEMENT COSTS NOTHING MORE, and asking it under a different role
    /// and a different attempt does not buy a second peek: the trial is keyed by the campaign, the version
    /// and the run, all three of which are content hashes or the app's own id.
    ///
    /// <para>This is the mutant — a trial keyed by the attempt, so a restart resets the budget.
    /// `docs/COUNCIL.md`:131 asks for a budget "that survives team replacement", and a killed worker
    /// coming back as a fresh attempt is the ordinary case, not the adversarial one.</para>
    /// </summary>
    [Fact]
    public async Task The_same_run_asked_for_again_by_a_replacement_is_one_trial()
    {
        var (gw, db, set, campaign) = await Campaigning();
        using var _1 = db;

        var first = Run(gw, set, GivenProgram(CouncilRoles.Research), attempt: "attempt-1");
        var restarted = Run(gw, set, GivenProgram(CouncilRoles.Research), attempt: "attempt-2-after-a-kill");
        var otherRole = Run(gw, set, GivenProgram(CouncilRoles.Operations), CouncilRoles.Operations, "attempt-3");

        // One version, one run, one trial — three asks.
        Assert.Equal(first.Result.RunId, restarted.Result.RunId);
        Assert.Equal(first.Result.RunId, otherRole.Result.RunId);
        Assert.Equal(1, gw.Campaigns.TrialsCharged(campaign.Id));
        Assert.Single(gw.Campaigns.Trials(campaign.Id));
    }

    /// <summary>
    /// A DIFFERENT WINDOW IS A DIFFERENT PEEK AND COSTS ANOTHER TRIAL. That is the other half of the
    /// same key: a run is one program over particular bytes under a particular execution model, so
    /// changing any of the three is a new question about the same data.
    /// </summary>
    [Fact]
    public async Task A_different_window_is_a_different_trial()
    {
        var (gw, db, set, campaign) = await Campaigning();
        using var _1 = db;
        var path = GivenProgram(CouncilRoles.Research);

        Run(gw, set, path, toBar: HoldoutAtBar - 1);
        Run(gw, set, path, toBar: HoldoutAtBar - 2);

        Assert.Equal(2, gw.Campaigns.TrialsCharged(campaign.Id));
    }

    /// <summary>
    /// A RUN OVER A FIXTURE DATASET IS RECORDED, CHARGED NOTHING AND NEVER EVIDENCE
    /// (<c>docs/COUNCIL.md</c>:134, "fixture runs establishing plumbing only").
    ///
    /// <para>Recorded rather than skipped: the plumbing run happened and the ledger says so. What it
    /// does not do is spend the budget that bounds how many times the real data has been looked at.</para>
    /// </summary>
    [Fact]
    public async Task A_run_over_a_fixture_dataset_is_charged_nothing()
    {
        var (gw, db, set, campaign) = await Campaigning(trials: 1, evaluationClass: EvaluationClass.Fixture);
        using var _1 = db;
        var path = GivenProgram(CouncilRoles.Research);

        Run(gw, set, path, toBar: HoldoutAtBar - 1);
        Run(gw, set, path, toBar: HoldoutAtBar - 2);
        Run(gw, set, path, toBar: HoldoutAtBar - 3);

        Assert.Equal(0, gw.Campaigns.TrialsCharged(campaign.Id));
        Assert.Equal(3, gw.Campaigns.Trials(campaign.Id).Count);
        Assert.All(gw.Campaigns.Trials(campaign.Id), t =>
        {
            Assert.Equal(EvaluationClass.Fixture, t.Kind);
            Assert.False(t.Charged);
        });
    }

    /// <summary>
    /// A CAMPAIGN OVER BUDGET REFUSES THE NEXT RUN IN WORDS, BEFORE IT RUNS.
    ///
    /// <para>Before, not after: a caller told "your budget is spent" once the evaluation is finished has
    /// spent the budget to learn that it was spent, and the run it just made is exactly the peek the
    /// count exists to bound. So the refusal is checked against the ledger first, and the proof that it
    /// really ran nothing is that no second run row exists.</para>
    /// </summary>
    [Fact]
    public async Task A_campaign_over_budget_refuses_the_next_run_in_words_before_it_runs()
    {
        var (gw, db, set, campaign) = await Campaigning(trials: 1);
        using var _1 = db;
        var path = GivenProgram(CouncilRoles.Research);

        Run(gw, set, path, toBar: HoldoutAtBar - 1);
        Assert.Equal(1, gw.Campaigns.TrialsCharged(campaign.Id));

        var refused = Assert.Throws<GatewayDeniedException>(() => Run(gw, set, path, toBar: HoldoutAtBar - 2));

        Assert.Equal(ErrorCode.CAMPAIGN_BUDGET_REACHED, refused.Code);
        Assert.Contains("has registered all 1 of its research trials", refused.Message, StringComparison.Ordinal);
        Assert.Contains("not reset by a restart", refused.Message, StringComparison.Ordinal);
        Assert.Contains("renewal", refused.Message, StringComparison.Ordinal);

        // It ran nothing: one run row, one trial, and the second window never became a measurement.
        Assert.Single(gw.Strategies.Runs());
        Assert.Equal(1, gw.Campaigns.TrialsCharged(campaign.Id));

        // A renewal is what is left, and it lets the next run through — carrying the parent's holdout.
        var renewed = gw.Campaigns.Renew(campaign.Id, trialBudget: 1, verdictBudget: 1, At);
        Assert.True(renewed.Ok, renewed.Why);
        Run(gw, set, path, toBar: HoldoutAtBar - 2);
        Assert.Equal(1, gw.Campaigns.TrialsCharged(renewed.Campaign!.Id));
        Assert.Equal(2, gw.Strategies.Runs().Count);
    }

    // ---- item 5: the verdict budget ---------------------------------------------------------------

    /// <summary>
    /// A VERDICT IS CHARGED BEFORE THE DOOR OPENS, AND THE DOOR IS THE ONLY WAY PAST THE CUTOFF.
    ///
    /// <para>The charge row exists the moment <c>RequestVerdict</c> answers — before any bar is read —
    /// and the feed it authorises really serves the months no pipe caller can have. The paired negative
    /// is in the same test: the same dataset opened as a pipe caller is refused.</para>
    /// </summary>
    [Fact]
    public async Task A_verdict_is_charged_before_the_holdout_is_read_and_the_referee_alone_reads_it()
    {
        var (gw, db, set, campaign) = await Campaigning(verdicts: 2);
        using var _1 = db;
        var version = Run(gw, set, GivenProgram(CouncilRoles.Research)).Result.VersionId;
        var referee = new Referee(db, () => At);

        var charge = referee.RequestVerdict(version, campaign.Id);

        Assert.True(charge.Ok, charge.Why);
        Assert.Equal(1, charge.Spent);
        Assert.Equal(2, charge.Budget);

        // THE ROW IS ALREADY THERE, before anything has been computed over the holdout.
        var row = Assert.Single(gw.Campaigns.Verdicts(campaign.Id));
        Assert.Equal(version, row.VersionId);
        Assert.Equal(set.HoldoutFrom, row.HoldoutFrom);
        Assert.Equal(At, row.RequestedAt);

        // AND THE DOOR OPENS ON THE BARS NOTHING ELSE CAN HAVE.
        var feed = referee.HoldoutFeed(charge);
        Assert.True(feed.Ok, feed.Why);
        var held = feed.Feed!.Bars(set.HoldoutFrom, null).ToList();
        Assert.NotEmpty(held);
        Assert.All(held, bar => Assert.True(bar.OpenTime >= set.HoldoutFrom!.Value));

        // The same dataset, asked for by a caller on the pipe: refused, and the refusal names the cutoff.
        var asPipe = BarFeed.Open(gw.Datasets, set.Id, BarAudience.Pipe(CouncilRoles.Research), null, null);
        Assert.False(asPipe.Ok);
        Assert.True(asPipe.IsHoldout);
        Assert.Contains("holds out every bar from", asPipe.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// OVER BUDGET, THE VERDICT IS REFUSED BEFORE ANYTHING RUNS — and the second ask deliberately comes
    /// BEFORE the first verdict has been computed.
    ///
    /// <para>That sequence is the mutant: charge after the run, and both asks get through, because the
    /// first has not paid yet. It is also the ordinary case rather than a contrived one — a verdict that
    /// has been authorised and not yet computed is exactly when the next request arrives.</para>
    /// </summary>
    [Fact]
    public async Task A_verdict_over_budget_is_refused_before_anything_is_computed()
    {
        var (gw, db, set, campaign) = await Campaigning(trials: 4, verdicts: 1);
        using var _1 = db;
        var path = GivenProgram(CouncilRoles.Research);
        var first = Run(gw, set, path, toBar: HoldoutAtBar - 1).Result.VersionId;
        var second = Run(gw, set, GivenProgram(CouncilRoles.Research, "other.strategy",
            "instrument BTCUSDT\nsize fixed 2\nexit when close < 97\nentry when close > 103\n")).Result.VersionId;
        Assert.NotEqual(first, second);

        var referee = new Referee(db, () => At);
        var paid = referee.RequestVerdict(first, campaign.Id);
        Assert.True(paid.Ok, paid.Why);

        // No feed opened in between: the first verdict is authorised and not yet computed.
        var refused = referee.RequestVerdict(second, campaign.Id);

        Assert.False(refused.Ok, "a second verdict was authorised on a budget of one");
        Assert.Contains("spent all 1 of its final judgements", refused.Why, StringComparison.Ordinal);
        Assert.Contains("BEFORE anything is computed", refused.Why, StringComparison.Ordinal);
        Assert.Single(gw.Campaigns.Verdicts(campaign.Id));

        // And the refusal really is a closed door, not a message beside an open one.
        var none = referee.HoldoutFeed(refused);
        Assert.False(none.Ok);
        Assert.Contains("this verdict was not charged", none.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE SAME VERSION ASKED TWICE IS ONE VERDICT AND ONE CHARGE, and it is still obtainable after the
    /// budget is full: the charge is taken before the computation, so a crash in between must not leave a
    /// verdict paid for and unreachable. The same version over the same holdout is the same answer.
    /// </summary>
    [Fact]
    public async Task The_same_version_asked_twice_is_one_verdict_and_stays_obtainable()
    {
        var (gw, db, set, campaign) = await Campaigning(verdicts: 1);
        using var _1 = db;
        var version = Run(gw, set, GivenProgram(CouncilRoles.Research)).Result.VersionId;
        var referee = new Referee(db, () => At);

        Assert.True(referee.RequestVerdict(version, campaign.Id).Ok);
        var again = referee.RequestVerdict(version, campaign.Id);

        Assert.True(again.Ok, again.Why);
        Assert.Single(gw.Campaigns.Verdicts(campaign.Id));
        Assert.Equal(1, gw.Campaigns.VerdictsInLineage(campaign.Id));
        Assert.True(referee.HoldoutFeed(again).Ok);
    }

    /// <summary>
    /// A RENEWAL BUYS ATTEMPTS AND NEVER HOLDOUT ACCESS (<c>docs/COUNCIL.md</c>:132).
    ///
    /// <para>This is the consequence of item 3's <c>renewed_from</c>, and the reason that mutant matters:
    /// verdicts are counted over the LINEAGE, so a renewed campaign starts with its trials renewed and its
    /// holdout access exactly as spent as its parent left it. A renewal that lost its parent would start
    /// untouched — a fresh set of peeks at months that have already been read.</para>
    /// </summary>
    [Fact]
    public async Task A_renewed_campaign_does_not_get_its_holdout_access_back()
    {
        var (gw, db, set, campaign) = await Campaigning(trials: 4, verdicts: 1);
        using var _1 = db;
        var path = GivenProgram(CouncilRoles.Research);
        var first = Run(gw, set, path, toBar: HoldoutAtBar - 1).Result.VersionId;
        var second = Run(gw, set, GivenProgram(CouncilRoles.Research, "other.strategy",
            "instrument BTCUSDT\nsize fixed 2\nexit when close < 97\nentry when close > 103\n")).Result.VersionId;

        var referee = new Referee(db, () => At);
        Assert.True(referee.RequestVerdict(first, campaign.Id).Ok);

        var renewed = gw.Campaigns.Renew(campaign.Id, trialBudget: 4, verdictBudget: 1, At.AddDays(1));
        Assert.True(renewed.Ok, renewed.Why);
        var child = renewed.Campaign!;

        // Trials ARE renewed — that is what a renewal is for.
        Assert.Equal(0, gw.Campaigns.TrialsCharged(child.Id));

        // Holdout access is NOT. The parent's verdict still counts against the child's budget.
        Assert.Equal(1, gw.Campaigns.VerdictsInLineage(child.Id));
        var refused = referee.RequestVerdict(second, child.Id);
        Assert.False(refused.Ok, "a renewal gave the research process its holdout access back");
        Assert.Contains("counted across every renewal", refused.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// A VERDICT IS ABOUT A VERSION THIS INSTALLATION HAS MEASURED, and an unknown one is refused without
    /// charging anything: a text nobody registered is not a submission, and charging for it would spend
    /// the scarcest budget in the product on a typo.
    /// </summary>
    [Fact]
    public async Task A_verdict_on_a_version_nobody_registered_is_refused_and_charges_nothing()
    {
        var (gw, db, _, campaign) = await Campaigning(verdicts: 2);
        using var _1 = db;
        var referee = new Referee(db, () => At);

        var refused = referee.RequestVerdict(new string('f', 64), campaign.Id);

        Assert.False(refused.Ok);
        Assert.Contains("never accepted a version", refused.Why, StringComparison.Ordinal);
        Assert.Empty(gw.Campaigns.Verdicts(campaign.Id));

        var noCampaign = referee.RequestVerdict("whatever", 4242);
        Assert.False(noCampaign.Ok);
        Assert.Contains("there is no campaign 4242", noCampaign.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE VERDICT ITSELF IS NOT IN THIS UNIT, AND THE SEAM SAYS SO RATHER THAN PRETENDING.
    ///
    /// <para><c>U-referee-2</c> computes the holdout run, the promotion record and the delivery. What this
    /// unit owes it is a charge that was taken before any of that and a door that no pipe caller can open
    /// — so the check here is that the seam is a real door (a feed with real bars behind it) and that the
    /// verdict table carries no outcome column for a later unit to have to unpick.</para>
    /// </summary>
    [Fact]
    public async Task The_verdict_table_is_the_budget_and_the_precommitment_and_carries_no_outcome()
    {
        var (gw, db, _, _) = await Campaigning();
        using var _1 = db;

        var columns = db.Read(_ =>
        {
            using var c = db.Cmd("SELECT name FROM pragma_table_info('strategy_verdict')");
            var names = new List<string>();
            using var r = c.ExecuteReader();
            while (r.Read()) names.Add(r.GetString(0));
            return names;
        });

        Assert.Equal(["campaign_id", "version_id", "requested_at", "holdout_from"], columns);
    }
    // ---- U-council-concurrent-2, item 5: the trial budget charged in ONE transaction ---------------

    /// <summary>
    /// RED FIRST: TWO ROLES ASKING FOR THE LAST TRIAL OF A CAMPAIGN BOTH TAKE IT, and the budget is
    /// exceeded by one.
    ///
    /// <para><c>U-referee-1</c> reported this race by name and left it: <c>TrialRefusal</c> read the
    /// count in one transaction and <c>RegisterTrial</c> wrote the row in another, with a backtest that
    /// takes minutes in between. Two roles can run at once since <c>U-council-concurrent-1</c>, so both
    /// read 199 of 200 and both registered — 201 peeks at the owner's held-back data under a budget of
    /// 200. "Campaign-wide trial limits that survive a team's replacement"
    /// (<c>docs/COUNCIL.md</c>:36) is not a limit two concurrent callers can walk past.</para>
    ///
    /// <para>TWO REAL THREADS AGAINST ONE <see cref="Database"/>, with a <see cref="Barrier"/> at the
    /// look, because "both were admitted" is a fact about two callers and a shared table and a stand-in
    /// for either would exercise neither. The idiom is <c>BudgetReservationTests</c>'s, and so is the
    /// shape of the fix: the look is honest and the transaction is the gate.</para>
    /// </summary>
    [Fact]
    public void Two_roles_racing_for_the_last_trial_of_a_campaign_take_exactly_one()
    {
        using var db = TestEnv.NewDb();
        var campaigns = new CampaignStore(db);
        var set = Held(db);
        var campaign = Opened(db, set, trials: Budget);
        var version = Measured(db, set, runs: Budget + 1);

        // 199 of the 200 already spent, so the next registration is the last one there is room for.
        for (var n = 0; n < Budget - 1; n++)
            Assert.True(campaigns
                .RegisterTrial(campaign.Id, version, $"run-{n}", EvaluationClass.Research, At).Ok);
        Assert.Equal(Budget - 1, campaigns.TrialsCharged(campaign.Id));

        var looked = new string?[2];
        var registered = new TrialRegistered[2];
        using var read = new Barrier(2);

        void Ask(int i)
        {
            // The loop's cheap first look, taken before the run — and on a campaign with one trial left
            // BOTH callers are honestly told there is room.
            looked[i] = campaigns.TrialRefusal(campaign.Id, version, EvaluationClass.Research);
            read.SignalAndWait();
            registered[i] = campaigns.RegisterTrial(
                campaign.Id, version, $"run-{Budget - 1 + i}", EvaluationClass.Research, At);
        }

        var a = new Thread(() => Ask(0));
        var b = new Thread(() => Ask(1));
        a.Start();
        b.Start();
        Assert.True(a.Join(TimeSpan.FromSeconds(30)), "the first caller never finished");
        Assert.True(b.Join(TimeSpan.FromSeconds(30)), "the second caller never finished");

        // Both were told there was room. That is the race and not a defect: it is what a look taken
        // before a run that takes minutes can honestly say, and it is why the look cannot be the gate.
        Assert.All(looked, why => Assert.Null(why));

        Assert.Equal(Budget, campaigns.TrialsCharged(campaign.Id));
        Assert.Single(registered, r => r.Ok);

        var refused = Assert.Single(registered, r => !r.Ok);
        Assert.Equal(Budget, refused.Spent);
        Assert.Equal(Budget, refused.Budget);
        Assert.False(refused.Charged);
        Assert.Contains($"all {Budget} of its research trials", refused.Why, StringComparison.Ordinal);
        Assert.Contains("this run is not recorded and its result is not served", refused.Why,
            StringComparison.Ordinal);
    }

    /// <summary>A campaign-sized budget, so the assertion reads as the figure the doctrine bounds.</summary>
    const int Budget = 200;

    /// <summary>
    /// ONE VERSION AND <paramref name="runs"/> RUNS OF IT, written through the app's own store, so the
    /// trials below have real rows to reference. <c>strategy_trial</c> has foreign keys to both, which
    /// is the ledger refusing to charge for a run nobody recorded.
    /// </summary>
    static string Measured(Database db, DatasetRecord over, int runs, string version = "version-x")
    {
        var strategies = new StrategyStore(db);
        strategies.RecordVersion(new StrategyVersionRow(
            version, "instrument BTCUSDT\n", "canonical", "{}", StrategyStore.InterpreterBuild,
            ParseVerdict.Accepted, 0, At, CouncilRoles.Research, "attempt-1"));

        for (var n = 0; n < runs; n++)
            strategies.RecordRun(new StrategyRunRow(
                $"run-{n}", version, over.Id, over.NormalisedSha256, Cutoff.AddDays(-10), Cutoff,
                "frictionless", BacktestOutcome.COMPLETED.ToString(), null,
                10, 1, 1, 1, 1, 1, 0, 0, 1m, 0m, 1m, 0m, "sha", At, CouncilRoles.Research, "attempt-1"),
                []);

        return version;
    }

    /// <summary>
    /// THE SAME TRIAL, ASKED FOR AGAIN, IS STILL OK EVEN WITH THE BUDGET FULL. A restart or a retry
    /// re-asking the identical question is the row that is already there; refusing it would make
    /// recovery look like an overrun and would leave a recorded run with its own trial unreadable.
    /// </summary>
    [Fact]
    public void A_trial_already_registered_answers_ok_even_once_the_budget_is_full()
    {
        using var db = TestEnv.NewDb();
        var campaigns = new CampaignStore(db);
        var set = Held(db);
        var campaign = Opened(db, set, trials: 1);
        var version = Measured(db, set, runs: 3);

        Assert.True(campaigns.RegisterTrial(campaign.Id, version, "run-0", EvaluationClass.Research, At).Ok);
        Assert.True(campaigns.Registered(campaign.Id, version, "run-0"));
        Assert.NotNull(campaigns.TrialRefusal(campaign.Id, version, EvaluationClass.Research));

        var again = campaigns.RegisterTrial(campaign.Id, version, "run-0", EvaluationClass.Research, At);
        Assert.True(again.Ok, again.Why);
        Assert.Equal(1, campaigns.TrialsCharged(campaign.Id));

        // A DIFFERENT run is a different peek, and there is no room for it.
        Assert.False(campaigns.RegisterTrial(campaign.Id, version, "run-1", EvaluationClass.Research, At).Ok);
        Assert.Equal(1, campaigns.TrialsCharged(campaign.Id));

        // A FIXTURE run is charged nothing, so a full budget refuses none of them.
        Assert.True(campaigns.RegisterTrial(campaign.Id, version, "run-2", EvaluationClass.Fixture, At).Ok);
        Assert.Equal(1, campaigns.TrialsCharged(campaign.Id));
    }

}

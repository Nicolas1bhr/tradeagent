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
}

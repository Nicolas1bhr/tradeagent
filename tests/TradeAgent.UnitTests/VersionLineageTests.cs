using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-ALLOCATOR-2, ITEMS 1-3 — VERSIONED PARENTAGE, COMPARABLE TRIALS, AND THE EXPLORATION RESERVE.
///
/// <para><b>What was wrong before this.</b> The only lineage in the build was the campaign's
/// <c>renewed_from</c>. A strategy version had no parent at all, so a variant of a promoted program was
/// a fresh hash related to nothing: whichever campaign it happened to be run under charged it against
/// an untouched trial budget, and <c>docs/COUNCIL.md</c>:201 — "evolution adds versioned parentage,
/// comparable trials, exploration and bounded replacement" — named three things this product did not
/// have.</para>
///
/// <para><b>The mutants this class exists to catch.</b> A parent INFERRED from the submitting role's
/// previous version, which makes two unrelated programs read as parent and child. A child charged to
/// its own campaign alone, which leaves the family's count unmoved by a variant. And the exploration
/// reading taken OUTSIDE the transaction that writes the trial, which is the by-one race
/// <c>U-referee-1</c> named, wearing a new name.</para>
/// </summary>
public class VersionLineageTests
{
    static readonly DateTimeOffset At = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    static readonly DateTimeOffset Cutoff = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>One program text per entry price, so a variant is a genuinely different hash.</summary>
    static string Text(int entry) =>
        $"instrument BTCUSDT\nsize fixed 1\nexit when close < 97\nentry when close > {entry}\n";

    /// <summary>A dataset with a holdout, its open campaign, and the store handles a test needs.</summary>
    sealed record Ledger(Database Db, DatasetRecord Set, CampaignRow Campaign, StrategyStore Strategies,
        CampaignStore Campaigns);

    static Ledger Given(int trialBudget = 10)
    {
        var db = TestEnv.NewDb();
        var l = Holdout(db, "BTCUSDT", trialBudget);
        return l;
    }

    /// <summary>One held-back dataset with its own open campaign, in a database that may hold several.</summary>
    static Ledger Holdout(Database db, string pair, int trialBudget = 10)
    {
        var datasets = new DatasetStore(db);
        var file = Path.Combine(Paths.Data, $"lineage-{Guid.NewGuid():n}.csv");
        Directory.CreateDirectory(Paths.Data);
        File.WriteAllText(file, KlineNormaliser.Header + "\n");

        var id = datasets.Record(new DatasetRecord(
            0, BinanceArchive.Source, pair, BinanceArchive.Interval, "v1", 12, 12, [],
            file, DatasetStore.Sha256(file)!, 1000, Cutoff.AddDays(-300), Cutoff.AddDays(60), 0, [],
            false, 0, 0, 0, At, DatasetState.ACCEPTED, null, []));

        Assert.True(datasets.SetHoldout(id, Cutoff, EvaluationClass.Research).Ok);
        var set = datasets.ById(id)!;

        var campaigns = new CampaignStore(db);
        var opened = campaigns.Open($"{pair} 1m v1", set, trialBudget, 3, At);
        Assert.True(opened.Ok, opened.Why);

        return new Ledger(db, set, opened.Campaign!, new StrategyStore(db), campaigns);
    }

    /// <summary>Records one version, declaring whatever parent the caller says and nothing else.</summary>
    static string Record(Ledger l, int entry, string? parent, string role = CouncilRoles.Research)
    {
        var program = StrategyParser.Parse(Text(entry)).Program!;
        l.Strategies.RecordVersion(new StrategyVersionRow(
            program.StrategyId, program.Source, program.Canonical, program.Manifest,
            StrategyStore.InterpreterBuild, ParseVerdict.Accepted, program.WarmUpBars, At, role, null)
        {
            ParentVersionId = parent
        });
        return program.StrategyId;
    }

    /// <summary>One run row, so a trial has something its foreign key can point at.</summary>
    static string Run(Ledger l, string versionId, decimal fees)
    {
        var model = ExecutionModel.Declare(fees, 0m, 0.0001m, 10_000m).Model!;
        var request = new BacktestRequest(l.Set.Id, l.Set.NormalisedSha256, model, Cutoff, null);
        var runId = request.RunIdFor(versionId);
        l.Strategies.RecordRun(new StrategyRunRow(
            runId, versionId, l.Set.Id, l.Set.NormalisedSha256, Cutoff, null, model.Canonical,
            BacktestOutcome.COMPLETED.ToString(), null, 500, 4, 3, 4, 4, 100, 0, 0,
            12m, 2m, 10m, 3m, "trace-sha", At, CouncilRoles.Research, null), []);
        return runId;
    }

    // ---- item 1: the parent is declared, and it is never inferred --------------------------------

    /// <summary>
    /// ITEM 1 — A VERSION CARRIES THE PARENT ITS SUBMITTER DECLARED, and declaring one moves no id.
    ///
    /// <para>The id is a hash over what the program MEANS. If parentage were inside it, the same twelve
    /// lines would be two different versions depending on what their submitter said about where they
    /// came from — and every run, trial and verdict already charged against the first would be evidence
    /// about a row nobody can find. So the column is outside the hash, and this test is what says so:
    /// the child's id is the id the parser minted for its text, unchanged by the declaration.</para>
    /// </summary>
    [Fact]
    public void A_declared_parent_is_recorded_on_the_child_and_moves_no_id()
    {
        var l = Given();
        using var _ = l.Db;

        var parent = Record(l, 103, parent: null);
        var child = Record(l, 104, parent: parent);

        Assert.Equal(StrategyParser.Parse(Text(104)).Program!.StrategyId, child);
        Assert.Equal(parent, l.Strategies.VersionById(child)!.ParentVersionId);
        Assert.Null(l.Strategies.VersionById(parent)!.ParentVersionId);
        Assert.Equal([child, parent], l.Strategies.Ancestry(child));
    }

    /// <summary>
    /// ITEM 1 — A VERSION THAT DECLARES NO PARENT IS ITS OWN ROOT, whatever else its submitter has sent.
    ///
    /// <para><b>The mutant.</b> Infer the parent from the submitting role's most recent version, which is
    /// the obvious cheap guess, and two unrelated programs from one research role read as parent and
    /// child. Every count this unit then takes over an ancestry is a count over an invention, and the
    /// exploration reserve — which is carved out for versions with no parent — stops reaching the
    /// programs it was carved out for.</para>
    /// </summary>
    [Fact]
    public void A_version_that_declares_no_parent_is_its_own_root_even_after_the_same_role_submitted_another()
    {
        var l = Given();
        using var _ = l.Db;

        var first = Record(l, 103, parent: null, role: CouncilRoles.Research);
        var second = Record(l, 105, parent: null, role: CouncilRoles.Research);

        Assert.Null(l.Strategies.VersionById(second)!.ParentVersionId);
        Assert.Equal([second], l.Strategies.Ancestry(second));
        Assert.DoesNotContain(first, l.Strategies.Ancestry(second));
    }

    // ---- item 2: comparable trials ---------------------------------------------------------------

    /// <summary>
    /// ITEM 2 — A VARIANT IS CHARGED TO ITS PARENT'S CAMPAIGN LINEAGE, so being a new hash buys a family
    /// no holdout access at all.
    ///
    /// <para><b>What was wrong.</b> A campaign is opened per holdout dataset, and a trial was charged to
    /// whichever campaign the run was made under. So a family that had spent its attempts on one holdout
    /// tweaked a number, got a fresh version id, ran it against a second held-back dataset and was
    /// charged against a budget that had never been touched. <c>docs/COUNCIL.md</c>:201 asks for
    /// COMPARABLE trials, and a comparison in which one candidate can mint its own allowance is not
    /// one.</para>
    ///
    /// <para><b>The mutant.</b> Charge the child to its own campaign — one line in
    /// <c>ChargedCampaignFor</c> — and the parent's lineage count is unmoved by the variant, which is
    /// the defect exactly.</para>
    /// </summary>
    [Fact]
    public void A_variant_run_against_a_second_holdout_is_charged_to_its_parents_campaign_as_well()
    {
        var first = Given();
        using var _ = first.Db;
        var second = Holdout(first.Db, "ETHUSDT");

        var parent = Record(first, 103, parent: null);
        Assert.True(first.Campaigns
            .RegisterTrial(first.Campaign.Id, parent, Run(first, parent, 0.001m),
                EvaluationClass.Research, At).Ok);
        Assert.Equal(1, first.Campaigns.TrialsCharged(first.Campaign.Id));

        // The variant: a new hash, a different holdout, and a campaign whose budget nothing has touched.
        var child = Record(first, 104, parent: parent);
        var registered = second.Campaigns.RegisterTrial(
            second.Campaign.Id, child, Run(second, child, 0.001m), EvaluationClass.Research, At);
        Assert.True(registered.Ok, registered.Why);

        // THE PEEK IS THE SECOND CAMPAIGN'S AND THE COST IS THE FIRST'S. Both counts move, because the
        // run really did read the second holdout's pre-cutoff bars and the family really did pay.
        Assert.Equal(2, first.Campaigns.TrialsCharged(first.Campaign.Id));
        Assert.Equal(1, second.Campaigns.TrialsCharged(second.Campaign.Id));

        var row = Assert.Single(second.Campaigns.Trials(second.Campaign.Id));
        Assert.Equal(second.Campaign.Id, row.CampaignId);
        Assert.Equal(first.Campaign.Id, row.ChargedTo);
    }

    /// <summary>
    /// ITEM 2 — AND THE CHARGE FOLLOWS A RENEWAL FORWARD, because a renewal is what buys a family more
    /// attempts and a closed campaign is not where a new charge belongs.
    ///
    /// <para><c>docs/COUNCIL.md</c>:132 — renewal is authorised by code and buys trials, never holdout
    /// access. The charge therefore lands on the campaign of that lineage which is open NOW, which is
    /// also what keeps the second ceiling from reading as permanently full.</para>
    /// </summary>
    [Fact]
    public void A_variant_is_charged_to_the_open_campaign_of_its_parents_lineage_after_a_renewal()
    {
        var first = Given(trialBudget: 1);
        using var _ = first.Db;
        var second = Holdout(first.Db, "ETHUSDT");

        var parent = Record(first, 103, parent: null);
        Assert.True(first.Campaigns
            .RegisterTrial(first.Campaign.Id, parent, Run(first, parent, 0.001m),
                EvaluationClass.Research, At).Ok);

        var renewed = first.Campaigns.Renew(first.Campaign.Id, 5, 3, At.AddDays(1));
        Assert.True(renewed.Ok, renewed.Why);

        var child = Record(first, 104, parent: parent);
        var registered = second.Campaigns.RegisterTrial(
            second.Campaign.Id, child, Run(second, child, 0.001m), EvaluationClass.Research, At);
        Assert.True(registered.Ok, registered.Why);

        var row = Assert.Single(second.Campaigns.Trials(second.Campaign.Id));
        Assert.Equal(renewed.Campaign!.Id, row.ChargedTo);
        Assert.Equal(1, first.Campaigns.TrialsCharged(renewed.Campaign.Id));

        // The parent's own campaign is closed and spent, and the renewal is where the family now pays.
        Assert.Equal(1, first.Campaigns.TrialsCharged(first.Campaign.Id));
    }
}

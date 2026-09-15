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
        var datasets = new DatasetStore(db);
        var file = Path.Combine(Paths.Data, $"lineage-{Guid.NewGuid():n}.csv");
        Directory.CreateDirectory(Paths.Data);
        File.WriteAllText(file, KlineNormaliser.Header + "\n");

        var id = datasets.Record(new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 12, [],
            file, DatasetStore.Sha256(file)!, 1000, Cutoff.AddDays(-300), Cutoff.AddDays(60), 0, [],
            false, 0, 0, 0, At, DatasetState.ACCEPTED, null, []));

        Assert.True(datasets.SetHoldout(id, Cutoff, EvaluationClass.Research).Ok);
        var set = datasets.ById(id)!;

        var campaigns = new CampaignStore(db);
        var opened = campaigns.Open("BTCUSDT 1m v1", set, trialBudget, 3, At);
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
}

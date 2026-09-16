using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-ALLOCATOR-2, ITEMS 4 AND 5 — THE RETIREMENT DISPOSITION THAT ERASES NOTHING, AND THE DIRECTORS'
/// OWN RECORD AGAINST A DECLARED BASELINE.
///
/// <para><b>What was wrong before this.</b> <c>BoundaryKind.Retirement</c> was a constant nothing
/// opened, so <c>docs/COUNCIL.md</c>:222's retirement-candidate event existed as a word. Nothing fenced
/// a candidate's next assignment, and nothing recorded what either director had RECOMMENDED — so :225's
/// "the directors' own recommendations, forecasts and timeliness are recorded against declared
/// baselines" had no row behind any of its three nouns.</para>
///
/// <para><b>The mutants this class exists to catch.</b> A retirement that clears the standing
/// allocation, which is a retired candidate's deployed strategy silently losing its capital — the one
/// thing :223 says retirement never does. And the baseline read at REVIEW time instead of at
/// declaration time, which makes every forecast read correct.</para>
/// </summary>
public class RetirementBoundaryTests
{
    static readonly DateTimeOffset At = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    static readonly DateTimeOffset Cutoff = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    static string Text(int entry) =>
        $"instrument BTCUSDT\nsize fixed 1\nexit when close < 97\nentry when close > {entry}\n";

    /// <summary>Everything a retired candidate must keep: its trial, its verdict, its promotion, its capital.</summary>
    sealed record Candidate(Database Db, DatasetRecord Set, CampaignRow Campaign, string VersionId,
        string RunId, PromotionRow Promotion, CouncilBoundaries Boundaries, StrategyStore Strategies);

    /// <summary>
    /// A promoted candidate, optionally with a SUCCESSOR that declares it as parent — which is what the
    /// replacement policy reads when it freezes the default on the boundary row.
    /// </summary>
    static Candidate Given(bool successor = false,
        string successorVerdict = PromotionVerdict.Promoted,
        TimeSpan? window = null)
    {
        var db = TestEnv.NewDb();
        var datasets = new DatasetStore(db);
        var file = Path.Combine(Paths.Data, $"retire-{Guid.NewGuid():n}.csv");
        Directory.CreateDirectory(Paths.Data);
        File.WriteAllText(file, KlineNormaliser.Header + "\n");

        var id = datasets.Record(new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 12, [],
            file, DatasetStore.Sha256(file)!, 1000, Cutoff.AddDays(-300), Cutoff.AddDays(60), 0, [],
            false, 0, 0, 0, At, DatasetState.ACCEPTED, null, []));

        Assert.True(datasets.SetHoldout(id, Cutoff, EvaluationClass.Research).Ok);
        var set = datasets.ById(id)!;

        var opened = new CampaignStore(db).Open("BTCUSDT 1m v1", set, 10, 3, At, exploration: 5);
        Assert.True(opened.Ok, opened.Why);
        var campaign = opened.Campaign!;

        var strategies = new StrategyStore(db);
        var parent = Version(strategies, 103, null);
        var runId = Judged(db, set, campaign, strategies, parent, 0.001m, PromotionVerdict.Promoted);

        // THE REGISTERED TRIAL AND THE CHARGED VERDICT: the history retirement must not touch.
        Assert.True(new CampaignStore(db)
            .RegisterTrial(campaign.Id, parent, runId, EvaluationClass.Research, At).Ok);

        if (successor)
        {
            var child = Version(strategies, 104, parent);
            Judged(db, set, campaign, strategies, child, 0.002m, successorVerdict);

            // THE CHARGED VERDICT IS THE SUCCESSOR'S, so the candidate's own verdict door is still shut
            // rather than already paid for — a question already answered stays answerable after a
            // retirement, and this test is about the one that has not been asked.
            Assert.True(new CampaignStore(db).ChargeVerdict(campaign.Id, child, At).Ok);
        }
        else
        {
            Assert.True(new CampaignStore(db).ChargeVerdict(campaign.Id, parent, At).Ok);
        }

        var promotion = new Promotions(db).All(10).First(p => p.VersionId == parent);

        return new Candidate(db, set, campaign, parent, runId, promotion,
            new CouncilBoundaries(db, () => window ?? CouncilBoundaries.DefaultWindow), strategies);
    }

    static string Version(StrategyStore strategies, int entry, string? parent)
    {
        var program = StrategyParser.Parse(Text(entry)).Program!;
        strategies.RecordVersion(new StrategyVersionRow(
            program.StrategyId, program.Source, program.Canonical, program.Manifest,
            StrategyStore.InterpreterBuild, ParseVerdict.Accepted, program.WarmUpBars,
            Cutoff.AddDays(-1), CouncilRoles.Research, null)
        {
            ParentVersionId = parent
        });
        return program.StrategyId;
    }

    /// <summary>One holdout run and one verdict about it, really in the tables. Returns the run id.</summary>
    static string Judged(Database db, DatasetRecord set, CampaignRow campaign, StrategyStore strategies,
        string versionId, decimal fees, string verdict)
    {
        var model = ExecutionModel.Declare(fees, 0m, 0.0001m, 10_000m).Model!;
        var request = new BacktestRequest(set.Id, set.NormalisedSha256, model, Cutoff, null);
        var runId = request.RunIdFor(versionId);

        strategies.RecordRun(new StrategyRunRow(
            runId, versionId, set.Id, set.NormalisedSha256, Cutoff, null, model.Canonical,
            BacktestOutcome.COMPLETED.ToString(), null, 500, 4, 3, 4, 4, 100, 0, 0,
            12m, 2m, 10m, 3m, "trace-sha", At, Referee.RunRole, null), []);

        new Promotions(db).Record(new PromotionRow(
            "", versionId, campaign.Id, campaign.ScoringPolicySha256, StrategyStore.InterpreterBuild,
            set.Id, set.NormalisedSha256, model.Canonical, Referee.EvaluatorVersion, runId,
            verdict, verdict == PromotionVerdict.Promoted ? PromotionReason.Met : PromotionReason.NoTrade,
            At));

        return runId;
    }

    /// <summary>One more run of the candidate, under a different declared fee, so it is a NEW trial.</summary>
    static string AnotherRun(Candidate c, decimal fees)
    {
        var model = ExecutionModel.Declare(fees, 0m, 0.0001m, 10_000m).Model!;
        var request = new BacktestRequest(c.Set.Id, c.Set.NormalisedSha256, model, Cutoff, null);
        var runId = request.RunIdFor(c.VersionId);

        c.Strategies.RecordRun(new StrategyRunRow(
            runId, c.VersionId, c.Set.Id, c.Set.NormalisedSha256, Cutoff, null, model.Canonical,
            BacktestOutcome.COMPLETED.ToString(), null, 500, 4, 3, 4, 4, 100, 0, 0,
            12m, 2m, 10m, 3m, "trace-sha", At, CouncilRoles.Research, null), []);
        return runId;
    }

    /// <summary>The capital standing behind the candidate, so that clearing it is something to catch.</summary>
    static AllocationRow Allocated(Candidate c)
    {
        var result = new Allocations(c.Db).Record(new AllocationRow(
            "", c.VersionId, c.Promotion.Id, AllocationPolicy.V1, 2m, null, "USD", At, null,
            "the owner allocated capital to this version", At));
        Assert.True(result.Ok, result.Why);
        return result.Allocation!;
    }

    // ---- item 4: the disposition erases nothing ---------------------------------------------------

    /// <summary>
    /// ITEM 4 — A RETIREMENT FENCES THE NEXT ASSIGNMENT AND ERASES NOTHING.
    ///
    /// <para><c>docs/COUNCIL.md</c>:223-224: "retirement stops new assignments and fences attempts, it
    /// never erases history, cancels reconciliation or kills a deployed strategy, which has its own
    /// lifecycle". Every clause of that is asserted here against real rows — the trial, the verdict
    /// charge, the promotion and the standing allocation, each read back after the disposition — and
    /// then the fence itself: a further trial and a further verdict are refused in words.</para>
    ///
    /// <para><b>The mutant.</b> Have the disposition clear the allocation row, which is the obvious
    /// reading of "retire the candidate", and a retired candidate's deployed strategy silently loses the
    /// capital the dispatch gate was enforcing for it.</para>
    /// </summary>
    [Fact]
    public void A_retirement_fences_the_next_assignment_and_rewrites_no_history()
    {
        var c = Given(successor: true, window: TimeSpan.FromMilliseconds(1));
        using var _ = c.Db;
        var allocation = Allocated(c);
        var campaigns = new CampaignStore(c.Db);

        var trialsBefore = campaigns.Trials(c.Campaign.Id);
        var verdictsBefore = campaigns.Verdicts(c.Campaign.Id);
        var promotionsBefore = new Promotions(c.Db).Count;

        var opened = c.Boundaries.OpenRetirement(
            c.VersionId, c.Campaign.Id, "a successor of this candidate has been promoted", At);
        Assert.True(opened.Fresh);
        Assert.Equal(BoundaryKind.Retirement, opened.Row.Kind);
        Assert.Equal(BoundaryDisposition.Retire, opened.Row.DefaultDisposition);

        var settled = Assert.Single(c.Boundaries.ApplyDue(At.AddDays(1)));
        Assert.Equal(BoundaryDisposition.Retire, settled.Disposition);
        Assert.Equal(BoundaryAuthor.Policy, settled.DisposedBy);

        // NOTHING WAS ERASED. The trial rows, the verdict charge and the promotions are what they were.
        Assert.Equal(trialsBefore, campaigns.Trials(c.Campaign.Id));
        Assert.Equal(verdictsBefore, campaigns.Verdicts(c.Campaign.Id));
        Assert.Equal(1, campaigns.TrialsCharged(c.Campaign.Id));
        Assert.Equal(promotionsBefore, new Promotions(c.Db).Count);
        Assert.True(new Promotions(c.Db).Standing(c.VersionId).IsPromoted);

        // AND THE DEPLOYED STRATEGY KEEPS ITS CAPITAL, which is the clause with money behind it.
        var standing = new Allocations(c.Db).StandingFor(c.VersionId, At.AddDays(2));
        Assert.NotNull(standing);
        Assert.Equal(allocation.Id, standing.Allocation.Id);
        Assert.Equal(2m, standing.Allocation.MaxQuantity);

        // WHAT IT DID DO: the next assignment is refused, in words that say what was not taken away.
        Assert.True(new Retirements(c.Db).IsRetired(c.VersionId));

        var refused = campaigns.RegisterTrial(
            c.Campaign.Id, c.VersionId, AnotherRun(c, 0.003m), EvaluationClass.Research, At.AddDays(2));
        Assert.False(refused.Ok);
        Assert.Contains("takes no new research assignment", refused.Why, StringComparison.Ordinal);
        Assert.Contains("Nothing of its record has been removed", refused.Why, StringComparison.Ordinal);

        // AND THE TRIAL IT ALREADY HAS IS STILL ANSWERED Ok, because that one is not a new assignment:
        // a restart must not read as something a retirement took away.
        var again = campaigns.RegisterTrial(
            c.Campaign.Id, c.VersionId, c.RunId, EvaluationClass.Research, At.AddDays(2));
        Assert.True(again.Ok, again.Why);

        // AND NO FURTHER VERDICT IS CHARGED FOR IT EITHER — "stops new assignments", both doors.
        var charged = campaigns.ChargeVerdict(c.Campaign.Id, c.VersionId, At.AddDays(2));
        Assert.False(charged.Ok);
        Assert.Contains("takes no new research assignment", charged.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// ITEM 4 — THE DISPOSITION IS THE POLICY'S AND IS FROZEN AT OPEN: a candidate nothing has replaced
    /// is KEPT, and a keep fences nothing at all.
    ///
    /// <para>"Bounded replacement" (<c>docs/COUNCIL.md</c>:201) is what the default reads: a candidate is
    /// retired when a successor that declared it as parent has been promoted. A default computed when
    /// the clock expires would be a default chosen once the outcome is known, which is why it is written
    /// at open and never afterwards.</para>
    /// </summary>
    [Fact]
    public void A_candidate_that_nothing_has_replaced_is_kept_and_nothing_is_fenced()
    {
        var c = Given(window: TimeSpan.FromMilliseconds(1));
        using var _ = c.Db;

        var opened = c.Boundaries.OpenRetirement(c.VersionId, c.Campaign.Id, "a routine review", At);
        Assert.Equal(BoundaryDisposition.Keep, opened.Row.DefaultDisposition);

        var settled = Assert.Single(c.Boundaries.ApplyDue(At.AddDays(1)));
        Assert.Equal(BoundaryDisposition.Keep, settled.Disposition);

        Assert.False(new Retirements(c.Db).IsRetired(c.VersionId));
        Assert.Null(new Retirements(c.Db).Refusal(c.VersionId));
    }

    /// <summary>
    /// ITEM 4 — A SUCCESSOR WHOSE PROMOTION DOES NOT STAND HAS REPLACED NOTHING.
    ///
    /// <para><c>Promotions.Standing</c> and never "a promotion row exists", the reading
    /// <c>Allocations.Record</c> takes for the same reason: a candidate must not be retired in favour of
    /// a successor TradeAgent judged and refused.</para>
    /// </summary>
    [Fact]
    public void A_successor_that_was_refused_does_not_retire_the_candidate_it_was_derived_from()
    {
        var c = Given(successor: true, successorVerdict: PromotionVerdict.Refused,
            window: TimeSpan.FromMilliseconds(1));
        using var _ = c.Db;

        var opened = c.Boundaries.OpenRetirement(c.VersionId, c.Campaign.Id, "a routine review", At);
        Assert.Equal(BoundaryDisposition.Keep, opened.Row.DefaultDisposition);

        Assert.Single(c.Boundaries.ApplyDue(At.AddDays(1)));
        Assert.False(new Retirements(c.Db).IsRetired(c.VersionId));
    }
}

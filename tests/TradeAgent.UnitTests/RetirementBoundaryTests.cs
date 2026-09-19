using TradeAgent.AgentRuntime;
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
        var standing = new Allocations(c.Db).StandingForLive(c.VersionId, At.AddDays(2));
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

    // ---- item 5: the directors are evaluated too --------------------------------------------------

    /// <summary>One director's assessment, declaring what schema 21 requires: a recommendation and a forecast.</summary>
    static Publication Assessment(string role, string recommendation, string baseline, string text) =>
        Built(role, PublicationKind.Assessment,
            $"{BoundaryDeclaration.RecommendationPrefix} {recommendation}\n"
            + $"{BoundaryDeclaration.BaselinePrefix} {baseline}\n{text}");

    static Publication Built(string role, string kind, string content) => new()
    {
        Id = Publication.IdOf(role, kind, content),
        Role = role,
        Kind = kind,
        Recipients = string.Join(",", CouncilRelay.RecipientsOf(role)),
        CreatedAt = At,
        Content = content
    };

    /// <summary>
    /// ITEM 5 — EACH ASSESSMENT RECORDS WHAT ITS DIRECTOR RECOMMENDED AND WHAT IT FORECAST, and the
    /// forecast is marked against what the app measured at the REGISTERED REVIEW TIME.
    ///
    /// <para><c>docs/COUNCIL.md</c>:225 — "the directors' own recommendations, forecasts and timeliness
    /// are recorded against declared baselines". Before this every boundary settled with two sealed
    /// opinions in it and no row anywhere saying what either of them had been for, so a director could
    /// never be wrong.</para>
    ///
    /// <para><b>The mutant.</b> Read the declared baseline off the boundary's REVIEW measurement instead
    /// of off the submission — one line in <c>DirectorRecord</c> — and every forecast is compared with
    /// itself, so every one of them reads correct.</para>
    /// </summary>
    [Fact]
    public void Each_assessment_records_its_recommendation_and_the_baseline_declared_when_it_was_written()
    {
        var c = Given(successor: true, window: TimeSpan.FromMilliseconds(1));
        using var _ = c.Db;

        var opened = c.Boundaries.OpenRetirement(
            c.VersionId, c.Campaign.Id, "a successor of this candidate has been promoted", At);

        // The candidate's promotion STANDS, so what the app measures at review is `promoted`. One
        // director forecasts that and one forecasts the opposite, both BEFORE the review.
        Assert.True(c.Boundaries.Assess(Assessment(
            CouncilRoles.Research, BoundaryDisposition.Retire, PromotionState.Promoted,
            "the successor is better on every clause."), At).Ok);
        Assert.True(c.Boundaries.Assess(Assessment(
            CouncilRoles.Operations, BoundaryDisposition.Keep, PromotionState.Unjudged,
            "I expect this evidence to be withdrawn."), At).Ok);

        var settled = Assert.Single(c.Boundaries.ApplyDue(At.AddDays(1)));
        Assert.Equal(BoundaryDisposition.Retire, settled.Disposition);
        Assert.Equal(PromotionState.Promoted, settled.ReviewBaseline);

        var records = c.Boundaries.Records();
        Assert.Equal(2, records.Count);

        var research = Assert.Single(records, r => r.Role == CouncilRoles.Research);
        Assert.Equal(BoundaryDisposition.Retire, research.Recommendation);
        Assert.Equal(true, research.Agreed);
        Assert.Equal(PromotionState.Promoted, research.Declared);
        Assert.Equal(PromotionState.Promoted, research.Measured);
        Assert.Equal(true, research.Held);
        Assert.False(research.Late);
        Assert.False(research.Silent);

        var operations = Assert.Single(records, r => r.Role == CouncilRoles.Operations);
        Assert.Equal(BoundaryDisposition.Keep, operations.Recommendation);
        Assert.Equal(false, operations.Agreed);
        Assert.Equal(PromotionState.Unjudged, operations.Declared);

        // THE FORECAST IS MARKED AGAINST WHAT WAS MEASURED, NOT AGAINST ITSELF.
        Assert.Equal(PromotionState.Promoted, operations.Measured);
        Assert.Equal(false, operations.Held);

        Assert.Contains("forecast unjudged, measured promoted — did not hold", operations.Line(),
            StringComparison.Ordinal);
        Assert.Equal(opened.Row.Id, operations.BoundaryId);
    }

    /// <summary>
    /// ITEM 5 — AN ASSESSMENT THAT DECLARES NO RECOMMENDATION IS REFUSED, having published nothing and
    /// bought nobody a turn.
    ///
    /// <para>A recommendation the app had to infer from prose would be the app's reading of a director
    /// rather than the director's own word, and a record built on it would be a record of the app's
    /// guesses. So it is a line of its own, from a closed vocabulary, or the assessment is not
    /// published — the shape every other refusal on this class has.</para>
    /// </summary>
    [Fact]
    public void An_assessment_that_declares_no_recommendation_is_refused_and_nothing_is_published()
    {
        var c = Given(successor: true, window: TimeSpan.FromMilliseconds(1));
        using var _ = c.Db;
        c.Boundaries.OpenRetirement(c.VersionId, c.Campaign.Id, "a routine review", At);

        var refused = c.Boundaries.Assess(
            Built(CouncilRoles.Research, PublicationKind.Assessment, "I read it as thin."), At);

        Assert.False(refused.Ok);
        Assert.Contains("RECOMMENDATION:", refused.Why, StringComparison.Ordinal);
        Assert.Empty(new PublicationStore(c.Db).By(CouncilRoles.Research));
        Assert.Empty(c.Boundaries.Submissions(
            BoundaryIds.Of(BoundaryKind.Retirement, c.VersionId, c.Campaign.Id)));

        // AND ONE THAT DECLARES A RECOMMENDATION BUT NO FORECAST, where a forecast is measurable.
        var half = c.Boundaries.Assess(Built(CouncilRoles.Research, PublicationKind.Assessment,
            $"{BoundaryDeclaration.RecommendationPrefix} {BoundaryDisposition.Keep}\nI read it as thin."),
            At);

        Assert.False(half.Ok);
        Assert.Contains("BASELINE:", half.Why, StringComparison.Ordinal);
        Assert.Empty(new PublicationStore(c.Db).By(CouncilRoles.Research));
    }

    /// <summary>
    /// ITEM 5 — A DIRECTOR THAT NEVER ANSWERED IS IN THE RECORD, as silent.
    ///
    /// <para>Timeliness is one of the three things :225 asks to be recorded, and a list holding only the
    /// assessments that arrived would be a record of the diligent. Saying nothing is the cheapest way to
    /// hold up a decision, so it is the thing that most needs a line.</para>
    /// </summary>
    [Fact]
    public void A_director_that_never_answered_is_in_the_record_as_silent()
    {
        var c = Given(successor: true, window: TimeSpan.FromMilliseconds(1));
        using var _ = c.Db;
        c.Boundaries.OpenRetirement(c.VersionId, c.Campaign.Id, "a routine review", At);

        Assert.True(c.Boundaries.Assess(Assessment(
            CouncilRoles.Research, BoundaryDisposition.Retire, PromotionState.Promoted,
            "the successor is better."), At).Ok);

        Assert.Single(c.Boundaries.ApplyDue(At.AddDays(1)));

        var silent = Assert.Single(c.Boundaries.Records(), r => r.Role == CouncilRoles.Operations);
        Assert.True(silent.Silent);
        Assert.Null(silent.Recommendation);
        Assert.Null(silent.Agreed);
        Assert.Contains("no assessment was submitted before code settled it", silent.Line(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// ITEM 5 — SECTION 8 OF THE OWNER'S REPORT NAMES EACH DIRECTOR'S RECORD AGAINST ITS BASELINE.
    ///
    /// <para>It goes in "measured by TradeAgent" and not in "claimed by an agent" because every part of
    /// it was frozen by the app: the recommendation and the forecast when the assessment was sealed, the
    /// disposition and the measurement when code settled the boundary. No director's account of its own
    /// performance is anywhere near it.</para>
    /// </summary>
    [Fact]
    public async Task Section_eight_names_each_directors_record_against_the_baseline_it_declared()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        var boundaries = new CouncilBoundaries(db, () => TimeSpan.FromMilliseconds(1));

        boundaries.Open(BoundaryKind.Promotion, "version-a", 1, BoundaryDisposition.Hold,
            "holdout run 9f3c under campaign 1", At);

        Assert.True(boundaries.Assess(Assessment(
            CouncilRoles.Research, BoundaryDisposition.Deploy, PromotionState.Promoted,
            "the evidence is sufficient."), At).Ok);
        Assert.True(boundaries.Assess(Assessment(
            CouncilRoles.Operations, BoundaryDisposition.Hold, PromotionState.Unjudged,
            "the sample is one campaign wide."), At).Ok);

        Assert.Single(boundaries.ApplyDue(At.AddDays(1)));

        var report = gw.Reports.Compose(DateTimeOffset.Now);
        var lines = string.Join("\n", report.Research.AppMetrics);

        Assert.Contains("recommended deploy, code applied hold — differed", lines, StringComparison.Ordinal);
        Assert.Contains("recommended hold, code applied hold — agreed", lines, StringComparison.Ordinal);
        Assert.Contains("forecast promoted, measured unjudged — did not hold", lines, StringComparison.Ordinal);
        Assert.Contains("forecast unjudged, measured unjudged — held", lines, StringComparison.Ordinal);
    }
}

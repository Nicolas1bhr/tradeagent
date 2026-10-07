using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Features;
using TradeAgent.Core.Strategy;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// EVIDENCE THAT STAYS TRUE (<c>U-language-v2a</c> item 4): a verdict on a version that reads features stands only while
/// its frozen text still reads as that version, and no capital may stand on it while any input it reads is research-only
/// — which every tape source is today.
/// </summary>
public class FeatureProgramStandingTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset At = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Cutoff = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A candle source this build has no reading of, recorded first-party: bars that confer, on their own.</summary>
    const string Conferring = "a-first-party-candle-source";

    sealed record Judged(DatasetRecord Set, string VersionId, PromotionRow Promotion);

    /// <summary>
    /// A VERSION THAT REALLY CARRIES A VERDICT IN THIS DATABASE — the dataset (first-party, so its bars confer), the
    /// holdout, the campaign, the version row, the holdout run and the promotion — as <c>DataLicenceTests</c> writes one.
    /// <paramref name="versionId"/> and <paramref name="canonical"/>, when given, are what an earlier build recorded the
    /// text under; otherwise this build's.
    /// </summary>
    static Judged Promote(Database db, string text, string verdict = PromotionVerdict.Promoted,
        string? versionId = null, string? canonical = null)
    {
        var datasets = new DatasetStore(db);
        var file = Path.Combine(Paths.Data, $"feature-standing-{Guid.NewGuid():n}.csv");
        Directory.CreateDirectory(Paths.Data);
        File.WriteAllText(file, KlineNormaliser.Header + "\n");

        var id = datasets.Record(new DatasetRecord(
            0, Conferring, "BTCUSDT", BinanceArchive.Interval, $"v{datasets.All().Count + 1}", 12, 12, [],
            file, DatasetStore.Sha256(file)!, 1000, Cutoff.AddDays(-300), Cutoff.AddDays(60), 0, [],
            false, 0, 0, 0, At, DatasetState.ACCEPTED, null, [])
        {
            Licence = new DatasetLicence(DataLicence.FirstParty, "terms of the test's own", "v1", "2026-10-01")
        });
        Assert.True(datasets.SetHoldout(id, Cutoff, EvaluationClass.Research).Ok);
        var set = datasets.ById(id)!;

        var campaign = new CampaignStore(db).Open($"BTCUSDT {Guid.NewGuid():n}", set, 10, 3, At);
        Assert.True(campaign.Ok, campaign.Why);

        var program = StrategyParser.Parse(text).Program!;
        var recorded = versionId ?? program.StrategyId;
        var strategies = new StrategyStore(db);
        strategies.RecordVersion(new StrategyVersionRow(
            recorded, program.Source, canonical ?? program.Canonical, program.Manifest, StrategyStore.InterpreterBuild,
            ParseVerdict.Accepted, program.WarmUpBars, Cutoff.AddDays(-1), null, null)
        {
            Timeframe = program.Freshness?.Timeframe,
            DataFreshness = program.Freshness?.DataFreshness,
            MaxDecisionAge = program.Freshness?.MaxDecisionAge
        });

        var model = ExecutionModel.Declare(0.001m, 0m, 0.0001m, 10_000m).Model!;
        var runId = new BacktestRequest(set.Id, set.NormalisedSha256, model, Cutoff, null) { FeaturesSha256 = "values" }
            .RunIdFor(recorded);
        strategies.RecordRun(new StrategyRunRow(
            runId, recorded, set.Id, set.NormalisedSha256, Cutoff, null, model.Canonical,
            BacktestOutcome.COMPLETED.ToString(), null, 500, 4, 3, 4, 4, 100, 0, 0,
            12m, 2m, 10m, 3m, "trace-sha", At, Referee.RunRole, null), []);

        var paper = verdict == PromotionVerdict.PaperEligible;
        var promotion = new Promotions(db).Record(new PromotionRow(
            "", recorded, campaign.Campaign!.Id, CampaignPolicy.Sha256Of(paper ? CampaignPolicy.PaperV1 : CampaignPolicy.V1),
            StrategyStore.InterpreterBuild, set.Id, set.NormalisedSha256, model.Canonical, Referee.EvaluatorVersion,
            runId, verdict, paper ? PromotionReason.MetOnHistory : PromotionReason.Met, At)
        {
            Timeframe = program.Freshness?.Timeframe,
            DataFreshness = program.Freshness?.DataFreshness,
            MaxDecisionAge = program.Freshness?.MaxDecisionAge
        });

        return new Judged(set, recorded, promotion);
    }

    static AllocationRow RowFor(Judged j) =>
        new("", j.VersionId, j.Promotion.Id, AllocationPolicy.V1, 1m, null, "USD", At, null,
            "the owner allocated capital to this version", At);

    static string Text => FeatureProgramGrammarTests.FundingProgram();

    /// <summary>The same program reading the close instead of funding: a v1 twin.</summary>
    const string Twin =
        "instrument BTCUSDT\nbars 1h\ntimeframe 1h\ndata_freshness 2h\nmax_decision_age 10m\nsize fixed 1\nstop percent 2\n"
        + "exit when close < 90\nentry when close > 110\n";

    /// <summary>
    /// (m) A FEATURE VERSION READ AS ANOTHER PROGRAM IS WITHDRAWN. A promoted version that reads funding stands while its
    /// frozen text reads as itself. The same text recorded under the id an earlier feature semantics hashed it to — the
    /// funding spec's id under <c>features=0</c>, the program's under that — stands promoted on nothing this build can
    /// reproduce: it is INVALIDATED in words naming both ids and <c>features=1</c>, for paper-eligible as for promoted,
    /// exactly as the runner and the referee already refuse it; and a text this build no longer parses is invalidated
    /// too. The stored manifest is this build's in both, so only re-reading the text can see it.
    ///
    /// <para><b>RED before item 4</b>: both stood — <c>Standing</c> compared the manifest and never re-read a text.</para>
    /// </summary>
    [Fact]
    public void A_feature_version_read_as_another_program_is_withdrawn()
    {
        using var db = TestEnv.NewDb();
        var promotions = new Promotions(db);
        var program = StrategyParser.Parse(Text).Program!;

        var stands = Promote(db, Text);
        Assert.Equal(PromotionState.Promoted, promotions.Standing(stands.VersionId).State);

        // AS AN EARLIER BUILD WOULD HAVE IDENTIFIED IT: the feature's id under another semantics, and the program's.
        var spec = Assert.Single(program.Features).Spec;
        var earlierFeature = Sha256Hex.Of(spec.Canonical + "\n" + "features=0");
        var earlierCanonical = program.Canonical.Replace(spec.Id, earlierFeature, StringComparison.Ordinal);
        var earlierId = Sha256Hex.Of($"{earlierCanonical}\n{program.Parameters}\n{program.Manifest}");
        Assert.NotEqual(program.StrategyId, earlierId);

        foreach (var verdict in new[] { PromotionVerdict.Promoted, PromotionVerdict.PaperEligible })
        {
            var other = Promote(db, Text, verdict, earlierId, earlierCanonical);
            var standing = promotions.Standing(other.VersionId);
            log.WriteLine($"{verdict}: {standing.State} — {standing.Why}");

            Assert.Equal(PromotionState.Invalidated, standing.State);
            Assert.False(standing.IsPromoted);
            Assert.False(standing.IsPaperEligible);
            Assert.Equal(
                $"the frozen text of version {earlierId[..12]}, which reads features, now reads as {program.StrategyId[..12]}: "
                + "a feature it reads — its spec, or what this build computes from one (features=1 now) — is not what that "
                + "id was hashed under, so the evidence is about a program that is no longer this text.",
                standing.Why);
            Assert.NotNull(standing.LiveRefusal);
        }

        // A TEXT THIS BUILD NO LONGER PARSES: recorded with a spec naming a source no row of this build records.
        var gone = Text.Replace("binance-um-premium", "binance-um-retired", StringComparison.Ordinal);
        var goneId = Sha256Hex.Of("a version an earlier build accepted");
        var refused = Promote(db, Text, versionId: goneId);
        new StrategyStore(db).RecordVersion(new StrategyVersionRow(
            Sha256Hex.Of("another"), gone, program.Canonical, program.Manifest, StrategyStore.InterpreterBuild,
            ParseVerdict.Accepted, 1, Cutoff, null, null));
        db.Write(_ =>
        {
            using var c = db.Cmd("UPDATE strategy_version SET source=$s WHERE id=$id", ("$s", gone), ("$id", goneId));
            return c.ExecuteNonQuery();
        });
        var lost = promotions.Standing(refused.VersionId);
        log.WriteLine(lost.Why);
        Assert.Equal(PromotionState.Invalidated, lost.State);
        Assert.StartsWith($"the frozen text of version {goneId[..12]}, which reads features, no longer parses in this build — line 6:",
            lost.Why, StringComparison.Ordinal);

        // AND A V1 VERSION'S STANDING IS WHAT IT WAS.
        Assert.Equal(PromotionState.Promoted, promotions.Standing(Promote(db, Twin).VersionId).State);
    }

    /// <summary>
    /// (n) RESEARCH-ONLY FEATURE INPUTS CONFER NO LIVE AUTHORITY. Over bars that confer — a first-party dataset — a
    /// promoted v1 version may hold capital and a promoted version that reads funding may not: its standing is promoted
    /// and its live refusal is the feature's, <c>FeatureLicence.LiveRefusal</c>'s sentence over its one input, which no
    /// licence reading of this build confers; the capital ledger refuses it in those words and writes nothing. The
    /// verdict is not re-judged. Paper never reads it: a paper-eligible feature version authorises on paper.
    ///
    /// <para><b>RED before item 4</b>: the feature version's live refusal was null and its capital was allocated.</para>
    /// </summary>
    [Fact]
    public void Research_only_feature_inputs_confer_no_live_authority()
    {
        using var db = TestEnv.NewDb();
        var promotions = new Promotions(db);
        var allocations = new Allocations(db);
        var program = StrategyParser.Parse(Text).Program!;

        var twin = Promote(db, Twin);
        Assert.Null(promotions.Standing(twin.VersionId).LiveRefusal);
        Assert.True(allocations.Record(RowFor(twin)).Ok);

        var reads = Promote(db, Text);
        var standing = promotions.Standing(reads.VersionId);
        var sentence = FeatureLicence.LiveRefusal(program.Features[0].Spec, new DataLicences(db).Newest);
        log.WriteLine(sentence);
        Assert.True(standing.IsPromoted);
        Assert.Equal(sentence, standing.LiveRefusal);
        Assert.Equal(
            $"feature {program.Features[0].Spec.Id[..12]} reads binance-um-premium, whose terms TradeAgent holds no licence "
            + "reading of; only first-party or commercial-ok data confers live eligibility, so no capital may stand on it — "
            + "it is research context, and backtests and paper go on.",
            sentence);

        var refused = allocations.Record(RowFor(reads));
        log.WriteLine(refused.Why);
        Assert.False(refused.Ok, "capital was allocated to a version that reads a research-only feature");
        Assert.Empty(allocations.For(reads.VersionId));
        Assert.Contains("REFUSED FOR LIVE", refused.Why, StringComparison.Ordinal);
        Assert.EndsWith(sentence!, refused.Why, StringComparison.Ordinal);
        Assert.True(promotions.Standing(reads.VersionId).IsPromoted, "refusing capital re-judged the verdict");

        var paper = Promote(db, Text.Replace("stop percent 2", "stop percent 3", StringComparison.Ordinal), PromotionVerdict.PaperEligible);
        var onPaper = promotions.Standing(paper.VersionId);
        Assert.True(onPaper.IsPaperEligible);
        Assert.NotNull(onPaper.LiveRefusal);
        var envelope = new PaperEnvelopeRow("envelope", "paper", "SIM-001", "BTCUSDT", "USD", 5m, null, 1, At,
            At.AddDays(30), "the owner's paper grant", null);
        Assert.True(new AllocationStanding(new AllocationRow("", paper.VersionId, paper.Promotion.Id, AllocationPolicy.V1,
                1m, null, "USD", At, null, "the app's paper policy", At) { Scope = AllocationScope.Paper, EnvelopeId = envelope.Id },
                onPaper, envelope).Authorises,
            "a paper-eligible feature version did not authorise on paper");
    }
}

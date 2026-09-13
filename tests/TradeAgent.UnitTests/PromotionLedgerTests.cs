using System.Globalization;
using System.Reflection;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEMS 1 AND 3 — THE PROMOTION RECORD, AND THE ASSUMPTION THAT INVALIDATES IT.
///
/// <para><b>What was wrong before this.</b> <c>strategy_version</c> had no state: nothing in this
/// product said a version was promoted, refused or invalidated, and the binding material
/// `docs/COUNCIL.md` rule 9 demands — "code, parameters, dependencies, data, evaluator,
/// execution-and-cost model and scoring-policy versions" — was scattered across one <c>strategy_run</c>
/// row that nothing compared with anything.</para>
///
/// <para><b>The two mutants this class exists to catch.</b> The id minted from the CLOCK, so one version
/// promoted twice becomes two records of one judgement. And invalidation checked against the dataset's
/// CURRENT hash on both sides instead of against the hash the run actually read, so a dataset collected
/// again under a promotion revalidates it.</para>
/// </summary>
public class PromotionLedgerTests
{
    static readonly DateTimeOffset At = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    static readonly DateTimeOffset Cutoff = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    const string ProgramText = "instrument BTCUSDT\nsize fixed 1\nexit when close < 97\nentry when close > 103\n";

    /// <summary>
    /// EVERYTHING A PROMOTION POINTS AT, really in the database: the held-back dataset, the campaign
    /// opened over it, the version and the holdout run. The table's foreign keys mean a test cannot
    /// invent any of them, which is the point — a promotion that referenced nothing would prove nothing.
    /// </summary>
    sealed record Judged(Database Db, DatasetRecord Set, CampaignRow Campaign, string VersionId, string RunId);

    static Judged Given(string pair = "BTCUSDT", string evaluationClass = EvaluationClass.Research)
    {
        var db = TestEnv.NewDb();
        var datasets = new DatasetStore(db);
        var file = Path.Combine(Paths.Data, $"promotion-{Guid.NewGuid():n}.csv");
        Directory.CreateDirectory(Paths.Data);
        File.WriteAllText(file, KlineNormaliser.Header + "\n");

        var id = datasets.Record(new DatasetRecord(
            0, BinanceArchive.Source, pair, BinanceArchive.Interval, "v1", 12, 12, [],
            file, DatasetStore.Sha256(file)!, 1000, Cutoff.AddDays(-300), Cutoff.AddDays(60), 0, [],
            false, 0, 0, 0, At, DatasetState.ACCEPTED, null, []));

        Assert.True(datasets.SetHoldout(id, Cutoff, evaluationClass).Ok);
        var set = datasets.ById(id)!;

        var opened = new CampaignStore(db).Open($"{pair} 1m v1", set, 10, 3, At);
        Assert.True(opened.Ok, opened.Why);

        var program = StrategyParser.Parse(ProgramText).Program!;
        var strategies = new StrategyStore(db);
        strategies.RecordVersion(new StrategyVersionRow(
            program.StrategyId, program.Source, program.Canonical, program.Manifest,
            StrategyStore.InterpreterBuild, ParseVerdict.Accepted, program.WarmUpBars,
            Cutoff.AddDays(-1), null, null));

        var request = new BacktestRequest(set.Id, set.NormalisedSha256, Model, Cutoff, null);
        var runId = request.RunIdFor(program.StrategyId);
        strategies.RecordRun(new StrategyRunRow(
            runId, program.StrategyId, set.Id, set.NormalisedSha256, Cutoff, null, Model.Canonical,
            BacktestOutcome.COMPLETED.ToString(), null, 500, 4, 3, 4, 4, 100, 0, 0,
            12m, 2m, 10m, 3m, "trace-sha", At, Referee.RunRole, null), []);

        return new Judged(db, set, opened.Campaign!, program.StrategyId, runId);
    }

    static ExecutionModel Model => ExecutionModel.Declare(0.001m, 0m, 0.0001m, 10_000m).Model!;

    /// <summary>The nine facts of one promotion, as the referee would have bound them.</summary>
    static PromotionRow RowFor(Judged j, string verdict = PromotionVerdict.Promoted,
        string reason = PromotionReason.Met) =>
        new("", j.VersionId, j.Campaign.Id, j.Campaign.ScoringPolicySha256, StrategyStore.InterpreterBuild,
            j.Set.Id, j.Set.NormalisedSha256, Model.Canonical, Referee.EvaluatorVersion, j.RunId,
            verdict, reason, At);

    // ---- item 1: the record -----------------------------------------------------------------------

    /// <summary>
    /// A PROMOTION IS THE HASH OF THE NINE FACTS IT BINDS, AND OF NOTHING ELSE.
    ///
    /// <para>Each of the nine is varied on its own and each one moves the id, which is what "bound to"
    /// means: a verdict computed under a different fee, a different interpreter or a different scoring
    /// policy is a different verdict and cannot occupy the row of the one before it.</para>
    /// </summary>
    [Fact]
    public void A_promotion_is_addressed_by_the_nine_facts_it_binds()
    {
        var j = Given();
        using var _1 = j.Db;
        var promotions = new Promotions(j.Db);

        var written = promotions.Record(RowFor(j));

        Assert.Equal(64, written.Id.Length);
        Assert.Equal(PromotionRow.IdOf(j.VersionId, j.Campaign.Id, j.Campaign.ScoringPolicySha256,
            StrategyStore.InterpreterBuild, j.Set.Id, j.Set.NormalisedSha256, Model.Canonical,
            Referee.EvaluatorVersion, j.RunId), written.Id);

        var baseline = written.Id;
        var varied = new[]
        {
            PromotionRow.IdOf("another-version", j.Campaign.Id, j.Campaign.ScoringPolicySha256,
                StrategyStore.InterpreterBuild, j.Set.Id, j.Set.NormalisedSha256, Model.Canonical,
                Referee.EvaluatorVersion, j.RunId),
            PromotionRow.IdOf(j.VersionId, j.Campaign.Id + 1, j.Campaign.ScoringPolicySha256,
                StrategyStore.InterpreterBuild, j.Set.Id, j.Set.NormalisedSha256, Model.Canonical,
                Referee.EvaluatorVersion, j.RunId),
            PromotionRow.IdOf(j.VersionId, j.Campaign.Id, "a-different-policy",
                StrategyStore.InterpreterBuild, j.Set.Id, j.Set.NormalisedSha256, Model.Canonical,
                Referee.EvaluatorVersion, j.RunId),
            PromotionRow.IdOf(j.VersionId, j.Campaign.Id, j.Campaign.ScoringPolicySha256,
                "app=0.0.1;language=1", j.Set.Id, j.Set.NormalisedSha256, Model.Canonical,
                Referee.EvaluatorVersion, j.RunId),
            PromotionRow.IdOf(j.VersionId, j.Campaign.Id, j.Campaign.ScoringPolicySha256,
                StrategyStore.InterpreterBuild, j.Set.Id + 1, j.Set.NormalisedSha256, Model.Canonical,
                Referee.EvaluatorVersion, j.RunId),
            PromotionRow.IdOf(j.VersionId, j.Campaign.Id, j.Campaign.ScoringPolicySha256,
                StrategyStore.InterpreterBuild, j.Set.Id, "other-bytes", Model.Canonical,
                Referee.EvaluatorVersion, j.RunId),
            PromotionRow.IdOf(j.VersionId, j.Campaign.Id, j.Campaign.ScoringPolicySha256,
                StrategyStore.InterpreterBuild, j.Set.Id, j.Set.NormalisedSha256,
                ExecutionModel.Declare(0.002m, 0m, 0.0001m, 10_000m).Model!.Canonical,
                Referee.EvaluatorVersion, j.RunId),
            PromotionRow.IdOf(j.VersionId, j.Campaign.Id, j.Campaign.ScoringPolicySha256,
                StrategyStore.InterpreterBuild, j.Set.Id, j.Set.NormalisedSha256, Model.Canonical,
                "evaluator=0", j.RunId),
            PromotionRow.IdOf(j.VersionId, j.Campaign.Id, j.Campaign.ScoringPolicySha256,
                StrategyStore.InterpreterBuild, j.Set.Id, j.Set.NormalisedSha256, Model.Canonical,
                Referee.EvaluatorVersion, "another-run")
        };

        Assert.Equal(9, varied.Length);
        Assert.DoesNotContain(baseline, varied);
        Assert.Equal(9, varied.Distinct(StringComparer.Ordinal).Count());

        // And the row really is on the table with all nine of them on it.
        var row = promotions.ById(baseline)!;
        Assert.Equal(j.VersionId, row.VersionId);
        Assert.Equal(j.Campaign.Id, row.CampaignId);
        Assert.Equal(j.Campaign.ScoringPolicySha256, row.ScoringPolicySha256);
        Assert.Equal(StrategyStore.InterpreterBuild, row.InterpreterBuild);
        Assert.Equal(j.Set.Id, row.HoldoutDatasetId);
        Assert.Equal(j.Set.NormalisedSha256, row.HoldoutDatasetSha256);
        Assert.Equal(Model.Canonical, row.ExecutionModel);
        Assert.Equal(Referee.EvaluatorVersion, row.EvaluatorVersion);
        Assert.Equal(j.RunId, row.HoldoutRunId);
        Assert.Equal(PromotionVerdict.Promoted, row.Verdict);
        Assert.Equal(PromotionReason.Met, row.Reason);
        Assert.Equal(At, row.At);
    }

    /// <summary>
    /// ONE VERSION PROMOTED TWICE ON THE SAME EVIDENCE IS ONE RECORD — this is the mutant.
    ///
    /// <para>An id minted from the clock would write a second row for the same judgement, and a reader
    /// would have two answers with nothing to choose between them. The second call is deliberately made
    /// at a LATER instant and with a DIFFERENT verdict: the first answer stands, whole, because
    /// <c>ON CONFLICT DO NOTHING</c> is the immutability — not a check the caller has to remember.</para>
    /// </summary>
    [Fact]
    public void One_version_promoted_twice_on_the_same_evidence_is_one_record()
    {
        var j = Given();
        using var _1 = j.Db;
        var promotions = new Promotions(j.Db);

        var first = promotions.Record(RowFor(j));
        var again = promotions.Record(RowFor(j, PromotionVerdict.Refused, PromotionReason.NotProfitable)
            with { At = At.AddHours(5) });

        Assert.Equal(first.Id, again.Id);
        Assert.Single(promotions.For(j.VersionId));
        Assert.Equal(1, promotions.Count);

        // THE FIRST ANSWER STANDS, with its own instant and its own verdict.
        Assert.Equal(PromotionVerdict.Promoted, again.Verdict);
        Assert.Equal(PromotionReason.Met, again.Reason);
        Assert.Equal(At, again.At);
        Assert.Equal(PromotionVerdict.Promoted, promotions.ById(first.Id)!.Verdict);
    }

    /// <summary>
    /// THE LEDGER HAS ONE WRITE AND IT ONLY EVER INSERTS. No update, no delete — not "no pipe op": no
    /// METHOD, so there is nothing for a later caller to reach for.
    ///
    /// <para>A promotion is what a version is allowed to trade the owner's money on. A record its
    /// subject could edit, or that the app could quietly restate once the outcome was known, is not a
    /// record — `docs/COUNCIL.md`:212 names precommitment as one of the four things that cannot be
    /// recovered afterwards.</para>
    /// </summary>
    [Fact]
    public void The_promotion_ledger_exposes_one_write_and_it_only_inserts()
    {
        var writes = typeof(Promotions)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .Where(n => n is not ("ById" or "For" or "All" or "Standing"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Record"], writes);
        Assert.DoesNotContain(typeof(Promotions).GetMethods(), m =>
            m.Name.Contains("Update", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Delete", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Remove", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Invalidate", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// THE TABLE IS AT SCHEMA 15 AND CARRIES EXACTLY THE THIRTEEN COLUMNS, IN THIS ORDER — nine facts,
    /// the verdict, the reason, the instant, and the id they hash to.
    ///
    /// <para>A FLOOR rather than an equality, so an additive migration above this one does not have to
    /// edit a test about this rung. The column list is pinned because the ABSENCE of an
    /// <c>invalidated</c> column is the design: a column would make a version's truth depend on a sweep
    /// having run, and the sweep that did not run is exactly what the money path must not get wrong.</para>
    /// </summary>
    [Fact]
    public void The_promotion_table_is_schema_fifteen_and_has_no_invalidated_column()
    {
        var j = Given();
        using var _1 = j.Db;

        Assert.True(Versions.DatabaseSchemaVersion >= 15,
            "the promotion record needs schema 15 or later; this build says "
            + Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture));

        Assert.Equal(
            Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture),
            j.Db.Read(_ =>
            {
                using var c = j.Db.Cmd("SELECT value FROM meta WHERE key='schema_version'");
                return c.ExecuteScalar() as string;
            }));

        var columns = j.Db.Read(_ =>
        {
            using var c = j.Db.Cmd("SELECT name FROM pragma_table_info('strategy_promotion')");
            var names = new List<string>();
            using var r = c.ExecuteReader();
            while (r.Read()) names.Add(r.GetString(0));
            return names;
        });

        Assert.Equal([
            "id", "version_id", "campaign_id", "scoring_policy_sha256", "interpreter_build",
            "holdout_dataset_id", "holdout_dataset_sha256", "execution_model", "evaluator_version",
            "holdout_run_id", "verdict", "reason", "at"
        ], columns);
    }

    // ---- item 3: a changed assumption invalidates the evidence ------------------------------------

    /// <summary>
    /// A DATASET REJECTED AFTER THE FACT INVALIDATES THE PROMOTION THAT RESTED ON IT, and the row is
    /// not touched to do it.
    ///
    /// <para>`docs/COUNCIL.md`:35: "a changed assumption invalidates the evidence that rested on it".
    /// The dataset going REJECTED means TradeAgent no longer vouches for the bars the verdict was
    /// computed over — so the verdict is still on the table, still says what it said, and no longer
    /// stands.</para>
    /// </summary>
    [Fact]
    public void A_dataset_rejected_afterwards_invalidates_the_promotion_that_rested_on_it()
    {
        var j = Given();
        using var _1 = j.Db;
        var promotions = new Promotions(j.Db);
        var written = promotions.Record(RowFor(j));

        Assert.Equal(PromotionState.Promoted, promotions.Standing(j.VersionId).State);

        new DatasetStore(j.Db).Reject(j.Set.Id, "a raw archive file changed under it");

        var standing = promotions.Standing(j.VersionId);
        Assert.Equal(PromotionState.Invalidated, standing.State);
        Assert.False(standing.IsPromoted);
        Assert.Contains("has since been REJECTED", standing.Why, StringComparison.Ordinal);
        Assert.Contains("a raw archive file changed under it", standing.Why, StringComparison.Ordinal);

        // THE ROW IS UNTOUCHED. Invalidation is computed at read time and never written back: a record
        // the app restates once the outcome is known is not a record.
        var row = promotions.ById(written.Id)!;
        Assert.Equal(PromotionVerdict.Promoted, row.Verdict);
        Assert.Equal(PromotionReason.Met, row.Reason);
        Assert.Equal(written, row);
    }

    /// <summary>
    /// A DATASET COLLECTED AGAIN INVALIDATES THE PROMOTION — this is the mutant.
    ///
    /// <para>The check is the sha the RUN read against the sha the ledger holds now. Comparing the
    /// ledger's current sha with itself is a check that can never fail, and a version would go on
    /// trading the owner's money on the evidence of months that are no longer the ones under that id.</para>
    ///
    /// <para>No writer in this build moves <c>dataset.normalised_sha256</c> — a re-collection writes a
    /// new row — so the test writes past the store to put the ledger in the state a collector that
    /// replaced a dataset in place would leave it in. That is the same thing
    /// <c>CampaignLedgerTests</c> does to prove the partial unique index is what holds: the guard has
    /// to be the arithmetic, not the absence of a caller.</para>
    /// </summary>
    [Fact]
    public void A_dataset_collected_again_under_the_same_id_invalidates_the_promotion()
    {
        var j = Given();
        using var _1 = j.Db;
        var promotions = new Promotions(j.Db);
        promotions.Record(RowFor(j));

        Assert.Equal(PromotionState.Promoted, promotions.Standing(j.VersionId).State);

        j.Db.Write(_ =>
        {
            using var c = j.Db.Cmd("UPDATE dataset SET normalised_sha256=$sha WHERE id=$id",
                ("$sha", "collected-again-0000"), ("$id", j.Set.Id));
            return c.ExecuteNonQuery();
        });

        var standing = promotions.Standing(j.VersionId);
        Assert.Equal(PromotionState.Invalidated, standing.State);
        Assert.Contains("the months have been collected again", standing.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// A PROMOTION TAKEN BY AN EARLIER INTERPRETER, OR UNDER AN EARLIER SCORING POLICY, DOES NOT STAND.
    ///
    /// <para>Both are ordinary rather than adversarial: the interpreter build moves with every release,
    /// and the scoring policy's text is a constant somebody can edit. Rule 9 binds promotion to both, so
    /// a build that changed either is a build whose evidence has to be recollected — the row is written
    /// with the facts of its own time, and the comparison is against this build's.</para>
    /// </summary>
    [Fact]
    public void A_promotion_from_another_interpreter_or_another_policy_does_not_stand()
    {
        var j = Given();
        using var _1 = j.Db;
        var promotions = new Promotions(j.Db);

        promotions.Record(RowFor(j) with { InterpreterBuild = "app=0.0.1;language=1" });
        var old = promotions.Standing(j.VersionId);
        Assert.Equal(PromotionState.Invalidated, old.State);
        Assert.Contains("app=0.0.1;language=1", old.Why, StringComparison.Ordinal);
        Assert.Contains("the program may not mean the same thing", old.Why, StringComparison.Ordinal);

        var second = Given("ETHUSDT");
        using var _2 = second.Db;
        var others = new Promotions(second.Db);
        others.Record(RowFor(second) with { ScoringPolicySha256 = new string('a', 64) });
        var moved = others.Standing(second.VersionId);
        Assert.Equal(PromotionState.Invalidated, moved.State);
        Assert.Contains("the standard has changed", moved.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE OTHER TWO ANSWERS: a version the referee refused reads <c>refused</c> with its reason in the
    /// owner's words, and a version nobody has asked about reads <c>unjudged</c> — which is a different
    /// fact from a refusal and is said as one.
    /// </summary>
    [Fact]
    public void A_refused_version_reads_refused_and_one_nobody_asked_about_reads_unjudged()
    {
        var j = Given();
        using var _1 = j.Db;
        var promotions = new Promotions(j.Db);

        var unjudged = promotions.Standing(j.VersionId);
        Assert.Equal(PromotionState.Unjudged, unjudged.State);
        Assert.Null(unjudged.Promotion);
        Assert.Contains("has not been asked about it", unjudged.Why, StringComparison.Ordinal);

        promotions.Record(RowFor(j, PromotionVerdict.Refused, PromotionReason.NotProfitable));

        var refused = promotions.Standing(j.VersionId);
        Assert.Equal(PromotionState.Refused, refused.State);
        Assert.False(refused.IsPromoted);
        Assert.Contains(PromotionReason.Words(PromotionReason.NotProfitable), refused.Why,
            StringComparison.Ordinal);
    }
}

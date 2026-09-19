using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TradeAgent.Core.Db;

/// <summary>
/// WHAT THE REFEREE ANSWERED ABOUT ONE VERSION. Two words, and neither of them is
/// <c>invalidated</c>: see <see cref="PromotionStanding"/>.
/// </summary>
public static class PromotionVerdict
{
    /// <summary>The evidence met the campaign's fixed scoring policy, over the holdout, after the freeze.</summary>
    public const string Promoted = "promoted";

    /// <summary>It did not, and <see cref="PromotionReason"/> says which clause it failed.</summary>
    public const string Refused = "refused";

    /// <summary>
    /// The evidence met the campaign's fixed PAPER policy over held-back months that do NOT post-date
    /// the version's freeze — a favourable HISTORICAL verdict, which <c>docs/PRINCIPLES.md</c> §
    /// Evidence asks be a different meaning from eligibility for live capital. It buys eligibility for
    /// paper observation and nothing else: <see cref="PromotionRow.IsPromoted"/> is false for it and
    /// <c>Allocations.Record</c> refuses it in words.
    /// </summary>
    public const string PaperEligible = "paper-eligible";

    public static bool IsKnown(string? v) => v is Promoted or Refused or PaperEligible;
}

/// <summary>
/// WHY, AS A CLOSED VOCABULARY — and the vocabulary is the disclosure boundary.
///
/// <para>A promotion row's <c>reason</c> is one of these words and never free text, because the reason
/// is the ONE thing about a holdout evaluation that crosses back to the team that submitted the version
/// (<c>docs/COUNCIL.md</c>:196-197, "private evaluation disclosures remain referee-budgeted"). A reason
/// column that could hold a sentence could hold a figure, and a figure computed over the held-back
/// months is the leak this whole protocol exists to bound — so the column cannot hold one at all.</para>
///
/// <para><see cref="Words"/> is the same fact in the owner's language, for their report. It is a
/// function of the code rather than a second column, so there is exactly one account of any verdict and
/// nothing stored that a later reader has to check for figures.</para>
/// </summary>
public static class PromotionReason
{
    /// <summary>Promoted: the run completed, traded, and came out ahead after its declared costs.</summary>
    public const string Met = "meets-the-scoring-policy";

    /// <summary>
    /// Refused: the holdout window begins at or before the version was frozen, so the evidence is
    /// history the submission may already have been written around (<c>docs/COUNCIL.md</c>:135-136).
    /// </summary>
    public const string PrecedesTheFreeze = "evidence-precedes-the-freeze";

    /// <summary>Refused: the run faulted, so the figures cover part of a window nobody chose.</summary>
    public const string DidNotComplete = "the-holdout-run-did-not-complete";

    /// <summary>Refused: nothing closed on the holdout, so there is no result to judge.</summary>
    public const string NoTrade = "no-trade-on-the-holdout";

    /// <summary>Refused: what closed did not cover its own declared fees and slippage.</summary>
    public const string NotProfitable = "not-profitable-after-costs";

    /// <summary>
    /// Paper-eligible: the run completed, traded, and came out ahead after its declared costs — but
    /// over a window that does not post-date the version's freeze, so <see cref="Met"/> is not
    /// available to it and <see cref="PrecedesTheFreeze"/> is not the informative answer either.
    /// <c>CampaignPolicy.PaperV1</c> is the standard that was applied and its sha is on the row.
    /// </summary>
    public const string MetOnHistory = "meets-the-paper-policy-on-historical-evidence";

    public static bool IsKnown(string? r) =>
        r is Met or PrecedesTheFreeze or DidNotComplete or NoTrade or NotProfitable or MetOnHistory;

    /// <summary>The same reason in the owner's words. No figure is in any of them, by construction.</summary>
    public static string Words(string reason) => reason switch
    {
        Met => "the version met this campaign's scoring policy on bars it had never been run over",
        PrecedesTheFreeze => "the held-back window begins at or before this version was frozen, so it is "
            + "not evidence collected after the freeze",
        DidNotComplete => "the holdout run halted before the end of its window",
        NoTrade => "the version closed no trade at all over the held-back months",
        NotProfitable => "what it did close did not cover its own declared fees and slippage",
        MetOnHistory => "the version met this campaign's paper policy over held-back months that do not "
            + "post-date its own freeze, so it may be observed forward on paper and is not promoted and "
            + "gets no capital",
        _ => reason
    };
}

/// <summary>
/// ONE IMMUTABLE PROMOTION RECORD, IDENTIFIED BY EVERYTHING THE VERDICT RESTED ON.
///
/// <para><b><see cref="Id"/> is a SHA-256 over the bound tuple and over nothing else</b> — the version,
/// the campaign, the scoring policy's hash, the interpreter build, the holdout dataset and its hash at
/// run time, the declared execution model, the evaluator's version and the holdout run. That list is
/// `docs/COUNCIL.md` rule 9 spelled out: "promotion needs app-computed evidence bound to code,
/// parameters, dependencies, data, evaluator, execution-and-cost model and scoring-policy versions".
/// An id minted from the clock would make the same evidence promote the same version twice, which is
/// the mutant this record was built against: two records for one judgement, and no way to tell which of
/// them a later reader is supposed to believe.</para>
///
/// <para><b><see cref="Verdict"/> and <see cref="Reason"/> are NOT in the id, deliberately.</b> They are
/// a function of the nine facts that are — the same run, scored by the same policy, cannot come out two
/// ways — so putting them in the hash would only make a build that disagreed with itself write a second
/// row instead of colliding with the first. <c>ON CONFLICT DO NOTHING</c> is what makes the first answer
/// stand.</para>
///
/// <para><b>What is not here is the STANDING.</b> Whether this promotion still holds is computed at read
/// time from these hashes against the current facts — see <see cref="Promotions.Standing"/> — never
/// written back over the row. A record the app can edit after the outcome is known is not a record.</para>
/// </summary>
public sealed record PromotionRow(
    string Id,
    string VersionId,
    long CampaignId,
    string ScoringPolicySha256,
    string InterpreterBuild,
    long HoldoutDatasetId,
    string HoldoutDatasetSha256,
    string ExecutionModel,
    string EvaluatorVersion,
    string HoldoutRunId,
    string Verdict,
    string Reason,
    DateTimeOffset At)
{
    public bool IsPromoted => Verdict == PromotionVerdict.Promoted;

    /// <summary>
    /// Whether this verdict is the favourable HISTORICAL one. It is deliberately a second question
    /// rather than a widening of <see cref="IsPromoted"/>: every caller on the money path asks that
    /// one, and an <c>IsPromoted</c> that answered true here would put the owner's capital behind
    /// months the submission may already have been written around.
    /// </summary>
    public bool IsPaperEligible => Verdict == PromotionVerdict.PaperEligible;

    /// <summary>
    /// THE BOUND TUPLE, HASHED: nine facts, newline separated, in this order. The order and the
    /// spelling are part of the contract — an id is compared against rows written by earlier builds.
    /// </summary>
    public static string IdOf(string versionId, long campaignId, string scoringPolicySha256,
        string interpreterBuild, long holdoutDatasetId, string holdoutDatasetSha256,
        string executionModel, string evaluatorVersion, string holdoutRunId) =>
        Sha256Hex.Of(string.Join('\n',
            versionId,
            campaignId.ToString(CultureInfo.InvariantCulture),
            scoringPolicySha256,
            interpreterBuild,
            holdoutDatasetId.ToString(CultureInfo.InvariantCulture),
            holdoutDatasetSha256,
            executionModel,
            evaluatorVersion,
            holdoutRunId));

    /// <summary>The id these nine facts hash to, whatever <see cref="Id"/> currently holds.</summary>
    public string ComputedId => IdOf(VersionId, CampaignId, ScoringPolicySha256, InterpreterBuild,
        HoldoutDatasetId, HoldoutDatasetSha256, ExecutionModel, EvaluatorVersion, HoldoutRunId);

    /// <summary>
    /// THE EXECUTION BOUNDS THE PROMOTED PROGRAM DECLARED, FROZEN ONTO THE VERDICT — or null on all
    /// three because the program declared none.
    ///
    /// <para><c>docs/COUNCIL.md</c>:35, "a changed assumption invalidates the evidence that rested on
    /// it". A verdict is evidence that a program is fit to trade the owner's money, and how stale a
    /// decision that program may act on is one of the assumptions it rested on. Recording it HERE is
    /// what makes a later reader able to say what was judged, rather than reading today's answer to
    /// yesterday's question.</para>
    ///
    /// <para><b>They are copied off the FROZEN PROGRAM and never off a request.</b> See
    /// <see cref="Strategy.Referee"/>: the referee re-parses the recorded source, checks that it still
    /// hashes to the version id, and takes the bounds from THAT. A promotion that restated a bound
    /// supplied at promotion time would be a record of a program nobody submitted.</para>
    ///
    /// <para>NOT in <see cref="Id"/>, and deliberately: a changed bound is already a different
    /// <see cref="VersionId"/> (<c>StrategyCanonical</c> hashes all three), so the tuple already binds
    /// them and a tenth hashed fact would only be the ninth spelled twice.</para>
    /// </summary>
    public TimeSpan? Timeframe { get; init; }

    public TimeSpan? DataFreshness { get; init; }

    public TimeSpan? MaxDecisionAge { get; init; }

    /// <summary>The three as one value, or null unless all three are there. See <c>FreshnessBounds</c>.</summary>
    public Strategy.FreshnessBounds? Freshness =>
        this is { Timeframe: { } t, DataFreshness: { } d, MaxDecisionAge: { } m }
            ? new Strategy.FreshnessBounds(t, d, m) : null;
}

/// <summary>WHERE A VERSION STANDS, COMPUTED AT READ TIME. Four answers; see <see cref="Promotions.Standing"/>.</summary>
public static class PromotionState
{
    /// <summary>A promotion was recorded and every assumption it rested on is still true.</summary>
    public const string Promoted = "promoted";

    /// <summary>The referee answered and the answer was no. <see cref="PromotionReason"/> says which clause.</summary>
    public const string Refused = "refused";

    /// <summary>
    /// It was promoted, and something it was bound to has changed since — the dataset was rejected or
    /// re-collected, the interpreter was rebuilt, the scoring policy is not the one that was applied.
    /// <c>docs/COUNCIL.md</c>:35: "a changed assumption invalidates the evidence that rested on it".
    /// </summary>
    public const string Invalidated = "invalidated";

    /// <summary>No verdict has ever been recorded for this version. Not a refusal — nobody has asked.</summary>
    public const string Unjudged = "unjudged";
}

/// <summary>
/// WHERE ONE VERSION STANDS AND WHY, in the words a person reads.
///
/// <para><see cref="Promotion"/> is the row the answer is about, or null for
/// <see cref="PromotionState.Unjudged"/>. <see cref="IsPromoted"/> is the only question a caller on the
/// money path may ask — a later unit reads it in <c>TryAuthorizeExecution</c> — and it is true for
/// exactly one of the four states, so an invalidated promotion is not "promoted with a note".</para>
/// </summary>
public sealed record PromotionStanding(string State, string Why, PromotionRow? Promotion)
{
    public bool IsPromoted => State == PromotionState.Promoted;
}

/// <summary>
/// THE PROMOTION LEDGER: WHAT THE REFEREE ANSWERED, WRITTEN ONCE AND NEVER EDITED.
///
/// <para><b>The app is the only writer and there is no update and no delete.</b> Not "no pipe op" — no
/// method at all: this class exposes one write (<see cref="Record"/>), which is
/// <c>ON CONFLICT DO NOTHING</c>, and the reads below. A promotion is the evidence a version is allowed
/// to trade the owner's money on; a record its subject could edit, or that the app could quietly
/// restate after the outcome was known, is not a record (<c>docs/COUNCIL.md</c>:212, precommitment).</para>
///
/// <para><b>Invalidation is READ, never written.</b> <see cref="Standing"/> compares the hashes on the
/// row with the facts as they are now. The alternative — an <c>invalidated</c> column somebody has to
/// remember to set — would make a promotion's truth depend on a sweep having run, and the sweep that
/// did not run is exactly the case the money path must not get wrong.</para>
/// </summary>
public sealed class Promotions(Database db)
{
    readonly DatasetStore _datasets = new(db);

    const string Cols =
        "id, version_id, campaign_id, scoring_policy_sha256, interpreter_build, holdout_dataset_id, " +
        "holdout_dataset_sha256, execution_model, evaluator_version, holdout_run_id, verdict, reason, at, " +
        // LAST, so every positional read above them keeps its index. See `PromotionRow.Timeframe`.
        "timeframe, data_freshness, max_decision_age";

    /// <summary>
    /// RECORDS ONE VERDICT, or leaves the row that is already there alone. Returns the row AS WRITTEN,
    /// with the id these nine facts hash to.
    ///
    /// <para>The id is computed here rather than taken from the caller, for the reason
    /// <c>PublicationStore.Commit</c> assigns its own revision: a caller that had to remember to hash
    /// the tuple is a caller that can forget, and a promotion keyed by anything else is a promotion
    /// that says nothing about what it was a promotion OF.</para>
    ///
    /// <para>A second record of the same evidence writes nothing and the FIRST answer stands, with its
    /// own <c>at</c>. That is what lets the referee be crashed in the middle: the run is deterministic,
    /// so the retry computes the same tuple and collides with the row that is already there.</para>
    /// </summary>
    public PromotionRow Record(PromotionRow promotion) => db.Write(_ =>
    {
        ArgumentNullException.ThrowIfNull(promotion);

        var row = promotion with { Id = promotion.ComputedId };

        using var c = db.Cmd($"""
            INSERT INTO strategy_promotion({Cols})
            VALUES($id,$ver,$camp,$policy,$build,$ds,$sha,$model,$eval,$run,$verdict,$reason,$at,$tf,$fresh,$age)
            ON CONFLICT(id) DO NOTHING
            """,
            ("$id", row.Id), ("$ver", row.VersionId), ("$camp", row.CampaignId),
            ("$policy", row.ScoringPolicySha256), ("$build", row.InterpreterBuild),
            ("$ds", row.HoldoutDatasetId), ("$sha", row.HoldoutDatasetSha256),
            ("$model", row.ExecutionModel), ("$eval", row.EvaluatorVersion), ("$run", row.HoldoutRunId),
            ("$verdict", row.Verdict), ("$reason", row.Reason), ("$at", Sql.T(row.At)),
            ("$tf", Sql.Seconds(row.Timeframe)), ("$fresh", Sql.Seconds(row.DataFreshness)),
            ("$age", Sql.Seconds(row.MaxDecisionAge)));
        c.ExecuteNonQuery();

        return ById(row.Id) ?? row;
    });

    /// <summary>One promotion by its id, or null when this installation has never recorded it.</summary>
    public PromotionRow? ById(string id) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {Cols} FROM strategy_promotion WHERE id=$id", ("$id", id));
        return Read(c).FirstOrDefault();
    });

    /// <summary>
    /// Every verdict recorded about one version, newest first. Rows, never a standing: whether any of
    /// them still holds is <see cref="Standing"/>'s answer and nobody else's.
    /// </summary>
    public IReadOnlyList<PromotionRow> For(string versionId) => db.Read(_ =>
    {
        using var c = db.Cmd(
            $"SELECT {Cols} FROM strategy_promotion WHERE version_id=$v ORDER BY at DESC, id DESC",
            ("$v", versionId));
        return Read(c);
    });

    /// <summary>Every verdict this installation has recorded, newest first. What the owner's report lists.</summary>
    public IReadOnlyList<PromotionRow> All(int limit = 100) => db.Read(_ =>
    {
        using var c = db.Cmd(
            $"SELECT {Cols} FROM strategy_promotion ORDER BY at DESC, id DESC LIMIT $n", ("$n", limit));
        return Read(c);
    });

    /// <summary>How many verdicts have been recorded at all. The report's gap line asks this.</summary>
    public int Count => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT COUNT(*) FROM strategy_promotion");
        return Convert.ToInt32(c.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    /// <summary>How far back <see cref="Current"/> looks. A version promoted long ago is still promoted.</summary>
    public const int Recent = 20;

    /// <summary>
    /// WHAT STANDS PROMOTED RIGHT NOW, or null when this installation has judged nothing at all.
    ///
    /// <para>Each recent judgement is asked in turn and the first that still HOLDS is the answer —
    /// <see cref="Standing"/> computes invalidation at read time, so a promotion whose dataset was
    /// rejected or whose interpreter has moved is skipped rather than reported as current. When none
    /// holds, the newest INVALIDATED standing is returned anyway, so a caller can say what was
    /// withdrawn and why; told only "none", a turn would go looking for a verdict that is on the
    /// table.</para>
    ///
    /// <para>Here rather than at either caller because there are now two — the Situation's promoted
    /// line and section 3 of the owner's report — and two copies of "what is promoted" are two
    /// answers about the one fact that decides whether anything may trade at all.</para>
    /// </summary>
    public PromotionStanding? Current(int look = Recent)
    {
        PromotionStanding? withdrawn = null;

        foreach (var row in All(look))
        {
            var standing = Standing(row.VersionId);
            if (standing.IsPromoted) return standing;
            withdrawn ??= standing.State == PromotionState.Invalidated ? standing : null;
        }

        return withdrawn;
    }

    /// <summary>
    /// WHERE THIS VERSION STANDS RIGHT NOW — the one reader, and the only place the word
    /// <c>invalidated</c> is ever produced.
    ///
    /// <para><b>Every check is the RECORDED hash against the CURRENT fact.</b> The dataset the evidence
    /// was computed over is asked whether it is still accepted and still the same bytes the run read;
    /// the interpreter build and the scoring policy on the row are compared with this build's. Comparing
    /// the current fact with itself — the mutant — is a check that can never fail: a dataset re-collected
    /// under a promotion would go on reading as promoted, which is a version trading the owner's money
    /// on months that are no longer on this machine.</para>
    ///
    /// <para><b>The newest verdict is the one that answers.</b> A version can be judged again under a
    /// renewed campaign or a later interpreter; the standing is the most recent of those judgements, and
    /// the ones before it stay on the table where a reader can see the sequence.</para>
    ///
    /// <para>The dataset's STATE is read off the ledger rather than re-hashed here. Every reader that
    /// actually opens the bars re-hashes them (<c>DatasetStore.Checked</c>, which is what writes the
    /// rejection down), and this answer is read on the money path where hashing tens of megabytes per
    /// order would be its own kind of wrong. `docs/CONTRACTS.md` says so.</para>
    /// </summary>
    public PromotionStanding Standing(string versionId)
    {
        if (For(versionId).FirstOrDefault() is not { } promotion)
            return new PromotionStanding(PromotionState.Unjudged,
                $"no verdict has been recorded for version {Short(versionId)}. TradeAgent's referee has "
                + "not been asked about it, which is a different thing from having answered no.", null);

        if (Invalidation(promotion) is { } changed)
            return new PromotionStanding(PromotionState.Invalidated, changed, promotion);

        return promotion.IsPromoted
            ? new PromotionStanding(PromotionState.Promoted,
                $"TradeAgent's referee promoted version {Short(versionId)} at {promotion.At:u} on the "
                + $"evidence of holdout run {Short(promotion.HoldoutRunId)}, and every assumption that "
                + "verdict was bound to still holds.", promotion)
            : new PromotionStanding(PromotionState.Refused,
                $"TradeAgent's referee refused version {Short(versionId)} at {promotion.At:u}: "
                + PromotionReason.Words(promotion.Reason) + ".", promotion);
    }

    /// <summary>
    /// WHAT HAS CHANGED SINCE THIS VERDICT WAS TAKEN, in words, or null because nothing has.
    ///
    /// <para>A REFUSAL is checked too, and that is deliberate: "this version was refused on evidence
    /// that no longer exists" is a different statement from "this version was refused", and the second
    /// one would be the app standing behind a judgement it can no longer support.</para>
    /// </summary>
    string? Invalidation(PromotionRow promotion)
    {
        var set = _datasets.ById(promotion.HoldoutDatasetId);

        if (set is null)
            return $"the holdout dataset {promotion.HoldoutDatasetId} this verdict was computed over is no "
                + "longer in this installation's ledger, so the evidence cannot be pointed at.";

        if (set.State == DatasetState.REJECTED)
            return $"the holdout dataset {promotion.HoldoutDatasetId} ({set.Pair} {set.Interval} "
                + $"{set.Version}) has since been REJECTED — {set.RejectedReason} — so the bars this "
                + "verdict rested on are bars TradeAgent no longer vouches for.";

        if (!string.Equals(set.NormalisedSha256, promotion.HoldoutDatasetSha256, StringComparison.OrdinalIgnoreCase))
            return $"the holdout dataset {promotion.HoldoutDatasetId} hashed "
                + $"{Short(promotion.HoldoutDatasetSha256)} when this verdict was computed and hashes "
                + $"{Short(set.NormalisedSha256)} now: the months have been collected again, so this "
                + "evidence is about bars that are no longer the ones under that id.";

        if (!string.Equals(set.EvaluationClass, Data.EvaluationClass.Research, StringComparison.Ordinal))
            return $"the holdout dataset {promotion.HoldoutDatasetId} is now classed "
                + $"{set.EvaluationClass}, and a fixture establishes plumbing only — it is never evidence.";

        if (!string.Equals(promotion.InterpreterBuild, StrategyStore.InterpreterBuild, StringComparison.Ordinal))
            return $"this verdict was computed by interpreter build {promotion.InterpreterBuild} and this "
                + $"build is {StrategyStore.InterpreterBuild}: the program may not mean the same thing, "
                + "so the evidence does not carry across.";

        if (!string.Equals(promotion.ScoringPolicySha256, CampaignPolicy.Sha256Of(CampaignPolicy.V1),
                StringComparison.OrdinalIgnoreCase))
            return $"this verdict was taken under scoring policy {Short(promotion.ScoringPolicySha256)} "
                + $"and this build applies {Short(CampaignPolicy.Sha256Of(CampaignPolicy.V1))}: the "
                + "standard has changed, so what met it then is not what would meet it now.";

        return null;
    }

    static string Short(string id) => id.Length <= 12 ? id : id[..12];

    static List<PromotionRow> Read(SqliteCommand c)
    {
        var rows = new List<PromotionRow>();
        using var r = c.ExecuteReader();
        while (r.Read())
            rows.Add(new PromotionRow(
                r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4),
                r.GetInt64(5), r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9),
                r.GetString(10), r.GetString(11), Sql.Time(r.GetString(12)))
            {
                Timeframe = Sql.Span(r, 13),
                DataFreshness = Sql.Span(r, 14),
                MaxDecisionAge = Sql.Span(r, 15)
            });
        return rows;
    }
}

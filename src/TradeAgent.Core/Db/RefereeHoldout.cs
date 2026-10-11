using System.Globalization;
using Microsoft.Data.Sqlite;
using TradeAgent.Core.Strategy;

namespace TradeAgent.Core.Db;

/// <summary>
/// WHAT ONE VERDICT SEALED OF ITS HOLDOUT RUN (<c>U-referee-v2c</c>; <c>docs/EDGE-FACTORY.md</c> § 4.5 E4; R04 :290) — and
/// <c>internal</c>, because every figure on it was computed from the held-back months and none of them may cross.
///
/// <para>The research measurement it read (<see cref="ResearchRow"/>, the method of the <c>referee_research</c> row under
/// the same campaign, version and evaluator), the holdout's <see cref="HoldoutDays"/> known days and their per-day
/// <see cref="SrH"/>, <see cref="SkewH"/> and raw <see cref="KurtosisH"/>, the fidelity <see cref="Z"/> and its status, the
/// betting e-value <see cref="EH"/>, p_h = 1 − PSR(0) (<see cref="PH"/>), the α-wealth step, the author and its cutoff, and
/// the flag. A figure that is not a number is null — never 0.</para>
/// </summary>
internal sealed record SealedHoldoutRow(
    long CampaignId,
    string VersionId,
    int Method,
    int ResearchRow,
    int HoldoutDays,
    double? SrH,
    double? SkewH,
    double? KurtosisH,
    double? Z,
    string ZStatus,
    double? EH,
    double? PH,
    double AlphaJ,
    double WealthBefore,
    double WealthAfter,
    string AddisStatus,
    string? AuthorAttempt,
    string? AuthorModel,
    DateOnly? ModelCutoff,
    string? CutoffSource,
    bool Remembered,
    DateTimeOffset At);

/// <summary>
/// THE ONE THING OF A SEALED HOLDOUT RECORD THAT MAY BE SHOWN: whether the authoring model may remember the holdout —
/// two dates and a model id, and no figure. <see cref="HoldoutFrom"/> is the campaign's cutoff, <see cref="Model"/> the
/// author's model as its runtime named it (null: none named, or no attempt), <see cref="Cutoff"/> its published knowledge
/// cutoff (null: none published, or no model).
/// </summary>
public sealed record RememberedFlag(long CampaignId, string VersionId, DateTimeOffset HoldoutFrom, string? Model,
    DateOnly? Cutoff, bool Remembered)
{
    /// <summary>The flag in words, for the verdict's line: empty for a holdout that cannot be remembered.</summary>
    public string Words => !Remembered
        ? ""
        : $"holdout may be remembered: it starts {HoldoutFrom.UtcDateTime:yyyy-MM-dd}, before "
          + (Model is null
              ? "a knowledge cutoff TradeAgent cannot read, because the runtime that wrote the version named no model"
              : Cutoff is { } through
                  ? $"the {through:yyyy-MM-dd} knowledge cutoff of {Model}, the model that wrote the version"
                  : $"a knowledge cutoff {Model}'s vendor does not publish")
          + " — offered paper only after every version whose holdout cannot be";
}

/// <summary>
/// THE REFEREE'S SEALED HOLDOUT RECORD — E4'S FIDELITY z, BETTING E-VALUE AND α-WEALTH, AND THE REMEMBERED-HOLDOUT FLAG,
/// COMPUTED AT EVERY VERDICT AND RECORDED WITH IT (<c>U-referee-v2c</c>; seat A's Q6 (a)). Method <see cref="Version"/>.
///
/// <para><b>Sealed.</b> Every figure is computed from the holdout run's trace, in memory, inside the verdict's own write —
/// the run keeps no stream — and is read back by no surface: <see cref="SealedOf"/> is internal, and the one public
/// read is <see cref="Flag"/>, which carries two dates and a model id. No clause of any policy reads a row; the verdict,
/// the promotion, <c>RefereeFeedback.Text</c>, the note, the boundary, the Situation and the report are what they were.
/// The paper sweep's order reads <see cref="Flag"/> and the research row, never a sealed figure.</para>
///
/// <para><b>The α-wealth is the lineage's and every campaign's over the same months</b>: the verdicts
/// <c>CampaignStore.JudgementsSpent</c> counts, in charge order (<c>CampaignStore.JudgementsCharged</c>); each earlier one is
/// read by its recorded step at this method under this build's evaluator, and one with none — before this rung, stopped
/// before its write, or sealed only under another evaluator — counts as tested, not rejected, not discarded (<see cref="AlphaWealth"/>). A renewal never resets it.</para>
///
/// <para><b>Append-only, written only by the app</b>: <see cref="Record"/> inserts one row per (campaign, version, method,
/// evaluator — this build's <see cref="Referee.EvaluatorVersion"/>, rung 36) in the verdict's write, first writer wins, and nothing updates or deletes a row; no op and no verb reaches it. A
/// verdict before the rung has none and gains none. A changed method is a new <see cref="Version"/> on new rows.</para>
/// </summary>
public sealed class RefereeHoldout(Database db)
{
    /// <summary>
    /// The record's method: <see cref="Fidelity.Version"/> 1, <see cref="BettingE.Version"/> 1 and
    /// <see cref="AlphaWealth.Version"/> 1 over <see cref="DailyReturns.Version"/> 1 days, p_h by
    /// <see cref="Deflation.Psr"/>, the flag by <see cref="ModelCutoffs.Remembered"/>.
    /// </summary>
    public const int Version = 1;

    const string Cols = "campaign_id, version_id, method, research_row, holdout_days, sr_h, skew_h, kurtosis_h, z, z_status, "
        + "e_h, p_h, alpha_j, wealth_before, wealth_after, addis_status, author_attempt, author_model, model_cutoff, "
        + "cutoff_source, remembered, at";

    /// <summary>
    /// SEALS one verdict's holdout: its <paramref name="days"/> (the holdout run's, in memory), the
    /// <paramref name="research"/> measurement the verdict took before the run, the α-wealth step after every earlier
    /// judgement over the same months, and <paramref name="author"/>. Writes nothing; called inside the verdict's write, so
    /// the history it reads is the one the row lands on.
    /// </summary>
    internal SealedHoldoutRow Seal(long campaignId, string versionId, ResearchEvidenceRow research,
        IReadOnlyList<DailyReturn> days, Authorship author, DateTimeOffset at) => db.Read(_ =>
    {
        ArgumentNullException.ThrowIfNull(versionId);
        ArgumentNullException.ThrowIfNull(research);
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(author);

        var moments = Deflation.MomentsOf(days);
        var known = Deflation.Known(days).Count;
        var fidelity = Fidelity.Of(moments, research.Sr, research.Sr0);
        var p = moments is { } m && Deflation.Psr(m.Sr, 0, m.T, m.Skew, m.Kurtosis) is { } psr ? 1 - psr : (double?)null;
        var step = AlphaWealth.Next(History(campaignId, versionId), p, known);

        return new SealedHoldoutRow(campaignId, versionId, Version, research.Method, known, moments?.Sr, moments?.Skew,
            moments?.Kurtosis, fidelity.Z, fidelity.Status, BettingE.Of(days), p, step.AlphaJ, step.WealthBefore,
            step.WealthAfter, step.Status, author.Attempt, author.Model, author.Cutoff?.Through, author.Cutoff?.Cited,
            author.Remembered, at);
    });

    /// <summary>
    /// THE α-WEALTH HISTORY BEFORE ONE VERDICT: the recorded step of every judgement charged before it over the same
    /// months, in charge order — <see cref="AlphaWealth.Tested"/> for one with no step at this method under this build's
    /// evaluator.
    /// </summary>
    internal IReadOnlyList<string> History(long campaignId, string versionId) => db.Read(_ =>
    {
        var charged = new CampaignStore(db).JudgementsCharged(campaignId);
        var history = new List<string>();
        foreach (var verdict in charged)
        {
            if (verdict.CampaignId == campaignId && string.Equals(verdict.VersionId, versionId, StringComparison.Ordinal)) break;
            using var c = db.Cmd(
                "SELECT addis_status FROM referee_holdout WHERE campaign_id=$camp AND version_id=$ver AND method=$m "
                + "AND evaluator_version=$eval",
                ("$camp", verdict.CampaignId), ("$ver", verdict.VersionId), ("$m", Version), ("$eval", Referee.EvaluatorVersion));
            history.Add(c.ExecuteScalar() as string ?? AlphaWealth.Tested);
        }
        return (IReadOnlyList<string>)history;
    });

    /// <summary>
    /// RECORDS one sealed row — inside the verdict's own write (<c>Referee.Verdict</c>), so it lands with the promotion or
    /// not at all. First writer wins: a row already recorded for the (campaign, version, method) under this build's
    /// evaluator stands. The evaluator written is this build's <see cref="Referee.EvaluatorVersion"/>, never a caller's.
    /// </summary>
    internal void Record(SealedHoldoutRow row) => db.Write(_ =>
    {
        ArgumentNullException.ThrowIfNull(row);
        using var insert = db.Cmd($"""
            INSERT INTO referee_holdout({Cols}, evaluator_version)
            VALUES($camp,$ver,$method,$research,$days,$sr,$skew,$kurt,$z,$zs,$e,$p,$a,$wb,$wa,$as,$att,$model,$cut,$src,$rem,$at,
                   $eval)
            ON CONFLICT(campaign_id, version_id, method, evaluator_version) DO NOTHING
            """,
            ("$camp", row.CampaignId), ("$ver", row.VersionId), ("$method", row.Method), ("$research", row.ResearchRow),
            ("$days", row.HoldoutDays), ("$sr", Text(row.SrH)), ("$skew", Text(row.SkewH)), ("$kurt", Text(row.KurtosisH)),
            ("$z", Text(row.Z)), ("$zs", row.ZStatus), ("$e", Text(row.EH)), ("$p", Text(row.PH)),
            ("$a", Text(row.AlphaJ)), ("$wb", Text(row.WealthBefore)), ("$wa", Text(row.WealthAfter)),
            ("$as", row.AddisStatus), ("$att", row.AuthorAttempt), ("$model", row.AuthorModel),
            ("$cut", row.ModelCutoff?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("$src", row.CutoffSource),
            ("$rem", row.Remembered ? 1 : 0), ("$at", Sql.T(row.At)), ("$eval", Referee.EvaluatorVersion));
        return insert.ExecuteNonQuery();
    });

    /// <summary>
    /// The sealed row of one verdict at this method under this build's evaluator, or null. Internal: no surface reads it,
    /// only this unit's tests.
    /// </summary>
    internal SealedHoldoutRow? SealedOf(long campaignId, string versionId) => db.Read(_ =>
    {
        using var c = db.Cmd($"""
            SELECT {Cols} FROM referee_holdout
             WHERE campaign_id=$camp AND version_id=$ver AND method=$m AND evaluator_version=$eval
            """, ("$camp", campaignId), ("$ver", versionId), ("$m", Version), ("$eval", Referee.EvaluatorVersion));
        using var r = c.ExecuteReader();
        return r.Read() ? Read(r) : null;
    });

    /// <summary>
    /// WHETHER ONE VERDICT'S HOLDOUT MAY BE REMEMBERED BY ITS AUTHOR — the flag, its two dates and the model, and no
    /// figure — or null: no sealed row at this method under this build's evaluator (a verdict before rung 35, one sealed
    /// only under another evaluator, or none at all) — which the paper queue reads as "may be", never as "cannot".
    /// </summary>
    public RememberedFlag? Flag(long campaignId, string versionId) => db.Read(_ =>
    {
        using var c = db.Cmd("""
            SELECT c.holdout_from, h.author_model, h.model_cutoff, h.remembered
              FROM referee_holdout h JOIN strategy_campaign c ON c.id = h.campaign_id
             WHERE h.campaign_id=$camp AND h.version_id=$ver AND h.method=$m AND h.evaluator_version=$eval
            """, ("$camp", campaignId), ("$ver", versionId), ("$m", Version), ("$eval", Referee.EvaluatorVersion));
        using var r = c.ExecuteReader();
        if (!r.Read()) return null;
        return new RememberedFlag(campaignId, versionId, Sql.Time(r.GetString(0)), Sql.S(r.GetValue(1)),
            r.GetValue(2) is string d ? DateOnly.ParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
            r.GetInt64(3) != 0);
    });

    static SealedHoldoutRow Read(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), Number(r.GetValue(5)),
        Number(r.GetValue(6)), Number(r.GetValue(7)), Number(r.GetValue(8)), r.GetString(9), Number(r.GetValue(10)),
        Number(r.GetValue(11)), Number(r.GetValue(12))!.Value, Number(r.GetValue(13))!.Value, Number(r.GetValue(14))!.Value,
        r.GetString(15), Sql.S(r.GetValue(16)), Sql.S(r.GetValue(17)),
        r.GetValue(18) is string d ? DateOnly.ParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
        Sql.S(r.GetValue(19)), r.GetInt64(20) != 0, Sql.Time(r.GetString(21)));

    /// <summary>A figure as the row holds it: invariant round-trip text, or NULL for one that is not a number.</summary>
    static string? Text(double? x) => x is { } v && double.IsFinite(v) ? v.ToString("R", CultureInfo.InvariantCulture) : null;

    static double? Number(object? o) => o is null or DBNull ? null : double.Parse((string)o, CultureInfo.InvariantCulture);
}

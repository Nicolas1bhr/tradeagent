using System.Globalization;

namespace TradeAgent.Core.Db;

/// <summary>What one pass of the resolver did: how many forecasts it resolved, and the ones it refused to read, in words.</summary>
public sealed record ForecastSweep(int Resolved, IReadOnlyList<string> Refused);

/// <summary>
/// THE APP RESOLVES FORECASTS FROM ITS OWN MEASUREMENTS (<c>U-org-decisions</c>, O5b; ORGANISATION § 5; ORCHESTRATION § 2.11) — once
/// per tick, inside the organisation's sweep. It READS EXACTLY THREE MEASURED RECORDS, each by a named constant a guard test reads:
/// the referee's verdict class on a version (<see cref="VerdictSql"/>: <c>strategy_promotion</c>'s version, verdict and time), a
/// mandate's events (<see cref="MandateEventSql"/>: <c>org_mandate_event</c>'s kind and time) and, for vocabulary v2's
/// <c>unit-admissible</c>, the admissible verdict classes on versions a unit's launches funded (<see cref="UnitAdmissibleSql"/>: the
/// same promotion columns, joined to a run only by its version and attempt and to the funding lineage by its unit). Never a holdout, a run's figures, a
/// stream, an E2–E4 value or a ledger revision (EVID-001): a forecast that could resolve on one of those would let a position
/// learn what the protocol keeps from it.
///
/// <para><b>What it decides.</b> <c>verdict-class</c>: the first verdict for the version by the date — of the forecast's class
/// <c>yes</c>, of another <c>no</c>; none by the date <c>void</c> (RULING Q4), never scored; one stamped BEFORE the forecast's write
/// (committed after it) <c>void</c> as "measured before the forecast", naming it — never dropped into "none by the date". <c>mandate-state</c>: the first event by
/// the date that is the target or closes the mandate — the target <c>yes</c>, another closing state <c>no</c>; the date passed with
/// neither, <c>no</c>. Each resolution is its own immutable row naming the record it rests on, written by
/// <see cref="OrgStore"/> with <c>ON CONFLICT DO NOTHING</c>, so a restart or a second pass writes nothing twice. A forecast whose
/// vocabulary version this build does not know is REFUSED — said, and left unresolved — never read on a guess. It wakes nobody
/// (RULING Q9).</para>
/// </summary>
public sealed class ForecastResolver(Database db)
{
    /// <summary>The verdicts the referee recorded for one version, oldest first.</summary>
    public const string VerdictSql = "SELECT id, verdict, at FROM strategy_promotion WHERE version_id = $subject ORDER BY at, rowid";

    /// <summary>One mandate's events, oldest first.</summary>
    public const string MandateEventSql = "SELECT id, kind, at FROM org_mandate_event WHERE mandate_id = $subject ORDER BY id";

    /// <summary>
    /// THE ADMISSIBLE VERDICTS ON VERSIONS A UNIT FUNDED (vocabulary v2, <c>unit-admissible</c>): the referee's promotion rows whose class
    /// is admissible, for a version with a run whose launch's RECORDED funding chain holds the unit — the run's version and attempt and
    /// nothing else of it, never a figure (EVID-001), and the lineage written at admission, never today's chart. Oldest first.
    /// </summary>
    public const string UnitAdmissibleSql =
        "SELECT p.id, p.verdict, p.at FROM strategy_promotion p WHERE p.verdict IN ($promoted, $paper) AND EXISTS (SELECT 1 FROM strategy_run r "
        + "JOIN ai_attempt_funding f ON f.attempt_id = r.attempt WHERE r.version_id = p.version_id AND f.unit_id = $subject) ORDER BY p.at, p.rowid";

    /// <summary>The three measurement reads, and nothing else the resolver reads to decide an outcome.</summary>
    public static readonly string[] MeasurementSql = [VerdictSql, MandateEventSql, UnitAdmissibleSql];

    /// <summary>Resolves every forecast whose outcome the app's records now settle; each in its own write. Answers what it did.</summary>
    public ForecastSweep Sweep(DateTimeOffset now)
    {
        var org = new OrgStore(db);
        var resolved = 0;
        // A ROW THE STORE CANNOT READ (U-forecast-nul) arrives here as a refusal, never a throw: it is listed with the others,
        // and every forecast the store could read still resolves in this pass.
        var open = org.OpenForecasts();
        var refused = new List<string>(open.Refused);
        foreach (var f in open.Rows)
        {
            // A STORED v1 FORECAST STILL RESOLVES AS v1; a version or a predicate this build does not know is refused by id, never guessed.
            if (!ForecastVocabulary.IsKnown(f.Vocabulary, f.Predicate))
            {
                refused.Add(OrgStore.UnknownVocabulary(f.Id, f.Vocabulary) + " It is left unresolved.");
                continue;
            }
            if (Decide(f, now) is { } r && org.RecordResolution(f.Id, r.Outcome, r.Kind, r.Id, r.Why, now)) resolved++;
        }
        return new ForecastSweep(resolved, refused);
    }

    /// <summary>The resolution the measurements settle for <paramref name="f"/> now, or null while it is open.</summary>
    (string Outcome, string Kind, string Id, string Why)? Decide(ForecastRow f, DateTimeOffset now)
    {
        if (f.Predicate == ForecastVocabulary.UnitAdmissible) return DecideUnit(f, now);
        var (sql, closes) = f.Predicate == ForecastVocabulary.VerdictClass
            ? (VerdictSql, (Func<string, bool>)(_ => true))
            : (MandateEventSql, (Func<string, bool>)(k => k == f.Target || MandateEventKind.Closes(k)));
        foreach (var (id, word, at) in Read(sql, f.Subject))
        {
            if (!closes(word)) continue;
            // MEASURED BEFORE THE FORECAST: a deciding record stamped before the forecast's write but committed after it (the
            // referee stamps before it commits) passed the already-measured check in between. It is never dropped into "nothing by
            // the date" — that would be a false record of what was measured: the forecast is void for this reason, naming it.
            if (at < f.At)
                return (ForecastOutcome.Void, f.Predicate == ForecastVocabulary.VerdictClass ? OrgRefKind.Promotion : "mandate-event", id,
                    $"measured before the forecast: '{word}' was recorded for {f.Subject} at {at:u}, before the forecast was written at {f.At:u}");
            if (at > f.ResolveBy) continue;
            return f.Predicate == ForecastVocabulary.VerdictClass
                ? (word == f.Target ? ForecastOutcome.Yes : ForecastOutcome.No, OrgRefKind.Promotion, id,
                    $"the referee recorded '{word}' for version {f.Subject} at {at:u}")
                : (word == f.Target ? ForecastOutcome.Yes : ForecastOutcome.No, "mandate-event", id,
                    $"mandate {f.Subject} was {word} at {at:u}");
        }
        if (now <= f.ResolveBy) return null;
        return f.Predicate == ForecastVocabulary.VerdictClass
            ? (ForecastOutcome.Void, OrgRefKind.Version, f.Subject, $"no verdict was recorded for version {f.Subject} by {f.ResolveBy:u}")
            : (ForecastOutcome.No, OrgRefKind.Mandate, f.Subject, $"mandate {f.Subject} was not {f.Target} by {f.ResolveBy:u}");
    }

    /// <summary>
    /// <c>unit-admissible</c> AT ITS DATE, never before: the first admissible verdict recorded after the forecast and by its date on a
    /// version the unit funded — <c>admitted</c> yes, <c>none</c> no — or, with none, <c>none</c> yes and <c>admitted</c> no, resting on
    /// the unit. A verdict recorded before the forecast was written is not counted: a forecast is made before its outcome.
    /// </summary>
    (string Outcome, string Kind, string Id, string Why)? DecideUnit(ForecastRow f, DateTimeOffset now)
    {
        if (now <= f.ResolveBy) return null;
        foreach (var (id, word, at) in Read(UnitAdmissibleSql, f.Subject))
        {
            if (at <= f.At || at > f.ResolveBy) continue;
            return (f.Target == ForecastVocabulary.Admitted ? ForecastOutcome.Yes : ForecastOutcome.No, OrgRefKind.Promotion, id,
                $"the referee recorded '{word}' at {at:u} for a version run under unit {f.Subject}'s funding");
        }
        return (f.Target == ForecastVocabulary.NoneAdmitted ? ForecastOutcome.Yes : ForecastOutcome.No, "unit", f.Subject,
            $"no version run under unit {f.Subject}'s funding was promoted or made paper-eligible between {f.At:u} and {f.ResolveBy:u}");
    }

    List<(string Id, string Word, DateTimeOffset At)> Read(string sql, string subject) => db.Read(_ =>
    {
        using var c = db.Cmd(sql, ("$subject", subject), ("$promoted", PromotionVerdict.Promoted), ("$paper", PromotionVerdict.PaperEligible));
        using var r = c.ExecuteReader();
        var rows = new List<(string, string, DateTimeOffset)>();
        while (r.Read())
            rows.Add((Convert.ToString(r.GetValue(0), CultureInfo.InvariantCulture)!, r.GetString(1), Sql.Time(r.GetValue(2))));
        return rows;
    });
}

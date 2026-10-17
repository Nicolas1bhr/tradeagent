using System.Globalization;

namespace TradeAgent.Core.Db;

/// <summary>What one pass of the resolver did: how many forecasts it resolved, and the ones it refused to read, in words.</summary>
public sealed record ForecastSweep(int Resolved, IReadOnlyList<string> Refused);

/// <summary>
/// THE APP RESOLVES FORECASTS FROM ITS OWN MEASUREMENTS (<c>U-org-decisions</c>, O5b; ORGANISATION § 5; ORCHESTRATION § 2.11) — once
/// per tick, inside the organisation's sweep. It READS EXACTLY TWO MEASURED RECORDS, each by a named constant a guard test reads:
/// the referee's verdict class on a version (<see cref="VerdictSql"/>: <c>strategy_promotion</c>'s version, verdict and time) and a
/// mandate's events (<see cref="MandateEventSql"/>: <c>org_mandate_event</c>'s kind and time). Never a holdout, a run's figures, a
/// stream, an E2–E4 value or a ledger revision (EVID-001): a forecast that could resolve on one of those would let a position
/// learn what the protocol keeps from it.
///
/// <para><b>What it decides.</b> <c>verdict-class</c>: the first verdict for the version by the date — of the forecast's class
/// <c>yes</c>, of another <c>no</c>; none by the date <c>void</c> (RULING Q4), never scored. <c>mandate-state</c>: the first event by
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

    /// <summary>The two measurement reads, and nothing else the resolver reads to decide an outcome.</summary>
    public static readonly string[] MeasurementSql = [VerdictSql, MandateEventSql];

    /// <summary>Resolves every forecast whose outcome the app's records now settle; each in its own write. Answers what it did.</summary>
    public ForecastSweep Sweep(DateTimeOffset now)
    {
        var org = new OrgStore(db);
        var resolved = 0;
        var refused = new List<string>();
        foreach (var f in org.OpenForecasts())
        {
            if (f.Vocabulary != ForecastVocabulary.Version || !ForecastVocabulary.IsKnown(f.Predicate))
            {
                refused.Add($"forecast {f.Id} is written in vocabulary v{f.Vocabulary.ToString(CultureInfo.InvariantCulture)} "
                            + $"('{f.Predicate}'), and this build resolves v{ForecastVocabulary.Version} only: it is left unresolved, never guessed.");
                continue;
            }
            if (Decide(f, now) is { } r && org.RecordResolution(f.Id, r.Outcome, r.Kind, r.Id, r.Why, now)) resolved++;
        }
        return new ForecastSweep(resolved, refused);
    }

    /// <summary>The resolution the measurements settle for <paramref name="f"/> now, or null while it is open.</summary>
    (string Outcome, string Kind, string Id, string Why)? Decide(ForecastRow f, DateTimeOffset now)
    {
        var (sql, closes) = f.Predicate == ForecastVocabulary.VerdictClass
            ? (VerdictSql, (Func<string, bool>)(_ => true))
            : (MandateEventSql, (Func<string, bool>)(k => k == f.Target || MandateEventKind.Closes(k)));
        foreach (var (id, word, at) in Read(sql, f.Subject))
        {
            if (at < f.At || at > f.ResolveBy || !closes(word)) continue;
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

    List<(string Id, string Word, DateTimeOffset At)> Read(string sql, string subject) => db.Read(_ =>
    {
        using var c = db.Cmd(sql, ("$subject", subject));
        using var r = c.ExecuteReader();
        var rows = new List<(string, string, DateTimeOffset)>();
        while (r.Read())
            rows.Add((Convert.ToString(r.GetValue(0), CultureInfo.InvariantCulture)!, r.GetString(1), Sql.Time(r.GetValue(2))));
        return rows;
    });
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradeAgent.Core.Db;

/// <summary>
/// THE APP RECORDS A DECISION, A FORECAST'S DECISION OR A LATERAL NOTE MAY CITE (<c>U-org-decisions</c>, O5b): a closed list, each
/// checked to exist at the write — never a file, never a free-text pointer.
/// </summary>
public static class OrgRefKind
{
    public const string Version = "version", Run = "run", Promotion = "promotion", Ledger = "ledger", Mandate = "mandate",
        Delivery = "delivery", Decision = "decision", Escalation = "escalation";

    public static readonly string[] All = [Version, Run, Promotion, Ledger, Mandate, Delivery, Decision, Escalation];

    public static bool IsKnown(string? kind) => kind is not null && Array.IndexOf(All, kind) >= 0;
}

/// <summary>An app record a decision or a note cites (<see cref="OrgRefKind"/>): a ledger entry by its number, the rest by id.</summary>
public sealed record OrgRef(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("id")] string Id);

/// <summary>THE BOUNDS ON WHAT A DECISION, A FORECAST, AN ESCALATION AND A NOTE CARRY. Longer is refused, never cut.</summary>
public static class DecisionLimits
{
    /// <summary>The most options one decision names, and the most characters an option, a choice or an escalation's reason holds.</summary>
    public const int MaxOptions = 10, MaxClaimChars = 500;

    /// <summary>The most refs one decision or note cites, and the most characters a ref's id holds.</summary>
    public const int MaxRefs = 20, MaxRefIdChars = 200;

    /// <summary>The most forecasts on one decision or one mandate, over its whole life.</summary>
    public const int MaxForecasts = 5;

    /// <summary>
    /// THE MOST A DECISION OR A NOTE TAKES ON THE WIRE — its claims and refs as <see cref="Json.Write"/> writes them, escaping
    /// included. A longer one is refused at its write, never cut, so <c>decision-show</c> and <c>lateral-list</c> always serve one
    /// WHOLE inside <see cref="ResearchLedger.MaxReadBytes"/> (O5a's rule for a delivery, <see cref="MandateLimits.MaxDeliveryBytes"/>).
    /// </summary>
    public const int MaxRecordBytes = 32 * 1024;

    /// <summary>The farthest ahead a forecast may name its resolution date.</summary>
    public static readonly TimeSpan ForecastHorizon = TimeSpan.FromDays(366);

    /// <summary>
    /// THE THROTTLE ON ESCALATIONS (RULING Q7): one per caller per mandate, and without a mandate at most this many per caller per
    /// UTC day. An escalation wakes one recipient once; this is what keeps a head from buying its chain's turns with words.
    /// </summary>
    public const int MandatelessPerDay = 2;
}

/// <summary>
/// WHAT A DECISION RECORD IS (ORGANISATION § 5; RULING Q2): <see cref="Routine"/> is the only kind <c>decision-record</c> writes.
/// The MANDATORY kinds — a charter, a merge, a close, a dormancy, an envelope tilt, a family's kill or branch, a verdict request,
/// a paper nomination, a worker replaced, a proposal to the owner — are written by the verb that performs each act
/// (<c>U-org-verbs</c>, O6), in that act's own write, so no record of one can stand without the act behind it. They are named
/// here so the verb can refuse them in words.
/// </summary>
public static class OrgDecisionKind
{
    public const string Routine = "routine";

    public static readonly string[] Mandatory =
        ["charter", "merge", "close", "dormancy", "envelope-tilt", "family-kill", "family-branch", "verdict-request",
         "paper-nomination", "worker-replacement", "owner-proposal"];

    public static readonly string[] All = [Routine, .. Mandatory];

    public static bool IsMandatory(string? kind) => kind is not null && Array.IndexOf(Mandatory, kind) >= 0;
}

/// <summary>
/// THE FORECAST VOCABULARY — closed and versioned (ORGANISATION § 5; RULING Q3). Version 1 holds two predicates, each resolved by
/// the app from ONE record of its own and nothing else (§ 2.11, EVID-001): <see cref="VerdictClass"/> — the verdict class the
/// referee records for a strategy version (<c>strategy_promotion.verdict</c>), never a holdout figure, a run's numbers, a stream or
/// an E2–E4 value — and <see cref="MandateState"/> — the state a mandate reaches (<c>org_mandate_event</c>). A forecast outside it
/// is refused at the write; a stored forecast whose version this build does not know is refused at resolve, never guessed.
/// </summary>
public static class ForecastVocabulary
{
    /// <summary>The vocabulary this build writes and resolves.</summary>
    public const int Version = 1;

    /// <summary><c>verdict-class(version, promoted | paper-eligible | refused, by)</c>.</summary>
    public const string VerdictClass = "verdict-class";

    /// <summary><c>mandate-state(mandate, accepted | rejected | expired | cancelled, by)</c>.</summary>
    public const string MandateState = "mandate-state";

    public static readonly string[] Predicates = [VerdictClass, MandateState];

    /// <summary>The targets a predicate may name.</summary>
    public static IReadOnlyList<string> Targets(string predicate) => predicate switch
    {
        VerdictClass => [PromotionVerdict.Promoted, PromotionVerdict.PaperEligible, PromotionVerdict.Refused],
        MandateState => [MandateEventKind.Accepted, MandateEventKind.Rejected, MandateEventKind.Expired, MandateEventKind.Cancelled],
        _ => []
    };

    public static bool IsKnown(string? predicate) => predicate is not null && Array.IndexOf(Predicates, predicate) >= 0;

    /// <summary>The longest probability text: "0." and the 28 places a decimal holds exactly, so no stated digit is ever rounded.</summary>
    public const int MaxProbabilityChars = 30;

    /// <summary>
    /// THE ONE SHAPE OF A PROBABILITY, the schema's CHECK on <c>org_forecast.probability</c> stated again for the writers: <c>0</c>,
    /// <c>1</c>, <c>0.</c> and one or more digits, or <c>1.</c> and one or more zeros — at most <see cref="MaxProbabilityChars"/>
    /// characters. Decided on the TEXT, never on a conversion: a prefix that merely looks like a number, or a value a REAL or a
    /// decimal would round into the range, is no probability.
    /// </summary>
    public static bool IsProbabilityText(string? text) =>
        text is { Length: > 0 and <= MaxProbabilityChars }
        && (text is "0" or "1"
            || (text.Length > 2 && text.StartsWith("0.", StringComparison.Ordinal) && text.AsSpan(2).IndexOfAnyExceptInRange('0', '9') < 0)
            || (text.Length > 2 && text.StartsWith("1.", StringComparison.Ordinal) && text.AsSpan(2).IndexOfAnyExcept('0') < 0));
}

/// <summary>
/// A FORECAST AS ITS FORECASTER STATES IT, before the app has validated it or minted an id: the predicate, its subject (a version
/// or a mandate id; null on <c>mandate-assign</c>, where it is the mandate being issued), the target, the date by which it
/// resolves and the probability — all claims.
/// </summary>
public sealed record ForecastClaim(string Predicate, string? Subject, string Target, DateTimeOffset By, decimal Probability);

/// <summary>THE APP'S RESOLUTION WORDS: what a resolution rests on.</summary>
public static class ForecastOutcome
{
    public const string Yes = "yes", No = "no", Void = "void";
}

/// <summary>One <c>org_decision</c> row: never updated; its rationale is the ledger entry <see cref="LedgerEntryId"/>.</summary>
public sealed record DecisionRow(
    string Id, string Position, string? Attempt, string Kind, IReadOnlyList<string> Options, string Choice,
    IReadOnlyList<OrgRef> Refs, long LedgerEntryId, DateTimeOffset At);

/// <summary>
/// One <c>org_forecast</c> row: on exactly one of <see cref="DecisionId"/> and <see cref="MandateId"/>. The predicate, subject,
/// target, date and probability are the forecaster's claim; <see cref="Scored"/> and <see cref="UnscoredWhy"/> are the app's.
/// </summary>
public sealed record ForecastRow(
    string Id, string? DecisionId, string? MandateId, string Forecaster, string? Attempt, int Vocabulary, string Predicate,
    string Subject, string Target, DateTimeOffset ResolveBy, decimal Probability, bool Scored, string? UnscoredWhy,
    DateTimeOffset At);

/// <summary>One <c>org_forecast_resolution</c> row — the APP'S: yes, no or void, and the record it rests on.</summary>
public sealed record ResolutionRow(string ForecastId, string Outcome, string RecordKind, string RecordId, string Why, DateTimeOffset At);

/// <summary>One <c>org_escalation</c> row. <see cref="Recipient"/> null is the owner: a record, with no wake.</summary>
public sealed record EscalationRow(
    string Id, string Sender, string? Attempt, string UnitId, string? MandateId, string? Recipient, string Reason,
    IReadOnlyList<string> Skipped, string? WakeId, DateTimeOffset At);

/// <summary>One <c>org_lateral</c> row: the sender the app stamped, the recipient, the text and refs — claims, stored verbatim.</summary>
public sealed record LateralRow(
    string Id, string Sender, string? Attempt, string Recipient, string Text, IReadOnlyList<OrgRef> Refs, DateTimeOffset At);

/// <summary>
/// DECISIONS, FORECASTS, ESCALATIONS AND LATERAL NOTES, AS ONLY <see cref="OrgStore"/> WRITES THEM (<c>U-org-decisions</c>, O5b,
/// rung 41; ORCHESTRATION §§ 2.3, 2.9–2.11; ORGANISATION §§ 4, 5). Every writer is one <see cref="Database.Write"/> and INSERTS
/// only — the five tables refuse UPDATE and DELETE by trigger.
///
/// <para><b>Measurement and claim stay apart.</b> Options, choice, rationale, a forecast's probability, an escalation's reason and a
/// note's text are CLAIMS, stored verbatim. A forecast's validation and <c>scored</c>, its resolution and the record that resolution
/// rests on, and an escalation's recipient are the APP'S, each in its own column or row.</para>
/// </summary>
public sealed partial class OrgStore
{
    /// <summary>The prefixes of the ids the app mints for these records; a caller never names one.</summary>
    public const string DecisionPrefix = "dec-", ForecastPrefix = "fc-", EscalationPrefix = "esc-", LateralPrefix = "lat-";

    internal static string Mint(string prefix) =>
        prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    /// <summary>
    /// WHY <paramref name="refs"/> CANNOT BE CITED, in words, or null: more than the bound, a kind outside <see cref="OrgRefKind"/>,
    /// an id empty or too long, or a record that is not in the app's ledgers. Read inside the writer's own write.
    /// </summary>
    string? RefsRefusal(IReadOnlyList<OrgRef> refs)
    {
        if (refs.Count > DecisionLimits.MaxRefs)
            return $"at most {DecisionLimits.MaxRefs} refs are cited at once.";
        foreach (var r in refs)
        {
            if (!OrgRefKind.IsKnown(r.Kind))
                return $"'{r.Kind}' is no kind of app record a ref names: {string.Join(" | ", OrgRefKind.All)}.";
            if (string.IsNullOrWhiteSpace(r.Id) || r.Id.Length > DecisionLimits.MaxRefIdChars)
                return $"a ref names its record's id, in at most {DecisionLimits.MaxRefIdChars} characters.";
            if (!RefExists(r))
                return $"{r.Kind} '{r.Id}' is not in the app's ledgers: a ref cites a record that is there.";
        }
        return null;
    }

    bool RefExists(OrgRef r)
    {
        var (table, column) = r.Kind switch
        {
            OrgRefKind.Version => ("strategy_version", "id"),
            OrgRefKind.Run => ("strategy_run", "id"),
            OrgRefKind.Promotion => ("strategy_promotion", "id"),
            OrgRefKind.Ledger => ("ledger_entry", "id"),
            OrgRefKind.Mandate => ("org_mandate", "id"),
            OrgRefKind.Delivery => ("org_delivery", "id"),
            OrgRefKind.Decision => ("org_decision", "id"),
            OrgRefKind.Escalation => ("org_escalation", "id"),
            _ => (null, null)
        };
        if (table is null) return false;
        object id = r.Id;
        if (r.Kind == OrgRefKind.Ledger)
        {
            if (!long.TryParse(r.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var entry)) return false;
            id = entry;
        }
        using var c = db.Cmd($"SELECT EXISTS (SELECT 1 FROM {table} WHERE {column} = $id)", ("$id", id));
        return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    const string DecisionCols = "id, position, attempt_id, kind, options, choice, refs, ledger_entry_id, at";

    /// <summary>
    /// RECORDS ONE DECISION OF <paramref name="position"/>: the options it considered and its choice (claims, stored verbatim), the
    /// refs it cites (each checked to exist), and its rationale as the position's own <see cref="LedgerKind.Decision"/> entry in the
    /// research ledger (marked claim) — the entry and the row in ONE write, or, refused in words, nothing. The id is the app's.
    /// </summary>
    /// <exception cref="TradeAgentException">Refused; nothing was written.</exception>
    public DecisionRow Decide(string position, string? attempt, string kind, IReadOnlyList<string> options, string choice,
        string rationale, IReadOnlyList<OrgRef> refs, IReadOnlyList<ForecastClaim> forecasts, DateTimeOffset now) => db.Write(_ =>
    {
        if (IsAppPrincipal(position) || Position(position) is not { Status: OrgPositionStatus.Active })
            throw Refused($"'{position}' is no active position: a decision is recorded under the position whose launch made it.");
        if (OrgDecisionKind.IsMandatory(kind))
            throw Refused($"a '{kind}' decision is written by the verb that performs it, in that act's own write — never by "
                          + $"'decision-record', which writes '{OrgDecisionKind.Routine}' decisions only.");
        if (kind != OrgDecisionKind.Routine)
            throw Refused($"'{kind}' is no kind of decision: '{OrgDecisionKind.Routine}' — the mandatory kinds "
                          + $"({string.Join(", ", OrgDecisionKind.Mandatory)}) are written by the verbs that perform them.");
        if (options.Count is 0 or > DecisionLimits.MaxOptions)
            throw Refused($"a decision names the options it considered, 1 to {DecisionLimits.MaxOptions}.");
        foreach (var o in options.Append(choice))
            if (string.IsNullOrWhiteSpace(o) || o.Length > DecisionLimits.MaxClaimChars)
                throw Refused($"each option and the choice are your words, at most {DecisionLimits.MaxClaimChars} characters, never empty.");
        if (RefsRefusal(refs) is { } badRef) throw Refused(badRef);
        var bytes = System.Text.Encoding.UTF8.GetByteCount(Json.Write(new { options, choice, rationale, refs }));
        if (bytes > DecisionLimits.MaxRecordBytes)
            throw Refused($"this decision is {bytes:N0} bytes as TradeAgent writes it (a character outside plain ASCII costs up to "
                          + $"six), and a decision holds at most {DecisionLimits.MaxRecordBytes:N0} so it is always read whole; "
                          + "say it shorter — longer is refused, never cut.");

        // THE RATIONALE FIRST, in this same write: the row points at the entry, and neither stands without the other.
        var entry = new ResearchLedger(db, () => now).AddDecisionRationale(position, attempt, rationale);
        var row = new DecisionRow(Mint(DecisionPrefix), position, attempt, kind, options, choice, refs, entry.Id, now);
        using (var c = db.Cmd($"""
            INSERT INTO org_decision({DecisionCols}) VALUES($id,$p,$attempt,$kind,$options,$choice,$refs,$entry,$at)
            """,
            ("$id", row.Id), ("$p", position), ("$attempt", attempt), ("$kind", kind),
            ("$options", JsonSerializer.Serialize(options)), ("$choice", choice), ("$refs", JsonSerializer.Serialize(refs)),
            ("$entry", entry.Id), ("$at", Sql.T(now))))
            c.ExecuteNonQuery();
        if (forecasts.Count > DecisionLimits.MaxForecasts)
            throw Refused($"a decision carries at most {DecisionLimits.MaxForecasts} forecasts.");
        foreach (var f in forecasts) AddForecast(position, attempt, row.Id, null, f, now);
        return row;
    });

    /// <summary>
    /// ATTACHES ONE FORECAST TO A RECORD OF THE CALLER'S: its own decision (<c>dec-…</c>), or an open mandate it decides
    /// (<c>man-…</c>). Validated and scored by the app (<see cref="AddForecast"/>) in one write — or refused in words, nothing written.
    /// </summary>
    /// <exception cref="TradeAgentException">Refused; nothing was written.</exception>
    public ForecastRow Forecast(string position, string? attempt, string on, ForecastClaim claim, DateTimeOffset now) => db.Write(_ =>
    {
        if (on.StartsWith(DecisionPrefix, StringComparison.Ordinal))
        {
            var d = Decision(on) ?? throw Refused($"'{on}' is no decision.");
            if (d.Position != position)
                throw Refused($"'{on}' is {d.Position}'s decision, not yours: a forecast is attached to your own decision, or to a mandate you decide.");
            return AddForecast(position, attempt, d.Id, null, claim, now);
        }
        if (on.StartsWith(MandatePrefix, StringComparison.Ordinal))
            return AddForecast(position, attempt, null, DecidedBy(on, position).Id, claim, now);
        throw Refused($"'{on}' is neither a decision ({DecisionPrefix}…) nor a mandate ({MandatePrefix}…): a forecast is attached to one of them.");
    });

    /// <summary>
    /// ONE FORECAST, VALIDATED AND SCORED BY THE APP, INSIDE ITS CALLER'S WRITE: the predicate and target from
    /// <see cref="ForecastVocabulary"/> v1; a subject that is there; a date in the future and inside the horizon; a probability from 0
    /// to 1; an outcome NOT already measured — a version with a verdict, a mandate that is closed or already reached the target —
    /// since a forecast is timestamped before its outcome (precommitment); at most <see cref="DecisionLimits.MaxForecasts"/> on one
    /// record. <c>scored</c> is computed here (<see cref="Unscored"/>), never stated by the forecaster.
    /// </summary>
    ForecastRow AddForecast(string forecaster, string? attempt, string? decisionId, string? mandateId, ForecastClaim claim, DateTimeOffset now)
    {
        if (!ForecastVocabulary.IsKnown(claim.Predicate))
            throw Refused($"'{claim.Predicate}' is no predicate of the forecast vocabulary v{ForecastVocabulary.Version}: "
                          + $"{string.Join(" | ", ForecastVocabulary.Predicates)}. A prediction outside it stays a claim — write it in your rationale.");
        var targets = ForecastVocabulary.Targets(claim.Predicate);
        if (!targets.Contains(claim.Target))
            throw Refused($"'{claim.Target}' is no target of {claim.Predicate}: {string.Join(" | ", targets)}.");
        if (claim.By <= now)
            throw Refused($"the forecast's date {claim.By:u} is not in the future: a forecast is made before its outcome.");
        if (claim.By > now + DecisionLimits.ForecastHorizon)
            throw Refused($"the forecast's date {claim.By:u} is beyond the {DecisionLimits.ForecastHorizon.TotalDays:N0}-day horizon.");
        if (claim.Probability is < 0m or > 1m || !ForecastVocabulary.IsProbabilityText(Sql.D(claim.Probability)))
            throw Refused($"{claim.Probability.ToString(CultureInfo.InvariantCulture)} is no probability: a number from 0 to 1.");
        var subject = claim.Subject ?? mandateId
            ?? throw Refused("a forecast names its subject: the version or the mandate it is about.");
        if (subject.Length > DecisionLimits.MaxRefIdChars)
            throw Refused($"a forecast's subject is an id of at most {DecisionLimits.MaxRefIdChars} characters.");

        string? unscored = null;
        if (claim.Predicate == ForecastVocabulary.VerdictClass)
        {
            if (!RefExists(new OrgRef(OrgRefKind.Version, subject)))
                throw Refused($"'{subject}' is no strategy version the app recorded.");
            using var v = db.Cmd("SELECT verdict FROM strategy_promotion WHERE version_id=$v ORDER BY at, rowid LIMIT 1", ("$v", subject));
            if (v.ExecuteScalar() is string verdict)
                throw Refused($"version {subject} already has a verdict ({verdict}): a forecast on an outcome already measured is no forecast.");
        }
        else
        {
            var m = Mandate(subject) ?? throw Refused($"'{subject}' is no mandate.");
            if (!IsOpen(m.Id))
                throw Refused($"mandate {m.Id} is {MandateStatus(m.Id)}: a forecast on an outcome already measured is no forecast.");
            if (MandateEvents(m.Id).Any(e => e.Kind == claim.Target))
                throw Refused($"mandate {m.Id} has already been {claim.Target}: a forecast on an outcome already measured is no forecast.");
            unscored = Unscored(forecaster, m);
        }

        var on = decisionId ?? mandateId!;
        if (ForecastsOn(on).Count >= DecisionLimits.MaxForecasts)
            throw Refused($"{on} carries {DecisionLimits.MaxForecasts} forecasts already, the most one record holds.");

        var row = new ForecastRow(Mint(ForecastPrefix), decisionId, mandateId, forecaster, attempt, ForecastVocabulary.Version,
            claim.Predicate, subject, claim.Target, claim.By, claim.Probability, unscored is null, unscored, now);
        using var c = db.Cmd($"""
            INSERT INTO org_forecast({ForecastCols})
            VALUES($id,$d,$m,$f,$attempt,$vocabulary,$predicate,$subject,$target,$by,$probability,$scored,$why,$at)
            """,
            ("$id", row.Id), ("$d", decisionId), ("$m", mandateId), ("$f", forecaster), ("$attempt", attempt),
            ("$vocabulary", row.Vocabulary), ("$predicate", row.Predicate), ("$subject", subject), ("$target", row.Target),
            ("$by", Sql.T(row.ResolveBy)), ("$probability", Sql.D(row.Probability)), ("$scored", row.Scored ? 1 : 0),
            ("$why", unscored), ("$at", Sql.T(now)));
        c.ExecuteNonQuery();
        return row;
    }

    /// <summary>
    /// WHY A FORECAST ON <paramref name="m"/>'S STATE IS NOT SCORED, or null when it is (ORGANISATION § 5, R17 #5): a predicate is
    /// scored only when neither the forecaster nor its subtree controls the outcome — and the decider and the holder of a mandate
    /// control whether it is accepted, rejected, cancelled or lets its deadline pass. Recorded, never scored, in fixed words.
    /// </summary>
    string? Unscored(string forecaster, MandateRow m)
    {
        if (m.Decider == forecaster) return "the forecaster decides this mandate";
        if (m.Holder == forecaster) return "the forecaster holds this mandate";
        var subtree = SubtreeOf(forecaster).Select(u => u.Id).ToHashSet(StringComparer.Ordinal);
        if (Position(m.Decider) is { } decider && subtree.Contains(decider.UnitId)) return "this mandate's decider is in the forecaster's subtree";
        if (Position(m.Holder) is { } holder && subtree.Contains(holder.UnitId)) return "this mandate's holder is in the forecaster's subtree";
        return null;
    }

    /// <summary>One decision as recorded, or null.</summary>
    public DecisionRow? Decision(string id) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {DecisionCols} FROM org_decision WHERE id=$id", ("$id", id));
        using var r = c.ExecuteReader();
        return r.Read() ? ReadDecision(r) : null;
    });

    /// <summary>Every decision <paramref name="position"/> recorded, newest first.</summary>
    public IReadOnlyList<DecisionRow> DecisionsOf(string position) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {DecisionCols} FROM org_decision WHERE position=$p ORDER BY at DESC, rowid DESC", ("$p", position));
        using var r = c.ExecuteReader();
        var rows = new List<DecisionRow>();
        while (r.Read()) rows.Add(ReadDecision(r));
        return (IReadOnlyList<DecisionRow>)rows;
    });

    static DecisionRow ReadDecision(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), Sql.S(r.GetValue(2)), r.GetString(3), JsonSerializer.Deserialize<string[]>(r.GetString(4)) ?? [],
        r.GetString(5), ReadRefs(r.GetString(6)), r.GetInt64(7), Sql.Time(r.GetValue(8)));

    const string ForecastCols =
        "id, decision_id, mandate_id, forecaster, attempt_id, vocabulary, predicate, subject, target, resolve_by, probability, " +
        "scored, unscored_why, at";

    /// <summary>The forecasts on a decision or a mandate (by its id), oldest first.</summary>
    public IReadOnlyList<ForecastRow> ForecastsOn(string recordId) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {ForecastCols} FROM org_forecast WHERE decision_id=$id OR mandate_id=$id ORDER BY at, rowid",
            ("$id", recordId));
        using var r = c.ExecuteReader();
        var rows = new List<ForecastRow>();
        while (r.Read()) rows.Add(ReadForecast(r));
        return (IReadOnlyList<ForecastRow>)rows;
    });

    /// <summary>The app's resolution of a forecast, or null while it is open.</summary>
    public ResolutionRow? Resolution(string forecastId) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT forecast_id, outcome, record_kind, record_id, why, at FROM org_forecast_resolution WHERE forecast_id=$id",
            ("$id", forecastId));
        using var r = c.ExecuteReader();
        return r.Read()
            ? new ResolutionRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), Sql.Time(r.GetValue(5)))
            : null;
    });

    /// <summary>The forecasts with no resolution yet, oldest first — the resolver's worklist.</summary>
    public IReadOnlyList<ForecastRow> OpenForecasts() => db.Read(_ =>
    {
        using var c = db.Cmd($"""
            SELECT {ForecastCols} FROM org_forecast f
             WHERE NOT EXISTS (SELECT 1 FROM org_forecast_resolution r WHERE r.forecast_id = f.id)
             ORDER BY at, rowid
            """);
        using var r = c.ExecuteReader();
        var rows = new List<ForecastRow>();
        while (r.Read()) rows.Add(ReadForecast(r));
        return (IReadOnlyList<ForecastRow>)rows;
    });

    /// <summary>
    /// THE APP'S RESOLUTION OF ONE FORECAST (<see cref="ForecastResolver"/>), one immutable row, in its own write: INSERT … ON CONFLICT
    /// DO NOTHING, so a second pass or a restart writes nothing twice and never replaces the first. Answers whether it wrote one.
    /// Raises no wake (RULING Q9).
    /// </summary>
    internal bool RecordResolution(string forecastId, string outcome, string recordKind, string recordId, string why, DateTimeOffset at) =>
        db.Write(_ =>
        {
            using var c = db.Cmd("""
                INSERT INTO org_forecast_resolution(forecast_id, outcome, record_kind, record_id, why, at)
                VALUES($f, $outcome, $kind, $id, $why, $at)
                ON CONFLICT(forecast_id) DO NOTHING
                """, ("$f", forecastId), ("$outcome", outcome), ("$kind", recordKind), ("$id", recordId), ("$why", why), ("$at", Sql.T(at)));
            return c.ExecuteNonQuery() == 1;
        });

    static ForecastRow ReadForecast(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        r.GetString(0), Sql.S(r.GetValue(1)), Sql.S(r.GetValue(2)), r.GetString(3), Sql.S(r.GetValue(4)), r.GetInt32(5),
        r.GetString(6), r.GetString(7), r.GetString(8), Sql.Time(r.GetValue(9)),
        decimal.Parse(r.GetString(10), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture), r.GetInt64(11) != 0,
        Sql.S(r.GetValue(12)), Sql.Time(r.GetValue(13)));

    // ---- escalations ----------------------------------------------------------------------------------------------------

    const string EscalationCols = "id, sender, attempt_id, unit_id, mandate_id, to_position, reason, skipped, wake_id, at";

    /// <summary>
    /// A HEAD ESCALATES (ORGANISATION § 4; RULINGS Q6, Q7): the APP chooses the recipient, walking UP the chain from the unit the
    /// caller heads — past every VACANT head and every head whose RECORDED seat cannot carry the organisation's verbs (its latest
    /// launch's assurance below <c>CapabilityPolicy.Minimum(DecisionVerbs)</c> — by capability, never by naming the legacy pair, so
    /// a confined <c>research</c> seat later receives them) — and writes the escalation and ONE <c>escalation</c> wake to that head in
    /// one write; at the root it is addressed to the OWNER as a record only — no wake, no pipe path (O7 shows it). Refused in words,
    /// nothing written: a caller that heads no unit, a mandate it neither decides nor holds, a second escalation of one mandate, a
    /// third without a mandate in one UTC day.
    /// </summary>
    /// <exception cref="TradeAgentException">Refused; nothing was written.</exception>
    public EscalationRow RaiseEscalation(string sender, string? attempt, string reason, string? mandateId, DateTimeOffset now) => db.Write(_ =>
    {
        if (Standing(sender) is not { Status: OrgPositionStatus.Active, Kind: OrgPositionKind.Head })
            throw Refused($"'{sender}' heads no unit: a head escalates up its chain. A member tells its head through its mandate "
                          + "(mandate-decline) or a lateral note.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > DecisionLimits.MaxClaimChars)
            throw Refused($"an escalation says why, in your words: at most {DecisionLimits.MaxClaimChars} characters.");
        var day = now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (mandateId is not null)
        {
            var m = Mandate(mandateId) ?? throw Refused($"'{mandateId}' is no mandate.");
            if (m.Decider != sender && m.Holder != sender)
                throw Refused($"'{mandateId}' is neither decided nor held by you: an escalation names a mandate of your own.");
            using var once = db.Cmd("SELECT id FROM org_escalation WHERE sender=$s AND mandate_id=$m", ("$s", sender), ("$m", m.Id));
            if (once.ExecuteScalar() is string earlier)
                throw Refused($"you escalated {m.Id} already ({earlier}): one escalation per mandate wakes the chain once.");
        }
        else
        {
            using var today = db.Cmd("SELECT COUNT(*) FROM org_escalation WHERE sender=$s AND mandate_id IS NULL AND day=$d",
                ("$s", sender), ("$d", day));
            if (Convert.ToInt32(today.ExecuteScalar(), CultureInfo.InvariantCulture) >= DecisionLimits.MandatelessPerDay)
                throw Refused($"you have raised {DecisionLimits.MandatelessPerDay} escalations without a mandate today (UTC), the most "
                              + "one head may: name the mandate it is about, or wait for tomorrow.");
        }

        var (start, recipient, skipped) = Walk(sender);
        var id = Mint(EscalationPrefix);
        var wake = recipient is null ? null : MissionEventIds.EscalationRaised(id);
        using (var c = db.Cmd($"""
            INSERT INTO org_escalation({EscalationCols}, day)
            VALUES($id,$s,$attempt,$unit,$m,$to,$reason,$skipped,$wake,$at,$day)
            """,
            ("$id", id), ("$s", sender), ("$attempt", attempt), ("$unit", start), ("$m", mandateId), ("$to", recipient),
            ("$reason", reason), ("$skipped", JsonSerializer.Serialize(skipped)), ("$wake", wake), ("$at", Sql.T(now)), ("$day", day)))
            c.ExecuteNonQuery();
        // ONE WAKE, TO ONE HEAD, ONCE — keyed by the escalation's id. The owner is never woken here: a record, shown by the app.
        if (wake is not null)
            new MissionEventStore(db).Raise(wake, MissionEventKind.Escalation, now, payload: id, role: recipient);
        return Escalation(id)!;
    });

    /// <summary>
    /// THE CHAIN ABOVE <paramref name="sender"/>, AS THE APP WALKS IT: from the topmost unit it heads, up through each parent — the
    /// first head that is active, not the sender, and whose RECORDED seat carries the organisation's verbs; the root's head is the
    /// owner (null). Every unit passed over is named with why, in the record.
    /// </summary>
    (string Start, string? Recipient, List<string> Skipped) Walk(string sender)
    {
        var headed = Units().Where(u => u.HeadPositionId == sender).ToList();
        var ids = headed.Select(u => u.Id).ToHashSet(StringComparer.Ordinal);
        var start = headed.First(u => !Ancestors(u.Id).Any(a => ids.Contains(a.Id)));
        var minimum = CapabilityPolicy.Minimum(Capability.DecisionVerbs);
        var skipped = new List<string>();
        foreach (var unit in Ancestors(start.Id))
        {
            if (unit.IsRoot) return (start.Id, null, skipped);
            if (unit.HeadPositionId is not { } head) { skipped.Add($"{unit.Id}: no head (vacant)"); continue; }
            if (head == sender) { skipped.Add($"{unit.Id}: headed by you"); continue; }
            if (Position(head) is not { Status: OrgPositionStatus.Active })
            {
                skipped.Add($"{unit.Id}: its head {head} is not active (vacant)");
                continue;
            }
            var recorded = RecordedAssurance(head);
            if (recorded is not { } assurance || assurance < minimum)
            {
                skipped.Add($"{unit.Id}: its head {head}'s recorded seat ({SeatName(recorded)}) cannot carry the organisation's verbs, "
                            + $"which need at least {SeatClasses.Name(minimum)}");
                continue;
            }
            return (start.Id, head, skipped);
        }
        return (start.Id, null, skipped);
    }

    /// <summary>
    /// THE ASSURANCE OF <paramref name="position"/>'S RECORDED SEAT: what the ledger wrote for its latest launch
    /// (<c>ai_attempt.assurance</c>, from the seat the app verified at admission), or null — never launched, or a word this build
    /// does not know, which is refused rather than guessed.
    /// </summary>
    SeatAssurance? RecordedAssurance(string position)
    {
        using var c = db.Cmd("SELECT assurance FROM ai_attempt WHERE role=$p ORDER BY started_at DESC, rowid DESC LIMIT 1", ("$p", position));
        var name = c.ExecuteScalar() as string;
        foreach (var a in Enum.GetValues<SeatAssurance>())
            if (SeatClasses.Name(a) == name) return a;
        return null;
    }

    static string SeatName(SeatAssurance? a) => a is { } known ? SeatClasses.Name(known) : "none recorded";

    /// <summary>One escalation as recorded, or null.</summary>
    public EscalationRow? Escalation(string id) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {EscalationCols} FROM org_escalation WHERE id=$id", ("$id", id));
        using var r = c.ExecuteReader();
        return r.Read() ? ReadEscalation(r) : null;
    });

    /// <summary>The escalations <paramref name="position"/> raised or received, newest first — never another position's.</summary>
    public IReadOnlyList<EscalationRow> EscalationsOf(string position) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {EscalationCols} FROM org_escalation WHERE sender=$p OR to_position=$p ORDER BY at DESC, rowid DESC",
            ("$p", position));
        using var r = c.ExecuteReader();
        var rows = new List<EscalationRow>();
        while (r.Read()) rows.Add(ReadEscalation(r));
        return (IReadOnlyList<EscalationRow>)rows;
    });

    static EscalationRow ReadEscalation(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), Sql.S(r.GetValue(2)), r.GetString(3), Sql.S(r.GetValue(4)), Sql.S(r.GetValue(5)), r.GetString(6),
        JsonSerializer.Deserialize<string[]>(r.GetString(7)) ?? [], Sql.S(r.GetValue(8)), Sql.Time(r.GetValue(9)));

    // ---- lateral notes ------------------------------------------------------------------------------------------------

    const string LateralCols = "id, sender, attempt_id, to_position, text, refs, at";

    /// <summary>
    /// A NOTE FROM ONE POSITION TO ANOTHER — information moves, authority does not (ORGANISATION § 4): the row, sender-stamped by the
    /// app, and NOTHING ELSE — no wake, no mandate event, no publication, no assignment; an approval inside it is void. The recipient
    /// reads it with <c>lateral-list</c>. Refused in words, nothing written: a recipient that is the sender, an app principal, the
    /// legacy pair (unconfined seats that hold no organisation verb to read it with, and <c>operations</c>, whose trading no note
    /// shapes — ORDER-001), not a position or not active; a text empty or too long; a ref that is not there.
    /// </summary>
    /// <exception cref="TradeAgentException">Refused; nothing was written.</exception>
    public LateralRow SendLateral(string sender, string? attempt, string to, string text, IReadOnlyList<OrgRef> refs, DateTimeOffset now) =>
        db.Write(_ =>
        {
            if (IsAppPrincipal(sender) || Position(sender) is not { Status: OrgPositionStatus.Active })
                throw Refused($"'{sender}' is no active position: a note is sent by the position whose launch wrote it.");
            if (to == sender)
                throw Refused("a note goes to another position, never to yourself — your own beliefs go in the research ledger.");
            if (IsAppPrincipal(to))
                throw Refused($"'{to}' is one of the app's own principals ({string.Join(", ", AppPrincipals.All)}): code, never a position, and it reads no note.");
            if (CouncilRoles.IsKnown(to))
                throw Refused($"'{to}' is one of the legacy pair, on unconfined seats that hold no organisation verb to read a note with; "
                              + (to == CouncilRoles.Operations ? "and what operations trades is never shaped by a note (ORDER-001). " : "")
                              + "Escalate through your chain instead.");
            if (Position(to) is not { } recipient)
                throw Refused($"'{to}' is no position on the chart.");
            if (recipient.Status != OrgPositionStatus.Active)
                throw Refused($"'{to}' is {recipient.Status}, not active: a note goes to an active position.");
            if (string.IsNullOrWhiteSpace(text) || text.EnumerateRunes().Count() > ResearchLedger.MaxTextChars)
                throw Refused($"a note's text is your words, never empty, at most {ResearchLedger.MaxTextChars:N0} characters; longer is refused, never cut.");
            if (RefsRefusal(refs) is { } badRef) throw Refused(badRef);
            var bytes = System.Text.Encoding.UTF8.GetByteCount(Json.Write(new { text, refs }));
            if (bytes > DecisionLimits.MaxRecordBytes)
                throw Refused($"this note is {bytes:N0} bytes as TradeAgent writes it, and a note holds at most {DecisionLimits.MaxRecordBytes:N0} "
                              + "so it is always read whole; say it shorter.");

            var row = new LateralRow(Mint(LateralPrefix), sender, attempt, to, text, refs, now);
            using var c = db.Cmd($"INSERT INTO org_lateral({LateralCols}) VALUES($id,$s,$attempt,$to,$text,$refs,$at)",
                ("$id", row.Id), ("$s", sender), ("$attempt", attempt), ("$to", to), ("$text", text),
                ("$refs", JsonSerializer.Serialize(refs)), ("$at", Sql.T(now)));
            c.ExecuteNonQuery();
            return row;
        });

    /// <summary>The notes <paramref name="position"/> sent or received, newest first — never another position's.</summary>
    public IReadOnlyList<LateralRow> LateralsOf(string position) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {LateralCols} FROM org_lateral WHERE sender=$p OR to_position=$p ORDER BY at DESC, rowid DESC", ("$p", position));
        using var r = c.ExecuteReader();
        var rows = new List<LateralRow>();
        while (r.Read())
            rows.Add(new LateralRow(r.GetString(0), r.GetString(1), Sql.S(r.GetValue(2)), r.GetString(3), r.GetString(4),
                ReadRefs(r.GetString(5)), Sql.Time(r.GetValue(6))));
        return (IReadOnlyList<LateralRow>)rows;
    });

    static IReadOnlyList<OrgRef> ReadRefs(string json) => JsonSerializer.Deserialize<OrgRef[]>(json) ?? [];
}

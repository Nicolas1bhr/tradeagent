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
        string rationale, IReadOnlyList<OrgRef> refs, DateTimeOffset now) => db.Write(_ =>
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
        return row;
    });

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

    static ForecastRow ReadForecast(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        r.GetString(0), Sql.S(r.GetValue(1)), Sql.S(r.GetValue(2)), r.GetString(3), Sql.S(r.GetValue(4)), r.GetInt32(5),
        r.GetString(6), r.GetString(7), r.GetString(8), Sql.Time(r.GetValue(9)),
        decimal.Parse(r.GetString(10), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture), r.GetInt64(11) != 0,
        Sql.S(r.GetValue(12)), Sql.Time(r.GetValue(13)));

    static IReadOnlyList<OrgRef> ReadRefs(string json) => JsonSerializer.Deserialize<OrgRef[]>(json) ?? [];
}

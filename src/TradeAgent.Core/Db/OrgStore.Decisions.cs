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

    static IReadOnlyList<OrgRef> ReadRefs(string json) => JsonSerializer.Deserialize<OrgRef[]>(json) ?? [];
}

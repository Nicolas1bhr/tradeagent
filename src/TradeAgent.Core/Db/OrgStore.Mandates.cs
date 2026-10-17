using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TradeAgent.Core.Db;

/// <summary>What a mandate asks for (<c>U-org-assignments-a</c>; ORCHESTRATION § 2.2): a task with an output shape, or an exploration.</summary>
public static class MandateKind
{
    public const string Task = "task";
    public const string Exploration = "exploration";

    public static readonly string[] All = [Task, Exploration];

    public static bool IsKnown(string? kind) => kind is not null && Array.IndexOf(All, kind) >= 0;
}

/// <summary>
/// HOW MUCH ROOM A HOLDER HAS (ORCHESTRATION § 2.2): <see cref="Open"/> lets it deviate and say so; <see cref="Exact"/> refuses a
/// delivery that names a deviation (RULING Q7) — the code half; the allocation is the rest.
/// </summary>
public static class MandateLatitude
{
    public const string Open = "open";
    public const string Exact = "exact";

    public static readonly string[] All = [Open, Exact];

    public static bool IsKnown(string? latitude) => latitude is not null && Array.IndexOf(All, latitude) >= 0;
}

/// <summary>
/// THE APP RECORDS A DELIVERY MAY NAME (RULING Q2: references to app records only, until <c>U-execution-environment</c>): a
/// strategy version, a run, a research-ledger entry or a registered lens — never a file.
/// </summary>
public static class MandateRefKind
{
    public const string Version = "version";
    public const string Run = "run";
    public const string Ledger = "ledger";
    public const string Lens = "lens";

    public static readonly string[] All = [Version, Run, Ledger, Lens];

    public static bool IsKnown(string? kind) => kind is not null && Array.IndexOf(All, kind) >= 0;
}

/// <summary>
/// WHAT A TASK MUST HAND BACK, from a closed list: a report, or at least one record of a <see cref="MandateRefKind"/>. An
/// exploration has none.
/// </summary>
public static class MandateShape
{
    public const string Report = "report";

    public static readonly string[] All = [Report, .. MandateRefKind.All];

    public static bool IsKnown(string? shape) => shape is not null && Array.IndexOf(All, shape) >= 0;
}

/// <summary>
/// THE V1 CHECKS THE APP RUNS ON A DELIVERY, IN ITS OWN WRITE — none runs code (RULING Q3: code only in a confined cell).
/// </summary>
public static class MandateCheck
{
    /// <summary>The report is at most <see cref="MandateLimits.ReportLines"/> lines — the relay's own cap on a report.</summary>
    public const string ReportSize = "report-size";

    /// <summary>Every record the delivery names exists.</summary>
    public const string RefsExist = "refs-exist";

    /// <summary>Every record the delivery names was produced by a launch charged to THIS mandate (the record's attempt, or its version's).</summary>
    public const string RefsOfThisMandate = "refs-of-this-mandate";

    /// <summary>A task's declared output shape is present: a report, or at least one record of the declared kind.</summary>
    public const string OutputShape = "output-shape";

    public static readonly string[] All = [ReportSize, RefsExist, RefsOfThisMandate, OutputShape];

    /// <summary>The checks a mandate of <paramref name="kind"/> is held to: all four for a task, the first three for an exploration.</summary>
    public static IReadOnlyList<string> For(string kind) =>
        kind == MandateKind.Task ? All : [ReportSize, RefsExist, RefsOfThisMandate];

    public static bool IsKnown(string? check) => check is not null && Array.IndexOf(All, check) >= 0;
}

/// <summary>
/// WHAT HAPPENED TO A MANDATE — the <c>org_mandate_event</c> vocabulary. A mandate's status IS its latest event; there is no
/// status column (append-only, ORCHESTRATION § 8).
/// </summary>
public static class MandateEventKind
{
    /// <summary>The decider issued it (<c>mandate-assign</c>).</summary>
    public const string Issued = "issued";

    /// <summary>The holder handed a sealed delivery back (<c>mandate-deliver</c>).</summary>
    public const string Delivered = "delivered";

    /// <summary>Code triaged a clean failure and woke the holder again; the decider was not woken.</summary>
    public const string Retry = "retry";

    /// <summary>Code stopped retrying and woke the decider: the retry limit, half the allocation, a decline, an unaffordable launch.</summary>
    public const string Escalated = "escalated";

    /// <summary>The holder declined it (<c>mandate-decline</c>), infeasible or ambiguous.</summary>
    public const string Declined = "declined";

    /// <summary>The decider accepted the exact delivery it was shown (<c>mandate-accept</c>). Closes the mandate.</summary>
    public const string Accepted = "accepted";

    /// <summary>The decider rejected a delivery, citing a failed check or a stated defect (<c>mandate-reject</c>); the holder is woken.</summary>
    public const string Rejected = "rejected";

    /// <summary>The decider cancelled it (<c>mandate-cancel</c>), with a reason. Closes the mandate and stops its spend.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>The sweep found it past its deadline. Closes the mandate and stops its spend.</summary>
    public const string Expired = "expired";

    public static readonly string[] All = [Issued, Delivered, Retry, Escalated, Declined, Accepted, Rejected, Cancelled, Expired];

    /// <summary>The events after which nothing more is owed or spent.</summary>
    public static readonly string[] Closing = [Accepted, Cancelled, Expired];

    public static bool IsKnown(string? kind) => kind is not null && Array.IndexOf(All, kind) >= 0;

    public static bool Closes(string? kind) => kind is not null && Array.IndexOf(Closing, kind) >= 0;
}

/// <summary>Why a holder declines (<c>mandate-decline</c>).</summary>
public static class MandateDecline
{
    public const string Infeasible = "infeasible";
    public const string Ambiguous = "ambiguous";

    public static readonly string[] All = [Infeasible, Ambiguous];

    public static bool IsKnown(string? reason) => reason is not null && Array.IndexOf(All, reason) >= 0;
}

/// <summary>THE BOUNDS ON WHAT A MANDATE AND A DELIVERY CARRY. Longer is refused, never cut.</summary>
public static class MandateLimits
{
    /// <summary>The issuer's objective and scope, each: claims, stored verbatim.</summary>
    public const int MaxObjectiveChars = 2_000;

    /// <summary>
    /// A REPORT'S LINES — the relay's own cap on a report (<c>CouncilRelay.ReportLines</c>, which a test holds equal). A longer
    /// report is SEALED and fails <see cref="MandateCheck.ReportSize"/>: that is a clean failure code triages, not a refusal.
    /// </summary>
    public const int ReportLines = 20;

    /// <summary>The most a report may hold at all; beyond it the delivery is refused and nothing is written.</summary>
    public const int MaxReportChars = 16_000;

    /// <summary>The most deviations and refs one delivery names, and the most a deviation's what, found or cost holds.</summary>
    public const int MaxDeviations = 10, MaxRefs = 20, MaxClaimChars = 500;

    /// <summary>
    /// THE MOST A DELIVERY TAKES ON THE WIRE (fix pass 1, item 3): its report, deviations and refs and the app's checks on them,
    /// as <see cref="Json.Write"/> writes them — escaping included, so a character outside ASCII costs the six or twelve bytes of
    /// its escape. A longer delivery is refused at the seal, never cut, so <c>mandate-show</c> always serves a delivery WHOLE
    /// beside its mandate inside <see cref="ResearchLedger.MaxReadBytes"/>: a decider never judges words it was not shown.
    /// </summary>
    public const int MaxDeliveryBytes = 32 * 1024;

    /// <summary>The retry limit when the issuer names none, and the most it may name.</summary>
    public const int DefaultRetryLimit = 2, MaxRetryLimit = 10;
}

/// <summary>A mandate as issued (<c>org_mandate</c>): never updated; its status is <see cref="MandateRow"/>'s latest event.</summary>
public sealed record MandateRow(
    string Id, string UnitId, string Decider, string Holder, string Kind, string Objective, string? Scope,
    string? OutputShape, IReadOnlyList<string> Checks, string Latitude, DateTimeOffset Deadline, decimal Allocation,
    int RetryLimit, string? Attempt, DateTimeOffset CreatedAt)
{
    public bool Exact => Latitude == MandateLatitude.Exact;
}

/// <summary>One <c>org_mandate_event</c> row.</summary>
public sealed record MandateEventRow(
    long Id, string MandateId, DateTimeOffset At, string Kind, string Actor, string? Attempt, string? Reason,
    IReadOnlyList<string> Evidence);

/// <summary>What a decider asks for, before the app has validated it or minted an id.</summary>
public sealed record MandateIssue(
    string Decider, string? Attempt, string Holder, string Kind, string Objective, string? Scope, string? OutputShape,
    bool Exact, DateTimeOffset Deadline, decimal Allocation, int RetryLimit);

/// <summary>
/// THE MEASURED SPEND OF ONE MANDATE — the app's, from <c>ai_attempt</c> by <c>mandate_id</c>: the cost of every resolved launch
/// (a LOST one keeps its reservation as its cost), the reservations still open, and how many resolved launches have NO cost —
/// unknown, which is never read as zero.
/// </summary>
public sealed record MandateSpend(decimal Spent, decimal Reserved, int Unknown, int Launches)
{
    public decimal Committed => Spent + Reserved;
}

/// <summary>A deviation a delivery names — what the holder changed, what it found, what it says it cost: ALL CLAIMS, stored verbatim.</summary>
public sealed record MandateDeviation(
    [property: System.Text.Json.Serialization.JsonPropertyName("what")] string What,
    [property: System.Text.Json.Serialization.JsonPropertyName("found")] string Found,
    [property: System.Text.Json.Serialization.JsonPropertyName("cost")] string Cost);

/// <summary>An app record a delivery cites (<see cref="MandateRefKind"/>): a lens is <c>id@version</c>, a ledger entry its number.</summary>
public sealed record MandateRef(
    [property: System.Text.Json.Serialization.JsonPropertyName("kind")] string Kind,
    [property: System.Text.Json.Serialization.JsonPropertyName("id")] string Id);

/// <summary>A sealed delivery (<c>org_delivery</c>): its id is the SHA-256 of its manifest; never updated.</summary>
public sealed record DeliveryRow(
    string Id, string MandateId, string? Attempt, string Report, IReadOnlyList<MandateDeviation> Deviations,
    IReadOnlyList<MandateRef> Refs, DateTimeOffset At);

/// <summary>The app's result of one check on one delivery (<c>org_check</c>).</summary>
public sealed record CheckRow(string DeliveryId, string Check, bool Passed, string Reason);

/// <summary>What a delivery came to: the seal, each check, the mandate's status after triage, and the wake code raised.</summary>
public sealed record DeliveryResult(DeliveryRow Delivery, IReadOnlyList<CheckRow> Checks, string Status, string? Wake);

/// <summary>The launch that registered a lens, as the tape's registry recorded it.</summary>
public sealed record LensOrigin(string? Attempt);

/// <summary>The launches a cited record came from: its own, and for a run its version's.</summary>
public sealed record RefOrigin(IReadOnlyList<string?> Attempts);

/// <summary>
/// THE MANDATE'S LIFE, AS ONLY <see cref="OrgStore"/> WRITES IT (<c>U-org-assignments-a</c>, rung 40; ORCHESTRATION §§ 2.1–2.4,
/// 2.9, 2.10). Every writer here is one <see cref="Database.Write"/>: the rows it adds, the events and the wakes it raises go in
/// together or not at all.
///
/// <para><b>Authority is checked again here, not only at the broker.</b> The gateway proves the CALLER (its position, and its
/// attempt's recorded seat assurance); the store checks what the chart and the mandate row say about that caller — a decider
/// assigns only into the subtree it heads, only the decider decides, only the holder delivers — so no caller of this class can
/// skip a rule by reaching it some other way (ORG-001).</para>
/// </summary>
public sealed partial class OrgStore
{
    /// <summary>The prefix of every mandate id; the app mints them, never a caller.</summary>
    public const string MandatePrefix = "man-";

    /// <summary>Who an app-written event names as its actor.</summary>
    public const string AppActor = OrgDecider.App;

    const string MandateCols =
        "id, unit_id, decider, holder, kind, objective, scope, output_shape, checks, latitude, deadline, allocation_ai, " +
        "retry_limit, attempt_id, created_at";

    const string MandateEventCols = "id, mandate_id, at, kind, actor, attempt_id, reason, evidence";

    /// <summary>A new mandate id: the prefix and twelve random hex digits.</summary>
    public static string MintMandate() =>
        MandatePrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    /// <summary>
    /// THE UNITS <paramref name="position"/> HEADS, AND EVERY UNIT BELOW THEM — the only place it may assign into (§ 2.3). Empty
    /// for a position that heads nothing.
    /// </summary>
    public IReadOnlyList<OrgUnitRow> SubtreeOf(string position)
    {
        var heads = Units().Where(u => u.HeadPositionId == position).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var tree = new List<OrgUnitRow>();
        foreach (var unit in heads)
            foreach (var u in Subtree(unit.Id))
                if (seen.Add(u.Id)) tree.Add(u);
        return tree;
    }

    /// <summary>
    /// WHY <paramref name="issue"/> CANNOT BE ISSUED, in words, or null. The chart's rules (ORCHESTRATION § 2.3, ORG-001; RULING
    /// Q4) and the mandate's own bounds, each refused by name: a holder that is the decider itself, an app principal,
    /// <c>operations</c>, no active position, or outside the subtree the decider heads; an unknown kind, shape or latitude; an
    /// objective too long; a deadline not in the future; an allocation not above zero; a retry limit out of range.
    /// </summary>
    public string? AssignRefusal(MandateIssue issue, DateTimeOffset now)
    {
        if (issue.Holder == issue.Decider)
            return $"'{issue.Holder}' is your own position: a mandate is given to someone in the subtree you head, never to yourself.";
        if (IsAppPrincipal(issue.Holder))
            return $"'{issue.Holder}' is one of the app's own principals ({string.Join(", ", AppPrincipals.All)}): code, never a position, and it holds no mandate.";
        // RULING Q4 — THE OWNER'S: operations places orders, and an order is never a mandate's to shape (no instrument, side, size or
        // timing, § 2.2). Refused whoever asks, before the chart is read.
        if (issue.Holder == CouncilRoles.Operations)
            return $"'{CouncilRoles.Operations}' holds no mandate: what the {CouncilRoles.Title(CouncilRoles.Operations)} trades is never shaped by an assignment (ORCHESTRATION § 2.2).";
        if (Position(issue.Holder) is not { } holder)
            return $"'{issue.Holder}' is no position on the chart.";
        if (holder.Status != OrgPositionStatus.Active)
            return $"'{issue.Holder}' is {holder.Status}, not active: only an active position holds a mandate.";

        var subtree = SubtreeOf(issue.Decider);
        if (subtree.Count == 0)
            return $"'{issue.Decider}' heads no unit, so there is no subtree it may assign into.";
        if (!subtree.Any(u => u.Id == holder.UnitId))
            return $"'{issue.Holder}' is not in the subtree you head ({string.Join(", ", subtree.Select(u => u.Id))}): a mandate goes only into your own subtree.";

        if (!MandateKind.IsKnown(issue.Kind))
            return $"'{issue.Kind}' is no mandate kind: {string.Join(" | ", MandateKind.All)}.";
        if (issue.Kind == MandateKind.Task && !MandateShape.IsKnown(issue.OutputShape))
            return $"a task names its output shape, one of {string.Join(" | ", MandateShape.All)} — '{issue.OutputShape}' is none of them.";
        if (issue.Kind == MandateKind.Exploration && issue.OutputShape is not null)
            return "an exploration has no output shape: name the shape only for a task.";
        if (string.IsNullOrWhiteSpace(issue.Objective))
            return "a mandate states its objective.";
        if (issue.Objective.Length > MandateLimits.MaxObjectiveChars || issue.Scope?.Length > MandateLimits.MaxObjectiveChars)
            return $"the objective and the scope hold at most {MandateLimits.MaxObjectiveChars:N0} characters each; longer is refused, never cut.";
        if (issue.Deadline <= now)
            return $"the deadline {issue.Deadline:u} is not in the future.";
        if (issue.Allocation <= 0m)
            return $"the AI allocation must be above zero — '{issue.Allocation.ToString(CultureInfo.InvariantCulture)}' is not.";
        if (issue.RetryLimit is < 1 or > MandateLimits.MaxRetryLimit)
            return $"the retry limit is a whole number from 1 to {MandateLimits.MaxRetryLimit}.";
        return null;
    }

    /// <summary>
    /// ISSUES A MANDATE: the row, its <c>issued</c> event and ONE <c>mandate</c> wake to its holder, in one write — or, refused in
    /// words (<see cref="AssignRefusal"/>), nothing. The id is the app's (<see cref="MintMandate"/>).
    /// </summary>
    /// <exception cref="TradeAgentException">The issue is refused; nothing was written.</exception>
    /// <param name="forecasts">The issuer's forecasts on this mandate (RULING Q5: optional), each attached to it in this same write
    /// (<c>U-org-decisions</c>) — a forecast on its own mandate's state is recorded, never scored: its decider controls it.</param>
    public MandateRow Assign(MandateIssue issue, DateTimeOffset now, IReadOnlyList<ForecastClaim>? forecasts = null) => db.Write(_ =>
    {
        if (AssignRefusal(issue, now) is { } refused)
            throw new TradeAgentException(ErrorCode.INVALID_REQUEST, refused + " Nothing was written.");

        var holderUnit = Position(issue.Holder)!.UnitId;
        // THE UNIT IT IS ISSUED FROM: the nearest unit the decider heads above (or at) the holder's.
        var heads = Units().Where(u => u.HeadPositionId == issue.Decider).Select(u => u.Id).ToHashSet(StringComparer.Ordinal);
        var unit = new[] { holderUnit }.Concat(Ancestors(holderUnit).Select(u => u.Id)).First(heads.Contains);

        var row = new MandateRow(MintMandate(), unit, issue.Decider, issue.Holder, issue.Kind, issue.Objective,
            issue.Scope, issue.OutputShape, MandateCheck.For(issue.Kind),
            issue.Exact ? MandateLatitude.Exact : MandateLatitude.Open, issue.Deadline, issue.Allocation, issue.RetryLimit,
            issue.Attempt, now);

        using (var c = db.Cmd($"""
            INSERT INTO org_mandate({MandateCols})
            VALUES($id,$unit,$decider,$holder,$kind,$objective,$scope,$shape,$checks,$latitude,$deadline,$allocation,
                   $retries,$attempt,$at)
            """,
            ("$id", row.Id), ("$unit", row.UnitId), ("$decider", row.Decider), ("$holder", row.Holder), ("$kind", row.Kind),
            ("$objective", row.Objective), ("$scope", row.Scope), ("$shape", row.OutputShape),
            ("$checks", JsonSerializer.Serialize(row.Checks)), ("$latitude", row.Latitude), ("$deadline", Sql.T(row.Deadline)),
            ("$allocation", Sql.D(row.Allocation)), ("$retries", row.RetryLimit), ("$attempt", row.Attempt),
            ("$at", Sql.T(now))))
            c.ExecuteNonQuery();

        var issued = AppendMandateEvent(row.Id, now, MandateEventKind.Issued, row.Decider, issue.Attempt, null, []);
        if (forecasts?.Count > DecisionLimits.MaxForecasts)
            throw Refused($"a mandate carries at most {DecisionLimits.MaxForecasts} forecasts.");
        foreach (var f in forecasts ?? []) AddForecast(row.Decider, issue.Attempt, null, row.Id, f, now);
        RaiseMandateWake(row, issued, now);
        return row;
    });

    /// <summary>
    /// CANCELS AN OPEN MANDATE — its decider only, with a reason: the <c>cancelled</c> event, and every untaken wake of it to the
    /// holder withdrawn, in one write. Its spend stops there: <see cref="AiAttemptStore.Begin"/> admits no launch for a closed
    /// mandate.
    /// </summary>
    /// <exception cref="TradeAgentException">Not the decider, not open, or no reason; nothing was written.</exception>
    public MandateEventRow Cancel(string mandateId, string caller, string? attempt, string why, DateTimeOffset now) => db.Write(_ =>
    {
        var m = DecidedBy(mandateId, caller);
        if (string.IsNullOrWhiteSpace(why) || why.Length > MandateLimits.MaxClaimChars)
            throw Refused($"a cancellation says why, in at most {MandateLimits.MaxClaimChars} characters.");
        var cancelled = AppendMandateEvent(m.Id, now, MandateEventKind.Cancelled, caller, attempt, why, []);
        new MissionEventStore(db).CloseMandateWakes(m.Id, now, MissionEventDisposition.Withdrawn, $"the mandate was cancelled: {why}");
        return cancelled;
    });

    /// <summary>
    /// A LAUNCH ITS MANDATE REFUSED (<see cref="AiAdmission.Mandate"/>), recorded in one write: the mandate's untaken
    /// <paramref name="wakes"/> closed by no launch with the refusal's sentence, so no pass offers them again, and — the mandate
    /// still open and not already escalated — an <c>escalated</c> event and ONE <c>escalation</c> wake to its decider. A closed
    /// mandate's wakes are only closed: there is nobody left to tell.
    /// </summary>
    public void LaunchRefused(string mandateId, IReadOnlyList<string> wakes, string why, DateTimeOffset now) => db.Write(_ =>
    {
        new MissionEventStore(db).CloseMandateWakes(mandateId, now, MissionEventDisposition.Refused, why, wakes);
        if (Mandate(mandateId) is not { } m || !IsOpen(m.Id) || MandateStatus(m.Id) == MandateEventKind.Escalated) return 0;
        Escalate(m, now, $"a launch under it was refused: {why}", []);
        return 1;
    });

    /// <summary>
    /// CODE STOPS RETRYING AND ASKS THE DECIDER: an <c>escalated</c> event by the app, every untaken <c>mandate</c> wake of the
    /// holder's closed by no launch — a retry queued before the escalation is an automatic retry, and those have stopped (fix
    /// pass 1, item 1) — and ONE <c>escalation</c> wake to the decider, keyed by that event. Called only inside a writer's own
    /// write — the escalation is never a second write after the one that found it. What the decider does next (reject, which
    /// owes the holder work again; cancel; accept) is the decider's; <see cref="AllocationRefusal"/> admits no launch meanwhile.
    /// </summary>
    MandateEventRow Escalate(MandateRow m, DateTimeOffset at, string reason, IReadOnlyList<string> evidence)
    {
        var escalated = AppendMandateEvent(m.Id, at, MandateEventKind.Escalated, AppActor, null, reason, evidence);
        var events = new MissionEventStore(db);
        events.CloseMandateWakes(m.Id, at, MissionEventDisposition.Withdrawn, $"code escalated the mandate to its decider: {reason}");
        events.Raise(MissionEventIds.Escalation(m.Id, escalated.Id), MissionEventKind.Escalation, at, payload: m.Id, role: m.Decider);
        return escalated;
    }

    // ---- what each position owns (RoleObjectives v3) ----------------------------------------------------------------------

    /// <summary>The open mandates <paramref name="holder"/> owes work on: issued, retried or sent back by a rejection.</summary>
    public IReadOnlyList<string> MandatesOwedBy(string holder) => OpenWith("holder", holder,
        [MandateEventKind.Issued, MandateEventKind.Retry, MandateEventKind.Rejected]);

    /// <summary>The open mandates <paramref name="decider"/> has a sealed delivery to judge on.</summary>
    public IReadOnlyList<string> DeliveriesToJudge(string decider) => OpenWith("decider", decider, [MandateEventKind.Delivered]);

    /// <summary>The open mandates code escalated to <paramref name="decider"/>.</summary>
    public IReadOnlyList<string> EscalationsFor(string decider) => OpenWith("decider", decider, [MandateEventKind.Escalated]);

    /// <summary>
    /// The mandates <paramref name="decider"/> issued that EXPIRED and whose expiry it has not yet been woken for — the escalation
    /// wake the sweep raised is still untaken.
    /// </summary>
    public IReadOnlyList<string> ExpiredUnread(string decider) => db.Read(_ =>
    {
        using var c = db.Cmd("""
            SELECT m.id FROM org_mandate m
             WHERE m.decider = $p
               AND (SELECT kind FROM org_mandate_event e WHERE e.mandate_id = m.id ORDER BY e.id DESC LIMIT 1) = $expired
               AND EXISTS (SELECT 1 FROM mission_event w WHERE w.kind = $escalation AND w.payload = m.id AND w.consumed_at IS NULL)
             ORDER BY m.created_at, m.id
            """, ("$p", decider), ("$expired", MandateEventKind.Expired), ("$escalation", MissionEventKind.Escalation));
        using var r = c.ExecuteReader();
        var ids = new List<string>();
        while (r.Read()) ids.Add(r.GetString(0));
        return (IReadOnlyList<string>)ids;
    });

    IReadOnlyList<string> OpenWith(string column, string position, string[] statuses) => db.Read(_ =>
    {
        var names = statuses.Select((_, i) => $"$s{i.ToString(CultureInfo.InvariantCulture)}").ToArray();
        using var c = db.Cmd($"""
            SELECT m.id FROM org_mandate m
             WHERE m.{column} = $p
               AND (SELECT kind FROM org_mandate_event e WHERE e.mandate_id = m.id ORDER BY e.id DESC LIMIT 1) IN ({string.Join(",", names)})
             ORDER BY m.created_at, m.id
            """, [("$p", (object?)position), .. names.Select((n, i) => (n, (object?)statuses[i]))]);
        using var r = c.ExecuteReader();
        var ids = new List<string>();
        while (r.Read()) ids.Add(r.GetString(0));
        return (IReadOnlyList<string>)ids;
    });

    // ---- the holder's verbs: decline and deliver ------------------------------------------------------------------------

    /// <summary>
    /// THE HOLDER DECLINES AN OPEN MANDATE, saying whether it is infeasible or ambiguous and why: the <c>declined</c> event, the
    /// holder's untaken wakes closed, and — code stopping there — the <c>escalated</c> event and ONE <c>escalation</c> wake to the
    /// decider, in one write. Automatic retries stop.
    /// </summary>
    /// <exception cref="TradeAgentException">Not the holder, not open, an unknown reason or no words; nothing was written.</exception>
    public MandateEventRow Decline(string mandateId, string caller, string? attempt, string reason, string why, DateTimeOffset now) =>
        db.Write(_ =>
        {
            var m = HeldBy(mandateId, caller);
            if (!MandateDecline.IsKnown(reason))
                throw Refused($"'{reason}' is no reason to decline: {string.Join(" | ", MandateDecline.All)}.");
            if (string.IsNullOrWhiteSpace(why) || why.Length > MandateLimits.MaxClaimChars)
                throw Refused($"a decline says why, in at most {MandateLimits.MaxClaimChars} characters.");

            var declined = AppendMandateEvent(m.Id, now, MandateEventKind.Declined, caller, attempt, $"{reason}: {why}", []);
            new MissionEventStore(db).CloseMandateWakes(m.Id, now, MissionEventDisposition.Withdrawn, $"its holder declined it ({reason})");
            Escalate(m, now, $"its holder declined it as {reason}: {why}", [declined.Id.ToString(CultureInfo.InvariantCulture)]);
            return declined;
        });

    /// <summary>
    /// THE HOLDER HANDS A DELIVERY BACK, SEALED: the report, the deviations it names and the refs it cites — its CLAIMS, stored
    /// verbatim — go into an immutable <c>org_delivery</c> whose id is the SHA-256 of its manifest (the mandate, the launch, the
    /// delivery's ordinal, the time and every claim); the app runs each of the mandate's v1 checks and records each result in
    /// <c>org_check</c>; the <c>delivered</c> event and what code makes of it (<see cref="Triage"/>) — all in one write. No check
    /// runs code (RULING Q3). An EXACT mandate refuses a delivery that names a deviation, with nothing written (RULING Q7). A later
    /// delivery is a new delivery, never an edit.
    /// </summary>
    /// <param name="lensOf">The attempt that registered a lens <c>id@version</c>, or null when no such lens is registered; the
    /// lens registry is the tape's file, which this store does not open.</param>
    /// <exception cref="TradeAgentException">Not the holder, not open, a claim out of bounds, or exact with a deviation; nothing was written.</exception>
    public DeliveryResult Deliver(string mandateId, string caller, string? attempt, string report,
        IReadOnlyList<MandateDeviation> deviations, IReadOnlyList<MandateRef> refs, DateTimeOffset now,
        Func<string, int, LensOrigin?>? lensOf = null) => db.Write(_ =>
    {
        var m = HeldBy(mandateId, caller);
        if (report.Length > MandateLimits.MaxReportChars)
            throw Refused($"a report holds at most {MandateLimits.MaxReportChars:N0} characters; longer is refused, never cut.");
        if (deviations.Count > MandateLimits.MaxDeviations || refs.Count > MandateLimits.MaxRefs)
            throw Refused($"a delivery names at most {MandateLimits.MaxDeviations} deviations and {MandateLimits.MaxRefs} refs.");
        foreach (var d in deviations)
            if (new[] { d.What, d.Found, d.Cost }.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > MandateLimits.MaxClaimChars))
                throw Refused($"a deviation names what it changed, what it found and what it cost, each in at most {MandateLimits.MaxClaimChars} characters.");
        foreach (var r in refs)
            if (!MandateRefKind.IsKnown(r.Kind) || string.IsNullOrWhiteSpace(r.Id) || r.Id.Length > 200)
                throw Refused($"a ref names an app record — {string.Join(" | ", MandateRefKind.All)} — and its id; '{r.Kind}' is not one.");
        // RULING Q7 — THE CODE HALF OF "EXACT": a deviation is refused, never sealed and judged later.
        if (m.Exact && deviations.Count > 0)
            throw Refused($"mandate {m.Id} is exact: a delivery that names a deviation is refused. Deliver what was asked, or decline it.");

        long ordinal;
        using (var n = db.Cmd("SELECT COUNT(*) FROM org_delivery WHERE mandate_id=$m", ("$m", m.Id)))
            ordinal = Convert.ToInt64(n.ExecuteScalar(), CultureInfo.InvariantCulture) + 1;
        var manifest = JsonSerializer.Serialize(new
        {
            mandate = m.Id, attempt, ordinal, at = Sql.T(now), report,
            deviations = deviations.Select(d => new { what = d.What, found = d.Found, cost = d.Cost }),
            refs = refs.Select(r => new { kind = r.Kind, id = r.Id })
        });
        var delivery = new DeliveryRow(Sha256Hex(manifest), m.Id, attempt, report, deviations, refs, now);

        using (var c = db.Cmd("""
            INSERT INTO org_delivery(id, mandate_id, attempt_id, report, deviations, refs, manifest, at)
            VALUES($id,$m,$attempt,$report,$deviations,$refs,$manifest,$at)
            """,
            ("$id", delivery.Id), ("$m", m.Id), ("$attempt", attempt), ("$report", report),
            ("$deviations", JsonSerializer.Serialize(deviations)), ("$refs", JsonSerializer.Serialize(refs)),
            ("$manifest", manifest), ("$at", Sql.T(now))))
            c.ExecuteNonQuery();

        var checks = m.Checks.Select(check => Run(check, m, delivery, lensOf)).ToList();
        var bytes = Encoding.UTF8.GetByteCount(Json.Write(new
        {
            report, deviations, refs, checks = checks.Select(c => new { check = c.Check, passed = c.Passed, reason = c.Reason })
        }));
        if (bytes > MandateLimits.MaxDeliveryBytes)
            throw Refused($"this delivery is {bytes:N0} bytes as TradeAgent writes it (a character outside plain ASCII costs up to "
                          + $"six), and a delivery holds at most {MandateLimits.MaxDeliveryBytes:N0} so its decider reads it whole; "
                          + "shorten the report or the claims — longer is refused, never cut.");
        foreach (var check in checks)
        {
            using var c = db.Cmd("INSERT INTO org_check(delivery_id, check_name, passed, reason) VALUES($d,$c,$p,$r)",
                ("$d", delivery.Id), ("$c", check.Check), ("$p", check.Passed ? 1 : 0), ("$r", check.Reason));
            c.ExecuteNonQuery();
        }

        var delivered = AppendMandateEvent(m.Id, now, MandateEventKind.Delivered, caller, attempt, null, [delivery.Id]);
        var triaged = Triage(m, delivery, checks, delivered, now);
        return new DeliveryResult(delivery, checks, MandateStatus(m.Id)!, triaged);
    });

    /// <summary>
    /// ONE V1 CHECK OF A DELIVERY, BY THE APP: what it found, and in words why it passed or failed. No code is run; every record is
    /// read from the app's own ledgers.
    /// </summary>
    CheckRow Run(string check, MandateRow m, DeliveryRow d, Func<string, int, LensOrigin?>? lensOf)
    {
        switch (check)
        {
            case MandateCheck.ReportSize:
            {
                var lines = d.Report.Length == 0 ? 0 : d.Report.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n').Length;
                return new CheckRow(d.Id, check, lines <= MandateLimits.ReportLines,
                    $"the report is {lines} line{(lines == 1 ? "" : "s")}; a report holds at most {MandateLimits.ReportLines}");
            }
            case MandateCheck.RefsExist:
            {
                var missing = d.Refs.Where(r => Origin(r, lensOf) is null).Select(Name).ToList();
                return new CheckRow(d.Id, check, missing.Count == 0, missing.Count == 0
                    ? $"every one of the {d.Refs.Count} records named is in the app's ledgers"
                    : $"not in the app's ledgers: {string.Join(", ", missing)}");
            }
            case MandateCheck.RefsOfThisMandate:
            {
                var foreign = d.Refs.Where(r => Origin(r, lensOf) is not { } o || !o.Attempts.Any(a => MandateOfAttempt(a) == m.Id))
                    .Select(Name).ToList();
                return new CheckRow(d.Id, check, foreign.Count == 0, foreign.Count == 0
                    ? $"every one of the {d.Refs.Count} records named was produced by a launch charged to {m.Id}"
                    : $"not produced by a launch charged to {m.Id}: {string.Join(", ", foreign)}");
            }
            case MandateCheck.OutputShape:
            {
                var shape = m.OutputShape;
                var present = shape == MandateShape.Report ? !string.IsNullOrWhiteSpace(d.Report) : d.Refs.Any(r => r.Kind == shape);
                return new CheckRow(d.Id, check, present, present
                    ? $"the task's output shape, {shape}, is present"
                    : shape == MandateShape.Report ? "the task's output shape is a report, and the report is empty"
                    : $"the task's output shape is a {shape}, and the delivery names none");
            }
            default:
                // REFUSE, NEVER GUESS: a check this build does not know fails, in words naming it.
                return new CheckRow(d.Id, check, false, $"'{check}' is no check this build runs");
        }

        static string Name(MandateRef r) => $"{r.Kind} {r.Id}";
    }

    /// <summary>The launches a ref's record came from — its own, and for a run its version's — or null when it is not in the ledgers.</summary>
    RefOrigin? Origin(MandateRef r, Func<string, int, LensOrigin?>? lensOf)
    {
        switch (r.Kind)
        {
            case MandateRefKind.Version:
            {
                using var c = db.Cmd("SELECT attempt FROM strategy_version WHERE id=$id", ("$id", r.Id));
                using var x = c.ExecuteReader();
                return x.Read() ? new RefOrigin([Sql.S(x.GetValue(0))]) : null;
            }
            case MandateRefKind.Run:
            {
                using var c = db.Cmd("""
                    SELECT r.attempt, v.attempt FROM strategy_run r JOIN strategy_version v ON v.id = r.version_id WHERE r.id=$id
                    """, ("$id", r.Id));
                using var x = c.ExecuteReader();
                return x.Read() ? new RefOrigin([Sql.S(x.GetValue(0)), Sql.S(x.GetValue(1))]) : null;
            }
            case MandateRefKind.Ledger:
            {
                if (!long.TryParse(r.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var entry)) return null;
                using var c = db.Cmd("SELECT attempt FROM ledger_entry WHERE id=$id", ("$id", entry));
                using var x = c.ExecuteReader();
                return x.Read() ? new RefOrigin([Sql.S(x.GetValue(0))]) : null;
            }
            case MandateRefKind.Lens:
            {
                var at = r.Id.LastIndexOf('@');
                if (lensOf is null || at <= 0
                    || !int.TryParse(r.Id[(at + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var version)) return null;
                return lensOf(r.Id[..at], version) is { } lens ? new RefOrigin([lens.Attempt]) : null;
            }
            default:
                return null;
        }
    }

    /// <summary>The mandate a launch was admitted under, or null.</summary>
    string? MandateOfAttempt(string? attempt)
    {
        if (attempt is null) return null;
        using var c = db.Cmd("SELECT mandate_id FROM ai_attempt WHERE id=$id", ("$id", attempt));
        return c.ExecuteScalar() as string;
    }

    /// <summary>
    /// WHAT CODE MAKES OF A SEALED DELIVERY, IN THE DELIVERY'S OWN WRITE (ORGANISATION § 11: "retry caps; numeric acceptance
    /// checks"). A pass, or ANY deviation the holder named, wakes the decider with ONE <c>delivery</c> wake — a deviation is the
    /// decider's to judge even when a check failed. A CLEAN failure — a check failed and nothing was named — is code's: retried
    /// fresh, a <c>retry</c> event and ONE new <c>mandate</c> wake to the holder, the decider NOT woken; until the clean failures
    /// reach the retry limit, or half the allocation is spent with nothing accepted, when code stops and escalates instead
    /// (<see cref="Escalate"/>), in this same write. Once a mandate has been escalated, automatic retries have stopped: a later
    /// clean failure goes to the decider. Answers the id of the wake it raised.
    /// </summary>
    string Triage(MandateRow m, DeliveryRow d, IReadOnlyList<CheckRow> checks, MandateEventRow delivered, DateTimeOffset now)
    {
        var events = new MissionEventStore(db);
        if (checks.All(c => c.Passed) || d.Deviations.Count > 0 || EverEscalated(m.Id))
        {
            var wake = MissionEventIds.Delivery(d.Id);
            events.Raise(wake, MissionEventKind.Delivery, now, payload: m.Id, role: m.Decider);
            return wake;
        }

        var failed = string.Join("; ", checks.Where(c => !c.Passed).Select(c => $"{c.Check}: {c.Reason}"));
        var failures = CleanFailures(m.Id);
        if (failures >= m.RetryLimit)
            return MissionEventIds.Escalation(m.Id, Escalate(m, now,
                $"{failures} clean failure{(failures == 1 ? "" : "s")} reach the retry limit of {m.RetryLimit} — the last: {failed}", [d.Id]).Id);
        if (Spend(db, m.Id).Spent >= m.Allocation / 2m)
            return MissionEventIds.Escalation(m.Id, Escalate(m, now, $"{HalfSpent} — the last delivery failed: {failed}", [d.Id]).Id);

        var retry = AppendMandateEvent(m.Id, now, MandateEventKind.Retry, AppActor, null, failed, [d.Id]);
        RaiseMandateWake(m, retry, now);
        return MissionEventIds.Mandate(m.Id, retry.Id);
    }

    /// <summary>The words an escalation for half the allocation begins with — how the sweep knows it has raised one already.</summary>
    public const string HalfSpent = "half its allocation is spent and nothing is accepted";

    /// <summary>How many of a mandate's deliveries failed a check and named no deviation: the failures code retries.</summary>
    int CleanFailures(string mandateId)
    {
        using var c = db.Cmd("""
            SELECT COUNT(*) FROM org_delivery d
             WHERE d.mandate_id = $m AND d.deviations = '[]'
               AND EXISTS (SELECT 1 FROM org_check c WHERE c.delivery_id = d.id AND c.passed = 0)
            """, ("$m", mandateId));
        return Convert.ToInt32(c.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    bool EverEscalated(string mandateId, string? reasonStartsWith = null)
    {
        using var c = db.Cmd("""
            SELECT EXISTS (SELECT 1 FROM org_mandate_event WHERE mandate_id=$m AND kind=$k AND ($r IS NULL OR substr(reason, 1, length($r)) = $r))
            """, ("$m", mandateId), ("$k", MandateEventKind.Escalated), ("$r", reasonStartsWith));
        return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    // ---- the decider's judgment ----------------------------------------------------------------------------------------

    /// <summary>
    /// THE DECIDER ACCEPTS THE EXACT DELIVERY IT WAS SHOWN: the mandate's latest sealed delivery, by its id — one sealed after it is
    /// refused, so an acceptance never lands on words the decider did not read. The <c>accepted</c> event closes the mandate; the
    /// holder's untaken wakes are closed with it. One write.
    /// </summary>
    /// <exception cref="TradeAgentException">Not the decider, closed, or not the latest delivery; nothing was written.</exception>
    public MandateEventRow Accept(string deliveryId, string caller, string? attempt, DateTimeOffset now) => db.Write(_ =>
    {
        var (m, d) = Judged(deliveryId, caller);
        var accepted = AppendMandateEvent(m.Id, now, MandateEventKind.Accepted, caller, attempt, null, [d.Id]);
        new MissionEventStore(db).CloseMandateWakes(m.Id, now, MissionEventDisposition.Withdrawn, $"its decider accepted delivery {d.Id}");
        return accepted;
    });

    /// <summary>
    /// THE DECIDER REJECTS THE LATEST DELIVERY, CITING EXACTLY ONE OF: a check of THAT delivery that failed, by name; or a defect it
    /// states, in its own words (a claim, stored verbatim). The <c>rejected</c> event and ONE fresh <c>mandate</c> wake to the holder,
    /// in one write.
    /// </summary>
    /// <exception cref="TradeAgentException">Not the decider, closed, not the latest delivery, or no valid citation; nothing was written.</exception>
    public MandateEventRow Reject(string deliveryId, string caller, string? attempt, string? check, string? defect, DateTimeOffset now) =>
        db.Write(_ =>
        {
            var (m, d) = Judged(deliveryId, caller);
            if ((check is null) == (defect is null))
                throw Refused("a rejection cites exactly one thing: a check of that delivery that failed ('failed', by its name), or a defect you state ('defect').");

            string reason;
            IReadOnlyList<string> evidence;
            if (check is not null)
            {
                var row = Checks(d.Id).FirstOrDefault(c => c.Check == check)
                    ?? throw Refused($"'{check}' is no check TradeAgent ran on delivery {d.Id}: {string.Join(", ", Checks(d.Id).Select(c => c.Check))}.");
                if (row.Passed)
                    throw Refused($"'{check}' PASSED on delivery {d.Id} ({row.Reason}): a rejection cites a check that failed, or states the defect.");
                reason = $"{check} failed: {row.Reason}";
                evidence = [d.Id, check];
            }
            else
            {
                if (string.IsNullOrWhiteSpace(defect) || defect.Length > MandateLimits.MaxClaimChars)
                    throw Refused($"a stated defect is your words, at most {MandateLimits.MaxClaimChars} characters.");
                reason = defect;
                evidence = [d.Id];
            }

            var rejected = AppendMandateEvent(m.Id, now, MandateEventKind.Rejected, caller, attempt, reason, evidence);
            RaiseMandateWake(m, rejected, now);
            return rejected;
        });

    /// <summary>The delivery and its open mandate, decided by <paramref name="caller"/>, and the latest sealed — or refused in words.</summary>
    (MandateRow, DeliveryRow) Judged(string deliveryId, string caller)
    {
        var d = Delivery(deliveryId) ?? throw Refused($"'{deliveryId}' is no sealed delivery.");
        var m = DecidedBy(d.MandateId, caller);
        var latest = Deliveries(m.Id)[^1];
        if (latest.Id != d.Id)
            throw Refused($"delivery {latest.Id} was sealed on {m.Id} after {d.Id}: a judgment binds the exact delivery you were shown, "
                          + "and that is no longer the latest. Read it with 'mandate-show' and judge that one.");
        return (m, d);
    }

    // ---- the sweep (once per tick) ---------------------------------------------------------------------------------------

    /// <summary>
    /// THE ORGANISATION'S ONCE-PER-TICK SWEEP (ORCHESTRATION § 4 flow 3), each mandate in its own write, every step idempotent:
    /// an open mandate past its deadline EXPIRES (the event, the holder's untaken wakes closed, ONE <c>escalation</c> wake to the
    /// decider); one with half its allocation spent and nothing accepted is escalated ONCE; and one whose holder's work is owed —
    /// issued, retried or rejected — with no untaken wake and no launch open is given ONE <c>mandate</c> wake again, keyed by its
    /// latest event and the last launch charged to it, so a restart re-raises each once and a second pass raises nothing; and
    /// one whose judgment is owed — a sealed delivery or an escalation — whose latest wake to the decider a LOST launch took is
    /// given ONE such wake again (<see cref="LostJudgment"/>), keyed by that wake and that launch. It buys
    /// no turn and dispatches nothing. Answers how many records it wrote.
    /// </summary>
    public int Sweep(DateTimeOffset now)
    {
        var written = 0;
        foreach (var id in OpenMandates())
            written += db.Write(_ =>
            {
                if (Mandate(id) is not { } m || MandateStatus(id) is not { } status || MandateEventKind.Closes(status)) return 0;

                if (m.Deadline <= now)
                {
                    var expired = AppendMandateEvent(m.Id, now, MandateEventKind.Expired, AppActor, null,
                        $"its deadline {m.Deadline:u} passed with nothing accepted", []);
                    new MissionEventStore(db).CloseMandateWakes(m.Id, now, MissionEventDisposition.Withdrawn, "the mandate expired");
                    new MissionEventStore(db).Raise(MissionEventIds.Escalation(m.Id, expired.Id), MissionEventKind.Escalation, now,
                        payload: m.Id, role: m.Decider);
                    return 1;
                }

                if (status != MandateEventKind.Escalated && !EverEscalated(m.Id, HalfSpent) && Spend(db, m.Id).Spent >= m.Allocation / 2m)
                {
                    Escalate(m, now, HalfSpent, []);
                    return 1;
                }

                if (status is MandateEventKind.Issued or MandateEventKind.Retry or MandateEventKind.Rejected
                    && !HasUntakenWake(m.Id) && !HasOpenLaunch(m.Id))
                {
                    var latest = MandateEvents(m.Id)[^1];
                    return new MissionEventStore(db).Raise(MissionEventIds.Mandate(m.Id, latest.Id, LastLaunch(m.Id) ?? "none"),
                        MissionEventKind.Mandate, now, payload: m.Id, role: m.Holder) ? 1 : 0;
                }

                // THE DECIDER'S JUDGMENT, TOO (fix pass 1, item 2): a sealed delivery or an escalation waiting on the decider whose
                // latest wake a launch took and a restart declared LOST is owed ONE wake of the same kind again, keyed by that
                // wake and that launch — never while a wake is untaken or the launch still runs, and never for one that ENDED:
                // that turn had the judgment in front of it, and waking it again every tick would spend the owner's allowance.
                if (status is MandateEventKind.Delivered or MandateEventKind.Escalated && LostJudgment(m) is { } lost)
                    return new MissionEventStore(db).Raise(MissionEventIds.Rejudge(lost.Id, lost.ConsumedBy!), lost.Kind, now,
                        payload: m.Id, role: m.Decider) ? 1 : 0;
                return 0;
            });
        return written;
    }

    IReadOnlyList<string> OpenMandates() => db.Read(_ =>
    {
        var closing = MandateEventKind.Closing.Select((_, i) => $"$c{i.ToString(CultureInfo.InvariantCulture)}").ToArray();
        using var c = db.Cmd($"""
            SELECT m.id FROM org_mandate m
             WHERE (SELECT kind FROM org_mandate_event e WHERE e.mandate_id = m.id ORDER BY e.id DESC LIMIT 1) NOT IN ({string.Join(",", closing)})
             ORDER BY m.created_at, m.id
            """, [.. closing.Select((n, i) => (n, (object?)MandateEventKind.Closing[i]))]);
        using var r = c.ExecuteReader();
        var ids = new List<string>();
        while (r.Read()) ids.Add(r.GetString(0));
        return (IReadOnlyList<string>)ids;
    });

    /// <summary>
    /// The latest <c>delivery</c> or <c>escalation</c> wake of <paramref name="m"/> to its decider when the launch that took it was
    /// declared LOST, or null — none raised, the latest still untaken, or taken by a launch that runs or ended.
    /// </summary>
    MissionEvent? LostJudgment(MandateRow m)
    {
        using var c = db.Cmd("""
            SELECT e.id, e.kind, e.consumed_by FROM mission_event e
             WHERE e.kind IN ($delivery, $escalation) AND e.payload = $m AND e.role = $decider
             ORDER BY e.rowid DESC LIMIT 1
            """, ("$delivery", MissionEventKind.Delivery), ("$escalation", MissionEventKind.Escalation), ("$m", m.Id),
            ("$decider", m.Decider));
        string id, kind;
        string? by;
        using (var r = c.ExecuteReader())
        {
            if (!r.Read()) return null;
            (id, kind, by) = (r.GetString(0), r.GetString(1), Sql.S(r.GetValue(2)));
        }
        if (by is null) return null;
        using var lost = db.Cmd("SELECT EXISTS (SELECT 1 FROM ai_attempt WHERE id=$by AND state='LOST')", ("$by", by));
        return Convert.ToInt64(lost.ExecuteScalar(), CultureInfo.InvariantCulture) != 0
            ? new MissionEvent { Id = id, Kind = kind, ConsumedBy = by }
            : null;
    }

    bool HasUntakenWake(string mandateId)
    {
        using var c = db.Cmd("SELECT EXISTS (SELECT 1 FROM mission_event WHERE kind=$k AND payload=$m AND consumed_at IS NULL)",
            ("$k", MissionEventKind.Mandate), ("$m", mandateId));
        return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    bool HasOpenLaunch(string mandateId)
    {
        using var c = db.Cmd("SELECT EXISTS (SELECT 1 FROM ai_attempt WHERE mandate_id=$m AND state='LAUNCHED')", ("$m", mandateId));
        return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    string? LastLaunch(string mandateId)
    {
        using var c = db.Cmd("SELECT id FROM ai_attempt WHERE mandate_id=$m ORDER BY started_at DESC, rowid DESC LIMIT 1", ("$m", mandateId));
        return c.ExecuteScalar() as string;
    }

    /// <summary>The mandate, open, and held by <paramref name="caller"/> — or refused in words.</summary>
    MandateRow HeldBy(string mandateId, string caller)
    {
        var m = Mandate(mandateId) ?? throw Refused($"'{mandateId}' is no mandate.");
        if (m.Holder != caller)
            throw Refused($"'{mandateId}' is held by '{m.Holder}', not by you: only its holder delivers or declines it.");
        if (!IsOpen(m.Id))
            throw Refused($"'{mandateId}' is {MandateStatus(m.Id)}: nothing more is owed on a closed mandate.");
        return m;
    }

    /// <summary>A mandate's sealed deliveries, oldest first.</summary>
    public IReadOnlyList<DeliveryRow> Deliveries(string mandateId) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT id, mandate_id, attempt_id, report, deviations, refs, at FROM org_delivery WHERE mandate_id=$m ORDER BY rowid",
            ("$m", mandateId));
        using var r = c.ExecuteReader();
        var rows = new List<DeliveryRow>();
        while (r.Read())
            rows.Add(new DeliveryRow(r.GetString(0), r.GetString(1), Sql.S(r.GetValue(2)), r.GetString(3),
                JsonSerializer.Deserialize<MandateDeviation[]>(r.GetString(4)) ?? [],
                JsonSerializer.Deserialize<MandateRef[]>(r.GetString(5)) ?? [], Sql.Time(r.GetValue(6))));
        return (IReadOnlyList<DeliveryRow>)rows;
    });

    /// <summary>One sealed delivery, or null.</summary>
    public DeliveryRow? Delivery(string deliveryId) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT mandate_id FROM org_delivery WHERE id=$id", ("$id", deliveryId));
        return c.ExecuteScalar() is string m ? Deliveries(m).FirstOrDefault(d => d.Id == deliveryId) : null;
    });

    /// <summary>The app's check results on a delivery, in the order the mandate lists its checks.</summary>
    public IReadOnlyList<CheckRow> Checks(string deliveryId) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT delivery_id, check_name, passed, reason FROM org_check WHERE delivery_id=$d ORDER BY rowid",
            ("$d", deliveryId));
        using var r = c.ExecuteReader();
        var rows = new List<CheckRow>();
        while (r.Read()) rows.Add(new CheckRow(r.GetString(0), r.GetString(1), r.GetInt64(2) != 0, r.GetString(3)));
        return (IReadOnlyList<CheckRow>)rows;
    });

    /// <summary>The manifest a delivery's id is the hash of, as sealed.</summary>
    public string? Manifest(string deliveryId) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT manifest FROM org_delivery WHERE id=$id", ("$id", deliveryId));
        return c.ExecuteScalar() as string;
    });

    // ---- reads ----------------------------------------------------------------------------------------------------------

    /// <summary>One mandate as issued, or null.</summary>
    public MandateRow? Mandate(string id) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {MandateCols} FROM org_mandate WHERE id=$id", ("$id", id));
        using var r = c.ExecuteReader();
        return r.Read() ? ReadMandate(r) : null;
    });

    /// <summary>Every mandate <paramref name="position"/> holds or decides, newest first.</summary>
    public IReadOnlyList<MandateRow> MandatesOf(string position) => db.Read(_ =>
    {
        using var c = db.Cmd($"""
            SELECT {MandateCols} FROM org_mandate WHERE holder=$p OR decider=$p ORDER BY created_at DESC, id DESC
            """, ("$p", position));
        using var r = c.ExecuteReader();
        var rows = new List<MandateRow>();
        while (r.Read()) rows.Add(ReadMandate(r));
        return (IReadOnlyList<MandateRow>)rows;
    });

    /// <summary>A mandate's events, oldest first.</summary>
    public IReadOnlyList<MandateEventRow> MandateEvents(string mandateId) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {MandateEventCols} FROM org_mandate_event WHERE mandate_id=$id ORDER BY id",
            ("$id", mandateId));
        using var r = c.ExecuteReader();
        var rows = new List<MandateEventRow>();
        while (r.Read()) rows.Add(ReadMandateEvent(r));
        return (IReadOnlyList<MandateEventRow>)rows;
    });

    /// <summary>A mandate's status: the kind of its latest event, or null for no such mandate.</summary>
    public string? MandateStatus(string mandateId) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT kind FROM org_mandate_event WHERE mandate_id=$id ORDER BY id DESC LIMIT 1", ("$id", mandateId));
        return c.ExecuteScalar() as string;
    });

    /// <summary>Whether the mandate exists and no closing event has been written for it.</summary>
    public bool IsOpen(string mandateId) => MandateStatus(mandateId) is { } status && !MandateEventKind.Closes(status);

    /// <summary>
    /// THE MEASURED SPEND OF A MANDATE (<see cref="MandateSpend"/>), read from the launch ledger — inside the caller's write when
    /// there is one, so a gate reads the rows it is about to add to.
    /// </summary>
    public MandateSpend SpendOf(string mandateId) => db.Read(_ => Spend(db, mandateId));

    internal static MandateSpend Spend(Database db, string mandateId)
    {
        using var c = db.Cmd("""
            SELECT
              COALESCE(SUM(CASE WHEN state<>'LAUNCHED' AND cost IS NOT NULL THEN CAST(cost AS REAL) ELSE 0 END), 0),
              COALESCE(SUM(CASE WHEN state='LAUNCHED' THEN CAST(reserved_cost AS REAL) ELSE 0 END), 0),
              SUM(CASE WHEN state<>'LAUNCHED' AND cost IS NULL THEN 1 ELSE 0 END),
              COUNT(*)
            FROM ai_attempt WHERE mandate_id = $id
            """, ("$id", mandateId));
        using var r = c.ExecuteReader();
        if (!r.Read()) return new MandateSpend(0m, 0m, 0, 0);
        return new MandateSpend((decimal)r.GetDouble(0), (decimal)r.GetDouble(1),
            r.IsDBNull(2) ? 0 : Convert.ToInt32(r.GetValue(2), CultureInfo.InvariantCulture),
            Convert.ToInt32(r.GetValue(3), CultureInfo.InvariantCulture));
    }

    // ---- the launch gate (read by AiAttemptStore.Begin, inside its transaction) ------------------------------------------

    /// <summary>
    /// THE MANDATE A LAUNCH SERVES: the one its consumed <c>mandate</c> wakes name, or the one the attempt names — or a refusal when
    /// they name more than one, or disagree. Null refusal and null <paramref name="mandateId"/> is a launch that serves none. Read
    /// inside <see cref="AiAttemptStore.Begin"/>'s transaction, from the wake rows themselves, so no host can drop the mandate on
    /// its way to the ledger.
    /// </summary>
    internal static AiAdmission? MandateServed(Database db, AiAttempt a, IReadOnlyList<string>? consuming, out string? mandateId)
    {
        mandateId = a.MandateId;
        if (consuming is not { Count: > 0 }) return null;

        var ids = consuming.Distinct(StringComparer.Ordinal).ToArray();
        var names = ids.Select((_, i) => $"$w{i.ToString(CultureInfo.InvariantCulture)}").ToArray();
        using var c = db.Cmd(
            $"SELECT DISTINCT payload FROM mission_event WHERE kind = $mandate AND id IN ({string.Join(",", names)}) ORDER BY payload",
            [("$mandate", (object?)MissionEventKind.Mandate), .. names.Select((n, i) => (n, (object?)ids[i]))]);
        var named = new List<string>();
        using (var r = c.ExecuteReader())
            while (r.Read()) named.Add(r.IsDBNull(0) ? "" : r.GetString(0));

        if (named.Count > 1)
            return MandateRefusal(named[0],
                $"launch {a.Id} would answer the wakes of {named.Count} mandates ({string.Join(", ", named)}), and a launch serves one: "
                + "its spend is charged to exactly one mandate.");
        if (named.Count == 1)
        {
            if (a.MandateId is { } stated && stated != named[0])
                return MandateRefusal(named[0], $"launch {a.Id} names mandate {stated} and answers a wake of {named[0]}: a launch serves the mandate its wake names.");
            mandateId = named[0];
        }
        return null;
    }

    /// <summary>
    /// WHY A LAUNCH UNDER <paramref name="mandateId"/> MAY NOT BE ADMITTED, or null — the mandate's allocation gate (ORCHESTRATION
    /// § 2.4, BUDGET-001; RULING Q5): no such mandate; one that is closed (accepted, cancelled, expired) or escalated and not sent back; one this role does not
    /// hold; one with a resolved launch of unknown cost (unknown is never zero); or measured spend + unresolved reservations + this
    /// launch's reservation above its lifetime allocation.
    /// </summary>
    internal static AiAdmission? AllocationRefusal(Database db, string mandateId, AiAttempt a)
    {
        MandateRow? m;
        using (var row = db.Cmd($"SELECT {MandateCols} FROM org_mandate WHERE id=$id", ("$id", mandateId)))
        using (var r = row.ExecuteReader())
            m = r.Read() ? ReadMandate(r) : null;
        if (m is null) return MandateRefusal(mandateId, $"'{mandateId}' is no mandate, so no launch is admitted under it.");

        using (var last = db.Cmd("SELECT kind FROM org_mandate_event WHERE mandate_id=$id ORDER BY id DESC LIMIT 1", ("$id", m.Id)))
            switch (last.ExecuteScalar())
            {
                case string status when MandateEventKind.Closes(status):
                    return MandateRefusal(m.Id, $"mandate {m.Id} is {status}: no launch is admitted under a closed mandate, and its spend has stopped.");
                // AUTOMATIC RETRIES STOP AT AN ESCALATION (fix pass 1, item 1): until its decider sends it back (a rejection owes the
                // holder work again), cancels it or accepts a delivery, no launch of the holder's is admitted under it.
                case MandateEventKind.Escalated:
                    return MandateRefusal(m.Id, $"mandate {m.Id} is escalated to its decider {m.Decider}: code has stopped retrying it, and "
                        + "no launch is admitted under it until its decider sends it back, cancels it or accepts a delivery.");
            }
        if (a.Role != m.Holder)
            return MandateRefusal(m.Id, $"mandate {m.Id} is held by {m.Holder}, not {a.Role ?? "the chair"}: only its holder's launches are charged to it.");

        var spend = Spend(db, m.Id);
        if (spend.Unknown > 0)
            return MandateRefusal(m.Id,
                $"mandate {m.Id} has {spend.Unknown} launch{(spend.Unknown == 1 ? "" : "es")} whose cost is not known, and an unknown "
                + "cost is never read as zero: no further launch is admitted under it until its decider decides.");
        if (spend.Spent + spend.Reserved + a.ReservedCost > m.Allocation)
            return MandateRefusal(m.Id,
                $"mandate {m.Id}'s allocation is {Sql.D(m.Allocation)}: {Sql.D(spend.Spent)} spent and {Sql.D(spend.Reserved)} reserved "
                + $"leave no room for this launch's {Sql.D(a.ReservedCost)}. Its decider is told; nothing was started.",
                spend.Spent + spend.Reserved + a.ReservedCost - m.Allocation);
        return null;
    }

    static AiAdmission MandateRefusal(string mandateId, string why, decimal over = 0m) =>
        new() { Refusal = why, Ceiling = AiAdmission.Mandate, MandateId = mandateId, Over = over };

    // ---- the writers' parts ----------------------------------------------------------------------------------------------

    /// <summary>Appends one event to a mandate's history and answers it. Called only inside a writer's own write.</summary>
    MandateEventRow AppendMandateEvent(string mandateId, DateTimeOffset at, string kind, string actor, string? attempt,
        string? reason, IReadOnlyList<string> evidence)
    {
        if (!MandateEventKind.IsKnown(kind)) throw new ArgumentException($"'{kind}' is no mandate event", nameof(kind));
        using var c = db.Cmd("""
            INSERT INTO org_mandate_event(mandate_id, at, kind, actor, attempt_id, reason, evidence)
            VALUES($m,$at,$kind,$actor,$attempt,$reason,$evidence)
            RETURNING id
            """,
            ("$m", mandateId), ("$at", Sql.T(at)), ("$kind", kind), ("$actor", actor), ("$attempt", attempt),
            ("$reason", reason), ("$evidence", JsonSerializer.Serialize(evidence)));
        var id = Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
        return new MandateEventRow(id, mandateId, at, kind, actor, attempt, reason, evidence);
    }

    /// <summary>ONE <c>mandate</c> wake to the holder, keyed by the event that owes it; its payload is the mandate's id.</summary>
    void RaiseMandateWake(MandateRow m, MandateEventRow cause, DateTimeOffset at, string? after = null) =>
        new MissionEventStore(db).Raise(MissionEventIds.Mandate(m.Id, cause.Id, after), MissionEventKind.Mandate, at,
            payload: m.Id, role: m.Holder);

    /// <summary>The mandate, open, and decided by <paramref name="caller"/> — or refused in words.</summary>
    MandateRow DecidedBy(string mandateId, string caller)
    {
        var m = Mandate(mandateId) ?? throw Refused($"'{mandateId}' is no mandate.");
        if (m.Decider != caller)
            throw Refused($"'{mandateId}' is decided by '{m.Decider}', not by you: only its decider may judge, cancel or reassign it.");
        if (!IsOpen(m.Id))
            throw Refused($"'{mandateId}' is {MandateStatus(m.Id)}: a closed mandate is decided already.");
        return m;
    }

    static TradeAgentException Refused(string why) => new(ErrorCode.INVALID_REQUEST, why + " Nothing was written.");

    static MandateRow ReadMandate(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), Sql.S(r.GetValue(6)),
        Sql.S(r.GetValue(7)), JsonSerializer.Deserialize<string[]>(r.GetString(8)) ?? [], r.GetString(9),
        Sql.Time(r.GetValue(10)), Sql.Dec(r.GetValue(11)), r.GetInt32(12), Sql.S(r.GetValue(13)), Sql.Time(r.GetValue(14)));

    static MandateEventRow ReadMandateEvent(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), Sql.Time(r.GetValue(2)), r.GetString(3), r.GetString(4), Sql.S(r.GetValue(5)),
        Sql.S(r.GetValue(6)), JsonSerializer.Deserialize<string[]>(r.GetString(7)) ?? []);

    /// <summary>The canonical JSON a seal hashes: the same input gives the same bytes on every platform.</summary>
    internal static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

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
    public MandateRow Assign(MandateIssue issue, DateTimeOffset now) => db.Write(_ =>
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
    /// CODE STOPS RETRYING AND ASKS THE DECIDER: an <c>escalated</c> event by the app and ONE <c>escalation</c> wake to the decider,
    /// keyed by that event. Called only inside a writer's own write — the escalation is never a second write after the one that
    /// found it.
    /// </summary>
    MandateEventRow Escalate(MandateRow m, DateTimeOffset at, string reason, IReadOnlyList<string> evidence)
    {
        var escalated = AppendMandateEvent(m.Id, at, MandateEventKind.Escalated, AppActor, null, reason, evidence);
        new MissionEventStore(db).Raise(MissionEventIds.Escalation(m.Id, escalated.Id), MissionEventKind.Escalation, at,
            payload: m.Id, role: m.Decider);
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
    /// § 2.4, BUDGET-001; RULING Q5): no such mandate; one that is closed (accepted, cancelled, expired); one this role does not
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
            if (last.ExecuteScalar() is string status && MandateEventKind.Closes(status))
                return MandateRefusal(m.Id, $"mandate {m.Id} is {status}: no launch is admitted under a closed mandate, and its spend has stopped.");
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

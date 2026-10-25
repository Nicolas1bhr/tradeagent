using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TradeAgent.Core.Db;

/// <summary>
/// THE FIVE IDS THE APP SEEDS, AND THE ONLY FIXED ONES (<c>docs/ORGANISATION.md</c> § 3).
///
/// <para>The schema-28 rung writes the root and the two divisions under these ids, and the two positions under
/// the legacy role strings themselves (<see cref="CouncilRoles.Operations"/>, <see cref="CouncilRoles.Research"/>)
/// — so every <c>role</c> a store wrote before the rung names a position after it, and no historical row changes
/// meaning. Every id after these is minted at random by the app; an id an agent asks for grants nothing.</para>
/// </summary>
public static class OrgIds
{
    /// <summary>
    /// The organisation itself. Its head is NULL, and NULL here means THE OWNER, in-process: no row names them,
    /// so no position can ever become them.
    /// </summary>
    public const string Root = "org";

    /// <summary>The division the legacy <c>operations</c> position heads — the only order permission, as today.</summary>
    public const string Operations = "div-operations";

    /// <summary>The division the legacy <c>research</c> position heads.</summary>
    public const string Research = "div-research";

    /// <summary>
    /// THE PREFIX EVERY POSITION ID THE APP MINTS CARRIES (<c>U-org-rights</c>). No legacy id (<c>operations</c>,
    /// <c>research</c>), no app principal (<see cref="AppPrincipals"/>) and no device name Windows reserves begins with
    /// it, so a minted id can never equal one of them; a test holds that over every one.
    /// </summary>
    public const string PositionPrefix = "pos-";

    /// <summary>
    /// A NEW POSITION ID: <see cref="PositionPrefix"/> and 48 bits from the operating system's cryptographic generator,
    /// lower-case hex — app-random, never a name an agent chose and never derived from anything a model wrote. Minting is
    /// not writing: <c>U-org-verbs</c>' writers call this, then <see cref="PositionIdRefusal"/>, inside the write that
    /// inserts the row, whose primary key refuses the astronomically unlikely repeat.
    /// </summary>
    public static string MintPosition() =>
        PositionPrefix + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    /// <summary>
    /// THE ONE CHECK A WRITER OF A NEW POSITION CALLS (<c>U-org-verbs</c>): null when <paramref name="id"/> may name a
    /// new position, otherwise the sentence that says why not. It refuses an id that is not a position id at all
    /// (<see cref="CouncilRoles.IsPositionId"/>: <c>[a-z0-9-]+</c>), a LEGACY id — the two positions the schema-28 rung
    /// seeded are never minted again — an APP PRINCIPAL, which is code and never a position, and a DEVICE NAME Windows
    /// reserves, which no home folder can carry. Each is named on its own, before the shape, so the refusal says what the
    /// id is rather than only that it failed.
    /// </summary>
    public static string? PositionIdRefusal(string? id)
    {
        if (CouncilRoles.IsKnown(id))
            return $"'{id}' is a legacy position's id (operations, research): they were seeded once, under those ids, and "
                   + "no new position takes one.";
        // A SEAT OF THE SECURITY BOARD (U-board-a1; R24 § 4.1): outside the chart, minted by the app and by no verb.
        if (Board.BoardSeats.IsBoardId(id)) return Board.BoardSeats.NotAPosition(id!);
        if (OrgStore.IsAppPrincipal(id))
            return $"'{id}' is one of the app's own principals ({string.Join(", ", AppPrincipals.All)}): code writing "
                   + "under a name, never a position.";
        if (CouncilRoles.IsReservedDeviceName(id))
            return $"'{id}' is a device name Windows reserves, so no folder can carry it and no position takes it.";
        if (!CouncilRoles.IsPositionId(id))
            return $"'{id}' is not a position id: a position id is lower-case letters, digits and hyphens.";
        return null;
    }
}

/// <summary>
/// WHAT A UNIT IS. <b>Strings rather than an enum</b>, for the reason <see cref="MissionEventKind"/> gives: the
/// column is read by a person as often as by this code, and a kind from a newer build must read as itself. The
/// store validates against <see cref="All"/> when it writes; SQL holds no list of them.
/// </summary>
public static class OrgUnitKind
{
    /// <summary>The organisation. Exactly one, and it is the only unit with no parent — that much SQL enforces.</summary>
    public const string Root = "root";

    public const string Division = "division";

    /// <summary>An earned division inside a division (<c>docs/ORGANISATION.md</c> § 3).</summary>
    public const string Subdivision = "subdivision";

    /// <summary>One strategy family: one mechanism over a declared universe.</summary>
    public const string Team = "team";

    /// <summary>A head's staff, such as the chief's Risk &amp; Portfolio analyst.</summary>
    public const string Staff = "staff";

    public static readonly string[] All = [Root, Division, Subdivision, Team, Staff];

    public static bool IsKnown(string? kind) => kind is not null && Array.IndexOf(All, kind) >= 0;
}

/// <summary>
/// A UNIT'S LIFE: chartered → active ⇄ dormant → closed. Closing never deletes a row. Strings, validated by the
/// store, for the reason <see cref="OrgUnitKind"/> gives.
/// </summary>
public static class OrgUnitStatus
{
    public const string Chartered = "chartered";
    public const string Active = "active";
    public const string Dormant = "dormant";
    public const string Closed = "closed";

    public static readonly string[] All = [Chartered, Active, Dormant, Closed];

    public static bool IsKnown(string? status) => status is not null && Array.IndexOf(All, status) >= 0;
}

/// <summary>A POSITION'S LIFE. An ended position keeps its row, its history and its home.</summary>
public static class OrgPositionStatus
{
    public const string Active = "active";
    public const string Dormant = "dormant";
    public const string Ended = "ended";

    public static readonly string[] All = [Active, Dormant, Ended];

    public static bool IsKnown(string? status) => status is not null && Array.IndexOf(All, status) >= 0;
}

/// <summary>
/// WHAT AN <c>org_event</c> RECORDS. One kind so far: the rung's own seed. The acts that restructure the chart —
/// charter, merge, close, hire, retire, a child envelope — arrive with the verbs that perform them.
/// </summary>
public static class OrgEventKind
{
    /// <summary>A row the schema-28 rung wrote: nobody decided it, the upgrade did.</summary>
    public const string Seeded = "seeded";

    /// <summary>The owner seated a position: its runtime, model and allowance (<c>U-org-seats</c>, <see cref="OrgStore.SetSeat"/>).</summary>
    public const string Seated = "seated";

    /// <summary>
    /// A POSITION-WIDE FENCE (<c>U-org-verbs-b</c>; ORCHESTRATION § 2.3): every attempt of the position whose row committed before it
    /// does nothing more (<see cref="OrgStore.FencedSince"/>) — commit order, carried by the app's mark in its detail
    /// (<c>U-org-verbs-d</c>). Written, with its cause in app-composed detail, by the transitions that restructure the chart (retire,
    /// close, merge, veto — O6 part d), in their own write.
    /// </summary>
    public const string Fenced = "fenced";

    /// <summary>A unit chartered by its parent's head (<c>U-org-verbs-c</c>, <see cref="OrgStore.Charter"/>); decided by that position.</summary>
    public const string Chartered = "chartered";

    /// <summary>A position hired, DORMANT, by the head of its unit's parent (<c>U-org-verbs-c</c>, <see cref="OrgStore.Hire"/>).</summary>
    public const string Hired = "hired";

    /// <summary>One currency of a unit's share re-split by the head of its parent (<c>U-org-verbs-e</c>, <see cref="OrgStore.Tilt"/>).</summary>
    public const string Tilted = "tilted";

    /// <summary>
    /// A position retired, a unit closed, a unit merged into a sibling (<c>U-org-verbs-d</c>: <see cref="OrgStore.Retire"/>,
    /// <see cref="OrgStore.Close"/>, <see cref="OrgStore.Merge"/>) — each decided by the head of the target's parent.
    /// </summary>
    public const string Retired = "retired", Closed = "closed", Merged = "merged";

    /// <summary>
    /// The owner's narrowing veto of one structural event, and his activation of a dormant position (<c>U-org-verbs-d</c>:
    /// <see cref="OrgStore.Veto"/>, <see cref="OrgStore.Activate"/>) — in-process only, decided by <see cref="OrgDecider.Owner"/>.
    /// </summary>
    public const string Vetoed = "vetoed", Activated = "activated";

    /// <summary>
    /// THE LAUNCHES A MANDATE'S CANCELLATION OR RE-SCOPE TOOK AUTHORITY FROM (<c>U-org-verbs-d</c> fix pass; the Codex review, P2): the
    /// ones under it still open when that transition committed, read in its own write and named in app-composed detail — the record
    /// <see cref="OrgStore.FencedBy"/> reads, so which launch was cut mid-turn is commit order and never two stamps. Decided by the app;
    /// it fences nothing by itself (the mandate's own event is the fence).
    /// </summary>
    public const string MandateFenced = "mandate-fenced";

    /// <summary>
    /// THE STRUCTURAL CHANGES THE WEEKLY THROTTLE COUNTS (ORGANISATION § 3: "at most two structural changes a week"; survey Q4): a
    /// charter, a close, a merge — decided by a position, never the app's or the owner's. A hire or a retirement is bounded by span and
    /// positions instead.
    /// </summary>
    public static readonly string[] Structural = [Chartered, Closed, Merged];
}

/// <summary>
/// WHO DECIDED AN ACT ON THE CHART. Today only the app: the rung's seed is the one writer there is.
/// </summary>
public static class OrgDecider
{
    public const string App = "app";

    /// <summary>The owner, in-process — never a pipe op, a door verb or a <c>trade</c> verb (<see cref="OrgStore.SetSeat"/>).</summary>
    public const string Owner = "owner";
}

/// <summary>
/// NAMES THE APP WRITES UNDER THAT ARE CODE AND NEVER POSITIONS (<c>docs/COUNCIL.md</c>:55-57: "code and never a
/// role").
///
/// <para>Two of them are in <c>role</c> columns today — the referee's holdout runs and verdict notes, the paper
/// allocator's notes — and neither is a place an agent fills: no workspace, no budget, no turn. Naming them here
/// gives the one question a reader of those columns asks, "is this a position?", an answer other than "unknown",
/// and keeps the names out of reach of any position minted later.</para>
/// </summary>
public static class AppPrincipals
{
    /// <summary><see cref="Strategy.Referee.RunRole"/>: the referee's holdout runs and its verdict notes.</summary>
    public const string Referee = Strategy.Referee.RunRole;

    /// <summary>
    /// The app's own paper allocation policy — <c>TradingGateway.PaperAllocatorRole</c>, spelled again here
    /// because the gateway's assembly sits above this one. A test holds the two spellings equal.
    /// </summary>
    public const string Allocator = "allocator";

    /// <summary>
    /// The decision port (<c>U-decision-port</c>): every call to a decision model is a launch-ledger row under this name,
    /// admitted against the owner's daily cap and perception's own budget. A meter role, never a position and never a
    /// council role — the ledger's readers count it apart and the relay's fence attributes nothing to it.
    /// </summary>
    public const string Perception = "perception";

    /// <summary>
    /// THE PUBLIC AUDIT LINE (<c>U-org-audit-a</c>; ORCHESTRATION § 2.13, ORGANISATION §§ 1–3): app-owned, outside the chief's
    /// subtree — and the chief heads the root, so outside the whole chart: never a unit, never a position, no <c>org_*</c> row. It
    /// samples accepted deliveries and re-checks them in code from approved evidence, in its own file (<c>TradeAgent.Core.Audit</c>);
    /// it can neither instruct nor block. Being one of these names is what refuses it as a minted position, a mandate holder, a
    /// decider, a note's sender or recipient and a caller of the organisation's verbs.
    /// </summary>
    public const string Audit = "audit";

    public static readonly string[] All = [Referee, Allocator, Perception, Audit];
}

/// <summary>
/// ONE UNIT OF A LAUNCH'S FUNDING CHAIN, AND THE ENVELOPE IT ADMITS AGAINST (<c>U-org-envelopes</c>). <see cref="Envelope"/> null
/// is a unit with no separate cap: its spend counts in the units above it, whose envelopes bind.
/// </summary>
public sealed record FundedUnit(string UnitId, decimal? Envelope);

/// <summary>
/// ONE UNIT OF A POSITION'S FUNDING CHAIN IN ONE CEILING, AS <c>org-show</c> SHOWS IT (<c>U-org-verbs-a</c>): the ceiling it is counted
/// in (<see cref="BillingClasses.Key"/>, the money cap, or <see cref="BillingClasses.Plan"/>, the plan's capacity — never money;
/// <c>U-billing-classes</c>), its envelope in that ceiling as admission resolves it (<see cref="FundedUnit.Envelope"/>; null is no
/// separate cap) and the day's spent and reserved charged to it in that ceiling's rows (<see cref="AiAttemptStore.UnitTotalsBetween"/>)
/// — O4's readers, read by the host that meters launches, never recomputed. <paramref name="Unpriced"/> counts that ceiling's resolved
/// turns today whose cost is UNKNOWN (<see cref="AiAttemptTotals.Unpriced"/>): <paramref name="Spent"/> adds them as nothing, so without
/// the count an unpriceable seat's day would read as free.
/// <para><b>MEASUREMENT AND CHARGE APART</b> (<c>U-verbs-a-followups</c>; BUDGET-001): <paramref name="Spent"/> is the PRICED part of the
/// day only — what a measured turn cost. A turn that ended without reporting what it used, or that a restart declared LOST, is charged
/// its reservation (<see cref="AiAttemptTotals.Unreported"/>): those turns are <paramref name="Unmeasured"/>, and their charge
/// <paramref name="UnmeasuredCharge"/>, never inside <paramref name="Spent"/>. The day admission compares is
/// <c>Spent + UnmeasuredCharge</c>; a reader that shows one without the other shows a guess as a bill, or a charge as nothing.</para>
/// </summary>
public sealed record UnitDay(
    string UnitId, string Ceiling, decimal? Envelope, decimal Spent, decimal Reserved, int Unpriced, int Unmeasured, decimal UnmeasuredCharge);

/// <summary>
/// HOW A UNIT'S ENVELOPE IS RESOLVED (<c>docs/ORCHESTRATION.md</c> § 2.4): <c>envelope_share</c> is a FRACTION of the parent's
/// envelope in the same currency and period, never an amount; the root's envelope is the owner's daily cap.
///
/// <para><b>A NULL share</b> on a legacy division (<see cref="OrgIds.Operations"/>, <see cref="OrgIds.Research"/>) is that
/// division's position's share of the day — the split on the Safety page, today's behaviour, so the legacy pair admits
/// exactly as it did; NULL anywhere else is no separate cap, and the unit spends inside the room of the units above it.
/// A child of a unit with no cap of its own takes its fraction of the nearest envelope above it.</para>
/// </summary>
public static class OrgEnvelopes
{
    /// <summary>The legacy position whose share of the day a legacy division's NULL envelope reads, or null for any other unit.</summary>
    public static string? LegacyRoleOf(string unitId) => unitId switch
    {
        OrgIds.Operations => CouncilRoles.Operations,
        OrgIds.Research => CouncilRoles.Research,
        _ => null
    };

    /// <summary>
    /// THE ENVELOPES OF A LINE OF UNITS, nearest first and ending at the root, as <see cref="OrgStore.FundingLine"/> reads it:
    /// resolved from the root down. A share that is not a fraction between 0 and 1 is damage, refused rather than guessed.
    /// </summary>
    public static IReadOnlyList<FundedUnit> Resolve(IReadOnlyList<OrgUnitRow> line, decimal cap, Func<string, decimal> legacyShare)
    {
        var resolved = new FundedUnit[line.Count];
        var above = cap;
        for (var i = line.Count - 1; i >= 0; i--)
        {
            var unit = line[i];
            decimal? envelope = unit.IsRoot ? cap
                : unit.EnvelopeShare is { } text ? Fraction(unit.Id, text) * above
                : LegacyRoleOf(unit.Id) is { } role ? legacyShare(role) * above
                : null;
            resolved[i] = new FundedUnit(unit.Id, envelope);
            if (envelope is { } e) above = e;
        }
        return resolved;
    }

    /// <summary>
    /// A UNIT'S NAME IN THE OWNER'S WORDS, for a refusal. Units carry no title column — titles are claims, published by charter —
    /// so the seeded three are named here and any other unit by its id.
    /// </summary>
    public static string NameOf(string unitId) => unitId switch
    {
        OrgIds.Root => "the organisation",
        OrgIds.Operations => "the Operations division",
        OrgIds.Research => "the Research division",
        _ => $"unit {unitId}"
    };

    internal static decimal Fraction(string unitId, string text) =>
        decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var share) && share is >= 0m and <= 1m
            ? share
            : throw new TradeAgentException(ErrorCode.STATE_DATABASE_CORRUPT,
                $"unit {unitId}'s envelope share '{text}' is not a fraction between 0 and 1");
}

/// <summary>
/// THE LEVEL OF A POSITION IN TIME (<c>docs/VISION.md</c> § 7.5, "Hierarchy as time"): what horizon it holds state over, and so
/// how long it may go unwoken — each level's maximum wake interval is policy data (<c>U-org-wakes</c>). DERIVED from the chart,
/// never stored: a head of a division is a department head, a head deeper down a team head, any other position an executor.
/// </summary>
public static class OrgLevel
{
    /// <summary>Holds state over weeks: which families, which sources. Heads a unit directly under the root.</summary>
    public const string Department = "department";

    /// <summary>Holds state over days: which hypotheses, which assignments. Heads a unit below a division.</summary>
    public const string Team = "team";

    /// <summary>Holds state over hours: the path inside its mandate. Heads nothing.</summary>
    public const string Executor = "executor";

    public static readonly string[] All = [Department, Team, Executor];

    /// <summary>The level a position of <paramref name="kind"/> in a unit at <paramref name="depth"/> holds.</summary>
    public static string Of(string kind, int depth) =>
        kind != OrgPositionKind.Head ? Executor : depth <= 1 ? Department : Team;
}

/// <summary>
/// ONE ACTIVE POSITION AS THE SCHEDULER READS IT (<c>U-org-wakes</c>, ORCHESTRATION § 2.5): its exact id, its kind derived from
/// the chart, the depth of its unit (the root is 0, a division 1) and its level (<see cref="OrgLevel"/>). Nothing here is
/// stored; it is <see cref="OrgStore.Roster"/>'s reading of one snapshot.
/// </summary>
public sealed record ChartPosition(string Id, string Kind, int Depth, string Level)
{
    /// <summary>Whether this is one of the two positions that ran before the chart.</summary>
    public bool IsLegacy => CouncilRoles.IsKnown(Id);

    /// <summary>The legacy pair as the chart seeds them — each heads its division, directly under the root.</summary>
    public static IReadOnlyList<ChartPosition> LegacyPair { get; } =
        [.. CouncilRoles.All.Select(r => new ChartPosition(r, OrgPositionKind.Head, 1, OrgLevel.Department))];
}

/// <summary>
/// One unit of the chart, as <c>org_unit</c> holds it. <see cref="CharterDecisionId"/> is the <c>charter</c> decision the verb that
/// chartered it wrote (rung 48), null on a unit the rung seeded. <see cref="TrialShare"/> and <see cref="VerdictShare"/> are its
/// fractions of a campaign's own trial and verdict budgets (<c>U-org-verbs-e</c>, rung 50) — null is no own cap.
/// </summary>
public sealed record OrgUnitRow(
    string Id,
    string? ParentId,
    string Kind,
    string? HeadPositionId,
    string Status,
    string? EnvelopeShare,
    string? CharterDecisionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ClosedAt,
    string? TrialShare = null,
    string? VerdictShare = null)
{
    /// <summary>The root: the one unit with no parent. Its head is the owner, whom no row names.</summary>
    public bool IsRoot => ParentId is null;
}

/// <summary>One position, as <c>org_position</c> holds it. NULL seat columns are today's behaviour.</summary>
public sealed record OrgPositionRow(
    string Id,
    string UnitId,
    string HomeDir,
    string Status,
    string? SeatRuntime,
    string? SeatModel,
    string? SeatAllowIn,
    string? SeatAllowOut,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EndedAt);

/// <summary>
/// THE ORGANISATION LEDGER: its readers, and the verbs that restructure it.
///
/// <para>The schema-28 rung seeded the chart: the root, two divisions and the two legacy positions. The verbs that
/// restructure it are this class's writers — <see cref="Charter"/> and <see cref="Hire"/> (<c>U-org-verbs-c</c>); merge,
/// close, retire and a child envelope's tilt arrive with parts d and e — each validated against <see cref="OrgUnitKind"/>,
/// <see cref="OrgUnitStatus"/>, <see cref="OrgPositionStatus"/> and the rights matrix (<see cref="OrgRights"/>), minting
/// random ids, and recording each act as an <c>org_event</c> whose detail is composed by the app. An agent ASKS through
/// <c>org-charter</c> or <c>org-hire</c>, and the app decides; no op writes a row the app did not check.</para>
///
/// <para><b>Rows are measurement; titles and charters are claims.</b> A unit's charter is its <c>charter</c> decision record
/// referenced by id (<c>charter_decision_id</c>, rung 48) — the rationale a ledger entry marked claim — never text copied into a row. Nothing here confers authority — order permission stays
/// <see cref="CouncilRoles.MayPlaceOrders"/>, bound to <c>operations</c>, and never follows headship.</para>
///
/// <para><b>A value from a newer build reads as itself.</b> The readers return the columns as stored and judge no
/// vocabulary; only the store's future writers do. What they DO refuse to answer is a chart that is not a tree:
/// a line of parents that loops or names a unit that is not there is damage, and a partial answer would make
/// "this unit is under the root" look provable when it is not.</para>
/// </summary>
public sealed partial class OrgStore(Database db)
{
    const string UnitCols =
        "id, parent_id, kind, head_position_id, status, envelope_share, charter_decision_id, created_at, closed_at, trial_share, verdict_share";

    const string PositionCols =
        "id, unit_id, home_dir, status, seat_runtime, seat_model, seat_allow_in, seat_allow_out, created_at, ended_at";

    /// <summary>Every unit, closed ones included: the root first, then in the order they were created.</summary>
    public IReadOnlyList<OrgUnitRow> Units() => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {UnitCols} FROM org_unit ORDER BY parent_id IS NOT NULL, created_at, id");
        using var r = c.ExecuteReader();
        var rows = new List<OrgUnitRow>();
        while (r.Read()) rows.Add(ReadUnit(r));
        return (IReadOnlyList<OrgUnitRow>)rows;
    });

    /// <summary>
    /// Every position, ended ones included, in the order they were created — never a board seat's id, whatever a row says
    /// (<c>U-board-a1</c>): the board is outside the chart, so a row under its id is never read as a position.
    /// </summary>
    public IReadOnlyList<OrgPositionRow> Positions() => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {PositionCols} FROM org_position ORDER BY created_at, id");
        using var r = c.ExecuteReader();
        var rows = new List<OrgPositionRow>();
        while (r.Read())
        {
            var row = ReadPosition(r);
            if (!Board.BoardSeats.IsBoardId(row.Id)) rows.Add(row);
        }
        return (IReadOnlyList<OrgPositionRow>)rows;
    });

    /// <summary>One position, or null because no position has that id — an app principal and a board seat included.</summary>
    public OrgPositionRow? Position(string id) => Board.BoardSeats.IsBoardId(id) ? null : db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {PositionCols} FROM org_position WHERE id=$id", ("$id", id));
        using var r = c.ExecuteReader();
        return r.Read() ? ReadPosition(r) : null;
    });

    /// <summary>
    /// ONE POSITION'S STANDING, or null because the chart holds no position by exactly that id — an app principal, a name
    /// that is not a position id, and an unknown id included, none of them folded into another. Its kind is DERIVED:
    /// <see cref="OrgPositionKind.Head"/> where a unit's head is this position, <see cref="OrgPositionKind.Staff"/> in a
    /// <see cref="OrgUnitKind.Staff"/> unit, else <see cref="OrgPositionKind.Member"/>; its home is computed from the id.
    /// Read in one snapshot. The seam <see cref="PositionActivation.Refusal"/> reads it (<c>U-org-principals</c>).
    /// </summary>
    public PositionStanding? Standing(string id)
    {
        if (IsOffChart(id) || !CouncilRoles.IsPositionId(id)) return null;
        return db.Read(_ =>
        {
            using var c = db.Cmd("""
                SELECT p.id, p.unit_id, p.status, u.kind,
                       EXISTS (SELECT 1 FROM org_unit h WHERE h.head_position_id = p.id)
                  FROM org_position p JOIN org_unit u ON u.id = p.unit_id
                 WHERE p.id = $id
                """, ("$id", id));
            using var r = c.ExecuteReader();
            if (!r.Read()) return null;
            var kind = r.GetInt64(4) != 0 ? OrgPositionKind.Head
                : r.GetString(3) == OrgUnitKind.Staff ? OrgPositionKind.Staff
                : OrgPositionKind.Member;
            return new PositionStanding(r.GetString(0), r.GetString(1), r.GetString(2), kind,
                CouncilRoles.IsKnown(id), CouncilRoles.HomeDir(id));
        });
    }

    /// <summary>
    /// EVERY ACTIVE POSITION, READ IN ONE SNAPSHOT (<c>U-org-wakes</c>): the legacy pair first, then every other active position
    /// by its exact id, ordinal — each with its kind derived as <see cref="Standing"/> derives it, its unit's depth and its level.
    /// A position whose id is not a position id, or whose unit is missing or hangs from a line that loops, is damage and throws:
    /// the scheduler reads a chart whole or not at all.
    /// </summary>
    public IReadOnlyList<ChartPosition> Roster() => db.Read(_ =>
    {
        var units = Units().ToDictionary(u => u.Id, StringComparer.Ordinal);
        var heads = units.Values.Where(u => u.HeadPositionId is not null).Select(u => u.HeadPositionId!).ToHashSet(StringComparer.Ordinal);
        var roster = new List<ChartPosition>();
        foreach (var p in Positions())
        {
            if (p.Status != OrgPositionStatus.Active || IsAppPrincipal(p.Id) || !CouncilRoles.IsPositionId(p.Id)) continue;
            if (!units.TryGetValue(p.UnitId, out var unit))
                throw Damaged($"position {p.Id} belongs to {p.UnitId}, which is not a unit");
            var kind = heads.Contains(p.Id) ? OrgPositionKind.Head
                : unit.Kind == OrgUnitKind.Staff ? OrgPositionKind.Staff
                : OrgPositionKind.Member;
            var depth = Line(units, unit).Count;
            roster.Add(new ChartPosition(p.Id, kind, depth, OrgLevel.Of(kind, depth)));
        }
        return (IReadOnlyList<ChartPosition>)[.. roster.OrderBy(p => p.IsLegacy ? Array.IndexOf(CouncilRoles.All, p.Id) : CouncilRoles.All.Length)
            .ThenBy(p => p.Id, StringComparer.Ordinal)];
    });

    /// <summary>
    /// THE ACTIVE POSITIONS BEYOND THE LEGACY PAIR, in the order they were created — ONE read, the activation gate's no-unconfined-
    /// neighbour question (<see cref="PositionActivation"/>; ORCHESTRATION § 2.3). <see cref="Hire"/> writes a position DORMANT, so a
    /// hire never adds to it: on a home the app made this is empty until a position is activated, which no organisation verb does.
    /// </summary>
    public IReadOnlyList<string> ActivePositionsBeyondLegacy() => db.Read(_ =>
    {
        using var c = db.Cmd("""
            SELECT id FROM org_position
             WHERE status = $active AND id NOT IN ($ops, $research)
             ORDER BY created_at, id
            """, ("$active", OrgPositionStatus.Active), ("$ops", CouncilRoles.Operations), ("$research", CouncilRoles.Research));
        using var r = c.ExecuteReader();
        var ids = new List<string>();
        while (r.Read())
            if (!Board.BoardSeats.IsBoardId(r.GetString(0))) ids.Add(r.GetString(0));
        return (IReadOnlyList<string>)ids;
    });

    /// <summary>The unit a position belongs to — which it heads when that unit's head is this position — or null.</summary>
    public OrgUnitRow? UnitOf(string positionId) => db.Read(_ =>
    {
        using var c = db.Cmd("""
            SELECT u.id, u.parent_id, u.kind, u.head_position_id, u.status, u.envelope_share,
                   u.charter_decision_id, u.created_at, u.closed_at, u.trial_share, u.verdict_share
              FROM org_position p JOIN org_unit u ON u.id = p.unit_id
             WHERE p.id = $id
            """, ("$id", positionId));
        using var r = c.ExecuteReader();
        return r.Read() ? ReadUnit(r) : null;
    });

    /// <summary>
    /// THE LINE OF PARENTS above a unit, nearest first and ending at the root; empty for the root and for an id
    /// that is no unit. A line that loops or names a missing parent is refused as damage
    /// (<see cref="ErrorCode.STATE_DATABASE_CORRUPT"/>), never answered in part.
    /// </summary>
    public IReadOnlyList<OrgUnitRow> Ancestors(string unitId)
    {
        var units = Units().ToDictionary(u => u.Id, StringComparer.Ordinal);
        return units.TryGetValue(unitId, out var unit) ? Line(units, unit) : [];
    }

    /// <summary><see cref="Ancestors"/> over one snapshot of the units: the parents above <paramref name="start"/>, nearest first.</summary>
    static List<OrgUnitRow> Line(Dictionary<string, OrgUnitRow> units, OrgUnitRow start)
    {
        var unit = start;
        var line = new List<OrgUnitRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { unit.Id };
        while (unit.ParentId is { } parentId)
        {
            if (!units.TryGetValue(parentId, out var parent))
                throw Damaged($"unit {unit.Id} hangs from {parentId}, which is not a unit");
            if (!seen.Add(parent.Id))
                throw Damaged($"the line of parents above {start.Id} loops at {parent.Id}");
            line.Add(parent);
            unit = parent;
        }
        return line;
    }

    /// <summary>
    /// THE FUNDING CHAIN OF A POSITION (<c>U-org-envelopes</c>, <c>docs/ORCHESTRATION.md</c> § 2.4): its own unit first, then
    /// every unit above it, ending at the root — read in one snapshot. A launch is charged to every one of them.
    ///
    /// <para><b>Refused, never answered in part.</b> A name that is no position, a position whose unit is not there, and a
    /// line of parents that loops or names a missing unit each throw, because a chain read short would admit a launch against
    /// fewer envelopes than fund it — and the admission that reads this refuses rather than falling back to the day's limit
    /// alone.</para>
    /// </summary>
    public IReadOnlyList<OrgUnitRow> FundingLine(string positionId) => db.Read(_ =>
    {
        var standing = Standing(positionId)
            ?? throw new TradeAgentException(ErrorCode.STATE_DATABASE_CORRUPT, $"'{positionId}' is no position on the chart");
        var units = Units().ToDictionary(u => u.Id, StringComparer.Ordinal);
        if (!units.TryGetValue(standing.UnitId, out var unit))
            throw Damaged($"position {positionId} belongs to {standing.UnitId}, which is not a unit");

        IReadOnlyList<OrgUnitRow> line = [unit, .. Line(units, unit)];
        if (line[^1].Kind != OrgUnitKind.Root)
            throw Damaged($"the line of parents above {unit.Id} ends at {line[^1].Id}, which is not the root");
        return line;
    });

    /// <summary>
    /// THE FUNDING CHAIN OF A POSITION WITH EACH UNIT'S ENVELOPE (<see cref="OrgEnvelopes.Resolve"/>) over the owner's daily
    /// <paramref name="cap"/> and the legacy split <paramref name="legacyShare"/>. Throws where <see cref="FundingLine"/> does,
    /// and on a share that is not a fraction.
    /// </summary>
    public IReadOnlyList<FundedUnit> FundingChain(string positionId, decimal cap, Func<string, decimal> legacyShare) =>
        OrgEnvelopes.Resolve(FundingLine(positionId), cap, legacyShare);

    /// <summary>
    /// A UNIT AND EVERY UNIT BELOW IT, breadth first: the unit itself, then its children, then theirs. Empty for
    /// an id that is no unit; a loop below it is refused as damage, as in <see cref="Ancestors"/>.
    /// </summary>
    public IReadOnlyList<OrgUnitRow> Subtree(string unitId)
    {
        var units = Units();
        var top = units.FirstOrDefault(u => u.Id == unitId);
        if (top is null) return [];

        var children = units.Where(u => u.ParentId is not null).ToLookup(u => u.ParentId!, StringComparer.Ordinal);
        var tree = new List<OrgUnitRow> { top };
        var seen = new HashSet<string>(StringComparer.Ordinal) { top.Id };
        for (var i = 0; i < tree.Count; i++)
            foreach (var child in children[tree[i].Id])
            {
                if (!seen.Add(child.Id))
                    throw Damaged($"the units below {unitId} loop at {child.Id}");
                tree.Add(child);
            }
        return tree;
    }

    /// <summary>
    /// THE REFUSAL OF A SPLIT THAT CANNOT FUND ITS OWN SEAT, or null: <paramref name="reservation"/> — what one turn of
    /// <paramref name="seat"/> reserves — is larger than <paramref name="envelope"/>, the WHOLE envelope of
    /// <paramref name="unitId"/> (R17 #1). The launch admission refuses with it (<see cref="AiAttemptStore.Begin"/>), and it is
    /// here for <c>U-org-verbs</c> to refuse such a split before it is written: one check, two callers.
    /// </summary>
    public static string? Unfundable(string seat, string unitId, decimal envelope, decimal reservation) =>
        reservation > envelope ? Labels.SeatExceedsEnvelope(seat, reservation, OrgEnvelopes.NameOf(unitId), envelope) : null;

    /// <summary>
    /// WHETHER A ROLE STRING IS ONE OF THE APP'S OWN PRINCIPALS (<see cref="AppPrincipals"/>) — code writing under
    /// a name, never a position. Exact, as every role comparison in this product is.
    /// </summary>
    public static bool IsAppPrincipal(string? role) => role is not null && Array.IndexOf(AppPrincipals.All, role) >= 0;

    /// <summary>
    /// WHETHER A NAME IS OUTSIDE THE CHART — one of the app's own principals, or a seat of the security board (<c>U-board-a1</c>;
    /// <see cref="Board.BoardSeats.IsBoardId"/>). Neither is ever a position, a holder, a decider, a note's party or a verb's caller.
    /// </summary>
    public static bool IsOffChart(string? id) => IsAppPrincipal(id) || Board.BoardSeats.IsBoardId(id);

    static TradeAgentException Damaged(string why) =>
        new(ErrorCode.STATE_DATABASE_CORRUPT, $"the organisation chart is damaged: {why}");

    static OrgUnitRow ReadUnit(SqliteDataReader r) => new(
        r.GetString(0), Sql.S(r.GetValue(1)), r.GetString(2), Sql.S(r.GetValue(3)), r.GetString(4),
        Sql.S(r.GetValue(5)), Sql.S(r.GetValue(6)), Sql.Time(r.GetValue(7)), Sql.TimeN(r.GetValue(8)),
        Sql.S(r.GetValue(9)), Sql.S(r.GetValue(10)));

    static OrgPositionRow ReadPosition(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), Sql.S(r.GetValue(4)),
        Sql.S(r.GetValue(5)), Sql.S(r.GetValue(6)), Sql.S(r.GetValue(7)), Sql.Time(r.GetValue(8)),
        Sql.TimeN(r.GetValue(9)));
}

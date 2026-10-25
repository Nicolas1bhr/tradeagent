namespace TradeAgent.Core.Db;

/// <summary>
/// THE STRUCTURAL OPERATIONS AND THE MANDATE VERBS THE RIGHTS MATRIX ANSWERS (<see cref="OrgRights"/>; ORCHESTRATION § 2.9). Strings,
/// as the chart's vocabulary is. <see cref="Charter"/>, <see cref="Hire"/> and <see cref="Appoint"/> have writers in this build
/// (<c>U-org-verbs-c</c>); the others are answered so that the verb that arrives for each reads an answer that was already here.
/// </summary>
public static class OrgOperation
{
    /// <summary>A new unit under the caller's own unit (<see cref="OrgStore.Charter"/>).</summary>
    public const string Charter = "charter";

    /// <summary>A new member position in a unit (<see cref="OrgStore.Hire"/>).</summary>
    public const string Hire = "hire";

    /// <summary>A new position as a VACANT unit's head (<see cref="OrgStore.Hire"/> with its head flag) — never a replacement.</summary>
    public const string Appoint = "appoint";

    /// <summary>A FILLED head's replacement — a different operation from an appointment, and no verb performs it in this build.</summary>
    public const string Replace = "replace";

    public const string Retire = "retire", Close = "close", Merge = "merge", Tilt = "tilt";

    /// <summary>The operations that restructure the chart.</summary>
    public static readonly string[] Structural = [Charter, Hire, Appoint, Replace, Retire, Close, Merge, Tilt];

    /// <summary>The O5a/O5b/O6a mandate verbs: who may act is the MANDATE ROW's decider or holder, never the matrix.</summary>
    public static readonly string[] Mandate = ["assign", "cancel", "rescope", "accept", "reject", "deliver", "decline", "propose"];

    /// <summary>
    /// A CUT RULE'S REVIEW ANSWERED (<c>U-org-scorecards-b</c>; <see cref="OrgStore.AnswerReview"/>): who answers is the reviewer the app
    /// woke for that review event, never the chart — and the answer changes nothing on it.
    /// </summary>
    public const string ReviewAnswer = "review-answer";

    /// <summary>The verbs the review event decides.</summary>
    public static readonly string[] Review = [ReviewAnswer];

    public static readonly string[] All = [.. Structural, .. Mandate, .. Review];
}

/// <summary>
/// THE RESERVED IDENTITIES A TARGET CAN BE (ORCHESTRATION § 2.9; ORGANISATION § 3; R24 § 4.1): what the matrix answers apart from the
/// caller's relation to the target. <see cref="None"/> is every other target, answered by the relation.
/// </summary>
public static class OrgReserved
{
    public const string None = "none";

    /// <summary>The organisation itself: its close, merge or tilt.</summary>
    public const string Root = "root";

    /// <summary><c>div-operations</c> and everything under it — the order-holding division's unit (R24 § 4.1: its seat, occupant and unit are the board's).</summary>
    public const string DivOperations = "div-operations";

    /// <summary>The <c>operations</c> position itself: its retirement or replacement.</summary>
    public const string Operations = "operations";

    /// <summary><c>div-research</c>: its close or merge, which would end the legacy <c>research</c> position's unit.</summary>
    public const string DivResearch = "div-research";

    /// <summary>The legacy <c>research</c> position: never retired or replaced by an agent (ORGANISATION § 3).</summary>
    public const string Research = "research";

    /// <summary>A division head's retirement or replacement (ORGANISATION § 2: an app trigger replaces, the chief only proposes).</summary>
    public const string DivisionHead = "division-head";

    /// <summary>The chief — the root's head: its appointment and replacement (R24 § 4.1: the board's).</summary>
    public const string Chief = "chief";

    public static readonly string[] All = [None, Root, DivOperations, Operations, DivResearch, Research, DivisionHead, Chief];
}

/// <summary>THE CALLER'S RELATION TO A TARGET UNIT, as the app reads it off the chart — never as the caller states it.</summary>
public static class OrgRelation
{
    /// <summary>The caller heads the target's parent unit (for a charter: the unit the new one is chartered under).</summary>
    public const string HeadOfParent = "head-of-parent";

    /// <summary>The caller heads the target unit itself.</summary>
    public const string HeadOfTarget = "head-of-target";

    /// <summary>Anything else: a member, a staff position, a head of another unit.</summary>
    public const string Other = "other";

    /// <summary>A row that answers whatever the relation (a reserved identity's, a mandate verb's).</summary>
    public const string Any = "any";

    public static readonly string[] Each = [HeadOfParent, HeadOfTarget, Other];
}

/// <summary>THE ANSWERS THE MATRIX GIVES, in words a refusal quotes.</summary>
public static class OrgRightAnswer
{
    /// <summary>The verb may write it — inside the bounds, the throttles and the earning test, which still refuse on their own.</summary>
    public const string Allowed = "allowed";

    /// <summary>
    /// A TWO-SEAT ACT OF THE SECURITY BOARD (R24 §§ 4.1, 4.2): never an organisation verb's to make. Until a board seat is ACTIVE no one
    /// can make it, and it is refused in those words — never allowed silently and never handed back to the owner.
    /// </summary>
    public const string BoardAct = "a two-seat board act";

    /// <summary>An app trigger or a two-seat board act (ORGANISATION § 2: a division head is replaced by an app trigger; R24 § 4.1).</summary>
    public const string AppTriggerOrBoardAct = "app trigger or a board act";

    /// <summary>The mandate verbs: the mandate row's decider or holder acts, and the store checks the row (O5a, O5b).</summary>
    public const string MandateRowDecides = "the mandate row decides";

    /// <summary>Only the head of the target's parent unit restructures it (ORGANISATION § 2 :107).</summary>
    public const string NotYours = "only the parent unit's head";

    /// <summary>A review answer: the reviewer the app woke for that review event answers it, and the store checks the event (<c>U-org-scorecards-b</c>).</summary>
    public const string ReviewEventDecides = "the review event decides";

    public static readonly string[] All = [Allowed, BoardAct, AppTriggerOrBoardAct, MandateRowDecides, NotYours, ReviewEventDecides];
}

/// <summary>One cell of the rights matrix: an operation, a reserved identity, a relation, and the answer with why.</summary>
public sealed record OrgRightsRow(string Operation, string Reserved, string Relation, string Answer, string Why);

/// <summary>
/// THE ONE RIGHTS MATRIX (ORCHESTRATION § 2.9; <c>U-org-verbs-c</c>): operation × the caller's relation to the target × reserved
/// identities, AS DATA the app enforces — every structural writer reads its answer before it writes, and a verb's gate (RECORDED seat
/// assurance, <c>OrgVerbCaller</c>) runs before it: headship alone grants nothing (ORG-001).
///
/// <para><b>R24-checked (2026-10-23).</b> Every answer that was "the owner's two presses" is now <see cref="OrgRightAnswer.BoardAct"/>:
/// the board holds what ORGANISATION § 3 gave the owner about <c>operations</c> — its seat, occupant and unit — and the chief's
/// appointment and replacement (R24 § 4.1). No answer here is the owner's: R24 § 4.3 keeps for him the board's composition and budget,
/// the audit line's budget, the unconfined-seat ceiling and his narrowing veto, none of them this matrix's. The board's verbs arrive with
/// <c>U-board-b</c>; no board seat is active in this build, so a board act is refused in words naming that.</para>
///
/// <para><b>Refuse, never guess.</b> A combination the matrix holds no row for is refused (<see cref="Answer"/> throws), never read as
/// allowed; a test holds that every operation × reserved identity the classifier can name × relation is answered.</para>
/// </summary>
public static class OrgRights
{
    /// <summary>Every row. Structural operations on an unreserved target are answered by the relation; reserved ones whatever it is.</summary>
    public static readonly IReadOnlyList<OrgRightsRow> Rows = Build();

    static List<OrgRightsRow> Build()
    {
        const string headOfParent = "the head of the parent unit restructures under it (ORGANISATION § 2)";
        const string onlyParent = "only the parent unit's head charters, hires, appoints, retires, closes, merges or tilts (ORGANISATION § 2 :107)";
        const string operations = "anything about `operations` — its seat, occupant and unit — is the security board's (R24 § 4.1; ORDER-001 keeps order permission bound to it in code)";
        const string chief = "the chief's appointment and replacement are the security board's (R24 § 4.1)";
        const string research = "the legacy `research` position is never retired or replaced by an agent (ORGANISATION § 3)";
        const string divisionHead = "a division head is replaced only by an app trigger or a board act; the chief proposes (ORGANISATION § 2; R24 § 4.1)";
        const string root = "the organisation itself is never closed, merged or tilted by a verb: the plan's day-total is the board's (R24 § 4.1)";
        const string divResearch = "closing or merging `div-research` would end the legacy `research` position's unit, which no agent does (ORGANISATION § 3)";

        var rows = new List<OrgRightsRow>();
        foreach (var op in OrgOperation.Structural)
        {
            rows.Add(new(op, OrgReserved.None, OrgRelation.HeadOfParent, OrgRightAnswer.Allowed, headOfParent));
            rows.Add(new(op, OrgReserved.None, OrgRelation.HeadOfTarget, OrgRightAnswer.NotYours, onlyParent));
            rows.Add(new(op, OrgReserved.None, OrgRelation.Other, OrgRightAnswer.NotYours, onlyParent));
            rows.Add(new(op, OrgReserved.DivOperations, OrgRelation.Any, OrgRightAnswer.BoardAct, operations));
        }
        foreach (var op in new[] { OrgOperation.Retire, OrgOperation.Replace })
        {
            rows.Add(new(op, OrgReserved.Operations, OrgRelation.Any, OrgRightAnswer.BoardAct, operations));
            rows.Add(new(op, OrgReserved.Research, OrgRelation.Any, OrgRightAnswer.AppTriggerOrBoardAct, research));
            rows.Add(new(op, OrgReserved.DivisionHead, OrgRelation.Any, OrgRightAnswer.AppTriggerOrBoardAct, divisionHead));
            rows.Add(new(op, OrgReserved.Chief, OrgRelation.Any, OrgRightAnswer.BoardAct, chief));
        }
        rows.Add(new(OrgOperation.Appoint, OrgReserved.Chief, OrgRelation.Any, OrgRightAnswer.BoardAct, chief));
        foreach (var op in new[] { OrgOperation.Close, OrgOperation.Merge })
            rows.Add(new(op, OrgReserved.DivResearch, OrgRelation.Any, OrgRightAnswer.AppTriggerOrBoardAct, divResearch));
        foreach (var op in new[] { OrgOperation.Close, OrgOperation.Merge, OrgOperation.Tilt })
            rows.Add(new(op, OrgReserved.Root, OrgRelation.Any, OrgRightAnswer.BoardAct, root));
        foreach (var op in OrgOperation.Mandate)
            rows.Add(new(op, OrgReserved.None, OrgRelation.Any, OrgRightAnswer.MandateRowDecides,
                "the mandate row's decider or holder acts, and the store checks the row (O5a, O5b)"));
        foreach (var op in OrgOperation.Review)
            rows.Add(new(op, OrgReserved.None, OrgRelation.Any, OrgRightAnswer.ReviewEventDecides,
                "the reviewer the app woke for the review event answers it, and the store checks the event (ORGANISATION § 8)"));
        return rows;
    }

    /// <summary>
    /// THE ROW THAT ANSWERS <paramref name="operation"/> on a target of <paramref name="reserved"/> identity by a caller in
    /// <paramref name="relation"/>: the exact row, else the reserved identity's row for any relation. None is refused, never guessed.
    /// </summary>
    /// <exception cref="TradeAgentException">The matrix holds no answer: refused.</exception>
    public static OrgRightsRow Answer(string operation, string reserved, string relation) =>
        Rows.FirstOrDefault(r => r.Operation == operation && r.Reserved == reserved && r.Relation == relation)
        ?? Rows.FirstOrDefault(r => r.Operation == operation && r.Reserved == reserved && r.Relation == OrgRelation.Any)
        ?? throw new TradeAgentException(ErrorCode.INVALID_REQUEST,
            $"the rights matrix holds no answer for '{operation}' on a {reserved} target by a caller who is {relation}: it is refused, "
            + "never guessed. Nothing was written.");

    /// <summary>
    /// THE RESERVED IDENTITY OF A TARGET, read off the chart (<c>U-org-verbs-c</c>): <paramref name="unit"/> is the target unit — for a
    /// charter, the unit the new one is chartered UNDER — <paramref name="line"/> its ancestors nearest first, and
    /// <paramref name="position"/> the position acted on, for a retirement or a replacement. The order-holding division's subtree first,
    /// for every operation; then the identities each operation reserves.
    /// </summary>
    public static string ReservedOf(string operation, OrgUnitRow unit, IReadOnlyList<OrgUnitRow> line, string? position = null)
    {
        if (unit.Id == OrgIds.Operations || line.Any(u => u.Id == OrgIds.Operations)) return OrgReserved.DivOperations;
        switch (operation)
        {
            case OrgOperation.Appoint when unit.IsRoot:
                return OrgReserved.Chief;
            case OrgOperation.Retire or OrgOperation.Replace:
                if (position == CouncilRoles.Operations) return OrgReserved.Operations;
                if (position == CouncilRoles.Research) return OrgReserved.Research;
                if (unit.IsRoot && (position is null || position == unit.HeadPositionId)) return OrgReserved.Chief;
                if (unit.Kind == OrgUnitKind.Division && (position is null || position == unit.HeadPositionId)) return OrgReserved.DivisionHead;
                return OrgReserved.None;
            case OrgOperation.Close or OrgOperation.Merge when unit.Id == OrgIds.Research:
                return OrgReserved.DivResearch;
            case OrgOperation.Close or OrgOperation.Merge or OrgOperation.Tilt when unit.IsRoot:
                return OrgReserved.Root;
            default:
                return OrgReserved.None;
        }
    }

    /// <summary>
    /// THE CALLER'S RELATION TO <paramref name="unit"/>: it heads the unit's parent, it heads the unit, or neither. For a charter the
    /// target is the parent-to-be, so the caller who heads it is the head of the new unit's parent — <see cref="OrgRelation.HeadOfParent"/>.
    /// </summary>
    public static string RelationOf(string operation, string caller, OrgUnitRow unit, IReadOnlyList<OrgUnitRow> line)
    {
        if (operation == OrgOperation.Charter)
            return unit.HeadPositionId == caller ? OrgRelation.HeadOfParent : OrgRelation.Other;
        if (line.Count > 0 && line[0].HeadPositionId == caller) return OrgRelation.HeadOfParent;
        return unit.HeadPositionId == caller ? OrgRelation.HeadOfTarget : OrgRelation.Other;
    }

    /// <summary>
    /// THE REFUSAL OF AN ANSWER THAT IS NOT <see cref="OrgRightAnswer.Allowed"/>, in words naming the answer and why, or null when it is.
    /// A board act is refused naming that no board seat is active in this build — never allowed silently, never handed to the owner.
    /// </summary>
    public static string? Refusal(OrgRightsRow row, string what) => row.Answer switch
    {
        OrgRightAnswer.Allowed => null,
        OrgRightAnswer.BoardAct =>
            $"{what} is {OrgRightAnswer.BoardAct} — {row.Why} — and never an organisation verb's to make. The security board's verbs "
            + "are not in this build and no board seat is active, so no one can make it yet: it is refused, not passed to anyone.",
        OrgRightAnswer.AppTriggerOrBoardAct =>
            $"{what} happens only by an {OrgRightAnswer.AppTriggerOrBoardAct} — {row.Why} — never by an organisation verb.",
        OrgRightAnswer.MandateRowDecides =>
            $"{what} is a mandate verb: {OrgRightAnswer.MandateRowDecides}, never the chart.",
        OrgRightAnswer.ReviewEventDecides =>
            $"{what} is a review's answer: {OrgRightAnswer.ReviewEventDecides}, never the chart.",
        _ => $"{what} is not yours: {row.Why}."
    };
}

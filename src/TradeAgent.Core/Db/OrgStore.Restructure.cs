using System.Globalization;

namespace TradeAgent.Core.Db;

/// <summary>
/// WHAT A RESTRUCTURING ENDS AND TOUCHES (<c>U-org-verbs-d</c> item 1; ORCHESTRATION § 2.9 <i>Restructuring is a transaction</i>):
/// <paramref name="Act"/> names it in the app's words (it becomes every cancelled mandate's reason); <paramref name="Decider"/> is who
/// decided it — a position, or <see cref="OrgDecider.Owner"/>; <paramref name="EndingUnits"/> the units it closes;
/// <paramref name="EndingPositions"/> the positions it ends; <paramref name="Fence"/> every position whose running launches it takes
/// authority from.
/// </summary>
internal sealed record Restructuring(string Act, string Decider, IReadOnlyCollection<string> EndingUnits,
    IReadOnlyCollection<string> EndingPositions, IReadOnlyCollection<string> Fence);

/// <summary>
/// A RETIREMENT AS ITS HEAD ASKS IT (<c>U-org-verbs-d</c> item 2): the position to retire, the rationale and options (claims), the app
/// records it cites, and its forecasts (at least one; a subject left empty is the position's unit).
/// </summary>
public sealed record RetireRequest(string Caller, string? Attempt, string Position, string Rationale, IReadOnlyList<string> Options,
    IReadOnlyList<OrgRef> Refs, IReadOnlyList<ForecastClaim> Forecasts);

/// <summary>What a retirement wrote: the position (ended), its <c>retire</c> decision, its forecasts, the mandates cancelled and the event's id.</summary>
public sealed record Retired(OrgPositionRow Position, DecisionRow Decision, IReadOnlyList<ForecastRow> Forecasts,
    IReadOnlyList<string> Cancelled, long EventId);

/// <summary>A CLOSE AS ITS HEAD ASKS IT (<c>U-org-verbs-d</c> item 3): the unit, the rationale and options, refs and forecasts.</summary>
public sealed record CloseRequest(string Caller, string? Attempt, string UnitId, string Rationale, IReadOnlyList<string> Options,
    IReadOnlyList<OrgRef> Refs, IReadOnlyList<ForecastClaim> Forecasts);

/// <summary>What a close wrote: the unit (closed), its decision, forecasts, the positions it ended, the mandates cancelled, the event's id.</summary>
public sealed record ClosedUnit(OrgUnitRow Unit, DecisionRow Decision, IReadOnlyList<ForecastRow> Forecasts, IReadOnlyList<string> Ended,
    IReadOnlyList<string> Cancelled, long EventId);

/// <summary>
/// A MERGE AS ITS HEAD ASKS IT (<c>U-org-verbs-d</c> item 3): <paramref name="UnitId"/> — A, the unit that closes — into
/// <paramref name="IntoUnitId"/> — B, its sibling of the same kind, which inherits A's positions and open child units.
/// </summary>
public sealed record MergeRequest(string Caller, string? Attempt, string UnitId, string IntoUnitId, string Rationale,
    IReadOnlyList<string> Options, IReadOnlyList<OrgRef> Refs, IReadOnlyList<ForecastClaim> Forecasts);

/// <summary>What a merge wrote: A (closed), B, the decision, forecasts, the positions and child units moved, the mandates cancelled, the event's id.</summary>
public sealed record MergedUnit(OrgUnitRow Closed, OrgUnitRow Successor, DecisionRow Decision, IReadOnlyList<ForecastRow> Forecasts,
    IReadOnlyList<string> MovedPositions, IReadOnlyList<string> MovedUnits, IReadOnlyList<string> Cancelled, long EventId);

/// <summary>
/// What the owner's veto did (<c>U-org-verbs-d</c> item 4): the event vetoed and its kind, the units and positions it restored DORMANT,
/// the units it closed and the positions it ended, the mandates its disposal cancelled, and the <c>vetoed</c> event's id.
/// </summary>
public sealed record Vetoed(long EventId, string Kind, IReadOnlyList<string> Restored, IReadOnlyList<string> Ended,
    IReadOnlyList<string> Cancelled, long VetoEventId);

/// <summary>What a restructuring disposed of: the mandates it cancelled and the fences it wrote (their <c>org_event</c> ids).</summary>
internal sealed record Disposal(IReadOnlyList<string> Cancelled, IReadOnlyList<long> Fences);

public sealed partial class OrgStore
{
    /// <summary>
    /// THE TRANSACTION EVERY RESTRUCTURING RUNS (<c>U-org-verbs-d</c> item 1; ORCHESTRATION § 2.9: "disposes every open mandate …,
    /// fences the grants it touches, … and keeps funding lineage"), inside the act's own write: every OPEN mandate held or decided
    /// inside what ends — by a position it ends, by any position in a unit it closes, or issued in such a unit — is <c>cancelled</c>
    /// with an app-composed reason naming the act, its holder's untaken wakes and its decider's untaken proposal wake withdrawn; every
    /// position it touches gets a position-wide <c>fenced</c> event marked with the last launch committed before it, so the running
    /// turn ends (part b) and no launch committed after it is touched. NOTHING in <c>ai_attempt</c> or <c>ai_attempt_funding</c> is
    /// written: a past charge stays on the chain it was admitted under, a LOST launch keeps its reservation (BUDGET-001), and trial and
    /// verdict counts carry because their keys carry no role (EVID-001). The legacy pair are never fenced (<see cref="NeverFenced"/>).
    /// Internal: only the store's own restructuring writers call it, each after its own checks.
    /// </summary>
    internal Disposal Restructure(Restructuring r, DateTimeOffset now) => db.Write(_ =>
    {
        var units = r.EndingUnits.ToHashSet(StringComparer.Ordinal);
        var inside = r.EndingPositions.ToHashSet(StringComparer.Ordinal);
        foreach (var p in Positions().Where(p => units.Contains(p.UnitId))) inside.Add(p.Id);

        var cancelled = new List<string>();
        var open = new List<(string Id, string Unit, string Decider, string Holder)>();
        using (var c = db.Cmd("SELECT id, unit_id, decider, holder FROM org_mandate ORDER BY created_at, id"))
        using (var rd = c.ExecuteReader())
            while (rd.Read()) open.Add((rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetString(3)));
        foreach (var m in open)
        {
            string? why = inside.Contains(m.Holder) ? $"its holder {m.Holder} is inside what this ends"
                : inside.Contains(m.Decider) ? $"its decider {m.Decider} is inside what this ends"
                : units.Contains(m.Unit) ? $"it was issued in {m.Unit}, which this closes"
                : null;
            if (why is null || !IsOpen(m.Id)) continue;
            var reason = $"{r.Act}: {why}, so the mandate is cancelled by the app (ORCHESTRATION § 2.9: a restructuring disposes every open mandate)";
            AppendMandateEvent(m.Id, now, MandateEventKind.Cancelled, AppActor, null, reason, []);
            var wakes = new MissionEventStore(db);
            wakes.CloseMandateWakes(m.Id, now, MissionEventDisposition.Withdrawn, reason);
            wakes.CloseMandateWakes(m.Id, now, MissionEventDisposition.Withdrawn, reason, kind: MissionEventKind.Proposal);
            cancelled.Add(m.Id);
        }

        var fences = new List<long>();
        foreach (var position in r.Fence.Distinct(StringComparer.Ordinal))
        {
            if (NeverFenced(position)) continue;
            var unit = UnitOf(position) ?? throw Damaged($"position {position} belongs to no unit, so its fence cannot be written");
            fences.Add(FencePosition(unit.Id, position, r.Decider, $"{r.Act}, decided by {r.Decider}", now));
        }
        return new Disposal(cancelled, fences);
    });

    /// <summary>
    /// RETIRES A POSITION (<c>U-org-verbs-d</c> item 2; ORGANISATION § 2 :107 — only the head of the position's unit's PARENT): the
    /// position <c>ended</c> with its time, the unit it headed left VACANT, the <c>retire</c> decision (rationale in the ledger), its
    /// forecasts, the restructuring's disposal (<see cref="Restructure"/>: every open mandate it holds or decides cancelled, the position
    /// fenced, so its running turn ends) and one <c>retired</c> event — in ONE write. Refused in words, nothing written: retiring itself;
    /// the rights matrix (<c>operations</c> and the chief a two-seat board act; <c>research</c> and a division head an app trigger or a
    /// board act; anyone but the parent's head not theirs); a position already ended; no forecast. Not counted in the weekly throttle
    /// (survey Q4).
    /// </summary>
    /// <exception cref="TradeAgentException">Refused; nothing was written.</exception>
    public Retired Retire(RetireRequest r, DateTimeOffset now) => db.Write(_ =>
    {
        ActiveCaller(r.Caller, "a position is retired by the head whose launch asks it");
        if (r.Position == r.Caller)
            throw Forbidden($"'{r.Caller}' cannot retire itself: a position is retired by the head of its unit's parent, never by its own launch.");
        var target = Position(r.Position) ?? throw Refused($"'{r.Position}' is no position on the chart.");
        var unit = Units().FirstOrDefault(u => u.Id == target.UnitId) ?? throw Damaged($"position {target.Id} belongs to {target.UnitId}, which is not a unit");
        var line = Ancestors(unit.Id);
        var right = OrgRights.Answer(OrgOperation.Retire, OrgRights.ReservedOf(OrgOperation.Retire, unit, line, target.Id),
            OrgRights.RelationOf(OrgOperation.Retire, r.Caller, unit, line));
        if (OrgRights.Refusal(right, $"retiring {target.Id}") is { } notYours) throw Forbidden(notYours);
        if (target.Status == OrgPositionStatus.Ended) throw Refused($"{target.Id} has ended already: nothing is retired twice.");
        ForecastsRefusal(r.Forecasts, "a retirement");

        var head = unit.HeadPositionId == target.Id;
        var choice = $"retire {target.Id} from {unit.Id}";
        var decision = WriteDecision(r.Caller, r.Attempt, OrgDecisionKind.Retire, r.Options.Count > 0 ? r.Options : [choice], choice,
            r.Rationale, r.Refs, now);
        EndPositions([target.Id], now);
        if (head) VacateHead(unit.Id, target.Id);
        var disposal = Restructure(new Restructuring($"{target.Id} was retired by {r.Caller}", r.Caller, [], [target.Id], [target.Id]), now);
        var forecasts = r.Forecasts.Select(f => AddForecast(r.Caller, r.Attempt, decision.Id, null, f, now, subjectByDefault: unit.Id)).ToList();
        var eventId = Event(now, unit.Id, target.Id, OrgEventKind.Retired, r.Caller,
            $"position={target.Id} unit={unit.Id} head={(head ? "yes" : "no")} was={target.Status} decision={decision.Id} "
            + $"cancelled={Ids(disposal.Cancelled)}");
        return new Retired(Position(target.Id)!, decision, forecasts, disposal.Cancelled, eventId);
    });

    /// <summary>
    /// CLOSES A UNIT (<c>U-org-verbs-d</c> item 3; ORGANISATION § 3): the unit <c>closed</c> with its time, every position in it
    /// <c>ended</c>, the <c>close</c> decision, its forecasts, the restructuring's disposal (every open mandate held, decided or issued
    /// inside it cancelled; each position fenced) and one <c>closed</c> event — in ONE write. Nothing is deleted: its decisions, events
    /// and the launches its chain funded stay as recorded. Refused in words, nothing written: the rights matrix (the organisation itself
    /// and <c>div-operations</c> a two-seat board act, <c>div-research</c> an app trigger or a board act); a unit closed already; open
    /// child units; a unit younger than <see cref="OrgBounds.MinimumLife"/> unless a forecast on its charter resolved; a third structural
    /// change in seven days; no forecast.
    /// </summary>
    /// <exception cref="TradeAgentException">Refused; nothing was written.</exception>
    public ClosedUnit Close(CloseRequest r, DateTimeOffset now) => db.Write(_ =>
    {
        ActiveCaller(r.Caller, "a unit is closed by the head whose launch asks it");
        var unit = Units().FirstOrDefault(u => u.Id == r.UnitId) ?? throw Refused($"'{r.UnitId}' is no unit on the chart.");
        var line = Ancestors(unit.Id);
        var right = OrgRights.Answer(OrgOperation.Close, OrgRights.ReservedOf(OrgOperation.Close, unit, line),
            OrgRights.RelationOf(OrgOperation.Close, r.Caller, unit, line));
        if (OrgRights.Refusal(right, $"closing {OrgEnvelopes.NameOf(unit.Id)}") is { } notYours) throw Forbidden(notYours);
        if (unit.Status == OrgUnitStatus.Closed) throw Refused($"{OrgEnvelopes.NameOf(unit.Id)} is closed already.");
        var children = Units().Where(u => u.ParentId == unit.Id && u.Status != OrgUnitStatus.Closed).Select(u => u.Id).ToList();
        if (children.Count > 0)
            throw Refused($"{OrgEnvelopes.NameOf(unit.Id)} has open units under it ({string.Join(", ", children)}): close or merge them first.");
        LifeRefusal(unit, now, "closed");
        if (ThrottleRefusal(now) is { } throttled) throw Refused(throttled);
        ForecastsRefusal(r.Forecasts, "a close");

        var choice = $"close {unit.Id}";
        var decision = WriteDecision(r.Caller, r.Attempt, OrgDecisionKind.Close, r.Options.Count > 0 ? r.Options : [choice], choice,
            r.Rationale, r.Refs, now);
        using (var c = db.Cmd("UPDATE org_unit SET status = $closed, closed_at = $at WHERE id = $u AND status <> $closed",
                   ("$closed", OrgUnitStatus.Closed), ("$at", Sql.T(now)), ("$u", unit.Id)))
            if (c.ExecuteNonQuery() != 1) throw Refused($"{OrgEnvelopes.NameOf(unit.Id)} was closed while this close was written.");
        var ended = Positions().Where(p => p.UnitId == unit.Id && p.Status != OrgPositionStatus.Ended).Select(p => p.Id).ToList();
        EndPositions(ended, now);
        var disposal = Restructure(new Restructuring($"{unit.Id} was closed by {r.Caller}", r.Caller, [unit.Id], ended, ended), now);
        // A CLOSED UNIT ADMITS NOTHING MORE, so a forecast whose subject is left empty is about the parent that carries on.
        var forecasts = r.Forecasts.Select(f => AddForecast(r.Caller, r.Attempt, decision.Id, null, f, now, subjectByDefault: unit.ParentId)).ToList();
        var eventId = Event(now, unit.Id, null, OrgEventKind.Closed, r.Caller,
            $"unit={unit.Id} parent={unit.ParentId} ended={Ids(ended)} decision={decision.Id} cancelled={Ids(disposal.Cancelled)}");
        return new ClosedUnit(Units().Single(u => u.Id == unit.Id), decision, forecasts, ended, disposal.Cancelled, eventId);
    });

    /// <summary>
    /// MERGES A UNIT INTO ITS SIBLING (<c>U-org-verbs-d</c> item 3; ORGANISATION § 3): A <c>closed</c> with B named as its successor,
    /// A's positions and open child units moved to B, A's head left vacant (the position is a member of B now), the <c>merge</c>
    /// decision, its forecasts, the restructuring's disposal (every open mandate held, decided or issued in A cancelled; A's positions,
    /// B's and every position in the subtrees of A's moved children fenced) and one <c>merged</c> event — in ONE write. A's history stays A's: its events, decisions and every launch its
    /// chain funded (lineage is immutable, BUDGET-001); only launches after the merge are funded through B. Refused in words, nothing
    /// written: the rights matrix on either unit; the same unit; either closed; not siblings of one parent; different kinds; A younger
    /// than <see cref="OrgBounds.MinimumLife"/> unless a forecast on its charter resolved; B past <see cref="OrgBounds.MaxMembers"/>
    /// members; a third structural change in seven days; no forecast.
    /// </summary>
    /// <exception cref="TradeAgentException">Refused; nothing was written.</exception>
    public MergedUnit Merge(MergeRequest r, DateTimeOffset now) => db.Write(_ =>
    {
        ActiveCaller(r.Caller, "units are merged by the head whose launch asks it");
        var all = Units();
        var a = all.FirstOrDefault(u => u.Id == r.UnitId) ?? throw Refused($"'{r.UnitId}' is no unit on the chart.");
        var b = all.FirstOrDefault(u => u.Id == r.IntoUnitId) ?? throw Refused($"'{r.IntoUnitId}' is no unit on the chart.");
        if (a.Id == b.Id) throw Refused($"{a.Id} is merged into a sibling, never into itself.");
        // THE MATRIX ON A FIRST (the caller heads its parent); B must be its sibling, so the caller's relation to B is the same, and
        // what is left to ask of B is its reserved identity.
        Rights(a);
        if (a.ParentId != b.ParentId)
            throw Refused($"{a.Id} hangs from {a.ParentId} and {b.Id} from {b.ParentId}: a merge joins siblings of one parent.");
        Rights(b);
        if (a.Status == OrgUnitStatus.Closed || b.Status == OrgUnitStatus.Closed)
            throw Refused($"{(a.Status == OrgUnitStatus.Closed ? a.Id : b.Id)} is closed: only open units merge.");
        if (a.Kind != b.Kind) throw Refused($"{a.Id} is a {a.Kind} and {b.Id} a {b.Kind}: a merge joins units of one kind.");
        LifeRefusal(a, now, "merged away");
        var moved = Positions().Where(p => p.UnitId == a.Id && p.Status != OrgPositionStatus.Ended).Select(p => p.Id).ToList();
        var stay = Positions().Where(p => p.UnitId == b.Id && p.Status != OrgPositionStatus.Ended).Select(p => p.Id).ToList();
        var members = stay.Count(p => p != b.HeadPositionId) + moved.Count;
        if (members > OrgBounds.MaxMembers)
            throw Refused($"{b.Id} would have {members.ToString(CultureInfo.InvariantCulture)} members, more than one head leads "
                          + $"({OrgBounds.MaxMembers.ToString(CultureInfo.InvariantCulture)}).");
        if (ThrottleRefusal(now) is { } throttled) throw Refused(throttled);
        ForecastsRefusal(r.Forecasts, "a merge");

        void Rights(OrgUnitRow u)
        {
            var line = Ancestors(u.Id);
            var right = OrgRights.Answer(OrgOperation.Merge, OrgRights.ReservedOf(OrgOperation.Merge, u, line),
                OrgRights.RelationOf(OrgOperation.Merge, r.Caller, u, line));
            if (OrgRights.Refusal(right, $"merging {OrgEnvelopes.NameOf(u.Id)}") is { } notYours) throw Forbidden(notYours);
        }

        var choice = $"merge {a.Id} into {b.Id}";
        var decision = WriteDecision(r.Caller, r.Attempt, OrgDecisionKind.Merge, r.Options.Count > 0 ? r.Options : [choice], choice,
            r.Rationale, r.Refs, now);
        // THE DISPOSAL FIRST, while A's positions are still A's: each fence names the unit its position stood in. Every position in the
        // subtrees of A's moved children is fenced too — their funding chain and the authority above them change with the merge (the
        // Codex review of U-org-verbs-d, P1).
        var children = all.Where(u => u.ParentId == a.Id && u.Status != OrgUnitStatus.Closed).Select(u => u.Id).ToList();
        var below = children.SelectMany(Subtree).Select(u => u.Id).ToHashSet(StringComparer.Ordinal);
        var underChildren = Positions().Where(p => below.Contains(p.UnitId) && p.Status != OrgPositionStatus.Ended).Select(p => p.Id).ToList();
        var disposal = Restructure(new Restructuring($"{a.Id} was merged into {b.Id} by {r.Caller}", r.Caller, [a.Id], [],
            [.. moved, .. stay, .. underChildren]), now);
        using (var c = db.Cmd("UPDATE org_unit SET status = $closed, closed_at = $at, head_position_id = NULL WHERE id = $a AND status <> $closed",
                   ("$closed", OrgUnitStatus.Closed), ("$at", Sql.T(now)), ("$a", a.Id)))
            if (c.ExecuteNonQuery() != 1) throw Refused($"{a.Id} was closed while this merge was written.");
        using (var c = db.Cmd("UPDATE org_position SET unit_id = $b WHERE unit_id = $a AND status <> $ended",
                   ("$b", b.Id), ("$a", a.Id), ("$ended", OrgPositionStatus.Ended)))
            c.ExecuteNonQuery();
        using (var c = db.Cmd("UPDATE org_unit SET parent_id = $b WHERE parent_id = $a AND status <> $closed",
                   ("$b", b.Id), ("$a", a.Id), ("$closed", OrgUnitStatus.Closed)))
            c.ExecuteNonQuery();
        var forecasts = r.Forecasts.Select(f => AddForecast(r.Caller, r.Attempt, decision.Id, null, f, now, subjectByDefault: b.Id)).ToList();
        var eventId = Event(now, a.Id, null, OrgEventKind.Merged, r.Caller,
            $"unit={a.Id} into={b.Id} head={a.HeadPositionId ?? "vacant"} moved={Ids(moved)} children={Ids(children)} decision={decision.Id} "
            + $"cancelled={Ids(disposal.Cancelled)}");
        var after = Units();
        return new MergedUnit(after.Single(u => u.Id == a.Id), after.Single(u => u.Id == b.Id), decision, forecasts, moved, children,
            disposal.Cancelled, eventId);
    });

    // ---- the owner's writers, in-process (item 4) ---------------------------------------------------------------------------------

    /// <summary>
    /// THE OWNER'S NARROWING VETO OF ONE STRUCTURAL EVENT (<c>U-org-verbs-d</c> item 4; ORGANISATION § 3; R24 § 4.3: it cancels an act
    /// and restores the state before it, never widens a limit or puts money to work) — IN-PROCESS ONLY: no pipe op, door verb,
    /// <c>trade</c> verb or setting reaches it (AUTH-001), and a guard test holds that. A <c>chartered</c> unit is closed (its positions
    /// ended); a <c>closed</c> unit is restored DORMANT with the positions that close ended, each DORMANT; a <c>retired</c> position is
    /// restored DORMANT — its unit's head again where it headed it and the head is still vacant; a <c>hired</c> position is ended. Each
    /// runs the restructuring's disposal (<see cref="Restructure"/>) over what it ends and fences every position it touches, with the
    /// owner as decider of record; and one <c>vetoed</c> event, decided by <see cref="OrgDecider.Owner"/> — never a structural change
    /// the weekly throttle counts. It restores STRUCTURE, never work: a mandate the act cancelled stays cancelled, a launch stays as it
    /// ran, a decision stays recorded. Refused in words, nothing written: no such event; one not decided by a position (the app's or the
    /// owner's own); one vetoed already; a merge (undoing it would move positions back across what has run since — not this build's);
    /// any other kind; a state the event no longer describes.
    /// </summary>
    /// <exception cref="TradeAgentException">Refused; nothing was written.</exception>
    public Vetoed Veto(long eventId, DateTimeOffset now) => db.Write(_ =>
    {
        string unitId, kind, decider, detail;
        string? positionId;
        using (var c = db.Cmd("SELECT unit_id, position_id, kind, decider, detail FROM org_event WHERE id = $id", ("$id", eventId)))
        using (var rd = c.ExecuteReader())
        {
            if (!rd.Read()) throw Refused($"event {eventId.ToString(CultureInfo.InvariantCulture)} is not on the chart's record.");
            (unitId, positionId, kind, decider, detail) = (rd.GetString(0), Sql.S(rd.GetValue(1)), rd.GetString(2), rd.GetString(3), rd.GetString(4));
        }
        var what = $"event {eventId.ToString(CultureInfo.InvariantCulture)} ({kind})";
        if (decider is OrgDecider.App or OrgDecider.Owner)
            throw Refused($"{what} was decided by {decider}: the veto undoes what the organisation decided, never the app's or your own act.");
        using (var c = db.Cmd("SELECT COUNT(*) FROM org_event WHERE kind = $vetoed AND detail LIKE $mark",
                   ("$vetoed", OrgEventKind.Vetoed), ("$mark", $"event={eventId.ToString(CultureInfo.InvariantCulture)} %")))
            if (Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture) != 0) throw Refused($"{what} is vetoed already.");

        var unit = Units().FirstOrDefault(u => u.Id == unitId) ?? throw Damaged($"{what} names {unitId}, which is not a unit");
        var act = $"the owner vetoed {what}";
        var restored = new List<string>();
        var ended = new List<string>();
        IReadOnlyList<string> cancelled = [];
        switch (kind)
        {
            case OrgEventKind.Chartered:
            {
                if (unit.Status == OrgUnitStatus.Closed) throw Refused($"{what}: {unit.Id} is closed already.");
                var children = Units().Where(u => u.ParentId == unit.Id && u.Status != OrgUnitStatus.Closed).Select(u => u.Id).ToList();
                if (children.Count > 0)
                    throw Refused($"{what}: {unit.Id} has open units under it ({string.Join(", ", children)}); veto or close those first.");
                using (var c = db.Cmd("UPDATE org_unit SET status = $closed, closed_at = $at WHERE id = $u",
                           ("$closed", OrgUnitStatus.Closed), ("$at", Sql.T(now)), ("$u", unit.Id)))
                    c.ExecuteNonQuery();
                ended.AddRange(Positions().Where(p => p.UnitId == unit.Id && p.Status != OrgPositionStatus.Ended).Select(p => p.Id));
                EndPositions(ended, now);
                cancelled = Restructure(new Restructuring(act, OrgDecider.Owner, [unit.Id], ended, ended), now).Cancelled;
                ended.Insert(0, unit.Id);
                break;
            }
            case OrgEventKind.Closed:
            {
                if (unit.Status != OrgUnitStatus.Closed) throw Refused($"{what}: {unit.Id} is not closed any more.");
                if (unit.ParentId is { } parent && Units().Single(u => u.Id == parent).Status == OrgUnitStatus.Closed)
                    throw Refused($"{what}: {unit.Id}'s parent {parent} is closed, so there is nothing to restore it under.");
                using (var c = db.Cmd("UPDATE org_unit SET status = $dormant, closed_at = NULL WHERE id = $u",
                           ("$dormant", OrgUnitStatus.Dormant), ("$u", unit.Id)))
                    c.ExecuteNonQuery();
                restored.Add(unit.Id);
                foreach (var p in IdsOf(DetailValue(detail, "ended"), what))
                    if (Position(p) is { Status: OrgPositionStatus.Ended } row && row.UnitId == unit.Id)
                    {
                        Restore(p);
                        restored.Add(p);
                    }
                Restructure(new Restructuring(act, OrgDecider.Owner, [], [], restored.Skip(1).ToList()), now);
                break;
            }
            case OrgEventKind.Retired:
            {
                var p = positionId ?? throw Damaged($"{what} names no position");
                if (Position(p) is not { Status: OrgPositionStatus.Ended } row || row.UnitId != unit.Id)
                    throw Refused($"{what}: {p} is not the ended member of {unit.Id} the retirement left.");
                if (unit.Status == OrgUnitStatus.Closed) throw Refused($"{what}: {unit.Id} is closed, so there is nothing to restore {p} into.");
                Restore(p);
                if (DetailValue(detail, "head") == "yes")
                {
                    using var c = db.Cmd("UPDATE org_unit SET head_position_id = $p WHERE id = $u AND head_position_id IS NULL", ("$p", p), ("$u", unit.Id));
                    c.ExecuteNonQuery();
                }
                restored.Add(p);
                Restructure(new Restructuring(act, OrgDecider.Owner, [], [], [p]), now);
                break;
            }
            case OrgEventKind.Hired:
            {
                var p = positionId ?? throw Damaged($"{what} names no position");
                if (Position(p) is not { } row || row.Status == OrgPositionStatus.Ended) throw Refused($"{what}: {p} has ended already.");
                EndPositions([p], now);
                if (unit.HeadPositionId == p) VacateHead(unit.Id, p);
                cancelled = Restructure(new Restructuring(act, OrgDecider.Owner, [], [p], [p]), now).Cancelled;
                ended.Add(p);
                break;
            }
            case OrgEventKind.Merged:
                throw Refused($"{what}: a merge is not undone by a veto in this build — its positions have run in their new unit since, and "
                              + "moving them back is a restructuring of its own.");
            default:
                throw Refused($"{what} is no structural act a veto undoes: a charter, a close, a retirement or a hire.");
        }

        var vetoId = Event(now, unit.Id, positionId, OrgEventKind.Vetoed, OrgDecider.Owner,
            $"event={eventId.ToString(CultureInfo.InvariantCulture)} kind={kind} restored={Ids(restored)} ended={Ids(ended)} cancelled={Ids(cancelled)}");
        return new Vetoed(eventId, kind, restored, ended, cancelled, vetoId);
    });

    /// <summary>
    /// THE OWNER ACTIVATES A DORMANT POSITION (<c>U-org-verbs-d</c> item 4; survey Q2) — IN-PROCESS ONLY (AUTH-001: no pipe op, door
    /// verb, <c>trade</c> verb or setting reaches it) and only while no board seat is ACTIVE: a board act on plan capacity with no other
    /// decider stays his until then (R24; the orchestrator's generalised ruling), and once a seat is active it is the board's, refused
    /// here in words naming it. Dormant → active and one <c>activated</c> event decided by <see cref="OrgDecider.Owner"/>. It starts
    /// nothing: the activation gate still decides every launch (<see cref="PositionActivation"/>), the proven-cell rule unchanged.
    /// <paramref name="declaredClass"/> is the host's declared seat class per position, and <paramref name="seatSetting"/> the owner's
    /// unconfined-seat setting: a position on a counted seat is refused while the ceiling is reached (<see cref="UnconfinedSeatCeiling"/>,
    /// item 5). Refused in words, nothing written: a board seat active; no such position; one not dormant; one in a closed unit; the
    /// unconfined-seat ceiling reached.
    /// </summary>
    /// <exception cref="TradeAgentException">Refused; nothing was written.</exception>
    public OrgPositionRow Activate(string positionId, Func<string, string?> declaredClass, int seatSetting, DateTimeOffset now) => db.Write(_ =>
    {
        if (new Board.BoardReader(db).AnyActive)
            throw Refused($"activating {positionId} is the security board's now that a board seat is active (R24): it is a board act on the "
                          + "organisation's plan capacity, made by board seats — no longer yours in the app.");
        var row = Position(positionId) ?? throw Refused($"'{positionId}' is no position on the chart.");
        if (row.Status != OrgPositionStatus.Dormant) throw Refused($"{positionId} is {row.Status}: only a dormant position is activated.");
        if (Units().FirstOrDefault(u => u.Id == row.UnitId) is not { Status: not OrgUnitStatus.Closed } unit)
            throw Refused($"{positionId}'s unit {row.UnitId} is closed: nothing is activated in a closed unit.");
        if (UnconfinedSeatCeiling.Refusal(positionId, declaredClass(positionId), UnconfinedSeatsActive(declaredClass), seatSetting) is { } over)
            throw Refused(over.Words);
        using (var c = db.Cmd("UPDATE org_position SET status = $active WHERE id = $p AND status = $dormant",
                   ("$active", OrgPositionStatus.Active), ("$p", positionId), ("$dormant", OrgPositionStatus.Dormant)))
            if (c.ExecuteNonQuery() != 1) throw Refused($"{positionId} changed while this activation was written.");
        Event(now, unit.Id, positionId, OrgEventKind.Activated, OrgDecider.Owner, $"position={positionId} unit={unit.Id} was={OrgPositionStatus.Dormant}");
        return Position(positionId)!;
    });

    /// <summary>
    /// THE ACTIVE POSITIONS ON SEATS THE OWNER'S UNCONFINED-SEAT CEILING COUNTS (<c>U-org-verbs-d</c> item 5): the legacy pair whenever
    /// they are active, and every other active position whose declared seat (<paramref name="declaredClass"/>, the host's answer) runs a
    /// model below a proven cell or was declared by nobody. One read.
    /// </summary>
    public IReadOnlyList<string> UnconfinedSeatsActive(Func<string, string?> declaredClass) => db.Read(_ =>
        (IReadOnlyList<string>)[.. Positions().Where(p => p.Status == OrgPositionStatus.Active
                                                         && (CouncilRoles.IsKnown(p.Id) || UnconfinedSeatCeiling.Counts(declaredClass(p.Id))))
                                    .Select(p => p.Id)]);

    /// <summary>
    /// THE UNITS WHOSE DAY <paramref name="unitId"/> CARRIES (<c>U-org-verbs-d</c>; ORCHESTRATION § 2.9: a merge "carries reservations,
    /// debts and history to the successor"): the unit itself and every unit merged into it, transitively — read from each
    /// <c>merged</c> event's app-composed successor link, never by rewriting <c>ai_attempt_funding</c>, so a launch A's chain funded
    /// stays A's on its row and counts in B's day as well. Read inside the caller's own read or write (admission's). A <c>merged</c>
    /// event whose link this build did not write is damage, refused, never read on a guess.
    /// </summary>
    internal static IReadOnlyList<string> CarriedBy(Database db, string unitId)
    {
        var links = new List<(string From, string Into)>();
        using (var c = db.Cmd("SELECT id, detail FROM org_event WHERE kind = $merged ORDER BY id", ("$merged", OrgEventKind.Merged)))
        using (var r = c.ExecuteReader())
            while (r.Read())
            {
                var detail = r.GetString(1);
                var what = $"merge event {r.GetInt64(0).ToString(CultureInfo.InvariantCulture)}";
                links.Add((DetailValue(detail, "unit") ?? throw Damaged($"{what} names no unit"),
                    DetailValue(detail, "into") ?? throw Damaged($"{what} names no successor")));
            }
        var carried = new List<string> { unitId };
        for (var i = 0; i < carried.Count; i++)
            foreach (var link in links.Where(l => l.Into == carried[i] && !carried.Contains(l.From, StringComparer.Ordinal)))
                carried.Add(link.From);
        return carried;
    }

    /// <summary>An ended position back to DORMANT, its end cleared, inside the caller's write.</summary>
    void Restore(string position)
    {
        using var c = db.Cmd("UPDATE org_position SET status = $dormant, ended_at = NULL WHERE id = $p AND status = $ended",
            ("$dormant", OrgPositionStatus.Dormant), ("$p", position), ("$ended", OrgPositionStatus.Ended));
        c.ExecuteNonQuery();
    }

    /// <summary>One <c>name=value</c> part of an app-composed event detail, or null.</summary>
    static string? DetailValue(string detail, string name)
    {
        foreach (var part in detail.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (part.StartsWith(name + "=", StringComparison.Ordinal)) return part[(name.Length + 1)..];
        return null;
    }

    /// <summary>A listed value's ids (<see cref="Ids"/>'s form); one this build did not write is damage, never read on a guess.</summary>
    static IReadOnlyList<string> IdsOf(string? listed, string what) => listed switch
    {
        null => throw Damaged($"{what} carries no list this build wrote"),
        "none" => [],
        _ => listed.Split(',')
    };

    // ---- the restructuring writers' parts ----------------------------------------------------------------------------------------

    /// <summary>The caller is an active position on the chart — never an app principal or a board seat — or refused in words.</summary>
    void ActiveCaller(string caller, string what)
    {
        if (IsOffChart(caller) || Position(caller) is not { Status: OrgPositionStatus.Active })
            throw Forbidden($"'{caller}' is no active position: {what}.");
    }

    /// <summary>A structural act is a decision with a forecast: 1 to the most a decision carries.</summary>
    static void ForecastsRefusal(IReadOnlyList<ForecastClaim> forecasts, string act)
    {
        if (forecasts.Count is 0 or > DecisionLimits.MaxForecasts)
            throw Refused($"{act} carries 1 to {DecisionLimits.MaxForecasts.ToString(CultureInfo.InvariantCulture)} forecasts — such as "
                          + $"'{ForecastVocabulary.UnitAdmissible}' on the unit, its subject left empty: a structural act is a decision with a forecast.");
    }

    /// <summary>
    /// A UNIT LIVES <see cref="OrgBounds.MinimumLife"/> (ORGANISATION § 3) — or until a precommitted kill criterion resolves: a forecast
    /// on its <c>charter</c> decision the app resolved yes or no. A seeded unit has no charter, and its age is the rung's.
    /// </summary>
    void LifeRefusal(OrgUnitRow unit, DateTimeOffset now, string act)
    {
        if (now - unit.CreatedAt >= OrgBounds.MinimumLife) return;
        if (unit.CharterDecisionId is { } charter)
        {
            using var c = db.Cmd("""
                SELECT EXISTS (SELECT 1 FROM org_forecast f JOIN org_forecast_resolution r ON r.forecast_id = f.id
                                WHERE f.decision_id = $charter AND r.outcome IN ('yes', 'no'))
                """, ("$charter", charter));
            if (Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture) != 0) return;
        }
        throw Refused($"{OrgEnvelopes.NameOf(unit.Id)} was chartered {(now - unit.CreatedAt).TotalDays.ToString("0.#", CultureInfo.InvariantCulture)} "
                      + $"days ago, and a unit lives {OrgBounds.MinimumLife.TotalDays.ToString(CultureInfo.InvariantCulture)} days before it is "
                      + $"{act} — or until a forecast on its charter resolves, and none has (ORGANISATION § 3).");
    }

    /// <summary>Each of <paramref name="positions"/> <c>ended</c> at <paramref name="now"/>, inside the caller's write.</summary>
    void EndPositions(IReadOnlyCollection<string> positions, DateTimeOffset now)
    {
        foreach (var p in positions)
        {
            using var c = db.Cmd("UPDATE org_position SET status = $ended, ended_at = $at WHERE id = $p AND status <> $ended",
                ("$ended", OrgPositionStatus.Ended), ("$at", Sql.T(now)), ("$p", p));
            if (c.ExecuteNonQuery() != 1) throw Refused($"{p} ended while this was written.");
        }
    }

    /// <summary><paramref name="unitId"/>'s head left vacant where it is still <paramref name="position"/>, inside the caller's write.</summary>
    void VacateHead(string unitId, string position)
    {
        using var c = db.Cmd("UPDATE org_unit SET head_position_id = NULL WHERE id = $u AND head_position_id = $p", ("$u", unitId), ("$p", position));
        c.ExecuteNonQuery();
    }

    /// <summary>Ids in an event's detail: comma-separated, or <c>none</c>.</summary>
    static string Ids(IReadOnlyCollection<string> ids) => ids.Count == 0 ? "none" : string.Join(",", ids);
}

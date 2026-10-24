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

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
}

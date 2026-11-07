namespace TradeAgent.Core.Db;

public sealed partial class OrgStore
{
    /// <summary>
    /// HOW OFTEN THE CHIEF'S BACKSTOP COMES ROUND (R30 D21, <c>U-chief-moves</c>): its level's floor in force, and never less than the floor as
    /// shipped, so a board that lowers the floor to zero cannot turn this into a clock that spins. Null where it would not be shorter than the
    /// ceiling, or the policy holds no ceiling: no clock wakes the chief then, and this adds none.
    /// </summary>
    public static TimeSpan? ChiefFloorEvery(RewakeFloors policy)
    {
        const string level = OrgLevel.Department;     // the chief heads the root, depth 0: OrgLevel.Of(Head, 0)
        var ceiling = policy.Ceilings.TryGetValue(level, out var c) ? c : TimeSpan.Zero;
        if (ceiling <= TimeSpan.Zero) return null;
        var shipped = TimeSpan.FromMinutes(RewakeFloors.DefaultMinutes[level]);
        var floor = policy.Floor(level);
        var every = floor > shipped ? floor : shipped;
        return every < ceiling ? every : null;
    }

    /// <summary>
    /// THE ORGANISATION IS NOT MOVING AND ITS CHIEF DID NOT SAY WHEN IT WILL (R30 D21, <c>U-chief-moves</c>): the interval <paramref name="position"/>
    /// is woken at instead of its ceiling, or null where the ceiling stands. The one real chief turn so far read, wrote a lesson and asked for
    /// nothing, so what follows it is not left to prose. It applies to the active chief alone, while no work is moving, and then if nothing is
    /// chartered, or if it asked for no wake. It buys nothing by itself: the wake it moves is the chief's one ceiling, due its last woken turn
    /// plus this interval, and the admission still meters that turn against the envelopes up to the day-total.
    /// </summary>
    public TimeSpan? ChiefFloor(string position, RewakeFloors policy, DateTimeOffset now) => db.Read(_ =>
    {
        if (ChiefFloorEvery(policy) is not { } every || ActiveChief() != position || WorkMoving(position, now)) return (TimeSpan?)null;
        var chartered = Units().Any(u => !u.IsRoot && u.Status != OrgUnitStatus.Closed && u.CharterDecisionId is not null);
        return chartered && new MissionEventStore(db).Pending(MissionEventKind.Self, position).Count > 0 ? null : every;
    });

    /// <summary>
    /// WHETHER ANYONE BUT <paramref name="chief"/> OWES THE ORGANISATION WORK RIGHT NOW: an open mandate before its deadline, whose next move is an
    /// active position's other than the chief's. That is its holder's until it delivers, declines or is escalated, and its decider's from then on.
    /// A dormant holder moves nothing, and a mandate waiting on the chief's own judgment is the chief's to move.
    /// </summary>
    bool WorkMoving(string chief, DateTimeOffset now)
    {
        var active = Roster().Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in OpenMandates())
        {
            if (Mandate(id) is not { } m || MandateStatus(id) is not { } status || m.Deadline <= now) continue;
            var owes = status is MandateEventKind.Delivered or MandateEventKind.Escalated or MandateEventKind.Declined ? m.Decider : m.Holder;
            if (owes != chief && active.Contains(owes)) return true;
        }
        return false;
    }
}

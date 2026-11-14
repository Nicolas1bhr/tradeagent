namespace TradeAgent.Core.Db;

/// <summary>A unit directly under one a position heads, with what its staffing reads on the chart (<c>U-head-staffs</c>).</summary>
public sealed record StaffedUnit(OrgUnitRow Unit, string? Head, DateTimeOffset? HeadSince);

public sealed partial class OrgStore
{
    // ---- what a head owes the units under its own (RoleObjectives v5, U-head-staffs; D13-hire § 4.1) --------------------------------

    /// <summary>
    /// THE UNITS DIRECTLY UNDER THE ONE <paramref name="head"/> HEADS, not closed, in the order they were created — read in one snapshot. The
    /// root is under nobody, so it is never among them. Empty for a position that heads no unit.
    /// </summary>
    IReadOnlyList<OrgUnitRow> UnitsUnder(string head)
    {
        var units = Units();
        var mine = units.Where(u => u.HeadPositionId == head).Select(u => u.Id).ToHashSet(StringComparer.Ordinal);
        return [.. units.Where(u => u.ParentId is { } parent && mine.Contains(parent) && u.Status != OrgUnitStatus.Closed)];
    }

    /// <summary>
    /// EVERY UNIT UNDER <paramref name="head"/>'S OWN WHOSE HEAD IS VACANT: the hirer is the only one the rights matrix lets staff it (the parent's
    /// head appoints), so the vacancy is its open work until it appoints one. A staff unit has no head by design and is never one; the legacy
    /// divisions are headed, so they never are. Rows only: the chart the app's own verbs wrote.
    /// </summary>
    public IReadOnlyList<OrgUnitRow> UnstaffedUnder(string head) => db.Read(_ =>
        (IReadOnlyList<OrgUnitRow>)[.. UnitsUnder(head).Where(u => u.HeadPositionId is null && u.Kind != OrgUnitKind.Staff)]);

    /// <summary>
    /// EVERY ACTIVE HEAD OF A UNIT UNDER <paramref name="head"/>'S OWN THAT HOLDS NO OPEN MANDATE <paramref name="head"/> DECIDES — the same gap one
    /// arrow later: appointed, and given no work by the one who appointed it. A dormant head runs nothing yet, and the legacy pair, who head the
    /// legacy divisions, are never counted: their work is not the organisation's mandates. One mandate-assign closes it; a mandate decided by
    /// anyone else does not.
    /// </summary>
    public IReadOnlyList<StaffedUnit> UnmandatedUnder(string head) => db.Read(_ =>
    {
        var closing = MandateEventKind.Closing.Select((_, i) => $"$c{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}").ToArray();
        var found = new List<StaffedUnit>();
        foreach (var unit in UnitsUnder(head))
        {
            if (unit.HeadPositionId is not { } sitting || CouncilRoles.IsKnown(sitting)) continue;
            if (Position(sitting) is not { Status: OrgPositionStatus.Active } position) continue;
            using var c = db.Cmd($"""
                SELECT COUNT(*) FROM org_mandate m
                 WHERE m.holder = $holder AND m.decider = $decider
                   AND {LatestStatus} NOT IN ({string.Join(",", closing)})
                """, [("$holder", (object?)sitting), ("$decider", head),
                      .. closing.Select((n, i) => (n, (object?)MandateEventKind.Closing[i]))]);
            if (Convert.ToInt64(c.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
                found.Add(new StaffedUnit(unit, sitting, position.CreatedAt));
        }
        return (IReadOnlyList<StaffedUnit>)found;
    });

    /// <summary>
    /// THE UNITS DIRECTLY UNDER <paramref name="unitId"/>, not closed, in the order they were created, each with its head (null: vacant) — what
    /// <c>org-show</c> lists below the caller's own unit. Read in one snapshot.
    /// </summary>
    public IReadOnlyList<OrgUnitRow> Children(string unitId) => db.Read(_ =>
        (IReadOnlyList<OrgUnitRow>)[.. Units().Where(u => u.ParentId == unitId && u.Status != OrgUnitStatus.Closed)]);

    /// <summary>The positions of <paramref name="unitId"/> that have not ended, a dormant one included — as the hire's bound counts them.</summary>
    public int PositionsNotEnded(string unitId) => db.Read(_ =>
        Positions().Count(p => p.UnitId == unitId && p.Status != OrgPositionStatus.Ended));

    /// <summary>
    /// A UNIT'S COLLAPSE DATE WHERE IT WAS CHARTERED AS A BOUNDED EXPERIMENT, read from its <c>chartered</c> event as the earning test reads it,
    /// or null.
    /// </summary>
    public DateTimeOffset? CollapseBy(string unitId) => db.Read(_ => ExperimentUntil(unitId));
}

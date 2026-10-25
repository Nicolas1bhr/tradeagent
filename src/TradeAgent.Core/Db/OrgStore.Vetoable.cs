using System.Globalization;

namespace TradeAgent.Core.Db;

/// <summary>
/// ONE STRUCTURAL EVENT THE OWNER'S VETO WOULD ACCEPT NOW (<c>U-org-surface-b</c> item 1; R24 § 4.3): the event, its kind, unit and
/// position, the position that decided it, when — and <see cref="Words"/>, what the veto would do, in the app's own words.
/// </summary>
public sealed record VetoableEvent(long EventId, string Kind, string UnitId, string? PositionId, string Decider, DateTimeOffset At, string Words);

public sealed partial class OrgStore
{
    /// <summary>The <c>org_event</c> row a veto reads, as it reads it.</summary>
    sealed record VetoSubject(long Id, string UnitId, string? PositionId, string Kind, string Decider, string Detail, DateTimeOffset At);

    /// <summary>The kinds a veto undoes; a merge is not among them (<see cref="Veto"/>).</summary>
    static readonly string[] VetoKinds = [OrgEventKind.Chartered, OrgEventKind.Closed, OrgEventKind.Retired, OrgEventKind.Hired];

    VetoSubject? VetoSubjectOf(long eventId) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT id, unit_id, position_id, kind, decider, detail, at FROM org_event WHERE id = $id", ("$id", eventId));
        using var r = c.ExecuteReader();
        return r.Read() ? ReadSubject(r) : null;
    });

    static VetoSubject ReadSubject(Microsoft.Data.Sqlite.SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), Sql.S(r.GetValue(2)), r.GetString(3), r.GetString(4), r.GetString(5), Sql.Time(r.GetValue(6)));

    /// <summary>
    /// WHY THE OWNER'S VETO REFUSES <paramref name="e"/> NOW, in words — or null where it would accept it. The ONE check: <see cref="Veto"/>
    /// throws on its answer and <see cref="Vetoable"/> lists by it, so the page never offers what the store would refuse. Reads only.
    /// A row this build did not write is damage, thrown, never answered on a guess.
    /// </summary>
    string? WhyNotVetoable(VetoSubject e)
    {
        var what = $"event {e.Id.ToString(CultureInfo.InvariantCulture)} ({e.Kind})";
        if (e.Decider is OrgDecider.App or OrgDecider.Owner)
            return $"{what} was decided by {e.Decider}: the veto undoes what the organisation decided, never the app's or your own act.";
        using (var c = db.Cmd("SELECT COUNT(*) FROM org_event WHERE kind = $vetoed AND detail LIKE $mark",
                   ("$vetoed", OrgEventKind.Vetoed), ("$mark", $"event={e.Id.ToString(CultureInfo.InvariantCulture)} %")))
            if (Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture) != 0) return $"{what} is vetoed already.";

        var units = Units();
        var unit = units.FirstOrDefault(u => u.Id == e.UnitId) ?? throw Damaged($"{what} names {e.UnitId}, which is not a unit");
        switch (e.Kind)
        {
            case OrgEventKind.Chartered:
            {
                if (unit.Status == OrgUnitStatus.Closed) return $"{what}: {unit.Id} is closed already.";
                var children = units.Where(u => u.ParentId == unit.Id && u.Status != OrgUnitStatus.Closed).Select(u => u.Id).ToList();
                return children.Count > 0
                    ? $"{what}: {unit.Id} has open units under it ({string.Join(", ", children)}); veto or close those first."
                    : null;
            }
            case OrgEventKind.Closed:
                if (unit.Status != OrgUnitStatus.Closed) return $"{what}: {unit.Id} is not closed any more.";
                if (unit.ParentId is { } parent && units.Single(u => u.Id == parent).Status == OrgUnitStatus.Closed)
                    return $"{what}: {unit.Id}'s parent {parent} is closed, so there is nothing to restore it under.";
                return null;
            case OrgEventKind.Retired:
            {
                var p = e.PositionId ?? throw Damaged($"{what} names no position");
                if (Position(p) is not { Status: OrgPositionStatus.Ended } row || row.UnitId != unit.Id)
                    return $"{what}: {p} is not the ended member of {unit.Id} the retirement left.";
                return unit.Status == OrgUnitStatus.Closed ? $"{what}: {unit.Id} is closed, so there is nothing to restore {p} into." : null;
            }
            case OrgEventKind.Hired:
            {
                var p = e.PositionId ?? throw Damaged($"{what} names no position");
                return Position(p) is not { } row || row.Status == OrgPositionStatus.Ended ? $"{what}: {p} has ended already." : null;
            }
            case OrgEventKind.Merged:
                return $"{what}: a merge is not undone by a veto in this build — its positions have run in their new unit since, and "
                       + "moving them back is a restructuring of its own.";
            default:
                return $"{what} is no structural act a veto undoes: a charter, a close, a retirement or a hire.";
        }
    }

    /// <summary>
    /// EVERY STRUCTURAL EVENT THE OWNER'S VETO WOULD ACCEPT NOW (<c>U-org-surface-b</c> item 1; R24 § 4.3; ORGANISATION § 3), newest
    /// first: a charter, close, retirement or hire decided by a position — never the app's or the owner's own act, never a merge — not
    /// vetoed already and still describing the chart, each with what the veto would do in the app's words. Exactly what
    /// <see cref="Veto"/> accepts, because both ask <see cref="WhyNotVetoable"/>. A row this build did not write is named in
    /// <see cref="OrgList{T}.Refused"/>, never offered. Reads only; IN-PROCESS ONLY — the owner's page reads it, no pipe op or verb does.
    /// </summary>
    public OrgList<VetoableEvent> Vetoable() => db.Read(_ =>
    {
        var subjects = new List<VetoSubject>();
        var kinds = VetoKinds.Select((_, i) => $"$k{i.ToString(CultureInfo.InvariantCulture)}").ToArray();
        using (var c = db.Cmd($"""
                   SELECT id, unit_id, position_id, kind, decider, detail, at FROM org_event
                    WHERE kind IN ({string.Join(", ", kinds)}) AND decider NOT IN ($app, $owner)
                    ORDER BY id DESC
                   """,
                   [.. kinds.Select((k, i) => (k, (object?)VetoKinds[i])), ("$app", (object?)OrgDecider.App), ("$owner", (object?)OrgDecider.Owner)]))
        using (var r = c.ExecuteReader())
            while (r.Read()) subjects.Add(ReadSubject(r));

        var rows = new List<VetoableEvent>();
        var refused = new List<string>();
        foreach (var e in subjects)
        {
            try
            {
                if (WhyNotVetoable(e) is null)
                    rows.Add(new VetoableEvent(e.Id, e.Kind, e.UnitId, e.PositionId, e.Decider, e.At, VetoWords(e)));
            }
            catch (TradeAgentException ex)
            {
                refused.Add($"event {e.Id.ToString(CultureInfo.InvariantCulture)} cannot be read as TradeAgent writes one ({ex.Message}): it is not offered.");
            }
        }
        return new OrgList<VetoableEvent>(rows, refused);
    });

    /// <summary>What the veto of an event it accepts would do, in the app's words — the same restoration <see cref="Veto"/> writes.</summary>
    string VetoWords(VetoSubject e)
    {
        const string work = " It restores the chart, never work: a mandate the act cancelled stays cancelled, and what ran stays as it ran.";
        var unit = OrgEnvelopes.NameOf(e.UnitId);
        return e.Kind switch
        {
            OrgEventKind.Chartered => $"Vetoing it closes {unit} and ends every position in it; the mandates they hold are cancelled.",
            OrgEventKind.Closed => $"Vetoing it reopens {unit} dormant, with the positions its close ended, each dormant." + work,
            OrgEventKind.Retired => $"Vetoing it restores {e.PositionId} dormant in {unit}"
                                    + (DetailValue(e.Detail, "head") == "yes" ? ", as its head again while the head is vacant." : ".") + work,
            OrgEventKind.Hired => $"Vetoing it ends {e.PositionId}; the mandates it holds are cancelled.",
            _ => throw Damaged($"event {e.Id.ToString(CultureInfo.InvariantCulture)} is a kind the veto does not undo")
        };
    }
}

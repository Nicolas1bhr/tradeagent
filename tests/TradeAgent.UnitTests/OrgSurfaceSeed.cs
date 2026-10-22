using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE CHART THE ORGANISATION-FIRST TESTS SEED (<c>U-org-surface-a</c>): positions, launches and mandates written the way the ledger would,
/// by a test and never by a real model. <see cref="Head"/> heads <see cref="Team"/> under Research with <see cref="Member"/>; <see cref="Other"/>
/// heads <see cref="Elsewhere"/>.
/// </summary>
internal static class OrgSurfaceSeed
{
    internal const string Head = "pos-5ac0ffee0001", Member = "pos-5ac0ffee0002", Other = "pos-5bc0ffee0001";
    internal const string Team = "team-5ac0ffee", Elsewhere = "team-5bc0ffee";
    internal const string Stamp = "2026-10-11T12:00:00.0000000Z";

    internal static void Exec(Database db, string sql, params (string, object?)[] ps) => db.Write(_ =>
    {
        using var c = db.Cmd(sql, ps);
        return c.ExecuteNonQuery();
    });

    internal static void Seat(Database db, string id, string unit, string status = "active") =>
        Exec(db, "INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES($id,$u,$h,$s,$at)",
            ("$id", id), ("$u", unit), ("$h", $"org/{id}"), ("$s", status), ("$at", Stamp));

    internal static void HeadAUnit(Database db, string position, string unit, string parent)
    {
        Exec(db, "INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES($t,$p,'team',NULL,'active',$at)",
            ("$t", unit), ("$p", parent), ("$at", Stamp));
        Seat(db, position, unit);
        Exec(db, "UPDATE org_unit SET head_position_id=$id WHERE id=$t", ("$id", position), ("$t", unit));
    }

    /// <summary>The chart: <see cref="Head"/> heads <see cref="Team"/> under Research with <see cref="Member"/>; <see cref="Other"/> heads another team.</summary>
    internal static void Chart(Database db)
    {
        HeadAUnit(db, Head, Team, OrgIds.Research);
        Seat(db, Member, Team);
        HeadAUnit(db, Other, Elsewhere, OrgIds.Research);
    }

    internal static string Launch(Database db, string role, DateTimeOffset at, string seatClass = SeatClasses.Harness, string billing = BillingClasses.Key,
        string? mandate = null, string? unit = Team, decimal reserved = 0.10m)
    {
        var id = $"turn-{Guid.NewGuid():n}"[..17];
        var unitIds = unit is null ? [] : new[] { unit, OrgIds.Research, OrgIds.Root };
        var admission = new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = id, StartedAt = at, Role = role, ReservedCost = reserved, SeatClass = seatClass, BillingClass = billing, MandateId = mandate,
            Assurance = SeatClasses.AssuranceOf(seatClass) is { } a ? SeatClasses.Name(a) : null, Funding = unitIds
        });
        Assert.True(admission.Admitted, admission.Refusal);
        return id;
    }

    internal static void Priced(Database db, string id, decimal cost) =>
        Assert.True(new AiAttemptStore(db).End(id, 0, DateTimeOffset.UtcNow, 10, 0, 0, 10, 0, "model-x", cost, null, null));

    internal static void Unreported(Database db, string id) =>
        Assert.True(new AiAttemptStore(db).End(id, 0, DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null));

    internal static void Mandate(Database db, string id, string decider, string holder, params string[] events)
    {
        Exec(db, """
            INSERT INTO org_mandate(id, unit_id, decider, holder, kind, objective, checks, latitude, deadline, allocation_ai, retry_limit, created_at)
            VALUES($id,$u,$d,$h,'research','find something','[]','exact',$by,'1.00',3,$at)
            """, ("$id", id), ("$u", Team), ("$d", decider), ("$h", holder), ("$by", "2026-12-01T00:00:00.0000000Z"), ("$at", Stamp));
        foreach (var kind in events)
            Exec(db, "INSERT INTO org_mandate_event(mandate_id, at, kind, actor, reason) VALUES($m,$at,$k,$a,$r)",
                ("$m", id), ("$at", Stamp), ("$k", kind), ("$a", decider), ("$r", kind == "proposed" ? "I found a thing" : null));
    }
}

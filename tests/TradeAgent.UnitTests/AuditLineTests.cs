using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE PUBLIC AUDIT LINE, PART A (<c>U-org-audit-a</c>; ORCHESTRATION § 2.13, ORGANISATION §§ 1–4 and 13, VISION § 5.3): an app
/// principal outside the chart, its own file and ladder, a read-only reader of approved evidence only, and salted samples of accepted
/// deliveries re-checked by code — no model, no money path, no rung of the app's database. NOTHING HERE REACHES A REAL MODEL: every
/// position is written by the test with raw SQL and every launch id is one the test seeded.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public partial class AuditLineTests(ITestOutputHelper log)
{
    /// <summary>A team head under Research's division and a member of that team.</summary>
    const string Head = "pos-a0d17000001", Member = "pos-a0d17000002";

    const string Team = "team-a0d17000";

    /// <summary>The audit line's principal, spelled here independently of the build's constant.</summary>
    const string Audit = "audit";

    const string Stamp = "2026-10-22T12:00:00.0000000Z";

    static void Exec(Database db, string sql, params (string, object?)[] ps) => db.Write(_ =>
    {
        using var c = db.Cmd(sql, ps);
        return c.ExecuteNonQuery();
    });

    static void Seat(Database db, string id, string unit) =>
        Exec(db, "INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES($id,$u,$h,'active',$at)",
            ("$id", id), ("$u", unit), ("$h", $"org/{id}"), ("$at", Stamp));

    /// <summary>The chart every test starts from: <see cref="Head"/> heads <see cref="Team"/> with <see cref="Member"/> in it.</summary>
    static void Chart(Database db)
    {
        Exec(db, "INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES($t,$p,'team',NULL,'active',$at)",
            ("$t", Team), ("$p", OrgIds.Research), ("$at", Stamp));
        Seat(db, Head, Team);
        Exec(db, "UPDATE org_unit SET head_position_id=$id WHERE id=$t", ("$id", Head), ("$t", Team));
        Seat(db, Member, Team);
    }

    static IpcRequest Frame(string op, params (string Key, object Value)[] args) => new()
    {
        Op = op,
        Session = "audit-line",
        RequestId = "audit-" + Guid.NewGuid().ToString("n")[..12],
        Args = args.ToDictionary(a => a.Key, a => JsonSerializer.SerializeToElement(a.Value))
    };

    // ---- (a) the identity ------------------------------------------------------------------------------------------------

    /// <summary>
    /// (a) THE AUDIT PRINCIPAL IS REFUSED WHEREVER A POSITION IS NAMED. Even on a chart that holds a row under its id — as a build
    /// before the rule could have minted — it is never a position: the minting check refuses it as an app principal; a mandate to it
    /// and a note to it are refused naming it one; a decision under it and a note from it are refused as no active position; the
    /// gateway's one helper answers no position for it, so an organisation verb under a grant in its name is refused on the read loop
    /// and through the door. RED at base: <c>PositionIdRefusal("audit")</c> was null — the id could be minted.
    /// </summary>
    [Fact]
    public async Task The_audit_principal_is_refused_wherever_a_position_is_named()
    {
        var minted = OrgIds.PositionIdRefusal(Audit);
        log.WriteLine($"mint: {minted ?? "null — the id could be minted"}");
        Assert.Contains("app's own principals", minted ?? "", StringComparison.Ordinal);
        Assert.Contains(Audit, AppPrincipals.All);
        Assert.True(OrgStore.IsAppPrincipal(Audit));

        var (gw, _, db) = await TestEnv.Ready();
        await using var _gw = gw;
        using var _db = db;
        Chart(db);
        Seat(db, Audit, Team);
        var org = new OrgStore(db);
        var now = DateTimeOffset.UtcNow;

        var assign = org.AssignRefusal(new MandateIssue(Head, null, Audit, MandateKind.Task, "audit this", null,
            MandateShape.Report, false, now.AddDays(1), 1m, 1), now);
        log.WriteLine($"assign: {assign}");
        Assert.Contains("app's own principals", assign ?? "", StringComparison.Ordinal);

        var to = Assert.Throws<TradeAgentException>(() => org.SendLateral(Head, null, Audit, "a note", [], now));
        log.WriteLine($"note to: {to.Message}");
        Assert.Contains("app's own principals", to.Message, StringComparison.Ordinal);
        var from = Assert.Throws<TradeAgentException>(() => org.SendLateral(Audit, null, Member, "a note", [], now));
        Assert.Contains("no active position", from.Message, StringComparison.Ordinal);
        var decided = Assert.Throws<TradeAgentException>(() => org.Decide(Audit, null, OrgDecisionKind.Routine,
            ["a", "b"], "a", "because", [], [], now));
        Assert.Contains("no active position", decided.Message, StringComparison.Ordinal);
        Assert.Equal(0L, db.Read(_ =>
        {
            using var c = db.Cmd("SELECT (SELECT COUNT(*) FROM org_lateral) + (SELECT COUNT(*) FROM org_decision) + (SELECT COUNT(*) FROM org_mandate)");
            return (long)c.ExecuteScalar()!;
        }));

        Assert.Null(gw.ActivePositionOf(Audit));
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure());
        var loop = await server.CallAsync(Frame(Ops.MandateList), Audit, "turn-audit-1");
        Assert.False(loop.Ok);
        Assert.Contains("an app principal", loop.Error!.Message, StringComparison.Ordinal);
        var grant = new AgentGrants().IssueHere(Audit, "turn-audit-2");
        var door = await server.CallAsGrantAsync(Frame(Ops.MandateList), grant);
        Assert.False(door.Ok);
        log.WriteLine($"door: {door.Error!.Message}");
    }
}

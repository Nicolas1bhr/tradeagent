using System.Globalization;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// DECISIONS, FORECASTS, ESCALATIONS AND LATERAL NOTES (<c>U-org-decisions</c>, O5b; ORCHESTRATION §§ 2.3, 2.9–2.11; ORGANISATION
/// §§ 4, 5). NOTHING HERE REACHES A REAL MODEL (R1): every caller is an in-process <see cref="GatewayPipeServer"/> call under a
/// launch id the TEST seeded in the ledger with the seat assurance it names — the <c>OrgRightsTests</c> / <c>OrgAssignmentTests</c>
/// pattern — and every position beyond the legacy pair is written by the test with raw SQL, as no writer of the chart exists yet.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class OrgDecisionTests
{
    /// <summary>
    /// The chart: <see cref="Chief"/> heads <see cref="Sub"/> under Research's division; <see cref="Head"/> heads <see cref="Team"/>
    /// under it with two members; <see cref="Outsider"/> heads a team directly under <c>div-research</c>.
    /// </summary>
    const string Chief = "pos-c3c0ffee0001", Head = "pos-a1c0ffee0001", Member = "pos-a1c0ffee0002", Other = "pos-a1c0ffee0003",
        Outsider = "pos-b2c0ffee0001";

    const string Sub = "sub-c3c0ffee", Team = "team-a1c0ffee", Elsewhere = "team-b2c0ffee";

    const string Stamp = "2026-10-11T12:00:00.0000000Z";

    static readonly DateTimeOffset Due = DateTimeOffset.UtcNow.AddDays(7);

    // ---- the chart, as only a test may write it --------------------------------------------------------------------

    static void Exec(Database db, string sql, params (string, object?)[] ps) => db.Write(_ =>
    {
        using var c = db.Cmd(sql, ps);
        return c.ExecuteNonQuery();
    });

    static void HeadAUnit(Database db, string position, string unit, string parent, string kind = "team")
    {
        Exec(db, "INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES($t,$p,$k,NULL,'active',$at)",
            ("$t", unit), ("$p", parent), ("$k", kind), ("$at", Stamp));
        Seat(db, position, unit);
        Exec(db, "UPDATE org_unit SET head_position_id=$id WHERE id=$t", ("$id", position), ("$t", unit));
    }

    static void Seat(Database db, string id, string unit, string status = OrgPositionStatus.Active) =>
        Exec(db, "INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES($id,$u,$h,$s,$at)",
            ("$id", id), ("$u", unit), ("$h", $"org/{id}"), ("$s", status), ("$at", Stamp));

    static void Chart(Database db)
    {
        HeadAUnit(db, Chief, Sub, OrgIds.Research, "subdivision");
        HeadAUnit(db, Head, Team, Sub);
        Seat(db, Member, Team);
        Seat(db, Other, Team);
        HeadAUnit(db, Outsider, Elsewhere, OrgIds.Research);
    }

    /// <summary>A launch the test recorded, as the ledger would: LAUNCHED, on a seat of <paramref name="seatClass"/> — never a real model.</summary>
    static string Launch(Database db, string role, string seatClass = SeatClasses.Harness)
    {
        var id = $"turn-{Guid.NewGuid():n}"[..17];
        var admission = new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = id, StartedAt = DateTimeOffset.UtcNow, Role = role, ReservedCost = 0.10m, SeatClass = seatClass,
            Assurance = SeatClasses.AssuranceOf(seatClass) is { } a ? SeatClasses.Name(a) : null
        });
        Assert.True(admission.Admitted, admission.Refusal);
        return id;
    }

    static IpcRequest Frame(string op, params (string Key, object Value)[] args) => new()
    {
        Op = op,
        Session = "decisions",
        RequestId = "decisions-" + Guid.NewGuid().ToString("n")[..12],
        Args = args.ToDictionary(a => a.Key, a => JsonSerializer.SerializeToElement(a.Value))
    };

    static JsonElement Body(IpcResponse r) => JsonDocument.Parse(Json.Write(r.Data)).RootElement;

    static long Count(Database db, string table, string where = "1") => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT COUNT(*) FROM {table} WHERE {where}");
        return (long)c.ExecuteScalar()!;
    });

    static async Task<(TradingGateway Gw, Database Db, GatewayPipeServer Server)> Pipe()
    {
        var (gw, _, db) = await TestEnv.Ready();
        Chart(db);
        return (gw, db, new GatewayPipeServer(gw, IpcToken.Ensure()));
    }

    // ---- item 1: the records ---------------------------------------------------------------------------------------

    /// <summary>
    /// THE FIVE RECORDS ARE APPEND-ONLY AND THEIR SHAPES ARE THE SCHEMA'S (rung 41): no UPDATE or DELETE reaches a decision, a
    /// forecast, a resolution, an escalation or a note; a forecast is on exactly one of a decision and a mandate, its probability a
    /// decimal text from 0 to 1, and an unscored one says why — each refused by a CHECK, so no writer on the app's connection can
    /// store a row that says otherwise.
    /// </summary>
    [Fact]
    public void The_decision_records_are_append_only_and_their_shapes_are_the_schemas()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        Assert.Equal(Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture),
            db.Read(_ => { using var c = db.Cmd("SELECT value FROM meta WHERE key='schema_version'"); return c.ExecuteScalar() as string; }));

        var entry = new ResearchLedger(db).Add(Head, null, LedgerKind.Finding, "a rationale", LedgerMark.Claim, null, null, null).Id;
        Exec(db, """
            INSERT INTO org_decision(id, position, kind, options, choice, refs, ledger_entry_id, at)
            VALUES('dec-000000000001', $p, 'routine', '["a","b"]', 'a', '[]', $e, $at)
            """, ("$p", Head), ("$e", entry), ("$at", Stamp));
        const string forecast = """
            INSERT INTO org_forecast(id, decision_id, mandate_id, forecaster, vocabulary, predicate, subject, target, resolve_by,
                                     probability, scored, unscored_why, at)
            VALUES($id, $d, $m, $p, 1, 'verdict-class', 'v', 'promoted', $at, $prob, $scored, $why, $at)
            """;
        Exec(db, forecast, ("$id", "fc-000000000001"), ("$d", "dec-000000000001"), ("$m", null), ("$p", Head), ("$at", Stamp),
            ("$prob", "0.7"), ("$scored", 1), ("$why", null));
        Exec(db, "INSERT INTO org_forecast_resolution(forecast_id, outcome, record_kind, record_id, why, at) VALUES('fc-000000000001','void','version','v','none',$at)",
            ("$at", Stamp));
        Exec(db, "INSERT INTO org_escalation(id, sender, unit_id, reason, skipped, day, at) VALUES('esc-000000000001',$p,$u,'r','[]','2026-10-11',$at)",
            ("$p", Head), ("$u", Team), ("$at", Stamp));
        Exec(db, "INSERT INTO org_lateral(id, sender, to_position, text, refs, at) VALUES('lat-000000000001',$p,$q,'t','[]',$at)",
            ("$p", Head), ("$q", Member), ("$at", Stamp));

        foreach (var table in new[] { "org_decision", "org_forecast", "org_forecast_resolution", "org_escalation", "org_lateral" })
        {
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Exec(db, $"UPDATE {table} SET at='x'"));
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Exec(db, $"DELETE FROM {table}"));
            Assert.Equal(1L, Count(db, table));
        }

        // THE SHAPES: both or neither of decision and mandate; a probability outside 0..1 or not a decimal text; scored with a reason,
        // or unscored without one; an escalation with a recipient and no wake; a note to oneself; an id the app would not mint.
        foreach (var (d, m, prob, scored, why) in new (string?, string?, object, int, string?)[]
                 {
                     ("dec-000000000001", "man-x", "0.5", 1, null), (null, null, "0.5", 1, null), ("dec-000000000001", null, "1.5", 1, null),
                     ("dec-000000000001", null, "-0.1", 1, null),
                     ("dec-000000000001", null, "0.5", 1, "why"), ("dec-000000000001", null, "0.5", 0, null)
                 })
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Exec(db, forecast, ("$id", "fc-000000000002"), ("$d", d), ("$m", m),
                ("$p", Head), ("$at", Stamp), ("$prob", prob), ("$scored", scored), ("$why", why)));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Exec(db,
            "INSERT INTO org_escalation(id, sender, unit_id, to_position, reason, skipped, day, at) VALUES('esc-000000000002',$p,$u,$q,'r','[]','d',$at)",
            ("$p", Head), ("$u", Team), ("$q", Chief), ("$at", Stamp)));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Exec(db,
            "INSERT INTO org_lateral(id, sender, to_position, text, refs, at) VALUES('lat-000000000002',$p,$p,'t','[]',$at)",
            ("$p", Head), ("$at", Stamp)));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Exec(db,
            "INSERT INTO org_lateral(id, sender, to_position, text, refs, at) VALUES('chosen-by-a-caller',$p,$q,'t','[]',$at)",
            ("$p", Head), ("$q", Member), ("$at", Stamp)));
        Assert.Equal(1L, Count(db, "org_forecast"));
    }
}

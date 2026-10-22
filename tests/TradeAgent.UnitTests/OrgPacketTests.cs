using System.Globalization;
using System.Text;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A POSITION'S PACKET AND WHAT IT GAVE EACH ATTEMPT (<c>U-org-packets-b</c>; ORCHESTRATION §§ 2.6, 2.10–2.12, 8; ORGANISATION § 4):
/// the input-lineage rung, the reads by position, the composer's blocks and their bound. Store and composer only — the loop's half
/// is in <c>CouncilLoopOrgPacketTests.cs</c>. No real model, no real seat.
/// </summary>
public class OrgPacketTests
{
    /// <summary>
    /// The chart: <see cref="Head"/> heads <see cref="Team"/> under Research's division with two members, <see cref="P1"/> and
    /// <see cref="P2"/>; <see cref="Outsider"/> heads a team of its own.
    /// </summary>
    const string Head = "pos-9ac0ffee0001", P1 = "pos-9ac0ffee0002", P2 = "pos-9ac0ffee0003", Outsider = "pos-9bc0ffee0001";

    const string Team = "team-9ac0ffee", Elsewhere = "team-9bc0ffee";

    const string Stamp = "2026-10-11T12:00:00.0000000Z";

    static void Exec(Database db, string sql, params (string, object?)[] ps) => db.Write(_ =>
    {
        using var c = db.Cmd(sql, ps);
        return c.ExecuteNonQuery();
    });

    static void HeadAUnit(Database db, string position, string unit, string parent)
    {
        Exec(db, "INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES($t,$p,'team',NULL,'active',$at)",
            ("$t", unit), ("$p", parent), ("$at", Stamp));
        Seat(db, position, unit);
        Exec(db, "UPDATE org_unit SET head_position_id=$id WHERE id=$t", ("$id", position), ("$t", unit));
    }

    static void Seat(Database db, string id, string unit) =>
        Exec(db, "INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES($id,$u,$h,'active',$at)",
            ("$id", id), ("$u", unit), ("$h", $"org/{id}"), ("$at", Stamp));

    static void Chart(Database db)
    {
        HeadAUnit(db, Head, Team, OrgIds.Research);
        Seat(db, P1, Team);
        Seat(db, P2, Team);
        HeadAUnit(db, Outsider, Elsewhere, OrgIds.Research);
    }

    /// <summary>A launch the test recorded, as the ledger would: LAUNCHED on a harness seat — never a real model.</summary>
    static string Launch(Database db, string role)
    {
        var id = $"turn-{Guid.NewGuid():n}"[..17];
        var admission = new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = id, StartedAt = DateTimeOffset.UtcNow, Role = role, ReservedCost = 0.10m, SeatClass = SeatClasses.Harness,
            Assurance = SeatClasses.AssuranceOf(SeatClasses.Harness) is { } a ? SeatClasses.Name(a) : null
        });
        Assert.True(admission.Admitted, admission.Refusal);
        return id;
    }

    static long Count(Database db, string table, string where = "1") => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT COUNT(*) FROM {table} WHERE {where}");
        return (long)c.ExecuteScalar()!;
    });

    // ---- item 1: the rung ----------------------------------------------------------------------------------------------

    /// <summary>
    /// THE LINEAGE TABLE ARRIVES AT ITS RUNG, AND A CRASH BEFORE THE STAMP RUNS IT AGAIN: a new home has it at this build's stamp; the
    /// home reopened with the stamp one lower — the table in place with a row in it, and again with its triggers never made — climbs
    /// to the stamp with the row kept and both triggers present and refusing.
    /// </summary>
    [Fact]
    public void The_input_lineage_table_arrives_at_its_rung_and_a_crash_before_its_stamp_runs_it_again()
    {
        const int rung = Versions.InputLineageRung;
        Assert.True(Versions.DatabaseSchemaVersion >= rung, $"the table arrives at rung {rung}; this build says {Versions.DatabaseSchemaVersion}");
        var file = Path.Combine(TestEnv.Home, $"input-lineage-rung-{Guid.NewGuid():n}.db");
        string attempt;
        using (var db = new Database(file))
        {
            Chart(db);
            attempt = Launch(db, P2);
            new AiAttemptStore(db).RecordInputs(attempt, [new PacketInput(PacketInputKind.Lateral, "lat-000000000001")]);
        }

        foreach (var dropped in new[] { false, true })
        {
            using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file};Pooling=False"))
            {
                raw.Open();
                using var c = raw.CreateCommand();
                c.CommandText = (dropped ? "DROP TRIGGER ai_attempt_input_sealed_u; DROP TRIGGER ai_attempt_input_sealed_d; " : "")
                                + $"UPDATE meta SET value='{rung - 1}' WHERE key='schema_version';";
                c.ExecuteNonQuery();
            }
            using var rerun = new Database(file);
            Assert.Equal(Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture),
                rerun.Read(_ => { using var c = rerun.Cmd("SELECT value FROM meta WHERE key='schema_version'"); return c.ExecuteScalar() as string; }));
            Assert.Equal(2L, Count(rerun, "sqlite_master", "type='trigger' AND name LIKE 'ai_attempt_input_sealed_%'"));
            Assert.Equal([new PacketInput(PacketInputKind.Lateral, "lat-000000000001")], new AiAttemptStore(rerun).InputsOf(attempt));
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Exec(rerun, "DELETE FROM ai_attempt_input"));
        }
    }
}

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

    // ---- item 2: reads by position ---------------------------------------------------------------------------------------

    static LateralRow Note(Database db, string from, string to, string text, DateTimeOffset? at = null) =>
        new OrgStore(db).SendLateral(from, null, to, text, [], at ?? DateTimeOffset.UtcNow);

    /// <summary>A decision of <paramref name="position"/>'s carrying one forecast on a strategy version, by the store.</summary>
    static ForecastRow Forecast(Database db, string position, string version, DateTimeOffset by, decimal probability)
    {
        var decision = new OrgStore(db).Decide(position, null, OrgDecisionKind.Routine, ["a", "b"], "a", "because", [],
            [new ForecastClaim(ForecastVocabulary.VerdictClass, version, PromotionVerdict.Promoted, by, probability)], DateTimeOffset.UtcNow);
        return Assert.Single(new OrgStore(db).ForecastsOn(decision.Id));
    }

    /// <summary>A strategy version the ledger recorded, as only a test may write it.</summary>
    static void Version(Database db, string id) =>
        Exec(db, """
            INSERT INTO strategy_version(id, source, canonical, manifest, interpreter_build, parse_verdict, warm_up_bars, created_at, role, attempt)
            VALUES($id, 'x', 'x', '{}', 'test', 'ok', 0, $at, $role, NULL)
            """, ("$id", id), ("$at", Stamp), ("$role", P1));

    static void Resolve(Database db, string forecast, string outcome, string version) =>
        Exec(db, "INSERT INTO org_forecast_resolution(forecast_id, outcome, record_kind, record_id, why, at) VALUES($f,$o,'version',$v,'by its date',$at)",
            ("$f", forecast), ("$o", outcome), ("$v", version), ("$at", Sql(DateTimeOffset.UtcNow)));

    static string Sql(DateTimeOffset d) => d.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// (b) A NOTE THE POSITION SENT NEVER RIDES ITS OWN PACKET: P1 sends P2 a note and P2 sends P1 one; each position's unshown notes
    /// are the one addressed to it, never the one it sent. RED at base: no read by position existed (<c>LateralsOf</c> mixes both directions, every note, no cursor).
    /// </summary>
    [Fact]
    public void A_note_the_position_sent_never_rides_its_own_packet()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var toP2 = Note(db, P1, P2, "the funding skew looks venue-specific");
        var toP1 = Note(db, P2, P1, "the tape has gaps on Tuesday");

        var org = new OrgStore(db);
        Assert.Equal([toP1.Id], org.LateralsUnshown(P1).Rows.Select(n => n.Id));
        Assert.Equal([toP2.Id], org.LateralsUnshown(P2).Rows.Select(n => n.Id));
        Assert.Empty(org.LateralsUnshown(Head).Rows);

        // A MALFORMED ROW IS REFUSED IN WORDS, never thrown through the list: the readable note still comes.
        Exec(db, "INSERT INTO org_lateral(id, sender, to_position, text, refs, at) VALUES('lat-0000000bad01',$s,$p,'x','not json',$at)",
            ("$s", P1), ("$p", P2), ("$at", Stamp));
        var read = org.LateralsUnshown(P2);
        Assert.Equal([toP2.Id], read.Rows.Select(n => n.Id));
        Assert.Contains("lat-0000000bad01", Assert.Single(read.Refused), StringComparison.Ordinal);
    }

    /// <summary>
    /// (c) A POSITION SEES ONLY ITS OWN FORECASTS, AND NEVER ANOTHER FORECASTER'S PROBABILITY: P1 and the head each forecast on a
    /// version; P1's forecasts due are its own, the head's its own, P2's none; a resolution of P1's forecast is P1's alone. RED at
    /// base: <c>OpenForecasts()</c> had no position filter, and no read of a position's resolutions existed.
    /// </summary>
    [Fact]
    public void A_position_sees_only_its_own_forecasts_and_never_another_forecasters_probability()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        Version(db, "ver-own");
        var soon = DateTimeOffset.UtcNow.AddDays(2);
        var mine = Forecast(db, P1, "ver-own", soon, 0.7m);
        var heads = Forecast(db, Head, "ver-own", soon, 0.2m);
        var later = Forecast(db, P1, "ver-own", DateTimeOffset.UtcNow.AddDays(90), 0.4m);

        var org = new OrgStore(db);
        var horizon = DateTimeOffset.UtcNow.AddDays(7);
        Assert.Equal([mine.Id], org.ForecastsDue(P1, horizon).Rows.Select(f => f.Id));
        Assert.Equal([heads.Id], org.ForecastsDue(Head, horizon).Rows.Select(f => f.Id));
        Assert.Empty(org.ForecastsDue(P2, horizon).Rows);
        Assert.DoesNotContain(later.Id, org.ForecastsDue(P1, horizon).Rows.Select(f => f.Id));

        Resolve(db, mine.Id, ForecastOutcome.No, "ver-own");
        Assert.Equal([mine.Id], org.ResolutionsUnshown(P1).Rows.Select(r => r.Forecast.Id));
        Assert.Empty(org.ResolutionsUnshown(Head).Rows);
        Assert.Empty(org.ForecastsDue(P1, horizon).Rows);
    }
}

using System.Globalization;
using Microsoft.Data.Sqlite;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE ORGANISATION BECOMES APP-MINTED DATA — `U-org-ledger`.
///
/// <para><c>docs/ORGANISATION.md</c> § 3 asks for the chart as "one table with <c>parent</c> and <c>head</c>, no
/// role enum". Before this rung "role" was unconstrained TEXT on eight columns and on every launch grant, the two
/// roles were two constants in <c>CouncilRoles</c>, and nothing modelled a unit, a parent, a head, a seat or an
/// envelope. After it the database holds the root <c>org</c> (its head NULL: the owner, in-process), the divisions
/// <c>div-operations</c> and <c>div-research</c>, and two positions whose ids ARE the legacy role strings — so no
/// historical row changes meaning. Nothing reads it yet and no behaviour changes.</para>
///
/// <para><b>The assertions about the seed are raw SQL on purpose.</b> They are what a database holds after an
/// upgrade, whatever reads it later, and they compile against a build that has no store at all — which is how
/// they were watched failing before the rung existed ("no such table").</para>
/// </summary>
public class OrgLedgerTests
{
    /// <summary>
    /// THE LEDGER'S RUNG. Renumbered with the rung if <c>main</c> gains this number first: the ladder stays
    /// contiguous, and the tests below write the rung BEFORE it into <c>meta</c>.
    /// </summary>
    const int LedgerRung = 28;

    // ---- (a) the seed ------------------------------------------------------------------------------

    /// <summary>
    /// (a) AN UPGRADE SEEDS ONE ROOT, TWO DIVISIONS AND THE TWO LEGACY POSITIONS — on a fresh database, and on a
    /// file that predates the rung.
    ///
    /// <para>The second half is the one that matters to an installation: a database built at the rung below,
    /// with the ledger's tables absent and the stamp one lower, reopened by this build. It is made the way
    /// <c>PaperEligibleVerdictTests</c> makes its 22 — by raw SQL, undoing every rung above the target — because
    /// a reopen runs all of them, and a half-rolled-back database is one no installation has ever had.</para>
    /// </summary>
    [Fact]
    public void An_upgrade_seeds_one_root_two_divisions_and_the_two_legacy_positions()
    {
        using (var fresh = TestEnv.NewDb())
            AssertSeeded(fresh);

        var file = NewFile("predates");
        using (new Database(file)) { }
        StampBack(file, dropLedger: true);

        using var upgraded = new Database(file);
        AssertSeeded(upgraded);
    }

    /// <summary>
    /// WHAT THE SEED IS, row by row and column by column. Every NULL here is deliberate: a NULL envelope share,
    /// charter and seat are TODAY'S behaviour, and a NULL head on the root is the owner, whom no row names.
    /// </summary>
    static void AssertSeeded(Database db)
    {
        Assert.Equal(
            [
                "div-operations|org|division|operations|active|NULL|NULL|NULL",
                "div-research|org|division|research|active|NULL|NULL|NULL",
                "org|NULL|root|NULL|active|NULL|NULL|NULL",
            ],
            Rows(db, """
                SELECT id, parent_id, kind, head_position_id, status, envelope_share, charter_publication_id,
                       closed_at
                  FROM org_unit ORDER BY id
                """));

        // ONLY THE ROOT HAS KIND `root`, and it is the one unit with no parent.
        Assert.Equal(["org"], Rows(db, "SELECT id FROM org_unit WHERE kind='root' OR parent_id IS NULL"));

        Assert.Equal(
            [
                "operations|div-operations|agent|active|NULL|NULL|NULL|NULL|NULL",
                "research|div-research|research|active|NULL|NULL|NULL|NULL|NULL",
            ],
            Rows(db, """
                SELECT id, unit_id, home_dir, status, seat_runtime, seat_model, seat_allow_in, seat_allow_out,
                       ended_at
                  FROM org_position ORDER BY id
                """));

        // THE IDS ARE THE LEGACY ROLE STRINGS AND THE HOMES ARE THE FOLDERS THEY ALREADY HAVE — the constants
        // every store has been writing, so a row written before this rung names a position after it.
        foreach (var role in CouncilRoles.All)
            Assert.Equal([CouncilRoles.HomeDir(role)],
                Rows(db, "SELECT home_dir FROM org_position WHERE id=$id", ("$id", role)));

        // EACH DIVISION'S HEAD IS A POSITION OF THAT DIVISION.
        Assert.Equal(["div-operations|operations", "div-research|research"], Rows(db, """
            SELECT u.id, p.id FROM org_unit u JOIN org_position p ON p.id = u.head_position_id AND p.unit_id = u.id
             ORDER BY u.id
            """));

        // ONE EVENT PER SEED, decided by the app, its detail composed by the app from ids and fixed words.
        Assert.Equal(
            [
                "org|NULL|seeded|app|unit=org kind=root parent=none head=owner",
                "div-operations|NULL|seeded|app|unit=div-operations kind=division parent=org head=operations",
                "div-research|NULL|seeded|app|unit=div-research kind=division parent=org head=research",
                "div-operations|operations|seeded|app|position=operations unit=div-operations home=agent",
                "div-research|research|seeded|app|position=research unit=div-research home=research",
            ],
            Rows(db, "SELECT unit_id, position_id, kind, decider, detail FROM org_event ORDER BY id"));

        // AND EVERY ROW SAYS WHEN — the instant of the upgrade, in the spelling every other ledger here uses.
        foreach (var at in Rows(db, """
                     SELECT created_at FROM org_unit UNION ALL SELECT created_at FROM org_position
                     UNION ALL SELECT at FROM org_event
                     """))
            Assert.True(DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _),
                $"'{at}' is not a time");
    }

    // ---- (c) a rung that runs twice --------------------------------------------------------------------

    /// <summary>
    /// (c) RERUNNING THE LEDGER RUNG SEEDS NOTHING TWICE.
    ///
    /// <para><b>Why the rung can run twice at all.</b> <c>Migrate</c> reads the stamp once and every rung is
    /// <c>if (have &lt; N)</c>, so a second open never re-runs a rung that finished. But the rungs run in
    /// autocommit and the stamp is each rung's LAST statement, so a crash between the seed and the stamp opens
    /// next time at the rung below with the seed already written — and runs it again. That is the state made
    /// here, twice: once with the whole seed in place, once with the crash part-way through it (the last
    /// position and its event never written).</para>
    ///
    /// <para><b>The mutant this exists to catch</b> is the seed events inserted without their
    /// <c>WHERE NOT EXISTS</c>: <c>org_event</c>'s key is a counter, so no conflict clause can refuse a second
    /// copy, and every crash would add five more.</para>
    /// </summary>
    [Fact]
    public void Rerunning_the_ledger_rung_seeds_nothing_twice()
    {
        var file = NewFile("rerun");
        using (new Database(file)) { }

        // THE WHOLE SEED IN PLACE, THE STAMP ONE LOWER.
        StampBack(file, dropLedger: false);
        using (var db = new Database(file))
        {
            AssertOneOfEach(db);
            Assert.Equal(Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture), Stamp(db));
        }

        // AND A CRASH PART-WAY THROUGH IT: the last position and its seed event were never written.
        using (var raw = Raw(file))
        {
            using var c = raw.CreateCommand();
            c.CommandText = $"""
                DELETE FROM org_event WHERE position_id='research';
                DELETE FROM org_position WHERE id='research';
                UPDATE meta SET value='{LedgerRung - 1}' WHERE key='schema_version';
                """;
            c.ExecuteNonQuery();
        }

        using var reopened = new Database(file);
        AssertOneOfEach(reopened);
        Assert.Equal(Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture), Stamp(reopened));
    }

    /// <summary>Three units, two positions, and exactly one seed event for each of the five.</summary>
    static void AssertOneOfEach(Database db)
    {
        Assert.Equal(["div-operations", "div-research", "org"], Rows(db, "SELECT id FROM org_unit ORDER BY id"));
        Assert.Equal(["operations", "research"], Rows(db, "SELECT id FROM org_position ORDER BY id"));

        Assert.Equal(["div-operations|1", "div-research|1", "org|1"], Rows(db, """
            SELECT unit_id, COUNT(*) FROM org_event
             WHERE kind='seeded' AND position_id IS NULL GROUP BY unit_id ORDER BY unit_id
            """));
        Assert.Equal(["operations|1", "research|1"], Rows(db, """
            SELECT position_id, COUNT(*) FROM org_event
             WHERE kind='seeded' AND position_id IS NOT NULL GROUP BY position_id ORDER BY position_id
            """));
        Assert.Equal(["5"], Rows(db, "SELECT COUNT(*) FROM org_event"));
    }

    // ---- (e) the rung ------------------------------------------------------------------------------

    /// <summary>
    /// (e) THE ORGANISATION LEDGER ARRIVES AT ITS RUNG. A FLOOR and not the build's number, for the reason
    /// <c>BacktestLedgerTests</c> gives: pinning the exact figure makes every later additive rung fail here for
    /// no reason of its own. The stamp on disk is still required to equal what this build writes, so an upgrade
    /// that did not run is still caught.
    /// </summary>
    [Fact]
    public void The_organisation_ledger_arrives_at_its_rung()
    {
        using var db = TestEnv.NewDb();

        Assert.True(Versions.DatabaseSchemaVersion >= LedgerRung,
            $"the organisation ledger needs schema {LedgerRung} or later; this build says "
            + Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture), Stamp(db));

        foreach (var table in new[] { "org_unit", "org_position", "org_event" })
            Assert.Equal(["1"], Rows(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n",
                ("$n", table)));
    }

    // ---- the shape is SQL's, the vocabulary the store's ----------------------------------------------

    /// <summary>
    /// THE CHART'S SHAPE IS SQL'S AND ITS VOCABULARY IS THE STORE'S.
    ///
    /// <para><b>The shape</b> — one root, only the root without a parent, every unit under a unit that exists,
    /// every position and every event about rows that exist — holds for any writer on the app's connection,
    /// so a later verb cannot get it wrong by accident. <b>The vocabulary</b> is TEXT that the store validates
    /// when it writes, never an enum and never a CHECK: a kind or a status from a newer build is written and
    /// read as itself, for the reason <c>MissionEventKind</c> gives.</para>
    /// </summary>
    [Fact]
    public void The_chart_s_shape_is_sql_s_and_its_vocabulary_is_the_store_s()
    {
        Assert.Equal(["root", "division", "subdivision", "team", "staff"], OrgUnitKind.All);
        Assert.Equal(["chartered", "active", "dormant", "closed"], OrgUnitStatus.All);
        Assert.Equal(["active", "dormant", "ended"], OrgPositionStatus.All);
        Assert.True(OrgUnitKind.IsKnown(OrgUnitKind.Team));
        Assert.False(OrgUnitKind.IsKnown("guild"));
        Assert.False(OrgUnitStatus.IsKnown(null));
        Assert.False(OrgPositionStatus.IsKnown("retired"));

        using var db = TestEnv.NewDb();
        const string at = "2026-10-02T12:00:00.0000000Z";

        foreach (var refused in new[]
                 {
                     // a second root
                     $"INSERT INTO org_unit(id, parent_id, kind, status, created_at) VALUES('org-2', NULL, 'root', 'active', '{at}')",
                     // a unit with no parent that is not the root
                     $"INSERT INTO org_unit(id, parent_id, kind, status, created_at) VALUES('loose', NULL, 'division', 'active', '{at}')",
                     // a root that hangs from something
                     $"INSERT INTO org_unit(id, parent_id, kind, status, created_at) VALUES('sub-root', 'org', 'root', 'active', '{at}')",
                     // a unit under nothing
                     $"INSERT INTO org_unit(id, parent_id, kind, status, created_at) VALUES('orphan', 'nowhere', 'team', 'active', '{at}')",
                     // a position in no unit
                     $"INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES('drifter', 'nowhere', 'org/drifter', 'active', '{at}')",
                     // an event about no unit, and one about no position
                     $"INSERT INTO org_event(at, unit_id, kind, decider, detail) VALUES('{at}', 'nowhere', 'seeded', 'app', 'x')",
                     $"INSERT INTO org_event(at, unit_id, position_id, kind, decider, detail) VALUES('{at}', 'org', 'nobody', 'seeded', 'app', 'x')",
                 })
            Assert.Throws<SqliteException>(() => Exec(db, refused));

        Assert.Equal(["5"], Rows(db, """
            SELECT (SELECT COUNT(*) FROM org_unit) + (SELECT COUNT(*) FROM org_position)
            """));

        // AND THE VOCABULARY IS NOT SQL'S: written and read back as itself.
        Exec(db, $"INSERT INTO org_unit(id, parent_id, kind, status, created_at) VALUES('guild-1', 'org', 'guild', 'proposed', '{at}')");
        Assert.Equal(["guild|proposed"], Rows(db, "SELECT kind, status FROM org_unit WHERE id='guild-1'"));
    }

    // ---- helpers -----------------------------------------------------------------------------------

    static void Exec(Database db, string sql) => db.Write(_ =>
    {
        using var c = db.Cmd(sql);
        return c.ExecuteNonQuery();
    });

    static string NewFile(string what) => Path.Combine(TestEnv.Home, $"org-{what}-{Guid.NewGuid():n}.db");

    /// <summary>A connection that runs no migration — the way a test reaches a file underneath this build.</summary>
    static SqliteConnection Raw(string file)
    {
        var raw = new SqliteConnection($"Data Source={file};Pooling=False");
        raw.Open();
        return raw;
    }

    /// <summary>
    /// BACK TO THE RUNG BELOW THE LEDGER'S, by raw SQL. With <paramref name="dropLedger"/> the three tables go
    /// too — an installation that predates the rung; without it they stay — a crash between the seed and the
    /// stamp. EVERY RUNG ABOVE THE LEDGER'S HAS TO BE UNDONE HERE AS WELL, in landing order, for the reason
    /// <c>VenueCatalogTests</c> states against 16: a reopen runs all of them.
    /// </summary>
    static void StampBack(string file, bool dropLedger)
    {
        using var raw = Raw(file);
        using var c = raw.CreateCommand();
        c.CommandText = (dropLedger ? "DROP TABLE org_event; DROP TABLE org_position; DROP TABLE org_unit; " : "")
                        + $"UPDATE meta SET value='{LedgerRung - 1}' WHERE key='schema_version';";
        c.ExecuteNonQuery();
    }

    static string? Stamp(Database db) =>
        Rows(db, "SELECT value FROM meta WHERE key='schema_version'").SingleOrDefault();

    /// <summary>Every row of a query, its columns joined by <c>|</c> and a NULL spelled <c>NULL</c>.</summary>
    static List<string> Rows(Database db, string sql, params (string, object?)[] ps) => db.Read(_ =>
    {
        using var c = db.Cmd(sql, ps);
        using var r = c.ExecuteReader();
        var rows = new List<string>();
        while (r.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i =>
                r.IsDBNull(i) ? "NULL" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture))));
        return rows;
    });
}

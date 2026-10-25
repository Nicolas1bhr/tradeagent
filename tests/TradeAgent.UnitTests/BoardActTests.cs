using Microsoft.Data.Sqlite;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE BOARD'S ACTS AS RECORDS (<c>U-board-b1</c>; R24 §§ 4.2–4.4): the rung that stores them, the typed intent and its direction,
/// one seat narrowing at once, two seats widening, the owner's veto that only narrows. NOTHING HERE REACHES A MODEL: a board seat is
/// made active only by the test's own seam (<c>BoardStore.SetSeatStateUnderTest</c>), and every act is a call the test makes.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public partial class BoardActTests(ITestOutputHelper log)
{
    static string? Scalar(Database db, string sql, params (string, object?)[] ps) => db.Read(_ =>
    {
        using var c = db.Cmd(sql, ps);
        return Convert.ToString(c.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    });

    static int Exec(Database db, string sql, params (string, object?)[] ps) => db.Write(_ =>
    {
        using var c = db.Cmd(sql, ps);
        return c.ExecuteNonQuery();
    });

    /// <summary>Statements on a connection of their own, outside every store — an older file, made the way one is made.</summary>
    static void Raw(string file, string sql)
    {
        using var raw = new SqliteConnection($"Data Source={file};Pooling=False");
        raw.Open();
        using var c = raw.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    static string NewFile() => Path.Combine(TestEnv.Home, $"board-acts-{Guid.NewGuid():n}.db");

    const string Stamp = "2026-10-24T12:00:00.0000000Z";

    // ---- item 1: the rung ----------------------------------------------------------------------------------------------------

    /// <summary>Every row of the board's store, in one line: what a re-run of the rung must leave exactly as it was.</summary>
    static string Rows(Database db) => Scalar(db,
        "SELECT (SELECT group_concat(version || ':' || decider || ':' || basis || ':' || ifnull(act_id, '-') || ':' || body_sha256, ',') FROM board_policy)"
        + " || '#' || (SELECT group_concat(id || ':' || kind || ':' || ifnull(budget_share, '-'), ',') FROM board_event)"
        + " || '#' || (SELECT group_concat(id || ':' || kind || ':' || intent_sha256, ',') FROM board_act)"
        + " || '#' || (SELECT group_concat(id || ':' || kind || ':' || seat, ',') FROM board_act_event)"
        + " || '#' || (SELECT COUNT(*) FROM board_seat_state) || '/' || (SELECT COUNT(*) FROM board_forecast)"
        + " || '/' || (SELECT COUNT(*) FROM board_forecast_resolution) || '/' || (SELECT COUNT(*) FROM board_power_proposal)")!;

    /// <summary>
    /// A RE-RUN OF RUNG 51 AFTER A CRASH BEFORE ITS STAMP WRITES NOTHING TWICE: a home that holds an act, its event and the version
    /// it wrote, stamped back to 50 (<c>U-org-verbs-e</c>'s rung), opens again with every row as it was, the rebuild not repeated, and the stamp 51. RED before
    /// the rung: no <c>board_act</c> table.
    /// </summary>
    [Fact]
    public void Rung_51_rerun_after_a_crash_before_its_stamp_writes_nothing_twice()
    {
        var file = NewFile();
        var body = BoardPolicyBody.Canonical(BoardFounding.V1 with { PlanDayTotal = 30m });
        string first;
        using (var db = new Database(file))
        {
            Exec(db, "INSERT INTO board_act(id, kind, seat, attempt_id, intent, intent_sha256, policy_version, rationale, refs, evidence_class, expires_at, at) "
                     + "VALUES('bact-rerun', 'narrow', 'board-1', NULL, '[]', $sha, 1, 'a claim', '[]', NULL, NULL, $at)",
                ("$sha", Sha256Hex.Of("[]")), ("$at", Stamp));
            Exec(db, "INSERT INTO board_policy(version, decider, basis, act_id, written_at, mode, live_activated, body, body_sha256, supersedes) "
                     + "VALUES(2, 'board', 'act', 'bact-rerun', $at, 'PAPER', 0, $body, $sha, 1)",
                ("$at", Stamp), ("$body", body), ("$sha", Sha256Hex.Of(body)));
            Exec(db, "INSERT INTO board_act_event(act_id, kind, seat, attempt_id, rationale, proposer_family, decider_family, same_family, policy_version, detail, at) "
                     + "VALUES('bact-rerun', 'applied', 'board-1', NULL, NULL, NULL, NULL, NULL, 2, NULL, $at)", ("$at", Stamp));
            first = Rows(db);
            log.WriteLine(first);
        }
        Raw(file, $"UPDATE meta SET value='{Versions.BoardActsRung - 1}' WHERE key='schema_version';");
        using (var rerun = new Database(file))
        {
            Assert.Equal(first, Rows(rerun));
            Assert.Equal(Versions.DatabaseSchemaVersion.ToString(), Scalar(rerun, "SELECT value FROM meta WHERE key='schema_version'"));
            Assert.Throws<SqliteException>(() => Exec(rerun, "UPDATE board_policy SET body='x' WHERE version=2"));
            Assert.Throws<SqliteException>(() => Exec(rerun, "DELETE FROM board_act"));
        }
    }

    /// <summary>
    /// RUNG 52 — A CHANGE OF THE CHIEF IS STRUCTURE, NOT POLICY (<c>U-org-chief</c> fix pass 1), AND ITS RE-RUN WRITES NOTHING TWICE: a home
    /// holding a policy act applied with the version it wrote and a chief act confirmed with NONE, stamped back to 51 as a crash before the
    /// stamp leaves it, opens again with every row as it was, the rebuild not repeated, the seals held and the stamp this build's. The rule
    /// holds both ways at the store: an applied event of a policy act without a version is refused, and so is a confirmed event of a chief act
    /// naming one. A home stamped past this build is refused, naming both versions. RED at base: rung 51's CHECK refused the chief act's
    /// confirmed event without a version.
    /// </summary>
    [Fact]
    public void Rung_52_lets_a_chief_act_name_no_version_and_its_rerun_writes_nothing_twice()
    {
        var file = NewFile();
        var body = BoardPolicyBody.Canonical(BoardFounding.V1 with { PlanDayTotal = 30m });
        const string chief = """[{"field":"org.chief","from":null,"to":{"model":"scripted-1","runtime":"puppet"}}]""";
        string first;
        using (var db = new Database(file))
        {
            Exec(db, "INSERT INTO board_act(id, kind, seat, attempt_id, intent, intent_sha256, policy_version, rationale, refs, evidence_class, expires_at, at) "
                     + "VALUES('bact-policy', 'narrow', 'board-1', NULL, '[]', $sha, 1, 'a claim', '[]', NULL, NULL, $at)",
                ("$sha", Sha256Hex.Of("[]")), ("$at", Stamp));
            Exec(db, "INSERT INTO board_policy(version, decider, basis, act_id, written_at, mode, live_activated, body, body_sha256, supersedes) "
                     + "VALUES(2, 'board', 'act', 'bact-policy', $at, 'PAPER', 0, $body, $sha, 1)",
                ("$at", Stamp), ("$body", body), ("$sha", Sha256Hex.Of(body)));
            Exec(db, "INSERT INTO board_act_event(act_id, kind, seat, policy_version, at) VALUES('bact-policy', 'applied', 'board-1', 2, $at)", ("$at", Stamp));
            Exec(db, "INSERT INTO board_act(id, kind, seat, attempt_id, intent, intent_sha256, policy_version, rationale, refs, evidence_class, expires_at, at) "
                     + "VALUES('bact-chief', 'propose', 'board-1', NULL, $intent, $sha, 2, 'a claim', '[]', NULL, $at, $at)",
                ("$intent", chief), ("$sha", Sha256Hex.Of(chief)), ("$at", Stamp));
            Exec(db, "INSERT INTO board_act_event(act_id, kind, seat, rationale, same_family, policy_version, at) "
                     + "VALUES('bact-chief', 'confirmed', 'board-2', 'my own reading', 0, NULL, $at)", ("$at", Stamp));

            // BOTH WAYS: a policy act's applied event names its version; a chief act's confirmed event names none.
            Exec(db, "INSERT INTO board_act(id, kind, seat, attempt_id, intent, intent_sha256, policy_version, rationale, refs, evidence_class, expires_at, at) "
                     + "VALUES('bact-other', 'narrow', 'board-1', NULL, '[]', $sha, 2, 'a claim', '[]', NULL, NULL, $at)",
                ("$sha", Sha256Hex.Of("[]")), ("$at", Stamp));
            Assert.Contains("a change of the chief writes none", Assert.Throws<SqliteException>(() =>
                Exec(db, "INSERT INTO board_act_event(act_id, kind, seat, policy_version, at) VALUES('bact-other', 'applied', 'board-1', NULL, $at)",
                    ("$at", Stamp))).Message, StringComparison.Ordinal);
            Assert.Contains("a change of the chief writes none", Assert.Throws<SqliteException>(() =>
                Exec(db, "INSERT INTO board_act_event(act_id, kind, seat, rationale, same_family, policy_version, at) "
                         + "VALUES('bact-chief', 'confirmed', 'board-3', 'mine', 0, 2, $at)", ("$at", Stamp))).Message, StringComparison.Ordinal);
            first = Rows(db);
            log.WriteLine(first);
        }
        Raw(file, $"UPDATE meta SET value='{Versions.ChiefStructureRung - 1}' WHERE key='schema_version';");
        using (var rerun = new Database(file))
        {
            Assert.Equal(first, Rows(rerun));
            Assert.Equal(Versions.DatabaseSchemaVersion.ToString(), Scalar(rerun, "SELECT value FROM meta WHERE key='schema_version'"));
            Assert.Equal("1", Scalar(rerun, "SELECT COUNT(*) FROM board_act_event WHERE act_id='bact-chief' AND policy_version IS NULL"));
            Assert.Throws<SqliteException>(() => Exec(rerun, "UPDATE board_act_event SET detail='x'"));
            Assert.Throws<SqliteException>(() => Exec(rerun, "DELETE FROM board_act_event"));
            // THE ONE CLOSE PER ACT, made again with the table: the chief act closed by its confirmation closes no second time.
            Assert.Throws<SqliteException>(() => Exec(rerun,
                "INSERT INTO board_act_event(act_id, kind, seat, policy_version, at) VALUES('bact-chief', 'stale', 'app', NULL, $at)", ("$at", Stamp)));
        }
        Raw(file, $"UPDATE meta SET value='{Versions.ChiefStructureRung + 1}' WHERE key='schema_version';");
        var newer = Assert.Throws<TradeAgentException>(() => new Database(file));
        Assert.Contains($"{Versions.ChiefStructureRung + 1} is newer than this build supports ({Versions.ChiefStructureRung})", newer.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A HOME AT RUNG 50 OPENS AND GAINS 51 WITH ITS FOUNDING RECORD BYTE FOR BYTE: rung 49's own <c>board_policy</c> and
    /// <c>board_event</c> are rebuilt with every row copied as it was — the body and its hash unchanged, the decider <c>app</c>, no act —
    /// the new tables made, every table sealed, and the founding policy still the one in force. RED before the rung: the home stays at 50. The board's tables are in the shape rung 49 wrote
    /// them — rung 50 (<c>U-org-verbs-e</c>) does not touch them — and the home holds rung 50's own columns, since this build made it.
    /// </summary>
    [Fact]
    public void A_home_at_rung_50_opens_gains_51_and_keeps_its_founding_record()
    {
        var file = NewFile();
        string policy49, events49;
        using (var db = new Database(file))
        {
            policy49 = Scalar(db, "SELECT version || '|' || decider || '|' || basis || '|' || written_at || '|' || body || '|' || body_sha256 FROM board_policy")!;
            events49 = Scalar(db, "SELECT group_concat(id || '|' || kind || '|' || detail, ',') FROM board_event")!;
        }
        // RUNG 49's OWN SHAPE of the board's tables, as that rung wrote it (Database.cs at e317c011) and rung 50 left it, and the stamp back at 50.
        Raw(file, """
            PRAGMA foreign_keys=OFF;
            DROP TABLE IF EXISTS board_forecast_resolution; DROP TABLE IF EXISTS board_forecast; DROP TABLE IF EXISTS board_act_event;
            DROP TABLE IF EXISTS board_power_proposal; DROP TABLE IF EXISTS board_seat_state; DROP TABLE IF EXISTS board_act;
            ALTER TABLE board_event RENAME TO board_event_x;
            ALTER TABLE board_policy RENAME TO board_policy_x;
            CREATE TABLE board_policy(
              version        INTEGER PRIMARY KEY CHECK (version >= 1),
              decider        TEXT NOT NULL CHECK (decider = 'app'),
              basis          TEXT NOT NULL CHECK (basis = 'founding'),
              written_at     TEXT NOT NULL,
              mode           TEXT NOT NULL CHECK (mode IN ('OBSERVE','PAPER')),
              live_activated INTEGER NOT NULL CHECK (live_activated = 0),
              body           TEXT NOT NULL CHECK (length(body) BETWEEN 2 AND 65536),
              body_sha256    TEXT NOT NULL CHECK (length(body_sha256) = 64),
              supersedes     INTEGER REFERENCES board_policy(version),
              CHECK ((version = 1 AND supersedes IS NULL) OR supersedes = version - 1)
            );
            INSERT INTO board_policy SELECT version, decider, basis, written_at, mode, live_activated, body, body_sha256, supersedes FROM board_policy_x;
            CREATE TABLE board_event(
              id             INTEGER PRIMARY KEY AUTOINCREMENT,
              at             TEXT NOT NULL,
              kind           TEXT NOT NULL CHECK (kind IN ('founded','earlier-live-set-aside')),
              policy_version INTEGER REFERENCES board_policy(version),
              home_schema    INTEGER CHECK (home_schema IS NULL OR home_schema >= 0),
              budget_share   TEXT,
              detail         TEXT NOT NULL CHECK (length(detail) BETWEEN 1 AND 2000),
              CHECK (kind <> 'founded' OR (policy_version = 1 AND home_schema IS NOT NULL AND budget_share IS NOT NULL))
            );
            INSERT INTO board_event SELECT id, at, kind, policy_version, home_schema, budget_share, detail FROM board_event_x;
            DROP TABLE board_event_x;
            DROP TABLE board_policy_x;
            UPDATE meta SET value='50' WHERE key='schema_version';
            """);

        using var opened = new Database(file);
        Assert.Equal(Versions.DatabaseSchemaVersion.ToString(), Scalar(opened, "SELECT value FROM meta WHERE key='schema_version'"));
        Assert.Equal(policy49, Scalar(opened, "SELECT version || '|' || decider || '|' || basis || '|' || written_at || '|' || body || '|' || body_sha256 FROM board_policy"));
        Assert.Equal(events49, Scalar(opened, "SELECT group_concat(id || '|' || kind || '|' || detail, ',') FROM board_event"));
        Assert.Equal("1", Scalar(opened, "SELECT COUNT(*) FROM board_policy WHERE act_id IS NULL"));
        foreach (var table in new[] { "board_act", "board_act_event", "board_forecast", "board_forecast_resolution", "board_power_proposal", "board_seat_state" })
            Assert.Equal("1", Scalar(opened, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$t", ("$t", table)));
        Assert.Empty(opened.Read(_ =>
        {
            using var c = opened.Cmd("PRAGMA foreign_key_check");
            using var r = c.ExecuteReader();
            var bad = new List<string>();
            while (r.Read()) bad.Add(r.GetString(0));
            return bad;
        }));
        foreach (var sql in new[] { "UPDATE board_policy SET body='x'", "DELETE FROM board_policy", "UPDATE board_event SET detail='x'", "DELETE FROM board_event" })
            Assert.Throws<SqliteException>(() => Exec(opened, sql));
        var inForce = new BoardReader(opened).InForce();
        Assert.False(inForce.CouldNotBeRead, inForce.Why);
        Assert.Equal((1, BoardDecider.App), (inForce.Version, inForce.Decider));
    }
}

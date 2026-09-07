using Microsoft.Data.Sqlite;

namespace TradeAgent.Core.Db;

/// <summary>
/// One small transactional store. Deliberately hand-rolled SQL rather than an ORM: on a low-spec
/// laptop, EF Core costs tens of MB of working set and hundreds of ms of startup for no benefit here.
/// </summary>
public sealed class Database : IDisposable
{
    readonly SqliteConnection _conn;

    // ONE gate for reads as well as writes. SqliteConnection is not thread-safe, and the gateway
    // touches this store from the connector's event stream, the background loop and the UI thread
    // at the same time. Guarding only writes left reads racing a live transaction, which surfaced as
    // a NullReferenceException deep inside the provider while closing the connection.
    readonly Lock _gate = new();

    public Database(string? path = null)
    {
        var file = path ?? Paths.DatabaseFile;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;");
        Migrate();
    }

    public SqliteConnection Connection => _conn;

    public void Exec(string sql)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    /// <summary>All writes funnel through here, transactionally, so a process never self-collides.</summary>
    public T Write<T>(Func<SqliteConnection, T> body)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            var r = body(_conn);
            tx.Commit();
            return r;
        }
    }

    /// <summary>
    /// All reads funnel through here too. Cheap at this workload — a handful of operations a second —
    /// and it is the only way a single shared connection is safe.
    /// </summary>
    public T Read<T>(Func<SqliteConnection, T> body)
    {
        lock (_gate) return body(_conn);
    }

    public SqliteCommand Cmd(string sql, params (string, object?)[] ps)
    {
        var c = _conn.CreateCommand();
        c.CommandText = sql;
        foreach (var (k, v) in ps) c.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return c;
    }

    void Migrate()
    {
        Exec("CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);");
        var have = ReadInt("SELECT value FROM meta WHERE key='schema_version'") ?? 0;

        if (have < 1)
        {
            Exec("""
            CREATE TABLE IF NOT EXISTS execution_request(
              request_id        TEXT PRIMARY KEY,
              agent_session_id  TEXT,
              connector_id      TEXT NOT NULL,
              account_id        TEXT NOT NULL,
              instrument        TEXT NOT NULL,
              intent            TEXT NOT NULL,
              parameters        TEXT NOT NULL,
              client_order_id   TEXT NOT NULL UNIQUE,
              created_at        TEXT NOT NULL,
              dispatched_at     TEXT,
              execution_state   TEXT NOT NULL,
              connector_order_id TEXT,
              filled_quantity   TEXT NOT NULL DEFAULT '0',
              average_price     TEXT,
              needs_reconciliation INTEGER NOT NULL DEFAULT 0,
              last_reconciled_at TEXT,
              last_error        TEXT,
              mode              TEXT NOT NULL,
              updated_at        TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_er_recon ON execution_request(needs_reconciliation);
            CREATE INDEX IF NOT EXISTS ix_er_state ON execution_request(execution_state);

            CREATE TABLE IF NOT EXISTS activity(
              id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, level TEXT NOT NULL, text TEXT NOT NULL);

            CREATE TABLE IF NOT EXISTS engineering_log(
              id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, component TEXT NOT NULL,
              event TEXT NOT NULL, severity TEXT NOT NULL, session TEXT, correlation_id TEXT,
              request_id TEXT, metadata TEXT, exception TEXT);

            CREATE TABLE IF NOT EXISTS health_event(
              id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, component TEXT NOT NULL,
              state TEXT NOT NULL, detail TEXT);

            CREATE TABLE IF NOT EXISTS runtime_install(
              id TEXT PRIMARY KEY, kind TEXT NOT NULL, version TEXT, path TEXT,
              installed_at TEXT, verified INTEGER NOT NULL DEFAULT 0);

            CREATE TABLE IF NOT EXISTS onboarding(
              step TEXT PRIMARY KEY, completed_at TEXT NOT NULL, detail TEXT);

            CREATE TABLE IF NOT EXISTS kv(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """);
            Exec($"INSERT INTO meta(key,value) VALUES('schema_version','1') ON CONFLICT(key) DO UPDATE SET value='1';");
        }

        if (have < 2)
        {
            // The material ledger. Two tables and they are two different KINDS of knowledge, which is
            // the entire reason this is worth having:
            //
            //   material      — what TradeAgent OBSERVED on disk. Written only by the scanner, from a
            //                   directory listing and a hash. The agent cannot write here at all.
            //   material_note — what somebody CLAIMED about it. The agent says it ran a program or
            //                   derived one file from another; this is where that goes, labelled.
            //
            // Do not merge them, and do not let a note edit a material row. An observation that can be
            // rewritten by the thing it observes is not a record, and the point of the ledger is that
            // in three weeks nobody has to take the agent's word for what is in the workspace.
            //
            // A row is a FILE VERSION, not a file path. Replace inbox/model.onnx with a different build
            // and the old row stays, stamped removed_at, and a new row appears. Provenance that forgets
            // the thing it replaced is not provenance.
            Exec("""
            CREATE TABLE IF NOT EXISTS material(
              id            INTEGER PRIMARY KEY AUTOINCREMENT,
              rel_path      TEXT NOT NULL,
              origin        TEXT NOT NULL,
              sha256        TEXT,
              size_bytes    INTEGER NOT NULL,
              modified_at   TEXT NOT NULL,
              first_seen_at TEXT NOT NULL,
              last_seen_at  TEXT NOT NULL,
              removed_at    TEXT,
              runnable      INTEGER NOT NULL DEFAULT 0
            );
            -- The observation key is the cheap tuple the scanner can read without opening the file.
            -- Hashing every file on every pass is what the low-spec laptop budget forbids, so the
            -- hash is filled in only when this tuple changes. The cost is a blind spot, recorded
            -- rather than hidden: content swapped with size AND mtime both preserved reads as the
            -- same version. Closing that means hashing unconditionally.
            CREATE UNIQUE INDEX IF NOT EXISTS ux_material_seen ON material(rel_path, size_bytes, modified_at);
            CREATE INDEX IF NOT EXISTS ix_material_live ON material(removed_at);
            CREATE INDEX IF NOT EXISTS ix_material_sha ON material(sha256);

            CREATE TABLE IF NOT EXISTS material_note(
              id          INTEGER PRIMARY KEY AUTOINCREMENT,
              at          TEXT NOT NULL,
              author      TEXT NOT NULL,
              session     TEXT,
              kind        TEXT NOT NULL,
              subject_sha TEXT,
              parent_sha  TEXT,
              text        TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_note_subject ON material_note(subject_sha);
            """);
            Exec($"INSERT INTO meta(key,value) VALUES('schema_version','2') ON CONFLICT(key) DO UPDATE SET value='2';");
        }

        if (have < 3)
        {
            // THE COMPOSITE LEDGER — one row per MULTI-TARGET intent, so a replay sends nothing.
            //
            // `execution_request` makes a single mutation idempotent: the caller's request id is the
            // primary key, so a repeated `buy` finds its record and dispatches nothing. A sweep had
            // no such row. `cancel-all` and `close-all` decomposed the request into per-order legs
            // named after a nonce minted FRESH on every call, so an agent that lost the reply and
            // sent the same request id again got a brand-new sweep over whatever was on the book by
            // then — including orders placed after the first one (Codex C2).
            //
            // This is the missing row. `plan` is what the outer id captured and `nonce` is what its
            // legs are named after, both written BEFORE any effect, so a second call with the same
            // request id reuses the SAME leg ids and the per-leg records refuse to dispatch twice.
            // `result` is the answer the first call produced, written after the effects; a replay
            // that finds one hands it back verbatim rather than doing the work again.
            //
            // Deliberately not merged into `execution_request`: that table is one row per thing sent
            // to a broker, and a composite is not one of those. A sweep with three legs has three
            // broker-facing rows and one row here, and conflating them was how "attempted" and
            // "cancelled" came to mean the same number.
            Exec("""
            CREATE TABLE IF NOT EXISTS composite_request(
              request_id        TEXT PRIMARY KEY,
              agent_session_id  TEXT,
              op                TEXT NOT NULL,
              nonce             TEXT NOT NULL,
              plan              TEXT NOT NULL,
              created_at        TEXT NOT NULL,
              result            TEXT,
              completed_at      TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_cr_nonce ON composite_request(nonce);
            """);
            Exec($"INSERT INTO meta(key,value) VALUES('schema_version','3') ON CONFLICT(key) DO UPDATE SET value='3';");
        }

        if (have < 4)
        {
            // A REMOVED ROW IS NEVER UN-REMOVED (REVIEW 2026-09-05b finding 6).
            //
            // The observation key was (rel_path, size_bytes, modified_at), and a sighting that
            // matched it cleared `removed_at`. So a file the ledger had WATCHED GO, replaced by
            // different content of the same length with the mtime restored, brought the old row
            // back — hash and all. `NeedingHash` only picks up rows whose sha256 is null, so it was
            // never re-read: the ledger stated a hash of bytes that were provably not there.
            //
            // The version column is what lets the answer be the one this table's own doc comment
            // already promises — "a file that is replaced leaves its old row behind and gains a new
            // one" — rather than clearing the hash and pretending one row had always been the same
            // file. It does not touch the blind spot the DDL above records (an in-place swap with
            // size AND mtime both preserved); that one is closed only by hashing unconditionally,
            // which the laptop budget forbids. This is the other half, the one the ledger watched.
            Exec("""
            ALTER TABLE material ADD COLUMN version INTEGER NOT NULL DEFAULT 0;
            DROP INDEX IF EXISTS ux_material_seen;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_material_seen ON material(rel_path, size_bytes, modified_at, version);
            """);
            Exec($"INSERT INTO meta(key,value) VALUES('schema_version','4') ON CONFLICT(key) DO UPDATE SET value='4';");
        }

        if (have < 5)
        {
            // THE FILL LEDGER. One row per execution, written by the gateway and by nothing else,
            // never updated and never deleted — the same discipline as `material`, for the same
            // reason: a number computed from rows the observed party can edit is not a measurement.
            //
            // THE KEY IS (account_id, execution_id) BECAUSE THE LEDGER HAS TWO SOURCES ON PURPOSE.
            // The connector raises an execution as it happens, and a pull of GetExecutionsAsync at
            // every (re)connect and every five minutes serves the same executions again. The event
            // is the fast one; the pull is the one that survives a dropped connection, a bridge that
            // raised nothing, and an app that was not running. Both are needed and both report the
            // SAME fill, so without this key the ledger's first act would be to double every figure
            // it exists to produce. `account_id` is in the key because an execution id is the
            // platform's, unique on that platform's account and nowhere else.
            //
            // Decimals as TEXT, exactly as execution_request stores them: a quantity and a price the
            // broker reported must come back out as they went in, not as the nearest double.
            //
            // `fee` NULL is UNKNOWN and 0 is the platform saying zero. `source` records which of the
            // two sources got there first. `agent_session` and `request_id` are resolved when the row
            // is written or never — the request row is what carries the session, and attribution per
            // agent cannot be worked out afterwards from the broker's own ids.
            Exec("""
            CREATE TABLE IF NOT EXISTS fill(
              account_id         TEXT NOT NULL,
              execution_id       TEXT NOT NULL,
              at                 TEXT NOT NULL,
              symbol             TEXT NOT NULL,
              side               TEXT NOT NULL,
              quantity           TEXT NOT NULL,
              price              TEXT NOT NULL,
              connector_order_id TEXT,
              client_order_id    TEXT,
              request_id         TEXT,
              agent_session      TEXT,
              source             TEXT NOT NULL,
              fee                TEXT,
              recorded_at        TEXT NOT NULL,
              PRIMARY KEY(account_id, execution_id)
            );
            CREATE INDEX IF NOT EXISTS ix_fill_at ON fill(at);
            CREATE INDEX IF NOT EXISTS ix_fill_symbol ON fill(symbol, at);
            """);
            Exec($"INSERT INTO meta(key,value) VALUES('schema_version','5') ON CONFLICT(key) DO UPDATE SET value='5';");
        }

        if (have < 6)
        {
            // THE LAUNCH LEDGER. One row per run of the agent CLI, written BEFORE the process starts.
            //
            // What it replaces is a pair of kv counters that were written when a turn FINISHED. That
            // arrangement priced a killed turn at nothing: the child was started, the tokens were
            // consumed and billed by the vendor, and because the app died before it saw the usage
            // event the day's total never moved. An AI that is killed and restarted therefore got its
            // allowance back every time, and the daily ceiling — the only thing bounding an agent
            // that works non-stop — was a check on completed spending rather than a cap. Measured on
            // 2026-09-07: 5.07 USD against a 5 USD ceiling, four turns.
            //
            // So the row is written first and carries `reserved_cost`, an upper bound committed
            // before the launch. `state` is the whole of the mechanism: LAUNCHED means the turn is
            // out there and its reservation is still committed; ENDED means the usage came back and
            // `cost` is what it actually came to; LOST means a meter opened this database while the
            // row was still LAUNCHED — nobody will ever report that turn's usage — and the
            // reservation becomes the cost rather than being released.
            //
            // MEASUREMENT AND CLAIM STAY APART, as they do between `material` and `material_note`:
            // every column here is written by the app, from the CLI's own event stream or from what
            // the app itself put on the command line. The agent has no verb that reaches this table.
            //
            // `input_hash` and `policy_version` are round 4 of docs/COUNCIL.md: which prompt entered
            // a model request, and under which grant policy, cannot be reconstructed afterwards.
            // `effective_model` is NULL when the runtime never named one — which is codex 0.153.4's
            // behaviour even when TradeAgent put -m on the command line — and NULL there is what
            // makes a priced row an estimate rather than a bill.
            Exec("""
            CREATE TABLE IF NOT EXISTS ai_attempt(
              id                     TEXT PRIMARY KEY,
              started_at             TEXT NOT NULL,
              runtime                TEXT,
              requested_model        TEXT,
              pricing_basis          TEXT,
              reserved_cost          TEXT NOT NULL DEFAULT '0',
              state                  TEXT NOT NULL,
              ended_at               TEXT,
              exit_code              INTEGER,
              input_tokens           INTEGER,
              cached_input_tokens    INTEGER,
              cache_write_input_tokens INTEGER,
              output_tokens          INTEGER,
              reasoning_output_tokens INTEGER,
              effective_model        TEXT,
              cost                   TEXT,
              unpriced_reason        TEXT,
              context                TEXT,
              policy_version         TEXT,
              input_hash             TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_attempt_started ON ai_attempt(started_at);
            CREATE INDEX IF NOT EXISTS ix_attempt_state ON ai_attempt(state);
            """);
            Exec($"INSERT INTO meta(key,value) VALUES('schema_version','6') ON CONFLICT(key) DO UPDATE SET value='6';");
        }

        if (have < 7)
        {
            // THE WAKE QUEUE. One row per reason the AI may take a turn, and the loop takes none
            // without one.
            //
            // What it replaces is nothing at all: `AskedForDelay` returned Zero whenever the AI had
            // not written `next.json`, so the loop asked for another turn the instant one ended, for
            // as long as the machine was on. The only thing that ever stopped it was the day's cost
            // ceiling — an agent with nothing to do burned the whole allowance discovering that.
            //
            // `id` IS THE DEDUPLICATION AND IT IS DETERMINISTIC PER SOURCE — `owner:<seq>`,
            // `inbox:<scan-at>`, `fill:<execution_id>`, `order:<request_id>:<state>`,
            // `renewal:<local-day>`, `self:<attempt-id>`, `review:<yyyy-MM-ddTHH:mm>`. A second raise
            // of the same fact is a no-op rather than a second turn, which is what makes a replay of
            // a whole day's events — a reconnect that serves every execution again, a restart that
            // re-reads the same scan — cost nothing. A random id here would have made every retry a
            // paid turn.
            //
            // `consumed_by` IS THE ATTEMPT ID, WRITTEN IN THE SAME TRANSACTION AS THE `ai_attempt`
            // ROW AND BEFORE THE PROCESS STARTS. Consumed after the turn instead, a kill between the
            // launch and the commit would hand the same events to the next launch and charge for
            // both. `disposition` is what became of the wake — see `MissionEventStore`.
            //
            // WRITTEN BY THE APP ONLY, the rule `material` and `ai_attempt` already keep: there is no
            // verb and no pipe op that reaches this table, so an agent cannot manufacture a reason to
            // be paid for another turn, and cannot delete the record of one it was given.
            Exec("""
            CREATE TABLE IF NOT EXISTS mission_event(
              id           TEXT PRIMARY KEY,
              kind         TEXT NOT NULL,
              created_at   TEXT NOT NULL,
              due_at       TEXT NOT NULL,
              payload      TEXT,
              consumed_at  TEXT,
              consumed_by  TEXT,
              disposition  TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_mission_event_due ON mission_event(consumed_at, due_at);
            CREATE INDEX IF NOT EXISTS ix_mission_event_kind ON mission_event(kind);
            """);
            Exec($"INSERT INTO meta(key,value) VALUES('schema_version','7') ON CONFLICT(key) DO UPDATE SET value='7';");
        }

        var found = ReadInt("SELECT value FROM meta WHERE key='schema_version'") ?? 0;
        if (found > Versions.DatabaseSchemaVersion)
            throw new TradeAgentException(ErrorCode.STATE_DATABASE_CORRUPT,
                $"database schema {found} is newer than this build supports ({Versions.DatabaseSchemaVersion})");
    }

    int? ReadInt(string sql)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = sql;
        var o = c.ExecuteScalar();
        return o is null || o is DBNull ? null : int.TryParse(o.ToString(), out var i) ? i : null;
    }

    public string? GetKv(string key) => Read(_ =>
    {
        using var c = Cmd("SELECT value FROM kv WHERE key=$k", ("$k", key));
        return c.ExecuteScalar() as string;
    });

    public void SetKv(string key, string value) => Write(_ =>
    {
        using var c = Cmd("INSERT INTO kv(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v", ("$k", key), ("$v", value));
        return c.ExecuteNonQuery();
    });

    public void Dispose()
    {
        lock (_gate) _conn.Dispose();
    }
}

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

    /// <summary>
    /// How many <see cref="Write{T}"/> calls are on this thread's stack. Only ever touched with
    /// <see cref="_gate"/> held, and the gate is held for the whole of the outermost call, so no
    /// other thread can ever see it above zero.
    /// </summary>
    int _depth;

    /// <summary>
    /// All writes funnel through here, transactionally, so a process never self-collides.
    ///
    /// <para><b>A NESTED CALL JOINS THE TRANSACTION ALREADY OPEN; it does not start a second one.</b>
    /// That is what lets a whole turn's transition — the launch record closed, the artifacts
    /// published, the wakes dispositioned — be ONE commit while each of those stays a store method
    /// that is correct called on its own. Without it the outer call would have to be written in raw
    /// SQL that duplicates three stores, or the turn would land in three commits with two windows in
    /// between: <c>docs/COUNCIL.md</c> rule 6 asks for one committed transition, and a crash in
    /// either window is an attempt that ended with its work unrecorded, or work recorded against an
    /// attempt that never ended.</para>
    ///
    /// <para>An exception out of a nested body still unwinds through the outer one, so the whole
    /// transaction rolls back — which is the point: a partly-applied turn is exactly what this
    /// prevents. A nested body that swallows its own failure has decided that fact is not worth the
    /// commit, and that decision belongs at the call site, not here.</para>
    /// </summary>
    public T Write<T>(Func<SqliteConnection, T> body)
    {
        lock (_gate)
        {
            if (_depth > 0) return body(_conn);

            using var tx = _conn.BeginTransaction();
            _depth++;
            try
            {
                var r = body(_conn);
                tx.Commit();
                return r;
            }
            finally { _depth--; }
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

        if (have < 8)
        {
            // THE DATASET LEDGER. What market data this installation collected, where every byte of
            // it came from, and everything it does NOT claim — written by the app and by nothing
            // else, like `material` and `fill` before it.
            //
            // There is no verb and no pipe op that writes here. The AI reads these rows through
            // `data-list` and reads the bars through `data-bars`, and that asymmetry is the point:
            // an agent that could edit the provenance of its own evidence could report a backtest
            // over twelve clean months of data that was three months with the gaps filled in.
            //
            // TWO TABLES BECAUSE A DATASET HAS TWO SCALES. `dataset` is one accepted normalised file
            // — the thing a backtest runs over. `dataset_file` is one row per RAW ARCHIVE FILE the
            // vendor published, carrying the hash Binance printed in its .CHECKSUM sidecar AND the
            // hash TradeAgent computed off the bytes it kept. Both, not one: the published figure is
            // what the bytes were checked against on the day, and the computed figure is what a
            // later read is checked against, which is how a raw file that CHANGED on disk is told
            // from one that was never right. A dataset whose raw file no longer hashes to its row is
            // REJECTED and is never re-normalised from those bytes.
            //
            // `unit` is per raw file because Binance changed this column from milliseconds to
            // microseconds in January 2025 and a twelve-month collection straddles that. It records
            // what the MAGNITUDE said, not what the month implied.
            Exec("""
            CREATE TABLE IF NOT EXISTS dataset(
              id                   INTEGER PRIMARY KEY AUTOINCREMENT,
              source               TEXT NOT NULL,
              pair                 TEXT NOT NULL,
              interval             TEXT NOT NULL,
              version              TEXT NOT NULL,
              months_attempted     INTEGER NOT NULL,
              months_present       INTEGER NOT NULL,
              months_not_published TEXT NOT NULL,
              normalised_path      TEXT NOT NULL,
              normalised_sha256    TEXT NOT NULL,
              bars                 INTEGER NOT NULL,
              first_bar            TEXT,
              last_bar             TEXT,
              gaps                 INTEGER NOT NULL,
              gap_runs             TEXT NOT NULL,
              gap_runs_truncated   INTEGER NOT NULL,
              duplicates           INTEGER NOT NULL,
              incomplete           INTEGER NOT NULL,
              unreadable           INTEGER NOT NULL,
              accepted_at          TEXT NOT NULL,
              state                TEXT NOT NULL,
              rejected_reason      TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_dataset_pair ON dataset(pair, accepted_at);

            CREATE TABLE IF NOT EXISTS dataset_file(
              dataset_id       INTEGER NOT NULL REFERENCES dataset(id),
              month            TEXT NOT NULL,
              url              TEXT NOT NULL,
              published_sha256 TEXT NOT NULL,
              computed_sha256  TEXT NOT NULL,
              bytes            INTEGER NOT NULL,
              downloaded_at    TEXT NOT NULL,
              unit             TEXT NOT NULL,
              path             TEXT NOT NULL,
              PRIMARY KEY(dataset_id, month)
            );
            """);
            Exec($"INSERT INTO meta(key,value) VALUES('schema_version','8') ON CONFLICT(key) DO UPDATE SET value='8';");
        }

        if (have < 9)
        {
            // THE COUNCIL. Two roles run serially by one app instance, and the app carrying work
            // between them — docs/COUNCIL.md, "The shape" and round 4's access contract.
            //
            // `role` ON THE TWO EXISTING TABLES, additively. Every row written before this migration
            // is NULL and reads as the chair's (CouncilRoles.Or): the single agent this replaces WAS
            // Operations, and reassigning its history to nobody would lose the attribution round 4
            // named as unrecoverable. A column rather than a second table because the fact is about
            // the attempt and the wake themselves — which role was charged, which role was woken —
            // and a fact split across two tables is a fact two queries can disagree about.
            //
            // `publication` IS THE IMMUTABLE ARTIFACT, and its id is the SHA-256 of its content.
            // That is the whole of what makes the relay safe to crash inside: publishing the same
            // text twice is publishing the same row twice, which the primary key refuses, so a
            // restart that re-reads a file already published writes nothing. An id minted from the
            // attempt would make every restart a second publication, a second delivery and a second
            // paid task — the exact defect the unit's property test injects a crash to catch.
            //
            // `content` is stored beside the metadata rather than left in the role's `out/` file,
            // because the file is the AGENT'S and may be overwritten, moved or deleted between the
            // commit and the copy; a delivery that cannot be re-copied from the app's own record is
            // a delivery a crash can lose. It is the recipient-scoped artifact of round 4, and
            // `recipients` and `classification` are what scope it.
            //
            // `delivery` is one row per recipient and carries the two-state life the copy needs:
            // `committed` the moment the transaction lands, `delivered` only once the file is on
            // disk in the recipient's `in/`. Between those two states a restart re-copies; there is
            // no state in which a delivery is claimed and no file exists.
            //
            // WRITTEN BY THE APP ONLY, the rule `material`, `ai_attempt` and `mission_event` already
            // keep. There is no verb and no pipe op that publishes, delivers or creates a task, so
            // neither role can hand itself work, publish in the other's name, or delete the record
            // of what it was asked to do.
            Exec("""
            ALTER TABLE ai_attempt ADD COLUMN role TEXT;
            ALTER TABLE mission_event ADD COLUMN role TEXT;

            CREATE TABLE IF NOT EXISTS publication(
              id             TEXT PRIMARY KEY,
              role           TEXT NOT NULL,
              attempt        TEXT,
              revision       INTEGER NOT NULL,
              kind           TEXT NOT NULL,
              recipients     TEXT NOT NULL,
              classification TEXT NOT NULL,
              created_at     TEXT NOT NULL,
              source         TEXT,
              content        TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_publication_role ON publication(role, revision);

            CREATE TABLE IF NOT EXISTS delivery(
              publication_id TEXT NOT NULL,
              recipient      TEXT NOT NULL,
              state          TEXT NOT NULL,
              created_at     TEXT NOT NULL,
              delivered_at   TEXT,
              PRIMARY KEY(publication_id, recipient)
            );
            CREATE INDEX IF NOT EXISTS ix_delivery_state ON delivery(recipient, state);
            """);
            Exec($"INSERT INTO meta(key,value) VALUES('schema_version','9') ON CONFLICT(key) DO UPDATE SET value='9';");
        }

        if (have < 10)
        {
            // WHAT A DISPOSITION POINTS AT, and the three dispositions that need it.
            //
            // `disposition` said what became of a wake in one closed word, and two of them —
            // `answered`, `failed` — are outcomes of a TURN. The owner's own messages need three the
            // APP reaches without one: `delegated` (the turn that took it published a brief, and the
            // brief's id is here), `blocked` (nothing could take it and this is why) and `superseded`
            // (the same words arrived again before anybody looked, and the later event's id is here).
            // Paying for a turn to record any of those would be spending the owner's money to tell
            // them what the app already knew.
            //
            // A COLUMN RATHER THAN A SUFFIX on the word itself: the disposition is a closed
            // vocabulary a query filters on and the detail is free text a person reads, and one field
            // carrying both makes every later filter a LIKE. Additive and nullable — every row
            // written before this reads as a disposition that points at nothing, which it did.
            Exec("ALTER TABLE mission_event ADD COLUMN disposition_detail TEXT;");
            Exec($"INSERT INTO meta(key,value) VALUES('schema_version','10') ON CONFLICT(key) DO UPDATE SET value='10';");
        }

        if (have < 11)
        {
            // A ROLE'S OWN MEMORY, VERSIONED. `publication` gains no column: a revision of
            // `trading/PLAN.md` is an artifact like any other — content-addressed id, role, attempt,
            // revision number, recipients, classification — and giving the private half its own
            // table would put two answers to "what did this role publish" in two places.
            //
            // What it gains is the index the restore reads. "An invalid publication is rejected and
            // the last valid plan stands" (docs/COUNCIL.md) is a lookup of the newest revision of
            // ONE kind for ONE role, run at the end of every turn; ix_publication_role orders by
            // revision but does not narrow by kind, so without this the newest plan is found by
            // walking every report and brief the role ever published.
            Exec("CREATE INDEX IF NOT EXISTS ix_publication_kind ON publication(role, kind, revision);");
            Exec($"INSERT INTO meta(key,value) VALUES('schema_version','11') ON CONFLICT(key) DO UPDATE SET value='11';");
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

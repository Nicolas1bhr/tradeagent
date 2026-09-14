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

        if (have < 12)
        {
            // THE STRATEGY LEDGER: what a program IS, what running it MEASURED, and every trade that
            // measurement is made of. Written by the app only, like `dataset`, `material` and `fill`,
            // and for the same reason — a record of how a strategy performed is the evidence its
            // author is judged on, and an author who could edit it could report a profitable year
            // that never happened. There is a pipe op that ASKS for a run; there is none that writes,
            // alters or deletes a row here, and the metrics are computed by the app from its own
            // trace rather than taken from anything an agent said.
            //
            // THREE TABLES BECAUSE THERE ARE THREE IDENTITIES. `strategy_version` is a PROGRAM and
            // its id is `StrategyProgram.StrategyId` — SHA-256 over the canonical form, the
            // parameters and the semantic-version manifest — so the same rule set offered twice is
            // one version with one lineage. `strategy_run` is a program over PARTICULAR BYTES UNDER A
            // PARTICULAR EXECUTION MODEL and its id is a hash of exactly that: version id, dataset
            // id, the dataset's normalised sha256, the window, and the declared fees, slippage,
            // quantity increment and initial capital. `strategy_trade` is one closed trade of one
            // run. An id minted from the attempt or the clock anywhere in that chain would make every
            // restart a new version and every accumulated result a result about nothing.
            //
            // `dataset_sha256` IS ON THE RUN as well as on the dataset, deliberately: a dataset row
            // can later be REJECTED because a raw archive changed under it, and this column is what
            // lets every run that fed on those bytes be found afterwards instead of leaving results
            // attached to a dataset id whose contents nobody can identify any more.
            //
            // The money columns are TEXT, as `fill` and `execution_request` store theirs — a decimal
            // must come back out as it went in and not as the nearest double — and NULL in one is an
            // UNKNOWN, never a zero: a run that faulted before its first complete bar has no net
            // result, and a zero there would read as a flat one.
            //
            // Additive: three new tables and one index. An older database gains them empty, which
            // reads correctly as "this installation has measured no strategy yet" — and that is what
            // section 8 of the owner's report then says, rather than nothing.
            Exec("""
            CREATE TABLE IF NOT EXISTS strategy_version(
              id                TEXT PRIMARY KEY,
              source            TEXT NOT NULL,
              canonical         TEXT NOT NULL,
              manifest          TEXT NOT NULL,
              interpreter_build TEXT NOT NULL,
              parse_verdict     TEXT NOT NULL,
              warm_up_bars      INTEGER NOT NULL,
              created_at        TEXT NOT NULL,
              role              TEXT,
              attempt           TEXT
            );

            CREATE TABLE IF NOT EXISTS strategy_run(
              id              TEXT PRIMARY KEY,
              version_id      TEXT NOT NULL REFERENCES strategy_version(id),
              dataset_id      INTEGER NOT NULL REFERENCES dataset(id),
              dataset_sha256  TEXT NOT NULL,
              window_from     TEXT,
              window_to       TEXT,
              execution_model TEXT NOT NULL,
              outcome         TEXT NOT NULL,
              fault_reason    TEXT,
              bars            INTEGER NOT NULL,
              trades          INTEGER NOT NULL,
              wins            INTEGER NOT NULL,
              intents         INTEGER NOT NULL,
              fills           INTEGER NOT NULL,
              exposure_bars   INTEGER NOT NULL,
              missing_minutes INTEGER NOT NULL,
              faults          INTEGER NOT NULL,
              gross_pnl       TEXT,
              fees            TEXT,
              net_pnl         TEXT,
              max_drawdown    TEXT,
              trace_sha256    TEXT NOT NULL,
              created_at      TEXT NOT NULL,
              role            TEXT,
              attempt         TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_strategy_run_version ON strategy_run(version_id, created_at);

            CREATE TABLE IF NOT EXISTS strategy_trade(
              run_id      TEXT NOT NULL REFERENCES strategy_run(id),
              ordinal     INTEGER NOT NULL,
              entry_bar   TEXT NOT NULL,
              entry_price TEXT NOT NULL,
              exit_bar    TEXT NOT NULL,
              exit_price  TEXT NOT NULL,
              quantity    TEXT NOT NULL,
              exit_reason TEXT NOT NULL,
              fees        TEXT NOT NULL,
              pnl         TEXT NOT NULL,
              PRIMARY KEY(run_id, ordinal)
            );
            """);
            Exec($"INSERT INTO meta(key,value) VALUES('schema_version','12') ON CONFLICT(key) DO UPDATE SET value='12';");
        }

        if (have < 13)
        {
            // THE OBSERVED DELIVERIES OF THE APP-OWNED HARNESS. Round 4 of `docs/COUNCIL.md` asks for
            // "identities, input hashes, grants, policy versions and observed deliveries" to be
            // recorded, and names the gap in the same breath: "unrestricted CLI reads remain
            // unobserved". This table is the half that stops being true for a worker on the harness —
            // every read, every write and every gateway call a model asked for, whether it was served
            // or refused, with the attempt it belongs to.
            //
            // WRITTEN BY THE APP ONLY, which is the rule `material`, `fill`, `ai_attempt`,
            // `mission_event` and `publication` already keep. There is no verb and no pipe op that
            // inserts, updates or deletes a row here, so a worker cannot edit the record of what it
            // asked for — the same separation `material` keeps from `material_note`.
            //
            // `argument` IS A SUMMARY AND NEVER THE PAYLOAD. A path, an op, a byte count: enough to
            // answer "what did this attempt reach for" without copying the content of a file or the
            // owner's own words into a second place. `bytes` is what the call RETURNED, which is the
            // quantity the app controls outright (rule 4's retrieval), so a turn's reading is a sum
            // over these rows rather than a number the agent reported about itself.
            //
            // 12 is U-runner-3's (the `backtest` pipe op). This unit takes 13 as its brief assigns,
            // so the two can land in either order without either renumbering.
            Exec("""
            CREATE TABLE IF NOT EXISTS tool_call(
              id       INTEGER PRIMARY KEY AUTOINCREMENT,
              at       TEXT NOT NULL,
              attempt  TEXT,
              role     TEXT,
              tool     TEXT NOT NULL,
              argument TEXT,
              bytes    INTEGER NOT NULL DEFAULT 0,
              served   INTEGER NOT NULL,
              refusal  TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_tool_call_attempt ON tool_call(attempt, at);
            CREATE INDEX IF NOT EXISTS ix_tool_call_role ON tool_call(role, at);
            """);
            Exec($"INSERT INTO meta(key,value) VALUES('schema_version','13') ON CONFLICT(key) DO UPDATE SET value='13';");
        }

        // 13 IS THE RUNG ABOVE, AND IT ARRIVED AFTER THIS ONE WAS WRITTEN. `U-api-worker`'s `tool_call`
        // table was in flight beside this unit, so this rung was written as `if (have < 14)` over a 13
        // that did not exist yet and needed no edit when it landed: the two add only their own columns
        // and tables, so the ladder comes out 11-12-13-14 whichever order the units land in.
        if (have < 14)
        {
            // THE HOLDOUT: A TIME CUTOFF ON A DATASET, NOT A SECOND DATASET.
            //
            // `docs/COUNCIL.md`:131 asks for "holdout data the research process cannot reach" and does
            // not say how; :212 says why the shape had to be decided now rather than later — "a leaked
            // holdout cannot become unseen", so this is the one boundary that cannot be added after the
            // fact. Two columns rather than a second `dataset` row: a cutoff cannot be asked for by id,
            // there is one normalised file to keep hashed, and a split would have needed a second set of
            // provenance that could drift from the first. `holdout_from` is UTC and INCLUSIVE — the bar
            // whose open time equals it is already private — and NULL means this dataset holds nothing
            // back, which is what every row written before this did.
            //
            // `evaluation_class` decides whether a run over these bars is CHARGED against a campaign:
            // `research` is real collected history and `fixture` is bars that exist to prove the
            // plumbing works (`docs/COUNCIL.md`:134, "fixture runs establishing plumbing only"). The
            // default is `research` deliberately — a team that could have its bars read as a fixture
            // would have bought itself unlimited free trials, so the free reading is never the default.
            //
            // Both are written by `DatasetStore.SetHoldout` and by nothing else: the owner presses it in
            // TradeAgent's own window, there is no pipe op and no `trade` verb, and moving a cutoff
            // EARLIER is refused in words. Additive — two columns, one nullable and one with a default —
            // so an older database gains them and every existing dataset keeps being served in full.
            Exec("ALTER TABLE dataset ADD COLUMN holdout_from TEXT;");
            Exec($"ALTER TABLE dataset ADD COLUMN evaluation_class TEXT NOT NULL DEFAULT '{Data.EvaluationClass.Research}';");
            // THE CAMPAIGN: A HOLDOUT, A STANDARD FIXED BEFORE THE WORK, AND A FINITE NUMBER OF ATTEMPTS.
            //
            // `docs/COUNCIL.md`:131-134: "registered submissions charged against a campaign-wide trial
            // budget that survives team replacement, with campaign renewal authorised by code so no new
            // campaign resets holdout access ... the scoring policy fixed before a campaign ... final
            // evaluation scarce because every verdict leaks".
            //
            // THE POLICY TEXT IS COPIED ONTO THE ROW, not referenced. A policy read from a constant at
            // judging time could change between the hypothesis and the verdict, and precommitment — that
            // the criteria were fixed before the outcome was known — is one of the four things :212 names
            // as unrecoverable afterwards. The sha beside it is what binds evidence to the standard.
            //
            // THE BUDGETS ARE COPIED TOO, off the owner's settings at open. A campaign's allowance is what
            // it was opened with, so a setting nudged half way through cannot move the standard under
            // evidence already collected.
            //
            // ONE OPEN CAMPAIGN PER HOLDOUT DATASET, and that is a PARTIAL UNIQUE INDEX rather than a
            // check in C#: two presses racing must not be able to make two. Renewal is the only way a
            // second campaign exists over one dataset — it closes the parent and opens a child carrying
            // `renewed_from`, the parent's holdout and the parent's policy — so the index is what makes
            // "renewal is the only route" true rather than merely intended.
            Exec("""
            CREATE TABLE IF NOT EXISTS strategy_campaign(
              id                    INTEGER PRIMARY KEY AUTOINCREMENT,
              name                  TEXT NOT NULL,
              scoring_policy        TEXT NOT NULL,
              scoring_policy_sha256 TEXT NOT NULL,
              trial_budget          INTEGER NOT NULL,
              verdict_budget        INTEGER NOT NULL,
              holdout_dataset_id    INTEGER NOT NULL REFERENCES dataset(id),
              holdout_from          TEXT NOT NULL,
              opened_at             TEXT NOT NULL,
              renewed_from          INTEGER REFERENCES strategy_campaign(id),
              closed_at             TEXT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_campaign_open_holdout
              ON strategy_campaign(holdout_dataset_id) WHERE closed_at IS NULL;
            """);
            // THE TRIAL: ONE ROW PER REGISTERED RESEARCH RUN, KEYED BY NOTHING AN AGENT CHOOSES.
            //
            // `docs/COUNCIL.md`:131: "registered submissions charged against a campaign-wide trial budget
            // that survives team replacement". The key is (campaign, version, run) and there is
            // DELIBERATELY no role column and no attempt column: a trial keyed by the attempt would make
            // a restart a fresh budget, which is the mutant this table was built against, and a trial
            // keyed by the role would let a replacement team start again. All three parts of the key are
            // content hashes or the app's own id, so the same program over the same bytes under the same
            // execution model is ONE trial however many times it is asked for, and a different window or
            // a different fee is a different trial because it is a different peek at the data.
            //
            // `kind` is the dataset's evaluation class AS IT STOOD when the run was registered, copied
            // rather than joined: a dataset reclassified later must not rewrite what past runs cost.
            // `charged` is the arithmetic that follows from it, written down so that a reader of one row
            // need not know the rule — a `fixture` run establishes plumbing only (:134) and costs nothing.
            //
            // Additive: one table and one index. An older database gains them empty, which reads as "no
            // campaign has been charged anything yet".
            Exec("""
            CREATE TABLE IF NOT EXISTS strategy_trial(
              campaign_id   INTEGER NOT NULL REFERENCES strategy_campaign(id),
              version_id    TEXT NOT NULL REFERENCES strategy_version(id),
              run_id        TEXT NOT NULL REFERENCES strategy_run(id),
              kind          TEXT NOT NULL,
              charged       INTEGER NOT NULL,
              registered_at TEXT NOT NULL,
              PRIMARY KEY(campaign_id, version_id, run_id)
            );
            CREATE INDEX IF NOT EXISTS ix_trial_charged ON strategy_trial(campaign_id, charged);
            """);
            // THE VERDICT, WRITTEN WHEN IT IS ASKED FOR AND NOT WHEN IT IS ANSWERED.
            //
            // `docs/COUNCIL.md`:134: "final evaluation scarce because every verdict leaks". A verdict is
            // computed over the holdout bars, and its answer — even one word of it — tells the research
            // process something about months it was never shown. So the charge is the REQUEST: a row here
            // is written before a single holdout bar is read, because a budget checked after the run has
            // already let the leak happen.
            //
            // Keyed (campaign, version): the same version's verdict is ONE verdict however many times it
            // is asked for, which is what lets a crash between the charge and the computation be
            // recovered rather than paid for twice. There is no role and no attempt here for the reason
            // `strategy_trial` has none.
            //
            // `holdout_from` is the cutoff as it stood when the verdict was charged — a record of what was
            // private when the answer was taken, kept because a cutoff can later move later.
            //
            // The verdict's OUTCOME is not here: the holdout run, the promotion record and the delivery
            // are `U-referee-2` at schema 15. This table is the budget and the precommitment, and it is
            // deliberately a seam rather than a stub that pretends to judge.
            //
            // Additive: one table. An older database gains it empty — no verdict has been asked for.
            Exec("""
            CREATE TABLE IF NOT EXISTS strategy_verdict(
              campaign_id   INTEGER NOT NULL REFERENCES strategy_campaign(id),
              version_id    TEXT NOT NULL REFERENCES strategy_version(id),
              requested_at  TEXT NOT NULL,
              holdout_from  TEXT NOT NULL,
              PRIMARY KEY(campaign_id, version_id)
            );
            """);
            Exec($"INSERT INTO meta(key,value) VALUES('schema_version','14') ON CONFLICT(key) DO UPDATE SET value='14';");
        }

        if (have < 15)
        {
            // THE PROMOTION RECORD: ONE IMMUTABLE ROW, ADDRESSED BY EVERYTHING THE VERDICT RESTED ON.
            //
            // `docs/COUNCIL.md` rule 9: "Promotion needs app-computed evidence bound to code, parameters,
            // dependencies, data, evaluator, execution-and-cost model and scoring-policy versions — a
            // changed assumption invalidates the evidence that rested on it". Before this rung
            // `strategy_version` had no state at all: nothing said a version was promoted, refused or
            // invalidated, and the binding material that rule asks for was scattered across one
            // `strategy_run` row where nobody could compare it with anything.
            //
            // `id` IS THE BINDING. It is the SHA-256 of the nine columns between `version_id` and
            // `holdout_run_id`, in the order `PromotionRow.IdOf` spells them, so a second verdict over
            // identical evidence collides with the first rather than becoming a second record of one
            // judgement. An id minted from the clock is the mutant this table was built against.
            //
            // `verdict` and `reason` are NOT in the hash: they are a function of the nine facts that are
            // — the same run under the same policy cannot come out two ways — and ON CONFLICT DO NOTHING
            // makes the first answer stand.
            //
            // THERE IS NO `invalidated` COLUMN, and that is the point of the rule this table implements.
            // Whether a promotion still holds is computed at READ time (`Promotions.Standing`) from these
            // recorded hashes against the current facts. A column would make a version's truth depend on
            // a sweep having run, and the sweep that did not run is exactly the case the money path must
            // not get wrong.
            //
            // Written by the app alone, like `dataset`, `material`, `strategy_run` and `strategy_trial`:
            // one INSERT with ON CONFLICT DO NOTHING, no UPDATE, no DELETE, no pipe op and no `trade`
            // verb anywhere near it. Additive — one table and one index — and an older database gains it
            // empty, which reads correctly as "nothing has been judged here".
            Exec("""
            CREATE TABLE IF NOT EXISTS strategy_promotion(
              id                     TEXT PRIMARY KEY,
              version_id             TEXT NOT NULL REFERENCES strategy_version(id),
              campaign_id            INTEGER NOT NULL REFERENCES strategy_campaign(id),
              scoring_policy_sha256  TEXT NOT NULL,
              interpreter_build      TEXT NOT NULL,
              holdout_dataset_id     INTEGER NOT NULL REFERENCES dataset(id),
              holdout_dataset_sha256 TEXT NOT NULL,
              execution_model        TEXT NOT NULL,
              evaluator_version      TEXT NOT NULL,
              holdout_run_id         TEXT NOT NULL REFERENCES strategy_run(id),
              verdict                TEXT NOT NULL,
              reason                 TEXT NOT NULL,
              at                     TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_promotion_version ON strategy_promotion(version_id, at);
            """);
            Exec($"INSERT INTO meta(key,value) VALUES('schema_version','15') ON CONFLICT(key) DO UPDATE SET value='15';");
        }

        if (have < 16)
        {
            // THE CONSEQUENTIAL BOUNDARY, AND THE TWO SEALED ASSESSMENTS AT IT.
            //
            // `docs/COUNCIL.md`:59-65: the strongest model is spent only at boundaries the app fixes, and
            // there "both directors submit an assessment before either sees the other's; one bounded
            // challenge, one disposition, a deadline with a predetermined default, and code applies the
            // promotion and allocation policy so neither director can veto an eligible deployment
            // forever ... Deduplicate boundary events by entity and revision, so repeated proposals
            // cannot manufacture senior spend". None of that had any product code at all.
            //
            // `id` IS `kind:entity:revision` (`BoundaryIds.Of`), the same shape every id in
            // `MissionEventIds` has: a function of the FACT and of nothing about the attempt, the clock
            // or the process. That is the whole of the deduplication — the second raise of one proposal
            // collides with the row already here, writes nothing and buys nobody a turn. The UNIQUE
            // index says the key out loud beside it, so a later build that changed how the id is spelled
            // would break on the index rather than quietly open a second boundary over one revision.
            //
            // `default_disposition` IS WRITTEN AT OPEN AND NEVER AFTERWARDS. "A deadline with a
            // predetermined default" is precommitment: a default computed when the deadline expires is a
            // default chosen once the outcome is known. `disposed_by` records who applied it and today
            // there is exactly one possible value — `policy` — because no method, no pipe op and no
            // `trade` verb lets a director dispose a boundary at all.
            //
            // `boundary_submission` is what makes the seal the APP'S and not a director's discretion.
            // PRIMARY KEY(boundary_id, role, kind) refuses a second assessment from the same director
            // over the same boundary in SQL; the partial UNIQUE index refuses a second CHALLENGE over
            // the boundary from EITHER of them. The publication itself stays in `publication`, where
            // every other artifact is — this table records only which boundary it answers.
            //
            // Written by the app alone, like `dataset`, `material`, `mission_event` and
            // `strategy_promotion`: one INSERT with ON CONFLICT DO NOTHING per table, no UPDATE that an
            // agent can reach, no DELETE at all. Additive — two tables and three indexes — and an older
            // database gains them empty, which reads correctly as "no boundary has been opened here".
            Exec("""
            CREATE TABLE IF NOT EXISTS boundary_event(
              id                  TEXT PRIMARY KEY,
              kind                TEXT NOT NULL,
              entity              TEXT NOT NULL,
              revision            INTEGER NOT NULL,
              opened_at           TEXT NOT NULL,
              deadline_at         TEXT NOT NULL,
              default_disposition TEXT NOT NULL,
              evidence            TEXT NOT NULL,
              disposition         TEXT,
              disposed_at         TEXT,
              disposed_by         TEXT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_boundary_key
              ON boundary_event(kind, entity, revision);
            CREATE INDEX IF NOT EXISTS ix_boundary_open
              ON boundary_event(disposition, deadline_at);

            CREATE TABLE IF NOT EXISTS boundary_submission(
              boundary_id    TEXT NOT NULL REFERENCES boundary_event(id),
              role           TEXT NOT NULL,
              kind           TEXT NOT NULL,
              publication_id TEXT NOT NULL REFERENCES publication(id),
              at             TEXT NOT NULL,
              PRIMARY KEY(boundary_id, role, kind)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_boundary_one_challenge
              ON boundary_submission(boundary_id) WHERE kind='challenge';
            """);
            Exec($"INSERT INTO meta(key,value) VALUES('schema_version','16') ON CONFLICT(key) DO UPDATE SET value='16';");
        }

        // 16 IS `U-council-concurrent-2`, WHICH WAS IN FLIGHT BESIDE THIS UNIT. This rung was written
        // as `if (have < 17)` over a 16 that did not exist yet, exactly as 14 was written over a 13 that
        // did not, and landing 16 first cost it nothing but its position in the file: the two add only
        // their own tables and columns, so the ladder comes out 15-16-17 whichever order they land in.
        if (have < 17)
        {
            // THE VENUE, AND IT IS APP-OWNED DATA RATHER THAN SOMETHING AN AGENT FILLS IN.
            //
            // `docs/COUNCIL.md`:145,152 wants bars "with instrument increments" and a size "rounded
            // down to the increment, then the gateway's limits"; :95 wants calendars per connector AND
            // per instrument; :239 wants venue capabilities recorded before unattended real money.
            // Before this rung the word "venue" was in this build's comments and nowhere else, so the
            // increment a run rounded to was a number that arrived on the request and defaulted to 1 —
            // a whole Bitcoin on a pair whose step is 0.00001.
            //
            // THE ROWS ARE THE CATALOGUE'S AND THE MIGRATION SEEDS NOTHING. `VenueCatalog` ships them
            // and `venues.json` overrides them (`docs/DECISIONS.md`:73-78, the `runtimes.json` pattern);
            // `VenueStore.Sync` writes the table from that. A migration that INSERTed the shipped rows
            // would be a vendor fact frozen into a database nobody can correct with a one-line edit,
            // which is the thing that pattern exists to prevent.
            //
            // `verified` is the honest column: 0 means nothing in this build has confirmed these
            // numbers against the venue's own instrument definition. The row is still served — as
            // unverified — and the caller that would have to turn it into a SIZE is the one that
            // refuses. `source` is who said so and `recorded_at` is when, nullable because a file that
            // did not say must read as an unknown rather than as the epoch.
            //
            // There is no fee column and no minimum-notional column. `docs/COUNCIL.md` is silent on a
            // fee table and :155 keeps fees DECLARED per backtest; a fee read out of a catalogue would
            // put a number nobody measured into a result and let it read as a measurement.
            Exec("""
            CREATE TABLE IF NOT EXISTS venue(
              id            TEXT PRIMARY KEY,
              display_name  TEXT NOT NULL,
              calendar_kind TEXT NOT NULL,
              source        TEXT NOT NULL,
              recorded_at   TEXT,
              verified      INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS venue_instrument(
              venue_id           TEXT NOT NULL REFERENCES venue(id),
              symbol             TEXT NOT NULL,
              tick_size          TEXT NOT NULL,
              quantity_increment TEXT NOT NULL,
              source             TEXT NOT NULL,
              recorded_at        TEXT,
              verified           INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY(venue_id, symbol)
            );
            """);

            // THE DATASET NAMES ITS VENUE AND ITS INSTRUMENT, and both are COPIED onto the row rather
            // than joined to the catalogue. A dataset's venue is a fact about the bytes that were
            // collected, and a catalogue edited next month must not be able to rewrite what last
            // month's evidence was collected from — the reading `strategy_trial.kind` already takes.
            // No foreign key for the same reason: a venue removed from `venues.json` must not be able
            // to make an existing dataset unwritable or its row a dangling reference.
            //
            // Nullable, because a row can genuinely have neither: a dataset collected by something that
            // did not record one reads as an unknown and is REFUSED an increment rather than given a
            // default. Every row that exists TODAY was written by the Binance collector, which is what
            // the backfill says — it is a statement about rows this build can account for, not a
            // default applied to whatever arrives later.
            Exec("ALTER TABLE dataset ADD COLUMN venue_id TEXT;");
            Exec("ALTER TABLE dataset ADD COLUMN instrument_symbol TEXT;");
            Exec($"UPDATE dataset SET venue_id='{Data.VenueCatalog.BinanceSpot}' WHERE venue_id IS NULL;");
            Exec("UPDATE dataset SET instrument_symbol=pair WHERE instrument_symbol IS NULL;");

            // WHERE A RUN'S INCREMENT CAME FROM, beside the run it decided. The four declared numbers
            // are already on the row in `execution_model` and are what the run id hashes; this says
            // whether the increment in them was the caller's own or was read out of the catalogue, and
            // which row it was read from. It is deliberately NOT in the hash — see
            // `ExecutionModel.Canonical` — because a run's identity is the model it ran under and not
            // the provenance of how that model was assembled, and folding it in would move every run id
            // this installation has already recorded.
            Exec("ALTER TABLE strategy_run ADD COLUMN increment_source TEXT;");

            Exec($"INSERT INTO meta(key,value) VALUES('schema_version','17') ON CONFLICT(key) DO UPDATE SET value='17';");
        }

        if (have < 18)
        {
            // WHAT THE SOURCE DECLARED, AND WHAT THE NORMALISER COUNTED. Three columns, and the split
            // between them is the point: the first two are a SOURCE's statements about itself and the
            // third is a measurement of the file this build wrote.
            //
            // `docs/COUNCIL.md`:164-172 names a second source — Revolut X public candles, five
            // minutes, a ninety-day target, actual depth recorded, and "a candle WITHOUT volume is
            // midpoint-derived and is flagged as such, never as trade evidence". Before this rung the
            // interval a dataset could be of and the depth a collection could target were constants in
            // `BinanceArchive`, so a row for such a source would have read `1m` over twelve months: a
            // provenance record of a collection that never happened.
            //
            // THERE IS NO `coverage_actual_days` COLUMN. The actual depth is the span between
            // `first_bar` and `last_bar`, which are already on the row, and two columns that can
            // disagree about one fact are two facts — the one nobody re-measures is the one that drifts.
            //
            // `source_carries_volume` defaults to 1 and `midpoint_bars` to 0, and for every row that
            // exists today that is not a default but the truth: all of them were written by the Binance
            // collector, whose archive publishes a traded volume on every kline. `coverage_target_days`
            // is backfilled with twelve months stated in days for the same reason — it is a statement
            // about rows this build can account for, not a value applied to whatever arrives later.
            Exec("ALTER TABLE dataset ADD COLUMN coverage_target_days INTEGER;");
            Exec("ALTER TABLE dataset ADD COLUMN source_carries_volume INTEGER NOT NULL DEFAULT 1;");
            Exec("ALTER TABLE dataset ADD COLUMN midpoint_bars INTEGER NOT NULL DEFAULT 0;");
            Exec($"UPDATE dataset SET coverage_target_days={Data.BinanceCandleSource.TwelveMonthsInDays} "
                 + "WHERE coverage_target_days IS NULL;");

            Exec($"INSERT INTO meta(key,value) VALUES('schema_version','18') ON CONFLICT(key) DO UPDATE SET value='18';");
        }

        if (have < 19)
        {
            // WHAT THE PROGRAM DECLARED ABOUT TIME AT EXECUTION, ON THE VERSION AND ON THE VERDICT.
            //
            // `docs/COUNCIL.md`:96-97, verbatim: "A promoted strategy declares its timeframe, its
            // required data freshness and its maximum decision age, and the runner checks them again
            // when the intent reaches execution". Before this rung `strategy_version` had no timeframe
            // at all — the only age anything on the money path knew was a QUOTE's, 30 seconds of it
            // (`GatewayOptions.MaxQuoteAge`) — so rule 1's freshness gate and :33's "never a late
            // trade" had nothing to read and no implementation.
            //
            // WHOLE SECONDS IN AN INTEGER COLUMN, which is the spelling `StrategyCanonical` hashes:
            // the number in the row and the number inside `strategy_version.id` are one number, and a
            // bound compared against a wall clock on the money path is never re-parsed from text.
            //
            // NULLABLE, AND NOT BACKFILLED, which is the opposite of what the schema 17 and 18 rungs
            // did with `venue_id` and `coverage_target_days`. Those two backfilled a value this build
            // can account for — every existing row WAS collected by the Binance collector at twelve
            // months. Here there is no such fact: the language could not spell a bound when these rows
            // were written, so every one of them declared none, and writing a default in would invent
            // a gate the submitted program never asked for. Null means "declared none" and the
            // dispatcher has nothing to refuse on, which is the truth about those versions.
            //
            // ON THE PROMOTION AS WELL AS ON THE VERSION, because :35 makes a changed assumption
            // invalidate the evidence that rested on it, and the bound a verdict was taken under is
            // one of those assumptions. They are NOT in `PromotionRow.IdOf`'s nine facts: a changed
            // bound is already a different `version_id`, so the tuple binds them once already.
            //
            // Additive — six columns, no table, no index — and an older database gains them null.
            Exec("ALTER TABLE strategy_version ADD COLUMN timeframe INTEGER;");
            Exec("ALTER TABLE strategy_version ADD COLUMN data_freshness INTEGER;");
            Exec("ALTER TABLE strategy_version ADD COLUMN max_decision_age INTEGER;");
            Exec("ALTER TABLE strategy_promotion ADD COLUMN timeframe INTEGER;");
            Exec("ALTER TABLE strategy_promotion ADD COLUMN data_freshness INTEGER;");
            Exec("ALTER TABLE strategy_promotion ADD COLUMN max_decision_age INTEGER;");

            Exec($"INSERT INTO meta(key,value) VALUES('schema_version','19') ON CONFLICT(key) DO UPDATE SET value='19';");
        }

        if (have < 20)
        {
            // THE CAPITAL ALLOCATION: WHAT A PROMOTED VERSION MAY TRADE, AND WHICH ONE CAUSED AN ORDER.
            //
            // `docs/COUNCIL.md`:56-57 puts the capital allocator among the things that are "code and
            // never a role"; :14-15 makes every order pass a code-enforced CAPITAL gate; :32-33 lets
            // only a promoted version execute. Before this rung no line of `src` allocated capital at
            // all — the only thing called an allocation was the AI budget's split between roles — and
            // nothing on the order path read `Promotions.Standing`. A promoted version and a quantity
            // the gateway would allow had nothing binding them.
            //
            // `id` IS THE BINDING, the same way `strategy_promotion.id` is: the SHA-256 of the seven
            // columns between `version_id` and `effective_from`, in the order `AllocationRow.IdOf`
            // spells them. `effective_to`, `reason` and `at` are NOT in it — they are the account of
            // the decision and not the decision — and ON CONFLICT DO NOTHING makes the first row
            // stand. An id minted from the clock is the mutant this table was built against: one
            // decision, two rows, and no way to say which one the gateway was enforcing.
            //
            // THERE IS NO UPDATE AND NO DELETE. A ceiling is lowered or withdrawn by recording a
            // FRESH allocation from a later instant, and `Allocations.StandingFor` answers with the
            // newest row in force. A limit its subject could edit is not a limit, and a capital
            // decision that can be moved after the outcome is known is not a record
            // (`docs/COUNCIL.md`:210-212, provenance and precommitment).
            //
            // THE FOREIGN KEYS ARE THE POINT OF THE TABLE. An allocation must name a version that
            // exists and the promotion that made it eligible; an allocation of capital to nothing is
            // the row this design cannot be allowed to hold. Whether that promotion still STANDS is a
            // read-time question and is deliberately not a column here, for the reason
            // `strategy_promotion` has no `invalidated` column.
            Exec("""
            CREATE TABLE IF NOT EXISTS strategy_allocation(
              id             TEXT PRIMARY KEY,
              version_id     TEXT NOT NULL REFERENCES strategy_version(id),
              promotion_id   TEXT NOT NULL REFERENCES strategy_promotion(id),
              policy_version TEXT NOT NULL,
              max_quantity   TEXT NOT NULL,
              max_notional   TEXT,
              currency       TEXT NOT NULL,
              effective_from TEXT NOT NULL,
              effective_to   TEXT,
              reason         TEXT NOT NULL,
              at             TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_allocation_version
              ON strategy_allocation(version_id, effective_from);
            """);

            // AND WHAT AN ORDER WAS PLACED UNDER, ON THE ORDER'S OWN ROW. Two columns rather than a
            // field inside `parameters`, because `parameters` is a blob: re-deriving the attribution
            // from it would let a rewritten blob re-attribute an order that has already been sent, and
            // :210-211 is exactly about provenance that cannot be reconstructed afterwards.
            //
            // NO FOREIGN KEY, deliberately, and this is the opposite reading from the table above. An
            // execution request is a record of something that may already have reached a broker; a
            // version or an allocation removed from this installation later must not be able to make an
            // existing order row unreadable or its reference dangling. The reading `dataset.venue_id`
            // takes at schema 17, for the same reason.
            //
            // Nullable and NOT backfilled. No order this installation has ever placed named a version —
            // nothing could, until this rung — so every existing row genuinely has none, and a default
            // would attribute a sent order to a decision nobody made.
            Exec("ALTER TABLE execution_request ADD COLUMN strategy_version_id TEXT;");
            Exec("ALTER TABLE execution_request ADD COLUMN allocation_id TEXT;");

            Exec($"INSERT INTO meta(key,value) VALUES('schema_version','20') ON CONFLICT(key) DO UPDATE SET value='20';");
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

    /// <summary>
    /// EVERY KV ROW UNDER ONE KEY PREFIX, in key order — the read a family of keys needs when the
    /// members are not known in advance (the day's closed symbols, which are whichever symbols
    /// breached).
    ///
    /// <para><c>substr</c> rather than <c>LIKE</c>, because a prefix holding <c>%</c> or <c>_</c>
    /// would match rows it does not own under <c>LIKE</c> and the escaping is one more thing to get
    /// wrong; and rather than a range comparison, because that decides the answer by collation.
    /// This table holds tens of rows, so the scan costs nothing worth the ambiguity.</para>
    /// </summary>
    public IReadOnlyList<(string Key, string Value)> KvStartingWith(string prefix) => Read(_ =>
    {
        ArgumentNullException.ThrowIfNull(prefix);
        using var c = Cmd("SELECT key,value FROM kv WHERE substr(key,1,$n)=$p ORDER BY key",
            ("$n", prefix.Length), ("$p", prefix));

        var rows = new List<(string, string)>();
        using var r = c.ExecuteReader();
        while (r.Read()) rows.Add((r.GetString(0), r.GetString(1)));
        return (IReadOnlyList<(string, string)>)rows;
    });

    public void Dispose()
    {
        lock (_gate) _conn.Dispose();
    }
}

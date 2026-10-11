using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradeAgent.Core.Data;
using TradeAgent.Core.Decisions;
using TradeAgent.Core.Perception;

namespace TradeAgent.Core.Db;

/// <summary>
/// What an as-of read served: the observation, or null because nothing had arrived — or, with no observation, why the
/// one it would have served is withheld (<see cref="TapeHoldout"/>). The refusal comes with NO row deliberately: a caller
/// that forgets to look at it is handed nothing, never a reading stamped inside a holdout window.
/// </summary>
public sealed record TapeAsOf(TapeObservation? Row, string? Refusal);

/// <summary>
/// ONE REGISTERED LENS, AS THE TAPE HOLDS IT (<c>U-lenses</c>): its whole canonical definition, the two hashes this store
/// computed, the version it numbered, who proposed it under which attempt, when, and the validator that admitted it.
/// </summary>
public sealed record LensRecord(
    string Id, int Version, string LensSha, string SchemaSha, string Definition, string TargetSource, string TargetSeries,
    string Proposer, string? Attempt, DateTimeOffset RegisteredAt, string ValidatorVersion)
{
    /// <summary>The port's reference for this lens: what a call asked under it carries, hashed from its questions.</summary>
    public SchemaRef Schema => new(Id, Version, SchemaSha);

    /// <summary>
    /// THE DEFINITION, PARSED BY THIS BUILD — or the refusal in words. A row another validator admitted
    /// (<see cref="ValidatorVersion"/> is not <see cref="Lens.ValidatorVersion"/>) is REFUSED, naming both versions, and
    /// never read under this build's rules or built into a state by this build's builder: refuse, never guess.
    /// </summary>
    public LensParse Parse()
    {
        if (!string.Equals(ValidatorVersion, Lens.ValidatorVersion, StringComparison.Ordinal))
            return LensParse.No($"the lens '{Id}' version {Version} was admitted by validator {ValidatorVersion}, and this build's is "
                                + $"{Lens.ValidatorVersion}; a lens is read only under the rules and the state builder that admitted it, "
                                + $"so this build reads it as nothing — a build of {ValidatorVersion} reads it, or a lens proposed to this "
                                + "build under another id takes its place");
        var parsed = Lens.Parse(Definition);
        return parsed.Ok
            ? parsed
            : LensParse.No($"the lens '{Id}' version {Version} is not one this build's validator ({Lens.ValidatorVersion}) admits: {parsed.Why}");
    }

    /// <summary>The definition, parsed — or null, for the reason <see cref="Parse"/> gives in words.</summary>
    public Lens? Parsed => Parse().Lens;
}

/// <summary>
/// WHAT ONE PROPOSAL CAME TO: the lens it is — <see cref="New"/> when this proposal wrote it, false when the same bytes
/// were already registered, and the first registration stands — or, with no lens, why it was refused, in words.
/// </summary>
public sealed record LensRegistration(LensRecord? Lens, bool New, string? Refusal);

/// <summary>
/// THE MARKET-CONTEXT TAPE: THE ONLY WRITER OF <c>state/tape.db</c>, now and for every later rung of
/// it (<c>U-tape-store</c>; <c>docs/EDGE-FACTORY.md</c> § 4.1).
///
/// <para><b>Its own file, its own connection, its own ladder.</b> WAL, <c>synchronous=FULL</c> and a
/// five-second busy timeout, exactly as <c>PaperBook</c> opens its book — with two differences that
/// are the reason this is not a copy of it. (1) THE VERSION IS CHECKED BEFORE ANYTHING IS MIGRATED.
/// <c>PaperBook</c> creates its tables and then reads the version, so a book written by a newer build
/// has this build's tables added to it before it is refused; here a newer tape is refused with the
/// file exactly as it was found. (2) The layout is an <c>if (have &lt; N)</c> ladder from version 1,
/// because <c>U-decision-port</c> adds rung 2, and it adds it HERE, through this type — a second
/// writer of this file is a second definition of what it holds.</para>
///
/// <para><b>Five properties, and the whole type exists to keep them.</b></para>
/// <list type="number">
/// <item><b>Every attempt is a row</b> in <c>tape_fetch</c>, succeeded or failed, with the URL asked
/// for, the ORIGIN this store computed from it, both instants, the status and the reason in words — and
/// every observation names the fetch it came in (a foreign key, enforced). A datum whose fetch cannot be
/// named is a datum with no provenance at all.</item>
/// <item><b>The first reading stands.</b> A re-reading whose payload matches the latest revision writes
/// NOTHING — it is counted on the fetch: <c>items</c> is what the answer carried and the rows naming
/// the fetch are what was new. A re-reading that DIFFERS is a new row, <c>revision + 1</c>, with its own
/// arrival instant. Never <c>INSERT OR REPLACE</c> and never an <c>UPDATE</c>: a ledger whose past its
/// vendor can edit is not a record of what was known when.</item>
/// <item><b>Three times per datum</b>: the vendor's source time, the instant it arrived, and which
/// revision it is. <see cref="AsOf"/> answers "what had arrived by t" and nothing later.</item>
/// <item><b>One transaction per fetch</b>: the attempt and everything it brought commit together or not
/// at all. Anything this store refuses — a payload over 64 KB, one that is not JSON, a subject that
/// could not be part of a key — is refused BEFORE the transaction opens, so a refusal never leaves an
/// attempt recorded with half its items.</item>
/// <item><b>The evidence class is computed here</b>, per observation, from recorded fields
/// (<see cref="TapeClass"/>). No caller passes one.</item>
/// </list>
///
/// <para><b>Every read is screened</b> (<c>U-tape-events</c>): each observation read carries
/// <see cref="TapeScreen"/>'s verdict, computed then and stored nowhere, and <see cref="AsOf"/> withholds the
/// payload of one it quarantines. A quarantined item is still written like any other — the record is what
/// the vendor published.</para>
///
/// <para><b>It is app-owned.</b> There is no verb and no pipe op that writes here but ONE, and it is bounded: the
/// collector in the app's own process is the only caller of <see cref="Append"/>, the port the only caller of
/// <see cref="RecordDecision"/>, and the one exception is <c>lens-propose</c> (<c>U-lenses</c>), which reaches
/// <see cref="RegisterLens"/> through a delegate the composition root hands the gateway — never this store — and can
/// write one thing: a <c>lens</c> row, insert-only, parsed, numbered and hashed HERE, at most
/// <see cref="MaxLensProposalsPerDay"/> a proposer an owner day, under the role the grant proved. It cannot write an
/// observation, a fetch or a call, edit or delete any row, or register a lens for another role. Until containment an
/// agent running unconfined could still edit the FILE — <c>docs/CONTRACTS.md</c> "The tape" says so and does not
/// claim otherwise.</para>
///
/// <para><b>Rung 2 adds the decision models' answers</b> (<c>U-decision-port</c>; <c>docs/EDGE-FACTORY.md</c> § 4.2:
/// "every answer is a recorded measurement"). <c>decision_call</c> holds one row per call that was SENT, keyed by
/// the launch-ledger row it was reserved on, written by <see cref="RecordDecision"/> before that row is settled —
/// who was asked and who answered, the schema's and the state's hashes (never the state), the instants, the latency,
/// the tokens, the estimated and the billed cost apart, the status and the full answers. Two of its fields are
/// computed HERE and taken from no caller, as the evidence class is: the ORIGIN, off the URL, and UNPINNED, off this
/// build's own pin for the instrument (<see cref="DecisionInstruments.IsPinned"/>). One insert, never an update: a
/// second record for one reservation is refused by its key.</para>
///
/// <para><b>Rung 3 adds the lenses</b> (<c>U-lenses</c>; <c>docs/EDGE-FACTORY.md</c> § 4.2): <c>lens</c> holds every
/// question schema a call may be asked under, each its whole canonical definition, its own hash and the port's hash of
/// its questions, the version THIS store numbered, who proposed it under which attempt, when, and the validator that
/// admitted it. Insert only — UNIQUE(id, version) and UNIQUE(lens_sha), never an update or a delete — so a recorded
/// answer resolves to exactly what was asked, about which item, for as long as the tape exists. This build's built-ins
/// are registered at open under <see cref="BuiltInLenses.Proposer"/>, and one whose bytes differ from the tape's row
/// of its id and version is REFUSED, the file untouched.</para>
/// </summary>
public sealed class TapeStore : IDisposable
{
    /// <summary>The version of the layout below, written into the file it describes.</summary>
    public const int Schema = 3;

    /// <summary>The most lenses one proposer may register in one owner day. Built-ins are not counted.</summary>
    public const int MaxLensProposalsPerDay = 8;

    /// <summary>The most one observation's canonical payload may hold, in UTF-8 bytes.</summary>
    public const int MaxPayloadBytes = 64 * 1024;

    /// <summary>The columns <see cref="Obs"/> maps, in its order. Shared with <see cref="TapeReader"/>, which maps them the same way.</summary>
    internal const string ObsCols =
        "id, source, series, subject, source_time, received_at, fetch_id, natural_key, revision, "
        + "payload_sha256, payload, evidence_class";

    const string FetchCols =
        "id, source, series, url, origin, requested_at, received_at, http_status, items, body_sha256, note";

    // ONE GATE FOR READS AS WELL AS WRITES, for the reason `Database` has one: a SqliteConnection is
    // not thread-safe, and one collector loop per catalogue row writes here at the same moment.
    readonly Lock _gate = new();
    readonly SqliteConnection _conn;

    /// <summary>The file this store holds open.</summary>
    public string File { get; }

    /// <summary>
    /// Opens the tape, creating it if it is not there, and refuses one written by a newer build.
    ///
    /// <para>The order is the point: the busy timeout (a connection setting, nothing written), then the
    /// version READ, then — only for a file this build understands — the journal mode, which is
    /// written into the file, and the ladder. A newer tape is left byte-for-byte as it was found.</para>
    /// </summary>
    /// <param name="file">The file to open. Null is <see cref="Paths.TapeFile"/>; a test passes its own.</param>
    /// <param name="builtIns">The lenses this build ships. Null is <see cref="BuiltInLenses.All"/>; a test passes its own.</param>
    public TapeStore(string? file = null, IReadOnlyList<BuiltInLens>? builtIns = null)
    {
        File = file ?? Paths.TapeFile;
        var dir = Path.GetDirectoryName(File);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = File,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());

        try
        {
            _conn.Open();
            Exec("PRAGMA busy_timeout=5000;");

            var have = FoundVersion();
            if (have > Schema)
                throw new InvalidOperationException(
                    $"the market-context tape at {File} was written by a newer TradeAgent (its layout is "
                    + $"version {have}, this build understands {Schema}). Nothing has been read from it or "
                    + "written to it, and a newer TradeAgent will open it exactly as it was left.");

            Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;");
            Migrate(have);
            RegisterBuiltIns(builtIns ?? BuiltInLenses.All());
        }
        catch (Exception)
        {
            _conn.Dispose();
            throw;
        }
    }

    /// <summary>
    /// OPENS THE TAPE FOR THE APP, OR SAYS IN THE ACTIVITY LOG WHY IT WILL NOT — the
    /// <c>AppHost</c> pattern for a collector that cannot start: the owner is told in words, the app
    /// comes up, and the rest of TradeAgent runs without the tape. Null means refused.
    /// </summary>
    public static TapeStore? TryOpen(string file, LogStore log)
    {
        ArgumentNullException.ThrowIfNull(log);
        try { return new TapeStore(file); }
        catch (Exception ex)
        {
            log.Activity("TradeAgent is not recording market context: " + ex.Message.ReplaceLineEndings(" "),
                "warn");
            return null;
        }
    }

    /// <summary>
    /// THE VERSION THE FILE NAMES, READ WITHOUT WRITING ANYTHING. Zero for a file that holds no tape
    /// yet — a new file, or a rung-one write that never reached its version row.
    /// </summary>
    int FoundVersion()
    {
        using (var exists = Cmd("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='tape_meta'"))
            if (Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) return 0;

        using var c = Cmd("SELECT value FROM tape_meta WHERE key='schema'");
        var value = c.ExecuteScalar();
        if (value is null or DBNull) return 0;

        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return n;

        throw new InvalidOperationException(
            $"the market-context tape at {File} names a layout version TradeAgent cannot read ('{text}'). "
            + "Nothing has been read from it or written to it.");
    }

    /// <summary>
    /// THE LADDER. Three rungs today — the tape itself, the decision models' answers (<c>U-decision-port</c>) and the
    /// lenses they are asked under (<c>U-lenses</c>); every
    /// later one is an <c>if (have &lt; N)</c> below them, written by the unit that needs it and committed in one
    /// transaction with its own version row, so a crash mid-rung re-runs the rung rather than leaving a file that claims
    /// a layout it does not have.
    /// </summary>
    void Migrate(int have)
    {
        if (have < 1)
            Rung("""
                CREATE TABLE IF NOT EXISTS tape_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);

                CREATE TABLE IF NOT EXISTS tape_fetch(
                  id           INTEGER PRIMARY KEY AUTOINCREMENT,
                  source       TEXT NOT NULL,
                  series       TEXT NOT NULL,
                  url          TEXT NOT NULL,
                  origin       TEXT NULL,
                  requested_at TEXT NOT NULL,
                  received_at  TEXT NOT NULL,
                  http_status  INTEGER NULL,
                  items        INTEGER NOT NULL CHECK (items >= 0),
                  body_sha256  TEXT NULL,
                  note         TEXT NULL);
                CREATE INDEX IF NOT EXISTS ix_tape_fetch_series ON tape_fetch(source, series, id);

                CREATE TABLE IF NOT EXISTS tape_obs(
                  id             INTEGER PRIMARY KEY AUTOINCREMENT,
                  source         TEXT NOT NULL,
                  series         TEXT NOT NULL,
                  subject        TEXT NOT NULL,
                  source_time    TEXT NOT NULL,
                  received_at    TEXT NOT NULL,
                  fetch_id       INTEGER NOT NULL REFERENCES tape_fetch(id),
                  natural_key    TEXT NOT NULL,
                  revision       INTEGER NOT NULL CHECK (revision >= 1),
                  payload_sha256 TEXT NOT NULL,
                  payload        TEXT NOT NULL CHECK (length(CAST(payload AS BLOB)) <= 65536),
                  evidence_class TEXT NOT NULL CHECK (evidence_class IN ('O-LIVE','O-PIT','O-ARCH','O-HIND')),
                  UNIQUE (source, series, natural_key, revision));
                CREATE INDEX IF NOT EXISTS ix_tape_obs_asof ON tape_obs(source, series, subject, source_time);
                CREATE INDEX IF NOT EXISTS ix_tape_obs_fetch ON tape_obs(fetch_id);

                INSERT INTO tape_meta(key, value) VALUES('schema', '1')
                  ON CONFLICT(key) DO UPDATE SET value='1';
                """);

        if (have < 2)
            Rung("""
                CREATE TABLE IF NOT EXISTS decision_call(
                  attempt_id       TEXT PRIMARY KEY,
                  instrument       TEXT NOT NULL,
                  url              TEXT NOT NULL,
                  origin           TEXT NULL,
                  requested_model  TEXT NOT NULL,
                  answered_model   TEXT NULL,
                  pin              TEXT NULL,
                  unpinned         INTEGER NOT NULL CHECK (unpinned IN (0, 1)),
                  host_response_id TEXT NULL,
                  schema_id        TEXT NOT NULL,
                  schema_version   INTEGER NOT NULL CHECK (schema_version >= 1),
                  schema_sha256    TEXT NOT NULL,
                  state_sha256     TEXT NOT NULL,
                  sources          TEXT NOT NULL,
                  requested_at     TEXT NOT NULL,
                  ended_at         TEXT NOT NULL,
                  latency_ms       INTEGER NULL,
                  http_status      INTEGER NULL,
                  status           TEXT NOT NULL CHECK (status IN ('ANSWERED','FAILED','UNANSWERED')),
                  error_class      TEXT NULL,
                  input_tokens     INTEGER NULL,
                  output_tokens    INTEGER NULL,
                  cost_estimated   TEXT NULL,
                  cost_billed      TEXT NULL,
                  price_basis      TEXT NULL,
                  answers          TEXT NULL,
                  CHECK (status <> 'ANSWERED' OR answers IS NOT NULL));
                CREATE INDEX IF NOT EXISTS ix_decision_call_instrument ON decision_call(instrument, requested_at);

                INSERT INTO tape_meta(key, value) VALUES('schema', '2')
                  ON CONFLICT(key) DO UPDATE SET value='2';
                """);

        if (have < 3)
            Rung("""
                CREATE TABLE IF NOT EXISTS lens(
                  id                TEXT NOT NULL,
                  version           INTEGER NOT NULL CHECK (version >= 1),
                  lens_sha          TEXT NOT NULL,
                  schema_sha        TEXT NOT NULL,
                  definition        TEXT NOT NULL,
                  target_source     TEXT NOT NULL,
                  target_series     TEXT NOT NULL,
                  proposer          TEXT NOT NULL,
                  attempt           TEXT NULL,
                  registered_at     TEXT NOT NULL,
                  validator_version TEXT NOT NULL,
                  UNIQUE (id, version),
                  UNIQUE (lens_sha));
                CREATE INDEX IF NOT EXISTS ix_lens_proposer ON lens(proposer, registered_at);

                INSERT INTO tape_meta(key, value) VALUES('schema', '3')
                  ON CONFLICT(key) DO UPDATE SET value='3';
                """);
    }

    void Rung(string sql) => Write(() =>
    {
        Exec(sql);
        return 0;
    });

    /// <summary>The layout version this file now carries.</summary>
    public int Version => Read(FoundVersion);

    // ------------------------------------------------------------------------------- the one write

    /// <summary>
    /// WRITES ONE ATTEMPT AND WHAT OF IT IS NEW, in one transaction.
    ///
    /// <para>The fetch row goes in first because the observations reference it. Then each item: its
    /// natural key, the latest revision already held under that key, and ONE insert. <paramref name="items"/>
    /// is what the body PARSED to; a caller whose body did not parse passes none and puts the reason on
    /// <see cref="TapeFetch.Note"/> — that is a recorded failure, not an empty success.</para>
    /// </summary>
    public TapeAppend Append(TapeFetch fetch, IReadOnlyList<TapeItem>? items = null)
    {
        CheckFetch(fetch);

        // EVERYTHING REFUSED IS REFUSED HERE, BEFORE THE TRANSACTION: see the type summary.
        var prepared = (items ?? []).Select(Prepare).ToList();

        // THE ORIGIN IS READ OFF THE URL, NOT TAKEN FROM THE CALLER. One URL, one origin: the address a
        // row was fetched from and the address its class is decided by cannot be two claims.
        var origin = UrlOrigin.Of(fetch.Url);

        return Write(() =>
        {
            var fetchId = InsertFetch(fetch, origin, prepared.Count);

            var tally = new Tally();
            foreach (var item in prepared)
                tally.Count(Insert(fetch.Source, fetch.Series, fetch.ReceivedAt, fetchId, item,
                    previous => ClassOf(fetch.Source, origin, item.SourceTime, fetch.ReceivedAt, previous)));

            return tally.Result(fetchId, prepared.Count);
        });
    }

    /// <summary>
    /// WRITES ONE FILE OF A VENDOR'S CHECKSUMMED ARCHIVE — the attempt, the file's own record and its kept rows — in
    /// one transaction (<c>U-tape-archive</c>).
    ///
    /// <para><b>The record</b> is an observation like any other: series <see cref="TapeArchiveBatch.RecordSeries"/>,
    /// subject <see cref="TapeArchiveBatch.RecordSubject"/>, source time the label, payload the file's address, size,
    /// published and computed MD5, SHA-256, Last-Modified, rows, the kept count and kept bytes THIS STORE counted, and
    /// the filter. Its arrival instant is its own <c>received_at</c>. So the first reading of a file stands as proof
    /// of what was read when, without the file's bytes; the same file read again is the same record and writes
    /// nothing, and a file that differs — a silent rebuild — is <c>revision + 1</c> beside the first.</para>
    ///
    /// <para><b>Every row is classed here and only here</b>, the record's and the kept rows' alike, by
    /// <see cref="ArchiveClassOf"/>; and everything refused is refused before the transaction opens: a malformed
    /// record, a row not at its file's label, a row named twice, and whatever <see cref="Append"/> refuses. The
    /// published and computed MD5 are recorded as given and may differ — what a mismatch costs is the class; the
    /// recorder writes no rows for one at all.</para>
    /// </summary>
    public TapeAppend AppendArchive(TapeFetch fetch, TapeArchiveBatch batch, IReadOnlyList<TapeItem> items)
    {
        CheckFetch(fetch);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(items);

        // EVERYTHING REFUSED IS REFUSED HERE, BEFORE THE TRANSACTION — the record's facts as well as its rows.
        if (string.IsNullOrWhiteSpace(batch.RecordSeries) || string.IsNullOrWhiteSpace(batch.ItemsSeries)
            || string.Equals(batch.RecordSeries, batch.ItemsSeries, StringComparison.Ordinal))
            throw new ArgumentException("an archive file names two different series, its record's and its rows'", nameof(batch));
        if (!IsHex(batch.PublishedMd5, 32) || !IsHex(batch.ComputedMd5, 32))
            throw new ArgumentException("an archive file's published and computed MD5 are 32 hex digits each", nameof(batch));
        if (!IsHex(batch.Sha256, 64))
            throw new ArgumentException("an archive file's SHA-256 is 64 hex digits", nameof(batch));
        if (batch.Bytes < 0 || batch.Rows < items.Count)
            throw new ArgumentException($"an archive file of {batch.Bytes} bytes and {batch.Rows} rows cannot have kept {items.Count}", nameof(batch));
        if (string.IsNullOrWhiteSpace(batch.Filter))
            throw new ArgumentException("an archive file names the filter that kept its rows", nameof(batch));

        var prepared = items.Select(Prepare).ToList();
        if (prepared.FirstOrDefault(p => p.SourceTime != batch.Label) is { Subject: not null } off)
            throw new ArgumentException($"the row '{off.Subject}' is not at its file's label {batch.Label:O}", nameof(items));
        if (prepared.GroupBy(p => p.Key, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new ArgumentException($"an archive file names the row '{twice.Key}' twice", nameof(items));

        var keptBytes = prepared.Sum(p => (long)Encoding.UTF8.GetByteCount(p.Payload));
        var record = Prepare(new TapeItem(batch.RecordSubject, batch.Label, RecordPayload(fetch.Url, batch, prepared.Count, keptBytes)));

        var origin = UrlOrigin.Of(fetch.Url);
        var verified = string.Equals(batch.PublishedMd5, batch.ComputedMd5, StringComparison.OrdinalIgnoreCase);
        string ClassFor(string? previous) =>
            ArchiveClassOf(fetch.Source, origin, batch.Label, fetch.ReceivedAt, verified, batch.LastModified, previous);

        return Write(() =>
        {
            var fetchId = InsertFetch(fetch, origin, prepared.Count + 1);

            var tally = new Tally();
            tally.Count(Insert(fetch.Source, batch.RecordSeries, fetch.ReceivedAt, fetchId, record, ClassFor));
            foreach (var item in prepared)
                tally.Count(Insert(fetch.Source, batch.ItemsSeries, fetch.ReceivedAt, fetchId, item, ClassFor));

            return tally.Result(fetchId, prepared.Count + 1);
        });
    }

    static void CheckFetch(TapeFetch fetch)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        if (string.IsNullOrWhiteSpace(fetch.Source)) throw new ArgumentException("a fetch names its source", nameof(fetch));
        if (string.IsNullOrWhiteSpace(fetch.Series)) throw new ArgumentException("a fetch names its series", nameof(fetch));
        if (string.IsNullOrWhiteSpace(fetch.Url)) throw new ArgumentException("a fetch names the URL it asked", nameof(fetch));
    }

    /// <summary>
    /// ONE OBSERVATION, AIMED AT ITS SLOT: its natural key, the latest revision already held under that key, and ONE
    /// insert. True when it wrote.
    ///
    /// <para><b>THE INSERT IS WHAT REFUSES, NOT A BRANCH ABOVE IT.</b> A re-reading whose payload matches the latest
    /// revision is AIMED AT THAT REVISION'S OWN SLOT — (source, series, natural_key, revision) — so it is the conflict
    /// clause that writes nothing. A check-then-skip cannot state that nothing was overwritten, which
    /// <c>ForwardBarStore</c> measured: an earlier draft there skipped on a read and left <c>INSERT OR REPLACE</c>
    /// passing every test because the replacing statement was never reached. Here <c>INSERT OR REPLACE</c> would move
    /// the first reading's arrival instant and fetch onto the re-reading — an edited past — and the tests watch for
    /// exactly that.</para>
    /// </summary>
    (bool Wrote, int Revision) Insert(string source, string series, DateTimeOffset receivedAt, long fetchId, Prepared item,
        Func<string?, string> classOf)
    {
        var latest = Latest(source, series, item.Key);
        var revision = latest is not { } held ? 1
            : held.Sha == item.Sha ? held.Revision
            : held.Revision + 1;

        var evidence = classOf(latest?.Class);

        using var c = Cmd("""
            INSERT INTO tape_obs(source, series, subject, source_time, received_at, fetch_id,
                                 natural_key, revision, payload_sha256, payload, evidence_class)
            VALUES($src,$ser,$subj,$st,$recv,$fetch,$key,$rev,$sha,$payload,$class)
            ON CONFLICT(source, series, natural_key, revision) DO NOTHING
            """,
            ("$src", source), ("$ser", series), ("$subj", item.Subject),
            ("$st", Sql.T(item.SourceTime)), ("$recv", Sql.T(receivedAt)), ("$fetch", fetchId),
            ("$key", item.Key), ("$rev", revision), ("$sha", item.Sha), ("$payload", item.Payload),
            ("$class", evidence));

        return (c.ExecuteNonQuery() == 1, revision);
    }

    /// <summary>What one write's inserts came to: new rows, of them revisions, and re-readings that wrote nothing.</summary>
    sealed class Tally
    {
        int _stored, _revised, _unchanged;

        public void Count((bool Wrote, int Revision) insert)
        {
            if (!insert.Wrote) { _unchanged++; return; }
            _stored++;
            if (insert.Revision > 1) _revised++;
        }

        public TapeAppend Result(long fetchId, int items) => new(fetchId, items, _stored, _revised, _unchanged);
    }

    /// <summary>
    /// THE ARCHIVE FILE'S RECORD, as this store writes it: what was read and from where, its hashes, and what was kept
    /// of it — counted here. No arrival instant: that is the row's own <c>received_at</c>, so a second reading of the
    /// same file is the same payload.
    /// </summary>
    static string RecordPayload(string url, TapeArchiveBatch b, int kept, long keptBytes)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("url", url);
            w.WriteNumber("bytes", b.Bytes);
            w.WriteString("md5Published", b.PublishedMd5.ToLowerInvariant());
            w.WriteString("md5Computed", b.ComputedMd5.ToLowerInvariant());
            w.WriteString("sha256", b.Sha256.ToLowerInvariant());
            if (b.LastModified is { } written) w.WriteString("lastModified", Sql.T(written));
            else w.WriteNull("lastModified");
            w.WriteNumber("rows", b.Rows);
            w.WriteNumber("kept", kept);
            w.WriteNumber("keptBytes", keptBytes);
            w.WriteString("filter", b.Filter);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    static bool IsHex(string? s, int length) =>
        s is not null && s.Length == length && !s.AsSpan().ContainsAnyExcept("0123456789abcdefABCDEF");

    /// <summary>
    /// THE EVIDENCE CLASS OF ONE NEW ROW, from fields this store recorded and from THIS BUILD'S rows —
    /// never from a caller, never from <c>tape-sources.json</c>.
    ///
    /// <para><c>O-LIVE</c> iff all three: the fetch's source is a BUILT-IN row
    /// (<see cref="TapeSourceCatalog.BuiltInLiveRule"/>); the fetch's origin — read off its own URL — is
    /// that row's built-in origin; and this reading arrived no later than the row's cadence plus its
    /// documented publication delay plus <see cref="TapeSourceCatalog.LiveTolerance"/> after its source
    /// time. Everything else is <c>O-ARCH</c>: a late reading, one fetched from any other origin (a test's
    /// loopback listener included), and every row a file added, whatever address it names.</para>
    ///
    /// <para><b>The delay is the vendor's word for how late its answer may be</b> — OKX documents that its
    /// announcements may be served about five minutes after their first publication, so its window is
    /// 60 + 300 + 30 s, and a market row's delay is zero, which leaves it exactly as it was. It is LATENESS
    /// only: a source time AHEAD of this machine's clock still has the cadence plus 30 s and no more — a
    /// clock a second behind the vendor's must not make every on-time reading archive, and a source time
    /// further ahead than that is not live either, delay or none.</para>
    ///
    /// <para><b>A later revision is never above the one before it</b>: a datum first read late, or from
    /// elsewhere, does not become live because the vendor re-published it on time. A live datum's late
    /// revision is archive.</para>
    /// </summary>
    static string ClassOf(string source, string? origin, DateTimeOffset sourceTime, DateTimeOffset receivedAt,
        string? previous)
    {
        var own = TapeSourceCatalog.BuiltInLiveRule(source) is { } rule
                  && string.Equals(origin, rule.Origin, StringComparison.Ordinal)
                  && InWindow(receivedAt - sourceTime, rule.Cadence, rule.Delay)
            ? TapeClass.Live
            : TapeClass.Arch;

        return previous is null ? own : TapeClass.Lower(own, previous);
    }

    /// <summary>
    /// Whether a reading that arrived <paramref name="late"/> after its source time (negative: before it) is
    /// within the live window: late by at most the cadence, the delay and the tolerance; early by at most the
    /// cadence and the tolerance.
    /// </summary>
    static bool InWindow(TimeSpan late, TimeSpan cadence, TimeSpan delay) =>
        late >= TimeSpan.Zero
            ? late <= cadence + delay + TapeSourceCatalog.LiveTolerance
            : -late <= cadence + TapeSourceCatalog.LiveTolerance;

    /// <summary>
    /// THE EVIDENCE CLASS OF ONE ROW OF AN ARCHIVE FILE — its record's and every kept row's alike — from fields this
    /// store recorded and from THIS BUILD'S rows (<c>U-tape-archive</c>; R07 § 5.1, "a vendor first-seen timestamp
    /// with a declared basis").
    ///
    /// <para><c>O-LIVE</c> by <see cref="ClassOf"/>'s own rule, the label as the source time: the built-in row, its
    /// built-in origin, within the cadence plus 30 s of the label. Else <c>O-PIT</c> iff all four: the built-in row;
    /// its built-in origin; the MD5 the vendor published equal to the one this build computed; and a Last-Modified no
    /// later than the label — the vendor's storage dates the file no later than the first-seen time the vendor
    /// declares for it, so it was not written after it. Else <c>O-ARCH</c>: another origin, an MD5 that disagrees, a
    /// file written after its label, or one whose storage gave no date. Never above the revision before it.</para>
    /// </summary>
    static string ArchiveClassOf(string source, string? origin, DateTimeOffset label, DateTimeOffset receivedAt,
        bool verified, DateTimeOffset? lastModified, string? previous)
    {
        var own = ClassOf(source, origin, label, receivedAt, null) == TapeClass.Live
            ? TapeClass.Live
            : TapeSourceCatalog.BuiltInLiveRule(source) is { } rule
              && string.Equals(origin, rule.Origin, StringComparison.Ordinal)
              && verified
              && lastModified is { } written && written <= label
                ? TapeClass.Pit
                : TapeClass.Arch;

        return previous is null ? own : TapeClass.Lower(own, previous);
    }

    /// <summary>
    /// THE NATURAL KEY OF AN OBSERVATION: its subject and the vendor's own time field for that series,
    /// in milliseconds — <c>symbol|time</c> for the premium index and open interest, <c>symbol|timestamp</c>
    /// for the 5-minute series, <c>symbol|fundingTime</c> for settled funding. Computed here and nowhere
    /// else, so "the same datum" has one definition, and in milliseconds whatever unit the vendor
    /// served, so a change of unit cannot split one instant into two keys.
    /// </summary>
    public static string NaturalKey(string subject, DateTimeOffset sourceTime) =>
        subject + "|" + sourceTime.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    readonly record struct Prepared(string Subject, DateTimeOffset SourceTime, string Key, string Payload, string Sha);

    static Prepared Prepare(TapeItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        // A SUBJECT IS HALF OF A KEY, so it may not carry the separator — `A|B` and `A` with a time of
        // `B|…` would otherwise be one key — and it is a symbol, so it looks like one.
        if (!IsSubject(item.Subject))
            throw new ArgumentException($"'{item.Subject}' is not a subject the tape can key: letters, digits, '.', '_' and '-' only, at most 40", nameof(item));

        string payload;
        try { payload = TapeJson.Canonical(item.Payload ?? ""); }
        catch (JsonException ex)
        {
            throw new ArgumentException("an observation's payload is not one JSON value: " + ex.Message, nameof(item), ex);
        }

        var bytes = Encoding.UTF8.GetByteCount(payload);
        if (bytes > MaxPayloadBytes)
            throw new ArgumentException($"an observation's payload is {bytes} bytes and the tape holds at most {MaxPayloadBytes}", nameof(item));

        return new Prepared(item.Subject, item.SourceTime, NaturalKey(item.Subject, item.SourceTime), payload,
            TapeJson.Sha256(payload));
    }

    /// <summary>Whether <paramref name="s"/> can be a subject: what a symbol looks like, and no key separator.</summary>
    public static bool IsSubject(string? s) =>
        s is { Length: >= 1 and <= 40 }
        && s.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');

    long InsertFetch(TapeFetch f, string? origin, int items)
    {
        using var c = Cmd("""
            INSERT INTO tape_fetch(source, series, url, origin, requested_at, received_at, http_status,
                                   items, body_sha256, note)
            VALUES($src,$ser,$url,$origin,$req,$recv,$status,$items,$sha,$note);
            SELECT last_insert_rowid();
            """,
            ("$src", f.Source), ("$ser", f.Series), ("$url", f.Url), ("$origin", origin),
            ("$req", Sql.T(f.RequestedAt)), ("$recv", Sql.T(f.ReceivedAt)), ("$status", f.HttpStatus),
            ("$items", items), ("$sha", f.BodySha256), ("$note", f.Note));

        return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    (int Revision, string Sha, string Class)? Latest(string source, string series, string key)
    {
        using var c = Cmd(
            "SELECT revision, payload_sha256, evidence_class FROM tape_obs "
            + "WHERE source=$src AND series=$ser AND natural_key=$key ORDER BY revision DESC LIMIT 1",
            ("$src", source), ("$ser", series), ("$key", key));
        using var r = c.ExecuteReader();
        return r.Read() ? (r.GetInt32(0), r.GetString(1), r.GetString(2)) : null;
    }

    // ------------------------------------------------------------------------- the decision models

    const string DecisionCols = """
        attempt_id, instrument, url, origin, requested_model, answered_model, pin, unpinned, host_response_id,
        schema_id, schema_version, schema_sha256, state_sha256, sources, requested_at, ended_at, latency_ms,
        http_status, status, error_class, input_tokens, output_tokens, cost_estimated, cost_billed, price_basis, answers
        """;

    /// <summary>
    /// RECORDS ONE CALL THAT WAS SENT, in one transaction, and answers what was written — with the two facts decided
    /// here: the origin, read off the call's own URL, and whether the answer is UNPINNED, read off this build's pin for
    /// its instrument and from no caller. A REFUSED call is refused here too: it was never sent, so there is nothing to
    /// record. A second record for one attempt is refused by the table's key.
    /// </summary>
    public DecisionCallRecord RecordDecision(DecisionCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (call.Status == DecisionStatus.REFUSED)
            throw new ArgumentException("a refused call was never sent and is not recorded", nameof(call));
        if (string.IsNullOrWhiteSpace(call.AttemptId) || string.IsNullOrWhiteSpace(call.Instrument)
            || string.IsNullOrWhiteSpace(call.Url) || string.IsNullOrWhiteSpace(call.RequestedModel))
            throw new ArgumentException("a decision call names its attempt, its instrument, its URL and the model it asked for", nameof(call));
        if (call.Status == DecisionStatus.ANSWERED && call.Answers is null)
            throw new ArgumentException("an answered call carries its answers", nameof(call));

        var answers = call.Answers is null ? null : TapeJson.Canonical(call.Answers);
        var sources = JsonSerializer.Serialize(call.Sources);
        var origin = UrlOrigin.Of(call.Url);
        var pin = DecisionInstruments.BuiltInPin(call.Instrument);
        var unpinned = !DecisionInstruments.IsPinned(call.Instrument, call.AnsweredModel);

        Write(() =>
        {
            // A CALL UNDER A LENS IS ITS LENS'S, OR IT IS NOT RECORDED: checked again here, in the transaction that writes it.
            if (LensCheck(call.Schema, call.StateSha256, call.Sources) is { } notTheLens)
                throw new ArgumentException(notTheLens, nameof(call));

            using var c = Cmd($"""
                INSERT INTO decision_call({DecisionCols})
                VALUES($attempt,$instrument,$url,$origin,$requested,$answered,$pin,$unpinned,$host,$schema,$version,$schemaSha,
                       $stateSha,$sources,$requestedAt,$endedAt,$latency,$http,$status,$error,$in,$out,$estimated,$billed,$basis,$answers)
                """,
                ("$attempt", call.AttemptId), ("$instrument", call.Instrument), ("$url", call.Url), ("$origin", origin),
                ("$requested", call.RequestedModel), ("$answered", call.AnsweredModel), ("$pin", pin), ("$unpinned", unpinned ? 1 : 0),
                ("$host", call.HostResponseId), ("$schema", call.Schema.Id), ("$version", call.Schema.Version),
                ("$schemaSha", call.Schema.Sha), ("$stateSha", call.StateSha256), ("$sources", sources),
                ("$requestedAt", Sql.T(call.RequestedAt)), ("$endedAt", Sql.T(call.EndedAt)), ("$latency", call.LatencyMs),
                ("$http", call.HttpStatus), ("$status", call.Status.ToString()), ("$error", call.ErrorClass),
                ("$in", call.InputTokens), ("$out", call.OutputTokens),
                ("$estimated", call.EstimatedCost is { } e ? Sql.D(e) : null), ("$billed", call.BilledCost is { } b ? Sql.D(b) : null),
                ("$basis", call.PriceBasis), ("$answers", answers));
            return c.ExecuteNonQuery();
        });

        return new DecisionCallRecord(call with { Answers = answers }, origin, pin, unpinned);
    }

    /// <summary>One recorded call by its attempt id, or null. In-process only, like every read of this store.</summary>
    public DecisionCallRecord? RecordedCall(string attemptId) => Read(() =>
    {
        using var c = Cmd($"SELECT {DecisionCols} FROM decision_call WHERE attempt_id=$id", ("$id", attemptId));
        using var r = c.ExecuteReader();
        return r.Read() ? Decision(r) : null;
    });

    /// <summary>The recorded calls, newest first.</summary>
    public IReadOnlyList<DecisionCallRecord> RecordedCalls(int limit = 100) => Read(() =>
    {
        using var c = Cmd($"SELECT {DecisionCols} FROM decision_call ORDER BY requested_at DESC, rowid DESC LIMIT $n",
            ("$n", Math.Max(0, limit)));
        return (IReadOnlyList<DecisionCallRecord>)ReadAll(c, Decision);
    });

    static DecisionCallRecord Decision(SqliteDataReader r) => new(
        new DecisionCall
        {
            AttemptId = r.GetString(0),
            Instrument = r.GetString(1),
            Url = r.GetString(2),
            RequestedModel = r.GetString(4),
            AnsweredModel = Sql.S(r.GetValue(5)),
            HostResponseId = Sql.S(r.GetValue(8)),
            Schema = new SchemaRef(r.GetString(9), r.GetInt32(10), r.GetString(11)),
            StateSha256 = r.GetString(12),
            Sources = JsonSerializer.Deserialize<List<string>>(r.GetString(13)) ?? [],
            RequestedAt = Sql.Time(r.GetString(14)),
            EndedAt = Sql.Time(r.GetString(15)),
            LatencyMs = r.IsDBNull(16) ? null : r.GetInt64(16),
            HttpStatus = r.IsDBNull(17) ? null : r.GetInt32(17),
            Status = Enum.Parse<DecisionStatus>(r.GetString(18)),
            ErrorClass = Sql.S(r.GetValue(19)),
            InputTokens = r.IsDBNull(20) ? null : r.GetInt64(20),
            OutputTokens = r.IsDBNull(21) ? null : r.GetInt64(21),
            EstimatedCost = Sql.DecN(r.GetValue(22)),
            BilledCost = Sql.DecN(r.GetValue(23)),
            PriceBasis = Sql.S(r.GetValue(24)),
            Answers = Sql.S(r.GetValue(25))
        },
        Sql.S(r.GetValue(3)), Sql.S(r.GetValue(6)), r.GetInt64(7) == 1);

    // ---------------------------------------------------------------------------------------------- the lenses

    const string LensCols =
        "id, version, lens_sha, schema_sha, definition, target_source, target_series, proposer, attempt, registered_at, validator_version";

    /// <summary>
    /// REGISTERS ONE PROPOSED LENS, OR SAYS WHY NOT, in one transaction (<c>U-lenses</c>). The definition is parsed here
    /// (<see cref="Lens.Parse"/>) and stored in its canonical spelling; the version and both hashes are this store's. The
    /// same bytes as a registered lens are that lens — the first registration stands and nothing is written; the same id
    /// with other bytes is its next version. Refused in words, nothing written: a definition the validator refuses, a
    /// built-in's id, a proposer that is nobody or the app, and a proposer's <see cref="MaxLensProposalsPerDay"/>th
    /// registration of the owner's day passed.
    /// </summary>
    /// <param name="proposer">The role the caller's launch grant proved — never a name the caller wrote.</param>
    /// <param name="attempt">The attempt it proved, or null.</param>
    /// <param name="now">When it was registered. Null is now; a test passes its own.</param>
    public LensRegistration RegisterLens(string definition, string proposer, string? attempt, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(proposer))
            return new(null, false, "a lens is registered under the role that proposed it, and this proposal names none; nothing was written");
        if (string.Equals(proposer, BuiltInLenses.Proposer, StringComparison.Ordinal))
            return new(null, false, $"'{BuiltInLenses.Proposer}' registers this build's own lenses and proposes none; nothing was written");

        var parsed = Lens.Parse(definition);
        if (parsed.Lens is not { } lens) return new(null, false, parsed.Why + "; nothing was written");
        if (BuiltInLenses.Ids().Contains(lens.Id))
            return new(null, false, $"'{lens.Id}' is the id of a lens this build ships, whose versions are the app's; a proposal "
                                    + "takes another id. Nothing was written");

        var at = now ?? DateTimeOffset.UtcNow;
        return Write(() =>
        {
            if (LensBySha(lens.LensSha) is { } same)
                return same.Parse() is { Ok: false } other
                    ? new LensRegistration(null, false, $"these bytes are already registered and cannot be used: {other.Why}. Nothing was written")
                    : new LensRegistration(same, false, null);

            var (from, to) = OwnerDay.Window(at);
            using (var c = Cmd("SELECT COUNT(*) FROM lens WHERE proposer=$p AND registered_at >= $from AND registered_at < $to",
                       ("$p", proposer), ("$from", Sql.T(from)), ("$to", Sql.T(to))))
            {
                var today = Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
                if (today >= MaxLensProposalsPerDay)
                    return new LensRegistration(null, false,
                        $"{proposer} has registered {today} lenses today, and a role registers at most {MaxLensProposalsPerDay} in one "
                        + "owner day; the same bytes again are the lens already there and cost nothing. Nothing was written");
            }

            int version;
            using (var c = Cmd("SELECT COALESCE(MAX(version), 0) + 1 FROM lens WHERE id=$id", ("$id", lens.Id)))
                version = Convert.ToInt32(c.ExecuteScalar(), CultureInfo.InvariantCulture);

            return new LensRegistration(InsertLens(lens, version, proposer, attempt, at), true, null);
        });
    }

    /// <summary>
    /// WHY A CALL MAY NOT GO UNDER THE LENS ITS SCHEMA NAMES, or null — because it names no registered lens, or because it
    /// is exactly that lens's question about one observation of its target (<c>U-lenses</c>). A call whose schema id names
    /// a lens must carry that lens's registered version and ITS questions' hash, exactly one source, <c>tape_obs:&lt;id&gt;</c>,
    /// naming an observation of the lens's target the screen does not quarantine, and the very state
    /// <see cref="LensState.For(LensRecord, TapeObservation?)"/> builds from it — so what was asked, and about which item,
    /// is the registry's to say and never the caller's. The wire asks this before its rate gate, its key or its reservation;
    /// <see cref="RecordDecision"/> asks it again, in the transaction that writes the call.
    /// </summary>
    public string? LensRefusal(DecisionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Schema is null) return null;
        string stateSha;
        try { stateSha = TapeJson.Sha256(DecisionRequests.CanonicalState(request.State)); }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { stateSha = ""; }
        return Read(() => LensCheck(request.Schema, stateSha, request.Sources));
    }

    string? LensCheck(SchemaRef schema, string stateSha, IReadOnlyList<string> sources)
    {
        using (var c = Cmd("SELECT COUNT(*) FROM lens WHERE id=$id", ("$id", schema.Id)))
            if (Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) return null;

        if (LensAt(schema.Id, schema.Version) is not { } lens)
            return $"the call names the lens '{schema.Id}', which has no version {schema.Version} on the tape; a lens's call carries "
                   + "a version the tape registered, and nothing was sent";
        if (!string.Equals(lens.SchemaSha, schema.Sha, StringComparison.Ordinal))
            return $"the call names the lens '{schema.Id}' version {schema.Version} and asks other questions than it registered (its "
                   + $"questions hash to {lens.SchemaSha}, the call's to {schema.Sha}); a lens's call asks exactly its lens's questions, "
                   + "and nothing was sent";
        if (sources is not { Count: 1 } || LensState.ObservationOf(sources[0]) is not { } id)
            return "a lens's call carries exactly one source, 'tape_obs:<id>', the observation its state was built from, and nothing was sent";

        TapeObservation? obs;
        using (var c = Cmd($"SELECT {ObsCols} FROM tape_obs WHERE id=$id", ("$id", id)))
        using (var r = c.ExecuteReader())
            obs = r.Read() ? Obs(r) : null;
        if (obs is null) return $"the call names tape_obs:{id}, which the tape does not hold, and nothing was sent";

        var built = LensState.For(lens, obs);
        if (built.Ask is not { } ask) return built.Refusal + ", and nothing was sent";
        if (!string.Equals(TapeJson.Sha256(ask.State), stateSha, StringComparison.Ordinal))
            return $"the state is not the one the lens '{lens.Id}' version {lens.Version} builds from tape_obs:{id}; a lens's state is "
                   + "built by code from its observation and nothing else, and nothing was sent";
        return null;
    }

    /// <summary>One registered lens by id and version, or null.</summary>
    public LensRecord? LensOf(string id, int version) => Read(() => LensAt(id, version));

    /// <summary>The registered lenses, newest first.</summary>
    public IReadOnlyList<LensRecord> Lenses(int limit = 100) => Read(() =>
    {
        using var c = Cmd($"SELECT {LensCols} FROM lens ORDER BY registered_at DESC, rowid DESC LIMIT $n", ("$n", Math.Max(0, limit)));
        return (IReadOnlyList<LensRecord>)ReadAll(c, LensRow);
    });

    /// <summary>
    /// THIS BUILD'S LENSES, REGISTERED AT OPEN — or the tape refused, untouched, because one of them differs from what the
    /// tape holds under its id and version. Every check is a read, made before anything is written, so a refusal leaves
    /// the file as it was found.
    /// </summary>
    void RegisterBuiltIns(IReadOnlyList<BuiltInLens> builtIns)
    {
        var lenses = new List<(Lens Lens, int Version)>();
        foreach (var b in builtIns)
        {
            var parsed = Lens.Parse(b.Definition);
            if (parsed.Lens is not { } lens || b.Version < 1)
                throw new InvalidOperationException($"a lens this build ships does not validate ({parsed.Why}); the tape at {File} "
                                                    + "has not been written to");
            lenses.Add((lens, b.Version));
        }

        var missing = Read(() =>
        {
            var todo = new List<(Lens Lens, int Version)>();
            foreach (var (lens, version) in lenses)
            {
                var held = LensAt(lens.Id, version);
                var bySha = LensBySha(lens.LensSha);
                if (held is not null && (held.LensSha != lens.LensSha || held.Proposer != BuiltInLenses.Proposer))
                    throw new InvalidOperationException(
                        $"the lens '{lens.Id}' version {version} this build ships hashes to {lens.LensSha}, and the market-context "
                        + $"tape at {File} holds '{lens.Id}' version {version} as {held.LensSha}, registered by {held.Proposer}. "
                        + "A lens never changes under its version — the answers recorded under it are bound to its exact bytes — "
                        + $"so this build is refused this tape, and nothing has been written to it. A build that ships the changed "
                        + $"lens as version {MaxVersion(lens.Id) + 1} opens it.");
                if (bySha is not null && (bySha.Id != lens.Id || bySha.Version != version))
                    throw new InvalidOperationException(
                        $"the lens '{lens.Id}' version {version} this build ships is byte for byte '{bySha.Id}' version "
                        + $"{bySha.Version} on the market-context tape at {File}; one definition is one lens, so this build is "
                        + "refused this tape, and nothing has been written to it.");
                if (held is null) todo.Add((lens, version));
            }
            return todo;
        });

        if (missing.Count == 0) return;
        var at = DateTimeOffset.UtcNow;
        Write(() =>
        {
            foreach (var (lens, version) in missing) InsertLens(lens, version, BuiltInLenses.Proposer, null, at);
            return 0;
        });
    }

    LensRecord InsertLens(Lens lens, int version, string proposer, string? attempt, DateTimeOffset at)
    {
        using (var c = Cmd($"""
                   INSERT INTO lens({LensCols})
                   VALUES($id,$version,$sha,$schema,$definition,$source,$series,$proposer,$attempt,$at,$validator)
                   """,
                   ("$id", lens.Id), ("$version", version), ("$sha", lens.LensSha), ("$schema", lens.SchemaSha),
                   ("$definition", lens.Canonical), ("$source", lens.Target.Source), ("$series", lens.Target.Series),
                   ("$proposer", proposer), ("$attempt", attempt), ("$at", Sql.T(at)), ("$validator", Lens.ValidatorVersion)))
            c.ExecuteNonQuery();
        return LensAt(lens.Id, version)!;
    }

    LensRecord? LensAt(string id, int version)
    {
        using var c = Cmd($"SELECT {LensCols} FROM lens WHERE id=$id AND version=$v", ("$id", id), ("$v", version));
        using var r = c.ExecuteReader();
        return r.Read() ? LensRow(r) : null;
    }

    LensRecord? LensBySha(string sha)
    {
        using var c = Cmd($"SELECT {LensCols} FROM lens WHERE lens_sha=$sha", ("$sha", sha));
        using var r = c.ExecuteReader();
        return r.Read() ? LensRow(r) : null;
    }

    int MaxVersion(string id)
    {
        using var c = Cmd("SELECT COALESCE(MAX(version), 0) FROM lens WHERE id=$id", ("$id", id));
        return Convert.ToInt32(c.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>One <c>lens</c> row. Shared with <see cref="TapeReader"/>, which maps it the same way.</summary>
    internal static LensRecord LensRow(SqliteDataReader r) => new(
        r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
        r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), Sql.Time(r.GetString(9)), r.GetString(10));

    // ---------------------------------------------------------------------------------- the reads

    /// <summary>
    /// WHAT HAD ARRIVED BY <paramref name="t"/> ABOUT ONE SUBJECT'S SERIES: the observation with the
    /// latest source time among those received at or before <paramref name="t"/>, at the latest of its
    /// revisions received by then — or no row, because nothing had.
    ///
    /// <para><b><paramref name="holdout"/> is required, and is checked INSIDE the reader</b>, as
    /// <c>DatasetReader.Read</c> checks its own (<c>U-tape-holdout</c>): when the row this would serve has a
    /// SOURCE time inside any dataset's holdout window, the answer is <see cref="TapeAsOf.Refusal"/> and no row —
    /// not the newest reading before the window, which would be a different answer from the one asked for. When
    /// it arrived does not matter: a late revision of a reading stamped before the window is served. The ledger
    /// is read after the tape's lock is released, so the two locks are never held together.</para>
    ///
    /// <para><b>A quarantined observation's payload is WITHHELD here</b> (<c>U-tape-events</c>): the answer
    /// carries every field and the <see cref="TapeObservation.Quarantine"/> that says why, and a null
    /// <see cref="TapeObservation.Payload"/> — to every audience, the referee's included: it reads the tape only to
    /// value the features of a version it judges (<c>U-language-v2a</c>; <c>FeatureFeed</c>, through
    /// <see cref="TapeReader.Window"/>), and such a reading's value is absent. Whether a quarantined item ever
    /// reaches a model is <c>U-annotator</c>'s decision, to be made by a door of its own rather than inherited
    /// from this one.</para>
    /// </summary>
    public TapeAsOf AsOf(TapeHoldout holdout, string source, string series, string subject, DateTimeOffset t)
    {
        ArgumentNullException.ThrowIfNull(holdout);

        var row = Read(() =>
        {
            using var c = Cmd($"""
                SELECT {ObsCols} FROM tape_obs
                WHERE source=$src AND series=$ser AND subject=$subj AND received_at <= $t
                ORDER BY source_time DESC, revision DESC
                LIMIT 1
                """,
                ("$src", source), ("$ser", series), ("$subj", subject), ("$t", Sql.T(t)));
            using var r = c.ExecuteReader();
            return r.Read() ? Obs(r) : null;
        });

        if (row is null) return new TapeAsOf(null, null);
        return holdout.Refusal(row, t) is { } withheld
            ? new TapeAsOf(null, withheld)
            : new TapeAsOf(Served(holdout.Audience, row), null);
    }

    /// <summary>
    /// THE ONE PLACE A PAYLOAD IS WITHHELD FROM AN AUDIENCE (<c>U-tape-events</c>; <c>U-tape-read</c>): a quarantined
    /// observation keeps every field and its <see cref="TapeObservation.Quarantine"/>, and loses its payload. Every
    /// read that takes a holdout goes through this — <see cref="AsOf"/> here and <see cref="TapeReader.Window"/>, the
    /// agent-facing range read — so a door onto the tape cannot serve a quarantined payload by forgetting a line, and
    /// a later rule about who may see what is a change to this method and not a hunt for every caller.
    ///
    /// <para><paramref name="audience"/> decides nothing yet: no audience may read a quarantined payload, the
    /// referee's included, and whether one ever reaches a model is <c>U-annotator</c>'s decision, to be made by a door
    /// of its own. It is required so that the decision, when it comes, has one place to live. The holdout windows are
    /// not decided here: they are <see cref="TapeHoldout"/>'s, checked by each reader before a row is served.</para>
    /// </summary>
    internal static TapeObservation Served(BarAudience audience, TapeObservation o)
    {
        ArgumentNullException.ThrowIfNull(audience);
        return o.Quarantine is null ? o : o with { Payload = null };
    }

    /// <summary>
    /// Every revision held under one natural key, first reading first. An in-process read, payloads whole
    /// and each with its quarantine: a reader that serves an agent withholds a quarantined payload, as
    /// <see cref="AsOf"/> does.
    ///
    /// <para><b>It takes no <see cref="TapeHoldout"/>, and that is why it is never a pipe read</b>: it is the
    /// recorders' own bookkeeping (<c>GdeltRecorder</c> reads its file records through it), and the gateway holds a
    /// <see cref="TapeReader"/>, never this store. <c>TapeHoldoutTests</c> holds the list of public reads that serve a
    /// row without a holdout to this and <see cref="ObservationsOf"/>, by name.</para>
    /// </summary>
    public IReadOnlyList<TapeObservation> Revisions(string source, string series, string naturalKey) => Read(() =>
    {
        using var c = Cmd(
            $"SELECT {ObsCols} FROM tape_obs WHERE source=$src AND series=$ser AND natural_key=$key ORDER BY revision",
            ("$src", source), ("$ser", series), ("$key", naturalKey));
        return (IReadOnlyList<TapeObservation>)ReadAll(c, Obs);
    });

    /// <summary>The observations one fetch brought, in the order they were written. In-process, like <see cref="Revisions"/>.</summary>
    public IReadOnlyList<TapeObservation> ObservationsOf(long fetchId) => Read(() =>
    {
        using var c = Cmd($"SELECT {ObsCols} FROM tape_obs WHERE fetch_id=$id ORDER BY id", ("$id", fetchId));
        return (IReadOnlyList<TapeObservation>)ReadAll(c, Obs);
    });

    /// <summary>
    /// WHAT THE FETCHES OF ONE SERIES FROM <paramref name="firstFetch"/> THROUGH <paramref name="lastFetch"/> STORED NEW —
    /// subject and source time of each, in the order written, never a payload: the collector's coalesced notice to its
    /// listener (<c>U-reconcile-packet</c>, review 2). Names and instants only, like <see cref="SourceTimes"/>.
    /// </summary>
    public IReadOnlyList<TapeArrived> Arrived(string source, string series, long firstFetch, long lastFetch) => Read(() =>
    {
        using var c = Cmd(
            "SELECT subject, source_time FROM tape_obs WHERE fetch_id >= $first AND fetch_id <= $last AND source=$src AND series=$ser ORDER BY id",
            ("$first", firstFetch), ("$last", lastFetch), ("$src", source), ("$ser", series));
        var items = new List<TapeArrived>();
        using var r = c.ExecuteReader();
        while (r.Read()) items.Add(new TapeArrived(r.GetString(0), Sql.Time(r.GetString(1))));
        return (IReadOnlyList<TapeArrived>)items;
    });

    /// <summary>
    /// THE SOURCE TIMES HELD FOR ONE SUBJECT OF ONE SERIES at or after <paramref name="from"/> — for an archive, which
    /// of its files the tape already holds a record of (<c>U-tape-archive</c>), so a recorder resumes from the tape and
    /// never from a memory of its own.
    /// </summary>
    public IReadOnlySet<DateTimeOffset> SourceTimes(string source, string series, string subject, DateTimeOffset from) => Read(() =>
    {
        using var c = Cmd(
            "SELECT DISTINCT source_time FROM tape_obs WHERE source=$src AND series=$ser AND subject=$subj AND source_time >= $from",
            ("$src", source), ("$ser", series), ("$subj", subject), ("$from", Sql.T(from)));
        var times = new HashSet<DateTimeOffset>();
        using var r = c.ExecuteReader();
        while (r.Read()) times.Add(Sql.Time(r.GetString(0)));
        return (IReadOnlySet<DateTimeOffset>)times;
    });

    /// <summary>
    /// WHAT ONE SERIES' ROWS WEIGH whose source time is in [<paramref name="from"/>, <paramref name="to"/>): the UTF-8
    /// bytes of every revision's payload, as stored — for an archive, what a day of its kept rows costs the owner's disk
    /// (<c>U-tape-archive</c>'s daily cap).
    /// </summary>
    public long PayloadBytes(string source, string series, DateTimeOffset from, DateTimeOffset to) => Read(() =>
    {
        using var c = Cmd("""
            SELECT COALESCE(SUM(length(CAST(payload AS BLOB))), 0) FROM tape_obs
            WHERE source=$src AND series=$ser AND source_time >= $from AND source_time < $to
            """,
            ("$src", source), ("$ser", series), ("$from", Sql.T(from)), ("$to", Sql.T(to)));
        return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    /// <summary>Attempts, newest first, optionally of one source and series.</summary>
    public IReadOnlyList<TapeFetchRecord> Fetches(string? source = null, string? series = null, int limit = 100) => Read(() =>
    {
        using var c = Cmd($"""
            SELECT {FetchCols} FROM tape_fetch
            WHERE ($src IS NULL OR source=$src) AND ($ser IS NULL OR series=$ser)
            ORDER BY id DESC LIMIT $n
            """,
            ("$src", source), ("$ser", series), ("$n", Math.Max(0, limit)));
        return (IReadOnlyList<TapeFetchRecord>)ReadAll(c, Fetch);
    });

    static List<T> ReadAll<T>(SqliteCommand c, Func<SqliteDataReader, T> map)
    {
        var rows = new List<T>();
        using var r = c.ExecuteReader();
        while (r.Read()) rows.Add(map(r));
        return rows;
    }

    /// <summary>
    /// ONE ROW, SCREENED AS IT IS READ. The verdict is computed here at every read and stored nowhere: no
    /// column holds it, so a later version of the screen reads every row ever recorded, and nothing written
    /// into the file can mark a row clean. Shared with <see cref="TapeReader"/>, so a row read there is screened by
    /// the same call.
    /// </summary>
    internal static TapeObservation Obs(SqliteDataReader r)
    {
        var payload = r.GetString(10);
        return new(
            r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), Sql.Time(r.GetString(4)),
            Sql.Time(r.GetString(5)), r.GetInt64(6), r.GetString(7), r.GetInt32(8), r.GetString(9), payload,
            r.GetString(11), TapeScreen.Check(payload));
    }

    static TapeFetchRecord Fetch(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
        Sql.Time(r.GetString(5)), Sql.Time(r.GetString(6)), r.IsDBNull(7) ? null : r.GetInt32(7), r.GetInt32(8),
        r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10));

    // -------------------------------------------------------------------------------- plumbing

    T Write<T>(Func<T> body)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            var result = body();
            tx.Commit();
            return result;
        }
    }

    T Read<T>(Func<T> body)
    {
        lock (_gate) return body();
    }

    // Created INSIDE the gate, after any transaction began: Microsoft.Data.Sqlite enlists a command in
    // the connection's open transaction when the command is created, not when it runs.
    SqliteCommand Cmd(string sql, params (string, object?)[] ps)
    {
        var c = _conn.CreateCommand();
        c.CommandText = sql;
        foreach (var (k, v) in ps) c.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return c;
    }

    void Exec(string sql)
    {
        using var c = Cmd(sql);
        c.ExecuteNonQuery();
    }

    public void Dispose()
    {
        lock (_gate) _conn.Dispose();
    }
}

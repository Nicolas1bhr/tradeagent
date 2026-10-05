using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradeAgent.Core.Data;

namespace TradeAgent.Core.Db;

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
/// <para><b>It is app-owned.</b> There is no verb and no pipe op that writes here; the collector in the
/// app's own process is the only caller of <see cref="Append"/>. Until containment an agent running
/// unconfined could still edit the FILE — <c>docs/CONTRACTS.md</c> "The tape" says so and does not
/// claim otherwise.</para>
/// </summary>
public sealed class TapeStore : IDisposable
{
    /// <summary>The version of the layout below, written into the file it describes.</summary>
    public const int Schema = 1;

    /// <summary>The most one observation's canonical payload may hold, in UTF-8 bytes.</summary>
    public const int MaxPayloadBytes = 64 * 1024;

    const string ObsCols =
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
    public TapeStore(string? file = null)
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
    /// THE LADDER. One rung today; every later one is an <c>if (have &lt; N)</c> below it, written by the
    /// unit that needs it and committed in one transaction with its own version row, so a crash
    /// mid-rung re-runs the rung rather than leaving a file that claims a layout it does not have.
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
        ArgumentNullException.ThrowIfNull(fetch);
        if (string.IsNullOrWhiteSpace(fetch.Source)) throw new ArgumentException("a fetch names its source", nameof(fetch));
        if (string.IsNullOrWhiteSpace(fetch.Series)) throw new ArgumentException("a fetch names its series", nameof(fetch));
        if (string.IsNullOrWhiteSpace(fetch.Url)) throw new ArgumentException("a fetch names the URL it asked", nameof(fetch));

        // EVERYTHING REFUSED IS REFUSED HERE, BEFORE THE TRANSACTION: see the type summary.
        var prepared = (items ?? []).Select(Prepare).ToList();

        // THE ORIGIN IS READ OFF THE URL, NOT TAKEN FROM THE CALLER. One URL, one origin: the address a
        // row was fetched from and the address its class is decided by cannot be two claims.
        var origin = UrlOrigin.Of(fetch.Url);

        return Write(() =>
        {
            var fetchId = InsertFetch(fetch, origin, prepared.Count);

            int stored = 0, revised = 0, unchanged = 0;
            foreach (var item in prepared)
            {
                var latest = Latest(fetch.Source, fetch.Series, item.Key);

                // THE INSERT IS WHAT REFUSES, NOT A BRANCH ABOVE IT. A re-reading whose payload
                // matches the latest revision is AIMED AT THAT REVISION'S OWN SLOT —
                // (source, series, natural_key, revision) — so it is the conflict clause that writes
                // nothing. A check-then-skip cannot state that nothing was overwritten, which
                // `ForwardBarStore` measured: an earlier draft there skipped on a read and left
                // `INSERT OR REPLACE` passing every test because the replacing statement was never
                // reached. Here `INSERT OR REPLACE` would move the first reading's arrival instant and
                // fetch onto the re-reading — an edited past — and the tests watch for exactly that.
                var revision = latest is not { } held ? 1
                    : held.Sha == item.Sha ? held.Revision
                    : held.Revision + 1;

                var evidence = ClassOf(fetch.Source, origin, item.SourceTime, fetch.ReceivedAt, latest?.Class);

                using var c = Cmd("""
                    INSERT INTO tape_obs(source, series, subject, source_time, received_at, fetch_id,
                                         natural_key, revision, payload_sha256, payload, evidence_class)
                    VALUES($src,$ser,$subj,$st,$recv,$fetch,$key,$rev,$sha,$payload,$class)
                    ON CONFLICT(source, series, natural_key, revision) DO NOTHING
                    """,
                    ("$src", fetch.Source), ("$ser", fetch.Series), ("$subj", item.Subject),
                    ("$st", Sql.T(item.SourceTime)), ("$recv", Sql.T(fetch.ReceivedAt)), ("$fetch", fetchId),
                    ("$key", item.Key), ("$rev", revision), ("$sha", item.Sha), ("$payload", item.Payload),
                    ("$class", evidence));

                if (c.ExecuteNonQuery() == 1)
                {
                    stored++;
                    if (revision > 1) revised++;
                }
                else unchanged++;
            }

            return new TapeAppend(fetchId, prepared.Count, stored, revised, unchanged);
        });
    }

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

    // ---------------------------------------------------------------------------------- the reads

    /// <summary>
    /// WHAT HAD ARRIVED BY <paramref name="t"/> ABOUT ONE SUBJECT'S SERIES: the observation with the
    /// latest source time among those received at or before <paramref name="t"/>, at the latest of its
    /// revisions received by then — or null, because nothing had.
    ///
    /// <para><b><paramref name="audience"/> is required</b>, and is checked INSIDE the reader, as
    /// <c>DatasetReader.Read</c> checks its own: a later holdout over the tape applies here without a
    /// caller having to remember a line. No tape holdout exists yet, so today every audience is served
    /// the same answer; the argument is what makes adding one a change to this method and not a hunt
    /// for every caller.</para>
    ///
    /// <para><b>A quarantined observation's payload is WITHHELD here</b> (<c>U-tape-events</c>): the answer
    /// carries every field and the <see cref="TapeObservation.Quarantine"/> that says why, and a null
    /// <see cref="TapeObservation.Payload"/>. That is <see cref="BarAudience.Pipe"/> — every caller on the
    /// agent-facing channel — and, today, every audience there is: the referee's, the only other, never
    /// reads the tape, and whether a quarantined item ever reaches a model is <c>U-annotator</c>'s decision,
    /// to be made by a door of its own rather than inherited from this one.</para>
    /// </summary>
    public TapeObservation? AsOf(BarAudience audience, string source, string series, string subject,
        DateTimeOffset t)
    {
        ArgumentNullException.ThrowIfNull(audience);

        return Read(() =>
        {
            using var c = Cmd($"""
                SELECT {ObsCols} FROM tape_obs
                WHERE source=$src AND series=$ser AND subject=$subj AND received_at <= $t
                ORDER BY source_time DESC, revision DESC
                LIMIT 1
                """,
                ("$src", source), ("$ser", series), ("$subj", subject), ("$t", Sql.T(t)));
            using var r = c.ExecuteReader();
            if (!r.Read()) return null;

            var o = Obs(r);
            return o.Quarantine is null ? o : o with { Payload = null };
        });
    }

    /// <summary>
    /// Every revision held under one natural key, first reading first. An in-process read, payloads whole
    /// and each with its quarantine: a reader that serves an agent withholds a quarantined payload, as
    /// <see cref="AsOf"/> does.
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
    /// into the file can mark a row clean.
    /// </summary>
    static TapeObservation Obs(SqliteDataReader r)
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

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradeAgent.Core.Db;

namespace TradeAgent.Core.Oversight;

/// <summary>A scope the owner's line reports over: a unit and everything under it. The root scope <c>org</c> is the whole organisation.</summary>
public sealed record OversightScope(string Id, string UnitId, DateTimeOffset CreatedAt);

/// <summary>One record a finding rests on: a kind from <see cref="OversightCite.Kinds"/> and the record's id in the app's database.</summary>
public sealed record OversightCite(string Kind, string Id)
{
    /// <summary>THE CLOSED LIST of what a finding may cite. A cite of any other kind is refused when the finding is written.</summary>
    public static readonly string[] Kinds = ["attempt", "mandate", "delivery", "check", "ledger", "run", "promotion", "event", "tool_call", "log"];

    /// <summary>The cite as a digest prints it: <c>kind:id</c>.</summary>
    public override string ToString() => $"{Kind}:{Id}";
}

/// <summary>
/// ONE FINDING: an APP MEASUREMENT over app rows — what a detector counted, in which unit, citing the records it counted. Organisation claims
/// are cited by id, never copied in as fact. Its id is the SHA-256 of detector, scope, subject and window, so the same fact swept twice is one row.
/// </summary>
public sealed record OversightFinding(
    string Id,
    string Detector,
    string Severity,
    string ScopeId,
    string UnitId,
    string UnitPath,
    string Subject,
    string Window,
    IReadOnlyList<OversightCite> Cites,
    IReadOnlyDictionary<string, string> Measured,
    string Blind,
    DateTimeOffset At)
{
    /// <summary>The row's order in the file, assigned when written; zero on a finding not yet written.</summary>
    public long Seq { get; init; }

    /// <summary>The id a finding of these four parts carries: <c>wf-</c> and the SHA-256 of them.</summary>
    public static string IdOf(string detector, string scope, string subject, string window) =>
        "wf-" + Sha256Hex.Of(string.Join('\n', detector, scope, subject, window));
}

/// <summary>A digest the code composed and <see cref="DigestCheck"/> passed: its text, the findings it reports and the last finding it covers.</summary>
public sealed record OversightDigest(string Id, string ScopeId, DateTimeOffset At, string Text, IReadOnlyList<string> Findings, long ThroughSeq);

/// <summary>Severity words a finding carries.</summary>
public static class OversightSeverity
{
    public const string Info = "info";
    public const string Severe = "severe";
}

/// <summary>
/// THE OWNER'S PRIVATE LINE, ITS OWN FILE (<c>U-org-watcher-a</c>; ORGANISATION § 9, VISION §§ 5.2–5.3, ORCHESTRATION § 2.13). Named
/// <b>Oversight</b> in code so it is never confused with the gateway's loss watcher (<c>LossBreach</c>).
///
/// <para><b>Not a rung of the app's database and never one</b> — the tape's precedent (<see cref="TapeStore"/>): its own file
/// <c>state/oversight.db</c>, its own ladder (<c>oversight_meta</c> key <c>schema</c>), its own connection. So it is never a row in any
/// <c>org_*</c> table, nothing in it is summed by any total an agent can read, and an oversight file written by a newer build is refused
/// with both versions named, nothing read from it or written to it. Its path is this class's constant, NOT one of <see cref="Paths"/>, so
/// any reference to the file is a reference to this namespace — which no agent-reachable assembly makes (a guard holds that).</para>
///
/// <para><b>Append-only by the schema:</b> every table but the version row refuses UPDATE and DELETE by trigger. A cursor moves by a new
/// row, never by an edit. Its own failures are written HERE (<c>oversight_failure</c>), never to the activity or engineering log, which
/// the organisation's reports can carry.</para>
///
/// <para><b>Advisory until containment</b> (ORCHESTRATION § 5, ORGANISATION § 14): on the legacy pair's unconfined CLI seats, which run as
/// the owner's own user, this file is readable, and the public repository names the line. Hard on app paths, mediated on broker-only
/// seats, hard in cells (<c>state/</c> is refused to every cell grant).</para>
/// </summary>
public sealed class OversightStore : IDisposable
{
    /// <summary>The file's name under <see cref="Paths.State"/>. HERE, not in <see cref="Paths"/>, on purpose (see the class).</summary>
    public const string FileName = "oversight.db";

    /// <summary>The layout version this build writes and understands.</summary>
    public const int Schema = 1;

    /// <summary>The root scope: the owner's private line over the whole organisation, seeded by rung 1.</summary>
    public const string RootScope = "org";

    /// <summary>The product's file: <c>state/oversight.db</c>.</summary>
    public static string DefaultFile => Path.Combine(Paths.State, FileName);

    readonly Lock _gate = new();
    readonly SqliteConnection _conn;

    /// <summary>The file this store holds open, canonical and absolute.</summary>
    public string File { get; }

    /// <summary>
    /// Opens the file, creating it if it is not there; refuses one written by a newer build, leaving it byte-for-byte as found. The order is
    /// the tape's: the busy timeout, the version READ, and only for a file this build understands the journal mode and the ladder.
    /// </summary>
    /// <param name="file">Null is <see cref="DefaultFile"/>; a test passes its own.</param>
    /// <param name="now">When the root scope is seeded; null is the wall clock.</param>
    public OversightStore(string? file = null, DateTimeOffset? now = null)
    {
        File = Path.GetFullPath(file ?? DefaultFile);
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
            Refuse(FoundVersion());
            Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;");
            Migrate(now ?? DateTimeOffset.UtcNow);
        }
        catch (Exception)
        {
            _conn.Dispose();
            throw;
        }
    }

    void Refuse(int have)
    {
        if (have > Schema)
            throw new InvalidOperationException(
                $"the owner's oversight file at {File} was written by a newer TradeAgent (its layout is version {have}, this build "
                + $"understands {Schema}). Nothing has been read from it or written to it, and a newer TradeAgent will open it exactly as it was left.");
    }

    /// <summary>The version the file names, read without writing anything; zero for a file holding no oversight yet.</summary>
    int FoundVersion()
    {
        using (var exists = Cmd("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='oversight_meta'"))
            if (Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) return 0;
        using var c = Cmd("SELECT value FROM oversight_meta WHERE key='schema'");
        var value = c.ExecuteScalar();
        if (value is null or DBNull) return 0;
        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return n;
        throw new InvalidOperationException(
            $"the owner's oversight file at {File} names a layout version TradeAgent cannot read ('{text}'). Nothing has been read from it or written to it.");
    }

    /// <summary>
    /// THE LADDER. One rung, in one transaction with its version row, the version read again under the write lock so two opens of one new
    /// file do not both climb it. A later rung is an <c>if (have &lt; N)</c> below, written by the unit that needs it.
    /// </summary>
    void Migrate(DateTimeOffset now) => Write(() =>
    {
        var have = FoundVersion();
        Refuse(have);
        if (have < 1)
        {
            Exec("""
                CREATE TABLE IF NOT EXISTS oversight_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);

                CREATE TABLE IF NOT EXISTS oversight_scope(
                  id         TEXT PRIMARY KEY,
                  unit_id    TEXT NOT NULL,
                  created_at TEXT NOT NULL);

                CREATE TABLE IF NOT EXISTS oversight_finding(
                  seq       INTEGER PRIMARY KEY AUTOINCREMENT,
                  id        TEXT NOT NULL UNIQUE CHECK (id GLOB 'wf-*'),
                  detector  TEXT NOT NULL,
                  severity  TEXT NOT NULL CHECK (severity IN ('info', 'severe')),
                  scope_id  TEXT NOT NULL REFERENCES oversight_scope(id),
                  unit_id   TEXT NOT NULL,
                  unit_path TEXT NOT NULL,
                  subject   TEXT NOT NULL,
                  win       TEXT NOT NULL,
                  cites     TEXT NOT NULL,
                  measured  TEXT NOT NULL,
                  blind     TEXT NOT NULL,
                  at        TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_oversight_finding_scope ON oversight_finding(scope_id, seq);

                CREATE TABLE IF NOT EXISTS oversight_cursor(
                  seq     INTEGER PRIMARY KEY AUTOINCREMENT,
                  source  TEXT NOT NULL,
                  last_id INTEGER NOT NULL,
                  at      TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_oversight_cursor ON oversight_cursor(source, seq);

                CREATE TABLE IF NOT EXISTS oversight_digest(
                  seq         INTEGER PRIMARY KEY AUTOINCREMENT,
                  id          TEXT NOT NULL UNIQUE,
                  scope_id    TEXT NOT NULL REFERENCES oversight_scope(id),
                  at          TEXT NOT NULL,
                  text        TEXT NOT NULL,
                  findings    TEXT NOT NULL,
                  through_seq INTEGER NOT NULL);

                CREATE TABLE IF NOT EXISTS oversight_failure(
                  seq  INTEGER PRIMARY KEY AUTOINCREMENT,
                  at   TEXT NOT NULL,
                  what TEXT NOT NULL);

                CREATE TRIGGER IF NOT EXISTS oversight_scope_sealed_u BEFORE UPDATE ON oversight_scope
                BEGIN SELECT RAISE(ABORT, 'an oversight scope is never updated'); END;
                CREATE TRIGGER IF NOT EXISTS oversight_scope_sealed_d BEFORE DELETE ON oversight_scope
                BEGIN SELECT RAISE(ABORT, 'an oversight scope is never deleted'); END;
                CREATE TRIGGER IF NOT EXISTS oversight_finding_sealed_u BEFORE UPDATE ON oversight_finding
                BEGIN SELECT RAISE(ABORT, 'a finding is a measurement and never updated'); END;
                CREATE TRIGGER IF NOT EXISTS oversight_finding_sealed_d BEFORE DELETE ON oversight_finding
                BEGIN SELECT RAISE(ABORT, 'a finding is a measurement and never deleted'); END;
                CREATE TRIGGER IF NOT EXISTS oversight_cursor_sealed_u BEFORE UPDATE ON oversight_cursor
                BEGIN SELECT RAISE(ABORT, 'a cursor moves by a new row, never by an edit'); END;
                CREATE TRIGGER IF NOT EXISTS oversight_cursor_sealed_d BEFORE DELETE ON oversight_cursor
                BEGIN SELECT RAISE(ABORT, 'a cursor row is never deleted'); END;
                CREATE TRIGGER IF NOT EXISTS oversight_digest_sealed_u BEFORE UPDATE ON oversight_digest
                BEGIN SELECT RAISE(ABORT, 'a digest is never updated'); END;
                CREATE TRIGGER IF NOT EXISTS oversight_digest_sealed_d BEFORE DELETE ON oversight_digest
                BEGIN SELECT RAISE(ABORT, 'a digest is never deleted'); END;
                CREATE TRIGGER IF NOT EXISTS oversight_failure_sealed_u BEFORE UPDATE ON oversight_failure
                BEGIN SELECT RAISE(ABORT, 'a failure record is never updated'); END;
                CREATE TRIGGER IF NOT EXISTS oversight_failure_sealed_d BEFORE DELETE ON oversight_failure
                BEGIN SELECT RAISE(ABORT, 'a failure record is never deleted'); END;
                """);
            using (var seed = Cmd("INSERT INTO oversight_scope(id, unit_id, created_at) VALUES($id, $unit, $at) ON CONFLICT DO NOTHING",
                       ("$id", RootScope), ("$unit", OrgIds.Root), ("$at", Sql.T(now))))
                seed.ExecuteNonQuery();
            Exec("INSERT INTO oversight_meta(key, value) VALUES('schema', '1') ON CONFLICT(key) DO UPDATE SET value='1';");
        }
        return have;
    });

    // -------------------------------------------------------------------------------- scopes

    /// <summary>Every scope, root first.</summary>
    public IReadOnlyList<OversightScope> Scopes() => Read(() =>
    {
        using var c = Cmd("SELECT id, unit_id, created_at FROM oversight_scope ORDER BY (id = 'org') DESC, id");
        var rows = new List<OversightScope>();
        using var r = c.ExecuteReader();
        while (r.Read()) rows.Add(new(r.GetString(0), r.GetString(1), Sql.Time(r.GetString(2))));
        return (IReadOnlyList<OversightScope>)rows;
    });

    /// <summary>
    /// Adds a scope over <paramref name="unitId"/>'s subtree — a department's line (VISION § 5.2), the same detectors routed to one more
    /// scope. App-owned: no organisation verb creates, funds or reads one. A second add of the same id is nothing.
    /// </summary>
    public OversightScope AddScope(string id, string unitId, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(unitId);
        return Write(() =>
        {
            using (var c = Cmd("INSERT INTO oversight_scope(id, unit_id, created_at) VALUES($id, $unit, $at) ON CONFLICT DO NOTHING",
                       ("$id", id), ("$unit", unitId), ("$at", Sql.T(at))))
                c.ExecuteNonQuery();
            return Scopes_(id);
        });
    }

    OversightScope Scopes_(string id)
    {
        using var c = Cmd("SELECT id, unit_id, created_at FROM oversight_scope WHERE id=$id", ("$id", id));
        using var r = c.ExecuteReader();
        r.Read();
        return new(r.GetString(0), r.GetString(1), Sql.Time(r.GetString(2)));
    }

    // -------------------------------------------------------------------------------- findings and cursors

    /// <summary>
    /// WRITES ONE SWEEP: its findings, then its cursors, in one transaction — a crash leaves neither, and a re-run writes the same findings
    /// again, which their ids make nothing. Returns the findings this call wrote new, with their order in the file.
    /// </summary>
    public IReadOnlyList<OversightFinding> Commit(IReadOnlyList<OversightFinding> findings, IReadOnlyDictionary<string, long> cursors, DateTimeOffset at) =>
        Write(() =>
        {
            var added = new List<OversightFinding>();
            foreach (var f in findings)
            {
                if (f.Severity is not (OversightSeverity.Info or OversightSeverity.Severe))
                    throw new ArgumentException($"a finding's severity is info or severe, not '{f.Severity}'");
                foreach (var cite in f.Cites)
                    if (Array.IndexOf(OversightCite.Kinds, cite.Kind) < 0)
                        throw new ArgumentException($"a finding may not cite a '{cite.Kind}': the kinds are {string.Join(", ", OversightCite.Kinds)}");
                if (f.Id != OversightFinding.IdOf(f.Detector, f.ScopeId, f.Subject, f.Window))
                    throw new ArgumentException($"finding {f.Id} does not carry the id its detector, scope, subject and window make");
                using var c = Cmd("""
                    INSERT INTO oversight_finding(id, detector, severity, scope_id, unit_id, unit_path, subject, win, cites, measured, blind, at)
                    VALUES($id, $det, $sev, $scope, $unit, $path, $subject, $win, $cites, $measured, $blind, $at)
                    ON CONFLICT(id) DO NOTHING
                    RETURNING seq
                    """,
                    ("$id", f.Id), ("$det", f.Detector), ("$sev", f.Severity), ("$scope", f.ScopeId), ("$unit", f.UnitId),
                    ("$path", f.UnitPath), ("$subject", f.Subject), ("$win", f.Window),
                    ("$cites", JsonSerializer.Serialize(f.Cites.Select(x => x.ToString()))),
                    ("$measured", JsonSerializer.Serialize(new SortedDictionary<string, string>(f.Measured.ToDictionary(), StringComparer.Ordinal))),
                    ("$blind", f.Blind), ("$at", Sql.T(f.At)));
                if (c.ExecuteScalar() is long seq) added.Add(f with { Seq = seq });
            }
            foreach (var (source, last) in cursors)
            {
                if ((CursorOf(source) ?? 0) >= last) continue;
                using var c = Cmd("INSERT INTO oversight_cursor(source, last_id, at) VALUES($s, $l, $at)",
                    ("$s", source), ("$l", last), ("$at", Sql.T(at)));
                c.ExecuteNonQuery();
            }
            return (IReadOnlyList<OversightFinding>)added;
        });

    /// <summary>The last id read from <paramref name="source"/>, or null when it has never been read.</summary>
    public long? Cursor(string source) => Read(() => CursorOf(source));

    long? CursorOf(string source)
    {
        using var c = Cmd("SELECT MAX(last_id) FROM oversight_cursor WHERE source=$s", ("$s", source));
        return c.ExecuteScalar() is long l ? l : null;
    }

    /// <summary>The findings of one scope, or of every scope, in the order they were written.</summary>
    public IReadOnlyList<OversightFinding> Findings(string? scope = null, long afterSeq = 0) => Read(() =>
    {
        using var c = Cmd($"""
            SELECT seq, id, detector, severity, scope_id, unit_id, unit_path, subject, win, cites, measured, blind, at
              FROM oversight_finding WHERE seq > $after {(scope is null ? "" : "AND scope_id = $scope")} ORDER BY seq
            """, ("$after", afterSeq), ("$scope", scope));
        var rows = new List<OversightFinding>();
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            var cites = JsonSerializer.Deserialize<string[]>(r.GetString(9))!
                .Select(s => s.Split(':', 2)).Select(p => new OversightCite(p[0], p[1])).ToList();
            var measured = JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(10))!;
            rows.Add(new OversightFinding(r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
                r.GetString(7), r.GetString(8), cites, measured, r.GetString(11), Sql.Time(r.GetString(12))) { Seq = r.GetInt64(0) });
        }
        return (IReadOnlyList<OversightFinding>)rows;
    });

    /// <summary>Whether a finding with this id is in the file.</summary>
    public bool HasFinding(string id) => Read(() =>
    {
        using var c = Cmd("SELECT COUNT(*) FROM oversight_finding WHERE id=$id", ("$id", id));
        return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    });

    // -------------------------------------------------------------------------------- digests

    /// <summary>The last digest of <paramref name="scope"/>, or null.</summary>
    public OversightDigest? LastDigest(string scope) => Read(() => DigestsOf(scope).LastOrDefault());

    /// <summary>Every digest of <paramref name="scope"/>, oldest first.</summary>
    public IReadOnlyList<OversightDigest> Digests(string scope) => Read(() => DigestsOf(scope));

    IReadOnlyList<OversightDigest> DigestsOf(string scope)
    {
        using var c = Cmd("SELECT id, scope_id, at, text, findings, through_seq FROM oversight_digest WHERE scope_id=$s ORDER BY seq", ("$s", scope));
        var rows = new List<OversightDigest>();
        using var r = c.ExecuteReader();
        while (r.Read())
            rows.Add(new(r.GetString(0), r.GetString(1), Sql.Time(r.GetString(2)), r.GetString(3),
                JsonSerializer.Deserialize<string[]>(r.GetString(4))!, r.GetInt64(5)));
        return rows;
    }

    /// <summary>Appends a digest. Its caller ran <c>DigestCheck.Verify</c> first; a digest is never written unchecked.</summary>
    internal void AppendDigest(OversightDigest d) => Write(() =>
    {
        using var c = Cmd("""
            INSERT INTO oversight_digest(id, scope_id, at, text, findings, through_seq) VALUES($id, $scope, $at, $text, $f, $through)
            """, ("$id", d.Id), ("$scope", d.ScopeId), ("$at", Sql.T(d.At)), ("$text", d.Text),
            ("$f", JsonSerializer.Serialize(d.Findings)), ("$through", d.ThroughSeq));
        return c.ExecuteNonQuery();
    });

    // -------------------------------------------------------------------------------- its own failures

    /// <summary>Records one of the line's own failures IN THIS FILE — never the activity or engineering log.</summary>
    public void RecordFailure(DateTimeOffset at, string what) => Write(() =>
    {
        using var c = Cmd("INSERT INTO oversight_failure(at, what) VALUES($at, $what)", ("$at", Sql.T(at)), ("$what", what));
        return c.ExecuteNonQuery();
    });

    /// <summary>The line's own failures, oldest first.</summary>
    public IReadOnlyList<(DateTimeOffset At, string What)> Failures() => Read(() =>
    {
        using var c = Cmd("SELECT at, what FROM oversight_failure ORDER BY seq");
        var rows = new List<(DateTimeOffset, string)>();
        using var r = c.ExecuteReader();
        while (r.Read()) rows.Add((Sql.Time(r.GetString(0)), r.GetString(1)));
        return (IReadOnlyList<(DateTimeOffset, string)>)rows;
    });

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

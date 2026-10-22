using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using TradeAgent.Core.Db;

namespace TradeAgent.Core.Audit;

/// <summary>One accepted delivery the line drew: the delivery, its mandate and unit, the <c>accepted</c> event it was read at, and when.</summary>
public sealed record AuditSample(string DeliveryId, string MandateId, string UnitId, long EventId, DateTimeOffset At)
{
    /// <summary>The row's order in the file, assigned when written; zero on a sample not yet written.</summary>
    public long Seq { get; init; }
}

/// <summary>One code check of one sample: its name, whether it passed, and the app's own words why — never an agent's.</summary>
public sealed record AuditCheck(string DeliveryId, string Name, bool Passed, string Reason);

/// <summary>
/// ONE FINDING: a check of a sample that failed, as an APP MEASUREMENT — it cites the delivery, the mandate and the attempt that sealed it,
/// by id, and carries no report text and no other string an agent wrote. Its id is the SHA-256 of the delivery and the check, so the same
/// fact found twice is one row.
/// </summary>
public sealed record AuditFinding(string Id, string DeliveryId, string MandateId, string? AttemptId, string Check, string Severity, DateTimeOffset At)
{
    /// <summary>The id a finding of this delivery and check carries: <c>af-</c> and the SHA-256 of them.</summary>
    public static string IdOf(string deliveryId, string check) => "af-" + Sha256Hex.Of(deliveryId + "\n" + check);
}

/// <summary>
/// ONE FLAG: a sample a model reader would be wanted for, recorded with the fixed words that the audit agent did NOT run — it is dormant.
/// A flag launches nothing (BUDGET-001: the line spends nothing in part a).
/// </summary>
public sealed record AuditFlag(string DeliveryId, string MandateId, string? AttemptId, string What, DateTimeOffset At);

/// <summary>What one sweep writes, in one transaction: its samples with their checks, findings and flags, and the cursor it read to.</summary>
public sealed record AuditSweepWrite(
    IReadOnlyList<AuditSample> Samples, IReadOnlyList<AuditCheck> Checks, IReadOnlyList<AuditFinding> Findings,
    IReadOnlyList<AuditFlag> Flags, long? Cursor);

/// <summary>Severity words a finding carries.</summary>
public static class AuditSeverity
{
    public const string Info = "info";
    public const string Severe = "severe";
}

/// <summary>
/// THE PUBLIC AUDIT LINE'S OWN FILE (<c>U-org-audit-a</c>; ORCHESTRATION §§ 2.13, 3 — "the audit line's … own tables, outside
/// <c>org_*</c>"). The watcher's precedent: <b>never a rung of the app's database</b> — its own file <c>state/audit.db</c>, its own ladder
/// (<c>audit_meta</c> key <c>schema</c>), its own connection. Its path is this class's constant, NOT one of <see cref="Paths"/>, so any
/// reference to the file is a reference to this namespace — which no agent-reachable assembly makes (a guard holds that). An audit file
/// written by a newer build is refused with both versions named, nothing read from it or written to it.
///
/// <para><b>Measurement, never claim:</b> every row here is written by the line's own code. A finding cites ids; a check's reason is the
/// app's sentence. Nothing an agent wrote is copied in.</para>
///
/// <para><b>Append-only by the schema:</b> every table but the version row refuses UPDATE and DELETE by trigger. The cursor moves by a new
/// row, never by an edit. Its own failures are written HERE (<c>audit_failure</c>), never to the activity or engineering log, which the
/// organisation's reports can carry.</para>
///
/// <para><b>The salt</b> is 32 bytes from the operating system's generator, written once when the file is made and read by nothing but the
/// sampler: it is what makes the draw unpredictable to anyone in the organisation (<c>AuditSampler</c>).</para>
/// </summary>
public sealed class AuditStore : IDisposable
{
    /// <summary>The file's name under <see cref="Paths.State"/>. HERE, not in <see cref="Paths"/>, on purpose (see the class).</summary>
    public const string FileName = "audit.db";

    /// <summary>The layout version this build writes and understands.</summary>
    public const int Schema = 1;

    /// <summary>The bytes of the sampler's salt.</summary>
    public const int SaltBytes = 32;

    /// <summary>The cursor source: the app's <c>org_mandate_event</c> rows, read by id.</summary>
    public const string EventSource = "org_mandate_event";

    /// <summary>The product's file: <c>state/audit.db</c>.</summary>
    public static string DefaultFile => Path.Combine(Paths.State, FileName);

    readonly Lock _gate = new();
    readonly SqliteConnection _conn;

    /// <summary>The file this store holds open, canonical and absolute.</summary>
    public string File { get; }

    /// <summary>
    /// Opens the file, creating it if it is not there; refuses one written by a newer build, leaving it byte-for-byte as found. The order is
    /// the watcher's: the busy timeout, the version READ, and only for a file this build understands the journal mode and the ladder.
    /// </summary>
    /// <param name="file">Null is <see cref="DefaultFile"/>; a test passes its own.</param>
    /// <param name="now">When the salt is written; null is the wall clock.</param>
    public AuditStore(string? file = null, DateTimeOffset? now = null)
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
                $"the audit line's file at {File} was written by a newer TradeAgent (its layout is version {have}, this build "
                + $"understands {Schema}). Nothing has been read from it or written to it, and a newer TradeAgent will open it exactly as it was left.");
    }

    /// <summary>The version the file names, read without writing anything; zero for a file holding no audit yet.</summary>
    int FoundVersion()
    {
        using (var exists = Cmd("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='audit_meta'"))
            if (Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) return 0;
        using var c = Cmd("SELECT value FROM audit_meta WHERE key='schema'");
        var value = c.ExecuteScalar();
        if (value is null or DBNull) return 0;
        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return n;
        throw new InvalidOperationException(
            $"the audit line's file at {File} names a layout version TradeAgent cannot read ('{text}'). Nothing has been read from it or written to it.");
    }

    /// <summary>
    /// THE LADDER. Rung 1, in one transaction with its version row, the version read again under the write lock so two opens of one new
    /// file do not both climb it. Every statement creates only where missing and the salt is written only where none is, so a crash
    /// between the rung's statements and its stamp reopens cleanly, the salt it already wrote kept. A later rung is an
    /// <c>if (have &lt; N)</c> below, written by the unit that needs it.
    /// </summary>
    void Migrate(DateTimeOffset now) => Write(() =>
    {
        var have = FoundVersion();
        Refuse(have);
        if (have < 1)
        {
            Exec("""
                CREATE TABLE IF NOT EXISTS audit_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);

                CREATE TABLE IF NOT EXISTS audit_salt(
                  id   INTEGER PRIMARY KEY CHECK (id = 1),
                  salt BLOB NOT NULL CHECK (length(salt) = 32),
                  at   TEXT NOT NULL);

                CREATE TABLE IF NOT EXISTS audit_cursor(
                  seq     INTEGER PRIMARY KEY AUTOINCREMENT,
                  source  TEXT NOT NULL,
                  last_id INTEGER NOT NULL,
                  at      TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_audit_cursor ON audit_cursor(source, seq);

                CREATE TABLE IF NOT EXISTS audit_sample(
                  seq         INTEGER PRIMARY KEY AUTOINCREMENT,
                  delivery_id TEXT NOT NULL UNIQUE,
                  mandate_id  TEXT NOT NULL,
                  unit_id     TEXT NOT NULL,
                  event_id    INTEGER NOT NULL,
                  at          TEXT NOT NULL);

                CREATE TABLE IF NOT EXISTS audit_check(
                  delivery_id TEXT NOT NULL REFERENCES audit_sample(delivery_id),
                  check_name  TEXT NOT NULL,
                  passed      INTEGER NOT NULL CHECK (passed IN (0, 1)),
                  reason      TEXT NOT NULL,
                  PRIMARY KEY (delivery_id, check_name));

                CREATE TABLE IF NOT EXISTS audit_finding(
                  seq         INTEGER PRIMARY KEY AUTOINCREMENT,
                  id          TEXT NOT NULL UNIQUE CHECK (id GLOB 'af-*'),
                  delivery_id TEXT NOT NULL REFERENCES audit_sample(delivery_id),
                  mandate_id  TEXT NOT NULL,
                  attempt_id  TEXT,
                  check_name  TEXT NOT NULL,
                  severity    TEXT NOT NULL CHECK (severity IN ('info', 'severe')),
                  at          TEXT NOT NULL);

                CREATE TABLE IF NOT EXISTS audit_flag(
                  seq         INTEGER PRIMARY KEY AUTOINCREMENT,
                  delivery_id TEXT NOT NULL UNIQUE REFERENCES audit_sample(delivery_id),
                  mandate_id  TEXT NOT NULL,
                  attempt_id  TEXT,
                  what        TEXT NOT NULL,
                  at          TEXT NOT NULL);

                CREATE TABLE IF NOT EXISTS audit_failure(
                  seq  INTEGER PRIMARY KEY AUTOINCREMENT,
                  at   TEXT NOT NULL,
                  what TEXT NOT NULL);

                CREATE TRIGGER IF NOT EXISTS audit_salt_sealed_u BEFORE UPDATE ON audit_salt
                BEGIN SELECT RAISE(ABORT, 'the salt is written once and never updated'); END;
                CREATE TRIGGER IF NOT EXISTS audit_salt_sealed_d BEFORE DELETE ON audit_salt
                BEGIN SELECT RAISE(ABORT, 'the salt is never deleted'); END;
                CREATE TRIGGER IF NOT EXISTS audit_cursor_sealed_u BEFORE UPDATE ON audit_cursor
                BEGIN SELECT RAISE(ABORT, 'a cursor moves by a new row, never by an edit'); END;
                CREATE TRIGGER IF NOT EXISTS audit_cursor_sealed_d BEFORE DELETE ON audit_cursor
                BEGIN SELECT RAISE(ABORT, 'a cursor row is never deleted'); END;
                CREATE TRIGGER IF NOT EXISTS audit_sample_sealed_u BEFORE UPDATE ON audit_sample
                BEGIN SELECT RAISE(ABORT, 'a sample is never updated'); END;
                CREATE TRIGGER IF NOT EXISTS audit_sample_sealed_d BEFORE DELETE ON audit_sample
                BEGIN SELECT RAISE(ABORT, 'a sample is never deleted'); END;
                CREATE TRIGGER IF NOT EXISTS audit_check_sealed_u BEFORE UPDATE ON audit_check
                BEGIN SELECT RAISE(ABORT, 'a check result is a measurement and never updated'); END;
                CREATE TRIGGER IF NOT EXISTS audit_check_sealed_d BEFORE DELETE ON audit_check
                BEGIN SELECT RAISE(ABORT, 'a check result is a measurement and never deleted'); END;
                CREATE TRIGGER IF NOT EXISTS audit_finding_sealed_u BEFORE UPDATE ON audit_finding
                BEGIN SELECT RAISE(ABORT, 'a finding is a measurement and never updated'); END;
                CREATE TRIGGER IF NOT EXISTS audit_finding_sealed_d BEFORE DELETE ON audit_finding
                BEGIN SELECT RAISE(ABORT, 'a finding is a measurement and never deleted'); END;
                CREATE TRIGGER IF NOT EXISTS audit_flag_sealed_u BEFORE UPDATE ON audit_flag
                BEGIN SELECT RAISE(ABORT, 'a flag is never updated'); END;
                CREATE TRIGGER IF NOT EXISTS audit_flag_sealed_d BEFORE DELETE ON audit_flag
                BEGIN SELECT RAISE(ABORT, 'a flag is never deleted'); END;
                CREATE TRIGGER IF NOT EXISTS audit_failure_sealed_u BEFORE UPDATE ON audit_failure
                BEGIN SELECT RAISE(ABORT, 'a failure record is never updated'); END;
                CREATE TRIGGER IF NOT EXISTS audit_failure_sealed_d BEFORE DELETE ON audit_failure
                BEGIN SELECT RAISE(ABORT, 'a failure record is never deleted'); END;
                """);
            using (var seed = Cmd("INSERT INTO audit_salt(id, salt, at) VALUES(1, $salt, $at) ON CONFLICT(id) DO NOTHING",
                       ("$salt", RandomNumberGenerator.GetBytes(SaltBytes)), ("$at", Sql.T(now))))
                seed.ExecuteNonQuery();
            Exec("INSERT INTO audit_meta(key, value) VALUES('schema', '1') ON CONFLICT(key) DO UPDATE SET value='1';");
        }
        return have;
    });

    // -------------------------------------------------------------------------------- the salt

    /// <summary>The sampler's salt. Read by <c>AuditSampler</c> and nothing else; never shown, logged or copied out.</summary>
    internal byte[] Salt() => Read(() =>
    {
        using var c = Cmd("SELECT salt FROM audit_salt WHERE id = 1");
        return (byte[])c.ExecuteScalar()!;
    });

    // -------------------------------------------------------------------------------- one sweep

    /// <summary>
    /// WRITES ONE SWEEP: its samples, their checks, findings and flags, then its cursor, in one transaction — a crash leaves none of it,
    /// and a re-run draws the same samples again, which their keys make nothing. Returns the samples this call wrote new.
    /// </summary>
    public IReadOnlyList<AuditSample> Commit(AuditSweepWrite sweep, DateTimeOffset at) => Write(() =>
    {
        var added = new List<AuditSample>();
        var fresh = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in sweep.Samples)
        {
            using var c = Cmd("""
                INSERT INTO audit_sample(delivery_id, mandate_id, unit_id, event_id, at) VALUES($d, $m, $u, $e, $at)
                ON CONFLICT(delivery_id) DO NOTHING
                RETURNING seq
                """, ("$d", s.DeliveryId), ("$m", s.MandateId), ("$u", s.UnitId), ("$e", s.EventId), ("$at", Sql.T(s.At)));
            if (c.ExecuteScalar() is long seq)
            {
                added.Add(s with { Seq = seq });
                fresh.Add(s.DeliveryId);
            }
        }
        // ONLY A SAMPLE THIS CALL DREW carries this call's checks, findings and flags: a sample drawn before keeps what it was found to be.
        foreach (var k in sweep.Checks.Where(k => fresh.Contains(k.DeliveryId)))
        {
            using var c = Cmd("INSERT INTO audit_check(delivery_id, check_name, passed, reason) VALUES($d, $n, $p, $r)",
                ("$d", k.DeliveryId), ("$n", k.Name), ("$p", k.Passed ? 1 : 0), ("$r", k.Reason));
            c.ExecuteNonQuery();
        }
        foreach (var f in sweep.Findings.Where(f => fresh.Contains(f.DeliveryId)))
        {
            if (f.Severity is not (AuditSeverity.Info or AuditSeverity.Severe))
                throw new ArgumentException($"a finding's severity is info or severe, not '{f.Severity}'");
            if (f.Id != AuditFinding.IdOf(f.DeliveryId, f.Check))
                throw new ArgumentException($"finding {f.Id} does not carry the id its delivery and check make");
            using var c = Cmd("""
                INSERT INTO audit_finding(id, delivery_id, mandate_id, attempt_id, check_name, severity, at)
                VALUES($id, $d, $m, $a, $n, $s, $at) ON CONFLICT(id) DO NOTHING
                """, ("$id", f.Id), ("$d", f.DeliveryId), ("$m", f.MandateId), ("$a", f.AttemptId), ("$n", f.Check), ("$s", f.Severity),
                ("$at", Sql.T(f.At)));
            c.ExecuteNonQuery();
        }
        foreach (var g in sweep.Flags.Where(g => fresh.Contains(g.DeliveryId)))
        {
            using var c = Cmd("""
                INSERT INTO audit_flag(delivery_id, mandate_id, attempt_id, what, at) VALUES($d, $m, $a, $w, $at)
                ON CONFLICT(delivery_id) DO NOTHING
                """, ("$d", g.DeliveryId), ("$m", g.MandateId), ("$a", g.AttemptId), ("$w", g.What), ("$at", Sql.T(g.At)));
            c.ExecuteNonQuery();
        }
        if (sweep.Cursor is { } last && (CursorOf(EventSource) ?? 0) < last)
        {
            using var c = Cmd("INSERT INTO audit_cursor(source, last_id, at) VALUES($s, $l, $at)",
                ("$s", EventSource), ("$l", last), ("$at", Sql.T(at)));
            c.ExecuteNonQuery();
        }
        return (IReadOnlyList<AuditSample>)added;
    });

    /// <summary>The last id read from <paramref name="source"/>, or null when it has never been read.</summary>
    public long? Cursor(string source = EventSource) => Read(() => CursorOf(source));

    long? CursorOf(string source)
    {
        using var c = Cmd("SELECT MAX(last_id) FROM audit_cursor WHERE source=$s", ("$s", source));
        return c.ExecuteScalar() is long l ? l : null;
    }

    // -------------------------------------------------------------------------------- reads

    /// <summary>Every sample, in the order drawn.</summary>
    public IReadOnlyList<AuditSample> Samples() => Read(() =>
    {
        using var c = Cmd("SELECT seq, delivery_id, mandate_id, unit_id, event_id, at FROM audit_sample ORDER BY seq");
        var rows = new List<AuditSample>();
        using var r = c.ExecuteReader();
        while (r.Read()) rows.Add(new AuditSample(r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), Sql.Time(r.GetString(5))) { Seq = r.GetInt64(0) });
        return (IReadOnlyList<AuditSample>)rows;
    });

    /// <summary>The checks of one sample, or of every sample, in the order written.</summary>
    public IReadOnlyList<AuditCheck> Checks(string? deliveryId = null) => Read(() =>
    {
        using var c = Cmd($"SELECT delivery_id, check_name, passed, reason FROM audit_check {(deliveryId is null ? "" : "WHERE delivery_id=$d")} ORDER BY rowid",
            ("$d", deliveryId));
        var rows = new List<AuditCheck>();
        using var r = c.ExecuteReader();
        while (r.Read()) rows.Add(new AuditCheck(r.GetString(0), r.GetString(1), r.GetInt64(2) != 0, r.GetString(3)));
        return (IReadOnlyList<AuditCheck>)rows;
    });

    /// <summary>Every finding, in the order written.</summary>
    public IReadOnlyList<AuditFinding> Findings() => Read(() =>
    {
        using var c = Cmd("SELECT id, delivery_id, mandate_id, attempt_id, check_name, severity, at FROM audit_finding ORDER BY seq");
        var rows = new List<AuditFinding>();
        using var r = c.ExecuteReader();
        while (r.Read())
            rows.Add(new AuditFinding(r.GetString(0), r.GetString(1), r.GetString(2), Sql.S(r.GetValue(3)), r.GetString(4), r.GetString(5),
                Sql.Time(r.GetString(6))));
        return (IReadOnlyList<AuditFinding>)rows;
    });

    /// <summary>Every flag, in the order written.</summary>
    public IReadOnlyList<AuditFlag> Flags() => Read(() =>
    {
        using var c = Cmd("SELECT delivery_id, mandate_id, attempt_id, what, at FROM audit_flag ORDER BY seq");
        var rows = new List<AuditFlag>();
        using var r = c.ExecuteReader();
        while (r.Read()) rows.Add(new AuditFlag(r.GetString(0), r.GetString(1), Sql.S(r.GetValue(2)), r.GetString(3), Sql.Time(r.GetString(4))));
        return (IReadOnlyList<AuditFlag>)rows;
    });

    // -------------------------------------------------------------------------------- its own failures

    /// <summary>Records one of the line's own failures IN THIS FILE — never the activity or engineering log.</summary>
    public void RecordFailure(DateTimeOffset at, string what) => Write(() =>
    {
        using var c = Cmd("INSERT INTO audit_failure(at, what) VALUES($at, $what)", ("$at", Sql.T(at)), ("$what", what));
        return c.ExecuteNonQuery();
    });

    /// <summary>The line's own failures, oldest first.</summary>
    public IReadOnlyList<(DateTimeOffset At, string What)> Failures() => Read(() =>
    {
        using var c = Cmd("SELECT at, what FROM audit_failure ORDER BY seq");
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

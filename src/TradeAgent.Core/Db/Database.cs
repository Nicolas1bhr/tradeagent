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

using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace TradeAgent.Core.Audit;

/// <summary>
/// THE REGISTER OF APPROVED EVIDENCE AND ITS ONE READER (<c>U-org-audit-a</c>; ORCHESTRATION § 2.13 — "it reads approved evidence, never
/// hidden reasoning"). The line reads the app's database through THIS class only, on a <c>Mode=ReadOnly</c>, <c>query_only</c> connection —
/// the watcher's reader pattern — and the engine itself holds the register: an authorizer on the connection refuses any statement that reads
/// a table not in <see cref="Tables"/>, and every write, schema change, pragma or attach, before it runs. So what the line can see is this
/// list, not whatever a statement happens to name.
///
/// <para><b>Approved</b> is the app's own records: the mandate, its events, its sealed deliveries and the app's checks on them; the launch
/// ledger and its funding chain; the research ledger's entries; strategy versions, runs and promotions; boundary events; sealed
/// publications; the chart's units and positions.</para>
///
/// <para><b>Excluded, and never added</b> (ORCHESTRATION § 2.13, "never hidden reasoning"): the seat homes' session transcripts; the
/// workspaces and the drafts in them; an agent's raw tool input in the tool-call ledger; and the watcher's own file — the owner's private
/// line is invisible, and a finding of this line reaches heads (part c), so it must never rest on the watcher. None is in the app's
/// database except the tool-call ledger, which the register leaves out; the others are files this namespace never names.</para>
/// </summary>
public sealed class AuditEvidence : IDisposable
{
    /// <summary>
    /// THE CLOSED LIST of tables the line may read. <c>strategy_version</c> is here beside the brief's list because a delivery's
    /// <c>version</c> and <c>run</c> refs resolve through it — its id and the launch that wrote it.
    /// </summary>
    public static readonly IReadOnlyList<string> Tables =
    [
        "org_mandate", "org_mandate_event", "org_delivery", "org_check", "ai_attempt", "ai_attempt_funding", "ledger_entry",
        "strategy_version", "strategy_run", "strategy_promotion", "boundary_event", "publication", "org_unit", "org_position",
    ];

    static readonly HashSet<string> Registered = new(Tables, StringComparer.Ordinal);

    readonly SqliteConnection _conn;
    readonly ConcurrentDictionary<string, byte> _read = new(StringComparer.Ordinal);
    readonly strdelegate_authorizer _authorizer;

    /// <summary>Every table a statement through this reader has read — always a subset of <see cref="Tables"/>.</summary>
    public IReadOnlyCollection<string> TablesRead => [.. _read.Keys];

    /// <summary>Opens the app's database READ-ONLY, <c>query_only</c>, with the register's authorizer on the connection.</summary>
    public AuditEvidence(string databaseFile)
    {
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databaseFile),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        try
        {
            _conn.Open();
            using (var c = _conn.CreateCommand())
            {
                c.CommandText = "PRAGMA busy_timeout=5000; PRAGMA query_only=ON;";
                c.ExecuteNonQuery();
            }
            _authorizer = Authorize;
            var rc = raw.sqlite3_set_authorizer(_conn.Handle, _authorizer, null);
            if (rc != raw.SQLITE_OK)
                throw new InvalidOperationException($"the audit line could not hold its register on the database (sqlite {rc}); it reads nothing without it.");
        }
        catch (Exception)
        {
            _conn.Dispose();
            throw;
        }
    }

    /// <summary>
    /// THE ENGINE'S RULE: a read of a registered table, a SELECT, a function, a recursive query and a read transaction pass; everything
    /// else — a read of any other table, an insert, update or delete, a create, drop or alter, a pragma, an attach — is refused.
    /// </summary>
    int Authorize(object userData, int action, string param0, string param1, string dbName, string trigger)
    {
        switch (action)
        {
            case raw.SQLITE_READ:
                // THE MAIN DATABASE ONLY (the provider may hand its name as null): nothing can be attached, and no temp table made.
                if (dbName is not (null or "main") || !Registered.Contains(param0)) return raw.SQLITE_DENY;
                _read.TryAdd(param0, 0);
                return raw.SQLITE_OK;
            case raw.SQLITE_SELECT or raw.SQLITE_FUNCTION or raw.SQLITE_RECURSIVE or raw.SQLITE_TRANSACTION:
                return raw.SQLITE_OK;
            default:
                return raw.SQLITE_DENY;
        }
    }

    /// <summary>Runs <paramref name="body"/> in ONE read transaction, so every row it reads is of one instant.</summary>
    internal T InOneRead<T>(Func<T> body)
    {
        using var tx = _conn.BeginTransaction(deferred: true);
        var result = body();
        tx.Commit();
        return result;
    }

    /// <summary>The rows one statement answers, each as its columns' values (null for SQL NULL). Refused by the engine outside the register.</summary>
    internal IReadOnlyList<object?[]> Rows(string sql, params (string Name, object? Value)[] ps)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = sql;
        foreach (var (name, value) in ps) c.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var rows = new List<object?[]>();
        using var r = c.ExecuteReader();
        while (r.Read())
            rows.Add([.. Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? null : r.GetValue(i))]);
        return rows;
    }

    public void Dispose() => _conn.Dispose();
}

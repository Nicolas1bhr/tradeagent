using Microsoft.Data.Sqlite;

namespace TradeAgent.Core.Oversight;

/// <summary>What one sweep did: the findings it wrote new, the digests it wrote, and the failure it recorded instead, if any.</summary>
public sealed record OversightSweepResult(IReadOnlyList<OversightFinding> Added, IReadOnlyList<OversightDigest> Digests, string? Failure);

/// <summary>
/// ONE SWEEP OF THE OWNER'S LINE (<c>U-org-watcher-a</c>): reads the app's database READ-ONLY (<see cref="OrgSnapshot"/>), runs every
/// detector over every scope, appends what is new to <c>oversight.db</c>, and composes a digest per scope when it has a finding newer than
/// the last digest — checked by <see cref="DigestCheck"/> before it is written.
///
/// <para><b>App code, never agent-runtime code:</b> run from the app's own timer (<see cref="OversightLine"/>), never from
/// <c>MissionLoop</c>; it raises no wake, writes no activity, engineering, publication or delivery row, moves no total and spends nothing
/// (no <c>ai_attempt</c> row — BUDGET-001). It has no authority: nothing here instructs, pauses or blocks anything. Idempotent: a finding's
/// id is its fact's hash and cursors move forward only, so a restart re-sweeps to the same rows.</para>
/// </summary>
public sealed class OversightSweep(string databaseFile, OversightStore store, IReadOnlyList<OversightDetector>? detectors = null)
{
    readonly IReadOnlyList<OversightDetector> _detectors = detectors ?? OversightDetector.All;

    /// <summary>The app's database this sweep reads.</summary>
    public string DatabaseFile { get; } = Path.GetFullPath(databaseFile);

    /// <summary>
    /// Runs one sweep at <paramref name="now"/>. Never throws for a failure of its own: that is written to the oversight file's own failure
    /// table and returned, and nothing anywhere else.
    /// </summary>
    public OversightSweepResult Run(DateTimeOffset now)
    {
        try
        {
            var cursors = new Dictionary<string, long?>
            {
                [OrgSnapshot.ToolCallSource] = store.Cursor(OrgSnapshot.ToolCallSource),
                [OrgSnapshot.PipeLogSource] = store.Cursor(OrgSnapshot.PipeLogSource),
            };
            var snapshot = OrgSnapshot.Read(DatabaseFile, now, cursors);
            var window = new OversightWindow(now);
            var scopes = store.Scopes();
            var found = scopes.SelectMany(scope => _detectors.SelectMany(d => d.Detect(snapshot, scope, window))).ToList();
            var added = store.Commit(found, snapshot.Cursors, now);

            var digests = new List<OversightDigest>();
            using var records = new OrgRecords(DatabaseFile);
            foreach (var scope in scopes)
            {
                var fresh = store.Findings(scope.Id, store.LastDigest(scope.Id)?.ThroughSeq ?? 0);
                if (DigestComposer.Compose(scope.Id, fresh, now) is not { } digest) continue;
                if (DigestCheck.Verify(digest.Text, cite => cite.Kind == "wf" ? store.HasFinding(cite.Id) : records.Resolves(cite)) is { } refused)
                {
                    store.RecordFailure(now, $"a digest for scope {scope.Id} was refused and not written: {refused}");
                    continue;
                }
                store.AppendDigest(digest);
                digests.Add(digest);
            }
            return new OversightSweepResult(added, digests, null);
        }
        catch (Exception ex)
        {
            var what = $"{ex.GetType().Name}: {ex.Message.ReplaceLineEndings(" ")}";
            try { store.RecordFailure(now, "a sweep failed: " + what); }
            catch (Exception) { /* the file itself cannot be written; nothing else is told */ }
            return new OversightSweepResult([], [], what);
        }
    }
}

/// <summary>
/// RESOLVES A CITE AGAINST THE APP'S DATABASE, read-only: whether the record a <c>kind:id</c> names exists. One query per kind of the closed
/// list (<see cref="OversightCite.Kinds"/>); any other kind resolves to nothing.
/// </summary>
public sealed class OrgRecords : IDisposable
{
    static readonly IReadOnlyDictionary<string, string> Lookups = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["attempt"] = "SELECT 1 FROM ai_attempt WHERE id = $id",
        ["mandate"] = "SELECT 1 FROM org_mandate WHERE id = $id",
        ["delivery"] = "SELECT 1 FROM org_delivery WHERE id = $id",
        ["check"] = "SELECT 1 FROM org_check WHERE delivery_id || '/' || check_name = $id",
        ["ledger"] = "SELECT 1 FROM ledger_entry WHERE CAST(id AS TEXT) = $id",
        ["run"] = "SELECT 1 FROM strategy_run WHERE id = $id",
        ["promotion"] = "SELECT 1 FROM strategy_promotion WHERE id = $id",
        ["event"] = "SELECT 1 FROM mission_event WHERE id = $id",
        ["tool_call"] = "SELECT 1 FROM tool_call WHERE CAST(id AS TEXT) = $id",
        ["log"] = "SELECT 1 FROM engineering_log WHERE CAST(id AS TEXT) = $id",
    };

    readonly SqliteConnection _conn;

    public OrgRecords(string databaseFile)
    {
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        _conn.Open();
        using var c = _conn.CreateCommand();
        c.CommandText = "PRAGMA busy_timeout=5000; PRAGMA query_only=ON;";
        c.ExecuteNonQuery();
    }

    /// <summary>Whether the record <paramref name="cite"/> names is in the app's database.</summary>
    public bool Resolves(OversightCite cite)
    {
        if (!Lookups.TryGetValue(cite.Kind, out var sql)) return false;
        using var c = _conn.CreateCommand();
        c.CommandText = sql;
        c.Parameters.AddWithValue("$id", cite.Id);
        return c.ExecuteScalar() is not null;
    }

    public void Dispose() => _conn.Dispose();
}

/// <summary>
/// THE LINE ON THE APP'S OWN TIMER (<c>U-org-watcher-a</c>): opens <c>state/oversight.db</c> and sweeps every
/// <see cref="Cadence"/>. The cadence is this unit's own design choice — the app has no shared timer to hook (its one loop is
/// a 5-second delay); a minute bounds how stale a severe finding can be at almost no cost, since layer 1 spends nothing.
///
/// <para>A file it will not open (a newer layout) is held in <see cref="Refusal"/> for the owner's page — never the activity log — and the
/// app runs on without the line.</para>
/// </summary>
public sealed class OversightLine : IAsyncDisposable
{
    /// <summary>How often the line sweeps.</summary>
    public static readonly TimeSpan Cadence = TimeSpan.FromSeconds(60);

    readonly OversightStore? _store;
    readonly OversightSweep? _sweep;
    readonly CancellationTokenSource _stop = new();
    Task? _running;

    /// <summary>Why the line is not running, or null when it is.</summary>
    public string? Refusal { get; }

    /// <summary>Opens the line over <paramref name="databaseFile"/>, writing to <paramref name="oversightFile"/> (null: <see cref="OversightStore.DefaultFile"/>).</summary>
    public OversightLine(string databaseFile, string? oversightFile = null)
    {
        try
        {
            _store = new OversightStore(oversightFile);
            _sweep = new OversightSweep(databaseFile, _store);
        }
        catch (Exception ex)
        {
            Refusal = ex.Message.ReplaceLineEndings(" ");
        }
    }

    /// <summary>Starts the timer. The first sweep is one cadence after the start, so the app's own start is never delayed by it.</summary>
    public void Start()
    {
        if (_sweep is null || _running is not null) return;
        _running = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(Cadence);
            try
            {
                while (await timer.WaitForNextTickAsync(_stop.Token)) _sweep.Run(DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) { }
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_running is not null)
        {
            try { await _running.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) { /* over the bound: the quit goes on */ }
        }
        _stop.Dispose();
        _store?.Dispose();
    }
}

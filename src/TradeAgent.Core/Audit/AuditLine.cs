namespace TradeAgent.Core.Audit;

/// <summary>
/// THE PUBLIC AUDIT LINE ON THE APP'S OWN TIMER (<c>U-org-audit-a</c>): opens <c>state/audit.db</c> and sweeps every <see cref="Cadence"/>.
/// It opens no file but the app's database, read-only through <see cref="AuditEvidence"/>, and its own. App code: never the mission loop,
/// never a wake, never a launch; it writes nothing the organisation reads, so it can neither instruct nor block (ORCHESTRATION § 2.13).
///
/// <para>A file it will not open (a newer layout) is held in <see cref="Refusal"/> — never told the activity log — and the app runs on
/// without the line. Its findings are shown nowhere until part c (seat A's ruling R4).</para>
/// </summary>
public sealed class AuditLine : IAsyncDisposable
{
    /// <summary>How often the line sweeps: a minute, the watcher's cadence — the code checks spend nothing.</summary>
    public static readonly TimeSpan Cadence = TimeSpan.FromSeconds(60);

    readonly AuditStore? _store;
    readonly AuditSweep? _sweep;
    readonly CancellationTokenSource _stop = new();
    Task? _running;

    /// <summary>Why the line is not running, or null when it is.</summary>
    public string? Refusal { get; }

    /// <summary>Opens the line over <paramref name="databaseFile"/>, writing to <paramref name="auditFile"/> (null: <see cref="AuditStore.DefaultFile"/>).</summary>
    public AuditLine(string databaseFile, string? auditFile = null)
    {
        try
        {
            _store = new AuditStore(auditFile);
            _sweep = new AuditSweep(databaseFile, _store);
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

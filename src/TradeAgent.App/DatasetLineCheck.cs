using TradeAgent.Core.Db;

namespace TradeAgent.App;

/// <summary>
/// THE SETTINGS PAGE'S CHECK OF THE NEWEST DATASET, OFF THE PAGE'S THREAD AND NOT ON EVERY TICK (<c>U-perf-scan</c>; PREFLIGHT-2).
///
/// <para>The line under "Market data" says what the newest dataset holds, VERIFIED as it is read: <see cref="DatasetStore.Checked"/>
/// re-hashes every raw month and the normalised file. That ran on the UI thread on every five-second refresh, and 36 months of minute
/// bars (D12) is three times the bytes it was. Now a check runs on a worker, at most one at a time, when the newest dataset is a
/// different one and otherwise once every <see cref="Every"/>; the page shows the last check's answer meanwhile. Nothing is decided by
/// this line: every reader that USES a dataset — a backtest, a sizing — checks it itself, as it always did.</para>
/// </summary>
/// <param name="check">The check: <see cref="DatasetStore.Checked"/> in the app.</param>
/// <param name="now">The clock the interval is measured on.</param>
/// <param name="post">How a finished check's answer reaches the page's thread: the UI dispatcher in the app.</param>
public sealed class DatasetLineCheck(Func<DatasetRecord, DatasetRecord> check, Func<DateTimeOffset> now, Action<Action> post)
{
    /// <summary>How often an unchanged newest dataset is checked again.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    /// <summary>What the line can say: the dataset as last checked, a check that failed, or nothing yet for this dataset.</summary>
    public readonly record struct Answer(DatasetRecord? Set, bool Failed)
    {
        public bool Pending => Set is null && !Failed;
    }

    DatasetRecord? _set;
    bool _failed;
    long _id = -1;
    DateTimeOffset _at;
    bool _running;

    /// <summary>How many checks this has started; for the tests that hold it to one per interval.</summary>
    public int Started { get; private set; }

    /// <summary>
    /// The answer for <paramref name="newest"/> as last checked, starting a check on a worker when one is due; called on the page's
    /// thread, and never waits for the check. <paramref name="changed"/> is called on that thread when a check finishes.
    /// </summary>
    public Answer Read(DatasetRecord newest, Action changed)
    {
        var at = now();
        if (!_running && (newest.Id != _id || at - _at >= Every))
        {
            _running = true;
            Started++;
            _ = Task.Run(() =>
            {
                DatasetRecord? found = null;
                try { found = check(newest); }
                catch (Exception) { found = null; }
                post(() =>
                {
                    _running = false;
                    (_set, _failed, _id, _at) = (found, found is null, newest.Id, at);
                    changed();
                });
            });
        }
        return newest.Id == _id ? new Answer(_set, _failed) : new Answer(null, false);
    }
}

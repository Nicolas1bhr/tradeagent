using System.Collections.Concurrent;
using TradeAgent.App;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE SETTINGS PAGE'S DATASET LINE (<c>U-perf-scan</c>; PREFLIGHT-2): the newest dataset is re-hashed on a worker, never on the
/// page's thread, and at most once a minute while it stays the newest — not on every five-second refresh.
/// </summary>
public class DatasetLineCheckTests
{
    static DatasetRecord Set(long id) => new(id, "test", "BTCUSDT", "1m", "v", 1, 1, [], "n.csv", "00", 10, null, null, 0, [], false,
        0, 0, 0, DateTimeOffset.UnixEpoch, DatasetState.ACCEPTED, null, []);

    /// <summary>The page's thread, as a queue the test drains: what a finished check posts runs only when the test says so.</summary>
    sealed class PageThread
    {
        readonly BlockingCollection<Action> _posted = new();
        public void Post(Action a) => _posted.Add(a);
        public void RunOne() { Assert.True(_posted.TryTake(out var a, TimeSpan.FromSeconds(10)), "no check finished"); a!(); }
    }

    [Fact]
    public void A_refresh_never_waits_for_the_hash_and_the_hash_runs_off_the_pages_thread()
    {
        var page = new PageThread();
        using var gate = new ManualResetEventSlim();
        var hashedOn = -1;
        var check = new DatasetLineCheck(s => { hashedOn = Environment.CurrentManagedThreadId; gate.Wait(TimeSpan.FromSeconds(10)); return s; },
            () => DateTimeOffset.UnixEpoch, page.Post);

        var answer = check.Read(Set(1), () => { });

        Assert.True(answer.Pending, "the first refresh answers before the dataset's files are read");
        gate.Set();
        page.RunOne();
        Assert.NotEqual(Environment.CurrentManagedThreadId, hashedOn);
        Assert.Equal(1, check.Read(Set(1), () => { }).Set?.Id);
    }

    [Fact]
    public void Twelve_refreshes_in_a_minute_check_the_same_dataset_once_and_a_new_dataset_at_once()
    {
        var page = new PageThread();
        var clock = DateTimeOffset.UnixEpoch;
        var checks = 0;
        var check = new DatasetLineCheck(s => { Interlocked.Increment(ref checks); return s; }, () => clock, page.Post);

        check.Read(Set(1), () => { });
        page.RunOne();
        for (var i = 1; i < 12; i++)
        {
            clock = clock.AddSeconds(5);
            Assert.Equal(1, check.Read(Set(1), () => { }).Set?.Id);
        }
        Assert.Equal(1, check.Started);

        Assert.True(check.Read(Set(2), () => { }).Pending, "a newly collected dataset is not described by the last one's check");
        page.RunOne();
        Assert.Equal(2, check.Started);

        clock = clock.AddMinutes(1);
        check.Read(Set(2), () => { });
        page.RunOne();
        Assert.Equal((3, 3), (check.Started, Volatile.Read(ref checks)));
    }

    [Fact]
    public void A_check_that_throws_says_it_could_not_be_read()
    {
        var page = new PageThread();
        var check = new DatasetLineCheck(_ => throw new IOException("held"), () => DateTimeOffset.UnixEpoch, page.Post);
        check.Read(Set(1), () => { });
        page.RunOne();
        Assert.True(check.Read(Set(1), () => { }).Failed);
    }

    [Fact]
    public void A_dataset_the_ledger_rejected_inside_the_minute_is_said_rejected_at_once_not_from_the_last_check()
    {
        var page = new PageThread();
        var check = new DatasetLineCheck(s => s, () => DateTimeOffset.UnixEpoch, page.Post);
        check.Read(Set(1), () => { });
        page.RunOne();
        Assert.Equal(DatasetState.ACCEPTED, check.Read(Set(1), () => { }).Set?.State);

        var rejected = Set(1) with { State = DatasetState.REJECTED, RejectedReason = "a backtest found a file changed" };
        Assert.Equal(DatasetState.REJECTED, check.Read(rejected, () => { }).Set?.State);
    }
}

using System.Globalization;
using System.Net;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Provisioning;
using TradeAgent.Tests;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE COLLECTOR AGAINST A LOOPBACK LISTENER, NEVER AGAINST THE VENDOR. Every test here points
/// <see cref="ForwardBarCollector"/> at <see cref="FakeArchive"/>, whose <c>PublishAt</c> answers an
/// arbitrary path and ignores the query string — which is exactly the shape a klines endpoint needs.
///
/// <para>The timer and the clock are injected, so a day of minutes takes milliseconds and no test in
/// this suite ever waits for a real one. <c>U-archive-win</c> paid for that rule: a hung loopback
/// request against a thirty-minute client leash reads as a thirty-minute test suite on Windows.</para>
/// </summary>
public class ForwardBarCollectorTests(ITestOutputHelper log)
{
    const string Symbol = "BTCUSDT";
    const string Path = "/api/v3/klines";

    static readonly DateTimeOffset Noon = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Seconds, not minutes: a test that waits a real interval is a test nobody runs.</summary>
    static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(5);

    /// <summary>One kline row in the vendor's own twelve-column shape, milliseconds.</summary>
    static string Row(int minute, decimal close = 100.5m)
    {
        var open = Noon.AddMinutes(minute).ToUnixTimeMilliseconds();
        var shut = Noon.AddMinutes(minute + 1).AddMilliseconds(-1).ToUnixTimeMilliseconds();
        return $"[{open},\"100.00\",\"101.00\",\"99.00\",\"{close.ToString(CultureInfo.InvariantCulture)}\","
               + $"\"1.50\",{shut},\"150.00\",10,\"0.75\",\"75.00\",\"0\"]";
    }

    static string Klines(params int[] minutes) => "[" + string.Join(",", minutes.Select(m => Row(m))) + "]";

    static ForwardBarCollector Collector(
        Database db, FakeArchive host, DateTimeOffset now, bool enabled = true,
        Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(db, () => Symbol, () => enabled,
            baseUrl: host.BaseUrl,
            requestTimeout: TimeSpan.FromSeconds(5),
            interval: Tick,
            now: () => now,
            delay: delay);

    /// <summary>
    /// THE WHOLE PATH, ONCE: a canned answer becomes closed bars in the ledger, the open minute does
    /// not, and the runner's event is raised for each bar actually stored.
    /// </summary>
    [Fact]
    public async Task One_look_stores_the_closed_bars_and_raises_one_event_for_each()
    {
        using var db = TestEnv.NewDb();
        using var host = new FakeArchive();
        host.PublishAt(Path, Klines(0, 1, 2));

        // Thirty seconds into minute 2: minutes 0 and 1 have closed and minute 2 has not.
        await using var collector = Collector(db, host, Noon.AddMinutes(2).AddSeconds(30));

        var seen = new List<DateTimeOffset>();
        collector.BarClosed += (s, at) => { Assert.Equal(Symbol, s); seen.Add(at); };

        var result = await collector.CollectOnceAsync();
        log.WriteLine(string.Join("\n", host.Marks));

        Assert.NotNull(result);
        Assert.Equal(2, result!.Stored);
        Assert.Equal(1, result.NotClosedYet);
        Assert.Equal([Noon, Noon.AddMinutes(1)], seen);
        Assert.Null(collector.LastError);

        var store = new ForwardBarStore(db);
        Assert.Equal(2, store.Series(Symbol).Bars);

        // THE ATTEMPT NAMES THE LOOPBACK HOST AND ITS BODY HASH IS THIS BUILD'S OWN — there is no
        // vendor hash for a live window and none is claimed.
        var attempt = Assert.Single(store.Attempts(Symbol));
        Assert.Equal(200, attempt.Status);
        Assert.StartsWith(host.BaseUrl, attempt.Url, StringComparison.Ordinal);
        Assert.Contains("interval=1m", attempt.Url, StringComparison.Ordinal);

        // AND THE NEXT ASK STARTS AT THE FIRST MINUTE THIS INSTALLATION DOES NOT HAVE.
        var next = collector.Url(Symbol, store.LastOpen(ForwardBars.Source, Symbol));
        Assert.Contains(
            "startTime=" + Noon.AddMinutes(2).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            next, StringComparison.Ordinal);
    }

    /// <summary>
    /// A BODY THAT DOES NOT PARSE IS A RECORDED FAILURE, NOT A BAR. An error page served with a 200
    /// is the case that matters: the status says the request worked and the content says nothing at
    /// all, and a collector that stored the readable part of it would claim coverage it has not got.
    /// </summary>
    [Fact]
    public async Task A_body_that_does_not_parse_is_a_recorded_failure_not_a_bar()
    {
        using var db = TestEnv.NewDb();
        using var host = new FakeArchive();
        host.PublishAt(Path, "<html><body>the CDN is having an afternoon</body></html>");

        await using var collector = Collector(db, host, Noon.AddMinutes(5));
        var result = await collector.CollectOnceAsync();

        var store = new ForwardBarStore(db);
        var attempt = Assert.Single(store.Attempts(Symbol));
        log.WriteLine($"status={attempt.Status} bars={attempt.Bars} note={attempt.Note}");

        // THE ATTEMPT IS A ROW, with the status the host really sent and the reason in words.
        Assert.Equal(200, attempt.Status);
        Assert.Equal(0, attempt.Bars);
        Assert.Contains("could not be read as klines", attempt.Note!, StringComparison.Ordinal);

        // AND NOT ONE BAR WAS STORED.
        Assert.Equal(0, result!.Stored);
        Assert.Empty(store.Since(Symbol));
        Assert.Equal(0, store.Series(Symbol).Bars);
        Assert.NotNull(collector.LastError);

        // A GAP IS NOT INVENTED EITHER: nothing arrived, so there is nothing to have a hole between.
        Assert.Empty(store.Gaps(Symbol));
    }

    /// <summary>
    /// A FAILING HOST BACKS THE LOOK OFF AND A WORKING ONE PUTS IT BACK — and neither throws out of
    /// the loop. The wait is asserted through the delay the loop actually asked for, because a
    /// backoff that can only be observed by waiting for it is a backoff nobody checks.
    /// </summary>
    [Fact]
    public async Task The_collector_backs_off_after_failures_and_recovers()
    {
        using var db = TestEnv.NewDb();
        using var host = new FakeArchive { AlwaysAnswer = HttpStatusCode.ServiceUnavailable };

        var waits = new List<TimeSpan>();
        var enough = new TaskCompletionSource();

        await using var collector = Collector(db, host, Noon.AddMinutes(10), delay: async (d, ct) =>
        {
            waits.Add(d);
            if (waits.Count < 4) return;
            enough.TrySetResult();
            // Hold the loop here until Dispose cancels it, rather than spinning.
            await Task.Delay(Timeout.Infinite, ct);
        });

        collector.Start();
        await enough.Task.WaitAsync(TimeSpan.FromSeconds(30));

        log.WriteLine(string.Join(", ", waits.Select(w => w.TotalMilliseconds + " ms")));

        // EVERY LOOK FAILED, EVERY ONE IS A ROW, and each wait is longer than the last.
        var store = new ForwardBarStore(db);
        Assert.True(store.Attempts(Symbol).Count >= 4, $"only {store.Attempts(Symbol).Count} attempts");
        Assert.All(store.Attempts(Symbol), a => Assert.Contains("503", a.Note!, StringComparison.Ordinal));
        Assert.Empty(store.Since(Symbol));

        for (var i = 1; i < waits.Count; i++)
            Assert.True(waits[i] > waits[i - 1],
                $"wait {i} was {waits[i]} which is not longer than {waits[i - 1]}");

        // THE BACKOFF IS CAPPED. An hour of failures must still be noticed within five minutes.
        Assert.Equal(ForwardBarCollector.MaxBackoff, ForwardBarCollector.Backoff(Tick, 100));
        Assert.Equal(ForwardBarCollector.MaxBackoff, ForwardBarCollector.Backoff(TimeSpan.FromMinutes(1), 40));
        Assert.Equal(Tick, ForwardBarCollector.Backoff(Tick, 0));

        // AND IT RECOVERS. The host comes back, the next look succeeds, and the wait is the plain
        // tick again rather than the backed-off one.
        host.AlwaysAnswer = null;
        host.PublishAt(Path, Klines(0, 1));

        var failuresWhileDown = collector.ConsecutiveFailures;
        Assert.True(failuresWhileDown >= 3, $"only {failuresWhileDown} consecutive failures recorded");

        var recovered = await collector.CollectOnceAsync();
        Assert.Equal(2, recovered!.Stored);
        Assert.Equal(0, collector.ConsecutiveFailures);
        Assert.Null(collector.LastError);
        Assert.Equal(Tick, ForwardBarCollector.Backoff(Tick, collector.ConsecutiveFailures));
    }

    /// <summary>
    /// A HOST THAT NEVER ANSWERS IS A RECORDED FAILURE WITH NO STATUS, not an invented one: "the
    /// vendor said nothing" and "the vendor said 503" are different facts and the ledger keeps them
    /// apart. The request's own leash is what ends it, and it is seconds.
    /// </summary>
    [Fact]
    public async Task A_host_that_never_answers_is_a_timeout_row_with_no_status()
    {
        using var db = TestEnv.NewDb();
        using var host = new FakeArchive(answers: false);

        await using var collector = new ForwardBarCollector(db, () => Symbol,
            baseUrl: host.BaseUrl, requestTimeout: TimeSpan.FromSeconds(2), interval: Tick,
            now: () => Noon.AddMinutes(10));

        var started = DateTimeOffset.UtcNow;
        await collector.CollectOnceAsync();
        var took = DateTimeOffset.UtcNow - started;
        log.WriteLine($"the leash ended it after {took.TotalSeconds:0.##} s");

        var attempt = Assert.Single(new ForwardBarStore(db).Attempts(Symbol));
        Assert.Null(attempt.Status);
        Assert.Contains("did not answer within", attempt.Note!, StringComparison.Ordinal);
        Assert.True(took < TimeSpan.FromSeconds(20), $"the leash took {took}, which is not seconds");
    }

    /// <summary>
    /// THE OWNER'S TOGGLE IS READ AT EVERY LOOK, AND OFF WRITES NOTHING AT ALL — not even a failure,
    /// because "the owner switched it off" is not a fetch that went wrong.
    /// </summary>
    [Fact]
    public async Task The_toggle_is_read_at_every_look_and_off_writes_nothing()
    {
        using var db = TestEnv.NewDb();
        using var host = new FakeArchive();
        host.PublishAt(Path, Klines(0, 1));

        var on = false;
        await using var collector = new ForwardBarCollector(db, () => Symbol, () => on,
            baseUrl: host.BaseUrl, requestTimeout: TimeSpan.FromSeconds(5), interval: Tick,
            now: () => Noon.AddMinutes(5));

        Assert.Null(await collector.CollectOnceAsync());
        Assert.Empty(new ForwardBarStore(db).Attempts(Symbol));
        Assert.Empty(host.Marks);

        on = true;
        Assert.Equal(2, (await collector.CollectOnceAsync())!.Stored);
    }

    /// <summary>
    /// THE FORWARD ROW IS DATA AND IT SAYS WHAT IT IS. The host and the endpoint live in the
    /// catalogue, verified by one measurement quoted on the row; it publishes no checksum, and it
    /// cannot be driven as an archive.
    /// </summary>
    [Fact]
    public void The_forward_source_is_a_catalogue_row_with_no_checksum_and_no_periods()
    {
        var entry = Assert.Single(CandleSourceCatalog.BuiltIn(), s => s.Id == ForwardBars.Source);

        Assert.True(entry.Verified);
        Assert.Equal("", entry.ChecksumUrlShape);
        Assert.Equal(ForwardBars.Interval, entry.Interval);
        Assert.Contains("market-data-only", entry.Source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("market_data_only", entry.Source, StringComparison.Ordinal);
        Assert.Contains("{base}/api/v3/klines", entry.UrlShape, StringComparison.Ordinal);
        Assert.StartsWith("https://", entry.BaseUrl, StringComparison.Ordinal);

        // AND IT IS NOT AN ARCHIVE. Driving it as one would fetch a live window and hand it to the
        // normaliser, producing a frozen dataset claiming a freeze it never had.
        var refused = Assert.Throws<TradeAgentException>(() => CandleSourceCatalog.Of(entry));
        Assert.Contains("collected FORWARD", refused.Message, StringComparison.Ordinal);
    }
}

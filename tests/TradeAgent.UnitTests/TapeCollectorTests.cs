using System.Globalization;
using System.Net;
using Microsoft.Data.Sqlite;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Provisioning;
using TradeAgent.Tests;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE TAPE'S COLLECTOR AGAINST A LOOPBACK LISTENER, NEVER AGAINST THE VENDOR (<c>U-tape-store</c>
/// item 4). Every collector here is pointed at <see cref="FakeArchive"/>; the bodies are the vendor's
/// own shapes as measured on 2026-10-02 (<c>docs/RESEARCH-REQUIRED.md</c>, C5b). The clock and the
/// timer are injected, so hours of looks take milliseconds and no test waits for a real one.
/// </summary>
public class TapeCollectorTests(ITestOutputHelper log)
{
    /// <summary>Two seconds past a minute boundary — where an aligned look lands.</summary>
    static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 2, TimeSpan.Zero);

    static readonly DateTimeOffset Boundary = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    static IReadOnlyList<string> Universe => TapeSourceCatalog.Universe;

    static string Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    static string NewFile() => Path.Combine(TestEnv.Home, $"tape-{Guid.NewGuid():n}.db");

    static TapeSourceEntry Row(string id) => TapeSourceCatalog.BuiltIn().Single(r => r.Id == id);

    static TapeSourceCatalogRead Only(params string[] ids) =>
        new([.. TapeSourceCatalog.BuiltIn().Where(r => ids.Contains(r.Id))], null, []);

    /// <summary>The path and query a row's series asks for one symbol — built from the row's own shape.</summary>
    static string PathOf(string rowId, int series, string? symbol) => TapeCollector.Url(Row(rowId).Series[series], "", symbol);

    static TapeCollector Collector(TapeStore store, FakeArchive host, Func<DateTimeOffset> now,
        Func<bool>? enabled = null, Func<TimeSpan, CancellationToken, Task>? delay = null,
        TapeSourceCatalogRead? catalog = null) =>
        new(store, enabled,
            baseUrl: host.BaseUrl,
            requestTimeout: TimeSpan.FromSeconds(5),
            now: now,
            delay: delay,
            catalog: catalog ?? new TapeSourceCatalogRead(TapeSourceCatalog.BuiltIn(), null, []));

    // ---- the vendor's shapes, as measured -------------------------------------------------------

    static string PremiumAll(DateTimeOffset at, IEnumerable<string> symbols) => "[" + string.Join(",", symbols.Select(s =>
        $$"""{"symbol":"{{s}}","markPrice":"85495.82186232","indexPrice":"85534.78173913","estimatedSettlePrice":"85550.56200966","lastFundingRate":"0.00001937","interestRate":"0.00010000","nextFundingTime":{{Ms(at.AddHours(4))}},"time":{{Ms(at)}}}""")) + "]";

    static string OpenInterestOf(string symbol, DateTimeOffset at) =>
        $$"""{"symbol":"{{symbol}}","openInterest":"96863.654","time":{{Ms(at)}}}""";

    static string OpenInterestHist(string symbol, DateTimeOffset newest) => "[" + string.Join(",", Enumerable.Range(0, 3).Select(i =>
        $$"""{"symbol":"{{symbol}}","sumOpenInterest":"9704{{i}}.28100000","sumOpenInterestValue":"8306435554.74540000","CMCCirculatingSupply":"20092187.00000000","timestamp":{{Ms(newest.AddMinutes(5 * (i - 2)))}}}""")) + "]";

    static string Accounts(string symbol, DateTimeOffset newest) => "[" + string.Join(",", Enumerable.Range(0, 3).Select(i =>
        $$"""{"symbol":"{{symbol}}","longAccount":"0.476{{i}}","longShortRatio":"0.9088","shortAccount":"0.5239","timestamp":{{Ms(newest.AddMinutes(5 * (i - 2)))}}}""")) + "]";

    /// <summary>The taker ratio's items name NO symbol — measured — so the symbol asked for is their subject.</summary>
    static string Taker(DateTimeOffset newest) => "[" + string.Join(",", Enumerable.Range(0, 3).Select(i =>
        $$"""{"buySellRatio":"2.146{{i}}","sellVol":"481.8950","buyVol":"1034.5010","timestamp":{{Ms(newest.AddMinutes(5 * (i - 2)))}}}""")) + "]";

    static string FundingOf(string symbol, DateTimeOffset newest) => "[" + string.Join(",", Enumerable.Range(0, 2).Select(i =>
        $$"""{"symbol":"{{symbol}}","fundingTime":{{Ms(newest.AddHours(8 * (i - 1)))}},"fundingRate":"0.0000269{{i}}","markPrice":"84829.60000000","rateType":"Regular"}""")) + "]";

    /// <summary>Every series of every built-in row, per symbol where it is asked per symbol — except <paramref name="silent"/>'s 5-minute open interest, which is left to answer 404.</summary>
    static void PublishAll(FakeArchive host, string? silent = null)
    {
        host.PublishAt(PathOf(TapeSourceCatalog.Premium, 0, null), PremiumAll(Now.AddSeconds(-1), [.. Universe, "LTCUSDT"]));
        foreach (var s in Universe)
        {
            host.PublishAtExactly(PathOf(TapeSourceCatalog.OpenInterest, 0, s), OpenInterestOf(s, Now.AddSeconds(-3)));
            if (s != silent) host.PublishAtExactly(PathOf(TapeSourceCatalog.OpenInterest5m, 0, s), OpenInterestHist(s, Boundary));
            host.PublishAtExactly(PathOf(TapeSourceCatalog.Ratios5m, 0, s), Accounts(s, Boundary));
            host.PublishAtExactly(PathOf(TapeSourceCatalog.Ratios5m, 1, s), Taker(Boundary.AddMinutes(-5)));
            host.PublishAtExactly(PathOf(TapeSourceCatalog.Funding, 0, s), FundingOf(s, Boundary.AddHours(-4)));
        }
    }

    static long Count(string file, string sql)
    {
        using var raw = new SqliteConnection($"Data Source={file};Pooling=False");
        raw.Open();
        using var c = raw.CreateCommand();
        c.CommandText = sql;
        return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// EVERY ATTEMPT IS A ROW, WITH THE ORIGIN IT WAS FETCHED FROM, AND EVERY OBSERVATION NAMES ITS FETCH.
    /// One look at every built-in row: thirty-one attempts — the premium index once for every symbol, every
    /// other series once per symbol — one of them a 404 that is a row like the rest. The all-symbol answer
    /// is kept to the six; the taker items, which name no symbol, are filed under the symbol asked for;
    /// and a second look at the same answers is thirty-one more attempts and not one new observation.
    /// </summary>
    [Fact]
    public async Task Every_fetch_is_a_row_with_its_origin_and_every_observation_names_its_fetch()
    {
        using var host = new FakeArchive();
        PublishAll(host, silent: "SOLUSDT");

        var file = NewFile();
        using var store = new TapeStore(file);
        await using var collector = Collector(store, host, () => Now);

        foreach (var row in collector.Rows)
            log.WriteLine($"{row.Id}: {await collector.CollectOnceAsync(row)}");

        var fetches = store.Fetches(limit: 1000);
        Assert.Equal(1 + 6 + 6 + 2 * 6 + 6, fetches.Count);
        Assert.All(fetches, f =>
        {
            Assert.Equal(host.BaseUrl, f.Origin);
            Assert.StartsWith(host.BaseUrl + "/", f.Url, StringComparison.Ordinal);
            Assert.Equal(Now, f.ReceivedAt);
            Assert.True(f.RequestedAt <= f.ReceivedAt);
        });

        // A FAILURE IS A ROW TOO: the status the host really sent, the reason in words, nothing stored.
        var failed = Assert.Single(fetches, f => f.Note is not null);
        log.WriteLine($"failed: {failed.Url} {failed.HttpStatus} {failed.Note}");
        Assert.Equal((TapeSourceCatalog.OpenInterest5m, 404, 0), (failed.Source, failed.HttpStatus, failed.Items));
        Assert.Contains("symbol=SOLUSDT", failed.Url, StringComparison.Ordinal);
        Assert.Contains("answered 404", failed.Note!, StringComparison.Ordinal);
        Assert.Empty(store.ObservationsOf(failed.Id));
        Assert.All(fetches.Where(f => f.Note is null), f => Assert.Equal(64, f.BodySha256!.Length));

        // EVERY OBSERVATION NAMES ITS FETCH, read straight off the file rather than through the store:
        // no observation without a fetch, none filed under another source's or series' fetch, none
        // claiming an arrival its fetch did not have.
        Assert.Equal(0, Count(file, """
            SELECT COUNT(*) FROM tape_obs o LEFT JOIN tape_fetch f ON f.id = o.fetch_id
            WHERE f.id IS NULL OR f.source <> o.source OR f.series <> o.series OR f.received_at <> o.received_at
            """));
        const long expected = 6 + 6 + 5 * 3 + 6 * 3 + 6 * 3 + 6 * 2;
        Assert.Equal(expected, Count(file, "SELECT COUNT(*) FROM tape_obs"));
        Assert.Equal(expected, Count(file, "SELECT SUM(items) FROM tape_fetch"));

        // THE ALL-SYMBOL ANSWER IS KEPT TO THE SIX: LTCUSDT was in it and is not here.
        var premium = Assert.Single(fetches, f => f.Source == TapeSourceCatalog.Premium);
        Assert.Equal(6, premium.Items);
        Assert.Equal(Universe.Order(), store.ObservationsOf(premium.Id).Select(o => o.Subject).Order());

        // THE TAKER ITEMS NAME NO SYMBOL, so each is filed under the symbol its request asked for.
        foreach (var s in Universe)
        {
            var taker = fetches.Single(f => f.Series == "taker-long-short-5m" && f.Url.Contains($"symbol={s}&", StringComparison.Ordinal));
            Assert.All(store.ObservationsOf(taker.Id), o => Assert.Equal(s, o.Subject));
            Assert.Equal(3, store.ObservationsOf(taker.Id).Count);
        }

        // FETCHED FROM A LOOPBACK LISTENER, NOTHING IS LIVE — the origin is not the built-in one.
        Assert.Equal(expected, Count(file, "SELECT COUNT(*) FROM tape_obs WHERE evidence_class = 'O-ARCH'"));

        // AND A SECOND LOOK AT THE SAME ANSWERS: every attempt a row, no observation written twice.
        foreach (var row in collector.Rows) await collector.CollectOnceAsync(row);
        Assert.Equal(62, store.Fetches(limit: 1000).Count);
        Assert.Equal(expected, Count(file, "SELECT COUNT(*) FROM tape_obs"));
    }

    /// <summary>
    /// A FAILING HOST BACKS THE ROW'S LOOKS OFF AND A WORKING ONE PUTS THEM BACK ON THE GRID. The waits
    /// are read off the delay the loop actually asked for: doubling from the sixty-second cadence and
    /// stopping at five minutes, then — the host back — the next minute boundary plus two seconds. And a
    /// vendor that says "too many requests" is obeyed at once: the look stops at that answer.
    /// </summary>
    [Fact]
    public async Task The_collector_backs_off_and_recovers()
    {
        using var host = new FakeArchive { AlwaysAnswer = HttpStatusCode.ServiceUnavailable };
        using var store = new TapeStore(NewFile());
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 30, TimeSpan.Zero);

        var waits = new List<TimeSpan>();
        var enough = new TaskCompletionSource();

        await using var collector = Collector(store, host, () => now, catalog: Only(TapeSourceCatalog.Premium),
            delay: async (d, ct) =>
            {
                waits.Add(d);
                if (waits.Count == 4)
                {
                    // THE HOST COMES BACK while the loop waits out its fourth backoff.
                    host.PublishAt(PathOf(TapeSourceCatalog.Premium, 0, null), PremiumAll(now.AddSeconds(-1), Universe));
                    host.AlwaysAnswer = null;
                }
                if (waits.Count < 5) return;
                enough.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            });

        collector.Start();
        await enough.Task.WaitAsync(TimeSpan.FromSeconds(30));
        log.WriteLine(string.Join(", ", waits.Select(w => w.TotalSeconds + " s")));

        // FOUR FAILED LOOKS: the wait doubles from the cadence and stops growing at five minutes.
        Assert.Equal([TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)],
            waits.Take(4));
        Assert.Equal(TapeCollector.MaxBackoff, TimeSpan.FromMinutes(5));

        // THE FIFTH LOOK WORKED, AND THE NEXT ONE IS ON THE GRID AGAIN: 12:01:02, thirty-two seconds away.
        Assert.Equal(TimeSpan.FromSeconds(32), waits[4]);
        Assert.Equal(0, collector.ConsecutiveFailures(TapeSourceCatalog.Premium));
        Assert.Null(collector.LastError(TapeSourceCatalog.Premium));

        // EVERY LOOK IS A ROW: four failures carrying the status the host really sent, then the delivery.
        var fetches = store.Fetches(TapeSourceCatalog.Premium).Reverse().ToList();
        Assert.Equal(5, fetches.Count);
        Assert.All(fetches.Take(4), f =>
        {
            Assert.Equal(503, f.HttpStatus);
            Assert.Contains("answered 503", f.Note!, StringComparison.Ordinal);
            Assert.Equal(0, f.Items);
        });
        Assert.Null(fetches[4].Note);
        Assert.Equal(6, fetches[4].Items);
        Assert.Equal(6, store.ObservationsOf(fetches[4].Id).Count);

        // AND "TOO MANY REQUESTS" ENDS A LOOK AT ONCE: one attempt of the six, counted as a failure.
        host.AlwaysAnswer = (HttpStatusCode)429;
        var throttled = await collector.CollectOnceAsync(Row(TapeSourceCatalog.OpenInterest));
        log.WriteLine($"throttled: {throttled}");
        Assert.Equal((1, 0, true, false), (throttled.Attempts, throttled.Delivered, throttled.Throttled, throttled.Worked));
        Assert.Equal(429, Assert.Single(store.Fetches(TapeSourceCatalog.OpenInterest)).HttpStatus);
    }

    /// <summary>
    /// SWITCHED OFF, THE COLLECTOR ASKS NOTHING AND WRITES NOTHING — not even a failed attempt, because
    /// "the owner switched it off" is not a fetch that went wrong. Switched back on, the next look works.
    /// </summary>
    [Fact]
    public async Task Switched_off_the_collector_asks_nothing_and_writes_nothing()
    {
        using var host = new FakeArchive();
        host.PublishAt(PathOf(TapeSourceCatalog.Premium, 0, null), PremiumAll(Now, Universe));
        using var store = new TapeStore(NewFile());

        var on = false;
        await using var collector = Collector(store, host, () => Now, enabled: () => on);
        var premium = Row(TapeSourceCatalog.Premium);

        var off = await collector.CollectOnceAsync(premium);
        Assert.True(off.Off);
        Assert.True(off.Worked);
        Assert.Equal(0, off.Attempts);
        Assert.Empty(store.Fetches());
        Assert.DoesNotContain(host.Marks, m => m.Contains("got ", StringComparison.Ordinal));

        on = true;
        var tick = await collector.CollectOnceAsync(premium);
        Assert.Equal((1, 1, 6), (tick.Attempts, tick.Delivered, tick.Stored));
        Assert.Single(store.Fetches());
    }

    /// <summary>
    /// AN ANSWER THAT IS NOT ONE IS A RECORDED FAILURE AND NEVER AN OBSERVATION: an answer about another
    /// symbol, an error page served with a 200, and a redirect — which is not followed, because the origin
    /// a fetch records must be the one that answered it.
    /// </summary>
    [Fact]
    public async Task A_wrong_symbol_an_unreadable_body_or_a_redirect_is_a_recorded_failure_and_stores_nothing()
    {
        using var host = new FakeArchive();
        host.PublishAtExactly(PathOf(TapeSourceCatalog.OpenInterest, 0, "BTCUSDT"), OpenInterestOf("ETHUSDT", Now));
        host.PublishAtExactly(PathOf(TapeSourceCatalog.OpenInterest, 0, "ETHUSDT"), "<html><body>an afternoon</body></html>");

        using var store = new TapeStore(NewFile());
        await using var collector = Collector(store, host, () => Now, catalog: Only(TapeSourceCatalog.OpenInterest));

        var tick = await collector.CollectOnceAsync(Row(TapeSourceCatalog.OpenInterest));
        var fetches = store.Fetches(TapeSourceCatalog.OpenInterest);
        foreach (var f in fetches) log.WriteLine($"{f.Url} {f.HttpStatus} {f.Note}");

        Assert.Equal(6, fetches.Count);
        Assert.Equal(0, tick.Delivered);
        Assert.False(tick.Worked);

        var btc = Assert.Single(fetches, f => f.Url.EndsWith("symbol=BTCUSDT", StringComparison.Ordinal));
        Assert.Equal(200, btc.HttpStatus);
        Assert.Contains("is about ETHUSDT, not the BTCUSDT that was asked for", btc.Note!, StringComparison.Ordinal);

        var eth = Assert.Single(fetches, f => f.Url.EndsWith("symbol=ETHUSDT", StringComparison.Ordinal));
        Assert.Equal(200, eth.HttpStatus);
        Assert.Contains("could not be read", eth.Note!, StringComparison.Ordinal);

        Assert.All(fetches, f => Assert.Empty(store.ObservationsOf(f.Id)));

        // A REDIRECT IS THE STATUS IT IS. The listener it points at is never asked anything.
        using var elsewhere = new FakeArchive();
        elsewhere.PublishAt(PathOf(TapeSourceCatalog.Premium, 0, null), PremiumAll(Now, Universe));
        using var redirecting = new FakeArchive { RedirectTo = elsewhere.BaseUrl };
        await using var redirected = Collector(store, redirecting, () => Now, catalog: Only(TapeSourceCatalog.Premium));

        await redirected.CollectOnceAsync(Row(TapeSourceCatalog.Premium));
        var moved = Assert.Single(store.Fetches(TapeSourceCatalog.Premium));
        log.WriteLine($"redirect: {moved.HttpStatus} {moved.Origin} {moved.Note}");
        Assert.Equal(302, moved.HttpStatus);
        Assert.Equal(redirecting.BaseUrl, moved.Origin);
        Assert.Contains("answered 302", moved.Note!, StringComparison.Ordinal);
        Assert.Empty(store.ObservationsOf(moved.Id));
        Assert.DoesNotContain(elsewhere.Marks, m => m.Contains("got ", StringComparison.Ordinal));
    }
}

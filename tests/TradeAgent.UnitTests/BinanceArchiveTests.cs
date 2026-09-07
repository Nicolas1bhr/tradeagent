using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using TradeAgent.Core.Data;
using TradeAgent.Provisioning;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEM 1 — the download. What the archive's checksum sidecar is FOR.
///
/// Red first: with the month fetched and no sidecar pinned against it, bytes that disagree with the
/// hash the vendor published were kept and normalised into a dataset the ledger then called
/// reproducible. The sidecar is the only integrity story a public S3 bucket over TLS has.
/// </summary>
public class BinanceArchiveTests
{
    static string Scratch()
    {
        var d = Path.Combine(TestEnv.Home, "arc-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(d);
        return d;
    }

    const string Pair = "BTCUSDT";
    static readonly DateOnly Month = new(2026, 8, 1);

    /// <summary>Two 1-minute rows in the archive's own shape, microsecond timestamps.</summary>
    const string TwoRows =
        "1785542400000000,62887.88000000,62900.00000000,62887.87000000,62893.06000000,7.71179000,1785542459999999,485038.09592460,873,1.18384000,74456.29821520,0\n" +
        "1785542460000000,62893.06000000,62928.00000000,62892.00000000,62928.00000000,2.76921000,1785542519999999,174181.85632380,1182,1.49271000,93898.00110200,0\n";

    [Fact]
    public async Task A_month_whose_sidecar_disagrees_with_the_bytes_is_thrown_away()
    {
        using var archive = new FakeArchive();
        archive.PublishWithWrongChecksum(Pair, Month, TwoRows);

        var raw = Scratch();
        var result = await new BinanceArchiveClient(archive.BaseUrl).FetchMonthAsync(Pair, Month, raw);

        Assert.Equal(MonthOutcome.ChecksumMismatch, result.Outcome);
        Assert.Null(result.File);
        Assert.Empty(Directory.GetFiles(raw, "*.zip"));
    }

    [Fact]
    public async Task A_month_that_matches_its_sidecar_is_collected_with_both_hashes_recorded()
    {
        using var archive = new FakeArchive();
        var bytes = archive.Publish(Pair, Month, TwoRows);

        var result = await new BinanceArchiveClient(archive.BaseUrl).FetchMonthAsync(Pair, Month, Scratch());

        Assert.Equal(MonthOutcome.Collected, result.Outcome);
        Assert.NotNull(result.File);
        Assert.Equal(FakeArchive.Sha256(bytes), result.File!.PublishedSha256);
        Assert.Equal(FakeArchive.Sha256(bytes), result.File.ComputedSha256);
        Assert.Equal(bytes.Length, result.File.Bytes);
        Assert.EndsWith($"/data/spot/monthly/klines/{Pair}/1m/{Pair}-1m-2026-08.zip", result.File.Url);
    }

    [Fact]
    public async Task A_month_the_vendor_has_not_published_is_recorded_and_is_not_an_error()
    {
        using var archive = new FakeArchive();

        var result = await new BinanceArchiveClient(archive.BaseUrl).FetchMonthAsync(Pair, Month, Scratch());

        Assert.Equal(MonthOutcome.NotPublished, result.Outcome);
        Assert.Null(result.File);
    }

    [Fact]
    public async Task A_month_with_no_sidecar_at_all_is_not_collected()
    {
        using var archive = new FakeArchive();
        archive.PublishWithoutSidecar(Pair, Month, TwoRows);

        var raw = Scratch();
        var result = await new BinanceArchiveClient(archive.BaseUrl).FetchMonthAsync(Pair, Month, raw);

        Assert.Equal(MonthOutcome.ChecksumNotPublished, result.Outcome);
        Assert.Empty(Directory.GetFiles(raw, "*.zip"));
    }

    [Fact]
    public async Task Twelve_months_are_asked_for_and_the_months_that_are_missing_are_reported_too()
    {
        using var archive = new FakeArchive();
        var now = new DateTimeOffset(2026, 9, 7, 16, 0, 0, TimeSpan.Zero);
        foreach (var m in BinanceArchive.RecentCompleteMonths(now).Skip(1)) archive.Publish("XRPUSDT", m, TwoRows);

        var results = await new BinanceArchiveClient(archive.BaseUrl).CollectMonthsAsync("XRPUSDT", now);

        Assert.Equal(12, results.Count);
        Assert.Equal("2025-09", results[0].Month);
        Assert.Equal(MonthOutcome.NotPublished, results[0].Outcome);
        Assert.Equal(11, results.Count(r => r.Outcome == MonthOutcome.Collected));
        Assert.All(results.Where(r => r.Outcome == MonthOutcome.Collected),
            r => Assert.StartsWith(BinanceArchive.RawDir("XRPUSDT"), r.File!.Path));
    }

    /// <summary>
    /// RED FIRST — the money-path defect. Before the fix a server that accepted the request and
    /// never answered produced <c>NotPublished</c>, word for word "Binance has not published
    /// BTCUSDT-1m-2026-08.zip": a timeout wearing the words of the vendor's own answer, written into
    /// the coverage a strategy is judged on. It is <c>IAtasAdapter</c> rule 3 on the data path —
    /// ambiguity is reported as ambiguity or it is not reported at all.
    ///
    /// The token is this test's own backstop, not the product's: if the client ever stops honouring
    /// the timeout it was handed, this fails in twenty seconds with a cancellation instead of
    /// sitting on the shared client's thirty minutes.
    /// </summary>
    [Fact]
    public async Task A_server_that_never_answers_is_unreachable_and_is_never_called_a_month_the_vendor_has_not_published()
    {
        using var archive = new FakeArchive(answers: false);
        using var backstop = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var result = await new BinanceArchiveClient(archive.BaseUrl, TimeSpan.FromSeconds(2))
            .FetchMonthAsync(Pair, Month, Scratch(), null, backstop.Token);

        Assert.Equal(MonthOutcome.Unreachable, result.Outcome);
        Assert.Contains("could not be asked about", result.Detail);
        Assert.Contains("2 seconds", result.Detail);
        Assert.DoesNotContain("has not published", result.Detail);

        // Both requests really did reach the server, so this is a silence and not a wrong URL.
        Assert.Equal(2, archive.Marks.Count(m => m.Contains("got ", StringComparison.Ordinal)));
    }

    /// <summary>
    /// RED FIRST — the thirty minutes. Under the same never-answering server the whole call used to
    /// run to <c>Downloader</c>'s shared 30-minute client timeout; one such request is what turned a
    /// one-minute Unit suite into 30 m 48 s on windows-latest (CI run 34167309186 at a22939d).
    ///
    /// TIMING CATEGORY, ARGUED WITH THE NUMBERS. Its verdict needs the runner to keep a wall clock:
    /// it measures a duration and asserts a ceiling on it. What it measures is deterministic — two
    /// cancellation leashes of 2 s each, one after the other — so the floor is 4 s of timer and the
    /// rest is a loopback connect. Measured end to end at the fix: 4.0 s on this Mac, and the
    /// runners' figures are in the unit's report. The ceiling is 10 s, a 2.4x margin over the floor,
    /// and it is a ceiling on the CLIENT'S OWN leash rather than on how fast the runner is.
    /// </summary>
    [Fact]
    [Trait("Category", "Timing")]
    public async Task A_server_that_never_answers_costs_seconds_and_not_the_clients_thirty_minutes()
    {
        using var archive = new FakeArchive(answers: false);
        var clock = Stopwatch.StartNew();

        var result = await new BinanceArchiveClient(archive.BaseUrl, TimeSpan.FromSeconds(2))
            .FetchMonthAsync(Pair, Month, Scratch());

        clock.Stop();
        Assert.Equal(MonthOutcome.Unreachable, result.Outcome);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10),
            $"a loopback server that never answers cost {clock.Elapsed.TotalSeconds:N1} s");
    }

    /// <summary>
    /// A vendor that answers something other than 200 or 404 has told us nothing about the month.
    /// This branch fell through to "Binance has not published it" as well: only a 404 is the archive
    /// saying the month does not exist, and a CDN having a bad afternoon is not a 404.
    /// </summary>
    [Fact]
    public async Task A_vendor_that_answers_five_hundred_and_three_has_not_said_the_month_is_missing()
    {
        using var archive = new FakeArchive { AlwaysAnswer = HttpStatusCode.ServiceUnavailable };
        archive.PublishWithoutSidecar(Pair, Month, TwoRows);

        var result = await new BinanceArchiveClient(archive.BaseUrl, TimeSpan.FromSeconds(5))
            .FetchMonthAsync(Pair, Month, Scratch());

        Assert.Equal(MonthOutcome.Unreachable, result.Outcome);
        Assert.Contains("503", result.Detail);
        Assert.DoesNotContain("has not published", result.Detail);
    }

    [Fact]
    public void A_sidecar_naming_another_file_is_not_this_files_hash()
    {
        var sha = new string('b', 64);
        Assert.Equal(sha, BinanceArchive.Sha256FromSidecar($"{sha}  BTCUSDT-1m-2026-08.zip", "BTCUSDT-1m-2026-08.zip"));
        Assert.Null(BinanceArchive.Sha256FromSidecar($"{sha}  ETHUSDT-1m-2026-08.zip", "BTCUSDT-1m-2026-08.zip"));
        Assert.Null(BinanceArchive.Sha256FromSidecar("not-a-hash  BTCUSDT-1m-2026-08.zip", "BTCUSDT-1m-2026-08.zip"));
        Assert.Null(BinanceArchive.Sha256FromSidecar("", "BTCUSDT-1m-2026-08.zip"));
    }

    [Fact]
    public void The_twelve_months_asked_for_never_include_the_month_in_progress()
    {
        var months = BinanceArchive.RecentCompleteMonths(new DateTimeOffset(2026, 9, 7, 16, 0, 0, TimeSpan.Zero));

        Assert.Equal(12, months.Count);
        Assert.Equal(new DateOnly(2025, 9, 1), months[0]);
        Assert.Equal(new DateOnly(2026, 8, 1), months[^1]);
        Assert.DoesNotContain(new DateOnly(2026, 9, 1), months);
    }
}

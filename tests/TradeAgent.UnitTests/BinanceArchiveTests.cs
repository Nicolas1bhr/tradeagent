using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using TradeAgent.Core.Data;
using TradeAgent.Provisioning;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A LOOPBACK STAND-IN FOR data.binance.vision. Every test in this unit talks to this and never to
/// the network: the one real download this unit was allowed is quoted in the brief's report and is
/// evidence about the vendor's URL pattern, not a thing a suite may repeat on every run.
///
/// It answers the two paths the archive has — the monthly zip and its <c>.CHECKSUM</c> sidecar — and
/// 404 for anything it was not given, which is what the vendor answers for a month it has not
/// published (measured: 2026-09 on 2026-09-07).
/// </summary>
public sealed class FakeArchive : IDisposable
{
    readonly HttpListener _http = new();
    readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _sidecars = new(StringComparer.Ordinal);

    public FakeArchive()
    {
        for (var attempt = 0; ; attempt++)
        {
            Port = 18000 + Random.Shared.Next(2000);
            _http.Prefixes.Clear();
            _http.Prefixes.Add($"http://127.0.0.1:{Port}/");
            try { _http.Start(); break; }
            catch (HttpListenerException) when (attempt < 20) { }
        }

        Serving = Task.Run(async () =>
        {
            while (_http.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _http.GetContextAsync(); }
                catch (Exception) { return; }

                var path = ctx.Request.Url!.AbsolutePath;
                try
                {
                    if (_sidecars.TryGetValue(path, out var text))
                    {
                        var body = Encoding.UTF8.GetBytes(text);
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentLength64 = body.Length;
                        await ctx.Response.OutputStream.WriteAsync(body);
                    }
                    else if (_files.TryGetValue(path, out var bytes))
                    {
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentLength64 = bytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(bytes);
                    }
                    else ctx.Response.StatusCode = 404;

                    ctx.Response.Close();
                }
                catch (Exception) { /* a client that hung up is not this harness's business */ }
            }
        });
    }

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    public Task Serving { get; }

    /// <summary>Publishes a month whose sidecar carries the hash of the bytes actually served.</summary>
    public byte[] Publish(string pair, DateOnly month, string csv)
    {
        var bytes = Zip(BinanceArchive.FileName(pair, month).Replace(".zip", ".csv"), csv);
        PublishBytes(pair, month, bytes, Sha256(bytes));
        return bytes;
    }

    /// <summary>Publishes a month whose sidecar carries a hash the bytes do not have.</summary>
    public void PublishWithWrongChecksum(string pair, DateOnly month, string csv)
    {
        var bytes = Zip(BinanceArchive.FileName(pair, month).Replace(".zip", ".csv"), csv);
        PublishBytes(pair, month, bytes, new string('a', 64));
    }

    /// <summary>Publishes the zip and no sidecar at all.</summary>
    public void PublishWithoutSidecar(string pair, DateOnly month, string csv)
    {
        var bytes = Zip(BinanceArchive.FileName(pair, month).Replace(".zip", ".csv"), csv);
        _files[PathOf(pair, month)] = bytes;
    }

    void PublishBytes(string pair, DateOnly month, byte[] bytes, string sha)
    {
        var path = PathOf(pair, month);
        _files[path] = bytes;
        _sidecars[path + ".CHECKSUM"] = $"{sha}  {BinanceArchive.FileName(pair, month)}";
    }

    static string PathOf(string pair, DateOnly month) =>
        $"/data/spot/monthly/klines/{pair}/1m/{BinanceArchive.FileName(pair, month)}";

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    static byte[] Zip(string entryName, string content)
    {
        using var into = new MemoryStream();
        using (var zip = new ZipArchive(into, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using var stream = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
        }
        return into.ToArray();
    }

    public void Dispose()
    {
        try { _http.Stop(); _http.Close(); } catch (Exception) { }
    }
}

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

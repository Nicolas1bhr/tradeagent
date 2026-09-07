using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using TradeAgent.Core.Data;
using TradeAgent.Provisioning;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// TEMPORARY — U-archive-win item 1, the measurement. It asserts nothing about the product; it fails
/// on purpose so that every runner prints what its own loopback stack did with each of the two
/// requests <see cref="BinanceArchiveClient.FetchMonthAsync"/> makes when there is no sidecar.
///
/// Delete it once the marks are quoted in the report.
/// </summary>
public class ArchiveRunnerProbeTests
{
    const string Pair = "BTCUSDT";
    static readonly DateOnly Month = new(2026, 8, 1);
    static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);

    const string TwoRows =
        "1785542400000000,62887.88000000,62900.00000000,62887.87000000,62893.06000000,7.71179000,1785542459999999,485038.09592460,873,1.18384000,74456.29821520,0\n" +
        "1785542460000000,62893.06000000,62928.00000000,62892.00000000,62928.00000000,2.76921000,1785542519999999,174181.85632380,1182,1.49271000,93898.00110200,0\n";

    [Fact]
    public async Task MEASURE_what_this_runner_does_with_the_two_requests_the_no_sidecar_path_makes()
    {
        var log = new List<string>
        {
            $"runner: {RuntimeInformation.OSDescription} / {RuntimeInformation.ProcessArchitecture}",
            $"listener: {(HttpListener.IsSupported ? "HttpListener supported" : "HttpListener NOT supported")}",
            $"bound per probe: {Bound.TotalSeconds:N0} s"
        };

        await Probe(log, "A  Downloader.TryGetSmallTextAsync GET .CHECKSUM (no sidecar published)",
            async (url, ct) =>
            {
                var text = await Downloader.TryGetSmallTextAsync(BinanceArchive.ChecksumUrl(url), 4096, Bound, ct);
                return text is null ? "null (unreadable)" : $"text[{text.Length}]";
            });

        await Probe(log, "B  Downloader.TryStatusAsync HEAD the zip (the request that read NotPublished)",
            async (url, ct) =>
            {
                var status = await Downloader.TryStatusAsync(url, ct);
                return status is null ? "null (could not be asked)" : $"{(int)status} {status}";
            });

        await Probe(log, "C  a fresh HttpClient HEAD the zip",
            async (url, ct) =>
            {
                using var http = new HttpClient { Timeout = Bound };
                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                return $"{(int)res.StatusCode} {res.StatusCode}, content-length {res.Content.Headers.ContentLength?.ToString() ?? "(none)"}";
            });

        await Probe(log, "D  a fresh HttpClient GET the zip",
            async (url, ct) =>
            {
                using var http = new HttpClient { Timeout = Bound };
                using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                var body = await res.Content.ReadAsByteArrayAsync(ct);
                return $"{(int)res.StatusCode} {res.StatusCode}, {body.Length} bytes read";
            });

        await Probe(log, "E  control: a fresh HttpClient HEAD a path the server was never given",
            async (url, ct) =>
            {
                using var http = new HttpClient { Timeout = Bound };
                using var req = new HttpRequestMessage(HttpMethod.Head, url + ".nothing");
                using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                return $"{(int)res.StatusCode} {res.StatusCode}";
            });

        await Probe(log, "F  the whole FetchMonthAsync, the shape that went red",
            async (url, ct) =>
            {
                var dir = Path.Combine(TestEnv.Home, "probe-" + Guid.NewGuid().ToString("n"));
                var client = new BinanceArchiveClient(url[..url.IndexOf("/data/", StringComparison.Ordinal)]);
                var result = await client.FetchMonthAsync(Pair, Month, dir, null, ct);
                return $"{result.Outcome} — {result.Detail}";
            });

        Assert.Fail(string.Join('\n', log));
    }

    static async Task Probe(List<string> log, string what, Func<string, CancellationToken, Task<string>> call)
    {
        using var archive = new FakeArchive();
        archive.PublishWithoutSidecar(Pair, Month, TwoRows);
        var url = BinanceArchive.MonthUrl(archive.BaseUrl, Pair, Month);

        using var cts = new CancellationTokenSource(Bound);
        var clock = Stopwatch.StartNew();
        string answer;
        try { answer = await call(url, cts.Token); }
        catch (Exception ex) { answer = $"THREW {ex.GetType().Name}: {ex.Message.ReplaceLineEndings(" ")}"; }
        clock.Stop();

        // The server marks its own close after the client is already back; give it a moment so the
        // record is the whole exchange rather than the part the client happened to see.
        await Task.Delay(500);

        log.Add("");
        log.Add($"{what}");
        log.Add($"{clock.ElapsedMilliseconds,7} ms  cli {answer}");
        foreach (var mark in archive.Marks) log.Add(mark);
        if (archive.Marks.Count == 0) log.Add("        (the server recorded no request at all)");
    }
}

using System.Net;
using TradeAgent.Core;
using TradeAgent.Provisioning;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// REVIEW 2026-09-05b finding 4 / Codex F15, red first: what a half-finished download may be
/// continued into, and what an install with no checksum has to say for itself.
///
/// The finding was not that resuming is wrong. It is that a part file named only after its
/// DESTINATION belongs to the destination rather than to the download: any interrupted transfer
/// left bytes at <c>%LOCALAPPDATA%\TradeAgent\tools\atas\ATASPlatform.exe.part</c>, the next attempt
/// asked a publisher's CDN to continue from that offset, and the concatenation was renamed to
/// ATASPlatform.exe and handed to <c>_runElevated</c>. Probe P4c on <c>review-probes-b</c> ran that
/// against a real socket and asserted the mixed file; these assert it cannot be built.
///
/// Everything here talks to a loopback <see cref="HttpListener"/>, because the behaviour under test
/// is a Range header and a 206 — the two things a fake stream cannot produce.
/// </summary>
public class DownloadPartBindingTests
{
    static string Scratch()
    {
        var d = Path.Combine(TestEnv.Home, "dl-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>
    /// A server that answers whatever range it is asked for, records the requests, and can be told
    /// to hang up part-way through the first one.
    /// </summary>
    sealed class Vendor : IDisposable
    {
        readonly HttpListener _http = new();
        readonly List<string?> _ranges = [];

        public Vendor(byte[] body, int abortAfter = 0, long? claimTotal = null)
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

                    var range = ctx.Request.Headers["Range"];
                    lock (_ranges) _ranges.Add(range);
                    var from = range is null ? 0 : int.Parse(range.Replace("bytes=", "").Split('-')[0]);
                    var total = claimTotal ?? body.Length;

                    try
                    {
                        // What a real CDN answers to a range that starts past the end of the body.
                        // Without this the harness would try to declare a negative content length
                        // and hang the client, which would hide a resume bug behind a timeout
                        // instead of a failed assertion.
                        if (from >= body.Length && range is not null)
                        {
                            ctx.Response.StatusCode = 416;
                            ctx.Response.Headers["Content-Range"] = $"bytes */{total}";
                            ctx.Response.Close();
                            continue;
                        }

                        if (range is null)
                        {
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentLength64 = total;
                        }
                        else
                        {
                            ctx.Response.StatusCode = 206;
                            ctx.Response.ContentLength64 = total - from;
                            ctx.Response.Headers["Content-Range"] = $"bytes {from}-{total - 1}/{total}";
                        }

                        var slice = body.AsMemory(Math.Min(from, body.Length));
                        if (abortAfter > 0)
                        {
                            await ctx.Response.OutputStream.WriteAsync(slice[..abortAfter]);
                            await ctx.Response.OutputStream.FlushAsync();
                            abortAfter = 0;
                            ctx.Response.Abort();
                            continue;
                        }

                        await ctx.Response.OutputStream.WriteAsync(slice);
                        ctx.Response.Close();
                    }
                    catch (Exception) { /* the client hung up; the next request is what matters */ }
                }
            });
        }

        public int Port { get; }
        public Task Serving { get; }
        public string Url => $"http://127.0.0.1:{Port}/ATASPlatform.exe";
        public IReadOnlyList<string?> Ranges { get { lock (_ranges) return _ranges.ToList(); } }
        public void Dispose() { try { _http.Stop(); _http.Close(); } catch (Exception) { } }
    }

    static Integrity NoChecksum => Integrity.Unverified("ATAS publishes no checksum for its installer");

    /// <summary>
    /// P4c, lifted. The stale prefix is the one P4c wrote, at the destination the ATAS installer
    /// always uses, and the server would happily answer a Range header. Nothing asks it to.
    /// </summary>
    [Fact]
    public async Task A_stale_part_from_another_download_is_never_resumed_into_this_one()
    {
        var root = Scratch();
        var dest = Path.Combine(root, "ATASPlatform.exe");
        await File.WriteAllTextAsync(dest + ".part", "STALE-PREFIX-FROM-AN-EARLIER-DOWNLOAD;");
        // And the same thing one build later: a part properly bound to a DIFFERENT download.
        await File.WriteAllTextAsync(dest + ".part-0123456789abcdef-99", "BYTES OF SOMETHING ELSE");

        using var vendor = new Vendor("REAL-INSTALLER-BYTES"u8.ToArray());
        var file = await Downloader.DownloadAsync(vendor.Url, dest, NoChecksum);

        Assert.Equal("REAL-INSTALLER-BYTES", await File.ReadAllTextAsync(file));
        Assert.Null(Assert.Single(vendor.Ranges));                        // nothing was asked to continue
        Assert.Empty(Directory.GetFiles(root, "*.part*"));                // and the stale bytes are gone
    }

    /// <summary>
    /// The other direction, so the fix is a binding and not the removal of resuming: a part of THIS
    /// download, at this URL and this length, is still continued rather than fetched again. A 459 MB
    /// installer over a domestic line is why this exists.
    /// </summary>
    [Fact]
    public async Task An_interrupted_download_of_this_file_is_resumed_rather_than_restarted()
    {
        var root = Scratch();
        var dest = Path.Combine(root, "ATASPlatform.exe");
        var body = "REAL-INSTALLER-BYTES-THAT-ARRIVE-IN-TWO-GOES"u8.ToArray();

        using var vendor = new Vendor(body, abortAfter: 12);

        await Assert.ThrowsAnyAsync<Exception>(() => Downloader.DownloadAsync(vendor.Url, dest, NoChecksum));
        var part = Assert.Single(Directory.GetFiles(root, "*.part*"));
        Assert.Equal(12, new FileInfo(part).Length);
        Assert.EndsWith($"-{body.Length}", part);                         // bound to what it is a part of

        var file = await Downloader.DownloadAsync(vendor.Url, dest, NoChecksum);

        Assert.Equal(body, await File.ReadAllBytesAsync(file));
        Assert.Equal([null, "bytes=12-"], vendor.Ranges);
        Assert.Empty(Directory.GetFiles(root, "*.part*"));
    }

    /// <summary>
    /// The same URL, a part of the right shape, and a body that is no longer the same length. The
    /// server answers the range; the prefix is thrown away and the whole file fetched once.
    /// </summary>
    [Fact]
    public async Task A_part_of_this_url_but_of_a_different_length_is_discarded_not_appended_to()
    {
        var root = Scratch();
        var dest = Path.Combine(root, "ATASPlatform.exe");
        var body = "A-SHORTER-INSTALLER"u8.ToArray();

        using var vendor = new Vendor(body);
        // Written by hand at the name a part of a 999-byte body would have had: same destination,
        // same URL, a length the vendor no longer serves.
        var key = Path.GetFileName(dest) + ".part-";
        var stale = Directory.GetFiles(root).FirstOrDefault(f => Path.GetFileName(f).StartsWith(key, StringComparison.Ordinal));
        Assert.Null(stale);
        await File.WriteAllTextAsync(dest + $".part-{UrlKey(vendor.Url)}-999", "STALE-PREFIX;");

        var file = await Downloader.DownloadAsync(vendor.Url, dest, NoChecksum);

        Assert.Equal(body, await File.ReadAllBytesAsync(file));
        Assert.Equal(["bytes=13-", null], vendor.Ranges);                 // asked, refused it, started again
        Assert.Empty(Directory.GetFiles(root, "*.part*"));
    }

    /// <summary>
    /// The second half of the finding: a checksum-less install is a decision the owner can read
    /// afterwards, not an argument somebody left out. The words are the caller's.
    /// </summary>
    [Fact]
    public async Task An_install_with_no_checksum_writes_the_decision_and_the_reason()
    {
        var root = Scratch();
        var previous = Downloader.RecordDecision;
        var recorded = new List<string>();
        Downloader.RecordDecision = t => { lock (recorded) recorded.Add(t); };
        try
        {
            using var vendor = new Vendor("REAL-INSTALLER-BYTES"u8.ToArray());
            await Downloader.DownloadAsync(vendor.Url, Path.Combine(root, "ATASPlatform.exe"), NoChecksum);
        }
        finally { Downloader.RecordDecision = previous; }

        var line = Assert.Single(recorded);
        Assert.Contains("ATASPlatform.exe", line);
        Assert.Contains("without checking it against a publisher's checksum", line);
        Assert.Contains("ATAS publishes no checksum for its installer", line);
    }

    /// <summary>
    /// And a pinned hash is still what it was: the file is thrown away rather than installed, and
    /// nothing is recorded as an accepted decision.
    /// </summary>
    [Fact]
    public async Task A_pinned_hash_that_does_not_match_throws_the_file_away()
    {
        var root = Scratch();
        var dest = Path.Combine(root, "ATASPlatform.exe");
        using var vendor = new Vendor("REAL-INSTALLER-BYTES"u8.ToArray());

        var ex = await Assert.ThrowsAsync<TradeAgentException>(() => Downloader.DownloadAsync(
            vendor.Url, dest, Integrity.Pinned(new string('a', 64))));

        Assert.Contains("did not match the publisher's checksum", ex.Message);
        Assert.False(File.Exists(dest));
        Assert.Empty(Directory.GetFiles(root, "*.part*"));
        Assert.Throws<ArgumentException>(() => Integrity.Pinned("   "));
    }

    static string UrlKey(string url) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(url)))[..16];
}

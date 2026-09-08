using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using TradeAgent.Core.Data;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A LOOPBACK STAND-IN FOR data.binance.vision. Every test in this unit talks to this and never to
/// the network: the one real download this unit was allowed is quoted in the brief's report and is
/// evidence about the vendor's URL pattern, not a thing a suite may repeat on every run.
///
/// It answers the two paths the archive has — the monthly zip and its <c>.CHECKSUM</c> sidecar — and
/// 404 for anything it was not given, which is what the vendor answers for a month it has not
/// published (measured: 2026-09 on 2026-09-07).
///
/// <b>IT KEEPS A MARK PER REQUEST</b> (<see cref="Marks"/>): the method, the path, the answer it
/// sent, and whether the write and the close actually completed. A harness that stops answering is
/// indistinguishable from a vendor that has nothing to say unless the harness says what it did, and
/// on windows-latest this suite once spent thirty minutes proving exactly that.
/// </summary>
public sealed class FakeArchive : IDisposable
{
    /// <summary>The methods that carry an entity body. A HEAD answer is headers and nothing else.</summary>
    const string Head = "HEAD";

    readonly HttpListener _http = new();
    readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _sidecars = new(StringComparer.Ordinal);
    readonly ConcurrentQueue<string> _marks = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <param name="answers">
    /// False makes this server ACCEPT every request and answer none of them, for the one thing a
    /// downloader must never read as a verdict about the vendor. Nothing else about it changes.
    /// </param>
    public FakeArchive(bool answers = true)
    {
        Answers = answers;

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
                var method = ctx.Request.HttpMethod;
                Mark($"got {method} {path}");

                if (!Answers) { Mark("answering nothing, on purpose"); continue; }

                try
                {
                    if (AlwaysAnswer is { } always)
                    {
                        ctx.Response.StatusCode = (int)always;
                        Mark($"answering {(int)always} to everything, on purpose");
                        continue;
                    }

                    byte[]? body = null;
                    var what = "200";

                    if (_sidecars.TryGetValue(path, out var text))
                    {
                        body = Encoding.UTF8.GetBytes(text);
                        what = "200 sidecar";
                    }
                    else if (_files.TryGetValue(path, out var bytes))
                    {
                        body = bytes;
                        what = "200 zip";
                    }

                    ctx.Response.StatusCode = body is null ? 404 : 200;
                    if (body is not null) ctx.Response.ContentLength64 = body.Length;

                    // A HEAD IS ANSWERED WITH THE HEADERS AND NOTHING ELSE. Writing the entity body
                    // to a HEAD response is what cost windows-latest thirty minutes, measured on the
                    // runner: http.sys allows no body on a HEAD, so the write threw
                    // `ProtocolViolationException: Bytes to be written to the stream exceed the
                    // Content-Length bytes size specified.`, the throw skipped the `Close` that was
                    // then inside this `try`, the response was never finished, and the client sat on
                    // the shared 30-minute client timeout before reading the silence as "the vendor
                    // has not published this month". ubuntu and macOS let the write through, so
                    // nobody noticed the harness was sending a body nobody had asked for.
                    if (body is not null && !string.Equals(method, Head, StringComparison.OrdinalIgnoreCase))
                    {
                        Mark($"answering {what}, {body.Length} bytes");
                        await ctx.Response.OutputStream.WriteAsync(body);
                        Mark("write returned");
                    }
                    else
                    {
                        Mark(body is null
                            ? "answering 404"
                            : $"answering {what}, {body.Length} bytes declared, no body because this is a HEAD");
                    }
                }
                catch (Exception ex) { Mark($"THREW {ex.GetType().Name}: {One(ex.Message)}"); }
                finally
                {
                    // THE CLOSE IS THE ANSWER, so it is not optional and it is not inside the try. A
                    // handler that threw halfway still owes the client a finished response; leaving
                    // one open turns a harness fault into a client-side timeout somewhere else.
                    try { ctx.Response.Close(); Mark("closed"); }
                    catch (Exception ex) { Mark($"close THREW {ex.GetType().Name}: {One(ex.Message)}"); }
                }
            }
        });
    }

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    public Task Serving { get; }

    /// <inheritdoc cref="FakeArchive(bool)"/>
    public bool Answers { get; }

    /// <summary>
    /// When set, every request is answered with this status and no body — a CDN having a bad
    /// afternoon. It is not 404: a vendor that says 503 has said nothing about the month.
    /// </summary>
    public HttpStatusCode? AlwaysAnswer { get; set; }

    /// <summary>What this server received and what it did about it, oldest first.</summary>
    public IReadOnlyList<string> Marks => [.. _marks];

    void Mark(string what) => _marks.Enqueue($"{_clock.ElapsedMilliseconds,7} ms  srv {what}");

    static string One(string s) => s.ReplaceLineEndings(" ");

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

    public static string PathOf(string pair, DateOnly month) =>
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

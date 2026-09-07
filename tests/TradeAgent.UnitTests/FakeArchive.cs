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

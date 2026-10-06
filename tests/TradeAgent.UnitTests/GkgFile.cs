using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using TradeAgent.Core.Data;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// GKG FILES IN GDELT'S OWN SHAPE, BUILT BY THE TEST (<c>U-tape-archive</c>): 27 tab-separated fields a row, one
/// row a line, zipped as one deflated entry named <c>&lt;label&gt;.gkg.csv</c> — the shape measured on 2026-10-06
/// (<c>docs/RESEARCH-REQUIRED.md</c>, C5e). Nothing here is GDELT's data; the rows are written to be kept or not.
/// </summary>
public static class GkgFile
{
    /// <summary>
    /// One row of <paramref name="label"/>'s batch. Every field is a plausible GKG field; the ones the filter reads
    /// are the arguments, and the page title goes inside <c>V2EXTRASXML</c> as GDELT writes it.
    /// </summary>
    public static string[] Row(DateTimeOffset label, int serial, string themes = "SOC_GENERALCRIME", string persons = "",
        string organizations = "", string allNames = "", string title = "Council approves new library hours",
        string? id = null, string? date = null, string gcam = "wc:120,c1.4:2")
    {
        var l = GdeltGkg.LabelText(label);
        return
        [
            id ?? $"{l}-{serial}", date ?? l, "1", "example-news.test", $"https://example-news.test/story/{serial}",
            "", "", themes, string.Join(';', themes.Split(';').Select((t, i) => $"{t},{100 + i}")), "", "",
            persons, "", organizations, "", "-1.2,2.1,3.3,5.4,20.1,0.1,120", "", gcam, "", "", "", "", "", allNames,
            "", "", $"<PAGE_PRECISEPUBTIMESTAMP>{l}</PAGE_PRECISEPUBTIMESTAMP><PAGE_TITLE>{title}</PAGE_TITLE>"
        ];
    }

    /// <summary>The rows as the file holds them: UTF-8, one line each, each ending in a line break.</summary>
    public static byte[] Csv(IEnumerable<string[]> rows) =>
        Encoding.UTF8.GetBytes(string.Concat(rows.Select(r => string.Join('\t', r) + "\n")));

    /// <summary>The CSV zipped as GDELT zips it: one deflated entry named for the label, sizes in its header.</summary>
    public static byte[] Zip(DateTimeOffset label, byte[] csv, string? entryName = null)
    {
        using var into = new MemoryStream();
        using (var zip = new ZipArchive(into, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(entryName ?? GdeltGkg.LabelText(label) + ".gkg.csv", CompressionLevel.Optimal);
            using var s = entry.Open();
            s.Write(csv);
        }
        return into.ToArray();
    }

    /// <summary>A zip of these rows.</summary>
    public static byte[] Zip(DateTimeOffset label, params string[][] rows) => Zip(label, Csv(rows));

    public static string Md5(byte[] bytes) => Convert.ToHexStringLower(MD5.HashData(bytes));

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The <c>lastupdate.txt</c> GDELT serves, its GKG line naming <paramref name="host"/> — GDELT's own unless a test says otherwise.</summary>
    public static string Listing(DateTimeOffset label, byte[] zip, string? host = null, string? md5 = null)
    {
        var l = GdeltGkg.LabelText(label);
        var h = host ?? GdeltGkg.Host;
        return $"73759 2ccb57ac55f509950a49742c6392b58e http://{h}/gdeltv2/{l}.export.CSV.zip\n"
               + $"90617 413f446e179a7b48f154db29757cccbd http://{h}/gdeltv2/{l}.mentions.CSV.zip\n"
               + $"{zip.Length} {md5 ?? Md5(zip)} http://{h}/gdeltv2/{l}.gkg.csv.zip\n";
    }

    /// <summary>Reads a file through <see cref="GdeltGkg.ReadBatchAsync"/> as the recorder does, from a stream that arrives in pieces.</summary>
    public static Task<GkgBatchRead> Read(byte[] zip, DateTimeOffset label, long maxBytes = 16L * 1024 * 1024,
        long keptBudget = long.MaxValue) =>
        GdeltGkg.ReadBatchAsync(new Trickle(zip), label, maxBytes, keptBudget);

    /// <summary>A stream that hands over at most 1,000 bytes a read, like a socket: no reader here may assume it gets a whole file at once.</summary>
    sealed class Trickle(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 1000)]);

        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1000));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 1000)], ct);

        public override bool CanSeek => false;
    }
}

using System.Text;
using System.Text.Json;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// GDELT'S GKG FILES, READ BY THIS BUILD (<c>U-tape-archive</c> item 1). No network: every file is built by
/// <see cref="GkgFile"/> in the shape measured on 2026-10-06 and read from a stream that arrives in pieces.
/// </summary>
public class GdeltGkgTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Label = new(2026, 10, 6, 0, 45, 0, TimeSpan.Zero);

    static JsonElement Json(string payload) => JsonDocument.Parse(payload).RootElement.Clone();

    /// <summary>
    /// THE FILTER KEEPS THE CRYPTO ROWS AND NOTHING ELSE, EACH WHOLE — and a row that is not its label's, or a kept row
    /// the tape cannot hold whole, refuses the FILE. Kept: the Bitcoin theme; a coin's name as a word in each of the
    /// three name fields; a coin in the title, behind a character reference, in any case. Not kept: a council story, a
    /// pair that only contains a coin's letters ("BNBUSDT", "Rippled"). A non-crypto row that is not UTF-8, or is long,
    /// is read past; a kept one is not, and neither is a row created in another batch or a GKGRECORDID named twice.
    /// </summary>
    [Fact]
    public async Task Only_crypto_rows_are_kept_and_a_mislabelled_or_oversize_row_refuses_the_batch()
    {
        string[][] rows =
        [
            GkgFile.Row(Label, 0),
            GkgFile.Row(Label, 1, themes: "ECON_BITCOIN;WB_1920_FINANCIAL_SECTOR_DEVELOPMENT"),
            GkgFile.Row(Label, 2, persons: "michael saylor;vitalik buterin", organizations: "solana foundation"),
            GkgFile.Row(Label, 3, allNames: "Ripple Labs,12;Brad Garlinghouse,40"),
            GkgFile.Row(Label, 4, persons: "a fan of xrp"),
            GkgFile.Row(Label, 5, title: "DOGECOIN &#x2013; what the rally means"),
            GkgFile.Row(Label, 6, title: "BNBUSDT and Rippled lead the pairs table", allNames: "Binancee Arena,3"),
            GkgFile.Row(Label, 7, gcam: "wc:9," + new string('c', 70_000)),
            GkgFile.Row(Label, 8, title: "Bitcoin and ether: the week ahead")
        ];

        var zip = GkgFile.Zip(Label, rows);
        var read = await GkgFile.Read(zip, Label);
        log.WriteLine($"rows={read.Rows} kept={read.Kept.Count} keptBytes={read.KeptBytes} refused={read.Refused ?? "-"}");

        Assert.Null(read.Refused);
        Assert.Equal(9, read.Rows);
        Assert.Equal(new[] { "20261006004500-1", "20261006004500-2", "20261006004500-3", "20261006004500-4", "20261006004500-5",
                             "20261006004500-8" }, read.Kept.Select(k => k.Subject));
        Assert.All(read.Kept, k => Assert.Equal(Label, k.SourceTime));
        Assert.Equal(read.Kept.Sum(k => (long)Encoding.UTF8.GetByteCount(k.Payload)), read.KeptBytes);

        // THE HASHES ARE OF EVERY BYTE OF THE FILE, computed by this build.
        Assert.Equal(zip.Length, read.Bytes);
        Assert.Equal(GkgFile.Md5(zip), read.Md5);
        Assert.Equal(GkgFile.Sha256(zip), read.Sha256);

        // A KEPT ROW IS ITS 27 CODEBOOK-NAMED FIELDS, WHOLE — the title's reference as GDELT wrote it, canonical JSON.
        var five = Json(read.Kept.Single(k => k.Subject.EndsWith("-5", StringComparison.Ordinal)).Payload);
        Assert.Equal(GdeltGkg.Fields.Order(StringComparer.Ordinal), five.EnumerateObject().Select(p => p.Name));
        for (var i = 0; i < GdeltGkg.Fields.Count; i++)
            Assert.Equal(rows[5][i], five.GetProperty(GdeltGkg.Fields[i]).GetString());
        Assert.Equal(TapeJson.Canonical(read.Kept[0].Payload), read.Kept[0].Payload);

        // A ROW CREATED IN ANOTHER BATCH REFUSES THE FILE, by its id or by its date, and nothing is kept.
        var otherBatch = GdeltGkg.LabelText(Label.AddMinutes(-15));
        foreach (var bad in new[]
                 {
                     GkgFile.Row(Label, 9, id: $"{otherBatch}-9"),
                     GkgFile.Row(Label, 9, date: otherBatch),
                     GkgFile.Row(Label, 9, id: $"{GdeltGkg.LabelText(Label)}-X9"),
                     GkgFile.Row(Label, 1)
                 })
        {
            var refused = await GkgFile.Read(GkgFile.Zip(Label, [.. rows, bad]), Label);
            log.WriteLine("refused: " + refused.Refused);
            Assert.NotNull(refused.Refused);
            Assert.Empty(refused.Kept);
            Assert.False(refused.OverBudget);
        }

        // A KEPT ROW THE TAPE CANNOT HOLD WHOLE REFUSES THE FILE: past 64 KB as the tape keeps it.
        var oversize = await GkgFile.Read(GkgFile.Zip(Label, [.. rows,
            GkgFile.Row(Label, 9, themes: "ECON_BITCOIN", gcam: "wc:9," + new string('c', TapeStore.MaxPayloadBytes))]), Label);
        log.WriteLine("refused: " + oversize.Refused);
        Assert.StartsWith("the kept row 20261006004500-9 is ", oversize.Refused, StringComparison.Ordinal);
        Assert.Empty(oversize.Kept);

        // NOT UTF-8 — measured in 2 of 24 GDELT files on 2026-10-06, a raw Latin-1 byte in an address: read past in a
        // row the filter does not keep; a refusal of the file in a row it keeps, because a kept row is kept whole.
        static byte[] Latin1(string[] row) =>
            [.. Encoding.UTF8.GetBytes(string.Join('\t', row[..^1]) + "\t<PAGE_TITLE>fl"), 0xE1, .. "vio</PAGE_TITLE>\n"u8];

        var readPast = await GkgFile.Read(GkgFile.Zip(Label, [.. GkgFile.Csv(rows), .. Latin1(GkgFile.Row(Label, 9))]), Label);
        Assert.Null(readPast.Refused);
        Assert.Equal((10, 6), (readPast.Rows, readPast.Kept.Count));

        var keptLatin1 = await GkgFile.Read(
            GkgFile.Zip(Label, [.. GkgFile.Csv(rows), .. Latin1(GkgFile.Row(Label, 9, themes: "ECON_BITCOIN"))]), Label);
        log.WriteLine("refused: " + keptLatin1.Refused);
        Assert.Equal("the kept row 20261006004500-9 is not UTF-8, so it cannot be kept whole and the file is not stored in part",
            keptLatin1.Refused);
        Assert.Empty(keptLatin1.Kept);
    }

    /// <summary>
    /// A FILE THAT IS NOT ITS LABEL'S ZIP IS NOT READ: not a zip, an entry with another name, one byte past the cap
    /// the caller set. And the kept rows passing the caller's budget — the daily cap — is a refusal that says it is
    /// the budget, not the file.
    /// </summary>
    [Fact]
    public async Task A_file_that_is_not_its_labels_zip_or_passes_a_bound_is_refused_in_words()
    {
        var rows = new[] { GkgFile.Row(Label, 0, themes: "ECON_BITCOIN"), GkgFile.Row(Label, 1, title: "Solana slips") };
        var zip = GkgFile.Zip(Label, rows);

        var whole = await GkgFile.Read(zip, Label, maxBytes: zip.Length);
        Assert.Null(whole.Refused);
        Assert.Equal(2, whole.Kept.Count);

        var cases = new (string Name, Task<GkgBatchRead> Read, string Starts)[]
        {
            ("not a zip", GkgFile.Read("not a zip at all, but long enough for a header"u8.ToArray(), Label), "the file is not a zip"),
            ("another entry", GkgFile.Read(GkgFile.Zip(Label, GkgFile.Csv(rows), "20261006003000.gkg.csv"), Label),
                "the zip's entry is not 20261006004500.gkg.csv"),
            ("another label", GkgFile.Read(zip, Label.AddMinutes(15)), "the zip's entry is not 20261006010000.gkg.csv"),
            ("a byte too many", GkgFile.Read(zip, Label, maxBytes: zip.Length - 1), "the file is longer than"),
            ("cut short", GkgFile.Read(zip[..(zip.Length / 2)], Label), "the file")
        };

        foreach (var (name, task, starts) in cases)
        {
            var read = await task;
            log.WriteLine($"{name}: {read.Refused}");
            Assert.StartsWith(starts, read.Refused, StringComparison.Ordinal);
            Assert.Empty(read.Kept);
            Assert.Null(read.Md5);
            Assert.False(read.OverBudget);
        }

        var budget = Encoding.UTF8.GetByteCount(whole.Kept[0].Payload);
        var capped = await GkgFile.Read(zip, Label, keptBudget: budget);
        log.WriteLine("budget: " + capped.Refused);
        Assert.True(capped.OverBudget);
        Assert.Empty(capped.Kept);
        Assert.Contains("daily cap", capped.Refused, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE LISTING IS READ FOR ITS LABEL, SIZE AND MD5 ONLY, and only from GDELT's own host. Measured on 2026-10-06:
    /// three lines, <c>size md5 http://&lt;GDELT's host&gt;/gdeltv2/&lt;label&gt;.&lt;feed&gt;.zip</c>. A GKG line on any
    /// other host, a file that is not <c>&lt;14 digits&gt;.gkg.csv.zip</c> at a fifteen-minute label, a size or MD5 that
    /// is not one, two GKG lines or none: each refused in words.
    /// </summary>
    [Fact]
    public void The_listing_is_read_for_its_label_size_and_md5_only_and_only_from_gdelts_host()
    {
        var zip = GkgFile.Zip(Label, GkgFile.Row(Label, 0));

        Assert.True(GdeltGkg.TryReadListing(GkgFile.Listing(Label, zip), out var listing, out var why), why);
        Assert.Equal(new GkgListing(Label, zip.Length, GkgFile.Md5(zip)), listing);

        // UPPER-CASE HEX IS THE SAME MD5, and the https spelling of the host is the same host.
        Assert.True(GdeltGkg.TryReadListing(
            $"{zip.Length} {GkgFile.Md5(zip).ToUpperInvariant()} https://{GdeltGkg.Host}/gdeltv2/20261006004500.gkg.csv.zip", out var upper, out _));
        Assert.Equal(GkgFile.Md5(zip), upper!.Md5);

        var l = GdeltGkg.LabelText(Label);
        var md5 = GkgFile.Md5(zip);
        string[] refused =
        [
            GkgFile.Listing(Label, zip, host: "127.0.0.1:9"),
            GkgFile.Listing(Label, zip, host: "data.example.test"),
            GkgFile.Listing(Label, zip, host: GdeltGkg.Host + ".example.test"),
            $"10 {md5} http://{GdeltGkg.Host}:8080/gdeltv2/{l}.gkg.csv.zip",
            $"10 {md5} http://user@{GdeltGkg.Host}/gdeltv2/{l}.gkg.csv.zip",
            $"10 {md5} x http://{GdeltGkg.Host}/gdeltv2/{l}.gkg.csv.zip",
            $"10 {md5} http://{GdeltGkg.Host}/other/{l}.gkg.csv.zip",
            $"10 {md5} http://{GdeltGkg.Host}/gdeltv2/2026100600450.gkg.csv.zip",
            $"10 {md5} http://{GdeltGkg.Host}/gdeltv2/20261006004000.gkg.csv.zip",
            $"10 {md5} http://{GdeltGkg.Host}/gdeltv2/20261306004500.gkg.csv.zip",
            $"-10 {md5} http://{GdeltGkg.Host}/gdeltv2/{l}.gkg.csv.zip",
            $"10 {md5[..31]}g http://{GdeltGkg.Host}/gdeltv2/{l}.gkg.csv.zip",
            $"10 {md5} http://{GdeltGkg.Host}/gdeltv2/{l}.gkg.csv.zip\n10 {md5} http://{GdeltGkg.Host}/gdeltv2/{l}.gkg.csv.zip",
            $"10 {md5} http://{GdeltGkg.Host}/gdeltv2/{l}.export.CSV.zip",
            ""
        ];

        foreach (var body in refused)
        {
            Assert.False(GdeltGkg.TryReadListing(body, out var none, out var because), body);
            log.WriteLine($"refused: {because} | {body.ReplaceLineEndings(" ")}");
            Assert.Null(none);
            Assert.False(string.IsNullOrWhiteSpace(because));
        }
    }

    /// <summary>
    /// A LABEL IS A WHOLE FIFTEEN MINUTES, UTC, and every address is built from one on a root the caller names —
    /// GDELT's own for the app, a loopback for a test. The label a file's address names reads back.
    /// </summary>
    [Fact]
    public void Labels_are_fifteen_minute_instants_and_every_address_is_built_from_one()
    {
        Assert.True(GdeltGkg.TryParseLabel("20261006004500", out var label));
        Assert.Equal(Label, label);
        foreach (var bad in new[] { "20261006004501", "20261006004000", "2026100600450", "202610060045000", "2026100600450x", "20261306004500", "" })
            Assert.False(GdeltGkg.TryParseLabel(bad, out _), bad);

        Assert.Equal(Label, GdeltGkg.LabelAtOrBefore(Label.AddMinutes(14).AddSeconds(59)));
        Assert.Equal(Label, GdeltGkg.LabelAtOrBefore(Label));
        Assert.Equal(Label, GdeltGkg.LabelAtOrBefore(new DateTimeOffset(2026, 10, 6, 2, 50, 0, TimeSpan.FromHours(2))));
        Assert.True(GdeltGkg.IsLabel(Label));
        Assert.False(GdeltGkg.IsLabel(Label.AddSeconds(1)));

        Assert.Equal("http://127.0.0.1:9/gdeltv2/20261006004500.gkg.csv.zip", GdeltGkg.BatchUrl("http://127.0.0.1:9/", Label));
        Assert.Equal("http://127.0.0.1:9/gdeltv2/lastupdate.txt", GdeltGkg.ListingUrl("http://127.0.0.1:9"));
        Assert.Equal(GdeltGkg.BaseUrl + "/gdeltv2/20261006004500.gkg.csv.zip", GdeltGkg.BatchUrl(GdeltGkg.BaseUrl, Label));
        Assert.Throws<ArgumentException>(() => GdeltGkg.BatchUrl(GdeltGkg.BaseUrl, Label.AddMinutes(1)));

        Assert.Equal(Label, GdeltGkg.LabelOf(GdeltGkg.BatchUrl("http://127.0.0.1:9", Label)));
        Assert.Null(GdeltGkg.LabelOf("http://127.0.0.1:9/gdeltv2/lastupdate.txt"));

        Assert.Equal(27, GdeltGkg.Fields.Count);
        Assert.Equal(27, GdeltGkg.Fields.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(("GKGRECORDID", "V2.1DATE", "V2EXTRASXML"), (GdeltGkg.Fields[0], GdeltGkg.Fields[1], GdeltGkg.Fields[26]));
        Assert.Equal("gkg-crypto-v1", GdeltGkg.Filter);
        Assert.Equal(TimeSpan.FromSeconds(900), GdeltGkg.Cadence);
    }
}

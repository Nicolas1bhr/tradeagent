using System.Text;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Tests;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// AN ARCHIVE FILE IN THE TAPE'S STORE (<c>U-tape-archive</c> item 2). No network: every fetch is a record built
/// by the test, with the built-in origin read off this build's own constant, because what is under test is what
/// the only writer of <c>state/tape.db</c> does with a file it is handed — one transaction, a record it counts
/// itself, and a class it computes from what it recorded.
/// </summary>
public class TapeArchiveStoreTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Label = new(2026, 10, 5, 4, 0, 0, TimeSpan.Zero);

    static string NewFile() => Path.Combine(TestEnv.Home, $"tape-{Guid.NewGuid():n}.db");

    static TapeFetch Fetch(DateTimeOffset label, DateTimeOffset receivedAt, string? root = null) => new()
    {
        Source = GdeltGkg.Source,
        Series = "gkg-backfill",
        Url = GdeltGkg.BatchUrl(root ?? GdeltGkg.BaseUrl, label),
        RequestedAt = receivedAt.AddSeconds(-1),
        ReceivedAt = receivedAt,
        HttpStatus = 200,
        BodySha256 = new string('5', 64)
    };

    static TapeArchiveBatch Batch(DateTimeOffset label, DateTimeOffset? lastModified, string? published = null,
        string sha256 = "", int rows = 900) => new()
    {
        RecordSeries = GdeltGkg.BatchSeries,
        RecordSubject = GdeltGkg.BatchSubject,
        ItemsSeries = GdeltGkg.ItemsSeries,
        Label = label,
        Bytes = 4_126_527,
        PublishedMd5 = published ?? "2b4bc982748899a2ce73887198e0d9a0",
        ComputedMd5 = "2b4bc982748899a2ce73887198e0d9a0",
        Sha256 = sha256.Length == 64 ? sha256 : new string('5', 64),
        LastModified = lastModified,
        Rows = rows,
        Filter = GdeltGkg.Filter
    };

    static TapeItem Row(DateTimeOffset label, int serial, string title = "Bitcoin and the week ahead")
    {
        var id = $"{GdeltGkg.LabelText(label)}-{serial}";
        return new(id, label, $$"""{"GKGRECORDID":"{{id}}","V2.1DATE":"{{GdeltGkg.LabelText(label)}}","V2EXTRASXML":"<PAGE_TITLE>{{title}}</PAGE_TITLE>"}""");
    }

    static IReadOnlyList<TapeItem> Rows(DateTimeOffset label) => [Row(label, 3), Row(label, 17)];

    /// <summary>
    /// A LATE FILE IS O-PIT IF GDELT'S STORAGE DATES IT NO LATER THAN ITS LABEL, AND O-ARCH IF AFTER — the record and
    /// every row of it alike, computed by the store from what it recorded. Measured on 2026-10-06: 24 files dated
    /// 569–724 s before their labels. Around that rule: on time is O-LIVE by the live rule (900 + 30 s either side of
    /// the label); an MD5 that disagrees, a storage that gave no date, or any other origin is O-ARCH; and a later
    /// revision is never above the one before it.
    /// </summary>
    [Fact]
    public void A_late_batch_is_pit_if_published_before_its_label_and_arch_if_after()
    {
        using var store = new TapeStore(NewFile());
        var late = TimeSpan.FromDays(1);

        string ClassesOf(TapeAppend a) => string.Join(",", store.ObservationsOf(a.FetchId).Select(o => o.EvidenceClass).Distinct());

        var before = Label;
        var after = Label.AddMinutes(15);
        var pit = store.AppendArchive(Fetch(before, before + late), Batch(before, before.AddSeconds(-724)), Rows(before));
        var arch = store.AppendArchive(Fetch(after, after + late), Batch(after, after.AddSeconds(1)), Rows(after));
        log.WriteLine($"written {late} late, dated 724 s before its label: {ClassesOf(pit)}; dated 1 s after: {ClassesOf(arch)}");

        Assert.All(store.ObservationsOf(pit.FetchId), o => Assert.Equal(TapeClass.Pit, o.EvidenceClass));
        Assert.All(store.ObservationsOf(arch.FetchId), o => Assert.Equal(TapeClass.Arch, o.EvidenceClass));
        Assert.Equal(3, store.ObservationsOf(pit.FetchId).Count);

        // DATED AT ITS LABEL EXACTLY IS NOT AFTER IT.
        var exact = Label.AddMinutes(30);
        Assert.Equal(TapeClass.Pit, ClassesOf(store.AppendArchive(Fetch(exact, exact + late), Batch(exact, exact), Rows(exact))));

        // AROUND THE RULE: on time either side of the label, whatever the file's date; and four ways not to be a
        // checked print — an MD5 that disagrees, no date, another origin, a row a file added.
        var cases = new (string Name, TimeSpan Offset, string Class)[]
        {
            ("930 s after its label, dated after", TimeSpan.FromSeconds(930), TapeClass.Live),
            ("660 s before its label, dated after", TimeSpan.FromSeconds(-660), TapeClass.Live),
            ("931 s after its label, dated after", TimeSpan.FromSeconds(931), TapeClass.Arch)
        };

        var at = Label.AddHours(1);
        foreach (var (name, offset, expected) in cases)
        {
            at = at.AddMinutes(15);
            var appended = store.AppendArchive(Fetch(at, at + offset), Batch(at, at.AddSeconds(5)), Rows(at));
            log.WriteLine($"{name}: {ClassesOf(appended)}");
            Assert.Equal(expected, ClassesOf(appended));
        }

        at = at.AddMinutes(15);
        Assert.Equal(TapeClass.Arch, ClassesOf(store.AppendArchive(Fetch(at, at + late),
            Batch(at, at.AddSeconds(-700), published: "0b4bc982748899a2ce73887198e0d9a0"), Rows(at))));
        at = at.AddMinutes(15);
        Assert.Equal(TapeClass.Arch, ClassesOf(store.AppendArchive(Fetch(at, at + late), Batch(at, null), Rows(at))));
        at = at.AddMinutes(15);
        Assert.Equal(TapeClass.Arch, ClassesOf(store.AppendArchive(Fetch(at, at + late, root: "http://127.0.0.1:9"),
            Batch(at, at.AddSeconds(-700)), Rows(at))));
        at = at.AddMinutes(15);
        Assert.Equal(TapeClass.Arch, ClassesOf(store.AppendArchive(Fetch(at, at + late) with { Source = "my-news" },
            Batch(at, at.AddSeconds(-700)), Rows(at))));

        // NEVER UPGRADED: a rebuilt PIT file read later, dated after its label, revises down to archive — and its
        // rebuild re-read on time cannot lift it back.
        var rebuilt = store.AppendArchive(Fetch(before, before + late * 2), Batch(before, before.AddDays(1), sha256: new string('6', 64)),
            [Row(before, 3, "Bitcoin and the week ahead, updated"), Row(before, 17)]);
        var revisions = store.Revisions(GdeltGkg.Source, GdeltGkg.ItemsSeries, TapeStore.NaturalKey($"{GdeltGkg.LabelText(before)}-3", before));
        Assert.Equal((TapeClass.Pit, TapeClass.Arch), (revisions[0].EvidenceClass, revisions[1].EvidenceClass));
        Assert.Equal(2, rebuilt.Stored);

        var again = store.AppendArchive(Fetch(before, before.AddSeconds(2)), Batch(before, before.AddSeconds(-724), sha256: new string('7', 64)),
            [Row(before, 3, "Bitcoin and the week ahead, again")]);
        Assert.Equal(TapeClass.Arch, ClassesOf(again));
    }

    /// <summary>
    /// ONE FILE, ONE TRANSACTION, A RECORD THE STORE COUNTS ITSELF. The attempt, the file's record and its kept rows
    /// commit together; the record holds the address, size, both MD5s, SHA-256, Last-Modified, rows, the kept count and
    /// kept bytes this store counted, and the filter — and no arrival instant, which is its own <c>received_at</c>. The
    /// same file again writes its attempt and nothing else; a rebuilt one is a second revision beside the first. What
    /// is refused is refused before the transaction opens: no attempt is recorded with half a file.
    /// </summary>
    [Fact]
    public void An_archive_file_is_one_transaction_and_its_record_is_counted_by_the_store()
    {
        var file = NewFile();
        using var store = new TapeStore(file);
        var lastModified = Label.AddSeconds(-724);

        var first = store.AppendArchive(Fetch(Label, Label.AddSeconds(3)), Batch(Label, lastModified), Rows(Label));
        Assert.Equal(new TapeAppend(first.FetchId, 3, 3, 0, 0), first);

        var rows = store.ObservationsOf(first.FetchId);
        var record = rows[0];
        Assert.Equal((GdeltGkg.BatchSeries, GdeltGkg.BatchSubject, Label, Label.AddSeconds(3), 1),
            (record.Series, record.Subject, record.SourceTime, record.ReceivedAt, record.Revision));
        Assert.Equal(new[] { GdeltGkg.ItemsSeries, GdeltGkg.ItemsSeries }, rows.Skip(1).Select(o => o.Series));
        Assert.All(rows, o => Assert.Equal(TapeClass.Live, o.EvidenceClass));

        using var payload = JsonDocument.Parse(record.Payload!);
        var r = payload.RootElement;
        log.WriteLine(record.Payload);
        Assert.Equal(GdeltGkg.BatchUrl(GdeltGkg.BaseUrl, Label), r.GetProperty("url").GetString());
        Assert.Equal(4_126_527, r.GetProperty("bytes").GetInt64());
        Assert.Equal("2b4bc982748899a2ce73887198e0d9a0", r.GetProperty("md5Published").GetString());
        Assert.Equal("2b4bc982748899a2ce73887198e0d9a0", r.GetProperty("md5Computed").GetString());
        Assert.Equal(new string('5', 64), r.GetProperty("sha256").GetString());
        Assert.Equal(lastModified, DateTimeOffset.Parse(r.GetProperty("lastModified").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal((900, 2), (r.GetProperty("rows").GetInt32(), r.GetProperty("kept").GetInt32()));
        Assert.Equal(Rows(Label).Sum(i => (long)Encoding.UTF8.GetByteCount(TapeJson.Canonical(i.Payload))), r.GetProperty("keptBytes").GetInt64());
        Assert.Equal(GdeltGkg.Filter, r.GetProperty("filter").GetString());
        Assert.False(r.TryGetProperty("receivedAt", out _));

        // THE SAME FILE AGAIN: its attempt, and nothing else.
        var second = store.AppendArchive(Fetch(Label, Label.AddHours(5)), Batch(Label, lastModified), Rows(Label));
        Assert.Equal(new TapeAppend(second.FetchId, 3, 0, 0, 3), second);
        Assert.Empty(store.ObservationsOf(second.FetchId));

        // A REBUILT FILE: the record's second revision beside the first, the unchanged rows writing nothing.
        var rebuilt = store.AppendArchive(Fetch(Label, Label.AddHours(6)), Batch(Label, Label.AddHours(1), sha256: new string('6', 64)), Rows(Label));
        Assert.Equal(new TapeAppend(rebuilt.FetchId, 3, 1, 1, 2), rebuilt);
        var records = store.Revisions(GdeltGkg.Source, GdeltGkg.BatchSeries, TapeStore.NaturalKey(GdeltGkg.BatchSubject, Label));
        Assert.Equal((2, Label.AddSeconds(3), Label.AddHours(6)), (records.Count, records[0].ReceivedAt, records[1].ReceivedAt));

        // REFUSED BEFORE THE TRANSACTION: a row not at its file's label, a row named twice, a malformed record.
        var fetchesBefore = store.Fetches(limit: 1000).Count;
        var next = Label.AddMinutes(15);
        Assert.Throws<ArgumentException>(() => store.AppendArchive(Fetch(next, next), Batch(next, null), [Row(next, 1), Row(Label, 2)]));
        Assert.Throws<ArgumentException>(() => store.AppendArchive(Fetch(next, next), Batch(next, null), [Row(next, 1), Row(next, 1)]));
        Assert.Throws<ArgumentException>(() => store.AppendArchive(Fetch(next, next), Batch(next, null, published: "not an md5"), Rows(next)));
        Assert.Throws<ArgumentException>(() => store.AppendArchive(Fetch(next, next), Batch(next, null, rows: 1), Rows(next)));
        Assert.Throws<ArgumentException>(() => store.AppendArchive(Fetch(next, next), Batch(next, null) with { ItemsSeries = GdeltGkg.BatchSeries }, Rows(next)));
        Assert.Throws<ArgumentException>(() => store.AppendArchive(Fetch(next, next), Batch(next, null),
            [new TapeItem("not a subject", next, "{}")]));
        Assert.Equal(fetchesBefore, store.Fetches(limit: 1000).Count);
    }
}

using System.Globalization;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEM 1 — BARS BY DATASET ID, AND THE VERDICT TAKEN ONCE.
///
/// <para>Before this, a dataset could be found by PAIR and read by PATH, and the one reader refused
/// any window over ten thousand bars (`DatasetReader.MaxBars`). A backtest is a twelve-month window
/// of one-minute bars — 525,600 of them — so the reader the pipe op uses cannot serve one, and the
/// caller had no way to name the dataset a result is attached to. `DatasetStore.ById` and
/// <see cref="BarFeed"/> are those two holes; the cap stays exactly where it was, because a pipe
/// reply that was quietly cut short is a different window from the one an agent asked for.</para>
///
/// <para><b>The verdict is taken ONCE, at the start of a run.</b> `DatasetStore.Checked` re-hashes
/// every raw archive file and the normalised file on EVERY call, and a twelve-month dataset is tens
/// of megabytes: taken per chunk it would re-read the whole archive a hundred times over in one
/// backtest. Once at the start is also the only verdict that can be REPORTED — a run whose dataset
/// changed state half way through has no single dataset to attach its result to.</para>
/// </summary>
public class BarFeedTests
{
    static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A directory of this test's own, so two tests never hash each other's bytes.</summary>
    static string Scratch() =>
        Directory.CreateDirectory(Path.Combine(TestEnv.Home, "feeds", Guid.NewGuid().ToString("n"))).FullName;

    /// <summary>
    /// Writes a normalised dataset file of <paramref name="bars"/> ascending minutes and records it
    /// exactly as the app's own collector would — real hashes, a real raw file — so that the
    /// ledger's verification has something true to check.
    /// </summary>
    static (DatasetRecord Row, string Csv, string Raw) Given(
        Database db, int bars, int skipAfter = -1, DateTimeOffset? start = null)
    {
        var dir = Scratch();
        var raw = Path.Combine(dir, "BTCUSDT-1m-2026-01.zip");
        File.WriteAllText(raw, "stand-in for the vendor's monthly archive");

        var csv = Path.Combine(dir, "v1.csv");
        var from = start ?? Start;
        var written = 0;
        using (var writer = new StreamWriter(csv) { NewLine = "\n" })
        {
            writer.WriteLine(KlineNormaliser.Header);
            for (var i = 0; i < bars; i++)
            {
                if (skipAfter >= 0 && i == skipAfter + 1) continue;
                var at = from.AddMinutes(i);
                var close = 100m + i % 7;
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{at.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},100.00,101.00,99.00,{close:0.00},1.00"));
                written++;
            }
        }

        var store = new DatasetStore(db);
        var row = new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 12, [],
            csv, DatasetStore.Sha256(csv)!, written, from, from.AddMinutes(bars - 1),
            skipAfter < 0 ? 0 : 1, [], false, 0, 0, 0, DateTimeOffset.UtcNow,
            DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-01", "https://127.0.0.1/x.zip", DatasetStore.Sha256(raw)!,
                DatasetStore.Sha256(raw)!, new FileInfo(raw).Length, DateTimeOffset.UtcNow,
                KlineTimeUnit.Microseconds, raw)]);

        return (row with { Id = store.Record(row) }, csv, raw);
    }

    [Fact]
    public void A_dataset_is_readable_by_its_ledger_id()
    {
        using var db = TestEnv.NewDb();
        var store = new DatasetStore(db);
        var (row, csv, _) = Given(db, 5);

        var found = store.ById(row.Id);

        Assert.NotNull(found);
        Assert.Equal(row.Id, found.Id);
        Assert.Equal("BTCUSDT", found.Pair);
        Assert.Equal(csv, found.NormalisedPath);
        Assert.Single(found.Files);
        Assert.Null(store.ById(row.Id + 1000));
    }

    [Fact]
    public void A_twelve_month_window_is_refused_by_the_reader_and_served_whole_by_the_feed()
    {
        using var db = TestEnv.NewDb();
        var store = new DatasetStore(db);
        const int year = 365 * 24 * 60;      // 525,600 one-minute bars: twelve months of 2026
        var (row, csv, _) = Given(db, year);

        // The reader the pipe op uses refuses, and does not truncate.
        var window = DatasetReader.Read(csv, null, null);
        Assert.True(window.OverCap);
        Assert.Empty(window.Bars);

        var open = BarFeed.Open(store, row.Id);
        Assert.True(open.Ok, open.Why);

        var count = 0;
        DateTimeOffset? last = null;
        foreach (var bar in open.Feed!.Bars())
        {
            if (last is { } previous) Assert.True(bar.OpenTime > previous, "the feed is ascending");
            last = bar.OpenTime;
            count++;
        }

        Assert.Equal(year, count);
        Assert.Equal(Start.AddMinutes(year - 1), last);
    }

    [Fact]
    public void The_feed_serves_the_window_it_was_asked_for_in_chunks()
    {
        using var db = TestEnv.NewDb();
        var store = new DatasetStore(db);
        var (row, _, _) = Given(db, 1000);

        var open = BarFeed.Open(store, row.Id);
        Assert.True(open.Ok, open.Why);

        var chunks = open.Feed!.Chunks(Start.AddMinutes(100), Start.AddMinutes(349), chunkBars: 64).ToList();

        Assert.Equal(250, chunks.Sum(c => c.Count));
        Assert.Equal([64, 64, 64, 58], chunks.Select(c => c.Count));
        Assert.Equal(Start.AddMinutes(100), chunks[0][0].OpenTime);
        Assert.Equal(Start.AddMinutes(349), chunks[^1][^1].OpenTime);
        Assert.Empty(open.Feed.Bars(Start.AddYears(5), Start.AddYears(6)));
    }

    [Fact]
    public void A_rejected_dataset_feeds_nothing_and_the_refusal_names_the_reason()
    {
        using var db = TestEnv.NewDb();
        var store = new DatasetStore(db);
        var (row, _, raw) = Given(db, 20);

        File.WriteAllText(raw, "somebody changed the archive this dataset was built from");

        var open = BarFeed.Open(store, row.Id);

        Assert.False(open.Ok);
        Assert.Contains("REJECTED", open.Why, StringComparison.Ordinal);
        Assert.Contains("no longer matches the hash recorded for it", open.Why, StringComparison.Ordinal);
        Assert.Null(open.Feed);
        Assert.Equal(DatasetState.REJECTED, store.ById(row.Id)!.State);
    }

    [Fact]
    public void There_is_no_feed_for_a_dataset_this_installation_does_not_have()
    {
        using var db = TestEnv.NewDb();
        var open = BarFeed.Open(new DatasetStore(db), 4242);

        Assert.False(open.Ok);
        Assert.Contains("4242", open.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// The verdict is ONE reading taken at the start. A raw file that disappears half way through a
    /// run does not turn the bars already being streamed into different bars, and re-hashing tens of
    /// megabytes per chunk would make a twelve-month run re-read the whole archive a hundred times.
    /// The NEXT run reads the ledger again and refuses.
    /// </summary>
    [Fact]
    public void The_datasets_verdict_is_taken_once_at_the_start_of_a_run()
    {
        using var db = TestEnv.NewDb();
        var store = new DatasetStore(db);
        var (row, _, raw) = Given(db, 500);

        var open = BarFeed.Open(store, row.Id);
        Assert.True(open.Ok, open.Why);
        Assert.Equal(DatasetState.ACCEPTED, open.Feed!.Dataset.State);

        File.Delete(raw);

        Assert.Equal(500, open.Feed.Bars().Count());
        Assert.False(BarFeed.Open(store, row.Id).Ok);
    }
}

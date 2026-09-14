using System.Text;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Provisioning;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEM 1 — THE COLLECTOR IS AN INTERFACE, AND BINANCE IS ONE IMPLEMENTATION OF IT.
///
/// <para>Before this unit the pipeline held one source's assumptions as constants:
/// <c>BinanceArchive.Interval</c> was the only interval a dataset could be of and
/// <c>MonthsWanted</c> the only coverage a collection could target, so a second source declaring five
/// minutes and ninety days produced a row saying <c>1m</c> over twelve months — a provenance record
/// describing a collection that never happened.</para>
///
/// <para>The other half of the same claim is that generalising it changed NOTHING about Binance. The
/// normalised file's bytes are the dataset's identity (`DatasetStore` re-hashes them on every read),
/// so the proof is a hash measured on the pre-change build and asserted here, not an assurance.</para>
/// </summary>
public class CandleSourceTests
{
    /// <summary>
    /// NOT BTCUSDT. The raw archive folder is app-owned and keyed by the PAIR, and xUnit runs test
    /// classes in parallel: sharing a pair with <c>DatasetLedgerTests</c> had the two collections
    /// writing part files into one directory and deleting each other's. The normalised file's bytes do
    /// not depend on the pair — it holds instants and prices — so the hash below is unaffected.
    /// </summary>
    const string Pair = "XBTUSDT";
    static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// THE MEASURED SHA-256 OF THE NORMALISED FILE THE PRE-CHANGE BUILD PRODUCED for the twelve
    /// fixture months below, at `main`'s tip 39d3e4f, before one line of this unit was written.
    ///
    /// It is a constant rather than a comparison against a second run because the claim is about a
    /// build that no longer exists: what has to stay true is that today's collector writes the SAME
    /// bytes the old one did, and only a number carried over from the old one can say so.
    /// </summary>
    const string BinanceNormalisedSha256Before =
        "6a5958f4f261e333136a652be9f95972726a34af42aefe66da8d19b75184b6bb";

    /// <summary>Three minutes per month is the same shape as a whole one, and it hashes the same way twice.</summary>
    static string Rows(DateOnly month)
    {
        var start = new DateTimeOffset(month.Year, month.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var b = new StringBuilder();
        for (var i = 0; i < 3; i++)
        {
            var open = start.AddMinutes(i).ToUnixTimeMilliseconds() * 1000L;
            var close = start.AddMinutes(i + 1).ToUnixTimeMilliseconds() * 1000L - 1;
            b.Append(open).Append(",100.00000000,101.00000000,99.00000000,100.50000000,1.00000000,")
             .Append(close).Append(",1000.00000000,10,0.50000000,500.00000000,0\n");
        }
        return b.ToString();
    }

    static async Task<(DataCollection Got, Database Db, FakeArchive Archive)> CollectBinance()
    {
        var archive = new FakeArchive();
        foreach (var m in BinanceArchive.RecentCompleteMonths(Now)) archive.Publish(Pair, m, Rows(m));

        var db = TestEnv.NewDb();
        var svc = new MarketDataService(db, new BinanceArchiveClient(archive.BaseUrl));
        return (await svc.CollectAsync(Pair, Now), db, archive);
    }

    /// <summary>
    /// THE BINANCE DATASET'S BYTES DID NOT MOVE. Header, bar count and SHA-256, against the number
    /// the build before this unit produced for the same twelve fixture months.
    /// </summary>
    [Fact]
    public async Task The_binance_dataset_file_is_byte_identical_to_the_build_before_this_unit()
    {
        var (got, db, archive) = await CollectBinance();
        using var _a = archive;
        using var _d = db;

        Assert.NotNull(got.Dataset);
        var lines = File.ReadAllText(got.Dataset!.NormalisedPath).Replace("\r\n", "\n").Split('\n');

        Assert.Equal("open_time,open,high,low,close,volume", lines[0]);
        Assert.Equal(36, got.Dataset.Bars);
        Assert.Equal(BinanceNormalisedSha256Before, got.Dataset.NormalisedSha256);
    }

    // ---- the second source, against the loopback harness and nothing else ----------------------

    /// <summary>The instrument the second source is asked for. Hyphenated, as venues outside Binance spell it.</summary>
    const string Symbol = "BTC-USD";

    /// <summary>
    /// THE SHIPPED REVOLUT X ROW, POINTED AT A LOOPBACK LISTENER.
    ///
    /// <para>The row itself is <c>CandleSourceCatalog</c>'s — five minutes, ninety days, no published
    /// checksum, candles that may carry no volume — and the ONLY thing a test changes is where it
    /// points. This build records no Revolut X public-candles endpoint, the shipped row's base URL is
    /// empty, and nothing in this suite has ever sent it a request.</para>
    /// </summary>
    static ICandleSource Second(FakeArchive archive) =>
        CandleSourceCatalog.Require(CandleSourceCatalog.RevolutXCandles, NoOverrideFile, archive.BaseUrl);

    /// <summary>A path in the scratch home that does not exist, so the built-ins stand. See VenueCatalog.Read.</summary>
    static string NoOverrideFile => Path.Combine(TestEnv.Home, "no-sources-override.json");

    /// <summary>
    /// Five-minute candles, ascending. <paramref name="withoutVolume"/> rows arrive with the volume
    /// column EMPTY, which is what <c>docs/COUNCIL.md</c> calls a midpoint-derived candle.
    /// </summary>
    static string Candles(DateTimeOffset start, int count, int withoutVolume = 0)
    {
        var b = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            var open = start.AddMinutes(5 * i).ToUnixTimeMilliseconds();
            var close = start.AddMinutes(5 * (i + 1)).ToUnixTimeMilliseconds() - 1;
            var volume = i < withoutVolume ? "" : "2.00000000";
            b.Append(open).Append(",100.00000000,101.00000000,99.00000000,100.50000000,")
             .Append(volume).Append(',').Append(close).Append(",1000.00000000,10\n");
        }
        return b.ToString();
    }

    /// <summary>The path the second source's URL resolves to on the loopback listener.</summary>
    static string SecondPath(FakeArchive archive, ICandleSource source) =>
        new Uri(source.Periods(Symbol, Now).Single().Url).AbsolutePath;

    static async Task<(DataCollection Got, Database Db)> CollectSecond(
        FakeArchive archive, int candles = 12, int withoutVolume = 0)
    {
        var source = Second(archive);
        archive.PublishAt(SecondPath(archive, source), Candles(Now.AddDays(-2), candles, withoutVolume));

        var db = TestEnv.NewDb();
        var svc = new MarketDataService(db);
        return (await svc.CollectAsync(source, Symbol, Now), db);
    }

    /// <summary>
    /// ITEM 1, RED FIRST: a source declaring FIVE MINUTES over NINETY DAYS produced a <c>1m</c> row
    /// over twelve months, because the interval was read off <c>BinanceArchive.Interval</c> and the
    /// coverage target did not exist.
    /// </summary>
    [Fact]
    public async Task A_source_declaring_five_minutes_and_ninety_days_does_not_produce_a_one_minute_twelve_month_row()
    {
        using var archive = new FakeArchive();
        var (got, db) = await CollectSecond(archive);
        using var _d = db;

        Assert.NotNull(got.Dataset);
        Assert.Equal("5m", got.Dataset!.Interval);
        Assert.Equal(90, got.Dataset.CoverageTargetDays);
        Assert.NotEqual(BinanceArchive.Interval, got.Dataset.Interval);
        Assert.NotEqual(BinanceCandleSource.TwelveMonthsInDays, got.Dataset.CoverageTargetDays);

        // AND THE ACTUAL DEPTH IS MEASURED, not assumed to be the target. Twelve five-minute candles
        // starting two days ago span one hour, which is one UTC day.
        Assert.Equal(1, got.Dataset.CoverageActualDays);
        Assert.Equal(CandleSourceCatalog.RevolutXCandles, got.Dataset.Source);
        Assert.Equal("BTC-USD", got.Dataset.InstrumentSymbol);

        // A FIVE-MINUTE DATASET HAS NO GAPS BETWEEN CONSECUTIVE BARS. Counted a minute at a time, as
        // this build counted before the interval reached the normaliser, these twelve bars would read
        // as 44 missing minutes.
        Assert.Equal(0, got.Dataset.Gaps);
    }

    /// <summary>
    /// ITEM 2, RED FIRST: a candle that arrived with NO volume normalised to <c>volume=0</c> and
    /// nothing anywhere said so — neither in the file nor on the row.
    /// </summary>
    [Fact]
    public async Task A_volume_less_candle_is_flagged_in_the_file_and_counted_on_the_row()
    {
        using var archive = new FakeArchive();
        var (got, db) = await CollectSecond(archive, candles: 10, withoutVolume: 4);
        using var _d = db;

        Assert.NotNull(got.Dataset);
        Assert.Equal(10, got.Dataset!.Bars);
        Assert.False(got.Dataset.SourceCarriesVolume);

        // FLAGGED IN THE FILE FIRST, under a header that says the column is there. This assertion and
        // the row's count below are deliberately in this order: writing the flag and counting it are
        // two different failures, and a test that could not tell them apart would report the same red
        // for both.
        var lines = File.ReadAllText(got.Dataset.NormalisedPath).Replace("\r\n", "\n")
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(KlineNormaliser.HeaderWithQuality, lines[0]);
        Assert.Equal(4, lines.Skip(1).Count(l => l.EndsWith("," + BarQuality.MidpointDerived, StringComparison.Ordinal)));
        Assert.Equal(6, lines.Skip(1).Count(l => l.EndsWith("," + BarQuality.Traded, StringComparison.Ordinal)));

        // AND COUNTED ON THE ROW. Without this the row says ten bars and claims a depth four of them
        // do not have.
        Assert.Equal(4, got.Dataset.MidpointBars);

        // A MIDPOINT BAR'S VOLUME IS ZERO AND THE FLAG IS WHAT SAYS THE ZERO IS NOT A MEASUREMENT.
        var bars = DatasetReader.Read(got.Dataset, BarAudience.Pipe(CouncilRoles.Research), null, null).Bars;
        Assert.Equal(4, bars.Count(b => b.Quality == BarQuality.MidpointDerived));
        Assert.All(bars.Where(b => b.Quality == BarQuality.MidpointDerived), b => Assert.Equal(0m, b.Volume));
        Assert.All(bars.Where(b => b.Quality == BarQuality.Traded), b => Assert.Equal(2m, b.Volume));
    }

    /// <summary>
    /// AN OLD SIX-COLUMN FILE STILL READS, and every bar in it is <c>traded</c> — which is what those
    /// bars ARE, because Binance publishes a traded volume on every kline.
    /// </summary>
    [Fact]
    public void A_file_written_before_the_quality_column_still_reads_and_its_bars_are_traded()
    {
        Assert.True(DatasetReader.TryBar("2026-08-01T00:00:00Z,100,101,99,100.5,1.25", out var old));
        Assert.Equal(BarQuality.Traded, old.Quality);
        Assert.Equal(1.25m, old.Volume);

        Assert.True(DatasetReader.TryBar(
            "2026-08-01T00:05:00Z,100,101,99,100.5,0," + BarQuality.MidpointDerived, out var flagged));
        Assert.Equal(BarQuality.MidpointDerived, flagged.Quality);

        // A WORD THIS BUILD DOES NOT KNOW READS AS `traded`, which claims nothing.
        Assert.True(DatasetReader.TryBar("2026-08-01T00:10:00Z,100,101,99,100.5,1,whatever", out var newer));
        Assert.Equal(BarQuality.Traded, newer.Quality);
    }

    /// <summary>
    /// ITEM 4, RED FIRST: the second source collected END TO END against the loopback harness, with
    /// the provenance a Binance month gets — the URL, the computed hash, the byte count and the
    /// moment it was read — and the venue the bars are of.
    ///
    /// <para>Red first: there was no second source, so a collection through one recorded no raw file
    /// at all and the dataset had no provenance to check.</para>
    /// </summary>
    [Fact]
    public async Task The_second_sources_dataset_records_its_raw_file_its_provenance_and_its_venue()
    {
        using var archive = new FakeArchive();
        var source = Second(archive);
        var url = source.Periods(Symbol, Now).Single().Url;
        var (got, db) = await CollectSecond(archive);
        using var _d = db;

        Assert.NotNull(got.Dataset);
        var file = Assert.Single(got.Dataset!.Files);

        // PROVENANCE IDENTICAL TO A BINANCE MONTH'S, minus the one thing this vendor does not publish.
        Assert.Equal(url, file.Url);
        Assert.Equal(64, file.ComputedSha256.Length);
        Assert.Equal(new FileInfo(file.Path).Length, file.Bytes);
        Assert.Equal(DatasetStore.Sha256(file.Path), file.ComputedSha256);
        Assert.True(file.DownloadedAt > Now.AddDays(-1) && file.DownloadedAt < DateTimeOffset.UtcNow.AddMinutes(1));

        // THE PUBLISHED HASH IS EMPTY BECAUSE THIS VENDOR PUBLISHES NONE — never this app's own
        // computed one, which would make an unchecked source indistinguishable from a checked one.
        Assert.Equal("", file.PublishedSha256);

        // AND THE DATASET NAMES ITS VENUE, which the catalogue knows of and has confirmed nothing about.
        Assert.Equal(VenueCatalog.RevolutX, got.Dataset.VenueId);
        var venue = Assert.Single(VenueCatalog.Read(NoOverrideFile).Venues, v => v.Id == VenueCatalog.RevolutX);
        Assert.False(venue.Verified);
        Assert.Empty(venue.Instruments);

        // AND IT WAS FETCHED FROM THE LOOPBACK LISTENER AND FROM NOWHERE ELSE, by its own marks: one
        // request, for the one period this source declares, and no sidecar was ever asked for.
        Assert.StartsWith("http://127.0.0.1:", url, StringComparison.Ordinal);
        var got_ = archive.Marks.Where(m => m.Contains("got ", StringComparison.Ordinal)).ToList();
        Assert.Single(got_);
        Assert.Contains("GET /candles", got_[0]);
        Assert.DoesNotContain(archive.Marks, m => m.Contains(".CHECKSUM", StringComparison.Ordinal));
    }

    /// <summary>
    /// A SOURCE WITH NO PUBLISHED CHECKSUM IS NEVER RECORDED AS VERIFIED — and the decision is the
    /// owner's to read, not a silence.
    /// </summary>
    [Fact]
    public async Task A_source_that_publishes_no_checksum_records_no_published_hash_and_says_why()
    {
        var decisions = new List<string>();
        var previous = Downloader.RecordDecision;
        Downloader.RecordDecision = decisions.Add;

        try
        {
            using var archive = new FakeArchive();
            var (got, db) = await CollectSecond(archive);
            using var _d = db;

            Assert.Equal("", Assert.Single(got.Dataset!.Files).PublishedSha256);
            Assert.False(CandleSourceCatalog.Require(
                CandleSourceCatalog.RevolutXCandles, NoOverrideFile, archive.BaseUrl).PublishesChecksum);

            // THE UNVERIFIED DOWNLOAD WROTE ITS REASON WHERE THE OWNER READS IT, before the bytes
            // were used. `Integrity.Unverified` has no way to be asked for without one.
            Assert.Contains(decisions, d => d.Contains("publishes no checksum", StringComparison.Ordinal));
        }
        finally { Downloader.RecordDecision = previous; }
    }

    // ---- item 5: validated data arrival is a wake ----------------------------------------------

    /// <summary>
    /// ITEM 5, RED FIRST: collecting twelve months woke nobody. <c>docs/COUNCIL.md</c>:89-91 lists
    /// "validated data arrival" among the persisted wakes, and nothing raised one — the owner could
    /// collect a year of history and the research process would find out at its next scheduled look.
    ///
    /// <para>And the dedup is by the DATASET, not by the press: a rebuild reproduces the same bytes
    /// from the same raw files, so it raises an id the queue already holds and costs nobody a turn.</para>
    /// </summary>
    [Fact]
    public async Task An_accepted_dataset_wakes_research_once_and_a_rebuild_wakes_nobody()
    {
        var archive = new FakeArchive();
        using var _a = archive;
        foreach (var m in BinanceArchive.RecentCompleteMonths(Now)) archive.Publish(Wakes, m, Rows(m));

        using var db = TestEnv.NewDb();
        var nudges = 0;
        var svc = new MarketDataService(db, new BinanceArchiveClient(archive.BaseUrl), () => nudges++);
        var events = new MissionEventStore(db);

        var got = await svc.CollectAsync(Wakes, Now);
        Assert.NotNull(got.Dataset);
        Assert.True(got.Woke);
        Assert.Equal(1, nudges);

        // ONE EVENT, RESEARCH'S, KEYED BY THE DATASET'S OWN SHA-256.
        var raised = events.DueFor(CouncilRoles.Research, DateTimeOffset.UtcNow)
                           .Where(e => e.Kind == MissionEventKind.Data).ToList();
        var wake = Assert.Single(raised);
        Assert.Equal(
            MissionEventIds.ForRole(MissionEventIds.Data(got.Dataset!.NormalisedSha256), CouncilRoles.Research),
            wake.Id);
        Assert.Equal(CouncilRoles.Research, wake.Role);
        Assert.Contains($"\"dataset\":{got.Dataset.Id}", wake.Payload);
        Assert.DoesNotContain(events.DueFor(CouncilRoles.Operations, DateTimeOffset.UtcNow),
            e => e.Kind == MissionEventKind.Data);

        // A REBUILD IS A RE-VERIFICATION, NOT NEWS: same raw files, same bytes, same id, no turn.
        var again = svc.Rebuild(Wakes);
        Assert.Equal(got.Dataset.NormalisedSha256, again.Dataset!.NormalisedSha256);
        Assert.False(again.Woke);
        Assert.Equal(1, nudges);
        Assert.Single(events.DueFor(CouncilRoles.Research, DateTimeOffset.UtcNow),
            e => e.Kind == MissionEventKind.Data);

        // AND THE SITUATION NAMES IT, which is where a turn reads what it has to work with.
        var line = MissionSituation.DataLine(svc.Store.Newest(Wakes), DateTimeOffset.UtcNow);
        Assert.Contains(Wakes, line);
        Assert.Contains(BinanceArchive.Interval, line);
        Assert.Contains($"{got.Dataset.Bars:N0} bars", line);
    }

    /// <summary>A pair of its own: this class's other collections must not raise this one's wake.</summary>
    const string Wakes = "WAKEUSDT";

    /// <summary>
    /// A DATASET WITH DIFFERENT BYTES IS DIFFERENT NEWS. The dedup is by the evidence and not by the
    /// pair, the press or the clock.
    /// </summary>
    [Fact]
    public async Task A_second_dataset_with_different_bytes_raises_a_second_wake()
    {
        using var archive = new FakeArchive();
        var (first, db) = await CollectSecond(archive, candles: 6);
        using var _d = db;

        var events = new MissionEventStore(db);
        Assert.Single(events.DueFor(CouncilRoles.Research, DateTimeOffset.UtcNow),
            e => e.Kind == MissionEventKind.Data);

        // The same source and symbol, different bars: a new file, a new hash, a new reason to look.
        var source = Second(archive);
        archive.PublishAt(SecondPath(archive, source), Candles(Now.AddDays(-3), 9));
        var second = await new MarketDataService(db).CollectAsync(source, Symbol, Now);

        Assert.NotEqual(first.Dataset!.NormalisedSha256, second.Dataset!.NormalisedSha256);
        Assert.True(second.Woke);
        Assert.Equal(2, events.DueFor(CouncilRoles.Research, DateTimeOffset.UtcNow)
                              .Count(e => e.Kind == MissionEventKind.Data));
    }
}

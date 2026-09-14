using System.Globalization;
using Microsoft.Data.Sqlite;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEM 1 — THE INSTRUMENT AS A RECORDED FACT WITH A SOURCE, NOT A NUMBER AN AGENT TYPED.
///
/// <para>Red first: the word "venue" existed in this build only in comments. There was no table, no
/// type and no calendar kind, so every backtest's quantity increment was whatever number arrived on
/// the request — <c>Frictionless</c>'s 1 when none did, on a pair whose real step is 0.00001
/// (<c>docs/COUNCIL.md</c>:145,152: bars carry instrument increments and a size is "rounded down to
/// the increment").</para>
/// </summary>
public class VenueCatalogTests
{
    [Fact]
    public void The_schema_carries_the_venue_tables_at_version_seventeen()
    {
        using var db = TestEnv.NewDb();

        // THE TABLE FIRST, because "no such table: venue_instrument" is what this unit started from
        // and is the sentence that has to stop being true. It is EMPTY on a bare database: the rows
        // are the catalogue's, written by `VenueStore.Sync`, and a migration that seeded them would
        // be a shipped fact nobody could correct with a one-line data fix.
        Assert.Equal(0L, db.Read(_ =>
        {
            using var c = db.Cmd("SELECT COUNT(*) FROM venue_instrument");
            return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
        }));

        // A FLOOR, not an equality, so an additive migration above this rung does not have to edit a
        // test about this one — the trap `AiAttemptLedgerTests` and `DatasetLedgerTests` were both
        // cured of. The STAMPED number is this build's, whatever that has become.
        Assert.True(Versions.DatabaseSchemaVersion >= 17,
            "the venue catalogue needs schema 17 or later; this build says "
            + Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture));

        Assert.Equal(
            Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture),
            db.Read(_ =>
            {
                using var c = db.Cmd("SELECT value FROM meta WHERE key='schema_version'");
                return c.ExecuteScalar() as string;
            }));
    }

    /// <summary>A store over a fresh database, synced from a catalogue this test controls.</summary>
    static (Database Db, VenueStore Store) Given(string? overridePath = null)
    {
        var db = TestEnv.NewDb();
        var store = new VenueStore(db);
        store.Sync(VenueCatalog.Read(overridePath ?? Nothing()));
        return (db, store);
    }

    /// <summary>
    /// A path where no file is, which is the ABSENT case and means "the built-ins".
    ///
    /// The whole assembly shares one <c>TRADEAGENT_HOME</c>, so a test that wrote its own
    /// <c>venues.json</c> into it would change what every gateway another class constructs reads.
    /// Every test here names its own file instead.
    /// </summary>
    static string Nothing() => Path.Combine(TestEnv.Home, $"venues-absent-{Guid.NewGuid():n}.json");

    static string Write(string json)
    {
        var path = Path.Combine(TestEnv.Home, $"venues-{Guid.NewGuid():n}.json");
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>
    /// THE SHIPPED ROWS ARE SERVED, AND THE ONES NOBODY HAS CHECKED SAY SO.
    ///
    /// <para>This is the mutant's test. The Binance row is <c>verified = false</c> because nothing in
    /// this build has read Binance's own instrument definition — this unit reaches no network — and the
    /// mutant that made a read serve every row as verified turns that admission into a claim. The
    /// numbers would be exactly as right or wrong as before; what would be gone is the one field that
    /// says whether anybody looked.</para>
    /// </summary>
    [Fact]
    public void The_shipped_catalogue_serves_an_unchecked_row_as_unchecked()
    {
        var (db, store) = Given();
        using var _1 = db;

        var pair = store.Instrument(VenueCatalog.BinanceSpot, "BTCUSDT");
        Assert.NotNull(pair);
        Assert.Equal(0.00001m, pair.QuantityIncrement);
        Assert.Equal(0.01m, pair.TickSize);
        Assert.False(pair.Verified, "nothing in this build has confirmed Binance's own instrument definition");
        Assert.Contains("NOT confirmed", pair.Source, StringComparison.Ordinal);
        Assert.Equal(VenueCatalog.ShippedAt, pair.RecordedAt);

        // The simulator's rows ARE verified, and that is not a double standard: the venue is this
        // application's own and there is no third party who could disagree with it.
        var es = store.Instrument(VenueCatalog.Simulator, "ES");
        Assert.NotNull(es);
        Assert.True(es.Verified);
        Assert.Equal(1m, es.QuantityIncrement);

        var venue = Assert.Single(store.Venues(), v => v.Id == VenueCatalog.BinanceSpot);
        Assert.Equal(CalendarKind.Continuous, venue.CalendarKind);
        Assert.False(venue.Verified);
    }

    /// <summary>
    /// A symbol nobody recorded is NULL, not a default — the answer <c>FakeBroker.TickSize</c> already
    /// gives for the same reason, and what item 5's refusal is built on.
    /// </summary>
    [Fact]
    public void A_symbol_the_catalogue_does_not_hold_is_null_and_not_a_substituted_grid()
    {
        var (db, store) = Given();
        using var _1 = db;

        Assert.Null(store.Instrument(VenueCatalog.BinanceSpot, "ETHUSDT"));
        Assert.Null(store.Instrument(VenueCatalog.Simulator, "XYZ"));
        Assert.Null(store.Instrument("no-such-venue", "BTCUSDT"));
    }

    /// <summary>
    /// THE SIMULATOR'S CATALOGUE ROWS ARE THE SIMULATOR'S OWN INSTRUMENT LIST.
    ///
    /// <para>They are written out in <c>TradeAgent.Core</c> because Core cannot reference a connector,
    /// and a copy that could drift from the thing it copies is worth less than no copy at all: a
    /// catalogue that said ES ticks in 0.25 while the simulator priced it in 0.10 would be a recorded
    /// fact about a venue that does not exist. This is what keeps the two equal.</para>
    /// </summary>
    [Fact]
    public void The_simulators_rows_are_the_simulators_own_instrument_list()
    {
        var (db, store) = Given();
        using var _1 = db;

        var catalogue = store.Instruments(VenueCatalog.Simulator);
        Assert.Equal(
            FakeBroker.Instruments.Select(i => i.Symbol).OrderBy(s => s, StringComparer.Ordinal),
            catalogue.Select(i => i.Symbol).OrderBy(s => s, StringComparer.Ordinal));

        foreach (var i in FakeBroker.Instruments)
            Assert.Equal(i.TickSize, store.Instrument(VenueCatalog.Simulator, i.Symbol)!.TickSize);
    }

    /// <summary>
    /// <c>venues.json</c> REPLACES the venue it names and leaves the others alone — the one-line data
    /// fix <c>docs/DECISIONS.md</c>:73-78 asks for, and <c>RuntimeManifest.Read</c>'s own rule.
    /// </summary>
    [Fact]
    public void A_venues_file_replaces_the_venue_it_names_and_adds_the_ones_it_invents()
    {
        var (db, store) = Given(Write("""
            [
              { "id": "binance-spot", "display_name": "Binance spot", "calendar_kind": "continuous",
                "source": "the owner read Binance's exchangeInfo on 2026-09-12",
                "recorded_at": "2026-09-12T00:00:00Z", "verified": true,
                "instruments": [
                  { "symbol": "ETHUSDT", "tick_size": 0.01, "quantity_increment": 0.0001,
                    "source": "the owner read Binance's exchangeInfo on 2026-09-12",
                    "recorded_at": "2026-09-12T00:00:00Z", "verified": true }
                ] },
              { "id": "kraken-spot", "display_name": "Kraken spot", "calendar_kind": "continuous",
                "source": "the owner", "verified": false, "instruments": [] }
            ]
            """));
        using var _1 = db;

        // Replaced whole, not merged: BTCUSDT was in the built-in binance-spot and the file's version
        // of that venue does not list it. A merge would leave a shipped number standing beside a
        // correction the owner wrote precisely to replace it.
        Assert.Null(store.Instrument(VenueCatalog.BinanceSpot, "BTCUSDT"));
        Assert.Equal(0.0001m, store.Instrument(VenueCatalog.BinanceSpot, "ETHUSDT")!.QuantityIncrement);
        Assert.True(store.Instrument(VenueCatalog.BinanceSpot, "ETHUSDT")!.Verified);

        // The venue the file invented is there, and the one it never mentioned is untouched.
        Assert.Contains(store.Venues(), v => v.Id == "kraken-spot");
        Assert.Equal(4, store.Instruments(VenueCatalog.Simulator).Count);
        Assert.Null(store.Unreadable);
    }

    /// <summary>
    /// ITEM 3 — THE BACKFILL. Every <c>dataset</c> row this build has ever written was written by the
    /// Binance collector, so the rung says so rather than leaving the two columns null on history that
    /// has a perfectly knowable answer.
    ///
    /// <para>Verified by taking a real database BACK to 16 — dropping the two columns and restamping
    /// <c>meta</c> — and reopening it, which is the only way the rung runs over a row that predates it.
    /// A test that merely asserted the columns exist would be a test of the CREATE and not of the
    /// UPDATE.</para>
    /// </summary>
    [Fact]
    public void The_rung_backfills_the_venue_of_every_dataset_that_predates_it()
    {
        var file = Path.Combine(TestEnv.Home, $"backfill-{Guid.NewGuid():n}.db");
        long id;

        using (var db = new Database(file))
            id = new DatasetStore(db).Record(Collected());

        // Back to 16: the columns every rung above it added are gone and the stamp is lowered, which is
        // what a database collected on before this unit landed actually looks like. EVERY rung above
        // 16 has to be undone, not only 17's — a reopen runs all of them, and a half-rolled-back
        // database is one no installation has ever had.
        using (var raw = new SqliteConnection($"Data Source={file}"))
        {
            raw.Open();
            using var c = raw.CreateCommand();
            c.CommandText = """
                ALTER TABLE dataset DROP COLUMN venue_id;
                ALTER TABLE dataset DROP COLUMN instrument_symbol;
                ALTER TABLE dataset DROP COLUMN coverage_target_days;
                ALTER TABLE dataset DROP COLUMN source_carries_volume;
                ALTER TABLE dataset DROP COLUMN midpoint_bars;
                ALTER TABLE strategy_run DROP COLUMN increment_source;
                ALTER TABLE strategy_version DROP COLUMN timeframe;
                ALTER TABLE strategy_version DROP COLUMN data_freshness;
                ALTER TABLE strategy_version DROP COLUMN max_decision_age;
                ALTER TABLE strategy_promotion DROP COLUMN timeframe;
                ALTER TABLE strategy_promotion DROP COLUMN data_freshness;
                ALTER TABLE strategy_promotion DROP COLUMN max_decision_age;
                DROP TABLE venue_instrument;
                DROP TABLE venue;
                UPDATE meta SET value='16' WHERE key='schema_version';
                """;
            c.ExecuteNonQuery();
        }

        using var reopened = new Database(file);
        var set = new DatasetStore(reopened).ById(id);

        Assert.NotNull(set);
        Assert.Equal(VenueCatalog.BinanceSpot, set.VenueId);
        Assert.Equal("BTCUSDT", set.InstrumentSymbol);

        // AND 18's BACKFILL, on the same row and for the same reason: every dataset that predates it
        // was collected by the Binance collector at twelve months, from an archive that publishes a
        // traded volume on every kline.
        Assert.Equal(BinanceCandleSource.TwelveMonthsInDays, set.CoverageTargetDays);
        Assert.True(set.SourceCarriesVolume);
        Assert.Equal(0, set.MidpointBars);
    }

    /// <summary>
    /// ITEM 3 — SECTION 8 OF THE OWNER'S DAILY REPORT SAYS WHAT THE BARS ARE OF.
    ///
    /// <para>A bar count says what was measured and not what it was measured ON. The owner reading this
    /// document is the person who would have to correct a wrong instrument, and a dataset that recorded
    /// no venue says so in words — otherwise the runner's refusal to take an increment for it arrives
    /// with nothing in the report to explain it.</para>
    /// </summary>
    [Fact]
    public async Task Section_eight_says_which_venue_and_instrument_each_dataset_is_of()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        var store = new DatasetStore(db);

        store.Record(Collected() with { VenueId = VenueCatalog.BinanceSpot, InstrumentSymbol = "BTCUSDT" });
        store.Record(Collected());   // the collector recorded neither

        var report = gw.Reports.Compose(DateTimeOffset.Now);

        Assert.Contains(report.Research.AppMetrics,
            m => m.Contains($"on {VenueCatalog.BinanceSpot}/BTCUSDT", StringComparison.Ordinal));
        Assert.Contains(report.Research.AppMetrics,
            m => m.Contains("on no venue recorded/no instrument recorded", StringComparison.Ordinal));
    }

    /// <summary>A dataset row with the provenance a collector writes, and nothing on disk to hash.</summary>
    static DatasetRecord Collected() => new(
        0, "binance-spot-monthly-klines", "BTCUSDT", "1m", "v1", 12, 12, [],
        Path.Combine(TestEnv.Home, $"{Guid.NewGuid():n}.csv"), new string('a', 64), 10,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(9), 0, [], false, 0, 0, 0,
        DateTimeOffset.UnixEpoch, DatasetState.ACCEPTED, null, []);

    /// <summary>
    /// AN UNREADABLE OVERRIDE IS THE MOST RESTRICTIVE OVERRIDE, and the shipped rows do NOT stand in
    /// for it.
    ///
    /// <para><c>RuntimeManifest.Read</c>'s judgement, applied to a number a size is computed from: an
    /// owner who edits this file to correct a step size and mistypes a comma must not silently get the
    /// number they were correcting. With no rows, every increment a request did not declare is refused
    /// in words.</para>
    /// </summary>
    [Fact]
    public void An_unreadable_venues_file_serves_no_catalogue_at_all_and_says_why()
    {
        var (db, store) = Given(Write("{ this is not json"));
        using var _1 = db;

        Assert.Empty(store.Venues());
        Assert.Empty(store.Instruments());
        Assert.NotNull(store.Unreadable);
        Assert.Contains("not valid JSON", store.Unreadable, StringComparison.Ordinal);
        Assert.Contains("NOT standing in for it", store.Unreadable, StringComparison.Ordinal);
    }
}

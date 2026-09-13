using System.Globalization;
using System.Text;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEMS 4 AND 5 — WHERE A RUN'S QUANTITY INCREMENT COMES FROM, AND WHAT HAPPENS WHEN NOBODY KNOWS.
///
/// <para>Red first: a request that named no increment ran at 1 — a WHOLE Bitcoin — over a pair whose
/// recorded step is 0.00001, because <c>ExecutionModel.Frictionless</c> was the only answer there was
/// and the dataset carried nothing to look one up by. <c>docs/COUNCIL.md</c>:145,152 asks for bars
/// "with instrument increments" and a size "rounded down to the increment", which is a fact about the
/// INSTRUMENT and not a number a caller types.</para>
///
/// <para>The declared model is still the only thing hashed into a run id. The catalogue fills in an
/// increment the caller did not give; it does not add a field to the identity, so nothing this
/// installation has already recorded moves — <c>BacktestDeterminismTests</c> is what holds that.</para>
/// </summary>
public class VenueIncrementTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset At = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    const string Text = """
        instrument BTCUSDT
        size fixed 1
        exit when close < 97
        entry when close > 103
        """;

    static AgentContext Caller(string role = CouncilRoles.Operations, string attempt = "attempt-1") =>
        AgentContext.ForAgent(role, role, attempt);

    static string GivenProgram(string role = CouncilRoles.Operations, string text = Text, string? name = null)
    {
        var dir = Path.Combine(Paths.RoleHome(role), "strategies");
        Directory.CreateDirectory(dir);
        var file = name ?? $"venue-{Guid.NewGuid():n}.strategy";
        File.WriteAllText(Path.Combine(dir, file), text);
        return Path.Combine("strategies", file);
    }

    /// <summary>A dataset the ledger really recorded, naming the venue and instrument its bars are of.</summary>
    static DatasetRecord GivenData(
        Database db, string? venue = VenueCatalog.BinanceSpot, string? symbol = "BTCUSDT",
        int bars = 120, string pair = "BTCUSDT")
    {
        var dir = BinanceArchive.DatasetDir(pair);
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        var raw = Path.Combine(dir, "raw", $"{pair}-{Guid.NewGuid():n}.zip");
        File.WriteAllText(raw, "a stand-in for the vendor's monthly archive");

        var csv = Path.Combine(dir, $"{Guid.NewGuid():n}.csv");
        var text = new StringBuilder().Append(KlineNormaliser.Header).Append('\n');
        for (var i = 0; i < bars; i++)
        {
            var close = 96m + i % 10;
            text.Append(CultureInfo.InvariantCulture,
                $"{At.AddMinutes(i).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{close},{close + 1m},{close - 1m},{close},1.00\n");
        }
        File.WriteAllText(csv, text.ToString());

        var record = new DatasetRecord(
            0, BinanceArchive.Source, pair, BinanceArchive.Interval, "v1", 12, 1, ["2025-09"],
            csv, DatasetStore.Sha256(csv)!, bars, At, At.AddMinutes(bars - 1), 0, [], false,
            0, 0, 0, At, DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-08", "https://127.0.0.1/x.zip", DatasetStore.Sha256(raw)!,
                DatasetStore.Sha256(raw)!, new FileInfo(raw).Length, At, KlineTimeUnit.Microseconds, raw)])
        {
            VenueId = venue,
            InstrumentSymbol = symbol
        };

        return record with { Id = new DatasetStore(db).Record(record) };
    }

    /// <summary>
    /// A CATALOGUE THE OWNER HAS CONFIRMED, written as the <c>venues.json</c> they would write.
    ///
    /// <para>The shipped BTCUSDT row is <c>verified = false</c> — nothing in this build has read
    /// Binance's own instrument definition — and an unverified row is not a number a size may be
    /// computed from. So the test that wants the catalogue's increment USED has to supply the
    /// confirmation, which is exactly what the product asks of the account owner.</para>
    ///
    /// <para>Written to a file of this test's own naming and synced into this test's own database. The
    /// assembly shares one <c>TRADEAGENT_HOME</c>, and a <c>venues.json</c> written into it would
    /// change what every gateway another class constructs reads.</para>
    /// </summary>
    static void GivenConfirmed(Database db, string symbol = "BTCUSDT", decimal increment = 0.00001m)
    {
        var path = Path.Combine(TestEnv.Home, $"venues-{Guid.NewGuid():n}.json");
        File.WriteAllText(path, $$"""
            [
              { "id": "binance-spot", "display_name": "Binance spot", "calendar_kind": "continuous",
                "source": "the account owner read Binance's own exchangeInfo",
                "recorded_at": "2026-09-12T00:00:00Z", "verified": true,
                "instruments": [
                  { "symbol": "{{symbol}}", "tick_size": 0.01,
                    "quantity_increment": {{increment.ToString(CultureInfo.InvariantCulture)}},
                    "source": "the account owner read Binance's own exchangeInfo",
                    "recorded_at": "2026-09-12T00:00:00Z", "verified": true }
                ] }
            ]
            """);
        new VenueStore(db).Sync(VenueCatalog.Read(path));
    }

    /// <summary>
    /// ITEM 4 — A RUN THAT DECLARED NO INCREMENT TAKES THE CATALOGUE'S, AND RECORDS WHERE IT CAME FROM.
    ///
    /// <para>Red first: this ran at 1 on a pair the catalogue records at 0.00001. A size of a whole
    /// Bitcoin is not a rounding detail — it is four orders of magnitude, and every figure the run
    /// produced was about a position nobody could have taken.</para>
    /// </summary>
    [Fact]
    public async Task A_run_that_declared_no_increment_takes_the_catalogues()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        GivenConfirmed(db);
        var set = GivenData(db);

        var ran = gw.Backtests.Run(Caller(), new BacktestAsk(GivenProgram(), set.Id));

        log.WriteLine(ran.Result.Request.Model.Canonical);
        Assert.Equal(0.00001m, ran.Result.Request.Model.QuantityIncrement);
        Assert.Contains("increment=0.00001", ran.Result.Request.Model.Canonical, StringComparison.Ordinal);

        // AND IT IS WRITTEN DOWN. The four declared numbers say WHAT the run used; this says who said
        // so, which is the difference between a recorded fact and a number somebody typed.
        var run = gw.Strategies.RunById(ran.Result.RunId);
        Assert.NotNull(run);
        Assert.NotNull(run.IncrementSource);
        Assert.Contains("binance-spot", run.IncrementSource, StringComparison.Ordinal);
        Assert.Contains("BTCUSDT", run.IncrementSource, StringComparison.Ordinal);
        Assert.Contains("exchangeInfo", run.IncrementSource, StringComparison.Ordinal);
    }

    /// <summary>
    /// A DECLARED INCREMENT STILL WINS, and the run says the number was the caller's own.
    ///
    /// <para>The catalogue is a default for a caller that gave none, never an override of one that did:
    /// a run declared at a coarser step is a legitimate question about a coarser step, and an app that
    /// silently substituted its own number would be answering a different one.</para>
    /// </summary>
    [Fact]
    public async Task A_declared_increment_wins_over_the_catalogue()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        GivenConfirmed(db);
        var set = GivenData(db);

        var ran = gw.Backtests.Run(Caller(), new BacktestAsk(GivenProgram(), set.Id, Increment: 0.5m));

        Assert.Equal(0.5m, ran.Result.Request.Model.QuantityIncrement);

        var run = gw.Strategies.RunById(ran.Result.RunId);
        Assert.NotNull(run);
        Assert.Contains("declared", run.IncrementSource!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("binance-spot", run.IncrementSource!, StringComparison.Ordinal);
    }

    /// <summary>
    /// ONLY THE DECLARED MODEL IS HASHED: the run id of a catalogue-sourced run is the run id of the
    /// same four numbers declared by hand.
    ///
    /// <para>This is the mutant's test, and its other half is <c>BacktestDeterminismTests</c>, whose
    /// golden hashes were computed outside this build. Folding the increment's PROVENANCE into
    /// <c>ExecutionModel.Canonical</c> would move every run id this installation has ever written —
    /// silently, because a hash that changed has no way of saying it used to be something else.</para>
    /// </summary>
    [Fact]
    public async Task The_provenance_of_an_increment_is_not_part_of_the_runs_identity()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        GivenConfirmed(db);
        var set = GivenData(db);
        var program = GivenProgram();

        var fromCatalogue = gw.Backtests.Run(Caller(), new BacktestAsk(program, set.Id));
        var byHand = gw.Backtests.Run(Caller(), new BacktestAsk(program, set.Id, Increment: 0.00001m));

        Assert.Equal(fromCatalogue.Result.RunId, byHand.Result.RunId);
        Assert.Equal(fromCatalogue.Result.Request.Model.Canonical, byHand.Result.Request.Model.Canonical);

        // The first write stands, so the recorded provenance is the first run's. That is the ledger's
        // own rule (ON CONFLICT DO NOTHING) and not a special case here.
        Assert.Contains("binance-spot", gw.Strategies.RunById(fromCatalogue.Result.RunId)!.IncrementSource!,
            StringComparison.Ordinal);
    }
}

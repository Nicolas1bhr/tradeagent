using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Proving;
using TradeAgent.Core.Strategy;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A SYNTHETIC MARKET IN A PROVING HOME (<c>U-proving-ground-a</c> item 4; <c>docs/ORCHESTRATION.md</c> § 2.14): seeded,
/// written through the real collection path and the tape's one writer, read by the UNCHANGED backtest — and the real home
/// beside it untouched. Every number in these markets is a hand-set constant or a seeded draw: synthetic by construction.
/// No network, no model.
/// </summary>
public class SyntheticMarketTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Start = new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The planted edge every market here carries: 16 ticks per unit of the signal, over the hour.</summary>
    static readonly SyntheticEdge Edge = new(16);

    static SyntheticMarketSpec Noise(int days) => new(11, "SYNA", VenueCatalog.BinanceSpot, Start, days);

    static SyntheticMarketSpec Planted(int days) => new(12, "SYNB", VenueCatalog.BinanceSpot, Start, days, Edge);

    /// <summary>Long while the instrument's own signal is high, flat otherwise — the same program for every instrument.</summary>
    static string Program(string symbol) => $$"""
        instrument {{symbol}}
        bars 1h
        feature signal = {"kind":"latest","input":{"source":"proving-synthetic","series":"signal","subject":"{{symbol}}","field":"value"},"latency_s":5,"max_age_s":7200}
        size fixed 1
        stop percent 50
        exit when signal <= 0.0030
        entry when signal > 0.0030
        """;

    /// <summary>
    /// THE UNCHANGED BACKTEST OVER ONE SYNTHETIC DATASET: the program parsed in a proving home, its bars from the proving
    /// ledger, its feature from the proving tape, judged at the app's own venue cost for the dataset's venue.
    /// </summary>
    BacktestResult Backtest(ProvingHome home, Database db, DatasetRecord set)
    {
        var cost = VenueCostModel.For(set, new VenueStore(db), 10_000m);
        Assert.True(cost.Ok, cost.Why);
        Assert.False(cost.Model!.IsFrictionless);

        var program = StrategyParser.Parse(Program(set.Pair), HomeKind.Proving);
        Assert.True(program.Ok, program.Why);

        var run = Core.Strategy.Backtest.Over(new DatasetStore(db), set.Id, program.Program!, cost.Model.Model,
            BarAudience.Pipe(CouncilRoles.Research), tape: new TapeReader(home.TapeFile, kind: HomeKind.Proving));
        Assert.True(run.Ok, run.Why);
        var m = run.Result!.Metrics;
        log.WriteLine($"{set.Pair}: {run.Result.Outcome}, {m.Trades} trades, gross {m.GrossPnl}, fees {m.Fees}, net {m.NetPnl}");
        Assert.Equal(BacktestOutcome.COMPLETED, run.Result.Outcome);
        return run.Result;
    }

    /// <summary>
    /// (d) A SEEDED MARKET IS BYTE-IDENTICAL ON EVERY RUN AND PLATFORM. The PRNG's first outputs are SplitMix64's published
    /// ones; one spec written into two fresh proving homes gives the same raw files, the same normalised file and the same
    /// tape payloads — and the hashes are pinned here, so CI's three platforms are held to one market.
    /// </summary>
    [Fact]
    public void A_seeded_market_is_byte_identical_on_every_run_and_platform()
    {
        var rng = new SplitMix64(0);
        Assert.Equal([0xE220A8397B1DCDAFUL, 0x6E789E6AA1B965F4UL, 0x06C45D188009454FUL], new[] { rng.Next(), rng.Next(), rng.Next() });

        using var scratch = TestEnv.NewScratch("seeded-market");
        var written = new List<(SyntheticMarketWritten Market, string Truth)>();
        foreach (var run in new[] { "one", "two" })
        {
            var home = ProvingHome.Create(Path.Combine(scratch.Dir, run));
            using var db = new Database(home.DatabaseFile, HomeKind.Proving);
            using var tape = new TapeStore(home.TapeFile, kind: HomeKind.Proving);
            var market = SyntheticMarket.Write(home, db, tape, Planted(2));
            log.WriteLine($"{run}: raw {market.RawSha256} normalised {market.Dataset.NormalisedSha256} payloads {market.PayloadDigest}");
            written.Add((market, File.ReadAllText(market.TruthFile)));
        }

        var (a, b) = (written[0], written[1]);
        Assert.Equal(a.Market.RawSha256, b.Market.RawSha256);
        Assert.Equal(a.Market.Dataset.NormalisedSha256, b.Market.Dataset.NormalisedSha256);
        Assert.Equal(a.Market.PayloadDigest, b.Market.PayloadDigest);
        Assert.Equal(a.Truth, b.Truth);
        Assert.Equal(48, a.Market.Readings);
        Assert.Equal(2 * 1440, a.Market.Dataset.Bars);
        Assert.Equal(0, a.Market.Dataset.Gaps + a.Market.Dataset.Duplicates + a.Market.Dataset.Incomplete + a.Market.Dataset.Unreadable);
        Assert.Contains("\"synthetic_by_construction\"", a.Truth, StringComparison.Ordinal);

        Assert.Equal(GoldenRaw, a.Market.RawSha256);
        Assert.Equal(GoldenNormalised, a.Market.Dataset.NormalisedSha256);
        Assert.Equal(GoldenPayloads, a.Market.PayloadDigest);
    }

    // THE MARKET'S HASHES, PINNED: seed 12, SYNB, two days from 2026-08-03, β 16 — first computed on the dev Mac and held
    // to on CI's three platforms. A change here is another market, and says so.
    const string GoldenRaw = "d2122a79958016b1d1bdf2483d4d1ae6849b32cc7b0c40e0b7ff310fba000cf4";
    const string GoldenNormalised = "b9e22f0d96e86b2cd73379de073f2ee16f2595279694d89a15712f05b288da01";
    const string GoldenPayloads = "0bd7136936e75c6a89456463febee44af42f27d744ff9d2a34ab5de7503972db";

    /// <summary>
    /// (e) THE UNCHANGED BACKTEST FINDS THE PLANTED EDGE AND NOT NOISE IN A PROVING HOME. One program — long while the
    /// instrument's own signal is above 0.0030 — run by <see cref="Core.Strategy.Backtest.Over"/> at the app's venue cost:
    /// net above zero on the planted instrument, and not above zero on the noise one. A sign, never an exact figure.
    /// </summary>
    [Fact]
    public void The_unchanged_backtest_finds_the_planted_edge_and_not_noise_in_a_proving_home()
    {
        using var scratch = TestEnv.NewScratch("planted-edge");
        var home = ProvingHome.Create(Path.Combine(scratch.Dir, "proving"));
        using var db = new Database(home.DatabaseFile, HomeKind.Proving);
        using (var tape = new TapeStore(home.TapeFile, kind: HomeKind.Proving))
        {
            SyntheticMarket.Write(home, db, tape, Noise(21));
            SyntheticMarket.Write(home, db, tape, Planted(21));
        }
        new VenueStore(db).Sync(SyntheticMarket.Venues(VenueCatalog.BinanceSpot, ["SYNA", "SYNB"]));

        var sets = new DatasetStore(db).All();
        var noise = Backtest(home, db, sets.Single(s => s.Pair == "SYNA"));
        var planted = Backtest(home, db, sets.Single(s => s.Pair == "SYNB"));

        Assert.True(noise.Metrics.Trades >= 20, $"noise traded {noise.Metrics.Trades} times");
        Assert.True(planted.Metrics.Trades >= 20, $"the planted instrument traded {planted.Metrics.Trades} times");
        Assert.True(planted.Metrics.NetPnl > 0m, $"the planted edge nets {planted.Metrics.NetPnl}");
        Assert.True(noise.Metrics.NetPnl <= 0m, $"noise nets {noise.Metrics.NetPnl}");
    }

    /// <summary>
    /// (f) THE REAL HOME IS BYTE-IDENTICAL AFTER A PROVING MARKET AND RUN. A stand-in real home — its database and tape,
    /// marked real, holding a row each — has a proving home made inside it where the proving ground keeps one
    /// (<c>proving/&lt;run&gt;/</c>), a market written and a backtest run there, and each store opened across the kinds and
    /// refused: every file of the real home outside <c>proving/</c> is then the same file, byte for byte.
    /// </summary>
    [Fact]
    public void The_real_home_is_byte_identical_after_a_proving_market_and_run()
    {
        using var scratch = TestEnv.NewScratch("real-beside-proving");
        var real = Path.Combine(scratch.Dir, "real");
        var realDb = Path.Combine(real, "state", "tradeagent.db");
        var realTape = Path.Combine(real, "state", "tape.db");
        using (var db = new Database(realDb)) db.SetKv("owner-row", "kept");
        using (var tape = new TapeStore(realTape)) TapeReadings.Reading(tape, Start.AddMinutes(10), "0.00010000");
        Directory.CreateDirectory(Path.Combine(real, "workspace", "inbox"));
        File.WriteAllText(Path.Combine(real, "workspace", "inbox", "note.txt"), "the owner's");

        var before = Files(real);
        Assert.Contains("state/tradeagent.db", before.Keys);
        Assert.Contains("state/tape.db", before.Keys);

        var home = ProvingHome.Create(Path.Combine(real, "proving", "run-1"));
        using (var db = new Database(home.DatabaseFile, HomeKind.Proving))
        {
            using (var tape = new TapeStore(home.TapeFile, kind: HomeKind.Proving)) SyntheticMarket.Write(home, db, tape, Planted(3));
            new VenueStore(db).Sync(SyntheticMarket.Venues(VenueCatalog.BinanceSpot, ["SYNB"]));
            Backtest(home, db, new DatasetStore(db).All().Single());
        }

        // A REAL HOME'S STORES ARE NEVER A PROVING WRITER'S — shown on a real ledger and tape of their own, beside this one.
        using (var otherLedger = new Database(Path.Combine(scratch.Dir, "other.db")))
        using (var otherTape = new TapeStore(Path.Combine(scratch.Dir, "other-tape.db")))
        {
            var refused = Assert.Throws<InvalidOperationException>(() => SyntheticMarket.Write(home, otherLedger, otherTape, Planted(1)));
            Assert.Contains("only into a proving home", refused.Message, StringComparison.Ordinal);
        }

        // AND EACH STORE REFUSES THE OTHER KIND, the real home's left as it was found.
        Assert.Throws<TradeAgentException>(() => new Database(realDb, HomeKind.Proving));
        Assert.Throws<InvalidOperationException>(() => new TapeStore(realTape, kind: HomeKind.Proving));
        Assert.Throws<TradeAgentException>(() => new Database(home.DatabaseFile));
        Assert.Throws<InvalidOperationException>(() => new TapeReader(home.TapeFile));

        var after = Files(real);
        Assert.Equal(before, after);
        log.WriteLine($"{before.Count} real-home files unchanged; the proving home holds {Directory.GetFiles(home.Dir, "*", SearchOption.AllDirectories).Length}");
    }

    /// <summary>Every file under <paramref name="home"/> outside <c>proving/</c>, by relative path, with its SHA-256.</summary>
    static SortedDictionary<string, string> Files(string home)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Directory.GetFiles(home, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(home, f).Replace('\\', '/');
            if (rel.StartsWith("proving/", StringComparison.Ordinal)) continue;
            files[rel] = Sha256Hex.OfFile(f)!;
        }
        return files;
    }
}

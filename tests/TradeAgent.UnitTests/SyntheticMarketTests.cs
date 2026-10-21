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

    /// <summary>
    /// R1 (SIM-001): A PROVING HOME OVER A REAL OR UNMARKED DIRECTORY IS REFUSED, AND THE REAL HOME IS UNCHANGED. A
    /// <see cref="ProvingHome"/> exists only through its factory — no public constructor, no record copy that could point
    /// one at another directory — and the factory refuses a directory holding anything but a marked proving home: a real
    /// home (its database marked real) and an unmarked one (files, no proving mark) alike, before anything is created in
    /// either. The writer refuses stores that do not live in the home it was handed, before it writes anything.
    /// </summary>
    [Fact]
    public void A_proving_home_over_a_real_or_unmarked_directory_is_refused_and_the_real_home_is_unchanged()
    {
        Assert.Empty(typeof(ProvingHome).GetConstructors());
        Assert.Null(typeof(ProvingHome).GetMethod("<Clone>$"));
        Assert.Null(typeof(ProvingHome).GetProperty(nameof(ProvingHome.Dir))!.SetMethod);

        using var scratch = TestEnv.NewScratch("proving-over-real");
        var real = Path.Combine(scratch.Dir, "real");
        using (var db = new Database(Path.Combine(real, "state", "tradeagent.db"))) db.SetKv("owner-row", "kept");
        using (var tape = new TapeStore(Path.Combine(real, "state", "tape.db"))) TapeReadings.Reading(tape, Start.AddMinutes(10), "0.00010000");
        var unmarked = Path.Combine(scratch.Dir, "unmarked");
        Directory.CreateDirectory(Path.Combine(unmarked, "workspace", "inbox"));
        File.WriteAllText(Path.Combine(unmarked, "workspace", "inbox", "note.txt"), "the owner's");
        var before = (Real: Files(real), Unmarked: Files(unmarked));

        foreach (var dir in new[] { real, unmarked })
        {
            var refused = Assert.Throws<InvalidOperationException>(() => ProvingHome.Create(dir));
            log.WriteLine(refused.Message);
            Assert.Contains(dir, refused.Message, StringComparison.Ordinal);
            Assert.Contains("is not a proving home", refused.Message, StringComparison.Ordinal);
        }
        Assert.False(Directory.Exists(Path.Combine(unmarked, "state")));

        // THE WRITER: one proving home's stores are never written as another's.
        var one = ProvingHome.Create(Path.Combine(scratch.Dir, "one"));
        var two = ProvingHome.Create(Path.Combine(scratch.Dir, "two"));
        using (var db = new Database(two.DatabaseFile, HomeKind.Proving))
        using (var tape = new TapeStore(two.TapeFile, kind: HomeKind.Proving))
        {
            var cross = Assert.Throws<InvalidOperationException>(() => SyntheticMarket.Write(one, db, tape, Planted(1)));
            log.WriteLine(cross.Message);
            Assert.Contains("does not live in the proving home", cross.Message, StringComparison.Ordinal);
            Assert.Empty(new DatasetStore(db).All());
            Assert.Empty(tape.Fetches());
        }
        Assert.False(Directory.Exists(one.DataDir));
        Assert.False(Directory.Exists(one.TruthDir));

        Assert.Equal(before.Real, Files(real));
        Assert.Equal(before.Unmarked, Files(unmarked));
    }

    /// <summary>
    /// R3: A SECOND MARKET FOR THE SAME INSTRUMENT AND MONTH LEAVES THE FIRST DATASET VALID AND ITS TRUTH UNCHANGED. Two
    /// seeds for SYNB in August, a week apart, in one proving home: each market's raw files and truth are its own — named by
    /// its spec — so the first dataset still passes the ledger's integrity check and its truth file is the same bytes; and
    /// the same market written again is refused before anything is written, never written over.
    /// </summary>
    [Fact]
    public void A_second_market_for_the_same_instrument_and_month_leaves_the_first_dataset_valid_and_its_truth_unchanged()
    {
        using var scratch = TestEnv.NewScratch("second-market");
        var home = ProvingHome.Create(Path.Combine(scratch.Dir, "proving"));
        using var db = new Database(home.DatabaseFile, HomeKind.Proving);
        using var tape = new TapeStore(home.TapeFile, kind: HomeKind.Proving);
        var store = new DatasetStore(db);

        var first = SyntheticMarket.Write(home, db, tape, Planted(2));
        var truth = File.ReadAllBytes(first.TruthFile);
        var second = SyntheticMarket.Write(home, db, tape, Planted(2) with { Seed = 13, Start = Start.AddDays(7) });

        Assert.Equal(first.Dataset.Files.Single().Month, second.Dataset.Files.Single().Month);
        Assert.NotEqual(first.RawSha256, second.RawSha256);
        Assert.NotEqual(first.Dataset.Files.Single().Path, second.Dataset.Files.Single().Path);
        Assert.NotEqual(first.TruthFile, second.TruthFile);

        void Valid()
        {
            foreach (var set in new[] { first.Dataset, second.Dataset })
            {
                var now = store.Checked(store.ById(set.Id)!);
                Assert.True(now.State == DatasetState.ACCEPTED, $"dataset {set.Id}: {now.RejectedReason}");
            }
            Assert.Equal(truth, File.ReadAllBytes(first.TruthFile));
        }
        Valid();

        var again = Assert.Throws<InvalidOperationException>(() => SyntheticMarket.Write(home, db, tape, Planted(2)));
        log.WriteLine(again.Message);
        Assert.Contains("is already written in this proving home", again.Message, StringComparison.Ordinal);
        Assert.Equal(2, store.All().Count);
        Valid();
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

/// <summary>
/// The tests that move the process's working directory: run alone, after every parallel class, so no other test resolves
/// a relative path against a directory one of these set.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WorkingDirectory
{
    public const string Name = "the process's working directory";
}

/// <summary>
/// R1, COMPLETED (<c>U-proving-ground-a</c> fix pass 2): a store's path is canonical and absolute from the moment it is
/// opened, so a handle opened RELATIVELY in one proving home never passes as another's after the working directory moves.
/// </summary>
[Collection(WorkingDirectory.Name)]
public class ProvingHandlePathTests(ITestOutputHelper log)
{
    [Fact]
    public void A_handle_opened_relatively_in_one_proving_home_is_refused_as_another_after_the_working_directory_moves()
    {
        using var scratch = TestEnv.NewScratch("relative-handles");
        var a = Directory.CreateDirectory(Path.Combine(scratch.Dir, "a")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(scratch.Dir, "b")).FullName;
        var spec = new SyntheticMarketSpec(12, "SYNB", VenueCatalog.BinanceSpot, new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero), 1,
            new SyntheticEdge(16));
        var was = Directory.GetCurrentDirectory();
        try
        {
            // HOME A AND ITS HANDLES, ALL BY THE SAME RELATIVE PATH, opened while the working directory is a.
            Directory.SetCurrentDirectory(a);
            var homeA = ProvingHome.Create("proving");
            using var dbA = new Database(Path.Combine("proving", "state", "tradeagent.db"), HomeKind.Proving);
            using var tapeA = new TapeStore(Path.Combine("proving", "state", "tape.db"), kind: HomeKind.Proving);

            // THE SAME RELATIVE PATH NOW NAMES HOME B.
            Directory.SetCurrentDirectory(b);
            var homeB = ProvingHome.Create("proving");

            var cross = Assert.Throws<InvalidOperationException>(() => SyntheticMarket.Write(homeB, dbA, tapeA, spec));
            log.WriteLine(cross.Message);
            Assert.Contains("does not live in the proving home", cross.Message, StringComparison.Ordinal);
            Assert.Empty(new DatasetStore(dbA).All());
            Assert.Empty(tapeA.Fetches());
            Assert.False(Directory.Exists(homeB.DataDir));
            Assert.False(Directory.Exists(homeB.TruthDir));

            // AND THE SAME HANDLES STILL WRITE INTO THE HOME THEY WERE OPENED IN, wherever the process now stands.
            var written = SyntheticMarket.Write(homeA, dbA, tapeA, spec);
            Assert.StartsWith(homeA.Dir, written.TruthFile, StringComparison.Ordinal);
            Assert.False(Directory.Exists(homeB.DataDir));
        }
        finally
        {
            Directory.SetCurrentDirectory(was);
        }
    }
}

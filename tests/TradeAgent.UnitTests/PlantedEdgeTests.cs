using System.Globalization;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Features;
using TradeAgent.Core.Proving;
using TradeAgent.Core.Strategy;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE MARKET'S FULL VOCABULARY (<c>U-proving-ground-b</c>; <c>docs/VISION.md</c> § 6.4): planted edges of known size,
/// half-life and regime, inside and beside doctrine, on series shaped like the real families' — each observed through the
/// bars and the tape the unchanged app reads, never through the generator's own arithmetic. Synthetic by construction:
/// every number is a hand-set constant or a seeded draw. No network, no model.
/// </summary>
public class PlantedEdgeTests(ITestOutputHelper log)
{
    internal static readonly DateTimeOffset Start = new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A fresh proving home under <paramref name="scratch"/>, with its two stores open.</summary>
    internal sealed class Proving : IDisposable
    {
        public Proving(string dir)
        {
            Home = ProvingHome.Create(dir);
            Db = new Database(Home.DatabaseFile, HomeKind.Proving);
            Tape = new TapeStore(Home.TapeFile, kind: HomeKind.Proving);
        }

        public ProvingHome Home { get; }
        public Database Db { get; }
        public TapeStore Tape { get; }

        public SyntheticMarketWritten Write(SyntheticMarketSpec spec) => SyntheticMarket.Write(Home, Db, Tape, spec);

        public void Dispose()
        {
            Tape.Dispose();
            Db.Dispose();
        }
    }

    /// <summary>Every minute's close of a written market, by the minute's open time, read from its normalised file.</summary>
    internal static Dictionary<DateTimeOffset, decimal> Closes(SyntheticMarketWritten market)
    {
        var closes = new Dictionary<DateTimeOffset, decimal>();
        foreach (var line in File.ReadLines(market.Dataset.NormalisedPath).Skip(1))
        {
            var f = line.Split(',');
            closes[DateTimeOffset.Parse(f[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)] =
                decimal.Parse(f[4], CultureInfo.InvariantCulture);
        }
        return closes;
    }

    /// <summary>Every reading of <paramref name="series"/> for <paramref name="subject"/> on the tape, in source-time order.</summary>
    internal static List<TapeObservation> Readings(TapeStore tape, string series, string subject) =>
        [.. tape.Fetches(ProvingSources.Synthetic, series, 100_000).SelectMany(f => tape.ObservationsOf(f.Id))
            .Where(o => o.Subject == subject).OrderBy(o => o.SourceTime)];

    /// <summary>A reading's value field, as its payload carries it.</summary>
    internal static decimal Value(TapeObservation row, string field) =>
        decimal.Parse(JsonDocument.Parse(row.Payload!).RootElement.GetProperty(field).GetString()!, CultureInfo.InvariantCulture);

    /// <summary>The move, in ticks, from the open of the hour starting at <paramref name="hour"/> to <paramref name="minutes"/> minutes later.</summary>
    internal static decimal Move(Dictionary<DateTimeOffset, decimal> closes, DateTimeOffset hour, int minutes = 60) =>
        (closes[hour.AddMinutes(minutes - 1)] - closes[hour.AddMinutes(-1)]) / SyntheticMarket.TickSize;

    /// <summary>
    /// THE MEASURED SLOPE OF THE PLANTED RELATION over the readings whose move starts from day <paramref name="from"/> to day
    /// <paramref name="to"/>: Σ z·R / Σ z², R the move over the hour after the first hourly close after the reading was first
    /// seen, z the reading in the series' integer units — ticks per unit, the edge's β·w where it is on, 0 where it is off.
    /// </summary>
    internal static double Slope(SyntheticMarketWritten market, TapeStore tape, string series, string field, decimal unit, decimal neutral,
        int from, int to)
    {
        var closes = Closes(market);
        var start = market.Dataset.FirstBar!.Value;
        double zr = 0, zz = 0;
        foreach (var row in Readings(tape, series, market.Dataset.Pair))
        {
            var hour = new DateTimeOffset(row.ReceivedAt.UtcTicks - row.ReceivedAt.UtcTicks % TimeSpan.TicksPerHour, TimeSpan.Zero).AddHours(1);
            var day = (hour - start).TotalDays;
            if (day < from || day >= to || !closes.ContainsKey(hour.AddMinutes(59))) continue;
            var z = (double)((Value(row, field) - neutral) / unit);
            zr += z * (double)Move(closes, hour);
            zz += z * z;
        }
        return zr / zz;
    }

    /// <summary>The manifest of a written market, parsed.</summary>
    internal static JsonElement Truth(SyntheticMarketWritten market) => JsonDocument.Parse(File.ReadAllText(market.TruthFile)).RootElement;

    /// <summary>
    /// (a) A PLANTED EDGE DECAYS BY ITS HALF-LIFE AND IS OFF OUTSIDE ITS REGIME. Two markets of four weeks: one whose edge
    /// halves every seven days, one whose edge is on in weeks two and four only. Measured through the bars the backtest
    /// reads and the readings the tape serves: the decaying edge's slope in week one is about β·0.72 and in week four under
    /// a quarter of it; the regime edge's slope is about β inside its windows and about zero outside them. The manifest
    /// states each edge's half-life, windows, mean weight and size as the spec implies them.
    /// </summary>
    [Fact]
    public void A_planted_edge_decays_by_its_half_life_and_is_off_outside_its_regime()
    {
        using var scratch = TestEnv.NewScratch("planted-decay");
        using var p = new Proving(Path.Combine(scratch.Dir, "proving"));
        const long Beta = 16;
        var decaying = p.Write(new SyntheticMarketSpec(21, "SYNA", VenueCatalog.BinanceSpot, Start, 28, new SyntheticEdge(Beta) { HalfLifeDays = 7 }));
        var regime = p.Write(new SyntheticMarketSpec(22, "SYNB", VenueCatalog.BinanceSpot, Start, 28,
            new SyntheticEdge(Beta) { Regimes = [new SyntheticRegime(7, 14), new SyntheticRegime(21, 28)] }));

        double S(SyntheticMarketWritten m, int from, int to) =>
            Slope(m, p.Tape, ProvingSources.Signal, ProvingSources.ValueField, 0.0001m, 0m, from, to);

        var weeks = Enumerable.Range(0, 4).Select(w => (Decaying: S(decaying, 7 * w, 7 * w + 7), Regime: S(regime, 7 * w, 7 * w + 7))).ToList();
        for (var w = 0; w < 4; w++) log.WriteLine($"week {w + 1}: decaying slope {weeks[w].Decaying:0.00}, regime slope {weeks[w].Regime:0.00} (β {Beta})");

        Assert.InRange(weeks[0].Decaying, 0.55 * Beta, 0.9 * Beta);
        Assert.True(weeks[1].Decaying < weeks[0].Decaying && weeks[2].Decaying < weeks[1].Decaying, "the decaying edge does not fall week on week");
        Assert.True(weeks[3].Decaying < weeks[0].Decaying / 4, $"week four's slope {weeks[3].Decaying:0.00} is not under a quarter of week one's");

        foreach (var on in new[] { 1, 3 }) Assert.InRange(weeks[on].Regime, 0.8 * Beta, 1.2 * Beta);
        foreach (var off in new[] { 0, 2 }) Assert.InRange(weeks[off].Regime, -0.1 * Beta, 0.1 * Beta);

        // THE MANIFEST: what the spec implies, stated before any run.
        var d = Truth(decaying).GetProperty("planted")[0];
        var r = Truth(regime).GetProperty("planted")[0];
        log.WriteLine(d.ToString());
        Assert.Equal(7, d.GetProperty("half_life_days").GetInt32());
        Assert.False(r.TryGetProperty("half_life_days", out _), "an edge with no half-life states one");
        Assert.Equal([7, 21], r.GetProperty("regimes").EnumerateArray().Select(w => w.GetProperty("from_day").GetInt32()).ToArray());
        Assert.Equal("beside", d.GetProperty("doctrine").GetString());

        // 671 readings start a move inside 28 days (the last one's would start after the end); 336 of them inside the windows.
        Assert.Equal(671, r.GetProperty("figures").GetProperty("readings").GetInt32());
        Assert.Equal(Math.Round(336m / 671m, 6), r.GetProperty("figures").GetProperty("mean_weight").GetDecimal());
        // A half-life of a week over four weeks: a mean weight near (1 − 2^−4)·7/(28·ln 2) ≈ 0.338.
        Assert.InRange(d.GetProperty("figures").GetProperty("mean_weight").GetDecimal(), 0.33m, 0.35m);
        // β·E|z|·w̄ ticks at 1000.00, E|z| = 50·51/101: 16 × 25.2475 × 0.500745 / 10 ≈ 20.23 bps; the round trip 2 × (0.10% + 0.02%).
        Assert.Equal(20.23m, r.GetProperty("figures").GetProperty("bps_per_signal").GetDecimal());
        Assert.Equal(24m, r.GetProperty("figures").GetProperty("round_trip_bps").GetDecimal());
        Assert.True(r.GetProperty("figures").GetProperty("expected_sharpe_before_cost").GetDecimal()
                    > r.GetProperty("figures").GetProperty("expected_sharpe_after_cost").GetDecimal());
    }

    // PART A'S MARKET, PINNED AGAIN HERE (U-proving-ground-a's (d)): seed 12, SYNB, two days from 2026-08-03, β 16.
    const string GoldenRaw = "d2122a79958016b1d1bdf2483d4d1ae6849b32cc7b0c40e0b7ff310fba000cf4";
    const string GoldenNormalised = "b9e22f0d96e86b2cd73379de073f2ee16f2595279694d89a15712f05b288da01";
    const string GoldenPayloads = "0bd7136936e75c6a89456463febee44af42f27d744ff9d2a34ab5de7503972db";

    /// <summary>
    /// (g) GUARD: A SPEC WITH NO NEW FIELD IS PART A'S MARKET, BYTE FOR BYTE. Part a's spec, and the same spec with every new
    /// field spelled at its default — no further edge, the bare signal alone, an edge with no half-life, no regime, beside
    /// doctrine — write the raw files, normalised file and tape payloads part a pinned.
    /// </summary>
    [Fact]
    public void A_spec_with_no_new_field_writes_part_as_golden_market()
    {
        using var scratch = TestEnv.NewScratch("golden-again");
        var specs = new[]
        {
            new SyntheticMarketSpec(12, "SYNB", VenueCatalog.BinanceSpot, Start, 2, new SyntheticEdge(16)),
            new SyntheticMarketSpec(12, "SYNB", VenueCatalog.BinanceSpot, Start, 2,
                new SyntheticEdge(16, 60) { Series = ProvingSources.Signal, HalfLifeDays = null, Regimes = [], Doctrine = EdgeDoctrine.Beside })
            {
                Edges = [], Series = [ProvingSources.Signal]
            }
        };
        for (var i = 0; i < specs.Length; i++)
        {
            using var p = new Proving(Path.Combine(scratch.Dir, $"home-{i}"));
            var m = p.Write(specs[i]);
            log.WriteLine($"spec {i}: raw {m.RawSha256} normalised {m.Dataset.NormalisedSha256} payloads {m.PayloadDigest}");
            Assert.Equal(GoldenRaw, m.RawSha256);
            Assert.Equal(GoldenNormalised, m.Dataset.NormalisedSha256);
            Assert.Equal(GoldenPayloads, m.PayloadDigest);
            Assert.Equal(48, m.Readings);
        }
    }

    /// <summary>A program over one feature of the proving source: long while the condition holds, flat otherwise, hourly.</summary>
    internal static string Program(string symbol, string series, string field, string entry, string exit, int maxAgeSeconds = 7200,
        string kind = "latest") => $$"""
        instrument {{symbol}}
        bars 1h
        timeframe 1h
        data_freshness 2h
        max_decision_age 10m
        feature x = {"kind":"{{kind}}","input":{"source":"proving-synthetic","series":"{{series}}","subject":"{{symbol}}","field":"{{field}}"},"latency_s":5,"max_age_s":{{maxAgeSeconds}}}
        size fixed 1
        stop percent 50
        exit when {{exit}}
        entry when {{entry}}
        """;

    /// <summary>The venue's own cost model for a proving dataset, as the app resolves it.</summary>
    internal static ExecutionModel VenueCost(Database db, DatasetRecord set)
    {
        var cost = TradeAgent.Core.Strategy.VenueCostModel.For(set, new VenueStore(db), 10_000m);
        Assert.True(cost.Ok, cost.Why);
        Assert.False(cost.Model!.IsFrictionless);
        return cost.Model.Model;
    }

    /// <summary>
    /// THE UNCHANGED BACKTEST OVER ONE PROVING DATASET: the program parsed in a proving home, its bars from the proving ledger,
    /// its features from the proving tape, under <paramref name="model"/> and the Research Director's audience.
    /// </summary>
    internal static BacktestResult Backtest(ITestOutputHelper log, ProvingHome home, Database db, DatasetRecord set, string text,
        ExecutionModel model, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var program = StrategyParser.Parse(text, HomeKind.Proving);
        Assert.True(program.Ok, program.Why);
        var run = TradeAgent.Core.Strategy.Backtest.Over(new DatasetStore(db), set.Id, program.Program!, model,
            BarAudience.Pipe(CouncilRoles.Research), from, to, tape: new TapeReader(home.TapeFile, kind: HomeKind.Proving));
        Assert.True(run.Ok, run.Why);
        var m = run.Result!.Metrics;
        log.WriteLine($"{set.Pair} under {model.Canonical}: {run.Result.Outcome}, {m.Trades} trades, gross {m.GrossPnl}, fees {m.Fees}, net {m.NetPnl}");
        Assert.Equal(BacktestOutcome.COMPLETED, run.Result.Outcome);
        return run.Result;
    }

    /// <summary>
    /// ITEM 2: EACH SERIES FAMILY HAS ITS OWN FIELDS AND CADENCE, AND A PLAYBOOK'S PROGRAM FINDS AN EDGE INSIDE DOCTRINE AND NOT
    /// ONE BESIDE IT. Two markets of twelve weeks writing the three family-shaped series: one plants a contrarian relation on the funding-like
    /// rate, inside doctrine; the other plants one on the taker-ratio-like ratio, beside it. Each series' payload carries its
    /// own field names at its own cadence, every reading O-LIVE; the one program a playbook would recall — long after deeply
    /// negative funding — nets above zero at the venue's cost on the first and not on the second. An edge flagged inside
    /// doctrine on the bare signal, which no playbook can name, is refused before anything is written.
    /// </summary>
    [Fact]
    public void Each_series_family_has_its_own_fields_and_cadence_and_a_playbook_program_finds_an_inside_edge_and_not_a_beside_one()
    {
        using var scratch = TestEnv.NewScratch("series-families");
        using var p = new Proving(Path.Combine(scratch.Dir, "proving"));
        IReadOnlyList<string> families = [ProvingSources.Funding, ProvingSources.OpenInterest, ProvingSources.TakerRatio];
        const int Days = 84;
        var inside = p.Write(new SyntheticMarketSpec(31, "SYNA", VenueCatalog.BinanceSpot, Start, Days,
            new SyntheticEdge(-16) { Series = ProvingSources.Funding, Doctrine = EdgeDoctrine.Inside }) { Series = families });
        var beside = p.Write(new SyntheticMarketSpec(32, "SYNB", VenueCatalog.BinanceSpot, Start, Days,
            new SyntheticEdge(6) { Series = ProvingSources.TakerRatio }) { Series = families });
        Assert.Equal(Days * 3 + 2 * Days * 24, inside.Readings);

        // THE SHAPES: each family's own fields and cadence, recorded on time.
        foreach (var (series, fields, every) in new[]
                 {
                     (ProvingSources.Funding, new[] { "symbol", "fundingTime", "fundingRate" }, TimeSpan.FromHours(8)),
                     (ProvingSources.OpenInterest, new[] { "symbol", "timestamp", "sumOpenInterest" }, TimeSpan.FromHours(1)),
                     (ProvingSources.TakerRatio, new[] { "symbol", "timestamp", "buySellRatio" }, TimeSpan.FromHours(1))
                 })
        {
            var rows = Readings(p.Tape, series, "SYNA");
            Assert.Equal((int)(TimeSpan.FromDays(Days) / every), rows.Count);
            Assert.All(rows, r => Assert.Equal(TapeClass.Live, r.EvidenceClass));
            Assert.Equal(Start, rows[0].SourceTime);
            Assert.All(rows.Zip(rows.Skip(1)), pair => Assert.Equal(every, pair.Second.SourceTime - pair.First.SourceTime));
            Assert.Equal(fields.Order(StringComparer.Ordinal),
                JsonDocument.Parse(rows[0].Payload!).RootElement.EnumerateObject().Select(f => f.Name).Order(StringComparer.Ordinal));
            log.WriteLine($"{series}: {rows.Count} readings, first {rows[0].Payload}");
        }
        Assert.Empty(Readings(p.Tape, ProvingSources.Signal, "SYNA"));

        // THE OPEN-INTEREST-LIKE LEVEL MOVES IN STEPS OF 100.000 FROM ITS START, and its readings' changes are the series' units.
        var levels = Readings(p.Tape, ProvingSources.OpenInterest, "SYNA").Select(r => Value(r, "sumOpenInterest")).ToList();
        Assert.All(levels.Prepend(10_000_000m).Zip(levels), pair => Assert.Equal(0m, (pair.Second - pair.First) % 100m));

        // INSIDE DOCTRINE ON THE BARE SIGNAL IS REFUSED, NOTHING WRITTEN.
        var refused = Assert.Throws<ArgumentException>(() => p.Write(new SyntheticMarketSpec(33, "SYNC", VenueCatalog.BinanceSpot, Start, 2,
            new SyntheticEdge(16) { Doctrine = EdgeDoctrine.Inside })));
        log.WriteLine(refused.Message);
        Assert.Contains("INSIDE doctrine rides a series shaped like a real family's", refused.Message, StringComparison.Ordinal);
        Assert.Empty(Readings(p.Tape, ProvingSources.Signal, "SYNC"));

        // THE PLAYBOOK'S PROGRAM, UNCHANGED APP: long after deeply negative funding, flat once it is not.
        new VenueStore(p.Db).Sync(SyntheticMarket.Venues(VenueCatalog.BinanceSpot, ["SYNA", "SYNB"]));
        string Playbook(string symbol) => Program(symbol, ProvingSources.Funding, "fundingRate", "x < -0.00030", "x >= -0.00030", 9 * 3600);
        var found = Backtest(log, p.Home, p.Db, inside.Dataset, Playbook("SYNA"), VenueCost(p.Db, inside.Dataset));
        var missed = Backtest(log, p.Home, p.Db, beside.Dataset, Playbook("SYNB"), VenueCost(p.Db, beside.Dataset));
        Assert.True(found.Metrics.Trades >= 20 && missed.Metrics.Trades >= 20, $"the playbook traded {found.Metrics.Trades} and {missed.Metrics.Trades} times");
        Assert.True(found.Metrics.NetPnl > 0m, $"the playbook nets {found.Metrics.NetPnl} on the edge inside doctrine");
        Assert.True(missed.Metrics.NetPnl <= 0m, $"the playbook nets {missed.Metrics.NetPnl} where the edge is beside doctrine");

        Assert.Equal("inside", Truth(inside).GetProperty("planted")[0].GetProperty("doctrine").GetString());
        Assert.Equal("beside", Truth(beside).GetProperty("planted")[0].GetProperty("doctrine").GetString());
        Assert.Equal(["fundingRate", "sumOpenInterest", "buySellRatio"],
            Truth(inside).GetProperty("series").EnumerateArray().Select(s => s.GetProperty("field").GetString()!).ToArray());
    }

    /// <summary>Pearson's correlation of two equal-length series.</summary>
    static double Correlation(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0, syy = 0;
        for (var i = 0; i < x.Count; i++)
        {
            sxy += (x[i] - mx) * (y[i] - my);
            sxx += (x[i] - mx) * (x[i] - mx);
            syy += (y[i] - my) * (y[i] - my);
        }
        return sxy / Math.Sqrt(sxx * syy);
    }

    /// <summary>
    /// (b) THE LEAKY JOIN IS SEEN BY A SOURCE-TIME JOIN AND NEVER THROUGH THE AS-OF GATE. A market whose signal readings are
    /// each first seen two hours after their source time — after the move each predicts. At every hourly close, the reading a
    /// join by source time holds is strongly correlated with the move over the next hour; the reading the app's gate
    /// (<see cref="FeatureEvaluator.Counts"/>, five seconds' latency) holds is not, because none had arrived. The manifest says
    /// what an honest app must say, and a leak first seen before its move ends is refused as no leak at all.
    ///
    /// <para><b>The watched mutant</b> — the trap's first-seen offset set to zero, the leak made honest — goes red here.</para>
    /// </summary>
    [Fact]
    public void The_leaky_join_is_seen_by_a_source_time_join_and_never_through_the_as_of_gate()
    {
        using var scratch = TestEnv.NewScratch("leaky-join");
        using var p = new Proving(Path.Combine(scratch.Dir, "proving"));
        var leak = p.Write(new SyntheticMarketSpec(41, "SYNA", VenueCatalog.BinanceSpot, Start, 21,
            SyntheticEdge.LeakyJoin(16, TimeSpan.FromHours(2))));
        var closes = Closes(leak);
        var rows = Readings(p.Tape, ProvingSources.Signal, "SYNA");
        var latency = TimeSpan.FromSeconds(5);

        var (bySource, byGate, moves) = (new List<double>(), new List<double>(), new List<double>());
        for (var close = Start.AddHours(4); closes.ContainsKey(close.AddMinutes(59)); close = close.AddHours(1))
        {
            var c = close;
            var source = rows.Last(r => r.SourceTime <= c);
            var gate = rows.Where(r => FeatureEvaluator.Counts(r, c, latency)).MaxBy(r => r.SourceTime)!;
            bySource.Add((double)Value(source, ProvingSources.ValueField));
            byGate.Add((double)Value(gate, ProvingSources.ValueField));
            moves.Add((double)Move(closes, c));
        }
        var (seen, unseen) = (Correlation(bySource, moves), Correlation(byGate, moves));
        log.WriteLine($"{moves.Count} hourly closes: the source-time join's correlation with the next hour's move {seen:0.000}, the as-of gate's {unseen:0.000}");

        Assert.True(seen > 0.5, $"a join by source time does not see the leak: correlation {seen:0.000}");
        Assert.True(Math.Abs(unseen) < 0.15, $"the as-of gate sees the leak: correlation {unseen:0.000}");

        var trap = Truth(leak).GetProperty("traps")[0];
        Assert.Equal("leaky-join", trap.GetProperty("trap").GetString());
        Assert.Equal(7200, trap.GetProperty("first_seen_after_seconds").GetInt64());
        Assert.StartsWith("no edge:", trap.GetProperty("honest_app_says").GetString(), StringComparison.Ordinal);
        Assert.Empty(Truth(leak).GetProperty("planted").EnumerateArray());
        Assert.Equal(7200, Truth(leak).GetProperty("series")[0].GetProperty("first_seen_after_seconds").GetInt64());

        // A "LEAK" FIRST SEEN BEFORE ITS MOVE ENDS IS NO LEAK, and is refused before anything is written.
        var honest = Assert.Throws<ArgumentException>(() => p.Write(new SyntheticMarketSpec(42, "SYNB", VenueCatalog.BinanceSpot, Start, 2,
            SyntheticEdge.LeakyJoin(16, TimeSpan.FromMinutes(30)))));
        log.WriteLine(honest.Message);
        Assert.Contains("first seen after the move they predict", honest.Message, StringComparison.Ordinal);
        Assert.Empty(Readings(p.Tape, ProvingSources.Signal, "SYNB"));
    }

    /// <summary>
    /// (c) THE PRE-FEE EDGE PASSES FRICTIONLESS AND FAILS AT THE VENUE COST. A trap twice whose largest move per signal is
    /// below the venue's round trip (β 2; fix pass 1 refuses the β 3 this test planted before, whose average signal was under
    /// the round trip and whose best selection was not): the unchanged backtest of a program that trades it nets above zero under
    /// <see cref="ExecutionModel.Frictionless"/> and below zero under the app's own venue cost model. The manifest states the
    /// gross under the round trip; a "pre-fee" edge above it is refused as a real edge.
    /// </summary>
    [Fact]
    public void The_pre_fee_edge_passes_frictionless_and_fails_at_the_venue_cost()
    {
        using var scratch = TestEnv.NewScratch("pre-fee");
        using var p = new Proving(Path.Combine(scratch.Dir, "proving"));
        var market = p.Write(new SyntheticMarketSpec(51, "SYNA", VenueCatalog.BinanceSpot, Start, 21, SyntheticEdge.PreFeeOnly(2)));
        new VenueStore(p.Db).Sync(SyntheticMarket.Venues(VenueCatalog.BinanceSpot, ["SYNA"]));

        var program = Program("SYNA", ProvingSources.Signal, ProvingSources.ValueField, "x > 0.0030", "x <= 0.0030");
        var free = Backtest(log, p.Home, p.Db, market.Dataset, program, ExecutionModel.Frictionless);
        var venue = Backtest(log, p.Home, p.Db, market.Dataset, program, VenueCost(p.Db, market.Dataset));
        Assert.True(free.Metrics.Trades >= 20, $"the program traded {free.Metrics.Trades} times");
        Assert.True(free.Metrics.NetPnl > 0m, $"frictionless, the pre-fee edge nets {free.Metrics.NetPnl}");
        Assert.True(venue.Metrics.NetPnl < 0m, $"at the venue's cost, the pre-fee edge nets {venue.Metrics.NetPnl}");

        var trap = Truth(market).GetProperty("traps")[0];
        Assert.Equal("pre-fee-only", trap.GetProperty("trap").GetString());
        var figures = trap.GetProperty("figures");
        Assert.True(figures.GetProperty("bps_per_signal").GetDecimal() < figures.GetProperty("round_trip_bps").GetDecimal());
        Assert.True(figures.GetProperty("expected_sharpe_before_cost").GetDecimal() > 0m);
        Assert.True(figures.GetProperty("expected_sharpe_after_cost").GetDecimal() < 0m);

        var real = Assert.Throws<ArgumentException>(() => p.Write(new SyntheticMarketSpec(52, "SYNB", VenueCatalog.BinanceSpot, Start, 2,
            SyntheticEdge.PreFeeOnly(16))));
        log.WriteLine(real.Message);
        Assert.Contains("it is a real edge, not a trap", real.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// EVERY PRE-FEE TRAP THE BUILD ACCEPTS LOSES AT THE VENUE'S COST, HOWEVER SELECTIVELY ITS SIGNALS ARE TRADED (fix pass 1,
    /// finding 1). For each β from 10 down to 1: a trap the build refuses is refused in words; on one it accepts, the existing
    /// threshold program (long while the signal is above 0.0030) and the most selective threshold the series allows (only its
    /// largest reading, 0.0050) are backtested, and what the planted relation earned them — the trap market's frictionless net
    /// less the same seed's noise twin's, the same trades on the same noise — is below what the venue charged them on the trap
    /// market. Noise-free by construction: the twin is the same seed without the relation, and the signal that times the trades
    /// is the same stream in both. The hysteresis program the bound is drawn for — in at the largest reading, out only at the
    /// smallest — is logged, not asserted: its planted gross per round trip is 2R·β in expectation but its few long holds make
    /// any one market's figure noisy.
    /// </summary>
    [Fact]
    public void Every_pre_fee_trap_the_build_accepts_loses_at_the_venue_cost_however_selectively_its_signals_are_traded()
    {
        using var scratch = TestEnv.NewScratch("pre-fee-selective");
        const ulong Seed = 53;
        string[][] programs =
        [
            ["x > 0.0030", "x <= 0.0030"],
            ["x > 0.0049", "x <= 0.0049"]
        ];
        string[] hysteresis = ["x > 0.0049", "x < -0.0049"];

        using var twin = new Proving(Path.Combine(scratch.Dir, "twin"));
        var noise = twin.Write(new SyntheticMarketSpec(Seed, "SYNA", VenueCatalog.BinanceSpot, Start, 21));
        new VenueStore(twin.Db).Sync(SyntheticMarket.Venues(VenueCatalog.BinanceSpot, ["SYNA"]));

        var accepted = 0;
        for (var beta = 10L; beta >= 1; beta--)
        {
            using var p = new Proving(Path.Combine(scratch.Dir, $"beta-{beta}"));
            SyntheticMarketWritten market;
            try
            {
                market = p.Write(new SyntheticMarketSpec(Seed, "SYNA", VenueCatalog.BinanceSpot, Start, 21, SyntheticEdge.PreFeeOnly(beta)));
            }
            catch (ArgumentException refused)
            {
                log.WriteLine($"β {beta}: refused — {refused.Message}");
                Assert.Contains("it is a real edge, not a trap", refused.Message, StringComparison.Ordinal);
                continue;
            }
            accepted++;
            new VenueStore(p.Db).Sync(SyntheticMarket.Venues(VenueCatalog.BinanceSpot, ["SYNA"]));

            foreach (var rule in programs.Append(hysteresis))
            {
                var program = Program("SYNA", ProvingSources.Signal, ProvingSources.ValueField, rule[0], rule[1]);
                var free = Backtest(log, p.Home, p.Db, market.Dataset, program, ExecutionModel.Frictionless);
                var venue = Backtest(log, p.Home, p.Db, market.Dataset, program, VenueCost(p.Db, market.Dataset));
                var bare = Backtest(log, twin.Home, twin.Db, noise.Dataset, program, ExecutionModel.Frictionless);
                Assert.Equal(bare.Metrics.Trades, free.Metrics.Trades);
                Assert.Equal(free.Metrics.Trades, venue.Metrics.Trades);

                var planted = free.Metrics.NetPnl!.Value - bare.Metrics.NetPnl!.Value;
                var cost = free.Metrics.NetPnl!.Value - venue.Metrics.NetPnl!.Value;
                log.WriteLine($"β {beta}, entry {rule[0]}, exit {rule[1]}: {free.Metrics.Trades} trades, planted gross {planted}, venue cost {cost}, "
                              + $"net at the venue {venue.Metrics.NetPnl}");
                if (rule == hysteresis) continue;
                Assert.True(free.Metrics.Trades > 0, $"β {beta}: entry {rule[0]} never traded");
                Assert.True(planted < cost,
                    $"β {beta}: the pre-fee trap paid for its costs — entry {rule[0]}, exit {rule[1]}: planted gross {planted} against a venue cost of {cost}");
            }
        }
        Assert.True(accepted > 0, "the build accepts no pre-fee trap at all");
    }

    /// <summary>
    /// NO COMBINATION OF A MARKET'S PRE-FEE SIGNALS CLEARS THE ROUND TRIP (fix pass 2, finding 1). A β 2 trap on the bare
    /// signal and one on the taker-ratio-like series are each accepted alone — twice the largest move per signal is 200 ticks,
    /// 20 bps of the starting price, below binance-spot's 24 — but a program reading both signals holds through the moves of
    /// both on one round trip, so the market that plants both is refused in words: together they bound 40 bps, a real edge.
    /// </summary>
    [Fact]
    public void Pre_fee_traps_on_two_series_are_refused_when_together_they_can_clear_the_round_trip()
    {
        using var scratch = TestEnv.NewScratch("pre-fee-combined");
        IReadOnlyList<string> both = [ProvingSources.Signal, ProvingSources.TakerRatio];

        using (var alone = new Proving(Path.Combine(scratch.Dir, "alone")))
        {
            alone.Write(new SyntheticMarketSpec(55, "SYNA", VenueCatalog.BinanceSpot, Start, 2, SyntheticEdge.PreFeeOnly(2)) { Series = both });
            alone.Write(new SyntheticMarketSpec(55, "SYNB", VenueCatalog.BinanceSpot, Start, 2,
                SyntheticEdge.PreFeeOnly(2, ProvingSources.TakerRatio)) { Series = both });
        }

        using var p = new Proving(Path.Combine(scratch.Dir, "combined"));
        var spec = new SyntheticMarketSpec(55, "SYNA", VenueCatalog.BinanceSpot, Start, 2, SyntheticEdge.PreFeeOnly(2))
        {
            Series = both,
            Edges = [SyntheticEdge.PreFeeOnly(2, ProvingSources.TakerRatio)]
        };
        var combined = Assert.Throws<ArgumentException>(() => p.Write(spec));
        log.WriteLine(combined.Message);
        Assert.Contains("it is a real edge, not a trap", combined.Message, StringComparison.Ordinal);
        Assert.Contains("is 40 bps of the starting price", combined.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(SyntheticMarket.TruthFile(p.Home, spec)), "a refused market wrote its truth");
    }

    /// <summary>
    /// A PLANTED MOVE WHOSE FIGURES CANNOT BE REPRESENTED IS REFUSED IN WORDS, NEVER STATED WITH A WRAPPED SHARPE (fix pass 2,
    /// finding 2). The reference trade's variance carries β² in ticks²: β 4,294,967,296 squares to 2^64, which a long wraps to
    /// zero, silently restoring the noise-only denominator. That β, the first past the largest whose square is a long
    /// (3,037,000,500) and the most negative long are refused before anything is written; the largest, 3,037,000,499, is
    /// planted and its manifest's Sharpe before cost is the reference trade's mean over its whole deviation, enumerated here
    /// independently — E|z| over the spread of |z| and the noise, every z from −50 to 50 equally likely, w 1 throughout.
    /// </summary>
    [Fact]
    public void A_planted_move_whose_figures_cannot_be_represented_is_refused_in_words_never_a_wrapped_sharpe()
    {
        using var scratch = TestEnv.NewScratch("planted-beta-range");
        using var p = new Proving(Path.Combine(scratch.Dir, "proving"));
        const int Days = 2;

        foreach (var beta in new[] { 4_294_967_296L, 3_037_000_500L, -3_037_000_500L, long.MinValue })
        {
            var spec = new SyntheticMarketSpec(57, "SYNA", VenueCatalog.BinanceSpot, Start, Days, new SyntheticEdge(beta));
            var refused = Assert.Throws<ArgumentException>(() => p.Write(spec));
            log.WriteLine($"β {beta}: {refused.Message}");
            Assert.Contains("cannot be represented", refused.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(SyntheticMarket.TruthFile(p.Home, spec)), $"β {beta}: a refused market wrote its truth");
        }

        const long Largest = 3_037_000_499L;
        var market = p.Write(new SyntheticMarketSpec(57, "SYNA", VenueCatalog.BinanceSpot, Start, Days, new SyntheticEdge(Largest)));
        var figures = Truth(market).GetProperty("planted")[0].GetProperty("figures");
        var readings = figures.GetProperty("readings").GetInt32();

        double draw = 0;
        for (var u = -10; u <= 10; u++) draw += (double)u * u / 21;
        var minute = 0.5 * 4 * draw + 0.5 * 4 * 4 * draw;
        double abs = 0, square = 0;
        for (var z = -50; z <= 50; z++)
        {
            abs += Math.Abs(z) / 101.0;
            square += (double)z * z / 101.0;
        }
        var beta2 = (double)Largest * Largest;
        var expected = Largest * abs / Math.Sqrt(beta2 * (square - abs * abs) + minute * 60) * Math.Sqrt(readings * 365.0 / Days);
        var said = (double)figures.GetProperty("expected_sharpe_before_cost").GetDecimal();
        log.WriteLine($"β {Largest}: {readings} readings, enumerated Sharpe {expected:0.0000} before cost; the manifest says {said}");
        Assert.InRange(said, expected - 0.006, expected + 0.006);
    }

    /// <summary>
    /// THE MANIFEST'S SHARPE IS THE REFERENCE TRADE'S MEAN OVER ITS TOTAL STANDARD DEVIATION (fix pass 1, finding 2). The
    /// reference trade takes every reading in the relation's direction, so its P&amp;L is |β|·|z|·w plus the horizon's noise.
    /// Enumerated here, independently of the generator's arithmetic: every reading's weight w from its move hour (the
    /// half-life and the regime), every z from −50 to 50 equally likely, the minute's noise from its four uniform draws and its
    /// two volatility regimes. The manifest's Sharpe before and after the venue's round trip match the enumeration's to the
    /// manifest's two places.
    /// </summary>
    [Fact]
    public void The_manifest_s_sharpe_is_the_reference_trade_s_mean_over_its_total_deviation()
    {
        using var scratch = TestEnv.NewScratch("planted-sharpe");
        using var p = new Proving(Path.Combine(scratch.Dir, "proving"));
        const int Days = 7;
        var edges = new[]
        {
            new SyntheticEdge(16, 30) { HalfLifeDays = 3, Regimes = [new SyntheticRegime(1, 5)] },
            new SyntheticEdge(-8) { Series = ProvingSources.TakerRatio }
        };
        var market = p.Write(new SyntheticMarketSpec(81, "SYNA", VenueCatalog.BinanceSpot, Start, Days, edges[0])
        {
            Series = [ProvingSources.Signal, ProvingSources.TakerRatio],
            Edges = [edges[1]]
        });

        // THE MINUTE'S NOISE: four draws from −10 to 10, doubled half the time.
        double draw = 0;
        for (var u = -10; u <= 10; u++) draw += (double)u * u / 21;
        var minute = 0.5 * 4 * draw + 0.5 * 4 * 4 * draw;
        const double Trip = 0.0024 * 100_000;

        var stated = Truth(market).GetProperty("planted");
        for (var e = 0; e < edges.Length; e++)
        {
            var edge = edges[e];
            var shape = ProvingSources.Shape(edge.Series)!;
            double n = 0, sum = 0, squares = 0;
            var readings = 0;
            for (var k = 0; k < shape.Count(Days); k++)
            {
                var hour = (shape.SourceMinute(k) * 60 + 2) / 3600 + 1;
                if (hour >= Days * 24) continue;
                readings++;
                var on = edge.Regimes.Count == 0 || edge.Regimes.Any(r => hour >= r.FromDay * 24 && hour < r.ToDay * 24);
                var w = !on ? 0 : edge.HalfLifeDays is { } h ? Math.Pow(2, -(double)hour / (h * 24)) : 1;
                for (var z = -50; z <= 50; z++)
                {
                    var pnl = Math.Abs(edge.BetaTicks) * Math.Abs(z) * w;
                    n++;
                    sum += pnl;
                    squares += pnl * pnl;
                }
            }
            var mean = sum / n;
            var sd = Math.Sqrt(squares / n - mean * mean + minute * edge.HorizonMinutes);
            var perYear = Math.Sqrt(readings * 365.0 / Days);
            var before = mean / sd * perYear;
            var after = (mean - Trip) / sd * perYear;

            var figures = stated[e].GetProperty("figures");
            var said = (Before: (double)figures.GetProperty("expected_sharpe_before_cost").GetDecimal(),
                After: (double)figures.GetProperty("expected_sharpe_after_cost").GetDecimal());
            log.WriteLine($"edge {e + 1}: enumerated Sharpe {before:0.0000} before cost, {after:0.0000} after; the manifest says {said.Before} and {said.After}");
            Assert.Equal(readings, figures.GetProperty("readings").GetInt32());
            Assert.InRange(said.Before, before - 0.006, before + 0.006);
            Assert.InRange(said.After, after - 0.006, after + 0.006);
        }
    }

    /// <summary>
    /// A CAMPAIGN IS OPENED ONLY WITH THE SPEC THAT WROTE ITS MARKET (fix pass 1, finding 3). A market written with its holdout
    /// from day 21: the proving process's press with the same spec but a holdout from day 7 is refused in words, and so is one
    /// with any other generating field changed — here the seed — with no cutoff and no campaign written; the spec that wrote
    /// the market opens its campaign at day 21.
    /// </summary>
    [Fact]
    public void A_campaign_is_opened_only_with_the_spec_that_wrote_its_market()
    {
        using var scratch = TestEnv.NewScratch("campaign-spec");
        using var p = new Proving(Path.Combine(scratch.Dir, "proving"));
        var spec = new SyntheticMarketSpec(91, "SYNA", VenueCatalog.BinanceSpot, Start, 28, SyntheticEdge.InSampleOnly(16))
        {
            Holdout = new SyntheticHoldout(21, TrialBudget: 10, VerdictBudget: 2)
        };
        var market = p.Write(spec);
        new VenueStore(p.Db).Sync(SyntheticMarket.Venues(VenueCatalog.BinanceSpot, ["SYNA"]));
        var at = Start.AddDays(28);

        var earlier = ProvingCampaign.Open(p.Home, p.Db, market, spec with { Holdout = new SyntheticHoldout(7, 10, 2) }, at);
        log.WriteLine(earlier.Why);
        Assert.False(earlier.Ok, "a campaign was opened from day 7 over a market whose in-sample trap was planted up to day 21");
        Assert.Contains("holdout", earlier.Why, StringComparison.Ordinal);

        var reseeded = ProvingCampaign.Open(p.Home, p.Db, market, spec with { Seed = 92 }, at);
        log.WriteLine(reseeded.Why);
        Assert.False(reseeded.Ok, "a campaign was opened with a spec of another seed");
        Assert.Contains("is not the spec that wrote", reseeded.Why, StringComparison.Ordinal);

        Assert.Null(new DatasetStore(p.Db).ById(market.Dataset.Id)!.HoldoutFrom);
        Assert.Empty(new CampaignStore(p.Db).All());

        var opened = ProvingCampaign.Open(p.Home, p.Db, market, spec, at);
        Assert.True(opened.Ok, opened.Why);
        Assert.Equal(Start.AddDays(21), opened.Campaign!.HoldoutFrom);
    }

    /// <summary>
    /// (f) THE MANIFEST IS WRITTEN ONLY INTO A PROVING HOME — and the holdout and campaign only into its own ledger. Beside a
    /// stand-in real home: a market with edges, traps and a holdout written through a real home's stores is refused before
    /// anything is written; written into the proving home, its manifest lies in that home's truth folder and states every
    /// edge, trap and the holdout; the proving process's holdout-and-campaign press is refused on the real ledger and on
    /// another proving home's, and on its own sets the spec's cutoff and budgets as research. The real home's files are the
    /// same bytes after all of it.
    /// </summary>
    [Fact]
    public void The_manifest_is_written_only_into_a_proving_home()
    {
        using var scratch = TestEnv.NewScratch("manifest-home");
        var real = Path.Combine(scratch.Dir, "real");
        var realDb = Path.Combine(real, "state", "tradeagent.db");
        var realTape = Path.Combine(real, "state", "tape.db");
        using (var db = new Database(realDb)) db.SetKv("owner-row", "kept");
        using (var tape = new TapeStore(realTape)) TapeReadings.Reading(tape, Start.AddMinutes(10), "0.00010000");
        var before = Hashes(real);

        var spec = new SyntheticMarketSpec(61, "SYNA", VenueCatalog.BinanceSpot, Start, 14, new SyntheticEdge(16) { HalfLifeDays = 10 })
        {
            Series = [ProvingSources.Signal, ProvingSources.TakerRatio],
            Edges = [SyntheticEdge.InSampleOnly(12, ProvingSources.TakerRatio)],
            Holdout = new SyntheticHoldout(10, TrialBudget: 7, VerdictBudget: 3)
        };

        using var p = new Proving(Path.Combine(real, "proving", "run-1"));
        using (var db = new Database(realDb))
        using (var tape = new TapeStore(realTape))
        {
            var refused = Assert.Throws<InvalidOperationException>(() => SyntheticMarket.Write(p.Home, db, tape, spec));
            Assert.Contains("only into a proving home", refused.Message, StringComparison.Ordinal);
        }
        Assert.False(Directory.Exists(p.Home.TruthDir));

        var market = p.Write(spec);
        Assert.StartsWith(p.Home.TruthDir + Path.DirectorySeparatorChar, market.TruthFile, StringComparison.Ordinal);
        var truth = Truth(market);
        log.WriteLine(truth.ToString());
        Assert.Equal(1, truth.GetProperty("planted").GetArrayLength());
        Assert.Equal("in-sample-only", truth.GetProperty("traps")[0].GetProperty("trap").GetString());
        Assert.Equal(10, truth.GetProperty("traps")[0].GetProperty("regimes")[0].GetProperty("to_day").GetInt32());
        Assert.Equal(Start.AddDays(10), truth.GetProperty("holdout").GetProperty("cutoff").GetDateTimeOffset());
        Assert.Equal(7, truth.GetProperty("holdout").GetProperty("trial_budget").GetInt32());

        // THE OWNER'S PRESS, PLAYED IN-PROCESS: never on a real ledger, never on another proving home's.
        new VenueStore(p.Db).Sync(SyntheticMarket.Venues(VenueCatalog.BinanceSpot, ["SYNA"]));
        var at = Start.AddDays(10);
        using (var db = new Database(realDb))
        {
            var onReal = Assert.Throws<InvalidOperationException>(() => ProvingCampaign.Open(p.Home, db, market, spec, at));
            log.WriteLine(onReal.Message);
            Assert.Contains("this database is a real home's (SIM-001)", onReal.Message, StringComparison.Ordinal);
        }
        using (var other = new Proving(Path.Combine(scratch.Dir, "other")))
        {
            var cross = Assert.Throws<InvalidOperationException>(() => ProvingCampaign.Open(p.Home, other.Db, market, spec, at));
            Assert.Contains("does not live in the proving home", cross.Message, StringComparison.Ordinal);
            Assert.Empty(new CampaignStore(other.Db).All());
        }

        var opened = ProvingCampaign.Open(p.Home, p.Db, market, spec, at);
        Assert.True(opened.Ok, opened.Why);
        Assert.Equal(7, opened.Campaign!.TrialBudget);
        Assert.Equal(3, opened.Campaign.VerdictBudget);
        Assert.Equal(Start.AddDays(10), opened.Campaign.HoldoutFrom);
        var set = new DatasetStore(p.Db).ById(market.Dataset.Id)!;
        Assert.Equal(Start.AddDays(10), set.HoldoutFrom);
        Assert.Equal(EvaluationClass.Research, set.EvaluationClass);

        var again = ProvingCampaign.Open(p.Home, p.Db, market, spec, at);
        Assert.False(again.Ok);
        log.WriteLine(again.Why);

        Assert.Equal(before, Hashes(real));
    }

    /// <summary>Every file under <paramref name="home"/> outside <c>proving/</c>, by relative path, with its SHA-256.</summary>
    static SortedDictionary<string, string> Hashes(string home)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Directory.GetFiles(home, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(home, f).Replace('\\', '/');
            if (!rel.StartsWith("proving/", StringComparison.Ordinal)) files[rel] = Sha256Hex.OfFile(f)!;
        }
        return files;
    }
}

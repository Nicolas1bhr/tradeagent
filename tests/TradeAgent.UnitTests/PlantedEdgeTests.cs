using System.Globalization;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Proving;
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
}

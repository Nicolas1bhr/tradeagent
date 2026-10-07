using System.Globalization;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Features;
using TradeAgent.Core.Strategy;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE FEATURE PROGRAMS' GOLDEN VECTORS — WHAT A PROGRAM THAT READS FEATURES OUTPUTS OVER FIXED BARS AND A FIXED TAPE,
/// PINNED BESIDE THE THREE NUMBERS THAT DECLARE WHAT IT MEANS (<c>U-language-v2a</c>).
///
/// <para><b>Why.</b> A feature program's evidence rests on four things this build computes: the program (its id, under
/// <see cref="StrategyVersions.Manifest"/>), the features' values (under <see cref="FeatureVersions.Manifest"/>), and the
/// trace and its figures (under <see cref="Referee.EvaluatorVersion"/>). A change to what any of it outputs with none of
/// the three moved fails here and prints the pins as this build computes them, so the change is a bump — which
/// withdraws the verdicts taken under the old meaning — and never a silent re-meaning of recorded evidence.</para>
///
/// <para><b>The inputs are fixtures and never generated.</b> The bars are <c>EvaluationGoldenVectorTests</c>' own
/// <c>trend-utc</c> series (<c>Golden/evaluation-bars.json</c>); the tape is <c>Golden/feature-tape.json</c> — 439
/// premium-index readings of BTCUSDT appended in order, one fetch each: archive readings from a loopback address until
/// 00:30Z and live ones after, a twelve-minute gap, a reading four minutes late, a revision, and one the tape's screen
/// quarantines. Both files' hashes are pinned and checked first.</para>
///
/// <para><b>What is pinned, per vector:</b> the program's id, the SHA-256 of every feature value the run read, the trace's
/// SHA-256 — its <c>Feature</c> lines included — and the run id that binds them all.</para>
/// </summary>
public class FeatureProgramGoldenVectorTests(ITestOutputHelper log)
{
    const string PinnedEvaluator = "backtest=2;metrics=1;scoring=1";
    const string PinnedManifest = "language=1;indicators=1;calendar=1";
    const string PinnedFeatures = "features=1";

    /// <summary>The SHA-256 of the tape fixture, its line endings read as LF.</summary>
    const string TapeFixtureSha256 = "542b3a265ea69e68278189491586aa9198488c8f09538c6f8cfd3bd2cc3eb614";

    /// <summary>The SHA-256 of the bars fixture — <c>EvaluationGoldenVectorTests.FixtureSha256</c>'s value, restated.</summary>
    const string BarsFixtureSha256 = "62069406c57c8c45e0decb2dfa33634437297f84b3fe56569cf4e3c0af57db9d";

    static string Input(string field) =>
        $$"""{"source":"binance-um-premium","series":"premium-index","subject":"BTCUSDT","field":"{{field}}"}""";

    sealed record Vector(string Name, string Program, decimal Fees, decimal Slippage, decimal Increment, decimal Capital)
    {
        public ExecutionModel Model => ExecutionModel.Declare(Fees, Slippage, Increment, Capital).Model!;
    }

    sealed record Pin(string Name, string Program, string Values, string Trace, string Run)
    {
        public string AsCode => $"        new(\"{Name}\", \"{Program}\",\n            \"{Values}\",\n            \"{Trace}\",\n            \"{Run}\"),";
    }

    static readonly Vector[] Vectors =
    [
        // The brief's latest funding, every minute: entries in the two dips, the archive stretch before the clean start,
        // the gap (absent), the late reading, and the quarantined one (absent).
        new("funding-latest-every-minute", $$"""
            instrument BTCUSDT
            feature funding = {"kind":"latest","input":{{Input("lastFundingRate")}},"latency_s":5,"max_age_s":180}
            size fixed 1
            stop percent 2
            exit when funding > 0.0001
            entry when funding < -0.0003
            """, Fees: 0.001m, Slippage: 0.0005m, Increment: 0.001m, Capital: 10_000m),

        // A fifteen-minute mean of the mark price on five-minute bars — over the revised reading — crossed by the close.
        new("mark-mean-window-five-minute", $$"""
            instrument BTCUSDT
            bars 5m
            feature avgmark = {"kind":"mean","input":{{Input("markPrice")}},"latency_s":0,"max_age_s":300,"window_s":900,"min_rows":10}
            size capital_fraction 0.5
            target percent 1
            exit when close < avgmark - 3
            entry when crosses_above(close, avgmark - 2)
            """, Fees: 0.0008m, Slippage: 0.0002m, Increment: 0.0001m, Capital: 5_000m),

        // A change and a latest together on fifteen-minute bars, a history reference inside a crossing, a maximum hold.
        new("funding-change-and-history-fifteen-minute", $$"""
            instrument BTCUSDT
            bars 15m
            feature dfund = {"kind":"change","mode":"diff","input":{{Input("lastFundingRate")}},"latency_s":5,"max_age_s":600,"lookback_s":1800}
            feature funding = {"kind":"latest","input":{{Input("lastFundingRate")}},"latency_s":5,"max_age_s":600}
            size fixed 2
            max_hold_bars 4
            exit when crosses_above(funding, funding[2])
            entry when dfund < 0 and funding < 0
            """, Fees: 0.001m, Slippage: 0m, Increment: 0.01m, Capital: 10_000m),

        // A rank over the hour, on hourly bars.
        new("funding-rank-hourly", $$"""
            instrument BTCUSDT
            bars 1h
            feature rank = {"kind":"pct-rank","input":{{Input("lastFundingRate")}},"latency_s":5,"max_age_s":3600,"window_s":3600,"min_rows":30}
            size fixed 1
            exit when rank > 0.8
            entry when rank <= 0.5
            """, Fees: 0.001m, Slippage: 0.0005m, Increment: 0.001m, Capital: 10_000m),
    ];

    static readonly Pin[] Pins =
    [
        new("funding-latest-every-minute", "429e884014a7327eacf46b5593663cd0329dcec3f655248f8775dbba48db4d2e",
            "1bf42a3a7a61676b4ba8017ca2e66ad3894f431d071e6a9ed5cc34bb510b66b2",
            "44281461351a467471773c711738f5ccf192f664e512bbdce37e1cbec4d27a6e",
            "d4074883ed7759de5df42f1be762144a0b338485ee97c8929a860d9f74694bc0"),
        new("mark-mean-window-five-minute", "beaf3ed496f80b858eb5698643b8a76dea8806c9aea341e8c4e7c385366e359f",
            "f67db0a5f1e9726b3614728afd325bc2f7e9cf1f64208e69ffee4cadc177b3ed",
            "4d0d07c2529ed298a2c91f21ebe89458b1e318a2f665f2df8ccf376b21d57544",
            "d1fddfe86eda942e1eb702bff82e50242ee49058322c623d8871e22a2de1f43c"),
        new("funding-change-and-history-fifteen-minute", "e646cc9f0f9e1d575b3ab3c101b37798dce51391acb094c08d681b1ba5172906",
            "66ab1125d64582f3588068dbc3106a8e25c0cf7b8e133239063fc08bb16fd1f7",
            "ec3d42a407e84821c073d28e4a3cd34e33701d123b97c87c08d15a89fa7800e5",
            "ee42d7c0491a9fa4dd40f384173f38283b988214416f7249b46ad149eb51715f"),
        new("funding-rank-hourly", "e8b9d27b4a75f854d78997aa237b15cecda23b4de99be802dedf478cd80ad1c5",
            "560542fb2e0886eda5cbbb4b69b738dfb47958eb373aa608f3b1ea6f79003798",
            "d213ef708b2ffe8d8b44446c7ba3ab39e40462dfcdb4728f3a1052f6d9f672cd",
            "145766b698e9843beee61d2637d9fbee41df4f7202c992c19bb3c92c57f3f78b"),
    ];

    // ---- the fixtures --------------------------------------------------------------------------

    static string TapeFixturePath() =>
        Path.Combine(DayOnePrograms.RepoRoot(), "tests", "TradeAgent.UnitTests", "Golden", "feature-tape.json");

    static string BarsFixturePath() =>
        Path.Combine(DayOnePrograms.RepoRoot(), "tests", "TradeAgent.UnitTests", "Golden", "evaluation-bars.json");

    static string Text(string path) => File.ReadAllText(path).ReplaceLineEndings("\n");

    /// <summary>The <c>trend-utc</c> series of the bars fixture: its start and its bars.</summary>
    static (DateTimeOffset Start, List<KlineBar> Bars) TrendUtc()
    {
        using var doc = JsonDocument.Parse(Text(BarsFixturePath()));
        var s = doc.RootElement.GetProperty("series").EnumerateArray().Single(e => e.GetProperty("name").GetString() == "trend-utc");
        var start = DateTimeOffset.Parse(s.GetProperty("start").GetString()!, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        return (start, [.. s.GetProperty("bars").EnumerateArray()
            .Select(b => new KlineBar(start.AddMinutes(b[0].GetInt32()),
                b[1].GetDecimal(), b[2].GetDecimal(), b[3].GetDecimal(), b[4].GetDecimal(), b[5].GetDecimal()))]);
    }

    /// <summary>The tape fixture written into a fresh tape file, reading by reading, one fetch each, in the file's order.</summary>
    static string Tape()
    {
        using var doc = JsonDocument.Parse(Text(TapeFixturePath()));
        var start = DateTimeOffset.Parse(doc.RootElement.GetProperty("start").GetString()!, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

        var file = Path.Combine(TestEnv.Home, $"golden-tape-{Guid.NewGuid():n}.db");
        using var store = new TapeStore(file);
        foreach (var r in doc.RootElement.GetProperty("readings").EnumerateArray())
        {
            var stamped = start.AddMinutes(r[0].GetInt32()).AddSeconds(r[1].GetInt32());
            var received = stamped.AddSeconds(r[2].GetInt32());
            var url = r[3].GetString() == "live"
                ? TapeSourceCatalog.BinanceUmBaseUrl + "/fapi/v1/premiumIndex"
                : "http://127.0.0.1:9/fapi/v1/premiumIndex";
            var note = r[6].GetString() is { Length: > 0 } words ? $",\"note\":\"{words}\"" : "";
            store.Append(new TapeFetch
            {
                Source = TapeSourceCatalog.Premium,
                Series = "premium-index",
                Url = url,
                RequestedAt = received.AddMilliseconds(-300),
                ReceivedAt = received,
                HttpStatus = 200
            },
            [
                new TapeItem("BTCUSDT", stamped,
                    $$"""{"symbol":"BTCUSDT","markPrice":"{{r[5].GetString()}}","lastFundingRate":"{{r[4].GetString()}}","time":{{stamped.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}}{{note}}}""")
            ]);
        }
        return file;
    }

    static (Vector Vector, BacktestResult Run, Pin Pin)[] RunAll(string tape)
    {
        var (start, bars) = TrendUtc();
        var all = new List<(Vector, BacktestResult, Pin)>();

        // THE RESEARCH DIRECTOR'S TAPE HOLDOUT OVER A LEDGER HOLDING NO CUTOFF (U-tape-holdout): no window, so every read
        // is served, no clean-history search is bounded, and the values are the referee's.
        using var ledger = TestEnv.NewDb();
        var holdout = TapeHoldout.Pipe(CouncilRoles.Research, new DatasetStore(ledger));
        foreach (var v in Vectors)
        {
            var parse = StrategyParser.Parse(v.Program);
            Assert.True(parse.Ok, $"golden vector {v.Name} no longer parses: {parse.Why}");
            var program = parse.Program!;
            var grid = BarGrid.For(program);
            using var feed = new FeatureFeed(new TapeReader(tape), holdout, program, grid.EndOf(grid.StartOf(bars[^1].OpenTime)));
            var run = Backtest.Run(program, new BacktestRequest(1, "golden:trend-utc", v.Model, start, null), bars,
                features: feed);
            all.Add((v, run, new Pin(v.Name, program.StrategyId, run.Request.FeaturesSha256!, run.Trace.Sha256, run.RunId)));
        }
        return [.. all];
    }

    static string Repin((Vector Vector, BacktestResult Run, Pin Pin)[] all, string tapeSha) =>
        $"    const string TapeFixtureSha256 = \"{tapeSha}\";\n\n    static readonly Pin[] Pins =\n    [\n"
        + string.Join("\n", all.Select(a => a.Pin.AsCode)) + "\n    ];";

    /// <summary>
    /// (p) CHANGED OUTPUT OF A FEATURE PROGRAM WITHOUT A VERSION BUMP FAILS. The fixtures are the ones pinned; the pins
    /// were computed under the three numbers this build declares; and every vector's program id, values, trace and run
    /// id are what was pinned — or the test says which number to bump and prints the pins to paste. Every vector really
    /// reads its features: each states a clean-history start, the minute vector counts the archive bars before it and
    /// the bars the gap and the quarantined reading left without a value, and every run COMPLETED.
    /// </summary>
    [Fact]
    public void Changed_feature_program_output_without_a_version_bump_fails_the_golden_vectors()
    {
        var tapeSha = Sha256Hex.Of(Text(TapeFixturePath()));
        Assert.Equal(BarsFixtureSha256, Sha256Hex.Of(Text(BarsFixturePath())));
        var all = RunAll(Tape());

        foreach (var (v, run, _) in all)
        {
            log.WriteLine($"{v.Name,-42} bars {run.Metrics.Bars,3} trades {run.Metrics.Trades,2} signals {run.Metrics.Signals,2} "
                          + $"undefined {run.Counters.UndefinedEvents,3} {run.Outcome}");
            foreach (var f in run.Trace.Of(BacktestEventKind.Feature)) log.WriteLine("    " + f.Reason);
            Assert.Equal(BacktestOutcome.COMPLETED, run.Outcome);
            Assert.All(run.Features, f => Assert.NotNull(f.CleanHistoryStart));
        }

        var minute = all.Single(a => a.Vector.Name == "funding-latest-every-minute").Run;
        Assert.True(minute.Features[0].BarsBefore > 0, "no bar of the minute vector was evaluated before the clean-history start");
        Assert.True(minute.Features[0].BarsAbsent > 0, "the gap and the quarantined reading left no bar without a value");
        Assert.True(minute.Metrics.Trades > 0, "the minute vector closed no trade");

        Assert.True(tapeSha == TapeFixtureSha256,
            $"the golden tape fixture {TapeFixturePath()} hashes {tapeSha} and was pinned at {TapeFixtureSha256}. Put the file "
            + "back, or — if the change is meant — say why in the commit and re-pin every vector over it:\n" + Repin(all, tapeSha));

        Assert.True(Referee.EvaluatorVersion == PinnedEvaluator && StrategyVersions.Manifest == PinnedManifest
                    && FeatureVersions.Manifest == PinnedFeatures,
            $"the semantics moved to {Referee.EvaluatorVersion} / {StrategyVersions.Manifest} / {FeatureVersions.Manifest}, and "
            + "the feature golden vectors are still pinned under the old ones. Set the three pinned numbers and re-pin in the "
            + "same commit:\n" + Repin(all, tapeSha));

        var pins = Pins.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var changed = all.Where(a => !pins.TryGetValue(a.Vector.Name, out var pinned) || pinned != a.Pin)
            .Select(a => a.Vector.Name).ToList();
        Assert.True(changed.Count == 0,
            $"{changed.Count} feature golden vector(s) produced output other than what was pinned — {string.Join(", ", changed)} "
            + "— while none of the three numbers moved. What a feature program is, what its features are worth at a close, "
            + "or what the backtest makes of them has changed: bump the number that owns it and re-pin in the same commit. "
            + "The pins as this build computes them:\n" + Repin(all, tapeSha));
    }

    /// <summary>
    /// EVERY FEATURE GOLDEN VECTOR WRITES THE SAME BYTES UNDER nl-BE'S AND fr-BE'S NUMBER FORMATS: the same trace text,
    /// values, run id — the <c>Feature</c> lines' words included, which carry instants and counts.
    /// </summary>
    [Fact]
    public void Every_feature_golden_vector_writes_the_same_bytes_under_nl_BE_and_fr_BE_number_formats()
    {
        var tape = Tape();
        var invariant = RunAll(tape).Select(a => (a.Run.Trace.Text, a.Pin)).ToList();

        foreach (var (culture, clone) in Cultures.Belgian)
        {
            var under = Cultures.Under(clone, () => RunAll(tape).Select(a => (a.Run.Trace.Text, a.Pin)).ToList());
            Assert.Equal(invariant.Count, under.Count);
            for (var i = 0; i < invariant.Count; i++)
            {
                Assert.True(invariant[i].Text == under[i].Text, $"{invariant[i].Pin.Name} wrote other trace bytes under {culture}");
                Assert.Equal(invariant[i].Pin, under[i].Pin);
            }
        }
    }
}

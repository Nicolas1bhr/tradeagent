using System.Diagnostics;
using System.Globalization;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-DOOR-BOUNDS ITEM 1, RE-RUN ON EVERY PLATFORM: THE LONGEST CALLS THE DOOR ADMITS, TIMED ON THE HOST THAT RUNS THEM, FIT THE DOOR'S LIMIT.
/// <see cref="BrokerSeat.LongestDoorCall"/> is a figure measured once; this is the measurement, so a change that slows a verdict or a backtest
/// past the limit's margin is seen here rather than as a model told its verdict failed. Each call runs at real size — a 36-month holdout of
/// one-minute bars for the verdict, the research cap's 200,000 bars for the backtest — and must finish inside
/// <see cref="BrokerSeat.DoorToolTimeout"/> divided by <see cref="HeavierProgram"/>: the host is the one measured, the program is the
/// cheapest one, and a real program may cost more per bar. Timing, because it reads a wall clock.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
[Trait("Category", "Timing")]
public class DoorCallDurationTests(ITestOutputHelper output)
{
    /// <summary>How much heavier per bar than these tests' program a real one is allowed for (the program's factor in <see cref="BrokerSeat.DoorTimeoutMargin"/>, five times five).</summary>
    const int HeavierProgram = 5;

    static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Bar0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    static TimeSpan Allowed => BrokerSeat.DoorToolTimeout / HeavierProgram;

    void Say(string what, long bars, TimeSpan took) =>
        output.WriteLine($"MEASURE {what}: {bars.ToString(CultureInfo.InvariantCulture)} bars in {took.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)} s "
                         + $"(allowed {Allowed.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s, the door's limit {BrokerSeat.DoorToolTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s)");

    static string Winning(string pair) =>
        $"instrument {pair}\nsize fixed 1\ntimeframe 1m\ndata_freshness 2m\nmax_decision_age 30s\nexit when close > 103\nentry when close < 97\n";

    static string Freeze(Database db, string source, DateTimeOffset at)
    {
        var parsed = StrategyParser.Parse(source);
        Assert.True(parsed.Ok, parsed.Why);
        var program = parsed.Program!;
        new StrategyStore(db).RecordVersion(new StrategyVersionRow(program.StrategyId, program.Source, program.Canonical, program.Manifest,
            StrategyStore.InterpreterBuild, ParseVerdict.Accepted, program.WarmUpBars, at, CouncilRoles.Research, "attempt-1")
        {
            Timeframe = program.Freshness?.Timeframe,
            DataFreshness = program.Freshness?.DataFreshness,
            MaxDecisionAge = program.Freshness?.MaxDecisionAge
        });
        return program.StrategyId;
    }

    /// <summary>
    /// THE LONGEST CALL: A VERDICT OVER THE LONGEST HOLDOUT THE APP DOWNLOADS. 36 months of every minute held back after one research month,
    /// judged by the app's own referee — the call the <c>verdict</c> op makes — and timed around it alone, never the test's own file writing.
    /// </summary>
    [Fact]
    public async Task A_verdict_over_a_36_month_holdout_fits_the_doors_limit()
    {
        const int months = BinanceArchive.MonthsWanted;
        var pair = TestEnv.NewPair();
        var (gw, _, db) = await TestEnv.Ready();
        using var owned = db;
        await using var gateway = gw;

        var end = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var cutoff = end.AddMonths(-months);
        var first = cutoff.AddMonths(-1);
        var dir = BinanceArchive.DatasetDir(pair);
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        var raw = Path.Combine(dir, "raw", $"{pair}-{Guid.NewGuid():n}.zip");
        File.WriteAllText(raw, "a stand-in for the vendor's monthly archive");
        var csv = Path.Combine(dir, $"{Guid.NewGuid():n}.csv");
        var bars = 0;
        using (var w = new StreamWriter(csv))
        {
            w.Write(KlineNormaliser.Header);
            w.Write('\n');
            for (var open = first; open < end; open = open.AddMinutes(1), bars++)
            {
                var close = open.DayOfYear % 4 == 3 ? 100 : 96 + bars / 3 % 10;
                w.Write(string.Create(CultureInfo.InvariantCulture,
                    $"{open.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{close},{close + 1},{close - 1},{close},1.00\n"));
            }
        }
        var id = gw.Datasets.Record(new DatasetRecord(0, BinanceArchive.Source, pair, BinanceArchive.Interval, "v1", months + 1, months + 1, [], csv,
            DatasetStore.Sha256(csv)!, bars, first, end.AddMinutes(-1), 0, [], false, 0, 0, 0, Now, DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-09", "https://127.0.0.1/x.zip", DatasetStore.Sha256(raw)!, DatasetStore.Sha256(raw)!,
                new FileInfo(raw).Length, first, KlineTimeUnit.Microseconds, raw)]));
        var (done, campaign) = gw.SetHoldout(id, cutoff, EvaluationClass.Research);
        Assert.True(done.Ok, done.Why);

        var winner = Freeze(db, Winning(pair), Now);
        var referee = new Referee(db, () => Now);
        var clock = Stopwatch.StartNew();
        var judged = referee.Verdict(winner, campaign!.Id);
        clock.Stop();

        Assert.True(judged.Ok, judged.Why);
        var run = new StrategyStore(db).RunById(judged.Promotion!.HoldoutRunId)!;
        Assert.True(run.Bars > 1_500_000, $"the verdict's run evaluated {run.Bars} bars, not a 36-month holdout");
        Say("verdict over 36 months", run.Bars, clock.Elapsed);
        Assert.True(clock.Elapsed <= Allowed,
            $"a verdict over 36 months took {clock.Elapsed.TotalSeconds:0.00} s, past {Allowed.TotalSeconds:0} s — the door's limit over {HeavierProgram}");
    }

    /// <summary>THE LONGEST RESEARCH CALL: a backtest of exactly the research cap's 200,000 bars, the most one run evaluates.</summary>
    [Fact]
    public async Task A_backtest_at_the_research_cap_fits_the_doors_limit()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        await using var _2 = gw;
        const int heldFrom = Backtest.MaxTracedBars + 1;
        var dir = BinanceArchive.DatasetDir("BTCUSDT");
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        var raw = Path.Combine(dir, "raw", $"BTCUSDT-{Guid.NewGuid():n}.zip");
        File.WriteAllText(raw, "a stand-in for the vendor's monthly archive");
        var csv = Path.Combine(dir, $"{Guid.NewGuid():n}.csv");
        var bars = 0;
        var last = 0;
        using (var w = new StreamWriter(csv))
        {
            w.Write(KlineNormaliser.Header);
            w.Write('\n');
            foreach (var minute in Enumerable.Range(0, Backtest.MaxTracedBars + 1).Concat(Enumerable.Range(heldFrom, 1440)))
            {
                var close = 96 + minute % 10;
                w.Write(string.Create(CultureInfo.InvariantCulture,
                    $"{Bar0.AddMinutes(minute).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{close},{close + 1},{close - 1},{close},1.00\n"));
                bars++;
                last = minute;
            }
        }
        var id = gw.Datasets.Record(new DatasetRecord(0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 12, [],
            csv, DatasetStore.Sha256(csv)!, bars, Bar0, Bar0.AddMinutes(last), 0, [], false, 0, 0, 0, Bar0, DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-01", "https://127.0.0.1/x.zip", DatasetStore.Sha256(raw)!, DatasetStore.Sha256(raw)!,
                new FileInfo(raw).Length, Bar0, KlineTimeUnit.Microseconds, raw)]));
        var (held, _) = gw.SetHoldout(id, Bar0.AddMinutes(heldFrom), EvaluationClass.Research);
        Assert.True(held.Ok, held.Why);
        var strategies = Path.Combine(Paths.RoleHome(CouncilRoles.Research), "strategies");
        Directory.CreateDirectory(strategies);
        File.WriteAllText(Path.Combine(strategies, "door-bounds.strategy"),
            "instrument BTCUSDT\nsize fixed 1\nexit when close < 97\nentry when close > 103\n");

        var clock = Stopwatch.StartNew();
        var ran = gw.Backtests.Run(AgentContext.ForAgent("agent", CouncilRoles.Research, "attempt-1"),
            new BacktestAsk("strategies/door-bounds.strategy", id, Bar0.AddMinutes(1), Bar0.AddMinutes(Backtest.MaxTracedBars), 0.001m, Increment: 1m));
        clock.Stop();

        Assert.Equal(BacktestOutcome.COMPLETED, ran.Result.Outcome);
        Assert.Equal(Backtest.MaxTracedBars, ran.Result.Metrics.Bars);
        Say("backtest at the research cap", ran.Result.Metrics.Bars, clock.Elapsed);
        Assert.True(clock.Elapsed <= Allowed,
            $"a 200,000-bar backtest took {clock.Elapsed.TotalSeconds:0.00} s, past {Allowed.TotalSeconds:0} s — the door's limit over {HeavierProgram}");
    }
}

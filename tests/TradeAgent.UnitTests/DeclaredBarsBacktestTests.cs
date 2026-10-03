using System.Globalization;
using System.Text;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A PROGRAM THAT DECLARES `bars 1h` IS BACKTESTED, AND JUDGED, ON HOURS — WHILE ITS FILLS, STOPS AND
/// TARGETS STAY ON THE MINUTE (<c>U-timeframe-a</c> item 3).
///
/// <para><b>Two clocks.</b> <c>Backtest.Run</c> walks closed minutes. On every minute a signal waiting
/// from the last decision fills at that minute's open and the stop and the target fire on that minute's
/// range. Only when one of the program's own bars closes is the maximum hold taken and the evaluator
/// asked, on that bar. So an hourly program's indicators, lookback, history, <c>max_hold_bars</c> and the
/// run cap count hours, its trace holds one line per hour plus its fills and exits, and a year is one
/// run. A program that declares nothing is unchanged, line for line (<c>DeclaredBarsGrammarTests</c>'s
/// guard (e) runs the golden vectors).</para>
///
/// <para><b>RED on the base</b> (<c>298bb36</c>) for every test that declares <c>bars</c>: the declaration
/// was refused by the parser, so each failed at its first parse. These tests use only what the base had —
/// the parser, <c>Backtest.Run</c>, <c>Backtest.Over</c> and the trace — so that is the only reason.</para>
/// </summary>
public class DeclaredBarsBacktestTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Start = new(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);

    static StrategyProgram Program(string text)
    {
        var parse = StrategyParser.Parse(text);
        Assert.True(parse.Ok, parse.Why);
        return parse.Program!;
    }

    static BacktestRequest Request(ExecutionModel? model = null, DateTimeOffset? to = null) =>
        new(7, "sha-of-the-normalised-file", model ?? ExecutionModel.Frictionless, Start, to);

    static IReadOnlyList<BacktestEvent> Of(BacktestResult run, BacktestEventKind kind) =>
        [.. run.Trace.Of(kind)];

    /// <summary>
    /// SIXTY MINUTES THAT ARE ONE HOUR OF THE STATED SHAPE: the first opens at <paramref name="open"/>,
    /// the last closes at <paramref name="close"/>, minute 20 alone reaches <paramref name="high"/> and
    /// minute 40 alone <paramref name="low"/>, and every minute trades <paramref name="volume"/> — so the
    /// hour is that bar only if it was built from all sixty, by the rule, and no single minute looks like it.
    /// </summary>
    static IEnumerable<KlineBar> Hour(int hour, decimal open, decimal high, decimal low, decimal close,
        decimal volume)
    {
        var previous = open;
        for (var j = 0; j < 60; j++)
        {
            var c = open + (close - open) * (j + 1) / 60m;
            var h = j == 20 ? high : Math.Max(previous, c) + 0.01m;
            var l = j == 40 ? low : Math.Min(previous, c) - 0.01m;
            yield return new KlineBar(Start.AddMinutes(hour * 60 + j), previous, h, l, c, volume);
            previous = c;
        }
    }

    /// <summary>One minute at <paramref name="minute"/> past <see cref="Start"/>, from explicit prices.</summary>
    static KlineBar M(int minute, decimal open, decimal high, decimal low, decimal close) =>
        new(Start.AddMinutes(minute), open, high, low, close, 1m);

    /// <summary>Flat minutes at one price, for the stretches a fixture does not care about.</summary>
    static IEnumerable<KlineBar> Flat(int from, int count, decimal price) =>
        Enumerable.Range(from, count).Select(i => M(i, price, price, price, price));

    /// <summary>
    /// (a) A `bars 1h` PROGRAM IS BACKTESTED ON HOURLY BARS BUILT FROM CLOSED MINUTES.
    ///
    /// <para>Twelve hours of minutes. The entry needs four things of an HOUR: a close above the three-hour
    /// mean of closes, a range over 3, a volume over 150 and an open below the close. Hours 2, 3 and 4 each
    /// have three of the four — the open, then the volume, then the range is the one missing — and hour 5
    /// has all four, so the run signals once, at hour 5's close, and fills at the first minute of hour 6.
    /// No single minute could do it: a minute's volume is at most 3 and its range a few cents. And the
    /// trace has twelve bar lines, one per hour at the hour, and nothing for the 708 minutes in between but
    /// the one fill.</para>
    ///
    /// <para><b>RED through mutant (i)</b> — the resampler bypassed, every minute handed to the evaluator:
    /// on the program's hourly grid the second minute is refused as "not on the 1h grid" and the run
    /// faults; and evaluated on minutes instead, it never signals and has 720 bar lines.</para>
    /// </summary>
    [Fact]
    public void A_bars_1h_program_is_backtested_on_hourly_bars_built_from_closed_minutes()
    {
        (decimal O, decimal H, decimal L, decimal C, decimal V)[] hours =
        [
            (100m, 101m, 99m, 100.5m, 1m),
            (100.5m, 102m, 99.5m, 101m, 1m),
            (103m, 105m, 101m, 102.5m, 3m),       // all but the open below the close
            (102m, 105m, 101m, 104m, 2m),         // all but the volume: 120
            (104m, 105.5m, 103.5m, 105m, 3m),     // all but the range: 2
            (105m, 108m, 104m, 107m, 3m),         // all four
            (107m, 109m, 106m, 108m, 1m),
            (108m, 110m, 107m, 109m, 1m),
            (109m, 111m, 108m, 110m, 1m),
            (110m, 112m, 109m, 111m, 1m),
            (111m, 113m, 110m, 112m, 1m),
            (112m, 114m, 111m, 113m, 1m)
        ];
        var minutes = hours.SelectMany((h, k) => Hour(k, h.O, h.H, h.L, h.C, h.V)).ToList();
        Assert.Equal(720, minutes.Count);

        var program = Program("""
            instrument BTCUSDT
            bars 1h
            size fixed 1
            stop percent 50
            indicator m = sma(close, 3)
            entry when close > m and high - low > 3 and volume > 150 and open < close
            """);

        var run = Backtest.Run(program, Request(), minutes);
        log.WriteLine(run.Trace.Text);

        Assert.Equal(BacktestOutcome.COMPLETED, run.Outcome);
        Assert.Null(run.FaultReason);

        var closes = Of(run, BacktestEventKind.Bar);
        Assert.Equal(12, closes.Count);
        Assert.Equal(Enumerable.Range(0, 12).Select(k => Start.AddHours(k)), closes.Select(e => e.Bar));
        Assert.Equal(Enumerable.Range(1, 12).Select(k => (long)k), closes.Select(e => e.Ordinal));
        Assert.Equal(["WarmingUp", "WarmingUp", "Idle", "Idle", "Idle", "Signalled", "Idle", "Idle", "Idle", "Idle", "Idle", "Idle"],
            closes.Select(e => e.Status));
        Assert.Equal(12, run.Metrics.Bars);
        Assert.Equal(12, run.Counters.Bars);

        var signal = Assert.Single(Of(run, BacktestEventKind.Signal));
        Assert.Equal((Start.AddHours(5), 6L, 107m, "Enter"), (signal.Bar, signal.Ordinal, signal.Price!.Value, signal.Status));

        var fill = Assert.Single(Of(run, BacktestEventKind.Fill));
        Assert.Equal((Start.AddHours(6), 7L, 107m), (fill.Bar, fill.Ordinal, fill.Price!.Value));

        // The last hour's line marks the open position at the hour's close: 10,000 less the one unit bought, plus 113.
        Assert.Equal(10_000m - 107m + 113m, closes[^1].Equity);
        Assert.Equal(14, run.Trace.Events.Count);

        // And the same minutes, the same rules, on minutes: no minute has that volume or that range.
        var everyMinute = Backtest.Run(Program(program.Source.Replace("bars 1h", "bars 1m")), Request(), minutes);
        Assert.Equal(720, everyMinute.Metrics.Bars);
        Assert.Empty(Of(everyMinute, BacktestEventKind.Signal));
    }

    /// <summary>
    /// (c) A DECISION AT AN HOURLY CLOSE FILLS AT THE NEXT MINUTE'S OPEN, AND A STOP STILL FILLS ON MINUTES.
    ///
    /// <para>Two runs of one program, which enters on hour 1's close. In the first, the minute that follows
    /// is missing, so the entry fills at the next minute there is — 02:05, at its open — and at 02:17 a
    /// minute opens THROUGH the stop: the exit is at that minute's open, 98.5, at 02:17. Judged on the hour
    /// it would have been the stop price at the hour's open. In the second the target is touched at 02:10
    /// and the stop only at 02:40: the minutes say the target came first and alone, so the trade is a
    /// target at 104.03 — while the hour's range covers both levels and, read as one bar, would have been
    /// the conservative STOP.</para>
    /// </summary>
    [Fact]
    public void A_decision_at_an_hourly_close_fills_at_the_next_minute_open_and_a_stop_still_fills_on_minutes()
    {
        var program = Program("""
            instrument BTCUSDT
            bars 1h
            size fixed 1
            stop percent 2
            target percent 3
            indicator m = sma(close, 2)
            entry when close > m
            """);

        // Hour 0 closes at 100, hour 1 at 101: above their mean of 100.5, so hour 1's close is the entry
        // signal, with a stop at 101 less 2% (98.98) and a target at 101 and 3% (104.03).
        var lead = Flat(0, 60, 100m).Concat(Flat(60, 59, 100.5m)).Append(M(119, 100.5m, 101m, 100.5m, 101m)).ToList();

        var throughTheStop = lead
            .Concat(Enumerable.Range(125, 12).Select(i => M(i, 101.5m, 101.6m, 101.4m, 101.5m)))   // 02:05..02:16
            .Append(M(137, 98.5m, 98.6m, 98.0m, 98.2m))                                           // 02:17 opens through it
            .Concat(Flat(138, 42, 98.2m))
            .ToList();

        var stopped = Backtest.Run(program, Request(), throughTheStop);
        log.WriteLine(stopped.Trace.Text);

        var signal = Assert.Single(Of(stopped, BacktestEventKind.Signal));
        Assert.Equal((Start.AddHours(1), 101m), (signal.Bar, signal.Price!.Value));

        var fill = Assert.Single(Of(stopped, BacktestEventKind.Fill));
        Assert.Equal((Start.AddMinutes(125), 101.5m, 3L), (fill.Bar, fill.Price!.Value, fill.Ordinal));

        var trade = Assert.Single(stopped.Trades);
        Assert.Equal(ExitReason.Stop, trade.Reason);
        Assert.Equal(98.5m, trade.ExitPrice);
        Assert.Equal(Start.AddMinutes(125), trade.EntryBar);
        Assert.Equal(Start.AddMinutes(137), trade.ExitBar);
        Assert.Equal(3L, Assert.Single(Of(stopped, BacktestEventKind.Exit)).Ordinal);

        // The hour the entry and the stop happened in is ONE evaluated bar, marked flat at its close.
        var hour2 = Of(stopped, BacktestEventKind.Bar).Single(e => e.Bar == Start.AddHours(2));
        Assert.Equal(0m, hour2.Position);
        Assert.True(hour2.Exposed);

        var targetFirst = lead
            .Concat(Enumerable.Range(120, 10).Select(i => M(i, 101.5m, 101.6m, 101.4m, 101.5m)))   // 02:00..02:09
            .Append(M(130, 101.5m, 104.5m, 101.4m, 103m))                                         // 02:10 reaches the target
            .Concat(Enumerable.Range(131, 29).Select(i => M(i, 102m, 102.1m, 101.9m, 102m)))
            .Append(M(160, 102m, 102m, 98m, 99m))                                                 // 02:40 reaches the stop
            .Concat(Flat(161, 19, 99m))
            .ToList();

        var target = Backtest.Run(program, Request(), targetFirst);
        log.WriteLine(target.Trace.Text);

        var won = Assert.Single(target.Trades);
        Assert.Equal(ExitReason.Target, won.Reason);
        Assert.Equal(104.03m, won.ExitPrice);
        Assert.Equal(Start.AddMinutes(130), won.ExitBar);
        Assert.Equal(Start.AddMinutes(120), won.EntryBar);
        Assert.Equal(101.5m, won.EntryPrice);
    }

    /// <summary>
    /// (d) A YEAR OF HOURLY BARS BACKTESTS IN ONE RUN.
    ///
    /// <para>525,600 closed minutes — 365 days — through an hourly moving-average crossover: 8,760 bars
    /// evaluated, the run COMPLETED, trades taken. The cap counts EVALUATED bars; on minutes the same window
    /// halts at 200,000 and has to be four runs of a quarter.</para>
    ///
    /// <para><b>RED through mutant (ii)</b> — the cap counting minutes: the run halts at minute 200,001,
    /// FAULTED, about 139 days in.</para>
    /// </summary>
    [Fact]
    public void A_year_of_hourly_bars_backtests_in_one_run()
    {
        var program = Program("""
            instrument BTCUSDT
            bars 1h
            size fixed 1
            stop percent 5
            indicator fast = sma(close, 6)
            indicator slow = sma(close, 24)
            exit when crosses_below(fast, slow)
            entry when crosses_above(fast, slow)
            """);

        var run = Backtest.Run(program, Request(), Year());
        log.WriteLine($"{run.Outcome} {run.FaultReason ?? "-"}: bars {run.Metrics.Bars}, trades {run.Metrics.Trades}, "
                      + $"trace lines {run.Trace.Events.Count}, missing {run.Metrics.MissingMinutes} min");

        Assert.Equal(BacktestOutcome.COMPLETED, run.Outcome);
        Assert.Null(run.FaultReason);
        Assert.Equal(365 * 24, run.Metrics.Bars);
        Assert.Equal(365 * 24, run.Counters.Bars);
        Assert.Equal(0, run.Metrics.MissingMinutes);
        Assert.True(run.Metrics.Trades > 100, $"{run.Metrics.Trades} trades");
        Assert.Equal(Start.AddDays(365).AddHours(-1), Of(run, BacktestEventKind.Bar)[^1].Bar);

        static IEnumerable<KlineBar> Year()
        {
            var previous = 100m;
            for (var i = 0; i < 365 * 1440; i++)
            {
                var phase = i / 60 % 48;
                var close = (phase < 24 ? 100m + phase : 148m - phase) + i % 60 * 0.001m;
                yield return new KlineBar(Start.AddMinutes(i), previous, Math.Max(previous, close) + 0.05m,
                    Math.Min(previous, close) - 0.05m, close, 1m);
                previous = close;
            }
        }
    }

    /// <summary>
    /// THE CAP STILL HALTS A DECLARED-BAR RUN, AT 200,000 EVALUATED BARS AND NOT A MINUTE BEFORE — so (d)
    /// passing is the cap counting bars, never the cap gone. Five-minute bars over 1,000,005 minutes: the
    /// 200,000th bar is evaluated, and the run halts on the first minute of the next, in words.
    /// </summary>
    [Fact]
    public void A_declared_bar_run_past_the_cap_halts_at_the_cap_counted_in_its_own_bars()
    {
        var program = Program("""
            instrument BTCUSDT
            bars 5m
            size fixed 1
            stop percent 5
            entry when close > 1000
            """);

        var minutes = Enumerable.Range(0, Backtest.MaxTracedBars * 5 + 5).Select(i => M(i, 100m, 100m, 100m, 100m));
        var run = Backtest.Run(program, Request(), minutes);

        Assert.Equal(BacktestOutcome.FAULTED, run.Outcome);
        Assert.Equal(Backtest.MaxTracedBars, run.Metrics.Bars);
        Assert.Equal(
            $"this window holds more than the {Backtest.MaxTracedBars} bars of 5m one run may evaluate, so the run halted "
            + $"at the bar before {Start.AddMinutes(Backtest.MaxTracedBars * 5):O}. Ask for a shorter window with --from and --to.",
            run.FaultReason);
        var halt = Assert.Single(Of(run, BacktestEventKind.Fault));
        Assert.Equal(Backtest.MaxTracedBars + 1L, halt.Ordinal);
    }

    /// <summary>
    /// AND A MINUTE PROGRAM PAST THE CAP HALTS EXACTLY AS IT ALWAYS DID: on its 200,001st minute, with the
    /// words it always had. Green on the base, and the two clocks must keep it so.
    /// </summary>
    [Fact]
    public void A_minute_program_past_the_cap_halts_exactly_as_it_always_did()
    {
        var program = Program("""
            instrument BTCUSDT
            size fixed 1
            stop percent 5
            entry when close > 1000
            """);

        var minutes = Enumerable.Range(0, Backtest.MaxTracedBars + 1).Select(i => M(i, 100m, 100m, 100m, 100m));
        var run = Backtest.Run(program, Request(), minutes);

        Assert.Equal(BacktestOutcome.FAULTED, run.Outcome);
        Assert.Equal(Backtest.MaxTracedBars, run.Metrics.Bars);
        Assert.Equal(
            $"this window holds more than the {Backtest.MaxTracedBars} bars one run may trace (about 138 days of "
            + $"one-minute bars), so the run halted at the bar before {Start.AddMinutes(Backtest.MaxTracedBars):O}. Ask "
            + "for a shorter window with --from and --to; a year is four runs of a quarter each.",
            run.FaultReason);
    }

    /// <summary>
    /// `max_hold_bars` COUNTS DECLARED BARS. Entered on hour 1's close and filled at 02:00, a two-bar hold
    /// is taken at the close of hour 4 — the second hour after the one it filled in — at that hour's close,
    /// stamped with its last minute; counted in minutes it would have been out at 02:02.
    /// </summary>
    [Fact]
    public void Max_hold_bars_counts_declared_bars_and_is_taken_at_the_close_of_the_bar_that_reaches_it()
    {
        var program = Program("""
            instrument BTCUSDT
            bars 1h
            size fixed 1
            max_hold_bars 2
            indicator m = sma(close, 2)
            entry when close > m and close < 102
            """);

        var minutes = Flat(0, 60, 100m).Concat(Flat(60, 59, 100.5m)).Append(M(119, 100.5m, 101m, 100.5m, 101m))
            .Concat(Flat(120, 180, 103m))
            .Concat(Flat(300, 59, 103m)).Append(M(359, 103m, 104m, 103m, 104m))
            .ToList();

        var run = Backtest.Run(program, Request(), minutes);
        log.WriteLine(run.Trace.Text);

        var trade = Assert.Single(run.Trades);
        Assert.Equal(ExitReason.MaxHoldBars, trade.Reason);
        Assert.Equal(Start.AddMinutes(120), trade.EntryBar);
        Assert.Equal(Start.AddMinutes(299), trade.ExitBar);
        Assert.Equal(103m, trade.ExitPrice);
        Assert.Equal(5L, Assert.Single(Of(run, BacktestEventKind.Exit)).Ordinal);
    }

    /// <summary>
    /// THE LIMITS COUNT DECLARED BARS: the most lookback a program may have, 500 bars, is 500 HOURS on an
    /// hourly program — about 21 days of context where it used to be about eight hours. A 500-hour mean is
    /// warming up for its first 499 hours and is first asked at hour 500, 20.8 days into 21 days of minutes.
    /// </summary>
    [Fact]
    public void The_lookback_limit_counts_declared_bars_so_500_hourly_bars_reach_back_about_21_days()
    {
        var program = Program($"""
            instrument BTCUSDT
            bars 1h
            size fixed 1
            stop percent 5
            indicator slow = sma(close, {StrategyLimits.MaxLookbackBars})
            entry when close > slow and close > close[{StrategyLimits.MaxHistoryDepth}]
            """);
        Assert.Equal(StrategyLimits.MaxLookbackBars, program.WarmUpBars);

        var minutes = Enumerable.Range(0, 21 * 1440).Select(i => M(i, 100m, 102m, 100m, 100m + i % 3));
        var run = Backtest.Run(program, Request(), minutes);
        var closes = Of(run, BacktestEventKind.Bar);

        Assert.Equal(21 * 24, closes.Count);
        Assert.All(closes.Take(StrategyLimits.MaxLookbackBars - 1), e => Assert.Equal("WarmingUp", e.Status));
        var first = closes[StrategyLimits.MaxLookbackBars - 1];
        Assert.NotEqual("WarmingUp", first.Status);
        Assert.Equal(Start.AddHours(StrategyLimits.MaxLookbackBars - 1), first.Bar);
        Assert.InRange((first.Bar - Start).TotalDays, 20.7, 20.9);
    }

    /// <summary>
    /// A RUN THAT ENDS INSIDE AN HOUR CLOSES IT, AS THE PARTIAL BAR IT IS. Two and a half hours of minutes:
    /// three hourly bars, the last built from thirty minutes and counted thirty short. Its exit signal has
    /// no minute after it to fill at, and the trace says so; the position is still open at the end, and the
    /// last line marks it at the last minute's close.
    /// </summary>
    [Fact]
    public void A_run_that_ends_inside_an_hour_closes_it_as_a_partial_bar()
    {
        var program = Program("""
            instrument BTCUSDT
            bars 1h
            size fixed 1
            exit when close > 0
            entry when close > 0
            """);

        var minutes = Flat(0, 120, 100m).Concat(Flat(120, 29, 101m)).Append(M(149, 101m, 102m, 101m, 102m)).ToList();
        var run = Backtest.Run(program, Request(), minutes);
        log.WriteLine(run.Trace.Text);

        var closes = Of(run, BacktestEventKind.Bar);
        Assert.Equal([Start, Start.AddHours(1), Start.AddHours(2)], closes.Select(e => e.Bar));
        Assert.Equal(10_000m - 100m + 102m, closes[^1].Equity);

        var gap = Assert.Single(Of(run, BacktestEventKind.Gap));
        Assert.Equal((3L, Start.AddHours(2), 30L), (gap.Ordinal, gap.Bar, gap.Minutes!.Value));
        Assert.Equal(30, run.Metrics.MissingMinutes);

        Assert.Equal(["Enter", "Exit"], Of(run, BacktestEventKind.Signal).Select(e => e.Status));
        var noTrade = Assert.Single(Of(run, BacktestEventKind.NoTrade));
        Assert.Equal("the run's window ended before the next bar, so this signal had no open to fill at", noTrade.Reason);
        Assert.True(run.Metrics.PositionOpenAtEnd);
        Assert.Equal(BacktestOutcome.COMPLETED, run.Outcome);
    }

    /// <summary>
    /// `Backtest.Over` — THE DOOR `trade backtest` AND THE REFEREE'S HOLDOUT RUN BOTH COME THROUGH — RUNS A
    /// PROGRAM ON THE BAR IT DECLARES. Six hours of minutes in a dataset the ledger recorded: an hourly
    /// program is evaluated six times, its minute twin 360.
    /// </summary>
    [Fact]
    public void Backtest_over_a_dataset_runs_a_bars_1h_program_on_the_hours_it_declares()
    {
        using var db = TestEnv.NewDb();
        var set = Dataset(db, 360);
        var store = new DatasetStore(db);
        var audience = BarAudience.Pipe(CouncilRoles.Research);
        const string Text = """
            instrument BTCUSDT
            bars 1h
            size fixed 1
            stop percent 5
            exit when close < 97
            entry when close > 100
            """;

        var hourly = Backtest.Over(store, set.Id, Program(Text), ExecutionModel.Frictionless, audience);
        var minutes = Backtest.Over(store, set.Id, Program(Text.Replace("bars 1h", "bars 1m")),
            ExecutionModel.Frictionless, audience);

        Assert.True(hourly.Ok, hourly.Why);
        Assert.True(minutes.Ok, minutes.Why);
        Assert.Equal(6, hourly.Result!.Metrics.Bars);
        Assert.Equal(360, minutes.Result!.Metrics.Bars);
        Assert.NotEqual(hourly.Result.VersionId, minutes.Result.VersionId);
    }

    /// <summary>A dataset the ledger really recorded, the way <c>BacktestDeterminismTests</c> writes one.</summary>
    static DatasetRecord Dataset(Database db, int bars)
    {
        var dir = BinanceArchive.DatasetDir("BTCUSDT");
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        var raw = Path.Combine(dir, "raw", $"BTCUSDT-1m-2026-01-{Guid.NewGuid():n}.zip");
        File.WriteAllText(raw, "a stand-in for the vendor's monthly archive");

        var csv = Path.Combine(dir, $"{Guid.NewGuid():n}.csv");
        var text = new StringBuilder().Append(KlineNormaliser.Header).Append('\n');
        for (var i = 0; i < bars; i++)
        {
            var close = 96m + i % 10;
            text.Append(CultureInfo.InvariantCulture,
                $"{Start.AddMinutes(i).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{close},{close + 1m},{close - 1m},{close},1.00\n");
        }
        File.WriteAllText(csv, text.ToString());

        var record = new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 1, ["2026-01"],
            csv, DatasetStore.Sha256(csv)!, bars, Start, Start.AddMinutes(bars - 1), 0, [], false,
            0, 0, 0, Start, DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-01", "https://127.0.0.1/x.zip", DatasetStore.Sha256(raw)!,
                DatasetStore.Sha256(raw)!, new FileInfo(raw).Length, Start, KlineTimeUnit.Microseconds, raw)]);

        return record with { Id = new DatasetStore(db).Record(record) };
    }
}

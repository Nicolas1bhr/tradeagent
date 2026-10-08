using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A RECORDED RUN'S TRADES, BELOW THE WIRE (<c>U-run-trace</c>): the reader that takes the holdout, its byte bound, the tape's
/// holdout over a run's feature reads, the op as a read in every table that says so, and the measurement the two caps are
/// figures from. The wire's own claims are <c>RunTradesOverPipeTests</c> and <c>HoldoutOverPipeTests</c>.
/// </summary>
public class RunTradesTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Bar0 = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    const string Program = "instrument BTCUSDT\nsize fixed 1\nexit when close < 97\nentry when close > 103\n";

    /// <summary>
    /// A BTCUSDT DATASET OF <paramref name="bars"/> MINUTE BARS FROM <paramref name="first"/> ON DISK, as
    /// <paramref name="version"/>, closes cycling 96 → 105 — its id.
    /// </summary>
    static long Dataset(DatasetStore store, string version, DateTimeOffset first, int bars)
    {
        var dir = BinanceArchive.DatasetDir("BTCUSDT");
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        var raw = Path.Combine(dir, "raw", $"BTCUSDT-{Guid.NewGuid():n}.zip");
        File.WriteAllText(raw, "a stand-in for the vendor's monthly archive");

        var csv = Path.Combine(dir, $"{Guid.NewGuid():n}.csv");
        var text = new StringBuilder().Append(KlineNormaliser.Header).Append('\n');
        for (var i = 0; i < bars; i++)
        {
            var close = 96m + i % 10;
            text.Append(CultureInfo.InvariantCulture,
                $"{first.AddMinutes(i).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{close},{close + 1m},{close - 1m},{close},1.00\n");
        }
        File.WriteAllText(csv, text.ToString());

        return store.Record(new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, version, 12, 1, ["2025-09"],
            csv, DatasetStore.Sha256(csv)!, bars, first, first.AddMinutes(bars - 1), 0, [], false,
            0, 0, 0, first, DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-08", "https://127.0.0.1/x.zip", DatasetStore.Sha256(raw)!,
                DatasetStore.Sha256(raw)!, new FileInfo(raw).Length, first, KlineTimeUnit.Microseconds, raw)]));
    }

    /// <summary>What a trade costs a page in these tests: its own JSON, as the wire's options write it.</summary>
    static long Bytes(StrategyTradeRow trade) => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(trade, Json.Options));

    /// <summary>
    /// A RUN OF <paramref name="trades"/> CLOSED TRADES RECORDED STRAIGHT INTO THE LEDGER, over a dataset and a version it
    /// really holds — what the byte bound is read against, below any backtest.
    /// </summary>
    static (StrategyStore Store, StrategyRunRow Run, DatasetStore Datasets) Recorded(Database db, int trades)
    {
        var datasets = new DatasetStore(db);
        var set = Dataset(datasets, "v1", Bar0, 120);
        var program = StrategyParser.Parse(Program).Program!;
        var store = new StrategyStore(db);
        store.RecordVersion(new StrategyVersionRow(program.StrategyId, program.Source, program.Canonical, program.Manifest,
            StrategyStore.InterpreterBuild, ParseVerdict.Accepted, program.WarmUpBars, Bar0, CouncilRoles.Research, "attempt-rt"));

        var run = new StrategyRunRow(new string('a', 64), program.StrategyId, set, datasets.ById(set)!.NormalisedSha256,
            Bar0, Bar0.AddMinutes(119), "fees=0;slippage=0;increment=1;capital=10000", nameof(BacktestOutcome.COMPLETED), null,
            120, trades, trades / 2, trades * 2, trades * 2, 60, 0, 0, 1m, 0m, 1m, 2m, new string('b', 64), Bar0.AddHours(3),
            CouncilRoles.Research, "attempt-rt");
        store.RecordRun(run, [.. Enumerable.Range(0, trades).Select(i => new StrategyTradeRow(
            run.Id, i, Bar0.AddMinutes(i), 100m + i, Bar0.AddMinutes(i + 1), 101m + i, 1m, "Rule", 0.25m, 1m))]);
        return (store, store.RunById(run.Id)!, datasets);
    }

    /// <summary>
    /// THE PAGE IS BOUNDED BY ITS BYTE BUDGET, SAYS SO, AND CONTINUES EXACTLY. Five trades at a budget of two and a half of
    /// them: pages of two, two and one, the first two saying <c>bytes</c> and handing back their last ordinal; and a budget
    /// smaller than one trade still serves the first, because a trade is never split. A limit outside 1 to 1,000 and an
    /// ordinal below −1 are the caller's mistakes and throw — the op refuses them in words before it gets here.
    /// </summary>
    [Fact]
    public void A_page_is_bounded_by_its_byte_budget_and_says_so()
    {
        using var db = TestEnv.NewDb();
        var (store, run, datasets) = Recorded(db, 5);
        var holdout = TapeHoldout.Pipe(CouncilRoles.Operations, datasets);
        var one = Bytes(store.TradesOf(run.Id)[0]);

        var served = new List<int>();
        long after = -1;
        var pages = new List<(int Count, bool More, string? CappedBy)>();
        while (true)
        {
            var page = store.ReadTrades(run.Id, holdout, after, StrategyStore.MaxTradeRows, one * 5 / 2, Bytes);
            Assert.Null(page.Refusal);
            Assert.Equal(5, page.TradeCount);
            Assert.Equal(run, page.Run);
            pages.Add((page.Trades.Count, page.More, page.CappedBy));
            served.AddRange(page.Trades.Select(t => t.Ordinal));
            if (!page.More) break;
            after = page.Trades[^1].Ordinal;
        }

        log.WriteLine($"one trade is {one} bytes; pages: {string.Join(", ", pages)}");
        Assert.Equal(new (int, bool, string?)[] { (2, true, TapeReader.CappedByBytes), (2, true, TapeReader.CappedByBytes), (1, false, null) },
            pages);
        Assert.Equal([0, 1, 2, 3, 4], served);

        var tight = store.ReadTrades(run.Id, holdout, -1, StrategyStore.MaxTradeRows, 1, Bytes);
        Assert.Equal(0, Assert.Single(tight.Trades).Ordinal);
        Assert.True(tight.More);
        Assert.Equal(TapeReader.CappedByBytes, tight.CappedBy);

        var limited = store.ReadTrades(run.Id[..12], holdout, 1, 2, long.MaxValue, Bytes);
        Assert.Equal([2, 3], limited.Trades.Select(t => t.Ordinal));
        Assert.Equal(TapeReader.CappedByLimit, limited.CappedBy);

        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadTrades(run.Id, holdout, -1, 0, 1, _ => 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadTrades(run.Id, holdout, -1, StrategyStore.MaxTradeRows + 1, 1, _ => 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadTrades(run.Id, holdout, -2, 1, 1, _ => 1));
        Assert.Throws<ArgumentNullException>(() => store.ReadTrades(run.Id, null!, -1, 1, 1, _ => 1));
    }

    /// <summary>
    /// REFUSED RATHER THAN GUESSED: a run whose version this build cannot read back, and a run whose dataset the ledger no
    /// longer holds, are each held back in words with no row and no trade — whether a holdout window reaches them cannot be
    /// decided, so nothing of them is served. Neither happens through the app (a version parsed when it was recorded, and a
    /// run's row references its dataset's), so both are made here by hand, below it.
    /// </summary>
    [Fact]
    public void A_run_whose_version_or_dataset_cannot_be_read_back_is_refused_rather_than_guessed()
    {
        using var db = TestEnv.NewDb();
        var (store, run, datasets) = Recorded(db, 3);
        var holdout = TapeHoldout.Pipe(CouncilRoles.Operations, datasets);
        Assert.Null(store.ReadTrades(run.Id, holdout, -1, 10, long.MaxValue, Bytes).Refusal);

        // A VERSION WHOSE RECORDED SOURCE IS NOT A PROGRAM THIS BUILD PARSES.
        var unreadable = new string('c', 64);
        store.RecordVersion(new StrategyVersionRow(unreadable, "this is not a program", "", "", StrategyStore.InterpreterBuild,
            ParseVerdict.Accepted, 0, Bar0, CouncilRoles.Research, "attempt-rt"));
        var orphan = run with { Id = new string('d', 64), VersionId = unreadable };
        store.RecordRun(orphan, [new StrategyTradeRow(orphan.Id, 0, Bar0, 100m, Bar0.AddMinutes(1), 101m, 1m, "Rule", 0.25m, 1m)]);

        var version = store.ReadTrades(orphan.Id, holdout, -1, 10, long.MaxValue, Bytes);
        log.WriteLine(version.Refusal);
        Assert.True(version.Withheld);
        Assert.Null(version.Run);
        Assert.Empty(version.Trades);
        Assert.Contains($"TradeAgent cannot read back version {unreadable[..12]}, which ran it — its recorded source does not "
                        + "parse in this build", version.Refusal, StringComparison.Ordinal);

        // A DATASET THE LEDGER NO LONGER HOLDS, removed by hand under the run's own reference to it.
        db.Exec("PRAGMA foreign_keys=OFF");
        db.Exec($"DELETE FROM dataset WHERE id={run.DatasetId.ToString(CultureInfo.InvariantCulture)}");
        db.Exec("PRAGMA foreign_keys=ON");
        Assert.Null(datasets.ById(run.DatasetId));

        var gone = store.ReadTrades(run.Id, holdout, -1, 10, long.MaxValue, Bytes);
        log.WriteLine(gone.Refusal);
        Assert.True(gone.Withheld);
        Assert.Null(gone.Run);
        Assert.Empty(gone.Trades);
        Assert.Contains($"it ran over dataset {run.DatasetId}, which this installation's ledger no longer holds",
            gone.Refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// NO READER OF A RUN'S TRADES TAKES NO HOLDOUT BUT THE IN-PROCESS ONE. Every public read of <see cref="StrategyStore"/>
    /// that serves a trade takes a <see cref="TapeHoldout"/> but <see cref="StrategyStore.TradesOf"/>, named here so a reader
    /// added later fails — so a new op that wants trades says who is asking before it gets one.
    /// </summary>
    [Fact]
    public void Every_reader_of_a_runs_trades_takes_the_holdout_but_the_in_process_one()
    {
        var unguarded = typeof(StrategyStore)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.ReturnType == typeof(RunTradesPage)
                        || (m.ReturnType.IsGenericType && m.ReturnType.GetGenericArguments().Contains(typeof(StrategyTradeRow))))
            .Where(m => !m.GetParameters().Any(p => p.ParameterType == typeof(TapeHoldout)))
            .Select(m => m.Name).Distinct().ToList();
        Assert.Equal(["TradesOf"], unguarded);
    }

    /// <summary>
    /// (1c) A RUN'S TRADES ARE HELD BACK ONCE ITS FEATURE READS REACH A WINDOW SET AFTER IT. The Research Director's run of
    /// the hourly funding program over 00:00–01:59 closes at 01:00 and 02:00 and read the tape from 23:00 the day before;
    /// it was served. Then the owner holds back a dataset of the hour 23:00–00:00: its window is clear of the run's BARS —
    /// they open at 00:00, on a dataset with no cutoff of its own — and not of its READINGS. So the trades are REFUSED in
    /// the tape holdout's words, exactly as a backtest of the same window is refused today; and a program that reads no
    /// feature, over the same bars, is still served. RED with (c) removed from the reader.
    /// </summary>
    [Fact]
    public async Task A_runs_trades_are_held_back_once_its_feature_reads_reach_a_window_set_after_it()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;

        var id = Dataset(gw.Datasets, "v1", Bar0, 8 * 60);
        var file = Path.Combine(TestEnv.Home, $"tape-{Guid.NewGuid():n}.db");
        using (var store = new TapeStore(file))
            for (var h = -3; h < 8; h++)
                FeatureProgramBacktestTests.Reading(store, Bar0.AddHours(h).AddMinutes(10), "0.00010000");
        gw.Tape = new TapeReader(file);

        var folder = Path.Combine(Paths.RoleHome(CouncilRoles.Research), "strategies");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "run-trace-funding.strategy"), FeatureProgramBacktestTests.Program());
        File.WriteAllText(Path.Combine(folder, "run-trace-plain.strategy"), Program);

        var research = AgentContext.ForAgent("agent", CouncilRoles.Research, "attempt-run-trace");
        var funding = gw.Backtests.Run(research,
            new BacktestAsk("strategies/run-trace-funding.strategy", id, Bar0, Bar0.AddHours(2).AddMinutes(-1), Increment: 1m));
        var plain = gw.Backtests.Run(research,
            new BacktestAsk("strategies/run-trace-plain.strategy", id, Bar0, Bar0.AddHours(2).AddMinutes(-1), Increment: 1m));
        Assert.Equal(Bar0.AddHours(2), Assert.Single(funding.Result.Features).AsOf);

        RunTradesPage Read(string run, string? role) =>
            gw.Strategies.ReadTrades(run, TapeHoldout.Pipe(role, gw.Datasets), -1, StrategyStore.MaxTradeRows, long.MaxValue, Bytes);

        foreach (var run in new[] { funding.Result.RunId, plain.Result.RunId })
            Assert.Null(Read(run, CouncilRoles.Operations).Refusal);

        // THE OWNER HOLDS BACK THE HOUR BEFORE THE RUN'S FIRST BAR.
        var early = Dataset(gw.Datasets, "v2", Bar0.AddHours(-2), 120);
        Assert.True(gw.Datasets.SetHoldout(early, Bar0.AddHours(-1), EvaluationClass.Research).Ok);
        var window = Assert.Single(TapeHoldout.Pipe(null, gw.Datasets).Windows());
        Assert.Equal((Bar0.AddHours(-1), (DateTimeOffset?)Bar0), (window.From, window.Until));

        foreach (var role in new[] { CouncilRoles.Operations, CouncilRoles.Research, null })
        {
            var held = Read(funding.Result.RunId, role);
            log.WriteLine(held.Refusal ?? $"SERVED {held.Trades.Count}");
            Assert.True(held.Withheld, "a run's trades were served though its feature reads now reach a holdout window");
            Assert.Null(held.Run);
            Assert.Empty(held.Trades);
            Assert.StartsWith($"the trades of run {funding.Result.RunId} are held back: its version reads the feature(s) `funding`",
                held.Refusal, StringComparison.Ordinal);
            Assert.Contains("so its closes from 2026-08-01T01:00:00.000Z to 2026-08-01T02:00:00.000Z read it from "
                            + "2026-07-31T23:00:00.000Z — which the tape's holdout withholds: " + window.Words,
                held.Refusal, StringComparison.Ordinal);

            var still = Read(plain.Result.RunId, role);
            Assert.Null(still.Refusal);
            Assert.Equal(gw.Strategies.TradesOf(plain.Result.RunId), still.Trades);
        }

        // EXACTLY WHAT A BACKTEST OF THE SAME WINDOW IS REFUSED TODAY.
        var over = Backtest.Over(gw.Datasets, id, funding.Program, ExecutionModel.Declare(increment: 1m).Model!,
            BarAudience.Pipe(CouncilRoles.Research), Bar0, Bar0.AddHours(2).AddMinutes(-1), tape: gw.Tape);
        Assert.False(over.Ok);
        Assert.True(over.IsHoldout);
        Assert.Contains("would read it from 2026-07-31T23:00:00.000Z — which the tape's holdout withholds: " + window.Words,
            over.Why, StringComparison.Ordinal);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE OP IS A READ, AND EVERY TABLE THAT SAYS SO SAYS IT: not in <c>Ops.Mutating</c>, in the drain table at zero, in
    /// the schema with its three arguments and its bounds, and on the worker surface for every role. And no op writes,
    /// edits or deletes a run: the only one whose name begins with run reads.
    /// </summary>
    [Fact]
    public async Task The_op_is_a_read_in_the_drain_table_the_schema_and_the_worker_surface()
    {
        Assert.Equal("run-trades", Ops.RunTrades);
        Assert.False(Ops.IsMutating(Ops.RunTrades));
        Assert.DoesNotContain(Ops.RunTrades, Ops.Mutating);

        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), "ta-rt-" + Guid.NewGuid().ToString("n")[..12]);
        var row = server.HandlerPaths.SingleOrDefault(h => h.Handler == Ops.RunTrades);
        Assert.Equal(Ops.RunTrades, row.Handler);
        Assert.Equal(TimeSpan.Zero, row.Path);
        log.WriteLine($"{row.Handler}: {row.Path} — {row.Why}");

        var spec = Assert.Single(GatewaySchema.Ops(), o => o.Op == Ops.RunTrades);
        Assert.False(spec.Mutating);
        Assert.Equal("trade run trades --run <id> [--after <ordinal>] [--limit <n>]", spec.Cli);
        Assert.Equal(["after", "limit", "run"], spec.Args.Select(a => a.Name).Order(StringComparer.Ordinal));
        Assert.True(spec.Args.Single(a => a.Name == "run").Required);
        Assert.All(spec.Args.Where(a => a.Name != "run"), a => Assert.False(a.Required));
        foreach (var words in new[]
                 {
                     "1000 trades and 262144 bytes", "REFUSED, never clamped", "HOLDOUT_WITHHELD", "the referee's holdout run",
                     "A cutoff the account owner sets after a run holds its trades back", "no row is a record of a fill",
                     "The bar-by-bar trace is not kept; its hash is", "nothing is charged"
                 })
            Assert.Contains(words, spec.Description, StringComparison.Ordinal);

        Assert.Contains(Ops.RunTrades, GrantedWorkerTools.TradeOps);
        Assert.DoesNotContain(GatewaySchema.Ops(), o => o.Op.StartsWith("run", StringComparison.Ordinal) && o.Op != Ops.RunTrades);
    }

    /// <summary>
    /// THE PIPE SERVER READS TRADES ONLY THROUGH THE READER THAT TAKES THE HOLDOUT: its compiled body calls
    /// <see cref="StrategyStore.ReadTrades"/> and never <see cref="StrategyStore.TradesOf"/>, the in-process reader — read off
    /// every method and every compiler-made type inside it, as <c>BarHoldoutTests</c> reads its bar reads.
    /// </summary>
    [Fact]
    public void The_pipe_server_reads_trades_only_through_the_reader_that_takes_the_holdout()
    {
        var called = BarHoldoutTests.Calls(typeof(GatewayPipeServer)).Where(c => c.DeclaringType == typeof(StrategyStore))
            .Select(c => c.Name).Distinct().Order(StringComparer.Ordinal).ToList();
        log.WriteLine("strategy ledger reads the pipe server calls: " + string.Join(", ", called));
        Assert.Contains(nameof(StrategyStore.ReadTrades), called);
        Assert.DoesNotContain(nameof(StrategyStore.TradesOf), called);
    }

    /// <summary>
    /// THE MEASUREMENT THE TWO CAPS ARE FIGURES FROM. A page of 1,000 trades of the observed run's shape — attempt 3's
    /// program (<c>ef4969c5…</c>, the shipped RSI mean reversion: RSI 14 under 30 in, over 55 out, stop 2 %, target 1 %, 60
    /// bars at most, a quarter of the capital), BTCUSDT's grid of a cent and 0.00001, the venue model's 0.1 % fee and 0.02 %
    /// slippage, over a seeded random walk around 100,000 — written exactly as <c>run-trades</c> writes it
    /// (<see cref="GatewayPipeServer.RunTradeBytes"/>). Attempt 3 decided on hourly bars and closed 103 trades in 5,832 of
    /// them; this decides on 180,000 one-minute bars, under <see cref="Backtest.MaxTracedBars"/>, so that more than 1,000
    /// close in one run, because a row's bytes do not depend on the bar it was decided on: both minutes are fill minutes,
    /// and every price, size, fee and pnl comes off the same grid through the same friction. It is under the byte cap, so
    /// the row limit is what stops an ordinary page; the figure is logged.
    /// </summary>
    [Fact]
    public void A_page_of_a_thousand_trades_of_the_observed_runs_shape_is_measured_against_the_caps()
    {
        var program = StrategyParser.Parse("""
            instrument BTCUSDT
            timezone UTC
            timeframe 1m
            data_freshness 5m
            max_decision_age 5m
            const period = 14
            const oversold = 30
            const recovered = 55
            indicator momentum = rsi(close, period)
            size capital_fraction 0.25
            stop percent 2
            target percent 1
            max_hold_bars 60
            exit when momentum > recovered
            entry when momentum < oversold
            """).Program!;
        var model = ExecutionModel.Declare(0.001m, 0.0002m, 0.00001m, 10_000m).Model!;

        var random = new Random(20261008);
        var bars = new List<KlineBar>();
        var price = 100_000.00m;
        for (var i = 0; i < 180_000; i++)
        {
            var open = price;
            var z = Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
            var close = decimal.Round(open * (decimal)Math.Exp(0.002 * z), 2);
            var high = decimal.Round(Math.Max(open, close) * (1m + (decimal)(random.NextDouble() * 0.001)), 2);
            var low = decimal.Round(Math.Min(open, close) * (1m - (decimal)(random.NextDouble() * 0.001)), 2);
            bars.Add(new KlineBar(Bar0.AddMinutes(i), open, high, low, close, 12.34567m));
            price = close;
        }

        var run = Backtest.Run(program, new BacktestRequest(1, "fixture", model, bars[0].OpenTime, null), bars);
        log.WriteLine($"{run.Outcome}: {bars.Count} one-minute bars, {run.Trades.Count} closed trades {run.FaultReason}");
        Assert.Equal(BacktestOutcome.COMPLETED, run.Outcome);
        Assert.True(run.Trades.Count >= StrategyStore.MaxTradeRows, $"the walk closed only {run.Trades.Count} trades");

        var page = run.Trades.Take(StrategyStore.MaxTradeRows).Select(t => new StrategyTradeRow(
            "run", t.Ordinal, t.EntryBar, t.EntryPrice, t.ExitBar, t.ExitPrice, t.Quantity, t.Reason.ToString(), t.Fees, t.Pnl)).ToList();
        var sizes = page.Select(GatewayPipeServer.RunTradeBytes).ToList();
        var bytes = sizes.Sum() + page.Count - 1;   // and the commas between them
        log.WriteLine($"a page of {page.Count} trades is {bytes} bytes on the wire: {sizes.Min()} to {sizes.Max()} a trade, "
                      + $"{sizes.Average():F1} on average; the byte cap is {GatewayPipeServer.MaxRunTradesReplyBytes}");
        log.WriteLine("the longest, as recorded: " + Json.Write(page[sizes.IndexOf(sizes.Max())]));
        Assert.True(bytes <= GatewayPipeServer.MaxRunTradesReplyBytes,
            $"a page of {page.Count} trades of the observed shape is {bytes} bytes, over the {GatewayPipeServer.MaxRunTradesReplyBytes} cap");
    }
}

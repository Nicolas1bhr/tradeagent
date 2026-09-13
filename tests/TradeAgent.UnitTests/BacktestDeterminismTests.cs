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
/// ITEM 4 — DETERMINISTIC, AND REFUSABLE.
///
/// <para><b>What determinism is FOR.</b> A referee's whole protocol is lineage by hash
/// (`docs/COUNCIL.md`, "The runner and the referee"). If the same request can produce two different
/// traces, an id stops being an identity: two results recorded against one run id would be results
/// about two different things, and a re-run could quietly replace last month's figure with this
/// month's. So the run id is a hash of everything that decided the answer and of nothing else, and no
/// clock is read inside a run — the trace carries no instant that is not a bar's own open time, which
/// is asserted here rather than asserted about.</para>
///
/// <para><b>And a dataset nobody can vouch for serves nothing.</b> A REJECTED row means a file this
/// ledger measured has changed on disk; a run over it would be a result about bytes that are not
/// there. The sha of the bytes a run DID feed on is carried onto the run row, which is what makes a
/// rejection discovered next month traceable to every figure it poisoned.</para>
/// </summary>
public class BacktestDeterminismTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Start = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    const string Text = """
        instrument BTCUSDT
        size fixed 1
        stop percent 5
        exit when close < 95
        entry when close > 100
        """;

    static StrategyProgram Program(string text = Text)
    {
        var parse = StrategyParser.Parse(text);
        Assert.True(parse.Ok, parse.Why);
        return parse.Program!;
    }

    /// <summary>
    /// THE AUDIENCE EVERY RUN IN THIS CLASS IS FOR: a caller on the agent pipe, which is the one that
    /// can never read a holdout bar. None of these datasets has a cutoff, so nothing here is refused by
    /// it — the point is that the strongest audience is what the determinism is measured under.
    /// </summary>
    static readonly BarAudience Research = BarAudience.Pipe(CouncilRoles.Research);

    static ExecutionModel Model(decimal? fees = null, decimal? slippage = null)
    {
        var declared = ExecutionModel.Declare(fees, slippage);
        Assert.True(declared.Ok, declared.Why);
        return declared.Model!;
    }

    /// <summary>
    /// A dataset the ledger really recorded: a normalised file with <paramref name="bars"/> minutes in
    /// it and a stand-in for the vendor's archive, both hashed, exactly as the app's own collector
    /// leaves them. <see cref="DatasetStore.Checked"/> re-reads those hashes, so the verification has
    /// something true to check.
    /// </summary>
    static DatasetRecord Given(Database db, int bars, string pair = "BTCUSDT")
    {
        var dir = BinanceArchive.DatasetDir(pair);
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        // A UNIQUE NAME PER CALL. Every class in this assembly shares one TRADEAGENT_HOME and xUnit
        // runs classes in parallel, so a fixed file name here is a sharing violation waiting for a
        // slow morning.
        var raw = Path.Combine(dir, "raw", $"{pair}-1m-2026-08-{Guid.NewGuid():n}.zip");
        File.WriteAllText(raw, "a stand-in for the vendor's monthly archive");

        var csv = Path.Combine(dir, $"{Guid.NewGuid():n}.csv");
        var text = new StringBuilder().Append(KlineNormaliser.Header).Append('\n');
        for (var i = 0; i < bars; i++)
        {
            // A sawtooth: it crosses 100 upwards every ten minutes, so a short window still trades.
            var close = 96m + i % 10;
            text.Append(CultureInfo.InvariantCulture,
                $"{Start.AddMinutes(i).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{close},{close + 1m},{close - 1m},{close},1.00\n");
        }
        File.WriteAllText(csv, text.ToString());

        var record = new DatasetRecord(
            0, BinanceArchive.Source, pair, BinanceArchive.Interval, "v1", 12, 1, ["2025-09"],
            csv, DatasetStore.Sha256(csv)!, bars, Start, Start.AddMinutes(bars - 1), 0, [], false,
            0, 0, 0, Start, DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-08", "https://127.0.0.1/x.zip", DatasetStore.Sha256(raw)!,
                DatasetStore.Sha256(raw)!, new FileInfo(raw).Length, Start, KlineTimeUnit.Microseconds, raw)]);

        return record with { Id = new DatasetStore(db).Record(record) };
    }

    /// <summary>
    /// THE SAME REQUEST TWICE IS A BYTE-IDENTICAL TRACE AND ONE RUN ID.
    ///
    /// <para>Compared as BYTES, not as a summary: two traces that agree on every metric and differ on
    /// which bar a fill landed on are two different runs, and a hash is the only comparison that
    /// notices.</para>
    /// </summary>
    [Fact]
    public void Two_runs_of_one_request_produce_a_byte_identical_trace_and_the_same_run_id()
    {
        using var db = TestEnv.NewDb();
        var set = Given(db, 120);
        var store = new DatasetStore(db);
        var program = Program();

        var first = Backtest.Over(store, set.Id, program, Model(fees: 0.001m), Research);
        var again = Backtest.Over(store, set.Id, program, Model(fees: 0.001m), Research);

        Assert.True(first.Ok, first.Why);
        Assert.True(again.Ok, again.Why);
        log.WriteLine($"{first.Result!.RunId} / {first.Result.Trace.Sha256}");

        Assert.Equal(first.Result.Trace.Text, again.Result!.Trace.Text);
        Assert.Equal(first.Result.Trace.Sha256, again.Result.Trace.Sha256);
        Assert.Equal(first.Result.RunId, again.Result.RunId);
        Assert.NotEmpty(first.Result.Trades);                  // the fixture really traded
    }

    /// <summary>
    /// NO CLOCK IS READ INSIDE A RUN, and the trace is where that is checkable.
    ///
    /// <para>Every timestamp in the trace is one of the bars' own open times. A run that stamped its
    /// events with "now" — or that recorded a duration, or a created-at — would produce a different
    /// trace every time it ran, and the byte comparison above would be comparing two runs a
    /// microsecond apart rather than two runs of one request.</para>
    /// </summary>
    [Fact]
    public void Every_instant_in_a_trace_is_a_bars_own_open_time_and_never_a_clock_reading()
    {
        using var db = TestEnv.NewDb();
        var set = Given(db, 60);
        var run = Backtest.Over(new DatasetStore(db), set.Id, Program(), Model(fees: 0.001m), Research);
        Assert.True(run.Ok, run.Why);

        var barTimes = Enumerable.Range(0, 60)
            .Select(i => Start.AddMinutes(i).UtcDateTime.ToString("O", CultureInfo.InvariantCulture))
            .ToHashSet(StringComparer.Ordinal);

        var lines = run.Result!.Trace.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
        foreach (var line in lines)
            Assert.Contains(line.Split('|')[1], barTimes);
    }

    /// <summary>
    /// THE RUN ID IS A HASH OF EVERY INPUT THAT DECIDED THE ANSWER, AND THE COMPOSITION IS PINNED.
    ///
    /// <para>The golden figure was computed OUTSIDE this build, in Python, over the exact string the
    /// composition produces — <c>version id \n dataset id \n dataset sha256 \n window \n model</c> —
    /// so a change to the order, the separator or the fields is caught here and not by a reader
    /// wondering why last month's id no longer reproduces.</para>
    ///
    /// <para>The dataset's SHA-256 is in there as well as its id, which is the mutant this test was
    /// watched against: with the sha left out, a run over the bytes the ledger measured and a run over
    /// bytes that replaced them collide on one id, and one of the two results silently stands for
    /// both.</para>
    /// </summary>
    [Fact]
    public void The_run_id_is_pinned_to_a_hash_computed_outside_this_build()
    {
        var window = new BacktestRequest(7, "abc123", Model(fees: 0.001m),
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal("2026-08-01T00:00:00.0000000Z..2026-08-02T00:00:00.0000000Z", window.Window);
        Assert.Equal("9eff99d7d6e9c95b3bf513f496ced6e193e4db52023add1386e68301769cee7d",
            window.RunIdFor("version-x"));

        // An open-ended window says `all` on the side that is open, and is its own run.
        var everything = window with { From = null, To = null };
        Assert.Equal("all..all", everything.Window);
        Assert.Equal("70fdabd4374362031062ad3c69888e55dbb981b15fd8d8d09533bc1c195fa42b",
            everything.RunIdFor("version-x"));
    }

    /// <summary>
    /// CHANGE ANY ONE OF THE FIVE AND IT IS A DIFFERENT RUN.
    ///
    /// <para>The fee is the one the brief names, and it is the one that would hurt most: the same
    /// program over the same bars under a fee of nothing and under a real fee are two results, and an
    /// id that could not tell them apart would let the frictionless one stand in for the other.</para>
    /// </summary>
    [Fact]
    public void A_changed_fee_window_dataset_or_program_is_a_different_run_id()
    {
        var baseline = new BacktestRequest(7, "abc123", Model(), Start, Start.AddDays(1));
        var id = baseline.RunIdFor("version-x");

        Assert.NotEqual(id, (baseline with { Model = Model(fees: 0.001m) }).RunIdFor("version-x"));
        Assert.NotEqual(id, (baseline with { Model = Model(slippage: 0.0005m) }).RunIdFor("version-x"));
        Assert.NotEqual(id, (baseline with { DatasetId = 8 }).RunIdFor("version-x"));
        Assert.NotEqual(id, (baseline with { DatasetSha256 = "def456" }).RunIdFor("version-x"));
        Assert.NotEqual(id, (baseline with { From = Start.AddMinutes(1) }).RunIdFor("version-x"));
        Assert.NotEqual(id, (baseline with { To = null }).RunIdFor("version-x"));
        Assert.NotEqual(id, baseline.RunIdFor("version-y"));

        // And the same five reach the same id again, which is the other half of the claim.
        Assert.Equal(id, new BacktestRequest(7, "abc123", Model(), Start, Start.AddDays(1))
            .RunIdFor("version-x"));
    }

    /// <summary>
    /// A REJECTED DATASET SERVES NO RUN, AND THE REFUSAL NAMES IT.
    ///
    /// <para>The raw archive is changed on disk after the ledger measured it, which is exactly the case
    /// the ledger exists to catch. The dataset is not re-normalised from the new bytes and no figure is
    /// computed over them.</para>
    /// </summary>
    [Fact]
    public void A_rejected_dataset_is_refused_and_no_run_comes_out_of_it()
    {
        using var db = TestEnv.NewDb();
        var set = Given(db, 60);
        var store = new DatasetStore(db);

        // It runs while the bytes are what the ledger says they are.
        Assert.True(Backtest.Over(store, set.Id, Program(), Model(), Research).Ok);

        File.WriteAllText(set.Files[0].Path, "somebody replaced the vendor's archive");

        var refused = Backtest.Over(store, set.Id, Program(), Model(), Research);
        Assert.False(refused.Ok);
        Assert.Null(refused.Result);
        Assert.Contains("REJECTED", refused.Why);
        Assert.Contains("no longer matches the hash", refused.Why);

        // A dataset id this installation does not have is refused the same way.
        Assert.Contains("no dataset 9999", Backtest.Over(store, 9999, Program(), Model(), Research).Why);
    }

    /// <summary>
    /// THE SHA THE RUN FED ON IS ON THE RUN ROW, SO A LATER REJECTION CAN BE TRACED TO IT.
    ///
    /// <para>The run is recorded; then the bytes change and the dataset becomes REJECTED. The question
    /// that matters afterwards is which results were computed over those bytes, and the answer is a
    /// query rather than an archaeology: the row still carries the hash of what it actually read.</para>
    /// </summary>
    [Fact]
    public void The_sha_a_run_fed_on_is_recorded_and_a_later_rejection_can_be_traced_to_it()
    {
        using var db = TestEnv.NewDb();
        var set = Given(db, 60);
        var datasets = new DatasetStore(db);
        var strategies = new StrategyStore(db);
        var program = Program();

        var run = Backtest.Over(datasets, set.Id, program, Model(fees: 0.001m), Research);
        Assert.True(run.Ok, run.Why);
        var result = run.Result!;

        strategies.RecordVersion(new StrategyVersionRow(
            program.StrategyId, program.Source, program.Canonical, program.Manifest,
            StrategyStore.InterpreterBuild, ParseVerdict.Accepted, program.WarmUpBars, Start, null, null));
        strategies.RecordRun(new StrategyRunRow(
            result.RunId, result.VersionId, set.Id, result.Request.DatasetSha256, null, null,
            result.Request.Model.Canonical, result.Outcome.ToString(), result.FaultReason,
            result.Metrics.Bars, result.Metrics.Trades, result.Metrics.Wins, result.Metrics.Signals,
            result.Metrics.Fills, result.Metrics.ExposureBars, result.Metrics.MissingMinutes,
            result.Metrics.Faults, result.Metrics.GrossPnl, result.Metrics.Fees, result.Metrics.NetPnl,
            result.Metrics.MaxDrawdown, result.Trace.Sha256, Start, null, null), []);

        // Now the bytes change under it and the ledger rejects the row.
        File.WriteAllText(set.NormalisedPath, "not the file that was measured");
        Assert.Equal(DatasetState.REJECTED, datasets.Checked(datasets.ById(set.Id)!).State);

        var row = Assert.Single(strategies.RunsOfDataset(set.Id));
        Assert.Equal(set.NormalisedSha256, row.DatasetSha256);      // what it really read
        Assert.NotEqual(DatasetStore.Sha256(set.NormalisedPath), row.DatasetSha256);
        Assert.Equal(result.Trace.Sha256, row.TraceSha256);
    }
}

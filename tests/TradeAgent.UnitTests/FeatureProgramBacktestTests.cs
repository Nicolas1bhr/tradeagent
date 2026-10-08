using System.Globalization;
using System.Text;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Features;
using TradeAgent.Core.Strategy;
using TradeAgent.AgentRuntime;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A FEATURE AS IT HAD ARRIVED (<c>U-language-v2a</c> item 3): the backtest hands the evaluator each feature's value at
/// the evaluated bar's close, read from a real tape file under the run's own audience, absent meaning no decision; the
/// values read reach the run id, and the trace ends with what each feature came to. No network: every reading is written
/// here through <see cref="TapeStore"/>, and the built-in address is never asked anything.
/// </summary>
public class FeatureProgramBacktestTests(ITestOutputHelper log) : IDisposable
{
    /// <summary>
    /// THE RESEARCH DIRECTOR'S TAPE HOLDOUT OVER A LEDGER HOLDING NO CUTOFF (<c>U-tape-holdout</c>): no window, so every
    /// read below is served as it was before the tape had a holdout. Its own database per test, disposed with it.
    /// </summary>
    readonly Database _ledger = TestEnv.NewDb();

    TapeHoldout ResearchTape => TapeHoldout.Pipe(CouncilRoles.Research, new DatasetStore(_ledger));

    public void Dispose() => _ledger.Dispose();

    /// <summary>The premium index's own address on this build's row: a reading recorded from it on time is O-LIVE.</summary>
    static string BuiltInUrl => TapeSourceCatalog.BinanceUmBaseUrl + "/fapi/v1/premiumIndex";

    public static readonly DateTimeOffset Start = new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);

    static string NewFile() => Path.Combine(TestEnv.Home, $"tape-{Guid.NewGuid():n}.db");

    static TapeFetch Fetch(DateTimeOffset receivedAt) => new()
    {
        Source = TapeSourceCatalog.Premium,
        Series = "premium-index",
        Url = BuiltInUrl,
        RequestedAt = receivedAt.AddMilliseconds(-300),
        ReceivedAt = receivedAt,
        HttpStatus = 200
    };

    /// <summary>One premium-index reading of BTCUSDT in the vendor's shape, stamped and received when the test says.</summary>
    internal static void Reading(TapeStore store, DateTimeOffset stamped, string funding, DateTimeOffset? received = null) =>
        store.Append(Fetch(received ?? stamped.AddSeconds(2)),
        [
            new TapeItem("BTCUSDT", stamped,
                $$"""{"symbol":"BTCUSDT","markPrice":"85000.00000000","lastFundingRate":"{{funding}}","time":{{stamped.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}}}""")
        ]);

    /// <summary>The brief's feature with a two-hour max age, so an hourly program sees a reading of the hour before.</summary>
    public const string Spec =
        """{"kind":"latest","input":{"source":"binance-um-premium","series":"premium-index","subject":"BTCUSDT","field":"lastFundingRate"},"latency_s":5,"max_age_s":7200}""";

    /// <summary>Buys when funding is deeply negative, sells when it turns positive — the brief's observable program, hourly.</summary>
    public static string Program(string extra = "", string bars = "1h") => $"""
        instrument BTCUSDT
        bars {bars}
        timeframe 1h
        data_freshness 2h
        max_decision_age 10m
        feature funding = {Spec}
        size fixed 1
        stop percent 50
        {extra}
        exit when funding > 0
        entry when funding < -0.0003
        """;

    public static StrategyProgram Parsed(string text)
    {
        var parse = StrategyParser.Parse(text);
        Assert.True(parse.Ok, parse.Why);
        return parse.Program!;
    }

    /// <summary><paramref name="minutes"/> closed one-minute bars from <paramref name="from"/>, every price 100.</summary>
    public static List<KlineBar> Minutes(DateTimeOffset from, int minutes) =>
        [.. Enumerable.Range(0, minutes).Select(i => new KlineBar(from.AddMinutes(i), 100m, 100.5m, 99.5m, 100m, 1m))];

    static readonly ExecutionModel Model = ExecutionModel.Declare(0m, 0m, 0.001m, 10_000m).Model!;

    /// <summary>A run of <paramref name="program"/> over <paramref name="bars"/> with its features read from <paramref name="file"/>.</summary>
    BacktestResult Run(StrategyProgram program, IReadOnlyList<KlineBar> bars, string file, string sha = "fixture")
    {
        var grid = BarGrid.For(program);
        using var feed = new FeatureFeed(new TapeReader(file), ResearchTape, program, grid.EndOf(grid.StartOf(bars[^1].OpenTime)));
        var run = Backtest.Run(program, new BacktestRequest(1, sha, Model, bars[0].OpenTime, null), bars, features: feed);
        foreach (var line in run.Trace.Events.Where(e => e.Kind is not BacktestEventKind.Bar || e.Status != "Idle"))
            log.WriteLine(line.Line);
        return run;
    }

    static List<(DateTimeOffset Bar, string Kind)> Signals(BacktestResult run) =>
        [.. run.Trace.Of(BacktestEventKind.Signal).Select(e => (e.Bar, e.Status!))];

    /// <summary>
    /// (f) A BACKTEST READS A FEATURE ONLY AS IT HAD ARRIVED. Funding is a mild 0.0001 at ten past each hour; a spike to
    /// −0.01 is stamped 03:59:58 — before the 04:00 close — and first seen at 04:00:20, after it. At the 04:00 close the
    /// spike had not arrived, so the hour opening 03:00 decides nothing; at the 05:00 close it had, and is the newest
    /// reading, so the hour opening 04:00 enters, and fills at 05:00. The value at each close is the one at THAT close:
    /// read at the next one, the spike would have moved the 03:00 hour's decision. (The exit waits a bar more than the
    /// funding does: an intent is held over the next event, as it always was.)
    ///
    /// <para><b>The mutant</b> — <c>Backtest.Run</c> asking the feed for the NEXT declared close — goes red here.</para>
    /// </summary>
    [Fact]
    public void A_backtest_reads_a_feature_only_as_it_had_arrived()
    {
        var file = NewFile();
        using (var store = new TapeStore(file))
        {
            for (var h = 0; h < 4; h++) Reading(store, Start.AddHours(h).AddMinutes(10), "0.00010000");
            Reading(store, Start.AddHours(4).AddSeconds(-2), "-0.01000000", received: Start.AddHours(4).AddSeconds(20));
            Reading(store, Start.AddHours(5).AddMinutes(30), "0.00010000");
        }

        var run = Run(Parsed(Program()), Minutes(Start, 7 * 60), file);

        Assert.Equal(BacktestOutcome.COMPLETED, run.Outcome);
        Assert.Equal([(Start.AddHours(4), "Enter"), (Start.AddHours(6), "Exit")], Signals(run));
        var atFour = run.Trace.Of(BacktestEventKind.Bar).Single(e => e.Bar == Start.AddHours(3));
        Assert.Equal(EvaluationStatus.Idle.ToString(), atFour.Status);
        Assert.Equal(Start.AddHours(5), Assert.Single(run.Trace.Of(BacktestEventKind.Fill)).Bar);
    }

    /// <summary>
    /// (g) AN ABSENT FEATURE IS NO DECISION. Funding is deeply negative at 00:10, 01:10 and 02:10 and then the tape is
    /// silent until 06:10: with a one-hour max age the value is absent at the 04:00, 05:00 and 06:00 closes. The program
    /// enters at the first close, and while the feature is absent nothing is decided — those three bars are UNDEFINED,
    /// counted, never read as zero or as the last value — yet the maximum hold, which is protection, still closes the
    /// position at the close that reaches it. The trace's <c>Feature</c> line counts the three bars with no value, and the
    /// evaluator, stepped directly with an absent value, still answers a session exit that is due: scheduled exits come
    /// before the rules.
    /// </summary>
    [Fact]
    public void An_absent_feature_is_no_decision()
    {
        var file = NewFile();
        using (var store = new TapeStore(file))
        {
            for (var h = 0; h < 3; h++) Reading(store, Start.AddHours(h).AddMinutes(10), "-0.01000000");
            Reading(store, Start.AddHours(6).AddMinutes(10), "-0.00010000");
        }

        var program = Parsed(Program("max_hold_bars 2").Replace("\"max_age_s\":7200", "\"max_age_s\":3600", StringComparison.Ordinal));
        var run = Run(program, Minutes(Start, 7 * 60), file);

        Assert.Equal(BacktestOutcome.COMPLETED, run.Outcome);
        Assert.Equal([(Start, "Enter")], Signals(run));
        var trade = Assert.Single(run.Trades);
        Assert.Equal(ExitReason.MaxHoldBars, trade.Reason);

        var undefined = run.Trace.Of(BacktestEventKind.Bar).Where(e => e.Status == EvaluationStatus.Undefined.ToString())
            .Select(e => e.Bar).ToList();
        Assert.Equal([Start.AddHours(3), Start.AddHours(4), Start.AddHours(5)], undefined);
        Assert.Equal(3, run.Counters.UndefinedEvents);

        var summary = Assert.Single(run.Features);
        Assert.Equal(3, summary.BarsAbsent);
        var line = Assert.Single(run.Trace.Of(BacktestEventKind.Feature));
        Assert.Same(run.Trace.Events[^1], line);
        Assert.Contains("funding: ", line.Reason, StringComparison.Ordinal);
        Assert.Contains("; 3 bar(s) with no value; worst class ", line.Reason, StringComparison.Ordinal);

        // THE EVALUATOR ITSELF: long, the feature absent, the session exit due — the scheduled exit is answered.
        var direct = Parsed(Program("session_exit 03:00"));
        var state = EvaluationState.Start(direct, null, direct.Bars);
        var account = new AccountReading(10_000m, 10_100m, PositionSide.Long, 1m, 100m, false, 1);
        var bar = new KlineBar(Start.AddHours(3), 100m, 100.5m, 99.5m, 100m, 1m) { Minutes = 60 };
        var absent = new FeatureValue(Start.AddHours(4), null, "nothing fresh", null, Sha256Hex.Of(""), 0);
        var outcome = StrategyEvaluator.Step(state, bar, account, [absent]);
        Assert.Equal(EvaluationStatus.Signalled, outcome.Status);
        Assert.Equal(IntentCause.SessionExit, outcome.Intent!.Cause);
    }

    /// <summary>
    /// (h) A DAILY BAR READS ITS CLOSE ACROSS DST. A New York program on <c>bars 1d</c>: the local day of Sunday 8 March
    /// 2026 is 23 hours long, so its bar closes at local midnight — 04:00Z on 9 March, not the 05:00Z a 24-hour day
    /// would reach and not UTC midnight. Funding is −0.01 at 03:30Z and +0.01 at 04:30Z: read at the true close, the
    /// 8 March bar enters; read an hour late it would not, and read at UTC midnight it would have nothing. The read grid
    /// is hourly, and the close is one of its points.
    /// </summary>
    [Fact]
    public void A_daily_bar_reads_its_close_across_DST()
    {
        var file = NewFile();
        var springDay = new DateTimeOffset(2026, 3, 8, 5, 0, 0, TimeSpan.Zero);       // 8 March, 00:00 EST
        var springClose = new DateTimeOffset(2026, 3, 9, 4, 0, 0, TimeSpan.Zero);     // 9 March, 00:00 EDT
        using (var store = new TapeStore(file))
        {
            Reading(store, springClose.AddMinutes(-30), "-0.01000000");
            Reading(store, springClose.AddMinutes(30), "0.01000000");
        }

        var program = Parsed(Program(bars: "1d").Replace("timeframe 1h", "timezone America/New_York\ntimeframe 1d", StringComparison.Ordinal)
            .Replace("data_freshness 2h", "data_freshness 1d", StringComparison.Ordinal));
        var grid = BarGrid.For(program);
        Assert.Equal(springClose, grid.EndOf(springDay));
        Assert.Equal(23 * 60, grid.MinutesIn(springDay));

        var from = springDay.AddDays(-1);
        var run = Run(program, Minutes(from, (int)(springClose.AddDays(1) - from).TotalMinutes), file);

        Assert.Equal(BacktestOutcome.COMPLETED, run.Outcome);
        Assert.Equal([(springDay, "Enter")], Signals(run));
        Assert.Equal(springClose, run.Trace.Of(BacktestEventKind.Fill).Single().Bar);
        Assert.Equal(TimeSpan.FromHours(1),
            new FeatureFeed(new TapeReader(file), TapeHoldout.Pipe(null, new DatasetStore(_ledger)), program).Step);
    }

    /// <summary>
    /// (j) TWO TAPES, TWO RUN IDS — AND EVERY V1 RUN ID UNCHANGED. The same program over the same bars, window and model
    /// is the same run over the same tape — the same id, the same trace — and another run over a tape holding one more
    /// reading, though no decision moved. The values read are a sixth line of the run id's text; a program
    /// that reads none hashes the five it always did, pinned here as a literal computed on <c>main</c> before this unit.
    /// </summary>
    [Fact]
    public void Two_tapes_two_run_ids_and_v1_run_ids_unchanged()
    {
        string Tape(bool another)
        {
            var file = NewFile();
            using var store = new TapeStore(file);
            for (var h = 0; h < 4; h++) Reading(store, Start.AddHours(h).AddMinutes(10), "0.00010000");
            if (another) Reading(store, Start.AddHours(2).AddMinutes(30), "0.00020000");
            Reading(store, Start.AddHours(4).AddSeconds(-2), "-0.01000000", received: Start.AddHours(4).AddSeconds(20));
            return file;
        }

        var program = Parsed(Program());
        var bars = Minutes(Start, 6 * 60);
        var a = Tape(false);
        var b = Tape(true);

        var first = Run(program, bars, a);
        var again = Run(program, bars, a);
        var other = Run(program, bars, b);

        Assert.Equal(first.RunId, again.RunId);
        Assert.Equal(first.Trace.Sha256, again.Trace.Sha256);
        Assert.Equal(first.Request.FeaturesSha256, again.Request.FeaturesSha256);
        Assert.Equal(Signals(first), Signals(other));
        Assert.NotEqual(first.Request.FeaturesSha256, other.Request.FeaturesSha256);
        Assert.NotEqual(first.RunId, other.RunId);

        var five = new BacktestRequest(1, "fixture", Model, Start, null);
        Assert.Equal(Sha256Hex.Of(string.Create(CultureInfo.InvariantCulture,
                $"{program.StrategyId}\n1\nfixture\n{five.Window}\n{Model.Canonical}\n{first.Request.FeaturesSha256}")),
            first.RunId);

        // THE V1 RUN ID, PINNED: a request with no feature digest hashes exactly the five lines it hashed on main.
        var v1 = Parsed(DayOnePrograms.Text("ma-crossover.strategy"));
        Assert.Null(five.FeaturesSha256);
        Assert.Equal(Sha256Hex.Of(string.Create(CultureInfo.InvariantCulture,
            $"{v1.StrategyId}\n1\nfixture\n{five.Window}\n{Model.Canonical}")), five.RunIdFor(v1.StrategyId));
        Assert.Equal(PinnedV1RunId, five.RunIdFor(v1.StrategyId));
        var v1Run = Backtest.Run(v1, five, Minutes(Start, 120));
        Assert.Null(v1Run.Request.FeaturesSha256);
        Assert.Empty(v1Run.Features);
        Assert.Empty(v1Run.Trace.Of(BacktestEventKind.Feature));
        Assert.Equal(PinnedV1RunId, v1Run.RunId);
    }

    // ---- through the gateway and the referee ---------------------------------------------------------------

    static readonly DateTimeOffset Bar0 = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset At = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    sealed record World(TradingGateway Gw, Database Db, DatasetRecord Set, CampaignRow Campaign, string Text);

    /// <summary>
    /// A BTCUSDT DATASET OF <paramref name="bars"/> MINUTE BARS FROM <paramref name="first"/> ON DISK, as
    /// <paramref name="version"/>, every price rising a point an hour — its id.
    /// </summary>
    static long Dataset(TradingGateway gw, string version, DateTimeOffset first, int bars)
    {
        var dir = BinanceArchive.DatasetDir("BTCUSDT");
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        var raw = Path.Combine(dir, "raw", $"BTCUSDT-{Guid.NewGuid():n}.zip");
        File.WriteAllText(raw, "a stand-in for the vendor's monthly archive");

        var csv = Path.Combine(dir, $"{Guid.NewGuid():n}.csv");
        var text = new StringBuilder().Append(KlineNormaliser.Header).Append('\n');
        for (var i = 0; i < bars; i++)
        {
            var close = 100m + i / 60;
            text.Append(CultureInfo.InvariantCulture,
                $"{first.AddMinutes(i).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{close},{close + 1m},{close - 1m},{close},1.00\n");
        }
        File.WriteAllText(csv, text.ToString());

        return gw.Datasets.Record(new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, version, 12, 1, ["2025-09"],
            csv, DatasetStore.Sha256(csv)!, bars, first, first.AddMinutes(bars - 1), 0, [], false,
            0, 0, 0, first, DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-08", "https://127.0.0.1/x.zip", DatasetStore.Sha256(raw)!,
                DatasetStore.Sha256(raw)!, new FileInfo(raw).Length, first, KlineTimeUnit.Microseconds, raw)]));
    }

    /// <summary>
    /// A GATEWAY WITH EIGHT HOURS OF MINUTE BARS ON DISK, ITS LAST FIVE HELD OUT, A CAMPAIGN, AND THE PROGRAM IN THE
    /// RESEARCH DIRECTOR'S OWN FOLDER — and, when <paramref name="tape"/> says so, a tape: funding mild before the cutoff,
    /// deeply negative at 03:10 and positive at 05:10, so a run over the held-back hours enters and leaves. The dataset's
    /// cutoff holds the tape back over the same five hours (<c>U-tape-holdout</c>).
    /// </summary>
    static async Task<World> Given(bool tape)
    {
        var (gw, _, db) = await TestEnv.Ready(s =>
        {
            s.CampaignTrialBudget = 5;
            s.CampaignVerdictBudget = 2;
        });

        var id = Dataset(gw, "v1", Bar0, 8 * 60);
        var (held, campaign) = gw.SetHoldout(id, Bar0.AddHours(3), EvaluationClass.Research);
        Assert.True(held.Ok, held.Why);

        if (tape)
        {
            var file = NewFile();
            using (var store = new TapeStore(file))
            {
                for (var h = -1; h < 3; h++) Reading(store, Bar0.AddHours(h).AddMinutes(10), "0.00010000");
                Reading(store, Bar0.AddHours(3).AddMinutes(10), "-0.01000000");
                Reading(store, Bar0.AddHours(5).AddMinutes(10), "0.00010000");
                Reading(store, Bar0.AddHours(7).AddMinutes(10), "0.00010000");
            }
            gw.Tape = new TapeReader(file);
        }

        var program = Program();
        var folder = Path.Combine(Paths.RoleHome(CouncilRoles.Research), "strategies");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "funding.strategy"), program);
        return new World(gw, db, gw.Datasets.ById(id)!, campaign!, program);
    }

    /// <summary>
    /// The Research Director's <c>backtest</c> of the program over <paramref name="dataset"/> (the world's own by default)
    /// from <see cref="Bar0"/> or <paramref name="from"/> to <paramref name="to"/> — by default the minute before the
    /// hour that closes at the cutoff, so its last hourly bar closes an hour before the cutoff and its features' reads
    /// stay outside the tape's holdout window.
    /// </summary>
    static BacktestRan Research(World w, DateTimeOffset? to = null, long? dataset = null, DateTimeOffset? from = null) =>
        w.Gw.Backtests.Run(
            AgentContext.ForAgent("agent", CouncilRoles.Research, "attempt-1"),
            new BacktestAsk("strategies/funding.strategy", dataset ?? w.Set.Id, from ?? Bar0, to ?? Bar0.AddHours(2).AddMinutes(-1),
                Increment: 1m));

    /// <summary>
    /// (i) A RUN READS FEATURES UNDER ITS BAR AUDIENCE. A <c>backtest</c> over the pipe reads them as the Research
    /// Director's launch — the audience its bars are read as, which may never read a holdout, on the tape's holdout for
    /// it — and the referee's verdict reads them under its own, over the held-back hours: the program enters and leaves
    /// there on the funding recorded in them, so the holdout run it records closed a trade. One audience reads a run's
    /// bars and its features, so a holdout decided for one is decided for the other — the tape's window included, which
    /// is why the backtest ends an hour before the cutoff (<see cref="A_backtest_whose_features_reach_a_holdout_window_is_refused"/>).
    /// </summary>
    [Fact]
    public async Task A_run_reads_features_under_its_bar_audience()
    {
        var w = await Given(tape: true);
        using var _1 = w.Db;

        var ran = Research(w);
        Assert.Equal(BarAudience.Pipe(CouncilRoles.Research).Who, ran.Result.FeaturesReadAs);
        Assert.False(BarAudience.Pipe(CouncilRoles.Research).MayReadHoldout);
        var summary = Assert.Single(ran.Result.Features);
        Assert.Equal(0, summary.BarsAbsent);
        Assert.Equal(BacktestEventKind.Feature, ran.Result.Trace.Events[^1].Kind);
        Assert.Equal(0, ran.Result.Metrics.Trades);

        var referee = new Referee(w.Db, () => At, tape: () => w.Gw.Tape);
        var verdict = referee.Verdict(ran.Program.StrategyId, w.Campaign.Id);
        Assert.True(verdict.Ok, verdict.Why);
        var run = w.Gw.Strategies.RunById(verdict.Promotion!.HoldoutRunId)!;
        log.WriteLine($"holdout run {run.Id[..12]}: {run.Bars} bars, {run.Trades} trade(s), {run.Outcome}");
        Assert.Equal(Referee.RunRole, run.Role);
        Assert.Equal(1, run.Trades);
        await w.Gw.DisposeAsync();
    }

    /// <summary>
    /// A BACKTEST WHOSE FEATURES REACH A HOLDOUT WINDOW IS REFUSED (<c>U-tape-holdout</c>, landed beside this unit). The
    /// dataset's cutoff at 03:00 holds the tape back from 03:00 to its last bar's close at 08:00, whatever the source. A
    /// research backtest ending at 02:59 has every bar before the cutoff, so the bars' holdout serves them — yet its last
    /// hourly bar closes AT 03:00, where its features would be read: it is REFUSED as <c>HOLDOUT_WITHHELD</c> in the
    /// holdout's words, before a bar is read, and nothing is charged or recorded. So is a backtest over ANOTHER dataset,
    /// one holding no cutoff at all, over the same hours: the tape's window is the market's time, not the dataset's. Over
    /// all eight hours that dataset's BARS are refused too, in the bars' words (<c>U-bar-holdout</c>: a cutoff holds every
    /// pair's bars over its window, from every dataset) — before this unit they were served there and only the features
    /// were refused. Ended an hour earlier, the first runs. And a cutoff set after a feed was asked halts the run at the next slice in
    /// the same words, which the feed keeps for <c>Backtest.Over</c> to answer as a refusal — never a run on the values
    /// before it.
    ///
    /// <para><b>RED on the rebase before the feed took the tape's holdout</b>: <c>FeatureFeed</c> handed every read a pipe
    /// audience and the reader refused a slice reaching the window, which the feed halved and then reported as a day too
    /// big for one read, a FAULTED run recorded as a trial.</para>
    /// </summary>
    [Fact]
    public async Task A_backtest_whose_features_reach_a_holdout_window_is_refused()
    {
        var w = await Given(tape: true);
        using var _1 = w.Db;
        var program = Parsed(w.Text);
        var window = Assert.Single(TapeHoldout.Pipe(CouncilRoles.Research, w.Gw.Datasets).Windows());
        Assert.Equal((Bar0.AddHours(3), (DateTimeOffset?)Bar0.AddHours(8)), (window.From, window.Until));

        // ITS OWN DATASET: every bar before the cutoff, the last one closing at it.
        var refused = Assert.Throws<GatewayDeniedException>(() => Research(w, to: Bar0.AddHours(3).AddMinutes(-1)));
        log.WriteLine(refused.Message);
        Assert.Equal(ErrorCode.HOLDOUT_WITHHELD, refused.Code);
        Assert.StartsWith("this program's feature(s) — `funding` — are read at the close of every bar a run evaluates",
            refused.Message, StringComparison.Ordinal);
        Assert.Contains("this run's closes from 2026-08-01T01:00:00.000Z to 2026-08-01T03:00:00.000Z would read it from "
                        + "2026-07-31T23:00:00.000Z — which the tape's holdout withholds: " + window.Words,
            refused.Message, StringComparison.Ordinal);
        Assert.Contains($"not to {BarAudience.Pipe(CouncilRoles.Research).Who}", refused.Message, StringComparison.Ordinal);
        Assert.EndsWith("Nothing was run: a run on the values outside the window would be another run.", refused.Message,
            StringComparison.Ordinal);
        Assert.Equal(0, new CampaignStore(w.Db).TrialsCharged(w.Campaign.Id));
        Assert.Null(w.Gw.Strategies.VersionById(program.StrategyId));

        var over = Backtest.Over(w.Gw.Datasets, w.Set.Id, program, Model, BarAudience.Pipe(CouncilRoles.Research), Bar0,
            Bar0.AddHours(3).AddMinutes(-1), tape: w.Gw.Tape);
        Assert.False(over.Ok);
        Assert.True(over.IsHoldout);
        Assert.Equal(refused.Message, over.Why + ".");

        // ANOTHER DATASET, NO CUTOFF OF ITS OWN, OVER THE SAME HOURS: its bars end before the window and are clear, its
        // features are not — the tape's window is the market's time, not the dataset's.
        var twin = Dataset(w.Gw, "v2", Bar0, 8 * 60);
        Assert.Null(w.Gw.Datasets.ById(twin)!.HoldoutFrom);
        var elsewhere = Assert.Throws<GatewayDeniedException>(() => Research(w, to: Bar0.AddHours(3).AddMinutes(-1), dataset: twin));
        log.WriteLine(elsewhere.Message);
        Assert.Equal(ErrorCode.HOLDOUT_WITHHELD, elsewhere.Code);
        Assert.Contains(window.Words, elsewhere.Message, StringComparison.Ordinal);
        Assert.Null(w.Gw.Strategies.VersionById(program.StrategyId));

        // AND OVER ALL EIGHT HOURS ITS BARS ARE HELD TOO (U-bar-holdout): the cutoff on v1 holds every pair's bars over
        // its window, from every dataset, so the run is refused in the bars' words before its features are asked.
        var barsHeld = Assert.Throws<GatewayDeniedException>(() => Research(w, to: Bar0.AddHours(8).AddMinutes(-1), dataset: twin));
        log.WriteLine(barsHeld.Message);
        Assert.Equal(ErrorCode.HOLDOUT_WITHHELD, barsHeld.Code);
        Assert.Contains(window.BarWords, barsHeld.Message, StringComparison.Ordinal);
        Assert.Contains($"dataset {twin} (BTCUSDT 1m v2) opening from {Bar0:u}", barsHeld.Message, StringComparison.Ordinal);
        Assert.Null(w.Gw.Strategies.VersionById(program.StrategyId));

        // AN HOUR EARLIER, IT RUNS: its last bar closes at 02:00, and its reads end there.
        var ran = Research(w);
        Assert.Equal(BacktestOutcome.COMPLETED, ran.Result.Outcome);
        Assert.Equal(Bar0.AddHours(2), Assert.Single(ran.Result.Features).AsOf);

        // A CUTOFF SET AFTER THE FEED WAS ASKED: the next slice reaching it is not read, and the run halts in its words.
        var late = Dataset(w.Gw, "v3", Bar0.AddDays(-1), 24 * 60);
        var hourly = Parsed(Program());
        var grid = BarGrid.For(hourly);
        var bars = Minutes(Bar0.AddDays(-1), 24 * 60);
        using var feed = new FeatureFeed(w.Gw.Tape!, TapeHoldout.Pipe(CouncilRoles.Research, w.Gw.Datasets), hourly,
            grid.EndOf(grid.StartOf(bars[^1].OpenTime)));
        Assert.Null(feed.Refusal(Bar0.AddDays(-1).AddHours(1)));
        Assert.True(w.Gw.Datasets.SetHoldout(late, Bar0.AddHours(-6), EvaluationClass.Research).Ok);
        var halted = Backtest.Run(hourly, new BacktestRequest(late, "fixture", Model, bars[0].OpenTime, null), bars, features: feed);
        log.WriteLine(halted.FaultReason);
        Assert.Equal(BacktestOutcome.FAULTED, halted.Outcome);
        Assert.NotNull(feed.Withheld);
        Assert.Equal(feed.Withheld, halted.FaultReason);
        Assert.Contains("the next slice of this run's closes from 2026-07-31T01:00:00.000Z", feed.Withheld, StringComparison.Ordinal);
        Assert.Contains($"dataset {late} (BTCUSDT 1m v3) holds out every bar from 2026-07-31 18:00:00Z", feed.Withheld, StringComparison.Ordinal);
        await w.Gw.DisposeAsync();
    }

    /// <summary>
    /// A CLEAN-HISTORY START BOUNDED BY A HOLDOUT SAYS SO IN THE TRACE (<c>U-tape-holdout</c>'s
    /// <see cref="FeatureCleanStart.Bounded"/>). A backtest over a dataset of the four hours that start two hours after
    /// the held-back dataset's tape window closes reads its features outside it — and the search for their clean-history
    /// start reaches back no further than that window's close at 08:00. The <c>Feature</c> line and the answer's
    /// <c>clean_history_bounded</c> say so, naming the window, so the start since then is never read as the input's own.
    /// The same run read over a ledger holding no cutoff states no bound — as a verdict never does, the referee reading
    /// every window.
    /// </summary>
    [Fact]
    public async Task A_clean_history_start_bounded_by_a_holdout_says_so_in_the_trace()
    {
        var w = await Given(tape: true);
        using var _1 = w.Db;
        using (var store = new TapeStore(w.Gw.Tape!.File))
            for (var h = 8; h < 14; h++) Reading(store, Bar0.AddHours(h).AddMinutes(10), "0.00010000");

        var after = Dataset(w.Gw, "v2", Bar0.AddHours(10), 4 * 60);
        var ran = Research(w, from: Bar0.AddHours(10), to: Bar0.AddHours(14).AddMinutes(-1), dataset: after);
        Assert.Equal(BacktestOutcome.COMPLETED, ran.Result.Outcome);

        var summary = Assert.Single(ran.Result.Features);
        log.WriteLine(ran.Result.Trace.Events[^1].Reason);
        var bound = $"the search read no reading stamped before {Bar0.AddHours(8):u}, the close of dataset {w.Set.Id} "
                    + "(BTCUSDT 1m v1)'s holdout window, which this read may not reach";
        Assert.StartsWith(bound, summary.CleanHistoryBounded, StringComparison.Ordinal);
        Assert.NotNull(summary.CleanHistoryStart);
        Assert.True(summary.CleanHistoryStart >= Bar0.AddHours(8), $"a bounded start {summary.CleanHistoryStart:O} is before the window's close");
        var line = Assert.Single(ran.Result.Trace.Of(BacktestEventKind.Feature));
        Assert.Contains($"funding: clean history from {summary.CleanHistoryStart!.Value.UtcDateTime:O} ({summary.CleanHistoryBounded}); ",
            line.Reason, StringComparison.Ordinal);

        // OVER A LEDGER HOLDING NO CUTOFF the same tape bounds nothing: the search reads back to the input's own start.
        var grid = BarGrid.For(ran.Program);
        var bars = Minutes(Bar0.AddHours(10), 4 * 60);
        using var feed = new FeatureFeed(w.Gw.Tape!, ResearchTape, ran.Program, grid.EndOf(grid.StartOf(bars[^1].OpenTime)));
        var free = Backtest.Run(ran.Program, new BacktestRequest(after, "fixture", Model, bars[0].OpenTime, null), bars, features: feed);
        var unbounded = Assert.Single(free.Features);
        log.WriteLine(free.Trace.Events[^1].Reason);
        Assert.Null(unbounded.CleanHistoryBounded);
        Assert.True(unbounded.CleanHistoryStart < Bar0.AddHours(8), $"an unbounded start {unbounded.CleanHistoryStart:O} is not the input's own");
        Assert.DoesNotContain("the search read no reading stamped before", free.Trace.Events[^1].Reason, StringComparison.Ordinal);
        await w.Gw.DisposeAsync();
    }

    /// <summary>
    /// (o) NO TAPE REFUSES BEFORE ANY CHARGE. Where no tape is open, a <c>backtest</c> of a program that reads a feature is
    /// refused in words naming the feature — before the trial budget: no trial, no run, no version recorded — and the
    /// referee refuses to judge it before the verdict budget: nothing charged. Below them, <c>Backtest.Over</c> refuses it
    /// before a bar is read, <c>Backtest.Run</c> will not run it without a feed, and the evaluator stepped without its
    /// values faults rather than run a bar as though every value were absent.
    /// </summary>
    [Fact]
    public async Task No_tape_refuses_before_any_charge()
    {
        var w = await Given(tape: false);
        using var _1 = w.Db;
        Assert.Null(w.Gw.Tape);
        var program = Parsed(w.Text);

        var refused = Assert.Throws<GatewayDeniedException>(() => Research(w));
        log.WriteLine(refused.Message);
        Assert.Equal(ErrorCode.MARKET_DATA_UNAVAILABLE, refused.Code);
        Assert.Equal(Backtest.NoTape(program) + " Nothing was run and no trial was charged.", refused.Message);
        Assert.StartsWith("this program reads 1 feature(s) — `funding` — and this installation has no market-context tape open",
            refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, new CampaignStore(w.Db).TrialsCharged(w.Campaign.Id));
        Assert.Null(w.Gw.Strategies.VersionById(program.StrategyId));

        new StrategyStore(w.Db).RecordVersion(new StrategyVersionRow(
            program.StrategyId, program.Source, program.Canonical, program.Manifest, StrategyStore.InterpreterBuild,
            ParseVerdict.Accepted, program.WarmUpBars, Bar0, CouncilRoles.Research, "attempt-1")
        {
            Timeframe = program.Freshness?.Timeframe,
            DataFreshness = program.Freshness?.DataFreshness,
            MaxDecisionAge = program.Freshness?.MaxDecisionAge
        });
        var verdict = new Referee(w.Db, () => At).Verdict(program.StrategyId, w.Campaign.Id);
        log.WriteLine(verdict.Why);
        Assert.False(verdict.Ok);
        Assert.Contains(Backtest.NoTape(program) + " No verdict was charged.", verdict.Why, StringComparison.Ordinal);
        Assert.Equal(0, new CampaignStore(w.Db).VerdictsInLineage(w.Campaign.Id));

        var over = Backtest.Over(w.Gw.Datasets, w.Set.Id, program, Model, BarAudience.Pipe(CouncilRoles.Research), Bar0, Bar0.AddHours(2));
        Assert.False(over.Ok);
        Assert.Equal(Backtest.NoTape(program), over.Why);

        var request = new BacktestRequest(w.Set.Id, w.Set.NormalisedSha256, Model, Bar0, null);
        var unfed = Assert.Throws<ArgumentException>(() => Backtest.Run(program, request, Minutes(Bar0, 120)));
        Assert.Contains("a program that reads a feature is run with its values or not at all", unfed.Message, StringComparison.Ordinal);

        var state = EvaluationState.Start(program, null, program.Bars);
        var outcome = StrategyEvaluator.Step(state, new KlineBar(Bar0, 100m, 101m, 99m, 100m, 1m) { Minutes = 60 },
            AccountReading.Flat(10_000m));
        Assert.Equal(EvaluationStatus.Faulted, outcome.Status);
        Assert.Contains("this program reads 1 feature(s) and was handed 0 value(s)", outcome.FaultReason, StringComparison.Ordinal);
        await w.Gw.DisposeAsync();
    }

    /// <summary>
    /// The run id of `ma-crossover.strategy` over the fixture request above: SHA-256 of the five lines main's
    /// <c>RunIdFor</c> hashes (at <c>18a7ab10</c>), computed outside this build, with Python's hashlib.
    /// </summary>
    const string PinnedV1RunId = "1cf789735c3282c5ac3b894cc490942c778bbdd32fc0edc57b09c53055fd6a77";
}

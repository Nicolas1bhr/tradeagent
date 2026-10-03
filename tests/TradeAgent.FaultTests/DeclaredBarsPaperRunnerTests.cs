using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// A PROGRAM ON DECLARED BARS IS DEPLOYED, RUN AND REPLACED LIKE ANY OTHER (<c>U-timeframe-b</c>).
///
/// <para><b>What changed.</b> <c>U-timeframe-a</c> taught the backtest and the referee to judge a program that
/// declares <c>bars 1h</c> on hours while the paper runner still evaluated every minute, so the runner ended such
/// a run before its first bar, in words, and the deployment sweep started no replacement for it — each would have
/// been another row, another flatten and another paid wake to say the same sentence. <c>U-timeframe-b</c> steps
/// the rules on the declared bars while protection stays on the minute (<c>ForwardRunnerTests</c> measures that
/// end to end over the paper connector), so the refusal and the sweep's guard for it went together, and an
/// hourly run that ends is replaced exactly as a minute run is.</para>
///
/// <para>Everything runs over <see cref="RecordingConnector"/> and the built-in simulator, in practice mode.
/// Nothing reaches a venue and no real money is involved.</para>
/// </summary>
public class DeclaredBarsPaperRunnerTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Cutoff = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset At = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// AN HOURLY PROGRAM: it enters when an hour closes at or above its two-bar mean, and its three execution
    /// bounds are declared, so nothing about the program itself ends a run of it.
    /// </summary>
    const string HourlyText =
        "instrument BTCUSDT\nbars 1h\ntimeframe 1h\ndata_freshness 2h\nmax_decision_age 1h\nsize fixed 1\n"
        + "indicator m = sma(close, 2)\nexit when close < 90\nentry when close >= m\n";

    sealed class TestClock(DateTimeOffset at) : TimeProvider
    {
        public DateTimeOffset At { get; set; } = at;
        public override DateTimeOffset GetUtcNow() => At;
        public override long GetTimestamp() => At.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    static async Task<(TradingGateway Gw, RecordingConnector Conn, Database Db, TestClock Clock)> Ready()
    {
        var db = TestEnv.NewDb();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker { AccountId = "SIM-001" }));
        var clock = new TestClock(At);
        var gw = new TradingGateway(db, conn, new HealthRegistry(), new GatewayOptions { Clock = clock });
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments, "BTCUSDT"];
            s.Risk.MaxOrderQuantity = 100m;
            s.Risk.MaxNotionalPerOrder = 0m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        return (gw, conn, db, clock);
    }

    /// <summary>
    /// A VERSION OF <paramref name="text"/> THAT REALLY CARRIES A PAPER-ELIGIBLE VERDICT IN THIS DATABASE —
    /// dataset, holdout, campaign, version, holdout run and promotion — written by this build.
    /// </summary>
    static string Judged(Database db, string text)
    {
        var datasets = new DatasetStore(db);
        var file = Path.Combine(Paths.Data, $"declared-bars-{Guid.NewGuid():n}.csv");
        Directory.CreateDirectory(Paths.Data);
        File.WriteAllText(file, KlineNormaliser.Header + "\n");

        var id = datasets.Record(new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 12, [],
            file, DatasetStore.Sha256(file)!, 1000, Cutoff.AddDays(-300), Cutoff.AddDays(60), 0, [],
            false, 0, 0, 0, At, DatasetState.ACCEPTED, null, []));
        Assert.True(datasets.SetHoldout(id, Cutoff, EvaluationClass.Research).Ok);
        var set = datasets.ById(id)!;

        var campaign = new CampaignStore(db).Open("BTCUSDT hourly bars", set, 10, 3, At);
        Assert.True(campaign.Ok, campaign.Why);

        var parse = StrategyParser.Parse(text);
        Assert.True(parse.Ok, parse.Why);
        var program = parse.Program!;

        var strategies = new StrategyStore(db);
        strategies.RecordVersion(new StrategyVersionRow(
            program.StrategyId, program.Source, program.Canonical, program.Manifest, StrategyStore.InterpreterBuild,
            ParseVerdict.Accepted, program.WarmUpBars, Cutoff.AddDays(-1), null, null)
        {
            Timeframe = program.Freshness?.Timeframe,
            DataFreshness = program.Freshness?.DataFreshness,
            MaxDecisionAge = program.Freshness?.MaxDecisionAge
        });

        var model = ExecutionModel.Declare(0.001m, 0m, 0.0001m, 10_000m).Model!;
        var runId = new BacktestRequest(set.Id, set.NormalisedSha256, model, Cutoff, null).RunIdFor(program.StrategyId);
        strategies.RecordRun(new StrategyRunRow(
            runId, program.StrategyId, set.Id, set.NormalisedSha256, Cutoff, null, model.Canonical,
            BacktestOutcome.COMPLETED.ToString(), null, 500, 4, 3, 4, 4, 100, 0, 0,
            12m, 2m, 10m, 3m, "trace-sha", At, Referee.RunRole, null), []);

        new Promotions(db).Record(new PromotionRow(
            "", program.StrategyId, campaign.Campaign!.Id, CampaignPolicy.Sha256Of(CampaignPolicy.PaperV1),
            StrategyStore.InterpreterBuild, set.Id, set.NormalisedSha256, model.Canonical, Referee.EvaluatorVersion,
            runId, PromotionVerdict.PaperEligible, PromotionReason.MetOnHistory, At)
        {
            Timeframe = program.Freshness?.Timeframe,
            DataFreshness = program.Freshness?.DataFreshness,
            MaxDecisionAge = program.Freshness?.MaxDecisionAge
        });

        return program.StrategyId;
    }

    /// <summary>The owner's grant, the app's allocation inside it and the app's deployment of it — each by the call the product uses.</summary>
    static async Task<StrategyDeploymentRow> Deployed(TradingGateway gw)
    {
        var granted = await gw.GrantPaperEnvelopeAsync("BTCUSDT", 5m, null, At.AddDays(30), At);
        Assert.True(granted.Ok, granted.Why);
        Assert.Equal(1, gw.AllocatePaperDue(At));
        Assert.Equal(1, gw.StartPaperDeploymentsDue(At));
        return gw.Deployments.Open().Single();
    }

    /// <summary>
    /// <paramref name="count"/> CLOSED FORWARD MINUTES AFTER THE RUN STARTED, stored the way the collector
    /// stores them — the first closing at 100 and the rest rising — and the clock moved past the last close.
    /// </summary>
    static void Minutes(Database db, TestClock clock, int count)
    {
        var store = new ForwardBarStore(db);
        for (var i = 1; i <= count; i++)
        {
            var open = At.AddMinutes(i);
            var close = open + ForwardBars.BarLength;
            var price = 100m + i * 0.01m;
            var append = store.Append(new ForwardFetchAttempt
            {
                Source = ForwardBars.Source,
                Symbol = "BTCUSDT",
                Url = "https://example.invalid/klines (this test wrote the rows; nothing was fetched)",
                RequestedAt = close,
                ReceivedAt = close.AddSeconds(1),
                HttpStatus = 200
            }, [new ForwardBars.Kline(open, price, price + 0.5m, price - 0.5m, price, 10m, close)]);
            Assert.Equal(1, append.Stored);
        }

        clock.At = At.AddMinutes(count + 1).AddSeconds(2);
    }

    /// <summary>
    /// AN HOURLY PROGRAM'S RUN IS STEPPED RATHER THAN ENDED, AND WHEN IT DOES END IT IS REPLACED AS A MINUTE
    /// PROGRAM'S IS. Two and a half hours of minutes are stepped as the two hours that closed in them; the run
    /// is then ended by the owner's own call — any end would do — and the next sweep starts its replacement.
    ///
    /// <para><b>RED before this unit</b>: the runner ended the run before its first bar, in words. With only the
    /// runner's refusal removed and the sweep's guard for it left behind, it goes red at the sweep instead,
    /// which starts nothing: the two went together.</para>
    /// </summary>
    [Fact]
    public async Task A_bars_1h_programs_run_is_stepped_and_its_ended_run_replaced_as_a_minute_programs_is()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        var runner = new ForwardRuns(gw, db, () => clock.At);

        Judged(db, HourlyText);
        var deployment = await Deployed(gw);
        Minutes(db, clock, 150);

        var state = Assert.Single(await runner.AdvanceAsync());
        log.WriteLine($"runner : {state.BarsReplayed} bars stepped, ended: {state.Ended ?? "no"}, {state.State?.Counters}");
        Assert.Null(state.Ended);
        Assert.Equal(2, state.BarsReplayed);
        Assert.Equal(2, state.State!.Counters.Bars);
        Assert.True(gw.Deployments.ById(deployment.Id)!.IsActive);

        await gw.EndPaperDeploymentAsync(deployment.Id, "ended by the test");
        Assert.True(gw.Deployments.IsReconciled(deployment.Id));

        Assert.Equal(1, gw.StartPaperDeploymentsDue(clock.At.AddMinutes(1)));
        Assert.Equal(2, gw.Deployments.ForAllocation(deployment.AllocationId).Count);
        Assert.Equal(0, conn.Places);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// AND A MINUTE PROGRAM'S ENDED RUN IS STILL REPLACED, AS IT ALWAYS WAS. Its run is ended by the owner's own
    /// call here — any end would do.
    /// </summary>
    [Fact]
    public async Task A_minute_programs_ended_run_is_still_replaced_as_before()
    {
        var (gw, _, db, clock) = await Ready();
        using var _1 = db;

        Judged(db, HourlyText.Replace("bars 1h\n", ""));
        var deployment = await Deployed(gw);
        await gw.EndPaperDeploymentAsync(deployment.Id, "ended by the test");
        Assert.True(gw.Deployments.IsReconciled(deployment.Id));

        Assert.Equal(1, gw.StartPaperDeploymentsDue(clock.At.AddMinutes(1)));
        Assert.Equal(2, gw.Deployments.ForAllocation(deployment.AllocationId).Count);
        await gw.DisposeAsync();
    }
}

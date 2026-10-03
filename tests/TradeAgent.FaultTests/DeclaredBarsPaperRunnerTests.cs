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
/// THE PAPER RUNNER REFUSES, IN WORDS, A PROGRAM ON BARS IT DOES NOT STEP — AND IS NOT HANDED A NEW ONE TO
/// REFUSE ON EVERY SWEEP (<c>U-timeframe-a</c> item 4).
///
/// <para><b>Why the runner refuses.</b> After this unit a program that declares <c>bars 1h</c> is backtested
/// and judged on hours, so it can carry a paper-eligible verdict, and the app's own policy allocates and
/// deploys it. This build's runner evaluates every closed minute: stepping that program here would run a
/// different strategy under the judged one's id. So the run is ENDED before a bar is stepped, saying why,
/// and nothing is sent — until <c>U-timeframe-b</c> steps rules on declared bars.</para>
///
/// <para><b>Why the sweep stops.</b> An ended, reconciled run frees its allocation, and the deployment sweep
/// starts a replacement at a later instant. For a run the runner will end at its first pass for the same
/// reason, that is a new deployment row, a flatten and a paid turn for Research on every sweep. Once one run
/// of the allocation exists, none is started for a version the runner refuses.</para>
///
/// <para>Everything runs over <see cref="RecordingConnector"/> and the built-in simulator, in practice mode.
/// Nothing reaches a venue and no real money is involved.</para>
/// </summary>
public class DeclaredBarsPaperRunnerTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Cutoff = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset At = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// AN HOURLY PROGRAM THAT WOULD TRADE ON THESE BARS IF IT WERE STEPPED ON MINUTES: every bar below closes
    /// at 100, above its two-bar mean the first time the price lifts. Its three execution bounds are declared,
    /// so the only reason this run can end is the one under test.
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
    /// (g) THE PAPER RUNNER REFUSES A `bars 1h` DEPLOYMENT IN WORDS.
    ///
    /// <para>A paper-eligible hourly version is allocated and deployed by the app's own policy, and two and a
    /// half hours of closed minutes are in the ledger — enough for two hourly bars to have closed, and for a
    /// minute-stepped run to have entered. The runner ends the run before stepping a single bar, with the
    /// sentence on the deployment's own line, and nothing reaches the connector.</para>
    ///
    /// <para><b>RED on the base</b> (<c>d99155e</c>): the hourly text did not parse, so no version could be
    /// recorded. <b>The mutant</b> — the refusal removed from <c>ForwardRuns.AdvanceOneAsync</c> — goes red
    /// here: the run is left active and stepped nothing, the evaluator refusing to start a program on bars it
    /// did not declare, and no sentence anywhere says why.</para>
    /// </summary>
    [Fact]
    public async Task The_paper_runner_refuses_a_bars_1h_deployment_in_words()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        var runner = new ForwardRuns(gw, db, () => clock.At);

        var version = Judged(db, HourlyText);
        Assert.Equal(PromotionState.PaperEligible, gw.Promotions.Standing(version).State);
        var deployment = await Deployed(gw);
        Minutes(db, clock, 150);

        var states = await runner.AdvanceAsync();
        var state = Assert.Single(states);
        var ended = gw.Deployments.ById(deployment.Id)!;
        log.WriteLine($"runner     : {state.BarsReplayed} bars stepped, ended: {state.Ended ?? "no"}");
        log.WriteLine($"deployment : {ended.State} — {ended.EndReason ?? "-"}");

        Assert.Equal(DeploymentState.Ended, ended.State);
        Assert.Equal(
            "this program declares `bars 1h`, and this build's paper runner evaluates every minute; programs on hourly "
            + "bars run after the next update. It was ended before a bar was stepped, and nothing was sent",
            ended.EndReason);
        Assert.Equal(ended.EndReason, state.Ended);
        Assert.Equal(0, state.BarsReplayed);
        Assert.Equal(0, conn.Places);
        Assert.DoesNotContain(gw.Deployments.OpsOf(deployment.Id), o => o.Kind is DeploymentOpKind.Entry
            or DeploymentOpKind.Exit or DeploymentOpKind.Stop or DeploymentOpKind.Target);
        Assert.True(gw.Deployments.IsReconciled(deployment.Id));
        await gw.DisposeAsync();
    }

    /// <summary>
    /// A RUN THE RUNNER REFUSED IS NOT REPLACED ON THE NEXT SWEEP, OR ANY SWEEP AFTER IT. The allocation
    /// still stands and its slot is free, so without the guard every sweep starts a new deployment for the
    /// runner to end on its next pass — a new row, a flatten and a new paid wake for Research each time.
    ///
    /// <para><b>The mutant</b> — the guard removed from <c>StartPaperDeploymentsDue</c> — starts one
    /// replacement per sweep here, each ended by the runner before the next: 1, 1, 1, and four deployments
    /// of one allocation.</para>
    /// </summary>
    [Fact]
    public async Task A_run_the_runner_refused_for_its_bars_is_not_replaced_on_the_next_sweep()
    {
        var (gw, _, db, clock) = await Ready();
        using var _1 = db;
        var runner = new ForwardRuns(gw, db, () => clock.At);

        Judged(db, HourlyText);
        var deployment = await Deployed(gw);
        Minutes(db, clock, 10);
        await runner.AdvanceAsync();
        Assert.Equal(DeploymentState.Ended, gw.Deployments.ById(deployment.Id)!.State);

        var started = new List<int>();
        for (var k = 1; k <= 3; k++)
        {
            clock.At = clock.At.AddMinutes(1);
            started.Add(gw.StartPaperDeploymentsDue(clock.At));
            await runner.AdvanceAsync();
        }
        log.WriteLine($"sweeps after the refusal started: {string.Join(", ", started)}");

        Assert.Equal([0, 0, 0], started);
        Assert.Single(gw.Deployments.ForAllocation(deployment.AllocationId));
        Assert.Empty(gw.Deployments.Open());
        await gw.DisposeAsync();
    }

    /// <summary>
    /// AND A MINUTE PROGRAM'S ENDED RUN IS STILL REPLACED, AS IT ALWAYS WAS: the guard is about a program the
    /// runner refuses and nothing else. Its run is ended by the owner's own call here — any end would do.
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

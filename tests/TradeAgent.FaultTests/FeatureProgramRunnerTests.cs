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
/// THE PAPER RUNNER REFUSES, IN WORDS, A PROGRAM REQUIRING A DECLARATION IT DOES NOT IMPLEMENT — AND IS NOT HANDED A NEW
/// ONE TO REFUSE ON EVERY SWEEP (<c>U-language-v2a</c> item 2; R05 row 10).
///
/// <para><b>Why the runner refuses.</b> A program that reads a feature is backtested and judged with the feature's values
/// as they had arrived, so it can carry a paper-eligible verdict, and the app's own policy allocates and deploys it. This
/// build's runner computes no feature value: stepping that program here would decide on rules whose inputs are missing —
/// a different program under the judged one's id. So the run is ENDED before a bar is stepped, saying why, and nothing is
/// sent — until <c>U-runner-features</c> values features at the runner's decision instant. The precedent is
/// <c>U-timeframe-a</c>'s refusal of declared bars, lifted by <c>U-timeframe-b</c>.</para>
///
/// <para>Everything runs over <see cref="RecordingConnector"/> and the built-in simulator, in practice mode. Nothing
/// reaches a venue and no real money is involved.</para>
/// </summary>
public class FeatureProgramRunnerTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Cutoff = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset At = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    const string Spec =
        """{"kind":"latest","input":{"source":"binance-um-premium","series":"premium-index","subject":"BTCUSDT","field":"lastFundingRate"},"latency_s":5,"max_age_s":180}""";

    /// <summary>
    /// AN HOURLY PROGRAM THAT READS FUNDING, its three execution bounds declared, so the only reason a run of it can end
    /// is the one under test. Its v1 twin reads the close instead, and is stepped.
    /// </summary>
    const string FeatureText =
        "instrument BTCUSDT\nbars 1h\ntimeframe 1h\ndata_freshness 2h\nmax_decision_age 1h\nsize fixed 1\n"
        + $"feature funding = {Spec}\nexit when funding > 0\nentry when funding < -0.0003\n";

    const string TwinText =
        "instrument BTCUSDT\nbars 1h\ntimeframe 1h\ndata_freshness 2h\nmax_decision_age 1h\nsize fixed 1\n"
        + "exit when close < 90\nentry when close >= 100\n";

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
    /// A VERSION OF <paramref name="text"/> THAT REALLY CARRIES A PAPER-ELIGIBLE VERDICT IN THIS DATABASE — dataset,
    /// holdout, campaign, version, holdout run and promotion — written by this build, as <c>DeclaredBarsPaperRunnerTests</c>
    /// writes one.
    /// </summary>
    static string Judged(Database db, string text)
    {
        var datasets = new DatasetStore(db);
        var file = Path.Combine(Paths.Data, $"feature-program-{Guid.NewGuid():n}.csv");
        Directory.CreateDirectory(Paths.Data);
        File.WriteAllText(file, KlineNormaliser.Header + "\n");

        var id = datasets.Record(new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 12, [],
            file, DatasetStore.Sha256(file)!, 1000, Cutoff.AddDays(-300), Cutoff.AddDays(60), 0, [],
            false, 0, 0, 0, At, DatasetState.ACCEPTED, null, []));
        Assert.True(datasets.SetHoldout(id, Cutoff, EvaluationClass.Research).Ok);
        var set = datasets.ById(id)!;

        var campaign = new CampaignStore(db).Open("BTCUSDT funding", set, 10, 3, At);
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

    /// <summary><paramref name="count"/> closed forward minutes after the run started, and the clock past the last close.</summary>
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
    /// (k) A RUNNER THAT DOES NOT UNDERSTAND A REQUIRED FIELD REFUSES. A paper-eligible version that reads funding is
    /// allocated and deployed by the app's own policy, and two and a half hours of closed minutes are in the ledger. The
    /// runner ends the run before stepping a single bar, with the sentence naming `feature` on the deployment's own line;
    /// nothing reaches the connector; <c>ForwardRuns.CannotRun</c> says the same sentence; and three sweeps after it
    /// start no replacement. Its v1 twin — the same program reading the close — is stepped as always, and nothing here
    /// refuses it.
    ///
    /// <para><b>RED on the base</b> (<c>18a7ab10</c>): the feature line did not parse, so no version could be recorded.
    /// <b>The mutant</b> — <c>feature</c> added to <c>ForwardRuns.Implements</c> — goes red here: the run is left active
    /// and stepped.</para>
    /// </summary>
    [Fact]
    public async Task A_runner_that_does_not_understand_a_required_field_refuses()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        var runner = new ForwardRuns(gw, db, () => clock.At);

        var version = Judged(db, FeatureText);
        Assert.Equal(PromotionState.PaperEligible, gw.Promotions.Standing(version).State);
        var program = StrategyParser.Parse(FeatureText).Program!;
        Assert.Contains(StrategyDeclarations.Feature, program.Requires);
        Assert.DoesNotContain(StrategyDeclarations.Feature, ForwardRuns.Implements);

        var deployment = await Deployed(gw);
        Minutes(db, clock, 150);

        var state = Assert.Single(await runner.AdvanceAsync());
        var ended = gw.Deployments.ById(deployment.Id)!;
        log.WriteLine($"runner     : {state.BarsReplayed} bars stepped, ended: {state.Ended ?? "no"}");
        log.WriteLine($"deployment : {ended.State} — {ended.EndReason ?? "-"}");

        const string Words =
            "this program requires `feature`, which this build's paper runner does not implement. Every declaration a "
            + "program uses constrains its orders, its risk or how it is evaluated, so a reader that left one out would run "
            + "a program nobody judged — it is refused rather than run without it. It reads `funding`, and this runner "
            + "computes no feature value yet: programs that read features run on paper after a later update. It was ended "
            + "before a bar was stepped, and nothing was sent";
        Assert.Equal(DeploymentState.Ended, ended.State);
        Assert.Equal(Words, ended.EndReason);
        Assert.Equal(Words, state.Ended);
        Assert.Equal(Words, ForwardRuns.CannotRun(gw.Strategies, version));
        Assert.Equal(0, state.BarsReplayed);
        Assert.Equal(0, conn.Places);
        Assert.DoesNotContain(gw.Deployments.OpsOf(deployment.Id), o => o.Kind is DeploymentOpKind.Entry
            or DeploymentOpKind.Exit or DeploymentOpKind.Stop or DeploymentOpKind.Target);
        Assert.True(gw.Deployments.IsReconciled(deployment.Id));

        // NO REPLACEMENT, ON ANY SWEEP: the allocation still stands and its slot is free.
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

        // THE V1 TWIN IS NOT THIS RUNNER'S TO REFUSE.
        var twin = StrategyParser.Parse(TwinText).Program!;
        Assert.DoesNotContain(StrategyDeclarations.Feature, twin.Requires);
        Assert.Null(ForwardRuns.Refuses(twin));
        await gw.DisposeAsync();
    }
}

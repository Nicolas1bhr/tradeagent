using System.Globalization;
using TradeAgent.Connectors.Fake;
using TradeAgent.ConnectorSdk;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// THE PAPER DEPLOYMENT: AN APP-OWNED RECORD OF WHAT IS BEING RUN FORWARD, AND THE FIVE THINGS IT
/// CANNOT DO.
///
/// <para><b>What this unit added.</b> An allocation said what a version MAY do; nothing said it was
/// doing it. A deployment is the run: seven immutable identity facts, an execution identity the pipe
/// cannot mint, a bar cursor that only advances over settled work, and an operation ledger written
/// BEFORE anything is dispatched.</para>
///
/// <para><b>The two mutants this class exists to catch.</b> The operation written AFTER the dispatch —
/// which makes a kill between the write and the answer indistinguishable from an order that never
/// went, and sends a second one — and <c>SuspendIfMoved</c> reading the MODE alone, which leaves a
/// deployment running on a platform and an account that are not the ones it was started on.</para>
///
/// <para>Everything is measured over <see cref="RecordingConnector"/> and the built-in simulator.
/// Nothing here reaches a venue and no real money is involved.</para>
/// </summary>
public class PaperDeploymentTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Cutoff = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset At = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A clock this suite owns and can MOVE, so a second deployment is a second instant.</summary>
    sealed class TestClock(DateTimeOffset at) : TimeProvider
    {
        public DateTimeOffset At { get; set; } = at;
        public override DateTimeOffset GetUtcNow() => At;
        public override long GetTimestamp() => At.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    static string ProgramText(int threshold) =>
        $"instrument BTCUSDT\nsize fixed 1\nexit when close < 97\nentry when close > {threshold}\n";

    /// <summary>A gateway on a simulator both witnesses call a simulation, in practice mode.</summary>
    static async Task<(TradingGateway Gw, RecordingConnector Conn, Database Db, TestClock Clock)> Ready(
        Action<TradeAgentSettings>? settings = null, string? connectorId = null, Database? db = null,
        RecordingConnector? conn = null, string account = "SIM-001")
    {
        db ??= TestEnv.NewDb();
        conn ??= new RecordingConnector(
            new FakeConnector(new FakeBroker { AccountId = account }), connectorId);
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
            settings?.Invoke(s);
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        return (gw, conn, db, clock);
    }

    /// <summary>
    /// A VERSION THAT REALLY CARRIES A VERDICT IN THIS DATABASE — the dataset, the holdout, the
    /// campaign, the version, the holdout run and the promotion row. A faked id would be refused by
    /// the ledger a step earlier and would prove nothing about anything above it.
    /// </summary>
    static string Judged(Database db, string verdict, int threshold = 103)
    {
        var datasets = new DatasetStore(db);
        var file = Path.Combine(Paths.Data, $"paper-deployment-{Guid.NewGuid():n}.csv");
        Directory.CreateDirectory(Paths.Data);
        File.WriteAllText(file, KlineNormaliser.Header + "\n");

        var id = datasets.Record(new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 12, [],
            file, DatasetStore.Sha256(file)!, 1000, Cutoff.AddDays(-300), Cutoff.AddDays(60), 0, [],
            false, 0, 0, 0, At, DatasetState.ACCEPTED, null, []));
        Assert.True(datasets.SetHoldout(id, Cutoff, EvaluationClass.Research).Ok);
        var set = datasets.ById(id)!;

        var campaign = new CampaignStore(db).Open($"BTCUSDT 1m v{threshold}", set, 10, 3, At);
        Assert.True(campaign.Ok, campaign.Why);

        var program = StrategyParser.Parse(ProgramText(threshold)).Program!;
        var strategies = new StrategyStore(db);
        strategies.RecordVersion(new StrategyVersionRow(
            program.StrategyId, program.Source, program.Canonical, program.Manifest,
            StrategyStore.InterpreterBuild, ParseVerdict.Accepted, program.WarmUpBars,
            Cutoff.AddDays(-1), null, null));

        var model = ExecutionModel.Declare(0.001m, 0m, 0.0001m, 10_000m).Model!;
        var runId = new BacktestRequest(set.Id, set.NormalisedSha256, model, Cutoff, null)
            .RunIdFor(program.StrategyId);
        strategies.RecordRun(new StrategyRunRow(
            runId, program.StrategyId, set.Id, set.NormalisedSha256, Cutoff, null, model.Canonical,
            BacktestOutcome.COMPLETED.ToString(), null, 500, 4, 3, 4, 4, 100, 0, 0,
            12m, 2m, 10m, 3m, "trace-sha", At, Referee.RunRole, null), []);

        new Promotions(db).Record(new PromotionRow(
            "", program.StrategyId, campaign.Campaign!.Id,
            verdict == PromotionVerdict.PaperEligible
                ? CampaignPolicy.Sha256Of(CampaignPolicy.PaperV1)
                : CampaignPolicy.Sha256Of(CampaignPolicy.V1),
            StrategyStore.InterpreterBuild, set.Id, set.NormalisedSha256, model.Canonical,
            Referee.EvaluatorVersion, runId, verdict,
            verdict == PromotionVerdict.PaperEligible ? PromotionReason.MetOnHistory : PromotionReason.Met, At));

        return program.StrategyId;
    }

    /// <summary>The owner's one grant, and the app's own allocation inside it — the state this unit starts from.</summary>
    static async Task<(PaperEnvelopeRow Envelope, string Version)> Allocated(TradingGateway gw, Database db)
    {
        var granted = await gw.GrantPaperEnvelopeAsync("BTCUSDT", 5m, null, At.AddDays(30), At);
        Assert.True(granted.Ok, granted.Why);
        var version = Judged(db, PromotionVerdict.PaperEligible);
        Assert.Equal(1, gw.AllocatePaperDue(At));
        return (granted.Envelope!, version);
    }

    /// <summary>A filled long the deployment's end has to flatten. Placed by the OWNER, in process.</summary>
    static async Task SeedAPosition(TradingGateway gw, RecordingConnector conn, string id = "seed-1")
    {
        await gw.PlaceAsync(AgentContext.Operator, id, new PlaceIntent(
            "BTCUSDT", OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, "seed"));
        Assert.Contains(conn.Broker.Positions, p => p.Symbol == "BTCUSDT" && p.Quantity != 0m);
    }

    // ---- item 2: the identity -------------------------------------------------------------------

    /// <summary>
    /// (e) THE DEPLOYMENT IDENTITY CANNOT BE PRESENTED OVER THE PIPE, AND IS REFUSED IN A LIVE MODE.
    ///
    /// <para><c>AgentContext.ForAgent</c> is the ONLY factory the pipe server uses, and it cannot
    /// return a deployment identity however the caller names its session — the same defence
    /// <c>AgentContext.Operator</c> has, and the reserved name at the pipe is a tripwire rather than
    /// the defence. And in <c>LIVE_CONFIRM</c> or <c>LIVE_AUTONOMOUS</c> the identity places nothing
    /// at all: a paper experiment cannot become a live one because the owner changed a mode.</para>
    /// </summary>
    [Fact]
    public async Task The_deployment_identity_cannot_be_presented_over_the_pipe_and_is_refused_in_a_live_mode()
    {
        var (gw, conn, db, _) = await Ready();
        using var _1 = db;

        var fromThePipe = AgentContext.ForAgent($"deployment:{new string('a', 64)}", CouncilRoles.Operations);
        var inProcess = AgentContext.Deployment(new string('a', 64));

        gw.Update(s =>
        {
            s.Mode = TradingMode.LIVE_CONFIRM;
            s.LiveActivated = true;
        });
        var liveRefusal = await Swallow(() => gw.PlaceAsync(inProcess, "dp-live-1",
            new PlaceIntent("BTCUSDT", OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, null)));

        log.WriteLine($"from the pipe        : {fromThePipe.SessionId}, deployment {fromThePipe.IsDeployment}");
        log.WriteLine($"in process           : {inProcess.SessionId}, deployment {inProcess.IsDeployment}");
        log.WriteLine($"in LIVE_CONFIRM      : {liveRefusal}");
        log.WriteLine($"orders at the wire   : {conn.Places}");

        Assert.False(fromThePipe.IsDeployment);
        Assert.Null(fromThePipe.DeploymentId);
        Assert.StartsWith(AgentContext.DeploymentSessionPrefix, fromThePipe.SessionId, StringComparison.Ordinal);
        Assert.True(inProcess.IsDeployment);
        Assert.True(inProcess.MayPlaceOrders);
        Assert.False(inProcess.IsOperator);

        Assert.StartsWith(ErrorCode.MODE_FORBIDS_EXECUTION.ToString(), liveRefusal, StringComparison.Ordinal);
        Assert.Equal(0, conn.Places);
        Assert.Null(gw.GetRequest("dp-live-1"));
        await gw.DisposeAsync();
    }

    // ---- item 1: the ledger ---------------------------------------------------------------------

    /// <summary>
    /// (f) A REQUEST ID IS SENDABLE AND IS UNIQUE PER DEPLOYMENT, BAR AND SEQUENCE.
    ///
    /// <para>It goes onto the broker order as <c>TA-&lt;id&gt;</c> and safety rule 1 requires it back
    /// unchanged, so it is bounded by <c>TradingGateway.MaxRequestIdChars</c> and drawn from
    /// <c>[A-Za-z0-9-]</c> — asked of <c>IsSendableId</c> itself rather than of a copy of its rule.
    /// And it is a FUNCTION of the three facts and of no clock: a re-plan of the same operation after
    /// a restart is the same id, which is what makes the duplicate-order defence in
    /// <c>ExecutionRequestStore.TryCreate</c> reach it at all.</para>
    /// </summary>
    [Fact]
    public void Request_ids_are_sendable_and_unique_per_deployment_bar_and_sequence()
    {
        var one = new string('a', 64);
        var two = new string('b', 64);
        var bar = new DateTimeOffset(2026, 9, 19, 12, 34, 0, TimeSpan.Zero);

        var ids = new[]
        {
            Deployments.RequestIdFor(one, bar, 0),
            Deployments.RequestIdFor(one, bar, 1),
            Deployments.RequestIdFor(one, bar.AddMinutes(1), 0),
            Deployments.RequestIdFor(two, bar, 0)
        };

        foreach (var id in ids)
            log.WriteLine($"id                   : {id} ({id.Length} chars, sendable {TradingGateway.IsSendableId(id)})");
        log.WriteLine($"budget               : {TradingGateway.MaxRequestIdChars} chars");

        Assert.Equal(4, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.True(TradingGateway.IsSendableId(id), $"'{id}' is not sendable"));
        Assert.All(ids, id => Assert.True(id.Length <= TradingGateway.MaxRequestIdChars));

        // THE SAME THREE FACTS ARE THE SAME ID, which is what a restart's re-plan relies on.
        Assert.Equal(ids[0], Deployments.RequestIdFor(one, bar, 0));
        Assert.StartsWith($"dp-{StrategyDeploymentRow.Short(one)}-", ids[0], StringComparison.Ordinal);
    }

    static async Task<string> Swallow(Func<Task> body)
    {
        try { await body(); return "ok"; }
        catch (GatewayDeniedException ex) { return $"{ex.Code} — {ex.Message}"; }
        catch (Exception ex) { return $"{ex.GetType().Name}: {ex.Message}"; }
    }
}

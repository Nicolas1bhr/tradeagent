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

    // ---- item 3: the policy ---------------------------------------------------------------------

    /// <summary>
    /// (a) ONE STANDING PAPER ALLOCATION STARTS ONE DEPLOYMENT, AND A SECOND WAITS WHILE THE
    /// ENVELOPE'S <c>max_deployments</c> IS FULL.
    ///
    /// <para>Three claims. The sweep starts ONE and running it again starts none — a policy on a clock
    /// that started a second run every tick would duplicate exposure by design. The envelope's own
    /// number is what bounds it, not a constant in this build. And an ENDED deployment whose last
    /// operation has no answer still occupies its slot: "a replacement waits for a flat, reconciled
    /// end", because an order that may be live at the platform is exactly what a second run would be
    /// placed on top of.</para>
    /// </summary>
    [Fact]
    public async Task A_standing_paper_allocation_starts_one_deployment_and_a_second_waits_while_max_deployments_is_full()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;

        var (envelope, version) = await Allocated(gw, db);

        var first = gw.StartPaperDeploymentsDue(At);
        var again = gw.StartPaperDeploymentsDue(At);
        var open = gw.Deployments.Open();

        Assert.Equal(1, first);
        Assert.Equal(0, again);
        Assert.Single(open);
        Assert.Equal(version, open[0].VersionId);
        Assert.Equal(envelope.Id, open[0].EnvelopeId);
        Assert.Equal(DeploymentState.Active, open[0].State);
        Assert.Null(open[0].CursorOpenTime);

        // END IT, LEAVING THE FLATTEN WITH NO ANSWER — the one outcome that is neither a fill nor a
        // refusal, and the one a replacement must wait on.
        await SeedAPosition(gw, conn);
        conn.Faults.DropBeforeBrokerAccept = 1;
        await gw.EndPaperDeploymentAsync(open[0].Id, "test: the owner stopped it");

        clock.At = At.AddMinutes(5);
        var whileUnreconciled = gw.StartPaperDeploymentsDue(clock.At);

        // NOW SETTLE IT, the way a person confirming on the Dashboard does, and the slot is free.
        var unresolved = gw.Deployments.OpsOf(open[0].Id).Single(o => !o.IsSettled);
        gw.ForceResolve(unresolved.RequestId, ExecutionState.CANCELLED, "the owner confirmed nothing landed");
        await gw.ReconcilePaperDeploymentsAsync(clock.At);

        clock.At = At.AddMinutes(10);
        var afterAFlatEnd = gw.StartPaperDeploymentsDue(clock.At);

        log.WriteLine($"first sweep          : {first}, second {again}");
        log.WriteLine($"while unreconciled   : {whileUnreconciled}");
        log.WriteLine($"after a flat end     : {afterAFlatEnd}");
        foreach (var d in gw.Deployments.All())
            log.WriteLine($"deployment           : {StrategyDeploymentRow.Short(d.Id)} {d.State} — {d.EndReason ?? "-"}");

        Assert.Equal(0, whileUnreconciled);
        Assert.Equal(1, afterAFlatEnd);
        Assert.Equal(2, gw.Deployments.All().Count);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// (b) AN OPERATION IS WRITTEN BEFORE IT IS DISPATCHED, AND A KILL BETWEEN THE WRITE AND THE
    /// ANSWER RESOLVES ON RESTART WITH NO SECOND ORDER AT THE WIRE.
    ///
    /// <para><b>The mutant.</b> Write the op AFTER the dispatch and this goes red: the restart finds no
    /// record of the flatten, cannot tell "sent and lost" from "never sent", and sends a second market
    /// order over a position the first one may already have closed. The write-ahead row is the whole
    /// of the difference, which is the reading <c>OpenPressRow</c> takes of an emergency press.</para>
    ///
    /// <para>UNKNOWN is never permission to retry. The op stays unresolved, the cursor does not move
    /// past its bar, and nothing is re-sent under its id.</para>
    /// </summary>
    [Fact]
    public async Task An_operation_written_before_dispatch_resolves_on_restart_with_no_second_order_at_the_wire()
    {
        var (gw, conn, db, _) = await Ready();
        using var _1 = db;

        await Allocated(gw, db);
        Assert.Equal(1, gw.StartPaperDeploymentsDue(At));
        var deployment = gw.Deployments.Open().Single();

        await SeedAPosition(gw, conn);
        var placesBefore = conn.Places;

        // THE KILL. The process stops INSIDE the connector call, between the write-ahead row and the
        // answer — which is the one window a record written afterwards would not cover. Nothing below
        // the wire call in this process runs again.
        var killed = new TaskCompletionSource();
        conn.Holds = RecordingConnector.HeldCall.Place;
        conn.Hold = killed.Task;
        var inFlight = gw.EndPaperDeploymentAsync(deployment.Id, "test: ended");
        await conn.Reached.Task;

        var flatten = gw.Deployments.OpsOf(deployment.Id)
            .Where(o => o.Kind == DeploymentOpKind.Flatten).ToList();
        var atTheWire = conn.Places - placesBefore;

        // THE RESTART: another gateway over the same database and the same book, with its own wire.
        var second = new RecordingConnector(new FakeConnector(conn.Broker));
        var (restarted, _, _, _) = await Ready(db: db, conn: second);
        var resolved = await restarted.ReconcilePaperDeploymentsAsync(At);
        var after = restarted.Deployments.OpsOf(deployment.Id)
            .Where(o => o.Kind == DeploymentOpKind.Flatten).ToList();

        log.WriteLine($"written before the wire: {flatten.Count} — {(flatten.Count == 1 ? flatten[0].State : "none")}");
        log.WriteLine($"orders at the wire   : {atTheWire} then {second.Places}");
        log.WriteLine($"after the restart    : {(after.Count == 1 ? after[0].State : "none")}, "
                      + $"resolved_at {(after.Count == 1 ? after[0].ResolvedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "none" : "none")}");
        log.WriteLine($"request state        : {(after.Count == 1 ? restarted.GetRequest(after[0].RequestId)?.State.ToString() ?? "no row" : "none")}");
        log.WriteLine($"position             : {conn.Broker.Positions.FirstOrDefault(p => p.Symbol == "BTCUSDT")?.Quantity ?? 0m}");
        log.WriteLine($"cursor               : {restarted.Deployments.ById(deployment.Id)!.CursorOpenTime?.ToString("O", CultureInfo.InvariantCulture) ?? "none"}");

        // THE RECORD EXISTS BEFORE THE ANSWER DOES. This is the whole claim.
        Assert.Single(flatten);
        Assert.Equal(DeploymentOpState.Dispatched, flatten[0].State);

        Assert.Equal(1, atTheWire);
        Assert.Equal(0, second.Places);
        Assert.Single(after);
        Assert.Equal(ExecutionState.UNKNOWN, restarted.GetRequest(after[0].RequestId)!.State);
        Assert.False(after[0].IsSettled);
        Assert.Null(after[0].ResolvedAt);
        Assert.Null(restarted.Deployments.ById(deployment.Id)!.CursorOpenTime);
        Assert.Equal(0, resolved);

        // AND NO REPLACEMENT WHILE IT IS UNRESOLVED. An operation nobody can account for is an order
        // that may be live, and a second run would be started on top of it.
        Assert.Equal(0, restarted.StartPaperDeploymentsDue(At.AddMinutes(5)));

        killed.SetResult();
        try { await inFlight; } catch (Exception) { /* the process this models did not come back */ }
        await restarted.DisposeAsync();
        await gw.DisposeAsync();
    }

    /// <summary>
    /// (c) A CONNECTOR, MODE OR ACCOUNT SWITCH SUSPENDS THE DEPLOYMENT AND NEVER RETARGETS IT.
    ///
    /// <para>Three arms, because an account id is unique only within a platform and the same platform
    /// and account are a different undertaking in LIVE than in PAPER — the reading
    /// <c>FlattenForBreachAsync</c> takes of a loss closure, arrived at from the other side. The
    /// identity columns are unchanged on every arm and nothing is sent on any of them.</para>
    ///
    /// <para><b>The mutant.</b> Read the MODE alone and the first and third arms go red: the
    /// deployment stays active while the gateway is operating a different platform and a different
    /// account, which is a run whose fills belong to nobody.</para>
    /// </summary>
    [Fact]
    public async Task A_connector_mode_or_account_switch_suspends_and_never_retargets()
    {
        // --- the account moved ---
        var (gw, conn, db, _) = await Ready();
        using var _1 = db;
        await Allocated(gw, db);
        Assert.Equal(1, gw.StartPaperDeploymentsDue(At));
        var started = gw.Deployments.Open().Single();

        gw.Update(s => s.SelectedAccountId = "SIM-OTHER");
        await gw.ReconcilePaperDeploymentsAsync(At);
        var onAnotherAccount = gw.Deployments.ById(started.Id)!;

        // and back again: a deployment resumes when the three match once more.
        gw.Update(s => s.SelectedAccountId = conn.Broker.AccountId);
        await gw.ReconcilePaperDeploymentsAsync(At);
        var resumed = gw.Deployments.ById(started.Id)!;

        // --- the mode moved ---
        gw.Update(s => s.Mode = TradingMode.LIVE_CONFIRM);
        await gw.ReconcilePaperDeploymentsAsync(At);
        var inALiveMode = gw.Deployments.ById(started.Id)!;
        gw.Update(s => s.Mode = TradingMode.PAPER);

        // --- the platform moved ---
        await gw.DisposeAsync();
        var (elsewhere, other, _, _) = await Ready(db: db, connectorId: "other-sim");
        await elsewhere.ReconcilePaperDeploymentsAsync(At);
        var onAnotherPlatform = elsewhere.Deployments.ById(started.Id)!;

        log.WriteLine($"another account      : {onAnotherAccount.State} — {onAnotherAccount.SuspendedReason}");
        log.WriteLine($"back again           : {resumed.State}");
        log.WriteLine($"a live mode          : {inALiveMode.State} — {inALiveMode.SuspendedReason}");
        log.WriteLine($"another platform     : {onAnotherPlatform.State} — {onAnotherPlatform.SuspendedReason}");
        log.WriteLine($"identity unchanged   : {onAnotherPlatform.ConnectorId}/{onAnotherPlatform.AccountId}/{onAnotherPlatform.Symbol}");
        log.WriteLine($"mutations            : {conn.Mutations} here, {other.Mutations} there");

        Assert.Equal(DeploymentState.Suspended, onAnotherAccount.State);
        Assert.Equal(DeploymentState.Active, resumed.State);
        Assert.Equal(DeploymentState.Suspended, inALiveMode.State);
        Assert.Equal(DeploymentState.Suspended, onAnotherPlatform.State);

        // NEVER RETARGETED. The seven identity facts are what the id is a hash of, and they are the
        // same seven the run started on.
        Assert.Equal(started.ConnectorId, onAnotherPlatform.ConnectorId);
        Assert.Equal(started.AccountId, onAnotherPlatform.AccountId);
        Assert.Equal(started.Symbol, onAnotherPlatform.Symbol);
        Assert.Equal(started.Mode, onAnotherPlatform.Mode);
        Assert.Equal(started.Id, onAnotherPlatform.ComputedId);

        Assert.Equal(0, conn.Mutations);
        Assert.Equal(0, other.Mutations);
        await elsewhere.DisposeAsync();
    }

    /// <summary>
    /// (d) ENDING CANCELS, FLATTENS THROUGH THE GATEWAY AND RECORDS THE REASON — WHILE THE ALLOCATION
    /// ROW SURVIVES.
    ///
    /// <para>The flatten is an ordinary <c>execution_request</c> placed under the deployment's own
    /// identity, so it passed every gate this gateway has rather than a shorter list kept for the
    /// app's own caller. And the ALLOCATION is untouched: a deployment is a run, an allocation is a
    /// decision, and ending the run must not quietly withdraw the owner's grant underneath it.</para>
    /// </summary>
    [Fact]
    public async Task Ending_cancels_flattens_through_the_gateway_and_records_the_reason_while_the_allocation_row_survives()
    {
        var (gw, conn, db, _) = await Ready();
        using var _1 = db;

        var (envelope, version) = await Allocated(gw, db);
        Assert.Equal(1, gw.StartPaperDeploymentsDue(At));
        var started = gw.Deployments.Open().Single();

        await SeedAPosition(gw, conn);
        var ended = await gw.EndPaperDeploymentAsync(started.Id, "test: the owner stopped it");

        var flatten = gw.Deployments.OpsOf(started.Id).Single(o => o.Kind == DeploymentOpKind.Flatten);
        var request = gw.GetRequest(flatten.RequestId)!;
        var standing = gw.Allocations.StandingForPaper(version, gw.Connector.Id, envelope.AccountId, At);

        log.WriteLine($"state                : {ended!.State} — {ended.EndReason}");
        log.WriteLine($"flatten op           : {flatten.RequestId} {flatten.State} — {flatten.Answer}");
        log.WriteLine($"the order it became  : {request.State}, session {request.AgentSessionId}");
        log.WriteLine($"position now         : {conn.Broker.Positions.FirstOrDefault(p => p.Symbol == "BTCUSDT")?.Quantity ?? 0m}");
        log.WriteLine($"allocation survives  : {standing is not null}");

        Assert.Equal(DeploymentState.Ended, ended.State);
        Assert.Equal("test: the owner stopped it", ended.EndReason);
        Assert.NotNull(ended.EndedAt);
        Assert.Equal(DeploymentOpState.Resolved, flatten.State);
        Assert.Equal(ExecutionState.FILLED, request.State);
        Assert.Equal($"deployment:{started.Id}", request.AgentSessionId);
        Assert.Equal(0m, conn.Broker.Positions.FirstOrDefault(p => p.Symbol == "BTCUSDT")?.Quantity ?? 0m);

        // THE ALLOCATION ROW SURVIVES, and so does the grant it was written under.
        Assert.NotNull(standing);
        Assert.NotNull(gw.Envelopes.ById(envelope.Id));
        await gw.DisposeAsync();
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

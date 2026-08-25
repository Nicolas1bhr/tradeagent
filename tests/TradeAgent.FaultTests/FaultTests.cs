using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace TradeAgent.Tests.Fault;

/// <summary>
/// The duplicate-order tests, which carry a positive control.
///
/// A test that cannot detect the failure it claims to prevent is not evidence. So each of these
/// first demonstrates that the harness DOES catch a duplicate when idempotency is switched off,
/// and only then asserts that the real path prevents it.
/// </summary>
public class DuplicateSubmissionTests
{
    [Fact]
    public async Task Control_the_harness_can_detect_a_duplicate_when_idempotency_is_off()
    {
        var (gw, conn, db) = await TestEnv.Ready(options: new GatewayOptions { IdempotencyEnabled = false });
        using var dbh = db;

        await gw.PlaceAsync(new AgentContext("a"), "same-id", TestEnv.Buy());
        await gw.PlaceAsync(new AgentContext("a"), "same-id", TestEnv.Buy());

        // Two orders at the broker from one request id. The harness sees it.
        Assert.Equal(2, conn.Broker.CountByClientOrderId(TradingGateway.ClientOrderIdFor("same-id")));
    }

    [Fact]
    public async Task A_repeated_request_id_never_places_a_second_order()
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var dbh = db;

        var first = await gw.PlaceAsync(new AgentContext("a"), "same-id", TestEnv.Buy());
        var second = await gw.PlaceAsync(new AgentContext("a"), "same-id", TestEnv.Buy());

        Assert.Equal(1, conn.Broker.CountByClientOrderId(TradingGateway.ClientOrderIdFor("same-id")));
        Assert.Single(conn.Broker.Orders);
        Assert.Equal(first.State, second.State);
        Assert.Equal(first.ClientOrderId, second.ClientOrderId);
    }

    [Fact]
    public async Task Concurrent_identical_requests_still_produce_one_order()
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var dbh = db;

        // Five racing callers, one request id. The unique constraint decides the winner, not luck.
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            gw.PlaceAsync(new AgentContext("a"), "race-1", TestEnv.Buy())));

        Assert.Single(conn.Broker.Orders);
        Assert.Equal(1, conn.Broker.CountByClientOrderId(TradingGateway.ClientOrderIdFor("race-1")));
    }

    [Fact]
    public async Task Recovery_after_a_lost_acknowledgement_does_not_create_a_second_order()
    {
        // The dangerous sequence: the broker has the order, we never heard back, and something has
        // to decide what to do next. The answer must never be "send it again".
        var (gw, conn, db) = await TestEnv.Ready(
            options: new GatewayOptions { AbsenceGrace = TimeSpan.Zero },
            faults: new FaultProfile { DropAfterBrokerAccept = 1 });
        using var dbh = db;

        var placed = await gw.PlaceAsync(new AgentContext("a"), "lost-ack", TestEnv.Buy());
        Assert.Equal(ExecutionState.UNKNOWN, placed.State);
        Assert.True(placed.NeedsReconciliation);

        var result = await gw.ReconcileAsync();

        Assert.True(result.Clean, string.Join("; ", result.Details));
        Assert.Equal(1, result.Resolved);
        Assert.Single(conn.Broker.Orders);   // still exactly one order at the broker
        Assert.Equal(ExecutionState.FILLED, gw.GetRequest("lost-ack")!.State);
        Assert.False(gw.GetRequest("lost-ack")!.NeedsReconciliation);
    }
}

/// <summary>Losing the connection at each dangerous moment, and proving what happens next.</summary>
public class DisconnectTests
{
    [Fact]
    public async Task An_unconfirmed_order_pauses_trading_until_it_is_confirmed()
    {
        var (gw, _, db) = await TestEnv.Ready(faults: new FaultProfile { DropAfterBrokerAccept = 1 });
        using var dbh = db;

        await gw.PlaceAsync(new AgentContext("a"), "pause-1", TestEnv.Buy());

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "pause-2", TestEnv.Buy()));
        Assert.Equal(ErrorCode.TRADING_PAUSED_UNRECONCILED, denied.Code);

        // ...and it resumes on its own once the truth is established.
        await gw.ReconcileAsync();
        var after = await gw.PlaceAsync(new AgentContext("a"), "pause-3", TestEnv.Buy());
        Assert.Equal(ExecutionState.FILLED, after.State);
    }

    [Fact]
    public async Task An_order_that_never_reached_the_broker_resolves_to_nothing_placed()
    {
        var (gw, conn, db) = await TestEnv.Ready(
            options: new GatewayOptions { AbsenceGrace = TimeSpan.Zero },
            faults: new FaultProfile { DropBeforeBrokerAccept = 1 });
        using var dbh = db;

        var placed = await gw.PlaceAsync(new AgentContext("a"), "never-landed", TestEnv.Buy());
        Assert.Equal(ExecutionState.UNKNOWN, placed.State);

        var result = await gw.ReconcileAsync();

        Assert.True(result.Clean);
        Assert.Empty(conn.Broker.Orders);
        Assert.Equal(ExecutionState.CANCELLED, gw.GetRequest("never-landed")!.State);
        Assert.Contains("never reached", gw.GetRequest("never-landed")!.LastError!);
    }

    [Fact]
    public async Task Absence_is_not_read_as_never_landed_while_the_grace_window_is_open()
    {
        // Positive control for the grace window: with a long grace, the same situation must stay
        // unresolved rather than be written off. A slow broker is not an absent order.
        var (gw, _, db) = await TestEnv.Ready(
            options: new GatewayOptions { AbsenceGrace = TimeSpan.FromMinutes(10) },
            faults: new FaultProfile { DropBeforeBrokerAccept = 1 });
        using var dbh = db;

        await gw.PlaceAsync(new AgentContext("a"), "grace-1", TestEnv.Buy());
        var result = await gw.ReconcileAsync();

        Assert.False(result.Clean);
        Assert.Equal(1, result.Inconclusive);
        Assert.True(gw.GetRequest("grace-1")!.NeedsReconciliation);
    }

    [Fact]
    public async Task While_the_connection_is_down_reconciliation_waits_instead_of_guessing()
    {
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { DropAfterBrokerAccept = 1 });
        using var dbh = db;

        await gw.PlaceAsync(new AgentContext("a"), "offline-1", TestEnv.Buy());
        conn.Faults.Disconnected = true;

        var result = await gw.ReconcileAsync();
        Assert.Equal(0, result.Resolved);
        Assert.Equal(1, result.Inconclusive);
        Assert.True(gw.GetRequest("offline-1")!.NeedsReconciliation);

        conn.Faults.Disconnected = false;
        Assert.True((await gw.ReconcileAsync()).Clean);
    }

    [Fact]
    public async Task A_backend_that_cannot_prove_its_own_history_never_auto_resolves()
    {
        // The safe direction to fail: unprovable stays unconfirmed, and a human is asked.
        var (gw, _, db) = await TestEnv.Ready(
            options: new GatewayOptions { AbsenceGrace = TimeSpan.Zero },
            faults: new FaultProfile { DropAfterBrokerAccept = 1, HideOrderHistory = true });
        using var dbh = db;

        await gw.PlaceAsync(new AgentContext("a"), "unprovable-1", TestEnv.Buy());

        for (var i = 0; i < 3; i++)
        {
            var r = await gw.ReconcileAsync();
            Assert.Equal(0, r.Resolved);
            Assert.Equal(1, r.Inconclusive);
        }
        Assert.True(gw.GetRequest("unprovable-1")!.NeedsReconciliation);

        // Only a person can settle it, and the override is recorded as theirs.
        gw.ForceResolve("unprovable-1", ExecutionState.FILLED, "checked in ATAS by hand");
        Assert.False(gw.GetRequest("unprovable-1")!.NeedsReconciliation);
        Assert.Contains("resolved by user", gw.GetRequest("unprovable-1")!.LastError!);
    }

    [Fact]
    public async Task A_dropped_connection_makes_execution_unavailable()
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var dbh = db;
        conn.Faults.Disconnected = true;
        await gw.RefreshHealthAsync();

        Assert.False(gw.TryAuthorizeExecution(new AgentContext("a"), out _));
        await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "down-1", TestEnv.Buy()));

        conn.Faults.Disconnected = false;
        await gw.RefreshHealthAsync();
        Assert.True(gw.TryAuthorizeExecution(new AgentContext("a"), out _));
    }
}

/// <summary>Killing the process mid-flight, and what the next start must conclude.</summary>
public class RestartTests
{
    [Fact]
    public async Task An_unconfirmed_order_survives_a_restart_and_still_blocks_trading()
    {
        var file = Path.Combine(TestEnv.Home, $"restart-{Guid.NewGuid():n}.db");
        var broker = new FakeBroker();

        // First run: an order is accepted by the broker, then the acknowledgement is lost and the
        // process dies before reconciling — the worst moment to be interrupted.
        using (var db = new Database(file))
        {
            var conn = new FakeConnector(broker, new FaultProfile { DropAfterBrokerAccept = 1 });
            var gw = new TradingGateway(db, conn, new HealthRegistry());
            gw.Update(s => { s.Mode = TradingMode.PAPER; s.SelectedAccountId = broker.AccountId; s.Risk.MaxNotionalPerOrder = 10_000_000m; });
            await conn.ConnectAsync();
            await gw.RefreshHealthAsync();
            await gw.PlaceAsync(new AgentContext("a"), "crash-1", TestEnv.Buy());
            Assert.Equal(ExecutionState.UNKNOWN, gw.GetRequest("crash-1")!.State);
        }

        // Second run: same records, same broker. Nothing may be resent, and nothing may be assumed.
        using (var db = new Database(file))
        {
            var conn = new FakeConnector(broker);
            var gw = new TradingGateway(db, conn, new HealthRegistry(), new GatewayOptions { AbsenceGrace = TimeSpan.Zero });
            await conn.ConnectAsync();
            await gw.RefreshHealthAsync();

            Assert.Single(gw.Requests.NeedingReconciliation());
            var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
                gw.PlaceAsync(new AgentContext("a"), "crash-2", TestEnv.Buy()));
            Assert.Equal(ErrorCode.TRADING_PAUSED_UNRECONCILED, denied.Code);

            Assert.True((await gw.ReconcileAsync()).Clean);
            Assert.Single(broker.Orders);            // recovery created no new order
            Assert.Equal(ExecutionState.FILLED, gw.GetRequest("crash-1")!.State);
        }
    }

    [Fact]
    public void A_second_gateway_refuses_to_share_the_installation()
    {
        var lockFile = Path.Combine(TestEnv.Home, $"lock-{Guid.NewGuid():n}.lock");
        var first = SingleInstanceLock.TryAcquire(lockFile);
        Assert.NotNull(first);

        // Two dispatchers over one account is how orders appear that nobody asked for.
        Assert.Null(SingleInstanceLock.TryAcquire(lockFile));

        first!.Dispose();
        var third = SingleInstanceLock.TryAcquire(lockFile);
        Assert.NotNull(third);
        third!.Dispose();
    }

    [Fact]
    public async Task Settings_survive_a_restart()
    {
        var file = Path.Combine(TestEnv.Home, $"settings-{Guid.NewGuid():n}.db");
        using (var db = new Database(file))
        {
            var gw = new TradingGateway(db, new FakeConnector(), new HealthRegistry());
            gw.SetMode(TradingMode.LIVE_CONFIRM);
            gw.ActivateLive(true);
            gw.Update(s => s.Risk.MaxOrderQuantity = 4m);
        }
        using (var db = new Database(file))
        {
            var gw = new TradingGateway(db, new FakeConnector(), new HealthRegistry());
            Assert.Equal(TradingMode.LIVE_CONFIRM, gw.Settings.Mode);
            Assert.True(gw.Settings.LiveActivated);
            Assert.Equal(4m, gw.Settings.Risk.MaxOrderQuantity);
        }
    }
}

/// <summary>
/// Concurrency against the store. The gateway reads and writes it from the connector's event stream,
/// a background loop and the UI thread at once, so "one shared SQLite connection" has to actually
/// hold up under that.
/// </summary>
public class StoreConcurrencyTests
{
    [Fact]
    public async Task Concurrent_reads_and_writes_do_not_corrupt_or_throw()
    {
        using var db = TestEnv.NewDb();
        var store = new ExecutionRequestStore(db);
        var log = new LogStore(db);
        var onboarding = new OnboardingStore(db);

        // Guarding only writes let a read race a live transaction; it surfaced as a
        // NullReferenceException inside the provider while closing the connection.
        var work = Enumerable.Range(0, 24).Select(i => Task.Run(() =>
        {
            var id = $"conc-{i}";
            store.TryCreate(new ExecutionRequest
            {
                RequestId = id, ConnectorId = "fake", AccountId = "SIM-001", Instrument = "ES",
                Intent = RequestIntent.PLACE, ParametersJson = "{}", ClientOrderId = $"TA-{id}",
                CreatedAt = DateTimeOffset.UtcNow, State = ExecutionState.CREATED, Mode = TradingMode.PAPER
            });
            store.Transition(id, ExecutionState.CREATED, ExecutionState.DISPATCHING);
            log.Activity($"worker {i}");
            log.Engineering("Test", "tick", requestId: id);
            onboarding.Complete(OnboardingStep.WELCOME);

            for (var r = 0; r < 6; r++)
            {
                _ = store.Get(id);
                _ = store.Open();
                _ = store.NeedingReconciliation();
                _ = log.RecentActivity(20);
                _ = onboarding.Current();
                _ = db.GetKv("settings");
            }
            store.Transition(id, ExecutionState.DISPATCHING, ExecutionState.FILLED);
        })).ToArray();

        await Task.WhenAll(work);

        Assert.Equal(24, store.Query().Count);
        Assert.All(store.Query(), r => Assert.Equal(ExecutionState.FILLED, r.State));
        Assert.Empty(store.NeedingReconciliation());
    }
}

/// <summary>Order outcomes that are definite, and must not be confused with the indefinite ones.</summary>
public class OrderOutcomeTests
{
    [Fact]
    public async Task A_broker_rejection_is_final_and_does_not_pause_trading()
    {
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { RejectNext = 1 });
        using var dbh = db;

        var r = await gw.PlaceAsync(new AgentContext("a"), "rej-1", TestEnv.Buy());

        Assert.Equal(ExecutionState.REJECTED, r.State);
        Assert.False(r.NeedsReconciliation);            // definite: nothing to reconcile
        Assert.Empty(conn.Broker.Positions);
        Assert.True(gw.TryAuthorizeExecution(new AgentContext("a"), out _));
    }

    [Fact]
    public async Task A_partial_fill_is_recorded_as_partial()
    {
        var (gw, _, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.PartialFill });
        using var dbh = db;

        var r = await gw.PlaceAsync(new AgentContext("a"), "part-1", TestEnv.Buy(qty: 2m));

        Assert.Equal(ExecutionState.PARTIALLY_FILLED, r.State);
        Assert.Equal(1m, r.FilledQuantity);
    }

    [Fact]
    public async Task A_working_order_can_be_cancelled_and_the_cancellation_is_recorded()
    {
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var dbh = db;

        var placed = await gw.PlaceAsync(new AgentContext("a"), "cx-1", TestEnv.Buy());
        Assert.Equal(ExecutionState.WORKING, placed.State);

        await gw.CancelAsync(new AgentContext("a"), "cx-1-cancel", placed.ConnectorOrderId!);
        Assert.Equal(ExecutionState.CANCELLED, conn.Broker.Orders.Single().State);
    }

    [Fact]
    public async Task A_stale_price_refuses_the_order_rather_than_sizing_from_a_memory()
    {
        var (gw, _, db) = await TestEnv.Ready(faults: new FaultProfile { QuoteAge = TimeSpan.FromMinutes(5) });
        using var dbh = db;

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "stale-1", TestEnv.Buy()));
        Assert.Equal(ErrorCode.MARKET_DATA_UNAVAILABLE, denied.Code);
    }
}

/// <summary>The human's switches, and the rule that no single button quietly liquidates a portfolio.</summary>
public class ControlTests
{
    [Fact]
    public async Task Stop_ai_trading_immediately_refuses_further_agent_execution()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var dbh = db;
        await gw.PlaceAsync(new AgentContext("a"), "pre-stop", TestEnv.Buy());

        gw.StopAiTrading("user pressed the button");

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "post-stop", TestEnv.Buy()));
        Assert.Equal(ErrorCode.AI_TRADING_STOPPED, denied.Code);

        gw.EnableAiTrading();
        Assert.Equal(ExecutionState.FILLED, (await gw.PlaceAsync(new AgentContext("a"), "after-enable", TestEnv.Buy())).State);
    }

    [Fact]
    public async Task Stopping_the_ai_does_not_touch_orders_or_positions()
    {
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var dbh = db;
        await gw.PlaceAsync(new AgentContext("a"), "keep-1", TestEnv.Buy());

        gw.StopAiTrading("test");

        // The kill switch removes authority, not money. Three separate controls, three separate effects.
        Assert.Equal(ExecutionState.WORKING, conn.Broker.Orders.Single().State);
    }

    [Fact]
    public async Task Cancel_all_removes_orders_but_leaves_positions_alone()
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var dbh = db;

        await gw.PlaceAsync(new AgentContext("a"), "pos-1", TestEnv.Buy());               // fills -> position
        conn.Faults.Fill = FillBehaviour.LeaveWorking;
        await gw.PlaceAsync(new AgentContext("a"), "work-1", TestEnv.Buy(symbol: "NQ")); // stays working

        await gw.OperatorCancelAllAsync();

        Assert.DoesNotContain(conn.Broker.Orders, o => o.State == ExecutionState.WORKING);
        Assert.NotEmpty(conn.Broker.Positions);   // cancel-all must never liquidate
    }

    [Fact]
    public async Task Close_all_is_a_separate_control_that_does_flatten_positions()
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var dbh = db;
        await gw.PlaceAsync(new AgentContext("a"), "flat-1", TestEnv.Buy());
        Assert.NotEmpty(conn.Broker.Positions);

        var closed = await gw.OperatorCloseAllAsync();

        Assert.Equal(1, closed);
        Assert.Empty(conn.Broker.Positions);
    }

    [Fact]
    public async Task Emergency_controls_still_work_after_the_kill_switch()
    {
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var dbh = db;
        await gw.PlaceAsync(new AgentContext("a"), "emg-1", TestEnv.Buy());
        gw.StopAiTrading("test");

        // Stopping the AI must not disarm the operator.
        await gw.OperatorCancelAllAsync();
        Assert.DoesNotContain(conn.Broker.Orders, o => o.State == ExecutionState.WORKING);
    }
}

/// <summary>Mode and limit gates: the numbers a person set, enforced before anything leaves the machine.</summary>
public class PolicyGateTests
{
    [Fact]
    public async Task Observe_mode_forbids_execution_entirely()
    {
        var (gw, _, db) = await TestEnv.Ready(s => s.Mode = TradingMode.OBSERVE);
        using var dbh = db;
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "obs-1", TestEnv.Buy()));
        Assert.Equal(ErrorCode.MODE_FORBIDS_EXECUTION, denied.Code);
    }

    [Fact]
    public async Task Paper_mode_refuses_to_send_an_order_to_a_real_money_account()
    {
        var db = TestEnv.NewDb();
        using var dbh = db;
        var conn = new FakeConnector(new FakeBroker { IsSimulated = false });
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s => { s.Mode = TradingMode.PAPER; s.SelectedAccountId = conn.Broker.AccountId; s.Risk.MaxNotionalPerOrder = 10_000_000m; });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "paper-live", TestEnv.Buy()));
        Assert.Equal(ErrorCode.MODE_ACCOUNT_MISMATCH, denied.Code);
        Assert.Empty(conn.Broker.Orders);
    }

    [Fact]
    public async Task Live_trading_requires_an_explicit_switch_and_not_merely_a_live_account()
    {
        var (gw, _, db) = await TestEnv.Ready(s => s.Mode = TradingMode.LIVE_AUTONOMOUS);
        using var dbh = db;

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "live-1", TestEnv.Buy()));
        Assert.Equal(ErrorCode.LIVE_NOT_ACTIVATED, denied.Code);

        gw.ActivateLive(true);
        Assert.Equal(ExecutionState.FILLED, (await gw.PlaceAsync(new AgentContext("a"), "live-2", TestEnv.Buy())).State);
    }

    [Fact]
    public async Task Leaving_live_mode_re_arms_the_safety()
    {
        var (gw, _, db) = await TestEnv.Ready(s => s.Mode = TradingMode.LIVE_AUTONOMOUS);
        using var dbh = db;
        gw.ActivateLive(true);
        gw.SetMode(TradingMode.PAPER);
        gw.SetMode(TradingMode.LIVE_AUTONOMOUS);

        // Going back to live must require the switch again, not silently remember consent.
        Assert.False(gw.Settings.LiveActivated);
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "rearm-1", TestEnv.Buy()));
        Assert.Equal(ErrorCode.LIVE_NOT_ACTIVATED, denied.Code);
    }

    [Fact]
    public async Task Autonomous_live_trading_is_refused_on_a_backend_that_cannot_prove_order_state()
    {
        var (gw, _, db) = await TestEnv.Ready(s => s.Mode = TradingMode.LIVE_AUTONOMOUS,
            faults: new FaultProfile { HideOrderHistory = true });
        using var dbh = db;
        gw.ActivateLive(true);

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "unprov-live", TestEnv.Buy()));
        Assert.Equal(ErrorCode.AUTONOMY_REQUIRES_PROVABLE_STATE, denied.Code);
    }

    [Fact]
    public async Task Confirm_mode_holds_the_order_until_a_person_approves_it()
    {
        var (gw, conn, db) = await TestEnv.Ready(s => s.Mode = TradingMode.LIVE_CONFIRM);
        using var dbh = db;
        gw.ActivateLive(true);

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "confirm-1", TestEnv.Buy()));
        Assert.Equal(ErrorCode.APPROVAL_REQUIRED, denied.Code);
        Assert.Empty(conn.Broker.Orders);
        Assert.Equal(ExecutionState.AWAITING_APPROVAL, gw.GetRequest("confirm-1")!.State);

        var approved = await gw.ApproveAsync("confirm-1");
        Assert.Equal(ExecutionState.FILLED, approved.State);
        Assert.Single(conn.Broker.Orders);
    }

    [Fact]
    public async Task A_declined_order_is_never_sent()
    {
        var (gw, conn, db) = await TestEnv.Ready(s => s.Mode = TradingMode.LIVE_CONFIRM);
        using var dbh = db;
        gw.ActivateLive(true);
        await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "decline-1", TestEnv.Buy()));

        gw.Decline("decline-1");
        Assert.Equal(ExecutionState.CANCELLED, gw.GetRequest("decline-1")!.State);
        Assert.Empty(conn.Broker.Orders);
    }

    [Theory]
    [InlineData("qty")]
    [InlineData("notional")]
    [InlineData("positions")]
    [InlineData("rate")]
    [InlineData("instrument")]
    public async Task Each_risk_limit_refuses_before_anything_reaches_the_broker(string limit)
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var dbh = db;

        switch (limit)
        {
            case "qty": gw.Update(s => s.Risk.MaxOrderQuantity = 1m); break;
            case "notional": gw.Update(s => s.Risk.MaxNotionalPerOrder = 10m); break;
            case "positions": gw.Update(s => s.Risk.MaxOpenPositions = 0); break;
            case "rate": gw.Update(s => s.Risk.MaxOrdersPerMinute = 0); break;
            case "instrument": gw.Update(s => s.Risk.InstrumentAllowlist.Add("MES")); break;
        }

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), $"risk-{limit}", TestEnv.Buy(qty: 5m)));
        Assert.Equal(ErrorCode.RISK_LIMIT_EXCEEDED, denied.Code);
        Assert.Empty(conn.Broker.Orders);
    }

    [Fact]
    public async Task The_rate_limit_counts_dispatches_rather_than_attempts()
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var dbh = db;
        gw.Update(s => s.Risk.MaxOrdersPerMinute = 2);

        await gw.PlaceAsync(new AgentContext("a"), "rate-1", TestEnv.Buy());
        await gw.PlaceAsync(new AgentContext("a"), "rate-2", TestEnv.Buy());
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "rate-3", TestEnv.Buy()));

        Assert.Equal(ErrorCode.RISK_LIMIT_EXCEEDED, denied.Code);
        Assert.Equal(2, conn.Broker.Orders.Count);

        // A repeat of an earlier request id is a replay, not a new order, so it must not be charged
        // against the rate limit.
        var replay = await gw.PlaceAsync(new AgentContext("a"), "rate-1", TestEnv.Buy());
        Assert.Equal(ExecutionState.FILLED, replay.State);
        Assert.Equal(2, conn.Broker.Orders.Count);
    }
}

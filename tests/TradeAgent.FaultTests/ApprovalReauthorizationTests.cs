using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// AN APPROVAL IS A DISPATCH DECISION AND MUST BE AUTHORIZED AT THE MOMENT IT IS MADE.
///
/// In LIVE_CONFIRM the AI's order is parked as AWAITING_APPROVAL after passing every gate, and a person
/// presses Approve later — minutes or hours later. Until this unit, ApproveAsync went straight to the
/// wire on whatever the world looked like when the order was parked: kill switch pressed since, mode
/// changed since, account cleared since, connection dead since, quote stale since, limits consumed
/// since — none of it was looked at. Every test here parks an order under good conditions, changes ONE
/// thing, and proves that Approve refuses with the exact reason, that nothing reaches the broker, and
/// that the record is in the documented state. Each then restores the condition and proves the same
/// approval goes through — a gate that also locks out legitimate approvals is a different bug, and one
/// that only "attack refused" would hide.
/// </summary>
public class ApprovalReauthorizationTests
{
    /// <summary>A gateway in LIVE_CONFIRM with real-money switched on, and one agent order parked.</summary>
    static async Task<(TradingGateway Gw, FakeConnector Conn, Database Db)> Parked(string requestId,
        Action<TradeAgentSettings>? settings = null, GatewayOptions? options = null, FaultProfile? faults = null,
        PlaceIntent? intent = null)
    {
        var env = await TestEnv.Ready(s => { s.Mode = TradingMode.LIVE_CONFIRM; settings?.Invoke(s); }, options, faults);
        env.Gw.ActivateLive(true);
        await Park(env.Gw, requestId, intent);
        return env;
    }

    static async Task Park(TradingGateway gw, string requestId, PlaceIntent? intent = null)
    {
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("agent-1"), requestId, intent ?? TestEnv.Buy()));
        Assert.Equal(ErrorCode.APPROVAL_REQUIRED, denied.Code);
        Assert.Equal(ExecutionState.AWAITING_APPROVAL, gw.GetRequest(requestId)!.State);
    }

    /// <summary>
    /// What TestEnv.Ready does, but over a database and a connector the caller owns, and in
    /// LIVE_CONFIRM with real money on. Needed to put TWO platforms over ONE store, which is the
    /// shape AppHost.SwitchConnectorAsync leaves behind: a new gateway and connector, the same
    /// database, and therefore the same parked requests.
    /// </summary>
    static async Task<TradingGateway> ReadyOver(Database db, ITradingConnector conn, string accountId,
        GatewayOptions? options = null)
    {
        var gw = new TradingGateway(db, conn, new HealthRegistry(), options);
        gw.Update(s =>
        {
            s.Mode = TradingMode.LIVE_CONFIRM;
            s.SelectedAccountId = accountId;
            s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments];
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        gw.ActivateLive(true);
        return gw;
    }

    /// <summary>The three facts every refusal must leave behind, asserted together so none can be skipped.</summary>
    static void AssertRefusedAndStillParked(GatewayDeniedException denied, ErrorCode expected, TradingGateway gw,
        FakeConnector conn, string requestId)
    {
        Assert.Equal(expected, denied.Code);
        Assert.Empty(conn.Broker.Orders);
        Assert.Equal(0, conn.Broker.CountByClientOrderId(TradingGateway.ClientOrderIdFor(requestId)));
        Assert.Equal(ExecutionState.AWAITING_APPROVAL, gw.GetRequest(requestId)!.State);
    }

    static void AssertDispatchedExactlyOnce(ExecutionRequest approved, FakeConnector conn, string requestId,
        ExecutionState expected = ExecutionState.FILLED)
    {
        Assert.Equal(expected, approved.State);
        Assert.Equal(1, conn.Broker.CountByClientOrderId(TradingGateway.ClientOrderIdFor(requestId)));
    }

    // ------------------------------------------------------------------ 1. positive control

    [Fact]
    public async Task Control_an_approval_under_unchanged_good_conditions_dispatches_exactly_once()
    {
        var (gw, conn, db) = await Parked("ok-1");
        using var dbh = db;

        var approved = await gw.ApproveAsync("ok-1");

        AssertDispatchedExactlyOnce(approved, conn, "ok-1");
        Assert.Single(conn.Broker.Orders);

        // Pressing Approve again is not a second order. The record is terminal, so it is refused.
        var again = await Assert.ThrowsAsync<GatewayDeniedException>(() => gw.ApproveAsync("ok-1"));
        Assert.Equal(ErrorCode.INVALID_REQUEST, again.Code);
        Assert.Single(conn.Broker.Orders);
    }

    [Fact]
    public async Task Control_a_resting_order_reaches_working_as_today()
    {
        var (gw, conn, db) = await Parked("ok-working", faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var dbh = db;

        var approved = await gw.ApproveAsync("ok-working");

        AssertDispatchedExactlyOnce(approved, conn, "ok-working", ExecutionState.WORKING);
    }

    // ------------------------------------------------------------------ 2. the world changed since parking

    [Fact]
    public async Task The_kill_switch_refuses_an_approval_because_the_order_is_the_ai_proposal()
    {
        var (gw, conn, db) = await Parked("ks-1");
        using var dbh = db;

        gw.StopAiTrading("you pressed STOP AI TRADING");

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() => gw.ApproveAsync("ks-1"));
        AssertRefusedAndStillParked(denied, ErrorCode.AI_TRADING_STOPPED, gw, conn, "ks-1");

        // The activity history says why in plain words, not in an error code, and does not claim
        // the order was approved.
        var last = gw.Log.RecentActivity().Last();
        Assert.Contains("AI trading is stopped", last.Text);
        Assert.DoesNotContain("AI_TRADING_STOPPED", last.Text);
        Assert.DoesNotContain("You approved", last.Text);
        Assert.Equal("warn", last.Level);

        // Two deliberate acts: re-enable, then approve. Both are the human's, and both are required.
        gw.EnableAiTrading();
        AssertDispatchedExactlyOnce(await gw.ApproveAsync("ks-1"), conn, "ks-1");
    }

    [Theory]
    [InlineData(TradingMode.PAPER)]
    [InlineData(TradingMode.OBSERVE)]
    [InlineData(TradingMode.LIVE_AUTONOMOUS)]
    public async Task A_mode_change_since_parking_refuses_the_approval_and_leaves_it_parked(TradingMode changedTo)
    {
        var (gw, conn, db) = await Parked($"mode-{changedTo}");
        using var dbh = db;

        gw.SetMode(changedTo);

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() => gw.ApproveAsync($"mode-{changedTo}"));
        AssertRefusedAndStillParked(denied, ErrorCode.MODE_FORBIDS_EXECUTION, gw, conn, $"mode-{changedTo}");

        // Back in the mode it was proposed under, with real money switched on again, the same approval works.
        gw.SetMode(TradingMode.LIVE_CONFIRM);
        gw.ActivateLive(true);
        AssertDispatchedExactlyOnce(await gw.ApproveAsync($"mode-{changedTo}"), conn, $"mode-{changedTo}");
    }

    [Fact]
    public async Task Switching_real_money_off_since_parking_refuses_the_approval()
    {
        var (gw, conn, db) = await Parked("live-off");
        using var dbh = db;

        gw.ActivateLive(false);

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() => gw.ApproveAsync("live-off"));
        AssertRefusedAndStillParked(denied, ErrorCode.LIVE_NOT_ACTIVATED, gw, conn, "live-off");

        gw.ActivateLive(true);
        AssertDispatchedExactlyOnce(await gw.ApproveAsync("live-off"), conn, "live-off");
    }

    [Fact]
    public async Task Clearing_the_chosen_account_since_parking_refuses_the_approval()
    {
        var (gw, conn, db) = await Parked("acct-none");
        using var dbh = db;

        gw.Update(s => s.SelectedAccountId = null);

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() => gw.ApproveAsync("acct-none"));
        AssertRefusedAndStillParked(denied, ErrorCode.ACCOUNT_NOT_FOUND, gw, conn, "acct-none");

        gw.Update(s => s.SelectedAccountId = conn.Broker.AccountId);
        AssertDispatchedExactlyOnce(await gw.ApproveAsync("acct-none"), conn, "acct-none");
    }

    /// <summary>
    /// DispatchPlaceAsync sends to the account the RECORD names, not the one currently chosen. If the
    /// owner changed accounts while the order waited, the two differ, and approving would trade an
    /// account nobody is looking at. The record is written straight into the store here because the
    /// simulator has one account, so the mismatch cannot be produced through the gateway.
    /// </summary>
    [Fact]
    public async Task An_order_parked_for_a_different_account_than_the_one_now_chosen_is_refused()
    {
        var (gw, conn, db) = await Parked("acct-same");
        using var dbh = db;
        var store = new ExecutionRequestStore(db);
        store.TryCreate(new ExecutionRequest
        {
            RequestId = "acct-other", AgentSessionId = "agent-1", ConnectorId = conn.Id, AccountId = "SIM-OTHER",
            Instrument = "ES", Intent = RequestIntent.PLACE, ParametersJson = Json.Write(TestEnv.Buy()),
            ClientOrderId = TradingGateway.ClientOrderIdFor("acct-other"), CreatedAt = DateTimeOffset.UtcNow,
            State = ExecutionState.AWAITING_APPROVAL, Mode = TradingMode.LIVE_CONFIRM
        });

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() => gw.ApproveAsync("acct-other"));
        AssertRefusedAndStillParked(denied, ErrorCode.ACCOUNT_NOT_FOUND, gw, conn, "acct-other");

        // The order parked for the chosen account is unaffected.
        AssertDispatchedExactlyOnce(await gw.ApproveAsync("acct-same"), conn, "acct-same");
    }

    /// <summary>
    /// AN ACCOUNT ID IS UNIQUE ONLY WITHIN A PLATFORM, SO THE PAIR IS WHAT IDENTIFIES THE MONEY.
    ///
    /// Switching platform in Settings disposes the gateway and builds a new one over the SAME
    /// database (AppHost.SwitchConnectorAsync:162-197, which also clears the chosen account because
    /// an id from one platform does not exist on the other). A request parked before the switch is
    /// therefore still sitting in the store afterwards. If the new platform happens to expose an
    /// account with the same id — "SIM-001" on a simulator and on a broker is not a contrived
    /// coincidence, it is what default ids look like — then comparing account ids alone lets a
    /// proposal made against a simulator dispatch to the real broker. The connector is compared too.
    /// </summary>
    [Fact]
    public async Task An_order_parked_on_a_different_platform_is_refused_even_when_the_account_id_matches()
    {
        var db = TestEnv.NewDb();
        using var dbh = db;

        var innerA = new FakeConnector(new FakeBroker());
        var innerB = new FakeConnector(new FakeBroker());
        Assert.Equal(innerA.Broker.AccountId, innerB.Broker.AccountId);   // the same id on both platforms
        var a = new ConnectorFacade(innerA, id: "platform-a");
        var b = new ConnectorFacade(innerB, id: "platform-b");

        // Parked on platform A, for account SIM-001 on platform A.
        var gwA = await ReadyOver(db, a, innerA.Broker.AccountId);
        await Park(gwA, "swap-1");
        Assert.Equal("platform-a", gwA.GetRequest("swap-1")!.ConnectorId);

        // The owner switches platform. Same database, same parked request, different wire.
        var gwB = await ReadyOver(db, b, innerB.Broker.AccountId);
        Assert.Equal(ExecutionState.AWAITING_APPROVAL, gwB.GetRequest("swap-1")!.State);

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() => gwB.ApproveAsync("swap-1"));
        Assert.Equal(ErrorCode.ACCOUNT_NOT_FOUND, denied.Code);
        Assert.Contains("platform-a", denied.Message);
        Assert.Contains("platform-b", denied.Message);

        // Nothing reached EITHER broker, and the request is still parked.
        Assert.Empty(innerB.Broker.Orders);
        Assert.Empty(innerA.Broker.Orders);
        Assert.Equal(ExecutionState.AWAITING_APPROVAL, gwB.GetRequest("swap-1")!.State);

        // Positive control: back on the platform it was proposed on, the same approval dispatches.
        AssertDispatchedExactlyOnce(await gwA.ApproveAsync("swap-1"), innerA, "swap-1");
        Assert.Empty(innerB.Broker.Orders);
    }

    [Fact]
    public async Task An_unconfirmed_order_that_appeared_since_parking_pauses_the_approval()
    {
        var (gw, conn, db) = await Parked("paused-1");
        using var dbh = db;
        var store = new ExecutionRequestStore(db);

        // Some other request went to the wire and never got an answer. Whatever its state, the flag
        // alone is what pauses trading (FaultTests: A_flagged_record_the_stream_already_settled...).
        store.TryCreate(new ExecutionRequest
        {
            RequestId = "flagged", AgentSessionId = "agent-1", ConnectorId = conn.Id, AccountId = conn.Broker.AccountId,
            Instrument = "NQ", Intent = RequestIntent.PLACE, ParametersJson = "{}",
            ClientOrderId = TradingGateway.ClientOrderIdFor("flagged"), CreatedAt = DateTimeOffset.UtcNow,
            State = ExecutionState.CREATED, Mode = TradingMode.LIVE_CONFIRM
        });
        store.Transition("flagged", ExecutionState.CREATED, ExecutionState.DISPATCHING);
        store.Transition("flagged", ExecutionState.DISPATCHING, ExecutionState.UNKNOWN, needsReconciliation: true,
            error: "connection lost");

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() => gw.ApproveAsync("paused-1"));
        AssertRefusedAndStillParked(denied, ErrorCode.TRADING_PAUSED_UNRECONCILED, gw, conn, "paused-1");

        // The human settles the unconfirmed one through the product's own override; then the approval works.
        gw.ForceResolve("flagged", ExecutionState.CANCELLED, "checked ATAS: no such order");
        Assert.Empty(store.NeedingReconciliation());
        AssertDispatchedExactlyOnce(await gw.ApproveAsync("paused-1"), conn, "paused-1");
    }

    [Fact]
    public async Task A_connection_that_died_since_parking_refuses_the_approval_instead_of_producing_an_unknown_order()
    {
        var (gw, conn, db) = await Parked("conn-1");
        using var dbh = db;

        conn.Faults.Disconnected = true;
        await gw.RefreshHealthAsync();
        Assert.NotEqual(HealthState.READY, gw.Health.Get(Components.TradingConnection).State);

        // Refused by the health gate — a GatewayDeniedException, not a transport failure from the wire
        // and not an UNKNOWN record that then pauses trading.
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() => gw.ApproveAsync("conn-1"));
        AssertRefusedAndStillParked(denied, ErrorCode.TRADING_PERMISSION_UNAVAILABLE, gw, conn, "conn-1");
        Assert.Empty(gw.Requests.NeedingReconciliation());

        conn.Faults.Disconnected = false;
        await gw.RefreshHealthAsync();
        AssertDispatchedExactlyOnce(await gw.ApproveAsync("conn-1"), conn, "conn-1");
    }

    [Fact]
    public async Task A_quote_gone_stale_since_parking_refuses_a_market_order_but_not_one_with_its_own_price()
    {
        var (gw, conn, db) = await Parked("stale-market");
        using var dbh = db;
        await Park(gw, "stale-limit", new PlaceIntent("ES", OrderSide.Buy, OrderType.Limit, 1m, 100m, null, TimeInForce.Day, null));

        conn.Faults.QuoteAge = TimeSpan.FromMinutes(5);

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() => gw.ApproveAsync("stale-market"));
        AssertRefusedAndStillParked(denied, ErrorCode.MARKET_DATA_UNAVAILABLE, gw, conn, "stale-market");

        // A limit order carries its own reference price, exactly as PlaceAsync treats it.
        AssertDispatchedExactlyOnce(await gw.ApproveAsync("stale-limit"), conn, "stale-limit");

        conn.Faults.QuoteAge = TimeSpan.Zero;
        AssertDispatchedExactlyOnce(await gw.ApproveAsync("stale-market"), conn, "stale-market");
    }

    [Fact]
    public async Task The_rate_limit_counts_the_approvals_dispatched_since_parking()
    {
        var (gw, conn, db) = await Parked("rate-1", s => s.Risk.MaxOrdersPerMinute = 2);
        using var dbh = db;
        await Park(gw, "rate-2");
        await Park(gw, "rate-3");

        // Parking charged nothing against the limit; dispatching does. Three quick approvals, limit two.
        AssertDispatchedExactlyOnce(await gw.ApproveAsync("rate-1"), conn, "rate-1");
        AssertDispatchedExactlyOnce(await gw.ApproveAsync("rate-2"), conn, "rate-2");

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() => gw.ApproveAsync("rate-3"));
        Assert.Equal(ErrorCode.RISK_LIMIT_EXCEEDED, denied.Code);
        Assert.Contains("per minute", denied.Message);
        Assert.Equal(2, conn.Broker.Orders.Count);
        Assert.Equal(0, conn.Broker.CountByClientOrderId(TradingGateway.ClientOrderIdFor("rate-3")));
        Assert.Equal(ExecutionState.AWAITING_APPROVAL, gw.GetRequest("rate-3")!.State);
    }

    [Fact]
    public async Task The_position_limit_counts_the_positions_opened_since_parking()
    {
        var (gw, conn, db) = await Parked("pos-es", s => s.Risk.MaxOpenPositions = 1);
        using var dbh = db;
        await Park(gw, "pos-nq", TestEnv.Buy(symbol: "NQ"));

        AssertDispatchedExactlyOnce(await gw.ApproveAsync("pos-nq"), conn, "pos-nq");
        Assert.Single(conn.Broker.Positions);

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() => gw.ApproveAsync("pos-es"));
        Assert.Equal(ErrorCode.RISK_LIMIT_EXCEEDED, denied.Code);
        Assert.Contains("positions", denied.Message);
        Assert.Single(conn.Broker.Orders);
        Assert.Equal(ExecutionState.AWAITING_APPROVAL, gw.GetRequest("pos-es")!.State);

        gw.Update(s => s.Risk.MaxOpenPositions = 2);
        AssertDispatchedExactlyOnce(await gw.ApproveAsync("pos-es"), conn, "pos-es");
    }

    [Fact]
    public async Task A_limit_tightened_since_parking_refuses_the_approval()
    {
        var (gw, conn, db) = await Parked("qty-1", intent: TestEnv.Buy(qty: 3m));
        using var dbh = db;

        gw.Update(s => s.Risk.MaxOrderQuantity = 1m);

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() => gw.ApproveAsync("qty-1"));
        AssertRefusedAndStillParked(denied, ErrorCode.RISK_LIMIT_EXCEEDED, gw, conn, "qty-1");

        gw.Update(s => s.Risk.MaxOrderQuantity = 3m);
        AssertDispatchedExactlyOnce(await gw.ApproveAsync("qty-1"), conn, "qty-1");
    }

    /// <summary>
    /// The allowlist is the owner's list of what the AI may touch at all. Narrowing it is the most
    /// direct way to say "not this instrument", and a parked order has to hear it: an approval that
    /// ignored the list would trade the one instrument the owner had just forbidden.
    /// </summary>
    [Fact]
    public async Task An_instrument_taken_off_the_allowlist_since_parking_refuses_the_approval()
    {
        var (gw, conn, db) = await Parked("allow-1");
        using var dbh = db;
        Assert.Empty(new RiskPolicy().InstrumentAllowlist);   // the default names nothing, so it allows nothing

        gw.Update(s => s.Risk.InstrumentAllowlist = ["NQ"]);   // ES is now off the list

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() => gw.ApproveAsync("allow-1"));
        AssertRefusedAndStillParked(denied, ErrorCode.RISK_LIMIT_EXCEEDED, gw, conn, "allow-1");
        Assert.Contains("allowed instrument list", denied.Message);

        gw.Update(s => s.Risk.InstrumentAllowlist = ["ES", "NQ"]);
        AssertDispatchedExactlyOnce(await gw.ApproveAsync("allow-1"), conn, "allow-1");
    }

    /// <summary>
    /// The notional cap is the only limit denominated in money, and it is the one whose arithmetic
    /// can silently under-count: ES is 50 units of face value per contract, so a cap compared
    /// against price × quantity alone would pass an order worth fifty times the limit. The cap is
    /// set between the two products, so only the multiplied value breaches it — on the approval
    /// path, which had no notional test of any kind.
    /// </summary>
    [Fact]
    public async Task The_notional_cap_on_an_approval_multiplies_by_contract_size()
    {
        var (gw, conn, db) = await Parked("notional-1");
        using var dbh = db;
        var contractSize = (await gw.InstrumentsAsync()).Single(i => i.Symbol == "ES").ContractSize!.Value;
        Assert.NotEqual(1m, contractSize);
        var price = FakeBroker.BasePrice("ES");

        gw.Update(s => s.Risk.MaxNotionalPerOrder = price * 10m);   // 1 < 10 < 50

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() => gw.ApproveAsync("notional-1"));
        AssertRefusedAndStillParked(denied, ErrorCode.RISK_LIMIT_EXCEEDED, gw, conn, "notional-1");
        Assert.Contains("order value", denied.Message);

        gw.Update(s => s.Risk.MaxNotionalPerOrder = price * contractSize + price);
        AssertDispatchedExactlyOnce(await gw.ApproveAsync("notional-1"), conn, "notional-1");
    }

    // ------------------------------------------------------------------ 2b. time-to-live

    /// <summary>A clock the test moves by hand. The gateway reads no other.</summary>
    sealed class TestClock : TimeProvider
    {
        DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public async Task An_approval_older_than_the_ttl_is_refused_and_the_request_is_declined_for_good()
    {
        var clock = new TestClock();
        var (gw, conn, db) = await Parked("ttl-inside", options: new GatewayOptions { Clock = clock });
        using var dbh = db;
        await Park(gw, "ttl-outside");
        var ttl = new GatewayOptions().ApprovalTtl;
        Assert.Equal(TimeSpan.FromMinutes(15), ttl);   // the default the design note states

        // One second inside the window: approvable.
        clock.Advance(ttl - TimeSpan.FromSeconds(1));
        AssertDispatchedExactlyOnce(await gw.ApproveAsync("ttl-inside"), conn, "ttl-inside");

        // Two seconds later, one second past it: refused, and declined through the state machine.
        clock.Advance(TimeSpan.FromSeconds(2));
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() => gw.ApproveAsync("ttl-outside"));
        Assert.Equal(ErrorCode.APPROVAL_EXPIRED, denied.Code);
        Assert.Equal(0, conn.Broker.CountByClientOrderId(TradingGateway.ClientOrderIdFor("ttl-outside")));
        Assert.Single(conn.Broker.Orders);

        var record = gw.GetRequest("ttl-outside")!;
        Assert.Equal(ExecutionState.CANCELLED, record.State);
        Assert.True(OrderStateMachine.IsTerminal(record.State));
        Assert.False(record.NeedsReconciliation);
        Assert.Contains("expired", record.LastError!);

        // Terminal means terminal: a second press cannot revive it.
        var again = await Assert.ThrowsAsync<GatewayDeniedException>(() => gw.ApproveAsync("ttl-outside"));
        Assert.Equal(ErrorCode.INVALID_REQUEST, again.Code);
        Assert.Single(conn.Broker.Orders);

        // What the AI sees on the wire: a replay of its request id returns the declined record rather
        // than "still waiting for approval", so it learns to propose again instead of waiting forever.
        var replay = await gw.PlaceAsync(new AgentContext("agent-1"), "ttl-outside", TestEnv.Buy());
        Assert.Equal(ExecutionState.CANCELLED, replay.State);
        Assert.Single(conn.Broker.Orders);

        // And the person is told in plain words, not in an error code.
        var line = gw.Log.RecentActivity().Last(a => a.Text.Contains("ttl-outside") || a.Text.Contains("declined"));
        Assert.DoesNotContain("APPROVAL_EXPIRED", line.Text);
        Assert.Equal("warn", line.Level);
    }

    [Fact]
    public async Task The_ttl_is_the_configured_option_not_a_constant()
    {
        var clock = new TestClock();
        var (gw, conn, db) = await Parked("ttl-short",
            options: new GatewayOptions { Clock = clock, ApprovalTtl = TimeSpan.FromMinutes(1) });
        using var dbh = db;

        clock.Advance(TimeSpan.FromSeconds(61));
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() => gw.ApproveAsync("ttl-short"));
        Assert.Equal(ErrorCode.APPROVAL_EXPIRED, denied.Code);
        Assert.Empty(conn.Broker.Orders);
        Assert.Equal(ExecutionState.CANCELLED, gw.GetRequest("ttl-short")!.State);
    }

    /// <summary>
    /// Kill switch on AND expired. The useful answer is "this one is dead", not "re-enable AI trading
    /// and try again" — which would walk the user straight back here for the same dead request, and
    /// leave a request nobody can ever dispatch sitting on the Dashboard as if it were live.
    /// </summary>
    [Fact]
    public async Task Expiry_is_judged_first_so_a_dead_request_is_not_left_parked_behind_another_refusal()
    {
        var clock = new TestClock();
        var (gw, conn, db) = await Parked("ttl-ks", options: new GatewayOptions { Clock = clock });
        using var dbh = db;
        gw.StopAiTrading("test");
        clock.Advance(TimeSpan.FromMinutes(16));

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() => gw.ApproveAsync("ttl-ks"));
        Assert.Equal(ErrorCode.APPROVAL_EXPIRED, denied.Code);
        Assert.Equal(ExecutionState.CANCELLED, gw.GetRequest("ttl-ks")!.State);
        Assert.Empty(conn.Broker.Orders);
    }

    /// <summary>
    /// A record timestamped in the FUTURE has a negative age, and no positive limit can ever exceed
    /// it, so under `age > ttl` such a request stayed approvable forever — the one state a
    /// time-to-live exists to make impossible. A clock stepped backwards between parking and
    /// approving is enough to produce it. Fail closed: an age that cannot be trusted is expired.
    /// </summary>
    [Fact]
    public async Task A_request_timestamped_in_the_future_is_expired_rather_than_approvable_forever()
    {
        var clock = new TestClock();
        var (gw, conn, db) = await Parked("future-control", options: new GatewayOptions { Clock = clock });
        using var dbh = db;

        new ExecutionRequestStore(db).TryCreate(new ExecutionRequest
        {
            RequestId = "future-1", AgentSessionId = "agent-1", ConnectorId = conn.Id,
            AccountId = conn.Broker.AccountId, Instrument = "ES", Intent = RequestIntent.PLACE,
            ParametersJson = Json.Write(TestEnv.Buy()),
            ClientOrderId = TradingGateway.ClientOrderIdFor("future-1"),
            CreatedAt = clock.GetUtcNow() + TimeSpan.FromHours(48),
            State = ExecutionState.AWAITING_APPROVAL, Mode = TradingMode.LIVE_CONFIRM
        });

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() => gw.ApproveAsync("future-1"));
        Assert.Equal(ErrorCode.APPROVAL_EXPIRED, denied.Code);
        Assert.Equal(0, conn.Broker.CountByClientOrderId(TradingGateway.ClientOrderIdFor("future-1")));
        Assert.Equal(ExecutionState.CANCELLED, gw.GetRequest("future-1")!.State);
        Assert.Contains("expired", gw.GetRequest("future-1")!.LastError!);

        // The person is told it could not be aged, not given a negative number of minutes.
        var line = gw.Log.RecentActivity().Last();
        Assert.DoesNotContain("-", line.Text);
        Assert.Equal("warn", line.Level);

        // A record with a sane timestamp on the same clock is untouched by this.
        AssertDispatchedExactlyOnce(await gw.ApproveAsync("future-control"), conn, "future-control");
    }

    /// <summary>
    /// ApprovalTtl is documented as literal, with no "0 = off". Under `age > ttl` a frozen clock
    /// leaves the age exactly zero, and a zero limit let it through — a limit of nothing permitting
    /// everything. `>=` is what makes the documented semantics true.
    /// </summary>
    [Fact]
    public async Task A_zero_ttl_expires_every_approval_including_one_made_at_the_same_instant()
    {
        var clock = new TestClock();
        var (gw, conn, db) = await Parked("ttl-zero",
            options: new GatewayOptions { Clock = clock, ApprovalTtl = TimeSpan.Zero });
        using var dbh = db;

        // The clock has not moved since the request was parked: age is exactly zero.
        Assert.Equal(clock.GetUtcNow(), gw.GetRequest("ttl-zero")!.CreatedAt);

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() => gw.ApproveAsync("ttl-zero"));
        Assert.Equal(ErrorCode.APPROVAL_EXPIRED, denied.Code);
        Assert.Empty(conn.Broker.Orders);
        Assert.Equal(ExecutionState.CANCELLED, gw.GetRequest("ttl-zero")!.State);
    }

    // ------------------------------------------------------------------ 2c. one clock

    /// <summary>
    /// BOTH ENDS OF A DURATION MUST COME FROM ONE CLOCK, OR THE SUBTRACTION MEANS NOTHING.
    ///
    /// `dispatched_at` is written by ExecutionRequestStore and the gateway subtracts it from its own
    /// clock to decide whether an order has been missing long enough for absence to mean it never
    /// landed. Until this unit the store wrote that timestamp from DateTimeOffset.UtcNow while the
    /// gateway read GatewayOptions.Clock, so the two were only comparable by the accident of both
    /// being the system clock. Substitute a clock — which the time-to-live tests must — and every
    /// order looked as old as the gap between the clocks the instant it was dispatched, skipping the
    /// grace window entirely. The clock here is moved hours away from the system clock BEFORE
    /// anything is written, which is exactly the condition that made the old code wrong.
    /// </summary>
    [Fact]
    public async Task The_reconcile_age_is_measured_on_the_gateways_clock_so_the_absence_grace_is_honoured()
    {
        var clock = new TestClock();
        clock.Advance(TimeSpan.FromHours(3));
        var (gw, conn, db) = await TestEnv.Ready(
            options: new GatewayOptions { Clock = clock },
            faults: new FaultProfile { DropBeforeBrokerAccept = 1 });
        using var dbh = db;
        var grace = new GatewayOptions().AbsenceGrace;
        Assert.Equal(TimeSpan.FromSeconds(15), grace);

        var placed = await gw.PlaceAsync(new AgentContext("a"), "grace-1", TestEnv.Buy());
        Assert.Equal(ExecutionState.UNKNOWN, placed.State);

        // The store wrote the dispatch time on the gateway's clock, not the system one.
        Assert.Equal(clock.GetUtcNow(), placed.DispatchedAt);
        Assert.True(placed.DispatchedAt > DateTimeOffset.UtcNow.AddHours(2));

        // Nothing has aged on that clock, so absence is not yet allowed to mean "never landed".
        var early = await gw.ReconcileAsync();
        Assert.False(early.Clean);
        Assert.Equal(0, early.Resolved);
        Assert.Contains(early.Details, d => d.Contains("grace window"));
        Assert.Equal(ExecutionState.RECONCILING, gw.GetRequest("grace-1")!.State);
        Assert.True(gw.GetRequest("grace-1")!.NeedsReconciliation);

        // Past the window on the same clock, the same absence is conclusive.
        clock.Advance(grace);
        var late = await gw.ReconcileAsync();
        Assert.True(late.Clean, string.Join("; ", late.Details));
        Assert.Equal(1, late.Resolved);
        Assert.Empty(conn.Broker.Orders);
        Assert.Equal(ExecutionState.CANCELLED, gw.GetRequest("grace-1")!.State);
        Assert.Contains("never reached", gw.GetRequest("grace-1")!.LastError!);
    }

    // ------------------------------------------------------------------ 3. gates the map listed as unpinned

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task G2_a_blank_request_id_is_refused_before_anything_else(string blank)
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var dbh = db;

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), blank, TestEnv.Buy()));
        Assert.Equal(ErrorCode.INVALID_REQUEST, denied.Code);
        Assert.Empty(conn.Broker.Orders);
        Assert.Empty(new ExecutionRequestStore(db).Query());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task G12_a_quantity_that_is_not_positive_is_refused(decimal qty)
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var dbh = db;

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), $"qty-{qty}", TestEnv.Buy(qty: qty)));
        Assert.Equal(ErrorCode.INVALID_REQUEST, denied.Code);
        Assert.Contains("greater than zero", denied.Message);
        Assert.Empty(conn.Broker.Orders);
    }

    /// <summary>
    /// The simulator's ES has ContractSize 50. A cap set between (price × qty) and (price × qty × 50)
    /// is breached only if the multiplication happens; a cap above both is the control.
    /// </summary>
    [Fact]
    public async Task G18_the_notional_cap_multiplies_by_contract_size()
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var dbh = db;
        var instruments = await gw.InstrumentsAsync();
        var contractSize = instruments.Single(i => i.Symbol == "ES").ContractSize!.Value;
        Assert.NotEqual(1m, contractSize);
        var price = FakeBroker.BasePrice("ES");   // the quote's Last, which is the reference for a market order

        gw.Update(s => s.Risk.MaxNotionalPerOrder = price * 10m);   // 1 < 10 < 50: only the multiplied value breaches it
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "notional-x", TestEnv.Buy()));
        Assert.Equal(ErrorCode.RISK_LIMIT_EXCEEDED, denied.Code);
        Assert.Contains("order value", denied.Message);
        Assert.Empty(conn.Broker.Orders);

        gw.Update(s => s.Risk.MaxNotionalPerOrder = price * contractSize + price);   // just above the multiplied value
        Assert.Equal(ExecutionState.FILLED, (await gw.PlaceAsync(new AgentContext("a"), "notional-ok", TestEnv.Buy())).State);
    }

    [Fact]
    public async Task G22_a_connector_that_cannot_modify_is_refused_before_the_order_is_looked_up()
    {
        var db = TestEnv.NewDb();
        using var dbh = db;
        var inner = new FakeConnector(new FakeBroker(), new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        var conn = new ConnectorFacade(inner, inner.Capabilities with { SupportsModify = false });
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s => { s.Mode = TradingMode.PAPER; s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments]; s.SelectedAccountId = inner.Broker.AccountId; s.Risk.MaxNotionalPerOrder = 10_000_000m; });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        var placed = await gw.PlaceAsync(new AgentContext("a"), "mod-target",
            new PlaceIntent("ES", OrderSide.Buy, OrderType.Limit, 1m, 100m, null, TimeInForce.Day, null));
        Assert.Equal(ExecutionState.WORKING, placed.State);

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.ModifyAsync(new AgentContext("a"), "mod-1", placed.ConnectorOrderId!, 2m, null, null));
        Assert.Equal(ErrorCode.TRADING_PERMISSION_UNAVAILABLE, denied.Code);
        Assert.Contains("cannot modify", denied.Message);
        Assert.Equal(1m, inner.Broker.Orders.Single().Quantity);
        Assert.Null(gw.GetRequest("mod-1"));   // refused before a record was even written
    }

    /// <summary>
    /// Forwards everything to the simulator except the two facts the gateway makes decisions on that
    /// FakeConnector hard-codes: the identity it reports and the capabilities it claims.
    /// </summary>
    sealed class ConnectorFacade(FakeConnector inner, ConnectorCapabilities? capabilities = null, string? id = null)
        : ITradingConnector
    {
        public string Id => id ?? inner.Id;
        public string DisplayName => inner.DisplayName;
        public ConnectorCapabilities Capabilities => capabilities ?? inner.Capabilities;
        public TimeSpan WorstCaseOperationPath => inner.WorstCaseOperationPath;
        public TimeSpan EmergencyBudget => inner.EmergencyBudget;

        public event Action<HealthState>? ConnectionChanged { add => inner.ConnectionChanged += value; remove => inner.ConnectionChanged -= value; }
        public event Action<QuoteInfo>? QuoteChanged { add => inner.QuoteChanged += value; remove => inner.QuoteChanged -= value; }
        public event Action<OrderInfo>? OrderChanged { add => inner.OrderChanged += value; remove => inner.OrderChanged -= value; }
        public event Action<ExecutionInfo>? ExecutionReceived { add => inner.ExecutionReceived += value; remove => inner.ExecutionReceived -= value; }
        public event Action<PositionInfo>? PositionChanged { add => inner.PositionChanged += value; remove => inner.PositionChanged -= value; }
        public event Action<AccountInfo>? AccountChanged { add => inner.AccountChanged += value; remove => inner.AccountChanged -= value; }

        public Task ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);
        public Task<HealthState> GetHealthAsync(CancellationToken ct = default) => inner.GetHealthAsync(ct);
        public Task<bool> IsConnectedAsync(CancellationToken ct = default) => inner.IsConnectedAsync(ct);
        public Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken ct = default) => inner.GetAccountsAsync(ct);
        public Task<AccountInfo?> GetAccountAsync(string accountId, CancellationToken ct = default) => inner.GetAccountAsync(accountId, ct);
        public Task<IReadOnlyList<InstrumentInfo>> GetInstrumentsAsync(CancellationToken ct = default) => inner.GetInstrumentsAsync(ct);
        public Task<QuoteInfo?> GetQuoteAsync(string symbol, CancellationToken ct = default) => inner.GetQuoteAsync(symbol, ct);
        public Task<IReadOnlyList<PositionInfo>> GetPositionsAsync(string accountId, CancellationToken ct = default) => inner.GetPositionsAsync(accountId, ct);
        public Task<IReadOnlyList<OrderInfo>> GetOrdersAsync(string accountId, bool includeInactive, DateTimeOffset? since, CancellationToken ct = default) => inner.GetOrdersAsync(accountId, includeInactive, since, ct);
        public Task<IReadOnlyList<ExecutionInfo>> GetExecutionsAsync(string accountId, DateTimeOffset? since, CancellationToken ct = default) => inner.GetExecutionsAsync(accountId, since, ct);
        public Task<OrderInfo> PlaceOrderAsync(PlaceOrderCommand cmd, CancellationToken ct = default) => inner.PlaceOrderAsync(cmd, ct);
        public Task<OrderInfo> ModifyOrderAsync(ModifyOrderCommand cmd, CancellationToken ct = default) => inner.ModifyOrderAsync(cmd, ct);
        public Task CancelOrderAsync(string connectorOrderId, CancellationToken ct = default) => inner.CancelOrderAsync(connectorOrderId, ct);
        public Task<IReadOnlyList<string>> CancelAllOrdersAsync(string accountId, CancellationToken ct = default) => inner.CancelAllOrdersAsync(accountId, ct);
        public Task<OrderInfo?> ClosePositionAsync(string accountId, string symbol, string clientOrderId, CancellationToken ct = default) => inner.ClosePositionAsync(accountId, symbol, clientOrderId, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

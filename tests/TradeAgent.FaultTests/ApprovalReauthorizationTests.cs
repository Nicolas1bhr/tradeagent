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
        var conn = new CapabilityOverride(inner, inner.Capabilities with { SupportsModify = false });
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s => { s.Mode = TradingMode.PAPER; s.SelectedAccountId = inner.Broker.AccountId; s.Risk.MaxNotionalPerOrder = 10_000_000m; });
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

    /// <summary>Forwards everything to the simulator except the capabilities it reports.</summary>
    sealed class CapabilityOverride(FakeConnector inner, ConnectorCapabilities capabilities) : ITradingConnector
    {
        public string Id => inner.Id;
        public string DisplayName => inner.DisplayName;
        public ConnectorCapabilities Capabilities => capabilities;

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

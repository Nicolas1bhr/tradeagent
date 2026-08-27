using TradeAgent.AtasBridge;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Atas;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// The real bridge code, the real connector, real pipes — with only the ATAS API replaced by
/// <see cref="LoopbackAtasAdapter"/>.
///
/// This is what shrinks the untested surface of the ATAS integration down to one file. Everything
/// between the agent and <see cref="IAtasAdapter"/> is exercised here; what remains unverified until
/// it runs on Windows is the adapter's mapping onto ATAS itself.
/// </summary>
public class BridgeRoundTripTests
{
    static string NewPipe() => "ta-brt-" + Guid.NewGuid().ToString("n")[..12];

    static async Task<(AtasConnector Conn, BridgeServer Bridge, LoopbackAtasAdapter Adapter)> ConnectedPair()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        await connector.ConnectAsync();

        var adapter = new LoopbackAtasAdapter();
        var bridge = new BridgeServer(adapter, pipe) { HeartbeatInterval = TimeSpan.FromMilliseconds(300) };
        bridge.Start();

        await Wait(async () => await connector.IsConnectedAsync());
        return (connector, bridge, adapter);
    }

    [Fact]
    public async Task The_bridge_and_the_connector_agree_on_the_handshake()
    {
        var (conn, bridge, _) = await ConnectedPair();
        await using var _1 = conn;
        await using var _2 = bridge;

        Assert.True(await conn.IsConnectedAsync());
        Assert.Equal(HealthState.READY, await conn.GetHealthAsync());
        Assert.True(conn.Capabilities.ReconciliationProvable);
        Assert.Equal("ATAS-LOOPBACK", conn.Bridge!.AccountId);
    }

    [Fact]
    public async Task Every_read_operation_crosses_the_bridge_intact()
    {
        var (conn, bridge, _) = await ConnectedPair();
        await using var _1 = conn;
        await using var _2 = bridge;

        Assert.Equal("ATAS-LOOPBACK", (await conn.GetAccountsAsync()).Single().Id);
        Assert.Equal("ES", (await conn.GetInstrumentsAsync()).Single().Symbol);
        var quote = await conn.GetQuoteAsync("ES");
        Assert.Equal(4300.25m, quote!.Ask);
        Assert.Empty(await conn.GetPositionsAsync("ATAS-LOOPBACK"));
        Assert.Empty(await conn.GetOrdersAsync("ATAS-LOOPBACK", true, null));
    }

    [Fact]
    public async Task An_order_placed_through_the_gateway_reaches_the_adapter_and_comes_back()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        await connector.ConnectAsync();
        var adapter = new LoopbackAtasAdapter();
        await using var bridge = new BridgeServer(adapter, pipe);
        bridge.Start();
        await Wait(async () => await connector.IsConnectedAsync());

        using var db = TestEnv.NewDb();
        await using var gw = new TradingGateway(db, connector, new HealthRegistry());
        gw.Update(s => { s.Mode = TradingMode.PAPER; s.SelectedAccountId = "ATAS-LOOPBACK"; s.Risk.MaxOrderQuantity = 5m; });
        await gw.RefreshHealthAsync();

        var placed = await gw.PlaceAsync(new AgentContext("agent"), "bridge-1",
            new PlaceIntent("ES", OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, null));

        Assert.Equal(ExecutionState.FILLED, placed.State);
        Assert.Equal(TradingGateway.ClientOrderIdFor("bridge-1"), adapter.GetOrders("ATAS-LOOPBACK", true, null).Single().ClientOrderId);
        Assert.Single(await connector.GetPositionsAsync("ATAS-LOOPBACK"));
    }

    [Fact]
    public async Task A_definite_rejection_survives_the_crossing_as_a_rejection()
    {
        var (conn, bridge, _) = await ConnectedPair();
        await using var _1 = conn;
        await using var _2 = bridge;

        // The adapter throws AtasRejectedException; the connector must see a rejection, NOT a
        // transport fault. Confusing the two is what makes a client resend a live order.
        await Assert.ThrowsAsync<ConnectorRejectedException>(() => conn.CancelOrderAsync("no-such-order"));
    }

    [Fact]
    public async Task Losing_the_bridge_surfaces_as_indefinite_rather_than_as_a_rejection()
    {
        var (conn, bridge, _) = await ConnectedPair();
        await using var _1 = conn;

        // ATAS closing, or the strategy being stopped, is the ordinary indefinite failure. It must
        // never arrive as ConnectorRejectedException: the gateway treats a rejection as final and
        // would write off an order that might still be live.
        await bridge.DisposeAsync();
        await Wait(async () => !await conn.IsConnectedAsync());

        var ex = await Record.ExceptionAsync(() => conn.GetAccountsAsync());
        Assert.IsType<ConnectorTransportException>(ex);
        Assert.Equal(HealthState.FAILED, await conn.GetHealthAsync());
    }

    [Fact]
    public async Task Reconciliation_works_across_the_bridge_after_a_lost_acknowledgement()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromMilliseconds(700));
        await connector.ConnectAsync();
        var adapter = new LoopbackAtasAdapter();
        var bridge = new BridgeServer(adapter, pipe);
        bridge.Start();
        await Wait(async () => await connector.IsConnectedAsync());

        using var db = TestEnv.NewDb();
        await using var gw = new TradingGateway(db, connector, new HealthRegistry(),
            new GatewayOptions { AbsenceGrace = TimeSpan.Zero });
        gw.Update(s => { s.Mode = TradingMode.PAPER; s.SelectedAccountId = "ATAS-LOOPBACK"; s.Risk.MaxOrderQuantity = 5m; });
        await gw.RefreshHealthAsync();

        // The order lands in the adapter, then the bridge dies before the reply is read: exactly the
        // "ATAS received it, we never heard back" case, across the real transport this time.
        var placed = await gw.PlaceAsync(new AgentContext("agent"), "bridge-lost",
            new PlaceIntent("ES", OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, null));
        Assert.Equal(ExecutionState.FILLED, placed.State);

        adapter.Place(new PlaceOrderCommand(TradingGateway.ClientOrderIdFor("bridge-orphan"), "ATAS-LOOPBACK",
            "ES", OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, "placed behind our back"));

        // A request we recorded as unconfirmed, whose order does exist at the far end.
        var store = gw.Requests;
        store.TryCreate(new ExecutionRequest
        {
            RequestId = "bridge-orphan", ConnectorId = "atas", AccountId = "ATAS-LOOPBACK", Instrument = "ES",
            Intent = RequestIntent.PLACE, ParametersJson = "{}",
            ClientOrderId = TradingGateway.ClientOrderIdFor("bridge-orphan"),
            CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-30), State = ExecutionState.CREATED, Mode = TradingMode.PAPER
        });
        store.Transition("bridge-orphan", ExecutionState.CREATED, ExecutionState.DISPATCHING);
        store.Transition("bridge-orphan", ExecutionState.DISPATCHING, ExecutionState.UNKNOWN, needsReconciliation: true);

        var result = await gw.ReconcileAsync();

        Assert.True(result.Clean, string.Join("; ", result.Details));
        Assert.Equal(ExecutionState.FILLED, gw.GetRequest("bridge-orphan")!.State);
        Assert.Equal(2, adapter.GetOrders("ATAS-LOOPBACK", true, null).Count); // nothing was resent
        await bridge.DisposeAsync();
    }

    [Fact]
    public async Task The_bridge_reconnects_by_itself_after_TradeAgent_restarts()
    {
        var pipe = NewPipe();
        var adapter = new LoopbackAtasAdapter();
        await using var bridge = new BridgeServer(adapter, pipe) { ReconnectDelay = TimeSpan.FromMilliseconds(200) };
        bridge.Start();

        // First "TradeAgent" comes and goes.
        var first = new AtasConnector(pipe, TimeSpan.FromSeconds(5));
        await first.ConnectAsync();
        await Wait(async () => await first.IsConnectedAsync());
        await first.DisposeAsync();

        // A second one starts later; ATAS was never touched, and the bridge finds it again.
        await using var second = new AtasConnector(pipe, TimeSpan.FromSeconds(5));
        await second.ConnectAsync();
        await Wait(async () => await second.IsConnectedAsync(), 15_000);
        Assert.Equal("ATAS-LOOPBACK", (await second.GetAccountsAsync()).Single().Id);
    }

    /// <summary>
    /// An adapter that behaves the way the real one does about rule 1: it will not claim to carry a
    /// client order id until it has actually seen one come back off a placed order.
    /// <see cref="LoopbackAtasAdapter"/> reports a static true, which is exactly the case that hid
    /// this bug — a capability that is true from the first frame never has to travel.
    /// </summary>
    sealed class ProvesOnFirstOrder(LoopbackAtasAdapter inner) : IAtasAdapter
    {
        bool _proven;
        int _attempts, _checks;

        public BridgeHello Describe()
        {
            var h = inner.Describe();
            h.SupportsClientOrderId = _proven;
            // Counted the way AtasStrategyAdapter counts them: the attempt when the order goes out,
            // the check when the read-back is actually performed.
            h.ClientOrderIdAttempts = _attempts;
            h.ClientOrderIdChecks = _checks;
            return h;
        }

        public OrderInfo Place(PlaceOrderCommand cmd)
        {
            _attempts++;
            var o = inner.Place(cmd);
            _checks++;
            _proven = true;          // the id came back off the order, as AtasStrategyAdapter requires
            return o;
        }

        public IReadOnlyList<AccountInfo> GetAccounts() => inner.GetAccounts();
        public IReadOnlyList<InstrumentInfo> GetInstruments() => inner.GetInstruments();
        public QuoteInfo? GetQuote(string symbol) => inner.GetQuote(symbol);
        public IReadOnlyList<PositionInfo> GetPositions(string a) => inner.GetPositions(a);
        public IReadOnlyList<OrderInfo> GetOrders(string a, bool i, DateTimeOffset? s) => inner.GetOrders(a, i, s);
        public IReadOnlyList<ExecutionInfo> GetExecutions(string a, DateTimeOffset? s) => inner.GetExecutions(a, s);
        public OrderInfo Modify(ModifyOrderCommand cmd) => inner.Modify(cmd);
        public void Cancel(string id) => inner.Cancel(id);
        public IReadOnlyList<string> CancelAll(string a) => inner.CancelAll(a);
        public OrderInfo? ClosePosition(string a, string sym, string cid) => inner.ClosePosition(a, sym, cid);

        public event Action<bool>? ConnectionChanged { add => inner.ConnectionChanged += value; remove => inner.ConnectionChanged -= value; }
        public event Action<QuoteInfo>? QuoteChanged { add => inner.QuoteChanged += value; remove => inner.QuoteChanged -= value; }
        public event Action<OrderInfo>? OrderChanged { add => inner.OrderChanged += value; remove => inner.OrderChanged -= value; }
        public event Action<ExecutionInfo>? ExecutionReceived { add => inner.ExecutionReceived += value; remove => inner.ExecutionReceived -= value; }
        public event Action<PositionInfo>? PositionChanged { add => inner.PositionChanged += value; remove => inner.PositionChanged -= value; }
        public event Action<AccountInfo>? AccountChanged { add => inner.AccountChanged += value; remove => inner.AccountChanged -= value; }
    }

    /// <summary>
    /// A capability that only becomes true after the handshake must still reach the connector.
    ///
    /// The bridge used to send Describe() exactly once, on Hello. Rule 1 makes SupportsClientOrderId
    /// false until a placed order has proved it, so the proof arrived strictly after the only moment
    /// anyone read it: the gateway refused LIVE_AUTONOMOUS for the entire life of the connection, and
    /// the staged trial could not reach its final step without restarting ATAS. Against that code
    /// this test fails on the last two assertions.
    /// </summary>
    [Fact]
    public async Task A_capability_proved_after_the_handshake_reaches_the_connector()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        await connector.ConnectAsync();
        await using var _1 = connector;

        var adapter = new ProvesOnFirstOrder(new LoopbackAtasAdapter());
        await using var bridge = new BridgeServer(adapter, pipe) { HeartbeatInterval = TimeSpan.FromMilliseconds(150) };
        bridge.Start();
        await Wait(async () => await connector.IsConnectedAsync());

        // Nothing has been placed, so the platform has confirmed nothing and autonomy stays refused.
        Assert.False(connector.Capabilities.SupportsClientOrderId);
        Assert.False(connector.Capabilities.ReconciliationProvable);

        await connector.PlaceOrderAsync(new PlaceOrderCommand(
            TradingGateway.ClientOrderIdFor("proves-it"), "ATAS-LOOPBACK", "ES",
            OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, null));

        // Same connection throughout — no reconnect, no restart.
        await Wait(async () => await Task.FromResult(connector.Capabilities.SupportsClientOrderId));
        Assert.True(connector.Capabilities.ReconciliationProvable);
        Assert.True(await connector.IsConnectedAsync());
    }

    /// <summary>Answers the handshake, then fails every later ask — ATAS's own properties throwing.</summary>
    sealed class ThrowsAfterHandshake(LoopbackAtasAdapter inner) : IAtasAdapter
    {
        int _calls;

        public BridgeHello Describe() =>
            Interlocked.Increment(ref _calls) == 1 ? inner.Describe() : throw new InvalidOperationException("ATAS is not ready");

        public IReadOnlyList<AccountInfo> GetAccounts() => inner.GetAccounts();
        public IReadOnlyList<InstrumentInfo> GetInstruments() => inner.GetInstruments();
        public QuoteInfo? GetQuote(string symbol) => inner.GetQuote(symbol);
        public IReadOnlyList<PositionInfo> GetPositions(string a) => inner.GetPositions(a);
        public IReadOnlyList<OrderInfo> GetOrders(string a, bool i, DateTimeOffset? s) => inner.GetOrders(a, i, s);
        public IReadOnlyList<ExecutionInfo> GetExecutions(string a, DateTimeOffset? s) => inner.GetExecutions(a, s);
        public OrderInfo Place(PlaceOrderCommand cmd) => inner.Place(cmd);
        public OrderInfo Modify(ModifyOrderCommand cmd) => inner.Modify(cmd);
        public void Cancel(string id) => inner.Cancel(id);
        public IReadOnlyList<string> CancelAll(string a) => inner.CancelAll(a);
        public OrderInfo? ClosePosition(string a, string sym, string cid) => inner.ClosePosition(a, sym, cid);

        public event Action<bool>? ConnectionChanged { add => inner.ConnectionChanged += value; remove => inner.ConnectionChanged -= value; }
        public event Action<QuoteInfo>? QuoteChanged { add => inner.QuoteChanged += value; remove => inner.QuoteChanged -= value; }
        public event Action<OrderInfo>? OrderChanged { add => inner.OrderChanged += value; remove => inner.OrderChanged -= value; }
        public event Action<ExecutionInfo>? ExecutionReceived { add => inner.ExecutionReceived += value; remove => inner.ExecutionReceived -= value; }
        public event Action<PositionInfo>? PositionChanged { add => inner.PositionChanged += value; remove => inner.PositionChanged -= value; }
        public event Action<AccountInfo>? AccountChanged { add => inner.AccountChanged += value; remove => inner.AccountChanged -= value; }
    }

    /// <summary>
    /// Reading the capabilities must never be able to cost the heartbeat.
    ///
    /// Describe() reaches into ATAS's own Portfolio and Connector properties. The heartbeat loop
    /// catches by returning, so a throw there would stop the pulse and TradeAgent would declare a
    /// perfectly healthy bridge dead once the heartbeat timeout expired — a worse failure than the
    /// stale capability this frame exists to fix. A failed read must degrade to the plain pulse.
    /// </summary>
    [Fact]
    public async Task A_failing_capability_read_does_not_stop_the_heartbeat()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10)) { HeartbeatTimeout = TimeSpan.FromMilliseconds(600) };
        await connector.ConnectAsync();
        await using var _1 = connector;

        await using var bridge = new BridgeServer(new ThrowsAfterHandshake(new LoopbackAtasAdapter()), pipe)
            { HeartbeatInterval = TimeSpan.FromMilliseconds(100) };
        bridge.Start();
        await Wait(async () => await connector.IsConnectedAsync());

        // Well past several heartbeat timeouts, every one of which failed to read Describe().
        await Task.Delay(1500);

        Assert.True(await connector.IsConnectedAsync());
        Assert.Equal(HealthState.READY, await connector.GetHealthAsync());
        // The handshake's answer is retained rather than lost or invented.
        Assert.Equal("ATAS-LOOPBACK", connector.Bridge!.AccountId);
        Assert.True(connector.Capabilities.ReconciliationProvable);
    }

    /// <summary>
    /// A false SupportsClientOrderId must say WHICH false it is.
    ///
    /// The protocol carries one boolean for rule 1, and false is three different facts: nothing was
    /// ever attempted, something was attempted but never came back to be checked, or the read-back
    /// ran and failed. Only the last is evidence against ATAS, and it is the one that decides
    /// whether this product may ever trade unattended. Before the counters existed the probe had to
    /// INFER which case it was from the live order book, and labelled its own verdict inferred.
    /// </summary>
    [Fact]
    public async Task Why_a_client_order_id_is_unproven_travels_with_the_answer()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        await connector.ConnectAsync();
        await using var _1 = connector;

        await using var bridge = new BridgeServer(new ProvesOnFirstOrder(new LoopbackAtasAdapter()), pipe)
            { HeartbeatInterval = TimeSpan.FromMilliseconds(150) };
        bridge.Start();
        await Wait(async () => await connector.IsConnectedAsync());

        // NEVER ATTEMPTED. False here says nothing whatever about ATAS.
        Assert.False(connector.Capabilities.SupportsClientOrderId);
        Assert.Equal(0, connector.Bridge!.ClientOrderIdAttempts);
        Assert.Equal(0, connector.Bridge!.ClientOrderIdChecks);

        await connector.PlaceOrderAsync(new PlaceOrderCommand(
            TradingGateway.ClientOrderIdFor("counts-it"), "ATAS-LOOPBACK", "ES",
            OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, null));

        // ATTEMPTED AND CHECKED — and the count reaches the connector on the same refreshed frame
        // that carries the capability, not only at a handshake that already happened.
        await Wait(async () => await Task.FromResult(connector.Bridge!.ClientOrderIdAttempts == 1));
        Assert.Equal(1, connector.Bridge!.ClientOrderIdChecks);
        Assert.True(connector.Capabilities.SupportsClientOrderId);
    }

    /// <summary>
    /// A bridge that does not report the counters must not read as one that attempted nothing.
    ///
    /// Null and zero are different claims: "I do not keep this count" against "I placed no orders".
    /// Collapsing them would rebuild, one field lower down, exactly the ambiguity the counters were
    /// added to remove — and it would do it silently, on any bridge older than this change.
    /// </summary>
    [Fact]
    public async Task A_bridge_that_reports_no_counters_is_not_a_bridge_reporting_zero()
    {
        var (conn, bridge, _) = await ConnectedPair();
        await using var _1 = conn;
        await using var _2 = bridge;

        // LoopbackAtasAdapter sets neither counter.
        Assert.Null(conn.Bridge!.ClientOrderIdAttempts);
        Assert.Null(conn.Bridge!.ClientOrderIdChecks);
    }

    /// <summary>
    /// A bridge speaking the wrong protocol version is still allowed to say which version it is —
    /// and still allowed nothing else.
    ///
    /// Refusing the hello outright was right about the capabilities and wrong about the user: the
    /// screen read "FAILED" with no number on it, and repairing a version mismatch starts with
    /// knowing what is loaded. This asserts both halves at once, because the dangerous fix is the
    /// one that keeps the version by keeping the whole frame.
    /// </summary>
    [Fact]
    public async Task An_incompatible_bridge_names_its_version_and_gains_nothing_by_it()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        await connector.ConnectAsync();
        await using var _1 = connector;

        await using var client = new System.IO.Pipes.NamedPipeClientStream(
            ".", pipe, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        await client.ConnectAsync(10_000);
        await using var w = new StreamWriter(client, new System.Text.UTF8Encoding(false)) { AutoFlush = true };

        var hello = new BridgeHello
        {
            BridgeProtocolVersion = Versions.BridgeProtocolVersion + 1,
            BridgeVersion = "9.9.9",
            AtasVersion = "6.1.2.3",
            // Everything an over-trusting connector could be talked into believing.
            SupportsClientOrderId = true,
            SupportsOrderHistory = true,
            SupportsModify = true,
            SupportsClosePosition = true
        };
        await w.WriteLineAsync(Json.Write(new BridgeFrame
        {
            Op = BridgeOps.Hello,
            Data = System.Text.Json.JsonSerializer.SerializeToElement(hello, Json.Options)
        }));

        await Wait(async () => await Task.FromResult(connector.Incompatible is not null));

        // The identity survives, and it is enough to act on.
        var bad = connector.Incompatible!;
        Assert.Equal(Versions.BridgeProtocolVersion + 1, bad.ReportedProtocolVersion);
        Assert.Equal(Versions.BridgeProtocolVersion, bad.ExpectedProtocolVersion);
        Assert.Equal("9.9.9", bad.BridgeVersion);
        Assert.Contains("9.9.9", connector.StatusDetail);

        // The claims do not. Not one of the four capabilities it asserted got through, the
        // connection is not up, and nothing may be traded on it.
        Assert.Null(connector.Bridge);
        Assert.False(connector.Capabilities.SupportsClientOrderId);
        Assert.False(connector.Capabilities.SupportsOrderHistory);
        Assert.False(connector.Capabilities.ReconciliationProvable);
        Assert.False(await connector.IsConnectedAsync());
    }

    /// <summary>
    /// When the incompatible bridge goes away, the row that explained it must be told.
    ///
    /// An incompatible bridge never sets _connected, so the disconnect path's "was it up?" guard is
    /// false and fires nothing. The reason string is cleared regardless — which would leave the
    /// dashboard displaying a version mismatch for a bridge no longer on the pipe. On a status
    /// display, the model and the screen disagreeing IS the bug.
    /// </summary>
    [Fact]
    public async Task When_an_incompatible_bridge_disconnects_the_status_row_is_told()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        var failures = 0;
        connector.ConnectionChanged += s => { if (s == HealthState.FAILED) Interlocked.Increment(ref failures); };
        await connector.ConnectAsync();
        await using var _1 = connector;

        var client = new System.IO.Pipes.NamedPipeClientStream(
            ".", pipe, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        await client.ConnectAsync(10_000);
        var w = new StreamWriter(client, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        await w.WriteLineAsync(Json.Write(new BridgeFrame
        {
            Op = BridgeOps.Hello,
            Data = System.Text.Json.JsonSerializer.SerializeToElement(
                new BridgeHello { BridgeProtocolVersion = Versions.BridgeProtocolVersion + 1, BridgeVersion = "9.9.9" },
                Json.Options)
        }));
        await Wait(async () => await Task.FromResult(connector.Incompatible is not null));
        var afterHello = Volatile.Read(ref failures);

        // The bridge goes away.
        await w.DisposeAsync();
        await client.DisposeAsync();

        // The reason is dropped AND the row is re-announced, so nothing is left displaying a
        // mismatch for a bridge that is gone.
        await Wait(async () => await Task.FromResult(connector.Incompatible is null));
        await Wait(async () => await Task.FromResult(Volatile.Read(ref failures) > afterHello));
        Assert.Null(connector.StatusDetail);
    }

    /// <summary>A version string is untrusted text on its way to a label.</summary>
    [Fact]
    public void An_incompatible_bridges_version_string_is_clipped_before_it_is_shown()
    {
        Assert.Equal("unknown", IncompatibleBridge.Clean(null));
        Assert.Equal("unknown", IncompatibleBridge.Clean("   "));
        Assert.Equal("ab", IncompatibleBridge.Clean("a\r\nb"));
        Assert.Equal(40, IncompatibleBridge.Clean(new string('x', 500)).Length);
    }

    static async Task Wait(Func<Task<bool>> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("condition was not met in time");
    }
}

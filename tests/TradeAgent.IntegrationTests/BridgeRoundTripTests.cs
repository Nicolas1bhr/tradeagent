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

        public BridgeHello Describe()
        {
            var h = inner.Describe();
            h.SupportsClientOrderId = _proven;
            return h;
        }

        public OrderInfo Place(PlaceOrderCommand cmd)
        {
            var o = inner.Place(cmd);
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

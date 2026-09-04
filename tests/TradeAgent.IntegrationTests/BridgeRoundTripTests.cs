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
    /// Authenticates the way the real bridge does, then holds the connection open and says nothing
    /// more. A peer that is refused and PARKS is the shape the round-6 ruling produced.
    /// </summary>
    /// <summary>
    /// Sends something a REFUSED peer has no right to be heard on, and does not care whether the
    /// send itself lands. Since round 7 the refusal also drops the connection, so the frame may die
    /// in the socket rather than in the connector — and either way the assertion afterwards is the
    /// same: nothing it claimed got through. Swallowing the write error is what keeps this test about
    /// the connector's belief rather than about the timing of a disconnect.
    /// </summary>
    static async Task Unheard(Func<Task> send)
    {
        try { await send(); } catch (IOException) { } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Dials the way a real bridge does: it redials. The pipe has ONE server instance and the accept
    /// loop recreates it only after the previous read loop ends, so a bridge arriving while the
    /// connector is recycling can land on a connection that is already going away — which the real
    /// bridge answers by trying again a moment later, and which a test that dialled exactly once
    /// would record as a failure of the connector.
    /// </summary>
    static async Task<StubBridge> Redial(string pipe, BridgeHello hello, int attempts = 8)
    {
        for (var attempt = 1; ; attempt++)
        {
            var bridge = new StubBridge(pipe, hello);
            try { await bridge.ConnectAsync(); return bridge; }
            catch (Exception) when (attempt < attempts)
            {
                await Unheard(async () => await bridge.DisposeAsync());
                await Task.Delay(100);
            }
        }
    }

    static async Task<System.IO.Pipes.NamedPipeClientStream> Park(string pipe, int protocolVersion)
    {
        var client = new System.IO.Pipes.NamedPipeClientStream(
            ".", pipe, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        await client.ConnectAsync(10_000);
        var w = new StreamWriter(client, new System.Text.UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };
        var r = new StreamReader(client, new System.Text.UTF8Encoding(false), false, 8192, leaveOpen: true);

        var cred = BridgePipeAuth.ReadForClient()!;
        var nonce = BridgePipeAuth.NewNonce();
        await w.WriteLineAsync(Json.Write(new
        {
            v = Versions.BridgeProtocolVersion,
            op = BridgePipeAuth.Challenge,
            data = new { nonce, proof = BridgePipeAuth.Proof(cred.Secret, BridgePipeAuth.BridgeRole, nonce) }
        }));
        string? line;
        while ((line = await r.ReadLineAsync()) is not null)
            if (Json.Read<BridgeFrame>(line)?.Op == BridgePipeAuth.Response) break;

        await w.WriteLineAsync(Json.Write(new BridgeFrame
        {
            Op = BridgeOps.Hello,
            Data = System.Text.Json.JsonSerializer.SerializeToElement(Speaking(protocolVersion), Json.Options)
        }));
        return client;
    }

    /// <summary>
    /// A REFUSED PEER MUST NOT BE ABLE TO HOLD THE TRADING PATH SHUT, AND PARKING IT DID EXACTLY THAT.
    ///
    /// Round 6 kept a mismatched peer on the pipe rather than dropping it, so that `Drop` could not
    /// erase the version number and the repair from the row. The pipe is created with
    /// `maxNumberOfServerInstances = 1`, and the accept loop creates the next instance only after the
    /// inner read loop ENDS — so a refused peer that holds the connection open and never speaks again
    /// occupies the only slot there is. The operator reads "reinstall the add-on from TradeAgent",
    /// does it, and the fixed bridge's `ConnectAsync` times out against a pipe held by the peer it
    /// was sent to replace.
    ///
    /// Any process running as this user can hold the trading path shut that way. The rule is both
    /// halves at once: the peer is DROPPED, and the reason it was dropped for survives the drop.
    /// </summary>
    [Fact]
    public async Task A_parked_refused_peer_does_not_keep_a_fixed_bridge_off_the_pipe()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        await connector.ConnectAsync();
        await using var _1 = connector;

        // A version-2 peer arrives, is refused, and then simply stops talking without disconnecting.
        using var parked = await Park(pipe, 2);
        await Wait(async () => await Task.FromResult(connector.Incompatible is not null));
        Assert.Equal(2, connector.Incompatible!.ReportedProtocolVersion);

        // The operator does what the row says, and a current bridge dials in.
        await using var repaired = await Redial(pipe, Speaking(Versions.BridgeProtocolVersion));
        await Wait(async () => await Task.FromResult(connector.Bridge is not null));

        Assert.Equal(Versions.BridgeProtocolVersion, connector.Bridge!.BridgeProtocolVersion);
        Assert.Null(connector.Incompatible);          // a good bridge is what ends the mismatch
        Assert.True(connector.Capabilities.ReconciliationProvable);
    }

    /// <summary>
    /// AND THE REASON SURVIVES THE DROP WE OURSELVES CAUSED — which is what made dropping unsafe
    /// before.
    ///
    /// `Drop` cleared the mismatch on the argument that a wrong version is a fact about the peer and
    /// leaves with the peer. That is true when the peer hangs up on its own; it is false when OUR
    /// refusal is what closed the connection, because then the reason is erased microseconds after
    /// being written and the dashboard reads FAILED with nothing on it while the bridge redials. The
    /// file already makes exactly this argument for an unproved peer's refusal two paragraphs down;
    /// a mismatch we refused is the same case.
    /// </summary>
    [Fact]
    public async Task A_refused_peer_leaves_the_version_and_the_repair_on_the_row()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        var failures = 0;
        connector.ConnectionChanged += s => { if (s == HealthState.FAILED) Interlocked.Increment(ref failures); };
        await connector.ConnectAsync();
        await using var _1 = connector;

        await using var old = new StubBridge(pipe, Speaking(2));
        await old.ConnectAsync();
        await Wait(async () => await Task.FromResult(connector.Incompatible is not null));

        // Dropped by us — and the row still names the version and the repair afterwards.
        await Wait(async () => await Task.FromResult(Volatile.Read(ref failures) > 0));
        Assert.Equal(2, connector.Incompatible!.ReportedProtocolVersion);
        Assert.Equal(Versions.BridgeProtocolVersion, connector.Incompatible.ExpectedProtocolVersion);
        Assert.Contains("reinstall the add-on", connector.StatusDetail);
        Assert.Contains("protocol 2", connector.StatusDetail);
        Assert.Null(connector.Bridge);
        Assert.False(connector.Capabilities.ReconciliationProvable);
    }

    /// <summary>
    /// AND A REFUSED BRIDGE THAT REDIALS IS REFUSED AGAIN. The refusal is a fact about the CONNECTION,
    /// so a new connection is heard out from the beginning — and says the same wrong thing, and is
    /// turned away again. What must not happen is a redial being taken for a repair.
    /// </summary>
    [Fact]
    public async Task A_refused_bridge_that_reconnects_is_refused_again()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        await connector.ConnectAsync();
        await using var _1 = connector;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var old = await Redial(pipe, Speaking(2));
            await Wait(async () => await Task.FromResult(connector.Incompatible is not null));
            Assert.Equal(2, connector.Incompatible!.ReportedProtocolVersion);
            Assert.Null(connector.Bridge);
            // Already off the pipe — its own teardown is tidying a connection the connector closed.
            await Unheard(async () => await old.DisposeAsync());
        }

        // And the pipe is still free for a bridge that is right.
        await using var repaired = await Redial(pipe, Speaking(Versions.BridgeProtocolVersion));
        await Wait(async () => await Task.FromResult(connector.Bridge is not null));
        Assert.Null(connector.Incompatible);
    }

    /// <summary>
    /// A PEER THIS CONNECTOR HAS REFUSED IS NOT ALLOWED TO SPEAK, WHATEVER FRAME IT SPEAKS IN.
    ///
    /// Round 5 guarded the EVENT branch and left the heartbeat one, and a heartbeat carries a whole
    /// <c>BridgeHello</c> — that is how a capability proved after the handshake reaches this end. So
    /// a peer whose hello was refused as protocol 2 set <c>_hello</c>, and with it
    /// <c>SupportsClientOrderId</c>, <c>SupportsOrderHistory</c> and <c>ReconciliationProvable</c>,
    /// by sending ONE heartbeat claiming protocol 3. The connector then displayed "speaks protocol 2
    /// — reinstall the add-on" and reported <c>ReconciliationProvable = true</c> at the same moment.
    ///
    /// That flag is not decoration. `TradingGateway` consults exactly it to refuse LIVE_AUTONOMOUS
    /// with AUTONOMY_REQUIRES_PROVABLE_STATE, and again to escalate an UNKNOWN order to "needs a
    /// human to look". Both refusals were removed by one frame from a bridge this build had already
    /// said it could not speak to.
    /// </summary>
    [Fact]
    public async Task A_refused_bridge_cannot_set_capabilities_through_a_heartbeat()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        await connector.ConnectAsync();
        await using var _1 = connector;

        await using var bridge = new StubBridge(pipe, Speaking(2));
        await bridge.ConnectAsync();
        await Wait(async () => await Task.FromResult(connector.Incompatible is not null));

        // The frame that used to buy everything back.
        await Unheard(() => bridge.Heartbeat(Speaking(Versions.BridgeProtocolVersion)));
        await Task.Delay(300);

        Assert.NotNull(connector.Incompatible);
        Assert.Equal(2, connector.Incompatible!.ReportedProtocolVersion);
        Assert.Null(connector.Bridge);
        Assert.False(connector.Capabilities.SupportsClientOrderId);
        Assert.False(connector.Capabilities.SupportsOrderHistory);
        // The one the gateway reads twice: to refuse LIVE_AUTONOMOUS, and to send an UNKNOWN order
        // to a person instead of resolving it.
        Assert.False(connector.Capabilities.ReconciliationProvable);
    }

    /// <summary>
    /// AND IT CANNOT TALK ITS WAY BACK EITHER. A mismatched hello poisons the CONNECTION, not the
    /// frame: nothing clears it but a reconnect. Sending a compatible hello afterwards used to set
    /// `_hello`, clear `_incompatible` and mark the connector connected — the same unlock as the
    /// heartbeat, one op to the left, which is why the rule is one decision for the connection
    /// rather than a guard per frame type.
    /// </summary>
    [Fact]
    public async Task A_refused_bridge_cannot_clear_its_refusal_with_a_later_hello()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        await connector.ConnectAsync();
        await using var _1 = connector;

        await using var bridge = new StubBridge(pipe, Speaking(2));
        await bridge.ConnectAsync();
        await Wait(async () => await Task.FromResult(connector.Incompatible is not null));

        await Unheard(() => bridge.SaySomethingElse(Speaking(Versions.BridgeProtocolVersion)));
        await Task.Delay(300);

        Assert.NotNull(connector.Incompatible);
        Assert.Equal(2, connector.Incompatible!.ReportedProtocolVersion);
        Assert.Null(connector.Bridge);
        Assert.False(connector.Capabilities.ReconciliationProvable);
    }

    /// <summary>
    /// AND THE OTHER DIRECTION, so the rule is a gate and not a wall: a fresh connection from a peer
    /// speaking the current protocol is accepted and its capabilities come through.
    /// </summary>
    [Fact]
    public async Task A_fresh_connection_from_a_compatible_bridge_is_still_accepted()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        await connector.ConnectAsync();
        await using var _1 = connector;

        await using var bridge = new StubBridge(pipe, Speaking(Versions.BridgeProtocolVersion));
        await bridge.ConnectAsync();
        await Wait(async () => await Task.FromResult(connector.Bridge is not null));

        Assert.Null(connector.Incompatible);
        Assert.Equal(Versions.BridgeProtocolVersion, connector.Bridge!.BridgeProtocolVersion);
        Assert.True(connector.Capabilities.ReconciliationProvable);
    }

    /// <summary>
    /// AUTHENTICATION IS NOT COMPATIBILITY. The event gate asked whether the peer had proved itself
    /// and whether a refusal had been recorded — and before any hello arrives, neither is true and
    /// nothing has been established about what this peer speaks. So an authenticated peer could
    /// publish order, execution and position events into the application before saying a word about
    /// its protocol. No trusted event is accepted until a COMPATIBLE hello has been seen on this
    /// connection.
    /// </summary>
    [Fact]
    public async Task An_authenticated_peer_raises_no_events_before_a_compatible_hello()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        await connector.ConnectAsync();
        await using var _1 = connector;

        var seen = 0;
        connector.QuoteChanged += _ => Interlocked.Increment(ref seen);
        connector.ConnectionChanged += _ => Interlocked.Increment(ref seen);

        await using var bridge = new StubBridge(pipe) { SendHello = false };
        await bridge.ConnectAsync();
        await Task.Delay(200);

        var before = Volatile.Read(ref seen);
        await bridge.RaiseEvent(BridgeEvents.Quote,
                                new QuoteInfo("ES", 4200.25m, 4200.50m, null, null, null, DateTimeOffset.UtcNow));
        await bridge.RaiseEvent(BridgeEvents.Connection, new { connected = true });
        await Task.Delay(300);

        Assert.Equal(before, Volatile.Read(ref seen));
        Assert.Null(connector.Bridge);

        // And it is a gate, not a wall: the hello arrives and the events that follow it are taken.
        await bridge.SaySomethingElse(Speaking(Versions.BridgeProtocolVersion));
        await Wait(async () => await Task.FromResult(connector.Bridge is not null));
        await bridge.RaiseEvent(BridgeEvents.Quote,
                                new QuoteInfo("ES", 4201m, 4202m, null, null, null, DateTimeOffset.UtcNow));
        await Wait(async () => await Task.FromResult(Volatile.Read(ref seen) > before));
    }

    /// <summary>
    /// AND THE SAME GATE ON THE HEARTBEAT, WHICH IS THE ONE THAT ASSIGNS. A heartbeat carries a whole
    /// <c>BridgeHello</c>, so a peer that has proved the secret and said NOTHING about its protocol
    /// could set <c>SupportsClientOrderId</c>, <c>SupportsOrderHistory</c> and
    /// <c>ReconciliationProvable</c> here without ever sending a hello at all — the same unlock as
    /// the refused peer's heartbeat, reached from the other side. The connection-level refusal
    /// cannot catch this one: nothing has been refused, because nothing has been claimed.
    /// </summary>
    [Fact]
    public async Task An_authenticated_peer_sets_no_capabilities_by_heartbeat_before_any_hello()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        await connector.ConnectAsync();
        await using var _1 = connector;

        await using var bridge = new StubBridge(pipe) { SendHello = false };
        await bridge.ConnectAsync();
        await Task.Delay(200);

        await bridge.Heartbeat(Speaking(Versions.BridgeProtocolVersion));
        await Task.Delay(300);

        Assert.Null(connector.Bridge);
        Assert.False(connector.Capabilities.SupportsClientOrderId);
        Assert.False(connector.Capabilities.ReconciliationProvable);

        // A gate, not a wall: the hello arrives and the next heartbeat is taken.
        await bridge.SaySomethingElse(Speaking(Versions.BridgeProtocolVersion));
        await Wait(async () => await Task.FromResult(connector.Bridge is not null));
        Assert.True(connector.Capabilities.ReconciliationProvable);
    }

    /// <summary>One hello, saying whatever protocol the caller wants it to say.</summary>
    static BridgeHello Speaking(int protocol) => new()
    {
        BridgeProtocolVersion = protocol,
        BridgeVersion = "0.1.1", AtasVersion = "6.1.2.3", AccountId = "ATAS-SIM",
        SupportsClientOrderId = true, SupportsOrderHistory = true,
        SupportsModify = true, SupportsClosePosition = true
    };

    /// <summary>
    /// A REFUSED PEER IS REFUSED AS A CONNECTION, NOT ONLY FOR THE CALLS THIS PROCESS MAKES.
    ///
    /// The mismatch branch sets <c>_connected = false</c> and leaves the read loop alive, and the
    /// event branch asked only whether the peer had AUTHENTICATED. So a version-2 bridge — which
    /// authenticates perfectly well, it is this product's own older DLL — could go on raising order,
    /// execution, position, account, quote and connection events into the application, while the
    /// same connector reported FAILED and refused it every RPC. "Refused outright" was true of what
    /// this process ASKS and false of what it BELIEVES.
    ///
    /// This is the version the bump exists for: the DLL actually deployed in ATAS's Strategies
    /// folder answers 2, and its events would describe orders placed by a build that sends them
    /// after a failed witness rewrite.
    /// </summary>
    [Fact]
    public async Task A_bridge_speaking_the_previous_protocol_raises_no_events_into_the_application()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        await connector.ConnectAsync();
        await using var _1 = connector;

        var seen = 0;
        connector.QuoteChanged += _ => Interlocked.Increment(ref seen);
        connector.ConnectionChanged += _ => Interlocked.Increment(ref seen);
        connector.OrderChanged += _ => Interlocked.Increment(ref seen);

        // A REAL, AUTHENTICATED peer — the whole point is that it passes every other gate.
        await using var bridge = new StubBridge(pipe, new BridgeHello
        {
            BridgeProtocolVersion = 2,
            BridgeVersion = "0.1.1", AtasVersion = "6.1.2.3", AccountId = "ATAS-SIM",
            SupportsClientOrderId = true, SupportsOrderHistory = true
        });
        await bridge.ConnectAsync();
        await Wait(async () => await Task.FromResult(connector.Incompatible is not null));
        Assert.Equal(2, connector.Incompatible!.ReportedProtocolVersion);

        // One branch gates all six event kinds, so two of them settle it — and these two are the
        // ones that need no record shape to be built by hand.
        var before = Volatile.Read(ref seen);
        await Unheard(() => bridge.RaiseEvent(BridgeEvents.Quote,
                                new QuoteInfo("ES", 4200.25m, 4200.50m, null, null, null, DateTimeOffset.UtcNow)));
        await Unheard(() => bridge.RaiseEvent(BridgeEvents.Connection, new { connected = true }));
        await Task.Delay(300);

        Assert.Equal(before, Volatile.Read(ref seen));
        Assert.Null(connector.Bridge);
        Assert.False(connector.Capabilities.SupportsClientOrderId);
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
    /// When the incompatible bridge goes away, the row that explained it must be told — AND ROUND 7
    /// REVERSED WHAT "TOLD" MEANS HERE.
    ///
    /// An incompatible bridge never sets `_connected`, so the disconnect path's "was it up?" guard is
    /// false and fires nothing; the row has to be re-announced explicitly, and that half is unchanged.
    /// What changed is the reason string. It used to be cleared unconditionally, on the argument that
    /// a wrong version is a fact about the peer and leaves with it. That is true when the peer hangs
    /// up on its own — and false when OUR refusal is what closed the connection, which is now every
    /// mismatch, because parking the peer instead held the single pipe instance shut against the very
    /// bridge the row tells the operator to install.
    ///
    /// So the announcement still fires, and the reason survives until a compatible hello ends it.
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

        // The peer goes away — it was already dropped by the refusal, so this is only its own tidying.
        await Unheard(async () => { await w.DisposeAsync(); await client.DisposeAsync(); });

        // The row is re-announced, and it still names the version and the repair: this disconnection
        // was ours, and a reason erased by the act of enforcing it is a dashboard reading FAILED with
        // nothing on it.
        await Wait(async () => await Task.FromResult(Volatile.Read(ref failures) > 0));
        Assert.NotNull(connector.Incompatible);
        Assert.Equal(Versions.BridgeProtocolVersion + 1, connector.Incompatible!.ReportedProtocolVersion);
        Assert.Contains("9.9.9", connector.StatusDetail);

        // And a bridge that is right is what ends it.
        await using var repaired = await Redial(pipe, Speaking(Versions.BridgeProtocolVersion));
        await Wait(async () => await Task.FromResult(connector.Bridge is not null));
        Assert.Null(connector.Incompatible);
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

    // ------------------------------------------------------------------- the blocking wait
    //
    // WHAT THE TESTS BELOW CATCH THAT THE TWO OLDER ONES DO NOT.
    //
    // A_definite_rejection_survives_the_crossing_as_a_rejection and
    // Losing_the_bridge_surfaces_as_indefinite_rather_than_as_a_rejection are the two tests that
    // look like they already cover this ground. They do not. Both run against
    // LoopbackAtasAdapter, which is wholly synchronous: it throws instantly from a method body and
    // never hands the bridge a Task at all. So they exercise the half of HandleFrame that cannot
    // break here — a naked exception out of a call that always returns — and they pass unchanged
    // against every wrong implementation listed in these comments, including a helper with no
    // deadline that wedges the command loop forever. Before these tests, nothing in the suite put a
    // faulted or a slow Task through HandleFrame. That is trap 9 in its exact form: a double that
    // answers immediately is not testing the thing that makes the real one hard.

    /// <summary>
    /// A definite refusal carried by a faulted Task must arrive as itself.
    ///
    /// CATCHES: waiting with .Wait() or .Result instead of GetAwaiter().GetResult(). Those wrap the
    /// fault in an AggregateException, and the reason the broker gave is replaced by "One or more
    /// errors occurred." A refusal that reaches the wire in that shape is classified indefinite, so
    /// the gateway records UNKNOWN and goes reconciling an order the broker never accepted — and the
    /// operator is left with no text saying why. Also catches a helper that does not wait at all:
    /// then there is no exception whatsoever and a refusal is reported as a placed order.
    /// </summary>
    [Fact]
    public void A_refusal_carried_by_a_faulted_task_keeps_its_type_and_its_reason()
    {
        var refused = Task.FromException(new AtasRejectedException("margin exceeded"));

        var ex = Record.Exception(() => AtasCall.Block(refused, TimeSpan.FromSeconds(5), "OpenOrderAsync"));

        Assert.NotNull(ex);                                   // not waiting swallows the fault entirely
        Assert.IsType<AtasRejectedException>(ex);             // .Wait()/.Result give AggregateException
        Assert.Equal("margin exceeded", ex.Message);          // and degrade this to a generic sentence
        Assert.Same(ex, BridgeServer.Refusal(ex));            // the wire agrees it is definite
    }

    /// <summary>
    /// A call that never answers must end the wait by itself, and the answer must be "unknown".
    ///
    /// CATCHES: no timeout at all — the defect this whole change exists for. BridgeServer.RunAsync
    /// awaits HandleFrame before reading the next frame, so a write that never returns means no
    /// further frame is ever read off the pipe, including the operator's cancel-all. The heartbeat
    /// is a separate Task.Run and keeps beating throughout, so the connector goes on reporting
    /// READY: a wedged bridge that looks healthy defeats the one check meant to catch it.
    ///
    /// ALSO CATCHES: converting the deadline into a rejection. That is rule 3 broken in the fatal
    /// direction — the deadline stopped US waiting, not ATAS working, so the order may be resting at
    /// the broker right now and calling it refused writes off a live position.
    /// </summary>
    [Fact]
    public async Task A_call_that_never_answers_ends_the_wait_and_reports_the_outcome_as_unknown()
    {
        var never = new TaskCompletionSource();
        // The wait runs on another thread so the test can outlive an implementation that never comes
        // back: without this, "no timeout at all" HANGS the run rather than failing it, and a hung
        // suite is not a red test.
        var call = Task.Run(() => AtasCall.Block(never.Task, TimeSpan.FromMilliseconds(200), "OpenOrderAsync"));

        var ex = await Record.ExceptionAsync(() => call.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.NotNull(ex);
        Assert.IsType<AtasCallTimeoutException>(ex);
        Assert.IsNotType<AtasRejectedException>(ex);
        Assert.Null(BridgeServer.Refusal(ex));                // indefinite on the wire, not rejected
        Assert.Contains("UNKNOWN", ex.Message);
        Assert.Contains("reconciled", ex.Message);
        Assert.Contains("OpenOrderAsync", ex.Message);        // says which call went unanswered

        never.SetResult();
    }

    /// <summary>
    /// Two failures are ambiguous, and stay ambiguous.
    ///
    /// CATCHES: leaning on GetAwaiter().GetResult() alone. It throws the FIRST of several faults and
    /// silently drops the rest, so a task that failed two ways — say a refusal AND a dropped link —
    /// would present to the wire as one definite AtasRejectedException. The second failure is
    /// exactly the case where an order may still be live, so collapsing the two is rule 3 broken
    /// while looking like it is being obeyed.
    /// </summary>
    [Fact]
    public void A_task_that_failed_two_ways_is_not_read_as_a_refusal()
    {
        var two = new TaskCompletionSource();
        two.SetException([new AtasRejectedException("margin exceeded"), new TimeoutException("and the link dropped")]);

        var ex = Record.Exception(() => AtasCall.Block(two.Task, TimeSpan.FromSeconds(5), "OpenOrderAsync"));

        var agg = Assert.IsType<AggregateException>(ex);
        Assert.Equal(2, agg.InnerExceptions.Count);
        Assert.Null(BridgeServer.Refusal(agg));
    }

    /// <summary>The wire classifier sees through wrappers, one fault deep, and no further.</summary>
    [Fact]
    public void The_wire_classifier_sees_through_a_single_fault_wrapper_and_no_further()
    {
        var refusal = new AtasRejectedException("margin exceeded");

        Assert.Same(refusal, BridgeServer.Refusal(refusal));
        Assert.Same(refusal, BridgeServer.Refusal(new AggregateException(refusal)));
        Assert.Same(refusal, BridgeServer.Refusal(new AggregateException(new AggregateException(refusal))));

        // Ambiguous at any layer means ambiguous overall.
        Assert.Null(BridgeServer.Refusal(new AggregateException(refusal, new TimeoutException())));
        Assert.Null(BridgeServer.Refusal(new AggregateException(new AggregateException(refusal, new TimeoutException()))));
        Assert.Null(BridgeServer.Refusal(new ConnectorTransportException("the pipe went away")));
    }

    /// <summary>
    /// An adapter shaped like the real one's write path: Place routes through
    /// <see cref="AtasCall.Block"/> on a Task the test controls, so the bridge's frame loop is really
    /// blocked for the duration. <see cref="LoopbackAtasAdapter"/> cannot stand in for this — it
    /// returns instantly, and a double that does not wait cannot test waiting.
    /// </summary>
    sealed class PlacesVia(LoopbackAtasAdapter inner, Func<PlaceOrderCommand, OrderInfo> place) : IAtasAdapter
    {
        public OrderInfo Place(PlaceOrderCommand cmd) => place(cmd);

        public BridgeHello Describe() => inner.Describe();
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

    static async Task<(AtasConnector Conn, BridgeServer Bridge)> PairWith(IAtasAdapter adapter, TimeSpan rpcTimeout)
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, rpcTimeout);
        await connector.ConnectAsync();
        var bridge = new BridgeServer(adapter, pipe) { HeartbeatInterval = TimeSpan.FromMilliseconds(150) };
        bridge.Start();
        await Wait(async () => await connector.IsConnectedAsync());
        return (connector, bridge);
    }

    /// <summary>
    /// A write that never answers must give the pipe back, and the bridge must be the one that says
    /// so.
    ///
    /// THE TRAP IN WRITING THIS TEST: with AtasConnector's default 10s RPC timeout, the CONNECTOR's
    /// own deadline answers first and the test passes against a bridge that is still wedged — it
    /// would be measuring the wrong end. The connector is given 30s here so that only the bridge's
    /// own 300ms deadline can possibly answer in time, and the assertions are on the BRIDGE's
    /// wording, so the test cannot pass for the wrong reason.
    ///
    /// CATCHES: no timeout in AtasCall.Block (the connector answers instead, with different text,
    /// and the frame after the wedged one never gets read); the deadline converted into a rejection
    /// (ConnectorRejectedException instead of ConnectorTransportException — a live order written off
    /// as refused); and a helper that does not wait at all (the place succeeds silently).
    /// </summary>
    [Fact]
    public async Task A_write_that_never_answers_gives_the_command_loop_back()
    {
        var never = new TaskCompletionSource();
        var loop = new LoopbackAtasAdapter();
        var (conn, bridge) = await PairWith(
            new PlacesVia(loop, cmd =>
            {
                AtasCall.Block(never.Task, TimeSpan.FromMilliseconds(300), "OpenOrderAsync");
                return loop.Place(cmd);
            }),
            TimeSpan.FromSeconds(30));
        try
        {
            var ex = await Record.ExceptionAsync(() => conn.PlaceOrderAsync(new PlaceOrderCommand(
                "wedge-1", "ATAS-LOOPBACK", "ES", OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, null)));

            // Indefinite, never a rejection: we stopped waiting, ATAS did not stop working.
            Assert.NotNull(ex);
            Assert.IsType<ConnectorTransportException>(ex);
            // The BRIDGE's account of it, not the connector's. The connector's own timeout message
            // names the wire op ("place") and says nothing about reconciling, so these two lines are
            // what stop this passing against a bridge that never answered at all.
            Assert.Contains("OpenOrderAsync", ex.Message);
            Assert.Contains("reconciled", ex.Message);

            // AND THE LOOP IS STILL ALIVE. This is the assertion the whole change is for: the frame
            // after a wedged write is read and answered. WaitAsync bounds it so a wedged loop fails
            // the test in five seconds instead of hanging the suite.
            Assert.Equal("ATAS-LOOPBACK", (await conn.GetAccountsAsync().WaitAsync(TimeSpan.FromSeconds(5))).Single().Id);

            // And the reason the bridge has to police its own deadline: the heartbeat runs on its own
            // task and never stopped, so health said READY throughout. Nothing outside would have
            // noticed a bridge that could not answer another frame.
            Assert.Equal(HealthState.READY, await conn.GetHealthAsync());
        }
        finally
        {
            // Release the wedge BEFORE tearing down, always. BridgeServer.DisposeAsync awaits
            // RunAsync, and a frame loop still blocked inside Place never completes it — so against
            // an implementation with no deadline this teardown would HANG the run instead of leaving
            // one red test, and a hung suite reports nothing at all.
            never.SetResult();
            await conn.DisposeAsync();
            await bridge.DisposeAsync();
        }
    }

    /// <summary>
    /// A refusal that reaches HandleFrame wrapped must still cross the wire as a refusal.
    ///
    /// AtasCall.Block unwraps a single fault at source, which is the right place for it. This is the
    /// wire declining to depend on that being true of every caller forever: anything that ever waits
    /// with .Wait() or .Result — here, or in a future call site — delivers the refusal inside an
    /// AggregateException, and a bare catch(AtasRejectedException) misses it completely. The broker's
    /// definite "no" would then arrive as rejected=false and the gateway would reconcile an order
    /// that does not exist.
    ///
    /// CATCHES: reverting BridgeServer to the bare catch.
    /// </summary>
    [Fact]
    public async Task A_refusal_wrapped_by_a_task_still_crosses_the_wire_as_a_refusal()
    {
        var loop = new LoopbackAtasAdapter();
        var (conn, bridge) = await PairWith(
            new PlacesVia(loop, _ => throw new AggregateException(new AtasRejectedException("margin exceeded"))),
            TimeSpan.FromSeconds(10));
        await using var _1 = conn;
        await using var _2 = bridge;

        var ex = await Record.ExceptionAsync(() => conn.PlaceOrderAsync(new PlaceOrderCommand(
            "wrapped-1", "ATAS-LOOPBACK", "ES", OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, null)));

        Assert.IsType<ConnectorRejectedException>(ex);
        // Off the refusal, not off the wrapper: AggregateException.Message alone would reach the
        // operator as "One or more errors occurred."
        Assert.Equal("margin exceeded", ex.Message);
    }

    /// <summary>
    /// Two failures wrapped together are ambiguous, and the wire must say so.
    ///
    /// CATCHES: unwrapping the first inner exception unconditionally. That is the tempting one-line
    /// version of the fix above, and it turns "the broker refused AND the link dropped" into a
    /// definite refusal — the reading under which an order that may be live is written off.
    /// </summary>
    [Fact]
    public async Task A_refusal_alongside_a_second_failure_crosses_the_wire_as_indefinite()
    {
        var loop = new LoopbackAtasAdapter();
        var (conn, bridge) = await PairWith(
            new PlacesVia(loop, _ => throw new AggregateException(
                new AtasRejectedException("margin exceeded"), new TimeoutException("and the link dropped"))),
            TimeSpan.FromSeconds(10));
        await using var _1 = conn;
        await using var _2 = bridge;

        var ex = await Record.ExceptionAsync(() => conn.PlaceOrderAsync(new PlaceOrderCommand(
            "wrapped-2", "ATAS-LOOPBACK", "ES", OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, null)));

        Assert.IsType<ConnectorTransportException>(ex);
    }

    /// <summary>
    /// The whole path, in the shape the real adapter has: a refusal arrives as a faulted Task, goes
    /// through AtasCall.Block inside a synchronous Place, and reaches the gateway as a rejection.
    ///
    /// CATCHES: a helper that leaves the call unawaited. Then Place returns an order, ok=true crosses
    /// the wire, and a broker refusal is recorded as a placed order — the failure with no downstream
    /// check behind it, because nothing ever asks again about an order it believes was accepted.
    /// </summary>
    [Fact]
    public async Task A_refusal_on_a_faulted_task_reaches_the_gateway_as_a_rejection()
    {
        var loop = new LoopbackAtasAdapter();
        var (conn, bridge) = await PairWith(
            new PlacesVia(loop, cmd =>
            {
                AtasCall.Block(Task.FromException(new AtasRejectedException("margin exceeded")),
                    TimeSpan.FromSeconds(5), "OpenOrderAsync");
                return loop.Place(cmd);
            }),
            TimeSpan.FromSeconds(10));
        await using var _1 = conn;
        await using var _2 = bridge;

        var ex = await Record.ExceptionAsync(() => conn.PlaceOrderAsync(new PlaceOrderCommand(
            "faulted-1", "ATAS-LOOPBACK", "ES", OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, null)));

        Assert.IsType<ConnectorRejectedException>(ex);
        Assert.Equal("margin exceeded", ex.Message);
        // Nothing was placed behind the refusal.
        Assert.Empty(loop.GetOrders("ATAS-LOOPBACK", true, null));
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

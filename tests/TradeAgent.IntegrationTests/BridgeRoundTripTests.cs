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

using System.Reflection;
using TradeAgent.AtasBridge;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Atas;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE MEASUREMENT ROUTE INTO <c>Place</c>, AND THE THING THAT MUST STAY TRUE ABOUT IT.
///
/// <c>BridgeOps.PlaceViaAsyncOverload</c> exists to answer one question that cannot be read out of a
/// signature: does <c>ITradingManager.OpenOrderAsync</c>'s task complete on SUBMISSION or on broker
/// ACKNOWLEDGEMENT? Taking that reading needs a second way to submit an order from inside
/// <c>Place</c> — which is precisely the kind of thing that, left unwatched, ends up reachable from
/// the money path.
///
/// So the tests here are not about the measurement. **Not one of them measures anything**, and the
/// one that could pretend to is the one this file most deliberately does not write: see
/// <see cref="LoopbackAtasAdapter.PlaceViaAsyncOverload"/>, which refuses rather than fabricating a
/// timing, because an in-memory adapter completes submission and acknowledgement in the same
/// statement and any number it produced would be this process's scheduler wearing ATAS's name.
///
/// What these tests hold in place is the boundary:
///
///   * the gateway's type cannot express the measurement route at all;
///   * the ordinary place op still reaches the ordinary path;
///   * an adapter that cannot take the measurement refuses, and the refusal crosses the wire as a
///     DEFINITE one — because nothing was submitted, and rule 3 turns on exactly that distinction.
///
/// The reading itself only exists on Windows, against a live ATAS, and is taken with
/// <c>probe atas --place-test-order --yes --via-async-overload</c>.
/// </summary>
public class PlaceRouteTests
{
    static string NewPipe() => "ta-prt-" + Guid.NewGuid().ToString("n")[..12];

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

    static PlaceOrderCommand Cmd(string id) => new(
        id, "ATAS-LOOPBACK", "ES", OrderSide.Buy, OrderType.Limit, 1m, 4000m, null, TimeInForce.Day, null);

    /// <summary>
    /// THE AUDIT, EXPRESSED AS A COMPILE-TIME FACT RATHER THAN A CONVENTION.
    ///
    /// TradingGateway is handed an <see cref="ITradingConnector"/>. If the measurement route were on
    /// that interface, "the gateway does not call it" would be a promise about behaviour that only a
    /// reading of every call site could keep. It is not on the interface, so the gateway cannot
    /// express it — and the only placement the interface does offer sends <c>BridgeOps.Place</c>.
    ///
    /// This test fails the moment somebody widens <c>ITradingConnector</c>, which is the one change
    /// that would quietly turn the safety argument from a fact into a habit.
    /// </summary>
    [Fact]
    public void The_gateways_connector_interface_cannot_express_the_measurement_route()
    {
        var onTheInterface = typeof(ITradingConnector)
            .GetMethods()
            .Select(m => m.Name)
            .ToList();

        Assert.Contains("PlaceOrderAsync", onTheInterface);
        Assert.DoesNotContain("PlaceOrderViaAsyncOverloadAsync", onTheInterface);

        // And it really does exist on the concrete connector — otherwise this test would pass
        // against a route that had been deleted, which is a different fact wearing the same green.
        Assert.NotNull(typeof(AtasConnector).GetMethod("PlaceOrderViaAsyncOverloadAsync",
            BindingFlags.Public | BindingFlags.Instance));

        // The interface's own placement is mapped to the ordinary op. Read off the implementation so
        // this cannot drift: AtasConnector.PlaceOrderAsync is what the gateway reaches.
        Assert.Equal("place", BridgeOps.Place);
        Assert.NotEqual(BridgeOps.Place, BridgeOps.PlaceViaAsyncOverload);
    }

    /// <summary>
    /// The ordinary op is untouched by the existence of the second one. An order placed the way the
    /// product places orders still reaches <see cref="IAtasAdapter.Place"/> and comes back whole.
    /// </summary>
    [Fact]
    public async Task The_ordinary_place_op_still_reaches_the_ordinary_path()
    {
        var (conn, bridge, adapter) = await ConnectedPair();
        await using var _1 = conn;
        await using var _2 = bridge;

        adapter.FillImmediately = false;
        var order = await conn.PlaceOrderAsync(Cmd("ordinary-1"));

        Assert.Equal("ordinary-1", order.ClientOrderId);
        Assert.Equal(ExecutionState.WORKING, order.State);
        Assert.Single(await conn.GetOrdersAsync("ATAS-LOOPBACK", true, null));
    }

    /// <summary>
    /// THE MEASUREMENT OP IS DISPATCHED, AND IT REACHES A DIFFERENT METHOD.
    ///
    /// Both halves matter. That the op is routed at all is what makes the probe flag work; that it
    /// lands somewhere other than <see cref="IAtasAdapter.Place"/> is what makes the audit line in
    /// <c>AtasStrategyAdapter</c> mean anything — if the dispatch quietly fell through to the
    /// ordinary place, the whole route would be a comment.
    /// </summary>
    [Fact]
    public async Task The_measurement_op_is_dispatched_to_its_own_method()
    {
        var loop = new LoopbackAtasAdapter();
        var spy = new RecordsWhichMethod(loop);
        var pipe = NewPipe();
        var conn = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        await conn.ConnectAsync();
        var bridge = new BridgeServer(spy, pipe) { HeartbeatInterval = TimeSpan.FromMilliseconds(300) };
        bridge.Start();
        await Wait(async () => await conn.IsConnectedAsync());

        await using var _1 = conn;
        await using var _2 = bridge;

        await conn.PlaceOrderAsync(Cmd("route-ordinary"));
        Assert.Equal(["Place"], spy.Calls);

        await conn.PlaceOrderViaAsyncOverloadAsync(Cmd("route-measure"));
        Assert.Equal(["Place", "PlaceViaAsyncOverload"], spy.Calls);
    }

    /// <summary>
    /// THE LOOPBACK REFUSES, AND THE REFUSAL IS DEFINITE — which is rule 3 read in the direction it
    /// is usually read backwards.
    ///
    /// The rule's normal work is stopping an ambiguous outcome from being recorded as a refusal,
    /// because an order behind a timeout may be live. Here the ambiguity runs the other way: this
    /// method submits nothing, cannot submit anything, and has no broker to submit to — so
    /// <c>rejected=true</c> is the truthful record and an indefinite failure would be the lie. It
    /// would send a caller reconciling an order that does not and cannot exist.
    ///
    /// The assertion on the wire classification is the load-bearing one. A refusal that arrived as
    /// <see cref="ConnectorTransportException"/> would look identical in a green test suite and mean
    /// the opposite thing.
    /// </summary>
    [Fact]
    public async Task The_loopback_refuses_the_measurement_rather_than_inventing_a_timing()
    {
        var (conn, bridge, _) = await ConnectedPair();
        await using var _1 = conn;
        await using var _2 = bridge;

        var ex = await Record.ExceptionAsync(() => conn.PlaceOrderViaAsyncOverloadAsync(Cmd("measure-1")));

        Assert.NotNull(ex);
        Assert.IsType<ConnectorRejectedException>(ex);
        Assert.Contains("NOTHING WAS SUBMITTED", ex.Message);

        // And it means it: the refusal is not a placement that also complained. Nothing reached the
        // book, which is the half of "nothing was submitted" a message cannot prove on its own.
        Assert.Empty(await conn.GetOrdersAsync("ATAS-LOOPBACK", true, null));
    }

    /// <summary>
    /// An adapter that never heard of the measurement route refuses too, without being asked to
    /// implement anything. The default on <see cref="IAtasAdapter"/> fails CLOSED: a bridge with no
    /// asynchronous submission path has no completion point to time, so refusing is the honest
    /// answer rather than a stub waiting to be filled in.
    /// </summary>
    [Fact]
    public async Task An_adapter_that_does_not_implement_the_route_refuses_by_default()
    {
        var pipe = NewPipe();
        var conn = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        await conn.ConnectAsync();
        var bridge = new BridgeServer(new OnlyTheBasics(new LoopbackAtasAdapter()), pipe)
        { HeartbeatInterval = TimeSpan.FromMilliseconds(300) };
        bridge.Start();
        await Wait(async () => await conn.IsConnectedAsync());

        await using var _1 = conn;
        await using var _2 = bridge;

        var ex = await Record.ExceptionAsync(() => conn.PlaceOrderViaAsyncOverloadAsync(Cmd("measure-2")));

        Assert.NotNull(ex);
        Assert.IsType<ConnectorRejectedException>(ex);
        Assert.Contains("NOTHING WAS SUBMITTED", ex.Message);

        // The frame after a refused one is still read. A refusal must not cost the command loop —
        // the same property the wedged-write test holds for the indefinite case.
        Assert.Equal("ATAS-LOOPBACK", (await conn.GetAccountsAsync()).Single().Id);
    }

    /// <summary>Records which adapter method the wire actually reached, and delegates the rest.</summary>
    sealed class RecordsWhichMethod(LoopbackAtasAdapter inner) : IAtasAdapter
    {
        public List<string> Calls { get; } = [];

        public OrderInfo Place(PlaceOrderCommand cmd) { Calls.Add(nameof(Place)); return inner.Place(cmd); }

        public OrderInfo PlaceViaAsyncOverload(PlaceOrderCommand cmd)
        {
            Calls.Add(nameof(PlaceViaAsyncOverload));
            // Deliberately NOT inner.PlaceViaAsyncOverload — that one refuses, correctly, and this
            // test is about which method the dispatch reached, not about what it then does.
            return inner.Place(cmd);
        }

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
        public OrderInfo? ClosePosition(string a, string s, string c) => inner.ClosePosition(a, s, c);

        public event Action<bool>? ConnectionChanged { add { } remove { } }
        public event Action<QuoteInfo>? QuoteChanged { add { } remove { } }
        public event Action<OrderInfo>? OrderChanged { add { } remove { } }
        public event Action<ExecutionInfo>? ExecutionReceived { add { } remove { } }
        public event Action<PositionInfo>? PositionChanged { add { } remove { } }
        public event Action<AccountInfo>? AccountChanged { add { } remove { } }
    }

    /// <summary>
    /// An adapter written to the interface as it was BEFORE the measurement route existed: it
    /// implements every member it has to and says nothing about <c>PlaceViaAsyncOverload</c>. That is
    /// the shape whose behaviour needs pinning down, because it is what every other implementation
    /// in this repository looks like.
    /// </summary>
    sealed class OnlyTheBasics(LoopbackAtasAdapter inner) : IAtasAdapter
    {
        public BridgeHello Describe() => inner.Describe();
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
        public OrderInfo? ClosePosition(string a, string s, string c) => inner.ClosePosition(a, s, c);

        public event Action<bool>? ConnectionChanged { add { } remove { } }
        public event Action<QuoteInfo>? QuoteChanged { add { } remove { } }
        public event Action<OrderInfo>? OrderChanged { add { } remove { } }
        public event Action<ExecutionInfo>? ExecutionReceived { add { } remove { } }
        public event Action<PositionInfo>? PositionChanged { add { } remove { } }
        public event Action<AccountInfo>? AccountChanged { add { } remove { } }
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

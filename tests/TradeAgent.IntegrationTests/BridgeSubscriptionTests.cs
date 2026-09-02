using TradeAgent.AtasBridge;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Atas;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// What a bridge is subscribed to after it is gone.
///
/// <see cref="BridgeServer"/> subscribes to all six adapter events when its loop starts, and until
/// now nothing ever unsubscribed: a disposed bridge stayed on ATAS's invocation lists for the life
/// of the strategy, and every event ATAS raised was handed to an object that had already been torn
/// down. A disposed-check inside the handler kept that from throwing back into ATAS's own event
/// raise; it did not make the subscription go away. These tests measure the subscription itself,
/// through an adapter that counts what it hands out.
/// </summary>
public class BridgeSubscriptionTests
{
    static string NewPipe() => "ta-sub-" + Guid.NewGuid().ToString("n")[..12];

    /// <summary>One bridge subscribes to exactly the six events on <see cref="IAtasAdapter"/>.</summary>
    const int EventsOnTheAdapter = 6;

    /// <summary>
    /// After DisposeAsync, an adapter event reaches no handler of the bridge's at all.
    /// Nothing listens on the pipe here and nothing needs to: the subscription is made before the
    /// first connection attempt.
    /// </summary>
    [Fact]
    public async Task A_disposed_bridge_is_not_handed_adapter_events()
    {
        var adapter = new ObservedAdapter();
        var bridge = new BridgeServer(adapter, NewPipe()) { ReconnectDelay = TimeSpan.FromMilliseconds(50) };
        bridge.Start();
        await Wait(() => adapter.Subscribers == EventsOnTheAdapter);

        await bridge.DisposeAsync();

        var handed = adapter.RaiseEverything();
        Assert.True(handed == 0, $"{handed} handler(s) of a disposed bridge were handed an adapter event");
        Assert.Equal(0, adapter.Subscribers);
    }

    /// <summary>
    /// Disposing in the same instant the loop starts is a race between its Subscribe() and the
    /// disposal, and both orders have to end the same way: nothing subscribed. Before the fix the
    /// loop could subscribe AFTER the bridge had been disposed, and then nothing ever removed it.
    /// </summary>
    [Fact]
    public async Task Disposing_a_bridge_the_moment_it_starts_leaves_nothing_subscribed()
    {
        var adapter = new ObservedAdapter();
        var bridge = new BridgeServer(adapter, NewPipe());
        bridge.Start();
        await bridge.DisposeAsync();

        Assert.Equal(0, adapter.Subscribers);
        Assert.Equal(0, adapter.RaiseEverything());
    }

    /// <summary>
    /// <see cref="BridgeServer.RunAsync"/> is public and takes a caller's token. A loop that ends
    /// because that token was cancelled has no further use for the adapter's events either.
    /// </summary>
    [Fact]
    public async Task A_loop_ended_by_its_callers_token_unsubscribes_too()
    {
        var adapter = new ObservedAdapter();
        await using var bridge = new BridgeServer(adapter, NewPipe()) { ReconnectDelay = TimeSpan.FromMilliseconds(50) };
        using var cts = new CancellationTokenSource();
        var loop = bridge.RunAsync(cts.Token);
        await Wait(() => adapter.Subscribers == EventsOnTheAdapter);

        await cts.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, adapter.Subscribers);
        Assert.Equal(0, adapter.RaiseEverything());
    }

    /// <summary>
    /// The other direction: a live bridge still forwards adapter events to its peer. Unsubscribing
    /// at the wrong moment — on every reconnect, say — would pass the three tests above and fail
    /// this one.
    /// </summary>
    [Fact]
    public async Task A_live_bridge_still_forwards_adapter_events_to_its_peer()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        await connector.ConnectAsync();
        var adapter = new LoopbackAtasAdapter();
        await using var bridge = new BridgeServer(adapter, pipe) { HeartbeatInterval = TimeSpan.FromMilliseconds(300) };
        bridge.Start();
        await Wait(() => connector.IsConnectedAsync().GetAwaiter().GetResult());

        var received = new TaskCompletionSource<QuoteInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        connector.QuoteChanged += q => received.TrySetResult(q);
        adapter.RaiseQuote(new QuoteInfo("ES", 4300m, 4300.25m, 4300.10m, 4, 6, DateTimeOffset.UtcNow));

        var quote = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("ES", quote.Symbol);
        Assert.Equal(4300.25m, quote.Ask);
    }

    // ---------------------------------------------------------------- helpers

    static async Task Wait(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("condition was not met in time");
    }

    /// <summary>
    /// An adapter that counts its subscribers and, when asked to raise, reports how many handlers
    /// it handed the event to. The trading methods are never reached: this adapter exists to be
    /// subscribed to and let go of.
    /// </summary>
    sealed class ObservedAdapter : IAtasAdapter
    {
        readonly Lock _gate = new();
        Action<bool>? _connection;
        Action<QuoteInfo>? _quote;
        Action<OrderInfo>? _order;
        Action<ExecutionInfo>? _execution;
        Action<PositionInfo>? _position;
        Action<AccountInfo>? _account;

        public int Subscribers
        {
            get { lock (_gate) return Count(_connection) + Count(_quote) + Count(_order) + Count(_execution) + Count(_position) + Count(_account); }
        }

        /// <summary>Raises each of the six events once. Returns how many handlers were handed one.</summary>
        public int RaiseEverything()
        {
            Action<bool>? c; Action<QuoteInfo>? q; Action<OrderInfo>? o; Action<ExecutionInfo>? x; Action<PositionInfo>? p; Action<AccountInfo>? a;
            lock (_gate) { c = _connection; q = _quote; o = _order; x = _execution; p = _position; a = _account; }

            var now = DateTimeOffset.UtcNow;
            var order = new OrderInfo("OBS-1", "obs-1", "ATAS-OBS", "ES", OrderSide.Buy, OrderType.Market, 1m, 1m, null, null,
                ExecutionState.FILLED, null, now);
            c?.Invoke(true);
            q?.Invoke(new QuoteInfo("ES", 1m, 2m, 1.5m, 1, 1, now));
            o?.Invoke(order);
            x?.Invoke(new ExecutionInfo("OBSX-1", "OBS-1", "obs-1", "ATAS-OBS", "ES", OrderSide.Buy, 1m, 1.5m, now));
            p?.Invoke(new PositionInfo("OBSP-ES", "ATAS-OBS", "ES", 1m, 1.5m, 0m));
            a?.Invoke(new AccountInfo("ATAS-OBS", "Observed", "USD", 1m, 1m, 0m, true, true));
            return Count(c) + Count(q) + Count(o) + Count(x) + Count(p) + Count(a);
        }

        static int Count(Delegate? d) => d?.GetInvocationList().Length ?? 0;

        public event Action<bool>? ConnectionChanged { add { lock (_gate) _connection += value; } remove { lock (_gate) _connection -= value; } }
        public event Action<QuoteInfo>? QuoteChanged { add { lock (_gate) _quote += value; } remove { lock (_gate) _quote -= value; } }
        public event Action<OrderInfo>? OrderChanged { add { lock (_gate) _order += value; } remove { lock (_gate) _order -= value; } }
        public event Action<ExecutionInfo>? ExecutionReceived { add { lock (_gate) _execution += value; } remove { lock (_gate) _execution -= value; } }
        public event Action<PositionInfo>? PositionChanged { add { lock (_gate) _position += value; } remove { lock (_gate) _position -= value; } }
        public event Action<AccountInfo>? AccountChanged { add { lock (_gate) _account += value; } remove { lock (_gate) _account -= value; } }

        public BridgeHello Describe() => new() { BridgeProtocolVersion = Versions.BridgeProtocolVersion, AccountId = "ATAS-OBS" };
        public IReadOnlyList<AccountInfo> GetAccounts() => [];
        public IReadOnlyList<InstrumentInfo> GetInstruments() => [];
        public QuoteInfo? GetQuote(string symbol) => null;
        public IReadOnlyList<PositionInfo> GetPositions(string accountId) => [];
        public IReadOnlyList<OrderInfo> GetOrders(string a, bool i, DateTimeOffset? s) => [];
        public IReadOnlyList<ExecutionInfo> GetExecutions(string a, DateTimeOffset? s) => [];
        public OrderInfo Place(PlaceOrderCommand cmd) => throw new NotSupportedException("this adapter is never traded through");
        public OrderInfo Modify(ModifyOrderCommand cmd) => throw new NotSupportedException();
        public void Cancel(string connectorOrderId) => throw new NotSupportedException();
        public IReadOnlyList<string> CancelAll(string accountId) => [];
        public OrderInfo? ClosePosition(string a, string symbol, string clientOrderId) => null;
    }
}

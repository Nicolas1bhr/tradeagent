using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Atas;
using TradeAgent.Core;

namespace TradeAgent.AtasBridge;

/// <summary>
/// An in-memory adapter that behaves like a well-implemented ATAS adapter should.
///
/// Two jobs. It lets the bridge be exercised end to end on any machine, including CI, so the
/// protocol half never ships untested. And it is the worked example the real
/// <c>AtasStrategyAdapter</c> is written against: every method here shows the shape and the
/// truthfulness the ATAS version has to match.
/// </summary>
public sealed class LoopbackAtasAdapter : IAtasAdapter
{
    readonly Lock _gate = new();
    readonly List<OrderInfo> _orders = [];
    readonly List<ExecutionInfo> _fills = [];
    readonly Dictionary<string, PositionInfo> _positions = new();
    long _seq;

    public string AccountId { get; init; } = "ATAS-LOOPBACK";
    public bool FillImmediately { get; set; } = true;

    public BridgeHello Describe() => new()
    {
        BridgeProtocolVersion = Versions.BridgeProtocolVersion,
        BridgeVersion = Versions.App,
        AtasVersion = "loopback",
        AccountId = AccountId,
        IsSimulated = true,
        SupportsClientOrderId = true,
        SupportsOrderHistory = true,
        // Trap 9: a field that is only ever populated by the real adapter is a field no test ever
        // carries across the wire. The loopback states its own surface for that reason — not to
        // imitate ATAS, but so the framing, serialisation and probe rendering of this field are
        // exercised by the suite rather than first tried on a live chart.
        TradingSurface = "loopback DataProvider=ok TradingManager=ok Connector=null orders=0 cache=none(loopback)",
        SupportsModify = true,
        SupportsClosePosition = true
    };

    public IReadOnlyList<AccountInfo> GetAccounts() =>
        [new AccountInfo(AccountId, "Loopback account", "USD", 25_000m, 25_000m, 0m, true, true)];

    public IReadOnlyList<InstrumentInfo> GetInstruments() =>
        [new InstrumentInfo("ES", "E-mini S&P 500", "CME", 0.25m, 12.50m, 50m)];

    public QuoteInfo? GetQuote(string symbol) =>
        new(symbol, 4300m, 4300.25m, 4300.10m, 4, 6, DateTimeOffset.UtcNow);

    public IReadOnlyList<PositionInfo> GetPositions(string accountId)
    {
        lock (_gate) return _positions.Values.ToList();
    }

    public IReadOnlyList<OrderInfo> GetOrders(string accountId, bool includeInactive, DateTimeOffset? since)
    {
        lock (_gate)
            return _orders
                .Where(o => includeInactive || !OrderStateMachine.IsTerminal(o.State))
                .Where(o => since is null || o.At >= since)
                .ToList();
    }

    public IReadOnlyList<ExecutionInfo> GetExecutions(string accountId, DateTimeOffset? since)
    {
        lock (_gate) return _fills.Where(f => since is null || f.At >= since).ToList();
    }

    public OrderInfo Place(PlaceOrderCommand cmd)
    {
        OrderInfo order;
        lock (_gate)
        {
            var price = cmd.LimitPrice ?? cmd.StopPrice ?? GetQuote(cmd.Symbol)!.Ask!.Value;
            var filled = FillImmediately ? cmd.Quantity : 0m;
            order = new OrderInfo($"LB-{++_seq}", cmd.ClientOrderId, cmd.AccountId, cmd.Symbol, cmd.Side,
                cmd.Type, cmd.Quantity, filled, cmd.LimitPrice, cmd.StopPrice,
                FillImmediately ? ExecutionState.FILLED : ExecutionState.WORKING, null, DateTimeOffset.UtcNow);
            _orders.Add(order);
            if (filled > 0) Apply(order, filled, price);
        }
        OrderChanged?.Invoke(order);
        return order;
    }

    void Apply(OrderInfo o, decimal qty, decimal price)
    {
        var signed = o.Side == OrderSide.Buy ? qty : -qty;
        var fill = new ExecutionInfo($"LBX-{++_seq}", o.ConnectorOrderId, o.ClientOrderId, o.AccountId,
            o.Symbol, o.Side, qty, price, DateTimeOffset.UtcNow);
        _fills.Add(fill);
        if (_positions.TryGetValue(o.Symbol, out var p))
        {
            var q = p.Quantity + signed;
            if (q == 0) _positions.Remove(o.Symbol); else _positions[o.Symbol] = p with { Quantity = q };
        }
        else _positions[o.Symbol] = new PositionInfo($"LBP-{o.Symbol}", o.AccountId, o.Symbol, signed, price, 0m);
        ExecutionReceived?.Invoke(fill);
    }

    public OrderInfo Modify(ModifyOrderCommand cmd)
    {
        lock (_gate)
        {
            var i = _orders.FindIndex(o => o.ConnectorOrderId == cmd.ConnectorOrderId);
            if (i < 0) throw new AtasRejectedException("order not found");
            _orders[i] = _orders[i] with
            {
                Quantity = cmd.Quantity ?? _orders[i].Quantity,
                LimitPrice = cmd.LimitPrice ?? _orders[i].LimitPrice,
                StopPrice = cmd.StopPrice ?? _orders[i].StopPrice
            };
            return _orders[i];
        }
    }

    public void Cancel(string connectorOrderId)
    {
        lock (_gate)
        {
            var i = _orders.FindIndex(o => o.ConnectorOrderId == connectorOrderId);
            if (i < 0 || OrderStateMachine.IsTerminal(_orders[i].State))
                throw new AtasRejectedException("order is not cancellable");
            _orders[i] = _orders[i] with { State = ExecutionState.CANCELLED };
        }
    }

    public IReadOnlyList<string> CancelAll(string accountId)
    {
        var ids = GetOrders(accountId, false, null).Select(o => o.ConnectorOrderId).ToList();
        foreach (var id in ids) Cancel(id);
        return ids;
    }

    public OrderInfo? ClosePosition(string accountId, string symbol, string clientOrderId)
    {
        var pos = GetPositions(accountId).FirstOrDefault(p => p.Symbol == symbol && p.Quantity != 0);
        if (pos is null) return null;
        return Place(new PlaceOrderCommand(clientOrderId, accountId, symbol,
            pos.Quantity > 0 ? OrderSide.Sell : OrderSide.Buy, OrderType.Market,
            Math.Abs(pos.Quantity), null, null, TimeInForce.Day, "close position"));
    }

    public event Action<bool>? ConnectionChanged;
    public event Action<QuoteInfo>? QuoteChanged;
    public event Action<OrderInfo>? OrderChanged;
    public event Action<ExecutionInfo>? ExecutionReceived;
    public event Action<PositionInfo>? PositionChanged;
    public event Action<AccountInfo>? AccountChanged;

    /// <summary>Test hooks for the event paths the real adapter raises from ATAS callbacks.</summary>
    public void RaiseConnection(bool connected) => ConnectionChanged?.Invoke(connected);
    public void RaiseQuote(QuoteInfo q) => QuoteChanged?.Invoke(q);
    public void RaisePosition(PositionInfo p) => PositionChanged?.Invoke(p);
    public void RaiseAccount(AccountInfo a) => AccountChanged?.Invoke(a);
}

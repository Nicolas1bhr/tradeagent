using TradeAgent.ConnectorSdk;
using TradeAgent.Core;

namespace TradeAgent.Connectors.Fake;

/// <summary>
/// A deterministic stand-in for a broker. It exists so the whole execution chain can be exercised,
/// and — critically — so faults can be injected that no live broker will reproduce on demand.
///
/// The book lives HERE, not in the connector, so it survives a simulated disconnect. That is what
/// makes "the order landed but we never heard the acknowledgement" a testable situation rather than
/// a thought experiment.
/// </summary>
public sealed class FakeBroker
{
    readonly Lock _gate = new();
    readonly List<OrderInfo> _orders = [];
    readonly List<ExecutionInfo> _executions = [];
    readonly Dictionary<string, PositionInfo> _positions = new();
    long _seq;

    public string AccountId { get; init; } = "SIM-001";
    public bool IsSimulated { get; init; } = true;
    public decimal Balance { get; private set; } = 100_000m;

    public IReadOnlyList<OrderInfo> Orders { get { lock (_gate) return _orders.ToList(); } }
    public IReadOnlyList<ExecutionInfo> Executions { get { lock (_gate) return _executions.ToList(); } }
    public IReadOnlyList<PositionInfo> Positions { get { lock (_gate) return _positions.Values.ToList(); } }

    /// <summary>Orders the broker holds for one client order id. More than one means a duplicate got through.</summary>
    public int CountByClientOrderId(string clientOrderId)
    {
        lock (_gate) return _orders.Count(o => o.ClientOrderId == clientOrderId);
    }

    public static decimal BasePrice(string symbol)
    {
        // Deterministic, no randomness: same symbol always starts at the same price.
        var h = symbol.Aggregate(7, (a, c) => unchecked(a * 31 + c));
        return 100m + Math.Abs(h % 4000) / 100m;
    }

    public decimal PriceOffset { get; set; }

    public QuoteInfo Quote(string symbol, DateTimeOffset at)
    {
        var mid = BasePrice(symbol) + PriceOffset;
        return new QuoteInfo(symbol, mid - 0.25m, mid + 0.25m, mid, 10, 10, at);
    }

    /// <summary>Accepts an order into the book. Called only after any injected transport fault decision.</summary>
    public OrderInfo Accept(PlaceOrderCommand cmd, FillBehaviour fill)
    {
        lock (_gate)
        {
            var id = $"FB-{++_seq}";
            var price = cmd.Type == OrderType.Market ? Quote(cmd.Symbol, DateTimeOffset.UtcNow).Ask!.Value : cmd.LimitPrice ?? cmd.StopPrice ?? 0m;
            var filled = fill switch
            {
                FillBehaviour.FillImmediately => cmd.Quantity,
                FillBehaviour.PartialFill => decimal.Round(cmd.Quantity / 2m, 4),
                _ => 0m
            };
            var state = fill switch
            {
                FillBehaviour.FillImmediately => ExecutionState.FILLED,
                FillBehaviour.PartialFill => ExecutionState.PARTIALLY_FILLED,
                _ => ExecutionState.WORKING
            };
            var order = new OrderInfo(id, cmd.ClientOrderId, cmd.AccountId, cmd.Symbol, cmd.Side, cmd.Type,
                cmd.Quantity, filled, cmd.LimitPrice, cmd.StopPrice, state, null, DateTimeOffset.UtcNow);
            _orders.Add(order);
            if (filled > 0) ApplyFill(order, filled, price);
            return order;
        }
    }

    public OrderInfo Reject(PlaceOrderCommand cmd, string reason)
    {
        lock (_gate)
        {
            var order = new OrderInfo($"FB-{++_seq}", cmd.ClientOrderId, cmd.AccountId, cmd.Symbol, cmd.Side,
                cmd.Type, cmd.Quantity, 0m, cmd.LimitPrice, cmd.StopPrice, ExecutionState.REJECTED, reason, DateTimeOffset.UtcNow);
            _orders.Add(order);
            return order;
        }
    }

    void ApplyFill(OrderInfo order, decimal qty, decimal price)
    {
        var signed = order.Side == OrderSide.Buy ? qty : -qty;
        _executions.Add(new ExecutionInfo($"X-{++_seq}", order.ConnectorOrderId, order.ClientOrderId,
            order.AccountId, order.Symbol, order.Side, qty, price, DateTimeOffset.UtcNow));
        var key = order.Symbol;
        if (_positions.TryGetValue(key, out var p))
        {
            var newQty = p.Quantity + signed;
            if (newQty == 0) _positions.Remove(key);
            else _positions[key] = p with { Quantity = newQty };
        }
        else _positions[key] = new PositionInfo($"P-{key}", order.AccountId, key, signed, price, 0m);
        Balance -= signed * price * 0.0001m; // token commission so balance moves observably
    }

    /// <summary>Advances a WORKING order to filled, the way a real book would when price arrives.</summary>
    public OrderInfo? FillWorking(string connectorOrderId)
    {
        lock (_gate)
        {
            var i = _orders.FindIndex(o => o.ConnectorOrderId == connectorOrderId);
            if (i < 0) return null;
            var o = _orders[i];
            if (OrderStateMachine.IsTerminal(o.State)) return o;
            var remaining = o.Quantity - o.FilledQuantity;
            var price = o.LimitPrice ?? Quote(o.Symbol, DateTimeOffset.UtcNow).Ask!.Value;
            var updated = o with { FilledQuantity = o.Quantity, State = ExecutionState.FILLED };
            _orders[i] = updated;
            if (remaining > 0) ApplyFill(updated, remaining, price);
            return updated;
        }
    }

    public bool Cancel(string connectorOrderId)
    {
        lock (_gate)
        {
            var i = _orders.FindIndex(o => o.ConnectorOrderId == connectorOrderId);
            if (i < 0 || OrderStateMachine.IsTerminal(_orders[i].State)) return false;
            _orders[i] = _orders[i] with { State = ExecutionState.CANCELLED };
            return true;
        }
    }

    public AccountInfo Account() => new(AccountId, IsSimulated ? "Simulation account" : "Live account",
        "USD", Balance, Balance, 0m, IsSimulated, true);
}

public enum FillBehaviour { LeaveWorking, FillImmediately, PartialFill }

/// <summary>
/// Faults the harness can inject. Each is a real failure mode a live broker produces at the worst
/// possible moment; counters are one-shot so a test can prove behaviour then let the system recover.
/// </summary>
public sealed class FaultProfile
{
    public bool Disconnected { get; set; }

    /// <summary>The broker ACCEPTS the order, then the acknowledgement is lost. The dangerous case.</summary>
    public int DropAfterBrokerAccept { get; set; }

    /// <summary>The transport dies before the broker sees anything. Also UNKNOWN to us at the time.</summary>
    public int DropBeforeBrokerAccept { get; set; }

    public int RejectNext { get; set; }

    /// <summary>
    /// The broker refuses the next cancellation, definitively. One-shot, like the others.
    ///
    /// A live broker refuses a cancel for ordinary reasons — the order filled a moment ago, it is
    /// already being cancelled, the venue will not accept one now — and without this the fake could
    /// only ever succeed at cancelling. That made a sweep reporting ATTEMPTS indistinguishable from
    /// one reporting SUCCESSES, so the mutant that swaps them survived every test. It is the
    /// difference between "cancel-all cancelled 3" and "cancel-all tried 3", which on the command a
    /// person reaches for when they want everything to stop is the whole of the meaning.
    /// </summary>
    public int RefuseCancel { get; set; }
    public FillBehaviour Fill { get; set; } = FillBehaviour.FillImmediately;

    /// <summary>Backdates quotes so staleness checks can be exercised.</summary>
    public TimeSpan QuoteAge { get; set; } = TimeSpan.Zero;

    /// <summary>Hides order history, simulating a backend that cannot prove what it holds.</summary>
    public bool HideOrderHistory { get; set; }

    public int LatencyMs { get; set; }

    public bool Take(Func<FaultProfile, int> get, Action<FaultProfile, int> set)
    {
        var n = get(this);
        if (n <= 0) return false;
        set(this, n - 1);
        return true;
    }
}

using TradeAgent.ConnectorSdk;
using TradeAgent.Core;

namespace TradeAgent.Connectors.Fake;

/// <summary>
/// The first connector, and permanently the test harness. Everything above it — gateway, trade CLI,
/// agent, UI — is developed and fault-tested against this before ATAS is involved at all.
/// </summary>
public sealed class FakeConnector(FakeBroker? broker = null, FaultProfile? faults = null) : ITradingConnector
{
    public FakeBroker Broker { get; } = broker ?? new FakeBroker();
    public FaultProfile Faults { get; } = faults ?? new FaultProfile();

    public string Id => "fake";

    /// <summary>
    /// In-process and unbounded by any wire, so the only thing that can make one call take time is a
    /// deliberately injected latency fault. It is reported rather than assumed to be zero, because a
    /// shutdown drain derived from it has to cover the faults the tests inject.
    /// </summary>
    public TimeSpan WorstCaseOperationPath =>
        TimeSpan.FromMilliseconds(Math.Max(Faults.LatencyMs, Faults.UncancellableLatencyMs));
    public string DisplayName => "Simulator (built in)";

    public ConnectorCapabilities Capabilities => new(
        IsPaper: Broker.IsSimulated,
        SupportsClientOrderId: true,
        SupportsOrderHistory: !Faults.HideOrderHistory,
        SupportsModify: true,
        SupportsClosePosition: true,
        SupportsStreaming: true);

    public event Action<HealthState>? ConnectionChanged;
    public event Action<QuoteInfo>? QuoteChanged;
    public event Action<OrderInfo>? OrderChanged;
    public event Action<ExecutionInfo>? ExecutionReceived;
    public event Action<PositionInfo>? PositionChanged;
    public event Action<AccountInfo>? AccountChanged;

    public Task ConnectAsync(CancellationToken ct = default)
    {
        ConnectionChanged?.Invoke(Faults.Disconnected ? HealthState.FAILED : HealthState.READY);
        return Task.CompletedTask;
    }

    public Task<HealthState> GetHealthAsync(CancellationToken ct = default) =>
        Task.FromResult(Faults.Disconnected ? HealthState.FAILED : HealthState.READY);

    public Task<bool> IsConnectedAsync(CancellationToken ct = default) => Task.FromResult(!Faults.Disconnected);

    /// <summary>Simulates the wire. Read paths fail loudly when disconnected; they never invent data.</summary>
    async Task Wire(CancellationToken ct)
    {
        if (Faults.LatencyMs > 0) await Task.Delay(Faults.LatencyMs, ct);
        if (Faults.UncancellableLatencyMs > 0) await Task.Delay(Faults.UncancellableLatencyMs);
        if (Faults.Disconnected) throw new ConnectorTransportException("simulator is disconnected");
    }

    public async Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken ct = default)
    { await Wire(ct); return [Broker.Account()]; }

    public async Task<AccountInfo?> GetAccountAsync(string accountId, CancellationToken ct = default)
    { await Wire(ct); return accountId == Broker.AccountId ? Broker.Account() : null; }

    public async Task<IReadOnlyList<InstrumentInfo>> GetInstrumentsAsync(CancellationToken ct = default)
    {
        await Wire(ct);
        return
        [
            new InstrumentInfo("ES", "E-mini S&P 500", "CME", 0.25m, 12.50m, 50m),
            new InstrumentInfo("NQ", "E-mini Nasdaq 100", "CME", 0.25m, 5.00m, 20m),
            new InstrumentInfo("MES", "Micro E-mini S&P 500", "CME", 0.25m, 1.25m, 5m),
        ];
    }

    public async Task<QuoteInfo?> GetQuoteAsync(string symbol, CancellationToken ct = default)
    {
        await Wire(ct);
        var q = Broker.Quote(symbol, DateTimeOffset.UtcNow - Faults.QuoteAge);
        QuoteChanged?.Invoke(q);
        return q;
    }

    public async Task<IReadOnlyList<PositionInfo>> GetPositionsAsync(string accountId, CancellationToken ct = default)
    { await Wire(ct); return Broker.Positions; }

    public async Task<IReadOnlyList<OrderInfo>> GetOrdersAsync(string accountId, bool includeInactive, DateTimeOffset? since, CancellationToken ct = default)
    {
        await Wire(ct);
        if (Faults.HideOrderHistory && includeInactive)
            throw new ConnectorTransportException("this backend cannot serve order history");
        return Broker.Orders
            .Where(o => includeInactive || !OrderStateMachine.IsTerminal(o.State))
            .Where(o => since is null || o.At >= since)
            .ToList();
    }

    public async Task<IReadOnlyList<ExecutionInfo>> GetExecutionsAsync(string accountId, DateTimeOffset? since, CancellationToken ct = default)
    { await Wire(ct); return Broker.Executions.Where(e => since is null || e.At >= since).ToList(); }

    public async Task<OrderInfo> PlaceOrderAsync(PlaceOrderCommand cmd, CancellationToken ct = default)
    {
        if (Faults.LatencyMs > 0) await Task.Delay(Faults.LatencyMs, ct);
        if (Faults.UncancellableLatencyMs > 0) await Task.Delay(Faults.UncancellableLatencyMs);
        if (Faults.Disconnected) throw new ConnectorTransportException("simulator is disconnected");

        // Transport dies before the broker sees it: nothing landed, but we cannot know that here.
        if (Faults.Take(f => f.DropBeforeBrokerAccept, (f, v) => f.DropBeforeBrokerAccept = v))
            throw new ConnectorTransportException("connection lost before the order was sent");

        if (Faults.Take(f => f.RejectNext, (f, v) => f.RejectNext = v))
        {
            var rejected = Broker.Reject(cmd, "insufficient margin (simulated)");
            OrderChanged?.Invoke(rejected);
            throw new ConnectorRejectedException(rejected.RejectReason!);
        }

        var order = Broker.Accept(cmd, Faults.Fill);

        // The broker HAS the order. Now lose the acknowledgement. This is the case that produces
        // duplicate fills in naive clients, and the reason UNKNOWN exists as a first-class state.
        if (Faults.Take(f => f.DropAfterBrokerAccept, (f, v) => f.DropAfterBrokerAccept = v))
            throw new ConnectorTransportException("connection lost after the order was accepted");

        OrderChanged?.Invoke(order);
        foreach (var x in Broker.Executions.Where(e => e.ClientOrderId == cmd.ClientOrderId)) ExecutionReceived?.Invoke(x);
        foreach (var p in Broker.Positions) PositionChanged?.Invoke(p);
        AccountChanged?.Invoke(Broker.Account());
        return order;
    }

    public async Task<OrderInfo> ModifyOrderAsync(ModifyOrderCommand cmd, CancellationToken ct = default)
    {
        await Wire(ct);
        var existing = Broker.Orders.FirstOrDefault(o => o.ConnectorOrderId == cmd.ConnectorOrderId)
            ?? throw new ConnectorRejectedException("order not found");
        var updated = existing with
        {
            Quantity = cmd.Quantity ?? existing.Quantity,
            LimitPrice = cmd.LimitPrice ?? existing.LimitPrice,
            StopPrice = cmd.StopPrice ?? existing.StopPrice
        };
        OrderChanged?.Invoke(updated);
        return updated;
    }

    public async Task CancelOrderAsync(string connectorOrderId, CancellationToken ct = default)
    {
        await Wire(ct);
        // A definite refusal from the broker, which is a real thing brokers do and the only way a
        // sweep can honestly report fewer cancellations than attempts.
        if (Faults.Take(f => f.RefuseCancel, (f, v) => f.RefuseCancel = v))
            throw new ConnectorRejectedException("the broker refused the cancellation (simulated)");
        if (!Broker.Cancel(connectorOrderId)) throw new ConnectorRejectedException("order is not cancellable");
    }

    public async Task<IReadOnlyList<string>> CancelAllOrdersAsync(string accountId, CancellationToken ct = default)
    {
        await Wire(ct);
        var ids = Broker.Orders.Where(o => !OrderStateMachine.IsTerminal(o.State)).Select(o => o.ConnectorOrderId).ToList();
        foreach (var id in ids) Broker.Cancel(id);
        return ids;
    }

    public async Task<OrderInfo?> ClosePositionAsync(string accountId, string symbol, string clientOrderId, CancellationToken ct = default)
    {
        await Wire(ct);
        var pos = Broker.Positions.FirstOrDefault(p => p.Symbol == symbol);
        if (pos is null || pos.Quantity == 0) return null;
        return await PlaceOrderAsync(new PlaceOrderCommand(clientOrderId, accountId, symbol,
            pos.Quantity > 0 ? OrderSide.Sell : OrderSide.Buy, OrderType.Market,
            Math.Abs(pos.Quantity), null, null, TimeInForce.Day, "close position"), ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

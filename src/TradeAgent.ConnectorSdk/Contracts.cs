using TradeAgent.Core;

namespace TradeAgent.ConnectorSdk;

public enum OrderSide { Buy, Sell }
public enum OrderType { Market, Limit, Stop, StopLimit }
public enum TimeInForce { Day, GoodTillCancel, ImmediateOrCancel, FillOrKill }

public sealed record AccountInfo(string Id, string Name, string Currency, decimal Balance, decimal Equity,
    decimal? UnrealizedPnl, bool IsSimulated, bool TradingEnabled);

public sealed record InstrumentInfo(string Symbol, string Description, string Exchange,
    decimal TickSize, decimal TickValue, decimal? ContractSize);

public sealed record QuoteInfo(string Symbol, decimal? Bid, decimal? Ask, decimal? Last,
    decimal? BidSize, decimal? AskSize, DateTimeOffset At)
{
    /// <summary>A quote older than this is not a price, it is a memory. Refuse to size orders from it.</summary>
    public bool IsStale(TimeSpan maxAge) => DateTimeOffset.UtcNow - At > maxAge;
}

public sealed record PositionInfo(string Id, string AccountId, string Symbol, decimal Quantity,
    decimal AveragePrice, decimal? UnrealizedPnl);

public sealed record OrderInfo(string ConnectorOrderId, string? ClientOrderId, string AccountId, string Symbol,
    OrderSide Side, OrderType Type, decimal Quantity, decimal FilledQuantity, decimal? LimitPrice,
    decimal? StopPrice, ExecutionState State, string? RejectReason, DateTimeOffset At);

public sealed record ExecutionInfo(string ExecutionId, string ConnectorOrderId, string? ClientOrderId,
    string AccountId, string Symbol, OrderSide Side, decimal Quantity, decimal Price, DateTimeOffset At);

public sealed record PlaceOrderCommand(string ClientOrderId, string AccountId, string Symbol, OrderSide Side,
    OrderType Type, decimal Quantity, decimal? LimitPrice, decimal? StopPrice, TimeInForce Tif, string? Comment);

public sealed record ModifyOrderCommand(string ConnectorOrderId, decimal? Quantity, decimal? LimitPrice, decimal? StopPrice);

/// <summary>
/// What a backend can actually promise. The gateway reads this to decide how much autonomy is safe:
/// without a client order id or order history it cannot prove after a disconnect whether an order
/// landed, so it refuses LIVE_AUTONOMOUS on that connector rather than guessing.
/// </summary>
public sealed record ConnectorCapabilities(
    bool IsPaper,
    bool SupportsClientOrderId,
    bool SupportsOrderHistory,
    bool SupportsModify,
    bool SupportsClosePosition,
    bool SupportsStreaming)
{
    public bool ReconciliationProvable => SupportsClientOrderId && SupportsOrderHistory;
}

/// <summary>The broker gave a definitive answer: no. Safe to record as REJECTED; nothing is working.</summary>
public sealed class ConnectorRejectedException(string reason) : Exception(reason);

/// <summary>
/// We do not know what happened. The order may be live at the broker. NEVER convert this into a
/// retry — it must become <see cref="ExecutionState.UNKNOWN"/> and go through reconciliation.
/// </summary>
public sealed class ConnectorTransportException(string message, Exception? inner = null) : Exception(message, inner);

public interface ITradingConnector : IAsyncDisposable
{
    string Id { get; }
    string DisplayName { get; }
    ConnectorCapabilities Capabilities { get; }

    Task ConnectAsync(CancellationToken ct = default);
    Task<HealthState> GetHealthAsync(CancellationToken ct = default);
    Task<bool> IsConnectedAsync(CancellationToken ct = default);

    Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken ct = default);
    Task<AccountInfo?> GetAccountAsync(string accountId, CancellationToken ct = default);
    Task<IReadOnlyList<InstrumentInfo>> GetInstrumentsAsync(CancellationToken ct = default);
    Task<QuoteInfo?> GetQuoteAsync(string symbol, CancellationToken ct = default);

    Task<IReadOnlyList<PositionInfo>> GetPositionsAsync(string accountId, CancellationToken ct = default);
    Task<IReadOnlyList<OrderInfo>> GetOrdersAsync(string accountId, bool includeInactive, DateTimeOffset? since, CancellationToken ct = default);
    Task<IReadOnlyList<ExecutionInfo>> GetExecutionsAsync(string accountId, DateTimeOffset? since, CancellationToken ct = default);

    Task<OrderInfo> PlaceOrderAsync(PlaceOrderCommand cmd, CancellationToken ct = default);
    Task<OrderInfo> ModifyOrderAsync(ModifyOrderCommand cmd, CancellationToken ct = default);
    Task CancelOrderAsync(string connectorOrderId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> CancelAllOrdersAsync(string accountId, CancellationToken ct = default);
    Task<OrderInfo?> ClosePositionAsync(string accountId, string symbol, string clientOrderId, CancellationToken ct = default);

    event Action<HealthState>? ConnectionChanged;
    event Action<QuoteInfo>? QuoteChanged;
    event Action<OrderInfo>? OrderChanged;
    event Action<ExecutionInfo>? ExecutionReceived;
    event Action<PositionInfo>? PositionChanged;
    event Action<AccountInfo>? AccountChanged;
}

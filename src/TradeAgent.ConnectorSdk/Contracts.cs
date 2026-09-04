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

/// <summary>
/// Implemented by connectors that can say, in one line, why they are in the state they are in.
///
/// Optional on purpose. HealthState carries five words and no nouns, so a connector that knows the
/// difference between "nothing has connected yet" and "the thing that connected speaks the wrong
/// protocol" has nowhere to put it, and the user is shown a red row with no way to act on it. Kept
/// off <see cref="ITradingConnector"/> so a connector with nothing to add implements nothing.
///
/// It is display text and only display text: nothing branches on it, and a connector must never use
/// it to say something it is not entitled to say through Capabilities.
/// </summary>
public interface IConnectorStatusDetail
{
    string? StatusDetail { get; }
}

/// <summary>
/// A trading backend, in the gateway's own vocabulary.
///
/// THE OBLIGATION THAT IS NOT A METHOD, AND IT IS THE ONE THAT CARRIES SAFETY: every implementation
/// of a MUTATING call — <see cref="PlaceOrderAsync"/>, <see cref="ModifyOrderAsync"/>,
/// <see cref="CancelOrderAsync"/>, <see cref="CancelAllOrdersAsync"/>,
/// <see cref="ClosePositionAsync"/> — must call <see cref="TransportLedger.Attempt"/> the moment it
/// STARTS, before anything can go wrong, and <see cref="TransportLedger.Record"/> at every site that
/// KNOWS where the frame got to. Both are no-ops outside a leg, so they may be called
/// unconditionally; reads must NOT record, because a leg is a read to find its target and then the
/// thing it came to do, and recording the read would report a reply for a mutation that never left.
///
/// WHY IT MATTERS MORE THAN IT LOOKS. A sweep leg is reported to the agent with one of five words,
/// and one of them — <c>not-sent</c> — is an ASSURANCE: nothing of this leg is at the broker, no
/// reconciliation, no pause. An empty transport record is what produces it. A connector that mutates
/// and never marks the attempt makes "nothing was recorded" mean "nobody wrote it down" instead of
/// "no mutation was started", and the gateway then reports an assurance about an order that may be
/// live. Measured on a connector written to this interface that really cancelled at the broker:
/// <c>not-sent</c>, <c>attempted: 0</c> (verifier round-11 F-2).
///
/// The gateway will not take a connector's silence as an assurance — a leg whose own record proves a
/// mutating step was dispatched is reported <c>sent-not-confirmed</c> whatever the ledger says, which
/// is the fail-closed direction — so a connector that ignores this is SAFE and IMPRECISE: every
/// ambiguous leg asks for a reconciliation it may not need. Marking the attempt is what buys the
/// precision back; <see cref="TransportOutcome.NothingWritten"/>, which only a connector can prove,
/// is the one report allowed to overrule the record.
/// </summary>
public interface ITradingConnector : IAsyncDisposable
{
    string Id { get; }
    string DisplayName { get; }
    ConnectorCapabilities Capabilities { get; }

    /// <summary>
    /// The longest ONE operation can take on this connector before it gives up, at its current
    /// values — every bounded wait it puts in series, added up.
    ///
    /// It is on the interface because a shutdown drain that is shorter than it abandons an order
    /// that is still legitimately in progress, and the drain is chosen by a component that holds an
    /// <see cref="ITradingConnector"/> and nothing more specific. A literal there is a number that
    /// silently stops being true the moment a connector is constructed with different deadlines —
    /// which is a supported thing to do (Codex C3).
    /// </summary>
    TimeSpan WorstCaseOperationPath { get; }

    /// <summary>
    /// How long a RISK-REDUCING OPERATION gets in total — the whole of a cancel, a cancel-all or a
    /// close, including every read it has to do first and every leg it decomposes into.
    ///
    /// It is on the interface because the component that DECOMPOSES the operation is the one that
    /// has to start the clock, and it holds an <see cref="ITradingConnector"/> and nothing more
    /// specific. Without it every RPC started its own budget and a sweep paid the bound once per
    /// leg (Codex round-7 F1).
    /// </summary>
    TimeSpan EmergencyBudget { get; }

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

    // THE MUTATIONS. Each one owes the transport ledger an <see cref="TransportLedger.Attempt"/> at
    // its start and a <see cref="TransportLedger.Record"/> wherever it learns where the frame got to.
    // See the obligation on this interface; a connector that skips it is reported fail-closed.
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

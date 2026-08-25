using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Atas;

namespace TradeAgent.AtasBridge;

/// <summary>
/// Everything the bridge needs from ATAS, and nothing else.
///
/// The point of this seam: the transport, framing, heartbeat, capability handshake and error
/// classification all live in <see cref="BridgeServer"/>, which is compiled and tested on any
/// machine. Only the calls below actually touch the ATAS API, so the part that cannot be verified
/// without ATAS is one small, explicit list rather than a whole subsystem.
/// </summary>
public interface IAtasAdapter
{
    /// <summary>Capabilities and versions, sent in the handshake. Be truthful here — the gateway
    /// withholds autonomous live trading from a bridge that cannot prove order state.</summary>
    BridgeHello Describe();

    IReadOnlyList<AccountInfo> GetAccounts();
    IReadOnlyList<InstrumentInfo> GetInstruments();
    QuoteInfo? GetQuote(string symbol);
    IReadOnlyList<PositionInfo> GetPositions(string accountId);

    /// <summary>
    /// Must include finished orders when <paramref name="includeInactive"/> is true, and must cover
    /// <paramref name="since"/>. Reconciliation after a disconnect depends entirely on this: if the
    /// history is incomplete, report SupportsOrderHistory = false rather than returning a partial list.
    /// </summary>
    IReadOnlyList<OrderInfo> GetOrders(string accountId, bool includeInactive, DateTimeOffset? since);

    IReadOnlyList<ExecutionInfo> GetExecutions(string accountId, DateTimeOffset? since);

    /// <summary>
    /// Places an order carrying <see cref="PlaceOrderCommand.ClientOrderId"/> so it can be found
    /// again after a lost connection. Throw <see cref="AtasRejectedException"/> ONLY when the broker
    /// definitively refused; any other failure must propagate so the gateway treats it as unknown.
    /// </summary>
    OrderInfo Place(PlaceOrderCommand cmd);

    OrderInfo Modify(ModifyOrderCommand cmd);
    void Cancel(string connectorOrderId);
    IReadOnlyList<string> CancelAll(string accountId);
    OrderInfo? ClosePosition(string accountId, string symbol, string clientOrderId);

    event Action<bool>? ConnectionChanged;
    event Action<QuoteInfo>? QuoteChanged;
    event Action<OrderInfo>? OrderChanged;
    event Action<ExecutionInfo>? ExecutionReceived;
    event Action<PositionInfo>? PositionChanged;
    event Action<AccountInfo>? AccountChanged;
}

/// <summary>
/// The broker said no, definitively. Reserved for exactly that: using it for a timeout or a
/// disconnect would tell the gateway an order failed when it may in fact be live.
/// </summary>
public sealed class AtasRejectedException(string reason) : Exception(reason);

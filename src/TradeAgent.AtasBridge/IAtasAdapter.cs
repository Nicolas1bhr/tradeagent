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

    /// <summary>
    /// The same placement as <see cref="Place"/>, submitted through the platform's ASYNCHRONOUS
    /// order call so that the completion point of that call can be timed. MEASUREMENT ONLY.
    ///
    /// It answers one question, and it is the last one blocking a decision that has been deferred
    /// three times: does <c>ITradingManager.OpenOrderAsync</c>'s task complete on SUBMISSION or on
    /// broker ACKNOWLEDGEMENT? Nothing in the ATAS documentation or the API dump says, and no amount
    /// of reading will — the two answers are indistinguishable except by a stopwatch on a venue whose
    /// acknowledgement is measurably slower than its submission.
    ///
    /// EVERY SAFETY RULE APPLIES HERE UNCHANGED, and rule 3 most of all. This is a real order on a
    /// real account. The pre-flight refusals, the write-ahead witness record, the acknowledgement
    /// wait and the classification of what comes back are the same code as <see cref="Place"/> —
    /// deliberately, because a second submission path with its own error handling is precisely where
    /// a timeout would get mistaken for a refusal. An expiry inside the async call raises
    /// <see cref="AtasCallTimeoutException"/> and propagates: UNKNOWN, reconcile, never REJECTED.
    ///
    /// AN IMPLEMENTATION THAT CANNOT TAKE THIS MEASUREMENT MUST REFUSE, NOT IMPROVISE. The default
    /// below does that, and it is the right default rather than a stub: an adapter with no
    /// asynchronous submission path has no completion point to time, and a number produced by
    /// anything other than the platform's own call answers a different question while wearing this
    /// one's name. The refusal happens before anything is submitted, so REJECTED is the truthful
    /// classification of it — nothing can be live at a broker that was never asked.
    /// </summary>
    OrderInfo PlaceViaAsyncOverload(PlaceOrderCommand cmd) =>
        throw new AtasRejectedException(
            $"this bridge ({GetType().Name}) has no asynchronous submission path to measure, so there " +
            "is no completion point to time. NOTHING WAS SUBMITTED.");

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

/// <summary>
/// WHICH PLATFORM CALL AN ADAPTER'S PLACE PATH SUBMITS THROUGH. Not a mode, not a setting, and not
/// reachable from the wire — a parameter on one internal overload, and the only reason it exists.
///
/// The safety argument is meant to be performed by reading a single line, and that line is the
/// public entry point in <c>AtasStrategyAdapter</c>:
///
///     public OrderInfo Place(PlaceOrderCommand cmd) =&gt; Place(cmd, PlaceRoute.Default);
///
/// <see cref="MeasureAsync"/> is unreachable from it. TradingGateway holds an
/// <c>ITradingConnector</c>, whose only placement is <c>PlaceOrderAsync</c>, which sends
/// <c>BridgeOps.Place</c>, which <c>BridgeServer</c> dispatches to <c>adapter.Place(cmd)</c> — the
/// line above. There is no flag, no configuration file and no environment variable anywhere in that
/// chain, because each of those would turn a one-line audit into a search.
///
/// INTERNAL ON PURPOSE. Widening this to public would let a caller outside the bridge assembly
/// select the measurement route, which is the whole thing being prevented.
/// </summary>
internal enum PlaceRoute
{
    /// <summary>What every caller in the product gets: the ordinary submission path.</summary>
    Default,

    /// <summary>
    /// Submit through the platform's asynchronous overload and block on its task, so the task's
    /// completion point can be compared against the acknowledgement the wait after it observes.
    /// Selected only by <see cref="IAtasAdapter.PlaceViaAsyncOverload"/>, which is reached only by
    /// <c>BridgeOps.PlaceViaAsyncOverload</c>, which is sent only by <c>tools/probe</c>.
    /// </summary>
    MeasureAsync
}

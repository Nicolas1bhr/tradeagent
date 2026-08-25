#if ATAS_SDK
using ATAS.Strategies.Chart;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Atas;
using TradeAgent.Core;

namespace TradeAgent.AtasBridge;

/// <summary>
/// The real ATAS adapter: the ONE file in this product that cannot be compiled or tested without
/// ATAS installed. Everything it plugs into — framing, heartbeat, reconnect, capability handshake,
/// error classification, the whole gateway — is already covered by tests using
/// <see cref="LoopbackAtasAdapter"/>.
///
/// HOW TO FINISH THIS FILE
///
/// Work against current official ATAS documentation and the assemblies in your install, NOT from
/// memory or from this skeleton. The class names, base class, property names and the order-object
/// shape below are UNVERIFIED starting points; see docs/RESEARCH-REQUIRED.md item A1.
///
/// The rules that must not be compromised while filling these in:
///
///   1. Carry ClientOrderId onto the ATAS order and read it back in GetOrders. If ATAS cannot round
///      trip a client identifier, set SupportsClientOrderId = false in Describe() and accept that the
///      gateway will refuse fully automatic live trading. Do NOT fake it.
///   2. GetOrders(includeInactive: true, since) must really return finished orders back to 'since'.
///      If it cannot, report SupportsOrderHistory = false. A partial history is worse than none: it
///      makes "this order does not exist" look provable when it is not.
///   3. Throw AtasRejectedException ONLY for a definite broker refusal. Timeouts, disconnects and
///      anything ambiguous must propagate as ordinary exceptions so the gateway records UNKNOWN and
///      reconciles instead of writing the order off.
///   4. Never place orders by driving the ATAS user interface. Programmatic API only.
/// </summary>
public sealed class AtasStrategyAdapter : ChartStrategy, IAtasAdapter
{
    BridgeServer? _bridge;

    public AtasStrategyAdapter()
    {
        // TODO(A1): confirm the correct base class and lifecycle hooks for a loadable chart strategy.
    }

    protected override void OnStarted()
    {
        base.OnStarted();
        _bridge = new BridgeServer(this);
        _bridge.Start();
    }

    protected override void OnStopping()
    {
        _bridge?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _bridge = null;
        base.OnStopping();
    }

    public BridgeHello Describe() => new()
    {
        BridgeProtocolVersion = Versions.BridgeProtocolVersion,
        BridgeVersion = Versions.App,
        AtasVersion = "TODO(A1): read the platform version",
        AccountId = null,          // TODO(A1): the selected portfolio/account identifier
        IsSimulated = false,       // TODO(A1): true only when ATAS reports a simulation connection
        SupportsClientOrderId = false,   // TODO(A1): true ONLY once a round trip is proven
        SupportsOrderHistory = false,    // TODO(A1): true ONLY once 'since' coverage is proven
        SupportsModify = false,          // TODO(A1)
        SupportsClosePosition = false    // TODO(A1)
    };

    public IReadOnlyList<AccountInfo> GetAccounts() => throw new NotImplementedException("TODO(A1): map ATAS portfolios");
    public IReadOnlyList<InstrumentInfo> GetInstruments() => throw new NotImplementedException("TODO(A1): map ATAS securities");
    public QuoteInfo? GetQuote(string symbol) => throw new NotImplementedException("TODO(A1): best bid/ask for the security");
    public IReadOnlyList<PositionInfo> GetPositions(string accountId) => throw new NotImplementedException("TODO(A1)");
    public IReadOnlyList<OrderInfo> GetOrders(string accountId, bool includeInactive, DateTimeOffset? since) => throw new NotImplementedException("TODO(A1): see rule 2 above");
    public IReadOnlyList<ExecutionInfo> GetExecutions(string accountId, DateTimeOffset? since) => throw new NotImplementedException("TODO(A1)");
    public OrderInfo Place(PlaceOrderCommand cmd) => throw new NotImplementedException("TODO(A1): OpenOrder, carrying cmd.ClientOrderId; see rules 1 and 3");
    public OrderInfo Modify(ModifyOrderCommand cmd) => throw new NotImplementedException("TODO(A1): ModifyOrder");
    public void Cancel(string connectorOrderId) => throw new NotImplementedException("TODO(A1): CancelOrder");
    public IReadOnlyList<string> CancelAll(string accountId) => throw new NotImplementedException("TODO(A1)");
    public OrderInfo? ClosePosition(string accountId, string symbol, string clientOrderId) => throw new NotImplementedException("TODO(A1): flatten via a programmatic market order");

    // TODO(A1): raise these from the corresponding ATAS callbacks (order changed, trade/execution,
    // position changed, portfolio changed, connection state changed).
    public event Action<bool>? ConnectionChanged;
    public event Action<QuoteInfo>? QuoteChanged;
    public event Action<OrderInfo>? OrderChanged;
    public event Action<ExecutionInfo>? ExecutionReceived;
    public event Action<PositionInfo>? PositionChanged;
    public event Action<AccountInfo>? AccountChanged;
}
#endif

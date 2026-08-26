#if ATAS_SDK
using System.Collections;
using System.ComponentModel;
using System.Globalization;
using ATAS.Strategies.Chart;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Atas;
using TradeAgent.Core;

// Every ATAS type is aliased rather than imported wholesale. ATAS.DataFeedsCore.TimeInForce and
// TradeAgent.ConnectorSdk.TimeInForce would otherwise collide on every use, and an alias makes it
// obvious at each call site which side of the boundary a name comes from.
using AtasDirections = ATAS.DataFeedsCore.OrderDirections;
using AtasMyTrade = ATAS.DataFeedsCore.MyTrade;
using AtasOrder = ATAS.DataFeedsCore.Order;
using AtasOrderStates = ATAS.DataFeedsCore.OrderStates;
using AtasOrderTypes = ATAS.DataFeedsCore.OrderTypes;
using AtasPortfolio = ATAS.DataFeedsCore.Portfolio;
using AtasPosition = ATAS.DataFeedsCore.Position;
using AtasSecurity = ATAS.DataFeedsCore.Security;
using AtasTif = ATAS.DataFeedsCore.TimeInForce;
using IAtasCache = ATAS.DataFeedsCore.Database.ICache;
using IFeedConnector = ATAS.DataFeedsCore.IDataFeedConnector;

namespace TradeAgent.AtasBridge;

/// <summary>
/// The real ATAS adapter: the ONE file in this product that cannot be compiled or tested without
/// ATAS installed. Everything it plugs into — framing, heartbeat, reconnect, capability handshake,
/// error classification, the whole gateway — is already covered by tests using
/// <see cref="LoopbackAtasAdapter"/>.
///
/// HOW THIS FILE WAS WRITTEN
///
/// Against a reflection dump of the real ATAS 8.0.14.397 assemblies (ATAS.Strategies.dll,
/// ATAS.Indicators.dll, ATAS.DataFeedsCore.dll, Utils.Common.dll) taken from the install directory.
/// Every ATAS type, property, method and event named below was found in that dump, with two
/// documented exceptions, both flagged inline:
///
///   * the dump lists PUBLIC members only, so the protected lifecycle overrides
///     (<c>OnCalculate</c>, <c>OnStarted</c>, <c>OnStopping</c>) could not be confirmed from it.
///     Their names come from the official ATAS documentation instead, and the class deliberately
///     ALSO drives itself from the public <see cref="ChartStrategy.StateChanged"/> event so that
///     deleting those two overrides costs no functionality if their signature turns out to differ.
///   * the dump does not record generic ARGUMENTS (it prints <c>IEnumerable`1</c>, not
///     <c>IEnumerable&lt;Order&gt;</c>). So no code here names one. Collections are read through
///     the non-generic <see cref="IEnumerable"/> with <c>OfType&lt;T&gt;()</c>, and every event is
///     subscribed with an implicitly-typed lambda whose payload is widened to <c>object</c> and
///     then matched on its runtime type. That is compile-proof against any generic argument AND
///     type-safe at runtime — it cannot silently read the wrong field off the wrong object.
///
/// The rules that are not compromised anywhere below:
///
///   1. ClientOrderId travels on <see cref="AtasOrder.Comment"/> and is read back in GetOrders.
///      Describe() reports SupportsClientOrderId only after the round trip has actually been
///      OBSERVED at runtime (see <see cref="ProveClientOrderId"/>). It is false until then.
///   2. ATAS exposes no order-history API at all — only a live in-memory order collection — so
///      SupportsOrderHistory is a hard false. GetOrders still returns the live view, but it never
///      lets a 'since' filter hide an order that is still working.
///   3. AtasRejectedException is raised only where nothing can still be live: a pre-flight refusal
///      that happened before submission, or an explicit ATAS order-failure event naming our order.
///      Timeouts, disconnects and unattributable failures propagate as ordinary exceptions.
///   4. No UI is touched. Orders go through ChartStrategy.OpenOrder / IDataFeedConnector.RegisterOrder.
/// </summary>
[DisplayName("TradeAgent Bridge")]
[Description("Connects this chart to TradeAgent. Start it once; TradeAgent detects it by heartbeat.")]
public sealed class AtasStrategyAdapter : ChartStrategy, IAtasAdapter
{
    /// <summary>How long Place/Modify/Cancel wait for ATAS to say yes or no before returning the
    /// order as-is. A timeout is NOT a rejection: the order may well be live, so it comes back in a
    /// non-terminal state and the gateway keeps tracking it.</summary>
    public TimeSpan AckTimeout { get; init; } = TimeSpan.FromSeconds(3);

    readonly Lock _gate = new();
    readonly ManualResetEventSlim _pulse = new(false);

    /// <summary>Reasons captured from ATAS order-failure events, keyed by both broker order id and
    /// client order id because the failure may arrive before an id has been assigned.</summary>
    readonly Dictionary<string, string> _failures = new(StringComparer.Ordinal);

    /// <summary>Orders we submitted, by client order id, so Place can watch the exact instance.</summary>
    readonly Dictionary<string, AtasOrder> _submitted = new(StringComparer.Ordinal);

    /// <summary>Last bid/ask/last we actually saw change, per symbol, and when we saw it. A quote is
    /// stamped with the time it was OBSERVED to move — never with "now" — because QuoteInfo.IsStale
    /// is what stops the gateway sizing an order off a price that stopped updating an hour ago.</summary>
    readonly Dictionary<string, (decimal Bid, decimal Ask, decimal? Last, DateTimeOffset At)> _quotes = new(StringComparer.OrdinalIgnoreCase);

    readonly HashSet<AtasSecurity> _tracked = [];

    BridgeServer? _bridge;
    IFeedConnector? _hooked;
    bool _clientOrderIdProven;

    public AtasStrategyAdapter()
    {
        // Public, dump-verified path into the lifecycle: ChartStrategy exposes StateChanged
        // (EventHandler`1) and State (StrategyStates). The lambda takes its parameters implicitly so
        // it compiles whatever the event's generic argument turns out to be, and it reads the state
        // off 'this' rather than off the event args. This is what makes the two protected overrides
        // below optional rather than load-bearing.
        StateChanged += (_, _) => Guard(SyncBridgeToState);
    }

    // ---------------------------------------------------------------- lifecycle

    /// <summary>
    /// Required override: ATAS.Indicators.Indicator declares OnCalculate abstract. The bridge draws
    /// nothing and computes nothing — it only relays — so this is deliberately empty.
    ///
    /// NOT PROVABLE FROM THE DUMP: the dump lists public members only. The (int bar, decimal value)
    /// shape is corroborated by the public extension
    /// ATAS.Indicators.Extensions.Calculate(Indicator indicator, Int32 bar, Decimal value) and by
    /// the official "Basic indicator" documentation page.
    /// </summary>
    protected override void OnCalculate(int bar, decimal value) { }

    /// <summary>Name from the official Strategies documentation ("OnStarted - is called when a
    /// strategy is started"), not from the dump. Everything it does is also done by the
    /// StateChanged subscription in the constructor, so it is safe to delete if it will not bind.</summary>
    protected override void OnStarted() => Guard(SyncBridgeToState);

    /// <summary>
    /// Name from the official Strategies documentation ("OnStopping - is called before stopping a
    /// strategy"). Deliberately does NOT cancel orders or flatten positions, even though the ATAS
    /// docs suggest a strategy should: this class holds no strategy of its own, and silently
    /// cancelling TradeAgent's working orders because a chart was closed would be a decision the
    /// user never asked for. TradeAgent sees the heartbeat stop and applies its own policy.
    /// </summary>
    protected override void OnStopping() => Guard(StopBridge);

    void SyncBridgeToState()
    {
        if (State == ATAS.Strategies.StrategyStates.Started) StartBridge();
        else if (State == ATAS.Strategies.StrategyStates.Stopped) StopBridge();
    }

    void StartBridge()
    {
        HookConnector();
        BridgeServer bridge;
        lock (_gate)
        {
            if (_bridge is not null) return;
            _bridge = bridge = new BridgeServer(this);
        }
        // Start outside the lock, off a local: a stop racing this must not turn Start() into a null
        // dereference inside an ATAS callback.
        bridge.Start();
    }

    void StopBridge()
    {
        BridgeServer? bridge;
        lock (_gate) { bridge = _bridge; _bridge = null; }
        bridge?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        UntrackSecurities();
    }

    // ---------------------------------------------------------------- handshake

    public BridgeHello Describe()
    {
        var portfolio = Portfolio;
        bool proven;
        lock (_gate) proven = _clientOrderIdProven;

        return new BridgeHello
        {
            BridgeProtocolVersion = Versions.BridgeProtocolVersion,
            BridgeVersion = Versions.App,
            // The platform version ATAS actually loaded us into. There is no public version property
            // in the dump, so this reads the assembly identity of the ATAS.Strategies.dll in process.
            AtasVersion = typeof(ChartStrategy).Assembly.GetName().Version?.ToString() ?? "unknown",
            AccountId = portfolio?.AccountID,
            // Portfolio.IsRealAccount is the only simulation signal in the dump. When there is no
            // portfolio yet we report NOT simulated, because guessing "simulated" on an unknown
            // account is the guess that costs money.
            IsSimulated = portfolio is not null && !portfolio.IsRealAccount,
            // Rule 1. False until a placed order has been seen coming back out of ATAS's own order
            // collection carrying our client id AND a broker-assigned id. Never hard-coded true.
            SupportsClientOrderId = proven,
            // Rule 2, and it is answered at runtime for the same reason rule 1 is. See HistoryCache():
            // IDataFeedConnector itself has no history call, only a live order collection. The one
            // order-history query in the whole ATAS surface lives on an interface nothing publicly
            // hands you, so this asks the running platform whether it is reachable instead of
            // assuming either way. False means the gateway withholds autonomous live trading.
            SupportsOrderHistory = HistoryCache() is { IsInitialized: true },
            SupportsModify = true,
            SupportsClosePosition = true
        };
    }

    // ---------------------------------------------------------------- reads

    public IReadOnlyList<AccountInfo> GetAccounts()
    {
        var c = RequireConnector();
        var list = new List<AccountInfo>();
        foreach (var p in Items<AtasPortfolio>(c.Portfolios)) list.Add(ToAccount(p, c));
        if (list.Count == 0 && Portfolio is { } own) list.Add(ToAccount(own, c));
        return list;
    }

    public IReadOnlyList<InstrumentInfo> GetInstruments()
    {
        var c = RequireConnector();
        var list = new List<InstrumentInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The chart's own instrument goes first: it is the one this strategy can trade through
        // ChartStrategy.OpenOrder, and it is the one the user is looking at.
        if (Security is { } chart && SymbolOf(chart).Length > 0 && seen.Add(SymbolOf(chart)))
            list.Add(ToInstrument(chart));
        foreach (var s in Items<AtasSecurity>(c.Securities))
            if (SymbolOf(s).Length > 0 && seen.Add(SymbolOf(s))) list.Add(ToInstrument(s));
        return list;
    }

    public QuoteInfo? GetQuote(string symbol)
    {
        var s = FindSecurity(symbol);
        if (s is null) return null;
        Track(s);

        var key = SymbolOf(s);
        lock (_gate)
        {
            // Only a quote we have watched move gets a real timestamp. One we have never seen tick
            // is returned at MinValue so IsStale() refuses it, rather than dressed up as fresh.
            var at = _quotes.TryGetValue(key, out var q) ? q.At : DateTimeOffset.MinValue;
            return BuildQuote(s, key, at);
        }
    }

    public IReadOnlyList<PositionInfo> GetPositions(string accountId)
    {
        var c = RequireConnector();
        var list = new List<PositionInfo>();
        foreach (var p in Items<AtasPosition>(c.Positions))
        {
            if (!AccountMatches(p.AccountID ?? p.Portfolio?.AccountID, accountId)) continue;
            list.Add(ToPosition(p));
        }
        return list;
    }

    /// <summary>
    /// Rule 2 in practice.
    ///
    /// The live book always comes from the connector. Finished orders additionally come from ATAS's
    /// order cache when one is reachable — and when it is not, none are claimed and Describe() has
    /// already said SupportsOrderHistory = false.
    ///
    /// Two things it will never do. It will never let the 'since' filter drop an order that is still
    /// working, because a working order hidden from reconciliation is the failure that loses money.
    /// And when asked for a window older than ATAS is configured to keep, it refuses outright rather
    /// than answering with a list that looks complete: a partial history makes "this order does not
    /// exist" look provable when it is not.
    /// </summary>
    public IReadOnlyList<OrderInfo> GetOrders(string accountId, bool includeInactive, DateTimeOffset? since)
    {
        var c = RequireConnector();
        var fills = FillsByOrder(c);
        var cache = includeInactive && !string.IsNullOrWhiteSpace(accountId) ? HistoryCache() : null;

        if (cache is not null && since is not null && cache.ClearCachePeriod > TimeSpan.Zero
            && since.Value < DateTimeOffset.UtcNow - cache.ClearCachePeriod)
            // Ordinary exception: the gateway must see "I cannot answer that", never a short list.
            throw new InvalidOperationException(
                $"ATAS keeps order history for {cache.ClearCachePeriod}; {since.Value:O} is further back " +
                "than that, so this history would be incomplete and must not be treated as proof");

        var byKey = new Dictionary<string, OrderInfo>(StringComparer.Ordinal);

        void Take(AtasOrder o)
        {
            if (!AccountMatches(o.AccountID ?? o.Portfolio?.AccountID, accountId)) return;
            var info = ToOrder(o, fills);
            if (OrderStateMachine.IsTerminal(info.State))
            {
                if (!includeInactive) return;
                if (since is not null && info.At < since.Value) return;
            }
            // First writer wins, and the live book is read first: a cached copy must never displace
            // the object ATAS is still updating.
            byKey.TryAdd(info.ConnectorOrderId, info);
        }

        foreach (var o in Items<AtasOrder>(c.Orders)) Take(o);
        if (cache is not null) foreach (var o in Items<AtasOrder>(cache.GetOrders(accountId))) Take(o);
        return [.. byKey.Values];
    }

    /// <summary>
    /// The whole basis for rule 2's answer, and it is a runtime question, not a guess.
    ///
    /// There is exactly one order-history query in the four ATAS assemblies:
    /// ATAS.DataFeedsCore.Database.ICache.GetOrders(String accountId). Nothing in the public surface
    /// returns an ICache — but IDataFeedConnector.Factory is typed IEntityFactory, and the concrete
    /// ATAS.DataFeedsCore.Database.Cache implements ICache and IEntityFactory on the same object. So
    /// this asks the running platform rather than assuming: a plain type test that either finds a
    /// real cache or finds nothing. When it finds nothing, SupportsOrderHistory is false and the
    /// gateway refuses fully automatic live trading — which is the correct outcome, not a fallback.
    /// </summary>
    IAtasCache? HistoryCache() => Connector?.Factory as IAtasCache;

    public IReadOnlyList<ExecutionInfo> GetExecutions(string accountId, DateTimeOffset? since)
    {
        var c = RequireConnector();
        var list = new List<ExecutionInfo>();
        foreach (var t in Items<AtasMyTrade>(c.MyTrades))
        {
            if (!AccountMatches(t.AccountID ?? t.Portfolio?.AccountID, accountId)) continue;
            var e = ToExecution(t);
            if (since is not null && e.At < since.Value) continue;
            list.Add(e);
        }
        return list;
    }

    // ---------------------------------------------------------------- writes

    public OrderInfo Place(PlaceOrderCommand cmd)
    {
        // Pre-flight. Every throw below happens before anything is submitted, so nothing can be live
        // at the broker and REJECTED is the truthful record. That is exactly the test rule 3 sets:
        // definite, not merely disappointing.
        if (string.IsNullOrWhiteSpace(cmd.ClientOrderId))
            throw new AtasRejectedException("a client order id is required; nothing was submitted");
        if (cmd.Quantity <= 0m)
            throw new AtasRejectedException($"quantity {cmd.Quantity} is not tradable; nothing was submitted");

        var c = RequireConnector();
        var security = FindSecurity(cmd.Symbol)
            ?? throw new AtasRejectedException($"ATAS has no instrument matching '{cmd.Symbol}'; nothing was submitted");
        var portfolio = FindPortfolio(cmd.AccountId)
            ?? throw new AtasRejectedException($"ATAS has no account matching '{cmd.AccountId}'; nothing was submitted");

        var order = new AtasOrder
        {
            Portfolio = portfolio,
            Security = security,
            SecurityId = security.SecurityId,
            AccountID = portfolio.AccountID,
            Direction = cmd.Side == OrderSide.Sell ? AtasDirections.Sell : AtasDirections.Buy,
            Type = ToAtasType(cmd.Type),
            QuantityToFill = cmd.Quantity,
            TimeInForce = ToAtasTif(cmd.Tif),
            // Rule 1: the client order id rides on Order.Comment, the only client-settable string on
            // the ATAS order. PlaceOrderCommand.Comment is deliberately NOT merged in — an exact
            // value is what makes the identifier findable again after a disconnect.
            Comment = cmd.ClientOrderId
        };

        if (cmd.Type is OrderType.Limit or OrderType.StopLimit)
            order.Price = ATAS.Strategies.ATM.Extensions.ShrinkPrice(security, cmd.LimitPrice
                ?? throw new AtasRejectedException("a limit price is required for this order type; nothing was submitted"));
        if (cmd.Type is OrderType.Stop or OrderType.StopLimit)
            order.TriggerPrice = ATAS.Strategies.ATM.Extensions.ShrinkPrice(security, cmd.StopPrice
                ?? throw new AtasRejectedException("a stop price is required for this order type; nothing was submitted"));

        lock (_gate)
        {
            Trim();
            _failures.Remove(cmd.ClientOrderId);
            _submitted[cmd.ClientOrderId] = order;
        }

        // From here on nothing may be reported as REJECTED unless ATAS says so explicitly: once
        // RegisterOrder/OpenOrder has been entered, the order may exist at the broker.
        if (ReferenceEquals(security, Security) && ReferenceEquals(portfolio, Portfolio)) OpenOrder(order);
        else Block(c.RegisterOrderAsync(order));

        WaitFor(() => Failure(cmd.ClientOrderId, order) is not null
                      || order.State != AtasOrderStates.None
                      || !string.IsNullOrEmpty(order.Id));

        if (Failure(cmd.ClientOrderId, order) is { } refusal)
            throw new AtasRejectedException(refusal);

        ProveClientOrderId(cmd.ClientOrderId);
        return ToOrder(order, null);
    }

    public OrderInfo Modify(ModifyOrderCommand cmd)
    {
        var c = RequireConnector();
        var order = FindOrder(cmd.ConnectorOrderId)
            ?? throw new AtasRejectedException($"ATAS does not know order '{cmd.ConnectorOrderId}'; nothing was submitted");
        if (order.State is AtasOrderStates.Done or AtasOrderStates.Failed)
            throw new AtasRejectedException("order has already finished and cannot be modified; nothing was submitted");
        if (cmd.Quantity is <= 0m)
            throw new AtasRejectedException($"quantity {cmd.Quantity} is not tradable; nothing was submitted");

        var replacement = order.Clone();
        if (cmd.Quantity is { } q) replacement.QuantityToFill = q;
        var sec = order.Security
            ?? throw new InvalidOperationException("the order to modify has no instrument, so its price cannot be rounded to a valid tick");
        if (cmd.LimitPrice is { } lp) replacement.Price = ATAS.Strategies.ATM.Extensions.ShrinkPrice(sec, lp);
        if (cmd.StopPrice is { } sp) replacement.TriggerPrice = ATAS.Strategies.ATM.Extensions.ShrinkPrice(sec, sp);

        var key = OrderKey(order);
        lock (_gate) _failures.Remove(key);

        if (IsStrategyOrder(order)) ModifyOrder(order, replacement);
        else Block(c.ModifyOrderAsync(order, replacement));

        // Settles as soon as ATAS refuses OR the live order visibly carries the change, so the
        // ordinary case does not sit on the timeout and stall the command loop behind it.
        bool Applied() =>
            (cmd.Quantity is not { } wantQty || order.QuantityToFill == wantQty)
            && (cmd.LimitPrice is null || order.Price == replacement.Price)
            && (cmd.StopPrice is null || order.TriggerPrice == replacement.TriggerPrice);

        WaitFor(() => Failure(key, order) is not null || Applied());
        if (Failure(key, order) is { } refusal) throw new AtasRejectedException(refusal);
        return ToOrder(order, null);
    }

    public void Cancel(string connectorOrderId)
    {
        var c = RequireConnector();
        var order = FindOrder(connectorOrderId)
            ?? throw new AtasRejectedException($"ATAS does not know order '{connectorOrderId}'; nothing was submitted");
        if (order.State is AtasOrderStates.Done or AtasOrderStates.Failed)
            throw new AtasRejectedException("order is not cancellable; nothing was submitted");

        var key = OrderKey(order);
        lock (_gate) _failures.Remove(key);

        if (IsStrategyOrder(order)) CancelOrder(order); else Block(c.CancelOrderAsync(order));

        WaitFor(() => Failure(key, order) is not null || order.State is AtasOrderStates.Done or AtasOrderStates.Failed);
        if (Failure(key, order) is { } refusal) throw new AtasRejectedException(refusal);
    }

    /// <summary>
    /// Best effort by design. One order ATAS definitively refuses to cancel (already done, already
    /// gone) must not stop the rest from being pulled — that is the emergency path. Anything
    /// ambiguous is different: those ids are collected and thrown afterwards, as an ordinary
    /// exception, so the gateway reconciles them instead of assuming they are flat.
    /// </summary>
    public IReadOnlyList<string> CancelAll(string accountId)
    {
        var cancelled = new List<string>();
        var ambiguous = new List<string>();
        foreach (var id in GetOrders(accountId, includeInactive: false, since: null).Select(o => o.ConnectorOrderId))
        {
            try { Cancel(id); cancelled.Add(id); }
            catch (AtasRejectedException) { /* definitively not cancellable; nothing is live */ }
            catch (Exception) { ambiguous.Add(id); }
        }
        if (ambiguous.Count > 0)
            throw new InvalidOperationException(
                $"cancel-all finished with an unknown outcome for {string.Join(", ", ambiguous)}; these must be reconciled");
        return cancelled;
    }

    /// <summary>
    /// Flattens through IDataFeedConnector.ClosePositionAsync, which is deliberate: ATAS decides the
    /// side. The dump gives no proof of the sign convention on Position.Volume, and a wrong sign
    /// here would not flatten a position, it would double it. So the side is never inferred.
    ///
    /// The cost is that the closing order does not carry our client id at submission time, so it is
    /// found afterwards by diffing the connector's order collection. If it cannot be identified,
    /// this throws an ORDINARY exception rather than returning null: the close was submitted, and
    /// reporting "no position" would be a lie the gateway would act on.
    /// </summary>
    public OrderInfo? ClosePosition(string accountId, string symbol, string clientOrderId)
    {
        var c = RequireConnector();
        var security = FindSecurity(symbol);
        if (security is null) return null;

        AtasPosition? position = null;
        foreach (var p in Items<AtasPosition>(c.Positions))
        {
            if (!AccountMatches(p.AccountID ?? p.Portfolio?.AccountID, accountId)) continue;
            if (!SymbolMatches(p.Security, p.SecurityId, symbol)) continue;
            if (p.Volume == 0m && !p.IsInPosition) continue;
            position = p;
            break;
        }
        if (position is null) return null;

        var before = new HashSet<string>(Items<AtasOrder>(c.Orders).Select(OrderKey), StringComparer.Ordinal);
        c.ClosePositionAsync(position).ConfigureAwait(false).GetAwaiter().GetResult();

        AtasOrder? created = null;
        WaitFor(() =>
        {
            created = Items<AtasOrder>(c.Orders)
                .FirstOrDefault(o => !before.Contains(OrderKey(o)) && SymbolMatches(o.Security, o.SecurityId, symbol));
            return created is not null;
        });

        if (created is null)
            throw new InvalidOperationException(
                $"ATAS accepted the close for {symbol} but the resulting order could not be identified; " +
                "it must be reconciled, not assumed flat");

        // Best effort only, and never counted as proof of a round trip: label the order ATAS created
        // so reconciliation has something of ours to match on.
        if (string.IsNullOrEmpty(created.Comment)) created.Comment = clientOrderId;
        return ToOrder(created, null);
    }

    // ---------------------------------------------------------------- events out

    public event Action<bool>? ConnectionChanged;
    public event Action<QuoteInfo>? QuoteChanged;
    public event Action<OrderInfo>? OrderChanged;
    public event Action<ExecutionInfo>? ExecutionReceived;
    public event Action<PositionInfo>? PositionChanged;
    public event Action<AccountInfo>? AccountChanged;

    /// <summary>
    /// Subscribes to the connector once.
    ///
    /// These handlers are never removed, and that is on purpose: every one of these events is a
    /// ConnectorEventHandler`N whose generic arguments the dump does not record, so the delegates
    /// cannot be stored in a typed field and '-=' with a fresh lambda would not remove anything
    /// anyway. Instead each handler compares the connector it was handed — the first parameter of
    /// every ConnectorEventHandler overload — against the live one and returns if they differ, so a
    /// subscription to a replaced connector goes inert rather than firing stale data.
    /// </summary>
    void HookConnector()
    {
        var c = Connector;
        if (c is null) return;
        lock (_gate)
        {
            if (ReferenceEquals(_hooked, c)) return;
            _hooked = c;
        }

        // Arity comes from the dump: ConnectorEventHandler.Invoke(connector),
        // ConnectorEventHandler`1.Invoke(connector, arg), `2.Invoke(connector, a1, a2),
        // `3.Invoke(connector, a1, a2, a3). Parameters stay implicitly typed so the generic
        // arguments never have to be named; payloads are widened to object and matched at runtime.
        c.Connected += conn => Guard(() => OnConnection(conn));
        c.Disconnected += conn => Guard(() => OnConnection(conn));
        c.ConnectionStateChanged += (conn, _) => Guard(() => OnConnection(conn));

        c.NewOrders += (conn, a) => Guard(() => OnOrderPayload(conn, a));
        c.OrderChanged += (conn, a) => Guard(() => OnOrderPayload(conn, a));
        c.NewMyTrades += (conn, a) => Guard(() => OnTradePayload(conn, a));
        c.NewPositions += (conn, a) => Guard(() => OnPositionPayload(conn, a));
        c.PositionsChanged += (conn, a) => Guard(() => OnPositionPayload(conn, a));
        c.NewPortfolios += (conn, a) => Guard(() => OnPortfolioPayload(conn, a));
        c.PortfoliosChanged += (conn, a) => Guard(() => OnPortfolioPayload(conn, a));
        c.BestBidAskUpdates += (conn, a) => Guard(() => OnQuotePayload(conn, a));
        c.SecurityChanged += (conn, a) => Guard(() => OnQuotePayload(conn, a));

        c.OrdersRegisterFailed += (conn, a, b) => Guard(() => OnFailurePayload(conn, a, b));
        c.OrdersCancelFailed += (conn, a, b) => Guard(() => OnFailurePayload(conn, a, b));
        c.OrderModifyFailed += (conn, a, b, d) => Guard(() => OnFailurePayload(conn, a, b, d));

        // Connector-level errors are NOT order rejections. They are recorded nowhere and never
        // become an AtasRejectedException; they only wake anything that is waiting.
        c.Error += (conn, _) => Guard(() => { if (IsLive(conn)) _pulse.Set(); });

        OnConnection(c);
    }

    bool IsLive(IFeedConnector? conn) => conn is not null && ReferenceEquals(conn, Connector);

    void OnConnection(IFeedConnector? conn)
    {
        if (!IsLive(conn)) return;
        _pulse.Set();
        ConnectionChanged?.Invoke(conn!.IsConnected);
    }

    void OnOrderPayload(IFeedConnector? conn, object? payload)
    {
        if (!IsLive(conn)) return;
        _pulse.Set();
        foreach (var o in Fan<AtasOrder>(payload))
        {
            if (!string.IsNullOrEmpty(o.Comment)) ProveClientOrderId(o.Comment);
            OrderChanged?.Invoke(ToOrder(o, null));
        }
    }

    void OnTradePayload(IFeedConnector? conn, object? payload)
    {
        if (!IsLive(conn)) return;
        _pulse.Set();
        foreach (var t in Fan<AtasMyTrade>(payload)) ExecutionReceived?.Invoke(ToExecution(t));
    }

    void OnPositionPayload(IFeedConnector? conn, object? payload)
    {
        if (!IsLive(conn)) return;
        _pulse.Set();
        foreach (var p in Fan<AtasPosition>(payload)) PositionChanged?.Invoke(ToPosition(p));
    }

    void OnPortfolioPayload(IFeedConnector? conn, object? payload)
    {
        if (!IsLive(conn)) return;
        _pulse.Set();
        foreach (var p in Fan<AtasPortfolio>(payload)) AccountChanged?.Invoke(ToAccount(p, conn!));
    }

    /// <summary>
    /// BestBidAskUpdates and SecurityChanged carry payloads whose generic arguments the dump does
    /// not record, so this pulls out whatever Securities it can find and re-reads the prices from
    /// them. Anything else in the payload is ignored rather than guessed at.
    /// </summary>
    void OnQuotePayload(IFeedConnector? conn, object? payload)
    {
        if (!IsLive(conn)) return;
        foreach (var s in Fan<AtasSecurity>(payload)) PublishQuote(s);
    }

    /// <summary>
    /// The only path that manufactures a definite refusal. It records a reason against every order
    /// it can positively identify in the payload; a failure it cannot attribute to a specific order
    /// is dropped, because attributing it to the wrong order is how a live order gets written off.
    /// </summary>
    void OnFailurePayload(IFeedConnector? conn, params object?[] payload)
    {
        if (!IsLive(conn)) return;

        string? reason = null;
        foreach (var part in payload)
        {
            if (part is string s && !string.IsNullOrWhiteSpace(s)) { reason ??= s; continue; }
            if (part is Exception ex) { reason ??= ex.Message; }
        }
        reason ??= "ATAS reported that the broker refused this order, without a reason";

        var orders = new List<AtasOrder>();
        foreach (var part in payload) orders.AddRange(Fan<AtasOrder>(part));
        if (orders.Count == 0) { _pulse.Set(); return; }

        lock (_gate)
        {
            foreach (var o in orders)
            {
                _failures[OrderKey(o)] = reason;
                if (!string.IsNullOrEmpty(o.Id)) _failures[o.Id] = reason;
                if (!string.IsNullOrEmpty(o.Comment)) _failures[o.Comment] = reason;
            }
        }
        _pulse.Set();
        foreach (var o in orders) OrderChanged?.Invoke(ToOrder(o, null));
    }

    // ---------------------------------------------------------------- quotes

    void Track(AtasSecurity s)
    {
        lock (_gate) { if (!_tracked.Add(s)) return; }
        // PropertyChangedEventHandler is a BCL delegate, so unlike the connector events this one can
        // be stored as a method group and removed again.
        s.PropertyChanged += OnSecurityPropertyChanged;
        SeedQuote(s);
    }

    void UntrackSecurities()
    {
        AtasSecurity[] tracked;
        lock (_gate) { tracked = [.. _tracked]; _tracked.Clear(); }
        foreach (var s in tracked) s.PropertyChanged -= OnSecurityPropertyChanged;
    }

    void OnSecurityPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is AtasSecurity s) Guard(() => PublishQuote(s));
    }

    /// <summary>Records the current prices WITHOUT a timestamp, so the first genuine move is
    /// detected as a move rather than mistaken for one.</summary>
    void SeedQuote(AtasSecurity s)
    {
        var key = SymbolOf(s);
        lock (_gate)
            if (!_quotes.ContainsKey(key))
                _quotes[key] = (s.BestBidPrice, s.BestAskPrice, s.LastTradePrice, DateTimeOffset.MinValue);
    }

    void PublishQuote(AtasSecurity s)
    {
        var key = SymbolOf(s);
        if (key.Length == 0) return;

        QuoteInfo quote;
        lock (_gate)
        {
            var bid = s.BestBidPrice;
            var ask = s.BestAskPrice;
            var last = s.LastTradePrice;
            if (_quotes.TryGetValue(key, out var prev) && prev.Bid == bid && prev.Ask == ask && prev.Last == last)
                return;
            var at = DateTimeOffset.UtcNow;
            _quotes[key] = (bid, ask, last, at);
            quote = BuildQuote(s, key, at);
        }
        QuoteChanged?.Invoke(quote);
    }

    static QuoteInfo BuildQuote(AtasSecurity s, string symbol, DateTimeOffset at) => new(
        symbol,
        s.BestBidPrice == 0m ? null : (decimal?)s.BestBidPrice,
        s.BestAskPrice == 0m ? null : (decimal?)s.BestAskPrice,
        s.LastTradePrice,
        s.BestBidVolume == 0m ? null : (decimal?)s.BestBidVolume,
        s.BestAskVolume == 0m ? null : (decimal?)s.BestAskVolume,
        at);

    // ---------------------------------------------------------------- mapping

    static AccountInfo ToAccount(AtasPortfolio p, IFeedConnector c) => new(
        p.AccountID ?? "",
        string.IsNullOrWhiteSpace(p.DepoName) ? p.AccountID ?? "" : p.DepoName,
        p.Currency?.ToString() ?? "",
        p.Balance,
        p.Balance + p.OpenPnL,
        p.OpenPnL,
        !p.IsRealAccount,
        !p.IsLocked && !p.IsSuspended && c.IsConnected && c.IsSupportedTradingFunctions);

    static InstrumentInfo ToInstrument(AtasSecurity s) => new(
        SymbolOf(s),
        string.IsNullOrWhiteSpace(s.Instrument) ? SymbolOf(s) : s.Instrument,
        s.Exchange ?? "",
        s.TickSize,
        s.TickCost,
        s.LotSize == 0m ? null : s.LotSize);

    /// <summary>
    /// Position carries no Id in the dump, so the natural key (account + symbol) stands in.
    ///
    /// The SIGN of Position.Volume is a semantic the dump cannot settle, so nothing that places an
    /// order reads it — see ClosePosition. It is reported here for display only.
    /// </summary>
    static PositionInfo ToPosition(AtasPosition p)
    {
        var account = p.AccountID ?? p.Portfolio?.AccountID ?? "";
        var symbol = SymbolOf(p.Security) is { Length: > 0 } s ? s : p.SecurityId ?? "";
        return new PositionInfo($"{account}:{symbol}", account, symbol, p.Volume, p.AveragePrice, p.UnrealizedPnL);
    }

    OrderInfo ToOrder(AtasOrder o, IReadOnlyDictionary<string, decimal>? fills)
    {
        var quantity = o.QuantityToFill;
        var filled = FilledOf(o, quantity, fills);
        var type = MapType(o);

        string? reason;
        lock (_gate) reason = Lookup(o);

        return new OrderInfo(
            OrderKey(o),
            string.IsNullOrEmpty(o.Comment) ? null : o.Comment,
            o.AccountID ?? o.Portfolio?.AccountID ?? "",
            SymbolOf(o.Security) is { Length: > 0 } s ? s : o.SecurityId ?? "",
            o.Direction == AtasDirections.Sell ? OrderSide.Sell : OrderSide.Buy,
            type,
            quantity,
            filled,
            (type is OrderType.Limit or OrderType.StopLimit) && o.Price != 0m ? (decimal?)o.Price : null,
            (type is OrderType.Stop or OrderType.StopLimit) && o.TriggerPrice != 0m ? (decimal?)o.TriggerPrice : null,
            MapState(o, quantity, filled),
            reason,
            ToOffset(o.Time));
    }

    static ExecutionInfo ToExecution(AtasMyTrade t) => new(
        t.Id ?? "",
        !string.IsNullOrEmpty(t.OrderId) ? t.OrderId : t.Order is { } o ? OrderKey(o) : "",
        string.IsNullOrEmpty(t.Order?.Comment) ? null : t.Order!.Comment,
        t.AccountID ?? t.Portfolio?.AccountID ?? "",
        SymbolOf(t.Security) is { Length: > 0 } s ? s : t.SecurityId ?? "",
        t.OrderDirection == AtasDirections.Sell ? OrderSide.Sell : OrderSide.Buy,
        t.Volume,
        t.Price,
        ToOffset(t.Time));

    /// <summary>
    /// OrderTypes.Unknown is a real value in the enum and has no counterpart in OrderType, so it is
    /// resolved from the prices ATAS did record rather than defaulted to Market.
    /// </summary>
    static OrderType MapType(AtasOrder o) => o.Type switch
    {
        AtasOrderTypes.Market => OrderType.Market,
        AtasOrderTypes.Limit => OrderType.Limit,
        AtasOrderTypes.Stop => OrderType.Stop,
        AtasOrderTypes.StopLimit => OrderType.StopLimit,
        _ => o.TriggerPrice != 0m && o.Price != 0m ? OrderType.StopLimit
            : o.TriggerPrice != 0m ? OrderType.Stop
            : o.Price != 0m ? OrderType.Limit
            : OrderType.Market
    };

    /// <summary>
    /// ATAS has four order states where TradeAgent has twelve, so the fill quantity does the rest of
    /// the work. OrderStates.None on an order that was never active means "submitted, no word yet";
    /// on one that HAS been active it means the state is genuinely unknown, which is the state that
    /// sends the gateway to reconcile instead of guessing.
    /// </summary>
    static ExecutionState MapState(AtasOrder o, decimal quantity, decimal filled) => o.State switch
    {
        AtasOrderStates.Failed => ExecutionState.REJECTED,
        AtasOrderStates.Active => filled > 0m ? ExecutionState.PARTIALLY_FILLED : ExecutionState.WORKING,
        AtasOrderStates.Done => quantity > 0m && filled >= quantity ? ExecutionState.FILLED : ExecutionState.CANCELLED,
        _ => o.WasActive ? ExecutionState.UNKNOWN : ExecutionState.DISPATCHING
    };

    /// <summary>
    /// Filled quantity from the trades themselves where there are any — the sum of my trades for an
    /// order is what was filled, by definition — falling back to QuantityToFill minus Unfilled.
    /// </summary>
    decimal FilledOf(AtasOrder o, decimal quantity, IReadOnlyDictionary<string, decimal>? fills)
    {
        var key = OrderKey(o);
        decimal traded = 0m;
        if (fills is not null) fills.TryGetValue(key, out traded);
        else
        {
            foreach (var t in Items<AtasMyTrade>(Connector?.MyTrades))
                if (TradeKey(t) == key) traded += t.Volume;
        }
        if (traded > 0m) return quantity > 0m ? Math.Min(traded, quantity) : traded;

        var remaining = quantity - o.Unfilled;
        return remaining <= 0m ? 0m : Math.Min(remaining, quantity);
    }

    IReadOnlyDictionary<string, decimal> FillsByOrder(IFeedConnector c)
    {
        var map = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var t in Items<AtasMyTrade>(c.MyTrades))
        {
            var key = TradeKey(t);
            if (key.Length == 0) continue;
            map[key] = map.TryGetValue(key, out var v) ? v + t.Volume : t.Volume;
        }
        return map;
    }

    static string TradeKey(AtasMyTrade t) =>
        !string.IsNullOrEmpty(t.OrderId) ? t.OrderId : t.Order is { } o ? OrderKey(o) : "";

    static AtasOrderTypes ToAtasType(OrderType t) => t switch
    {
        OrderType.Market => AtasOrderTypes.Market,
        OrderType.Limit => AtasOrderTypes.Limit,
        OrderType.Stop => AtasOrderTypes.Stop,
        OrderType.StopLimit => AtasOrderTypes.StopLimit,
        _ => AtasOrderTypes.Market
    };

    static AtasTif ToAtasTif(TimeInForce t) => t switch
    {
        TimeInForce.Day => AtasTif.Day,
        TimeInForce.GoodTillCancel => AtasTif.GoodTillCancel,
        TimeInForce.ImmediateOrCancel => AtasTif.ImmediateOrCancel,
        TimeInForce.FillOrKill => AtasTif.FillOrKill,
        _ => AtasTif.Default
    };

    /// <summary>
    /// ATAS entity times are plain DateTime with no documented kind. Unspecified is read as UTC,
    /// which is the only choice that cannot silently shift a timestamp by the machine's offset —
    /// and GetOrders never lets this filter drop a working order, so a wrong reading here cannot
    /// hide one.
    /// </summary>
    static DateTimeOffset ToOffset(DateTime t) => t.Kind switch
    {
        DateTimeKind.Utc => new DateTimeOffset(t, TimeSpan.Zero),
        DateTimeKind.Local => new DateTimeOffset(t),
        _ => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc), TimeSpan.Zero)
    };

    // ---------------------------------------------------------------- lookups

    IFeedConnector RequireConnector()
    {
        var c = Connector
            // Ordinary exception on purpose: "ATAS has no connection right now" says nothing about
            // whether an order already reached the broker, so the gateway must treat it as unknown.
            ?? throw new InvalidOperationException("this ATAS chart has no trading connection attached yet");
        HookConnector();
        // Cheap and idempotent. The chart's instrument may be attached after the connector is, and
        // an untracked security is one whose quotes never get an honest timestamp.
        if (Security is { } own) Track(own);
        return c;
    }

    AtasSecurity? FindSecurity(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;
        if (Security is { } own && SymbolMatches(own, own.SecurityId, symbol)) return own;
        foreach (var s in Items<AtasSecurity>(Connector?.Securities))
            if (SymbolMatches(s, s.SecurityId, symbol)) return s;
        return null;
    }

    AtasPortfolio? FindPortfolio(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return Portfolio;
        if (Portfolio is { } own && string.Equals(own.AccountID, accountId, StringComparison.OrdinalIgnoreCase)) return own;
        foreach (var p in Items<AtasPortfolio>(Connector?.Portfolios))
            if (string.Equals(p.AccountID, accountId, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    /// <summary>
    /// Identity first, client id only as a fallback — and never in the same pass.
    ///
    /// Cancel and Modify both start here, so "the first order that matches anything" is not good
    /// enough: an order whose Comment happened to equal another order's id would be cancelled in its
    /// place. So every broker/platform identity is checked across the whole book before any client
    /// id is considered.
    /// </summary>
    AtasOrder? FindOrder(string connectorOrderId)
    {
        if (string.IsNullOrWhiteSpace(connectorOrderId)) return null;

        foreach (var o in Items<AtasOrder>(Connector?.Orders)) if (IsSameOrder(o, connectorOrderId)) return o;
        foreach (var o in Items<AtasOrder>(Orders)) if (IsSameOrder(o, connectorOrderId)) return o;

        foreach (var o in Items<AtasOrder>(Connector?.Orders)) if (HasClientId(o, connectorOrderId)) return o;
        foreach (var o in Items<AtasOrder>(Orders)) if (HasClientId(o, connectorOrderId)) return o;

        lock (_gate) return _submitted.TryGetValue(connectorOrderId, out var mine) ? mine : null;
    }

    static bool IsSameOrder(AtasOrder o, string id) =>
        string.Equals(OrderKey(o), id, StringComparison.Ordinal)
        || (!string.IsNullOrEmpty(o.Id) && string.Equals(o.Id, id, StringComparison.Ordinal))
        || string.Equals(o.ExtId.ToString(CultureInfo.InvariantCulture), id, StringComparison.Ordinal);

    static bool HasClientId(AtasOrder o, string id) =>
        !string.IsNullOrEmpty(o.Comment) && string.Equals(o.Comment, id, StringComparison.Ordinal);

    bool IsStrategyOrder(AtasOrder order) => Items<AtasOrder>(Orders).Any(x => ReferenceEquals(x, order));

    static string OrderKey(AtasOrder o) =>
        !string.IsNullOrEmpty(o.Id) ? o.Id : $"ext:{o.ExtId.ToString(CultureInfo.InvariantCulture)}";

    static string SymbolOf(AtasSecurity? s) =>
        s is null ? "" : !string.IsNullOrWhiteSpace(s.Code) ? s.Code : s.SecurityId ?? "";

    static bool SymbolMatches(AtasSecurity? s, string? securityId, string symbol) =>
        (s is not null && (string.Equals(s.Code, symbol, StringComparison.OrdinalIgnoreCase)
                           || string.Equals(s.SecurityId, symbol, StringComparison.OrdinalIgnoreCase)
                           || string.Equals(s.Instrument, symbol, StringComparison.OrdinalIgnoreCase)))
        || string.Equals(securityId, symbol, StringComparison.OrdinalIgnoreCase);

    static bool AccountMatches(string? candidate, string wanted) =>
        string.IsNullOrWhiteSpace(wanted) || string.Equals(candidate, wanted, StringComparison.OrdinalIgnoreCase);

    string? Lookup(AtasOrder o)
    {
        if (_failures.TryGetValue(OrderKey(o), out var byKey)) return byKey;
        if (!string.IsNullOrEmpty(o.Id) && _failures.TryGetValue(o.Id, out var byId)) return byId;
        if (!string.IsNullOrEmpty(o.Comment) && _failures.TryGetValue(o.Comment, out var byComment)) return byComment;
        return null;
    }

    string? Failure(string key, AtasOrder o)
    {
        lock (_gate) return _failures.TryGetValue(key, out var direct) ? direct : Lookup(o);
    }

    /// <summary>
    /// This bridge is expected to stay loaded for weeks, so the two side tables cannot grow forever.
    /// Both are caches over data whose real home is ATAS's own order collection, so dropping them
    /// wholesale costs a reject reason on very old orders and nothing else — the caller holds the
    /// lock. Deliberately not a leak the user discovers as a slow memory climb months from now.
    /// </summary>
    void Trim()
    {
        const int cap = 4096;
        if (_submitted.Count > cap) _submitted.Clear();
        if (_failures.Count > cap) _failures.Clear();
    }

    /// <summary>
    /// Rule 1's proof, and the only thing that ever sets SupportsClientOrderId true.
    ///
    /// It requires the client id to be readable off an order sitting in the CONNECTOR's own
    /// collection, and that order to already carry a broker-assigned Id. What that proves is that
    /// ATAS carries the identifier alongside a real order for the life of the session, which is what
    /// reconciliation after a dropped pipe actually needs. What it does NOT prove is that the broker
    /// echoes the comment back after ATAS itself is restarted; nothing observable from inside a
    /// strategy can prove that, and it is not claimed anywhere.
    /// </summary>
    void ProveClientOrderId(string clientOrderId)
    {
        if (string.IsNullOrEmpty(clientOrderId)) return;
        lock (_gate) { if (_clientOrderIdProven) return; }

        var c = Connector;
        if (c is null) return;
        foreach (var o in Items<AtasOrder>(c.Orders))
        {
            if (!string.Equals(o.Comment, clientOrderId, StringComparison.Ordinal)) continue;
            if (string.IsNullOrEmpty(o.Id)) continue;
            lock (_gate) _clientOrderIdProven = true;
            return;
        }
    }

    // ---------------------------------------------------------------- plumbing

    /// <summary>Waits for a definite answer, and treats not getting one as exactly that — no
    /// exception, no rejection, just the order returned in whatever state it is really in.</summary>
    /// <summary>
    /// Waits for one of ATAS's async connector calls from a synchronous adapter method.
    ///
    /// The synchronous overloads exist but are [Obsolete], and an obsolete call is exactly the kind
    /// of thing that keeps working until a vendor update removes it — on the path that places real
    /// orders. Blocking here is safe because every caller is on the bridge's pipe-handling thread,
    /// never ATAS's UI thread, and ConfigureAwait(false) keeps it off any captured context.
    /// </summary>
    static void Block(Task task) => task.ConfigureAwait(false).GetAwaiter().GetResult();

    void WaitFor(Func<bool> settled)
    {
        var deadline = DateTime.UtcNow + AckTimeout;
        while (true)
        {
            if (settled()) return;
            if (DateTime.UtcNow >= deadline) return;
            _pulse.Wait(TimeSpan.FromMilliseconds(25));
            _pulse.Reset();
        }
    }

    /// <summary>
    /// An exception thrown out of an ATAS callback lands inside ATAS's own event dispatch, where it
    /// can take down unrelated subscribers or the platform's data loop. Nothing here is worth that,
    /// so callbacks fail silently and the next poll picks the state up instead.
    /// </summary>
    static void Guard(Action action)
    {
        try { action(); }
        catch (Exception) { /* never propagate into the platform's event dispatch */ }
    }

    /// <summary>
    /// Reads any ATAS collection without naming its generic argument — the dump records arity but
    /// not type arguments, and OfType&lt;T&gt;() over the non-generic IEnumerable is both
    /// compile-proof and type-checked at runtime.
    /// </summary>
    static IEnumerable<T> Items<T>(object? source) => source is IEnumerable e ? e.OfType<T>() : [];

    /// <summary>Handles a payload that may be one entity or a collection of them, since the dump
    /// does not say which of the two an event carries.</summary>
    static IEnumerable<T> Fan<T>(object? payload) => payload is T one ? [one] : Items<T>(payload);
}
#endif

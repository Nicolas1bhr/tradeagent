#if ATAS_SDK
using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
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
using IAtasDataProvider = ATAS.Indicators.IIndicatorDataProvider;
using IAtasOnlineData = ATAS.Indicators.IOnlineDataProvider;
using IAtasTrading = ATAS.Indicators.ITradingManager;
using IFeedConnector = ATAS.DataFeedsCore.IDataFeedConnector;

namespace TradeAgent.AtasBridge;

/// <summary>
/// The real ATAS adapter: the ONE file in this product that cannot be compiled or tested without
/// ATAS installed. Everything it plugs into — framing, heartbeat, reconnect, capability handshake,
/// error classification, the whole gateway — is already covered by tests using
/// <see cref="LoopbackAtasAdapter"/>.
///
/// WHICH ATAS SURFACE THIS BINDS TO, AND WHY IT CHANGED
///
/// It used to bind to <see cref="ChartStrategy.Connector"/>. That property EXISTS, has the right
/// type, compiles, and is **null at runtime for a chart strategy** — measured on ATAS 8.0.14.397
/// against a chart that was demonstrably attached to a portfolio (`Portfolio.AccountID` came back,
/// `IsSimulated = true`), while every read through the connector failed with "this ATAS chart has
/// no trading connection attached yet". A whole live run was spent on that.
///
/// The surface a chart strategy actually gets is <see cref="IAtasTrading"/>, reached from the
/// indicator's <see cref="IAtasDataProvider"/> (`DataProvider.TradingManager`). Everything below
/// requires THAT and nothing else.
///
/// The connector is demoted, not deleted. Where it is non-null — a different host, a future ATAS —
/// it is strictly richer than the trading manager: it alone has `Portfolios` (plural), `Securities`
/// (plural), `Positions` (plural), a socket-level `IsConnected`, and the `Factory` that is the one
/// route to ATAS's order-history cache. So it is used as ENRICHMENT everywhere and required nowhere.
///
/// HOW THIS FILE WAS WRITTEN
///
/// Against a reflection dump of the real ATAS 8.0.14.397 assemblies (ATAS.Strategies.dll,
/// ATAS.Indicators.dll, ATAS.DataFeedsCore.dll, Utils.Common.dll) taken from the install directory.
/// Every ATAS type, property, method and event named below was found in that dump, with three
/// documented exceptions, all flagged inline:
///
///   * the dump lists PUBLIC members only, so the protected lifecycle overrides
///     (<c>OnCalculate</c>, <c>OnStarted</c>, <c>OnStopping</c>) could not be confirmed from it.
///     Their names come from the official ATAS documentation instead, and the class deliberately
///     ALSO drives itself from the public <see cref="ChartStrategy.StateChanged"/> event so that
///     deleting those two overrides costs no functionality if their signature turns out to differ.
///   * the dump does not record generic ARGUMENTS (it prints <c>IEnumerable`1</c>, not
///     <c>IEnumerable&lt;Order&gt;</c>, and <c>Action`2</c>, not <c>Action&lt;Order,String&gt;</c>).
///     So no code here names one. Collections are read through the non-generic
///     <see cref="IEnumerable"/> with <c>OfType&lt;T&gt;()</c>, and every ATAS event is subscribed
///     with an implicitly-typed lambda whose payload is widened to <c>object</c> and then matched on
///     its runtime type. That is compile-proof against any generic argument AND type-safe at
///     runtime — it cannot silently read the wrong field off the wrong object. The event ARITIES
///     are dump-verified and are what the lambdas are shaped to.
///   * the dump does not record generic CONSTRAINTS anywhere in its 694 types, so the absence of one
///     on <c>IIndicatorDataProvider.GetService&lt;T&gt;()</c> proves nothing. See
///     <see cref="ResolveService"/> for why that single call is made reflectively.
///
/// The rules that are not compromised anywhere below:
///
///   1. ClientOrderId travels on <see cref="AtasOrder.Comment"/> and is read back out of ATAS's own
///      order collection. Describe() reports SupportsClientOrderId only after the round trip has
///      actually been OBSERVED at runtime, for an id THIS adapter submitted (see
///      <see cref="ProveClientOrderId"/>). It is false until then.
///   2. SupportsOrderHistory is ANSWERED AT RUNTIME. The one order-history query in the whole ATAS
///      surface lives on <see cref="IAtasCache"/>; <see cref="ProbeCache"/> tries every route to one
///      that exists on this platform and reports which route answered, so a false is legible as
///      "looked, found nothing" rather than "could not look". Never hard-coded true.
///   3. AtasRejectedException is raised only where nothing can still be live: a pre-flight refusal
///      that happened before submission, or an explicit ATAS order-failure event naming our order.
///      Timeouts, disconnects and unattributable failures propagate as ordinary exceptions.
///   4. No UI is touched. Orders go through ITradingManager's FLAGGED overloads with
///      askConfirmation: false — see <see cref="Place"/> for why the unflagged
///      <c>ChartStrategy.OpenOrder(Order)</c> is deliberately not used.
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

    /// <summary>The surfaces already subscribed to, so binding stays idempotent. Three separate
    /// fields because they are three separate objects with three separate lifetimes: the trading
    /// manager is the one that must exist, the online data provider is a quote source, and the
    /// connector may never be non-null at all.</summary>
    IAtasTrading? _hookedTrading;
    IAtasOnlineData? _hookedOnline;
    IFeedConnector? _hookedConnector;

    /// <summary>Last value pushed through <see cref="ConnectionChanged"/>, so a re-read that says
    /// the same thing does not spam the gateway. Null means nothing has been said yet.</summary>
    bool? _lastConnected;

    bool _clientOrderIdProven;

    /// <summary>
    /// What a false <c>SupportsClientOrderId</c> is actually saying. Attempts counts the orders we
    /// submitted carrying a client id; checks counts the times we then went and looked one of them
    /// up in ATAS's own order collection. Attempts with no checks means nothing ever came back to
    /// examine — a very different fact from a read-back that ran and found nothing.
    ///
    /// Deliberately NOT derived from <see cref="_submitted"/>, whose count Trim() resets to zero
    /// after 4096 orders: a diagnostic that silently rewinds to "never attempted" is worse than no
    /// diagnostic. These only ever increase.
    /// </summary>
    int _clientOrderIdAttempts, _clientOrderIdChecks;

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
        TryBind();
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

    // ---------------------------------------------------------------- the bound surfaces

    /// <summary>
    /// The trading surface for a chart strategy, and the reason this file was rewritten.
    ///
    /// <c>DataProvider</c> is dump-verified on ATAS.Indicators.ExtendedIndicator, which
    /// Indicator — and therefore ChartStrategy — derives from. Both hops are nullable and both are
    /// null at different, distinguishable moments: no DataProvider means the strategy is not
    /// attached to a chart at all, and a DataProvider with no TradingManager means it is attached to
    /// a chart that has no trading connection. Describe() reports which.
    /// </summary>
    IAtasTrading? Trading => DataProvider?.TradingManager;

    /// <summary>The portfolio this chart trades. Both spellings are read because both exist and only
    /// one has been MEASURED populated: ChartStrategy.Portfolio came back with an AccountID on the
    /// live machine. Which of the two ATAS fills in first has not been measured.</summary>
    AtasPortfolio? BoundPortfolio => Trading?.Portfolio ?? Portfolio;

    /// <inheritdoc cref="BoundPortfolio"/>
    AtasSecurity? BoundSecurity => Trading?.Security ?? Security;

    /// <summary>
    /// Replaces the old RequireConnector(). Same contract — throw something an operator can act on —
    /// but it names the real cause instead of the one that cost a live run.
    ///
    /// Ordinary exceptions on purpose, never AtasRejectedException: "ATAS has no trading surface
    /// right now" says nothing about whether an order already reached the broker, so the gateway
    /// must treat it as unknown and reconcile (rule 3).
    /// </summary>
    IAtasTrading RequireTrading()
    {
        var provider = DataProvider
            ?? throw new InvalidOperationException(
                "this TradeAgent Bridge strategy is not attached to an ATAS chart yet (the chart has " +
                "given it no data provider), so it has no trading surface at all — add it to a chart " +
                "and start it");

        var trading = provider.TradingManager
            ?? throw new InvalidOperationException(
                "this ATAS chart has no trading manager, so no account is attached to it yet — " +
                "connect ATAS to a broker and select a portfolio on this chart");

        Bind(provider, trading);
        return trading;
    }

    /// <summary>Binds whatever is available without ever throwing. Used by Describe() and by
    /// StartBridge, both of which must survive being called before ATAS has finished attaching the
    /// chart — reporting "nothing is bound" is a legitimate answer for them, unlike for a read.</summary>
    void TryBind()
    {
        Guard(() =>
        {
            if (DataProvider is not { } provider) return;
            Bind(provider, provider.TradingManager);
        });
    }

    void Bind(IAtasDataProvider provider, IAtasTrading? trading)
    {
        if (trading is not null) HookTrading(trading);
        HookOnline(provider.OnlineDataProvider);
        HookConnector(Connector);
        // Cheap and idempotent. The chart's instrument may be attached after the trading manager is,
        // and an untracked security is one whose quotes never get an honest timestamp.
        if (BoundSecurity is { } own) Track(own);
        PublishConnection();
    }

    // ---------------------------------------------------------------- handshake

    public BridgeHello Describe()
    {
        // Binding here as well as at Start(): Describe() is the first thing TradeAgent asks, and a
        // chart that finished attaching after the strategy started would otherwise report an empty
        // surface until the first read came in.
        TryBind();

        var portfolio = BoundPortfolio;
        var cache = ProbeCache(portfolio?.AccountID);

        bool proven;
        int attempts, checks;
        lock (_gate) { proven = _clientOrderIdProven; attempts = _clientOrderIdAttempts; checks = _clientOrderIdChecks; }

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
            // Why it is false, when it is. Diagnostic only — see BridgeHello.ClientOrderIdAttempts.
            ClientOrderIdAttempts = attempts,
            ClientOrderIdChecks = checks,
            // Rule 2, and it is answered at runtime for the same reason rule 1 is. ProbeCache tries
            // every route to an ICache that exists on this platform and confirms the one it finds
            // actually knows THIS account, because a cache that does not would answer GetOrders with
            // a short list — and a short list makes "this order does not exist" look provable when
            // it is not. False means the gateway withholds autonomous live trading.
            SupportsOrderHistory = cache.Cache is not null,
            // What was actually bound, and what was found there. Free text, diagnostic only, and the
            // one field that can say "I looked at the wrong object" — which is exactly the failure
            // that cost the first live run and which no capability boolean can express.
            TradingSurface = SurfaceReport(portfolio, cache.Note),
            SupportsModify = true,
            SupportsClosePosition = true
        };
    }

    /// <summary>
    /// A short, factual, single-line account of the surface this adapter is bound to RIGHT NOW.
    ///
    /// Every value in it is read; nothing is formatted that was not read. The counts are wrapped
    /// individually so that a collection that throws while being enumerated reports <c>err</c>
    /// rather than taking the whole handshake down — and, more importantly, so that "I could not
    /// look" and "I looked and there was nothing" are different strings on the wire.
    /// </summary>
    string SurfaceReport(AtasPortfolio? portfolio, string cacheNote)
    {
        try
        {
            var provider = DataProvider;
            var trading = provider?.TradingManager;
            var connector = Connector;

            return string.Join(' ',
                $"DataProvider={(provider is null ? "null" : "ok")}",
                // "unreachable" rather than "null": with no data provider the trading manager was
                // never asked for, which is a different fact from having asked and got null.
                $"TradingManager={(provider is null ? "unreachable" : trading is null ? "null" : "ok")}",
                $"Connector={(connector is null ? "null" : "ok")}",
                $"orders={Count(trading?.Orders)}",
                // Reported separately from orders= on purpose: whether ChartStrategy.Orders and
                // ITradingManager.Orders are the SAME list has never been measured, and these two
                // numbers side by side are the cheapest reading that would settle it.
                $"strategyorders={Count(Orders)}",
                $"mytrades={Count(trading?.MyTrades)}",
                $"portfolio={Token(portfolio?.AccountID)}",
                $"security={Token(SymbolOf(BoundSecurity))}",
                $"position={(trading?.Position is { } p ? p.Volume.ToString(CultureInfo.InvariantCulture) : "none")}",
                $"cache={cacheNote}");
        }
        catch (Exception ex)
        {
            // Never let the diagnostic be the thing that breaks the handshake.
            return $"surface=unreadable({ex.GetType().Name})";
        }
    }

    static string Count(object? source)
    {
        try { return Items<object>(source).Count().ToString(CultureInfo.InvariantCulture); }
        catch (Exception) { return "err"; }
    }

    /// <summary>One line, one token: the surface report is whitespace-separated, so a value
    /// containing a space would silently split into two fields.</summary>
    static string Token(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "none";
        var kept = new string(raw.Where(c => !char.IsControl(c) && !char.IsWhiteSpace(c)).Take(24).ToArray());
        return kept.Length == 0 ? "none" : kept;
    }

    // ---------------------------------------------------------------- reads

    /// <summary>
    /// ONE account, and that is a property of the surface rather than a bug.
    ///
    /// ITradingManager has no plural collections at all — `Portfolio`, singular, is the whole of it.
    /// A chart strategy is attached to one chart, which is attached to one portfolio, and ATAS gives
    /// it no way to enumerate the others. So this returns the one account that is genuinely visible.
    /// It does not invent entries to look complete, and it does not throw because there is only one:
    /// one real account is a true answer, and BridgeHello.TradingSurface carries `Connector=null` so
    /// a reader can see WHY there is only one.
    ///
    /// Where a connector does exist it is strictly richer, so its Portfolios are folded in.
    /// </summary>
    public IReadOnlyList<AccountInfo> GetAccounts()
    {
        var trading = RequireTrading();
        var connector = Connector;
        var list = new List<AccountInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Take(AtasPortfolio? p)
        {
            if (p is null) return;
            if (!seen.Add(p.AccountID ?? "")) return;
            list.Add(ToAccount(p, connector));
        }

        Take(trading.Portfolio ?? Portfolio);
        foreach (var p in Items<AtasPortfolio>(connector?.Portfolios)) Take(p);
        return list;
    }

    /// <summary>
    /// At least one instrument — the chart's own — for the same reason GetAccounts returns one
    /// account: ITradingManager exposes `Security`, singular. That is what a chart strategy can see,
    /// not a defect in the reading.
    /// </summary>
    public IReadOnlyList<InstrumentInfo> GetInstruments()
    {
        var trading = RequireTrading();
        var list = new List<InstrumentInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Take(AtasSecurity? s)
        {
            if (s is null) return;
            var symbol = SymbolOf(s);
            if (symbol.Length == 0 || !seen.Add(symbol)) return;
            list.Add(ToInstrument(s));
        }

        // The chart's own instrument goes first: it is the one this strategy can trade, and it is
        // the one the user is looking at.
        Take(trading.Security ?? Security);
        foreach (var s in Items<AtasSecurity>(Connector?.Securities)) Take(s);
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

    /// <summary>
    /// One position, for the same reason as one account: ITradingManager exposes `Position`,
    /// singular — the position on this chart's instrument in this chart's portfolio. Connector
    /// positions are folded in where a connector exists.
    /// </summary>
    public IReadOnlyList<PositionInfo> GetPositions(string accountId)
    {
        var trading = RequireTrading();
        var byKey = new Dictionary<string, PositionInfo>(StringComparer.Ordinal);

        void Take(AtasPosition? p)
        {
            if (p is null) return;
            if (!AccountMatches(p.AccountID ?? p.Portfolio?.AccountID, accountId)) return;
            var info = ToPosition(p);
            byKey.TryAdd(info.Id, info);
        }

        Take(trading.Position);
        foreach (var p in Items<AtasPosition>(Connector?.Positions)) Take(p);
        return [.. byKey.Values];
    }

    /// <summary>
    /// Rule 2 in practice.
    ///
    /// The live book always comes from the trading manager (plus the connector where one exists).
    /// Finished orders additionally come from ATAS's order cache when one is reachable — and when it
    /// is not, none are claimed and Describe() has already said SupportsOrderHistory = false.
    ///
    /// Two things it will never do. It will never let the 'since' filter drop an order that is still
    /// working, because a working order hidden from reconciliation is the failure that loses money.
    /// And when asked for a window older than ATAS is configured to keep, it refuses outright rather
    /// than answering with a list that looks complete: a partial history makes "this order does not
    /// exist" look provable when it is not.
    /// </summary>
    public IReadOnlyList<OrderInfo> GetOrders(string accountId, bool includeInactive, DateTimeOffset? since)
    {
        RequireTrading();
        var fills = FillsByOrder();
        var cache = includeInactive && !string.IsNullOrWhiteSpace(accountId) ? ProbeCache(accountId).Cache : null;

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

        foreach (var o in LiveOrders()) Take(o);
        if (cache is not null) foreach (var o in Items<AtasOrder>(cache.GetOrders(accountId))) Take(o);
        return [.. byKey.Values];
    }

    /// <summary>What a cache probe found, and — just as important — how it failed when it did not.
    /// The note goes straight onto the wire in BridgeHello.TradingSurface.</summary>
    readonly record struct CacheProbe(IAtasCache? Cache, string Note);

    /// <summary>
    /// The whole basis for rule 2's answer, and it is a runtime question, not a guess.
    ///
    /// There is exactly one order-history query in the four ATAS assemblies:
    /// ATAS.DataFeedsCore.Database.ICache.GetOrders(String accountId). Nothing in the public surface
    /// hands you an ICache, so this tries the two routes that exist and says which one answered:
    ///
    ///   1. IDataFeedConnector.Factory, typed IEntityFactory — and the concrete
    ///      ATAS.DataFeedsCore.Database.Cache implements ICache and IEntityFactory on the same
    ///      object. This is the route the old code used, and it is the reason SupportsOrderHistory
    ///      was previously meaningless: Connector is null, so it could only ever return null and its
    ///      false meant "could not look".
    ///   2. IIndicatorDataProvider.GetService&lt;T&gt;(), the indicator's own service locator, which
    ///      IS reachable from a chart strategy.
    ///
    /// A cache found by either route is then CONFIRMED, not assumed: it must be initialised, and it
    /// must know the account being asked about. Rule 2 says a partial history is worse than none, and
    /// a cache belonging to some other configuration would answer GetOrders with a short list that
    /// looks complete. Nothing here can make SupportsOrderHistory true by accident.
    /// </summary>
    CacheProbe ProbeCache(string? accountId)
    {
        try
        {
            // Route 1. Typed and dump-verified. Null on a chart strategy today; kept because where a
            // connector DOES exist this is the authoritative cache for that connection.
            if (Connector?.Factory is IAtasCache byFactory) return Confirm(byFactory, "connector.factory", accountId);

            // Route 2.
            if (DataProvider is not { } provider)
                return new CacheProbe(null, "none(no-dataprovider)");

            var (service, note) = ResolveService(provider, typeof(IAtasCache));
            if (service is IAtasCache byService) return Confirm(byService, "getservice", accountId);
            // The locator answered with something that is not an ICache. Distinct from a plain miss:
            // it means the route works and the platform simply does not register a cache under this
            // type, which is a different next step from "the call could not be made at all".
            if (service is not null) note = "getservice-wrongtype";

            var factoryNote = Connector is null ? "connector-null" : "factory-not-cache";
            return new CacheProbe(null, $"none({factoryNote},{note})");
        }
        catch (Exception ex)
        {
            // Distinct from none(): the probe itself failed, so this is "could not look".
            return new CacheProbe(null, $"err({ex.GetType().Name})");
        }
    }

    CacheProbe Confirm(IAtasCache cache, string via, string? accountId)
    {
        try
        {
            if (!cache.IsInitialized) return new CacheProbe(null, $"uninit({via})");

            // Rule 2. A cache that has never heard of this account would answer GetOrders(accountId)
            // with an empty or short list, and that is exactly the answer that makes "this order does
            // not exist" look provable when it is not. GetPortfolio is dump-verified on ICache and is
            // the cheapest question that settles it.
            if (!string.IsNullOrWhiteSpace(accountId) && cache.GetPortfolio(accountId) is null)
                return new CacheProbe(null, $"foreign({via})");

            return new CacheProbe(cache, $"ok({via})");
        }
        catch (Exception) { return new CacheProbe(null, $"err({via})"); }
    }

    /// <summary>
    /// Calls IIndicatorDataProvider.GetService&lt;T&gt;() reflectively, and this is the one place in
    /// the file that reaches for reflection on purpose.
    ///
    /// The dump records `T GetService()` and does not record generic CONSTRAINTS — it prints none
    /// anywhere across 694 types, so their absence is not evidence of absence. If GetService&lt;T&gt;
    /// is constrained to some ATAS service marker, `GetService&lt;ICache&gt;()` written directly is a
    /// COMPILE error, on the one file in this product that can only be compiled on a machine this
    /// session cannot reach. Reflection turns that unknown into a runtime fact — and, critically,
    /// into a fact this method can NAME: getservice-absent, -constrained, -null and -threw are four
    /// different strings, so the resulting SupportsOrderHistory = false stays legible instead of
    /// collapsing back into "could not look".
    ///
    /// The method is looked up on the INTERFACE and invoked on the instance, so an explicit interface
    /// implementation is found too. Nothing here can throw into ATAS.
    /// </summary>
    static (object? Service, string Note) ResolveService(IAtasDataProvider provider, Type wanted)
    {
        try
        {
            var definition = typeof(IAtasDataProvider).GetMethods()
                .FirstOrDefault(m => m.Name == "GetService"
                                     && m.IsGenericMethodDefinition
                                     && m.GetGenericArguments().Length == 1
                                     && m.GetParameters().Length == 0);
            if (definition is null) return (null, "getservice-absent");

            MethodInfo bound;
            try { bound = definition.MakeGenericMethod(wanted); }
            // Thrown when the type argument violates a constraint the dump could not show us.
            catch (ArgumentException) { return (null, "getservice-constrained"); }

            // Written as statements rather than a conditional expression: a tuple literal whose
            // first element is a bare null has no natural type, and relying on target typing through
            // a conditional is not something to discover on a machine this session cannot compile on.
            var value = bound.Invoke(provider, null);
            if (value is null) return (null, "getservice-null");
            return (value, "getservice");
        }
        catch (Exception) { return (null, "getservice-threw"); }
    }

    public IReadOnlyList<ExecutionInfo> GetExecutions(string accountId, DateTimeOffset? since)
    {
        RequireTrading();
        var list = new List<ExecutionInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in LiveTrades())
        {
            if (!AccountMatches(t.AccountID ?? t.Portfolio?.AccountID, accountId)) continue;
            var e = ToExecution(t);
            if (since is not null && e.At < since.Value) continue;
            // LiveTrades reads more than one collection and they may overlap, so an execution id
            // that has already been reported is dropped rather than duplicated.
            if (e.ExecutionId.Length > 0 && !seen.Add(e.ExecutionId)) continue;
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

        var trading = RequireTrading();
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
            // Counted here rather than after the round trip, because the question this answers is
            // "was anything ever put to ATAS carrying an id" — and an order that failed on the way
            // out was still an attempt.
            if (!string.IsNullOrEmpty(cmd.ClientOrderId)) _clientOrderIdAttempts++;
        }

        // Whether ITradingManager will place an order for an instrument or portfolio OTHER than its
        // own selected pair has NOT been measured. Where a connector exists it definitely will, so an
        // off-chart order prefers it; where one does not — the chart-strategy case — the trading
        // manager is asked anyway and any refusal surfaces as an exception rather than a quiet no-op.
        var offChart = !ReferenceEquals(security, trading.Security)
                       || !ReferenceEquals(portfolio, trading.Portfolio);
        var feed = offChart ? Connector : null;

        // From here on nothing may be reported as REJECTED unless ATAS says so explicitly: once the
        // order has been handed to ATAS, it may exist at the broker.
        //
        // WHY THE FLAGGED OVERLOAD, ALWAYS, AND WITH THESE EXACT FLAGS:
        //
        //   setDefaultQuantity: false — true lets the platform overwrite the size TradeAgent
        //       computed with whatever is selected in the DOM's volume selector. The whole gateway
        //       sizes orders deliberately; letting a UI control replace that number is not an option.
        //   askConfirmation: false — true pops a modal dialog. That would hang an unattended order
        //       forever AND would be placing an order through a user interface, which rule 4 forbids
        //       outright.
        //   checkOrderStates: true — asks ATAS to validate rather than silently accept. The exact
        //       semantics are NOT in the dump; it is set true because "let the platform object" is
        //       the direction to fail in, and any objection arrives as an exception we propagate.
        //
        // ChartStrategy.OpenOrder(Order) — the overload with no flags — is deliberately NOT used,
        // not even as a fallback. Its confirmation behaviour is not in the dump, and an unflagged
        // call that MIGHT ask for confirmation is exactly the rule 4 hazard the flags above exist to
        // remove. There is no situation where it is reachable and the flagged overload is not: both
        // require a trading manager, and RequireTrading() has already thrown without one.
        if (feed is not null) Block(feed.RegisterOrderAsync(order));
        else trading.OpenOrder(order, setDefaultQuantity: false, askConfirmation: false, checkOrderStates: true);

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
        var trading = RequireTrading();
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

        // Same flag reasoning as Place: askConfirmation: false is rule 4, not a preference.
        // ChartStrategy.ModifyOrder(order, newOrder) is not used for the same reason its OpenOrder is
        // not — and note that routing on "is this a strategy order" would be actively dangerous here,
        // because whether ChartStrategy.Orders and ITradingManager.Orders are the SAME list has never
        // been measured. If they are, every order would take the unflagged path.
        trading.ModifyOrder(order, replacement, askConfirmation: false, checkOrderStates: true);

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
        var trading = RequireTrading();
        var order = FindOrder(connectorOrderId)
            ?? throw new AtasRejectedException($"ATAS does not know order '{connectorOrderId}'; nothing was submitted");
        if (order.State is AtasOrderStates.Done or AtasOrderStates.Failed)
            throw new AtasRejectedException("order is not cancellable; nothing was submitted");

        var key = OrderKey(order);
        lock (_gate) _failures.Remove(key);

        trading.CancelOrder(order, askConfirmation: false, checkOrderStates: true);

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
    /// Flattens through ITradingManager.ClosePosition, which is deliberate: ATAS decides the side.
    /// The dump gives no proof of the sign convention on Position.Volume, and a wrong sign here would
    /// not flatten a position, it would double it. So the side is never inferred.
    ///
    /// The cost is that the closing order does not carry our client id at submission time, so it is
    /// found afterwards by diffing ATAS's order collection. If it cannot be identified, this throws
    /// an ORDINARY exception rather than returning null: the close was submitted, and reporting "no
    /// position" would be a lie the gateway would act on.
    /// </summary>
    public OrderInfo? ClosePosition(string accountId, string symbol, string clientOrderId)
    {
        var trading = RequireTrading();
        var security = FindSecurity(symbol);
        if (security is null) return null;

        // The trading manager's own position first — it is the one this chart trades — then any the
        // connector can see, where a connector exists at all.
        IEnumerable<AtasPosition> Candidates()
        {
            if (trading.Position is { } own) yield return own;
            foreach (var p in Items<AtasPosition>(Connector?.Positions)) yield return p;
        }

        AtasPosition? position = null;
        foreach (var p in Candidates())
        {
            if (!AccountMatches(p.AccountID ?? p.Portfolio?.AccountID, accountId)) continue;
            if (!SymbolMatches(p.Security, p.SecurityId, symbol)) continue;
            if (p.Volume == 0m && !p.IsInPosition) continue;
            position = p;
            break;
        }
        if (position is null) return null;

        var before = new HashSet<string>(LiveOrders().Select(OrderKey), StringComparer.Ordinal);

        // Same flags, same reasons. The boolean this returns has no documented meaning in the dump,
        // so it is NOT treated as a definite refusal — a false becomes part of the message below if
        // no order appears, and rule 3 keeps that an ordinary exception so the gateway reconciles.
        var accepted = trading.ClosePosition(position, askConfirmation: false, checkOrderStates: true);

        AtasOrder? created = null;
        WaitFor(() =>
        {
            created = LiveOrders()
                .FirstOrDefault(o => !before.Contains(OrderKey(o)) && SymbolMatches(o.Security, o.SecurityId, symbol));
            return created is not null;
        });

        if (created is null)
            throw new InvalidOperationException(
                $"ATAS was asked to close {symbol} (it returned {(accepted ? "true" : "false")}) but the " +
                "resulting order could not be identified; it must be reconciled, not assumed flat");

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
    /// Subscribes to the trading manager once.
    ///
    /// ITradingManager's events are plain Action`1 / Action`2 / Action`3 — dump-verified ARITIES —
    /// so unlike the connector's ConnectorEventHandler`N these are subscribed directly. The lambda
    /// parameters stay implicitly typed because the dump does not record generic ARGUMENTS; the
    /// payloads are widened to object and matched on their runtime type, which is compile-proof
    /// against whatever those arguments turn out to be and still cannot read the wrong field off the
    /// wrong object.
    ///
    /// These handlers are never removed. A fresh lambda cannot be removed with '-=' anyway, so
    /// instead each handler closes over the manager it was subscribed to and compares it against the
    /// live one — a subscription to a replaced surface goes inert rather than firing stale data.
    /// </summary>
    void HookTrading(IAtasTrading trading)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_hookedTrading, trading)) return;
            _hookedTrading = trading;
        }

        trading.NewOrder += a => Guard(() => OnOrderPayload(trading, a));
        trading.OrderChanged += a => Guard(() => OnOrderPayload(trading, a));
        trading.NewMyTrade += a => Guard(() => OnTradePayload(trading, a));
        trading.PositionChanged += a => Guard(() => OnPositionPayload(trading, a));
        trading.PortfolioChanged += a => Guard(() => OnPortfolioPayload(trading, a));
        // PortfolioSelected/SecuritySelected mean the chart has been pointed at a different account
        // or instrument. Both are re-read rather than assumed: the payload may or may not be the
        // entity itself, and the bound properties are authoritative either way.
        trading.PortfolioSelected += a => Guard(() => OnPortfolioPayload(trading, a));
        trading.SecuritySelected += a => Guard(() => OnSecurityPayload(trading, a));

        // The definite-refusal signals, and the only path that manufactures an AtasRejectedException.
        trading.OrderRegisterFailed += (a, b) => Guard(() => OnFailurePayload(trading, a, b));
        trading.OrderCancelFailed += (a, b) => Guard(() => OnFailurePayload(trading, a, b));
        trading.OrderModifyFailed += (a, b, c) => Guard(() => OnFailurePayload(trading, a, b, c));
    }

    /// <summary>
    /// The chart's quote feed. Quotes do NOT come from the connector on this surface — they come off
    /// Security.PropertyChanged, which Track() subscribes to — so this is an additional wake-up, not
    /// the only source. It never stamps a quote it did not see move: PublishQuote compares against
    /// the last observed prices and returns without emitting when nothing changed, so a spurious
    /// BestBidAskChanged cannot manufacture freshness.
    /// </summary>
    void HookOnline(IAtasOnlineData? online)
    {
        if (online is null) return;
        lock (_gate)
        {
            if (ReferenceEquals(_hookedOnline, online)) return;
            _hookedOnline = online;
        }

        // Action`1, arity dump-verified; the generic argument is not, so the payload is widened. If
        // it carries Securities they are published; otherwise the chart's own instrument is re-read,
        // which is what the event is about by construction — this is the chart's data provider.
        online.BestBidAskChanged += a => Guard(() =>
        {
            var any = false;
            foreach (var s in Fan<AtasSecurity>(a)) { any = true; PublishQuote(s); }
            if (!any && BoundSecurity is { } own) PublishQuote(own);
        });
    }

    /// <summary>
    /// The connector path, kept for hosts where Connector is NOT null. It is the only surface with a
    /// socket-level connection signal, so where it exists it is what ConnectionChanged reports.
    /// Nothing requires it and nothing below runs on a chart strategy today.
    /// </summary>
    void HookConnector(IFeedConnector? connector)
    {
        if (connector is null) return;
        lock (_gate)
        {
            if (ReferenceEquals(_hookedConnector, connector)) return;
            _hookedConnector = connector;
        }

        // Arity comes from the dump: ConnectorEventHandler.Invoke(connector),
        // ConnectorEventHandler`1.Invoke(connector, arg). Parameters stay implicitly typed so the
        // generic arguments never have to be named.
        connector.Connected += _ => Guard(PublishConnection);
        connector.Disconnected += _ => Guard(PublishConnection);
        connector.ConnectionStateChanged += (_, _) => Guard(PublishConnection);

        // Connector-level errors are NOT order rejections. They are recorded nowhere and never
        // become an AtasRejectedException; they only wake anything that is waiting.
        connector.Error += (_, _) => Guard(() => _pulse.Set());
    }

    /// <summary>
    /// The equivalent of the old IsLive(connector): an event that arrives from a surface ATAS has
    /// since replaced must be ignored rather than reported as current.
    /// </summary>
    bool IsLive(IAtasTrading? trading) => trading is not null && ReferenceEquals(trading, Trading);

    /// <summary>
    /// What "connected" can honestly mean on each surface, and the two are not the same claim.
    ///
    ///   * With a connector: IDataFeedConnector.IsConnected, a real socket-level fact.
    ///   * Without one: ATAS gives a chart strategy NO socket-level signal at all. The strongest
    ///     thing that can be observed is that a trading manager is bound and has a portfolio, so
    ///     that is what is reported — "a trading surface with an account is attached", not "the
    ///     broker link is up". BridgeHello.TradingSurface carries Connector=null so a reader can
    ///     tell which of the two answered, and the gateway's own heartbeat staleness check is what
    ///     actually catches a dead pipe.
    /// </summary>
    void PublishConnection()
    {
        var connector = Connector;
        var connected = connector is not null ? connector.IsConnected : (Trading?.Portfolio is not null);

        _pulse.Set();

        bool changed;
        lock (_gate) { changed = _lastConnected != connected; _lastConnected = connected; }
        if (changed) ConnectionChanged?.Invoke(connected);
    }

    void OnOrderPayload(IAtasTrading trading, object? payload)
    {
        if (!IsLive(trading)) return;
        _pulse.Set();
        foreach (var o in Fan<AtasOrder>(payload))
        {
            if (!string.IsNullOrEmpty(o.Comment)) ProveClientOrderId(o.Comment);
            OrderChanged?.Invoke(ToOrder(o, null));
        }
    }

    void OnTradePayload(IAtasTrading trading, object? payload)
    {
        if (!IsLive(trading)) return;
        _pulse.Set();
        foreach (var t in Fan<AtasMyTrade>(payload)) ExecutionReceived?.Invoke(ToExecution(t));
    }

    void OnPositionPayload(IAtasTrading trading, object? payload)
    {
        if (!IsLive(trading)) return;
        _pulse.Set();
        var any = false;
        foreach (var p in Fan<AtasPosition>(payload)) { any = true; PositionChanged?.Invoke(ToPosition(p)); }
        // The payload's generic argument is not dump-verified, so when nothing recognisable came
        // through, the bound position is re-read rather than the event being dropped.
        if (!any && trading.Position is { } own) PositionChanged?.Invoke(ToPosition(own));
    }

    void OnPortfolioPayload(IAtasTrading trading, object? payload)
    {
        if (!IsLive(trading)) return;
        _pulse.Set();
        // A portfolio arriving or leaving changes what "connected" means on the trading-manager
        // surface, so the connection reading is refreshed before the account is reported.
        PublishConnection();

        var connector = Connector;
        var any = false;
        foreach (var p in Fan<AtasPortfolio>(payload)) { any = true; AccountChanged?.Invoke(ToAccount(p, connector)); }
        if (!any && trading.Portfolio is { } own) AccountChanged?.Invoke(ToAccount(own, connector));
    }

    void OnSecurityPayload(IAtasTrading trading, object? payload)
    {
        if (!IsLive(trading)) return;
        _pulse.Set();
        var any = false;
        foreach (var s in Fan<AtasSecurity>(payload)) { any = true; Track(s); PublishQuote(s); }
        if (!any && trading.Security is { } own) { Track(own); PublishQuote(own); }
    }

    /// <summary>
    /// The only path that manufactures a definite refusal. It records a reason against every order
    /// it can positively identify in the payload; a failure it cannot attribute to a specific order
    /// is dropped, because attributing it to the wrong order is how a live order gets written off.
    ///
    /// The payload shapes are dump-verified by ARITY only — OrderRegisterFailed and
    /// OrderCancelFailed are Action`2, OrderModifyFailed is Action`3 — so every element is inspected
    /// for an order and for a reason rather than being read positionally.
    /// </summary>
    void OnFailurePayload(IAtasTrading trading, params object?[] payload)
    {
        if (!IsLive(trading)) return;

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
        // PropertyChangedEventHandler is a BCL delegate, so unlike the ATAS events this one can be
        // stored as a method group and removed again. On the chart-strategy surface this is the
        // PRIMARY quote source, not a supplement: nothing else here streams prices.
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

    /// <summary>
    /// TradingEnabled is weaker on the chart-strategy surface, and deliberately so rather than
    /// silently. With a connector it means the account is neither locked nor suspended AND the feed
    /// is connected and supports trading. Without one — the normal case here — ATAS offers no
    /// socket-level signal at all, so it means only what Portfolio itself says: not locked, not
    /// suspended. BridgeHello.TradingSurface carries Connector=null, which is where a reader finds
    /// out which of those two answers they are looking at.
    /// </summary>
    static AccountInfo ToAccount(AtasPortfolio p, IFeedConnector? c) => new(
        p.AccountID ?? "",
        string.IsNullOrWhiteSpace(p.DepoName) ? p.AccountID ?? "" : p.DepoName,
        p.Currency?.ToString() ?? "",
        p.Balance,
        p.Balance + p.OpenPnL,
        p.OpenPnL,
        !p.IsRealAccount,
        !p.IsLocked && !p.IsSuspended && (c is null || (c.IsConnected && c.IsSupportedTradingFunctions)));

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
            var counted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in LiveTrades())
            {
                if (TradeKey(t) != key) continue;
                // LiveTrades may read overlapping collections; a trade counted twice would report a
                // fill larger than the order.
                if (t.Id is { Length: > 0 } id && !counted.Add(id)) continue;
                traded += t.Volume;
            }
        }
        if (traded > 0m) return quantity > 0m ? Math.Min(traded, quantity) : traded;

        var remaining = quantity - o.Unfilled;
        return remaining <= 0m ? 0m : Math.Min(remaining, quantity);
    }

    IReadOnlyDictionary<string, decimal> FillsByOrder()
    {
        var map = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var counted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in LiveTrades())
        {
            var key = TradeKey(t);
            if (key.Length == 0) continue;
            if (t.Id is { Length: > 0 } id && !counted.Add(id)) continue;
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

    /// <summary>
    /// Every order collection this adapter can see, most authoritative first.
    ///
    /// Whether ITradingManager.Orders and ChartStrategy.Orders are the SAME list has NOT been
    /// measured — BridgeHello.TradingSurface reports both counts so that one probe run settles it.
    /// Until it is settled, reading both is the safe direction to be wrong in: an order missed by
    /// reading only one of them would be an order hidden from reconciliation.
    ///
    /// EACH ENTITY IS YIELDED ONCE. If those collections do turn out to be the same list, a caller
    /// that SUMS per order — FilledOf adding up my-trade volumes — would otherwise report a fill of
    /// twice the real size, which reads as FILLED on a half-filled order. Neither Order nor MyTrade
    /// overrides Equals in the dump, so the set is reference identity, which is exactly the question
    /// being asked: is this the same object arriving again.
    /// </summary>
    IEnumerable<AtasOrder> LiveOrders()
    {
        var seen = new HashSet<AtasOrder>();
        foreach (var o in Items<AtasOrder>(Trading?.Orders)) if (seen.Add(o)) yield return o;
        foreach (var o in Items<AtasOrder>(Orders)) if (seen.Add(o)) yield return o;
        foreach (var o in Items<AtasOrder>(Connector?.Orders)) if (seen.Add(o)) yield return o;
    }

    /// <inheritdoc cref="LiveOrders"/>
    IEnumerable<AtasMyTrade> LiveTrades()
    {
        var seen = new HashSet<AtasMyTrade>();
        foreach (var t in Items<AtasMyTrade>(Trading?.MyTrades)) if (seen.Add(t)) yield return t;
        foreach (var t in Items<AtasMyTrade>(MyTrades)) if (seen.Add(t)) yield return t;
        foreach (var t in Items<AtasMyTrade>(Connector?.MyTrades)) if (seen.Add(t)) yield return t;
    }

    AtasSecurity? FindSecurity(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;
        if (BoundSecurity is { } own && SymbolMatches(own, own.SecurityId, symbol)) return own;
        // Only where a connector exists. On the chart-strategy surface the chart's own instrument is
        // the whole set, which is why an unknown symbol is a definite pre-flight refusal in Place.
        foreach (var s in Items<AtasSecurity>(Connector?.Securities))
            if (SymbolMatches(s, s.SecurityId, symbol)) return s;
        return null;
    }

    AtasPortfolio? FindPortfolio(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return BoundPortfolio;
        if (BoundPortfolio is { } own && string.Equals(own.AccountID, accountId, StringComparison.OrdinalIgnoreCase)) return own;
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

        foreach (var o in LiveOrders()) if (IsSameOrder(o, connectorOrderId)) return o;
        foreach (var o in LiveOrders()) if (HasClientId(o, connectorOrderId)) return o;

        lock (_gate) return _submitted.TryGetValue(connectorOrderId, out var mine) ? mine : null;
    }

    static bool IsSameOrder(AtasOrder o, string id) =>
        string.Equals(OrderKey(o), id, StringComparison.Ordinal)
        || (!string.IsNullOrEmpty(o.Id) && string.Equals(o.Id, id, StringComparison.Ordinal))
        || string.Equals(o.ExtId.ToString(CultureInfo.InvariantCulture), id, StringComparison.Ordinal);

    static bool HasClientId(AtasOrder o, string id) =>
        !string.IsNullOrEmpty(o.Comment) && string.Equals(o.Comment, id, StringComparison.Ordinal);

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
    /// It requires the client id to be readable off an order sitting in ATAS's OWN order collection,
    /// and that order to already carry a broker-assigned Id. What that proves is that ATAS carries
    /// the identifier alongside a real order for the life of the session, which is what
    /// reconciliation after a dropped pipe actually needs. What it does NOT prove is that the broker
    /// echoes the comment back after ATAS itself is restarted; nothing observable from inside a
    /// strategy can prove that, and it is not claimed anywhere.
    ///
    /// The collection moved with the surface: it used to be IDataFeedConnector.Orders, which is null
    /// here, and is now ITradingManager.Orders — plus ChartStrategy.Orders, because whether those two
    /// are the SAME list has NOT been measured and reading only one of them could refuse a proof
    /// that was there. Reading both cannot manufacture one: the id must still be an id THIS adapter
    /// submitted, and the order must still carry a broker-assigned Id.
    /// </summary>
    void ProveClientOrderId(string clientOrderId)
    {
        if (string.IsNullOrEmpty(clientOrderId)) return;
        lock (_gate)
        {
            if (_clientOrderIdProven) return;

            // Rule 1 is that the adapter reads back ITS OWN identifier, and this is what makes that
            // literally true. Without it, OnOrderPayload handed in the Comment of every order that
            // crossed the feed, and any order in ATAS's book carrying any comment — placed by hand,
            // or by another strategy — set the latch. TradeAgent would then report
            // SupportsClientOrderId = true on evidence it never produced, and with an order cache
            // reachable that is the whole of ReconciliationProvable: the gateway would permit
            // LIVE_AUTONOMOUS on a round trip nobody had performed. That is precisely the "do not
            // fake it" the rule spells out on IAtasAdapter.
            //
            // Trim() can empty _submitted after 4096 orders, so a very old id stops being provable.
            // That refuses a proof rather than inventing one, which is the direction to fail in.
            if (!_submitted.ContainsKey(clientOrderId)) return;
        }

        // No trading surface means no collection to look in, so there is nothing to learn and this is
        // not a check. Counting it as one would turn "we never got to look" into "we looked and it
        // was not there" — the exact confusion the counter exists to remove.
        if (Trading is null) return;

        lock (_gate) _clientOrderIdChecks++;

        foreach (var o in LiveOrders())
        {
            if (!string.Equals(o.Comment, clientOrderId, StringComparison.Ordinal)) continue;
            if (string.IsNullOrEmpty(o.Id)) continue;
            lock (_gate) _clientOrderIdProven = true;
            return;
        }
    }

    // ---------------------------------------------------------------- plumbing

    /// <summary>
    /// Waits for one of ATAS's async calls from a synchronous adapter method.
    ///
    /// Only the CONNECTOR path needs this: its synchronous overloads are [Obsolete], and an obsolete
    /// call is exactly the kind of thing that keeps working until a vendor update removes it — on the
    /// path that places real orders. ITradingManager's synchronous overloads are not obsolete and are
    /// used directly, which also sidesteps blocking on a task that may be marshalled to ATAS's GUI
    /// thread. Blocking here is safe because every caller is on the bridge's pipe-handling thread,
    /// never ATAS's UI thread, and ConfigureAwait(false) keeps it off any captured context.
    /// </summary>
    static void Block(Task task) => task.ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>Waits for a definite answer, and treats not getting one as exactly that — no
    /// exception, no rejection, just the order returned in whatever state it is really in.</summary>
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

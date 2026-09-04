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
// One side of the book, or one print on the tape: the payload of the chart's own market-data
// events and the type behind ChartStrategy.BestBid / BestAsk. Exactly ONE MarketDataArg exists in
// the dump — ATAS.Indicators.MarketDataArg — so the alias is not resolving an ambiguity, it is
// keeping the naming convention that every ATAS type here is spelled Atas*.
using AtasMarketData = ATAS.Indicators.MarketDataArg;
using AtasMyTrade = ATAS.DataFeedsCore.MyTrade;
using AtasOrder = ATAS.DataFeedsCore.Order;
using AtasOrderStates = ATAS.DataFeedsCore.OrderStates;
using AtasOrderTypes = ATAS.DataFeedsCore.OrderTypes;
using AtasPortfolio = ATAS.DataFeedsCore.Portfolio;
using AtasPosition = ATAS.DataFeedsCore.Position;
using AtasSecurity = ATAS.DataFeedsCore.Security;
using AtasTif = ATAS.DataFeedsCore.TimeInForce;
// A print on the tape, as opposed to AtasMyTrade which is OUR fill. Referenced only as one of the
// two shapes IOnlineDataProvider.NewTrades could carry, since the dump records that event's arity
// and not its generic argument.
using AtasTrade = ATAS.DataFeedsCore.Trade;
using IAtasCache = ATAS.DataFeedsCore.Database.ICache;
using IAtasDataProvider = ATAS.Indicators.IIndicatorDataProvider;
// The interface ICache derives from, and the one ATAS's own code stores the object under
// (IDataFeedConnector.Factory is typed IEntityFactory, not ICache). ProbeCache asks the service
// locator for it by name, because that is the likelier registration of the two.
using IAtasEntityFactory = ATAS.DataFeedsCore.IEntityFactory;
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
///      <see cref="ProveClientOrderId"/>). It is false until then. What that observation is WORTH
///      depends on whose object came back: Place hands ATAS the instance it constructed, so a match
///      against that same instance proves only that ATAS assigned an Id. TradingSurface reports
///      which happened, as coid=proven-sameref, coid=proven-distinct or coid=proven-crosssession,
///      and only the last two report SupportsClientOrderId = true — a same-reference match is a
///      real match that proves nothing, and reporting true from it is the "do not fake it" rule 1
///      names. The strongest of the three is the cross-session reading, taken when an identifier a
///      PREVIOUS run of this product wrote to <see cref="CoidWitness"/> before submitting is found
///      on an order in ATAS's book carrying the broker id that run recorded. Only that one shows
///      the identifier surviving the process that made it.
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

    /// <summary>How long <see cref="AtasCall.Block"/> waits on one of ATAS's async calls before
    /// declaring the outcome unknown. Expiry is NOT a rejection — see
    /// <see cref="AtasCallTimeoutException"/>.
    ///
    /// FIVE SECONDS IS ARITHMETIC, NOT A MEASUREMENT. Nothing here has been timed. Place costs the
    /// call plus WaitFor(AckTimeout), so the worst case a caller waits is CallTimeout + AckTimeout =
    /// 5 + 3 = 8s. AtasConnector's RPC timeout defaults to 10s, and 8 &lt; 10, so the bridge answers
    /// first and the connector reports the bridge's own account of what happened. Above about 6s the
    /// order reverses: the connector gives up before the bridge replies, both ends time out, and the
    /// bridge is still wedged when the next frame arrives — which is the whole failure this deadline
    /// exists to prevent. Change either number and redo the sum.</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long <see cref="StopBridge"/> waits for the bridge's frame loop to end before
    /// abandoning it. Runs on ATAS's OWN THREAD, which is what makes an unbounded wait here a hang
    /// of the platform rather than of us.
    ///
    /// DERIVED, NOT CHOSEN, and computed rather than stored so the sum cannot drift when either
    /// term is changed. <c>BridgeServer.DisposeAsync</c> awaits the frame loop, and the loop awaits
    /// <c>HandleFrame</c>; the longest a HEALTHY frame can take is one order call plus its
    /// acknowledgement wait, which is <see cref="CallTimeout"/> + <see cref="AckTimeout"/> = 8s.
    /// Two seconds of slack on top makes this a detector of a WEDGED loop rather than a race against
    /// a slow one: a stop that arrives while an order is genuinely in flight still waits for the
    /// reply to reach the gateway, and only a loop that is never going to finish is abandoned.
    /// A shorter deadline would abandon healthy shutdowns; a longer one just holds ATAS's thread.</summary>
    TimeSpan StopTimeout => CallTimeout + AckTimeout + TimeSpan.FromSeconds(2);

    readonly Lock _gate = new();
    readonly ManualResetEventSlim _pulse = new(false);

    /// <summary>Reasons captured from ATAS order-failure events, keyed by both broker order id and
    /// client order id because the failure may arrive before an id has been assigned.</summary>
    readonly Dictionary<string, string> _failures = new(StringComparer.Ordinal);

    /// <summary>Orders we submitted, by client order id, so Place can watch the exact instance.</summary>
    readonly Dictionary<string, AtasOrder> _submitted = new(StringComparer.Ordinal);

    /// <summary>
    /// EVERY ORDER OBJECT THIS ADAPTER TOUCHED, and therefore every object that can never be rule
    /// 1's proof. Deliberately WIDER than <see cref="_submitted"/>, which is keyed by client order
    /// id and holds only what <see cref="Place"/> built.
    ///
    /// The two it misses are the two that matter, and both are objects the adapter itself produces
    /// carrying our identifier:
    ///
    ///   * <see cref="Modify"/>'s <c>order.Clone()</c> — Clone copies Comment, so the replacement is
    ///     an object WE constructed holding OUR client order id, while <c>_submitted[id]</c> still
    ///     points at the original. A read-back that asked only "is this the instance I submitted"
    ///     answered no for it and recorded Distinct: a round trip the adapter performed against
    ///     itself, on which SupportsClientOrderId flips true.
    ///   * <see cref="ClosePosition"/>'s <c>created.Comment = clientOrderId</c> — our identifier
    ///     written by hand onto an order ATAS created.
    ///
    /// Whether ATAS's own order collection ever contains the Modify replacement is NOT VERIFIED and
    /// cannot be settled from the API dump, which lists public members only. That is exactly why the
    /// guard is unconditional: rule 1 is not allowed to rest on an unverified platform behaviour.
    ///
    /// Under <see cref="_gate"/> like every other side table here, trimmed by <see cref="Trim"/>,
    /// and its whole decision — including what a trimmed-away entry does to a proof — lives in
    /// <see cref="AdapterTouchedOrders"/>, in a file that compiles and is tested on every machine.
    /// </summary>
    readonly AdapterTouchedOrders _touched = new();

    /// <summary>
    /// THE DURABLE HALF OF <see cref="_submitted"/>, AND THE ONLY ROUTE TO SETTLING RULE 1.
    ///
    /// <see cref="_submitted"/> dies with this process, and the one experiment that can answer rule
    /// 1 from a source that cannot be our own object — place a resting order, RESTART ATAS, read the
    /// book — is precisely the experiment in which the process that submitted the order is gone.
    /// <see cref="CoidWitness"/> writes the claim "we are about to submit this identifier" to disk
    /// BEFORE the order exists, and later the broker order id ATAS assigned to it, so a later
    /// process can ask the same question <see cref="_submitted"/> answers.
    ///
    /// Constructed here, in a field initialiser, so the session id is minted when ATAS constructs
    /// the strategy and every record this run writes carries the same one.
    ///
    /// IT DOES NOT WEAKEN THE 2026-08-27 GUARD, and that is the point of it. The identifier must
    /// still be one this PRODUCT submitted; the only change is that the evidence for that may have
    /// been written down by an earlier process instead of held in memory by this one. Everything
    /// else the guard requires is unchanged, and the cross-session branch requires strictly MORE:
    /// the order must also carry the broker id that earlier process recorded — the half we did not
    /// write. See <see cref="ProveClientOrderId"/>.
    ///
    /// Its own lock, not <see cref="_gate"/>: it performs file IO, and holding the adapter's gate
    /// across a disk write would put every read of every side table here behind a spinning disk.
    /// </summary>
    readonly CoidWitness _witness = new();

    /// <summary>
    /// THIS STRATEGY HAS BEEN TAKEN DOWN, AND ITS HANDLERS ARE STILL SUBSCRIBED.
    ///
    /// `StopBridge` releases the witness lease, but the order-event fan is a lambda closed over the
    /// trading manager and is never unsubscribed — a fresh lambda cannot be removed with `-=`, which
    /// is why every handler in this class compares against the live surface instead. The fan calls
    /// `CoidWitness.Identified` for every order in ATAS's book carrying a comment, so a stopped
    /// strategy could take the lease back on the next order event and hold it for the life of the
    /// ATAS process, refusing every order the live bridge then tried to record.
    ///
    /// The witness side of that is closed too — `Identified` no longer leases before it knows it has
    /// something to write — but a stopped strategy should not be reaching for the file at all, and
    /// this says so at the one place that decides whether this instance is still anybody's bridge.
    ///
    /// IT IS A SEPARATE CLASS BECAUSE THIS FILE CANNOT BE COMPILED OFF A WINDOWS BOX WITH ATAS ON
    /// IT. Two defects were found in the teardown below — a check taken outside the lock the release
    /// takes, and a release that an exception could skip — and neither could be given a failing test
    /// while it lived here. The rule needs no ATAS type at all, so it lives in
    /// <see cref="AdapterTeardown"/>, where <c>AdapterTeardownTests</c> drives both interleavings
    /// against a real <see cref="CoidWitness"/> on any machine. Same move, same reason, as
    /// <see cref="AdapterTouchedOrders"/> and <see cref="AtasCall"/>.
    /// </summary>
    readonly AdapterTeardown _teardown = new();

    /// <summary>
    /// How many prior-session identifiers <see cref="Describe"/> re-checks per call.
    ///
    /// <see cref="OnOrderPayload"/> is a PUSH and nothing guarantees ATAS raises an order event for
    /// an order that merely SITS THERE after a restart — which is exactly the order the experiment
    /// is about. Describe runs on the handshake and on every heartbeat, so it pulls instead of
    /// waiting to be told. Bounded because that is a five-second cadence and the witness file can
    /// hold <see cref="CoidWitness.DefaultCap"/> records: unbounded, a stale file would rescan
    /// ATAS's whole order book hundreds of times per heartbeat. Newest first, because the
    /// experiment is always about the most recent order.
    /// </summary>
    const int WitnessSweep = 16;

    /// <summary>What is known about each symbol's book, per SIDE, and where each side came from. A
    /// quote is stamped with the time the price was true — never with "now" for a price that was
    /// merely read — because QuoteInfo.IsStale is what stops the gateway sizing an order off a price
    /// that stopped updating an hour ago. See the quotes section for why this is per-side.</summary>
    readonly Dictionary<string, (QuoteSide Bid, QuoteSide Ask, QuoteSide Last)> _quotes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One side of one symbol's book: the price, the size that arrived with it, when that price was
    /// true, the <see cref="DateTimeKind"/> ATAS put on that time, and which surface said so.
    ///
    /// Per side rather than one row per symbol because ATAS's quote events are SINGLE-SIDED — a
    /// <see cref="AtasMarketData"/> carries a bid or an ask, with its own Time — so one shared
    /// timestamp would silently claim the newly arrived side's freshness for the other one.
    /// </summary>
    readonly record struct QuoteSide(decimal Price, decimal Size, DateTimeOffset At, DateTimeKind Kind, QuoteSource Source)
    {
        /// <summary>A non-positive price is "not reported", never "the price is zero". The whole file
        /// reads it that way — BuildQuote did before this, and tools/probe says so in as many words:
        /// "The adapter reports a zero bid as null rather than as a price".</summary>
        public bool HasPrice => Price > 0m;

        public static QuoteSide None { get; } = new(0m, 0m, DateTimeOffset.MinValue, DateTimeKind.Unspecified, QuoteSource.None);

        /// <summary>A side read off an ATAS entity that carries its own DateTime. The Kind is kept
        /// verbatim because it is the one reading that settles what <see cref="ToQuoteTime"/> has to
        /// assume — see there.</summary>
        public static QuoteSide From(decimal price, decimal size, DateTime time, QuoteSource source) =>
            new(price, size, ToQuoteTime(time), time.Kind, source);
    }

    /// <summary>
    /// Which surface a price came from, ordered WEAKEST TO STRONGEST and compared as such: a weaker
    /// surface never displaces a side a stronger one has already filled, and never overwrites a real
    /// price with a zero.
    ///
    ///   None          nothing has ever filled this side.
    ///   SecurityRead  Security.BestBidPrice / BestAskPrice read at the moment of the call. There is
    ///                 no timestamp anywhere on Security in the dump, so this price cannot be shown
    ///                 to be current at all — it is the last resort and it leaves At unset.
    ///   ChartProp     ChartStrategy.BestBid / BestAsk, read on demand. A MarketDataArg, so it does
    ///                 carry a time; but it is a read, not something we watched arrive.
    ///   SecurityMove  Security.PropertyChanged fired and the price had genuinely changed. We
    ///                 watched that happen, so "now" is an honest stamp for it.
    ///   MarketData    a BestBidAskChanged / NewTrades event, carrying the feed's own timestamp.
    ///                 The strongest reading there is, and on this platform the only one measured to
    ///                 carry an ES price at all.
    /// </summary>
    enum QuoteSource { None, SecurityRead, ChartProp, SecurityMove, MarketData }

    readonly HashSet<AtasSecurity> _tracked = [];

    BridgeServer? _bridge;

    /// <summary>The surfaces already subscribed to, so binding stays idempotent. Three separate
    /// fields because they are three separate objects with three separate lifetimes: the trading
    /// manager is the one that must exist, the online data provider is THE quote source on this
    /// surface — measured, see the quotes section — and the connector may never be non-null at
    /// all.</summary>
    IAtasTrading? _hookedTrading;
    IAtasOnlineData? _hookedOnline;
    IFeedConnector? _hookedConnector;

    /// <summary>Last value pushed through <see cref="ConnectionChanged"/>, so a re-read that says
    /// the same thing does not spam the gateway. Null means nothing has been said yet.</summary>
    bool? _lastConnected;

    /// <summary>
    /// WHAT THE RULE-1 READ-BACK ACTUALLY OBSERVED — which is not the same question as whether it
    /// matched. THE ONLY STATE BEHIND SupportsClientOrderId; there is no separate "proven" flag.
    ///
    /// <see cref="Place"/> hands ATAS the very Order instance it constructed and set Comment on, and
    /// the rest of Place assumes ATAS mutates that same instance. If ATAS's own collection simply
    /// CONTAINS that instance, then "an order in ATAS's collection carries our client id" is true by
    /// construction — the adapter is reading its own field back off its own object — and the only
    /// thing genuinely proven is that ATAS assigned an Id. The round trip through the platform's own
    /// storage, which is the thing reconciliation after a dropped pipe depends on, would not have
    /// been demonstrated at all. Reporting SupportsClientOrderId = true off that reading would be
    /// rule 1 faked, which is the one thing rule 1 names.
    ///
    /// So the proof records WHICH object carried the id back. Reference-equal to the one we
    /// submitted: vacuous. A different object: ATAS really did carry our identifier onto something
    /// this adapter did not write. Surfaced in BridgeHello.TradingSurface as coid=... either way.
    ///
    /// THE ONE LIVE READING THAT THIS WAS WAITING FOR HAS NOW BEEN TAKEN, and it was SameRef — real
    /// ATAS 8.0.14.397, a resting limit order on a sim account, 2026-08-28. So the deferred wiring
    /// is done: <see cref="Describe"/> reports SupportsClientOrderId = ProvesRoundTrip(this), which
    /// is true for Distinct and CrossSession. The bool that used to hold that answer alongside this field is
    /// gone on purpose — two variables for one fact is exactly how a capability boolean and the
    /// coid= token beside it come to disagree, and a boolean contradicted by the diagnostic printed
    /// next to it is worse than either on its own.
    ///
    /// Every judgement made from this value lives in <see cref="ClientOrderIdProofs"/>, in a file
    /// that compiles and is tested on every machine, because this one is not.
    /// </summary>
    ClientOrderIdProof _clientOrderIdProof;

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

    /// <summary>
    /// How the last order this adapter placed actually behaved in time, for
    /// <c>BridgeHello.TradingSurface</c>'s <c>place=</c> token. See the block in <c>Place</c>.
    ///
    /// IT NAMES THE ROUTE FIRST BECAUSE THE ROUTE IS WHAT MAKES THE NUMBERS MEAN ANYTHING.
    /// <c>sync</c>, <c>connector</c> and <c>asyncoverload</c> are three different platform calls, and
    /// a <c>call=</c> reading is only comparable with another taken through the same one.
    ///
    /// IT IS ONLY WRITTEN ON A PATH THAT REACHED THE ACKNOWLEDGEMENT WAIT, and the boundary is
    /// exactly the submission call. A pre-flight refusal, or an exception out of the submission
    /// itself — an <see cref="AtasCallTimeoutException"/> from the async route being the one that
    /// route newly makes possible — leaves the PREVIOUS order's reading standing, because the write
    /// is below the throw and this path has no catch. A refusal ATAS reports through its order-failure
    /// event does NOT: that one is detected after the write, so its reading is this order's.
    ///
    /// So an unchanged token is not this run's answer, and the difference is not visible from the
    /// token. A harness has to compare it against the one it read before placing; tools/probe does.
    ///
    /// DIAGNOSTIC ONLY. Nothing derives a capability, a state or a decision from it, and nothing may:
    /// it is a stopwatch reading, and a stopwatch reading is not a round trip.
    /// </summary>
    volatile string _lastPlace = "none";

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

    /// <summary>
    /// THE SECOND TERMINAL PATH, BECAUSE ONE CALLBACK IS NOT A GUARANTEE.
    ///
    /// The witness lease is held for the life of its owner and released by <see cref="StopBridge"/>,
    /// which until now was reached only from <see cref="OnStopping"/>. If ATAS tears a strategy down
    /// some other way — a chart closed, a workspace switched, the strategy removed rather than
    /// stopped — the lease is held for the life of the ATAS PROCESS and every later order is refused
    /// "another writer owns this witness" until ATAS itself restarts. Fail-closed, and a mis-click
    /// that costs a restart.
    ///
    /// `ChartStrategy` is `IDisposable` (dump line 269) and this hook is the disposal one; both call
    /// the same idempotent teardown, so whichever ATAS actually fires, the lease is let go.
    ///
    /// WHICH ONE IT FIRES IS NOT VERIFIED. That needs a running ATAS with a strategy on a chart, and
    /// this build is not deployed — the box still runs the protocol-2 DLL. Until the v0.1.2 redeploy
    /// this is two hooks and a compiler, not a measurement.
    /// </summary>
    protected override void OnDispose() => Guard(StopBridge);

    void SyncBridgeToState()
    {
        if (State == ATAS.Strategies.StrategyStates.Started) StartBridge();
        else if (State == ATAS.Strategies.StrategyStates.Stopped) StopBridge();
    }

    void StartBridge()
    {
        TryBind();
        _teardown.Started();
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

    /// <summary>
    /// Takes the bridge down, ON ATAS'S OWN THREAD, and therefore under a deadline.
    ///
    /// WHY THERE IS A DEADLINE AT ALL. <c>BridgeServer.DisposeAsync</c> cancels and then awaits the
    /// frame-reading loop, and that loop awaits <c>HandleFrame</c> — so a wedged write, or any frame
    /// handler that never returns, means this method never returns either. It runs from
    /// <see cref="OnStopping"/> and from the StateChanged fan, both of which are ATAS calling us:
    /// an unbounded wait here stops the PLATFORM, not the strategy. This is the same
    /// unbounded-wait shape <see cref="AtasCall"/> was extracted to remove from the write path, so
    /// it is the same helper, for the same reason.
    ///
    /// WHAT A TIMEOUT DURING SHUTDOWN MEANS, WHICH IS NOT WHAT ONE MEANS DURING A WRITE. Nothing is
    /// pending on it and there is nothing to reconcile: no order outcome is being decided, the pipe
    /// is being closed deliberately, and the gateway learns the bridge is gone the way it always
    /// does — the heartbeat stops. So <see cref="AtasCallTimeoutException"/>'s message, which is
    /// written for an order that may be live at the broker, describes nothing that is true here.
    /// That is why it is caught rather than logged or propagated: the mechanism fits, the words
    /// do not.
    ///
    /// WHY IT CATCHES EVERYTHING, AND WHY THE CATCH IS HERE RATHER THAN LEFT TO <see cref="Guard"/>.
    /// Both call sites are already inside Guard, so nothing escapes into ATAS's dispatch either way
    /// — but Guard catches ONE level up, which would skip <see cref="UntrackSecurities"/> and leave
    /// this adapter subscribed to ATAS security events for the rest of the session, with the bridge
    /// it feeds already discarded. Teardown must finish; catching here is what finishes it.
    ///
    /// WHAT ABANDONING THE WAIT LEAVES BEHIND. DisposeAsync has already requested cancellation
    /// before it awaits, so the abandoned loop cannot start another connection cycle and its
    /// heartbeat token is cancelled with it — it stops where it is stuck and stops speaking. It
    /// still holds its pipe client until whatever wedged it returns, so a strategy restarted inside
    /// that window can find TradeAgent already holding a connection it will never hear from again.
    /// That is a visibly dead bridge, which the heartbeat timeout already handles, and it is
    /// strictly better than holding ATAS's thread forever.
    /// </summary>
    void StopBridge() => _teardown.Stop(steps: () =>
    {
        BridgeServer? bridge;
        lock (_gate) { bridge = _bridge; _bridge = null; }

        if (bridge is not null)
        {
            try { AtasCall.Block(bridge.DisposeAsync().AsTask(), StopTimeout, "BridgeServer.DisposeAsync",
                    "ATAS is taking this strategy down either way, so there is nothing to reconcile — but the "
                    + "frame loop did not end, which is what a wedged write into ATAS looks like from here."); }
            catch (Exception) { /* see above: nothing is pending on this, and teardown must finish */ }
        }

        // NOT WRAPPED IN A CATCH OF ITS OWN, AND THAT IS THE POINT OF THE finally IT NOW SITS IN.
        // This calls into ATAS while ATAS is taking the strategy down — the one moment the platform
        // is most likely to answer with an exception — and as two plain statements an unsubscribe
        // that threw skipped the witness release below and left the lease held on a terminal path.
        // AdapterTeardown.Stop releases in a finally, so the exception still reaches Guard and no
        // longer decides whether the release happens.
        UntrackSecurities();
    },
    // AND THE WITNESS STOPS BEING OURS. The lease is held for the life of the owner, and a strategy
    // ATAS has taken down is not the owner of anything — leaving it held would refuse the witness to
    // a bridge started afterwards in the same ATAS process, for no reason. The instance stays usable:
    // if this strategy is started again, its next write takes the lease back. Process death releases
    // it too, which is what makes a crash harmless.
    releaseWitness: _witness.Dispose);

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

        // THE PULL, AND IT IS HERE BECAUSE THE PUSH IS NOT GUARANTEED TO ARRIVE.
        //
        // The read-back is normally driven by OnOrderPayload, which is ATAS telling us an order
        // changed. Nothing says ATAS raises an order event for an order that merely SITS THERE
        // after a restart — and that order is the entire experiment. Describe runs on the handshake
        // and on every heartbeat, so it asks rather than waiting to be told. Guarded because a
        // diagnostic must never be the thing that takes the handshake down.
        Guard(SweepWitness);

        ClientOrderIdProof proof;
        int attempts, checks;
        lock (_gate) { proof = _clientOrderIdProof; attempts = _clientOrderIdAttempts; checks = _clientOrderIdChecks; }

        return new BridgeHello
        {
            BridgeProtocolVersion = Versions.BridgeProtocolVersion,
            BridgeVersion = Versions.App,
            // The platform version ATAS actually loaded us into. There is no public version property
            // in the dump, so this reads the assembly identity of the ATAS.Strategies.dll in process.
            AtasVersion = typeof(ChartStrategy).Assembly.GetName().Version?.ToString() ?? "unknown",
            AccountId = portfolio?.AccountID,
            // THE REFUSAL HAS TO BE VISIBLE OR IT IS A SILENT STOP. A permanent local failure at the
            // witness path — a directory where the temp belongs, a permission — now refuses EVERY
            // order, forever, and without this the owner sees orders failing with nothing on any
            // screen saying why. This rides the hello into the ATAS bridge health row.
            WitnessFailure = _witness.Trouble,
            // Portfolio.IsRealAccount is the only simulation signal in the dump. When there is no
            // portfolio yet we report NOT simulated, because guessing "simulated" on an unknown
            // account is the guess that costs money.
            IsSimulated = portfolio is not null && !portfolio.IsRealAccount,
            // Rule 1. False until a placed order has been seen coming back out of ATAS's own order
            // collection carrying our client id AND a broker-assigned id — ON AN OBJECT THIS ADAPTER
            // DID NOT HAND IN, in this session or in an earlier one. Never hard-coded true, and
            // deliberately not true for the match that ATAS actually produces here: a same-reference
            // match is our own object being read back to us, so it proves only that Order.Id was
            // assigned. ProvesRoundTrip is the whole of the decision and it lives in
            // ClientOrderIdProofs, which every machine can test.
            // AND FALSE WHILE THE WITNESS CANNOT VOUCH FOR ITS OWN HISTORY. The reading above is
            // about what ATAS did; this is about whether the record that makes it answerable after a
            // restart is intact. An unresolved durability gap means some claim this product made is
            // not on disk, so "rule 1 is proven" is a statement this run cannot support — and the
            // gate it feeds decides whether the product may trade unattended. Reporting the
            // capability false is the direction to fail in; the per-order refusal in Place is the
            // precise test and it is unaffected, so LIVE_CONFIRM dispatch still works.
            SupportsClientOrderId = proof.ProvesRoundTrip() && _witness.Trouble is null,
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
    /// Re-checks the identifiers PREVIOUS runs of this product submitted, against the live book.
    ///
    /// This is the pull half of the restart experiment. Half 1 places a resting order and leaves it;
    /// ATAS is restarted; this adapter comes up with an empty <see cref="_submitted"/> and no reason
    /// to look at anything — the order is just sitting in the book, generating no events. Without
    /// this, the reading would depend on ATAS happening to raise an order event for it, which is not
    /// a property anything has measured.
    ///
    /// Cheap when there is nothing to do: the latch is checked first, the witness returns an empty
    /// list when no prior session left an acknowledged record, and ProveClientOrderId re-checks the
    /// latch itself. Bounded by <see cref="WitnessSweep"/> — see the note there.
    /// </summary>
    void SweepWitness()
    {
        lock (_gate) { if (_clientOrderIdProof.IsSettled()) return; }
        if (Trading is null) return;
        foreach (var id in _witness.PriorSessionIds(WitnessSweep))
        {
            ProveClientOrderId(id);
            lock (_gate) { if (_clientOrderIdProof.IsSettled()) return; }
        }
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
                // Reported separately from orders= on purpose, and the reading is now taken:
                // ITradingManager.Orders and ChartStrategy.Orders are NOT the same collection.
                // On 2026-08-30, with one resting order live in ATAS, three separate probe runs
                // reported `orders=1 strategyorders=0` — both counted inside this one method, so
                // from one instant. A shared list cannot report two different lengths at once.
                // (probe-half2.txt, probe-clean.txt; probe-verify.txt shows 0/0 after the cancel,
                // so the 1 was tracking the real order.)
                $"strategyorders={Count(Orders)}",
                $"mytrades={Count(trading?.MyTrades)}",
                $"portfolio={Token(portfolio?.AccountID)}",
                $"security={Token(SymbolOf(BoundSecurity))}",
                $"position={(trading?.Position is { } p ? p.Volume.ToString(CultureInfo.InvariantCulture) : "none")}",
                // Where the price came from and how old it is. Without this a refusal to place
                // reads as "no bid" and says nothing about WHICH of four surfaces was empty.
                $"quote={QuoteToken()}",
                // Rule 1's reading, and the only token that says whether the proof is worth anything.
                $"coid={ClientOrderIdToken()}",
                // What the durable witness holds, and — through the session prefix — WHICH RUN of
                // the bridge is reading it. That prefix is the difference between "the experiment
                // has been performed" and "you are looking at the same process that wrote the
                // record", which is otherwise invisible from outside and is the single easiest way
                // to mistake a restart that did not happen for a proof that did not appear.
                //
                // CoidWitness.Token contains no space by contract, which this line depends on: the
                // report is space-joined and tools/probe splits it on spaces, so a space here would
                // silently turn one field into two.
                $"witness={_witness.Token()}",
                // How the last order behaved in time. `gap` is this platform's acknowledgement
                // latency measured through the path actually in use, and it is what decides whether
                // the OpenOrderAsync question is answerable here at all. Space-free by construction.
                //
                // THE FIRST FIELD IS THE ROUTE, AND NOTHING IS READABLE WITHOUT IT. `sync` is
                // ITradingManager.OpenOrder, `connector` is IDataFeedConnector.RegisterOrderAsync,
                // `asyncoverload` is ITradingManager.OpenOrderAsync — three different calls whose
                // `call=` readings mean three different things. A run that asked for one route and
                // reads a token saying another measured something else; check the route before
                // believing the number.
                $"place={_lastPlace}",
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

    /// <summary>
    /// The rule-1 read-back, in one token, with the six readings kept apart because they mean six
    /// different things and only two of them are proof:
    ///
    ///   unattempted      no order carrying a client order id has been submitted yet. Says nothing
    ///                    about ATAS.
    ///   unchecked        one was submitted, and no read-back has ever run — there was no trading
    ///                    surface to look in at the moment it would have. Still says nothing.
    ///   notfound         a read-back RAN and no order in ATAS's own collection carried our id with a
    ///                    broker-assigned Id on it. This one is evidence, and it is negative.
    ///   proven-sameref   an order carried it back — and it is REFERENCE-EQUAL to the instance this
    ///                    adapter submitted. ATAS handed us our own object, so the comment match was
    ///                    true by construction and proves nothing beyond Order.Id being assigned.
    ///                    MUST NOT be trusted as a round trip.
    ///   proven-distinct  a genuinely different object carried our identifier. That is the round trip
    ///                    rule 1 asks about, actually observed — within one session.
    ///   proven-crosssession
    ///                    an identifier a PREVIOUS run of this product wrote down before submitting
    ///                    was found on an order in ATAS's book carrying the broker id that run
    ///                    recorded. The identifier outlived the process that made it, which is the
    ///                    reading reconciliation after a dropped pipe actually rests on.
    ///
    /// The six strings are not free text: tools/probe switches on them verbatim and BUILD-STATUS.md
    /// quotes them as evidence. SupportsClientOrderId reads the same field — proven-distinct and
    /// proven-crosssession are the two it reports true from — so the boolean and this token cannot
    /// contradict each other. The word is still the more informative of the two: proven-sameref says
    /// "a match happened AND it was worthless", which no boolean can say, and proven-crosssession
    /// says "and it survived a restart", which no boolean can say either.
    /// </summary>
    string ClientOrderIdToken()
    {
        ClientOrderIdProof proof;
        int attempts, checks;
        lock (_gate) { proof = _clientOrderIdProof; attempts = _clientOrderIdAttempts; checks = _clientOrderIdChecks; }
        return ClientOrderIdProofs.Token(proof, attempts, checks);
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

    /// <summary>
    /// The price, from the strongest source that has one — see <see cref="Compose"/> for the order
    /// and for what each source is worth.
    ///
    /// Only a price whose source carries a time gets one. A quote assembled from a surface that has
    /// no timestamp comes back at MinValue so IsStale() refuses it, rather than dressed up as fresh.
    /// </summary>
    public QuoteInfo? GetQuote(string symbol)
    {
        var s = FindSecurity(symbol);
        if (s is null) return null;
        Track(s);
        lock (_gate) return Compose(s, SymbolOf(s)).Quote;
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
        // An order nothing identifies has no key to be deduplicated by — see OrderKey — and
        // deduplicating several of them onto one empty key would DROP working orders from this list.
        // A duplicate is a cosmetic problem; an order missing from the book the gateway reconciles
        // against is the problem this whole file exists to avoid. So they are listed, not merged.
        var keyless = new List<OrderInfo>();

        void Take(AtasOrder o)
        {
            if (!AccountMatches(o.AccountID ?? o.Portfolio?.AccountID, accountId)) return;
            var info = ToOrder(o, fills);
            if (OrderStateMachine.IsTerminal(info.State))
            {
                if (!includeInactive) return;
                if (since is not null && info.At < since.Value) return;
            }
            if (info.ConnectorOrderId.Length == 0) { keyless.Add(info); return; }
            // First writer wins, and the live book is read first: a cached copy must never displace
            // the object ATAS is still updating.
            byKey.TryAdd(info.ConnectorOrderId, info);
        }

        foreach (var o in LiveOrders()) Take(o);
        if (cache is not null) foreach (var o in Items<AtasOrder>(cache.GetOrders(accountId))) Take(o);
        return [.. byKey.Values, .. keyless];
    }

    /// <summary>What a cache probe found, and — just as important — how it failed when it did not.
    /// The note goes straight onto the wire in BridgeHello.TradingSurface.</summary>
    readonly record struct CacheProbe(IAtasCache? Cache, string Note);

    /// <summary>
    /// The whole basis for rule 2's answer, and it is a runtime question, not a guess.
    ///
    /// There is exactly one order-history query in the four ATAS assemblies:
    /// ATAS.DataFeedsCore.Database.ICache.GetOrders(String accountId). Nothing in the public surface
    /// hands you an ICache, so this walks every route that could plausibly produce one, writes down
    /// what each route said, and confirms whatever it finds before letting it count.
    ///
    /// WHY IT WALKS ALL OF THEM AND RECORDS EVERY ANSWER
    ///
    /// The 2026-08-28 live reading inside ATAS 8.0.14.397 was, in full:
    ///
    ///     cache=none(connector-null,getservice-threw)
    ///
    /// That is a dead end, not a finding, and it is the misdiagnosis every line below exists to
    /// prevent. It cannot separate three states with opposite consequences — GetService&lt;T&gt; is
    /// constrained in a way ICache can never satisfy (the route is permanently dead), the service
    /// locator is not built yet when the handshake runs (the route may work later), or the locator
    /// is perfectly healthy and simply has no cache registered under that type (ask it for a
    /// different type). It also stopped at the first failure, so nobody could tell whether any other
    /// route had even been attempted. Both defects are fixed here: every route runs, guarded on its
    /// own, and an outcome that is an exception NAMES the exception and part of its message.
    ///
    /// A cache found by any route is then CONFIRMED, not assumed: it must be initialised, and it
    /// must know the account actually in use. Rule 2 says a partial history is worse than none, and
    /// a cache belonging to some other configuration would answer GetOrders with a short list that
    /// looks complete. A route that produces a cache which fails confirmation does NOT stop the
    /// walk — a later route may hand back a different instance that does belong here. Nothing in
    /// this method can make SupportsOrderHistory true by accident.
    /// </summary>
    CacheProbe ProbeCache(string? accountId)
    {
        // Every route's outcome, in the order attempted, whether or not a later route succeeded.
        var log = new List<string>(6);
        IAtasCache? found = null;
        var via = "";

        CacheProbe Result() => found is null
            ? new CacheProbe(null, Joined("none", log))
            : new CacheProbe(found, $"ok({via})");

        // Returns true once a CONFIRMED cache is in hand, so the walk can stop. It returns false —
        // and the walk continues — for a route that produced an object which failed confirmation:
        // stopping there would report "foreign" or "uninit" as though it were the platform's final
        // word, when the very next route may return a different instance that does belong here.
        bool Land(string route, (object? Value, string? Fault) attempt)
        {
            if (attempt.Fault is { } fault) { log.Add($"{route}={fault}"); return false; }
            if (attempt.Value is null) { log.Add($"{route}=null"); return false; }
            if (attempt.Value is not IAtasCache cache)
            {
                // Names the class that came back. "The locator answered, with something else" is a
                // completely different next step from "the locator had nothing", and only the
                // concrete type name says which other type to go and ask for.
                log.Add($"{route}=wrongtype({Clip(attempt.Value.GetType().Name, 40)})");
                return false;
            }

            var note = Confirm(cache, accountId);
            log.Add($"{route}={note}");
            if (note != "ok") return false;
            found = cache;
            via = route;
            return true;
        }

        try
        {
            // ROUTE 1 — the connector's own entity factory.
            // Dump: `interface ATAS.DataFeedsCore.IDataFeedConnector : ILoggerSource` declares
            //       `IEntityFactory Factory { get; set; }`
            // Dump: `class ATAS.DataFeedsCore.Database.Cache : Cache`1, ILoggerSource,
            //       ISettingsSource`1, ICache, IEntityFactory` — ONE object implementing both, so a
            //       connector's Factory genuinely can be the cache.
            // Null on a chart strategy (trap 13), and kept only because where a connector does exist
            // this is the authoritative cache for that connection.
            var connector = Connector;
            if (connector is null) log.Add("factory=connector-null");
            else if (Land("factory", Attempt(() => (object?)connector.Factory))) return Result();

            if (DataProvider is not { } provider)
            {
                log.Add("svc=no-dataprovider");
                return Result();
            }

            // One GetService definition, found once and shared by every route below, so "the method
            // is not there at all" is said once rather than four times.
            var definition = ServiceMethod();
            if (definition is null)
            {
                // Structural: this ATAS build's IIndicatorDataProvider has no GetService<T>() at
                // all. No reconnect, no retry and no other type argument can change that.
                log.Add("svc=absent");
                return Result();
            }

            // CONTROL PROBE, and it is the single reading that makes the rest of this line
            // interpretable. Ask the locator for the one service it is CERTAIN to know before asking
            // it for anything exotic.
            // Dump: `ITradingManager TradingManager { get; }` on
            //       `interface ATAS.Indicators.IIndicatorDataProvider` — the same object is reachable
            //       as a plain property, so the locator's answer can be compared by reference.
            // Dump: `class ATAS.Indicators.IndicatorDataProvider : IIndicatorDataProvider` takes an
            //       `IIndicatorServiceProvider indicatorServiceProvider` in its constructor and
            //       exposes `T GetService()` — GetService is a facade over that locator, so a null
            //       or unbuilt locator is exactly the failure this control detects.
            //
            //   ok-same    the locator works AND is wired to this chart. A null or a throw on the
            //              cache routes below is therefore a real statement about REGISTRATION.
            //   ok-other   the locator works but hands out a different instance than the property —
            //              still a real statement, but whatever it returns may not be this chart's.
            //   null       the locator answers and knows nothing, not even the trading manager. It
            //              is empty or not yet populated: re-read after the chart has finished
            //              attaching before concluding anything from the lines that follow.
            //   threw(..)  the locator itself is broken or uninitialised. Every other svc line below
            //              then says nothing at all about registration, and THIS is the one to chase.
            log.Add("svc:probe=" + ControlNote(ResolveService(definition, provider, typeof(IAtasTrading)), provider));

            // ROUTE 2 — ask the locator for the cache interface by name.
            // Dump: `interface ATAS.DataFeedsCore.Database.ICache : IEntityFactory, ILoggerSource`,
            //       carrying `ICollection`1 GetOrders(String accountId)` — the only order-history
            //       query in the whole surface, which is why this type is worth asking for directly.
            if (Land("svc:ICache", ResolveService(definition, provider, typeof(IAtasCache)))) return Result();

            // ROUTE 3 — ask for the interface the platform actually STORES the object under, and see
            // whether what comes back happens to be the cache. Likelier than route 2, not less.
            // Dump: ICache derives from IEntityFactory (route 2's line), and the only place ATAS's
            //       own code holds one of these is `IEntityFactory Factory { get; set; }` on
            //       IDataFeedConnector — typed as the BASE, not as ICache. A container registering
            //       `class ATAS.DataFeedsCore.Database.Cache : ..., ICache, IEntityFactory` under the
            //       type ATAS itself asks for would therefore register it as IEntityFactory.
            // If this answers with something that is not a cache, wrongtype() names the class, which
            // is the next thing worth knowing rather than another dead end.
            if (Land("svc:IEntityFactory", ResolveService(definition, provider, typeof(IAtasEntityFactory)))) return Result();

            // ROUTE 4 — reach a connector through the locator, then take ITS factory.
            // Dump: `IEntityFactory Factory { get; set; }` on
            //       `interface ATAS.DataFeedsCore.IDataFeedConnector : ILoggerSource`.
            // ChartStrategy.Connector being null (trap 13) is a fact about a property on the
            // strategy, NOT evidence that the process has no connector — the locator may well hand
            // one out. If it does, that is worth considerably more than the cache: the connector
            // alone carries Portfolios, Securities, Positions and a socket-level IsConnected, every
            // one of which this adapter currently reports as unavailable.
            //
            // It is deliberately NOT bound here. A diagnostic that rewires the adapter as a side
            // effect of being read is a diagnostic nobody can trust, and Describe() runs on every
            // five-second heartbeat.
            var byLocator = ResolveService(definition, provider, typeof(IFeedConnector));
            if (byLocator.Fault is { } locatorFault) log.Add($"svc:IDataFeedConnector={locatorFault}");
            else if (byLocator.Value is null) log.Add("svc:IDataFeedConnector=null");
            else if (byLocator.Value is not IFeedConnector viaLocator)
                log.Add($"svc:IDataFeedConnector=wrongtype({Clip(byLocator.Value.GetType().Name, 40)})");
            else
            {
                // Logged in its own token rather than left to be inferred from the SHAPE of the next
                // one: "a connector is reachable from a chart strategy after all" is a bigger fact
                // than anything this method was sent to find out, and it must be impossible to miss.
                log.Add("svc:IDataFeedConnector=ok");
                if (Land("svc:IDataFeedConnector.factory", Attempt(() => (object?)viaLocator.Factory))) return Result();
            }

            return Result();
        }
        catch (Exception ex)
        {
            // The walk itself fell over, which is distinct from none(): none() means the walk ran to
            // the end and found nothing. Should be unreachable — every route above is individually
            // guarded — so if this is ever read, the bug is in this method, not in ATAS.
            return new CacheProbe(null, $"err({Clip(ex.GetType().Name, 40)})");
        }
    }

    /// <summary>
    /// Rule 2's gate. A cache that has been FOUND is not yet a cache that may be BELIEVED.
    ///
    /// Returns "ok" only when the cache is initialised and can answer for the account actually in
    /// use. Every other answer is a word naming why not, and the caller keeps walking.
    ///
    /// A blank account is refused rather than waved through. That is stricter than it was: the old
    /// code skipped the account check entirely when no portfolio was bound, so a cache found before
    /// the chart finished attaching could have turned SupportsOrderHistory true having been confirmed
    /// against nothing. False is the safe direction for rule 2 and true is the expensive one, and
    /// the token says which of the two this is, so the false stays actionable.
    /// </summary>
    string Confirm(IAtasCache cache, string? accountId)
    {
        try
        {
            if (!cache.IsInitialized) return "uninit";

            // A cache that has never heard of this account would answer GetOrders(accountId) with an
            // empty or short list, and that is precisely the answer that makes "this order does not
            // exist" look provable when it is not. GetPortfolio is dump-verified on ICache
            // (`Portfolio GetPortfolio(String accountId)`) and is the cheapest question that settles
            // it.
            if (string.IsNullOrWhiteSpace(accountId)) return "unconfirmed-no-account";
            if (cache.GetPortfolio(accountId) is null) return "foreign";

            return "ok";
        }
        catch (Exception ex) { return Fault(ex); }
    }

    /// <summary>
    /// The open generic IIndicatorDataProvider.GetService&lt;T&gt;(), looked up on the INTERFACE so
    /// that an explicit interface implementation is found too. Null when this ATAS build has no such
    /// method, which is a structural answer rather than a transient one.
    /// </summary>
    static MethodInfo? ServiceMethod()
    {
        try
        {
            return typeof(IAtasDataProvider).GetMethods()
                .FirstOrDefault(m => m.Name == "GetService"
                                     && m.IsGenericMethodDefinition
                                     && m.GetGenericArguments().Length == 1
                                     && m.GetParameters().Length == 0);
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Calls GetService&lt;T&gt;() reflectively, and this is the one place in the file that reaches
    /// for reflection on purpose.
    ///
    /// The dump records `T GetService()` and does not record generic CONSTRAINTS — it prints none
    /// anywhere across its 694 types, so their absence is not evidence of absence. If GetService is
    /// constrained to some ATAS service marker, `GetService&lt;ICache&gt;()` written directly is a
    /// COMPILE error, on the one file in this product that can only be compiled on a machine this
    /// session cannot reach. Reflection turns that unknown into a runtime fact the caller can NAME.
    ///
    /// A null Fault means the call was made. A null Value with a null Fault therefore means the
    /// locator answered and had nothing — which is a real answer about registration, and must never
    /// be collapsed together with the call not having happened at all.
    /// </summary>
    static (object? Value, string? Fault) ResolveService(MethodInfo definition, IAtasDataProvider provider, Type wanted)
    {
        MethodInfo bound;
        // Asked per type argument rather than once per method, because a constraint may well admit
        // IEntityFactory and refuse ICache — and then only the per-route answer is true.
        try { bound = definition.MakeGenericMethod(wanted); }
        catch (ArgumentException) { return (null, "constrained"); }
        catch (Exception ex) { return (null, Fault(ex)); }

        try { return (bound.Invoke(provider, null), null); }
        catch (Exception ex) { return (null, Fault(ex)); }
    }

    /// <summary>Reads the control probe: did the locator answer at all, and with this chart's own
    /// trading manager? See the block comment at its call site for what each word means.</summary>
    static string ControlNote((object? Value, string? Fault) probe, IAtasDataProvider provider)
    {
        if (probe.Fault is { } fault) return fault;
        if (probe.Value is null) return "null";
        // The comparison is a bonus, not the point: if reading the property throws, the locator has
        // still demonstrably answered, and saying so is more useful than discarding that.
        try { return ReferenceEquals(probe.Value, provider.TradingManager) ? "ok-same" : "ok-other"; }
        catch (Exception) { return "ok-uncompared"; }
    }

    /// <summary>Runs one read that belongs to ATAS and cannot be trusted not to throw, turning the
    /// throw into a named token instead of an escape.</summary>
    static (object? Value, string? Fault) Attempt(Func<object?> read)
    {
        try { return (read(), null); }
        catch (Exception ex) { return (null, Fault(ex)); }
    }

    /// <summary>
    /// One token naming an exception: its type, and enough of its message to act on.
    ///
    /// The unwrap is the whole point. MethodInfo.Invoke wraps whatever the target threw in
    /// TargetInvocationException, whose own message is the content-free "Exception has been thrown
    /// by the target of an invocation." Reporting THAT would repeat 2026-08-28's `getservice-threw`
    /// in a longer form: still unable to say whether ICache is simply not registered or the service
    /// locator does not exist yet, which are the two states with opposite consequences.
    /// </summary>
    static string Fault(Exception ex)
    {
        var root = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
        var name = Clip(root.GetType().Name, 40);
        var message = Clip(root.Message, 64);
        return message.Length == 0 ? $"threw({name})" : $"threw({name}:{message})";
    }

    /// <summary>
    /// One bounded, whitespace-free fragment of a diagnostic token.
    ///
    /// THE TRUNCATION IS LOAD-BEARING, NOT TIDINESS. Everything built here ends up inside
    /// BridgeHello.TradingSurface, which travels in the hello frame — a single JSON line on the
    /// bridge pipe, re-sent on every five-second heartbeat, and read in a terminal. An ATAS
    /// exception message is not under this product's control: it can carry a database connection
    /// string, a whole chain of nested messages, or a stack of type names. Letting one through would
    /// trade a legible diagnostic for a handshake that is unreadable or, at the extreme the frame
    /// reader guards against, refused outright — and a diagnostic that can break the handshake
    /// teaches the reader nothing at all.
    ///
    /// Whitespace is replaced rather than kept because SurfaceReport joins its fields with spaces: a
    /// message containing one would silently split into two fields and shift every field after it.
    /// </summary>
    static string Clip(string? raw, int max)
    {
        if (string.IsNullOrEmpty(raw) || max <= 0) return "";
        var kept = new char[Math.Min(raw.Length, max)];
        var n = 0;
        foreach (var c in raw)
        {
            if (n == kept.Length) break;
            if (char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ':') kept[n++] = c;
            // Runs of punctuation collapse to a single dash so the character budget above is spent
            // on the words rather than on quotes and brackets.
            else if (n > 0 && kept[n - 1] != '-') kept[n++] = '-';
        }
        while (n > 0 && kept[n - 1] == '-') n--;
        return new string(kept, 0, n);
    }

    /// <summary>
    /// The route log as one whitespace-free token: kind(route=outcome,route=outcome,...).
    ///
    /// The overall cap is a second, blunter limit on top of the per-message one in <see cref="Clip"/>
    /// — six routes each naming a long exception could still add up to more than any terminal line
    /// can show. Routes are listed in the order they were attempted, so cutting the tail loses the
    /// least, and the cut is marked so a truncated reading is never mistaken for a complete one.
    /// </summary>
    static string Joined(string kind, List<string> entries)
    {
        const int max = 320;
        var body = string.Join(',', entries);
        if (body.Length > max) body = body[..max] + "~cut";
        return $"{kind}({body})";
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

    /// <summary>
    /// THE PLACEMENT THE PRODUCT MAKES, AND THE ONLY ONE IT CAN MAKE.
    ///
    /// This one line is the entire safety argument for the measurement route below it. TradingGateway
    /// is handed an <c>ITradingConnector</c>; its only placement is <c>PlaceOrderAsync</c>, which
    /// sends <c>BridgeOps.Place</c>, which <c>BridgeServer</c> dispatches here — and this passes
    /// <see cref="PlaceRoute.Default"/> unconditionally. There is no overload of it that takes a
    /// route, no setting, no environment variable and no wire field that changes it, deliberately:
    /// each of those would turn an audit that is one line long into a search, and a second way to
    /// submit an order is exactly where a rule-3 misclassification would hide.
    /// </summary>
    public OrderInfo Place(PlaceOrderCommand cmd) => Place(cmd, PlaceRoute.Default);

    /// <inheritdoc/>
    /// <remarks>
    /// MEASUREMENT ONLY, and it places a real order to take the measurement. Reached from
    /// <c>BridgeOps.PlaceViaAsyncOverload</c>, which nothing in the product sends. Everything about
    /// the order — the pre-flight refusals, the write-ahead witness record, the acknowledgement wait,
    /// the classification of what comes back — is the same code path as the line above; the single
    /// difference is which <c>ITradingManager</c> overload submits it, and that difference is the
    /// whole experiment.
    /// </remarks>
    public OrderInfo PlaceViaAsyncOverload(PlaceOrderCommand cmd) => Place(cmd, PlaceRoute.MeasureAsync);

    internal OrderInfo Place(PlaceOrderCommand cmd, PlaceRoute route)
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

        // THE WRITE-AHEAD RECORD, AND IT GOES DOWN BEFORE THE ORDER DOES.
        //
        // That ordering is the whole evidential value of the file. The claim "this product is about
        // to submit this identifier" is made while there is still no order to describe, by a process
        // that will be gone by the time a later run reads it — so it cannot be a story composed
        // afterwards to fit an order somebody found in ATAS's book. Written after the submission it
        // would say exactly the same words and prove nothing, and nothing in the data would show
        // which of the two happened. Do not move this below the OpenOrder call.
        //
        // AND THE ORDER IS REFUSED WHEN THE CLAIM DID NOT LAND. Submitting returns whether the
        // record reached the disk. Rule 1 rests on that record: it is the only thing that can answer
        // "did this product submit this identifier" for a process that has already ended, and a
        // claim that lived solely in this process's memory is not one. So an order whose identifier
        // could not be recorded is not sent.
        //
        // IT SITS ABOVE THE LOCK, and that placement is load-bearing. Below it, _submitted, _touched
        // and _clientOrderIdAttempts have already been written, and refusing there would leave all
        // three describing an order that was never sent — _clientOrderIdAttempts in particular
        // answers "was anything ever put to ATAS carrying an id", which would then be false. Here
        // the order object is fully built and validated and nothing has been recorded anywhere yet.
        //
        // THE THROW IS A DEFINITE REFUSAL AND RULE 3 IS INTACT. Rule 3 is about ambiguity AFTER the
        // order has been handed to ATAS; OpenOrder is a long way below this line and nothing has
        // reached the platform, so "nothing was submitted" is the literal truth — the same shape as
        // the four pre-flight refusals above. Still Guarded, because an EXCEPTION out of the witness
        // must not become the outcome of an order: the false return is the refusal, never a throw
        // from a diagnostic.
        var recorded = false;
        Guard(() => recorded = _witness.Submitting(cmd.ClientOrderId, portfolio.AccountID, cmd.Symbol,
                                                   cmd.Side.ToString(), cmd.Quantity, cmd.LimitPrice));
        if (!recorded)
            throw new AtasRejectedException(
                $"the write-ahead record for {cmd.ClientOrderId} could not be written to " +
                $"{_witness.Path ?? "<no witness file>"}; nothing was submitted. " +
                (_witness.LastWriteFailure ?? ""));

        lock (_gate)
        {
            Trim();
            // Both lines, and they are the same key today: Comment IS the client order id on an order
            // this method just built. ClearFailures is here anyway because it is the one thing that
            // stays in step with Lookup — a stale reason left under any key the lookup can reach is
            // read back as a fresh refusal the instant this order is submitted.
            ClearFailures(order);
            _failures.Remove(cmd.ClientOrderId);
            _submitted[cmd.ClientOrderId] = order;
            // BEFORE ATAS is handed this object, and inside the same lock that records it in
            // _submitted. The order-event fan runs on ATAS's thread and reaches ProveClientOrderId
            // the instant the order becomes visible there, so registering afterwards would leave a
            // window in which our own object is the proof.
            _touched.Add(order);
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
        // ---- THE MEASUREMENT, AND ON THE DEFAULT ROUTE IT CHANGES NOTHING ABOUT THE SUBMISSION ----
        //
        // A stopwatch and two property reads. On PlaceRoute.Default — the only route the gateway can
        // reach, see the one-line public Place above — the submission below is what it always was.
        //
        // What it is for: the four synchronous order calls are obsolete and cannot be given a
        // deadline, so a block in any of them wedges BridgeServer's frame loop — including the
        // operator's cancel-all — while the heartbeat goes on reporting READY (trap 31). Switching
        // them to the Async overloads is what lets AtasCall.Block reach them. Whether that is safe
        // turns on ONE fact: does OpenOrderAsync's task complete on SUBMISSION or on broker
        // ACKNOWLEDGEMENT? See the long comment on AtasCall.Block.
        //
        // IT TAKES TWO RUNS, AND THIS IS THE FIRST OF THEM. The WaitFor below already waits for
        // exactly the condition that separates the two answers — a state change or an assigned Id,
        // which is acknowledgement arriving. So the gap between the call returning and that condition
        // holding IS this platform's acknowledgement latency, measured through the path actually in
        // use, and it decides whether the question is answerable here at all:
        //
        //   gap large  -> submission and acknowledgement ARE separable here, so a run through
        //                 PlaceRoute.MeasureAsync will say which of the two its task waits for.
        //   gap ~zero  -> they are NOT separable on this platform, so a fast async completion would
        //                 be no evidence at all. Report that; do not round it to a green.
        //
        // The second run is the branch further down. It reports the same token, so `call=` under
        // place=asyncoverload is read against `call=` under place=sync from an ordinary run: alike
        // means the task completed on submission, near `settled=` means it waited for the broker.
        //
        // Microseconds as an integer, deliberately: this machine formats decimals with a comma, and
        // a comma would be indistinguishable from a separator in this token.
        //
        // ---- AND THE ONE SUBMISSION THAT IS NOT THE ORDINARY ONE ----
        //
        // route == MeasureAsync submits through ITradingManager.OpenOrderAsync and blocks on the
        // task, which is the only way to observe what that task actually waits for. `call=` then
        // times the ASYNC call rather than the synchronous one, and the answer is read straight off
        // the token: near the synchronous reading means the task completes on SUBMISSION and the four
        // obsolete call sites can be flipped; near `settled=` means it completes on ACKNOWLEDGEMENT.
        //
        // WHY THE `feed` BRANCH STILL WINS. An off-chart order needs the connector to reach an
        // instrument or portfolio the trading manager has not selected — that is a correctness
        // requirement for the ORDER, and a measurement does not get to override one. The reading is
        // subordinate: when the connector route is taken, `place=connector` says so and the run
        // simply did not answer the question. Reading that token before believing a number is the
        // harness's job, and tools/probe does it.
        //
        // RULE 3 IS UNCHANGED BY THIS BRANCH, and that is the point of routing it through
        // AtasCall.Block rather than giving it its own error handling. There is no catch here — the
        // whole write path has none — so an expiry raises AtasCallTimeoutException and PROPAGATES,
        // the wire reads it as indefinite, the gateway records UNKNOWN and reconciles. A definite
        // refusal still arrives the only way it ever does: through the _failures dictionary, fed by
        // ATAS's own OrderRegisterFailed event, which the sync/async choice does not touch.
        //
        // Blocking is safe here for the same reason it is safe on the connector branch above: this
        // runs on BridgeServer's pipe thread, never on ATAS's GUI thread, so a call that marshals
        // itself to the GUI thread is not waiting on the thread that is waiting for it.
        var placeRoute = feed is not null ? "connector"
                       : route == PlaceRoute.MeasureAsync ? "asyncoverload"
                       : "sync";
        var placeClock = System.Diagnostics.Stopwatch.StartNew();

        if (feed is not null)
            AtasCall.Block(feed.RegisterOrderAsync(order), CallTimeout, "RegisterOrderAsync");
        else if (route == PlaceRoute.MeasureAsync)
            AtasCall.Block(trading.OpenOrderAsync(order, setDefaultQuantity: false, askConfirmation: false,
                                                  checkOrderStates: true), CallTimeout, "OpenOrderAsync");
        else
            trading.OpenOrder(order, setDefaultQuantity: false, askConfirmation: false, checkOrderStates: true);

        var placeCallUs = (long)(placeClock.Elapsed.TotalMilliseconds * 1000);
        // Guarded like every other diagnostic in this method: reading an ATAS object the platform is
        // mutating on its own thread must never turn a placed order into an UNKNOWN one.
        var placeAtReturn = "unread";
        Guard(() => placeAtReturn = OrderShape(order));

        WaitFor(() => Failure(cmd.ClientOrderId, order) is not null
                      || order.State != AtasOrderStates.None
                      || !string.IsNullOrEmpty(order.Id));

        var placeSettledUs = (long)(placeClock.Elapsed.TotalMilliseconds * 1000);
        Guard(() => _lastPlace =
            $"{placeRoute};call={placeCallUs}us;atreturn={placeAtReturn};" +
            $"settled={placeSettledUs}us;gap={placeSettledUs - placeCallUs}us;now={OrderShape(order)}");

        if (Failure(cmd.ClientOrderId, order) is { } refusal)
            throw new AtasRejectedException(refusal);

        // THE HALF WE DID NOT WRITE. Order.Id as ATAS assigned it, recorded against the claim made
        // above. A later process cannot accept a cross-session match on the identifier alone — any
        // order carrying that comment would satisfy it — so this is what lets it require that the
        // order in front of it is the order this run submitted. Guarded for the same reason the
        // write-ahead call is: it runs after the order has been accepted, where an exception would
        // turn a placed order into an UNKNOWN one.
        //
        // WaitFor above has already settled or timed out, so Order.Id is either assigned or is not
        // coming promptly; Identified ignores an empty id, and OnOrderPayload records it later if
        // ATAS assigns one after this returns.
        Guard(() => _witness.Identified(cmd.ClientOrderId, order.Id));

        // GUARDED, and the guard is the whole point. This is a diagnostic, and it enumerates ATAS's
        // own order collection — which ATAS may be mutating on its own thread at precisely this
        // moment, because the order was just added to it. Unguarded, a "Collection was modified"
        // thrown in here propagates out of Place for an order that was placed perfectly well, and the
        // gateway records UNKNOWN and goes reconciling a success. A diagnostic must never change the
        // outcome of the operation it observes; the identical call in OnOrderPayload is Guarded for
        // the same reason, and so are SurfaceReport's counts.
        Guard(() => ProveClientOrderId(cmd.ClientOrderId));
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
        // EVERY key Lookup can match on, not just this one. Clearing only OrderKey let a single
        // OrderCancelFailed recorded against this order under its client id survive, and then every
        // later modify of it was submitted to the broker and unconditionally reported as a definite
        // refusal on the strength of a reason that belonged to a cancel that had already happened.
        // Rule 3: AtasRejectedException is for a definite refusal of THIS request and nothing else.
        lock (_gate)
        {
            ClearFailures(order);
            // RULE 1, AND IT IS NOT DEFENSIVE. Clone() copied Comment, so `replacement` is an object
            // THIS ADAPTER CONSTRUCTED carrying OUR client order id — and _submitted still holds the
            // original under that id, so a read-back asking only "is this the instance I submitted"
            // sees a different object with our identifier on it and records Distinct. That is the
            // adapter proving rule 1 against itself, and the capability that gates autonomous live
            // trading turns true on it. Registered here, before ModifyOrder can put it anywhere ATAS
            // enumerates.
            _touched.Add(replacement);
        }

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

    /// <summary>
    /// What a cancel actually achieved. FOUR outcomes, not two, because Cancel and CancelAll have to
    /// react differently to the same events and "void, or an exception" cannot carry the difference:
    ///
    ///   * a pre-flight refusal and a broker refusal are both AtasRejectedException to a caller, and
    ///     they are opposites for the book — the first means nothing was sent, the second means the
    ///     order is STILL WORKING. CancelAll swallowed both as "nothing is live";
    ///   * a cancel that was submitted and never acknowledged is neither a success nor a refusal, and
    ///     returning void reported it to the operator as a completed cancellation.
    /// </summary>
    enum CancelResult
    {
        /// <summary>ATAS reported the order finished. Nothing is live.</summary>
        Confirmed,

        /// <summary>Refused before anything was submitted — already finished, or not an order ATAS
        /// knows. Nothing was sent, so this cancel left nothing new live.</summary>
        NotSubmitted,

        /// <summary>The broker refused the cancellation. THE ORDER IS STILL WORKING.</summary>
        Refused,

        /// <summary>Submitted, and ATAS said nothing either way inside AckTimeout. The order may be
        /// cancelled, may be working, may have filled. Rule 3's "anything ambiguous".</summary>
        Unconfirmed
    }

    /// <summary>
    /// The one cancel implementation. It CLASSIFIES rather than throwing, so that the two callers can
    /// each be truthful: <see cref="Cancel"/> has only exceptions to speak with, and CancelAll needs
    /// to tell "already finished" apart from "the broker said no and it is still on the book".
    /// </summary>
    (CancelResult Result, string Detail) CancelCore(string connectorOrderId)
    {
        var trading = RequireTrading();
        var order = FindOrder(connectorOrderId);
        if (order is null)
            return (CancelResult.NotSubmitted, $"ATAS does not know order '{connectorOrderId}'; nothing was submitted");
        if (order.State is AtasOrderStates.Done or AtasOrderStates.Failed)
            return (CancelResult.NotSubmitted, "order is not cancellable; nothing was submitted");

        var key = OrderKey(order);
        // Every key Lookup reads, for the same reason as Modify: one earlier OrderCancelFailed
        // recorded under this order's client id used to make every later cancel of it report a
        // definite broker refusal that had already happened once. Rule 3.
        lock (_gate) ClearFailures(order);

        trading.CancelOrder(order, askConfirmation: false, checkOrderStates: true);

        WaitFor(() => Failure(key, order) is not null || order.State is AtasOrderStates.Done or AtasOrderStates.Failed);
        if (Failure(key, order) is { } refusal) return (CancelResult.Refused, refusal);
        if (order.State is AtasOrderStates.Done or AtasOrderStates.Failed) return (CancelResult.Confirmed, "");

        return (CancelResult.Unconfirmed,
            $"the cancellation of '{connectorOrderId}' was submitted and ATAS did not confirm it within " +
            $"{AckTimeout.TotalSeconds:0}s; the order may still be working and must be reconciled, not assumed gone");
    }

    /// <summary>
    /// IAtasAdapter.Cancel returns void, so the only way to say "submitted, unconfirmed" is to not
    /// return normally — and it must not return normally, because a silent return is read all the way
    /// up as a completed cancellation. An ordinary exception is exactly right for it: the bridge
    /// sends rejected=false, the connector raises ConnectorTransportException, and the gateway
    /// settles the request UNKNOWN and pauses execution capability with "a cancellation is
    /// unconfirmed" — which is the truth. Rule 3, from the side people forget.
    ///
    /// Both refusals stay AtasRejectedException because both are definite. Note what a REFUSED cancel
    /// does not mean: the order is still working. Only CancelAll can act on that difference, and it
    /// reads <see cref="CancelCore"/> directly to get it.
    /// </summary>
    public void Cancel(string connectorOrderId)
    {
        var (result, detail) = CancelCore(connectorOrderId);
        switch (result)
        {
            case CancelResult.Confirmed: return;
            case CancelResult.NotSubmitted:
            case CancelResult.Refused: throw new AtasRejectedException(detail);
            default: throw new InvalidOperationException(detail);
        }
    }

    /// <summary>
    /// Best effort by design. One order ATAS definitively refuses to cancel because it is already
    /// finished must not stop the rest from being pulled — that is the emergency path.
    ///
    /// WHAT THIS USED TO GET WRONG: it caught AtasRejectedException and dropped it, commented
    /// "definitively not cancellable; nothing is live". True of a pre-flight refusal. FALSE of a
    /// broker-refused cancel, which means the broker declined to pull an order that is STILL ON THE
    /// BOOK — and such an order appeared in neither the cancelled list nor the ambiguous one, so the
    /// operator was told the book was clear while it was not. On the emergency path.
    ///
    /// Three ways this can now fail to clear an order, kept apart in the message because they need
    /// different actions: still working (go cancel it another way), unaddressable (visible with no id
    /// to cancel by), and unknown (reconcile it). Only a confirmed cancellation is reported as one.
    /// </summary>
    public IReadOnlyList<string> CancelAll(string accountId)
    {
        var cancelled = new List<string>();
        var working = new List<string>();
        var unaddressable = new List<string>();
        var ambiguous = new List<string>();

        foreach (var o in GetOrders(accountId, includeInactive: false, since: null))
        {
            var id = o.ConnectorOrderId;
            if (id.Length == 0)
            {
                // Nothing identifies it, so there is no id to send a cancel with. Silence here would
                // be the same lie in a quieter form.
                unaddressable.Add($"{o.Symbol} {o.Side} {o.Quantity} ({o.ClientOrderId ?? "no client order id"})");
                continue;
            }
            try
            {
                switch (CancelCore(id).Result)
                {
                    case CancelResult.Confirmed: cancelled.Add(id); break;
                    case CancelResult.NotSubmitted: break;
                    case CancelResult.Refused: working.Add(id); break;
                    default: ambiguous.Add(id); break;
                }
            }
            catch (Exception) { ambiguous.Add(id); }
        }

        var problems = new List<string>();
        if (working.Count > 0)
            problems.Add($"still working after the broker refused to cancel: {string.Join(", ", working)}");
        if (unaddressable.Count > 0)
            problems.Add($"on the book with no id to cancel by: {string.Join(", ", unaddressable)}");
        if (ambiguous.Count > 0)
            problems.Add($"unknown outcome: {string.Join(", ", ambiguous)}");
        if (problems.Count > 0)
            throw new InvalidOperationException(
                $"cancel-all did not clear the book — {string.Join("; ", problems)}. Cancelled " +
                $"{cancelled.Count}. These must be dealt with, not assumed flat");
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

        // THE WRITE-AHEAD RECORD FOR A CLOSE GOES IN BEFORE ATAS IS ASKED — the same rule as Place,
        // for the same reason, on the path that moves the most money.
        //
        // This is the operator's close-all. It ends in ITradingManager.ClosePosition, which creates
        // an order; the identifier is written onto that order AFTERWARDS, by hand, because ATAS
        // decides the side and the order does not exist until it has. That ordering meant a witness
        // that could not write could not stop this order the way it stops every other one: Place
        // asks and refuses, and this asked nothing at all. An order that moves a real position was
        // therefore the one order in the product with no durable record that it was submitted.
        //
        // Placed HERE, after the position is found and before anything is asked of ATAS: with no
        // position there is nothing to submit and nothing to record, and below this line the close
        // is on its way and a refusal would be a lie.
        //
        // THE SIDE IS NOT INFERRED, here or anywhere on this path — the sign convention on
        // Position.Volume is not proven by the dump and a wrong guess would double a position rather
        // than flatten it. The record says what is true of the SUBMISSION: a close of this size on
        // this instrument, with ATAS choosing the direction.
        var recorded = false;
        Guard(() => recorded = _witness.Submitting(clientOrderId, accountId, symbol, "Close",
                                                   Math.Abs(position.Volume), null));
        if (!recorded)
            throw new AtasRejectedException(
                $"the write-ahead record for {clientOrderId} could not be written to " +
                $"{_witness.Path ?? "<no witness file>"}; nothing was submitted. " +
                (_witness.LastWriteFailure ?? ""));

        // Reference identity, not OrderKey. An order ATAS has not identified yet has no key to be
        // diffed by — they all used to collapse onto one string — and "is this the same object I
        // already saw" is exactly the question a before/after diff is asking. LiveOrders yields each
        // entity once by the same identity, so the two agree.
        var before = new HashSet<AtasOrder>(LiveOrders());

        // Same flags, same reasons. The boolean this returns has no documented meaning in the dump,
        // so it is NOT treated as a definite refusal — a false becomes part of the message below if
        // no order appears, and rule 3 keeps that an ordinary exception so the gateway reconciles.
        var accepted = trading.ClosePosition(position, askConfirmation: false, checkOrderStates: true);

        AtasOrder? created = null;
        WaitFor(() =>
        {
            created = LiveOrders()
                .FirstOrDefault(o => !before.Contains(o) && SymbolMatches(o.Security, o.SecurityId, symbol));
            return created is not null;
        });

        if (created is null)
            throw new InvalidOperationException(
                $"ATAS was asked to close {symbol} (it returned {(accepted ? "true" : "false")}) but the " +
                "resulting order could not be identified; it must be reconciled, not assumed flat");

        // Best effort only, and never counted as proof of a round trip: label the order ATAS created
        // so reconciliation has something of ours to match on.
        //
        // THIS IS THE ONE PLACE THE ADAPTER WRITES ITS OWN IDENTIFIER ONTO AN OBJECT ATAS BUILT, and
        // that is precisely the shape rule 1 forbids as evidence — a comment we typed on, read back
        // as though ATAS had carried it. Two things keep it honest, and only one of them was ever
        // designed:
        //
        //   * incidental: `clientOrderId` never enters _submitted, so ProveClientOrderId refuses it
        //     at its first guard and never even looks. Anyone who "fixes" that asymmetry — making
        //     the closing order findable by client id, or counting it as an attempt — hands the
        //     proof a comment the adapter wrote by hand onto ATAS's own object;
        //   * designed: `created` is registered as touched BEFORE the Comment is written, so from
        //     the instant it carries our identifier it is already refused as evidence, whatever
        //     _submitted later comes to hold. Registering after the write would leave a window in
        //     which the order-event fan, on ATAS's thread, could read the label as ATAS's own work.
        if (string.IsNullOrEmpty(created.Comment))
        {
            lock (_gate) _touched.Add(created);
            created.Comment = clientOrderId;
        }
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
    /// THE quote feed on a chart strategy, and the thing whose absence stopped a placement.
    ///
    /// This used to fan the payload out as <see cref="AtasSecurity"/> and re-publish off the Security
    /// object. That reads the wrong surface: the payload of BestBidAskChanged is market data, and the
    /// chart's Security on ATAS 8.0.14.397 carries BestBidPrice = 0 — see the quotes section for the
    /// measurement. So the handler now reads the payload for what it is.
    ///
    /// Subscribed here, alongside <see cref="HookTrading"/>, off the same Bind() call and with the
    /// same idempotence check. Nothing is ever unsubscribed, for the same reason HookTrading
    /// unsubscribes nothing: a fresh lambda cannot be removed with '-=' anyway. Each handler closes
    /// over the provider it subscribed to and <see cref="IsLive(IAtasOnlineData?)"/> compares that
    /// against the live one, so a subscription to a provider ATAS has since replaced goes inert
    /// instead of writing stale prices into the book.
    /// </summary>
    void HookOnline(IAtasOnlineData? online)
    {
        if (online is null) return;
        lock (_gate)
        {
            if (ReferenceEquals(_hookedOnline, online)) return;
            _hookedOnline = online;
        }

        // Dump, verbatim, on `interface ATAS.Indicators.IOnlineDataProvider`:
        //
        //     event Action`1 BestBidAskChanged
        //     event Action`1 NewTrades
        //
        // Arities are dump-verified; the generic ARGUMENTS are not — the dump prints Action`1 and
        // never Action<T> — so neither lambda names one. The parameter stays implicitly typed, the
        // payload widens to object, and OnMarketData matches on the runtime type. Same convention as
        // every other ATAS event in this file, and the only one that cannot bind to the wrong shape.
        //
        // NewTrades is subscribed for the LAST price alone. Bid and ask come from BestBidAskChanged;
        // nothing derives a bid from a trade print, because the last trade is not a side of the book
        // and pricing a resting order off it would be pricing off a different number than the one
        // this was designed around.
        online.BestBidAskChanged += a => Guard(() => OnMarketData(online, a));
        online.NewTrades += a => Guard(() => OnMarketData(online, a));
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

    /// <inheritdoc cref="IsLive(IAtasTrading?)"/>
    bool IsLive(IAtasOnlineData? online) => online is not null && ReferenceEquals(online, DataProvider?.OnlineDataProvider);

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
            if (!string.IsNullOrEmpty(o.Comment))
            {
                // ATAS assigns Order.Id asynchronously, so Place's own attempt at this can run
                // before there is an id to record. This is the other place it can arrive.
                //
                // IT IS SAFE TO CALL IT FOR EVERY ORDER THAT CROSSES THE FEED, and the safety is
                // CoidWitness.Identified's rather than this call site's: it writes only into a
                // record belonging to the RUNNING session. An order carrying a PRIOR session's
                // comment — restored from a workspace, or placed by hand with the same text — is a
                // no-op here, which is exactly the guard that stops such an order writing its own
                // id into the record and then matching itself on the next read-back.
                // A STOPPED STRATEGY RECORDS NOTHING. See _teardown: this fan is still subscribed
                // after the teardown, and every call here reaches for the witness. The check and the
                // write are one act under the lock the release takes — asking the flag and then
                // writing left a window the width of the whole write, in which the lease was
                // released and then taken straight back by a strategy that no longer exists.
                //
                // ProveClientOrderId is deliberately OUTSIDE it: it scans ATAS's order book, and
                // holding a lock that teardown waits on across that scan would put an unbounded wait
                // on ATAS's own thread — the shape AtasCall exists to keep out of this file. It runs
                // only when the write was allowed, and it takes no lease.
                if (_teardown.Record(() => _witness.Identified(o.Comment, o.Id)))
                    ProveClientOrderId(o.Comment);
            }
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
            // Identities only, and exactly the list Lookup reads back. OrderKey used to be written
            // here too, and for an order ATAS had not identified yet that key was "ext:0" — SHARED by
            // every such order. A refusal filed under it was then read back as the refusal of the
            // next order placed: submitted to the broker, then settled REJECTED, then never
            // reconciled, because "rejected" is precisely the state the gateway does not reconcile.
            // Rule 3 exists to prevent that exact sequence. An order with no identity at all now
            // records nothing, per the summary above: dropping an unattributable failure costs a
            // reason, attributing it costs an order.
            foreach (var o in orders)
                foreach (var key in FailureKeys(o))
                    _failures[key] = reason;
        }
        _pulse.Set();
        foreach (var o in orders) OrderChanged?.Invoke(ToOrder(o, null));
    }

    // ---------------------------------------------------------------- quotes

    // WHY THIS SECTION WAS REWRITTEN, AND WHAT THAT IS NOT A STORY ABOUT
    //
    // The bridge refused to place a test order because it had no price. Measured on ATAS
    // 8.0.14.397, live, on an ES 5m chart that was attached, activated and answering every other
    // read — the probe's own output, verbatim:
    //
    //     QUOTE (raw)  : {"symbol":"ES","at":"0001-01-01T00:00:00+00:00"}
    //     REFUSED TO PLACE : THE QUOTE CARRIES NO USABLE BID.
    //
    // `at` is DateTimeOffset.MinValue and bid/ask/last are absent entirely, which is this adapter
    // saying it has never written a quote for ES and that Security.BestBidPrice reads 0.
    //
    // THIS IS NOT A REGRESSION FROM THE ITradingManager REWIRING. Quotes came from two places
    // before it and neither ever worked here: IDataFeedConnector events, on a Connector that is null
    // for a chart strategy (trap 13), and Security.PropertyChanged on a Security object that the
    // reading above shows carries no level-1 data at all. Both were already dead. The rewiring did
    // not break quotes; it removed the last thing that made it possible not to notice they had never
    // worked. Anyone reading this while bisecting will not find the commit that broke it.
    //
    // What ATAS actually gives a chart strategy, both dump-verified:
    //
    //     interface ATAS.Indicators.IOnlineDataProvider ...
    //         event Action`1 BestBidAskChanged
    //         event Action`1 NewTrades
    //     abstract class ATAS.Strategies.Chart.ChartStrategy : Indicator
    //         MarketDataArg BestAsk { get; set; }
    //         MarketDataArg BestBid { get; set; }
    //
    // The events are the stream; the two properties are the same data at rest, for the moment
    // before any event has arrived. Both carry MarketDataArg, which carries its own Time — and that
    // is the whole reason this is worth doing rather than reading Security.BestBidPrice and
    // stamping DateTime.UtcNow on it. A price with a manufactured timestamp defeats
    // QuoteInfo.IsStale, and IsStale is the only thing standing between the gateway and an order
    // sized off a book that stopped updating.
    //
    // THE FEED ON THIS MACHINE IS dxFeed 15-MINUTE DELAYED. A correct ES quote therefore reads about
    // 900 seconds old and that is not a broken clock, a dead feed, or a wrong DateTimeKind. The
    // quote= token in the surface report exists so that reading is not mistaken for any of them.

    /// <summary>
    /// The quote clock, and it is deliberately NOT <see cref="ToOffset"/>.
    ///
    /// ATAS hands out plain DateTimes and the dump records no Kind, so Unspecified has to be read as
    /// something. THE TWO READINGS FAIL IN OPPOSITE DIRECTIONS AND ONLY ONE IS SAFE HERE:
    ///
    ///   * As UTC — what ToOffset does. If ATAS is in fact handing out local wall clock, this
    ///     machine runs UTC+2, so the quote lands two hours in the FUTURE. UtcNow - At goes
    ///     negative, QuoteInfo.IsStale(maxAge) returns false for a quote of any age whatsoever, and
    ///     the one check that stops the gateway pricing off a dead book is silently disabled.
    ///   * As LOCAL — what this does. If ATAS is in fact handing out UTC, the quote looks exactly
    ///     the machine's own offset too old, and IsStale REFUSES it. A refused quote costs a probe
    ///     run. An accepted dead one costs money.
    ///
    /// So Unspecified is read as local. <see cref="ToOffset"/> keeps reading it as UTC because its
    /// failure mode is the other way round: it timestamps orders and trades, GetOrders never lets a
    /// timestamp drop a working order, and nothing on that path can be made to look FRESH.
    ///
    /// THE ARGUMENT ABOVE IS ABOUT THIS MACHINE, AND THAT IS NOT GOOD ENOUGH ON ITS OWN. It runs
    /// UTC+2, so local-read-as-UTC is the reading that lands in the future. West of Greenwich the two
    /// swap over: on a UTC-5 box a UTC time read as local would land five hours ahead instead, and
    /// the choice below would be the dangerous one. So the choice is not what makes this safe —
    /// <see cref="Compose"/> refusing a quote stamped in the future is. Wrong in either direction, on
    /// any machine, the quote is refused rather than accepted.
    ///
    /// NOTHING HERE IS GUESSING WHICH IT IS, AND NOTHING HAS TO. The surface report carries kind=
    /// and age= for exactly this, and one probe run settles it outright:
    ///
    ///     kind=utc                     no assumption was used; the reading is whatever age= says.
    ///     kind=local, age≈900s         ATAS marks its market data local, this reads it as local,
    ///                                  and 900s is the 15-minute delay. Correct.
    ///     kind=unspecified, age≈900s   the assumption here is right. Nothing to change.
    ///     kind=unspecified, age≈8100s  900s plus this machine's UTC+2 offset: ATAS is handing out
    ///                                  UTC as Unspecified, and the Unspecified branch below should
    ///                                  be switched to the UTC one.
    ///     a NEGATIVE age               the quote is stamped in the future, so this reading is wrong
    ///                                  the other way. Compose has already unset the quote's At, so
    ///                                  IsStale refuses it and nothing is priced off it; the token
    ///                                  still shows the raw offset so the size of the error names
    ///                                  the timezone that caused it.
    ///
    /// A DateTime nobody set stays unset: default in, MinValue out, never "now".
    /// </summary>
    static DateTimeOffset ToQuoteTime(DateTime t)
    {
        if (t == default) return DateTimeOffset.MinValue;
        if (t.Kind == DateTimeKind.Utc) return new DateTimeOffset(t, TimeSpan.Zero);
        if (t.Kind == DateTimeKind.Local) return new DateTimeOffset(t);

        // MEASURED, 2026-08-28, ATAS 8.0.14.397: Unspecified means UTC.
        //
        // This branch used to read Unspecified as LOCAL, reasoning that on a UTC+2 machine the
        // opposite mistake would stamp the quote in the FUTURE, make UtcNow - At negative, and leave
        // IsStale returning false for a quote of any age — silently disabling the only check between
        // the gateway and a dead book. The reasoning was sound and the premise was wrong. The live
        // reading settled it in one run:
        //
        //   quote=event(bid=7753.75,ask=7754.00,age=8544s,kind=unspecified)
        //
        // 8544s is a hair over two hours plus the feed's own delay. This machine is UTC+2 and the ES
        // feed is dxFeed 15-minute delayed, so the true age is ~900s and the extra 7200s is exactly
        // this conversion. Read as local, every quote looks two hours stale, IsStale refuses all of
        // them, and the gateway can never size an order — the failure is total rather than subtle,
        // which is the only reason it was cheap to find.
        //
        // The safety net below is what actually makes this safe, and it stays regardless: it is a
        // measurement of one platform on one machine, and the sign of the error flips west of
        // Greenwich. Compose() still unsets At for anything stamped more than 60s in the future, so
        // being wrong in either direction refuses the quote rather than trusting it.
        try { return new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc), TimeSpan.Zero); }
        catch (ArgumentOutOfRangeException) { return DateTimeOffset.MinValue; }
    }

    void Track(AtasSecurity s)
    {
        lock (_gate) { if (!_tracked.Add(s)) return; }
        // PropertyChangedEventHandler is a BCL delegate, so unlike the ATAS events this one can be
        // stored as a method group and removed again. It is kept because on a host where the Security
        // object IS fed it is a real quote source — but it is no longer described as the primary one:
        // on this platform it was measured carrying nothing at all.
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

    /// <summary>
    /// A market-data update from the chart's own feed: BestBidAskChanged, or NewTrades for the last
    /// price. Runs inside <see cref="Guard"/> like every other ATAS callback — an exception thrown
    /// back into ATAS's data loop can take down subscribers that have nothing to do with us.
    ///
    /// A payload from a provider ATAS has since replaced is dropped rather than written, which is
    /// what <see cref="IsLive(IAtasOnlineData?)"/> is for.
    /// </summary>
    void OnMarketData(IAtasOnlineData online, object? payload)
    {
        if (!IsLive(online)) return;

        // IOnlineDataProvider is THIS CHART's data provider, so an update it raises is about this
        // chart's instrument. MarketDataArg names no Security of its own — the dump lists Price,
        // Volume, Time, DataType, Direction, OpenInterest, OriginPrice, IsBid, IsAsk and two
        // exchange order ids, and nothing else — so there is no other symbol it could honestly be
        // filed under. With no bound security there is no key, and inventing one would file one
        // instrument's book under another instrument's symbol.
        var own = SymbolOf(BoundSecurity);

        // A bool rather than a set of symbols: the only key anything below can push is `own`, since
        // the tape deliberately pushes nothing (see ApplyMarketData). This runs per tick, so it does
        // not allocate one.
        var moved = false;
        lock (_gate)
        {
            foreach (var a in Fan<AtasMarketData>(payload))
                if (own.Length > 0 && ApplyMarketData(own, a)) moved = true;

            // The other shape NewTrades could carry. Its generic argument is not in the dump either,
            // and ATAS.DataFeedsCore.Trade is the plausible alternative to a MarketDataArg — so both
            // are read and whichever the payload really holds is the one that matches. Unlike
            // MarketDataArg a Trade names its own Security, so it is keyed off that where it has one.
            foreach (var t in Fan<AtasTrade>(payload))
            {
                var key = SymbolOf(t.Security);
                if (key.Length == 0) key = own;
                if (key.Length == 0) continue;
                // Recorded, deliberately not pushed — see ApplyMarketData for why the tape does not
                // raise QuoteChanged.
                Merge(key, QuoteSide.None, QuoteSide.None,
                    QuoteSide.From(t.Price, t.Volume, t.Time, QuoteSource.MarketData));
            }
        }

        // Outside the lock: a subscriber is arbitrary code and must never run holding _gate.
        if (moved) EmitQuote(own);
    }

    /// <summary>
    /// One market-data update is one SIDE of the book, or one print on the tape.
    ///
    /// IsBid / IsAsk are read instead of DataType deliberately. The dump prints simple type names,
    /// and TWO enums called MarketDataType exist in it with identical members —
    /// ATAS.Indicators.MarketDataType and ATAS.DataFeedsCore.MarketDataType — so which one DataType
    /// is typed as cannot be settled from the dump, and naming the wrong one would not compile
    /// against the real assembly. The dump's `Boolean IsAsk { get; }` and `Boolean IsBid { get; }`
    /// are unambiguous, and "neither" is precisely the third member that enum has: a trade.
    ///
    /// Caller holds <see cref="_gate"/>. Returns whether the BOOK changed — which is not the same
    /// as whether a price changed, see below.
    /// </summary>
    bool ApplyMarketData(string key, AtasMarketData a)
    {
        var side = QuoteSide.From(a.Price, a.Volume, a.Time, QuoteSource.MarketData);
        if (a.IsBid) return Merge(key, side, QuoteSide.None, QuoteSide.None);
        if (a.IsAsk) return Merge(key, QuoteSide.None, side, QuoteSide.None);

        // A print on the tape. It updates `last` in the book, so the next GetQuote is exact — but it
        // does NOT raise QuoteChanged, and that is a deliberate limit on what this change switches on.
        //
        // Nothing in the product subscribes to the quote stream: TradingGateway PULLS with
        // GetQuoteAsync and never handles QuoteChanged. BridgeServer.Push is fire-and-forget behind a
        // single semaphore, and the tape is the highest-rate stream ATAS has — so pushing a frame per
        // print would put hundreds of writes a second on a pipe with no reader waiting for them, for
        // a number no consumer reads. Book changes still push; they are the ones that mean something.
        Merge(key, QuoteSide.None, QuoteSide.None, side);
        return false;
    }

    /// <summary>Records the current prices WITHOUT a timestamp, so the first genuine move is
    /// detected as a move rather than mistaken for one. Written at the weakest source rank, so it
    /// cannot displace a side a real tick has already filled.</summary>
    void SeedQuote(AtasSecurity s)
    {
        var key = SymbolOf(s);
        if (key.Length == 0) return;
        lock (_gate) Merge(key, ReadSide(s.BestBidPrice, s.BestBidVolume), ReadSide(s.BestAskPrice, s.BestAskVolume),
            ReadSide(s.LastTradePrice ?? 0m, 0m));
    }

    /// <summary>
    /// The Security surface's own path. It fires when ATAS mutates the Security object, so a price
    /// that differs from the one recorded here really was OBSERVED to move and "now" is an honest
    /// stamp for it. An unchanged price emits nothing and refreshes nothing: a PropertyChanged for
    /// LotSize must not be able to make an hour-old quote look current.
    /// </summary>
    void PublishQuote(AtasSecurity s)
    {
        var key = SymbolOf(s);
        if (key.Length == 0) return;

        bool changed;
        var now = DateTimeOffset.UtcNow;
        QuoteSide Moved(decimal price, decimal size) => new(price, size, now, DateTimeKind.Utc, QuoteSource.SecurityMove);
        lock (_gate)
            changed = Merge(key, Moved(s.BestBidPrice, s.BestBidVolume), Moved(s.BestAskPrice, s.BestAskVolume),
                Moved(s.LastTradePrice ?? 0m, 0m));

        if (changed) EmitQuote(key);
    }

    static QuoteSide ReadSide(decimal price, decimal size) =>
        new(price, size, DateTimeOffset.MinValue, DateTimeKind.Unspecified, QuoteSource.SecurityRead);

    /// <summary>Publishes the composed quote for one symbol. Never called holding
    /// <see cref="_gate"/>: QuoteChanged runs arbitrary subscriber code.</summary>
    void EmitQuote(string key)
    {
        var s = FindSecurity(key);
        if (s is null) return;
        QuoteInfo quote;
        lock (_gate) quote = Compose(s, key).Quote;
        QuoteChanged?.Invoke(quote);
    }

    /// <summary>
    /// Folds freshly read sides into the book. Caller holds <see cref="_gate"/>.
    ///
    /// Returns whether a PRICE changed — which is what QuoteChanged is for — and NOT whether
    /// anything was written, because a tick repeating the same price still refreshes how old that
    /// price is. Those are two different questions and conflating them either floods the pipe with
    /// every tick or freezes the age of a quiet-but-live book.
    /// </summary>
    bool Merge(string key, QuoteSide bid, QuoteSide ask, QuoteSide last)
    {
        // out var, not a conditional expression: the dictionary's value type carries the element
        // NAMES, and a bare tuple literal in the other branch would erase them.
        if (!_quotes.TryGetValue(key, out var book)) book = (QuoteSide.None, QuoteSide.None, QuoteSide.None);
        var changed = false;
        book.Bid = Fold(book.Bid, bid, ref changed);
        book.Ask = Fold(book.Ask, ask, ref changed);
        book.Last = Fold(book.Last, last, ref changed);
        _quotes[key] = book;
        return changed;
    }

    /// <summary>
    /// One side, in or out. Three rules, and each of them names a way this has gone wrong:
    ///
    ///   * A non-positive price is "not reported", never "the price is zero". The chart's Security
    ///     object reports BestBidPrice = 0 on this platform because it is never fed level-1 data at
    ///     all; letting that overwrite a real bid would erase the only price there is, and the
    ///     gateway would then be sizing an order off nothing.
    ///   * A weaker surface never displaces a stronger one on the same side. The Security surface
    ///     and a market-data tick can disagree, and the one measured to be empty must not win.
    ///   * A repeat of the same price refreshes the timestamp only when it arrived as market data.
    ///     A market-data event IS fresh evidence that the price is still true. A Security
    ///     PropertyChanged for some unrelated field is not, and treating it as one is exactly the
    ///     manufactured freshness the whole dictionary exists to prevent.
    ///
    /// A consequence worth stating out loud: if the strongest source stops, the side it filled is
    /// PINNED and simply ages out, rather than quietly falling back to a weaker surface that may be
    /// reporting something else. IsStale then refuses the quote and the quote= token shows
    /// event(...,age=<large>) — a stopped feed, named as one. Silently substituting a different
    /// surface's number under the same symbol is the failure this ordering exists to prevent.
    /// </summary>
    static QuoteSide Fold(QuoteSide old, QuoteSide fresh, ref bool changed)
    {
        if (!fresh.HasPrice) return old;
        if (fresh.Source < old.Source) return old;
        if (old.HasPrice && old.Price == fresh.Price)
            return fresh.Source == QuoteSource.MarketData ? fresh : old;
        changed = true;
        return fresh;
    }

    /// <summary>
    /// Assembles the answer to "what is the price right now" out of the strongest source that has
    /// one, and hands back the side it priced off so the caller can say WHICH — a quote nobody can
    /// trace is a quote nobody can diagnose. Caller holds <see cref="_gate"/>.
    ///
    /// Sources, strongest first:
    ///
    ///   1. the book, fed by market-data ticks and by observed Security moves;
    ///   2. ChartStrategy.BestBid / BestAsk, read on demand — dump: `MarketDataArg BestAsk
    ///      { get; set; }`, `MarketDataArg BestBid { get; set; }` on
    ///      ATAS.Strategies.Chart.ChartStrategy. They carry a real MarketDataArg.Time, which is what
    ///      makes reading them honest: the quote gets the time the price was true, not the time we
    ///      looked. They describe THIS CHART's instrument and no other, so they are applied only to
    ///      the chart's own symbol — attributing them to a connector-supplied symbol would price an
    ///      order off a different instrument's book;
    ///   3. Security.BestBidPrice / BestAskPrice. Last resort, and the dump gives Security NO time
    ///      field anywhere, so a price from here CANNOT be shown to be current: At stays unset,
    ///      IsStale refuses it, and the probe prints the MinValue and says in as many words that
    ///      nothing proves the feed is live. It is kept rather than deleted because it was the only
    ///      source this file ever had, so a host where it IS fed must not be made worse — and a
    ///      number the operator can see beats a null they cannot.
    ///
    /// ONE SCALAR HAS TO STAND FOR THE WHOLE QUOTE, and it is the time of the side the price is
    /// taken from: the bid, falling back to the ask and then to the last trade. The bid is what a
    /// resting order is priced off, so the freshness claim tracks exactly the number that carries
    /// the risk. Taking the newest of all three instead would let a bid that stopped updating an
    /// hour ago ride on an ask that ticked a second ago.
    /// </summary>
    (QuoteInfo Quote, QuoteSide Priced) Compose(AtasSecurity s, string key)
    {
        // out var, not a conditional expression: the dictionary's value type carries the element
        // NAMES, and a bare tuple literal in the other branch would erase them.
        if (!_quotes.TryGetValue(key, out var book)) book = (QuoteSide.None, QuoteSide.None, QuoteSide.None);
        var ignored = false;

        // The chart's two properties, for the chart's own instrument only.
        if (key.Length > 0 && string.Equals(key, SymbolOf(BoundSecurity), StringComparison.OrdinalIgnoreCase))
        {
            book.Bid = Fold(book.Bid, ChartSide(BestBid), ref ignored);
            book.Ask = Fold(book.Ask, ChartSide(BestAsk), ref ignored);
        }

        // Re-read rather than trusting the seed: Track() seeds once, and the Security may have been
        // updated since without a PropertyChanged ever reaching us.
        book.Bid = Fold(book.Bid, ReadSide(s.BestBidPrice, s.BestBidVolume), ref ignored);
        book.Ask = Fold(book.Ask, ReadSide(s.BestAskPrice, s.BestAskVolume), ref ignored);
        book.Last = Fold(book.Last, ReadSide(s.LastTradePrice ?? 0m, 0m), ref ignored);

        var priced = book.Bid.HasPrice ? book.Bid : book.Ask.HasPrice ? book.Ask : book.Last;

        // A price stamped in the future is not a price with a timestamp, it is a price whose
        // timestamp cannot be read — a wrong DateTimeKind assumption, or a feed clock nobody owns.
        // Passing it through would be the one genuinely unsafe outcome available here: UtcNow - At
        // goes negative, IsStale returns false for a quote of ANY age, and the gateway would price
        // off a book that stopped updating hours ago. So At is unset instead and IsStale refuses it.
        // The slack is generous on purpose: a feed clock a few seconds ahead of ours is normal, and
        // every way of getting the Kind wrong is out by whole timezone-sized amounts.
        var at = priced.HasPrice ? priced.At : DateTimeOffset.MinValue;
        if (at > DateTimeOffset.UtcNow + FutureQuoteSlack) at = DateTimeOffset.MinValue;

        var quote = new QuoteInfo(
            key,
            book.Bid.HasPrice ? book.Bid.Price : null,
            book.Ask.HasPrice ? book.Ask.Price : null,
            book.Last.HasPrice ? book.Last.Price : null,
            book.Bid.HasPrice && book.Bid.Size > 0m ? book.Bid.Size : null,
            book.Ask.HasPrice && book.Ask.Size > 0m ? book.Ask.Size : null,
            at);
        return (quote, priced);
    }

    /// <inheritdoc cref="Compose"/>
    static readonly TimeSpan FutureQuoteSlack = TimeSpan.FromSeconds(60);

    /// <summary>ChartStrategy.BestBid / BestAsk are typed as a reference and ATAS sets them when it
    /// has data, so an unset one is null and must read as "no side" rather than as a zero price.</summary>
    static QuoteSide ChartSide(AtasMarketData? a) =>
        a is null ? QuoteSide.None : QuoteSide.From(a.Price, a.Volume, a.Time, QuoteSource.ChartProp);

    /// <summary>
    /// Where the price came from and how old it is, as one whitespace-free token in the surface
    /// report. Without it a refusal to place reads as "no bid" and says nothing about WHICH of four
    /// surfaces was empty — which is another live run spent finding out.
    ///
    ///   quote=event(bid=7751.25,ask=7751.50,age=902s,kind=utc)
    ///       A BestBidAskChanged tick fed it. This is the working state. age≈900s is the dxFeed
    ///       15-minute delay on this machine and is CORRECT; see ToQuoteTime for what age and kind
    ///       together say about the DateTimeKind assumption. Nothing to do. (Prices print at the
    ///       decimal scale the feed sent, so 7751.50 rather than 7751.5.)
    ///       A NEGATIVE age here means the quote is stamped in the future: the price is real, the
    ///       clock reading is not, Compose has already unset the quote's At so nothing prices off
    ///       it, and ToQuoteTime is the function to change.
    ///   quote=event(bid=none,ask=none,age=0s,kind=utc)
    ///       NewTrades is arriving and BestBidAskChanged is not. The tape is live and the book is
    ///       not being delivered — a placement still cannot be priced, and the thing to check is the
    ///       chart's level-1 subscription, not this file.
    ///   quote=chartprop(...)
    ///       No tick has arrived yet, so ChartStrategy.BestBid / BestAsk answered instead. The price
    ///       is real and timestamped, so a placement can proceed — but the event path has not been
    ///       shown to work, and a growing age= here means it is not going to.
    ///   quote=security(...)
    ///       Security.PropertyChanged was watched moving. age is measured from OUR clock, not the
    ///       feed's, so it cannot be compared with the 15-minute delay.
    ///   quote=secprop(bid=...,ask=...,age=none,kind=none)
    ///       Only Security.BestBidPrice answered, and it carries no timestamp anywhere in the dump.
    ///       The price may be perfectly good; nothing here can show that it is. IsStale refuses it.
    ///   quote=none(no-onlinedataprovider)
    ///       DataProvider.OnlineDataProvider is null, so no quote source was ever subscribed. The
    ///       strategy is not attached to a chart that has a data provider — re-add it and start it.
    ///   quote=none(no-tick)
    ///       Subscribed, and nothing has arrived and neither fallback holds a price. The chart is
    ///       not receiving data: check the connection and the instrument, not this file.
    ///   quote=none(no-security)
    ///       No bound instrument at all, so there is nothing to quote. GetInstruments is empty too.
    ///   quote=unreadable(Foo)
    ///       Reading the quote threw. Never lets the handshake fail with it.
    /// </summary>
    string QuoteToken()
    {
        try
        {
            if (BoundSecurity is not { } s) return "none(no-security)";

            QuoteInfo quote;
            QuoteSide priced;
            lock (_gate) (quote, priced) = Compose(s, SymbolOf(s));

            if (!priced.HasPrice)
                return DataProvider?.OnlineDataProvider is null ? "none(no-onlinedataprovider)" : "none(no-tick)";

            // Measured from what the FEED stamped, not from the At that Compose hands the gateway,
            // and signed rather than clipped at zero. Those are the same number until the clock
            // reading is wrong, and when it is, this is the only place the error is still visible:
            // Compose has unset the quote's At by then, so age= here would read "none" and say
            // nothing about why.
            var unset = priced.At == DateTimeOffset.MinValue;
            var age = unset ? "none" : (DateTimeOffset.UtcNow - priced.At).TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "s";
            var kind = unset ? "none" : priced.Kind.ToString().ToLowerInvariant();

            return $"{SourceToken(priced.Source)}(bid={PriceToken(quote.Bid)},ask={PriceToken(quote.Ask)},age={age},kind={kind})";
        }
        catch (Exception ex)
        {
            return $"unreadable({ex.GetType().Name})";
        }
    }

    /// <inheritdoc cref="QuoteToken"/>
    static string SourceToken(QuoteSource source) => source switch
    {
        QuoteSource.MarketData => "event",
        QuoteSource.SecurityMove => "security",
        QuoteSource.ChartProp => "chartprop",
        QuoteSource.SecurityRead => "secprop",
        _ => "none"
    };

    /// <summary>Invariant culture, because a comma decimal separator would split one token into two
    /// in a report whose only structure is that it is space-separated.</summary>
    static string PriceToken(decimal? value) => value is { } v ? v.ToString(CultureInfo.InvariantCulture) : "none";

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
            // An unknown fill is reported as NO fill — see FilledOf for why that is the safe
            // direction and the other one costs money.
            filled ?? 0m,
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
    /// the work — and where the fill is not KNOWN it refuses to do that work instead of guessing.
    ///
    /// OrderStates.None on an order that was never active means "submitted, no word yet"; on one that
    /// HAS been active it means the state is genuinely unknown, which is the state that sends the
    /// gateway to reconcile instead of guessing.
    ///
    /// Done with NO evidence either way is the case that used to read FILLED on an order that had
    /// never traded. It is UNKNOWN now, and deliberately not CANCELLED: "this order finished and the
    /// platform never said what happened to it" is not a licence to assert a fill, and not a licence
    /// to assert a cancellation either. UNKNOWN is the one answer that routes it to reconciliation,
    /// which is where an order nobody can account for belongs. FILLED is now unreachable without
    /// trades that were actually observed.
    /// </summary>
    static ExecutionState MapState(AtasOrder o, decimal quantity, decimal? filled) => o.State switch
    {
        AtasOrderStates.Failed => ExecutionState.REJECTED,
        // A lifted comparison: an unknown fill is not > 0, so this reads WORKING, never
        // PARTIALLY_FILLED. A working order reported as partially filled is a position that does not
        // exist.
        AtasOrderStates.Active => filled > 0m ? ExecutionState.PARTIALLY_FILLED : ExecutionState.WORKING,
        AtasOrderStates.Done => filled is not { } done ? ExecutionState.UNKNOWN
            : quantity > 0m && done >= quantity ? ExecutionState.FILLED
            : ExecutionState.CANCELLED,
        _ => o.WasActive ? ExecutionState.UNKNOWN : ExecutionState.DISPATCHING
    };

    /// <summary>
    /// How much of this order the platform can be SHOWN to have filled — or null, meaning it has not
    /// said. Two sources of positive evidence, and nothing else counts as any:
    ///
    ///   * MyTrades. The sum of an order's own trades is what was filled, by definition.
    ///   * A STRICTLY POSITIVE Order.Unfilled, which ATAS must have written: decimal defaults to 0,
    ///     so a positive value cannot be an untouched field. Note it also cannot on its own produce a
    ///     full fill, because the remainder it implies is always strictly less than the quantity.
    ///
    /// WHAT THIS USED TO DO, AND IT WOULD HAVE FIRED ON THE VERY FIRST ORDER: Place constructs the
    /// Order and never sets Unfilled, so Unfilled is 0. With no trades yet, "quantity - Unfilled" is
    /// the WHOLE quantity — so a resting order that had never traded reported itself fully filled.
    /// MapState then read PARTIALLY_FILLED while it worked and FILLED the moment ATAS marked it Done.
    /// The reading was inferring a fill from the ABSENCE of information: Unfilled == 0 means both
    /// "everything filled" and "nobody ever set this field", and the code picked the money-losing one.
    ///
    /// WHICH READING IS CHOSEN WHEN THE PLATFORM IS SILENT: no fill, and unknown rather than zero
    /// where the caller can tell the difference. An unknown fill read as NO fill leaves the gateway
    /// believing it still has a working order and an unsettled outcome, so it keeps watching, keeps
    /// the order in the reconciliation set, and can correct itself on the next read. Read as a FULL
    /// fill it believes it holds a position it does not hold, stops watching an order that is still
    /// live at the broker, and sizes the next order off both — and nothing later contradicts it,
    /// because a filled order is terminal. One error is self-correcting; the other is permanent.
    /// </summary>
    decimal? FilledOf(AtasOrder o, decimal quantity, IReadOnlyDictionary<string, decimal>? fills)
    {
        var key = OrderKey(o);
        decimal traded = 0m;
        // A keyless order matches no trade. Matching on an empty key would credit this order with
        // another order's fills, which is the same mistake pointing the other way.
        if (key.Length > 0)
        {
            if (fills is not null) fills.TryGetValue(key, out traded);
            else
            {
                var counted = new HashSet<string>(StringComparer.Ordinal);
                foreach (var t in LiveTrades())
                {
                    if (TradeKey(t) != key) continue;
                    // LiveTrades may read overlapping collections; a trade counted twice would report
                    // a fill larger than the order.
                    if (t.Id is { Length: > 0 } id && !counted.Add(id)) continue;
                    traded += t.Volume;
                }
            }
        }
        if (traded > 0m) return quantity > 0m ? Math.Min(traded, quantity) : traded;

        // ATAS demonstrably wrote this one: positive, and no larger than the order it belongs to.
        // Unfilled == quantity is a real reading too — "nothing has filled" — and it is what makes a
        // genuine cancellation legible as CANCELLED rather than UNKNOWN.
        if (o.Unfilled > 0m && o.Unfilled <= quantity) return quantity - o.Unfilled;

        // Zero, negative, or larger than the order. Indistinguishable from a field nobody set, so the
        // honest answer is that this adapter does not know.
        return null;
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
    /// ITradingManager.Orders and ChartStrategy.Orders are NOT the same collection — measured
    /// 2026-08-30, and the reason to read both is therefore still the original one: an order missed
    /// by reading only one of them would be an order hidden from reconciliation. With one resting
    /// order live, three probe runs reported `orders=1 strategyorders=0` from a single instant
    /// inside SurfaceReport, and a shared list cannot report two lengths at once.
    ///
    /// WHAT IS STILL OPEN is narrower and does not change this method: whether an order placed by
    /// THIS strategy instance in THIS session ever appears in ChartStrategy.Orders at all. Every
    /// surface reading ever captured was taken at the hello, before anything was placed, so
    /// `strategyorders=0` has never been observed in the one situation that would give it meaning.
    /// The probe now takes the reading again after the place, so the next run on hardware settles it.
    ///
    /// EACH ENTITY IS YIELDED ONCE, and that guard stays whatever the second reading says. It is
    /// defensive rather than load-bearing on the evidence above, but it costs one HashSet and what
    /// it prevents is a caller that SUMS per order — FilledOf adding up my-trade volumes —
    /// reporting a fill of twice the real size, which reads as FILLED on a half-filled order.
    /// Neither Order nor MyTrade overrides Equals in the dump, so the set is reference identity,
    /// which is exactly the question being asked: is this the same object arriving again.
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

        // A handle OrderKey minted before ATAS had identified the order carries our client order id
        // inside it. The moment ATAS assigns an Id, that handle stops matching OrderKey — and the
        // order it names is exactly the one a cancel issued seconds after a placement is aiming at,
        // which is the cancel that matters most. So the id is recovered from the handle and looked up
        // as the client order id it is. Still a second pass: identity across the whole book first.
        var clientId = connectorOrderId.StartsWith(NoIdPrefix, StringComparison.Ordinal)
            ? connectorOrderId[NoIdPrefix.Length..]
            : connectorOrderId;

        foreach (var o in LiveOrders()) if (HasClientId(o, clientId)) return o;

        lock (_gate) return _submitted.TryGetValue(clientId, out var mine) ? mine : null;
    }

    static bool IsSameOrder(AtasOrder o, string id) =>
        (OrderKey(o) is { Length: > 0 } key && string.Equals(key, id, StringComparison.Ordinal))
        || (!string.IsNullOrEmpty(o.Id) && string.Equals(o.Id, id, StringComparison.Ordinal))
        // ExtId 0 is the default of an Int64 nobody assigned, so it identifies nothing. Matching it
        // against the literal "0" would hand back the first unidentified order in the book — and
        // this function decides which order a cancel lands on.
        || (o.ExtId != 0L && string.Equals(o.ExtId.ToString(CultureInfo.InvariantCulture), id, StringComparison.Ordinal));

    static bool HasClientId(AtasOrder o, string id) =>
        !string.IsNullOrEmpty(o.Comment) && string.Equals(o.Comment, id, StringComparison.Ordinal);

    /// <summary>
    /// The handle this adapter hands out for an order: reported as OrderInfo.ConnectorOrderId, and
    /// the string a later cancel or modify arrives holding.
    ///
    /// IT IS NOT ALWAYS AN IDENTITY, AND IT USED TO PRETEND OTHERWISE. It returned "ext:{ExtId}"
    /// whenever Order.Id was empty — and a freshly constructed order has Id = null and ExtId = 0, so
    /// EVERY order ATAS had not identified yet came back as the same string, "ext:0". One handle
    /// shared across unrelated orders is how a refusal recorded against order A is read back as a
    /// refusal of order B, and how a cancel aimed at one order lands on another.
    ///
    /// The chain now ends in something that is either unique or empty:
    ///
    ///   Order.Id            the broker's own id, once it exists. The only broker-assigned answer.
    ///   ext:{ExtId}         ATAS's platform id where it is set. Synthetic — and the "ext:" prefix is
    ///                       what every reader downstream keys on to know it is not broker-assigned.
    ///   ext:none/{Comment}  no platform id at all, but the order carries OUR client order id: unique
    ///                       per order, and resolvable again by FindOrder. Still prefixed "ext:",
    ///                       because a synthetic handle that stopped saying so would be read as a
    ///                       broker id — including by the probe's rule-1 verdict.
    ///   ""                  nothing identifies this order. An empty handle is the truthful answer; a
    ///                       shared one is a wrong answer that looks usable.
    /// </summary>
    static string OrderKey(AtasOrder o) =>
        !string.IsNullOrEmpty(o.Id) ? o.Id
        : o.ExtId != 0L ? ExtKey(o.ExtId)
        : !string.IsNullOrEmpty(o.Comment) ? NoIdPrefix + o.Comment
        : "";

    /// <summary>Prefix of a handle for an order ATAS has not identified at all. FindOrder strips it
    /// back off; the "ext:" it starts with is what tells every reader downstream — the probe's rule-1
    /// verdict included — that this is not a broker-assigned id.</summary>
    const string NoIdPrefix = "ext:none/";

    static string ExtKey(long extId) => $"ext:{extId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Every key a failure for this order could have been filed under, in ONE place, because
    /// <see cref="Lookup"/> and the pre-flight clear in Place, Modify and CancelCore have to walk the
    /// same list. When they did not, one OrderCancelFailed recorded under the client id outlived a
    /// clear that only removed OrderKey, and poisoned every later modify and cancel of that order:
    /// each was submitted to the broker and then reported as a definite refusal. Rule 3.
    ///
    /// ONLY IDENTITIES APPEAR HERE. A key that cannot tell one order from another is not a key, so
    /// the "ext:0" that every unidentified order used to share is gone, and an order with nothing
    /// identifying it at all yields nothing — a failure that cannot be attributed is dropped rather
    /// than pinned on whichever order comes next.
    /// </summary>
    static IEnumerable<string> FailureKeys(AtasOrder o)
    {
        if (!string.IsNullOrEmpty(o.Id)) yield return o.Id;
        if (o.ExtId != 0L) yield return ExtKey(o.ExtId);
        // Ours, and unique per order. This is the key that lets a failure arriving BEFORE ATAS has
        // assigned any id still be attributed to the right order.
        if (!string.IsNullOrEmpty(o.Comment)) yield return o.Comment;
    }

    static string SymbolOf(AtasSecurity? s) =>
        s is null ? "" : !string.IsNullOrWhiteSpace(s.Code) ? s.Code : s.SecurityId ?? "";

    static bool SymbolMatches(AtasSecurity? s, string? securityId, string symbol) =>
        (s is not null && (string.Equals(s.Code, symbol, StringComparison.OrdinalIgnoreCase)
                           || string.Equals(s.SecurityId, symbol, StringComparison.OrdinalIgnoreCase)
                           || string.Equals(s.Instrument, symbol, StringComparison.OrdinalIgnoreCase)))
        || string.Equals(securityId, symbol, StringComparison.OrdinalIgnoreCase);

    static bool AccountMatches(string? candidate, string wanted) =>
        string.IsNullOrWhiteSpace(wanted) || string.Equals(candidate, wanted, StringComparison.OrdinalIgnoreCase);

    /// <summary>The caller holds the lock. Walks <see cref="FailureKeys"/> and nothing else, so that
    /// what can be found is exactly what <see cref="ClearFailures"/> removes.</summary>
    string? Lookup(AtasOrder o)
    {
        foreach (var key in FailureKeys(o))
            if (_failures.TryGetValue(key, out var reason)) return reason;
        return null;
    }

    /// <inheritdoc cref="FailureKeys"/>
    void ClearFailures(AtasOrder o)
    {
        foreach (var key in FailureKeys(o)) _failures.Remove(key);
    }

    string? Failure(string key, AtasOrder o)
    {
        // An empty key is never stored and must never be looked up: it would be asking the failure
        // map a question that identifies no order.
        lock (_gate) return key.Length > 0 && _failures.TryGetValue(key, out var direct) ? direct : Lookup(o);
    }

    /// <summary>
    /// This bridge is expected to stay loaded for weeks, so the side tables cannot grow forever.
    /// The first two are caches over data whose real home is ATAS's own order collection, so dropping
    /// them wholesale costs a reject reason on very old orders and nothing else — the caller holds
    /// the lock. Deliberately not a leak the user discovers as a slow memory climb months from now.
    ///
    /// THE THIRD IS NOT A CACHE AND DOES NOT DROP QUIETLY. <see cref="_touched"/> is what stops the
    /// adapter's own order objects being read back as ATAS's, so an entry silently forgotten would
    /// not cost a diagnostic — it would let the next read-back record Distinct against an object we
    /// built, which is rule 1 faked. <see cref="AdapterTouchedOrders.Trim"/> therefore records the
    /// drop and refuses every proof from then on rather than inventing one; the full reasoning, and
    /// what the permanence costs, is on that method.
    /// </summary>
    void Trim()
    {
        const int cap = 4096;
        if (_submitted.Count > cap) _submitted.Clear();
        if (_failures.Count > cap) _failures.Clear();
        _touched.Trim(cap);
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
    ///
    /// AND THERE IS A WAY FOR ALL OF THAT TO BE TRUE AND PROVE NOTHING. Place hands ATAS the Order
    /// INSTANCE it constructed and set Comment on. If ATAS's collection just holds that instance,
    /// this loop finds our own object, reads our own field off it, and matches by construction —
    /// every guard above passes and the only fact established is that ATAS assigned Order.Id. The
    /// identifier would not have been shown to survive anything.
    ///
    /// This cannot be settled from here, so it is MEASURED instead: the match records whether the
    /// order it matched is an object THIS ADAPTER TOUCHED, and Describe reports it as
    /// coid=proven-sameref (vacuous) or coid=proven-distinct (a real round trip). A whole pass is
    /// scanned rather than stopping at the first hit, so that a distinct match anywhere in the book
    /// is not missed because one of our own objects happened to be enumerated first.
    ///
    /// "TOUCHED" IS WIDER THAN "SUBMITTED", AND HAS TO BE. The test was once reference-equality
    /// against <c>_submitted[clientOrderId]</c>, which is only the instance Place built. The adapter
    /// produces two other order objects carrying our identifier — Modify's <c>Clone()</c>, which
    /// copies Comment, and the order ClosePosition labels by hand — and each of them is a DIFFERENT
    /// object holding OUR id, so the narrow test would have called either one Distinct and flipped
    /// the capability true on a round trip the adapter performed against itself. See
    /// <see cref="_touched"/> and <see cref="AdapterTouchedOrders"/>; the rule is stated there,
    /// where a test on any machine can reach it.
    ///
    /// THE LIVE READING HAS BEEN TAKEN AND IT WAS SAMEREF, so SupportsClientOrderId reads the
    /// distinction rather than the match. See ClientOrderIdProofs.ProvesRoundTrip for the decision,
    /// and the latch note in the body for why "we have an answer" is not the same condition as
    /// "stop looking".
    ///
    /// THERE ARE NOW TWO ROUTES IN, AND THE SECOND IS THE ONE THAT CAN SETTLE RULE 1.
    ///
    ///   * IN-SESSION. The id is in <see cref="_submitted"/>; the reading is SameRef or Distinct
    ///     depending on whose object carried it back. Unchanged in every respect.
    ///   * CROSS-SESSION. The id is NOT in _submitted, and <see cref="CoidWitness"/> holds a record
    ///     written by an EARLIER process of this product, before that order existed, carrying the
    ///     broker order id that process saw ATAS assign. The reading is CrossSession, and it is the
    ///     only one obtainable after a restart — which is the only experiment that can read the
    ///     identifier off something that cannot be our own object, because our own objects do not
    ///     survive the process that made them.
    ///
    /// The second route is NOT the 2026-08-27 guard relaxed. The identifier must still be one this
    /// product submitted; the evidence for that may simply have been written down rather than
    /// remembered. And it demands more than the first: the order must ALSO carry the broker id in
    /// the record — the half this product did not choose — which is what stops a stray order that
    /// merely carries the same comment from standing in for the one that was submitted.
    ///
    /// WHAT A CROSS-SESSION MATCH STILL DOES NOT PROVE: that the identifier reached the BROKER. It
    /// cannot separate ATAS rebuilding the order from the broker's own answer on reconnect from
    /// ATAS rehydrating it out of its own local store. All three survive a restart and look
    /// identical from inside a chart strategy. See ClientOrderIdProof.CrossSession.
    /// </summary>
    void ProveClientOrderId(string clientOrderId)
    {
        if (string.IsNullOrEmpty(clientOrderId)) return;
        AtasOrder? mine;
        lock (_gate)
        {
            // THE LATCH, AND IT MEANS "NOTHING STRONGER CAN BE OBSERVED" — not "something was".
            //
            // It exists so the read-back stops rescanning ATAS's whole order book on every order
            // event once the answer is final, and it used to fire on ANY match. That was safe only
            // while any match set the capability. It is not safe now: a SameRef match is the reading
            // this platform actually produces, and if it latched, the scan would never run again and
            // a genuinely Distinct match arriving later in the same session — the exact reading the
            // product is waiting for — could never be observed. Worse, nothing would look wrong: the
            // diagnostic would go on truthfully reporting proven-sameref forever. Latching on the
            // vacuous reading is how the real proof becomes permanently unreachable in silence.
            //
            // So the latch follows the STRONGEST reading rather than the capability, and IsSettled
            // says which that is. THE TWO HAVE NOW PARTED COMPANY, which is what they were kept
            // separate for: Distinct reports the capability and does NOT settle the search, because
            // in a FRESH SESSION a Distinct reading is free. After ATAS restarts this adapter has
            // constructed no Order at all, so every match is reference-distinct by construction —
            // and if that latched, the cross-session reading the restart experiment exists to take
            // would be unreachable, in silence, behind a truthful proven-distinct. That is trap 30.
            //
            // The cost of not latching before CrossSession is one extra pass over the live order
            // book per order event that names an id we can account for. That is the same scan this
            // method already ran on every such event before any match at all, so it is not a new
            // kind of work — and it is the price of the proof staying reachable, which outranks it.
            if (_clientOrderIdProof.IsSettled()) return;

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
            _submitted.TryGetValue(clientOrderId, out mine);
        }

        // THE CROSS-SESSION BRANCH, AND IT IS NOT A RELAXATION OF THE GUARD ABOVE.
        //
        // The identifier must still be one this PRODUCT submitted. The only thing that changes is
        // WHERE that is established: in memory by this process, or on disk by an earlier one that
        // wrote the claim down before the order existed and is gone by the time it is read. An id
        // in neither place is refused exactly as it always was, so an order in ATAS's book carrying
        // somebody else's comment still proves nothing.
        //
        // It requires strictly MORE than the in-session path, not less — see the Id test in the
        // scan below. And it is reached only when the id is absent from _submitted, so an order
        // THIS session placed can never take it: for those, _submitted is the authority and the
        // in-session readings apply. That is what stops a fresh process reaching the strongest
        // reading in the product for an order it placed itself thirty seconds ago.
        CoidWitnessRecord? prior = null;
        if (mine is null)
        {
            prior = _witness.PriorSession(clientOrderId);
            if (prior is null) return;
        }

        // No trading surface means no collection to look in, so there is nothing to learn and this is
        // not a check. Counting it as one would turn "we never got to look" into "we looked and it
        // was not there" — the exact confusion the counter exists to remove.
        if (Trading is null) return;

        lock (_gate) _clientOrderIdChecks++;

        AtasOrder? match = null;
        // Starts true and only a genuinely untouched match can clear it, so every path that fails to
        // establish whose object this is leaves the reading vacuous. Refuse a proof, never invent one.
        var matchIsOurs = true;
        foreach (var o in LiveOrders())
        {
            if (!string.Equals(o.Comment, clientOrderId, StringComparison.Ordinal)) continue;
            if (string.IsNullOrEmpty(o.Id)) continue;

            // THE HALF WE DID NOT WRITE, and the reason the cross-session branch is stricter rather
            // than looser. The comment is a string this product chose, so an order carrying it is
            // satisfiable by anything that carries that text — a workspace-restored order, a hand
            // placement, a copied comment. The broker order id is not ours: the earlier session read
            // it off Order.Id after ATAS assigned it, and CoidWitness refuses to let any later
            // session write one in. Requiring the two together is what makes this "the order that
            // run submitted" rather than "an order with the same label on it".
            if (prior is not null && !string.Equals(o.Id, prior.BrokerOrderId, StringComparison.Ordinal)) continue;

            match = o;

            // WHOSE OBJECT IS THIS? The only question that separates a round trip from the adapter
            // reading its own field back off its own object.
            //
            // Both tests, and the second subsumes the first. _touched contains `mine` by
            // construction — Place registers it in the same lock that writes _submitted — so the
            // ReferenceEquals is a floor: if that registration is ever lost, what breaks is the
            // WIDENED guard, not the original one that has already been measured on real ATAS.
            //
            // Under the gate because _touched is written by Place and Modify on the pipe thread
            // while this loop runs on ATAS's. Asked only after the two cheap filters above, so it is
            // a handful of uncontended acquisitions per pass, not one per order in the book.
            bool ours;
            lock (_gate) ours = ReferenceEquals(o, mine) || !_touched.CountsAsEvidence(o);
            // An object we never touched carrying our identifier is the reading worth having, so
            // keep looking while every match so far is one of ours.
            if (!ours) { matchIsOurs = false; break; }
        }
        if (match is null) return;

        ClientOrderIdProof observed;
        if (prior is null) observed = ClientOrderIdProofs.Observed(matchIsOurs);
        else
        {
            // AN OBJECT THIS ADAPTER TOUCHED IS NEVER EVIDENCE, IN ANY SESSION — and this is not
            // belt-and-braces, there is a live path to it. Modify() clones the order it is replacing
            // and Clone() copies Comment, so a modify of an order left over from a PREVIOUS session
            // (which is exactly what reconciliation after a restart does) produces an object THIS
            // adapter constructed carrying a PRIOR session's identifier. Without this test that
            // clone would be the cross-session proof, and it would be the adapter proving rule 1
            // against its own object with extra steps.
            //
            // matchIsOurs starts true and only an untouched match clears it, so a _touched set that
            // has forgotten (AdapterTouchedOrders.Trim) refuses this reading too. Refusing a proof
            // is the direction to fail in; the restart experiment runs on a fresh session, where
            // that set is empty.
            if (matchIsOurs) return;
            observed = ClientOrderIdProof.CrossSession;
        }

        lock (_gate)
        {
            // Only ever strengthen. The latch above and this write are separate lock acquisitions
            // with a whole enumeration of ATAS's collection between them, and this method is called
            // from Place on the pipe thread AND from the order-event fan on ATAS's — so two passes
            // can both get past the latch, and without this the slower one could write SameRef over
            // a Distinct the faster one had just established, demoting a real proof to a vacuous one.
            if (observed.Supersedes(_clientOrderIdProof)) _clientOrderIdProof = observed;
        }
    }

    // ---------------------------------------------------------------- plumbing

    // The blocking-wait helper that used to live here is now AtasCall.Block, and the move is the
    // point rather than tidying. This file is <Compile Remove>d on every machine without ATAS, so
    // while the helper lived here no test on the dev Mac or in CI could reach it — and what it got
    // wrong (waiting with no deadline, which wedges the bridge's whole command loop while the
    // heartbeat goes on reporting READY) was therefore unreachable by any test that could have
    // caught it. AtasCall.cs sits outside #if ATAS_SDK; its doc comment carries the reasoning that
    // used to be here, including the correction to what it said about where refusals arrive.

    /// <summary>
    /// An order's state and whether ATAS has assigned it an Id, as one space-free token.
    ///
    /// Those two together are exactly what <c>Place</c>'s WaitFor treats as "acknowledgement has
    /// arrived", so they are what the timing token has to report at each instant it samples.
    /// </summary>
    static string OrderShape(AtasOrder order) =>
        $"{order.State}/{(string.IsNullOrEmpty(order.Id) ? "noid" : "id")}";

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

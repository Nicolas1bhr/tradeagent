using TradeAgent.ConnectorSdk;
using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.Gateway;

public sealed class GatewayOptions
{
    /// <summary>
    /// TEST SEAM. Off means "behave like a naive client": dispatch even when the request id was
    /// already seen. It exists so the fault harness can first PROVE it is able to detect duplicate
    /// submission, before asserting that the real path prevents it. Never ship it off.
    /// </summary>
    public bool IdempotencyEnabled { get; set; } = true;

    /// <summary>How old a price may be before it is refused as a basis for sizing an order.</summary>
    public TimeSpan MaxQuoteAge { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a parked LIVE_CONFIRM order stays approvable. An approval pressed after this is
    /// refused with APPROVAL_EXPIRED and the request is declined for good; the AI has to propose it
    /// again against the market as it is now. Fifteen minutes is a judgment, not a measurement:
    /// long enough for a person to walk back to the screen, short enough that the price the
    /// proposal was sized from is not history. Literal semantics — zero expires every approval;
    /// there is deliberately no "0 = off" here, unlike the notional cap.
    /// </summary>
    public TimeSpan ApprovalTtl { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The gateway's one source of time. Tests substitute a clock they can move, so a time-to-live
    /// can be proved on both sides of its boundary without sleeping through it.
    /// </summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>
    /// How long after a dispatch could last have been in flight we wait before "the broker has never
    /// heard of this order" is allowed to mean it never landed. Protects against reading a slow
    /// backend as an absent one.
    ///
    /// It is measured from the LATER of the dispatch and the stranded bound — see
    /// <c>TradingGateway.AbsenceCountsFrom</c>. Measured from the dispatch alone it was a no-op on
    /// exactly the records it exists to protect: a stranded record is only visible to the reconciler
    /// once it is older than the bound, and the bound is longer than this window, so the grace had
    /// always already expired by the time anything could ask (REVIEW 2026-09-05 finding 1).
    /// </summary>
    public TimeSpan AbsenceGrace { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// What a DISPATCH costs BEYOND its connector call: the settle write, and a continuation that
    /// has to be scheduled before it can make that write. Added ONCE on top of the connector's own
    /// worst case to give <c>TradingGateway.DispatchStrandedAfter</c> — the same shape as
    /// <c>GatewayPipeServer.HandlerOverhead</c>, and for the same reason: the connector's deadlines
    /// bound the CALL and describe nothing that happens after it returns.
    ///
    /// Twenty seconds is deliberate slack rather than a measurement, and it is also four passes of
    /// <see cref="HealthInterval"/> — long enough that the loop which notices has had several turns.
    /// It is settable because it is the term that does NOT describe the wire; the term that does
    /// (the connector's worst case) is read off the connector and no caller can shorten it.
    /// </summary>
    public TimeSpan DispatchSettleSlack { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// An EXPLICIT stranded bound, and null — the default — means "derive it".
    ///
    /// The derived value is the live connector's <c>WorstCaseOperationPath</c> plus
    /// <see cref="DispatchSettleSlack"/>; see <c>TradingGateway.DispatchStrandedAfter</c>. A value
    /// set here may only LENGTHEN that, exactly as <c>GatewayPipeServer.HandlerDrainTimeout</c>
    /// works: a caller naming a longer bound means it, and one naming a shorter bound is asking for
    /// an order still on the wire to be written off, which is not theirs to ask for.
    /// </summary>
    public TimeSpan? DispatchStrandedAfter { get; set; }

    public TimeSpan HealthInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// HOW OFTEN THE LOSS BUDGETS ARE MEASURED WHEN NO ORDER IS ARRIVING — the backbone of the
    /// watch, and the answer to "a bleeding book that nobody asks about is never measured".
    ///
    /// <para>Fifteen seconds: three passes of <see cref="HealthInterval"/>, which is the tick the
    /// watch rides on, and the longest gap this product is willing to leave between a position
    /// going through the owner's budget and the app knowing it. It is not a promise about the
    /// market — a quote can move a book through a budget and back inside one tick, and no interval
    /// short of a tick-by-tick subscription would see that. <c>QuoteChanged</c> is what makes it
    /// faster than this when the platform is talking: an arriving quote schedules one immediate
    /// evaluation, coalesced.</para>
    ///
    /// <para>Recorded in <c>docs/CONTRACTS.md</c> as a choice the account owner may overrule.</para>
    /// </summary>
    public TimeSpan LossWatchInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// HOW LONG A FIRST SIGHTING OF A BREACH STAYS STANDING WHILE IT WAITS FOR A SECOND, DISTINCT
    /// PULL TO AGREE WITH IT.
    ///
    /// <para>Sixty seconds, four ticks of <see cref="LossWatchInterval"/>. The window exists because
    /// the admission gate and the watch answer different questions: an order that arrives on a
    /// single bad print is refused on that print, which costs nothing and is undone by the next one
    /// — but CLOSING THE DAY on a single print is a decision nothing undoes until tomorrow. So a
    /// closure needs two agreeing pulls, and this bounds how long the first one counts for. Any pull
    /// that disagrees drops the sighting immediately, whatever the window says.</para>
    ///
    /// <para>Recorded in <c>docs/CONTRACTS.md</c> as a choice the account owner may overrule.</para>
    /// </summary>
    public TimeSpan LossBreachConfirmWithin { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Who is asking, and whether they are the person at the keyboard.
///
/// <see cref="IsOperator"/> is the difference between an order that parks for approval and one that
/// goes to the broker, and between the kill switch holding and the kill switch being ignored. It
/// used to be <c>SessionId == "operator"</c> — a STRING COMPARISON on a value that arrives over the
/// agent pipe: <see cref="GatewayPipeServer"/> built the context from <c>req.Session</c>, and `trade`
/// copies <c>TRADEAGENT_SESSION</c> into that field verbatim. `TRADEAGENT_SESSION=operator trade buy`
/// therefore placed a live order in LIVE_CONFIRM with nobody approving it, and traded through a
/// pressed kill switch. Measured over the real pipe on 2026-09-02, before the fix: state FILLED,
/// connector order FB-1, mode LIVE_CONFIRM.
///
/// A CLASS, NOT A RECORD, AND THAT IS THE FIX FOR THE SECOND HOLE. As a record it kept its own copy
/// constructor, so <c>AgentContext.Operator with { SessionId = "x" }</c> produced a NEW operator
/// context with someone else's name on it — while the comment here claimed no public route could.
/// A class has no <c>with</c>, so the claim and the code now agree. Equality is not implemented
/// because nothing compares these for authority and an accidental value-equality check on a
/// security type is a trap, not a convenience.
///
/// <see cref="Operator"/> is the only operator context that exists, made once by a constructor
/// nothing else can reach. The reserved word is ALSO refused at the pipe, but that refusal is a
/// tripwire, not the defence: this type is the defence.
/// </summary>
public sealed class AgentContext
{
    /// <summary>The session name the operator's own context carries. Reserved on the wire.</summary>
    public const string OperatorSessionId = "operator";

    /// <summary>The one and only operator context. In-process callers pass this; nothing can forge it.</summary>
    public static readonly AgentContext Operator = new(OperatorSessionId, isOperator: true);

    /// <summary>An ordinary caller. Cannot be an operator, whatever the session is called.</summary>
    public AgentContext(string sessionId) : this(sessionId, isOperator: false) { }

    AgentContext(string sessionId, bool isOperator, string? role = null, string? attemptId = null)
    {
        SessionId = sessionId;
        IsOperator = isOperator;
        Role = role;
        AttemptId = attemptId;
    }

    public string SessionId { get; }
    public bool IsOperator { get; }

    /// <summary>
    /// THE COUNCIL ROLE THIS CALLER PROVED IT IS, or null because it proved nothing.
    ///
    /// Null is not "the chair" and it is not a default to be filled in downstream. Everywhere else
    /// in this product a row with no role reads as Operations (<see cref="CouncilRoles.Or"/>),
    /// because rows written before the council existed are the chair's — and applying that reading
    /// HERE was the defect: a caller holding nothing but the machine token, which every agent process
    /// can read, was served the chair's authority. A caller says which role it is by presenting the
    /// launch grant the app minted for that role's process, or it is nobody.
    /// </summary>
    public string? Role { get; }

    /// <summary>The AI attempt this caller's launch was opened under, when it proved one.</summary>
    public string? AttemptId { get; }

    /// <summary>
    /// Whether this caller may place, change or cancel an order.
    ///
    /// The operator always may — that is the owner at the keyboard, in-process, and nothing on the
    /// pipe can forge it. Otherwise it is the ROLE's answer, and a caller with no role has none:
    /// Research submits hypotheses and reads, and the doctrine gives it no order permission
    /// (<c>docs/COUNCIL.md</c>).
    /// </summary>
    public bool MayPlaceOrders => IsOperator || CouncilRoles.MayPlaceOrders(Role);

    /// <summary>
    /// The context for a caller on the other side of the fence, named by whatever session string it
    /// sent and by the role it PROVED. The only factory the pipe server uses, and it cannot return
    /// an operator.
    /// </summary>
    public static AgentContext ForAgent(string? sessionId, string? role = null, string? attemptId = null) =>
        new(string.IsNullOrWhiteSpace(sessionId) ? "agent" : sessionId!, isOperator: false, role, attemptId);

    public override string ToString() =>
        IsOperator ? "operator (in-process)" : Role is { Length: > 0 } r ? $"{SessionId} ({r})" : SessionId;
}

/// <summary>
/// What the caller asked for, before it becomes a <see cref="PlaceOrderCommand"/>.
///
/// <see cref="Intent"/> is init-only with a default so that adding it did not silently re-parameterise
/// every construction site, and so that a caller who does not know cannot claim the fast path. It is
/// PERSISTED in <c>ParametersJson</c> and read back when a parked order is approved, which is what
/// keeps a close that waited for a person a close when it is finally dispatched.
/// </summary>
public sealed record PlaceIntent(string Symbol, OrderSide Side, OrderType Type, decimal Quantity,
    decimal? LimitPrice, decimal? StopPrice, TimeInForce Tif, string? Comment)
{
    /// <summary>Why the order is being placed. See <see cref="OrderIntent"/>.</summary>
    public OrderIntent Intent { get; init; } = OrderIntent.Open;

    /// <summary>
    /// WHEN THIS ORDER WAS DECIDED AND UNDER WHAT BOUNDS — <see cref="IntentDecision"/> — or null
    /// because no strategy decided it.
    ///
    /// <para>Null is a real state and not an omission: the owner's own buy, a close, a leg of the
    /// emergency press and a modification are all orders with no closed bar behind them, and there is
    /// nothing there for a freshness gate to be about. What null must never mean is "a strategy's
    /// intent that lost its stamp on the way", which is why <see cref="IntentDecision"/> is
    /// all-or-nothing and why <c>IntentDecision.From</c> is the only thing that builds one.</para>
    ///
    /// <para>Init-only with a default, like <see cref="Intent"/>, and PERSISTED in
    /// <c>ParametersJson</c> — read back when a parked order is approved, so an order that waited for
    /// a person is still measured against the bar it was decided on rather than the moment somebody
    /// pressed the button.</para>
    /// </summary>
    public IntentDecision? Decision { get; init; }

    /// <summary>
    /// WHICH PROMOTED STRATEGY VERSION IS PLACING THIS ORDER, or null because no strategy is.
    ///
    /// <para><c>docs/COUNCIL.md</c>:210-211 names "which strategy or allocation caused an operation"
    /// as one of the four things that cannot be recovered later. Before this field nothing on the
    /// order path said which version an order came from — <c>execution_request</c> recorded none — so
    /// a fill could never be attributed to the program that decided it, and the capital gate
    /// (<c>:14-15</c>) had nothing to be a gate ABOUT.</para>
    ///
    /// <para>Null is a real state and not an omission, exactly as on <see cref="Decision"/>: the
    /// owner's own buy, a close, a leg of the emergency press and every modification are orders no
    /// strategy version decided, and there is no allocation for them to be charged against. What it
    /// must never mean is "a version's order that lost its name on the way", which is why the id
    /// travels on the intent from the caller that decided it rather than being looked up later.</para>
    ///
    /// <para>It is a CLAIM and not a permission. Naming a version grants nothing: the gateway asks
    /// the promotion ledger and the allocation ledger what that version actually stands on, at
    /// dispatch, and refuses when the answer is nothing (<c>TradingGateway</c>'s allocation gate).</para>
    /// </summary>
    public string? StrategyVersionId { get; init; }
}

/// <summary>
/// THE CLOSED BAR AN ORDER WAS DECIDED FROM, AND THE TWO BOUNDS ITS PROGRAM DECLARED.
///
/// <para><c>docs/COUNCIL.md</c>:96-97, verbatim: "A promoted strategy declares its timeframe, its
/// required data freshness and its maximum decision age, and the runner checks them again when the
/// intent reaches execution". This record is what makes that possible to check at all: before it,
/// the only age the money path knew was a QUOTE's — 30 seconds of it,
/// <see cref="GatewayOptions.MaxQuoteAge"/> — and nothing anywhere said how old the BAR behind an
/// order was, so :33's "never a late trade" had no implementation.</para>
///
/// <para><b>All four, or nothing.</b> Every field is required, and <see cref="From"/> answers null
/// for an intent whose program declared no bounds. A block carrying times and no bounds would be a
/// decision the dispatcher can see and cannot judge, which reads as a gate and is not one.</para>
///
/// <para><b><paramref name="BarClose"/> is the decision instant</b>, not <paramref name="BarOpen"/>.
/// The signal was computed from the bar's CLOSE, so that is when the decision existed and the
/// earliest instant it may be acted on — <c>StrategyIntent.NotBefore</c>. Measuring the age from the
/// open instead would read a one-minute intent as a whole bar older than it is, and on a program
/// whose <c>max_decision_age</c> is under one timeframe it would refuse every order the moment it
/// was made. <paramref name="BarOpen"/> is carried beside it because a reader settling "which bar
/// was this" needs the bar and not only its edge.</para>
/// </summary>
public sealed record IntentDecision(
    DateTimeOffset BarOpen,
    DateTimeOffset BarClose,
    TimeSpan DataFreshness,
    TimeSpan MaxDecisionAge)
{
    /// <summary>
    /// THE ONE WAY A DECISION BLOCK IS BUILT — off an intent the evaluator emitted, and off nothing
    /// else. Null when the program declared no bounds: there is then nothing to gate, and inventing
    /// one here would be the fake `CLAUDE.md` rule 1 forbids.
    /// </summary>
    public static IntentDecision? From(Core.Strategy.StrategyIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return intent.Freshness is { } bounds
            ? new IntentDecision(intent.Bar, intent.NotBefore, bounds.DataFreshness, bounds.MaxDecisionAge)
            : null;
    }

    /// <summary>How old the decision and the bars behind it are at <paramref name="now"/>.</summary>
    public TimeSpan AgeAt(DateTimeOffset now) => now - BarClose;
}

public sealed record GatewayStatus(
    string ProtocolVersion, string AppVersion, TradingMode Mode, bool AiTradingStopped, bool LiveActivated,
    bool ExecutionAvailable, string? ExecutionBlockedReason, string? ConnectorId, string? ConnectorName,
    bool ConnectorIsPaper, string? AccountId, IReadOnlyList<ComponentHealth> Health,
    int OpenRequests, int UnreconciledRequests, RiskPolicy Risk)
{
    /// <summary>
    /// What the AI's own loop is doing: <c>stopped</c>, <c>working</c>, <c>waiting</c> or
    /// <c>paused</c>. Init-only rather than a fourth positional parameter, because these three are
    /// composed by the app and every other construction site of this record means to say nothing
    /// about them.
    /// </summary>
    public string AiState { get; init; } = AiActivity.None.State;

    /// <summary>Turns the AI has taken since local midnight.</summary>
    public int AiTurnsToday { get; init; }

    /// <summary>
    /// What those turns cost, or ABSENT when TradeAgent cannot price them — which is the ordinary
    /// case for a CLI that reports tokens but no model, and for a machine with no <c>costs.json</c>.
    /// It is absent rather than zero on purpose: a zero here would tell the agent its work was free.
    /// </summary>
    public decimal? AiCostToday { get; init; }

    /// <summary>
    /// Present when <see cref="AiCostToday"/> is an UPPER BOUND rather than a bill: the CLI named no
    /// model, so the dearest model in its catalogue was charged, and this is the sentence saying so.
    ///
    /// It is on the wire for the same reason the figure is: the agent's mission is to cover what it
    /// costs, and an estimate it cannot tell from a measurement is one it will plan against wrongly.
    /// Absent means the figure was priced against a model something actually named.
    /// </summary>
    public string? AiCostEstimated { get; init; }

    /// <summary>
    /// WHAT THE TRADING HAS LOST TODAY, realised and unrealised, in the account's currency — or
    /// ABSENT when TradeAgent could not work it out, which is the same convention
    /// <see cref="AiCostToday"/> has and for a sharper version of the same reason.
    ///
    /// A zero here would tell the agent the day is flat. It is the figure its next order is about to
    /// be refused on, so a zero it could not distinguish from a real one is a plan made against a
    /// budget that has already been reached. Absent means unknown and never means "nothing lost".
    ///
    /// Absent also while no budget is set: nothing is measured then, and a figure nobody asked for
    /// is not computed (the rule <see cref="RiskPolicy.MaxNotionalPerOrder"/> has).
    /// </summary>
    public decimal? LossToday { get; init; }

    /// <summary>
    /// The most the day may lose before orders that could increase exposure are refused, or ABSENT
    /// when that budget is not enforced. Absent is "there is no limit here", which is the honest
    /// reading of a zero on <see cref="RiskPolicy.MaxDailyLoss"/> and is not a limit of nothing.
    /// </summary>
    public decimal? LossBudgetDay { get; init; }

    /// <summary>The most any one position may lose before it may be added to, or ABSENT when off.</summary>
    public decimal? LossBudgetTrade { get; init; }

    /// <summary>
    /// SINCE WHEN THE DAY HAS BEEN CLOSED TO NEW RISK, or ABSENT because it is open.
    ///
    /// <para>Off the durable record, never from <see cref="LossToday"/>: a day closed at a thousand
    /// down whose loser is then closed at nine hundred and fifty is under its budget again and is
    /// still closed. An agent that recomputed this would plan a session it is not going to be
    /// allowed to have and would read every refusal as the software being broken.</para>
    ///
    /// <para>It is not a kill switch and nothing was closed for the agent: closing and reducing work
    /// exactly as before, and the day reopens by itself at the next UTC midnight.</para>
    /// </summary>
    public DateTimeOffset? LossDayClosedAt { get; init; }

    /// <summary>
    /// The instruments closed to opens and adds for the rest of the UTC day, or ABSENT because none
    /// are. A symbol here is closed whatever the day's own budget is doing, and everything not named
    /// is untouched.
    /// </summary>
    public IReadOnlyList<string>? LossSymbolsClosed { get; init; }

    /// <summary>
    /// THE MODEL TRADEAGENT ASKED YOUR AI TOOL FOR, or absent where it asked for none — either the
    /// tool takes no model flag, or nothing has named one and it runs on its own configuration.
    ///
    /// It is the app's choice, not the agent's, and there is no verb that changes it. It is on the
    /// wire because an agent told to cover what it costs is being told most of that arithmetic: the
    /// same turn on a different model is a different bill by a factor of fifty.
    /// </summary>
    public string? AiModel { get; init; }
}

public sealed record ReconcileResult(int Resolved, int Inconclusive, IReadOnlyList<string> Details)
{
    public bool Clean => Inconclusive == 0;
}

/// <summary>Thrown when the gateway refuses a request. Carries a user-safe explanation.</summary>
public sealed class GatewayDeniedException(ErrorCode code, string technical) : Exception(technical)
{
    public ErrorCode Code { get; } = code;
    public ErrorInfo Info => Errors.Get(Code, Message);
}

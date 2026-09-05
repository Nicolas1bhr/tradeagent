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

    AgentContext(string sessionId, bool isOperator)
    {
        SessionId = sessionId;
        IsOperator = isOperator;
    }

    public string SessionId { get; }
    public bool IsOperator { get; }

    /// <summary>
    /// The context for a caller on the other side of the fence, named by whatever session string it
    /// sent. The only factory the pipe server uses, and it cannot return an operator.
    /// </summary>
    public static AgentContext ForAgent(string? sessionId) =>
        new(string.IsNullOrWhiteSpace(sessionId) ? "agent" : sessionId!);

    public override string ToString() => IsOperator ? "operator (in-process)" : SessionId;
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
}

public sealed record GatewayStatus(
    string ProtocolVersion, string AppVersion, TradingMode Mode, bool AiTradingStopped, bool LiveActivated,
    bool ExecutionAvailable, string? ExecutionBlockedReason, string? ConnectorId, string? ConnectorName,
    bool ConnectorIsPaper, string? AccountId, IReadOnlyList<ComponentHealth> Health,
    int OpenRequests, int UnreconciledRequests, RiskPolicy Risk);

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

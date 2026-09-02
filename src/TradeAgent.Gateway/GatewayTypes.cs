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
    /// How long after dispatch we wait before "the broker has never heard of this order" is allowed
    /// to mean it never landed. Protects against reading a slow backend as an absent one.
    /// </summary>
    public TimeSpan AbsenceGrace { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a record may stay in DISPATCHING before the gateway counts it as unconfirmed work
    /// and refuses to trade over it, WITHOUT waiting for a restart to notice.
    ///
    /// 30 s = the connector's own 10 s RPC deadline (<c>AtasConnector</c>'s <c>rpcTimeout</c>; the
    /// adapter's internal budget inside it is 8 s) plus 20 s of slack, which is also four passes of
    /// <see cref="HealthInterval"/>. Under the deadline, "still DISPATCHING" is an ordinary order in
    /// flight and must not pause anything; three times past it, the call has either returned or
    /// thrown and something failed to write the outcome down.
    /// </summary>
    public TimeSpan DispatchStrandedAfter { get; set; } = ExecutionRequestStore.DefaultDispatchStrandedAfter;

    public TimeSpan HealthInterval { get; set; } = TimeSpan.FromSeconds(5);
}

public sealed record AgentContext(string SessionId)
{
    public static readonly AgentContext Operator = new("operator");
    public bool IsOperator => SessionId == "operator";
}

public sealed record PlaceIntent(string Symbol, OrderSide Side, OrderType Type, decimal Quantity,
    decimal? LimitPrice, decimal? StopPrice, TimeInForce Tif, string? Comment);

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

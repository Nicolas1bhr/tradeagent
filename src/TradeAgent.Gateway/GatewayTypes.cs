using TradeAgent.ConnectorSdk;
using TradeAgent.Core;

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
    /// How long after dispatch we wait before "the broker has never heard of this order" is allowed
    /// to mean it never landed. Protects against reading a slow backend as an absent one.
    /// </summary>
    public TimeSpan AbsenceGrace { get; set; } = TimeSpan.FromSeconds(15);

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

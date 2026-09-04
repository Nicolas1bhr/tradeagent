using System.Text.Json.Serialization;

namespace TradeAgent.Core;

public enum TradingMode { OBSERVE, PAPER, LIVE_CONFIRM, LIVE_AUTONOMOUS }

/// <summary>
/// Order lifecycle. UNKNOWN is first-class: it means "we do not know", never "it failed".
/// </summary>
public enum ExecutionState
{
    CREATED, AWAITING_APPROVAL, DISPATCHING, ACKNOWLEDGED, WORKING,
    PARTIALLY_FILLED, FILLED, CANCEL_PENDING, CANCELLED, REJECTED, UNKNOWN, RECONCILING
}

public enum RequestIntent { PLACE, MODIFY, CANCEL, CANCEL_ALL, CLOSE, CLOSE_ALL }

/// <summary>
/// Limits the gateway enforces before anything reaches a broker. Borrowed in spirit from
/// venture-agent's policy file: the agent's autonomy inside its own environment is broad,
/// but authority that leaves the machine is bounded by numbers a human set.
/// </summary>
public sealed class RiskPolicy
{
    /// <summary>Contracts or shares per order. For leveraged products this is the limit that matters.</summary>
    public decimal MaxOrderQuantity { get; set; } = 1m;

    /// <summary>
    /// Order value cap. Zero means "not enforced", which is the default deliberately: one ES future
    /// is a six-figure notional at a four-figure margin, so any naively chosen cap here refuses every
    /// legitimate futures order while teaching the user nothing. Set it for instruments where face
    /// value is the real exposure; rely on <see cref="MaxOrderQuantity"/> otherwise.
    /// </summary>
    public decimal MaxNotionalPerOrder { get; set; }
    public int MaxOpenPositions { get; set; } = 2;
    public int MaxOrdersPerMinute { get; set; } = 6;
    public List<string> InstrumentAllowlist { get; set; } = new();

    public bool InstrumentAllowed(string instrument) =>
        InstrumentAllowlist.Count == 0 ||
        InstrumentAllowlist.Any(i => string.Equals(i, instrument, StringComparison.OrdinalIgnoreCase));
}

public sealed class TradeAgentSettings
{
    public TradingMode Mode { get; set; } = TradingMode.PAPER;
    public bool LiveActivated { get; set; }
    public bool AiTradingStopped { get; set; }
    public string? SelectedRuntimeId { get; set; }
    public string? SelectedConnectorId { get; set; }
    public string? SelectedAccountId { get; set; }
    public RiskPolicy Risk { get; set; } = new();

    [JsonIgnore] public bool ModeAllowsExecution => Mode != TradingMode.OBSERVE;
    [JsonIgnore] public bool ModeIsLive => Mode is TradingMode.LIVE_CONFIRM or TradingMode.LIVE_AUTONOMOUS;
}

/// <summary>
/// A durable record of one MULTI-TARGET intent — a `cancel-all`, a `close-all`, an operator press.
/// Written before any effect, completed with the answer afterwards, never deleted.
/// </summary>
public sealed class CompositeRequest
{
    /// <summary>The id the CALLER used. This is what a replay is recognised by.</summary>
    public required string RequestId { get; init; }
    public string? AgentSessionId { get; init; }

    /// <summary>The operation, as the protocol names it: `cancel-all`, `close-all`.</summary>
    public required string Op { get; init; }

    /// <summary>What this composite's per-target ids are derived from. Stable across replays.</summary>
    public required string Nonce { get; init; }

    /// <summary>The targets captured when the composite was created, as a JSON array.</summary>
    public required string PlanJson { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>The answer the first run produced, or null while it has not produced one.</summary>
    public string? ResultJson { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>A durable record of one mutating intent. Written before dispatch, never deleted.</summary>
public sealed class ExecutionRequest
{
    public required string RequestId { get; init; }
    public string? AgentSessionId { get; init; }
    public required string ConnectorId { get; init; }
    public required string AccountId { get; init; }
    public required string Instrument { get; init; }
    public required RequestIntent Intent { get; init; }
    public required string ParametersJson { get; init; }
    public required string ClientOrderId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? DispatchedAt { get; set; }
    public ExecutionState State { get; set; }
    public string? ConnectorOrderId { get; set; }
    public decimal FilledQuantity { get; set; }
    public decimal? AveragePrice { get; set; }
    public bool NeedsReconciliation { get; set; }
    public DateTimeOffset? LastReconciledAt { get; set; }
    public string? LastError { get; set; }
    public TradingMode Mode { get; init; }
}

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
    /// <summary>
    /// THE INSTRUMENTS THE AI MAY TOUCH. AN EMPTY LIST IS NOT A WILDCARD.
    ///
    /// It used to be: <c>InstrumentAllowed</c> began <c>Count == 0 ||</c>, so "the owner has named
    /// nothing" and "the owner has permitted everything" were the same value. Three different things
    /// arrive at that value, and only one of them is a decision:
    ///
    ///   * a fresh install, where nobody has said anything yet;
    ///   * a settings row this build could not read, where the owner's list was replaced by a
    ///     default (REVIEW 2026-09-05 finding 5);
    ///   * an owner who cleared the box meaning "stop trading these".
    ///
    /// Reading any of those as "every instrument the platform offers" is the software inventing a
    /// permission. So the empty list allows NOTHING, and every screen that shows the list says so.
    /// </summary>
    public List<string> InstrumentAllowlist { get; set; } = new();

    public bool InstrumentAllowed(string instrument) =>
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

    /// <summary>
    /// IS THE SAVED MODE ONE THIS BUILD ACTUALLY HAS?
    ///
    /// <see cref="TradingMode"/> is persisted as a name, and <c>System.Text.Json</c>'s enum converter
    /// reads NUMBERS as well — and casts a number it does not recognise straight onto the enum. A
    /// settings row saying <c>"mode": 999</c> therefore produced a mode of 999, and every gate below
    /// is a comparison against the named values: 999 is not OBSERVE so it executed, it is not
    /// LIVE_CONFIRM or LIVE_AUTONOMOUS so the real-money activation switch was never consulted, and
    /// it is not PAPER so a real-money account was not refused either. A mode nobody chose, trading
    /// real money with the safety off (REVIEW 2026-09-05, Codex F3).
    ///
    /// It is not a hypothetical row: a newer build writes a mode this one has never heard of, and a
    /// rollback reads it. So the classification fails closed — an unrecognised mode allows nothing —
    /// and <c>TradingGateway.LoadSettings</c> says so in the owner's words.
    /// </summary>
    [JsonIgnore] public bool ModeIsRecognised => Enum.IsDefined(Mode);

    [JsonIgnore] public bool ModeAllowsExecution => ModeIsRecognised && Mode != TradingMode.OBSERVE;
    [JsonIgnore] public bool ModeIsLive => Mode is TradingMode.LIVE_CONFIRM or TradingMode.LIVE_AUTONOMOUS;

    /// <summary>
    /// TRUE WHEN NONE OF THE VALUES ABOVE CAME FROM THE OWNER. Not persisted — it is a fact about
    /// this run, and it stops being true the moment the row is written again (see
    /// <see cref="MarkSaved"/>). The health row and the Safety page read it; nothing else sets it.
    /// </summary>
    [JsonIgnore] public bool CouldNotBeRead { get; private set; }

    /// <summary>The row on disk is now one this build wrote, so it is readable by definition.</summary>
    public void MarkSaved() => CouldNotBeRead = false;

    /// <summary>
    /// THE SETTINGS A BUILD USES WHEN IT CANNOT READ THE ROW THE OWNER SAVED (REVIEW 2026-09-05,
    /// finding 5).
    ///
    /// <c>new TradeAgentSettings()</c> was what that failure produced, and its defaults are the
    /// permissions of a FRESH INSTALL: the kill switch up, an allowlist that used to mean
    /// "everything", a quantity cap of one and two open positions. So the one event proving the
    /// software cannot read what the owner asked for was also the event that granted the AI
    /// authority nobody gave it.
    ///
    /// Every field here is instead the most restrictive value that field has:
    ///
    ///   Mode = OBSERVE            the only mode that executes nothing at all
    ///   AiTradingStopped = true   the kill switch, down
    ///   LiveActivated = false     real money off
    ///   SelectedAccountId = null  no account was chosen, and a guess is not a choice
    ///   allowlist = []            which now allows NOTHING
    ///   quantity, positions, orders-per-minute = 0
    ///
    /// <c>MaxNotionalPerOrder</c> stays at 0, which for that field alone means "not enforced": it has
    /// no floor, and a quantity cap of zero has already refused every order before a notional is
    /// computed. The emergency controls are deliberately still reachable — they take no mode, no
    /// allowlist and no cap, and an owner holding a live position needs them most on the day the
    /// software cannot read its own settings.
    ///
    /// The raw row is not destroyed by this; the caller keeps it, because the owner's own values are
    /// the only evidence of what they had asked for.
    /// </summary>
    public static TradeAgentSettings Unreadable() => new()
    {
        CouldNotBeRead = true,
        Mode = TradingMode.OBSERVE,
        AiTradingStopped = true,
        LiveActivated = false,
        SelectedAccountId = null,
        Risk = new RiskPolicy
        {
            MaxOrderQuantity = 0m,
            MaxOpenPositions = 0,
            MaxOrdersPerMinute = 0,
            InstrumentAllowlist = []
        }
    };
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

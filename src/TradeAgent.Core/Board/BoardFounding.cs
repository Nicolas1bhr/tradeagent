using TradeAgent.Core.Db;

namespace TradeAgent.Core.Board;

/// <summary>
/// THE FOUNDING RECORD THE APP WRITES ONCE, at rung 47 (<c>U-board-a1</c>; R24 § 4.5): the policy that governs until the board
/// decides, the board's founding composition and its founding budget share. Its decider is <see cref="BoardDecider.App"/> and its
/// basis <see cref="BoardBasis.Founding"/> — never attributed to the owner, and never written from what an older settings row held.
///
/// <para><b>The values are the build's current defaults</b> (<c>Core/Trading.cs</c>; a test holds them equal to the settings'
/// own while those fields still live there). The standing paper envelope and the instrument allowlist have no default today: they
/// are the orchestrator's to decide and arrive with <c>U-board-a2</c>, so this record states none — an empty allowlist allows
/// nothing, as it always has.</para>
/// </summary>
public static class BoardFounding
{
    /// <summary>The founding policy, version 1.</summary>
    public static BoardPolicy V1 { get; } = new()
    {
        Mode = TradingMode.PAPER,
        LiveActivated = false,
        Risk = new RiskPolicy
        {
            MaxOrderQuantity = 1m,
            MaxNotionalPerOrder = 0m,
            MaxOpenPositions = 2,
            MaxOrdersPerMinute = 6,
            MaxLossPerTrade = 0m,
            MaxDailyLoss = 0m,
            LossMinClosureHours = 24m,
            LossStrikeWindowDays = 7,
            ValuationLossExitMinutes = 15m,
            InstrumentAllowlist = []
        },
        PlanDayTotal = 40m,
        RoleShares = new Dictionary<string, decimal>(StringComparer.Ordinal),
        TurnAllowanceInput = TurnAllowance.Default.InputTokens,
        TurnAllowanceOutput = TurnAllowance.Default.OutputTokens,
        Lanes = 1,
        LanesPerPool = 1,
        LanesPerCellClass = 1,
        ReviewMinutes = 30,
        HeadCeilingHours = 24,
        RewakeFloorMinutes = new Dictionary<string, int>(RewakeFloors.DefaultMinutes, StringComparer.Ordinal),
        PaperEnvelope = null
    };

    /// <summary>
    /// THE FOUNDING COMPOSITION (R24 § 4.1): three seats on at least two model classes, none of them <c>cli</c>. One vendor family
    /// today — the owner's plan is the only runtime he has connected — and the rows say so; a second family joins when he connects
    /// one, by his own press (CHANGE-001). Dormant: nothing launches a board seat before the activation gate (ORCHESTRATION § 2.3).
    /// </summary>
    public static IReadOnlyList<(string Id, string SeatClass, string ModelClass, string VendorFamily)> Seats { get; } =
    [
        ("board-1", SeatClasses.Broker, BoardModelClass.Strong, "openai"),
        ("board-2", SeatClasses.Harness, BoardModelClass.Strong, "openai"),
        ("board-3", SeatClasses.Broker, BoardModelClass.Standard, "openai")
    ];

    /// <summary>
    /// THE BOARD'S FOUNDING BUDGET: its share of the plan's AI day-total, recorded in the founding event and changeable only by the
    /// owner (CHANGE-001; no writer for it in this build). Nothing reads it before a board seat can launch.
    /// </summary>
    public const decimal BudgetShare = 0.10m;
}

/// <summary>The two model classes a board seat names (ORGANISATION § 10: the seat names a CLASS; the runtime profile maps it).</summary>
public static class BoardModelClass
{
    public const string Strong = "strong";
    public const string Standard = "standard";
    public static readonly string[] All = [Strong, Standard];
}

/// <summary>Who decided a policy version. Only <see cref="App"/> is storable at rung 47; <c>U-board-b</c> widens it to the board's acts.</summary>
public static class BoardDecider
{
    public const string App = "app";
}

/// <summary>On what basis a policy version stands. Only <see cref="Founding"/> is storable at rung 47.</summary>
public static class BoardBasis
{
    public const string Founding = "founding";
}

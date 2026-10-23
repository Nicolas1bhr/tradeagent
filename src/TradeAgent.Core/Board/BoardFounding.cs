using TradeAgent.Core.Db;

namespace TradeAgent.Core.Board;

/// <summary>
/// THE FOUNDING RECORD THE APP WRITES ONCE, at rung 49 (<c>U-board-a1</c>; R24 § 4.5): the policy that governs until the board
/// decides, the board's founding composition and its founding budget share. Its decider is <see cref="BoardDecider.App"/> and its
/// basis <see cref="BoardBasis.Founding"/> — never attributed to the owner, and never written from what an older settings row held.
///
/// <para><b>The values are the build's current defaults</b> (<c>Core/Trading.cs</c>; a test holds them equal to the settings'
/// own while those fields still live there). Where today has no default — the instrument allowlist and the standing paper envelope
/// — the values are the ones the orchestrator ruled on 2026-10-23 (R24 § 4.5, Q3): the values the owner chose for M0 attempt 4's
/// paper loop, so the next attempt stays comparable. They are the APP'S founding values, decided by <c>app</c>, never attributed to
/// him: <c>BTCUSDT</c> alone; one standing envelope on the app's own paper venue only (<c>paper</c>, <c>PAPER-1</c>), each
/// deployment at most 0.05 BTC AND at most 5,000 USDT of notional — the notional cap is the one that binds whenever BTC's price
/// makes 0.05 BTC worth more than 5,000 USDT — the build's one deployment per envelope, and no expiry while no board seat is active
/// (the first active board sets its life). Nothing here authorises anything live.</para>
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
            InstrumentAllowlist = [PaperInstrument]
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
        PaperEnvelope = new PaperEnvelopeTerms(PaperVenue, PaperAccount, PaperInstrument, MaxQuantity: 0.05m, MaxNotional: 5_000m,
            Deployments: 1, Days: null)
    };

    /// <summary>The app's own paper venue and its one account (<c>PaperConnector.ConnectorId</c>, <c>TheAccount</c>; a test holds them equal).</summary>
    public const string PaperVenue = "paper", PaperAccount = "PAPER-1";

    /// <summary>The one instrument the founding policy allows: the one the paper venue lists and the tape records.</summary>
    public const string PaperInstrument = "BTCUSDT";

    /// <summary>Where the founding paper values come from, as the founding event records it — the app's decision, citing its source.</summary>
    public const string PaperValuesSource =
        "the allowlist and the paper envelope are the values the owner chose for M0 attempt 4's paper loop, set here by the app as its "
        + "founding values: BTCUSDT; each deployment at most 0.05 BTC and at most 5,000 USDT notional, the notional cap binding whenever "
        + "BTC's price makes 0.05 BTC worth more; one deployment; no expiry while no board seat is active";

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

/// <summary>Who decided a policy version. Only <see cref="App"/> is storable at rung 49; <c>U-board-b</c> widens it to the board's acts.</summary>
public static class BoardDecider
{
    public const string App = "app";
}

/// <summary>On what basis a policy version stands. Only <see cref="Founding"/> is storable at rung 49.</summary>
public static class BoardBasis
{
    public const string Founding = "founding";
}

namespace TradeAgent.Core;

/// <summary>
/// THE SECURITY BOARD'S PAGE AND THE OWNER'S PAGE, IN WORDS (<c>U-settings-shed</c>; R24 § 4.7). Spelled once, here, so the rail, the pages
/// and every sentence that sends the owner to one of them say the same name — the rule <see cref="Labels"/> exists for.
/// </summary>
public static partial class Labels
{
    /// <summary>The read-only page that shows every decision of the security board in force.</summary>
    public const string BoardPage = "Board";

    /// <summary>The owner's page: what stays his (R24 § 4.3) — the emergency family, his connections and keys, his real money, evidence and updates.</summary>
    public const string OwnerPage = "Owner";

    /// <summary>
    /// ONE FIELD OF THE BOARD'S POLICY, AS THE PAGE NAMES IT: its path in the policy's body (<c>risk.max_order_quantity</c>), the group it
    /// is shown under, its words, and the unit its figure is read in (null where the value is a word or a list).
    /// </summary>
    public sealed record BoardFieldWords(string Path, string Group, string Label, string? Unit);

    public const string BoardGroupTrading = "Trading and real money";
    public const string BoardGroupOrders = "Limits on every order";
    public const string BoardGroupLosses = "Losses";
    public const string BoardGroupSpending = "What the AI may use";
    public const string BoardGroupWork = "How the AI works";
    public const string BoardGroupPaper = "Paper experiments";

    /// <summary>
    /// EVERY FIELD THE PAGE HAS WORDS FOR, in the order it shows them. A field of the policy in force that is not here is NOT shown as a
    /// decision: the page names it as one this version has no words for (refuse, never guess). The envelope's terms appear as one row
    /// while there are none and as their seven rows while there are.
    /// </summary>
    public static readonly IReadOnlyList<BoardFieldWords> BoardFields =
    [
        new("mode", BoardGroupTrading, "Trading mode", null),
        new("live_activated", BoardGroupTrading, "Real money switched on", null),

        new("risk.max_order_quantity", BoardGroupOrders, MaxOrderQuantity, null),
        new("risk.max_notional_per_order", BoardGroupOrders, MaxNotionalPerOrder, null),
        new("risk.max_open_positions", BoardGroupOrders, MaxOpenPositions, null),
        new("risk.max_orders_per_minute", BoardGroupOrders, MaxOrdersPerMinute, null),
        new("risk.instrument_allowlist", BoardGroupOrders, InstrumentAllowlist, null),

        new("risk.max_loss_per_trade", BoardGroupLosses, MaxLossPerTrade, null),
        new("risk.max_daily_loss", BoardGroupLosses, MaxDailyLoss, null),
        new("risk.loss_min_closure_hours", BoardGroupLosses, "How long it stays closed after a losing day", "hours"),
        new("risk.loss_strike_window_days", BoardGroupLosses, LossStrikeWindow, "days"),
        new("risk.valuation_loss_exit_minutes", BoardGroupLosses, "Close a position nobody can value after", "minutes"),

        new("plan_day_total", BoardGroupSpending, PlanDailyCapacity, null),
        new("role_shares", BoardGroupSpending, "Each role's share of that day", null),
        new("turn_allowance_input", BoardGroupSpending, TurnAllowanceIn, "tokens"),
        new("turn_allowance_output", BoardGroupSpending, TurnAllowanceOut, "tokens"),

        new("lanes", BoardGroupWork, Lanes, null),
        new("lanes_per_pool", BoardGroupWork, LanesPerPool, null),
        new("lanes_per_cell_class", BoardGroupWork, LanesPerCellClass, null),
        new("review_minutes", BoardGroupWork, "Wake the AI to look around at most every", "minutes"),
        new("head_ceiling_hours", BoardGroupWork, "The longest a head waits between looks", "hours"),
        new("rewake_floor_minutes", BoardGroupWork, "The shortest wait before a seat is woken again, by level", "minutes"),

        new("paper_envelope", BoardGroupPaper, "Standing paper envelope", null),
        new("paper_envelope.connector", BoardGroupPaper, "Paper envelope: platform", null),
        new("paper_envelope.account", BoardGroupPaper, "Paper envelope: account", null),
        new("paper_envelope.instrument", BoardGroupPaper, "Paper envelope: instrument", null),
        new("paper_envelope.max_quantity", BoardGroupPaper, "Paper envelope: most it may hold at once", null),
        new("paper_envelope.max_notional", BoardGroupPaper, "Paper envelope: most it may be worth", null),
        new("paper_envelope.deployments", BoardGroupPaper, "Paper envelope: runs it may carry", null),
        new("paper_envelope.days", BoardGroupPaper, "Paper envelope: for how long", "days")
    ];

    /// <summary>The words for <paramref name="path"/>, or null — and then the page refuses to show it as a decision.</summary>
    public static BoardFieldWords? BoardField(string path) => BoardFields.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.Ordinal));

    /// <summary>A field of the policy in force that this version has no words for: named, and never shown as decided.</summary>
    public static string BoardFieldUnnamed(IEnumerable<string> paths) =>
        "The policy in force also holds " + string.Join(", ", paths.Select(p => $"“{p}”"))
        + ", which this version of TradeAgent has no words for, so it is not shown here as a decision.";

    /// <summary>
    /// WHO DECIDED A VERSION, in words. The founding record's decider is the app — never the owner (R24 § 4.5: a founding value is the
    /// app's, and saying it was his would make the record mean something it is not).
    /// </summary>
    public static string BoardDecider(string decider) => decider switch
    {
        "app" => "the app — TradeAgent's founding policy, not you",
        _ => $"the board ({decider})"
    };

    /// <summary>What a field's value rests on: the founding record, or the board's version that set it.</summary>
    public static string BoardFieldSource(bool founding, int version, string? since) =>
        founding
            ? "founding (the app's)" + (since is null ? "" : $", since {since}")
            : $"set by the board's version {version}" + (since is null ? "" : $", since {since}");

    /// <summary>A version's rationale is the deciders' claim, never a measurement — said in the line that shows it.</summary>
    public static string BoardRationale(string? rationale) =>
        rationale is { Length: > 0 } r
            ? $"Their reasons, as they gave them (a claim, not a measurement): {r}"
            : "No reasons: the app wrote this version, and no seat decided it.";

    /// <summary>A version's score, or that the audit line has not scored it.</summary>
    public static string BoardScore(decimal? score) => score is { } s ? $"Scored {s}" : "Not yet scored";

    /// <summary>The evidence class of what a version allocated, or that it allocated nothing.</summary>
    public static string BoardEvidenceClass(string? evidenceClass) =>
        evidenceClass is { Length: > 0 } c ? $"Evidence class: {c}" : "Evidence class: none — it allocated nothing";

    /// <summary>What an empty instrument list means, on the Board page.</summary>
    public const string NoInstrumentAllowedHere = "no instrument is allowed";

    /// <summary>The press that opens a version history on the Board page. It reads; it writes nothing.</summary>
    public const string ShowBoardHistory = "Show every version";

    /// <summary>The same press once the history is open.</summary>
    public const string HideBoardHistory = "Hide the versions";

    /// <summary>The history while the founding record is all there is.</summary>
    public const string BoardHistoryFoundingOnly = "No board act yet. The only version is the app's founding record.";

    /// <summary>The policy in force could not be read: nothing on the page may be taken as decided.</summary>
    public static string BoardPolicyUnreadable(string? why) =>
        "The security board's policy could not be read"
        + (why is { Length: > 0 } w ? $" — {w}" : "")
        + ". Nothing below is shown as decided, and TradeAgent allows nothing that needs it.";

    /// <summary>A seat's class, in words (R24 § 4.1: broker-only or confined, never a program with a shell).</summary>
    public static string BoardSeatClass(string seatClass) => seatClass switch
    {
        "broker" => "reaches TradeAgent only through its own verbs, with no shell",
        "harness" => "runs on TradeAgent's own worker, with no shell",
        "cell" => "runs in a sealed workspace",
        _ => $"a seat class this version has no words for (“{seatClass}”)"
    };

    /// <summary>A seat's status, in words.</summary>
    public static string BoardSeatStatus(string status) => status switch
    {
        "dormant" => "dormant — founded and never started; it takes no turn and decides nothing",
        "active" => "active",
        _ => $"a status this version has no words for (“{status}”)"
    };

    /// <summary>The line under the seats while none of them is active.</summary>
    public const string BoardSeatsDormant =
        "No board seat is active, so nothing above has been decided by the board: every value is the app's founding policy. "
        + "A seat starts only once a sealed workspace has been proven on this computer.";
}

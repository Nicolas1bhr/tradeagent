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
        "board" => "the security board — an act of its seats",
        "owner" => "you — your veto of a board act, which only takes away",
        _ => $"a decider this version has no words for (“{decider}”)"
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
        "retired" => "retired — by you; it acts no more",
        _ => $"a status this version has no words for (“{status}”)"
    };

    // ---- the board's acts, as the owner reads them (U-board-b1; the seam with U-org-surface-b) ----------------------------------

    /// <summary>An act's status, in words.</summary>
    public static string BoardActStatus(string status) => status switch
    {
        "open" => "waiting for a second seat",
        "applied" => "applied at once — it narrowed",
        "confirmed" => "confirmed by a second seat and applied",
        "expired" => "expired — no second seat confirmed it, and it applied nothing",
        "stale" => "closed — what it changed had moved, and it applied nothing",
        "vetoed" => "vetoed by you",
        _ => $"a status this version has no words for (“{status}”)"
    };

    /// <summary>What a seat may propose of the board's own power, in words (CHANGE-001: the owner decides, or no one does).</summary>
    public static string BoardPowerTarget(string target) => target switch
    {
        "composition" => "the board's seats",
        "board-budget" => "the board's budget",
        "audit-budget" => "the audit line's budget",
        "connection" => "a connection, key, runtime or vendor",
        "founding-policy-shape" => "the founding policy's shape",
        "kernel" => "a change to TradeAgent's kernel",
        _ => $"a target this version has no words for (“{target}”)"
    };

    /// <summary>The owner's veto: press one shows what returns; press two vetoes. It only ever takes away (R24 Q2).</summary>
    public const string VetoBoardActPress = "Veto this act";

    /// <summary>The veto's second press, after the first has shown what returns.</summary>
    public const string VetoBoardActConfirm = "Veto it — nothing is widened";

    /// <summary>Under the veto's first press: what a veto does, and what it never does.</summary>
    public const string VetoBoardActWhat =
        "A veto closes a proposal, or sets every field the act changed back to the narrower of now and before. It never widens a "
        + "limit or puts anything to work, and an act that only narrowed cannot be vetoed — undoing it would widen.";

    /// <summary>The owner's presses over the board's own power (CHANGE-001), each two presses in his UI.</summary>
    public const string SetBoardBudgetSharePress = "Set the board's share of the day",
        FoundBoardSeatPress = "Found a board seat",
        RetireBoardSeatPress = "Retire this seat";

    /// <summary>The line under the seats while none of them is active.</summary>
    public const string BoardSeatsDormant =
        "No board seat is active, so nothing above has been decided by the board: every value is the app's founding policy. "
        + "A seat starts only once a sealed workspace has been proven on this computer.";

    // ---- the owner's page -------------------------------------------------------------------------------------------------------

    /// <summary>The line under the owner's page title: what is his, and where everything else is.</summary>
    public const string OwnerPageSubtitle =
        "What only you decide: stopping the AI, your connections and keys, the real money it may spend, how its work is priced, and "
        + "updates. Every limit and allowance is the security board's — the " + BoardPage + " page shows each one in force.";

    /// <summary>The emergency press that cancels every working order (two presses).</summary>
    public const string CancelAllWorkingOrders = "Cancel all working orders";

    /// <summary>The emergency press that closes every position (two presses).</summary>
    public const string CloseAllPositions = "Close all positions";

    /// <summary>
    /// "REOPEN AFTER REVIEW" IS HIS ONLY UNTIL THE BOARD IS ACTIVE (the orchestrator's ruling of 2026-10-23 on the owner's behalf): said on
    /// the card that carries the press, so the owner is not surprised the day it is gone.
    /// </summary>
    public const string ReopenAfterReviewIsYoursUntilTheBoard =
        "Reopening a held paper scope is " + YoursUntilTheBoardIsActive + ", then the board's.";

    /// <summary>
    /// THE WORDS ON EVERY PRESS THAT IS HIS ONLY WHILE NO BOARD SEAT IS ACTIVE (the orchestrator's ruling of 2026-10-23): the release of a
    /// held paper scope and the seat choices. Spelled once, so the two presses cannot drift apart.
    /// </summary>
    public const string YoursUntilTheBoardIsActive = "yours until the security board is active";

    /// <summary>The seat choices' card title on the owner's page (the orchestrator's ruling: his while no board seat is active).</summary>
    public const string SeatChoicesTitle = "What each seat runs on";

    /// <summary>Under the title: whose the choices are, and until when — the same words as the review release.</summary>
    public const string SeatChoicesAreYours =
        "Which model the AI runs on, and what the Research Director runs on, are " + YoursUntilTheBoardIsActive + ", then the board's: "
        + "from the first active board seat this card goes. Choosing a model grants nothing — a dearer one reaches the day's limit sooner.";

    /// <summary>A seat choice refused because a board seat is active (or the board cannot be read): it writes nothing.</summary>
    public const string SeatChoicesAreTheBoards =
        "Nothing was changed. A security board seat is active, so what each seat runs on is the board's decision now, never this page's.";

    /// <summary>
    /// THE RELEASE REFUSED BECAUSE A HELD SCOPE IS NOT PAPER (Codex P1 on <c>U-settings-shed</c>): the owner's release is for paper scopes
    /// only, and the release lifts every hold on the account at once, so while one of them is not paper nothing is released.
    /// </summary>
    public static string ReleaseRefusedNotPaper(IReadOnlyList<string> scopes) =>
        $"Nothing was released. {string.Join(", ", scopes)} {(scopes.Count == 1 ? "is" : "are")} held outside paper trading, and "
        + "reopening anything but a paper scope is the security board's decision, never this page's. The release lifts every hold on "
        + "this account at once, so it stays refused while one of them is not paper.";

    /// <summary>
    /// The release refused because a hold row does not match the closure it is filed under — it names another closure, or a mode other
    /// than that closure's (Codex re-review 2 of <c>U-settings-shed</c>, P1). The release lifts what a row is FILED under, so a row that
    /// says something else is never released on a guess, tampered or not.
    /// </summary>
    public static string ReleaseRefusedMismatch(IReadOnlyList<string> scopes) =>
        $"Nothing was released. The hold on {string.Join(", ", scopes)} does not match the closure it is filed under — it names another "
        + "closure or another mode — so TradeAgent will not release it on a guess. Reopening anything but a paper scope is the security "
        + "board's decision, never this page's, and the release lifts every hold on this account at once.";

    /// <summary>The release refused because a board seat is active (or the board cannot be read): from then on a release is the board's act.</summary>
    public const string ReleaseRefusedBoardActive =
        "Nothing was released. A security board seat is active, so reopening a held scope is the board's decision now, never this page's.";

    /// <summary>The plan's day-total is the board's decision: the owner's page reads the plan's day and points at where it is decided.</summary>
    public const string PlanDayTotalIsTheBoards =
        "How much of your plan the AI may use in a day is the security board's decision; the " + BoardPage + " page shows it.";

    /// <summary>What would let a real-money turn start again when midnight will not: the owner's own limit, or a cheaper model.</summary>
    public const string TodayMoneyRepair =
        "Only a higher real-money limit below, or a cheaper model — the security board's choice — changes that.";

    /// <summary>The same, said away from the owner's page (the activity log): it names the page.</summary>
    public const string TodayMoneyRepairOnTheOwnerPage =
        "Only a higher real-money limit on the " + OwnerPage + " page, or a cheaper model — the security board's choice — changes that.";

    /// <summary>What would let a plan turn start again when midnight will not: both the board's.</summary>
    public const string TodayPlanRepair =
        "Only a higher plan day-total or a cheaper model changes that, and both are the security board's; the " + BoardPage
        + " page shows the day-total.";

    /// <summary>A ceiling of 0: no day admits a turn of this kind, so the line does not promise midnight.</summary>
    public static string TodayAdmitsNothing(string repair) =>
        $"reached, and midnight will not bring a turn back: a limit of 0 admits no turn of this kind on any day. {repair}";

    /// <summary>One turn sets aside more than the whole day allows, so a fresh day cannot fund it either.</summary>
    public static string TodayCannotFundATurn(string oneTurn, string repair) =>
        $"reached, and midnight will not bring a turn back: one turn of this kind sets aside {oneTurn}, more than the whole day allows. {repair}";

    /// <summary>The owner's page's four groups, in the order they are shown.</summary>
    public const string OwnerGroupConnections = "Connections";
    public const string OwnerGroupMoney = "Real money";
    public const string OwnerGroupEvidence = "Evidence";
    public const string OwnerGroupUpdates = "Updates";

    /// <summary>Under the campaign budgets: what they are, and why there is no box.</summary>
    public const string CampaignBudgetsAreEvidence =
        "What each research campaign may spend, and the account the judge pretends to have. Evidence, not limits: each campaign copies "
        + "them when it opens, no model sets them, and the security board cannot either. This page shows them; it has no box for them.";

    /// <summary>The caution card over a home an earlier build left armed for real money.</summary>
    public const string EarlierLiveSettingTitle = "Real money armed by an earlier version";

    /// <summary>
    /// The header's AI line over an earlier armed home (fix pass 1, Codex P2): shown before "stopped", because while it stands every order
    /// is refused whatever the kill switch says, and the repair is the strip under the header.
    /// </summary>
    public const string HeaderEarlierLiveSetting = "Every order refused — set aside the real-money setting an earlier version saved";

    /// <summary>What the one press did, in the gateway's terms: kept, not converted; the founding policy governs.</summary>
    public const string EarlierLiveSettingSetAside =
        "Set aside. Your earlier settings are kept exactly as they were under their own name, nothing was converted, and the founding "
        + "policy (Practice, no real money) governs until the security board decides.";

    /// <summary>The press, or the reading, refused: said, and the card stays.</summary>
    public static string EarlierLiveSettingNotSetAside(string why) =>
        $"The earlier real-money setting was not set aside: {why}. Every order stays refused until it is.";
}

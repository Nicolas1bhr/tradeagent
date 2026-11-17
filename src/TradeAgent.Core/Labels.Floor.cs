using System.Globalization;
using TradeAgent.Core.Db;

namespace TradeAgent.Core;

/// <summary>
/// THE WORDS OF THE FLOOR AND OF THE WINDOW'S PLACES (<c>U-ui-alive</c>, <c>U-ui-world</c>). Spelled once, so the page, a test and a sentence cannot drift
/// apart. The floor is the organisation drawn as a place: every seat a cell, lit while its turn is open. Every sentence here is said of a
/// row the app wrote; none of them says more than the row does. What a model or the owner wrote is only ever shown inside
/// <see cref="FloorQuote"/>'s marks, so no such text can go on sounding like the app.
/// </summary>
public static partial class Labels
{
    // ---- the floor's name ---------------------------------------------------------------------------------------------------------

    /// <summary>The place's name in the dock. Not "Live": in this app that word means real money, and this page is not that.</summary>
    public const string NavFloor = "The floor";

    // ---- the places (U-ui-world): the window is one picture, and every page is found at the thing it is about ------------------------

    /// <summary>The floor's own leaf: the whole organisation, with no sheet over it.</summary>
    public const string PlaceOverview = "Overview";
    public const string PlaceTalk = "Talk";
    public const string PlaceDesk = "The desk";
    public const string PlaceBoard = "The board";
    public const string PlaceYours = "Yours";
    public const string PlaceMachine = "The machine";

    public const string PlaceFloorSays = "The organisation itself: who is working, what crosses between them, and what it costs.";
    public const string PlaceTalkSays = "Your conversation with the AI.";
    public const string PlaceDeskSays = "Where the organisation meets the market: what is running now, and each day's report.";
    public const string PlaceBoardSays = "The security board's decisions in force.";
    public const string PlaceYoursSays = "What only you decide, what reached you, what you hand the AI, and your private line.";
    public const string PlaceMachineSays = "What the floor stands on: this computer's parts, what TradeAgent did, and the market data.";

    /// <summary>The search over every page and every press, and what it says before anything is typed and when nothing matches.</summary>
    public const string FindPress = "Find";
    public const string FindPrompt = "Find a page or a button";
    public const string FindNothing = "Nothing in TradeAgent is called that.";
    /// <summary>What a found press says under its own words: the page it is on.</summary>
    public static string FindOnPage(string page) => $"on the {page} page";
    /// <summary>What a found page says under its own name: the place it is a leaf of.</summary>
    public static string FindInPlace(string place) => $"in {place}";

    /// <summary>
    /// What a figure at the top of a unit's column is a figure of (the floor's money lens): an API key's spend is real money; the
    /// plan's is its capacity counted at list price, and not money. Never one word for both.
    /// </summary>
    public static string FloorCostOf(string billingClass) => BillingClasses.Of(billingClass) switch
    {
        BillingClasses.Key => "real money today",
        BillingClasses.Plan => "of the plan's capacity today, at list price",
        _ => "counted today"
    };

    /// <summary>What stands under "unknown" on a unit whose turns reported no cost: how many, and that it is not a figure.</summary>
    public static string FloorCostUnpriced(int turns) =>
        $"{turns.ToString(CultureInfo.InvariantCulture)} {(turns == 1 ? "turn" : "turns")} reported no cost";

    /// <summary>The press that puts a sheet away and gives the floor the whole window back.</summary>
    public const string SheetClose = "Back to the floor";

    // ---- what stands on the floor -----------------------------------------------------------------------------------------------

    public const string FloorBoard = "Security board";
    public const string FloorOwner = "You";
    public const string FloorReferee = "The referee";
    /// <summary>What stands beside the tape's newest end: whose prices and the last minute's close.</summary>
    public static string FloorTapeCaption(string symbol, decimal close) =>
        symbol + "  " + close.ToString("#,0.########", CultureInfo.InvariantCulture);

    /// <summary>The same, where the newest minute is older than a tape being recorded would be: it says how old.</summary>
    public static string FloorTapeStale(string symbol, decimal close, TimeSpan age) =>
        FloorTapeCaption(symbol, close) + "  ·  last recorded " + FloorSpan(age) + " ago";

    /// <summary>A security-board seat's name on the floor: its number, from its id.</summary>
    public static string FloorBoardSeat(string id) =>
        id.StartsWith("board-", StringComparison.Ordinal) && id.Length > 6 ? "Board seat " + id[6..] : id;

    /// <summary>The empty place where a unit's head would stand.</summary>
    public static string FloorVacant(string unit) => $"{unit}: nobody at its head";

    public const string FloorVacantLine = "This unit has no head. Nothing is given to it and nothing comes back from it until one is hired.";
    public const string FloorDormantLine = "Dormant: it has a place and takes no turn until it is activated.";
    public const string FloorIdleLine = "Active. No turn is open in it now.";
    public const string FloorAsleepLine = "Active, but the AI is not running, so no turn opens in it.";
    public const string FloorBoardSeatLine = "A seat of the security board: it decides limits and allowances, never an order.";

    // ---- the floor's own state --------------------------------------------------------------------------------------------------

    public const string FloorNobodyWorking = "No turn is open right now.";

    /// <summary>Who has a turn open, by name: the first two named, the rest counted.</summary>
    public static string FloorWorking(IReadOnlyList<string> names) => names.Count switch
    {
        0 => FloorNobodyWorking,
        1 => $"{names[0]} is working.",
        2 => $"{names[0]} and {names[1]} are working.",
        _ => $"{names[0]}, {names[1]} and {FloorCount(names.Count - 2, "other", "others")} are working."
    };

    /// <summary>
    /// The soonest queued wake of an active position: that it is queued and when it is due, which is what the queue records — not a
    /// promise that the turn opens then.
    /// </summary>
    public static string FloorNext(string name, TimeSpan until) =>
        until <= TimeSpan.Zero ? $"Queued next: {name}, due now." : $"Queued next: {name}, due in {FloorSpan(until)}.";

    /// <summary>What a working cell's caption says under its name: how long its turn has been open.</summary>
    public static string FloorWorkingFor(TimeSpan open) => "working · " + FloorClock(open);

    public const string FloorFeed = "What just happened";
    public const string FloorFeedAll = "Show everything";
    public const string FloorNothingOfIts = "Nothing of its is among the last things recorded.";
    public const string FloorNothingYet = "Nothing has happened on this floor yet.";
    public const string FloorNotRead = "The organisation's records could not be read just now.";
    public const string FloorNotDrawn = "The floor could not be drawn on this computer, so it is left dark. Every other page works as before.";
    public const string FloorEmpty = "No organisation yet: nothing has been chartered in this home.";

    public const string FloorStart = "Start the AI";

    // ---- the figures along the bottom -------------------------------------------------------------------------------------------

    public const string FloorTurnsToday = "AI turns today";
    public const string FloorSpendToday = "AI spend today";
    public const string FloorOpenWork = "Tasks open";
    public const string FloorToJudge = "Deliveries waiting to be judged";
    public const string FloorUnknown = "unknown";

    // ---- replay -----------------------------------------------------------------------------------------------------------------

    public const string FloorReplay = "Replay";
    public const string FloorReplayHour = "The last hour";
    public const string FloorReplayDay = "The last 24 hours";
    public const string FloorReplayAll = "Everything recorded";
    public const string FloorReplayStop = "Back to now";

    /// <summary>What stands over the floor while it replays: that it is a replay, and of when.</summary>
    public static string FloorReplaying(DateTimeOffset at) =>
        "Replay of the records — " + at.ToLocalTime().ToString("d MMM, HH:mm", CultureInfo.InvariantCulture);

    public const string FloorReplayNote = "A replay shows what was recorded, sped up. Nothing in it is happening now.";
    public const string FloorReplayEnded = "The replay has reached now.";
    public const string FloorReplayLoading = "Reading the records…";
    public const string FloorReplayNothing = "Nothing was recorded in that time.";

    // ---- what happened, one line each -------------------------------------------------------------------------------------------

    public static string FloorTurnStarted(string who) => $"{who} started a turn.";

    public static string FloorTurnEnded(string who, TimeSpan took, string? cost) =>
        $"{who} finished a turn after {FloorSpan(took)}" + (cost is null ? "." : $", {cost}.");

    public static string FloorTurnFailed(string who, TimeSpan took) => $"{who}'s turn ended with an error after {FloorSpan(took)}.";

    public static string FloorTurnLost(string who) => $"{who}'s turn was cut off: TradeAgent stopped while it was open.";

    public static string FloorMandateIssued(string decider, string holder, bool exploration, string objective) =>
        $"{decider} gave {holder} {(exploration ? "an exploration" : "a task")}: {FloorQuote(objective)}";

    public static string FloorMandateDelivered(string holder, string decider, string? report) =>
        $"{holder} delivered to {decider}" + (report is { Length: > 0 } ? $": {FloorQuote(report)}" : ".");

    public static string FloorMandateAccepted(string decider, string holder) => $"{decider} accepted {holder}'s delivery.";

    public static string FloorMandateRejected(string decider, string holder, string? reason) =>
        $"{decider} rejected {holder}'s delivery" + (reason is { Length: > 0 } ? $": {FloorQuote(reason)}" : ".");

    public static string FloorMandateRetry(string holder) => $"{holder} starts another try at its task.";

    public static string FloorMandateDeclined(string holder, string? reason) =>
        $"{holder} declined its task" + (reason is { Length: > 0 } ? $": {FloorQuote(reason)}" : ".");

    public static string FloorMandateCancelled(string holder) => $"The task {holder} held was cancelled.";
    public static string FloorMandateExpired(string holder) => $"The task {holder} held ran past its deadline and expired.";
    public static string FloorMandateRescoped(string holder) => $"The task {holder} holds was changed.";

    public static string FloorMandateProposed(string holder, string? what) =>
        $"{holder} proposed a change to its task" + (what is { Length: > 0 } ? $": {FloorQuote(what)}" : ".");

    public static string FloorDecision(string who, string choice) => $"{who} decided: {FloorQuote(choice)}";

    public static string FloorEscalation(string sender, string to, string reason) => $"{sender} escalated to {to}: {FloorQuote(reason)}";

    public static string FloorEscalationToOwner(string sender, string reason) => $"{sender} escalated to you: {FloorQuote(reason)}";

    public static string FloorLateral(string sender, string to, string text) => $"{sender} told {to}: {FloorQuote(text)}";

    public static string FloorChartered(string by, string unit) => $"{by} chartered {unit}.";
    public static string FloorHired(string by, string unit) => $"{by} hired a position into {unit}.";
    public static string FloorActivated(string by, string position) => $"{by} activated {position}.";
    public static string FloorRetired(string by, string position) => $"{by} retired {position}.";
    public static string FloorClosed(string by, string unit) => $"{by} closed {unit}.";
    public static string FloorChiefAppointed(string by) => $"{by} appointed a chief.";
    public static string FloorChiefReplaced(string by) => $"{by} replaced the chief.";
    public static string FloorChiefVacated(string by) => $"{by} removed the chief.";

    /// <summary>Who decided a change to the chart, where the row names the app, the owner or the board and not a position.</summary>
    public static string FloorDecider(string decider) => decider switch
    {
        "owner" => "You",
        "app" => "TradeAgent",
        "board" => "The security board",
        _ => decider
    };

    public static string FloorBoardAct(string seat, string eventKind, string actKind) => eventKind switch
    {
        "proposed" => $"{seat} proposed a board act ({actKind}).",
        "applied" => $"{seat}'s board act ({actKind}) took effect.",
        "confirmed" => $"{seat} confirmed a board act ({actKind}).",
        "refused" => $"{seat} refused a board act ({actKind}).",
        "expired" => $"A board act ({actKind}) expired unconfirmed.",
        "stale" => $"A board act ({actKind}) went stale: what it was about had changed.",
        "vetoed" => $"A board act ({actKind}) was vetoed.",
        _ => $"{seat}: board act {eventKind} ({actKind})."
    };

    public static string FloorBacktest(string who, int? trades, string? outcome) =>
        $"{who} ran a backtest" + (trades is { } n ? $": {FloorCount(n, "trade", "trades")}" : "")
        + (outcome is { Length: > 0 } ? $", {outcome.ToLowerInvariant()}" : "") + " — a simulation.";

    public static string FloorVerdictAsked(string who) => $"{who} asked the referee for a final judgement.";

    public static string FloorVerdict(string verdict, string? reason) =>
        $"The referee judged: {verdict.ToLowerInvariant()}" + (reason is { Length: > 0 } ? $" — {FloorCut(reason, 110)}" : ".");

    public static string FloorPaperRun(string symbol) => $"A paper run started in {symbol} — a simulation, no money.";

    public static string FloorFill(string side, string symbol, decimal quantity, decimal price) =>
        $"Filled: {side.ToLowerInvariant()} {quantity.ToString("0.########", CultureInfo.InvariantCulture)} {symbol} at "
        + price.ToString("0.########", CultureInfo.InvariantCulture) + ".";

    public static string FloorOwnerWords(string to) => $"You wrote to {to}.";

    /// <summary>
    /// A tool call, as a working cell's caption shows it under its name. What it was called with is the seat's own words, so it is
    /// shown as a quotation like everything else an AI wrote; the tool's name and "refused" are TradeAgent's.
    /// </summary>
    public static string FloorTool(string tool, string? argument, bool served) =>
        (served ? "" : "refused: ") + (argument is { Length: > 0 } ? FloorQuote(argument, 64) : tool);

    // ---- how the floor says a length of time, a count, and somebody else's words ---------------------------------------------------

    /// <summary>Somebody else's words: one line, cut at <paramref name="most"/> characters, in marks that the text itself cannot close.</summary>
    public static string FloorQuote(string text, int most = 120) =>
        "“" + FloorCut(text, most).Replace('“', '‘').Replace('”', '’') + "”";

    /// <summary>One line of at most <paramref name="most"/> characters: line breaks become spaces, and a cut says so with an ellipsis.</summary>
    public static string FloorCut(string text, int most)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= most ? line : line[..most].TrimEnd() + "…";
    }

    /// <summary>A length of time in words: seconds under a minute, minutes under an hour, then hours and minutes, then days and hours.</summary>
    public static string FloorSpan(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        var inv = CultureInfo.InvariantCulture;
        if (t.TotalSeconds < 60) return ((int)t.TotalSeconds).ToString(inv) + " s";
        if (t.TotalMinutes < 60) return ((int)t.TotalMinutes).ToString(inv) + " min";
        if (t.TotalHours < 24)
            return ((int)t.TotalHours).ToString(inv) + " h" + (t.Minutes > 0 ? " " + t.Minutes.ToString(inv) + " min" : "");
        return ((int)t.TotalDays).ToString(inv) + " d" + (t.Hours > 0 ? " " + t.Hours.ToString(inv) + " h" : "");
    }

    /// <summary>A running clock: minutes and seconds, and hours once there are any.</summary>
    public static string FloorClock(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        var inv = CultureInfo.InvariantCulture;
        return t.TotalHours >= 1
            ? ((int)t.TotalHours).ToString(inv) + ":" + t.Minutes.ToString("00", inv) + ":" + t.Seconds.ToString("00", inv)
            : t.Minutes.ToString(inv) + ":" + t.Seconds.ToString("00", inv);
    }

    static string FloorCount(int n, string one, string many) => n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);
}

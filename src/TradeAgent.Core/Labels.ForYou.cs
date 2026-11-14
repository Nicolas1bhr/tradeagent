using System.Globalization;

namespace TradeAgent.Core;

/// <summary>
/// THE WORDS OF THE OWNER'S "FOR YOU" PAGE, THE BOARD'S ACTS AND HIS PRESSES OVER THE BOARD'S OWN POWER (<c>U-org-surface-b</c>; R24
/// §§ 4.3, 4.4, 4.6, 4.7; ORCHESTRATION §§ 2.5, 2.9). Spelled once, so a page, a test and a sentence cannot drift apart.
/// </summary>
public static partial class Labels
{
    // ---- the page ---------------------------------------------------------------------------------------------------------------

    /// <summary>The rail entry and the page's title (the orchestrator's ruling Q1, 2026-10-24).</summary>
    public const string NavForYou = "For you";

    public const string ForYouSubtitle =
        "What reached you: escalations nobody below could take, your words still owed a turn, and the board's proposals of its own "
        + "power. The presses here are yours: a veto, which only ever takes away, and activating a position.";

    /// <summary>What the page says of an escalation: a record, never a wake, and no answer goes from here.</summary>
    public const string ForYouEscalationsAreRecords =
        "An escalation that climbs past every head reaches you as a record only: nothing waits on you and nothing wakes. "
        + "If you want to answer, your words go by Chat.";

    public const string ForYouEscalations = "Escalations that reached you";
    public const string ForYouEscalationsNone = "No escalation has reached you.";
    public const string ForYouOwed = "Your words still owed a turn";
    public const string ForYouOwedNone = "Every word of yours has been taken by a turn.";
    public const string ForYouPower = "The board's proposals of its own power";
    public const string ForYouPowerNone = "No board seat has proposed a change to its own power.";
    public const string ForYouBoardVetoes = "Board acts you can veto";

    public const string ForYouBoardVetoesNone =
        "No board act can be vetoed now. An act that only narrowed is never offered: undoing it would widen.";

    public const string ForYouStructureVetoes = "Changes to the chart you can veto";

    public const string ForYouStructureVetoesNone =
        "No change to the chart can be vetoed now: a veto undoes a charter, a close, a retirement or a hire a position decided.";

    public const string ForYouActivate = "Positions waiting to be activated";
    public const string ForYouActivateNone = "No position is waiting to be activated.";

    /// <summary>Who wrote the owner's own words, as a claim's attribution.</summary>
    public const string ForYouYou = "You";

    /// <summary>An escalation's line of the heads it skipped on its way to the root — the app's own walk.</summary>
    public static string EscalationSkipped(IReadOnlyList<string> skipped) =>
        skipped.Count == 0 ? "It skipped no head on its way." : "On its way it skipped: " + string.Join("; ", skipped) + ".";

    /// <summary>Where an escalation came from — the app's stamp, never the sender's say-so.</summary>
    public static string EscalationFrom(string sender, string unit, string? mandate, string when) =>
        $"From {sender} in {unit}, {when}" + (mandate is null ? ", about no mandate." : $", about mandate {mandate}.");

    /// <summary>One owed word's addressee and receipt.</summary>
    public static string OwedTo(string addressee, string when) => $"To {Whom(addressee)}, received {when}.";

    /// <summary>An addressee in the owner's words: a council role by its title ("the Operations Director"), any other by its id.</summary>
    static string Whom(string addressee) => CouncilRoles.Title(addressee) is var title && title != addressee ? "the " + title : addressee;

    /// <summary>Why a word is still owed: the block's own reason (the app's), a launch that holds it, or the next turn.</summary>
    public static string OwedWhy(string addressee, string? blocked, string? takenBy) =>
        blocked is { Length: > 0 } why ? $"Not delivered: {why}"
        : takenBy is { Length: > 0 } turn ? $"Taken by launch {turn}; its turn has not answered yet."
        : $"Waiting for {Whom(addressee)}'s next turn.";

    /// <summary>An owed word whose payload is not one this build writes: named, never guessed at.</summary>
    public const string OwedUnreadable = "These words cannot be read as TradeAgent writes them.";

    /// <summary>A power proposal: who proposed a change to what, when.</summary>
    public static string PowerFrom(string seat, string target, string when) => $"{seat} proposes a change to {BoardPowerTarget(target)}, {when}.";

    /// <summary>Under every power proposal (CHANGE-001: the owner acts on it, or no one does).</summary>
    public const string PowerAppliedByNothing = "Applied by nothing: it is a record for you to act on, or no one does.";

    // ---- the veto of a board act ------------------------------------------------------------------------------------------------

    /// <summary>One field of a veto's preview: its value now and what the veto would set, the app's figures.</summary>
    public static string VetoField(string field, string now, string restored) =>
        $"{BoardField(field)?.Label ?? field}: {now} → {restored}";

    /// <summary>A board act's title on the owner's page and the Board page.</summary>
    public static string BoardActTitle(string id, string kind, string seat, string when) =>
        $"Act {id} — {(kind == "narrow" ? "a narrowing" : "a proposal")} by {seat}, {when}";

    // ---- the veto of a structural change ----------------------------------------------------------------------------------------

    public const string VetoStructurePress = "Veto this change";
    public const string VetoStructureConfirm = "Veto it — the chart is restored, nothing is granted";

    /// <summary>A structural event's title: what was decided, by whom, when — the app's record.</summary>
    public static string StructureEventTitle(long id, string kind, string unit, string? position, string decider, string when) =>
        $"{StructureKind(kind)} — {(position is null ? unit : $"{position} in {unit}")}, decided by {decider}, {when} "
        + $"(event {id.ToString(CultureInfo.InvariantCulture)})";

    static string StructureKind(string kind) => kind switch
    {
        "chartered" => "A unit chartered",
        "closed" => "A unit closed",
        "retired" => "A position retired",
        "hired" => "A position hired",
        _ => $"An event this version has no words for (“{kind}”)"
    };

    // ---- the owner's activation, his until the board has an act for it ------------------------------------------------------------

    public const string ActivatePositionPress = "Activate";
    public const string ActivatePositionConfirm = "Activate it — the activation gate still decides every launch";

    public const string ActivateIsYoursUntilTheBoard =
        "Activating a dormant position is " + YoursUntilTheBoardDecidesIt + ". Activating starts nothing: no seat on a real model runs "
        + "before a sealed workspace is proven on this computer.";

    /// <summary>A dormant position as the activation list names it.</summary>
    public static string DormantPosition(string id, string unit) => $"{id}, dormant in {unit}";

    // ---- the board's acts, on the Board page ------------------------------------------------------------------------------------

    public const string BoardActs = "The board's acts";
    public const string BoardActsNone = "No board act yet.";

    /// <summary>An act's evidence class; null is "none yet" — never inferred (EVID-001).</summary>
    public static string BoardActEvidenceClass(string? evidenceClass) =>
        evidenceClass is { Length: > 0 } c ? $"Evidence class: {c}" : "Evidence class: none yet";

    /// <summary>The audit line's score of an act: <c>U-org-audit-b</c>'s, not this build's.</summary>
    public const string BoardActAuditScore = "Audit score: not scored yet";

    /// <summary>
    /// One event of an act, the app's record of it. <paramref name="chiefAct"/>: the act changes the chief (<c>org.chief</c>), which writes
    /// NO policy version — its event's version is the one in force it was DECIDED UNDER (U-org-chief, the orchestrator's ruling), and is
    /// worded so, never as written (fix pass 1; Codex P2).
    /// </summary>
    public static string BoardActEventLine(string kind, string seat, string when, string? proposerFamily, string? deciderFamily,
        bool? sameFamily, int? version, bool chiefAct, string? detail)
    {
        var families = (proposerFamily, deciderFamily) switch
        {
            ({ } p, { } d) => $"; proposer's vendor family {p}, decider's {d}"
                              + (sameFamily switch { true => " — the same family", false => " — different families", _ => "" }),
            _ => ""
        };
        return $"{Capital(kind)} by {seat}, {when}{families}"
               + (version is { } v
                   ? chiefAct
                       ? $"; decided under version {v.ToString(CultureInfo.InvariantCulture)}; no policy version written"
                       : $"; it wrote the policy's version {v.ToString(CultureInfo.InvariantCulture)}"
                   : "")
               + (detail is { Length: > 0 } d2 ? $". {d2}" : ".");
    }

    /// <summary>A board forecast's resolution — the app's scoring — or that it is not resolved yet.</summary>
    public static string BoardForecastResolution(string? outcome, string? why, string? when, bool scored, string? unscoredWhy) =>
        outcome is { } o ? $"Resolved {o}, {when}: {why}"
        : !scored && unscoredWhy is { Length: > 0 } u ? $"Not scored: {u}"
        : "Not resolved yet.";

    static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // ---- ventures ---------------------------------------------------------------------------------------------------------------

    public const string Ventures = "Ventures";
    public const string VenturesNone = "No venture yet — the board approves them once they exist";

    // ---- the board's composition and budget, the owner's ------------------------------------------------------------------------

    public const string BoardCompositionTitle = "The security board's seats and budget";

    public const string BoardCompositionIsYours =
        "The board's seats and its share of your plan's day are yours, never the board's own: a seat may only propose a change "
        + "to them. Founding a seat starts nothing — it is dormant until a sealed workspace is proven; the board stays on at least two "
        + "model classes. Activating a seat is refused, in words, until this computer could seat it; its second press says what an "
        + "active seat changes and what stays yours.";

    public const string SetBoardBudgetShareConfirm = "Press again to set the board's share";
    public const string FoundBoardSeatConfirm = "Press again to found the seat, dormant";
    public const string RetireBoardSeatConfirm = "Press again to retire the seat — it acts no more";

    /// <summary>The activation's armed press (<c>U-board-alive</c> item 1); <see cref="ActivateBoardSeatWhat"/> shows beside it, in full.</summary>
    public const string ActivateBoardSeatConfirm = "Press again to activate the seat, as written below";

    /// <summary>
    /// WHAT THE ACTIVATION DOES AND DOES NOT CHANGE, IN FULL, shown while its press is armed (<c>U-board-alive-2</c>; the orchestrator's ruling
    /// of 2026-11-13: activating a board seat takes nothing from the owner that the board has no act for). Each clause is code on main:
    /// a narrowing is one seat's act and a widening two seats' (<c>BoardActs</c>); the chief's appointment and replacement leave his presses
    /// from the first active seat (<c>OrgStore.Chief</c>); a seat's turns are paid from the board's share; real money stays closed at
    /// the gateway; retiring is refused while the seats left would stand on fewer than two model classes (<c>BoardStore</c>).
    /// </summary>
    public const string ActivateBoardSeatWhat =
        "Activating this seat lets it act whenever a proposal or a position's request wakes it, and its turns are paid from the board's "
        + "share of your plan's day. What changes: an active seat may tighten a limit or an allowance on its own, and loosening one takes "
        + "two active seats; appointing and replacing the chief become the board's, by two seats — so while only this one seat is active, no "
        + "one appoints or replaces the chief. What stays as it is: real money stays "
        + "closed; you keep your veto, the board's seats and share, removing the chief, activating a dormant position or the chief, what "
        + "each seat runs on, and reopening a held paper scope. The way back is retiring the seat, which is refused while the seats left "
        + "would stand on fewer than two model classes.";
    public const string BoardShareLabel = "The board's share of the plan's day";
    public const string BoardVendorFamilyPlaceholder = "e.g. openai";
}

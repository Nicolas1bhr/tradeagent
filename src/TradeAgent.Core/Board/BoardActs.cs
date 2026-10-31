using TradeAgent.Core.Db;

namespace TradeAgent.Core.Board;

/// <summary>The two kinds of board act (<c>U-board-b1</c>; R24 § 4.2): one seat narrows at once; a widening is proposed for a second seat.</summary>
public static class BoardActKind
{
    /// <summary>Applied in its own write by the one seat that made it: every change narrows (or leaves alone).</summary>
    public const string Narrow = "narrow";

    /// <summary>Applies nothing until a DIFFERENT active seat confirms it, naming its intent's hash, before it expires.</summary>
    public const string Propose = "propose";
}

/// <summary>What happens to an act, each an append-only event (<c>board_act_event</c>).</summary>
public static class BoardActEventKind
{
    public const string Proposed = "proposed", Applied = "applied", Confirmed = "confirmed", Refused = "refused", Expired = "expired",
        Stale = "stale", Vetoed = "vetoed";

    /// <summary>The events that close an act: after one, no seat decides it.</summary>
    public static readonly string[] Closing = [Applied, Confirmed, Expired, Stale, Vetoed];
}

/// <summary>An act's state, as its latest closing event says it — or <see cref="Open"/>: a proposal no seat has closed.</summary>
public static class BoardActStatus
{
    public const string Open = "open";
}

/// <summary>
/// THE BOARD'S APP CONSTANTS (<c>U-board-b1</c>) — never policy data: a value the board's own acts could change would be the board
/// widening its own power (CHANGE-001).
/// </summary>
public static class BoardLimits
{
    /// <summary>How long a proposal waits for a second seat before it expires, applying nothing.</summary>
    public static readonly TimeSpan ConfirmationLife = TimeSpan.FromHours(24);

    /// <summary>The longest rationale, a seat's own words.</summary>
    public const int MaxRationaleChars = 4000;

    /// <summary>The most forecasts one act carries.</summary>
    public const int MaxForecasts = DecisionLimits.MaxForecasts;

    /// <summary>The longest text of a power proposal.</summary>
    public const int MaxPowerTextChars = 4000;

    /// <summary>
    /// HOW MANY PETITIONS ONE POSITION FILES IN ANY 24 HOURS (<c>U-board-alive</c>): each one buys a board seat's strong-model turn on the
    /// board's own budget, so a position cannot spend the board's day by asking. An app constant (CHANGE-001: the board does not set it).
    /// </summary>
    public const int PetitionsPerDay = 3;
}

/// <summary>
/// A POSITION'S PETITION TO THE BOARD (<c>board_petition</c>, rung 53; <c>U-board-alive</c>): the position (the chief included), its launch,
/// the intent and its hash as the app parsed them, its direction, the version in force it was read against, the position's words — a
/// claim — the one active seat the app woke for it, and when. It applies nothing.
/// </summary>
public sealed record BoardPetitionRow(string Id, string Position, string? Attempt, string Intent, string IntentSha256, string Direction,
    int PolicyVersion, string Rationale, string RoutedTo, DateTimeOffset At);

/// <summary>
/// WHAT A SEAT MAY PROPOSE OF THE BOARD'S OWN POWER (<c>U-board-b1</c>; R24 § 4.4, CHANGE-001) — a closed list. Each proposal is a record
/// shown to the owner; he acts on it in-process, or no one does.
/// </summary>
public static class BoardPowerTarget
{
    public const string Composition = "composition", BoardBudget = "board-budget", AuditBudget = "audit-budget", Connection = "connection",
        FoundingPolicyShape = "founding-policy-shape", Kernel = "kernel";

    public static readonly string[] All = [Composition, BoardBudget, AuditBudget, Connection, FoundingPolicyShape, Kernel];

    public static bool IsKnown(string? target) => target is not null && Array.IndexOf(All, target) >= 0;
}

/// <summary>Who decided a policy version (<c>board_policy.decider</c>).</summary>
public static class BoardDecider
{
    /// <summary>The app's founding record — never attributed to the owner.</summary>
    public const string App = "app";

    /// <summary>An act of active board seats: one narrowing, or a proposal and another seat's confirmation.</summary>
    public const string Board = "board";

    /// <summary>The owner's veto, which only ever narrows (R24 Q2).</summary>
    public const string Owner = "owner";
}

/// <summary>On what basis a policy version stands (<c>board_policy.basis</c>): founding, an act, or the owner's veto of one.</summary>
public static class BoardBasis
{
    public const string Founding = "founding";
    public const string Act = "act";
    public const string Veto = "veto";
}

/// <summary>
/// ONE ACT AS ITS ROW HOLDS IT: its id, kind, the seat and launch that made it, the exact intent and that text's hash, the version it
/// was made against, the rationale (a CLAIM, the seat's words), the refs the app checked, the evidence class (null: no act of this
/// build allocates), the expiry of a proposal, and when.
/// </summary>
public sealed record BoardActRow(
    string Id, string Kind, string Seat, string? Attempt, string Intent, string IntentSha256, int PolicyVersion, string Rationale,
    IReadOnlyList<OrgRef> Refs, string? EvidenceClass, DateTimeOffset? ExpiresAt, DateTimeOffset At);

/// <summary>
/// ONE EVENT OF AN ACT: its kind, the seat or principal (<c>app</c>, <c>owner</c>) that did it, the launch, its own sealed rationale
/// (a decision's), the two seats' vendor families and whether they were one, the version it wrote, and the app's own words.
/// </summary>
public sealed record BoardActEventRow(
    long Id, string ActId, string Kind, string Seat, string? Attempt, string? Rationale, string? ProposerFamily, string? DeciderFamily,
    bool? SameFamily, int? PolicyVersion, string? Detail, DateTimeOffset At);

/// <summary>One forecast a board seat attached to its act — <c>org_forecast</c>'s shape; the probability is its claim, the scoring the app's.</summary>
public sealed record BoardForecastRow(
    string Id, string ActId, string Forecaster, string? Attempt, int Vocabulary, string Predicate, string Subject, string Target,
    DateTimeOffset ResolveBy, decimal Probability, bool Scored, string? UnscoredWhy, DateTimeOffset At);

/// <summary>
/// ONE ACT WHOLE, AS THE OWNER SEES IT (<c>BoardReader.Acts</c>, <c>Act</c> — the seam with <c>U-org-surface-b</c>): the row, its status,
/// every event with every seat's rationale, the forecasts and their resolutions. Never shown to a seat that has not sealed its own decision.
/// </summary>
public sealed record BoardActRecord(
    BoardActRow Act, string Status, IReadOnlyList<BoardActEventRow> Events, IReadOnlyList<BoardForecastRow> Forecasts,
    IReadOnlyList<ResolutionRow> Resolutions);

/// <summary>What an act, a decision or an expiry did: the act, its status now, the version it wrote (or null), and the app's words.</summary>
public sealed record BoardActResult(BoardActRow Act, string Status, int? PolicyVersion, string Words);

/// <summary>
/// AN ACT AS ITS SEAT STATES IT, before the app has checked it: the seat (the caller the broker proved), its launch, the intent's JSON,
/// the rationale, the refs and the forecasts — all claims until the store has checked each.
/// </summary>
public sealed record BoardActRequest(
    string Seat, string? Attempt, string Intent, string Rationale, IReadOnlyList<OrgRef> Refs, IReadOnlyList<ForecastClaim> Forecasts);

/// <summary>
/// ONE OPEN ACT AS A SEAT THAT HAS NOT SEALED ITS DECISION SEES IT — THE BLIND READ (R24 § 4.2, VISION § 5.4's shadow rule): the intent,
/// its hash and direction, the refs, the app's own figures in force for each field the intent names, the version they are read from,
/// and the expiry. NEVER the proposer's rationale or forecasts, nor any other seat's rationale: there is no member here to hold them.
/// </summary>
public sealed record BoardActBlindView(
    string Id, string Kind, string Seat, string Status, string Intent, string IntentSha256, string Direction, IReadOnlyList<OrgRef> Refs,
    IReadOnlyList<BoardPolicyField> InForce, int InForceVersion, DateTimeOffset? ExpiresAt, DateTimeOffset At);

/// <summary>A proposal of the board's own power (<c>board_power_proposal</c>): the target, the seat's text — a claim — and when. Applied by nothing.</summary>
public sealed record BoardPowerProposalRow(string Id, string Seat, string? Attempt, string Target, string Text, DateTimeOffset At);

/// <summary>One field of the owner's veto, before his second press: its value now, the act's <c>from</c>, and what the veto would set.</summary>
public sealed record BoardVetoField(string Field, string Now, string From, string Restored);

/// <summary>
/// WHAT THE OWNER'S VETO WOULD DO (press one, <c>PreviewBoardVeto</c>): close an open proposal, write a version decided by him whose
/// every field the act changed is the narrower of now and before, record a veto with nothing narrower to restore — or be refused, in
/// words (a narrowing act: restoring it would widen). Reads only. <paramref name="Status"/> is the act's state the preview was read at.
///
/// <para><b>Press two is about what press one showed</b> (<c>U-org-surface-b</c> fix pass 1): the owner's second press hands this preview
/// back, and <see cref="BoardStore.VetoBoardAct"/> refuses as moved — nothing written — when the veto it would write now is not the one
/// shown (<see cref="WhatMoved"/>): a proposal confirmed between his presses is never vetoed with an effect he never saw.</para>
/// </summary>
public sealed record BoardVetoPreview(string ActId, string Status, bool Allowed, string Words, IReadOnlyList<BoardVetoField> Fields)
{
    /// <summary>
    /// What differs between this preview — the one shown at press one — and <paramref name="now"/>, field by field, in words; or null
    /// when the veto would do exactly what was shown: the same act, in the same state, accepted or refused alike, with the same words and
    /// the same value now, <c>from</c> and restored value for every field, in order.
    /// </summary>
    public string? WhatMoved(BoardVetoPreview now)
    {
        var moved = new List<string>();
        if (!string.Equals(ActId, now.ActId, StringComparison.Ordinal)) moved.Add($"the preview shown was of {ActId}, not {now.ActId}");
        if (!string.Equals(Status, now.Status, StringComparison.Ordinal)) moved.Add($"it was {Status} and is {now.Status} now");
        if (Allowed != now.Allowed) moved.Add(now.Allowed ? "a veto was refused and is accepted now" : $"a veto is refused now: {now.Words}");
        if (!Fields.SequenceEqual(now.Fields)) moved.Add($"what the veto restores was {Effect(Fields)} and is {Effect(now.Fields)} now");
        if (moved.Count == 0 && !string.Equals(Words, now.Words, StringComparison.Ordinal))
            moved.Add($"what the veto does was “{Words}” and is “{now.Words}” now");
        return moved.Count == 0
            ? null
            : $"{ActId} moved since your first press: {string.Join("; ", moved)}. Its card shows what a veto would do now — two presses veto that.";
    }

    static string Effect(IReadOnlyList<BoardVetoField> fields) => fields.Count == 0
        ? "nothing"
        : string.Join(", ", fields.Select(f => $"{f.Field} {f.Now} → {f.Restored}"));
}

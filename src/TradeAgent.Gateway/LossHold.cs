using System.Globalization;
using TradeAgent.Core;

namespace TradeAgent.Gateway;

/// <summary>
/// A SCOPE THAT HAS BREACHED TWICE IN A WEEK IS NOT REOPENED BY CODE — the strike count, the hold
/// it writes, the owner's release, and the one bounded extension a hold may carry.
///
/// <para><b>Why a second breach is different from the first.</b> <c>U-reopen-1</c> reopens every
/// closure once it has earned it: the time served, the book read flat, nothing unaccounted for. That
/// is the right answer to ONE bad day — the budget did what the owner set it to do and the account
/// goes back to work. It is the wrong answer to a strategy or a market that is going through the
/// budget repeatedly: reopening by code every day spends one daily budget a day, for ever, and
/// nobody is ever asked whether that is what should be happening.</para>
///
/// <para><b>The count is taken ONCE, at confirmation, and written down.</b> Not at reopening. A
/// window measured when the closure is asked to lift would let a strike age out while the scope sat
/// closed — the closure itself buys the time that makes the earlier breach fall out of the window,
/// so the worse the run, the sooner the software would stop noticing it. The hold is therefore a
/// ROW, written under the dispatch gate in the same pass that writes the breach, naming the episodes
/// it counted; everything afterwards reads the row.</para>
///
/// <para><b>What a hold is, and what it is not.</b> It removes the CODE path out of a closure and
/// nothing else: the refusals, the flatten and every other rule are <c>U-flatten-1</c>'s and
/// <c>U-reopen-1</c>'s, unchanged. The way out is the owner's own release on the Safety page, and a
/// release lifts the HOLD only — the receipt still needs the eligibility instant, the fresh flat
/// read and the honest clock. Nothing an agent can reach writes, lifts or asks for any of it.</para>
/// </summary>
public static class LossHold
{
    /// <summary>Every hold starts here, so a scan can find them all.</summary>
    public const string Prefix = "loss_hold:";

    /// <summary>Every release starts here.</summary>
    public const string ReleasePrefix = "loss_release:";

    /// <summary>Every bounded extension starts here. See <see cref="LossExtensionRecord"/>.</summary>
    public const string ExtensionPrefix = "loss_extend:";

    /// <summary>
    /// The key one episode's row is filed under, whatever the family:
    /// <c>{prefix}{connector}:{account}[:{symbol}]:{utcDay}</c>. The day and the symbol come off the
    /// BREACH record and never off the clock, exactly as <see cref="LossReopen.KeyFor(string, LossBreachRecord)"/>
    /// does, because an episode that began on one day is decided on another.
    /// </summary>
    public static string KeyFor(string prefix, string connectorId, string account, string? symbol, string utcDay) =>
        symbol is null
            ? $"{prefix}{LossReopen.Scope(connectorId, account)}:{utcDay}"
            : $"{prefix}{LossReopen.Scope(connectorId, account)}:{symbol}:{utcDay}";

    /// <summary>The hold on one breach record.</summary>
    public static string HoldKey(string connectorId, LossBreachRecord breach)
    {
        ArgumentNullException.ThrowIfNull(breach);
        return KeyFor(Prefix, connectorId, breach.Account, breach.Symbol, breach.Day);
    }

    /// <summary>The owner's release of one breach record's hold.</summary>
    public static string ReleaseKey(string connectorId, LossBreachRecord breach)
    {
        ArgumentNullException.ThrowIfNull(breach);
        return KeyFor(ReleasePrefix, connectorId, breach.Account, breach.Symbol, breach.Day);
    }

    /// <summary>The one bounded extension of one breach record's closure.</summary>
    public static string ExtensionKey(string connectorId, LossBreachRecord breach)
    {
        ArgumentNullException.ThrowIfNull(breach);
        return KeyFor(ExtensionPrefix, connectorId, breach.Account, breach.Symbol, breach.Day);
    }

    /// <summary>
    /// THE FIRST UTC DATE STILL INSIDE THE STRIKE WINDOW at <paramref name="at"/> — the window is
    /// <paramref name="days"/> UTC DATES, today and the ones before it, and never a rolling span of
    /// hours. A breach at 23:58Z and one at 00:02Z are two days apart on any clock a person reads,
    /// and every other figure in this product that says "day" means a UTC date.
    /// </summary>
    public static string WindowFrom(DateTimeOffset at, int days) =>
        LossBreach.Stamp(at.AddDays(-(Math.Max(days, 1) - 1)));

    /// <summary>Whether one breach record's UTC day is inside the window that ends at <paramref name="at"/>.</summary>
    public static bool InWindow(string day, DateTimeOffset at, int days) =>
        days > 0
        && string.CompareOrdinal(day, WindowFrom(at, days)) >= 0
        && string.CompareOrdinal(day, LossBreach.Stamp(at)) <= 0;

    /// <summary>
    /// THE SENTENCE A HOLD IS RECORDED WITH, AND SHOWN WITH, EVERYWHERE — written once, onto the
    /// row, at the moment the second breach is confirmed.
    ///
    /// <para>On the record rather than recomposed per surface, for the reason
    /// <see cref="LossBreach.DaySentence"/> is: it names the episodes that were counted and the
    /// window they were counted in, and a sentence rebuilt later would count again against a window
    /// that has since moved. Three things have to be in it — that this is the second, WHICH days,
    /// and that the way out is the owner's own press and not the passing of time, because an owner
    /// waiting for software that is waiting for them is the failure this sentence prevents.</para>
    /// </summary>
    public static string Sentence(string? symbol, IReadOnlyList<string> episodes, int windowDays,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        var what = symbol is null ? "your account" : symbol;
        return $"TradeAgent will NOT reopen {what} by itself: it has now reached your loss budget "
               + $"{Count(episodes.Count)} in {Days(windowDays)} — {string.Join(", ", episodes)} — and a scope that "
               + "keeps reaching the budget is a strategy or a market for you to look at rather than "
               + $"something to let back in every day. It has been held for review since "
               + $"{at.UtcDateTime:yyyy-MM-dd HH:mm} UTC. It stays closed until you release it yourself on the "
               + "Safety page, and releasing it lifts this hold only — the closure still has to run its "
               + "time and TradeAgent still has to see your book flat before anything trades.";
    }

    /// <summary>The sentence the owner's release is recorded with, in the owner's own note.</summary>
    public static string ReleaseSentence(string? symbol, string note, DateTimeOffset at,
        IReadOnlyList<string> episodes)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        var what = symbol is null ? "your account" : symbol;
        return $"You released the review hold on {what} at {at.UtcDateTime:yyyy-MM-dd HH:mm} UTC, "
               + $"acknowledging {string.Join(", ", episodes)}, with this note: “{note}”. The hold is lifted "
               + "and nothing else is: TradeAgent still reopens the closure itself, once it has run its "
               + "time and a fresh reading of your platform shows nothing open.";
    }

    /// <summary>"twice", "three times", and a number after that. The owner's words, not a counter.</summary>
    static string Count(int n) => n switch
    {
        <= 2 => "twice",
        3 => "three times",
        4 => "four times",
        _ => $"{n.ToString(CultureInfo.InvariantCulture)} times"
    };

    /// <summary>"a week" where the window is seven dates, and the number of days otherwise.</summary>
    public static string Days(int days) => days switch
    {
        1 => "one day",
        7 => "a week",
        _ => $"{days.ToString(CultureInfo.InvariantCulture)} days"
    };
}

/// <summary>
/// WHAT THE OWNER'S RELEASE PRESS DID: whether anything was released, the keys written, and the
/// sentence the card shows either way.
///
/// <para>A refusal is words and not an exception, for the reason <c>AllocationResult</c> is: the
/// caller is a button on a screen, and a person who pressed something twice has to read why it did
/// nothing rather than watch a dialog appear.</para>
/// </summary>
public sealed record LossReleaseResult(bool Ok, string Why, IReadOnlyList<string> Released);

/// <summary>
/// THE WHOLE OF WHAT THE SURFACES SAY ABOUT A CLOSURE ENDING, from one reading of the rows this app
/// wrote — the earliest instant, what is holding it, whether a person has to look at it, when one
/// last released one, and under which rule the standing closure is being judged.
///
/// <para>A record rather than a tuple because it is read by four surfaces (the status, the
/// Situation, the Safety page and the daily report) and each of them shows a different subset: a
/// positional tuple of nine would have every call site naming fields by index, and the one that got
/// it wrong would say "reopens at" where "held for review" belongs.</para>
///
/// <para><see cref="At"/> is the EARLIEST and never a promise — no surface takes the fresh platform
/// read a reopen also needs — and it is ABSENT whenever anything other than the passing of time is
/// in the way, which is when <see cref="Held"/> carries the sentence.</para>
/// </summary>
public sealed record LossReopenReading
{
    /// <summary>The earliest instant the governing closure may be lifted, or null. See the summary.</summary>
    public DateTimeOffset? At { get; init; }

    /// <summary>What is holding it, in one sentence, or null because nothing is.</summary>
    public string? Held { get; init; }

    /// <summary>When the last closure was lifted. ABSENT whenever anything is still closed.</summary>
    public DateTimeOffset? ReopenedAt { get; init; }

    /// <summary>The receipt's own sentence. Absent on the same rule as <see cref="ReopenedAt"/>.</summary>
    public string? ReopenedWhy { get; init; }

    /// <summary>
    /// THE HOLD'S OWN SENTENCE while a standing closure is held for the owner to look at, or null.
    ///
    /// <para>Separate from <see cref="Held"/>, which carries whatever is in the way including this
    /// one, because it is the only entry in that list that waiting does not fix: everything else
    /// resolves when a flatten finishes or a clock is put right, and this resolves when a person
    /// presses something. An agent and an owner both plan differently around the two.</para>
    /// </summary>
    public string? HeldForReview { get; init; }

    /// <summary>When the owner last released a hold on this account, or null because none was.</summary>
    public DateTimeOffset? ReleasedAt { get; init; }

    /// <summary>The release's own sentence, carrying the owner's note verbatim. Null while there is none.</summary>
    public string? ReleasedWhy { get; init; }

    /// <summary>
    /// THE RULE THE STANDING CLOSURE IS BEING JUDGED UNDER, in the owner's words — the closure
    /// length and the strike window SNAPSHOT onto that closure's own record, never the live setting.
    /// Null while nothing is closed.
    /// </summary>
    public string? Rule { get; init; }
}

/// <summary>
/// THE HOLD ITSELF — what was counted, when, and against which window. Written once and never
/// updated, like every other row in this family.
/// </summary>
public sealed record LossHoldRecord
{
    /// <summary>The account held. Off the BREACH record, never the currently selected one.</summary>
    public string Account { get; init; } = "";

    /// <summary>The platform the account is on. See <see cref="LossReopen"/> on why it is in the key.</summary>
    public string Connector { get; init; } = "";

    /// <summary>The breach's UTC day — the day this hold is filed under.</summary>
    public string Day { get; init; } = "";

    /// <summary>The symbol held, or null when it is the whole account.</summary>
    public string? Symbol { get; init; }

    /// <summary>The breach record this hold belongs to, so the episode reads end to end from here.</summary>
    public string BreachKey { get; init; } = "";

    /// <summary>
    /// EVERY EPISODE COUNTED, oldest first, as <c>yyyy-MM-dd HH:mm UTC</c>. The evidence and not a
    /// number: an owner asked to look at a run of losing days has to be able to see which days.
    /// </summary>
    public IReadOnlyList<string> Episodes { get; init; } = [];

    /// <summary>The breach keys of those episodes, so a reader can go to each record.</summary>
    public IReadOnlyList<string> EpisodeKeys { get; init; } = [];

    /// <summary>
    /// The window this was counted in, IN UTC DATES, as it stood at confirmation — the snapshot rule
    /// the closure length follows. A window the owner shortens afterwards does not lift a hold that
    /// has been written, and one they lengthen does not manufacture a strike that was not counted.
    /// </summary>
    public int StrikeWindowDays { get; init; }

    /// <summary>The first UTC date the window covered. On the row so the arithmetic is checkable.</summary>
    public string WindowFrom { get; init; } = "";

    /// <summary>When this hold was written: the instant the second breach was confirmed.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>The sentence the owner and the agent are shown. Written once, with the row.</summary>
    public string Why { get; init; } = "";
}

/// <summary>
/// THE OWNER'S RELEASE OF ONE HOLD — the only thing in this product that lifts one, written once by
/// a two-press card on the Safety page and by nothing else.
///
/// <para>There is no verb, no pipe op and no setting that reaches it: <c>CLAUDE.md</c>'s rule that
/// operator authority is in-process only, applied to the one permission this unit adds. The note is
/// REQUIRED because this row is the durable trace of a person overruling the software's own refusal
/// to let an account back in, and a blank one turns that trace into a timestamp.</para>
/// </summary>
public sealed record LossReleaseRecord
{
    public string Account { get; init; } = "";

    public string Connector { get; init; } = "";

    /// <summary>The mode the release was made in. PAPER and LIVE are different money.</summary>
    public TradingMode Mode { get; init; }

    public string Day { get; init; } = "";

    public string? Symbol { get; init; }

    /// <summary>The breach record whose hold this releases.</summary>
    public string BreachKey { get; init; } = "";

    /// <summary>The hold row this releases, so the two read as one episode.</summary>
    public string HoldKey { get; init; } = "";

    /// <summary>The episodes the owner acknowledged — copied off the hold, never recounted here.</summary>
    public IReadOnlyList<string> Episodes { get; init; } = [];

    /// <summary>What the owner typed. Required, never empty, and quoted verbatim on every surface.</summary>
    public string Note { get; init; } = "";

    /// <summary>When the second press landed.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>
    /// The closure length and strike window in force when the release was made, so the report can
    /// say which rule the episode was judged under even after the owner has changed them.
    /// </summary>
    public TimeSpan MinClosure { get; init; }

    public int StrikeWindowDays { get; init; }

    /// <summary>The sentence shown everywhere, carrying the owner's own note.</summary>
    public string Why { get; init; } = "";
}

/// <summary>
/// A BOUNDED EXTENSION OF ONE CLOSURE — at most one per episode, of at most one closure length, and
/// NEVER a hold without an end.
///
/// <para><b>Why this type exists at all.</b> A loss-budget breach opens a
/// <c>BoundaryKind.LossBudget</c> boundary whose default disposition is <c>hold</c>, and
/// <c>CouncilBoundaries.ApplyDue</c> writes that default onto the row when the deadline passes. The
/// obvious thing to do with it — read the boundary's <c>hold</c> as holding the closure — is a
/// closure that never ends: the disposition is written once, by policy, and nothing in the protocol
/// ever changes it afterwards. So the boundary's disposition holds NOTHING, and the only way an
/// episode's eligibility instant can move at all is a row of this shape, whose <see cref="Until"/>
/// is an END the code computes and clamps.</para>
///
/// <para><b>What is NOT here, stated rather than implied.</b> Nothing in this build writes one. A
/// director's assessment is free markdown (<c>PublicationKind.Assessment</c>, an
/// <c>assessment-*.md</c> file capped in lines) and <c>CouncilBoundaries</c> deliberately exposes no
/// method that takes a disposition from a director at all — so there is no structured way for one to
/// ask for more time, and scraping a phrase out of a director's prose would be an agent moving a
/// money-path state by writing words in a document, which is exactly what <c>AGENTS.md</c> says the
/// inbox may not do. The BOUND is implemented and the request channel is not: a row that arrives
/// here from anywhere is clamped, applied once, voided by a release, and always shown with its
/// end.</para>
/// </summary>
public sealed record LossExtensionRecord
{
    public string Account { get; init; } = "";

    public string Connector { get; init; } = "";

    public string Day { get; init; } = "";

    public string? Symbol { get; init; }

    /// <summary>The breach record whose closure this extends.</summary>
    public string BreachKey { get; init; } = "";

    /// <summary>The boundary row this was read from, so a reader can go to the post-mortem.</summary>
    public string BoundaryId { get; init; } = "";

    /// <summary>
    /// THE END. A row without one extends nothing — that is the whole rule, and it is checked by
    /// <c>TradingGateway</c> before the instant is used, never assumed by the writer.
    /// </summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>When the extension was written.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>Why, in the words a surface shows. Always names the end.</summary>
    public string Why { get; init; } = "";
}

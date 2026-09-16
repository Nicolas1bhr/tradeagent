using TradeAgent.Core;

namespace TradeAgent.Gateway;

/// <summary>
/// HOW A CLOSED SCOPE IS LET BACK IN — the receipt that ends a closure, the instant it may earliest
/// be written, and the clock mark that stops a moved clock writing it early.
///
/// <para><b>The closure used to end because the key changed.</b> <c>U-flatten-1</c> filed a breach
/// under <c>loss_breach:{account}:{utcDay}</c> and every reader asked for TODAY's key, so a breach
/// confirmed at 23:58Z was gone two minutes later: the account came back to a book it had just been
/// flattened out of, with nothing checking that the flatten had resolved, that the book was flat,
/// that the clock had moved forward honestly, or that any time had passed at all. Nothing recorded
/// that a reopen had happened either, because nothing had happened — a key had merely gone out of
/// scope.</para>
///
/// <para><b>So the closure is a STATE and the reopen is an EVENT.</b> A scope is closed from the
/// breach record's <c>ConfirmedAt</c> until a receipt exists for that record, whatever day it is;
/// and the receipt is a row this app writes, once, with the evidence it wrote it on. The two halves
/// are deliberately asymmetric: the closure needs nothing to persist and the reopen needs a
/// positive act, so every failure mode — a killed process, an unreadable row, a clock nobody can
/// trust, a flatten with a leg still flagged — leaves the scope closed rather than open.</para>
///
/// <para><b>Keyed by the account AND the platform it belongs to</b>, exactly as
/// <see cref="LossFlatten"/> is and for the same reason: an account id is unique only WITHIN a
/// platform, switching platforms builds a new gateway over the SAME database, and a PAPER reopen
/// must never be able to answer for a LIVE closure. The BREACH's key is unchanged — it is
/// <c>U-flatten-1</c>'s and nothing rewrites it — so the receipt carries the day and symbol OF THE
/// BREACH IT RELEASES rather than of the day it was written on, which is how a breach confirmed on
/// one day is released on another.</para>
/// </summary>
public static class LossReopen
{
    /// <summary>Every receipt starts here, so a scan can find them all.</summary>
    public const string Prefix = "loss_reopen:";

    /// <summary>The account as this app identifies one: the platform it is on, and then its id.</summary>
    public static string Scope(string connectorId, string account) =>
        $"{LossBreach.Part(connectorId)}:{LossBreach.Part(account)}";

    /// <summary>The day's own receipt: <c>loss_reopen:{connector}:{account}:{utcDay}</c>.</summary>
    public static string DayKey(string connectorId, string account, string utcDay) =>
        $"{Prefix}{Scope(connectorId, account)}:{utcDay}";

    /// <summary>One symbol's: <c>loss_reopen:{connector}:{account}:{symbol}:{utcDay}</c>.</summary>
    public static string SymbolKey(string connectorId, string account, string symbol, string utcDay) =>
        $"{Prefix}{Scope(connectorId, account)}:{LossBreach.Part(symbol)}:{utcDay}";

    /// <summary>
    /// THE RECEIPT THAT RELEASES ONE BREACH RECORD — the day and symbol come off the RECORD, never
    /// off the clock, because a breach confirmed on one day is released on a later one.
    /// </summary>
    public static string KeyFor(string connectorId, LossBreachRecord breach)
    {
        ArgumentNullException.ThrowIfNull(breach);
        return breach.Symbol is null
            ? DayKey(connectorId, breach.Account, breach.Day)
            : SymbolKey(connectorId, breach.Account, breach.Symbol, breach.Day);
    }

    /// <summary>The same, from a breach KEY, so a scan need not parse the row to ask.</summary>
    public static string KeyFor(string connectorId, string account, string utcDay, string? symbol) =>
        symbol is null ? DayKey(connectorId, account, utcDay) : SymbolKey(connectorId, account, symbol, utcDay);

    /// <summary>
    /// THE EARLIEST INSTANT A CLOSURE MAY BE LIFTED — a pure function of the immutable breach record
    /// and one duration, so every surface that shows it and the tick that acts on it compute the
    /// same number from the same evidence.
    ///
    /// <para>It is the LATER of two things. The first UTC midnight after the breach, because every
    /// figure this product calls "today" is a UTC day and a closure that ended inside its own day
    /// would hand the account back to the same ledger figure that closed it. And
    /// <c>ConfirmedAt + LossMinClosure</c>, because midnight alone is the defect this unit exists to
    /// close: a breach at 23:58Z is two minutes from a midnight, and two minutes is not a pause.</para>
    ///
    /// <para>At the 24 h this build fixes <see cref="GatewayOptions.LossMinClosure"/> at, the second
    /// term always wins — the next midnight is at most 24 h away. Both terms are kept because the
    /// owner's number is what changes (<c>U-reopen-2</c>), and a shorter one must still not be able
    /// to end a closure inside the UTC day that recorded it.</para>
    /// </summary>
    public static DateTimeOffset EligibleAt(DateTimeOffset confirmedAt, TimeSpan minClosure)
    {
        var midnight = new DateTimeOffset(confirmedAt.UtcDateTime.Date, TimeSpan.Zero).AddDays(1);
        var earliest = confirmedAt + minClosure;
        return midnight > earliest ? midnight : earliest;
    }

    /// <summary>
    /// THE SENTENCE A REOPEN IS RECORDED WITH, AND SHOWN WITH, EVERYWHERE — written ONCE, onto the
    /// receipt, at the moment it is written.
    ///
    /// <para>It is on the record rather than recomposed per surface for the reason the closure's own
    /// sentence is (<see cref="LossBreach.DaySentence"/>): a sentence recomposed later would quietly
    /// re-read state that has moved on, and say something the decision was not made on. Three things
    /// have to be in it — what was reopened and when, WHY it was allowed (the closure served, the
    /// book read flat, nothing left unresolved), and that TradeAgent did this by code with nobody
    /// pressing anything, because an owner who finds an account trading again and cannot see who
    /// decided that has been told nothing at all.</para>
    /// </summary>
    public static string Sentence(LossBreachRecord breach, DateTimeOffset eligible, DateTimeOffset at,
        TimeSpan minClosure)
    {
        ArgumentNullException.ThrowIfNull(breach);
        var what = breach.Symbol is null ? "your account" : breach.Symbol;
        return $"TradeAgent reopened {what} to new risk at {at.UtcDateTime:HH:mm} UTC on "
               + $"{at.UtcDateTime:yyyy-MM-dd}, by code and with nobody pressing anything. The loss budget was "
               + $"reached at {breach.ConfirmedAt.UtcDateTime:HH:mm} UTC on {breach.Day}; the closure ran the "
               + $"{Hours(minClosure)} it had to, a fresh reading of your platform showed nothing open on it, "
               + "and everything TradeAgent sent when it closed the book is accounted for. The earliest it could "
               + $"have been reopened was {eligible.UtcDateTime:yyyy-MM-dd HH:mm} UTC.";
    }

    /// <summary>
    /// THE OWNER'S NUMBER AS A DURATION. At or below zero is NO closure beyond the UTC day — the
    /// widest value the setting has — and <see cref="EligibleAt"/>'s midnight term is what is then
    /// left holding it, which is deliberate: the figure that closed the scope is a UTC day's, and a
    /// closure ending inside its own day hands the account back to the same figure.
    /// </summary>
    public static TimeSpan ClosureOf(decimal hours) =>
        hours <= 0m ? TimeSpan.Zero : TimeSpan.FromHours((double)hours);

    /// <summary>The closure length in the words an owner uses. Whole hours; anything else says both.</summary>
    public static string Hours(TimeSpan span) =>
        span.Minutes == 0 && span.Seconds == 0
            ? $"{span.TotalHours:0} hours"
            : $"{span.TotalHours:0.#} hours";

    /// <summary>
    /// A STEP OF THE CLOCK, IN THE UNIT THAT READS. <see cref="Hours"/> is the closure's own wording
    /// and is always hours, which turns a step of ninety seconds into "0 hours"; a clock step is
    /// anything from a minute to a year, so it says minutes below the hour and hours above it.
    /// </summary>
    public static string Step(TimeSpan span) =>
        span < TimeSpan.FromHours(1)
            ? $"{span.TotalMinutes:0.#} minutes"
            : $"{span.TotalHours:0.#} hours";

    /// <summary>
    /// THE MARK OF THE GATEWAY'S OWN CLOCK, while anything is closed:
    /// <c>loss_clock_high_water:{connector}:{account}</c>. It carries TWO readings of the same tick
    /// and the time this gateway has declined to count, and a closure is held against all three.
    ///
    /// <para>Every instant this unit reasons about comes from <c>GatewayOptions.Clock</c>, and a
    /// machine clock can be moved — by a person, by an NTP correction, by a restored image. The mark
    /// is on disk rather than in memory because a restart is the cheapest way to forget an in-memory
    /// one, and a restart is exactly what follows a clock change.</para>
    ///
    /// <para><b>A step FORWARD used to be ordinary, and it was not.</b> The mark refused a reading
    /// below itself and nothing else, so ONE forward step past the eligibility instant ended a
    /// closure on the very next tick with no time having passed at all: the mark rose, no suspect
    /// row was written, and the only remaining test — <c>at &lt; eligible</c> — had just been
    /// satisfied by the jump (REVIEW 2026-09-16, finding 6, probe <c>C4</c>). What both this comment
    /// and <c>docs/CONTRACTS.md</c> used to say — that a forward step "only ever leaves things
    /// closed longer" — was a claim about the code that the code did not keep.</para>
    ///
    /// <para><b>So the mark carries a MONOTONE reading beside the wall one</b>
    /// (<see cref="LossClockMark.Monotone"/>, from the same <see cref="TimeProvider"/> — in
    /// production <c>Stopwatch</c>'s counter, which nothing can set). Within one run the two have to
    /// move together: a wall step larger than the monotone elapse by more than
    /// <see cref="ForwardSlack"/> is a clock somebody moved, and it is refused and recorded exactly
    /// as a backward one is.</para>
    ///
    /// <para><b>Across a RESTART the two readings cannot be compared at all</b> — a monotone
    /// counter's origin is its own process's — so the mark names the run that wrote it
    /// (<see cref="LossClockMark.Run"/>) and the first tick of a new run writes no suspect row.
    /// What it does instead is decline to CREDIT the gap it did not see: the wall step across the
    /// downtime is added to <see cref="LossClockMark.Unverified"/>, and every closure standing at
    /// the time has its eligibility instant moved by that much. The closure is lengthened rather
    /// than shortened, which is this line's direction everywhere.</para>
    /// </summary>
    public static string ClockKey(string connectorId, string account) =>
        $"loss_clock_high_water:{Scope(connectorId, account)}";

    /// <summary>
    /// HOW FAR THE WALL CLOCK MAY RUN AHEAD OF THE MONOTONE READING BEFORE IT IS SUSPECT — four
    /// watch intervals, 60 s at the shipped 15 s.
    ///
    /// <para>It is a multiple of the watch's own interval rather than a constant because the thing
    /// being bounded is the gap between two consecutive ticks of THIS watch, and that is what the
    /// interval is. Four of them, so an ordinary late tick — a loaded machine, a slow platform read,
    /// a garbage collection — is not a clock anybody moved; and small enough that the smallest step
    /// worth making (an hour, to bring a 24 h closure inside a working day) is nowhere near it.</para>
    /// </summary>
    public static TimeSpan ForwardSlack(TimeSpan watchInterval) =>
        watchInterval > TimeSpan.Zero ? watchInterval * 4 : TimeSpan.FromSeconds(60);

    /// <summary>
    /// WRITTEN ONCE, THE FIRST TIME A TICK SEES A CLOCK IT CANNOT TRUST:
    /// <c>loss_clock_suspect:{connector}:{account}</c>. Once, because the condition repeats every
    /// tick for as long as the clock is wrong and a row per tick is a log, not a fact.
    /// </summary>
    public static string SuspectKey(string connectorId, string account) =>
        $"loss_clock_suspect:{Scope(connectorId, account)}";
}

/// <summary>
/// THE RECEIPT ITSELF — what was true at <see cref="At"/>, written once and never updated.
///
/// <para>It is the third record of one episode and the three say different things.
/// <see cref="LossBreachRecord"/> is the MEASUREMENT that closed the scope;
/// <see cref="LossFlattenRecord"/> is the CLAIM about what was then done to the book; this is the
/// DECISION to let the scope trade again, with the evidence the decision was made on. None of them
/// is a field on another, and nothing rewrites any of them.</para>
/// </summary>
public sealed record LossReopenRecord
{
    /// <summary>The account reopened — off the BREACH record, never the currently selected one.</summary>
    public string Account { get; init; } = "";

    /// <summary>The platform the account is on. See <see cref="LossReopen"/> on why it is in the key.</summary>
    public string Connector { get; init; } = "";

    /// <summary>The mode the reopen ran in, for a reader. PAPER and LIVE are different money.</summary>
    public TradingMode Mode { get; init; }

    /// <summary>The breach's UTC day, <c>yyyy-MM-dd</c> — the day this receipt is filed under.</summary>
    public string Day { get; init; } = "";

    /// <summary>The symbol this receipt reopens, or null when it is the whole account.</summary>
    public string? Symbol { get; init; }

    /// <summary>The breach record's own key, so the episode can be read end to end from here.</summary>
    public string BreachKey { get; init; } = "";

    /// <summary>When the breach that closed this scope was confirmed.</summary>
    public DateTimeOffset ConfirmedAt { get; init; }

    /// <summary>
    /// The instant computed from the record — <see cref="LossReopen.EligibleAt"/> — and the closure
    /// length it was computed with. Both are on the row because the option may change afterwards and
    /// a reader has to be able to check the arithmetic that was actually done.
    /// </summary>
    public DateTimeOffset EligibleAt { get; init; }

    public TimeSpan MinClosure { get; init; }

    /// <summary>
    /// The strike window, IN UTC DATES, that this episode was counted against — the second half of
    /// the rule, on the row for the reason the first half is: the owner may change it afterwards and
    /// a reader has to be able to see which number the decision was actually made under.
    /// </summary>
    public int StrikeWindowDays { get; init; }

    /// <summary>When the tick wrote this receipt. Never earlier than <see cref="EligibleAt"/>.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>
    /// The clock's own high-water mark as this tick left it. A reopen is refused while the clock
    /// reads below it, so the mark is on the row as the evidence that it did not.
    /// </summary>
    public DateTimeOffset ClockHighWater { get; init; }

    /// <summary>
    /// The wall time this gateway declined to count against this closure — the restart gaps it could
    /// not verify, which were ADDED to <see cref="EligibleAt"/>. Zero on an installation that never
    /// went down while the scope was closed, which is the ordinary case.
    /// </summary>
    public TimeSpan ClockUnverified { get; init; }

    /// <summary>
    /// The connection the flat reading came from. A position read from before a reconnect is a
    /// memory of a book rather than a reading of it — the rule the breach's own marks follow.
    /// </summary>
    public int ConnectionEpoch { get; init; }

    /// <summary>
    /// THE FRESH FLAT READ. Every position the platform reported on this tick for the scope, as
    /// <c>"ES 0"</c> — empty when the platform reported none at all. It is the evidence and not a
    /// boolean, because "flat" is the one claim this product makes about the PLATFORM's book.
    /// </summary>
    public IReadOnlyList<string> PositionsRead { get; init; } = [];

    /// <summary>
    /// The flatten record's <c>Flat</c> for this breach, or null where the breach was confirmed with
    /// nothing open and no flatten ever ran.
    /// </summary>
    public bool? FlattenWasFlat { get; init; }

    /// <summary>The sentence the owner and the agent are shown. Written once, with the receipt.</summary>
    public string Why { get; init; } = "";
}

/// <summary>
/// THE GATEWAY CLOCK'S MARK, AS PERSISTED — the wall reading, the monotone reading taken with it,
/// the run that took them, and the wall time this gateway has declined to count. See
/// <see cref="LossReopen.ClockKey"/> for what each one is for.
/// </summary>
public sealed record LossClockMark
{
    /// <summary>The highest wall instant this gateway has seen while something was closed.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>
    /// The monotone counter at the same tick, from <c>GatewayOptions.Clock.GetTimestamp()</c>. It is
    /// comparable only against a reading from the same run, which is what <see cref="Run"/> says.
    /// Zero on a mark written before this unit, which <see cref="Run"/> being empty already covers.
    /// </summary>
    public long Monotone { get; init; }

    /// <summary>
    /// The gateway run that wrote this mark. EMPTY means a mark written before <c>U-review-med</c>,
    /// and such a mark is treated as another run's: its monotone reading is not compared, and the
    /// step across it is not credited.
    /// </summary>
    public string Run { get; init; } = "";

    /// <summary>
    /// THE WALL TIME THIS GATEWAY COULD NOT VERIFY, accumulated. It grows only across a restart —
    /// the one gap a monotone counter cannot speak about — and it is added to the eligibility
    /// instant of every closure standing over it, so a process that was down for a day has not
    /// served a day of closure.
    /// </summary>
    public TimeSpan Unverified { get; init; }
}

/// <summary>
/// THE FIRST TIME THE GATEWAY'S CLOCK DID NOT AGREE WITH ITS OWN MARK, while something was closed —
/// written once and never updated.
///
/// <para>It is a fact about the MACHINE rather than about the account, and it is the one thing in
/// this episode nobody in the software can put right: a clock that moved is a clock that may move
/// again, and every instant on every record in this family was taken from it. So it refuses the
/// reopen, it is said on every surface, and it stays said until the owner deals with it.</para>
///
/// <para><b>Two ways it can disagree, and the row says which.</b> BACKWARDS is a reading below the
/// mark. FORWARD is a reading above it by more than <see cref="LossReopen.ForwardSlack"/> with no
/// monotone elapse to match — which is the same act in the direction that ENDS a closure rather
/// than lengthening one, and is therefore the one worth the row.</para>
/// </summary>
public sealed record LossClockSuspect
{
    public string Connector { get; init; } = "";

    public string Account { get; init; } = "";

    /// <summary>The highest instant this gateway had already seen.</summary>
    public DateTimeOffset HighWater { get; init; }

    /// <summary>What the clock said instead: below the mark, or too far above it.</summary>
    public DateTimeOffset Reading { get; init; }

    /// <summary>
    /// <c>"backwards"</c> or <c>"forward"</c>. EMPTY on a row written before <c>U-review-med</c>,
    /// when backwards was the only direction this record could describe.
    /// </summary>
    public string Direction { get; init; } = "";

    /// <summary>
    /// What the wall clock moved by and what the monotone counter measured over the same two
    /// readings, on a FORWARD row — the whole of the evidence, so a reader can check the arithmetic
    /// rather than take the sentence's word for it. Null on a backwards row.
    /// </summary>
    public TimeSpan? Stepped { get; init; }

    public TimeSpan? Measured { get; init; }

    /// <summary>The sentence the owner and the agent are shown. Written once, with the row.</summary>
    public string Why { get; init; } = "";
}

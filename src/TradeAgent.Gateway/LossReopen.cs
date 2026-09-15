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
    public static string Scope(string connectorId, string account) => $"{connectorId}:{account}";

    /// <summary>The day's own receipt: <c>loss_reopen:{connector}:{account}:{utcDay}</c>.</summary>
    public static string DayKey(string connectorId, string account, string utcDay) =>
        $"{Prefix}{Scope(connectorId, account)}:{utcDay}";

    /// <summary>One symbol's: <c>loss_reopen:{connector}:{account}:{symbol}:{utcDay}</c>.</summary>
    public static string SymbolKey(string connectorId, string account, string symbol, string utcDay) =>
        $"{Prefix}{Scope(connectorId, account)}:{symbol}:{utcDay}";

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
    /// THE HIGH-WATER MARK OF THE GATEWAY'S OWN CLOCK, while anything is closed:
    /// <c>loss_clock_high_water:{connector}:{account}</c>.
    ///
    /// <para>Every instant this unit reasons about comes from <c>GatewayOptions.Clock</c>, and a
    /// machine clock can be set backwards — by a person, by an NTP correction, by a restored image.
    /// Eligibility alone would be satisfied by moving the clock forward a day; refusing that is what
    /// this mark is for, and it is monotone on disk rather than in memory because a restart is the
    /// cheapest way to forget an in-memory one.</para>
    ///
    /// <para>A clock that steps FORWARD is allowed and only ever leaves things closed longer: it
    /// advances the mark, and the eligibility instant it is compared against does not move.</para>
    /// </summary>
    public static string ClockKey(string connectorId, string account) =>
        $"loss_clock_high_water:{Scope(connectorId, account)}";

    /// <summary>
    /// WRITTEN ONCE, THE FIRST TIME A TICK SEES THE CLOCK BELOW ITS OWN MARK:
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

    /// <summary>When the tick wrote this receipt. Never earlier than <see cref="EligibleAt"/>.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>
    /// The clock's own high-water mark as this tick left it. A reopen is refused while the clock
    /// reads below it, so the mark is on the row as the evidence that it did not.
    /// </summary>
    public DateTimeOffset ClockHighWater { get; init; }

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

/// <summary>The gateway clock's high-water mark, as persisted. One field, and it only goes up.</summary>
public sealed record LossClockMark
{
    public DateTimeOffset At { get; init; }
}

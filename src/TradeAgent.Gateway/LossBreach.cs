using System.Globalization;
using TradeAgent.Core;

namespace TradeAgent.Gateway;

/// <summary>
/// THE DAY THE BUDGET WAS BREACHED, AS A DURABLE FACT — the keys it is filed under, and the
/// evidence it is filed with.
///
/// <para>Until this existed both loss budgets were evaluated only when an order arrived, and a
/// breach was a property of that one evaluation: it refused that order and remembered nothing. Three
/// consequences, and all three are the same defect. A close realised UNDER the budget reopened the
/// day, because the next evaluation read a smaller figure. A restart reopened it, because the whole
/// state was <c>_dailyLossSaidFor</c> — a field that exists to stop a log line repeating. And a day
/// in which no order happened to arrive was never measured at all, so a book bleeding on its own
/// reached no budget because nobody asked.</para>
///
/// <para><b>The record outranks the ledger.</b> Once it is written, <c>LossBudgetOrThrow</c> refuses
/// off IT and reads neither the ledger nor the platform: the figure that closed the day is the one
/// on the row, and a later figure — smaller because a loser was closed, or smaller because the owner
/// widened the budget — is not evidence that the day did not happen. Nothing expires it: a closure
/// ends when a RECEIPT is written for this record (<c>U-reopen-1</c>), and until then every reader
/// finds it whatever day it is.</para>
///
/// <para><b>Written once and never updated.</b> The first CONFIRMED breach writes it with everything
/// that was true at that instant; while the scope is still closed no further breach of it writes
/// anything at all — one episode, whatever day the watch is on.
/// A record that could be rewritten is a record whose evidence is whatever the last writer thought,
/// which is exactly what the ledger already is.</para>
///
/// <para>It lives in the app-owned <c>kv</c> table and NOT in a table of its own: no schema rung is
/// spent on it, and nothing an agent can reach writes there. A table with its own columns waits on
/// the unit that gives the protections a home (<c>U-protect</c>).</para>
/// </summary>
public static class LossBreach
{
    /// <summary>Every key this record family uses starts here, so a scan can find them all.</summary>
    public const string Prefix = "loss_breach:";

    /// <summary>
    /// The UTC day a key names. It used to be the whole expiry mechanism — tomorrow asked for
    /// another key — and since <c>U-reopen-1</c> it is only an identity: what ENDS a closure is a
    /// receipt, and the day in the key is how the receipt names the record it releases.
    /// </summary>
    public static string Stamp(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// ONE NAME, MADE SAFE TO PUT BETWEEN TWO COLONS — and the whole of this unit's key half.
    ///
    /// <para>A key is an ADDRESS. The names in it are the PLATFORM's strings and never ones
    /// TradeAgent chooses: <c>ES:H6</c>, <c>BINANCE:BTCUSDT</c> and an account id a prop firm
    /// qualifies with its own venue all carry the character the key is built out of, so
    /// <c>loss_breach:ACC:ES:H6:2026-03-10</c> and <c>loss_breach:ACC:ES:H6</c>-the-account's own day
    /// were the same row — one scope's closure silently overwriting another's, or silently not being
    /// written at all (REVIEW 2026-09-16, finding 1 and UNVERIFIED 2).</para>
    ///
    /// <para><b>Escaped, not refused.</b> The alternative the review offered — refuse a name that
    /// contains the delimiter at the point the key is minted — would remove the loss budget from
    /// exactly the venues whose own convention is <c>VENUE:SYMBOL</c>, which is a protection deleted
    /// rather than a defect fixed. <c>%</c> becomes <c>%25</c> and <c>:</c> becomes <c>%3A</c>: a
    /// total, reversible mapping, so no name is ever unaddressable and no two names ever share an
    /// address. A name that contains NEITHER character — every name any connector in this build
    /// produces — maps to itself, so every key and every receipt already on disk is byte for byte
    /// the key it was and nothing is orphaned by this change.</para>
    /// </summary>
    public static string Part(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Contains('%', StringComparison.Ordinal) || value.Contains(':', StringComparison.Ordinal)
            ? value.Replace("%", "%25", StringComparison.Ordinal).Replace(":", "%3A", StringComparison.Ordinal)
            : value;
    }

    /// <summary>The name back out of <see cref="Part"/>. Undone in the reverse order it was done.</summary>
    public static string Name(string part)
    {
        ArgumentNullException.ThrowIfNull(part);
        return part.Contains('%', StringComparison.Ordinal)
            ? part.Replace("%3A", ":", StringComparison.Ordinal).Replace("%25", "%", StringComparison.Ordinal)
            : part;
    }

    /// <summary>
    /// THE DAY'S OWN CLOSURE: <c>loss_breach:{account}:{utcDay}</c>. The account is in the key
    /// because a budget is the ACCOUNT's, and a second account on the same platform has lost nothing
    /// because this one has.
    /// </summary>
    public static string DayKey(string account, DateTimeOffset at) => $"{Prefix}{Part(account)}:{Stamp(at)}";

    /// <summary>
    /// ONE SYMBOL'S CLOSURE: <c>loss_breach:{account}:{symbol}:{utcDay}</c>. Opens and adds on that
    /// symbol are refused until TradeAgent reopens it; everything else on the account is untouched.
    /// </summary>
    public static string SymbolKey(string account, string symbol, DateTimeOffset at) =>
        $"{Prefix}{Part(account)}:{Part(symbol)}:{Stamp(at)}";

    /// <summary>What a scan for one account's closures asks for.</summary>
    public static string AccountPrefix(string account) => $"{Prefix}{Part(account)}:";

    /// <summary>
    /// THE KEY ONE RECORD IS FILED UNDER, off the RECORD's own day and never off the clock. A
    /// closure now outlives the day it began in (<c>U-reopen-1</c>), so a caller that rebuilt the
    /// key from <c>Now</c> would name a row that does not exist the moment the clock crosses a
    /// midnight — which is how a flatten's outcome would be filed against a breach that never
    /// happened.
    /// </summary>
    public static string KeyFor(LossBreachRecord breach)
    {
        ArgumentNullException.ThrowIfNull(breach);
        return breach.Symbol is null
            ? $"{Prefix}{Part(breach.Account)}:{breach.Day}"
            : $"{Prefix}{Part(breach.Account)}:{Part(breach.Symbol)}:{breach.Day}";
    }

    /// <summary>
    /// THE SENTENCE THE DAY'S CLOSURE IS REFUSED WITH, AND SHOWN WITH, EVERYWHERE — written ONCE,
    /// onto the record, at the moment it is confirmed.
    ///
    /// <para>It is on the record rather than recomposed per surface because the figure it names is
    /// the one that closed the day, and a sentence recomposed later would quietly re-read the
    /// ledger: the refusal would then say a number that is not the number it refused on. Four things
    /// have to be in it — since when, why, HOW LONG it lasts and what ends it, and that TradeAgent
    /// CLOSES what is open when this happens. The fourth used to be its opposite, and changing it was
    /// the point of <c>U-flatten-2</c>: an owner who reads "the day is closed" and assumes their
    /// positions are where they left them has been told the opposite of what happened. It says the
    /// RULE rather than the outcome, because this sentence is written at the moment of the breach and
    /// the flatten has not run yet; what was actually closed is on the flatten's own record.</para>
    ///
    /// <para>The third used to say "until the next UTC day". That was true of a record which expired
    /// with its key, and it is the defect <c>U-reopen-1</c> closes: a breach at 23:58Z was two
    /// minutes of closure. It now says the LENGTH, and that a reopen is something TradeAgent DOES
    /// after looking — "it lifts by itself at midnight" and "it lifts once the software has checked"
    /// are different things to be waiting for.</para>
    /// </summary>
    public static string DaySentence(decimal loss, decimal budget, string currency,
        DateTimeOffset at, int feesUnknownFills, TimeSpan minClosure) =>
        $"TradeAgent closed today to new risk at {at.UtcDateTime:HH:mm} UTC: the day was down "
        + $"{Labels.Money(loss, currency)} against the {Labels.Money(budget, currency)} you set as "
        + $"“{Labels.MaxDailyLoss}”{Fees(feesUnknownFills)}. It stays closed for at least "
        + $"{LossReopen.Hours(minClosure)}, whatever the figure does afterwards, and TradeAgent reopens it "
        + "itself once that time has run and it can see your book is flat — there is nothing to press. "
        + "TradeAgent CLOSES YOUR OPEN POSITIONS when this happens — it cancels the orders that could add "
        + "risk and closes what is open, and the result is reported separately. Closing or reducing a "
        + "position is still allowed.";

    /// <summary>The same, for one position, which is closed to opens and adds and to nothing else.</summary>
    public static string SymbolSentence(string symbol, decimal loss, decimal budget, string currency,
        DateTimeOffset at, int feesUnknownFills, TimeSpan minClosure) =>
        $"TradeAgent closed {symbol} to new risk at {at.UtcDateTime:HH:mm} UTC: it was down "
        + $"{Labels.Money(loss, currency)} against the {Labels.Money(budget, currency)} you set as "
        + $"“{Labels.MaxLossPerTrade}”{Fees(feesUnknownFills)}. It stays closed for at least "
        + $"{LossReopen.Hours(minClosure)}, whatever the figure does afterwards, and TradeAgent reopens it "
        + "itself once that time has run and it can see nothing is open on it — there is nothing to press. "
        + "TradeAgent CLOSES THIS POSITION when this happens — it cancels that instrument's working orders "
        + "and closes what is open, and the result is reported separately. Closing or reducing it is still "
        + "allowed.";

    /// <summary>The clause every loss figure carries while the platform has reported no fee for a fill.</summary>
    static string Fees(int fills) =>
        fills == 0 ? ""
            : $" (your platform reported no fee for {fills} of today's fills, so the real figure is a "
              + "little worse)";

    /// <summary>
    /// WHAT ONE BREACH KEY CLOSES AND WHICH DAY IT WAS WRITTEN ON — or null when the key is not this
    /// account's at all.
    ///
    /// <para><b>It decodes an ADDRESS, and it is no longer how a reader learns what is closed.</b>
    /// Every reader now takes the scope off the ROW — <see cref="LossBreachRecord.Account"/>,
    /// <see cref="LossBreachRecord.Symbol"/>, <see cref="LossBreachRecord.Day"/>, which were always
    /// there — so a key is only what a row is filed and looked up under (<c>U-scope-identity</c>).
    /// This is left for the one thing a row cannot answer: the receipt of a closure whose row can no
    /// longer be parsed, where the alternative is to refuse today's orders on a rotted record from
    /// an episode that ended months ago.</para>
    ///
    /// <para>Four colon-separated parts is a symbol key and three is the day's own, and each part is
    /// a <see cref="Part"/> — so a venue-qualified name makes four parts and not five, and comes
    /// back out of <see cref="Name"/> exactly as the platform spelled it.</para>
    ///
    /// <para><b>It answers the DAY rather than filtering by it</b> (<c>U-reopen-1</c>). A closure no
    /// longer ends when its key goes out of scope, so a reader asking "what is closed" has to see
    /// every day's rows and decide from the record and its receipt — a parse that took today's date
    /// and dropped everything else would be the day-keyed expiry it replaced, wearing a scan.</para>
    /// </summary>
    public static (string Day, string? Symbol)? ScopeOf(string key, string account)
    {
        ArgumentNullException.ThrowIfNull(key);
        var parts = key.Split(':');
        if (parts.Length is not (3 or 4)) return null;
        if (!string.Equals(parts[0] + ":", Prefix, StringComparison.Ordinal)) return null;
        if (!string.Equals(Name(parts[1]), account, StringComparison.Ordinal)) return null;
        return parts.Length == 3 ? (Name(parts[2]), null) : (Name(parts[3]), Name(parts[2]));
    }
}

/// <summary>
/// ONE OPEN POSITION AS IT WAS VALUED AT THE BREACH — and by what.
///
/// <para>The mark is the half of the figure that is not in the ledger, so a record that carried the
/// loss without it would be a number nobody can check afterwards. <see cref="Source"/> says which
/// price was used: the platform's own mark where it reports one, and otherwise the EXECUTABLE side
/// of this gateway's newest quote — the bid for a long, the ask for a short, because what a book is
/// worth is what it can be closed at and never the mid.</para>
/// </summary>
/// <param name="Symbol">The instrument.</param>
/// <param name="Quantity">Signed: positive is long.</param>
/// <param name="Side">"long" or "short" — the side that decides which quote half is executable.</param>
/// <param name="Mark">The price used, or null where the platform's own mark was taken instead.</param>
/// <param name="Source">"platform mark", "bid" or "ask".</param>
/// <param name="AgeSeconds">How old the quote was when it was used, or null for a platform mark.</param>
/// <param name="Value">What this position contributed to the day, signed: negative is a loss.</param>
public sealed record LossBreachMark(
    string Symbol, decimal Quantity, string Side, decimal? Mark, string Source,
    double? AgeSeconds, decimal Value);

/// <summary>
/// THE RECORD ITSELF. Everything on it was true at <see cref="ConfirmedAt"/> and nothing on it is
/// recomputed afterwards — that is the whole point of writing it down.
/// </summary>
public sealed record LossBreachRecord
{
    /// <summary>The account whose budget was breached.</summary>
    public string Account { get; init; } = "";

    /// <summary>
    /// THE PLATFORM THIS BREACH WAS MEASURED ON, and the MODE it was measured in — the other two
    /// thirds of a loss-line scope, and the two this record was missing.
    ///
    /// <para>An account id is unique only within a platform, and switching platforms builds a new
    /// gateway over the same database. Without these, a PAPER breach on a simulator's
    /// <c>SIM-001</c> was indistinguishable from a LIVE breach on a broker's <c>SIM-001</c>, and
    /// <c>FlattenForBreachAsync</c> — which compared the account and nothing else — cancelled the
    /// owner's resting orders and closed their real positions on an installation that had set no
    /// loss budget at all (REVIEW 2026-09-16, finding 3). Every other record on this line already
    /// carried both; this one is why the flatten could not ask.</para>
    ///
    /// <para><b>Empty means a record written before <c>U-scope-identity</c></b>, and such a row
    /// names no platform and no mode. It still CLOSES — refusing new risk on an account that
    /// reached its budget is the safe direction on any platform — and it never flattens, because
    /// sending closes is not.</para>
    /// </summary>
    public string Connector { get; init; } = "";

    /// <summary>The mode this gateway was in when the breach was confirmed. See <see cref="Connector"/>.</summary>
    public TradingMode? Mode { get; init; }

    /// <summary>The UTC day, <c>yyyy-MM-dd</c>. The day it stays closed for.</summary>
    public string Day { get; init; } = "";

    /// <summary>The symbol this closes, or null when it is the whole day.</summary>
    public string? Symbol { get; init; }

    /// <summary>When the breach was first seen, and when a second distinct pull agreed.</summary>
    public DateTimeOffset FirstSeenAt { get; init; }

    public DateTimeOffset ConfirmedAt { get; init; }

    /// <summary>The two pull numbers, so "a second DISTINCT pull agreed" is checkable and not asserted.</summary>
    public long FirstPull { get; init; }

    public long ConfirmingPull { get; init; }

    /// <summary>What the day (or the position) was down, positive, when it was confirmed.</summary>
    public decimal Loss { get; init; }

    /// <summary>Both limits as they stood. A budget widened afterwards does not reopen the day.</summary>
    public decimal DayBudget { get; init; }

    public decimal TradeBudget { get; init; }

    public string Currency { get; init; } = "";

    /// <summary>
    /// WHICH SETTINGS THE BREACH WAS MEASURED UNDER — a SHA-256 of the risk policy as it then was,
    /// short form. Not a counter, because nothing in this product numbers a settings revision; a
    /// hash of the values is a revision identity that cannot drift from the values it names.
    /// </summary>
    public string SettingsRevision { get; init; } = "";

    /// <summary>The ledger half of the figure, and how much of it is understated.</summary>
    public decimal Realized { get; init; }

    public decimal Unrealized { get; init; }

    /// <summary>Fills today the platform reported no fee for. The loss is understated by exactly those.</summary>
    public int FeesUnknownFills { get; init; }

    /// <summary>
    /// The connection this valuation's quotes came from. A mark from before a disconnect is a memory
    /// of a book, so the watcher takes none — and the epoch is on the record so that a reader can
    /// tell which connection the evidence belongs to.
    /// </summary>
    public int ConnectionEpoch { get; init; }

    /// <summary>Every open position and the mark it was valued at.</summary>
    public IReadOnlyList<LossBreachMark> Marks { get; init; } = [];

    /// <summary>
    /// THE CLOSURE LENGTH THAT APPLIED TO THIS BREACH, snapshot at confirmation — the owner's
    /// <c>RiskPolicy.LossMinClosureHours</c> as it then stood (<c>U-reopen-2</c>, item 3).
    ///
    /// <para>Eligibility is computed from THIS and never from the number in force when the question
    /// is asked, which is the record-outranks-the-ledger rule applied to the one value the record is
    /// measured against: a closure narrowed after the event cannot bring this reopen forward, and one
    /// widened after it cannot delay a receipt that has been earned.</para>
    ///
    /// <para><b>NULL means a record written before this unit</b>, and such a row is judged by the
    /// FIXED default (<c>GatewayOptions.LossMinClosure</c>, 24 hours) — the rule that was in force
    /// when it was written, which is the only honest thing to judge it by. It is nullable rather than
    /// defaulted for exactly that reason: a <c>TimeSpan.Zero</c> from an absent JSON field would be
    /// indistinguishable from an owner who set the closure to nothing.</para>
    /// </summary>
    public TimeSpan? MinClosure { get; init; }

    /// <summary>
    /// The strike window, IN UTC DATES, that applied to this breach. Snapshot and nullable for
    /// <see cref="MinClosure"/>'s reasons — a zero from an absent field would read as "the owner
    /// switched the strike rule off", which is a decision and not a missing value.
    /// </summary>
    public int? StrikeWindowDays { get; init; }

    /// <summary>
    /// THE GATEWAY'S UNVERIFIED-TIME COUNTER AS IT STOOD WHEN THIS BREACH WAS CONFIRMED — the
    /// BASELINE the closure's own restart penalty is measured from (<c>U-review-med</c>, item 1).
    ///
    /// <para>The counter on <c>LossClockMark</c> is per account and lives across episodes, so it is
    /// not a duration this closure served: what belongs to THIS closure is how much it has grown
    /// since, and that subtraction needs the value at the breach. It is on the record for the reason
    /// <see cref="MinClosure"/> is — a reader has to be able to check the arithmetic that was
    /// actually done, and the counter it is subtracted from moves.</para>
    ///
    /// <para><b>NULL means a record written before this unit</b>, and such a row is measured from
    /// ZERO: the whole of the account's accumulated unverified time counts against it, which leaves
    /// the closure longer rather than shorter and is this line's direction on every unknown.</para>
    /// </summary>
    public TimeSpan? ClockUnverified { get; init; }

    /// <summary>The sentence the owner and the agent are shown. Written once, with the record.</summary>
    public string Why { get; init; } = "";
}

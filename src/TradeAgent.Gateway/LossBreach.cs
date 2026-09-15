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
    /// THE DAY'S OWN CLOSURE: <c>loss_breach:{account}:{utcDay}</c>. The account is in the key
    /// because a budget is the ACCOUNT's, and a second account on the same platform has lost nothing
    /// because this one has.
    /// </summary>
    public static string DayKey(string account, DateTimeOffset at) => $"{Prefix}{account}:{Stamp(at)}";

    /// <summary>
    /// ONE SYMBOL'S CLOSURE: <c>loss_breach:{account}:{symbol}:{utcDay}</c>. Opens and adds on that
    /// symbol are refused until TradeAgent reopens it; everything else on the account is untouched.
    /// </summary>
    public static string SymbolKey(string account, string symbol, DateTimeOffset at) =>
        $"{Prefix}{account}:{symbol}:{Stamp(at)}";

    /// <summary>What a scan for one account's closures asks for.</summary>
    public static string AccountPrefix(string account) => $"{Prefix}{account}:";

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
            ? $"{Prefix}{breach.Account}:{breach.Day}"
            : $"{Prefix}{breach.Account}:{breach.Symbol}:{breach.Day}";
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
    /// <para>Four colon-separated parts is a symbol key and three is the day's own. The parse is
    /// here rather than at the call site so that the two shapes are decided by the one piece of code
    /// that writes them. A symbol carrying a colon would not survive it — no venue in the catalogue
    /// quotes one, and the alternative, a second index row listing the day's closed symbols, is a
    /// second copy of a fact that can disagree with the first.</para>
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
        if (!string.Equals(parts[1], account, StringComparison.Ordinal)) return null;
        return parts.Length == 3 ? (parts[2], null) : (parts[3], parts[2]);
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

    /// <summary>The sentence the owner and the agent are shown. Written once, with the record.</summary>
    public string Why { get; init; } = "";
}

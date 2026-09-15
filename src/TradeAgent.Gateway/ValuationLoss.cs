using TradeAgent.Core;

namespace TradeAgent.Gateway;

/// <summary>
/// A BOOK NOBODY CAN VALUE IS ITS OWN FAILURE, WITH ITS OWN CLOCK AND ITS OWN WAY OUT — and it is
/// NOT a loss-budget breach, however much of the same machinery answers it.
///
/// <para><b>What the loss units left open.</b> <c>U-flatten-1</c> records nothing on a suspect print
/// and <c>U-flatten-2</c> flattens nothing from one, which is right: closing a book on one bad
/// reading is a decision a second reading undoes. But the other half of a bad reading is a reading
/// that never arrives. A feed that goes silent, a platform that stops marking, a disconnect that
/// outlives the day — each leaves a position open with nobody measuring it, and the loss budget the
/// owner set is then a promise nothing is keeping. Silent indefinite exposure is a failure mode too,
/// and this is its answer.</para>
///
/// <para><b>Two records, and the difference between them is the whole design.</b>
/// <see cref="ValuationUnavailableRecord"/> is the episode — when the gateway last could NOT value
/// this position, and since when. <see cref="ValuationExitRecord"/> is what was then done about it.
/// The measurement and the claim are separate rows for <c>LossFlatten</c>'s reason, and the exit is
/// written once at the SQL layer.</para>
///
/// <para><b>Never <c>LOSS_BUDGET_REACHED</c>, never a breach record, never a strike.</b> The reason
/// is <see cref="Reason"/> and it says what actually happened. A budget breach is a fact about the
/// owner's money — it closes the scope, it counts towards a hold, it opens a post-mortem about a
/// LOSS. A lost valuation is a fact about the owner's DATA: nothing was lost that anybody has
/// measured, and filing it as a breach would make the daily report claim a budget was reached when
/// it was not. See <c>docs/CONTRACTS.md</c> at <c>U-flatten-3</c>, where both choices are recorded.</para>
/// </summary>
public static class ValuationLoss
{
    /// <summary>The reason an exit is recorded and reported under. It is not an <c>ErrorCode</c>:
    /// nothing refuses a caller with it, because nothing is refused — a position is closed.</summary>
    public const string Reason = "VALUATION_LOST";

    /// <summary>Every episode row starts here, so a scan can find them all.</summary>
    public const string Prefix = "valuation_unavailable:";

    /// <summary>Every exit record starts here.</summary>
    public const string ExitPrefix = "loss_valuation_exit:";

    /// <summary>The account as this app identifies one: the platform it is on, and then its id.</summary>
    public static string Scope(string connectorId, string account) => $"{connectorId}:{account}";

    /// <summary>
    /// ONE INSTRUMENT'S EPISODE: <c>valuation_unavailable:{connector}:{account}:{symbol}</c>.
    ///
    /// <para><b>No day in the key, deliberately.</b> An unvaluable book is not a fact about a UTC
    /// date the way a day's loss figure is — it is a continuous stretch of time that starts when a
    /// price stops arriving and ends when one does, and a key carrying the day would start a fresh
    /// episode with a fresh clock at every midnight. The exit bound is measured across the whole
    /// stretch, so the stretch is what the row is about.</para>
    /// </summary>
    public static string KeyFor(string connectorId, string account, string symbol) =>
        $"{Prefix}{Scope(connectorId, account)}:{symbol}";

    /// <summary>The episode's identity, as a stamp a key can carry: the instant it began.</summary>
    public static string EpisodeStamp(DateTimeOffset since) =>
        since.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// THE EXIT'S OWN KEY, AND IT IS THE EPISODE'S AND NOT THE DAY'S:
    /// <c>loss_valuation_exit:{connector}:{account}:{symbol}:{yyyyMMddTHHmmssZ}</c>.
    ///
    /// <para><b>This is a deviation from the brief's <c>{utcDay}</c> and it is deliberate.</b> A
    /// breach record is keyed by the UTC day because a breach IS a fact about a day: the ledger
    /// figure that closed the scope is a day's figure. A lost valuation is a fact about an EPISODE —
    /// a feed that went silent at 09:40 and came back at 09:55 is a different event from one that
    /// goes silent again at 14:10, even on the same instrument on the same date. Keyed by the day,
    /// the second episode's exit would collide with the first one's row, the write-once insert would
    /// answer false, and this code would read that as "already exited" and leave a position open
    /// that nothing would ever close again. The day is on the record for a reader; the episode is
    /// what the key is.</para>
    /// </summary>
    public static string ExitKey(string connectorId, string account, string symbol, DateTimeOffset since) =>
        $"{ExitPrefix}{Scope(connectorId, account)}:{symbol}:{EpisodeStamp(since)}";

    /// <summary>
    /// THE BOUND AS A DURATION, from the owner's number in minutes. At or below zero is OFF — no
    /// data-loss exit at all — which is the WIDEST value it has and the reading every other zero in
    /// <see cref="RiskPolicy"/> already has (the notional cap, both loss budgets, the strike window).
    ///
    /// <para>Zero is deliberately not "exit on the first unvaluable tick". A number whose smallest
    /// value liquidates a book the instant one price is late would be a foot-gun the owner reaches
    /// by typing the same digit that switches every other limit in this product off.</para>
    /// </summary>
    public static TimeSpan BoundOf(decimal minutes) =>
        minutes <= 0m ? TimeSpan.Zero : TimeSpan.FromMinutes((double)minutes);

    /// <summary>How a duration is said to a person: whole minutes, or hours where it runs to them.</summary>
    public static string Spell(TimeSpan d) =>
        d < TimeSpan.FromMinutes(1) ? $"{d.TotalSeconds:0} seconds"
        : d < TimeSpan.FromHours(1) ? $"{d.TotalMinutes:0} minute{(Math.Round(d.TotalMinutes) == 1 ? "" : "s")}"
        : $"{d.TotalHours:0.#} hour{(Math.Abs(d.TotalHours - 1) < 0.05 ? "" : "s")}";

    /// <summary>
    /// WHAT AN OPEN EPISODE MEANS, in the owner's words — written onto the row when the episode
    /// opens and shown from there rather than recomposed per surface, for the reason a breach's own
    /// sentence is (<c>LossBreach.DaySentence</c>): a sentence recomposed later quietly re-reads
    /// state that has moved on and says something the decision was not made on.
    /// </summary>
    public static string OpenSentence(string symbol, decimal quantity, DateTimeOffset since, string why,
        TimeSpan bound) =>
        $"TradeAgent cannot work out what your open {Math.Abs(quantity)} {symbol} is worth ({why}), so what "
        + $"today has made or lost cannot be worked out either and new risk is refused. It has been unable to "
        + $"value it since {since.UtcDateTime:yyyy-MM-dd HH:mm} UTC. "
        + (bound > TimeSpan.Zero
            ? $"If that is still true {Spell(bound)} after it began, TradeAgent will close the position by "
              + "itself, because a position nobody can measure is a position your loss budget is not bounding. "
              + "This is NOT a loss-budget breach and your day is not closed."
            : "TradeAgent will NOT close it: the data-loss exit is switched off in your safety limits, so this "
              + "position stays open, unvalued and unbounded by your loss budget until a price arrives.");

    /// <summary>
    /// WHAT AN EXIT MEANS. Three things have to be in it and the third is the one a budget sentence
    /// would get wrong: WHAT was closed and when, WHY (the valuation, not the money), and that the
    /// day is NOT closed and nothing was lost through a budget — an owner who reads a close and
    /// assumes their budget went is an owner who will go looking for a loss that did not happen.
    /// </summary>
    public static string ExitSentence(string symbol, decimal quantity, DateTimeOffset since,
        DateTimeOffset at, TimeSpan bound, bool flat, string? trouble) =>
        (flat
            ? $"TradeAgent closed your open {Math.Abs(quantity)} {symbol} at {at.UtcDateTime:HH:mm} UTC on "
              + $"{at.UtcDateTime:yyyy-MM-dd}, by code and with nobody pressing anything, and your platform "
              + "reads flat on it. "
            : $"TradeAgent tried to close your open {Math.Abs(quantity)} {symbol} at {at.UtcDateTime:HH:mm} UTC "
              + $"on {at.UtcDateTime:yyyy-MM-dd} and CANNOT CONFIRM that it is closed — go and look. ")
        + $"The reason is {Reason}: it had been unable to work out what the position was worth since "
        + $"{since.UtcDateTime:yyyy-MM-dd HH:mm} UTC, which is longer than the {Spell(bound)} it is set to "
        + "wait, and a position nobody can measure is a position your loss budget is not bounding. "
        + "Your loss budget was NOT reached, no day and no instrument is closed to new risk because of this, "
        + $"and {symbol} is refused new risk only for as long as it still cannot be valued."
        + (trouble is { Length: > 0 } ? $" {trouble}" : "");

    /// <summary>
    /// WHAT THE THRESHOLDS DO AND WHAT THEY CANNOT DO — the disclosure, in one place, so the daily
    /// report and <c>docs/CONTRACTS.md</c> cannot drift into two different promises.
    ///
    /// <para><b>Why it is printed whatever the budget is.</b> The sentence exists because a budget
    /// that IS set reads as a guarantee: "the most the account may be down on the day" is a
    /// threshold at which the software intervenes, and the loss it is actually left holding is
    /// whatever the intervention fills at. Printing this only for an install with no budget would
    /// put the disclosure exactly where there is nothing to disclose and withhold it from every
    /// owner who is relying on one.</para>
    /// </summary>
    public static string Disclosure(TimeSpan tick, TimeSpan confirmWithin, TimeSpan quoteAge,
        TimeSpan exitBound, TimeSpan? lastReadingTook)
    {
        var sampling = lastReadingTook is { } took
            ? $"TradeAgent's last reading of your open book took {took.TotalMilliseconds:0} ms; "
            : "";

        return "these are thresholds at which TradeAgent INTERVENES, and none of them is a maximum loss. "
               + $"It values your open book every {Spell(tick)} (sooner when a price arrives), needs a second "
               + $"agreeing reading within {Spell(confirmWithin)} before it closes anything, refuses a price "
               + $"older than {Spell(quoteAge)} as a basis for the figure, and "
               + (exitBound > TimeSpan.Zero
                   ? $"closes a position it has been unable to value at all for {Spell(exitBound)}. "
                   : "will NOT close a position it cannot value at all, because the data-loss exit is switched "
                     + "off in your safety limits. ")
               + sampling
               + "Between one reading and the next the market can gap straight through the figure, and what the "
               + "closing MARKET order then fills at — the spread it crosses, the slippage on the way, and the "
               + "fees your platform has not reported — is not TradeAgent's to choose. So the loss you are left "
               + "holding can be larger than the budget, and on a gap it can be very much larger. What the budget "
               + "buys you is that TradeAgent stops and closes at the threshold; it does not buy you the number.";
    }
}

/// <summary>
/// ONE STRETCH OF TIME IN WHICH ONE OPEN POSITION COULD NOT BE VALUED — the measurement the exit's
/// clock runs on.
///
/// <para><b><see cref="Since"/> is carried forward and never recomputed.</b> The watch refreshes this
/// row on every tick the episode is still open, and the one field it must not touch is the instant it
/// began: an episode whose start moved with the clock would be an episode that never ages, and the
/// exit bound would never be reached however long the feed stayed silent. The only thing that ends
/// an episode is a tick that actually valued the position — a FRESH, in-epoch, executable mark, or
/// the platform's own figure — and that tick writes <see cref="ClearedAt"/>.</para>
/// </summary>
public sealed record ValuationUnavailableRecord
{
    public string Connector { get; init; } = "";

    public string Account { get; init; } = "";

    public string Symbol { get; init; } = "";

    /// <summary>The mode the episode began in. PAPER and LIVE are different money.</summary>
    public TradingMode Mode { get; init; }

    /// <summary>The signed position as the LAST tick read it. A reader needs the size, not a sign.</summary>
    public decimal Quantity { get; init; }

    /// <summary>The first tick that could not value it. NEVER moved while the episode is open.</summary>
    public DateTimeOffset Since { get; init; }

    /// <summary>The connection epoch the episode began on, for a reader chasing a reconnect.</summary>
    public int SinceEpoch { get; init; }

    /// <summary>The latest tick that still could not value it. This is what moves.</summary>
    public DateTimeOffset LastSeenAt { get; init; }

    /// <summary>Why the latest tick could not: "no quote", "older than 30s", "from a previous connection".</summary>
    public string Refusal { get; init; } = "";

    /// <summary>The sentence, written when the episode opened.</summary>
    public string Why { get; init; } = "";

    /// <summary>The tick that valued it again, which is the only thing that ends an episode.</summary>
    public DateTimeOffset? ClearedAt { get; init; }

    /// <summary>The app cancel this episode ran over the symbol's risk-increasing orders, if any.</summary>
    public string? CancelNonce { get; init; }

    public IReadOnlyList<string> CancelledOrders { get; init; } = [];

    /// <summary>Cancels that did not settle. Non-empty means the cancel is retried on the next tick.</summary>
    public IReadOnlyList<string> CancelsNotSettled { get; init; } = [];

    /// <summary>The exit record this episode ended in, or null because none has been attempted.</summary>
    public string? ExitKey { get; init; }

    /// <summary>Whether this episode is the one currently standing on the instrument.</summary>
    public bool Standing => ClearedAt is null;

    /// <summary>How long it has been running at <paramref name="at"/>. Never negative.</summary>
    public TimeSpan Age(DateTimeOffset at) => at > Since ? at - Since : TimeSpan.Zero;
}

/// <summary>
/// WHAT THE APP DID ABOUT AN EPISODE THAT OUTRAN THE BOUND — its own write-once record, keyed to the
/// episode and never to the day, and carrying its own reason.
///
/// <para>It is a second record rather than fields on the episode row for <c>LossFlatten</c>'s reason:
/// the episode is a measurement the watch keeps refreshing and this is the claim about what was then
/// done, and a claim that lives in a row the app rewrites is a claim the app can rewrite.</para>
/// </summary>
public sealed record ValuationExitRecord
{
    public string Account { get; init; } = "";

    public string Connector { get; init; } = "";

    public TradingMode Mode { get; init; }

    public string Symbol { get; init; } = "";

    /// <summary>The UTC day the exit ran on, <c>yyyy-MM-dd</c> — for a reader, never for the key.</summary>
    public string Day { get; init; } = "";

    /// <summary><see cref="ValuationLoss.Reason"/>. On the row rather than implied by the prefix.</summary>
    public string Reason { get; init; } = ValuationLoss.Reason;

    /// <summary>The episode this exit answers: the instant it began.</summary>
    public DateTimeOffset Since { get; init; }

    /// <summary>The bound that was in force for this episode, taken when the exit ran.</summary>
    public TimeSpan Bound { get; init; }

    /// <summary>What the episode's last refusal was — the reason the book could not be valued.</summary>
    public string Refusal { get; init; } = "";

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset FinishedAt { get; init; }

    /// <summary>The connection epoch the exit ran on. A reconnect mid-run is a reader's business.</summary>
    public int ConnectionEpoch { get; init; }

    public string CancelNonce { get; init; } = "";

    public string CloseNonce { get; init; } = "";

    public IReadOnlyList<string> CancelledOrders { get; init; } = [];

    /// <summary>Openers that would not settle. Non-empty means NO close was sent at all.</summary>
    public IReadOnlyList<string> OpenersNotSettled { get; init; } = [];

    /// <summary>The close this exit sent, with what became of it. Shared with the budget flatten.</summary>
    public IReadOnlyList<LossFlattenLeg> Legs { get; init; } = [];

    public IReadOnlyList<string> Residual { get; init; } = [];

    /// <summary>True only behind a fresh read-back saying the instrument is flat.</summary>
    public bool Flat { get; init; }

    public string Why { get; init; } = "";
}

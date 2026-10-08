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
    public static string Scope(string connectorId, string account) =>
        $"{LossBreach.Part(connectorId)}:{LossBreach.Part(account)}";

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
        $"{Prefix}{Scope(connectorId, account)}:{LossBreach.Part(symbol)}";

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
        $"{ExitPrefix}{Scope(connectorId, account)}:{LossBreach.Part(symbol)}:{EpisodeStamp(since)}";

    /// <summary>
    /// THE EXIT'S CONFIRM (<see cref="ValuationExitConfirm"/>, <c>U-valuation-close-confirm</c>): what became of an
    /// exit's close whose answer was lost, asked of the platform's order history or decided by the owner's answer —
    /// its own family, written once at the SQL layer, keyed exactly as the exit it answers and never a field on it.
    /// </summary>
    public const string ExitConfirmPrefix = "loss_valuation_exit_confirm:";

    /// <summary>
    /// THE EXIT'S CLOSING AGAIN: the <see cref="ValuationExitRecord"/> of the second close a confirm that read the
    /// symbol still open asked for, sent only while the reason the exit was sent for still holds. Its own family,
    /// because the exit is written once and a second close is a second fact, not an edit of the first.
    /// </summary>
    public const string ExitAgainPrefix = "loss_valuation_exit_again:";

    /// <summary>
    /// THE CLOSING AGAIN'S OWN CONFIRM: a <see cref="ValuationExitConfirm"/> of the second generation. It owes nothing
    /// — flat, or still open and the owner's to close — so there is never a third close for one episode.
    /// </summary>
    public const string ExitAgainConfirmPrefix = "loss_valuation_exit_again_confirm:";

    /// <summary><c>loss_valuation_exit_confirm:{connector}:{account}:{symbol}:{yyyyMMddTHHmmssZ}</c>, off the exit's own key.</summary>
    public static string ExitConfirmKeyFor(string exitKey) => Sibling(ExitConfirmPrefix, exitKey);

    /// <summary><c>loss_valuation_exit_again:{connector}:{account}:{symbol}:{yyyyMMddTHHmmssZ}</c>, off the exit's own key.</summary>
    public static string ExitAgainKeyFor(string exitKey) => Sibling(ExitAgainPrefix, exitKey);

    /// <summary><c>loss_valuation_exit_again_confirm:{connector}:{account}:{symbol}:{yyyyMMddTHHmmssZ}</c>.</summary>
    public static string ExitAgainConfirmKeyFor(string exitKey) => Sibling(ExitAgainConfirmPrefix, exitKey);

    /// <summary>The exit key's own suffix — platform, account, symbol and episode — under another family's prefix.</summary>
    static string Sibling(string prefix, string exitKey)
    {
        ArgumentNullException.ThrowIfNull(exitKey);
        if (!exitKey.StartsWith(ExitPrefix, StringComparison.Ordinal))
            throw new ArgumentException($"'{exitKey}' is not a data-loss exit's key", nameof(exitKey));
        return prefix + exitKey[ExitPrefix.Length..];
    }

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
    ///
    /// <para>The CLOSING AGAIN (<paramref name="again"/>, <c>U-valuation-close-confirm</c>) says so first: the answer to
    /// its first close was lost, what settled it (<paramref name="settledFrom"/>: the platform's history, the owner's
    /// answer on the Dashboard, or both), and that the platform still read the position open — this is TradeAgent
    /// finishing the job, once.</para>
    /// </summary>
    public static string ExitSentence(string symbol, decimal quantity, DateTimeOffset since,
        DateTimeOffset at, TimeSpan bound, bool flat, string? trouble, bool again = false, string? settledFrom = null) =>
        (flat && again
            ? $"TradeAgent CLOSED AGAIN your open {Math.Abs(quantity)} {symbol} at {at.UtcDateTime:HH:mm} UTC on "
              + $"{at.UtcDateTime:yyyy-MM-dd}, by code and with nobody pressing anything: the answer to its first close "
              + $"had been lost, {settledFrom ?? "your platform's order history"} settled it and your platform still "
              + "read the position open, and it now reads flat. "
            : flat
            ? $"TradeAgent closed your open {Math.Abs(quantity)} {symbol} at {at.UtcDateTime:HH:mm} UTC on "
              + $"{at.UtcDateTime:yyyy-MM-dd}, by code and with nobody pressing anything, and your platform "
              + "reads flat on it. "
            : $"TradeAgent tried {(again ? "AGAIN " : "")}to close your open {Math.Abs(quantity)} {symbol} at "
              + $"{at.UtcDateTime:HH:mm} UTC on {at.UtcDateTime:yyyy-MM-dd} and CANNOT CONFIRM that it is closed — go "
              + "and look. ")
        + ReasonSentence(symbol, since, bound)
        + (trouble is { Length: > 0 } ? $" {trouble}" : "");

    /// <summary>The reason every exit sentence ends on: the valuation, never the money.</summary>
    static string ReasonSentence(string symbol, DateTimeOffset since, TimeSpan bound) =>
        $"The reason is {Reason}: it had been unable to work out what the position was worth since "
        + $"{since.UtcDateTime:yyyy-MM-dd HH:mm} UTC, which is longer than the {Spell(bound)} it is set to "
        + "wait, and a position nobody can measure is a position your loss budget is not bounding. "
        + "Your loss budget was NOT reached, no day and no instrument is closed to new risk because of this, "
        + $"and {symbol} is refused new risk only for as long as it still cannot be valued.";

    /// <summary>
    /// WHAT AN EXIT'S CONFIRM SAYS (<c>U-valuation-close-confirm</c>): what decided each lost close — the platform's
    /// history or the owner's answer, each verdict naming its own (<paramref name="from"/>) — what a fresh read of the
    /// symbol shows, and what happens next.
    ///
    /// <para>Next is one of three, and only the first exit's confirm can say the first: CLOSING IT AGAIN, while the
    /// position still cannot be valued (<paramref name="notAgain"/> null); NOT closing it again, and why
    /// (<paramref name="notAgain"/>: it can be valued again, the exit is switched off, the stretch has ended); or —
    /// the closing again's own confirm (<paramref name="again"/>) over a position still open — not a third time:
    /// it is the owner's.</para>
    /// </summary>
    public static string ConfirmSentence(string symbol, decimal quantity, DateTimeOffset since, TimeSpan bound,
        string from, IReadOnlyList<LossFlattenVerdict> verdicts, IReadOnlyList<string> stillOpen, bool again,
        string? notAgain)
    {
        var sent = $"the close it sent {(again ? "AGAIN " : "")}for your {Math.Abs(quantity)} {symbol}";
        var found = string.Join("; ", verdicts.Select(v =>
            $"the {v.Symbol} close is {v.State}{(v.Filled is > 0m ? $" ({v.Filled} filled)" : "")} — {v.Evidence}"));
        var open = string.Join(", ", stillOpen);

        var head = stillOpen.Count == 0
            ? $"TradeAgent CONFIRMED FROM {from.ToUpperInvariant()} what became of {sent}, whose answer had been lost: "
              + $"{found}. A fresh read of your account says {symbol} is closed, so nothing more was sent. "
            : again
            ? $"TradeAgent learned from {from} what became of {sent}, whose answer had been lost: {found}. A fresh read "
              + $"still shows {open} open, and TradeAgent does NOT close it a third time: what is still open is yours to "
              + "close — check the platform. "
            : notAgain is null
            ? $"TradeAgent learned from {from} what became of {sent}, whose answer had been lost: {found}. A fresh read "
              + $"still shows {open} open and TradeAgent still cannot value it, so it is CLOSING IT AGAIN, once, with "
              + "every check the first close had — it may only send an order that reduces what is there. "
            : $"TradeAgent learned from {from} what became of {sent}, whose answer had been lost: {found}. A fresh read "
              + $"still shows {open} open, and TradeAgent is NOT closing it again: {notAgain}. ";

        return head + ReasonSentence(symbol, since, bound);
    }

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
        // THE FIGURE AS MEASURED, and never rounded to zero: a reading reported as taking no time at
        // all is a claim that the book is sampled instantaneously, which is the opposite of what this
        // paragraph exists to say. Three decimals, so a sub-millisecond reading prints as itself.
        var sampling = lastReadingTook is { } took
            ? $"TradeAgent's last reading of your open book took {took.TotalMilliseconds:0.###} ms, and that "
              + "is the app's own arithmetic rather than your platform's round trip. "
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

/// <summary>
/// WHAT BECAME OF A DATA-LOSS EXIT'S CLOSES WHOSE ANSWER WAS LOST, ASKED OF THE PLATFORM'S ORDER HISTORY — or decided
/// by the owner's answer on the Dashboard under its live-order veto — AND THE SYMBOL READ BACK AFTER IT
/// (<c>U-valuation-close-confirm</c>).
///
/// <para><b><see cref="LossFlattenConfirm"/>'s rules, for the exit's own event.</b> A third record beside the episode
/// and the exit, never an edit of either: the exit was true when it finished — a close went out and its answer was
/// lost — and stays true. This is a later fact about the same close, from the platform's own history or the owner's
/// own measurement, written ONCE at the SQL layer BEFORE a single row is settled, so it is the decision and the rows
/// are its application. Only every lost close decided by the same history question and the same owner's answer the
/// budget's confirm asks — absence only where the connector's closes carry our id, the owner only past his clock and
/// never over a close the history holds live — and nothing is written while one is undecided.</para>
///
/// <para><b>One per close generation.</b> The exit's confirm is filed under <see cref="ValuationLoss.ExitConfirmPrefix"/>;
/// the exit's closing again — sent only while the reason the exit was sent for still holds — has its own, under
/// <see cref="ValuationLoss.ExitAgainConfirmPrefix"/>, which owes nothing. Each settles and unflags its own two nonces'
/// rows and no other's, which lifts the pause the lost close imposed and lets later exits go out.</para>
/// </summary>
public sealed record ValuationExitConfirm
{
    public string Account { get; init; } = "";

    public string Connector { get; init; } = "";

    public TradingMode Mode { get; init; }

    public string Symbol { get; init; } = "";

    /// <summary>The UTC day this confirm was written on, <c>yyyy-MM-dd</c> — for a reader, never for the key.</summary>
    public string Day { get; init; } = "";

    /// <summary>The episode the exit answered: the instant it began.</summary>
    public DateTimeOffset Since { get; init; }

    /// <summary>The FIRST exit's key — the generation family this record belongs to, whichever generation it answers.</summary>
    public string ExitKey { get; init; } = "";

    /// <summary>The outcome this answers for: the exit's own <c>loss_valuation_exit:</c> key, or its closing again's
    /// <c>loss_valuation_exit_again:</c> key.</summary>
    public string OutcomeKey { get; init; } = "";

    /// <summary>That outcome's two press nonces, whose rows — and no other generation's — this record settles and unflags.</summary>
    public string CancelNonce { get; init; } = "";

    public string CloseNonce { get; init; } = "";

    public DateTimeOffset At { get; init; }

    /// <summary>One verdict per close whose answer was lost.</summary>
    public IReadOnlyList<LossFlattenVerdict> Verdicts { get; init; } = [];

    /// <summary>What a fresh read of the platform showed open on the symbol, as <c>"ES 1"</c>.</summary>
    public IReadOnlyList<string> StillOpen { get; init; } = [];

    /// <summary>True only behind that fresh read saying the symbol is flat: nothing more is sent.</summary>
    public bool Flat { get; init; }

    /// <summary>
    /// The first exit's confirm read the symbol open while the episode it was sent for still stood: it said
    /// CLOSING IT AGAIN. Whether the closing again then runs is decided on the tick, against the episodes that tick
    /// could not value; a reader whose episode has since ended says so instead. Never true of the closing again's own.
    /// </summary>
    public bool ClosingAgain { get; init; }

    /// <summary>The sentence the owner and the agent are shown.</summary>
    public string Why { get; init; } = "";
}

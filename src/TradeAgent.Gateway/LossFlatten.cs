using TradeAgent.Core;

namespace TradeAgent.Gateway;

/// <summary>
/// WHAT THE APP DID ABOUT A CONFIRMED BREACH — its own write-once record, keyed to the breach and
/// filed beside it.
///
/// <para><b>It is a second record and not a field on the first.</b> <see cref="LossBreachRecord"/>
/// is the MEASUREMENT: written once, by the watch, with everything that was true at the instant two
/// pulls agreed. This is the CLAIM about what was then done about it. A record the app appends to
/// is a record the app can rewrite, and the one thing an owner reading a breach has to be able to
/// trust is that the evidence which closed the day is the evidence that closed it — so the breach
/// row is never touched again, by this unit or by any other.</para>
///
/// <para><b>Keyed by the account AND the platform it belongs to.</b> An account id is unique only
/// WITHIN a platform (the approval path has refused on exactly this since 2026-09-05: the
/// simulator's <c>SIM-001</c> and a broker's <c>SIM-001</c> are different money), and switching
/// platforms builds a new gateway over the SAME database. A key carrying only the account id would
/// let a PAPER flatten answer for a LIVE closure. So the scope is <c>{connector}:{account}</c>, and
/// the record carries the mode as well, for a reader.</para>
///
/// <para><b>Written once, at the end, whatever the outcome.</b> A flatten that leaves a leg
/// unresolved writes this record with <see cref="Flat"/> false and the reason — because the absence
/// of this record is what makes the startup sweep re-run a flatten that was killed before it
/// finished, and a run that ended honestly in "TradeAgent could not confirm it" is not one to
/// repeat over an unreconciled row.</para>
///
/// <para><b>Except an attempt that put nothing on the wire</b> (<c>U-fix-loss-reopen</c>): nothing
/// happened, so there is no outcome to write and no row to reconcile. It writes a
/// <see cref="LossFlattenOwed"/> note instead, this record stays absent, and the same sweep re-runs it
/// on every later pass — unless it found the book already flat, which IS an outcome and is written.</para>
/// </summary>
public static class LossFlatten
{
    /// <summary>Every key this record family uses starts here, so a scan can find them all.</summary>
    public const string Prefix = "loss_flatten:";

    /// <summary>The account as this app identifies one: the platform it is on, and then its id.</summary>
    public static string Scope(string connectorId, string account) =>
        $"{LossBreach.Part(connectorId)}:{LossBreach.Part(account)}";

    /// <summary>The day's own flatten: <c>loss_flatten:{connector}:{account}:{utcDay}</c>.</summary>
    public static string DayKey(string connectorId, string account, string utcDay) =>
        $"{Prefix}{Scope(connectorId, account)}:{utcDay}";

    /// <summary>One symbol's: <c>loss_flatten:{connector}:{account}:{symbol}:{utcDay}</c>.</summary>
    public static string SymbolKey(string connectorId, string account, string symbol, string utcDay) =>
        $"{Prefix}{Scope(connectorId, account)}:{LossBreach.Part(symbol)}:{utcDay}";

    /// <summary>
    /// THE OUTCOME KEY OF ONE BREACH RECORD — the day comes off the RECORD and never off the clock.
    ///
    /// <para>It was <c>Stamp(Now)</c> until <c>U-reopen-1</c>, which was the same instant as the
    /// breach for as long as a closure could not outlive its own UTC day. It can now: a breach
    /// confirmed at 23:58Z is flattened at 23:58Z and swept, re-read and reported on for a whole day
    /// afterwards, and every one of those readers has to be naming the same row.</para>
    /// </summary>
    public static string KeyFor(string connectorId, LossBreachRecord breach)
    {
        ArgumentNullException.ThrowIfNull(breach);
        return breach.Symbol is null
            ? DayKey(connectorId, breach.Account, breach.Day)
            : SymbolKey(connectorId, breach.Account, breach.Symbol, breach.Day);
    }

    /// <summary>
    /// The owed note's family (<see cref="LossFlattenOwed"/>) — its own prefix, so no scan of
    /// outcomes can ever read a note as an outcome.
    /// </summary>
    public const string OwedPrefix = "loss_flatten_owed:";

    /// <summary>
    /// WHERE A FLATTEN THAT HAS NOT HAPPENED YET SAYS SO: <c>loss_flatten_owed:{connector}:{account}:[{symbol}:]{utcDay}</c>,
    /// off the breach's own day exactly as the outcome is.
    /// </summary>
    public static string OwedKeyFor(string connectorId, LossBreachRecord breach)
    {
        ArgumentNullException.ThrowIfNull(breach);
        return OwedPrefix + KeyFor(connectorId, breach)[Prefix.Length..];
    }

    /// <summary>
    /// The confirm's family (<see cref="LossFlattenConfirm"/>, <c>U-flatten-confirm</c>) — its own
    /// prefix, written ONCE per close generation at the SQL layer, and never a field on the outcome it
    /// answers: this one the FIRST flatten's, and the closing again's under <see cref="AgainConfirmPrefix"/>.
    /// </summary>
    public const string ConfirmPrefix = "loss_flatten_confirm:";

    /// <summary><c>loss_flatten_confirm:{connector}:{account}:[{symbol}:]{utcDay}</c>, off the breach's own day.</summary>
    public static string ConfirmKeyFor(string connectorId, LossBreachRecord breach)
    {
        ArgumentNullException.ThrowIfNull(breach);
        return ConfirmPrefix + KeyFor(connectorId, breach)[Prefix.Length..];
    }

    /// <summary>
    /// The CLOSING AGAIN's outcome family: the <see cref="LossFlattenRecord"/> of the flatten a confirm
    /// asked for when the book was still open. Its own prefix, because the first outcome is written once
    /// and a second flatten is a second fact, not an edit of the first.
    /// </summary>
    public const string AgainPrefix = "loss_flatten_again:";

    /// <summary><c>loss_flatten_again:{connector}:{account}:[{symbol}:]{utcDay}</c>.</summary>
    public static string AgainKeyFor(string connectorId, LossBreachRecord breach)
    {
        ArgumentNullException.ThrowIfNull(breach);
        return AgainPrefix + KeyFor(connectorId, breach)[Prefix.Length..];
    }

    /// <summary>The closing again's owed note family (<see cref="LossFlattenOwed"/>), on the first attempt's rule.</summary>
    public const string AgainOwedPrefix = "loss_flatten_again_owed:";

    /// <summary><c>loss_flatten_again_owed:{connector}:{account}:[{symbol}:]{utcDay}</c>.</summary>
    public static string AgainOwedKeyFor(string connectorId, LossBreachRecord breach)
    {
        ArgumentNullException.ThrowIfNull(breach);
        return AgainOwedPrefix + KeyFor(connectorId, breach)[Prefix.Length..];
    }

    /// <summary>
    /// THE CLOSING AGAIN'S OWN CONFIRM (<c>U-valuation-close-confirm</c>): a <see cref="LossFlattenConfirm"/> of the
    /// second close generation, written once at the SQL layer exactly as the first flatten's is, and never a field on
    /// either outcome or on the first confirm. A close the closing again lost the answer to is decided here, by the
    /// same history question and the same owner's answer, and it OWES NOTHING: flat, or still open and the owner's to
    /// close — there is no third flatten.
    /// </summary>
    public const string AgainConfirmPrefix = "loss_flatten_again_confirm:";

    /// <summary><c>loss_flatten_again_confirm:{connector}:{account}:[{symbol}:]{utcDay}</c>.</summary>
    public static string AgainConfirmKeyFor(string connectorId, LossBreachRecord breach)
    {
        ArgumentNullException.ThrowIfNull(breach);
        return AgainConfirmPrefix + KeyFor(connectorId, breach)[Prefix.Length..];
    }
}

/// <summary>
/// WHAT DECIDED ONE CLOSE WHOSE ANSWER WAS LOST: THE PLATFORM'S ORDER HISTORY, OR THE OWNER'S ANSWER ON THE
/// DASHBOARD (<c>U-loss-hold-release</c>).
/// </summary>
/// <param name="RequestId">The flagged write-ahead row the close was sent under.</param>
/// <param name="Symbol">The instrument.</param>
/// <param name="State">The TERMINAL state the platform holds the close in — or CANCELLED, "never reached
/// the platform", where a connector whose closes carry our id lists no order and no fill under it past the
/// grace (<c>U-flatten-absence</c>) — or, where <paramref name="ByTheOwner"/>, the terminal state the owner gave
/// the row on the Dashboard. Nothing else is a verdict.</param>
/// <param name="Filled">What the platform says filled, where it says.</param>
/// <param name="ConnectorOrderId">The platform's own reference, where the history named one.</param>
/// <param name="Evidence">Which read said it, or the owner's own words, in the owner's words.</param>
/// <param name="ByTheOwner">True when the owner's answer on the Dashboard decided it: his own measurement, through
/// operator authority in-process, counted only once the close could no longer be on its way to the platform and
/// never over a close the platform's history holds live.</param>
public sealed record LossFlattenVerdict(string RequestId, string Symbol, string State, decimal? Filled,
    string? ConnectorOrderId, string Evidence, bool ByTheOwner = false);

/// <summary>
/// THE CONFIRM — WHAT BECAME OF A FLATTEN'S LOST CLOSES, ASKED OF THE PLATFORM'S ORDER HISTORY, AND THE
/// BOOK READ BACK AFTER IT (<c>U-flatten-confirm</c>).
///
/// <para><b>A third record, and deliberately not an edit of the outcome.</b> <see cref="LossFlattenRecord"/>
/// was true when its flatten finished — a close went out and its answer was lost — and it stays true:
/// nothing rewrites it. This is a later fact about the same breach, from a different source: the
/// platform's own history, read on a later pass, and a fresh read of the book after it.</para>
///
/// <para><b>Written once per CLOSE GENERATION, at the SQL layer, BEFORE a single row is settled</b>, so it is
/// the decision and the rows are its application: a pass killed between the two finishes on the next one
/// from the record. A generation is one outcome with its own two press nonces (<c>U-valuation-close-confirm</c>):
/// the first flatten's confirm is filed under <see cref="LossFlatten.ConfirmPrefix"/>, and a lost answer to the
/// closing again is decided by that generation's OWN confirm under <see cref="LossFlatten.AgainConfirmPrefix"/>
/// — never by re-reading this one, and never by settling a row of another generation.</para>
///
/// <para><b>Only every lost close decided by the platform's own word, by absence only behind a claim, or
/// by the owner's answer under the platform's veto.</b> A close the history holds in a terminal state, or
/// whose fills it lists, is decided; one still working, and a read that did not answer, decide nothing, and
/// no confirm is written. One the history does not list decides nothing either — except on a connector that
/// claims its closes carry the id they are handed (<c>ConnectorCapabilities.ClosesCarryClientOrderId</c>),
/// where, past the grace, it never reached the platform (<c>U-flatten-absence</c>). ATAS makes no such claim.
/// A close the OWNER has answered on the Dashboard is decided by his answer (<c>U-loss-hold-release</c>) — once
/// it can no longer be on its way to the platform, and never while a history that can be asked holds it
/// live — so where no history can be asked, ATAS first, a closure is confirmed once he has answered every lost
/// close.</para>
/// </summary>
public sealed record LossFlattenConfirm
{
    public string Account { get; init; } = "";

    public string Connector { get; init; } = "";

    public TradingMode Mode { get; init; }

    /// <summary>The breach's UTC day, which this record is filed under.</summary>
    public string Day { get; init; } = "";

    public string? Symbol { get; init; }

    public string BreachKey { get; init; } = "";

    /// <summary>The outcome this answers for: the first flatten's <c>loss_flatten:</c> key, or the closing
    /// again's <c>loss_flatten_again:</c> key for the closing again's own confirm.</summary>
    public string OutcomeKey { get; init; } = "";

    /// <summary>That outcome's two press nonces, whose rows — and no other generation's — this record settles and unflags.</summary>
    public string CancelNonce { get; init; } = "";

    public string CloseNonce { get; init; } = "";

    public DateTimeOffset At { get; init; }

    /// <summary>One verdict per close whose answer was lost.</summary>
    public IReadOnlyList<LossFlattenVerdict> Verdicts { get; init; } = [];

    /// <summary>What a fresh read of the platform showed open in the breach's scope, as <c>"ES 1"</c>.</summary>
    public IReadOnlyList<string> StillOpen { get; init; } = [];

    /// <summary>
    /// The read-back was flat: nothing is sent. False on the FIRST flatten's confirm is CLOSING AGAIN — the
    /// sweep runs the same flatten once nothing is unconfirmed, and files it under
    /// <see cref="LossFlatten.AgainPrefix"/>. False on the closing again's own confirm sends nothing more: what is
    /// still open is the owner's to close, and the sentence says so.
    /// </summary>
    public bool Flat { get; init; }

    /// <summary>The sentence the owner and the agent are shown.</summary>
    public string Why { get; init; } = "";
}

/// <summary>
/// A FLATTEN THAT IS OWED — written by an attempt that put NOTHING on the wire (<c>U-fix-loss-reopen</c>).
///
/// <para><b>Not an outcome, and deliberately not one.</b> <see cref="LossFlattenRecord"/> is written
/// once, at the end, and it is final: a flatten that ended in "could not confirm" is never repeated
/// over a row nobody has reconciled. An attempt whose transport record is still EMPTY at the end
/// dispatched nothing at all — its budget went before its first close, or the platform could not be
/// read — so there is no row to reconcile and nothing that happened to record. Writing the outcome
/// for it is what closed the day on windows-latest with the book open and nothing ever trying again.
/// So it writes this instead, the outcome stays absent, and the sweep that re-runs a flatten with no
/// outcome does exactly that on every later pass.</para>
///
/// <para><b>It is the owner's words while the book is still open.</b> Every surface that shows what
/// was done about a closure shows this when there is no outcome yet: not flat, not closed, and trying
/// again. It is rewritten by each attempt that sends nothing — it is a standing obligation and not a
/// fact about one instant — and it is left on disk once an outcome exists, which every reader prefers.</para>
/// </summary>
public sealed record LossFlattenOwed
{
    public string Account { get; init; } = "";

    public string Connector { get; init; } = "";

    public TradingMode Mode { get; init; }

    /// <summary>The breach's UTC day, which this note is filed under.</summary>
    public string Day { get; init; } = "";

    public string? Symbol { get; init; }

    public string BreachKey { get; init; } = "";

    /// <summary>When the first attempt that sent nothing started, and when the latest one did.</summary>
    public DateTimeOffset FirstTriedAt { get; init; }

    public DateTimeOffset LastTriedAt { get; init; }

    /// <summary>How many attempts have sent nothing so far.</summary>
    public int Attempts { get; init; }

    /// <summary>Why the latest attempt could send nothing, in the words its own steps recorded.</summary>
    public string Reason { get; init; } = "";

    /// <summary>The sentence the owner and the agent are shown.</summary>
    public string Why { get; init; } = "";
}

/// <summary>
/// ONE CLOSE THE FLATTEN SENT, AND WHETHER IT ANSWERED THE QUESTION IT WAS SENT TO ANSWER.
/// </summary>
/// <param name="RequestId">The flagged write-ahead row this leg was sent under.</param>
/// <param name="Symbol">The instrument.</param>
/// <param name="Captured">The signed position the close was sized against.</param>
/// <param name="State">The execution state the record ended in.</param>
/// <param name="Filled">What the platform said it filled, where it said anything.</param>
/// <param name="PositionAfter">
/// The position on this instrument on a FRESH read after the close. It is the half of the answer a
/// state cannot give: a close the platform calls FILLED and a position that is still 2 long are not
/// the same news, and only this number tells them apart.
/// </param>
/// <param name="Resolved">
/// Whether the app cleared this leg's flag by itself. True needs BOTH halves — a terminal, filled
/// close and a flat read-back. A rejected or cancelled close is terminal and is not flatness.
/// </param>
/// <param name="Outcome">The sentence, in the owner's words.</param>
public sealed record LossFlattenLeg(string RequestId, string Symbol, decimal Captured,
    string State, decimal? Filled, decimal? PositionAfter, bool Resolved, string Outcome);

/// <summary>
/// THE FLATTEN ITSELF. Everything on it was true at <see cref="FinishedAt"/>; nothing on it is
/// recomputed afterwards, and nothing rewrites it.
/// </summary>
public sealed record LossFlattenRecord
{
    /// <summary>The account flattened — off the BREACH record, never the currently selected one.</summary>
    public string Account { get; init; } = "";

    /// <summary>The platform the account is on. See <see cref="LossFlatten"/> on why it is in the key.</summary>
    public string Connector { get; init; } = "";

    /// <summary>The mode the flatten ran in, for a reader. PAPER and LIVE are different money.</summary>
    public TradingMode Mode { get; init; }

    /// <summary>The UTC day, <c>yyyy-MM-dd</c>.</summary>
    public string Day { get; init; } = "";

    /// <summary>The symbol this flatten was for, or null when the whole day's budget was breached.</summary>
    public string? Symbol { get; init; }

    /// <summary>The breach record's own key. This record is keyed TO that fact, and says so.</summary>
    public string BreachKey { get; init; } = "";

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset FinishedAt { get; init; }

    /// <summary>The two app press nonces, so every row this flatten wrote can be found from here.</summary>
    public string CancelNonce { get; init; } = "";

    public string CloseNonce { get; init; } = "";

    /// <summary>Working orders that could have increased exposure, and were cancelled first.</summary>
    public IReadOnlyList<string> CancelledOrders { get; init; } = [];

    /// <summary>
    /// Openers whose cancellation did NOT settle. Non-empty means NO close was sent at all: an order
    /// still live at the platform is an order that can still fill, and closing underneath one is how
    /// a flatten leaves an account long again a second later.
    /// </summary>
    public IReadOnlyList<string> OpenersNotSettled { get; init; } = [];

    /// <summary>Every close this flatten sent, with what became of it.</summary>
    public IReadOnlyList<LossFlattenLeg> Legs { get; init; } = [];

    /// <summary>What was still open on a fresh read after the flatten, as <c>"ES 2"</c>.</summary>
    public IReadOnlyList<string> Residual { get; init; } = [];

    /// <summary>
    /// THE ONLY CLAIM THAT MATTERS, AND IT IS A READ-BACK. True when every leg resolved and nothing
    /// this flatten was about is still open. Never derived from a composite answering "ok": a
    /// composite is this app agreeing with itself.
    /// </summary>
    public bool Flat { get; init; }

    /// <summary>The sentence the owner and the agent are shown. Written once, with the record.</summary>
    public string Why { get; init; } = "";
}

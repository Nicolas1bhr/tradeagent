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

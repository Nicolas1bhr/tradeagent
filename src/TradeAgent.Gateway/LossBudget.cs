using TradeAgent.ConnectorSdk;
using TradeAgent.Core;

namespace TradeAgent.Gateway;

/// <summary>
/// THE DAY'S LOSS, ASSEMBLED FROM THE LEDGER AND THE OPEN POSITIONS — and refusing to answer rather
/// than guessing at either half.
///
/// The realised half comes from <see cref="Pnl"/>: the same average-cost book, over the same fills,
/// that <c>trade pnl</c> and the Performance card are computed from, so the number the gate refuses
/// on and the number the owner reads are one number. The unrealised half is the platform's own mark
/// where it reports one, and otherwise the last price this gateway saw times the instrument's
/// multiplier — the arithmetic <see cref="Pnl"/> uses for the same figure.
///
/// WHAT IT REFUSES TO ANSWER ON, and why each is fatal rather than a note:
///
///   * A SYMBOL TRADED TODAY WHOSE MULTIPLIER IS UNKNOWN. Realised profit is
///     <c>(price - average) x quantity x multiplier</c>, so a missing multiplier is not a small
///     error on a futures contract — on ES it is the whole answer divided by fifty, in the direction
///     that says the day is fine. <c>trade pnl</c> reports that as <c>incomplete</c> and shows the
///     figure anyway, which is right for a report and wrong for a gate.
///   * AN OPEN POSITION NOBODY CAN VALUE — no mark from the platform and no price ever seen, or no
///     price and no multiplier. Treating it as flat is the software deciding that a position it
///     cannot see is a position that is not losing.
///
/// A day with no fills and nothing open is not unknown: it is a day that has lost nothing, and it
/// answers zero.
/// </summary>
public static class LossBudget
{
    /// <summary>
    /// <paramref name="today"/> is <see cref="Pnl.Compute"/> over EVERY fill the ledger holds with
    /// its window set to the start of the UTC day — never over today's fills alone. Average cost is
    /// a running quantity: handed only today's fills, a sell that closes a position opened yesterday
    /// has nothing to close and reads as opening a short, which is not a smaller answer but a wrong
    /// one. <see cref="PnlInputs.AllFills"/> says the same thing at the source.
    /// </summary>
    public static LossToday Read(RiskPolicy risk, string currency, PnlReport today,
        IReadOnlyList<PositionInfo> positions, Func<string, QuoteInfo?> lastQuote,
        IReadOnlyList<InstrumentInfo> instruments)
    {
        // NEITHER BUDGET SET READS NOTHING. The rule MaxNotionalPerOrder has: an installation that
        // asked for no budget must not be stopped from trading by metadata no gate is asking for.
        if (risk.MaxDailyLoss <= 0m && risk.MaxLossPerTrade <= 0m) return LossToday.NotEnforced;

        foreach (var s in today.BySymbol)
            if (s.Fills > 0 && !s.MultiplierKnown)
                return CannotBeRead(risk, currency,
                    $"your platform did not say what one contract of {s.Symbol} is worth, and {s.Symbol} " +
                    "has traded today, so what today has made or lost cannot be worked out");

        // The known fees are taken off; the unknown ones are counted and carried. A net that
        // subtracted an unreported fee as if it were zero would understate the loss — see
        // PnlReport.Net, which withholds itself entirely for the same reason.
        var realized = today.Realized - (today.Fees ?? 0m);

        var unrealized = 0m;
        var lossBySymbol = new Dictionary<string, decimal>(StringComparer.Ordinal);

        foreach (var p in positions.Where(p => p.Quantity != 0))
        {
            decimal value;

            // The platform's own mark first: it is stated in the account's currency, by the party
            // holding the position, and it needs no multiplier this project has never measured
            // (docs/CONTRACTS.md on ContractSize). A platform that reports nothing says null.
            if (p.UnrealizedPnl is { } marked)
            {
                value = marked;
            }
            else
            {
                var quote = lastQuote(p.Symbol);
                if ((quote?.Last ?? quote?.Bid ?? quote?.Ask) is not { } last)
                    return CannotBeRead(risk, currency,
                        $"your platform does not say what your open {Math.Abs(p.Quantity)} {p.Symbol} is " +
                        "worth and TradeAgent has never seen a price for it, so what today has made or " +
                        "lost cannot be worked out");

                var multiplier = Pnl.MultiplierFor(p.Symbol, instruments);
                if (!multiplier.Known)
                    return CannotBeRead(risk, currency,
                        $"your platform does not say what your open {Math.Abs(p.Quantity)} {p.Symbol} is " +
                        "worth, and did not say what one contract of it is worth either, so what today " +
                        "has made or lost cannot be worked out");

                value = (last - p.AveragePrice) * p.Quantity * multiplier.Value;
            }

            unrealized += value;
            if (value < 0m) lossBySymbol[p.Symbol] = -value;
        }

        var day = realized + unrealized;
        return new LossToday
        {
            Enforced = true,
            Loss = day < 0m ? -day : 0m,
            Realized = realized,
            Unrealized = unrealized,
            LossBySymbol = lossBySymbol,
            FeesUnknownFills = today.FeesUnknownFills,
            DayBudget = risk.MaxDailyLoss,
            TradeBudget = risk.MaxLossPerTrade,
            Currency = currency
        };
    }

    /// <summary>
    /// A reading that could not be taken, carrying the budgets it was to be measured against — so
    /// that every surface can still say WHAT is being enforced while saying that the figure is not
    /// available. <see cref="LossToday.Enforced"/> stays true: something IS in force, and the reason
    /// it cannot be applied is exactly the news.
    /// </summary>
    public static LossToday CannotBeRead(RiskPolicy risk, string currency, string why) => new()
    {
        Enforced = true,
        Unknown = why,
        DayBudget = risk.MaxDailyLoss,
        TradeBudget = risk.MaxLossPerTrade,
        Currency = currency
    };
}

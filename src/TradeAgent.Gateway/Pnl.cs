using TradeAgent.ConnectorSdk;
using TradeAgent.Core.Db;

namespace TradeAgent.Gateway;

/// <summary>One symbol's share of the answer. Every nullable field here is an UNKNOWN, not a zero.</summary>
public sealed record PnlSymbol(
    string Symbol,
    decimal Realized,
    decimal? Fees,
    int FeesUnknownFills,
    decimal? Unrealized,
    decimal OpenQuantity,
    decimal? LastPrice,
    DateTimeOffset? LastPriceAt,
    decimal Multiplier,
    bool MultiplierKnown,
    int Fills);

/// <summary>One UTC calendar day. Days are UTC so that the same fill lands in the same bucket everywhere.</summary>
public sealed record PnlDay(string Day, decimal Realized, decimal? Fees, int FeesUnknownFills, int Fills);

/// <summary>
/// What the AI made or lost, and everything the figure does not know.
///
/// <para><b><see cref="Net"/> is null when it cannot be computed</b> — that is the whole point of this
/// type. A fee TradeAgent was never told about is not a fee of zero, and a net figure that quietly
/// treats it as one overstates the result in the owner's favour every single time, which is the one
/// direction a money number must never be wrong in. Where a value is unknown the field is null and
/// <see cref="Incomplete"/> says, in the owner's words, what is missing.</para>
/// </summary>
public sealed record PnlReport(
    string Window,
    DateTimeOffset? Since,
    DateTimeOffset AsOf,
    decimal Realized,
    decimal? Fees,
    int FeesUnknownFills,
    decimal? Net,
    decimal? Unrealized,
    decimal MaxDrawdown,
    bool DrawdownIncludesUnrealized,
    int Fills,
    DateTimeOffset? FirstFillAt,
    DateTimeOffset? CoverageFrom,
    IReadOnlyList<PnlSymbol> BySymbol,
    IReadOnlyList<PnlDay> ByDay,
    IReadOnlyList<string> Incomplete);

/// <summary>What <see cref="Pnl.Compute"/> is given. Nulls mean "not known", never "none".</summary>
public sealed record PnlInputs
{
    /// <summary>
    /// EVERY fill the ledger holds, oldest first — not only the window's.
    ///
    /// Average cost is a running quantity: a position opened last week and closed today realises
    /// against last week's price. Handed only today's fills, a closing sell has nothing to close and
    /// reads as opening a short, which is not a smaller answer but a wrong one. The window selects
    /// which realised increments are COUNTED, after they have been computed in order.
    /// </summary>
    public required IReadOnlyList<Fill> AllFills { get; init; }

    /// <summary>
    /// The platform's own positions, with its own average price. Null means open positions are not
    /// part of this answer at all — <see cref="PnlReport.Unrealized"/> is then null, and it is the
    /// CALLER that says why in <see cref="Notes"/>: "the card only counts closed trades" and "the
    /// position read failed" are both this case and they are not the same sentence.
    /// </summary>
    public IReadOnlyList<PositionInfo>? Positions { get; init; }

    /// <summary>The last price seen per symbol. A symbol absent from here cannot be valued.</summary>
    public IReadOnlyDictionary<string, QuoteInfo> Quotes { get; init; } =
        new Dictionary<string, QuoteInfo>(StringComparer.Ordinal);

    public IReadOnlyList<InstrumentInfo> Instruments { get; init; } = [];

    /// <summary>Null is everything the ledger holds.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>What to call the window in the answer: <c>today</c>, <c>all</c> or <c>since</c>.</summary>
    public required string Window { get; init; }

    public required DateTimeOffset AsOf { get; init; }

    /// <summary>When this ledger started watching. Named in <c>incomplete</c> when the window predates it.</summary>
    public DateTimeOffset? CoverageFrom { get; init; }

    /// <summary>Things the CALLER already knows are missing — a failed pull, an unreadable position list.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>
/// Realised profit and loss by average cost, and the arithmetic is deliberately the dullest one that
/// is right.
///
/// <para>Each fill is signed by side. A fill in the direction of the running position enlarges it and
/// moves the average cost; a fill against it CLOSES, realising <c>(price - average) x quantity</c> in
/// the direction the position was held, times the instrument's multiplier. A fill bigger than the
/// position closes all of it and opens the remainder at its own price.</para>
///
/// <para>The multiplier is <see cref="InstrumentInfo.ContractSize"/>, or the value of one point
/// (<c>TickValue / TickSize</c>) where the platform reported those instead. Where it reported
/// neither, the arithmetic falls back to quantity x price — and SAYS SO in <c>incomplete</c>, because
/// on a futures contract that answer is out by the contract size and a reader has to know which they
/// are looking at.</para>
/// </summary>
public static class Pnl
{
    public static PnlReport Compute(PnlInputs i)
    {
        var incomplete = new List<string>(i.Notes);
        var fills = i.AllFills.OrderBy(f => f.At).ToList();

        // ---- realised, by walking every fill in order ------------------------------------------
        var books = new Dictionary<string, Book>(StringComparer.Ordinal);
        var multipliers = new Dictionary<string, (decimal Value, bool Known)>(StringComparer.Ordinal);
        var counted = new List<(Fill Fill, decimal Realized)>();

        foreach (var f in fills)
        {
            if (!multipliers.TryGetValue(f.Symbol, out var m))
                multipliers[f.Symbol] = m = MultiplierFor(f.Symbol, i.Instruments);

            if (!books.TryGetValue(f.Symbol, out var book)) books[f.Symbol] = book = new Book();
            var realized = book.Apply(Signed(f), f.Price, m.Value);

            if (i.Since is null || f.At >= i.Since) counted.Add((f, realized));
        }

        var window = counted.Select(c => c.Fill).ToList();
        var realizedTotal = counted.Sum(c => c.Realized);

        // ---- fees: known ones summed, unknown ones counted, never coalesced ---------------------
        var feeKnown = window.Where(f => f.Fee is not null).Select(f => f.Fee!.Value).ToList();
        var feesUnknown = window.Count(f => f.Fee is null);
        decimal? fees = feeKnown.Count > 0 ? feeKnown.Sum() : null;

        // THE RULE THIS UNIT EXISTS FOR. A net that subtracted only the fees it happens to know is a
        // number that is wrong in the owner's favour, silently, and it is the number a loss budget
        // would be enforced against. It is withheld instead, and `incomplete` says why.
        decimal? net = feesUnknown == 0 ? realizedTotal - (fees ?? 0m) : null;
        if (feesUnknown > 0)
            incomplete.Add($"your platform did not report a fee for {feesUnknown} of the {window.Count} " +
                           "fills in this period, so the net figure after costs is not given — " +
                           (fees is null ? "no fees are counted at all" : "only the fees it did report are counted"));

        foreach (var s in multipliers.Where(x => !x.Value.Known).Select(x => x.Key).OrderBy(x => x, StringComparer.Ordinal))
            if (window.Any(f => f.Symbol == s))
                incomplete.Add($"your platform did not say what one contract of {s} is worth, so its fills are " +
                               "counted as quantity x price, which is wrong by the contract size on a futures contract");

        // ---- unrealised, from the platform's own positions at the last price we saw -------------
        decimal? unrealized = 0m;
        var openBySymbol = new Dictionary<string, (decimal Qty, decimal? Value, decimal? Price, DateTimeOffset? At)>(StringComparer.Ordinal);
        if (i.Positions is null)
        {
            unrealized = null;
        }
        else
        {
            foreach (var p in i.Positions.Where(p => p.Quantity != 0))
            {
                if (!multipliers.TryGetValue(p.Symbol, out var m))
                    multipliers[p.Symbol] = m = MultiplierFor(p.Symbol, i.Instruments);

                var quote = i.Quotes.GetValueOrDefault(p.Symbol);
                var last = quote?.Last ?? quote?.Bid ?? quote?.Ask;
                if (last is null)
                {
                    unrealized = null;
                    openBySymbol[p.Symbol] = (p.Quantity, null, null, quote?.At);
                    incomplete.Add($"TradeAgent has not seen a price for {p.Symbol} yet, so your open " +
                                   $"{Math.Abs(p.Quantity)} there is not valued in these figures");
                    continue;
                }

                var value = (last.Value - p.AveragePrice) * p.Quantity * m.Value;
                openBySymbol[p.Symbol] = (p.Quantity, value, last, quote!.At);
                if (unrealized is not null) unrealized += value;
                if (!m.Known)
                    incomplete.Add($"your platform did not say what one contract of {p.Symbol} is worth, so " +
                                   "your open position there is valued as quantity x price");
            }
        }

        // ---- the curve, and the worst drop along it --------------------------------------------
        var includeUnrealized = unrealized is not null;
        var (drawdown, _) = Drawdown(counted, includeUnrealized ? unrealized : null);

        // ---- per symbol and per day ------------------------------------------------------------
        // THE ROWS COVER EVERY SYMBOL THE TOTALS DO, which includes one whose only presence in this
        // period is a position still open: it contributes to `unrealized` and has no fill in the
        // window, so grouping the fills alone gave a breakdown that did not add up to its own total.
        var symbols = counted.Select(c => c.Fill.Symbol)
            .Concat(openBySymbol.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal);

        var bySymbol = symbols
            .Select(symbol =>
            {
                var g = counted.Where(c => string.Equals(c.Fill.Symbol, symbol, StringComparison.Ordinal)).ToList();
                var known = g.Where(c => c.Fill.Fee is not null).Select(c => c.Fill.Fee!.Value).ToList();
                var open = openBySymbol.GetValueOrDefault(symbol);
                var m = multipliers[symbol];
                return new PnlSymbol(
                    Symbol: symbol,
                    Realized: g.Sum(c => c.Realized),
                    Fees: known.Count > 0 ? known.Sum() : null,
                    FeesUnknownFills: g.Count(c => c.Fill.Fee is null),
                    Unrealized: open.Value,
                    OpenQuantity: open.Qty,
                    LastPrice: open.Price,
                    LastPriceAt: open.At,
                    Multiplier: m.Value,
                    MultiplierKnown: m.Known,
                    Fills: g.Count);
            })
            .ToList();

        var byDay = counted
            .GroupBy(c => c.Fill.At.UtcDateTime.ToString("yyyy-MM-dd"))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var known = g.Where(c => c.Fill.Fee is not null).Select(c => c.Fill.Fee!.Value).ToList();
                return new PnlDay(g.Key, g.Sum(c => c.Realized), known.Count > 0 ? known.Sum() : null,
                    g.Count(c => c.Fill.Fee is null), g.Count());
            })
            .ToList();

        if (i.Since is not null && i.CoverageFrom is { } from && i.Since < from)
            incomplete.Add($"TradeAgent has been recording your fills since {from.UtcDateTime:yyyy-MM-dd HH:mm} UTC; " +
                           "anything your platform did not report at that first read is not counted here");
        if (i.Since is null && i.CoverageFrom is { } from2)
            incomplete.Add($"TradeAgent has been recording your fills since {from2.UtcDateTime:yyyy-MM-dd HH:mm} UTC; " +
                           "anything before that is not counted here");

        return new PnlReport(
            Window: i.Window,
            Since: i.Since,
            AsOf: i.AsOf,
            Realized: realizedTotal,
            Fees: fees,
            FeesUnknownFills: feesUnknown,
            Net: net,
            Unrealized: unrealized,
            MaxDrawdown: drawdown,
            DrawdownIncludesUnrealized: includeUnrealized,
            Fills: window.Count,
            FirstFillAt: fills.Count > 0 ? fills[0].At : null,
            CoverageFrom: i.CoverageFrom,
            BySymbol: bySymbol,
            ByDay: byDay,
            Incomplete: incomplete);
    }

    /// <summary>
    /// The worst peak-to-trough drop of the curve, which steps at every fill in the window and takes
    /// one last step for the open position when that is known.
    ///
    /// Known fees are subtracted as they are paid; unknown ones cannot be, which is one more reason
    /// the report says how many there were. The curve starts at zero, so a run that only ever lost
    /// money has a drawdown equal to its loss, and one that only ever made money has none.
    /// </summary>
    static (decimal Worst, decimal Final) Drawdown(List<(Fill Fill, decimal Realized)> counted, decimal? unrealized)
    {
        decimal running = 0m, peak = 0m, worst = 0m;
        foreach (var (fill, realized) in counted)
        {
            running += realized - (fill.Fee ?? 0m);
            peak = Math.Max(peak, running);
            worst = Math.Max(worst, peak - running);
        }
        if (unrealized is { } u)
        {
            running += u;
            peak = Math.Max(peak, running);
            worst = Math.Max(worst, peak - running);
        }
        return (worst, running);
    }

    static decimal Signed(Fill f) =>
        string.Equals(f.Side, nameof(OrderSide.Sell), StringComparison.OrdinalIgnoreCase) ? -f.Quantity : f.Quantity;

    /// <summary>
    /// What one unit of this symbol is worth per point of price. Contract size where the platform
    /// reported one, otherwise the value of a point derived from the tick, otherwise 1 and said so.
    /// </summary>
    static (decimal Value, bool Known) MultiplierFor(string symbol, IReadOnlyList<InstrumentInfo> instruments)
    {
        var info = instruments.FirstOrDefault(x => string.Equals(x.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
        if (info?.ContractSize is { } size and > 0) return (size, true);
        if (info is not null && info.TickSize > 0 && info.TickValue > 0) return (info.TickValue / info.TickSize, true);
        return (1m, false);
    }

    /// <summary>One symbol's running position and average cost. Nothing here is persisted.</summary>
    sealed class Book
    {
        decimal _qty;      // signed: positive is long
        decimal _avg;

        /// <summary>Applies one signed fill and returns what it realised.</summary>
        public decimal Apply(decimal signedQty, decimal price, decimal multiplier)
        {
            if (signedQty == 0) return 0m;

            // Same direction, or flat: this enlarges the position and moves the average.
            if (_qty == 0 || Math.Sign(_qty) == Math.Sign(signedQty))
            {
                var total = Math.Abs(_qty) + Math.Abs(signedQty);
                _avg = total == 0 ? 0m : (_avg * Math.Abs(_qty) + price * Math.Abs(signedQty)) / total;
                _qty += signedQty;
                return 0m;
            }

            // Against the position: it closes, up to what is there.
            var closed = Math.Min(Math.Abs(_qty), Math.Abs(signedQty));
            var realized = (price - _avg) * closed * Math.Sign(_qty) * multiplier;
            var left = _qty + signedQty;

            if (left == 0) { _qty = 0m; _avg = 0m; }
            else if (Math.Sign(left) == Math.Sign(_qty)) _qty = left;          // still on the same side
            else { _qty = left; _avg = price; }                                // flipped: the rest opens here

            return realized;
        }
    }
}

namespace TradeAgent.Core.Strategy;

/// <summary>
/// ONE UTC DAY OF A RUN'S ACCOUNT (<c>U-trial-returns</c>; <see cref="DailyReturns"/>).
///
/// <para><see cref="Bars"/> is how many of the run's evaluated bars CLOSED in the day. With none, the day is UNKNOWN:
/// <see cref="Mark"/>, <see cref="NetReturn"/> and <see cref="Since"/> are null — never 0, which would be a measured flat
/// day (<c>docs/PRINCIPLES.md</c>: unknown is never zero). With one or more, <see cref="Mark"/> is the account at the last
/// of them to close, the open position marked at that close and no exit fee charged on it; <see cref="NetReturn"/> is
/// that mark over the mark it runs from, less one; and <see cref="Since"/> names the day that mark is from — null where it
/// runs from the declared capital, which is the first marked day's. A day after unknown days names the last day that had
/// a mark, so its return spans them and says so.</para>
/// </summary>
public sealed record DailyReturn(DateOnly Day, int Bars, decimal? Mark, decimal? NetReturn, DateOnly? Since);

/// <summary>
/// A RUN'S DAILY NET RETURNS — COMPUTED FROM ITS TRACE, ITS BAR GRID AND ITS DECLARED CAPITAL, AND FROM NOTHING ELSE
/// (<c>U-trial-returns</c>; <c>docs/EDGE-FACTORY.md</c> § 4.5, E1: "daily net returns stored").
///
/// <para><b>The rule, version <see cref="Version"/>.</b> A day is a UTC date. Each <see cref="BacktestEventKind.Bar"/>
/// line of the trace is one evaluated bar, stamped at its OPEN; it closes where the run's grid says that bar ends
/// (<see cref="BarGrid.EndOf"/>), and it belongs to the UTC day it closes in — a close at exactly 00:00Z ends the day
/// before, because that bar's last minute was. A day's mark is the <see cref="BacktestEvent.Equity"/> of the last bar
/// closing in it: cash plus the open position marked at that close, which is what the run's drawdown is measured on, and
/// no exit fee is charged on a position still open. Its net return is the mark over the previous mark, less one; the
/// first marked day's runs from the declared capital. Fees and slippage are in the marks already — the trace is the run's
/// own account under its own execution model — so the return is net of them.</para>
///
/// <para><b>The days run from the first bar's day to the last bar's, every date between them included.</b> A date with no
/// closed bar is in the list with no mark and no return — unknown, never 0 — and the next marked day's return runs from
/// the last mark, naming its day. A day whose marks did not move is a measured 0. A return whose previous mark is not
/// above zero is not a number either way, and is null with the mark kept.</para>
///
/// <para><b>Three inputs, deliberately</b>, for <see cref="BacktestMetrics.Of"/>'s reason: a figure read off the program's
/// text or a counter a caller kept would be a claim about the run rather than a measurement of it. The grid is the run's
/// own (<see cref="BarGrid.For"/>) and the capital is its execution model's — both are what the run already ran on.</para>
/// </summary>
public static class DailyReturns
{
    /// <summary>The rule's version, recorded on every stream: a later rule is a different number, never the same name quietly meaning something else.</summary>
    public const int Version = 1;

    /// <summary>The rule's days for one trace. See the type's summary.</summary>
    public static IReadOnlyList<DailyReturn> Of(BacktestTrace trace, BarGrid grid, decimal capital)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(grid);

        // EACH DAY'S BAR COUNT AND LAST EQUITY, in trace order — which is time order, so the last one written is the last close.
        var closes = new SortedDictionary<DateOnly, (int Bars, decimal Mark)>();
        foreach (var e in trace.Events)
        {
            if (e.Kind != BacktestEventKind.Bar || e.Equity is not { } equity) continue;
            var day = DayOf(grid.EndOf(e.Bar));
            closes[day] = (closes.TryGetValue(day, out var seen) ? seen.Bars + 1 : 1, equity);
        }

        if (closes.Count == 0) return [];

        var days = new List<DailyReturn>();
        var from = (Mark: capital, Day: (DateOnly?)null);
        for (var day = closes.Keys.First(); day <= closes.Keys.Last(); day = day.AddDays(1))
        {
            if (!closes.TryGetValue(day, out var closed))
            {
                days.Add(new DailyReturn(day, 0, null, null, null));
                continue;
            }

            days.Add(new DailyReturn(day, closed.Bars, closed.Mark, Return(closed.Mark, from.Mark), from.Day));
            from = (closed.Mark, day);
        }

        return days;
    }

    /// <summary>The UTC day a bar closing at <paramref name="close"/> belongs to: a close at exactly 00:00Z ends the day before.</summary>
    static DateOnly DayOf(DateTimeOffset close)
    {
        var utc = close.UtcDateTime;
        var day = DateOnly.FromDateTime(utc);
        return utc.TimeOfDay == TimeSpan.Zero ? day.AddDays(-1) : day;
    }

    /// <summary>The mark over the one it runs from, less one — or null where that is not a number (see the type's summary).</summary>
    static decimal? Return(decimal mark, decimal from)
    {
        if (from <= 0m) return null;
        try { return mark / from - 1m; }
        catch (OverflowException) { return null; }
    }
}

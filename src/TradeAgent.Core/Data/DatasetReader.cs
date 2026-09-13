using System.Globalization;
using TradeAgent.Core.Db;

namespace TradeAgent.Core.Data;

/// <summary>One closed bar as it is served to a reader. Prices exactly as the vendor published them.</summary>
public sealed record KlineBar(
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);

/// <summary>
/// What a window of a dataset holds, whether it is more than a caller may have at once, and whether
/// it may be served to this caller at all.
///
/// <para><see cref="Refusal"/> is the holdout's, in words, or null. It comes with an EMPTY
/// <see cref="Bars"/> deliberately: a caller that forgets to look at it is handed nothing, never a
/// bar the owner held back. See <see cref="Holdout"/>.</para>
/// </summary>
public sealed record BarWindow(IReadOnlyList<KlineBar> Bars, bool OverCap, string? Refusal = null);

/// <summary>
/// Reads bars back out of a normalised dataset file.
///
/// <b>THE CAP REFUSES; IT DOES NOT TRUNCATE.</b> An answer silently cut at ten thousand bars is a
/// different window from the one that was asked for, and a caller that backtests over it is
/// backtesting over a period it did not choose and cannot see the edge of. So a window with more
/// bars in it than the cap comes back with <see cref="BarWindow.OverCap"/> set and the caller
/// refuses, naming the limit and the window it applies to.
/// </summary>
public static class DatasetReader
{
    /// <summary>The most bars one call may have. Named in the refusal, not merely enforced.</summary>
    public const int MaxBars = 10_000;

    /// <summary>
    /// The bars of <paramref name="set"/> between <paramref name="from"/> and <paramref name="to"/>,
    /// both inclusive and both optional — or nothing at all, because they are held back from this
    /// audience.
    ///
    /// <para><b><paramref name="audience"/> is required, and the holdout is checked HERE rather than
    /// beside the call.</b> A caller that wants bars has to say who is asking, and the file is not even
    /// opened when the answer is no: that is what stops a new op from serving the months the owner held
    /// back by forgetting a line. The refusal comes back on <see cref="BarWindow.Refusal"/> with an
    /// empty bar list, so ignoring it serves nothing rather than everything.</para>
    ///
    /// <para>Reading stops one bar past the cap: that is enough to know the window is too big, and it is
    /// the last row this ever asks the disk for.</para>
    /// </summary>
    public static BarWindow Read(DatasetRecord set, BarAudience audience, DateTimeOffset? from,
        DateTimeOffset? to, int cap = MaxBars)
    {
        ArgumentNullException.ThrowIfNull(set);

        if (Holdout.Refusal(set, audience, from, to) is { } withheld) return new BarWindow([], false, withheld);

        var bars = new List<KlineBar>();

        foreach (var line in File.ReadLines(set.NormalisedPath))
        {
            if (!TryBar(line, out var bar)) continue;

            if (from is { } lo && bar.OpenTime < lo) continue;
            if (to is { } hi && bar.OpenTime > hi) break;   // the file is written in ascending order

            bars.Add(bar);
            if (bars.Count > cap) return new BarWindow([], true);
        }

        return new BarWindow(bars, false);
    }

    /// <summary>
    /// ONE LINE OF THE NORMALISED FILE, OR NOT A BAR AT ALL — the header, a blank line, a short row
    /// or a column that will not parse.
    ///
    /// <para>Public and in one place because there are now two readers of this format —
    /// <see cref="Read"/>, which the pipe op uses and which REFUSES a window over
    /// <see cref="MaxBars"/>, and <see cref="BarFeed"/>, which streams a whole run with no cap. Both
    /// take a <see cref="BarAudience"/> and both refuse a holdout window; this row parse is below that
    /// and knows nothing about it, which is why the check is on the two entry points and not here. Two
    /// copies of the row parse would be two definitions of what a bar is, and the one that drifted
    /// would be the one nobody read.</para>
    ///
    /// <para>A line that is not a bar is SKIPPED rather than refused: the file is the app's own
    /// output, hashed in the ledger, so a row this cannot read is a corrupt file — and what settles
    /// a corrupt file is `DatasetStore.Checked` comparing the hash, not a reader guessing.</para>
    /// </summary>
    public static bool TryBar(string line, out KlineBar bar)
    {
        bar = Empty;

        if (line.Length == 0 || line.StartsWith("open_time", StringComparison.Ordinal)) return false;

        var f = line.Split(',');
        if (f.Length < 6) return false;
        if (!DateTimeOffset.TryParse(f[0], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)) return false;

        if (!decimal.TryParse(f[1], CultureInfo.InvariantCulture, out var o)) return false;
        if (!decimal.TryParse(f[2], CultureInfo.InvariantCulture, out var h)) return false;
        if (!decimal.TryParse(f[3], CultureInfo.InvariantCulture, out var l)) return false;
        if (!decimal.TryParse(f[4], CultureInfo.InvariantCulture, out var c)) return false;
        if (!decimal.TryParse(f[5], CultureInfo.InvariantCulture, out var v)) return false;

        bar = new KlineBar(at, o, h, l, c, v);
        return true;
    }

    static readonly KlineBar Empty = new(default, 0m, 0m, 0m, 0m, 0m);
}

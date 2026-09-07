using System.Globalization;

namespace TradeAgent.Core.Data;

/// <summary>One closed bar as it is served to a reader. Prices exactly as the vendor published them.</summary>
public sealed record KlineBar(
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);

/// <summary>What a window of a dataset holds, and whether it is more than a caller may have at once.</summary>
public sealed record BarWindow(IReadOnlyList<KlineBar> Bars, bool OverCap);

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
    /// The bars of <paramref name="path"/> between <paramref name="from"/> and
    /// <paramref name="to"/>, both inclusive and both optional.
    ///
    /// Reading stops one bar past the cap: that is enough to know the window is too big, and it is
    /// the last row this ever asks the disk for.
    /// </summary>
    public static BarWindow Read(string path, DateTimeOffset? from, DateTimeOffset? to, int cap = MaxBars)
    {
        var bars = new List<KlineBar>();

        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || line.StartsWith("open_time", StringComparison.Ordinal)) continue;

            var f = line.Split(',');
            if (f.Length < 6) continue;
            if (!DateTimeOffset.TryParse(f[0], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)) continue;

            if (from is { } lo && at < lo) continue;
            if (to is { } hi && at > hi) break;   // the file is written in ascending order

            if (!decimal.TryParse(f[1], CultureInfo.InvariantCulture, out var o)) continue;
            if (!decimal.TryParse(f[2], CultureInfo.InvariantCulture, out var h)) continue;
            if (!decimal.TryParse(f[3], CultureInfo.InvariantCulture, out var l)) continue;
            if (!decimal.TryParse(f[4], CultureInfo.InvariantCulture, out var c)) continue;
            if (!decimal.TryParse(f[5], CultureInfo.InvariantCulture, out var v)) continue;

            bars.Add(new KlineBar(at, o, h, l, c, v));
            if (bars.Count > cap) return new BarWindow([], true);
        }

        return new BarWindow(bars, false);
    }
}

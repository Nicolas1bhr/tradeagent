using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace TradeAgent.Core.Data;

/// <summary>Which unit a raw month's timestamp column is written in. Read off the number, never the date.</summary>
public enum KlineTimeUnit
{
    Milliseconds,
    Microseconds
}

/// <summary>A raw archive file on disk, and the month it is.</summary>
public sealed record RawArchiveFile(string Month, string Path);

/// <summary>What one raw month contributed, and in which unit it turned out to be written.</summary>
public sealed record MonthReading(string Month, KlineTimeUnit Unit, int Rows);

/// <summary>A run of consecutive minutes with no bar. Nothing is put in them.</summary>
public sealed record GapRun(DateTimeOffset From, DateTimeOffset To, int Minutes);

/// <summary>
/// One normalised dataset file and every count that says what it is not.
/// </summary>
public sealed record NormalisedDataset(
    string Path,
    string Sha256,
    int Bars,
    DateTimeOffset? FirstBar,
    DateTimeOffset? LastBar,
    int Gaps,
    IReadOnlyList<GapRun> GapRuns,
    bool GapRunsTruncated,
    int Duplicates,
    int Incomplete,
    int Unreadable,
    IReadOnlyList<MonthReading> Months);

/// <summary>
/// TWELVE ZIPS INTO ONE TIMELINE, with every reading that could be wrong counted rather than
/// smoothed.
///
/// Nothing is filled in. A minute with no bar stays a minute with no bar and is counted, because the
/// one thing a backtest must not be handed is a price that was invented to make a series continuous.
/// </summary>
public static class KlineNormaliser
{
    /// <summary>How many gap runs are listed before the list says it was cut short.</summary>
    public const int MaxGapRunsListed = 50;

    /// <summary>The header of the normalised file. UTC instants, LF endings, no BOM.</summary>
    public const string Header = "open_time,open,high,low,close,volume";

    static readonly TimeSpan Bar = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The unit a kline timestamp is written in, decided by its MAGNITUDE.
    ///
    /// Binance moved this column from milliseconds to microseconds in January 2025 and left the
    /// column where it was, so a twelve-month collection can hold both. A plausible instant in
    /// milliseconds is thirteen digits and in microseconds is sixteen; there is no overlap between
    /// them for any date this product will ever see, which is what makes the number able to answer
    /// the question on its own. Deciding it from the MONTH instead would be a build asserting a fact
    /// about the vendor's release history over the bytes in front of it — and would read a
    /// millisecond file dated after the change as 1970.
    /// </summary>
    public static KlineTimeUnit? TryUnitOf(long timestamp) => timestamp switch
    {
        >= 100_000_000_000L and < 100_000_000_000_000L => KlineTimeUnit.Milliseconds,
        >= 100_000_000_000_000L and < 100_000_000_000_000_000L => KlineTimeUnit.Microseconds,
        _ => null
    };

    /// <inheritdoc cref="TryUnitOf"/>
    public static KlineTimeUnit UnitOf(long timestamp) =>
        TryUnitOf(timestamp) ?? throw new TradeAgentException(ErrorCode.MARKET_DATA_UNAVAILABLE,
            $"{timestamp} is not a timestamp in milliseconds or in microseconds, so this file's " +
            "time column cannot be read at all");

    static DateTimeOffset At(long timestamp, KlineTimeUnit unit) =>
        unit == KlineTimeUnit.Milliseconds
            ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
            : DateTimeOffset.FromUnixTimeMilliseconds(timestamp / 1000L).AddTicks(timestamp % 1000L * 10L);

    /// <summary>
    /// Reads every raw month into one dataset file at <paramref name="destFile"/>.
    ///
    /// <paramref name="downloadedAt"/> is what an INCOMPLETE bar is measured against: a bar whose
    /// close time is after the moment the archive was fetched had not finished when it was written
    /// down, so it is excluded and counted rather than presented as a closed minute.
    /// </summary>
    public static NormalisedDataset Normalise(
        IReadOnlyList<RawArchiveFile> raws, DateTimeOffset downloadedAt, string destFile)
    {
        var bars = new SortedDictionary<DateTimeOffset, string>();
        var months = new List<MonthReading>();
        int duplicates = 0, incomplete = 0, unreadable = 0;

        foreach (var raw in raws.OrderBy(r => r.Month, StringComparer.Ordinal))
        {
            // THE UNIT IS THIS FILE'S, DECIDED BY THE FIRST TIMESTAMP'S MAGNITUDE AND HELD TO BY
            // EVERY ROW AFTER IT. Per file rather than per collection because the vendor's change
            // fell in the middle of a twelve-month window, and per file rather than per date because
            // the month a file is named after is a fact about the vendor, not about its bytes.
            KlineTimeUnit? magnitude = null;
            var rows = 0;

            foreach (var line in RowsOf(raw.Path))
            {
                var f = line.Split(',');
                if (f.Length < 7 || !long.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var openT)
                    || !long.TryParse(f[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var closeT))
                {
                    unreadable++;
                    continue;
                }

                // A row whose magnitude disagrees with the rest of its file is not this file's
                // series: it is unreadable, counted, and left out rather than placed a thousand
                // years from where it belongs.
                magnitude ??= UnitOf(openT);
                if (TryUnitOf(openT) != magnitude) { unreadable++; continue; }

                // THE GUARD. The unit IS the magnitude. It is not derived from raw.Month, and the
                // one line that made it so would read a millisecond file dated after the vendor's
                // change as January 1970.
                var unit = magnitude.Value;

                if (!TryPrices(f, out var priced)) { unreadable++; continue; }

                var open = At(openT, unit);
                if (At(closeT, unit) > downloadedAt) { incomplete++; continue; }
                if (!bars.TryAdd(open, priced)) { duplicates++; continue; }
                rows++;
            }

            months.Add(new MonthReading(raw.Month, magnitude ?? KlineTimeUnit.Milliseconds, rows));
        }

        Write(destFile, bars);

        var (gaps, runs, truncated) = GapsIn(bars.Keys);

        return new NormalisedDataset(
            destFile, Sha256(destFile), bars.Count,
            bars.Count == 0 ? null : bars.Keys.First(),
            bars.Count == 0 ? null : bars.Keys.Last(),
            gaps, runs, truncated, duplicates, incomplete, unreadable, months);
    }

    /// <summary>
    /// THE MINUTES BETWEEN THE FIRST BAR AND THE LAST THAT HAVE NO BAR, counted and grouped into
    /// runs — and NOT filled.
    ///
    /// Only between the first and the last: a dataset that starts on the 3rd is not missing the 1st
    /// and the 2nd, it simply does not cover them, and coverage is what the months list says. What
    /// is inside those ends is a hole in a series somebody is going to backtest across, and the
    /// count is what tells them so. Filling a hole with the previous bar would make every count here
    /// read zero while the file itself became a claim about minutes the venue never traded.
    ///
    /// The run list is bounded because a dataset with a dead venue in the middle of it can have tens
    /// of thousands of runs, and a provenance row is read by a person. The COUNT is never bounded.
    /// </summary>
    static (int Gaps, IReadOnlyList<GapRun> Runs, bool Truncated) GapsIn(IEnumerable<DateTimeOffset> minutes)
    {
        var runs = new List<GapRun>();
        var gaps = 0;
        var truncated = false;
        DateTimeOffset? previous = null;

        foreach (var at in minutes)
        {
            if (previous is { } last && at - last > Bar)
            {
                var missing = (int)((at - last) / Bar) - 1;
                gaps += missing;
                if (runs.Count < MaxGapRunsListed) runs.Add(new GapRun(last + Bar, at - Bar, missing));
                else truncated = true;
            }
            previous = at;
        }

        return (gaps, runs, truncated);
    }

    /// <summary>Parses the five price columns and returns them in the normalised file's own text.</summary>
    static bool TryPrices(string[] f, out string priced)
    {
        priced = "";
        Span<decimal> v = stackalloc decimal[5];
        for (var i = 0; i < 5; i++)
            if (!decimal.TryParse(f[i + 1], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out v[i]))
                return false;

        priced = string.Create(CultureInfo.InvariantCulture, $"{v[0]},{v[1]},{v[2]},{v[3]},{v[4]}");
        return true;
    }

    /// <summary>The lines of the one CSV inside a month's zip.</summary>
    static IEnumerable<string> RowsOf(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) continue;

            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                var trimmed = line.Trim();
                // The archive ships no header, but a vendor that starts shipping one must not become
                // a row of unreadable prices in the middle of a dataset.
                if (trimmed.Length == 0 || trimmed.StartsWith("open_time", StringComparison.OrdinalIgnoreCase)) continue;
                yield return trimmed;
            }
        }
    }

    static void Write(string destFile, SortedDictionary<DateTimeOffset, string> bars)
    {
        var dir = System.IO.Path.GetDirectoryName(destFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // LF and no BOM, written the same way on every platform, because the file's SHA-256 is the
        // dataset's identity and a line ending is not a difference of opinion.
        using var stream = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" };
        writer.WriteLine(Header);
        foreach (var (at, priced) in bars)
            writer.WriteLine($"{at.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{priced}");
    }

    static string Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}

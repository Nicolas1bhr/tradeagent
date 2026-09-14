using System.Globalization;
using System.IO.Compression;
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
    IReadOnlyList<MonthReading> Months)
{
    /// <summary>
    /// HOW MANY OF THESE BARS CARRY NO TRADED VOLUME — <c>midpoint_derived</c>, never trade evidence
    /// (<c>docs/COUNCIL.md</c>:164-172).
    ///
    /// <para>Init-only with a default of zero, like <c>DatasetRecord.HoldoutFrom</c>, so adding it
    /// re-parameterised no construction site — and zero is what every dataset written before this unit
    /// IS, because Binance's archive publishes a traded volume on every kline.</para>
    /// </summary>
    public int MidpointDerived { get; init; }
}

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

    /// <summary>
    /// THE HEADER OF A NORMALISED FILE FROM A SOURCE WHOSE CANDLES ALWAYS CARRY VOLUME. UTC instants,
    /// LF endings, no BOM — and byte for byte what this build wrote before this unit, which is why
    /// every Binance dataset already on disk still hashes to the number its row recorded.
    /// </summary>
    public const string Header = "open_time,open,high,low,close,volume";

    /// <summary>
    /// THE HEADER OF A FILE WHOSE SOURCE MAY PUBLISH A CANDLE WITHOUT VOLUME — one more column, the
    /// per-bar <see cref="BarQuality"/>.
    ///
    /// <para><b>Versioned by the header line and keyed on the SOURCE, not on the data.</b> A reader
    /// tells the two apart by counting columns (<see cref="DatasetReader.TryBar"/>), so a file written
    /// before this unit still reads and every bar in it reads as <c>traded</c>, which is what those
    /// bars are. Keyed on the source rather than on whether a volume-less candle actually turned up,
    /// because "this dataset came from a source that may publish candles without volume" is a fact
    /// about the evidence that a file whose candles all happened to carry volume must still state.</para>
    /// </summary>
    public const string HeaderWithQuality = Header + ",quality";

    /// <summary>
    /// THE BAR LENGTH ONE INTERVAL NAMES, for counting gaps. A source declaring <c>5m</c> whose gaps
    /// were counted a minute at a time would report four missing minutes between every pair of
    /// consecutive bars — a dataset with no holes at all read as one that is 80% holes.
    /// </summary>
    public static TimeSpan BarLength(string? interval)
    {
        var text = (interval ?? "").Trim().ToLowerInvariant();
        if (text.Length < 2 || !int.TryParse(text[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            || n <= 0)
            throw new TradeAgentException(ErrorCode.MARKET_DATA_UNAVAILABLE,
                $"'{interval}' is not a bar length this build can read. It reads 1m, 5m, 1h and 1d.");

        return text[^1] switch
        {
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            'd' => TimeSpan.FromDays(n),
            's' => TimeSpan.FromSeconds(n),
            _ => throw new TradeAgentException(ErrorCode.MARKET_DATA_UNAVAILABLE,
                $"'{interval}' is not a bar length this build can read. It reads 1m, 5m, 1h and 1d.")
        };
    }

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
    /// <param name="candlesCarryVolume">
    /// The SOURCE's declaration (<see cref="ICandleSource.CandlesCarryVolume"/>). True writes the
    /// six-column <see cref="Header"/> this build has always written; false writes
    /// <see cref="HeaderWithQuality"/> and flags every candle that arrived without a volume as
    /// <see cref="BarQuality.MidpointDerived"/>. A source that says its candles always carry one gets
    /// today's behaviour exactly: a row with an empty volume column is UNREADABLE and counted, because
    /// for such a source it is a broken row and not a midpoint.
    /// </param>
    /// <param name="interval">The bar length these rows are of, for counting gaps. See <see cref="BarLength"/>.</param>
    public static NormalisedDataset Normalise(
        IReadOnlyList<RawArchiveFile> raws, DateTimeOffset downloadedAt, string destFile,
        bool candlesCarryVolume = true, string interval = "1m")
    {
        var bar = BarLength(interval);
        var bars = new SortedDictionary<DateTimeOffset, Priced>();
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

                if (!TryPrices(f, candlesCarryVolume, out var priced)) { unreadable++; continue; }

                var open = At(openT, unit);
                if (At(closeT, unit) > downloadedAt) { incomplete++; continue; }
                if (!bars.TryAdd(open, priced)) { duplicates++; continue; }
                rows++;
            }

            months.Add(new MonthReading(raw.Month, magnitude ?? KlineTimeUnit.Milliseconds, rows));
        }

        Write(destFile, bars, candlesCarryVolume);

        var (gaps, runs, truncated) = GapsIn(bars.Keys, bar);

        return new NormalisedDataset(
            destFile, Sha256(destFile), bars.Count,
            bars.Count == 0 ? null : bars.Keys.First(),
            bars.Count == 0 ? null : bars.Keys.Last(),
            gaps, runs, truncated, duplicates, incomplete, unreadable, months)
        {
            // COUNTED, NOT ONLY WRITTEN. The flag in the file tells a reader which bar it is looking
            // at; the count is what the dataset ROW carries, and without it every surface that
            // describes a dataset — `data-list`, the backtest reply, the owner's report — would state
            // a bar count that reads as depth the source never measured.
            MidpointDerived = bars.Values.Count(v => v.Quality == BarQuality.MidpointDerived)
        };
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
    static (int Gaps, IReadOnlyList<GapRun> Runs, bool Truncated) GapsIn(
        IEnumerable<DateTimeOffset> minutes, TimeSpan bar)
    {
        var runs = new List<GapRun>();
        var gaps = 0;
        var truncated = false;
        DateTimeOffset? previous = null;

        foreach (var at in minutes)
        {
            if (previous is { } last && at - last > bar)
            {
                var missing = (int)((at - last) / bar) - 1;
                gaps += missing;
                if (runs.Count < MaxGapRunsListed) runs.Add(new GapRun(last + bar, at - bar, missing));
                else truncated = true;
            }
            previous = at;
        }

        return (gaps, runs, truncated);
    }

    /// <summary>One bar's five numbers as the file writes them, and what its volume is a measurement of.</summary>
    readonly record struct Priced(string Text, string Quality);

    /// <summary>
    /// Parses the five price columns, and decides what the VOLUME column is.
    ///
    /// <para><b>An empty volume is a midpoint-derived candle for a source that said it may publish
    /// one, and a broken row for a source that said it may not.</b> The same bytes, read two ways,
    /// because the difference is a fact about the vendor and not about the row: writing
    /// <c>volume=0</c> for a candle nobody traded on is honest only when something says so beside it
    /// (<c>docs/COUNCIL.md</c>:164-172), and inventing that flag for a source that publishes a traded
    /// volume on every candle would turn a corrupt row into a quality claim.</para>
    /// </summary>
    static bool TryPrices(string[] f, bool candlesCarryVolume, out Priced priced)
    {
        priced = default;
        Span<decimal> v = stackalloc decimal[5];
        var quality = BarQuality.Traded;

        for (var i = 0; i < 5; i++)
        {
            if (decimal.TryParse(f[i + 1], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out v[i])) continue;

            // THE VOLUME COLUMN IS THE ONLY ONE THAT MAY BE ABSENT. A missing price is a row nobody
            // can read whatever the source says about its candles.
            if (i != 4 || candlesCarryVolume || !string.IsNullOrWhiteSpace(f[5])) return false;

            v[4] = 0m;
            quality = BarQuality.MidpointDerived;
        }

        priced = new Priced(
            string.Create(CultureInfo.InvariantCulture, $"{v[0]},{v[1]},{v[2]},{v[3]},{v[4]}"), quality);
        return true;
    }

    /// <summary>
    /// THE ROWS OF ONE RAW FILE — the CSV inside a zip, or the file's own lines.
    ///
    /// <para>Decided by the EXTENSION, because it is a fact about the bytes on disk: Binance publishes
    /// its months as zips and a source that publishes plain text is kept exactly as it arrived
    /// (<c>CandleSourceEntry.RawFileExtension</c>). Converting either into the other's shape would make
    /// the raw file something other than what the vendor sent, and the whole reproducibility claim is
    /// that it is not.</para>
    ///
    /// <para>The COLUMN ORDER is the same in both, and that is this build's requirement of a source
    /// rather than a discovery about one: open time, the four prices, volume, close time. For the
    /// second source it is as unverified as its URL is — nothing here has seen the vendor's real
    /// answer — and the loopback harness serves what this build would accept.</para>
    /// </summary>
    static IEnumerable<string> RowsOf(string path)
    {
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var line in File.ReadLines(path))
            {
                var row = line.Trim();
                if (row.Length == 0 || row.StartsWith("open_time", StringComparison.OrdinalIgnoreCase)) continue;
                yield return row;
            }
            yield break;
        }

        using var zip = ZipFile.OpenRead(path);
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

    static void Write(string destFile, SortedDictionary<DateTimeOffset, Priced> bars, bool candlesCarryVolume)
    {
        var dir = System.IO.Path.GetDirectoryName(destFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // LF and no BOM, written the same way on every platform, because the file's SHA-256 is the
        // dataset's identity and a line ending is not a difference of opinion.
        using var stream = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" };
        writer.WriteLine(candlesCarryVolume ? Header : HeaderWithQuality);
        foreach (var (at, priced) in bars)
            writer.WriteLine(candlesCarryVolume
                ? $"{at.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{priced.Text}"
                : $"{at.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{priced.Text},{priced.Quality}");
    }

    static string Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Sha256Hex.Of(stream);
    }
}

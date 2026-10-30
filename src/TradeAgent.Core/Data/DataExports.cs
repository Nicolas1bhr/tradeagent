using System.Globalization;
using System.Text;
using TradeAgent.Core.Db;

namespace TradeAgent.Core.Data;

/// <summary>
/// THE RESEARCH MONTHS OF A DATASET, AS A FILE IN THE CALLER'S OWN FOLDER (<c>U-evidence-campaign</c> item 3; the MVP survey's "executors'
/// code cannot read app datasets as files"): what <c>data-export</c> writes, and the bounds it writes it under.
///
/// <para><b>Never a held-back bar.</b> The bars are read by <see cref="DatasetReader.Read"/> with the caller's pipe holdout — the dataset's
/// own cutoff first (<see cref="Holdout.Refusal"/>), then every other window and every fresh holdout being collected
/// (<see cref="TapeHoldout"/>), the same two refusals the evaluator's <see cref="BarFeed"/> asks — and a window reaching any of them
/// is REFUSED with nothing written, never clipped. An export is a read: the caller's code reading the file learns exactly what a
/// <c>data-bars</c> answer over the same window would have told its model.</para>
///
/// <para><b>Bounded.</b> At most <see cref="MaxBars"/> bars a file and <see cref="MaxFiles"/> files in the folder, so a position cannot
/// fill the machine's disk by asking; a file of the same window is written again in place.</para>
/// </summary>
public static class DataExports
{
    /// <summary>The most bars one export writes: about 139 days of one-minute bars, some 12 MB.</summary>
    public const int MaxBars = 200_000;

    /// <summary>The most export files the folder may hold; a position deletes one of its own to make room.</summary>
    public const int MaxFiles = 4;

    /// <summary>The folder, relative to the caller's home, that exports are written to.</summary>
    public const string Folder = "data";

    /// <summary>The prefix of every export's file name — what the folder count counts.</summary>
    public const string Prefix = "export-";

    /// <summary>
    /// The file the bars from <paramref name="first"/> to <paramref name="last"/> (their open times) of one dataset are written to,
    /// relative to the caller's home: named by the bars it holds, to the second, so two exports share a name only when they hold the
    /// same bars (the Codex review's P2: a name cut to the asked minutes made different windows overwrite each other).
    /// </summary>
    public static string FileName(DatasetRecord set, DateTimeOffset first, DateTimeOffset last)
    {
        ArgumentNullException.ThrowIfNull(set);
        return $"{Folder}/{Prefix}{set.Pair}-{set.Interval}-dataset{set.Id.ToString(CultureInfo.InvariantCulture)}-"
               + $"{first.UtcDateTime.ToString("yyyyMMddTHHmmss", CultureInfo.InvariantCulture)}-"
               + $"{last.UtcDateTime.ToString("yyyyMMddTHHmmss", CultureInfo.InvariantCulture)}.csv";
    }

    /// <summary>The file's bytes: the dataset's own columns and each bar's quality, UTC, ascending, nothing filled in.</summary>
    public static byte[] Csv(IReadOnlyList<KlineBar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);
        var text = new StringBuilder(64 * (bars.Count + 1)).Append(KlineNormaliser.HeaderWithQuality).Append('\n');
        foreach (var b in bars)
            text.Append(CultureInfo.InvariantCulture,
                $"{b.OpenTime.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{b.Open},{b.High},{b.Low},{b.Close},{b.Volume},{b.Quality}\n");
        return new UTF8Encoding(false).GetBytes(text.ToString());
    }
}

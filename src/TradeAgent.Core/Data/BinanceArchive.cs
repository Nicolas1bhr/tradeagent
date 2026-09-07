namespace TradeAgent.Core.Data;

/// <summary>
/// WHERE BINANCE'S PUBLIC 1-MINUTE HISTORY LIVES, and what a month of it is called.
///
/// Pure string and calendar work, deliberately: the URL pattern and the checksum sidecar's format
/// are the two things a test has to be able to state without a socket, and the one real download
/// this unit was allowed to make is the evidence that the pattern below is the vendor's. Measured on
/// 2026-09-07 against the live archive:
///
///   https://data.binance.vision/data/spot/monthly/klines/BTCUSDT/1m/BTCUSDT-1m-2026-08.zip
///     -> HTTP 200, content-type application/zip, content-length 2084946
///   the same URL + ".CHECKSUM"
///     -> "acab442e02745177063031d402929703ae983715010064b40cf811c4beb843d4  BTCUSDT-1m-2026-08.zip"
///   the same URL for 2026-09 (the month in progress) -> HTTP 404 from AmazonS3
///
/// The 404 is why <see cref="RecentCompleteMonths"/> never asks for the current month, and the
/// sidecar's exact shape — lowercase hex, two spaces, the file's own name, no trailing newline — is
/// why <see cref="Sha256FromSidecar"/> checks the NAME as well as the hash.
///
/// A public archive grants nothing. Everything here produces a URL or a hash; nothing here downloads,
/// and nothing an agent says reaches it.
/// </summary>
public static class BinanceArchive
{
    /// <summary>What the <c>dataset</c> ledger records as the origin of these bars.</summary>
    public const string Source = "binance-spot-monthly-klines";

    /// <summary>The only interval this unit collects. Bars are 1-minute, closed, UTC.</summary>
    public const string Interval = "1m";

    /// <summary>The vendor's host. A parameter everywhere below so a test can serve loopback instead.</summary>
    public const string BaseUrl = "https://data.binance.vision";

    /// <summary>How many months the owner's one press asks for.</summary>
    public const int MonthsWanted = 12;

    /// <summary>
    /// A PAIR IS PART OF A URL PATH AND OF A DIRECTORY NAME, so it is checked rather than trusted.
    ///
    /// Binance spot symbols are upper-case letters and digits. Anything else — a dot, a slash, a
    /// space, an empty string — is refused here rather than escaped later, because the two consumers
    /// disagree about what is dangerous and only one of them is a URL.
    /// </summary>
    public static bool IsPair(string? pair) =>
        pair is { Length: >= 2 and <= 20 } && pair.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9');

    public static string RequirePair(string? pair) =>
        IsPair(pair)
            ? pair!
            : throw new TradeAgentException(ErrorCode.INVALID_REQUEST,
                $"'{pair}' is not a trading pair this build will ask Binance for. Use upper-case letters " +
                "and digits only, for example BTCUSDT.");

    /// <summary>The archive's own name for one month of one pair, e.g. <c>BTCUSDT-1m-2026-08.zip</c>.</summary>
    public static string FileName(string pair, DateOnly month) =>
        $"{RequirePair(pair)}-{Interval}-{month.Year:D4}-{month.Month:D2}.zip";

    /// <summary>The monthly klines URL. <paramref name="baseUrl"/> is the vendor's host, or a test's.</summary>
    public static string MonthUrl(string baseUrl, string pair, DateOnly month) =>
        $"{baseUrl.TrimEnd('/')}/data/spot/monthly/klines/{RequirePair(pair)}/{Interval}/{FileName(pair, month)}";

    /// <summary>The sidecar beside it. One line: the hash, two spaces, the file's own name.</summary>
    public static string ChecksumUrl(string monthUrl) => monthUrl + ".CHECKSUM";

    /// <summary>
    /// The published hash out of a <c>.CHECKSUM</c> body, or null when this is not a sidecar for
    /// <paramref name="expectedFileName"/>.
    ///
    /// Null is not "take it anyway": every caller turns it into a month that is NOT collected. A
    /// sidecar naming a different file is the case that makes checking the name worth the code — it
    /// is a hash that would be pinned against the wrong bytes, and pinning a hash that cannot match
    /// is indistinguishable from pinning one that must not.
    /// </summary>
    public static string? Sha256FromSidecar(string? body, string expectedFileName)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        foreach (var raw in body.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) continue;

            var hash = parts[0];
            // The vendor writes "sha256  name"; some tools write "sha256  *name" for a binary read.
            var named = parts[1].TrimStart('*');

            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) continue;
            if (!string.Equals(named, expectedFileName, StringComparison.Ordinal)) continue;

            return hash.ToLowerInvariant();
        }

        return null;
    }

    /// <summary>
    /// The <paramref name="count"/> most recent COMPLETE months, oldest first.
    ///
    /// The month in progress is never one of them, and that is measured rather than assumed: the
    /// archive answered 404 for 2026-09 on 2026-09-07. Asking for it would turn every press during
    /// the first days of a month into a "not published" entry the owner would read as a fault.
    /// </summary>
    public static IReadOnlyList<DateOnly> RecentCompleteMonths(DateTimeOffset nowUtc, int count = MonthsWanted)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        var newest = new DateOnly(nowUtc.UtcDateTime.Year, nowUtc.UtcDateTime.Month, 1).AddMonths(-1);
        var months = new List<DateOnly>(count);
        for (var i = count - 1; i >= 0; i--) months.Add(newest.AddMonths(-i));
        return months;
    }

    /// <summary>
    /// Where this pair's collected history lives: <c>state/data/binance/&lt;PAIR&gt;/1m/</c>.
    /// App-owned, outside the workspace. See <see cref="Paths.Data"/>.
    /// </summary>
    public static string DatasetDir(string pair) =>
        Path.Combine(Paths.Data, "binance", RequirePair(pair), Interval);

    /// <summary>The vendor's own zips, kept exactly as they arrived so a dataset stays reproducible.</summary>
    public static string RawDir(string pair) => Path.Combine(DatasetDir(pair), "raw");

    /// <summary>How a month is written everywhere the owner or the ledger sees it.</summary>
    public static string MonthName(DateOnly month) => $"{month.Year:D4}-{month.Month:D2}";
}

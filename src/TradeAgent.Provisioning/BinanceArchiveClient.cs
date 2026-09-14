using TradeAgent.Core;
using TradeAgent.Core.Data;

namespace TradeAgent.Provisioning;

/// <summary>What became of one month of the archive. Three of the four are not failures.</summary>
public enum MonthOutcome
{
    /// <summary>The zip is on disk and its bytes match the hash the vendor published beside it.</summary>
    Collected,

    /// <summary>The vendor has not published this month. Recorded, not an error.</summary>
    NotPublished,

    /// <summary>The zip is there and no sidecar names it, so there is nothing to pin against.</summary>
    ChecksumNotPublished,

    /// <summary>The bytes disagreed with the published hash. The file was thrown away.</summary>
    ChecksumMismatch,

    /// <summary>
    /// THE VENDOR WAS NEVER ASKED, OR NEVER ANSWERED — a timeout, a dropped connection, a name that
    /// did not resolve, a 5xx from the CDN. It says NOTHING about whether the month exists.
    ///
    /// It is separate from <see cref="NotPublished"/> because the two used to be the same value, and
    /// on a hosted Windows runner one request that hung for thirty minutes was reported to the owner
    /// as "Binance has not published 2026-08". A guess wearing the words of an answer is the defect
    /// <c>IAtasAdapter</c> rule 3 forbids on the order path; the data path gets the same rule.
    /// </summary>
    Unreachable
}

/// <summary>
/// One raw archive file as it sits on disk, with everything the ledger records about it.
///
/// <para><see cref="Month"/> is the PERIOD'S NAME, which for Binance's monthly archive is its month
/// (<c>2026-08</c>) and for a source whose periods are day windows is those days
/// (<c>2026-06-16..2026-09-13</c>). <see cref="PublishedSha256"/> is EMPTY where the vendor publishes
/// no hash, and never this app's own computed one — see <see cref="CandleSourceClient"/>.</para>
/// </summary>
public sealed record RawMonth(
    string Month,
    string Url,
    string PublishedSha256,
    string ComputedSha256,
    long Bytes,
    DateTimeOffset DownloadedAt,
    string Path);

/// <summary>The answer for one period. <see cref="File"/> is set only for <see cref="MonthOutcome.Collected"/>.</summary>
public sealed record MonthResult(string Month, MonthOutcome Outcome, RawMonth? File, string Detail);

/// <summary>
/// BINANCE'S HALF OF <see cref="CandleSourceClient"/>, and the name the rest of this build knows it by.
///
/// <para>Everything here now delegates: <see cref="BinanceCandleSource"/> says what the URLs and the
/// months are and <see cref="CandleSourceClient"/> fetches them, so there is ONE implementation of
/// "fetch a period and pin it to what the vendor published" rather than one per source. The behaviour
/// is unchanged and the bytes are unchanged — <c>CandleSourceTests</c> holds the normalised file to the
/// SHA-256 the build before this unit produced.</para>
///
/// <b>THE SIDECAR IS THE WHOLE INTEGRITY STORY AND IT IS PINNED, NOT READ.</b> The archive is a
/// public S3 bucket behind a CDN; there is no signature under these bytes and no account behind the
/// request. What the vendor does publish is a <c>.CHECKSUM</c> beside every month, so the sidecar is
/// fetched FIRST and handed to <see cref="Integrity.Pinned"/>, which throws the file away when the
/// bytes disagree. A month whose sidecar cannot be read is simply not collected — twelve months minus
/// one is a coverage figure the ledger reports, and it is a far better outcome than a month of
/// unidentified bytes.
///
/// The folder is <c>state/data/binance/&lt;PAIR&gt;/1m/raw/</c> and it is NOT in the agent's
/// workspace: nothing the AI can write reaches it, and there is no verb and no pipe op that puts a
/// file there. The AI reads what comes out of it through <c>data-list</c> and <c>data-bars</c>.
/// </summary>
public sealed class BinanceArchiveClient(string baseUrl = BinanceArchive.BaseUrl, TimeSpan? requestTimeout = null)
{
    /// <summary>
    /// What one request may spend before it is given up on: <see cref="Downloader"/>'s shared client
    /// timeout, which is what these requests already spent in production.
    ///
    /// It is a ceiling and not a target — the sidecar keeps its own thirty seconds below it. The
    /// point of naming it is that a caller can pass a smaller one: the suite drives a loopback
    /// server, so a harness that stops answering there should cost the runner seconds. It cost one
    /// windows-latest job thirty minutes, once.
    /// </summary>
    public static readonly TimeSpan DefaultRequestTimeout = CandleSourceClient.DefaultRequestTimeout;

    readonly CandleSourceClient _client = new(requestTimeout);

    public string BaseUrl { get; } = baseUrl;

    /// <inheritdoc cref="DefaultRequestTimeout"/>
    public TimeSpan RequestTimeout => _client.RequestTimeout;

    /// <summary>The one fetcher, with this client's own timeout, so a caller driving the source
    /// interface directly spends what this client was built to spend.</summary>
    public CandleSourceClient Fetcher => _client;

    /// <summary>Binance's archive as one implementation of <see cref="ICandleSource"/>, pointed at <see cref="BaseUrl"/>.</summary>
    public BinanceCandleSource Source(int months = BinanceArchive.MonthsWanted) => new(BaseUrl, months);

    /// <summary>
    /// Downloads one month, or says why it did not.
    ///
    /// A month the vendor has not published is <see cref="MonthOutcome.NotPublished"/> and is not an
    /// error: the archive is published on the vendor's own schedule and a twelve-month ask near the
    /// start of a month legitimately finds eleven.
    /// </summary>
    public Task<MonthResult> FetchMonthAsync(
        string pair, DateOnly month, string rawDir,
        IProgress<ProvisionProgress>? progress = null, CancellationToken ct = default)
    {
        var url = BinanceArchive.MonthUrl(BaseUrl, pair, month);
        var period = new CandlePeriod(
            BinanceArchive.MonthName(month), url, BinanceArchive.ChecksumUrl(url),
            BinanceArchive.FileName(pair, month));

        return _client.FetchPeriodAsync(Source(), period, rawDir, progress, ct);
    }

    /// <summary>
    /// THE OWNER'S ONE PRESS: the twelve most recent COMPLETE months of one pair, oldest first.
    ///
    /// Every month is reported, including the ones that produced no file — a coverage TARGET of
    /// twelve with the ACTUAL coverage recorded beside it is what <c>docs/COUNCIL.md</c> asks for,
    /// and a loop that returned only what it got would leave the difference invisible. Nothing here
    /// throws for a missing month: the vendor publishes on its own schedule.
    /// </summary>
    public Task<IReadOnlyList<MonthResult>> CollectMonthsAsync(
        string pair, DateTimeOffset nowUtc, int months = BinanceArchive.MonthsWanted,
        IProgress<ProvisionProgress>? progress = null, CancellationToken ct = default) =>
        _client.CollectAsync(Source(months), pair, nowUtc, progress, ct);
}

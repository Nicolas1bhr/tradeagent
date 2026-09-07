using System.Net;
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
    ChecksumMismatch
}

/// <summary>One raw archive file as it sits on disk, with everything the ledger records about it.</summary>
public sealed record RawMonth(
    string Month,
    string Url,
    string PublishedSha256,
    string ComputedSha256,
    long Bytes,
    DateTimeOffset DownloadedAt,
    string Path);

/// <summary>The answer for one month. <see cref="File"/> is set only for <see cref="MonthOutcome.Collected"/>.</summary>
public sealed record MonthResult(string Month, MonthOutcome Outcome, RawMonth? File, string Detail);

/// <summary>
/// Fetches months of Binance's public spot klines archive into an app-owned folder.
///
/// <b>THE SIDECAR IS THE WHOLE INTEGRITY STORY AND IT IS PINNED, NOT READ.</b> The archive is a
/// public S3 bucket behind a CDN; there is no signature under these bytes and no account behind the
/// request. What the vendor does publish is a <c>.CHECKSUM</c> beside every month, so the sidecar is
/// fetched FIRST and handed to <see cref="Integrity.Pinned"/>, which throws the file away when the
/// bytes disagree. <see cref="Integrity.Unverified"/> appears nowhere in this file on purpose: it
/// would turn a mismatch into a recorded decision and keep the bytes, and a dataset built from bytes
/// nobody could check is one whose provenance row is a description of nothing. A month whose sidecar
/// cannot be read is simply not collected — twelve months minus one is a coverage figure the ledger
/// reports, and it is a far better outcome than a month of unidentified bytes.
///
/// The folder is <c>state/data/binance/&lt;PAIR&gt;/1m/raw/</c> and it is NOT in the agent's
/// workspace: nothing the AI can write reaches it, and there is no verb and no pipe op that puts a
/// file there. The AI reads what comes out of it through <c>data-list</c> and <c>data-bars</c>.
/// </summary>
public sealed class BinanceArchiveClient(string baseUrl = BinanceArchive.BaseUrl)
{
    /// <summary>A sidecar is one short line. Anything larger is not one.</summary>
    const int SidecarMaxBytes = 4096;

    static readonly TimeSpan SidecarTimeout = TimeSpan.FromSeconds(30);

    public string BaseUrl { get; } = baseUrl;

    /// <summary>
    /// Downloads one month, or says why it did not.
    ///
    /// A month the vendor has not published is <see cref="MonthOutcome.NotPublished"/> and is not an
    /// error: the archive is published on the vendor's own schedule and a twelve-month ask near the
    /// start of a month legitimately finds eleven.
    /// </summary>
    public async Task<MonthResult> FetchMonthAsync(
        string pair, DateOnly month, string rawDir,
        IProgress<ProvisionProgress>? progress = null, CancellationToken ct = default)
    {
        var name = BinanceArchive.FileName(pair, month);
        var url = BinanceArchive.MonthUrl(BaseUrl, pair, month);
        var monthName = BinanceArchive.MonthName(month);
        var dest = Path.Combine(rawDir, name);

        Directory.CreateDirectory(rawDir);
        TryDelete(dest);

        var sidecar = await Downloader.TryGetSmallTextAsync(
            BinanceArchive.ChecksumUrl(url), SidecarMaxBytes, SidecarTimeout, ct);

        var published = BinanceArchive.Sha256FromSidecar(sidecar, name);
        if (published is null)
        {
            // Which of the two silences this is decides what the owner is told, so it is asked
            // rather than guessed: the month may simply not exist yet.
            var status = await Downloader.TryStatusAsync(url, ct);
            return status is HttpStatusCode.OK
                ? new MonthResult(monthName, MonthOutcome.ChecksumNotPublished, null,
                    $"{name} is published but no .CHECKSUM names it, so there is nothing to check its bytes against")
                : new MonthResult(monthName, MonthOutcome.NotPublished, null,
                    $"Binance has not published {name}");
        }

        try
        {
            await Downloader.DownloadAsync(url, dest, Integrity.Pinned(published),
                progress, ct, ErrorCode.MARKET_DATA_UNAVAILABLE);
        }
        catch (TradeAgentException ex) when (ex.Info.Code == ErrorCode.MARKET_DATA_UNAVAILABLE)
        {
            TryDelete(dest);
            return new MonthResult(monthName, MonthOutcome.ChecksumMismatch, null,
                $"{name} did not match the checksum Binance published for it, so it was thrown away");
        }
        catch (TradeAgentException)
        {
            TryDelete(dest);
            return new MonthResult(monthName, MonthOutcome.NotPublished, null,
                $"Binance has not published {name}");
        }

        // Computed here as well as inside the download, and recorded beside the published hash
        // rather than in place of it: the ledger's reproducibility check re-reads THIS number off
        // the file later, and a row carrying only the vendor's figure could not tell a file that
        // changed on disk from one that was never right.
        var computed = await Downloader.Sha256Async(dest, ct);
        return new MonthResult(monthName, MonthOutcome.Collected,
            new RawMonth(monthName, url, published, computed, new FileInfo(dest).Length,
                DateTimeOffset.UtcNow, dest),
            $"{name} collected");
    }

    /// <summary>
    /// THE OWNER'S ONE PRESS: the twelve most recent COMPLETE months of one pair, oldest first.
    ///
    /// Every month is reported, including the ones that produced no file — a coverage TARGET of
    /// twelve with the ACTUAL coverage recorded beside it is what <c>docs/COUNCIL.md</c> asks for,
    /// and a loop that returned only what it got would leave the difference invisible. Nothing here
    /// throws for a missing month: the vendor publishes on its own schedule.
    /// </summary>
    public async Task<IReadOnlyList<MonthResult>> CollectMonthsAsync(
        string pair, DateTimeOffset nowUtc, int months = BinanceArchive.MonthsWanted,
        IProgress<ProvisionProgress>? progress = null, CancellationToken ct = default)
    {
        var rawDir = BinanceArchive.RawDir(pair);
        var results = new List<MonthResult>(months);

        foreach (var month in BinanceArchive.RecentCompleteMonths(nowUtc, months))
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await FetchMonthAsync(pair, month, rawDir, progress, ct));
        }

        return results;
    }

    static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

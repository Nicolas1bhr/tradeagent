using System.Net;
using TradeAgent.Core;
using TradeAgent.Core.Data;

namespace TradeAgent.Provisioning;

/// <summary>
/// FETCHES THE PERIODS OF ANY <see cref="ICandleSource"/> INTO AN APP-OWNED FOLDER. The one place in
/// this product that opens a socket for market history.
///
/// <para><b>THE SIDECAR IS THE INTEGRITY STORY WHERE THERE IS ONE, AND IT IS PINNED, NOT READ.</b>
/// A source that DECLARES a published checksum (<see cref="ICandleSource.PublishesChecksum"/>) has its
/// sidecar fetched FIRST and handed to <see cref="Integrity.Pinned"/>, which throws the file away when
/// the bytes disagree; a period whose sidecar cannot be read is simply not collected, because twelve
/// periods minus one is a coverage figure the ledger reports and a period of unidentified bytes is
/// not.</para>
///
/// <para><b>A source that publishes NO checksum is recorded as one, and is never recorded as
/// verified.</b> Its bytes are taken under <see cref="Integrity.Unverified"/> — which writes the
/// decision into the owner's activity log before the file is used — and the provenance row carries an
/// EMPTY published hash. Putting this app's own computed hash in that column would make a source
/// nobody can check indistinguishable from one that was checked against the vendor, which is the
/// difference the column exists to keep. The computed hash is still recorded, and it is what
/// <c>DatasetStore.Checked</c> re-reads: it proves the file has not changed SINCE, which is a
/// different claim from proving it is what the vendor meant to publish, and the two are never merged.</para>
///
/// <para>The folder is under <c>state/data/</c> and is NOT in the agent's workspace: nothing the AI
/// can write reaches it, and there is no verb and no pipe op that puts a file there.</para>
/// </summary>
public sealed class CandleSourceClient(TimeSpan? requestTimeout = null)
{
    /// <summary>A sidecar is one short line. Anything larger is not one.</summary>
    const int SidecarMaxBytes = 4096;

    static readonly TimeSpan SidecarTimeout = TimeSpan.FromSeconds(30);

    /// <inheritdoc cref="BinanceArchiveClient.DefaultRequestTimeout"/>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromMinutes(30);

    /// <inheritdoc cref="BinanceArchiveClient.DefaultRequestTimeout"/>
    public TimeSpan RequestTimeout { get; } = requestTimeout ?? DefaultRequestTimeout;

    /// <summary>The sidecar's own leash, never longer than the one the caller asked for.</summary>
    TimeSpan SidecarLeash => RequestTimeout < SidecarTimeout ? RequestTimeout : SidecarTimeout;

    /// <summary>
    /// Downloads one period, or says why it did not.
    ///
    /// A period the vendor has not published is <see cref="MonthOutcome.NotPublished"/> and is not an
    /// error: an archive is published on the vendor's own schedule and a twelve-month ask near the
    /// start of a month legitimately finds eleven.
    /// </summary>
    public async Task<MonthResult> FetchPeriodAsync(
        ICandleSource source, CandlePeriod period, string rawDir,
        IProgress<ProvisionProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(period);

        var name = period.FileName;
        var dest = Path.Combine(rawDir, name);

        Directory.CreateDirectory(rawDir);
        TryDelete(dest);

        string? published = null;

        // THE GUARD. A source that says the vendor publishes a hash MUST produce one before a byte is
        // kept; a source that says it does not is downloaded unpinned AND RECORDED AS UNPINNED. The
        // one thing that must never happen is the second case wearing the first's clothes.
        if (source.PublishesChecksum && period.ChecksumUrl is { } sidecarUrl)
        {
            var sidecar = await Downloader.TryGetSmallTextAsync(sidecarUrl, SidecarMaxBytes, SidecarLeash, ct);
            published = BinanceArchive.Sha256FromSidecar(sidecar, name);

            if (published is null)
            {
                // WHICH SILENCE THIS IS DECIDES WHAT THE OWNER IS TOLD, so it is asked rather than
                // guessed — and the answer to the question is not the same thing as failing to ask it.
                // ONLY a 404 is the vendor saying the period does not exist. A server that never
                // answered is `Unreachable` and says so in words: reporting it as "not published" would
                // put a fact in the ledger that nobody measured, and the coverage figure a strategy is
                // judged on would then describe the network rather than the archive.
                var status = await Downloader.StatusAsync(period.Url, RequestTimeout, ct);
                return status.Status switch
                {
                    HttpStatusCode.OK => new MonthResult(period.Name, MonthOutcome.ChecksumNotPublished, null,
                        $"{name} is published but no .CHECKSUM names it, so there is nothing to check its bytes against"),
                    HttpStatusCode.NotFound => new MonthResult(period.Name, MonthOutcome.NotPublished, null,
                        $"{source.DisplayName} has not published {name}"),
                    { } other => new MonthResult(period.Name, MonthOutcome.Unreachable, null,
                        $"{source.DisplayName} answered {(int)other} for {name}, which says nothing about whether that period exists"),
                    _ => new MonthResult(period.Name, MonthOutcome.Unreachable, null,
                        $"{name} could not be asked about: {status.Because}")
                };
            }
        }

        try
        {
            await Downloader.DownloadAsync(period.Url, dest,
                published is null ? Unpinned(source, name) : Integrity.Pinned(published),
                progress, ct, ErrorCode.MARKET_DATA_UNAVAILABLE);
        }
        catch (TradeAgentException ex) when (published is not null && ex.Info.Code == ErrorCode.MARKET_DATA_UNAVAILABLE)
        {
            TryDelete(dest);
            return new MonthResult(period.Name, MonthOutcome.ChecksumMismatch, null,
                $"{name} did not match the checksum {source.DisplayName} published for it, so it was thrown away");
        }
        catch (TradeAgentException ex)
        {
            // WHATEVER WENT WRONG WITH THE BYTES, IT WAS NOT THE VENDOR SAYING THERE IS NO SUCH
            // PERIOD. This branch used to answer "not published" to a CDN 503 and to a body that
            // stopped short.
            TryDelete(dest);

            // A SOURCE WITH NO SIDECAR HAS NO OTHER WAY TO SAY "there is no such period", so the one
            // question that distinguishes the two is asked here — and only here, where a download has
            // already failed. A question that cannot be answered leaves the outcome Unreachable, which
            // is the reading that claims nothing.
            if (published is null && await NotPublished(period.Url, ct))
                return new MonthResult(period.Name, MonthOutcome.NotPublished, null,
                    $"{source.DisplayName} has not published {name}");

            return new MonthResult(period.Name, MonthOutcome.Unreachable, null,
                $"{name} did not arrive from {source.DisplayName}: {ex.Message}");
        }

        // Computed here as well as inside the download, and recorded BESIDE the published hash rather
        // than in place of it: the ledger's reproducibility check re-reads THIS number off the file
        // later, and a row carrying only the vendor's figure could not tell a file that changed on
        // disk from one that was never right.
        var computed = await Downloader.Sha256Async(dest, ct);
        return new MonthResult(period.Name, MonthOutcome.Collected,
            // THE PUBLISHED HASH IS EMPTY WHERE THE VENDOR PUBLISHES NONE. Never the computed one.
            new RawMonth(period.Name, period.Url, published ?? "", computed, new FileInfo(dest).Length,
                DateTimeOffset.UtcNow, dest),
            $"{name} collected");
    }

    /// <summary>
    /// The recorded decision for a source that publishes no checksum. <see cref="Integrity.Unverified"/>
    /// writes it into the owner's activity log before the bytes are used; there is no way to take
    /// unpinned bytes without one.
    /// </summary>
    static Integrity Unpinned(ICandleSource source, string name) => Integrity.Unverified(
        $"{source.DisplayName} publishes no checksum beside {name}, so these bytes are recorded with the hash "
        + "TradeAgent computed and with NO published hash to check them against");

    /// <summary>
    /// Whether the vendor answers 404 for this URL — asked only after an unpinned download failed, so
    /// that the one outcome meaning "there is no such period" is still reachable for a source with no
    /// sidecar to ask about. A question that cannot be answered leaves the outcome Unreachable.
    /// </summary>
    async Task<bool> NotPublished(string url, CancellationToken ct)
    {
        try { return (await Downloader.StatusAsync(url, RequestTimeout, ct)).Status == HttpStatusCode.NotFound; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// ONE PRESS: every period this source declares for this symbol, oldest first.
    ///
    /// Every period is reported, including the ones that produced no file — a coverage TARGET with the
    /// ACTUAL coverage recorded beside it is what <c>docs/COUNCIL.md</c> asks for, and a loop that
    /// returned only what it got would leave the difference invisible.
    /// </summary>
    public async Task<IReadOnlyList<MonthResult>> CollectAsync(
        ICandleSource source, string symbol, DateTimeOffset nowUtc,
        IProgress<ProvisionProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var rawDir = source.RawDir(symbol);
        var periods = source.Periods(symbol, nowUtc);
        var results = new List<MonthResult>(periods.Count);

        foreach (var period in periods)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await FetchPeriodAsync(source, period, rawDir, progress, ct));
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

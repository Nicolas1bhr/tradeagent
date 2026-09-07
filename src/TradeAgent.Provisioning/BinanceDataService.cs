using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;

namespace TradeAgent.Provisioning;

/// <summary>
/// What one press of "Download 12 months" produced: the dataset if one was accepted, every month's
/// outcome including the ones that produced no file, and a sentence for the owner's screen.
/// </summary>
public sealed record DataCollection(DatasetRecord? Dataset, IReadOnlyList<MonthResult> Months, string Summary);

/// <summary>
/// COLLECT, NORMALISE, RECORD — the whole of the owner's one press, and the only thing that ever
/// writes the dataset ledger.
///
/// It lives here rather than in the gateway on purpose. The gateway is the agent-facing surface and
/// everything on it is reachable by the AI; this is reachable from the app's own window and from
/// nowhere else. There is no verb, no pipe op and no path from the agent into
/// <see cref="CollectAsync"/> or <see cref="Rebuild"/>.
/// </summary>
public sealed class BinanceDataService(Database db, BinanceArchiveClient? client = null)
{
    readonly BinanceArchiveClient _client = client ?? new BinanceArchiveClient();
    readonly DatasetStore _store = new(db);

    public DatasetStore Store => _store;

    /// <summary>
    /// Fetches the twelve most recent complete months of <paramref name="pair"/>, normalises what
    /// arrived into one dataset file, and records the whole provenance.
    ///
    /// A collection that produced no file at all records nothing: a dataset row with no bars would
    /// be a provenance record for an absence, and "no dataset yet" is already what an empty ledger
    /// says.
    /// </summary>
    public async Task<DataCollection> CollectAsync(
        string pair, DateTimeOffset nowUtc,
        IProgress<ProvisionProgress>? progress = null, CancellationToken ct = default)
    {
        pair = BinanceArchive.RequirePair(pair);

        var months = await _client.CollectMonthsAsync(pair, nowUtc, BinanceArchive.MonthsWanted, progress, ct);
        var collected = months.Where(m => m.Outcome == MonthOutcome.Collected && m.File is not null)
                              .Select(m => m.File!)
                              .OrderBy(f => f.Month, StringComparer.Ordinal)
                              .ToList();

        if (collected.Count == 0)
            return new DataCollection(null, months,
                $"Binance published none of the {months.Count} months asked for, so there is no dataset to record.");

        // THE EARLIEST download time in the collection is what an incomplete bar is measured
        // against, not the latest: a bar is only provably closed if it closed before the EARLIEST
        // moment any of these files was read. Choosing the latest would let a bar that was still
        // forming when its own month was fetched pass as closed because a later month arrived after
        // it.
        var readAt = collected.Min(f => f.DownloadedAt);
        var version = _store.NextVersion(pair, BinanceArchive.Interval);
        var dest = System.IO.Path.Combine(BinanceArchive.DatasetDir(pair), $"{version}.csv");

        var set = KlineNormaliser.Normalise(
            [.. collected.Select(f => new RawArchiveFile(f.Month, f.Path))], readAt, dest);

        var record = Recorded(pair, version, months, collected, set, DateTimeOffset.UtcNow);
        return new DataCollection(record, months, Describe(record, months));
    }

    /// <summary>
    /// Normalises the raw files an accepted dataset was built from ALL OVER AGAIN, and records the
    /// result as a new version.
    ///
    /// This is the reproducibility claim made operable: the same raw files produce a normalised file
    /// with the same SHA-256, every time, on any machine. It REFUSES when a raw file no longer
    /// hashes to the row that recorded it — that dataset is rejected instead, and the bytes that
    /// changed are never normalised into anything. A rebuild that quietly re-derived from an altered
    /// file would hand back a dataset with a provenance record describing bytes that are gone.
    /// </summary>
    public DataCollection Rebuild(string pair)
    {
        pair = BinanceArchive.RequirePair(pair);

        var existing = _store.Newest(pair);
        if (existing is null)
            return new DataCollection(null, [], $"There is no {pair} dataset to rebuild.");

        var verified = _store.Checked(existing);
        if (verified.State == DatasetState.REJECTED)
            return new DataCollection(verified, [],
                $"The {pair} dataset was NOT rebuilt: {verified.RejectedReason}. Collect the months again.");

        var version = _store.NextVersion(pair, BinanceArchive.Interval);
        var dest = System.IO.Path.Combine(BinanceArchive.DatasetDir(pair), $"{version}.csv");
        var readAt = verified.Files.Min(f => f.DownloadedAt);

        var set = KlineNormaliser.Normalise(
            [.. verified.Files.Select(f => new RawArchiveFile(f.Month, f.Path))], readAt, dest);

        var record = new DatasetRecord(
            0, verified.Source, pair, BinanceArchive.Interval, version,
            verified.MonthsAttempted, verified.MonthsPresent, verified.MonthsNotPublished,
            set.Path, set.Sha256, set.Bars, set.FirstBar, set.LastBar,
            set.Gaps, set.GapRuns, set.GapRunsTruncated, set.Duplicates, set.Incomplete, set.Unreadable,
            DateTimeOffset.UtcNow, DatasetState.ACCEPTED, null, verified.Files);

        var id = _store.Record(record);
        var stored = record with { Id = id };
        return new DataCollection(stored, [], Describe(stored, []));
    }

    DatasetRecord Recorded(
        string pair, string version, IReadOnlyList<MonthResult> months,
        IReadOnlyList<RawMonth> collected, NormalisedDataset set, DateTimeOffset acceptedAt)
    {
        var units = set.Months.ToDictionary(m => m.Month, m => m.Unit, StringComparer.Ordinal);

        var record = new DatasetRecord(
            0, BinanceArchive.Source, pair, BinanceArchive.Interval, version,
            months.Count, collected.Count,
            [.. months.Where(m => m.Outcome != MonthOutcome.Collected).Select(m => m.Month)],
            set.Path, set.Sha256, set.Bars, set.FirstBar, set.LastBar,
            set.Gaps, set.GapRuns, set.GapRunsTruncated, set.Duplicates, set.Incomplete, set.Unreadable,
            acceptedAt, DatasetState.ACCEPTED, null,
            [.. collected.Select(f => new DatasetFile(f.Month, f.Url, f.PublishedSha256, f.ComputedSha256,
                f.Bytes, f.DownloadedAt, units.GetValueOrDefault(f.Month, KlineTimeUnit.Microseconds), f.Path))]);

        return record with { Id = _store.Record(record) };
    }

    /// <summary>
    /// The owner's sentence. It states the coverage TARGET and what was actually got, because a
    /// twelve-month ask that found eleven is a normal day at this vendor and a figure with no
    /// denominator beside it is the one an owner misreads as complete.
    /// </summary>
    static string Describe(DatasetRecord set, IReadOnlyList<MonthResult> months)
    {
        var missing = months.Count == 0 ? set.MonthsNotPublished.Count : months.Count - set.MonthsPresent;
        var period = set.FirstBar is { } first && set.LastBar is { } last
            ? $"{first.UtcDateTime:yyyy-MM-dd HH:mm} to {last.UtcDateTime:yyyy-MM-dd HH:mm} UTC"
            : "no bars";

        return $"{set.Pair} {set.Interval}: {set.Bars:N0} bars, {period}. " +
               $"{set.MonthsPresent} of {set.MonthsAttempted} months collected" +
               (missing > 0 ? $" ({missing} not published)" : "") +
               $". {set.Gaps:N0} minutes missing inside that period, {set.Duplicates:N0} duplicate rows dropped, " +
               $"{set.Incomplete:N0} bars excluded because they had not closed when the archive was read.";
    }
}

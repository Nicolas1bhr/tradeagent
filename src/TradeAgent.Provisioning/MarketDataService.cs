using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;

namespace TradeAgent.Provisioning;

/// <summary>
/// What one press of "Download" produced: the dataset if one was accepted, every period's outcome
/// including the ones that produced no file, and a sentence for the owner's screen.
/// </summary>
public sealed record DataCollection(DatasetRecord? Dataset, IReadOnlyList<MonthResult> Months, string Summary)
{
    /// <summary>
    /// Whether this collection RAISED the <c>data</c> wake — a dataset the AI has never been told
    /// about. False for a rebuild that reproduced bytes the ledger already holds, which is a
    /// re-verification and not news. See <see cref="MarketDataService.Wake"/>.
    /// </summary>
    public bool Woke { get; init; }
}

/// <summary>
/// COLLECT, NORMALISE, RECORD — the whole of the owner's one press, and the only thing that ever
/// writes the dataset ledger.
///
/// It lives here rather than in the gateway on purpose. The gateway is the agent-facing surface and
/// everything on it is reachable by the AI; this is reachable from the app's own window and from
/// nowhere else. There is no verb, no pipe op and no path from the agent into
/// <see cref="CollectAsync(ICandleSource, string, DateTimeOffset, IProgress{ProvisionProgress}?, CancellationToken)"/>
/// or <see cref="Rebuild"/>.
///
/// <para><b>It is SOURCE-DRIVEN and no longer Binance's.</b> The interval, the coverage target, the
/// venue and whether the candles carry volume are the <see cref="ICandleSource"/>'s and are recorded
/// from it; nothing here reads <c>BinanceArchive.Interval</c>. A row that said <c>1m</c> over twelve
/// months for a source declaring five minutes over ninety days would be a provenance record of a
/// collection that never happened.</para>
/// </summary>
/// <param name="nudge">
/// What to poke when a collection raises a wake — the mission loop, in the app. Null in a test and in
/// any process with no loop behind it: the ROW is what matters and it is written either way, and a
/// loop that is not there cannot be woken.
/// </param>
public sealed class MarketDataService(Database db, BinanceArchiveClient? client = null, Action? nudge = null)
{
    readonly BinanceArchiveClient _binance = client ?? new BinanceArchiveClient();
    readonly CandleSourceClient _fetcher = new();
    readonly DatasetStore _store = new(db);
    readonly MissionEventStore _wakes = new(db);

    public DatasetStore Store => _store;

    /// <summary>
    /// Fetches the twelve most recent complete months of <paramref name="pair"/> from Binance and
    /// records the whole provenance. The owner's press on the Settings page.
    /// </summary>
    public Task<DataCollection> CollectAsync(
        string pair, DateTimeOffset nowUtc,
        IProgress<ProvisionProgress>? progress = null, CancellationToken ct = default) =>
        CollectAsync(_binance.Source(), _binance.Fetcher, pair, nowUtc, progress, ct);

    /// <summary>
    /// Fetches every period <paramref name="source"/> declares for <paramref name="symbol"/>,
    /// normalises what arrived into one dataset file, and records the whole provenance.
    ///
    /// A collection that produced no file at all records nothing: a dataset row with no bars would
    /// be a provenance record for an absence, and "no dataset yet" is already what an empty ledger
    /// says.
    /// </summary>
    public Task<DataCollection> CollectAsync(
        ICandleSource source, string symbol, DateTimeOffset nowUtc,
        IProgress<ProvisionProgress>? progress = null, CancellationToken ct = default) =>
        CollectAsync(source, _fetcher, symbol, nowUtc, progress, ct);

    async Task<DataCollection> CollectAsync(
        ICandleSource source, CandleSourceClient fetcher, string symbol, DateTimeOffset nowUtc,
        IProgress<ProvisionProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        symbol = source.RequireSymbol(symbol);

        var periods = await fetcher.CollectAsync(source, symbol, nowUtc, progress, ct);
        var collected = periods.Where(m => m.Outcome == MonthOutcome.Collected && m.File is not null)
                               .Select(m => m.File!)
                               .OrderBy(f => f.Month, StringComparer.Ordinal)
                               .ToList();

        if (collected.Count == 0)
            return new DataCollection(null, periods, NothingArrived(source, periods));

        // THE EARLIEST download time in the collection is what an incomplete bar is measured
        // against, not the latest: a bar is only provably closed if it closed before the EARLIEST
        // moment any of these files was read. Choosing the latest would let a bar that was still
        // forming when its own period was fetched pass as closed because a later period arrived
        // after it.
        var readAt = collected.Min(f => f.DownloadedAt);
        var version = _store.NextVersion(symbol, source.Interval);
        var dest = System.IO.Path.Combine(source.DatasetDir(symbol), $"{version}.csv");

        var set = KlineNormaliser.Normalise(
            [.. collected.Select(f => new RawArchiveFile(f.Month, f.Path))], readAt, dest,
            source.CandlesCarryVolume, source.Interval);

        var record = Recorded(source, symbol, version, periods, collected, set, DateTimeOffset.UtcNow);
        return new DataCollection(record, periods, Describe(record, periods)) { Woke = Wake(record) };
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
        var existing = _store.Newest(pair);
        if (existing is null)
            return new DataCollection(null, [], $"There is no {pair} dataset to rebuild.");

        var verified = _store.Checked(existing);
        if (verified.State == DatasetState.REJECTED)
            return new DataCollection(verified, [],
                $"The {pair} dataset was NOT rebuilt: {verified.RejectedReason}. Collect the months again.");

        var version = _store.NextVersion(pair, verified.Interval);
        var dest = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(verified.NormalisedPath) ?? "", $"{version}.csv");
        var readAt = verified.Files.Min(f => f.DownloadedAt);

        // THE ROW'S OWN DECLARATION, not the catalogue's as it stands today. A rebuild has to
        // reproduce the bytes the ledger recorded, and `sources.json` can have been edited since:
        // reading the shape of the file out of a file the owner can change would make the one claim
        // this method exists to make — same inputs, same SHA-256 — depend on something that is not an
        // input.
        var set = KlineNormaliser.Normalise(
            [.. verified.Files.Select(f => new RawArchiveFile(f.Month, f.Path))], readAt, dest,
            verified.SourceCarriesVolume, verified.Interval);

        var record = new DatasetRecord(
            0, verified.Source, pair, verified.Interval, version,
            verified.MonthsAttempted, verified.MonthsPresent, verified.MonthsNotPublished,
            set.Path, set.Sha256, set.Bars, set.FirstBar, set.LastBar,
            set.Gaps, set.GapRuns, set.GapRunsTruncated, set.Duplicates, set.Incomplete, set.Unreadable,
            DateTimeOffset.UtcNow, DatasetState.ACCEPTED, null, verified.Files)
        {
            // THE VENUE AND THE INSTRUMENT THESE BARS ARE OF, recorded with them — a fact about what
            // was fetched, not a lookup, which is why it is carried from the row rather than derived
            // at read time.
            VenueId = verified.VenueId,
            InstrumentSymbol = verified.InstrumentSymbol,
            CoverageTargetDays = verified.CoverageTargetDays,
            SourceCarriesVolume = verified.SourceCarriesVolume,
            MidpointBars = set.MidpointDerived
        };

        var id = _store.Record(record);
        var stored = record with { Id = id };
        return new DataCollection(stored, [], Describe(stored, [])) { Woke = Wake(stored) };
    }

    /// <summary>
    /// <b>VALIDATED DATA ARRIVAL IS A WAKE</b> (<c>docs/COUNCIL.md</c>:89-91), and it is Research's.
    ///
    /// <para><b>Keyed by the dataset's own SHA-256 and never by the press.</b> A rebuild re-derives the
    /// same bytes from the same raw files — that is the whole point of it — so its id is one the queue
    /// already holds and it buys nobody a turn. Keyed by the collection ATTEMPT, an owner pressing
    /// "rebuild" four times would have bought four paid turns to be told the same thing, which is
    /// <c>docs/COUNCIL.md</c>:64 exactly: deduplicate by entity, so a repeated proposal cannot
    /// manufacture senior spend. A dataset whose bytes are genuinely new has a different hash and is
    /// genuinely news.</para>
    ///
    /// <para>A REJECTED dataset raises nothing: the wake says data ARRIVED, and bars nothing will serve
    /// have not. A re-verification raises nothing either — <c>DatasetStore.Checked</c> writes no row
    /// here and never calls this.</para>
    ///
    /// <para>Written by the app, like every other wake. There is no verb and no pipe op that raises
    /// one, so an agent cannot buy itself a turn by asking for data.</para>
    /// </summary>
    bool Wake(DatasetRecord set)
    {
        if (set.State != DatasetState.ACCEPTED || set.Bars == 0) return false;

        var raised = _wakes.Raise(
            MissionEventIds.ForRole(MissionEventIds.Data(set.NormalisedSha256), CouncilRoles.Research),
            MissionEventKind.Data, DateTimeOffset.UtcNow,
            Json.Write(new
            {
                dataset = set.Id,
                source = set.Source,
                pair = set.Pair,
                interval = set.Interval,
                version = set.Version,
                bars = set.Bars,
                midpoint_bars = set.MidpointBars
            }),
            CouncilRoles.Research);

        if (raised) nudge?.Invoke();
        return raised;
    }

    DatasetRecord Recorded(
        ICandleSource source, string symbol, string version, IReadOnlyList<MonthResult> periods,
        IReadOnlyList<RawMonth> collected, NormalisedDataset set, DateTimeOffset acceptedAt)
    {
        var units = set.Months.ToDictionary(m => m.Month, m => m.Unit, StringComparer.Ordinal);

        var record = new DatasetRecord(
            // THE SOURCE'S OWN DECLARATIONS, every one of them, and not a constant: this is the line
            // that used to read `BinanceArchive.Interval`.
            0, source.Id, symbol, source.Interval, version,
            periods.Count, collected.Count,
            [.. periods.Where(m => m.Outcome != MonthOutcome.Collected).Select(m => Missing(source, m))],
            set.Path, set.Sha256, set.Bars, set.FirstBar, set.LastBar,
            set.Gaps, set.GapRuns, set.GapRunsTruncated, set.Duplicates, set.Incomplete, set.Unreadable,
            acceptedAt, DatasetState.ACCEPTED, null,
            [.. collected.Select(f => new DatasetFile(f.Month, f.Url, f.PublishedSha256, f.ComputedSha256,
                f.Bytes, f.DownloadedAt, units.GetValueOrDefault(f.Month, KlineTimeUnit.Microseconds), f.Path))])
        {
            VenueId = source.VenueId,
            InstrumentSymbol = symbol,
            CoverageTargetDays = source.CoverageTargetDays,
            SourceCarriesVolume = source.CandlesCarryVolume,
            MidpointBars = set.MidpointDerived
        };

        return record with { Id = _store.Record(record) };
    }

    /// <summary>
    /// WHAT BECAME OF A PERIOD THAT PRODUCED NO FILE, written into the ledger beside its name.
    ///
    /// The row records the periods that are not in the dataset; a bare name in that list reads as
    /// "the vendor has no such period", and for three of the four ways one can be missing that is not
    /// what happened. A period nobody could reach in particular is a hole in the network, and a
    /// coverage figure that calls it a hole in the archive is a measurement of the wrong thing.
    /// </summary>
    static string Missing(ICandleSource source, MonthResult month) => month.Outcome switch
    {
        MonthOutcome.NotPublished => month.Month,
        MonthOutcome.ChecksumNotPublished => $"{month.Month} (published with no checksum)",
        MonthOutcome.ChecksumMismatch => $"{month.Month} (checksum mismatch)",
        _ => $"{month.Month} ({source.DisplayName} could not be reached)"
    };

    /// <summary>
    /// The sentence for a press that produced nothing at all. "published none of them" is only true
    /// when the vendor actually answered; when it did not, saying so is the whole point.
    /// </summary>
    static string NothingArrived(ICandleSource source, IReadOnlyList<MonthResult> periods)
    {
        var unreachable = periods.Count(m => m.Outcome == MonthOutcome.Unreachable);

        return unreachable == 0
            ? $"{source.DisplayName} published none of the {periods.Count} periods asked for, so there is no dataset to record."
            : $"None of the {periods.Count} periods asked for arrived, and {unreachable} of them could not be reached "
              + $"at all, so nothing was recorded. That is not evidence that {source.DisplayName} has no data for them.";
    }

    /// <summary>
    /// The owner's sentence. It states the coverage TARGET and what was actually got, because a
    /// twelve-month ask that found eleven is a normal day at this vendor and a figure with no
    /// denominator beside it is the one an owner misreads as complete. A period nobody could reach is
    /// counted separately, because it is the one number here that is about this machine's network
    /// rather than about the archive.
    /// </summary>
    static string Describe(DatasetRecord set, IReadOnlyList<MonthResult> months)
    {
        var missing = months.Count == 0 ? set.MonthsNotPublished.Count : months.Count - set.MonthsPresent;
        var unreachable = months.Count(m => m.Outcome == MonthOutcome.Unreachable);
        var period = set.FirstBar is { } first && set.LastBar is { } last
            ? $"{first.UtcDateTime:yyyy-MM-dd HH:mm} to {last.UtcDateTime:yyyy-MM-dd HH:mm} UTC"
            : "no bars";

        return $"{set.Pair} {set.Interval}: {set.Bars:N0} bars, {period}. " +
               $"{set.MonthsPresent} of {set.MonthsAttempted} periods collected" +
               (missing > 0
                   ? unreachable > 0
                       ? $" ({missing} missing, {unreachable} of them because the source could not be reached)"
                       : $" ({missing} not published)"
                   : "") +
               $". {set.CoverageActualDays:N0} days deep against a target of {set.CoverageTargetDays:N0}. " +
               $"{set.Gaps:N0} minutes missing inside that period, {set.Duplicates:N0} duplicate rows dropped, " +
               $"{set.Incomplete:N0} bars excluded because they had not closed when the archive was read." +
               (set.MidpointBars > 0
                   ? $" {set.MidpointBars:N0} of these bars carry NO traded volume: they are midpoint-derived "
                     + "and are never trade evidence."
                   : "");
    }
}

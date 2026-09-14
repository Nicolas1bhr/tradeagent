using TradeAgent.Core.Db;

namespace TradeAgent.Core.Data;

/// <summary>
/// A FEED, OR WHY THERE IS NONE.
///
/// <para>Opening a feed is where the dataset's verdict is taken, so the answer is one of these two
/// and never an exception a caller could forget to expect. A refusal names the dataset and the
/// reason the ledger gave, because the only useful thing to do about a REJECTED dataset is collect
/// the months again and that is a decision somebody has to be told about.</para>
/// </summary>
public sealed record BarFeedOpen
{
    BarFeedOpen(BarFeed? feed, string? refusal)
    {
        Feed = feed;
        Refusal = refusal;
    }

    /// <summary>The feed, or null when the dataset was refused.</summary>
    public BarFeed? Feed { get; }

    /// <summary>Why there is no feed, or null when there is one.</summary>
    public string? Refusal { get; }

    public bool Ok => Feed is not null;

    /// <summary>The refusal's text, or the empty string when a feed came out.</summary>
    public string Why => Refusal ?? "";

    /// <summary>
    /// Whether the refusal is the HOLDOUT's rather than the ledger's. The caller turns the two into
    /// different error codes: a rejected dataset is data this installation can no longer vouch for, and
    /// a withheld window is data it will not show THIS caller — one is repaired by collecting the months
    /// again and the other by asking for an earlier window, so an agent that could not tell them apart
    /// would spend its turn on the wrong repair.
    /// </summary>
    public bool IsHoldout { get; private init; }

    internal static BarFeedOpen Yes(BarFeed feed) => new(feed, null);

    internal static BarFeedOpen No(string reason) => new(null, reason);

    internal static BarFeedOpen Withheld(string reason) => new(null, reason) { IsHoldout = true };
}

/// <summary>
/// EVERY CLOSED BAR OF ONE DATASET, BY ITS LEDGER ID, ASCENDING, IN CHUNKS, WITH NO CAP.
///
/// <para><b>Why this is not <see cref="DatasetReader.Read"/> with a bigger number.</b> That reader
/// answers a pipe op, and its cap is a contract: an agent that asked for a year and got ten thousand
/// bars would be backtesting over a period it did not choose and cannot see the edge of, so the cap
/// REFUSES. A run inside the app is the other case — it wants the whole window and it must not hold
/// it all in memory at once — so this streams the file it is already hashed against and never
/// materialises more than one chunk. Nothing here changes the reader, and nothing here is reachable
/// from the pipe.</para>
///
/// <para><b>The dataset's verdict is taken ONCE, in <see cref="Open"/>.</b> `DatasetStore.Checked`
/// re-hashes every raw archive file and the normalised file on every call; a twelve-month dataset is
/// tens of megabytes and a run reads it in hundreds of chunks. Once is also the only verdict a run
/// can REPORT: a run whose dataset changed state half way through has no one dataset its result is
/// about. The next run reads the ledger again, and a rejected row never comes back.</para>
///
/// <para><b>What a dataset does not have.</b> There are no per-bar quality flags and no instrument
/// increment anywhere in `dataset` or `dataset_file` — the row carries counts, hashes and coverage
/// (`DatasetStore.cs`) — so a feed serves exactly the five prices the vendor published and the
/// evaluator states its intents without an increment applied. What the row DOES carry since
/// `U-venue-catalog` is which venue and instrument the bars are of, which is what a run's increment is
/// looked up by (`venue_instrument`); the number is still not in the dataset and no bar is changed by
/// it. `docs/CONTRACTS.md` says so where a caller will read it.</para>
/// </summary>
public sealed class BarFeed
{
    /// <summary>
    /// Bars per chunk by default. A chunk is what a caller holds at once: 4096 one-minute bars is
    /// under three days and a few hundred kilobytes, and a twelve-month run is 129 of them.
    /// </summary>
    public const int ChunkBars = 4096;

    BarFeed(DatasetRecord dataset) => Dataset = dataset;

    /// <summary>The dataset this feed serves, as the ledger's one verdict left it.</summary>
    public DatasetRecord Dataset { get; }

    /// <summary>
    /// WHAT THESE BARS' VOLUMES ARE, in words, or null when every one of them was traded.
    ///
    /// <para>A feed is where a run gets its bars, so it is where a run has to be able to read that some
    /// of them are MIDPOINT-DERIVED: a candle for which no volume was published at all, whose zero is
    /// not a measurement and which is never trade evidence (<c>docs/COUNCIL.md</c>:164-172). Every bar
    /// this feed yields carries its own <see cref="KlineBar.Quality"/> as well, so a consumer can tell
    /// them apart one at a time; this is the sentence for the whole window, and it is the same sentence
    /// <c>data-bars</c>, the backtest reply and the owner's report carry.</para>
    /// </summary>
    public string? MidpointNote => Dataset.MidpointNote;

    /// <summary>
    /// Opens a feed on one dataset by its ledger id, for a named audience over a named window, or
    /// refuses and says why.
    ///
    /// <para>The refusal is a VALUE. A caller that forgot to check it gets a null feed rather than a
    /// run over bars the ledger will not vouch for.</para>
    ///
    /// <para><b>The window is declared HERE, at the open, and not only at <see cref="Chunks"/>.</b> The
    /// holdout is a property of the window and of the audience together — a run to July over a dataset
    /// held out from June is refused, and the same run to May is served — so the two have to be known at
    /// the one moment this dataset's verdict is taken. A caller that opens for the whole dataset and then
    /// streams a narrower window is asking for more than it reads, and over a dataset with a cutoff that
    /// is exactly the request that must not succeed.</para>
    ///
    /// <para><paramref name="audience"/> is required for the reason it is required on
    /// <see cref="DatasetReader.Read"/>: the refusal is the default path, and a new caller has to say
    /// who is asking before it gets a bar. The only audience that reads past a cutoff is the referee's,
    /// which no assembly outside this one can mint (<see cref="BarAudience"/>).</para>
    /// </summary>
    public static BarFeedOpen Open(DatasetStore store, long datasetId, BarAudience audience,
        DateTimeOffset? from, DateTimeOffset? to)
    {
        ArgumentNullException.ThrowIfNull(store);

        var row = store.ById(datasetId);
        if (row is null)
            return BarFeedOpen.No($"there is no dataset {datasetId} in this installation's ledger");

        var verdict = store.Checked(row);
        if (verdict.State == DatasetState.REJECTED)
            return BarFeedOpen.No(
                $"dataset {datasetId} ({verdict.Pair} {verdict.Interval} {verdict.Version}) is REJECTED " +
                $"and its bars are not served: {verdict.RejectedReason}");

        if (Holdout.Refusal(verdict, audience, from, to) is { } withheld)
            return BarFeedOpen.Withheld(withheld);

        return BarFeedOpen.Yes(new BarFeed(verdict));
    }

    /// <summary>
    /// The dataset's closed bars whose open time is in [<paramref name="from"/>,
    /// <paramref name="to"/>] — both inclusive, both optional — ascending, in chunks of at most
    /// <paramref name="chunkBars"/>.
    ///
    /// <para>A minute with no bar is not in here and nothing stands in for it: the dataset counts its
    /// gaps and fills none (`KlineNormaliser`), and a run counts them again as it crosses them.</para>
    /// </summary>
    public IEnumerable<IReadOnlyList<KlineBar>> Chunks(
        DateTimeOffset? from = null, DateTimeOffset? to = null, int chunkBars = ChunkBars)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkBars, 1);
        return Streamed(from, to, chunkBars);
    }

    /// <summary>The same window, one bar at a time.</summary>
    public IEnumerable<KlineBar> Bars(
        DateTimeOffset? from = null, DateTimeOffset? to = null, int chunkBars = ChunkBars)
    {
        foreach (var chunk in Chunks(from, to, chunkBars))
            foreach (var bar in chunk)
                yield return bar;
    }

    IEnumerable<IReadOnlyList<KlineBar>> Streamed(DateTimeOffset? from, DateTimeOffset? to, int chunkBars)
    {
        var chunk = new List<KlineBar>(chunkBars);

        foreach (var line in File.ReadLines(Dataset.NormalisedPath))
        {
            if (!DatasetReader.TryBar(line, out var bar)) continue;
            if (from is { } lo && bar.OpenTime < lo) continue;
            if (to is { } hi && bar.OpenTime > hi) break;   // the file is written in ascending order

            chunk.Add(bar);
            if (chunk.Count < chunkBars) continue;

            yield return chunk;
            chunk = new List<KlineBar>(chunkBars);
        }

        if (chunk.Count > 0) yield return chunk;
    }
}

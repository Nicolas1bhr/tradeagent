namespace TradeAgent.Core.Data;

/// <summary>
/// WHAT A BAR'S VOLUME COLUMN IS A MEASUREMENT OF.
///
/// <para><c>docs/COUNCIL.md</c>:164-172 — "a candle WITHOUT volume is midpoint-derived and is flagged
/// as such, never as trade evidence". A midpoint-derived candle's prices are the midpoint of a book
/// nobody traded on; its volume is written as <c>0</c> because there is no measured quantity, and a
/// zero that is not distinguishable from a real zero is a claim about depth the source never made.</para>
///
/// <para>Strings and not an enum, for the reason <see cref="Db.MissionEventKind"/> is strings: the
/// column is read by a person as often as by this code, and a quality arriving from a newer build must
/// read as itself rather than as whichever enum member happens to be zero.</para>
/// </summary>
public static class BarQuality
{
    /// <summary>The source published a traded volume for this bar.</summary>
    public const string Traded = "traded";

    /// <summary>No volume was published: the bar is derived from a midpoint and is never trade evidence.</summary>
    public const string MidpointDerived = "midpoint_derived";

    public static bool IsKnown(string? quality) => quality is Traded or MidpointDerived;

    /// <summary>
    /// The quality of a row that names none. A normalised file written before this unit has six
    /// columns and every bar in it came from Binance's archive, which publishes a traded volume on
    /// every kline — so <see cref="Traded"/> is what those rows ARE, not a default standing in for a
    /// fact nobody measured.
    /// </summary>
    public static string Or(string? quality) => IsKnown(quality) ? quality! : Traded;

    /// <summary>
    /// WHAT A DATASET WITH MIDPOINT-DERIVED BARS IN IT SAYS ABOUT ITSELF, or null when there is
    /// nothing to say. <b>One definition, read by four surfaces</b> — <c>data-list</c>,
    /// <c>data-bars</c>, the backtest reply and section 8 of the owner's report — because four copies
    /// of a warning are four wordings, and the one that drifts is the one nobody re-reads.
    ///
    /// <para><c>docs/COUNCIL.md</c>:164-172: a candle without volume is midpoint-derived and is flagged
    /// as such, "never as trade evidence". The sentence has to reach the caller in the ANSWER and not
    /// only in a schema: an agent that ran a backtest over these bars and got back a reply identical to
    /// one over traded bars has been handed a number it cannot read correctly.</para>
    ///
    /// <para>The second case — a source that MAY publish volume-less candles where none of these bars
    /// is one — is stated too, and deliberately. It is a fact about the evidence rather than about
    /// these particular rows, and a caller comparing two datasets from two sources is entitled to know
    /// which of them could contain a candle nobody traded on.</para>
    /// </summary>
    public static string? Note(int midpointBars, int bars, bool sourceCarriesVolume)
    {
        if (midpointBars > 0)
            return $"{midpointBars:N0} of these {bars:N0} bars carry NO traded volume: no volume was "
                   + "published for them, so they are MIDPOINT-DERIVED — a price with no trade behind "
                   + "it — and their volume of 0 is not a measurement. They are never trade evidence, "
                   + "and a figure computed over them says nothing about what would have filled. The "
                   + "'quality' column on each bar says which is which.";

        return sourceCarriesVolume
            ? null
            : "This source may publish candles with no traded volume at all — midpoint-derived, never "
              + "trade evidence — and none of these bars is one. Every bar here carries a volume the "
              + "source published.";
    }
}

/// <summary>
/// ONE PERIOD OF ONE SOURCE'S HISTORY: what to ask the vendor for, what to check it against, and what
/// this installation calls it.
///
/// <para><see cref="ChecksumUrl"/> is null for a source that publishes no checksum, and that is a
/// DECLARATION rather than a fetch that failed — see <see cref="ICandleSource.PublishesChecksum"/>.</para>
/// </summary>
public sealed record CandlePeriod(string Name, string Url, string? ChecksumUrl, string FileName);

/// <summary>
/// THE COLLECTOR, AS AN INTERFACE — because the pipeline used to hold ONE source's answers as
/// constants and could not tell its Binance-specific assumptions from its general ones.
///
/// <para>Before this, <c>BinanceArchive.Interval</c> was the only interval a dataset could be of and
/// <c>MonthsWanted</c> the only coverage a collection could target. A second source declaring five
/// minutes over ninety days (<c>docs/COUNCIL.md</c>:164-172) would have produced a provenance row
/// reading <c>1m</c> over twelve months: a record describing a collection that never happened.</para>
///
/// <para><b>Everything an implementation declares here is recorded on the dataset row</b> — the
/// interval, the coverage target, whether the vendor publishes a checksum and whether its candles
/// carry volume. None of it is inferred from the bytes afterwards, because a fact about a SOURCE is
/// not a fact a reader can recover from a file it produced: a Revolut X dataset whose candles all
/// happen to carry volume is still a dataset from a source that may publish candles without it.</para>
///
/// <para><b>Nothing here downloads.</b> An implementation produces URLs and names; the fetching is
/// <c>CandleSourceClient</c>'s, which is the one place a socket is opened.</para>
/// </summary>
public interface ICandleSource
{
    /// <summary>What the <c>dataset</c> ledger records as the origin of these bars.</summary>
    string Id { get; }

    /// <summary>The vendor's name, for a sentence an owner reads. Never the id, which is for the ledger.</summary>
    string DisplayName { get; }

    /// <summary>The venue these candles are of, as <see cref="VenueCatalog"/> names it.</summary>
    string VenueId { get; }

    /// <summary>The bar length, e.g. <c>1m</c> or <c>5m</c>. Recorded on the row; never assumed.</summary>
    string Interval { get; }

    /// <summary>
    /// HOW DEEP THIS SOURCE IS ASKED TO GO, in UTC days. A TARGET: what actually arrived is measured
    /// and recorded beside it (<c>docs/COUNCIL.md</c>:164-172, "actual depth recorded").
    ///
    /// <para>Days rather than months because the two sources this build knows state their depth in
    /// different units — twelve complete months and ninety days — and a coverage figure that cannot be
    /// compared across sources is a figure nobody can read.</para>
    /// </summary>
    int CoverageTargetDays { get; }

    /// <summary>The URL pattern this source's periods are built from, for the record. See <see cref="Verified"/>.</summary>
    string UrlShape { get; }

    /// <summary>
    /// Whether the VENDOR publishes a hash beside its bytes. False is a declaration about the vendor,
    /// and the provenance row then records no published hash rather than the app's own computed one
    /// standing in for one — a source with no checksum must never read as a source that was verified.
    /// </summary>
    bool PublishesChecksum { get; }

    /// <summary>
    /// Whether every candle this source publishes carries a traded volume. False means a candle may
    /// arrive without one, and the normalised file then carries a per-bar quality column for the whole
    /// dataset — see <see cref="BarQuality"/> and <see cref="KlineNormaliser.HeaderWithQuality"/>.
    /// </summary>
    bool CandlesCarryVolume { get; }

    /// <summary>
    /// Whether anything in this build has confirmed this source's URL shape against the vendor.
    /// Carries the meaning it carries on a runtime manifest and on a venue row: false means nobody
    /// checked, the source is still served, and it is served AS unverified.
    /// </summary>
    bool Verified { get; }

    /// <summary>Where this symbol's collected history lives. App-owned, outside the workspace.</summary>
    string DatasetDir(string symbol);

    /// <summary>The vendor's own bytes, kept exactly as they arrived so a dataset stays reproducible.</summary>
    string RawDir(string symbol);

    /// <summary>
    /// The periods one press asks for, oldest first. Never the period in progress: Binance answers 404
    /// for the month it has not finished publishing (measured 2026-09-07) and a candle for a day that
    /// has not ended is not a closed bar.
    /// </summary>
    IReadOnlyList<CandlePeriod> Periods(string symbol, DateTimeOffset nowUtc);

    /// <summary>The symbol as this source spells it, or a refusal. A symbol is part of a URL path.</summary>
    string RequireSymbol(string? symbol);
}

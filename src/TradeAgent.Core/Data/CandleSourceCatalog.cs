using System.Globalization;

namespace TradeAgent.Core.Data;

/// <summary>
/// ONE CANDLE SOURCE AS THIS INSTALLATION RECORDS IT — the shape of its URL, the depth it is asked
/// for, what it publishes beside its bytes, and WHO SAID SO.
///
/// <para>Settable properties rather than a positional record for the reason
/// <see cref="VenueInstrumentEntry"/> has them: these rows arrive from <c>sources.json</c> as well as
/// from this build, and <c>System.Text.Json</c> reads what the file names and leaves the rest at its
/// default — which for <see cref="Verified"/> is FALSE, the answer a row that says nothing must get.</para>
/// </summary>
public sealed class CandleSourceEntry
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";

    /// <summary>The venue these candles are of. <see cref="VenueCatalog"/> names it.</summary>
    public string VenueId { get; set; } = "";

    /// <summary>The bar length, e.g. <c>1m</c>, <c>5m</c>.</summary>
    public string Interval { get; set; } = "";

    /// <inheritdoc cref="ICandleSource.CoverageTargetDays"/>
    public int CoverageTargetDays { get; set; }

    /// <summary>
    /// The vendor's host, or EMPTY because this build records none. Empty is a real state and is
    /// refused rather than guessed at: see <see cref="CandleSourceCatalog.RevolutXCandles"/>.
    /// </summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// The URL pattern, with <c>{base}</c>, <c>{symbol}</c>, <c>{interval}</c>, <c>{from}</c> and
    /// <c>{to}</c> substituted. DATA, exactly as <c>runtimes.json</c> is data: a vendor that renames a
    /// query parameter should cost a one-line edit, not a rebuild.
    /// </summary>
    public string UrlShape { get; set; } = "";

    /// <summary>The sidecar's pattern, or empty when the vendor publishes no checksum.</summary>
    public string ChecksumUrlShape { get; set; } = "";

    /// <inheritdoc cref="ICandleSource.CandlesCarryVolume"/>
    public bool CandlesCarryVolume { get; set; }

    /// <summary>The extension the raw bytes are kept under. The vendor's own format, never converted.</summary>
    public string RawFileExtension { get; set; } = ".csv";

    /// <summary>Who says so, in words. Never empty on a row this build ships.</summary>
    public string Source { get; set; } = "";

    public DateTimeOffset? RecordedAt { get; set; }

    /// <inheritdoc cref="ICandleSource.Verified"/>
    public bool Verified { get; set; }
}

/// <summary>The catalogue as it stands, or the reason there is none. Never both — see <see cref="CandleSourceCatalog.Read"/>.</summary>
public sealed record CandleSourceCatalogRead(IReadOnlyList<CandleSourceEntry> Sources, string? Unreadable);

/// <summary>
/// THE HISTORICAL CANDLE SOURCES THIS INSTALLATION KNOWS OF, AS DATA.
///
/// <para><b>Exactly the <c>runtimes.json</c> / <c>venues.json</c> pattern</b>
/// (<c>docs/DECISIONS.md</c>:73-78, <c>CLAUDE.md</c>: "vendor commands are data, not code"). A file
/// entry REPLACES the built-in source with the same id and is otherwise appended, and an UNREADABLE
/// override yields NO sources at all rather than the built-ins — a file the owner wrote to correct an
/// endpoint must never silently revert to the shipped guess. An ABSENT file is a different fact and
/// keeps meaning "the built-ins".</para>
///
/// <para><b>THE FACT THAT MAKES THIS FILE NECESSARY: this repository records NO Revolut X
/// public-candles endpoint.</b> <c>docs/RESEARCH-REQUIRED.md</c>:170 has only the SIGNED trading base,
/// and there is no sandbox. So the Revolut X row below ships with an EMPTY
/// <see cref="CandleSourceEntry.BaseUrl"/> and an UNVERIFIED URL shape, the collector refuses to fetch
/// from it in words, and the one real fetch is the account owner's to authorise once they supply the
/// endpoint. Everything about that source in this build is proved against a loopback harness and
/// nothing else. See <c>docs/CONTRACTS.md</c>.</para>
/// </summary>
public static class CandleSourceCatalog
{
    /// <summary>The overridable file, beside <c>runtimes.json</c> and <c>venues.json</c>.</summary>
    public static string OverridePath => Path.Combine(Paths.Home, "sources.json");

    /// <summary>Binance's public monthly spot klines. The id the <c>dataset</c> ledger already carries.</summary>
    public const string BinanceMonthlyKlines = BinanceArchive.Source;

    /// <summary>Revolut X's public candles. NO ENDPOINT IS RECORDED IN THIS REPOSITORY — see the type.</summary>
    public const string RevolutXCandles = "revolut-x-public-candles";

    /// <inheritdoc cref="VenueCatalog.ShippedAt"/>
    public static readonly DateTimeOffset ShippedAt = new(2026, 9, 14, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// What the Revolut X row says about itself, in the owner's words. It is the whole reason that row
    /// is <see cref="CandleSourceEntry.Verified"/> false and its base URL empty.
    /// </summary>
    public const string NoEndpointRecorded =
        "TradeAgent records NO public-candles endpoint for Revolut X: docs/RESEARCH-REQUIRED.md holds "
        + "only the signed trading base, and there is no sandbox. The URL shape below is this build's "
        + "unverified guess at the shape such an endpoint would take, it has never been sent to "
        + "Revolut X, and TradeAgent will not send it: put the real endpoint in sources.json and the "
        + "first real fetch is yours to authorise.";

    const string BinanceMeasured =
        "measured against the live archive on 2026-09-07: one 200, its .CHECKSUM sidecar, and a 404 "
        + "for the month in progress (BUILD-STATUS.md, U-data-binance)";

    /// <summary>
    /// THE CATALOGUE THIS BUILD SHIPS. Two sources, and only one of them has ever been reached.
    ///
    /// <para>Binance is <see cref="CandleSourceEntry.Verified"/> TRUE and that is not a double
    /// standard: its URL shape and its sidecar's format were measured against the live archive once,
    /// by hand, and the measurement is quoted in <c>BinanceArchive</c> and in <c>BUILD-STATUS.md</c>.
    /// Revolut X has never been asked anything at all.</para>
    /// </summary>
    public static List<CandleSourceEntry> BuiltIn() =>
    [
        new CandleSourceEntry
        {
            Id = BinanceMonthlyKlines,
            DisplayName = "Binance",
            VenueId = VenueCatalog.BinanceSpot,
            Interval = BinanceArchive.Interval,
            // Twelve complete months, stated in the one unit both sources can state a depth in.
            CoverageTargetDays = BinanceCandleSource.TwelveMonthsInDays,
            BaseUrl = BinanceArchive.BaseUrl,
            UrlShape = "{base}/data/spot/monthly/klines/{symbol}/{interval}/{symbol}-{interval}-{month}.zip",
            ChecksumUrlShape = "{url}.CHECKSUM",
            CandlesCarryVolume = true,
            RawFileExtension = ".zip",
            Source = BinanceMeasured,
            RecordedAt = ShippedAt,
            Verified = true
        },
        new CandleSourceEntry
        {
            Id = RevolutXCandles,
            DisplayName = "Revolut X",
            VenueId = VenueCatalog.RevolutX,
            // docs/COUNCIL.md:164-172: five minutes, a ninety-day target, actual depth recorded, and a
            // candle WITHOUT volume is midpoint-derived and flagged — never trade evidence.
            Interval = "5m",
            CoverageTargetDays = 90,
            BaseUrl = "",
            UrlShape = "{base}/candles?symbol={symbol}&interval={interval}&start_date={from}&end_date={to}",
            ChecksumUrlShape = "",
            CandlesCarryVolume = false,
            RawFileExtension = ".csv",
            Source = NoEndpointRecorded,
            RecordedAt = ShippedAt,
            Verified = false
        }
    ];

    /// <summary>
    /// The catalogue, or the reason there is none. See the type summary: an unreadable
    /// <c>sources.json</c> yields NO sources at all, and an absent one yields the built-ins.
    /// </summary>
    /// <param name="overridePath">
    /// The file to read instead of <see cref="OverridePath"/>. For tests, which share one
    /// <c>TRADEAGENT_HOME</c> across a whole assembly.
    /// </param>
    public static CandleSourceCatalogRead Read(string? overridePath = null)
    {
        var file = VendorFile.Read<List<CandleSourceEntry>>(overridePath ?? OverridePath);
        if (file.Unreadable is { } why)
            return new([],
                $"TradeAgent found a sources.json in its own folder and could not read it: {why}. No "
                + "candle source is being served from it, and the ones this build ships are NOT standing "
                + "in for it — an endpoint the file was written to correct must never quietly revert to "
                + "the shipped one. Fix the file or remove it.");

        var sources = BuiltIn();
        foreach (var o in file.Value ?? [])
        {
            var i = sources.FindIndex(s => string.Equals(s.Id, o.Id, StringComparison.Ordinal));
            if (i >= 0) sources[i] = o; else sources.Add(o);
        }
        return new(sources, null);
    }

    /// <summary>
    /// The source with this id, as an <see cref="ICandleSource"/>, or a refusal naming what there is.
    /// Binance is its own implementation; everything else is driven by the row.
    /// </summary>
    public static ICandleSource Require(string id, string? overridePath = null, string? baseUrl = null)
    {
        var read = Read(overridePath);
        if (read.Unreadable is { } why)
            throw new TradeAgentException(ErrorCode.MARKET_DATA_UNAVAILABLE, why);

        var entry = read.Sources.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal))
            ?? throw new TradeAgentException(ErrorCode.MARKET_DATA_UNAVAILABLE,
                $"'{id}' is not a candle source TradeAgent knows. It has: "
                + string.Join(", ", read.Sources.Select(s => s.Id)) + ".");

        return Of(entry, baseUrl);
    }

    /// <summary>One row as a collector. Binance keeps its own implementation; see <see cref="BinanceCandleSource"/>.</summary>
    public static ICandleSource Of(CandleSourceEntry entry, string? baseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return entry.Id == BinanceMonthlyKlines
            ? new BinanceCandleSource(baseUrl ?? entry.BaseUrl)
            : new DataCandleSource(entry, baseUrl);
    }
}

/// <summary>
/// BINANCE'S PUBLIC MONTHLY ARCHIVE AS ONE IMPLEMENTATION OF <see cref="ICandleSource"/>.
///
/// <para>Every answer here is <see cref="BinanceArchive"/>'s, unchanged: the URL, the file name, the
/// sidecar and the months are the ones measured against the live archive on 2026-09-07, and the
/// normalised bytes this produces are byte-identical to the ones the build before this unit produced
/// (<c>CandleSourceTests</c> asserts the SHA-256 measured on that build).</para>
/// </summary>
public sealed class BinanceCandleSource(string baseUrl = BinanceArchive.BaseUrl,
    int months = BinanceArchive.MonthsWanted) : ICandleSource
{
    /// <summary>
    /// Twelve complete months as a day count. Approximate BY NATURE — months are not all the same
    /// length — and that is exactly why it is a TARGET: what arrived is measured from the bars
    /// themselves and recorded beside it.
    /// </summary>
    public const int TwelveMonthsInDays = 365;

    public string Id => BinanceArchive.Source;
    public string DisplayName => "Binance";
    public string VenueId => VenueCatalog.BinanceSpot;
    public string Interval => BinanceArchive.Interval;
    public int CoverageTargetDays => months * TwelveMonthsInDays / BinanceArchive.MonthsWanted;
    public string UrlShape => "{base}/data/spot/monthly/klines/{symbol}/{interval}/{symbol}-{interval}-{month}.zip";
    public bool PublishesChecksum => true;
    public bool CandlesCarryVolume => true;
    public bool Verified => true;

    public string BaseUrl { get; } = baseUrl;

    public string DatasetDir(string symbol) => BinanceArchive.DatasetDir(symbol);

    public string RawDir(string symbol) => BinanceArchive.RawDir(symbol);

    public string RequireSymbol(string? symbol) => BinanceArchive.RequirePair(symbol);

    public IReadOnlyList<CandlePeriod> Periods(string symbol, DateTimeOffset nowUtc)
    {
        var pair = RequireSymbol(symbol);
        var periods = new List<CandlePeriod>(months);

        foreach (var month in BinanceArchive.RecentCompleteMonths(nowUtc, months))
        {
            var url = BinanceArchive.MonthUrl(BaseUrl, pair, month);
            periods.Add(new CandlePeriod(
                BinanceArchive.MonthName(month), url, BinanceArchive.ChecksumUrl(url),
                BinanceArchive.FileName(pair, month)));
        }

        return periods;
    }
}

/// <summary>
/// A SOURCE DRIVEN ENTIRELY BY ITS ROW — one window of the most recent complete days, built from the
/// row's URL shape.
///
/// <para><b>An empty base URL is a refusal, in words, and not a request to a relative path.</b> The
/// Revolut X row ships with none because this repository records none, and a build that quietly
/// assembled some host out of the pieces it had would be inventing a vendor endpoint — which is the
/// one thing this unit was told not to do.</para>
/// </summary>
public sealed class DataCandleSource(CandleSourceEntry entry, string? baseUrl = null) : ICandleSource
{
    public string Id => entry.Id;
    public string DisplayName => entry.DisplayName;
    public string VenueId => entry.VenueId;
    public string Interval => entry.Interval;
    public int CoverageTargetDays => entry.CoverageTargetDays;
    public string UrlShape => entry.UrlShape;
    public bool PublishesChecksum => !string.IsNullOrWhiteSpace(entry.ChecksumUrlShape);
    public bool CandlesCarryVolume => entry.CandlesCarryVolume;
    public bool Verified => entry.Verified;

    /// <summary>The row's host, or the one a caller passed instead — a test's loopback listener.</summary>
    public string BaseUrl { get; } = (baseUrl ?? entry.BaseUrl).TrimEnd('/');

    /// <summary>
    /// A SYMBOL IS PART OF A URL AND OF A DIRECTORY NAME, so it is checked rather than trusted — the
    /// same rule <see cref="BinanceArchive.IsPair"/> applies, and for the same two disagreeing consumers.
    /// A hyphen is allowed here because venues outside Binance spell pairs <c>BTC-USD</c>.
    /// </summary>
    public string RequireSymbol(string? symbol) =>
        symbol is { Length: >= 2 and <= 20 }
        && symbol.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '-')
            ? symbol
            : throw new TradeAgentException(ErrorCode.INVALID_REQUEST,
                $"'{symbol}' is not an instrument this build will ask {entry.DisplayName} for. Use "
                + "upper-case letters, digits and hyphens only, for example BTC-USD.");

    public string DatasetDir(string symbol) =>
        Path.Combine(Paths.Data, entry.Id, RequireSymbol(symbol), Interval);

    public string RawDir(string symbol) => Path.Combine(DatasetDir(symbol), "raw");

    public IReadOnlyList<CandlePeriod> Periods(string symbol, DateTimeOffset nowUtc)
    {
        var instrument = RequireSymbol(symbol);

        if (string.IsNullOrWhiteSpace(BaseUrl))
            throw new TradeAgentException(ErrorCode.MARKET_DATA_UNAVAILABLE,
                $"TradeAgent has no endpoint to ask {entry.DisplayName} for candles. {entry.Source}");

        if (CoverageTargetDays <= 0)
            throw new TradeAgentException(ErrorCode.MARKET_DATA_UNAVAILABLE,
                $"{entry.Id} declares a coverage target of {CoverageTargetDays} days, which is not a depth.");

        // THE DAY IN PROGRESS IS NEVER ASKED FOR. A candle for a day that has not ended is not a
        // closed bar, and the same reasoning keeps `RecentCompleteMonths` off the current month.
        var to = DateOnly.FromDateTime(nowUtc.UtcDateTime).AddDays(-1);
        var from = to.AddDays(-(CoverageTargetDays - 1));

        var url = Fill(entry.UrlShape, instrument, from, to, null);
        var name = $"{Day(from)}..{Day(to)}";

        return
        [
            new CandlePeriod(
                name, url,
                PublishesChecksum ? Fill(entry.ChecksumUrlShape, instrument, from, to, url) : null,
                $"{instrument}-{Interval}-{Day(from)}_{Day(to)}{entry.RawFileExtension}")
        ];
    }

    static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    string Fill(string shape, string symbol, DateOnly from, DateOnly to, string? url) =>
        shape.Replace("{base}", BaseUrl, StringComparison.Ordinal)
             .Replace("{symbol}", symbol, StringComparison.Ordinal)
             .Replace("{interval}", Interval, StringComparison.Ordinal)
             .Replace("{from}", Day(from), StringComparison.Ordinal)
             .Replace("{to}", Day(to), StringComparison.Ordinal)
             .Replace("{url}", url ?? "", StringComparison.Ordinal);
}

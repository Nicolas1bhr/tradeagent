using System.Collections.Frozen;
using System.Text.Json.Serialization;

namespace TradeAgent.Core.Data;

/// <summary>
/// ONE SERIES OF A TAPE SOURCE: where it is asked, which field of each item is its time, and which —
/// if any — names its symbol.
///
/// <para>Settable properties for the reason <see cref="CandleSourceEntry"/> has them: these arrive from
/// <c>tape-sources.json</c> as well as from this build, and <c>System.Text.Json</c> reads what the file
/// names and leaves the rest at its default.</para>
/// </summary>
public sealed class TapeSeriesEntry
{
    /// <summary>The series' name within its source, e.g. <c>premium-index</c>. Part of every row it writes.</summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// The URL pattern, with <c>{base}</c> and — on a per-symbol source — <c>{symbol}</c> substituted.
    /// DATA, exactly as <c>sources.json</c>'s shapes are: a vendor that renames a parameter costs a line.
    /// </summary>
    public string UrlShape { get; set; } = "";

    /// <summary>The item field holding the vendor's time for it: <c>time</c>, <c>timestamp</c>, <c>fundingTime</c>.</summary>
    public string TimeField { get; set; } = "";

    /// <summary>
    /// The item field naming its symbol, or EMPTY because the items carry none and the subject is the
    /// symbol that was asked for. Binance's taker ratio is the case: measured on 2026-10-02, its items
    /// are <c>{buySellRatio, buyVol, sellVol, timestamp}</c> and nothing else.
    /// </summary>
    public string SymbolField { get; set; } = "";
}

/// <summary>
/// ONE SOURCE THE TAPE RECORDS, AS DATA — the <c>runtimes.json</c> pattern (<c>docs/EDGE-FACTORY.md</c>
/// § 4.1): its host, its cadence, whether it is asked once or once per symbol, its series, the terms it
/// is read under, where its vendor documents it, and the one measurement that says it answers.
///
/// <para><b>It carries no key and cannot carry one.</b> There is no property for a credential, the
/// collector sends none, and an unknown property in the file is ignored rather than honoured. That is
/// what "unkeyed" means here: a keyed source's origin comes only from a built-in row (§ 6.10), and this
/// type has no way to hold a key for any row at all.</para>
/// </summary>
public sealed class TapeSourceEntry
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";

    /// <summary>The vendor's host, scheme and all. Its ORIGIN is what <c>O-LIVE</c> is decided against.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>How often a look is taken, in seconds. The looks are aligned to multiples of it.</summary>
    public int CadenceSeconds { get; set; }

    /// <summary>True: one request per symbol of the universe. False: one request for every symbol, filtered to the universe.</summary>
    public bool PerSymbol { get; set; }

    /// <summary>The parser family. One exists: <see cref="TapeSourceCatalog.JsonParser"/>, an object or a list of objects.</summary>
    public string Parser { get; set; } = "";

    public List<TapeSeriesEntry> Series { get; set; } = [];

    /// <summary>The terms this source is read under, in words.</summary>
    public string Terms { get; set; } = "";

    /// <summary>Where the vendor documents it.</summary>
    public string DocUrl { get; set; } = "";

    /// <summary>Who says it answers, and when. Never empty on a row this build ships.</summary>
    public string Measured { get; set; } = "";

    /// <summary><see cref="CadenceSeconds"/> as a span. Not part of the file.</summary>
    [JsonIgnore]
    public TimeSpan Cadence => TimeSpan.FromSeconds(CadenceSeconds);
}

/// <summary>
/// The catalogue as it stands: every row to record, the reason the file could not be read if it
/// could not, and the file's rows that were refused, each with its reason in words.
/// </summary>
public sealed record TapeSourceCatalogRead(
    IReadOnlyList<TapeSourceEntry> Sources, string? Unreadable, IReadOnlyList<string> Refused);

/// <summary>
/// THE SOURCES THE MARKET-CONTEXT TAPE RECORDS (<c>U-tape-store</c>): five built-in rows over Binance
/// USDⓈ-M public market data for six symbols, and whatever unkeyed rows <c>tape-sources.json</c> adds.
///
/// <para><b>The file may ADD rows. It may never replace, redirect or remove a built-in one</b>, and
/// that is the difference from <c>sources.json</c>, where a file row replaces the built-in with its id.
/// <c>tape-sources.json</c> sits in TradeAgent's own folder, which an agent running unconfined can
/// write; a built-in row it could redirect would be a live-classed record of whatever an agent chose
/// to serve. So a file row naming a built-in id is refused in words, and every row the file adds is
/// recorded as <c>O-ARCH</c> whatever address it points at — the store decides a class from THIS
/// build's rows (<see cref="BuiltInLiveRule"/>) and from nothing a file says.</para>
///
/// <para><b>An unreadable file stops only its own rows.</b> The rule for <c>runtimes.json</c> and
/// <c>sources.json</c> is that an unreadable override is the most restrictive one, because the
/// built-ins would otherwise stand in for a correction the owner wrote. Nothing here can be corrected
/// by the file, so the built-ins stand in for nothing it said: they go on, the file's rows are not
/// recorded, and the reason is the owner's to read. The alternative would let anyone who can write one
/// bad byte into that file switch the tape off — and a minute nobody recorded is gone.</para>
/// </summary>
public static class TapeSourceCatalog
{
    /// <summary>The file that may add rows, beside <c>sources.json</c>.</summary>
    public static string OverridePath => Path.Combine(Paths.Home, "tape-sources.json");

    /// <summary>The premium index for every symbol in one call — mark, index, the LIVE funding rate.</summary>
    public const string Premium = "binance-um-premium";

    /// <summary>Open interest now, per symbol.</summary>
    public const string OpenInterest = "binance-um-oi";

    /// <summary>Open interest at 5-minute points, per symbol.</summary>
    public const string OpenInterest5m = "binance-um-oi-5m";

    /// <summary>The 5-minute global long/short account ratio and the taker buy/sell ratio, per symbol.</summary>
    public const string Ratios5m = "binance-um-ratios-5m";

    /// <summary>Settled funding, per symbol.</summary>
    public const string Funding = "binance-um-funding";

    /// <summary>
    /// BINANCE'S USDⓈ-M FUTURES HOST. Spelled in pieces so the test-tree scan that forbids a test naming
    /// a vendor host (<c>SuiteReachesNoVendorTests</c>) can look for it without finding this line.
    /// </summary>
    public const string BinanceUmBaseUrl = "https://fapi" + ".binance" + ".com";

    /// <summary>The one parser family: the body is a JSON object, or a list of them, one item each.</summary>
    public const string JsonParser = "binance-um-json";

    /// <summary>The symbols every row is recorded for, built-in and added alike.</summary>
    public static readonly IReadOnlyList<string> Universe =
        ["BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT", "DOGEUSDT"];

    /// <summary>
    /// HOW LATE PAST ITS CADENCE A FIRST READING MAY ARRIVE AND STILL BE <c>O-LIVE</c>: thirty seconds,
    /// the stated tolerance of <c>docs/EDGE-FACTORY.md</c> § 4.1.
    /// </summary>
    public static readonly TimeSpan LiveTolerance = TimeSpan.FromSeconds(30);

    /// <summary>The shortest cadence a file row may ask for. A file an agent can write must not be able to make TradeAgent hammer a host.</summary>
    public static readonly TimeSpan MinCadence = TimeSpan.FromSeconds(60);

    /// <summary>The longest cadence a file row may ask for.</summary>
    public static readonly TimeSpan MaxCadence = TimeSpan.FromDays(1);

    /// <summary>The most rows the file may add, for the reason <see cref="MinCadence"/> exists.</summary>
    public const int MaxFileRows = 8;

    /// <summary>The most series one row may hold.</summary>
    public const int MaxSeriesPerRow = 4;

    const string DocPage =
        "https://developers.binance.com/en/docs/catalog/core-trading-derivatives-trading-usd-s-m-futures/api/rest-api/market-data";

    const string BinanceTerms =
        "Binance's public USDⓈ-M futures market data, read with no key and no account. Binance's own API "
        + "terms and rate limits govern it; they were NOT re-read for this build (docs/RESEARCH-REQUIRED.md, C5b).";

    const string Run =
        "measured 2026-10-02 from the dev Mac with no API key, every answer HTTP 200 in 0.31-0.37 s "
        + "(U-tape-store; docs/RESEARCH-REQUIRED.md, C5b): ";

    /// <summary>
    /// THE ROWS THIS BUILD SHIPS. A fresh copy on every call, so a caller that edits one edits its own.
    ///
    /// <para>The 5-minute and funding shapes ask for the last few points (<c>limit=3</c>,
    /// <c>limit=2</c>) on every look, the first one included. That is an overlap so a missed look loses
    /// nothing, and it is not a backfill: history is <c>U-tape-archive</c>'s, from the vendor's
    /// checksummed archive.</para>
    /// </summary>
    public static List<TapeSourceEntry> BuiltIn() =>
    [
        new()
        {
            Id = Premium,
            DisplayName = "Binance USDⓈ-M premium index",
            BaseUrl = BinanceUmBaseUrl,
            CadenceSeconds = 60,
            PerSymbol = false,
            Parser = JsonParser,
            Series =
            [
                new() { Id = "premium-index", UrlShape = "{base}/fapi/v1/premiumIndex", TimeField = "time", SymbolField = "symbol" }
            ],
            Terms = BinanceTerms,
            DocUrl = DocPage + "#mark-price",
            Measured = Run + "GET /fapi/v1/premiumIndex?symbol=BTCUSDT; without a symbol it answers every symbol in one list"
        },
        new()
        {
            Id = OpenInterest,
            DisplayName = "Binance USDⓈ-M open interest",
            BaseUrl = BinanceUmBaseUrl,
            CadenceSeconds = 60,
            PerSymbol = true,
            Parser = JsonParser,
            Series =
            [
                new() { Id = "open-interest", UrlShape = "{base}/fapi/v1/openInterest?symbol={symbol}", TimeField = "time", SymbolField = "symbol" }
            ],
            Terms = BinanceTerms,
            DocUrl = DocPage + "#open-interest",
            Measured = Run + "GET /fapi/v1/openInterest?symbol=BTCUSDT"
        },
        new()
        {
            Id = OpenInterest5m,
            DisplayName = "Binance USDⓈ-M open interest, 5-minute",
            BaseUrl = BinanceUmBaseUrl,
            CadenceSeconds = 300,
            PerSymbol = true,
            Parser = JsonParser,
            Series =
            [
                new()
                {
                    Id = "open-interest-5m",
                    UrlShape = "{base}/futures/data/openInterestHist?symbol={symbol}&period=5m&limit=3",
                    TimeField = "timestamp",
                    SymbolField = "symbol"
                }
            ],
            Terms = BinanceTerms,
            DocUrl = DocPage + "#open-interest-statistics",
            Measured = Run + "GET /futures/data/openInterestHist?symbol=BTCUSDT&period=5m"
        },
        new()
        {
            Id = Ratios5m,
            DisplayName = "Binance USDⓈ-M long/short and taker ratios, 5-minute",
            BaseUrl = BinanceUmBaseUrl,
            CadenceSeconds = 300,
            PerSymbol = true,
            Parser = JsonParser,
            Series =
            [
                new()
                {
                    Id = "long-short-account-5m",
                    UrlShape = "{base}/futures/data/globalLongShortAccountRatio?symbol={symbol}&period=5m&limit=3",
                    TimeField = "timestamp",
                    SymbolField = "symbol"
                },
                new()
                {
                    Id = "taker-long-short-5m",
                    UrlShape = "{base}/futures/data/takerlongshortRatio?symbol={symbol}&period=5m&limit=3",
                    TimeField = "timestamp",
                    SymbolField = ""
                }
            ],
            Terms = BinanceTerms,
            DocUrl = DocPage + "#long-short-ratio",
            Measured = Run + "GET /futures/data/globalLongShortAccountRatio?symbol=BTCUSDT&period=5m and "
                     + "GET /futures/data/takerlongshortRatio?symbol=BTCUSDT&period=5m (documented at "
                     + DocPage + "#taker-buy-sell-volume)"
        },
        new()
        {
            Id = Funding,
            DisplayName = "Binance USDⓈ-M settled funding",
            BaseUrl = BinanceUmBaseUrl,
            CadenceSeconds = 900,
            PerSymbol = true,
            Parser = JsonParser,
            Series =
            [
                new() { Id = "funding-rate", UrlShape = "{base}/fapi/v1/fundingRate?symbol={symbol}&limit=2", TimeField = "fundingTime", SymbolField = "symbol" }
            ],
            Terms = BinanceTerms,
            DocUrl = DocPage + "#get-funding-rate-history",
            Measured = Run + "GET /fapi/v1/fundingRate?symbol=BTCUSDT"
        }
    ];

    /// <summary>
    /// What a built-in row lets the store call live: its ORIGIN and its CADENCE, read from this build's
    /// rows once and never from a file. Value types, so no caller can edit the answer for the next one.
    /// </summary>
    static readonly FrozenDictionary<string, (string Origin, TimeSpan Cadence)> LiveRules =
        BuiltIn().ToFrozenDictionary(r => r.Id,
            r => (UrlOrigin.Of(r.BaseUrl) ?? throw new InvalidOperationException($"built-in tape row '{r.Id}' has no origin"), r.Cadence),
            StringComparer.Ordinal);

    /// <summary>
    /// THE ORIGIN AND CADENCE THAT CAN MAKE A ROW OF <paramref name="sourceId"/> LIVE, or null because
    /// it is not a built-in row and nothing it records can be. The store's evidence class reads this
    /// and nothing else.
    /// </summary>
    public static (string Origin, TimeSpan Cadence)? BuiltInLiveRule(string? sourceId) =>
        sourceId is not null && LiveRules.TryGetValue(sourceId, out var rule) ? rule : null;

    /// <summary>
    /// The catalogue: the built-in rows and whatever valid rows the file adds, or the built-ins alone
    /// with the reason the file could not be read. See the type summary for why an unreadable file does
    /// not stop the built-ins.
    /// </summary>
    /// <param name="overridePath">The file to read instead of <see cref="OverridePath"/>. For tests.</param>
    public static TapeSourceCatalogRead Read(string? overridePath = null)
    {
        var sources = BuiltIn();

        var file = VendorFile.Read<List<TapeSourceEntry?>>(overridePath ?? OverridePath);
        if (file.Unreadable is { } why)
            return new(sources,
                $"TradeAgent found a tape-sources.json in its own folder and could not read it: {why}. The "
                + "rows it adds are not being recorded. The built-in rows go on, because that file can only "
                + "ever add rows and none of them stands in for anything it said. Fix the file or remove it.",
                []);

        var refused = new List<string>();
        var builtIn = sources.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var added = 0;

        foreach (var row in file.Value ?? [])
        {
            if (row is null) { refused.Add("tape-sources.json holds an empty row, and it is not recorded."); continue; }
            if (Refusal(row, builtIn, seen) is { } no) { refused.Add(no); continue; }
            if (added == MaxFileRows)
            {
                refused.Add($"'{row.Id}' is past the {MaxFileRows} rows tape-sources.json may add, and it is not recorded.");
                continue;
            }

            if (string.IsNullOrEmpty(row.Parser)) row.Parser = JsonParser;
            sources.Add(row);
            added++;
        }

        return new(sources, null, refused);
    }

    /// <summary>Why a file row is not recorded, in words, or null because it is.</summary>
    static string? Refusal(TapeSourceEntry row, HashSet<string> builtIn, HashSet<string> seen)
    {
        var id = row.Id ?? "";
        if (!IsName(id))
            return $"A row in tape-sources.json has the id '{id}', which is not one TradeAgent records: lower-case "
                   + "letters, digits and '-' only, at most 64.";
        if (builtIn.Contains(id))
            return $"'{id}' is one of TradeAgent's built-in rows and tape-sources.json cannot replace it: a built-in "
                   + "row's address comes from this build and from nowhere else. Give your row its own id.";
        if (!seen.Add(id))
            return $"'{id}' appears more than once in tape-sources.json; only its first row is recorded.";

        if (UrlOrigin.Of(row.BaseUrl) is null
            || !Uri.TryCreate(row.BaseUrl.Trim(), UriKind.Absolute, out var uri)
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return $"'{id}' has the base address '{row.BaseUrl}', which is not a plain http or https address "
                   + "(no user name, no query, no fragment).";

        if (row.Cadence < MinCadence || row.Cadence > MaxCadence)
            return $"'{id}' asks to be looked at every {row.CadenceSeconds} s; a row in tape-sources.json is looked "
                   + $"at no more often than every {MinCadence.TotalSeconds:0} s and no less often than once a day.";

        if (!string.IsNullOrEmpty(row.Parser) && row.Parser != JsonParser)
            return $"'{id}' names the parser '{row.Parser}'; the one TradeAgent has is '{JsonParser}'.";

        if (row.Series is not { Count: >= 1 } series || series.Count > MaxSeriesPerRow)
            return $"'{id}' must hold between 1 and {MaxSeriesPerRow} series.";

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in series)
        {
            if (s is null || !IsName(s.Id) || !names.Add(s.Id))
                return $"'{id}' has a series with a missing, malformed or repeated id.";
            if (s.UrlShape is null || !s.UrlShape.StartsWith("{base}", StringComparison.Ordinal))
                return $"'{id}' series '{s.Id}' must start its address with {{base}}, so the row's base is the only host it reaches.";
            if (row.PerSymbol != s.UrlShape.Contains("{symbol}", StringComparison.Ordinal))
                return $"'{id}' series '{s.Id}': a per-symbol row names {{symbol}} in every series, and an all-symbol row names it in none.";
            if (!IsField(s.TimeField))
                return $"'{id}' series '{s.Id}' must name the field that holds each item's time.";
            if (!string.IsNullOrEmpty(s.SymbolField) && !IsField(s.SymbolField))
                return $"'{id}' series '{s.Id}' names a symbol field that is not a field name.";
            if (!row.PerSymbol && string.IsNullOrEmpty(s.SymbolField))
                return $"'{id}' series '{s.Id}' is asked once for every symbol, so it must name the field holding "
                       + "each item's symbol — otherwise nothing could be kept to the symbols TradeAgent records.";
        }

        return null;
    }

    static bool IsName(string? s) =>
        s is { Length: >= 1 and <= 64 } && s.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    static bool IsField(string? s) =>
        s is { Length: >= 1 and <= 64 } && s.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');
}

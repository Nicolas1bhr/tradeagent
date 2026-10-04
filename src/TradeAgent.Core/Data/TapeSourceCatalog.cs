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

    /// <summary>
    /// WHERE THE ANNOUNCEMENT PARSER FINDS ITS ITEMS (<see cref="TapeSourceCatalog.AnnouncementParser"/>):
    /// property names from the body's root, dotted, every list on the way flattened — OKX's
    /// <c>data.details</c> is "the <c>details</c> list of each element of <c>data</c>". EMPTY for the market
    /// parser, which reads the body itself as its items.
    /// </summary>
    public string ItemsPath { get; set; } = "";

    /// <summary>
    /// WHICH FIELD NAMES AN ANNOUNCEMENT — OKX's <c>url</c>. The item's subject is the first 32 hex
    /// characters of that text's SHA-256 (<see cref="TapeParse.ItemSubject"/>), because a URL cannot be a
    /// subject itself (<c>TapeStore.IsSubject</c>). EMPTY for the market parser.
    /// </summary>
    public string IdField { get; set; } = "";
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

    /// <summary>
    /// The parser family: <see cref="TapeSourceCatalog.JsonParser"/>, an object or a list of objects, or
    /// <see cref="TapeSourceCatalog.AnnouncementParser"/>, an exchange's list of announcements — which only
    /// a built-in row may name.
    /// </summary>
    public string Parser { get; set; } = "";

    public List<TapeSeriesEntry> Series { get; set; } = [];

    /// <summary>The terms this source is read under, in words — on a row whose terms were read, the clause read and the day.</summary>
    public string Terms { get; set; } = "";

    /// <summary>
    /// THE TERMS BASIS: the vendor's own page that <see cref="Terms"/> was read from, on the day it names —
    /// never inferred from an answer that came back without a key. EMPTY on a row whose terms were not
    /// re-read, and such a row's <see cref="Terms"/> says so.
    /// </summary>
    public string TermsUrl { get; set; } = "";

    /// <summary>Where the vendor documents it.</summary>
    public string DocUrl { get; set; } = "";

    /// <summary>Who says it answers, and when. Never empty on a row this build ships.</summary>
    public string Measured { get; set; } = "";

    /// <summary><see cref="CadenceSeconds"/> as a span. Not part of the file.</summary>
    [JsonIgnore]
    public TimeSpan Cadence => TimeSpan.FromSeconds(CadenceSeconds);

    /// <summary>
    /// HOW LATE THE VENDOR DOCUMENTS ITS ANSWER MAY BE AFTER THE SOURCE TIME: OKX writes that an
    /// announcement's <c>pTime</c> is when it was first published and that the answer may be delayed around
    /// five minutes. The live rule allows it on top of the cadence (<c>TapeStore.ClassOf</c>); a market row's
    /// is zero. NOT part of the file: only a built-in row's rule is ever read, and a row the file adds is
    /// archive whatever it claims.
    /// </summary>
    [JsonIgnore]
    public TimeSpan PublicationDelay { get; set; }
}

/// <summary>
/// The catalogue as it stands: every row to record, the reason the file could not be read if it
/// could not, and the file's rows that were refused, each with its reason in words.
/// </summary>
public sealed record TapeSourceCatalogRead(
    IReadOnlyList<TapeSourceEntry> Sources, string? Unreadable, IReadOnlyList<string> Refused);

/// <summary>
/// THE SOURCES THE MARKET-CONTEXT TAPE RECORDS: two built-in families — five rows over Binance USDⓈ-M public
/// market data for six symbols (<c>U-tape-store</c>) and OKX's announcements for EU users
/// (<c>U-tape-events</c>) — and whatever unkeyed market rows <c>tape-sources.json</c> adds.
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

    /// <summary>OKX's announcements for EU users, its first page once a minute (<c>U-tape-events</c>).</summary>
    public const string OkxEeaAnnouncements = "okx-eea-announcements";

    /// <summary>
    /// OKX'S DOMAIN FOR EU USERS, spelled in pieces for the reason <see cref="BinanceUmBaseUrl"/> is. Its
    /// answer is restricted by the asking IP, and this is the domain OKX names for the EEA.
    /// </summary>
    public const string OkxEeaBaseUrl = "https://eea" + ".okx" + ".com";

    /// <summary>
    /// THE TERMS THE OKX ROW IS READ UNDER: OKX's API Agreement as published for the EEA, which its EEA
    /// Terms of Service (§ 1.14) name as the terms of the API Services, public endpoints included.
    /// </summary>
    public const string OkxTermsUrl = "https://www" + ".okx" + ".com/en-eu/help/okx-api-agreement";

    /// <summary>
    /// BINANCE'S USDⓈ-M FUTURES HOST. Spelled in pieces so the test-tree scan that forbids a test naming
    /// a vendor host (<c>SuiteReachesNoVendorTests</c>) can look for it without finding this line.
    /// </summary>
    public const string BinanceUmBaseUrl = "https://fapi" + ".binance" + ".com";

    /// <summary>The market parser family: the body is a JSON object, or a list of them, one item each.</summary>
    public const string JsonParser = "binance-um-json";

    /// <summary>
    /// THE ANNOUNCEMENT PARSER FAMILY (<c>U-tape-events</c>): the list at the series'
    /// <see cref="TapeSeriesEntry.ItemsPath"/>, one item each, its subject the digest of its
    /// <see cref="TapeSeriesEntry.IdField"/> and its time the vendor's own (<see cref="TapeParse.TryReadItems"/>).
    /// Built-in rows only.
    /// </summary>
    public const string AnnouncementParser = "announcement-json";

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

    const string OkxTerms =
        "OKX's API Agreement for the EEA (last updated 28 July 2026), re-read 2026-10-04 (U-tape-events): § 3.2(a) lets "
        + "its public endpoints be used without a key, under the Agreement and its § 9; § 9.4 keeps what they serve to the "
        + "user's own personal, non-commercial trading, never redistributed, and allows automated means at a rate "
        + "'reasonably necessary for your personal trading use' that puts no unreasonable load on OKX, binding callers "
        + "without an OKX account the same way; § 9.3(b) asks OKX's written authorisation before the API Services are "
        + "offered as part of a commercial product. TradeAgent asks for one page a minute, against a documented limit of "
        + "five requests every two seconds, keeps what it reads on the owner's own machine for his own trading, and passes "
        + "it to no one. Whether selling TradeAgent itself needs that authorisation is a legal question this row raises "
        + "and does not settle (docs/RESEARCH-REQUIRED.md, C5c).";

    /// <summary>
    /// THE ANNOUNCEMENT ROWS THIS BUILD SHIPS (<c>U-tape-events</c>), a fresh copy on every call: OKX's
    /// announcements for EU users, the first page once a minute — the twenty newest by first publication,
    /// about a month of them. Page 1 only, by design: an announcement is first seen there, and one that has
    /// moved past it is no longer being watched.
    ///
    /// <para><b>A source is here only with a terms basis re-read on the day.</b> Bybit's announcements were
    /// measured with OKX's and are NOT here: Bybit EU's General Terms (12 June 2026, § 9.2.2) forbid bots,
    /// scripts and other automatic means to access or monitor any part of its platform, so the row was dropped
    /// rather than pointed at Bybit's non-EU host (<c>docs/RESEARCH-REQUIRED.md</c>, C5c).</para>
    /// </summary>
    public static List<TapeSourceEntry> Announcements() =>
    [
        new()
        {
            Id = OkxEeaAnnouncements,
            DisplayName = "OKX announcements (EEA)",
            BaseUrl = OkxEeaBaseUrl,
            CadenceSeconds = 60,
            PerSymbol = false,
            Parser = AnnouncementParser,
            Series =
            [
                new()
                {
                    Id = "announcements",
                    UrlShape = "{base}/api/v5/support/announcements",
                    ItemsPath = "data.details",
                    IdField = "url",
                    TimeField = "pTime"
                }
            ],
            PublicationDelay = TimeSpan.FromSeconds(300),
            Terms = OkxTerms,
            TermsUrl = OkxTermsUrl,
            DocUrl = "https://www" + ".okx" + ".com/docs-v5/en/#announcement-get-announcements",
            Measured = "measured 2026-10-04 from the dev Mac with no API key (U-tape-events; docs/RESEARCH-REQUIRED.md, C5c): "
                     + "GET /api/v5/support/announcements, pages 1 to 15, every answer HTTP 200 in 0.18-1.80 s and about 5 KB; "
                     + "twenty items a page, newest pTime first, each {annType, title, url, pTime, businessPTime} in 175-343 "
                     + "bytes, page 1 reaching back 32 days"
        }
    ];

    /// <summary>
    /// EVERY ROW THIS BUILD SHIPS, family by family — the market rows (<see cref="BuiltIn"/>) and the
    /// announcement rows (<see cref="Announcements"/>) — a fresh copy on every call. The live rule, the ids a
    /// file may not reuse and <see cref="Read"/> all take this list, so another family joins by being added
    /// here and nowhere else.
    /// </summary>
    public static List<TapeSourceEntry> Shipped() => [.. BuiltIn(), .. Announcements()];

    /// <summary>
    /// What a built-in row lets the store call live: its ORIGIN, its CADENCE and its documented
    /// PUBLICATION DELAY, read from this build's rows once and never from a file. Value types, so no caller
    /// can edit the answer for the next one.
    /// </summary>
    static readonly FrozenDictionary<string, (string Origin, TimeSpan Cadence, TimeSpan Delay)> LiveRules =
        Shipped().ToFrozenDictionary(r => r.Id,
            r => (UrlOrigin.Of(r.BaseUrl) ?? throw new InvalidOperationException($"built-in tape row '{r.Id}' has no origin"),
                  r.Cadence, r.PublicationDelay),
            StringComparer.Ordinal);

    /// <summary>
    /// THE ORIGIN, CADENCE AND DOCUMENTED DELAY THAT CAN MAKE A ROW OF <paramref name="sourceId"/> LIVE, or
    /// null because it is not a built-in row and nothing it records can be. The store's evidence class reads
    /// this and nothing else.
    /// </summary>
    public static (string Origin, TimeSpan Cadence, TimeSpan Delay)? BuiltInLiveRule(string? sourceId) =>
        sourceId is not null && LiveRules.TryGetValue(sourceId, out var rule) ? rule : null;

    /// <summary>
    /// The catalogue: every shipped row and whatever valid rows the file adds, or the shipped rows alone
    /// with the reason the file could not be read. See the type summary for why an unreadable file does
    /// not stop the built-ins.
    /// </summary>
    /// <param name="overridePath">The file to read instead of <see cref="OverridePath"/>. For tests.</param>
    public static TapeSourceCatalogRead Read(string? overridePath = null)
    {
        var sources = Shipped();

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

        // THE ANNOUNCEMENT PARSER IS BUILT-IN ONLY. It keeps an exchange's text as published, and this file
        // is one an agent running on this computer can write: a row here that named it would let whatever
        // address the row points at put text into the tape, beside the exchanges' own, for agents to read.
        if (string.Equals(row.Parser, AnnouncementParser, StringComparison.Ordinal))
            return $"'{id}' names the parser '{AnnouncementParser}', which only TradeAgent's built-in announcement rows use: "
                   + "an announcement is an exchange's own text, and a row in tape-sources.json must not be able to put text "
                   + $"into the tape from wherever it points. A row there may add market data read with '{JsonParser}'.";

        if (!string.IsNullOrEmpty(row.Parser) && row.Parser != JsonParser)
            return $"'{id}' names the parser '{row.Parser}'; a row in tape-sources.json may name only '{JsonParser}'.";

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
            if (!string.IsNullOrEmpty(s.ItemsPath) || !string.IsNullOrEmpty(s.IdField))
                return $"'{id}' series '{s.Id}' names an items path or an id field, which only the announcement parser reads, "
                       + "and a row in tape-sources.json cannot name that parser.";
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

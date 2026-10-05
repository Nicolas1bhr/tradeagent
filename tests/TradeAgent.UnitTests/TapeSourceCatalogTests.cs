using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Tests;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE TAPE'S SOURCES AS DATA (<c>U-tape-store</c> item 3): five built-in rows over six symbols, and a
/// <c>tape-sources.json</c> that can add unkeyed rows and can never replace, redirect or remove a
/// built-in one. Every file here is the test's own, passed by path — no test writes the shared home's.
/// </summary>
public class TapeSourceCatalogTests(ITestOutputHelper log)
{
    static string NewFile(string json)
    {
        var path = Path.Combine(TestEnv.Home, $"tape-sources-{Guid.NewGuid():n}.json");
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>
    /// THE ROWS AS SHIPPED: the premium index once a minute for every symbol in one call, open interest
    /// once a minute per symbol, the two 5-minute rows, and settled funding every fifteen minutes — over
    /// exactly the six symbols, from Binance's USDⓈ-M host, each with its terms, its documentation and
    /// the measurement that says it answers.
    /// </summary>
    [Fact]
    public void The_built_in_rows_are_five_sources_over_six_symbols_at_their_cadences()
    {
        var rows = TapeSourceCatalog.BuiltIn();
        foreach (var r in rows)
            log.WriteLine($"{r.Id} {r.CadenceSeconds}s perSymbol={r.PerSymbol} series={string.Join(",", r.Series.Select(s => s.Id))}");

        Assert.Equal(
            [TapeSourceCatalog.Premium, TapeSourceCatalog.OpenInterest, TapeSourceCatalog.OpenInterest5m,
             TapeSourceCatalog.Ratios5m, TapeSourceCatalog.Funding],
            rows.Select(r => r.Id));
        Assert.Equal(["binance-um-premium", "binance-um-oi", "binance-um-oi-5m", "binance-um-ratios-5m", "binance-um-funding"],
            rows.Select(r => r.Id));
        Assert.Equal([60, 60, 300, 300, 900], rows.Select(r => r.CadenceSeconds));
        Assert.Equal([false, true, true, true, true], rows.Select(r => r.PerSymbol));
        Assert.Equal(["BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT", "DOGEUSDT"], TapeSourceCatalog.Universe);
        Assert.Equal(TimeSpan.FromSeconds(30), TapeSourceCatalog.LiveTolerance);

        // THE RATIOS ROW IS TWO SERIES, and the taker one's items carry no symbol: its subject is the
        // symbol asked for (measured 2026-10-02 — see docs/RESEARCH-REQUIRED.md, C5b).
        var ratios = rows.Single(r => r.Id == TapeSourceCatalog.Ratios5m);
        Assert.Equal(["long-short-account-5m", "taker-long-short-5m"], ratios.Series.Select(s => s.Id));
        Assert.Equal("", ratios.Series[1].SymbolField);

        // THE PREMIUM ROW IS ASKED ONCE FOR EVERY SYMBOL, so it must name the symbol field it filters on.
        var premium = rows.Single(r => r.Id == TapeSourceCatalog.Premium);
        Assert.DoesNotContain("{symbol}", premium.Series[0].UrlShape, StringComparison.Ordinal);
        Assert.Equal("symbol", premium.Series[0].SymbolField);

        Assert.All(rows, r =>
        {
            Assert.Equal(TapeSourceCatalog.BinanceUmBaseUrl, r.BaseUrl);
            Assert.StartsWith("https://", r.BaseUrl, StringComparison.Ordinal);
            Assert.Equal(TapeSourceCatalog.JsonParser, r.Parser);
            Assert.StartsWith("https://developers.binance.com/", r.DocUrl, StringComparison.Ordinal);
            Assert.Contains("no key", r.Terms, StringComparison.Ordinal);
            Assert.Contains("NOT re-read", r.Terms, StringComparison.Ordinal);
            Assert.Contains("measured 2026-10-02", r.Measured, StringComparison.Ordinal);
            Assert.All(r.Series, s =>
            {
                Assert.StartsWith("{base}/", s.UrlShape, StringComparison.Ordinal);
                Assert.Equal(r.PerSymbol, s.UrlShape.Contains("{symbol}", StringComparison.Ordinal));
                Assert.NotEqual("", s.TimeField);
            });

            // AND THE LIVE RULE IS READ OFF THESE ROWS: their origin and their cadence.
            var rule = TapeSourceCatalog.BuiltInLiveRule(r.Id);
            Assert.NotNull(rule);
            Assert.Equal(UrlOrigin.Of(TapeSourceCatalog.BinanceUmBaseUrl), rule!.Value.Origin);
            Assert.Equal(r.Cadence, rule.Value.Cadence);
        });

        // A FRESH COPY EVERY CALL: a caller that edits a row edits its own, never the next caller's,
        // and never the rule the store decides a class by.
        rows[0].BaseUrl = "http://127.0.0.1:9";
        Assert.Equal(TapeSourceCatalog.BinanceUmBaseUrl, TapeSourceCatalog.BuiltIn()[0].BaseUrl);
        Assert.Equal(UrlOrigin.Of(TapeSourceCatalog.BinanceUmBaseUrl),
            TapeSourceCatalog.BuiltInLiveRule(TapeSourceCatalog.Premium)!.Value.Origin);
        Assert.Null(TapeSourceCatalog.BuiltInLiveRule("not-a-row"));
        Assert.Null(TapeSourceCatalog.BuiltInLiveRule(null));
    }

    /// <summary>
    /// THE FILE ADDS, AND ONLY ADDS. A row of its own is recorded; a row reusing a built-in id is refused
    /// in words and the built-in stands as shipped; a row with credentials in its address, a cadence that
    /// would hammer a host, or a repeated id is refused one by one. An unreadable file stops only its own
    /// rows — the built-ins go on — and says why.
    /// </summary>
    [Fact]
    public void A_tape_sources_file_adds_unkeyed_rows_and_never_replaces_a_built_in_one()
    {
        var path = NewFile("""
            [
              { "id": "my-top-ratio", "display_name": "Top trader ratio", "base_url": "http://127.0.0.1:9",
                "cadence_seconds": 300, "per_symbol": true, "credential": "ignored-because-no-row-can-hold-one",
                "series": [ { "id": "top-5m", "url_shape": "{base}/futures/data/topLongShortAccountRatio?symbol={symbol}&period=5m",
                              "time_field": "timestamp", "symbol_field": "symbol" } ] },
              { "id": "binance-um-premium", "base_url": "http://127.0.0.1:9", "cadence_seconds": 60,
                "series": [ { "id": "premium-index", "url_shape": "{base}/fapi/v1/premiumIndex", "time_field": "time", "symbol_field": "symbol" } ] },
              { "id": "with-user", "base_url": "https://someone@127.0.0.1:9", "cadence_seconds": 300, "per_symbol": true,
                "series": [ { "id": "s", "url_shape": "{base}/x?symbol={symbol}", "time_field": "t" } ] },
              { "id": "too-often", "base_url": "http://127.0.0.1:9", "cadence_seconds": 5, "per_symbol": true,
                "series": [ { "id": "s", "url_shape": "{base}/x?symbol={symbol}", "time_field": "t" } ] },
              { "id": "my-top-ratio", "base_url": "http://127.0.0.1:9", "cadence_seconds": 300, "per_symbol": true,
                "series": [ { "id": "s", "url_shape": "{base}/x?symbol={symbol}", "time_field": "t" } ] }
            ]
            """);

        var read = TapeSourceCatalog.Read(path);
        foreach (var why in read.Refused) log.WriteLine("refused: " + why);

        Assert.Null(read.Unreadable);
        Assert.Equal(TapeSourceCatalog.Shipped().Select(r => r.Id).Append("my-top-ratio"), read.Sources.Select(r => r.Id));

        // THE BUILT-IN PREMIUM ROW IS THE ONE SHIPPED, not the file's redirect of it.
        var premium = Assert.Single(read.Sources, r => r.Id == TapeSourceCatalog.Premium);
        Assert.Equal(TapeSourceCatalog.BinanceUmBaseUrl, premium.BaseUrl);

        // THE ADDED ROW IS RECORDED, with the one parser family, and nothing it says can make it live.
        var added = read.Sources[^1];
        Assert.Equal(TapeSourceCatalog.JsonParser, added.Parser);
        Assert.Null(TapeSourceCatalog.BuiltInLiveRule(added.Id));

        Assert.Equal(4, read.Refused.Count);
        Assert.Contains(read.Refused, r => r.Contains("'binance-um-premium' is one of TradeAgent's built-in rows", StringComparison.Ordinal));
        Assert.Contains(read.Refused, r => r.Contains("'with-user'", StringComparison.Ordinal) && r.Contains("no user name", StringComparison.Ordinal));
        Assert.Contains(read.Refused, r => r.Contains("'too-often' asks to be looked at every 5 s", StringComparison.Ordinal));
        Assert.Contains(read.Refused, r => r.Contains("'my-top-ratio' appears more than once", StringComparison.Ordinal));

        // AN UNREADABLE FILE STOPS ONLY ITS OWN ROWS.
        var broken = TapeSourceCatalog.Read(NewFile("[ { \"id\": \"half-writ"));
        log.WriteLine("unreadable: " + broken.Unreadable);
        Assert.Equal(TapeSourceCatalog.Shipped().Select(r => r.Id), broken.Sources.Select(r => r.Id));
        Assert.Contains("could not read it", broken.Unreadable!, StringComparison.Ordinal);
        Assert.Contains("built-in rows go on", broken.Unreadable!, StringComparison.Ordinal);
        Assert.Empty(broken.Refused);

        // AN ABSENT FILE IS THE SHIPPED ROWS — the five market rows and the announcement row — and says nothing.
        var absent = TapeSourceCatalog.Read(Path.Combine(TestEnv.Home, $"absent-{Guid.NewGuid():n}.json"));
        Assert.Equal(5 + 1, absent.Sources.Count);
        Assert.Equal(TapeSourceCatalog.Shipped().Select(r => r.Id), absent.Sources.Select(r => r.Id));
        Assert.Null(absent.Unreadable);
        Assert.Empty(absent.Refused);

        // AND THE FILE IS CAPPED, so a file an agent can write cannot multiply the requests TradeAgent makes.
        var many = string.Join(",", Enumerable.Range(1, TapeSourceCatalog.MaxFileRows + 2).Select(i => $$"""
            { "id": "row-{{i}}", "base_url": "http://127.0.0.1:9", "cadence_seconds": 300, "per_symbol": true,
              "series": [ { "id": "s", "url_shape": "{base}/x?symbol={symbol}", "time_field": "t" } ] }
            """));
        var capped = TapeSourceCatalog.Read(NewFile("[" + many + "]"));
        Assert.Equal(5 + 1 + TapeSourceCatalog.MaxFileRows, capped.Sources.Count);
        Assert.Equal(2, capped.Refused.Count);
        Assert.All(capped.Refused, r => Assert.Contains("is past the 8 rows", r, StringComparison.Ordinal));
    }

    /// <summary>
    /// THE ANNOUNCEMENT FAMILY AS SHIPPED (<c>U-tape-events</c>): one row, OKX's announcements for EU users —
    /// its first page once a minute, read by the announcement parser, with OKX's documented five-minute
    /// delay, its terms basis (the page and the clauses read on the day) and the measurement that says it
    /// answers. <see cref="TapeSourceCatalog.BuiltIn"/> stays the five market rows; the live rule, the ids a
    /// file may not reuse and <see cref="TapeSourceCatalog.Read"/> take both families.
    /// </summary>
    [Fact]
    public void The_announcement_row_is_okx_eea_with_its_terms_basis_and_its_documented_delay()
    {
        var row = Assert.Single(TapeSourceCatalog.Announcements());
        log.WriteLine($"{row.Id} {row.CadenceSeconds}s delay={row.PublicationDelay.TotalSeconds}s parser={row.Parser}");
        log.WriteLine("terms: " + row.Terms);

        Assert.Equal((TapeSourceCatalog.OkxEeaAnnouncements, "okx-eea-announcements"), (row.Id, row.Id));
        Assert.Equal(TapeSourceCatalog.OkxEeaBaseUrl, row.BaseUrl);
        Assert.StartsWith("https://", row.BaseUrl, StringComparison.Ordinal);
        Assert.Equal((60, false, TapeSourceCatalog.AnnouncementParser), (row.CadenceSeconds, row.PerSymbol, row.Parser));
        Assert.Equal(TimeSpan.FromSeconds(300), row.PublicationDelay);

        var series = Assert.Single(row.Series);
        Assert.Equal(("announcements", "{base}/api/v5/support/announcements", "data.details", "url", "pTime", ""),
            (series.Id, series.UrlShape, series.ItemsPath, series.IdField, series.TimeField, series.SymbolField));

        // THE TERMS BASIS: the vendor's own page, and the clauses read on the day — never a keyless 200.
        Assert.Equal(TapeSourceCatalog.OkxTermsUrl, row.TermsUrl);
        Assert.StartsWith("https://", row.TermsUrl, StringComparison.Ordinal);
        Assert.EndsWith("/en-eu/help/okx-api-agreement", row.TermsUrl, StringComparison.Ordinal);
        Assert.Contains("re-read 2026-10-06", row.Terms, StringComparison.Ordinal);
        Assert.Contains("§ 9.4", row.Terms, StringComparison.Ordinal);
        Assert.Contains("personal trading use", row.Terms, StringComparison.Ordinal);
        Assert.Contains("§ 9.3(b)", row.Terms, StringComparison.Ordinal);
        Assert.Contains("one page a minute", row.Terms, StringComparison.Ordinal);
        Assert.Contains("/docs-v5/en/#announcement-get-announcements", row.DocUrl, StringComparison.Ordinal);
        Assert.Contains("measured 2026-10-06", row.Measured, StringComparison.Ordinal);

        // THE LIVE RULE TAKES THE ROW'S ORIGIN, CADENCE AND DOCUMENTED DELAY; a market row's delay is zero.
        Assert.Equal((UrlOrigin.Of(TapeSourceCatalog.OkxEeaBaseUrl)!, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(300)),
            TapeSourceCatalog.BuiltInLiveRule(TapeSourceCatalog.OkxEeaAnnouncements));
        Assert.All(TapeSourceCatalog.BuiltIn(), r => Assert.Equal(TimeSpan.Zero, TapeSourceCatalog.BuiltInLiveRule(r.Id)!.Value.Delay));

        // BUILT-IN STAYS THE FIVE MARKET ROWS; THE SHIPPED LIST IS BOTH FAMILIES, IN THAT ORDER.
        Assert.Equal(5, TapeSourceCatalog.BuiltIn().Count);
        Assert.DoesNotContain(TapeSourceCatalog.BuiltIn(), r => r.Id == TapeSourceCatalog.OkxEeaAnnouncements);
        Assert.Equal(TapeSourceCatalog.BuiltIn().Select(r => r.Id).Append(TapeSourceCatalog.OkxEeaAnnouncements),
            TapeSourceCatalog.Shipped().Select(r => r.Id));

        // A FRESH COPY EVERY CALL, and an edited copy moves nothing the store decides by.
        row.BaseUrl = "http://127.0.0.1:9";
        row.PublicationDelay = TimeSpan.FromDays(1);
        Assert.Equal(TapeSourceCatalog.OkxEeaBaseUrl, TapeSourceCatalog.Announcements()[0].BaseUrl);
        Assert.Equal(TimeSpan.FromSeconds(300), TapeSourceCatalog.BuiltInLiveRule(TapeSourceCatalog.OkxEeaAnnouncements)!.Value.Delay);
    }

    /// <summary>
    /// A FILE ROW NAMING THE ANNOUNCEMENT PARSER IS REFUSED, in words that say why: an announcement is an
    /// exchange's own text, and a file an agent can write must not be able to put text into the tape from
    /// wherever it points. So is a file row reusing the announcement row's id, and one whose series names
    /// an items path or an id field, which only that parser reads. A delay the file names is not read at all.
    /// </summary>
    [Fact]
    public void A_file_row_naming_the_announcement_parser_is_refused()
    {
        var path = NewFile("""
            [
              { "id": "my-announcements", "base_url": "http://127.0.0.1:9", "cadence_seconds": 60, "parser": "announcement-json",
                "series": [ { "id": "news", "url_shape": "{base}/news", "time_field": "pTime", "items_path": "data.details", "id_field": "url" } ] },
              { "id": "okx-eea-announcements", "base_url": "http://127.0.0.1:9", "cadence_seconds": 60,
                "series": [ { "id": "announcements", "url_shape": "{base}/api/v5/support/announcements", "time_field": "pTime", "symbol_field": "x" } ] },
              { "id": "my-items-path", "base_url": "http://127.0.0.1:9", "cadence_seconds": 60,
                "series": [ { "id": "s", "url_shape": "{base}/x", "time_field": "t", "symbol_field": "symbol", "items_path": "data.list" } ] },
              { "id": "my-late-ratio", "base_url": "http://127.0.0.1:9", "cadence_seconds": 300, "per_symbol": true, "publication_delay": "01:00:00",
                "series": [ { "id": "s", "url_shape": "{base}/x?symbol={symbol}", "time_field": "timestamp", "symbol_field": "symbol" } ] }
            ]
            """);

        var read = TapeSourceCatalog.Read(path);
        foreach (var why in read.Refused) log.WriteLine("refused: " + why);

        Assert.Null(read.Unreadable);
        Assert.Equal(3, read.Refused.Count);
        Assert.Contains(read.Refused, r => r.StartsWith("'my-announcements' names the parser 'announcement-json', which only "
                                                        + "TradeAgent's built-in announcement rows use", StringComparison.Ordinal)
                                           && r.Contains("must not be able to put text into the tape", StringComparison.Ordinal));
        Assert.Contains(read.Refused, r => r.Contains("'okx-eea-announcements' is one of TradeAgent's built-in rows", StringComparison.Ordinal));
        Assert.Contains(read.Refused, r => r.Contains("'my-items-path' series 's' names an items path or an id field", StringComparison.Ordinal));

        // THE ANNOUNCEMENT ROW STANDS AS SHIPPED, and the one row added is a market row with no delay to claim.
        Assert.Equal(TapeSourceCatalog.Shipped().Select(r => r.Id).Append("my-late-ratio"), read.Sources.Select(r => r.Id));
        Assert.Equal(TapeSourceCatalog.OkxEeaBaseUrl, read.Sources.Single(r => r.Id == TapeSourceCatalog.OkxEeaAnnouncements).BaseUrl);
        var added = read.Sources[^1];
        Assert.Equal((TapeSourceCatalog.JsonParser, TimeSpan.Zero), (added.Parser, added.PublicationDelay));
        Assert.Null(TapeSourceCatalog.BuiltInLiveRule(added.Id));
    }
}

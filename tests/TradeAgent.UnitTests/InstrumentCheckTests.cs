using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Provisioning;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE APP CHECKS AN INSTRUMENT AGAINST THE VENUE'S OWN PUBLISHED DEFINITION — `U-venue-verify`.
///
/// <para>Before this unit the only route to a VERIFIED instrument was a hand-edited <c>venues.json</c>
/// that no screen writes, and the shipped BTCUSDT row is unverified: the paper connector offered nothing,
/// the runner sized nothing and the cost model refused the campaign press. After it the app reads the
/// venue's definition from the BUILT-IN origin, records every attempt in <c>instrument_check</c>, and
/// serves the venue's numbers for seven days to every reader of the catalogue.</para>
///
/// <para>Every test here talks to a loopback <see cref="FakeArchive"/> serving canned exchangeInfo JSON;
/// none reaches a vendor, which <see cref="SuiteReachesNoVendorTests"/> holds the whole tree to.</para>
/// </summary>
public class InstrumentCheckTests(ITestOutputHelper log)
{
    /// <summary>The rung. Renumbered with the rung if <c>main</c> gains this number first: the ladder stays contiguous.</summary>
    const int CheckRung = 29;

    static readonly DateTimeOffset T0 = new(2026, 10, 3, 4, 48, 54, TimeSpan.Zero);

    static InstrumentCheckAttempt Attempt(string outcome, string url = "https://127.0.0.1:1/api/v3/exchangeInfo?symbol=BTCUSDT") => new()
    {
        VenueId = VenueCatalog.BinanceSpot,
        Symbol = "BTCUSDT",
        Url = url,
        RequestedAt = T0,
        ReceivedAt = T0.AddMilliseconds(300),
        Outcome = outcome
    };

    // ---- the rung ------------------------------------------------------------------------------------

    /// <summary>
    /// THE CHECK LEDGER ARRIVES AT ITS RUNG, EMPTY. A FLOOR and not the build's number, for the reason
    /// <c>BacktestLedgerTests</c> gives; the stamp on disk is still required to equal what this build
    /// writes, so an upgrade that did not run is caught. The roll-back lists in <c>VenueCatalogTests</c>,
    /// <c>PaperEligibleVerdictTests</c> and <c>OrgLedgerTests</c> drop the table, so each still reopens a
    /// database that predates it.
    /// </summary>
    [Fact]
    public void The_instrument_check_arrives_at_its_rung_empty()
    {
        using var db = TestEnv.NewDb();

        Assert.True(Versions.DatabaseSchemaVersion >= CheckRung,
            $"the instrument check needs schema {CheckRung} or later; this build says "
            + Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture), Scalar(db,
            "SELECT value FROM meta WHERE key='schema_version'"));
        Assert.Equal("1", Scalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='instrument_check'"));
        Assert.Equal("0", Scalar(db, "SELECT COUNT(*) FROM instrument_check"));
    }

    /// <summary>
    /// A ROW IS WHAT HAPPENED, AND THE STORE REFUSES WHAT CANNOT HAVE. A verified attempt carries a tick
    /// size and a step above zero — a step of nothing would size every position to nothing — and an
    /// attempt of any other outcome carries no numbers at all; an outcome that is not one of the three
    /// words is refused. The ORIGIN is the store's reading of the URL, never the caller's.
    /// </summary>
    [Fact]
    public void A_check_row_is_what_happened_and_the_store_refuses_what_cannot_have()
    {
        using var db = TestEnv.NewDb();
        var store = new InstrumentCheckStore(db);

        Assert.Throws<ArgumentException>(() => store.Append(Attempt(InstrumentCheckOutcome.Verified)));
        Assert.Throws<ArgumentException>(() => store.Append(Attempt(InstrumentCheckOutcome.Verified) with
        {
            TickSize = 0.01m, QuantityIncrement = 0m
        }));
        Assert.Throws<ArgumentException>(() => store.Append(Attempt(InstrumentCheckOutcome.Failed) with
        {
            QuantityIncrement = 0.00001m
        }));
        Assert.Throws<ArgumentException>(() => store.Append(Attempt("trusted")));
        Assert.Equal("0", Scalar(db, "SELECT COUNT(*) FROM instrument_check"));

        var failed = store.Append(Attempt(InstrumentCheckOutcome.Failed, "https://127.0.0.1:443/x?symbol=BTCUSDT") with
        {
            HttpStatus = 503, Note = "the host answered 503 and no definition was read"
        });
        var verified = store.Append(Attempt(InstrumentCheckOutcome.Verified, "http://127.0.0.1:8080/x?symbol=BTCUSDT") with
        {
            HttpStatus = 200, BodySha256 = new string('a', 64),
            TickSize = 0.01m, QuantityIncrement = 0.00001m, MinQuantity = 0.00001m, MinNotional = 5m
        });

        Assert.Equal("https://127.0.0.1", failed.Origin);
        Assert.Equal("http://127.0.0.1:8080", verified.Origin);
        Assert.Equal(verified, store.Latest(VenueCatalog.BinanceSpot, "BTCUSDT"));
        Assert.Equal(verified, Assert.Single(store.LatestVerified()));
        Assert.Equal(0.00001m, verified.QuantityIncrement);
        Assert.Equal(5m, verified.MinNotional);
        Assert.Equal(failed, store.ById(failed.Id));
    }

    // ---- the verifier ----------------------------------------------------------------------------------

    /// <summary>
    /// THE DEFINITION IS READ STRICTLY. Today's shape (measured 2026-10-03) reads to the four numbers with
    /// their trailing zeros gone — and the trap in it is <c>MARKET_LOT_SIZE</c>, whose <c>stepSize</c> is
    /// <c>0</c>: a reader that took the first step it found would serve a step of nothing. The older
    /// <c>MIN_NOTIONAL</c> filter is read where <c>NOTIONAL</c> is absent. Everything that is not one
    /// definition of the instrument is refused in words.
    /// </summary>
    [Fact]
    public void The_definition_is_read_strictly()
    {
        Assert.True(InstrumentDefinition.TryParse(ExchangeInfo(), "BTCUSDT", out var today, out var why), why);
        Assert.Equal(new InstrumentDefinition(0.01m, 0.00001m, 0.00001m, 5m), today);
        Assert.Equal("0.01", today!.TickSize.ToString(CultureInfo.InvariantCulture));
        Assert.Equal("0.00001", today.QuantityIncrement.ToString(CultureInfo.InvariantCulture));

        Assert.True(InstrumentDefinition.TryParse(ExchangeInfo(notionalFilter: "MIN_NOTIONAL", minNotional: "10.00000000"),
            "BTCUSDT", out var legacy, out why), why);
        Assert.Equal(10m, legacy!.MinNotional);

        foreach (var (body, word) in new (string?, string)[]
        {
            (null, "empty"),
            ("<html>maintenance</html>", "not JSON"),
            ("""{"code":-1121,"msg":"Invalid symbol."}""", "no list of symbols"),
            (ExchangeInfo(symbol: "ETHUSDT"), "does not list BTCUSDT"),
            (ExchangeInfo().Replace("\"symbols\":[", "\"symbols\":[" + Entry("BTCUSDT") + ",", StringComparison.Ordinal), "2 times"),
            (ExchangeInfo().Replace("{\"filterType\":\"ICEBERG_PARTS\"", "{\"filterType\":\"LOT_SIZE\",\"stepSize\":\"1\"},{\"filterType\":\"ICEBERG_PARTS\"", StringComparison.Ordinal), "LOT_SIZE filter more than once"),
            (ExchangeInfo(tick: "0.00000000"), "PRICE_FILTER"),
            (ExchangeInfo(step: "0"), "LOT_SIZE filter with a step size above zero"),
            (ExchangeInfo(minQty: "a lot"), "minimum quantity"),
        })
        {
            Assert.False(InstrumentDefinition.TryParse(body, "BTCUSDT", out var none, out var said));
            log.WriteLine(said);
            Assert.Null(none);
            Assert.Contains(word, said, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// (b) A FAILED CHECK IS A ROW AND LEAVES THE INSTRUMENT UNVERIFIED. Three ways a check fails — a host
    /// answering 503, an answer that is not a definition, and no answer at all inside the leash — and each
    /// is a <c>failed</c> row with its reason in words and no numbers, the body hashed where there was one.
    /// The served row stays the shipped, unverified one.
    /// </summary>
    [Fact]
    public async Task A_failed_check_is_a_row_and_leaves_the_instrument_unverified()
    {
        using var db = TestEnv.NewDb();
        var venues = new VenueStore(db);
        venues.Sync(Shipped());

        using (var down = new FakeArchive { AlwaysAnswer = HttpStatusCode.ServiceUnavailable })
        {
            var row = await Verifier(db, Shape(down)).CheckAsync(VenueCatalog.BinanceSpot, "BTCUSDT");
            log.WriteLine(row?.Note ?? "(no note)");
            Assert.Equal(InstrumentCheckOutcome.Failed, row!.Outcome);
            Assert.Equal(503, row.HttpStatus);
            Assert.Contains("answered 503", row.Note, StringComparison.Ordinal);
            Assert.Null(row.QuantityIncrement);
        }

        using (var odd = new FakeArchive())
        {
            odd.PublishAtExactly(DefinitionPath, "<html>maintenance</html>");
            var row = await Verifier(db, Shape(odd)).CheckAsync(VenueCatalog.BinanceSpot, "BTCUSDT");
            log.WriteLine(row?.Note ?? "(no note)");
            Assert.Equal(InstrumentCheckOutcome.Failed, row!.Outcome);
            Assert.Equal(200, row.HttpStatus);
            Assert.Equal(64, row.BodySha256!.Length);
            Assert.Contains("could not be read as BTCUSDT's definition", row.Note, StringComparison.Ordinal);
            Assert.Null(row.TickSize);
        }

        using (var silent = new FakeArchive(answers: false))
        {
            var row = await Verifier(db, Shape(silent), timeout: TimeSpan.FromMilliseconds(300))
                .CheckAsync(VenueCatalog.BinanceSpot, "BTCUSDT");
            log.WriteLine(row?.Note ?? "(no note)");
            Assert.Equal(InstrumentCheckOutcome.Failed, row!.Outcome);
            Assert.Null(row.HttpStatus);
            Assert.Contains("did not answer within", row.Note, StringComparison.Ordinal);
        }

        Assert.Equal("3", Scalar(db, "SELECT COUNT(*) FROM instrument_check WHERE outcome='failed'"));
        Assert.Empty(new InstrumentCheckStore(db).LatestVerified());

        var served = venues.Instrument(VenueCatalog.BinanceSpot, "BTCUSDT")!;
        Assert.False(served.Verified);
    }

    /// <summary>
    /// (d) A DEFINITION URL FROM ANOTHER ORIGIN IS REFUSED AND NEVER VERIFIES. A <c>venues.json</c> entry —
    /// a file the AI's own program can write — names a definition address on another origin, serving a
    /// perfectly good definition. The check is a <c>refused-origin</c> row carrying the origin it refused;
    /// NOTHING reached that listener, nothing reached the built-in one either, and the instrument is not
    /// verified. The <c>U-key-host-pin</c> rule, through the one <c>UrlOrigin</c>.
    /// </summary>
    [Fact]
    public async Task A_definition_url_from_another_origin_is_refused_and_never_verifies()
    {
        using var db = TestEnv.NewDb();
        var venues = new VenueStore(db);
        venues.Sync(Shipped());

        using var builtIn = new FakeArchive();
        using var elsewhere = new FakeArchive();
        builtIn.PublishAtExactly(DefinitionPath, ExchangeInfo());
        elsewhere.PublishAtExactly(DefinitionPath, ExchangeInfo());

        var row = await Verifier(db, Shape(builtIn), Overriding(Shape(elsewhere)))
            .CheckAsync(VenueCatalog.BinanceSpot, "BTCUSDT");

        log.WriteLine(row?.Note ?? "(no note)");
        Assert.Equal(InstrumentCheckOutcome.RefusedOrigin, row!.Outcome);
        Assert.Equal(elsewhere.BaseUrl, row.Origin);
        Assert.Null(row.HttpStatus);
        Assert.Null(row.BodySha256);
        Assert.Null(row.QuantityIncrement);
        Assert.Contains("sent nothing", row.Note, StringComparison.Ordinal);
        Assert.Contains(builtIn.BaseUrl, row.Note, StringComparison.Ordinal);

        Assert.DoesNotContain(elsewhere.Marks, m => m.Contains("got GET", StringComparison.Ordinal));
        Assert.DoesNotContain(builtIn.Marks, m => m.Contains("got GET", StringComparison.Ordinal));
        Assert.Empty(new InstrumentCheckStore(db).LatestVerified());
        Assert.False(venues.Instrument(VenueCatalog.BinanceSpot, "BTCUSDT")!.Verified);
    }

    /// <summary>
    /// AN OVERRIDE ON THE BUILT-IN ORIGIN IS ASKED. The rule is about the HOST, not about the file: a venue
    /// that moves its endpoint to another path on its own host costs a one-line data fix, and the check
    /// goes to the moved path and verifies.
    /// </summary>
    [Fact]
    public async Task An_override_on_the_built_in_origin_is_asked()
    {
        using var db = TestEnv.NewDb();
        using var venue = new FakeArchive();
        venue.PublishAtExactly("/api/v4/exchangeInfo?symbol=BTCUSDT", ExchangeInfo());

        var row = await Verifier(db, Shape(venue), Overriding(venue.BaseUrl + "/api/v4/exchangeInfo?symbol={symbol}"))
            .CheckAsync(VenueCatalog.BinanceSpot, "BTCUSDT");

        log.WriteLine(string.Join("\n", venue.Marks));
        Assert.Equal(InstrumentCheckOutcome.Verified, row!.Outcome);
        Assert.Equal(venue.BaseUrl + "/api/v4/exchangeInfo?symbol=BTCUSDT", row.Url);
        Assert.Equal(0.00001m, row.QuantityIncrement);
    }

    // ---- (f) no op writes it -------------------------------------------------------------------------

    /// <summary>
    /// (f) NO PIPE OP WRITES AN INSTRUMENT CHECK — a GUARD, green before this unit as after it, in the shape
    /// of <c>OrgLedgerTests.No_pipe_op_writes_an_org_table</c>.
    ///
    /// <para><b>Four layers, because a name is the weakest of them.</b> No op and no <c>trade</c> verb names
    /// a check, a verification or a definition, and no op takes an argument that would address one. The
    /// store's public surface is its one append and three reads. No source file but the store writes the
    /// table with SQL, so no handler can do it by hand. And the pipe server's own assembly does not even
    /// reference the assembly the verifier lives in, so no handler can start a check: an agent that could
    /// record its own instrument definition could choose the step its sizes are rounded to.</para>
    /// </summary>
    [Fact]
    public void No_pipe_op_writes_an_instrument_check()
    {
        string[] words = ["verif", "check", "definition", "exchangeinfo", "exchange-info"];
        foreach (var op in GatewaySchema.Ops())
        {
            foreach (var word in words)
            {
                Assert.DoesNotContain(word, op.Op, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(word, op.Cli, StringComparison.OrdinalIgnoreCase);
            }

            foreach (var arg in op.Args)
                foreach (var word in words.Concat(["url", "tick", "notional"]))
                    Assert.DoesNotContain(word, arg.Name, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(
            ["Append", "ById", "Latest", "LatestVerified"],
            typeof(InstrumentCheckStore)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(m => m.Name).Order(StringComparer.Ordinal).ToArray());

        var root = RepoRoot();
        var writes = new Regex(
            @"\b(INSERT\s+(OR\s+\w+\s+)?INTO|REPLACE\s+INTO|UPDATE|DELETE\s+FROM|DROP\s+TABLE|ALTER\s+TABLE)\s+instrument_check\b",
            RegexOptions.IgnoreCase);
        var writers = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => writes.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal).ToArray();
        log.WriteLine(string.Join(", ", writers));
        Assert.Equal(["src/TradeAgent.Core/Db/InstrumentCheckStore.cs"], writers);

        var pipe = typeof(GatewayPipeServer).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        log.WriteLine(string.Join(", ", pipe.Where(n => n.StartsWith("TradeAgent.", StringComparison.Ordinal))));
        Assert.DoesNotContain("TradeAgent.Provisioning", pipe);
    }

    // ---- helpers -------------------------------------------------------------------------------------

    /// <summary>The path and query the verifier asks a loopback listener for BTCUSDT.</summary>
    const string DefinitionPath = "/api/v3/exchangeInfo?symbol=BTCUSDT";

    /// <summary>A listener's stand-in for the built-in definition shape.</summary>
    static string Shape(FakeArchive archive) => archive.BaseUrl + "/api/v3/exchangeInfo?symbol={symbol}";

    /// <summary>
    /// A verifier whose BUILT-IN definition address is <paramref name="builtIn"/> — a loopback listener, never
    /// the vendor — over the shipped catalogue unless a test hands it another.
    /// </summary>
    static InstrumentVerifier Verifier(Database db, string builtIn, VenueCatalogRead? catalogue = null,
        TimeSpan? timeout = null, Func<DateTimeOffset>? now = null) =>
        new(db, catalogue ?? Shipped(),
            builtInDefinition: venue => venue == VenueCatalog.BinanceSpot ? builtIn : null,
            requestTimeout: timeout, now: now ?? (() => T0));

    /// <summary>The catalogue this build ships, read past any venues.json: BTCUSDT unverified, no definition override.</summary>
    static VenueCatalogRead Shipped() => VenueCatalog.Read(Path.Combine(TestEnv.Home, "no-such-venues.json"));

    /// <summary>The shipped catalogue with Binance spot's definition address overridden, as a venues.json entry would.</summary>
    static VenueCatalogRead Overriding(string shape)
    {
        var read = Shipped();
        read.Venues.Single(v => v.Id == VenueCatalog.BinanceSpot).DefinitionUrl = shape;
        return read;
    }

    /// <summary>
    /// ONE SYMBOL'S exchangeInfo ANSWER, IN THE SHAPE MEASURED ON 2026-10-03 — decimals as strings, the
    /// filters in Binance's order, and the <c>MARKET_LOT_SIZE</c> step of zero that a careless reader takes.
    /// </summary>
    static string ExchangeInfo(string symbol = "BTCUSDT", string tick = "0.01000000", string step = "0.00001000",
        string minQty = "0.00001000", string minNotional = "5.00000000", string notionalFilter = "NOTIONAL") =>
        "{\"timezone\":\"UTC\",\"serverTime\":1791002934000,\"rateLimits\":[],\"exchangeFilters\":[],\"symbols\":["
        + Entry(symbol, tick, step, minQty, minNotional, notionalFilter) + "]}";

    static string Entry(string symbol, string tick = "0.01000000", string step = "0.00001000",
        string minQty = "0.00001000", string minNotional = "5.00000000", string notionalFilter = "NOTIONAL") =>
        "{\"symbol\":\"" + symbol + "\",\"status\":\"TRADING\",\"baseAsset\":\"BTC\",\"quoteAsset\":\"USDT\",\"filters\":["
        + "{\"filterType\":\"PRICE_FILTER\",\"minPrice\":\"0.01000000\",\"maxPrice\":\"1000000.00000000\",\"tickSize\":\"" + tick + "\"},"
        + "{\"filterType\":\"LOT_SIZE\",\"minQty\":\"" + minQty + "\",\"maxQty\":\"9000.00000000\",\"stepSize\":\"" + step + "\"},"
        + "{\"filterType\":\"ICEBERG_PARTS\",\"limit\":100},"
        + "{\"filterType\":\"MARKET_LOT_SIZE\",\"minQty\":\"0.00000000\",\"maxQty\":\"127.86529633\",\"stepSize\":\"0.00000000\"},"
        + "{\"filterType\":\"" + notionalFilter + "\",\"minNotional\":\"" + minNotional + "\",\"applyMinToMarket\":true,"
        + "\"maxNotional\":\"9000000.00000000\",\"applyMaxToMarket\":false,\"avgPriceMins\":5},"
        + "{\"filterType\":\"MAX_NUM_ORDERS\",\"maxNumOrders\":200}]}";

    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "src"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("could not find the repository root");
    }

    static string? Scalar(Database db, string sql) => db.Read(_ =>
    {
        using var c = db.Cmd(sql);
        return Convert.ToString(c.ExecuteScalar(), CultureInfo.InvariantCulture);
    });
}

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

    // ---- the served read -------------------------------------------------------------------------------

    /// <summary>
    /// (a) A SUCCESSFUL CHECK SERVES THE INSTRUMENT VERIFIED WITH THE VENUE'S NUMBERS. The catalogue's row
    /// here DISAGREES with the venue — a step of 0.001 nobody checked — so whose number is served is
    /// visible: the venue's 0.00001, with the address and the instant it was read as its source, and the
    /// catalogue's 0.001 kept beside it rather than lost. The same row reaches every reader of an increment:
    /// the served list, the paper connector's catalogue and the referee's cost model, whose pinned text
    /// records where its step came from. Red on base, where nothing overlays the catalogue at all.
    /// </summary>
    [Fact]
    public async Task A_successful_check_serves_the_instrument_verified_with_the_venue_numbers()
    {
        using var db = TestEnv.NewDb();
        var catalogue = Shipped();
        catalogue.Venues.Single(v => v.Id == VenueCatalog.BinanceSpot).Instruments.Single().QuantityIncrement = 0.001m;
        var venues = new VenueStore(db, () => T0.AddHours(1));
        venues.Sync(catalogue);
        Assert.False(venues.Instrument(VenueCatalog.BinanceSpot, "BTCUSDT")!.Verified);

        using var venue = new FakeArchive();
        venue.PublishAtExactly(DefinitionPath, ExchangeInfo());
        var row = await Verifier(db, Shape(venue)).CheckAsync(VenueCatalog.BinanceSpot, "BTCUSDT");

        Assert.Equal(InstrumentCheckOutcome.Verified, row!.Outcome);
        Assert.Equal(venue.BaseUrl + DefinitionPath, row.Url);
        Assert.Equal(venue.BaseUrl, row.Origin);
        Assert.Equal(200, row.HttpStatus);
        Assert.Equal(FakeArchive.Sha256(System.Text.Encoding.UTF8.GetBytes(ExchangeInfo())), row.BodySha256);
        Assert.Equal((0.01m, 0.00001m, 0.00001m, 5m), (row.TickSize, row.QuantityIncrement, row.MinQuantity, row.MinNotional));

        var served = venues.Instrument(VenueCatalog.BinanceSpot, "BTCUSDT")!;
        log.WriteLine(served.Source);
        Assert.True(served.Verified);
        Assert.Equal(0.01m, served.TickSize);
        Assert.Equal(0.00001m, served.QuantityIncrement);
        Assert.Equal(row, served.Check);
        Assert.Equal(row.ReceivedAt, served.RecordedAt);
        Assert.Contains(row.Url, served.Source, StringComparison.Ordinal);
        Assert.Contains("2026-10-03T04:48:54Z", served.Source, StringComparison.Ordinal);
        Assert.Equal(0.001m, served.CatalogueQuantityIncrement);
        Assert.Null(served.CatalogueTickSize);
        Assert.Equal(served, Assert.Single(venues.Instruments(VenueCatalog.BinanceSpot)));

        // THE PAPER CONNECTOR'S CATALOGUE AND THE REFEREE'S COST MODEL READ THE SAME ROW.
        var offered = venues.Catalogue().Venues.Single(v => v.Id == VenueCatalog.BinanceSpot).Instruments.Single();
        Assert.True(offered.Verified);
        Assert.Equal(0.00001m, offered.QuantityIncrement);

        var judge = Core.Strategy.VenueCostModel.For(Bars(), venues, 10_000m);
        Assert.True(judge.Ok, judge.Why);
        Assert.Equal(0.00001m, judge.Model!.Model.QuantityIncrement);
        Assert.Contains(row.Url, judge.Model.Canonical, StringComparison.Ordinal);

        var said = venues.Verification(VenueCatalog.BinanceSpot, "BTCUSDT");
        log.WriteLine(said.Says);
        Assert.True(said.Verified);
        Assert.StartsWith("verified against Binance spot's published instrument definition on 2026-10-03 04:48 UTC: "
                          + "tick 0.01, step 0.00001, minimum quantity 0.00001, minimum notional 5 (recorded, not applied)",
            said.Says, StringComparison.Ordinal);
        Assert.Contains("TradeAgent's catalogue says step 0.001, and the venue's number is the one used", said.Says,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// (c) A CHECK OLDER THAN SEVEN DAYS NO LONGER VERIFIES. Seven days to the instant is still served; one
    /// second more and the catalogue's own unverified row stands again — in the list, in the paper
    /// connector's catalogue and in the cost model, which refuses — and the sentence says why. The rows are
    /// untouched: the age is judged at read time, on the reader's clock.
    /// </summary>
    [Fact]
    public async Task A_check_older_than_seven_days_no_longer_verifies()
    {
        using var db = TestEnv.NewDb();
        var clock = T0;
        var venues = new VenueStore(db, () => clock);
        venues.Sync(Shipped());

        using var venue = new FakeArchive();
        venue.PublishAtExactly(DefinitionPath, ExchangeInfo());
        var row = await Verifier(db, Shape(venue)).CheckAsync(VenueCatalog.BinanceSpot, "BTCUSDT");
        Assert.True(row!.IsVerified);

        clock = row.ReceivedAt + VenueStore.CheckServedFor;
        Assert.True(venues.Instrument(VenueCatalog.BinanceSpot, "BTCUSDT")!.Verified, "seven days to the instant is still served");

        clock = row.ReceivedAt + VenueStore.CheckServedFor + TimeSpan.FromSeconds(1);
        var lapsed = venues.Instrument(VenueCatalog.BinanceSpot, "BTCUSDT")!;
        Assert.False(lapsed.Verified, "a check older than seven days still verified the instrument");
        Assert.Null(lapsed.Check);
        Assert.Contains("NOT confirmed", lapsed.Source, StringComparison.Ordinal);
        Assert.False(venues.Catalogue().Venues.Single(v => v.Id == VenueCatalog.BinanceSpot).Instruments.Single().Verified);
        Assert.False(Core.Strategy.VenueCostModel.For(Bars(), venues, 10_000m).Ok);

        var said = venues.Verification(VenueCatalog.BinanceSpot, "BTCUSDT");
        log.WriteLine(said.Says);
        Assert.False(said.Verified);
        Assert.Equal("not verified: the last check, on 2026-10-03 04:48 UTC, is more than seven days old", said.Says);
        Assert.Equal(row, new InstrumentCheckStore(db).ById(row.Id));
    }

    /// <summary>
    /// (e) THE RUNNING PAPER CONNECTOR OFFERS AN INSTRUMENT VERIFIED AFTER IT STARTED — and stops offering it
    /// when the check lapses, with no restart either way. Before the check it offers nothing and REFUSES an
    /// order outright; after it, it offers BTCUSDT on the venue's grid and sizes an order DOWN to the
    /// venue's step; eight days on it offers nothing again. And the hosts' own wiring,
    /// <c>Platforms.Connectors.Create</c>, hands the served read to the connector it builds. Red on base,
    /// where the connector read its catalogue once, when it was built.
    /// </summary>
    [Fact]
    public async Task The_running_paper_connector_offers_an_instrument_verified_after_it_started()
    {
        using var db = TestEnv.NewDb();
        var clock = T0;
        var venues = new VenueStore(db, () => clock);
        venues.Sync(Shipped());

        await using var paper = new Connectors.Paper.PaperConnector(new Connectors.Paper.PaperConnectorOptions
        {
            Source = new Connectors.Paper.MemoryBarSource(),
            Clock = () => T0,
            BookFile = Path.Combine(TestEnv.Home, $"paper-{Guid.NewGuid():n}.db"),
            CatalogueNow = () => venues.Catalogue()
        });
        await paper.ConnectAsync();

        Assert.Empty(await paper.GetInstrumentsAsync());
        var refused = await Assert.ThrowsAsync<ConnectorSdk.ConnectorRejectedException>(
            () => paper.PlaceOrderAsync(Market("before-check")));
        log.WriteLine(refused.Message);

        using var venue = new FakeArchive();
        venue.PublishAtExactly(DefinitionPath, ExchangeInfo());
        Assert.True((await Verifier(db, Shape(venue)).CheckAsync(VenueCatalog.BinanceSpot, "BTCUSDT"))!.IsVerified);

        var offered = Assert.Single(await paper.GetInstrumentsAsync());
        Assert.Equal(("BTCUSDT", 0.01m), (offered.Symbol, offered.TickSize));
        var placed = await paper.PlaceOrderAsync(Market("after-check"));
        Assert.Equal(0.12345m, placed.Quantity);

        await using var built = Platforms.Connectors.Create(Platforms.Connectors.Paper, new Platforms.ConnectorChoice
        {
            PaperInstruments = () => venues.Catalogue()
        });
        Assert.Equal("BTCUSDT", Assert.Single(await built.GetInstrumentsAsync()).Symbol);

        clock = T0.AddDays(8);
        Assert.Empty(await paper.GetInstrumentsAsync());
        Assert.Empty(await built.GetInstrumentsAsync());
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

        // AND IT SAYS WHY: the last attempt's own reason, in the sentence every surface shows.
        var said = venues.Verification(VenueCatalog.BinanceSpot, "BTCUSDT");
        log.WriteLine(said.Says);
        Assert.False(said.Verified);
        Assert.StartsWith("not verified: the last check, at 2026-10-03 04:48 UTC, failed: the host did not answer within",
            said.Says, StringComparison.Ordinal);
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

        var said = venues.Verification(VenueCatalog.BinanceSpot, "BTCUSDT").Says;
        log.WriteLine(said);
        Assert.StartsWith("not verified: the last check, at 2026-10-03 04:48 UTC, was refused: the definition address ",
            said, StringComparison.Ordinal);
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

    // ---- the surfaces ---------------------------------------------------------------------------------

    /// <summary>
    /// <c>status</c> SAYS WHETHER THE MARKET-DATA PAIR IS VERIFIED, AND WHY — the Market data card's own
    /// sentence, off the same served read every sizing reader uses — and, while a check stands, the venue's
    /// numbers, the address and the instant. Before any check it says none has been made; nothing on it is
    /// an invented "verified".
    /// </summary>
    [Fact]
    public async Task The_status_says_whether_the_market_data_pair_is_verified()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;

        using (var wire = System.Text.Json.JsonDocument.Parse(Json.Write(await gw.StatusAsync())))
        {
            var check = wire.RootElement.GetProperty("instrument_check");
            log.WriteLine(check.GetRawText());
            Assert.Equal("BTCUSDT", check.GetProperty("symbol").GetString());
            Assert.False(check.GetProperty("verified").GetBoolean());
            Assert.Equal("not verified: TradeAgent has not checked it against Binance spot's published instrument definition yet",
                check.GetProperty("says").GetString());
            Assert.False(check.TryGetProperty("quantity_increment", out _));
        }

        var now = gw.UtcNow;
        var row = new InstrumentCheckStore(db).Append(Attempt(InstrumentCheckOutcome.Verified) with
        {
            RequestedAt = now, ReceivedAt = now, HttpStatus = 200,
            TickSize = 0.01m, QuantityIncrement = 0.00001m, MinQuantity = 0.00001m, MinNotional = 5m
        });

        using (var wire = System.Text.Json.JsonDocument.Parse(Json.Write(await gw.StatusAsync())))
        {
            var check = wire.RootElement.GetProperty("instrument_check");
            log.WriteLine(check.GetRawText());
            Assert.True(check.GetProperty("verified").GetBoolean());
            Assert.Equal(row.Url, check.GetProperty("url").GetString());
            Assert.Equal(0.00001m, check.GetProperty("quantity_increment").GetDecimal());
            Assert.Equal(5m, check.GetProperty("min_notional").GetDecimal());
            Assert.StartsWith("verified against Binance spot's published instrument definition on ",
                check.GetProperty("says").GetString(), StringComparison.Ordinal);
        }

        var schema = Assert.Single(GatewaySchema.Ops(), o => o.Op == Ops.Status).Description;
        Assert.Contains("instrument_check", schema, StringComparison.Ordinal);
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

    /// <summary>Dataset bars of BTCUSDT on Binance spot, as the collector records them — all the cost model reads of a dataset.</summary>
    static DatasetRecord Bars() =>
        new(7, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 12, [],
            "/not/read/here.csv", "aa11", 1000, T0.AddDays(-60), T0.AddDays(-30), 0, [], false, 0, 0, 0, T0,
            DatasetState.ACCEPTED, null, [])
        {
            VenueId = VenueCatalog.BinanceSpot,
            InstrumentSymbol = "BTCUSDT"
        };

    /// <summary>A market buy of 0.123456 BTCUSDT on the paper account, which the venue's step rounds DOWN to 0.12345.</summary>
    static ConnectorSdk.PlaceOrderCommand Market(string clientOrderId) =>
        new(clientOrderId, Connectors.Paper.PaperConnector.TheAccount, "BTCUSDT", ConnectorSdk.OrderSide.Buy,
            ConnectorSdk.OrderType.Market, 0.123456m, null, null, ConnectorSdk.TimeInForce.Day, null);

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

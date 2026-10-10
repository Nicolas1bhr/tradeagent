using System.Text.Json;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Connectors.Paper;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-paper-friction — A PAPER FILL PAYS THE COST MODEL THE REFEREE JUDGES WITH, AND SAYS WHOSE NUMBERS
/// THEY ARE.
///
/// <para><b>What was wrong before this.</b> The paper connector was handed the owner's two settings,
/// non-nullable decimals that defaulted to 0 and that nothing in the app ever wrote, so every paper fill
/// on every installation was FRICTIONLESS: forward evidence with no cost in it, judged later against a
/// referee that charges Binance spot's 0.100% a fill (<c>docs/EDGE-FACTORY.md</c> § 4.5, one cost model
/// at every stage). And the fill's own sentence called any friction that was not zero "declared by the
/// account owner" — which a fee TradeAgent took from its own table is not.</para>
///
/// <para><b>The connector's own default is untouched</b> (<c>PaperSettlementTests</c>, which asserts
/// FRICTIONLESS on a connector built with nothing, stands): the venue model is the HOST's choice, made
/// from the owner's settings at the moment of each fill.</para>
/// </summary>
public class PaperFrictionTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset T0 = new(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);

    static string NewFile() => Path.Combine(TestEnv.Home, $"paper-friction-{Guid.NewGuid():n}.db");

    static KlineBar Bar(DateTimeOffset open, decimal o, decimal h, decimal l, decimal c) => new(open, o, h, l, c, 1m);

    /// <summary>
    /// ONE VERIFIED SPOT ROW, declared the way an account owner declares it — the shipped catalogue's
    /// BTCUSDT is unverified, and the paper connector trades verified rows and nothing else.
    /// </summary>
    static VenueCatalogRead Catalogue() => new(
    [
        new VenueEntry
        {
            Id = VenueCatalog.BinanceSpot,
            DisplayName = "Binance spot",
            CalendarKind = CalendarKind.Continuous,
            Source = "declared by this test, standing in for the account owner's venues.json",
            Verified = true,
            Instruments =
            [
                new VenueInstrumentEntry
                {
                    Symbol = "BTCUSDT", TickSize = 0.01m, QuantityIncrement = 0.001m,
                    Source = "declared by this test", Verified = true
                }
            ]
        }
    ], null);

    static PaperConnector Paper(MemoryBarSource source, string file, Func<PaperFriction> friction) =>
        new(new PaperConnectorOptions
        {
            Source = source,
            Clock = () => T0.AddSeconds(30),
            BookFile = file,
            Catalogue = Catalogue(),
            Friction = friction
        });

    static PlaceOrderCommand Market(string coid) =>
        new(coid, PaperConnector.TheAccount, "BTCUSDT", OrderSide.Buy, OrderType.Market, 1m, null, null,
            TimeInForce.Day, null);

    /// <summary>
    /// One market buy of 1 BTCUSDT placed inside the bar that opened at 100, filled at the next bar's
    /// open of 110 — and the fill as the connector reports it, with the sentence the book recorded.
    /// </summary>
    static async Task<(ExecutionInfo Fill, string Sentence, string? Status)> OneFill(Func<PaperFriction> friction)
    {
        var source = new MemoryBarSource();
        var file = NewFile();
        await using var paper = Paper(source, file, friction);
        await paper.ConnectAsync();

        source.Add("BTCUSDT", Bar(T0, 100m, 101m, 99m, 100m));
        await paper.PlaceOrderAsync(Market("pf-1"));
        source.Add("BTCUSDT", Bar(T0.AddMinutes(1), 110m, 112m, 109m, 111m));

        var fill = Assert.Single(await paper.GetExecutionsAsync(PaperConnector.TheAccount, null));
        var status = paper.StatusDetail;

        using var book = new PaperBook(file, PaperConnector.TheAccount, "USDT", 10_000m);
        return (fill, Assert.Single(book.Fills(null)).Friction, status);
    }

    /// <summary>
    /// (d) A FILL PRICED BY TRADEAGENT'S OWN TABLE SAYS SO, AND NEVER THAT THE OWNER DECLARED IT.
    ///
    /// <para>Red first: the sentence had two readings — FRICTIONLESS, or "declared by the account owner"
    /// for anything else — so the venue's published fee on a paper fill would have been recorded as a
    /// number the owner typed. The model is named by its id and the sha of its text, the same pair a run
    /// records, so a reader can say exactly which table the fill was charged under.</para>
    /// </summary>
    [Fact]
    public async Task The_fill_sentence_names_the_venue_model_not_the_owner()
    {
        var venue = VenueFriction.Of(VenueCatalog.BinanceSpot);
        Assert.NotNull(venue);

        var (fill, sentence, status) =
            await OneFill(() => PaperFriction.Of(FrictionInForce.Resolve(null, null, venue)));
        log.WriteLine(sentence);

        // The venue's numbers, applied: two basis points up on the open, then 0.100% of what was paid.
        Assert.Equal(110m * 1.0002m, fill.Price);
        Assert.Equal(110m * 1.0002m * 0.001m, fill.Fee);

        Assert.Contains(venue.VenueId, sentence, StringComparison.Ordinal);
        Assert.Contains($"v{VenueCostModel.Version}", sentence, StringComparison.Ordinal);
        Assert.Contains(venue.Sha256, sentence, StringComparison.Ordinal);
        Assert.Contains("published standard taker rate", sentence, StringComparison.Ordinal);
        Assert.Contains("ASSUMPTION", sentence, StringComparison.Ordinal);
        Assert.DoesNotContain("account owner", sentence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FRICTIONLESS", sentence, StringComparison.Ordinal);

        // And the connector's own status line carries the same words, not a second wording of them.
        Assert.Contains(sentence, status, StringComparison.Ordinal);
    }

    /// <summary>
    /// (a) AN INSTALLATION WHERE NOBODY EVER CHOSE A FRICTION PAYS THE VENUE'S FEE ON A PAPER FILL — the
    /// rule the app itself hands the connector (<c>AppHost.PaperFrictionFor</c>, which
    /// <c>PaperChoice</c> reads at every fill).
    ///
    /// <para>Red first: the app built the friction from two settings that defaulted to 0 and that nothing
    /// wrote, so this fill was at the open and cost nothing. The mutant this class is watched against is
    /// the venue-model branch of <c>FrictionInForce.Resolve</c> replaced by a zero.</para>
    /// </summary>
    [Fact]
    public async Task An_install_that_never_chose_friction_pays_the_venue_fee_on_a_paper_fill()
    {
        var (gw, _, db) = await TestEnv.Ready();          // a fresh installation: nothing about friction was set
        using var _1 = db;

        var (fill, sentence, _) = await OneFill(() => App.AppHost.PaperFrictionFor(gw.Settings));
        log.WriteLine($"price {fill.Price}, fee {fill.Fee}: {sentence}");

        Assert.Equal(110m * 1.0002m, fill.Price);
        Assert.Equal(110m * 1.0002m * 0.001m, fill.Fee);
        Assert.Contains(VenueFriction.Of(VenueCatalog.BinanceSpot)!.Sha256, sentence, StringComparison.Ordinal);
    }

    /// <summary>
    /// (b) AN OWNER WHO SETS ZERO GETS ZERO — NOT THE VENUE'S FEE, AND NOT A ZERO DRESSED AS A VENUE'S.
    ///
    /// <para>Null and zero are two answers: null is "never chose" and pays the venue model; zero is a
    /// choice and is used exactly, with the sentence saying whose it was. The zero is WRITTEN into the
    /// row and read back as a zero — the row omits only nulls, so a zero that went missing on the way
    /// would turn the owner's choice back into the venue's fee at the next start. Either number may be
    /// overridden alone: the other is still the venue model's.</para>
    /// </summary>
    [Fact]
    public async Task An_owner_override_of_zero_is_used_exactly()
    {
        var (gw, _, db) = await TestEnv.Ready(s =>
        {
            s.PaperFeeOverride = 0m;
            s.PaperSlippageOverride = 0m;
        });
        using var _1 = db;

        var row = db.GetKv("settings")!;
        Assert.Contains("\"paper_fee_override\":0", row, StringComparison.Ordinal);
        Assert.Contains("\"paper_slippage_override\":0", row, StringComparison.Ordinal);
        var loaded = Json.Read<TradeAgentSettings>(row)!;
        Assert.Equal(0m, loaded.PaperFeeOverride);
        Assert.Equal(0m, loaded.PaperSlippageOverride);

        var (fill, sentence, _) = await OneFill(() => App.AppHost.PaperFrictionFor(loaded));
        log.WriteLine(sentence);
        Assert.Equal(110m, fill.Price);                   // the open itself: the owner's zero slippage
        Assert.Equal(0m, fill.Fee);                       // and the owner's zero fee
        Assert.Contains("account owner", sentence, StringComparison.Ordinal);
        Assert.Contains("FRICTIONLESS", sentence, StringComparison.Ordinal);
        Assert.DoesNotContain("venue cost model", sentence, StringComparison.Ordinal);

        // ONE NUMBER OVERRIDDEN, THE OTHER LEFT ALONE: the owner's fee, exactly, and the venue's slippage.
        gw.Update(s => { s.PaperFeeOverride = 0.00075m; s.PaperSlippageOverride = null; });
        var (mixed, said, _) = await OneFill(() => App.AppHost.PaperFrictionFor(gw.Settings));
        log.WriteLine(said);
        Assert.Equal(110m * 1.0002m, mixed.Price);
        Assert.Equal(110m * 1.0002m * 0.00075m, mixed.Fee);
        Assert.Contains("fee fraction 0.00075, declared by the account owner", said, StringComparison.Ordinal);
        Assert.Contains("slippage fraction 0.0002 from TradeAgent's venue cost model", said, StringComparison.Ordinal);
        Assert.DoesNotContain("paper_slippage_override", db.GetKv("settings")!, StringComparison.Ordinal);
    }

    /// <summary>
    /// (e) A ROW WRITTEN BEFORE THIS UNIT READS AS "NEVER CHOSE", AND PAYS THE VENUE MODEL.
    ///
    /// <para>Every such row holds <c>"paper_fee_fraction":0</c> and <c>"paper_slippage_fraction":0</c>:
    /// the settings are written whole with only nulls omitted, and no build ever wrote either field, so
    /// those zeros are defaults and not choices. They are still READ — the row loads as it always did,
    /// and a rollback finds what it wrote — and no longer consulted, even where a hand-edit put something
    /// else there. A save by this build writes no override back: nothing was chosen, so nothing is
    /// recorded as chosen.</para>
    /// </summary>
    [Fact]
    public async Task A_settings_file_from_before_this_unit_reads_as_unset()
    {
        using var db = TestEnv.NewDb();
        db.SetKv("settings",
            """{"mode":"PAPER","market_data_pair":"BTCUSDT","paper_fee_fraction":0,"paper_slippage_fraction":0.0007}""");
        await using var gw = new TradingGateway(db, new FakeConnector(new FakeBroker()), new HealthRegistry());

        Assert.False(gw.Settings.CouldNotBeRead);
        Assert.Equal(0.0007m, gw.Settings.PaperSlippageFraction);        // read, as it always was
        Assert.Null(gw.Settings.PaperFeeOverride);
        Assert.Null(gw.Settings.PaperSlippageOverride);

        var inForce = FrictionInForce.ForPaper(gw.Settings);             // and not consulted
        Assert.Equal(FrictionSource.VenueModel, inForce.FeeSource);
        Assert.Equal(FrictionSource.VenueModel, inForce.SlippageSource);
        Assert.Equal(0.001m, inForce.Fee);
        Assert.Equal(0.0002m, inForce.Slippage);
        Assert.Equal("venue_model", inForce.Source);

        gw.Update(_ => { });
        var saved = db.GetKv("settings")!;
        Assert.DoesNotContain("paper_fee_override", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("paper_slippage_override", saved, StringComparison.Ordinal);
        Assert.Null(Json.Read<TradeAgentSettings>(saved)!.PaperFeeOverride);
    }

    // ---- the surfaces: where an agent and an owner read the friction in force ------------------------

    /// <summary>
    /// THE FRICTION IN FORCE IS NAMED WHERE IT IS READ — <c>status</c> and its schema for the agent, the
    /// daily report and the paper row's own line for the owner — by the rule the connector reads at the
    /// fill, so no surface can describe a friction the fills are not paying.
    /// </summary>
    [Fact]
    public async Task The_status_the_report_and_the_paper_row_name_the_friction_in_force()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        var venue = VenueFriction.Of(VenueCatalog.BinanceSpot)!;

        // NOTHING CHOSEN: the venue model, by id and sha, on the wire.
        using (var wire = JsonDocument.Parse(Json.Write(await gw.StatusAsync())))
        {
            var paper = wire.RootElement.GetProperty("paper_friction");
            log.WriteLine(paper.GetRawText());
            Assert.Equal("venue_model", paper.GetProperty("source").GetString());
            Assert.Equal(0.001m, paper.GetProperty("fee").GetDecimal());
            Assert.Equal(0.0002m, paper.GetProperty("slippage").GetDecimal());
            Assert.Equal("venue-cost-model-v1/binance-spot", paper.GetProperty("model").GetProperty("id").GetString());
            Assert.Equal(venue.Sha256, paper.GetProperty("model").GetProperty("sha256").GetString());
        }

        var schema = Assert.Single(GatewaySchema.Ops(), o => o.Op == Ops.Status).Description;
        Assert.Contains("paper_friction", schema, StringComparison.Ordinal);
        Assert.Contains("venue_model", schema, StringComparison.Ordinal);

        var report = DailyReportText.Render(gw.Reports.Compose(DateTimeOffset.UtcNow));
        Assert.Contains("- paper fills pay: 0.1% + 0.02% (assumption) — Binance spot standard taker, 2026-10-02",
            report, StringComparison.Ordinal);

        // THE OWNER'S NUMBERS: the status says whose, the model is ABSENT rather than null, and the
        // report says "your override".
        gw.Update(s => { s.PaperFeeOverride = 0.0005m; s.PaperSlippageOverride = 0m; });
        using (var wire = JsonDocument.Parse(Json.Write(await gw.StatusAsync())))
        {
            var paper = wire.RootElement.GetProperty("paper_friction");
            log.WriteLine(paper.GetRawText());
            Assert.Equal("owner", paper.GetProperty("source").GetString());
            Assert.Equal(0.0005m, paper.GetProperty("fee").GetDecimal());
            Assert.Equal(0m, paper.GetProperty("slippage").GetDecimal());
            Assert.False(paper.TryGetProperty("model", out _));
        }
        Assert.Contains("- paper fills pay: 0.05% + 0% — your override",
            DailyReportText.Render(gw.Reports.Compose(DateTimeOffset.UtcNow)), StringComparison.Ordinal);

        // ONE OF EACH: the source names both, and the row says which number is whose.
        gw.Update(s => s.PaperSlippageOverride = null);
        Assert.Equal("owner/venue_model", (await gw.StatusAsync()).PaperFriction!.Source);
        Assert.Equal("0.05% (your override) + 0.02% (assumption)", FrictionInForce.ForPaper(gw.Settings).Line);
    }

    /// <summary>
    /// THE PAPER ROW'S TWO BOXES READ A PERCENTAGE, EMPTY AS "NONE" AND ZERO AS ZERO, a comma as the
    /// decimal point a Belgian keyboard types — and refuse in words what is not a percentage or is more
    /// than any venue charges, rather than saving it.
    /// </summary>
    [Fact]
    public void The_paper_rows_boxes_read_a_percentage_and_refuse_what_no_venue_charges()
    {
        Assert.True(App.SettingsPage.TryPercent("0,075", 0.05m, "fee", out var bnb, out _));
        Assert.Equal(0.00075m, bnb);
        Assert.True(App.SettingsPage.TryPercent(" 0.1 % ", 0.05m, "fee", out var tenth, out _));
        Assert.Equal(0.001m, tenth);
        Assert.True(App.SettingsPage.TryPercent("0", 0.05m, "fee", out var zero, out _));
        Assert.Equal(0m, zero);
        Assert.True(App.SettingsPage.TryPercent("  ", 0.05m, "fee", out var none, out _));
        Assert.Null(none);

        Assert.False(App.SettingsPage.TryPercent("10", 0.05m, "fee", out _, out var tooMuch));
        Assert.Contains("at most 5%", tooMuch, StringComparison.Ordinal);
        Assert.False(App.SettingsPage.TryPercent("-0.1", 0.05m, "slippage", out _, out var negative));
        Assert.Contains("not a slippage", negative, StringComparison.Ordinal);
        Assert.False(App.SettingsPage.TryPercent("ten bp", 0.05m, "fee", out _, out var words));
        Assert.Contains("not a fee", words, StringComparison.Ordinal);
    }

    // ---- research: the same table, for a number a backtest leaves out ------------------------------

    static AgentContext Researcher() => AgentContext.ForAgent("agent", CouncilRoles.Research, "attempt-1");

    static string GivenProgram(string text)
    {
        var dir = Path.Combine(Paths.RoleHome(CouncilRoles.Research), "strategies");
        Directory.CreateDirectory(dir);
        var file = $"friction-{Guid.NewGuid():n}.strategy";
        File.WriteAllText(Path.Combine(dir, file), text);
        return Path.Combine("strategies", file);
    }

    /// <summary>
    /// (c) A NUMBER A RESEARCH RUN LEAVES OUT IS THE VENUE'S, A NUMBER IT DECLARES IS ITS OWN — PER
    /// NUMBER — AND THE RUN RECORDS WHICH.
    ///
    /// <para>Red first: an undeclared fee was zero, so a run that declared only its slippage measured a
    /// market where trading costs nothing but slippage, over Binance spot's bars, and was later judged
    /// under Binance spot's 0.100%. The provenance is recorded with the run and comes back in the answer;
    /// it is not part of the run's identity, and the increment's sentence stays the increment's.</para>
    /// </summary>
    [Fact]
    public async Task An_undeclared_research_friction_field_takes_the_venue_value_and_a_declared_one_wins()
    {
        var w = await CostModelPinTests.Given();
        using var _1 = w.Db;
        var program = GivenProgram(CostModelPinTests.BtcProgram);
        var venue = VenueFriction.Of(VenueCatalog.BinanceSpot)!;

        // THE SLIPPAGE DECLARED, THE FEE LEFT OUT: the venue's fee and the caller's slippage.
        var ran = w.Gw.Backtests.Run(Researcher(), new BacktestAsk(program, w.Set.Id, Slippage: 0.0005m));
        log.WriteLine($"{ran.Result.Request.Model.Canonical} — {ran.FrictionSource}");
        Assert.Equal("fees=0.001;slippage=0.0005;increment=0.00001;capital=10000", ran.Result.Request.Model.Canonical);

        var run = w.Gw.Strategies.RunById(ran.Result.RunId)!;
        Assert.Equal(ran.FrictionSource, run.FrictionSource);
        Assert.Contains($"fee 0.001 from {venue.Named}", run.FrictionSource, StringComparison.Ordinal);
        Assert.Contains("slippage 0.0005 declared by the caller", run.FrictionSource, StringComparison.Ordinal);
        Assert.Contains($"{VenueCatalog.BinanceSpot}/BTCUSDT", run.IncrementSource, StringComparison.Ordinal);
        Assert.DoesNotContain("slippage", run.IncrementSource!, StringComparison.Ordinal);

        // THE FEE DECLARED AS ZERO, THE SLIPPAGE LEFT OUT: the caller's zero, exactly, and the venue's slippage.
        var free = w.Gw.Backtests.Run(Researcher(), new BacktestAsk(program, w.Set.Id, Fees: 0m));
        log.WriteLine($"{free.Result.Request.Model.Canonical} — {free.FrictionSource}");
        Assert.Equal("fees=0;slippage=0.0002;increment=0.00001;capital=10000", free.Result.Request.Model.Canonical);
        Assert.Contains("fee 0 declared by the caller", free.FrictionSource, StringComparison.Ordinal);
        Assert.Contains($"slippage 0.0002 from {venue.Named}", free.FrictionSource, StringComparison.Ordinal);

        // NOTHING DECLARED: both numbers from the table — the very model the referee pins over these bars.
        var none = w.Gw.Backtests.Run(Researcher(), new BacktestAsk(program, w.Set.Id));
        Assert.Equal(CostModelPinTests.VenueModel, none.Result.Request.Model.Canonical);
        Assert.Contains(venue.Sha256, w.Gw.Strategies.RunById(none.Result.RunId)!.FrictionSource, StringComparison.Ordinal);
        Assert.Equal(3, new[] { ran, free, none }.Select(r => r.Result.RunId).Distinct().Count());
    }

    /// <summary>
    /// BARS WITH NO VENUE ARE CHARGED NOTHING AND SAY SO; A VENUE WHOSE FEE TRADEAGENT NEVER READ IS
    /// REFUSED RATHER THAN CHARGED A GUESS — the two readings the cost model already gives the referee.
    /// </summary>
    [Fact]
    public async Task Bars_with_no_venue_run_frictionless_and_say_so_and_a_venue_with_no_published_fee_is_refused()
    {
        var w = await CostModelPinTests.Given();
        using var _1 = w.Db;
        var program = GivenProgram(CostModelPinTests.BtcProgram);

        var bare = CostModelPinTests.Dataset(w.Gw, venue: null, symbol: null, 84_000m, 250m);
        var ran = w.Gw.Backtests.Run(Researcher(), new BacktestAsk(program, bare.Id, Increment: 0.00001m));
        log.WriteLine($"{ran.Result.Request.Model.Canonical} — {ran.FrictionSource}");
        Assert.Equal("fees=0;slippage=0;increment=0.00001;capital=10000", ran.Result.Request.Model.Canonical);
        Assert.Contains("records no venue", ran.FrictionSource, StringComparison.Ordinal);
        Assert.Contains("no venue recorded", ran.FrictionSource, StringComparison.Ordinal);

        var unread = CostModelPinTests.Dataset(w.Gw, VenueCatalog.RevolutX, "BTCUSDT", 84_000m, 250m);
        var refused = Assert.Throws<GatewayDeniedException>(() => w.Gw.Backtests.Run(
            Researcher(), new BacktestAsk(program, unread.Id, Slippage: 0.0005m, Increment: 0.00001m)));
        log.WriteLine(refused.Message);
        Assert.Equal(ErrorCode.INVALID_REQUEST, refused.Info.Code);
        Assert.Contains("declared no fee", refused.Message, StringComparison.Ordinal);
        Assert.Contains(VenueCatalog.RevolutX, refused.Message, StringComparison.Ordinal);
        Assert.Contains("--fees", refused.Message, StringComparison.Ordinal);

        // Declared, it runs: the refusal is only ever about a number nobody gave.
        var declared = w.Gw.Backtests.Run(Researcher(),
            new BacktestAsk(program, unread.Id, Fees: 0.0009m, Slippage: 0.0005m, Increment: 0.00001m));
        Assert.Equal("fees=0.0009;slippage=0.0005;increment=0.00001;capital=10000",
            declared.Result.Request.Model.Canonical);
    }

    // ---- U-trial-returns: every research run keeps its daily net returns at 1x and 2x the venue cost ------------------

    /// <summary>Three UTC days of the fixture's minute bars, from 2026-08-01 00:00Z: 4,320 of them, the last closing at 00:00Z on the 4th.</summary>
    const int ThreeDays = 3 * 1440;

    /// <summary>The venue model at twice Binance spot's fee and slippage, by hand: 0.2% a fill, four basis points, the run's step and capital.</summary>
    const string TwiceVenueModel = "fees=0.002;slippage=0.0004;increment=0.00001;capital=10000";

    static ExecutionModel Model(string canonical)
    {
        var n = canonical.Split(';').Select(p => decimal.Parse(p[(p.IndexOf('=') + 1)..], System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        return ExecutionModel.Declare(n[0], n[1], n[2], n[3]).Model!;
    }

    /// <summary>A run of the fixture's program over <paramref name="dataset"/> under <paramref name="model"/>, made here — what the app's own evaluation must equal.</summary>
    static BacktestResult Direct(CostModelPinTests.World w, long dataset, string model) =>
        Backtest.Over(w.Gw.Datasets, dataset, StrategyParser.Parse(CostModelPinTests.BtcProgram).Program!, Model(model),
            BarAudience.Pipe(CouncilRoles.Research)).Result!;

    static IReadOnlyList<DailyReturn> DaysOf(BacktestResult run) =>
        DailyReturns.Of(run.Trace, BarGrid.For(StrategyParser.Parse(CostModelPinTests.BtcProgram).Program!), run.Request.Model.InitialCapital);

    string Logged(IReadOnlyList<StrategyStreamRow> streams)
    {
        foreach (var s in streams)
            log.WriteLine($"{s.Multiple}x {s.ExecutionModel ?? "-"} friction {s.FrictionSha256 ?? "-"} trace {s.TraceSha256 ?? "-"} "
                + $"{s.Outcome ?? "-"} method {s.Method} missing: {s.Missing ?? "-"}; "
                + string.Join(", ", s.Days.Select(d => $"{d.Day:MM-dd} {d.Bars} bars mark {d.Mark} return {d.NetReturn}")));
        return string.Join(",", streams.Select(s => s.Multiple));
    }

    /// <summary>
    /// (c) A RESEARCH RUN KEEPS ITS DAILY NET RETURNS AT 1x AND 2x TRADEAGENT'S VENUE COST MODEL. A run that declares
    /// nothing over three UTC days answers exactly as before — its trace is the one a run under its model makes — and
    /// leaves two streams: 1x, the venue model it ran under, computed from the run's OWN trace; and 2x, the venue's fee and
    /// slippage doubled at the run's own step and capital, from an evaluation of the app's own whose trace is a direct run
    /// at 2x. Three days each, named by the venue friction's sha and the rule's version, and the 2x account ends below the
    /// 1x one. The extra evaluation is no run of anyone's: one run row, and the answer names no stream.
    /// </summary>
    [Fact]
    public async Task A_research_run_keeps_daily_net_returns_at_1x_and_2x_the_venue_cost()
    {
        var w = await CostModelPinTests.Given();
        using var _1 = w.Db;
        var three = CostModelPinTests.Dataset(w.Gw, VenueCatalog.BinanceSpot, "BTCUSDT", 84_000m, 250m, ThreeDays);
        var program = GivenProgram(CostModelPinTests.BtcProgram);
        var venue = VenueFriction.Of(VenueCatalog.BinanceSpot)!;

        var ran = w.Gw.Backtests.Run(Researcher(), new BacktestAsk(program, three.Id));
        var run = w.Gw.Strategies.RunById(ran.Result.RunId)!;

        // THE ANSWER IS TODAY'S: the run's model, its trace, its figures — those of a run made directly under that model.
        var direct = Direct(w, three.Id, CostModelPinTests.VenueModel);
        Assert.Equal(CostModelPinTests.VenueModel, ran.Result.Request.Model.Canonical);
        Assert.Equal(direct.Trace.Sha256, ran.Result.Trace.Sha256);
        Assert.Equal(direct.Metrics, ran.Result.Metrics with { Missing = direct.Metrics.Missing });
        Assert.Equal(direct.Trace.Sha256, run.TraceSha256);
        Assert.Single(w.Gw.Strategies.Runs());

        var streams = w.Gw.Strategies.StreamsOf(run.Id);
        Assert.Equal("1,2", Logged(streams));
        var (one, two) = (streams[0], streams[1]);

        // 1x: THE RUN'S OWN TRACE, because its model is exactly the venue's at 1x.
        Assert.Equal(CostModelPinTests.VenueModel, one.ExecutionModel);
        Assert.Equal(run.TraceSha256, one.TraceSha256);
        Assert.Equal(DaysOf(ran.Result), one.Days);

        // 2x: AN EVALUATION OF THE APP'S OWN at twice the venue's fee and slippage — a direct run at 2x, trace for trace.
        var twice = Direct(w, three.Id, TwiceVenueModel);
        Assert.Equal(TwiceVenueModel, two.ExecutionModel);
        Assert.Equal(twice.Trace.Sha256, two.TraceSha256);
        Assert.NotEqual(one.TraceSha256, two.TraceSha256);
        Assert.Equal(DaysOf(twice), two.Days);

        foreach (var stream in streams)
        {
            Assert.Equal(venue.Sha256, stream.FrictionSha256);
            Assert.Equal(nameof(BacktestOutcome.COMPLETED), stream.Outcome);
            Assert.Equal(DailyReturns.Version, stream.Method);
            Assert.Null(stream.Missing);
            Assert.Equal([new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 3)], stream.Days.Select(d => d.Day));
            Assert.All(stream.Days, d => Assert.Equal(1440, d.Bars));
            Assert.Null(stream.Days[0].Since);
            Assert.Equal(new DateOnly(2026, 8, 2), stream.Days[2].Since);
        }

        // TWICE THE COST IS A LOWER ACCOUNT, every day.
        Assert.All(one.Days.Zip(two.Days), d => Assert.True(d.Second.Mark < d.First.Mark, $"{d.First.Day}: {d.Second.Mark} at 2x, {d.First.Mark} at 1x"));

        // THE SAME RUN ASKED FOR AGAIN WRITES NOTHING: the first account of it stands, streams included.
        w.Gw.Backtests.Run(Researcher(), new BacktestAsk(program, three.Id));
        Assert.Single(w.Gw.Strategies.Runs());
        Assert.Equal(Logged(streams), Logged(w.Gw.Strategies.StreamsOf(run.Id)));
        Assert.Equal(two.TraceSha256, w.Gw.Strategies.StreamsOf(run.Id)[1].TraceSha256);
    }

    /// <summary>
    /// (d) A DECLARED FRICTION KEEPS THE VENUE'S STREAMS, AND BARS WITH NO VENUE MODEL KEEP THEM MISSING. A run declared
    /// at no fee and no slippage is answered at no cost, as declared — and its 1x stream is still the VENUE's, from an
    /// evaluation of the app's own, never the run's frictionless trace. Bars that record no venue, and a venue whose fee
    /// TradeAgent never read, have no venue model at all: both streams are recorded missing, saying why, with no day.
    /// </summary>
    [Fact]
    public async Task Declared_friction_keeps_venue_streams_and_no_venue_keeps_them_missing()
    {
        var w = await CostModelPinTests.Given();
        using var _1 = w.Db;
        var three = CostModelPinTests.Dataset(w.Gw, VenueCatalog.BinanceSpot, "BTCUSDT", 84_000m, 250m, ThreeDays);
        var program = GivenProgram(CostModelPinTests.BtcProgram);

        var free = w.Gw.Backtests.Run(Researcher(), new BacktestAsk(program, three.Id, Fees: 0m, Slippage: 0m));
        const string Declared = "fees=0;slippage=0;increment=0.00001;capital=10000";
        Assert.Equal(Declared, free.Result.Request.Model.Canonical);
        Assert.Equal(Direct(w, three.Id, Declared).Trace.Sha256, free.Result.Trace.Sha256);

        var streams = w.Gw.Strategies.StreamsOf(free.Result.RunId);
        Assert.Equal("1,2", Logged(streams));
        var once = Direct(w, three.Id, CostModelPinTests.VenueModel);
        Assert.Equal(CostModelPinTests.VenueModel, streams[0].ExecutionModel);
        Assert.Equal(once.Trace.Sha256, streams[0].TraceSha256);
        Assert.NotEqual(free.Result.Trace.Sha256, streams[0].TraceSha256);
        Assert.Equal(DaysOf(once), streams[0].Days);
        Assert.Equal(TwiceVenueModel, streams[1].ExecutionModel);
        Assert.Equal(Direct(w, three.Id, TwiceVenueModel).Trace.Sha256, streams[1].TraceSha256);
        Assert.All(streams, s => Assert.Equal(3, s.Days.Count));
        Assert.Single(w.Gw.Strategies.Runs());

        // NO VENUE RECORDED: nothing to charge the bars, so no stream of them — missing, saying so, with no day.
        var bare = CostModelPinTests.Dataset(w.Gw, venue: null, symbol: null, 84_000m, 250m, ThreeDays);
        var unpriced = w.Gw.Backtests.Run(Researcher(), new BacktestAsk(program, bare.Id, Increment: 0.00001m));
        var none = w.Gw.Strategies.StreamsOf(unpriced.Result.RunId);
        Assert.Equal("1,2", Logged(none));
        Assert.All(none, s =>
        {
            Assert.Contains("records no venue", s.Missing, StringComparison.Ordinal);
            Assert.Contains("no venue recorded", s.Missing, StringComparison.Ordinal);
            Assert.Empty(s.Days);
            Assert.Null(s.TraceSha256);
            Assert.Null(s.FrictionSha256);
            Assert.Null(s.Outcome);
            Assert.Equal(DailyReturns.Version, s.Method);
        });

        // A VENUE WHOSE FEE TRADEAGENT NEVER READ: the run is the caller's, declared; the venue's streams cannot be.
        var unread = CostModelPinTests.Dataset(w.Gw, VenueCatalog.RevolutX, "BTCUSDT", 84_000m, 250m, ThreeDays);
        var declared = w.Gw.Backtests.Run(Researcher(),
            new BacktestAsk(program, unread.Id, Fees: 0.0009m, Slippage: 0.0005m, Increment: 0.00001m));
        var unpublished = w.Gw.Strategies.StreamsOf(declared.Result.RunId);
        Assert.Equal("1,2", Logged(unpublished));
        Assert.All(unpublished, s =>
        {
            Assert.Contains(VenueCatalog.RevolutX, s.Missing, StringComparison.Ordinal);
            Assert.Contains("published", s.Missing, StringComparison.Ordinal);
            Assert.Empty(s.Days);
        });
    }
}

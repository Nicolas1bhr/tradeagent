using System.Globalization;
using TradeAgent.App;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Paper;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using TradeAgent.Platforms;
using TradeAgent.Provisioning;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// EVERY PAIR SETUP VERIFIES CAN TRADE ON PAPER, WITH NO RESTART (PREFLIGHT-2 blocker 1; R30 § 3 row 11) — through the path a real install takes, in its
/// real order: onboarding's look at the venue's list while only BTCUSDT is verified, then setup's one press (<see cref="EvidenceCampaigns.SetUpAsync"/>) with
/// the real <see cref="InstrumentVerifier"/> over a loopback venue, then the app's own sweeps and the forward runner on real gates. Nothing is seeded around
/// them: <c>FoundingOrderLimitTests</c> used to hand its gateway a whole catalogue from the first look, which is why that row passed over this defect.
///
/// <para><b>The defect.</b> The gateway's instrument list filled once, while it was empty, and never again: the founding allowlist's reach over the venue's
/// own list therefore ended at whatever the venue listed on the first look, and four of five pairs were refused until a restart. Nothing reaches a vendor;
/// no real money is involved.</para>
/// </summary>
public class PaperPairsAfterSetupTests(ITestOutputHelper log)
{
    /// <summary>When the owner presses: after the download's last bar closed (<see cref="EvidenceCampaignTests"/>).</summary>
    static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset First = new(2023, 10, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Last = new(2026, 9, 30, 23, 59, 0, TimeSpan.Zero);

    /// <summary>The 2026 levels R28 sized against, in USDT.</summary>
    static readonly IReadOnlyDictionary<string, decimal> Prices = new Dictionary<string, decimal>(StringComparer.Ordinal)
    {
        ["BTCUSDT"] = 100_000m, ["ETHUSDT"] = 4_000m, ["SOLUSDT"] = 200m, ["BNBUSDT"] = 1_000m, ["XRPUSDT"] = 2m
    };

    sealed class Clock(DateTimeOffset at) : TimeProvider
    {
        public DateTimeOffset At { get; set; } = at;
        public override DateTimeOffset GetUtcNow() => At;
        public override long GetTimestamp() => At.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    static string Program(string pair) =>
        $"instrument {pair}\ntimezone UTC\ntimeframe 1m\ndata_freshness 5m\nmax_decision_age 5m\nsize capital_fraction 1\n"
        + "exit when close < 0.5\nentry when close > 0.5\n";

    static string ExchangeInfo(string pair) =>
        "{\"timezone\":\"UTC\",\"serverTime\":1791002934000,\"rateLimits\":[],\"exchangeFilters\":[],\"symbols\":["
        + "{\"symbol\":\"" + pair + "\",\"status\":\"TRADING\",\"baseAsset\":\"X\",\"quoteAsset\":\"USDT\",\"filters\":["
        + "{\"filterType\":\"PRICE_FILTER\",\"minPrice\":\"0.00010000\",\"maxPrice\":\"1000000.00000000\",\"tickSize\":\"0.00010000\"},"
        + "{\"filterType\":\"LOT_SIZE\",\"minQty\":\"0.00001000\",\"maxQty\":\"9000000.00000000\",\"stepSize\":\"0.00001000\"},"
        + "{\"filterType\":\"NOTIONAL\",\"minNotional\":\"5.00000000\",\"applyMinToMarket\":true,"
        + "\"maxNotional\":\"9000000.00000000\",\"applyMaxToMarket\":false,\"avgPriceMins\":5}]}]}";

    /// <summary>A version of <paramref name="pair"/> judged PaperEligible under the campaign setup's press opened, through the rows a verdict writes.</summary>
    static string Judged(Database db, CampaignRow campaign, DatasetRecord set, string pair)
    {
        var program = StrategyParser.Parse(Program(pair)).Program!;
        var cutoff = set.HoldoutFrom!.Value;
        var strategies = new StrategyStore(db);
        strategies.RecordVersion(new StrategyVersionRow(
            program.StrategyId, program.Source, program.Canonical, program.Manifest, StrategyStore.InterpreterBuild,
            ParseVerdict.Accepted, program.WarmUpBars, cutoff.AddDays(-1), CouncilRoles.Research, "turn-test")
        {
            Timeframe = program.Freshness?.Timeframe,
            DataFreshness = program.Freshness?.DataFreshness,
            MaxDecisionAge = program.Freshness?.MaxDecisionAge
        });

        var model = ExecutionModel.Declare(0m, 0m, 0.001m, 1_000_000m).Model!;
        var run = new BacktestRequest(set.Id, set.NormalisedSha256, model, cutoff, null).RunIdFor(program.StrategyId);
        strategies.RecordRun(new StrategyRunRow(
            run, program.StrategyId, set.Id, set.NormalisedSha256, cutoff, null, model.Canonical,
            BacktestOutcome.COMPLETED.ToString(), null, 500, 4, 3, 4, 4, 100, 0, 0, 12m, 2m, 10m, 3m, "trace-sha", Now,
            Referee.RunRole, null), []);
        new Promotions(db).Record(new PromotionRow(
            "", program.StrategyId, campaign.Id, CampaignPolicy.Sha256Of(CampaignPolicy.PaperV1), StrategyStore.InterpreterBuild,
            set.Id, set.NormalisedSha256, model.Canonical, Referee.EvaluatorVersion, run,
            PromotionVerdict.PaperEligible, PromotionReason.MetOnHistory, Now));
        return program.StrategyId;
    }

    /// <summary>
    /// ONBOARDING, THEN SETUP, THEN EVERY PAIR'S ENTRY FILLS. RED at the base, on every pair but BTCUSDT:
    /// <c>entry refused: nothing was sent: RISK_LIMIT_EXCEEDED — SOLUSDT is not on the allowed instrument list</c>.
    /// </summary>
    [Fact]
    public async Task A_pair_setup_verifies_after_onboarding_fills_its_entry_with_no_restart()
    {
        await using var app = await EveryPairEnteredAsync();
    }

    /// <summary>
    /// A CLOSE OF A HELD PAIR IS NEVER REFUSED BY AN INSTRUMENT-LIST MISS (<c>U-paper-oco</c>, R31 A3-2): every pair is entered as above, and then
    /// eight days pass with no instrument check — every pair's lapses, so the served list is empty and the four pairs the founding list reaches
    /// only through it are on no list at all. Every program then exits. A close reduces risk and approves no instrument: each run's exit is sized,
    /// sent, allowed and filled, and every book is flat. Opening is still refused: nothing re-enters while the list is empty.
    ///
    /// <para><b>RED at the base</b>: every exit wrote nothing — the runner had no verified increment to size a close by — and every run stayed long.</para>
    /// </summary>
    [Fact]
    public async Task A_held_pair_whose_check_lapsed_still_closes_and_nothing_reopens()
    {
        await using var app = await EveryPairEnteredAsync();

        // EIGHT DAYS ON, AND THE VENUE'S LIST READ AGAIN — as any caller reads it, the agents' instruments op among them: empty.
        var later = Now.AddDays(8);
        app.Clock.At = later;
        Assert.Empty(await app.Gw.InstrumentsAsync());
        for (var minute = 1; minute <= 4; minute++)
            await app.MinuteAsync(later.AddMinutes(minute), _ => 0.4m);

        foreach (var run in app.Gw.Deployments.All().OrderBy(d => d.Symbol, StringComparer.Ordinal))
        {
            var ops = app.Gw.Deployments.OpsOf(run.Id);
            foreach (var op in ops.Where(o => o.BarOpenTime > Now.AddDays(1)))
                log.WriteLine($"{run.Symbol} {op.BarOpenTime:MM-dd HH:mm} {op.Kind} {op.State}: {op.Answer}");
            var book = app.Gw.Deployments.BookOf(run);
            Assert.True(book.Held == 0m, $"{run.Symbol} still holds {book.Held} after its exit");
            Assert.Contains(ops, o => o.Kind == DeploymentOpKind.Exit && o.BarOpenTime > Now.AddDays(1) && o.Answer == nameof(ExecutionState.FILLED));
        }
        Assert.Empty(await app.Conn.GetPositionsAsync(PaperConnector.TheAccount));

        // AND NOTHING OPENS AGAIN: the prices come back up, the programs signal, and every entry is refused before the wire.
        for (var minute = 5; minute <= 7; minute++)
            await app.MinuteAsync(later.AddMinutes(minute), pair => Prices[pair]);
        Assert.Empty(await app.Conn.GetPositionsAsync(PaperConnector.TheAccount));
    }

    /// <summary>The app as <see cref="EveryPairEnteredAsync"/> leaves it: every pair's run long, and a way to add one minute to every pair.</summary>
    sealed class App(Database db, Clock clock, ForwardBarStore bars, ForwardBarSource source, PaperConnector conn, TradingGateway gw) : IAsyncDisposable
    {
        public PaperConnector Conn => conn;
        public Clock Clock => clock;
        public TradingGateway Gw => gw;
        public ForwardRuns Runner { get; } = new(gw, db, () => clock.At);

        /// <summary>One closed minute opening at <paramref name="open"/> on every pair, at <paramref name="price"/>, and a pass of the runner over it.</summary>
        public async Task MinuteAsync(DateTimeOffset open, Func<string, decimal> price)
        {
            var close = open + ForwardBars.BarLength;
            clock.At = close;
            foreach (var pair in EvidenceDefaults.Pairs)
                Assert.Equal(1, bars.Append(new ForwardFetchAttempt
                {
                    Source = ForwardBars.Source, Symbol = pair, Url = "https://example.invalid/klines (this test wrote the rows; nothing was fetched)",
                    RequestedAt = close, ReceivedAt = close.AddSeconds(1), HttpStatus = 200
                }, [new ForwardBars.Kline(open, price(pair), price(pair), price(pair), price(pair), 10m, close)]).Stored);
            clock.At = close.AddSeconds(3);
            foreach (var pair in EvidenceDefaults.Pairs) source.Announce(pair, open);
            await gw.RefreshHealthAsync();
            await Runner.AdvanceAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await gw.DisposeAsync();
            await conn.DisposeAsync();
            db.Dispose();
        }
    }

    /// <summary>
    /// ONBOARDING, THEN SETUP, THEN EVERY PAIR'S ENTRY FILLS — the path a real install takes, asserted on the way (the first test of this class).
    /// </summary>
    async Task<App> EveryPairEnteredAsync()
    {
        var db = TestEnv.NewDb();
        var clock = new Clock(Now);
        var bars = new ForwardBarStore(db);
        var source = new ForwardBarSource(bars);

        // THE BINDING AppHost MAKES: the served catalogue, read at every use.
        var conn = new PaperConnector(new PaperConnectorOptions
        {
            Source = source,
            Clock = () => clock.At,
            BookFile = Path.Combine(TestEnv.Home, $"paper-pairs-{Guid.NewGuid():n}.db"),
            CatalogueNow = () => new VenueStore(db, () => clock.At).Catalogue()
        });
        var gw = new TradingGateway(db, conn, new HealthRegistry(), new GatewayOptions { Clock = clock });
        var app = new App(db, clock, bars, source, conn, gw);
        gw.Update(s => s.SelectedAccountId = PaperConnector.TheAccount);
        await conn.ConnectAsync();

        using var venue = new FakeArchive();
        foreach (var pair in EvidenceDefaults.Pairs) venue.PublishAtExactly($"/api/v3/exchangeInfo?symbol={pair}", ExchangeInfo(pair));
        var verifier = new InstrumentVerifier(db, VenueCatalog.Read(Path.Combine(TestEnv.Home, "no-such-venues.json")),
            builtInDefinition: id => id == VenueCatalog.BinanceSpot ? venue.BaseUrl + "/api/v3/exchangeInfo?symbol={symbol}" : null,
            now: () => clock.At);

        // ONBOARDING: the background check of the configured pair has run, the health pass and the market-data step have looked at the list.
        Assert.True((await verifier.CheckAsync(VenueCatalog.BinanceSpot, "BTCUSDT"))!.IsVerified);
        Assert.Equal(["BTCUSDT"], (await gw.InstrumentsAsync()).Select(i => i.Symbol));
        await gw.RefreshHealthAsync();

        // SETUP'S ONE PRESS: the app's own, its check the real verifier's, over every pair it offers.
        var evidence = new EvidenceCampaigns(() => gw,
            (pair, _, _, _) => Task.FromResult(new DataCollection(EvidenceCampaignTests.OnDisk(db, pair, First, Last),
                [.. BinanceArchive.RecentCompleteMonths(Now).Select(m => new MonthResult(BinanceArchive.MonthName(m), MonthOutcome.Collected, null, ""))],
                "36 of 36 periods collected.")),
            async (pair, ct) => Assert.True((await verifier.CheckAsync(VenueCatalog.BinanceSpot, pair, ct))!.IsVerified));
        var setup = await evidence.SetUpAsync(Now, EvidenceDefaults.Pairs);
        log.WriteLine(setup.Words);
        Assert.True(setup.Ok, setup.Words);
        Assert.Equal(EvidenceDefaults.Pairs.Count, setup.Campaigns.Count);
        await gw.RefreshHealthAsync();

        // EVERY PAIR'S VERSION JUDGED, AND THE APP'S OWN SWEEPS PUT IT ON PAPER.
        foreach (var campaign in setup.Campaigns)
            Judged(db, campaign, gw.Datasets.ById(campaign.HoldoutDatasetId)!, gw.Datasets.ById(campaign.HoldoutDatasetId)!.Pair);
        Assert.Equal(EvidenceDefaults.Pairs.Count, gw.AllocatePaperDue(Now));
        Assert.Equal(EvidenceDefaults.Pairs.Count, gw.StartPaperDeploymentsDue(Now));
        for (var minute = 1; minute <= 4; minute++)
            await app.MinuteAsync(Now.AddMinutes(minute), pair => Prices[pair]);

        foreach (var run in gw.Deployments.All().OrderBy(d => d.Symbol, StringComparer.Ordinal))
        {
            var ops = gw.Deployments.OpsOf(run.Id);
            foreach (var op in ops) log.WriteLine($"{run.Symbol} {op.BarOpenTime:HH:mm} {op.Kind} {op.State}: {op.Answer}");
            var entry = ops.First(o => o.Kind == DeploymentOpKind.Entry);
            Assert.DoesNotContain("is not on the allowed instrument list", entry.Answer ?? "", StringComparison.Ordinal);
            Assert.True(gw.Deployments.FillsOf(run).Any(), $"{run.Symbol}'s entry never filled: {entry.State}: {entry.Answer}");
        }

        return app;
    }
}

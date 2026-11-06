using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Paper;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Platforms;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE FOUNDING QUANTITY LIMIT FITS THE ENVELOPES (R30 D18; R28 P2). Under the founding policy as it ships, on the app's own paper venue with the real
/// runner: a full-size entry on SOL, BNB and XRP fills, where the flat 1 refused every one; an order over the run's share is still refused; the 5,000 USDT
/// per pair still binds; and nothing outside a paper run's own order moved. Nothing reaches a venue and no real money is involved.
/// </summary>
public class FoundingOrderLimitTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Start = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The 2026 levels R28 sized against, in USDT.</summary>
    static readonly (string Pair, decimal Price)[] Pairs = [("SOLUSDT", 200m), ("BNBUSDT", 1_000m), ("XRPUSDT", 2m)];

    sealed class Clock(DateTimeOffset at) : TimeProvider
    {
        public DateTimeOffset At { get; set; } = at;
        public override DateTimeOffset GetUtcNow() => At;
        public override long GetTimestamp() => At.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

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
                .. Pairs.Select(p => new VenueInstrumentEntry
                {
                    Symbol = p.Pair, TickSize = 0.0001m, QuantityIncrement = 0.001m, Source = "declared by this test", Verified = true
                })
            ]
        }
    ], null);

    static string Program(string pair) =>
        $"instrument {pair}\ntimezone UTC\ntimeframe 1m\ndata_freshness 5m\nmax_decision_age 5m\nsize capital_fraction 1\n"
        + "exit when close < 0.5\nentry when close > 0.5\n";

    sealed class Rig : IAsyncDisposable
    {
        public required Database Db { get; init; }
        public required PaperConnector Conn { get; init; }
        public required TradingGateway Gw { get; init; }
        public required ForwardRuns Runner { get; init; }
        public required Clock Clock { get; init; }
        public required ForwardBarStore Bars { get; init; }
        public required ForwardBarSource Source { get; init; }

        /// <summary>One closed minute on every pair, at its price, the way the collector stores one.</summary>
        public void Minute(int minute)
        {
            var open = Start.AddMinutes(minute);
            var close = open + ForwardBars.BarLength;
            Clock.At = close;
            foreach (var (pair, price) in Pairs)
            {
                var append = Bars.Append(new ForwardFetchAttempt
                {
                    Source = ForwardBars.Source, Symbol = pair,
                    Url = "https://example.invalid/klines (this test wrote the rows; nothing was fetched)",
                    RequestedAt = close, ReceivedAt = close.AddSeconds(1), HttpStatus = 200
                }, [new ForwardBars.Kline(open, price, price, price, price, 10m, close)]);
                Assert.Equal(1, append.Stored);
            }

            Clock.At = close.AddSeconds(1);
            foreach (var (pair, _) in Pairs) Source.Announce(pair, open);
        }

        public StrategyDeploymentRow RunOn(string pair) => Gw.Deployments.All().Single(d => d.Symbol == pair);

        public async ValueTask DisposeAsync()
        {
            await Gw.DisposeAsync();
            await Conn.DisposeAsync();
        }
    }

    /// <summary>A home as the founding policy leaves it: its envelopes granted by the first health pass, each pair's one judged version on paper.</summary>
    static async Task<Rig> Founded()
    {
        var db = TestEnv.NewDb();
        var clock = new Clock(Start);
        var bars = new ForwardBarStore(db);
        var source = new ForwardBarSource(bars);
        var conn = new PaperConnector(new PaperConnectorOptions
        {
            Source = source,
            Clock = () => clock.At,
            BookFile = Path.Combine(TestEnv.Home, $"paper-run-{Guid.NewGuid():n}.db"),
            Catalogue = Catalogue()
        });
        var gw = new TradingGateway(db, conn, new HealthRegistry(), new GatewayOptions { Clock = clock, MaxQuoteAge = TimeSpan.FromDays(3650) });
        gw.Update(s => s.SelectedAccountId = PaperConnector.TheAccount);
        new VenueStore(db).Sync(Catalogue());
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        Assert.Equal(BoardFounding.V1.Risk.MaxOrderQuantity, gw.Policy.Risk.MaxOrderQuantity);

        foreach (var (pair, _) in Pairs) ForwardRunnerTests.Judged(db, Program(pair), Start, pair);
        Assert.Equal(Pairs.Length, gw.AllocatePaperDue(Start));
        Assert.Equal(Pairs.Length, gw.StartPaperDeploymentsDue(Start));

        return new Rig
        {
            Db = db, Conn = conn, Gw = gw, Clock = clock, Bars = bars, Source = source,
            Runner = new ForwardRuns(gw, db, () => clock.At)
        };
    }

    int _asked;

    /// <summary>What the gateway answered an order of the run's for <paramref name="quantity"/>, written down under the run's own id.</summary>
    async Task<string> Asked(Rig rig, string pair, decimal quantity)
    {
        var run = rig.RunOn(pair);
        var bar = Start.AddMinutes(1);
        var id = Deployments.RequestIdFor(run.Id, bar, ++_asked);
        await rig.Gw.RunDeploymentIntentAsync(run, id, DeploymentOpKind.Entry, bar,
            new PlaceIntent(pair, OrderSide.Buy, OrderType.Market, quantity, null, null, TimeInForce.Day, $"deployment:{run.Id} test")
            {
                StrategyVersionId = run.VersionId
            });
        var said = rig.Gw.Deployments.OpsOf(run.Id).Single(o => o.RequestId == id).Answer ?? "(no answer)";
        log.WriteLine($"{pair} {quantity}: {said}");
        return said;
    }

    /// <summary>
    /// A NORMAL ENTRY ON SOL, BNB AND XRP PASSES (D18): a full-size entry, its whole capital of 1,666.66 USDT at the 2026 levels, reaches the ledger as a
    /// fill on each pair, through every gate an order meets, with the runner choosing the size.
    ///
    /// <para>RED at the base: <c>quantity 8.333 exceeds the limit of 1</c>, and no run on these three pairs ever filled.</para>
    /// </summary>
    [Fact]
    public async Task Normal_sized_entries_on_sol_bnb_and_xrp_fill_under_the_founding_policy()
    {
        await using var rig = await Founded();

        rig.Minute(1);
        await rig.Runner.AdvanceAsync();
        rig.Minute(2);
        await rig.Gw.RefreshHealthAsync();
        await rig.Runner.AdvanceAsync();
        rig.Minute(3);
        await rig.Gw.RefreshHealthAsync();
        await rig.Runner.AdvanceAsync();

        foreach (var (pair, price) in Pairs)
        {
            var run = rig.RunOn(pair);
            foreach (var op in rig.Gw.Deployments.OpsOf(run.Id)) log.WriteLine($"{pair} {op.Kind} {op.State}: {op.Answer}");

            var fill = Assert.Single(rig.Gw.Deployments.FillsOf(run));
            log.WriteLine($"{pair}: {fill.Quantity} at {fill.Price}");
            Assert.InRange(fill.Quantity, 0.99m * 1_666.66666666m / price, 1_666.66666666m / price);
            Assert.True(fill.Quantity > BoardFounding.V1.Risk.MaxOrderQuantity);
        }
    }

    /// <summary>
    /// AN ORDER OVER THE RUN'S SHARE IS STILL REFUSED, AND THE VALUE STILL BINDS BELOW IT (D18): quantity ceilings stay ceilings. SOL's share of 50 is 16.66…
    /// and an order of 17 is refused by the per-order limit, in its words, on every pair. Under that share, 10 SOL at 200 is worth 2,000 USDT, over the run's
    /// 1,666.66 of the pair's 5,000, and is refused by the capital gate.
    /// </summary>
    [Fact]
    public async Task An_order_over_the_share_is_refused_and_the_notional_still_binds()
    {
        await using var rig = await Founded();
        rig.Minute(1);

        foreach (var (pair, quantity, limit) in new[]
                 {
                     ("SOLUSDT", 17m, 16.66666666m), ("BNBUSDT", 3.4m, 3.33333333m), ("XRPUSDT", 1_700m, 1_666.66666666m)
                 })
            Assert.Contains($"exceeds the limit of {limit}", await Asked(rig, pair, quantity), StringComparison.Ordinal);

        var overValue = await Asked(rig, "SOLUSDT", 10m);
        Assert.Contains("the capital allocated to it is", overValue, StringComparison.Ordinal);
        Assert.Empty(rig.Gw.Deployments.FillsOf(rig.RunOn("SOLUSDT")));
    }

    /// <summary>
    /// NOTHING ELSE MOVED: another caller's order on the same pair is bound by the flat 1, the owner's included, and a figure other than the founding one
    /// binds a run too. The per-pair limit is a paper run's own, under the founding value, and it is a way toward nothing.
    /// </summary>
    [Fact]
    public async Task The_flat_limit_stays_on_every_other_caller_and_a_decided_figure_binds_a_run()
    {
        await using var rig = await Founded();
        rig.Minute(1);

        var owner = await Assert.ThrowsAsync<GatewayDeniedException>(() => rig.Gw.PlaceAsync(AgentContext.Operator, "owner-order",
            new PlaceIntent("SOLUSDT", OrderSide.Buy, OrderType.Market, 2m, null, null, TimeInForce.Day, null)));
        log.WriteLine($"the owner's 2 SOL: {owner.Message}");
        Assert.Contains("exceeds the limit of 1", owner.Message, StringComparison.Ordinal);

        rig.Gw.UpdateRiskUnderTest(r => r.MaxOrderQuantity = 0.5m);
        Assert.Contains("exceeds the limit of 0.5", await Asked(rig, "SOLUSDT", 2m), StringComparison.Ordinal);
    }
}

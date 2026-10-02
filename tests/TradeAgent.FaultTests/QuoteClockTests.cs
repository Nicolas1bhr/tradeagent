using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Connectors.Paper;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// A BAR-FED PRICE STAMPED AT ITS BAR'S CLOSE, ONE CLOCK FOR EVERY AGE ON THE ORDER PATH, AND A
/// HEALTH ROW THAT KNOWS A BAR-FED FEED MOVES ONCE A BAR (<c>U-runner-forward</c> items 3 and 4).
///
/// <para><b>What was wrong.</b> The paper connector stamped its quote <c>SettledThrough +
/// Interval</c>, the interval being the spacing of the LAST TWO bars — so the first bar after a gap
/// was stamped as far in the future as the gap was long, and a price four minutes old passed as a
/// fresh one. <c>QuoteInfo.IsStale</c> read the machine clock at both order-path quote gates and in
/// the loss valuation while the decision gate read the gateway's own, so two gates on one order
/// could disagree about how old one instant was. And the Market data row held a feed that moves once
/// a minute to the thirty seconds a streaming quote is held to, so a healthy paper feed read
/// "degraded" for half of every minute.</para>
///
/// <para>Nothing here reaches a venue: the paper connector's assembly has none, and the practice
/// simulator is an in-memory book. No real money is involved.</para>
/// </summary>
public class QuoteClockTests(ITestOutputHelper log)
{
    /// <summary>A clock the test sets by hand. The gateway and the paper connector read no other.</summary>
    sealed class TestClock(DateTimeOffset at) : TimeProvider
    {
        public DateTimeOffset At { get; set; } = at;
        public override DateTimeOffset GetUtcNow() => At;
    }

    /// <summary>The owner's venues.json, standing in one line: BTCUSDT on Binance spot, VERIFIED.</summary>
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

    static KlineBar Flat(DateTimeOffset open, decimal price) => new(open, price, price, price, price, 1m);

    static DateTimeOffset Minute(DateTimeOffset at) =>
        new(at.UtcTicks - at.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero);

    static PlaceIntent Buy(string symbol = "BTCUSDT") =>
        new(symbol, OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, null);

    sealed record PaperRig(TradingGateway Gw, PaperConnector Paper, MemoryBarSource Source, TestClock Clock,
        HealthRegistry Health, Database Db) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Gw.DisposeAsync();
            await Paper.DisposeAsync();
            Db.Dispose();
        }
    }

    /// <summary>The paper connector behind the real gateway, both on one injected clock.</summary>
    static async Task<PaperRig> PaperReady(DateTimeOffset at)
    {
        var clock = new TestClock(at);
        var source = new MemoryBarSource();
        var paper = new PaperConnector(new PaperConnectorOptions
        {
            Source = source,
            Clock = () => clock.At,
            BookFile = Path.Combine(TestEnv.Home, $"paper-clock-{Guid.NewGuid():n}.db"),
            Catalogue = Catalogue()
        });

        var health = new HealthRegistry();
        var db = TestEnv.NewDb();
        var gw = new TradingGateway(db, paper, health, new GatewayOptions { Clock = clock });
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = PaperConnector.TheAccount;
            s.Risk.InstrumentAllowlist = ["BTCUSDT"];
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
        });

        await paper.ConnectAsync();
        await gw.RefreshHealthAsync();
        return new PaperRig(gw, paper, source, clock, health, db);
    }

    static async Task<string> SwallowAsync(Task<ExecutionRequest> t)
    {
        try { var r = await t; return $"ok — {r.State}"; }
        catch (GatewayDeniedException ex) { return $"{ex.Code} — {ex.Message}"; }
    }

    // ---------------------------------------------------------------- (d) the stamp

    /// <summary>
    /// THE FIRST BAR AFTER A GAP IS STAMPED AT ITS OWN CLOSE, AND FOUR MINUTES LATER ITS PRICE SIZES
    /// NOTHING.
    ///
    /// <para>A bar, ten minutes with none, then the bar that opened five minutes ago. Stamped
    /// <c>open + spacing of the last two</c> that price is dated five minutes in the FUTURE and passes
    /// every age check there is. Stamped at its close it is four minutes old, and refused. The
    /// gateway's clock is anchored on the machine's minute so that the build before this unit, which
    /// measured on the machine clock, is shown the same instant.</para>
    /// </summary>
    [Fact]
    public async Task A_quote_after_a_bar_gap_is_stamped_at_its_close_and_a_stale_price_is_refused()
    {
        var now = Minute(DateTimeOffset.UtcNow);
        var closed = now.AddMinutes(-4);
        await using var rig = await PaperReady(closed.AddSeconds(5));

        rig.Source.Add("BTCUSDT", Flat(now.AddMinutes(-15), 100m));
        rig.Source.Add("BTCUSDT", Flat(now.AddMinutes(-5), 101m));    // ten minutes after the last one

        // FIVE SECONDS AFTER IT CLOSED: the quote is that bar's close, dated at that bar's close.
        var quote = await rig.Paper.GetQuoteAsync("BTCUSDT");
        log.WriteLine($"quote {quote?.Last} stamped {quote?.At:O}; the bar closed {closed:O}");
        Assert.Equal(101m, quote!.Last);
        Assert.Equal(closed, quote.At);

        var fresh = await SwallowAsync(rig.Gw.PlaceAsync(new AgentContext("a"), "gap-fresh", Buy()));
        log.WriteLine($"5 s after the close  : {fresh}");
        Assert.StartsWith("ok", fresh, StringComparison.Ordinal);

        // FOUR MINUTES AFTER IT CLOSED, WITH NO BAR SINCE: a memory, and nothing is sized from it.
        rig.Clock.At = now;
        var stale = await SwallowAsync(rig.Gw.PlaceAsync(new AgentContext("a"), "gap-stale", Buy()));
        log.WriteLine($"4 min after the close: {stale}");
        Assert.StartsWith(ErrorCode.MARKET_DATA_UNAVAILABLE.ToString(), stale, StringComparison.Ordinal);
        Assert.Null(rig.Gw.GetRequest("gap-stale"));
        Assert.Single(await rig.Paper.GetOrdersAsync(PaperConnector.TheAccount, true, null));
    }

    // ---------------------------------------------------------------- (e) the one clock

    /// <summary>A one-minute bar that closed <paramref name="ago"/> before the clock now reads.</summary>
    static PlaceIntent Decided(TestClock clock, TimeSpan ago, TimeSpan freshness, TimeSpan maxAge)
    {
        var close = clock.At - ago;
        return new PlaceIntent("ES", OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, null)
        {
            Decision = new IntentDecision(close - TimeSpan.FromMinutes(1), close, freshness, maxAge)
        };
    }

    /// <summary>
    /// BOTH QUOTE GATES AND THE DECISION GATE MEASURE AGE ON THE GATEWAY'S CLOCK — the one the
    /// decision gate always read.
    ///
    /// <para>The gateway's clock is thirty days behind the machine's and the broker's quote is five
    /// seconds old on the gateway's. A gate that reads the machine sees thirty days and refuses an
    /// order every other gate passed. Then the clock moves forty seconds INSIDE the dispatch gate's
    /// position read, and the dispatch-time quote gate refuses on the moved clock — after the read,
    /// which is where it sits. And the valuation the loss watch takes of the open position reads
    /// the same clock: five seconds is a mark, not "older than 30s".</para>
    /// </summary>
    [Fact]
    public async Task Both_quote_gates_and_the_decision_gate_read_the_same_clock()
    {
        var behind = TimeSpan.FromDays(30);
        var clock = new TestClock(DateTimeOffset.UtcNow - behind);
        using var db = TestEnv.NewDb();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker()));
        conn.Faults.QuoteAge = behind + TimeSpan.FromSeconds(5);
        var gw = new TradingGateway(db, conn, new HealthRegistry(), new GatewayOptions { Clock = clock });
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments];
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        // ONE: a price five seconds old and a decision ten seconds old, on the one clock — it goes.
        var sent = await SwallowAsync(gw.PlaceAsync(new AgentContext("a"), "clock-fresh",
            Decided(clock, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5))));
        log.WriteLine($"fresh on the gateway's clock : {sent}");
        Assert.StartsWith("ok", sent, StringComparison.Ordinal);
        Assert.Single(conn.Placed);

        // TWO: the decision gate on that clock — a bar six minutes closed is past five.
        var expired = await SwallowAsync(gw.PlaceAsync(new AgentContext("a"), "clock-old-decision",
            Decided(clock, TimeSpan.FromMinutes(6), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5))));
        log.WriteLine($"a decision six minutes old   : {expired}");
        Assert.StartsWith(ErrorCode.DECISION_EXPIRED.ToString(), expired, StringComparison.Ordinal);

        // THREE: the valuation reads it too. A loss budget makes the watch value the open position.
        gw.Update(s => s.Risk.MaxDailyLoss = 1_000_000m);
        var pass = await gw.LossWatchAsync();
        log.WriteLine($"loss watch                   : ran={pass.Ran} why={pass.Why}");
        Assert.DoesNotContain("older than", pass.Why ?? "", StringComparison.Ordinal);
        gw.Update(s => s.Risk.MaxDailyLoss = 0m);

        // FOUR: forty seconds pass inside the position read. The risk check took a price five seconds
        // old; the gate after the read measures it on the same, moved clock — forty-five — and refuses.
        var moved = 0;
        conn.Seam = kind =>
        {
            if (kind == RecordingConnector.HeldCall.Positions && Interlocked.Exchange(ref moved, 1) == 0)
                clock.At += TimeSpan.FromSeconds(40);
            return Task.CompletedTask;
        };
        var late = await SwallowAsync(gw.PlaceAsync(new AgentContext("a"), "clock-late",
            Decided(clock, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5))));
        conn.Seam = null;
        log.WriteLine($"40 s inside the position read: {late}");
        Assert.Equal(1, moved);
        Assert.StartsWith(ErrorCode.MARKET_DATA_UNAVAILABLE.ToString(), late, StringComparison.Ordinal);
        Assert.Null(gw.GetRequest("clock-late"));
        Assert.Single(conn.Placed);

        await gw.DisposeAsync();
    }
}

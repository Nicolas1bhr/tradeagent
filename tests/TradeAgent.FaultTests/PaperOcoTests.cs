using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Paper;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// TWO RUNS ON ONE PAIR SHARE THE PAPER ACCOUNT'S ONE HOLDING, AND EACH RUN'S STOP AND TARGET ARE ITS OWN (<c>U-paper-oco</c>, R31 A3-1).
///
/// <para>The paper account carries one position per symbol: the sum of every run on it. A run's stop and target are two resting
/// sells of its whole holding, and the runner cancelled the loser only on the live minute after the winner's fill landed. When
/// several minutes settle in one read — a restart, a collector backing off — the loser's level could be reached first, and its
/// sell was covered by ANOTHER run's units, so it filled on that run's holding: one run's book sold twice, the other's read long
/// over units the account no longer held. These hold the book to three things: a run's resting sells are one-cancels-other, in
/// the fill's own transaction; a run's sell only ever fills against what that run holds; and a stop firing for one run does not
/// suppress another run's touched target on the same bar.</para>
///
/// <para>The run is read off the client order id the gateway sends (<see cref="Deployments.HolderOf"/>): <c>TA-</c> before
/// the run's request id.</para>
/// </summary>
public class PaperOcoTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset T0 = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);

    const string RunA = "aaaaaaaaaaaa0000000000000000000000000000000000000000000000000000";
    const string RunB = "bbbbbbbbbbbb0000000000000000000000000000000000000000000000000000";

    /// <summary>A run's client order id exactly as the gateway mints it: <c>TA-</c> and the run's request id for that minute.</summary>
    static string Id(string run, int minute, int seq) =>
        TradeAgent.Gateway.TradingGateway.ClientOrderIdFor(Deployments.RequestIdFor(run, T0.AddMinutes(minute), seq));

    static string NewFile() => Path.Combine(TestEnv.Home, $"paper-oco-{Guid.NewGuid():n}.db");

    static KlineBar Bar(int minute, decimal o, decimal h, decimal l, decimal c) => new(T0.AddMinutes(minute), o, h, l, c, 1m);

    /// <summary>
    /// A LEDGER THAT RAISES NOTHING: only a read settles, so every bar added before a read settles IN THAT ONE READ, in bar order —
    /// the batch a restart or a collector's backoff hands the book.
    /// </summary>
    sealed class QuietSource : IPaperBarSource
    {
        readonly List<KlineBar> _bars = [];
        public TimeSpan BarLength => ForwardBars.BarLength;
        public event Action<ClosedBar>? BarClosed { add { } remove { } }
        public void Add(params KlineBar[] bars) => _bars.AddRange(bars);
        public Task<IReadOnlyList<KlineBar>> SinceAsync(string symbol, DateTimeOffset from, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KlineBar>>([.. _bars.Where(b => b.OpenTime > from).OrderBy(b => b.OpenTime)]);
    }

    static VenueCatalogRead Catalogue() => new(
    [
        new VenueEntry
        {
            Id = VenueCatalog.BinanceSpot, DisplayName = "Binance spot", CalendarKind = CalendarKind.Continuous,
            Source = "declared by this test", Verified = true,
            Instruments =
            [
                new VenueInstrumentEntry
                {
                    Symbol = "BTCUSDT", TickSize = 0.01m, QuantityIncrement = 0.001m, Source = "declared by this test", Verified = true
                }
            ]
        }
    ], null);

    static PlaceOrderCommand Order(string coid, OrderSide side, OrderType type, decimal? limit = null, decimal? stop = null) =>
        new(coid, PaperConnector.TheAccount, "BTCUSDT", side, type, 1m, limit, stop, TimeInForce.Day, null)
        {
            Intent = side == OrderSide.Sell ? OrderIntent.Close : OrderIntent.Open
        };

    /// <summary>
    /// THE TWO RUNS' ENTRIES: one unit each, placed after the bar at minute 0 closed and filled at minute 1's open — the account
    /// then holds two, one for each run. The clock is left at minute 1 and a half, so what is placed next rests from there.
    /// </summary>
    static async Task<(PaperConnector Paper, QuietSource Source, Func<DateTimeOffset> Clock)> TwoRunsHoldingOneEachAsync()
    {
        var source = new QuietSource();
        var now = T0.AddSeconds(30);
        var paper = new PaperConnector(new PaperConnectorOptions
        {
            Source = source, Clock = () => now, BookFile = NewFile(), Catalogue = Catalogue(), Friction = () => PaperFriction.None
        });
        await paper.ConnectAsync();

        source.Add(Bar(0, 100m, 101m, 99m, 100m));
        await paper.PlaceOrderAsync(Order(Id(RunA, 0, 0), OrderSide.Buy, OrderType.Market));
        await paper.PlaceOrderAsync(Order(Id(RunB, 0, 0), OrderSide.Buy, OrderType.Market));
        source.Add(Bar(1, 100m, 101m, 99m, 100m));
        Assert.Equal(2m, Assert.Single(await paper.GetPositionsAsync(PaperConnector.TheAccount)).Quantity);

        now = T0.AddMinutes(1).AddSeconds(30);
        return (paper, source, () => now);
    }

    async Task<(decimal Held, IReadOnlyList<OrderInfo> Orders, IReadOnlyList<ExecutionInfo> Fills)> ReadAsync(PaperConnector paper)
    {
        var held = (await paper.GetPositionsAsync(PaperConnector.TheAccount)).Sum(p => p.Quantity);
        var orders = await paper.GetOrdersAsync(PaperConnector.TheAccount, true, null);
        var fills = await paper.GetExecutionsAsync(PaperConnector.TheAccount, null);
        foreach (var o in orders) log.WriteLine($"{o.ClientOrderId} {o.Side} {o.Type} {o.State} filled={o.FilledQuantity} {o.RejectReason}");
        foreach (var f in fills) log.WriteLine($"fill {f.ClientOrderId} {f.Side} {f.Quantity} @ {f.Price}");
        log.WriteLine($"the account holds {held}");
        return (held, orders, fills);
    }

    /// <summary>
    /// A RUN'S STOP AND TARGET ARE ONE-CANCELS-OTHER WHEN MINUTES SETTLE IN A BATCH: run A's stop is reached on the first minute of
    /// the batch and its target on the next. The stop fills; the target is CANCELLED in the stop's own fill, so it never reaches the
    /// second minute; the account still holds run B's unit, and run B's stop and target still rest over it. A second read of the
    /// same minutes changes nothing.
    ///
    /// <para><b>RED on the base</b>: both of run A's sells filled — the target on run B's unit — and the account held 0.</para>
    /// </summary>
    [Fact]
    public async Task A_runs_target_is_cancelled_by_its_own_stops_fill_when_minutes_settle_in_one_read()
    {
        var (paper, source, _) = await TwoRunsHoldingOneEachAsync();
        await using var _p = paper;

        await paper.PlaceOrderAsync(Order(Id(RunA, 1, 1), OrderSide.Sell, OrderType.Stop, stop: 95m));
        await paper.PlaceOrderAsync(Order(Id(RunA, 1, 2), OrderSide.Sell, OrderType.Limit, limit: 110m));
        await paper.PlaceOrderAsync(Order(Id(RunB, 1, 1), OrderSide.Sell, OrderType.Stop, stop: 80m));
        await paper.PlaceOrderAsync(Order(Id(RunB, 1, 2), OrderSide.Sell, OrderType.Limit, limit: 130m));

        // THE BATCH: minute 2 falls through run A's stop, minute 3 rises through its target. Nothing reads in between.
        source.Add(Bar(2, 99m, 99m, 94m, 96m), Bar(3, 96m, 112m, 96m, 111m));

        var (held, orders, fills) = await ReadAsync(paper);

        Assert.Equal(1m, held);
        var sold = Assert.Single(fills, f => f.Side == OrderSide.Sell);
        Assert.Equal((Id(RunA, 1, 1), 95m), (sold.ClientOrderId, sold.Price));
        Assert.Equal(ExecutionState.CANCELLED, Assert.Single(orders, o => o.ClientOrderId == Id(RunA, 1, 2)).State);
        Assert.Equal(ExecutionState.WORKING, Assert.Single(orders, o => o.ClientOrderId == Id(RunB, 1, 1)).State);
        Assert.Equal(ExecutionState.WORKING, Assert.Single(orders, o => o.ClientOrderId == Id(RunB, 1, 2)).State);

        // AND A SECOND READ OF THE SAME MINUTES CHANGES NOTHING.
        var (again, _, fillsAgain) = await ReadAsync(paper);
        Assert.Equal((1m, 3), (again, fillsAgain.Count));
    }

    /// <summary>
    /// A RUN'S SELL ONLY EVER FILLS AGAINST WHAT THAT RUN HOLDS: run A sold its unit at market and its target was left resting (the
    /// runner's cancel never came). When the target's level is reached the account still holds run B's unit — and the target is
    /// REJECTED in words, unfilled, rather than filled on run B's holding.
    ///
    /// <para><b>RED on the base</b>: the target filled, on run B's unit, and the account held 0.</para>
    /// </summary>
    [Fact]
    public async Task A_runs_orphaned_target_is_rejected_rather_than_filled_on_another_runs_holding()
    {
        var (paper, source, _) = await TwoRunsHoldingOneEachAsync();
        await using var _p = paper;

        await paper.PlaceOrderAsync(Order(Id(RunA, 1, 2), OrderSide.Sell, OrderType.Limit, limit: 110m));
        await paper.PlaceOrderAsync(Order(Id(RunA, 1, 3), OrderSide.Sell, OrderType.Market));

        // Minute 2: the market exit fills at the open. Minute 3: the orphaned target's level is reached.
        source.Add(Bar(2, 100m, 101m, 99m, 100m), Bar(3, 100m, 112m, 100m, 111m));

        var (held, orders, fills) = await ReadAsync(paper);

        Assert.Equal(1m, held);
        Assert.Equal(Id(RunA, 1, 3), Assert.Single(fills, f => f.Side == OrderSide.Sell).ClientOrderId);
        var target = Assert.Single(orders, o => o.ClientOrderId == Id(RunA, 1, 2));
        Assert.Equal((ExecutionState.REJECTED, 0m), (target.State, target.FilledQuantity));
        Assert.Contains("insufficient holdings", target.RejectReason, StringComparison.Ordinal);
        Assert.Contains("the run that placed it", target.RejectReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A RUN'S SELL NEVER REACHES ANOTHER RUN'S UNITS EVEN AFTER A SELL NO RUN SENT (the opus review of this unit): the account's own market
    /// sell of one unit — the owner's hand — leaves one unit, which run B still counts as its own. Run A's stop is then reached: it is
    /// REJECTED, unfilled, and run B's unit stays. Whose unit the owner sold the book cannot say; what it can say is that run A's sell
    /// would take what run B holds.
    ///
    /// <para><b>RED on the unit's first cut</b> (the run's own fills alone): run A's stop filled on the one unit left, and the account held 0.</para>
    /// </summary>
    [Fact]
    public async Task After_a_sell_no_run_sent_a_runs_stop_does_not_reach_another_runs_unit()
    {
        var (paper, source, _) = await TwoRunsHoldingOneEachAsync();
        await using var _p = paper;

        await paper.PlaceOrderAsync(Order(Id(RunA, 1, 1), OrderSide.Sell, OrderType.Stop, stop: 95m));
        await paper.PlaceOrderAsync(Order("owner-hand-sell-1", OrderSide.Sell, OrderType.Market));

        // Minute 2: the owner's sell fills at the open. Minute 3: run A's stop is reached.
        source.Add(Bar(2, 100m, 101m, 99m, 100m), Bar(3, 99m, 99m, 94m, 96m));

        var (held, orders, fills) = await ReadAsync(paper);

        Assert.Equal(1m, held);
        Assert.Equal("owner-hand-sell-1", Assert.Single(fills, f => f.Side == OrderSide.Sell).ClientOrderId);
        var stop = Assert.Single(orders, o => o.ClientOrderId == Id(RunA, 1, 1));
        Assert.Equal((ExecutionState.REJECTED, 0m), (stop.State, stop.FilledQuantity));
        Assert.Contains("other runs count 1", stop.RejectReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A STOP FIRING FOR ONE RUN DOES NOT SUPPRESS ANOTHER RUN'S TOUCHED TARGET ON THE SAME BAR. "A bar that touched both a
    /// position's stop and its target counts as the stop" is about ONE position: run A's stop at 95 and run B's target at 105 are
    /// both reached by a bar from 94 to 106, and both fill.
    ///
    /// <para><b>RED on the base</b>: run B's target was held back by run A's stop, every such bar.</para>
    /// </summary>
    [Fact]
    public async Task One_runs_stop_on_a_bar_does_not_hold_back_another_runs_touched_target()
    {
        var (paper, source, _) = await TwoRunsHoldingOneEachAsync();
        await using var _p = paper;

        await paper.PlaceOrderAsync(Order(Id(RunA, 1, 1), OrderSide.Sell, OrderType.Stop, stop: 95m));
        await paper.PlaceOrderAsync(Order(Id(RunB, 1, 2), OrderSide.Sell, OrderType.Limit, limit: 105m));

        source.Add(Bar(2, 100m, 106m, 94m, 100m));

        var (held, _, fills) = await ReadAsync(paper);

        Assert.Equal(0m, held);
        var sells = fills.Where(f => f.Side == OrderSide.Sell).Select(f => (f.ClientOrderId!, f.Price)).ToHashSet();
        Assert.Equal(new[] { (Id(RunA, 1, 1), 95m), (Id(RunB, 1, 2), 105m) }, sells.OrderBy(s => s.Price));
    }

    /// <summary>
    /// AND ONE POSITION'S STOP AND TARGET ON ONE BAR STILL COUNT AS THE STOP, for a run as for the account's own: run A's stop and
    /// target are both reached by one bar; the stop fills and the target is cancelled with it.
    /// </summary>
    [Fact]
    public async Task One_runs_stop_and_target_on_one_bar_is_still_the_stop()
    {
        var (paper, source, _) = await TwoRunsHoldingOneEachAsync();
        await using var _p = paper;

        await paper.PlaceOrderAsync(Order(Id(RunA, 1, 1), OrderSide.Sell, OrderType.Stop, stop: 95m));
        await paper.PlaceOrderAsync(Order(Id(RunA, 1, 2), OrderSide.Sell, OrderType.Limit, limit: 105m));

        source.Add(Bar(2, 100m, 106m, 94m, 100m));

        var (held, orders, fills) = await ReadAsync(paper);

        Assert.Equal(1m, held);
        Assert.Equal(Id(RunA, 1, 1), Assert.Single(fills, f => f.Side == OrderSide.Sell).ClientOrderId);
        Assert.Equal(ExecutionState.CANCELLED, Assert.Single(orders, o => o.ClientOrderId == Id(RunA, 1, 2)).State);
    }

    /// <summary>
    /// WHOSE AN ORDER IS, OFF ITS ID: a run's client order id names the run, exactly as the gateway mints it; anything else —
    /// another shape, an upper-case id, a prefix alone, an agent's or the owner's id — is the account's own.
    /// </summary>
    [Fact]
    public void A_run_is_read_off_the_gateways_client_order_id_and_nothing_else_reads_as_one()
    {
        Assert.Equal(Deployments.HolderFor(RunA), Deployments.HolderOf(Id(RunA, 7, 3)));
        Assert.Equal("dp-aaaaaaaaaaaa", Deployments.HolderOf(Id(RunA, 7, 3)));
        foreach (var other in new[]
                 {
                     null, "", "TA-dp-", "TA-dp-aaaaaaaaaaaa", "TA-dp-aaaaaaaaaaaa-1", "TA-dp-aaaaaaaaaaaa-1-", "TA-dp-AAAAAAAAAAAA-1-0",
                     "TA-dp-aaaaaaaaaaa-1-0", "TA-dp-aaaaaaaaaaaa-1-0-x", "dp-aaaaaaaaaaaa-1-0", "TA-op-close-1", "hold-1"
                 })
            Assert.Null(Deployments.HolderOf(other));
    }
}

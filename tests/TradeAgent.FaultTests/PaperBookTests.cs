using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Paper;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// THE BOOK: ITS OWN FILE, PERSISTENT, IDEMPOTENT, AND REACHING BACK AS FAR AS IT IS ASKED.
///
/// <para>A second instance over the same file answers the same book; a bar served twice writes no
/// second fill, and the thing that makes that true is <c>UNIQUE (client_order_id, bar_open_time)</c>
/// rather than a habit; the client order id round-trips onto the order, onto the execution and out of
/// the file again; and a window asked for from a year ago really reaches back a year.</para>
/// </summary>
public class PaperBookTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset T0 = new(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);

    static string NewFile() => Path.Combine(TestEnv.Home, $"paper-{Guid.NewGuid():n}.db");

    static KlineBar Bar(DateTimeOffset open, decimal o, decimal h, decimal l, decimal c) =>
        new(open, o, h, l, c, 1m);

    /// <summary>
    /// A catalogue with ONE verified spot row. The shipped catalogue has none — Binance spot's
    /// BTCUSDT is <c>verified = false</c> — so a test that wants to trade declares the row the way an
    /// account owner does, in the catalogue, rather than by widening the connector.
    /// </summary>
    static VenueCatalogRead Catalogue(decimal increment = 0.001m) => new(
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
                    Symbol = "BTCUSDT", TickSize = 0.01m, QuantityIncrement = increment,
                    Source = "declared by this test", Verified = true
                }
            ]
        }
    ], null);

    static PaperConnector Paper(MemoryBarSource source, Func<DateTimeOffset> clock, string file,
        PaperFriction? friction = null) =>
        new(new PaperConnectorOptions
        {
            Source = source,
            Clock = clock,
            BookFile = file,
            Catalogue = Catalogue(),
            Friction = () => friction ?? PaperFriction.None
        });

    static PlaceOrderCommand Market(string coid, OrderSide side = OrderSide.Buy, decimal qty = 1m) =>
        new(coid, PaperConnector.TheAccount, "BTCUSDT", side, OrderType.Market, qty, null, null, TimeInForce.Day, null);

    /// <summary>
    /// A NEW INSTANCE OVER THE SAME FILE READS THE SAME BOOK, AND RE-PROCESSING A BAR WRITES NO
    /// SECOND FILL.
    ///
    /// <para>The second half is the one with teeth. The replaying source hands the connector the bar
    /// it has already settled — which is what a ledger restarted from an older watermark does — and
    /// the only thing between that and a duplicated position is
    /// <c>UNIQUE (client_order_id, bar_open_time)</c> on the fills. Drop it and this test goes red.</para>
    /// </summary>
    [Fact]
    public async Task A_new_instance_over_the_same_file_reads_the_same_book_and_re_processing_a_bar_writes_no_second_fill()
    {
        var file = NewFile();
        var first = new MemoryBarSource();

        await using (var paper = Paper(first, () => T0.AddSeconds(30), file))
        {
            await paper.ConnectAsync();
            first.Add("BTCUSDT", Bar(T0, 100m, 106m, 99m, 105m));
            await paper.PlaceOrderAsync(Market("c-1", qty: 2m));
            first.Add("BTCUSDT", Bar(T0.AddMinutes(1), 110m, 112m, 109m, 111m));
            Assert.Single(await paper.GetExecutionsAsync(PaperConnector.TheAccount, null));
        }

        // A second process over the same file, and a source whose own watermark is behind: it serves
        // both bars again, including the one that already filled the order.
        var replaying = new MemoryBarSource { ServeFromTheStart = true };
        replaying.Add("BTCUSDT", Bar(T0, 100m, 106m, 99m, 105m));
        replaying.Add("BTCUSDT", Bar(T0.AddMinutes(1), 110m, 112m, 109m, 111m));

        await using var reopened = Paper(replaying, () => T0.AddMinutes(5), file);
        await reopened.ConnectAsync();

        var orders = await reopened.GetOrdersAsync(PaperConnector.TheAccount, true, null);
        var order = Assert.Single(orders);
        Assert.Equal("c-1", order.ClientOrderId);
        Assert.Equal(ExecutionState.FILLED, order.State);

        var fills = await reopened.GetExecutionsAsync(PaperConnector.TheAccount, null);
        log.WriteLine($"after a replay of every bar: {fills.Count} fill(s)");
        Assert.Single(fills);

        var position = Assert.Single(await reopened.GetPositionsAsync(PaperConnector.TheAccount));
        Assert.Equal(2m, position.Quantity);
        Assert.Equal(110m, position.AveragePrice);
    }

    /// <summary>
    /// ORDERS AND EXECUTIONS SINCE A TIMESTAMP REACH BACK TO IT — really back to it, which is what
    /// <c>SupportsOrderHistory = true</c> is entitled to mean and the only thing that entitles this
    /// connector to say it. A partial history is worse than none: it makes "this order does not
    /// exist" look provable when it is not.
    /// </summary>
    [Fact]
    public async Task Orders_and_executions_since_a_timestamp_reach_back_to_it()
    {
        var source = new MemoryBarSource();
        var now = T0.AddSeconds(30);
        await using var paper = Paper(source, () => now, NewFile());
        await paper.ConnectAsync();

        source.Add("BTCUSDT", Bar(T0, 100m, 101m, 99m, 100m));
        await paper.PlaceOrderAsync(Market("d-1"));
        source.Add("BTCUSDT", Bar(T0.AddMinutes(1), 110m, 112m, 109m, 111m));
        await paper.GetExecutionsAsync(PaperConnector.TheAccount, null);

        now = T0.AddMinutes(90);
        await paper.PlaceOrderAsync(Market("d-2"));
        source.Add("BTCUSDT", Bar(T0.AddMinutes(91), 120m, 122m, 119m, 121m));
        await paper.GetExecutionsAsync(PaperConnector.TheAccount, null);

        // A YEAR before the first order, which is the point: nothing is trimmed and no window is
        // silently substituted for the one that was asked for.
        var since = T0.AddYears(-1);
        var allOrders = await paper.GetOrdersAsync(PaperConnector.TheAccount, true, since);
        var allFills = await paper.GetExecutionsAsync(PaperConnector.TheAccount, since);
        log.WriteLine($"since {since:O}: {allOrders.Count} order(s), {allFills.Count} fill(s)");
        Assert.Equal(2, allOrders.Count);
        Assert.Equal(2, allFills.Count);
        Assert.Contains(allOrders, o => o.ClientOrderId == "d-1");
        Assert.Contains(allFills, f => f.ClientOrderId == "d-1");

        // And the bound really bounds: asked from the second order onwards, the first is not served.
        var recent = await paper.GetOrdersAsync(PaperConnector.TheAccount, true, T0.AddMinutes(60));
        Assert.Equal("d-2", Assert.Single(recent).ClientOrderId);
    }

    /// <summary>
    /// THE CLIENT ORDER ID ROUND-TRIPS — onto the order, back off it, onto the execution, and out of
    /// the file a second instance opens. Rule 1 on <c>IAtasAdapter</c>, and
    /// <c>SupportsClientOrderId = true</c> is not allowed to mean anything less.
    /// </summary>
    [Fact]
    public async Task The_client_order_id_round_trips()
    {
        var file = NewFile();
        var source = new MemoryBarSource();
        const string coid = "TA-paper-round-trip-1";

        await using (var paper = Paper(source, () => T0.AddSeconds(30), file))
        {
            await paper.ConnectAsync();
            source.Add("BTCUSDT", Bar(T0, 100m, 101m, 99m, 100m));

            var placed = await paper.PlaceOrderAsync(Market(coid));
            Assert.Equal(coid, placed.ClientOrderId);
            Assert.Equal(coid, Assert.Single(await paper.GetOrdersAsync(PaperConnector.TheAccount, true, null)).ClientOrderId);

            source.Add("BTCUSDT", Bar(T0.AddMinutes(1), 110m, 112m, 109m, 111m));
            Assert.Equal(coid, Assert.Single(await paper.GetExecutionsAsync(PaperConnector.TheAccount, null)).ClientOrderId);

            // The same id twice is ONE order, because the id is the book's primary key. That is what
            // it is for: after a lost acknowledgement the caller retries with the same id.
            var again = await paper.PlaceOrderAsync(Market(coid));
            Assert.Equal(placed.ConnectorOrderId, again.ConnectorOrderId);
            Assert.Single(await paper.GetOrdersAsync(PaperConnector.TheAccount, true, null));
        }

        await using var reopened = Paper(new MemoryBarSource(), () => T0.AddMinutes(5), file);
        await reopened.ConnectAsync();
        Assert.Equal(coid, Assert.Single(await reopened.GetOrdersAsync(PaperConnector.TheAccount, true, null)).ClientOrderId);
        Assert.Equal(coid, Assert.Single(await reopened.GetExecutionsAsync(PaperConnector.TheAccount, null)).ClientOrderId);
    }
}

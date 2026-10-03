using Microsoft.Data.Sqlite;
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

    /// <summary>
    /// A FAILURE BETWEEN THE WATERMARK AND THE CLOSE LEAVES BOTH AS THEY WERE (<c>U-paper-settle</c>
    /// item 2).
    ///
    /// <para>They were two commits. A process killed between them kept bar N's watermark beside bar
    /// N−1's close, and the quote is that close stamped at bar N's close — a stale price that reads
    /// fresh. The fault here is at the second write and nowhere else: a trigger on the book's own
    /// file refuses the close row, so no seam had to be added to the book to reach it. Then the
    /// trigger goes and the same bar settles, which shows nothing was left half-open behind it.</para>
    /// </summary>
    [Fact]
    public void A_failure_between_the_watermark_and_the_close_leaves_both_as_they_were()
    {
        var file = NewFile();
        using var book = new PaperBook(file, PaperConnector.TheAccount, "USDT", 10_000m);
        book.MarkSettled("BTCUSDT", T0, 100m);

        Raw(file, """
            CREATE TRIGGER fault_close_insert BEFORE INSERT ON paper_meta WHEN NEW.key LIKE 'close:%'
            BEGIN SELECT RAISE(ABORT, 'the close was not written (injected by this test)'); END;
            CREATE TRIGGER fault_close_update BEFORE UPDATE ON paper_meta WHEN NEW.key LIKE 'close:%'
            BEGIN SELECT RAISE(ABORT, 'the close was not written (injected by this test)'); END;
            """);

        var thrown = Assert.Throws<SqliteException>(() => book.MarkSettled("BTCUSDT", T0.AddMinutes(1), 111m));
        log.WriteLine($"{thrown.Message}; watermark {book.SettledThrough("BTCUSDT"):HH:mm}, close {book.LastClose("BTCUSDT")}");
        Assert.Equal(T0, book.SettledThrough("BTCUSDT"));
        Assert.Equal(100m, book.LastClose("BTCUSDT"));

        Raw(file, "DROP TRIGGER fault_close_insert; DROP TRIGGER fault_close_update;");
        book.MarkSettled("BTCUSDT", T0.AddMinutes(1), 111m);
        Assert.Equal(T0.AddMinutes(1), book.SettledThrough("BTCUSDT"));
        Assert.Equal(111m, book.LastClose("BTCUSDT"));
    }

    /// <summary>A statement run on the book's file over a connection of the test's own.</summary>
    static void Raw(string file, string sql)
    {
        using var raw = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Pooling = false
        }.ToString());
        raw.Open();
        using var c = raw.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }
}

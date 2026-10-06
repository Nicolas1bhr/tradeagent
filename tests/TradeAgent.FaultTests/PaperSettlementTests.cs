using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Paper;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// SETTLEMENT PER CLOSED BAR, IN THE BACKTEST'S ORDER (<c>docs/CONTRACTS.md</c>, "The backtest").
///
/// <para>A market order fills at the next closed bar's OPEN plus adverse slippage and never at a bar
/// that had already closed; a stop gapped through fills at the open and a target at its level; a bar
/// that touched both counts as the stop; a size is rounded DOWN to the increment and a size below it
/// is a definite refusal; a fill with nothing declared says it was FRICTIONLESS.</para>
/// </summary>
public class PaperSettlementTests(ITestOutputHelper log)
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

    static PaperConnector Paper(IPaperBarSource source, Func<DateTimeOffset> clock, string file,
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
    /// A MARKET ORDER FILLS AT THE NEXT CLOSED BAR'S OPEN, PLUS ADVERSE SLIPPAGE — and never at the
    /// bar that had already closed when it was placed.
    ///
    /// <para>That is the backtest's rule and the reason for it is the same: a decision taken while a
    /// bar was closing could not have been acted on inside that bar, and a fill that used its close
    /// would be a price the order could not have had. The three numbers here are deliberately all
    /// different — the closed bar opened at 100 and closed at 105, the next one opens at 110 — so
    /// that filling at either of the first two is a different figure and not a rounding.</para>
    /// </summary>
    [Fact]
    public async Task A_market_order_fills_at_the_next_closed_bars_open_plus_slippage_never_at_the_bar_already_closed()
    {
        var source = new MemoryBarSource();
        var placedAt = T0.AddSeconds(30);
        await using var paper = Paper(source, () => placedAt, NewFile(), new PaperFriction(0m, 0.001m));
        await paper.ConnectAsync();

        source.Add("BTCUSDT", Bar(T0, 100m, 106m, 99m, 105m));

        var order = await paper.PlaceOrderAsync(Market("a-1"));
        Assert.Equal(ExecutionState.WORKING, order.State);
        Assert.Empty(await paper.GetExecutionsAsync(PaperConnector.TheAccount, null));

        source.Add("BTCUSDT", Bar(T0.AddMinutes(1), 110m, 112m, 109m, 111m));

        var fill = Assert.Single(await paper.GetExecutionsAsync(PaperConnector.TheAccount, null));
        log.WriteLine($"filled at {fill.Price}");
        Assert.Equal(110.110m, fill.Price);
        Assert.NotEqual(105m, fill.Price);
        Assert.NotEqual(100m, fill.Price);
        Assert.Equal(ExecutionState.FILLED,
            Assert.Single(await paper.GetOrdersAsync(PaperConnector.TheAccount, true, null)).State);
    }

    /// <summary>
    /// A STOP FILLS AT THE OPEN WHEN THE BAR GAPPED THROUGH IT, A TARGET FILLS AT ITS LEVEL, AND A
    /// BAR THAT TOUCHED BOTH COUNTS AS THE STOP.
    ///
    /// <para><c>docs/CONTRACTS.md</c>, "The backtest": bars carry no intrabar ordering, so the other
    /// reading of a bar that reached both levels invents a winning trade. The gap-through is the
    /// same judgement pointing the other way — a stop at 90 on a bar that opened at 85 did not get
    /// 90, and pretending it did is a loss the simulation never records.</para>
    /// </summary>
    [Fact]
    public async Task A_stop_fills_at_the_open_on_a_gap_a_target_at_its_level_and_both_touched_is_the_stop()
    {
        // The target alone: a bar that never reached the stop — over the one unit each of them protects, bought first,
        // because a sell of what the book does not hold is refused (U-runner-exit-hygiene-b).
        var quiet = new MemoryBarSource();
        await using var one = Paper(quiet, () => T0.AddSeconds(30), NewFile());
        await one.ConnectAsync();
        await HoldOneAsync(one, quiet);
        await one.PlaceOrderAsync(new PlaceOrderCommand("b-target", PaperConnector.TheAccount, "BTCUSDT",
            OrderSide.Sell, OrderType.Limit, 1m, 110m, null, TimeInForce.Day, null));
        await one.PlaceOrderAsync(new PlaceOrderCommand("b-stop-untouched", PaperConnector.TheAccount, "BTCUSDT",
            OrderSide.Sell, OrderType.Stop, 1m, null, 90m, TimeInForce.Day, null));
        quiet.Add("BTCUSDT", Bar(T0.AddMinutes(2), 100m, 115m, 95m, 112m));

        var target = Assert.Single(await Sells(one));
        log.WriteLine($"target filled at {target.Price} on order {target.ClientOrderId}");
        Assert.Equal("b-target", target.ClientOrderId);
        Assert.Equal(110m, target.Price);

        // The gap, and both levels touched in the same bar.
        var violent = new MemoryBarSource();
        await using var two = Paper(violent, () => T0.AddSeconds(30), NewFile());
        await two.ConnectAsync();
        await HoldOneAsync(two, violent);
        await two.PlaceOrderAsync(new PlaceOrderCommand("b-both-stop", PaperConnector.TheAccount, "BTCUSDT",
            OrderSide.Sell, OrderType.Stop, 1m, null, 90m, TimeInForce.Day, null));
        await two.PlaceOrderAsync(new PlaceOrderCommand("b-both-target", PaperConnector.TheAccount, "BTCUSDT",
            OrderSide.Sell, OrderType.Limit, 1m, 110m, null, TimeInForce.Day, null));
        violent.Add("BTCUSDT", Bar(T0.AddMinutes(2), 85m, 115m, 84m, 100m));

        var fill = Assert.Single(await Sells(two));
        log.WriteLine($"both touched: filled {fill.ClientOrderId} at {fill.Price}");
        Assert.Equal("b-both-stop", fill.ClientOrderId);
        Assert.Equal(85m, fill.Price);
    }

    /// <summary>
    /// ONE UNIT HELD, BOUGHT AT MARKET: placed after the bar at T0 closed and filled at the next bar's open, T0 + 1
    /// minute, a bar that reaches no level any test here rests at.
    /// </summary>
    static async Task HoldOneAsync(PaperConnector paper, MemoryBarSource source)
    {
        source.Add("BTCUSDT", Bar(T0, 100m, 101m, 99m, 100m));
        await paper.PlaceOrderAsync(Market("hold-1"));
        source.Add("BTCUSDT", Bar(T0.AddMinutes(1), 100m, 101m, 99m, 100m));
        Assert.Equal(1m, Assert.Single(await paper.GetPositionsAsync(PaperConnector.TheAccount)).Quantity);
    }

    /// <summary>The book's sell executions, oldest first.</summary>
    static async Task<List<ExecutionInfo>> Sells(PaperConnector paper) =>
        [.. (await paper.GetExecutionsAsync(PaperConnector.TheAccount, null)).Where(x => x.Side == OrderSide.Sell)];

    /// <summary>
    /// A SELL BEYOND THE HOLDING IS REFUSED, AND THE PAPER BOOK NEVER GOES SHORT (<c>U-runner-exit-hygiene-b</c>).
    ///
    /// <para>The prices are a spot venue's, and a spot account cannot sell what it does not hold. A lone market sell on
    /// a flat book is a no the book can prove, at placement. Two sells resting on one holding are each covered when
    /// placed — the book reserves nothing, as a spot venue's OCO would — and the bar that reaches both fills the first,
    /// the market sell at its open, and REJECTS the second, unfilled, because by then nothing is held: the book used to
    /// fill it too and hold minus one.</para>
    ///
    /// <para><b>RED on the base</b>: the lone sell accepted; the pair both filled, the position −1.000. <b>Mutant
    /// (iv)</b> — the fill-time check removed, placement's kept — goes red here at −1.000.</para>
    /// </summary>
    [Fact]
    public async Task A_sell_beyond_the_holding_is_refused_and_the_paper_book_never_goes_short()
    {
        var source = new MemoryBarSource();
        var now = T0.AddSeconds(30);
        await using var paper = Paper(source, () => now, NewFile());
        await paper.ConnectAsync();
        source.Add("BTCUSDT", Bar(T0, 100m, 101m, 99m, 100m));

        // A LONE MARKET SELL ON A FLAT BOOK: refused, definite, nothing written.
        var refused = await Assert.ThrowsAsync<ConnectorRejectedException>(
            () => paper.PlaceOrderAsync(Market("s-alone", OrderSide.Sell)));
        log.WriteLine(refused.Message);
        Assert.Contains("insufficient holdings", refused.Message, StringComparison.Ordinal);
        Assert.Null((await paper.GetOrdersAsync(PaperConnector.TheAccount, true, null))
            .FirstOrDefault(o => o.ClientOrderId == "s-alone"));

        // BOUGHT 1, FILLED AT T0 + 1'S OPEN. THEN A MARKET SELL OF 1 AND A SELL STOP OF 1 AT 95, BOTH ACCEPTED.
        await paper.PlaceOrderAsync(Market("s-buy"));
        source.Add("BTCUSDT", Bar(T0.AddMinutes(1), 100m, 101m, 99m, 100m));
        Assert.Equal(1m, Assert.Single(await paper.GetPositionsAsync(PaperConnector.TheAccount)).Quantity);

        now = T0.AddMinutes(1).AddSeconds(30);
        Assert.Equal(ExecutionState.WORKING, (await paper.PlaceOrderAsync(Market("s-sell", OrderSide.Sell))).State);
        Assert.Equal(ExecutionState.WORKING, (await paper.PlaceOrderAsync(new PlaceOrderCommand("s-stop",
            PaperConnector.TheAccount, "BTCUSDT", OrderSide.Sell, OrderType.Stop, 1m, null, 95m, TimeInForce.Day, null))).State);

        // THE NEXT BAR REACHES BOTH: it opens at 96 and falls to 90.
        source.Add("BTCUSDT", Bar(T0.AddMinutes(2), 96m, 97m, 90m, 92m));

        var held = (await paper.GetPositionsAsync(PaperConnector.TheAccount)).Sum(p => p.Quantity);
        var orders = await paper.GetOrdersAsync(PaperConnector.TheAccount, true, null);
        foreach (var o in orders) log.WriteLine($"{o.ClientOrderId} {o.Side} {o.Type} {o.State} filled={o.FilledQuantity} — {o.RejectReason}");
        log.WriteLine($"held {held}");

        Assert.Equal(0m, held);
        var sold = Assert.Single(await Sells(paper));
        Assert.Equal(("s-sell", 96m), (sold.ClientOrderId, sold.Price));
        var stop = Assert.Single(orders, o => o.ClientOrderId == "s-stop");
        Assert.Equal((ExecutionState.REJECTED, 0m), (stop.State, stop.FilledQuantity));
        Assert.Contains("insufficient holdings", stop.RejectReason, StringComparison.Ordinal);

        // AND A SECOND READ OF THE SAME BARS CHANGES NOTHING: the rejection is the order's, once.
        Assert.Equal(2, (await paper.GetExecutionsAsync(PaperConnector.TheAccount, null)).Count);
        Assert.Empty(await paper.GetPositionsAsync(PaperConnector.TheAccount));
    }

    /// <summary>
    /// A SIZE IS ROUNDED DOWN TO THE INCREMENT AND A SIZE BELOW IT IS A DEFINITE REFUSAL — never a
    /// silent zero and never a rounding up. The increment is the catalogue's, which is the one number
    /// <c>docs/CONTRACTS.md</c> refuses to let anything invent.
    /// </summary>
    [Fact]
    public async Task A_size_is_rounded_down_to_the_increment_and_below_it_is_refused()
    {
        var source = new MemoryBarSource();
        await using var paper = Paper(source, () => T0.AddSeconds(30), NewFile());
        await paper.ConnectAsync();
        source.Add("BTCUSDT", Bar(T0, 100m, 101m, 99m, 100m));

        var rounded = await paper.PlaceOrderAsync(Market("r-1", qty: 1.2345m));
        log.WriteLine($"1.2345 at an increment of 0.001 became {rounded.Quantity}");
        Assert.Equal(1.234m, rounded.Quantity);

        var refused = await Assert.ThrowsAsync<ConnectorRejectedException>(
            () => paper.PlaceOrderAsync(Market("r-2", qty: 0.0009m)));
        Assert.Contains("rounds DOWN to nothing", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// FRICTIONLESS IS SAID, NOT LEFT TO BE INFERRED FROM A ZERO. Every fill carries the friction it
    /// was simulated under in words, and so does the connector's status line — because a zero fee
    /// that reads as a measurement of a venue that charges nothing is the figure
    /// <c>docs/CONTRACTS.md</c> will not let a run report.
    /// </summary>
    [Fact]
    public async Task A_fill_with_no_declared_fee_or_slippage_says_it_was_frictionless()
    {
        var source = new MemoryBarSource();
        var file = NewFile();
        await using var paper = Paper(source, () => T0.AddSeconds(30), file);
        await paper.ConnectAsync();
        source.Add("BTCUSDT", Bar(T0, 100m, 101m, 99m, 100m));
        await paper.PlaceOrderAsync(Market("f-1"));
        source.Add("BTCUSDT", Bar(T0.AddMinutes(1), 110m, 112m, 109m, 111m));

        var fill = Assert.Single(await paper.GetExecutionsAsync(PaperConnector.TheAccount, null));
        Assert.Equal(110m, fill.Price);      // no slippage declared, so the open itself
        Assert.Equal(0m, fill.Fee);          // zero because nothing was declared — and it SAYS so:

        using var book = new PaperBook(file, PaperConnector.TheAccount, "USDT", 10_000m);
        var recorded = Assert.Single(book.Fills(null));
        log.WriteLine(recorded.Friction);
        Assert.Contains("FRICTIONLESS", recorded.Friction, StringComparison.Ordinal);
        Assert.Contains("FRICTIONLESS", paper.StatusDetail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// CANCELLING AN ORDER THAT HAS ALREADY FILLED IS A DEFINITE NO, and so is modifying one. Rule 3:
    /// <see cref="ConnectorRejectedException"/> is for a refusal the book can prove and nothing else.
    /// </summary>
    [Fact]
    public async Task Cancelling_a_filled_order_is_a_definite_refusal()
    {
        var source = new MemoryBarSource();
        await using var paper = Paper(source, () => T0.AddSeconds(30), NewFile());
        await paper.ConnectAsync();
        source.Add("BTCUSDT", Bar(T0, 100m, 101m, 99m, 100m));
        var order = await paper.PlaceOrderAsync(Market("x-1"));
        source.Add("BTCUSDT", Bar(T0.AddMinutes(1), 110m, 112m, 109m, 111m));
        await paper.GetExecutionsAsync(PaperConnector.TheAccount, null);

        var refused = await Assert.ThrowsAsync<ConnectorRejectedException>(
            () => paper.CancelOrderAsync(order.ConnectorOrderId));
        log.WriteLine(refused.Message);
        Assert.Contains("FILLED", refused.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- U-paper-settle: past one page

    /// <summary>
    /// A LEDGER THAT ANSWERS A PAGE AT A TIME, the way <c>ForwardBarStore.Since</c> answers ten
    /// thousand bars — here three, so eight bars are more than two pages. It raises no
    /// <see cref="IPaperBarSource.BarClosed"/>, so only a read can settle, and it writes down the
    /// watermark every read was asked from.
    /// </summary>
    sealed class PagedBarSource(int pageSize) : IPaperBarSource
    {
        readonly List<KlineBar> _bars = [];

        /// <summary>The watermark each read was asked from, in the order the reads came.</summary>
        public List<DateTimeOffset> AskedFrom { get; } = [];

        public int Reads => AskedFrom.Count;

        /// <summary>The read, counted from one, that fails as an unreadable ledger does; zero for none.</summary>
        public int FailOnRead { get; set; }

        /// <summary>Run inside every read, after it is counted: how a test acts between two pages.</summary>
        public Action<int>? DuringRead { get; set; }

        public TimeSpan BarLength => ForwardBars.BarLength;

        public event Action<ClosedBar>? BarClosed { add { } remove { } }

        public void Add(params KlineBar[] bars)
        {
            _bars.AddRange(bars);
            _bars.Sort((a, b) => a.OpenTime.CompareTo(b.OpenTime));
        }

        public Task<IReadOnlyList<KlineBar>> SinceAsync(string symbol, DateTimeOffset openTimeExclusive,
            CancellationToken ct = default)
        {
            AskedFrom.Add(openTimeExclusive);
            DuringRead?.Invoke(Reads);
            if (Reads == FailOnRead)
                throw new IOException($"read {Reads} of the ledger failed (injected by this test)");
            IReadOnlyList<KlineBar> page =
                [.. _bars.Where(b => b.OpenTime > openTimeExclusive).Take(pageSize)];
            return Task.FromResult(page);
        }
    }

    /// <summary>Bar <paramref name="n"/> of a numbered series: it opens <paramref name="n"/> minutes after T0.</summary>
    static DateTimeOffset Open(int n) => T0.AddMinutes(n);

    /// <summary>Eight bars a page of three cannot hold, each closing at 100 + its number so a quote names its bar.</summary>
    static PagedBarSource EightBars()
    {
        var source = new PagedBarSource(pageSize: 3);
        for (var n = 1; n <= 8; n++) source.Add(Bar(Open(n), 100m, 101m, 99m, 100m + n));
        return source;
    }

    /// <summary>
    /// A WORKING ORDER WHOSE BAR IS PAST THE FIRST PAGE FILLS ON THAT BAR, ONCE, IN THE READ THAT
    /// BRINGS IT (<c>U-paper-settle</c>).
    ///
    /// <para>One settle used to make one read, and one read of a paged ledger is one page: a limit
    /// first touched on bar 7 of eight, three bars a page, did not fill in the read the owner made —
    /// it filled two reads later. Here one read settles every page, the fill is on bar 7 at the
    /// limit and not on bar 8, which touches it too, and a second read writes nothing more.</para>
    /// </summary>
    [Fact]
    public async Task A_working_order_fills_once_on_its_own_bar_past_the_first_page_in_one_read()
    {
        var source = new PagedBarSource(pageSize: 3);
        var file = NewFile();
        await using var paper = Paper(source, () => T0.AddSeconds(30), file);
        await paper.ConnectAsync();

        // Placed while the ledger is still empty, so every bar below is one it has not had.
        await paper.PlaceOrderAsync(new PlaceOrderCommand("p-limit", PaperConnector.TheAccount, "BTCUSDT",
            OrderSide.Buy, OrderType.Limit, 1m, 95m, null, TimeInForce.Day, null));
        for (var n = 1; n <= 6; n++) source.Add(Bar(Open(n), 100m, 101m, 99m, 100m));
        source.Add(Bar(Open(7), 100m, 101m, 94m, 96m));     // the first bar to reach 95
        source.Add(Bar(Open(8), 96m, 97m, 93m, 94m));       // reaches it too, a bar too late
        var before = source.Reads;

        var fill = Assert.Single(await paper.GetExecutionsAsync(PaperConnector.TheAccount, null));
        log.WriteLine($"one call made {source.Reads - before} read(s) and filled {fill.ClientOrderId} at {fill.Price}");
        Assert.Equal("p-limit", fill.ClientOrderId);
        Assert.Equal(95m, fill.Price);

        using (var book = new PaperBook(file, PaperConnector.TheAccount, "USDT", 10_000m))
            Assert.Equal(Open(7), Assert.Single(book.Fills(null)).BarOpenTime);

        Assert.Single(await paper.GetExecutionsAsync(PaperConnector.TheAccount, null));
        Assert.Equal(ExecutionState.FILLED,
            Assert.Single(await paper.GetOrdersAsync(PaperConnector.TheAccount, true, null)).State);
    }

    /// <summary>
    /// A PAGED CATCH-UP READS FROM THE WATERMARK EACH PAGE ENDED AT UNTIL A READ BRINGS NOTHING PAST
    /// IT — AND THEN STOPS.
    ///
    /// <para>Four reads for eight bars at three a page: from nothing, from bar 3, from bar 6, and
    /// from bar 8, which brings nothing. The stop is the book's own watermark not moving, and never
    /// a page that came back short — this connector cannot know a source's page size, and a stop at
    /// the first page shorter than the forward ledger's would stop here after one read, at bar 3.</para>
    /// </summary>
    [Fact]
    public async Task A_paged_catch_up_reads_until_nothing_is_past_the_watermark_and_no_further()
    {
        var source = EightBars();
        await using var paper = Paper(source, () => T0.AddSeconds(30), NewFile());
        await paper.ConnectAsync();

        var quote = await paper.GetQuoteAsync("BTCUSDT");
        log.WriteLine($"{source.Reads} read(s), asked from {string.Join(", ", source.AskedFrom.Select(t => t.ToString("HH:mm")))}; "
            + $"quote {quote?.Last} stamped {quote?.At:HH:mm}");

        Assert.Equal([DateTimeOffset.MinValue, Open(3), Open(6), Open(8)], source.AskedFrom);
        Assert.Equal(108m, quote!.Last);
        Assert.Equal(Open(8) + ForwardBars.BarLength, quote.At);
    }

    /// <summary>
    /// A BOOK THAT IS CAUGHT UP READS ITS SOURCE ONCE WHEN NOTHING IS NEW — the guard on the loop.
    /// Every SDK call settles first, so this is the cost of every read the gateway makes: one query,
    /// from the newest bar settled, and not a second one to find out that the first was empty.
    /// </summary>
    [Fact]
    public async Task A_caught_up_book_reads_its_source_once_when_nothing_is_new()
    {
        var source = new PagedBarSource(pageSize: 3);
        source.Add(Bar(Open(1), 100m, 101m, 99m, 101m), Bar(Open(2), 100m, 101m, 99m, 102m));
        await using var paper = Paper(source, () => T0.AddSeconds(30), NewFile());
        await paper.ConnectAsync();
        Assert.Equal(102m, (await paper.GetQuoteAsync("BTCUSDT"))!.Last);
        var before = source.Reads;

        var again = await paper.GetQuoteAsync("BTCUSDT");
        log.WriteLine($"caught up: {source.Reads - before} read(s), asked from {source.AskedFrom[^1]:HH:mm}");
        Assert.Equal(1, source.Reads - before);
        Assert.Equal(Open(2), source.AskedFrom[^1]);
        Assert.Equal(102m, again!.Last);
    }

    /// <summary>
    /// A LATER PAGE THAT THROWS PROPAGATES, AND THE PAGES BEFORE IT STAY SETTLED. Nothing turns an
    /// unreadable ledger into a refusal or an empty answer — the gateway records UNKNOWN and
    /// reconciles — and nothing is held across reads: every page is applied before the next is
    /// asked for, so the read after the failure starts where the first page ended.
    /// </summary>
    [Fact]
    public async Task A_later_page_that_throws_propagates_with_the_earlier_pages_settled()
    {
        var source = EightBars();
        source.FailOnRead = 2;
        await using var paper = Paper(source, () => T0.AddSeconds(30), NewFile());
        await paper.ConnectAsync();

        var thrown = await Assert.ThrowsAsync<IOException>(() => paper.GetQuoteAsync("BTCUSDT"));
        log.WriteLine(thrown.Message);
        Assert.Equal(2, source.Reads);

        source.FailOnRead = 0;
        var quote = await paper.GetQuoteAsync("BTCUSDT");
        Assert.Equal(Open(3), source.AskedFrom[2]);         // resumed after the page that was settled
        Assert.Equal(108m, quote!.Last);
    }

    /// <summary>
    /// A CANCELLATION BETWEEN PAGES STOPS BEFORE THE NEXT READ, AND THE PAGES BEFORE IT STAY
    /// SETTLED. The connector checks for cancellation before every read itself rather than trusting
    /// a source to: this one never looks.
    /// </summary>
    [Fact]
    public async Task A_cancellation_between_pages_stops_before_the_next_read_with_the_earlier_pages_settled()
    {
        using var cancel = new CancellationTokenSource();
        var source = EightBars();
        source.DuringRead = n => { if (n == 1) cancel.Cancel(); };
        await using var paper = Paper(source, () => T0.AddSeconds(30), NewFile());
        await paper.ConnectAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => paper.GetQuoteAsync("BTCUSDT", cancel.Token));
        log.WriteLine($"cancelled after {source.Reads} read(s)");
        Assert.Equal(1, source.Reads);

        source.DuringRead = null;
        var quote = await paper.GetQuoteAsync("BTCUSDT");
        Assert.Equal(Open(3), source.AskedFrom[1]);
        Assert.Equal(108m, quote!.Last);
    }
}

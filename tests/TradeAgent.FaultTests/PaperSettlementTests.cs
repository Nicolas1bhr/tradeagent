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
        // The target alone: a bar that never reached the stop.
        var quiet = new MemoryBarSource();
        await using var one = Paper(quiet, () => T0.AddSeconds(30), NewFile());
        await one.ConnectAsync();
        quiet.Add("BTCUSDT", Bar(T0, 100m, 101m, 99m, 100m));
        await one.PlaceOrderAsync(new PlaceOrderCommand("b-target", PaperConnector.TheAccount, "BTCUSDT",
            OrderSide.Sell, OrderType.Limit, 1m, 110m, null, TimeInForce.Day, null));
        await one.PlaceOrderAsync(new PlaceOrderCommand("b-stop-untouched", PaperConnector.TheAccount, "BTCUSDT",
            OrderSide.Sell, OrderType.Stop, 1m, null, 90m, TimeInForce.Day, null));
        quiet.Add("BTCUSDT", Bar(T0.AddMinutes(1), 100m, 115m, 95m, 112m));

        var target = Assert.Single(await one.GetExecutionsAsync(PaperConnector.TheAccount, null));
        log.WriteLine($"target filled at {target.Price} on order {target.ClientOrderId}");
        Assert.Equal("b-target", target.ClientOrderId);
        Assert.Equal(110m, target.Price);

        // The gap, and both levels touched in the same bar.
        var violent = new MemoryBarSource();
        await using var two = Paper(violent, () => T0.AddSeconds(30), NewFile());
        await two.ConnectAsync();
        violent.Add("BTCUSDT", Bar(T0, 100m, 101m, 99m, 100m));
        await two.PlaceOrderAsync(new PlaceOrderCommand("b-both-stop", PaperConnector.TheAccount, "BTCUSDT",
            OrderSide.Sell, OrderType.Stop, 1m, null, 90m, TimeInForce.Day, null));
        await two.PlaceOrderAsync(new PlaceOrderCommand("b-both-target", PaperConnector.TheAccount, "BTCUSDT",
            OrderSide.Sell, OrderType.Limit, 1m, 110m, null, TimeInForce.Day, null));
        violent.Add("BTCUSDT", Bar(T0.AddMinutes(1), 85m, 115m, 84m, 100m));

        var fill = Assert.Single(await two.GetExecutionsAsync(PaperConnector.TheAccount, null));
        log.WriteLine($"both touched: filled {fill.ClientOrderId} at {fill.Price}");
        Assert.Equal("b-both-stop", fill.ClientOrderId);
        Assert.Equal(85m, fill.Price);
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
}

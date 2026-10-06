using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Paper;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// WHAT THE PAPER CONNECTOR SAYS IT IS, AND WHAT IT IS LINKED AGAINST.
///
/// <para>One simulated account, every capability but streaming, and an instrument list that is the
/// venue catalogue's VERIFIED rows and nothing else. And the claim the rest of it rests on: this
/// assembly references nothing that can open a socket, which is a fact the compiler maintains rather
/// than a promise about what its methods do today.</para>
/// </summary>
public class PaperConnectorTests(ITestOutputHelper log)
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
    /// WHAT THIS CONNECTOR SAYS IT IS: one simulated account, every capability but streaming, and an
    /// instrument list that is the catalogue's VERIFIED rows and nothing else. The shipped catalogue
    /// has no verified spot row at all, so out of the box the list is empty and an order is REFUSED
    /// rather than sized against an increment nobody confirmed.
    /// </summary>
    [Fact]
    public async Task It_declares_one_simulated_account_and_only_verified_instruments()
    {
        var source = new MemoryBarSource();
        await using var paper = Paper(source, () => T0, NewFile());
        await paper.ConnectAsync();

        Assert.Equal("paper", paper.Id);
        var caps = paper.Capabilities;
        Assert.True(caps.IsPaper);
        Assert.True(caps.SupportsClientOrderId);
        Assert.True(caps.SupportsOrderHistory);
        Assert.True(caps.SupportsModify);
        Assert.True(caps.SupportsClosePosition);
        Assert.False(caps.SupportsStreaming);

        var account = Assert.Single(await paper.GetAccountsAsync());
        Assert.Equal(PaperConnector.TheAccount, account.Id);
        Assert.True(account.IsSimulated);
        Assert.Equal("USDT", account.Currency);
        Assert.Equal(10_000m, account.Equity);

        Assert.Equal("BTCUSDT", Assert.Single(await paper.GetInstrumentsAsync()).Symbol);

        // The shipped catalogue, unmodified: no verified spot row, so nothing is offered and nothing
        // is guessed. The refusal names the route out, which is TradeAgent's own instrument check — the
        // owner's pair choice and Check now — and no longer a file (U-venue-verify).
        await using var shipped = new PaperConnector(new PaperConnectorOptions
        {
            Source = source, Clock = () => T0, BookFile = NewFile(),
            Catalogue = VenueCatalog.Read(Path.Combine(TestEnv.Home, "no-such-venues.json"))
        });
        await shipped.ConnectAsync();
        Assert.Empty(await shipped.GetInstrumentsAsync());
        var refused = await Assert.ThrowsAsync<ConnectorRejectedException>(
            () => shipped.PlaceOrderAsync(Market("shipped-1")));
        log.WriteLine(refused.Message);
        Assert.Contains("Check now", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("venues.json", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// ITS CLAIM ABOUT CLOSES, EARNED: A CLOSE CARRIES THE ID IT IS HANDED, ONTO THE ORDER IN THE BOOK AND
    /// ONTO ITS FILL (<c>U-flatten-absence</c>).
    ///
    /// <para><see cref="ConnectorCapabilities.ClosesCarryClientOrderId"/> lets the gateway read a lost close
    /// that a complete history does not list as one that never reached the platform. That is true here only
    /// because a close is a placement under the id it is handed — the book's primary key — and its fill is
    /// keyed by the same id; this holds the connector to it through the two history reads the gateway asks,
    /// each with a <c>since</c>.</para>
    /// </summary>
    [Fact]
    public async Task A_close_carries_the_id_it_is_handed_onto_the_order_and_its_fill()
    {
        var source = new MemoryBarSource();
        var now = T0.AddSeconds(30);
        await using var paper = Paper(source, () => now, NewFile());
        await paper.ConnectAsync();

        // A POSITION TO CLOSE: a buy, filled at the next bar's open.
        source.Add("BTCUSDT", Bar(T0, 100m, 106m, 99m, 105m));
        await paper.PlaceOrderAsync(Market("carried-open"));
        source.Add("BTCUSDT", Bar(T0.AddMinutes(1), 110m, 112m, 109m, 111m));
        Assert.Equal(1m, Assert.Single(await paper.GetPositionsAsync(PaperConnector.TheAccount)).Quantity);

        // THE CLOSE, under the id the gateway would have recorded it under, filled at the bar after it.
        now = T0.AddMinutes(1).AddSeconds(30);
        const string id = "TA-op-budget-close-0123456789abcdef-0";
        var close = await paper.ClosePositionAsync(PaperConnector.TheAccount, "BTCUSDT", id);
        source.Add("BTCUSDT", Bar(T0.AddMinutes(2), 120m, 121m, 119m, 120m));

        var since = now - TimeSpan.FromMinutes(5);
        var orders = await paper.GetOrdersAsync(PaperConnector.TheAccount, true, since);
        var fills = await paper.GetExecutionsAsync(PaperConnector.TheAccount, since);
        log.WriteLine($"the close : {close?.ClientOrderId} {close?.Side} {close?.Quantity} {close?.State}");
        log.WriteLine($"history   : [{string.Join(" | ", orders.Select(o => $"{o.ClientOrderId} {o.Side} {o.Quantity} {o.State}"))}]");
        log.WriteLine($"fills     : [{string.Join(" | ", fills.Select(f => $"{f.ClientOrderId} {f.Side} {f.Quantity} @ {f.Price}"))}]");

        Assert.True(paper.Capabilities.ClosesCarryClientOrderId);
        Assert.NotNull(close);
        Assert.Equal(id, close.ClientOrderId);
        Assert.Equal(OrderSide.Sell, close.Side);
        Assert.Equal(1m, close.Quantity);
        var listed = Assert.Single(orders, o => o.ConnectorOrderId == close.ConnectorOrderId);
        Assert.Equal(id, listed.ClientOrderId);
        Assert.Equal(ExecutionState.FILLED, listed.State);
        Assert.Equal(id, Assert.Single(fills, f => f.ConnectorOrderId == close.ConnectorOrderId).ClientOrderId);
        Assert.Empty(await paper.GetPositionsAsync(PaperConnector.TheAccount));
    }

    /// <summary>
    /// THE ASSEMBLY REFERENCES NO HTTP CLIENT — and no socket, no vendor SDK and no other connector.
    ///
    /// <para>"This connector cannot reach a venue" is the whole safety argument for letting it place
    /// orders unattended, and a promise about what its methods do today is not an argument: the next
    /// person to add a price fetch would keep the promise's words and break its meaning. So it is
    /// stated as what the assembly is LINKED against, which is a fact the compiler maintains.</para>
    /// </summary>
    [Fact]
    public void The_assembly_references_no_http_client()
    {
        var referenced = typeof(PaperConnector).Assembly
            .GetReferencedAssemblies().Select(a => a.Name!).OrderBy(n => n, StringComparer.Ordinal).ToList();
        log.WriteLine(string.Join(", ", referenced));

        string[] banned = ["System.Net.Http", "System.Net.Sockets", "System.Net.WebSockets",
            "System.Net.Requests", "System.Net.HttpListener", "System.Net.Mail", "System.Net.NameResolution",
            "System.Net.Security", "System.Net.WebClient", "Grpc.Core", "RestSharp", "Newtonsoft.Json"];
        foreach (var b in banned) Assert.DoesNotContain(b, referenced);

        // And nothing of TradeAgent's beyond the two it is allowed: no other connector, no gateway,
        // no bridge, no provisioning — the three places a venue could be reached from.
        var ours = referenced.Where(n => n.StartsWith("TradeAgent.", StringComparison.Ordinal)).ToList();
        Assert.Equal(["TradeAgent.ConnectorSdk", "TradeAgent.Core"], ours);
    }
}

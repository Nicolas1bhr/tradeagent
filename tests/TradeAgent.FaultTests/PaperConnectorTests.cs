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
        // is guessed. The refusal names the route out, which is the account owner's venues.json.
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
        Assert.Contains("venues.json", refused.Message, StringComparison.Ordinal);
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

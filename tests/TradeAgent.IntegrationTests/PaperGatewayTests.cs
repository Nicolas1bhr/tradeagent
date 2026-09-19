using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Paper;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE PAPER CONNECTOR ON THE REAL PATH, DRIVEN THE WAY THE PRODUCT DRIVES A CONNECTOR.
///
/// <para>Not <c>PlaceOrderAsync</c> on the connector — <c>TradingGateway.PlaceAsync</c>, which is
/// where the risk gates, the quote freshness check, the dispatch gate and the request ledger are.
/// The whole point of a paper connector is that it exercises that path rather than a shortcut around
/// it, and a test that called the connector directly would prove nothing about the path.</para>
///
/// <para>The second half is the boundary: a LIVE mode does not get this account either. The refusal
/// is the installation's own live gate, in front of every platform, and this connector could not
/// reach a venue past it if it were opened — there is no venue in the assembly.</para>
/// </summary>
public class PaperGatewayTests(ITestOutputHelper log)
{
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

    static KlineBar Bar(DateTimeOffset open, decimal o, decimal h, decimal l, decimal c) => new(open, o, h, l, c, 1m);

    [Fact]
    public async Task The_gateway_in_PAPER_mode_places_through_the_paper_connector_and_a_live_mode_refuses_its_account()
    {
        // The quote the gateway sizes against is the LAST CLOSED BAR's close, stamped at that bar's
        // close time — so the fixture's bars have to be as recent as a live feed's, or the gateway
        // correctly refuses to trade off a price it considers a memory.
        var now = DateTimeOffset.UtcNow;
        var placedAt = now.AddSeconds(-30);
        var source = new MemoryBarSource();
        source.Add("BTCUSDT", Bar(now.AddMinutes(-2), 49_000m, 49_500m, 48_900m, 49_400m));
        source.Add("BTCUSDT", Bar(now.AddMinutes(-1), 49_400m, 50_100m, 49_300m, 50_000m));

        await using var conn = new PaperConnector(new PaperConnectorOptions
        {
            Source = source,
            Clock = () => placedAt,
            BookFile = Path.Combine(TestEnv.Home, $"paper-gw-{Guid.NewGuid():n}.db"),
            Catalogue = Catalogue()
        });

        var db = TestEnv.NewDb();
        await using var gw = new TradingGateway(db, conn, new HealthRegistry());
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
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        var intent = new PlaceIntent("BTCUSDT", OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, null);
        var request = await gw.PlaceAsync(new AgentContext("ai"), "paper-gw-1", intent);

        log.WriteLine($"{request.ConnectorId} / {request.State} / coid {request.ClientOrderId}");
        Assert.Equal(PaperConnector.ConnectorId, request.ConnectorId);
        Assert.Equal(PaperConnector.TheAccount, request.AccountId);
        Assert.Equal(TradingGateway.ClientOrderIdFor("paper-gw-1"), request.ClientOrderId);

        // Accepted at once and WORKING: what it fills at is the next closed bar's open, and that bar
        // has not arrived. Nothing invented a price to fill it with in the meantime.
        Assert.Equal(ExecutionState.WORKING, request.State);
        Assert.Empty(await conn.GetExecutionsAsync(PaperConnector.TheAccount, null));

        source.Add("BTCUSDT", Bar(now, 50_000m, 50_400m, 49_950m, 50_300m));

        var fill = Assert.Single(await conn.GetExecutionsAsync(PaperConnector.TheAccount, null));
        log.WriteLine($"filled {fill.Quantity} at {fill.Price} on {fill.ClientOrderId}");
        Assert.Equal(TradingGateway.ClientOrderIdFor("paper-gw-1"), fill.ClientOrderId);
        Assert.Equal(50_000m, fill.Price);
        Assert.Equal(1m, fill.Quantity);

        // AND THE OTHER HALF. A live mode is refused this account — by the installation's own live
        // gate, which stands in front of every platform. The paper connector never becomes a
        // real-money path: there is no venue in its assembly to become one with.
        gw.Update(s => s.Mode = TradingMode.LIVE_CONFIRM);
        var refused = await Assert.ThrowsAsync<GatewayDeniedException>(
            () => gw.PlaceAsync(new AgentContext("ai"), "paper-gw-2", intent));
        log.WriteLine($"{refused.Code} — {refused.Message}");
        Assert.Equal(ErrorCode.LIVE_NOT_ACTIVATED, refused.Code);
        Assert.Single(await conn.GetExecutionsAsync(PaperConnector.TheAccount, null));
    }
}

using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Paper;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Strategy;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-paper-friction — A PAPER FILL PAYS THE COST MODEL THE REFEREE JUDGES WITH, AND SAYS WHOSE NUMBERS
/// THEY ARE.
///
/// <para><b>What was wrong before this.</b> The paper connector was handed the owner's two settings,
/// non-nullable decimals that defaulted to 0 and that nothing in the app ever wrote, so every paper fill
/// on every installation was FRICTIONLESS: forward evidence with no cost in it, judged later against a
/// referee that charges Binance spot's 0.100% a fill (<c>docs/EDGE-FACTORY.md</c> § 4.5, one cost model
/// at every stage). And the fill's own sentence called any friction that was not zero "declared by the
/// account owner" — which a fee TradeAgent took from its own table is not.</para>
///
/// <para><b>The connector's own default is untouched</b> (<c>PaperSettlementTests</c>, which asserts
/// FRICTIONLESS on a connector built with nothing, stands): the venue model is the HOST's choice, made
/// from the owner's settings at the moment of each fill.</para>
/// </summary>
public class PaperFrictionTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset T0 = new(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);

    static string NewFile() => Path.Combine(TestEnv.Home, $"paper-friction-{Guid.NewGuid():n}.db");

    static KlineBar Bar(DateTimeOffset open, decimal o, decimal h, decimal l, decimal c) => new(open, o, h, l, c, 1m);

    /// <summary>
    /// ONE VERIFIED SPOT ROW, declared the way an account owner declares it — the shipped catalogue's
    /// BTCUSDT is unverified, and the paper connector trades verified rows and nothing else.
    /// </summary>
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

    static PaperConnector Paper(MemoryBarSource source, string file, Func<PaperFriction> friction) =>
        new(new PaperConnectorOptions
        {
            Source = source,
            Clock = () => T0.AddSeconds(30),
            BookFile = file,
            Catalogue = Catalogue(),
            Friction = friction
        });

    static PlaceOrderCommand Market(string coid) =>
        new(coid, PaperConnector.TheAccount, "BTCUSDT", OrderSide.Buy, OrderType.Market, 1m, null, null,
            TimeInForce.Day, null);

    /// <summary>
    /// One market buy of 1 BTCUSDT placed inside the bar that opened at 100, filled at the next bar's
    /// open of 110 — and the fill as the connector reports it, with the sentence the book recorded.
    /// </summary>
    static async Task<(ExecutionInfo Fill, string Sentence, string? Status)> OneFill(Func<PaperFriction> friction)
    {
        var source = new MemoryBarSource();
        var file = NewFile();
        await using var paper = Paper(source, file, friction);
        await paper.ConnectAsync();

        source.Add("BTCUSDT", Bar(T0, 100m, 101m, 99m, 100m));
        await paper.PlaceOrderAsync(Market("pf-1"));
        source.Add("BTCUSDT", Bar(T0.AddMinutes(1), 110m, 112m, 109m, 111m));

        var fill = Assert.Single(await paper.GetExecutionsAsync(PaperConnector.TheAccount, null));
        var status = paper.StatusDetail;

        using var book = new PaperBook(file, PaperConnector.TheAccount, "USDT", 10_000m);
        return (fill, Assert.Single(book.Fills(null)).Friction, status);
    }

    /// <summary>
    /// (d) A FILL PRICED BY TRADEAGENT'S OWN TABLE SAYS SO, AND NEVER THAT THE OWNER DECLARED IT.
    ///
    /// <para>Red first: the sentence had two readings — FRICTIONLESS, or "declared by the account owner"
    /// for anything else — so the venue's published fee on a paper fill would have been recorded as a
    /// number the owner typed. The model is named by its id and the sha of its text, the same pair a run
    /// records, so a reader can say exactly which table the fill was charged under.</para>
    /// </summary>
    [Fact]
    public async Task The_fill_sentence_names_the_venue_model_not_the_owner()
    {
        var venue = VenueFriction.Of(VenueCatalog.BinanceSpot);
        Assert.NotNull(venue);

        var (fill, sentence, status) =
            await OneFill(() => PaperFriction.Of(FrictionInForce.Resolve(null, null, venue)));
        log.WriteLine(sentence);

        // The venue's numbers, applied: two basis points up on the open, then 0.100% of what was paid.
        Assert.Equal(110m * 1.0002m, fill.Price);
        Assert.Equal(110m * 1.0002m * 0.001m, fill.Fee);

        Assert.Contains(venue.VenueId, sentence, StringComparison.Ordinal);
        Assert.Contains($"v{VenueCostModel.Version}", sentence, StringComparison.Ordinal);
        Assert.Contains(venue.Sha256, sentence, StringComparison.Ordinal);
        Assert.Contains("published standard taker rate", sentence, StringComparison.Ordinal);
        Assert.Contains("ASSUMPTION", sentence, StringComparison.Ordinal);
        Assert.DoesNotContain("account owner", sentence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FRICTIONLESS", sentence, StringComparison.Ordinal);

        // And the connector's own status line carries the same words, not a second wording of them.
        Assert.Contains(sentence, status, StringComparison.Ordinal);
    }
}

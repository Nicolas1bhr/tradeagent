using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE BUILT-IN SIMULATOR QUOTES PRICES THAT COULD EXIST.
///
/// Found by the AI itself on 2026-09-07, on the loop's first run on a screen: it read the practice
/// simulator's MES at 107.31 / 107.81 against a 0.25 tick grid, concluded the quotes were not prices
/// any exchange would print, and declined to treat them as evidence for anything. That was the right
/// call and it cost a turn — but the deeper cost is that a fixture nobody can believe cannot be used
/// to rehearse the one thing the simulator exists for, which is the mechanics of an order.
///
/// A price off the grid is not a small inaccuracy in a fake. Every limit price, every stop, every
/// fill and every P&amp;L figure derived from these quotes is then a number the real venue would have
/// rejected, and a rounding rule written against it is written against a world that does not exist.
///
/// What is asserted is the GRID, not the figures: the prices stay deterministic and unmoving — the
/// simulator is a fixture, not a market — but they now sit where the instrument's own
/// <see cref="InstrumentInfo.TickSize"/> says a price may sit.
/// </summary>
public class SimulatorQuoteGridTests
{
    static readonly FakeConnector Connector = new(new FakeBroker());

    static async Task<IReadOnlyList<InstrumentInfo>> Instruments() => await Connector.GetInstrumentsAsync();

    /// <summary>
    /// Every quote the simulator prints, on the grid the simulator itself publishes for that symbol.
    /// YM's grid is 1 and the other three are 0.25, so this is not one arithmetic dressed up as four.
    /// </summary>
    [Fact]
    public async Task Every_quoted_price_sits_on_the_instruments_own_tick_grid()
    {
        var broker = new FakeBroker();

        foreach (var i in await Instruments())
        {
            var tick = i.TickSize;
            var q = broker.Quote(i.Symbol, DateTimeOffset.UtcNow);

            Assert.Equal(0m, decimal.Remainder(q.Bid!.Value, tick));
            Assert.Equal(0m, decimal.Remainder(q.Ask!.Value, tick));
            Assert.Equal(0m, decimal.Remainder(q.Last!.Value, tick));
        }
    }

    /// <summary>
    /// The spread is ONE tick either side of the mid — the tightest a book can be — so a test that
    /// crosses it by a tick is crossing it by a tick on every symbol rather than by four on YM.
    /// </summary>
    [Fact]
    public async Task The_spread_is_one_tick_either_side_of_the_mid()
    {
        var broker = new FakeBroker();

        foreach (var i in await Instruments())
        {
            var tick = i.TickSize;
            var q = broker.Quote(i.Symbol, DateTimeOffset.UtcNow);

            Assert.Equal(q.Last!.Value - tick, q.Bid!.Value);
            Assert.Equal(q.Last!.Value + tick, q.Ask!.Value);
        }
    }

    /// <summary>
    /// STILL DETERMINISTIC AND STILL UNMOVING. The whole harness rests on it: a simulator whose
    /// second quote differs from its first turns every fill-price assertion in three test projects
    /// into a flake.
    /// </summary>
    [Fact]
    public void The_same_symbol_is_quoted_the_same_way_every_time()
    {
        var broker = new FakeBroker();
        var first = broker.Quote("MES", DateTimeOffset.UtcNow);
        var again = new FakeBroker().Quote("MES", DateTimeOffset.UtcNow.AddHours(3));

        Assert.Equal(first.Bid, again.Bid);
        Assert.Equal(first.Ask, again.Ask);
        Assert.Equal(first.Last, again.Last);
    }

    /// <summary>
    /// The offset a test uses to move the price is applied BEFORE the snap, so a quote a test has
    /// pushed is on the grid too. A snapped base with an unsnapped offset added afterwards would put
    /// the price back off the grid at exactly the moment a test cared where it was.
    /// </summary>
    [Fact]
    public void A_price_a_test_has_moved_is_snapped_as_well()
    {
        var broker = new FakeBroker { PriceOffset = 0.4m };
        var q = broker.Quote("ES", DateTimeOffset.UtcNow);

        Assert.Equal(0m, decimal.Remainder(q.Last!.Value, 0.25m));
        Assert.Equal(0m, decimal.Remainder(q.Bid!.Value, 0.25m));
        Assert.Equal(0m, decimal.Remainder(q.Ask!.Value, 0.25m));
    }

    /// <summary>
    /// A SYMBOL THE PLATFORM LISTS NO INSTRUMENT FOR HAS NO GRID TO SNAP TO, and nothing is invented
    /// for it. <c>XYZ</c> is that symbol on purpose — it is what
    /// <c>TickNormalizedModifyTests.An_unknown_tick_grid_leaves_a_changed_price_for_a_person_to_judge</c>
    /// trades — so a default grid quietly applied here would make the one case that tests "we do not
    /// know this instrument" indistinguishable from the ones that do.
    /// </summary>
    [Fact]
    public async Task A_symbol_the_simulator_does_not_list_is_quoted_without_a_grid()
    {
        Assert.DoesNotContain(await Instruments(), i => i.Symbol == "XYZ");

        var q = new FakeBroker().Quote("XYZ", DateTimeOffset.UtcNow);

        Assert.Equal(FakeBroker.BasePrice("XYZ"), q.Last);
        Assert.Equal(q.Last!.Value - 0.25m, q.Bid);
        Assert.Equal(q.Last!.Value + 0.25m, q.Ask);
    }
}

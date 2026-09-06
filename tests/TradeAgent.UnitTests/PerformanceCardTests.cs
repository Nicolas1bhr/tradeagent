using TradeAgent.App;
using TradeAgent.ConnectorSdk;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-ledger item 3. The card's WORDS, which are the part that can be wrong in a way a screenshot
/// would not show: a net figure the software cannot compute must read as a dash and a reason, never
/// as a number that flatters the result by treating an unreported cost as no cost.
/// </summary>
public class PerformanceCardTests
{
    static readonly DateTimeOffset Day = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    static Fill F(OrderSide side, decimal qty, decimal price, decimal? fee, int minute) =>
        new("SIM-001", $"X-{minute}", Day.AddMinutes(minute), "ES", side.ToString(), qty, price,
            "FB-1", null, null, null, FillSource.Event, fee, Day.AddMinutes(minute));

    static PnlReport Report(IEnumerable<Fill> fills) => Pnl.Compute(new PnlInputs
    {
        AllFills = fills.ToList(),
        Instruments = [new InstrumentInfo("ES", "E-mini S&P 500", "CME", 0.25m, 12.50m, 50m)],
        Window = "all",
        AsOf = Day.AddHours(1)
    });

    [Fact]
    public void A_net_that_cannot_be_computed_is_a_dash_and_a_reason_never_a_number()
    {
        var w = PerformanceCard.Words(Report([
            F(OrderSide.Buy, 1, 100m, 2m, 0),
            F(OrderSide.Sell, 1, 110m, null, 1),
        ]), "today");

        Assert.Equal("—", w.Net);
        Assert.False(w.NetIsKnown);
        Assert.NotNull(w.Incomplete);
        Assert.Contains("did not report a fee", w.Incomplete);
        // The fees it DOES know are still shown, marked as at least this much.
        Assert.Equal("2.00+", w.Fees);
    }

    [Fact]
    public void A_profit_reads_as_a_signed_number_and_a_loss_as_a_negative_one()
    {
        var won = PerformanceCard.Words(Report([
            F(OrderSide.Buy, 1, 100m, 1m, 0), F(OrderSide.Sell, 1, 110m, 1m, 1),
        ]), "today");
        Assert.Equal("+498.00", won.Net);
        Assert.True(won.NetIsPositive);
        Assert.True(won.NetIsKnown);
        Assert.Null(won.Incomplete);

        var lost = PerformanceCard.Words(Report([
            F(OrderSide.Buy, 1, 110m, 1m, 0), F(OrderSide.Sell, 1, 100m, 1m, 1),
        ]), "today");
        Assert.Equal("-502.00", lost.Net);
        Assert.False(lost.NetIsPositive);
        Assert.Equal("502.00", lost.Drop);   // the drop is the curve's, and the curve pays the fees it knows
        Assert.Equal("2", lost.Fills);
    }

    /// <summary>An empty ledger is empty, not a loss of nothing: no fills, no fees, no claim.</summary>
    [Fact]
    public void An_empty_ledger_reads_as_nothing_rather_than_as_zero_profit()
    {
        var w = PerformanceCard.Words(Report([]), "today");

        Assert.Equal("0", w.Fills);
        Assert.Equal("0.00", w.Net);       // nothing has been traded, so nothing is unknown either
        Assert.Equal("0.00", w.Fees);
        Assert.Null(w.Incomplete);
    }
}

using TradeAgent.ConnectorSdk;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-ledger item 2, red first. The arithmetic is ordinary; the rule that is not is this one:
/// <b>an unknown never reads as zero.</b>
///
/// A fee the platform never reported is not a fee of nothing. A net figure that treats it as one is
/// wrong in the owner's favour on every single fill, and it is the number a loss budget would be
/// enforced against — so it is WITHHELD, and <c>incomplete</c> says why in words.
/// </summary>
public class PnlTests
{
    static readonly DateTimeOffset Day = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    static readonly InstrumentInfo[] Instruments =
    [
        new("ES", "E-mini S&P 500", "CME", 0.25m, 12.50m, 50m),
        new("MES", "Micro E-mini S&P 500", "CME", 0.25m, 1.25m, 5m),
    ];

    static Fill F(string symbol, OrderSide side, decimal qty, decimal price, decimal? fee = null,
        int minute = 0, string? id = null) =>
        new(AccountId: "SIM-001", ExecutionId: id ?? $"X-{symbol}-{side}-{minute}-{qty}-{price}",
            At: Day.AddMinutes(minute), Symbol: symbol, Side: side.ToString(), Quantity: qty, Price: price,
            ConnectorOrderId: "FB-1", ClientOrderId: null, RequestId: null, AgentSession: null,
            Source: FillSource.Event, Fee: fee, RecordedAt: Day.AddMinutes(minute));

    static PnlInputs In(IEnumerable<Fill> fills, DateTimeOffset? since = null,
        IReadOnlyList<PositionInfo>? positions = null, IReadOnlyDictionary<string, QuoteInfo>? quotes = null,
        IReadOnlyList<InstrumentInfo>? instruments = null) => new()
    {
        AllFills = fills.ToList(),
        Positions = positions,
        Quotes = quotes ?? new Dictionary<string, QuoteInfo>(),
        Instruments = instruments ?? Instruments,
        Since = since,
        Window = since is null ? "all" : "since",
        AsOf = Day.AddHours(1)
    };

    /// <summary>
    /// THE ITEM'S OWN RED. One fill's fee is known, the other's is not. The net after costs cannot be
    /// computed from that, so it is not given — and the reader is told which fills are missing one.
    /// </summary>
    [Fact]
    public void A_fee_the_platform_did_not_report_withholds_the_net_and_is_named()
    {
        var r = Pnl.Compute(In([
            F("ES", OrderSide.Buy, 1, 100m, fee: 2m, minute: 0),
            F("ES", OrderSide.Sell, 1, 101m, fee: null, minute: 1),
        ]));

        Assert.Equal(50m, r.Realized);              // 1 point x 50 per point
        Assert.Null(r.Net);                         // NOT 48: the second fee is unknown
        Assert.Equal(2m, r.Fees);                   // only what the platform actually said
        Assert.Equal(1, r.FeesUnknownFills);
        Assert.Contains(r.Incomplete, s => s.Contains("did not report a fee for 1 of the 2 fills"));
    }

    /// <summary>
    /// And when every fee IS known the net is a number, so the rule above costs nothing on a platform
    /// that reports them. A reported zero is a reported fee.
    /// </summary>
    [Fact]
    public void A_net_is_given_when_every_fill_in_the_period_carries_a_fee()
    {
        var r = Pnl.Compute(In([
            F("ES", OrderSide.Buy, 1, 100m, fee: 2m, minute: 0),
            F("ES", OrderSide.Sell, 1, 101m, fee: 0m, minute: 1),
        ]));

        Assert.Equal(50m, r.Realized);
        Assert.Equal(2m, r.Fees);
        Assert.Equal(48m, r.Net);
        Assert.Equal(0, r.FeesUnknownFills);
        Assert.DoesNotContain(r.Incomplete, s => s.Contains("did not report a fee"));
    }

    /// <summary>Average cost, signed by side, times what the platform says a point is worth.</summary>
    [Fact]
    public void Realized_is_average_cost_signed_by_side_and_sized_by_the_contract()
    {
        var r = Pnl.Compute(In([
            F("ES", OrderSide.Buy, 2, 100m, fee: 0m, minute: 0),
            F("ES", OrderSide.Buy, 2, 110m, fee: 0m, minute: 1),   // average is now 105
            F("ES", OrderSide.Sell, 3, 120m, fee: 0m, minute: 2),  // 3 x 15 x 50
        ]));

        Assert.Equal(2250m, r.Realized);
        Assert.Equal(2250m, r.Net);
        var es = Assert.Single(r.BySymbol);
        Assert.Equal(50m, es.Multiplier);
        Assert.True(es.MultiplierKnown);
    }

    /// <summary>A short realises the other way round, and a fill bigger than the position flips it.</summary>
    [Fact]
    public void A_sale_that_opens_a_short_realizes_when_it_is_bought_back()
    {
        var r = Pnl.Compute(In([
            F("MES", OrderSide.Sell, 2, 100m, fee: 0m, minute: 0),
            F("MES", OrderSide.Buy, 3, 96m, fee: 0m, minute: 1),   // closes 2 at +4, opens 1 long at 96
            F("MES", OrderSide.Sell, 1, 98m, fee: 0m, minute: 2),  // closes that 1 at +2
        ]));

        Assert.Equal(2 * 4m * 5m + 1 * 2m * 5m, r.Realized);
    }

    /// <summary>
    /// The window selects which realised increments are counted; it does not restart the position.
    /// A sale today of a position opened yesterday realises against yesterday's price, and reading it
    /// as the opening of a short would be a wrong answer rather than a partial one.
    /// </summary>
    [Fact]
    public void Todays_figure_realizes_against_a_position_opened_before_today()
    {
        var yesterday = Day.AddDays(-1);
        var fills = new List<Fill>
        {
            F("ES", OrderSide.Buy, 1, 100m, fee: 0m, minute: 0) with { At = yesterday, RecordedAt = yesterday },
            F("ES", OrderSide.Sell, 1, 110m, fee: 0m, minute: 1),
        };

        var today = Pnl.Compute(In(fills, since: TradingGateway.StartOfDay(Day)));

        Assert.Equal(1, today.Fills);                  // one fill today
        Assert.Equal(500m, today.Realized);            // realised against yesterday's 100, not against nothing
        Assert.Equal(2, Pnl.Compute(In(fills)).Fills); // and the whole ledger still holds both
    }

    /// <summary>Days are UTC calendar days, and each carries its own fills and fees.</summary>
    [Fact]
    public void Days_are_utc_and_carry_their_own_fills()
    {
        var r = Pnl.Compute(In([
            F("ES", OrderSide.Buy, 1, 100m, fee: 1m, minute: 0) with { At = Day.AddDays(-1) },
            F("ES", OrderSide.Sell, 1, 110m, fee: 1m, minute: 1),
        ]));

        Assert.Equal(2, r.ByDay.Count);
        Assert.Equal("2026-09-05", r.ByDay[0].Day);
        Assert.Equal("2026-09-06", r.ByDay[1].Day);
        Assert.Equal(0m, r.ByDay[0].Realized);
        Assert.Equal(500m, r.ByDay[1].Realized);
    }

    /// <summary>
    /// A symbol the platform describes no contract for is counted as quantity x price — the honest
    /// fallback — and that is SAID, because on a futures contract it is out by the contract size.
    /// </summary>
    [Fact]
    public void A_symbol_with_no_contract_size_is_counted_flat_and_named()
    {
        var r = Pnl.Compute(In([
            F("XYZ", OrderSide.Buy, 1, 100m, fee: 0m, minute: 0),
            F("XYZ", OrderSide.Sell, 1, 110m, fee: 0m, minute: 1),
        ]));

        Assert.Equal(10m, r.Realized);
        Assert.False(Assert.Single(r.BySymbol).MultiplierKnown);
        Assert.Contains(r.Incomplete, s => s.Contains("what one contract of XYZ is worth"));
    }

    /// <summary>The worst peak-to-trough drop of the curve, which is what a loss budget watches.</summary>
    [Fact]
    public void Max_drawdown_is_the_worst_drop_along_the_curve()
    {
        var r = Pnl.Compute(In([
            F("ES", OrderSide.Buy, 1, 100m, fee: 0m, minute: 0),
            F("ES", OrderSide.Sell, 1, 90m, fee: 0m, minute: 1),    // -500
            F("ES", OrderSide.Buy, 1, 90m, fee: 0m, minute: 2),
            F("ES", OrderSide.Sell, 1, 100m, fee: 0m, minute: 3),   // +500, back to flat
        ]));

        Assert.Equal(0m, r.Realized);
        Assert.Equal(500m, r.MaxDrawdown);
        Assert.False(r.DrawdownIncludesUnrealized);   // no positions were handed in
    }

    /// <summary>
    /// An open position with no price is not valued at zero and does not make the total read as
    /// though the position were flat. Unrealized is null and the position is named.
    /// </summary>
    [Fact]
    public void An_open_position_with_no_price_leaves_unrealized_unknown()
    {
        var fills = new[] { F("ES", OrderSide.Buy, 1, 100m, fee: 0m, minute: 0) };
        var open = new[] { new PositionInfo("P-ES", "SIM-001", "ES", 1m, 100m, null) };

        var blind = Pnl.Compute(In(fills, positions: open));
        Assert.Null(blind.Unrealized);
        Assert.Contains(blind.Incomplete, s => s.Contains("has not seen a price for ES"));

        var priced = Pnl.Compute(In(fills, positions: open, quotes: new Dictionary<string, QuoteInfo>
        {
            ["ES"] = new("ES", 104.75m, 105.25m, 105m, 1, 1, Day.AddMinutes(5))
        }));
        Assert.Equal(250m, priced.Unrealized);        // 5 points x 50
        Assert.True(priced.DrawdownIncludesUnrealized);
        Assert.DoesNotContain(priced.Incomplete, s => s.Contains("has not seen a price"));
    }

    /// <summary>
    /// An empty <c>incomplete</c> is a claim that nothing was missing, so it is only made when
    /// nothing was: every fee reported, every contract sized, every open position priced.
    /// </summary>
    [Fact]
    public void Incomplete_is_empty_only_when_nothing_is_missing()
    {
        var r = Pnl.Compute(In(
            [F("ES", OrderSide.Buy, 1, 100m, fee: 1m, minute: 0), F("ES", OrderSide.Sell, 1, 101m, fee: 1m, minute: 1)],
            positions: []));

        Assert.Empty(r.Incomplete);
        Assert.Equal(48m, r.Net);
        Assert.Equal(0m, r.Unrealized);
    }
}

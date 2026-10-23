using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// A REFUSED ARMED HOME NEVER STRANDS A REAL POSITION (<c>U-board-a1</c> fix pass 1; seat A's ruling, amending the brief): while a
/// home an earlier build armed for real money is refused under <c>HOME_ARMED_BEFORE_THE_BOARD</c>, nothing that opens or adds exposure
/// goes out — an agent's order, a deployment's — but what only REDUCES an open position still does: the loss budget's automatic
/// flatten and the valuation exit, each sized and re-read at the wire and refused if it would open, flip or add, and the owner's own
/// Cancel all working orders and Close all positions. The refusal's words say so. Every position here is one the earlier build left
/// open on a real (not simulated) account, written straight onto the book, and the settings row is the earlier build's, byte for byte.
/// </summary>
public class ArmedHomeReductionTests(ITestOutputHelper log)
{
    sealed class TestClock(DateTimeOffset at) : TimeProvider
    {
        DateTimeOffset _now = at;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    static readonly DateTimeOffset Noon = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Tick = TimeSpan.FromSeconds(20);
    static readonly TimeSpan Silent = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A HOME AN EARLIER BUILD ARMED: its settings row says <c>LIVE_AUTONOMOUS</c> with real money switched on, over the limits this build
    /// still reads from settings until part a2, on a broker account that is not simulated; and a long ES 2 the earlier build left open.
    /// </summary>
    static async Task<(TradingGateway Gw, RecordingConnector Conn, Database Db, TestClock Clock, string Raw)> ArmedHome(
        decimal exitAfterMinutes = 15m)
    {
        var clock = new TestClock(Noon);
        var db = TestEnv.NewDb();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker { IsSimulated = false })
        {
            EmergencyBudget = Unresolved.PressBudgetFor(2),
            QuoteClock = clock
        });
        var shape = new TradeAgentSettings { SelectedAccountId = conn.Broker.AccountId };
        shape.Risk.InstrumentAllowlist = [.. TestEnv.Instruments];
        shape.Risk.MaxOrderQuantity = 10m;
        shape.Risk.MaxNotionalPerOrder = 10_000_000m;
        shape.Risk.MaxOpenPositions = 10;
        shape.Risk.MaxOrdersPerMinute = 100;
        shape.Risk.MaxDailyLoss = 1_000m;
        shape.Risk.ValuationLossExitMinutes = exitAfterMinutes;
        var raw = "{\"mode\":\"LIVE_AUTONOMOUS\",\"live_activated\":true," + Json.Write(shape)[1..];
        db.SetKv("settings", raw);

        conn.Broker.Accept(new PlaceOrderCommand("EARLIER-1", conn.Broker.AccountId, "ES", OrderSide.Buy,
            OrderType.Market, 2m, null, null, TimeInForce.Day, "left open by an earlier build"), FillBehaviour.FillImmediately);

        var gw = new TradingGateway(db, conn, new HealthRegistry(), new GatewayOptions { Clock = clock });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        Assert.NotNull(gw.EarlierLiveSetting);
        return (gw, conn, db, clock, raw);
    }

    static decimal Held(RecordingConnector conn, string symbol) =>
        conn.Broker.Positions.FirstOrDefault(p => p.Symbol == symbol)?.Quantity ?? 0m;

    static async Task Ticks(TradingGateway gw, TestClock clock, int howMany)
    {
        for (var i = 0; i < howMany; i++)
        {
            clock.Advance(Tick);
            await gw.RefreshHealthAsync();
        }
    }

    /// <summary>
    /// NOTHING THAT OPENS GOES OUT, AND THE LOSS BUDGET STILL CLOSES WHAT IS OPEN: an agent's buy is refused under the armed home's
    /// code, and a confirmed daily breach flattens the earlier build's long on the health pass — a close, never a place — with the
    /// settings row untouched and the home still refused.
    /// </summary>
    [Fact]
    public async Task In_a_refused_armed_home_the_loss_budget_still_closes_and_nothing_opens()
    {
        var (gw, conn, db, clock, raw) = await ArmedHome();
        using var _1 = db;
        await using var _2 = gw;

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "opener", TestEnv.Buy("ES", 1m)));
        Assert.Equal(ErrorCode.HOME_ARMED_BEFORE_THE_BOARD, denied.Code);

        conn.Broker.PriceOffset = -20m;
        await Ticks(gw, clock, 2);
        log.WriteLine($"places {conn.Places}, closes {conn.Closes}, cancels {conn.Cancels}; ES {Held(conn, "ES")}");

        Assert.NotNull(gw.DayClosed(conn.Broker.AccountId));
        Assert.Equal(0, conn.Places);
        Assert.True(conn.Closes >= 1);
        Assert.Equal(0m, Held(conn, "ES"));
        Assert.Equal(raw, db.GetKv("settings"));
        Assert.NotNull(gw.EarlierLiveSetting);
    }

    /// <summary>
    /// AN AUTOMATIC EXIT THAT WOULD ADD IS REFUSED AT THE WIRE: between the flatten's write-ahead row and its close, an outside sell of 3
    /// turns the long 2 into a short 1, and the sell of 2 the flatten composed would add to that short. Nothing reaches the wire, the
    /// leg is refused in words, and the book is the short 1 the outside fill made — never a short 3.
    /// </summary>
    [Fact]
    public async Task In_a_refused_armed_home_an_automatic_exit_that_would_add_is_refused()
    {
        var (gw, conn, db, clock, _) = await ArmedHome();
        using var _1 = db;
        await using var _2 = gw;
        var account = conn.Broker.AccountId;

        var landed = false;
        conn.Seam = kind =>
        {
            if (kind == RecordingConnector.HeldCall.Positions && !landed
                && gw.Requests.Query("request_id LIKE $p", ("$p", $"{TradingGateway.BudgetClosePress}-%"))
                    .Any(r => r.Instrument == "ES"))
            {
                landed = true;
                conn.Broker.Accept(new PlaceOrderCommand("EXT-1", account, "ES", OrderSide.Sell,
                    OrderType.Market, 3m, null, null, TimeInForce.Day, "somebody else"), FillBehaviour.FillImmediately);
            }
            return Task.CompletedTask;
        };

        var closes = conn.Closes;
        conn.Broker.PriceOffset = -20m;
        clock.Advance(Tick);
        await gw.RefreshHealthAsync();
        clock.Advance(Tick);
        await gw.LossWatchAsync();

        var leg = Assert.Single(gw.Requests.Query("request_id LIKE $p",
            ("$p", $"{TradingGateway.BudgetClosePress}-%")), r => r.Instrument == "ES");
        log.WriteLine($"landed {landed}; closes {closes} -> {conn.Closes}; ES {Held(conn, "ES")}; leg {leg.State} — {leg.LastError}");

        Assert.True(landed);
        Assert.Equal(closes, conn.Closes);
        Assert.Equal(0, conn.Places);
        Assert.Equal(-1m, Held(conn, "ES"));
        Assert.Equal(ExecutionState.REJECTED, leg.State);
        Assert.Contains("ADD", leg.LastError!, StringComparison.Ordinal);
    }

    /// <summary>THE VALUATION EXIT STILL CLOSES A POSITION NOBODY CAN VALUE in a refused armed home — a close, never a place.</summary>
    [Fact]
    public async Task In_a_refused_armed_home_the_valuation_exit_still_closes()
    {
        var (gw, conn, db, clock, raw) = await ArmedHome(exitAfterMinutes: 1m);
        using var _1 = db;
        await using var _2 = gw;

        await Ticks(gw, clock, 1);
        conn.Faults.QuoteAge = Silent;
        await Ticks(gw, clock, 4);
        log.WriteLine($"places {conn.Places}, closes {conn.Closes}; ES {Held(conn, "ES")}");

        Assert.Equal(0, conn.Places);
        Assert.True(conn.Closes >= 1);
        Assert.Equal(0m, Held(conn, "ES"));
        Assert.Equal(raw, db.GetKv("settings"));
        Assert.NotNull(gw.EarlierLiveSetting);
    }

    /// <summary>
    /// THE OWNER'S EMERGENCY PRESSES STILL GO OUT, AND THE REFUSAL SAYS SO: in a refused armed home his Cancel all working orders
    /// cancels the earlier build's resting order and his Close all positions closes its long, and the refusal's words name both
    /// presses — "sending nothing to your broker" would be untrue and would leave him believing his position is stranded.
    /// </summary>
    [Fact]
    public async Task In_a_refused_armed_home_the_owners_cancel_all_and_close_all_still_go_out()
    {
        var (gw, conn, db, _, raw) = await ArmedHome();
        using var _1 = db;
        await using var _2 = gw;
        var resting = conn.Broker.Accept(new PlaceOrderCommand("EARLIER-2", conn.Broker.AccountId, "ES", OrderSide.Buy,
            OrderType.Limit, 1m, conn.Broker.Quote("ES", Noon).Bid - 100m, null, TimeInForce.Day, "left resting"), FillBehaviour.LeaveWorking);

        var cancelled = await gw.OperatorCancelAllAsync();
        log.WriteLine($"cancel all: {cancelled}");
        Assert.Equal(ExecutionState.CANCELLED, conn.Broker.Orders.Single(o => o.ConnectorOrderId == resting.ConnectorOrderId).State);

        var closed = await gw.OperatorCloseAllAsync();
        log.WriteLine($"close all: {closed}");
        Assert.Equal(0m, Held(conn, "ES"));
        Assert.Equal(0, conn.Places);
        Assert.Equal(raw, db.GetKv("settings"));
        Assert.NotNull(gw.EarlierLiveSetting);

        // AND THE WORDS SAY SO, asked last so the presses above are proved whatever the words were.
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "opener", TestEnv.Buy("ES", 1m)));
        log.WriteLine($"{denied.Code}: {denied.Message}");
        Assert.Contains("Close all positions", denied.Message, StringComparison.Ordinal);
        Assert.Contains("Cancel all working orders", denied.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sending nothing", denied.Message, StringComparison.Ordinal);
        var info = Errors.Get(ErrorCode.HOME_ARMED_BEFORE_THE_BOARD);
        var plain = info.UserMessage + " " + info.Repair;
        log.WriteLine($"explained: {plain}");
        Assert.Contains("Close all positions", plain, StringComparison.Ordinal);
    }
}

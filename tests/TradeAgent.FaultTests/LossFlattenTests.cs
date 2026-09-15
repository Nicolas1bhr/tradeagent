using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// A CONFIRMED BREACH CLOSES WHAT IS OPEN, BY CODE, AND THE VERDICT IS THE BOOK.
///
/// <para><c>U-flatten-1</c> made a breach a durable record and sent nothing. This is the unit that
/// sends: an app-owned press kind, a reduction-only exception to two-press enforced before the wire,
/// and resolution by MACHINE — a leg is resolved when its close is terminal-and-filled AND the
/// position reads back flat, and anything else stays flagged and pauses order flow exactly as an
/// owner's press does.</para>
///
/// <para><b>Every press in this file takes <see cref="Unresolved.PressBudget"/>.</b> Every verdict
/// below is the book or a record, and everything between the instant the flatten opens its deadline
/// and the instant its leg goes out is durable SQLite at <c>synchronous=FULL</c> — one such commit
/// has measured 2234 ms on windows-latest, which is the whole of the simulator's two seconds
/// (<c>U-press-win-3</c>). Nothing here is loosened: the fixture whose verdict IS the deadline is
/// not in this file.</para>
/// </summary>
public class LossFlattenTests(ITestOutputHelper log)
{
    sealed class TestClock(DateTimeOffset at) : TimeProvider
    {
        DateTimeOffset _now = at;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    static readonly DateTimeOffset Noon = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    /// <summary>One tick of the watch's own interval, and then some: the gap between two pulls.</summary>
    static readonly TimeSpan Tick = TimeSpan.FromSeconds(20);

    static async Task<(TradingGateway Gw, RecordingConnector Conn, Database Db, TestClock Clock)> Ready(
        Action<TradeAgentSettings>? settings = null)
    {
        var clock = new TestClock(Noon);
        var db = TestEnv.NewDb();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker())
        {
            EmergencyBudget = Unresolved.PressBudget
        });
        var gw = new TradingGateway(db, conn, new HealthRegistry(), new GatewayOptions { Clock = clock });
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments];
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
            s.Risk.MaxDailyLoss = 1_000m;
            settings?.Invoke(s);
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        return (gw, conn, db, clock);
    }

    /// <summary>
    /// Two agreeing pulls through the daily budget, on the health pass every host already runs. The
    /// watch is never called by name here: a flatten that only happens when a test asks for it is
    /// not the product.
    /// </summary>
    static async Task Breach(TradingGateway gw, RecordingConnector conn, TestClock clock, decimal offset = -20m)
    {
        conn.Broker.PriceOffset = offset;
        clock.Advance(Tick);
        await gw.RefreshHealthAsync();
        clock.Advance(Tick);
        await gw.RefreshHealthAsync();
    }

    static decimal Held(RecordingConnector conn, string symbol) =>
        conn.Broker.Positions.FirstOrDefault(p => p.Symbol == symbol)?.Quantity ?? 0m;

    /// <summary>
    /// THE OPENERS GO FIRST, AND THE PROOF IS A RESTING ONE THAT FILLS AFTERWARDS (item 1).
    ///
    /// <para>A flatten that closes the position and leaves a working buy on the book has not
    /// flattened anything: the opener fills a second later and the account is long again, with the
    /// day already closed to every order that could have hedged it. So a confirmed daily breach
    /// cancels every working order that could increase exposure BEFORE any close is sent, and the
    /// cancels have to have SETTLED — an order still live at the platform is an order that can
    /// still fill.</para>
    ///
    /// <para>The fill here is the real book's behaviour and not a contrivance: the test fills the
    /// resting order by hand AFTER the flatten has finished, which is exactly what price arriving
    /// would do. A cancelled order cannot be filled, so the book stays flat.</para>
    /// </summary>
    [Fact]
    public async Task A_resting_opener_is_cancelled_before_the_flatten_sends_a_close()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        var account = conn.Broker.AccountId;

        await gw.PlaceAsync(new AgentContext("a"), "open-es", TestEnv.Buy("ES", 2m));

        // A RESTING OPENER: a buy the book takes and does not fill.
        conn.Faults.Fill = FillBehaviour.LeaveWorking;
        await gw.PlaceAsync(new AgentContext("a"), "opener", new PlaceIntent("ES", OrderSide.Buy,
            OrderType.Limit, 1m, conn.Broker.Quote("ES", Noon).Bid - 100m, null, TimeInForce.Day, null));
        conn.Faults.Fill = FillBehaviour.FillImmediately;
        var opener = conn.Broker.Orders.Single(o => o.State == ExecutionState.WORKING).ConnectorOrderId;
        log.WriteLine($"resting opener        : {opener}");

        await Breach(gw, conn, clock);
        Assert.NotNull(gw.DayClosed(account));

        log.WriteLine($"book after the flatten: [{string.Join(" | ", conn.Broker.Orders.Select(o => $"{o.ConnectorOrderId} {o.Side} {o.Quantity} {o.State}"))}]");
        log.WriteLine($"position              : ES {Held(conn, "ES")}");

        // THE OPENER FILLS AFTER THE FLATTEN, which is what a book does when price arrives.
        conn.Broker.FillWorking(opener);
        log.WriteLine($"after the opener fills: ES {Held(conn, "ES")}");

        Assert.Equal(0m, Held(conn, "ES"));

        await gw.DisposeAsync();
    }

    /// <summary>
    /// A CANCEL THAT DID NOT TAKE STOPS THE CLOSES (item 1, the settle).
    ///
    /// <para>"The cancel call returned" is a statement about a round trip. What the next step needs
    /// is that the order can no longer fill, and only the platform can say that — so the working book
    /// is read again and every target has to be gone from it. An order the platform still lists as
    /// working is an order that can still fill, and a close sent underneath one leaves the account
    /// open a second later, which is the failure the cancel exists to prevent arrived at from the
    /// other side.</para>
    ///
    /// <para>So nothing is closed at all: the positions are where they were, the record says which
    /// order could not be stopped, and the flagged rows pause order flow until a person looks. That is
    /// strictly worse than a flat book and strictly better than a reversed one.</para>
    /// </summary>
    [Fact]
    public async Task No_close_is_sent_while_an_opening_order_is_still_working_at_the_platform()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        var account = conn.Broker.AccountId;

        await gw.PlaceAsync(new AgentContext("a"), "open-es", TestEnv.Buy("ES", 2m));

        conn.Faults.Fill = FillBehaviour.LeaveWorking;
        await gw.PlaceAsync(new AgentContext("a"), "opener", new PlaceIntent("ES", OrderSide.Buy,
            OrderType.Limit, 1m, conn.Broker.Quote("ES", Noon).Bid - 100m, null, TimeInForce.Day, null));
        conn.Faults.Fill = FillBehaviour.FillImmediately;

        // THE PLATFORM REFUSES THE CANCEL — definitely, which is the platform saying the order is
        // still live. Anything ambiguous would be worse news and gets the same answer.
        conn.Faults.RefuseCancel = 5;

        var closes = conn.Closes;
        await Breach(gw, conn, clock);

        var flatten = gw.FlattenToday(account);
        Assert.NotNull(flatten);
        log.WriteLine($"openers not settled   : {string.Join("; ", flatten.OpenersNotSettled)}");
        log.WriteLine($"why                   : {flatten.Why}");
        log.WriteLine($"closes on the wire    : {closes} -> {conn.Closes}");
        log.WriteLine($"position              : ES {Held(conn, "ES")}");

        // NOTHING WAS CLOSED, and the record says which order stopped it.
        Assert.Equal(closes, conn.Closes);
        Assert.Empty(flatten.Legs);
        Assert.False(flatten.Flat);
        Assert.NotEmpty(flatten.OpenersNotSettled);
        Assert.Equal(2m, Held(conn, "ES"));

        // AND ORDER FLOW IS PAUSED, exactly as an owner's press pauses it.
        Assert.True(gw.HasUnconfirmedWork());

        await gw.DisposeAsync();
    }

    /// <summary>
    /// TWO OPEN POSITIONS, A CONFIRMED BREACH, AND THE BOOK IS FLAT (item 2).
    ///
    /// <para>The legs go out through the SAME method the owner's Close all positions button uses —
    /// <c>RiskReducingScope</c>, the composite plan, the settle-before-send, the drift re-read, the
    /// flagged write-ahead row, <c>ClosePositionAsync</c> — under the app's own press kind. Every one
    /// of those steps is a safety property that cost a review round to get right, and a second copy of
    /// the sequence would be a second place for one of them to go missing.</para>
    ///
    /// <para>The caller's answer does not change: <c>LOSS_BUDGET_REACHED</c> is still what an order
    /// that could increase exposure is refused with, and it is still refused off the record rather
    /// than off the figure.</para>
    /// </summary>
    [Fact]
    public async Task A_confirmed_daily_breach_closes_every_open_position_and_the_book_reads_flat()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        var account = conn.Broker.AccountId;

        await gw.PlaceAsync(new AgentContext("a"), "es", TestEnv.Buy("ES", 2m));
        await gw.PlaceAsync(new AgentContext("a"), "nq", TestEnv.Buy("NQ", 1m));

        var closes = conn.Closes;
        await Breach(gw, conn, clock);

        var flatten = gw.FlattenToday(account);
        Assert.NotNull(flatten);
        log.WriteLine($"why                   : {flatten.Why}");
        foreach (var leg in flatten.Legs)
            log.WriteLine($"leg                   : {leg.RequestId} {leg.Symbol} captured {leg.Captured} -> {leg.State} filled {leg.Filled}, position now {leg.PositionAfter}, resolved {leg.Resolved}");
        log.WriteLine($"book                  : [{string.Join(" | ", conn.Broker.Positions.Select(p => $"{p.Symbol} {p.Quantity}"))}]");

        Assert.Equal(closes + 2, conn.Closes);
        Assert.Equal(2, flatten.Legs.Count);
        Assert.True(flatten.Flat);
        Assert.Empty(flatten.Residual);
        Assert.Equal(0m, Held(conn, "ES"));
        Assert.Equal(0m, Held(conn, "NQ"));

        // EVERY LEG RAN UNDER THE APP'S OWN KIND, never the owner's. An unresolved app flatten must
        // not be able to refuse a person pressing Close all positions.
        Assert.All(flatten.Legs, l => Assert.StartsWith(TradingGateway.BudgetClosePress + "-", l.RequestId, StringComparison.Ordinal));
        Assert.Empty(gw.Requests.Query("request_id LIKE $p", ("$p", $"{TradingGateway.ClosePress}-%")));

        // AND LOSS_BUDGET_REACHED IS STILL THE CALLER'S ANSWER.
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "after", TestEnv.Buy("ES")));
        log.WriteLine($"caller's answer       : {denied.Code}");
        Assert.Equal(ErrorCode.LOSS_BUDGET_REACHED, denied.Code);

        await gw.DisposeAsync();
    }

    /// <summary>
    /// A LEG THAT WOULD REVERSE THE POSITION THROWS BEFORE THE WIRE (item 2, the reduction-only
    /// check).
    ///
    /// <para>The drift re-read happens BEFORE the flagged write-ahead row, and that row is a durable
    /// commit — one has measured 2234 ms on windows-latest. A fill can land inside that window, and
    /// the order this flatten composed was sized against the reading from before it: a sell of 2
    /// against a long that is now 1 does not close anything, it opens a short 1. The owner's press
    /// closes that window with a person, who can look and press again; an app leg has nobody, so it
    /// reads the position one last time immediately before the call and refuses.</para>
    ///
    /// <para>The seam is what makes the window observable: it fires on the position read the leg
    /// makes AFTER its own row exists — which is exactly the last read before the wire — and lands an
    /// external fill first. That is a real book doing a real thing, not a contrivance.</para>
    ///
    /// <para><b>What is asserted is the wire and the row.</b> The simulator's own
    /// <c>ClosePositionAsync</c> re-reads the position and sizes itself, so a reversal cannot be
    /// produced through it at all; the verdict that can be stated here is that no close reached the
    /// connector and the record says why. <see cref="TradeAgent.Tests.Fault.ReductionOnlyTests"/> is
    /// the arithmetic itself.</para>
    /// </summary>
    [Fact]
    public async Task A_position_that_shrinks_before_the_wire_is_not_closed_at_the_size_that_was_captured()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        var account = conn.Broker.AccountId;

        await gw.PlaceAsync(new AgentContext("a"), "es", TestEnv.Buy("ES", 2m));

        // AN EXTERNAL FILL LANDS BETWEEN THE ROW AND THE WIRE. The leg's own row is what says the
        // flatten has got that far, so the first position read after it exists is the last read
        // before the call.
        var landed = false;
        conn.Seam = kind =>
        {
            if (kind == RecordingConnector.HeldCall.Positions && !landed
                && gw.Requests.Query("request_id LIKE $p", ("$p", $"{TradingGateway.BudgetClosePress}-%"))
                    .Any(r => r.Instrument == "ES"))
            {
                landed = true;
                conn.Broker.Accept(new PlaceOrderCommand("EXT-1", account, "ES", OrderSide.Sell,
                    OrderType.Market, 1m, null, null, TimeInForce.Day, "somebody else"), FillBehaviour.FillImmediately);
            }
            return Task.CompletedTask;
        };

        var closes = conn.Closes;
        await Breach(gw, conn, clock);

        var flatten = gw.FlattenToday(account);
        Assert.NotNull(flatten);
        log.WriteLine($"external fill landed  : {landed}");
        log.WriteLine($"closes on the wire    : {closes} -> {conn.Closes}");
        log.WriteLine($"position              : ES {Held(conn, "ES")}");
        log.WriteLine($"why                   : {flatten.Why}");

        var leg = Assert.Single(gw.Requests.Query("request_id LIKE $p",
            ("$p", $"{TradingGateway.BudgetClosePress}-%")).Where(r => r.Instrument == "ES"));
        log.WriteLine($"leg row               : {leg.RequestId} {leg.State} — {leg.LastError}");

        Assert.True(landed);
        Assert.Equal(closes, conn.Closes);                       // NOTHING reached the wire
        Assert.Equal(1m, Held(conn, "ES"));                      // and the position is not reversed
        Assert.Equal(ExecutionState.REJECTED, leg.State);
        Assert.Contains("REVERSE", leg.LastError!, StringComparison.Ordinal);
        Assert.False(flatten.Flat);
        Assert.Contains("ES 1", flatten.Residual);
        Assert.True(gw.HasUnconfirmedWork());

        await gw.DisposeAsync();
    }
}

/// <summary>
/// THE ARITHMETIC OF THE REDUCTION-ONLY EXCEPTION, on its own, because it is the one rule that says
/// what an app-owned press may put on a broker.
///
/// <para>Two-press is what makes every other money-moving act in this product a person's. The loss
/// budget's flatten is the single exception, and it is bounded by this method and nothing else: the
/// order must OPPOSE the position and be at most its size. Everything outside that — a same-side
/// order, an order larger than the position, an order against a flat book — is a new position
/// arrived at by a different name, and there is no person between this line and the broker.</para>
/// </summary>
public class ReductionOnlyTests(ITestOutputHelper log)
{
    static PlaceIntent Close(OrderSide side, decimal qty) =>
        new("ES", side, OrderType.Market, qty, null, null, TimeInForce.Day, "close position (loss budget)")
        { Intent = OrderIntent.Close };

    [Fact]
    public void A_close_that_opposes_the_position_and_is_at_most_its_size_is_allowed()
    {
        TradingGateway.ReductionOnlyOrThrow(Close(OrderSide.Sell, 2m), 2m);      // exactly flat
        TradingGateway.ReductionOnlyOrThrow(Close(OrderSide.Sell, 1m), 2m);      // a partial reduce
        TradingGateway.ReductionOnlyOrThrow(Close(OrderSide.Buy, 2m), -2m);      // the short side
        log.WriteLine("three reductions allowed, nothing thrown");
    }

    [Theory]
    [InlineData("Sell", 3, 2, "REVERSE")]        // larger than the long it is against
    [InlineData("Buy", 3, -2, "REVERSE")]        // larger than the short
    [InlineData("Buy", 1, 2, "ADD")]             // the same side as the position
    [InlineData("Sell", 1, -2, "ADD")]
    [InlineData("Sell", 1, 0, "OPEN")]           // nothing to reduce
    public void Anything_that_is_not_a_reduction_throws_before_the_wire(string side, int qty, int live, string says)
    {
        var intent = Close(side == "Buy" ? OrderSide.Buy : OrderSide.Sell, qty);
        var denied = Assert.Throws<GatewayDeniedException>(() =>
            TradingGateway.ReductionOnlyOrThrow(intent, live));
        log.WriteLine($"{side} {qty} against {live}: {denied.Message}");
        Assert.Equal(ErrorCode.INVALID_REQUEST, denied.Code);
        Assert.Contains(says, denied.Message, StringComparison.Ordinal);
        Assert.Contains("nothing was sent", denied.Message, StringComparison.Ordinal);
    }

    /// <summary>An order the composer did not even declare a close is refused on that alone.</summary>
    [Fact]
    public void An_order_that_is_not_declared_a_close_is_refused_whatever_the_position_is()
    {
        var opener = new PlaceIntent("ES", OrderSide.Sell, OrderType.Market, 1m, null, null, TimeInForce.Day, null);
        var denied = Assert.Throws<GatewayDeniedException>(() =>
            TradingGateway.ReductionOnlyOrThrow(opener, 2m));
        log.WriteLine($"undeclared            : {denied.Message}");
        Assert.Contains("not declared a close", denied.Message, StringComparison.Ordinal);
    }
}

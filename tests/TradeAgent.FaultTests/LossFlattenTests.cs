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
}

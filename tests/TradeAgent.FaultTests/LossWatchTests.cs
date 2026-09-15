using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// THE BOOK IS MEASURED WHETHER OR NOT ANYBODY SENDS AN ORDER.
///
/// <para>Every loss figure in this product used to be computed inside the dispatch gate, which means
/// the budgets only ever asked their question when an order arrived to ask it of. An AI that has
/// stopped sending — thinking, out of AI budget, crashed, or simply right to wait — leaves an open
/// position that can go through the owner's daily budget and keep going, and nothing in the software
/// would have looked. What is measured here is the tick that looks: a fresh pull per open symbol, a
/// valuation off the executable side of it, and a durable closure when a SECOND distinct pull agrees.
/// </para>
///
/// <para><b>What the wire does is asserted in the same tests, and it changed with
/// <c>U-flatten-2</c>.</b> Until that unit a confirmed breach sent nothing at all and these tests said
/// so. It now closes what is open, by code, under the app's own press kind — so what is asserted here
/// is the number of closes, which symbol they were for, and the position read back after them. The
/// measurement this class is about is unchanged: the pulls, the confirmation window and the record are
/// exactly what they were.</para>
/// </summary>
public class LossWatchTests(ITestOutputHelper log)
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
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker()));
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
            settings?.Invoke(s);
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        return (gw, conn, db, clock);
    }

    /// <summary>
    /// A DAY NOBODY SENT AN ORDER INTO IS CLOSED BY THE WATCH (item 2), AND NOTHING IS SENT (the
    /// unit's standing claim).
    ///
    /// <para>One position is opened, the market drops through the budget, and then the AI does
    /// nothing at all — no order, no status call, nothing. Two passes of the health tick later the
    /// day is closed on a record carrying the evidence that closed it, and the next order is refused
    /// off that record. The passes are the ones every host already runs; nothing here calls the
    /// watch by name, because the watch has to happen without anybody remembering to ask for it.
    /// </para>
    ///
    /// <para>The mark is the BID, because the position is long and a long is worth what it can be
    /// sold at. That is stricter than the mid <c>trade pnl</c> reports and it is the right direction
    /// for a gate: the owner's budget is about what closing the book would cost.</para>
    /// </summary>
    [Fact]
    public async Task A_book_that_goes_through_the_budget_with_no_order_arriving_closes_the_day()
    {
        var (gw, conn, db, clock) = await Ready(s => s.Risk.MaxDailyLoss = 1_000m);
        using var _1 = db;

        var account = conn.Broker.AccountId;
        await gw.PlaceAsync(new AgentContext("a"), "watch-open", TestEnv.Buy("ES"));
        conn.Broker.PriceOffset = -20m;

        var mutations = conn.Mutations;
        var orders = conn.Broker.Orders.Count;

        // PULL ONE. The drop is seen, and one sighting closes nothing.
        clock.Advance(Tick);
        await gw.RefreshHealthAsync();
        log.WriteLine($"after one pass        : closed={gw.DayClosed(account) is not null}");
        Assert.Null(gw.DayClosed(account));

        // PULL TWO, a distinct pull inside the confirmation window, and it agrees.
        clock.Advance(Tick);
        await gw.RefreshHealthAsync();

        var rec = gw.DayClosed(account);
        Assert.NotNull(rec);
        log.WriteLine($"record                : loss={rec.Loss} budget={rec.DayBudget} {rec.Currency}");
        log.WriteLine($"pulls                 : first={rec.FirstPull} confirming={rec.ConfirmingPull}");
        log.WriteLine($"marks                 : {string.Join("; ", rec.Marks.Select(m => $"{m.Symbol} {m.Side} {m.Source} {m.Mark} age {m.AgeSeconds:0.0}s -> {m.Value}"))}");
        log.WriteLine($"why                   : {rec.Why}");

        // THE EVIDENCE, and the two pulls are on it so that "a second distinct pull agreed" is
        // checkable rather than asserted.
        Assert.True(rec.Loss >= 1_000m);
        Assert.Equal(1_000m, rec.DayBudget);
        Assert.Equal(LossBreach.Stamp(Noon), rec.Day);
        Assert.NotEqual(rec.FirstPull, rec.ConfirmingPull);
        Assert.True(rec.ConfirmedAt > rec.FirstSeenAt);
        Assert.NotEqual("", rec.SettingsRevision);
        var mark = Assert.Single(rec.Marks);
        Assert.Equal("ES", mark.Symbol);
        Assert.Equal("long", mark.Side);
        Assert.Equal("bid", mark.Source);        // the executable side, not the mid
        Assert.Equal(conn.Broker.Quote("ES", Noon).Bid, mark.Mark);
        // The sentence states the RULE, and U-flatten-2 reversed the rule: it used to promise
        // "NOTHING WAS CLOSED FOR YOU" and now says the opposite, because the app closes the book.
        Assert.Contains("CLOSES YOUR OPEN POSITIONS", rec.Why, StringComparison.Ordinal);

        // THE WIRE. ONE close, for the one open position, and the book reads flat afterwards
        // (U-flatten-2; until that unit this asserted that nothing was sent at all).
        log.WriteLine($"mutations before/after: {mutations}/{conn.Mutations}, orders {orders}/{conn.Broker.Orders.Count}");
        Assert.Equal(1, conn.Closes);
        Assert.Equal(mutations + 1, conn.Mutations);
        Assert.Equal(orders + 1, conn.Broker.Orders.Count);
        Assert.Equal(0m, conn.Broker.Positions.FirstOrDefault(p => p.Symbol == "ES")?.Quantity ?? 0m);

        var flatten = gw.FlattenToday(account);
        Assert.NotNull(flatten);
        log.WriteLine($"flatten               : flat={flatten.Flat} {flatten.Why}");
        Assert.True(flatten.Flat);

        // AND THE NEXT ORDER IS REFUSED OFF THE RECORD — nothing more reaches the wire for it.
        var afterFlatten = conn.Mutations;
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "after-watch", TestEnv.Buy("NQ")));
        Assert.Equal(ErrorCode.LOSS_BUDGET_REACHED, denied.Code);
        Assert.Equal(afterFlatten, conn.Mutations);

        await gw.DisposeAsync();
    }

    /// <summary>
    /// ONE BAD PRINT COSTS AN ORDER AND NOT A DAY (item 2, the confirmation).
    ///
    /// <para>The two decisions are deliberately asymmetric. An order arriving on a single print
    /// through the budget is refused on that print with no tolerance at all, because a refusal costs
    /// nothing and the next print undoes it. Closing the DAY is the opposite: it lasts until the next
    /// UTC day, nothing undoes it, and the agent's whole session is over. So a closure takes two
    /// pulls that agree, and a pull that disagrees drops the sighting on the spot.</para>
    ///
    /// <para>This is what makes the second read have to be a PULL. A confirmation taken from the
    /// quote map would be the first print read twice — the same number, agreeing with itself.</para>
    /// </summary>
    [Fact]
    public async Task A_single_print_through_the_budget_that_the_next_pull_disagrees_with_closes_nothing()
    {
        var (gw, conn, db, clock) = await Ready(s => s.Risk.MaxDailyLoss = 1_000m);
        using var _1 = db;

        var account = conn.Broker.AccountId;
        await gw.PlaceAsync(new AgentContext("a"), "print-open", TestEnv.Buy("ES"));

        // THE BAD PRINT: one pull sees the book far through the budget.
        conn.Broker.PriceOffset = -20m;
        clock.Advance(Tick);
        var first = await gw.LossWatchAsync();
        log.WriteLine($"pull {first.Pull}: day reached {first.DayReached}, closed [{string.Join(",", first.Closed)}]");
        Assert.True(first.DayReached);
        Assert.Empty(first.Closed);

        // AND THE PRINT IS GONE. The next pull values the same book at a price that is not through
        // the budget, and the sighting goes with it.
        conn.Broker.PriceOffset = -1m;
        clock.Advance(Tick);
        var second = await gw.LossWatchAsync();
        log.WriteLine($"pull {second.Pull}: day reached {second.DayReached}, closed [{string.Join(",", second.Closed)}]");

        Assert.False(second.DayReached);
        Assert.Empty(second.Closed);
        Assert.Null(gw.DayClosed(account));

        // A THIRD PULL BACK THROUGH THE BUDGET IS A FIRST SIGHTING AGAIN, not the old one resumed.
        conn.Broker.PriceOffset = -20m;
        clock.Advance(Tick);
        var third = await gw.LossWatchAsync();
        Assert.True(third.DayReached);
        Assert.Empty(third.Closed);
        Assert.Null(gw.DayClosed(account));

        // The order that arrives meanwhile is still refused on first sight, with no tolerance: the
        // admission gate does not wait for a confirmation and does not record one either.
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "on-the-print", TestEnv.Buy("NQ")));
        log.WriteLine($"admission gate        : {denied.Code}");
        Assert.Equal(ErrorCode.LOSS_BUDGET_REACHED, denied.Code);
        Assert.Null(gw.DayClosed(account));

        await gw.DisposeAsync();
    }

    /// <summary>
    /// A BOOK THAT CAN ONLY BE VALUED AT A PRICE TOO OLD TO USE CLOSES NOTHING (item 2, the mark).
    ///
    /// <para>A mark has to be younger than <c>MaxQuoteAge</c> — the same bound the order-value check
    /// and the market-data health row use. An old price is not a small error here: it is the price
    /// before whatever moved, which is exactly the move being measured. The safe half of an
    /// unavailable valuation already exists, in the admission gate, which refuses new risk with
    /// RISK_CHECK_UNAVAILABLE; the unsafe half would be inventing a closure out of it, and this is
    /// the test that says the watch does not.</para>
    ///
    /// <para>The watch is STRICTER about a mark than the admission gate is, deliberately. The gate
    /// refuses one order and the next price undoes it; the watch writes a day nothing undoes, so it
    /// takes the executable side, of a quote young enough to use, from the connection it is on, and
    /// otherwise records nothing at all.</para>
    /// </summary>
    [Fact]
    public async Task A_book_valued_only_by_a_stale_price_records_nothing_and_says_why()
    {
        var (gw, conn, db, clock) = await Ready(s => s.Risk.MaxDailyLoss = 1_000m);
        using var _1 = db;

        var account = conn.Broker.AccountId;
        await gw.PlaceAsync(new AgentContext("a"), "stale-open", TestEnv.Buy("ES"));

        conn.Broker.PriceOffset = -20m;
        conn.Faults.QuoteAge = TimeSpan.FromMinutes(5);   // every quote arrives already too old to use

        for (var i = 0; i < 3; i++)
        {
            clock.Advance(Tick);
            var pass = await gw.LossWatchAsync();
            log.WriteLine($"pull {pass.Pull}: ran={pass.Ran} closed=[{string.Join(",", pass.Closed)}] why={pass.Why}");
            Assert.Empty(pass.Closed);
            Assert.NotNull(pass.Why);
        }

        Assert.Null(gw.DayClosed(account));

        // And the order that arrives while nothing can be priced is refused too — by the check that
        // needs a price to size it. Which gate answers first is not this test's business; that
        // NOTHING was recorded is.
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "stale-place", TestEnv.Buy("NQ")));
        log.WriteLine($"admission gate        : {denied.Code} — {denied.Message}");
        Assert.Null(gw.DayClosed(account));

        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE PER-POSITION BUDGET IS WATCHED TOO, and it closes ONE symbol.
    ///
    /// <para>Two positions, one of them losing. The loser's own budget is reached and its symbol is
    /// closed to opens and adds for the day; the other position is not the one that lost and is not
    /// closed. The day's budget is off throughout, so what is measured is this record and not the
    /// other one.</para>
    /// </summary>
    [Fact]
    public async Task The_watch_closes_the_symbol_that_breached_and_leaves_the_rest_of_the_book_open()
    {
        var (gw, conn, db, clock) = await Ready(s =>
        {
            s.Risk.MaxLossPerTrade = 500m;
            s.Risk.MaxDailyLoss = 0m;
        });
        using var _1 = db;

        var account = conn.Broker.AccountId;
        await gw.PlaceAsync(new AgentContext("a"), "sym-es", TestEnv.Buy("ES", 2m));
        await gw.PlaceAsync(new AgentContext("a"), "sym-ym", TestEnv.Buy("YM"));

        // ES is down 2 x 20 x 50; YM is down 20 x 5. Only one of them reaches a 500 budget.
        conn.Broker.PriceOffset = -20m;

        var mutations = conn.Mutations;
        clock.Advance(Tick);
        var first = await gw.LossWatchAsync();
        clock.Advance(Tick);
        var second = await gw.LossWatchAsync();

        log.WriteLine($"reached               : [{string.Join(",", second.SymbolsReached)}]");
        log.WriteLine($"closed                : [{string.Join(",", second.Closed)}]");
        Assert.Empty(first.Closed);

        Assert.Equal(new[] { "ES" }, gw.SymbolsClosedToday(account));
        Assert.Null(gw.DayClosed(account));       // the day's budget is off; nothing closed the day
        Assert.NotNull(gw.SymbolClosed(account, "ES"));
        Assert.Null(gw.SymbolClosed(account, "YM"));

        var es = gw.SymbolClosed(account, "ES")!;
        log.WriteLine($"why                   : {es.Why}");
        Assert.Equal("ES", es.Symbol);
        Assert.Equal(500m, es.TradeBudget);
        Assert.True(es.Loss >= 500m);

        // THE WIRE AGAIN, AND THE SCOPE IS THE SYMBOL. ES is closed by the app and reads flat; YM
        // breached nothing and is exactly where it was (U-flatten-2; this asserted that a breached
        // position is not closed for anybody until that unit).
        log.WriteLine($"mutations before/after: {mutations}/{conn.Mutations}, closes {conn.Closes}");
        Assert.Equal(1, conn.Closes);
        Assert.Equal(mutations + 1, conn.Mutations);
        Assert.Equal(0m, conn.Broker.Positions.FirstOrDefault(p => p.Symbol == "ES")?.Quantity ?? 0m);
        Assert.Equal(1m, conn.Broker.Positions.First(p => p.Symbol == "YM").Quantity);
        Assert.True(gw.FlattenToday(account, "ES")!.Flat);
        Assert.Null(gw.FlattenToday(account, "YM"));

        // ES is refused; YM is not.
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "es-add", TestEnv.Buy("ES")));
        Assert.Equal(ErrorCode.LOSS_BUDGET_REACHED, denied.Code);
        var ym = await gw.PlaceAsync(new AgentContext("a"), "ym-more", TestEnv.Buy("YM"));
        Assert.Equal(ExecutionState.FILLED, ym.State);

        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE RECORD IS WRITTEN ONCE. A third and fourth agreeing pull change nothing: not the figure,
    /// not the instant, not the pull numbers. A record that could be rewritten would carry whatever
    /// the last pass happened to see, which is the ledger's job and not this one's.
    /// </summary>
    [Fact]
    public async Task A_closure_already_written_is_never_rewritten_by_a_later_pull()
    {
        var (gw, conn, db, clock) = await Ready(s => s.Risk.MaxDailyLoss = 1_000m);
        using var _1 = db;

        var account = conn.Broker.AccountId;
        await gw.PlaceAsync(new AgentContext("a"), "once-open", TestEnv.Buy("ES"));
        conn.Broker.PriceOffset = -20m;

        clock.Advance(Tick);
        await gw.LossWatchAsync();
        clock.Advance(Tick);
        await gw.LossWatchAsync();

        var written = gw.DayClosed(account);
        Assert.NotNull(written);

        conn.Broker.PriceOffset = -60m;           // three times as bad, twice more
        for (var i = 0; i < 2; i++) { clock.Advance(Tick); await gw.LossWatchAsync(); }

        var now = gw.DayClosed(account)!;
        log.WriteLine($"first  : {written.Loss} at {written.ConfirmedAt:HH:mm:ss} pulls {written.FirstPull}/{written.ConfirmingPull}");
        log.WriteLine($"later  : {now.Loss} at {now.ConfirmedAt:HH:mm:ss} pulls {now.FirstPull}/{now.ConfirmingPull}");
        // Compared as the row, because that is what "never updated" is about — and because a record
        // holding a list compares by reference otherwise.
        Assert.Equal(Json.Write(written), Json.Write(now));

        await gw.DisposeAsync();
    }
}

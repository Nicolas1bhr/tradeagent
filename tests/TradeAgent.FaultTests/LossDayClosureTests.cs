using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// THE DAY IS CLOSED BY A RECORD, NOT BY AN ARITHMETIC THAT HAPPENS TO COME OUT THE SAME WAY TWICE.
///
/// <para>Both loss budgets used to be evaluated only when an order arrived, and a breach was a
/// property of that evaluation and of nothing else. So the day reopened three ways, none of them
/// anybody's decision: close the loser and the realised figure is smaller than the unrealised one
/// was; restart and the whole memory of it was a field that stops a log line repeating; widen the
/// budget and the same figure is under it. What is measured here is that a CONFIRMED breach is a
/// durable fact — and that the fact outranks every later reading of the ledger.</para>
///
/// <para>The record is written here by the test, exactly as the watcher writes it, because THIS is
/// the unit of "the refusal comes off the record". That the watcher writes one at all, and only on a
/// second agreeing pull, is measured next door in <c>LossWatchTests</c>.</para>
/// </summary>
public class LossDayClosureTests(ITestOutputHelper log)
{
    /// <summary>
    /// A clock the test owns. Fixed rather than started at <c>UtcNow</c>, because two of these tests
    /// are about which DAY a record belongs to, and a suite that ran at 23:59 would otherwise decide
    /// that for itself.
    /// </summary>
    sealed class TestClock(DateTimeOffset at) : TimeProvider
    {
        DateTimeOffset _now = at;
        public override DateTimeOffset GetUtcNow() => _now;
        public void MoveTo(DateTimeOffset to) => _now = to;
    }

    /// <summary>22:30 UTC on a day whose local date, in a timezone east of UTC, has already rolled.</summary>
    static readonly DateTimeOffset Evening = new(2026, 3, 10, 22, 30, 0, TimeSpan.Zero);

    static async Task<(TradingGateway Gw, RecordingConnector Conn, Database Db)> Ready(
        TimeProvider? clock = null, Action<TradeAgentSettings>? settings = null)
    {
        var db = TestEnv.NewDb();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker()));
        var gw = new TradingGateway(db, conn, new HealthRegistry(),
            clock is null ? null : new GatewayOptions { Clock = clock });
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
        return (gw, conn, db);
    }

    /// <summary>The record as the watcher writes it, with the evidence that closed the day.</summary>
    static LossBreachRecord Breach(string account, DateTimeOffset at, decimal loss, decimal budget,
        string? symbol = null) => new()
        {
            Account = account,
            Day = LossBreach.Stamp(at),
            Symbol = symbol,
            FirstSeenAt = at - TimeSpan.FromSeconds(15),
            ConfirmedAt = at,
            FirstPull = 1,
            ConfirmingPull = 2,
            Loss = loss,
            DayBudget = symbol is null ? budget : 0m,
            TradeBudget = symbol is null ? 0m : budget,
            Currency = "USD",
            Realized = 0m,
            Unrealized = -loss,
            Why = symbol is null
                ? $"today was closed to new risk at {at.UtcDateTime:HH:mm} UTC, down {loss} of a {budget} "
                  + "daily budget. Closing or reducing a position is still allowed."
                : $"{symbol} was closed to new risk at {at.UtcDateTime:HH:mm} UTC, down {loss} of a {budget} "
                  + "budget for one position. Closing or reducing it is still allowed."
        };

    static void Write(Database db, string key, LossBreachRecord rec) => db.SetKv(key, Json.Write(rec));

    /// <summary>
    /// THE CLOSE THAT USED TO REOPEN THE DAY (item 1).
    ///
    /// <para>The shape is the ordinary one and that is what makes it dangerous: an agent is down a
    /// thousand on an open position, the budget refuses it, it closes the position at a price that
    /// is nineteen points down instead of twenty — and the realised loss it is left with is SMALLER
    /// than the unrealised loss that closed the day. Evaluated afresh, the next order passes. The
    /// day the budget fired is the day it must not.</para>
    ///
    /// <para>The wire is what is asserted, both ways: nothing is placed, and the close in the middle
    /// of it goes out, because a budget an account cannot be flattened out of is a trap.</para>
    /// </summary>
    [Fact]
    public async Task A_loser_closed_under_the_budget_does_not_reopen_a_day_the_record_has_closed()
    {
        var (gw, conn, db) = await Ready(new TestClock(Evening), s => s.Risk.MaxDailyLoss = 1_000m);
        using var _1 = db;

        await gw.PlaceAsync(new AgentContext("a"), "close-open", TestEnv.Buy("ES"));
        conn.Broker.PriceOffset = -20m;  // 1 x 20 x the ES multiplier of 50 = 1,000 down, unrealised

        // The simulator raises a quote only inside a pull, and a stale price values nothing: this is
        // the read that makes the drop visible to the gateway at all, and it is why the watcher of
        // item 2 pulls rather than waiting to be told.
        await gw.QuoteAsync("ES");

        var live = await gw.LossTodayAsync();
        log.WriteLine($"unrealised, live      : {live.Loss} of {live.DayBudget}, day reached {live.DayReached}");
        Assert.True(live.DayReached);

        Write(db, LossBreach.DayKey(conn.Broker.AccountId, Evening),
            Breach(conn.Broker.AccountId, Evening, 1_000m, 1_000m));

        // THE WAY OUT IS NOT SHUT. The position that closed the day is closed after it, on the
        // record, and it reaches the wire.
        conn.Broker.PriceOffset = -19m;
        var closed = await gw.CloseAsync(new AgentContext("a"), "close-out", "ES");
        Assert.Equal(ExecutionState.FILLED, closed!.State);
        Assert.Equal(0m, conn.Broker.Positions.FirstOrDefault(p => p.Symbol == "ES")?.Quantity ?? 0m);

        await gw.QuoteAsync("ES");
        var after = await gw.LossTodayAsync();
        log.WriteLine($"realised after the close: {after.Loss} of {after.DayBudget}, day reached {after.DayReached}");
        Assert.False(after.DayReached);   // 950 realised is UNDER the 1,000 budget — the old reopening

        var places = conn.Places;
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "after-close", TestEnv.Buy("NQ")));

        log.WriteLine($"refusal               : {denied.Code} — {denied.Message}");
        log.WriteLine($"places before/after   : {places}/{conn.Places}");

        Assert.Equal(ErrorCode.LOSS_BUDGET_REACHED, denied.Code);
        Assert.Contains("closed to new risk", denied.Message, StringComparison.Ordinal);
        Assert.Equal(places, conn.Places);
        Assert.Null(gw.GetRequest("after-close"));

        // AND A WIDENED BUDGET IS NOT A REOPENED DAY EITHER — not even the widest widening there is,
        // which is the one that turns the limit off.
        gw.Update(s => s.Risk.MaxDailyLoss = 0m);
        var stillDenied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "after-widening", TestEnv.Buy("NQ")));
        Assert.Equal(ErrorCode.LOSS_BUDGET_REACHED, stillDenied.Code);
        Assert.Equal(places, conn.Places);

        await gw.DisposeAsync();
    }

    /// <summary>
    /// ONE SYMBOL'S CLOSURE SHUTS THAT SYMBOL AND NOTHING ELSE (item 1, the per-position half).
    ///
    /// <para>Averaging down into a loser is what the per-position budget exists for, and the re-entry
    /// is the same act with a gap in the middle: sell the loser, buy it again, and every order is
    /// inside every limit. The record is per <c>{account}:{symbol}:{utcDay}</c>, so the day's other
    /// instruments are untouched — a symbol budget that closed the account would be a day budget
    /// nobody asked for.</para>
    /// </summary>
    [Fact]
    public async Task A_symbol_closed_for_the_day_refuses_a_re_entry_and_leaves_every_other_symbol_open()
    {
        var (gw, conn, db) = await Ready(new TestClock(Evening), s => s.Risk.MaxLossPerTrade = 500m);
        using var _1 = db;

        var account = conn.Broker.AccountId;
        Write(db, LossBreach.SymbolKey(account, "ES", Evening),
            Breach(account, Evening, 600m, 500m, symbol: "ES"));

        var places = conn.Places;
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "es-again", TestEnv.Buy("ES")));

        log.WriteLine($"ES re-entry           : {denied.Code} — {denied.Message}");
        Assert.Equal(ErrorCode.LOSS_BUDGET_REACHED, denied.Code);
        Assert.Contains("ES", denied.Message, StringComparison.Ordinal);
        Assert.Equal(places, conn.Places);
        Assert.Null(gw.GetRequest("es-again"));

        // The account is not closed: NQ is a different position and a different question.
        var nq = await gw.PlaceAsync(new AgentContext("a"), "nq-ok", TestEnv.Buy("NQ"));
        log.WriteLine($"NQ after the ES closure: {nq.State}, places {conn.Places}");
        Assert.Equal(ExecutionState.FILLED, nq.State);
        Assert.Equal(places + 1, conn.Places);

        // And the scan finds exactly the one symbol, which is what the surfaces list.
        Assert.Equal(new[] { "ES" }, gw.SymbolsClosedToday(account));

        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE DAY THE RECORD CLOSES IS THE UTC DAY, AND IT REOPENS BY ITSELF (item 1).
    ///
    /// <para>Everything that says "today" in this product says the UTC day —
    /// <c>TradingGateway.StartOfDay</c>, <c>trade pnl</c>, the sentence the agent is given. A record
    /// keyed on the LOCAL date would expire at local midnight, which in every timezone east of UTC
    /// is hours before the ledger's day ends: the budget would be enforced against a figure that is
    /// still accumulating, and the agent would get a fresh day while the day it lost was still
    /// running. Both instants below are the same UTC day and straddle a local midnight.</para>
    ///
    /// <para>Nothing reopens the day but the clock. There is no verb, no op and no setting that
    /// clears a closure — see <c>CLAUDE.md</c> on where authority lives.</para>
    /// </summary>
    [Fact]
    public async Task The_closure_lasts_the_whole_utc_day_and_the_next_one_starts_clean()
    {
        var clock = new TestClock(Evening);
        var (gw, conn, db) = await Ready(clock, s => s.Risk.MaxDailyLoss = 1_000m);
        using var _1 = db;

        var account = conn.Broker.AccountId;
        Write(db, LossBreach.DayKey(account, Evening), Breach(account, Evening, 1_000m, 1_000m));

        var places = conn.Places;
        log.WriteLine($"breach at             : {Evening:yyyy-MM-dd HH:mm}Z, local {Evening.LocalDateTime:yyyy-MM-dd HH:mm}");

        // AN HOUR LATER: the same UTC day, and in a timezone east of UTC a different LOCAL date.
        var later = Evening.AddHours(1);
        clock.MoveTo(later);
        log.WriteLine($"still the same UTC day: {later:yyyy-MM-dd HH:mm}Z, local {later.LocalDateTime:yyyy-MM-dd HH:mm}");

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "same-day", TestEnv.Buy("ES")));
        Assert.Equal(ErrorCode.LOSS_BUDGET_REACHED, denied.Code);
        Assert.Equal(places, conn.Places);
        Assert.NotNull(gw.DayClosed(account));

        // THE NEXT UTC DAY: nothing was cleared, and nothing had to be. The key carries the day.
        clock.MoveTo(Evening.AddHours(2));
        Assert.Null(gw.DayClosed(account));
        var fresh = await gw.PlaceAsync(new AgentContext("a"), "next-day", TestEnv.Buy("ES"));
        log.WriteLine($"next UTC day          : {fresh.State}, places {conn.Places}");
        Assert.Equal(ExecutionState.FILLED, fresh.State);
        Assert.Equal(places + 1, conn.Places);

        await gw.DisposeAsync();
    }

    /// <summary>
    /// A RECORD THAT CANNOT BE READ REFUSES. The row exists, so something closed this day; the only
    /// thing in doubt is the evidence. Answering "open" because the JSON will not parse is the
    /// software deciding that a breach it cannot describe did not happen.
    /// </summary>
    [Fact]
    public async Task A_closure_row_that_cannot_be_read_refuses_rather_than_reading_as_an_open_day()
    {
        var (gw, conn, db) = await Ready(new TestClock(Evening), s => s.Risk.MaxDailyLoss = 1_000m);
        using var _1 = db;

        db.SetKv(LossBreach.DayKey(conn.Broker.AccountId, Evening), "{ this is not json");

        var places = conn.Places;
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "unreadable", TestEnv.Buy("ES")));

        log.WriteLine($"unreadable closure    : {denied.Code} — {denied.Message}");
        Assert.Equal(ErrorCode.RISK_CHECK_UNAVAILABLE, denied.Code);
        Assert.Equal(places, conn.Places);

        await gw.DisposeAsync();
    }
}

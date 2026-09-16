using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// A SCOPE THAT REACHES THE BUDGET TWICE IN A WEEK IS HELD FOR THE OWNER, AND CODE DOES NOT LET IT
/// BACK IN (<c>U-reopen-2</c>, item 1).
///
/// <para><c>U-reopen-1</c> reopens a closure once it has earned it, which is the right answer to one
/// bad day. It is the wrong answer to a run of them: a strategy or a market going through the
/// owner's budget repeatedly would be handed one daily budget a day, for ever, with nobody ever
/// asked whether that is what should be happening.</para>
///
/// <para>The count is taken at CONFIRMATION and written down, never recomputed when the closure is
/// asked to lift — a window measured at reopening lets a strike age out while the scope sits closed,
/// so the closure itself would buy the time that makes the earlier breach stop counting.</para>
///
/// <para>Everything goes through <c>RecordingConnector</c>, so "nothing was sent" is a count at the
/// wire rather than a claim about the gateway's intentions.</para>
/// </summary>
public class LossStrikeTests(ITestOutputHelper log)
{
    sealed class TestClock(DateTimeOffset at) : TimeProvider
    {
        DateTimeOffset _now = at;
        public override DateTimeOffset GetUtcNow() => _now;

        // A COHERENT CLOCK: the monotone half moves with the wall half, which is what a machine
        // nobody has touched does. The gateway holds a closure against BOTH since U-review-med, and
        // a fake that moved only the wall would be simulating a clock somebody set forward —
        // LossClockMonotoneTests is where that one is measured.
        public override long GetTimestamp() => _now.UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan by) => _now += by;
        public void MoveTo(DateTimeOffset to) => _now = to;
    }

    static readonly DateTimeOffset Noon = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Tick = TimeSpan.FromSeconds(20);
    static readonly TimeSpan Day = TimeSpan.FromHours(24);

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
    /// ONE WHOLE EPISODE: a position opened, driven through the day's budget, closed by the watch on
    /// two agreeing pulls and flattened by the app. Leaves the book flat and the budget wide, which
    /// is the state a reopen is judged from.
    /// </summary>
    /// <param name="budget">
    /// The day's budget for THIS episode. It climbs between episodes because the simulator stamps a
    /// fill with the wall clock rather than with the test's, so the ledger's "today" is cumulative
    /// across the days this test walks through — a harness artifact, and the episodes themselves are
    /// what is being measured.
    /// </param>
    static async Task BreachAndFlatten(TradingGateway gw, RecordingConnector conn, TestClock clock,
        string tag, decimal budget, ITestOutputHelper log)
    {
        gw.Update(s => s.Risk.MaxDailyLoss = budget);
        await gw.PlaceAsync(new AgentContext("a"), tag, TestEnv.Buy("ES"));
        conn.Broker.PriceOffset = -20m;

        clock.Advance(Tick);
        await gw.LossWatchAsync();
        clock.Advance(Tick);
        var closing = await gw.LossWatchAsync();
        log.WriteLine($"{tag,-14} closed  : [{string.Join(", ", closing.Closed)}]");
        Assert.NotEmpty(closing.Closed);

        var flatten = gw.FlattenToday(conn.Broker.AccountId);
        Assert.True(flatten!.Flat);

        conn.Broker.PriceOffset = 0m;
        gw.Update(s => s.Risk.MaxDailyLoss = 100_000m);
    }

    /// <summary>
    /// THE SECOND BREACH IN THE WINDOW IS NOT REOPENED BY CODE — the item itself.
    ///
    /// <para>The first episode reopens exactly as <c>U-reopen-1</c> says it should: the closure runs
    /// its time, the book reads flat, a receipt is written and the account trades again. The second
    /// one, three days later, meets every one of those conditions too — and is held, because a scope
    /// that has now reached the budget twice inside the window is a question for the owner rather
    /// than a closure to be served.</para>
    /// </summary>
    [Fact]
    public async Task A_second_breach_inside_the_strike_window_is_held_for_review_and_not_reopened()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        var account = conn.Broker.AccountId;

        // EPISODE ONE, AND THE REOPEN U-REOPEN-1 EARNS.
        await BreachAndFlatten(gw, conn, clock, "episode-one", 1_000m, log);
        var first = gw.DayClosed(account)!;
        clock.MoveTo(LossReopen.EligibleAt(first.ConfirmedAt, Day) + TimeSpan.FromMinutes(1));
        var reopened = await gw.LossWatchAsync();
        log.WriteLine($"first reopen   : [{string.Join(", ", reopened.Reopened)}]");
        Assert.Single(reopened.Reopened);
        Assert.Null(gw.DayClosed(account));

        // EPISODE TWO, THREE DAYS AFTER THE FIRST — four UTC dates inside a seven-date window.
        clock.MoveTo(new DateTimeOffset(2026, 3, 13, 9, 0, 0, TimeSpan.Zero));
        await BreachAndFlatten(gw, conn, clock, "episode-two", 2_000m, log);
        var second = gw.DayClosed(account)!;
        log.WriteLine($"episodes       : {first.Day} {first.ConfirmedAt:HH:mm}Z, "
                      + $"{second.Day} {second.ConfirmedAt:HH:mm}Z");
        Assert.NotEqual(first.Day, second.Day);

        // PAST EVERY ELIGIBILITY INSTANT, WITH A FLAT BOOK AND NOTHING UNACCOUNTED FOR.
        var eligible = LossReopen.EligibleAt(second.ConfirmedAt, Day);
        clock.MoveTo(eligible + TimeSpan.FromHours(1));
        var pass = await gw.LossWatchAsync();
        log.WriteLine($"second reopen  : [{string.Join(", ", pass.Reopened)}]");
        log.WriteLine($"book           : [{string.Join(" | ", conn.Broker.Positions.Where(p => p.Quantity != 0m).Select(p => $"{p.Symbol} {p.Quantity}"))}]");

        Assert.Empty(pass.Reopened);
        Assert.Null(db.GetKv(LossReopen.KeyFor(conn.Id, second)));
        Assert.NotNull(gw.DayClosed(account));

        // AND THE HOLD IS A ROW, NAMING BOTH EPISODES.
        var held = db.GetKv(LossHold.HoldKey(conn.Id, second));
        Assert.NotNull(held);
        var hold = Json.Read<LossHoldRecord>(held)!;
        log.WriteLine($"hold           : {hold.Why}");
        Assert.Equal(second.ConfirmedAt, hold.At);
        Assert.Equal(LossBreach.KeyFor(second), hold.BreachKey);
        Assert.Equal(2, hold.Episodes.Count);
        Assert.Equal(7, hold.StrikeWindowDays);
        Assert.Contains(LossBreach.KeyFor(first), hold.EpisodeKeys);
        Assert.Contains(LossBreach.KeyFor(second), hold.EpisodeKeys);

        // NOTHING REACHES THE WIRE, AND THE REFUSAL IS STILL THE CLOSURE'S OWN.
        var places = conn.Places;
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "held-for-review", TestEnv.Buy("ES")));
        log.WriteLine($"refusal        : {denied.Code}");
        Assert.Equal(ErrorCode.LOSS_BUDGET_REACHED, denied.Code);
        Assert.Equal(places, conn.Places);

        // AND THE SURFACES SAY SO RATHER THAN NAMING AN INSTANT NOTHING IS GOING TO HONOUR.
        var reading = gw.ReopenReading();
        log.WriteLine($"reopens at     : {reading.At?.ToString("yyyy-MM-dd HH:mm") ?? "none"}");
        Assert.Null(reading.At);
        Assert.NotNull(reading.HeldForReview);
        Assert.Equal(hold.Why, reading.Held);

        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE WINDOW IS COUNTED AT CONFIRMATION, SO A STRIKE CANNOT AGE OUT WHILE THE SCOPE IS CLOSED —
    /// the mutant of the guard, held directly.
    ///
    /// <para>A window evaluated when the closure is asked to lift is a window the closure itself
    /// moves: the scope sits shut, the days go by, and the earlier breach drops out of the count that
    /// was supposed to be about it. Ten days after the first episode there is only one breach inside
    /// a seven-date window looking back from now — and the hold still stands, because the count was
    /// taken once, when the second breach was confirmed, and written down.</para>
    /// </summary>
    [Fact]
    public async Task A_strike_cannot_age_out_while_the_scope_it_counted_is_still_closed()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        var account = conn.Broker.AccountId;

        await BreachAndFlatten(gw, conn, clock, "episode-one", 1_000m, log);
        var first = gw.DayClosed(account)!;
        clock.MoveTo(LossReopen.EligibleAt(first.ConfirmedAt, Day) + TimeSpan.FromMinutes(1));
        Assert.Single((await gw.LossWatchAsync()).Reopened);

        clock.MoveTo(new DateTimeOffset(2026, 3, 13, 9, 0, 0, TimeSpan.Zero));
        await BreachAndFlatten(gw, conn, clock, "episode-two", 2_000m, log);
        var second = gw.DayClosed(account)!;
        Assert.NotNull(db.GetKv(LossHold.HoldKey(conn.Id, second)));

        // TEN DAYS ON. Looking back seven UTC dates from here the first episode is not there at all,
        // and a count taken now would find one breach and let the scope out.
        var later = new DateTimeOffset(2026, 3, 23, 9, 0, 0, TimeSpan.Zero);
        clock.MoveTo(later);
        log.WriteLine($"window from    : {LossHold.WindowFrom(later, 7)} (first episode {first.Day})");
        Assert.False(LossHold.InWindow(first.Day, later, 7));

        var pass = await gw.LossWatchAsync();
        log.WriteLine($"reopened       : [{string.Join(", ", pass.Reopened)}]");
        Assert.Empty(pass.Reopened);
        Assert.Null(db.GetKv(LossReopen.KeyFor(conn.Id, second)));
        Assert.NotNull(gw.DayClosed(account));

        var places = conn.Places;
        await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "aged-out", TestEnv.Buy("ES")));
        Assert.Equal(places, conn.Places);

        await gw.DisposeAsync();
    }

    /// <summary>
    /// A DAILY CLOSURE AND A SYMBOL CLOSURE ON ONE DAY ARE ONE INCIDENT EACH, FOR THEIR OWN SCOPE.
    ///
    /// <para>Both budgets go through on the same pull, so the account and the instrument are each
    /// closed by their own record. Neither is the other's second strike: they are one incident seen
    /// by two limits, and a count that folded them together would hold an account for review the
    /// first time it ever reached the budget. A duplicate pull adds none either — the breach key is
    /// written once and a second sighting writes nothing at all.</para>
    /// </summary>
    [Fact]
    public async Task A_daily_and_a_symbol_closure_on_one_day_are_one_incident_each_and_neither_is_held()
    {
        var (gw, conn, db, clock) = await Ready(s => s.Risk.MaxLossPerTrade = 500m);
        using var _1 = db;
        var account = conn.Broker.AccountId;

        await gw.PlaceAsync(new AgentContext("a"), "both-budgets", TestEnv.Buy("ES"));
        conn.Broker.PriceOffset = -20m;

        clock.Advance(Tick);
        await gw.LossWatchAsync();
        clock.Advance(Tick);
        var closing = await gw.LossWatchAsync();
        log.WriteLine($"closed         : [{string.Join(", ", closing.Closed)}]");
        Assert.Equal(2, closing.Closed.Count);

        // A THIRD AND FOURTH PULL SEEING THE SAME THING WRITE NOTHING AT ALL.
        clock.Advance(Tick);
        var again = await gw.LossWatchAsync();
        Assert.Empty(again.Closed);

        var day = gw.DayClosed(account)!;
        var symbol = gw.SymbolClosed(account, "ES")!;
        log.WriteLine($"hold (day)     : {db.GetKv(LossHold.HoldKey(conn.Id, day)) ?? "none"}");
        log.WriteLine($"hold (ES)      : {db.GetKv(LossHold.HoldKey(conn.Id, symbol)) ?? "none"}");
        Assert.Null(db.GetKv(LossHold.HoldKey(conn.Id, day)));
        Assert.Null(db.GetKv(LossHold.HoldKey(conn.Id, symbol)));

        // AND BOTH REOPEN ON THEIR OWN RECEIPTS, WHICH IS THE POINT OF NOT HOLDING THEM.
        conn.Broker.PriceOffset = 0m;
        gw.Update(s => { s.Risk.MaxDailyLoss = 100_000m; s.Risk.MaxLossPerTrade = 100_000m; });
        clock.MoveTo(LossReopen.EligibleAt(day.ConfirmedAt, Day) + TimeSpan.FromHours(1));

        var pass = await gw.LossWatchAsync();
        log.WriteLine($"reopened       : [{string.Join(", ", pass.Reopened)}]");
        Assert.Equal(2, pass.Reopened.Count);
        Assert.Null(gw.DayClosed(account));
        Assert.Null(gw.SymbolClosed(account, "ES"));

        await gw.DisposeAsync();
    }
}

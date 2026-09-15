using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// AN EPISODE IS JUDGED BY THE RULE THAT WAS IN FORCE WHEN IT HAPPENED (<c>U-reopen-2</c>, item 3).
///
/// <para>The closure length and the strike window became the account owner's numbers in this unit,
/// which makes them values that can change between a breach and its reopen. Reading the LIVE setting
/// at the moment a closure is asked to lift would break the one rule this whole family of records
/// exists to keep — the record outranks the ledger — at exactly the number the record is measured
/// against: a closure narrowed to an hour after the event would reopen an account the owner shut for
/// a day, and one widened afterwards would delay a receipt that had already been earned.</para>
///
/// <para>So each breach record carries the values that applied to IT, and eligibility is computed
/// from that snapshot. A record written before this unit carries none, and is judged by the fixed
/// defaults — the rule that WAS in force when it was written.</para>
/// </summary>
public class LossRuleSnapshotTests(ITestOutputHelper log)
{
    sealed class TestClock(DateTimeOffset at) : TimeProvider
    {
        DateTimeOffset _now = at;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
        public void MoveTo(DateTimeOffset to) => _now = to;
    }

    static readonly DateTimeOffset Noon = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Tick = TimeSpan.FromSeconds(20);
    static readonly TimeSpan Day = TimeSpan.FromHours(24);

    static async Task<(TradingGateway Gw, RecordingConnector Conn, Database Db, TestClock Clock)> Ready(
        DateTimeOffset? start = null, Action<TradeAgentSettings>? settings = null)
    {
        var clock = new TestClock(start ?? Noon);
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

        conn.Broker.PriceOffset = 0m;
        gw.Update(s => s.Risk.MaxDailyLoss = 100_000m);
    }

    /// <summary>
    /// A CLOSURE NARROWED AFTER THE BREACH DOES NOT BRING THAT BREACH'S REOPEN FORWARD — the item.
    ///
    /// <para>The breach is recorded under the 24 hours the owner had set, and the record says so.
    /// Shortening the setting to one hour afterwards is the widest thing an owner can do to this
    /// rule, and it is the same act as setting a budget to zero after a breach: the number that
    /// governs is the one the record was written with, and the one set since governs the NEXT
    /// breach.</para>
    /// </summary>
    [Fact]
    public async Task A_closure_narrowed_after_the_breach_does_not_reopen_that_breach_early()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        var account = conn.Broker.AccountId;

        await BreachAndFlatten(gw, conn, clock, "episode", 1_000m, log);
        var breach = gw.DayClosed(account)!;
        log.WriteLine($"snapshot       : closure={breach.MinClosure} window={breach.StrikeWindowDays}");
        Assert.Equal(Day, breach.MinClosure);
        Assert.Equal(7, breach.StrikeWindowDays);

        // THE OWNER SHORTENS IT TO AN HOUR. On the live setting that would put the earliest instant
        // at the next UTC midnight; on the record's own it stays a day out.
        gw.Update(s => s.Risk.LossMinClosureHours = 1m);
        var snapshotted = LossReopen.EligibleAt(breach.ConfirmedAt, Day);
        var live = LossReopen.EligibleAt(breach.ConfirmedAt, TimeSpan.FromHours(1));
        log.WriteLine($"eligible       : snapshot {snapshotted:yyyy-MM-dd HH:mm}Z / live {live:yyyy-MM-dd HH:mm}Z");
        Assert.True(live < snapshotted);

        clock.MoveTo(live + TimeSpan.FromHours(2));
        var early = await gw.LossWatchAsync();
        log.WriteLine($"at the live one: [{string.Join(", ", early.Reopened)}]");
        Assert.Empty(early.Reopened);
        Assert.Null(db.GetKv(LossReopen.KeyFor(conn.Id, breach)));
        Assert.NotNull(gw.DayClosed(account));

        var places = conn.Places;
        await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "narrowed-early", TestEnv.Buy("ES")));
        Assert.Equal(places, conn.Places);

        // THE SURFACES NAME THE SNAPSHOT'S INSTANT TOO, not the live setting's.
        log.WriteLine($"reopens at     : {gw.ReopenReading().At:yyyy-MM-dd HH:mm}Z");
        Assert.Equal(snapshotted, gw.ReopenReading().At);

        // AND AT THE INSTANT THE RECORD WAS WRITTEN WITH, IT REOPENS — carrying the rule with it.
        clock.MoveTo(snapshotted + TimeSpan.FromMinutes(1));
        var pass = await gw.LossWatchAsync();
        Assert.Single(pass.Reopened);
        var receipt = Json.Read<LossReopenRecord>(db.GetKv(LossReopen.KeyFor(conn.Id, breach))!)!;
        log.WriteLine($"receipt rule   : closure={receipt.MinClosure} window={receipt.StrikeWindowDays}");
        Assert.Equal(Day, receipt.MinClosure);
        Assert.Equal(7, receipt.StrikeWindowDays);
        Assert.Equal(snapshotted, receipt.EligibleAt);

        await gw.DisposeAsync();
    }

    /// <summary>
    /// A CLOSURE THE OWNER SHORTENED BEFORE THE BREACH IS WHAT THAT BREACH IS JUDGED BY — the other
    /// direction, so the setting is the owner's and not decoration.
    ///
    /// <para>Six hours, set before the breach, on a breach confirmed at 22:00Z: the earliest instant
    /// is 04:00Z the next morning, because <c>EligibleAt</c> is the LATER of the two terms and the
    /// midnight one has already passed by then. At 00:30Z it is still closed; at 04:01Z the tick
    /// writes the receipt.</para>
    /// </summary>
    [Fact]
    public async Task A_closure_the_owner_shortened_before_the_breach_governs_that_breach()
    {
        var evening = new DateTimeOffset(2026, 3, 10, 22, 0, 0, TimeSpan.Zero);
        var (gw, conn, db, clock) = await Ready(evening, s => s.Risk.LossMinClosureHours = 6m);
        using var _1 = db;
        var account = conn.Broker.AccountId;

        await BreachAndFlatten(gw, conn, clock, "evening", 1_000m, log);
        var breach = gw.DayClosed(account)!;
        log.WriteLine($"confirmed      : {breach.ConfirmedAt:yyyy-MM-dd HH:mm}Z, snapshot {breach.MinClosure}");
        Assert.Equal(TimeSpan.FromHours(6), breach.MinClosure);

        var eligible = LossReopen.EligibleAt(breach.ConfirmedAt, TimeSpan.FromHours(6));
        log.WriteLine($"eligible       : {eligible:yyyy-MM-dd HH:mm}Z");
        Assert.Equal(breach.ConfirmedAt + TimeSpan.FromHours(6), eligible);

        // PAST MIDNIGHT AND STILL CLOSED: the six hours have not run.
        clock.MoveTo(new DateTimeOffset(2026, 3, 11, 0, 30, 0, TimeSpan.Zero));
        Assert.Empty((await gw.LossWatchAsync()).Reopened);
        Assert.NotNull(gw.DayClosed(account));

        clock.MoveTo(eligible + TimeSpan.FromMinutes(1));
        var pass = await gw.LossWatchAsync();
        log.WriteLine($"reopened       : [{string.Join(", ", pass.Reopened)}]");
        Assert.Single(pass.Reopened);
        Assert.Null(gw.DayClosed(account));

        await gw.DisposeAsync();
    }

    /// <summary>
    /// A CLOSURE WIDENED AFTER A RECEIPT WAS WRITTEN DOES NOT UN-REOPEN ANYTHING. The receipt is the
    /// act, and a setting changed afterwards is about the next breach: a scope that is trading again
    /// does not stop because somebody typed a bigger number.
    /// </summary>
    [Fact]
    public async Task A_closure_widened_after_the_receipt_does_not_close_the_scope_again()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        var account = conn.Broker.AccountId;

        await BreachAndFlatten(gw, conn, clock, "episode", 1_000m, log);
        var breach = gw.DayClosed(account)!;
        clock.MoveTo(LossReopen.EligibleAt(breach.ConfirmedAt, Day) + TimeSpan.FromMinutes(1));
        Assert.Single((await gw.LossWatchAsync()).Reopened);

        gw.Update(s => s.Risk.LossMinClosureHours = 240m);
        log.WriteLine($"widened to     : {gw.Settings.Risk.LossMinClosureHours} h");
        Assert.Null(gw.DayClosed(account));

        var places = conn.Places;
        var fresh = await gw.PlaceAsync(new AgentContext("a"), "after-widening", TestEnv.Buy("ES"));
        log.WriteLine($"after widening : {fresh.State}, places {places} -> {conn.Places}");
        Assert.Equal(ExecutionState.FILLED, fresh.State);
        Assert.Equal(places + 1, conn.Places);

        await gw.DisposeAsync();
    }

    /// <summary>
    /// A RECORD FROM BEFORE THIS UNIT CARRIES NO SNAPSHOT AND IS JUDGED BY THE FIXED DEFAULTS.
    ///
    /// <para>The row is written here exactly as an older build left it: no closure length on it and
    /// no window. The only honest thing to judge it by is the rule that was in force when it was
    /// written, which is the 24 hours <c>U-reopen-1</c> fixed — and emphatically not whatever the
    /// owner has set since, which is the reading that would let a number typed today shorten a
    /// closure recorded last week.</para>
    /// </summary>
    [Fact]
    public async Task A_breach_record_with_no_snapshot_is_judged_by_the_fixed_defaults()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        var account = conn.Broker.AccountId;

        var confirmed = new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);
        db.SetKv(LossBreach.DayKey(account, confirmed), Json.Write(new LossBreachRecord
        {
            Account = account, Day = LossBreach.Stamp(confirmed), ConfirmedAt = confirmed,
            Loss = 1_200m, DayBudget = 1_000m, Currency = "USD",
            Why = "an older build wrote this row and put no rule on it"
        }));

        var old = gw.DayClosed(account)!;
        log.WriteLine($"snapshot       : closure={old.MinClosure?.ToString() ?? "none"} "
                      + $"window={old.StrikeWindowDays?.ToString() ?? "none"}");
        Assert.Null(old.MinClosure);
        Assert.Null(old.StrikeWindowDays);

        // AND THE LIVE SETTING IS NARROWED AS FAR AS IT GOES, which must change nothing about it.
        gw.Update(s => { s.Risk.LossMinClosureHours = 0m; s.Risk.LossStrikeWindowDays = 0; });

        var reading = gw.ReopenReading();
        log.WriteLine($"reopens at     : {reading.At:yyyy-MM-dd HH:mm}Z");
        Assert.Equal(LossReopen.EligibleAt(confirmed, Day), reading.At);

        clock.MoveTo(confirmed + TimeSpan.FromHours(20));
        Assert.Empty((await gw.LossWatchAsync()).Reopened);
        Assert.NotNull(gw.DayClosed(account));

        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE STRIKE WINDOW IS SNAPSHOT TOO, AND A SECOND BREACH OUTSIDE IT IS NOT A STRIKE. Two dates
    /// is the owner's window here, and the episodes are four dates apart — so the second one is a
    /// first offence for its own scope and reopens by code like any other.
    /// </summary>
    [Fact]
    public async Task A_second_breach_outside_the_owners_own_window_is_not_a_strike()
    {
        var (gw, conn, db, clock) = await Ready(settings: s => s.Risk.LossStrikeWindowDays = 2);
        using var _1 = db;
        var account = conn.Broker.AccountId;

        await BreachAndFlatten(gw, conn, clock, "episode-one", 1_000m, log);
        var first = gw.DayClosed(account)!;
        Assert.Equal(2, first.StrikeWindowDays);
        clock.MoveTo(LossReopen.EligibleAt(first.ConfirmedAt, Day) + TimeSpan.FromMinutes(1));
        Assert.Single((await gw.LossWatchAsync()).Reopened);

        clock.MoveTo(new DateTimeOffset(2026, 3, 13, 9, 0, 0, TimeSpan.Zero));
        await BreachAndFlatten(gw, conn, clock, "episode-two", 2_000m, log);
        var second = gw.DayClosed(account)!;

        log.WriteLine($"hold           : {db.GetKv(LossHold.HoldKey(conn.Id, second)) ?? "none"}");
        Assert.Null(db.GetKv(LossHold.HoldKey(conn.Id, second)));
        Assert.Null(gw.ReopenReading().HeldForReview);

        clock.MoveTo(LossReopen.EligibleAt(second.ConfirmedAt, Day) + TimeSpan.FromMinutes(1));
        var pass = await gw.LossWatchAsync();
        log.WriteLine($"reopened       : [{string.Join(", ", pass.Reopened)}]");
        Assert.Single(pass.Reopened);
        Assert.Null(gw.DayClosed(account));

        await gw.DisposeAsync();
    }
}

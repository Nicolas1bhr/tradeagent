using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// A CLOSURE IS HELD AGAINST A MONOTONE READING AS WELL AS AGAINST THE WALL CLOCK
/// (<c>U-review-med</c>, item 1; REVIEW 2026-09-16 finding 6, probe <c>C4</c>).
///
/// <para>The high-water mark used to refuse <c>at &lt; mark</c> and nothing else, so ONE forward
/// step of the machine clock past the eligibility instant ended a closure on the very next tick,
/// with no time having passed at all: the mark rose, no <c>loss_clock_suspect</c> row was written,
/// and <c>HeldBy</c>'s remaining test — <c>at &lt; eligible</c> — had just been satisfied by the
/// jump. <c>docs/CONTRACTS.md</c> said "a step forward is ordinary and lengthens nothing" and
/// <c>LossReopen.ClockKey</c> said a forward step "only ever leaves things closed longer"; both
/// were claims about the code that the code did not keep.</para>
///
/// <para>What is measured here is the guard that replaced it. The mark now carries a MONOTONE
/// reading taken from the same <see cref="TimeProvider"/> — in production
/// <c>TimeProvider.System.GetTimestamp()</c>, which is <c>Stopwatch</c>'s counter and cannot be set
/// — and a forward wall step unmatched by a monotone elapse is refused exactly as a backward one
/// is. Across a RESTART the two readings cannot be compared at all, so the gap is not credited to
/// the closure instead: it is added to the instant, which is the conservative direction.</para>
///
/// <para>Everything goes through <c>RecordingConnector</c>, so "nothing was sent" is a count at the
/// wire rather than a claim about the gateway's intentions.</para>
/// </summary>
public class LossClockMonotoneTests(ITestOutputHelper log)
{
    /// <summary>
    /// A COHERENT CLOCK, AND A MACHINE CLOCK SOMEBODY MOVED — the difference this unit is about.
    ///
    /// <para><see cref="Advance"/> is time passing: both halves move, exactly as they do on a
    /// machine nobody has touched. <see cref="SetForward"/> is the wall half moving on its own — an
    /// NTP correction, a restored image, a person setting the clock — and it is the only thing the
    /// monotone reading can tell apart from the first.</para>
    /// </summary>
    sealed class TestClock(DateTimeOffset at) : TimeProvider
    {
        DateTimeOffset _now = at;
        TimeSpan _skew = TimeSpan.Zero;

        public override DateTimeOffset GetUtcNow() => _now + _skew;

        public override long GetTimestamp() => _now.UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan by) => _now += by;

        public void SetForward(TimeSpan by) => _skew += by;
    }

    static readonly DateTimeOffset Noon = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Tick = TimeSpan.FromSeconds(20);

    static TradingGateway Build(Database db, RecordingConnector conn, TestClock clock)
    {
        var gw = new TradingGateway(db, conn, new HealthRegistry(), new GatewayOptions { Clock = clock });
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments];
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 0m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
            s.Risk.MaxDailyLoss = 1_000m;
        });
        return gw;
    }

    /// <summary>One long, closed by the watch on two agreeing pulls, flattened, nothing left to lose.</summary>
    static async Task<LossBreachRecord> ClosedAndFlat(TradingGateway gw, RecordingConnector conn,
        TestClock clock, ITestOutputHelper log)
    {
        await gw.PlaceAsync(new AgentContext("a"), "mono-open", TestEnv.Buy("ES"));
        conn.Broker.PriceOffset = -20m;

        clock.Advance(Tick);
        await gw.LossWatchAsync();
        clock.Advance(Tick);
        var closing = await gw.LossWatchAsync();
        Assert.NotEmpty(closing.Closed);

        conn.Broker.PriceOffset = 0m;
        gw.Update(s => s.Risk.MaxDailyLoss = 1_000_000m);

        var breach = gw.DayClosed(conn.Broker.AccountId)!;
        log.WriteLine($"closed at               : {breach.ConfirmedAt.UtcDateTime:u}");
        return breach;
    }

    /// <summary>
    /// THE RED: ONE FORWARD STEP OF THE MACHINE CLOCK, NO TIME ELAPSED, ONE TICK — and the closure
    /// is still a closure. This is probe <c>C4</c> with its observations turned into assertions.
    /// </summary>
    [Fact]
    public async Task A_clock_set_forward_past_the_instant_with_no_time_elapsed_ends_no_closure()
    {
        var clock = new TestClock(Noon);
        using var db = TestEnv.NewDb();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker())
        {
            EmergencyBudget = Unresolved.PressBudgetFor(1)
        });
        var gw = Build(db, conn, clock);
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        var account = conn.Broker.AccountId;
        var breach = await ClosedAndFlat(gw, conn, clock, log);
        log.WriteLine($"eligible at             : {LossReopen.EligibleAt(breach.ConfirmedAt, TimeSpan.FromHours(24)).UtcDateTime:u}");

        // ONE forward step, and no elapsed time at all: an NTP correction, a restored image, or a
        // person setting the machine clock two days on.
        clock.SetForward(TimeSpan.FromDays(2));
        var places = conn.Places;
        var pass = await gw.LossWatchAsync();

        log.WriteLine($"after ONE forward jump  : reopened=[{string.Join(",", pass.Reopened)}]");
        log.WriteLine($"day closed now          : {gw.DayClosed(account) is not null}");
        log.WriteLine($"clock suspect row       : {(db.GetKv(LossReopen.SuspectKey(conn.Id, account)) is null ? "none" : "written")}");

        Assert.Empty(pass.Reopened);
        Assert.NotNull(gw.DayClosed(account));
        Assert.Null(db.GetKv(LossReopen.KeyFor(conn.Id, breach)));

        // REFUSED AND RECORDED, exactly as a backward step is: one row, and it says which way it went.
        var suspect = gw.ClockSuspect(account);
        Assert.NotNull(suspect);
        log.WriteLine($"suspect says            : {suspect!.Why}");
        Assert.Contains("FORWARD", suspect.Why, StringComparison.Ordinal);

        // AND SAID ON THE SURFACES. `status` carries this through LossTodayAsync's ReopenHeld.
        var reading = gw.ReopenReading();
        Assert.Null(reading.At);
        Assert.Equal(suspect.Why, reading.Held);
        log.WriteLine($"status would say        : {reading.Held}");

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "mono-after", TestEnv.Buy("ES")));
        log.WriteLine($"a new buy after the jump: {denied.Code}");
        Assert.Equal(places, conn.Places);
    }

    /// <summary>
    /// THE CONTROL, and it is the one that stops the guard from being "never reopen anything": on a
    /// clock whose two halves move together, the same closure serves its time and lifts.
    /// </summary>
    [Fact]
    public async Task A_closure_whose_time_really_passes_still_reopens()
    {
        var clock = new TestClock(Noon);
        using var db = TestEnv.NewDb();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker())
        {
            EmergencyBudget = Unresolved.PressBudgetFor(1)
        });
        var gw = Build(db, conn, clock);
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        var account = conn.Broker.AccountId;
        var breach = await ClosedAndFlat(gw, conn, clock, log);

        clock.Advance(LossReopen.EligibleAt(breach.ConfirmedAt, TimeSpan.FromHours(24))
                      - clock.GetUtcNow() + TimeSpan.FromMinutes(1));
        var pass = await gw.LossWatchAsync();

        log.WriteLine($"reopened                : [{string.Join(",", pass.Reopened)}]");
        Assert.NotEmpty(pass.Reopened);
        Assert.Null(gw.DayClosed(account));
        Assert.Null(gw.ClockSuspect(account));
        await gw.PlaceAsync(new AgentContext("a"), "mono-ok", TestEnv.Buy("ES"));
    }

    /// <summary>
    /// A RESTART IS NOT A FORWARD JUMP, AND THE GAP IS NOT CREDITED TO THE CLOSURE.
    ///
    /// <para>Two readings taken by two different processes cannot be compared — the monotone
    /// counter's origin is the process's own — so a restart writes no suspect row. What it does
    /// instead is decline to count the time it did not see: the closure's instant moves forward by
    /// the gap, which is the conservative direction and the only honest one. A second gateway over
    /// the same database is a restart for exactly this purpose.</para>
    /// </summary>
    [Fact]
    public async Task A_restart_does_not_credit_the_gap_it_could_not_see()
    {
        var clock = new TestClock(Noon);
        using var db = TestEnv.NewDb();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker())
        {
            EmergencyBudget = Unresolved.PressBudgetFor(1)
        });
        var gw = Build(db, conn, clock);
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        var account = conn.Broker.AccountId;
        var breach = await ClosedAndFlat(gw, conn, clock, log);
        var eligible = LossReopen.EligibleAt(breach.ConfirmedAt, TimeSpan.FromHours(24));

        // THE PROCESS GOES DOWN FOR THE WHOLE CLOSURE and comes back a minute past the instant.
        clock.Advance(eligible - clock.GetUtcNow() + TimeSpan.FromMinutes(1));
        var restarted = Build(db, new RecordingConnector(conn.Inner), clock);
        var pass = await restarted.LossWatchAsync();

        log.WriteLine($"first tick of the new run : reopened=[{string.Join(",", pass.Reopened)}]");
        log.WriteLine($"suspect row               : {(db.GetKv(LossReopen.SuspectKey(conn.Id, account)) is null ? "none" : "written")}");

        Assert.Empty(pass.Reopened);
        Assert.NotNull(restarted.DayClosed(account));

        // NOT SUSPECT — nothing about the clock is wrong. The instant simply moved by the gap.
        Assert.Null(restarted.ClockSuspect(account));
        var moved = restarted.ReopenReading();
        Assert.NotNull(moved.At);
        Assert.True(moved.At > eligible, $"the instant moved: {moved.At:u} against {eligible:u}");
        log.WriteLine($"the instant now           : {moved.At:u} (was {eligible:u})");

        // AND IT IS THE GAP AND NOT FOR EVER: the same closure lifts once that time has run on a
        // clock both of whose halves are moving.
        clock.Advance(moved.At!.Value - clock.GetUtcNow() + TimeSpan.FromMinutes(1));
        var second = await restarted.LossWatchAsync();
        log.WriteLine($"after the gap has run     : reopened=[{string.Join(",", second.Reopened)}]");
        Assert.NotEmpty(second.Reopened);
        Assert.Null(restarted.DayClosed(account));
    }
}

using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// THE DIRECTORS' HOLD IS BOUNDED, OR IT IS NOTHING (<c>U-reopen-2</c>, item 4).
///
/// <para>A confirmed breach opens a <c>BoundaryKind.LossBudget</c> boundary whose default disposition
/// is <c>hold</c>, and <c>CouncilBoundaries.ApplyDue</c> writes that default onto the row when the
/// deadline passes. The obvious thing to do with it — read the boundary's <c>hold</c> as holding the
/// closure — is a closure that never ends: the disposition is written once, by policy, and nothing
/// in the protocol ever changes it afterwards. An account would be shut for good by a post-mortem
/// nobody could answer.</para>
///
/// <para>So the boundary's disposition holds NOTHING, and the only thing that can move an episode's
/// eligibility instant at all is a row with an END on it — clamped by code to at most one closure
/// length, applied once per episode, void the moment the owner releases, and shown with its end
/// wherever the instant is shown.</para>
/// </summary>
public class LossDirectorHoldTests(ITestOutputHelper log)
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

    static async Task<(TradingGateway Gw, RecordingConnector Conn, Database Db, TestClock Clock)> Ready()
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
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        return (gw, conn, db, clock);
    }

    static async Task<LossBreachRecord> BreachAndFlatten(TradingGateway gw, RecordingConnector conn,
        TestClock clock, string tag, decimal budget, ITestOutputHelper log)
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
        return gw.DayClosed(conn.Broker.AccountId)!;
    }

    /// <summary>
    /// A <c>HOLD</c> DISPOSITION WITH NO END HOLDS NOTHING — the item itself.
    ///
    /// <para>The boundary is opened by the closure, nobody assesses it, and the policy writes its
    /// predetermined default onto the row when the deadline runs out. That default is <c>hold</c> and
    /// it is never revised, so a closure that read it as "stay closed" would be a closure with no end
    /// at all — the account shut for good because two directors said nothing.</para>
    /// </summary>
    [Fact]
    public async Task A_hold_disposition_with_no_end_extends_nothing_and_the_scope_still_reopens()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        var account = conn.Broker.AccountId;
        var breach = await BreachAndFlatten(gw, conn, clock, "episode", 1_000m, log);

        var boundaries = new CouncilBoundaries(db);
        var id = gw.LossBoundaryIdFor(breach);
        var opened = boundaries.ById(id);
        log.WriteLine($"boundary       : {id} default={opened?.DefaultDisposition} open={opened?.IsOpen}");
        Assert.NotNull(opened);
        Assert.Equal(BoundaryDisposition.Hold, opened.DefaultDisposition);

        // THE DEADLINE RUNS OUT AND THE POLICY WRITES THE DEFAULT. Nothing ever revises it.
        var settled = boundaries.ApplyDue(opened.DeadlineAt + TimeSpan.FromMinutes(1));
        var row = boundaries.ById(id)!;
        log.WriteLine($"disposed       : {row.Disposition} by {row.DisposedBy} ({settled.Count} settled)");
        Assert.Equal(BoundaryDisposition.Hold, row.Disposition);

        // AND THE CLOSURE ENDS ON ITS OWN TERMS ANYWAY. There is no extension row, so there is no
        // end to show and therefore nothing holding it beyond the rule it was recorded with.
        var eligible = LossReopen.EligibleAt(breach.ConfirmedAt, Day);
        Assert.Equal(eligible, gw.ReopenReading().At);

        clock.MoveTo(eligible + TimeSpan.FromMinutes(1));
        var pass = await gw.LossWatchAsync();
        log.WriteLine($"reopened       : [{string.Join(", ", pass.Reopened)}]");
        Assert.Single(pass.Reopened);
        Assert.Null(gw.DayClosed(account));

        var places = conn.Places;
        var fresh = await gw.PlaceAsync(new AgentContext("a"), "after-the-hold", TestEnv.Buy("ES"));
        log.WriteLine($"after the hold : {fresh.State}, places {places} -> {conn.Places}");
        Assert.Equal(ExecutionState.FILLED, fresh.State);
        Assert.Equal(places + 1, conn.Places);

        await gw.DisposeAsync();
    }

    /// <summary>
    /// AN EXTENSION IS CLAMPED TO ONE CLOSURE LENGTH, APPLIED ONCE, AND SHOWN WITH ITS END.
    ///
    /// <para>The row asks for thirty days. What the code applies is one closure length — the bound is
    /// the whole of what makes an extension safe to have at all — and it applies it ONCE: an
    /// extension re-applied on every tick would push the instant out faster than the clock reaches
    /// it, which is the same closure-with-no-end the boundary's own disposition would have been.</para>
    /// </summary>
    [Fact]
    public async Task An_extension_is_clamped_to_one_closure_length_and_applied_once()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        var account = conn.Broker.AccountId;
        var breach = await BreachAndFlatten(gw, conn, clock, "episode", 1_000m, log);

        var eligible = LossReopen.EligibleAt(breach.ConfirmedAt, Day);
        var bound = eligible + Day;
        db.SetKv(LossHold.ExtensionKey(conn.Id, breach), Json.Write(new LossExtensionRecord
        {
            Account = account, Connector = conn.Id, Day = breach.Day, Symbol = breach.Symbol,
            BreachKey = LossBreach.KeyFor(breach), BoundaryId = gw.LossBoundaryIdFor(breach),
            Until = eligible + TimeSpan.FromDays(30), At = breach.ConfirmedAt,
            Why = "the post-mortem asked for longer"
        }));

        var reading = gw.ReopenReading();
        log.WriteLine($"eligible       : {eligible:yyyy-MM-dd HH:mm}Z");
        log.WriteLine($"asked / applied: {eligible + TimeSpan.FromDays(30):yyyy-MM-dd HH:mm}Z / "
                      + $"{reading.At:yyyy-MM-dd HH:mm}Z");
        log.WriteLine($"said           : {reading.Extended ?? "nothing"}");
        Assert.Equal(bound, reading.At);
        Assert.NotNull(reading.Extended);
        Assert.Contains(bound.UtcDateTime.ToString("yyyy-MM-dd HH:mm"), reading.Extended!, StringComparison.Ordinal);

        // AT THE UNEXTENDED INSTANT, STILL CLOSED — with ticks in between, which is where an
        // extension re-applied per tick would run away from the clock.
        clock.MoveTo(eligible + TimeSpan.FromMinutes(1));
        Assert.Empty((await gw.LossWatchAsync()).Reopened);
        clock.MoveTo(eligible + TimeSpan.FromHours(12));
        Assert.Empty((await gw.LossWatchAsync()).Reopened);
        Assert.NotNull(gw.DayClosed(account));

        var places = conn.Places;
        await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "still-extended", TestEnv.Buy("ES")));
        Assert.Equal(places, conn.Places);

        // AND AT THE BOUND, IT REOPENS. The extension has an end and the end arrives.
        clock.MoveTo(bound + TimeSpan.FromMinutes(1));
        var pass = await gw.LossWatchAsync();
        log.WriteLine($"reopened       : [{string.Join(", ", pass.Reopened)}]");
        Assert.Single(pass.Reopened);
        Assert.Null(gw.DayClosed(account));

        await gw.DisposeAsync();
    }

    /// <summary>
    /// AN EXTENSION WITH NO END EXTENDS NOTHING, and neither does one about a different boundary.
    ///
    /// <para>"No hold without an end" is checked where the instant is used and never assumed by
    /// whoever wrote the row, because a row with no end is exactly what a build that had not thought
    /// about this would write. The boundary id is checked for the same reason: an extension that
    /// names a post-mortem other than this episode's is not about this episode, whatever it says.</para>
    /// </summary>
    [Fact]
    public async Task An_extension_with_no_end_or_another_boundarys_id_extends_nothing()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        var breach = await BreachAndFlatten(gw, conn, clock, "episode", 1_000m, log);
        var eligible = LossReopen.EligibleAt(breach.ConfirmedAt, Day);
        var key = LossHold.ExtensionKey(conn.Id, breach);

        LossExtensionRecord Row(DateTimeOffset? until, string boundary) => new()
        {
            Account = breach.Account, Connector = conn.Id, Day = breach.Day,
            BreachKey = LossBreach.KeyFor(breach), BoundaryId = boundary, Until = until,
            At = breach.ConfirmedAt, Why = "seeded"
        };

        foreach (var (what, row) in new (string, LossExtensionRecord)[]
                 {
                     ("no end at all", Row(null, gw.LossBoundaryIdFor(breach))),
                     ("an end in the past", Row(eligible - TimeSpan.FromHours(1), gw.LossBoundaryIdFor(breach))),
                     ("another boundary", Row(eligible + Day, "loss_budget:SOMEONE-ELSE:20260101"))
                 })
        {
            db.SetKv(key, Json.Write(row));
            var reading = gw.ReopenReading();
            log.WriteLine($"{what,-18}: reopens at {reading.At:yyyy-MM-dd HH:mm}Z, said {reading.Extended ?? "nothing"}");
            Assert.Equal(eligible, reading.At);
            Assert.Null(reading.Extended);
        }

        clock.MoveTo(eligible + TimeSpan.FromMinutes(1));
        var pass = await gw.LossWatchAsync();
        log.WriteLine($"reopened       : [{string.Join(", ", pass.Reopened)}]");
        Assert.Single(pass.Reopened);

        await gw.DisposeAsync();
    }

    /// <summary>
    /// AN EXTENSION IS NEVER APPLIED PAST A RELEASE. The owner's press is the last word on an episode:
    /// a person who has looked at the account and decided it may trade again is not then made to wait
    /// out a delay that was asked for before they looked.
    /// </summary>
    [Fact]
    public async Task A_release_ends_an_extension()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        var account = conn.Broker.AccountId;

        var first = await BreachAndFlatten(gw, conn, clock, "episode-one", 1_000m, log);
        clock.MoveTo(LossReopen.EligibleAt(first.ConfirmedAt, Day) + TimeSpan.FromMinutes(1));
        Assert.Single((await gw.LossWatchAsync()).Reopened);

        clock.MoveTo(new DateTimeOffset(2026, 3, 13, 9, 0, 0, TimeSpan.Zero));
        var second = await BreachAndFlatten(gw, conn, clock, "episode-two", 2_000m, log);
        Assert.NotNull(gw.ReopenReading().HeldForReview);

        var eligible = LossReopen.EligibleAt(second.ConfirmedAt, Day);
        db.SetKv(LossHold.ExtensionKey(conn.Id, second), Json.Write(new LossExtensionRecord
        {
            Account = account, Connector = conn.Id, Day = second.Day,
            BreachKey = LossBreach.KeyFor(second), BoundaryId = gw.LossBoundaryIdFor(second),
            Until = eligible + Day, At = second.ConfirmedAt, Why = "the post-mortem asked for longer"
        }));

        clock.MoveTo(eligible + TimeSpan.FromMinutes(1));
        Assert.Empty((await gw.LossWatchAsync()).Reopened);

        Assert.True(gw.ReleaseHold("looked at the journal and the ES run; it can trade").Ok);
        var after = gw.ReopenReading();
        log.WriteLine($"after release  : reopens at {after.At?.ToString("yyyy-MM-dd HH:mm") ?? "now"}Z, "
                      + $"extended {after.Extended ?? "nothing"}");
        Assert.Null(after.Extended);

        var pass = await gw.LossWatchAsync();
        log.WriteLine($"reopened       : [{string.Join(", ", pass.Reopened)}]");
        Assert.Single(pass.Reopened);
        Assert.Null(gw.DayClosed(account));

        await gw.DisposeAsync();
    }
}

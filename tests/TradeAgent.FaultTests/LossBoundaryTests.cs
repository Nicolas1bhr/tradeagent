using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// A CLOSED DAY IS A CONSEQUENTIAL BOUNDARY, AND THE APP OPENS IT ITSELF.
///
/// <para><c>docs/COUNCIL.md</c>:59 lists "the post-mortem after a loss-budget event" beside promotion
/// as one of the four places the strongest model is spent, and until this unit the only one any code
/// opened was promotion. The property that matters is not that a boundary exists — it is that
/// exactly ONE exists per account per day however hard an agent in trouble tries: :64, "deduplicate
/// boundary events by entity and revision, so repeated proposals cannot manufacture senior spend".
/// Two assessments are two paid turns of the dearest model there is, and an agent that could
/// manufacture them by sending order after order into a closed day would be buying them with the
/// owner's money.</para>
/// </summary>
public class LossBoundaryTests(ITestOutputHelper log)
{
    sealed class TestClock(DateTimeOffset at) : TimeProvider
    {
        DateTimeOffset _now = at;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    static readonly DateTimeOffset Noon = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
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
    /// THE CLOSURE OPENS ONE BOUNDARY, WAKES BOTH DIRECTORS, AND THE SECOND, THIRD AND FOURTH EVENTS
    /// OF THE SAME DAY OPEN NOTHING (item 3).
    ///
    /// <para>Both budgets are on and both are breached, so two records are written; then two orders
    /// arrive and are refused off them. Four chances to open a second boundary, and the ledger holds
    /// one — because its id is a function of the account and the UTC day, which is the FACT, rather
    /// than of the pull, the symbol or the request that noticed it.</para>
    ///
    /// <para>The disposition written at open is <c>hold</c>, and it is written NOW: a day that closed
    /// itself on the owner's limit must not reopen because two directors ran out of clock. Nothing in
    /// the closure waits for either of them — the record and the refusal are in force before the
    /// boundary row exists, and this whole test runs with no council, no relay and no AI budget of
    /// any kind.</para>
    /// </summary>
    [Fact]
    public async Task A_confirmed_breach_opens_exactly_one_boundary_for_the_account_and_the_day()
    {
        var (gw, conn, db, clock) = await Ready(s =>
        {
            s.Risk.MaxDailyLoss = 1_000m;
            s.Risk.MaxLossPerTrade = 500m;
        });
        using var _1 = db;

        var account = conn.Broker.AccountId;
        var boundaries = new CouncilBoundaries(db);
        Assert.Empty(boundaries.All());

        await gw.PlaceAsync(new AgentContext("a"), "b-open", TestEnv.Buy("ES"));
        conn.Broker.PriceOffset = -20m;

        clock.Advance(Tick);
        await gw.LossWatchAsync();
        clock.Advance(Tick);
        var closing = await gw.LossWatchAsync();

        log.WriteLine($"closed                : [{string.Join(", ", closing.Closed)}]");
        Assert.Equal(2, closing.Closed.Count);          // the day AND the position, one pass, two records

        var boundary = Assert.Single(boundaries.All());
        log.WriteLine($"boundary              : {boundary.Id} kind={boundary.Kind} entity={boundary.Entity} rev={boundary.Revision}");
        log.WriteLine($"default disposition   : {boundary.DefaultDisposition}, deadline {boundary.DeadlineAt:yyyy-MM-dd HH:mm}");
        log.WriteLine($"evidence              : {boundary.Evidence}");

        Assert.Equal(BoundaryKind.LossBudget, boundary.Kind);
        Assert.Equal(account, boundary.Entity);
        Assert.Equal(20260310L, boundary.Revision);
        Assert.Equal(BoundaryDisposition.Hold, boundary.DefaultDisposition);
        Assert.True(boundary.IsOpen);
        Assert.Contains("closed", boundary.Evidence, StringComparison.Ordinal);

        // BOTH DIRECTORS ARE WOKEN, ONCE EACH. Two assessments are two turns, and they have to be
        // turns the app actually caused.
        var events = new MissionEventStore(db);
        foreach (var role in CouncilRoles.All)
        {
            var due = events.DueFor(role, Noon.AddMinutes(1)).Where(e => e.Kind == MissionEventKind.Boundary).ToList();
            log.WriteLine($"wake for {role,-11}: {due.Count}");
            Assert.Single(due);
        }

        // AND NOW THE AGENT TRIES. Two refused orders are two more chances to open a boundary.
        foreach (var id in new[] { "b-refused-1", "b-refused-2" })
            await Assert.ThrowsAsync<GatewayDeniedException>(() =>
                gw.PlaceAsync(new AgentContext("a"), id, TestEnv.Buy("NQ")));

        clock.Advance(Tick);
        await gw.LossWatchAsync();

        log.WriteLine($"boundaries after 2 refusals and another pull: {boundaries.All().Count}");
        Assert.Single(boundaries.All());
        foreach (var role in CouncilRoles.All)
            Assert.Single(events.DueFor(role, Noon.AddMinutes(5)).Where(e => e.Kind == MissionEventKind.Boundary));

        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE NEXT DAY IS A DIFFERENT BOUNDARY. The revision is the UTC day, so a second bad day is a
    /// second post-mortem — and the first one is not reopened, reused or overwritten by it.
    /// </summary>
    [Fact]
    public async Task A_breach_on_the_next_utc_day_opens_its_own_boundary()
    {
        var (gw, conn, db, clock) = await Ready(s => s.Risk.MaxDailyLoss = 1_000m);
        using var _1 = db;

        var boundaries = new CouncilBoundaries(db);
        await gw.PlaceAsync(new AgentContext("a"), "d1-open", TestEnv.Buy("ES"));
        conn.Broker.PriceOffset = -20m;

        clock.Advance(Tick);
        await gw.LossWatchAsync();
        clock.Advance(Tick);
        await gw.LossWatchAsync();
        Assert.Single(boundaries.All());

        // The next UTC day: the closure has expired with its key, and the book is still through the
        // budget, so the watch closes the new day too.
        clock.Advance(TimeSpan.FromDays(1));
        await gw.LossWatchAsync();
        clock.Advance(Tick);
        await gw.LossWatchAsync();

        var ids = boundaries.All().Select(b => b.Id).ToList();
        log.WriteLine($"boundaries            : {string.Join(", ", ids)}");
        Assert.Equal(2, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());

        await gw.DisposeAsync();
    }
}

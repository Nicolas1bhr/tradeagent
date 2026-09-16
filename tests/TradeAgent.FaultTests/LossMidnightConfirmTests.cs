using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// THE TWO AGREEING PULLS ARE FILED UNDER THE SCOPE, AND THE RECORD'S DAY IS THE CONFIRMING PULL'S
/// (<c>U-review-med</c>, item 3; REVIEW 2026-09-16 finding 8, probe <c>P4</c>).
///
/// <para><c>Confirmed</c> used to store the first sighting under the BREACH KEY, which carries the
/// UTC day. A pair straddling midnight therefore never agreed: the sighting at 23:59:50Z was filed
/// under yesterday's key, the pull at 00:00:10Z looked under today's, found nothing, and filed
/// itself as a first sighting of its own. At <c>LossWatchInterval</c> 15 s that is the last tick of
/// every UTC day — and the day that went through the owner's budget wrote no <c>loss_breach</c> row
/// at all: nothing flattened, no <c>BoundaryKind.LossBudget</c> boundary, no strike counted, and the
/// only thing still refusing was the admission gate's live figure, which stops refusing the moment
/// the figure recovers.</para>
///
/// <para>A sighting is about a SCOPE — <c>(connector, account, symbol)</c> — and a scope does not
/// change at midnight. The DAY is the confirming pull's, taken where the record is composed, so the
/// row and the key it is written under name the same day and every reader that takes the day off the
/// record (<c>U-scope-identity</c>) reads the day the closure is addressed by.</para>
/// </summary>
public class LossMidnightConfirmTests(ITestOutputHelper log)
{
    sealed class TestClock(DateTimeOffset at) : TimeProvider
    {
        DateTimeOffset _now = at;
        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => _now.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void MoveTo(DateTimeOffset to) => _now = to;
    }

    static async Task<(TradingGateway Gw, RecordingConnector Conn)> Ready(Database db, TestClock clock)
    {
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker())
        {
            EmergencyBudget = Unresolved.PressBudgetFor(1)
        });
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
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        return (gw, conn);
    }

    /// <summary>
    /// THE RED: a loser realised before midnight, first seen at 23:59:50Z and agreed at 00:00:10Z.
    /// The control an hour earlier is the same run with nothing else changed. This is probe
    /// <c>P4</c>, with its observations turned into assertions and the day asserted as well.
    /// </summary>
    [Theory]
    [InlineData("2026-03-10T23:59:30Z", "2026-03-11")]
    [InlineData("2026-03-10T22:59:30Z", "2026-03-10")]
    public async Task A_breach_first_seen_in_the_last_tick_of_a_utc_day_is_still_confirmed(
        string startsAt, string expectedDay)
    {
        var start = DateTimeOffset.Parse(startsAt, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal);
        var clock = new TestClock(start);
        using var db = TestEnv.NewDb();
        var (gw, conn) = await Ready(db, clock);
        var account = conn.Broker.AccountId;

        // A loser, REALISED before midnight: the day's figure is on the ledger and the book is flat.
        await gw.PlaceAsync(new AgentContext("a"), "midnight-open", TestEnv.Buy("ES"));
        conn.Broker.PriceOffset = -25m;
        await gw.PlaceAsync(new AgentContext("a"), "midnight-close",
            new PlaceIntent("ES", OrderSide.Sell, OrderType.Market, 1m, null, null, TimeInForce.Day, null));

        clock.MoveTo(start.AddSeconds(20));   // 23:59:50Z, or 22:59:50Z for the control
        var first = await gw.LossWatchAsync();
        clock.MoveTo(start.AddSeconds(40));   // 00:00:10Z, or 23:00:10Z
        var second = await gw.LossWatchAsync();

        log.WriteLine($"[first sighting at {start.AddSeconds(20).UtcDateTime:HH:mm:ss}Z]");
        log.WriteLine($"  pull 1: reached={first.DayReached} closed=[{string.Join(",", first.Closed)}]");
        log.WriteLine($"  pull 2: reached={second.DayReached} closed=[{string.Join(",", second.Closed)}]");
        log.WriteLine($"  breach rows : [{string.Join(" ", db.KvStartingWith(LossBreach.Prefix).Select(x => x.Key))}]");

        // THE PAIR AGREED, SO THE SCOPE IS CLOSED — on the pull that agreed and not one later.
        Assert.True(first.DayReached);
        Assert.Empty(first.Closed);
        Assert.NotEmpty(second.Closed);

        var breach = gw.DayClosed(account);
        Assert.NotNull(breach);
        log.WriteLine($"  day closed  : True, record day={breach!.Day}");

        // THE DAY IS THE CONFIRMING PULL'S, and it is the day the row is addressed by: a record
        // whose Day disagreed with its own key would send every reader that takes the day off the
        // record (U-scope-identity) to a receipt nobody is going to write.
        Assert.Equal(expectedDay, breach.Day);
        Assert.Equal(LossBreach.Stamp(start.AddSeconds(40)), breach.Day);
        Assert.Equal(second.Closed[0], LossBreach.KeyFor(breach));
        Assert.NotNull(db.GetKv(LossBreach.KeyFor(breach)));

        // AND THE CLOSURE IS A CLOSURE: nothing new reaches the wire.
        var places = conn.Places;
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "midnight-after", TestEnv.Buy("ES")));
        log.WriteLine($"  a new buy   : {denied.Code}");
        Assert.Equal(places, conn.Places);
    }

    /// <summary>
    /// A SCOPE IS NOT A DAY, AND IT IS NOT AN ACCOUNT EITHER. Two instruments whose per-trade
    /// budgets go at the same instant are two sightings and two closures, so keying the sighting on
    /// the scope rather than on the day cannot merge them.
    /// </summary>
    [Fact]
    public async Task Two_symbols_breaching_together_are_two_sightings_and_two_closures()
    {
        var start = new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
        var clock = new TestClock(start);
        using var db = TestEnv.NewDb();
        var (gw, conn) = await Ready(db, clock);
        gw.Update(s => { s.Risk.MaxDailyLoss = 0m; s.Risk.MaxLossPerTrade = 100m; });

        await gw.PlaceAsync(new AgentContext("a"), "two-es", TestEnv.Buy("ES"));
        await gw.PlaceAsync(new AgentContext("a"), "two-nq", TestEnv.Buy("NQ"));
        conn.Broker.PriceOffset = -20m;

        clock.MoveTo(start.AddSeconds(20));
        Assert.Empty((await gw.LossWatchAsync()).Closed);
        clock.MoveTo(start.AddSeconds(40));
        var closed = (await gw.LossWatchAsync()).Closed;

        log.WriteLine($"closed : [{string.Join(" ", closed)}]");
        Assert.Equal(2, closed.Count);
        Assert.NotNull(gw.SymbolClosed(conn.Broker.AccountId, "ES"));
        Assert.NotNull(gw.SymbolClosed(conn.Broker.AccountId, "NQ"));
    }
}

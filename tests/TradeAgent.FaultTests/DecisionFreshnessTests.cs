using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// THE FRESHNESS GATE AT DISPATCH — <c>docs/COUNCIL.md</c>:96-97 and :33, "an expired opportunity
/// takes the policy's safe outcome, never a late trade".
///
/// <para><b>What was wrong before this.</b> The only age the money path knew was a QUOTE's, thirty
/// seconds of it (<c>GatewayOptions.MaxQuoteAge</c>), and it is about the price an order is SIZED
/// from. Nothing anywhere knew how old the closed BAR behind a decision was, so an intent computed
/// an hour ago went to the broker exactly as one computed a second ago did.</para>
///
/// <para><b>Why the gate is at DISPATCH and not in the risk check.</b> Everything between the risk
/// check and the wire is an awaited connector read — an account, a quote, the positions, the loss
/// budget — and at shipped ATAS deadlines that chain is tens of seconds wide. A decision-age check
/// evaluated before those reads is a verdict about a moment that has passed by the time anything is
/// sent, which is the same defect <c>ReauthorizeAtDispatchOrThrow</c> exists for (REVIEW 2026-09-05
/// finding 6). The second test here is that mutant, measured: the clock is advanced INSIDE the
/// position read, and the order must still not go out.</para>
///
/// <para><b>Recorded bars and a controlled clock.</b> There is no live or paper bar feed in this
/// product and `docs/COUNCIL.md` names no unit that produces one. The decision blocks below are the
/// shape <c>IntentDecision.From</c> builds out of a real evaluation over recorded bars, and the age
/// is made by moving the gateway's own <see cref="GatewayOptions.Clock"/> rather than by waiting.
/// Nothing here reaches a venue, and no real money is involved.</para>
/// </summary>
public class DecisionFreshnessTests(ITestOutputHelper log)
{
    /// <summary>A clock the test moves by hand, so "an hour ago" needs no hour.</summary>
    sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    static async Task<(TradingGateway Gw, RecordingConnector Conn, Database Db, TestClock Clock)> Ready()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
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
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        return (gw, conn, db, clock);
    }

    /// <summary>A one-minute bar that closed <paramref name="ago"/> before the clock now reads.</summary>
    static PlaceIntent Decided(TestClock clock, TimeSpan ago, TimeSpan freshness, TimeSpan maxAge)
    {
        var close = clock.GetUtcNow() - ago;
        return new PlaceIntent("ES", ConnectorSdk.OrderSide.Buy, ConnectorSdk.OrderType.Market, 1m,
            null, null, ConnectorSdk.TimeInForce.Day, null)
        {
            Decision = new IntentDecision(close - TimeSpan.FromMinutes(1), close, freshness, maxAge)
        };
    }

    static async Task<string> SwallowAsync(Task<ExecutionRequest> t)
    {
        try { var r = await t; return $"ok — {r.State}"; }
        catch (GatewayDeniedException ex) { return $"{ex.Code} — {ex.Message}"; }
    }

    /// <summary>RED before this unit: an intent an hour old dispatched, and the order filled.</summary>
    [Fact]
    public async Task An_intent_an_hour_old_is_refused_and_nothing_is_sent()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;

        var intent = Decided(clock, TimeSpan.FromHours(1), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30));
        var outcome = await SwallowAsync(gw.PlaceAsync(new AgentContext("a"), "df-old", intent));

        log.WriteLine($"outcome              : {outcome}");
        log.WriteLine($"record               : {gw.GetRequest("df-old")?.State.ToString() ?? "none"}");
        log.WriteLine($"orders at the broker : {conn.Broker.Orders.Count}");

        Assert.StartsWith(ErrorCode.DECISION_EXPIRED.ToString(), outcome, StringComparison.Ordinal);
        Assert.Empty(conn.Broker.Orders);
        Assert.Empty(conn.Placed);

        // A DEFINITE REFUSAL, AND NOT AN UNKNOWN. Nothing left the process, so the record is the one
        // the write-ahead never advanced: CREATED, which a sweep leg reads as `not-sent`.
        Assert.Equal(ExecutionState.CREATED, gw.GetRequest("df-old")!.State);
        Assert.False(gw.GetRequest("df-old")!.NeedsReconciliation);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE MUTANT'S TEST — the check evaluated ABOVE the awaited reads.
    ///
    /// <para>The decision is ten seconds old when the risk check runs, which is inside its
    /// thirty-second bound; the clock is then advanced five minutes INSIDE the dispatch gate's
    /// position read, so by the time anything could be sent it is five minutes past that bound. A
    /// gate in <c>RiskCheckOrThrow</c> has already said yes, and the order goes out:
    /// <c>Expected: refused / Actual: DISPATCHING</c>.</para>
    /// </summary>
    [Fact]
    public async Task An_intent_that_ages_past_its_bound_during_the_reads_is_still_refused()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;

        var aged = 0;
        conn.Seam = kind =>
        {
            if (kind == RecordingConnector.HeldCall.Positions && Interlocked.Exchange(ref aged, 1) == 0)
                clock.Advance(TimeSpan.FromMinutes(5));
            return Task.CompletedTask;
        };

        var intent = Decided(clock, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(30));
        var outcome = await SwallowAsync(gw.PlaceAsync(new AgentContext("a"), "df-aged", intent));

        log.WriteLine($"aged inside the reads: {aged == 1}");
        log.WriteLine($"outcome              : {outcome}");
        log.WriteLine($"record               : {gw.GetRequest("df-aged")?.State.ToString() ?? "none"}");
        log.WriteLine($"orders at the broker : {conn.Broker.Orders.Count}");

        Assert.Equal(1, aged);
        Assert.StartsWith(ErrorCode.DECISION_EXPIRED.ToString(), outcome, StringComparison.Ordinal);
        Assert.Empty(conn.Broker.Orders);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE OTHER BOUND, ON ITS OWN. A decision made a minute ago is inside a `max_decision_age` of
    /// five minutes and outside a `data_freshness` of thirty seconds, and the refusal names which.
    /// </summary>
    [Fact]
    public async Task A_decision_from_a_bar_older_than_its_data_freshness_is_refused_by_name()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;

        var intent = Decided(clock, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));
        var outcome = await SwallowAsync(gw.PlaceAsync(new AgentContext("a"), "df-stale-data", intent));

        log.WriteLine($"outcome : {outcome}");

        Assert.StartsWith(ErrorCode.DECISION_EXPIRED.ToString(), outcome, StringComparison.Ordinal);
        Assert.Contains("data_freshness", outcome, StringComparison.Ordinal);
        Assert.Empty(conn.Broker.Orders);
        await gw.DisposeAsync();
    }

    /// <summary>A decision inside both of its bounds goes out, which is what makes the gate a gate.</summary>
    [Fact]
    public async Task A_decision_inside_both_of_its_bounds_is_sent()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;

        var intent = Decided(clock, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30));
        var outcome = await SwallowAsync(gw.PlaceAsync(new AgentContext("a"), "df-fresh", intent));

        log.WriteLine($"outcome              : {outcome}");
        log.WriteLine($"orders at the broker : {conn.Broker.Orders.Count}");

        Assert.StartsWith("ok", outcome, StringComparison.Ordinal);
        Assert.Single(conn.Placed);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// AN ORDER NO STRATEGY DECIDED IS NOT GATED BY A BOUND IT DOES NOT HAVE. The owner's own buy, a
    /// close and a leg of the emergency press have no closed bar behind them; refusing them for
    /// staleness would be a gate inventing a rule nobody declared.
    /// </summary>
    [Fact]
    public async Task An_order_with_no_decision_behind_it_is_not_refused_for_staleness()
    {
        var (gw, conn, db, _) = await Ready();
        using var _1 = db;

        var outcome = await SwallowAsync(gw.PlaceAsync(new AgentContext("a"), "df-none", TestEnv.Buy()));

        log.WriteLine($"outcome : {outcome}");

        Assert.StartsWith("ok", outcome, StringComparison.Ordinal);
        Assert.Single(conn.Placed);
        await gw.DisposeAsync();
    }
}

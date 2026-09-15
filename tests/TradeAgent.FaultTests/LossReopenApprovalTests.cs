using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// A PROPOSAL THAT PREDATES A BREACH DIES WITH THE BREACH — AND AFTER THE REOPEN TOO.
///
/// <para>A LIVE_CONFIRM proposal is a question about the account as it was when the AI wrote it: this
/// size, against that position, at that price. A confirmed loss breach then cancels every working
/// order that could add risk and CLOSES what is open. So the book the proposal was sized from is not
/// merely older — it is gone, by the app's own hand. Approving it afterwards is not approving what
/// was asked for.</para>
///
/// <para><b>The reopen is what makes this a rule rather than a side effect.</b> While the scope was
/// closed, <c>LOSS_BUDGET_REACHED</c> refused the approval anyway and the request stayed parked,
/// waiting for a day that ends. Once a closure can END (<c>U-reopen-1</c>), that refusal stops and
/// the stale proposal would go through — twenty-four hours late, against a flat account, on a size
/// nobody has looked at since. So it is refused on its own terms, with its own code, and the request
/// is declined for good rather than left on the Dashboard to be pressed again.</para>
/// </summary>
public class LossReopenApprovalTests(ITestOutputHelper log)
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

    /// <summary>
    /// LIVE_CONFIRM with real money on, and an approval window long enough that expiry is not what
    /// is being measured: a proposal parked before a breach is normally expired by
    /// <c>ApprovalTtl</c> (fifteen minutes) long before a closure ends, and this rule has to hold
    /// when it is not — an owner who lengthened the window, a clock that jumped, a restored row.
    /// </summary>
    static async Task<(TradingGateway Gw, RecordingConnector Conn, Database Db, TestClock Clock)> Ready()
    {
        var clock = new TestClock(Noon);
        var db = TestEnv.NewDb();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker())
        {
            EmergencyBudget = Unresolved.PressBudget
        });
        var gw = new TradingGateway(db, conn, new HealthRegistry(), new GatewayOptions
        {
            Clock = clock, ApprovalTtl = TimeSpan.FromDays(30)
        });
        gw.Update(s =>
        {
            s.Mode = TradingMode.LIVE_CONFIRM;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments];
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
            s.Risk.MaxDailyLoss = 1_000m;
        });
        gw.ActivateLive(true);
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        return (gw, conn, db, clock);
    }

    static async Task Park(TradingGateway gw, string requestId, PlaceIntent intent)
    {
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("agent-1"), requestId, intent));
        Assert.Equal(ErrorCode.APPROVAL_REQUIRED, denied.Code);
        Assert.Equal(ExecutionState.AWAITING_APPROVAL, gw.GetRequest(requestId)!.State);
    }

    /// <summary>
    /// THE PROPOSAL WRITTEN BEFORE THE BREACH IS REFUSED AFTER THE REOPEN, ON ITS OWN TERMS (item 5).
    /// </summary>
    [Fact]
    public async Task A_proposal_that_predates_the_breach_is_declined_even_after_the_scope_reopens()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        var account = conn.Broker.AccountId;

        // A POSITION, AND A SECOND ORDER THE AI PROPOSED AGAINST IT — parked, waiting for a person.
        await Park(gw, "held-es", TestEnv.Buy("ES"));
        await gw.ApproveAsync("held-es");
        await Park(gw, "stale", TestEnv.Buy("ES"));
        var parkedAt = gw.GetRequest("stale")!.CreatedAt;

        // THE BREACH, THE FLATTEN, AND THE REOPEN A DAY LATER.
        conn.Broker.PriceOffset = -20m;
        clock.Advance(Tick);
        await gw.LossWatchAsync();
        clock.Advance(Tick);
        await gw.LossWatchAsync();

        var breach = gw.DayClosed(account)!;
        Assert.True(gw.FlattenToday(account)!.Flat);
        log.WriteLine($"parked / confirmed    : {parkedAt:yyyy-MM-dd HH:mm:ss}Z / {breach.ConfirmedAt:yyyy-MM-dd HH:mm:ss}Z");
        Assert.True(parkedAt < breach.ConfirmedAt);

        conn.Broker.PriceOffset = 0m;
        gw.Update(s => s.Risk.MaxDailyLoss = 100_000m);
        clock.MoveTo(LossReopen.EligibleAt(breach.ConfirmedAt, TimeSpan.FromHours(24)) + TimeSpan.FromMinutes(1));
        var pass = await gw.LossWatchAsync();
        Assert.Single(pass.Reopened);
        Assert.Null(gw.DayClosed(account));

        // AND THE STALE PROPOSAL IS STILL REFUSED — with its OWN code, and declined for good.
        var places = conn.Places;
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() => gw.ApproveAsync("stale"));
        log.WriteLine($"approval              : {denied.Code} — {denied.Message}");
        log.WriteLine($"places                : {places} -> {conn.Places}");

        Assert.Equal(ErrorCode.APPROVAL_PREDATES_LOSS_BREACH, denied.Code);
        Assert.NotEqual(ErrorCode.LOSS_BUDGET_REACHED, denied.Code);
        Assert.Equal(places, conn.Places);
        Assert.Equal(ExecutionState.CANCELLED, gw.GetRequest("stale")!.State);

        // Pressed again, it is not approvable at all: it died, rather than being told to wait.
        var again = await Assert.ThrowsAsync<GatewayDeniedException>(() => gw.ApproveAsync("stale"));
        Assert.Equal(ErrorCode.INVALID_REQUEST, again.Code);
        Assert.Equal(places, conn.Places);

        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE SAME REFUSAL WHILE THE SCOPE IS STILL CLOSED, so the proposal does not sit on the
    /// Dashboard for a day being told to come back later (item 5).
    ///
    /// <para>This is the case an owner actually meets, because <c>ApprovalTtl</c> is fifteen minutes
    /// and a breach lands inside one. Before this the answer was <c>LOSS_BUDGET_REACHED</c> and the
    /// row stayed <c>AWAITING_APPROVAL</c> — a button that will never do anything, under a sentence
    /// that says to try again tomorrow.</para>
    /// </summary>
    [Fact]
    public async Task A_proposal_that_predates_the_breach_dies_while_the_scope_is_still_closed()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        var account = conn.Broker.AccountId;

        await Park(gw, "held-es", TestEnv.Buy("ES"));
        await gw.ApproveAsync("held-es");
        await Park(gw, "stale", TestEnv.Buy("NQ"));

        conn.Broker.PriceOffset = -20m;
        clock.Advance(Tick);
        await gw.LossWatchAsync();
        clock.Advance(Tick);
        await gw.LossWatchAsync();
        Assert.NotNull(gw.DayClosed(account));

        var places = conn.Places;
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() => gw.ApproveAsync("stale"));
        log.WriteLine($"approval while closed : {denied.Code} — {denied.Message}");

        Assert.Equal(ErrorCode.APPROVAL_PREDATES_LOSS_BREACH, denied.Code);
        Assert.Equal(places, conn.Places);
        Assert.Equal(ExecutionState.CANCELLED, gw.GetRequest("stale")!.State);

        await gw.DisposeAsync();
    }

    /// <summary>
    /// A PROPOSAL WRITTEN AFTER THE BREACH SURVIVES THE CLOSURE AND IS APPROVED (item 5, the other
    /// half — and the one a mutant takes away).
    ///
    /// <para>The comparison is against <c>ConfirmedAt</c> and not against the instant the closure
    /// became liftable. A proposal written DURING a closure was written by an AI that could already
    /// see the closure on its status, against the book as the flatten left it; there is nothing
    /// stale about it, and refusing it would be refusing work the closure never invalidated. Here
    /// the owner has opened something by hand while the account was shut — which they may always do
    /// — and the AI has proposed a reduce against it.</para>
    /// </summary>
    [Fact]
    public async Task A_proposal_written_during_the_closure_is_still_approved_after_the_reopen()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        var account = conn.Broker.AccountId;

        await Park(gw, "held-es", TestEnv.Buy("ES"));
        await gw.ApproveAsync("held-es");

        conn.Broker.PriceOffset = -20m;
        clock.Advance(Tick);
        await gw.LossWatchAsync();
        clock.Advance(Tick);
        await gw.LossWatchAsync();

        var breach = gw.DayClosed(account)!;
        Assert.True(gw.FlattenToday(account)!.Flat);

        conn.Broker.PriceOffset = 0m;
        gw.Update(s => s.Risk.MaxDailyLoss = 100_000m);

        // THE OWNER OPENS SOMETHING BY HAND while the account is closed, and the AI proposes a
        // reduce against it. A reduce is never refused by a loss budget, so it parks.
        clock.Advance(TimeSpan.FromHours(1));
        conn.Broker.Accept(new PlaceOrderCommand("by-hand", account, "ES", OrderSide.Buy,
            OrderType.Market, 1m, null, null, TimeInForce.Day, "opened by hand"), FillBehaviour.FillImmediately);
        await Park(gw, "during", new PlaceIntent("ES", OrderSide.Sell, OrderType.Market, 1m, null, null,
            TimeInForce.Day, null));

        var parkedAt = gw.GetRequest("during")!.CreatedAt;
        var eligible = LossReopen.EligibleAt(breach.ConfirmedAt, TimeSpan.FromHours(24));
        log.WriteLine($"confirmed / parked    : {breach.ConfirmedAt:yyyy-MM-dd HH:mm}Z / {parkedAt:yyyy-MM-dd HH:mm}Z");
        log.WriteLine($"eligible at           : {eligible:yyyy-MM-dd HH:mm}Z");
        Assert.True(parkedAt > breach.ConfirmedAt);
        Assert.True(parkedAt < eligible);

        // THE OWNER CLOSES IT AGAIN, so the book is flat when the tick looks — item 3's rule, and
        // the reason this test has to put the account back the way it found it.
        conn.Broker.Accept(new PlaceOrderCommand("by-hand-2", account, "ES", OrderSide.Sell,
            OrderType.Market, 1m, null, null, TimeInForce.Day, "closed by hand"), FillBehaviour.FillImmediately);

        // THE REOPEN, and then the proposal is approved and reaches the wire.
        clock.MoveTo(eligible + TimeSpan.FromMinutes(1));
        var pass = await gw.LossWatchAsync();
        Assert.Single(pass.Reopened);

        var places = conn.Places;
        var sent = await gw.ApproveAsync("during");
        log.WriteLine($"approval              : {sent.State}, places {places} -> {conn.Places}");
        Assert.Equal(ExecutionState.FILLED, sent.State);
        Assert.Equal(places + 1, conn.Places);

        await gw.DisposeAsync();
    }
}

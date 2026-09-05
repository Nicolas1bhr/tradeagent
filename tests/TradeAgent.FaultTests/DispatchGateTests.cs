using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// THE GATES ARE DECIDED AT THE MOMENT OF DISPATCH, NOT WHERE THE REQUEST CAME IN (REVIEW
/// 2026-09-05 finding 6, executed as probe P3; Codex F4).
///
/// <c>PlaceAsync</c> called <c>AuthorizeOrThrow</c> at the top and then made four connector reads —
/// the account, positions, a quote, the instruments — before it wrote its record and touched the
/// wire. Every gate that authorize asks about is a switch the person at the keyboard can move: Stop
/// AI trading, the real-money activation, the mode. None of them was consulted again, so a press
/// inside that window did not stop the order it was pressed to stop. At shipped ATAS deadlines the
/// window is 4 × 50 s.
///
/// The rate limit had the same shape and one worse consequence: the count was READ in
/// <c>RiskCheckOrThrow</c> and SPENT two awaited reads later in the dispatcher, so N callers
/// arriving together all read the same free count and all then took it. `MaxOrdersPerMinute = 1`
/// admitted as many orders as there were callers.
///
/// Every test here is a BARRIER test, because that is the only way to observe the window: the
/// connector is held inside a read the request has already passed authorization to reach, the
/// switch is moved while it is held, and then it is released. What is measured is the WIRE.
/// </summary>
public class DispatchGateTests(ITestOutputHelper log)
{
    /// <summary>A gateway whose connector can be stopped inside the risk check's position read.</summary>
    static async Task<(TradingGateway Gw, RecordingConnector Conn, Database Db)> Ready(
        Action<TradeAgentSettings>? settings = null)
    {
        var db = TestEnv.NewDb();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker()))
        {
            Holds = RecordingConnector.HeldCall.Positions
        };
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
            settings?.Invoke(s);
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        // The health refresh reads positions too, and it must not be the thing that gets held.
        conn.Hold = null;
        return (gw, conn, db);
    }

    static async Task<string> SwallowAsync(Task<ExecutionRequest> t)
    {
        try { var r = await t; return $"ok — {r.State}"; }
        catch (GatewayDeniedException ex) { return $"{ex.Code} — {ex.Message}"; }
        catch (Exception ex) { return $"{ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// THE KILL SWITCH, PRESSED WHILE THE ORDER IS IN ITS RISK CHECK. Probe P3 ran this against the
    /// shipped build and got FILLED, one order at the broker, with the switch down.
    /// </summary>
    [Fact]
    public async Task Stop_ai_trading_pressed_while_an_order_is_in_its_risk_check_stops_it()
    {
        var (gw, conn, db) = await Ready();
        using var _1 = db;

        var release = new TaskCompletionSource();
        conn.Hold = release.Task;

        var order = gw.PlaceAsync(new AgentContext("a"), "dg-stop", TestEnv.Buy());
        await conn.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));   // authorized; inside the reads

        gw.StopAiTrading("the owner pressed Stop AI trading");
        release.SetResult();
        var outcome = await SwallowAsync(order);

        log.WriteLine($"AiTradingStopped     : {gw.Settings.AiTradingStopped}");
        log.WriteLine($"outcome              : {outcome}");
        log.WriteLine($"record               : {gw.GetRequest("dg-stop")?.State.ToString() ?? "none"}");
        log.WriteLine($"orders at the broker : {conn.Broker.Orders.Count}   place calls : {conn.Places}");

        Assert.True(gw.Settings.AiTradingStopped);
        Assert.Equal(0, conn.Places);
        Assert.Empty(conn.Broker.Orders);
        Assert.NotEqual(ExecutionState.FILLED, gw.GetRequest("dg-stop")?.State ?? ExecutionState.CREATED);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE SAME WINDOW, THE OTHER SWITCH. Real-money trading switched back off while a live order is
    /// in its reads. The operator's own context is used because it is the one that does not park in
    /// LIVE_CONFIRM — the activation switch applies to it exactly as it does to the AI, and that is
    /// what is being measured.
    /// </summary>
    [Fact]
    public async Task Switching_real_money_off_while_an_order_is_in_its_risk_check_stops_it()
    {
        var (gw, conn, db) = await Ready(s => s.Mode = TradingMode.LIVE_CONFIRM);
        using var _1 = db;
        gw.ActivateLive(true);

        var release = new TaskCompletionSource();
        conn.Hold = release.Task;

        var order = gw.PlaceAsync(AgentContext.Operator, "dg-live", TestEnv.Buy());
        await conn.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        gw.ActivateLive(false);
        release.SetResult();
        var outcome = await SwallowAsync(order);

        log.WriteLine($"LiveActivated        : {gw.Settings.LiveActivated}");
        log.WriteLine($"outcome              : {outcome}");
        log.WriteLine($"orders at the broker : {conn.Broker.Orders.Count}   place calls : {conn.Places}");

        Assert.False(gw.Settings.LiveActivated);
        Assert.Equal(0, conn.Places);
        Assert.Empty(conn.Broker.Orders);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// AND THE MODE, WHICH IS THE ARM RE-RUNNING THE AUTHORIZATION ALONE WOULD MISS.
    ///
    /// The approval path checks that the mode is still LIVE_CONFIRM — and then makes two awaited
    /// connector reads before the wire. A mode moved to PAPER inside that window passes every
    /// remaining check on the way out: PAPER allows execution, it is not live so the activation
    /// switch is not consulted, and the account is a simulation account so the paper guard is happy.
    /// The order was proposed under LIVE_CONFIRM and was about to be sent under rules the person
    /// never chose for it. A record carries the mode it was decided under, and only that mode may
    /// send it.
    /// </summary>
    [Fact]
    public async Task An_approval_is_not_dispatched_after_the_mode_moved_while_it_was_being_checked()
    {
        var (gw, conn, db) = await Ready(s => s.Mode = TradingMode.LIVE_CONFIRM);
        using var _1 = db;
        gw.ActivateLive(true);

        var parked = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "dg-appr", TestEnv.Buy()));
        Assert.Equal(ErrorCode.APPROVAL_REQUIRED, parked.Code);
        Assert.Equal(0, conn.Places);

        var release = new TaskCompletionSource();
        conn.Hold = release.Task;

        var press = gw.ApproveAsync("dg-appr");
        await conn.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));   // inside the approval's own risk check

        gw.SetMode(TradingMode.PAPER);
        release.SetResult();
        var outcome = await SwallowAsync(press);

        log.WriteLine($"mode now             : {gw.Settings.Mode}   (proposed under LIVE_CONFIRM)");
        log.WriteLine($"outcome              : {outcome}");
        log.WriteLine($"record               : {gw.GetRequest("dg-appr")?.State.ToString() ?? "none"}");
        log.WriteLine($"orders at the broker : {conn.Broker.Orders.Count}   place calls : {conn.Places}");

        Assert.Equal(0, conn.Places);
        Assert.Empty(conn.Broker.Orders);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE MINUTE'S BUDGET ADMITS EXACTLY WHAT IT SAYS, however many callers arrive at once. They
    /// are all held inside the position read — so every one of them has passed the limit's early
    /// check on the same free count — and then released as a wave.
    ///
    /// The second row is the other direction and is why this is a theory: "exactly one" alone would
    /// also be satisfied by a gate that had simply stopped letting anything through.
    /// </summary>
    [Theory]
    [InlineData(4, 1)]
    [InlineData(5, 3)]
    public async Task Concurrent_orders_send_exactly_the_minutes_budget_and_no_more(int racers, int limit)
    {
        var (gw, conn, db) = await Ready(s => s.Risk.MaxOrdersPerMinute = limit);
        using var _1 = db;

        var release = new TaskCompletionSource();
        conn.Hold = release.Task;

        var placing = Enumerable.Range(0, racers)
            .Select(i => Task.Run(() => gw.PlaceAsync(new AgentContext("a"), $"dg-race-{limit}-{i}", TestEnv.Buy())))
            .ToList();

        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref conn.Positions) < racers && waited.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(10);
        Assert.Equal(racers, Volatile.Read(ref conn.Positions));

        release.SetResult();
        var outcomes = await Task.WhenAll(placing.Select(SwallowAsync));
        foreach (var o in outcomes) log.WriteLine($"racer : {o}");
        log.WriteLine($"limit {limit}, {racers} callers -> orders at the broker : {conn.Broker.Orders.Count}   place calls : {conn.Places}");

        Assert.Equal(limit, conn.Places);
        Assert.Equal(limit, conn.Broker.Orders.Count);
        Assert.Equal(racers - limit,
            outcomes.Count(o => o.StartsWith(ErrorCode.RISK_LIMIT_EXCEEDED.ToString(), StringComparison.Ordinal)));
        await gw.DisposeAsync();
    }

    /// <summary>
    /// AND THE ORDINARY, UNRACED PATH IS UNTOUCHED: a budget of two admits two and refuses the third,
    /// which is what the limit did before any of this and has to keep doing.
    /// </summary>
    [Fact]
    public async Task The_minutes_budget_still_admits_exactly_its_limit_one_order_at_a_time()
    {
        var (gw, conn, db) = await Ready(s => s.Risk.MaxOrdersPerMinute = 2);
        using var _1 = db;

        Assert.Equal(ExecutionState.FILLED, (await gw.PlaceAsync(new AgentContext("a"), "dg-seq-1", TestEnv.Buy())).State);
        Assert.Equal(ExecutionState.FILLED, (await gw.PlaceAsync(new AgentContext("a"), "dg-seq-2", TestEnv.Buy())).State);

        var third = await SwallowAsync(gw.PlaceAsync(new AgentContext("a"), "dg-seq-3", TestEnv.Buy()));
        log.WriteLine($"third : {third}   place calls : {conn.Places}");
        Assert.StartsWith(ErrorCode.RISK_LIMIT_EXCEEDED.ToString(), third, StringComparison.Ordinal);
        Assert.Equal(2, conn.Places);
        await gw.DisposeAsync();
    }
}

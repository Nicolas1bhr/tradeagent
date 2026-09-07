using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// THE TWO RISK LIMITS THAT WERE DECIDED ON A READING THEY DID NOT OWN (REVIEW 2026-09-05b, Codex
/// F1 and F2).
///
/// <c>MaxOpenPositions</c> counted the positions the platform had already FILLED, and nothing else,
/// from a snapshot taken before the dispatch gate. Two placements arriving together therefore both
/// read the same empty account, both passed a cap of one, and both sent — the cap was a hint about
/// the past rather than a limit on what may be open.
///
/// The notional cap multiplied by a contract size that was allowed to be MISSING: an instrument
/// read that failed was caught and discarded, and the absent multiplier defaulted to 1. On a
/// futures account that is the difference between the order the owner capped and fifty times it.
///
/// Both tests measure the WIRE, because a refusal code says nothing about whether a frame went out.
/// </summary>
public class RiskGateTests(ITestOutputHelper log)
{
    static async Task<(TradingGateway Gw, RecordingConnector Conn, Database Db)> Ready(
        Action<TradeAgentSettings>? settings = null, FaultProfile? faults = null)
    {
        var db = TestEnv.NewDb();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker(), faults));
        var gw = new TradingGateway(db, conn, new HealthRegistry());
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

    /// <summary>
    /// Lets nobody past <paramref name="on"/> until <paramref name="n"/> callers are standing at it.
    /// The only honest way to state "these two placements were in flight together".
    /// </summary>
    static Func<RecordingConnector.HeldCall, Task> Barrier(int n, RecordingConnector.HeldCall on)
    {
        var arrived = 0;
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return async kind =>
        {
            if (kind != on) return;
            if (Interlocked.Increment(ref arrived) >= n) open.TrySetResult();
            await open.Task;
        };
    }

    /// <summary>A clock the test moves by hand, so "yesterday" needs no sleeping through a night.</summary>
    sealed class TestClock : TimeProvider
    {
        DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    static async Task<string> SwallowAsync(Task<ExecutionRequest> t)
    {
        try { var r = await t; return $"ok — {r.State}"; }
        catch (GatewayDeniedException ex) { return $"{ex.Code} — {ex.Message}"; }
        catch (Exception ex) { return $"{ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// TWO OPENING ORDERS IN FLIGHT TOGETHER, A CAP OF ONE, AN EMPTY ACCOUNT (Codex F1).
    ///
    /// Both are barriered at the quote read — after the risk check's own position read and before
    /// the dispatch gate — so each one's view of the account is the empty one it started with.
    /// What is asserted is the WIRE: a cap of one open position may put ONE opening order out.
    /// </summary>
    [Fact]
    public async Task Two_opening_orders_in_flight_together_cannot_both_pass_a_cap_of_one()
    {
        var (gw, conn, db) = await Ready(s => s.Risk.MaxOpenPositions = 1);
        using var _1 = db;
        conn.Seam = Barrier(2, RecordingConnector.HeldCall.Quote);

        var es = gw.PlaceAsync(new AgentContext("a"), "cap-es", TestEnv.Buy("ES"));
        var nq = gw.PlaceAsync(new AgentContext("a"), "cap-nq", TestEnv.Buy("NQ"));
        var outcomes = await Task.WhenAll(SwallowAsync(es), SwallowAsync(nq));

        log.WriteLine($"cap                  : {gw.Settings.Risk.MaxOpenPositions}");
        log.WriteLine($"ES                   : {outcomes[0]}");
        log.WriteLine($"NQ                   : {outcomes[1]}");
        log.WriteLine($"connector place calls: {conn.Places}");
        log.WriteLine($"orders at the broker : {conn.Broker.Orders.Count}");
        log.WriteLine($"positions open       : {conn.Broker.Positions.Count(p => p.Quantity != 0)}");

        Assert.Equal(1, conn.Places);
        Assert.Single(conn.Broker.Orders);
        Assert.Single(conn.Broker.Positions, p => p.Quantity != 0);
        Assert.Contains(outcomes, o => o.StartsWith(ErrorCode.RISK_LIMIT_EXCEEDED.ToString(), StringComparison.Ordinal));
        await gw.DisposeAsync();
    }

    /// <summary>
    /// A RESTING OPENING ORDER IS AN OPEN POSITION THE ACCOUNT HAS NOT SHOWN YET. Nothing fills, so
    /// the platform reports no positions at all and the cap saw a free account for as long as the
    /// order sat on the book.
    /// </summary>
    [Fact]
    public async Task A_working_opening_order_counts_against_the_cap_before_it_fills()
    {
        var (gw, conn, db) = await Ready(s => s.Risk.MaxOpenPositions = 1,
            new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var _1 = db;

        var first = await gw.PlaceAsync(new AgentContext("a"), "rest-es", TestEnv.Buy("ES"));
        log.WriteLine($"first                : {first.State}");
        log.WriteLine($"positions reported   : {conn.Broker.Positions.Count(p => p.Quantity != 0)}");

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "rest-nq", TestEnv.Buy("NQ")));
        log.WriteLine($"second               : {denied.Code} — {denied.Message}");
        log.WriteLine($"orders at the broker : {conn.Broker.Orders.Count}");

        Assert.Equal(ErrorCode.RISK_LIMIT_EXCEEDED, denied.Code);
        Assert.Single(conn.Broker.Orders);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// AND THE CAP STILL LETS THE POSITION IT IS ALREADY IN BE CLOSED. A close is sized from a
    /// position that exists, so counting the working order that opened it must not turn the cap
    /// into a trap the account cannot be flattened out of.
    /// </summary>
    [Fact]
    public async Task A_close_of_the_instrument_already_held_is_not_refused_by_the_cap()
    {
        var (gw, conn, db) = await Ready(s => s.Risk.MaxOpenPositions = 1);
        using var _1 = db;

        await gw.PlaceAsync(new AgentContext("a"), "trap-open", TestEnv.Buy("ES", 2m));
        var closed = await gw.CloseAsync(new AgentContext("a"), "trap-close", "ES");

        log.WriteLine($"close                : {closed?.State.ToString() ?? "none"}");
        log.WriteLine($"orders at the broker : {conn.Broker.Orders.Count}");
        log.WriteLine($"ES after             : {conn.Broker.Positions.FirstOrDefault(p => p.Symbol == "ES")?.Quantity ?? 0m}");

        Assert.Equal(ExecutionState.FILLED, closed!.State);
        Assert.Equal(0m, conn.Broker.Positions.FirstOrDefault(p => p.Symbol == "ES")?.Quantity ?? 0m);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE NOTIONAL CAP CANNOT BE CHECKED WITHOUT THE MULTIPLIER, SO WITHOUT IT NOTHING IS SENT
    /// (Codex F2).
    ///
    /// Two ways the multiplier goes missing, and until this unit both ended at the same silent
    /// substitution — <c>?? 1m</c>, a guess that an ES order's exposure is its price rather than
    /// fifty times it:
    ///
    ///   * the instrument read FAILS, and the failure was caught and discarded;
    ///   * the read SUCCEEDS with metadata carrying no <c>ContractSize</c> — what the ATAS mapping
    ///     produces from a security whose <c>LotSize</c> is zero, which is the unproved field Codex
    ///     names.
    ///
    /// A third, and it is the one that made the guess invisible: a size of ZERO multiplies every
    /// order's value down to nothing, so a cap of any size passes.
    ///
    /// THE CAP SITS BETWEEN THE RAW AND THE MULTIPLIED NOTIONAL — ten times the price against a real
    /// ES multiplier of fifty — which is what makes this a measurement of the harm rather than of
    /// the error code. With the multiplier the order breaches the cap; with the substituted 1 it
    /// does not, and until this unit it went out. What is asserted is the wire, and that the refusal
    /// is not RISK_LIMIT_EXCEEDED: no limit was broken — the gateway could not work out whether one
    /// would be.
    /// </summary>
    [Theory]
    [InlineData("read-fails")]
    [InlineData("no-contract-size")]
    [InlineData("zero-contract-size")]
    public async Task A_notional_cap_that_cannot_be_multiplied_refuses_before_the_wire(string how)
    {
        // 1 < 10 < 50: breached only if the ES multiplier is applied, which is precisely what a
        // missing multiplier hides.
        var (gw, conn, db) = await Ready(s => s.Risk.MaxNotionalPerOrder = FakeBroker.BasePrice("ES") * 10m);
        using var _1 = db;

        // Set AFTER Ready: the health refresh reads a quote, not the instrument list, so the cache
        // this order will find is cold — which is the state a configured install is always in.
        if (how == "read-fails") conn.InstrumentsThrow = new ConnectorTransportException("the platform did not answer");
        else conn.InstrumentsAnswer =
            [new InstrumentInfo("ES", "E-mini S&P 500", "CME", 0.25m, 12.50m, how == "zero-contract-size" ? 0m : null)];

        var before = conn.Calls;
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), $"notional-{how}", TestEnv.Buy("ES")));

        log.WriteLine($"how                  : {how}");
        log.WriteLine($"refusal              : {denied.Code} — {denied.Message}");
        log.WriteLine($"connector place calls: {conn.Places}");
        log.WriteLine($"orders at the broker : {conn.Broker.Orders.Count}");

        Assert.Equal(ErrorCode.RISK_CHECK_UNAVAILABLE, denied.Code);
        Assert.Equal(0, conn.Places);
        Assert.Empty(conn.Broker.Orders);
        Assert.Empty(new ExecutionRequestStore(db).Query());
        Assert.True(conn.Calls > before, "the refusal must be reached, not short-circuited before any read");
        await gw.DisposeAsync();
    }

    // ---- the loss budgets: the limits that are about what HAPPENED, not about what is sent -------

    static PlaceIntent Sell(string symbol, decimal qty) =>
        new(symbol, OrderSide.Sell, OrderType.Market, qty, null, null, TimeInForce.Day, null);

    /// <summary>
    /// Trades the day into a real loss on the simulator and leaves ONE position open, so that both
    /// halves of the rule can be asserted against the same account: the day is past its budget, and
    /// the position that is still open can still be got out of.
    ///
    /// The loss is made the only way a loss is really made — a round trip at a worse price. ES is
    /// bought at 2, the whole market drops twenty points, and the ES position is closed, realising
    /// 2 x 20 x the ES multiplier of 50. The NQ bought beside it stays open and unrealised.
    /// </summary>
    static async Task<decimal> LoseTheDay(TradingGateway gw, RecordingConnector conn)
    {
        await gw.PlaceAsync(new AgentContext("a"), "day-es", TestEnv.Buy("ES", 2m));
        await gw.PlaceAsync(new AgentContext("a"), "day-nq", TestEnv.Buy("NQ", 1m));
        conn.Broker.PriceOffset = -20m;
        await gw.CloseAsync(new AgentContext("a"), "day-es-out", "ES");
        return gw.LedgerPnl(TradingGateway.StartOfDay(DateTimeOffset.UtcNow), "today").Realized;
    }

    /// <summary>
    /// THE DAY'S BUDGET REFUSES NEW RISK AND NOTHING ELSE (brief item 2).
    ///
    /// Nothing above this gate bounds a day: an agent inside every order limit can lose the account
    /// one permitted order at a time, and with no human in the loop for real money there is nobody
    /// to notice. What is asserted is the WIRE — a refusal code says nothing about whether a frame
    /// went out — and the other half of the rule in the same test: the position that is still open
    /// is still closable, because a budget that trapped an account would be the worst possible one.
    /// </summary>
    [Fact]
    public async Task A_day_past_its_loss_budget_refuses_a_new_position_and_still_lets_one_be_closed()
    {
        var (gw, conn, db) = await Ready();
        using var _1 = db;

        var realized = await LoseTheDay(gw, conn);
        gw.Update(s => s.Risk.MaxDailyLoss = 500m);

        var places = conn.Places;
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "after-budget", TestEnv.Buy("NQ")));

        log.WriteLine($"realised today       : {realized}");
        log.WriteLine($"budget               : {gw.Settings.Risk.MaxDailyLoss}");
        log.WriteLine($"refusal              : {denied.Code} — {denied.Message}");
        log.WriteLine($"places before/after  : {places}/{conn.Places}");

        // THE FIGURE, WORKED OUT AGAIN FROM THE PUBLIC PIECES rather than matched against a string
        // somebody typed: the day is what the ledger realised plus what the open NQ is down at the
        // last price this gateway saw, times NQ's contract size of 20. Both numbers and the
        // account's currency have to be in the sentence — a refusal naming one of the two is one an
        // owner cannot act on.
        var nq = conn.Broker.Positions.First(p => p.Symbol == "NQ");
        var last = gw.LastQuote("NQ")!.Last!.Value;
        var expected = -(realized + (last - nq.AveragePrice) * nq.Quantity * 20m);
        log.WriteLine($"day's loss, recomputed: {expected}");

        Assert.Equal(ErrorCode.LOSS_BUDGET_REACHED, denied.Code);
        Assert.Contains("daily loss budget", denied.Message, StringComparison.Ordinal);
        Assert.Contains(Labels.Money(expected, "USD"), denied.Message, StringComparison.Ordinal);
        Assert.Contains(Labels.Money(500m, "USD"), denied.Message, StringComparison.Ordinal);
        Assert.Equal(places, conn.Places);
        Assert.Null(gw.GetRequest("after-budget"));

        // AND THE WAY OUT IS NOT SHUT. The open NQ is closed after the budget is reached, on the
        // same gateway, with the budget still breached.
        var closed = await gw.CloseAsync(new AgentContext("a"), "after-budget-out", "NQ");
        log.WriteLine($"close after the budget: {closed?.State.ToString() ?? "none"}");
        Assert.Equal(ExecutionState.FILLED, closed!.State);
        Assert.Equal(0m, conn.Broker.Positions.FirstOrDefault(p => p.Symbol == "NQ")?.Quantity ?? 0m);

        // One activity line for the day, not one per refused order. An AI that works non-stop will
        // meet a reached budget on every turn it takes.
        await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "after-budget-2", TestEnv.Buy("ES")));
        var said = gw.Log.RecentActivity(200).Count(a => a.Text.Contains(Labels.MaxDailyLoss, StringComparison.Ordinal));
        log.WriteLine($"activity lines       : {said}");
        Assert.Equal(1, said);

        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE PER-POSITION BUDGET REFUSES AN ADD AND ALLOWS A REDUCE (brief item 3).
    ///
    /// Averaging down into a loser is the shape this exists for: every order is inside every order
    /// limit, the position cap is untouched because the instrument is already counted, and the
    /// account is lost one permitted order at a time. What is asserted is the WIRE both ways round —
    /// the add reaches nothing, and the sell against the same position, at the same moment, on the
    /// same breached budget, goes out.
    ///
    /// The DAY'S budget is off throughout, so what is measured here is this gate and not the other.
    /// </summary>
    [Fact]
    public async Task Adding_to_a_position_past_its_own_loss_budget_is_refused_and_reducing_it_is_not()
    {
        var (gw, conn, db) = await Ready();
        using var _1 = db;

        await gw.PlaceAsync(new AgentContext("a"), "add-open", TestEnv.Buy("ES", 2m));
        conn.Broker.PriceOffset = -20m;
        gw.Update(s => { s.Risk.MaxLossPerTrade = 500m; s.Risk.MaxDailyLoss = 0m; });

        var places = conn.Places;
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "add-more", TestEnv.Buy("ES")));

        var es = conn.Broker.Positions.First(p => p.Symbol == "ES");
        var last = gw.LastQuote("ES")!.Last!.Value;
        var down = -((last - es.AveragePrice) * es.Quantity * 50m);

        log.WriteLine($"ES held              : {es.Quantity} at {es.AveragePrice}, last {last}");
        log.WriteLine($"position is down     : {down}");
        log.WriteLine($"refusal              : {denied.Code} — {denied.Message}");
        log.WriteLine($"places before/after  : {places}/{conn.Places}");

        Assert.Equal(ErrorCode.LOSS_BUDGET_REACHED, denied.Code);
        Assert.Contains("ES position is down", denied.Message, StringComparison.Ordinal);
        Assert.Contains(Labels.Money(down, "USD"), denied.Message, StringComparison.Ordinal);
        Assert.Contains(Labels.Money(500m, "USD"), denied.Message, StringComparison.Ordinal);
        Assert.Equal(places, conn.Places);
        Assert.Null(gw.GetRequest("add-more"));

        // THE OTHER HALF, ON THE SAME BREACHED BUDGET. Selling one of the two is smaller than the
        // position it is against, so it reduces — and a budget that refused that would be a budget
        // that holds an owner in a losing trade.
        var reduced = await gw.PlaceAsync(new AgentContext("a"), "add-reduce", Sell("ES", 1m));
        log.WriteLine($"reduce               : {reduced.State}");
        Assert.Equal(ExecutionState.FILLED, reduced.State);
        Assert.Equal(1m, conn.Broker.Positions.First(p => p.Symbol == "ES").Quantity);

        // And the whole of what is left can still be closed.
        var closed = await gw.CloseAsync(new AgentContext("a"), "add-close", "ES");
        log.WriteLine($"close                : {closed?.State.ToString() ?? "none"}");
        Assert.Equal(ExecutionState.FILLED, closed!.State);

        var said = gw.Log.RecentActivity(200).Count(a => a.Text.Contains(Labels.MaxLossPerTrade, StringComparison.Ordinal));
        log.WriteLine($"activity lines       : {said}");
        Assert.Equal(1, said);

        await gw.DisposeAsync();
    }

    /// <summary>
    /// A SYMBOL TRADED TODAY WHOSE MULTIPLIER NOBODY CAN STATE REFUSES THE ORDER (brief item 4).
    ///
    /// Realised profit is <c>(price - average) x quantity x multiplier</c>, so on a futures contract
    /// a missing multiplier is not a rounding error — on ES it is the day's answer divided by fifty,
    /// always in the direction that says the day is fine. <c>trade pnl</c> reports that as
    /// <c>incomplete</c> and prints the figure anyway, which is right for a report and wrong for a
    /// gate: an unknown on the money path is refused, never waved through as a zero.
    ///
    /// XYZ is the simulator's deliberate hole — it quotes and trades a symbol its instrument list
    /// does not describe (see <c>FakeConnector.GetInstrumentsAsync</c>) — so this is the real shape
    /// of the failure rather than a mock of it. RISK_CHECK_UNAVAILABLE and not LOSS_BUDGET_REACHED:
    /// no budget was reached, TradeAgent could not work out whether one had been.
    /// </summary>
    [Fact]
    public async Task A_symbol_traded_today_with_no_contract_size_refuses_instead_of_valuing_the_day_wrong()
    {
        // No value cap, because XYZ has no contract size and that gate would refuse first for its
        // own reasons — exactly as an owner who trades an undescribed symbol has to set none.
        var (gw, conn, db) = await Ready(s => s.Risk.MaxNotionalPerOrder = 0m);
        using var _1 = db;

        await gw.PlaceAsync(new AgentContext("a"), "xyz-open", TestEnv.Buy("XYZ"));
        conn.Broker.PriceOffset = -20m;
        await gw.CloseAsync(new AgentContext("a"), "xyz-out", "XYZ");
        gw.Update(s => s.Risk.MaxDailyLoss = 500m);

        var places = conn.Places;
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "after-xyz", TestEnv.Buy("ES")));

        log.WriteLine($"refusal              : {denied.Code} — {denied.Message}");
        log.WriteLine($"places before/after  : {places}/{conn.Places}");
        log.WriteLine($"row written          : {gw.GetRequest("after-xyz")?.State.ToString() ?? "none"}");

        Assert.Equal(ErrorCode.RISK_CHECK_UNAVAILABLE, denied.Code);
        Assert.Contains("XYZ", denied.Message, StringComparison.Ordinal);
        Assert.Equal(places, conn.Places);
        Assert.Null(gw.GetRequest("after-xyz"));

        await gw.DisposeAsync();
    }

    /// <summary>
    /// AN OPEN POSITION THIS GATEWAY CANNOT VALUE REFUSES TOO, and the case is a restart rather than
    /// a contrivance: TradeAgent stops with a position open, starts again, and has seen no price for
    /// that instrument yet. The platform does not mark its own book either (the simulator reports a
    /// null unrealised, which is what a platform that does not compute one says), so there is no
    /// figure anywhere — and treating that as flat is the software deciding a position it cannot see
    /// is a position that is not losing.
    ///
    /// The allowlist puts NQ first so the health probe's one quote read is NQ's: it is the ES
    /// position that must be unpriced, and the probe would otherwise have priced it.
    /// </summary>
    [Fact]
    public async Task An_open_position_with_no_price_and_no_mark_refuses_rather_than_counting_as_flat()
    {
        var (gw, conn, db) = await Ready();
        using var _1 = db;
        await gw.PlaceAsync(new AgentContext("a"), "restart-open", TestEnv.Buy("ES", 2m));
        await gw.DisposeAsync();

        using var db2 = TestEnv.NewDb();
        var restarted = new TradingGateway(db2, conn, new HealthRegistry());
        restarted.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.InstrumentAllowlist = ["NQ", "ES"];
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
            s.Risk.MaxDailyLoss = 500m;
        });
        await restarted.RefreshHealthAsync();

        log.WriteLine($"ES held              : {conn.Broker.Positions.First(p => p.Symbol == "ES").Quantity}");
        log.WriteLine($"ES marked by platform: {conn.Broker.Positions.First(p => p.Symbol == "ES").UnrealizedPnl?.ToString() ?? "null"}");
        log.WriteLine($"ES quote seen        : {restarted.LastQuote("ES")?.Last?.ToString() ?? "none"}");

        var places = conn.Places;
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            restarted.PlaceAsync(new AgentContext("a"), "after-restart", TestEnv.Buy("NQ")));

        log.WriteLine($"refusal              : {denied.Code} — {denied.Message}");

        Assert.Null(restarted.LastQuote("ES"));
        Assert.Equal(ErrorCode.RISK_CHECK_UNAVAILABLE, denied.Code);
        Assert.Contains("ES", denied.Message, StringComparison.Ordinal);
        Assert.Equal(places, conn.Places);
        Assert.Null(restarted.GetRequest("after-restart"));

        await restarted.DisposeAsync();
    }

    /// <summary>
    /// AND THE THIRD WAY A POSITION CANNOT BE VALUED: the price is there and the MULTIPLIER is not.
    ///
    /// It is its own branch and its own test because it is the one that looks answerable. A price
    /// exists, the arithmetic runs, and it produces <c>(price - average) x quantity x 1</c> — which
    /// on a futures contract is the position's loss divided by its contract size, in the direction
    /// that says the budget has not been reached. That is the substitution REVIEW 2026-09-05b Codex
    /// F2 found in the notional cap, reached from the loss budget's side.
    ///
    /// XYZ is OPEN and was traded YESTERDAY, on a clock the test moves: a symbol that traded today
    /// is caught by the refusal above and this branch is never reached. Overnight is also the shape
    /// this really has — a position held through a session, valued the next morning.
    /// </summary>
    [Fact]
    public async Task An_open_position_with_a_price_but_no_contract_size_refuses_too()
    {
        var (gw, conn, db) = await Ready(s => s.Risk.MaxNotionalPerOrder = 0m);
        using var _1 = db;
        await gw.PlaceAsync(new AgentContext("a"), "xyz-held", TestEnv.Buy("XYZ"));
        await gw.DisposeAsync();

        // Tomorrow, with the position still open: the fill is in the ledger and outside the day.
        var clock = new TestClock();
        clock.Advance(TimeSpan.FromDays(1));
        using var db2 = TestEnv.NewDb();
        var restarted = new TradingGateway(db2, conn, new HealthRegistry(), new GatewayOptions { Clock = clock });
        restarted.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.InstrumentAllowlist = ["XYZ", "ES"];   // the health probe prices XYZ, and only XYZ
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 0m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
            s.Risk.MaxDailyLoss = 500m;
        });
        await restarted.RefreshHealthAsync();

        var today = TradingGateway.StartOfDay(clock.GetUtcNow());
        log.WriteLine($"XYZ quote seen       : {restarted.LastQuote("XYZ")?.Last?.ToString() ?? "none"}");
        log.WriteLine($"fills held / today   : {restarted.LedgerPnl(null, "all").Fills} / {restarted.LedgerPnl(today, "today").Fills}");

        var places = conn.Places;
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            restarted.PlaceAsync(new AgentContext("a"), "after-xyz-held", TestEnv.Buy("ES")));

        log.WriteLine($"refusal              : {denied.Code} — {denied.Message}");

        Assert.NotNull(restarted.LastQuote("XYZ"));          // the PRICE is there
        Assert.Equal(0, restarted.LedgerPnl(today, "today").Fills);   // and nothing traded TODAY
        Assert.Equal(ErrorCode.RISK_CHECK_UNAVAILABLE, denied.Code);
        Assert.Contains("one contract of it is worth", denied.Message, StringComparison.Ordinal);
        Assert.Contains("XYZ", denied.Message, StringComparison.Ordinal);
        Assert.Equal(places, conn.Places);
        Assert.Null(restarted.GetRequest("after-xyz-held"));

        await restarted.DisposeAsync();
    }

    /// <summary>
    /// AND NEITHER BUDGET IS ASKED FOR WHEN NEITHER IS SET — the rule <c>MaxNotionalPerOrder</c> has
    /// and for the same reason (see <c>No_notional_cap_means_the_missing_contract_size_is_not_asked
    /// _for</c>). Both budgets at zero mean not enforced, so the ledger is not read, the instrument
    /// list is not demanded, and the day's undescribed symbol refuses nothing.
    ///
    /// This is the test that stops a loss budget nobody set from becoming a reason a working
    /// installation cannot trade.
    /// </summary>
    [Fact]
    public async Task No_loss_budget_means_the_day_is_never_read_and_nothing_is_refused()
    {
        var (gw, conn, db) = await Ready(s => s.Risk.MaxNotionalPerOrder = 0m);
        using var _1 = db;

        await gw.PlaceAsync(new AgentContext("a"), "free-xyz", TestEnv.Buy("XYZ"));
        conn.Broker.PriceOffset = -20m;
        await gw.CloseAsync(new AgentContext("a"), "free-xyz-out", "XYZ");

        // The platform stops answering about instruments altogether. Nothing may ask it.
        conn.InstrumentsThrow = new ConnectorTransportException("the platform did not answer");

        var placed = await gw.PlaceAsync(new AgentContext("a"), "free-again", TestEnv.Buy("XYZ"));
        log.WriteLine($"budgets              : trade {gw.Settings.Risk.MaxLossPerTrade}, day {gw.Settings.Risk.MaxDailyLoss}");
        log.WriteLine($"placed               : {placed.State}");

        Assert.Equal(0m, gw.Settings.Risk.MaxDailyLoss);
        Assert.Equal(0m, gw.Settings.Risk.MaxLossPerTrade);
        Assert.Equal(ExecutionState.FILLED, placed.State);

        await gw.DisposeAsync();
    }

    /// <summary>
    /// A position INSIDE its budget is added to normally, and a losing position in ONE instrument
    /// does not refuse an order in another. The budget is per position, and a gate that read the
    /// day's total here would be the daily budget wearing the wrong name.
    /// </summary>
    [Fact]
    public async Task A_position_inside_its_budget_is_added_to_and_another_instrument_is_untouched()
    {
        var (gw, conn, db) = await Ready();
        using var _1 = db;

        await gw.PlaceAsync(new AgentContext("a"), "ok-open", TestEnv.Buy("ES", 2m));
        conn.Broker.PriceOffset = -20m;
        gw.Update(s => { s.Risk.MaxLossPerTrade = 100_000m; s.Risk.MaxDailyLoss = 0m; });

        var added = await gw.PlaceAsync(new AgentContext("a"), "ok-add", TestEnv.Buy("ES"));
        log.WriteLine($"add inside the budget: {added.State}");
        Assert.Equal(ExecutionState.FILLED, added.State);

        // Now tighten it: ES is far past the budget, NQ has no position at all and is not refused.
        gw.Update(s => s.Risk.MaxLossPerTrade = 500m);
        var elsewhere = await gw.PlaceAsync(new AgentContext("a"), "ok-nq", TestEnv.Buy("NQ"));
        log.WriteLine($"another instrument   : {elsewhere.State}");
        Assert.Equal(ExecutionState.FILLED, elsewhere.State);

        await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "ok-add-2", TestEnv.Buy("ES")));

        await gw.DisposeAsync();
    }

    /// <summary>
    /// A day INSIDE its budget is not refused, and the figure is the real one rather than a
    /// threshold that happens to be crossed by any loss at all. The same trades, a budget wider
    /// than what they lost, and the next opening order goes to the wire.
    /// </summary>
    [Fact]
    public async Task A_day_inside_its_loss_budget_places_normally()
    {
        var (gw, conn, db) = await Ready();
        using var _1 = db;

        await LoseTheDay(gw, conn);
        gw.Update(s => s.Risk.MaxDailyLoss = 100_000m);

        var placed = await gw.PlaceAsync(new AgentContext("a"), "inside-budget", TestEnv.Buy("ES"));
        log.WriteLine($"placed               : {placed.State}");
        Assert.Equal(ExecutionState.FILLED, placed.State);

        await gw.DisposeAsync();
    }

    /// <summary>
    /// AND THE MULTIPLIER IS ONLY REQUIRED BY THE GATE THAT USES IT. <c>MaxNotionalPerOrder</c> is
    /// zero by default and that means "not enforced" (see <c>RiskPolicy</c>): an installation that
    /// never set a value cap must not be stopped from trading by metadata no gate is asking for.
    /// </summary>
    [Fact]
    public async Task No_notional_cap_means_the_missing_contract_size_is_not_asked_for()
    {
        var (gw, conn, db) = await Ready(s => s.Risk.MaxNotionalPerOrder = 0m);
        using var _1 = db;
        conn.InstrumentsThrow = new ConnectorTransportException("the platform did not answer");

        var placed = await gw.PlaceAsync(new AgentContext("a"), "no-cap", TestEnv.Buy("ES"));
        log.WriteLine($"placed               : {placed.State}");
        log.WriteLine($"orders at the broker : {conn.Broker.Orders.Count}");

        Assert.Equal(ExecutionState.FILLED, placed.State);
        Assert.Single(conn.Broker.Orders);
        await gw.DisposeAsync();
    }
}

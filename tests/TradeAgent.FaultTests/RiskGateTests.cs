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
}

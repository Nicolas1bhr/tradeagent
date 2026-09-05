using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// ONE EMERGENCY PRESS AT A TIME, AND THE REFUSAL IS THE SAME STEP AS THE FIRST DURABLE ROW
/// (REVIEW 2026-09-05 finding 2, executed as probe P10; Codex F6).
///
/// <c>RefuseWhileAPressIsOpen</c> READ the store and <c>OpenPressRow</c> WROTE it, with a connector
/// round trip in between and nothing holding the two together — no lock, no transaction, no
/// uniqueness the second writer could lose on. Two callers arriving together both passed the check,
/// both captured the same position, both passed the drift re-read (neither fill had landed yet) and
/// both sent a market close. P10 measured it: a long 2 became SHORT 2 and both presses answered
/// "ok". <c>OperatorCloseAllAsync</c> has two entry points in this repo — the Dashboard button and
/// <c>GatewayHost/Program.cs</c>, a SECOND PROCESS over the same database — so an in-process lock
/// alone would not have settled it either.
///
/// The test is a BARRIER test because that is the only way to observe the window: both presses are
/// held inside the position read they have already passed the check to reach, then released
/// together. What is measured is the WIRE.
/// </summary>
public class PressAtomicityTests(ITestOutputHelper log)
{
    static async Task<(TradingGateway Gw, RecordingConnector Conn, Database Db)> Ready()
    {
        var db = TestEnv.NewDb();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker()));
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        conn.Hold = null;                       // the health refresh reads positions too
        conn.Holds = RecordingConnector.HeldCall.Positions;
        return (gw, conn, db);
    }

    static async Task<string> SwallowAsync(Task<TradingGateway.PressOutcome> t)
    {
        try { var o = await t; return $"ok — {o.Summary}"; }
        catch (GatewayDeniedException ex) { return $"{ex.Code} — {ex.Message}"; }
        catch (Exception ex) { return $"{ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// P10, TURNED THE RIGHT WAY UP. Two presses released together against one long 2: one set of
    /// wire calls, one press, the other refused in the words the contract promises, and the account
    /// FLAT rather than reversed.
    /// </summary>
    [Fact]
    public async Task Two_close_all_presses_released_together_send_one_close_and_refuse_the_other()
    {
        var (gw, conn, db) = await Ready();
        using var _1 = db;

        await gw.PlaceAsync(new AgentContext("a"), "pa-open", TestEnv.Buy("ES", 2m));
        var before = conn.Broker.Positions.Single();
        log.WriteLine($"position before : {before.Symbol} {before.Quantity}");

        // Both presses are parked inside the capture read — past the check, before any row exists.
        var release = new TaskCompletionSource();
        var baseline = Volatile.Read(ref conn.Positions);
        conn.Hold = release.Task;

        var a = Task.Run(() => gw.OperatorCloseAllAsync());
        var b = Task.Run(() => gw.OperatorCloseAllAsync());

        var waited = 0;
        while (Volatile.Read(ref conn.Positions) - baseline < 2 && waited < 400)
        {
            await Task.Delay(5);
            waited++;
        }
        log.WriteLine($"both inside the capture read : {Volatile.Read(ref conn.Positions) - baseline >= 2}");
        release.SetResult();

        var outcomes = await Task.WhenAll(SwallowAsync(a), SwallowAsync(b));
        log.WriteLine($"press A : {outcomes[0]}");
        log.WriteLine($"press B : {outcomes[1]}");
        log.WriteLine($"close calls on the wire : {conn.Closes}");
        foreach (var o in conn.Broker.Orders)
            log.WriteLine($"  {o.ConnectorOrderId} {o.Side} {o.Quantity} {o.Symbol} {o.State} coid={o.ClientOrderId}");
        var after = conn.Broker.Positions.FirstOrDefault();
        log.WriteLine($"position after  : {(after is null ? "flat" : $"{after.Symbol} {after.Quantity}")}");
        var rows = gw.Requests.Query("request_id LIKE 'op-close-%'");
        log.WriteLine($"press rows      : {rows.Count} -> {string.Join(", ", rows.Select(r => r.RequestId))}");

        // ONE set of wire calls, from ONE press.
        Assert.Equal(1, conn.Closes);
        Assert.Equal(2, conn.Broker.Orders.Count);          // the opening buy and one closing sell
        Assert.True(after is null || after.Quantity == 0m,
            $"expected the account flat, it holds {after?.Quantity}");

        // ...and the other press was refused in the contract's own words.
        var refused = Assert.Single(outcomes, o => o.StartsWith("EMERGENCY_PRESS_UNRESOLVED"));
        Assert.Contains("close-all sent at", refused);
        Assert.Contains("resolve it first", refused);
        Assert.Single(outcomes, o => o.StartsWith("ok —"));

        // One press wrote rows, and they are all one nonce.
        Assert.Single(rows.Select(r => r.RequestId.Split('-')[2]).Distinct());
        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE OTHER DIRECTION, because a guard that refuses everything would pass the test above. A
    /// press whose records the owner has resolved leaves the control usable, and the next press
    /// reaches the wire.
    /// </summary>
    [Fact]
    public async Task A_press_after_the_previous_one_is_resolved_still_reaches_the_wire()
    {
        var (gw, conn, db) = await Ready();
        using var _1 = db;

        await gw.PlaceAsync(new AgentContext("a"), "pa-open-1", TestEnv.Buy("ES", 2m));
        await gw.OperatorCloseAllAsync();
        log.WriteLine($"first press  : close calls {conn.Closes}");
        Assert.Equal(1, conn.Closes);

        // The owner reads the card and confirms every line of it. `ForceResolve` deliberately does
        // not recompute ExecutionCapability, so the card refreshes health afterwards; this is that.
        foreach (var r in gw.Requests.Query("request_id LIKE 'op-close-%'"))
            gw.ForceResolve(r.RequestId, r.State, "checked in ATAS");
        await gw.RefreshHealthAsync();
        log.WriteLine($"unresolved press after the owner confirmed : " +
                      $"{gw.UnresolvedPressNonce(TradingGateway.ClosePress) ?? "none"}");
        Assert.Null(gw.UnresolvedPressNonce(TradingGateway.ClosePress));

        await gw.PlaceAsync(new AgentContext("a"), "pa-open-2", TestEnv.Buy("ES", 1m));
        var outcome = await gw.OperatorCloseAllAsync();
        log.WriteLine($"second press : close calls {conn.Closes} — {outcome.Summary}");

        Assert.Equal(2, conn.Closes);
        await gw.DisposeAsync();
    }
}

using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// THE OWNER'S PER-ORDER LIMITS ARE ASKED AGAIN AT THE WIRE, ON THE PLACE AND THE APPROVAL PATHS
/// ALIKE (<c>U-review-med</c>, item 2; REVIEW 2026-09-16 finding 7, probe <c>P2</c>).
///
/// <para><c>docs/CONTRACTS.md</c> has said "every gate is evaluated at the moment of dispatch, after
/// the awaited reads" since the kill switch was found to be answerable after the fact. What was
/// actually re-evaluated there was the AUTHORIZATION chain and the record's mode
/// (<c>ReauthorizeAtDispatchOrThrow</c>) plus the gates that need the position reading. The
/// instrument allowlist, the quantity cap, the value cap and the quote-age rule were decided in
/// <c>RiskCheckOrThrow</c> ABOVE the gate — so an owner narrowing a limit on the Safety page while
/// an order sat in the gate's position read was answered after the order had gone. The window is one
/// connector round trip, 50 s at shipped ATAS values.</para>
///
/// <para>The barrier is <c>RecordingConnector</c>'s, so the order really is inside the gate's
/// position read while the limits change, and "reached the broker" is the broker's own book.</para>
/// </summary>
public class DispatchOrderLimitsTests(ITestOutputHelper log)
{
    static TradingGateway Build(TradeAgent.Core.Db.Database db, RecordingConnector conn,
        TradingMode mode = TradingMode.PAPER)
    {
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = mode;
            s.LiveActivated = mode != TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments];
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 0m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
            s.Risk.MaxDailyLoss = 1_000_000m;
        });
        return gw;
    }

    /// <summary>
    /// THE RED: the owner takes ES off the allowlist and cuts the size cap from 10 to 1 while an
    /// <c>ES Buy 5</c> is parked in the gate's position read. This is probe <c>P2</c>, with its
    /// observations turned into assertions.
    /// </summary>
    [Fact]
    public async Task An_instrument_taken_off_the_allowlist_inside_the_gate_does_not_reach_the_wire()
    {
        using var db = TestEnv.NewDb();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker()));
        var gw = Build(db, conn);
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.Holds = RecordingConnector.HeldCall.Positions;
        conn.Hold = release.Task;

        var flight = gw.PlaceAsync(new AgentContext("a"), "limits-inflight", TestEnv.Buy("ES", 5m));
        await conn.Reached.Task;   // the order is inside the dispatch gate, in the position read

        // The owner, on the Safety page, takes ES off the allowlist and lowers the size cap.
        gw.Update(s => { s.Risk.InstrumentAllowlist = ["NQ"]; s.Risk.MaxOrderQuantity = 1m; });
        log.WriteLine("owner narrowed the limits while the order was inside the gate");

        release.SetResult();
        var sent = "SENT";
        try { await flight; }
        catch (GatewayDeniedException ex) { sent = ex.Code.ToString(); }

        conn.Hold = null;
        var fresh = "SENT";
        try { await gw.PlaceAsync(new AgentContext("a"), "limits-after", TestEnv.Buy("ES", 5m)); }
        catch (GatewayDeniedException ex) { fresh = ex.Code.ToString(); }

        log.WriteLine($"the order in flight       : {sent}");
        log.WriteLine($"orders at the broker      : [{string.Join(",", conn.Broker.Orders.Select(o => $"{o.Symbol} {o.Side} {o.Quantity}"))}]");
        log.WriteLine($"the same order, fresh     : {fresh}");

        // THE SAME ANSWER FOR THE SAME ORDER, whichever side of the read it was on.
        Assert.Equal(ErrorCode.RISK_LIMIT_EXCEEDED.ToString(), sent);
        Assert.Equal(fresh, sent);
        Assert.Empty(conn.Broker.Orders);
        Assert.Equal(0, conn.Places);
    }

    /// <summary>
    /// THE SAME QUESTION ON THE APPROVAL PATH. A parked proposal's limits are re-run inside the gate
    /// beside the position gates, so a size cap narrowed while a person was deciding is the cap the
    /// press is answered against.
    /// </summary>
    [Fact]
    public async Task A_size_cap_narrowed_while_a_proposal_waited_refuses_the_approval()
    {
        using var db = TestEnv.NewDb();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker()));
        var gw = Build(db, conn, TradingMode.LIVE_CONFIRM);
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        var parked = "SENT";
        try { await gw.PlaceAsync(new AgentContext("a"), "limits-park", TestEnv.Buy("ES", 5m)); }
        catch (GatewayDeniedException ex) { parked = ex.Code.ToString(); }
        log.WriteLine($"parked                    : {parked}");
        Assert.Equal(ErrorCode.APPROVAL_REQUIRED.ToString(), parked);

        // The owner narrows the cap on the Safety page, then presses Approve on the card.
        gw.Update(s => s.Risk.MaxOrderQuantity = 1m);

        var places = conn.Places;
        var approved = "SENT";
        try { await gw.ApproveAsync("limits-park"); }
        catch (GatewayDeniedException ex) { approved = ex.Code.ToString(); }

        log.WriteLine($"the PARKED order, approved: {approved}");
        log.WriteLine($"orders that reached the wire: {conn.Places - places}");

        Assert.Equal(ErrorCode.RISK_LIMIT_EXCEEDED.ToString(), approved);
        Assert.Equal(places, conn.Places);
    }

    /// <summary>
    /// THE CONTROL: with nothing narrowed, the order the gate admitted still goes. A guard that
    /// refused everything would pass the two above and be worthless.
    /// </summary>
    [Fact]
    public async Task An_order_whose_limits_did_not_move_still_reaches_the_wire()
    {
        using var db = TestEnv.NewDb();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker()));
        var gw = Build(db, conn);
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.Holds = RecordingConnector.HeldCall.Positions;
        conn.Hold = release.Task;

        var flight = gw.PlaceAsync(new AgentContext("a"), "limits-ok", TestEnv.Buy("ES", 5m));
        await conn.Reached.Task;
        release.SetResult();

        var done = await flight;
        log.WriteLine($"state                     : {done.State}");
        log.WriteLine($"orders at the broker      : [{string.Join(",", conn.Broker.Orders.Select(o => $"{o.Symbol} {o.Side} {o.Quantity}"))}]");
        Assert.Equal(1, conn.Places);
    }
}

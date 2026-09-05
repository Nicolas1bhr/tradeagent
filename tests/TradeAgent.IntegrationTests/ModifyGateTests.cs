using System.Text.Json;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using TradeAgent.TradeCli;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// EVERY MUTATING VERB PASSES THE SAME GATES, AND `modify` DID NOT (REVIEW 2026-09-05, Codex F2).
///
/// <c>TradingGateway.ModifyAsync</c> called <c>AuthorizeOrThrow</c> and then went to the wire. That
/// is the kill switch, the mode and the unconfirmed-work pause — and nothing else. It never called
/// <c>RiskCheckOrThrow</c>, so the quantity cap, the notional cap, the open-position limit, the
/// instrument allowlist and the rate limit did not apply to it; and it never parked, so in
/// LIVE_CONFIRM a change no person had seen went straight to the broker.
///
/// A working order is a live claim on the account. Raising its quantity from 1 to 1000 is the same
/// act as placing a 1000 order, arrived at by a different verb, and it was not bounded by any of the
/// numbers the owner set. These tests are over the real pipe because that is the boundary the claim
/// is about: an authenticated agent session, the ordinary `modify` frame.
///
/// The measurement is the WIRE, not the error code — <see cref="RecordingConnector"/> counts the
/// calls that reached the connector, and the fake broker does not write a modification back into its
/// book, so nothing else could tell a refused change from an applied one.
/// </summary>
public class ModifyGateTests(ITestOutputHelper log)
{
    static string NewPipe() => "ta-modgate-" + Guid.NewGuid().ToString("n")[..12];

    /// <summary>A gateway whose orders stay WORKING, so there is always something to modify.</summary>
    static async Task<(TradingGateway Gw, RecordingConnector Conn, Database Db)> Ready(Action<TradeAgentSettings> settings)
    {
        var db = TestEnv.NewDb();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker(), new FaultProfile { Fill = FillBehaviour.LeaveWorking }));
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
            settings(s);
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        return (gw, conn, db);
    }

    static IpcRequest Modify(string requestId, string target, string? quantity = null, string? limit = null)
    {
        var req = new IpcRequest { Op = Ops.Modify, Session = "a", RequestId = requestId, Args = new() };
        req.Args["id"] = JsonSerializer.SerializeToElement(target);
        if (quantity is not null) req.Args["quantity"] = JsonSerializer.SerializeToElement(quantity);
        if (limit is not null) req.Args["limit"] = JsonSerializer.SerializeToElement(limit);
        return req;
    }

    static PlaceIntent Buy(string symbol = "ES", decimal qty = 1m) =>
        new(symbol, OrderSide.Buy, OrderType.Market, qty, null, null, TimeInForce.Day, null);

    /// <summary>
    /// THE FINDING, RUN. LIVE_CONFIRM, the owner's quantity cap is 1, and an agent asks over the
    /// authenticated pipe for the working quantity-1 order to become a quantity-1000 order.
    ///
    /// Both gates the change has to pass are missing at once, so either one refusing is enough for
    /// the assertion that matters: nothing reached the wire.
    /// </summary>
    [Fact]
    public async Task A_modify_that_breaks_the_quantity_limit_never_reaches_the_wire()
    {
        var (gw, conn, db) = await Ready(s =>
        {
            s.Mode = TradingMode.LIVE_CONFIRM;
            s.Risk.MaxOrderQuantity = 1m;
        });
        using var _1 = db;
        gw.ActivateLive(true);

        // The order the agent is about to try to grow, placed by the person at the keyboard so that
        // it is working rather than parked.
        var working = await gw.PlaceAsync(AgentContext.Operator, "mg-open", Buy());
        Assert.Equal(ExecutionState.WORKING, working.State);

        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        var before = conn.Modifies;
        var reply = await client.SendAsync(Modify("mg-1", working.ConnectorOrderId!, quantity: "1000"))
            .WaitAsync(TimeSpan.FromSeconds(20));

        log.WriteLine($"reply                 : ok={reply.Ok} code={reply.Error?.Code} — {reply.Error?.Message}");
        log.WriteLine($"record                : {gw.GetRequest("mg-1")?.State.ToString() ?? "none"}");
        log.WriteLine($"modify calls on the wire : {conn.Modifies - before}");

        Assert.False(reply.Ok,
            $"an agent grew a working order from 1 to 1000 past a quantity cap of 1: {Json.Write(reply.Data)}");
        Assert.Equal(before, conn.Modifies);
        Assert.NotEqual(ExecutionState.ACKNOWLEDGED, gw.GetRequest("mg-1")?.State ?? ExecutionState.CREATED);
    }

    /// <summary>
    /// THE APPROVAL HALF. Within the owner's limits, and still nobody has seen it: in LIVE_CONFIRM
    /// an agent's modification parks exactly as its placement does, and the person's press is what
    /// sends it. Zero calls before the press, exactly one after.
    /// </summary>
    [Fact]
    public async Task A_modify_in_live_confirm_parks_for_a_person_and_only_the_press_sends_it()
    {
        var (gw, conn, db) = await Ready(s => s.Mode = TradingMode.LIVE_CONFIRM);
        using var _1 = db;
        gw.ActivateLive(true);

        var working = await gw.PlaceAsync(AgentContext.Operator, "mg2-open", Buy());
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        var before = conn.Modifies;
        var reply = await client.SendAsync(Modify("mg2-mod", working.ConnectorOrderId!, quantity: "2"))
            .WaitAsync(TimeSpan.FromSeconds(20));

        var parked = gw.GetRequest("mg2-mod");
        log.WriteLine($"reply                    : ok={reply.Ok} code={reply.Error?.Code} — {reply.Error?.Message}");
        log.WriteLine($"record                   : {parked?.State.ToString() ?? "none"}");
        log.WriteLine($"modify calls before press: {conn.Modifies - before}");

        Assert.False(reply.Ok);
        Assert.Equal(ErrorCode.APPROVAL_REQUIRED.ToString(), reply.Error?.Code);
        Assert.NotNull(parked);
        Assert.Equal(ExecutionState.AWAITING_APPROVAL, parked!.State);
        Assert.Equal(RequestIntent.MODIFY, parked.Intent);
        Assert.Equal(before, conn.Modifies);

        var approved = await gw.ApproveAsync("mg2-mod");
        log.WriteLine($"after the press          : {approved.State}, modify calls={conn.Modifies - before}");

        Assert.Equal(ExecutionState.ACKNOWLEDGED, approved.State);
        Assert.Equal(before + 1, conn.Modifies);
    }

    /// <summary>
    /// THE OTHER DIRECTION, which is what keeps the fix from being "refuse every modification". A
    /// change inside every limit, in the simulator, still goes through and is still recorded as
    /// applied.
    /// </summary>
    [Fact]
    public async Task A_modify_within_the_limits_in_paper_mode_still_applies()
    {
        var (gw, conn, db) = await Ready(_ => { });
        using var _1 = db;

        var working = await gw.PlaceAsync(new AgentContext("a"), "mg3-open", Buy());
        Assert.Equal(ExecutionState.WORKING, working.State);

        var modified = await gw.ModifyAsync(new AgentContext("a"), "mg3-mod", working.ConnectorOrderId!, 3m, null, null);

        log.WriteLine($"record : {modified.State}   modify calls : {conn.Modifies}");
        Assert.Equal(ExecutionState.ACKNOWLEDGED, modified.State);
        Assert.Equal(1, conn.Modifies);
    }

    /// <summary>
    /// THE INSTRUMENT ALLOWLIST IS A GATE LIKE ANY OTHER, and a verb that skips the risk check skips
    /// this one too. The owner allows MES; the working order is on ES, put there before the
    /// allowlist narrowed. A modification to it is a fresh act on an instrument the owner has
    /// withdrawn, and it is refused with nothing on the wire.
    /// </summary>
    [Fact]
    public async Task A_modify_of_an_order_on_an_instrument_the_owner_withdrew_is_refused()
    {
        var (gw, conn, db) = await Ready(_ => { });
        using var _1 = db;

        var working = await gw.PlaceAsync(new AgentContext("a"), "mg4-open", Buy());
        gw.Update(s => s.Risk.InstrumentAllowlist = ["MES"]);

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.ModifyAsync(new AgentContext("a"), "mg4-mod", working.ConnectorOrderId!, 2m, null, null));

        log.WriteLine($"refusal : {denied.Code} — {denied.Message}   modify calls : {conn.Modifies}");
        Assert.Equal(ErrorCode.RISK_LIMIT_EXCEEDED, denied.Code);
        Assert.Equal(0, conn.Modifies);
    }
}

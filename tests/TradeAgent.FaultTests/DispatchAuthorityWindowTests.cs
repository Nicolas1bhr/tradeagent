using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// HOW WIDE THE LAST-CHECK-TO-WIRE WINDOW REALLY IS (REVIEW 2026-09-05b, Codex F5).
///
/// The claim: dispatch re-authorization is not atomic with <c>SetMode</c>, <c>ActivateLive</c>,
/// <c>StopAiTrading</c> or <c>Update</c>, because none of those writers takes the dispatch gate, so
/// a revoke landing between the re-check and the wire still sends. It is TWO windows wearing one
/// name, and they have opposite answers.
///
///   * ONE IS THE GATEWAY'S OWN, AND IT WAS REAL. <c>DispatchPlaceAsync</c> re-authorized and then,
///     on a close, made an awaited connector read — <c>RefuseAStaleCloseOrThrow</c>'s position
///     re-read — before touching the wire. A revoke inside that read arrived after the last gate had
///     already been passed, so the close went out with the switch down. One connector round trip
///     wide: 50 s at shipped ATAS values (<c>Stranded.AtasOrderPath</c>). Closed, by making the
///     re-check the LAST thing before the wire with nothing awaited after it.
///
///   * THE OTHER IS THE CONNECTOR'S OWN SEND, AND IT CANNOT BE CLOSED. Once the command is inside
///     <c>PlaceOrderAsync</c> the frame is on its way, and the gateway's only levers are worse than
///     the window: it already holds <c>_dispatchGate</c> across the whole wire call, so making the
///     writers take that gate would not stop the order — it would make the owner's press WAIT for
///     it, up to a full <c>WorstCaseOperationPath</c>, which on a kill switch is the wrong thing to
///     do with a person's emergency. Cancelling the send instead manufactures exactly the ambiguity
///     safety rule 3 exists to avoid: a cancelled write cannot say whether the broker saw the order,
///     so the record settles UNKNOWN, trading pauses on unconfirmed work, and one authorized order
///     becomes an unresolved position. The second test here REFUTES that half by measuring it, and
///     `docs/CONTRACTS.md` states the bound.
/// </summary>
public class DispatchAuthorityWindowTests(ITestOutputHelper log)
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
            s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments];
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        return (gw, conn, db);
    }

    /// <summary>
    /// THE KILL SWITCH, PRESSED WHILE A CLOSE IS INSIDE ITS STALE-POSITION RE-READ — the one read
    /// the dispatcher makes AFTER it has re-authorized.
    ///
    /// The moment is identified by the RECORD rather than by counting reads: a close makes four
    /// position reads in all (sizing, the open-position cap, this one, and the fake broker's own
    /// bookkeeping), and only the ones from the dispatcher onwards happen with a row already in the
    /// store — <c>TryCreate</c> runs inside the dispatch gate, before <c>DispatchPlaceAsync</c>. So
    /// "a position read while this request id exists" is exactly the window Codex names, and it
    /// stays exact if the chain grows another read somewhere else.
    /// </summary>
    [Fact]
    public async Task Stop_ai_trading_pressed_inside_a_closes_position_re_read_stops_the_close()
    {
        var (gw, conn, db) = await Ready();
        using var _1 = db;

        await gw.PlaceAsync(new AgentContext("a"), "aw-open", TestEnv.Buy("ES", 2m));
        var opened = conn.Broker.Orders.Count;

        var pressed = 0;
        conn.Seam = kind =>
        {
            if (kind == RecordingConnector.HeldCall.Positions
                && gw.GetRequest("aw-close") is not null
                && Interlocked.Exchange(ref pressed, 1) == 0)
                gw.StopAiTrading("the owner pressed Stop AI trading");
            return Task.CompletedTask;
        };

        string outcome;
        try { outcome = $"ok — {(await gw.CloseAsync(new AgentContext("a"), "aw-close", "ES"))?.State.ToString() ?? "none"}"; }
        catch (GatewayDeniedException ex) { outcome = $"{ex.Code} — {ex.Message}"; }

        log.WriteLine($"pressed inside the re-read : {pressed == 1}");
        log.WriteLine($"AiTradingStopped           : {gw.Settings.AiTradingStopped}");
        log.WriteLine($"outcome                    : {outcome}");
        log.WriteLine($"record                     : {gw.GetRequest("aw-close")?.State.ToString() ?? "none"}");
        log.WriteLine($"orders at the broker       : {conn.Broker.Orders.Count} (was {opened})");
        log.WriteLine($"ES after                   : {conn.Broker.Positions.FirstOrDefault(p => p.Symbol == "ES")?.Quantity ?? 0m}");

        Assert.Equal(1, pressed);
        Assert.True(gw.Settings.AiTradingStopped);
        Assert.Equal(opened, conn.Broker.Orders.Count);
        Assert.Equal(2m, conn.Broker.Positions.FirstOrDefault(p => p.Symbol == "ES")?.Quantity ?? 0m);
        Assert.NotEqual(ExecutionState.FILLED, gw.GetRequest("aw-close")?.State ?? ExecutionState.CREATED);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// AND THE SAME PRESS, ONE STEP LATER, DOES NOT RECALL THE ORDER — Codex F5's probe run exactly
    /// as it is written, and kept because the answer is NO rather than YES.
    ///
    /// The connector is paused immediately before its simulated wire write, authority is revoked,
    /// and it is released: the order is placed. Nothing above this line can prevent that. The
    /// gateway's last gate has been passed, the frame is inside the connector, and the two ways to
    /// reach into it are both worse than the window — see the class note. What the software owes
    /// here is the truth about the bound, not a claim to have closed it: ONE
    /// <c>WorstCaseOperationPath</c>, 50 s at shipped ATAS values, during which an order the owner
    /// has just revoked permission for may still reach the broker.
    ///
    /// This test is therefore a REFUTATION, and it fails if anyone ever quietly makes the send
    /// cancellable: that would be a behaviour change on the money path — an order in an UNKNOWN
    /// state instead of a placed one — and it must be argued for, not slipped in.
    /// </summary>
    [Fact]
    public async Task Stop_ai_trading_pressed_inside_the_connectors_send_does_not_recall_the_order()
    {
        var (gw, conn, db) = await Ready();
        using var _1 = db;

        var release = new TaskCompletionSource();
        conn.Holds = RecordingConnector.HeldCall.Place;
        conn.Hold = release.Task;

        var order = gw.PlaceAsync(new AgentContext("a"), "aw-wire", TestEnv.Buy());
        await conn.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));   // inside the connector's send

        gw.StopAiTrading("the owner pressed Stop AI trading");
        release.SetResult();
        var placed = await order;

        log.WriteLine($"AiTradingStopped     : {gw.Settings.AiTradingStopped}");
        log.WriteLine($"record               : {placed.State}");
        log.WriteLine($"orders at the broker : {conn.Broker.Orders.Count}   place calls : {conn.Places}");
        log.WriteLine($"window bound         : one WorstCaseOperationPath — " +
                      $"{Stranded.AtasOrderPath.TotalSeconds:0}s at shipped ATAS values");

        Assert.True(gw.Settings.AiTradingStopped);
        Assert.Equal(1, conn.Places);
        Assert.Single(conn.Broker.Orders);
        Assert.Equal(ExecutionState.FILLED, placed.State);

        // AND THE NEXT ONE IS REFUSED, which is what makes the window a window rather than a hole:
        // the switch bites everything that has not already been handed to the connector.
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "aw-after", TestEnv.Buy()));
        log.WriteLine($"the next order       : {denied.Code} — {denied.Message}");
        Assert.Equal(ErrorCode.AI_TRADING_STOPPED, denied.Code);
        Assert.Single(conn.Broker.Orders);
        await gw.DisposeAsync();
    }
}

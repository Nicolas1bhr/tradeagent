using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// A REPLAY IS BOUND TO THE VERB AND THE SESSION THAT MADE IT, AND IS LOOKED UP BEFORE ANY LIVE READ
/// (REVIEW 2026-09-05, Codex F7 — <c>TradingGateway.BeginComposite</c>).
///
/// <c>composite_request</c> has carried <c>op</c> and <c>agent_session_id</c> since it was
/// introduced, and nothing read them back: a known id returned its stored plan, nonce and answer to
/// whoever asked, under whatever verb they asked with. And the lookup came AFTER the caller had read
/// the book, so the one situation a request id exists for — a lost reply, re-sent — could not be
/// answered at all when the platform was unreachable.
///
/// The tests mirror what <c>GatewayPipeServer.CancelAll</c> and <c>CloseAll</c> do, in their order,
/// because the ordering IS the finding: read the book, then ask whether this id has been seen.
/// </summary>
public class CompositeReplayBindingTests(ITestOutputHelper log)
{
    /// <summary>
    /// A sweep exactly as the pipe server runs one: the plan is captured from a LIVE read, the
    /// composite is claimed, a replay returns the stored answer and touches nothing.
    /// </summary>
    static async Task<string> SweepAsync(TradingGateway gw, AgentContext ctx, string op, string rid,
        string nonce, CancellationToken ct = default)
    {
        var composite = await gw.BeginCompositeAsync(ctx, rid, op,
            async c => op == Ops.CancelAll
                ? (await gw.OrdersAsync(false, c)).Select(o => o.ConnectorOrderId).ToList()
                : (await gw.PositionsAsync(c)).Where(p => p.Quantity != 0).Select(p => p.Symbol).ToList(),
            () => nonce, ct);

        if (composite.StoredResultJson is { } answered) return answered;

        var legs = 0;
        for (var i = 0; i < composite.Targets.Count; i++)
        {
            var legId = $"op-{composite.Nonce}-{(op == Ops.CancelAll ? "cancelall" : "closeall")}-{i}";
            if (op == Ops.CancelAll) await gw.CancelAsync(ctx, legId, composite.Targets[i], ct);
            else await gw.CloseAsync(ctx, legId, composite.Targets[i], ct);
            legs++;
        }

        var result = Json.Write(new { op, legs, targets = composite.Targets });
        gw.CompleteComposite(rid, result);
        return result;
    }

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
        return (gw, conn, db);
    }

    /// <summary>
    /// AN INCOMPLETE CLOSE ALL, REPLAYED AS CANCEL ALL. The stored plan is a list of INSTRUMENTS and
    /// the verb asked for acts on ORDER IDS; resuming it runs the wrong operation over the wrong
    /// plan. Refused, and nothing reaches the wire.
    /// </summary>
    [Fact]
    public async Task An_incomplete_close_all_replayed_as_cancel_all_is_refused_and_sends_nothing()
    {
        var (gw, conn, db) = await Ready();
        using var _1 = db;
        var ctx = new AgentContext("a");

        await gw.PlaceAsync(ctx, "cr-open", TestEnv.Buy("ES", 2m));

        // A close-all that claimed its id and died before it answered — the resumable case.
        gw.BeginComposite(ctx, "cr-1", Ops.CloseAll, ["ES"], () => "aaaa0000");
        var seeded = gw.Composites.Get("cr-1")!;
        log.WriteLine($"seeded composite : op={seeded.Op} session={seeded.AgentSessionId} " +
                      $"plan={seeded.PlanJson} result={seeded.ResultJson ?? "none"}");

        var mutations = conn.Mutations;
        var thrown = await Record.ExceptionAsync(() => SweepAsync(gw, ctx, Ops.CancelAll, "cr-1", "bbbb0000"));
        log.WriteLine($"replayed as cancel-all : {(thrown is GatewayDeniedException d ? $"{d.Code} — {d.Message}" : thrown?.ToString() ?? "ACCEPTED, no refusal")}");
        log.WriteLine($"wire calls during the replay : {conn.Mutations - mutations}");
        log.WriteLine($"leg records under the replay's nonce : " +
                      $"{gw.Requests.Query("request_id LIKE 'op-bbbb0000-%'").Count}");
        log.WriteLine($"composite op after           : {gw.Composites.Get("cr-1")!.Op}");

        // Refused for the RIGHT reason: the id already names a close-all. Before this it was
        // ACCEPTED and resumed, and what stopped it was the close-all's plan happening not to look
        // like an order id — an accident of the fake's book, not a rule.
        var denied = Assert.IsType<GatewayDeniedException>(thrown);
        Assert.Equal(ErrorCode.INVALID_REQUEST, denied.Code);
        Assert.Contains(Ops.CloseAll, denied.Message);
        Assert.Equal(mutations, conn.Mutations);
        Assert.Empty(gw.Requests.Query("request_id LIKE 'op-bbbb0000-%'"));
        await gw.DisposeAsync();
    }

    /// <summary>
    /// A COMPLETED CLOSE ALL, REPLAYED AS CANCEL ALL, USED TO ANSWER WITH THE CLOSE ALL'S REPLY —
    /// the worse half of the same defect, because it succeeds. The agent is handed a stored result
    /// for an operation it did not ask for and every order it meant to cancel is still working.
    /// </summary>
    [Fact]
    public async Task A_completed_close_all_replayed_as_cancel_all_does_not_answer_with_its_result()
    {
        var (gw, conn, db) = await Ready();
        using var _1 = db;
        var ctx = new AgentContext("a");

        await gw.PlaceAsync(ctx, "cr-open", TestEnv.Buy("ES", 2m));
        var first = await SweepAsync(gw, ctx, Ops.CloseAll, "cr-2", "cccc0000");
        log.WriteLine($"close-all answered  : {first}");

        var thrown = await Record.ExceptionAsync(() => SweepAsync(gw, ctx, Ops.CancelAll, "cr-2", "dddd0000"));
        log.WriteLine($"cancel-all with the same id : " +
                      $"{(thrown is GatewayDeniedException d ? $"{d.Code} — {d.Message}" : "ACCEPTED — " + await SweepAsync(gw, ctx, Ops.CancelAll, "cr-2", "dddd0000"))}");

        var denied = Assert.IsType<GatewayDeniedException>(thrown);
        Assert.Equal(ErrorCode.INVALID_REQUEST, denied.Code);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// ANOTHER SESSION MAY NOT REPLAY THIS ID. A request id is a name inside one conversation; a
    /// second agent reusing it would be handed the first one's plan, nonce and answer.
    /// </summary>
    [Fact]
    public async Task A_composite_from_another_session_is_not_replayable_here()
    {
        var (gw, conn, db) = await Ready();
        using var _1 = db;

        await gw.PlaceAsync(new AgentContext("a"), "cr-open", TestEnv.Buy("ES", 2m));
        await SweepAsync(gw, new AgentContext("a"), Ops.CloseAll, "cr-3", "eeee0000");

        var thrown = await Record.ExceptionAsync(() =>
            SweepAsync(gw, new AgentContext("b"), Ops.CloseAll, "cr-3", "ffff0000"));
        log.WriteLine($"session b replaying session a's id : " +
                      $"{(thrown is GatewayDeniedException d ? $"{d.Code} — {d.Message}" : "ACCEPTED, no refusal")}");

        var denied = Assert.IsType<GatewayDeniedException>(thrown);
        Assert.Equal(ErrorCode.INVALID_REQUEST, denied.Code);
        Assert.Contains("another session", denied.Message);

        // ...and the session that made it still gets its answer.
        var mine = await SweepAsync(gw, new AgentContext("a"), Ops.CloseAll, "cr-3", "ffff0000");
        log.WriteLine($"session a replaying its own id     : {mine}");
        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE REPLAY IS OFFLINE-SAFE, because the lookup happens before the read that builds the plan.
    /// This is the case a request id exists for: the reply was lost, the agent asks again, and the
    /// platform is exactly as unreachable as it was when the reply went missing.
    /// </summary>
    [Fact]
    public async Task A_replay_answers_from_the_store_with_the_connector_unreachable_and_reads_nothing()
    {
        var (gw, conn, db) = await Ready();
        using var _1 = db;
        var ctx = new AgentContext("a");

        await gw.PlaceAsync(ctx, "cr-open", TestEnv.Buy("ES", 2m));
        var first = await SweepAsync(gw, ctx, Ops.CloseAll, "cr-4", "11110000");
        log.WriteLine($"first answer : {first}");

        conn.Faults.Disconnected = true;                  // nothing can be read from the platform
        var reads = conn.Positions;
        var replay = await SweepAsync(gw, ctx, Ops.CloseAll, "cr-4", "22220000");
        log.WriteLine($"replay while unreachable : {replay}");
        log.WriteLine($"position reads attempted during the replay : {conn.Positions - reads}");

        Assert.Equal(first, replay);
        Assert.Equal(reads, conn.Positions);
        conn.Faults.Disconnected = false;
        await gw.DisposeAsync();
    }
}

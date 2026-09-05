using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// A SECOND CALLER ON A REQUEST ID THAT IS STILL RUNNING IS NOT A CRASH TO RECOVER FROM (REVIEW
/// 2026-09-05b, Codex F18).
///
/// <c>BeginCompositeAsync</c> has three answers: a new id runs, a finished id returns its stored
/// answer, and a KNOWN id with no answer yet is read as "the first run died mid-flight" and
/// resumed. The third answer is right for a dead owner and wrong for a live one, and the store
/// cannot tell them apart — a row with a null result means both.
///
/// What the duplicate then did: it re-ran the stored plan while the owner was still inside its first
/// connector call, so every leg that already had a record read back the state that record was in AT
/// THAT INSTANT — DISPATCHING, the write-ahead row of an order in flight — and it called
/// <c>CompleteComposite</c> with that. <c>Complete</c> is first-write-wins on purpose (the reply a
/// caller lost is still the reply this id has), so the TRANSIENT answer became the permanent one and
/// the owner's real answer, arriving a moment later, was dropped. The agent that asked "what
/// happened to my cancel-all" is then told, for ever, that nothing was cancelled — about a sweep
/// that cancelled everything.
///
/// The fix is an owner lease held in memory for exactly as long as one caller is running the plan.
/// A duplicate waits for it and returns what the owner stored. It is deliberately NOT in the
/// database: a claim that outlived the process holding it would be a claim nothing could release,
/// and a row left over from a crash must still resume — which is the case this class's second test
/// keeps.
/// </summary>
public class CompositeOwnerTests(ITestOutputHelper log)
{
    /// <summary>
    /// One cancel-all, run the way <c>GatewayPipeServer.CancelAll</c> runs it: claim the id with the
    /// plan as a delegate, run a leg per target under the composite's own nonce, then write the
    /// answer once. The answer carries each leg's FINAL state, because a state is the thing a
    /// transient answer gets wrong.
    ///
    /// <c>FromStore</c> says WHICH of the two things happened, and it is not a nicety: "returned the
    /// owner's stored answer" and "re-ran the plan and got an equal answer" are different claims, and
    /// only the first is what a request id promises. They are easy to confuse because a re-run that
    /// happens after the owner has finished usually produces the same string.
    /// </summary>
    static async Task<(string Answer, bool FromStore)> CancelAllAsync(TradingGateway gw, AgentContext ctx,
        string rid, string nonce, CancellationToken ct = default)
    {
        var composite = await gw.BeginCompositeAsync(ctx, rid, Ops.CancelAll,
            async c => (await gw.OrdersAsync(false, c)).Select(o => o.ConnectorOrderId).ToList(),
            () => nonce, ct);
        using var owner = composite.Owner;
        if (composite.StoredResultJson is { } answered) return (answered, true);

        var states = new List<string>();
        for (var i = 0; i < composite.Targets.Count; i++)
        {
            var leg = await gw.CancelAsync(ctx, $"op-{composite.Nonce}-cancelall-{i}", composite.Targets[i], ct);
            states.Add($"{composite.Targets[i]}={leg.State}");
        }

        var result = Json.Write(new
        {
            cancelled = states.Count(s => s.EndsWith($"={ExecutionState.CANCELLED}", StringComparison.Ordinal)),
            legs = states
        });
        gw.CompleteComposite(rid, result);
        return (result, false);
    }

    static async Task<(TradingGateway Gw, RecordingConnector Conn, Database Db)> Ready()
    {
        var db = TestEnv.NewDb();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker(),
            new FaultProfile { Fill = FillBehaviour.LeaveWorking }));
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
    /// CODEX F18'S PROBE. The first sweep is blocked inside its first connector cancel; the same
    /// request id is issued concurrently; the duplicate must wait for the owner and return the
    /// owner's final stored answer.
    /// </summary>
    [Fact]
    public async Task A_second_call_on_a_running_composite_waits_for_the_owners_answer()
    {
        var (gw, conn, db) = await Ready();
        using var _1 = db;
        var ctx = new AgentContext("a");

        await gw.PlaceAsync(ctx, "co-es", TestEnv.Buy("ES"));
        await gw.PlaceAsync(ctx, "co-nq", TestEnv.Buy("NQ"));
        Assert.Equal(2, conn.Broker.Orders.Count);

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCancel = 0;
        conn.Seam = async kind =>
        {
            if (kind != RecordingConnector.HeldCall.Cancel) return;
            if (Interlocked.Exchange(ref firstCancel, 1) != 0) return;
            inside.TrySetResult();
            await release.Task;
        };

        var owner = CancelAllAsync(gw, ctx, "co-sweep", "aaaa0000");
        await inside.Task.WaitAsync(TimeSpan.FromSeconds(10));   // the owner is inside its first leg

        var duplicate = CancelAllAsync(gw, ctx, "co-sweep", "bbbb0000");
        var duplicateFinishedFirst = await Task.WhenAny(duplicate, Task.Delay(TimeSpan.FromSeconds(1))) == duplicate;

        release.SetResult();
        var (ownerAnswer, ownerFromStore) = await owner.WaitAsync(TimeSpan.FromSeconds(30));
        var (duplicateAnswer, duplicateFromStore) = await duplicate.WaitAsync(TimeSpan.FromSeconds(30));

        log.WriteLine($"owner answer         : {ownerAnswer}   (from the store: {ownerFromStore})");
        log.WriteLine($"duplicate answer     : {duplicateAnswer}   (from the store: {duplicateFromStore})");
        log.WriteLine($"answered before the owner was released : {duplicateFinishedFirst}");
        log.WriteLine($"stored               : {gw.Composites.Get("co-sweep")!.ResultJson}");
        log.WriteLine($"connector cancels    : {conn.Cancels}");

        // The duplicate did not answer out of a half-finished sweep.
        Assert.False(duplicateFinishedFirst);
        Assert.False(ownerFromStore);
        // AND IT RETURNED THE OWNER'S ANSWER RATHER THAN COMPUTING AN EQUAL ONE. A resumed plan run
        // after the owner has finished produces the same string here, which is exactly why the two
        // are told apart by WHERE the answer came from and not by comparing them.
        Assert.True(duplicateFromStore);
        Assert.Equal(ownerAnswer, duplicateAnswer);
        Assert.Equal(ownerAnswer, gw.Composites.Get("co-sweep")!.ResultJson);
        Assert.DoesNotContain(ExecutionState.DISPATCHING.ToString(), duplicateAnswer, StringComparison.Ordinal);
        Assert.Contains($"\"cancelled\":2", duplicateAnswer, StringComparison.Ordinal);
        Assert.Equal(2, conn.Cancels);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// AND A ROW WHOSE OWNER IS REALLY GONE STILL RESUMES. The lease is in memory, so a composite
    /// left unfinished by a crashed process has none — which is the difference between "somebody is
    /// still running this" and "nobody is", and the reason the lease may not live in the database.
    /// </summary>
    [Fact]
    public async Task A_composite_left_unfinished_with_no_live_owner_still_resumes()
    {
        var (gw, conn, db) = await Ready();
        using var _1 = db;
        var ctx = new AgentContext("a");

        await gw.PlaceAsync(ctx, "cor-es", TestEnv.Buy("ES"));
        var target = conn.Broker.Orders.Single().ConnectorOrderId;

        // The row a dead process leaves behind: claimed, planned, never answered, no owner in memory.
        gw.BeginComposite(ctx, "cor-sweep", Ops.CancelAll, [target], () => "cccc0000");
        Assert.Null(gw.Composites.Get("cor-sweep")!.ResultJson);

        var (answer, fromStore) = await CancelAllAsync(gw, ctx, "cor-sweep", "dddd0000")
            .WaitAsync(TimeSpan.FromSeconds(30));

        log.WriteLine($"resumed answer       : {answer}   (from the store: {fromStore})");
        log.WriteLine($"connector cancels    : {conn.Cancels}");
        log.WriteLine($"orders still working : {conn.Broker.Orders.Count(o => o.State == ExecutionState.WORKING)}");

        Assert.False(fromStore);                              // it RAN, it did not read an answer back
        Assert.Contains($"\"cancelled\":1", answer, StringComparison.Ordinal);
        Assert.Equal(1, conn.Cancels);
        await gw.DisposeAsync();
    }
}

using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

// =================================================================================================
// U-override-lease — the human override respects the dispatch lease, and a broker's definite answer
// still lands on a row a human moved.
//
// REVIEW 2026-09-05b finding 1, executed as P3. `U-stranded` gave the RECONCILER an in-memory lease
// over a row a handler is still inside the connector call for. The Dashboard's unconfirmed card is
// fed by `Unreconciled()`, which does not consult that lease, so the SAME live row was rendered with
// its two override buttons — and `ForceResolve` wrote a terminal state straight onto it, cleared the
// flag and the latch, and trading resumed. The dispatcher's FILLED then met `Settle`'s rescue arm
// (`UNKNOWN or RECONCILING`), which a ForceResolved row is not, and was filed `already_settled`.
//
// End state P3 quoted: the broker holds ES 1, the durable record says CANCELLED "resolved by user:
// no such order exists", nothing is flagged, trading has resumed. Every line of that is refuted
// below, on the same seam and the same movable clock `U-stranded`'s own tests use.
// =================================================================================================

public class OverrideLeaseTests(ITestOutputHelper Out)
{
    // ---------------------------------------------------------------------------------------------
    // Item 1 — P3 itself. The card asks the lease and the override refuses while it is live.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task The_override_is_refused_while_the_dispatcher_is_still_on_the_wire()
    {
        var (gw, c, db, clock) = await Stranded.Ready();
        using var dbh = db;
        var release = new TaskCompletionSource();
        c.HangPlaceBeforeTheBroker = release;

        var inFlight = gw.PlaceAsync(new AgentContext("a"), "p3-owned", TestEnv.Buy());
        await c.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(120));            // past the 70 s derived bound

        // The row is still on the card — it is unconfirmed work and it still pauses trading. What
        // the card may not do is offer to resolve it, and this is the question it asks.
        var card = gw.Unreconciled();
        Out.WriteLine($"card shows              : {string.Join(", ", card.Select(r => $"{r.RequestId}/{r.State}"))}");
        Out.WriteLine($"the card asks the lease : {gw.StillOnTheWire("p3-owned") ?? "(nothing)"}");
        Out.WriteLine($"the reconciler          : {Describe(await gw.ReconcileAsync())}");

        // The other button on the same card: "No order exists". RECORDED rather than asserted here
        // on purpose — with the guard removed this call SUCCEEDS, and the run has to reach the end
        // state below so the mutant's failure prints P3 rather than stopping one line into it.
        var refused = Record.Exception(
            () => gw.ForceResolve("p3-owned", ExecutionState.CANCELLED, "I checked in ATAS and no such order exists"))
            as GatewayDeniedException;
        Out.WriteLine($"the override            : {(refused is null ? "went through" : $"{refused.Code} — {refused.Message}")}");

        var duringTheHang = gw.GetRequest("p3-owned")!;
        Out.WriteLine($"record during the hang  : {duringTheHang.State}, needs_reconciliation={duringTheHang.NeedsReconciliation}");
        await gw.RefreshHealthAsync();
        Out.WriteLine($"trading                 : {(gw.TryAuthorizeExecution(new AgentContext("a"), out var why, out _) ? "resumed" : $"paused — {why}")}");

        release.SetResult();
        var placed = await inFlight;
        var final = gw.GetRequest("p3-owned")!;
        var events = Recovery.Engineering(db, "p3-owned").Select(e => e.Event).ToList();
        Out.WriteLine($"dispatch answered       : {placed.State}");
        Out.WriteLine($"orders at the broker    : {c.Inner.Broker.Orders.Count} " +
                      string.Join(" ", c.Inner.Broker.Orders.Select(o => $"{o.ConnectorOrderId} {o.State} {o.Quantity} {o.Symbol}")));
        Out.WriteLine($"position at the broker  : {string.Join(" ", (await c.GetPositionsAsync(c.Inner.Broker.AccountId)).Select(p => $"{p.Symbol} {p.Quantity}"))}");
        Out.WriteLine($"record now              : {final.State}, needs_reconciliation={final.NeedsReconciliation}, last_error={final.LastError ?? "(none)"}");
        Out.WriteLine($"engineering             : {string.Join(", ", events)}");

        // The refusal names the two numbers the reconciler's own refusal names, and tells the owner
        // what to do instead of leaving them holding a dead button.
        Assert.NotNull(refused);
        Assert.Equal(ErrorCode.INVALID_REQUEST, refused.Code);
        Assert.Contains("still on the wire for 120s of a possible 50s", refused.Message);
        Assert.Contains("wait", refused.Message, StringComparison.OrdinalIgnoreCase);

        // P3's end state, line by line, inverted.
        Assert.Equal(ExecutionState.DISPATCHING, duringTheHang.State);  // the human did not move it
        Assert.Equal(ExecutionState.FILLED, placed.State);              // the dispatcher got its answer
        Assert.Equal(ExecutionState.FILLED, final.State);               // and the record holds it
        Assert.Null(final.LastError);                                   // not "resolved by user: no such order exists"
        Assert.Single(c.Inner.Broker.Orders);
        Assert.Equal(1m, (await c.GetPositionsAsync(c.Inner.Broker.AccountId)).Single(p => p.Symbol == "ES").Quantity);
        Assert.DoesNotContain("already_settled", events);               // the broker's answer was not discarded
        await gw.DisposeAsync();
    }

    static string Describe(ReconcileResult r) =>
        $"resolved={r.Resolved} inconclusive={r.Inconclusive} {string.Join("; ", r.Details)}";
}

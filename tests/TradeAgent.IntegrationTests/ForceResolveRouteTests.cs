using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// The escape hatch behind the Dashboard's "unconfirmed orders" card, proven end to end at the
/// logic level: a request the machine cannot settle -> the human asserts what they saw -> the flag
/// clears -> trading is allowed again.
///
/// This exists because the card is the ONLY route into <see cref="TradingGateway.ForceResolve"/>.
/// Operator authority is deliberately absent from the agent-facing pipe and from the trade CLI, so
/// if this sequence does not actually unblock trading the button is decorative and the first
/// ambiguous order on ATAS pauses the product forever.
///
/// WHAT THESE TESTS PIN DOWN, and why each assertion is load-bearing:
///
///  1. Clearing the flag is NOT sufficient on its own. `TryAuthorizeExecution` checks two things in
///     sequence — the unreconciled count AND `HealthRegistry.ExecutionTrustable`. ForceResolve only
///     touches the first. `Health_stays_paused_until_it_is_refreshed` pins that, because it is the
///     reason the UI must refresh health after the press rather than trusting the five-second tick.
///  2. FILLED and CANCELLED are the only two outcomes the card offers a movable record, and
///     `Both_offered_outcomes_are_reachable_from_every_state...` is the reason: they are the only
///     targets <see cref="OrderStateMachine"/> permits from every state a flagged request can hold.
///     If someone narrows that table, this test fails before the button does.
/// </summary>
public class ForceResolveRouteTests
{
    static readonly AgentContext Agent = new("a");

    /// <summary>
    /// The ATAS shape, reproduced: the broker accepted the order, the acknowledgement was lost, and
    /// the backend cannot prove its own history — so no number of reconcile passes can settle it.
    /// </summary>
    static async Task<(TradingGateway Gw, Database Db)> Stuck(string requestId, bool reconcileFirst)
    {
        var (gw, _, db) = await TestEnv.Ready(
            options: new GatewayOptions { AbsenceGrace = TimeSpan.Zero },
            faults: new FaultProfile { DropAfterBrokerAccept = 1, HideOrderHistory = true });

        await gw.PlaceAsync(Agent, requestId, TestEnv.Buy());

        // Reconciled or not, the record is flagged. The two paths leave it in different states —
        // UNKNOWN straight off the failed dispatch, RECONCILING once the reconciler has given up —
        // and the card has to work from both, because which one the user is looking at depends only
        // on whether a background tick happened to run first.
        if (reconcileFirst)
        {
            var r = await gw.ReconcileAsync();
            Assert.Equal(0, r.Resolved);
            Assert.Equal(1, r.Inconclusive);
        }

        var stored = gw.GetRequest(requestId)!;
        Assert.True(stored.NeedsReconciliation);
        Assert.Equal(reconcileFirst ? ExecutionState.RECONCILING : ExecutionState.UNKNOWN, stored.State);
        return (gw, db);
    }

    static void AssertBlocked(TradingGateway gw)
    {
        Assert.NotEmpty(gw.Requests.NeedingReconciliation());
        Assert.False(gw.TryAuthorizeExecution(Agent, out var reason, out var code));
        Assert.Equal(ErrorCode.TRADING_PAUSED_UNRECONCILED, code);
        Assert.Contains("unconfirmed", reason);
    }

    // ---------------------------------------------------------------- the route itself

    [Theory]
    [InlineData(ExecutionState.FILLED, false)]
    [InlineData(ExecutionState.FILLED, true)]
    [InlineData(ExecutionState.CANCELLED, false)]
    [InlineData(ExecutionState.CANCELLED, true)]
    public async Task The_card_route_clears_the_flag_and_lets_trading_resume(ExecutionState outcome, bool reconcileFirst)
    {
        var id = $"route-{outcome}-{reconcileFirst}";
        var (gw, db) = await Stuck(id, reconcileFirst);
        using var dbh = db;
        AssertBlocked(gw);

        // Exactly what the card's confirmed press does, in order.
        gw.ForceResolve(id, outcome, "I checked in ATAS by hand");
        await gw.RefreshHealthAsync();

        Assert.Empty(gw.Requests.NeedingReconciliation());
        Assert.True(gw.TryAuthorizeExecution(Agent, out var reason, out _), reason);

        var settled = gw.GetRequest(id)!;
        Assert.Equal(outcome, settled.State);
        Assert.False(settled.NeedsReconciliation);
        Assert.NotNull(settled.LastReconciledAt);
        Assert.Contains("resolved by user", settled.LastError);
        Assert.Contains("I checked in ATAS by hand", settled.LastError);
    }

    /// <summary>
    /// THE REASON THE CARD REFRESHES HEALTH ITSELF.
    ///
    /// ForceResolve clears `needs_reconciliation` and nothing else. The ExecutionCapability health
    /// row was set PAUSED by the failed dispatch and by the reconciler, and only
    /// <see cref="TradingGateway.RefreshHealthAsync"/> recomputes it — so between the press and the
    /// next background tick, trading is still refused, now for a DIFFERENT reason. A user who
    /// pressed the button and watched "AI trading: paused" stay on screen would reasonably conclude
    /// the button does nothing.
    /// </summary>
    [Fact]
    public async Task Health_stays_paused_until_it_is_refreshed()
    {
        var (gw, db) = await Stuck("route-health", reconcileFirst: true);
        using var dbh = db;

        gw.ForceResolve("route-health", ExecutionState.FILLED, "checked in ATAS");

        // The flag is gone, so the unreconciled gate passes...
        Assert.Empty(gw.Requests.NeedingReconciliation());
        // ...and trading is STILL refused, by the health gate underneath it.
        Assert.False(gw.TryAuthorizeExecution(Agent, out _, out var code));
        Assert.Equal(ErrorCode.TRADING_PERMISSION_UNAVAILABLE, code);

        await gw.RefreshHealthAsync();
        Assert.True(gw.TryAuthorizeExecution(Agent, out var reason, out _), reason);
    }

    /// <summary>
    /// The card unblocks the AI's whole pipeline, not just its own row: a real order goes through
    /// afterwards. Without this the previous tests only prove a boolean flipped.
    /// </summary>
    [Fact]
    public async Task The_ai_can_place_an_order_again_after_the_user_confirms()
    {
        var (gw, db) = await Stuck("route-then-trade", reconcileFirst: true);
        using var dbh = db;

        await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(Agent, "route-blocked", TestEnv.Buy()));

        gw.ForceResolve("route-then-trade", ExecutionState.FILLED, "checked in ATAS: 1 ES filled");
        await gw.RefreshHealthAsync();

        var next = await gw.PlaceAsync(Agent, "route-after", TestEnv.Buy());
        Assert.Equal(ExecutionState.FILLED, next.State);
    }

    // ---------------------------------------------------------------- why these two outcomes

    /// <summary>
    /// WHY THE CARD OFFERS FILLED AND CANCELLED AND NOTHING ELSE.
    ///
    /// ForceResolve reaches its target either directly or by forcing the record through RECONCILING
    /// first. FILLED and CANCELLED are the only two outcomes that survive that for EVERY state a
    /// flagged request can hold. WORKING, for instance, is unreachable from WORKING,
    /// PARTIALLY_FILLED and CANCEL_PENDING — offering it would hand the user a button that throws
    /// on the states where it is most likely to be the true answer.
    /// </summary>
    [Fact]
    public void Both_offered_outcomes_are_reachable_from_every_state_a_flagged_request_can_hold()
    {
        // Every state a request can be in while flagged: UNKNOWN and RECONCILING come from the
        // reconciler, the live ones from MarkNeedsReconciliation when the event stream got there
        // first. Terminal states are excluded deliberately — see the test below.
        ExecutionState[] flaggable =
        [
            ExecutionState.DISPATCHING, ExecutionState.ACKNOWLEDGED, ExecutionState.WORKING,
            ExecutionState.PARTIALLY_FILLED, ExecutionState.CANCEL_PENDING,
            ExecutionState.UNKNOWN, ExecutionState.RECONCILING
        ];

        static bool ForceResolveCanReach(ExecutionState from, ExecutionState to) =>
            OrderStateMachine.CanTransition(from, to)
            || (OrderStateMachine.CanTransition(from, ExecutionState.RECONCILING)
                && OrderStateMachine.CanTransition(ExecutionState.RECONCILING, to));

        foreach (var from in flaggable)
        {
            Assert.True(ForceResolveCanReach(from, ExecutionState.FILLED), $"FILLED unreachable from {from}");
            Assert.True(ForceResolveCanReach(from, ExecutionState.CANCELLED), $"CANCELLED unreachable from {from}");
        }

        // And the outcome the card deliberately does NOT offer, so the reason is recorded rather
        // than remembered. If the table ever gains these edges, the card may widen.
        Assert.False(ForceResolveCanReach(ExecutionState.WORKING, ExecutionState.WORKING));
        Assert.False(ForceResolveCanReach(ExecutionState.PARTIALLY_FILLED, ExecutionState.WORKING));
        Assert.False(ForceResolveCanReach(ExecutionState.CANCEL_PENDING, ExecutionState.WORKING));
    }

    /// <summary>
    /// BITE PROOF. If ForceResolve is handed an outcome the state machine cannot reach, it throws
    /// and the request stays flagged and trading stays paused — nothing is silently half-done. This
    /// is the mutant the tests above are guarding against: a card wired to the wrong target state
    /// would leave the user pressing a button that changes nothing.
    /// </summary>
    [Fact]
    public async Task An_unreachable_outcome_throws_and_leaves_trading_paused()
    {
        var (gw, db) = await Stuck("route-mutant", reconcileFirst: true);
        using var dbh = db;

        Assert.Throws<TradeAgentException>(() =>
            gw.ForceResolve("route-mutant", ExecutionState.DISPATCHING, "a state a human cannot check"));

        AssertBlocked(gw);
    }

    /// <summary>
    /// THE SHAPE THE CARD SHOWS ONE BUTTON FOR.
    ///
    /// `ExecutionRequestStore.MarkNeedsReconciliation` flags a request WITHOUT changing its state,
    /// and TradingGateway.SettleUnknown reaches it when a dispatch failed indefinitely but the event
    /// stream had already written an outcome. That outcome can be terminal, so a record can sit in
    /// FILLED and flagged — pausing trading — with no outgoing edge in OrderStateMachine at all.
    /// ReconcileAsync can never move it, so this is the one shape only a human can end.
    ///
    /// ForceResolve handles it by treating "the outcome I am asserting is the one already recorded"
    /// as a flag-clear rather than a transition, and by REFUSING a different outcome: a definite
    /// broker answer that the platform contradicts is a conflict to investigate, not to overwrite.
    /// That is why the card offers exactly one button here — "our record is right" — instead of the
    /// two it offers everywhere else.
    /// </summary>
    [Fact]
    public async Task A_flagged_request_that_is_already_terminal_is_ended_by_confirming_what_it_says()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var dbh = db;

        await gw.PlaceAsync(Agent, "terminal-1", TestEnv.Buy());
        Assert.Equal(ExecutionState.FILLED, gw.GetRequest("terminal-1")!.State);

        // What SettleUnknown does when the stream beat it to a terminal outcome.
        gw.Requests.MarkNeedsReconciliation("terminal-1", "connection lost while sending");
        AssertBlocked(gw);

        // The reconciler cannot move it: FILLED has no edge to UNKNOWN.
        var r = await gw.ReconcileAsync();
        Assert.Equal(0, r.Resolved);
        Assert.Equal(1, r.Inconclusive);

        // Asserting a DIFFERENT outcome over a definite one is refused, and the card never offers it.
        Assert.Throws<GatewayDeniedException>(() =>
            gw.ForceResolve("terminal-1", ExecutionState.CANCELLED, "checked in ATAS"));
        AssertBlocked(gw);

        // The one button the card does show, and what it does.
        gw.ForceResolve("terminal-1", ExecutionState.FILLED, "ATAS order list shows this filled");
        await gw.RefreshHealthAsync();

        var settled = gw.GetRequest("terminal-1")!;
        Assert.Equal(ExecutionState.FILLED, settled.State);
        Assert.False(settled.NeedsReconciliation);
        Assert.Empty(gw.Requests.NeedingReconciliation());
        Assert.True(gw.TryAuthorizeExecution(Agent, out var reason, out _), reason);
    }
}

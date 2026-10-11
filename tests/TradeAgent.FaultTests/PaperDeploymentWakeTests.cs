using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// WHOSE A FILL OR A FINISHED ORDER IS (<c>U-reconcile-wakes</c>, item 1). A paper deployment is the app's own
/// run of a frozen Research version, and its observation is its forward-run note; every other order and fill
/// is the chair's book. Only the role a wake is raised under is decided: the order, the fill row and
/// <c>RecordFill</c>'s answer are what they were.
/// </summary>
public partial class PaperDeploymentTests
{
    /// <summary>
    /// (c) A DEPLOYMENT'S FILL WAKES NO DIRECTOR, AND AN OPERATIONS ORDER WAKES OPERATIONS.
    ///
    /// <para>RED at base 604a3133's semantics (every fill and finished order raised with no role, which the
    /// queue reads as the chair's): the deployment's entry fill bought the Operations Director a turn.</para>
    /// </summary>
    [Fact]
    public async Task A_deployments_fill_wakes_no_director_and_an_operations_order_wakes_operations()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        var wakes = new List<(string Id, string Kind, string Role)>();
        gw.RaiseMissionWake = (id, kind, _, role) => { lock (wakes) wakes.Add((id, kind, role)); };

        // THE CHAIR'S OWN ORDER FIRST — the account is not yet under the paper envelope, which accepts only
        // allocated versions' orders. A market order the platform leaves resting, then fills on its own side;
        // the gateway's health pass reads that answer back past its grace — a later platform answer, the one
        // path that raises an order's final-state wake — and the fill pull records the execution.
        conn.Faults.Fill = FillBehaviour.LeaveWorking;
        var chair = new AgentContext("chair");
        var resting = await gw.PlaceAsync(chair, "ops-resting", TestEnv.Buy());
        Assert.Equal(ExecutionState.WORKING, gw.GetRequest(resting.RequestId)!.State);
        Assert.NotNull(conn.Broker.FillWorking(gw.GetRequest(resting.RequestId)!.ConnectorOrderId!));
        clock.At = clock.At.AddMinutes(5);
        await gw.RefreshHealthAsync();
        Assert.Equal(ExecutionState.FILLED, gw.GetRequest(resting.RequestId)!.State);
        conn.Faults.Fill = FillBehaviour.FillImmediately;

        log.WriteLine($"wakes after the chair's order: {string.Join(", ", wakes)}");
        var fill = Assert.Single(wakes, w => w.Kind == MissionEventKind.Fill);
        Assert.Equal(CouncilRoles.Operations, fill.Role);
        var order = Assert.Single(wakes, w => w.Kind == MissionEventKind.Order);
        Assert.Equal(CouncilRoles.Operations, order.Role);
        Assert.Contains(resting.RequestId, order.Id, StringComparison.Ordinal);
        lock (wakes) wakes.Clear();

        // THE DEPLOYMENT'S ENTRY, FILLED: written down, dispatched under the run's own identity, filled.
        await Allocated(gw, db);
        Assert.Equal(1, gw.StartPaperDeploymentsDue(At));
        var deployment = gw.Deployments.Open().Single();
        await SeedAPosition(gw, conn, deployment);
        var executions = conn.Broker.Executions.Count;
        Assert.True(executions >= 2, "the deployment's entry reached no execution");
        Assert.True(gw.Fills.Count() >= 2, "the deployment's fill was not recorded");

        log.WriteLine($"wakes after the deployment's fill: {string.Join(", ", wakes)}");
        Assert.DoesNotContain(wakes, w => w.Kind is MissionEventKind.Fill or MissionEventKind.Order);

        await gw.DisposeAsync();
    }
}

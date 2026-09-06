using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-ledger item 1, red first. The fill ledger has TWO sources by design — the connector's event
/// stream and a pull of its execution list at every (re)connect and on the interval — and the
/// question the table has to answer is what happens when both of them carry the same fill.
///
/// Two rows would double every number the owner is ever shown, which is worse than no ledger: a
/// wrong figure is acted on and a missing one is asked about.
/// </summary>
public class FillLedgerTests
{
    static Task<(TradingGateway Gw, FakeConnector Conn, Database Db)> Ready(
        Action<TradeAgentSettings>? settings = null, FaultProfile? faults = null) =>
        TestEnv.Ready(settings, faults: faults);

    /// <summary>
    /// THE ITEM'S OWN RED. The order fills, the connector raises it on the event stream, and the
    /// pull that follows serves the very same execution back. One execution, one row.
    /// </summary>
    [Fact]
    public async Task An_execution_reported_by_both_the_event_and_the_pull_is_one_row()
    {
        var (gw, conn, db) = await Ready();
        await using var _ = gw;
        using var __ = db;

        await gw.PlaceAsync(new AgentContext("agent-1"), "fill-both", TestEnv.Buy());
        Assert.Single(gw.Fills.Since());                          // the event wrote it

        var pull = await gw.PullFillsAsync();                     // the same execution, again

        var rows = gw.Fills.Since();
        Assert.Single(rows);
        Assert.True(pull.Ok);
        Assert.Equal(1, pull.Seen);
        Assert.Equal(0, pull.Added);
        Assert.Equal(conn.Broker.Executions.Single().ExecutionId, rows[0].ExecutionId);
        // The first source owns the row; the second is a no-op, not a correction.
        Assert.Equal(FillSource.Event, rows[0].Source);
    }

    /// <summary>
    /// The other half of the same rule: a fill the event stream never carried is still in the ledger,
    /// because the pull is what makes a dropped connection survivable. <c>FillWorking</c> is the
    /// broker filling a resting order with nobody listening — no <c>ExecutionReceived</c> is raised.
    /// </summary>
    [Fact]
    public async Task A_fill_the_event_stream_never_carried_is_recorded_by_the_pull()
    {
        var (gw, conn, db) = await Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        await using var _ = gw;
        using var __ = db;

        var req = await gw.PlaceAsync(new AgentContext("agent-1"), "fill-pull-only",
            new PlaceIntent("ES", OrderSide.Buy, OrderType.Limit, 2m, 100m, null, TimeInForce.Day, null));
        Assert.Empty(gw.Fills.Since());

        conn.Broker.FillWorking(req.ConnectorOrderId!);
        Assert.Empty(gw.Fills.Since());                           // still nothing: nobody was told

        var pull = await gw.PullFillsAsync();

        Assert.Equal(1, pull.Added);
        var row = Assert.Single(gw.Fills.Since());
        Assert.Equal(FillSource.Pull, row.Source);
        Assert.Equal(2m, row.Quantity);
    }

    /// <summary>
    /// Attribution is resolved when the row is written or never: the client order id is
    /// <c>TA-{requestId}</c>, and the request row is what carries the agent session.
    /// </summary>
    [Fact]
    public async Task A_fill_carries_the_request_and_the_agent_session_that_asked_for_it()
    {
        var (gw, _, db) = await Ready();
        await using var __ = gw;
        using var ___ = db;

        await gw.PlaceAsync(new AgentContext("agent-seven"), "fill-attrib", TestEnv.Buy());

        var row = Assert.Single(gw.Fills.Since());
        Assert.Equal("fill-attrib", row.RequestId);
        Assert.Equal("agent-seven", row.AgentSession);
        Assert.Equal(TradingGateway.ClientOrderIdFor("fill-attrib"), row.ClientOrderId);
        Assert.Equal("Buy", row.Side);
    }

    /// <summary>
    /// A fee the platform did not report is NULL, and a fee it reported as zero is 0. The ledger has
    /// to hold both, because <c>pnl</c>'s whole rule is that they are different answers.
    /// </summary>
    [Fact]
    public async Task An_unreported_fee_is_null_and_a_reported_zero_is_zero()
    {
        var (silent, _, dbA) = await Ready();
        await using (silent)
        using (dbA)
        {
            await silent.PlaceAsync(new AgentContext("a"), "fee-silent", TestEnv.Buy());
            Assert.Null(Assert.Single(silent.Fills.Since()).Fee);
        }

        var (says, conn, dbB) = await Ready();
        await using (says)
        using (dbB)
        {
            conn.Broker.FeePerContract = 0m;
            await says.PlaceAsync(new AgentContext("a"), "fee-zero", TestEnv.Buy());
            Assert.Equal(0m, Assert.Single(says.Fills.Since()).Fee);

            conn.Broker.FeePerContract = 2.10m;
            await says.PlaceAsync(new AgentContext("a"), "fee-known", TestEnv.Buy());
            Assert.Equal(2.10m, says.Fills.Since().Last().Fee);
        }
    }

    /// <summary>
    /// A pull that failed is recorded as a failure. It is the difference between "nothing traded"
    /// and "nothing was asked", and <c>pnl</c> reports the second in <c>incomplete</c>.
    /// </summary>
    [Fact]
    public async Task A_pull_that_failed_is_written_down_as_a_failure()
    {
        var faults = new FaultProfile();
        var (gw, conn, db) = await Ready(faults: faults);
        await using var _ = gw;
        using var __ = db;

        faults.Disconnected = true;
        var pull = await gw.PullFillsAsync();

        Assert.False(pull.Ok);
        Assert.NotNull(pull.Error);
        var stored = Json.Read<TradingGateway.FillPullRecord>(db.GetKv(TradingGateway.FillPullKey)!)!;
        Assert.False(stored.Ok);
        Assert.Equal(pull.Error, stored.Error);
    }

    /// <summary>
    /// The ledger says from when it knows anything, once, on the first pull that worked. The
    /// connector's own claim about history is recorded next to it rather than believed: on ATAS the
    /// execution list is the strategy's in-session trades whatever the platform says about orders.
    /// </summary>
    [Fact]
    public async Task Coverage_is_stamped_by_the_first_successful_pull_and_never_moved()
    {
        var (gw, _, db) = await Ready();
        await using var _2 = gw;
        using var _3 = db;

        var first = Json.Read<TradingGateway.FillCoverage>(db.GetKv(TradingGateway.FillCoverageKey)!)!;
        await gw.PlaceAsync(new AgentContext("a"), "cov-1", TestEnv.Buy());
        await gw.PullFillsAsync();

        var again = Json.Read<TradingGateway.FillCoverage>(db.GetKv(TradingGateway.FillCoverageKey)!)!;
        Assert.Equal(first.WatchingFrom, again.WatchingFrom);
        Assert.True(first.HistoryClaimed);   // the simulator can serve its own history and says so
    }
}

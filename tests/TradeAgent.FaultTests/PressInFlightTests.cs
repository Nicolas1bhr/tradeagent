using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

// =================================================================================================
// U-press-inflight — a close is never sized from a position an order already on the wire is moving
//
// REVIEW 2026-09-05b finding 2 (probes P6/P6b) and Codex F3 are the same class, reached from the two
// ends of one race. The emergency press's atomicity covered PRESS ROWS and its drift re-read
// compared POSITIONS; an agent's `close` is an ordinary `execution_request` inside the connector
// call, so the re-read still showed ES 2, the press captured 2 and sent a market sell 2 while the
// agent's sell 2 was on the wire. Long 2 became SHORT 2 after the owner pressed the control whose
// whole purpose is to flatten.
//
// The fix is one statement in each direction over the set the gateway already keeps:
//   - the press's write-ahead insert refuses a leg while a DISPATCHING request that can move the
//     position exists on that instrument — an order a handler is inside the connector call for
//     (item 1);
//   - the agent's own close re-reads the position at dispatch, inside the gate, and is refused when
//     it moved (item 2).
// =================================================================================================

static class InFlight
{
    public static async Task<string> Pos(ITradingConnector c, string accountId) =>
        string.Join(" ", (await c.GetPositionsAsync(accountId)).Select(p => $"{p.Symbol} {p.Quantity}"));
}

// =================================================================================================
// Item 1 — a press leg is refused while open work exists against its instrument
// =================================================================================================

public class PressWaitsOnOpenWorkTests(ITestOutputHelper Out)
{
    /// <summary>
    /// P6, LIFTED. Same setup, opposite assertion: the press must send NOTHING for an instrument the
    /// gateway itself has an order on the wire for, the agent's close must be the only close that
    /// reaches the broker, and the position must end FLAT rather than reversed.
    /// </summary>
    [Fact]
    public async Task An_agent_close_in_flight_stops_the_press_sending_a_second_close_for_that_instrument()
    {
        var (gw, c, db, _) = await Stranded.Ready();
        using var dbh = db;

        await gw.PlaceAsync(new AgentContext("ai"), "p6-open", TestEnv.Buy(qty: 2m));
        Out.WriteLine($"position before         : {await InFlight.Pos(c, c.Inner.Broker.AccountId)}");

        // The agent's own close, held inside the connector call.
        var release = new TaskCompletionSource();
        c.HangPlaceBeforeTheBroker = release;
        var agentClose = gw.CloseAsync(new AgentContext("ai"), "p6-agent", "ES");
        await c.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Out.WriteLine($"agent close             : on the wire, record {gw.GetRequest("p6-agent")?.State.ToString() ?? "none"}");

        var press = await gw.OperatorCloseAllAsync();
        Out.WriteLine($"press                   : {press.Summary}");
        Out.WriteLine($"orders after the press  : {c.Inner.Broker.Orders.Count}");

        release.SetResult();
        var leg = await agentClose;
        Out.WriteLine($"agent close answered    : {leg?.State.ToString() ?? "null"}");
        Out.WriteLine($"orders at the broker    : {c.Inner.Broker.Orders.Count} " +
                      string.Join(" ", c.Inner.Broker.Orders.Select(o => $"{o.ConnectorOrderId} {o.Side} {o.Quantity} {o.State}")));
        Out.WriteLine($"position at the end     : {await InFlight.Pos(c, c.Inner.Broker.AccountId)}");

        // The open and the agent's close. Nothing from the press.
        Assert.Equal(2, c.Inner.Broker.Orders.Count);
        Assert.Equal(ExecutionState.FILLED, leg!.State);
        Assert.DoesNotContain(await c.GetPositionsAsync(c.Inner.Broker.AccountId), p => p.Symbol == "ES" && p.Quantity != 0);
        Assert.Contains("1 leg waited on an order still on the wire", press.Summary);
        Assert.Contains("p6-agent", press.Summary);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// P6b, LIFTED UNCHANGED. The other direction still holds: with the press's flagged row already
    /// on disk, `U-gates`' re-check at the dispatch gate refuses the agent's close.
    /// </summary>
    [Fact]
    public async Task A_press_row_on_disk_still_refuses_the_agents_close()
    {
        var (gw, c, db, _) = await Stranded.Ready();
        using var dbh = db;

        await gw.PlaceAsync(new AgentContext("ai"), "p6b-open", TestEnv.Buy(qty: 2m));
        var press = await gw.OperatorCloseAllAsync();
        Out.WriteLine($"press                   : {press.Summary}");
        Out.WriteLine($"orders after the press  : {c.Inner.Broker.Orders.Count}");

        var refused = await Assert.ThrowsAsync<GatewayDeniedException>(
            () => gw.CloseAsync(new AgentContext("ai"), "p6b-agent", "ES"));
        Out.WriteLine($"agent close             : {refused.Code} — {refused.Message}");
        Out.WriteLine($"orders at the end       : {c.Inner.Broker.Orders.Count}");

        Assert.Equal(ErrorCode.TRADING_PAUSED_UNRECONCILED, refused.Code);
        Assert.Equal(2, c.Inner.Broker.Orders.Count);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE GUARD IS PER INSTRUMENT, NOT PER PRESS. A press over two positions must still flatten the
    /// one nothing is in flight on — an emergency control that gives up on every symbol because one
    /// of them is busy would be a worse failure than the one this unit fixes.
    /// </summary>
    [Fact]
    public async Task The_other_instruments_of_the_same_press_are_still_closed()
    {
        var (gw, c, db, _) = await Stranded.Ready();
        using var dbh = db;

        await gw.PlaceAsync(new AgentContext("ai"), "two-es", TestEnv.Buy(qty: 2m));
        await gw.PlaceAsync(new AgentContext("ai"), "two-nq", TestEnv.Buy("NQ", 2m));

        var release = new TaskCompletionSource();
        c.HangPlaceBeforeTheBroker = release;
        var agentClose = gw.CloseAsync(new AgentContext("ai"), "two-agent", "ES");
        await c.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var press = await gw.OperatorCloseAllAsync();
        Out.WriteLine($"press                   : {press.Summary}");
        release.SetResult();
        await agentClose;
        Out.WriteLine($"position at the end     : {await InFlight.Pos(c, c.Inner.Broker.AccountId)}");

        Assert.Single(press.Targets);
        Assert.Equal("NQ", press.Targets[0].Target);
        Assert.Contains("1 leg waited on an order still on the wire", press.Summary);
        await gw.DisposeAsync();
    }
}

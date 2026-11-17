using TradeAgent.AgentRuntime;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-DOOR-BOUNDS: A DOOR CALL'S TIME LIMIT IS STATED, AND ITS DURATION MEASURED (the agent boundary). The vendor's harness waits for one MCP tool
/// call for <c>mcp_servers.&lt;name&gt;.tool_timeout_sec</c>, and for 300 s when it is not set (codex <c>codex-mcp/src/rmcp_client.rs:106</c> at
/// rust-v0.160.1); the door's longest call is a verdict over the longest holdout the app downloads. The bound is the app's own, stated on
/// both door classes, and compared whole with the rest of a verified class's deny set.
/// </summary>
public class DoorBoundsTests
{
    static RuntimeManifest Codex => RuntimeCatalog.BuiltIn(HostPlatform.Current).First(m => m.Id == "codex");

    static string Pair => $"mcp_servers.{BrokerSeat.DoorServer}.tool_timeout_sec={BrokerSeat.DoorToolTimeout.TotalSeconds:0.0}";

    /// <summary>
    /// BOTH DOOR CLASSES CARRY THE DOOR'S TIME LIMIT, as one adjacent <c>-c</c> pair, exactly once: the broker's and the cell's. RED at base:
    /// neither carried one, so the vendor's default applied, a number nobody in the app had stated.
    /// </summary>
    [Fact]
    public void Both_door_classes_carry_the_doors_time_limit()
    {
        var seat = Codex.Seat!;
        foreach (var seatClass in new[] { SeatPlan.BrokerClass, SeatPlan.CellClass })
        {
            var args = seat.ArgsFor(seatClass);
            var at = Enumerable.Range(0, args.Length).Where(i => args[i] == Pair).ToList();
            Assert.True(at.Count == 1, $"the {seatClass} class carries '{Pair}' {at.Count} times: {string.Join(' ', args)}");
            Assert.Equal("-c", args[at[0] - 1]);
            Assert.Single(args, a => a.Contains(".tool_timeout_sec=", StringComparison.Ordinal));
        }
        Assert.DoesNotContain(seat.ArgsFor(SeatPlan.CliClass), a => a.Contains("tool_timeout_sec", StringComparison.Ordinal));
    }

    /// <summary>
    /// THE LIMIT OUTLASTS THE LONGEST CALL THE DOOR ADMITS, AND ENDS WELL INSIDE THE TURN: above the measured verdict over the longest holdout
    /// with the stated margin, and at most a quarter of the turn's own wall clock, so a call that hits it leaves the turn time to read the
    /// answer when it is asked again.
    /// </summary>
    [Fact]
    public void The_limit_sits_between_the_longest_call_and_the_turn()
    {
        Assert.True(BrokerSeat.DoorToolTimeout >= BrokerSeat.LongestDoorCall * BrokerSeat.DoorTimeoutMargin,
            $"{BrokerSeat.DoorToolTimeout} is under {BrokerSeat.DoorTimeoutMargin}x the longest measured call ({BrokerSeat.LongestDoorCall})");
        Assert.True(BrokerSeat.DoorToolTimeout * 4 <= TradeAgent.Core.SeatSpec.VendorTurnLimit,
            $"{BrokerSeat.DoorToolTimeout} is more than a quarter of a turn's {TradeAgent.Core.SeatSpec.VendorTurnLimit}");
    }

    /// <summary>
    /// A <c>runtimes.json</c> THAT CHANGES THE LIMIT IS REFUSED for both verified classes, naming the deny set: the pair is one of the class's
    /// security fields, compared whole (<see cref="BrokerSeat.OverrideRefusal(RuntimeManifest, string)"/>), never read from a file.
    /// </summary>
    [Fact]
    public void A_runtimes_json_that_changes_the_limit_is_refused()
    {
        foreach (var seatClass in new[] { SeatPlan.BrokerClass, SeatPlan.CellClass })
        {
            var edited = Codex;
            var args = edited.Seat!.Args[seatClass];
            edited.Seat.Args[seatClass] = [.. args.Select(a => a == Pair ? $"mcp_servers.{BrokerSeat.DoorServer}.tool_timeout_sec=3600.0" : a)];
            var refusal = BrokerSeat.OverrideRefusal(edited, seatClass);
            Assert.True(refusal is not null, $"an override that moved the door's limit was accepted for the {seatClass} class");
            Assert.Contains("runtimes.json gives its deny set as ", refusal);
        }
    }
}

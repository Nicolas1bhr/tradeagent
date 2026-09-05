using System.Text.Json;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Gateway;
using TradeAgent.Security;
using TradeAgent.TradeCli;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// `close-all` ANSWERS BY THE PER-LEG WORD, the way `cancel-all` has since U2c1c.
///
/// `cancelled` and `not_cancelled` read the word "so a terminal row that was never sent is not
/// counted as a cancellation that landed" (CONTRACTS.md). `closed` and `not_closed` were left
/// reading <c>ExecutionRequest.State</c>, and `not_closed` entries carried no `outcome` field at
/// all — so a leg the connector PROVED it never sent came back as <c>state: CANCELLED</c> and
/// nothing else, which reads as "the platform cancelled the closing order" rather than "nothing was
/// ever sent and that position is still open" (REVIEW 2026-09-05, finding 9).
///
/// The two halves are not symmetrical and the count says so. `confirmed` means "this leg's own
/// intent is done", and for a CANCEL leg that is a record of CANCELLED or FILLED; for a CLOSE leg
/// the intent is a filled offsetting order, and a closing order that was itself cancelled has
/// flattened nothing. So `closed` requires the word AND a FILLED record: the word alone would let a
/// cancelled close count as a position closed, which is the same over-claim from the other side.
/// </summary>
public class CloseAllAnswersByTheWordTests(ITestOutputHelper log)
{
    static string NewPipe() => "ta-closeall-" + Guid.NewGuid().ToString("n")[..12];

    static async Task<JsonElement> CloseAllOverThePipe(TradingGateway gw, string rid)
    {
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);
        var reply = await client.SendAsync(new IpcRequest { Op = Ops.CloseAll, Session = "agent-1", RequestId = rid })
            .WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(reply.Ok, reply.Error?.Message);
        return JsonSerializer.SerializeToElement(reply.Data);
    }

    /// <summary>
    /// The leg the connector proved it never sent. Its record is CANCELLED — the gateway settles a
    /// proven-unsent mutation that way and does not flag it — so the state alone says a closing
    /// order was cancelled at the platform. Nothing was ever sent, and that position is still open.
    /// </summary>
    [Fact]
    public async Task A_close_leg_that_was_never_sent_is_reported_by_its_word_and_not_by_its_record()
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var dbh = db;
        await gw.PlaceAsync(new AgentContext("a"), "w-open", TestEnv.Buy());
        Assert.Single(conn.Broker.Positions);

        conn.Faults.RefuseBeforeSend = 1;      // the connector PROVES the next mutation never left
        var root = await CloseAllOverThePipe(gw, "wsweep1");
        log.WriteLine(root.ToString());

        var leg = root.GetProperty("outcomes").EnumerateArray().Single();
        var notClosed = root.GetProperty("not_closed").EnumerateArray().Single();

        Assert.Equal("not-sent", leg.GetProperty("outcome").GetString());
        Assert.Equal("NothingWritten", leg.GetProperty("transport").GetString());

        // The correction: the entry carries the word, exactly as `not_cancelled` does.
        Assert.Equal("not-sent", notClosed.GetProperty("outcome").GetString());
        Assert.Equal("CANCELLED", notClosed.GetProperty("state").GetString());
        Assert.Equal(0, root.GetProperty("closed").GetInt32());
        Assert.Equal(0, root.GetProperty("attempted").GetInt32());
        Assert.Equal(1, root.GetProperty("not_sent").GetInt32());
        Assert.DoesNotContain(conn.Broker.Orders, o => o.ClientOrderId!.Contains("closeall"));
    }

    /// <summary>
    /// A closing order the platform accepted and left WORKING. It reached the wire, so it is not
    /// `not-sent`; it has flattened nothing, so it is not `closed` either. The word says which.
    /// </summary>
    [Fact]
    public async Task A_close_order_left_working_is_not_counted_as_a_position_closed()
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var dbh = db;
        await gw.PlaceAsync(new AgentContext("a"), "k-open", TestEnv.Buy());

        conn.Faults.Fill = FillBehaviour.LeaveWorking;   // the close rests instead of filling
        var root = await CloseAllOverThePipe(gw, "ksweep1");
        log.WriteLine(root.ToString());

        var notClosed = root.GetProperty("not_closed").EnumerateArray().Single();
        Assert.Equal("sent-still-working", notClosed.GetProperty("outcome").GetString());
        Assert.Equal("WORKING", notClosed.GetProperty("state").GetString());
        Assert.Equal(0, root.GetProperty("closed").GetInt32());
        Assert.Equal(1, root.GetProperty("attempted").GetInt32());
        Assert.NotEmpty(conn.Broker.Positions);          // still open, which is what `closed: 0` means
    }

    /// <summary>The positive control: a close that really lands still counts, and carries `confirmed`.</summary>
    [Fact]
    public async Task A_close_that_lands_is_counted_and_carries_the_confirmed_word()
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var dbh = db;
        await gw.PlaceAsync(new AgentContext("a"), "y-open", TestEnv.Buy());

        var root = await CloseAllOverThePipe(gw, "ysweep1");
        log.WriteLine(root.ToString());

        Assert.Equal("confirmed", root.GetProperty("outcomes").EnumerateArray().Single()
            .GetProperty("outcome").GetString());
        Assert.Equal(1, root.GetProperty("closed").GetInt32());
        Assert.Equal(1, root.GetProperty("attempted").GetInt32());
        Assert.Empty(root.GetProperty("not_closed").EnumerateArray());
        Assert.All(conn.Broker.Positions, p => Assert.Equal(0m, p.Quantity));
    }

    /// <summary>
    /// The two sweeps describe themselves to the agent in the same terms.
    ///
    /// There is no `AGENTS.md` paragraph for either of them — the workspace file the builder writes
    /// says nothing about the sweeps at all — and `trade schema --json` is what that file tells the
    /// agent to read instead of trusting it. So the parity is asserted where the words actually are.
    /// </summary>
    [Fact]
    public void The_schema_describes_close_all_and_cancel_all_in_the_same_terms()
    {
        var ops = GatewaySchema.Ops().ToDictionary(o => o.Op, o => o.Description);
        log.WriteLine($"cancel-all : {ops[Ops.CancelAll]}");
        log.WriteLine($"close-all  : {ops[Ops.CloseAll]}");

        foreach (var description in new[] { ops[Ops.CancelAll], ops[Ops.CloseAll] })
        {
            Assert.Contains("one entry per leg in `outcomes`", description);
            Assert.Contains("counts only what landed", description);
            Assert.Contains("not what was attempted", description);
            Assert.Contains("carries the same word", description);
        }
    }
}

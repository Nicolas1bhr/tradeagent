using System.Text.Json;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Gateway;
using TradeAgent.Security;
using TradeAgent.TradeCli;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// What `cancel-all` and `close-all` name the requests they derive, and what they then claim to
/// have done.
///
/// Both derived their per-order ids as <c>{rid}-{i}</c>, which is a shape an agent can also type.
/// An agent that placed an order with <c>--request-id X-0</c> and later swept with
/// <c>--request-id X</c> handed the first cancellation the id <c>X-0</c>, already in the
/// idempotency store as a PLACE — so the store replayed that record instead of cancelling anything,
/// and the sweep counted it anyway. `cancelled=1`, order still WORKING.
///
/// The count was the second half of it: <c>cancelled = results.Count</c> counted ATTEMPTS. On the
/// one command a person reaches for when they want everything to stop, that is the worst possible
/// thing to be wrong about.
/// </summary>
public class SweepRequestIdTests
{
    static string NewPipe() => "ta-sweep-" + Guid.NewGuid().ToString("n")[..12];

    static IpcRequest Buy(string requestId, string symbol) => new()
    {
        Op = Ops.Buy,
        RequestId = requestId,
        Args = new()
        {
            ["symbol"] = JsonSerializer.SerializeToElement(symbol),
            ["quantity"] = JsonSerializer.SerializeToElement("1"),
            // A limit far from the market, so it rests as WORKING and is there to be cancelled.
            ["limit"] = JsonSerializer.SerializeToElement("1")
        }
    };

    /// <summary>
    /// The collision itself: an order placed under the id the sweep would derive, then the sweep.
    /// Nothing may be reported cancelled that is not cancelled.
    /// </summary>
    [Fact]
    public async Task A_sweep_cannot_collide_with_an_id_the_agent_chose_itself()
    {
        // LeaveWorking, or the fake broker fills every order on arrival, the working list is empty
        // and a sweep with nothing to sweep passes every assertion vacuously.
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        // The agent chooses exactly the id the old scheme would derive for the first cancellation.
        var placed = await client.SendAsync(Buy("sweep-1-0", "ES")).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(placed.Ok, Json.Write(placed.Error));
        Assert.Single(conn.Broker.Orders);
        Assert.Equal(ExecutionState.WORKING, conn.Broker.Orders.Single().State);
        Assert.Single(await gw.OrdersAsync(false));   // there IS something for the sweep to cancel

        var sweep = await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "sweep-1" })
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(sweep.Ok, Json.Write(sweep.Error));

        var data = (JsonElement)sweep.Data!;
        var claimed = data.GetProperty("cancelled").GetInt32();

        // Whatever it claims, it must be true. Count what the broker actually shows as cancelled.
        var reallyCancelled = (await gw.OrdersAsync(true)).Count(o => o.State == ExecutionState.CANCELLED);
        Assert.True(claimed <= reallyCancelled,
            $"cancel-all reported cancelled={claimed} while only {reallyCancelled} order(s) are actually cancelled");
    }

    /// <summary>
    /// The reserved separator is refused on the way in, which is what makes a derived id
    /// uncollidable by construction rather than by hoping the agent picks different words.
    /// </summary>
    [Fact]
    public async Task A_request_id_containing_the_reserved_separator_is_refused()
    {
        var (gw, _, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        var reply = await client.SendAsync(Buy("mine#cancel-all#0", "ES")).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(reply.Ok, "an id in the shape the sweep derives was accepted");
        Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), reply.Error!.Code);
    }

    /// <summary>
    /// The count is of cancellations that LANDED. With nothing working, a sweep cancels nothing and
    /// must say so — and the other direction, a real working order, is cancelled and counted once.
    /// </summary>
    [Fact]
    public async Task The_count_is_what_landed_not_what_was_attempted()
    {
        // LeaveWorking, or the fake broker fills every order on arrival, the working list is empty
        // and a sweep with nothing to sweep passes every assertion vacuously.
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        var empty = (JsonElement)(await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "sweep-empty" })
            .WaitAsync(TimeSpan.FromSeconds(10))).Data!;
        Assert.Equal(0, empty.GetProperty("cancelled").GetInt32());
        Assert.Equal(0, empty.GetProperty("attempted").GetInt32());

        Assert.True((await client.SendAsync(Buy("sweep-2-place", "ES")).WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        var sweep = (JsonElement)(await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "sweep-2" })
            .WaitAsync(TimeSpan.FromSeconds(10))).Data!;

        var claimed = sweep.GetProperty("cancelled").GetInt32();
        Assert.Equal(1, sweep.GetProperty("attempted").GetInt32());   // not a vacuous sweep
        var reallyCancelled = (await gw.OrdersAsync(true)).Count(o => o.State == ExecutionState.CANCELLED);
        Assert.Equal(reallyCancelled, claimed);
        Assert.Equal(sweep.GetProperty("attempted").GetInt32() - claimed,
            sweep.GetProperty("not_cancelled").GetArrayLength());
    }
}

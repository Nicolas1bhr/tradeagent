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
    /// EVERY ID THE GATEWAY MINTS LEAVES THIS PROCESS ON A BROKER ORDER, so its charset is a safety
    /// property and not a style question.
    ///
    /// The id is carried onto the order as <c>TA-{id}</c>, and safety rule 1 requires that field to
    /// round-trip. The previous scheme minted <c>TA-…#close-all#0</c> — and whether ATAS accepts
    /// <c>#</c> in a client order id is not knowable from here, only on the box. This asserts every
    /// minted id is <c>[A-Za-z0-9-]</c>, from a sweep whose own id is at the edge of what is allowed.
    /// </summary>
    [Fact]
    public async Task Every_id_the_gateway_mints_is_in_the_conservative_charset()
    {
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        Assert.True((await client.SendAsync(Buy("mint-a", "ES")).WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        Assert.True((await client.SendAsync(Buy("mint-b", "NQ")).WaitAsync(TimeSpan.FromSeconds(10))).Ok);

        var sweep = (JsonElement)(await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "sweep-mint" })
            .WaitAsync(TimeSpan.FromSeconds(10))).Data!;
        Assert.Equal(2, sweep.GetProperty("attempted").GetInt32());

        var minted = sweep.GetProperty("requests").EnumerateArray()
            .Select(r => r.GetProperty("request_id").GetString()!).ToList();
        Assert.Equal(2, minted.Count);

        foreach (var id in minted)
        {
            Assert.Matches("^[A-Za-z0-9-]+$", id);
            Assert.StartsWith("op-", id);
            // And what actually reaches the broker, which is the string the rule is about.
            Assert.Matches("^[A-Za-z0-9-]+$", TradingGateway.ClientOrderIdFor(id));
        }
        Assert.Equal(minted.Count, minted.Distinct().Count());
    }

    /// <summary>
    /// The reserved PREFIX is refused on the way in. That is what makes a minted id uncollidable by
    /// construction rather than by hoping the agent picks different words.
    /// </summary>
    [Theory]
    [InlineData("op-deadbeef-cancelall-0")]
    [InlineData("op-anything")]
    [InlineData("OP-UPPERCASE")]
    public async Task A_request_id_using_the_reserved_minted_prefix_is_refused(string id)
    {
        var (gw, _, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        var reply = await client.SendAsync(Buy(id, "ES")).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(reply.Ok, $"'{id}' was accepted, and it can collide with a minted sweep id");
        Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), reply.Error!.Code);
    }

    /// <summary>
    /// An id that would not survive the trip to the broker is refused before an order carries it.
    /// </summary>
    [Theory]
    [InlineData("has space")]
    [InlineData("has#hash")]
    [InlineData("has/slash")]
    [InlineData("has_underscore")]
    [InlineData("émoji")]
    public async Task A_request_id_outside_the_conservative_charset_is_refused(string id)
    {
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        var reply = await client.SendAsync(Buy(id, "ES")).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(reply.Ok, $"'{id}' was accepted and would have reached the broker as TA-{id}");
        Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), reply.Error!.Code);
        Assert.Empty(conn.Broker.Orders);
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

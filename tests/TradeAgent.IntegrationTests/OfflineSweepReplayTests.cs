using System.Text.Json;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using TradeAgent.TradeCli;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// A REPLAYED SWEEP ANSWERS FROM THE STORE WITH THE PLATFORM UNREACHABLE — OVER THE REAL PIPE.
///
/// <c>TradingGateway.BeginCompositeAsync</c> takes the plan as a delegate and does not invoke it on
/// a replay, and <c>CompositeReplayBindingTests</c> proves that at the gateway. The pipe server did
/// not use it: <c>CancelAll</c> read the working orders and <c>CloseAll</c> read the positions
/// BEFORE claiming the id, so the lookup that would have returned the stored answer never ran. With
/// the connector unreachable — which is the state the world is in when a reply goes missing — the
/// same request id came back <c>TRADING_CONNECTION_MISSING</c> instead of the answer sitting in the
/// database, and made one connector call finding that out.
///
/// That is the whole reason a request id exists: the agent lost the reply, the platform is exactly
/// as unreachable as it was when the reply went missing, and asking again must be safe and must
/// work. The measurement is <see cref="RecordingConnector.Calls"/> — reads included — because
/// "answered from the store" and "answered after asking the platform" produce the same bytes.
/// </summary>
public class OfflineSweepReplayTests(ITestOutputHelper log)
{
    static string NewPipe() => "ta-offrep-" + Guid.NewGuid().ToString("n")[..12];

    static async Task<(TradingGateway Gw, RecordingConnector Conn, Database Db, GatewayPipeServer Server, PipeClient Client)>
        Counted(FaultProfile? faults = null)
    {
        var db = TestEnv.NewDb();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker(), faults));
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        var pipe = NewPipe();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);
        return (gw, conn, db, server, client);
    }

    static Task<IpcResponse> Send(PipeClient client, IpcRequest req) =>
        client.SendAsync(req).WaitAsync(TimeSpan.FromSeconds(10));

    /// <summary>A limit far from the market, so it rests as WORKING and there is something to sweep.</summary>
    static IpcRequest Resting(string requestId) => new()
    {
        Op = Ops.Buy,
        RequestId = requestId,
        Args = new()
        {
            ["symbol"] = JsonSerializer.SerializeToElement("ES"),
            ["quantity"] = JsonSerializer.SerializeToElement("1"),
            ["limit"] = JsonSerializer.SerializeToElement("1")
        }
    };

    static string Body(IpcResponse r) => Json.Write(r.Data ?? r.Error) ?? "<nothing>";

    /// <summary>
    /// CANCEL-ALL: the sweep completed, then the platform went away, then the agent asked again.
    /// It gets the answer it already had, and nothing is asked of the platform to produce it.
    /// </summary>
    [Fact]
    public async Task A_completed_cancel_all_replayed_offline_answers_from_the_store_and_reads_nothing()
    {
        var (gw, conn, db, server, client) = await Counted(new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        Assert.True((await Send(client, Resting("or-open-1"))).Ok);

        var first = await Send(client, new IpcRequest { Op = Ops.CancelAll, RequestId = "or-cancel-1" });
        log.WriteLine($"cancel-all or-cancel-1        : ok={first.Ok} {Body(first)}");
        Assert.True(first.Ok, Body(first));

        conn.Faults.Disconnected = true;                    // the platform is gone, as it was when the reply was lost
        var calls = conn.Calls;
        var replay = await Send(client, new IpcRequest { Op = Ops.CancelAll, RequestId = "or-cancel-1" });
        log.WriteLine($"replayed while unreachable    : ok={replay.Ok} {Body(replay)}");
        log.WriteLine($"connector calls during the replay : {conn.Calls - calls}");

        Assert.True(replay.Ok, Body(replay));
        Assert.Equal(Body(first), Body(replay));
        Assert.Equal(calls, conn.Calls);

        conn.Faults.Disconnected = false;
        await gw.DisposeAsync();
    }

    /// <summary>
    /// CLOSE-ALL: the same, one read over. The position read is what used to fail first, and the
    /// answer to a lost close-all reply is the one an owner is least able to wait for.
    /// </summary>
    [Fact]
    public async Task A_completed_close_all_replayed_offline_answers_from_the_store_and_reads_nothing()
    {
        var (gw, conn, db, server, client) = await Counted();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        // A market buy that fills, so there is a position for close-all to flatten.
        var opened = await Send(client, new IpcRequest
        {
            Op = Ops.Buy,
            RequestId = "or-open-2",
            Args = new()
            {
                ["symbol"] = JsonSerializer.SerializeToElement("ES"),
                ["quantity"] = JsonSerializer.SerializeToElement("2")
            }
        });
        Assert.True(opened.Ok, Body(opened));

        var first = await Send(client, new IpcRequest { Op = Ops.CloseAll, RequestId = "or-close-1" });
        log.WriteLine($"close-all or-close-1          : ok={first.Ok} {Body(first)}");
        Assert.True(first.Ok, Body(first));

        conn.Faults.Disconnected = true;
        var calls = conn.Calls;
        var replay = await Send(client, new IpcRequest { Op = Ops.CloseAll, RequestId = "or-close-1" });
        log.WriteLine($"replayed while unreachable    : ok={replay.Ok} {Body(replay)}");
        log.WriteLine($"connector calls during the replay : {conn.Calls - calls}");

        Assert.True(replay.Ok, Body(replay));
        Assert.Equal(Body(first), Body(replay));
        Assert.Equal(calls, conn.Calls);

        conn.Faults.Disconnected = false;
        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE OTHER DIRECTION: a request id this installation has never seen is not a replay, and the
    /// lookup moving to the front must not turn an unreachable platform into a stored answer for it.
    /// Both sweeps still fail, and they fail with the connector's own reason.
    /// </summary>
    [Fact]
    public async Task A_new_request_id_with_the_connector_unreachable_still_fails()
    {
        var (gw, conn, db, server, client) = await Counted();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        conn.Faults.Disconnected = true;

        var cancel = await Send(client, new IpcRequest { Op = Ops.CancelAll, RequestId = "or-new-cancel" });
        log.WriteLine($"cancel-all, id never seen     : ok={cancel.Ok} code={cancel.Error?.Code} {cancel.Error?.Message}");
        var close = await Send(client, new IpcRequest { Op = Ops.CloseAll, RequestId = "or-new-close" });
        log.WriteLine($"close-all,  id never seen     : ok={close.Ok} code={close.Error?.Code} {close.Error?.Message}");

        Assert.False(cancel.Ok);
        Assert.Equal(nameof(ErrorCode.TRADING_CONNECTION_MISSING), cancel.Error!.Code);
        Assert.False(close.Ok);
        Assert.Equal(nameof(ErrorCode.TRADING_CONNECTION_MISSING), close.Error!.Code);

        // And no composite was claimed for either: the plan was never captured, so there is no
        // stored row for a later call to be handed as an answer.
        Assert.Null(gw.Composites.Get("or-new-cancel"));
        Assert.Null(gw.Composites.Get("or-new-close"));

        conn.Faults.Disconnected = false;
        await gw.DisposeAsync();
    }

    /// <summary>
    /// AND A FRESH SWEEP STILL READS THE BOOK — EXACTLY ONCE. Moving the read into a delegate is
    /// only correct if the delegate still runs for an id that is new, and runs once. Measured on an
    /// empty book and a flat account, where the plan read is the only connector call a sweep makes,
    /// so the number is the delegate's own and nothing else's.
    /// </summary>
    [Fact]
    public async Task A_fresh_sweep_reads_the_book_exactly_once()
    {
        var (gw, conn, db, server, client) = await Counted();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        var reads = conn.Reads;
        var cancel = await Send(client, new IpcRequest { Op = Ops.CancelAll, RequestId = "or-fresh-cancel" });
        log.WriteLine($"cancel-all on an empty book   : ok={cancel.Ok} {Body(cancel)}");
        log.WriteLine($"order reads it made           : {conn.Reads - reads}");
        Assert.True(cancel.Ok, Body(cancel));
        Assert.Equal(reads + 1, conn.Reads);

        var positions = conn.Positions;
        var close = await Send(client, new IpcRequest { Op = Ops.CloseAll, RequestId = "or-fresh-close" });
        log.WriteLine($"close-all on a flat account   : ok={close.Ok} {Body(close)}");
        log.WriteLine($"position reads it made        : {conn.Positions - positions}");
        Assert.True(close.Ok, Body(close));
        Assert.Equal(positions + 1, conn.Positions);

        await gw.DisposeAsync();
    }
}

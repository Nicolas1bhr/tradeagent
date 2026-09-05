using System.Text.Json;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Gateway;
using TradeAgent.Core.Db;
using TradeAgent.Security;
using TradeAgent.TradeCli;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE VERB BINDING, OVER THE REAL PIPE (REVIEW 2026-09-05, Codex F7).
///
/// The gateway-level proof is <c>CompositeReplayBindingTests</c>; this is the same rule seen from
/// where an agent sits, because <c>cancel-all</c> and <c>close-all</c> are the two ops that key a
/// <c>composite_request</c> and the reply an agent gets is the whole of what it knows. Reusing a
/// completed <c>close-all</c>'s id for a <c>cancel-all</c> used to answer <c>ok</c> with the
/// close-all's stored reply — so an agent could be told its cancellation had already happened while
/// every one of its orders was still working.
/// </summary>
public class CompositeVerbBindingTests(ITestOutputHelper log)
{
    static string NewPipe() => "ta-verb-" + Guid.NewGuid().ToString("n")[..12];

    [Fact]
    public async Task A_close_alls_request_id_cannot_be_reused_for_a_cancel_all()
    {
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        // One resting order, so a cancel-all has something real to do.
        var placed = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Buy,
            RequestId = "cv-open",
            Args = new()
            {
                ["symbol"] = JsonSerializer.SerializeToElement("ES"),
                ["quantity"] = JsonSerializer.SerializeToElement("1"),
                ["limit"] = JsonSerializer.SerializeToElement("1")
            }
        }).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(placed.Ok, Json.Write(placed.Error));

        var closeAll = await client.SendAsync(new IpcRequest { Op = Ops.CloseAll, RequestId = "cv-1" })
            .WaitAsync(TimeSpan.FromSeconds(10));
        log.WriteLine($"close-all  cv-1 : ok={closeAll.Ok} {Json.Write(closeAll.Data ?? closeAll.Error)}");
        Assert.True(closeAll.Ok, Json.Write(closeAll.Error));

        var cancelAll = await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "cv-1" })
            .WaitAsync(TimeSpan.FromSeconds(10));
        log.WriteLine($"cancel-all cv-1 : ok={cancelAll.Ok} {Json.Write(cancelAll.Data ?? cancelAll.Error)}");
        log.WriteLine($"orders at the broker : " +
                      $"{string.Join(", ", conn.Broker.Orders.Select(o => $"{o.ConnectorOrderId} {o.State}"))}");

        Assert.False(cancelAll.Ok);
        Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), cancelAll.Error!.Code);
        Assert.Contains(Ops.CloseAll, cancelAll.Error.Message);

        // ...and the order it would have cancelled is untouched, which is the point: the agent was
        // not told "already done" about work that never happened.
        Assert.Equal(ExecutionState.WORKING, conn.Broker.Orders.Single().State);
    }
}

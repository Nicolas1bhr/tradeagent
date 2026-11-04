using System.IO.Pipes;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE DOOR IS OPEN WHEN <see cref="GatewayPipeServer.Start"/> RETURNS. Main's push CI at 2a4a7edc failed
/// <c>OversightPageTests.Caller_text_never_reaches_the_pages_findings</c> on Windows with a <see cref="TimeoutException"/> from
/// the client's five-second connect: the first pipe instance was created inside the accept loop's <c>Task.Run</c>, so on a
/// loaded thread pool (that shard ran 43 minutes) the server could still have no instance when the caller's budget ran out.
/// A caller told the server has started may connect at once; one attempt, no wait, is enough.
/// </summary>
public class GatewayPipeDoorTests
{
    [Fact]
    public async Task A_started_server_accepts_a_connection_at_once_without_waiting_for_its_accept_loop()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;
        await using var _gw = gw;
        for (var i = 0; i < 20; i++)
        {
            var pipe = "ta-door-" + Guid.NewGuid().ToString("n")[..12];
            await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
            server.Start();
            await using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            // ONE ATTEMPT, NO WAIT: zero is the whole budget, so this measures the door, not the scheduler.
            client.Connect(0);
            Assert.True(client.IsConnected, $"attempt {i}: the started server had no pipe instance to connect to");
        }
    }
}

using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE PIPE KNOWS WHICH LAUNCH IS CALLING, OR IT KNOWS NOTHING AND GRANTS NOTHING.
///
/// Every process on the agent's side of the fence can read the machine token: it is a file under the
/// user's own account, and the CLI reads it on every invocation. So a token proves "something on this
/// machine" and nothing else, and until this unit that was the whole of the gateway's knowledge —
/// one <c>TRADEAGENT_SESSION</c> for all, and <c>AgentContext.ForAgent</c> returning a caller that
/// every rule downstream treated as the chair.
///
/// These tests speak the wire by hand rather than through <c>PipeClient</c>, deliberately: the
/// interesting client is the one that does NOT do what the product's client does — presents the
/// machine token alone, or presents a role's grant and asks for another role's work.
/// </summary>
[Collection("containment")]
public class LaunchGrantTests
{
    [Fact]
    public async Task The_machine_token_alone_is_not_served_as_the_chair()
    {
        var pipe = NewPipe();
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { Grants = new AgentGrants() };
        server.Start();

        await using var client = await Raw.ConnectAsync(pipe);
        var hello = await client.SendAsync(new IpcRequest { Op = Ops.Hello, Token = IpcToken.Ensure() });
        Assert.True(hello.Ok, "a caller holding the machine token should still be served — it just is not anybody");

        var buy = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Buy,
            Token = IpcToken.Ensure(),
            Session = "some-agent",
            RequestId = "grant-red-1",
            Args = new() { ["symbol"] = JsonSerializer.SerializeToElement("ES"), ["quantity"] = JsonSerializer.SerializeToElement("1") }
        });

        Assert.False(buy.Ok,
            "a client holding nothing but the machine token — which every process on the agent's side " +
            "of the fence can read — was served an order as though it were the Operations Director");
        Assert.Equal(nameof(ErrorCode.ROLE_MAY_NOT_TRADE), buy.Error?.Code);
    }

    [Fact]
    public async Task Research_cannot_place_an_order_with_its_own_grant()
    {
        var pipe = NewPipe();
        var grants = new AgentGrants();
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { Grants = grants };
        server.Start();

        using var research = grants.Issue(CouncilRoles.Research, "attempt-r1");
        await using var client = await Raw.ConnectAsync(pipe);
        var hello = await client.SendAsync(new IpcRequest { Op = Ops.Hello, Token = IpcToken.Ensure(), Grant = research.Token });
        Assert.True(hello.Ok);

        var buy = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Buy, Token = IpcToken.Ensure(), Session = "research", RequestId = "grant-red-2",
            Args = new() { ["symbol"] = JsonSerializer.SerializeToElement("ES"), ["quantity"] = JsonSerializer.SerializeToElement("1") }
        });

        Assert.False(buy.Ok, "the Research Director's launch placed an order; the doctrine gives it no order permission");
        Assert.Equal(nameof(ErrorCode.ROLE_MAY_NOT_TRADE), buy.Error?.Code);

        // The same grant, on a read, is served: the refusal is about what the role may do, not about
        // whether it may speak.
        var positions = await client.SendAsync(new IpcRequest { Op = Ops.Positions, Token = IpcToken.Ensure(), Session = "research" });
        Assert.True(positions.Ok);
    }

    [Fact]
    public async Task The_chairs_own_grant_places_the_order_research_could_not()
    {
        var pipe = NewPipe();
        var grants = new AgentGrants();
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { Grants = grants };
        server.Start();

        using var chair = grants.Issue(CouncilRoles.Operations, "attempt-o1");
        await using var client = await Raw.ConnectAsync(pipe);
        await client.SendAsync(new IpcRequest { Op = Ops.Hello, Token = IpcToken.Ensure(), Grant = chair.Token });

        var buy = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Buy, Token = IpcToken.Ensure(), Session = "operations", RequestId = "grant-green-1",
            Args = new() { ["symbol"] = JsonSerializer.SerializeToElement("ES"), ["quantity"] = JsonSerializer.SerializeToElement("1") }
        });

        Assert.True(buy.Ok, $"the chair's own launch was refused: {buy.Error?.Code} {buy.Error?.Message}");
    }

    [Fact]
    public async Task A_grant_whose_turn_is_over_is_refused_at_hello()
    {
        var pipe = NewPipe();
        var now = DateTimeOffset.UtcNow;
        var grants = new AgentGrants(() => now);
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { Grants = grants };
        server.Start();

        var grant = grants.Issue(CouncilRoles.Operations, "attempt-expired", TimeSpan.FromMinutes(5));
        now = now.AddHours(1);

        await using var client = await Raw.ConnectAsync(pipe);
        var hello = await client.SendAsync(new IpcRequest { Op = Ops.Hello, Token = IpcToken.Ensure(), Grant = grant.Token });

        Assert.False(hello.Ok, "a launch grant whose turn ended an hour ago was accepted");
        Assert.Equal(nameof(ErrorCode.IPC_UNAUTHENTICATED), hello.Error?.Code);
    }

    [Fact]
    public async Task A_grant_this_app_never_issued_is_refused_at_hello()
    {
        var pipe = NewPipe();
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { Grants = new AgentGrants() };
        server.Start();

        await using var client = await Raw.ConnectAsync(pipe);
        var hello = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Hello, Token = IpcToken.Ensure(),
            Grant = "00000000000000000000000000000000000000000000000000000000000000ff"
        });

        Assert.False(hello.Ok, "a launch grant nobody issued was accepted");
        Assert.Equal(nameof(ErrorCode.IPC_UNAUTHENTICATED), hello.Error?.Code);
    }

    /// <summary>
    /// The peer-image rule on the live connection, with the kernel call replaced by a stand-in —
    /// the RULE is what this build owns, and it can only be exercised where the answer can be
    /// arranged. <c>PeerImageRuleTests</c> covers the rule's own decisions.
    /// </summary>
    [Fact]
    public async Task A_copied_trade_command_may_not_present_a_grant()
    {
        var pipe = NewPipe();
        var grants = new AgentGrants();
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;

        var deployed = Path.Combine(Paths.Bin, ToolDeployer.TradeCliName);
        await File.WriteAllTextAsync(deployed, "the trade command this app deployed");
        var copy = Path.Combine(Paths.Workspace, "agent", ToolDeployer.TradeCliName);
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(deployed, copy, overwrite: true);

        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe)
        {
            Grants = grants,
            Peer = Containment.PeerRuleNow() with { ExpectedHash = PeerImage.HashOf(deployed) },
            PeerImagePath = _ => copy      // the byte-identical copy inside the agent's own workspace
        };
        server.Start();

        using var chair = grants.Issue(CouncilRoles.Operations, "attempt-o2");
        await using var client = await Raw.ConnectAsync(pipe);
        var hello = await client.SendAsync(new IpcRequest { Op = Ops.Hello, Token = IpcToken.Ensure(), Grant = chair.Token });

        Assert.False(hello.Ok,
            "a copy of the trade command, inside the folder the agent itself writes to, presented the " +
            "chair's launch grant and was served");
        // The WORKSPACE clause by name. The word appears in the copy's path too, so asserting on
        // "workspace" alone would still pass when what refused it was the ordinary path rule — and
        // the order of those two clauses is the whole point of the rule.
        Assert.Contains("inside the agent's own workspace", hello.Error?.Message ?? "");
    }

    static string NewPipe() => "ta-grant-" + Guid.NewGuid().ToString("n")[..12];

    /// <summary>
    /// A client that sends exactly the frame it is given. <c>PipeClient</c> always says hello with
    /// whatever grant the environment holds, which is the right behaviour for the product and the
    /// wrong instrument for testing what happens when a peer does something else.
    /// </summary>
    sealed class Raw : IAsyncDisposable
    {
        NamedPipeClientStream _pipe = null!;
        StreamReader _r = null!;
        StreamWriter _w = null!;

        public static async Task<Raw> ConnectAsync(string pipeName)
        {
            var c = new Raw { _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous) };
            await c._pipe.ConnectAsync(5000);
            c._r = new StreamReader(c._pipe, new UTF8Encoding(false), false, 8192, leaveOpen: true);
            c._w = new StreamWriter(c._pipe, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };
            return c;
        }

        public async Task<IpcResponse> SendAsync(IpcRequest req)
        {
            await _w.WriteLineAsync(Core.Json.Write(req));
            var line = await _r.ReadLineAsync() ?? throw new IOException("the gateway closed the connection");
            return Core.Json.Read<IpcResponse>(line) ?? throw new IOException("unreadable reply");
        }

        public ValueTask DisposeAsync()
        {
            _r.Dispose();
            _w.Dispose();
            return _pipe.DisposeAsync();
        }
    }
}

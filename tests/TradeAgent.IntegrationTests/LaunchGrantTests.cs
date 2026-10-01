using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
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
/// machine token alone, or presents a role's grant and asks for another role's work. The ones that
/// ask the KERNEL who is calling are the exception, and run the real trade command: there the
/// program on the other end is the thing being checked, so it cannot be a stand-in.
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

    /// <summary>
    /// WHO IS ON THE OTHER END, ASKED OF THE KERNEL ITSELF — no stand-in. A connection this process
    /// dials is held by this process, so the answer is this program's own image, in the form the
    /// kernel reports it: every symbolic link resolved.
    /// </summary>
    [Fact]
    public async Task The_kernel_names_the_program_on_the_other_end_of_a_connection()
    {
        var name = NewPipe();
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accepted = server.WaitForConnectionAsync();
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        await accepted;

        var self = Real(Environment.ProcessPath ?? throw new InvalidOperationException("this process has no path"));
        Assert.Equal(self, PeerImage.ClientPath(server), ignoreCase: OperatingSystem.IsWindows());
    }

    /// <summary>
    /// THE OBSERVED RUN'S REFUSAL, end to end and with nothing replaced: TradeAgent's own trade
    /// command, put where the app puts it by the code that puts it there, presents a Research launch
    /// grant to a gateway holding exactly the rule <c>AppHost</c> configures — and is served.
    /// </summary>
    [Fact]
    public async Task The_trade_command_this_app_deployed_presents_its_grant_and_is_served()
    {
        var pipe = NewPipe();
        var grants = new AgentGrants();
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;

        var deployed = Deploy();
        try
        {
            var rule = Containment.PeerRuleNow();
            Assert.NotNull(rule.ExpectedHash);     // admitted below therefore means: this path, these bytes
            await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { Grants = grants, Peer = rule };
            server.Start();

            using var research = grants.Issue(CouncilRoles.Research, "attempt-peer-1");
            var (code, stdout, stderr) = await RunAsync(deployed, pipe, research.Token, "data", "list", "--json");

            Assert.True(code == 0,
                $"TradeAgent's own trade command at {deployed} presented its launch grant and was not served " +
                $"(exit {code}): {Said(stdout)} {stderr}");
            using var doc = JsonDocument.Parse(stdout);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), stdout);
        }
        finally { Undeploy(); }
    }

    /// <summary>
    /// The same bytes anywhere else are a different program. A copy of the deployed command — so its
    /// hash IS the recorded one and only the path can refuse it — outside the app's bin, the runtime
    /// folder and the workspace, presenting a live grant, is refused, and the refusal names it.
    /// </summary>
    [Fact]
    public async Task A_copy_of_the_trade_command_anywhere_else_is_refused_by_name()
    {
        var pipe = NewPipe();
        var grants = new AgentGrants();
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;

        Deploy();
        var elsewhere = Path.Combine(Path.GetTempPath(), "ta-" + Guid.NewGuid().ToString("n")[..12]);
        try
        {
            var copy = CopyDeployedTo(elsewhere);

            await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe)
            {
                Grants = grants,
                Peer = Containment.PeerRuleNow()
            };
            server.Start();

            using var research = grants.Issue(CouncilRoles.Research, "attempt-peer-2");
            var (code, stdout, _) = await RunAsync(copy, pipe, research.Token, "data", "list", "--json");

            Assert.True(code != 0, $"a copy of the trade command at {copy} presented a launch grant and was served: {stdout}");
            var said = Said(stdout);
            // Its own name, which nothing else on this machine has: the folder is fresh. Short or long
            // form of the folders above it is the operating system's choice, not this rule's.
            Assert.Contains(Path.Combine(Path.GetFileName(elsewhere), ToolDeployer.TradeCliName), said);
            Assert.Contains("but TradeAgent's own trade command is", said);
        }
        finally
        {
            Undeploy();
            try { Directory.Delete(elsewhere, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The folder clauses come FIRST, and they must meet the kernel's answer in the kernel's form. On
    /// macOS this workspace is under /var and the kernel says /private/var: a clause comparing the two
    /// literally lets the PATH clause do the refusing instead — the right answer from the wrong rule,
    /// and the rule that does not depend on a record would be gone.
    /// </summary>
    [Fact]
    public async Task A_copy_in_the_agents_workspace_is_refused_by_the_workspace_clause_as_the_kernel_names_it()
    {
        var pipe = NewPipe();
        var grants = new AgentGrants();
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;

        Deploy();
        var inside = Path.Combine(Paths.Workspace, "copy-" + Guid.NewGuid().ToString("n")[..8]);
        try
        {
            var copy = CopyDeployedTo(inside);

            await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe)
            {
                Grants = grants,
                Peer = Containment.PeerRuleNow()
            };
            server.Start();

            using var research = grants.Issue(CouncilRoles.Research, "attempt-peer-3");
            var (code, stdout, _) = await RunAsync(copy, pipe, research.Token, "data", "list", "--json");

            Assert.True(code != 0, $"a copy of the trade command inside the agent's workspace ({copy}) presented a launch grant and was served: {stdout}");
            Assert.Contains("inside the agent's own workspace", Said(stdout));
        }
        finally
        {
            Undeploy();
            try { Directory.Delete(inside, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// A byte-identical copy of the deployed command and everything beside it, in <paramref name="dir"/>,
    /// ready to run. Returns the command's path.
    /// </summary>
    static string CopyDeployedTo(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(Paths.Bin)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        var copy = Path.Combine(dir, ToolDeployer.TradeCliName);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(copy, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return copy;
    }

    /// <summary>
    /// Deploys TradeAgent's own trade command the way the app does at start — <c>ToolDeployer</c>,
    /// from the build output — which also records the hash the rule checks. Whatever another test
    /// left at that path goes first: the deployer copies only over an OLDER file, and
    /// <c>ContainmentTests</c> writes a shim there.
    /// </summary>
    static string Deploy()
    {
        var exe = Path.Combine(Paths.Bin, ToolDeployer.TradeCliName);
        if (File.Exists(exe)) File.Delete(exe);
        Assert.Equal(exe, ToolDeployer.EnsureTradeCli(Path.GetDirectoryName(Build.TradeCliDll)));
        return exe;
    }

    /// <summary>Takes the deployed command and its record away again, so no later test inherits them.</summary>
    static void Undeploy()
    {
        try { File.Delete(Path.Combine(Paths.Bin, ToolDeployer.TradeCliName)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        try { File.Delete(ToolDeployer.DeployedHashFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Runs a trade command as the agent's runtime would: the pipe and the launch grant in its
    /// environment and nowhere else. The command is framework-dependent, so it is told where .NET is
    /// when this test host was not — the app does the same for the processes it starts.
    /// </summary>
    static async Task<(int Code, string Out, string Err)> RunAsync(string exe, string pipe, string grant, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["TRADEAGENT_PIPE"] = pipe;
        psi.Environment[AgentGrants.Variable] = grant;
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_ROOT")))
            psi.Environment["DOTNET_ROOT"] = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));

        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await p.WaitForExitAsync(cts.Token);
        return (p.ExitCode, await so, await se);
    }

    /// <summary>What the command's JSON said went wrong, or the whole output when it is not JSON.</summary>
    static string Said(string stdout)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            return doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object
                ? $"{e.GetProperty("code").GetString()}: {e.GetProperty("message").GetString()}"
                : stdout;
        }
        catch (JsonException) { return stdout; }
    }

    /// <summary>
    /// The path with every symbolic link resolved — the form macOS and Linux report a program's image
    /// in (on macOS a home under $TMPDIR is under /var, which is /private/var) — computed here, by
    /// libc, rather than by the code under test. Windows: as given.
    /// </summary>
    static string Real(string path)
    {
        if (OperatingSystem.IsWindows()) return path;
        var buffer = new byte[4096];
        if (realpath(path, buffer) == IntPtr.Zero)
            throw new IOException($"realpath({path}) failed with errno {Marshal.GetLastPInvokeError()}");
        return Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0));
    }

    [DllImport("libc", SetLastError = true)]
    static extern IntPtr realpath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] resolved);

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

using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// ROUTE (a) AND THE STAGE THAT CARRIES IT (<c>U-cell-linux-c</c>): the cell stage's own environment built from scratch, what it
/// leaves behind, and — with the pinned linux-musl codex CI fetches — <c>codex exec-server --listen stdio</c> running in a real
/// cell while the harness that holds the login stays outside. Every row runs the product's launcher; a missing prerequisite
/// FAILS, it is never skipped on Linux.
/// </summary>
public partial class CellLinuxTests
{
    /// <summary>The runtime IPC files a .NET process with diagnostics on leaves in <c>/tmp</c>, by the pid in their names.</summary>
    static List<string> RuntimeFilesOf(string pid) =>
        [.. Directory.EnumerateFileSystemEntries("/tmp")
            .Where(p => (Path.GetFileName(p).StartsWith("dotnet-diagnostic-" + pid + "-", StringComparison.Ordinal)
                         || Path.GetFileName(p).StartsWith("clr-debug-pipe-" + pid + "-", StringComparison.Ordinal)))];

    /// <summary>
    /// ROW (d): THE CELL STAGE LEAVES NOTHING IN /tmp. The stage is a .NET process that becomes the program by execve before its
    /// runtime shuts down, so with diagnostics on it left <c>dotnet-diagnostic-&lt;pid&gt;-…-socket</c> and
    /// <c>clr-debug-pipe-&lt;pid&gt;-in/out</c> behind, named by the cell's own pid (execve keeps it). Its environment is now built
    /// from scratch with <c>DOTNET_EnableDiagnostics=0</c>, and none is made.
    /// </summary>
    [LinuxFact]
    public async Task The_cell_stage_leaves_nothing_in_tmp()
    {
        PlatformIsACells();
        var w = new World("tmp");
        var ran = await InCell(w.Policy(), "/bin/sh", "-c", "echo cell-pid=$$");
        Assert.Equal(0, ran.Code);
        var pid = CellPid(ran);
        Assert.NotEqual("none", pid);
        var left = RuntimeFilesOf(pid);
        Assert.True(left.Count == 0, $"the cell stage (pid {pid}) left in /tmp: {string.Join(", ", left)}");
    }

    /// <summary>
    /// ROW (e), A GUARD: A CELL CANNOT REACH THE APP'S OWN DIAGNOSTIC SOCKET OR DEBUG PIPES. The test host is a .NET process with
    /// diagnostics on, as the app is; its <c>dotnet-diagnostic-&lt;pid&gt;</c> socket and <c>clr-debug-pipe-&lt;pid&gt;-in</c> sit in
    /// <c>/tmp</c>, reachable from outside. From inside the cell an AF_UNIX socket is refused by seccomp and <c>/tmp</c> is granted
    /// to nobody, so neither is reached.
    /// </summary>
    [LinuxFact]
    public async Task A_cell_cannot_reach_the_apps_diagnostic_socket_or_debug_pipes()
    {
        PlatformIsACells();
        var self = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var mine = RuntimeFilesOf(self);
        var socket = mine.FirstOrDefault(p => Path.GetFileName(p).StartsWith("dotnet-diagnostic-", StringComparison.Ordinal));
        var pipe = mine.FirstOrDefault(p => Path.GetFileName(p).EndsWith("-in", StringComparison.Ordinal));
        Require(socket is not null && pipe is not null,
            $"the test host's own diagnostic socket and debug pipe in /tmp (found: {string.Join(", ", mine.DefaultIfEmpty("none"))})");
        var w = new World("diag");
        var probe = w.Script("diag.py", """
            import os, socket, sys
            sock, pipe = sys.argv[1], sys.argv[2]
            try:
                s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM); s.connect(sock); print("socket-connected")
            except PermissionError:
                print("socket-denied")
            try:
                fd = os.open(pipe, os.O_WRONLY | os.O_NONBLOCK); print("pipe-opened"); os.close(fd)
            except PermissionError:
                print("pipe-denied")
            except OSError as e:
                print("pipe-error " + str(e.errno))
            """);
        var outside = await Outside("/usr/bin/python3", probe, socket!, pipe!);
        Says(outside, "socket-connected");
        var ran = await InCell(w.Policy(), "/usr/bin/python3", probe, socket!, pipe!);
        Says(ran, "socket-denied", "pipe-denied");
    }

    /// <summary>
    /// L3, A GUARD ON THE GRANT: A CELL-LEAF PROCESS IS OUTSIDE THE HARNESS'S GRANT. Route (a)'s harness runs in
    /// <c>&lt;attempt&gt;/harness</c> and its launch grant answers to that leaf alone. A process of the same session that moved into the
    /// attempt's <c>cell/</c> leaf — still beneath the attempt, where -b's container on the attempt would have counted it a member —
    /// presents the harness's grant and is refused naming the cgroup; the harness's own process is served.
    /// </summary>
    [LinuxFact]
    public async Task A_cell_leaf_process_is_outside_the_harness_grant()
    {
        PlatformIsACells();
        using var rows = Rows();
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;
        var pipe = "ta-ra-" + Guid.NewGuid().ToString("n")[..12];
        var grants = new AgentGrants();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { Grants = grants };
        server.Start();
        var cli = Launcher();

        async Task<(int Code, string Out, string Err)> FromHarness(string grantName, string before)
        {
            using var grant = grants.Issue(CouncilRoles.Research, grantName);
            var psi = Plain("/bin/sh", "-c", before + "exec \"$0\" \"$@\"", cli[0], cli[1], "data", "list", "--json");
            psi.Environment["TRADEAGENT_PIPE"] = pipe;
            psi.Environment[AgentGrants.Variable] = grant.Token;
            using var h = ProcessContainment.StartHarness(psi, ProcessContainment.Prepare(Launcher()), Launcher(), attemptId: grantName,
                cgroups: rows.Cgroups);
            Assert.NotNull(h.Container);
            Assert.True(Directory.Exists(h.Cgroup!.Cell), "the attempt's cell leaf was not made beside the harness");
            Assert.True(grants.Bind(grant.Token, h.Container!));
            var ran = await Finish(h);
            return (ran.Code, ran.Out, ran.Err);
        }

        var own = await FromHarness("harness-own", "");
        Assert.True(own.Code == 0, $"the harness's own process, in its harness leaf, was not served (exit {own.Code}): {own.Out} {own.Err}");
        using (var doc = JsonDocument.Parse(own.Out)) Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), own.Out);

        const string intoCell = "cg=/sys/fs/cgroup$(sed -n 's/^0:://p' /proc/self/cgroup); " +
                                "[ \"${cg##*/}\" = harness ] || exit 8; echo $$ > \"${cg%/harness}/cell/cgroup.procs\" || exit 9; ";
        var moved = await FromHarness("harness-moved", intoCell);
        Assert.True(moved.Code is not (0 or 8 or 9),
            $"a process in the attempt's cell leaf presented the harness's grant and was served (exit {moved.Code}): {moved.Out} {moved.Err}");
        Assert.Contains("IPC_UNAUTHENTICATED", moved.Out + moved.Err);
        Assert.Contains("cgroup", moved.Out + moved.Err);
    }

    /// <summary>
    /// ITEM 3: THE SUPERVISOR JOINS AN EMPTY <c>harness</c> OR <c>cell</c> LEAF OF AN ATTEMPT, AND NOTHING ELSE BENEATH ONE. A leaf of
    /// another name, a leaf of a directory that is not an attempt's, and a leaf that already holds a process are refused with exit 126
    /// and nothing started.
    /// </summary>
    [LinuxFact]
    public async Task The_supervisor_joins_an_empty_harness_or_cell_leaf_of_an_attempt_and_nothing_else()
    {
        PlatformIsACells();
        using var rows = Rows();
        var attempt = rows.Cgroups.Create("leaves");
        attempt.MakeLeaves();
        var other = Path.Combine(attempt.Dir, "other");
        Directory.CreateDirectory(other);
        var notAttempt = Path.Combine(Path.GetDirectoryName(attempt.Dir)!, "plain-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(notAttempt, AttemptCgroups.CellLeaf));
        try
        {
            async Task<Ran> Join(string dir)
            {
                var cli = Launcher();
                var psi = Plain(cli[0], cli[1], ContainedLaunch.Flag, ContainedLaunch.CgroupFlag, dir, "/bin/sh", "-c",
                    "sed -n 's/^0:://p' /proc/self/cgroup");
                using var p = System.Diagnostics.Process.Start(psi)!;
                p.StandardInput.Close();
                var so = p.StandardOutput.ReadToEndAsync();
                var se = p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
                return new Ran(p.ExitCode, await so, await se, "join");
            }
            foreach (var leaf in new[] { attempt.Harness, attempt.Cell })
            {
                var ran = await Join(leaf);
                Assert.True(ran.Code == 0, $"joining {leaf}: exit {ran.Code} {ran.Err}");
                Assert.Equal(LineOf(leaf)[3..], ran.Out.Trim());
            }
            foreach (var refused in new[] { other, Path.Combine(notAttempt, AttemptCgroups.CellLeaf) })
            {
                var ran = await Join(refused);
                Assert.True(ran.Code == ContainedLaunch.CgroupRefused, $"{refused} was joined (exit {ran.Code}): {ran.Out} {ran.Err}");
                Assert.Contains("nothing was started", ran.Err);
            }
        }
        finally
        {
            attempt.Kill(TimeSpan.FromSeconds(3));
            attempt.Remove();
            try { Directory.Delete(Path.Combine(notAttempt, AttemptCgroups.CellLeaf)); Directory.Delete(notAttempt); } catch (IOException) { }
        }
    }
}

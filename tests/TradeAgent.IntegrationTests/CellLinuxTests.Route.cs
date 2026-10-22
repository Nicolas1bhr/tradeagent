using TradeAgent.Core;
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
}

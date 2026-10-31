using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.AgentRuntime;

/// <summary>One row of the host benchmark: its name, whether it held, and what was seen.</summary>
public sealed record BenchRow(string Name, bool Held, string Seen)
{
    /// <summary>
    /// A GAP ROW (fix pass 1, R4): a reach the cell HAS, measured and recorded as such in the proof — never a pass. It holds when the
    /// measurement shows the gap as stated; one that shows otherwise means the profile no longer says what the kernel does, and no
    /// proof is written.
    /// </summary>
    public bool Gap { get; init; }
}

/// <summary>What one benchmark pass found, and the proof it wrote (null when any row failed, or none could be bound).</summary>
public sealed record BenchResult(IReadOnlyList<BenchRow> Rows, string ProfileHash, long? ProofId, string Sentence);

/// <summary>
/// THE HOST BENCHMARK THAT WRITES <c>cell_proof</c> (<c>U-cell-linux-c</c> item 5; ORCHESTRATION § 2.8 Acceptance; the survey's Q-a).
/// The app runs it ITSELF at every start on Linux — no press, no flag, no setting — through the product's own path
/// (<see cref="ProcessContainment.Start(ProcessStartInfo, CellPolicy, IReadOnlyList{string}?, CellPlatform?, AttemptCgroups?, string?)"/>
/// under the delegated cgroup), inside the presence register (a cell's processes are agent processes). Each row launches a real
/// cell and shows a denial or a parity from inside it; the last is route (a)'s leg with no model — the pinned
/// <c>codex exec-server --listen stdio</c> answers <c>initialize</c>, an EMPTY <c>environmentConfig/read</c>, and <c>process/start</c>
/// of <c>pwd</c> printing the workspace. Every row held ⇒ ONE proof row, bound to machine, full kernel release, boot and build
/// (<see cref="CellProofStore.Record"/>); any row failed ⇒ nothing is written, and no position beyond the legacy pair activates.
///
/// <para><b>The profile</b> is a SHA-256 over what the kernel is told and what was measured — provider, platform, ABI, security
/// modules, the seccomp program and the handled Landlock rights, the codex pin, the egress classes the rows proved and the row names
/// — and every cell launch recomputes it (<see cref="Profile"/>): a mismatch refuses the launch and revokes the proof.</para>
/// </summary>
public static class CellBenchmark
{
    /// <summary>
    /// THE EGRESS CLASSES THIS BENCHMARK'S ROWS PROVE (L4; the owner's answer of 2026-10-21, "Web (443) for every executor"): <c>none</c>
    /// and <c>web</c>. The proof names them, and a launch whose cell names another class is refused (<see cref="RouteA.Admission"/>).
    /// </summary>
    public static readonly IReadOnlyList<string> ProvedEgress = [CellEgress.None.Name, CellEgress.Web.Name];

    /// <summary>The rows, by name, in the order they run: the profile names them, so a build that changes the list holds no proof.</summary>
    public static readonly IReadOnlyList<string> RowNames =
    [
        "env-is-the-policys", "state-unreadable", "state-unwritable", "login-unreadable", "inbox-uncreatable", "no-egress",
        "web-443-granted", "web-nothing-else", "gap-web-loopback-443", "gap-web-lan-443",
        "ipc-denied", "signals-environ-denied", "io-uring-denied", "cgroup-escape-denied", "descendants-cancelled",
        "tmp-unchanged", "route-a-exec-server"
    ];

    /// <summary>AF_UNIX from inside a cell: a socket made at all, then a connect to a pathname socket and to an abstract one.</summary>
    const string IpcProbe = """
        import socket, sys
        try:
            socket.socket(socket.AF_UNIX, socket.SOCK_STREAM).close(); print("socket-open")
        except OSError as e:
            print(f"socket-denied errno={e.errno}")
        for name, addr in (("path", sys.argv[1]), ("abstract", "\0" + sys.argv[2])):
            try:
                s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM); s.connect(addr); print(name + "-connected"); s.close()
            except OSError as e:
                print(f"{name}-denied errno={e.errno}")
        """;

    /// <summary>Another process from inside a cell: its environment read, a signal (0: none delivered) and a trace attach, each tried.</summary>
    const string ProcsProbe = """
        import ctypes, os, sys
        libc = ctypes.CDLL(None, use_errno=True)
        pid = int(sys.argv[1])
        try:
            open(f"/proc/{pid}/environ", "rb").read(); print("environ-open")
        except OSError as e:
            print(f"environ-denied errno={e.errno}")
        try:
            os.kill(pid, 0); print("signal-sent")
        except OSError as e:
            print(f"signal-denied errno={e.errno}")
        if libc.ptrace(16, pid, 0, 0) == 0:
            print("ptrace-attached"); libc.ptrace(17, pid, 0, 0)
        else:
            print(f"ptrace-denied errno={ctypes.get_errno()}")
        """;

    /// <summary><c>io_uring_setup</c> (syscall 425 on x86-64 and arm64 alike) from inside a cell.</summary>
    const string UringProbe = """
        import ctypes
        libc = ctypes.CDLL(None, use_errno=True)
        params = ctypes.create_string_buffer(120)
        fd = libc.syscall(425, 4, params)
        print("io_uring-open" if fd >= 0 else f"io_uring-denied errno={ctypes.get_errno()}")
        """;

    /// <summary>A double fork with <c>setsid</c> between: the grandchild leaves the session, writes its pid, and waits to be ended.</summary>
    const string DaemonProbe = """
        import os, sys, time
        out = sys.argv[1]
        if os.fork() == 0:
            os.setsid()
            if os.fork() == 0:
                open(out + ".tmp", "w").write(str(os.getpid())); os.rename(out + ".tmp", out)
                time.sleep(300)
                os._exit(0)
            os._exit(0)
        os.wait()
        time.sleep(300)
        """;

    /// <summary>Where the rows that need a socket, a syscall or a fork find their interpreter: absent, those rows cannot hold.</summary>
    const string Python = "/usr/bin/python3";

    /// <summary>
    /// ONE PROBE OF THE NETWORK FROM INSIDE A CELL, by tag: <c>tag=host:port</c> connects (a tag starting <c>fast</c> sends with
    /// <c>MSG_FASTOPEN</c> instead), <c>udp=host:port</c> sends a datagram, <c>bind=</c> and <c>listen=</c> try to serve. Each prints
    /// <c>tag-connected|sent|open</c>, <c>tag-denied</c> (the kernel's rules), <c>tag-reached</c> (past the rules, refused by the
    /// peer), <c>tag-absent</c> (no such address here) or <c>tag-unclear</c>, with the errno.
    /// </summary>
    const string NetProbe = """
        import errno, socket, sys
        def say(tag, e):
            if e.errno in (errno.EACCES, errno.EPERM): print(f"{tag}-denied errno={e.errno}")
            elif e.errno == errno.ECONNREFUSED: print(f"{tag}-reached errno={e.errno}")
            elif e.errno in (errno.EADDRNOTAVAIL, errno.ENETUNREACH, errno.EAFNOSUPPORT): print(f"{tag}-absent errno={e.errno}")
            else: print(f"{tag}-unclear errno={e.errno}")
        for spec in sys.argv[1:]:
            tag, rest = spec.split("=", 1)
            try:
                if tag == "bind":
                    socket.socket(socket.AF_INET, socket.SOCK_STREAM).bind(("127.0.0.1", 0)); print("bind-open")
                elif tag == "listen":
                    socket.socket(socket.AF_INET, socket.SOCK_STREAM).listen(1); print("listen-open")
                else:
                    host, port = rest.rsplit(":", 1)
                    host = host.strip("[]")
                    if tag == "udp":
                        socket.socket(socket.AF_INET, socket.SOCK_DGRAM).sendto(b"x", (host, int(port))); print("udp-sent")
                        continue
                    s = socket.socket(socket.AF_INET6 if ":" in host else socket.AF_INET, socket.SOCK_STREAM)
                    s.settimeout(5)
                    if tag.startswith("fast"):
                        s.sendto(b"x", socket.MSG_FASTOPEN, (host, int(port))); print(tag + "-sent")
                    else:
                        s.connect((host, int(port))); print(tag + "-connected")
                    s.close()
            except OSError as e:
                say(tag, e)
        """;

    /// <summary>Whether a connect got past the kernel's rules: it connected, or the peer refused it.</summary>
    static bool Through(string output, string tag) =>
        output.Contains(tag + "-connected", StringComparison.Ordinal) || output.Contains(tag + "-reached", StringComparison.Ordinal);

    /// <summary>This host's own IPv4 address on an interface that is up, besides loopback — a LAN address a <c>web</c> cell reaches on 443.</summary>
    static IPAddress? OwnLanAddress()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(u => u.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { return null; }
    }

    /// <summary>THE PROFILE a proof of <paramref name="provider"/> is measured under on this platform with codex <paramref name="codexPin"/>.</summary>
    public static string Profile(string provider, CellPlatform platform, string codexPin) => platform.Windows
        ? WindowsProfile(provider, platform, codexPin)
        : Sha256Hex.Of(string.Join('\n',
        [
            "provider " + provider, "platform " + CellProofStore.ThisPlatform, "architecture " + platform.Architecture,
            "landlock-abi " + platform.LandlockAbi, "lsm " + (platform.Lsm ?? "unknown"),
            CellNative.KernelSurface(RuntimeInformation.ProcessArchitecture, platform.LandlockAbi),
            "codex " + codexPin, "egress " + string.Join(',', ProvedEgress), "rows " + string.Join(',', RowNames)
        ]));

    /// <summary>
    /// THE WINDOWS ROWS, by name, in the order they run (<c>U-cell-windows</c>; the survey's row set, its own and never the Linux rows):
    /// the profile names them, so a build that changes the list holds no proof.
    /// </summary>

    /// <summary>The Windows rows every proof needs: any one failing, nothing is written.</summary>
    public static readonly IReadOnlyList<string> WindowsCoreRows =
    [
        "env-is-the-policys", "state-unreadable", "state-unwritable", "login-unreadable", "inbox-uncreatable", "no-egress",
        "loopback-denied", "own-lan-address-denied", "pipe-denied", "processes-denied", "descendants-cancelled", "slot-leaves-nothing",
        "route-a-exec-server"
    ];

    /// <summary>
    /// The Windows rows that decide whether the proof names egress <c>web</c> (the egress decision of 2026-10-30): both held — 443 reached
    /// and every other port refused, which only the firewall rule makes true — and the proof names <c>none</c> and <c>web</c>; otherwise
    /// <c>none</c> alone, and a <c>web</c> launch runs under <c>none</c> (<see cref="RouteA.AdmissionOn"/>).
    /// </summary>
    public static readonly IReadOnlyList<string> WindowsWebRows = ["web-443-granted", "web-nothing-else"];

    /// <summary>
    /// Every Windows row IN THE ORDER THEY RUN: <c>slot-leaves-nothing</c> runs LAST, after the web launches, so a grant a web cell leaves
    /// is caught by it too. Declared after both sets so neither is null when it is built.
    /// </summary>
    public static readonly IReadOnlyList<string> WindowsRowNames =
    [
        .. WindowsCoreRows.Where(n => n != "slot-leaves-nothing"), .. WindowsWebRows, "slot-leaves-nothing"
    ];

    /// <summary>
    /// THE WINDOWS PROFILE: provider, platform, processor and build, the cell's mechanism as the stage sets it (the AppContainer, the job's
    /// limits and UI restrictions, the window station, the slot count), the codex pin, the egress classes and the Windows rows.
    /// </summary>
    static string WindowsProfile(string provider, CellPlatform platform, string codexPin) =>
        Sha256Hex.Of(string.Join('\n',
        [
            "provider " + provider, "platform " + CellProofStore.ThisPlatform, "architecture " + platform.Architecture,
            "windows-build " + platform.WindowsBuild,
            "mechanism appcontainer+job(kill-on-close,die-on-unhandled-exception,no-breakaway,ui-0xFF)+own-window-station+handle-list;slots " + WindowsCellSlots.Count,
            "codex " + codexPin, "egress " + string.Join(',', ProvedEgress), "rows " + string.Join(',', WindowsRowNames)
        ]));

    /// <summary>
    /// RUNS THE BENCHMARK and writes a proof only when every row held. <paramref name="codex"/> is the pinned codex this host runs
    /// (null: not installed — the route (a) leg fails and nothing is written). Never throws: a failure is a row that did not hold.
    /// </summary>
    public static async Task<BenchResult> RunAsync(Database db, string? codex, string codexPin, AttemptCgroups cgroups,
        IReadOnlyList<string>? launcher = null, CancellationToken ct = default)
    {
        var platform = CellPlatform.Read();
        var profile = Profile(RouteA.Provider, platform, codexPin);
        var rows = new List<BenchRow>();
        if (platform.Refusal() is { } refusal)
            return new BenchResult([new BenchRow("platform", false, refusal)], profile, null, "no cell proof: " + refusal);
        if (!cgroups.Held)
            return new BenchResult([new BenchRow("cgroup", false, cgroups.Refusal ?? "no delegated root")], profile, null,
                "no cell proof: no delegated cgroup (Delegate=yes)");

        var root = Path.Combine(Paths.Home, "cell-bench", Guid.NewGuid().ToString("n")[..12]);
        var ws = Path.Combine(root, "ws");
        var codexHome = Path.Combine(root, "codex");
        var home = Path.Combine(root, "home");
        foreach (var d in new[] { ws, codexHome, home }) Directory.CreateDirectory(d);
        try
        {
            var policy = codex is not null
                ? RouteA.Policy(ws, codex, RouteA.CodexRoot(codex), codexHome, home)
                : new CellPolicy(ws, [codexHome, home], CellPolicy.SystemRoots,
                    [new("PATH", "/usr/local/bin:/usr/bin:/bin"), new("HOME", home), new("LANG", "C.UTF-8"), .. CellPolicy.ToolEnvironment(ws)]);

            async Task<(int Code, string Out)> Run(CellPolicy under, string program, params string[] args)
            {
                var psi = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var held = ProcessContainment.Start(psi, under, launcher, cgroups: cgroups);
                held.Process.StandardInput.Close();
                var so = held.Process.StandardOutput.ReadToEndAsync(ct);
                var se = held.Process.StandardError.ReadToEndAsync(ct);
                await held.Process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(60), ct);
                return (held.Process.ExitCode, (await so) + (await se));
            }

            Task<(int Code, string Out)> Sh(string script, string shell = "/bin/sh") => Run(policy, shell, "-c", script);

            // A ROW THAT NEEDS AN INTERPRETER: its script written into the cell's workspace and run there. No python3: it cannot hold.
            Task<(int Code, string Out)> Py(CellPolicy under, string name, string script, params string[] args)
            {
                if (!File.Exists(Python)) throw new InvalidOperationException($"python3 is not at {Python} on this host, so the row could not be tried");
                var file = Path.Combine(ws, name + ".py");
                File.WriteAllText(file, script);
                return Run(under, Python, ["-I", file, .. args]);
            }

            async Task Row(string name, Func<Task<(bool, string)>> body, bool gap = false)
            {
                try
                {
                    var (ok, seen) = await body();
                    rows.Add(new BenchRow(name, ok, seen) { Gap = gap });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    rows.Add(new BenchRow(name, false, ex.Message) { Gap = gap });
                }
            }

            string? cellPid = null;
            await Row("env-is-the-policys", async () =>
            {
                var (code, output) = await Sh("echo cell-pid=$$; env");
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                cellPid = lines.FirstOrDefault(l => l.StartsWith("cell-pid=", StringComparison.Ordinal))?["cell-pid=".Length..];
                var seen = lines.Where(l => !l.StartsWith("cell-pid=", StringComparison.Ordinal) && l.Contains('=')
                                            && l.Split('=')[0] is not ("PWD" or "SHLVL" or "_" or "OLDPWD")).Order(StringComparer.Ordinal).ToList();
                var want = policy.Environment.Select(e => e.Key + "=" + e.Value).Order(StringComparer.Ordinal).ToList();
                return (code == 0 && seen.SequenceEqual(want), $"exit {code}; {seen.Count} variables, the policy names {want.Count}");
            });
            static string Denied(string path) => $"if ls '{path}' >/dev/null 2>&1; then echo OPEN; else echo DENIED; fi";
            await Row("state-unreadable", async () =>
            {
                var (_, o) = await Sh(Denied(Paths.State) + $"; if cat '{Paths.DatabaseFile}' >/dev/null 2>&1; then echo OPEN; else echo DENIED; fi");
                return (o.Split('\n').Count(l => l == "DENIED") == 2 && !o.Contains("OPEN", StringComparison.Ordinal), o.Trim());
            });
            await Row("state-unwritable", async () =>
            {
                // A FILE OF state/ THE APP PUTS THERE, appended to from the cell, and a new one created beside it: both denied, the
                // file's bytes unchanged and nothing new — the ledger itself is never the target, so a failing cell harms nothing.
                var sentinel = Path.Combine(Paths.State, ".cell-bench-" + Guid.NewGuid().ToString("n")[..8]);
                var made = sentinel + "-new";
                File.WriteAllText(sentinel, "sentinel\n");
                try
                {
                    var (_, o) = await Sh($"if {{ echo x >> '{sentinel}'; }} 2>/dev/null; then echo APPEND-OPEN; else echo APPEND-DENIED; fi; " +
                                          $"if touch '{made}' 2>/dev/null; then echo CREATE-OPEN; else echo CREATE-DENIED; fi");
                    var same = File.ReadAllText(sentinel) == "sentinel\n";
                    var created = File.Exists(made);
                    return (o.Contains("APPEND-DENIED", StringComparison.Ordinal) && o.Contains("CREATE-DENIED", StringComparison.Ordinal) && same && !created,
                        $"{o.Trim().Replace('\n', ' ')}; the file {(same ? "unchanged" : "CHANGED")}, {(created ? "a new file WAS made" : "nothing made")}");
                }
                finally
                {
                    File.Delete(sentinel);
                    File.Delete(made);
                }
            });
            await Row("login-unreadable", async () =>
            {
                var (_, o) = await Sh(Denied(Paths.SeatHome));
                return (o.Trim() == "DENIED", o.Trim());
            });
            await Row("inbox-uncreatable", async () =>
            {
                var probe = Path.Combine(Paths.Inbox, ".cell-bench-" + Guid.NewGuid().ToString("n")[..8]);
                var (_, o) = await Sh($"if touch '{probe}' 2>/dev/null; then echo OPEN; else echo DENIED; fi");
                var made = File.Exists(probe);
                if (made) File.Delete(probe);
                return (o.Trim() == "DENIED" && !made, o.Trim());
            });
            await Row("no-egress", async () =>
            {
                if (!File.Exists("/bin/bash")) return (false, "/bin/bash is not on this host, so the connect could not be tried");
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                try
                {
                    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    var (_, o) = await Sh($"if (exec 3<>/dev/tcp/127.0.0.1/{port}) 2>/dev/null; then echo OPEN; else echo DENIED; fi", "/bin/bash");
                    return (o.Trim() == "DENIED", $"loopback port {port}: {o.Trim()}");
                }
                finally { listener.Stop(); }
            });

            // EGRESS `web` (fix pass 1, R4; U-cell-linux-d's rows): the same cell under class `web` — TCP 443 let through by the kernel's
            // rules while any other port, a fast-open send, a datagram, a bind and a listen are denied; and the reach that comes with it,
            // loopback and the LAN on 443 (Landlock's port rules are port-only), measured and recorded as GAP rows. 443 is measured by a
            // connect the peer answers or refuses — nothing here can listen on it — and a denied connect says EACCES, at once.
            var web = new CellPolicy(policy.Workspace, policy.ReadOnly, policy.Executable, policy.Environment, CellEgress.Web);
            await Row("web-443-granted", async () =>
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                try
                {
                    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    var (_, o) = await Py(web, "web-granted", NetProbe, "https=127.0.0.1:443", $"other=127.0.0.1:{port}");
                    var reached = listener.Pending();
                    return (Through(o, "https") && o.Contains("other-denied", StringComparison.Ordinal) && !reached,
                        $"443 {(Through(o, "https") ? "let through" : "NOT let through")}, another port {(o.Contains("other-denied", StringComparison.Ordinal) ? "denied" : "NOT denied")}" +
                        $"{(reached ? ", and its listener was reached" : "")}: {o.Trim().Replace('\n', ' ')}");
                }
                finally { listener.Stop(); }
            });
            await Row("web-nothing-else", async () =>
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                try
                {
                    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    var udp = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
                    var (_, o) = await Py(web, "web-else", NetProbe, $"fastother=127.0.0.1:{port}", $"udp=127.0.0.1:{udp}", "bind=", "listen=");
                    var denied = new[] { "fastother", "udp", "bind", "listen" }.All(t => o.Contains(t + "-denied", StringComparison.Ordinal));
                    var arrived = listener.Pending() || receiver.Available > 0;
                    return (denied && !arrived, $"{o.Trim().Replace('\n', ' ')}{(arrived ? "; something ARRIVED outside" : "; nothing arrived outside")}");
                }
                finally { listener.Stop(); }
            });
            await Row("gap-web-loopback-443", async () =>
            {
                var (_, o) = await Py(web, "web-loopback", NetProbe, "v4=127.0.0.1:443", "v6=[::1]:443");
                var v6 = Through(o, "v6") || o.Contains("v6-absent", StringComparison.Ordinal);
                return (Through(o, "v4") && v6,
                    $"GAP: a web cell reaches this host's own loopback on 443 (a service listening there is reachable from the cell): {o.Trim().Replace('\n', ' ')}");
            }, gap: true);
            await Row("gap-web-lan-443", async () =>
            {
                if (OwnLanAddress() is not { } lan)
                    return (true, "GAP: this host has no LAN address to measure from; the kernel's port rule names no address, so a web cell reaches any host of the LAN on 443");
                var (_, o) = await Py(web, "web-lan", NetProbe, $"lan={lan}:443");
                return (Through(o, "lan"),
                    "GAP: a web cell reaches this host's own LAN address on 443, and so any host of the LAN on 443 (the port rule names no address): " +
                    o.Trim().Replace(lan.ToString(), "<own address>", StringComparison.Ordinal).Replace('\n', ' '));
            }, gap: true);

            // THE REST OF THE SURVEY'S Q-a (fix pass 1, R1): IPC, other processes, io_uring, the cgroup tree and descendants.
            await Row("ipc-denied", async () =>
            {
                var tag = Guid.NewGuid().ToString("n")[..12];
                var path = Path.Combine(Path.GetTempPath(), "ta-cell-bench-" + tag + ".sock");
                using var named = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                using var hidden = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    named.Bind(new UnixDomainSocketEndPoint(path));
                    named.Listen(4);
                    hidden.Bind(new UnixDomainSocketEndPoint("\0ta-cell-bench-" + tag));
                    hidden.Listen(4);
                    var (_, o) = await Py(policy, "ipc", IpcProbe, path, "ta-cell-bench-" + tag);
                    var denied = new[] { "socket", "path", "abstract" }.All(t => o.Contains(t + "-denied", StringComparison.Ordinal));
                    var arrived = named.Poll(0, SelectMode.SelectRead) || hidden.Poll(0, SelectMode.SelectRead);
                    return (denied && !arrived, $"{o.Trim().Replace('\n', ' ')}{(arrived ? "; a listener WAS reached" : "")}");
                }
                finally { File.Delete(path); }
            });
            await Row("signals-environ-denied", async () =>
            {
                var app = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var (_, o) = await Py(policy, "procs", ProcsProbe, app);
                return (new[] { "environ", "signal", "ptrace" }.All(t => o.Contains(t + "-denied", StringComparison.Ordinal)),
                    $"the app's own process: {o.Trim().Replace('\n', ' ')}");
            });
            await Row("io-uring-denied", async () =>
            {
                var (_, o) = await Py(policy, "uring", UringProbe);
                return (o.Contains("io_uring-denied", StringComparison.Ordinal), o.Trim());
            });
            await Row("cgroup-escape-denied", async () =>
            {
                // A LEAF BESIDE THE ATTEMPTS, made for this row: a cell that could write its cgroup.procs would have left its attempt,
                // and cgroup.kill with it.
                var escape = Path.Combine(cgroups.Root!, "bench-escape-" + Guid.NewGuid().ToString("n")[..8]);
                Directory.CreateDirectory(escape);
                try
                {
                    var (_, o) = await Sh($"if {{ echo $$ > '{escape}/cgroup.procs'; }} 2>/dev/null; then echo MOVED; else echo MOVE-REFUSED; fi; " +
                                          "echo CG=$(sed -n 's/^0:://p' /proc/self/cgroup)");
                    // WHERE IT IS AFTER THE TRY, read by the cell itself: still its attempt's, never the leaf it was refused.
                    var after = o.Split('\n').FirstOrDefault(l => l.StartsWith("CG=", StringComparison.Ordinal))?[3..] ?? "";
                    var stayed = after.Contains("/" + AttemptCgroups.AttemptPrefix, StringComparison.Ordinal)
                                 && !after.Contains(Path.GetFileName(escape), StringComparison.Ordinal);
                    return (o.Contains("MOVE-REFUSED", StringComparison.Ordinal) && stayed,
                        $"{(o.Contains("MOVE-REFUSED", StringComparison.Ordinal) ? "refused" : "MOVED")}; {(stayed ? "still in its attempt's cgroup" : "NOT in its attempt's cgroup: " + after)}");
                }
                finally
                {
                    for (var i = 0; i < 100; i++)
                        try { Directory.Delete(escape); break; }
                        catch (IOException) { await Task.Delay(20, CancellationToken.None); }
                }
            });
            await Row("descendants-cancelled", async () =>
            {
                if (!File.Exists(Python)) return (false, $"python3 is not at {Python} on this host, so the row could not be tried");
                var probe = Path.Combine(ws, "daemon.py");
                var pidFile = Path.Combine(ws, "daemon.pid");
                File.WriteAllText(probe, DaemonProbe);
                var psi = new ProcessStartInfo(Python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in new[] { "-I", probe, pidFile }) psi.ArgumentList.Add(a);
                using var held = ProcessContainment.Start(psi, policy, launcher, cgroups: cgroups);
                held.Process.StandardInput.Close();
                for (var i = 0; i < 300 && !File.Exists(pidFile); i++) await Task.Delay(100, ct);
                if (!File.Exists(pidFile)) { held.Kill(); return (false, "the setsid'd daemon never wrote its pid"); }
                var pid = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
                if (ProcessTable.Read(pid) is not { Zombie: false } daemon) { held.Kill(); return (false, $"the daemon {pid} was not running"); }
                var leftSession = daemon.Session != held.Process.Id;
                held.Kill();
                var gone = ProcessTable.Read(pid) is not { Zombie: false } after || after.Start != daemon.Start;
                for (var i = 0; i < 100 && !gone; i++)
                {
                    await Task.Delay(20, ct);
                    gone = ProcessTable.Read(pid) is not { Zombie: false } later || later.Start != daemon.Start;
                }
                if (!gone) Posix.Kill(pid);
                return (leftSession && gone && held.Ended is { Ended: true },
                    $"a daemon that {(leftSession ? "left" : "did NOT leave")} the cell's session {(gone ? "was ended" : "OUTLIVED")} the attempt's cancel " +
                    $"({(held.Ended is { } end ? end.Ended ? "the teardown ended everything" : end.Sentence : "no teardown said anything")})");
            });

            await Row("tmp-unchanged", () =>
            {
                if (cellPid is null) return Task.FromResult((false, "the first row printed no pid"));
                var left = Directory.EnumerateFileSystemEntries("/tmp").Select(Path.GetFileName)
                    .Where(n => n!.StartsWith("dotnet-diagnostic-" + cellPid + "-", StringComparison.Ordinal)
                                || n.StartsWith("clr-debug-pipe-" + cellPid + "-", StringComparison.Ordinal)).ToList();
                return Task.FromResult((left.Count == 0, left.Count == 0 ? $"nothing of pid {cellPid}" : string.Join(", ", left)));
            });
            await Row("route-a-exec-server", async () =>
            {
                if (codex is null) return (false, "codex is not installed, so route (a) could not be measured");
                var psi = new ProcessStartInfo(codex) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in new[] { "exec-server", "--listen", "stdio" }) psi.ArgumentList.Add(a);
                using var held = ProcessContainment.Start(psi, policy, launcher, cgroups: cgroups);
                using var es = new ExecServerClient(held.Process);
                if (await es.CallAsync("initialize", new { clientName = "codex-environment", resumeSessionId = (string?)null }, ct) is not { Ok: true } init)
                    return (false, "initialize: " + es.Last);
                es.Notify("initialized", new { });
                var config = await es.CallAsync("environmentConfig/read", new
                {
                    cwd = new Uri(ws).AbsoluteUri, configPaths = new[] { new[] { "mcp_servers" } }, requirementsPaths = new[] { new[] { "mcp_servers" } }
                }, ct);
                var layers = config is { Ok: true } c && c.Result.TryGetProperty("config", out var cfg) && cfg.TryGetProperty("layers", out var l)
                    ? l.GetArrayLength() : -1;
                var (exit, output) = await es.RunAsync(ws, ["/bin/sh", "-c", "pwd"], ct);
                var parity = output.Split('\n')[0] == ws;
                return (layers == 0 && exit == 0 && parity, $"initialize ok; environmentConfig/read {layers} layer(s); pwd exit {exit} {(parity ? "= the workspace" : "≠ the workspace: " + output.Trim())}");
            });
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* swept later */ }
        }

        var held = rows.Select(r => r.Name).SequenceEqual(RowNames) && rows.All(r => r.Held);
        long? proof = null;
        if (held)
        {
            try { proof = new CellProofStore(db).Record(RouteA.Provider, profile, ProvedEgress, [.. rows.Select(r => new ProofRow(r.Name, r.Gap, r.Seen))]); }
            catch (Exception ex) when (ex is TradeAgentException or Microsoft.Data.Sqlite.SqliteException)
            {
                return new BenchResult(rows, profile, null, "every row held, and no proof could be written: " + ex.Message);
            }
        }
        var sentence = held
            ? $"cell proof {proof} written for {RouteA.Provider} ({platform.Summary}), egress {string.Join(" and ", ProvedEgress)}: " +
              $"{string.Join(", ", rows.Where(r => !r.Gap).Select(r => r.Name))} held; GAP recorded: {string.Join(", ", rows.Where(r => r.Gap).Select(r => r.Name))}"
            : "no cell proof: " + string.Join("; ", rows.Where(r => !r.Held).Select(r => $"{r.Name} did not hold ({r.Seen})"));
        return new BenchResult(rows, profile, proof, sentence);
    }
}

/// <summary>
/// A JSON-RPC CLIENT FOR <c>codex exec-server --listen stdio</c> as the harness drives it (newline-delimited, responses by id,
/// notifications kept in order) — the benchmark's route (a) leg. Never sends anything but what the caller asks.
/// </summary>
public sealed class ExecServerClient : IDisposable
{
    readonly TextWriter _in;
    readonly Action _end;
    readonly List<JsonElement> _seen = [];
    readonly StringBuilder _stderr = new();
    readonly Task _pump;
    int _id;

    public ExecServerClient(Process process)
        : this(process.StandardInput, process.StandardOutput, process.StandardError, () => process.WaitForExit(10_000)) { }

    /// <summary>
    /// Over any three streams (a Windows cell's pipes, <c>U-cell-windows</c>): <paramref name="end"/> runs on dispose, after the input
    /// is closed, to wait for or end the server.
    /// </summary>
    public ExecServerClient(TextWriter input, TextReader output, TextReader error, Action end)
    {
        _in = input;
        _end = end;
        _pump = Task.Run(async () =>
        {
            while (await output.ReadLineAsync() is { } line)
                try { lock (_seen) _seen.Add(JsonDocument.Parse(line).RootElement.Clone()); }
                catch (JsonException) { /* not a message */ }
        });
        _ = Task.Run(async () =>
        {
            while (await error.ReadLineAsync() is { } line)
                lock (_stderr) if (_stderr.Length < 16_384) _stderr.Append(line).Append('\n');
        });
    }

    /// <summary>What the server wrote to its standard error so far (the first 16 KiB).</summary>
    public string Stderr { get { lock (_stderr) return _stderr.ToString(); } }

    /// <summary>The last error or non-answer, in words.</summary>
    public string Last { get; private set; } = "";

    void Send(object message)
    {
        _in.WriteLine(JsonSerializer.Serialize(message));
        _in.Flush();
    }

    public void Notify(string method, object @params) => Send(new { method, @params });

    public sealed record Answer(bool Ok, JsonElement Result);

    public async Task<Answer?> CallAsync(string method, object @params, CancellationToken ct, int seconds = 30)
    {
        var id = Interlocked.Increment(ref _id);
        try { Send(new { id, method, @params }); }
        catch (IOException ex) { Last = ex.Message; return null; }
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            lock (_seen)
                foreach (var m in _seen)
                    if (m.TryGetProperty("id", out var mid) && mid.ValueKind == JsonValueKind.Number && mid.GetInt32() == id)
                    {
                        if (m.TryGetProperty("result", out var r)) return new Answer(true, r);
                        Last = m.ToString();
                        return new Answer(false, m);
                    }
            if (_pump.IsCompleted) { Last = "the exec-server closed its output"; return null; }
            await Task.Delay(20, ct);
        }
        Last = $"no answer to {method} in {seconds} s";
        return null;
    }

    /// <summary>
    /// Runs <paramref name="argv"/> through <c>process/start</c> and returns its exit code and output, or (-1, why). <paramref name="pipeStdin"/>:
    /// the child's input a pipe the server holds, not the null device.
    /// </summary>
    public async Task<(int Exit, string Output)> RunAsync(string cwd, string[] argv, CancellationToken ct, bool pipeStdin = false)
    {
        var pid = "bench-" + Guid.NewGuid().ToString("n")[..12];
        var started = await CallAsync("process/start", new Dictionary<string, object?>
        {
            ["processId"] = pid, ["argv"] = argv, ["cwd"] = new Uri(cwd).AbsoluteUri,
            ["envPolicy"] = new { inherit = "all", ignoreDefaultExcludes = true, exclude = Array.Empty<string>(), set = new { }, includeOnly = Array.Empty<string>() },
            ["env"] = new { }, ["tty"] = false, ["pipeStdin"] = pipeStdin, ["arg0"] = null, ["sandbox"] = null, ["enforceManagedNetwork"] = false
        }, ct);
        if (started is not { Ok: true }) return (-1, "process/start: " + Last);
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            var output = new StringBuilder();
            int exit = -1;
            var closed = false;
            lock (_seen)
                foreach (var m in _seen)
                {
                    if (!m.TryGetProperty("method", out var method) || !m.TryGetProperty("params", out var p)
                        || !p.TryGetProperty("processId", out var id) || id.GetString() != pid) continue;
                    switch (method.GetString())
                    {
                        case "process/output": output.Append(Encoding.UTF8.GetString(Convert.FromBase64String(p.GetProperty("chunk").GetString() ?? ""))); break;
                        case "process/exited": exit = p.TryGetProperty("exitCode", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : -1; break;
                        case "process/closed": closed = true; break;
                    }
                }
            if (closed) return (exit, output.ToString());
            await Task.Delay(20, ct);
        }
        return (-1, "no process/closed in 30 s");
    }

    public void Dispose()
    {
        try { _in.Close(); } catch (Exception) { /* closed */ }
        try { _end(); } catch (Exception) { /* gone */ }
    }
}

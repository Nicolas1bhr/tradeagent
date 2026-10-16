using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using TradeAgent.Core;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE LINUX CELL'S NETWORK (<c>U-cell-linux-d</c>, ORCHESTRATION § 2.8: "side-channel claims are limited to the cell's tested network,
/// IPC and file policy, and inter-cell communication is part of the benchmark"): how a cell resolves names — decided by the probe's
/// measurement — and what each egress class reaches, every reach a granted port allows asserted as a recorded gap.
/// </summary>
public partial class CellLinuxTests
{
    readonly ITestOutputHelper _output;

    /// <summary>The probe's table and the rows' evidence go to the test's output (the trx the ubuntu job uploads).</summary>
    public CellLinuxTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// A NAME ONLY systemd-resolved ANSWERS ITSELF (RFC 6761's <c>.localhost</c>, synthesized as 127.0.0.1) and that no
    /// <c>/etc/hosts</c> names: resolving it takes a DNS query to the stub, and no query leaves the runner. The loopback index
    /// listens on 127.0.0.1, so a tool that resolves it reaches the index BY NAME.
    /// </summary>
    const string StubName = "ta-cell-index.localhost";

    /// <summary>Names the stub answers from its own synthesis: the default gateway, the outbound address, the stub itself, and <see cref="StubName"/>.</summary>
    static readonly string[] ProbeNames = ["_gateway", "_outbound", "_localdnsstub", StubName];

    /// <summary>
    /// ITEM 1, THE PROBE THAT DECIDES (seat A's ruling 5). In a cell, outside control first, each name is resolved through glibc
    /// (<c>getent ahosts</c>), Python's <c>getaddrinfo</c> and Node's <c>dns.lookup</c>, and the loopback index is reached by name
    /// with <c>curl</c> and <c>pip download</c> — under (A) datagrams denied, <c>RES_OPTIONS=use-vc</c>, TCP 53 granted and the
    /// resolver's configuration readable, (A0) the same without the resolver's configuration, and (B) a probe-only seccomp program
    /// allowing IPv4/IPv6 UDP; class <c>none</c> beside them. It prints the table and the decision, and FAILS only on a missing
    /// prerequisite: no local stub resolver in <c>/etc/resolv.conf</c>, a tool missing, or a name the stub does not answer outside.
    /// </summary>
    [LinuxFact]
    public async Task A_cell_name_resolution_probe()
    {
        PlatformIsACells();
        var w = new World("probe");
        var resolvConf = File.Exists(CellEgress.ResolverConfiguration) ? File.ReadAllLines(CellEgress.ResolverConfiguration) : [];
        var nameserver = resolvConf.Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("nameserver ", StringComparison.Ordinal));
        var hostsLine = File.Exists("/etc/nsswitch.conf")
            ? File.ReadAllLines("/etc/nsswitch.conf").Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("hosts:", StringComparison.Ordinal))
            : null;
        var resolver = CellNative.RealPath(CellEgress.ResolverConfiguration);
        _output.WriteLine($"probe: {CellPlatform.Read().Summary}");
        _output.WriteLine($"probe: {CellEgress.ResolverConfiguration} -> {resolver ?? "absent"}; {nameserver ?? "no nameserver line"}; nsswitch {hostsLine ?? "no hosts line"}");
        Require(nameserver is not null && nameserver.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1].StartsWith("127.", StringComparison.Ordinal),
            $"a local stub resolver named in {CellEgress.ResolverConfiguration} (found: {nameserver ?? "no nameserver line"})");
        Require(resolver is not null, $"{CellEgress.ResolverConfiguration} resolving to a file");
        foreach (var tool in new[] { "/usr/bin/getent", "/usr/bin/curl" })
            Require(File.Exists(tool), tool);
        var node = Which("node");
        Require(node is not null && CellPolicy.SystemRoots.Any(r => CellPolicy.Within(CellNative.RealPath(node) ?? node, r)),
            $"node on PATH under the cell's program roots (found {node ?? "none"} -> {(node is null ? "" : CellNative.RealPath(node))})");
        var pip = await Outside("/usr/bin/python3", "-I", "-m", "pip", "--version");
        Require(pip.Code == 0, $"pip for /usr/bin/python3 ({pip.Err.Trim()})");
        _output.WriteLine($"probe: node {node} -> {CellNative.RealPath(node!)}; {pip.Out.Trim()}");

        using var index = LoopbackIndex.Start(IPAddress.Loopback);
        var wheel = Wheel.Build("ta-cell-probe", "1.0", "ta_cell_probe", "VALUE = 'from-the-loopback-index'\n");
        index.ServePython(wheel);
        var script = w.Script("resolve.py", ResolveScript);
        string[] args = ["-I", script, string.Join(",", ProbeNames), $"http://{StubName}:{index.Port}", w.Ws];

        var outside = await Outside("/usr/bin/python3", args);
        var rows = new List<(string Mode, Dictionary<string, string> Rows)> { ("outside", Rows(outside)) };
        foreach (var name in ProbeNames)
            Require(rows[0].Rows.GetValueOrDefault($"glibc {name}", "").StartsWith("ok", StringComparison.Ordinal),
                $"the stub answering {name} outside the cell: {rows[0].Rows.GetValueOrDefault($"glibc {name}", outside.Err)}");

        var useVc = new KeyValuePair<string, string>("RES_OPTIONS", "use-vc");
        (string Mode, CellPolicy Policy)[] modes =
        [
            ("none", w.Policy(CellEgress.None)),
            ("A tcp53+use-vc", w.Policy(CellEgress.ForTests([CellEgress.DnsPort, index.Port]), [useVc], [resolver!])),
            ("A0 no-resolv.conf", w.Policy(CellEgress.ForTests([CellEgress.DnsPort, index.Port]), [useVc])),
            ("B datagrams", w.Policy(CellEgress.ForTests([index.Port], CellDns.Datagrams), inputs: [resolver!])),
        ];
        foreach (var (mode, policy) in modes)
        {
            var ran = await InCell(policy, "/usr/bin/python3", args);
            rows.Add((mode, Rows(ran)));
            if (ran.Err.Length > 0) _output.WriteLine($"probe: {mode} stderr: {ran.Err.Trim().Replace('\n', ' ')[..Math.Min(400, ran.Err.Trim().Length)]}");
        }

        var keys = rows[0].Rows.Keys.ToList();
        _output.WriteLine("probe: resolver × mode — " + string.Join(" | ", rows.Select(r => r.Mode)));
        foreach (var key in keys)
            _output.WriteLine($"probe: {key,-28} " + string.Join(" | ", rows.Select(r => r.Rows.GetValueOrDefault(key, "-"))));

        // THE DECISION, BY THE MEASUREMENT: every resolver resolves under (A) each name that class none could NOT resolve (a name
        // answered without a socket — nss-myhostname, /etc/hosts — measures nothing about DNS and is said so).
        var none = rows.Single(r => r.Mode == "none").Rows;
        var a = rows.Single(r => r.Mode.StartsWith("A tcp", StringComparison.Ordinal)).Rows;
        var dnsKeys = keys.Where(k => !none.GetValueOrDefault(k, "fail").StartsWith("ok", StringComparison.Ordinal)).ToList();
        foreach (var k in keys.Except(dnsKeys)) _output.WriteLine($"probe: {k} resolved under class none — answered without DNS, not counted");
        var aAll = dnsKeys.Count > 0 && dnsKeys.All(k => a.GetValueOrDefault(k, "fail").StartsWith("ok", StringComparison.Ordinal));
        var families = new[] { "glibc", "python", "node", "pip" };
        var covered = families.All(f => dnsKeys.Any(k => k.StartsWith(f + " ", StringComparison.Ordinal)));
        _output.WriteLine(aAll && covered
            ? "probe: DECISION — all of glibc/Python/Node/pip resolve under (A): datagrams DENIED (hard); DNS over TCP, port 53 to any host, labelled"
            : $"probe: DECISION — (A) did not resolve everything ({string.Join(", ", dnsKeys.Where(k => !a.GetValueOrDefault(k, "fail").StartsWith("ok", StringComparison.Ordinal)))}; " +
              $"families measured: {string.Join(", ", families.Where(f => dnsKeys.Any(k => k.StartsWith(f + " ", StringComparison.Ordinal))))}): " +
              "UDP OPEN — any host and port — for egress-classed cells, labelled; class none keeps it denied");
        _output.WriteLine($"probe: this build's classes resolve by {CellEgress.Decided}");
    }

    // ---- item 3: the network rows ------------------------------------------------------------------------------------

    /// <summary>
    /// What a cell's TCP and datagram attempts print, one line each: <c>&lt;name&gt;-connected</c> / <c>-sent</c> / <c>-open</c>, or
    /// <c>&lt;name&gt;-denied errno=N</c>. Arguments are <c>name=host:port</c> to connect to; the fixed tries follow them.
    /// </summary>
    const string NetScript = """
        import socket, subprocess, sys
        def family(host):
            return socket.AF_INET6 if ":" in host else socket.AF_INET
        for arg in sys.argv[1:]:
            name, target = arg.split("=", 1)
            host, port = target.rsplit(":", 1)
            host = host.strip("[]")
            try:
                s = socket.socket(family(host), socket.SOCK_STREAM); s.settimeout(10)
                if name.startswith("fastopen"):
                    s.sendto(b"fast", socket.MSG_FASTOPEN, (host, int(port))); print(name + "-sent")
                else:
                    s.connect((host, int(port))); s.sendall(name.encode()); print(name + "-connected")
            except OSError as e:
                print(f"{name}-denied errno={e.errno}")
        try:
            s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.sendto(b"x", ("127.0.0.53", 53)); print("udp-sent")
        except OSError as e:
            print(f"udp-denied errno={e.errno}")
        for name, fam, addr in (("bind", socket.AF_INET, ("127.0.0.1", 0)), ("bind6", socket.AF_INET6, ("::", 0))):
            try:
                s = socket.socket(fam, socket.SOCK_STREAM); s.bind(addr); print(name + "-open")
            except OSError as e:
                print(f"{name}-denied errno={e.errno}")
        try:
            s = socket.socket(socket.AF_INET, socket.SOCK_STREAM); s.listen(8); print(f"listen-open port={s.getsockname()[1]}")
        except OSError as e:
            print(f"listen-denied errno={e.errno}")
        r = subprocess.run(["getent", "ahosts", "_localdnsstub"], capture_output=True, text=True)
        print("dns-resolved" if r.returncode == 0 and r.stdout.strip() else f"dns-failed rc={r.returncode}")
        """;

    /// <summary>A TCP listener outside every cell that counts what connects to it and records the first bytes each connection sends.</summary>
    sealed class Counting : IDisposable
    {
        readonly TcpListener _listener;
        readonly CancellationTokenSource _stop = new();

        public Counting(IPAddress at, int port = 0)
        {
            _listener = new TcpListener(at, port);
            if (at.AddressFamily == AddressFamily.InterNetworkV6) _listener.Server.DualMode = false;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(Loop);
        }

        public int Port { get; }

        public ConcurrentQueue<string> Received { get; } = new();

        async Task Loop()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (Exception) { return; }
                _ = Task.Run(async () =>
                {
                    using (c)
                    {
                        var buf = new byte[64];
                        var n = 0;
                        try { n = await c.GetStream().ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
                        catch (Exception) { /* nothing sent */ }
                        Received.Enqueue(Encoding.ASCII.GetString(buf, 0, n));
                    }
                });
            }
        }

        /// <summary>Waits briefly for queued connections to be read, then returns what arrived.</summary>
        public async Task<string[]> Settled()
        {
            await Task.Delay(500);
            return [.. Received];
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    /// <summary>
    /// ROW (a), A GUARD — AND THE WATCHED MUTANT'S ROW: a cell of class <c>none</c> connects nowhere — not to a listening loopback
    /// port, not to the stub resolver's TCP 53, not by a TCP Fast Open send — makes no datagram socket, binds and listens nowhere and
    /// resolves no name through DNS. The same connects, sends and resolution work from outside first; the outside listener counts
    /// every connection, so a connect the cell's own report missed still shows.
    /// </summary>
    [LinuxFact]
    public async Task A_cell_with_no_egress_connects_nowhere_and_sends_no_datagram()
    {
        PlatformIsACells();
        var w = new World("none");
        using var listener = new Counting(IPAddress.Loopback);
        var probe = w.Script("net.py", NetScript);
        string[] tries = [$"tcp=127.0.0.1:{listener.Port}", "stub53=127.0.0.53:53", $"fastopen=127.0.0.1:{listener.Port}"];

        var control = await Outside("/usr/bin/python3", ["-I", probe, .. tries]);
        Says(control, "tcp-connected", "stub53-connected", "fastopen-sent", "udp-sent", "dns-resolved");
        Require((await listener.Settled()).Contains("fast"), $"TCP Fast Open from outside reaching a listener (net.ipv4.tcp_fastopen's client bit): {string.Join(",", listener.Received)}");
        listener.Received.Clear();

        var ran = await InCell(w.Policy(CellEgress.None), "/usr/bin/python3", ["-I", probe, .. tries]);
        _output.WriteLine("row (a) class none: " + ran.Out.Replace('\n', ' '));
        Says(ran, "tcp-denied errno=13", "stub53-denied errno=13", "fastopen-denied errno=13", "udp-denied errno=13",
            "bind-denied errno=13", "bind6-denied errno=13", "listen-denied errno=13");
        Assert.Contains("dns-failed", ran.Out);
        Assert.DoesNotContain("-connected", ran.Out);
        Assert.DoesNotContain("-sent", ran.Out);
        Assert.Empty(await listener.Settled());
    }

    /// <summary>
    /// ROW (b), AN ASSERTED GAP: Landlock's TCP rules are port-only, so a cell granted one port reaches it on 127.0.0.1, on ::1 and on
    /// this host's own address alike — the reach the profile labels. A port it was not granted stays denied on each. The day the kernel
    /// scopes a port rule by address this row turns red, and the profile is updated.
    /// </summary>
    [LinuxFact]
    public async Task A_granted_port_reaches_loopback_ipv6_loopback_and_the_hosts_own_address_alike_a_recorded_gap()
    {
        PlatformIsACells();
        var w = new World("reach");
        var own = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(u => u.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        Require(own is not null, "an IPv4 address of this host's own, besides loopback");

        // ONE PORT ON THREE ADDRESSES: the loopback listener names it, the others take the same number (retried on a collision).
        Counting? v4 = null, v6 = null, host = null;
        for (var attempt = 0; host is null; attempt++)
        {
            v4?.Dispose(); v6?.Dispose();
            v4 = new Counting(IPAddress.Loopback);
            try
            {
                v6 = new Counting(IPAddress.IPv6Loopback, v4.Port);
                host = new Counting(own!, v4.Port);
            }
            catch (SocketException) when (attempt < 10) { v6?.Dispose(); v6 = null; }
        }
        using var deniedListener = new Counting(IPAddress.Loopback);
        var port = v4!.Port;
        var probe = w.Script("net.py", NetScript);
        string[] tries = [$"v4=127.0.0.1:{port}", $"v6=[::1]:{port}", $"host={own}:{port}", $"other=127.0.0.1:{deniedListener.Port}"];

        var control = await Outside("/usr/bin/python3", ["-I", probe, .. tries]);
        Says(control, "v4-connected", "v6-connected", "host-connected", "other-connected");
        foreach (var l in new[] { v4, v6!, host, deniedListener }) l.Received.Clear();

        try
        {
            var ran = await InCell(w.Policy(CellEgress.ForTests([port])), "/usr/bin/python3", ["-I", probe, .. tries]);
            _output.WriteLine($"row (b) granted {port} on 127.0.0.1, ::1 and {own}: " + ran.Out.Replace('\n', ' '));
            Says(ran, "v4-connected", "v6-connected", "host-connected", "other-denied errno=13");
            Assert.Equal(["v4"], await v4.Settled());
            Assert.Equal(["v6"], await v6!.Settled());
            Assert.Equal(["host"], await host.Settled());
            Assert.Empty(await deniedListener.Settled());
        }
        finally { v6?.Dispose(); host.Dispose(); v4.Dispose(); }
    }

    /// <summary>
    /// ROW (c), A GUARD: no cell serves another — a cell granted a port binds nowhere (Landlock handles bind and grants no bind rule:
    /// IPv4 or IPv6, an ephemeral port or its own granted one) and cannot <c>listen()</c> on a socket nobody bound, which would pick an
    /// ephemeral port without asking Landlock (seccomp).
    /// </summary>
    [LinuxFact]
    public async Task A_cell_cannot_listen_on_any_tcp_port()
    {
        PlatformIsACells();
        var w = new World("listen");
        int granted;
        using (var free = new TcpListener(IPAddress.Loopback, 0)) { free.Start(); granted = ((IPEndPoint)free.LocalEndpoint).Port; free.Stop(); }
        var ran = await InCell(w.Policy(CellEgress.ForTests([granted])), "/usr/bin/python3", "-I", "-c", $$"""
            import socket
            for name, fam, addr in (("ephemeral", socket.AF_INET, ("127.0.0.1", 0)), ("granted", socket.AF_INET, ("127.0.0.1", {{granted}})),
                                    ("any6", socket.AF_INET6, ("::", 0)), ("granted6", socket.AF_INET6, ("::1", {{granted}}))):
                try:
                    s = socket.socket(fam, socket.SOCK_STREAM); s.bind(addr); print(name + "-bind-open")
                except OSError as e:
                    print(f"{name}-bind-denied errno={e.errno}")
            for name, fam in (("autobind", socket.AF_INET), ("autobind6", socket.AF_INET6)):
                try:
                    s = socket.socket(fam, socket.SOCK_STREAM); s.listen(8); print(f"{name}-listen-open port={s.getsockname()[1]}")
                except OSError as e:
                    print(f"{name}-listen-denied errno={e.errno}")
            """);
        _output.WriteLine("row (c): " + ran.Out.Replace('\n', ' '));
        Says(ran, "ephemeral-bind-denied errno=13", "granted-bind-denied errno=13", "any6-bind-denied errno=13", "granted6-bind-denied errno=13",
            "autobind-listen-denied errno=13", "autobind6-listen-denied errno=13");
        Assert.DoesNotContain("-open", ran.Out);
    }

    /// <summary>
    /// ROW (d), AN ASSERTED CHANNEL: two cells granted one port reach the same outside listener, and through it each other — a relay
    /// outside the app's (ORCHESTRATION § 2.8: "inter-cell communication is part of the benchmark"). Each cell sends its word and
    /// prints what came back; the listener hands each the other's. Recorded so the profile says it, and red the day it closes.
    /// </summary>
    [LinuxFact]
    public async Task Two_cells_granted_one_port_reach_the_same_outside_listener_a_recorded_channel()
    {
        PlatformIsACells();
        var one = new World("chan1");
        var two = new World("chan2");
        using var relay = new TcpListener(IPAddress.Loopback, 0);
        relay.Start();
        var port = ((IPEndPoint)relay.LocalEndpoint).Port;
        var relaying = Task.Run(async () =>
        {
            using var a = await relay.AcceptTcpClientAsync();
            using var b = await relay.AcceptTcpClientAsync();
            var bufA = new byte[16];
            var bufB = new byte[16];
            var wa = Encoding.ASCII.GetString(bufA, 0, await a.GetStream().ReadAsync(bufA));
            var wb = Encoding.ASCII.GetString(bufB, 0, await b.GetStream().ReadAsync(bufB));
            await a.GetStream().WriteAsync(Encoding.ASCII.GetBytes(wb));
            await b.GetStream().WriteAsync(Encoding.ASCII.GetBytes(wa));
            await Task.Delay(500);
        });
        const string talk = """
            import socket, sys
            s = socket.create_connection(("127.0.0.1", int(sys.argv[1])), 30); s.sendall(sys.argv[2].encode())
            print("heard-" + s.recv(16).decode())
            """;
        var first = InCell(one.Policy(CellEgress.ForTests([port])), "/usr/bin/python3", "-I", "-c", talk, $"{port}", "from-one");
        var second = InCell(two.Policy(CellEgress.ForTests([port])), "/usr/bin/python3", "-I", "-c", talk, $"{port}", "from-two");
        var both = await Task.WhenAll(first, second);
        await relaying.WaitAsync(TimeSpan.FromSeconds(60));
        _output.WriteLine($"row (d): cell one {both[0].Out.Trim()}; cell two {both[1].Out.Trim()}");
        Says(both[0], "heard-from-two");
        Says(both[1], "heard-from-one");
    }

    /// <summary>
    /// ROW (e): A CELL RESOLVES NAMES AS ITS CLASS SAYS — item 1's decision, through the table's own machinery. Class <c>none</c>
    /// resolves nothing through DNS; <see cref="CellEgress.Web"/> (443, and its name resolution) and a test table with the index's
    /// port and the same resolution resolve the stub's own name through glibc, Python and Node, and curl reaches the loopback index
    /// by that name — with datagrams still denied when the decision is DNS over TCP.
    /// </summary>
    [LinuxFact]
    public async Task A_cell_resolves_names_as_its_class_says()
    {
        PlatformIsACells();
        var w = new World("names");
        using var index = LoopbackIndex.Start(IPAddress.Loopback);
        var script = w.Script("names.py", $$"""
            import socket, subprocess, sys
            name = "{{StubName}}"
            r = subprocess.run(["getent", "ahosts", name], capture_output=True, text=True)
            print("glibc-" + ("ok " + r.stdout.split()[0] if r.returncode == 0 and r.stdout.strip() else f"fail rc={r.returncode}"))
            try:
                print("python-ok " + socket.getaddrinfo(name, None)[0][4][0])
            except OSError as e:
                print(f"python-fail errno={e.errno}")
            r = subprocess.run(["node", "-e", "require('dns').lookup(process.argv[1], (e, a) => console.log(e ? 'node-fail ' + e.code : 'node-ok ' + a))", name], capture_output=True, text=True)
            print(r.stdout.strip() or "node-fail " + r.stderr.strip()[:120])
            if len(sys.argv) > 1:
                r = subprocess.run(["curl", "-sS", "-m", "20", "-o", "/dev/null", "-w", "%{http_code}", f"http://{name}:{sys.argv[1]}/ping"], capture_output=True, text=True)
                print("curl-" + r.stdout.strip())
            try:
                socket.socket(socket.AF_INET, socket.SOCK_DGRAM); print("udp-open")
            except OSError as e:
                print(f"udp-denied errno={e.errno}")
            """);

        var none = await InCell(w.Policy(CellEgress.None), "/usr/bin/python3", "-I", script);
        _output.WriteLine("row (e) class none: " + none.Out.Replace('\n', ' '));
        Says(none, "udp-denied errno=13");
        Assert.Contains("glibc-fail", none.Out);
        Assert.Contains("python-fail", none.Out);
        Assert.Contains("node-fail", none.Out);

        var web = await InCell(w.Policy(CellEgress.Web), "/usr/bin/python3", "-I", script);
        _output.WriteLine($"row (e) class web ({CellEgress.Web.Describe()}): " + web.Out.Replace('\n', ' '));
        Says(web, "glibc-ok 127.0.0.1", "python-ok 127.0.0.1", "node-ok 127.0.0.1",
            CellEgress.Decided == CellDns.Datagrams ? "udp-open" : "udp-denied errno=13");

        var test = await InCell(w.Policy(CellEgress.ForTests([index.Port], CellEgress.Decided)), "/usr/bin/python3", "-I", script, $"{index.Port}");
        _output.WriteLine("row (e) test table with the index's port: " + test.Out.Replace('\n', ' '));
        Says(test, "glibc-ok 127.0.0.1", "python-ok 127.0.0.1", "node-ok 127.0.0.1", "curl-200");
    }

    /// <summary>
    /// The probe's driver, run inside and outside the cell: one line per resolver and name, <c>row &lt;resolver&gt; &lt;name&gt; ok|fail …</c>.
    /// </summary>
    const string ResolveScript = """
        import os, socket, subprocess, sys
        names, index, ws = sys.argv[1].split(","), sys.argv[2], sys.argv[3]
        print("row pid " + str(os.getpid()))
        def run(cmd, timeout=40):
            try:
                p = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)
                return p.returncode, " ".join((p.stdout + " " + p.stderr).split())[:160]
            except Exception as e:
                return -1, (type(e).__name__ + " " + str(e))[:160]
        def say(key, ok, detail):
            print("row " + key + " " + ("ok " if ok else "fail ") + detail)
        for n in names:
            rc, out = run(["getent", "ahosts", n])
            say("glibc " + n, rc == 0 and out != "", out.split(" ")[0] if rc == 0 and out else "rc=" + str(rc) + " " + out)
            try:
                say("python " + n, True, socket.getaddrinfo(n, None)[0][4][0])
            except OSError as e:
                say("python " + n, False, "errno=" + str(e.errno) + " " + str(e))
            rc, out = run(["node", "-e", "require('dns').lookup(process.argv[1], (e, a) => console.log(e ? 'fail ' + e.code : 'ok ' + a))", n])
            say("node " + n, rc == 0 and out.startswith("ok"), out[3:] if out.startswith("ok") else "rc=" + str(rc) + " " + out)
        rc, out = run(["curl", "-sS", "-m", "20", "-o", "/dev/null", "-w", "%{http_code} %{remote_ip}", index + "/ping"])
        say("curl index", rc == 0 and out.startswith("200"), out)
        dl = os.path.join(ws, "dl-" + str(os.getpid()))
        rc, out = run([sys.executable, "-I", "-m", "pip", "download", "--no-deps", "--no-cache-dir", "--disable-pip-version-check", "--no-input",
                       "--retries", "0", "--timeout", "15", "--index-url", index + "/simple/", "-d", dl, "ta-cell-probe"], 120)
        got = os.path.isdir(dl) and any(f.endswith(".whl") for f in os.listdir(dl))
        say("pip index", rc == 0 and got, "downloaded" if rc == 0 and got else "rc=" + str(rc) + " " + out[-150:])
        """;

    /// <summary>The driver's rows of one run, keyed by resolver and name; a run that printed none is one row saying why.</summary>
    static Dictionary<string, string> Rows(Ran ran)
    {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in ran.Out.Split('\n').Where(l => l.StartsWith("row ", StringComparison.Ordinal)))
        {
            var parts = line.Split(' ', 4);
            if (parts.Length >= 4 && parts[1] != "pid") rows[parts[1] + " " + parts[2]] = parts[3];
        }
        if (rows.Count == 0) rows["run nothing"] = $"fail exit {ran.Code}: {ran.Err.Trim()}";
        return rows;
    }

    /// <summary>The cell's own PATH (<see cref="World.Policy"/>).</summary>
    const string CellPath = "/usr/local/bin:/usr/bin:/bin";

    /// <summary>A program on the cell's PATH, or null.</summary>
    static string? Which(string program) =>
        CellPath.Split(':', StringSplitOptions.RemoveEmptyEntries)
        .Select(d => Path.Combine(d, program)).FirstOrDefault(File.Exists);

    /// <summary>
    /// A LOOPBACK PACKAGE INDEX: a PEP 503 simple index for pip and an npm registry, serving only what the test built, over a bare
    /// <see cref="TcpListener"/> — so a request whose Host is a NAME (<see cref="StubName"/>) is served too, where an
    /// <see cref="HttpListener"/> prefix would refuse it. One request per connection; every request is recorded.
    /// </summary>
    sealed class LoopbackIndex : IDisposable
    {
        readonly TcpListener _listener;
        readonly CancellationTokenSource _stop = new();
        readonly ConcurrentDictionary<string, (string Type, byte[] Body)> _files = new(StringComparer.Ordinal);

        LoopbackIndex(IPAddress at)
        {
            _listener = new TcpListener(at, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _files["/ping"] = ("text/plain", "pong"u8.ToArray());
            _ = Task.Run(Loop);
        }

        public static LoopbackIndex Start(IPAddress at) => new(at);

        public int Port { get; }

        public ConcurrentQueue<string> Requests { get; } = new();

        /// <summary>Serves <paramref name="wheel"/> under <c>/simple/&lt;name&gt;/</c>.</summary>
        public void ServePython(Wheel wheel)
        {
            var link = $"/files/{wheel.FileName}";
            _files[link] = ("application/octet-stream", wheel.Bytes);
            var page = Encoding.UTF8.GetBytes($"<!DOCTYPE html><html><body><a href=\"{link}#sha256={Convert.ToHexString(SHA256.HashData(wheel.Bytes)).ToLowerInvariant()}\">{wheel.FileName}</a></body></html>");
            _files[$"/simple/{wheel.Name}/"] = ("text/html", page);
            _files[$"/simple/{wheel.Name}"] = ("text/html", page);
        }

        /// <summary>Serves an npm package (its packument and its tarball) the way a registry does.</summary>
        public void ServeNpm(string name, string version, byte[] tarball)
        {
            var path = $"/{name}/-/{name}-{version}.tgz";
            _files[path] = ("application/octet-stream", tarball);
            var shasum = Convert.ToHexString(SHA1.HashData(tarball)).ToLowerInvariant();
            var integrity = "sha512-" + Convert.ToBase64String(SHA512.HashData(tarball));
            var packument = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["name"] = name,
                ["dist-tags"] = new Dictionary<string, string> { ["latest"] = version },
                ["versions"] = new Dictionary<string, object>
                {
                    [version] = new Dictionary<string, object>
                    {
                        ["name"] = name, ["version"] = version, ["main"] = "index.js",
                        ["dist"] = new Dictionary<string, string>
                        {
                            ["tarball"] = $"http://127.0.0.1:{Port}{path}", ["shasum"] = shasum, ["integrity"] = integrity
                        }
                    }
                }
            });
            _files[$"/{name}"] = ("application/json", Encoding.UTF8.GetBytes(packument));
        }

        async Task Loop()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (Exception) { return; }
                _ = Task.Run(() => Serve(client));
            }
        }

        async Task Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var head = new StringBuilder();
                    var buf = new byte[1];
                    while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && head.Length < 16384
                           && await stream.ReadAsync(buf) == 1)
                        head.Append((char)buf[0]);
                    var target = head.ToString().Split(' ') is { Length: > 1 } parts ? parts[1].Split('?')[0] : "";
                    Requests.Enqueue(target);
                    var (status, type, body) = _files.TryGetValue(Uri.UnescapeDataString(target), out var f)
                        ? ("200 OK", f.Type, f.Body) : ("404 Not Found", "text/plain", "not here"u8.ToArray());
                    var header = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header);
                    await stream.WriteAsync(body);
                    await stream.FlushAsync();
                }
                catch (Exception) { /* the client went away */ }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    /// <summary>A pure-Python wheel the test builds itself: one module, its metadata and a RECORD with every file's hash.</summary>
    sealed record Wheel(string Name, string FileName, byte[] Bytes)
    {
        public static Wheel Build(string name, string version, string module, string code)
        {
            var dist = name.Replace('-', '_');
            var info = $"{dist}-{version}.dist-info";
            var files = new List<(string Path, byte[] Body)>
            {
                ($"{module}/__init__.py", Encoding.UTF8.GetBytes(code)),
                ($"{info}/METADATA", Encoding.UTF8.GetBytes($"Metadata-Version: 2.1\nName: {name}\nVersion: {version}\n")),
                ($"{info}/WHEEL", Encoding.UTF8.GetBytes("Wheel-Version: 1.0\nGenerator: tradeagent-tests\nRoot-Is-Purelib: true\nTag: py3-none-any\n")),
            };
            var record = new StringBuilder();
            foreach (var (p, body) in files)
                record.Append($"{p},sha256={Convert.ToBase64String(SHA256.HashData(body)).TrimEnd('=').Replace('+', '-').Replace('/', '_')},{body.Length}\n");
            record.Append($"{info}/RECORD,,\n");
            files.Add(($"{info}/RECORD", Encoding.UTF8.GetBytes(record.ToString())));

            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (p, body) in files)
                {
                    using var s = zip.CreateEntry(p).Open();
                    s.Write(body);
                }
            return new Wheel(name, $"{dist}-{version}-py3-none-any.whl", ms.ToArray());
        }
    }
}

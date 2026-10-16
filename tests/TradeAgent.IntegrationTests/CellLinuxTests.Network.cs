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

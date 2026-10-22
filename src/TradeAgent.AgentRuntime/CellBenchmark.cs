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
        "env-is-the-policys", "state-unreadable", "login-unreadable", "inbox-uncreatable", "no-egress",
        "web-443-granted", "web-nothing-else", "gap-web-loopback-443", "gap-web-lan-443",
        "tmp-unchanged", "route-a-exec-server"
    ];

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
    public static string Profile(string provider, CellPlatform platform, string codexPin) =>
        Sha256Hex.Of(string.Join('\n',
        [
            "provider " + provider, "platform " + CellProofStore.ThisPlatform, "architecture " + platform.Architecture,
            "landlock-abi " + platform.LandlockAbi, "lsm " + (platform.Lsm ?? "unknown"),
            CellNative.KernelSurface(RuntimeInformation.ProcessArchitecture, platform.LandlockAbi),
            "codex " + codexPin, "egress " + string.Join(',', ProvedEgress), "rows " + string.Join(',', RowNames)
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
    readonly Process _p;
    readonly List<JsonElement> _seen = [];
    readonly Task _pump;
    int _id;

    public ExecServerClient(Process process)
    {
        _p = process;
        _pump = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
                try { lock (_seen) _seen.Add(JsonDocument.Parse(line).RootElement.Clone()); }
                catch (JsonException) { /* not a message */ }
        });
        _ = Task.Run(async () => { while (await process.StandardError.ReadLineAsync() is not null) { } });
    }

    /// <summary>The last error or non-answer, in words.</summary>
    public string Last { get; private set; } = "";

    void Send(object message)
    {
        _p.StandardInput.WriteLine(JsonSerializer.Serialize(message));
        _p.StandardInput.Flush();
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

    /// <summary>Runs <paramref name="argv"/> through <c>process/start</c> and returns its exit code and output, or (-1, why).</summary>
    public async Task<(int Exit, string Output)> RunAsync(string cwd, string[] argv, CancellationToken ct)
    {
        var pid = "bench-" + Guid.NewGuid().ToString("n")[..12];
        var started = await CallAsync("process/start", new Dictionary<string, object?>
        {
            ["processId"] = pid, ["argv"] = argv, ["cwd"] = new Uri(cwd).AbsoluteUri,
            ["envPolicy"] = new { inherit = "all", ignoreDefaultExcludes = true, exclude = Array.Empty<string>(), set = new { }, includeOnly = Array.Empty<string>() },
            ["env"] = new { }, ["tty"] = false, ["pipeStdin"] = false, ["arg0"] = null, ["sandbox"] = null, ["enforceManagedNetwork"] = false
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
        try { _p.StandardInput.Close(); } catch (Exception) { /* closed */ }
        try { _p.WaitForExit(10_000); } catch (Exception) { /* gone */ }
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
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
            using var h = ProcessContainment.StartHarness(psi, ProcessContainment.RouteAAttempt(rows.Cgroups, grantName), Launcher());
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

    // ---- route (a) with the pinned codex (CI fetches it; TA_CELL_CODEX names its bin/codex) ------------------------------------

    /// <summary>
    /// THE PINNED LINUX CODEX, OR A RED ROW: CI's Cell step unpacks <c>codex-package-x86_64-unknown-linux-musl.tar.gz</c> at the release
    /// every seat runs and names its <c>bin/codex</c> in <c>TA_CELL_CODEX</c>; the binary must hash to this build's own pin.
    /// </summary>
    static string PinnedCodex()
    {
        var path = Environment.GetEnvironmentVariable("TA_CELL_CODEX");
        Require(!string.IsNullOrEmpty(path) && File.Exists(path), $"the pinned linux-musl codex named by TA_CELL_CODEX (got '{path}')");
        var pin = RuntimeCatalog.BuiltIn(HostPlatform.Current).First(m => m.Id == "codex").Install.ExecutableSha256;
        using var f = File.OpenRead(path!);
        var hash = Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
        Require(hash == pin, $"{path} hashes to {hash}, not this build's codex pin {pin}");
        return path!;
    }

    /// <summary>The package root the cell runs codex from: the folder holding <c>bin/codex</c>.</summary>
    static string PackageOf(string codex) => Path.GetDirectoryName(Path.GetDirectoryName(codex)!)!;

    /// <summary>
    /// <c>codex exec-server --listen stdio</c> IN A CELL, driven over its stdio as the harness drives it: newline-delimited JSON-RPC,
    /// responses matched by id, notifications (<c>process/output</c>, <c>process/exited</c>, <c>process/closed</c>) kept in order.
    /// </summary>
    sealed class ExecServer : IDisposable
    {
        readonly ContainedProcess _held;
        readonly List<JsonElement> _seen = [];
        readonly StringBuilder _err = new();
        readonly Task _pump, _errPump;
        int _id;

        ExecServer(ContainedProcess held)
        {
            _held = held;
            _pump = Task.Run(async () =>
            {
                while (await held.Process.StandardOutput.ReadLineAsync() is { } line)
                {
                    try { lock (_seen) _seen.Add(JsonDocument.Parse(line).RootElement.Clone()); }
                    catch (JsonException) { lock (_err) _err.Append("[stdout, not JSON] ").AppendLine(line); }
                }
            });
            _errPump = Task.Run(async () =>
            {
                while (await held.Process.StandardError.ReadLineAsync() is { } line) lock (_err) _err.AppendLine(line);
            });
        }

        public static ExecServer Start(CellPolicy policy, string codex, RowsRoot rows, IReadOnlyDictionary<string, string>? harnessEnv = null)
        {
            var psi = Plain(codex, "exec-server", "--listen", "stdio");
            foreach (var (k, v) in harnessEnv ?? new Dictionary<string, string>()) psi.Environment[k] = v;
            return new ExecServer(ProcessContainment.Start(psi, policy, Launcher(), cgroups: rows.Cgroups));
        }

        public string Stderr { get { lock (_err) return _err.ToString(); } }

        public bool Exited => _held.Process.HasExited;

        void Send(object message)
        {
            _held.Process.StandardInput.WriteLine(JsonSerializer.Serialize(message));
            _held.Process.StandardInput.Flush();
        }

        public void Notify(string method, object @params) => Send(new { method, @params });

        /// <summary>The result of one call, or null on an error answer or none within <paramref name="seconds"/> (the reason in <paramref name="why"/>).</summary>
        public JsonElement? Call(string method, object @params, out string why, int seconds = 30)
        {
            var id = Interlocked.Increment(ref _id);
            try { Send(new { id, method, @params }); }
            catch (IOException ex) { why = $"the exec-server's input is closed ({ex.Message})"; return null; }
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                lock (_seen)
                    foreach (var m in _seen)
                        if (m.TryGetProperty("id", out var mid) && mid.ValueKind == JsonValueKind.Number && mid.GetInt32() == id)
                        {
                            if (m.TryGetProperty("result", out var r)) { why = ""; return r; }
                            why = m.TryGetProperty("error", out var e) ? e.ToString() : m.ToString();
                            return null;
                        }
                if (Exited && _pump.IsCompleted) { why = $"the exec-server exited ({_held.Process.ExitCode})"; return null; }
                Thread.Sleep(20);
            }
            why = $"no answer to {method} in {seconds} s";
            return null;
        }

        /// <summary>Starts <paramref name="argv"/> through <c>process/start</c> in <paramref name="cwd"/> and returns its exit code and its stdout and stderr, or why not.</summary>
        public (int? Exit, string Output, string Why) Run(string cwd, params string[] argv)
        {
            var pid = "row-" + Guid.NewGuid().ToString("n")[..12];
            var started = Call("process/start", new Dictionary<string, object?>
            {
                ["processId"] = pid, ["argv"] = argv, ["cwd"] = new Uri(cwd).AbsoluteUri,
                ["envPolicy"] = new { inherit = "all", ignoreDefaultExcludes = true, exclude = Array.Empty<string>(), set = new { }, includeOnly = Array.Empty<string>() },
                ["env"] = new { }, ["tty"] = false, ["pipeStdin"] = false, ["arg0"] = null, ["sandbox"] = null, ["enforceManagedNetwork"] = false
            }, out var why);
            if (started is null) return (null, "", "process/start: " + why);
            var until = DateTime.UtcNow.AddSeconds(60);
            int? exit = null;
            while (DateTime.UtcNow < until)
            {
                var output = new StringBuilder();
                var closed = false;
                lock (_seen)
                    foreach (var m in _seen)
                    {
                        if (!m.TryGetProperty("method", out var method) || !m.TryGetProperty("params", out var p)
                            || !p.TryGetProperty("processId", out var id) || id.GetString() != pid) continue;
                        switch (method.GetString())
                        {
                            case "process/output":
                                output.Append(Encoding.UTF8.GetString(Convert.FromBase64String(p.GetProperty("chunk").GetString()!)));
                                break;
                            case "process/exited": exit = p.TryGetProperty("exitCode", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : -1; break;
                            case "process/closed": closed = true; break;
                        }
                    }
                if (closed) return (exit, output.ToString(), "");
                Thread.Sleep(20);
            }
            return (exit, "", "no process/closed within 60 s");
        }

        public void Dispose()
        {
            try { _held.Process.StandardInput.Close(); } catch (Exception) { /* already closed */ }
            try { _held.Process.WaitForExit(10_000); } catch (Exception) { /* gone */ }
            _held.Dispose();
        }
    }

    /// <summary>Initialises an exec-server as the harness does: <c>initialize</c> then <c>initialized</c>. Null when it answered, else why not.</summary>
    static string? Initialise(ExecServer es)
    {
        if (es.Call("initialize", new { clientName = "codex-environment", resumeSessionId = (string?)null }, out var why) is null)
            return "initialize: " + why;
        es.Notify("initialized", new { });
        return null;
    }

    /// <summary>One cell world for route (a): the workspace, the cell's empty codex home and home (read-only), the pinned codex's package.</summary>
    sealed record RouteWorld(World W, string Codex, string CellCodexHome, string CellHome)
    {
        public static RouteWorld Make(string name, string codex)
        {
            var w = new World(name);
            var cch = Path.Combine(w.Root, "cell-codex");
            var ch = Path.Combine(w.Root, "cell-home");
            Directory.CreateDirectory(cch);
            Directory.CreateDirectory(ch);
            return new RouteWorld(w, codex, cch, ch);
        }

        public CellPolicy Policy => RouteA.Policy(W.Ws, Codex, PackageOf(Codex), CellCodexHome, CellHome);
    }

    /// <summary>
    /// ITEM 1, THE PROBE THAT DECIDES (prints a table; fails only on a missing prerequisite): the pinned exec-server in a real cell
    /// under (A) an empty READ-ONLY codex home and (B) an empty WRITABLE one inside the workspace — does it start, answer
    /// <c>initialize</c>, answer an empty <c>environmentConfig/read</c>, run <c>process/start</c> in the workspace — and what it wrote.
    /// (A) holding decides read-only; route (a)'s policy (<see cref="RouteA.Policy"/>) is (A).
    /// </summary>
    [LinuxFact]
    public void Route_a_probe_the_exec_server_in_a_cell_under_a_read_only_and_a_writable_codex_home()
    {
        PlatformIsACells();
        var codex = PinnedCodex();
        using var rows = Rows();
        var table = new StringBuilder("route-a probe | codex home | started | initialize | environmentConfig/read | process/start pwd; id -u | written to codex home | stderr\n");
        foreach (var writable in new[] { false, true })
        {
            var r = RouteWorld.Make(writable ? "probe-b" : "probe-a", codex);
            var codexHome = writable ? Path.Combine(r.W.Ws, ".cell-codex-home") : r.CellCodexHome;
            Directory.CreateDirectory(codexHome);
            var policy = writable
                ? new CellPolicy(r.W.Ws, [r.CellHome], [PackageOf(codex), .. CellPolicy.SystemRoots],
                    r.Policy.Environment.Select(e => e.Key == "CODEX_HOME" ? new KeyValuePair<string, string>("CODEX_HOME", codexHome) : e), CellEgress.None)
                : r.Policy;
            using var es = ExecServer.Start(policy, codex, rows);
            var init = Initialise(es);
            var config = init is null ? es.Call("environmentConfig/read", new
            {
                cwd = new Uri(r.W.Ws).AbsoluteUri, configPaths = new[] { new[] { "mcp_servers" } }, requirementsPaths = new[] { new[] { "mcp_servers" } }
            }, out var cwhy) : null;
            var layers = config is { } c && c.TryGetProperty("config", out var cfg) && cfg.TryGetProperty("layers", out var l) ? l.GetArrayLength().ToString(System.Globalization.CultureInfo.InvariantCulture) : "n/a";
            var ran = init is null ? es.Run(r.W.Ws, "/bin/sh", "-c", "pwd; id -u") : (null, "", "not initialised");
            var wrote = Directory.EnumerateFileSystemEntries(codexHome, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(codexHome, p)).ToList();
            table.Append(CultureInfo(writable ? "B writable (workspace)" : "A read-only")).Append(" | ")
                .Append(!es.Exited || init is null ? "yes" : $"no (exit {SafeExit(es)})").Append(" | ")
                .Append(init ?? "ok").Append(" | ")
                .Append(config is null ? "failed" : $"ok, {layers} layer(s)").Append(" | ")
                .Append(ran.Why.Length > 0 ? ran.Why : $"exit {ran.Exit}: {ran.Output.Replace('\n', ' ').Trim()} (workspace {(ran.Output.Split('\n')[0] == r.W.Ws ? "parity" : "MISMATCH")})").Append(" | ")
                .Append(wrote.Count == 0 ? "nothing" : string.Join(", ", wrote.Take(12))).Append(" | ")
                .Append(es.Stderr.Replace('\n', ' ').Trim() is { Length: > 0 } e ? e[..Math.Min(e.Length, 300)] : "-").Append('\n');
        }
        _output.WriteLine(table.ToString());
        Console.WriteLine(table.ToString());

        static string CultureInfo(string s) => s;
        static string SafeExit(ExecServer es) { try { return es.Exited ? "exited" : "running"; } catch (Exception) { return "?"; } }
    }

    /// <summary>
    /// ROW (a): THE EXEC-SERVER IN A CELL SEES ONLY ITS POLICY'S ENVIRONMENT. Started the way the harness starts it — by a process whose
    /// environment holds the harness's <c>CODEX_HOME</c>, <c>HOME</c> and the launch's bearer — its own <c>/proc/self/environ</c>, read by
    /// a child it starts, is the policy's, name for name and value for value: none of the three crossed. THE WATCHED MUTANT: the stage's
    /// execve handed its inherited <c>environ</c> instead of the policy's.
    /// </summary>
    [LinuxFact]
    public void The_exec_server_in_a_cell_sees_only_its_policys_environment()
    {
        PlatformIsACells();
        var codex = PinnedCodex();
        using var rows = Rows();
        var r = RouteWorld.Make("es-env", codex);
        var harness = new Dictionary<string, string>
        {
            ["CODEX_HOME"] = Path.Combine(r.W.Root, "harness-codex"), ["HOME"] = Path.Combine(r.W.Root, "harness-home"),
            [AgentGrants.Variable] = "FAKE-harness-bearer-0001"
        };
        using var es = ExecServer.Start(r.Policy, codex, rows, harness);
        Assert.Null(Initialise(es));
        var ran = es.Run(r.W.Ws, "/bin/sh", "-c", "tr '\\0' '\\n' < /proc/$PPID/environ; echo PARENT=$(tr '\\0' ' ' < /proc/$PPID/cmdline)");
        Assert.True(ran.Why.Length == 0 && ran.Exit == 0, $"process/start: {ran.Why} exit {ran.Exit}: {ran.Output}\n{es.Stderr}");
        var lines = ran.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        Assert.Contains(lines, l => l.StartsWith("PARENT=", StringComparison.Ordinal) && l.Contains("exec-server", StringComparison.Ordinal));
        var environ = lines.Where(l => !l.StartsWith("PARENT=", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        var expected = r.Policy.Environment.Select(e => e.Key + "=" + e.Value).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(expected, environ);
        Assert.DoesNotContain("FAKE-harness-bearer", ran.Output);
        Assert.DoesNotContain("harness-codex", ran.Output);
    }

    /// <summary>A one-pixel PNG, for <c>view_image</c>.</summary>
    static readonly byte[] Pixel = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    /// <summary>The outcome of one route (a) turn: what each tool call returned to the model, by call id, and the harness's own output.</summary>
    sealed record Turn(Dictionary<string, string> Outputs, int Exit, string Stdout, string Stderr);

    /// <summary>
    /// ONE ROUTE (a) TURN AGAINST THE MOCK: the attempt and its leaves; the cell's policy written; the harness's per-attempt codex home
    /// (<see cref="RouteA.Make"/>, its <c>environments.toml</c> naming TradeAgent's launcher); <c>codex exec</c> started in the harness leaf
    /// with the CELL class's deny set (less its door) — which drops <c>--ignore-user-config</c> — a FAKE bearer and a FAKE sign-in; the mock answering the
    /// case's steps. Returns what every tool call gave back to the model, read off the last request.
    /// </summary>
    static async Task<Turn> RouteTurn(RouteWorld r, RowsRoot rows, object[] steps, Action<RouteA.Layout>? plant = null)
    {
        var dir = Path.Combine(r.W.Root, "mock");
        Directory.CreateDirectory(dir);
        var caseFile = Path.Combine(dir, "case.json");
        File.WriteAllText(caseFile, JsonSerializer.Serialize(new { steps, final = "ROUTE-A-DONE" }));
        var portFile = Path.Combine(dir, "port");
        var mock = Process.Start(new ProcessStartInfo("/usr/bin/python3",
            ["-I", Path.Combine(TradeAgent.Tests.DayOnePrograms.RepoRoot(), "tests", "TradeAgent.IntegrationTests", "CellRoute", "mock_responses.py"),
             portFile, caseFile, Path.Combine(dir, "req")]) { UseShellExecute = false })!;
        try
        {
            for (var i = 0; i < 100 && !File.Exists(portFile); i++) await Task.Delay(50);
            Require(File.Exists(portFile), "the mock model's port");
            var port = File.ReadAllText(portFile).Trim();

            var attempt = ProcessContainment.RouteAAttempt(rows.Cgroups, "route");
            var layout = RouteA.LayoutFor(Path.GetFileName(attempt.Dir));
            var policyFile = r.Policy.Write();
            var signIn = Path.Combine(r.W.Root, "seat-auth.json");
            File.WriteAllText(signIn, "{\"auth_mode\":\"apikey\",\"OPENAI_API_KEY\":\"sk-FAKE-route-a-0000\"}");
            // THE LAUNCHER, ITS STAGE AND THE EXEC-SERVER WRITE THEIR STDERR TO A FILE the row prints: codex keeps a stdio program's
            // stderr to itself, and a cell that would not start is otherwise only "unsupported call" (CI run 39702504076).
            var leg = Path.Combine(dir, "cell-leg.err");
            IReadOnlyList<string> launcher = ["/bin/sh", "-c", $"exec \"$0\" \"$@\" 2>>'{leg}'", .. Launcher()];
            RouteA.Make(layout, Encoding.UTF8.GetBytes("# the class's keys travel on the command line\n"),
                RouteA.EnvironmentsToml(launcher, attempt.Cell, policyFile, r.Codex, Paths.Home), signIn);
            plant?.Invoke(layout);

            var cli = RuntimeCatalog.BuiltIn(HostPlatform.Current).First(m => m.Id == "codex");
            var psi = Plain(r.Codex, ["exec", "--json", "--dangerously-bypass-approvals-and-sandbox", "--skip-git-repo-check",
                .. WithoutDoor(cli.Seat!.ArgsFor(SeatPlan.CellClass)), "-m", "gpt-5.5",
                "-c", "model_provider=\"mock\"", "-c", "model_providers.mock.name=\"mock\"",
                "-c", $"model_providers.mock.base_url=\"http://127.0.0.1:{port}/v1\"", "-c", "model_providers.mock.wire_api=\"responses\"", "go"]);
            psi.WorkingDirectory = r.W.Ws;
            psi.Environment.Clear();
            psi.Environment["PATH"] = "/usr/bin:/bin";
            psi.Environment["CODEX_HOME"] = layout.HarnessCodexHome;
            psi.Environment["HOME"] = r.W.Home;
            psi.Environment[AgentGrants.Variable] = "FAKE-harness-bearer-0002";
            psi.Environment["TRADEAGENT_HOME"] = Paths.Home;
            psi.Environment["RUST_LOG"] = "warn,codex_exec_server=debug,codex_core::environment=debug,codex_app_server_client=debug";
            if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } dotnetRoot) psi.Environment["DOTNET_ROOT"] = dotnetRoot;
            using var h = ProcessContainment.StartHarness(psi, attempt, Launcher());
            h.Process.StandardInput.Close();
            var so = h.Process.StandardOutput.ReadToEndAsync();
            var se = h.Process.StandardError.ReadToEndAsync();
            try { await h.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120)); }
            catch (TimeoutException) { Assert.Fail($"the route (a) turn did not finish in 120 s; stderr: {(se.IsCompleted ? await se : "")}"); }
            var outputs = new Dictionary<string, string>();
            var last = Directory.EnumerateFiles(Path.Combine(dir, "req"), "*.json").Order(StringComparer.Ordinal).LastOrDefault();
            if (last is not null)
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(last));
                if (doc.RootElement.GetProperty("body").TryGetProperty("input", out var input))
                    foreach (var item in input.EnumerateArray())
                        if (item.TryGetProperty("type", out var t) && t.GetString()!.EndsWith("_output", StringComparison.Ordinal) && item.TryGetProperty("call_id", out var id))
                            outputs[id.GetString()!] = item.GetProperty("output").ToString();
            }
            RouteA.Remove(layout);
            var err = await se;
            var legErr = File.Exists(leg) ? File.ReadAllText(leg) : "(the launcher never ran: no stderr file)";
            var envLines = string.Join("\n", err.Split('\n').Where(l => l.Contains("environment", StringComparison.OrdinalIgnoreCase)
                || l.Contains("exec_server", StringComparison.Ordinal) || l.Contains("stdio", StringComparison.OrdinalIgnoreCase)).TakeLast(40));
            return new Turn(outputs, h.Process.ExitCode, await so,
                $"{err[^Math.Min(err.Length, 1500)..]}\n--- policy file after the turn: {(File.Exists(policyFile) ? "STILL THERE (no supervisor removed it)" : "removed")}" +
                $"\n--- the cell leg's stderr ---\n{legErr[^Math.Min(legErr.Length, 3000)..]}\n--- codex's environment log ---\n{envLines}");
        }
        finally
        {
            try { mock.Kill(); mock.WaitForExit(5000); } catch (Exception) { /* gone */ }
            mock.Dispose();
        }
    }

    /// <summary>The cell class's deny set less its door (no door runs in these rows; the class's <c>required=true</c> would fail the session).</summary>
    static IEnumerable<string> WithoutDoor(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "-c" && i + 1 < args.Length && args[i + 1].StartsWith($"mcp_servers.{BrokerSeat.DoorServer}.", StringComparison.Ordinal)) { i++; continue; }
            yield return args[i];
        }
    }

    static object Exec(string id, string cmd) => new { type = "function_call", call_id = id, name = "exec_command", arguments = JsonSerializer.Serialize(new { cmd }) };

    static object Patch(string id, string file, string line) =>
        new { type = "custom_tool_call", call_id = id, name = "apply_patch", input = $"*** Begin Patch\n*** Add File: {file}\n+{line}\n*** End Patch\n" };

    static object View(string id, string path) => new { type = "function_call", call_id = id, name = "view_image", arguments = JsonSerializer.Serialize(new { path }) };

    /// <summary>
    /// ROW (b): EVERY MODEL-DIRECTED ACTION OF ROUTE (a) RUNS IN THE CELL (kit b1 b3 b4; b8's sub-agents and b9's code mode are not
    /// offered by the class — its deny set disables multi_agent and its model is classic-tools). Item 1's second question was answered
    /// here: under <c>--ignore-user-config</c> the per-attempt <c>environments.toml</c> is NOT read and every action ran in the harness
    /// (CI run 39700964111); the class drops the flag, and the shell's cgroup is now the attempt's CELL leaf. The shell sees the cell's empty codex home and home, no
    /// bearer, and is refused the harness's file; a patch lands in the workspace and is refused outside it; an image is read from
    /// the workspace and refused from the harness's side.
    /// </summary>
    [LinuxFact]
    public async Task Every_model_directed_action_of_route_a_runs_in_the_cell()
    {
        PlatformIsACells();
        Require(File.Exists("/usr/bin/python3"), "/usr/bin/python3 for the mock model");
        var codex = PinnedCodex();
        using var rows = Rows();
        var r = RouteWorld.Make("route-b", codex);
        var harnessSide = Path.Combine(r.W.Root, "harness-only");
        Directory.CreateDirectory(harnessSide);
        File.WriteAllText(Path.Combine(harnessSide, "secret.txt"), "harness-secret");
        File.WriteAllBytes(Path.Combine(harnessSide, "img.png"), Pixel);
        File.WriteAllBytes(Path.Combine(r.W.Ws, "img.png"), Pixel);

        var turn = await RouteTurn(r, rows,
        [
            Exec("c1", "echo MARK; echo PWD=$(pwd); echo HOME=$HOME; echo CODEX_HOME=$CODEX_HOME; echo GRANT=${" + AgentGrants.Variable + ":-none}; " +
                       "echo CG=$(sed -n 's/^0:://p' /proc/self/cgroup); cat " + Path.Combine(harnessSide, "secret.txt") + " 2>&1 | sed 's/^/HARN=/'"),
            Patch("c2", "rel-patched.txt", "in the cell"),
            Patch("c3", Path.Combine(harnessSide, "patched.txt"), "outside"),
            View("c4", Path.Combine(r.W.Ws, "img.png")),
            View("c5", Path.Combine(harnessSide, "img.png")),
        ]);
        var said = $"exit {turn.Exit}\n--- outputs ---\n{string.Join("\n", turn.Outputs.Select(o => o.Key + ": " + o.Value))}\n--- stderr ---\n{turn.Stderr}";
        _output.WriteLine("route (b): " + said);
        Assert.True(turn.Outputs.TryGetValue("c1", out var c1), "the shell call returned nothing: " + said);
        Assert.Contains("MARK", c1);
        Assert.Contains("PWD=" + r.W.Ws, c1);
        Assert.Contains("HOME=" + r.CellHome, c1);
        Assert.Contains("CODEX_HOME=" + r.CellCodexHome, c1);
        Assert.Contains("GRANT=none", c1);
        Assert.Matches("CG=/.*/" + AttemptCgroups.AttemptPrefix + "[^/]*/" + AttemptCgroups.CellLeaf + @"(\\n|\s|$)", c1);
        Assert.Contains("Permission denied", c1);
        Assert.DoesNotContain("harness-secret", c1);
        Assert.True(File.Exists(Path.Combine(r.W.Ws, "rel-patched.txt")), "the relative patch did not land in the workspace: " + said);
        Assert.False(File.Exists(Path.Combine(harnessSide, "patched.txt")), "a patch wrote the harness's side: " + said);
        Assert.True(turn.Outputs.TryGetValue("c4", out var c4) && !c4.Contains("unable to locate image", StringComparison.Ordinal), "the workspace image was not read: " + said);
        Assert.True(turn.Outputs.TryGetValue("c5", out var c5) && c5.Contains("unable to locate image", StringComparison.Ordinal), "the harness's image was read: " + said);
    }

    /// <summary>
    /// ROW (c): A CELL-DECLARED MCP SERVER NEVER STARTS (kit b10). The cell's codex home answers <c>environmentConfig/read</c>; one that
    /// declares an MCP server — planted here, since the cell's own home is read-only and empty in the product — is never started by the
    /// harness, in the cell or outside it: the marker its command would write never appears.
    /// </summary>
    [LinuxFact]
    public async Task A_cell_declared_mcp_server_never_starts()
    {
        PlatformIsACells();
        Require(File.Exists("/usr/bin/python3"), "/usr/bin/python3 for the mock model");
        var codex = PinnedCodex();
        using var rows = Rows();
        var r = RouteWorld.Make("route-c", codex);
        var marker = Path.Combine(r.W.Ws, "inj-started");
        File.WriteAllText(Path.Combine(r.CellCodexHome, "config.toml"),
            $"[mcp_servers.inj]\ncommand = \"/bin/sh\"\nargs = [\"-c\", \"touch {marker}; sleep 5\"]\n");
        var turn = await RouteTurn(r, rows,
        [
            new { type = "function_call", call_id = "i1", @namespace = "mcp__inj", name = "echo", arguments = "{\"text\": \"inj\"}" },
            Exec("c2", "sleep 2; echo AFTER"),
        ]);
        _output.WriteLine($"route (c): exit {turn.Exit}; outputs {string.Join(" | ", turn.Outputs.Select(o => o.Key + ": " + o.Value))}");
        Assert.False(File.Exists(marker), "an MCP server the cell's codex home declared was started");
    }

    /// <summary>
    /// ROW (f): A ROUTE (a) CELL CANNOT CONNECT TO THE DOOR. In route (a) the harness is the door's client, so the cell needs no port:
    /// its egress is <c>none</c>, a loopback listener standing in for the door is reachable from outside and refused from inside, and
    /// the cell's environment holds no bearer.
    /// </summary>
    [LinuxFact]
    public async Task A_route_a_cell_cannot_connect_to_the_door()
    {
        PlatformIsACells();
        var w = new World("door");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var probe = w.Script("door.py", """
                import os, socket, sys
                try:
                    socket.create_connection(("127.0.0.1", int(sys.argv[1])), timeout=3).close(); print("door-connected")
                except PermissionError:
                    print("door-denied")
                print("bearer=" + os.environ.get(sys.argv[2], "none"))
                """);
            var outside = await Outside("/usr/bin/python3", probe, port, AgentGrants.Variable);
            Says(outside, "door-connected");
            var cellHome = Path.Combine(w.Root, "cell-home");
            Directory.CreateDirectory(cellHome);
            var policy = RouteA.Policy(w.Ws, "/usr/bin/python3", "/usr/bin", cellHome, cellHome);
            Assert.Equal(CellEgress.None.Name, policy.Egress.Name);
            var ran = await InCell(policy, "/usr/bin/python3", probe, port, AgentGrants.Variable);
            Says(ran, "door-denied", "bearer=none");
        }
        finally { listener.Stop(); }
    }

    /// <summary>
    /// ROW (i), first half: A PROOF IS WRITTEN ONLY WHEN EVERY ROW HOLDS. The host benchmark, run through the product's path with the
    /// pinned codex, holds every row and writes one proof that then counts here; run again with no codex, its route (a) leg cannot hold
    /// and nothing more is written. The rows are printed (the evidence beyond CI).
    /// </summary>
    [LinuxFact]
    public async Task A_proof_is_written_only_when_every_row_holds()
    {
        PlatformIsACells();
        var codex = PinnedCodex();
        using var rows = Rows();
        _ = new World("bench");
        using var db = TestEnv.NewDb();
        var pin = RuntimeCatalog.BuiltIn(HostPlatform.Current).First(m => m.Id == "codex").Install.ExecutableSha256!;
        var held = await CellBenchmark.RunAsync(db, codex, pin, rows.Cgroups, Launcher());
        var said = string.Join("\n", held.Rows.Select(r => $"  {r.Name}: {(r.Held ? "HELD" : "FAILED")} — {r.Seen}"));
        _output.WriteLine($"cell benchmark: {held.Sentence}\n{said}");
        Console.WriteLine($"cell benchmark: {held.Sentence}\n{said}");
        Assert.True(held.ProofId is not null, $"the benchmark wrote no proof:\n{said}");
        var proof = Assert.Single(new CellProofStore(db).Proven());
        Assert.Equal((held.ProofId!.Value, RouteA.Provider, held.ProfileHash), (proof.Id, proof.Provider, proof.ProfileHash));

        var without = await CellBenchmark.RunAsync(db, null, pin, rows.Cgroups, Launcher());
        Assert.Null(without.ProofId);
        Assert.Contains(without.Rows, r => r.Name == "route-a-exec-server" && !r.Held);
        Assert.Single(new CellProofStore(db).Proven());
    }
}

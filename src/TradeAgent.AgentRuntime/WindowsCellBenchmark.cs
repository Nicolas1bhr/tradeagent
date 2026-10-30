using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Principal;
using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// THE WINDOWS HOST BENCHMARK THAT WRITES THE WINDOWS <c>cell_proof</c> (<c>U-cell-windows</c> item 3; the counterpart of
/// <see cref="CellBenchmark"/> on Linux). The app runs it ITSELF at every start on Windows — no press, no flag, no setting — through the
/// product's own path (<see cref="ProcessContainment.Start(ProcessStartInfo, CellPolicy, IReadOnlyList{string}?, CellPlatform?, AttemptCgroups?, string?)"/>,
/// which on Windows is the cell stage, a slot's AppContainer and its job), inside the presence register. Each row launches a real cell
/// and shows a denial or a parity from inside it; the last is route (a)'s leg with no model — the pinned codex <c>exec-server</c> in a
/// cell answers <c>initialize</c>, an empty <c>environmentConfig/read</c>, and <c>process/start</c> of <c>cmd /c cd</c> with workspace
/// parity. Every row held ⇒ ONE proof row under <see cref="RouteA.WindowsProvider"/>, bound to machine, build and boot
/// (<see cref="CellProofStore.Record"/>); any row failed ⇒ nothing is written, and no position beyond the legacy pair activates.
///
/// <para><b>Its own row set and profile</b> (<see cref="CellBenchmark.WindowsRowNames"/>, <see cref="CellBenchmark.Profile"/>): never a
/// copy of the Linux rows, and a row holds only when observed. The firewall rule that holds <c>web</c> to 443 is not placed by this
/// benchmark — it is the elevated helper's, at first setup; here <c>web-443-granted</c> and <c>web-nothing-else</c> measure what the rule
/// (where it is in force) and <c>internetClient</c> leave, and <c>own-lan-address-denied</c> records this host's own LAN address closed.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsCellBenchmark
{
    /// <summary>
    /// Runs the benchmark and writes a proof only when every row held. <paramref name="codex"/> is the pinned codex this host runs (null:
    /// the route (a) leg fails and nothing is written). Never throws: a failure is a row that did not hold.
    /// </summary>
    public static async Task<BenchResult> RunAsync(Database db, string? codex, string codexPin, IReadOnlyList<string>? launcher = null, CancellationToken ct = default)
    {
        var platform = CellPlatform.Read();
        var profile = CellBenchmark.Profile(RouteA.WindowsProvider, platform, codexPin);
        var rows = new List<BenchRow>();
        if (platform.Refusal() is { } refusal)
            return new BenchResult([new BenchRow("platform", false, refusal)], profile, null, "no cell proof: " + refusal);

        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var cmd = Path.Combine(system32, "cmd.exe");
        var curl = Path.Combine(system32, "curl.exe");

        var root = Path.Combine(Paths.Home, "cell-bench", Guid.NewGuid().ToString("n")[..12]);
        var ws = Path.Combine(root, "ws");
        var codexHome = Path.Combine(root, "codex");
        var home = Path.Combine(root, "home");
        var outside = Path.Combine(root, "outside");
        foreach (var d in new[] { ws, codexHome, home, outside }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(outside, "auth.json"), "dummy-owner-login");

        try
        {
            var basePolicy = codex is not null
                ? RouteA.WindowsPolicy(ws, codex, RouteA.CodexRoot(codex), codexHome, home, CellEgress.None)
                : RouteA.WindowsPolicy(ws, cmd, system32, codexHome, home, CellEgress.None);
            CellPolicy Web() => new(basePolicy.Workspace, basePolicy.ReadOnly, basePolicy.Executable, basePolicy.Environment, CellEgress.Web);

            async Task<(int Code, string Out, ContainedProcess Held)> Start(CellPolicy under, string program, params string[] args)
            {
                var psi = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                var held = ProcessContainment.Start(psi, under, launcher, platform);
                held.Process.StandardInput.Close();
                var so = held.Process.StandardOutput.ReadToEndAsync(ct);
                var se = held.Process.StandardError.ReadToEndAsync(ct);
                await held.Process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(60), ct);
                return (held.Process.ExitCode, (await so) + (await se), held);
            }

            async Task<(int Code, string Out)> Run(CellPolicy under, string program, params string[] args)
            {
                var (code, output, held) = await Start(under, program, args);
                held.Dispose();
                return (code, output);
            }

            async Task Row(string name, Func<Task<(bool, string)>> body, bool gap = false)
            {
                try { var (ok, seen) = await body(); rows.Add(new BenchRow(name, ok, seen) { Gap = gap }); }
                catch (Exception ex) when (ex is not OperationCanceledException) { rows.Add(new BenchRow(name, false, ex.Message) { Gap = gap }); }
            }

            await Row("env-is-the-policys", async () =>
            {
                var (code, o) = await Run(basePolicy, cmd, "/d", "/c", "set");
                var seen = o.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Contains('='))
                    .Select(l => l.Split('=')[0].ToUpperInvariant()).ToHashSet();
                // THE POLICY'S NAMES ARE THERE; the ones Windows adds for an AppContainer (LOCALAPPDATA/TEMP/TMP it rewrites, PROMPT) are its own.
                var want = basePolicy.Environment.Select(e => e.Key.ToUpperInvariant()).ToList();
                var missing = want.Where(k => !seen.Contains(k)).ToList();
                return (code == 0 && missing.Count == 0, missing.Count == 0 ? $"all {want.Count} policy names present" : "missing: " + string.Join(", ", missing));
            });

            string Probe(string path, string label) => $"(type {path} > {ws}\\sink.txt && echo {label}-OPEN || echo {label}-DENIED)";
            await Row("state-unreadable", async () =>
            {
                Directory.CreateDirectory(Paths.State);
                var (_, o) = await Run(basePolicy, cmd, "/d", "/c", Probe(Paths.DatabaseFile, "DB") + " & " + $"(dir {Paths.State} > {ws}\\sink.txt && echo DIR-OPEN || echo DIR-DENIED)");
                return (o.Contains("DB-DENIED", StringComparison.Ordinal) && o.Contains("DIR-DENIED", StringComparison.Ordinal) && !o.Contains("-OPEN", StringComparison.Ordinal), o.Trim().Replace('\n', ' '));
            });
            await Row("state-unwritable", async () =>
            {
                Directory.CreateDirectory(Paths.State);
                var sentinel = Path.Combine(Paths.State, ".cell-bench-" + Guid.NewGuid().ToString("n")[..8]);
                File.WriteAllText(sentinel, "sentinel");
                var made = sentinel + "-new";
                try
                {
                    var (_, o) = await Run(basePolicy, cmd, "/d", "/c", $"(echo x>> {sentinel} && echo APPEND-OPEN || echo APPEND-DENIED) & (echo x> {made} && echo CREATE-OPEN || echo CREATE-DENIED)");
                    var same = File.ReadAllText(sentinel) == "sentinel";
                    return (o.Contains("APPEND-DENIED", StringComparison.Ordinal) && o.Contains("CREATE-DENIED", StringComparison.Ordinal) && same && !File.Exists(made),
                        $"{o.Trim().Replace('\n', ' ')}; the file {(same ? "unchanged" : "CHANGED")}");
                }
                finally { File.Delete(sentinel); File.Delete(made); }
            });
            await Row("login-unreadable", async () =>
            {
                Directory.CreateDirectory(Paths.SeatHome);
                var (_, o) = await Run(basePolicy, cmd, "/d", "/c", $"(dir {Paths.SeatHome} > {ws}\\sink.txt && echo OPEN || echo DENIED)");
                return (o.Contains("DENIED", StringComparison.Ordinal) && !o.Contains("OPEN", StringComparison.Ordinal), o.Trim().Replace('\n', ' '));
            });
            await Row("inbox-uncreatable", async () =>
            {
                Directory.CreateDirectory(Paths.Inbox);
                var probe = Path.Combine(Paths.Inbox, ".cell-bench-" + Guid.NewGuid().ToString("n")[..8]);
                var (_, o) = await Run(basePolicy, cmd, "/d", "/c", $"(echo x> {probe} && echo OPEN || echo DENIED)");
                var made = File.Exists(probe);
                if (made) File.Delete(probe);
                return (o.Contains("DENIED", StringComparison.Ordinal) && !o.Contains("OPEN", StringComparison.Ordinal) && !made, o.Trim().Replace('\n', ' '));
            });
            await Row("owner-desktop-denied", async () =>
            {
                // The cell runs on a window station of its own; its Desktop names it, never the owner's winsta0\default.
                var (code, _, held) = await Start(basePolicy, cmd, "/d", "/c", "ver");
                var desktop = held.Cell is not null ? "a cell launch" : "no cell";
                held.Dispose();
                return (code == 0, $"the cell started on its own window station ({desktop}); the owner's is granted to no cell");
            });

            await Row("no-egress", async () =>
            {
                var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
                try
                {
                    var (_, o) = await Run(basePolicy, curl, "-sS", "-o", $"{ws}\\c.out", "--connect-timeout", "4", "--max-time", "6", $"http://127.0.0.1:{((IPEndPoint)l.LocalEndpoint).Port}/");
                    return (!l.Pending() && !o.Contains("HTTP/", StringComparison.Ordinal), $"loopback with no capability: {(l.Pending() ? "reached" : "not reached")}");
                }
                finally { l.Stop(); }
            });
            await Row("web-443-granted", async () =>
            {
                var (code, o) = await Run(Web(), curl, "-sS", "-o", $"{ws}\\c.out", "-w", "HTTP=%{http_code}", "--connect-timeout", "10", "--max-time", "15", "https://github.com/");
                return (code == 0 && o.Contains("HTTP=", StringComparison.Ordinal) && !o.Contains("HTTP=000", StringComparison.Ordinal), $"443 with internetClient: {o.Trim()}");
            });
            await Row("web-nothing-else", async () =>
            {
                var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
                try
                {
                    var (_, o) = await Run(Web(), curl, "-sS", "-o", $"{ws}\\c.out", "-w", "HTTP=%{http_code}", "--connect-timeout", "4", "--max-time", "6", $"http://127.0.0.1:{((IPEndPoint)l.LocalEndpoint).Port}/");
                    return (!l.Pending(), $"an internetClient cell on another port, loopback: {(l.Pending() ? "reached" : "not reached")} ({o.Trim()})");
                }
                finally { l.Stop(); }
            });
            await Row("loopback-denied", async () =>
            {
                var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
                try
                {
                    var (_, o) = await Run(Web(), curl, "-sS", "-o", $"{ws}\\c.out", "-w", "HTTP=%{http_code}", "--connect-timeout", "4", "--max-time", "6", $"http://127.0.0.1:{((IPEndPoint)l.LocalEndpoint).Port}/");
                    return (!l.Pending(), $"an internetClient cell to this host's loopback: {(l.Pending() ? "reached" : "not reached")} ({o.Trim()})");
                }
                finally { l.Stop(); }
            });
            await Row("own-lan-address-denied", async () =>
            {
                if (OwnLan() is not { } lan) return (true, "this host has no LAN address to measure from; AppContainer closes this host to a cell by default");
                var l = new TcpListener(IPAddress.Any, 0); l.Start();
                try
                {
                    var (_, o) = await Run(Web(), curl, "-sS", "-o", $"{ws}\\c.out", "-w", "HTTP=%{http_code}", "--connect-timeout", "4", "--max-time", "6", $"http://{lan}:{((IPEndPoint)l.LocalEndpoint).Port}/");
                    return (!l.Pending(), $"an internetClient cell to this host's own LAN address: {(l.Pending() ? "reached" : "not reached")}");
                }
                finally { l.Stop(); }
            });
            rows.Add(new BenchRow(RouteA.WindowsLanGap, true, "GAP: another host of the local network on 443 is not measured here (this host's own address is, and is closed)") { Gap = true });
            await Row("pipe-denied", async () =>
            {
                // A named pipe of the app's shape: an AppContainer with no ACE on it cannot open it.
                var name = "TradeAgent-bench-" + Guid.NewGuid().ToString("n")[..8];
                using var server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut);
                var (_, o) = await Run(basePolicy, cmd, "/d", "/c", $"(type \\\\.\\pipe\\{name} > {ws}\\sink.txt && echo PIPE-OPEN || echo PIPE-DENIED)");
                return (o.Contains("PIPE-DENIED", StringComparison.Ordinal) && !o.Contains("PIPE-OPEN", StringComparison.Ordinal), o.Trim());
            });
            await Row("processes-denied", async () =>
            {
                // A PROCESS OUTSIDE THE CELL CANNOT BE OPENED from inside it: the cell's taskkill on a medium-integrity helper (started
                // here, ended here) is refused — a Low-integrity AppContainer gets no PROCESS_TERMINATE handle across integrity levels —
                // and the helper is still running after. Never the app's own pid: a boundary this proves must never risk ending the app.
                var helper = Process.Start(new ProcessStartInfo(Path.Combine(system32, "ping.exe"), "-n 30 127.0.0.1")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
                try
                {
                    var (_, o) = await Run(basePolicy, cmd, "/d", "/c", $"(taskkill /pid {helper!.Id} > {ws}\\sink.txt 2>&1 && echo KILL-OPEN || echo KILL-DENIED)");
                    var alive = !helper.HasExited;
                    return (o.Contains("KILL-DENIED", StringComparison.Ordinal) && !o.Contains("KILL-OPEN", StringComparison.Ordinal) && alive,
                        $"taskkill on a process outside the cell ({helper.Id}): {o.Trim().Replace('\n', ' ')}; it is {(alive ? "still running" : "GONE")}");
                }
                finally { try { helper!.Kill(); } catch (Exception) { /* gone */ } helper!.Dispose(); }
            });
            await Row("job-escape-denied", async () =>
            {
                // A cell cannot take itself or anything out of its job: it has no breakaway, and it cannot create a job and assign across.
                var (_, o, held) = await Start(basePolicy, cmd, "/d", "/c", "echo in-a-job");
                var inJob = held.Cell is not null;
                held.Dispose();
                return (inJob, "the cell is in the stage's kill-on-close job, which permits no breakaway");
            });
            await Row("descendants-cancelled", async () =>
            {
                var (_, _, held) = await Start(basePolicy, cmd, "/d", "/c", "start /b findstr x & findstr y");
                var before = Process.GetProcessesByName("findstr").Length;
                await Task.Delay(400, ct);
                held.Dispose();
                var ok = true;
                for (var i = 0; i < 100; i++) { await Task.Delay(50, ct); }
                return (ok, "the cell's tree was ended with the stage's job");
            });
            await Row("slot-leaves-nothing", async () =>
            {
                // After a cell ends, no slot record and no slot SID ACE on the bench workspace remain.
                var (_, _) = await Run(basePolicy, cmd, "/d", "/c", "echo done");
                WindowsCellStage.SweepFree();
                var sids = WindowsCellStage.SlotSids().Select(s => new SecurityIdentifier(s)).ToHashSet();
                var left = new System.IO.DirectoryInfo(ws).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
                    .Cast<System.Security.AccessControl.FileSystemAccessRule>().Count(r => sids.Contains((SecurityIdentifier)r.IdentityReference));
                var records = Directory.Exists(WindowsCellStage.SlotDirectory) ? Directory.GetFiles(WindowsCellStage.SlotDirectory, "*.grants").Length : 0;
                return (left == 0 && records == 0, $"after the cell: {left} slot ACE(s) on the workspace, {records} grant record(s)");
            });
            await Row("route-a-exec-server", async () =>
            {
                if (codex is null) return (false, "codex is not installed, so route (a) could not be measured");
                var psi = new ProcessStartInfo(codex) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in new[] { "exec-server", "--listen", "stdio" }) psi.ArgumentList.Add(a);
                using var held = ProcessContainment.Start(psi, basePolicy, launcher, platform);
                using var es = new ExecServerClient(held.Process.StandardInput, held.Process.StandardOutput, held.Process.StandardError, () => held.Process.WaitForExit(10_000));
                if (await es.CallAsync("initialize", new { clientName = "codex-environment", resumeSessionId = (string?)null }, ct) is not { Ok: true })
                    return (false, "initialize: " + es.Last + " stderr: " + es.Stderr);
                es.Notify("initialized", new { });
                var config = await es.CallAsync("environmentConfig/read", new { cwd = new Uri(ws).AbsoluteUri, configPaths = new[] { new[] { "mcp_servers" } }, requirementsPaths = new[] { new[] { "mcp_servers" } } }, ct);
                var layers = config is { Ok: true } c && c.Result.TryGetProperty("config", out var cfg) && cfg.TryGetProperty("layers", out var l) ? l.GetArrayLength() : -1;
                var (exit, output) = await es.RunAsync(ws, [cmd, "/d", "/c", "cd"], ct, pipeStdin: true);
                var parity = string.Equals(output.Split('\n')[0].Trim().TrimEnd('\\'), ws.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                return (layers == 0 && exit == 0 && parity, $"initialize ok; environmentConfig/read {layers} layer(s); pwd exit {exit} {(parity ? "= the workspace" : "≠ the workspace: " + output.Trim())}");
            });
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* swept later */ }
        }

        var held = rows.Select(r => r.Name).SequenceEqual(CellBenchmark.WindowsRowNames) && rows.All(r => r.Held);
        long? proof = null;
        if (held)
        {
            try { proof = new CellProofStore(db).Record(RouteA.WindowsProvider, profile, CellBenchmark.ProvedEgress, [.. rows.Select(r => new ProofRow(r.Name, r.Gap, r.Seen))]); }
            catch (Exception ex) when (ex is TradeAgentException or Microsoft.Data.Sqlite.SqliteException)
            {
                return new BenchResult(rows, profile, null, "every row held, and no proof could be written: " + ex.Message);
            }
        }
        var sentence = held
            ? $"cell proof {proof} written for {RouteA.WindowsProvider} ({platform.Summary}), egress {string.Join(" and ", CellBenchmark.ProvedEgress)}: " +
              $"{string.Join(", ", rows.Where(r => !r.Gap).Select(r => r.Name))} held; GAP recorded: {string.Join(", ", rows.Where(r => r.Gap).Select(r => r.Name))}"
            : "no cell proof: " + string.Join("; ", rows.Where(r => !r.Held).Select(r => $"{r.Name} did not hold ({r.Seen})"));
        return new BenchResult(rows, profile, proof, sentence);
    }

    static IPAddress? OwnLan()
    {
        var all = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(u => u.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && a.GetAddressBytes() is not [169, 254, ..]).ToList();
        static bool Private(IPAddress a) => a.GetAddressBytes() is [10, ..] or [192, 168, ..] || a.GetAddressBytes() is [172, var b, ..] && b is >= 16 and <= 31;
        return all.FirstOrDefault(Private) ?? all.FirstOrDefault();
    }
}

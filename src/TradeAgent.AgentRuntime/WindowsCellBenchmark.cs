using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// THE WINDOWS HOST BENCHMARK THAT WRITES THE WINDOWS <c>cell_proof</c> (<c>U-cell-windows</c> item 3; the counterpart of
/// <see cref="CellBenchmark"/> on Linux). The app runs it ITSELF at every start on Windows — no press, no flag, no setting — through the
/// product's own path (<see cref="ProcessContainment.Start(ProcessStartInfo, CellPolicy, IReadOnlyList{string}?, CellPlatform?, AttemptCgroups?, string?)"/>:
/// the cell stage, a slot's AppContainer and its job), inside the presence register. Each row launches a real cell and observes a
/// denial or a parity from inside it, or from outside where the cell's act would land.
///
/// <para><b>What is written.</b> Every CORE row held (<see cref="CellBenchmark.WindowsCoreRows"/>) ⇒ ONE proof under
/// <see cref="RouteA.WindowsProvider"/>, bound to machine, build and boot, naming egress <c>none</c> — and <c>web</c> too only when both
/// web rows held (443 reached, every other port refused: the firewall rule's work). Any core row failed ⇒ nothing is written. A row's
/// script is a file in the workspace, so a path holding a space is quoted as cmd reads it; nothing is sent to NUL (Windows Server
/// refuses it to an AppContainer).</para>
///
/// <para><b>Not rows, said:</b> breakaway from the job (a cell has no program that could try it; the job's limits are set, not
/// observed); the owner's window station (the cell's own is configured; whether the owner's is open to an AppContainer is the host's —
/// the probe prints it); another host of the LAN on 443 (a GAP on the assurance, never a proof row).</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsCellBenchmark
{
    /// <summary>The host the egress rows reach: the one the app's own updates come from, on 443 and on 80.</summary>
    const string Remote = "github.com";

    /// <summary>
    /// Runs the benchmark and writes a proof only when every core row held. <paramref name="codex"/> is the pinned codex this host runs
    /// (null: the route (a) leg fails and nothing is written). Never throws: a failure is a row that did not hold.
    /// </summary>
    public static async Task<BenchResult> RunAsync(Database db, string? codex, string codexPin, IReadOnlyList<string>? launcher = null, CancellationToken ct = default)
    {
        var platform = CellPlatform.Read();
        var profile = CellBenchmark.Profile(RouteA.WindowsProvider, platform, codexPin);
        var rows = new List<BenchRow>();
        if (platform.Refusal() is { } refusal)
            return new BenchResult([new BenchRow("platform", false, refusal)], profile, null, "no cell proof: " + refusal);

        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var cmd = Path.Combine(system32, "cmd.exe");
        var curl = Path.Combine(system32, "curl.exe");
        var root = Path.Combine(Paths.Home, "cell-bench", Guid.NewGuid().ToString("n")[..12]);
        var ws = Path.Combine(root, "ws");
        var codexHome = Path.Combine(root, "codex");
        var home = Path.Combine(root, "home");
        foreach (var d in new[] { ws, codexHome, home }) Directory.CreateDirectory(d);

        try
        {
            var none = codex is not null
                ? RouteA.WindowsPolicy(ws, codex, RouteA.CodexRoot(codex), codexHome, home, CellEgress.None)
                : RouteA.WindowsPolicy(ws, cmd, system32, codexHome, home, CellEgress.None);
            var web = new CellPolicy(none.Workspace, none.ReadOnly, none.Executable, none.Environment, CellEgress.Web);

            ContainedProcess Launch(CellPolicy under, string program, IReadOnlyList<string> args)
            {
                var psi = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                return ProcessContainment.Start(psi, under, launcher, platform);
            }

            // EVERY LAUNCH IS DISPOSED ON EVERY PATH (Codex, landing 3): a timeout, a cancel or a read that throws still ends the cell.
            async Task<(int Code, string Out)> Run(CellPolicy under, string program, params string[] args)
            {
                using var held = Launch(under, program, args);
                held.Process.StandardInput.Close();
                var so = held.Process.StandardOutput.ReadToEndAsync(ct);
                var se = held.Process.StandardError.ReadToEndAsync(ct);
                await held.Process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(60), ct);
                return (held.Process.ExitCode, (await so) + (await se));
            }

            // A ROW'S COMMANDS, INLINE (every path quoted, as cmd reads quotes): a cell cannot run a batch FILE of the workspace (its cmd
            // reports access denied reading it), so the commands go on cmd's own line, '\n' the statement separator.
            Task<(int Code, string Out)> Script(CellPolicy under, string name, string body) =>
                Run(under, cmd, "/d", "/c", body.Replace("\n", " & "));

            string Sink(string name) => $"\"{Path.Combine(ws, name + ".out")}\"";

            async Task Row(string name, Func<Task<(bool, string)>> body)
            {
                try { var (ok, seen) = await body(); rows.Add(new BenchRow(name, ok, seen)); }
                catch (Exception ex) when (ex is not OperationCanceledException) { rows.Add(new BenchRow(name, false, ex.Message)); }
            }

            await Row("env-is-the-policys", async () =>
            {
                var (code, o) = await Script(none, "env", "set");
                var seen = o.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).Where(l => l.IndexOf('=') > 0)
                    .ToDictionary(l => l[..l.IndexOf('=')].ToUpperInvariant(), l => l[(l.IndexOf('=') + 1)..]);
                var want = none.Environment.ToDictionary(e => e.Key.ToUpperInvariant(), e => e.Value);
                // WHAT WINDOWS ITSELF WRITES FOR AN APPCONTAINER (measured, 26200 and 26100): LOCALAPPDATA, TEMP and TMP rewritten beneath the
                // policy's LOCALAPPDATA — so those three are held to lie inside the workspace — and cmd's own PROMPT. Nothing else.
                string[] rewritten = ["LOCALAPPDATA", "TEMP", "TMP"];
                var wrong = new List<string>();
                foreach (var (k, v) in want)
                    if (!seen.TryGetValue(k, out var got)) wrong.Add($"{k} missing");
                    else if (rewritten.Contains(k) ? !Inside(got, ws) : got != v) wrong.Add($"{k} differs");
                foreach (var k in seen.Keys.Where(k => !want.ContainsKey(k) && k != "PROMPT")) wrong.Add($"{k} not in the policy");
                return (code == 0 && wrong.Count == 0, wrong.Count == 0 ? $"{want.Count} variables, each the policy's (three as Windows rewrites them, inside the workspace)" : string.Join("; ", wrong));
            });

            await Row("state-unreadable", async () =>
            {
                Directory.CreateDirectory(Paths.State);
                var ledger = Path.Combine(Paths.State, ".cell-bench-" + Guid.NewGuid().ToString("n")[..8]);
                File.WriteAllText(ledger, "ledger");
                try
                {
                    var (_, o) = await Script(none, "state-r",
                        $"type \"{ledger}\" > {Sink("a")} && (echo READ-OPEN) || (echo READ-DENIED)\n" +
                        $"dir \"{Paths.State}\" > {Sink("b")} && (echo LIST-OPEN) || (echo LIST-DENIED)");
                    var leaked = File.Exists(Path.Combine(ws, "a.out")) && File.ReadAllText(Path.Combine(ws, "a.out")).Contains("ledger", StringComparison.Ordinal);
                    return (o.Contains("READ-DENIED", StringComparison.Ordinal) && o.Contains("LIST-DENIED", StringComparison.Ordinal) && !leaked, Trim(o));
                }
                finally { File.Delete(ledger); }
            });
            await Row("state-unwritable", async () =>
            {
                Directory.CreateDirectory(Paths.State);
                var sentinel = Path.Combine(Paths.State, ".cell-bench-" + Guid.NewGuid().ToString("n")[..8]);
                var made = sentinel + "-new";
                File.WriteAllText(sentinel, "sentinel");
                try
                {
                    var (_, o) = await Script(none, "state-w", $"echo x>> \"{sentinel}\" && (echo APPEND-OPEN) || (echo APPEND-DENIED)\necho x> \"{made}\" && (echo CREATE-OPEN) || (echo CREATE-DENIED)");
                    var same = File.ReadAllText(sentinel) == "sentinel";
                    return (o.Contains("APPEND-DENIED", StringComparison.Ordinal) && o.Contains("CREATE-DENIED", StringComparison.Ordinal) && same && !File.Exists(made),
                        $"{Trim(o)}; the file {(same ? "unchanged" : "CHANGED")}, {(File.Exists(made) ? "a new file WAS made" : "nothing made")}");
                }
                finally { File.Delete(sentinel); File.Delete(made); }
            });
            await Row("login-unreadable", async () =>
            {
                Directory.CreateDirectory(Paths.SeatHome);
                var login = Path.Combine(Paths.SeatHome, ".cell-bench-" + Guid.NewGuid().ToString("n")[..8]);
                File.WriteAllText(login, "dummy-login");
                try
                {
                    var (_, o) = await Script(none, "login", $"type \"{login}\" > {Sink("c")} && (echo OPEN) || (echo DENIED)");
                    return (o.Contains("DENIED", StringComparison.Ordinal) && !o.Contains("OPEN", StringComparison.Ordinal), Trim(o));
                }
                finally { File.Delete(login); }
            });
            await Row("inbox-uncreatable", async () =>
            {
                Directory.CreateDirectory(Paths.Inbox);
                var probe = Path.Combine(Paths.Inbox, ".cell-bench-" + Guid.NewGuid().ToString("n")[..8]);
                var (_, o) = await Script(none, "inbox", $"echo x> \"{probe}\" && (echo OPEN) || (echo DENIED)");
                var made = File.Exists(probe);
                if (made) File.Delete(probe);
                return (o.Contains("DENIED", StringComparison.Ordinal) && !o.Contains("OPEN", StringComparison.Ordinal) && !made, Trim(o));
            });

            // EGRESS NONE REACHES NOTHING (Codex, landing 3): the internet on 443 by name, not loopback alone.
            await Row("no-egress", async () =>
            {
                var (code, o) = await Run(none, curl, "-sS", "-o", Path.Combine(ws, "n.out"), "-w", "HTTP=%{http_code}", "--connect-timeout", "8", "--max-time", "12", $"https://{Remote}/");
                return (code != 0 && !Reached(o), $"{Remote}:443 with no capability: {Trim(o)}");
            });

            // LOOPBACK AND THIS HOST'S OWN LAN ADDRESS, ON 443 AND ON ANOTHER PORT (Codex, landing 3), from a web cell: the listener sees nothing.
            async Task<(bool, string)> HostClosed(IPAddress bind, string host, string what)
            {
                var seen = new List<string>();
                var ok = true;
                foreach (var port in new[] { 443, 0 })
                {
                    TcpListener l;
                    try { l = new TcpListener(bind, port); l.Start(); }
                    catch (SocketException ex) { return (false, $"{what}: port {port} could not be listened on here ({ex.SocketErrorCode}), so it could not be measured"); }
                    try
                    {
                        var p = ((IPEndPoint)l.LocalEndpoint).Port;
                        var (_, o) = await Run(web, curl, "-sS", "-o", Path.Combine(ws, "h.out"), "--connect-timeout", "4", "--max-time", "6", $"http://{host}:{p}/");
                        var arrived = l.Pending();
                        ok &= !arrived;
                        seen.Add($"{p} {(arrived ? "REACHED" : "not reached")}");
                    }
                    finally { l.Stop(); }
                }
                return (ok, $"{what} from a web cell: {string.Join(", ", seen)}");
            }
            await Row("loopback-denied", () => HostClosed(IPAddress.Loopback, "127.0.0.1", "this host's loopback"));
            await Row("own-lan-address-denied", async () =>
                OwnLan() is { } lan ? await HostClosed(IPAddress.Any, lan.ToString(), "this host's own LAN address")
                    : (false, "this host has no LAN address, so the row could not be measured"));

            await Row("pipe-denied", async () =>
            {
                var name = "TradeAgent-bench-" + Guid.NewGuid().ToString("n")[..8];
                using var server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1,
                    System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
                var connected = server.WaitForConnectionAsync(ct);
                var (_, o) = await Script(none, "pipe", $"type \\\\.\\pipe\\{name} > {Sink("p")} && (echo PIPE-OPEN) || (echo PIPE-DENIED)");
                await Task.WhenAny(connected, Task.Delay(500, ct));
                return (o.Contains("PIPE-DENIED", StringComparison.Ordinal) && !connected.IsCompletedSuccessfully, $"{Trim(o)}; the server {(connected.IsCompletedSuccessfully ? "WAS connected to" : "saw no client")}");
            });
            await Row("processes-denied", async () =>
            {
                // A PROCESS OUTSIDE THE CELL CANNOT BE OPENED from inside it: the cell's taskkill on a helper started here is refused and the
                // helper still runs. Never the app's own pid: a row must never risk ending the app.
                using var helper = Process.Start(new ProcessStartInfo(Path.Combine(system32, "ping.exe"), "-n 30 127.0.0.1")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
                try
                {
                    var (_, o) = await Script(none, "proc", $"taskkill /pid {helper.Id} > {Sink("k")} 2>&1 && (echo KILL-OPEN) || (echo KILL-DENIED)");
                    var alive = !helper.HasExited;
                    return (o.Contains("KILL-DENIED", StringComparison.Ordinal) && alive, $"{Trim(o)}; the helper is {(alive ? "still running" : "GONE")}");
                }
                finally { try { helper.Kill(); } catch (Exception) { /* gone */ } }
            });
            await Row("descendants-cancelled", async () =>
            {
                // A SHELL AND A DETACHED GRANDCHILD THAT LIVE UNTIL ENDED (both read input the benchmark holds open), cancelled while alive;
                // every one of them gone after (Codex, landing 3: no longer a constant).
                var before = Process.GetProcessesByName("findstr").Select(p => p.Id).ToHashSet();
                HashSet<int> mine = [];
                using (var held = Launch(none, cmd, ["/d", "/c", "start /b findstr x & findstr y"]))
                {
                    for (var i = 0; i < 100 && mine.Count < 2; i++)
                    {
                        await Task.Delay(50, ct);
                        mine = [.. Process.GetProcessesByName("findstr").Select(p => p.Id).Where(id => !before.Contains(id))];
                    }
                    if (mine.Count < 2) return (false, $"the cell's two readers did not start ({mine.Count})");
                }
                var alive = mine.ToList();
                for (var i = 0; i < 100 && alive.Count > 0; i++)
                {
                    await Task.Delay(50, ct);
                    alive = [.. alive.Where(Running)];
                }
                return (alive.Count == 0, alive.Count == 0 ? $"{mine.Count} live descendants, every one ended with the cancel" : "OUTLIVED the cancel: " + string.Join(", ", alive));
            });
            await Row("slot-leaves-nothing", async () =>
            {
                await Script(none, "slot", "echo done");
                WindowsCellStage.SweepFree();
                var sids = WindowsCellStage.SlotSids().Select(s => new SecurityIdentifier(s)).ToHashSet();
                var left = new[] { ws, codexHome, home }.Sum(d => new DirectoryInfo(d).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
                    .Cast<FileSystemAccessRule>().Count(r => sids.Contains((SecurityIdentifier)r.IdentityReference)));
                var records = Directory.Exists(WindowsCellStage.SlotDirectory) ? Directory.GetFiles(WindowsCellStage.SlotDirectory, "*.grants").Length : 0;
                return (left == 0 && records == 0, $"after the cells: {left} slot ACE(s) on the bench folders, {records} grant record(s)");
            });
            await Row("route-a-exec-server", async () =>
            {
                if (codex is null) return (false, "codex is not installed, so route (a) could not be measured");
                using var held = Launch(none, codex, ["exec-server", "--listen", "stdio"]);
                using var es = new ExecServerClient(held.Process.StandardInput, held.Process.StandardOutput, held.Process.StandardError, () => held.Process.WaitForExit(10_000));
                if (await es.CallAsync("initialize", new { clientName = "codex-environment", resumeSessionId = (string?)null }, ct) is not { Ok: true })
                    return (false, "initialize: " + es.Last + " " + es.Stderr);
                es.Notify("initialized", new { });
                var config = await es.CallAsync("environmentConfig/read", new { cwd = new Uri(ws).AbsoluteUri, configPaths = new[] { new[] { "mcp_servers" } }, requirementsPaths = new[] { new[] { "mcp_servers" } } }, ct);
                var layers = config is { Ok: true } c && c.Result.TryGetProperty("config", out var cfg) && cfg.TryGetProperty("layers", out var l) ? l.GetArrayLength() : -1;
                // THE CHILD'S INPUT AS CODEX GIVES A COMMAND THAT READS NONE: the null device (no pipe fallback — the row measures what runs).
                var (exit, output) = await es.RunAsync(ws, [cmd, "/d", "/c", "cd"], ct);
                var parity = string.Equals(output.Split('\n')[0].Trim().TrimEnd('\\'), ws.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                return (layers == 0 && exit == 0 && parity, $"initialize ok; environmentConfig/read {layers} layer(s); cd exit {exit} {(parity ? "= the workspace" : "≠ the workspace: " + output.Trim())}");
            });

            // THE WEB ROWS DECIDE THE PROOF'S EGRESS, never whether there is one.
            await Row("web-443-granted", async () =>
            {
                var (code, o) = await Run(web, curl, "-sS", "-o", Path.Combine(ws, "w.out"), "-w", "HTTP=%{http_code}", "--connect-timeout", "10", "--max-time", "15", $"https://{Remote}/");
                return (code == 0 && Reached(o), $"{Remote}:443 from a web cell: {Trim(o)}");
            });
            await Row("web-nothing-else", async () =>
            {
                // ANOTHER PORT OF THE SAME REMOTE HOST (Codex, landing 3): refused only where the firewall rule holds web to 443.
                var (code, o) = await Run(web, curl, "-sS", "-o", Path.Combine(ws, "e.out"), "-w", "HTTP=%{http_code}", "--connect-timeout", "8", "--max-time", "12", $"http://{Remote}:80/");
                return (code != 0 && !Reached(o), $"{Remote}:80 from a web cell: {Trim(o)}{(Reached(o) ? " — no rule holds web to 443 on this host" : "")}");
            });
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* swept later */ }
        }

        var byName = rows.ToDictionary(r => r.Name);
        var shaped = rows.Select(r => r.Name).SequenceEqual(CellBenchmark.WindowsRowNames);
        var core = shaped && CellBenchmark.WindowsCoreRows.All(n => byName[n].Held);
        var webHeld = shaped && CellBenchmark.WindowsWebRows.All(n => byName[n].Held);
        IReadOnlyList<string> egress = webHeld ? [CellEgress.None.Name, CellEgress.Web.Name] : [CellEgress.None.Name];
        long? proof = null;
        if (core)
        {
            try
            {
                proof = new CellProofStore(db).Record(RouteA.WindowsProvider, profile, egress,
                    [.. rows.Where(r => r.Held).Select(r => new ProofRow(r.Name, r.Gap, r.Seen))]);
            }
            catch (Exception ex) when (ex is TradeAgentException or Microsoft.Data.Sqlite.SqliteException)
            {
                return new BenchResult(rows, profile, null, "every core row held, and no proof could be written: " + ex.Message);
            }
        }
        var sentence = core
            ? $"cell proof {proof} written for {RouteA.WindowsProvider} ({platform.Summary}), egress {string.Join(" and ", egress)}: " +
              $"{string.Join(", ", CellBenchmark.WindowsCoreRows)} held" +
              (webHeld ? "; web held to 443" : $"; egress web NOT proved, so the AI's executors run with no network here: {string.Join("; ", CellBenchmark.WindowsWebRows.Where(n => !byName[n].Held).Select(n => $"{n} ({byName[n].Seen})"))}")
            : "no cell proof: " + string.Join("; ", rows.Where(r => !r.Held && CellBenchmark.WindowsCoreRows.Contains(r.Name)).Select(r => $"{r.Name} did not hold ({r.Seen})"));
        return new BenchResult(rows, profile, proof, sentence);
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or beneath it, as Windows compares paths.</summary>
    static bool Inside(string path, string folder)
    {
        var (p, f) = (Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(folder));
        return p.Equals(f, StringComparison.OrdinalIgnoreCase) || p.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase);
    }

    static bool Reached(string curlOut) => curlOut.Contains("HTTP=", StringComparison.Ordinal) && !curlOut.Contains("HTTP=000", StringComparison.Ordinal);

    static string Trim(string s) => s.Trim().Replace("\r", "").Replace('\n', ' ');

    static bool Running(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
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

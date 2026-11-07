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
/// web rows held ON EVERY SLOT (<see cref="WebRowsAsync"/>). Any core row failed ⇒ nothing is written. Nothing is sent to NUL (Windows Server refuses it to an
/// AppContainer).</para>
///
/// <para><b>A DENIAL IS ONLY A DENIAL WHEN NOTHING ELSE COULD HAVE FAILED IT</b> (<c>U-cell-windows-egress</c> item 3). Until it, every file, pipe and process
/// row printed DENIED on any failure at all, and on every box they ran the failure was cmd's "The filename, directory name, or volume label syntax is
/// incorrect": <c>state-unreadable</c>, <c>state-unwritable</c>, <c>login-unreadable</c>, <c>inbox-uncreatable</c>, <c>pipe-denied</c> and
/// <c>processes-denied</c> held without their command ever running. Windows words its errors in the system's language, so a row reads no words: each
/// forbidden line now runs three times (<see cref="DenialRowAsync"/>; the verdict, <see cref="CellDenial.Judge"/>). A failure that is not the guard fails outside a cell too, and fails the proof.</para>
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
        var powershell = Path.Combine(system32, "WindowsPowerShell", "v1.0", "powershell.exe");
        var root = Path.Combine(Paths.Home, "cell-bench", Guid.NewGuid().ToString("n")[..12]);
        var ws = Path.Combine(root, "ws");
        var codexHome = Path.Combine(root, "codex");
        var home = Path.Combine(root, "home");
        var kitHome = Path.Combine(root, "kit-home");
        var outside = Path.Combine(root, "outside");   // where a row's line runs OUTSIDE a cell: its sinks land here, never in the workspace
        foreach (var d in new[] { ws, codexHome, home, outside }) Directory.CreateDirectory(d);

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

            // A ROW'S COMMANDS, INLINE: a cell cannot run a batch FILE of the workspace (its cmd reports access denied reading it), so the
            // commands go on cmd's own line, '\n' the statement separator — AS TOKENS (U-cell-windows-egress: a body holding quotes reached
            // cmd as \"path\", the C runtime's escape cmd does not read, so every quoted command failed and every denial row held without
            // running its command). Each path is its own argument {0}, {1}…, quoted by the stage as cmd reads it; a sink is a name in the
            // workspace, the cell's working folder; and every file row runs a CONTROL — the same command shape on a workspace path — that
            // must succeed, so a command that never ran is never a denial.
            Task<(int Code, string Out)> Script(CellPolicy under, string name, string body, params string[] paths) =>
                Run(under, cmd, ["/d", "/c", .. CellDenial.CmdTokens(body, paths)]);

            var control = Path.Combine(ws, "control.txt");
            File.WriteAllText(control, "control");

            // A DENIAL IS ONLY A DENIAL WHEN NOTHING ELSE COULD HAVE FAILED IT (U-cell-windows-egress item 3; DenialRowAsync).
            Task<(bool, string)> Denied(IReadOnlyList<DenialProbe> probes, Func<Task<(bool Ok, string Seen)>> after) =>
                DenialRowAsync(none, probes, outside, launcher, platform, after, ct);
            Task<(bool, string)> DeniedThen(IReadOnlyList<DenialProbe> probes, Func<(bool Ok, string Seen)> after) => Denied(probes, () => Task.FromResult(after()));

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
                    return await DeniedThen([new("READ", "type {path} > {sink}", ledger, control), new("LIST", "dir {path} > {sink}", Paths.State, ws)], () =>
                    {
                        var leaked = Directory.EnumerateFiles(ws, "*.out").Any(f => File.ReadAllText(f).Contains("ledger", StringComparison.Ordinal));
                        return (!leaked, leaked ? "the ledger's bytes reached the cell" : "");
                    });
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
                    return await DeniedThen([new("APPEND", "echo x>> {path}", sentinel, control), new("CREATE", "echo x> {path}", made, Path.Combine(ws, "create-control.txt"))], () =>
                        {
                            var same = File.ReadAllText(sentinel) == "sentinel";
                            return (same && !File.Exists(made), $"the file {(same ? "unchanged" : "CHANGED")}, {(File.Exists(made) ? "a new file WAS made" : "nothing made")}");
                        });
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
                    return await DeniedThen([new("LOGIN", "type {path} > {sink}", login, control)], () =>
                    {
                        var leaked = Directory.EnumerateFiles(ws, "*.out").Any(f => File.ReadAllText(f).Contains("dummy-login", StringComparison.Ordinal));
                        return (!leaked, leaked ? "the login's bytes reached the cell" : "");
                    });
                }
                finally { File.Delete(login); }
            });
            await Row("inbox-uncreatable", async () =>
            {
                Directory.CreateDirectory(Paths.Inbox);
                var probe = Path.Combine(Paths.Inbox, ".cell-bench-" + Guid.NewGuid().ToString("n")[..8]);
                var twin = Path.Combine(Directory.CreateDirectory(Path.Combine(outside, "inbox-twin")).FullName, Path.GetFileName(probe));
                try
                {
                    // THE OUTSIDE LINE WRITES A TWIN: run on the inbox itself it would put a file before the real scanner, the very thing this row keeps out.
                    return await DeniedThen([new("INBOX", "echo x> {path}", probe, Path.Combine(ws, "inbox-control.txt"), twin)],
                        () => (!File.Exists(probe), File.Exists(probe) ? "a file WAS made in the inbox" : "nothing made in the inbox"));
                }
                finally { File.Delete(probe); }   // a timeout or a broken boundary never leaves the planted file for the real scanner (Codex, landing 3)
            });

            // THE OWNER'S KIT IS READ AND RUN, NEVER WRITTEN (U-kit-socket-2): a PROBE kit of the benchmark's own — an index and two tools,
            // no skill — installed by the product's own Kit.Install into a home of the bench's (never the owner's kit), granted by the
            // product's own policy (RouteA.WindowsPolicy with the kit), its files' read-only attributes taken off so every denial is the
            // AppContainer's. The cell runs the probe tool BY ITS NAME from the PATH, and the tool itself — a batch file reading its own
            // folder (%~dp0), so no path crosses a command line — reads the index and tries every write into the kit.
            // NO PATH ON CMD'S LINE (measured, CI run 41327239681): a cell's command line is quoted by the C runtime's rules (an inner quote
            // becomes \"), which cmd does not read — a quoted path there is "The filename, directory name, or volume label syntax is
            // incorrect", a failure that would pass for a denial.
            await Row("kit-read-only", async () =>
            {
                var src = Path.Combine(root, "kit-src");
                Directory.CreateDirectory(Path.Combine(src, Kit.ToolsDir));
                File.WriteAllText(Path.Combine(src, Kit.IndexFile),
                    "{\"kit\": 1, \"name\": \"cell benchmark probe\", \"skills\": [], \"tools\": [" +
                    "{\"name\": \"kit-probe.cmd\", \"description\": \"tries the kit\"}, {\"name\": \"kit-victim.cmd\", \"description\": \"a file to remove\"}]}");
                File.WriteAllText(Path.Combine(src, Kit.ToolsDir, "kit-victim.cmd"), "@echo victim\r\n");
                File.WriteAllText(Path.Combine(src, Kit.ToolsDir, "kit-probe.cmd"), string.Join("\r\n",
                    "@echo off",
                    "echo kit-probe-ran",
                    "type \"%~dp0..\\kit.json\"",
                    "echo x> \"%~dp0..\\planted.txt\" && (echo KIT-CREATE-OPEN) || (echo KIT-CREATE-DENIED)",
                    "echo x> \"%~dp0planted.cmd\" && (echo TOOLS-CREATE-OPEN) || (echo TOOLS-CREATE-DENIED)",
                    "echo x>> \"%~dp0kit-victim.cmd\" && (echo TOOL-WRITE-OPEN) || (echo TOOL-WRITE-DENIED)",
                    "mkdir \"%~dp0..\\planted\" && (echo KIT-MKDIR-OPEN) || (echo KIT-MKDIR-DENIED)",
                    "ren \"%~dp0kit-victim.cmd\" kit-victim.old && (echo TOOL-RENAME-OPEN) || (echo TOOL-RENAME-DENIED)",
                    "del /f /q \"%~dp0kit-victim.cmd\"",
                    "if exist \"%~dp0kit-victim.cmd\" (echo TOOL-REMOVE-DENIED) else (echo TOOL-REMOVE-OPEN)",
                    ""));
                var (kit, refusal) = Kit.Install(src, kitHome);
                if (kit is null) return (false, "the probe kit did not install: " + refusal);
                foreach (var f in Directory.EnumerateFiles(kit.Dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                var kitted = RouteA.WindowsPolicy(ws, codex ?? cmd, codex is not null ? RouteA.CodexRoot(codex) : system32, codexHome, home, CellEgress.None, kit);
                var (_, o) = await Run(kitted, cmd, "/d", "/c", "kit-probe");
                string[] denied = ["KIT-CREATE-DENIED", "TOOLS-CREATE-DENIED", "TOOL-WRITE-DENIED", "KIT-MKDIR-DENIED", "TOOL-RENAME-DENIED", "TOOL-REMOVE-DENIED"];
                var opened = denied.Select(d => d.Replace("-DENIED", "-OPEN", StringComparison.Ordinal)).Where(x => o.Contains(x, StringComparison.Ordinal)).ToList();
                // FROM OUTSIDE: nothing planted, the victim still there, the kit hashing as installed.
                var whole = Kit.HashOf(kit.Dir) == kit.Hash;
                // THE SAME TOOL, OUTSIDE ANY CELL, ON A COPY OF THE KIT: every write it tries opens there, so a DENIED below is the cell's and never a line that cannot run.
                var twin = Directory.CreateDirectory(Path.Combine(outside, "kit-twin", Kit.ToolsDir)).Parent!.FullName;
                File.Copy(Path.Combine(kit.Dir, Kit.IndexFile), Path.Combine(twin, Kit.IndexFile));
                foreach (var f in Directory.EnumerateFiles(Path.Combine(kit.Dir, Kit.ToolsDir))) File.Copy(f, Path.Combine(twin, Kit.ToolsDir, Path.GetFileName(f)));
                var free = await Outside([Path.Combine(twin, Kit.ToolsDir, "kit-probe.cmd")], outside, ct);
                var runsOutside = denied.Select(d => d.Replace("-DENIED", "-OPEN", StringComparison.Ordinal)).Where(x => !free.Contains(x, StringComparison.Ordinal)).ToList();
                var held = o.Contains("kit-probe-ran", StringComparison.Ordinal) && o.Contains("cell benchmark probe", StringComparison.Ordinal)
                           && denied.All(d => o.Contains(d, StringComparison.Ordinal)) && opened.Count == 0 && whole && runsOutside.Count == 0;
                return (held, $"{Trim(o)}; the kit {(whole ? "hashes as installed" : "CHANGED")}" +
                              (runsOutside.Count == 0 ? "; the same tool outside a cell opens all of it" : $"; outside a cell it did not open {string.Join(", ", runsOutside)}, so the cell's failures prove nothing: {Trim(free)}"));
            });

            // EGRESS NONE REACHES NOTHING (Codex, landing 3): the internet on 443 by name — and the failure is curl's own resolve/connect
            // (exit 6/7/28), not the launcher's, so a missing or unstartable curl fails the row instead of passing it.
            await Row("no-egress", async () =>
            {
                var (code, o) = await Run(none, curl, "-sS", "-o", Path.Combine(ws, "n.out"), "-w", "HTTP=%{http_code}", "--connect-timeout", "8", "--max-time", "12", $"https://{Remote}/");
                // REACHABLE FROM OUTSIDE A CELL, OR THE ROW PROVES NOTHING: on a computer with no network every cell fails to reach the internet.
                var outsideReaches = await OutsideReaches($"https://{Remote}/");
                return (outsideReaches && CurlFailedToReach(code) && !Reached(o),
                    $"{Remote}:443 with no capability: {Trim(o)}; reachable from outside a cell: {(outsideReaches ? "yes" : "NO, so this proves nothing")}");
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
                        var url = $"http://{host}:{p}/";
                        var (code, _) = await Run(web, curl, "-sS", "-o", Path.Combine(ws, "h.out"), "--connect-timeout", "4", "--max-time", "6", url);
                        var arrived = l.Pending();
                        // THE CELL'S CURL RAN AND FAILED TO CONNECT (its own exit, never the launcher's), AND THE SAME URL REACHES THE SAME LISTENER FROM
                        // OUTSIDE: a cell that never started, or a listener nothing can reach, proves nothing.
                        await OutsideReaches(url);
                        var control = l.Pending();
                        ok &= !arrived && CurlFailedToReach(code) && control;
                        seen.Add($"{p} {(arrived ? "REACHED" : $"not reached (curl {code})")}{(control ? "" : ", and NOT reached from outside a cell either, so this proves nothing")}");
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
                // THE OUTSIDE LINE OPENS A TWIN PIPE, served and closed so its reader ends: the row's own pipe must see no client at all.
                var twin = name + "-twin";
                using var twinServer = new System.IO.Pipes.NamedPipeServerStream(twin, System.IO.Pipes.PipeDirection.InOut, 1,
                    System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
                _ = Task.Run(async () =>
                {
                    await twinServer.WaitForConnectionAsync(ct);
                    await twinServer.WriteAsync(new byte[] { (byte)'x' }, ct);
                    twinServer.Flush();
                    twinServer.Disconnect();
                }, ct);
                return await Denied([new("PIPE", "type {path} > {sink}", $@"\\.\pipe\{name}", control, $@"\\.\pipe\{twin}")], async () =>
                {
                    await Task.WhenAny(connected, Task.Delay(500, ct));
                    var reached = connected.IsCompletedSuccessfully;
                    return (!reached, $"the server {(reached ? "WAS connected to" : "saw no client")}");
                });
            });
            Process Pinger() => Process.Start(new ProcessStartInfo(Path.Combine(system32, "ping.exe"), "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            await Row("processes-denied", async () =>
            {
                // A PROCESS OUTSIDE THE CELL CANNOT BE OPENED FOR TERMINATION from inside it. One script, three targets (CellDenial.KillScript): in the cell a process of its
                // own (it ends), in the cell the helper (refused, and the helper still runs), and outside any cell the helper's twin (it ends). Never the app's own pid: a row
                // must never risk ending the app.
                // NOT TASKKILL: in a cell it stops at a sign-in error even on the cell's own process (measured, CI run 42331636963), so it never tried the helper.
                using var helper = Pinger();
                using var twin = Pinger();
                try
                {
                    var (inCellCode, inCell) = await Run(none, powershell, CellDenial.PowerShell(CellDenial.KillScript(helper.Id)));
                    await Task.Delay(200, ct);
                    var alive = !helper.HasExited;
                    var free = await OutsidePowerShell(powershell, CellDenial.KillScript(twin.Id), outside, ct);
                    await Task.Delay(200, ct);
                    var ended = twin.HasExited;
                    var (held, seen) = CellDenial.Judge(["KILL"], inCell, free);
                    return (held && alive && ended, $"{seen}; exit {inCellCode}; the helper is {(alive ? "still running" : "GONE")}, and its twin {(ended ? "was ended from outside" : "was NOT ended from outside")}");
                }
                finally
                {
                    foreach (var p in new[] { helper, twin }) try { p.Kill(); } catch (Exception) { /* gone */ }
                }
            });
            await Row("descendants-cancelled", async () =>
            {
                // A SHELL AND A DETACHED GRANDCHILD THAT LIVE UNTIL ENDED (both read input the benchmark holds open), cancelled while alive;
                // every one of them gone after. THE CELL'S PROCESSES ARE WINDOWS' OWN LIST of the attempt's job whose token is an
                // AppContainer's (U-cell-windows-egress, card 3) — never a name, so a process outside the cell is never counted.
                IReadOnlyList<CellMember> mine = [];
                using (var held = Launch(none, cmd, ["/d", "/c", "start /b findstr x & findstr y"]))
                {
                    for (var i = 0; i < 100 && mine.Count(m => m.Name == "findstr") < 2; i++)
                    {
                        await Task.Delay(50, ct);
                        mine = CellMembers(held) ?? [];
                    }
                    if (mine.Count(m => m.Name == "findstr") < 2)
                        return (false, $"the cell's two readers are not both in its job ({string.Join(", ", mine.Select(m => m.Name))})");
                }
                var alive = mine.ToList();
                for (var i = 0; i < 100 && alive.Count > 0; i++)
                {
                    await Task.Delay(50, ct);
                    alive = [.. alive.Where(m => WindowsProcessTable.StillRunning(new ProcessEntry(m.Pid, 0, 0, 0, m.Start, false, false)))];
                }
                return (alive.Count == 0, alive.Count == 0
                    ? $"{mine.Count} processes of the cell's job ({string.Join(", ", mine.Select(m => m.Name))}), every one ended with the cancel"
                    : "OUTLIVED the cancel: " + string.Join(", ", alive.Select(m => $"{m.Name} {m.Pid}")));
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

            // THE WEB ROWS DECIDE THE PROOF'S EGRESS, never whether there is one — measured on EVERY slot (U-cell-windows-egress item 2).
            foreach (var row in await WebRowsAsync(web, ws, launcher, platform, ct)) rows.Add(row);

            // LAST, SO IT CATCHES A WEB CELL'S LEAK TOO (Codex, landing 3): after every launch above, no slot's SID holds an ACE on the
            // bench folders and no grant record is left.
            await Row("slot-leaves-nothing", async () =>
            {
                var (ranCode, ranOut) = await Script(none, "slot", "echo done");
                if (ranCode != 0 || !ranOut.Contains("done", StringComparison.Ordinal))
                    return (false, $"the last cell did not run ({ranCode}: {Trim(ranOut)}), so nothing can be said of what it left");
                WindowsCellStage.SweepFree();
                var sids = WindowsCellStage.SlotSids().Select(s => new SecurityIdentifier(s)).ToHashSet();
                var left = new[] { ws, codexHome, home, Kit.Dir(kitHome) }.Where(Directory.Exists).Sum(d => new DirectoryInfo(d).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
                    .Cast<FileSystemAccessRule>().Count(r => sids.Contains((SecurityIdentifier)r.IdentityReference)));
                var records = Directory.Exists(WindowsCellStage.SlotDirectory) ? Directory.GetFiles(WindowsCellStage.SlotDirectory, "*.grants").Length : 0;
                return (left == 0 && records == 0, $"after every cell: {left} slot ACE(s) on the bench folders, {records} grant record(s)");
            });
        }
        finally
        {
            // THE PROBE KIT'S FILES ARE READ-ONLY until its row takes that off; a row that failed first leaves them so.
            try { foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the delete below says nothing either */ }
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

    /// <summary>
    /// THE ONE WAY A ROW SAYS DENIED: every probe's line run three times, the same line each time —
    /// <list type="number">
    /// <item>in the cell, on the workspace target: it must OPEN, so the line runs in a cell at all;</item>
    /// <item>in the cell, on the forbidden target: it must be DENIED;</item>
    /// <item>outside any cell, the same command line byte for byte, on the forbidden target: it must OPEN, so the line can run, and only the cell differs.</item>
    /// </list>
    /// A syntax error, a missing program or a target that is not there fails (2) and (3) alike, and the row fails. <paramref name="after"/> says what
    /// the row sees from outside, read between the cell's run and the outside one.
    /// </summary>
    public static async Task<(bool Held, string Seen)> DenialRowAsync(CellPolicy under, IReadOnlyList<DenialProbe> probes, string outsideFolder, IReadOnlyList<string>? launcher,
        CellPlatform platform, Func<Task<(bool Ok, string Seen)>>? after = null, CancellationToken ct = default)
    {
        var (body, cellPaths, outsidePaths) = CellDenial.Lines(probes);
        var cellLine = CellDenial.CmdTokens(body, [.. cellPaths]);
        var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in (string[])["/d", "/c", .. cellLine]) psi.ArgumentList.Add(a);
        string cell;
        using (var held = ProcessContainment.Start(psi, under, launcher, platform))
        {
            held.Process.StandardInput.Close();
            var so = held.Process.StandardOutput.ReadToEndAsync(ct);
            var se = held.Process.StandardError.ReadToEndAsync(ct);
            await held.Process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(60), ct);
            cell = (await so) + (await se);
        }
        var (afterOk, afterSeen) = after is null ? (true, "") : await after();
        var free = await Outside(["/d", "/c", .. CellDenial.CmdTokens(body, [.. outsidePaths])], outsideFolder, ct, verbatim: true);
        var (judged, seen) = CellDenial.Judge([.. probes.Select(p => p.Word)], cell, free);
        return (judged && afterOk, afterSeen.Length == 0 ? seen : $"{seen}; {afterSeen}");
    }

    /// <summary>The kill probe outside any cell: PowerShell, the same script, no shell around it. Ended after 30 s.</summary>
    static async Task<string> OutsidePowerShell(string powershell, string script, string folder, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, WorkingDirectory = folder };
        foreach (var a in CellDenial.PowerShell(script)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var so = p.StandardOutput.ReadToEndAsync(ct);
        var se = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct); }
        finally { try { p.Kill(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* it ended */ } }
        return (await so) + (await se);
    }

    /// <summary>
    /// The row's line OUTSIDE any cell, in <paramref name="folder"/>. <paramref name="verbatim"/>: the command line is built by the same quoting a cell's is, so
    /// what cmd reads here is what it read in the cell, byte for byte. Otherwise the tool is started by cmd by name. Returns what it printed; ended after 30 s.
    /// </summary>
    static async Task<string> Outside(IReadOnlyList<string> arguments, string folder, CancellationToken ct, bool verbatim = false)
    {
        var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var psi = new ProcessStartInfo(cmd) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = folder };
        psi.Arguments = AppContainerCell.ArgumentLine(verbatim ? arguments : ["/d", "/c", .. arguments]);
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync(ct);
        var se = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct); }
        finally { try { p.Kill(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* it ended */ } }
        return (await so) + (await se);
    }

    /// <summary>One process of a cell's job: its pid, its creation (so a pid handed on is never it) and its image name, for the words only.</summary>
    public sealed record CellMember(int Pid, ulong Start, string Name, string Sid);

    /// <summary>
    /// THE CELL'S PROCESSES (<c>U-cell-windows-egress</c>, card 3): the processes Windows lists in the attempt's job whose token is an
    /// AppContainer's — the stage itself (the app's own code, outside any container) is in the job and never counted. Null when Windows would
    /// not list the job.
    /// </summary>
    public static IReadOnlyList<CellMember>? CellMembers(ContainedProcess held)
    {
        if (held.JobMembers() is not { } pids) return null;
        var members = new List<CellMember>();
        foreach (var pid in pids)
        {
            if (WindowsToken.AppContainerSidOf(pid) is not { Length: > 0 } sid) continue;
            if (WindowsProcessTable.StartOf(pid) is not { } start) continue;
            string name;
            try { using var p = Process.GetProcessById(pid); name = p.ProcessName; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { continue; }
            members.Add(new CellMember(pid, start, name, sid));
        }
        return members;
    }

    /// <summary>
    /// THE TWO WEB ROWS, MEASURED ON EVERY SLOT (<c>U-cell-windows-egress</c> item 2): the firewall rule names each slot's container SID, so
    /// one slot holding proves nothing of the others. For each slot in turn every OTHER free slot is held, one <c>web</c> cell is started,
    /// the slot it landed in is named by its own token's container SID, the hold is let go, and the cell then tries
    /// <list type="bullet">
    /// <item>github.com on 443 — reached (<c>web-443-granted</c>);</item>
    /// <item>github.com on 80, a third TCP port and a direct DNS query to a public resolver over UDP — all refused while each is reachable from outside a
    /// cell (the positive controls), so an upstream block is never taken for the rule's work (<c>web-nothing-else</c>). A rule closing 80 alone would pass a probe
    /// of 80 alone (R29 F-D2), so the third port is measured too, and every slot's rules are read back exactly.</item>
    /// </list>
    /// Each row holds only when all <see cref="WindowsCellSlots.Count"/> slots were measured and each held. The first slot that does not hold
    /// ends the sweep: the slots after it are NOT RUN, said in the row.
    /// </summary>
    public static async Task<IReadOnlyList<BenchRow>> WebRowsAsync(CellPolicy web, string ws, IReadOnlyList<string>? launcher, CellPlatform platform,
        CancellationToken ct = default)
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string cmd = Path.Combine(system32, "cmd.exe"), nslookup = Path.Combine(system32, "nslookup.exe");
        var sids = WindowsCellStage.SlotSids();
        var port80 = await OutsideReaches($"http://{Remote}:80/");
        string? other = null;
        foreach (var candidate in OtherPorts)
            if (Connected(await OutsideConnects($"http://{candidate}/"))) { other = candidate; break; }
        string? resolver = null;
        foreach (var candidate in Resolvers)
            if (await OutsideResolves(nslookup, candidate)) { resolver = candidate; break; }

        var reached = new List<string>();
        var refused = new List<string>();
        var measured = 0;
        var allReached = true;
        var allRefused = true;
        string? stop = !port80 ? $"port 80 of {Remote} is not reachable even from outside a cell, so a cell's failure to reach it proves nothing"
            : other is null ? $"no third TCP port ({string.Join(", ", OtherPorts)}) answers even from outside a cell, so a cell's failure to reach one proves nothing"
            : resolver is null ? $"no public resolver ({string.Join(", ", Resolvers)}) answers even from outside a cell, so a cell's unanswered query proves nothing"
            : null;
        // THE RULES AS WINDOWS' FIREWALL HOLDS THEM (Codex): the probes reach port 80 and UDP 53 only, and rules blocking just those would pass
        // them; so every slot's two rules must also read back exactly — TCP all-but-443 and all UDP, every profile, every interface, nothing
        // that narrows them. Read, never placed, here; a firewall that cannot be read is not web.
        if (stop is null)
        {
            try
            {
                if (WindowsCellEgress.Missing(WindowsCellEgress.RulesFor(Paths.Home)) is { Count: > 0 } missing)
                    stop = $"the firewall does not hold the cells' rules exactly ({missing.Count} of {2 * WindowsCellSlots.Count} absent or different, e.g. {missing[0]})";
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { stop = $"the firewall's rules could not be read ({ex.Message})"; }
        }

        for (var slot = 1; slot <= WindowsCellSlots.Count && stop is null; slot++)
        {
            // THE TOOLS BY NAME, FROM THE POLICY'S PATH: a cell's cmd says "Access is denied" of any program named by its full path, even a dir of it
            // (measured, CI run 42331636963 on 26100), which ended every earlier run of these rows at slot 1 on all three commands.
            // NO REVOCATION CHECK ON THE 443 PROBE: Schannel fetches a certificate's revocation list over port 80, which the rule closes, so a clean slot fails its TLS
            // with CRYPT_E_REVOCATION_OFFLINE (measured, CI run 42332664334) and this row is about reach, not about that.
            var body = "echo READY & set /p go= & " +
                       $"echo ==443== & curl -sS --ssl-no-revoke -o web{slot}-443.out -D - --connect-timeout 10 --max-time 15 https://{Remote}/ 2>&1 & " +
                       $"echo ==80== & curl -sS -o web{slot}-80.out -D - --connect-timeout 8 --max-time 12 http://{Remote}:80/ 2>&1 & " +
                       $"echo ==other== & curl -sS -o web{slot}-other.out -w C=%{{time_connect}} --connect-timeout 6 --max-time 8 http://{other}/ 2>&1 & " +
                       $"echo ==dns== & nslookup -timeout=3 -retry=1 {Remote} {resolver} 2>&1";
            string output;
            string? landed;
            var hold = WindowsCellStage.HoldAllBut(slot);
            try
            {
                var psi = new ProcessStartInfo(cmd) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in (string[])["/d", "/c", .. CellDenial.CmdTokens(body)]) psi.ArgumentList.Add(a);
                using var held = ProcessContainment.Start(psi, web, launcher, platform);
                var err = held.Process.StandardError.ReadToEndAsync(ct);
                var ready = await held.Process.StandardOutput.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(60), ct);
                // THE SLOT IT LANDED IN, BY ITS OWN TOKEN — while it waits, before the hold is let go.
                landed = ready?.Trim() == "READY" ? (CellMembers(held) ?? []).Select(m => m.Sid).Distinct().SingleOrDefault() : null;
                hold.Dispose();
                await held.Process.StandardInput.WriteLineAsync("go");
                held.Process.StandardInput.Close();
                var rest = held.Process.StandardOutput.ReadToEndAsync(ct);
                await held.Process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(90), ct);
                output = (ready ?? "") + "\n" + await rest + await err;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stop = $"slot {slot}: the web cell could not be measured ({ex.Message})";
                break;
            }
            finally { hold.Dispose(); }

            if (landed is null || !string.Equals(landed, sids[slot - 1], StringComparison.OrdinalIgnoreCase))
            {
                stop = $"slot {slot}: the measured cell's token names {(landed is null ? "no single container" : landed)}, not slot {slot}'s {sids[slot - 1]}";
                break;
            }
            measured++;
            var (s443, s80, sOther, sdns) = (Section(output, "443"), Section(output, "80"), Section(output, "other"), Section(output, "dns"));
            var got443 = s443.Contains("HTTP/", StringComparison.Ordinal);
            // CURL'S OWN FAILURE, NEVER ANOTHER: a tool that did not run prints no "curl: (n)", and fails these as it fails the 443 above.
            var refused80 = !s80.Contains("HTTP/", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.IsMatch(s80, @"curl: \((6|7|28|35)\)");
            var refusedOther = !Connected(sOther) && System.Text.RegularExpressions.Regex.IsMatch(sOther, @"curl: \((6|7|28)\)");
            // THE SAME QUERY WAS ANSWERED OUTSIDE A CELL A MOMENT AGO, so what nslookup prints here without an answer is its query not arriving, not the resolver's refusal.
            var unanswered = sdns.Contains(resolver!, StringComparison.Ordinal) && !Answered(sdns, resolver!);
            reached.Add($"slot {slot} {(got443 ? "reached" : "NOT reached: " + Trim(s443))}");
            refused.Add($"slot {slot} 80 {(refused80 ? "refused" : "NOT refused: " + Trim(s80))}, {other} {(refusedOther ? "refused" : "NOT refused: " + Trim(sOther))}, dns {(unanswered ? "unanswered" : "NOT unanswered: " + Trim(sdns))}");
            allReached &= got443;
            allRefused &= refused80 && refusedOther && unanswered;
            if (!got443 || !refused80 || !refusedOther || !unanswered)
                stop = $"slot {slot} does not hold web to 443";
        }

        var all = measured == WindowsCellSlots.Count && stop is null;
        var tail = (stop is null ? "" : $"; {stop}") +
                   (measured < WindowsCellSlots.Count ? $"; slot{(measured + 1 < WindowsCellSlots.Count ? $"s {measured + 1}-" : " ")}{WindowsCellSlots.Count} NOT RUN" : "");
        return
        [
            new BenchRow("web-443-granted", all && allReached,
                $"{Remote}:443 from a web cell in each slot: {(reached.Count == 0 ? "none measured" : string.Join("; ", reached))}{tail}"),
            new BenchRow("web-nothing-else", all && allRefused,
                $"{Remote}:80 (reachable from outside: {(port80 ? "yes" : "no")}), {other ?? "no third port that answers"} and direct DNS to {resolver ?? "no answering resolver"} from a web cell in each slot: " +
                $"{(refused.Count == 0 ? "none measured" : string.Join("; ", refused))}{tail}")
        ];
    }

    /// <summary>The public resolvers a direct query is tried against, the first that answers from outside a cell the one used.</summary>
    static readonly string[] Resolvers = ["8.8.8.8", "1.1.1.1"];

    /// <summary>TCP ports that are neither 443 nor 80 and answer on the open internet: the first that connects from outside a cell is the third port the web rows try.</summary>
    static readonly string[] OtherPorts = [$"{Remote}:22", "8.8.8.8:53", "1.1.1.1:53"];

    static readonly string[] Markers = ["==443==", "==80==", "==other==", "==dns=="];

    /// <summary>The text after the marker <c>==name==</c> and before the next of the markers.</summary>
    static string Section(string output, string name)
    {
        var at = output.IndexOf($"=={name}==", StringComparison.Ordinal);
        if (at < 0) return "";
        var from = at + name.Length + 4;
        var next = Markers.Select(m => output.IndexOf(m, from, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(output.Length).Min();
        return output[from..next];
    }

    /// <summary>What curl's <c>C=%{time_connect}</c> printed says: a connection was made. It reads zero when none was, whatever curl then got from the far end.</summary>
    static bool Connected(string curlOut) =>
        System.Text.RegularExpressions.Regex.Match(curlOut, @"C=([0-9.]+)") is { Success: true } m
        && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0;

    /// <summary>What curl from OUTSIDE any cell prints of <paramref name="url"/>'s connection: the positive control for the third port.</summary>
    static async Task<string> OutsideConnects(string url)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-sS", "-o", "NUL", "-w", "C=%{time_connect}", "--connect-timeout", "6", "--max-time", "8", url }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var o = await p.StandardOutput.ReadToEndAsync();
            p.WaitForExit(20_000);
            return o;
        }
        catch (Exception) { return ""; }
    }

    /// <summary>Whether a direct query to <paramref name="resolver"/> from OUTSIDE any cell is answered: the positive control for the DNS denial.</summary>
    static async Task<bool> OutsideResolves(string nslookup, string resolver)
    {
        try
        {
            var psi = new ProcessStartInfo(nslookup) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-timeout=3", "-retry=1", Remote, resolver }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var o = await p.StandardOutput.ReadToEndAsync();
            p.WaitForExit(20_000);
            return Answered(o, resolver);
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Whether nslookup's output carries an ANSWER: an IPv4 address other than the resolver's own (which it prints asked or not). Read by
    /// address, never by its words — they are the system's language ("Name:", "Nom :"), and a refusal can name the host it could not find.
    /// </summary>
    static bool Answered(string output, string resolver) =>
        System.Text.RegularExpressions.Regex.Matches(output, @"(?<![\d.])(\d{1,3}\.){3}\d{1,3}(?![\d.])").Any(m => m.Value != resolver);

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or beneath it, as Windows compares paths.</summary>
    static bool Inside(string path, string folder)
    {
        var (p, f) = (Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(folder));
        return p.Equals(f, StringComparison.OrdinalIgnoreCase) || p.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>curl's exit for a connect/resolve/timeout failure (6 host, 7 connect, 28 timeout, 35 TLS) — a real attempt that did not reach, not a launcher error.</summary>
    static bool CurlFailedToReach(int code) => code is 6 or 7 or 28 or 35;

    /// <summary>Whether <paramref name="url"/> is reachable from OUTSIDE any cell (the app's own curl): a positive control for a denial row.</summary>
    static async Task<bool> OutsideReaches(string url)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-sS", "-o", "NUL", "-w", "HTTP=%{http_code}", "--connect-timeout", "8", "--max-time", "12", url }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var o = await p.StandardOutput.ReadToEndAsync();
            p.WaitForExit(20_000);
            return Reached(o);
        }
        catch (Exception) { return false; }
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

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
/// web rows held ON EVERY SLOT (443 reached, port 80 and a direct DNS query refused: the firewall rule's work; <see cref="WebRowsAsync"/>). Any core row failed ⇒ nothing is written. A row's
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

            // A ROW'S COMMANDS, INLINE: a cell cannot run a batch FILE of the workspace (its cmd reports access denied reading it), so the
            // commands go on cmd's own line, '\n' the statement separator — AS TOKENS (U-cell-windows-egress: a body holding quotes reached
            // cmd as \"path\", the C runtime's escape cmd does not read, so every quoted command failed and every denial row held without
            // running its command). Each path is its own argument {0}, {1}…, quoted by the stage as cmd reads it; a sink is a name in the
            // workspace, the cell's working folder; and every file row runs a CONTROL — the same command shape on a workspace path — that
            // must succeed, so a command that never ran is never a denial.
            Task<(int Code, string Out)> Script(CellPolicy under, string name, string body, params string[] paths) =>
                Run(under, cmd, ["/d", "/c", .. CmdTokens(body, paths)]);

            string Sink(string name) => name + ".out";
            var control = Path.Combine(ws, "control.txt");
            File.WriteAllText(control, "control");

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
                        $"type {{0}} > {Sink("ctl")} && (echo CONTROL-OPEN) || (echo CONTROL-DENIED)\n" +
                        $"type {{1}} > {Sink("a")} && (echo READ-OPEN) || (echo READ-DENIED)\n" +
                        $"dir {{2}} > {Sink("b")} && (echo LIST-OPEN) || (echo LIST-DENIED)", control, ledger, Paths.State);
                    var leaked = File.Exists(Path.Combine(ws, "a.out")) && File.ReadAllText(Path.Combine(ws, "a.out")).Contains("ledger", StringComparison.Ordinal);
                    return (o.Contains("CONTROL-OPEN", StringComparison.Ordinal) && o.Contains("READ-DENIED", StringComparison.Ordinal)
                            && o.Contains("LIST-DENIED", StringComparison.Ordinal) && !leaked, Trim(o));
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
                    var (_, o) = await Script(none, "state-w",
                        "echo x>> {0} && (echo CONTROL-OPEN) || (echo CONTROL-DENIED)\n" +
                        "echo x>> {1} && (echo APPEND-OPEN) || (echo APPEND-DENIED)\necho x> {2} && (echo CREATE-OPEN) || (echo CREATE-DENIED)", control, sentinel, made);
                    var same = File.ReadAllText(sentinel) == "sentinel";
                    return (o.Contains("CONTROL-OPEN", StringComparison.Ordinal) && o.Contains("APPEND-DENIED", StringComparison.Ordinal)
                            && o.Contains("CREATE-DENIED", StringComparison.Ordinal) && same && !File.Exists(made),
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
                    var (_, o) = await Script(none, "login",
                        $"type {{0}} > {Sink("ctl")} && (echo CONTROL-OPEN) || (echo CONTROL-DENIED)\n" +
                        $"type {{1}} > {Sink("c")} && (echo LOGIN-OPEN) || (echo LOGIN-DENIED)", control, login);
                    return (o.Contains("CONTROL-OPEN", StringComparison.Ordinal) && o.Contains("LOGIN-DENIED", StringComparison.Ordinal)
                            && !o.Contains("LOGIN-OPEN", StringComparison.Ordinal), Trim(o));
                }
                finally { File.Delete(login); }
            });
            await Row("inbox-uncreatable", async () =>
            {
                Directory.CreateDirectory(Paths.Inbox);
                var probe = Path.Combine(Paths.Inbox, ".cell-bench-" + Guid.NewGuid().ToString("n")[..8]);
                try
                {
                    var made = Path.Combine(ws, "inbox-control.txt");
                    var (_, o) = await Script(none, "inbox",
                        "echo x> {0} && (echo CONTROL-OPEN) || (echo CONTROL-DENIED)\necho x> {1} && (echo INBOX-OPEN) || (echo INBOX-DENIED)", made, probe);
                    return (o.Contains("CONTROL-OPEN", StringComparison.Ordinal) && o.Contains("INBOX-DENIED", StringComparison.Ordinal)
                            && !o.Contains("INBOX-OPEN", StringComparison.Ordinal) && !File.Exists(probe), Trim(o));
                }
                finally { File.Delete(probe); }   // a timeout or a broken boundary never leaves the planted file for the real scanner (Codex, landing 3)
            });

            // EGRESS NONE REACHES NOTHING (Codex, landing 3): the internet on 443 by name — and the failure is curl's own resolve/connect
            // (exit 6/7/28), not the launcher's, so a missing or unstartable curl fails the row instead of passing it.
            await Row("no-egress", async () =>
            {
                var (code, o) = await Run(none, curl, "-sS", "-o", Path.Combine(ws, "n.out"), "-w", "HTTP=%{http_code}", "--connect-timeout", "8", "--max-time", "12", $"https://{Remote}/");
                return (CurlFailedToReach(code) && !Reached(o), $"{Remote}:443 with no capability: {Trim(o)}");
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
                var (_, o) = await Script(none, "pipe", $"type {{0}} > {Sink("p")} && (echo PIPE-OPEN) || (echo PIPE-DENIED)", $@"\\.\pipe\{name}");
                await Task.WhenAny(connected, Task.Delay(500, ct));
                return (o.Contains("PIPE-DENIED", StringComparison.Ordinal) && !connected.IsCompletedSuccessfully, $"{Trim(o)}; the server {(connected.IsCompletedSuccessfully ? "WAS connected to" : "saw no client")}");
            });
            await Row("processes-denied", async () =>
            {
                // A PROCESS OUTSIDE THE CELL CANNOT BE OPENED FOR TERMINATION from inside it: the cell's `taskkill /F` (PROCESS_TERMINATE,
                // unambiguous — not a graceful WM_CLOSE) on a helper is refused and the helper still runs. A POSITIVE CONTROL first: the app,
                // outside any cell, `taskkill /F`s a second helper and it dies — so the cell's denial is the cell's, not an unkillable helper.
                // Never the app's own pid: a row must never risk ending the app.
                var killable = Process.Start(new ProcessStartInfo(Path.Combine(system32, "ping.exe"), "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
                using var control = Process.Start(new ProcessStartInfo(Path.Combine(system32, "taskkill.exe"), $"/F /pid {killable.Id}") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
                control.WaitForExit(10_000);
                await Task.Delay(200, ct);
                if (!killable.HasExited) { try { killable.Kill(); } catch (Exception) { } killable.Dispose(); return (false, "the control could not kill the helper from outside a cell, so the row proves nothing"); }
                killable.Dispose();
                using var helper = Process.Start(new ProcessStartInfo(Path.Combine(system32, "ping.exe"), "-n 30 127.0.0.1")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
                try
                {
                    var (_, o) = await Script(none, "proc", $"taskkill /F /pid {helper.Id} > {Sink("k")} 2>&1 && (echo KILL-OPEN) || (echo KILL-DENIED)");
                    // The sink is a bare name now (cmd read the quoted one as a path it could not open, and taskkill never ran): the row ran
                    // its command only if taskkill said something.
                    if (!File.Exists(Path.Combine(ws, "k.out")) || new FileInfo(Path.Combine(ws, "k.out")).Length == 0)
                        return (false, $"taskkill wrote nothing, so it never ran: {Trim(o)}");
                    var alive = !helper.HasExited;
                    return (o.Contains("KILL-DENIED", StringComparison.Ordinal) && !o.Contains("KILL-OPEN", StringComparison.Ordinal) && alive,
                        $"{Trim(o)}; the helper is {(alive ? "still running" : "GONE")} (a control killed its twin from outside)");
                }
                finally { try { helper.Kill(); } catch (Exception) { /* gone */ } }
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
                await Script(none, "slot", "echo done");
                WindowsCellStage.SweepFree();
                var sids = WindowsCellStage.SlotSids().Select(s => new SecurityIdentifier(s)).ToHashSet();
                var left = new[] { ws, codexHome, home }.Sum(d => new DirectoryInfo(d).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
                    .Cast<FileSystemAccessRule>().Count(r => sids.Contains((SecurityIdentifier)r.IdentityReference)));
                var records = Directory.Exists(WindowsCellStage.SlotDirectory) ? Directory.GetFiles(WindowsCellStage.SlotDirectory, "*.grants").Length : 0;
                return (left == 0 && records == 0, $"after every cell: {left} slot ACE(s) on the bench folders, {records} grant record(s)");
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
    /// <item>github.com on 80 and a direct DNS query to a public resolver over UDP — both refused while each is reachable from outside a cell
    /// (the positive controls), so an upstream block is never taken for the rule's work (<c>web-nothing-else</c>).</item>
    /// </list>
    /// Each row holds only when all <see cref="WindowsCellSlots.Count"/> slots were measured and each held. The first slot that does not hold
    /// ends the sweep: the slots after it are NOT RUN, said in the row.
    /// </summary>
    public static async Task<IReadOnlyList<BenchRow>> WebRowsAsync(CellPolicy web, string ws, IReadOnlyList<string>? launcher, CellPlatform platform,
        CancellationToken ct = default)
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string cmd = Path.Combine(system32, "cmd.exe"), curl = Path.Combine(system32, "curl.exe"), nslookup = Path.Combine(system32, "nslookup.exe");
        var sids = WindowsCellStage.SlotSids();
        var port80 = await OutsideReaches($"http://{Remote}:80/");
        string? resolver = null;
        foreach (var candidate in Resolvers)
            if (await OutsideResolves(nslookup, candidate)) { resolver = candidate; break; }

        var reached = new List<string>();
        var refused = new List<string>();
        var measured = 0;
        var allReached = true;
        var allRefused = true;
        string? stop = !port80 ? $"port 80 of {Remote} is not reachable even from outside a cell, so a cell's failure to reach it proves nothing"
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
            var body = "echo READY & set /p go= & " +
                       $"echo ==443== & {{0}} -sS -o web{slot}-443.out -D - --connect-timeout 10 --max-time 15 https://{Remote}/ 2>&1 & " +
                       $"echo ==80== & {{0}} -sS -o web{slot}-80.out -D - --connect-timeout 8 --max-time 12 http://{Remote}:80/ 2>&1 & " +
                       $"echo ==dns== & {{1}} -timeout=3 -retry=1 {Remote} {resolver} 2>&1";
            string output;
            string? landed;
            var hold = WindowsCellStage.HoldAllBut(slot);
            try
            {
                var psi = new ProcessStartInfo(cmd) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in (string[])["/d", "/c", .. CmdTokens(body, curl, nslookup)]) psi.ArgumentList.Add(a);
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
            var (s443, s80, sdns) = (Section(output, "443"), Section(output, "80"), Section(output, "dns"));
            var got443 = s443.Contains("HTTP/", StringComparison.Ordinal);
            var refused80 = !s80.Contains("HTTP/", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.IsMatch(s80, @"curl: \((6|7|28|35)\)");
            var unanswered = sdns.Contains(resolver!, StringComparison.Ordinal) && !Answered(sdns, resolver!);
            reached.Add($"slot {slot} {(got443 ? "reached" : "NOT reached: " + Trim(s443))}");
            refused.Add($"slot {slot} 80 {(refused80 ? "refused" : "NOT refused: " + Trim(s80))}, dns {(unanswered ? "unanswered" : "NOT unanswered: " + Trim(sdns))}");
            allReached &= got443;
            allRefused &= refused80 && unanswered;
            if (!got443 || !refused80 || !unanswered)
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
                $"{Remote}:80 (reachable from outside: {(port80 ? "yes" : "no")}) and direct DNS to {resolver ?? "no answering resolver"} from a web cell in each slot: " +
                $"{(refused.Count == 0 ? "none measured" : string.Join("; ", refused))}{tail}")
        ];
    }

    /// <summary>The public resolvers a direct query is tried against, the first that answers from outside a cell the one used.</summary>
    static readonly string[] Resolvers = ["8.8.8.8", "1.1.1.1"];

    /// <summary>
    /// A cmd line as ARGUMENTS: <paramref name="body"/> split on spaces ('\n' the statement separator, as " &amp; "), each token <c>{i}</c> replaced
    /// by <paramref name="paths"/>[i] whole. The stage quotes an argument holding a space as cmd reads it — no quote is ever inside one, so
    /// nothing reaches cmd as the C runtime's <c>\"</c>. A path may not end in a backslash or hold a quote.
    /// </summary>
    public static IReadOnlyList<string> CmdTokens(string body, params string[] paths)
    {
        foreach (var path in paths)
            if (path.Contains('"') || path.EndsWith('\\'))
                throw new ArgumentException($"a cell row's path cannot be passed to cmd as one argument: {path}");
        return [.. body.Replace("\n", " & ").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t =>
            t.Length >= 3 && t[0] == '{' && t[^1] == '}' && int.TryParse(t[1..^1], out var i) && i < paths.Length ? paths[i] : t)];
    }

    static readonly string[] Markers = ["==443==", "==80==", "==dns=="];

    /// <summary>The text after the marker <c>==name==</c> and before the next of the three markers.</summary>
    static string Section(string output, string name)
    {
        var at = output.IndexOf($"=={name}==", StringComparison.Ordinal);
        if (at < 0) return "";
        var from = at + name.Length + 4;
        var next = Markers.Select(m => output.IndexOf(m, from, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(output.Length).Min();
        return output[from..next];
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

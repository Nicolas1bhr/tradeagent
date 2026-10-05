using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;

namespace ContainmentProbe;

/// <summary>
/// The in-sandbox half: this is what runs INSIDE the AppContainer. It attempts each negative and
/// positive control and writes a JSON result file to PROBE_OUT. It is deliberately dumb about why a
/// thing failed beyond the Win32 error code — the host decides what a result means. It reads no byte
/// of any real secret: a secret cell records only whether the open succeeded and the error code.
/// </summary>
internal static class Worker
{
    public static int Run(string mode)
    {
        var r = new List<Probe>();
        string Env(string k) => Environment.GetEnvironmentVariable(k) ?? "";
        var outPath = Env("PROBE_OUT");

        try
        {
            switch (mode)
            {
                case "net": NetCells(r, Env); break;
                case "sleep": return Sleep(Env);
                case "idle": Thread.Sleep(900); return 0;   // perf cell: just exist briefly
                default: MainCells(r, Env); break;
            }
        }
        catch (Exception ex)
        {
            r.Add(new Probe("worker", "worker crashed", "no crash", "ERROR", false, ex.GetType().Name + ": " + ex.Message));
        }

        if (!string.IsNullOrEmpty(outPath)) ProbeJson.Write(outPath, r);
        foreach (var p in r) Console.WriteLine($"[{p.Cell}] {p.Outcome} ok={p.Ok} {p.Detail}");
        return 0;
    }

    static void MainCells(List<Probe> r, Func<string, string> Env)
    {
        var ws = Env("PROBE_WORKSPACE");
        var ro = Env("PROBE_ROMOUNT");
        var state = Env("PROBE_STATE");
        var stateDir = Path.GetDirectoryName(state) ?? "";

        // Cell 1 — workspace RW (positive): create, write, read back.
        try
        {
            var f = Path.Combine(ws, "probe-write.txt");
            File.WriteAllText(f, "ok");
            var back = File.ReadAllText(f);
            r.Add(Probe.Allowed("1", "workspace RW", "ALLOWED", $"wrote+read '{back}' at {f}", back == "ok"));
        }
        catch (Exception ex) { r.Add(new Probe("1", "workspace RW", "ALLOWED", "DENIED", false, ex.Message)); }

        // Cell 2 — read-only mount: read allowed, write denied.
        var roRead = Native.TryOpenRead(Path.Combine(ro, "reference.txt"));
        r.Add(new Probe("2a", "RO mount readable", "ALLOWED", roRead.ok ? "ALLOWED" : "DENIED", roRead.ok,
            roRead.ok ? "read reference.txt" : "could not read", roRead.ok ? null : roRead.err));
        var roWrite = Native.TryOpenWrite(Path.Combine(ro, "intruder.txt"));
        r.Add(new Probe("2b", "RO mount not writable", "DENIED", roWrite.ok ? "ALLOWED" : "DENIED", !roWrite.ok,
            roWrite.ok ? "WROTE into an RO mount" : "write refused", roWrite.ok ? null : roWrite.err));

        // Cell 3 — TradeAgent state/ unreadable and unwritable (negative).
        var sRead = Native.TryOpenRead(state);
        r.Add(new Probe("3a", "state/ read blocked", "DENIED", sRead.ok ? "ALLOWED" : "DENIED", !sRead.ok,
            sRead.ok ? "READ state file" : "read refused", sRead.ok ? null : sRead.err));
        var sWrite = Native.TryOpenWrite(Path.Combine(stateDir, "intruder.bin"));
        r.Add(new Probe("3b", "state/ write blocked", "DENIED", sWrite.ok ? "ALLOWED" : "DENIED", !sWrite.ok,
            sWrite.ok ? "WROTE into state/" : "write refused", sWrite.ok ? null : sWrite.err));

        // Cell 4 — user secrets unreadable. Open-for-read only; no byte read, nothing hashed or printed.
        Secret(r, "4a", "owner .codex/auth.json unreadable", Env("PROBE_SECRET_CODEX"), false);
        Secret(r, "4b", "owner .ssh unreadable", Env("PROBE_SECRET_SSH"), true);
        var browser = Env("PROBE_SECRET_BROWSER");
        if (!string.IsNullOrEmpty(browser)) Secret(r, "4c", "browser profile unreadable", browser, true);

        // Cell 5 — reparse escapes fail: make a junction in the workspace pointing at state/, read through it.
        try
        {
            var link = Path.Combine(ws, "escape-junction");
            if (Directory.Exists(link)) Directory.Delete(link);
            var mk = Run("cmd.exe", $"/c mklink /J \"{link}\" \"{stateDir}\"");
            var madeJunction = Directory.Exists(link);
            if (madeJunction)
            {
                var through = Native.TryOpenRead(Path.Combine(link, Path.GetFileName(state)));
                r.Add(new Probe("5", "reparse escape fails", "DENIED", through.ok ? "ALLOWED" : "DENIED", !through.ok,
                    through.ok ? "READ state through a junction" : "junction made but target still denied",
                    through.ok ? null : through.err));
            }
            else
                r.Add(new Probe("5", "reparse escape fails", "DENIED", "DENIED", true,
                    "junction creation itself refused: " + mk.Trim()));
        }
        catch (Exception ex) { r.Add(new Probe("5", "reparse escape fails", "DENIED", "ERROR", true, ex.Message)); }

        // Cell 6 — descendants stay inside: a child shell/interpreter cannot read state/ either.
        Descendant(r, "6a", "cmd descendant contained", "cmd.exe", $"/c type \"{state}\"", state);
        Descendant(r, "6b", "powershell descendant contained", "powershell.exe",
            $"-NoProfile -Command \"Get-Content -LiteralPath '{state}' -TotalCount 1\"", state);
        var node = Env("PROBE_NODE");
        if (!string.IsNullOrEmpty(node))
            Descendant(r, "6c", "node descendant contained", node,
                $"-e \"require('fs').readFileSync(process.env.PROBE_STATE)\"", state);

        // Cell 7 — persistence outside the boundary fails: a scheduled task and a Run key.
        var task = "TA_ProbeContainment_" + Environment.ProcessId;
        var sch = Run("schtasks.exe", $"/create /tn {task} /tr \"cmd /c exit\" /sc once /st 23:59 /f");
        var schMade = Run("schtasks.exe", $"/query /tn {task}").Contains(task);
        if (schMade) Run("schtasks.exe", $"/delete /tn {task} /f");   // self-cleanup, recorded
        r.Add(new Probe("7a", "scheduled task blocked", "DENIED", schMade ? "ALLOWED" : "DENIED", !schMade,
            schMade ? "CREATED a scheduled task (removed again)" : "refused: " + Trunc(sch)));
        try
        {
            var reg = Run("reg.exe",
                $"add HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run /v {task} /t REG_SZ /d x /f");
            var regMade = Run("reg.exe",
                $"query HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run /v {task}").Contains(task);
            if (regMade) Run("reg.exe", $"delete HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run /v {task} /f");
            r.Add(new Probe("7b", "Run key blocked", "DENIED", regMade ? "ALLOWED" : "DENIED", !regMade,
                regMade ? "WROTE an HKCU Run value (removed again)" : "refused/redirected: " + Trunc(reg)));
        }
        catch (Exception ex) { r.Add(new Probe("7b", "Run key blocked", "DENIED", "ERROR", true, ex.Message)); }

        // Cell 8 — opening a host process fails.
        if (int.TryParse(Env("PROBE_HOSTPID"), out var hpid))
        {
            var h = Native.OpenProcess(Native.PROCESS_VM_READ | Native.PROCESS_VM_WRITE | Native.PROCESS_VM_OPERATION
                | Native.PROCESS_QUERY_INFORMATION, false, (uint)hpid);
            var opened = h != IntPtr.Zero;
            if (opened) Native.CloseHandle(h);
            r.Add(new Probe("8", "open/inject host process fails", "DENIED", opened ? "ALLOWED" : "DENIED", !opened,
                opened ? $"OPENED pid {hpid} for VM write" : $"OpenProcess(pid {hpid}) refused",
                opened ? null : System.Runtime.InteropServices.Marshal.GetLastWin32Error()));
        }

        // Cell 9 — TradeAgent's named pipe unreachable without a grant (the pipe is ACL'd to the owning
        // user only, the way GatewayPipeServer.CreateServer ACLs the gateway pipe).
        var pipe = Env("PROBE_PIPE");
        if (!string.IsNullOrEmpty(pipe))
        {
            try
            {
                using var c = new NamedPipeClientStream(".", pipe, PipeDirection.InOut);
                c.Connect(3000);
                r.Add(new Probe("9", "privileged pipe blocked", "DENIED", "ALLOWED", false,
                    "CONNECTED to the ACL'd pipe from inside the sandbox"));
            }
            catch (Exception ex)
            {
                r.Add(new Probe("9", "privileged pipe blocked", "DENIED", "DENIED", true,
                    ex.GetType().Name + ": " + Trunc(ex.Message)));
            }
        }

        // Cell 15 — compile and run a generated tool (in-box C# via Add-Type; no user-profile paths).
        try
        {
            var src = "public class T{public static void M(){System.Console.Write(2+2);}}";
            var ps = $"Add-Type -TypeDefinition \"{src}\"; [T]::M()";
            var o = Run("powershell.exe", $"-NoProfile -Command \"{ps}\"");
            var compiled = o.Trim() == "4";
            r.Add(new Probe("15", "compile+run generated tool", "ALLOWED", compiled ? "ALLOWED" : "FAIL", compiled,
                compiled ? "Add-Type compiled C# and ran it (printed 4)" : "compile/run failed: " + Trunc(o)));
        }
        catch (Exception ex) { r.Add(new Probe("15", "compile+run generated tool", "ALLOWED", "ERROR", false, ex.Message)); }

        // Cell 18 — the credential question (topology illustration). A credential placed INSIDE the
        // sandbox (Topology D) is readable by code inside it; the owner's real login OUTSIDE is not.
        var dummy = Env("PROBE_SECRET_DUMMY");
        if (!string.IsNullOrEmpty(dummy))
        {
            var d = Native.TryOpenRead(dummy);
            r.Add(new Probe("18a", "credential INSIDE sandbox readable (topology D)", "ALLOWED (by design, bad)",
                d.ok ? "ALLOWED" : "DENIED", d.ok,
                d.ok ? "in-sandbox code opened the dummy auth.json placed in the workspace — Topology D exposes it"
                     : "dummy not reachable", d.ok ? null : d.err));
        }
        var realCodex = Native.TryOpenRead(Env("PROBE_SECRET_CODEX"));
        r.Add(new Probe("18b", "credential OUTSIDE sandbox hidden (topology A/C)", "DENIED",
            realCodex.ok ? "ALLOWED" : "DENIED", !realCodex.ok,
            realCodex.ok ? "in-sandbox code opened the owner's real login" : "owner login denied to in-sandbox code",
            realCodex.ok ? null : realCodex.err));
    }

    static void NetCells(List<Probe> r, Func<string, string> Env)
    {
        var tag = Env("PROBE_NET_TAG"); // "with-cap" or "no-cap"
        // Cell 12 — public HTTPS where granted.
        try
        {
            using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var resp = h.GetAsync(Env("PROBE_URL")).GetAwaiter().GetResult();
            r.Add(new Probe("12", $"public HTTPS ({tag})", tag == "with-cap" ? "ALLOWED" : "DENIED",
                "ALLOWED", true, $"HTTP {(int)resp.StatusCode} from {Env("PROBE_URL")}"));
        }
        catch (Exception ex)
        {
            r.Add(new Probe("12", $"public HTTPS ({tag})", tag == "with-cap" ? "ALLOWED" : "DENIED",
                "DENIED", true, ex.GetType().Name + ": " + Trunc(ex.Message)));
        }

        // Cell 13 — loopback reachability (recorded, not judged: AppContainer blocks loopback by default).
        var port = Env("PROBE_LOOPBACK_PORT");
        if (int.TryParse(port, out var p))
        {
            try
            {
                using var c = new TcpClient();
                c.Connect("127.0.0.1", p);
                r.Add(new Probe("13", $"loopback reachability ({tag})", "recorded", "REACHABLE", true,
                    $"connected to 127.0.0.1:{p} from inside the sandbox"));
            }
            catch (Exception ex)
            {
                r.Add(new Probe("13", $"loopback reachability ({tag})", "recorded", "BLOCKED", true,
                    "loopback refused (AppContainer default): " + Trunc(ex.Message)));
            }
        }
    }

    // cell 16 helper: a long-lived child that spawns its own grandchild, so the host can prove a job
    // close takes the whole tree down.
    static int Sleep(Func<string, string> Env)
    {
        try
        {
            var marker = Env("PROBE_MARKER");
            // grandchild first, so we can record its pid alongside our own
            var gc = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 600 127.0.0.1 > nul")
            { UseShellExecute = false, CreateNoWindow = true });
            if (!string.IsNullOrEmpty(marker))
                File.WriteAllText(marker, $"{Environment.ProcessId},{gc?.Id ?? 0}");
            Thread.Sleep(600000);
        }
        catch { /* dying is the point */ }
        return 0;
    }

    static void Secret(List<Probe> r, string cell, string name, string path, bool dir)
    {
        if (string.IsNullOrEmpty(path)) { r.Add(new Probe(cell, name, "DENIED", "N/A", true, "path not provided")); return; }
        var (ok, err) = Native.TryOpenRead(path, dir);
        r.Add(new Probe(cell, name, "DENIED", ok ? "ALLOWED" : "DENIED", !ok,
            ok ? "OPEN-FOR-READ SUCCEEDED (no bytes read)" : "open-for-read refused", ok ? null : err));
    }

    static void Descendant(List<Probe> r, string cell, string name, string exe, string args, string state)
    {
        try
        {
            var o = Run(exe, args);
            // The child read state iff its output carries the file's content. The state file holds a
            // known sentinel; "could not" / access-denied text means contained.
            var leaked = o.Contains("TRUSTED-STATE-SENTINEL");
            r.Add(new Probe(cell, name, "DENIED", leaked ? "ALLOWED" : "DENIED", !leaked,
                leaked ? "descendant READ state/" : "descendant could not read state/: " + Trunc(o)));
        }
        catch (Exception ex)
        {
            r.Add(new Probe(cell, name, "DENIED", "DENIED", true, "descendant launch/read refused: " + Trunc(ex.Message)));
        }
    }

    static string Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(20000);
        return o;
    }

    static string Trunc(string s) => s.Length > 160 ? s[..160].Replace("\r", " ").Replace("\n", " ") : s.Replace("\r", " ").Replace("\n", " ");
}

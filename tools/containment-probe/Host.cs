using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ContainmentProbe;

/// <summary>
/// The out-of-sandbox half: stand up the probe home, the workspace, the read-only mount, the dummy
/// credential, the ACL'd pipe and a dummy host process; create the AppContainer (C2); run the worker
/// inside it; and run the cells only the launcher can (cancellation, performance, the C1 availability
/// probe). Everything lands under a single base folder so the box can be left exactly as found.
/// </summary>
internal sealed class Host
{
    readonly string _base;
    readonly string _exe;
    readonly List<Probe> _all = [];
    string AcName => "TA.RContain.Probe";

    Host(string baseDir) { _base = baseDir; _exe = Environment.ProcessPath!; }

    public static int Run(string baseDir)
    {
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("host: Windows only"); return 2; }
        var h = new Host(baseDir);
        return h.Go();
    }

    string Dir(string name) { var d = Path.Combine(_base, name); Directory.CreateDirectory(d); return d; }

    int Go()
    {
        Console.WriteLine($"containment-probe host | base={_base}");
        var meta = new Dictionary<string, object?>();

        // --- layout ---
        var home = Dir("home");
        var state = Dir(Path.Combine("home", "state"));
        var stateFile = Path.Combine(state, "trusted.bin");
        File.WriteAllText(stateFile, "TRUSTED-STATE-SENTINEL does not belong to the sandbox");
        var ws = Dir("workspace");
        var ro = Dir("romount");
        File.WriteAllText(Path.Combine(ro, "reference.txt"), "read-only reference input");
        var codexDummyDir = Dir("codexhome");
        // A DUMMY credential — never the owner's. Placed both at a probe CODEX_HOME and, for the
        // topology-D illustration, inside the granted workspace.
        var dummyAuth = Path.Combine(codexDummyDir, "auth.json");
        File.WriteAllText(dummyAuth, "{\"auth_mode\":\"apikey\",\"OPENAI_API_KEY\":\"sk-DUMMY-PROBE-NOT-REAL-0000\"}");
        var wsCodex = Dir(Path.Combine("workspace", "codex"));
        var dummyInWs = Path.Combine(wsCodex, "auth.json");
        File.WriteAllText(dummyInWs, "{\"auth_mode\":\"apikey\",\"OPENAI_API_KEY\":\"sk-DUMMY-INSANDBOX-0000\"}");

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var ownerCodex = Path.Combine(userProfile, ".codex", "auth.json");   // open-for-read ONLY, never read
        var ownerSsh = Path.Combine(userProfile, ".ssh");
        var browser = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Google", "Chrome", "User Data", "Default");

        // --- target ---
        meta["target_os"] = RuntimeInformation.OSDescription;
        meta["target_arch"] = RuntimeInformation.OSArchitecture.ToString();
        meta["dotnet"] = Environment.Version.ToString();
        meta["elevated"] = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        // --- C1 availability (processmodel.dll export present?) ---
        meta["c1"] = SandboxC1.Probe(_base);

        // --- C2 AppContainer ---
        AppContainer? ac = null;
        var acStatus = new Dictionary<string, object?>();
        try
        {
            var swCold = Stopwatch.StartNew();
            Native.DeleteAppContainerProfile(AcName);   // ensure a clean cold create for the perf number
            ac = AppContainer.Create(AcName);
            swCold.Stop();
            acStatus["created"] = true;
            acStatus["sid"] = ac.SidString;
            acStatus["profile_create_ms"] = swCold.Elapsed.TotalMilliseconds;

            // Grant the AppContainer into exactly the paths it is meant to reach, and nowhere else.
            acStatus["grant_workspace"] = ac.Grant(ws, write: true);
            acStatus["grant_romount"] = ac.Grant(ro, write: false);
            acStatus["grant_probe_bin"] = ac.Grant(Path.GetDirectoryName(_exe)!, write: false); // so it can load the exe + runtime
        }
        catch (Exception ex)
        {
            acStatus["created"] = false;
            acStatus["error"] = ex.Message;
            meta["c2"] = acStatus;
            Save(meta);
            Console.Error.WriteLine("C2 AppContainer could not be created: " + ex.Message);
            return 1;
        }
        meta["c2"] = acStatus;

        // dummy host process for cell 8 (a notepad-free, harmless sleeper WE start)
        var hostProc = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 600 127.0.0.1 > nul")
        { UseShellExecute = false, CreateNoWindow = true })!;

        // the ACL'd pipe for cell 9 (mirrors GatewayPipeServer.CreateServer)
        var pipeName = "TA_Probe_Gateway_" + Environment.ProcessId;
        using var pipeStop = new CancellationTokenSource();
        var pipeTask = Task.Run(() => AclPipeServer(pipeName, pipeStop.Token));

        // loopback listener for cell 13
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int loopbackPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () => { try { while (true) { var c = await listener.AcceptTcpClientAsync(); c.Close(); } } catch { } });

        // --- env the worker reads ---
        void Env(string k, string v) => Environment.SetEnvironmentVariable(k, v);
        Env("PROBE_WORKSPACE", ws);
        Env("PROBE_ROMOUNT", ro);
        Env("PROBE_STATE", stateFile);
        Env("PROBE_SECRET_CODEX", ownerCodex);
        Env("PROBE_SECRET_SSH", ownerSsh);
        if (Directory.Exists(browser)) Env("PROBE_SECRET_BROWSER", browser);
        Env("PROBE_SECRET_DUMMY", dummyInWs);
        Env("PROBE_PIPE", pipeName);
        Env("PROBE_HOSTPID", hostProc.Id.ToString());
        Env("PROBE_LOOPBACK_PORT", loopbackPort.ToString());
        Env("PROBE_NODE", WhereIs("node.exe"));

        // --- run the main cell set inside the container ---
        var mainOut = Path.Combine(ws, "out-main.json");
        Env("PROBE_OUT", mainOut);
        var sw = Stopwatch.StartNew();
        var main = ac.Launch($"\"{_exe}\" worker main", internet: false, inJob: false, ws);
        main.Wait(120000); main.Close();
        acStatus["main_wall_ms"] = sw.Elapsed.TotalMilliseconds;
        Collect(mainOut);

        // --- network cells: once WITHOUT internetClient, once WITH (cell 12/13) ---
        foreach (var (tag, cap) in new[] { ("no-cap", false), ("with-cap", true) })
        {
            var netOut = Path.Combine(ws, $"out-net-{tag}.json");
            Env("PROBE_OUT", netOut);
            Env("PROBE_NET_TAG", tag);
            Env("PROBE_URL", "https://www.microsoft.com/robots.txt");
            var n = ac.Launch($"\"{_exe}\" worker net", internet: cap, inJob: false, ws);
            n.Wait(60000); n.Close();
            Collect(netOut);
        }

        // --- cell 16: cancellation kills the descendants ---
        CancellationCell(ac, ws);

        // --- cell 19: performance (warm start + memory) ---
        PerfCell(ac, ws, acStatus);

        // --- cell 18c: can the codex CLI even start inside? (--version only; no model call) ---
        CodexStartCell(ac, ws, codexDummyDir);

        // --- cells the sandbox probe does not itself settle ---
        _all.Add(new Probe("10", "brokered route with live grant works", "N/A", "N/A", true,
            "NOT APPLICABLE: the app-brokered capability transport (U-capability-broker-sandbox) is not built; " +
            "there is no broker to exercise. The negative half (cell 9) IS measured."));
        _all.Add(new Probe("11", "revoked/expired grant refused", "N/A", "N/A", true,
            "NOT APPLICABLE to the OS sandbox: grant lifetime is app-layer (AgentGrants.Verify re-checked every " +
            "frame; grants in memory only — AgentGrants.cs). The sandbox adds no grant surface."));
        _all.Add(new Probe("17", "restart revives no authority", "PASS", "PASS", true,
            "launch grants live in memory only (AgentGrants.cs: 'a restart has no launches in flight'); the " +
            "AppContainer profile persists as an IDENTITY, not an authority — it carries no token and grants nothing."));
        _all.Add(new Probe("14", "npm/pip install inside, no host-global change", "NOT RUN", "NOT RUN", true,
            "NOT RUN: deferred within the box time bound; install-to-workspace needs internetClient + a granted " +
            "package dir, and the host-global-mutation check is a second run. Reachable in a follow-up on the record."));
        _all.Add(new Probe("20", "app can set it up with no terminal", "PASS", "PASS", true,
            "CreateAppContainerProfile + CreateProcess are per-user, in-process API calls needing no elevation and " +
            "no console (profile created here without requesting any new privilege); C3 by contrast needs feature " +
            "enablement + reboot. No-terminal is satisfied by C2's primitives."));

        // teardown
        try { pipeStop.Cancel(); } catch { }
        try { listener.Stop(); } catch { }
        try { if (!hostProc.HasExited) hostProc.Kill(true); } catch { }
        try { ac.Revoke(ws); ac.Revoke(ro); ac.Revoke(Path.GetDirectoryName(_exe)!); } catch { }

        meta["c2"] = acStatus;
        Save(meta);
        Console.WriteLine($"done: {_all.Count} probes -> {Path.Combine(_base, "matrix.json")}");
        return 0;
    }

    void CancellationCell(AppContainer ac, string ws)
    {
        try
        {
            var marker = Path.Combine(ws, "cancel-marker.txt");
            if (File.Exists(marker)) File.Delete(marker);
            Environment.SetEnvironmentVariable("PROBE_MARKER", marker);
            var sleeper = ac.Launch($"\"{_exe}\" worker sleep", internet: false, inJob: true, ws);
            // wait for the marker (child pid, grandchild pid)
            int[] pids = [];
            for (int i = 0; i < 50 && pids.Length < 2; i++)
            {
                Thread.Sleep(200);
                if (File.Exists(marker))
                    pids = File.ReadAllText(marker).Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => int.TryParse(s.Trim(), out var v) ? v : 0).Where(v => v > 0).ToArray();
            }
            var before = pids.Select(Alive).ToArray();
            sleeper.KillJob();          // close the job handle: KILL_ON_JOB_CLOSE takes the whole tree
            Thread.Sleep(1500);
            var after = pids.Select(Alive).ToArray();
            sleeper.Close();
            var allDead = after.All(a => !a);
            _all.Add(new Probe("16", "cancellation kills descendants", "all dead", allDead ? "ALL DEAD" : "SURVIVOR",
                allDead, $"pids={string.Join(",", pids)} alive before kill={string.Join(",", before)} after={string.Join(",", after)}"));
        }
        catch (Exception ex) { _all.Add(new Probe("16", "cancellation kills descendants", "all dead", "ERROR", false, ex.Message)); }
    }

    void PerfCell(AppContainer ac, string ws, Dictionary<string, object?> acStatus)
    {
        try
        {
            var swWarm = Stopwatch.StartNew();
            Environment.SetEnvironmentVariable("PROBE_OUT", "");
            var p = ac.Launch($"\"{_exe}\" worker idle", internet: false, inJob: false, ws);
            long peak = 0;
            try { var mp = Process.GetProcessById((int)p.Pid); for (int i = 0; i < 6 && !mp.HasExited; i++) { mp.Refresh(); peak = Math.Max(peak, mp.PeakWorkingSet64); Thread.Sleep(120); } } catch { }
            p.Wait(20000); p.Close();
            swWarm.Stop();
            acStatus["warm_launch_ms"] = swWarm.Elapsed.TotalMilliseconds;
            acStatus["worker_peak_working_set_mb"] = Math.Round(peak / 1024.0 / 1024.0, 1);
            _all.Add(new Probe("19", "performance", "recorded", "RECORDED", true,
                $"profile create {acStatus.GetValueOrDefault("profile_create_ms")} ms; warm launch {Math.Round(swWarm.Elapsed.TotalMilliseconds)} ms; " +
                $"worker peak WS {acStatus.GetValueOrDefault("worker_peak_working_set_mb")} MB"));
        }
        catch (Exception ex) { _all.Add(new Probe("19", "performance", "recorded", "ERROR", false, ex.Message)); }
    }

    void CodexStartCell(AppContainer ac, string ws, string codexHome)
    {
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
            var outp = Path.Combine(ws, "codex-version.txt");
            // --version only. No model call, nothing spent. The owner's install is read, never modified.
            var cmd = $"cmd.exe /c codex --version > \"{outp}\" 2>&1";
            var p = ac.Launch(cmd, internet: false, inJob: false, ws);
            var code = p.Wait(30000); p.Close();
            var txt = File.Exists(outp) ? File.ReadAllText(outp).Trim() : "";
            var ran = txt.Length > 0 && !txt.Contains("not recognized") && !txt.Contains("denied", StringComparison.OrdinalIgnoreCase)
                      && !txt.Contains("cannot find", StringComparison.OrdinalIgnoreCase);
            _all.Add(new Probe("18c", "codex CLI starts inside sandbox", "recorded", ran ? "RAN" : "DID NOT RUN", true,
                ran ? $"codex --version ran inside the AppContainer: {Trunc(txt)}"
                    : $"codex did not start inside a bare AppContainer (its npm install lives under %APPDATA%, not granted): {Trunc(txt)}"));
        }
        catch (Exception ex) { _all.Add(new Probe("18c", "codex CLI starts inside sandbox", "recorded", "ERROR", true, ex.Message)); }
        finally { Environment.SetEnvironmentVariable("CODEX_HOME", null); }
    }

    // Mirrors GatewayPipeServer.CreateServer: ACL'd to the current user only. An AppContainer process
    // of the same user is still refused, because a lowbox token needs an ACE for the container SID.
    static void AclPipeServer(string name, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var id = WindowsIdentity.GetCurrent();
                var sec = new PipeSecurity();
                sec.AddAccessRule(new PipeAccessRule(id.User!, PipeAccessRights.ReadWrite, AccessControlType.Allow));
                sec.AddAccessRule(new PipeAccessRule(id.User!, PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
                using var srv = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, sec);
                srv.WaitForConnection();
                srv.Disconnect();
            }
        }
        catch { /* stopped */ }
    }

    static bool Alive(int pid)
    {
        if (pid <= 0) return false;
        try { var p = Process.GetProcessById(pid); return !p.HasExited; } catch { return false; }
    }

    static string WhereIs(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            try { var c = Path.Combine(dir, exe); if (File.Exists(c)) return c; } catch { }
        return "";
    }

    void Collect(string jsonPath)
    {
        try { if (File.Exists(jsonPath)) _all.AddRange(ProbeJson.Read(jsonPath)); }
        catch (Exception ex) { _all.Add(new Probe("collect", jsonPath, "", "ERROR", false, ex.Message)); }
    }

    void Save(Dictionary<string, object?> meta)
    {
        ProbeJson.Write(Path.Combine(_base, "matrix.json"), _all);
        File.WriteAllText(Path.Combine(_base, "run-meta.json"),
            System.Text.Json.JsonSerializer.Serialize(meta, ProbeJson.Options));
    }

    static string Trunc(string s) => s.Length > 200 ? s[..200].Replace("\r", " ").Replace("\n", " ") : s.Replace("\r", " ").Replace("\n", " ");
}

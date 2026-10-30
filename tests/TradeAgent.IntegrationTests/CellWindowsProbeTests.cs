using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>A row of the Windows cell probe: run on Windows, where a missing prerequisite FAILS; skipped elsewhere, where there is no AppContainer.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "a Windows cell row: CI's windows job runs it (Category=Cell, CellScope=Windows); off Windows there is no AppContainer";
    }
}

/// <summary>
/// A Windows row that changes the MACHINE (a firewall rule): run only where <c>TA_CELL_WINDOWS_ADMIN=1</c> says the machine is CI's own,
/// elevated and thrown away after — there a missing elevation FAILS. Anywhere else, the tests box included, it is NOT RUN and says so:
/// nothing of ours changes a machine somebody owns.
/// </summary>
public sealed class WindowsAdminFactAttribute : FactAttribute
{
    public const string Variable = "TA_CELL_WINDOWS_ADMIN";

    public WindowsAdminFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "a Windows cell row: CI's windows job runs it; off Windows there is no AppContainer";
        else if (Environment.GetEnvironmentVariable(Variable) != "1")
            Skip = $"NOT RUN: this row places a firewall rule, so it runs only on CI's elevated, disposable windows runner ({Variable}=1)";
    }
}

/// <summary>
/// THE WINDOWS CELL PROBE (<c>U-cell-windows</c> item 1; the survey's open questions (a)–(d), R-containment-decision.md route C2): the
/// pinned codex Windows binary's <c>exec-server --listen stdio</c> inside a STABLE AppContainer and a kill-on-close job, launched
/// suspended → assigned → resumed by <see cref="AppContainerCell"/>, and the reach that comes with it — files, the window station,
/// loopback, the LAN, port 443 and every other port with and without the firewall rule the egress decision names, and the DNS that rule
/// leaves. Each row prints what it SAW; it asserts what the provider will rely on, and only what was observed.
///
/// <para><b>Never skipped on Windows</b> (but the firewall row off CI, which changes the machine): no pinned codex, no curl, a profile
/// Windows would not make — each FAILS naming the prerequisite. CI runs the category in the windows job's first shard.</para>
/// </summary>
[Trait("Category", "Cell"), Trait("CellScope", "Windows")]
[SupportedOSPlatform("windows")]
public sealed class CellWindowsProbeTests : IDisposable
{
    const string Planted = "dummy-owner-login";
    readonly ITestOutputHelper _out;
    readonly string _root, _ws, _ro, _home, _codexHome, _outside, _profile, _sid;
    readonly List<string> _granted = [];

    public CellWindowsProbeTests(ITestOutputHelper output)
    {
        _out = output;
        _root = Path.Combine(TestEnv.Home, "wcell-" + Guid.NewGuid().ToString("n")[..8]);
        _ws = Dir("ws");
        _ro = Dir("ro");
        _home = Dir("home");
        _codexHome = Dir("codex-home");
        _outside = Dir("outside");
        File.WriteAllText(Path.Combine(_ro, "in.txt"), "shared-input");
        File.WriteAllText(Path.Combine(_outside, "auth.json"), Planted);
        _profile = "TradeAgent.Probe." + Guid.NewGuid().ToString("n")[..12];
        _sid = AppContainerCell.Profile(_profile, "TradeAgent cell probe (a test; deleted when it ends)");
        Directory.CreateDirectory(Path.Combine(_ws, ".local", "Packages", _profile, "AC", "Temp"));
        Grant(_ws, FileSystemRights.Modify);
        foreach (var d in new[] { _ro, _home, _codexHome }) Grant(d, FileSystemRights.ReadAndExecute);
    }

    string Dir(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    void Grant(string path, FileSystemRights rights)
    {
        AppContainerCell.Grant(path, _sid, rights);
        _granted.Add(path);
    }

    public void Dispose()
    {
        foreach (var p in _granted) try { AppContainerCell.Revoke(p, _sid); } catch (Exception) { /* the folder goes with the home */ }
        _out.WriteLine($"profile {_profile} deleted: {AppContainerCell.DeleteProfile(_profile)}");
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { /* swept with the test home */ }
    }

    static void Require(bool ok, string what) => Assert.True(ok, $"missing prerequisite for the Windows cell rows: {what}");

    static string System32 => Environment.GetFolderPath(Environment.SpecialFolder.System);

    /// <summary>"Client" or "Server", as Windows records its own installation type.</summary>
    static string Edition => Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "InstallationType", "") as string ?? "";
    static string Windows => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>The whole environment a probe cell starts with: the system's own names, the cell's home and codex home, TEMP the workspace.</summary>
    List<KeyValuePair<string, string>> Env(string? codexBin = null) =>
    [
        new("SystemRoot", Windows), new("windir", Windows), new("ComSpec", Path.Combine(System32, "cmd.exe")),
        new("PATHEXT", ".COM;.EXE;.BAT;.CMD"),
        new("PATH", string.Join(';', new[] { codexBin, System32, Windows }.Where(p => p is not null))),
        new("USERPROFILE", _home), new("HOME", _home), new("CODEX_HOME", _codexHome), new("TEMP", _ws), new("TMP", _ws),
        // WINDOWS REWRITES THREE OF THESE for an AppContainer (measured on Windows 11 26200): LOCALAPPDATA becomes
        // <given>\Packages\<profile>\AC and TEMP and TMP <that>\Temp — and a block with no LOCALAPPDATA is refused (error 203). Given
        // inside the workspace, the rewritten TEMP is the workspace's too; the folder is made before the start.
        new("LOCALAPPDATA", LocalAppData), new("APPDATA", Path.Combine(_home, "AppData", "Roaming")),
    ];

    string LocalAppData => Path.Combine(_ws, ".local");

    /// <summary>Runs one program in the probe cell to its end (60 s at most) and returns its exit code and output.</summary>
    async Task<(int Code, string Out)> Run(string program, IReadOnlyList<string> args, IReadOnlyList<string>? caps = null, bool ownDesktop = true)
    {
        using var p = AppContainerCell.Start(program, args, _ws, Env(), _sid, caps ?? [], ownDesktop: ownDesktop);
        p.StandardInput!.Close();
        var so = p.StandardOutput!.ReadToEndAsync();
        var se = p.StandardError!.ReadToEndAsync();
        Require(p.WaitForExit(60_000), $"{program} ended within 60 s");
        var text = (await so) + (await se);
        _out.WriteLine($"> {Path.GetFileName(program)} {string.Join(' ', args)}  [caps {(caps is { Count: > 0 } ? string.Join(',', caps) : "none")}; desktop {(ownDesktop ? p.Desktop : "the caller's")}]");
        Assert.True(ownDesktop == p.Desktop.StartsWith("TradeAgent-cell-", StringComparison.Ordinal), $"the cell's desktop: '{p.Desktop}'");
        _out.WriteLine($"  exit {p.ExitCode} (0x{p.ExitCode:X8}): {text.Trim().Replace("\r", "").Replace('\n', ' ')}");
        return (p.ExitCode, text);
    }

    // ---- the pinned codex, from CI's step or the vendor's archive held to this build's pins ------------------------------------

    /// <summary>
    /// THE PINNED WINDOWS CODEX: <c>TA_CELL_CODEX</c> where CI's step unpacked it, else the release archive this build pins, downloaded
    /// into the test root and held to the archive's and the program's SHA-256 — never another codex.
    /// </summary>
    static string PinnedCodex()
    {
        var plan = RuntimeCatalog.BuiltIn(HostPlatform.Current).First(m => m.Id == "codex").Install;
        var path = Environment.GetEnvironmentVariable("TA_CELL_CODEX");
        if (string.IsNullOrEmpty(path))
        {
            var dir = Path.Combine(TestEnv.Root, "codex-" + plan.Version + "-windows");
            path = Path.Combine(dir, plan.ExecutableInArchive!.Replace('/', '\\'));
            if (!File.Exists(path))
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                var bytes = http.GetByteArrayAsync(plan.Url).GetAwaiter().GetResult();
                var got = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                Require(got == plan.Sha256, $"the pinned codex archive {plan.Url} hashes to {got}, not this build's pin {plan.Sha256}");
                Directory.CreateDirectory(dir);
                using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gz, dir, overwriteFiles: true);
            }
        }
        Require(File.Exists(path), $"the pinned windows codex (TA_CELL_CODEX or the pinned archive; got '{path}')");
        using var f = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
        Require(hash == plan.ExecutableSha256, $"{path} hashes to {hash}, not this build's codex pin {plan.ExecutableSha256}");
        return path;
    }

    /// <summary>The package root (the folder holding <c>bin\codex.exe</c>), granted read and run.</summary>
    string GrantCodex(string codex)
    {
        var package = Path.GetDirectoryName(Path.GetDirectoryName(codex)!)!;
        Grant(package, FileSystemRights.ReadAndExecute);
        return package;
    }

    // ---- (c) codex exec-server in the container --------------------------------------------------------------------------------

    /// <summary>
    /// ROUTE (a)'S LEG IN AN APPCONTAINER: the pinned <c>codex exec-server --listen stdio</c> with no capability and no window-station grant,
    /// its codex home and home EMPTY and read-only, answers <c>initialize</c>, an empty <c>environmentConfig/read</c> (0 layers) and
    /// <c>process/start</c> of <c>cmd /c cd</c> printing the workspace. Printed: what its children see as their home, and what it wrote.
    /// </summary>
    [WindowsFact]
    public async Task Exec_server_answers_in_an_appcontainer_with_workspace_parity()
    {
        var codex = PinnedCodex();
        GrantCodex(codex);
        using var p = AppContainerCell.Start(codex, ["exec-server", "--listen", "stdio"], _ws, Env(Path.GetDirectoryName(codex)), _sid, []);
        Assert.True(p.Holds(p.Id), "the exec-server is in its cell's job");
        using var es = new ExecServerClient(p.StandardInput!, p.StandardOutput!, p.StandardError!, () => p.WaitForExit(10_000));
        var init = await es.CallAsync("initialize", new { clientName = "codex-environment", resumeSessionId = (string?)null }, default);
        _out.WriteLine($"initialize: {(init is { Ok: true } ? "ok" : "FAILED " + es.Last)}; exited {p.HasExited} code {p.ExitCode}");
        _out.WriteLine("exec-server stderr: " + es.Stderr.Trim());
        Assert.True(init is { Ok: true }, "initialize: " + es.Last + " stderr: " + es.Stderr);
        es.Notify("initialized", new { });
        var config = await es.CallAsync("environmentConfig/read", new
        {
            cwd = new Uri(_ws).AbsoluteUri, configPaths = new[] { new[] { "mcp_servers" } }, requirementsPaths = new[] { new[] { "mcp_servers" } }
        }, default);
        var layers = config is { Ok: true } c && c.Result.TryGetProperty("config", out var cfg) && cfg.TryGetProperty("layers", out var l)
            ? l.GetArrayLength() : -1;
        _out.WriteLine($"environmentConfig/read: {(config is { Ok: true } ? layers + " layer(s)" : "FAILED " + es.Last)}");
        var cmd = Path.Combine(System32, "cmd.exe");
        // THE CHILD'S INPUT: the null device first, as codex gives a command that reads none; then a pipe. Windows Server 2025 (CI's
        // image) refuses an AppContainer the null device where Windows 11 26200 grants it — measured — so both are printed, and the
        // row holds on the first that gives the workspace.
        var (exit, output) = await es.RunAsync(_ws, [cmd, "/d", "/c", "cd"], default);
        _out.WriteLine($"process/start cmd /c cd (input: the null device): exit {exit}: {output.Trim()}");
        var piped = false;
        if (exit != 0)
        {
            piped = true;
            (exit, output) = await es.RunAsync(_ws, [cmd, "/d", "/c", "cd"], default, pipeStdin: true);
            _out.WriteLine($"process/start cmd /c cd (input: a pipe): exit {exit}: {output.Trim()}");
        }
        var (_, seen) = await es.RunAsync(_ws, [cmd, "/d", "/c", "echo USERPROFILE=%USERPROFILE% LOCALAPPDATA=%LOCALAPPDATA% APPDATA=%APPDATA% TEMP=%TEMP%"], default, piped);
        _out.WriteLine($"a child's home: {seen.Trim()}");
        _out.WriteLine("exec-server stderr after: " + es.Stderr.Trim().Replace('\n', ' '));
        _out.WriteLine("the workspace after: " + string.Join(", ", Directory.EnumerateFileSystemEntries(_ws, "*", SearchOption.AllDirectories).Select(e => Path.GetRelativePath(_ws, e))));
        var ac = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", _profile);
        _out.WriteLine($"the container's own folder {ac}: " + (Directory.Exists(ac)
            ? string.Join(", ", Directory.EnumerateFileSystemEntries(ac, "*", SearchOption.AllDirectories).Select(e => Path.GetRelativePath(ac, e)).Take(40))
            : "absent"));
        _out.WriteLine($"this host: Windows {Edition} {Environment.OSVersion.Version}");
        if (Edition == "Server")
        {
            // WINDOWS SERVER (CI's image, 26100) REFUSES WHAT WINDOWS 11 GRANTS (measured: CI runs 41110438315, 41116396083 against the
            // tests box's 26200): the pinned codex cannot canonicalize its own read-only home from an AppContainer there — so
            // environmentConfig/read fails — and a child given the null device is refused. Asserted as what this edition does, so the
            // day it changes this row says so; the route is claimed on Windows 11 alone, where the row below holds.
            Assert.Contains("failed to canonicalize CODEX_HOME", es.Stderr, StringComparison.Ordinal);
            Assert.Equal(-1, layers);
            Assert.True(piped, "on Server a child given the null device is refused");
        }
        else
        {
            Assert.Equal(0, layers);
            Assert.False(piped, "on Windows 11 a child given the null device starts");
        }
        Assert.Equal(0, exit);
        Assert.Equal(_ws.TrimEnd('\\'), output.Split('\n')[0].Trim().TrimEnd('\\'), ignoreCase: true);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_codexHome));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_home));
    }

    /// <summary>
    /// THE WINDOW STATION (the survey's open question): with no ACE on the owner's window station or desktop, the pinned codex dies
    /// 0xC0000142 where cmd and curl start (measured on Windows 11 26200) — so every cell runs on a window station and desktop of its
    /// own (<see cref="PrivateDesktop"/>), and the owner's are never granted. Here codex, cmd and curl start there; cmd prints what its
    /// environment became.
    /// </summary>
    [WindowsFact]
    public async Task Native_programs_start_on_the_cells_own_desktop()
    {
        var codex = PinnedCodex();
        GrantCodex(codex);
        // THE CONTROL: on the window station this test runs on, which no ACE grants the container, codex cannot start — so a cell
        // that starts below is on its own, and the owner's station and desktop are not the container's.
        // THE CONTROL IS PRINTED, NOT ASSERTED: whether the caller's station admits an AppContainer is the HOST's — closed on the tests
        // box (0xC0000142, 26200), open on CI's runner session (exit 0, run 41116396083). The cell never depends on it: it is always
        // started on a station of its own, which the line below asserts by name.
        var (control, _) = await Run(codex, ["--version"], ownDesktop: false);
        _out.WriteLine(control == unchecked((int)0xC0000142)
            ? "the caller's window station is closed to an AppContainer on this host"
            : $"the caller's window station is OPEN to an AppContainer on this host (exit {control})");
        var (codexCode, codexOut) = await Run(codex, ["--version"]);
        var (cmdCode, _) = await Run(Path.Combine(System32, "cmd.exe"), ["/d", "/c", "ver & set"]);
        var (curlCode, _) = await Run(Path.Combine(System32, "curl.exe"), ["--version"]);
        Assert.Equal(0, codexCode);
        Assert.Contains("0.160.1", codexOut, StringComparison.Ordinal);
        Assert.Equal(0, cmdCode);
        Assert.Equal(0, curlCode);
    }

    /// <summary>
    /// FILES: the workspace written, the read-only input read and not written, and what no ACE names — a folder of the account's own
    /// beside the cell's holding a dummy login, and the account's real profile folder (listed only) — denied.
    /// </summary>
    [WindowsFact]
    public async Task Files_outside_the_grants_are_denied()
    {
        var cmd = Path.Combine(System32, "cmd.exe");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // NO QUOTES INSIDE: cmd reads its command line raw and a C-runtime \" escape is not one to it; the test paths hold no space.
        // NOTHING SENT TO NUL: CI's Windows Server 2025 refuses an AppContainer the null device, which would fail every probe that
        // writes there whatever it read — each probe's output goes to a file of the workspace instead, and NUL is a line of its own.
        Require(!$"{_ws}{_ro}{_outside}{profile}".Contains(' '), "test paths without a space");
        var sink = Path.Combine(_ws, "sink.txt");
        var (_, o) = await Run(cmd, ["/d", "/c",
            $"(echo x> {_ws}\\made.txt && echo WS-WRITE-OPEN || echo WS-WRITE-DENIED) & " +
            $"(type {_ro}\\in.txt > {sink} && echo RO-READ-OPEN || echo RO-READ-DENIED) & " +
            $"(echo x>> {_ro}\\in.txt && echo RO-WRITE-OPEN || echo RO-WRITE-DENIED) & " +
            $"(type {_outside}\\auth.json > {sink} && echo OUTSIDE-READ-OPEN || echo OUTSIDE-READ-DENIED) & " +
            $"(dir {profile} > {sink} && echo PROFILE-LIST-OPEN || echo PROFILE-LIST-DENIED) & " +
            "(echo x> nul && echo NUL-OPEN || echo NUL-DENIED)"]);
        _out.WriteLine("[host] the read-only input's ACL: " + new DirectoryInfo(_ro).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        Assert.Contains("WS-WRITE-OPEN", o, StringComparison.Ordinal);
        Assert.Contains("RO-READ-OPEN", o, StringComparison.Ordinal);
        Assert.Contains("RO-WRITE-DENIED", o, StringComparison.Ordinal);
        Assert.Contains("OUTSIDE-READ-DENIED", o, StringComparison.Ordinal);
        Assert.Contains("PROFILE-LIST-DENIED", o, StringComparison.Ordinal);
        Assert.Equal("shared-input", File.ReadAllText(Path.Combine(_ro, "in.txt")));
    }

    // ---- the network ------------------------------------------------------------------------------------------------------------

    const string Https = "https://github.com/", Http80 = "http://github.com/";

    Task<(int Code, string Out)> Curl(string url, IReadOnlyList<string>? caps, int seconds = 10) =>
        Run(Path.Combine(System32, "curl.exe"), ["-sS", "-o", Path.Combine(_ws, "curl.out"), "-w", "HTTP=%{http_code}", "--connect-timeout", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--max-time", (seconds + 5).ToString(System.Globalization.CultureInfo.InvariantCulture), url], caps);

    /// <summary>A connect the host's own listener sees or not: (curl's word, whether it arrived).</summary>
    async Task<(string Curl, bool Arrived)> Reach(IPAddress listen, string host, IReadOnlyList<string>? caps)
    {
        var listener = new TcpListener(listen, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var (code, o) = await Curl($"http://{host}:{port}/", caps, 4);
            var arrived = listener.Pending();
            _out.WriteLine($"  {host}:{port} — curl exit {code}; the listener {(arrived ? "WAS reached" : "was not reached")}");
            return ($"exit {code}: {o.Trim()}", arrived);
        }
        finally { listener.Stop(); }
    }

    /// <summary>This host's own IPv4 address on an interface that is up: a private (RFC 1918) one first, else any but loopback and link-local.</summary>
    static IPAddress? OwnLanAddress()
    {
        var all = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(u => u.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && a.GetAddressBytes()[0..2] is not [169, 254]).ToList();
        static bool Private(IPAddress a) => a.GetAddressBytes() is [10, ..] or [192, 168, ..] || a.GetAddressBytes() is [172, var b, ..] && b is >= 16 and <= 31;
        return all.FirstOrDefault(Private) ?? all.FirstOrDefault();
    }

    /// <summary>EGRESS NONE: with no capability the cell reaches neither the internet on 443 nor this host's loopback.</summary>
    [WindowsFact]
    public async Task Without_a_capability_the_cell_reaches_nothing()
    {
        var (code, o) = await Curl(Https, []);
        var (_, loop) = await Reach(IPAddress.Loopback, "127.0.0.1", []);
        Assert.NotEqual(0, code);
        Assert.DoesNotContain("HTTP=2", o, StringComparison.Ordinal);
        Assert.DoesNotContain("HTTP=3", o, StringComparison.Ordinal);
        Assert.False(loop, "loopback reached from a cell with no capability");
    }

    /// <summary>
    /// internetClient ALONE (the survey's (a)): 443 and 80 both reached — every outbound port, which is why the rule exists; loopback,
    /// the LAN and a direct DNS query to a public resolver printed as measured.
    /// </summary>
    [WindowsFact]
    public async Task Internet_client_alone_opens_every_port_and_loopback_stays_shut()
    {
        string[] caps = [AppContainerCell.InternetClient];
        var (c443, o443) = await Curl(Https, caps);
        var (c80, o80) = await Curl(Http80, caps);
        var (_, loop) = await Reach(IPAddress.Loopback, "127.0.0.1", caps);
        // THIS HOST'S OWN LAN ADDRESS, printed: another machine of the LAN is not measured here (NOT RUN) — nothing of ours probes a
        // network somebody else owns.
        if (OwnLanAddress() is { } lan) await Reach(IPAddress.Any, lan.ToString(), caps);
        else _out.WriteLine("  this host has no LAN IPv4 address to measure from");
        await Run(Path.Combine(System32, "nslookup.exe"), ["github.com", "8.8.8.8"], caps);
        Assert.True(c443 == 0 && o443.Contains("HTTP=", StringComparison.Ordinal) && !o443.Contains("HTTP=000", StringComparison.Ordinal), "443 with internetClient: " + o443);
        Assert.True(c80 == 0 && !o80.Contains("HTTP=000", StringComparison.Ordinal), "80 with internetClient: " + o80);
        Assert.False(loop, "loopback reached from an internetClient cell");
    }

    /// <summary>
    /// THE EGRESS DECISION'S RULE (CI only: it changes the machine): outbound TCP to every port but 443 and every UDP datagram blocked for
    /// the container's SID. Then 443 is reached, 80 is not, a direct DNS query to a public resolver is not, and a NAME still resolves —
    /// through the system's DNS client, outside the cell. The firewall's profiles are printed first.
    /// </summary>
    [WindowsAdminFact]
    public async Task The_443_rule_leaves_443_and_the_systems_dns_alone()
    {
        Require(new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),
            $"an elevated session ({WindowsAdminFactAttribute.Variable}=1 says this runner is CI's own)");
        string[] caps = [AppContainerCell.InternetClient];
        var name = "TradeAgent cell probe " + _profile;
        Ps("Get-NetFirewallProfile | ForEach-Object { \"$($_.Name) enabled=$($_.Enabled) outbound=$($_.DefaultOutboundAction)\" }");
        try
        {
            Ps($"New-NetFirewallRule -DisplayName '{name}' -Direction Outbound -Action Block -Protocol TCP -RemotePort 1-442,444-65535 -Package '{_sid}' | Out-Null; " +
               $"New-NetFirewallRule -DisplayName '{name}' -Direction Outbound -Action Block -Protocol UDP -Package '{_sid}' | Out-Null; " +
               $"(Get-NetFirewallRule -DisplayName '{name}').Count");
            var (c443, o443) = await Curl(Https, caps);
            var (c80, o80) = await Curl(Http80, caps);
            var (_, dns) = await Run(Path.Combine(System32, "nslookup.exe"), ["-timeout=3", "-retry=1", "github.com", "8.8.8.8"], caps);
            Assert.True(c443 == 0 && !o443.Contains("HTTP=000", StringComparison.Ordinal), "443 under the rule: " + o443);
            Assert.True(c80 != 0, "80 under the rule: " + o80);
            // nslookup prints the name it resolved ("Name: github.com") only when the resolver ANSWERED the cell's own datagram.
            Assert.DoesNotContain("github.com", dns, StringComparison.OrdinalIgnoreCase);
        }
        finally { Ps($"Remove-NetFirewallRule -DisplayName '{name}'"); }
    }

    void Ps(string script)
    {
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        _out.WriteLine($"[host] {script[..Math.Min(80, script.Length)]}… exit {p.ExitCode}: {o.Trim().Replace("\r", "").Replace('\n', ' ')}");
        Require(p.ExitCode == 0, "powershell: " + o);
    }

    // ---- (d) the job: descendants and breakaway ----------------------------------------------------------------------------------

    /// <summary>
    /// A SHELL AND A DETACHED GRANDCHILD THAT LIVE UNTIL ENDED: both read the cell's standard input, which the test holds open, so neither
    /// ends by itself (a sleeper such as <c>ping</c> can end on its own — under the first form of this row a mutant without kill-on-close
    /// still read "every one ended" on the tests box, a vacuous row).
    /// </summary>
    const string Blockers = "start /b findstr x & findstr y";

    /// <summary>
    /// KILL ON CLOSE — WHAT THE APP'S CRASH DOES: the job's last handle closed with nothing asked of Windows, and the shell and its
    /// detached grandchild are gone.
    /// </summary>
    [WindowsFact]
    public async Task Closing_the_jobs_last_handle_ends_the_cell()
    {
        var cmd = Path.Combine(System32, "cmd.exe");
        var p = AppContainerCell.Start(cmd, ["/d", "/c", Blockers], _ws, Env(), _sid, []);
        try
        {
            IReadOnlyList<int> pids = [];
            for (var i = 0; i < 100 && pids.Count < 3; i++)
            {
                pids = p.ProcessIds();
                await Task.Delay(50);
            }
            _out.WriteLine($"the job held {string.Join(", ", pids)}");
            Assert.True(pids.Count >= 3, $"the shell and its two readers are in the job ({pids.Count})");
            p.ReleaseJob();
            var alive = pids.ToList();
            for (var i = 0; i < 100 && alive.Count > 0; i++)
            {
                await Task.Delay(50);
                alive = [.. alive.Where(Alive)];
            }
            _out.WriteLine($"after the last handle closed: {(alive.Count == 0 ? "every one ended" : "STILL RUNNING " + string.Join(", ", alive))}");
            Assert.Empty(alive);
        }
        finally { p.Dispose(); }

        static bool Alive(int pid)
        {
            try { using var q = Process.GetProcessById(pid); return !q.HasExited; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
    }

    /// <summary>
    /// DESCENDANTS END WITH THE JOB: a cell whose shell starts a detached grandchild (<c>start /b</c>) holds them all in its job, and
    /// ending the job ends every one of them.
    /// </summary>
    [WindowsFact]
    public async Task Descendants_are_in_the_job_and_end_with_it()
    {
        var cmd = Path.Combine(System32, "cmd.exe");
        using var p = AppContainerCell.Start(cmd, ["/d", "/c", Blockers], _ws, Env(), _sid, []);
        Assert.True(p.Holds(p.Id), "the shell is in its job");
        var most = 0;
        for (var i = 0; i < 100 && most < 3; i++)
        {
            most = Math.Max(most, p.ActiveProcesses);
            await Task.Delay(50);
        }
        _out.WriteLine($"the job held {most} process(es) at most before the cancel");
        Assert.True(most >= 3, $"the shell and its two readers are in the job ({most})");
        Assert.True(p.Kill(), "the job was ended");
        var left = p.ActiveProcesses;
        for (var i = 0; i < 100 && left > 0; i++)
        {
            await Task.Delay(50);
            left = p.ActiveProcesses;
        }
        _out.WriteLine($"after the cancel the job holds {left}");
        Assert.Equal(0, left);
        Assert.True(p.WaitForExit(5_000), "the shell itself ended within 5 s of the cancel");
    }
}

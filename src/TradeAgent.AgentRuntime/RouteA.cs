using System.Text;
using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// ROUTE (a) — THE HARNESS OUTSIDE, CODEX'S EXEC-SERVER IN A CELL (<c>U-cell-linux-c</c> item 4; <c>docs/ORCHESTRATION.md</c> § 2.8;
/// <c>R-exec-server</c> DECISION 1). The codex harness that holds the login runs in the attempt's <c>harness/</c> leaf with a
/// PER-ATTEMPT <c>CODEX_HOME</c>: the class's verified <c>config.toml</c> bytes, an <c>environments.toml</c> naming ONE stdio program —
/// <c>trade --spawn-contained --cgroup &lt;attempt&gt;/cell --cell &lt;policy&gt; codex exec-server --listen stdio</c> — with
/// <c>default = "cell"</c> and <c>include_local = false</c>, and <c>auth.json</c> a symlink to the seat home's (codex writes it in place,
/// <c>login/src/auth/storage.rs</c> at rust-v0.160.1, so a refresh reaches the seat home). Every model-directed shell, file, patch and
/// image action runs in the exec-server, in the cell: the workspace at the same path on both sides (the position's own home), the
/// cell's codex home and home empty and READ-ONLY, egress <c>none</c>, its environment the policy's alone.
///
/// <para><b>Item 1's probe decided the cell's codex home</b> (CI run 39700964111, kernel 7.0.0-1017-azure, Landlock ABI 8): under an EMPTY
/// READ-ONLY codex home the pinned exec-server starts, answers <c>initialize</c>, an empty <c>environmentConfig/read</c> (0 layers) and
/// <c>process/start</c> with workspace parity, writing nothing (only a warning that it could not make its PATH aliases, EACCES) — so the
/// home is read-only, never writable.</para>
///
/// <para>Here are the pure parts — the paths, the policy, the file — so the launch and the Linux rows build the same thing.</para>
/// </summary>
public static class RouteA
{
    /// <summary>The provider a <c>cell_proof</c> row names for this route: the pinned codex exec-server in a Linux cell.</summary>
    public const string Provider = "codex-exec-server-linux";

    /// <summary>
    /// THE WINDOWS PROVIDER (<c>U-cell-windows</c>): the pinned codex exec-server in an AppContainer slot inside a kill-on-close job, on a
    /// window station of its own — its OWN row set and profile, never the Linux rows' (<see cref="CellBenchmark"/>).
    /// </summary>
    public const string WindowsProvider = "codex-exec-server-windows-appcontainer";

    /// <summary>The provider a cell on <paramref name="platform"/> runs and is proved under.</summary>
    public static string ProviderOn(CellPlatform platform) => platform.Windows ? WindowsProvider : Provider;

    /// <summary>The harness a <c>cell_assurance</c> row names for this route.</summary>
    public const string Harness = "codex-0.160.1-exec-server-stdio";

    /// <summary>
    /// THE EGRESS OF AN EXECUTOR'S CELL, ONCE (<c>U-execution-environment</c> item 3; the owner's answer of 2026-10-21, ORGANISATION
    /// § 16: "Web (443) for every executor"): <see cref="CellEgress.Web"/> — TCP 443 and DNS over TCP, any host. Read by the cell's
    /// policy (<see cref="Policy"/>), by every admission of a launch under it (<see cref="Admission"/>'s callers) and by the attempt's
    /// assurance (<see cref="AssuranceRows"/>). Code: no file, setting, verb or pipe op reaches it (AUTH-001). Its loopback and LAN reach
    /// on 443 are GAP rows on the proof and on every attempt.
    /// </summary>
    public static CellEgress Egress => CellEgress.Web;

    /// <summary>The one environment <c>environments.toml</c> names, and its default.</summary>
    public const string EnvironmentId = "cell";

    /// <summary>Where the per-attempt homes live: the harness's under the seat home (it holds the sign-in's link), the cell's under the app's home.</summary>
    public sealed record Layout(string Key, string HarnessCodexHome, string CellCodexHome, string CellHome)
    {
        /// <summary>Every folder of this attempt's, for the end of the turn.</summary>
        public IEnumerable<string> Roots => [Path.GetDirectoryName(HarnessCodexHome)!, Path.GetDirectoryName(CellCodexHome)!];
    }

    /// <summary>The folder the cells' empty homes are made in: under the app's home, beside (never inside) <c>cells/</c> and <c>state/</c>.</summary>
    public static string CellHomes => Path.Combine(Paths.Home, "cell-homes");

    /// <summary>The folder the harnesses' per-attempt codex homes are made in: under the seat home, which no cell is ever granted.</summary>
    public static string HarnessHomes => Path.Combine(Paths.SeatHome, "attempts");

    /// <summary>One attempt's layout, by a key the caller makes unique (the attempt cgroup's own name).</summary>
    public static Layout LayoutFor(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException($"'{key}' is not a key a folder can be named by", nameof(key));
        return new Layout(key, Path.Combine(HarnessHomes, key, "codex"), Path.Combine(CellHomes, key, "codex"), Path.Combine(CellHomes, key, "home"));
    }

    /// <summary>
    /// THE PROGRAM ROOT A CELL RUNS CODEX FROM: the unpacked package (<c>&lt;root&gt;/bin/codex</c> beside <c>codex-resources/</c>) when the
    /// binary is the one the app installed under <see cref="Paths.Tools"/>, else the binary's own folder alone — never a parent that
    /// would grant a whole <c>~/.local</c> or <c>/usr</c>.
    /// </summary>
    public static string CodexRoot(string exe)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(exe))!;
        var package = Path.GetDirectoryName(dir);
        var tools = Path.TrimEndingDirectorySeparator(Paths.Tools);
        var how = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return Path.GetFileName(dir).Equals("bin", how) && package is not null && package.StartsWith(tools + Path.DirectorySeparatorChar, how) ? package : dir;
    }

    /// <summary>
    /// THE CELL'S POLICY: the workspace read-write; the cell's codex home and home read-only (both empty, both the app's); codex's own
    /// root read and run; the system roots; an environment named in full — no <c>CODEX_HOME</c>, <c>HOME</c> or bearer of the
    /// harness's — and egress <see cref="Egress"/> (<c>web</c>: <c>U-execution-environment</c>; <c>none</c> before it).
    /// </summary>
    public static CellPolicy Policy(string workspace, string codexExe, string codexRoot, string cellCodexHome, string cellHome) =>
        OperatingSystem.IsWindows() ? WindowsPolicy(workspace, codexExe, codexRoot, cellCodexHome, cellHome, Egress) :
        new(workspace, [cellCodexHome, cellHome], [codexRoot, .. CellPolicy.SystemRoots],
        [
            new("PATH", Path.GetDirectoryName(codexExe) + ":/usr/local/bin:/usr/bin:/bin"),
            new("HOME", cellHome),
            new("CODEX_HOME", cellCodexHome),
            new("LANG", "C.UTF-8"),
            .. CellPolicy.ToolEnvironment(workspace)
        ], Egress);

    /// <summary>
    /// THE WINDOWS CELL'S POLICY (<c>U-cell-windows</c>): the same grants — the workspace read-write, the cell's EMPTY codex home and home
    /// read-only, codex's own root read and run — and no system root (Windows grants every AppContainer its own system folders); an
    /// environment named in full, Windows-shaped: the system's names, the cell's home as USERPROFILE and HOME, LOCALAPPDATA inside the
    /// workspace (Windows rewrites it, and TEMP and TMP beneath it, for an AppContainer — measured), and nothing of the owner's.
    /// </summary>
    public static CellPolicy WindowsPolicy(string workspace, string codexExe, string codexRoot, string cellCodexHome, string cellHome, CellEgress egress)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system32 = Path.Combine(windows, "System32");
        return new(workspace, [cellCodexHome, cellHome], [codexRoot],
        [
            new("PATH", string.Join(';', Path.GetDirectoryName(codexExe), system32, windows)),
            new("SystemRoot", windows), new("windir", windows), new("ComSpec", Path.Combine(system32, "cmd.exe")),
            new("PATHEXT", ".COM;.EXE;.BAT;.CMD"),
            new("USERPROFILE", cellHome), new("HOME", cellHome), new("CODEX_HOME", cellCodexHome),
            new("LOCALAPPDATA", Path.Combine(workspace, ".local")), new("APPDATA", Path.Combine(cellHome, "AppData", "Roaming")),
            new("TEMP", workspace), new("TMP", workspace), new("LANG", "C.UTF-8"),
            .. CellPolicy.ToolEnvironment(workspace)
        ], egress);
    }

    /// <summary>
    /// THE HARNESS'S <c>environments.toml</c>: one stdio environment, the default, no local one. Its program is TradeAgent's own
    /// launcher, which joins <paramref name="cellLeaf"/>, enters the cell <paramref name="policyFile"/> describes and becomes
    /// <c>codex exec-server --listen stdio</c>; its only variable is the app's home, where the launcher finds its cell folder. Every
    /// string is a TOML literal the bytes of which are refused if they cannot be one.
    /// </summary>
    public static string EnvironmentsToml(IReadOnlyList<string> launcher, string? cellLeaf, string policyFile, string codexExe, string home)
    {
        if (launcher is not { Count: > 0 }) throw new ArgumentException("no launcher", nameof(launcher));
        // ON WINDOWS NO CGROUP (cellLeaf null): the stage runs in the harness's job and the cell in a job of the stage's, nested in it.
        string[] args =
        [
            .. launcher.Skip(1), ContainedLaunch.Flag, .. cellLeaf is null ? Array.Empty<string>() : [ContainedLaunch.CgroupFlag, cellLeaf],
            ContainedLaunch.CellFlag, policyFile, codexExe, "exec-server", "--listen", "stdio"
        ];
        var sb = new StringBuilder();
        sb.Append("default = ").Append(Literal(EnvironmentId)).Append('\n');
        sb.Append("include_local = false\n\n[[environments]]\n");
        sb.Append("id = ").Append(Literal(EnvironmentId)).Append('\n');
        sb.Append("program = ").Append(Literal(launcher[0])).Append('\n');
        sb.Append("args = [").Append(string.Join(", ", args.Select(Literal))).Append("]\n");
        sb.Append("env = { TRADEAGENT_HOME = ").Append(Literal(home)).Append(" }\n");
        return sb.ToString();
    }

    /// <summary>A TOML basic string, or a refusal: no control character is ever written into a file codex parses as its environment.</summary>
    static string Literal(string s)
    {
        if (s.Any(char.IsControl))
            throw new TradeAgentException(ErrorCode.CELL_UNAVAILABLE, $"'{s}' holds a control character and cannot be written into environments.toml");
        return "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>
    /// MAKES ONE ATTEMPT'S HOMES: the cell's codex home and home EMPTY (owner-only folders, granted read-only), and the harness's codex
    /// home with the class's verified <c>config.toml</c> bytes, <paramref name="environmentsToml"/>, and <c>auth.json</c> a symlink to
    /// <paramref name="seatAuth"/>. A layout that already exists is refused: one attempt, one set of homes.
    /// </summary>
    public static void Make(Layout layout, byte[] config, string environmentsToml, string seatAuth)
    {
        foreach (var root in layout.Roots)
            if (Directory.Exists(root)) throw new TradeAgentException(ErrorCode.CELL_UNAVAILABLE, $"{root} already exists: one attempt, one set of homes");
        foreach (var dir in new[] { layout.HarnessCodexHome, layout.CellCodexHome, layout.CellHome })
        {
            Directory.CreateDirectory(dir);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        OwnerOnlyFile.Write(Path.Combine(layout.HarnessCodexHome, "config.toml"), config);
        OwnerOnlyFile.Write(Path.Combine(layout.HarnessCodexHome, CellPolicy.CodexEnvironments), Encoding.UTF8.GetBytes(environmentsToml));
        var link = Path.Combine(layout.HarnessCodexHome, "auth.json");
        // ON WINDOWS A HARD LINK: a symlink needs Developer Mode or elevation there, and codex rewrites auth.json in place, so a link to the
        // same file carries a refresh to the seat home either way. Same volume by construction: both live under the seat home.
        if (OperatingSystem.IsWindows())
        {
            if (!CreateHardLink(link, seatAuth, IntPtr.Zero))
                throw new TradeAgentException(ErrorCode.CELL_UNAVAILABLE,
                    $"Windows would not link the harness's sign-in to the seat home's (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
        }
        else File.CreateSymbolicLink(link, seatAuth);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    static extern bool CreateHardLink(string link, string existing, IntPtr reserved);

    /// <summary>Removes one attempt's homes. Never throws: what stays grants nothing on its own and the next start's sweep sees it.</summary>
    public static void Remove(Layout layout)
    {
        foreach (var root in layout.Roots)
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* left for the sweep */ }
    }

    /// <summary>
    /// WHETHER A ROUTE (a) LAUNCH MAY START NOW, and under which proof (<c>U-cell-linux-c</c> items 4 and 5): a platform a cell runs on;
    /// a COUNTED proof of this provider — this machine, full kernel release, boot and build; a profile that recomputes to the proof's
    /// own (else the proof is revoked: the canary); and a policy whose egress class that proof's rows measured (L4). Anything else is a
    /// refusal in words — never a degraded launch, never a cell-class row without a proof.
    /// </summary>
    public static (CellProof? Proof, string? Refusal) Admission(CellRoute? route, string codexPin, CellPlatform platform, CellEgress egress)
    {
        var (proof, _, refusal) = AdmissionOn(route, codexPin, platform, egress);
        return (proof, refusal);
    }

    /// <summary>
    /// <see cref="Admission"/>, and the egress the cell then runs under. ON WINDOWS ONLY (the egress decision of 2026-10-30, the owner's to
    /// overrule): a launch asking <c>web</c> on a host whose proof measured only <c>none</c> — the firewall rule declined or failed, so
    /// <c>web</c> could not be shown to hold to 443 — runs under <c>none</c>, the proof's own class, and the refusal is not one. Linux is
    /// unchanged: there a proof without <c>web</c> refuses a <c>web</c> launch.
    /// </summary>
    public static (CellProof? Proof, CellEgress Egress, string? Refusal) AdmissionOn(CellRoute? route, string codexPin, CellPlatform platform, CellEgress egress)
    {
        if (platform.Windows && egress.Name == CellEgress.Web.Name && route?.Proofs.ProofOf(ProviderOn(platform)) is { } hostProof
            && !hostProof.Egress.Contains(CellEgress.Web.Name, StringComparer.Ordinal)
            && hostProof.Egress.Contains(CellEgress.None.Name, StringComparer.Ordinal))
            egress = CellEgress.None;
        var (proof, refusal) = Admit(route, codexPin, platform, egress);
        return (proof, egress, refusal);
    }

    static (CellProof? Proof, string? Refusal) Admit(CellRoute? route, string codexPin, CellPlatform platform, CellEgress egress)
    {
        const string none = "TradeAgent did not start the AI as a cell seat";
        if (route is null) return (null, $"{none}: this host was given no record of proven cells to launch one under.");
        if (platform.Refusal() is { } refusal) return (null, $"{none}: {refusal}");
        var provider = ProviderOn(platform);
        if (route.Proofs.ProofOf(provider) is not { } proof)
            return (null, $"{none}: no cell proof counts on this machine, kernel, boot and build. TradeAgent measures its cells itself at " +
                          "every start and records a proof only when every row holds; until then no cell seat starts.");
        // THE CLASS THE PROOF MEASURED, read off the proof itself (fix pass 1, R4): a proof names the egress classes its rows proved.
        if (!proof.Egress.Contains(egress.Name, StringComparer.Ordinal))
            return (null, $"{none}: its cell would have egress '{egress.Name}', and the proof it would run under measured only " +
                          $"{(proof.Egress.Count == 0 ? "no egress class" : string.Join(", ", proof.Egress))}.");
        var now = CellBenchmark.Profile(provider, platform, codexPin);
        if (now != proof.ProfileHash)
        {
            try { route.Proofs.Revoke(proof.Id, $"a launch recomputed the profile as {now[..12]}, not the proof's {proof.ProfileHash[..Math.Min(12, proof.ProfileHash.Length)]}"); }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException) { /* refused all the same */ }
            return (null, $"{none}: this computer's cell no longer matches the one its proof measured (the profile changed), so the proof " +
                          "was revoked. The next start measures it again.");
        }
        return (proof, null);
    }

    /// <summary>
    /// ROUTE (a)'S ASSURANCE PROFILE (<c>U-cell-linux-c</c> item 6; the survey's Q-b): per property, the cell's side and the harness's,
    /// with the attempt side added as the weaker. The harness is an unconfined codex holding the login, so every property the cell
    /// holds HARD is MEDIATED for the attempt — no model tool crosses, the process could — and presence and lanes do not move (Q2, Q5).
    /// GAPs are asserted: any process reads another's <c>/proc/*/cmdline</c>, and Landlock does not handle <c>stat</c> outside the grants.
    /// </summary>
    /// <para><b>The network, as the policy's egress</b> (<c>U-execution-environment</c> item 3): the row names <paramref name="egress"/>,
    /// and under <c>web</c> two GAP rows carry the reach the owner accepted with it — loopback and his LAN on 443 — under the proof's own
    /// row names, never as passes.</para>
    public static IReadOnlyList<AssuranceRow> AssuranceRows(long proofId, CellEgress egress, bool windows = false)
    {
        var proof = $"cell proof {proofId}";
        var web = egress.Name == CellEgress.Web.Name;
        if (!web && egress.Name != CellEgress.None.Name)
            throw new ArgumentException($"route (a)'s assurance knows egress none and web, not '{egress.Name}'", nameof(egress));
        if (windows) return WindowsAssuranceRows(proof, web);
        AssuranceRow Cell(string p, EnforcementClass c, string why) => new(p, AssuranceSide.Cell, c, $"{why}; {proof}");
        static AssuranceRow Harness(string p, EnforcementClass c, string why) => new(p, AssuranceSide.Harness, c, why);
        const string unconfined = "an unconfined codex holding the login: no model-directed tool crosses, the process could";
        return CellAssuranceStore.WithAttemptSide(
        [
            Cell("state", EnforcementClass.HardPlatform, "Landlock: state/ is granted to no cell (Guarded refuses it)"),
            Harness("state", EnforcementClass.Mediated, unconfined),
            Cell("holdout-ledgers", EnforcementClass.HardPlatform, "Landlock: under state/"),
            Harness("holdout-ledgers", EnforcementClass.Mediated, unconfined),
            Cell("login", EnforcementClass.HardPlatform, "the seat home is granted to no cell; the cell's environment is the policy's alone"),
            Harness("login", EnforcementClass.Mediated, "the harness holds it; code mode is refused before admission (Law 2)"),
            Cell("inbox", EnforcementClass.HardPlatform, "Landlock: the inbox and what holds it are granted to no cell"),
            Harness("inbox", EnforcementClass.Mediated, unconfined),
            Cell("ipc", EnforcementClass.HardPlatform, "seccomp: AF_UNIX socket() refused; Landlock abstract-socket scope"),
            Harness("ipc", EnforcementClass.Mediated, unconfined),
            Cell("signals-ptrace-environ", EnforcementClass.HardPlatform, "Landlock ABI >= 6 signal scope and ptrace restriction"),
            Harness("signals-ptrace-environ", EnforcementClass.Mediated, unconfined),
            Cell("network", EnforcementClass.HardPlatform, web
                ? "egress web: Landlock connect to TCP 443 alone and no bind, seccomp listen, fast-open and datagrams"
                : "egress none: Landlock connect and bind, seccomp listen and datagrams"),
            Harness("network", EnforcementClass.Advisory, "the harness reaches its provider and anything else its account can"),
            Cell("descendants-cancellation", EnforcementClass.HardPlatform, "the attempt's cgroup: cgroup.kill ends every descendant"),
            Harness("descendants-cancellation", EnforcementClass.HardPlatform, "the attempt's cgroup, harness leaf"),
            Cell("broker-reach", EnforcementClass.HardPlatform, web
                ? "egress web reaches TCP 443 alone, the door listens on an ephemeral loopback port, and no bearer is in the cell"
                : "egress none and no bearer in the cell"),
            Harness("broker-reach", EnforcementClass.HardApp, "the launch grant answers to the harness leaf alone"),
            Cell("order-permission", EnforcementClass.HardPlatform, web
                ? "no pipe and no grant in the cell; its one port is 443, where no TradeAgent listener is"
                : "no pipe, no port and no grant in the cell"),
            Harness("order-permission", EnforcementClass.HardApp, "the grant's role and the gateway's order rules"),
            Cell("cmdline", EnforcementClass.Gap, "/proc/*/cmdline of other processes is readable from a cell"),
            Harness("cmdline", EnforcementClass.Gap, "/proc/*/cmdline is readable"),
            Cell("stat", EnforcementClass.Gap, "Landlock does not handle stat/getattr outside the grants"),
            Harness("stat", EnforcementClass.Gap, unconfined),
            .. web ? WebGaps(proof) : [],
        ]);
    }

    /// <summary>
    /// THE WINDOWS CELL'S ASSURANCE (<c>U-cell-windows</c>): the same properties, held by an AppContainer slot, its job and its own window
    /// station, each a claim the Windows proof's rows measured. What no row measured on Windows is never HARD: <c>cmdline</c> and
    /// <c>stat</c> stay GAP, and under <c>web</c> another host of the LAN on 443 is a GAP (only this host's own address is measured).
    /// </summary>
    static IReadOnlyList<AssuranceRow> WindowsAssuranceRows(string proof, bool web)
    {
        AssuranceRow Cell(string p, EnforcementClass c, string why) => new(p, AssuranceSide.Cell, c, $"{why}; {proof}");
        static AssuranceRow Harness(string p, EnforcementClass c, string why) => new(p, AssuranceSide.Harness, c, why);
        const string unconfined = "an unconfined codex holding the login: no model-directed tool crosses, the process could";
        const string noAce = "AppContainer: no ACE names the cell's slot";
        return CellAssuranceStore.WithAttemptSide(
        [
            Cell("state", EnforcementClass.HardPlatform, $"{noAce} on state/ (Guarded refuses it)"),
            Harness("state", EnforcementClass.Mediated, unconfined),
            Cell("holdout-ledgers", EnforcementClass.HardPlatform, $"{noAce}: under state/"),
            Harness("holdout-ledgers", EnforcementClass.Mediated, unconfined),
            Cell("login", EnforcementClass.HardPlatform, $"{noAce} on the seat home; the cell's environment is the policy's alone"),
            Harness("login", EnforcementClass.Mediated, "the harness holds it; code mode is refused before admission (Law 2)"),
            Cell("inbox", EnforcementClass.HardPlatform, $"{noAce} on the inbox or what holds it"),
            Harness("inbox", EnforcementClass.Mediated, unconfined),
            Cell("ipc", EnforcementClass.HardPlatform, "AppContainer: no pipe of the app's grants the container, and loopback is closed to it"),
            Harness("ipc", EnforcementClass.Mediated, unconfined),
            Cell("signals-ptrace-environ", EnforcementClass.HardPlatform, "AppContainer at Low integrity: no process outside the cell opens to it"),
            Harness("signals-ptrace-environ", EnforcementClass.Mediated, unconfined),
            Cell("network", EnforcementClass.HardPlatform, web
                ? "egress web: internetClient, with the firewall rule blocking every TCP port but 443 and every datagram for the slot's SID"
                : "egress none: no capability, no network"),
            Harness("network", EnforcementClass.Advisory, "the harness reaches its provider and anything else its account can"),
            Cell("descendants-cancellation", EnforcementClass.HardPlatform, "the stage's kill-on-close job without breakaway, nested in the harness's"),
            Harness("descendants-cancellation", EnforcementClass.HardPlatform, "the harness's kill-on-close job, the app's"),
            Cell("broker-reach", EnforcementClass.HardPlatform, "loopback, where the door listens, is closed to an AppContainer, and nothing of the harness's sign-in is in the cell"),
            Harness("broker-reach", EnforcementClass.HardApp, "the launch grant answers to the harness's job"),
            Cell("order-permission", EnforcementClass.HardPlatform, "no pipe, no loopback and no grant in the cell"),
            Harness("order-permission", EnforcementClass.HardApp, "the grant's role and the gateway's order rules"),
            Cell("cmdline", EnforcementClass.Gap, "not measured on Windows"),
            Harness("cmdline", EnforcementClass.Gap, "any process of the account reads another's command line"),
            Cell("stat", EnforcementClass.Gap, "not measured on Windows"),
            Harness("stat", EnforcementClass.Gap, unconfined),
            .. web
                ? new[]
                {
                    new AssuranceRow(WindowsLanGap, AssuranceSide.Cell, EnforcementClass.Gap,
                        $"egress web: another host of the local network on 443 is not measured (this host's own address is, and is closed); {proof} records it as a GAP"),
                    new AssuranceRow(WindowsLanGap, AssuranceSide.Harness, EnforcementClass.Gap, "the harness reaches the local network and beyond"),
                }
                : [],
        ]);
    }

    /// <summary>The Windows proof's GAP row: another host of the LAN on 443, which no row measures.</summary>
    public const string WindowsLanGap = "gap-web-lan-other-hosts-443";

    /// <summary>
    /// WHAT WEB EGRESS LEAVES OPEN, SAID (the owner's answer, ORGANISATION § 16): from a <c>web</c> cell, TCP 443 on loopback and on the
    /// owner's LAN is reachable — a listener there is a peer the cell can talk to. Recorded under the proof's own GAP row names
    /// (<see cref="CellBenchmark.RowNames"/>), on every side, so the attempt's own class carries them; never a pass.
    /// </summary>
    static IEnumerable<AssuranceRow> WebGaps(string proof)
    {
        foreach (var (row, what) in new[] { (WebLoopbackGap, "TCP 443 on loopback"), (WebLanGap, "TCP 443 on the local network") })
        {
            yield return new(row, AssuranceSide.Cell, EnforcementClass.Gap, $"egress web lets the cell reach {what}; {proof} records it as a GAP");
            yield return new(row, AssuranceSide.Harness, EnforcementClass.Gap, $"the harness reaches {what} and beyond");
        }
    }

    /// <summary>The proof's GAP row for loopback on 443, and the attempt's (<see cref="CellBenchmark.RowNames"/>).</summary>
    public const string WebLoopbackGap = "gap-web-loopback-443";

    /// <summary>The proof's GAP row for the LAN on 443, and the attempt's.</summary>
    public const string WebLanGap = "gap-web-lan-443";
}

/// <summary>
/// WHAT A ROUTE (a) LAUNCH NEEDS FROM THE HOST (<c>U-cell-linux-c</c>): its record of proofs and assurance, the delegated cgroup root,
/// and the launcher. A session given none refuses every cell launch.
/// </summary>
public sealed class CellRoute(Database db, AttemptCgroups? cgroups = null, IReadOnlyList<string>? launcher = null)
{
    public CellProofStore Proofs { get; } = new(db);

    public CellAssuranceStore Assurance { get; } = new(db);

    public AttemptCgroups Cgroups => cgroups ?? AttemptCgroups.Current;

    public IReadOnlyList<string>? Launcher => launcher ?? ProcessContainment.DefaultLauncher();
}

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
/// <para>Here are the pure parts — the paths, the policy, the file — so the launch and the Linux rows build the same thing.</para>
/// </summary>
public static class RouteA
{
    /// <summary>The provider a <c>cell_proof</c> row names for this route: the pinned codex exec-server in a Linux cell.</summary>
    public const string Provider = "codex-exec-server-linux";

    /// <summary>The harness a <c>cell_assurance</c> row names for this route.</summary>
    public const string Harness = "codex-0.160.1-exec-server-stdio";

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
        return Path.GetFileName(dir) == "bin" && package is not null && package.StartsWith(tools + "/", StringComparison.Ordinal) ? package : dir;
    }

    /// <summary>
    /// THE CELL'S POLICY: the workspace read-write; the cell's codex home and home read-only (both empty, both the app's); codex's own
    /// root read and run; the system roots; an environment named in full — no <c>CODEX_HOME</c>, <c>HOME</c> or bearer of the
    /// harness's — and egress <c>none</c>.
    /// </summary>
    public static CellPolicy Policy(string workspace, string codexExe, string codexRoot, string cellCodexHome, string cellHome) =>
        new(workspace, [cellCodexHome, cellHome], [codexRoot, .. CellPolicy.SystemRoots],
        [
            new("PATH", Path.GetDirectoryName(codexExe) + ":/usr/local/bin:/usr/bin:/bin"),
            new("HOME", cellHome),
            new("CODEX_HOME", cellCodexHome),
            new("LANG", "C.UTF-8"),
            .. CellPolicy.ToolEnvironment(workspace)
        ], CellEgress.None);

    /// <summary>
    /// THE HARNESS'S <c>environments.toml</c>: one stdio environment, the default, no local one. Its program is TradeAgent's own
    /// launcher, which joins <paramref name="cellLeaf"/>, enters the cell <paramref name="policyFile"/> describes and becomes
    /// <c>codex exec-server --listen stdio</c>; its only variable is the app's home, where the launcher finds its cell folder. Every
    /// string is a TOML literal the bytes of which are refused if they cannot be one.
    /// </summary>
    public static string EnvironmentsToml(IReadOnlyList<string> launcher, string cellLeaf, string policyFile, string codexExe, string home)
    {
        if (launcher is not { Count: > 0 }) throw new ArgumentException("no launcher", nameof(launcher));
        string[] args =
        [
            .. launcher.Skip(1), ContainedLaunch.Flag, ContainedLaunch.CgroupFlag, cellLeaf, ContainedLaunch.CellFlag, policyFile,
            codexExe, "exec-server", "--listen", "stdio"
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
        File.CreateSymbolicLink(Path.Combine(layout.HarnessCodexHome, "auth.json"), seatAuth);
    }

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
    /// own (else the proof is revoked: the canary); and a policy whose egress class the proof's rows measured (L4). Anything else is a
    /// refusal in words — never a degraded launch, never a cell-class row without a proof.
    /// </summary>
    public static (CellProof? Proof, string? Refusal) Admission(CellRoute? route, string codexPin, CellPlatform platform, CellEgress egress)
    {
        const string none = "TradeAgent did not start the AI as a cell seat";
        if (route is null) return (null, $"{none}: this host was given no record of proven cells to launch one under.");
        if (platform.Refusal() is { } refusal) return (null, $"{none}: {refusal}");
        if (!CellBenchmark.ProvedEgress.Contains(egress.Name, StringComparer.Ordinal))
            return (null, $"{none}: its cell would have egress '{egress.Name}', and the benchmark's rows prove only " +
                          $"{string.Join(", ", CellBenchmark.ProvedEgress)}.");
        if (route.Proofs.ProofOf(Provider) is not { } proof)
            return (null, $"{none}: no cell proof counts on this machine, kernel, boot and build. TradeAgent measures its cells itself at " +
                          "every start and records a proof only when every row holds; until then no cell seat starts.");
        var now = CellBenchmark.Profile(Provider, platform, codexPin);
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
    public static IReadOnlyList<AssuranceRow> AssuranceRows(long proofId)
    {
        var proof = $"cell proof {proofId}";
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
            Cell("network", EnforcementClass.HardPlatform, "egress none: Landlock connect and bind, seccomp listen and datagrams"),
            Harness("network", EnforcementClass.Advisory, "the harness reaches its provider and anything else its account can"),
            Cell("descendants-cancellation", EnforcementClass.HardPlatform, "the attempt's cgroup: cgroup.kill ends every descendant"),
            Harness("descendants-cancellation", EnforcementClass.HardPlatform, "the attempt's cgroup, harness leaf"),
            Cell("broker-reach", EnforcementClass.HardPlatform, "egress none and no bearer in the cell"),
            Harness("broker-reach", EnforcementClass.HardApp, "the launch grant answers to the harness leaf alone"),
            Cell("order-permission", EnforcementClass.HardPlatform, "no pipe, no port and no grant in the cell"),
            Harness("order-permission", EnforcementClass.HardApp, "the grant's role and the gateway's order rules"),
            Cell("cmdline", EnforcementClass.Gap, "/proc/*/cmdline of other processes is readable from a cell"),
            Harness("cmdline", EnforcementClass.Gap, "/proc/*/cmdline is readable"),
            Cell("stat", EnforcementClass.Gap, "Landlock does not handle stat/getattr outside the grants"),
            Harness("stat", EnforcementClass.Gap, unconfined),
        ]);
    }
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

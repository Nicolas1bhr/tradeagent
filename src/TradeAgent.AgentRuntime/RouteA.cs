using System.Text;
using TradeAgent.Core;

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
}

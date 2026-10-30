using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE WINDOWS CELL PROVIDER'S PURE PARTS (<c>U-cell-windows</c> item 2), on every OS: the path rule Windows resolves by, the slot count,
/// the harness's environment program, the policy, the assurance rows and the admission's egress — each a guard with a red-first and a
/// mutant on record in the landing entry.
/// </summary>
public sealed class CellWindowsProviderTests
{
    static CellPlatform Windows11 => new(false, "Microsoft Windows 10.0.26200", "x64", 0, "not Linux", null, null, Windows: true, WindowsBuild: 26200);

    static CellPlatform LinuxStandIn => new(true, "6.17.0-1022-azure", "x64", 7, null, "lockdown,capability,landlock,yama,apparmor", "1");

    /// <summary>
    /// A GRANT IS JUDGED AS WINDOWS RESOLVES THE PATH: without case and with either separator — so the app's <c>state/</c> spelt in
    /// capitals, or with forward slashes, lies inside it — and still by whole components. Linux's rule is unchanged: case counts.
    /// </summary>
    [Fact]
    public void Within_on_windows_ignores_case_and_separator_and_keeps_whole_components()
    {
        const string state = @"C:\Users\Owner\AppData\Local\TradeAgent\state";
        Assert.True(CellPolicy.Within(@"C:\USERS\owner\appdata\local\tradeagent\STATE\tradeagent.db", state, windows: true));
        Assert.True(CellPolicy.Within("C:/Users/Owner/AppData/Local/TradeAgent/state/x", state, windows: true));
        Assert.True(CellPolicy.Within(state + @"\", state, windows: true));
        Assert.False(CellPolicy.Within(@"C:\Users\Owner\AppData\Local\TradeAgent\statefile", state, windows: true));
        Assert.True(CellPolicy.Within(@"D:\anything", "D:", windows: true));
        Assert.False(CellPolicy.Within("/home/o/TradeAgent/STATE/x", "/home/o/TradeAgent/state", windows: false));
        Assert.True(CellPolicy.Within("/home/o/TradeAgent/state/x", "/home/o/TradeAgent/state", windows: false));
    }

    /// <summary>
    /// ON WINDOWS A GRANT OF THE APP'S STATE IN ANOTHER SPELLING IS REFUSED: capitals, forward slashes — NTFS resolves both to state/.
    /// RED at base on Windows: Linux's rule (ordinal, "/") let <c>…\STATE\x</c> and even <c>…\state\x</c> through.
    /// </summary>
    [Fact]
    public void On_windows_the_apps_state_in_any_spelling_is_refused()
    {
        if (!OperatingSystem.IsWindows()) return;   // the rule under test is the one Windows resolves by; Within_on_windows covers it everywhere
        Directory.CreateDirectory(Paths.State);
        foreach (var spelling in new[] { Path.Combine(Paths.State, "x"), Path.Combine(Paths.State.ToUpperInvariant(), "x"), Paths.State.Replace('\\', '/') + "/x" })
        {
            Directory.CreateDirectory(spelling);
            var problem = new CellPolicy(Path.Combine(TestEnv.Home, "ws-ok"), [spelling], [], [new("PATH", "x")]).Problem();
            Assert.True(problem is not null && problem.Contains("inside TradeAgent's state", StringComparison.Ordinal), $"{spelling}: {problem ?? "granted"}");
        }
    }

    [Fact]
    public void One_slot_per_lane()
    {
        Assert.Equal(LanePolicy.MaxLanes, WindowsCellSlots.Count);
    }

    /// <summary>Windows 11 x64 runs a cell; an older build or another processor is refused in words; Linux's sentences are unchanged.</summary>
    [Fact]
    public void The_windows_platform_runs_a_cell_on_windows_11_x64_alone()
    {
        Assert.Null(Windows11.Refusal());
        Assert.Contains("Windows 11 (build 22000 or later)", (Windows11 with { WindowsBuild = 19045 }).Refusal(), StringComparison.Ordinal);
        Assert.Contains("built for x64", (Windows11 with { Architecture = "arm64" }).Refusal(), StringComparison.Ordinal);
        Assert.Null(LinuxStandIn.Refusal());
        Assert.Contains("Linux only", new CellPlatform(false, "Darwin", "arm64", 0, "not Linux", null, null).Refusal(), StringComparison.Ordinal);
        Assert.Equal(RouteA.WindowsProvider, RouteA.ProviderOn(Windows11));
        Assert.Equal(RouteA.Provider, RouteA.ProviderOn(LinuxStandIn));
        Assert.NotEqual(CellBenchmark.Profile(RouteA.WindowsProvider, Windows11, "pin"), CellBenchmark.Profile(RouteA.WindowsProvider, Windows11 with { WindowsBuild = 26100 }, "pin"));
    }

    /// <summary>On Windows the harness's environment program is the stage with no cgroup; on Linux it still joins the cell leaf.</summary>
    [Fact]
    public void The_windows_environment_program_names_the_cell_stage_and_no_cgroup()
    {
        var windows = RouteA.EnvironmentsToml(["trade.exe"], null, "p.cell.json", "codex.exe", "home");
        Assert.DoesNotContain(ContainedLaunch.CgroupFlag, windows, StringComparison.Ordinal);
        Assert.Contains($"\"{ContainedLaunch.Flag}\", \"{ContainedLaunch.CellFlag}\", \"p.cell.json\", \"codex.exe\", \"exec-server\"", windows, StringComparison.Ordinal);
        var linux = RouteA.EnvironmentsToml(["trade"], "/sys/fs/cgroup/x/cell", "p.cell.json", "codex", "home");
        Assert.Contains($"\"{ContainedLaunch.CgroupFlag}\", \"/sys/fs/cgroup/x/cell\"", linux, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE WINDOWS POLICY names everything the cell starts with and nothing of the owner's: no Linux system root, the cell's own home as
    /// USERPROFILE and HOME, LOCALAPPDATA inside the workspace, the egress it was given.
    /// </summary>
    [Fact]
    public void The_windows_policy_is_the_cells_alone()
    {
        var ws = Path.Combine(TestEnv.Home, "wp-ws");
        var p = RouteA.WindowsPolicy(ws, Path.Combine(TestEnv.Home, "codex", "bin", "codex.exe"), Path.Combine(TestEnv.Home, "codex"),
            Path.Combine(TestEnv.Home, "c-codex"), Path.Combine(TestEnv.Home, "c-home"), CellEgress.Web);
        Assert.DoesNotContain(p.Executable, e => CellPolicy.SystemRoots.Contains(e));
        var env = p.Environment.ToDictionary(e => e.Key, e => e.Value);
        Assert.Equal(Path.Combine(TestEnv.Home, "c-home"), env["USERPROFILE"]);
        Assert.Equal(Path.Combine(TestEnv.Home, "c-codex"), env["CODEX_HOME"]);
        Assert.Equal(Path.Combine(ws, ".local"), env["LOCALAPPDATA"]);
        Assert.Equal(CellEgress.Web.Name, p.Egress.Name);
        Assert.DoesNotContain(p.Environment, e => e.Value.Contains(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + Path.DirectorySeparatorChar + ".codex", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// WHAT NO WINDOWS ROW MEASURED IS NEVER HARD: cmdline and stat are GAP on the cell's side, and under web another LAN host on 443 is a
    /// GAP row; no sentence names Landlock or seccomp.
    /// </summary>
    [Fact]
    public void The_windows_assurance_claims_only_what_its_rows_measure()
    {
        var web = RouteA.AssuranceRows(3, CellEgress.Web, windows: true);
        Assert.All(web.Where(r => r.Property is "cmdline" or "stat"), r => Assert.Equal(EnforcementClass.Gap, r.Class));
        Assert.Contains(web, r => r.Property == RouteA.WindowsLanGap && r.Side == AssuranceSide.Cell && r.Class == EnforcementClass.Gap);
        Assert.DoesNotContain(web, r => r.Evidence.Contains("Landlock", StringComparison.Ordinal) || r.Evidence.Contains("seccomp", StringComparison.Ordinal));
        Assert.DoesNotContain(RouteA.AssuranceRows(3, CellEgress.None, windows: true), r => r.Property == RouteA.WindowsLanGap);
    }

    /// <summary>
    /// THE EGRESS DECISION (2026-10-30), WINDOWS ONLY: a web launch on a Windows host whose proof measured only none — its firewall rule
    /// declined or failed — runs under none and is admitted; the same proof shape on Linux still refuses the web launch.
    /// </summary>
    [Fact]
    public void A_windows_proof_of_none_alone_admits_a_web_launch_under_none_and_linux_still_refuses()
    {
        using var db = TestEnv.NewDb();
        var route = new CellRoute(db);
        new CellProofStore(db).Record(RouteA.WindowsProvider, CellBenchmark.Profile(RouteA.WindowsProvider, Windows11, "pin"), [CellEgress.None.Name]);
        var (proof, egress, refusal) = RouteA.AdmissionOn(route, "pin", Windows11, CellEgress.Web);
        Assert.Null(refusal);
        Assert.NotNull(proof);
        Assert.Equal(CellEgress.None.Name, egress.Name);

        using var linuxDb = TestEnv.NewDb();
        new CellProofStore(linuxDb).Record(RouteA.Provider, CellBenchmark.Profile(RouteA.Provider, LinuxStandIn, "pin"), [CellEgress.None.Name]);
        var (_, linuxEgress, linuxRefusal) = RouteA.AdmissionOn(new CellRoute(linuxDb), "pin", LinuxStandIn, CellEgress.Web);
        Assert.Contains("measured only none", linuxRefusal, StringComparison.Ordinal);
        Assert.Equal(CellEgress.Web.Name, linuxEgress.Name);
    }
}

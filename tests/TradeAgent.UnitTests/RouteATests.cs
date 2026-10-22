using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ROUTE (a)'S PURE PARTS (<c>U-cell-linux-c</c> item 4): the program root a cell runs codex from, the cell's policy, and the harness's
/// <c>environments.toml</c> — what the launch and the Linux rows both build from.
/// </summary>
public class RouteATests
{
    [UnixFact]
    public void Codex_runs_from_its_installed_package_or_its_own_folder_and_never_a_wider_parent()
    {
        Assert.Equal(Path.Combine(Paths.Tools, "codex"), RouteA.CodexRoot(Path.Combine(Paths.Tools, "codex", "bin", "codex")));
        Assert.Equal("/home/someone/.local/bin", RouteA.CodexRoot("/home/someone/.local/bin/codex"));
        Assert.Equal("/usr/bin", RouteA.CodexRoot("/usr/bin/codex"));
    }

    [UnixFact]
    public void The_cells_policy_is_the_workspace_two_empty_read_only_homes_codex_and_no_egress()
    {
        var root = Path.Combine(TestEnv.Home, "route-a-" + Guid.NewGuid().ToString("n")[..8]);
        var ws = Path.Combine(root, "ws");
        var package = Path.Combine(Paths.Tools, "codex-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(ws);
        Directory.CreateDirectory(Path.Combine(package, "bin"));
        var layout = RouteA.LayoutFor("attempt-x-" + Guid.NewGuid().ToString("n")[..8]);
        var exe = Path.Combine(package, "bin", "codex");
        var policy = RouteA.Policy(ws, exe, RouteA.CodexRoot(exe), layout.CellCodexHome, layout.CellHome);
        Assert.Null(policy.Problem());
        Assert.Equal(CellEgress.None.Name, policy.Egress.Name);
        Assert.Equal(ws, policy.Workspace);
        Assert.Equal([layout.CellCodexHome, layout.CellHome], policy.ReadOnly);
        Assert.Equal(package, policy.Executable[0]);
        var env = policy.Environment.ToDictionary(e => e.Key, e => e.Value);
        Assert.Equal(layout.CellCodexHome, env["CODEX_HOME"]);
        Assert.Equal(layout.CellHome, env["HOME"]);
        Assert.StartsWith(Path.Combine(package, "bin") + ":", env["PATH"], StringComparison.Ordinal);
        Assert.False(env.ContainsKey(TradeAgent.Security.AgentGrants.Variable));
        Assert.StartsWith(Paths.SeatHome, layout.HarnessCodexHome, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => RouteA.LayoutFor("../escape"));
    }

    [Fact]
    public void Environments_toml_names_one_default_stdio_environment_run_through_the_launcher()
    {
        var toml = RouteA.EnvironmentsToml(["/opt/ta/dotnet", "/opt/ta/trade.dll"], "/sys/fs/cgroup/svc/attempt-a-1/cell",
            "/h/cells/p.cell.json", "/h/tools/codex/bin/codex", "/h");
        Assert.Equal(
            "default = \"cell\"\ninclude_local = false\n\n[[environments]]\nid = \"cell\"\nprogram = \"/opt/ta/dotnet\"\n" +
            "args = [\"/opt/ta/trade.dll\", \"--spawn-contained\", \"--cgroup\", \"/sys/fs/cgroup/svc/attempt-a-1/cell\", \"--cell\", " +
            "\"/h/cells/p.cell.json\", \"/h/tools/codex/bin/codex\", \"exec-server\", \"--listen\", \"stdio\"]\n" +
            "env = { TRADEAGENT_HOME = \"/h\" }\n", toml);
        Assert.Contains("\\\"q", RouteA.EnvironmentsToml(["/a\"q"], "/c", "/p", "/x", "/h"));
        Assert.Throws<TradeAgentException>(() => RouteA.EnvironmentsToml(["/a\nb"], "/c", "/p", "/x", "/h"));
    }
}

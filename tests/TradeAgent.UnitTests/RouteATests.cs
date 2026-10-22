using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
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

    static readonly CellPlatform Linux = new(true, "6.17.0-1010-azure", "x64", 7, null, "lockdown,capability,landlock,yama,apparmor", "1");

    static RuntimeManifest Codex => RuntimeCatalog.BuiltIn(HostPlatform.Current).First(m => m.Id == "codex");

    /// <summary>
    /// ROW (j): A CELL SEAT WITHOUT A COUNTED PROOF IS REFUSED — and so is one on a platform no cell runs on, one whose cell would have an
    /// egress the proof's rows did not measure (L4), and one whose profile no longer recomputes to the proof's, which also REVOKES the
    /// proof (the canary). With a counted proof whose profile holds, it is admitted under that proof.
    /// </summary>
    [Fact]
    public void A_cell_seat_without_a_counted_proof_is_refused()
    {
        using var db = TestEnv.NewDb();
        var route = new CellRoute(db);
        var pin = Codex.Install.ExecutableSha256 ?? "";
        var (none, refusal) = RouteA.Admission(route, pin, Linux, CellEgress.None);
        Assert.Null(none);
        Assert.Contains("no cell proof counts", refusal);
        Assert.Contains("this host was given no record", RouteA.Admission(null, pin, Linux, CellEgress.None).Refusal);
        Assert.Contains("Linux", RouteA.Admission(route, pin, Linux with { LandlockAbi = 5 }, CellEgress.None).Refusal);

        var id = route.Proofs.Record(RouteA.Provider, CellBenchmark.Profile(RouteA.Provider, Linux, pin));
        var (proof, ok) = RouteA.Admission(route, pin, Linux, CellEgress.None);
        Assert.Null(ok);
        Assert.Equal(id, proof!.Id);
        Assert.Contains("prove only none", RouteA.Admission(route, pin, Linux, CellEgress.Web).Refusal);

        var changed = RouteA.Admission(route, pin, Linux with { Lsm = "lockdown,capability,yama,apparmor,landlock" }, CellEgress.None);
        Assert.Null(changed.Proof);
        Assert.Contains("revoked", changed.Refusal);
        Assert.Empty(route.Proofs.Proven());
        Assert.Contains("no cell proof counts", RouteA.Admission(route, pin, Linux, CellEgress.None).Refusal);
    }

    /// <summary>
    /// L1 AND L2: THE CELL CLASS CARRIES THE DOOR EXACTLY AS THE BROKER DOES and keeps its shell, patch and image tools — which run in
    /// the cell — on its pinned classic-tools model; its offered surface is held to the measured set, and code mode's <c>exec</c>, or a
    /// sub-agent, refuses before admission. Its security fields are this build's own.
    /// </summary>
    [Fact]
    public void The_cell_class_carries_the_door_its_cell_tools_and_nothing_that_runs_beside_the_login()
    {
        var seat = Codex.Seat!;
        var cell = seat.ArgsFor(SeatPlan.CellClass);
        Assert.DoesNotContain("mcp_servers={}", cell);
        foreach (var door in seat.ArgsFor(SeatPlan.BrokerClass).Where(a => a.StartsWith($"mcp_servers.{BrokerSeat.DoorServer}.", StringComparison.Ordinal)))
            Assert.Contains(door, cell);
        // codex exec reads environments.toml only WITHOUT --ignore-user-config (vendor exec/src/lib.rs:698-711; CI run 39700964111).
        Assert.DoesNotContain("--ignore-user-config", cell);
        Assert.Contains("--strict-config", cell);
        Assert.Contains("--ignore-rules", cell);
        Assert.Contains("agents.enabled=false", cell);
        Assert.DoesNotContain("shell_tool", cell);
        Assert.DoesNotContain("view_image", cell);
        Assert.Equal([BrokerSeat.DoorServer], seat.ServersFor(SeatPlan.CellClass));
        Assert.True(seat.NeedsDoor(SeatPlan.CellClass));
        Assert.Equal(BrokerSeat.Model, BrokerSeat.ModelOf(SeatPlan.CellClass));
        Assert.Null(BrokerSeat.OverrideRefusal(Codex, SeatPlan.CellClass));

        string[] measured = ["exec_command", "write_stdin", "list_mcp_resources", "list_mcp_resource_templates", "read_mcp_resource",
                             "request_user_input", "apply_patch", "view_image", "tool_search"];
        Assert.Null(BrokerSeat.SurfaceRefusal(BrokerSeat.Model, measured, "Codex", SeatPlan.CellClass));
        Assert.Contains("exec", BrokerSeat.SurfaceRefusal(BrokerSeat.Model, [.. measured, "exec"], "Codex", SeatPlan.CellClass));
        Assert.Contains("spawn_agent", BrokerSeat.SurfaceRefusal(BrokerSeat.Model, [.. measured, "spawn_agent"], "Codex", SeatPlan.CellClass));
        Assert.NotNull(BrokerSeat.SurfaceRefusal(BrokerSeat.Model, measured, "Codex", SeatPlan.BrokerClass));
        Assert.NotNull(BrokerSeat.SurfaceRefusal(BrokerSeat.Model, measured.Except(["apply_patch"]).ToList(), "Codex", SeatPlan.CellClass));
    }

    /// <summary>A cell seat is still refused while real money is armed: its harness, which holds the sign-in, is unconfined.</summary>
    [Fact]
    public void A_cell_seat_is_still_refused_while_live_is_armed()
    {
        var sandbox = Containment.Sandbox(SeatClasses.Cell);
        Assert.False(sandbox.Ok);
        Assert.Contains("harness", sandbox.Detail);
        Assert.NotNull(Containment.RefusalToLaunch(modeIsLive: true, liveActivated: true, sandbox));
        Assert.Null(Containment.RefusalToLaunch(modeIsLive: false, liveActivated: false, sandbox));
    }

    /// <summary>Route (a)'s assurance: every property the cell holds hard is MEDIATED or weaker for the attempt — presence and lanes stay put.</summary>
    [Fact]
    public void Route_a_attempts_state_and_inbox_are_mediated_and_its_gaps_are_asserted()
    {
        var rows = RouteA.AssuranceRows(7);
        EnforcementClass Attempt(string p) => rows.Single(r => r.Property == p && r.Side == AssuranceSide.Attempt).Class;
        Assert.Equal(EnforcementClass.Mediated, Attempt("state"));
        Assert.Equal(EnforcementClass.Mediated, Attempt("inbox"));
        Assert.Equal(EnforcementClass.HardPlatform, Attempt("descendants-cancellation"));
        Assert.Equal(EnforcementClass.Gap, Attempt("cmdline"));
        Assert.Equal(EnforcementClass.Gap, Attempt("stat"));
        Assert.All(rows.Where(r => r.Side == AssuranceSide.Cell && r.Class != EnforcementClass.Gap), r => Assert.Contains("cell proof 7", r.Evidence));
    }
}

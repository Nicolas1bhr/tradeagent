using System.Reflection;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Security;
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
    public void The_cells_policy_is_the_workspace_two_empty_read_only_homes_codex_and_web_egress()
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
        // WEB, the owner's answer for every executor (U-execution-environment item 3; -c wrote none here).
        Assert.Equal(CellEgress.Web.Name, policy.Egress.Name);
        Assert.Equal(ws, policy.Workspace);
        Assert.Equal([layout.CellCodexHome, layout.CellHome], policy.ReadOnly.Take(2));
        Assert.Equal(CellEgress.Web.ReadOnly, policy.ReadOnly.Skip(2));   // web's resolver configuration, read-only
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

        // A PROOF WHOSE ROWS MEASURED `none` ALONE ADMITS `none` and refuses `web` (L4; fix pass 1, R4): the class is read off the proof.
        using (var noneDb = TestEnv.NewDb())
        {
            var noneRoute = new CellRoute(noneDb);
            var noneOnly = noneRoute.Proofs.Record(RouteA.Provider, CellBenchmark.Profile(RouteA.Provider, Linux, pin), [CellEgress.None.Name]);
            Assert.Equal(noneOnly, RouteA.Admission(noneRoute, pin, Linux, CellEgress.None).Proof!.Id);
            Assert.Contains("measured only none.", RouteA.Admission(noneRoute, pin, Linux, CellEgress.Web).Refusal);
        }

        // THE BENCHMARK'S OWN CLASSES, none AND web: both admitted under it, and web-plain still refused.
        Assert.Equal([CellEgress.None.Name, CellEgress.Web.Name], CellBenchmark.ProvedEgress);
        var id = route.Proofs.Record(RouteA.Provider, CellBenchmark.Profile(RouteA.Provider, Linux, pin), CellBenchmark.ProvedEgress);
        var (proof, ok) = RouteA.Admission(route, pin, Linux, CellEgress.None);
        Assert.Null(ok);
        Assert.Equal(id, proof!.Id);
        Assert.Equal(id, RouteA.Admission(route, pin, Linux, CellEgress.Web).Proof!.Id);
        Assert.Contains("measured only none, web", RouteA.Admission(route, pin, Linux, CellEgress.WebPlain).Refusal);

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
                             "request_user_input", "request_user_input_async", "clock.curr_time", "apply_patch", "view_image", "tool_search"];
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
        var rows = RouteA.AssuranceRows(7, CellEgress.None);
        EnforcementClass Attempt(string p) => rows.Single(r => r.Property == p && r.Side == AssuranceSide.Attempt).Class;
        Assert.Equal(EnforcementClass.Mediated, Attempt("state"));
        Assert.Equal(EnforcementClass.Mediated, Attempt("inbox"));
        Assert.Equal(EnforcementClass.HardPlatform, Attempt("descendants-cancellation"));
        Assert.Equal(EnforcementClass.Gap, Attempt("cmdline"));
        Assert.Equal(EnforcementClass.Gap, Attempt("stat"));
        Assert.All(rows.Where(r => r.Side == AssuranceSide.Cell && r.Class != EnforcementClass.Gap), r => Assert.Contains("cell proof 7", r.Evidence));
    }
    /// <summary>
    /// R2 (fix pass 1): A CELL LAUNCH WHOSE ASSURANCE COULD NOT BE WRITTEN AND WHOSE CGROUP WOULD NOT DIE IS NEVER REPORTED ENDED. The
    /// session starts a <c>cell</c> seat for real — admitted under a counted proof, its attempt's cgroup and leaves made, its harness
    /// started in the harness leaf, where it leaves a setsid'd daemon — and then the assurance INSERT fails (a trigger) at the moment
    /// the attempt's <c>cgroup.kill</c> stops being writable. The teardown itself stands in through the session's own seam
    /// (<see cref="AgentSession.EndTree"/>) as one that could not end it: on CI the fixed teardown's per-process pass ends the daemon
    /// even with the attempt's kill and listing denied (runs 39715903760, 39718123756), so a failed teardown is not something a test can make. The turn ends carrying what still runs, and its presence stays held, so
    /// the meter admits no further turn while any of it runs. Linux only (CI's Cell step, under a delegated root); elsewhere the same
    /// session is refused before anything starts, which this row shows too.
    /// </summary>
    [Fact, Trait("Category", "Cell")]
    public async Task A_cell_launch_whose_assurance_insert_and_cgroup_kill_both_fail_reports_its_survivors_and_keeps_its_presence()
    {
        if (OperatingSystem.IsWindows()) return;   // no cell and no shell script there; the Cell step is Linux
        using var db = TestEnv.NewDb();
        var dir = Path.Combine(TestEnv.Home, "r2-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        var daemonFile = Path.Combine(dir, "daemon.pid");
        var exe = Path.Combine(dir, "codex");
        File.WriteAllText(exe, "#!/bin/sh\necho 'codex-cli 0.160.1'\n[ \"$1\" = \"exec\" ] || exit 0\n" +
                               $"setsid sleep 300 < /dev/null > /dev/null 2>&1 &\necho $! > '{daemonFile}'\nexec sleep 300\n");
        File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var codex = RuntimeCatalog.BuiltIn().Single(m => m.Id == "codex");
        codex.Executable = exe;
        codex.Install.ExecutableSha256 = SeatProfile.Sha256Of(exe);
        BrokerSeat.PinForTest(codex.Install.ExecutableSha256);
        codex.Seat!.Home = Path.Combine(dir, "seat-home");
        typeof(RuntimeManifest).GetProperty("DeclaredSeatClass", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(codex, SeatPlan.CellClass);

        string? root = null;
        var cgroups = AttemptCgroups.Unavailable("not Linux");
        if (OperatingSystem.IsLinux())
        {
            var mine = AttemptCgroups.OwnCgroup();
            Assert.True(mine is not null && Path.GetFileName(mine) == "runner",
                $"missing prerequisite: the test run in <service>/runner (CI's Cell step moves it there under Delegate=yes); this process is in {mine}");
            root = Path.Combine(Path.GetDirectoryName(mine!)!, "rows-" + Guid.NewGuid().ToString("n")[..12]);
            cgroups = AttemptCgroups.Open(root);
            Assert.True(cgroups.Held, $"missing prerequisite: a delegated root at {root}: {cgroups.Refusal}");
        }
        new CellProofStore(db).Record(RouteA.Provider, CellBenchmark.Profile(RouteA.Provider, CellPlatform.Read(), codex.Install.ExecutableSha256),
            CellBenchmark.ProvedEgress);

        // THE TWO FAILURES, AT ONE MOMENT: the attempt's cgroup.kill made unwritable, then the assurance INSERT aborted.
        db.Connection.CreateFunction("r2_deny_kill", () =>
        {
            // THE HARNESS HAS LEFT ITS DAEMON before the failure lands: the launcher starts it a moment after StartHarness returns.
            for (var i = 0; i < 300 && OperatingSystem.IsLinux() && !File.Exists(daemonFile); i++) Thread.Sleep(100);
            if (OperatingSystem.IsLinux() && root is not null)
                foreach (var attempt in Directory.EnumerateDirectories(root, AttemptCgroups.AttemptPrefix + "*"))
                    File.SetUnixFileMode(Path.Combine(attempt, "cgroup.kill"), UnixFileMode.None);
            return 0;
        });
        db.Write(_ =>
        {
            using var c = db.Cmd("CREATE TEMP TRIGGER r2_fail BEFORE INSERT ON cell_assurance " +
                                 "BEGIN SELECT r2_deny_kill(); SELECT RAISE(ABORT, 'the assurance insert failed (injected)'); END");
            return c.ExecuteNonQuery();
        });

        var presence = new AgentPresence();
        IReadOnlyList<string> launcher = [Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : Environment.ProcessPath!, TradeCliDll()];
        string[] measured = ["exec_command", "write_stdin", "list_mcp_resources", "list_mcp_resource_templates", "read_mcp_resource",
                             "request_user_input", "request_user_input_async", "clock.curr_time", "apply_patch", "view_image", "tool_search"];
        var session = new AgentSession(codex, () => exe, () => dir, () => new Dictionary<string, string>(), presence: presence,
            attempt: () => "r2", grants: new AgentGrants(), seatProbe: BrokerSeatTests.BrokerProbe(), door: () => new Uri("http://127.0.0.1:1/mcp"),
            surfaceProbe: new BrokerSeatTests.FakeSurface(measured), cells: new CellRoute(db, cgroups, launcher))
        {
            // A TEARDOWN THAT COULD NOT END THE LAUNCH: nothing ended, and what its cgroup still holds, as the kernel lists it.
            EndTree = contained => new TreeEnd(false, contained.Cgroup?.Left() ?? [], 0)
        };

        AgentTurnEnded? ended = null;
        try
        {
            ended = await SeatHomeTests.Turn(session);
            if (!OperatingSystem.IsLinux())
            {
                Assert.True(ended.Raw.Contains("did not start the AI as a cell seat", StringComparison.Ordinal), ended.Raw);
                // admitted by everything before the cell, refused at it: off Linux and Windows by the platform; on Windows by the proof,
                // which is the Linux provider's here and so counts for no Windows cell (U-cell-windows).
                Assert.Contains(OperatingSystem.IsWindows() ? "no cell proof counts" : "Linux only", ended.Raw, StringComparison.Ordinal);
                Assert.Null(ended.Survivors);
                Assert.Equal(0, presence.Live);
                return;
            }
            Assert.Contains("injected", ended.Raw, StringComparison.Ordinal);
            Assert.True(File.Exists(daemonFile), $"the harness never ran, so this row tests nothing: {ended.Raw}");
            var daemon = int.Parse(File.ReadAllText(daemonFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(ProcessTable.Read(daemon) is { Zombie: false }, $"the daemon {daemon} did not outlive the failed launch, so this row tests nothing");
            Assert.True(ended.Survivors is { Count: > 0 },
                $"a cell launch whose cgroup could not be killed was reported ended while its daemon {daemon} ran: {ended.Raw}");
            Assert.Contains(ended.Survivors!, p => p.Pid == daemon);
            Assert.NotNull(ended.SurvivorPresence);
            Assert.True(presence.Live > 0, "the launch's presence was closed while its daemon still ran");
        }
        finally
        {
            ended?.SurvivorPresence?.Dispose();
            if (root is not null && Directory.Exists(root))
            {
                foreach (var attempt in Directory.EnumerateDirectories(root, AttemptCgroups.AttemptPrefix + "*"))
                    try { File.SetUnixFileMode(Path.Combine(attempt, "cgroup.kill"), UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch (IOException) { }
                try { File.WriteAllText(Path.Combine(root, "cgroup.kill"), "1"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                for (var i = 0; i < 300 && !File.ReadAllText(Path.Combine(root, "cgroup.events")).Contains("populated 0", StringComparison.Ordinal); i++)
                    await Task.Delay(10);
                foreach (var sub in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).ToList())
                    try { Directory.Delete(sub); } catch (IOException) { }
                try { Directory.Delete(root); } catch (IOException) { }
            }
        }
    }

    /// <summary>The built trade CLI, the launcher's second word (the first is the dotnet host).</summary>
    static string TradeCliDll()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "TradeAgent.sln"))) d = d.Parent;
        Assert.NotNull(d);
        var hit = Directory.GetFiles(Path.Combine(d!.FullName, "src", "TradeAgent.TradeCli", "bin"), "trade.dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        Assert.NotNull(hit);
        return hit!;
    }
}

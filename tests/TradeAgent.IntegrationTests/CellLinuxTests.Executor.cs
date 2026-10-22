using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THIS ASSEMBLY IS A TEST HOST FOR SEATS (<c>U-execution-environment</c> item 0): the flag that honours a test manifest's declared class
/// and its mock provider (<c>RuntimeManifest.DeclaredSeatClass</c>, <c>TestProviderArgs</c>) and a puppet. Internal to the runtime, visible
/// to the test assemblies alone; nothing in the product sets it.
/// </summary>
static class SeatTestHost
{
    [ModuleInitializer]
    internal static void Init() => PuppetSeats.TestHost = true;
}

/// <summary>
/// AN EXECUTOR POSITION RUNS IN A CELL, END TO END (<c>U-execution-environment</c> items 0 and 5; <c>docs/ORCHESTRATION.md</c> § 2.8):
/// real <see cref="AgentSession"/> turns over CI's pinned linux-musl codex, installed where the app installs it, against the mock model
/// (<c>CellRoute/mock_responses.py</c>) — a FAKE key, no login, no model — through <c>StartInCell</c>: the harness outside in the
/// attempt's harness leaf with the app's door as its one tool server, every model-directed shell action in a route (a) cell whose
/// workspace is the position's own home, egress <c>web</c>, under a counted proof. The rows print what each tool call returned.
/// </summary>
public partial class CellLinuxTests
{
    const string Executor = "pos-e7ec0e01";

    /// <summary>
    /// THE PINNED CODEX WHERE THE APP INSTALLS IT — <c>tools/codex/bin/codex</c> beside its <c>codex-resources/</c> — copied once from CI's
    /// unpacked package, so <see cref="RouteA.CodexRoot"/> grants the cell the package exactly as it would in the product.
    /// </summary>
    static string InstalledCodex()
    {
        lock (InstallGate)
        {
            var target = Path.Combine(Paths.Tools, "codex");
            var exe = Path.Combine(target, "bin", "codex");
            if (File.Exists(exe)) return exe;
            var package = PackageOf(PinnedCodex());
            Directory.CreateDirectory(Paths.Tools);
            using var cp = Process.Start(new ProcessStartInfo("/bin/cp", ["-a", package, target]) { UseShellExecute = false })!;
            cp.WaitForExit();
            Require(cp.ExitCode == 0 && File.Exists(exe), $"the pinned codex copied into {target}");
            return exe;
        }
    }

    static readonly Lock InstallGate = new();

    /// <summary>The mock model for one turn: its port, the case it answers and the requests it logged.</summary>
    sealed class MockModel : IDisposable
    {
        readonly Process _process;
        public string Port { get; }
        public string Requests { get; }

        MockModel(Process process, string port, string requests) => (_process, Port, Requests) = (process, port, requests);

        public static async Task<MockModel> StartAsync(string root, object[] steps)
        {
            var dir = Path.Combine(root, "mock-" + Guid.NewGuid().ToString("n")[..8]);
            Directory.CreateDirectory(dir);
            var caseFile = Path.Combine(dir, "case.json");
            File.WriteAllText(caseFile, JsonSerializer.Serialize(new { steps, final = "EXECUTOR-DONE" }));
            var portFile = Path.Combine(dir, "port");
            var requests = Path.Combine(dir, "req");
            var p = Process.Start(new ProcessStartInfo("/usr/bin/python3",
                ["-I", Path.Combine(TradeAgent.Tests.DayOnePrograms.RepoRoot(), "tests", "TradeAgent.IntegrationTests", "CellRoute", "mock_responses.py"),
                 portFile, caseFile, requests]) { UseShellExecute = false })!;
            for (var i = 0; i < 100 && !File.Exists(portFile); i++) await Task.Delay(50);
            Require(File.Exists(portFile), "the mock model's port");
            return new MockModel(p, File.ReadAllText(portFile).Trim(), requests);
        }

        /// <summary>The <c>-c</c> keys that point codex at this mock: the test host's provider seam.</summary>
        public string[] ProviderArgs =>
        [
            "-c", "model_provider=\"mock\"", "-c", "model_providers.mock.name=\"mock\"",
            "-c", $"model_providers.mock.base_url=\"http://127.0.0.1:{Port}/v1\"", "-c", "model_providers.mock.wire_api=\"responses\""
        ];

        /// <summary>What every tool call returned to the model, by call id, read off the last request.</summary>
        public Dictionary<string, string> Outputs()
        {
            var outputs = new Dictionary<string, string>();
            if (!Directory.Exists(Requests)) return outputs;
            var last = Directory.EnumerateFiles(Requests, "*.json").Order(StringComparer.Ordinal).LastOrDefault();
            if (last is null) return outputs;
            using var doc = JsonDocument.Parse(File.ReadAllText(last));
            if (doc.RootElement.GetProperty("body").TryGetProperty("input", out var input))
                foreach (var item in input.EnumerateArray())
                    if (item.TryGetProperty("type", out var t) && t.GetString()!.EndsWith("_output", StringComparison.Ordinal) && item.TryGetProperty("call_id", out var id))
                        outputs[id.GetString()!] = item.GetProperty("output").ToString();
            return outputs;
        }

        public void Dispose()
        {
            try { _process.Kill(); _process.WaitForExit(5000); } catch (Exception) { /* gone */ }
            _process.Dispose();
        }
    }

    /// <summary>The CLI's own reports under the cell class's keys, canned: its shell tools off, the door listed (as -c's R2 row reads them).</summary>
    sealed class CellProbe : ISeatProbe
    {
        public Task<SeatReading> ReadAsync(RuntimeManifest manifest, string exe, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(new SeatReading(
                "unified_exec   stable   true\nshell_snapshot   stable   true\nshell_tool   stable   false\nview_image   stable   false\n" +
                "sleep_tool   stable   false\nplugins   stable   false\n", $"[{{\"name\": \"{BrokerSeat.DoorServer}\"}}]"));
    }

    /// <summary>The cell class's offered surface for gpt-5.5, as -c's verifier expects it.</summary>
    sealed class CellSurface : ISurfaceProbe
    {
        public Task<IReadOnlyList<string>> OfferedAsync(SurfaceAsk ask, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(
            ["exec_command", "write_stdin", "list_mcp_resources", "list_mcp_resource_templates", "read_mcp_resource", "request_user_input",
             "apply_patch", "view_image", "tool_search"]);
    }

    /// <summary>
    /// ONE EXECUTOR'S WORLD: the chart (a Member under Research's division), a counted route (a) proof measuring none and web on this
    /// host, the app's door on loopback answering the pinned codex, the position's home as a start builds it, the seat home's FAKE
    /// sign-in, and one session over the installed codex — the register, the gate, the meter where asked, the cell route — as the app
    /// composes them.
    /// </summary>
    sealed class ExecutorWorld : IAsyncDisposable
    {
        public required Database Db { get; init; }
        public required RowsRoot Rows { get; init; }
        public required BrokerDoor Door { get; init; }
        public required RuntimeManifest Codex { get; init; }
        public required string Exe { get; init; }
        public required string Home { get; init; }
        public required string Root { get; init; }
        public required AgentSession Session { get; init; }
        public required AgentPresence Presence { get; init; }
        public required SeatRegister Register { get; init; }
        public int ToolsServed;
        public string? AttemptId;

        public async ValueTask DisposeAsync()
        {
            await Door.DisposeAsync();
            Rows.Dispose();
            Db.Dispose();
        }
    }

    /// <summary>The proof a host benchmark writes when every row holds, recorded as the product records it (rows: CellBenchmark's).</summary>
    static void Proven(Database db, IReadOnlyList<string>? egress = null) =>
        new CellProofStore(db).Record(RouteA.Provider, CellBenchmark.Profile(RouteA.Provider, CellPlatform.Read(), BenchPin),
            egress ?? CellBenchmark.ProvedEgress);

    static async Task<ExecutorWorld> Executor_(string name, string role = Executor, string? declaredClass = null, bool meter = false)
    {
        PlatformIsACells();
        var exe = InstalledCodex();
        var rows = Rows();
        Deployed(true);
        var db = TestEnv.NewDb();
        Proven(db);
        if (!CouncilRoles.IsKnown(role))
            db.Write(_ =>
            {
                using var c = db.Cmd("""
                    INSERT INTO org_position(id, unit_id, home_dir, status, created_at)
                    VALUES($id, $unit, $home, 'active', '2026-10-22T12:00:00.0000000Z')
                    """, ("$id", role), ("$unit", OrgIds.Research), ("$home", $"org/{role}"));
                return c.ExecuteNonQuery();
            });

        var root = Path.Combine(TestEnv.Home, "executor-" + name + "-" + Guid.NewGuid().ToString("n")[..6]);
        Directory.CreateDirectory(root);
        var codex = RuntimeCatalog.BuiltIn(HostPlatform.Current).First(m => m.Id == "codex");
        codex.Executable = exe;
        codex.Seat!.Home = Path.Combine(root, "seat-home");
        Directory.CreateDirectory(Path.Combine(codex.Seat.Root, "codex"));
        File.WriteAllText(Path.Combine(codex.Seat.Root, "codex", "auth.json"), "{\"auth_mode\":\"apikey\",\"OPENAI_API_KEY\":\"sk-FAKE-executor-0000\"}");
        if (declaredClass is not null)
            typeof(RuntimeManifest).GetProperty("DeclaredSeatClass", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(codex, declaredClass);

        // THE POSITION'S HOME, AS A START BUILDS IT: under the app's workspace, the cell's canon written for an executor.
        var kind = new OrgStore(db).Standing(role)?.Kind;
        var home = WorkspaceBuilder.Build(new WorkspaceContext("Simulator (built in)", true, null, TradingMode.PAPER, true, null,
            new RiskPolicy(), ConnectorIsBuiltInSimulator: true, Role: role, Kind: kind));

        var register = new SeatRegister
        {
            Standing = id => new OrgStore(db).Standing(id),
            // THE LEGACY PAIR'S FACTS ARE NOT READ HERE: the gate asks the register's neighbour read for them (a declared cell seat of
            // the pair passes it; row (0)).
            Facts = id => CouncilRoles.IsKnown(id) ? null : new SeatFacts
            {
                Standing = new OrgStore(db).Standing(id),
                DeclaredClass = codex.SessionSeatClassFor(id, new OrgStore(db).Standing(id)?.Kind),
                ProvenCell = new CellProofStore(db).HoldsProof(),
                ActiveBeyondLegacy = new OrgStore(db).ActivePositionsBeyondLegacy()
            },
            Neighbours = () => new OrgStore(db).ActivePositionsBeyondLegacy()
        };

        var grants = new AgentGrants();
        ExecutorWorld? world = null;
        var door = new BrokerDoor
        {
            Grants = grants,
            Image = () => new PeerRule(exe, BenchPin, ToolsDir: null, WorkspaceDir: Paths.Workspace, BrokerDoor.RuntimeWord),
            Tools = grant =>
            {
                if (world is not null) Interlocked.Increment(ref world.ToolsServed);
                return BrokerDoor.ToolsFor(grant, _ => home, () => null, null);
            }
        };
        door.Start();
        var presence = new AgentPresence();
        TurnMeter? metering = null;
        var session = new AgentSession(codex, () => exe, () => home, () => WorkspaceBuilder.EnvironmentFor("executor-rows", home),
            presence: presence, role: role, attempt: () => metering?.OpenAttemptIdFor(role) ?? world?.AttemptId ?? $"exec-{name}",
            grants: grants, seatProbe: new CellProbe(), seats: register, door: () => door.Url, surfaceProbe: new CellSurface(),
            cells: new TradeAgent.AgentRuntime.CellRoute(db, rows.Cgroups, Launcher()));
        world = new ExecutorWorld
        {
            Db = db, Rows = rows, Door = door, Codex = codex, Exe = exe, Home = home, Root = root, Session = session, Presence = presence,
            Register = register
        };
        if (meter)
        {
            metering = new TurnMeter(db, () => 5m, live: new LiveAttempts(),
                declaredSeat: id => codex.SessionSeatClassFor(id, new OrgStore(db).Standing(id)?.Kind));
            metering.Attach(session, role);
        }
        return world;
    }

    /// <summary>One turn of <paramref name="w"/>'s session against a mock answering <paramref name="steps"/>: what the turn said, what each call returned.</summary>
    async Task<(AgentTurnEnded Ended, Dictionary<string, string> Outputs)> ExecutorTurn(ExecutorWorld w, object[] steps, bool metered = false)
    {
        using var mock = await MockModel.StartAsync(w.Root, steps);
        w.Codex.TestProviderArgs = mock.ProviderArgs;
        AgentTurnEnded? ended = null;
        void On(AgentTurnEnded e) => ended = e;
        w.Session.TurnEnded += On;
        try
        {
            var turn = metered ? w.Session.SendAsync("Continue your mission.") : w.Session.SendMissionAsync("Continue your mission.");
            await turn.WaitAsync(TimeSpan.FromSeconds(180));
        }
        finally { w.Session.TurnEnded -= On; }
        var outputs = mock.Outputs();
        var requests = Directory.Exists(mock.Requests) ? Directory.GetFiles(mock.Requests, "*.json").Length : 0;
        var history = string.Join("\n", w.Session.History.TakeLast(6).Select(t => $"[{t.Role}] {t.Text[^Math.Min(t.Text.Length, 1500)..]}"));
        var said = $"turn: exit {ended?.ExitCode}; mock requests {requests}; door tool lists {w.ToolsServed}; outputs " +
                   $"{string.Join(" | ", outputs.Select(o => o.Key + ": " + o.Value))}\n--- raw ---\n" +
                   $"{(ended?.Raw is { } raw ? raw[^Math.Min(raw.Length, 2500)..] : "(no TurnEnded)")}\n--- the session's last lines ---\n{history}";
        _output.WriteLine(said);
        Console.WriteLine(said);
        Assert.True(ended is not null, "the turn never reported its end");
        return (ended!, outputs);
    }

    static string Out(Dictionary<string, string> outputs, string id, AgentTurnEnded ended) =>
        outputs.TryGetValue(id, out var o) ? o : throw new Xunit.Sdk.XunitException($"no output for call {id}; the turn said: {ended.Raw[^Math.Min(ended.Raw.Length, 2500)..]}");

    /// <summary>The attempt's recorded cell assurance: its egress, and each property's attempt-side class.</summary>
    static (string? Egress, Dictionary<string, string> Attempt) AssuranceOf(Database db, string attemptId) => db.Read(_ =>
    {
        string? egress;
        using (var c = db.Cmd("SELECT egress FROM cell_assurance WHERE attempt_id = $a", ("$a", attemptId)))
            egress = c.ExecuteScalar() as string;
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var c = db.Cmd("SELECT property, class FROM cell_assurance_row WHERE attempt_id = $a AND side = 'attempt'", ("$a", attemptId)))
        using (var r = c.ExecuteReader())
            while (r.Read()) rows[r.GetString(0)] = r.GetString(1);
        return (egress, rows);
    });

    // ---- (0) StartInCell end to end ----------------------------------------------------------------------------------

    /// <summary>
    /// ROW (0), GUARD: A CELL-CLASS SESSION RUNS A TURN THROUGH <c>StartInCell</c> END TO END — -c's launch path, never run whole before
    /// (its record's NOT VERIFIED): a test host's manifest declared <c>cell</c>, the real session, the pinned codex as the app installs it,
    /// the app's door answering the harness, the mock model asking for one shell command — which runs in the cell, in the position's home,
    /// with the cell's own home and no grant — and the launch's assurance written with its egress.
    /// </summary>
    [LinuxFact]
    public async Task A_cell_class_session_runs_a_turn_through_StartInCell_end_to_end()
    {
        await using var w = await Executor_("guard0", CouncilRoles.Research, declaredClass: SeatPlan.CellClass);
        w.AttemptId = "exec-guard0";
        Assert.Equal(SeatPlan.CellClass, w.Session.SeatClass);
        var (ended, outputs) = await ExecutorTurn(w,
            [Exec("c1", "echo MARK; echo PWD=$(pwd); echo HOME=$HOME; echo GRANT=${" + AgentGrants.Variable + ":-none}; id -u")]);
        var c1 = Out(outputs, "c1", ended);
        Assert.Contains("MARK", c1);
        Assert.Contains("PWD=" + w.Home, c1);
        Assert.Contains("HOME=" + RouteA.CellHomes, c1);
        Assert.Contains("GRANT=none", c1);
        Assert.Equal(0, ended.ExitCode);
        Assert.True(w.ToolsServed > 0, "the app's door never served the harness its tools");
        var (egress, attempt) = AssuranceOf(w.Db, "exec-guard0");
        Assert.Equal(CellEgress.Web.Name, egress);
        Assert.Equal("gap", attempt.GetValueOrDefault(RouteA.WebLoopbackGap));
        Assert.Equal(0, w.Presence.Live);
    }

    // ---- item 5: the executor rows --------------------------------------------------------------------------------------

    /// <summary>
    /// ROW (d): AN EXECUTOR CELL WRITES ONLY ITS OWN HOME. A Member's turn — the chart's kind, the app's register, no declaration of the
    /// test's — writes into its own home, and is denied a sibling position's home, the owner's inbox, the app's state and the seat home.
    /// </summary>
    [LinuxFact]
    public async Task An_executor_cell_writes_only_its_own_home()
    {
        await using var w = await Executor_("d");
        Assert.Equal(SeatPlan.CellClass, w.Session.SeatClass);
        var sibling = Path.Combine(Paths.Workspace, "org", "pos-e7ec0e02");
        Directory.CreateDirectory(sibling);
        var inbox = Path.Combine(Paths.Workspace, MaterialScanner.InboxDir);
        // EVERY TARGET EXISTS, so a denial is the cell's and never a missing folder's.
        foreach (var dir in new[] { inbox, Paths.State, Paths.SeatHome }) Directory.CreateDirectory(dir);
        string Try(string label, string dir) => $"(echo x > '{dir}/w.txt' && echo {label}-WROTE) 2>/dev/null || echo {label}-denied; ";
        var (ended, outputs) = await ExecutorTurn(w, [Exec("d1",
            "echo own > scratch/own.txt && echo own-ok; " + Try("sibling", sibling) + Try("inbox", inbox) + Try("state", Paths.State) +
            Try("seat", Paths.SeatHome) + Try("app-home", Paths.Home))]);
        var d1 = Out(outputs, "d1", ended);
        foreach (var said in new[] { "own-ok", "sibling-denied", "inbox-denied", "state-denied", "seat-denied", "app-home-denied" })
            Assert.Contains(said, d1);
        Assert.DoesNotContain("WROTE", d1);
        Assert.Equal("own\n", File.ReadAllText(Path.Combine(w.Home, "scratch", "own.txt")));
    }

    /// <summary>
    /// ROW (g): A MEMBER WRITES A TOOL IN ITS CELL AND RUNS IT WITH NO ADMISSION BEYOND ITS TURN. Through the meter, as the app runs a turn:
    /// one admission, its attempt recorded <c>cell</c>, and inside that turn the executor writes a program into its home and runs it — no
    /// second admission, no approval, no other row.
    /// </summary>
    [LinuxFact]
    public async Task A_member_writes_a_tool_in_its_cell_and_runs_it_with_no_admission_beyond_its_turn()
    {
        await using var w = await Executor_("g", meter: true);
        var (ended, outputs) = await ExecutorTurn(w, [Exec("g1",
            "mkdir -p scripts && printf '#!/bin/sh\\necho TOOL-RAN \"$1\" in $(pwd)\\n' > scripts/tool.sh && chmod +x scripts/tool.sh && ./scripts/tool.sh one")],
            metered: true);
        Assert.Contains("TOOL-RAN one in " + w.Home, Out(outputs, "g1", ended));
        var attempt = Assert.Single(new AiAttemptStore(w.Db).Between(DateTimeOffset.MinValue, DateTimeOffset.MaxValue), a => a.Role == Executor);
        Assert.Equal(SeatClasses.Cell, attempt.SeatClass);
        var (egress, rows) = AssuranceOf(w.Db, attempt.Id);
        Assert.Equal(CellEgress.Web.Name, egress);
        Assert.Equal("gap", rows.GetValueOrDefault(RouteA.WebLanGap));
    }

    /// <summary>
    /// ROW (h): A TOOL WRITTEN IN ONE ATTEMPT RUNS IN THE NEXT. Two turns of one executor: the first writes a program into its home and the
    /// second — a fresh codex session, since a cell seat's per-attempt codex home is swept with its turn — runs it.
    /// </summary>
    [LinuxFact]
    public async Task A_tool_written_in_one_attempt_runs_in_the_next()
    {
        await using var w = await Executor_("h");
        w.AttemptId = "exec-h-1";
        var (first, one) = await ExecutorTurn(w, [Exec("h1",
            "mkdir -p scripts && printf '#!/bin/sh\\necho TOOL-RAN \"$1\"\\n' > scripts/tool.sh && chmod +x scripts/tool.sh && echo written")]);
        Assert.Contains("written", Out(one, "h1", first));
        w.AttemptId = "exec-h-2";
        var (second, two) = await ExecutorTurn(w, [Exec("h2", "./scripts/tool.sh two")]);
        Assert.Contains("TOOL-RAN two", Out(two, "h2", second));
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(new TurnSession(true, SessionWhy.Cell), second.Session);   // fresh, and the record says why: a cell seat never resumes
    }

    /// <summary>
    /// ROW (i): AN EXECUTOR'S TOOL REACHES 443 AND NO OTHER PORT, AND READS NO STATE, SEAT HOME OR INBOX. A program the executor runs in its
    /// cell: a connection to port 443 is let through to the network (refused by nobody listening, never denied by the cell), one to a
    /// loopback listener on another port is denied; the app's state, the seat home's sign-in and the owner's inbox cannot be read.
    /// </summary>
    [LinuxFact]
    public async Task An_executor_tool_reaches_443_and_no_other_port_and_reads_no_state_seat_home_or_inbox()
    {
        await using var w = await Executor_("i");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Directory.CreateDirectory(Paths.State);
            var state = Path.Combine(Paths.State, "executor-row.db");
            File.WriteAllText(state, "state");
            var inbox = Path.Combine(Paths.Workspace, MaterialScanner.InboxDir, "handed.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(inbox)!);
            File.WriteAllText(inbox, "the owner's");
            var auth = Path.Combine(w.Codex.Seat!.Root, "codex", "auth.json");
            File.WriteAllText(Path.Combine(w.Home, "scratch", "probe.py"), """
                import errno, socket, sys
                def connect(port):
                    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM); s.settimeout(3)
                    try: s.connect(("127.0.0.1", port)); return "connected"
                    except PermissionError: return "denied"
                    except OSError as e: return "errno=%d" % e.errno
                    finally: s.close()
                print("443:" + connect(443))
                print("other:" + connect(int(sys.argv[1])))
                for label, path in (("state", sys.argv[2]), ("seat", sys.argv[3]), ("inbox", sys.argv[4])):
                    try: open(path).read(); print(label + ":READ")
                    except PermissionError: print(label + ":denied")
                    except OSError as e: print(label + ":errno=%d" % e.errno)
                """);
            var (ended, outputs) = await ExecutorTurn(w, [Exec("i1", $"python3 -I scratch/probe.py {port} '{state}' '{auth}' '{inbox}'")]);
            var i1 = Out(outputs, "i1", ended);
            Assert.Contains("443:errno=111", i1);   // ECONNREFUSED: through the cell, nobody listening — never EACCES
            Assert.Contains("other:denied", i1);
            Assert.Contains("state:denied", i1);
            Assert.Contains("seat:denied", i1);
            Assert.Contains("inbox:denied", i1);
            Assert.DoesNotContain("READ", i1);
        }
        finally { listener.Stop(); }
    }

    /// <summary>
    /// ROW (j), GUARD: UNDER WEB, A ROUTE (a) CELL STILL CANNOT CONNECT TO THE DOOR. The door listens on an ephemeral loopback port (never
    /// 443: an unprivileged listener gets none below 1024); route (a)'s own policy — an executor's, egress <c>web</c> — reaches it from
    /// outside and is denied it from inside, and holds no bearer. -c's row (f) showed it under <c>none</c> only.
    /// </summary>
    [LinuxFact]
    public async Task Under_web_a_route_a_cell_still_cannot_connect_to_the_door()
    {
        PlatformIsACells();
        var w = new World("door-web");
        await using var door = new BrokerDoor { Grants = new AgentGrants() };
        door.Start();
        var port = door.Url!.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(door.Url.Port > 1023, $"the door listens on {door.Url.Port}");
        var probe = w.Script("door-web.py", """
            import os, socket, sys
            try:
                socket.create_connection(("127.0.0.1", int(sys.argv[1])), timeout=3).close(); print("door-connected")
            except PermissionError:
                print("door-denied")
            print("bearer=" + os.environ.get(sys.argv[2], "none"))
            """);
        Says(await Outside("/usr/bin/python3", probe, port, AgentGrants.Variable), "door-connected");
        var cellHome = Path.Combine(w.Root, "cell-home");
        Directory.CreateDirectory(cellHome);
        var policy = RouteA.Policy(w.Ws, "/usr/bin/python3", "/usr/bin", cellHome, cellHome);
        Assert.Equal(CellEgress.Web.Name, policy.Egress.Name);
        Says(await InCell(policy, "/usr/bin/python3", probe, port, AgentGrants.Variable), "door-denied", "bearer=none");
    }
}

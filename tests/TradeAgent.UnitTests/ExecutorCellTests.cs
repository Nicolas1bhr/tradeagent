using System.Reflection;
using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// AN EXECUTOR POSITION RUNS IN A CELL (<c>U-execution-environment</c>; <c>docs/ORCHESTRATION.md</c> § 2.8, § 2.3, § 5's doing-seat
/// column; the owner's answer of 2026-10-21, "Web (443) for every executor"). The rows every OS runs, OS-absolute: the class rule (a, b),
/// no degraded start (c), the executor cell's one egress (e), and the attempt's assurance under it (k). The Linux rows — a turn through
/// <c>AgentSession.StartInCell</c> against CI's pinned codex — are <c>CellLinuxTests.Executor.cs</c>.
/// </summary>
// Row (e) writes a runtimes.json and reads the catalog back.
[Collection(VendorOverrideFiles.Name)]
public class ExecutorCellTests : IDisposable
{
    const string Member = "pos-e7ec0001";
    const string HeadId = "pos-e7ec00h1";
    const string StaffId = "pos-e7ec00s1";

    public void Dispose()
    {
        if (File.Exists(RuntimeCatalog.OverridePath)) File.Delete(RuntimeCatalog.OverridePath);
    }

    static RuntimeManifest Codex => RuntimeCatalog.BuiltIn().Single(m => m.Id == "codex");

    /// <summary>A chart with a Member under Research's division, a Head of a team there and a Staff position in a staff unit.</summary>
    static Database Chart()
    {
        var db = TestEnv.NewDb();
        ThinkingSeatsTests.Seat(db, Member);
        ThinkingSeatsTests.Head(db, HeadId);
        db.Write(_ =>
        {
            using var c = db.Cmd("""
                INSERT INTO org_unit(id, parent_id, kind, status, created_at)
                VALUES('staff-e7ec', 'org', 'staff', 'active', '2026-10-22T12:00:00.0000000Z')
                """);
            return c.ExecuteNonQuery();
        });
        ThinkingSeatsTests.Seat(db, StaffId, unit: "staff-e7ec");
        return db;
    }

    /// <summary>
    /// A register as the app sets its own (<c>AppHost.FactsFor</c>): the chart's standing, and each position declared on the class the
    /// session's own rule gives it over <paramref name="codex"/> (<c>AppHost.DeclaredSeatClass</c>).
    /// </summary>
    static SeatRegister Register(Database db, RuntimeManifest? codex = null)
    {
        var runtime = codex ?? Codex;
        return new SeatRegister
        {
            Standing = id => new OrgStore(db).Standing(id),
            Facts = id => new SeatFacts
            {
                Standing = new OrgStore(db).Standing(id),
                DeclaredClass = runtime.SessionSeatClassFor(id, new OrgStore(db).Standing(id)?.Kind),
                ProvenCell = new CellProofStore(db).HoldsProof(),
                ActiveBeyondLegacy = new OrgStore(db).ActivePositionsBeyondLegacy()
            },
            Neighbours = () => new OrgStore(db).ActivePositionsBeyondLegacy()
        };
    }

    static AgentSession SessionOf(RuntimeManifest codex, string role, SeatRegister register, string exe = "/nonexistent/codex",
        TradeAgent.AgentRuntime.CellRoute? cells = null, AgentPresence? presence = null) =>
        new(codex, () => exe, () => Path.GetDirectoryName(exe)!, () => new Dictionary<string, string>(), presence: presence ?? new AgentPresence(),
            role: role, seatProbe: BrokerSeatTests.BrokerProbe(), seats: register, door: () => new Uri("http://127.0.0.1:1/mcp"),
            surfaceProbe: new BrokerSeatTests.FakeSurface(CellSurface), cells: cells);

    /// <summary>The cell class's offered surface for gpt-5.5, as -c's verifier expects it (RouteATests).</summary>
    internal static readonly string[] CellSurface =
        ["exec_command", "write_stdin", "list_mcp_resources", "list_mcp_resource_templates", "read_mcp_resource", "request_user_input",
         "apply_patch", "view_image", "tool_search"];

    // ---- (a) the class rule ------------------------------------------------------------------------------------------

    /// <summary>
    /// (a) A MEMBER BEYOND THE LEGACY PAIR IS DECLARED <c>cell</c> — by the session that starts it, by the class rule the app's declared
    /// seat (the gate) reads, and by the canon its home is written for: one answer, from the chart's kind, on the built-in codex, whose
    /// manifest has the class. A runtime with no cell class never gives a Member one.
    ///
    /// <para>RED at base: "Assert.Equal() Failure: Expected: cell Actual: broker" (the rule read the role alone).</para>
    /// </summary>
    [Fact]
    public void A_member_beyond_the_legacy_pair_is_declared_cell()
    {
        using var db = Chart();
        var register = Register(db);
        Assert.Equal(OrgPositionKind.Member, register.KindOf(Member));

        var session = SessionOf(Codex, Member, register);
        Assert.Equal(SeatClasses.Cell, session.SeatClass);
        Assert.Equal(SeatClasses.Cell, Codex.SessionSeatClassFor(Member, OrgPositionKind.Member));
        Assert.True(BrokerSeat.Offers("codex", SeatPlan.CellClass));
        Assert.Equal([RuntimeClass.Cell, RuntimeClass.Harness], WorkspaceBuilder.RuntimesOf(Member, OrgPositionKind.Member));

        // A RUNTIME WHOSE BUILT-IN MANIFEST HAS NO CELL CLASS gives a Member none: its class is whatever else it has, and the gate
        // then refuses a Member on an unconfined class (its shell is code).
        foreach (var other in RuntimeCatalog.BuiltIn().Where(m => m.Id != "codex" && m.Seat is not null))
            Assert.NotEqual(SeatClasses.Cell, other.SessionSeatClassFor(Member, OrgPositionKind.Member));
    }

    /// <summary>
    /// (b) GUARD: HEADS AND STAFF STAY <c>broker</c>, THE LEGACY PAIR <c>cli</c> — and a position whose kind nobody could read is never a
    /// Member: it starts the broker class, which the gate refuses wherever it is not the declared one.
    /// </summary>
    [Fact]
    public void Heads_and_staff_stay_broker_and_the_legacy_pair_cli()
    {
        using var db = Chart();
        var register = Register(db);
        Assert.Equal(OrgPositionKind.Head, register.KindOf(HeadId));
        Assert.Equal(OrgPositionKind.Staff, register.KindOf(StaffId));
        Assert.Equal(SeatClasses.Broker, SessionOf(Codex, HeadId, register).SeatClass);
        Assert.Equal(SeatClasses.Broker, SessionOf(Codex, StaffId, register).SeatClass);
        Assert.Equal(SeatClasses.Cli, SessionOf(Codex, CouncilRoles.Operations, register).SeatClass);
        Assert.Equal(SeatClasses.Cli, SessionOf(Codex, CouncilRoles.Research, register).SeatClass);
        Assert.Equal(SeatClasses.Broker, Codex.SessionSeatClassFor(Member, kind: null));
        Assert.Equal(SeatClasses.Cli, Codex.SessionSeatClassFor(CouncilRoles.Research, OrgPositionKind.Member));
        Assert.Equal([RuntimeClass.Broker, RuntimeClass.Harness], WorkspaceBuilder.RuntimesOf(HeadId, OrgPositionKind.Head));
        Assert.Equal([RuntimeClass.Broker, RuntimeClass.Harness], WorkspaceBuilder.RuntimesOf(StaffId, OrgPositionKind.Staff));
        Assert.DoesNotContain(RuntimeClass.Cell, WorkspaceBuilder.RuntimesOf(CouncilRoles.Research, OrgPositionKind.Member));
        Assert.Null(new SeatRegister().KindOf(Member));
        Assert.Null(new SeatRegister { Standing = _ => throw new InvalidOperationException("chart unreadable") }.KindOf(Member));
    }

    // ---- (c) no degraded start --------------------------------------------------------------------------------------------

    /// <summary>
    /// (c) A MEMBER WITH NO PROOF MEASURING WEB IS REFUSED, AND NEVER STARTED ON ANOTHER CLASS. On a host with no proof at all, the gate's
    /// own words; with a proof that is not route (a)'s, or route (a)'s measuring only <c>none</c>, <see cref="RouteA.Admission"/>'s words
    /// (or, off Linux, its platform's) — as the loop's look (<c>Barred</c>, lasting: nothing written, its wakes kept) and as the launch's
    /// hard line. In every case no program started, and the session's class is still <c>cell</c>.
    ///
    /// <para>RED at base: the Member was declared and started <c>broker</c> — "the program ran: exec …".</para>
    /// </summary>
    [Fact]
    public async Task A_member_with_no_proof_measuring_web_is_refused_and_never_started_on_another_class()
    {
        var platformOk = CellPlatform.Read().Refusal() is null;
        foreach (var (what, prove, said) in new (string, Action<Database, RuntimeManifest>, string)[]
                 {
                     ("no proof at all", (_, _) => { }, "no proven executor cell recorded"),
                     ("a proof that is not route (a)'s", (db, _) => ThinkingSeatsTests.ProveCell(db), "no cell proof counts"),
                     ("route (a)'s proof measuring only none", (db, codex) => new CellProofStore(db).Record(RouteA.Provider,
                         CellBenchmark.Profile(RouteA.Provider, CellPlatform.Read(), codex.Install.ExecutableSha256!), [CellEgress.None.Name]),
                      "measured only none"),
                 })
        {
            using var db = Chart();
            var (codex, exe, dump) = SeatHomeTests.Seat($"exec-c-{what.Length}");
            prove(db, codex);
            var register = Register(db, codex);
            var session = SessionOf(codex, Member, register, exe, new TradeAgent.AgentRuntime.CellRoute(db));
            Assert.Equal(SeatClasses.Cell, session.SeatClass);

            var ended = await SeatHomeTests.Turn(session);
            var bar = session.Barred();
            if (what == "no proof at all")
            {
                Assert.Contains(said, ended.Raw, StringComparison.Ordinal);
            }
            else
            {
                Assert.True(bar is { Lasting: true }, $"{what}: the loop's look let a cell seat with no proof measuring web through");
                Assert.Contains("did not start the AI as a cell seat", bar!.Why, StringComparison.Ordinal);
                Assert.Contains("did not start the AI as a cell seat", ended.Raw, StringComparison.Ordinal);
                if (platformOk) Assert.Contains(said, bar.Why, StringComparison.Ordinal);
            }
            Assert.True(!SeatHomeTests.Argv(dump).Any(l => l.StartsWith("exec", StringComparison.Ordinal)),
                $"{what}: the program ran: {string.Join(" | ", SeatHomeTests.Argv(dump))}");
            Assert.Equal(SeatClasses.Cell, session.SeatClass);
        }
    }

    // ---- (e) the executor cell's one egress ----------------------------------------------------------------------------

    /// <summary>
    /// (e) AN EXECUTOR CELL'S EGRESS IS <c>web</c>, AND NOTHING OUTSIDE CODE CHANGES IT: route (a)'s policy carries
    /// <see cref="RouteA.Egress"/>, which is <see cref="CellEgress.Web"/>; no manifest, seat plan or setting has a field that names an
    /// egress, and a <c>runtimes.json</c> that tries is refused or ignored — the policy is the same after it.
    ///
    /// <para>RED at base: "Expected: web Actual: none".</para>
    /// </summary>
    [UnixFact]
    public void An_executor_cells_egress_is_web_and_nothing_outside_code_changes_it()
    {
        var ws = Path.Combine(TestEnv.Home, "exec-e-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(ws);
        var layout = RouteA.LayoutFor("attempt-e-" + Guid.NewGuid().ToString("n")[..8]);
        var exe = Path.Combine(Paths.Tools, "codex", "bin", "codex");
        CellPolicy Policy() => RouteA.Policy(ws, exe, RouteA.CodexRoot(exe), layout.CellCodexHome, layout.CellHome);

        Assert.Equal(CellEgress.Web.Name, RouteA.Egress.Name);
        Assert.Equal(CellEgress.Web.Name, Policy().Egress.Name);
        Assert.Contains(CellEgress.Web.Name, CellBenchmark.ProvedEgress);

        // NO FIELD OF DATA NAMES AN EGRESS: not a manifest's, a seat plan's, nor the owner's settings.
        foreach (var type in new[] { typeof(RuntimeManifest), typeof(SeatPlan), typeof(InstallPlan), typeof(TradeAgentSettings) })
            Assert.DoesNotContain(type.GetProperties(BindingFlags.Public | BindingFlags.Instance),
                p => p.Name.Contains("Egress", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Network", StringComparison.OrdinalIgnoreCase));

        // A runtimes.json that names one is refused or ignored: the policy after it is the same.
        var before = Policy().Hash;
        Directory.CreateDirectory(Path.GetDirectoryName(RuntimeCatalog.OverridePath)!);
        File.WriteAllText(RuntimeCatalog.OverridePath, JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object> { ["id"] = "codex", ["egress"] = "none", ["seat"] = new Dictionary<string, object> { ["egress"] = "none" } }
        }));
        try { _ = RuntimeCatalog.Load(); } catch (Exception) { /* refused is as good as ignored */ }
        Assert.Equal(before, Policy().Hash);
        Assert.Equal(CellEgress.Web.Name, Policy().Egress.Name);
    }

    /// <summary>
    /// (e), THE WORKSPACE'S CANON: AN EXECUTOR'S <c>AGENTS.md</c> IS THE APP'S AGAIN BEFORE ITS TURN. Its cell writes its whole home, the
    /// canon included; the app's start writes it once (<see cref="WorkspaceBuilder"/>) and the cell seat's session puts the text the app
    /// kept back before the vendor reads it — so the turn's record reads the app's own canon, version and all. Every OS: the launch
    /// itself is refused off a proven Linux cell, after the canon is put back.
    ///
    /// <para>RED before the fix: the canon the cell had rewritten stayed, and the turn ran under it ("rewritten by the cell").</para>
    /// </summary>
    [Fact]
    public async Task An_executors_canon_is_the_apps_again_before_its_turn()
    {
        using var db = Chart();
        ThinkingSeatsTests.ProveCell(db);
        var files = new AppFileManifest(Path.Combine(TestEnv.Home, $"app-files-{Guid.NewGuid():n}.tsv"));
        var root = Path.Combine(TestEnv.Home, "exec-canon-" + Guid.NewGuid().ToString("n")[..8]);
        var ctx = new WorkspaceContext("Simulator (built in)", true, null, TradingMode.PAPER, true, null, new RiskPolicy(),
            ConnectorIsBuiltInSimulator: true, Role: Member, Kind: OrgPositionKind.Member);
        var home = WorkspaceBuilder.Build(ctx, root, files);
        var canon = Path.Combine(home, Canon.CliFile);
        var own = File.ReadAllText(canon);
        Assert.Contains("a sealed Linux cell", own, StringComparison.Ordinal);
        File.WriteAllText(canon, "# rewritten by the cell\n");

        var (codex, exe, _) = SeatHomeTests.Seat("exec-canon");
        var session = new AgentSession(codex, () => exe, () => home, () => new Dictionary<string, string>(), presence: new AgentPresence(),
            role: Member, appFiles: files, seatProbe: BrokerSeatTests.BrokerProbe(), seats: Register(db, codex),
            door: () => new Uri("http://127.0.0.1:1/mcp"), surfaceProbe: new BrokerSeatTests.FakeSurface(CellSurface));
        var ended = await SeatHomeTests.Turn(session);
        Assert.Equal(own, File.ReadAllText(canon));
        Assert.True(ended.Canon is { AppOwn: true, Version: Canon.Version }, $"the turn's canon record: {ended.Canon}; {ended.Raw}");
    }

    // ---- (k) the attempt's assurance under web ------------------------------------------------------------------------

    /// <summary>
    /// (k) AN EXECUTOR ATTEMPT'S ASSURANCE NAMES WEB, AND ITS LOOPBACK AND LAN 443 GAPS: the network row says <c>egress web</c>, and two
    /// GAP rows under the proof's own names are on the cell, the harness and the attempt — so the attempt's own class is a GAP, never a
    /// pass. Under <c>none</c> (-c's) neither row is there.
    ///
    /// <para>RED at base: it did not compile — <c>AssuranceRows</c> took no egress; its network row said "egress none".</para>
    /// </summary>
    [Fact]
    public void An_executor_attempts_assurance_names_web_and_its_loopback_and_lan_443_gaps()
    {
        var rows = RouteA.AssuranceRows(11, RouteA.Egress);
        var network = rows.Single(r => r.Property == "network" && r.Side == AssuranceSide.Cell);
        Assert.StartsWith("egress web", network.Evidence, StringComparison.Ordinal);
        Assert.Contains("cell proof 11", network.Evidence, StringComparison.Ordinal);
        foreach (var gap in new[] { RouteA.WebLoopbackGap, RouteA.WebLanGap })
        {
            Assert.Contains(gap, CellBenchmark.RowNames);
            foreach (var side in new[] { AssuranceSide.Cell, AssuranceSide.Harness, AssuranceSide.Attempt })
                Assert.Equal(EnforcementClass.Gap, rows.Single(r => r.Property == gap && r.Side == side).Class);
        }
        Assert.Contains("443", rows.Single(r => r.Property == RouteA.WebLoopbackGap && r.Side == AssuranceSide.Cell).Evidence);
        Assert.Contains("ephemeral loopback port", rows.Single(r => r.Property == "broker-reach" && r.Side == AssuranceSide.Cell).Evidence);

        var none = RouteA.AssuranceRows(11, CellEgress.None);
        Assert.StartsWith("egress none", none.Single(r => r.Property == "network" && r.Side == AssuranceSide.Cell).Evidence, StringComparison.Ordinal);
        Assert.DoesNotContain(none, r => r.Property.StartsWith("gap-web", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => RouteA.AssuranceRows(11, CellEgress.WebPlain));
    }
}

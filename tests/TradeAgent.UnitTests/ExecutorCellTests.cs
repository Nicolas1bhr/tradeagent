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
}

using System.Reflection;
using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// WHAT EACH ROLE OWNS IS THE APP'S RECORD AND NOTHING ELSE (<c>U-reconcile-wakes</c>, item 2). An objective is the
/// desired world as app data: kinds in code, instances derived from rows the app wrote, and no path from anything an
/// agent writes into either — not an op, an argument, a file or a public writer.
/// </summary>
public class RoleObjectivesTests
{
    /// <summary>
    /// (i) NOTHING AN AGENT WRITES BECOMES AN OBJECTIVE OR A WAKE. No op and no op argument names an objective or a
    /// wake; the one file the app reads to schedule a role is <c>next.json</c>, with its one field; and
    /// <see cref="RoleObjectives"/> has no writer — every public member is a constant or a read, and the derivation is
    /// handed the database, the role and the host's own reading of the book, never a path into an agent's folder.
    ///
    /// <para>Pins an absence, so it has no red at base; watched red with a planted public writer on the class.
    /// <c>MissionEventReachTests.No_verb_and_no_pipe_op_names_the_wake_queue</c> stands beside it unedited.</para>
    /// </summary>
    [Fact]
    public void Nothing_an_agent_writes_becomes_an_objective_or_a_wake()
    {
        string[] banned = ["objective", "wake", "renewal"];

        var ops = typeof(Ops).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!).ToArray();
        var args = GatewaySchema.Ops().SelectMany(o => o.Args.Select(a => $"{o.Op}.{a.Name}")).ToArray();
        foreach (var word in banned)
        {
            Assert.DoesNotContain(ops, o => o.Contains(word, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(args, a => a.Contains(word, StringComparison.OrdinalIgnoreCase));
        }

        // THE ONE FILE, AS IT IS: one field, the delay, capped by the loop.
        Assert.Equal(".tradeagent/next.json", MissionLoop.WakeFile);
        Assert.Equal(["AfterSeconds"], typeof(MissionNext).GetProperties().Select(p => p.Name).ToArray());

        // NO WRITER: constants and reads only.
        var type = typeof(RoleObjectives);
        Assert.Empty(type.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance));
        Assert.All(type.GetFields(BindingFlags.Public | BindingFlags.Static), f => Assert.True(f.IsLiteral, f.Name));
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Assert.Equal(["KindsOf", "Open"], methods.Select(m => m.Name).Order().ToArray());
        Assert.All(methods, m => Assert.True(
            m.ReturnType.IsGenericType && m.ReturnType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>), m.Name));
        var open = type.GetMethod(nameof(RoleObjectives.Open))!;
        Assert.Equal([typeof(Database), typeof(string), typeof(IReadOnlyList<string>)],
            open.GetParameters().Select(p => p.ParameterType).ToArray());
    }

    /// <summary>
    /// THE INSTANCES ARE THE ROWS. Each kind opens from the fact the app recorded and closes when that fact does; the
    /// research mandate is standing; a deployment's request is not the chair's.
    /// </summary>
    [Fact]
    public void Each_instance_opens_from_the_row_the_app_recorded_and_closes_with_it()
    {
        using var db = TestEnv.NewDb();
        var at = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        var events = new MissionEventStore(db);

        Assert.Empty(RoleObjectives.Open(db, CouncilRoles.Operations, []));
        Assert.Equal([new RoleObjective(RoleObjectives.ResearchMandate, CouncilRoles.Research)],
            RoleObjectives.Open(db, CouncilRoles.Research, []));
        Assert.Equal([new RoleObjective(RoleObjectives.PositionWatched, RoleObjectives.UnreadBook)],
            RoleObjectives.Open(db, CouncilRoles.Operations, null));

        // THE OWNER'S WORDS open the chair's objective until a turn takes them.
        Assert.True(events.RecordOwnerMessage("how is it going?", at));
        var owner = Assert.Single(RoleObjectives.Open(db, CouncilRoles.Operations, []));
        Assert.Equal(RoleObjectives.OwnerAnswered, owner.Kind);
        events.Consume([owner.Entity], "turn-1", at);
        Assert.Empty(RoleObjectives.Open(db, CouncilRoles.Operations, []));

        // A BOUNDARY opens both directors' until each has assessed it.
        var boundary = new CouncilBoundaries(db).Open(BoundaryKind.Promotion, "version-a", 1,
            BoundaryDisposition.Deploy, "holdout run 1", at).Row;
        Assert.Contains(new RoleObjective(RoleObjectives.BoundaryAssessed, boundary.Id),
            RoleObjectives.Open(db, CouncilRoles.Operations, []));
        Assert.Contains(new RoleObjective(RoleObjectives.BoundaryAssessed, boundary.Id),
            RoleObjectives.Open(db, CouncilRoles.Research, []));

        // A POSITION in the book the host read.
        Assert.Contains(new RoleObjective(RoleObjectives.PositionWatched, "ES +1"),
            RoleObjectives.Open(db, CouncilRoles.Operations, ["ES +1"]));

        Assert.Equal([RoleObjectives.OwnerAnswered, RoleObjectives.RequestFinal, RoleObjectives.PositionWatched,
                RoleObjectives.BoundaryAssessed], RoleObjectives.KindsOf(CouncilRoles.Operations));
        Assert.Equal(1, RoleObjectives.Version);
    }

    /// <summary>
    /// A WITHDRAWN LOOK IS ONLY EVER THE ROLE'S OWN SCHEDULED ONE. The statement's guard, as
    /// <c>BringLookForward</c>'s is: the owner's press (a review WITH a payload), the other role's look, a renewal
    /// and an owner's message keep their rows whatever the caller asks.
    /// </summary>
    [Fact]
    public void Only_a_roles_own_scheduled_look_is_ever_withdrawn()
    {
        using var db = TestEnv.NewDb();
        var at = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        var events = new MissionEventStore(db);
        var chair = CouncilRoles.Operations;
        events.RaiseDue("review:a", MissionEventKind.Review, at, at.AddMinutes(30), role: chair);
        events.RaiseDue("review:press", MissionEventKind.Review, at, at, payload: "{\"because\":\"owner\"}", role: chair);
        events.RaiseDue("review:b#research", MissionEventKind.Review, at, at.AddMinutes(30), role: CouncilRoles.Research);
        events.RaiseDue("renewal:x", MissionEventKind.Renewal, at, at.AddHours(12), role: chair);
        events.Raise("owner:1", MissionEventKind.Owner, at, role: chair);

        Assert.Equal(1, events.WithdrawLooks(chair, at, "no open work"));

        var look = events.Get("review:a")!;
        Assert.True(look.Consumed);
        Assert.Null(look.ConsumedBy);
        Assert.Equal(MissionEventDisposition.Withdrawn, look.Disposition);
        Assert.Equal("no open work", look.DispositionDetail);
        foreach (var kept in new[] { "review:press", "review:b#research", "renewal:x", "owner:1" })
            Assert.False(events.Get(kept)!.Consumed, kept);
    }

    /// <summary>EVERY ATTEMPT SAYS WHICH OBJECTIVES IT WAS SCHEDULED UNDER, as it says which canon it ran under.</summary>
    [Fact]
    public async Task An_attempt_records_the_objectives_version()
    {
        using var db = TestEnv.NewDb();
        var root = Path.Combine(TestEnv.Home, $"objectives-{Guid.NewGuid():n}");
        Directory.CreateDirectory(root);
        var meter = new TurnMeter(db, () => 100m, runtimeId: () => "probe",
            recordPath: Path.Combine(root, "turns.jsonl"), live: new LiveAttempts());
        var session = AgentRuntimeProbe.SessionOverStream(
            """{"type":"turn.completed","usage":{"input_tokens":10,"output_tokens":2}}""");
        using (meter.Attach(session, CouncilRoles.Operations))
            await session.SendAsync("hello");

        using var c = db.Cmd("SELECT id FROM ai_attempt ORDER BY started_at DESC, rowid DESC LIMIT 1");
        var row = new AiAttemptStore(db).Get(Convert.ToString(c.ExecuteScalar())!)!;
        using var context = JsonDocument.Parse(row.Context!);
        Assert.True(context.RootElement.TryGetProperty("objectives_version", out var version), row.Context);
        Assert.Equal(RoleObjectives.Version, version.GetInt32());
    }
}

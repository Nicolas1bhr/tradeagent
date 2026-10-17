using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE MANDATE'S LIFE (<c>U-org-assignments-a</c>, O5a; ORCHESTRATION §§ 2.2–2.4, 2.9, 2.10): a head assigns a task or an
/// exploration into its own subtree, the holder's launches are charged to it, its delivery is sealed and checked by the app,
/// code triages, the head accepts or rejects. NOTHING HERE REACHES A REAL MODEL (R1): every caller is an in-process
/// <see cref="GatewayPipeServer"/> call under a launch id the TEST seeded in the ledger with the seat assurance it names — the
/// <c>OrgRightsTests</c> pattern — and every position beyond the legacy pair is written by the test with raw SQL, as no writer
/// of the chart exists yet.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class OrgAssignmentTests
{
    /// <summary>The head of a team under Research's division, and two members of that team.</summary>
    const string Head = "pos-a1c0ffee0001", Member = "pos-a1c0ffee0002", Other = "pos-a1c0ffee0003";

    /// <summary>A second team, outside the head's subtree, and its member.</summary>
    const string Outsider = "pos-b2c0ffee0001";

    const string Team = "team-a1c0ffee", Elsewhere = "team-b2c0ffee";

    const string Stamp = "2026-10-11T12:00:00.0000000Z";

    static readonly DateTimeOffset Due = DateTimeOffset.UtcNow.AddDays(7);

    // ---- the chart, as only a test may write it --------------------------------------------------------------------

    static void Exec(Database db, string sql, params (string, object?)[] ps) => db.Write(_ =>
    {
        using var c = db.Cmd(sql, ps);
        return c.ExecuteNonQuery();
    });

    /// <summary>A team under <paramref name="parent"/>, headed by a new active position.</summary>
    static void HeadATeam(Database db, string position, string team, string parent = OrgIds.Research)
    {
        Exec(db, "INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES($t,$p,'team',NULL,'active',$at)",
            ("$t", team), ("$p", parent), ("$at", Stamp));
        Seat(db, position, team);
        Exec(db, "UPDATE org_unit SET head_position_id=$id WHERE id=$t", ("$id", position), ("$t", team));
    }

    /// <summary>A position in <paramref name="unit"/>, in the status given.</summary>
    static void Seat(Database db, string id, string unit, string status = OrgPositionStatus.Active) =>
        Exec(db, "INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES($id,$u,$h,$s,$at)",
            ("$id", id), ("$u", unit), ("$h", $"org/{id}"), ("$s", status), ("$at", Stamp));

    /// <summary>The chart every test starts from: <see cref="Head"/> heads <see cref="Team"/> with two members; an outsider elsewhere.</summary>
    static void Chart(Database db)
    {
        HeadATeam(db, Head, Team);
        Seat(db, Member, Team);
        Seat(db, Other, Team);
        HeadATeam(db, Outsider, Elsewhere);
    }

    /// <summary>
    /// A LAUNCH THE TEST RECORDED, as the ledger would: <paramref name="role"/>'s row, LAUNCHED, on a seat of
    /// <paramref name="seatClass"/> with its assurance — never a real model.
    /// </summary>
    static string Launch(Database db, string role, string seatClass = SeatClasses.Harness, decimal reserved = 0.10m,
        string? mandate = null, IReadOnlyList<string>? consuming = null)
    {
        var id = $"turn-{Guid.NewGuid():n}"[..17];
        var admission = new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = id, StartedAt = DateTimeOffset.UtcNow, Role = role, ReservedCost = reserved, SeatClass = seatClass,
            Assurance = SeatClasses.AssuranceOf(seatClass) is { } a ? SeatClasses.Name(a) : null, MandateId = mandate
        }, consuming);
        Assert.True(admission.Admitted, admission.Refusal);
        return id;
    }

    static IpcRequest Frame(string op, params (string Key, object Value)[] args) => new()
    {
        Op = op,
        Session = "mandates",
        RequestId = "mandates-" + Guid.NewGuid().ToString("n")[..12],
        Args = args.ToDictionary(a => a.Key, a => JsonSerializer.SerializeToElement(a.Value))
    };

    static JsonElement Body(IpcResponse r) => JsonDocument.Parse(Json.Write(r.Data)).RootElement;

    static long Count(Database db, string table, string where = "1") => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT COUNT(*) FROM {table} WHERE {where}");
        return (long)c.ExecuteScalar()!;
    });

    static MandateIssue Issue(string holder = Member, string kind = MandateKind.Task, string? shape = MandateShape.Report,
        bool exact = false, decimal allocation = 5m, int retries = MandateLimits.DefaultRetryLimit) =>
        new(Head, null, holder, kind, "find out whether the funding skew predicts the next hour", null,
            kind == MandateKind.Task ? shape : null, exact, Due, allocation, retries);

    // ---- item 1: the records ---------------------------------------------------------------------------------------

    /// <summary>
    /// THE MANDATE'S RECORDS ARE APPEND-ONLY AND ITS STATUS IS ITS LATEST EVENT (rung 40): an issue writes the row, its
    /// <c>issued</c> event and ONE <c>mandate</c> wake to the holder in one write; no UPDATE or DELETE reaches a mandate, its
    /// events, a delivery or a check; a cancellation is an event, never an edit, and closes the holder's untaken wake.
    /// </summary>
    [Fact]
    public void The_mandate_records_are_append_only_and_a_status_is_its_latest_event()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var now = DateTimeOffset.UtcNow;

        var m = org.Assign(Issue(), now);
        Assert.StartsWith(OrgStore.MandatePrefix, m.Id, StringComparison.Ordinal);
        Assert.Equal(Team, m.UnitId);
        Assert.Equal(MandateEventKind.Issued, org.MandateStatus(m.Id));
        Assert.Equal(MandateCheck.All, m.Checks);

        var wake = Assert.Single(new MissionEventStore(db).OfKind(MissionEventKind.Mandate));
        Assert.Equal(Member, wake.Role);
        Assert.Equal(m.Id, wake.Payload);

        foreach (var sql in new[]
                 {
                     "UPDATE org_mandate SET objective='x'", "DELETE FROM org_mandate",
                     "UPDATE org_mandate_event SET kind='accepted'", "DELETE FROM org_mandate_event"
                 })
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Exec(db, sql));
        Assert.Equal("find out whether the funding skew predicts the next hour", org.Mandate(m.Id)!.Objective);

        org.Cancel(m.Id, Head, null, "the question is answered elsewhere", now.AddMinutes(1));
        Assert.Equal(MandateEventKind.Cancelled, org.MandateStatus(m.Id));
        Assert.False(org.IsOpen(m.Id));
        Assert.Equal([MandateEventKind.Issued, MandateEventKind.Cancelled], org.MandateEvents(m.Id).Select(e => e.Kind));
        var closed = new MissionEventStore(db).Get(wake.Id)!;
        Assert.True(closed.Consumed);
        Assert.Equal(MissionEventDisposition.Withdrawn, closed.Disposition);

        // ONLY THE DECIDER CANCELS, and only an open mandate.
        var second = org.Assign(Issue(), now);
        Assert.Contains("only its decider", Assert.Throws<TradeAgentException>(() => org.Cancel(second.Id, Member, null, "no", now)).Message,
            StringComparison.Ordinal);
        Assert.Throws<TradeAgentException>(() => org.Cancel(m.Id, Head, null, "again", now));

        Assert.Equal(1L, Count(db, "pragma_table_info('ai_attempt')", "name='mandate_id'"));
        Assert.Equal(Versions.DatabaseSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            db.Read(_ => { using var c = db.Cmd("SELECT value FROM meta WHERE key='schema_version'"); return c.ExecuteScalar() as string; }));
    }

    // ---- item 2: the caller gate, assign and cancel --------------------------------------------------------------

    /// <summary>The assign frame every pipe test sends, naming <paramref name="holder"/>.</summary>
    static IpcRequest AssignFrame(string holder, string kind = MandateKind.Task, bool exact = false, decimal allocation = 5m,
        int retries = MandateLimits.DefaultRetryLimit) =>
        Frame(Ops.MandateAssign, [
            ("holder", holder), ("goal", "find out whether the funding skew predicts the next hour"), ("kind", kind),
            .. kind == MandateKind.Task ? new (string, object)[] { ("shape", MandateShape.Report) } : [],
            ("due", Due.ToString("O")), ("allocation", allocation), ("retries", retries), ("exact", exact ? "true" : "false")
        ]);

    static async Task<(TradingGateway Gw, Database Db, GatewayPipeServer Server)> Pipe()
    {
        var (gw, _, db) = await TestEnv.Ready();
        Chart(db);
        return (gw, db, new GatewayPipeServer(gw, IpcToken.Ensure()));
    }

    /// <summary>
    /// (a) A BROKERED — OR CONFINED — HEAD ASSIGNS INTO ITS SUBTREE AND THE HOLDER GETS ONE WAKE: the app mints <c>man-…</c>,
    /// writes the mandate and its <c>issued</c> event and one <c>mandate</c> wake to the holder in one write. RED at base: no
    /// <c>mandate-assign</c> op existed (unknown operation).
    /// </summary>
    [Fact]
    public async Task A_brokered_head_assigns_into_its_subtree_and_the_holder_gets_one_wake()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;

        var reply = await server.CallAsync(AssignFrame(Member), Head, Launch(db, Head));
        Assert.True(reply.Ok, Json.Write(reply.Error));
        var id = Body(reply).GetProperty("mandate").GetString()!;
        Assert.StartsWith(OrgStore.MandatePrefix, id, StringComparison.Ordinal);
        Assert.Equal(MandateEventKind.Issued, Body(reply).GetProperty("status").GetString());

        var m = gw.Org.Mandate(id)!;
        Assert.Equal((Head, Member, Team, MandateKind.Task, MandateShape.Report), (m.Decider, m.Holder, m.UnitId, m.Kind, m.OutputShape));
        Assert.Equal(5m, m.Allocation);
        var wake = Assert.Single(new MissionEventStore(db).OfKind(MissionEventKind.Mandate));
        Assert.Equal((Member, id), (wake.Role, wake.Payload));
        Assert.Equal(WakeClass.Research, WakePriority.ClassOf(wake.Kind));

        // A CONFINED SEAT carries it too; an exploration names no shape.
        var confined = await server.CallAsync(AssignFrame(Other, MandateKind.Exploration), Head, Launch(db, Head, SeatClasses.Cell));
        Assert.True(confined.Ok, Json.Write(confined.Error));
        Assert.Equal(2L, Count(db, "org_mandate"));
        Assert.Equal(2, new MissionEventStore(db).OfKind(MissionEventKind.Mandate).Count);
    }

    /// <summary>
    /// (a2) THE LEGACY PAIR ON THEIR UNCONFINED SEATS HOLD NO MANDATE VERB, RESEARCH'S HEADSHIP INCLUDED (GUARD): <c>research</c>
    /// heads <c>div-research</c>, whose subtree holds the team, and <c>operations</c> heads <c>div-operations</c> — both are
    /// refused at the verb on a CLI seat, as a non-legacy head on one is, and nothing is written. Watched red with the
    /// assurance check removed from <c>OrgVerbCaller</c> (headship kept): research's assign is served.
    /// </summary>
    [Fact]
    public async Task The_legacy_pair_on_unconfined_seats_hold_no_mandate_verb_research_headship_included()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        Seat(db, "pos-a1c0ffee00f0", OrgIds.Operations);

        Assert.Equal(OrgPositionKind.Head, gw.Org.Standing(CouncilRoles.Research)!.Kind);
        Assert.Contains(gw.Org.SubtreeOf(CouncilRoles.Research), u => u.Id == Team);

        foreach (var (caller, holder) in new[]
                 {
                     (CouncilRoles.Research, Member), (CouncilRoles.Operations, "pos-a1c0ffee00f0"), (Head, Member)
                 })
        {
            var refused = await server.CallAsync(AssignFrame(holder), caller, Launch(db, caller, SeatClasses.Cli));
            Assert.False(refused.Ok, $"{caller}'s assign on an unconfined seat was served");
            Assert.Contains("Heading a unit grants no verb by itself", refused.Error!.Message, StringComparison.Ordinal);
        }
        Assert.Equal(0L, Count(db, "org_mandate"));
        Assert.Equal(0L, Count(db, "org_mandate_event"));
        Assert.Empty(new MissionEventStore(db).OfKind(MissionEventKind.Mandate));

        // NO GRANT AT ALL, AN APP PRINCIPAL, OR A LAUNCH NOT IN THE LEDGER is nobody.
        Assert.False((await server.CallAsync(AssignFrame(Member), null, null)).Ok);
        Assert.False((await server.CallAsync(AssignFrame(Member), AppPrincipals.Referee, "turn-x")).Ok);
        Assert.False((await server.CallAsync(AssignFrame(Member), Head, "turn-never-recorded")).Ok);
        Assert.Equal(0L, Count(db, "org_mandate"));

        // AND THE CLI'S REACH NAMES NONE OF THEM, for either legacy role.
        foreach (var role in CouncilRoles.All)
            Assert.DoesNotContain(TradeAgent.AgentRuntime.AgentReach.For(role, TradeAgent.AgentRuntime.RuntimeClass.Cli).Verbs,
                v => Ops.IsOrganisation(v.Op));
    }

    /// <summary>
    /// (a3) AN ASSIGN OUTSIDE THE SUBTREE, TO ITSELF, AN APP PRINCIPAL, AN INACTIVE POSITION OR OPERATIONS IS REFUSED, in words,
    /// with nothing written — on a brokered seat, so the refusal is the chart's and not the seat's. RED at base: no op existed.
    /// Watched red with the subtree check removed: the outsider's assign is served.
    /// </summary>
    [Fact]
    public async Task An_assign_outside_the_subtree_to_itself_an_app_principal_an_inactive_position_or_operations_is_refused()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        Seat(db, "pos-a1c0ffee00d0", Team, OrgPositionStatus.Dormant);

        foreach (var (holder, words) in new[]
                 {
                     (Outsider, "not in the subtree you head"),
                     (Head, "your own position"),
                     (AppPrincipals.Referee, "app's own principals"),
                     ("pos-a1c0ffee00d0", "not active"),
                     (CouncilRoles.Operations, "holds no mandate"),
                     ("pos-nobody", "no position on the chart")
                 })
        {
            var refused = await server.CallAsync(AssignFrame(holder), Head, Launch(db, Head));
            Assert.False(refused.Ok, $"an assign to {holder} was served");
            Assert.Contains(words, refused.Error!.Message, StringComparison.Ordinal);
            Assert.Contains("Nothing was written", refused.Error.Message, StringComparison.Ordinal);
        }

        // AND THE MANDATE'S OWN BOUNDS: a past deadline, a zero allocation, a task with no shape.
        var past = Frame(Ops.MandateAssign, ("holder", Member), ("goal", "g"), ("kind", "task"), ("shape", "report"),
            ("due", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O")), ("allocation", 1m));
        Assert.Contains("not in the future", (await server.CallAsync(past, Head, Launch(db, Head))).Error!.Message, StringComparison.Ordinal);
        Assert.False((await server.CallAsync(AssignFrame(Member, allocation: 0m), Head, Launch(db, Head))).Ok);
        var shapeless = Frame(Ops.MandateAssign, ("holder", Member), ("goal", "g"), ("kind", "task"),
            ("due", Due.ToString("O")), ("allocation", 1m));
        Assert.Contains("output shape", (await server.CallAsync(shapeless, Head, Launch(db, Head))).Error!.Message, StringComparison.Ordinal);
        // AN ARGUMENT THE VERB DOES NOT TAKE — an id, a status — is refused, never ignored.
        var forged = Frame(Ops.MandateAssign, ("holder", Member), ("goal", "g"), ("kind", "exploration"),
            ("due", Due.ToString("O")), ("allocation", 1m), ("id", "man-chosen"));
        Assert.Contains("'id' is not an argument", (await server.CallAsync(forged, Head, Launch(db, Head))).Error!.Message, StringComparison.Ordinal);

        Assert.Equal(0L, Count(db, "org_mandate"));
        Assert.Empty(new MissionEventStore(db).OfKind(MissionEventKind.Mandate));
    }

    /// <summary>
    /// (f) NO MANDATE OP IS MUTATING OR REACHES OPERATOR AUTHORITY (GUARD): <see cref="Ops.Mutating"/> is exactly the order ops
    /// and <c>deployment-stop</c>, as before; no organisation verb is in it, none is described mutating, and no name or argument
    /// of one speaks of the mode, the kill switch, an approval, live trading, an update, a credential or an order. Watched red
    /// with <c>mandate-deliver</c> added to <see cref="Ops.Mutating"/>.
    /// </summary>
    [Fact]
    public void No_mandate_op_is_mutating_or_reaches_operator_authority()
    {
        Assert.Equal([Ops.Buy, Ops.Sell, Ops.Modify, Ops.Cancel, Ops.CancelAll, Ops.Close, Ops.CloseAll, Ops.DeploymentStop], Ops.Mutating);
        Assert.NotEmpty(Ops.Organisation);
        string[] authority = ["mode", "kill", "approv", "live", "update", "credential", "token", "key", "order", "symbol", "side",
            "quantity", "size", "account", "trade"];
        foreach (var op in Ops.Organisation)
        {
            Assert.False(Ops.IsMutating(op), $"{op} is mutating");
            var spec = Assert.Single(GatewaySchema.Ops(), o => o.Op == op);
            Assert.False(spec.Mutating, $"{op} is described mutating");
            foreach (var word in authority)
            {
                Assert.DoesNotContain(word, op, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(spec.Args, a => a.Name.Contains(word, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    /// <summary>
    /// ORGANISATION VERB NAMES AND PIPE OP NAMES ARE DISJOINT (GUARD; ORCHESTRATION § 2.9): every organisation verb is a
    /// <c>mandate-*</c> name that no other op of the pipe carries — so <c>cancel</c>, an order op that moves money, is never an
    /// organisation verb. Read off <see cref="Ops"/>'s own fields. Watched red with <c>MandateCancel</c> renamed <c>cancel</c>.
    /// </summary>
    [Fact]
    public void Organisation_verb_names_and_pipe_op_names_are_disjoint()
    {
        var fields = typeof(Ops).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).ToList();
        var organisation = fields.Where(f => f.Name.StartsWith("Mandate", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!).ToHashSet(StringComparer.Ordinal);
        var pipe = fields.Where(f => !f.Name.StartsWith("Mandate", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(organisation.Order(StringComparer.Ordinal), Ops.Organisation.Order(StringComparer.Ordinal));
        Assert.All(organisation, op => Assert.StartsWith("mandate-", op, StringComparison.Ordinal));
        Assert.Empty(organisation.Intersect(pipe));
        Assert.DoesNotContain(Ops.Cancel, organisation);
    }
}

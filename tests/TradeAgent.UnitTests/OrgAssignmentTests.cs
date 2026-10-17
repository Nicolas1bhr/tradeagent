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
            // WORD BY WORD of the name and each argument, so 'deliver' is not read as 'live'.
            var words = op.Split('-').Concat(spec.Args.SelectMany(a => a.Name.Split('_'))).ToList();
            foreach (var word in authority)
                Assert.DoesNotContain(words, w => w.StartsWith(word, StringComparison.OrdinalIgnoreCase));
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

    // ---- item 3: the launch carries its mandate ------------------------------------------------------------------

    /// <summary>A launch of <paramref name="role"/> as the ledger would admit it, answering <paramref name="consuming"/> — its admission, not asserted.</summary>
    static AiAdmission Begin(Database db, string role, decimal reserved, IReadOnlyList<string>? consuming = null, string? mandate = null,
        string? id = null) =>
        new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = id ?? $"turn-{Guid.NewGuid():n}"[..17], StartedAt = DateTimeOffset.UtcNow, Role = role, ReservedCost = reserved,
            SeatClass = SeatClasses.Harness, Assurance = SeatClasses.Name(SeatAssurance.Brokered), MandateId = mandate
        }, consuming);

    /// <summary>A <c>mandate</c> wake to the holder, as the app raises one.</summary>
    static string MandateWake(Database db, MandateRow m, long seq)
    {
        var id = MissionEventIds.Mandate(m.Id, seq, "test");
        new MissionEventStore(db).Raise(id, MissionEventKind.Mandate, DateTimeOffset.UtcNow, payload: m.Id, role: m.Holder);
        return id;
    }

    /// <summary>
    /// (b) A LAUNCH PAST ITS MANDATE'S ALLOCATION IS REFUSED WITH NO ROW, NO RESERVATION, NO WAKE: the holder's first launch
    /// answers the mandate's wake and is charged to it (<c>ai_attempt.mandate_id</c>, in the admission write); the next one, whose
    /// reservation would take the mandate past its lifetime allocation, is refused in <see cref="AiAttemptStore.Begin"/> — the
    /// third gate, after the day and the chain — naming the mandate, and nothing is written. A closed mandate admits nothing;
    /// a launch serves one mandate. RED at base: no gate — the second launch was admitted. Watched red with the gate removed.
    /// </summary>
    [Fact]
    public void A_launch_past_its_mandates_allocation_is_refused_with_no_row_no_reservation_no_wake()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var m = org.Assign(Issue(allocation: 1.00m), DateTimeOffset.UtcNow);
        var issued = Assert.Single(new MissionEventStore(db).OfKind(MissionEventKind.Mandate)).Id;

        var first = Begin(db, Member, 0.60m, [issued]);
        Assert.True(first.Admitted, first.Refusal);
        Assert.Equal(m.Id, new AiAttemptStore(db).Get(first.Id!)!.MandateId);
        Assert.Equal(new MandateSpend(0m, 0.60m, 0, 1), org.SpendOf(m.Id));

        var again = MandateWake(db, m, 2);
        var reservedBefore = Count(db, "ai_attempt");
        var refused = Begin(db, Member, 0.60m, [again], id: "turn-over-the-cap");
        Assert.False(refused.Admitted);
        Assert.Equal(AiAdmission.Mandate, refused.Ceiling);
        Assert.Equal(m.Id, refused.MandateId);
        Assert.Contains("allocation is 1.00", refused.Refusal, StringComparison.Ordinal);
        Assert.Null(new AiAttemptStore(db).Get("turn-over-the-cap"));
        Assert.Equal(reservedBefore, Count(db, "ai_attempt"));
        Assert.False(new MissionEventStore(db).Get(again)!.Consumed);
        Assert.Equal(new MandateSpend(0m, 0.60m, 0, 1), org.SpendOf(m.Id));

        // A LAUNCH THAT NAMES THE MANDATE without a wake is gated the same way; one that fits is admitted.
        Assert.False(Begin(db, Member, 0.60m, mandate: m.Id).Admitted);
        Assert.True(Begin(db, Member, 0.40m, mandate: m.Id).Admitted);

        // NOT THE HOLDER, TWO MANDATES IN ONE LAUNCH, A CLOSED MANDATE: refused, nothing written.
        var second = org.Assign(Issue(holder: Other), DateTimeOffset.UtcNow);
        Assert.Contains("held by", Begin(db, Member, 0.01m, mandate: second.Id).Refusal, StringComparison.Ordinal);
        var otherWake = Assert.Single(new MissionEventStore(db).OfKind(MissionEventKind.Mandate), e => e.Payload == second.Id).Id;
        var third = org.Assign(Issue(holder: Other), DateTimeOffset.UtcNow);
        var thirdWake = Assert.Single(new MissionEventStore(db).OfKind(MissionEventKind.Mandate), e => e.Payload == third.Id).Id;
        Assert.Contains("a launch serves one", Begin(db, Other, 0.01m, [otherWake, thirdWake]).Refusal, StringComparison.Ordinal);
        // THE LOOP TAKES ONE MANDATE'S WAKES PER LAUNCH, the first due, and keeps every other wake.
        var due = new MissionEventStore(db).DueFor(Other, DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(2, due.Where(e => e.Kind == MissionEventKind.Mandate).Select(e => e.Payload).Distinct().Count());
        var taken = TradeAgent.AgentRuntime.MissionLoop.OneMandate(due);
        Assert.Single(taken.Where(e => e.Kind == MissionEventKind.Mandate).Select(e => e.Payload).Distinct());

        org.Cancel(second.Id, Head, null, "no longer needed", DateTimeOffset.UtcNow);
        Assert.Contains("closed mandate", Begin(db, Other, 0.01m, mandate: second.Id).Refusal, StringComparison.Ordinal);
        Assert.False(new MissionEventStore(db).Get(thirdWake)!.Consumed);

    }

    /// <summary>
    /// (b2) A LOST ATTEMPT KEEPS ITS RESERVATION AGAINST THE MANDATE: a holder's launch that never reported its usage is declared
    /// LOST, its reservation becomes its cost, and the mandate's measured spend counts it — so the next launch that would pass the
    /// allocation is refused. A resolved launch of unknown cost refuses the next one too: unknown is never zero. RED at base: no
    /// gate, and no spend read by mandate.
    /// </summary>
    [Fact]
    public void A_lost_attempt_keeps_its_reservation_against_the_mandate()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var m = org.Assign(Issue(allocation: 1.00m), DateTimeOffset.UtcNow);
        var issued = Assert.Single(new MissionEventStore(db).OfKind(MissionEventKind.Mandate)).Id;

        Assert.True(Begin(db, Member, 0.70m, [issued]).Admitted);
        Assert.Equal(1, new AiAttemptStore(db).LoseOpen(DateTimeOffset.UtcNow));
        Assert.Equal(new MandateSpend(0.70m, 0m, 0, 1), org.SpendOf(m.Id));

        var refused = Begin(db, Member, 0.40m, [MandateWake(db, m, 2)]);
        Assert.Equal(AiAdmission.Mandate, refused.Ceiling);
        Assert.True(Begin(db, Member, 0.30m, mandate: m.Id).Admitted);

        // UNKNOWN IS NEVER ZERO: a launch that ended reporting usage nobody could price, under another mandate.
        var n = org.Assign(Issue(allocation: 100m), DateTimeOffset.UtcNow);
        var unpriced = Begin(db, Member, 0m, mandate: n.Id);
        new AiAttemptStore(db).End(unpriced.Id!, 0, DateTimeOffset.UtcNow, 10, null, null, 10, null, "model-x", null, "no price", null);
        Assert.Equal(1, org.SpendOf(n.Id).Unknown);
        Assert.Contains("never read as zero", Begin(db, Member, 0.01m, mandate: n.Id).Refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// A LAUNCH ITS MANDATE REFUSED closes that mandate's wakes by no launch and tells the decider ONCE, in one write; the
    /// objectives say who owns what: the holder its open mandate, the decider its escalation.
    /// </summary>
    [Fact]
    public void A_refused_mandate_launch_closes_its_wakes_and_escalates_once()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var m = org.Assign(Issue(allocation: 1.00m), DateTimeOffset.UtcNow);
        var issued = Assert.Single(new MissionEventStore(db).OfKind(MissionEventKind.Mandate)).Id;
        Assert.Contains(new TradeAgent.AgentRuntime.RoleObjective(TradeAgent.AgentRuntime.RoleObjectives.MandateOwed, m.Id),
            TradeAgent.AgentRuntime.RoleObjectives.Open(db, Member, null));

        org.LaunchRefused(m.Id, [issued], "its allocation has no room", DateTimeOffset.UtcNow);
        org.LaunchRefused(m.Id, [issued], "its allocation has no room", DateTimeOffset.UtcNow);
        var closed = new MissionEventStore(db).Get(issued)!;
        Assert.Equal((true, MissionEventDisposition.Refused), (closed.Consumed, closed.Disposition));
        Assert.Equal(MandateEventKind.Escalated, org.MandateStatus(m.Id));
        var escalation = Assert.Single(new MissionEventStore(db).OfKind(MissionEventKind.Escalation));
        Assert.Equal((Head, m.Id), (escalation.Role, escalation.Payload));
        Assert.Contains(new TradeAgent.AgentRuntime.RoleObjective(TradeAgent.AgentRuntime.RoleObjectives.EscalationOpen, m.Id),
            TradeAgent.AgentRuntime.RoleObjectives.Open(db, Head, null));
    }

    // ---- item 4: decline and deliver ------------------------------------------------------------------------------

    /// <summary>Assigns through the pipe as <see cref="Head"/> on a brokered seat and answers the mandate's id.</summary>
    static async Task<string> Assigned(GatewayPipeServer server, Database db, string holder = Member, bool exact = false,
        decimal allocation = 5m, int retries = MandateLimits.DefaultRetryLimit, string kind = MandateKind.Task)
    {
        var reply = await server.CallAsync(AssignFrame(holder, kind, exact, allocation, retries), Head, Launch(db, Head));
        Assert.True(reply.Ok, Json.Write(reply.Error));
        return Body(reply).GetProperty("mandate").GetString()!;
    }

    static IpcRequest DeliverFrame(string mandate, string report, object? deviations = null, object? refs = null) =>
        Frame(Ops.MandateDeliver, [
            ("mandate", mandate), ("report", report),
            .. deviations is null ? Array.Empty<(string, object)>() : [("deviations", deviations)],
            .. refs is null ? Array.Empty<(string, object)>() : [("refs", refs)]
        ]);

    /// <summary>A strategy version the ledger recorded under <paramref name="attempt"/>, as only a test may write it.</summary>
    static void Version(Database db, string id, string? attempt) =>
        Exec(db, """
            INSERT INTO strategy_version(id, source, canonical, manifest, interpreter_build, parse_verdict, warm_up_bars, created_at, role, attempt)
            VALUES($id, 'x', 'x', '{}', 'test', 'ok', 0, $at, $role, $attempt)
            """, ("$id", id), ("$at", Stamp), ("$role", Member), ("$attempt", attempt));

    /// <summary>
    /// (c) A DELIVERY IS SEALED BY ITS MANIFEST HASH AND A SECOND IS A NEW DELIVERY: the holder's report goes into an immutable
    /// <c>org_delivery</c> whose id is the SHA-256 of the manifest the app wrote, each check is recorded in the same write, and the
    /// same words handed back again are a new delivery with its own id — never an edit. Only the holder delivers. RED at base: no
    /// <c>mandate-deliver</c> op existed.
    /// </summary>
    [Fact]
    public async Task A_delivery_is_sealed_by_its_manifest_hash_and_a_second_is_a_new_delivery()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        var m = await Assigned(server, db);

        // NOT THE HOLDER: the decider itself is refused, and nothing is sealed.
        Assert.Contains("only its holder", (await server.CallAsync(DeliverFrame(m, "done"), Head, Launch(db, Head))).Error!.Message,
            StringComparison.Ordinal);
        Assert.Equal(0L, Count(db, "org_delivery"));

        var first = await server.CallAsync(DeliverFrame(m, "the skew does not predict the next hour"), Member, Launch(db, Member, mandate: m));
        Assert.True(first.Ok, Json.Write(first.Error));
        var id = Body(first).GetProperty("delivery").GetString()!;
        var manifest = gw.Org.Manifest(id)!;
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant(), id);
        Assert.Contains("the skew does not predict the next hour", manifest, StringComparison.Ordinal);
        Assert.Equal(MandateCheck.All, gw.Org.Checks(id).Select(c => c.Check));
        Assert.All(gw.Org.Checks(id), c => Assert.True(c.Passed, c.Reason));

        var second = await server.CallAsync(DeliverFrame(m, "the skew does not predict the next hour"), Member, Launch(db, Member, mandate: m));
        Assert.True(second.Ok, Json.Write(second.Error));
        Assert.NotEqual(id, Body(second).GetProperty("delivery").GetString());
        Assert.Equal(2, gw.Org.Deliveries(m).Count);

        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Exec(db, "UPDATE org_delivery SET report='better'"));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Exec(db, "UPDATE org_check SET passed=1"));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Exec(db, "DELETE FROM org_delivery"));
        Assert.Equal("the skew does not predict the next hour", gw.Org.Delivery(id)!.Report);
    }

    /// <summary>
    /// (c2) AN EXACT MANDATE REFUSES A DELIVERY NAMING A DEVIATION (RULING Q7), with nothing written; the same deviation on an open
    /// mandate is sealed. RED at base: no op, and no latitude.
    /// </summary>
    [Fact]
    public async Task An_exact_mandate_refuses_a_delivery_naming_a_deviation()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        var deviation = new[] { new { what = "used 5-minute bars", found = "minute bars were missing for August", cost = "one extra backtest" } };

        var exact = await Assigned(server, db, exact: true);
        var refused = await server.CallAsync(DeliverFrame(exact, "done", deviation), Member, Launch(db, Member, mandate: exact));
        Assert.False(refused.Ok);
        Assert.Contains("is exact", refused.Error!.Message, StringComparison.Ordinal);
        Assert.Equal(0L, Count(db, "org_delivery"));
        Assert.Equal(0L, Count(db, "org_check"));
        Assert.Equal(MandateEventKind.Issued, gw.Org.MandateStatus(exact));

        // THE SAME DELIVERY WITHOUT IT IS SEALED; and on an open mandate the deviation is sealed verbatim, as a JSON string too.
        Assert.True((await server.CallAsync(DeliverFrame(exact, "done"), Member, Launch(db, Member, mandate: exact))).Ok);
        var open = await Assigned(server, db);
        var sealedOne = await server.CallAsync(DeliverFrame(open, "done", JsonSerializer.Serialize(deviation)), Member, Launch(db, Member, mandate: open));
        Assert.True(sealedOne.Ok, Json.Write(sealedOne.Error));
        var d = gw.Org.Delivery(Body(sealedOne).GetProperty("delivery").GetString()!)!;
        Assert.Equal(new MandateDeviation("used 5-minute bars", "minute bars were missing for August", "one extra backtest"), Assert.Single(d.Deviations));

        // A DEVIATION WITH A FIELD IT DOES NOT TAKE, OR MISSING ONE, is refused, never cut or filled.
        Assert.False((await server.CallAsync(DeliverFrame(open, "x", new[] { new { what = "a", found = "b" } }), Member, Launch(db, Member, mandate: open))).Ok);
        Assert.False((await server.CallAsync(DeliverFrame(open, "x", new[] { new { what = "a", found = "b", cost = "c", measured = "0" } }), Member, Launch(db, Member, mandate: open))).Ok);
    }

    /// <summary>
    /// (c3) A REF NOT PRODUCED UNDER THIS MANDATE FAILS ITS CHECK: a version the holder made under a launch charged to this mandate
    /// passes <c>refs-of-this-mandate</c>; one made under another launch — the holder's own, uncharged — fails it in words naming
    /// it; one that does not exist fails <c>refs-exist</c>. The checks are the app's, never the claim's. RED at base: no checks.
    /// </summary>
    [Fact]
    public async Task A_ref_not_produced_under_this_mandate_fails_its_check()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        var m = await Assigned(server, db);
        var charged = Launch(db, Member, mandate: m);
        var uncharged = Launch(db, Member);
        Version(db, "ver-charged", charged);
        Version(db, "ver-elsewhere", uncharged);

        async Task<IReadOnlyList<CheckRow>> Checked(params string[] versions)
        {
            var reply = await server.CallAsync(DeliverFrame(m, "a version", refs: versions.Select(v => new { kind = "version", id = v }).ToArray()),
                Member, charged);
            Assert.True(reply.Ok, Json.Write(reply.Error));
            return gw.Org.Checks(Body(reply).GetProperty("delivery").GetString()!);
        }

        var good = await Checked("ver-charged");
        Assert.True(good.Single(c => c.Check == MandateCheck.RefsOfThisMandate).Passed);

        var foreign = (await Checked("ver-charged", "ver-elsewhere")).Single(c => c.Check == MandateCheck.RefsOfThisMandate);
        Assert.False(foreign.Passed);
        Assert.Contains("version ver-elsewhere", foreign.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("ver-charged", foreign.Reason, StringComparison.Ordinal);

        var missing = await Checked("ver-nowhere");
        Assert.False(missing.Single(c => c.Check == MandateCheck.RefsExist).Passed);
        Assert.False(missing.Single(c => c.Check == MandateCheck.RefsOfThisMandate).Passed);

        // AN UNKNOWN REF KIND is refused at the seal; a too-long report is sealed and fails its size check.
        Assert.False((await server.CallAsync(DeliverFrame(m, "x", refs: new[] { new { kind = "file", id = "out/x.md" } }), Member, charged)).Ok);
        var tall = await server.CallAsync(DeliverFrame(m, string.Join("\n", Enumerable.Range(1, MandateLimits.ReportLines + 1))), Member, charged);
        Assert.False(gw.Org.Checks(Body(tall).GetProperty("delivery").GetString()!).Single(c => c.Check == MandateCheck.ReportSize).Passed);
    }

    /// <summary>A HOLDER DECLINES: the event, its wakes closed, and the decider told once — retries stop.</summary>
    [Fact]
    public async Task A_decline_stops_retries_and_tells_the_decider()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        var m = await Assigned(server, db);

        Assert.False((await server.CallAsync(Frame(Ops.MandateDecline, ("mandate", m), ("reason", "boring"), ("why", "x")), Member, Launch(db, Member))).Ok);
        var declined = await server.CallAsync(Frame(Ops.MandateDecline, ("mandate", m), ("reason", MandateDecline.Ambiguous),
            ("why", "'the next hour' could mean the funding hour or the clock hour")), Member, Launch(db, Member));
        Assert.True(declined.Ok, Json.Write(declined.Error));
        Assert.Equal([MandateEventKind.Issued, MandateEventKind.Declined, MandateEventKind.Escalated], gw.Org.MandateEvents(m).Select(e => e.Kind));
        Assert.All(new MissionEventStore(db).OfKind(MissionEventKind.Mandate), w => Assert.True(w.Consumed));
        Assert.Equal(Head, Assert.Single(new MissionEventStore(db).OfKind(MissionEventKind.Escalation)).Role);
    }

    // ---- item 5: triage, retry, escalation, judgment ------------------------------------------------------------------

    /// <summary>A report one line too long for its size check: a clean failure when it names no deviation.</summary>
    static readonly string TooLong = string.Join("\n", Enumerable.Range(1, MandateLimits.ReportLines + 1).Select(i => $"line {i}"));

    static List<MissionEvent> Wakes(Database db, string kind, string role) =>
        [.. new MissionEventStore(db).OfKind(kind).Where(e => e.Role == role)];

    /// <summary>
    /// (d) A CLEAN FAILURE RETRIES FRESH WITHOUT WAKING THE HEAD: a delivery that fails a check and names no deviation is code's —
    /// a <c>retry</c> event and ONE new <c>mandate</c> wake to the holder, in the delivery's own write; the decider gets no
    /// delivery and no escalation wake. RED at base: no triage. Watched red with triage waking the head on a clean failure.
    /// </summary>
    [Fact]
    public async Task A_clean_failure_retries_fresh_without_waking_the_head()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        var m = await Assigned(server, db, retries: 3);

        var reply = await server.CallAsync(DeliverFrame(m, TooLong), Member, Launch(db, Member, mandate: m));
        Assert.True(reply.Ok, Json.Write(reply.Error));
        Assert.Equal(MandateEventKind.Retry, Body(reply).GetProperty("status").GetString());
        Assert.Equal([MandateEventKind.Issued, MandateEventKind.Delivered, MandateEventKind.Retry], gw.Org.MandateEvents(m).Select(e => e.Kind));
        Assert.Equal(2, Wakes(db, MissionEventKind.Mandate, Member).Count);
        Assert.Empty(new MissionEventStore(db).OfKind(MissionEventKind.Delivery));
        Assert.Empty(new MissionEventStore(db).OfKind(MissionEventKind.Escalation));
        Assert.Empty(new MissionEventStore(db).DueFor(Head, DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    /// <summary>
    /// (d2) THE LIMITING FAILURE ESCALATES TO THE DECIDER IN THE SAME WRITE: with a retry limit of one, the first clean failure
    /// writes the delivery, its checks, the <c>delivered</c> and <c>escalated</c> events and ONE <c>escalation</c> wake together —
    /// and when that wake cannot be written, NOTHING of the delivery is: no half state, no failure counted, no retry. RED at base:
    /// no escalation. Watched red with the escalation written in a second write after the failure's.
    /// </summary>
    [Fact]
    public async Task The_limiting_failure_escalates_to_the_decider_in_the_same_write()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        var m = await Assigned(server, db, retries: 1);
        var launch = Launch(db, Member, mandate: m);

        // KILL THE WRITE BETWEEN THEM: the escalation's wake cannot be written.
        Exec(db, """
            CREATE TRIGGER abort_escalation BEFORE INSERT ON mission_event WHEN NEW.kind = 'escalation'
            BEGIN SELECT RAISE(ABORT, 'the escalation could not be written'); END
            """);
        Assert.False((await server.CallAsync(DeliverFrame(m, TooLong), Member, launch)).Ok);
        Assert.Equal(0L, Count(db, "org_delivery"));
        Assert.Equal(0L, Count(db, "org_check"));
        Assert.Equal([MandateEventKind.Issued], gw.Org.MandateEvents(m).Select(e => e.Kind));
        Assert.Single(Wakes(db, MissionEventKind.Mandate, Member));
        Exec(db, "DROP TRIGGER abort_escalation");

        var reply = await server.CallAsync(DeliverFrame(m, TooLong), Member, launch);
        Assert.True(reply.Ok, Json.Write(reply.Error));
        Assert.Equal([MandateEventKind.Issued, MandateEventKind.Delivered, MandateEventKind.Escalated], gw.Org.MandateEvents(m).Select(e => e.Kind));
        Assert.Contains("retry limit of 1", gw.Org.MandateEvents(m)[^1].Reason, StringComparison.Ordinal);
        var escalation = Assert.Single(new MissionEventStore(db).OfKind(MissionEventKind.Escalation));
        Assert.Equal((Head, m), (escalation.Role, escalation.Payload));
        Assert.Single(Wakes(db, MissionEventKind.Mandate, Member));

        // RETRIES HAVE STOPPED: a later clean failure goes to the decider, never back to the holder.
        Assert.True((await server.CallAsync(DeliverFrame(m, TooLong), Member, launch)).Ok);
        Assert.Single(Wakes(db, MissionEventKind.Mandate, Member));
        Assert.Single(Wakes(db, MissionEventKind.Delivery, Head));
    }

    /// <summary>
    /// (d3) A NAMED DEVIATION REACHES THE HEAD EVEN WHEN CHECKS FAIL: a delivery that fails its size check but names a deviation is
    /// the decider's to judge — ONE <c>delivery</c> wake to it, no retry. RED at base: no triage.
    /// </summary>
    [Fact]
    public async Task A_named_deviation_reaches_the_head_even_when_checks_fail()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        var m = await Assigned(server, db);

        var reply = await server.CallAsync(DeliverFrame(m, TooLong,
            new[] { new { what = "wrote a longer report", found = "the result needs the table", cost = "none" } }), Member, Launch(db, Member, mandate: m));
        Assert.True(reply.Ok, Json.Write(reply.Error));
        Assert.Equal(MandateEventKind.Delivered, gw.Org.MandateStatus(m));
        Assert.Contains(gw.Org.Checks(Body(reply).GetProperty("delivery").GetString()!), c => !c.Passed);
        var wake = Assert.Single(Wakes(db, MissionEventKind.Delivery, Head));
        Assert.Equal(m, wake.Payload);
        Assert.Single(Wakes(db, MissionEventKind.Mandate, Member));
        Assert.Contains(new TradeAgent.AgentRuntime.RoleObjective(TradeAgent.AgentRuntime.RoleObjectives.DeliveryToJudge, m),
            TradeAgent.AgentRuntime.RoleObjectives.Open(db, Head, null));
    }

    /// <summary>
    /// (e) A REJECTION CITES A CHECK OR A DEFECT AND ACCEPTANCE BINDS THE CHECKED DELIVERY: a rejection with neither, with both, or
    /// citing a check that PASSED is refused; one citing a failed check, or a stated defect, is recorded and wakes the holder
    /// again; an acceptance of a delivery that is no longer the latest is refused, and of the latest closes the mandate — and only
    /// the decider judges. RED at base: no verbs.
    /// </summary>
    [Fact]
    public async Task A_rejection_cites_a_check_or_a_defect_and_acceptance_binds_the_checked_delivery()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        var m = await Assigned(server, db);
        var work = Launch(db, Member, mandate: m);

        var deviated = new[] { new { what = "shorter window", found = "August only", cost = "none" } };
        var first = Body(await server.CallAsync(DeliverFrame(m, TooLong, deviated), Member, work)).GetProperty("delivery").GetString()!;
        async Task<IpcResponse> Judge(string op, params (string, object)[] args) =>
            await server.CallAsync(Frame(op, [("delivery", first), .. args]), Head, Launch(db, Head));

        Assert.Contains("exactly one thing", (await Judge(Ops.MandateReject)).Error!.Message, StringComparison.Ordinal);
        Assert.Contains("exactly one thing", (await Judge(Ops.MandateReject, ("check", MandateCheck.ReportSize), ("defect", "x"))).Error!.Message, StringComparison.Ordinal);
        Assert.Contains("PASSED", (await Judge(Ops.MandateReject, ("check", MandateCheck.RefsExist))).Error!.Message, StringComparison.Ordinal);
        Assert.False((await server.CallAsync(Frame(Ops.MandateReject, ("delivery", first), ("defect", "x")), Member, work)).Ok);
        Assert.Equal(MandateEventKind.Delivered, gw.Org.MandateStatus(m));

        Assert.True((await Judge(Ops.MandateReject, ("check", MandateCheck.ReportSize))).Ok);
        Assert.Equal(MandateEventKind.Rejected, gw.Org.MandateStatus(m));
        Assert.Equal("report-size failed: the report is 21 lines; a report holds at most 20", gw.Org.MandateEvents(m)[^1].Reason);
        Assert.Equal(2, Wakes(db, MissionEventKind.Mandate, Member).Count);

        // A SECOND DELIVERY: the first can no longer be judged; the stated defect is the decider's words, verbatim.
        var second = Body(await server.CallAsync(DeliverFrame(m, "short now"), Member, work)).GetProperty("delivery").GetString()!;
        Assert.Contains("no longer the latest", (await Judge(Ops.MandateAccept)).Error!.Message, StringComparison.Ordinal);
        Assert.True((await server.CallAsync(Frame(Ops.MandateReject, ("delivery", second), ("defect", "it answers a different question")),
            Head, Launch(db, Head))).Ok);
        Assert.Equal("it answers a different question", gw.Org.MandateEvents(m)[^1].Reason);

        var third = Body(await server.CallAsync(DeliverFrame(m, "the answer"), Member, work)).GetProperty("delivery").GetString()!;
        Assert.False((await server.CallAsync(Frame(Ops.MandateAccept, ("delivery", third)), Member, work)).Ok);
        var accepted = await server.CallAsync(Frame(Ops.MandateAccept, ("delivery", third)), Head, Launch(db, Head));
        Assert.True(accepted.Ok, Json.Write(accepted.Error));
        Assert.Equal(MandateEventKind.Accepted, gw.Org.MandateStatus(m));
        Assert.Equal([third], gw.Org.MandateEvents(m)[^1].Evidence);
        Assert.Contains("closed mandate", Begin(db, Member, 0.01m, mandate: m).Refusal, StringComparison.Ordinal);
        Assert.False((await server.CallAsync(DeliverFrame(m, "more"), Member, work)).Ok);
        Assert.All(Wakes(db, MissionEventKind.Mandate, Member), w => Assert.True(w.Consumed));
    }

    /// <summary>
    /// (g) A RESTART RERAISES EACH OPEN MANDATE ONCE (ORCHESTRATION § 4 flow 3): a holder's launch that a restart declared LOST
    /// leaves its mandate with no untaken wake and no open launch, and the sweep raises ONE wake for it — never for a mandate whose
    /// wake is still untaken or whose launch still runs — and a second sweep raises nothing. The same sweep expires a mandate past
    /// its deadline, once, telling its decider, and escalates one half spent, once. RED at base: no sweep.
    /// </summary>
    [Fact]
    public void A_restart_reraises_each_open_mandate_once()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var events = new MissionEventStore(db);
        var now = DateTimeOffset.UtcNow;
        var lost = org.Assign(Issue(allocation: 50m), now);
        var waiting = org.Assign(Issue(holder: Other, allocation: 50m), now);
        var running = org.Assign(Issue(allocation: 50m), now);

        var lostWake = Assert.Single(events.OfKind(MissionEventKind.Mandate), e => e.Payload == lost.Id).Id;
        Assert.True(Begin(db, Member, 0.10m, [lostWake]).Admitted);
        Assert.Equal(1, new AiAttemptStore(db).LoseOpen(now));
        var runningWake = Assert.Single(events.OfKind(MissionEventKind.Mandate), e => e.Payload == running.Id).Id;
        Assert.True(Begin(db, Member, 0.10m, [runningWake]).Admitted);

        Assert.Equal(1, org.Sweep(now));
        Assert.Equal(2, events.OfKind(MissionEventKind.Mandate).Count(e => e.Payload == lost.Id));
        Assert.Single(events.OfKind(MissionEventKind.Mandate), e => e.Payload == waiting.Id);
        Assert.Single(events.OfKind(MissionEventKind.Mandate), e => e.Payload == running.Id);
        Assert.Equal(0, org.Sweep(now));
        Assert.Equal(2, events.OfKind(MissionEventKind.Mandate).Count(e => e.Payload == lost.Id));

        // HALF SPENT, NOTHING ACCEPTED: escalated once, never re-raised.
        var costly = org.Assign(Issue(allocation: 1m), now);
        var costlyWake = Assert.Single(events.OfKind(MissionEventKind.Mandate), e => e.Payload == costly.Id).Id;
        Assert.False(Begin(db, Other, 0.60m, [costlyWake]).Admitted);
        Assert.True(Begin(db, Member, 0.60m, [costlyWake]).Admitted);
        new AiAttemptStore(db).LoseOpen(now);
        Assert.True(org.Sweep(now) >= 1);
        Assert.Equal(MandateEventKind.Escalated, org.MandateStatus(costly.Id));
        Assert.StartsWith(OrgStore.HalfSpent, org.MandateEvents(costly.Id)[^1].Reason, StringComparison.Ordinal);
        Assert.Single(events.OfKind(MissionEventKind.Escalation), e => e.Payload == costly.Id);
        org.Sweep(now);
        Assert.Single(events.OfKind(MissionEventKind.Escalation), e => e.Payload == costly.Id);

        // PAST ITS DEADLINE: expired once, the holder's untaken wake closed, the decider told; nothing more after it.
        var later = Due.AddMinutes(1);
        org.Sweep(later);
        Assert.Equal(MandateEventKind.Expired, org.MandateStatus(waiting.Id));
        Assert.All(events.OfKind(MissionEventKind.Mandate).Where(e => e.Payload == waiting.Id), e => Assert.True(e.Consumed));
        Assert.Single(events.OfKind(MissionEventKind.Escalation), e => e.Payload == waiting.Id);
        Assert.Contains(new TradeAgent.AgentRuntime.RoleObjective(TradeAgent.AgentRuntime.RoleObjectives.MandateExpired, waiting.Id),
            TradeAgent.AgentRuntime.RoleObjectives.Open(db, Head, null));
        Assert.Equal(0, org.Sweep(later));
    }

    // ---- item 6: the reads ---------------------------------------------------------------------------------------------

    /// <summary>
    /// (h) A DEVIATION'S STATED COST IS SHOWN BESIDE THE MEASURED SPEND, NEVER INSTEAD: <c>mandate-show</c> gives the delivery
    /// verbatim, each check, and each deviation's cost as the holder STATED it, under its own name, beside the spend the app
    /// MEASURED from the launches charged to the mandate — two fields, neither standing in for the other. A position reads only
    /// the mandates it holds or decides. RED at base: no read.
    /// </summary>
    [Fact]
    public async Task A_deviations_stated_cost_is_shown_beside_the_measured_spend_never_instead()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        var m = await Assigned(server, db);
        var work = Launch(db, Member, reserved: 0.50m, mandate: m);
        new AiAttemptStore(db).End(work, 0, DateTimeOffset.UtcNow, 1000, null, null, 100, null, "model-x", 0.30m, null, null);

        const string stated = "three extra backtests, about $0.90";
        var delivered = await server.CallAsync(DeliverFrame(m, "the skew predicts nothing",
            new[] { new { what = "added two months", found = "August alone was too short", cost = stated } }), Member, Launch(db, Member, mandate: m));
        Assert.True(delivered.Ok, Json.Write(delivered.Error));

        var show = await server.CallAsync(Frame(Ops.MandateShow, ("mandate", m)), Head, Launch(db, Head));
        Assert.True(show.Ok, Json.Write(show.Error));
        var body = Body(show);
        var delivery = Assert.Single(body.GetProperty("deliveries").EnumerateArray());
        Assert.Equal("the skew predicts nothing", delivery.GetProperty("report").GetString());
        var deviation = Assert.Single(delivery.GetProperty("deviations").EnumerateArray());
        Assert.Equal(stated, deviation.GetProperty("stated_cost").GetString());
        Assert.False(deviation.TryGetProperty("cost", out _));
        // THE MEASURED SPEND: the ended launch's 0.30 and the open one's reservation — the app's numbers, never the claim.
        Assert.Equal("0.3", deviation.GetProperty("measured_spend").GetProperty("spent").GetString());
        Assert.Equal("0.1", deviation.GetProperty("measured_spend").GetProperty("reserved").GetString());
        Assert.Equal(deviation.GetProperty("measured_spend").GetRawText(), body.GetProperty("measured_spend").GetRawText());
        Assert.Equal(MandateCheck.All, delivery.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("check").GetString()));
        Assert.Equal([MandateEventKind.Issued, MandateEventKind.Delivered],
            body.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("kind").GetString()));

        // THE HOLDER READS IT TOO, AND LISTS IT; ANYONE ELSE IS REFUSED — another member, the outsider.
        Assert.True((await server.CallAsync(Frame(Ops.MandateShow, ("mandate", m)), Member, Launch(db, Member))).Ok);
        var listed = Body(await server.CallAsync(Frame(Ops.MandateList), Member, Launch(db, Member))).GetProperty("mandates");
        Assert.Equal("holder", Assert.Single(listed.EnumerateArray()).GetProperty("you_are").GetString());
        foreach (var stranger in new[] { Other, Outsider })
        {
            Assert.False((await server.CallAsync(Frame(Ops.MandateShow, ("mandate", m)), stranger, Launch(db, stranger))).Ok);
            Assert.Empty(Body(await server.CallAsync(Frame(Ops.MandateList), stranger, Launch(db, stranger))).GetProperty("mandates").EnumerateArray());
        }
        // AND THE LEGACY PAIR READ NONE OF IT on an unconfined seat.
        Assert.False((await server.CallAsync(Frame(Ops.MandateList), CouncilRoles.Research, Launch(db, CouncilRoles.Research, SeatClasses.Cli))).Ok);
    }

    /// <summary>THE RELAY'S WORDS: a staged file stays held, and the hold says a position's work reaches its issuer through mandate-deliver.</summary>
    [Fact]
    public void A_held_output_says_its_work_reaches_the_issuer_through_mandate_deliver()
    {
        Assert.Contains($"'{Ops.MandateDeliver}'", TradeAgent.AgentRuntime.CouncilRelay.HeldWhy, StringComparison.Ordinal);
        Assert.Contains("stays held", TradeAgent.AgentRuntime.CouncilRelay.HeldWhy, StringComparison.Ordinal);
        Assert.Empty(TradeAgent.AgentRuntime.CouncilRelay.RecipientsOf(Member));
        Assert.Equal(MandateLimits.ReportLines, TradeAgent.AgentRuntime.CouncilRelay.ReportLines);
    }
}

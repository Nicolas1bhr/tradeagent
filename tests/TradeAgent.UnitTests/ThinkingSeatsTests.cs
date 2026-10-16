using System.Globalization;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE ACTIVATION GATE (<c>U-thinking-seats-a</c>; <c>docs/ORCHESTRATION.md</c> § 2.3, § 2.5): each capability has a minimum seat
/// assurance, checked when a position opens and again, fail closed, at admission and immediately before the process or the request;
/// no position beyond the legacy pair runs on a real-model seat until the host records a proven executor cell; no unconfined
/// neighbour; every refusal says its kind as data and none consumes a wake or the owner's words. Each position here is written into
/// the chart by the TEST with raw SQL, as no writer of the chart exists yet — and so is a proof, which only the app may write.
///
/// <para>Shares the vendor-override collection because (g) writes <c>runtimes.json</c> under the shared test home.</para>
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class ThinkingSeatsTests : IDisposable
{
    const string Third = "pos-7f3a90c1";
    const string Second = "pos-2b8e41d0";

    public void Dispose()
    {
        if (File.Exists(RuntimeCatalog.OverridePath)) File.Delete(RuntimeCatalog.OverridePath);
    }

    // ---- (a) code needs a cell --------------------------------------------------------------------------------------

    /// <summary>
    /// (a) A POSITION ON A CLI SEAT IS REFUSED: its shell is code, and code needs a confined cell — whatever the position asked for,
    /// and even on a host with a proven cell. A refusal about the position. RED at base: the meter admitted any active position.
    /// </summary>
    [Fact]
    public void A_position_on_a_cli_seat_is_refused_because_its_shell_is_code_and_code_needs_a_cell()
    {
        using var db = TestEnv.NewDb();
        Seat(db, Third);
        ProveCell(db);
        var meter = Meter(db, _ => SeatClasses.Cli);
        using var seat = meter.Attach(new Seated(SeatClasses.Cli), Third);

        var refused = meter.Begin("## Situation", role: Third);
        Assert.False(refused.Admitted, "a position beyond the legacy pair was admitted on a CLI seat");
        Assert.Equal(RefusalKind.Position, refused.Kind);
        Assert.Contains("code needs a confined cell", refused.Refusal, StringComparison.Ordinal);
        Assert.Equal(0, Attempts(db));

        // THE SEAM SAYS THE SAME WITH THE DECLARED SEAT, BEFORE ANY CONVERSATION IS OPENED.
        var declared = PositionActivation.Decide(Third, new OrgStore(db).Standing(Third), Facts(db, SeatClasses.Cli));
        Assert.Equal(RefusalKind.Position, declared?.Kind);
        Assert.Equal(Capability.Code, CapabilityPolicy.Binding(
            CapabilityPolicy.CapabilitiesOf(new OrgStore(db).Standing(Third)!, SeatClasses.Cli), SeatAssurance.Unconfined));
    }

    // ---- (b) the owner's answer, literally -------------------------------------------------------------------------

    /// <summary>
    /// (b) A HEAD ON THE HARNESS IS REFUSED UNTIL THE HOST RECORDS A PROVEN EXECUTOR CELL — though a brokered seat meets every
    /// capability minimum it has (the owner's answer, ORCHESTRATION § 2.3). A refusal about the host. A proof recorded for another
    /// platform or another build is not one here. RED at base: the meter admitted the head.
    /// </summary>
    [Fact]
    public void A_head_on_the_harness_is_refused_until_the_host_records_a_proven_executor_cell()
    {
        using var db = TestEnv.NewDb();
        Head(db, Third);
        var meter = Meter(db, _ => SeatClasses.Harness);
        using var seat = meter.Attach(new Seated(SeatClasses.Harness), Third);

        Assert.Equal(OrgPositionKind.Head, new OrgStore(db).Standing(Third)!.Kind);
        Assert.Null(CapabilityPolicy.Binding(
            CapabilityPolicy.CapabilitiesOf(new OrgStore(db).Standing(Third)!, SeatClasses.Harness), SeatAssurance.Brokered));

        var refused = meter.Begin("## Situation", role: Third);
        Assert.False(refused.Admitted, "a head ran on a real-model seat with no proven cell recorded");
        Assert.Equal(RefusalKind.HostOrLaunch, refused.Kind);
        Assert.Contains("no proven executor cell", refused.Refusal, StringComparison.Ordinal);

        ProveCell(db, platform: "elsewhere-x64");
        ProveCell(db, build: "0.0.1-another-build");
        Assert.False(meter.Begin("## Situation", role: Third).Admitted, "a proof of another platform or build counted here");
        Assert.Equal(0, Attempts(db));
    }

    /// <summary>
    /// (b) WITH A PROVEN CELL RECORDED, A HEAD ON THE HARNESS PASSES AND ITS ROW RECORDS ITS SEAT: harness, brokered. RED at base:
    /// the row recorded no seat.
    /// </summary>
    [Fact]
    public void With_a_proven_cell_recorded_a_head_on_the_harness_passes_and_records_brokered()
    {
        using var db = TestEnv.NewDb();
        Head(db, Third);
        ProveCell(db);
        var meter = Meter(db, _ => SeatClasses.Harness);
        using var seat = meter.Attach(new Seated(SeatClasses.Harness), Third);

        var admitted = meter.Begin("## Situation", role: Third);
        Assert.True(admitted.Admitted, admitted.Refusal);
        var row = new AiAttemptStore(db).Get(admitted.Id!)!;
        Assert.Equal(Third, row.Role);
        Assert.Equal(SeatClasses.Harness, row.SeatClass);
        Assert.Equal("brokered", row.Assurance);
        Assert.Single(new CellProofStore(db).Proven());
    }

    // ---- (c) the launch-time half ---------------------------------------------------------------------------------

    /// <summary>
    /// (c) THE LAUNCH-TIME CHECK REFUSES A SEAT WEAKER THAN THE ONE DECLARED, with no row, no reservation and no wake consumed — and
    /// on a host with no meter, the session refuses before its process starts and the harness before its request. THE WATCHED
    /// MUTANT: <c>Begin</c>'s gate removed (the seam kept) admits it. RED at base.
    /// </summary>
    [Fact]
    public async Task The_launch_time_check_refuses_a_seat_weaker_than_the_one_declared_with_no_row_no_reservation_no_wake()
    {
        using var db = TestEnv.NewDb();
        Head(db, Third);
        ProveCell(db);
        var wakes = new MissionEventStore(db);
        var wake = Wake(wakes, Third);
        var meter = Meter(db, _ => SeatClasses.Harness);
        using var seat = meter.Attach(new Seated(SeatClasses.Cli), Third);   // declared the harness, about to start a shell

        // THE SEAM, WHICH SEES ONLY THE DECLARED SEAT, LETS IT THROUGH; THE ADMISSION, WHICH SEES THE VERIFIED ONE, DOES NOT.
        Assert.Null(PositionActivation.Decide(Third, new OrgStore(db).Standing(Third), Facts(db, SeatClasses.Harness)));
        var refused = meter.Begin("## Situation", consuming: [wake], role: Third);
        Assert.False(refused.Admitted, "a launch on a seat weaker than the one declared was admitted");
        Assert.Equal(RefusalKind.HostOrLaunch, refused.Kind);
        Assert.Equal(0, Attempts(db));
        Assert.Equal(0m, meter.Today.Reserved);
        Assert.False(wakes.Get(wake)!.Consumed);
        Assert.Null(meter.OpenAttemptIdFor(Third));

        // A HOST WITH NO METER: the CLI session refuses after verifying its seat, before any process starts.
        var (codex, exe, dump) = SeatHomeTests.Seat(nameof(The_launch_time_check_refuses_a_seat_weaker_than_the_one_declared_with_no_row_no_reservation_no_wake));
        var register = Register(db, SeatClasses.Harness);
        var session = new AgentSession(codex, () => exe, () => Path.GetDirectoryName(exe)!, () => new Dictionary<string, string>(),
            presence: new AgentPresence(), role: Third, seatProbe: new SeatHomeTests.FakeProbe(), seats: register);
        await SeatHomeTests.Turn(session);
        Assert.DoesNotContain(SeatHomeTests.Argv(dump), l => l.StartsWith("exec", StringComparison.Ordinal));
        Assert.Contains(session.History, t => t.Role == ChatRole.System && t.Text.Contains($"'{Third}'", StringComparison.Ordinal));

        // AND THE HARNESS, ON A HOST WITH NO PROVEN CELL, SENDS NO REQUEST.
        using var noCell = TestEnv.NewDb();
        Head(noCell, Third);
        using var provider = new FakeProvider();
        provider.Answer(FakeProvider.Message("should never be asked for"));
        var harness = HarnessFor(provider, Register(noCell, SeatClasses.Harness));
        await harness.SendMissionAsync("## Situation");
        Assert.Empty(provider.Requests);
        Assert.Contains(harness.History, t => t.Role == ChatRole.System
                                              && t.Text.Contains("no proven executor cell", StringComparison.Ordinal));
    }

    // ---- (e) no unconfined neighbour ------------------------------------------------------------------------------

    /// <summary>
    /// (e) WHILE A POSITION BEYOND THE LEGACY PAIR IS ACTIVE, AN UNCONFINED LAUNCH AND THE OWNER'S CHAT ARE REFUSED, in words naming
    /// the position and the repair — "the legacy pair run confined too, or not there" — and the owner's words are held, never lost.
    /// And the other half: a position beyond the pair is refused while an unconfined launch is open. RED at base.
    /// </summary>
    [Fact]
    public async Task While_a_position_beyond_the_legacy_pair_is_active_an_unconfined_launch_and_the_owners_chat_are_refused()
    {
        using var db = TestEnv.NewDb();
        Head(db, Third);
        ProveCell(db);
        var meter = Meter(db, role => CouncilRoles.IsKnown(role) ? SeatClasses.Cli : SeatClasses.Harness);

        var (codex, exe, dump) = SeatHomeTests.Seat(nameof(While_a_position_beyond_the_legacy_pair_is_active_an_unconfined_launch_and_the_owners_chat_are_refused));
        var chat = new AgentSession(codex, () => exe, () => Path.GetDirectoryName(exe)!, () => new Dictionary<string, string>(),
            presence: new AgentPresence(), role: CouncilRoles.Operations, seatProbe: new SeatHomeTests.FakeProbe());
        using (meter.Attach(chat, CouncilRoles.Operations))
        {
            await chat.SendAsync("how are we doing?");
            Assert.Equal(0, Attempts(db));
            Assert.DoesNotContain(SeatHomeTests.Argv(dump), l => l.StartsWith("exec", StringComparison.Ordinal));
            var said = chat.History.Last(t => t.Role == ChatRole.System).Text;
            Assert.Contains($"'{Third}'", said, StringComparison.Ordinal);
            Assert.Contains("confined too, or not there", said, StringComparison.Ordinal);
            Assert.Contains("how are we doing?", chat.TakeTyped());   // held, never consumed

            var research = meter.Begin("## Situation", role: CouncilRoles.Research);
            Assert.False(research.Admitted, "Research ran unconfined beside an active position");
            Assert.Equal(RefusalKind.HostOrLaunch, research.Kind);
        }

        // THE OTHER HALF: the legacy pair alone, the chair's unconfined launch open — then the position, back, waits for it.
        SetStatus(db, Third, OrgPositionStatus.Dormant);
        using var chair = meter.Attach(new Seated(SeatClasses.Cli), CouncilRoles.Operations);
        Assert.True(meter.Begin("## Situation", role: CouncilRoles.Operations).Admitted);
        SetStatus(db, Third, OrgPositionStatus.Active);
        using var head = meter.Attach(new Seated(SeatClasses.Harness), Third);
        var waits = meter.Begin("## Situation", role: Third);
        Assert.False(waits.Admitted, "a position ran beside an open unconfined launch");
        Assert.Equal(RefusalKind.HostOrLaunch, waits.Kind);
        Assert.Contains($"'{CouncilRoles.Operations}'", waits.Refusal, StringComparison.Ordinal);
        Assert.Equal([CouncilRoles.Operations], meter.OpenUnconfined(Third));
    }

    // ---- (f) fail closed -------------------------------------------------------------------------------------------

    /// <summary>
    /// (f) A SEAM WHOSE REGISTER IS UNSET — or answers null, or throws — REFUSES EVERY POSITION BUT THE LEGACY PAIR, as a refusal
    /// about the host, at the seam and at launch alike. The behaviour is base's (it refused every position beyond the pair); the
    /// kind is new.
    /// </summary>
    [Fact]
    public void A_seam_whose_register_is_unset_refuses_every_position_but_the_legacy_pair()
    {
        using var db = TestEnv.NewDb();
        Head(db, Third);
        ProveCell(db);
        var chart = new OrgStore(db);

        var unset = new SeatRegister();
        var blank = new SeatRegister { Facts = _ => null };
        var broken = new SeatRegister { Facts = _ => throw new InvalidOperationException("the chart is on fire") };
        foreach (var register in new[] { unset, blank, broken })
        {
            var refused = PositionActivation.Decide(Third, chart.Standing(Third), register.Read(Third));
            Assert.Equal(RefusalKind.HostOrLaunch, refused?.Kind);
            Assert.Equal(RefusalKind.HostOrLaunch, PositionActivation.AtLaunch(Third, SeatClasses.Harness, register)?.Kind);
            foreach (var legacy in CouncilRoles.All)
            {
                Assert.Null(PositionActivation.Decide(legacy, chart.Standing(legacy), register.Read(legacy)));
                Assert.Null(PositionActivation.AtLaunch(legacy, SeatClasses.Cli, register));
            }
        }

        // THE PROCESS-WIDE REGISTER, WHICH NOTHING IN A TEST PROCESS SETS.
        Assert.NotNull(PositionActivation.Refusal(Third, chart.Standing(Third)));
        Assert.Null(PositionActivation.Refusal(CouncilRoles.Operations, chart.Standing(CouncilRoles.Operations)));

        // AND A METER WITH NO DECLARED SEAT IS THE SAME UNSET REGISTER.
        var meter = new TurnMeter(db, () => 5m, live: new LiveAttempts());
        using var seat = meter.Attach(new Seated(SeatClasses.Harness), Third);
        Assert.Equal(RefusalKind.HostOrLaunch, meter.Begin("## Situation", role: Third).Kind);
    }

    // ---- (g) code, never data --------------------------------------------------------------------------------------

    /// <summary>
    /// (g) A SEAT CLASS IS NEVER READ FROM <c>runtimes.json</c> (ORCHESTRATION § 2.7): a manifest there that names a cell, a broker
    /// or the harness for itself is still a CLI seat, unconfined, at the verdict and on the ledger row. RED at base: no class at all.
    /// </summary>
    [Fact]
    public void A_seat_class_is_never_read_from_runtimes_json()
    {
        var (codex, exe, _) = SeatHomeTests.Seat(nameof(A_seat_class_is_never_read_from_runtimes_json));
        foreach (var claimed in new[] { SeatClasses.Cell, SeatClasses.Broker, SeatClasses.Harness })
            codex.Seat!.Args[claimed] = ["--sandbox", "confined-by-a-file"];
        RuntimeCatalog.SaveOverrides([codex]);
        var text = File.ReadAllText(RuntimeCatalog.OverridePath);
        File.WriteAllText(RuntimeCatalog.OverridePath,
            text.Replace("\"Home\":", "\"Class\": \"cell\", \"Assurance\": \"Confined\", \"SeatClass\": \"cell\", \"Home\":",
                StringComparison.Ordinal));

        var loaded = RuntimeCatalog.Find("codex");
        Assert.NotNull(loaded);
        Assert.Contains(SeatClasses.Cell, loaded!.Seat!.Args.Keys);   // the file was read, its claim with it

        var session = new AgentSession(loaded, () => exe, () => Path.GetDirectoryName(exe)!, () => new Dictionary<string, string>(),
            presence: new AgentPresence(), seatProbe: new SeatHomeTests.FakeProbe());
        var verdict = session.CheckSeat();
        Assert.Equal(SeatClasses.Cli, verdict.SeatClass);
        Assert.Equal(SeatAssurance.Unconfined, verdict.Assurance);

        using var db = TestEnv.NewDb();
        var meter = Meter(db, _ => SeatClasses.Cli);
        using var metering = meter.Attach(session, CouncilRoles.Operations);
        var admitted = meter.Begin("## Situation", role: CouncilRoles.Operations);
        Assert.True(admitted.Admitted, admitted.Refusal);
        var row = new AiAttemptStore(db).Get(admitted.Id!)!;
        Assert.Equal((SeatClasses.Cli, "unconfined"), (row.SeatClass, row.Assurance));
    }

    // ---- (h) GUARD -------------------------------------------------------------------------------------------------

    /// <summary>
    /// (h) GUARD: THE LEGACY PAIR ALONE RUN EXACTLY AS BEFORE — on unconfined CLI seats, the owner's chat and the loop's turns
    /// admitted and started, with no proven cell and no position beyond them. Green at base.
    /// </summary>
    [Fact]
    public async Task The_legacy_pair_alone_run_exactly_as_before()
    {
        using var db = TestEnv.NewDb();
        var meter = Meter(db, _ => SeatClasses.Cli);
        var (codex, exe, dump) = SeatHomeTests.Seat(nameof(The_legacy_pair_alone_run_exactly_as_before));
        var register = Register(db, SeatClasses.Cli);

        foreach (var role in CouncilRoles.All)
        {
            var session = new AgentSession(codex, () => exe, () => Path.GetDirectoryName(exe)!, () => new Dictionary<string, string>(),
                presence: new AgentPresence(), role: role, seatProbe: new SeatHomeTests.FakeProbe(), seats: register);
            using var metering = meter.Attach(session, role);
            await session.SendAsync("what are you doing?");
            Assert.DoesNotContain(session.History, t => t.Role == ChatRole.System);
        }
        Assert.Equal(2, Attempts(db));
        Assert.Equal(2, SeatHomeTests.Argv(dump).Count(l => l.StartsWith("exec", StringComparison.Ordinal)));
        Assert.True(meter.Begin("## Situation", role: CouncilRoles.Research).Admitted);
    }

    // ---- (i) GUARD -------------------------------------------------------------------------------------------------

    /// <summary>
    /// (i) GUARD: ARMED LIVE REFUSES EVERY AGENT-SESSION SEAT — with a seat plan or without one — AND NOT THE HARNESS, which has no
    /// process to start and takes no such answer (ORCHESTRATION § 8). Green at base.
    /// </summary>
    [Fact]
    public async Task Armed_live_refuses_every_agent_session_seat_and_not_the_harness()
    {
        const string armed = "Real money is armed, and no sandbox confines the AI here.";
        var (codex, exe, dump) = SeatHomeTests.Seat(nameof(Armed_live_refuses_every_agent_session_seat_and_not_the_harness));
        var bare = RuntimeCatalog.BuiltIn().Single(m => m.Id == "codex");
        bare.Executable = exe;
        bare.Seat = null;
        foreach (var manifest in new[] { codex, bare })
        {
            var session = new AgentSession(manifest, () => exe, () => Path.GetDirectoryName(exe)!, () => new Dictionary<string, string>(),
                presence: new AgentPresence(), launchRefusal: () => armed, seatProbe: new SeatHomeTests.FakeProbe());
            await SeatHomeTests.Turn(session);
            Assert.Contains(session.History, t => t.Role == ChatRole.System && t.Text == armed);
        }
        Assert.DoesNotContain(SeatHomeTests.Argv(dump), l => l.StartsWith("exec", StringComparison.Ordinal));

        using var provider = new FakeProvider();
        provider.Answer(FakeProvider.Message("I read the plan."));
        var harness = HarnessFor(provider, seats: null, role: CouncilRoles.Research);
        await harness.SendMissionAsync("## Situation");
        Assert.Single(provider.Requests);
    }

    // ---- (j) the kinds ---------------------------------------------------------------------------------------------

    /// <summary>
    /// (j) EVERY REFUSAL SAYS ITS KIND AS DATA, AND NONE CONSUMES A WAKE OR THE OWNER'S WORDS (ORCHESTRATION § 2.5 as amended
    /// <c>7509521a</c>): the position's standing and a capability its seat cannot carry are <see cref="RefusalKind.Position"/>; no
    /// proven cell, a verified seat that differs, an unset register, an unconfined neighbour either way are
    /// <see cref="RefusalKind.HostOrLaunch"/>. Every refused launch leaves its wakes due, the owner's among them. RED at base.
    /// </summary>
    [Fact]
    public void Every_refusal_says_its_kind_and_none_consumes_a_wake_or_the_owners_words()
    {
        var cases = new (string Name, Action<Database> Arrange, string Role, string? Declared, string Verified, RefusalKind Kind)[]
        {
            ("unknown", _ => { }, "pos-0000dead", SeatClasses.Harness, SeatClasses.Harness, RefusalKind.Position),
            ("dormant", db => Seat(db, Third, status: OrgPositionStatus.Dormant), Third, SeatClasses.Harness, SeatClasses.Harness, RefusalKind.Position),
            ("code on a cli seat", db => { Head(db, Third); ProveCell(db); }, Third, SeatClasses.Cli, SeatClasses.Cli, RefusalKind.Position),
            ("no proven cell", db => Head(db, Third), Third, SeatClasses.Harness, SeatClasses.Harness, RefusalKind.HostOrLaunch),
            ("verified differs", db => { Head(db, Third); ProveCell(db); }, Third, SeatClasses.Harness, SeatClasses.Broker, RefusalKind.HostOrLaunch),
            ("register unset", db => { Head(db, Third); ProveCell(db); }, Third, null, SeatClasses.Harness, RefusalKind.HostOrLaunch),
            ("legacy beside an active position", db => Head(db, Third), CouncilRoles.Research, SeatClasses.Cli, SeatClasses.Cli, RefusalKind.HostOrLaunch),
        };

        foreach (var (name, arrange, role, declared, verified, kind) in cases)
        {
            using var db = TestEnv.NewDb();
            arrange(db);
            var wakes = new MissionEventStore(db);
            var review = Wake(wakes, role);
            var owner = Wake(wakes, role, MissionEventKind.Owner);
            var meter = Meter(db, _ => declared);
            using var seat = meter.Attach(new Seated(verified), role);

            var refused = meter.Begin("## Situation", consuming: [review, owner], role: role);
            Assert.False(refused.Admitted, $"{name}: admitted");
            Assert.True(kind == refused.Kind, $"{name}: {refused.Kind} — {refused.Refusal}");
            Assert.Equal(0, Attempts(db));
            foreach (var w in new[] { review, owner })
            {
                var e = wakes.Get(w)!;
                Assert.False(e.Consumed, $"{name}: {w} consumed");
                Assert.Null(e.Disposition);
            }
        }

        // AND THE OTHER NEIGHBOUR: a position, its seat and its proof in order, waits for an open unconfined launch.
        using var open = TestEnv.NewDb();
        Head(open, Third);
        ProveCell(open);
        SetStatus(open, Third, OrgPositionStatus.Dormant);
        var shared = Meter(open, r => CouncilRoles.IsKnown(r) ? SeatClasses.Cli : SeatClasses.Harness);
        using var chair = shared.Attach(new Seated(SeatClasses.Cli), CouncilRoles.Operations);
        Assert.True(shared.Begin("## Situation", role: CouncilRoles.Operations).Admitted);
        SetStatus(open, Third, OrgPositionStatus.Active);
        using var head = shared.Attach(new Seated(SeatClasses.Harness), Third);
        var queue = new MissionEventStore(open);
        var owed = Wake(queue, Third, MissionEventKind.Owner);
        var waits = shared.Begin("## Situation", consuming: [owed], role: Third);
        Assert.Equal(RefusalKind.HostOrLaunch, waits.Kind);
        Assert.False(queue.Get(owed)!.Consumed);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    /// <summary>A meter over <paramref name="db"/> whose app declares each role's seat as <paramref name="declared"/> answers.</summary>
    internal static TurnMeter Meter(Database db, Func<string, string?> declared) =>
        new(db, () => 5m, live: new LiveAttempts(), declaredSeat: declared);

    /// <summary>The facts the app would hand the gate for a position, declared on <paramref name="declared"/>.</summary>
    static SeatFacts Facts(Database db, string declared, string? id = null) => new()
    {
        Standing = id is null ? null : new OrgStore(db).Standing(id),
        DeclaredClass = declared,
        ProvenCell = new CellProofStore(db).HoldsProof(),
        ActiveBeyondLegacy = new OrgStore(db).ActivePositionsBeyondLegacy()
    };

    /// <summary>A register over <paramref name="db"/>, as the app fills its own, every position declared on <paramref name="declared"/>.</summary>
    internal static SeatRegister Register(Database db, string declared) =>
        new() { Facts = id => Facts(db, declared, id) };

    /// <summary>A harness conversation for <paramref name="role"/> pointed at the loopback fake, with a fake key pasted for it.</summary>
    static IAgentConversation HarnessFor(FakeProvider provider, SeatRegister? seats, string role = Third)
    {
        var manifest = RuntimeCatalog.Require(ApiAgentRuntime.RuntimeId);
        manifest.BaseUrl = provider.BaseUrl;
        var runtime = new ApiAgentRuntime(manifest, provider.Holding("not-a-real-credential"),
            allowance: () => TurnAllowance.Default, requestTimeout: TimeSpan.FromSeconds(10), seats: seats);
        var home = Path.Combine(TestEnv.Home, $"thinking-seats-{Guid.NewGuid():n}");
        Directory.CreateDirectory(home);
        return runtime.OpenConversation(role, () => home, () => new Dictionary<string, string>(), () => null);
    }

    /// <summary>A position written straight into the chart, as only a test may: under Research's division.</summary>
    internal static void Seat(Database db, string id, string unit = OrgIds.Research, string status = OrgPositionStatus.Active) =>
        db.Write(_ =>
        {
            using var c = db.Cmd("""
                INSERT INTO org_position(id, unit_id, home_dir, status, created_at)
                VALUES($id, $unit, $home, $status, '2026-10-16T12:00:00.0000000Z')
                """, ("$id", id), ("$unit", unit), ("$home", $"org/{id}"), ("$status", status));
            return c.ExecuteNonQuery();
        });

    /// <summary>A team under Research's division, headed by <paramref name="id"/>: a head, as the chart derives one.</summary>
    internal static void Head(Database db, string id)
    {
        var team = $"team-{id}";
        db.Write(_ =>
        {
            using var c = db.Cmd("""
                INSERT INTO org_unit(id, parent_id, kind, status, created_at)
                VALUES($team, $parent, 'team', 'active', '2026-10-16T12:00:00.0000000Z')
                """, ("$team", team), ("$parent", OrgIds.Research));
            return c.ExecuteNonQuery();
        });
        Seat(db, id, unit: team);
        db.Write(_ =>
        {
            using var c = db.Cmd("UPDATE org_unit SET head_position_id=$id WHERE id=$team", ("$id", id), ("$team", team));
            return c.ExecuteNonQuery();
        });
    }

    internal static void SetStatus(Database db, string id, string status) =>
        db.Write(_ =>
        {
            using var c = db.Cmd("UPDATE org_position SET status=$s WHERE id=$id", ("$s", status), ("$id", id));
            return c.ExecuteNonQuery();
        });

    /// <summary>A proven executor-cell row, as only the app may write one (none of its writers exists yet): this platform and build.</summary>
    internal static void ProveCell(Database db, string? platform = null, string? build = null) =>
        db.Write(_ =>
        {
            using var c = db.Cmd("""
                INSERT INTO cell_proof(provider, platform, build, profile_hash, proven_at)
                VALUES('test-cell', $platform, $build, 'sha256-test', $at)
                """, ("$platform", platform ?? CellProofStore.ThisPlatform), ("$build", build ?? Versions.App),
                ("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
            return c.ExecuteNonQuery();
        });

    static string Wake(MissionEventStore wakes, string role, string kind = MissionEventKind.Review)
    {
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        var id = $"{kind}:{role}:{Guid.NewGuid():n}";
        Assert.True(wakes.Raise(id, kind, at, role: role));
        return id;
    }

    static long Attempts(Database db) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT COUNT(*) FROM ai_attempt");
        return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    /// <summary>A conversation whose seat the app established as <paramref name="seatClass"/>; it never runs a turn here.</summary>
    internal sealed class Seated(string seatClass) : IAgentConversation, ISeatLaunch
    {
        public SeatVerdict CheckSeat() => SeatVerdict.None with { SeatClass = seatClass };

        public bool Busy => false;
        public IReadOnlyList<ChatTurn> History => [];
        public event Action<ChatTurn>? TurnAdded { add { } remove { } }
        public event Action<string>? Delta { add { } remove { } }
        public event Action? StateChanged { add { } remove { } }
        public event Action<AgentTurnEnded>? TurnEnded { add { } remove { } }
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string message, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendMissionAsync(string message, CancellationToken ct = default) => Task.CompletedTask;
        public void Queue(string message) { }
        public IReadOnlyList<string> TakeTyped() => [];
        public Task CancelAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
    }
}

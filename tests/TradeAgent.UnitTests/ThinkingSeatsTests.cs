using System.Globalization;
using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Connectors.Fake;
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
    /// kind is new. Fix pass 1 (review item 2): a register the app SET whose reads fail refuses the legacy pair too while a position
    /// beyond them is active, as it cannot rule that neighbour out; only a register nothing set — a process that composed no AI —
    /// leaves the pair as they were.
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
            var refused = PositionActivation.Ask(Third, chart.Standing(Third), register);
            Assert.Equal(RefusalKind.HostOrLaunch, refused?.Kind);
            Assert.Equal(RefusalKind.HostOrLaunch, PositionActivation.AtLaunch(Third, SeatClasses.Harness, register)?.Kind);
            foreach (var legacy in CouncilRoles.All)
            {
                var seam = PositionActivation.Ask(legacy, chart.Standing(legacy), register);
                var launch = PositionActivation.AtLaunch(legacy, SeatClasses.Cli, register);
                if (ReferenceEquals(register, unset))
                {
                    Assert.Null(seam);
                    Assert.Null(launch);
                }
                else
                {
                    Assert.Equal(RefusalKind.HostOrLaunch, seam?.Kind);
                    Assert.Equal(RefusalKind.HostOrLaunch, launch?.Kind);
                }
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

    // ---- (n) review item 1: a late refusal keeps its kind and settles nothing ------------------------------------

    /// <summary>
    /// (n) A REFUSAL AT THE GATE'S LAST CHECK, AFTER ADMISSION, STARTS NOTHING, SETTLES NO WAKE AND KEEPS THE OWNER'S WORDS (fix
    /// pass 1, review item 1; ORCHESTRATION § 2.5): the gate's facts change between <c>Begin</c> and the start — a position beyond
    /// the legacy pair activated — so the chair's unconfined CLI launch is refused immediately before its process. No process; every
    /// wake it consumed is due again; the owner's words are neither settled nor retried as failed, only shown undelivered with the
    /// refusal's sentence; the row is closed as refused, charged nothing, with the kind on it. RED at the rebased tip: the loop
    /// settled the wakes as a failed turn and re-raised the owner's words.
    /// </summary>
    [Fact]
    public async Task A_late_gate_refusal_starts_nothing_settles_no_wake_and_keeps_the_owners_words()
    {
        using var db = TestEnv.NewDb();
        var events = new MissionEventStore(db);
        Assert.True(events.RecordOwnerMessage("how are we doing?", DateTimeOffset.UtcNow.AddMinutes(-1)));
        var owner = Assert.Single(events.Due(DateTimeOffset.UtcNow)).Id;
        var review = Wake(events, CouncilRoles.Operations);
        var meter = Meter(db, _ => SeatClasses.Cli);
        var (codex, exe, dump) = SeatHomeTests.Seat(nameof(A_late_gate_refusal_starts_nothing_settles_no_wake_and_keeps_the_owners_words));
        var session = new AgentSession(codex, () => exe, () => Path.GetDirectoryName(exe)!, () => new Dictionary<string, string>(),
            presence: new AgentPresence(), role: CouncilRoles.Operations, seatProbe: new SeatHomeTests.FakeProbe(),
            seats: Register(db, SeatClasses.Cli));
        using var metering = meter.Attach(session, CouncilRoles.Operations);
        AgentTurnEnded? ended = null;
        session.TurnEnded += e => ended = e;
        var host = new LateGateHost(db, meter, session, afterBegin: () => Head(db, Third));

        await new MissionLoop(host, new MissionOptions { ReviewEvery = TimeSpan.Zero }).TurnAsync();

        Assert.NotNull(host.Admitted);   // the admission passed: this is the last check's refusal
        Assert.DoesNotContain(SeatHomeTests.Argv(dump), l => l.StartsWith("exec", StringComparison.Ordinal));
        foreach (var id in new[] { owner, review })
            Assert.False(events.Get(id)!.Consumed, $"{id} was taken by a launch the gate refused: {events.Get(id)!.Disposition}");
        Assert.Null(events.Get(review)!.Disposition);
        Assert.Equal(MissionEventDisposition.Blocked, events.Get(owner)!.Disposition);
        Assert.Contains($"'{Third}'", events.Get(owner)!.DispositionDetail, StringComparison.Ordinal);
        Assert.Equal(1L, OwnerRows(db));   // never re-raised as a failed turn's retry

        Assert.Equal(RefusalKind.HostOrLaunch, ended?.GateRefused?.Refusal.Kind);
        Assert.True(ended!.GateRefused!.BeforeAnyWork);
        var row = new AiAttemptStore(db).Get(host.Admitted!)!;
        Assert.Equal(AiAttemptState.ENDED, row.State);
        Assert.Equal(0m, row.Cost);
        using var context = JsonDocument.Parse(row.Context!);
        Assert.Equal(nameof(RefusalKind.HostOrLaunch), context.RootElement.GetProperty("gate_kind").GetString());
        Assert.Equal(0m, meter.Today.Reserved);

        // AND THE HARNESS, ON A HOST WITH NO METER: refused before its first request, the turn's end says so as data.
        using var noCell = TestEnv.NewDb();
        Head(noCell, Third);
        using var provider = new FakeProvider();
        var harness = HarnessFor(provider, Register(noCell, SeatClasses.Harness));
        AgentTurnEnded? harnessEnded = null;
        harness.TurnEnded += e => harnessEnded = e;
        await harness.SendMissionAsync("## Situation");
        Assert.Empty(provider.Requests);
        Assert.Equal((RefusalKind.HostOrLaunch, true), (harnessEnded?.GateRefused?.Refusal.Kind, harnessEnded?.GateRefused?.BeforeAnyWork));
        Assert.Equal(ApiConversation.EndedGateRefused, harnessEnded!.Outcome);
    }

    static long OwnerRows(Database db) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT COUNT(*) FROM mission_event WHERE kind=$k", ("$k", MissionEventKind.Owner));
        return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    /// <summary>
    /// A host whose admission is the real meter's and whose transition commits the meter's close and the loop's dispositions in one
    /// write, as the app's does — and which changes the gate's facts the moment an admission passes.
    /// </summary>
    sealed class LateGateHost(Database db, TurnMeter meter, IAgentConversation conversation, Action afterBegin) : IMissionHost
    {
        public string? Admitted { get; private set; }
        public IAgentConversation? Conversation => conversation;
        public IAgentConversation? ConversationFor(string role) => conversation;
        public MissionEventStore? Events { get; } = new(db);
        public string AgentHome { get; } = Path.Combine(TestEnv.Home, $"late-gate-{Guid.NewGuid():n}");
        public bool InboxChangedSinceLastPass => false;
        public Task ScanAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<MissionSituation> SituationAsync(CancellationToken ct) =>
            Task.FromResult(new MissionSituation { LocalTime = DateTimeOffset.Now, Mode = "PAPER" });
        public string? NextAttemptId() => meter.Mint();
        public AiAdmission BeginTurn(string prompt, IReadOnlyList<string> wakes) => BeginTurn(prompt, wakes, CouncilRoles.Default);
        public AiAdmission BeginTurn(string prompt, IReadOnlyList<string> wakes, string role)
        {
            var admission = meter.Begin(prompt, wakes, role);
            if (admission.Admitted)
            {
                Admitted = admission.Id;
                afterBegin();
            }
            return admission;
        }
        public void CommitTurn(string role, string? attempt, Action dispositions) =>
            db.Write(_ => { meter.CommitStaged(role); dispositions(); return 0; });
    }

    // ---- (p) re-review item 6: a refused turn publishes nothing ------------------------------------------------

    /// <summary>
    /// (p) A TURN THE GATE REFUSED MID-WAY PUBLISHES NOTHING AND GIVES THE OWNER'S WORDS BACK (fix pass 2, item 6; ORCHESTRATION
    /// § 2.3, the outputs of a refused attempt are held, never published; § 2.5, the owner's words are never settled by a refusal):
    /// the chair on the harness stages an agenda in its first request, its seat changes, and its second request is refused at the
    /// last check. Through the real relay's committed transition, the agenda is held — recorded as the app's own copy with a held
    /// disposition, delivered to nobody, buying no turn — so it never delegates the owner's message: every wake the launch took is
    /// due again and the owner's words stay open, shown undelivered with the refusal's sentence. A later pass over the same folder
    /// holds it again. RED at the rebased tip: the relay published the agenda and delegated the owner's words before they were
    /// given back.
    /// </summary>
    [Fact]
    public async Task A_turn_the_gate_refused_mid_way_publishes_nothing_and_gives_the_owners_words_back()
    {
        using var db = TestEnv.NewDb();
        var events = new MissionEventStore(db);
        Assert.True(events.RecordOwnerMessage("brief research on the March gap", DateTimeOffset.UtcNow.AddMinutes(-1)));
        var owner = Assert.Single(events.Due(DateTimeOffset.UtcNow)).Id;

        var root = Path.Combine(TestEnv.Home, $"gate-relay-{Guid.NewGuid():n}");
        string HomeFor(string role) => WorkspaceBuilder.HomeOf(root, role);
        foreach (var role in CouncilRoles.All) Directory.CreateDirectory(Path.Combine(HomeFor(role), WorkspaceBuilder.OutDir));

        var declared = SeatClasses.Harness;
        var meter = Meter(db, _ => declared);
        var register = new SeatRegister
        {
            Facts = id => Facts(db, declared, id),
            Neighbours = () => new OrgStore(db).ActivePositionsBeyondLegacy()
        };
        using var provider = new FakeProvider();
        var manifest = RuntimeCatalog.Require(ApiAgentRuntime.RuntimeId);
        manifest.BaseUrl = provider.BaseUrl;
        using var runtime = new ApiAgentRuntime(manifest, provider.Holding("not-a-real-credential"),
            tools: role => new GrantedWorkerTools(role, home: () => HomeFor(role), attempt: () => meter.OpenAttemptIdFor(role)),
            attemptId: role => meter.OpenAttemptIdFor(role),
            allowance: () => TurnAllowance.Default, requestTimeout: TimeSpan.FromSeconds(10), seats: register);
        var chair = runtime.OpenConversation(CouncilRoles.Operations, () => HomeFor(CouncilRoles.Operations),
            () => new Dictionary<string, string>(), () => null);
        using var metering = meter.Attach(chair, CouncilRoles.Operations);
        AgentTurnEnded? ended = null;
        chair.TurnEnded += e => ended = e;

        // REQUEST 1 STAGES THE AGENDA under the launch's own id; the chair's seat then changes, so request 2 is refused.
        string? attempt = null;
        provider.Arriving = () =>
        {
            if (provider.Requests.Count != 1) return;
            attempt = meter.OpenAttemptIdFor(CouncilRoles.Operations);
            provider.Answer(FakeProvider.ToolCall(GrantedWorkerTools.WriteFile,
                new { path = $"{WorkspaceBuilder.OutDir}/agenda-{attempt}.md", content = "Research: find the March gap." }));
            declared = SeatClasses.Cli;
        };
        var relay = new CouncilRelay(db, HomeFor);
        var host = new RelayedHost(db, meter, chair, relay, HomeFor(CouncilRoles.Operations));

        await new MissionLoop(host, new MissionOptions { ReviewEvery = TimeSpan.Zero }).TurnAsync();

        Assert.Single(provider.Requests);
        Assert.Equal((RefusalKind.HostOrLaunch, false), (ended?.GateRefused?.Refusal.Kind, ended?.GateRefused?.BeforeAnyWork));
        var staged = Path.Combine(HomeFor(CouncilRoles.Operations), WorkspaceBuilder.OutDir, $"agenda-{attempt}.md");
        Assert.True(File.Exists(staged), "the refused turn's staged agenda was not kept on disk");

        var words = events.Get(owner)!;
        Assert.False(words.Consumed || words.Disposition == MissionEventDisposition.Delegated,
            $"the owner's words were taken by a turn the gate refused: consumed {words.Consumed}, {words.Disposition} {words.DispositionDetail}");
        Assert.Equal(MissionEventDisposition.Blocked, words.Disposition);
        AssertNothingPublished(db, events);

        // A LATER PASS OVER THE SAME FOLDER, AND THE START-UP RECONCILE, HOLD IT AGAIN: the attempt's row says it was refused.
        relay.Run(CouncilRoles.Operations, attempt: null);
        relay.Reconcile();
        AssertNothingPublished(db, events);
        Assert.Equal(MissionEventDisposition.Blocked, events.Get(owner)!.Disposition);
        Assert.False(events.Get(owner)!.Consumed);
    }

    // ---- (r) re-review 2, P1: a crash after the refusal never turns it into permission ------------------------

    /// <summary>
    /// (r) A CRASH BETWEEN A MID-TURN GATE REFUSAL AND ITS COMMIT NEVER PUBLISHES THE REFUSED LAUNCH'S OUTPUT (re-review 2, P1;
    /// ORCHESTRATION § 2.3: a refused attempt's outputs are held, never published, and LOST never turns a refusal into permission):
    /// the chair stages an agenda, its second request is refused, and the app dies at the instant before the turn's commit. The
    /// next start declares the launch LOST and the start-up reconcile walks its folder: the agenda is held — no delivery, no brief
    /// wake — because the refusal was written on the row the moment it happened. The owner's words stay owed and are never
    /// settled as a lost turn's: the next turn is handed them, once, and answers them. RED before the fix: the reconcile published
    /// the agenda under the LOST launch.
    /// </summary>
    [Fact]
    public async Task A_crash_after_a_mid_turn_refusal_holds_the_refused_launchs_output_and_keeps_the_owners_words()
    {
        using var rig = RefusedTurnRig.Arrange();
        rig.Relay.Boundary = at =>
        {
            if (at == CouncilRelay.BeforeCommit) throw new IOException("TradeAgent stopped here");
        };
        await rig.Run();
        Assert.Equal(AiAttemptState.LAUNCHED, new AiAttemptStore(rig.Db).Get(rig.Attempt!)!.State);

        // THE NEXT START: a meter that opens the database turns the open row LOST, and the relay reconciles every folder.
        var restarted = Meter(rig.Db, _ => SeatClasses.Harness);
        Assert.Equal(AiAttemptState.LOST, new AiAttemptStore(rig.Db).Get(rig.Attempt!)!.State);
        new CouncilRelay(rig.Db, rig.HomeFor).Reconcile();
        Assert.True(Count(rig.Db, "SELECT COUNT(*) FROM delivery") == 0,
            "the start-up reconcile published the output of a launch the gate refused, because a crash had made it LOST");
        AssertNothingPublished(rig.Db, rig.Events);

        // THE NEXT TURN IS HANDED THE OWNER'S WORDS AND ANSWERS THEM: never settled as a lost turn's and raised again.
        rig.Declared = SeatClasses.Harness;
        rig.Provider.Arriving = null;
        rig.Provider.Answer(FakeProvider.Message("Research is briefed on the March gap."));
        using var runtime = rig.Runtime(restarted);
        var chair = runtime.OpenConversation(CouncilRoles.Operations, () => rig.HomeFor(CouncilRoles.Operations),
            () => new Dictionary<string, string>(), () => null);
        using var metering = restarted.Attach(chair, CouncilRoles.Operations);
        var next = new RelayedHost(rig.Db, restarted, chair, new CouncilRelay(rig.Db, rig.HomeFor), rig.HomeFor(CouncilRoles.Operations));
        await new MissionLoop(next, new MissionOptions { ReviewEvery = TimeSpan.Zero }).TurnAsync();

        var words = rig.Events.Get(rig.Owner)!;
        Assert.True(words.ConsumedBy is { } by && by != rig.Attempt && words.Disposition == MissionEventDisposition.Answered,
            $"the owner's words were not handed to the next turn: consumed by {words.ConsumedBy}, {words.Disposition} {words.DispositionDetail}");
        Assert.Equal(1L, OwnerRows(rig.Db));
        AssertNothingPublished(rig.Db, rig.Events);
    }

    // ---- (s) re-review 2, P2: a held output never shares an identity with a published one -----------------------

    /// <summary>
    /// (s) A HELD OUTPUT NEVER TAKES THE PUBLICATION'S CONTENT IDENTITY (re-review 2, P2): the refused launch A staged an agenda and
    /// it was held; the next launch B writes the very same text and publishes it. B's publication is attributed to B and delivered;
    /// no publication attributed to A carries a delivery, then or ever. RED before the fix: B's identical text hashed to A's held
    /// row, and the delivery went out under a publication that names the refused launch.
    /// </summary>
    [Fact]
    public async Task A_held_output_never_shares_its_identity_with_a_later_identical_publication()
    {
        using var rig = RefusedTurnRig.Arrange();
        await rig.Run();
        var refused = rig.Attempt!;
        AssertNothingPublished(rig.Db, rig.Events);

        // LAUNCH B, ON THE SAME SEAT, WRITES THE SAME TEXT AND ENDS.
        rig.Declared = SeatClasses.Harness;
        string? second = null;
        rig.Provider.Arriving = () =>
        {
            if (rig.Provider.Requests.Count != 2) return;
            second = rig.Meter.OpenAttemptIdFor(CouncilRoles.Operations);
            rig.Provider.Answer(FakeProvider.ToolCall(GrantedWorkerTools.WriteFile,
                new { path = $"{WorkspaceBuilder.OutDir}/agenda-{second}.md", content = RefusedTurnRig.Agenda }, "call-2"));
            rig.Provider.Answer(FakeProvider.Message("Research is briefed."));
        };
        await rig.Run();

        Assert.NotNull(second);
        var delivered = Count(rig.Db, $"SELECT COUNT(*) FROM delivery d JOIN publication p ON p.id = d.publication_id WHERE p.attempt = '{refused}'");
        Assert.True(delivered == 0, $"{delivered} delivery(ies) went out under a publication attributed to the refused launch");
        Assert.Equal(1L, Count(rig.Db, $"SELECT COUNT(*) FROM delivery d JOIN publication p ON p.id = d.publication_id WHERE p.attempt = '{second}'"));
    }

    /// <summary>
    /// THE REFUSED CHAIR TURN OF (p), (r) AND (s): the chair on the harness, metered, through the real relay; request 1 stages
    /// <see cref="Agenda"/> under the launch's own id and the chair's declared seat changes, so request 2 is refused at the last check.
    /// </summary>
    sealed class RefusedTurnRig : IDisposable
    {
        public const string Agenda = "Research: find the March gap.";

        public Database Db { get; } = TestEnv.NewDb();
        public MissionEventStore Events { get; }
        public string Owner { get; }
        public string Root { get; } = Path.Combine(TestEnv.Home, $"gate-rig-{Guid.NewGuid():n}");
        public string Declared { get; set; } = SeatClasses.Harness;
        public FakeProvider Provider { get; } = new();
        public TurnMeter Meter { get; }
        public CouncilRelay Relay { get; }
        public string? Attempt { get; private set; }
        readonly ApiAgentRuntime _runtime;
        readonly IDisposable _metering;
        readonly RelayedHost _host;

        RefusedTurnRig()
        {
            Events = new MissionEventStore(Db);
            Assert.True(Events.RecordOwnerMessage("brief research on the March gap", DateTimeOffset.UtcNow.AddMinutes(-1)));
            Owner = Assert.Single(Events.Due(DateTimeOffset.UtcNow)).Id;
            foreach (var role in CouncilRoles.All) Directory.CreateDirectory(Path.Combine(HomeFor(role), WorkspaceBuilder.OutDir));
            Meter = ThinkingSeatsTests.Meter(Db, _ => Declared);
            _runtime = Runtime(Meter);
            var chair = _runtime.OpenConversation(CouncilRoles.Operations, () => HomeFor(CouncilRoles.Operations),
                () => new Dictionary<string, string>(), () => null);
            _metering = Meter.Attach(chair, CouncilRoles.Operations);
            Relay = new CouncilRelay(Db, HomeFor);
            _host = new RelayedHost(Db, Meter, chair, Relay, HomeFor(CouncilRoles.Operations));
            Provider.Arriving = () =>
            {
                if (Provider.Requests.Count != 1) return;
                Attempt = Meter.OpenAttemptIdFor(CouncilRoles.Operations);
                Provider.Answer(FakeProvider.ToolCall(GrantedWorkerTools.WriteFile,
                    new { path = $"{WorkspaceBuilder.OutDir}/agenda-{Attempt}.md", content = Agenda }));
                Declared = SeatClasses.Cli;
            };
        }

        public static RefusedTurnRig Arrange() => new();

        public string HomeFor(string role) => WorkspaceBuilder.HomeOf(Root, role);

        /// <summary>A harness runtime over the loopback fake, metered by <paramref name="meter"/>, its register declaring <see cref="Declared"/>.</summary>
        public ApiAgentRuntime Runtime(TurnMeter meter)
        {
            var manifest = RuntimeCatalog.Require(ApiAgentRuntime.RuntimeId);
            manifest.BaseUrl = Provider.BaseUrl;
            var register = new SeatRegister
            {
                Facts = id => ThinkingSeatsTests.Facts(Db, Declared, id),
                Neighbours = () => new OrgStore(Db).ActivePositionsBeyondLegacy()
            };
            return new ApiAgentRuntime(manifest, Provider.Holding("not-a-real-credential"),
                tools: role => new GrantedWorkerTools(role, home: () => HomeFor(role), attempt: () => meter.OpenAttemptIdFor(role)),
                attemptId: role => meter.OpenAttemptIdFor(role),
                allowance: () => TurnAllowance.Default, requestTimeout: TimeSpan.FromSeconds(10), seats: register);
        }

        public Task Run() => new MissionLoop(_host, new MissionOptions { ReviewEvery = TimeSpan.Zero }).TurnAsync();

        public void Dispose()
        {
            _metering.Dispose();
            _runtime.Dispose();
            Provider.Dispose();
            Db.Dispose();
        }
    }

    /// <summary>No delivery and no brief bought a turn; the one agenda is held, with why, as the app's own copy.</summary>
    static void AssertNothingPublished(Database db, MissionEventStore events)
    {
        Assert.Equal(0L, Count(db, "SELECT COUNT(*) FROM delivery"));
        Assert.Equal(0L, Count(db, $"SELECT COUNT(*) FROM mission_event WHERE kind='{MissionEventKind.Brief}'"));
        var held = Assert.Single(events.HeldSince(CouncilRoles.Operations, null, 10));
        Assert.Contains("refused", held.DispositionDetail, StringComparison.Ordinal);
    }

    static long Count(Database db, string sql) => db.Read(_ =>
    {
        using var c = db.Cmd(sql);
        return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    /// <summary>A host whose committed transition is the real relay's, as the app wires it: close, land, snapshot, dispositions.</summary>
    sealed class RelayedHost(Database db, TurnMeter meter, IAgentConversation conversation, CouncilRelay relay, string home)
        : IMissionHost
    {
        public IAgentConversation? Conversation => conversation;
        public IAgentConversation? ConversationFor(string role) => conversation;
        public MissionEventStore? Events { get; } = new(db);
        public string AgentHome => home;
        public bool InboxChangedSinceLastPass => false;
        public Task ScanAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<MissionSituation> SituationAsync(CancellationToken ct) =>
            Task.FromResult(new MissionSituation { LocalTime = DateTimeOffset.Now, Mode = "PAPER" });
        public string? NextAttemptId() => meter.Mint();
        public AiAdmission BeginTurn(string prompt, IReadOnlyList<string> wakes) => BeginTurn(prompt, wakes, CouncilRoles.Default);
        public AiAdmission BeginTurn(string prompt, IReadOnlyList<string> wakes, string role) => meter.Begin(prompt, wakes, role);
        public void Relay(string role, string? attempt) => relay.Run(role, attempt);
        public void CommitTurn(string role, string? attempt, Action dispositions) =>
            relay.CommitTurn(role, attempt, () => meter.CommitStaged(role), dispositions);
    }

    // ---- (q) re-review item 7: the puppet seam ------------------------------------------------------------------

    /// <summary>
    /// (q) A PUPPET SEAT RUNS NO MODEL AND IS NO UNCONFINED NEIGHBOUR (fix pass 2, item 7; the seam owed to <c>U-puppet-runtime</c>,
    /// ORCHESTRATION § 2.3 and § 7): a position on a puppet seat is not asked for a proven cell — the owner's answer binds real-model
    /// seats — and neither the legacy pair's puppet launch beside an active position nor an open puppet launch is an unconfined
    /// neighbour; the class goes into <c>ai_attempt.seat_class</c> at admission. A harness head on the same host is still refused.
    /// RED at the rebased tip: a puppet seat was a class the gate did not know, refused, and counted as unconfined.
    /// </summary>
    [Fact]
    public void A_puppet_seat_is_asked_for_no_proven_cell_and_is_no_unconfined_neighbour()
    {
        using var db = TestEnv.NewDb();
        Head(db, Third);   // and no proven cell
        var chart = new OrgStore(db);
        var puppets = Register(db, SeatPlan.PuppetClass);

        var third = PositionActivation.AtLaunch(Third, SeatPlan.PuppetClass, puppets);
        Assert.True(third is null, $"a position on a puppet seat was refused: {third?.Kind} {third?.Words}");
        foreach (var legacy in CouncilRoles.All)
        {
            Assert.Null(PositionActivation.AtLaunch(legacy, SeatPlan.PuppetClass, puppets));
            Assert.Null(PositionActivation.Ask(legacy, chart.Standing(legacy), puppets));
        }
        Assert.Equal(RefusalKind.HostOrLaunch, PositionActivation.AtLaunch(Third, SeatClasses.Harness, Register(db, SeatClasses.Harness))?.Kind);

        // THROUGH THE METER: the chair's puppet launch is admitted beside the active third and is no unconfined neighbour to it.
        var meter = Meter(db, _ => SeatPlan.PuppetClass);
        using var chair = meter.Attach(new Seated(SeatPlan.PuppetClass), CouncilRoles.Operations);
        using var head = meter.Attach(new Seated(SeatPlan.PuppetClass), Third);
        var first = meter.Begin("## Situation", role: CouncilRoles.Operations);
        Assert.True(first.Admitted, first.Refusal);
        Assert.Empty(meter.OpenUnconfined(except: Third));
        var second = meter.Begin("## Situation", role: Third);
        Assert.True(second.Admitted, second.Refusal);
        foreach (var id in new[] { first.Id!, second.Id! })
            Assert.Equal(SeatPlan.PuppetClass, new AiAttemptStore(db).Get(id)!.SeatClass);
    }

    // ---- (k) review item 2: the neighbour rule is asked whatever the facts read -----------------------------------

    /// <summary>
    /// (k) A LEGACY LAUNCH WHOSE FACTS CANNOT BE READ IS STILL ASKED THE NO-UNCONFINED-NEIGHBOUR RULE (fix pass 1, review item 2;
    /// ORCHESTRATION § 2.3): the register's own neighbour read decides — a position beyond the pair active refuses the pair's
    /// unconfined launch, naming it; a neighbour read that fails refuses it, about the host; a brokered seat is no neighbour; and
    /// once the read succeeds and names nobody, the pair run as before. RED at the rebased tip: a register whose facts threw passed
    /// <c>AtLaunch("operations", "cli")</c> beside an active position. THE WATCHED MUTANT: the neighbour read removed from
    /// <c>AtLaunch</c>'s legacy path.
    /// </summary>
    [Fact]
    public void A_legacy_launch_whose_facts_cannot_be_read_is_still_asked_the_neighbour_rule()
    {
        using var db = TestEnv.NewDb();
        Head(db, Third);
        var chart = new OrgStore(db);

        var throwing = new SeatRegister { Facts = _ => throw new InvalidOperationException("the chart is on fire") };
        Assert.Equal(RefusalKind.HostOrLaunch, PositionActivation.AtLaunch(CouncilRoles.Operations, SeatClasses.Cli, throwing)?.Kind);

        var neighbours = new SeatRegister
        {
            Facts = _ => throw new InvalidOperationException("the chart is on fire"),
            Neighbours = () => new OrgStore(db).ActivePositionsBeyondLegacy()
        };
        foreach (var legacy in CouncilRoles.All)
        {
            var launch = PositionActivation.AtLaunch(legacy, SeatClasses.Cli, neighbours);
            Assert.Equal(RefusalKind.HostOrLaunch, launch?.Kind);
            Assert.Contains($"'{Third}'", launch!.Words, StringComparison.Ordinal);
            var seam = PositionActivation.Ask(legacy, chart.Standing(legacy), neighbours);
            Assert.Equal(RefusalKind.HostOrLaunch, seam?.Kind);
            Assert.Contains("confined too, or not there", seam!.Words, StringComparison.Ordinal);
            Assert.Null(PositionActivation.AtLaunch(legacy, SeatClasses.Harness, neighbours));   // brokered: no unconfined neighbour
        }

        var blind = new SeatRegister { Facts = _ => null, Neighbours = () => throw new InvalidOperationException("unreadable") };
        var unread = PositionActivation.AtLaunch(CouncilRoles.Research, SeatClasses.Cli, blind);
        Assert.Equal(RefusalKind.HostOrLaunch, unread?.Kind);
        Assert.Contains("could not read whether any position beyond the legacy pair is active", unread!.Words, StringComparison.Ordinal);

        // THE READ SUCCEEDS AND NAMES NOBODY: the legacy pair run exactly as before.
        SetStatus(db, Third, OrgPositionStatus.Dormant);
        foreach (var legacy in CouncilRoles.All)
        {
            Assert.Null(PositionActivation.AtLaunch(legacy, SeatClasses.Cli, neighbours));
            Assert.Null(PositionActivation.Ask(legacy, chart.Standing(legacy), neighbours));
        }
    }

    // ---- (o) re-review item 5: outside a test host an unset register fails closed -------------------------------

    /// <summary>
    /// (o) OUTSIDE A TEST HOST, A REGISTER NOTHING SET REFUSES THE LEGACY PAIR'S UNCONFINED LAUNCH (fix pass 2, item 5; ORCHESTRATION
    /// § 2.3, fail closed): a production path that forgot the register cannot run the pair unconfined beside a position nobody could
    /// rule out — at the seam and at the last check, about the host. A brokered seat is no neighbour; a test host keeps the pair's
    /// behaviour from before the gate; and the app's own host sets its register when it composes the AI, before any conversation
    /// exists, so the product's launches are asked the neighbour rule of a register that answers. RED at the rebased tip: an unset
    /// register passed <c>AtLaunch("operations", "cli")</c> beside an active position. THE WATCHED MUTANT: the test-host condition
    /// removed.
    /// </summary>
    [Fact]
    public void Outside_a_test_host_an_unset_register_refuses_the_legacy_pairs_unconfined_launch()
    {
        using var db = TestEnv.NewDb();
        Head(db, Third);
        var chart = new OrgStore(db);
        var unset = new SeatRegister();

        foreach (var legacy in CouncilRoles.All)
        {
            var launch = PositionActivation.AtLaunch(legacy, SeatClasses.Cli, unset, testHost: false);
            Assert.True(launch?.Kind == RefusalKind.HostOrLaunch,
                $"an unset register passed '{legacy}' unconfined beside an active position outside a test host: {launch?.Kind}");
            Assert.Contains("register is not set", launch!.Words, StringComparison.Ordinal);
            Assert.Equal(RefusalKind.HostOrLaunch,
                PositionActivation.Ask(legacy, chart.Standing(legacy), unset, null, testHost: false)?.Kind);
            Assert.Null(PositionActivation.AtLaunch(legacy, SeatClasses.Harness, unset, testHost: false));   // brokered: no neighbour
            Assert.Null(PositionActivation.AtLaunch(legacy, SeatClasses.Cli, unset, testHost: true));         // a test host, as before
        }
        Assert.True(PositionActivation.TestHost, "this assembly's module initializer did not mark it a test host");

        // THE APP'S HOST SETS ITS REGISTER WHEN IT COMPOSES THE AI: outside a test host its legacy launch is asked a neighbour read
        // that answers — refused while the third is active, run once it is dormant.
        var app = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        Assert.True(app.Seats.IsSet, "the app composed its AI and left the activation gate's register unset");
        var beside = PositionActivation.AtLaunch(CouncilRoles.Operations, SeatClasses.Cli, app.Seats, testHost: false);
        Assert.Equal(RefusalKind.HostOrLaunch, beside?.Kind);
        Assert.Contains($"'{Third}'", beside!.Words, StringComparison.Ordinal);
        SetStatus(db, Third, OrgPositionStatus.Dormant);
        Assert.Null(PositionActivation.AtLaunch(CouncilRoles.Operations, SeatClasses.Cli, app.Seats, testHost: false));
    }

    // ---- (m) review item 3: a proof counts on the host that recorded it ------------------------------------------

    /// <summary>
    /// (m) A PROOF RECORDED ON ANOTHER MACHINE, OR UNDER ANOTHER KERNEL RELEASE, HOLDS NONE HERE (fix pass 1, review item 3;
    /// ORCHESTRATION § 2.3: no real-model position before a proven cell on THIS host): a home copied to a machine of the same platform
    /// and build activates nothing until the benchmark has passed there. A host whose identity cannot be read holds none; this host's
    /// own row holds one. RED at the rebased tip: a proof of the same platform and build counted on any machine.
    /// </summary>
    [Fact]
    public void A_proof_recorded_on_another_machine_or_kernel_release_holds_none_here()
    {
        using var db = TestEnv.NewDb();
        Assert.NotNull(HostIdentity.This);
        Assert.Equal(64, HostIdentity.This!.Machine.Length);   // a hash, never the id itself

        ProveCell(db, machine: Sha256Hex.Of("another machine"));
        Assert.False(new CellProofStore(db).HoldsProof(), "a proof recorded on another machine counted here");
        ProveCell(db, release: "0.0.0-another-release");
        Assert.False(new CellProofStore(db).HoldsProof(), "a proof recorded under another kernel release counted here");

        ProveCell(db);
        Assert.False(new CellProofStore(db, () => null).HoldsProof(), "a host whose identity could not be read held a proof");
        Assert.True(new CellProofStore(db).HoldsProof());
        var proof = Assert.Single(new CellProofStore(db).Proven());
        Assert.Equal((HostIdentity.This.Machine, HostIdentity.This.OsRelease), (proof.Machine, proof.OsRelease));
    }

    // ---- (l) review item 4: a chart read failure is about the host ------------------------------------------------

    /// <summary>
    /// (l) A CHART THAT CANNOT BE READ IS A REFUSAL ABOUT THE HOST, AT BOTH ENTRY POINTS (fix pass 1, review item 4; ORCHESTRATION
    /// § 2.5): a transient store failure for an active position is <see cref="RefusalKind.HostOrLaunch"/> at the meter's admission
    /// and at the app's seam — its wakes kept under backoff, never settled by O3 as a position defect. A position the chart READ as
    /// dormant or unknown stays <see cref="RefusalKind.Position"/>. RED at the rebased tip: both answered Position.
    /// </summary>
    [Fact]
    public async Task A_chart_that_cannot_be_read_is_a_refusal_about_the_host_at_both_entry_points()
    {
        using var db = TestEnv.NewDb();
        Head(db, Third);
        ProveCell(db);
        var meter = Meter(db, _ => SeatClasses.Harness);
        using var seat = meter.Attach(new Seated(SeatClasses.Harness), Third);
        var app = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        await using var gateway = app.Gateway;

        Rename(db, "org_position", "org_position_away");
        try
        {
            var begun = meter.Begin("## Situation", role: Third);
            Assert.False(begun.Admitted);
            var seam = app.ActivationRefusal(Third);
            Assert.True(begun.Kind == RefusalKind.HostOrLaunch && seam?.Kind == RefusalKind.HostOrLaunch,
                $"a chart read failure was a refusal about the position: meter {begun.Kind}, seam {seam?.Kind}");
            Assert.Contains("could not read the organisation chart", begun.Refusal, StringComparison.Ordinal);
            Assert.Equal(0, Attempts(db));
        }
        finally { Rename(db, "org_position_away", "org_position"); }

        // READ, AND NOT ACTIVE: a refusal about the position, as before.
        SetStatus(db, Third, OrgPositionStatus.Dormant);
        Assert.Equal(RefusalKind.Position, meter.Begin("## Situation", role: Third).Kind);
        Assert.Equal(RefusalKind.Position, app.ActivationRefusal(Third)?.Kind);
        Assert.Equal(RefusalKind.Position, app.ActivationRefusal("pos-0000dead")?.Kind);
    }

    /// <summary>A table renamed for the length of a test: the transient store failure, without a hook in the product.</summary>
    static void Rename(Database db, string from, string to) => db.Write(_ =>
    {
        using var c = db.Cmd($"ALTER TABLE {from} RENAME TO {to}");
        return c.ExecuteNonQuery();
    });

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
    internal static SeatFacts Facts(Database db, string declared, string? id = null) => new()
    {
        Standing = id is null ? null : new OrgStore(db).Standing(id),
        DeclaredClass = declared,
        ProvenCell = new CellProofStore(db).HoldsProof(),
        ActiveBeyondLegacy = new OrgStore(db).ActivePositionsBeyondLegacy()
    };

    /// <summary>A register over <paramref name="db"/>, as the app fills its own, every position declared on <paramref name="declared"/>.</summary>
    internal static SeatRegister Register(Database db, string declared) =>
        new() { Facts = id => Facts(db, declared, id), Neighbours = () => new OrgStore(db).ActivePositionsBeyondLegacy() };

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

    /// <summary>
    /// A proven executor-cell row, as only the app may write one (none of its writers exists yet): this host — its identity as the
    /// app reads it, never a literal — this platform, release and build, unless the test names another.
    /// </summary>
    internal static void ProveCell(Database db, string? platform = null, string? build = null, string? machine = null,
        string? release = null)
    {
        var here = HostIdentity.This ?? throw new InvalidOperationException("this host's identity could not be read");
        db.Write(_ =>
        {
            using var c = db.Cmd("""
                INSERT INTO cell_proof(provider, platform, machine, os_release, build, profile_hash, proven_at)
                VALUES('test-cell', $platform, $machine, $release, $build, 'sha256-test', $at)
                """, ("$platform", platform ?? CellProofStore.ThisPlatform), ("$machine", machine ?? here.Machine),
                ("$release", release ?? here.OsRelease), ("$build", build ?? Versions.App),
                ("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
            return c.ExecuteNonQuery();
        });
    }

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

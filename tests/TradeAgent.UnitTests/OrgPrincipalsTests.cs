using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A THIRD POSITION IS A ROW, NOT A CONSTANT (<c>U-org-principals</c>): its own home, open-attempt slot, loop counters
/// and ledger authorship, keyed by its exact id and never folded into the chair — representable, never runnable on its
/// own. Each third position here is seeded by the TEST with raw SQL, as no writer of the chart exists yet.
///
/// <para>The class shares the vendor-override collection because (h) puts a probe runtime into <c>runtimes.json</c>
/// under the shared test home, which the tests that corrupt that file on purpose must not race.</para>
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class OrgPrincipalsTests : IDisposable
{
    /// <summary>An id shaped as the app would mint one: opaque, lower-case, never a name an agent chose.</summary>
    const string Third = "pos-7f3a90c1";

    const string Probe = "principals-probe";

    public void Dispose()
    {
        if (File.Exists(RuntimeCatalog.OverridePath)) File.Delete(RuntimeCatalog.OverridePath);
    }

    /// <summary>A position written straight into the chart, as only a test may: under Research's division, active.</summary>
    static void Seat(Database db, string id, string unit = OrgIds.Research, string status = OrgPositionStatus.Active) =>
        db.Write(_ =>
        {
            using var c = db.Cmd("""
                INSERT INTO org_position(id, unit_id, home_dir, status, created_at)
                VALUES($id, $unit, $home, $status, '2026-10-11T12:00:00.0000000Z')
                """, ("$id", id), ("$unit", unit), ("$home", $"org/{id}"), ("$status", status));
            return c.ExecuteNonQuery();
        });

    static long Attempts(Database db) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT COUNT(*) FROM ai_attempt");
        return Convert.ToInt64(c.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    });

    static WorkspaceContext Context(string role) => new(
        "Practice simulator", ConnectorIsPaper: true, "SIM-1", TradingMode.PAPER,
        ExecutionAvailable: true, null, new RiskPolicy { InstrumentAllowlist = ["ES"] },
        ConnectorIsBuiltInSimulator: false, Role: role);

    static string Root()
    {
        var root = Path.Combine(TestEnv.Home, $"principals-{Guid.NewGuid():n}");
        Directory.CreateDirectory(root);
        return root;
    }

    // ---- (a) homes ------------------------------------------------------------------------------------------------

    /// <summary>
    /// (a) A THIRD POSITION'S HOME IS ITS OWN. RED at base: <c>HomeDir</c> sent every id but Research's to the chair's
    /// folder, <c>workspace/agent</c>, so its canon overwrote the chair's and its CLI would resume the chair's session.
    /// </summary>
    [Fact]
    public void A_third_positions_home_is_its_own_not_the_chairs()
    {
        using var db = TestEnv.NewDb();
        Seat(db, Third);

        Assert.Equal($"org/{Third}", CouncilRoles.HomeDir(Third));
        Assert.Equal("agent", CouncilRoles.HomeDir(CouncilRoles.Operations));
        Assert.Equal("research", CouncilRoles.HomeDir(CouncilRoles.Research));

        var root = Root();
        var files = new AppFileManifest(Path.Combine(root, "app-files.tsv"));
        var home = WorkspaceBuilder.Build(Context(Third), root, files);
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "org", Third)), Path.GetFullPath(home));
        Assert.True(File.Exists(Path.Combine(home, Canon.CliFile)), $"no canon in {home}");
        Assert.False(File.Exists(Path.Combine(root, "agent", Canon.CliFile)), "the third position's canon went into the chair's home");
        Assert.True(files.Names(AppFileManifest.Key($"org/{Third}", Canon.CliFile)));

        // AND THE WALKER WALKS IT: a file in the third position's home is on the record the agent cannot edit.
        Directory.CreateDirectory(Path.Combine(home, "trading"));
        File.WriteAllText(Path.Combine(home, "trading", "notes.md"), "mine");
        new MaterialScanner(db, root, appFiles: files).Scan();
        Assert.Contains($"org/{Third}/trading/notes.md",
            new MaterialStore(db).Present(MaterialOrigin.Agent).Select(m => m.RelPath));
    }

    // ---- (b) the meter's slot -------------------------------------------------------------------------------------

    /// <summary>
    /// (b) A THIRD POSITION'S OPEN ATTEMPT IS KEYED BY ITS OWN ID. RED at base: <c>TurnMeter.Key</c> was
    /// <c>CouncilRoles.Or</c>, so the third position's launch took the chair's slot and the chair's open attempt — the
    /// one its grant names — became the third position's.
    ///
    /// <para><b>Re-posed by <c>U-thinking-seats-a</c>, the purpose kept:</b> the activation gate now admits a position beyond the
    /// legacy pair only on a seat that carries its capabilities, on a host with a proven executor cell, and never beside the chair's
    /// unconfined launch — so the two slots compared are two positions' on the harness, each its own and neither the chair's.</para>
    /// </summary>
    [Fact]
    public void A_third_positions_open_attempt_is_keyed_by_its_own_id()
    {
        const string fourth = "pos-2b8e41d0";
        using var db = TestEnv.NewDb();
        Seat(db, Third);
        Seat(db, fourth);
        ThinkingSeatsTests.ProveCell(db);
        var meter = ThinkingSeatsTests.Meter(db, _ => SeatClasses.Harness);
        using var a = meter.Attach(new ThinkingSeatsTests.Seated(SeatClasses.Harness), Third);
        using var b = meter.Attach(new ThinkingSeatsTests.Seated(SeatClasses.Harness), fourth);

        var third = meter.Begin("## Situation", role: Third).Id;
        var other = meter.Begin("## Situation", role: fourth).Id;
        Assert.NotNull(third);
        Assert.NotNull(other);
        Assert.NotEqual(third, other);

        Assert.Null(meter.OpenAttemptIdFor(CouncilRoles.Operations));
        Assert.Equal(third, meter.OpenAttemptIdFor(Third));
        Assert.Equal(other, meter.OpenAttemptIdFor(fourth));
        Assert.Equal(Third, new AiAttemptStore(db).Get(third!)!.Role);
    }

    // ---- (e) refused, never folded --------------------------------------------------------------------------------

    /// <summary>
    /// (e) AN UNKNOWN PRINCIPAL IS REFUSED, NOT FOLDED INTO THE CHAIR. RED at base: the meter admitted a launch under a
    /// name no position holds and keyed it as the chair's; <c>HomeDir</c> mapped any string to the chair's folder.
    /// </summary>
    [Fact]
    public void An_unknown_principal_is_refused_not_folded_into_the_chair()
    {
        using var db = TestEnv.NewDb();
        Seat(db, "pos-dormant", status: OrgPositionStatus.Dormant);
        var meter = new TurnMeter(db, () => 5m, live: new LiveAttempts());
        var chair = meter.Begin("## Situation", role: CouncilRoles.Operations).Id;

        foreach (var who in new[] { "nobody-here", "Research", "pos-dormant", AppPrincipals.Perception, AppPrincipals.Referee })
        {
            var admission = meter.Begin("## Situation", role: who);
            Assert.False(admission.Admitted, $"a launch under '{who}' was admitted");
            Assert.Null(admission.Id);
            Assert.Equal(chair, meter.OpenAttemptIdFor(CouncilRoles.Operations));
        }
        Assert.Equal(1, Attempts(db));

        // A HOME IS NEVER MAPPED FOR AN ID THAT FAILS THE CHECK: thrown, never the chair's folder.
        foreach (var bad in new[] { "", "Research", "../agent", "org/x", "a b", "con", "nul", "com1", "lpt9", "referee" })
            Assert.Throws<TradeAgentException>(() => CouncilRoles.HomeDir(bad));

        // A WAKE FOR A NAME READS AS THAT NAME; ONLY A ROW THAT NAMES NONE IS THE CHAIR'S (history).
        Assert.Equal("nobody-here", new MissionEvent { Id = "x", Kind = MissionEventKind.Review, Role = "nobody-here" }.For);
        Assert.Equal(CouncilRoles.Operations, new MissionEvent { Id = "y", Kind = MissionEventKind.Review }.For);
    }

    // ---- (g) ledger authorship ------------------------------------------------------------------------------------

    /// <summary>
    /// (g) A THIRD POSITION WRITES LEDGER ENTRIES UNDER ITS OWN ID. RED at base: the author floor was
    /// <c>CouncilRoles.IsKnown</c>, "is not a council role", though the column already references <c>org_position</c>.
    /// An app principal, a dormant position and nobody are still refused.
    /// </summary>
    [Fact]
    public async Task A_third_position_writes_ledger_entries_under_its_own_id()
    {
        using var db = TestEnv.NewDb();
        Seat(db, Third);
        Seat(db, "pos-dormant", status: OrgPositionStatus.Dormant);
        var ledger = new ResearchLedger(db);

        var entry = ledger.Add(Third, "attempt-t", LedgerKind.Lesson, "a lesson of my own", LedgerMark.Claim, null, null, null);
        Assert.Equal(Third, entry.Author);
        var revised = ledger.Revise(Third, "attempt-t2", entry.Id, "still mine", LedgerMark.Claim, "more seen", null, null);
        Assert.Equal(2, revised.Revision);
        Assert.Equal([entry.Id], ledger.List(Third, null, null, null, 10).Select(l => l.Entry.Id));

        foreach (var who in AppPrincipals.All.Append("").Append("pos-dormant").Append("nobody-here"))
        {
            var refused = Assert.Throws<TradeAgentException>(() => ledger.Add(who, "attempt-x", LedgerKind.Lesson,
                "a lesson", LedgerMark.Claim, null, null, null));
            Assert.Equal(ErrorCode.INVALID_REQUEST, refused.Code);
        }
        Assert.Throws<TradeAgentException>(() => ledger.List("pos-dormant", null, null, null, 10));

        // THE PIPE'S AUTHOR IS THE GATEWAY'S ONE ANSWER: the active position the grant proved, or nobody.
        await using var gateway = new TradingGateway(db, new FakeConnector(new FakeBroker()), new HealthRegistry());
        Assert.Equal(Third, gateway.ActivePositionOf(Third));
        Assert.Equal(CouncilRoles.Research, gateway.ActivePositionOf(CouncilRoles.Research));
        foreach (var who in AppPrincipals.All.Append("pos-dormant").Append("nobody-here").Append(null))
            Assert.Null(gateway.ActivePositionOf(who));
    }

    // ---- (h) no conversation before the activation gate -----------------------------------------------------------

    /// <summary>
    /// (h) A THIRD POSITION OPENS NO CONVERSATION BEFORE THE ACTIVATION GATE. RED at base:
    /// <c>AppHost.ConversationFor</c> opened one for any role string, in the chair's folder. The legacy pair still open
    /// theirs, through the host's own composition (<c>ComposeTheAi</c>) and a real runtime over a one-line program.
    /// </summary>
    [Fact]
    public async Task A_third_position_opens_no_conversation_before_the_activation_gate()
    {
        RuntimeCatalog.SaveOverrides([ProbeRuntime()]);
        using var db = TestEnv.NewDb();
        Seat(db, Third);
        var connector = new FakeConnector(new FakeBroker());
        await using (var before = new TradingGateway(db, connector, new HealthRegistry()))
            before.Update(s => s.SelectedRuntimeId = Probe);
        new OnboardingStore(db).Complete(OnboardingStep.SETUP_COMPLETE);
        await connector.ConnectAsync();

        var host = AppHost.Composed(db, connector, new AgentPresence());
        try
        {
            await host.StartTheAiAsync();
            var attempts = Attempts(db);

            var opened = host.ConversationFor(Third);
            Assert.True(opened is null, $"a conversation opened for {Third}, in {host.HomeFor(Third)}");
            Assert.Equal(attempts, Attempts(db));

            // THE SEAM REFUSES IT IN WORDS, AND ITS STANDING IS DERIVED FROM THE CHART, NEVER STORED.
            var standing = new OrgStore(db).Standing(Third);
            Assert.Equal(new PositionStanding(Third, OrgIds.Research, OrgPositionStatus.Active, OrgPositionKind.Member,
                IsLegacy: false, HomeDir: $"org/{Third}"), standing);
            // U-thinking-seats-a: the process-wide register is unset in a test process, which refuses a position beyond the legacy
            // pair; this host's own register declares the probe's CLI seat, whose shell is code — a refusal about the position.
            Assert.Contains($"'{Third}'", PositionActivation.Refusal(Third, standing), StringComparison.Ordinal);
            var gated = host.ActivationRefusal(Third);
            Assert.Equal(RefusalKind.Position, gated?.Kind);
            Assert.Contains("code needs a confined cell", gated!.Words, StringComparison.Ordinal);
            Assert.Contains("not an active position", host.ActivationRefusal("nobody-here")?.Words, StringComparison.Ordinal);
            Assert.Equal(RefusalKind.Position, host.ActivationRefusal("nobody-here")!.Kind);
            Assert.Null(new OrgStore(db).Standing(AppPrincipals.Perception));
            Assert.Equal(OrgPositionKind.Head, new OrgStore(db).Standing(CouncilRoles.Operations)!.Kind);
            db.Write(_ =>
            {
                using var c = db.Cmd("""
                    INSERT INTO org_unit(id, parent_id, kind, status, created_at)
                    VALUES('staff-1', 'org', 'staff', 'active', '2026-10-11T12:00:00.0000000Z')
                    """);
                return c.ExecuteNonQuery();
            });
            Seat(db, "pos-staff", unit: "staff-1");
            Assert.Equal(OrgPositionKind.Staff, new OrgStore(db).Standing("pos-staff")!.Kind);
            Assert.True(new OrgStore(db).Standing(CouncilRoles.Research)!.IsLegacy);
            Assert.Null(PositionActivation.Refusal(CouncilRoles.Operations, new OrgStore(db).Standing(CouncilRoles.Operations)));
            Assert.Null(PositionActivation.Refusal(CouncilRoles.Research, new OrgStore(db).Standing(CouncilRoles.Research)));

            // NO UNCONFINED NEIGHBOUR (U-thinking-seats-a; ORCHESTRATION § 2.3): while a position beyond the legacy pair is
            // active, the legacy pair on their unconfined CLI seats run confined too, or not there — refused, naming it.
            Assert.Null(host.ConversationFor(CouncilRoles.Operations));
            var neighbour = host.ActivationRefusal(CouncilRoles.Research);
            Assert.Equal(RefusalKind.HostOrLaunch, neighbour?.Kind);
            Assert.Contains($"'{Third}'", neighbour!.Words, StringComparison.Ordinal);

            // THE LEGACY PAIR ALONE KEEP THEIR EXISTING TWO-ROLE BEHAVIOUR.
            foreach (var other in new[] { Third, "pos-staff" })
                db.Write(_ =>
                {
                    using var c = db.Cmd("UPDATE org_position SET status='dormant' WHERE id=$id", ("$id", other));
                    return c.ExecuteNonQuery();
                });
            Assert.Same(host.Conversation, host.ConversationFor(CouncilRoles.Operations));
            Assert.NotNull(host.ConversationFor(CouncilRoles.Research));
        }
        finally
        {
            await host.Agent.StopAsync();
            await host.Gateway.DisposeAsync();
        }
    }

    // ---- (i) a host with no seam refuses, never folds -------------------------------------------------------------

    /// <summary>
    /// (i) A HOST WITH NO ACTIVATION SEAM REFUSES EVERY ID BUT THE LEGACY PAIR (ORCHESTRATION § 2.1: an id that is not an
    /// active position is refused with a reason, never folded into the chair). RED at fix base: the default
    /// <c>IMissionHost.ActivationRefusal</c> refused nobody, so a wake for an unknown id ran through the default
    /// <c>ConversationFor</c> — the host's single conversation — in the default home. The legacy pair still turn.
    /// </summary>
    [Fact]
    public async Task A_host_with_no_seam_refuses_a_non_legacy_wake_and_the_legacy_pair_still_turn()
    {
        using var db = TestEnv.NewDb();
        var events = new MissionEventStore(db);
        var conversation = new Recording();
        IMissionHost host = new NoSeamHost(events, conversation);
        var loop = new MissionLoop(host, new MissionOptions { ReviewEvery = TimeSpan.Zero });

        var earlier = DateTimeOffset.UtcNow.AddMinutes(-2);
        var strangers = MissionEventIds.ForRole(MissionEventIds.Review(earlier), "nobody-here");
        events.Raise(strangers, MissionEventKind.Review, earlier, role: "nobody-here");

        await loop.TurnAsync();
        Assert.True(conversation.Sent.Count == 0,
            $"a wake for 'nobody-here' was run through the host's default conversation: {conversation.Sent.Count} sent");
        Assert.Empty(((NoSeamHost)host).Wakes);
        // REFUSED FOR WHAT IT IS (U-org-wakes): the default seam's refusal is about the id, so its wake settles — consumed by
        // no launch, never by a turn — rather than being re-read on every pass.
        Assert.Equal(MissionEventStore.NoLaunch, events.Get(strangers)!.ConsumedBy);
        Assert.Equal(MissionEventDisposition.Refused, events.Get(strangers)!.Disposition);

        var chairs = MissionEventIds.ForRole(MissionEventIds.Review(earlier), CouncilRoles.Operations);
        events.Raise(chairs, MissionEventKind.Review, earlier.AddSeconds(1), role: CouncilRoles.Operations);

        await loop.TurnAsync();
        Assert.Single(conversation.Sent);
        var launched = Assert.Single(((NoSeamHost)host).Wakes);
        Assert.Contains(chairs, launched);
        Assert.DoesNotContain(strangers, launched);

        // THE REFUSAL IS A REASON, naming the id; a third position with no seam to ask is refused the same way.
        foreach (var stranger in new[] { "nobody-here", Third })
        {
            var refusal = host.ActivationRefusal(stranger);
            Assert.NotNull(refusal);
            Assert.Equal(RefusalKind.Position, refusal.Kind);
            Assert.Contains($"'{stranger}'", refusal.Words, StringComparison.Ordinal);
        }
        Assert.Null(host.ActivationRefusal(CouncilRoles.Operations));
        Assert.Null(host.ActivationRefusal(CouncilRoles.Research));
    }

    // ---- (j) the canon's paths resolve from the position's own home -----------------------------------------------

    /// <summary>
    /// (j) EVERY PATH A POSITION'S CANON AND GUIDE NAME RESOLVES FROM ITS OWN HOME (VISION § 6.7: the canon's facts are
    /// system truth generated from what the app grants). RED at fix base: the inbox was the prose <c>../inbox</c>, which
    /// from <c>org/&lt;id&gt;</c> is <c>workspace/org/inbox</c> — a folder that is not the owner's. Every other path read is
    /// inside the home; the legacy pair's homes resolve to the same real inbox.
    /// </summary>
    [Fact]
    public void Every_path_a_positions_canon_names_resolves_from_its_own_home_and_the_inbox_is_the_owners()
    {
        var root = Root();
        var inbox = Path.GetFullPath(Path.Combine(root, MaterialScanner.InboxDir));

        foreach (var role in new[] { Third, CouncilRoles.Operations, CouncilRoles.Research })
        {
            var files = new AppFileManifest(Path.Combine(root, $"app-files-{role}.tsv"));
            var home = WorkspaceBuilder.Build(Context(role), root, files);

            foreach (var runtime in WorkspaceBuilder.RuntimesOf(role))
            {
                var reach = AgentReach.For(role, runtime);
                var named = reach.Reads.Where(r => r != AgentReach.OwnFolder)
                    .Concat(Canon.Render(Context(role), reach).Reads)
                    .Concat(Canon.Guide(Context(role), reach).Reads)
                    .Distinct().ToList();

                if (runtime == RuntimeClass.Cli)
                {
                    Assert.True(reach.ReadsInbox, $"{role}·{runtime} reads no inbox");
                    var path = Assert.Single(named, p => p.Split('/').Contains(".."));
                    Assert.True(Path.GetFullPath(Path.Combine(home, path)) == inbox,
                        $"{role}'s canon names `{path}` for the owner's inbox, which from {home} is "
                        + $"{Path.GetFullPath(Path.Combine(home, path))}, not {inbox}");
                    Assert.Contains($"`{path}`", File.ReadAllText(Path.Combine(home, Canon.CliFile)), StringComparison.Ordinal);
                    Assert.Contains($"`{path}`", File.ReadAllText(Path.Combine(home, Canon.GuideFile)), StringComparison.Ordinal);
                }

                // EVERY OTHER PATH IT READS IS INSIDE ITS OWN HOME.
                var homeFull = Path.GetFullPath(home) + Path.DirectorySeparatorChar;
                foreach (var p in named.Where(p => !p.Split('/').Contains("..")))
                    Assert.StartsWith(homeFull, Path.GetFullPath(Path.Combine(home, p)) + Path.DirectorySeparatorChar);
            }

            // AND THE TURN'S OWN BLOCK NAMES THE SAME INBOX, from the same home.
            var told = new MissionSituation { LocalTime = DateTimeOffset.Now, Mode = "PAPER", Role = role, NewMaterial = ["a.pdf"] };
            Assert.Contains($"`{AgentReach.InboxFrom(role)}`", told.Text(), StringComparison.Ordinal);
            Assert.Equal(inbox, Path.GetFullPath(Path.Combine(home, AgentReach.InboxFrom(role))));
        }
    }

    /// <summary>A loop's world with the wake queue and one conversation, and NO activation seam: every default kept.</summary>
    sealed class NoSeamHost(MissionEventStore events, IAgentConversation conversation) : IMissionHost
    {
        public List<IReadOnlyList<string>> Wakes { get; } = [];

        public IAgentConversation? Conversation => conversation;
        public MissionEventStore? Events => events;
        public string AgentHome { get; } = Path.Combine(TestEnv.Home, $"no-seam-{Guid.NewGuid():n}");
        public bool InboxChangedSinceLastPass => false;
        public Task ScanAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<MissionSituation> SituationAsync(CancellationToken ct) =>
            Task.FromResult(new MissionSituation { LocalTime = DateTimeOffset.Now, Mode = "PAPER" });

        public AiAdmission BeginTurn(string prompt, IReadOnlyList<string> wakes)
        {
            Wakes.Add([.. wakes]);
            return AiAdmission.NoLedger;
        }
    }

    /// <summary>A conversation that records what it was sent and ends each turn at once.</summary>
    sealed class Recording : IAgentConversation
    {
        public List<string> Sent { get; } = [];
        public bool Busy => false;
        public IReadOnlyList<ChatTurn> History => [];

        public event Action<ChatTurn>? TurnAdded;
        public event Action<string>? Delta;
        public event Action? StateChanged;
        public event Action<AgentTurnEnded>? TurnEnded;

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string m, CancellationToken ct = default) => SendMissionAsync(m, ct);

        public Task SendMissionAsync(string message, CancellationToken ct = default)
        {
            Sent.Add(message);
            TurnAdded?.Invoke(new ChatTurn(ChatRole.Ai, "done", DateTimeOffset.UtcNow));
            Delta?.Invoke("");
            StateChanged?.Invoke();
            TurnEnded?.Invoke(new AgentTurnEnded(0, TimeSpan.Zero, "done", DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }

        public void Queue(string message) { }
        public IReadOnlyList<string> TakeTyped() => [];
        public Task CancelAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
    }

    static RuntimeManifest ProbeRuntime()
    {
        var dir = Path.Combine(TestEnv.Home, $"principals-probe-{Guid.NewGuid():n}");
        Directory.CreateDirectory(dir);

        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(dir, "probe.ps1");
            File.WriteAllText(script,
                "if ($args.Count -gt 0 -and $args[0] -eq 'version') { 'principals-probe 1.0.0'; exit 0 }\r\n"
                + "'Nothing needs doing this turn.'\r\nexit 0\r\n");
            string[] run = ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script];
            return new RuntimeManifest
            {
                Id = Probe,
                DisplayName = "Principals probe",
                Executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                VersionArgs = [.. run, "version"],
                ExecArgs = [.. run, "{prompt}"],
                ResumeArgs = [.. run, "{prompt}"]
            };
        }

        var sh = Path.Combine(dir, "probe.sh");
        File.WriteAllText(sh,
            "#!/bin/sh\n[ \"$1\" = \"version\" ] && { echo 'principals-probe 1.0.0'; exit 0; }\n"
            + "echo 'Nothing needs doing this turn.'\n");
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new RuntimeManifest
        {
            Id = Probe,
            DisplayName = "Principals probe",
            Executable = sh,
            VersionArgs = ["version"],
            ExecArgs = ["{prompt}"],
            ResumeArgs = ["{prompt}"]
        };
    }
}

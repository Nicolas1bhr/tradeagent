using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ONE SCHEDULER, ONE ROLE PER TURN, ONE PROCESS AT A TIME (<c>docs/COUNCIL.md</c>, the
/// <c>U-council-thin</c> line: "two role workspaces run SERIALLY by one app instance").
///
/// <para>Before this the loop asked <c>MissionEventStore.Due</c> for every due event, handed the
/// whole batch to the ONE conversation the app had, and charged the ONE cap. Two roles could not
/// exist under that: a fact that woke Research was consumed by the chair's turn, Research never ran,
/// and there was no reading of the day's spending that could say which of them had spent it.</para>
///
/// <para>Serial is not a simplification to be undone later — it is what makes the leases in
/// <c>U-council-concurrent</c> unnecessary today and the relay's crash cases tractable. Two agent
/// processes against one trading account is a race with real money in it.</para>
/// </summary>
public class CouncilLoopTests
{
    // ---- the world ---------------------------------------------------------------------------

    /// <summary>
    /// How many agent processes are alive at once, across every role. The council's whole claim is
    /// that this never goes above one, so it is measured rather than assumed: each conversation
    /// increments it for the length of its turn and the test reads the high-water mark.
    /// </summary>
    sealed class Concurrency
    {
        readonly Lock _gate = new();
        int _live;
        public int Peak { get; private set; }

        public IDisposable Enter()
        {
            lock (_gate) { _live++; Peak = Math.Max(Peak, _live); }
            return new Exit(this);
        }

        sealed class Exit(Concurrency owner) : IDisposable
        {
            public void Dispose() { lock (owner._gate) owner._live--; }
        }
    }

    /// <summary>One role's conversation: what it was told, and while it was the only one running.</summary>
    sealed class RoleConversation(string role, Concurrency concurrency) : IAgentConversation
    {
        readonly List<ChatTurn> _history = [];

        public string Role => role;
        public List<string> Sent { get; } = [];
        public bool Busy { get; private set; }

        /// <summary>What the agent does during its turn — writing a file into its own <c>out/</c>.</summary>
        public Action? OnTurn { get; set; }
        public IReadOnlyList<ChatTurn> History => _history.ToArray();

        public event Action<ChatTurn>? TurnAdded;
        public event Action<string>? Delta;
        public event Action? StateChanged;
        public event Action<AgentTurnEnded>? TurnEnded;

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string message, CancellationToken ct = default) => Run(message, ct);
        public Task SendMissionAsync(string message, CancellationToken ct = default) => Run(message, ct);

        async Task Run(string message, CancellationToken ct)
        {
            Sent.Add(message);
            Busy = true;
            StateChanged?.Invoke();
            using (concurrency.Enter()) { OnTurn?.Invoke(); await Task.Delay(2, ct); }

            var turn = new ChatTurn(ChatRole.Ai, $"{role} did its turn.", DateTimeOffset.UtcNow);
            _history.Add(turn);
            TurnAdded?.Invoke(turn);
            Busy = false;
            StateChanged?.Invoke();
            Delta?.Invoke("");
            TurnEnded?.Invoke(new AgentTurnEnded(0, TimeSpan.FromMilliseconds(3), turn.Text, DateTimeOffset.UtcNow));
        }

        public IReadOnlyList<string> TakeTyped() => [];
        public Task StopAsync() => Task.CompletedTask;
        public Task CancelAsync() => Task.CompletedTask;
        public void Queue(string message) { }
    }

    /// <summary>
    /// The app, with a conversation, a folder and a spending reading per role, and the REAL launch
    /// ledger behind <see cref="BeginTurn"/> — so the one-transaction consumption in
    /// <see cref="AiAttemptStore.Begin"/> is the thing under test rather than a stand-in for it.
    /// </summary>
    sealed class CouncilHost : IMissionHost
    {
        readonly Database _db;
        readonly string _tag = Guid.NewGuid().ToString("n")[..8];
        int _attempts;

        readonly CouncilRelay _relay;

        public CouncilHost(Database db, string root, Concurrency concurrency)
        {
            _db = db;
            Root = root;
            Events = new MissionEventStore(db);
            _relay = new CouncilRelay(db, HomeFor);
            foreach (var role in CouncilRoles.All)
            {
                Conversations[role] = new RoleConversation(role, concurrency);
                Directory.CreateDirectory(Path.Combine(WorkspaceBuilder.HomeOf(root, role), ".tradeagent"));
                Directory.CreateDirectory(Path.Combine(WorkspaceBuilder.HomeOf(root, role), WorkspaceBuilder.OutDir));
            }
        }

        public string Root { get; }
        public Dictionary<string, RoleConversation> Conversations { get; } = [];
        public Dictionary<string, AiSpendToday> Spending { get; } = [];

        /// <summary>Every launch this host recorded, in order, as (role, prompt).</summary>
        public List<(string Role, string Prompt)> Opened { get; } = [];

        public MissionEventStore? Events { get; }

        public IAgentConversation? Conversation => Conversations[CouncilRoles.Operations];
        public IAgentConversation? ConversationFor(string role) => Conversations[role];

        public string AgentHome => WorkspaceBuilder.HomeOf(Root, CouncilRoles.Operations);
        public string HomeFor(string role) => WorkspaceBuilder.HomeOf(Root, role);

        public bool InboxChangedSinceLastPass => false;
        public Task ScanAsync(CancellationToken ct) => Task.CompletedTask;

        public AiSpendToday Spend => AiSpendToday.NotMetered;

        public AiSpendToday SpendFor(string role) =>
            Spending.TryGetValue(role, out var s) ? s : AiSpendToday.NotMetered;

        public Task<MissionSituation> SituationAsync(CancellationToken ct) =>
            Task.FromResult(new MissionSituation());

        public Task<MissionSituation> SituationAsync(string role, CancellationToken ct) =>
            Task.FromResult(new MissionSituation { Role = role, Spend = SpendFor(role) });

        public string? BeginTurn(string prompt, IReadOnlyList<string> wakes) =>
            BeginTurn(prompt, wakes, CouncilRoles.Default);

        public string? BeginTurn(string prompt, IReadOnlyList<string> wakes, string role)
        {
            Opened.Add((role, prompt));
            var id = $"turn-{_tag}-{++_attempts}";
            new AiAttemptStore(_db).Begin(
                new AiAttempt { Id = id, StartedAt = DateTimeOffset.UtcNow, Role = role }, wakes);
            return id;
        }

        /// <summary>The REAL relay, so what the chair is handed is what the app actually publishes.</summary>
        public void Relay(string role, string? attempt) => _relay.Run(role, attempt);

        public MissionDelivery? Delivered(string publicationId)
        {
            var p = new PublicationStore(_db).Get(publicationId);
            return p is null ? null : new MissionDelivery(p.Id, p.Kind, p.Role, p.Content);
        }
    }

    static (Database Db, string Root) Workspace()
    {
        var root = Path.Combine(TestEnv.Home, $"council-loop-{Guid.NewGuid():n}");
        Directory.CreateDirectory(Path.Combine(root, MaterialScanner.InboxDir));
        return (TestEnv.NewDb(), root);
    }

    /// <summary>The heartbeat off, so every turn below has a named cause the test put there.</summary>
    static readonly MissionOptions NoHeartbeat = new() { ReviewEvery = TimeSpan.Zero };

    /// <summary>A metered reading with a share, so the two admission gates can be posed separately.</summary>
    static AiSpendToday Reading(string role, decimal spent, decimal cap, decimal share) => new()
    {
        Metered = true,
        Role = role,
        Spent = spent,
        RoleSpent = spent,
        Cap = cap,
        RoleCap = cap * share,
        NextTurnReservation = 0.10m,
        Currency = "USD",
        Turns = 1,
        CanPrice = true,
        ResumesAt = DateTimeOffset.Now.AddHours(1)
    };

    /// <summary>
    /// MIDDAY, ON A FIXED CLOCK. Every wait below is the QUEUE'S MINIMUM, and the day's renewal is
    /// due at local midnight — so a suite run at 23:55 would measure the renewal rather than the
    /// thing under test. Pinning the hour is not loosening the assertion; it is asking it of a clock
    /// the test controls.
    /// </summary>
    static DateTimeOffset Noon() => new(DateTime.Today.AddHours(12), DateTimeOffset.Now.Offset);

    // ---- the property ------------------------------------------------------------------------

    /// <summary>
    /// RED FIRST, AND THIS IS THE UNIT'S ITEM 2. Two due wakes, one for each role: two turns, in
    /// the order the queue was waiting in, on two different conversations, and never both at once.
    ///
    /// It was red for a reason with nothing subtle in it: <c>TurnAsync</c> asked
    /// <c>events.Due(now)</c> for EVERY due event and handed the batch to <c>_host.Conversation</c>.
    /// The chair's turn therefore consumed the Research Director's wake as well as its own, the
    /// research conversation was never sent anything at all, and the second role's only route to a
    /// turn was a fact that happened to arrive after the chair had gone quiet.
    ///
    /// The half of the assertion that is easy to miss is the one in the middle: after the FIRST
    /// turn, the other role's wake is still unconsumed. That is what "the first turn ends before the
    /// second starts" means where it matters — not two threads, but one wake per launch record.
    /// </summary>
    [Fact]
    public async Task Two_due_wakes_one_per_role_are_two_turns_and_never_two_at_once()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var concurrency = new Concurrency();
        var host = new CouncilHost(db, root, concurrency);
        var events = host.Events!;
        var loop = new MissionLoop(host, NoHeartbeat);

        var earlier = DateTimeOffset.UtcNow.AddMinutes(-2);
        events.Raise(MissionEventIds.Fill("EXEC-1"), MissionEventKind.Fill, earlier,
            role: CouncilRoles.Operations);
        var researchWake = MissionEventIds.ForRole(MissionEventIds.Review(earlier), CouncilRoles.Research);
        events.Raise(researchWake, MissionEventKind.Review, earlier.AddSeconds(1),
            role: CouncilRoles.Research);

        await loop.TurnAsync();

        // ONE ROLE'S WORK, AND THE OTHER'S WAKE UNTOUCHED.
        Assert.Single(host.Conversations[CouncilRoles.Operations].Sent);
        Assert.Empty(host.Conversations[CouncilRoles.Research].Sent);
        Assert.False(events.Get(researchWake)!.Consumed,
            "the chair's turn consumed the Research Director's wake");

        await loop.TurnAsync();

        Assert.Single(host.Conversations[CouncilRoles.Operations].Sent);
        Assert.Single(host.Conversations[CouncilRoles.Research].Sent);
        Assert.True(events.Get(researchWake)!.Consumed);

        // Two launch records, one per role, each charged to the role that ran.
        Assert.Equal([CouncilRoles.Operations, CouncilRoles.Research], host.Opened.Select(o => o.Role));

        // ONE PROCESS AT A TIME, measured rather than assumed.
        Assert.Equal(1, concurrency.Peak);

        // And the turn was told which role it is, so it need not infer it from its own folder.
        Assert.Contains($"You are the {CouncilRoles.Title(CouncilRoles.Research)}",
            host.Opened[1].Prompt);
    }

    /// <summary>
    /// THE SHARE IS A SECOND GATE AND THIS IS THE MUTANT'S TEST. A role that has spent its slice of
    /// the day is STEPPED OVER, and the role behind it in the queue works.
    ///
    /// Drop <c>RoleAdmitsAnotherTurn</c> from <see cref="AiSpendToday.AdmitsAnotherTurn"/> — the
    /// mutant — and nothing is over-spent, because the owner's ceiling still holds. What is lost is
    /// the council: the chair, whose wake is older, takes this turn and every turn after it, and the
    /// Research Director is never scheduled at all until midnight. That is the failure a share
    /// exists to prevent, and it is invisible to any assertion about the day's total.
    /// </summary>
    [Fact]
    public async Task A_role_that_has_spent_its_share_is_stepped_over_and_the_other_one_works()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var concurrency = new Concurrency();
        var host = new CouncilHost(db, root, concurrency);
        var events = host.Events!;
        var loop = new MissionLoop(host, NoHeartbeat);

        // The chair has spent its whole half of a 5.00 day. The council has 2.50 left, all of it
        // Research's, so the owner's ceiling has plenty of room and only the share is in the way.
        host.Spending[CouncilRoles.Operations] = Reading(CouncilRoles.Operations, 2.50m, 5.00m, 0.5m);
        host.Spending[CouncilRoles.Research] = Reading(CouncilRoles.Research, 0m, 5.00m, 0.5m);

        Assert.True(host.SpendFor(CouncilRoles.Operations).AdmitsGlobally,
            "the day's ceiling must have room, or this is a test of the cap and not of the share");
        Assert.False(host.SpendFor(CouncilRoles.Operations).AdmitsAnotherTurn);

        var earlier = DateTimeOffset.UtcNow.AddMinutes(-2);
        events.Raise(MissionEventIds.Fill("EXEC-1"), MissionEventKind.Fill, earlier,
            role: CouncilRoles.Operations);
        events.Raise(MissionEventIds.ForRole(MissionEventIds.Review(earlier), CouncilRoles.Research),
            MissionEventKind.Review, earlier.AddSeconds(1), role: CouncilRoles.Research);

        await loop.TurnAsync();

        Assert.Empty(host.Conversations[CouncilRoles.Operations].Sent);
        Assert.Single(host.Conversations[CouncilRoles.Research].Sent);
        Assert.Equal([CouncilRoles.Research], host.Opened.Select(o => o.Role));
    }

    /// <summary>
    /// EVERY ROLE OVER ITS SHARE IS A WAIT, NOT A SPIN, and the card says whose share ran out rather
    /// than sending the owner to the daily limit on the Safety page — which is not the number that
    /// needs changing.
    /// </summary>
    [Fact]
    public async Task With_every_share_spent_the_loop_waits_and_the_card_names_the_role()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var host = new CouncilHost(db, root, new Concurrency());
        var loop = new MissionLoop(host, NoHeartbeat, delay: (_, ct) => Task.Delay(1, ct));

        foreach (var role in CouncilRoles.All)
            host.Spending[role] = Reading(role, 2.50m, 5.00m, 0.5m);

        var now = DateTimeOffset.UtcNow;
        foreach (var role in CouncilRoles.All)
            host.Events!.Raise(MissionEventIds.ForRole(MissionEventIds.Review(now), role),
                MissionEventKind.Review, now, role: role);

        loop.Start();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (loop.Status.WaitingFor is null && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
        var status = loop.Status;
        await loop.PauseAsync();

        foreach (var role in CouncilRoles.All) Assert.Empty(host.Conversations[role].Sent);
        Assert.Equal(MissionState.Waiting, status.State);
        Assert.Equal($"the {CouncilRoles.Title(CouncilRoles.Operations)}'s share of the day to reset",
            status.WaitingFor);
        Assert.StartsWith($"{CouncilRoles.Title(CouncilRoles.Operations)}: waiting",
            DashboardPage.MissionSentence(status));
    }

    /// <summary>
    /// THE WHOLE ROUND TRIP, AND ITEM 4's ORDERING: Research writes a report, the app publishes and
    /// delivers it, and the chair's next turn is handed the owner's words FIRST and the report
    /// second — with its id, so what the chair decides can be tied back to the artifact.
    ///
    /// RED FIRST. Before this item a report reached the chair as a file it had to notice and open:
    /// the Situation had no deliveries in it at all, so the turn that was CHARGED for reading the
    /// report was not told what it said.
    ///
    /// The order is the guard, and it is the mutant's target. <c>docs/COUNCIL.md</c> round 4: "Owner
    /// text enters Operations' agenda first, with receipt, disposition and deadline." Put another
    /// agent's report above the person waiting for an answer and the chair spends its turn on the
    /// report — which is the failure the sentence exists to prevent, and which no assertion about
    /// the presence of either block would catch.
    /// </summary>
    [Fact]
    public async Task Research_reports_and_the_chairs_next_turn_reads_the_owner_first_then_the_report()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var host = new CouncilHost(db, root, new Concurrency());
        var events = host.Events!;
        var loop = new MissionLoop(host, NoHeartbeat);

        foreach (var role in CouncilRoles.All) host.Spending[role] = Reading(role, 0.40m, 5.00m, 0.5m);

        const string report = "The 1-minute bars have a 40-minute gap on 2026-03-09.\nIt is in the archive, not the download.";
        host.Conversations[CouncilRoles.Research].OnTurn = () => File.WriteAllText(
            Path.Combine(host.HomeFor(CouncilRoles.Research), WorkspaceBuilder.OutDir, "report-1.md"),
            report);

        var earlier = DateTimeOffset.UtcNow.AddMinutes(-2);
        events.Raise(MissionEventIds.ForRole(MissionEventIds.Review(earlier), CouncilRoles.Research),
            MissionEventKind.Review, earlier, role: CouncilRoles.Research);

        await loop.TurnAsync();                      // Research works, and the app publishes what it left

        var published = Assert.Single(new PublicationStore(db).By(CouncilRoles.Research));
        Assert.Equal(report, published.Content);

        // The owner types while that is happening. Their words go to the chair, and they go first.
        events.RecordOwnerMessage("how is the data coming along?", DateTimeOffset.UtcNow);

        await loop.TurnAsync();                      // the chair

        var prompt = Assert.Single(host.Conversations[CouncilRoles.Operations].Sent);
        var owner = prompt.IndexOf("how is the data coming along?", StringComparison.Ordinal);
        var delivery = prompt.IndexOf("A report from the Research Director", StringComparison.Ordinal);
        var whoAmI = prompt.IndexOf($"You are the {CouncilRoles.Title(CouncilRoles.Operations)}",
            StringComparison.Ordinal);

        Assert.True(owner >= 0, "the chair was not told what the owner typed");
        Assert.True(delivery >= 0, "the chair was not told what the report said");
        Assert.True(owner < delivery, "another agent's report was put above the owner's words");
        Assert.True(delivery < whoAmI, "the delivery was rendered below the state lines");

        // The text, the id and the file, so a decision can be tied back to the artifact it was about.
        Assert.Contains(report.Split('\n')[0], prompt);
        Assert.Contains($"{WorkspaceBuilder.InDir}/{published.Id}.md", prompt);
        Assert.True(File.Exists(Path.Combine(host.HomeFor(CouncilRoles.Operations),
            WorkspaceBuilder.InDir, $"{published.Id}.md")));

        // And its own share of the day, which is not the day's total.
        Assert.Contains($"Your share of that limit, as the {CouncilRoles.Title(CouncilRoles.Operations)}",
            prompt);
    }

    /// <summary>
    /// EACH ROLE ASKS TO BE WOKEN IN ITS OWN <c>next.json</c>, and the wake that answers it is that
    /// role's. One shared file would let whichever role ran last decide when the other works, and a
    /// self-wake with no role on it would be answered by the chair — one role asking to be left
    /// alone and the other one waking up.
    /// </summary>
    [Fact]
    public async Task A_roles_request_to_be_left_alone_is_read_from_its_own_folder()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var host = new CouncilHost(db, root, new Concurrency());
        var now = Noon();
        var loop = new MissionLoop(host, NoHeartbeat, now: () => now);

        File.WriteAllText(
            Path.Combine(host.HomeFor(CouncilRoles.Research), ".tradeagent", "next.json"),
            """{"after_seconds": 600}""");

        host.Events!.Raise(MissionEventIds.ForRole(MissionEventIds.Review(now), CouncilRoles.Research),
            MissionEventKind.Review, now, role: CouncilRoles.Research);

        var wait = await loop.TurnAsync();

        Assert.InRange(wait, TimeSpan.FromSeconds(590), TimeSpan.FromSeconds(600));
        var self = host.Events.OfKind(MissionEventKind.Self).Single();
        Assert.Equal(CouncilRoles.Research, self.For);

        // Consumed, so the request does not become "every turn is ten minutes apart, for ever".
        Assert.False(File.Exists(
            Path.Combine(host.HomeFor(CouncilRoles.Research), ".tradeagent", "next.json")));
    }
}

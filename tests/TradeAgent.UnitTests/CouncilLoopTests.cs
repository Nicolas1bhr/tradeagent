using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ONE SCHEDULER, ONE ROLE PER TURN, AND AT MOST ONE TURN PER ROLE (<c>docs/COUNCIL.md</c>, the
/// <c>U-council-thin</c> line: "two role workspaces run SERIALLY by one app instance", and
/// <c>U-council-concurrent</c>, which makes every slot that sentence allowed a slot per role).
///
/// <para>Before this the loop asked <c>MissionEventStore.Due</c> for every due event, handed the
/// whole batch to the ONE conversation the app had, and charged the ONE cap. Two roles could not
/// exist under that: a fact that woke Research was consumed by the chair's turn, Research never ran,
/// and there was no reading of the day's spending that could say which of them had spent it.</para>
///
/// <para><b>What is no longer claimed, and what still is.</b> One process at a time was the whole of
/// the safety argument and it is not any more: a role's turn may overlap the other role's, each under
/// its own lease, and the tests below measure that rather than assume it. What has NOT changed is
/// what a role may DO — only the Operations Director places orders, the gateway's dispatch gate is
/// still a mutex, and two agent processes against one trading account is a race with real money in
/// it precisely because the account is reached through that one gate and not through the loop.</para>
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

    /// <summary>
    /// One role's conversation: what it was told, and while it was the only one running.
    ///
    /// <para>It reports itself to an <see cref="AgentPresence"/> for the length of its turn, exactly
    /// as <c>AgentSession</c> does, because the inbox attestation is worth nothing unless the
    /// scanner and every agent process are looking at one register.</para>
    /// </summary>
    sealed class RoleConversation(string role, Concurrency concurrency, AgentPresence? presence = null)
        : IAgentConversation
    {
        readonly List<ChatTurn> _history = [];

        public string Role => role;
        public List<string> Sent { get; } = [];
        public bool Busy { get; private set; }

        /// <summary>How the turn ends. Non-zero is a failed turn, which is what the backoff counts.</summary>
        public int Exit { get; set; }

        /// <summary>How many times this conversation has been told to start a fresh CLI session.</summary>
        public int Stops { get; private set; }

        /// <summary>What the agent does during its turn — writing a file into its own <c>out/</c>.</summary>
        public Action? OnTurn { get; set; }
        public IReadOnlyList<ChatTurn> History
        {
            get { lock (_history) return _history.ToArray(); }
        }

        public event Action<ChatTurn>? TurnAdded;
        public event Action<string>? Delta;
        public event Action? StateChanged;
        public event Action<AgentTurnEnded>? TurnEnded;

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string message, CancellationToken ct = default) => Run(message, ct);
        public Task SendMissionAsync(string message, CancellationToken ct = default) => Run(message, ct);

        async Task Run(string message, CancellationToken ct)
        {
            lock (Sent) Sent.Add(message);
            Busy = true;
            StateChanged?.Invoke();
            using (concurrency.Enter())
            using (presence?.Enter() ?? Nothing)
            {
                OnTurn?.Invoke();
                await Task.Delay(2, ct);
            }

            var turn = new ChatTurn(ChatRole.Ai, $"{role} did its turn.", DateTimeOffset.UtcNow);
            lock (_history) _history.Add(turn);
            TurnAdded?.Invoke(turn);
            Busy = false;
            StateChanged?.Invoke();
            Delta?.Invoke("");
            TurnEnded?.Invoke(new AgentTurnEnded(Exit, TimeSpan.FromMilliseconds(3), turn.Text,
                DateTimeOffset.UtcNow));
        }

        /// <summary>A handle that closes nothing, for a conversation with no register behind it.</summary>
        static IDisposable Nothing { get; } = new NoWindow();

        sealed class NoWindow : IDisposable
        {
            public void Dispose() { }
        }

        public IReadOnlyList<string> TakeTyped() => [];

        public Task StopAsync()
        {
            Stops++;
            return Task.CompletedTask;
        }
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

        public CouncilHost(Database db, string root, Concurrency concurrency,
            AgentPresence? presence = null)
        {
            _db = db;
            Root = root;
            Presence = presence;
            Events = new MissionEventStore(db);
            Boundaries = new CouncilBoundaries(db, () => BoundaryWindow);
            _relay = new CouncilRelay(db, HomeFor, boundaries: Boundaries);
            foreach (var role in CouncilRoles.All)
            {
                Conversations[role] = new RoleConversation(role, concurrency, presence);
                Directory.CreateDirectory(Path.Combine(WorkspaceBuilder.HomeOf(root, role), ".tradeagent"));
                Directory.CreateDirectory(Path.Combine(WorkspaceBuilder.HomeOf(root, role), WorkspaceBuilder.OutDir));
            }
        }

        public string Root { get; }
        public Dictionary<string, RoleConversation> Conversations { get; } = [];
        public Dictionary<string, AiSpendToday> Spending { get; } = [];

        /// <summary>
        /// RUN AT THE LAST MOMENT BEFORE A TURN IS ADMITTED, so a test can hold two callers there
        /// and let them go together. It is called from <see cref="SituationAsync(string,
        /// CancellationToken)"/> — the last thing the loop does before it takes the role's lease —
        /// which is what makes the race below a race rather than a sequence.
        /// </summary>
        public Action<string>? BeforeLaunch { get; set; }

        /// <summary>Every launch this host recorded, in order, as (role, prompt).</summary>
        public List<(string Role, string Prompt)> Opened { get; } = [];

        public MissionEventStore? Events { get; }

        /// <summary>
        /// The REAL boundary ledger, so the loop's deadline sweep is the thing under test rather than a
        /// stand-in for it. Its window is a minute, which is a day the test can run in milliseconds.
        /// </summary>
        public CouncilBoundaries? Boundaries { get; }

        public IAgentConversation? Conversation => Conversations[CouncilRoles.Operations];
        public IAgentConversation? ConversationFor(string role) => Conversations[role];

        public string AgentHome => WorkspaceBuilder.HomeOf(Root, CouncilRoles.Operations);
        public string HomeFor(string role) => WorkspaceBuilder.HomeOf(Root, role);

        /// <summary>
        /// The register every conversation here reports itself to, or null for the tests that are
        /// not about the attestation at all — and then nothing is scanned and nothing is claimed.
        /// </summary>
        public AgentPresence? Presence { get; }

        /// <summary>Run inside the material pass, so a test can hold one open across another turn.</summary>
        public Action? WhileScanning { get; set; }

        /// <summary>How many complete material passes this host has run.</summary>
        public int Passes;

        DateTimeOffset? _lastPassAt;

        public bool InboxChangedSinceLastPass =>
            Presence is not null && MissionInbox.ChangedSince(Root, _lastPassAt);

        public Task ScanAsync(CancellationToken ct)
        {
            if (Presence is null) return Task.CompletedTask;

            // Captured BEFORE the walk, so this host's idea of the last pass is never later than the
            // scanner's own. Erring early costs a spare pass; erring late costs an attestation.
            _lastPassAt = DateTimeOffset.UtcNow;
            Interlocked.Increment(ref Passes);
            WhileScanning?.Invoke();
            new MaterialScanner(_db, Root, Presence.NoneSince).Scan(ct);
            return Task.CompletedTask;
        }

        public AiSpendToday Spend => AiSpendToday.NotMetered;

        public AiSpendToday SpendFor(string role) =>
            Spending.TryGetValue(role, out var s) ? s : AiSpendToday.NotMetered;

        public Task<MissionSituation> SituationAsync(CancellationToken ct) =>
            Task.FromResult(new MissionSituation());

        public Task<MissionSituation> SituationAsync(string role, CancellationToken ct)
        {
            BeforeLaunch?.Invoke(role);
            return Task.FromResult(new MissionSituation { Role = role, Spend = SpendFor(role) });
        }

        public AiAdmission BeginTurn(string prompt, IReadOnlyList<string> wakes) =>
            BeginTurn(prompt, wakes, CouncilRoles.Default);

        public AiAdmission BeginTurn(string prompt, IReadOnlyList<string> wakes, string role)
        {
            lock (Opened) Opened.Add((role, prompt));

            // THE MINTED ID IS TAKEN, exactly as <see cref="TurnMeter.Begin"/> takes it: an id that
            // stayed here would be used by two launches the moment two callers mint before either
            // records, and the second INSERT would fail on the primary key rather than on the thing
            // a test is asking about.
            string id;
            lock (_mint) { id = _pending ?? NewId(); _pending = null; }

            return new AiAttemptStore(_db).Begin(
                new AiAttempt { Id = id, StartedAt = DateTimeOffset.UtcNow, Role = role }, wakes);
        }

        /// <summary>
        /// The id the turn about to run will carry. Minted before the prompt, exactly as the meter
        /// does it, so a conversation writing into <c>out/</c> can name the file after it.
        /// </summary>
        public string? NextAttemptId()
        {
            lock (_mint) return Minted = _pending = NewId();
        }

        readonly Lock _mint = new();
        string? _pending;

        string NewId() => $"turn-{_tag}-{++_attempts}";

        /// <summary>The id minted for the turn in flight, for a conversation that has to name it.</summary>
        public string? Minted { get; private set; }

        /// <summary>The REAL relay, so what the chair is handed is what the app actually publishes.</summary>
        public void Relay(string role, string? attempt) => _relay.Run(role, attempt);

        public MissionDelivery? Delivered(string publicationId)
        {
            var p = new PublicationStore(_db).Get(publicationId);
            return p is null ? null : new MissionDelivery(p.Id, p.Kind, p.Role, p.Content);
        }
    }

    /// <summary>How long a boundary in these tests stays open. A day compressed to a minute.</summary>
    static readonly TimeSpan BoundaryWindow = TimeSpan.FromMinutes(1);

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

    /// <summary>How many launches this ledger holds for one role, whatever state they are in.</summary>
    static int Launches(Database db, string role)
    {
        using var c = db.Cmd("SELECT COUNT(*) FROM ai_attempt WHERE role=$r", ("$r", role));
        return Convert.ToInt32(c.ExecuteScalar());
    }

    /// <summary>
    /// Runs both callers on REAL THREADS and waits for them, because "both were admitted" is a fact
    /// about two callers and one ledger, and a stand-in for either would exercise neither. The idiom
    /// is <c>BudgetReservationTests</c>'s.
    /// </summary>
    static void Together(Action a, Action b)
    {
        Exception? first = null;
        void Run(Action work)
        {
            try { work(); }
            catch (Exception ex) { Interlocked.CompareExchange(ref first, ex, null); }
        }

        var one = new Thread(() => Run(a));
        var two = new Thread(() => Run(b));
        one.Start();
        two.Start();
        Assert.True(one.Join(TimeSpan.FromSeconds(60)), "the first turn never finished");
        Assert.True(two.Join(TimeSpan.FromSeconds(60)), "the second turn never finished");
        if (first is not null) throw first;
    }

    // ---- the property ------------------------------------------------------------------------

    /// <summary>
    /// ITEM 1, RED FIRST: ONE TURN PER ROLE AT A TIME, and the second caller is REFUSED rather than
    /// queued behind the first.
    ///
    /// <para>Nothing prevented a second <see cref="MissionLoop.TurnAsync"/> for a role already
    /// turning. Everything before the launch is a look — the queue, the share, the conversation's
    /// own busy flag — and two callers that take those looks together both pass them: the wake is
    /// still unconsumed, the share still has room, and the conversation is not busy until one of
    /// them actually sends. Both then launched. Two launch records for one reason to work, two agent
    /// processes in one role's folder, and the second one charged to a day that had room for one.</para>
    ///
    /// <para>The barrier is what makes this a race rather than a sequence, exactly as in
    /// <c>BudgetReservationTests</c>: neither caller reaches the lease until both have been past
    /// every look the loop takes before it.</para>
    /// </summary>
    [Fact]
    public void A_second_turn_for_a_role_already_turning_is_refused_and_never_launched()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var concurrency = new Concurrency();
        var host = new CouncilHost(db, root, concurrency);
        var loop = new MissionLoop(host, NoHeartbeat);

        var earlier = DateTimeOffset.UtcNow.AddMinutes(-2);
        host.Events!.Raise(MissionEventIds.Fill("EXEC-1"), MissionEventKind.Fill, earlier,
            role: CouncilRoles.Operations);

        using var both = new Barrier(2);
        host.BeforeLaunch = _ => Assert.True(both.SignalAndWait(TimeSpan.FromSeconds(30)),
            "the two callers never met: one of them was refused before the lease, not at it");

        // AND THE WINNER'S LEASE IS STILL HELD WHEN THE LOSER ASKS FOR IT.
        //
        // The barrier above holds both callers inside SituationAsync, which the loop runs BEFORE it
        // takes the role's lease — so it makes them MEET, and that was all it did. Nothing kept the
        // winner turning while the loser walked the few instructions from the barrier to Lease():
        // the winner's whole turn is a 2 ms delay and two ledger writes, it drops the lease, and a
        // loser that arrives afterwards finds the role free and is admitted. That second turn is
        // legitimately admitted — it is not two turns at once, which is the property this test is
        // about — and it writes a second `ai_attempt` row for a wake already spent.
        //
        // MEASURED on draft PR #23 (runs 35504722157 and 35513092386), 30 rounds per row: the
        // winner's whole TurnAsync is 3.3 ms (min 3.3, median 3.7 on this Mac), and with 20 ms of
        // preemption on ONE of the two callers out of the barrier — nothing else changed, which is
        // all a starved runner does to a thread — the shipped body recorded TWO launches in 30 of
        // 30 rounds: `Expected: 1 / Actual: 2`, macos-latest's red at `659eb5b` (run 35503895941).
        // With this hand-over and the same preemption: one launch in 30 of 30.
        //
        // IT CANNOT HIDE THE DEFECT IT IS HERE FOR. If both callers were admitted, both would run a
        // turn, both would wait here for an answer that is not coming, and the wait fails by name.
        using var answered = new ManualResetEventSlim(false);
        var joined = true;
        host.Conversations[CouncilRoles.Operations].OnTurn =
            () => { if (!answered.Wait(TimeSpan.FromSeconds(30))) joined = false; };

        void Turn()
        {
            try { loop.TurnAsync().GetAwaiter().GetResult(); }
            finally { answered.Set(); }
        }

        Together(Turn, Turn);

        Assert.True(joined,
            "the admitted turn was never joined by the other caller's answer: either the other "
            + "caller is running a turn of its own for this role, or it never reached the lease");

        // ONE LAUNCH FOR ONE REASON TO WORK. The other caller was answered, not parked.
        Assert.Equal(1, Launches(db, CouncilRoles.Operations));
        Assert.Single(host.Conversations[CouncilRoles.Operations].Sent);
        Assert.Equal(1, concurrency.Peak);
    }


    // ===================== PROBE (U-runner-reds-3, throwaway; reverted before the fix) ===========
    /// <summary>
    /// PROBE: where the second caller actually is when the first one's turn ENDS.
    ///
    /// The barrier holds both callers inside <c>SituationAsync</c>, which the loop runs BEFORE it
    /// takes the role's lease. Nothing then keeps the winner's lease held: its turn is a 2 ms
    /// delay and two ledger writes, so a runner that leaves the loser between the barrier and
    /// <c>Lease</c> for longer than that admits it too — a second launch record for a wake that is
    /// already spent, which is `Expected: 1 / Actual: 2` at line 385 and not a product defect.
    /// Measured here as: the wall gap between the two callers leaving the barrier, and how many
    /// rounds out of 30 record two launches — the shipped body against one that holds the winner's
    /// lease until the loser has been answered.
    /// </summary>
    [Fact]
    public void Probe_where_the_second_caller_is_when_the_first_turn_ends()
    {
        var said = new List<string>();

        // (the hand-over on or off, how long ONE of the two callers is preempted on its way out of
        //  the barrier — which is the only thing a starved runner does differently)
        foreach (var (handOver, preemptMs) in new[] { (false, 0), (false, 20), (true, 0), (true, 20) })
        {
            var twoLaunches = 0;
            var gaps = new List<double>();
            var counts = new List<int>();
            var turns = new List<double>();

            for (var round = 0; round < 30; round++)
            {
                var (db, root) = Workspace();
                using var _ = db;
                var concurrency = new Concurrency();
                var host = new CouncilHost(db, root, concurrency);
                var loop = new MissionLoop(host, NoHeartbeat);

                var earlier = DateTimeOffset.UtcNow.AddMinutes(-2);
                host.Events!.Raise(MissionEventIds.Fill("EXEC-1"), MissionEventKind.Fill, earlier,
                    role: CouncilRoles.Operations);

                var wall = System.Diagnostics.Stopwatch.StartNew();
                var leftTheBarrier = new System.Collections.Concurrent.ConcurrentBag<double>();
                var ran = new System.Collections.Concurrent.ConcurrentBag<double>();
                var arrivals = 0;
                using var both = new Barrier(2);
                host.BeforeLaunch = _ =>
                {
                    both.SignalAndWait(TimeSpan.FromSeconds(30));
                    leftTheBarrier.Add(wall.Elapsed.TotalMilliseconds);
                    // ONE OF THE TWO IS PREEMPTED between the barrier and the lease, which is what a
                    // starved runner does to a thread and what nothing in the fixture holds against.
                    if (Interlocked.Increment(ref arrivals) == 2 && preemptMs > 0) Thread.Sleep(preemptMs);
                };

                // THE PROPOSED HAND-OVER: the admitted turn does not end until the other caller has
                // been answered, so the lease it holds is still held when the other one asks for it.
                using var answered = new ManualResetEventSlim(false);
                if (handOver)
                    host.Conversations[CouncilRoles.Operations].OnTurn =
                        () => answered.Wait(TimeSpan.FromSeconds(30));

                void One()
                {
                    var t = System.Diagnostics.Stopwatch.StartNew();
                    try { loop.TurnAsync().GetAwaiter().GetResult(); }
                    finally { ran.Add(t.Elapsed.TotalMilliseconds); answered.Set(); }
                }

                Together(One, One);

                var launches = Launches(db, CouncilRoles.Operations);
                counts.Add(launches);
                if (launches != 1) twoLaunches++;
                var times = leftTheBarrier.Order().ToList();
                if (times.Count == 2) gaps.Add(times[1] - times[0]);
                turns.Add(ran.Max());
            }

            said.Add($"PROBE council {(handOver ? "hand-over" : "shipped  ")} preempt={preemptMs} ms: launches != 1 in "
                     + $"{twoLaunches} of 30 rounds | counts [{string.Join(",", counts)}] | the gap between the two "
                     + $"callers leaving the barrier min/median/max = {gaps.Min():0.###}/"
                     + $"{gaps.Order().ElementAt(gaps.Count / 2):0.###}/{gaps.Max():0.###} ms | the longer caller's "
                     + $"whole TurnAsync min/median/max = {turns.Min():0.###}/"
                     + $"{turns.Order().ElementAt(turns.Count / 2):0.###}/{turns.Max():0.###} ms");
        }

        // A PROBE IS NOT A TEST. It fails on purpose so the measurement is printed by the runner's
        // own step log, which is the only place a draft PR's numbers can be read from.
        Assert.Fail(string.Join("\n", said));
    }
    // =================== end PROBE ==============================================================

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

        // ONE AT A TIME HERE, measured rather than assumed: these two turns are taken one after the
        // other by one caller, and nothing in the loop starts a second turn behind the first. What
        // is NOT claimed by this number is that two turns can never overlap — they can, and
        // Two_roles_turns_overlap_under_their_own_leases_and_the_card_names_both measures two.
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
        // NAMED AFTER THE TURN THAT WRITES IT, which is what the app now requires: the relay
        // attributes a file by the attempt id in its name and quarantines one naming no launch.
        host.Conversations[CouncilRoles.Research].OnTurn = () => File.WriteAllText(
            Path.Combine(host.HomeFor(CouncilRoles.Research), WorkspaceBuilder.OutDir,
                $"report-{host.Minted}.md"),
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
    /// THE MEASURED NUMBER THIS UNIT CHANGES: two roles' turns really do overlap, each under its own
    /// lease, and the card names both of them.
    ///
    /// <para>Every other test in this class takes its turns one after another, so its high-water mark
    /// is one; that is a fact about the caller, not a guarantee of the loop. Here both turns are in
    /// flight at the same instant — each conversation reports itself alive and waits for the other —
    /// and the peak is two. It is measured rather than assumed for the same reason it always was.</para>
    ///
    /// <para>And the line on the card is read at that instant. "Working" over two roles that are both
    /// working is not wrong so much as useless: the owner cannot tell it from one role working and
    /// the other stuck, which is the state the share on the Safety page exists to repair.</para>
    /// </summary>
    [Fact]
    public void Two_roles_turns_overlap_under_their_own_leases_and_the_card_names_both()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var concurrency = new Concurrency();
        var host = new CouncilHost(db, root, concurrency);
        var loop = new MissionLoop(host, NoHeartbeat);

        var earlier = DateTimeOffset.UtcNow.AddMinutes(-2);
        foreach (var role in CouncilRoles.All)
            host.Events!.Raise(MissionEventIds.ForRole(MissionEventIds.Review(earlier), role),
                MissionEventKind.Review, earlier, role: role);

        // BOTH INSIDE THEIR OWN TURN AT ONCE. The chair goes first because its wake is the oldest;
        // the second caller starts only once the first is really inside, which is the moment the
        // loop has to step over a role that is turning and give the other one its turn.
        using var release = new ManualResetEventSlim();
        var inside = CouncilRoles.All.ToDictionary(r => r, _ => new ManualResetEventSlim());
        foreach (var role in CouncilRoles.All)
        {
            var mine = inside[role];
            host.Conversations[role].OnTurn = () =>
            {
                mine.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(30)), "the other role never started");
            };
        }

        MissionStatus? mid = null;
        var chair = new Thread(() => loop.TurnAsync().GetAwaiter().GetResult());
        chair.Start();
        Assert.True(inside[CouncilRoles.Operations].Wait(TimeSpan.FromSeconds(30)),
            "the chair never started its turn");

        var director = new Thread(() => loop.TurnAsync().GetAwaiter().GetResult());
        director.Start();
        Assert.True(inside[CouncilRoles.Research].Wait(TimeSpan.FromSeconds(30)),
            "the Research Director was not given a turn while the chair was in one");

        mid = loop.Status;
        release.Set();

        Assert.True(chair.Join(TimeSpan.FromSeconds(30)), "the chair's turn never finished");
        Assert.True(director.Join(TimeSpan.FromSeconds(30)), "the Research Director's turn never finished");
        foreach (var e in inside.Values) e.Dispose();

        Assert.Equal(2, concurrency.Peak);
        Assert.Single(host.Conversations[CouncilRoles.Operations].Sent);
        Assert.Single(host.Conversations[CouncilRoles.Research].Sent);

        Assert.NotNull(mid);
        Assert.Equal(MissionState.Working, mid!.State);
        Assert.Equal([CouncilRoles.Operations, CouncilRoles.Research], mid.Roles);
        Assert.Equal($"{CouncilRoles.Title(CouncilRoles.Operations)} and "
                     + $"{CouncilRoles.Title(CouncilRoles.Research)}: working",
            DashboardPage.MissionSentence(mid));
    }

    /// <summary>
    /// ITEM 5, RED FIRST: QUIESCENCE IS OF EVERY MANAGED AGENT, so a file the owner drops between
    /// turns is still recorded as THEIRS when two roles are turning.
    ///
    /// <para><c>docs/COUNCIL.md</c> rule 7. The pass before a turn can attest what it finds only
    /// across a window with no agent process alive in it, and a material row is written ONCE — so a
    /// pass that runs while the other role is mid-turn does not merely fail to attest, it spends the
    /// sighting on the weaker word for good. Serially there was no such moment; with two roles there
    /// is one every time a turn launches beside a pass.</para>
    ///
    /// <para>Both directions are pressed here: the pass is held open, and the other role tries to
    /// launch inside it. The turn is refused — the pass is the app's own, bounded, and the loop asks
    /// again in five seconds — and the owner keeps the one claim in the ledger that says the file is
    /// theirs.</para>
    /// </summary>
    [Fact]
    public async Task A_file_dropped_between_turns_is_still_the_owners_when_another_role_tries_to_launch()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();
        var host = new CouncilHost(db, root, new Concurrency(), presence);
        var loop = new MissionLoop(host, NoHeartbeat);

        var earlier = DateTimeOffset.UtcNow.AddMinutes(-2);

        void Wake(string role, int n) => host.Events!.Raise(
            MissionEventIds.ForRole($"{MissionEventKind.Review}:{n}", role),
            MissionEventKind.Review, earlier.AddSeconds(n), role: role);

        // ---- a first turn, so a pass has run and the window is closed behind it ----------------
        Wake(CouncilRoles.Operations, 1);
        await loop.TurnAsync();

        // ---- the owner drops something, with nothing alive -------------------------------------
        File.WriteAllText(Path.Combine(root, MaterialScanner.InboxDir, "broker-statement.pdf"),
            "the owner's document");

        // ---- and the other role tries to launch INSIDE the pass that is recording it -----------
        Wake(CouncilRoles.Operations, 2);
        Wake(CouncilRoles.Research, 3);

        using var scanning = new ManualResetEventSlim();
        using var tried = new ManualResetEventSlim();
        host.WhileScanning = () =>
        {
            if (!scanning.IsSet) { scanning.Set(); Assert.True(tried.Wait(TimeSpan.FromSeconds(30))); }
        };

        Together(
            () => loop.TurnAsync().GetAwaiter().GetResult(),
            () =>
            {
                Assert.True(scanning.Wait(TimeSpan.FromSeconds(30)), "no pass ever started");
                loop.TurnAsync().GetAwaiter().GetResult();
                tried.Set();
            });

        var row = new MaterialStore(db).Present().Single(m => m.Name == "broker-statement.pdf");
        Assert.Equal(MaterialOrigin.Inbox, row.Origin);
    }

    /// <summary>
    /// ITEM 5, RED FIRST: ONE ROLE'S TURNS ARE NOT THE OTHER ROLE'S. The session count and the run
    /// of failures are per role, because both are facts about ONE conversation.
    ///
    /// <para>A single session counter rotated whichever conversation ran next: two chair turns threw
    /// away the Research Director's CLI session, which had taken one, and the context that was
    /// actually long went on growing. A single error count made one role's failures the other
    /// role's backoff — a Research Director that cannot start is a half-hour wait on a chair that is
    /// working perfectly well.</para>
    /// </summary>
    [Fact]
    public async Task One_roles_failures_and_session_do_not_rotate_or_back_off_the_other_roles()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var host = new CouncilHost(db, root, new Concurrency());
        var loop = new MissionLoop(host, new MissionOptions
        {
            ReviewEvery = TimeSpan.Zero,
            TurnsPerSession = 2
        });

        var chair = host.Conversations[CouncilRoles.Operations];
        var research = host.Conversations[CouncilRoles.Research];
        chair.Exit = 2;                                  // every chair turn fails

        var earlier = DateTimeOffset.UtcNow.AddMinutes(-2);
        void Wake(string role, int n) => host.Events!.Raise(
            MissionEventIds.ForRole($"{MissionEventKind.Review}:{n}", role),
            MissionEventKind.Review, earlier.AddSeconds(n), role: role);

        // TWO CHAIR TURNS, both failing, which is exactly its allowance of one CLI session.
        for (var i = 1; i <= 2; i++)
        {
            Wake(CouncilRoles.Operations, i);
            Assert.Equal(TimeSpan.FromSeconds(i == 1 ? 30 : 60), await loop.TurnAsync());
        }

        Assert.Equal(2, chair.Sent.Count);
        Assert.Equal(0, chair.Stops);

        // THE RESEARCH DIRECTOR'S FIRST TURN, which ends cleanly. Its session has taken NO turns, so
        // nothing rotates it: one counter between them would throw away the session of the role that
        // had not used it, and leave the long context of the role that had.
        Wake(CouncilRoles.Research, 3);
        await loop.TurnAsync();

        Assert.Single(research.Sent);
        Assert.Equal(0, research.Stops);

        // AND THE CHAIR'S RUN OF FAILURES IS STILL ITS OWN: its third failed turn doubles to 120s.
        // One counter between them would have been reset by the clean turn above and this would be
        // thirty seconds — a role that cannot start, retried as though it were healthy, because a
        // different role is. Its own third turn is also where its session is rotated.
        Wake(CouncilRoles.Operations, 4);
        Assert.Equal(TimeSpan.FromSeconds(120), await loop.TurnAsync());
        Assert.Equal(1, chair.Stops);
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
    // ---- U-council-concurrent-2, item 4: the deadline's default is applied by CODE ---------------

    /// <summary>
    /// RED FIRST: the deadline passes and the boundary stays open for ever.
    ///
    /// <para><c>docs/COUNCIL.md</c>:62 — "code applies the promotion and allocation policy so neither
    /// director can veto an eligible deployment forever". This is the end-to-end half of that: the loop
    /// settles it ITSELF, with no agent running, no launch record written and nothing charged — and the
    /// clock it settles on is the app's, because the loop SLEEPS UNTIL the deadline rather than waiting
    /// for a wake that may never come on a quiet installation.</para>
    ///
    /// <para>The two turns before it are the two the boundary bought when it opened: two assessments are
    /// two turns (:63), and the third tick is not a third one.</para>
    /// </summary>
    [Fact]
    public async Task The_loop_settles_a_boundary_at_its_deadline_without_launching_anything()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var now = Noon();
        var host = new CouncilHost(db, root, new Concurrency());
        var loop = new MissionLoop(host, NoHeartbeat, now: () => now);

        var boundary = host.Boundaries!.Open(BoundaryKind.Promotion, "version-a", 1,
            BoundaryDisposition.Deploy, "holdout run 9f3c under campaign 1", now).Row;

        // THE TWO TURNS THE BOUNDARY BOUGHT, one per director, and nothing more.
        await loop.TurnAsync();
        await loop.TurnAsync();
        Assert.Equal(1, Launches(db, CouncilRoles.Operations));
        Assert.Equal(1, Launches(db, CouncilRoles.Research));

        // NOTHING IS DUE, AND THE LOOP SLEEPS UNTIL THE DEADLINE — not until the queue's next tick,
        // which on an installation with the heartbeat off is nothing at all.
        now = boundary.DeadlineAt.AddSeconds(-20);
        var wait = await loop.TurnAsync();
        Assert.Equal(TimeSpan.FromSeconds(20), wait);
        Assert.True(host.Boundaries.ById(boundary.Id)!.IsOpen);

        // AT THE DEADLINE: the policy's default, written by the app, with no third launch anywhere.
        now = boundary.DeadlineAt;
        await loop.TurnAsync();

        var settled = host.Boundaries.ById(boundary.Id)!;
        Assert.Equal(BoundaryDisposition.Deploy, settled.Disposition);
        Assert.Equal(BoundaryAuthor.Policy, settled.DisposedBy);
        Assert.Equal(1, Launches(db, CouncilRoles.Operations));
        Assert.Equal(1, Launches(db, CouncilRoles.Research));
        Assert.Equal(2, host.Opened.Count);
    }

    /// <summary>
    /// EACH DIRECTOR IS TOLD ITS OWN OPEN BOUNDARIES AND THEIR DEADLINES, and what the app will answer
    /// without them. A deadline whose consequence is not stated is a date.
    ///
    /// <para>The line is composed from the LEDGER — <c>CouncilBoundaries.OpenFor</c> — so what a
    /// director is told and what the sweep will do are the same two facts read from the same row.</para>
    /// </summary>
    [Fact]
    public void The_situation_shows_a_director_its_open_boundary_its_deadline_and_the_default()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var now = Noon();
        var host = new CouncilHost(db, root, new Concurrency());

        var row = host.Boundaries!.Open(BoundaryKind.Promotion, "version-a", 1,
            BoundaryDisposition.Deploy, "holdout run 9f3c under campaign 1", now).Row;

        var standing = Assert.Single(host.Boundaries.OpenFor(CouncilRoles.Research));
        var text = new MissionSituation { Role = CouncilRoles.Research, Boundaries = [standing.Line()] }
            .Text();

        Assert.Contains(row.Id, text, StringComparison.Ordinal);
        Assert.Contains("write your assessment this turn", text, StringComparison.Ordinal);
        Assert.Contains("you cannot revise it", text, StringComparison.Ordinal);
        Assert.Contains($"{row.DeadlineAt.LocalDateTime:yyyy-MM-dd HH:mm}", text, StringComparison.Ordinal);
        Assert.Contains("`deploy`", text, StringComparison.Ordinal);
        Assert.Contains("holdout run 9f3c under campaign 1", text, StringComparison.Ordinal);
    }

}

using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE AI DOES NOT STOP, AND WORKING NON-STOP DOES NOT COST THE LEDGER ITS ATTESTATION.
///
/// Turns run back to back for as long as the owner has said the AI may work on its own. That is the
/// product — until this loop existed nothing happened unless somebody typed — and it collides with
/// the one measurement in <c>material</c> that claims something the filesystem cannot show: an inbox
/// sighting is attributed to the ACCOUNT OWNER only across a window with no agent process alive
/// (BUILD-STATUS, U-batch-2 finding 5). A loop that never leaves such a window would have quietly
/// downgraded every file the owner ever drops to <see cref="MaterialOrigin.InboxUnattested"/>.
///
/// So the loop yields, in two places that are each load-bearing, and both are pressed below.
/// </summary>
public class MissionLoopTests
{
    // ---- the world the loop runs in ----------------------------------------------------------

    /// <summary>
    /// A conversation with no process behind it that is nevertheless honest about the one thing the
    /// attestation turns on: while a turn runs, an agent process is alive, and
    /// <see cref="AgentPresence"/> is told so exactly as <c>AgentSession</c> tells it.
    /// </summary>
    sealed class FakeConversation(AgentPresence presence) : IAgentConversation
    {
        readonly List<ChatTurn> _history = [];
        readonly List<string> _typed = [];

        public List<string> Sent { get; } = [];
        public int Exit { get; set; }
        public int Sessions { get; private set; } = 1;
        public bool Busy { get; private set; }
        public IReadOnlyList<ChatTurn> History => _history.ToArray();
        public string Reply { get; set; } = "Read PLAN.md, ran a backtest.\nDetail on the second line.";

        public event Action<ChatTurn>? TurnAdded;
        public event Action<string>? Delta;
        public event Action? StateChanged;
        public event Action<AgentTurnEnded>? TurnEnded;

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SendAsync(string message, CancellationToken ct = default)
        {
            if (Busy) { _typed.Add(message); return Task.CompletedTask; }
            return Run(message, ct);
        }

        public Task SendMissionAsync(string message, CancellationToken ct = default) => Run(message, ct);

        async Task Run(string message, CancellationToken ct)
        {
            Sent.Add(message);
            Busy = true;
            StateChanged?.Invoke();
            using (presence.Enter()) await Task.Delay(2, ct);

            // The child is dead by the time a turn returns — every path out of AgentSession's own
            // turn has that property — so a scan taken after this line measures a window it is not
            // inside. Five milliseconds of separation, so the two clock reads cannot tie.
            await Task.Delay(5, ct);

            var turn = new ChatTurn(ChatRole.Ai, Reply, DateTimeOffset.UtcNow);
            _history.Add(turn);
            TurnAdded?.Invoke(turn);
            Busy = false;
            StateChanged?.Invoke();
            Delta?.Invoke("");
            TurnEnded?.Invoke(new AgentTurnEnded(Exit, TimeSpan.FromMilliseconds(7), Reply, DateTimeOffset.UtcNow));
        }

        public IReadOnlyList<string> TakeTyped()
        {
            var taken = _typed.ToArray();
            _typed.Clear();
            return taken;
        }

        /// <summary>What the loop calls to forget the CLI session, so a fresh one starts next turn.</summary>
        public Task StopAsync() { Sessions++; return Task.CompletedTask; }
        public Task CancelAsync() => Task.CompletedTask;
        public void Queue(string message) => _typed.Add(message);
    }

    /// <summary>
    /// The app, with a REAL scanner and a REAL presence behind it. Nothing about the attestation is
    /// simulated here: the same <c>MaterialScanner</c> the product runs walks the same tree layout,
    /// and the only thing the test controls is when a pass happens.
    /// </summary>
    sealed class FakeHost : IMissionHost
    {
        readonly Database _db;
        DateTimeOffset? _lastPassAt;

        public FakeHost(Database db, string root, AgentPresence presence, IAgentConversation? conversation)
        {
            _db = db;
            Root = root;
            Presence = presence;
            Conversation = conversation;
        }

        public string Root { get; }
        public AgentPresence Presence { get; }
        public IAgentConversation? Conversation { get; set; }
        public int Passes { get; private set; }
        public MissionSituation Next { get; set; } = new();

        /// <summary>
        /// The wake queue, or null for the pre-<c>U-wakes</c> shape — a host with no database behind
        /// it, which is what every test above this line is. Null there is not a convenience: it is
        /// the documented default of <see cref="IMissionHost.Events"/>, and these tests are what
        /// pins that a host with no queue still turns.
        /// </summary>
        public MissionEventStore? Events { get; set; }

        /// <summary>Every prompt a launch was opened for, in order.</summary>
        public List<string> Opened { get; } = [];

        /// <summary>The id of the last launch this host recorded, or null if it has recorded none.</summary>
        public string? LastAttemptId { get; private set; }

        // Unique to this HOST, not to this database. Two hosts over one database is exactly the
        // shape of a restart, and ids that collided there would fail the restart tests for a reason
        // that has nothing to do with what they are about.
        readonly string _tag = Guid.NewGuid().ToString("n")[..8];
        int _attempts;

        public string AgentHome => Path.Combine(Root, MaterialScanner.AgentDir);

        /// <summary>
        /// The launch record, through the REAL store, so the one-transaction consumption in
        /// <see cref="AiAttemptStore.Begin"/> is the thing under test rather than a stand-in for it.
        /// A host with no queue records nothing and answers null, exactly as the interface's default
        /// does.
        /// </summary>
        public string? BeginTurn(string prompt, IReadOnlyList<string> wakes)
        {
            Opened.Add(prompt);
            if (Events is null) return null;

            var id = $"turn-{_tag}-{++_attempts}";
            new AiAttemptStore(_db).Begin(
                new AiAttempt { Id = id, StartedAt = DateTimeOffset.UtcNow }, wakes);
            return LastAttemptId = id;
        }

        public Task<MissionSituation> SituationAsync(CancellationToken ct) => Task.FromResult(Next);

        public bool InboxChangedSinceLastPass => MissionInbox.ChangedSince(Root, _lastPassAt);

        public Task ScanAsync(CancellationToken ct)
        {
            // Captured BEFORE the walk, so this host's idea of the last pass is never later than the
            // scanner's own. Erring early costs a spare pass; erring late costs an attestation.
            _lastPassAt = DateTimeOffset.UtcNow;
            Passes++;
            new MaterialScanner(_db, Root, Presence.NoneSince).Scan(ct);
            return Task.CompletedTask;
        }
    }

    static (Database Db, string Root) Workspace()
    {
        var root = Path.Combine(TestEnv.Home, $"mission-{Guid.NewGuid():n}");
        Directory.CreateDirectory(Path.Combine(root, MaterialScanner.InboxDir));
        foreach (var d in MaterialScanner.TrackedAgentDirs)
            Directory.CreateDirectory(Path.Combine(root, MaterialScanner.AgentDir, d));
        Directory.CreateDirectory(Path.Combine(root, MaterialScanner.AgentDir, ".tradeagent"));
        return (TestEnv.NewDb(), root);
    }

    static void Drop(string root, string name, string content) =>
        File.WriteAllText(Path.Combine(root, MaterialScanner.InboxDir, name), content);

    /// <summary>
    /// The heartbeat OFF, so that every turn in the wake tests below has a named cause. With it on,
    /// "the loop did nothing" and "the loop woke itself" are the same observation for half an hour.
    /// </summary>
    static readonly MissionOptions NoHeartbeat = new() { ReviewEvery = TimeSpan.Zero };

    static string OwnerSaid(string text) =>
        Json.Write(new MissionOwnerMessage(text, DateTimeOffset.UtcNow));

    // ---- 1. the loop yields to the scanner ---------------------------------------------------

    /// <summary>
    /// THE FINDING, RED FIRST. The owner drops a file between two turns of a loop that never stops.
    /// The row has to read <see cref="MaterialOrigin.Inbox"/> — "the owner handed this over" — and
    /// it can only do so if some pass measured a window with no agent process in it.
    ///
    /// Both yields are needed and neither alone is enough. The pass AFTER a turn is what moves the
    /// window's start past that turn's last breath; the pass BEFORE the next turn is what records
    /// the file while the window is still clean. Delete either one and this goes red — which is what
    /// the two mutants in the unit's report do.
    /// </summary>
    [Fact]
    public async Task A_file_the_owner_drops_between_turns_is_still_recorded_as_theirs()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();
        var host = new FakeHost(db, root, presence, new FakeConversation(presence));
        var loop = new MissionLoop(host);

        await loop.TurnAsync();
        Drop(root, "broker-statement.pdf", "the owner's document");
        await loop.TurnAsync();

        var row = new MaterialStore(db).Present().Single(m => m.Name == "broker-statement.pdf");
        Assert.Equal(MaterialOrigin.Inbox, row.Origin);
    }

    /// <summary>
    /// The other direction, so the yield is a measurement and not a blanket upgrade: a file that
    /// appears while a turn is actually running is still unattested. Nobody can show who put it
    /// there, and the loop working harder must never turn that into a claim.
    /// </summary>
    [Fact]
    public async Task A_file_that_appears_during_a_turn_is_still_unattested()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();
        var host = new FakeHost(db, root, presence, new FakeConversation(presence));
        var loop = new MissionLoop(host);

        await loop.TurnAsync();

        // Exactly the shape the ledger cannot attest: an agent process is alive as the file lands.
        using (presence.Enter()) Drop(root, "signed-authority.pdf", "I am allowed to trade live");
        await loop.TurnAsync();

        var row = new MaterialStore(db).Present().Single(m => m.Name == "signed-authority.pdf");
        Assert.Equal(MaterialOrigin.InboxUnattested, row.Origin);
    }

    /// <summary>
    /// The yield is not a scan on every turn. An idle drop folder costs one pass to close the window
    /// behind the turn and nothing more, because the walk is the most expensive thing in the app's
    /// background loop and a mission that never stops would otherwise run it twice a turn for ever.
    /// </summary>
    [Fact]
    public async Task An_unchanged_inbox_costs_one_pass_a_turn()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();
        var host = new FakeHost(db, root, presence, new FakeConversation(presence));
        var loop = new MissionLoop(host);

        await loop.TurnAsync();                     // the first turn has never seen a pass: 2
        var before = host.Passes;
        await loop.TurnAsync();
        await loop.TurnAsync();

        Assert.Equal($"first {2}, then {2}", $"first {before}, then {host.Passes - before}");
    }

    // ---- 1b. the loop turns because something happened, and not otherwise --------------------

    /// <summary>
    /// THE PROPERTY THE UNIT IS BUILT ON (<c>docs/COUNCIL.md</c>, the <c>U-wakes</c> line): with no
    /// eligible unconsumed event, ticks, a fresh loop over the same database and a replay of every
    /// consumed event launch ZERO paid processes.
    ///
    /// RED FIRST, and it was red for the plainest possible reason: <c>AskedForDelay</c> answered
    /// <c>Zero</c> whenever the AI had written no <c>next.json</c>, so a turn was taken every time
    /// the loop was asked for one and the only thing that ever stopped it was the day's cost
    /// ceiling. A bill is not a reason to work.
    ///
    /// The replay is not hypothetical: the fill ledger has two sources on purpose, a reconnect
    /// re-pulls a whole day of executions, and a bridge that reconnects repeats order transitions.
    /// Every one of those raises an id the table already holds.
    /// </summary>
    [Fact]
    public async Task With_nothing_due_ticks_a_restart_and_a_replay_launch_nothing()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();
        var events = new MissionEventStore(db);

        // The day that already happened: one real reason to wake, and the turn that answered it.
        var yesterday = new FakeConversation(presence);
        events.Raise(MissionEventIds.Owner(1), MissionEventKind.Owner, DateTimeOffset.UtcNow,
            OwnerSaid("how did today go?"));
        await new MissionLoop(new FakeHost(db, root, presence, yesterday) { Events = events },
            NoHeartbeat).TurnAsync();
        Assert.Single(yesterday.Sent);

        // From here nothing new has happened, and nothing may be launched.
        var conversation = new FakeConversation(presence);
        var host = new FakeHost(db, root, presence, conversation) { Events = new MissionEventStore(db) };
        var restarted = new MissionLoop(host, NoHeartbeat);

        for (var i = 0; i < 5; i++) await restarted.TurnAsync();          // ticks
        foreach (var e in events.OfKind(MissionEventKind.Owner))          // the day, replayed
            events.Raise(e.Id, e.Kind, DateTimeOffset.UtcNow, e.Payload);
        for (var i = 0; i < 5; i++) await restarted.TurnAsync();

        Assert.Empty(conversation.Sent);
        Assert.Empty(host.Opened);
        Assert.Equal(0, restarted.Status.Turns);
    }

    /// <summary>
    /// The other direction, so the gate is a queue and not an off switch: one due event is one turn,
    /// and the turn names what woke it. An AI told nothing about why it is awake has to look at
    /// everything, every turn, which is both the expensive way and the way that misses the one thing
    /// that changed.
    /// </summary>
    [Fact]
    public async Task One_due_event_is_one_turn_and_the_situation_says_what_woke_it()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();
        var events = new MissionEventStore(db);
        var conversation = new FakeConversation(presence);
        var host = new FakeHost(db, root, presence, conversation) { Events = events };
        var loop = new MissionLoop(host, NoHeartbeat);

        events.Raise(MissionEventIds.Fill("EXEC-1"), MissionEventKind.Fill, DateTimeOffset.UtcNow);
        await loop.TurnAsync();
        await loop.TurnAsync();

        Assert.Single(conversation.Sent);
        Assert.Contains("- Why you are awake: an order filled", conversation.Sent[0]);
        Assert.Equal(MissionEventDisposition.Answered,
            events.Get(MissionEventIds.Fill("EXEC-1"))!.Disposition);
    }

    /// <summary>
    /// THE WAKE IS SPENT BEFORE THE PROCESS STARTS, in the launch record's own transaction. Two
    /// commits would leave a window in which the turn is recorded and its reason is not, and a kill
    /// inside that window hands the same reason to the next launch — the owner paying twice for one
    /// thing having happened.
    /// </summary>
    [Fact]
    public async Task The_wake_is_consumed_by_the_attempt_that_answered_it()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();
        var events = new MissionEventStore(db);
        var host = new FakeHost(db, root, presence, new FakeConversation(presence)) { Events = events };
        events.Raise(MissionEventIds.Fill("EXEC-1"), MissionEventKind.Fill, DateTimeOffset.UtcNow);

        await new MissionLoop(host, NoHeartbeat).TurnAsync();

        var row = events.Get(MissionEventIds.Fill("EXEC-1"))!;
        Assert.True(row.Consumed);
        Assert.Equal(host.LastAttemptId, row.ConsumedBy);
        Assert.Equal(AiAttemptState.LAUNCHED, new AiAttemptStore(db).Get(host.LastAttemptId!)!.State);
    }

    /// <summary>
    /// The AI's request to be woken later is an event like any other now, so a delay it asked for
    /// survives a restart — and the wait the loop returns is the queue's, not a number it is holding
    /// in memory. Nothing is due before the delay is up.
    ///
    /// ON A FIXED CLOCK AT MIDDAY, because the wait this asserts is the QUEUE'S MINIMUM and the
    /// day's renewal is due at local midnight. A suite that happened to run at 23:55 measured the
    /// renewal instead of the AI's request and read a true answer as a failure; the assertion is
    /// unchanged, and what is pinned is the hour it is asked at.
    /// </summary>
    [Fact]
    public async Task The_delay_the_ai_asks_for_becomes_an_event_that_survives_the_loop()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();
        var events = new MissionEventStore(db);
        var conversation = new FakeConversation(presence);
        var host = new FakeHost(db, root, presence, conversation) { Events = events };
        var noon = new DateTimeOffset(DateTime.Today.AddHours(12), DateTimeOffset.Now.Offset);
        events.Raise(MissionEventIds.Review(noon), MissionEventKind.Review, noon);
        File.WriteAllText(Path.Combine(host.AgentHome, ".tradeagent", "next.json"),
            """{"after_seconds": 600}""");

        var wait = await new MissionLoop(host, NoHeartbeat, now: () => noon).TurnAsync();

        Assert.InRange(wait, TimeSpan.FromSeconds(590), TimeSpan.FromSeconds(600));
        var self = events.OfKind(MissionEventKind.Self).Single();
        Assert.Equal(MissionEventIds.Self(host.LastAttemptId!), self.Id);
        Assert.False(self.Consumed);

        // A fresh loop over the same database honours it too, and takes no turn until it is due.
        var later = new FakeConversation(presence);
        await new MissionLoop(new FakeHost(db, root, presence, later) { Events = new MissionEventStore(db) },
            NoHeartbeat, now: () => noon).TurnAsync();
        Assert.Empty(later.Sent);
    }

    /// <summary>
    /// THE SCHEDULED WAKES ARE ALWAYS IN THE FUTURE. The loop arranging its own next look must never
    /// be what makes this moment eligible — otherwise "no eligible event" is unreachable and the
    /// queue is a clock with extra steps. Exactly one of each kind is pending at a time, so an early
    /// wake does not leave a trickle of paid reviews behind it.
    ///
    /// EXACTLY ONE OF EACH KIND PER ROLE, which is what "never pile up" means once the council
    /// exists. One shared review tick would be consumed by whichever role reached it first, and the
    /// other would run only when a real event named it — a Research Director never scheduled at all
    /// on a quiet day. Two roles, two ticks, two renewals, and still nothing behind them.
    /// </summary>
    [Fact]
    public async Task The_review_tick_and_the_renewal_are_scheduled_ahead_and_never_pile_up()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();
        var events = new MissionEventStore(db);
        var conversation = new FakeConversation(presence);
        var loop = new MissionLoop(new FakeHost(db, root, presence, conversation) { Events = events },
            new MissionOptions { ReviewEvery = TimeSpan.FromMinutes(30) });

        for (var i = 0; i < 4; i++) await loop.TurnAsync();

        Assert.Empty(conversation.Sent);
        Assert.Equal(CouncilRoles.All, events.OfKind(MissionEventKind.Review).Select(e => e.For).ToArray());
        Assert.Equal(CouncilRoles.All, events.OfKind(MissionEventKind.Renewal).Select(e => e.For).ToArray());
        Assert.True(events.NextDueAt() > DateTimeOffset.UtcNow, "a scheduled wake was already due");
    }

    /// <summary>
    /// A QUIET CARD IS THE ORDINARY STATE OF A QUIET DAY, and the owner cannot tell that from a
    /// broken one unless it says which. The line names the minute and the reason.
    /// </summary>
    [Fact]
    public async Task The_card_says_what_the_loop_is_waiting_for()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();
        var conversation = new FakeConversation(presence);
        var loop = new MissionLoop(new FakeHost(db, root, presence, conversation)
        { Events = new MissionEventStore(db) },
            new MissionOptions { ReviewEvery = TimeSpan.FromMinutes(30) },
            delay: (_, ct) => Task.Delay(1, ct));

        loop.Start();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (loop.Status.WaitingFor is null && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);
        var status = loop.Status;
        await loop.PauseAsync();

        Assert.Empty(conversation.Sent);                    // it waited; it did not work
        Assert.Equal(MissionState.Waiting, status.State);
        // Whichever of the two scheduled wakes is nearer. A test run at 23:55 has the day turning
        // over before the next half-hourly look, and pinning one of them would be pinning the hour
        // the suite happened to run at.
        Assert.True(status.WaitingFor is "the next scheduled look" or "the day to turn over",
            $"the card said it was waiting for: {status.WaitingFor}");
        Assert.Contains("waiting until", DashboardPage.MissionSentence(status));
        Assert.EndsWith($" for {status.WaitingFor}", DashboardPage.MissionSentence(status));
    }

    /// <summary>The four readings of the line, without a loop, so the wording itself is pinned.</summary>
    [Theory]
    [InlineData(true, "the day to turn over", "waiting until")]
    [InlineData(true, null, "waiting until")]
    [InlineData(false, "nothing is scheduled", "waiting for nothing is scheduled")]
    [InlineData(false, null, "waiting")]
    public void The_waiting_line_reads_the_minute_and_the_reason(bool hasTime, string? why, string expected)
    {
        var at = hasTime ? DateTimeOffset.UtcNow.AddMinutes(30) : (DateTimeOffset?)null;
        var line = DashboardPage.MissionSentence(
            new MissionStatus(MissionState.Waiting, at, 3, 0, null) { WaitingFor = why });

        Assert.StartsWith(expected, line);
        if (why is not null && hasTime) Assert.EndsWith($" for {why}", line);
    }

    // ---- 2. what the next turn waits for -----------------------------------------------------

    /// <summary>Nothing asked for, nothing gone wrong: the next turn starts at once.</summary>
    [Fact]
    public async Task A_turn_that_ended_cleanly_is_followed_at_once()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();
        var loop = new MissionLoop(new FakeHost(db, root, presence, new FakeConversation(presence)));

        Assert.Equal(TimeSpan.Zero, await loop.TurnAsync());
    }

    /// <summary>
    /// The AI asks to be left alone, in its own file, and is — up to the cap. The cap is what stops
    /// one bad number from taking the product off the air: <c>{"after_seconds": 86400}</c> is a day.
    /// </summary>
    [Theory]
    [InlineData("{\"after_seconds\": 120}", 120)]
    [InlineData("{\"after_seconds\": 86400}", 1800)]
    [InlineData("{\"after_seconds\": 0}", 0)]
    [InlineData("{\"after_seconds\": -5}", 0)]
    [InlineData("{\"after_seconds\": \"soon\"}", 0)]
    [InlineData("not json at all", 0)]
    [InlineData("{}", 0)]
    public async Task The_delay_the_ai_asks_for_is_honoured_and_capped(string json, int expectedSeconds)
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();
        var host = new FakeHost(db, root, presence, new FakeConversation(presence));
        var next = Path.Combine(host.AgentHome, ".tradeagent", "next.json");
        File.WriteAllText(next, json);

        var wait = await new MissionLoop(host).TurnAsync();

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), wait);
        // Consumed: one request to be left alone for half an hour must not make every turn half an
        // hour apart for the rest of the installation's life.
        Assert.False(File.Exists(next));
    }

    /// <summary>
    /// While turns keep ending badly the loop backs off, doubling from thirty seconds to half an
    /// hour and no further — and one clean turn puts it straight back to work.
    /// </summary>
    [Fact]
    public async Task Turns_that_end_in_error_back_off_by_doubling_and_recover_at_once()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();
        var conversation = new FakeConversation(presence) { Exit = 2 };
        var loop = new MissionLoop(new FakeHost(db, root, presence, conversation));

        var waits = new List<double>();
        for (var i = 0; i < 8; i++) waits.Add((await loop.TurnAsync()).TotalSeconds);

        Assert.Equal([30, 60, 120, 240, 480, 960, 1800, 1800], waits);
        Assert.Equal(8, loop.Status.ConsecutiveErrors);

        conversation.Exit = 0;
        Assert.Equal(TimeSpan.Zero, await loop.TurnAsync());
        Assert.Equal(0, loop.Status.ConsecutiveErrors);
    }

    // ---- 3. the session, and what crosses it -------------------------------------------------

    /// <summary>
    /// Twenty turns are resumed into one CLI session; the twenty-first starts a fresh one. Resuming
    /// for ever grows one context until the runtime refuses it, and nothing is lost by starting
    /// again: the files are the memory, which is what every turn's last sentence says.
    /// </summary>
    [Fact]
    public async Task A_fresh_cli_session_starts_every_n_turns_and_the_files_are_what_crosses()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();
        var conversation = new FakeConversation(presence);
        var loop = new MissionLoop(new FakeHost(db, root, presence, conversation),
            new MissionOptions { TurnsPerSession = 3 });

        for (var i = 0; i < 7; i++) await loop.TurnAsync();

        Assert.Equal(3, conversation.Sessions);     // one to start with, then two fresh ones
        Assert.All(conversation.Sent, s => Assert.Contains(MissionSituation.Continue, s));
    }

    /// <summary>
    /// STOP AI TRADING TAKES THE AI'S PERMISSION TO TRADE, NOT ITS WORK. There is plenty to do while
    /// execution is off — research, backtesting, writing strategies, keeping the journal — and a
    /// kill switch that also silenced the loop would make the safest setting the least useful one.
    /// The loop reads no permission of any kind; it only tells the AI what it is allowed to do.
    /// </summary>
    [Fact]
    public async Task Stopping_ai_trading_does_not_stop_the_loop()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();
        var conversation = new FakeConversation(presence);
        var host = new FakeHost(db, root, presence, conversation)
        {
            Next = new MissionSituation
            {
                Mode = "PAPER",
                ExecutionAvailable = false,
                ExecutionBlockedReason = "you pressed STOP AI TRADING"
            }
        };
        var loop = new MissionLoop(host);

        await loop.TurnAsync();
        await loop.TurnAsync();

        Assert.Equal(2, conversation.Sent.Count);
        Assert.All(conversation.Sent,
            s => Assert.Contains("execution_available: false — you pressed STOP AI TRADING", s));
    }

    // ---- 4. the message a turn is given ------------------------------------------------------

    /// <summary>
    /// The owner's own words come FIRST, above the time, the mode and the account. Somebody typed a
    /// question while the AI was working; everything else in the block is context the AI did not ask
    /// for, and a Situation that buries the question under nine lines of state gets it answered nine
    /// turns late.
    /// </summary>
    [Fact]
    public async Task What_the_owner_typed_while_it_worked_is_the_first_thing_in_the_next_turn()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();
        var conversation = new FakeConversation(presence);
        var host = new FakeHost(db, root, presence, conversation)
        {
            Next = new MissionSituation { Mode = "PAPER", Account = "SIM-1", Guidance = "stay in ES" }
        };
        var loop = new MissionLoop(host);

        conversation.Queue("stop buying NQ");
        await loop.TurnAsync();

        var sent = conversation.Sent.Single();
        Assert.Contains("> stop buying NQ", sent);
        Assert.True(sent.IndexOf("stop buying NQ", StringComparison.Ordinal)
                    < sent.IndexOf("Trading mode", StringComparison.Ordinal),
            $"the owner's words came after the state:\n{sent}");

        // Taken once. Repeating it every turn would have the AI answering the same question for ever.
        await loop.TurnAsync();
        Assert.DoesNotContain("stop buying NQ", conversation.Sent[1]);
    }

    /// <summary>
    /// Everything the block is required to carry, in the words a turn actually receives. Written as
    /// one assertion per fact because a missing line here is an AI reasoning about a world that is
    /// not the one it is in.
    /// </summary>
    [Fact]
    public void The_situation_block_says_the_time_the_mode_the_account_and_what_is_new()
    {
        var text = new MissionSituation
        {
            LocalTime = new DateTimeOffset(2026, 9, 6, 14, 2, 0, TimeSpan.Zero),
            Mode = "LIVE_CONFIRM",
            ExecutionAvailable = true,
            Account = "REAL-42",
            Positions = ["ES +1 at 5000"],
            OpenOrders = 2,
            UnconfirmedRequests = 1,
            NewMaterial = ["strategy.pdf"],
            Guidance = "stay in ES"
        }.Text();

        Assert.Contains("## Situation", text);
        Assert.Contains("- Trading mode: LIVE_CONFIRM", text);
        Assert.Contains("- execution_available: true", text);
        Assert.Contains("- Account: REAL-42", text);
        Assert.Contains("- Open orders: 2, unconfirmed: 1", text);
        Assert.Contains("- Positions: ES +1 at 5000", text);
        Assert.Contains("- New in `../inbox` since your last turn: strategy.pdf", text);
        Assert.Contains("- The owner's guidance: stay in ES", text);
        Assert.EndsWith(MissionSituation.Continue + Environment.NewLine, text);
    }

    /// <summary>
    /// THE TIME IS THE OWNER'S WALL CLOCK, not the offset the app happened to build the value with.
    /// The AI reasons about market hours from this line, and a turn told it is 14:02 when the person
    /// it works for is looking at 16:02 will decide the market is shut.
    ///
    /// Asserted by rendering ONE instant from two different offsets: the two blocks have to come out
    /// identical, which they only can if both were converted to this machine's local zone.
    /// </summary>
    [Fact]
    public void The_local_time_is_local_however_the_value_reached_the_block()
    {
        var instant = new DateTimeOffset(2026, 9, 6, 14, 2, 0, TimeSpan.Zero);
        var elsewhere = instant.ToOffset(TimeSpan.FromHours(9));

        var a = new MissionSituation { LocalTime = instant }.Text();
        var b = new MissionSituation { LocalTime = elsewhere }.Text();

        Assert.Equal(a, b);
        Assert.Contains($"- Local time: {instant.LocalDateTime:yyyy-MM-dd HH:mm}", a);
    }

    /// <summary>
    /// A blocked execution says WHY, because "not available" with no reason is what sends an AI
    /// round the same refused order again. An empty account and no positions are said too, rather
    /// than left out, so an absent line never has to be read as a fact.
    /// </summary>
    [Fact]
    public void A_blocked_execution_carries_its_reason_and_nothing_is_left_to_inference()
    {
        var text = new MissionSituation
        {
            Mode = "PAPER",
            ExecutionAvailable = false,
            ExecutionBlockedReason = "an order could not be confirmed"
        }.Text();

        Assert.Contains("- execution_available: false — an order could not be confirmed", text);
        Assert.Contains("- Account: not selected", text);
        Assert.Contains("- Positions: none", text);
    }

    // ---- 5. what the card reads --------------------------------------------------------------

    /// <summary>
    /// The four words the card shows, and the two numbers beside them. Nothing here is derived from
    /// a flag the loop sets hopefully: working is a turn in flight, waiting is the gap the loop
    /// itself computed, and stopped means there is no agent to run a turn through at all.
    /// </summary>
    [Fact]
    public async Task The_card_reads_stopped_paused_and_the_turn_count_from_the_loop_itself()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();
        var conversation = new FakeConversation(presence);
        var host = new FakeHost(db, root, presence, conversation: null);

        Assert.Equal(MissionState.Stopped, new MissionLoop(host).Status.State);

        // A loop started before the AI is still reads stopped: there is nothing to take a turn, and
        // "waiting until 14:32" over a turn that cannot happen is the card inventing a future.
        var early = new MissionLoop(host);
        early.Start();
        Assert.Equal(MissionState.Stopped, early.Status.State);
        await early.PauseAsync();

        host.Conversation = conversation;
        var loop = new MissionLoop(host);
        Assert.Equal(MissionState.Paused, loop.Status.State);

        await loop.TurnAsync();
        await loop.TurnAsync();

        var status = loop.Status;
        Assert.Equal(2, status.Turns);
        Assert.Equal(0, status.ConsecutiveErrors);
        Assert.Equal("Read PLAN.md, ran a backtest.", status.LastTurnFirstLine);
    }

    /// <summary>
    /// A LOOP WHOSE PURPOSE IS NOT TO STOP DOES NOT STOP ON A SURPRISE. A turn that throws before it
    /// can report how it ended — the owner pressing Send in the same instant, a runtime that
    /// vanished — is counted as a failed turn and backed off from. If it killed the loop instead,
    /// an owner who left the AI working overnight would find it stopped with no reason anywhere.
    /// </summary>
    [Fact]
    public async Task A_turn_that_throws_is_a_failed_turn_and_not_the_end_of_the_loop()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();
        var conversation = new ThrowingConversation();
        var loop = new MissionLoop(new FakeHost(db, root, presence, conversation),
            delay: (_, ct) => Task.Delay(1, ct));

        loop.Start();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (loop.Status.ConsecutiveErrors < 3 && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
        var errors = loop.Status.ConsecutiveErrors;
        await loop.PauseAsync();

        Assert.True(errors >= 3, $"errors: {errors}");
        Assert.True(conversation.Attempts >= 3, $"attempts: {conversation.Attempts}");
    }

    /// <summary>A conversation whose every turn throws something the loop has no answer for.</summary>
    sealed class ThrowingConversation : IAgentConversation
    {
        public int Attempts;
        public bool Busy => false;
        public IReadOnlyList<ChatTurn> History => [];

        public event Action<ChatTurn>? TurnAdded;
        public event Action<string>? Delta;
        public event Action? StateChanged;
        public event Action<AgentTurnEnded>? TurnEnded;

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string message, CancellationToken ct = default) => SendMissionAsync(message, ct);

        public Task SendMissionAsync(string message, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Attempts);
            TurnAdded?.Invoke(new ChatTurn(ChatRole.System, "", DateTimeOffset.UtcNow));
            Delta?.Invoke("");
            StateChanged?.Invoke();
            TurnEnded?.Invoke(new AgentTurnEnded(-1, TimeSpan.Zero, "", DateTimeOffset.UtcNow));
            throw new InvalidOperationException("the runtime went away mid-turn");
        }

        public void Queue(string message) { }
        public IReadOnlyList<string> TakeTyped() => [];
        public Task CancelAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
    }

    /// <summary>
    /// The loop really runs on its own — the turn-at-a-time driver above is how the rest of this
    /// class stays deterministic, not a claim that the timer works. Started here and stopped by the
    /// one press that stops it, with the waits collapsed so the test does not sleep.
    /// </summary>
    [Fact]
    public async Task Started_it_takes_turns_by_itself_until_it_is_paused()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var presence = new AgentPresence();
        var conversation = new FakeConversation(presence);
        var loop = new MissionLoop(new FakeHost(db, root, presence, conversation),
            delay: (_, ct) => Task.Delay(1, ct));

        loop.Start();
        Assert.True(loop.Running);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (loop.Status.Turns < 3 && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
        await loop.PauseAsync();

        Assert.False(loop.Running);
        Assert.Equal(MissionState.Paused, loop.Status.State);
        Assert.True(loop.Status.Turns >= 3, $"turns: {loop.Status.Turns}");

        var settled = loop.Status.Turns;
        await Task.Delay(50);
        Assert.Equal(settled, loop.Status.Turns);
    }
}

using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// EACH ROLE IS WOKEN FOR WHAT IT OWNS (<c>U-reconcile-wakes</c>): every wake names its role and says
/// truthfully what it is, a role with no open work is not looked at, and a renewal is bought only for a
/// role its allowance stopped.
/// </summary>
public partial class CouncilLoopTests
{
    /// <summary>
    /// (d) A NOTE WAKE IS NAMED AND QUOTED. The paper allocation and the forward-run notes are TradeAgent's
    /// own words to Research about its versions; a turn that read "Why you are awake: note" and nothing
    /// else had to spend a tool call opening the file the app already held.
    ///
    /// <para>RED at base 604a3133: the reason line read "note" and the note's text was not in the prompt.</para>
    /// </summary>
    [Fact]
    public async Task A_note_wake_is_named_and_quoted()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var host = new CouncilHost(db, root, new Concurrency());
        var now = Noon();
        var loop = new MissionLoop(host, NoHeartbeat, now: () => now);

        const string content = "Forward run of version v-1 on BTCUSDT, day 2026-10-10: 3 trades, the run continues.";
        new PublicationStore(db).Commit(new Publication
        {
            Id = Publication.IdOf("allocator", PublicationKind.Note, content),
            Role = "allocator",
            Kind = PublicationKind.Note,
            Recipients = CouncilRoles.Research,
            Classification = PublicationClass.Council,
            CreatedAt = now,
            Content = content
        }, now, MissionEventIds.PaperRun("deployment-1", "2026-10-10"));

        await loop.TurnAsync();

        var (role, prompt) = Assert.Single(host.Opened);
        Assert.Equal(CouncilRoles.Research, role);
        Assert.Contains("- Why you are awake: TradeAgent's forward-run note arrived in `in/`" + Environment.NewLine,
            prompt, StringComparison.Ordinal);
        Assert.Contains("> " + content, prompt, StringComparison.Ordinal);
        Assert.Contains("A note TradeAgent wrote itself", prompt, StringComparison.Ordinal);
    }

    // ---- item 2: a role is looked at only while it owns open work ---------------------------------

    /// <summary>
    /// Sleeps the loop through a quiet span as the app does — to the earliest wake, every turn due there taken —
    /// and answers the launches it made, as (role, prompt).
    /// </summary>
    static async Task<List<(string Role, string Prompt)>> Quiet(CouncilHost host, MissionOptions options,
        DateTimeOffset from, TimeSpan span)
    {
        var events = host.Events!;
        var now = from;
        var loop = new MissionLoop(host, options, now: () => now);
        var before = host.Opened.Count;

        await loop.TurnAsync();      // the first pass schedules; nothing is due yet
        var end = from + span;
        for (var step = 0; step < 400 && events.NextDueAt() is { } next && next <= end; step++)
        {
            now = next;
            for (var i = 0; i < 10 && events.RolesDue(now).Count > 0; i++) await loop.TurnAsync();
        }
        return host.Opened.Skip(before).ToList();
    }

    static readonly MissionOptions HalfHourLooks = new() { ReviewEvery = TimeSpan.FromMinutes(30) };

    static DateTimeOffset LocalMidnight() => new(DateTime.Today, DateTimeOffset.Now.Offset);

    /// <summary>
    /// (a) AN IDLE DAY COSTS ONLY THE LOOKS AN OPEN OBJECTIVE ASKS FOR. Twenty-four hours in which nothing happens,
    /// from local midnight to the next: the chair owns nothing open — no message, no request, an empty book, no
    /// boundary — and is not looked at; Research's standing mandate keeps its look at the pace <c>U-quiet-review</c>
    /// gave it; and neither role was stopped by its allowance, so no renewal is bought.
    ///
    /// <para>RED at base 604a3133's schedule (a look and a renewal per role whatever it owns), with this host saying
    /// what each role owns: "18 paid turns in 24 quiet hours — Operations 9, Research 9, renewals 2".</para>
    /// </summary>
    [Fact]
    public async Task An_idle_day_costs_only_the_looks_an_open_objective_asks_for()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var host = new CouncilHost(db, root, new Concurrency()) { Owns = true };

        var opened = await Quiet(host, HalfHourLooks, LocalMidnight(), TimeSpan.FromHours(24));

        var operations = opened.Count(o => o.Role == CouncilRoles.Operations);
        var research = opened.Count(o => o.Role == CouncilRoles.Research);
        var renewals = opened.Count(o => o.Prompt.Contains("the day turned over", StringComparison.Ordinal));
        var said = $"{opened.Count} paid turns in 24 quiet hours — Operations {operations}, Research {research}, "
                   + $"renewals {renewals}";

        Assert.True(operations == 0, said);
        Assert.True(research is >= 1 and <= 8, said);
        Assert.True(renewals == 0, said);
        Assert.Empty(host.Events!.OfKind(MissionEventKind.Renewal));
        Assert.All(opened, o => Assert.Contains(
            "- Why you are awake: a scheduled look; nothing else has happened" + Environment.NewLine, o.Prompt));
    }

    /// <summary>
    /// (b) A RENEWAL WAKES ONLY A ROLE ITS ALLOWANCE STOPPED. The renewal exists to tell a role that stopped at its
    /// share of the day that it may work again; a role nothing stopped is not paid a turn at midnight to be told so.
    ///
    /// <para>RED at base 604a3133: both roles had a renewal raised, the one with room to work included.</para>
    /// </summary>
    [Fact]
    public async Task A_renewal_wakes_only_a_role_its_allowance_stopped()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var host = new CouncilHost(db, root, new Concurrency()) { Owns = true };
        // RESEARCH HAS SPENT ITS HALF OF A 2.00 DAY; the chair has room.
        host.Spending[CouncilRoles.Research] = Reading(CouncilRoles.Research, spent: 1m, cap: 2m, share: 0.5m);
        Assert.False(host.SpendFor(CouncilRoles.Research).AdmitsAnotherTurn);
        Assert.True(host.SpendFor(CouncilRoles.Operations).AdmitsAnotherTurn);

        var now = Noon();
        await new MissionLoop(host, NoHeartbeat, now: () => now).TurnAsync();

        var renewal = Assert.Single(host.Events!.OfKind(MissionEventKind.Renewal));
        Assert.Equal(CouncilRoles.Research, renewal.For);
        Assert.True(renewal.DueAt > now, "the renewal was raised already due");
        Assert.Empty(host.Opened);
    }

    /// <summary>
    /// (e) AN OPEN POSITION KEEPS OPERATIONS' LOOK, AND NO BROKER IS CALLED TO KNOW IT. The book is the chair's to
    /// watch; the host hands over the positions its last Situation already read, and the loop decides from that.
    /// The same four quiet hours with an empty book look at the chair not once.
    ///
    /// <para>RED with the open-position instance dropped (the mutant): the chair holding a position was never
    /// looked at.</para>
    /// </summary>
    [Fact]
    public async Task An_open_position_keeps_operations_look_without_a_broker_call()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var holding = new CouncilHost(db, root, new Concurrency()) { Owns = true, Book = ["ES +1"] };
        var held = await Quiet(holding, HalfHourLooks, Noon(), TimeSpan.FromHours(4));
        Assert.True(held.Count(o => o.Role == CouncilRoles.Operations) >= 1,
            $"the chair holding ES +1 was looked at {held.Count(o => o.Role == CouncilRoles.Operations)} times in four quiet hours");

        var (db2, root2) = Workspace();
        using var _2 = db2;
        var flat = new CouncilHost(db2, root2, new Concurrency()) { Owns = true, Book = [] };
        var none = await Quiet(flat, HalfHourLooks, Noon(), TimeSpan.FromHours(4));
        Assert.DoesNotContain(none, o => o.Role == CouncilRoles.Operations);

        // AN UNREAD BOOK IS NOT AN EMPTY ONE: a host with no reading keeps the chair's look.
        var (db3, root3) = Workspace();
        using var _3 = db3;
        var unread = new CouncilHost(db3, root3, new Concurrency()) { Owns = true, Book = null };
        var blind = await Quiet(unread, HalfHourLooks, Noon(), TimeSpan.FromHours(4));
        Assert.Contains(blind, o => o.Role == CouncilRoles.Operations);

        // AND THE APP'S HOST ANSWERS FROM ITS LAST READ, never by asking the platform: the member is one
        // synchronous line over the ledgers and the book the Situation stored.
        var source = File.ReadAllText(Path.Combine(Repo(), "src", "TradeAgent.App", "AppHost.cs"));
        var member = source[source.IndexOf("public IReadOnlyList<RoleObjective>? Objectives(string role)", StringComparison.Ordinal)..];
        member = member[..member.IndexOf(';')];
        Assert.Contains("RoleObjectives.Open(db, role, Volatile.Read(ref host._lastBook))", member, StringComparison.Ordinal);
        Assert.DoesNotContain("Async", member, StringComparison.Ordinal);
    }

    /// <summary>
    /// A REQUEST NOT YET AT THE BROKER IS STILL OPEN WORK. A request the chair placed that waits for the owner's
    /// approval, or was written and not yet dispatched, has not reached a final state, and the chair keeps its look
    /// over it with an empty book and nothing else open.
    ///
    /// <para>RED at a2e839e6 (review 1, finding 1): the objective read <c>ExecutionRequestStore.Open()</c>, which
    /// leaves out <c>CREATED</c> and <c>AWAITING_APPROVAL</c>, so the chair's look was withdrawn.</para>
    /// </summary>
    [Theory]
    [InlineData(ExecutionState.CREATED)]
    [InlineData(ExecutionState.AWAITING_APPROVAL)]
    public async Task A_request_waiting_before_the_broker_keeps_operations_look(ExecutionState state)
    {
        var (db, root) = Workspace();
        using var _ = db;
        var requests = new ExecutionRequestStore(db);
        requests.TryCreate(new ExecutionRequest
        {
            RequestId = "chair-1", ConnectorId = "fake", AccountId = "SIM-001", Instrument = "ES",
            Intent = RequestIntent.PLACE, ParametersJson = "{}", ClientOrderId = "TA-chair-1",
            CreatedAt = DateTimeOffset.UtcNow, State = ExecutionState.CREATED, Mode = TradingMode.PAPER
        });
        if (state != ExecutionState.CREATED) requests.Transition("chair-1", ExecutionState.CREATED, state);

        Assert.Contains(new RoleObjective(RoleObjectives.RequestFinal, "chair-1"),
            RoleObjectives.Open(db, CouncilRoles.Operations, []));

        var host = new CouncilHost(db, root, new Concurrency()) { Owns = true, Book = [] };
        var now = Noon();
        await new MissionLoop(host, HalfHourLooks, now: () => now).TurnAsync();
        Assert.True(host.Events!.HasUnconsumed(MissionEventKind.Review, CouncilRoles.Operations),
            $"the chair's request at {state} raised no look");
    }

    /// <summary>
    /// THE OWNER'S WORDS A LOST TURN TOOK REACH THE CHAIR'S NEXT TURN FIRST, AND THAT TURN CLOSES THEM. A launch
    /// consumes the owner's message in its own write; a crash after that write leaves it consumed by a launch a
    /// restart declares LOST. The loop re-raises those words once, as it does a failed turn's, so the chair's next
    /// admitted turn is shown them first; that turn's outcome settles them; and nothing stays open that no turn can
    /// close — four quiet hours with a flat book buy that one turn and no look after it.
    ///
    /// <para>Review 1, finding 2, as the re-review of 053c03ff corrected it. RED at 053c03ff: the lost message stayed
    /// an objective that no turn could take, so the chair was bought scheduled looks that never quoted the words.</para>
    /// </summary>
    [Fact]
    public async Task The_owners_words_a_lost_turn_took_reach_the_chairs_next_turn_first_and_then_close()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var events = new MissionEventStore(db);
        var now = Noon();
        const string said = "close the ES position before the weekend";
        Assert.True(events.RecordOwnerMessage(said, now));
        var owner = Assert.Single(events.OfKind(MissionEventKind.Owner));

        // THE LAUNCH'S WRITE: the chair's turn admitted, the owner's words consumed in the same commit.
        var lost = TurnMeter.IdMintedAt(now);
        Assert.True(new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = lost, StartedAt = now, ReservedCost = 0.10m, Role = CouncilRoles.Operations
        }, [owner.Id]).Admitted);

        // THE CRASH, AND THE RESTART: the app's meter opens the database and loses the launch nobody will report.
        var restarted = new TurnMeter(db, () => 100m, now: () => now.AddMinutes(1), live: new LiveAttempts(),
            recordPath: Path.Combine(root, "turns.jsonl"));
        Assert.NotNull(restarted);
        Assert.Equal(AiAttemptState.LOST, new AiAttemptStore(db).Get(lost)!.State);

        // THE SCHEDULED LOOKS, A FLAT BOOK, FOUR QUIET HOURS.
        var host = new CouncilHost(db, root, new Concurrency()) { Owns = true, Book = [] };
        var opened = await Quiet(host, HalfHourLooks, now.AddMinutes(2), TimeSpan.FromHours(4));
        var chair = opened.Where(o => o.Role == CouncilRoles.Operations).ToList();
        var said1 = $"{chair.Count} chair turns in four quiet hours; first quotes the words: "
                    + (chair.Count > 0 && chair[0].Prompt.Contains(said, StringComparison.Ordinal));

        var turn = Assert.Single(chair);
        Assert.True(turn.Prompt.Contains(said, StringComparison.Ordinal), said1);
        Assert.True(turn.Prompt.IndexOf(said, StringComparison.Ordinal)
                    < turn.Prompt.IndexOf("Why you are awake", StringComparison.Ordinal), "the owner's words are not first");
        Assert.Empty(RoleObjectives.Open(db, CouncilRoles.Operations, []));
        Assert.All(events.OfKind(MissionEventKind.Owner), e => Assert.NotNull(e.Disposition));
    }

    /// <summary>
    /// THE LOST WORDS' SETTLEMENT AND THEIR RETRY ARE ONE COMMIT (pass 3, item 1). A process killed after the original
    /// was settled <c>failed</c> and before its retry was written left a message no later pass recovers — it is no
    /// longer LOST-and-unsettled — and no row carrying the words: the owner's question gone, with its receipt shown.
    /// The kill is a trigger that refuses the retry's row, exactly where the process would have died; the restart
    /// drops it.
    ///
    /// <para>RED at bda825ef: the chair was never woken for the words again — "Assert.Single() Failure: The collection was empty".</para>
    /// </summary>
    [Fact]
    public async Task A_crash_between_settling_the_lost_words_and_writing_their_retry_leaves_them_owed()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var events = new MissionEventStore(db);
        var now = Noon();
        const string said = "close the ES position before the weekend";
        Assert.True(events.RecordOwnerMessage(said, now));
        var owner = Assert.Single(events.OfKind(MissionEventKind.Owner));

        var lost = TurnMeter.IdMintedAt(now);
        Assert.True(new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = lost, StartedAt = now, ReservedCost = 0.10m, Role = CouncilRoles.Operations
        }, [owner.Id]).Admitted);
        Assert.NotNull(new TurnMeter(db, () => 100m, now: () => now.AddMinutes(1), live: new LiveAttempts(),
            recordPath: Path.Combine(root, "turns.jsonl")));
        Assert.Equal(AiAttemptState.LOST, new AiAttemptStore(db).Get(lost)!.State);

        // THE KILL: the retry's row is refused, so the pass dies between the settlement and the retry.
        Sql(db, "CREATE TRIGGER killed_before_the_retry BEFORE INSERT ON mission_event WHEN NEW.kind = 'owner' "
                + "BEGIN SELECT RAISE(ABORT, 'the process was killed before the retry was written'); END");
        var host = new CouncilHost(db, root, new Concurrency()) { Owns = true, Book = [] };
        await new MissionLoop(host, HalfHourLooks, now: () => now.AddMinutes(2)).TurnAsync();

        // THE RESTART.
        Sql(db, "DROP TRIGGER killed_before_the_retry");
        var opened = await Quiet(host, HalfHourLooks, now.AddMinutes(3), TimeSpan.FromHours(4));
        var chair = opened.Where(o => o.Role == CouncilRoles.Operations).ToList();
        var count = $"{chair.Count} chair turns in four quiet hours";

        var turn = Assert.Single(chair);
        Assert.True(turn.Prompt.Contains(said, StringComparison.Ordinal), count);
        Assert.True(turn.Prompt.IndexOf(said, StringComparison.Ordinal)
                    < turn.Prompt.IndexOf("Why you are awake", StringComparison.Ordinal), "the owner's words are not first");
        Assert.Single(events.OfKind(MissionEventKind.Owner),
            e => Json.Read<MissionOwnerMessage>(e.Payload ?? "")?.RetryOf == owner.Id);
    }

    /// <summary>
    /// THE LOST WORDS COME FIRST WHATEVER THE BACKLOG (pass 3, item 3). A turn is handed the oldest
    /// <see cref="MissionEventStore.BatchLimit"/> due wakes, and the retry is written at the recovery: fifty fills the
    /// chair is owed from the downtime, all older than the retry, pushed the owner's words out of the chair's next turn.
    ///
    /// <para>RED at bda825ef: the chair's next turn did not carry the words.</para>
    /// </summary>
    [Fact]
    public async Task The_lost_words_reach_the_chairs_next_turn_whatever_the_backlog()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var events = new MissionEventStore(db);
        var now = Noon();
        const string said = "close the ES position before the weekend";
        Assert.True(events.RecordOwnerMessage(said, now));
        var owner = Assert.Single(events.OfKind(MissionEventKind.Owner));

        var lost = TurnMeter.IdMintedAt(now);
        Assert.True(new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = lost, StartedAt = now, ReservedCost = 0.10m, Role = CouncilRoles.Operations
        }, [owner.Id]).Admitted);
        Assert.NotNull(new TurnMeter(db, () => 100m, now: () => now.AddMinutes(1), live: new LiveAttempts(),
            recordPath: Path.Combine(root, "turns.jsonl")));

        // THE BACKLOG: a full batch of the chair's fills, every one older than the recovery.
        for (var i = 0; i < MissionEventStore.BatchLimit; i++)
            Assert.True(events.Raise($"fill:backlog-{i}", MissionEventKind.Fill, now.AddSeconds(61 + i),
                role: CouncilRoles.Operations));

        var host = new CouncilHost(db, root, new Concurrency()) { Owns = true, Book = [] };
        await new MissionLoop(host, HalfHourLooks, now: () => now.AddMinutes(3)).TurnAsync();

        var (role, prompt) = Assert.Single(host.Opened);
        Assert.Equal(CouncilRoles.Operations, role);
        Assert.True(prompt.Contains(said, StringComparison.Ordinal), "the chair's next turn does not carry the words");
        Assert.True(prompt.IndexOf(said, StringComparison.Ordinal)
                    < prompt.IndexOf("Why you are awake", StringComparison.Ordinal), "the owner's words are not first");
    }

    static void Sql(Database db, string sql) => db.Write(_ =>
    {
        using var c = db.Cmd(sql);
        return c.ExecuteNonQuery();
    });

    static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }
}

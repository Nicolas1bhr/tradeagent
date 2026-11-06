using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ONE BAD STREAM OR ONE HUNG TURN CANNOT STALL A SEAT (<c>U-turn-bounds</c>; R30 § 3 row 7, R28 W1, W2, W5, W6, W8, D17). A 72-hour run has nobody to
/// restart it, so each of these used to be the end of a seat or of the whole lane until someone did.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class TurnBoundsTests : IDisposable
{
    readonly Database _db = TestEnv.NewDb();
    readonly string _records = Path.Combine(TestEnv.Home, $"turn-bounds-{Guid.NewGuid():n}");

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(RuntimeCatalog.OverridePath)) File.Delete(RuntimeCatalog.OverridePath);
    }

    static readonly string Ops = CouncilRoles.Operations;
    static readonly string Research = CouncilRoles.Research;
    static readonly DateTimeOffset Noon = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    static RuntimeManifest Seat(string name) => VendorLimitTests.Codex(name);

    // ---- W1: a mark expires -----------------------------------------------------------------------------------------

    /// <summary>
    /// RED at base (a static dictionary marked for the life of the process, R28 W1): A MARK HOLDS FOR ITS TIME, THEN ONE PROBE MAY RUN. The look that
    /// claims nothing lets nothing through; the first launch to ask is the probe, the same launch may ask again before its process starts, and
    /// every other launch of the seat stays refused while it runs.
    /// </summary>
    [Fact]
    public void A_mark_holds_for_its_time_and_then_lets_one_probe_through()
    {
        var seat = Seat(nameof(A_mark_holds_for_its_time_and_then_lets_one_probe_through));
        const string model = "mark-expires";
        var words = SeatMarks.Set(seat, null, model, SeatTrouble.ToolsUnavailable, "Code Mode is unavailable because it is off.", Noon);
        object first = new(), second = new();

        Assert.Equal(words, SeatMarks.Refusal(seat, null, model, first, Noon));
        Assert.Equal(words, SeatMarks.Refusal(seat, null, model, first, Noon + SeatMarks.FirstHold - TimeSpan.FromSeconds(1)));

        var over = Noon + SeatMarks.FirstHold;
        Assert.Null(SeatMarks.Refusal(seat, null, model, first, over));
        Assert.Null(SeatMarks.Refusal(seat, null, model, first, over));
        Assert.Contains("one turn is trying it now", SeatMarks.Refusal(seat, null, model, second, over));

        // A PROBE THAT NEVER REPORTED BACK DOES NOT HOLD THE SEAT FOR EVER: the window passes and another launch tries.
        Assert.Null(SeatMarks.Refusal(seat, null, model, second, over + SeatMarks.ProbeWindow));

        // A PROBE THAT SETTLED NOTHING WAS STILL THE ONE PROBE: its own next launch waits out the hold like any other.
        var failing = new object();
        var later = over + SeatMarks.ProbeWindow * 2;
        Assert.Null(SeatMarks.Refusal(seat, null, model, failing, later));
        SeatMarks.ProbeEnded(seat, null, model, failing, later);
        Assert.NotNull(SeatMarks.Refusal(seat, null, model, failing, later));
        Assert.Null(SeatMarks.Refusal(seat, null, model, failing, later + SeatMarks.FirstHold));

        // A CLEAN TURN ENDS THE MATTER: every launch of the seat runs, and the next trouble is a first strike.
        SeatMarks.Clear(seat, null, model);
        Assert.Null(SeatMarks.Refusal(seat, null, model, new object(), over));
        Assert.Null(SeatMarks.Refusal(seat, null, model));
    }

    /// <summary>
    /// A SEAT THAT FAILS ITS PROBES BACKS OFF, TO A CAP: an hour, two, four, eight and no further, so a seat that never recovers is tried a few
    /// times a day and a seat the vendor brings back is found within the working day. A second lane's mark of the same trouble is no second strike.
    /// </summary>
    [Fact]
    public void Repeated_marks_back_off_to_the_cap_and_a_second_lane_is_no_second_strike()
    {
        var seat = Seat(nameof(Repeated_marks_back_off_to_the_cap_and_a_second_lane_is_no_second_strike));
        const string model = "marks-back-off";
        var now = Noon;
        foreach (var hours in new[] { 1, 2, 4, 8, 8, 8 })
        {
            var words = SeatMarks.Set(seat, null, model, SeatTrouble.ToolsUnavailable, "Defaulting to fallback metadata", now);
            Assert.Equal(words, SeatMarks.Set(seat, null, model, SeatTrouble.ToolsUnavailable, "Defaulting to fallback metadata", now));

            var held = now + TimeSpan.FromHours(hours);
            Assert.NotNull(SeatMarks.Refusal(seat, null, model, new object(), held - TimeSpan.FromSeconds(1)));
            var probe = new object();
            Assert.Null(SeatMarks.Refusal(seat, null, model, probe, held));
            now = held;
        }
    }

    /// <summary>
    /// A REAL SESSION PROBES: a turn that meets a mark past its hold runs, and its clean end clears the seat. RED at base: the mark stood until the
    /// process ended, so this launch was refused and nothing could ever clear it.
    /// </summary>
    [Fact]
    public async Task A_clean_probe_turn_clears_the_seat()
    {
        const string clean = """
            {"type":"thread.started","thread_id":"00000000-0000-0000-0000-000000000000"}
            {"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"All good."}}
            {"type":"turn.completed","usage":{"input_tokens":10,"cached_input_tokens":0,"output_tokens":2}}
            """;
        var name = nameof(A_clean_probe_turn_clears_the_seat);
        RuntimeManifest seat = null!;
        var session = AgentRuntimeProbe.SessionOverStream(clean, manifest: script => seat = VendorLimitTests.Codex(script), name: name);
        SeatMarks.Set(seat, null, null, SeatTrouble.ToolsUnavailable, "Code Mode is unavailable because it is off.", DateTimeOffset.UtcNow - SeatMarks.FirstHold - TimeSpan.FromSeconds(1));

        Assert.False(session.CheckSeat().Refused, "the seat stayed refused past its mark's hold");
        await session.SendMissionAsync("continue");

        Assert.Equal(ChatRole.Ai, session.History.Last().Role);
        Assert.Null(SeatMarks.Refusal(seat, null, null, new object()));
    }

    // ---- W2: every turn has a wall-clock limit ------------------------------------------------------------------------

    /// <summary>
    /// A HUNG TURN ENDS AT ITS LIMIT, WHOLE. The chair's puppet takes the owner's words, starts a child that holds the turn's stdout, and hangs; the
    /// loop ends it at its limit. Its whole tree is gone, its row is closed in words saying why with its reservation as its cost, its lease is
    /// released, the words stay owed and blocked with the same sentence, and the next turn answers them and is told its last turn was cut.
    /// </summary>
    [Fact]
    public async Task A_hung_turn_ends_at_its_limit_with_its_tree_and_nothing_it_was_woken_for_is_lost()
    {
        const string words = "Close the book before the open.";
        await using var life = await PuppetLife.Begin("turn-limit");
        life.Host.TurnLimitUnderTest = TimeSpan.FromSeconds(4);
        var overLimit = Labels.TurnPastItsLimit(TimeSpan.FromSeconds(4));
        life.Script(Ops, ["signal hung", "child kid", "hang"], ["usage 40 0 20", "final Understood."]);

        Assert.True(life.Host.RecordOwnerMessage(words));
        await life.RunUntil(() => life.Reached("kid.pid"), "the chair's puppet started its child and hung");
        var hung = Assert.Single(life.AttemptsOf(Ops));
        Assert.Equal(AiAttemptState.LAUNCHED, hung.State);
        var puppet = int.Parse(File.ReadAllLines(Path.Combine(life.Scripts, "launches.log")).Last().Split(' ')[1]);
        var kid = int.Parse(File.ReadAllText(Path.Combine(life.Scripts, "kid.pid")));

        await life.RunUntil(() => new AiAttemptStore(life.Host.Db).Get(hung.Id)!.State != AiAttemptState.LAUNCHED, "the hung turn's row closed");

        Assert.False(Alive(puppet), "the hung puppet outlived its limit");
        Assert.False(Alive(kid), "the hung puppet's child outlived its limit");

        // HONEST ON THE ROW: why it ended, and its reservation as its cost, because nothing reported what it used.
        var row = new AiAttemptStore(life.Host.Db).Get(hung.Id)!;
        Assert.Equal(AiAttemptState.ENDED, row.State);
        Assert.Equal(overLimit, RefusedOn(row));
        Assert.Equal(hung.ReservedCost, row.Cost);

        // THE LEASE IS RELEASED AND THE WORDS STAY OWED, shown undelivered in the same sentence, and the position stands aside for its backoff.
        await life.RunUntil(() => life.Host.Mission.StandingAsideFor(Ops) is not null && life.Host.Mission.Status.State != MissionState.Working,
            "the lease was released and the position stood aside");
        var owed = Assert.Single(life.Host.Wakes!.OfKind(MissionEventKind.Owner));
        Assert.False(owed.Consumed);
        Assert.Equal(MissionEventDisposition.Blocked, owed.Disposition);
        Assert.Equal(overLimit, owed.DispositionDetail);
        Assert.Equal(overLimit, life.Host.Mission.StandingAsideFor(Ops)?.Why);

        // AND THE NEXT TURN — the owner's press lifts the backoff here, so a test need not sit it out — answers them, told what happened.
        life.Host.Mission.SeatChanged(Ops);
        await life.RunUntil(() => life.Turns(Ops) == 2 && life.Host.Wakes!.OfKind(MissionEventKind.Owner).Single().Consumed, "the next turn took the words");
        Assert.Contains(words, life.Prompt(Ops, 2));
        Assert.Contains(overLimit, life.Prompt(Ops, 2));
    }

    static bool Alive(int pid)
    {
        for (var i = 0; i < 100; i++)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited) return false;
            }
            catch (ArgumentException) { return false; }
            Thread.Sleep(100);
        }
        return true;
    }

    /// <summary>What the row's context says ended it, read as the report reads it.</summary>
    static string? RefusedOn(AiAttempt row)
    {
        using var doc = JsonDocument.Parse(row.Context ?? "{}");
        return doc.RootElement.TryGetProperty("refused", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
    }

    /// <summary>
    /// THE LIMIT COMES FROM THE SEAT, one number per kind of seat, and both sit well above the one real turn measured (20 s) and below what would let
    /// a hung turn hold a seat for most of an hour.
    /// </summary>
    [Fact]
    public void The_limit_is_the_seats_one_number_per_kind_and_well_above_a_real_turn()
    {
        var allowance = new TurnAllowance(1, 1);
        Assert.Equal(SeatSpec.VendorTurnLimit, new SeatSpec("codex", "m", allowance, TurnBound.Reservation).WallClock);
        Assert.Equal(SeatSpec.HarnessTurnLimit, new SeatSpec("h", "m", allowance, TurnBound.Enforced).WallClock);
        Assert.True(SeatSpec.HarnessTurnLimit >= TimeSpan.FromSeconds(20 * 30), "a limit near a real turn would cut real work");
        Assert.True(SeatMarks.ProbeWindow > SeatSpec.VendorTurnLimit, "a probe's window must outlast the longest turn it can be");
    }

    // ---- W5 and W8: a refused role steps aside ------------------------------------------------------------------------

    static void Due(MissionEventStore events, string role, DateTimeOffset at) =>
        events.Raise(MissionEventIds.ForRole(MissionEventIds.Review(at), role), MissionEventKind.Review, at, role: role);

    /// <summary>
    /// RED at base (R28 W5): with one lane the refused role stayed oldest due and was picked first on every tick, every 5 s, and the role behind it never
    /// turned. Now it steps aside keeping its wakes, the other is served on the next look, and it is asked again only under a doubling backoff.
    /// </summary>
    [Fact]
    public async Task A_role_refused_for_its_seat_steps_aside_and_the_lane_serves_the_next()
    {
        var events = new MissionEventStore(_db);
        var now = DateTimeOffset.UtcNow;
        Due(events, Ops, now - TimeSpan.FromMinutes(3));
        Due(events, Research, now - TimeSpan.FromMinutes(2));

        var asked = new List<string>();
        var refusing = true;
        var host = new OrgLanesTests.LaneHost { Events = events };
        host.Admit = (role, _) =>
        {
            asked.Add(role);
            return role == Ops && refusing ? new AiAdmission { Refusal = "the seat is refused", Kind = RefusalKind.HostOrLaunch } : null;
        };
        var options = new MissionOptions { ReviewEvery = TimeSpan.Zero };
        var loop = new MissionLoop(host, options, now: () => now);

        Assert.Equal(TimeSpan.Zero, await loop.TurnAsync());
        Assert.Empty(host.Conv(Ops).Sent);
        Assert.Equal(1, asked.Count(r => r == Ops));
        Assert.Contains("the seat is refused", loop.StandingAsideFor(Ops)?.Why);

        // THE NEXT LOOK SERVES THE ROLE BEHIND IT, in the same second.
        await loop.TurnAsync();
        Assert.Single(host.Conv(Research).Sent);

        // NOTHING ELSE IS DUE: it sleeps out the rest, and asks nobody anything until then.
        var wait = await loop.TurnAsync();
        Assert.Equal(options.FirstBackoff, wait);
        Assert.Equal(1, asked.Count(r => r == Ops));

        // ASKED AGAIN WHEN THE REST ENDS, and the rest doubles.
        now += options.FirstBackoff;
        Assert.Equal(TimeSpan.Zero, await loop.TurnAsync());
        Assert.Equal(2, asked.Count(r => r == Ops));
        Assert.Equal(options.FirstBackoff * 2, loop.StandingAsideFor(Ops)!.Value.Until - now);

        // AND ITS WAKE WAS KEPT: when its seat takes a launch, the launch has it, and nothing stands against it any more.
        refusing = false;
        now += options.FirstBackoff * 2;
        await loop.TurnAsync();
        Assert.Single(host.Conv(Ops).Sent);
        Assert.Null(loop.StandingAsideFor(Ops));
    }

    /// <summary>
    /// RED at base (R28 W8): a due wake a refused host cannot serve made every look return 5 s, a full sweep with its writes. Now the loop sleeps out the
    /// rest — and still wakes for the next other position's wake, which a rest must never hide.
    /// </summary>
    [Fact]
    public async Task A_due_wake_nothing_can_serve_does_not_sweep_every_five_seconds()
    {
        var events = new MissionEventStore(_db);
        var now = DateTimeOffset.UtcNow;
        Due(events, Ops, now - TimeSpan.FromMinutes(3));
        var host = new OrgLanesTests.LaneHost { Events = events };
        host.Refusals[Ops] = new TurnRefusal(RefusalKind.HostOrLaunch, "this host cannot hold it");
        var options = new MissionOptions { ReviewEvery = TimeSpan.Zero };
        var loop = new MissionLoop(host, options, now: () => now);

        Assert.Equal(options.FirstBackoff, await loop.TurnAsync());
        now += TimeSpan.FromSeconds(10);
        Assert.Equal(options.FirstBackoff - TimeSpan.FromSeconds(10), await loop.TurnAsync());

        // A WAKE OF ANOTHER POSITION DUE BEFORE THE REST ENDS IS WHAT THE LOOP WAKES FOR.
        Due(events, Research, now + TimeSpan.FromSeconds(7));
        Assert.Equal(TimeSpan.FromSeconds(7), await loop.TurnAsync());
        Assert.Empty(host.Conv(Ops).Sent);
    }

    /// <summary>THE DISPATCHER READS THE SAME REST: a position stood aside takes no lane, and its wait shortens the dispatcher's sleep.</summary>
    [Fact]
    public async Task A_position_standing_aside_takes_no_lane_of_the_dispatcher()
    {
        var events = new MissionEventStore(_db);
        var now = DateTimeOffset.UtcNow;
        Due(events, Ops, now - TimeSpan.FromMinutes(3));
        var host = new OrgLanesTests.LaneHost { Events = events, Lanes = new LaneSettings(2, 2, 2) };
        host.Refusals[Ops] = new TurnRefusal(RefusalKind.HostOrLaunch, "this host cannot hold it");
        var options = new MissionOptions { ReviewEvery = TimeSpan.Zero };
        var loop = new MissionLoop(host, options, now: () => now);

        var tick = await loop.TickAsync();
        Assert.Empty(tick.Lanes);
        Assert.Equal(options.FirstBackoff, tick.Wait);
    }

    /// <summary>
    /// A POSITION WITH NOTHING TO RUN ON IS A REFUSED SEAT TOO. RED at base: no conversation answered 5 s and left it the lane's oldest due for good.
    /// </summary>
    [Fact]
    public async Task A_position_with_no_program_to_run_on_steps_aside_as_well()
    {
        var events = new MissionEventStore(_db);
        var now = DateTimeOffset.UtcNow;
        Due(events, Ops, now - TimeSpan.FromMinutes(3));
        Due(events, Research, now - TimeSpan.FromMinutes(2));
        var host = new NoChair(events);
        var loop = new MissionLoop(host, new MissionOptions { ReviewEvery = TimeSpan.Zero }, now: () => now);

        Assert.Equal(TimeSpan.Zero, await loop.TurnAsync());
        await loop.TurnAsync();
        Assert.Single(host.Inner.Conv(Research).Sent);
        Assert.Contains("no AI program is ready", loop.StandingAsideFor(Ops)?.Why);
    }

    /// <summary>The lanes host, with no conversation for the chair.</summary>
    sealed class NoChair(MissionEventStore events) : IMissionHost
    {
        public OrgLanesTests.LaneHost Inner { get; } = new() { Events = events };
        public MissionEventStore? Events => events;
        public IAgentConversation? Conversation => null;
        public IAgentConversation? ConversationFor(string role) => role == Ops ? null : Inner.ConversationFor(role);
        public string AgentHome => Inner.AgentHome;
        public bool InboxChangedSinceLastPass => false;
        public Task ScanAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<MissionSituation> SituationAsync(CancellationToken ct) => Inner.SituationAsync(ct);
        public AiAdmission BeginTurn(string prompt, IReadOnlyList<string> wakes, string role, string? attemptId) => Inner.BeginTurn(prompt, wakes, role, attemptId);
    }

    /// <summary>
    /// A HOST WITH NO WAKE QUEUE HAS NO PICK TO STEP OVER ITS ONE ROLE: a refusal sleeps the rest. Answered zero, its loop would launch again at once, for ever.
    /// </summary>
    [Fact]
    public async Task A_host_with_no_queue_sleeps_a_refusal_out_and_never_spins()
    {
        var now = DateTimeOffset.UtcNow;
        var host = new OrgLanesTests.LaneHost();
        host.Admit = (_, _) => new AiAdmission { Refusal = "the seat is refused", Kind = RefusalKind.HostOrLaunch };
        var options = new MissionOptions { ReviewEvery = TimeSpan.Zero };
        var loop = new MissionLoop(host, options, now: () => now);

        Assert.Equal(options.FirstBackoff, await loop.TurnAsync());
    }

    /// <summary>THE OWNER'S PRESS IS NOT MADE TO WAIT OUT A BACKOFF: changing the seat lifts the rest it was about, and the loop looks now.</summary>
    [Fact]
    public async Task Changing_a_seat_lifts_the_rest_it_was_about()
    {
        var events = new MissionEventStore(_db);
        var now = DateTimeOffset.UtcNow;
        Due(events, Ops, now - TimeSpan.FromMinutes(3));
        var refusing = true;
        var host = new OrgLanesTests.LaneHost { Events = events };
        host.Admit = (_, _) => refusing ? new AiAdmission { Refusal = "the model was refused", Kind = RefusalKind.HostOrLaunch } : null;
        var loop = new MissionLoop(host, new MissionOptions { ReviewEvery = TimeSpan.Zero }, now: () => now);

        await loop.TurnAsync();
        Assert.NotNull(loop.StandingAsideFor(Ops));

        refusing = false;
        loop.SeatChanged(Ops);
        await loop.TurnAsync();
        Assert.Single(host.Conv(Ops).Sent);
    }

    // ---- W6 and D17: a refused model ----------------------------------------------------------------------------------

    /// <summary>The vendor's own fixture for a retired model (vendor <c>core/tests/suite/compact.rs</c>:2603 at rust-v0.160.1), in its error event.</summary>
    const string Refused = "The 'gpt-6-luna' model is not supported when using Codex with a ChatGPT account.";

    static string RefusedStream(string before = "") =>
        $$$"""
        {"type":"thread.started","thread_id":"00000000-0000-0000-0000-000000000000"}
        {"type":"turn.started"}
        {{{before}}}{"type":"error","message":"{{{Refused}}}"}
        {"type":"turn.failed","error":{"message":"{{{Refused}}}"}}
        """;

    const string WorkedFirst =
        """{"type":"item.completed","item":{"id":"item_0","type":"command_execution","command":"ls","aggregated_output":"PLAN.md\n","exit_code":0,"status":"completed"}}""" + "\n";

    static readonly DateTimeOffset At = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    TurnMeter Meter() =>
        new(_db, () => 50m, runtimeId: () => "codex", now: () => At, recordPath: _records,
            owner: () => new OwnerPrice(1m, 4m), model: () => "gpt-5.6-sol", share: _ => 1m);

    AiAttempt OnlyRow() => Assert.Single(new AiAttemptStore(_db).Between(At.AddDays(-1), At.AddDays(1)));

    static AgentSession RefusedSession(string name, string stream) =>
        AgentRuntimeProbe.SessionOverStream(stream, exitCode: 1, manifest: VendorLimitTests.Codex, name: name);

    /// <summary>
    /// (D17, the money half) RED at base: the refused turn exited with no usage and was charged its reservation, and every look bought another. Now the
    /// refusal before any work costs nothing, says so on its row with the app's sentence, and the seat's next launch is refused before admission:
    /// no second row, no second reservation, no matter how many looks come.
    /// </summary>
    [Fact]
    public async Task A_model_the_vendor_refused_before_any_work_is_charged_nothing_and_no_further_launch_is_reserved()
    {
        var name = nameof(A_model_the_vendor_refused_before_any_work_is_charged_nothing_and_no_further_launch_is_reserved);
        var meter = Meter();
        var chat = RefusedSession(name, RefusedStream());
        using var metering = meter.Attach(chat, Ops);
        AgentTurnEnded? ended = null;
        chat.TurnEnded += e => ended = e;

        await chat.SendAsync("reply ok");

        var row = OnlyRow();
        Assert.Equal(AiAttemptState.ENDED, row.State);
        Assert.Equal(0m, row.Cost);
        Assert.Contains(Refused, RefusedOn(row));
        Assert.Equal(row.Context is { } context && context.Contains(ModelRefusal.Ended, StringComparison.Ordinal), true);
        Assert.Equal(0m, meter.Today.Spent + meter.Today.Reserved);

        var next = meter.Begin("a second look", role: Ops);
        Assert.False(next.Admitted);
        Assert.Equal(RefusalKind.HostOrLaunch, next.Kind);
        Assert.Contains(Refused, next.Refusal);
        OnlyRow();

        // THE WORDS THE OWNER READS: what was refused, that nothing moves by itself, and the press that exists.
        Assert.Contains("gpt-6.1-sol (the fallback)", ended!.ModelRefused!.Message);
        Assert.Contains("does not change a position's model by itself", ended.ModelRefused.Message);
        Assert.Equal(ended.ModelRefused.Message, chat.History.Last().Text);
    }

    /// <summary>A GUARD: A REFUSAL AFTER WORK KEEPS ITS RESERVATION. Commands ran, so requests ran, and unknown is never zero. The seat is held all the same.</summary>
    [Fact]
    public async Task A_model_refused_after_work_keeps_its_reservation_as_its_cost()
    {
        var name = nameof(A_model_refused_after_work_keeps_its_reservation_as_its_cost);
        var meter = Meter();
        var chat = RefusedSession(name, RefusedStream(WorkedFirst));
        using var metering = meter.Attach(chat, Ops);

        await chat.SendAsync("reply ok");

        var row = OnlyRow();
        Assert.Equal(row.ReservedCost, row.Cost);
        Assert.True(row.Cost > 0m, "a turn that worked was charged nothing");
        Assert.True(chat.CheckSeat().Refused);
    }

    /// <summary>
    /// (D17, the wakes half) RED at base: the wakes settled failed and the turn backed off 30 s, about 48 failures a day per role. Now every wake is given
    /// back unspent, the owner's words show why, and the position stands aside so the next look launches nothing.
    /// </summary>
    [Fact]
    public async Task A_model_the_vendor_refused_gives_its_wakes_back_and_holds_the_seat()
    {
        var name = nameof(A_model_the_vendor_refused_gives_its_wakes_back_and_holds_the_seat);
        var events = new MissionEventStore(_db);
        var now = DateTimeOffset.UtcNow;
        var chat = RefusedSession(name, RefusedStream());
        var host = new VendorLimitTests.Host(_db, events, chat, Path.Combine(_records, name)) { Clock = () => now };
        var loop = new MissionLoop(host, new MissionOptions { ReviewEvery = TimeSpan.Zero }, now: () => now);

        Assert.True(events.RecordOwnerMessage("how is the book?", now));
        var owner = Assert.Single(events.Due(now), e => e.Kind == MissionEventKind.Owner).Id;
        var look = MissionEventIds.ForRole(MissionEventIds.Review(now), Ops);
        events.Raise(look, MissionEventKind.Review, now, role: Ops);

        await loop.TurnAsync();
        Assert.Single(host.Opened);

        var kept = events.Get(look)!;
        Assert.False(kept.Consumed, $"the wake was spent on a refused model: {kept.Disposition}");
        var words = events.Get(owner)!;
        Assert.False(words.Consumed);
        Assert.Equal(MissionEventDisposition.Blocked, words.Disposition);
        Assert.Contains(Refused, words.DispositionDetail);

        Assert.Contains(Refused, loop.StandingAsideFor(Ops)?.Why);
        await loop.TurnAsync();
        Assert.Single(host.Opened);
    }

    /// <summary>D17, STATED AS A TEST: NO MODEL IS SWITCHED BY ANYTHING BUT THE OWNER'S PRESS. The refused turn's next launch still asks for the same model.</summary>
    [Fact]
    public void A_refused_model_is_never_switched_and_the_fallback_refused_names_no_press()
    {
        var seat = Seat(nameof(A_refused_model_is_never_switched_and_the_fallback_refused_names_no_press));
        var luna = SeatMarks.Set(seat, null, BrokerSeat.Model, SeatTrouble.ModelRefused, Refused, Noon);
        Assert.Contains(BrokerSeat.Model, luna);
        Assert.NotNull(SeatMarks.Refusal(seat, null, BrokerSeat.Model, new object(), Noon));
        Assert.Null(SeatMarks.Refusal(seat, null, BrokerSeat.Fallback, new object(), Noon));

        var sol = SeatMarks.Set(seat, null, BrokerSeat.Fallback, SeatTrouble.ModelRefused, Refused, Noon);
        Assert.DoesNotContain("press", sol, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What the vendor's pattern recognises, against its own fixtures, and what it must not: its usage limit and a plain failure are other things.</summary>
    [Theory]
    [InlineData(Refused, true)]
    [InlineData("""{"detail":"The 'gpt-6-luna' model is not supported when using Codex with a ChatGPT account."}""", true)]
    [InlineData("unexpected status 404 Not Found: Model not found gpt-6-luna, url: https://chatgpt.com/backend-api/codex/responses", true)]
    [InlineData("The model `gpt-6-luna` does not exist or you do not have access to it.", true)]
    [InlineData("You've hit your usage limit. Try again at 10:30 PM.", false)]
    [InlineData("stream disconnected before completion: error sending request", false)]
    [InlineData("Selected model is at capacity. Please try a different model.", false)]
    public void The_shipped_pattern_reads_the_vendors_refusal_and_nothing_else(string message, bool refused)
    {
        var codex = RuntimeCatalog.BuiltIn().Single(r => r.Id == "codex");
        Assert.Equal(refused, codex.RefusesModel(message));
    }

    /// <summary>A POSITION STANDING ASIDE SAYS SO ON ITS ROW, in plain words; a position that is not shows nothing.</summary>
    [Fact]
    public void The_chart_page_shows_a_position_standing_aside_in_plain_words()
    {
        using var db = TestEnv.NewDb();
        OrgSurfaceSeed.Chart(db);
        var words = Labels.SeatStandingAside("Codex refused the model gpt-6-luna.", "14:30");
        Assert.Equal("Not running for now: Codex refused the model gpt-6-luna. TradeAgent looks at it again at 14:30.", words);
        var page = new OrganisationPage(new OrgSurfaceSources
        {
            Org = () => new OrgStore(db), Attempts = () => new AiAttemptStore(db), Currency = () => "USD",
            StandingAside = id => id == OrgSurfaceSeed.Member ? words : null
        });
        page.Update();
        var shown = page.CardOf(OrgSurfaceSeed.Team)!.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();
        Assert.Single(shown, t => t == words);
    }
}

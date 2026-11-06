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

        // A CLEAN PROBE ENDS THE MATTER: every launch of the seat runs, and the next trouble is a first strike.
        var probe = new object();
        var admitted = SeatMarks.Admitted();
        Assert.Null(SeatMarks.Refusal(seat, null, model, probe, later + SeatMarks.FirstHold * 2));
        SeatMarks.Clear(seat, null, model, probe, admitted);
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
        Assert.Contains(ModelRefusal.Ended, row.Context);
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

    // ---- U-turn-bounds-2: what the independent review found -----------------------------------------------------------

    /// <summary>What the meter's owner price makes of <paramref name="usage"/>, read the way the row must be.</summary>
    static decimal Priced(TurnUsage usage) =>
        CostCatalog.Price(usage, "codex", owner: new OwnerPrice(1m, 4m), requestedModel: "gpt-5.6-sol").Cost!.Value;

    const string MeasuredThenHung = """
        {"type":"thread.started","thread_id":"00000000-0000-0000-0000-000000000000"}
        {"type":"turn.completed","usage":{"input_tokens":40000,"cached_input_tokens":0,"output_tokens":2000}}
        {"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"Working."}}
        """;

    /// <summary>A metered session that prints <paramref name="stream"/> and hangs, cancelled at its read once the model's line has been read.</summary>
    async Task<(AgentTurnEnded Ended, AiAttempt Row)> CutAtTheRead(string name, string stream)
    {
        var meter = Meter();
        var chat = AgentRuntimeProbe.SessionOverStream(stream, manifest: VendorLimitTests.Codex, hangSeconds: 300, name: name);
        using var metering = meter.Attach(chat, Ops);
        AgentTurnEnded? ended = null;
        chat.TurnEnded += e => ended = e;
        using var cut = new CancellationTokenSource();
        chat.TurnAdded += t => { if (t.Role == ChatRole.Ai) cut.Cancel(); };

        // THE TOKEN, NOT THE TEARDOWN: killing the tree first would end the read at end-of-file, which returns what it read. Cancelled, the read throws.
        await chat.SendAsync("reply ok", cut.Token).WaitAsync(TimeSpan.FromSeconds(60));
        return (ended!, OnlyRow());
    }

    /// <summary>
    /// RED at base (the review's P1, AgentSession): the read threw before the usage it held was returned, so the turn ended with Usage null and the meter
    /// charged the reservation in place of what the vendor had said. Now the row keeps the tokens, prices them, and says the figures may not be all of it.
    /// </summary>
    [Fact]
    public async Task A_cancelled_read_keeps_and_prices_the_usage_that_arrived()
    {
        var (ended, row) = await CutAtTheRead(nameof(A_cancelled_read_keeps_and_prices_the_usage_that_arrived), MeasuredThenHung);

        Assert.NotNull(ended.Usage);
        Assert.Equal(40000, row.InputTokens);
        Assert.Equal(2000, row.OutputTokens);
        Assert.Equal(Priced(new TurnUsage(40000, 0, 0, 2000, 0, null)), row.Cost);
        Assert.NotEqual(row.ReservedCost, row.Cost);
        Assert.Contains("was not measured", row.Context);
    }

    /// <summary>A GUARD: A TURN THAT MEASURED NOTHING INVENTS NOTHING. No tokens on the row, its reservation as its cost, never a zero, and the row says so.</summary>
    [Fact]
    public async Task A_cut_turn_that_measured_nothing_says_so_and_keeps_its_reservation()
    {
        const string nothing = """
            {"type":"thread.started","thread_id":"00000000-0000-0000-0000-000000000000"}
            {"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"Working."}}
            """;
        var (ended, row) = await CutAtTheRead(nameof(A_cut_turn_that_measured_nothing_says_so_and_keeps_its_reservation), nothing);

        Assert.Null(ended.Usage);
        Assert.Null(row.InputTokens);
        Assert.Equal(row.ReservedCost, row.Cost);
        Assert.True(row.Cost > 0m, "unknown was charged as zero");
        Assert.Contains("nothing was measured", row.Context);
    }

    /// <summary>
    /// A HUNG TURN THAT HAD REPORTED ITS USAGE IS PRICED ON IT, WHOLE LOOP: the row of a puppet ended at its limit carries what it said, either way the read
    /// ended, and the words stay owed. That the figures are the vendor's own is what the row says beside them.
    /// </summary>
    [Fact]
    public async Task A_turn_past_its_limit_keeps_the_usage_it_reported()
    {
        await using var life = await PuppetLife.Begin("turn-limit-usage");
        life.Host.TurnLimitUnderTest = TimeSpan.FromSeconds(3);
        life.Script(Ops, ["usage 4000 0 200", "signal hung", "hang"]);

        Assert.True(life.Host.RecordOwnerMessage("Close the book."));
        await life.RunUntil(() => life.Reached("hung"), "the chair's puppet reported its usage and hung");
        var hung = Assert.Single(life.AttemptsOf(Ops));
        await life.RunUntil(() => new AiAttemptStore(life.Host.Db).Get(hung.Id)!.State != AiAttemptState.LAUNCHED, "the hung turn's row closed");

        var row = new AiAttemptStore(life.Host.Db).Get(hung.Id)!;
        Assert.Equal(4000, row.InputTokens);
        Assert.Equal(200, row.OutputTokens);
        Assert.Equal(Labels.TurnPastItsLimit(TimeSpan.FromSeconds(3)), RefusedOn(row));
        Assert.Contains("was not measured", row.Context);
        Assert.False(Assert.Single(life.Host.Wakes!.OfKind(MissionEventKind.Owner)).Consumed);
    }

    /// <summary>
    /// RED at base (the review's P1, W6): a refusal after one command settled the wakes failed for good, and the review wake never ran. Now they come back,
    /// the owner's words show why, and the spend the turn incurred stays on its row.
    /// </summary>
    [Fact]
    public async Task A_model_refused_after_work_gives_back_the_wakes_it_did_not_finish_and_keeps_its_spend()
    {
        var name = nameof(A_model_refused_after_work_gives_back_the_wakes_it_did_not_finish_and_keeps_its_spend);
        var events = new MissionEventStore(_db);
        var now = DateTimeOffset.UtcNow;
        var chat = RefusedSession(name, RefusedStream(WorkedFirst));
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
    }

    /// <summary>
    /// WHAT FINISHED IS NOT RUN TWICE. The one thing that tells them apart is a record the app wrote against that launch: the decider's judgment (asked of the
    /// host) and the brief the owner's words became (the relay's measurement). The look beside them has no such record and comes back.
    /// </summary>
    [Fact]
    public async Task A_model_refused_after_work_keeps_what_the_records_show_finished()
    {
        var name = nameof(A_model_refused_after_work_keeps_what_the_records_show_finished);
        var events = new MissionEventStore(_db);
        var now = DateTimeOffset.UtcNow;
        var chat = RefusedSession(name, RefusedStream(WorkedFirst));
        var judged = MissionEventIds.ForRole("delivery-wake", Ops);
        Assert.True(events.RecordOwnerMessage("brief Research on the skew", now));
        var owner = Assert.Single(events.Due(now), e => e.Kind == MissionEventKind.Owner).Id;
        var look = MissionEventIds.ForRole(MissionEventIds.Review(now), Ops);
        events.Raise(look, MissionEventKind.Review, now, role: Ops);
        events.Raise(judged, MissionEventKind.Delivery, now, payload: "man-judged", role: Ops);

        string? askedFor = null;
        var host = new VendorLimitTests.Host(_db, events, chat, Path.Combine(_records, name))
        {
            Clock = () => now,
            Finished = (wake, attempt) => { askedFor = attempt; return wake.Id == judged; },
            Relayed = () => events.SettleWithoutTurn(owner, MissionEventDisposition.Delegated, "pub-brief")
        };
        var loop = new MissionLoop(host, new MissionOptions { ReviewEvery = TimeSpan.Zero }, now: () => now);

        await loop.TurnAsync();

        Assert.False(events.Get(look)!.Consumed, "a look nothing speaks for was spent");
        var finished = events.Get(judged)!;
        Assert.True(finished.Consumed);
        Assert.Equal(MissionEventDisposition.Answered, finished.Disposition);
        Assert.Equal(finished.ConsumedBy, askedFor);
        var brief = events.Get(owner)!;
        Assert.True(brief.Consumed, "words the turn already turned into a brief were owed again");
        Assert.Equal(MissionEventDisposition.Delegated, brief.Disposition);
        Assert.Equal("pub-brief", brief.DispositionDetail);
    }

    /// <summary>
    /// A RECORD SPEAKS ONLY FOR ITS OWN LAUNCH AND ITS OWN SUBJECT: a holder's delivery, a decider's judgment, a reviewer's answer. Another launch's act, an
    /// act of the wrong side, or a kind that asks for no act finishes nothing.
    /// </summary>
    [Fact]
    public void The_records_that_finish_a_wake_name_its_launch_and_its_subject()
    {
        var now = DateTimeOffset.UtcNow;
        var org = new OrgStore(_db);
        OrgSurfaceSeed.Chart(_db);
        var m = org.Assign(new MandateIssue(OrgSurfaceSeed.Head, null, OrgSurfaceSeed.Member, MandateKind.Task, "find out", null,
            MandateShape.Report, false, now.AddDays(7), 5m, MandateLimits.MaxRetryLimit), now);
        var holderWake = new MissionEvent { Id = "w1", Kind = MissionEventKind.Mandate, Payload = m.Id, Role = OrgSurfaceSeed.Member };
        var deciderWake = new MissionEvent { Id = "w2", Kind = MissionEventKind.Delivery, Payload = m.Id, Role = OrgSurfaceSeed.Head };

        Assert.False(org.WakeFinished(holderWake, "turn-holder"), "nothing was delivered yet");
        var delivered = org.Deliver(m.Id, OrgSurfaceSeed.Member, "turn-holder", "the answer", [], [], now).Delivery.Id;

        Assert.True(org.WakeFinished(holderWake, "turn-holder"));
        Assert.False(org.WakeFinished(holderWake, "turn-another"), "another launch's delivery spoke for this one");
        Assert.False(org.WakeFinished(deciderWake, "turn-holder"), "a delivery spoke for the judgment of it");
        Assert.False(org.WakeFinished(new MissionEvent { Id = "w3", Kind = MissionEventKind.Review, Payload = m.Id, Role = OrgSurfaceSeed.Member }, "turn-holder"));

        org.Accept(delivered, OrgSurfaceSeed.Head, "turn-decider", now);
        Assert.True(org.WakeFinished(deciderWake, "turn-decider"));
    }

    /// <summary>
    /// A PROGRAM TWO SESSIONS OF ONE SEAT SHARE: it says it started, waits in its own folder for <c>go</c>, then prints that folder's stream. One program is
    /// one seat, and what each session does is in the folder it runs in, so one can be held open across the other's whole turn.
    /// </summary>
    static string SharedProgram(string dir)
    {
        string script;
        if (OperatingSystem.IsWindows())
        {
            script = Path.Combine(dir, "runtime.cmd");
            File.WriteAllText(script, "@echo off\r\ntype nul > started\r\n:wait\r\nif exist go goto done\r\nping -n 2 127.0.0.1 > nul\r\ngoto wait\r\n:done\r\ntype stream.txt\r\n");
            return script;
        }
        script = Path.Combine(dir, "runtime.sh");
        File.WriteAllText(script, "#!/bin/sh\n: > started\nwhile [ ! -f go ]; do sleep 0.1; done\ncat stream.txt\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    /// <summary>One session of the shared seat, in a folder of its own holding <paramref name="stream"/>.</summary>
    static (AgentSession Session, string Folder) Beside(RuntimeManifest seat, string program, string dir, string name, string stream)
    {
        var folder = Path.Combine(dir, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "stream.txt"), stream.Replace("\r\n", "\n") + "\n");
        return (new AgentSession(seat, () => program, () => folder, () => new Dictionary<string, string>(), presence: new AgentPresence(),
            grants: new TradeAgent.Security.AgentGrants()) { Admit = _ => AiAdmission.NoLedger }, folder);
    }

    /// <summary>
    /// RED at base (the review's P2): any clean end cleared the shared mark, so a turn admitted BEFORE another's refusal lifted the hold without its time or a
    /// probe. Turn B starts, turn A meets the refusal and marks the seat, B ends clean: the seat stays held, and a third launch is refused.
    /// </summary>
    [Fact]
    public async Task A_turn_admitted_before_the_refusal_does_not_lift_the_hold()
    {
        var dir = Path.Combine(TestEnv.Home, "turn-bounds", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var program = SharedProgram(dir);
        var seat = VendorLimitTests.Codex(program);
        const string clean = """
            {"type":"thread.started","thread_id":"00000000-0000-0000-0000-000000000000"}
            {"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"All good."}}
            {"type":"turn.completed","usage":{"input_tokens":10,"cached_input_tokens":0,"output_tokens":2}}
            """;
        var (b, slow) = Beside(seat, program, dir, "b", clean);
        var (a, quick) = Beside(seat, program, dir, "a", RefusedStream());
        var (third, _) = Beside(seat, program, dir, "third", clean);

        var running = b.SendMissionAsync("continue");
        for (var i = 0; i < 600 && !File.Exists(Path.Combine(slow, "started")); i++) await Task.Delay(50);
        Assert.True(File.Exists(Path.Combine(slow, "started")), "turn B never started");

        File.WriteAllText(Path.Combine(quick, "go"), "");
        await a.SendMissionAsync("continue");
        Assert.True(third.CheckSeat().Refused, "turn A's refusal did not hold the seat");

        File.WriteAllText(Path.Combine(slow, "go"), "");
        await running;
        Assert.Equal(ChatRole.Ai, b.History.Last().Role);

        Assert.True(third.CheckSeat().Refused, "a turn admitted before the refusal lifted the hold");
    }

    /// <summary>THE RULE ITSELF, BOTH WAYS: an older turn lifts nothing; the mark's own probe does, and so does a launch admitted after the mark that outran its probe.</summary>
    [Fact]
    public void Only_the_marks_probe_or_a_later_admission_lifts_it()
    {
        var seat = Seat(nameof(Only_the_marks_probe_or_a_later_admission_lifts_it));
        const string model = "who-may-clear";
        var older = new object();
        var admittedBefore = SeatMarks.Admitted();
        SeatMarks.Set(seat, null, model, SeatTrouble.ModelRefused, Refused, Noon);

        SeatMarks.Clear(seat, null, model, older, admittedBefore);
        Assert.NotNull(SeatMarks.Refusal(seat, null, model, new object(), Noon));

        // THE PROBE, whose window then passes to a second launch: the first still ran after the mark, so its clean end speaks for the seat.
        var over = Noon + SeatMarks.FirstHold;
        object slowProbe = new(), nextProbe = new();
        var admittedAfter = SeatMarks.Admitted();
        Assert.Null(SeatMarks.Refusal(seat, null, model, slowProbe, over));
        Assert.Null(SeatMarks.Refusal(seat, null, model, nextProbe, over + SeatMarks.ProbeWindow));
        SeatMarks.Clear(seat, null, model, slowProbe, admittedAfter);
        Assert.Null(SeatMarks.Refusal(seat, null, model));
        Assert.Null(SeatMarks.Refusal(seat, null, model, new object(), Noon));
    }

    /// <summary>
    /// RED at base (the review's P1, MissionLoop): only the loop's turns had a clock, so a hung owner's chat with no chief kept its tree, its reservation and a
    /// Busy conversation for ever. Now it ends at the seat's limit like a mission turn: the tree is gone, the row closed in the words and at the reservation,
    /// the conversation free, the owner told plainly, and his words kept.
    /// </summary>
    [Fact]
    public async Task A_hung_owner_chat_ends_at_its_limit_with_its_tree_and_his_words_are_kept()
    {
        const string words = "What is the book worth?";
        await using var life = await PuppetLife.Begin("chat-limit");
        var limit = TimeSpan.FromSeconds(4);
        life.Host.TurnLimitUnderTest = limit;
        life.Script(Ops, ["signal hung", "child kid", "hang"]);
        var chat = life.Host.Conversation!;

        var sent = chat.SendAsync(words);
        await life.RunUntil(() => life.Reached("kid.pid"), "the chat's puppet started its child and hung");
        var hung = Assert.Single(life.AttemptsOf(Ops));
        Assert.Equal(AiAttemptState.LAUNCHED, hung.State);
        Assert.True(chat.Busy);
        var puppet = int.Parse(File.ReadAllLines(Path.Combine(life.Scripts, "launches.log")).Last().Split(' ')[1]);
        var kid = int.Parse(File.ReadAllText(Path.Combine(life.Scripts, "kid.pid")));

        await sent.WaitAsync(PuppetLife.Patience);

        Assert.False(chat.Busy);
        Assert.False(Alive(puppet), "the hung puppet outlived its limit");
        Assert.False(Alive(kid), "the hung puppet's child outlived its limit");

        var said = Labels.ChatTurnPastItsLimit(limit);
        var row = new AiAttemptStore(life.Host.Db).Get(hung.Id)!;
        Assert.Equal(AiAttemptState.ENDED, row.State);
        Assert.Equal(said, RefusedOn(row));
        Assert.Equal(hung.ReservedCost, row.Cost);
        Assert.Contains(chat.History, t => t.Role == ChatRole.System && t.Text == said);
        Assert.DoesNotContain("exit", said, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(life.Host.Wakes!.OfKind(MissionEventKind.Owner), e => !e.Consumed && (e.Payload ?? "").Contains(words, StringComparison.Ordinal));
    }

    /// <summary>A CHAT TURN THAT ENDS CLEAN IN TIME NEVER HEARS OF THE LIMIT: no words, no keeping, and a later turn is not told an old one's ending.</summary>
    [Fact]
    public async Task A_chat_turn_that_finished_is_not_kept_or_told_of_any_limit()
    {
        const string clean = """
            {"type":"thread.started","thread_id":"00000000-0000-0000-0000-000000000000"}
            {"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"All good."}}
            {"type":"turn.completed","usage":{"input_tokens":10,"cached_input_tokens":0,"output_tokens":2}}
            """;
        var chat = AgentRuntimeProbe.SessionOverStream(clean, manifest: VendorLimitTests.Codex, name: nameof(A_chat_turn_that_finished_is_not_kept_or_told_of_any_limit));
        var kept = new List<string>();
        chat.RecordTyped = text => { kept.Add(text); return true; };
        chat.TurnLimit = () => TimeSpan.FromMinutes(5);
        AgentTurnEnded? ended = null;
        chat.TurnEnded += e => ended = e;

        await chat.SendAsync("reply ok");

        Assert.Null(ended!.TimedOut);
        Assert.Null(ended.UsageUnreported);
        Assert.Empty(kept);
    }

    /// <summary>A LIMIT THAT CANNOT BE READ IS THE KIND'S OWN, NEVER NONE: nothing a host answers leaves a turn without a clock.</summary>
    [Fact]
    public void A_turn_limit_that_cannot_be_read_is_the_seats_kinds_own()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), TurnClock.Of(() => TimeSpan.FromMinutes(2), SeatSpec.VendorTurnLimit));
        Assert.Equal(SeatSpec.VendorTurnLimit, TurnClock.Of(null, SeatSpec.VendorTurnLimit));
        Assert.Equal(SeatSpec.VendorTurnLimit, TurnClock.Of(() => TimeSpan.Zero, SeatSpec.VendorTurnLimit));
        Assert.Equal(SeatSpec.HarnessTurnLimit, TurnClock.Of(() => throw new InvalidOperationException("no seat"), SeatSpec.HarnessTurnLimit));
    }

    /// <summary>
    /// RED at base (R30 row 7): an ordinary failed turn's backoff was slept by the whole serial lane, so one failing position stopped every other. Now it is that
    /// position's: the next look serves the others, its wake waits out the backoff, and the position is seen resting in plain words.
    /// </summary>
    [Fact]
    public async Task A_failed_turn_rests_its_position_and_the_lane_serves_the_others()
    {
        var events = new MissionEventStore(_db);
        var now = DateTimeOffset.UtcNow;
        Due(events, Ops, now - TimeSpan.FromMinutes(3));
        Due(events, Research, now - TimeSpan.FromMinutes(2));
        var host = new OrgLanesTests.LaneHost { Events = events };
        host.Conv(Ops).FailWith = 1;
        var options = new MissionOptions { ReviewEvery = TimeSpan.Zero };
        var loop = new MissionLoop(host, options, now: () => now);

        Assert.Equal(TimeSpan.Zero, await loop.TurnAsync());
        Assert.Single(host.Conv(Ops).Sent);
        Assert.Equal(Labels.LastTurnDidNotFinish, loop.StandingAsideFor(Ops)?.Why);

        await loop.TurnAsync();
        Assert.Single(host.Conv(Research).Sent);

        // A WAKE OF THE RESTING POSITION WAITS THE REST OUT, and is what the next look after it finds.
        Due(events, Ops, now);
        Assert.Equal(options.FirstBackoff, await loop.TurnAsync());
        Assert.Single(host.Conv(Ops).Sent);

        now += options.FirstBackoff;
        await loop.TurnAsync();
        Assert.Equal(2, host.Conv(Ops).Sent.Count);
        Assert.Equal(options.FirstBackoff * 2, loop.StandingAsideFor(Ops)!.Value.Until - now);

        // AND A TURN THAT GOES CLEAN ENDS THE MATTER.
        host.Conv(Ops).FailWith = 0;
        now += options.FirstBackoff * 2;
        Due(events, Ops, now);
        await loop.TurnAsync();
        Assert.Null(loop.StandingAsideFor(Ops));
        Assert.Equal(0, loop.Status.ConsecutiveErrors);
    }

    /// <summary>A REST OUTLIVES A QUIET MOMENT: nothing due at the failure, a wake a second later, and the failing position is still not launched before its backoff ends.</summary>
    [Fact]
    public async Task A_failing_position_is_not_launched_faster_than_its_backoff_when_nothing_else_is_due()
    {
        var events = new MissionEventStore(_db);
        var now = DateTimeOffset.UtcNow;
        Due(events, Ops, now - TimeSpan.FromMinutes(3));
        var host = new OrgLanesTests.LaneHost { Events = events };
        host.Conv(Ops).FailWith = 1;
        var options = new MissionOptions { ReviewEvery = TimeSpan.Zero };
        var loop = new MissionLoop(host, options, now: () => now);

        await loop.TurnAsync();
        await loop.TurnAsync();

        now += TimeSpan.FromSeconds(1);
        Due(events, Ops, now);
        await loop.TurnAsync();
        Assert.Single(host.Conv(Ops).Sent);
    }

    /// <summary>A RUN THAT THROWS IS THE SAME FAILED TURN (the serial half of what a lane already did): its position rests, and the next look serves the others.</summary>
    [Fact]
    public async Task A_turn_that_throws_rests_its_position_too()
    {
        var events = new MissionEventStore(_db);
        var now = DateTimeOffset.UtcNow;
        Due(events, Ops, now - TimeSpan.FromMinutes(3));
        Due(events, Research, now - TimeSpan.FromMinutes(2));
        var host = new OrgLanesTests.LaneHost { Events = events };
        host.Unreadable = new InvalidOperationException("the situation could not be read");
        var loop = new MissionLoop(host, new MissionOptions { ReviewEvery = TimeSpan.Zero }, now: () => now);

        Assert.Equal(TimeSpan.Zero, await loop.TurnAsync());
        Assert.Equal(Labels.LastTurnDidNotFinish, loop.StandingAsideFor(Ops)?.Why);

        host.Unreadable = null;
        await loop.TurnAsync();
        Assert.Single(host.Conv(Research).Sent);
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

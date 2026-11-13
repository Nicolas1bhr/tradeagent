using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE ORGANISATION MOVES WITH NO OWNER INPUT, AND THE BACKSTOP THAT SEES TO IT CANNOT SPIN (R30 D21, <c>U-chief-moves</c>): the real loop and the real
/// wake queue on the seam host, on the test's own clock, with a puppet chief that does nothing. No real model, no real seat.
/// </summary>
public partial class CouncilLoopTests
{
    static readonly TimeSpan ChiefEvery = TimeSpan.FromHours(4);

    static readonly DateTimeOffset ChartDay = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    static readonly ForecastClaim ChartForecast =
        new(ForecastVocabulary.UnitAdmissible, null, ForecastVocabulary.Admitted, ChartDay.AddDays(45), 0.4m);

    /// <summary>A home whose chief is appointed, active and on a puppet seat, on a host reading <paramref name="floors"/>.</summary>
    (CouncilHost Host, Database Db, OrgStore Org, string Chief) ChiefHome(Func<DateTimeOffset> clock, RewakeFloors? floors = null)
    {
        var (db, root) = Workspace();
        var org = new OrgStore(db);
        var chief = org.AppointChief(new ChiefSeat("puppet", "scripted-1"), r => r == "puppet", clock()).Chief!;
        org.Activate(chief, _ => SeatClasses.Puppet, 3, clock(), () => false);
        var host = new CouncilHost(db, root, new Concurrency()) { Clock = clock, Floors = floors ?? ShippedFloors, EndsTurns = true }.Add(chief);
        return (host, db, org, chief);
    }

    /// <summary>The chief charters one division and hires its head, who is active: what the chief's first real act leaves.</summary>
    static (string Unit, string Head) Chartered(Database db, OrgStore org, string chief, DateTimeOffset at)
    {
        var funding = new CharterFunding(SeatClasses.Harness, 0.10m, [new(BillingClasses.Plan, 10m)],
            new Dictionary<string, decimal> { [OrgIds.Operations] = 0m, [OrgIds.Research] = 0m });
        var unit = org.Charter(new CharterRequest(chief, "turn-chart", OrgUnitKind.Division, 0.2m, "a division for the funding family",
            ["a division"], [], [ChartForecast], at.AddDays(30)), funding, at).Unit;
        var head = org.Hire(new HireRequest(chief, "turn-chart", unit.Id, true, "its first head", ["appoint"], [], [ChartForecast]), at).Position.Id;
        Chart(db, "UPDATE org_position SET status='active' WHERE id=$id", ("$id", head));
        return (unit.Id, head);
    }

    static MandateIssue Work(string chief, string head, DateTimeOffset at) =>
        new(chief, null, head, MandateKind.Task, "measure the skew", null, MandateShape.Report, false, at.AddDays(3), 5m, 3);

    /// <summary>
    /// THE CLOCK RUNS <paramref name="hours"/> HOURS, A PASS EVERY FIVE MINUTES, AND THIS IS WHEN <paramref name="chief"/> TURNED. A pass that launches may
    /// leave another due, so each tick takes two, as a loop that does not sleep would.
    /// </summary>
    static async Task<List<DateTimeOffset>> Run(MissionLoop loop, CouncilHost host, string chief, Action<DateTimeOffset> setClock, DateTimeOffset start, int hours)
    {
        var at = new List<DateTimeOffset>();
        for (var t = start; t <= start.AddHours(hours); t = t.AddMinutes(5))
        {
            setClock(t);
            var before = host.Opened.Count(o => o.Role == chief);
            await loop.TurnAsync();
            await loop.TurnAsync();
            at.AddRange(Enumerable.Repeat(t, host.Opened.Count(o => o.Role == chief) - before));
        }
        return at;
    }

    /// <summary>
    /// (a) A CHIEF THAT DOES NOTHING IS WOKEN AT ITS FLOOR AND NEVER MORE OFTEN. A puppet takes every turn and does nothing: no charter, no wake request, no
    /// file. Over 72 hours it turns 18 times, four hours apart, so it is never left for a day and never turns more than 6 times in a day.
    ///
    /// <para>RED at base: 3 turns, a day apart ("Expected: 18, Actual: 3"), which is what the one real chief turn would have bought the 72-hour run.</para>
    /// </summary>
    [Fact]
    public async Task A_chief_that_does_nothing_is_woken_at_its_floor_and_never_more_often()
    {
        var start = ChartDay;
        var now = start;
        var (host, db, _, chief) = ChiefHome(() => now);
        using var _ = db;
        var loop = new MissionLoop(host, NoHeartbeat, now: () => now);

        var turns = await Run(loop, host, chief, t => now = t, start, 72);

        Assert.Equal(18, turns.Count);
        Assert.Equal(start.AddHours(4), turns[0]);
        Assert.All(turns.Zip(turns.Skip(1)), p => Assert.True(p.Second - p.First >= ChiefEvery, $"{p.First:u} then {p.Second:u}"));
        Assert.All(turns, t => Assert.True(turns.Count(o => o > t.AddHours(-24) && o <= t) <= 6, $"more than six turns in the day to {t:u}"));
        Assert.All(host.Conversations[chief].Sent, sent => Assert.Contains("no work is moving in the organisation", sent, StringComparison.Ordinal));
    }

    /// <summary>
    /// (b) A TURN THAT FAILS BUYS NO SOONER WAKE. Every turn of the chief exits non-zero: the ceiling is no told kind, so nothing raises it again, the failed turn
    /// resets the floor as any turn does, and the count is the same 18.
    /// </summary>
    [Fact]
    public async Task A_chief_whose_every_turn_fails_cannot_spin_on_the_backstop()
    {
        var start = ChartDay;
        var now = start;
        var (host, db, _, chief) = ChiefHome(() => now);
        using var _ = db;
        host.Conversations[chief].Exit = 1;
        var loop = new MissionLoop(host, NoHeartbeat, now: () => now);

        var turns = await Run(loop, host, chief, t => now = t, start, 72);

        Assert.Equal(18, turns.Count);
        Assert.All(turns.Zip(turns.Skip(1)), p => Assert.True(p.Second - p.First >= ChiefEvery, $"{p.First:u} then {p.Second:u}"));
    }

    /// <summary>
    /// (b2) NOR DOES A TURN THAT HANGS TO ITS LIMIT, the one failure that gives its wakes back: the backstop's stays consumed, so that turn counts, and the next is a floor after it.
    ///
    /// <para>RED with the wake given back: the chief retried on the failure backoff, the Codex review's P1.</para>
    /// </summary>
    [Fact]
    public async Task A_chief_whose_every_turn_hangs_to_its_limit_is_held_to_the_floor()
    {
        var start = ChartDay;
        var now = start;
        var (host, db, _, chief) = ChiefHome(() => now);
        using var _ = db;
        host.Conversations[chief].Exit = -1;
        host.Conversations[chief].TimedOut = "this turn ran past the most one turn may run";
        var loop = new MissionLoop(host, NoHeartbeat, now: () => now);

        var turns = await Run(loop, host, chief, t => now = t, start, 72);

        Assert.Equal(18, turns.Count);
        Assert.All(turns.Zip(turns.Skip(1)), p => Assert.True(p.Second - p.First >= ChiefEvery, $"{p.First:u} then {p.Second:u}"));
    }

    /// <summary>
    /// (b3) A CHIEF THAT HAS NEVER TURNED IS HELD TO THE FLOOR NOW IN FORCE: the board raises it to eight hours after the four-hour wake was raised, and that wake moves out.
    /// </summary>
    [Fact]
    public async Task A_raised_floor_moves_out_the_wake_of_a_chief_that_never_turned()
    {
        var start = ChartDay;
        var now = start;
        var (host, db, _, chief) = ChiefHome(() => now);
        using var _ = db;
        var loop = new MissionLoop(host, NoHeartbeat, now: () => now);
        await loop.TurnAsync();
        Assert.Equal(start.AddHours(4), Assert.Single(Ceilings(host, chief), e => !e.Consumed).DueAt);

        now = start.AddHours(1);
        host.Floors = RewakeFloors.From(new Dictionary<string, int> { [OrgLevel.Department] = 480 }, OrgLevel.All.ToDictionary(l => l, _ => TimeSpan.FromHours(24)));
        await loop.TurnAsync();
        Assert.Equal(now.AddHours(8), Assert.Single(Ceilings(host, chief), e => !e.Consumed).DueAt);

        now = now.AddHours(1);                                                 // and it is no heartbeat: the row stands
        await loop.TurnAsync();
        Assert.Equal(start.AddHours(9), Assert.Single(Ceilings(host, chief), e => !e.Consumed).DueAt);

        // A FLOOR THE CEILING NO LONGER CLEARS ENDS THE BACKSTOP: the wake goes out to the ceiling and says no more of it.
        now = now.AddHours(1);
        host.Floors = RewakeFloors.From(new Dictionary<string, int> { [OrgLevel.Department] = 1440 }, OrgLevel.All.ToDictionary(l => l, _ => TimeSpan.FromHours(24)));
        await loop.TurnAsync();
        var plain = Assert.Single(Ceilings(host, chief), e => !e.Consumed);
        Assert.Equal(now + MissionOptions.DefaultCeiling, plain.DueAt);
        Assert.Null(plain.Payload);
    }

    /// <summary>
    /// (b4) A CEILING THAT WAS ALREADY DUE WHEN THE BACKSTOP COMES ON IS MARKED AS THE BACKSTOP'S, so a turn that hangs on it cannot give it back. The board's ceiling was
    /// four hours, so the wake is due at the floor with no word; the ceiling goes back to a day and the same instant carries the reason.
    /// </summary>
    [Fact]
    public async Task A_ceiling_already_at_the_floor_takes_the_backstops_word()
    {
        var now = ChartDay;
        var (host, db, _, chief) = ChiefHome(() => now);
        using var _ = db;
        var loop = new MissionLoop(host, new MissionOptions { ReviewEvery = TimeSpan.Zero, Ceilings = OrgLevel.All.ToDictionary(l => l, _ => ChiefEvery) }, now: () => now);
        host.Floors = RewakeFloors.From(new Dictionary<string, int> { [OrgLevel.Department] = 240 }, OrgLevel.All.ToDictionary(l => l, _ => ChiefEvery));
        Told(host, MissionEventKind.Note, chief, now);
        await loop.TurnAsync();
        var turned = new AiAttemptStore(db).LastWoken(chief)!.StartedAt;
        var plain = Assert.Single(Ceilings(host, chief), e => !e.Consumed);
        Assert.Equal((turned + ChiefEvery, null), (plain.DueAt, plain.Payload));

        host.Floors = ShippedFloors;
        var longer = new MissionLoop(host, NoHeartbeat, now: () => now);
        await longer.TurnAsync();

        var same = Assert.Single(Ceilings(host, chief), e => !e.Consumed);
        Assert.Equal((plain.Id, MissionEventStore.ChiefFloorWake), (same.Id, same.Payload));
    }

    /// <summary>
    /// (c) A BOARD THAT LOWERS THE FLOOR TO ZERO DOES NOT MAKE IT A SPIN: the interval is the floor in force and never less than the floor as shipped. One that
    /// raises the floor is obeyed. A policy with no ceiling, or a ceiling no longer than the floor, gets no backstop.
    /// </summary>
    [Theory]
    [InlineData(0, 24, 240)]
    [InlineData(60, 24, 240)]
    [InlineData(240, 24, 240)]
    [InlineData(480, 24, 480)]
    [InlineData(240, 4, null)]
    [InlineData(240, 0, null)]
    public void The_backstops_interval_obeys_the_floor_and_never_runs_under_the_shipped_one(int floorMinutes, int ceilingHours, int? expectMinutes)
    {
        var policy = RewakeFloors.From(new Dictionary<string, int> { [OrgLevel.Department] = floorMinutes },
            OrgLevel.All.ToDictionary(l => l, _ => TimeSpan.FromHours(ceilingHours)));

        Assert.Equal(expectMinutes is { } m ? TimeSpan.FromMinutes(m) : null, OrgStore.ChiefFloorEvery(policy));
    }

    /// <summary>The same floor of zero through the loop: 72 hours, 18 turns, four hours apart.</summary>
    [Fact]
    public async Task A_floor_of_zero_cannot_make_the_backstop_spin()
    {
        var start = ChartDay;
        var now = start;
        var noFloor = RewakeFloors.From(new Dictionary<string, int> { [OrgLevel.Department] = 0 },
            OrgLevel.All.ToDictionary(l => l, _ => TimeSpan.FromHours(24)));
        var (host, db, _, chief) = ChiefHome(() => now, noFloor);
        using var _ = db;
        var loop = new MissionLoop(host, NoHeartbeat, now: () => now);

        Assert.Equal(18, (await Run(loop, host, chief, t => now = t, start, 72)).Count);
    }

    /// <summary>
    /// (d) THE ENVELOPE STILL DECIDES. A chief whose share is spent is stepped over: the backstop's wake stays due, no turn runs and none is charged, the loop
    /// looks again inside its bound, and the first turn the day allows is one turn, not the ones it missed.
    /// </summary>
    [Fact]
    public async Task A_chief_with_no_room_takes_no_turn_on_the_backstop_and_owes_only_one_after()
    {
        var start = ChartDay;
        var now = start;
        var (host, db, _, chief) = ChiefHome(() => now);
        using var _ = db;
        var loop = new MissionLoop(host, NoHeartbeat, now: () => now);
        host.Spending[chief] = Reading(chief, 2.50m, 5.00m, 0.5m) with { ResumesAt = start.AddHours(36) };

        Assert.Empty(await Run(loop, host, chief, t => now = t, start, 24));

        Assert.Single(Ceilings(host, chief), e => !e.Consumed);
        now = now.AddHours(12);
        host.Spending.Remove(chief);
        await loop.TurnAsync();
        await loop.TurnAsync();
        await loop.TurnAsync();
        Assert.Single(host.Opened, o => o.Role == chief);
    }

    /// <summary>
    /// (e) IT STOPS ONCE WORK MOVES, AND COMES BACK WHEN IT STOPS. A chartered division with a head and an open mandate: the chief's next wake is its 24-hour ceiling,
    /// with no word of the backstop. The mandate closes: the backstop is back, counted from the same last turn. It never asks for a turn while the holder owes one.
    ///
    /// <para>RED at base: the ceiling read 24 hours throughout, so the assertion on the backstop fails ("Expected: …16:00, Actual: …12:00 next day").</para>
    /// </summary>
    [Fact]
    public async Task The_backstop_stops_once_work_moves_and_returns_when_it_stops()
    {
        var start = ChartDay;
        var now = start;
        var (host, db, org, chief) = ChiefHome(() => now);
        using var _ = db;
        var loop = new MissionLoop(host, NoHeartbeat, now: () => now);
        var pending = () => Assert.Single(Ceilings(host, chief), e => !e.Consumed);

        await loop.TurnAsync();
        Assert.Equal(start.AddHours(4), pending().DueAt);
        Assert.Equal(MissionEventStore.ChiefFloorWake, pending().Payload);

        now = start.AddHours(4);
        await loop.TurnAsync();
        var turned = new AiAttemptStore(db).LastWoken(chief)!.StartedAt;
        Assert.Equal(turned.AddHours(4), pending().DueAt);

        var (_, head) = Chartered(db, org, chief, now);
        await loop.TurnAsync();
        Assert.Equal(turned.AddHours(4), pending().DueAt);                 // chartered and no wake asked: nothing moves yet

        var work = org.Assign(Work(chief, head, now), now);
        await loop.TurnAsync();
        Assert.Equal(turned + MissionOptions.DefaultCeiling, pending().DueAt);
        Assert.Null(pending().Payload);

        org.Cancel(work.Id, chief, null, "it was the wrong question", now);
        await loop.TurnAsync();
        Assert.Equal(turned.AddHours(4), pending().DueAt);
        Assert.Equal(MissionEventStore.ChiefFloorWake, pending().Payload);
    }

    /// <summary>
    /// (e2) WORK OWED BY A HOLDER THE GATE HOLDS IS NOT MOVING (<c>U-chief-floor-held</c>, R31 A1-2). The legacy pair are held beside an active chief by the
    /// no-unconfined-neighbour rule, but they are on the chart, active and in the chief's subtree, so a mandate to <c>research</c> is written. It is never served
    /// while the chief is active, and it must not switch the chief's backstop off until its deadline: the chief's next wake stays its 4-hour floor. The same
    /// mandate to a holder the gate admits does stop the backstop.
    ///
    /// <para>RED at base: the ceiling moved to the 24-hour one ("Expected: 2026-11-06T20:00, Actual: 2026-11-07T16:00") — the loop never asked the gate about the holder.</para>
    /// </summary>
    [Fact]
    public async Task A_mandate_to_a_held_holder_does_not_switch_the_backstop_off()
    {
        var start = ChartDay;
        var now = start;
        var (host, db, org, chief) = ChiefHome(() => now);
        using var _ = db;
        host.Refusals[CouncilRoles.Research] = new TurnRefusal(RefusalKind.HostOrLaunch,
            $"'{CouncilRoles.Research}' runs unconfined, and '{chief}' is active beyond the legacy pair on this host.");
        var loop = new MissionLoop(host, NoHeartbeat, now: () => now);
        var pending = () => Assert.Single(Ceilings(host, chief), e => !e.Consumed);

        await loop.TurnAsync();
        now = start.AddHours(4);
        await loop.TurnAsync();
        var turned = new AiAttemptStore(db).LastWoken(chief)!.StartedAt;

        var held = org.Assign(Work(chief, CouncilRoles.Research, now), now);
        await loop.TurnAsync();
        Assert.Equal(turned.AddHours(4), pending().DueAt);
        Assert.Equal(MissionEventStore.ChiefFloorWake, pending().Payload);
        Assert.DoesNotContain(host.Opened, o => o.Role == CouncilRoles.Research);

        // THE STORE'S OWN ANSWER, both ways: asked nobody, the mandate reads as moving; asked the gate, it does not.
        Assert.Null(org.ChiefFloor(chief, ShippedFloors, now));
        Assert.Equal(ChiefEvery, org.ChiefFloor(chief, ShippedFloors, now, r => r != CouncilRoles.Research));
        Assert.Null(org.ChiefFloor(chief, ShippedFloors, now, _ => true));
        Assert.Equal(MandateEventKind.Issued, org.MandateStatus(held.Id));
    }

    /// <summary>
    /// (f) A CHIEF THAT ASKED FOR ITS WAKE KEEPS IT, UNLESS NOTHING IS CHARTERED. With a unit chartered its pending request is its plan, and the ceiling stands; with
    /// nothing chartered the request does not hold the organisation still.
    /// </summary>
    [Fact]
    public void A_chief_that_asked_for_its_wake_keeps_it_unless_nothing_is_chartered()
    {
        var now = ChartDay;
        var (host, db, org, chief) = ChiefHome(() => now);
        using var _ = db;
        Assert.True(host.Events!.RaiseDue(MissionEventIds.ForRole(MissionEventIds.Self("asked"), chief), MissionEventKind.Self, now, now.AddHours(20), role: chief));

        Assert.Equal(ChiefEvery, org.ChiefFloor(chief, ShippedFloors, now));
        Chartered(db, org, chief, now);
        Assert.Null(org.ChiefFloor(chief, ShippedFloors, now));
    }

    /// <summary>
    /// (g) ONLY THE ACTIVE CHIEF, AND WHAT COUNTS AS MOVING. A head is never moved short; work owed by a dormant holder, or by the chief, or past its deadline moves
    /// nothing, and neither does a delivery waiting on the chief's own judgment.
    /// </summary>
    [Fact]
    public void Only_the_active_chief_has_a_backstop_and_only_work_someone_else_owes_stops_it()
    {
        var now = ChartDay;
        var (_, db, org, chief) = ChiefHome(() => now);
        using var _ = db;
        var (_, head) = Chartered(db, org, chief, now);

        Assert.Null(org.ChiefFloor(head, ShippedFloors, now));
        Assert.Null(org.ChiefFloor(OrgIds.Research, ShippedFloors, now));

        var work = org.Assign(Work(chief, head, now), now);
        Assert.Null(org.ChiefFloor(chief, ShippedFloors, now));
        Assert.Equal(ChiefEvery, org.ChiefFloor(chief, ShippedFloors, work.Deadline));          // its deadline has passed

        Chart(db, "UPDATE org_position SET status='dormant' WHERE id=$id", ("$id", head));
        Assert.Equal(ChiefEvery, org.ChiefFloor(chief, ShippedFloors, now));                    // a dormant holder moves nothing
        Chart(db, "UPDATE org_position SET status='active' WHERE id=$id", ("$id", head));
        Assert.Null(org.ChiefFloor(chief, ShippedFloors, now));

        Chart(db, "INSERT INTO org_mandate_event(mandate_id, at, kind, actor) VALUES($m, $at, $k, $a)",
            ("$m", work.Id), ("$at", TradeAgent.Core.Db.Sql.T(now)), ("$k", MandateEventKind.Delivered), ("$a", head));
        Assert.Equal(ChiefEvery, org.ChiefFloor(chief, ShippedFloors, now));                    // the delivery waits on the chief itself
    }

    /// <summary>(g2) THE CARD SAYS IT: what wakes the chief after the press names the backstop where there is one, and no such clock where there is none.</summary>
    [Fact]
    public void The_cards_sentence_names_the_backstop_when_there_is_one()
    {
        Assert.Contains("every 4 h while no work is moving and it has not asked", OrgStore.ChiefWakeAfter(TimeSpan.FromHours(24), ChiefEvery), StringComparison.Ordinal);
        Assert.DoesNotContain("while no work is moving", OrgStore.ChiefWakeAfter(TimeSpan.FromHours(24)), StringComparison.Ordinal);
    }

    /// <summary>
    /// (h) THE WORST CASE AT REAL PRICES, stated and held. The founding policy's floor is 4 h, so the backstop buys at most 6 turns a day. On gpt-6-luna a turn at the whole
    /// bound costs 0.16, so a day of them is 0.96 of the founding 40: 2.4 %. What admission sets aside for an unmeasured plan turn is 0.054, 0.81 %; the one real
    /// chief turn measured 0.00487, 0.07 %.
    /// </summary>
    [Fact]
    public void The_backstops_worst_day_at_real_prices_is_a_small_part_of_the_day_total()
    {
        var policy = BoardFounding.V1;
        var every = OrgStore.ChiefFloorEvery(RewakeFloors.Of(policy));
        Assert.Equal(ChiefEvery, every);
        var turns = (int)(TimeSpan.FromHours(24) / every!.Value);
        Assert.Equal(6, turns);

        var bound = CostCatalog.Reserve(TurnAllowance.Default, "codex", requestedModel: "gpt-6-luna").Cost!.Value;
        var reserved = TurnMeter.PlanFactor * CostCatalog.Reserve(TurnMeter.TypicalTurn, "codex", requestedModel: "gpt-6-luna").Cost!.Value;
        Assert.Equal(0.16m, bound);
        Assert.Equal(0.054m, reserved);
        Assert.Equal(0.96m, turns * bound);
        Assert.True(turns * bound <= policy.PlanDayTotal * 0.025m, $"{turns * bound} of {policy.PlanDayTotal}");
    }
}

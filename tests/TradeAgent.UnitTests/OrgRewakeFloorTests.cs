using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE RE-WAKE FLOOR (<c>U-rewake-floor</c>; ORCHESTRATION § 2.5: timescale ceilings are "policy data — a maximum interval per
/// level, a floor and never a heartbeat"; § 0.4, turns are capital): the organisation's sweep re-wakes a holder whose launch ended
/// without delivering no sooner than its level's floor, doubling while it keeps not delivering, to eight floors and never past
/// the level's ceiling. The issued wake, a retry, a rejection, the owner's words and the holder's own requested wake keep their
/// time. Every clock here is the test's; no model.
/// </summary>
public partial class OrgAssignmentTests
{
    /// <summary>The shipped floors (executor 15 min, team 60, department 240) under the shipped 24 h ceiling.</summary>
    static RewakeFloors Shipped => RewakeFloors.From(RewakeFloors.DefaultMinutes, OrgLevel.All.ToDictionary(l => l, _ => TimeSpan.FromHours(24)));

    /// <summary>
    /// ONE LAUNCH OF <paramref name="holder"/> AT <paramref name="at"/> taking the due wakes it is offered — the mandate's, and
    /// <paramref name="also"/> — as the loop hands them, ENDED at <paramref name="ended"/> (default: when it started) without
    /// delivering. Answers its id.
    /// </summary>
    static string TurnOn(Database db, string holder, MandateRow m, DateTimeOffset at, DateTimeOffset? ended = null, params string[] also)
    {
        var due = new MissionEventStore(db).DueFor(holder, at).Where(e => e.Kind == MissionEventKind.Mandate && e.Payload == m.Id)
            .Select(e => e.Id).Concat(also).ToList();
        Assert.True(due.Count > 0, $"nothing of {m.Id} was due for {holder} at {at:O}");
        var id = $"turn-{Guid.NewGuid():n}"[..17];
        var admission = new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = id, StartedAt = at, Role = holder, ReservedCost = 0.01m, SeatClass = SeatClasses.Harness,
            Assurance = SeatClasses.Name(SeatAssurance.Brokered)
        }, due);
        Assert.True(admission.Admitted, admission.Refusal);
        Assert.True(new AiAttemptStore(db).End(id, 0, ended ?? at, 10, null, null, 10, null, "model-x", 0.01m, null, null));
        return id;
    }

    /// <summary>The sweep's re-raise of <paramref name="m"/> after <paramref name="launch"/>, keyed as it always was.</summary>
    static MissionEvent? Reraise(Database db, OrgStore org, MandateRow m, string launch) =>
        new MissionEventStore(db).Get(MissionEventIds.Mandate(m.Id, org.MandateEvents(m.Id)[^1].Id, launch));

    /// <summary>
    /// (a) AN UNDELIVERED LAUNCH IS RE-WOKEN NO SOONER THAN ITS LEVEL'S FLOOR, AND THE FLOOR DOUBLES TO ITS CAP. A member's launch
    /// ends undelivered at T: the sweep's re-raise is written at once, keyed as before, and due at T + 15 min — not due a minute
    /// earlier; each further undelivered launch waits twice the last, 30, 60, 120, then 120 again (eight floors). A team head's
    /// waits 60, 120, 240, 480, 480; a department head's floor of 240 stops at its 24 h ceiling, below its eight floors.
    ///
    /// <para>RED at base: every re-raise was due the moment the sweep wrote it.</para>
    /// </summary>
    [Fact]
    public void An_undelivered_launch_is_rewoken_no_sooner_than_its_levels_floor_and_the_floor_doubles_to_its_cap()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var events = new MissionEventStore(db);
        var floors = Shipped;
        var t = DateTimeOffset.UtcNow;

        foreach (var (holder, issue, steps) in new[]
                 {
                     (Member, Issue(), new[] { 15, 30, 60, 120, 120, 120 }),
                     (Head, new MandateIssue(CouncilRoles.Research, null, Head, MandateKind.Task, "rank the team's hypotheses", null,
                         MandateShape.Report, false, Due, 5m, 3), new[] { 60, 120, 240, 480, 480 })
                 })
        {
            var m = org.Assign(issue, t);
            var at = t;
            foreach (var minutes in steps)
            {
                var launch = TurnOn(db, holder, m, at);
                Assert.Equal(1, org.Sweep(at, floors));
                var row = Reraise(db, org, m, launch);
                Assert.NotNull(row);
                Assert.Equal(at, row!.CreatedAt);
                Assert.Equal(at.AddMinutes(minutes), row.DueAt);
                Assert.DoesNotContain(events.DueFor(holder, row.DueAt.AddMinutes(-1)), e => e.Id == row.Id);
                Assert.Equal(0, org.Sweep(row.DueAt.AddMinutes(-1), floors));
                at = row.DueAt;
            }
        }

        Assert.Equal((TimeSpan.FromHours(4), TimeSpan.FromHours(24), TimeSpan.FromHours(24)),
            (floors.After(OrgLevel.Department, 1), floors.After(OrgLevel.Department, 4), floors.After(OrgLevel.Department, 9)));
    }

    /// <summary>
    /// (b) A RESTART RE-RAISES ONCE, DUE FROM THE RESTART, AND A SECOND PASS RAISES NOTHING. A member's launch is open when the app
    /// dies; two hours later the start declares it LOST, its end the restart's instant. The sweep raises ONE re-wake, keyed by that
    /// launch as before, due a floor after the restart — so a crash loop buys no turns — and a second pass, then or at any time
    /// before it is due, raises nothing.
    ///
    /// <para>RED at base: the re-raise was due at the restart.</para>
    /// </summary>
    [Fact]
    public void A_restart_reraises_once_due_from_the_restart_and_a_second_pass_raises_nothing()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var t = DateTimeOffset.UtcNow;
        var m = org.Assign(Issue(), t);
        var wake = Assert.Single(new MissionEventStore(db).DueFor(Member, t)).Id;
        var open = $"turn-{Guid.NewGuid():n}"[..17];
        Assert.True(new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = open, StartedAt = t, Role = Member, ReservedCost = 0.01m, SeatClass = SeatClasses.Harness,
            Assurance = SeatClasses.Name(SeatAssurance.Brokered)
        }, [wake]).Admitted);
        Assert.Equal(0, org.Sweep(t, Shipped));

        var restart = t.AddHours(2);
        Assert.Equal(1, new AiAttemptStore(db).LoseOpen(restart));
        Assert.Equal(1, org.Sweep(restart, Shipped));
        var row = Reraise(db, org, m, open)!;
        Assert.Equal(restart.AddMinutes(15), row.DueAt);
        Assert.Equal(0, org.Sweep(restart, Shipped));
        Assert.Equal(0, org.Sweep(restart.AddMinutes(14), Shipped));
        Assert.Equal(2, new MissionEventStore(db).OfKind(MissionEventKind.Mandate).Count(e => e.Payload == m.Id));
    }

    /// <summary>
    /// (c) GUARD — THE ISSUED WAKE, A RETRY, A REJECTION AND THE OWNER'S WORDS ARE NEVER DELAYED. While the holder's re-raise of one
    /// mandate waits on its floor: a second mandate issued to it is due at once; the owner's words to it stay due; a clean failure
    /// code retries is due at once; and once a delivery is rejected the rejection's wake is due at once. The sweep, run between
    /// each, moves none of them.
    /// </summary>
    [Fact]
    public void The_issued_wake_a_retry_a_rejection_and_the_owners_words_are_never_delayed()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var events = new MissionEventStore(db);
        var t = DateTimeOffset.UtcNow;
        var floors = Shipped;
        var m = org.Assign(Issue(), t);
        TurnOn(db, Member, m, t);
        Assert.Equal(1, org.Sweep(t, floors));

        var owner = MissionEventIds.ForRole(MissionEventIds.Owner(4242), Member);
        Assert.True(events.Raise(owner, MissionEventKind.Owner, t, payload: "{}", role: Member));
        var second = org.Assign(Issue(), t);
        org.Sweep(t, floors);
        var due = events.DueFor(Member, t);
        Assert.Contains(due, e => e.Id == owner);
        Assert.Contains(due, e => e.Kind == MissionEventKind.Mandate && e.Payload == second.Id);
        Assert.DoesNotContain(due, e => e.Payload == m.Id);

        // A CLEAN FAILURE, delivered by a turn its own wake started: code's retry is due at once.
        var at = t.AddMinutes(1);
        var failed = org.Deliver(m.Id, Member, null, TooLong, [], [], at);
        Assert.Equal(MandateEventKind.Retry, failed.Status);
        org.Sweep(at, floors);
        Assert.Contains(events.DueFor(Member, at), e => e.Id == failed.Wake);

        // A DELIVERY THE DECIDER REJECTS: the rejection's wake is due at once.
        var delivered = org.Deliver(m.Id, Member, null, "the skew predicts nothing", [], [], at);
        Assert.Equal(MandateEventKind.Delivered, delivered.Status);
        var rejected = org.Reject(delivered.Delivery.Id, Head, null, null, "use four hours", at);
        org.Sweep(at, floors);
        Assert.Contains(events.DueFor(Member, at), e => e.Id == MissionEventIds.Mandate(m.Id, rejected.Id));
        Assert.False(events.Get(owner)!.Consumed);
    }

    /// <summary>
    /// (d) THE HOLDER'S OWN REQUESTED WAKE BRINGS THE RE-RAISE TO ITS TIME, AND THE TURN IS CHARGED TO THE MANDATE. A member's launch
    /// ends undelivered having asked to be woken in 60 s (its <c>self</c> wake, as the loop writes it): the re-raise is due then,
    /// not at the floor, so the launch that takes both serves the mandate. Written in the other order — the sweep before the self
    /// wake is written — the next pass brings the pending re-raise forward to it, once. The self wake itself is untouched.
    ///
    /// <para>RED at base: the re-raise was due at once, so the request was pre-empted on the next tick.</para>
    /// </summary>
    [Fact]
    public void The_holders_own_requested_wake_brings_the_reraise_to_its_time_and_the_turn_is_charged_to_the_mandate()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var events = new MissionEventStore(db);
        var t = DateTimeOffset.UtcNow;
        var m = org.Assign(Issue(), t);

        var first = TurnOn(db, Member, m, t);
        var self = MissionEventIds.Self(first);
        Assert.True(events.RaiseDue(self, MissionEventKind.Self, t, t.AddSeconds(60), role: Member));
        Assert.Equal(1, org.Sweep(t, Shipped));
        Assert.Equal(t.AddSeconds(60), Reraise(db, org, m, first)!.DueAt);
        Assert.Equal(t.AddSeconds(60), events.Get(self)!.DueAt);

        var woken = TurnOn(db, Member, m, t.AddSeconds(60), also: [self]);
        Assert.Equal(m.Id, new AiAttemptStore(db).Get(woken)!.MandateId);

        // THE OTHER ORDER: the sweep ran before the self wake was written.
        var at = t.AddSeconds(60);
        Assert.Equal(1, org.Sweep(at, Shipped));
        var row = Reraise(db, org, m, woken)!;
        Assert.Equal(at.AddMinutes(30), row.DueAt);
        var asked = MissionEventIds.Self(woken);
        Assert.True(events.RaiseDue(asked, MissionEventKind.Self, at, at.AddSeconds(90), role: Member));
        Assert.Equal(1, org.Sweep(at, Shipped));
        Assert.Equal(at.AddSeconds(90), Reraise(db, org, m, woken)!.DueAt);
        Assert.Equal(0, org.Sweep(at, Shipped));
        Assert.Equal(at.AddSeconds(90), events.Get(asked)!.DueAt);
    }

    /// <summary>
    /// A LAUNCH OF <paramref name="holder"/> AT <paramref name="at"/> WOKEN BY THE OWNER'S WORDS ALONE — so it serves no mandate —
    /// that ended there having asked to be woken again at <paramref name="asked"/> (its <c>self</c> wake, as the loop writes it).
    /// Answers the launch's id.
    /// </summary>
    static string OwnerTurnAsking(Database db, string holder, DateTimeOffset at, DateTimeOffset asked, long message)
    {
        var events = new MissionEventStore(db);
        var owner = MissionEventIds.ForRole(MissionEventIds.Owner(message), holder);
        Assert.True(events.Raise(owner, MissionEventKind.Owner, at, payload: "{}", role: holder));
        var id = $"turn-{Guid.NewGuid():n}"[..17];
        Assert.True(new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = id, StartedAt = at, Role = holder, ReservedCost = 0.01m, SeatClass = SeatClasses.Harness,
            Assurance = SeatClasses.Name(SeatAssurance.Brokered)
        }, [owner]).Admitted);
        Assert.True(new AiAttemptStore(db).End(id, 0, at, 10, null, null, 10, null, "model-x", 0.01m, null, null));
        Assert.Null(new AiAttemptStore(db).Get(id)!.MandateId);
        Assert.True(events.RaiseDue(MissionEventIds.Self(id), MissionEventKind.Self, at, asked, role: holder));
        return id;
    }

    /// <summary>
    /// (d, fix pass 1) THE HOLDER'S EARLIEST PENDING <c>self</c> WAKE BRINGS ITS OWED MANDATE'S RE-RAISE TO ITS TIME, WHICHEVER LAUNCH
    /// ASKED FOR IT. A member's launch of the mandate ends undelivered at T, so its re-raise waits the floor, T + 15 min; the owner's
    /// words wake it at T + 2 min (a launch that serves no mandate), and that turn asks to be woken at T + 3 min. The re-raise is due
    /// then, so the turn the executor asked for takes the mandate's wake with it and is charged to the mandate (BUDGET-001), not
    /// spent outside its allocation while the mandate's own re-wake still waits.
    ///
    /// <para>RED before fix pass 1: only the self wake of the mandate's own last launch counted, and the re-raise stayed at T + 15.</para>
    /// </summary>
    [Fact]
    public void The_holders_earliest_self_wake_from_a_launch_that_served_no_mandate_brings_the_reraise_to_its_time()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var t = DateTimeOffset.UtcNow;
        var m = org.Assign(Issue(), t);
        var first = TurnOn(db, Member, m, t);

        var asked = t.AddMinutes(3);
        OwnerTurnAsking(db, Member, t.AddMinutes(2), asked, 4242);
        Assert.Equal(1, org.Sweep(t.AddMinutes(2), Shipped));
        Assert.Equal(asked, Reraise(db, org, m, first)!.DueAt);
        Assert.Contains(new MissionEventStore(db).DueFor(Member, asked), e => e.Id == Reraise(db, org, m, first)!.Id);
    }

    /// <summary>
    /// (d, fix pass 1) ONE REQUEST BRINGS ONE OWED MANDATE FORWARD, NEVER EVERY MANDATE THE HOLDER HAS. A member holds two owed
    /// mandates, each re-raise waiting its floor (written before the request: the bring-forward path). A launch that served no
    /// mandate asks for a wake: only the OLDEST owed mandate's re-raise comes to it. When the request is left instead by a launch
    /// that served the newer one, still owed, that one comes forward and the older stays at its floor.
    ///
    /// <para>RED before fix pass 1: the owner-woken launch's request moved neither re-raise.</para>
    /// </summary>
    [Fact]
    public void One_requested_wake_brings_forward_one_owed_mandate_the_oldest_or_the_one_its_launch_served()
    {
        foreach (var servedNewer in new[] { false, true })
        {
            using var db = TestEnv.NewDb();
            Chart(db);
            var org = new OrgStore(db);
            var events = new MissionEventStore(db);
            var t = DateTimeOffset.UtcNow;
            var older = org.Assign(Issue(), t);
            var newer = org.Assign(Issue(), t.AddSeconds(1));
            var a = TurnOn(db, Member, older, t);
            var b = TurnOn(db, Member, newer, t.AddSeconds(1));
            Assert.Equal(2, org.Sweep(t.AddSeconds(1), Shipped));
            Assert.Equal((t.AddMinutes(15), t.AddSeconds(1).AddMinutes(15)),
                (Reraise(db, org, older, a)!.DueAt, Reraise(db, org, newer, b)!.DueAt));

            var asked = t.AddMinutes(3);
            if (servedNewer) Assert.True(events.RaiseDue(MissionEventIds.Self(b), MissionEventKind.Self, t, asked, role: Member));
            else OwnerTurnAsking(db, Member, t.AddMinutes(2), asked, 4243);
            Assert.Equal(1, org.Sweep(t.AddMinutes(2), Shipped));
            Assert.Equal(0, org.Sweep(t.AddMinutes(2), Shipped));
            Assert.Equal(servedNewer
                    ? (t.AddMinutes(15), asked)
                    : (asked, t.AddSeconds(1).AddMinutes(15)),
                (Reraise(db, org, older, a)!.DueAt, Reraise(db, org, newer, b)!.DueAt));
        }
    }

    /// <summary>
    /// (e) A DELIVERY WITHDRAWS THE PENDING RE-RAISE BEFORE ITS RETRY. The holder's re-raise waits on its floor; it delivers (from a
    /// turn its own wake started): the delivery's write closes that re-raise — consumed by no launch, withdrawn, "delivered" and the
    /// delivery's id — before code's retry of the clean failure is raised, which stays due. A delivery that passes leaves no
    /// mandate wake to the holder at all.
    ///
    /// <para>RED at base: the floored re-raise survived the delivery and would have woken the holder for delivered work.</para>
    /// </summary>
    [Fact]
    public void A_delivery_withdraws_the_pending_reraise_before_its_retry()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var events = new MissionEventStore(db);
        var t = DateTimeOffset.UtcNow;
        var m = org.Assign(Issue(), t);
        var launch = TurnOn(db, Member, m, t);
        org.Sweep(t, Shipped);
        var pending = Reraise(db, org, m, launch)!;
        Assert.False(pending.Consumed);

        var failed = org.Deliver(m.Id, Member, null, TooLong, [], [], t.AddMinutes(5));
        var closed = events.Get(pending.Id)!;
        Assert.True(closed.Consumed);
        Assert.Equal((MissionEventStore.NoLaunch, MissionEventDisposition.Withdrawn), (closed.ConsumedBy, closed.Disposition));
        Assert.Equal($"delivered {failed.Delivery.Id}", closed.DispositionDetail);
        Assert.False(events.Get(failed.Wake!)!.Consumed);

        var other = org.Assign(Issue(), t);
        var turned = TurnOn(db, Member, other, t);
        org.Sweep(t, Shipped);
        Assert.False(Reraise(db, org, other, turned)!.Consumed);
        Assert.Equal(MandateEventKind.Delivered, org.Deliver(other.Id, Member, null, "the answer", [], [], t.AddMinutes(5)).Status);
        Assert.DoesNotContain(events.OfKind(MissionEventKind.Mandate), e => e.Payload == other.Id && !e.Consumed);
    }

    /// <summary>
    /// (f) FLOORS ZERO GIVE TODAY'S RE-RAISE EXACTLY, AND A NEGATIVE READS ZERO. With every floor zero the re-raise is the row the
    /// sweep always wrote: the same key, due the instant it is written. A floor below zero reads zero; a level the owner's setting
    /// leaves out reads its shipped default; the settings ship the three defaults.
    /// </summary>
    [Fact]
    public void Floors_zero_give_todays_reraise_exactly_and_a_negative_reads_zero()
    {
        var read = RewakeFloors.From(new Dictionary<string, int> { [OrgLevel.Executor] = -5 }, null);
        Assert.Equal((TimeSpan.Zero, TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(240)),
            (read.Floor(OrgLevel.Executor), read.Floor(OrgLevel.Team), read.Floor(OrgLevel.Department)));
        Assert.Equal(TimeSpan.Zero, read.After(OrgLevel.Executor, 3));
        Assert.Equal(new Dictionary<string, int> { [OrgLevel.Executor] = 15, [OrgLevel.Team] = 60, [OrgLevel.Department] = 240 },
            new TradeAgentSettings().RewakeFloorMinutes);

        foreach (var zero in new[] { RewakeFloors.None, RewakeFloors.From(OrgLevel.All.ToDictionary(l => l, _ => -1), null) })
        {
            using var db = TestEnv.NewDb();
            Chart(db);
            var org = new OrgStore(db);
            var t = DateTimeOffset.UtcNow;
            var m = org.Assign(Issue(), t);
            var at = t;
            for (var n = 0; n < 3; n++)
            {
                var launch = TurnOn(db, Member, m, at);
                Assert.Equal(1, org.Sweep(at, zero));
                var row = Reraise(db, org, m, launch)!;
                Assert.Equal((at, at), (row.CreatedAt, row.DueAt));
                Assert.Equal(0, org.Sweep(at, zero));
                at = at.AddSeconds(1);
            }
        }
    }

    /// <summary>
    /// (g) A HOLDER THE CHART CANNOT PLACE TAKES THE LARGEST FLOOR AND SAYS SO. A member whose position went dormant after its mandate
    /// was issued is in no roster: refused a guess, its re-raise waits the largest floor there is (the department's 240 min), and
    /// the sweep says so in words naming the holder — once, with the re-raise.
    ///
    /// <para>RED at base: due at once, and nothing said.</para>
    /// </summary>
    [Fact]
    public void A_holder_the_chart_cannot_place_takes_the_largest_floor_and_says_so()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var t = DateTimeOffset.UtcNow;
        var m = org.Assign(Issue(), t);
        var launch = TurnOn(db, Member, m, t);
        Exec(db, "UPDATE org_position SET status=$s WHERE id=$id", ("$s", OrgPositionStatus.Dormant), ("$id", Member));

        var said = new List<string>();
        Assert.Equal(1, org.Sweep(t, Shipped, said.Add));
        Assert.Equal(t.AddMinutes(240), Reraise(db, org, m, launch)!.DueAt);
        var line = Assert.Single(said);
        Assert.Contains(Member, line, StringComparison.Ordinal);
        Assert.Contains("240", line, StringComparison.Ordinal);
        Assert.Equal(0, org.Sweep(t, Shipped, said.Add));
        Assert.Single(said);
    }
}

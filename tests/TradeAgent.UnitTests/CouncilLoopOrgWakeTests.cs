using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE TURN ALLOCATOR (<c>U-org-wakes</c>, <c>docs/ORCHESTRATION.md</c> § 2.5): wakes are addressed to positions, a position the
/// activation gate refuses for what it is is never woken again, the next position served is the one holding the highest
/// ORGANISATION § 10 class among its due wakes, heads are floored by a ceiling wake and members by none, and the loop's
/// affordability reads the envelopes admission enforces.
///
/// <para>RULING R3: every position here runs on this file's SEAM HOST — <see cref="CouncilHost"/>, whose activation seam answers
/// null for a test position and whose conversations are test doubles. No real model, no real seat.</para>
/// </summary>
public partial class CouncilLoopTests
{
    /// <summary>An id shaped as the app mints one.</summary>
    const string Pos = "pos-0a3c5e7f9b11";

    /// <summary>The words the seam refuses <see cref="Pos"/> with, for what it is.</summary>
    const string PosRefusal = "'pos-0a3c5e7f9b11' is not an active position, so no conversation is opened for it.";

    static MissionEvent? Row(CouncilHost host, string id) => host.Events!.Get(id);

    static int Rows(Database db, string role)
    {
        using var c = db.Cmd("SELECT COUNT(*) FROM mission_event WHERE role=$r", ("$r", role));
        return Convert.ToInt32(c.ExecuteScalar());
    }

    // ---- (a) a position refused for what it is ---------------------------------------------------------------------

    /// <summary>
    /// (a) A REFUSED POSITION'S DUE WAKES SETTLE ONCE AND NEVER RETRY. The seam refuses <see cref="Pos"/> for what it is: every
    /// due wake of it is consumed by no launch, <c>refused</c>, with the seam's sentence, at no cost; a wake due later is left
    /// for its own pass; Research takes the turn; and a second pass writes nothing more — no launch, no wake, no look.
    ///
    /// <para>RED at base (O2a's step-over): both due wakes stayed unconsumed and were re-read on every pass.</para>
    /// </summary>
    [Fact]
    public async Task A_refused_positions_due_wakes_settle_once_and_never_retry()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var host = new CouncilHost(db, root, new Concurrency());
        var now = Noon();
        var loop = new MissionLoop(host, NoHeartbeat, now: () => now);
        host.Refusals[Pos] = new TurnRefusal(RefusalKind.Position, PosRefusal);

        var review = MissionEventIds.ForRole(MissionEventIds.Review(now.AddMinutes(-3)), Pos);
        var self = MissionEventIds.Self("turn-earlier");
        var later = MissionEventIds.ForRole(MissionEventIds.Review(now.AddHours(1)), Pos);
        host.Events!.Raise(review, MissionEventKind.Review, now.AddMinutes(-3), role: Pos);
        host.Events.Raise(self, MissionEventKind.Self, now.AddMinutes(-2), role: Pos);
        host.Events.RaiseDue(later, MissionEventKind.Review, now, now.AddHours(1), role: Pos);
        host.Events.Raise(MissionEventIds.ForRole(MissionEventIds.Review(now.AddMinutes(-1)), CouncilRoles.Research),
            MissionEventKind.Review, now.AddMinutes(-1), role: CouncilRoles.Research);

        await loop.TurnAsync();

        Assert.Equal([CouncilRoles.Research], host.Opened.Select(o => o.Role));
        foreach (var id in new[] { review, self })
        {
            var row = Row(host, id)!;
            Assert.True(row.Consumed, $"{id} was left due");
            Assert.Equal(MissionEventStore.NoLaunch, row.ConsumedBy);
            Assert.Equal(MissionEventDisposition.Refused, row.Disposition);
            Assert.Equal(PosRefusal, row.DispositionDetail);
        }
        Assert.False(Row(host, later)!.Consumed);
        Assert.Equal(0, Launches(db, Pos));

        // ONCE: a second pass settles nothing again, launches nothing for it and raises it nothing.
        var settled = Row(host, review)!.ConsumedAt;
        var rows = Rows(db, Pos);
        await loop.TurnAsync();
        Assert.Equal(settled, Row(host, review)!.ConsumedAt);
        Assert.Equal(rows, Rows(db, Pos));
        Assert.DoesNotContain(host.Opened, o => o.Role == Pos);
        Assert.Equal(0, Launches(db, Pos));
    }

    /// <summary>
    /// (a2) GUARD: A SEAM THAT THROWS KEEPS THE WAKES. A chart that could not be read is no verdict on the position: the pass
    /// steps over it, writes nothing on its wakes, and Research still turns.
    /// </summary>
    [Fact]
    public async Task A_seam_that_throws_keeps_the_wakes()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var host = new CouncilHost(db, root, new Concurrency());
        var now = Noon();
        var loop = new MissionLoop(host, NoHeartbeat, now: () => now);
        host.SeamThrows.Add(Pos);

        var review = MissionEventIds.ForRole(MissionEventIds.Review(now.AddMinutes(-3)), Pos);
        host.Events!.Raise(review, MissionEventKind.Review, now.AddMinutes(-3), role: Pos);
        host.Events.Raise(MissionEventIds.ForRole(MissionEventIds.Review(now.AddMinutes(-1)), CouncilRoles.Research),
            MissionEventKind.Review, now.AddMinutes(-1), role: CouncilRoles.Research);

        await loop.TurnAsync();

        Assert.Equal([CouncilRoles.Research], host.Opened.Select(o => o.Role));
        var row = Row(host, review)!;
        Assert.False(row.Consumed);
        Assert.Null(row.Disposition);
        Assert.Null(row.ConsumedBy);
    }

    /// <summary>
    /// (a3) THE OWNER'S WORDS TO A REFUSED POSITION STAY OWED AND SHOW UNDELIVERED WITH THE SENTENCE (RULING Q1). The position's
    /// other due wake settles; the owner's message is left unconsumed — still owed a turn — and carries the seam's sentence as
    /// its standing reason, so his surface says why it waits.
    ///
    /// <para>RED at base: the message carried no reason at all.</para>
    /// </summary>
    [Fact]
    public async Task Owner_words_to_a_refused_position_stay_owed_and_show_undelivered_with_the_sentence()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var host = new CouncilHost(db, root, new Concurrency());
        var now = Noon();
        var loop = new MissionLoop(host, NoHeartbeat, now: () => now);
        host.Refusals[Pos] = new TurnRefusal(RefusalKind.Position, PosRefusal);

        var words = MissionEventIds.ForRole(MissionEventIds.Owner(900), Pos);
        host.Events!.Raise(words, MissionEventKind.Owner, now.AddMinutes(-4),
            Json.Write(new MissionOwnerMessage("what is the plan?", now.AddMinutes(-4))), Pos);
        var review = MissionEventIds.ForRole(MissionEventIds.Review(now.AddMinutes(-3)), Pos);
        host.Events.Raise(review, MissionEventKind.Review, now.AddMinutes(-3), role: Pos);

        await loop.TurnAsync();

        var owed = Row(host, words)!;
        Assert.False(owed.Consumed);
        Assert.Equal(MissionEventDisposition.Blocked, owed.Disposition);
        Assert.Equal(PosRefusal, owed.DispositionDetail);
        Assert.Equal(MissionEventDisposition.Refused, Row(host, review)!.Disposition);
        Assert.Empty(host.Opened);
    }

    /// <summary>
    /// (a4) A HOST REFUSAL OF THE LEGACY PAIR KEEPS THE OWNER'S WORDS TO OPERATIONS OWED (RULING Q1). The no-unconfined-neighbour
    /// rule refuses the chair's launch for this HOST: every wake is kept — the fill untouched, the owner's message unconsumed —
    /// and the message shows undelivered with the sentence. Nothing is settled refused.
    /// </summary>
    [Fact]
    public async Task A_host_refusal_of_the_legacy_pair_keeps_the_owners_words_to_operations_owed()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var host = new CouncilHost(db, root, new Concurrency());
        var now = Noon();
        var loop = new MissionLoop(host, NoHeartbeat, now: () => now);
        const string neighbour = "an unconfined seat is running beside it, so the chair's order-capable turn waits (§ 2.3)";
        host.Refusals[CouncilRoles.Operations] = new TurnRefusal(RefusalKind.HostOrLaunch, neighbour);

        Assert.True(host.Events!.RecordOwnerMessage("flatten the book", now.AddMinutes(-2)));
        var words = Assert.Single(host.Events.OfKind(MissionEventKind.Owner)).Id;
        var fill = MissionEventIds.Fill("EXEC-9");
        host.Events.Raise(fill, MissionEventKind.Fill, now.AddMinutes(-1), role: CouncilRoles.Operations);

        await loop.TurnAsync();

        Assert.Empty(host.Opened);
        var owed = Row(host, words)!;
        Assert.False(owed.Consumed);
        Assert.Equal(MissionEventDisposition.Blocked, owed.Disposition);
        Assert.Equal(neighbour, owed.DispositionDetail);
        var kept = Row(host, fill)!;
        Assert.False(kept.Consumed);
        Assert.Null(kept.Disposition);
        Assert.DoesNotContain(host.Events.OfKind(MissionEventKind.Fill), e => e.Disposition == MissionEventDisposition.Refused);
    }

    /// <summary>
    /// (a5) THE LAUNCH GATE'S OWN REFUSAL CARRIES ITS KIND (RULING Q1): a launch refused because the name is not an active
    /// position settles the position's due wakes as the seam's refusal does; one refused for the host keeps them.
    /// </summary>
    [Fact]
    public async Task The_launch_gates_position_refusal_settles_and_its_host_refusal_keeps()
    {
        foreach (var (kind, settles) in new[] { (RefusalKind.Position, true), (RefusalKind.HostOrLaunch, false) })
        {
            var (db, root) = Workspace();
            using var _ = db;
            var host = new CouncilHost(db, root, new Concurrency())
            {
                GateRefuses = new AiAdmission { Refusal = "refused at the gate", Kind = kind }
            }.Add(Pos);
            var now = Noon();
            var loop = new MissionLoop(host, NoHeartbeat, now: () => now);
            var review = MissionEventIds.ForRole(MissionEventIds.Review(now.AddMinutes(-3)), Pos);
            host.Events!.Raise(review, MissionEventKind.Review, now.AddMinutes(-3), role: Pos);

            await loop.TurnAsync();

            var row = Row(host, review)!;
            Assert.Equal(settles, row.Consumed);
            Assert.Equal(settles ? MissionEventDisposition.Refused : null, row.Disposition);
        }
    }

    // ---- (b2) an exhausted subtree --------------------------------------------------------------------------------------

    /// <summary>Writes the chart rows a test needs, as only a test may: no verb writes the chart yet (<c>U-org-verbs</c>).</summary>
    static void Chart(Database db, string sql, params (string, object?)[] ps) => db.Write(_ =>
    {
        using var c = db.Cmd(sql, ps);
        return c.ExecuteNonQuery();
    });

    /// <summary>A unit under <paramref name="parent"/>, with its own envelope share or none.</summary>
    static void Unit(Database db, string id, string parent, string? share, string kind = OrgUnitKind.Team, string? head = null) =>
        Chart(db, """
            INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, envelope_share, created_at)
            VALUES($id, $parent, $kind, NULL, 'active', $share, '2026-10-11T12:00:00.0000000Z')
            """, ("$id", id), ("$parent", parent), ("$kind", kind), ("$share", share));

    /// <summary>An active position in <paramref name="unit"/>; with <paramref name="heads"/> it is that unit's head.</summary>
    static void Seat(Database db, string id, string unit, bool heads = false)
    {
        Chart(db, """
            INSERT INTO org_position(id, unit_id, home_dir, status, created_at)
            VALUES($id, $unit, $home, 'active', '2026-10-11T12:00:00.0000000Z')
            """, ("$id", id), ("$unit", unit), ("$home", $"org/{id}"));
        if (heads) Chart(db, "UPDATE org_unit SET head_position_id=$id WHERE id=$unit", ("$id", id), ("$unit", unit));
    }

    /// <summary>
    /// (b2) AN EXHAUSTED SUBTREE DOES NOT WIN OVER A FUNDED SIBLING (<c>U-org-wakes</c> item 2). Two positions in sibling teams
    /// under Research; the first team's envelope (0.2 of a 10.00 day) already holds one reservation of 1.28, so the gate would
    /// refuse its next launch. Its wake is the older, yet the loop's first look — the meter's reading of each position — must
    /// step over it and turn the funded sibling.
    ///
    /// <para>RED at base: the reading's cap was the share's (here the whole day), so the exhausted position looked affordable,
    /// won on age, and the gate then refused it: no turn at all for the sibling the money was there for.</para>
    /// </summary>
    [Fact]
    public async Task An_exhausted_subtree_does_not_win_over_a_funded_sibling()
    {
        var (db, root) = Workspace();
        using var _ = db;
        const string spent = "pos-1a2b3c4d5e6f", funded = "pos-6f5e4d3c2b1a";
        Unit(db, "team-spent", OrgIds.Research, "0.2");
        Unit(db, "team-funded", OrgIds.Research, "0.5");
        Seat(db, spent, "team-spent");
        Seat(db, funded, "team-funded");

        var now = Noon();
        const decimal reservation = 1.28m;
        new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = "turn-earlier", StartedAt = now.AddMinutes(-30), ReservedCost = reservation, Role = spent,
            Funding = ["team-spent", OrgIds.Research, OrgIds.Root]
        });
        var meter = new TurnMeter(db, () => 10m, now: () => now, share: _ => 1m, live: new LiveAttempts(),
            recordPath: Path.Combine(TestEnv.Home, $"turns-{Guid.NewGuid():n}.jsonl"));

        var host = new CouncilHost(db, root, new Concurrency()).Add(spent).Add(funded);
        foreach (var p in new[] { spent, funded })
            host.Spending[p] = meter.TodayFor(p) with { NextTurnReservation = reservation };
        var loop = new MissionLoop(host, NoHeartbeat, now: () => now);

        host.Events!.Raise(MissionEventIds.ForRole(MissionEventIds.Review(now.AddMinutes(-5)), spent),
            MissionEventKind.Review, now.AddMinutes(-5), role: spent);
        host.Events.Raise(MissionEventIds.ForRole(MissionEventIds.Review(now.AddMinutes(-1)), funded),
            MissionEventKind.Review, now.AddMinutes(-1), role: funded);

        await loop.TurnAsync();

        Assert.False(host.Spending[spent].AdmitsAnotherTurn, $"room {host.Spending[spent].RoleRemaining} of {host.Spending[spent].RoleCap}");
        Assert.Equal([funded], host.Opened.Select(o => o.Role));
    }

    // ---- (c) heads, members and the legacy pair on the clock --------------------------------------------------------------

    /// <summary>The ceiling wakes of one position, every one, oldest first.</summary>
    static List<MissionEvent> Ceilings(CouncilHost host, string position) =>
        [.. host.Events!.OfKind(MissionEventKind.Ceiling).Where(e => e.Role == position)];

    /// <summary>A turn of <paramref name="position"/> the ledger recorded at <paramref name="at"/>, taking one wake of its own.</summary>
    static void Turned(Database db, string position, DateTimeOffset at)
    {
        var events = new MissionEventStore(db);
        var wake = MissionEventIds.ForRole(MissionEventIds.Self($"turn-{at.UtcTicks}"), position);
        events.Raise(wake, MissionEventKind.Self, at, role: position);
        new AiAttemptStore(db).Begin(new AiAttempt { Id = $"turn-{at.UtcTicks}", StartedAt = at, Role = position }, [wake]);
    }

    /// <summary>
    /// (c) A RUNNABLE HEAD GETS ONE CEILING WAKE FROM ITS LAST TURN (RULING Q2). A team head on the seam host last turned two
    /// hours ago: one ceiling wake is raised, due at that turn plus the team level's maximum interval (24 h), and a later pass
    /// raises no second one and does not move it. Past the floor the head turns on it, and its new turn moves the floor —
    /// one pending ceiling again, due 24 h after the turn that took it.
    ///
    /// <para>RED at base: Schedule walked the legacy pair only, so the head had no floor at all.</para>
    /// </summary>
    [Fact]
    public async Task A_runnable_head_gets_one_ceiling_wake_from_its_last_turn()
    {
        var (db, root) = Workspace();
        using var _ = db;
        Unit(db, "team-head", OrgIds.Research, null);
        Seat(db, Pos, "team-head", heads: true);
        var now = Noon();
        Turned(db, Pos, now.AddHours(-2));
        var host = new CouncilHost(db, root, new Concurrency()).Add(Pos);
        var loop = new MissionLoop(host, NoHeartbeat, now: () => now);

        await loop.TurnAsync();
        var floor = Assert.Single(Ceilings(host, Pos));
        Assert.Equal(now.AddHours(-2) + MissionOptions.DefaultCeiling, floor.DueAt);
        Assert.False(floor.Consumed);
        Assert.Empty(host.Opened);

        // A LATER PASS IS NOT A TICK: no second row, the same floor.
        now = now.AddHours(1);
        await loop.TurnAsync();
        Assert.Equal(floor.Id, Assert.Single(Ceilings(host, Pos)).Id);

        // PAST THE FLOOR THE HEAD TURNS ON IT, and its turn moves the floor.
        now = floor.DueAt.AddMinutes(1);
        await loop.TurnAsync();
        Assert.Equal([Pos], host.Opened.Select(o => o.Role));
        var taken = Row(host, floor.Id)!;
        Assert.True(taken.Consumed);
        var turned = new AiAttemptStore(db).LastWoken(Pos)!;
        Assert.Equal(turned.Id, taken.ConsumedBy);
        var next = Assert.Single(Ceilings(host, Pos), e => !e.Consumed);
        Assert.Equal(turned.StartedAt + MissionOptions.DefaultCeiling, next.DueAt);
    }

    /// <summary>
    /// (c2) GUARD: A MEMBER GETS NO CLOCK WAKE (RULING Q2). With the heartbeat on and the host saying what each position owns, a
    /// member — even one with the owner's words open — is raised no look, no ceiling and no renewal: an executor wakes for its
    /// own request and the work addressed to it, never for the app's clock.
    /// </summary>
    [Fact]
    public async Task A_member_gets_no_clock_wake()
    {
        var (db, root) = Workspace();
        using var _ = db;
        Unit(db, "team-m", OrgIds.Research, null);
        Seat(db, Pos, "team-m");
        var now = Noon();
        Turned(db, Pos, now.AddDays(-3));
        var host = new CouncilHost(db, root, new Concurrency()) { Owns = true }.Add(Pos);
        host.Spending[Pos] = Reading(Pos, 10m, 10m, 1m);
        var loop = new MissionLoop(host, new MissionOptions { ReviewEvery = TimeSpan.FromMinutes(30) }, now: () => now);

        var before = Rows(db, Pos);
        await loop.TurnAsync();
        now = now.AddDays(2);
        await loop.TurnAsync();

        Assert.Equal(before, Rows(db, Pos));
        Assert.Empty(Ceilings(host, Pos));
        Assert.DoesNotContain(host.Opened, o => o.Role == Pos);
    }

    /// <summary>
    /// (c4) A CEILING MOVED BACK TO A DUE TIME IT ONCE SUPERSEDED KEEPS THE HEAD'S FLOOR (fix pass 1, review point 1). The floor
    /// goes D1, then D2 (say its level's interval changed), then D1 again: exactly one pending ceiling remains, due at D1, and
    /// the first D1 row still reads superseded — its history is not rewritten. A repeat at D1 writes nothing.
    ///
    /// <para>RED before the fix: the third move's raise hit the superseded D1 row's id and wrote nothing, and the supersede
    /// then closed the pending D2 — the head was left with no floor at all.</para>
    /// </summary>
    [Fact]
    public void A_ceiling_moved_back_to_a_superseded_due_time_keeps_the_heads_floor()
    {
        var (db, _) = Workspace();
        using var __ = db;
        var events = new MissionEventStore(db);
        var now = Noon();
        DateTimeOffset d1 = now.AddHours(24), d2 = now.AddHours(12);

        events.MoveCeiling(Pos, now, d1);
        events.MoveCeiling(Pos, now.AddMinutes(1), d2);
        events.MoveCeiling(Pos, now.AddMinutes(2), d1);

        var floor = Assert.Single(events.Pending(MissionEventKind.Ceiling, Pos));
        Assert.Equal(d1, floor.DueAt);
        var first = events.Get(MissionEventIds.Ceiling(Pos, d1))!;
        Assert.True(first.Consumed);
        Assert.Equal(MissionEventDisposition.Superseded, first.Disposition);

        Assert.False(events.MoveCeiling(Pos, now.AddMinutes(3), d1));
        Assert.Equal(floor.Id, Assert.Single(events.Pending(MissionEventKind.Ceiling, Pos)).Id);
    }

    /// <summary>
    /// (c3) GUARD: THE LEGACY PAIR'S LOOKS ARE UNCHANGED by the positions now on the chart: the same rows — kind, role, due —
    /// raised for the chair and Research with a head and a member beside them as without, the heartbeat on and the host saying
    /// what each owns (<c>U-reconcile-wakes</c>' "no open work, no look"; <c>U-reconcile-packet</c>'s reminders).
    /// </summary>
    [Fact]
    public async Task The_legacy_pairs_looks_are_unchanged()
    {
        async Task<List<string>> LegacyRows(bool chart)
        {
            var (db, root) = Workspace();
            using var _ = db;
            if (chart)
            {
                Unit(db, "team-c", OrgIds.Research, null);
                Seat(db, Pos, "team-c", heads: true);
                Seat(db, "pos-0b0b0b0b0b0b", "team-c");
            }
            var now = Noon();
            var host = new CouncilHost(db, root, new Concurrency()) { Owns = true, Book = ["ES +1"] };
            foreach (var role in CouncilRoles.All) host.Spending[role] = Reading(role, 2.50m, 5.00m, 0.5m);
            var loop = new MissionLoop(host, new MissionOptions { ReviewEvery = TimeSpan.FromMinutes(30) }, now: () => now);
            await loop.TurnAsync();
            now = now.AddHours(3);
            await loop.TurnAsync();
            return [.. CouncilRoles.All.SelectMany(r => host.Events!.Pending(MissionEventKind.Review, r)
                    .Concat(host.Events.Pending(MissionEventKind.Renewal, r)))
                .Select(e => $"{e.Kind} {e.For} {e.Id} {e.DueAt:O}")];
        }

        var without = await LegacyRows(chart: false);
        Assert.NotEmpty(without);
        Assert.Equal(without, await LegacyRows(chart: true));
    }

    /// <summary>
    /// (g) GUARD: NO DUE WAKE MEANS NO PAID TURN, ACROSS A RESTART. A head with its floor a day out and a member with nothing:
    /// a loop and then a fresh loop over the same queue — a restart — launch nothing, and the head still holds exactly one
    /// ceiling, not one per start.
    /// </summary>
    [Fact]
    public async Task No_due_wake_means_no_paid_turn()
    {
        var (db, root) = Workspace();
        using var _ = db;
        Unit(db, "team-g", OrgIds.Research, null);
        Seat(db, Pos, "team-g", heads: true);
        Seat(db, "pos-0d0d0d0d0d0d", "team-g");
        var now = Noon();
        var host = new CouncilHost(db, root, new Concurrency()).Add(Pos).Add("pos-0d0d0d0d0d0d");

        for (var start = 0; start < 2; start++)
        {
            var loop = new MissionLoop(host, NoHeartbeat, now: () => now);
            await loop.TurnAsync();
            await loop.TurnAsync();
            now = now.AddMinutes(10);
        }

        Assert.Empty(host.Opened);
        Assert.Equal(0, Launches(db, Pos));
        Assert.Single(Ceilings(host, Pos));
    }

    // ---- (d) priority -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// (d) THE OWNER'S WORDS OUTRANK AN OLDER REVIEW, AND TIES GO BY DEPTH, THEN ID (ORCHESTRATION § 2.5; ORGANISATION § 10). The
    /// chair holds the owner's words (due a minute ago); Research a look due ten minutes ago; three positions a request of their
    /// own each, all due five minutes ago — a department-level head at depth 2 and its twin, and a team head at depth 3 whose id
    /// sorts first. Served in turn: the owner's words; then the three requests ("research" outranks "housekeeping") by depth
    /// and then id; then Research's look, oldest of all.
    ///
    /// <para>RED at base: <c>RolesDue</c> ordered by <c>MIN(due_at)</c> alone, so Research's look went first.</para>
    /// </summary>
    [Fact]
    public async Task Owner_words_outrank_an_older_review_and_ties_go_by_depth_then_id()
    {
        var (db, root) = Workspace();
        using var _ = db;
        const string deep = "pos-0aaaaaaaaaaa", shallow = "pos-0zzzzzzzzzzz", twin = "pos-0yyyyyyyyyyy";
        Unit(db, "sub-d", OrgIds.Research, null, OrgUnitKind.Subdivision);
        Unit(db, "team-d", "sub-d", null);
        Unit(db, "sub-e", OrgIds.Research, null, OrgUnitKind.Subdivision);
        Seat(db, shallow, "sub-d", heads: true);
        Seat(db, twin, "sub-e", heads: true);
        Seat(db, deep, "team-d", heads: true);
        var now = Noon();
        var host = new CouncilHost(db, root, new Concurrency()).Add(deep).Add(shallow).Add(twin);
        var loop = new MissionLoop(host, NoHeartbeat, now: () => now);

        Assert.True(host.Events!.RecordOwnerMessage("where are we?", now.AddMinutes(-1)));
        host.Events.Raise(MissionEventIds.ForRole(MissionEventIds.Review(now.AddMinutes(-10)), CouncilRoles.Research),
            MissionEventKind.Review, now.AddMinutes(-10), role: CouncilRoles.Research);
        foreach (var p in new[] { deep, shallow, twin })
            host.Events.Raise(MissionEventIds.ForRole(MissionEventIds.Self($"asked-{p}"), p), MissionEventKind.Self,
                now.AddMinutes(-5), role: p);

        for (var i = 0; i < 5; i++) await loop.TurnAsync();

        Assert.Equal([CouncilRoles.Operations, twin, shallow, deep, CouncilRoles.Research], host.Opened.Select(o => o.Role));
    }

    /// <summary>
    /// (d2) EVERY WAKE KIND HAS A CLASS (RULING Q3): every <see cref="MissionEventKind"/> constant is in the one table, the table
    /// holds nothing else, and a kind it does not class is refused at raise — by the queue, the coalescing raise and the relay's
    /// commit — in a sentence naming the kind, with nothing written.
    /// </summary>
    [Fact]
    public void Every_wake_kind_has_a_class()
    {
        var kinds = typeof(MissionEventKind)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(kinds);
        foreach (var kind in kinds) Assert.True(WakePriority.ClassOf(kind) is not null, $"'{kind}' has no class");
        Assert.Equal(kinds.Order(StringComparer.Ordinal), WakePriority.Table.Keys.Order(StringComparer.Ordinal));

        using var db = TestEnv.NewDb();
        var events = new MissionEventStore(db);
        var at = DateTimeOffset.UtcNow;
        var raise = Assert.Throws<ArgumentException>(() => events.Raise("hunch:1", "hunch", at, role: CouncilRoles.Research));
        Assert.Contains("'hunch'", raise.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => events.RaiseCoalesced(new MissionEvent
        {
            Id = "hunch:2", Kind = "hunch", CreatedAt = at, DueAt = at, Role = CouncilRoles.Research
        }));
        Assert.Throws<ArgumentException>(() => new PublicationStore(db).Commit(new Publication
        {
            Id = "pub-hunch", Role = CouncilRoles.Research, Kind = "hunch", Recipients = CouncilRoles.Operations,
            CreatedAt = at, Content = "x"
        }, at));
        Assert.Null(events.Get("hunch:1"));
        Assert.Null(events.Get("hunch:2"));
        Assert.Null(new PublicationStore(db).Get("pub-hunch"));
    }
}

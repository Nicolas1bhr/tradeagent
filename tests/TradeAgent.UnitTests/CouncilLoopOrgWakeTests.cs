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
}

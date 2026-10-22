using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE SESSION POLICY (<c>U-org-packets-a</c>; <c>docs/ORCHESTRATION.md</c> § 2.6: "an executor starts fresh per mandate; a head
/// resumes within a rotation bound (turns or tokens)"). Every position here runs on this file's SEAM HOST — <see cref="CouncilHost"/>,
/// whose conversations are test doubles recording each send and each stop in order. No real model, no real seat.
/// </summary>
public partial class CouncilLoopTests
{
    /// <summary>A team head and its member, as the app mints ids.</summary>
    const string TeamHead = "pos-5e5510000001", Executor = "pos-5e5510000002";

    const string SessionTeam = "team-5e551000";

    /// <summary>The chart: <see cref="TeamHead"/> heads <see cref="SessionTeam"/> under Research; <see cref="Executor"/> is its member.</summary>
    static CouncilHost SessionChart(Database db, string root)
    {
        Unit(db, SessionTeam, OrgIds.Research, null);
        Seat(db, TeamHead, SessionTeam, heads: true);
        Seat(db, Executor, SessionTeam);
        return new CouncilHost(db, root, new Concurrency()).Add(TeamHead).Add(Executor);
    }

    /// <summary>A mandate <see cref="TeamHead"/> gives <see cref="Executor"/>: the issuer's words are the objective.</summary>
    static MandateRow Give(Database db, string objective, string? scope = null, int retries = 3) =>
        new OrgStore(db).Assign(new MandateIssue(TeamHead, null, Executor, MandateKind.Task, objective, scope, MandateShape.Report,
            false, DateTimeOffset.UtcNow.AddDays(7), 5m, retries), DateTimeOffset.UtcNow);

    static void SelfWake(CouncilHost host, string position, string tag) =>
        host.Events!.Raise(MissionEventIds.ForRole(MissionEventIds.Self(tag), position), MissionEventKind.Self,
            DateTimeOffset.UtcNow.AddSeconds(-1), role: position);

    /// <summary>Turns until <paramref name="position"/> has sent <paramref name="sends"/> messages, or fails saying where it stood.</summary>
    static async Task TurnsUntil(MissionLoop loop, CouncilHost host, string position, int sends)
    {
        for (var i = 0; i < 12 && host.Conversations[position].Sent.Count < sends; i++) await loop.TurnAsync();
        Assert.True(host.Conversations[position].Sent.Count == sends,
            $"{position} sent {host.Conversations[position].Sent.Count} of {sends}: {string.Join(", ", host.Conversations[position].Timeline)}");
    }

    /// <summary>The mandate each of <paramref name="position"/>'s launches served, in launch order, as the ledger recorded it.</summary>
    static List<string?> Served(Database db, string position)
    {
        using var c = db.Cmd("SELECT mandate_id FROM ai_attempt WHERE role=$r ORDER BY rowid", ("$r", position));
        using var r = c.ExecuteReader();
        var served = new List<string?>();
        while (r.Read()) served.Add(r.IsDBNull(0) ? null : r.GetString(0));
        return served;
    }

    /// <summary>A report one line too long for its size check: a clean failure when it names no deviation, so code retries.</summary>
    static readonly string OverLong = string.Join("\n", Enumerable.Range(1, MandateLimits.ReportLines + 1).Select(i => $"line {i}"));

    // ---- item 1: the session key ---------------------------------------------------------------------------------------

    /// <summary>
    /// (a) AN EXECUTOR'S SECOND MANDATE STARTS A FRESH SESSION. The member holds M1 and M2; its first turn answers M1 and opens a
    /// session; its next turn answers M2 — and its conversation was stopped, for <c>mandate</c>, before M2's send.
    ///
    /// <para>RED at base e697686d's behaviour (the turn bound alone): the timeline was <c>send, send</c>, no stop between.
    /// Mutant, the mandate comparison removed: red the same way.</para>
    /// </summary>
    [Fact]
    public async Task An_executors_second_mandate_starts_a_fresh_session()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var host = SessionChart(db, root);
        var loop = new MissionLoop(host, NoHeartbeat);
        var m1 = Give(db, "find out whether the funding skew predicts the next hour");
        var m2 = Give(db, "count the gaps in last week's tape");

        await TurnsUntil(loop, host, Executor, 1);
        await TurnsUntil(loop, host, Executor, 2);

        var executor = host.Conversations[Executor];
        Assert.Equal(["send", $"stop:{SessionWhy.Mandate}", "send"], executor.Timeline);
        Assert.Equal([m1.Id, m2.Id], Served(db, Executor));
    }

    /// <summary>
    /// (b) A RETRY OR A REJECTION STARTS THE EXECUTOR FRESH, AND ITS OWN SELF-WAKE RESUMES. On one mandate: its first turn opens a
    /// session; its own self-wake resumes it; a clean failure code retries is fresh (<c>retry</c>); a delivery the head rejects is
    /// fresh again (<c>retry</c>); and the head, woken by that delivery, is never rotated per mandate.
    ///
    /// <para>RED at base e697686d's behaviour (the turn bound alone): the timeline was four sends and no stop.</para>
    /// </summary>
    [Fact]
    public async Task A_retry_or_rejection_starts_the_executor_fresh_and_its_own_self_wake_resumes()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var host = SessionChart(db, root);
        var loop = new MissionLoop(host, NoHeartbeat);
        var org = new OrgStore(db);
        var m = Give(db, "find out whether the funding skew predicts the next hour");

        await TurnsUntil(loop, host, Executor, 1);
        SelfWake(host, Executor, "turn-own-1");
        await TurnsUntil(loop, host, Executor, 2);

        // A CLEAN FAILURE: code retries it, a fresh executor attempt (ORCHESTRATION § 2.5).
        var failed = org.Deliver(m.Id, Executor, null, OverLong, [], [], DateTimeOffset.UtcNow);
        Assert.Equal(MandateEventKind.Retry, org.MandateStatus(m.Id));
        await TurnsUntil(loop, host, Executor, 3);

        // A DELIVERY THAT PASSES ITS CHECKS wakes the head, which turns resumed; the head rejects it, and the holder is fresh.
        var delivered = org.Deliver(m.Id, Executor, null, "The skew predicts nothing over the hour.", [], [], DateTimeOffset.UtcNow);
        Assert.Equal(MandateEventKind.Delivered, org.MandateStatus(m.Id));
        await TurnsUntil(loop, host, TeamHead, 1);
        org.Reject(delivered.Delivery.Id, TeamHead, null, null, "the hour is the wrong horizon: use four", DateTimeOffset.UtcNow);
        await TurnsUntil(loop, host, Executor, 4);

        Assert.Equal(["send", "send", $"stop:{SessionWhy.Retry}", "send", $"stop:{SessionWhy.Retry}", "send"],
            host.Conversations[Executor].Timeline);
        Assert.Equal(["send"], host.Conversations[TeamHead].Timeline);
        Assert.Equal(MandateEventKind.Retry, failed.Status);
        Assert.Equal([m.Id, null, m.Id, m.Id], Served(db, Executor));
    }

    /// <summary>
    /// (b2) A CHART THAT CANNOT BE READ IS NEVER GUESSED: an executor's second mandate on a host whose roster throws keeps its
    /// session to the turn bound only — no stop — and the loop says why.
    /// </summary>
    [Fact]
    public async Task An_unreadable_chart_keeps_the_turn_bound_only_and_is_said()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var host = SessionChart(db, root);
        var loop = new MissionLoop(host, NoHeartbeat);
        var said = new List<string>();
        loop.Noted += said.Add;
        Give(db, "find out whether the funding skew predicts the next hour");
        Give(db, "count the gaps in last week's tape");

        await TurnsUntil(loop, host, Executor, 1);
        host.ChartThrows = true;
        await TurnsUntil(loop, host, Executor, 2);

        Assert.Equal(["send", "send"], host.Conversations[Executor].Timeline);
        Assert.Contains(said, s => s.Contains("the organisation chart could not be read", StringComparison.Ordinal)
                                   && s.Contains("kept to the turn bound", StringComparison.Ordinal));
    }
}

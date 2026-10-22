using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// FENCES (<c>U-org-verbs-b</c>, O6 part b; ORCHESTRATION §§ 2.3, 2.9): an authority-changing transition ends every attempt it touches —
/// refused on its next operation, its turn and process tree ended, its outputs held. The fence is a DB fact: the mandate's own
/// <c>cancelled</c> or <c>rescoped</c> event, or an <c>org_event</c> <c>fenced</c> naming the position. In-process calls under launch
/// ids the test seeded in the ledger, as the other parts do. No real model.
/// </summary>
public partial class OrgAssignmentTests
{
    const string CancelWhy = "the question is answered elsewhere";

    /// <summary>A launch row as the ledger would hold it, started at <paramref name="started"/> — as only a test may write it.</summary>
    static string LaunchedAt(Database db, string role, DateTimeOffset started, string? mandate = null)
    {
        var id = $"turn-{Guid.NewGuid():n}"[..17];
        Exec(db, """
            INSERT INTO ai_attempt(id, started_at, reserved_cost, state, role, seat_class, assurance, mandate_id)
            VALUES($id, $at, '0.10', 'LAUNCHED', $role, $class, $assurance, $mandate)
            """, ("$id", id), ("$at", started.UtcDateTime.ToString("O")), ("$role", role), ("$class", SeatClasses.Harness),
            ("$assurance", SeatClasses.Name(SeatClasses.AssuranceOf(SeatClasses.Harness)!.Value)), ("$mandate", mandate));
        return id;
    }

    /// <summary>A position-wide fence, as parts c–d will write it in their transitions' own writes.</summary>
    static void FencePosition(Database db, string position, string unit, DateTimeOffset at, string detail) =>
        Exec(db, "INSERT INTO org_event(at, unit_id, position_id, kind, decider, detail) VALUES($at, $u, $p, 'fenced', 'app', $d)",
            ("$at", at.UtcDateTime.ToString("O")), ("$u", unit), ("$p", position), ("$d", detail));

    // ---- item 1: the fence read ------------------------------------------------------------------------------------

    /// <summary>
    /// (1) AN ATTEMPT IS FENCED BY A LATER TRANSITION AND NEVER BY AN EARLIER ONE: a launch under a mandate its decider then cancels
    /// is fenced, naming the mandate, the cause and the reason; a launch that started after the cancellation is not; a launch under
    /// a re-scoped mandate is fenced as re-scoped; a position-wide <c>fenced</c> event fences the position's launches that started
    /// before it and none that started after; the legacy pair are never read as fenced, whatever a row says. RED at base: no
    /// <c>OrgStore.FencedSince</c>.
    /// </summary>
    [Fact]
    public void An_attempt_is_fenced_by_a_later_cancel_and_not_by_an_earlier_one()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var t0 = DateTimeOffset.UtcNow;

        // THE MANDATE'S OWN CANCELLATION, after the launch started: fenced.
        var m = org.Assign(Issue(), t0);
        var before = LaunchedAt(db, Member, t0.AddSeconds(1), m.Id);
        Assert.Null(org.FencedSince(before));
        org.Cancel(m.Id, Head, null, CancelWhy, t0.AddSeconds(2));
        var fence = org.FencedSince(before);
        Assert.NotNull(fence);
        Assert.Equal((before, Member, MandateEventKind.Cancelled, m.Id, CancelWhy), (fence!.Attempt, fence.Position, fence.Cause, fence.Mandate, fence.Detail));
        Assert.Contains(m.Id, fence.Words, StringComparison.Ordinal);
        Assert.Contains("cancelled", fence.Words, StringComparison.Ordinal);

        // ... and a launch that started AFTER it is not fenced by it.
        var after = LaunchedAt(db, Member, t0.AddSeconds(3), m.Id);
        Assert.Null(org.FencedSince(after));

        // A RE-SCOPE fences the old mandate's launch as re-scoped, and not the successor's.
        var old = org.Assign(Issue(), t0);
        var underOld = LaunchedAt(db, Member, t0.AddSeconds(1), old.Id);
        var (_, successor) = org.Rescope(old.Id, Head, null, RescopeWhy,
            new MandateTerms(MandateKind.Task, NarrowerGoal, null, MandateShape.Report, false, Due, 3m, 2), t0.AddSeconds(2));
        Assert.Equal(MandateEventKind.Rescoped, org.FencedSince(underOld)?.Cause);
        Assert.Null(org.FencedSince(LaunchedAt(db, Member, t0.AddSeconds(1), successor.Id)));

        // A POSITION-WIDE FENCE: the launch that started before it is fenced, the one after it is not.
        var early = LaunchedAt(db, Other, t0.AddSeconds(1));
        var late = LaunchedAt(db, Other, t0.AddSeconds(5));
        FencePosition(db, Other, Team, t0.AddSeconds(4), "the position was retired");
        Assert.Equal((OrgEventKind.Fenced, (string?)null, "the position was retired"),
            (org.FencedSince(early)?.Cause, org.FencedSince(early)?.Mandate, org.FencedSince(early)?.Detail));
        Assert.Null(org.FencedSince(late));

        // THE LEGACY PAIR ARE NEVER READ AS FENCED; nor is a launch no row records.
        foreach (var (legacy, unit) in new[] { (CouncilRoles.Operations, OrgIds.Operations), (CouncilRoles.Research, OrgIds.Research) })
        {
            var turn = LaunchedAt(db, legacy, t0.AddSeconds(1));
            FencePosition(db, legacy, unit, t0.AddSeconds(4), "a row nothing should write");
            Assert.Null(org.FencedSince(turn));
        }
        Assert.Null(org.FencedSince("turn-never-recorded"));
    }
}

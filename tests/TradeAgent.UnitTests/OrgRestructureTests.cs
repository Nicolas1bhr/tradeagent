using System.Globalization;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A RESTRUCTURING IS ONE TRANSACTION (<c>U-org-verbs-d</c>, O6 part d; ORCHESTRATION § 2.9): it disposes every open mandate held or
/// decided inside what ends, fences the positions it touches and moves no past charge. Store-level, over the chart
/// <see cref="OrgStructureTests"/> builds with raw SQL. No model.
/// </summary>
public partial class OrgStructureTests
{
    /// <summary>A member of <see cref="Sub"/>, under <see cref="SubHead"/>.</summary>
    const string SubMember = "pos-d1c0ffee0004";

    static MandateRow Mandate(OrgStore org, string decider, string holder) =>
        org.Assign(new MandateIssue(decider, null, holder, MandateKind.Task, "find out whether the funding skew predicts the next hour",
            null, MandateShape.Report, false, Now.AddDays(7), 5m, 2), Now);

    /// <summary>Every launch a unit's chain funded, with its reservation and cost, as the ledger holds them.</summary>
    static string[] ChargedTo(Database db, string unit) => db.Read(_ =>
    {
        using var c = db.Cmd("""
            SELECT a.id || ' ' || a.reserved_cost || ' ' || COALESCE(a.cost, '-') || ' ' || f.depth
              FROM ai_attempt_funding f JOIN ai_attempt a ON a.id = f.attempt_id WHERE f.unit_id = $u ORDER BY a.id
            """, ("$u", unit));
        using var r = c.ExecuteReader();
        var rows = new List<string>();
        while (r.Read()) rows.Add(r.GetString(0));
        return rows.ToArray();
    });

    static long Untaken(Database db, string mandate) =>
        Count(db, "mission_event", $"kind = 'mandate' AND payload = '{mandate}' AND consumed_at IS NULL");

    /// <summary>
    /// (1) THE TRANSACTION CANCELS, FENCES AND MOVES NO PAST CHARGE: closing <see cref="Sub"/> cancels the mandate HELD inside it
    /// (decided from outside) and the one DECIDED inside it, each with the app's reason naming the act, their holders' untaken wakes
    /// withdrawn; a mandate wholly outside it stays open; the position touched is fenced, so its running launch is; and every launch
    /// the unit's chain funded reads exactly as before — its rows, reservations, costs and depths (BUDGET-001). RED at base: no
    /// <c>OrgStore.Restructure</c>. The watched mutant: skipping the mandates HELD in the closed unit leaves one open.
    /// </summary>
    [Fact]
    public void A_restructure_cancels_fences_and_moves_no_past_charge()
    {
        using var db = Chart();
        Seat(db, SubMember, Sub);
        var org = new OrgStore(db);
        var heldInside = Mandate(org, Head, SubHead);
        var decidedInside = Mandate(org, SubHead, SubMember);
        var outside = Mandate(org, Head, Member);
        var running = Funded(db, SubHead, Sub, Div, OrgIds.Root);
        var spent = Funded(db, SubMember, Sub, Div, OrgIds.Root);
        Assert.True(new AiAttemptStore(db).End(spent, 0, Now, 10, 0, 5, 5, 0, null, 0.04m, null, null));
        var charged = ChargedTo(db, Sub);
        Assert.Equal(2, charged.Length);
        Assert.Equal(1, Untaken(db, heldInside.Id));

        var disposed = org.Restructure(new Restructuring($"closing {Sub}", Head, [Sub], [], [SubHead, SubMember]), Now.AddMinutes(1));

        foreach (var m in new[] { heldInside, decidedInside })
        {
            Assert.Equal(MandateEventKind.Cancelled, org.MandateStatus(m.Id));
            var why = org.MandateEvents(m.Id)[^1];
            Assert.Equal(OrgStore.AppActor, why.Actor);
            Assert.Contains($"closing {Sub}", why.Reason, StringComparison.Ordinal);
            Assert.Equal(0, Untaken(db, m.Id));
        }
        Assert.True(org.IsOpen(outside.Id), "a mandate wholly outside the restructuring was disposed of");
        Assert.Equal(new[] { heldInside.Id, decidedInside.Id }.Order(StringComparer.Ordinal), disposed.Cancelled.Order(StringComparer.Ordinal));
        Assert.Equal(2, disposed.Fences.Count);
        Assert.Equal(OrgEventKind.Fenced, org.FencedSince(running)?.Cause);
        Assert.Equal(charged, ChargedTo(db, Sub));
    }

    // ---- item 2: org-retire -------------------------------------------------------------------------------------------

    static RetireRequest RetireAsk(string position, string caller = Head) =>
        new(caller, null, position, "the member's line of work is answered", ["retire", "keep"], [], [Admissible()]);

    /// <summary>One refusal of a retirement: the target, the caller, and the words it is refused in.</summary>
    public static TheoryData<string> RetireRefusals => new()
    {
        "operations", "research", "a division head", "the chief", "the caller itself", "not the parent's head", "an ended position", "no forecast"
    };

    /// <summary>
    /// (2a) EACH REFUSAL OF A RETIREMENT, IN WORDS, WITH NOTHING WRITTEN: <c>operations</c> (a two-seat board act, R24 § 4.1), the legacy
    /// <c>research</c> (never an agent's: an app trigger or a board act), a division head (the same), the chief (a board act), the
    /// caller itself, a caller that does not head the position's parent unit, a position already ended, and no forecast. RED at base: no
    /// <c>OrgStore.Retire</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(RetireRefusals))]
    public void Each_refusal_of_a_retirement_is_in_words_and_writes_nothing(string refusal)
    {
        using var db = Chart();
        var org = new OrgStore(db);
        var (ask, words) = refusal switch
        {
            "operations" => (RetireAsk(CouncilRoles.Operations), "a two-seat board act"),
            "research" => (RetireAsk(CouncilRoles.Research), "app trigger or a board act"),
            "a division head" => (RetireAsk(Head, caller: SubHead), "app trigger or a board act"),
            "the chief" => (RetireAsk(Chief), "a two-seat board act"),
            "the caller itself" => (RetireAsk(Head), "cannot retire itself"),
            "not the parent's head" => (RetireAsk(Member, caller: SubHead), "only the parent unit's head"),
            "an ended position" => (RetireAsk(SubHead), "has ended already"),
            "no forecast" => (RetireAsk(SubHead) with { Forecasts = [] }, "1 to"),
            _ => throw new InvalidOperationException(refusal)
        };
        if (refusal == "the chief")
        {
            Seat(db, Chief, OrgIds.Root);
            Exec(db, "UPDATE org_unit SET head_position_id=$c WHERE id='org'", ("$c", Chief));
        }
        if (refusal == "an ended position") Exec(db, "UPDATE org_position SET status='ended' WHERE id=$p", ("$p", SubHead));

        var before = Written(db);
        var refused = Assert.Throws<TradeAgentException>(() => org.Retire(ask, Now));
        Assert.Contains(words, refused.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was written", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, Written(db));
    }

    /// <summary>
    /// (2b) A RETIREMENT ENDS THE POSITION, LEAVES ITS UNIT VACANT AND DISPOSES OF ITS WORK, IN ONE WRITE: the division's head retires
    /// the head of the subdivision under it — the position <c>ended</c> with its time, the subdivision's head vacant, the <c>retire</c>
    /// decision with its rationale in the ledger, the mandate it held cancelled with the app's reason naming the retirement, the
    /// position fenced, and one <c>retired</c> event decided by the head. Not a structural change the weekly throttle counts.
    /// </summary>
    [Fact]
    public void A_retirement_ends_the_position_vacates_its_head_and_disposes_of_its_work()
    {
        using var db = Chart();
        var org = new OrgStore(db);
        var held = Mandate(org, Head, SubHead);
        var running = Funded(db, SubHead, Sub, Div, OrgIds.Root);

        var retired = org.Retire(RetireAsk(SubHead), Now);

        Assert.Equal(OrgPositionStatus.Ended, retired.Position.Status);
        Assert.Equal(Now, retired.Position.EndedAt);
        Assert.Null(org.Units().Single(u => u.Id == Sub).HeadPositionId);
        Assert.Equal((Head, OrgDecisionKind.Retire), (retired.Decision.Position, retired.Decision.Kind));
        Assert.Equal([held.Id], retired.Cancelled);
        Assert.Equal(MandateEventKind.Cancelled, org.MandateStatus(held.Id));
        Assert.Contains($"{SubHead} was retired by {Head}", org.MandateEvents(held.Id)[^1].Reason, StringComparison.Ordinal);
        Assert.NotNull(org.FencedSince(running));
        Assert.Equal(1, Count(db, "org_event", $"kind = 'retired' AND position_id = '{SubHead}' AND decider = '{Head}' AND id = {retired.EventId}"));
        Assert.Equal(0, Count(db, "org_event", "kind IN ('chartered', 'closed', 'merged')"));
    }
}

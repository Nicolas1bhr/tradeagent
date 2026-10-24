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
}

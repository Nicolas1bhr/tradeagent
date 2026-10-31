using System.Globalization;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

public partial class OrgStructureTests
{
    const string Twin = "team-tw1nc0ffee", Closing = "team-c105c0ffee";

    /// <summary>
    /// A CHART WITH ONE OF EACH STRUCTURAL EVENT the veto reads, made by the real verbs: a merge of a twin team into <see cref="Old"/>
    /// (aged past the weekly throttle's week), a charter under the division, a hire into <see cref="Old"/>, the subdivision head's
    /// retirement and the close of a team — beside the rung's own seeded events, which the app decided.
    /// </summary>
    static (OrgStore Org, long Merged, long Chartered, long Hired, long Retired, long Closed) EveryStructuralKind(Database db)
    {
        var org = new OrgStore(db);
        Aged(db, Old, Div, OrgUnitKind.Team, 20, OldHead);
        Aged(db, Twin, Div, OrgUnitKind.Team, 20);
        var merged = org.Merge(MergeAsk(Twin, Old), Now);
        Exec(db, "UPDATE org_event SET at = $at WHERE id = $id",
            ("$at", Now.AddDays(-8).UtcDateTime.ToString("O", CultureInfo.InvariantCulture)), ("$id", merged.EventId));
        var chartered = org.Charter(Ask(refs: [CurrentRun(db)]), Metered, Now);
        var hired = org.Hire(new HireRequest(Head, "turn-test", Old, false, "one more pair of hands", ["hire"], [CurrentRun(db)], [Admissible()]), Now);
        var retired = org.Retire(RetireAsk(SubHead), Now);
        Aged(db, Closing, Div, OrgUnitKind.Team, 20);
        var closed = org.Close(CloseAsk(Closing), Now);
        return (org, merged.EventId, chartered.EventId, hired.EventId, retired.EventId, closed.EventId);
    }

    static long[] EventIds(Database db) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT id FROM org_event ORDER BY id");
        using var r = c.ExecuteReader();
        var ids = new List<long>();
        while (r.Read()) ids.Add(r.GetInt64(0));
        return ids.ToArray();
    });

    /// <summary>
    /// (U-org-surface-b 1b) THE VETOABLE LIST IS EXACTLY WHAT THE VETO ACCEPTS: over a chart holding a charter, a close, a retirement, a
    /// hire and a merge decided by a position and the app's seeded events, the list holds the four the veto undoes — newest first, each
    /// with what it would do in words — and never the merge or an app's event. Then, until the list is empty: every event on the
    /// record NOT listed is refused by the veto with nothing written, and the first listed one vetoes. So each refusal kind — the app's
    /// or the owner's own act, one vetoed already, a merge, a state the event no longer describes — is absent from the list, and each
    /// listed one vetoes. RED at base: no <c>OrgStore.Vetoable</c>.
    /// </summary>
    [Fact]
    public void Vetoable_lists_exactly_what_Veto_accepts()
    {
        using var db = Chart();
        var (org, merged, chartered, hired, retired, closed) = EveryStructuralKind(db);

        var first = org.Vetoable();
        Assert.Empty(first.Refused);
        Assert.Equal(new[] { closed, retired, hired, chartered }, first.Rows.Select(r => r.EventId));
        Assert.Equal(new[] { OrgEventKind.Closed, OrgEventKind.Retired, OrgEventKind.Hired, OrgEventKind.Chartered }, first.Rows.Select(r => r.Kind));
        Assert.DoesNotContain(first.Rows, r => r.EventId == merged);
        Assert.All(first.Rows, r => Assert.Equal(Head, r.Decider));
        Assert.Contains("reopens", first.Rows[0].Words, StringComparison.Ordinal);
        Assert.Contains($"restores {SubHead} dormant", first.Rows[1].Words, StringComparison.Ordinal);
        Assert.Contains("ends", first.Rows[2].Words, StringComparison.Ordinal);
        Assert.Contains("closes", first.Rows[3].Words, StringComparison.Ordinal);

        var vetoes = 0;
        while (true)
        {
            var listed = org.Vetoable().Rows.Select(r => r.EventId).ToHashSet();
            foreach (var id in EventIds(db).Where(id => !listed.Contains(id)))
            {
                var before = Written(db);
                Assert.Throws<TradeAgentException>(() => org.Veto(id, Now));
                Assert.Equal(before, Written(db));
            }
            if (listed.Count == 0) break;
            org.Veto(listed.Max(), Now);
            vetoes++;
        }
        Assert.Equal(4, vetoes);
    }
}

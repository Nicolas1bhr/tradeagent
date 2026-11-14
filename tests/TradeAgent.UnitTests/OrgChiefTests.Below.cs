using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Unit;

public partial class OrgChiefTests
{
    /// <summary>
    /// (e, <c>U-head-staffs</c>) ORG-SHOW SHOWS THE UNITS DIRECTLY UNDER YOURS (D13-hire § 4.1): the 72-hour run's chief chartered a division whose
    /// vacant head no read of its own ever showed, because <c>org-show</c> was its unit and the line above. Its <c>below</c> lists each child unit not
    /// closed — id, kind, head (a position id, or "vacant"), status, shares, positions not ended and a bounded experiment's collapse date — and no
    /// child's forecasts or probabilities; after the appointment the head reads as the new position. A <c>below</c> that does not fit the answer's
    /// bound is cut and counted, never silent.
    ///
    /// <para>RED at base: "The given key was not present in the dictionary" — no <c>below</c>. Mutant watched red: <c>below</c> left empty (the chain only).</para>
    /// </summary>
    [Fact]
    public async Task Org_show_lists_the_units_under_yours_with_a_vacant_head_said_so()
    {
        var (gw, _, db) = await TestEnv.Ready();
        await using var _gw = gw;
        using var _db = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure());
        var org = new OrgStore(db);
        var chief = ActiveChief(db);
        var funding = new CharterFunding(SeatClasses.Harness, 0.10m, [new(BillingClasses.Plan, 10m)],
            new Dictionary<string, decimal> { [OrgIds.Operations] = 0m, [OrgIds.Research] = 0m });
        var collapse = Now.AddDays(30);
        var unit = org.Charter(new CharterRequest(chief, "turn-test", OrgUnitKind.Division, 0.2m, "a division for the funding family",
            ["a division"], [], [Admissible()], collapse), funding, Now).Unit.Id;

        var launch = Funded(db, chief, SeatClasses.Harness, OrgIds.Root);
        JsonElement Show()
        {
            var show = server.CallAsync(Frame(Ops.OrgShow), chief, launch).GetAwaiter().GetResult();
            Assert.True(show.Ok, Json.Write(show.Error));
            return Body(show);
        }

        var view = Show();
        var below = view.GetProperty("below").EnumerateArray().ToList();
        Assert.Equal([OrgIds.Operations, OrgIds.Research, unit], below.Select(u => u.GetProperty("id").GetString()));
        var mine = below[2];
        Assert.Equal((OrgUnitKind.Division, "vacant", OrgUnitStatus.Active),
            (mine.GetProperty("kind").GetString(), mine.GetProperty("head").GetString(), mine.GetProperty("status").GetString()));
        Assert.Equal("0.2", mine.GetProperty("shares").GetProperty("ai").GetString());
        Assert.Equal(0, mine.GetProperty("positions").GetInt32());
        Assert.Equal(collapse.UtcDateTime, mine.GetProperty("collapse_by").GetDateTimeOffset().UtcDateTime, TimeSpan.FromSeconds(1));
        Assert.Equal(CouncilRoles.Research, below[1].GetProperty("head").GetString());
        Assert.Equal(JsonValueKind.Null, below[1].GetProperty("collapse_by").ValueKind);
        Assert.Equal(3, view.GetProperty("below_total").GetInt32());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("below_cut").ValueKind);
        var text = view.GetRawText();
        Assert.DoesNotContain("probability", text, StringComparison.Ordinal);
        Assert.DoesNotContain(ForecastVocabulary.UnitAdmissible, text, StringComparison.Ordinal);

        var head = org.Hire(new HireRequest(chief, "turn-test", unit, true, "its first head", ["appoint"], [], [Admissible()]), Now).Position.Id;
        mine = Show().GetProperty("below").EnumerateArray().Single(u => u.GetProperty("id").GetString() == unit);
        Assert.Equal((head, 1), (mine.GetProperty("head").GetString(), mine.GetProperty("positions").GetInt32()));

        // A CLOSED CHILD IS NOT LISTED; MORE CHILDREN THAN THE ANSWER HOLDS ARE CUT AND COUNTED.
        Exec(db, "UPDATE org_unit SET status='closed', closed_at=$at WHERE id=$u", ("$u", unit), ("$at", Stamp));
        Assert.DoesNotContain(Show().GetProperty("below").EnumerateArray(), u => u.GetProperty("id").GetString() == unit);
        const int many = 600;
        foreach (var n in Enumerable.Range(0, many))
            Exec(db, "INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, envelope_share, created_at) VALUES($u,'org','division',NULL,'active','0.0001',$at)",
                ("$u", $"unit-{n:x4}c0ffee00"), ("$at", Stamp));
        var cut = Show();
        var served = cut.GetProperty("below").GetArrayLength();
        Assert.Equal(many + 2, cut.GetProperty("below_total").GetInt32());
        Assert.InRange(served, 1, many + 1);
        Assert.Contains($"{served} of the {many + 2} units", cut.GetProperty("below_cut").GetString(), StringComparison.Ordinal);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(Json.Write(cut)) <= ResearchLedger.MaxReadBytes + 1024, "the answer outgrew its bound");
    }
}

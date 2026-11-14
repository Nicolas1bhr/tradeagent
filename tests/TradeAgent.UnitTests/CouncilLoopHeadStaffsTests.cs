using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A HEAD IS TOLD, EVERY TURN, THAT A UNIT UNDER IT STANDS EMPTY (<c>U-head-staffs</c>; D13-hire § 4.1). The 72-hour run's chief chartered a
/// division at its first turn and never hired into it: after the charter's own answer no surface it had named the vacant head. Each turn's
/// "what is wanted" now carries a unit under the position's own whose head is vacant (<c>unit-unstaffed</c>) and, one arrow later, a head under
/// it holding no mandate from it (<c>head-unmandated</c>) — the app's rows, rendered in the app's words, granting nothing and moving no clock.
/// </summary>
public partial class CouncilLoopTests
{
    /// <summary>The chief charters one division and hires nobody into it: what the run's first chief turn left.</summary>
    static string CharterOnly(OrgStore org, string chief, DateTimeOffset at, string kind = OrgUnitKind.Division)
    {
        var funding = new CharterFunding(SeatClasses.Harness, 0.10m, [new(BillingClasses.Plan, 10m)],
            new Dictionary<string, decimal> { [OrgIds.Operations] = 0m, [OrgIds.Research] = 0m });
        return org.Charter(new CharterRequest(chief, "turn-chart", kind, 0.2m, "a division for the funding family",
            ["a division"], [], [ChartForecast], at.AddDays(30)), funding, at).Unit.Id;
    }

    static List<RoleObjective> OfKind(Database db, string position, string kind) =>
        [.. RoleObjectives.Open(db, position, null).Where(o => o.Kind == kind)];

    /// <summary>
    /// (a) A UNIT UNDER YOURS THAT STANDS EMPTY IS YOUR OPEN WORK, until you appoint its head. A chief that chartered a division holds exactly one
    /// <c>unit-unstaffed</c> naming it; after the hire with its head flag it holds none. The root, the legacy divisions (headed), a closed unit, a
    /// headless team that has a member, and an empty unit under ANOTHER head open none for the chief — the last is that head's.
    ///
    /// <para>RED at base: "Assert.Single() Failure: The collection was empty" — no kind for a vacant head (RoleObjectives v4). Mutants, each watched red:
    /// the kind left out of a head's kinds; the parent filter dropped (the chief then owns the team under the other head too); closed units not
    /// excluded.</para>
    /// </summary>
    [Fact]
    public void A_unit_under_yours_with_no_head_is_your_open_work_until_you_appoint_one()
    {
        var now = ChartDay;
        var (_, db, org, chief) = ChiefHome(() => now);
        using var _ = db;
        Assert.Empty(OfKind(db, chief, "unit-unstaffed"));                     // the root and the legacy divisions open none

        var unit = CharterOnly(org, chief, now);
        var vacant = Assert.Single(OfKind(db, chief, "unit-unstaffed"));
        Assert.Equal(unit, vacant.Entity);

        // A VACANT UNIT UNDER ANOTHER HEAD IS THAT HEAD'S: a second division, headed, with a team under it whose head is vacant.
        var (other, otherHead) = Chartered(db, org, chief, now);
        const string team = "unit-7ea4c0ffee01";
        Chart(db, "INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES($u,$p,'team',NULL,'active',$at)",
            ("$u", team), ("$p", other), ("$at", now.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal([unit], OfKind(db, chief, "unit-unstaffed").Select(o => o.Entity));
        Assert.Equal([team], OfKind(db, otherHead, "unit-unstaffed").Select(o => o.Entity));

        // A UNIT WITH NO HEAD BUT A MEMBER IS STAFFED: a team's members are hired, and led, by the head above it.
        const string led = "unit-7ea4c0ffee02", member = "pos-7ea4c0ffee03";
        Chart(db, "INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES($u,$p,'team',NULL,'active',$at)",
            ("$u", led), ("$p", other), ("$at", now.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
        Chart(db, "INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES($id,$u,$h,'dormant',$at)",
            ("$id", member), ("$u", led), ("$h", $"org/{member}"), ("$at", now.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal([team], OfKind(db, otherHead, "unit-unstaffed").Select(o => o.Entity));

        // A CLOSED UNIT OPENS NONE.
        Chart(db, "UPDATE org_unit SET status='closed', closed_at=$at WHERE id=$u", ("$u", team), ("$at", now.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Empty(OfKind(db, otherHead, "unit-unstaffed"));

        // THE HIRE WITH ITS HEAD FLAG CLOSES IT.
        org.Hire(new HireRequest(chief, "turn-hire", unit, true, "its first head", ["appoint"], [], [ChartForecast]), now);
        Assert.Empty(OfKind(db, chief, "unit-unstaffed"));
        Assert.Equal(5, RoleObjectives.Version);
    }

    /// <summary>
    /// (d) THE SAME GAP ONE ARROW LATER: AN ACTIVE HEAD UNDER YOURS WITH NO MANDATE FROM YOU. The appointed head opens <c>head-unmandated</c> for the
    /// chief once it is active (dormant, it opens none); a mandate decided by someone else leaves it open; the chief's own mandate closes it, and
    /// cancelling that mandate opens it again. The legacy pair, who head the legacy divisions, never open one.
    ///
    /// <para>RED at base: "Assert.Single() Failure: The collection was empty". Mutant watched red: the decider filter dropped (any open mandate the head
    /// holds closes it).</para>
    /// </summary>
    [Fact]
    public void An_active_head_under_yours_with_no_mandate_from_you_is_your_open_work()
    {
        var now = ChartDay;
        var (_, db, org, chief) = ChiefHome(() => now);
        using var _ = db;
        Assert.Empty(OfKind(db, chief, "head-unmandated"));                    // operations and research head the legacy divisions: none

        var unit = CharterOnly(org, chief, now);
        var head = org.Hire(new HireRequest(chief, "turn-hire", unit, true, "its first head", ["appoint"], [], [ChartForecast]), now).Position.Id;
        Assert.Equal(OrgPositionStatus.Dormant, org.Position(head)!.Status);
        Assert.Empty(OfKind(db, chief, "head-unmandated"));                    // a dormant head is not yet anyone's to give work

        Chart(db, "UPDATE org_position SET status='active' WHERE id=$id", ("$id", head));
        Assert.Equal(head, Assert.Single(OfKind(db, chief, "head-unmandated")).Entity);

        // A MANDATE DECIDED BY SOMEONE ELSE does not close it: written as the chief's, then its decider rewritten under the test's own hand.
        var elsewhere = org.Assign(Work(chief, head, now), now);
        Chart(db, "UPDATE org_mandate SET decider=$d WHERE id=$m", ("$d", CouncilRoles.Research), ("$m", elsewhere.Id));
        Assert.Equal(head, Assert.Single(OfKind(db, chief, "head-unmandated")).Entity);

        var work = org.Assign(Work(chief, head, now), now);
        Assert.Empty(OfKind(db, chief, "head-unmandated"));
        org.Cancel(work.Id, chief, null, "it was the wrong question", now);
        Assert.Equal(head, Assert.Single(OfKind(db, chief, "head-unmandated")).Entity);
    }

    /// <summary>
    /// (b, c) THE CHIEF'S FLOOR WAKE SAYS WHAT IS WANTED, AND THE CLOCK IS UNTOUCHED. A chief that chartered a division and hired nobody: its next turn is
    /// still its four-hour floor, no scheduled look is raised for it, and that turn's Situation lists the vacant division under "What is wanted" in the
    /// app's words — its kind, when it was chartered, and the hire with its head flag as this seat calls it — never the bare kind and id.
    ///
    /// <para>RED at base: "Assert.Contains() Failure: Not found: - What is wanted" — the floor wake's packet carried no objective. Mutants watched red: Want's
    /// default branch for the new kind ("unit-unstaffed: `unit-…`"); the objective wired into the clock (a Review look raised for a head beyond the
    /// pair that holds open work).</para>
    /// </summary>
    [Fact]
    public async Task The_chiefs_floor_wake_names_the_unit_it_left_empty_and_its_clock_is_unchanged()
    {
        var start = ChartDay;
        var now = start;
        var (host, db, org, chief) = ChiefHome(() => now);
        using var _ = db;
        host.Owns = true;
        var unit = CharterOnly(org, chief, start);
        var loop = new MissionLoop(host, NoHeartbeat, now: () => now);

        var turns = await Run(loop, host, chief, t => now = t, start, 5);

        Assert.Equal([start.AddHours(4)], turns);
        Assert.DoesNotContain(host.Events!.OfKind(MissionEventKind.Review), e => e.Role == chief);
        var sent = Assert.Single(host.Conversations[chief].Sent);
        Assert.Contains("no work is moving in the organisation", sent, StringComparison.Ordinal);
        Assert.Contains("- What is wanted", sent, StringComparison.Ordinal);
        var line = Assert.Single(sent.Split('\n'), l => l.Contains(unit, StringComparison.Ordinal) && l.StartsWith("  - ", StringComparison.Ordinal));
        Assert.Contains($"the division `{unit}` under yours", line, StringComparison.Ordinal);
        Assert.Contains($"chartered {start:yyyy-MM-dd HH:mm} UTC", line, StringComparison.Ordinal);
        Assert.Contains("has no head", line, StringComparison.Ordinal);
        Assert.Contains($"`trade org hire {unit} <rationale> --forecasts JSON --head true`", line, StringComparison.Ordinal);
        Assert.DoesNotContain("unit-unstaffed:", sent, StringComparison.Ordinal);
    }
}

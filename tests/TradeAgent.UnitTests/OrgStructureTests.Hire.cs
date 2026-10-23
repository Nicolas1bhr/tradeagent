using System.Globalization;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>Item 4 of <c>U-org-verbs-c</c>: <c>org-hire</c>'s store writer, <see cref="OrgStore.Hire"/>.</summary>
public partial class OrgStructureTests
{
    /// <summary>A team under <see cref="Div"/>, chartered by <see cref="Head"/> citing current work: vacant, no position yet.</summary>
    static string Team(Database db, OrgStore org, DateTimeOffset? collapse = null) =>
        org.Charter(Ask(refs: collapse is null ? [CurrentRun(db)] : [], collapse: collapse), Metered, Now).Unit.Id;

    static HireRequest HireAsk(string unit, string caller = Head, bool asHead = false, IReadOnlyList<OrgRef>? refs = null,
        IReadOnlyList<ForecastClaim>? forecasts = null) =>
        new(caller, null, unit, asHead, "an executor for the skew family", [], refs ?? [], forecasts ?? [Admissible()]);

    /// <summary>
    /// (4a) A HIRED POSITION IS DORMANT, AND THE LEGACY PAIR KEEP TURNING (the activation seam; survey Q2): <c>research</c>, head of
    /// <c>div-research</c>, hires the first member of a vacant team under it — a minted <c>pos-…</c> id, DORMANT, home
    /// <c>org/&lt;id&gt;</c>, no seat, its <c>hire</c> decision and its <c>hired</c> event — and the activation gate's neighbour read
    /// names nobody, so a legacy launch on an unconfined (CLI) seat is NOT refused; the hired position itself opens no conversation.
    /// RED at base: no <c>OrgStore.Hire</c>. The watched mutant — <c>Hire</c> writes <c>active</c> — makes the pair Neighboured.
    /// </summary>
    [Fact]
    public void A_hired_position_is_dormant_and_the_legacy_pair_keep_turning()
    {
        using var db = TestEnv.NewDb();
        const string team = "team-9e5ea4c4";
        Exec(db, "INSERT INTO org_unit(id, parent_id, kind, status, created_at) VALUES($t, $p, 'team', 'active', $at)",
            ("$t", team), ("$p", OrgIds.Research), ("$at", Stamp));
        var org = new OrgStore(db);

        var hired = org.Hire(HireAsk(team, caller: CouncilRoles.Research), Now);
        var p = hired.Position;

        // NOTHING ACTIVATED — asked first, of the seam itself: no position beyond the legacy pair is active, so the pair's unconfined
        // launches keep turning.
        var register = new SeatRegister { Neighbours = () => new OrgStore(db).ActivePositionsBeyondLegacy() };
        foreach (var legacy in CouncilRoles.All)
            Assert.True(PositionActivation.AtLaunch(legacy, SeatClasses.Cli, register) is null,
                PositionActivation.AtLaunch(legacy, SeatClasses.Cli, register)?.Words);
        Assert.Empty(org.ActivePositionsBeyondLegacy());

        Assert.StartsWith(OrgIds.PositionPrefix, p.Id, StringComparison.Ordinal);
        Assert.Equal((team, OrgPositionStatus.Dormant, $"org/{p.Id}", (string?)null, (string?)null),
            (p.UnitId, p.Status, p.HomeDir, p.SeatRuntime, p.SeatModel));
        Assert.Equal((CouncilRoles.Research, OrgDecisionKind.Hire), (hired.Decision.Position, hired.Decision.Kind));
        Assert.Equal(team, Assert.Single(hired.Forecasts).Subject);
        Assert.Equal(1L, Count(db, "org_event", $"kind='hired' AND position_id='{p.Id}' AND decider='research'"));

        Assert.Contains("is not an active position", PositionActivation.Decide(p.Id, org.Standing(p.Id), null)!.Words, StringComparison.Ordinal);
    }

    /// <summary>
    /// (4b) A HIRE FILLS A VACANT HEAD AS AN APPOINTMENT, AND A UNIT CHARTERED AS AN EXPERIMENT HIRES INSIDE IT: the division's head
    /// appoints the head of a team it chartered, then hires a member — inside the experiment's life, without cited work;
    /// a hire into a unit chartered on cited work, which has positions already, cites current work again.
    /// </summary>
    [Fact]
    public void A_hire_appoints_a_vacant_head_and_an_experiment_hires_inside_its_life()
    {
        using var db = Chart();
        var org = new OrgStore(db);
        var experiment = Team(db, org, collapse: Now.AddDays(60));

        var head = org.Hire(HireAsk(experiment, asHead: true), Now).Position;
        Assert.Equal(head.Id, org.Units().Single(u => u.Id == experiment).HeadPositionId);
        Assert.Equal(OrgPositionKind.Head, org.Standing(head.Id)!.Kind);
        var member = org.Hire(HireAsk(experiment), Now).Position;
        Assert.All(new[] { head, member }, x => Assert.Equal(OrgPositionStatus.Dormant, x.Status));
        Assert.Contains(" experiment collapse_by=", db.Read(_ =>
        {
            using var c = db.Cmd("SELECT detail FROM org_event WHERE position_id=$p", ("$p", member.Id));
            return (string)c.ExecuteScalar()!;
        }), StringComparison.Ordinal);

        var cited = Team(db, org);
        org.Hire(HireAsk(cited), Now);
        Assert.Contains("the earning test", Assert.Throws<TradeAgentException>(() => org.Hire(HireAsk(cited), Now)).Message, StringComparison.Ordinal);
        org.Hire(HireAsk(cited, refs: [CurrentRun(db)]), Now);
    }

    public static TheoryData<string> HireRefusals => new()
    {
        "a member is not the parent's head", "a team head does not grow its own team", "a hire into div-operations is a board act",
        "the chief's appointment is a board act", "a replacement is a different operation", "no such unit", "a closed unit",
        "an eleventh position", "a sixth member", "nothing current cited", "no forecast"
    };

    /// <summary>
    /// (4c) EACH REFUSAL OF A HIRE, IN WORDS, WITH NOTHING WRITTEN: the rights matrix (a member; a team head into its own team; anything
    /// under <c>div-operations</c> and the chief's appointment, two-seat board acts no board seat can make yet), a filled head (a
    /// replacement, a different operation), a unit that is not there or is closed, an eleventh position, a sixth member, the earning test
    /// and a hire with no forecast. RED at base: no <c>OrgStore.Hire</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(HireRefusals))]
    public void Each_refusal_of_a_hire_is_in_words_and_writes_nothing(string refusal)
    {
        using var db = Chart();
        var org = new OrgStore(db);
        var team = Team(db, org);
        HireRequest ask;
        string words;
        switch (refusal)
        {
            case "a member is not the parent's head":
                ask = HireAsk(team, caller: Member); words = "only the parent unit's head"; break;
            case "a team head does not grow its own team":
                ask = HireAsk(Sub, caller: SubHead); words = "only the parent unit's head"; break;
            case "a hire into div-operations is a board act":
                ask = HireAsk(OpsTeam, caller: CouncilRoles.Operations); words = "a two-seat board act"; break;
            case "the chief's appointment is a board act":
                ask = HireAsk(OrgIds.Root, asHead: true); words = "the security board's"; break;
            case "a replacement is a different operation":
                ask = HireAsk(Sub, asHead: true); words = "a different operation from an appointment"; break;
            case "no such unit":
                ask = HireAsk("team-nowhere"); words = "no unit on the chart"; break;
            case "a closed unit":
                Exec(db, "UPDATE org_unit SET status='closed' WHERE id=$t", ("$t", team)); ask = HireAsk(team); words = "is closed"; break;
            case "an eleventh position":
                for (var i = 0; i < 4; i++) Seat(db, $"pos-e1e7e0000{i:00}", Sub, OrgPositionStatus.Dormant);
                ask = HireAsk(team); words = "10 positions that have not ended"; break;
            case "a sixth member":
                // FIVE MEMBERS IN THE SUBDIVISION, nine positions open in all: the member moves in, the ops team's head has ended.
                Exec(db, "UPDATE org_position SET unit_id=$s WHERE id=$m", ("$s", Sub), ("$m", Member));
                Exec(db, "UPDATE org_position SET status='ended' WHERE id=$o", ("$o", OpsHead));
                for (var i = 0; i < 4; i++) Seat(db, $"pos-5e7e00000{i:00}", Sub);
                Seat(db, "pos-5e7e00000ff", Sub, OrgPositionStatus.Ended);
                ask = HireAsk(Sub, refs: [CurrentRun(db)]); words = "has 5 members"; break;
            case "nothing current cited":
                org.Hire(HireAsk(team), Now); ask = HireAsk(team); words = "the earning test"; break;
            case "no forecast":
                ask = HireAsk(team, forecasts: []); words = "1 to 5 forecasts"; break;
            default:
                throw new InvalidOperationException(refusal);
        }

        var before = Written(db);
        var refused = Assert.Throws<TradeAgentException>(() => org.Hire(ask, Now));
        Assert.Contains(words, refused.Message, StringComparison.Ordinal);
        Assert.EndsWith("Nothing was written.", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, Written(db));
    }
}

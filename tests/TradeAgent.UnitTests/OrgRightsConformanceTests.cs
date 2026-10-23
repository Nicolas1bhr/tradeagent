using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE ONE RIGHTS MATRIX, AS DATA, AND EVERY ROW OF IT EXERCISED (<c>U-org-verbs-c</c>, item 2; ORCHESTRATION § 2.9): operation ×
/// the caller's relation to the target × reserved identities. R24-checked: what was "the owner's two presses" is "a two-seat board
/// act", and no answer is the owner's. The pipe half — every row × assurance class × position kind on seeded attempts — is
/// <c>Every_row_of_the_matrix_answers_on_the_pipe_as_it_reads</c> (with the verbs, item 6).
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public partial class OrgRightsConformanceTests
{
    static OrgUnitRow U(string id, string? parent, string kind, string? head = null) =>
        new(id, parent, kind, head, OrgUnitStatus.Active, null, null, DateTimeOffset.UnixEpoch, null);

    static readonly OrgUnitRow Root = U(OrgIds.Root, null, OrgUnitKind.Root);
    static readonly OrgUnitRow OpsDivision = U(OrgIds.Operations, OrgIds.Root, OrgUnitKind.Division, CouncilRoles.Operations);
    static readonly OrgUnitRow Research = U(OrgIds.Research, OrgIds.Root, OrgUnitKind.Division, CouncilRoles.Research);

    /// <summary>
    /// (2a) THE MATRIX ANSWERS EVERY COMBINATION THE CHART CAN PRESENT, AND NO ANSWER IS THE OWNER'S: for each structural operation,
    /// every reserved identity, every relation, <see cref="OrgRights.Answer"/> returns a row whose answer is one of the five words —
    /// the reserved identities each operation does not reserve answered by the relation (<see cref="OrgReserved.None"/>) — and the
    /// words "owner" and "two presses" appear in no answer. A combination with no row is REFUSED, never read as allowed. RED at base:
    /// no <c>OrgRights</c>.
    /// </summary>
    [Fact]
    public void The_matrix_answers_every_combination_and_none_is_the_owners()
    {
        foreach (var op in OrgOperation.Structural)
            foreach (var relation in OrgRelation.Each)
            {
                foreach (var reserved in Reachable(op))
                {
                    var row = OrgRights.Answer(op, reserved, relation);
                    Assert.Contains(row.Answer, OrgRightAnswer.All);
                    Assert.DoesNotContain("owner", row.Answer, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("press", row.Why, StringComparison.OrdinalIgnoreCase);
                }
                // AN UNRESERVED TARGET: only the parent's head is allowed.
                Assert.Equal(relation == OrgRelation.HeadOfParent ? OrgRightAnswer.Allowed : OrgRightAnswer.NotYours,
                    OrgRights.Answer(op, OrgReserved.None, relation).Answer);
                // THE ORDER-HOLDING DIVISION'S SUBTREE: the board's, whatever the relation — for every structural operation.
                Assert.Equal(OrgRightAnswer.BoardAct, OrgRights.Answer(op, OrgReserved.DivOperations, relation).Answer);
            }

        foreach (var op in OrgOperation.Mandate)
            Assert.Equal(OrgRightAnswer.MandateRowDecides, OrgRights.Answer(op, OrgReserved.None, OrgRelation.HeadOfTarget).Answer);

        // A COMBINATION NO ROW ANSWERS IS REFUSED, NEVER GUESSED.
        Assert.Contains("holds no answer", Assert.Throws<TradeAgentException>(() =>
            OrgRights.Answer(OrgOperation.Charter, OrgReserved.Chief, OrgRelation.HeadOfParent)).Message, StringComparison.Ordinal);
        Assert.Throws<TradeAgentException>(() => OrgRights.Answer("promote", OrgReserved.None, OrgRelation.HeadOfParent));

        // THE RESERVED ANSWERS, R24-CHECKED.
        Assert.Equal(OrgRightAnswer.BoardAct, OrgRights.Answer(OrgOperation.Appoint, OrgReserved.Chief, OrgRelation.Other).Answer);
        Assert.Equal(OrgRightAnswer.BoardAct, OrgRights.Answer(OrgOperation.Replace, OrgReserved.Chief, OrgRelation.HeadOfParent).Answer);
        Assert.Equal(OrgRightAnswer.BoardAct, OrgRights.Answer(OrgOperation.Retire, OrgReserved.Operations, OrgRelation.HeadOfParent).Answer);
        Assert.Equal(OrgRightAnswer.AppTriggerOrBoardAct, OrgRights.Answer(OrgOperation.Retire, OrgReserved.Research, OrgRelation.HeadOfParent).Answer);
        Assert.Equal(OrgRightAnswer.AppTriggerOrBoardAct, OrgRights.Answer(OrgOperation.Replace, OrgReserved.DivisionHead, OrgRelation.HeadOfParent).Answer);
        Assert.Equal(OrgRightAnswer.AppTriggerOrBoardAct, OrgRights.Answer(OrgOperation.Close, OrgReserved.DivResearch, OrgRelation.HeadOfParent).Answer);
        Assert.Equal(OrgRightAnswer.BoardAct, OrgRights.Answer(OrgOperation.Tilt, OrgReserved.Root, OrgRelation.HeadOfParent).Answer);

        // A BOARD ACT'S REFUSAL SAYS NO BOARD SEAT IS ACTIVE AND HANDS NOTHING TO THE OWNER.
        var board = OrgRights.Refusal(OrgRights.Answer(OrgOperation.Hire, OrgReserved.DivOperations, OrgRelation.HeadOfParent), "a hire")!;
        Assert.Contains("no board seat is active", board, StringComparison.Ordinal);
        Assert.DoesNotContain("owner", board, StringComparison.OrdinalIgnoreCase);
        Assert.Null(OrgRights.Refusal(OrgRights.Answer(OrgOperation.Hire, OrgReserved.None, OrgRelation.HeadOfParent), "a hire"));
    }

    /// <summary>
    /// (2b) THE CHART DECIDES THE RESERVED IDENTITY AND THE RELATION, never the caller: a unit under <c>div-operations</c> at any depth is
    /// reserved for every operation; the root's appointment is the chief's; a retirement of <c>operations</c>, <c>research</c> or a
    /// division head is reserved; a charter's relation is whether the caller heads the unit it charters under.
    /// </summary>
    [Fact]
    public void The_chart_decides_the_reserved_identity_and_the_relation()
    {
        var deep = U("team-under-ops", OrgIds.Operations, OrgUnitKind.Team, "pos-000000000001");
        foreach (var op in OrgOperation.Structural)
        {
            Assert.Equal(OrgReserved.DivOperations, OrgRights.ReservedOf(op, OpsDivision, [Root]));
            Assert.Equal(OrgReserved.DivOperations, OrgRights.ReservedOf(op, deep, [OpsDivision, Root]));
        }
        Assert.Equal(OrgReserved.Chief, OrgRights.ReservedOf(OrgOperation.Appoint, Root, []));
        Assert.Equal(OrgReserved.None, OrgRights.ReservedOf(OrgOperation.Appoint, U("team-x", OrgIds.Research, OrgUnitKind.Team), [Research, Root]));
        Assert.Equal(OrgReserved.Research, OrgRights.ReservedOf(OrgOperation.Retire, Research, [Root], CouncilRoles.Research));
        Assert.Equal(OrgReserved.DivisionHead, OrgRights.ReservedOf(OrgOperation.Replace, U("div-x", OrgIds.Root, OrgUnitKind.Division, "pos-000000000002"), [Root]));
        Assert.Equal(OrgReserved.DivResearch, OrgRights.ReservedOf(OrgOperation.Close, Research, [Root]));
        Assert.Equal(OrgReserved.Root, OrgRights.ReservedOf(OrgOperation.Tilt, Root, []));
        Assert.Equal(OrgReserved.None, OrgRights.ReservedOf(OrgOperation.Charter, Research, [Root]));

        Assert.Equal(OrgRelation.HeadOfParent, OrgRights.RelationOf(OrgOperation.Charter, CouncilRoles.Research, Research, [Root]));
        Assert.Equal(OrgRelation.Other, OrgRights.RelationOf(OrgOperation.Charter, "pos-000000000009", Research, [Root]));
        var team = U("team-y", OrgIds.Research, OrgUnitKind.Team, "pos-000000000003");
        Assert.Equal(OrgRelation.HeadOfParent, OrgRights.RelationOf(OrgOperation.Hire, CouncilRoles.Research, team, [Research, Root]));
        Assert.Equal(OrgRelation.HeadOfTarget, OrgRights.RelationOf(OrgOperation.Hire, "pos-000000000003", team, [Research, Root]));
        Assert.Equal(OrgRelation.Other, OrgRights.RelationOf(OrgOperation.Hire, "pos-000000000004", team, [Research, Root]));
    }

    /// <summary>The reserved identities <see cref="OrgRights.ReservedOf"/> can name for <paramref name="op"/>.</summary>
    static string[] Reachable(string op) => op switch
    {
        OrgOperation.Appoint => [OrgReserved.None, OrgReserved.DivOperations, OrgReserved.Chief],
        OrgOperation.Retire or OrgOperation.Replace => [OrgReserved.None, OrgReserved.DivOperations, OrgReserved.Operations, OrgReserved.Research,
            OrgReserved.DivisionHead, OrgReserved.Chief],
        OrgOperation.Close or OrgOperation.Merge => [OrgReserved.None, OrgReserved.DivOperations, OrgReserved.DivResearch, OrgReserved.Root],
        OrgOperation.Tilt => [OrgReserved.None, OrgReserved.DivOperations, OrgReserved.Root],
        _ => [OrgReserved.None, OrgReserved.DivOperations]
    };
}

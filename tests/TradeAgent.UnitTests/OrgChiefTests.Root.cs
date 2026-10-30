using System.Text.Json;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Unit;

public partial class OrgChiefTests
{
    // ---- item 5: the chief at the root -----------------------------------------------------------------------------------------------

    const string DivHead = "pos-c4d1c0ffee01", Div = "div-c4d1c0ffee", Stamp = "2026-10-11T12:00:00.0000000Z";

    /// <summary>A division under the root with its head, active — written raw, as the chart tests write their precondition.</summary>
    static void Division(Database db, string unit = Div, string head = DivHead)
    {
        Exec(db, "INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES($u,'org','division',NULL,'active',$at)",
            ("$u", unit), ("$at", Stamp));
        Exec(db, "INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES($p,$u,$h,'active',$at)",
            ("$p", head), ("$u", unit), ("$h", $"org/{head}"), ("$at", Stamp));
        Exec(db, "UPDATE org_unit SET head_position_id=$p WHERE id=$u", ("$p", head), ("$u", unit));
    }

    /// <summary>An ACTIVE chief, appointed by the owner's press and activated on a puppet seat, with a launch recorded on a harness seat.</summary>
    static string ActiveChief(Database db)
    {
        var org = new OrgStore(db);
        var chief = org.AppointChief(Puppet, Known, Now).Chief!;
        org.Activate(chief, _ => SeatClasses.Puppet, 3, Now);
        Funded(db, chief, SeatClasses.Harness, OrgIds.Root);
        return chief;
    }

    /// <summary>
    /// (5a) AN ESCALATION REACHES AN ACTIVE CHIEF BEFORE THE OWNER: a division head's escalation is addressed to the chief — active, its
    /// recorded seat able to carry the organisation's verbs — with one escalation wake to it; the chief's own escalation goes on to the
    /// owner as a record, no wake; a DORMANT chief is passed over with why, and the owner receives it. The chart page and
    /// <c>org-show</c> name the chief at the root. RED at base: the walk stopped at the root as the owner.
    /// </summary>
    [Fact]
    public async Task An_escalation_reaches_an_active_chief_before_the_owner()
    {
        var (gw, _, db) = await TestEnv.Ready();
        await using var _gw = gw;
        using var _db = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure());
        var org = new OrgStore(db);
        Division(db);
        Funded(db, DivHead, SeatClasses.Harness, Div, OrgIds.Root);

        // A DORMANT CHIEF IS PASSED OVER, WITH WHY.
        var dormant = org.AppointChief(Puppet, Known, Now).Chief!;
        var passed = org.RaiseEscalation(DivHead, null, "the division's day is spent before noon", null, Now);
        Assert.Null(passed.Recipient);
        Assert.Contains(passed.Skipped, s => s.Contains(dormant, StringComparison.Ordinal) && s.Contains("not active", StringComparison.Ordinal));
        org.VacateChief(org.Chief(), Now);

        var chief = ActiveChief(db);
        var raised = org.RaiseEscalation(DivHead, null, "the division needs a second family", null, Now);
        Assert.Equal(chief, raised.Recipient);
        Assert.NotNull(raised.WakeId);
        Assert.Equal(1, Count(db, "mission_event", $"kind='escalation' AND role='{chief}'"));

        var up = org.RaiseEscalation(chief, null, "the organisation needs the owner's word", null, Now);
        Assert.Null(up.Recipient);
        Assert.Null(up.WakeId);

        // THE CHART PAGE AND THE CALLER'S OWN CHAIN NAME THE CHIEF AT THE ROOT.
        var words = OrganisationWords.Of(OrganisationRead.From(org, new AiAttemptStore(db), proofHeld: false), null);
        Assert.Contains($"headed by the chief {chief}", words.Units.Single(u => u.Id == OrgIds.Root).Line, StringComparison.Ordinal);
        Assert.Equal("chief of the organisation", words.Units.Single(u => u.Id == OrgIds.Root).Positions.Single(p => p.Id == chief).Role);
        var show = await server.CallAsync(Frame(Ops.OrgShow), DivHead, Funded(db, DivHead, SeatClasses.Harness, Div, OrgIds.Root));
        Assert.True(show.Ok, Json.Write(show.Error));
        Assert.Equal(chief, Body(show).GetProperty("chain").EnumerateArray().Last().GetProperty("head").GetString());
    }

    static IpcRequest Frame(string op, params (string Key, object Value)[] args) => new()
    {
        Op = op,
        Session = "chief",
        RequestId = "chief-" + Guid.NewGuid().ToString("n")[..12],
        Args = args.ToDictionary(a => a.Key, a => JsonSerializer.SerializeToElement(a.Value))
    };

    static JsonElement Body(IpcResponse r) => JsonDocument.Parse(Json.Write(r.Data)).RootElement;

    static ForecastClaim Admissible() =>
        new(ForecastVocabulary.UnitAdmissible, null, ForecastVocabulary.Admitted, Now.AddDays(45), 0.4m);

    /// <summary>
    /// (5b) THE CHIEF CHARTERS DIVISIONS AND NEVER REACHES <c>operations</c>, THE BOARD OR AN ORDER: an active chief charters a division
    /// under the root and appoints its vacant head; a seventh open division is refused. Refused in words, nothing written: a hire into
    /// <c>div-operations</c>, replacing a division head, retiring <c>operations</c>, tilting or closing the organisation itself — the
    /// matrix's board-act refusal naming the board's own verbs — every <c>board-*</c> op, and an order. RED at base: the board-act refusal
    /// said the board's verbs were not in this build.
    /// </summary>
    [Fact]
    public async Task The_chief_charters_divisions_and_never_reaches_operations_the_board_or_an_order()
    {
        var (gw, _, db) = await TestEnv.Ready();
        await using var _gw = gw;
        using var _db = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure());
        var org = new OrgStore(db);
        var chief = ActiveChief(db);
        var funding = new CharterFunding(SeatClasses.Harness, 0.10m, [new(BillingClasses.Plan, 10m)],
            new Dictionary<string, decimal> { [OrgIds.Operations] = 0m, [OrgIds.Research] = 0m });

        var chartered = org.Charter(new CharterRequest(chief, null, OrgUnitKind.Division, 0.2m, "a division for the funding family",
            ["a division"], [], [Admissible()], Now.AddDays(30)), funding, Now);
        Assert.Equal((OrgIds.Root, OrgUnitKind.Division), (chartered.Unit.ParentId, chartered.Unit.Kind));
        var head = org.Hire(new HireRequest(chief, null, chartered.Unit.Id, true, "its first head", ["appoint"], [], [Admissible()]), Now);
        Assert.Equal(head.Position.Id, org.Units().Single(u => u.Id == chartered.Unit.Id).HeadPositionId);

        var before = Written(db);
        foreach (var (what, refused, says) in new (string, Action, string)[]
                 {
                     ("a hire into div-operations", () => org.Hire(new HireRequest(chief, null, OrgIds.Operations, false, "a trader", ["hire"], [], [Admissible()]), Now), "board-propose"),
                     ("replacing a division head", () => org.Hire(new HireRequest(chief, null, OrgIds.Research, true, "a new head", ["hire"], [], [Admissible()]), Now), "app trigger or a board act"),
                     ("retiring operations", () => org.Retire(new RetireRequest(chief, null, CouncilRoles.Operations, "why", ["retire"], [], [Admissible()]), Now), "board-propose"),
                     ("tilting the organisation", () => org.Tilt(new TiltRequest(chief, null, OrgIds.Root, OrgCurrency.Ai, 0.5m, "why", ["tilt"], [], [Admissible()]), funding, Now), "board-propose"),
                     ("closing the organisation", () => org.Close(new CloseRequest(chief, null, OrgIds.Root, "why", ["close"], [], [Admissible()]), Now), "board-propose")
                 })
        {
            var words = Assert.Throws<TradeAgentException>(refused).Message;
            log.WriteLine($"{what}: {words}");
            Assert.Contains(says, words, StringComparison.Ordinal);
            Assert.DoesNotContain("not in this build", words, StringComparison.Ordinal);
        }
        Assert.Equal(before, Written(db));

        // A SEVENTH OPEN DIVISION.
        foreach (var n in Enumerable.Range(1, 3))
            Exec(db, "INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES($u,'org','division',NULL,'active',$at)",
                ("$u", $"div-c4d1c0ffee{n}"), ("$at", Stamp));
        Assert.Contains("6 open divisions", Assert.Throws<TradeAgentException>(() => org.Charter(new CharterRequest(chief, null,
            OrgUnitKind.Division, 0.05m, "one more", ["a division"], [], [Admissible()], Now.AddDays(30)), funding, Now.AddDays(8))).Message,
            StringComparison.Ordinal);

        // EVERY BOARD OP, AND AN ORDER: refused at the pipe; never order permission, as a role or a context.
        var launch = Funded(db, chief, SeatClasses.Harness, OrgIds.Root);
        foreach (var op in Ops.Board)
        {
            var refused = await server.CallAsync(Frame(op), chief, launch);
            Assert.False(refused.Ok, $"{op} was served to the chief");
            Assert.Contains("No position holds the board's verbs", refused.Error!.Message, StringComparison.Ordinal);
        }
        Assert.False(CouncilRoles.MayPlaceOrders(chief));
        Assert.False(AgentContext.ForAgent("s", chief).MayPlaceOrders);
        var buy = await server.CallAsync(Frame(Ops.Buy, ("symbol", "ES"), ("quantity", 1)), chief, launch);
        Assert.False(buy.Ok, "the chief's buy was served");
        Assert.Equal(nameof(ErrorCode.ROLE_MAY_NOT_TRADE), buy.Error!.Code);
        Assert.Equal(before[5..7], Written(db)[5..7]);
    }
}

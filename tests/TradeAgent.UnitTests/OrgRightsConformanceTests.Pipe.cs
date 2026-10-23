using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE MATRIX ON THE PIPE (<c>U-org-verbs-c</c>, items 2 and 6): every row the structural verbs of this build can reach — charter,
/// hire, appointment, and a replacement asked through a hire's head flag — is called through <see cref="GatewayPipeServer"/> under
/// launches the TEST seeded in the ledger on each assurance class (unconfined CLI, mediated broker, brokered harness, confined cell; the
/// <c>OrgRightsTests</c> pattern), by each kind of position the row can name (a head of the parent, a head of the target or of a unit
/// elsewhere, a member). Below the organisation's minimum the GATE refuses whatever the row says; at or above it the ROW decides:
/// allowed is written, every other answer refused in its words with nothing written. Every row no verb of this build reaches is
/// named, and no op serves it.
/// </summary>
public partial class OrgRightsConformanceTests
{
    const string Head = "pos-c0f0c0f00001", Member = "pos-c0f0c0f00002", TeamHead = "pos-c0f0c0f00003", OpsHead = "pos-c0f0c0f00004";

    const string Div = "div-c0f0c0f0", Vacant = "team-c0f0c0f1", Headed = "team-c0f0c0f2", OpsTeam = "team-c0f0c0f3", OpsVacant = "team-c0f0c0f4";

    const string Stamp = "2026-10-11T12:00:00.0000000Z";

    /// <summary>The seat classes whose launches the test records, one per assurance class.</summary>
    static readonly string[] Seats = [SeatClasses.Cli, SeatClasses.Broker, SeatClasses.Harness, SeatClasses.Cell];

    static void Exec(Database db, string sql, params (string, object?)[] ps) => db.Write(_ =>
    {
        using var c = db.Cmd(sql, ps);
        return c.ExecuteNonQuery();
    });

    static void Unit(Database db, string id, string parent, string kind, string? head)
    {
        Exec(db, "INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES($t,$p,$k,NULL,'active',$at)",
            ("$t", id), ("$p", parent), ("$k", kind), ("$at", Stamp));
        if (head is null) return;
        Position(db, head, id);
        Exec(db, "UPDATE org_unit SET head_position_id=$h WHERE id=$t", ("$h", head), ("$t", id));
    }

    static void Position(Database db, string id, string unit) =>
        Exec(db, "INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES($id,$u,$h,'active',$at)",
            ("$id", id), ("$u", unit), ("$h", $"org/{id}"), ("$at", Stamp));

    /// <summary>
    /// The chart: <see cref="Head"/> heads the division <see cref="Div"/> with a member; under it a vacant team and a team
    /// <see cref="TeamHead"/> heads; <see cref="OpsHead"/> heads a team under <c>div-operations</c>, beside a vacant one.
    /// </summary>
    static async Task<(TradingGateway Gw, Database Db, GatewayPipeServer Server)> Pipe()
    {
        var (gw, _, db) = await TestEnv.Ready();
        Unit(db, Div, OrgIds.Root, OrgUnitKind.Division, Head);
        Position(db, Member, Div);
        Unit(db, Vacant, Div, OrgUnitKind.Team, null);
        Unit(db, Headed, Div, OrgUnitKind.Team, TeamHead);
        Unit(db, OpsTeam, OrgIds.Operations, OrgUnitKind.Team, OpsHead);
        Unit(db, OpsVacant, OrgIds.Operations, OrgUnitKind.Team, null);
        // THE HOST'S METER, AS ADMISSION WOULD READ IT: each unit of the caller's chain with an envelope in the plan's ceiling.
        gw.AiEnvelopes = role => [.. new OrgStore(db).FundingLine(role).Select(u => new UnitDay(u.Id, BillingClasses.Plan, 10m, 0m, 0m, 0))];
        return (gw, db, new GatewayPipeServer(gw, IpcToken.Ensure()));
    }

    static string Launch(Database db, string role, string seatClass)
    {
        var id = $"turn-{Guid.NewGuid():n}"[..17];
        var admission = new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = id, StartedAt = DateTimeOffset.UtcNow, Role = role, ReservedCost = 0.10m, SeatClass = seatClass,
            Assurance = SeatClasses.AssuranceOf(seatClass) is { } a ? SeatClasses.Name(a) : null
        });
        Assert.True(admission.Admitted, admission.Refusal);
        return id;
    }

    static IpcRequest Frame(string op, params (string Key, object Value)[] args) => new()
    {
        Op = op,
        Session = "conformance",
        RequestId = "conformance-" + Guid.NewGuid().ToString("n")[..12],
        Args = args.ToDictionary(a => a.Key, a => JsonSerializer.SerializeToElement(a.Value))
    };

    static readonly string Forecasts = JsonSerializer.Serialize(new[]
    {
        new { predicate = ForecastVocabulary.UnitAdmissible, target = ForecastVocabulary.Admitted, by = DateTimeOffset.UtcNow.AddDays(45).ToString("O"), probability = 0.4m }
    });

    static IpcRequest CharterFrame() => Frame(Ops.OrgCharter, ("kind", OrgUnitKind.Team), ("ai_share", 0.2m),
        ("rationale", "the skew family"), ("forecasts", Forecasts), ("collapse_by", DateTimeOffset.UtcNow.AddDays(60).ToString("O")));

    static IpcRequest HireFrame(string unit, bool head) => Frame(Ops.OrgHire, ("unit", unit), ("rationale", "an executor"),
        ("forecasts", Forecasts), ("head", head ? "true" : "false"));

    /// <summary>One call a row is exercised by: the row, who calls, and the frame.</summary>
    sealed record Case(OrgRightsRow Row, string Caller, IpcRequest Frame);

    /// <summary>Every row a verb of this build reaches, each with the callers that name it.</summary>
    static List<Case> Reachable()
    {
        OrgRightsRow R(string op, string reserved, string relation) => OrgRights.Answer(op, reserved, relation);
        return
        [
            new(R(OrgOperation.Charter, OrgReserved.None, OrgRelation.HeadOfParent), Head, CharterFrame()),
            new(R(OrgOperation.Charter, OrgReserved.None, OrgRelation.Other), Member, CharterFrame()),
            new(R(OrgOperation.Charter, OrgReserved.DivOperations, OrgRelation.HeadOfParent), OpsHead, CharterFrame()),
            new(R(OrgOperation.Charter, OrgReserved.DivOperations, OrgRelation.HeadOfParent), CouncilRoles.Operations, CharterFrame()),

            new(R(OrgOperation.Hire, OrgReserved.None, OrgRelation.HeadOfParent), Head, HireFrame(Vacant, false)),
            new(R(OrgOperation.Hire, OrgReserved.None, OrgRelation.HeadOfTarget), TeamHead, HireFrame(Headed, false)),
            new(R(OrgOperation.Hire, OrgReserved.None, OrgRelation.Other), Member, HireFrame(Vacant, false)),
            new(R(OrgOperation.Hire, OrgReserved.DivOperations, OrgRelation.Other), OpsHead, HireFrame(OpsTeam, false)),
            new(R(OrgOperation.Hire, OrgReserved.DivOperations, OrgRelation.Other), Head, HireFrame(OpsVacant, false)),

            new(R(OrgOperation.Appoint, OrgReserved.None, OrgRelation.HeadOfParent), Head, HireFrame(Vacant, true)),
            new(R(OrgOperation.Appoint, OrgReserved.None, OrgRelation.Other), Member, HireFrame(Vacant, true)),
            new(R(OrgOperation.Appoint, OrgReserved.DivOperations, OrgRelation.Other), OpsHead, HireFrame(OpsVacant, true)),
            new(R(OrgOperation.Appoint, OrgReserved.Chief, OrgRelation.Other), Head, HireFrame(OrgIds.Root, true)),
            new(R(OrgOperation.Appoint, OrgReserved.Chief, OrgRelation.Other), Member, HireFrame(OrgIds.Root, true)),

            new(R(OrgOperation.Replace, OrgReserved.None, OrgRelation.HeadOfParent), Head, HireFrame(Headed, true)),
            new(R(OrgOperation.Replace, OrgReserved.None, OrgRelation.HeadOfTarget), TeamHead, HireFrame(Headed, true)),
            new(R(OrgOperation.Replace, OrgReserved.None, OrgRelation.Other), Member, HireFrame(Headed, true)),
            new(R(OrgOperation.Replace, OrgReserved.DivOperations, OrgRelation.Other), Head, HireFrame(OpsTeam, true)),
            new(R(OrgOperation.Replace, OrgReserved.DivisionHead, OrgRelation.Other), Head, HireFrame(Div, true)),
            new(R(OrgOperation.Replace, OrgReserved.DivisionHead, OrgRelation.Other), Member, HireFrame(Div, true)),
            new(R(OrgOperation.Replace, OrgReserved.Research, OrgRelation.Other), Head, HireFrame(OrgIds.Research, true)),
        ];
    }

    /// <summary>
    /// (2c) EVERY ROW OF THE MATRIX ANSWERS ON THE PIPE AS IT READS, ON EVERY ASSURANCE CLASS: an unconfined launch is refused by the
    /// gate whatever the row ("headship grants no verb by itself"); a mediated, brokered or confined one gets the row's answer — allowed
    /// is written (a unit chartered, a dormant position hired or appointed), a replacement is refused as a different operation, and
    /// every other answer is refused in its own words with nothing written. Every row is either exercised here or named as one no verb
    /// of this build reaches, and none of those has an op. RED at base: no <c>org-charter</c> or <c>org-hire</c> op.
    /// </summary>
    [Fact]
    public async Task Every_row_of_the_matrix_answers_on_the_pipe_as_it_reads()
    {
        var cases = Reachable();
        foreach (var c in cases)
            foreach (var seat in Seats)
            {
                var (gw, db, server) = await Pipe();
                await using var _gw = gw;
                using var _db = db;
                await using var _server = server;
                var before = Written(db);
                var reply = await server.CallAsync(c.Frame, c.Caller, Launch(db, c.Caller, seat));
                var said = reply.Error?.Message ?? "";
                var label = $"{c.Row.Operation}/{c.Row.Reserved}/{c.Row.Relation} by {c.Caller} on a {seat} seat";

                if (SeatClasses.AssuranceOf(seat) < CapabilityPolicy.Minimum(Capability.DecisionVerbs))
                {
                    Assert.False(reply.Ok, $"{label} was served");
                    Assert.Contains("Heading a unit grants no verb by itself", said, StringComparison.Ordinal);
                }
                else if (c.Row.Operation == OrgOperation.Replace)
                {
                    Assert.False(reply.Ok, $"{label} was served");
                    Assert.Contains("a different operation from an appointment", said, StringComparison.Ordinal);
                    if (c.Row.Answer != OrgRightAnswer.Allowed) Assert.Contains(c.Row.Answer, said, StringComparison.Ordinal);
                }
                else if (c.Row.Answer == OrgRightAnswer.Allowed)
                {
                    Assert.True(reply.Ok, $"{label}: {said}");
                    Assert.NotEqual(before, Written(db));
                    continue;
                }
                else
                {
                    Assert.False(reply.Ok, $"{label} was served");
                    Assert.Contains(c.Row.Answer == OrgRightAnswer.NotYours ? "is not yours" : c.Row.Answer, said, StringComparison.Ordinal);
                }
                Assert.Equal(before, Written(db));
            }

        // EVERY ROW IS EXERCISED ABOVE OR NAMED HERE AS ONE NO VERB OF THIS BUILD REACHES — and no op serves one of those.
        var exercised = cases.Select(c => c.Row).ToHashSet();
        var unreached = OrgRights.Rows.Where(r => !exercised.Contains(r)).ToList();
        foreach (var row in unreached)
        {
            var why = row.Operation switch
            {
                OrgOperation.Retire or OrgOperation.Close or OrgOperation.Merge or OrgOperation.Tilt => "its verb arrives with part d or e",
                _ when OrgOperation.Mandate.Contains(row.Operation) => "a mandate verb: the row decides, never the chart",
                OrgOperation.Charter when row.Relation == OrgRelation.HeadOfTarget => "a unit not yet chartered has no head",
                OrgOperation.Appoint when row.Relation == OrgRelation.HeadOfTarget => "a vacant unit has no head",
                OrgOperation.Replace when row.Reserved is OrgReserved.Operations or OrgReserved.Chief =>
                    "operations' unit is div-operations' (its row answers first), and the root's head is the owner today",
                _ => null
            };
            Assert.True(why is not null, $"row {row.Operation}/{row.Reserved}/{row.Relation} is neither exercised nor named");
            if (row.Operation is OrgOperation.Retire or OrgOperation.Close or OrgOperation.Merge or OrgOperation.Tilt or OrgOperation.Replace)
                Assert.DoesNotContain(Ops.Organisation, op => op.EndsWith("-" + row.Operation, StringComparison.Ordinal));
        }
    }

    static long[] Written(Database db) => db.Read(_ =>
    {
        var counts = new List<long>();
        foreach (var table in new[] { "org_unit", "org_position", "org_event", "org_decision", "org_forecast" })
        {
            using var c = db.Cmd($"SELECT COUNT(*) FROM {table}");
            counts.Add((long)c.ExecuteScalar()!);
        }
        return counts.ToArray();
    });
}

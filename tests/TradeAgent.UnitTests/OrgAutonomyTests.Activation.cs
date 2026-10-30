using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEM 2 (R25 D3, gap 5): A HIRE ACTIVATES ITSELF INSIDE THE GATE, AND ACTIVATION WAKES THE HIRE AND ITS HIRER. The host's facts are the
/// test's: a declared seat class per position and a proven cell or none — never a real model.
/// </summary>
public partial class OrgAutonomyTests
{
    /// <summary>A unit under <see cref="Team"/> with a vacant head, so <see cref="Head"/> — its parent's head — hires into it.</summary>
    static string SubUnit(Database db, string id)
    {
        Exec(db, "INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES($t,$p,'team',NULL,'active',$at)",
            ("$t", id), ("$p", Team), ("$at", Stamp));
        return id;
    }

    static IpcRequest HireFrame(string unit, bool head = true) => Frame(Ops.OrgHire, ("unit", unit), ("rationale", "an executor for the skew"),
        ("forecasts", System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { predicate = ForecastVocabulary.UnitAdmissible, target = ForecastVocabulary.Admitted, by = DateTimeOffset.UtcNow.AddDays(45).ToString("O"), probability = 0.4m }
        })), ("head", head ? "true" : "false"));

    /// <summary>The host's facts for any position: its standing on the chart, the class <paramref name="seat"/> declares, a proven cell or not.</summary>
    static void Host(TradingGateway gw, Database db, Func<string, string?> seat, bool provenCell = true, decimal? turn = 0.10m)
    {
        gw.SeatFactsOf = id => new SeatFacts
        {
            Standing = new OrgStore(db).Standing(id),
            DeclaredClass = seat(id),
            ProvenCell = provenCell,
            ActiveBeyondLegacy = new OrgStore(db).ActivePositionsBeyondLegacy()
        };
        gw.AiReservation = _ => turn;
    }

    static long Activations(Database db, string position) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT COUNT(*) FROM org_event WHERE kind = $k AND position_id = $p", ("$k", OrgEventKind.Activated), ("$p", position));
        return (long)c.ExecuteScalar()!;
    });

    static List<MissionEvent> ActivatedWakes(Database db) => new MissionEventStore(db).OfKind(MissionEventKind.Activated);

    /// <summary>
    /// (2a) A HIRE ONTO A BROKER SEAT — OR A CELL SEAT — THE GATE ADMITS, WITH ROOM IN ITS DAY, ACTIVATES ITSELF IN THE SAME WRITE: it is
    /// active, one <c>activated</c> event decided by the hiring head, and ONE wake to the hire and ONE to its hirer. The owner's narrowing
    /// veto still ends it. RED at base: every hire is dormant and wakes nobody.
    /// </summary>
    [Fact]
    public async Task A_hire_onto_a_broker_or_cell_seat_the_gate_admits_activates_itself_and_wakes_hire_and_hirer()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        Host(gw, db, id => new OrgStore(db).Standing(id)?.Kind == OrgPositionKind.Member ? SeatClasses.Cell : SeatClasses.Broker);

        var unit = SubUnit(db, "team-a7a7c0df");
        var hired = await server.CallAsync(HireFrame(unit), Head, Launch(db, Head));
        Assert.True(hired.Ok, hired.Error?.Message);
        var body = Body(hired);
        var id = body.GetProperty("position").GetString()!;
        Assert.Equal(OrgPositionStatus.Active, body.GetProperty("status").GetString());
        Assert.True(Absent(body, "dormant_because"));
        Assert.Equal(OrgPositionStatus.Active, new OrgStore(db).Position(id)!.Status);
        Assert.Equal(1L, Activations(db, id));
        Assert.Equal(Head, db.Read(_ =>
        {
            using var c = db.Cmd("SELECT decider FROM org_event WHERE kind = $k AND position_id = $p", ("$k", OrgEventKind.Activated), ("$p", id));
            return (string)c.ExecuteScalar()!;
        }));
        var wakes = ActivatedWakes(db);
        Assert.Equal(new[] { Head, id }.Order(StringComparer.Ordinal), wakes.Select(w => w.Role!).Order(StringComparer.Ordinal));
        Assert.All(wakes, w => Assert.Equal(id, w.Payload));

        // A MEMBER ONTO A CELL SEAT activates itself the same way.
        var member = await server.CallAsync(HireFrame(SubUnit(db, "team-a7a7c0e0"), head: false), Head, Launch(db, Head));
        Assert.True(member.Ok, member.Error?.Message);
        Assert.Equal(OrgPositionStatus.Active, Body(member).GetProperty("status").GetString());

        // THE OWNER'S NARROWING VETO ENDS THE SELF-ACTIVATED HIRE (two presses on his page call this one writer).
        var hiredEvent = new OrgStore(db).Vetoable().Rows.Single(v => v.PositionId == id && v.Kind == OrgEventKind.Hired);
        new OrgStore(db).Veto(hiredEvent.EventId, DateTimeOffset.UtcNow);
        Assert.Equal(OrgPositionStatus.Ended, new OrgStore(db).Position(id)!.Status);
    }

    /// <summary>
    /// (2b) OUTSIDE THE GATE THE HIRE STAYS DORMANT, SAYS WHY, AND WAKES NOBODY: no proven cell on this host; an unconfined (CLI) seat;
    /// a host that read no facts; no room for one turn in the day; a seat nobody priced. RED at base: no reason is given (the hire answers
    /// no 'dormant_because').
    /// </summary>
    [Fact]
    public async Task A_hire_outside_the_gate_stays_dormant_says_why_and_wakes_nobody()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;

        var cases = new (string Unit, Action Arrange, string Words)[]
        {
            ("team-a7a7c0e1", () => Host(gw, db, _ => SeatClasses.Broker, provenCell: false), "no proven executor cell recorded"),
            ("team-a7a7c0e2", () => Host(gw, db, _ => SeatClasses.Cli), "only onto a broker or a cell seat"),
            ("team-a7a7c0e3", () => gw.SeatFactsOf = null, "nothing has told this host which seat"),
            ("team-a7a7c0e4", () => { Host(gw, db, _ => SeatClasses.Broker); gw.AiEnvelopes = Metered(db, spent: 9.95m); }, "committed today"),
            ("team-a7a7c0e5", () => { Host(gw, db, _ => SeatClasses.Broker, turn: null); gw.AiEnvelopes = Metered(db); }, "could not price one turn")
        };
        foreach (var (unit, arrange, words) in cases)
        {
            arrange();
            var hired = await server.CallAsync(HireFrame(SubUnit(db, unit)), Head, Launch(db, Head));
            Assert.True(hired.Ok, hired.Error?.Message);
            var body = Body(hired);
            Assert.Equal(OrgPositionStatus.Dormant, body.GetProperty("status").GetString());
            Assert.Contains(words, body.GetProperty("dormant_because").GetString(), StringComparison.Ordinal);
            Assert.Equal(0L, Activations(db, body.GetProperty("position").GetString()!));
        }
        Assert.Empty(ActivatedWakes(db));
    }

    /// <summary>
    /// (2c) THE OWNER'S ACTIVATION WAKES THE POSITION AND ITS HIRER TOO (gap 5): a hire left dormant, activated by his in-process press,
    /// raises one wake to it and one to the head that hired it. RED at base: activation wakes nobody.
    /// </summary>
    [Fact]
    public async Task The_owners_activation_wakes_the_position_and_its_hirer()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        gw.SeatFactsOf = null;

        var hired = await server.CallAsync(HireFrame(SubUnit(db, "team-a7a7c0e6")), Head, Launch(db, Head));
        var id = Body(hired).GetProperty("position").GetString()!;
        Assert.Empty(ActivatedWakes(db));

        new OrgStore(db).Activate(id, _ => SeatClasses.Broker, 3, DateTimeOffset.UtcNow, () => true);
        Assert.Equal(new[] { Head, id }.Order(StringComparer.Ordinal), ActivatedWakes(db).Select(w => w.Role!).Order(StringComparer.Ordinal));
    }

    static bool Absent(System.Text.Json.JsonElement e, string name) =>
        !e.TryGetProperty(name, out var v) || v.ValueKind == System.Text.Json.JsonValueKind.Null;
}

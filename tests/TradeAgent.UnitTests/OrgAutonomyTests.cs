using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE ORGANISATION RUNS WITHOUT OWNER PRESSES, INSIDE THE BOUNDARIES THE APP ENFORCES (<c>U-org-autonomy</c>; R25 D3–D6, D9). Every caller
/// is an in-process <see cref="GatewayPipeServer"/> call under a launch id the TEST seeded in the ledger with the seat assurance it names —
/// the <see cref="OrgAssignmentTests"/> pattern — and every position beyond the legacy pair is written by the test with raw SQL. No real
/// model.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public partial class OrgAutonomyTests
{
    /// <summary>The head of a team under Research's division (team level), and a member of it.</summary>
    const string Head = "pos-a7a7c0de0001", Member = "pos-a7a7c0de0002";

    const string Team = "team-a7a7c0de";

    const string Stamp = "2026-10-30T12:00:00.0000000Z";

    static void Exec(Database db, string sql, params (string, object?)[] ps) => db.Write(_ =>
    {
        using var c = db.Cmd(sql, ps);
        return c.ExecuteNonQuery();
    });

    static void Seat(Database db, string id, string unit, string status = OrgPositionStatus.Active) =>
        Exec(db, "INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES($id,$u,$h,$s,$at)",
            ("$id", id), ("$u", unit), ("$h", $"org/{id}"), ("$s", status), ("$at", Stamp));

    static void HeadATeam(Database db, string position, string team, string parent = OrgIds.Research)
    {
        Exec(db, "INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES($t,$p,'team',NULL,'active',$at)",
            ("$t", team), ("$p", parent), ("$at", Stamp));
        Seat(db, position, team);
        Exec(db, "UPDATE org_unit SET head_position_id=$id WHERE id=$t", ("$id", position), ("$t", team));
    }

    static void Chart(Database db)
    {
        HeadATeam(db, Head, Team);
        Seat(db, Member, Team);
    }

    /// <summary>A launch the test recorded, LAUNCHED, on a seat of <paramref name="seatClass"/> — never a real model.</summary>
    static string Launch(Database db, string role, string seatClass = SeatClasses.Broker, decimal reserved = 0.10m)
    {
        var id = $"turn-{Guid.NewGuid():n}"[..17];
        var admission = new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = id, StartedAt = DateTimeOffset.UtcNow, Role = role, ReservedCost = reserved, SeatClass = seatClass,
            Assurance = SeatClasses.AssuranceOf(seatClass) is { } a ? SeatClasses.Name(a) : null
        });
        Assert.True(admission.Admitted, admission.Refusal);
        return id;
    }

    static IpcRequest Frame(string op, params (string Key, object Value)[] args) => new()
    {
        Op = op,
        Session = "autonomy",
        RequestId = "autonomy-" + Guid.NewGuid().ToString("n")[..12],
        Args = args.ToDictionary(a => a.Key, a => JsonSerializer.SerializeToElement(a.Value))
    };

    static JsonElement Body(IpcResponse r) => JsonDocument.Parse(Json.Write(r.Data)).RootElement;

    /// <summary>Every unit of a position's funding chain metered at <paramref name="envelope"/> with <paramref name="spent"/> spent today.</summary>
    static Func<string, IReadOnlyList<UnitDay>?> Metered(Database db, decimal envelope = 10m, decimal spent = 0m) =>
        role => [.. new OrgStore(db).FundingLine(role).Select(u => new UnitDay(u.Id, BillingClasses.Plan, envelope, spent, 0m, 0, 0, 0m))];

    static async Task<(TradingGateway Gw, Database Db, GatewayPipeServer Server)> Pipe()
    {
        var (gw, _, db) = await TestEnv.Ready();
        Chart(db);
        gw.AiEnvelopes = Metered(db);
        return (gw, db, new GatewayPipeServer(gw, IpcToken.Ensure()));
    }

    static IpcRequest WakeFrame(object minutes) => Frame(Ops.OrgWake, ("after_minutes", minutes));

    static List<MissionEvent> Asked(Database db, string role) =>
        [.. new MissionEventStore(db).OfKind(MissionEventKind.Self).Where(e => e.Role == role)];

    // ---- item 1: a position asks for its next wake (R25 D5) ---------------------------------------------------------------------

    /// <summary>
    /// (1a) A HEAD ON A BROKER SEAT ASKS FOR ITS NEXT WAKE AND IS WOKEN ONCE: one <c>self</c> wake for it, due when it asked, inside the
    /// policy's floor (60 minutes at the team level) and ceiling (24 hours); a second request while the first is untaken is refused in
    /// words, nothing written. The verb is in the broker and the cell seat's reach, so a canon that derives verbs from the reach names it.
    /// RED at base: no <c>org-wake</c> op (unknown operation).
    /// </summary>
    [Fact]
    public async Task A_head_asks_for_its_next_wake_and_one_self_wake_is_written()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;

        var before = DateTimeOffset.UtcNow;
        var ok = await server.CallAsync(WakeFrame(90), Head, Launch(db, Head));
        Assert.True(ok.Ok, ok.Error?.Message);
        var wake = Assert.Single(Asked(db, Head));
        Assert.InRange(wake.DueAt, before.AddMinutes(90), DateTimeOffset.UtcNow.AddMinutes(90));
        var body = Body(ok);
        Assert.Equal(OrgLevel.Team, body.GetProperty("level").GetString());
        Assert.Equal(60, body.GetProperty("floor_minutes").GetInt32());
        Assert.Equal(24 * 60, body.GetProperty("ceiling_minutes").GetInt32());

        var again = await server.CallAsync(WakeFrame(120), Head, Launch(db, Head));
        Assert.False(again.Ok);
        Assert.Contains("one request at a time", again.Error!.Message, StringComparison.Ordinal);
        Assert.Single(Asked(db, Head));

        foreach (var runtime in new[] { RuntimeClass.Broker, RuntimeClass.Cell })
            Assert.Contains(AgentReach.For(Head, runtime).Verbs, v => v.Op == Ops.OrgWake);
        Assert.DoesNotContain(Ops.OrgWake, Ops.Mutating);
    }

    /// <summary>
    /// (1b) A WAKE BEFORE THE FLOOR, PAST THE CEILING, FOR NOTHING, OR FROM A SEAT THAT CARRIES NO ORGANISATION VERB IS REFUSED IN WORDS,
    /// nothing written. RED at base: no <c>org-wake</c> op.
    /// </summary>
    [Fact]
    public async Task A_wake_before_the_floor_past_the_ceiling_or_from_an_unconfined_seat_is_refused()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;

        foreach (var (minutes, words) in new (object, string)[]
                 {
                     (30, "floor between wakes at the team level is 60 minutes"),
                     (25 * 60, "your ceiling wakes"),
                     (0, "above zero"),
                     (12.5, "whole number")
                 })
        {
            var refused = await server.CallAsync(WakeFrame(minutes), Head, Launch(db, Head));
            Assert.False(refused.Ok);
            Assert.Contains(words, refused.Error!.Message, StringComparison.Ordinal);
        }
        var unconfined = await server.CallAsync(WakeFrame(90), Member, Launch(db, Member, SeatClasses.Cli));
        Assert.False(unconfined.Ok);
        Assert.Contains("Heading a unit grants no verb by itself", unconfined.Error!.Message, StringComparison.Ordinal);
        Assert.Empty(new MissionEventStore(db).OfKind(MissionEventKind.Self));
    }

    /// <summary>
    /// (1c) THE FLOOR AND THE CEILING ARE THE POLICY'S, NEVER A CONSTANT: under a policy whose team floor is 10 minutes and whose ceiling
    /// is 2 hours, 15 minutes is asked (the shipped floor would refuse it) and 3 hours is refused; a level the policy holds no ceiling
    /// for asks for nothing. The floor is BETWEEN wakes: anchored at the last woken turn's start, not at now. RED at base: no request.
    /// </summary>
    [Fact]
    public void The_floor_and_ceiling_a_wake_is_held_to_are_the_policy_in_force()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var now = DateTimeOffset.UtcNow;
        var ceilings = OrgLevel.All.ToDictionary(l => l, _ => TimeSpan.FromHours(2), StringComparer.Ordinal);
        var policy = RewakeFloors.From(new Dictionary<string, int> { [OrgLevel.Team] = 10 }, ceilings);

        var asked = org.RequestWake(new WakeRequest(Head, "turn-a", TimeSpan.FromMinutes(15)), policy, null, now);
        Assert.Equal(now.AddMinutes(15), asked.DueAt);
        Assert.Equal((TimeSpan.FromMinutes(10), TimeSpan.FromHours(2)), (asked.Floor, asked.Ceiling));
        Assert.Contains("ceiling wakes",
            Assert.Throws<TradeAgentException>(() => org.RequestWake(new WakeRequest(Member, "turn-b", TimeSpan.FromHours(3)), policy, null, now)).Message,
            StringComparison.Ordinal);

        var none = RewakeFloors.From(null, null);
        Assert.Contains("holds no ceiling",
            Assert.Throws<TradeAgentException>(() => org.RequestWake(new WakeRequest(Member, "turn-c", TimeSpan.FromMinutes(30)), none, null, now)).Message,
            StringComparison.Ordinal);

        // BETWEEN WAKES: the member's last woken turn began 20 minutes ago, so under the executor's shipped 15-minute floor a request
        // for 1 minute is asked (due 21 minutes after that turn), and under a 30-minute floor it is refused.
        var shipped = RewakeFloors.From(null, ceilings);
        new MissionEventStore(db).Raise("review:anchor", MissionEventKind.Review, now.AddMinutes(-21), role: Member);
        var anchor = new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = "turn-anchor", StartedAt = now.AddMinutes(-20), Role = Member, ReservedCost = 0.1m, SeatClass = SeatClasses.Cell,
            Assurance = SeatClasses.Name(SeatClasses.AssuranceOf(SeatClasses.Cell)!.Value)
        }, ["review:anchor"]);
        Assert.True(anchor.Admitted, anchor.Refusal);
        var strict = RewakeFloors.From(new Dictionary<string, int> { [OrgLevel.Executor] = 30 }, ceilings);
        Assert.Contains("floor between wakes",
            Assert.Throws<TradeAgentException>(() => org.RequestWake(new WakeRequest(Member, "turn-d", TimeSpan.FromMinutes(1)), strict, null, now)).Message,
            StringComparison.Ordinal);
        Assert.Equal(now.AddMinutes(1), org.RequestWake(new WakeRequest(Member, "turn-e", TimeSpan.FromMinutes(1)), shipped, null, now).DueAt);
    }

    /// <summary>
    /// (1d) A WAKE THAT DOES NOT FIT THE AI DAY IS REFUSED: one more turn of the caller's seat (0.10 reserved) on a chain with 9.95 of its
    /// 10 committed is refused naming the unit; a host that meters nothing refuses it too, never guessed. Nothing written. RED at base:
    /// no <c>org-wake</c> op.
    /// </summary>
    [Fact]
    public async Task A_wake_that_does_not_fit_the_AI_day_is_refused()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;

        gw.AiEnvelopes = Metered(db, envelope: 10m, spent: 9.95m);
        var full = await server.CallAsync(WakeFrame(90), Head, Launch(db, Head));
        Assert.False(full.Ok);
        Assert.Contains("does not fit its AI day", full.Error!.Message, StringComparison.Ordinal);

        gw.AiEnvelopes = null;
        var unread = await server.CallAsync(WakeFrame(90), Head, Launch(db, Head));
        Assert.False(unread.Ok);
        Assert.Contains("could not read your unit's AI envelope", unread.Error!.Message, StringComparison.Ordinal);
        Assert.Empty(new MissionEventStore(db).OfKind(MissionEventKind.Self));
    }
}

using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE ROOT GETS A HEAD (<c>U-org-chief</c>; R24 §§ 4.1, 4.3, Q12; ORCHESTRATION §§ 2.1, 2.3, 2.9): the chief is appointed and replaced by
/// the owner's in-process press only while no board seat is active, and after that only by a two-seat board act on <c>U-board-b1</c>'s
/// machinery; the owner removes a chief at any time and restores no one; the chief charters divisions and receives escalations without
/// ever reaching <c>operations</c>, the board or an order. NOTHING HERE REACHES A MODEL: a board seat is made active only through b1's own
/// seam (<c>BoardStore.SetSeatStateUnderTest</c>), every chief is a puppet or a seat that never launches, and every act is a call the test makes.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public partial class OrgChiefTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    /// <summary>The runtimes this test's host knows — a CLI and the test host's puppet. Anything else is unknown.</summary>
    static bool Known(string runtime) => runtime is "codex" or "puppet";

    static readonly ChiefSeat Codex = new("codex", "gpt-6"), Puppet = new("puppet", "scripted-1");

    static int Exec(Database db, string sql, params (string, object?)[] ps) => db.Write(_ =>
    {
        using var c = db.Cmd(sql, ps);
        return c.ExecuteNonQuery();
    });

    static long Count(Database db, string table, string where = "1") => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT COUNT(*) FROM {table} WHERE {where}");
        return (long)c.ExecuteScalar()!;
    });

    /// <summary>Every row a change of the chief, an act or a refusal could touch, counted — a refusal leaves each where it was.</summary>
    static long[] Written(Database db) =>
        [Count(db, "org_unit"), Count(db, "org_position"), Count(db, "org_event"), Count(db, "org_mandate_event"),
         Count(db, "board_policy"), Count(db, "board_act"), Count(db, "board_act_event"),
         db.Read(_ => { using var c = db.Cmd("SELECT COUNT(*) FROM org_position WHERE status='ended'"); return (long)c.ExecuteScalar()!; })];

    static string? RootHead(Database db) => new OrgStore(db).Units().Single(u => u.IsRoot).HeadPositionId;

    /// <summary>A launch the ledger recorded for <paramref name="role"/>, funded by <paramref name="funding"/> — the way the ledger writes one. Never a model.</summary>
    static string Funded(Database db, string role, string seatClass, params string[] funding)
    {
        var id = $"turn-{Guid.NewGuid():n}"[..17];
        var admission = new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = id, StartedAt = DateTimeOffset.UtcNow, Role = role, ReservedCost = 0.10m, SeatClass = seatClass,
            Assurance = SeatClasses.AssuranceOf(seatClass) is { } a ? SeatClasses.Name(a) : null, Funding = funding
        });
        Assert.True(admission.Admitted, admission.Refusal);
        return id;
    }

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

    static ChiefChanged Seat(OrgStore org, string? from, ChiefSeat? to, string decider = OrgDecider.Owner, string basis = "press", string? act = null) =>
        org.SeatChief(from, to, decider, basis, act, TurnAllowance.Default, Known, Now);

    // ---- item 1: the one chart writer ------------------------------------------------------------------------------------------------

    /// <summary>
    /// (1) REPLACING THE CHIEF FENCES THE OLD ONE AND HIRES THE NEW ONE DORMANT, IN ONE WRITE: an appointment over a vacant root mints a
    /// position under the root, DORMANT, seated on its runtime and model at the allowance given, the root's head set, and one
    /// <c>chief-appointed</c> event decided by its decider. A replacement in ONE write ends the sitting chief through the restructuring's
    /// transaction — the mandate it decided cancelled with the app's reason, its running launch fenced, every past charge on the root's
    /// chain exactly as it was — and seats the new one dormant, one <c>chief-replaced</c> event naming both and the act. A vacancy ends the
    /// chief and seats no one. A change whose <c>from</c> is not the chief sitting now is refused, nothing written; and when the event
    /// cannot be written, nothing of the replacement is. RED at base: no <c>OrgStore.SeatChief</c>.
    /// </summary>
    [Fact]
    public void Replacing_the_chief_fences_the_old_one_and_hires_the_new_one_dormant_in_one_write()
    {
        using var db = TestEnv.NewDb();
        var org = new OrgStore(db);

        var appointed = Seat(org, null, Puppet);
        var first = appointed.Chief!;
        Assert.Equal(OrgEventKind.ChiefAppointed, appointed.Kind);
        Assert.StartsWith(OrgIds.PositionPrefix, first, StringComparison.Ordinal);
        Assert.Equal(first, RootHead(db));
        var row = org.Position(first)!;
        Assert.Equal((OrgIds.Root, OrgPositionStatus.Dormant, "puppet", "scripted-1", "1200000", "20000", $"org/{first}"),
            (row.UnitId, row.Status, row.SeatRuntime, row.SeatModel, row.SeatAllowIn, row.SeatAllowOut, row.HomeDir));
        Assert.Equal(1, Count(db, "org_event", $"kind='{OrgEventKind.ChiefAppointed}' AND position_id='{first}' AND decider='owner'"));
        Assert.Equal(first, org.Chief());

        // THE SITTING CHIEF AT WORK: active, a mandate it decided, a launch the root's chain funded and one still running.
        Assert.Equal(OrgPositionStatus.Active, org.Activate(first, _ => SeatClasses.Puppet, 3, Now).Status);
        var mandate = org.Assign(new MandateIssue(first, null, CouncilRoles.Research, MandateKind.Task,
            "find out whether the funding skew predicts the next hour", null, MandateShape.Report, false, Now.AddDays(7), 5m, 2), Now);
        var spent = Funded(db, first, SeatClasses.Harness, OrgIds.Root);
        Assert.True(new AiAttemptStore(db).End(spent, 0, Now, 10, 0, 5, 5, 0, null, 0.04m, null, null));
        var running = Funded(db, first, SeatClasses.Harness, OrgIds.Root);
        var charged = ChargedTo(db, OrgIds.Root);

        // A STALE `from` IS REFUSED, NOTHING WRITTEN — and so is a replacement whose event cannot be written.
        var before = Written(db);
        Assert.Contains("is the chief now", Assert.Throws<TradeAgentException>(() => Seat(org, null, Codex)).Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was written", Assert.Throws<TradeAgentException>(() => Seat(org, "pos-000000000000", Codex)).Message,
            StringComparison.Ordinal);
        Exec(db, "CREATE TRIGGER abort_chief BEFORE INSERT ON org_event WHEN NEW.kind = 'chief-replaced' BEGIN SELECT RAISE(ABORT, 'no event'); END");
        Assert.ThrowsAny<Exception>(() => Seat(org, first, Codex, OrgDecider.Board, "act", "bact-0123456789ab"));
        Exec(db, "DROP TRIGGER abort_chief");
        Assert.Equal(before, Written(db));
        Assert.Equal(first, RootHead(db));

        var replaced = Seat(org, first, Codex, OrgDecider.Board, "act", "bact-0123456789ab");
        var second = replaced.Chief!;
        Assert.Equal((OrgEventKind.ChiefReplaced, first), (replaced.Kind, replaced.Was));
        Assert.NotEqual(first, second);
        Assert.Equal(second, RootHead(db));
        Assert.Equal(OrgPositionStatus.Ended, org.Position(first)!.Status);
        Assert.Equal((OrgPositionStatus.Dormant, "codex", "gpt-6"), (org.Position(second)!.Status, org.Position(second)!.SeatRuntime, org.Position(second)!.SeatModel));
        Assert.Equal(MandateEventKind.Cancelled, org.MandateStatus(mandate.Id));
        Assert.Contains(first, org.MandateEvents(mandate.Id)[^1].Reason, StringComparison.Ordinal);
        Assert.Equal([mandate.Id], replaced.Cancelled);
        Assert.Equal(OrgEventKind.Fenced, org.FencedSince(running)?.Cause);
        Assert.Equal(charged, ChargedTo(db, OrgIds.Root));
        var detail = db.Read(_ =>
        {
            using var c = db.Cmd("SELECT decider || ' ' || detail FROM org_event WHERE kind = 'chief-replaced'");
            return (string)c.ExecuteScalar()!;
        });
        log.WriteLine(detail);
        Assert.StartsWith("board ", detail, StringComparison.Ordinal);
        Assert.Contains($"chief={second} was={first}", detail, StringComparison.Ordinal);
        Assert.Contains("act=bact-0123456789ab", detail, StringComparison.Ordinal);

        var vacated = Seat(org, second, null, OrgDecider.Owner, "veto", "bact-0123456789ab");
        Assert.Equal((OrgEventKind.ChiefVacated, (string?)null, second), (vacated.Kind, vacated.Chief, vacated.Was));
        Assert.Null(RootHead(db));
        Assert.Equal(OrgPositionStatus.Ended, org.Position(second)!.Status);
        Assert.Contains("no chief to remove", Assert.Throws<TradeAgentException>(() => Seat(org, null, null)).Message, StringComparison.Ordinal);
        Assert.Contains("not a runtime this build knows",
            Assert.Throws<TradeAgentException>(() => Seat(org, null, new ChiefSeat("a-runtime-nobody-knows", "m"))).Message, StringComparison.Ordinal);
    }
}

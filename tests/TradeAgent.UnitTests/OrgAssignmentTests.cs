using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE MANDATE'S LIFE (<c>U-org-assignments-a</c>, O5a; ORCHESTRATION §§ 2.2–2.4, 2.9, 2.10): a head assigns a task or an
/// exploration into its own subtree, the holder's launches are charged to it, its delivery is sealed and checked by the app,
/// code triages, the head accepts or rejects. NOTHING HERE REACHES A REAL MODEL (R1): every caller is an in-process
/// <see cref="GatewayPipeServer"/> call under a launch id the TEST seeded in the ledger with the seat assurance it names — the
/// <c>OrgRightsTests</c> pattern — and every position beyond the legacy pair is written by the test with raw SQL, as no writer
/// of the chart exists yet.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class OrgAssignmentTests
{
    /// <summary>The head of a team under Research's division, and two members of that team.</summary>
    const string Head = "pos-a1c0ffee0001", Member = "pos-a1c0ffee0002", Other = "pos-a1c0ffee0003";

    /// <summary>A second team, outside the head's subtree, and its member.</summary>
    const string Outsider = "pos-b2c0ffee0001";

    const string Team = "team-a1c0ffee", Elsewhere = "team-b2c0ffee";

    const string Stamp = "2026-10-11T12:00:00.0000000Z";

    static readonly DateTimeOffset Due = DateTimeOffset.UtcNow.AddDays(7);

    // ---- the chart, as only a test may write it --------------------------------------------------------------------

    static void Exec(Database db, string sql, params (string, object?)[] ps) => db.Write(_ =>
    {
        using var c = db.Cmd(sql, ps);
        return c.ExecuteNonQuery();
    });

    /// <summary>A team under <paramref name="parent"/>, headed by a new active position.</summary>
    static void HeadATeam(Database db, string position, string team, string parent = OrgIds.Research)
    {
        Exec(db, "INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES($t,$p,'team',NULL,'active',$at)",
            ("$t", team), ("$p", parent), ("$at", Stamp));
        Seat(db, position, team);
        Exec(db, "UPDATE org_unit SET head_position_id=$id WHERE id=$t", ("$id", position), ("$t", team));
    }

    /// <summary>A position in <paramref name="unit"/>, in the status given.</summary>
    static void Seat(Database db, string id, string unit, string status = OrgPositionStatus.Active) =>
        Exec(db, "INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES($id,$u,$h,$s,$at)",
            ("$id", id), ("$u", unit), ("$h", $"org/{id}"), ("$s", status), ("$at", Stamp));

    /// <summary>The chart every test starts from: <see cref="Head"/> heads <see cref="Team"/> with two members; an outsider elsewhere.</summary>
    static void Chart(Database db)
    {
        HeadATeam(db, Head, Team);
        Seat(db, Member, Team);
        Seat(db, Other, Team);
        HeadATeam(db, Outsider, Elsewhere);
    }

    /// <summary>
    /// A LAUNCH THE TEST RECORDED, as the ledger would: <paramref name="role"/>'s row, LAUNCHED, on a seat of
    /// <paramref name="seatClass"/> with its assurance — never a real model.
    /// </summary>
    static string Launch(Database db, string role, string seatClass = SeatClasses.Harness, decimal reserved = 0.10m,
        string? mandate = null, IReadOnlyList<string>? consuming = null)
    {
        var id = $"turn-{Guid.NewGuid():n}"[..17];
        var admission = new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = id, StartedAt = DateTimeOffset.UtcNow, Role = role, ReservedCost = reserved, SeatClass = seatClass,
            Assurance = SeatClasses.AssuranceOf(seatClass) is { } a ? SeatClasses.Name(a) : null, MandateId = mandate
        }, consuming);
        Assert.True(admission.Admitted, admission.Refusal);
        return id;
    }

    static IpcRequest Frame(string op, params (string Key, object Value)[] args) => new()
    {
        Op = op,
        Session = "mandates",
        RequestId = "mandates-" + Guid.NewGuid().ToString("n")[..12],
        Args = args.ToDictionary(a => a.Key, a => JsonSerializer.SerializeToElement(a.Value))
    };

    static JsonElement Body(IpcResponse r) => JsonDocument.Parse(Json.Write(r.Data)).RootElement;

    static long Count(Database db, string table, string where = "1") => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT COUNT(*) FROM {table} WHERE {where}");
        return (long)c.ExecuteScalar()!;
    });

    static MandateIssue Issue(string holder = Member, string kind = MandateKind.Task, string? shape = MandateShape.Report,
        bool exact = false, decimal allocation = 5m, int retries = MandateLimits.DefaultRetryLimit) =>
        new(Head, null, holder, kind, "find out whether the funding skew predicts the next hour", null,
            kind == MandateKind.Task ? shape : null, exact, Due, allocation, retries);

    // ---- item 1: the records ---------------------------------------------------------------------------------------

    /// <summary>
    /// THE MANDATE'S RECORDS ARE APPEND-ONLY AND ITS STATUS IS ITS LATEST EVENT (rung 40): an issue writes the row, its
    /// <c>issued</c> event and ONE <c>mandate</c> wake to the holder in one write; no UPDATE or DELETE reaches a mandate, its
    /// events, a delivery or a check; a cancellation is an event, never an edit, and closes the holder's untaken wake.
    /// </summary>
    [Fact]
    public void The_mandate_records_are_append_only_and_a_status_is_its_latest_event()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var now = DateTimeOffset.UtcNow;

        var m = org.Assign(Issue(), now);
        Assert.StartsWith(OrgStore.MandatePrefix, m.Id, StringComparison.Ordinal);
        Assert.Equal(Team, m.UnitId);
        Assert.Equal(MandateEventKind.Issued, org.MandateStatus(m.Id));
        Assert.Equal(MandateCheck.All, m.Checks);

        var wake = Assert.Single(new MissionEventStore(db).OfKind(MissionEventKind.Mandate));
        Assert.Equal(Member, wake.Role);
        Assert.Equal(m.Id, wake.Payload);

        foreach (var sql in new[]
                 {
                     "UPDATE org_mandate SET objective='x'", "DELETE FROM org_mandate",
                     "UPDATE org_mandate_event SET kind='accepted'", "DELETE FROM org_mandate_event"
                 })
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Exec(db, sql));
        Assert.Equal("find out whether the funding skew predicts the next hour", org.Mandate(m.Id)!.Objective);

        org.Cancel(m.Id, Head, null, "the question is answered elsewhere", now.AddMinutes(1));
        Assert.Equal(MandateEventKind.Cancelled, org.MandateStatus(m.Id));
        Assert.False(org.IsOpen(m.Id));
        Assert.Equal([MandateEventKind.Issued, MandateEventKind.Cancelled], org.MandateEvents(m.Id).Select(e => e.Kind));
        var closed = new MissionEventStore(db).Get(wake.Id)!;
        Assert.True(closed.Consumed);
        Assert.Equal(MissionEventDisposition.Withdrawn, closed.Disposition);

        // ONLY THE DECIDER CANCELS, and only an open mandate.
        var second = org.Assign(Issue(), now);
        Assert.Contains("only its decider", Assert.Throws<TradeAgentException>(() => org.Cancel(second.Id, Member, null, "no", now)).Message,
            StringComparison.Ordinal);
        Assert.Throws<TradeAgentException>(() => org.Cancel(m.Id, Head, null, "again", now));

        Assert.Equal(1L, Count(db, "pragma_table_info('ai_attempt')", "name='mandate_id'"));
        Assert.Equal(Versions.DatabaseSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            db.Read(_ => { using var c = db.Cmd("SELECT value FROM meta WHERE key='schema_version'"); return c.ExecuteScalar() as string; }));
    }
}

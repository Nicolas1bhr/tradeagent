using System.Globalization;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A HEAD CHARTERS A UNIT AND HIRES A POSITION INTO ITS SUBTREE (<c>U-org-verbs-c</c>, items 3 and 4; ORCHESTRATION § 2.9; ORGANISATION
/// §§ 2, 3, 5): each one write, each a mandatory decision record with its rationale in the ledger, inside the rights matrix, the chart's
/// bounds, the weekly throttle and the earning test — or refused in words with nothing written. Store-level: the caller gate (recorded
/// seat assurance) is the pipe's, exercised in <see cref="OrgRightsConformanceTests"/>. Every position the chart starts with beyond the
/// legacy pair is written by the TEST with raw SQL. No model.
/// </summary>
public partial class OrgStructureTests
{
    /// <summary>
    /// The chart: <see cref="Head"/> heads the division <see cref="Div"/> with one member <see cref="Member"/>; <see cref="SubHead"/>
    /// heads a subdivision <see cref="Sub"/> under it; <see cref="OpsHead"/> heads a team under <c>div-operations</c>.
    /// </summary>
    const string Head = "pos-d1c0ffee0001", Member = "pos-d1c0ffee0002", SubHead = "pos-d1c0ffee0003", OpsHead = "pos-0bc0ffee0001",
        Chief = "pos-c4c0ffee0001";

    const string Div = "div-d1c0ffee", Sub = "sub-d1c0ffee", OpsTeam = "team-0bc0ffee";

    const string Stamp = "2026-10-11T12:00:00.0000000Z";

    static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    static readonly CharterFunding Metered = new(SeatClasses.Harness, 0.10m, [new(BillingClasses.Plan, 10m)]);

    static void Exec(Database db, string sql, params (string, object?)[] ps) => db.Write(_ =>
    {
        using var c = db.Cmd(sql, ps);
        return c.ExecuteNonQuery();
    });

    static long Count(Database db, string table, string where = "1") => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT COUNT(*) FROM {table} WHERE {where}");
        return (long)c.ExecuteScalar()!;
    });

    static void HeadAUnit(Database db, string position, string unit, string parent, string kind)
    {
        Exec(db, "INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES($t,$p,$k,NULL,'active',$at)",
            ("$t", unit), ("$p", parent), ("$k", kind), ("$at", Stamp));
        Seat(db, position, unit);
        Exec(db, "UPDATE org_unit SET head_position_id=$id WHERE id=$t", ("$id", position), ("$t", unit));
    }

    static void Seat(Database db, string id, string unit, string status = OrgPositionStatus.Active) =>
        Exec(db, "INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES($id,$u,$h,$s,$at)",
            ("$id", id), ("$u", unit), ("$h", $"org/{id}"), ("$s", status), ("$at", Stamp));

    static Database Chart()
    {
        var db = TestEnv.NewDb();
        HeadAUnit(db, Head, Div, OrgIds.Root, OrgUnitKind.Division);
        Seat(db, Member, Div);
        HeadAUnit(db, SubHead, Sub, Div, OrgUnitKind.Subdivision);
        HeadAUnit(db, OpsHead, OpsTeam, OrgIds.Operations, OrgUnitKind.Team);
        return db;
    }

    /// <summary>A launch of <paramref name="role"/> the ledger recorded, funded by <paramref name="funding"/>, nearest first.</summary>
    static string Funded(Database db, string role, params string[] funding)
    {
        var id = $"turn-{Guid.NewGuid():n}"[..17];
        var admission = new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = id, StartedAt = DateTimeOffset.UtcNow, Role = role, ReservedCost = 0.10m, SeatClass = SeatClasses.Harness,
            Assurance = SeatClasses.Name(SeatAssurance.Brokered), Funding = funding
        });
        Assert.True(admission.Admitted, admission.Refusal);
        return id;
    }

    /// <summary>A run by <paramref name="attempt"/>, recorded at <paramref name="at"/>, as only a test may write it (its references off).</summary>
    static string Run(Database db, string attempt, DateTimeOffset at)
    {
        var id = $"run-{Guid.NewGuid():n}"[..16];
        db.Exec("PRAGMA foreign_keys=OFF");
        Exec(db, """
            INSERT INTO strategy_run(id, version_id, dataset_id, dataset_sha256, execution_model, outcome, bars, trades, wins, intents, fills,
                                     exposure_bars, missing_minutes, faults, trace_sha256, created_at, role, attempt)
            VALUES($id, 'ver-x', 1, 's', 'm', 'completed', 1, 0, 0, 0, 0, 0, 0, 0, 't', $at, NULL, $attempt)
            """, ("$id", id), ("$at", at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)), ("$attempt", attempt));
        db.Exec("PRAGMA foreign_keys=ON");
        return id;
    }

    /// <summary>Current work of <see cref="Head"/>'s subtree: a run its member ran under a launch the division funded, an hour ago.</summary>
    static OrgRef CurrentRun(Database db) => new(OrgRefKind.Run, Run(db, Funded(db, Member, Div, OrgIds.Root), Now.AddHours(-1)));

    static ForecastClaim Admissible(DateTimeOffset? by = null, string? subject = null) =>
        new(ForecastVocabulary.UnitAdmissible, subject, ForecastVocabulary.Admitted, by ?? Now.AddDays(45), 0.4m);

    static CharterRequest Ask(string caller = Head, string kind = OrgUnitKind.Team, decimal share = 0.2m, IReadOnlyList<OrgRef>? refs = null,
        IReadOnlyList<ForecastClaim>? forecasts = null, DateTimeOffset? collapse = null) =>
        new(caller, null, kind, share, "the funding skew has a family worth its own team", ["a team", "a mandate to the member"],
            refs ?? [], forecasts ?? [Admissible()], collapse);

    /// <summary>Every row a charter or a hire writes, counted: a refusal leaves each count where it was.</summary>
    static long[] Written(Database db) =>
        [Count(db, "org_unit"), Count(db, "org_position"), Count(db, "org_event"), Count(db, "org_decision"), Count(db, "org_forecast"),
         Count(db, "ledger_entry")];

    // ---- item 3: org-charter ----------------------------------------------------------------------------------------

    /// <summary>
    /// (3a) A CHARTER WRITES THE UNIT, ITS DECISION, ITS LEDGER ENTRY, ITS FORECAST AND ITS EVENT IN ONE WRITE: the division's head
    /// charters a team under it citing a run its subtree funded — the unit (kind team, head VACANT, active, the share as its envelope,
    /// its charter decision named), the <c>charter</c> decision with the rationale as the head's own ledger entry, a v2
    /// <c>unit-admissible</c> forecast whose subject is the new unit, and one <c>chartered</c> event decided by the head naming the
    /// evidence. When the event cannot be written, NOTHING of the charter is. RED at base: no <c>OrgStore.Charter</c>.
    /// </summary>
    [Fact]
    public void A_charter_writes_unit_decision_ledger_entry_and_event_in_one_write()
    {
        using var db = Chart();
        var org = new OrgStore(db);
        var run = CurrentRun(db);

        Exec(db, "CREATE TRIGGER abort_event BEFORE INSERT ON org_event WHEN NEW.kind = 'chartered' BEGIN SELECT RAISE(ABORT, 'no event'); END");
        var before = Written(db);
        Assert.ThrowsAny<Exception>(() => org.Charter(Ask(refs: [run]), Metered, Now));
        Assert.Equal(before, Written(db));
        Exec(db, "DROP TRIGGER abort_event");

        var made = org.Charter(Ask(refs: [run]), Metered, Now);
        var unit = made.Unit;
        Assert.StartsWith(OrgStore.UnitPrefix, unit.Id, StringComparison.Ordinal);
        Assert.Equal((Div, OrgUnitKind.Team, (string?)null, OrgUnitStatus.Active, "0.2", made.Decision.Id),
            (unit.ParentId, unit.Kind, unit.HeadPositionId, unit.Status, unit.EnvelopeShare, unit.CharterDecisionId));

        Assert.Equal((Head, OrgDecisionKind.Charter), (made.Decision.Position, made.Decision.Kind));
        Assert.Equal([run], made.Decision.Refs);
        var entry = new ResearchLedger(db).Entry(made.Decision.LedgerEntryId)!;
        Assert.Equal((LedgerKind.Decision, Head), (entry.Kind, entry.Author));

        var f = Assert.Single(made.Forecasts);
        Assert.Equal((ForecastVocabulary.UnitAdmissible, unit.Id, ForecastVocabulary.Version, true), (f.Predicate, f.Subject, f.Vocabulary, f.Scored));

        var e = new OrgStore(db).Units().Single(u => u.Id == unit.Id);
        Assert.Equal(unit, e);
        var detail = db.Read(_ =>
        {
            using var c = db.Cmd("SELECT kind, decider, detail FROM org_event WHERE id=$id", ("$id", made.EventId));
            using var r = c.ExecuteReader();
            Assert.True(r.Read());
            return (r.GetString(0), r.GetString(1), r.GetString(2));
        });
        Assert.Equal((OrgEventKind.Chartered, Head), (detail.Item1, detail.Item2));
        Assert.Contains($"decision={made.Decision.Id}", detail.Item3, StringComparison.Ordinal);
        Assert.Contains($"evidence run:{run.Id}", detail.Item3, StringComparison.Ordinal);

        // THE DECISION IS MANDATORY: decision-record still refuses a charter kind in words.
        Assert.Contains("written by the verb that performs it", Assert.Throws<TradeAgentException>(() =>
            org.Decide(Head, null, OrgDecisionKind.Charter, ["a"], "a", "b", [], [], Now)).Message, StringComparison.Ordinal);
    }

    /// <summary>One refusal of a charter: its name, how the chart is set up for it, the request, and the words it is refused in.</summary>
    public static TheoryData<string> CharterRefusals => new()
    {
        "a member is not the parent's head", "a unit under div-operations is a board act", "a closed parent", "a kind the parent cannot hold",
        "a fourth manager layer", "a seventh division", "a share that is no fraction", "siblings past the whole", "a share that cannot fund a turn",
        "no meter", "a third structural change in seven days", "nothing current cited", "a stale run", "a run funded elsewhere",
        "an experiment too large", "an experiment that never collapses", "no unit-admissible forecast"
    };

    /// <summary>
    /// (3b) EACH REFUSAL OF A CHARTER, IN WORDS, WITH NOTHING WRITTEN: the rights matrix (a member; anything under
    /// <c>div-operations</c>, a two-seat board act no board seat can make yet), a closed parent, a kind the parent cannot hold, a fourth
    /// manager layer (the earning test's, never a depth constant), a seventh division (under a chief the test seats), a share that is no
    /// fraction or would split more than the whole or cannot fund one turn of the seat (<see cref="OrgStore.Unfundable"/>) — or no meter
    /// to size it — a third structural change in seven days (the app's and the owner's not counted), the earning test (nothing current
    /// cited, a run older than 30 days, a run another subtree funded; an experiment over a quarter or with no collapse within 90 days),
    /// and a charter with no <c>unit-admissible</c> forecast. RED at base: no <c>OrgStore.Charter</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(CharterRefusals))]
    public void Each_refusal_of_a_charter_is_in_words_and_writes_nothing(string refusal)
    {
        using var db = Chart();
        var org = new OrgStore(db);
        var run = CurrentRun(db);
        CharterRequest ask = Ask(refs: [run]);
        CharterFunding? funding = Metered;
        string words;
        switch (refusal)
        {
            case "a member is not the parent's head":
                ask = Ask(caller: Member, refs: [run]); words = "only the parent unit's head"; break;
            case "a unit under div-operations is a board act":
                ask = Ask(caller: OpsHead, refs: [run]); words = "a two-seat board act"; break;
            case "a closed parent":
                Exec(db, "UPDATE org_unit SET status='closed' WHERE id=$d", ("$d", Div)); words = "is closed"; break;
            case "a kind the parent cannot hold":
                ask = Ask(kind: OrgUnitKind.Division, refs: [run]); words = "does not hang from a division"; break;
            case "a fourth manager layer":
                ask = Ask(caller: SubHead, refs: [run]); words = "manager layer 4"; break;
            case "a seventh division":
                Seat(db, Chief, OrgIds.Root);
                Exec(db, "UPDATE org_unit SET head_position_id=$c WHERE id='org'", ("$c", Chief));
                for (var i = 0; i < 3; i++)
                    Exec(db, "INSERT INTO org_unit(id, parent_id, kind, status, created_at) VALUES($t,'org','division','active',$at)",
                        ("$t", $"div-extra{i}"), ("$at", Stamp));
                ask = Ask(caller: Chief, kind: OrgUnitKind.Division, collapse: Now.AddDays(60)); words = "6 open divisions"; break;
            case "a share that is no fraction":
                ask = Ask(share: 1.5m, refs: [run]); words = "is no AI share"; break;
            case "siblings past the whole":
                Exec(db, "UPDATE org_unit SET envelope_share='0.9' WHERE id=$s", ("$s", Sub)); words = "more than the whole"; break;
            case "a share that cannot fund a turn":
                funding = Metered with { Parent = [new(BillingClasses.Plan, 0.4m)] }; words = "plan"; break;
            case "no meter":
                funding = null; words = "meters no AI envelope"; break;
            case "a third structural change in seven days":
                foreach (var decider in new[] { Head, SubHead, OrgDecider.App, OrgDecider.Owner })
                    Exec(db, "INSERT INTO org_event(at, unit_id, kind, decider, detail) VALUES($at, $u, 'chartered', $d, 'x')",
                        ("$at", Now.AddDays(-3).UtcDateTime.ToString("O", CultureInfo.InvariantCulture)), ("$u", Div), ("$d", decider));
                words = "2 structural changes in the last seven days"; break;
            case "nothing current cited":
                ask = Ask(refs: []); words = "the earning test"; break;
            case "a stale run":
                ask = Ask(refs: [new(OrgRefKind.Run, Run(db, Funded(db, Member, Div, OrgIds.Root), Now.AddDays(-40)))]); words = "the earning test"; break;
            case "a run funded elsewhere":
                ask = Ask(refs: [new(OrgRefKind.Run, Run(db, Funded(db, CouncilRoles.Research, OrgIds.Research, OrgIds.Root), Now.AddHours(-1)))]);
                words = "the earning test"; break;
            case "an experiment too large":
                ask = Ask(share: 0.3m, collapse: Now.AddDays(60)); words = "at most 0.25"; break;
            case "an experiment that never collapses":
                ask = Ask(collapse: Now.AddDays(120)); words = "within 90 days"; break;
            case "no unit-admissible forecast":
                ask = Ask(refs: [run], forecasts: []); words = "at least one 'unit-admissible'"; break;
            default:
                throw new InvalidOperationException(refusal);
        }

        var before = Written(db);
        var refused = Assert.Throws<TradeAgentException>(() => org.Charter(ask, funding, Now));
        Assert.Contains(words, refused.Message, StringComparison.Ordinal);
        Assert.EndsWith("Nothing was written.", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, Written(db));
    }

    /// <summary>
    /// (3c) A FUNDED, BOUNDED EXPERIMENT PASSES THE EARNING TEST WITHOUT CITED WORK, and the weekly throttle counts only positions'
    /// structural changes: two charters by the head in one week pass (the app's and the owner's events not counted), a third is refused.
    /// </summary>
    [Fact]
    public void An_experiment_charters_without_cited_work_and_the_third_charter_of_a_week_is_refused()
    {
        using var db = Chart();
        var org = new OrgStore(db);
        foreach (var decider in new[] { OrgDecider.App, OrgDecider.Owner })
            Exec(db, "INSERT INTO org_event(at, unit_id, kind, decider, detail) VALUES($at, $u, 'chartered', $d, 'x')",
                ("$at", Now.AddDays(-1).UtcDateTime.ToString("O", CultureInfo.InvariantCulture)), ("$u", Div), ("$d", decider));

        var first = org.Charter(Ask(share: 0.2m, collapse: Now.AddDays(60)), Metered, Now);
        Assert.Contains("experiment collapse_by=", db.Read(_ =>
        {
            using var c = db.Cmd("SELECT detail FROM org_event WHERE id=$id", ("$id", first.EventId));
            return (string)c.ExecuteScalar()!;
        }), StringComparison.Ordinal);
        org.Charter(Ask(share: 0.2m, collapse: Now.AddDays(60)), Metered, Now.AddMinutes(1));
        Assert.Contains("the most it makes in a week", Assert.Throws<TradeAgentException>(() =>
            org.Charter(Ask(share: 0.2m, collapse: Now.AddDays(60)), Metered, Now.AddMinutes(2))).Message, StringComparison.Ordinal);
        // A WEEK LATER THE THROTTLE HAS ROOM AGAIN.
        org.Charter(Ask(share: 0.2m, collapse: Now.AddDays(60)), Metered, Now.AddDays(8));
    }
}

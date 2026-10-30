using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A WORKING ORGANISATION FOR THE RENDERED PAGES (<c>U-ui-pages</c>): written the way the app's ledgers would be — through the stores where a
/// store writes it, as the ledger's own rows where only a launch would — by a test, never by a real model. A chief at the root; under Research
/// a team whose head decides and two executors who work; a second team with a dormant executor; today's launches with their costs; mandates
/// open, late and delivered; a decision with forecasts; an escalation that reached the owner and words he is owed.
/// </summary>
static class OwnerPagesSeed
{
    public const string Head = "pos-a1c0ffee0001", Executor1 = "pos-a1c0ffee0002", Executor2 = "pos-a1c0ffee0003";
    public const string OtherHead = "pos-b1c0ffee0001", Dormant = "pos-b1c0ffee0002";
    public const string Team = "team-a1c0ffee", OtherTeam = "team-b1c0ffee";

    static DateTimeOffset Now => DateTimeOffset.UtcNow;
    static string T(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    static void Exec(Database db, string sql, params (string, object?)[] ps) => db.Write(_ =>
    {
        using var c = db.Cmd(sql, ps);
        return c.ExecuteNonQuery();
    });

    static void Seat(Database db, string id, string unit, string status = "active") =>
        Exec(db, "INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES($id,$u,$h,$s,$at)",
            ("$id", id), ("$u", unit), ("$h", $"org/{id}"), ("$s", status), ("$at", T(Now.AddDays(-6))));

    static void HeadAUnit(Database db, string position, string unit, string parent)
    {
        Exec(db, "INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES($t,$p,'team',NULL,'active',$at)",
            ("$t", unit), ("$p", parent), ("$at", T(Now.AddDays(-6))));
        Seat(db, position, unit);
        Exec(db, "UPDATE org_unit SET head_position_id=$id WHERE id=$t", ("$id", position), ("$t", unit));
    }

    static string Launch(Database db, string role, DateTimeOffset at, string unit, string? mandate = null, decimal reserved = 0.40m)
    {
        var id = $"turn-{Guid.NewGuid():n}"[..17];
        var admission = new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = id, StartedAt = at, Role = role, ReservedCost = reserved, SeatClass = SeatClasses.Harness, BillingClass = BillingClasses.Key,
            MandateId = mandate, Runtime = "claude", RequestedModel = "claude-sonnet",
            Assurance = SeatClasses.AssuranceOf(SeatClasses.Harness) is { } a ? SeatClasses.Name(a) : null,
            Funding = [unit, OrgIds.Research, OrgIds.Root]
        });
        Assert.True(admission.Admitted, admission.Refusal);
        return id;
    }

    static void Priced(Database db, string id, DateTimeOffset at, decimal cost) =>
        Assert.True(new AiAttemptStore(db).End(id, 0, at, 18_400, 9_000, 0, 2_100, 0, "claude-sonnet", cost, null, null));

    static void Mandate(Database db, string id, string decider, string holder, string objective, DateTimeOffset deadline, decimal allocation,
        params string[] events)
    {
        Exec(db, """
            INSERT INTO org_mandate(id, unit_id, decider, holder, kind, objective, scope, checks, latitude, deadline, allocation_ai, retry_limit, created_at)
            VALUES($id,$u,$d,$h,'task',$o,NULL,'[]','exact',$by,$al,3,$at)
            """, ("$id", id), ("$u", Team), ("$d", decider), ("$h", holder), ("$o", objective), ("$by", T(deadline)),
            ("$al", allocation.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("$at", T(Now.AddDays(-2))));
        foreach (var kind in events)
            Exec(db, "INSERT INTO org_mandate_event(mandate_id, at, kind, actor, reason) VALUES($m,$at,$k,$a,NULL)",
                ("$m", id), ("$at", T(Now.AddDays(-2))), ("$k", kind), ("$a", decider));
    }

    static string Deliver(Database db, string mandate, string report, DateTimeOffset at, params (string Check, bool Passed, string Reason)[] checks)
    {
        var id = "dlv-" + Guid.NewGuid().ToString("n")[..12];
        Exec(db, "INSERT INTO org_delivery(id, mandate_id, report, deviations, refs, manifest, at) VALUES($id,$m,$r,'[]','[]','{}',$at)",
            ("$id", id), ("$m", mandate), ("$r", report), ("$at", T(at)));
        foreach (var (check, passed, reason) in checks)
            Exec(db, "INSERT INTO org_check(delivery_id, check_name, passed, reason) VALUES($d,$c,$p,$r)",
                ("$d", id), ("$c", check), ("$p", passed ? 1 : 0), ("$r", reason));
        return id;
    }

    /// <summary>The working organisation, in a database of its own at <paramref name="path"/>.</summary>
    public static Database Working(string path)
    {
        var db = new Database(path);
        var org = new OrgStore(db);

        // THE CHART: a chief at the root (appointed by the owner while no board seat is active), a team under Research with a head and two
        // executors, and a second team whose executor is dormant.
        org.AppointChief(new ChiefSeat("claude", "claude-opus"), _ => true, Now.AddDays(-5));
        HeadAUnit(db, Head, Team, OrgIds.Research);
        Seat(db, Executor1, Team);
        Seat(db, Executor2, Team);
        HeadAUnit(db, OtherHead, OtherTeam, OrgIds.Research);
        Seat(db, Dormant, OtherTeam, "dormant");

        // THE WORK: an open mandate per executor (one of them past its deadline), one delivered and waiting for the head's judgement.
        Mandate(db, "man-a1c0ffee0001", Head, Executor1, "Test whether the funding-rate skew predicts the next 4-hour move on BTCUSDT.",
            Now.AddDays(3), 2.00m, "issued");
        Mandate(db, "man-a1c0ffee0002", Head, Executor2, "Backtest the opening-range breakout on the last six months and report the costs.",
            Now.AddHours(-6), 1.50m, "issued");
        Mandate(db, "man-a1c0ffee0003", Head, Executor1, "Collect a second source for the liquidation data and compare the two.",
            Now.AddDays(1), 1.00m, "issued", "delivered");

        // TODAY'S LAUNCHES AND WHAT THEY COST — inside the owner's local day as it stands now, however soon after midnight the test runs
        // (the pages cut the day there: a launch "three hours ago" at 00:30 is yesterday's, and the Costs page rightly leaves it out).
        var day = OwnerDay.Window(Now).From;
        var span = Now - day;
        DateTimeOffset At(int sixth) => day + span * sixth / 6;
        DateTimeOffset Ended(int sixth) => At(sixth) + span / 12;
        Priced(db, Launch(db, Executor1, At(1), Team, "man-a1c0ffee0001"), Ended(1), 0.31m);
        Priced(db, Launch(db, Executor1, At(2), Team, "man-a1c0ffee0001"), Ended(2), 0.27m);
        Priced(db, Launch(db, Executor2, At(3), Team, "man-a1c0ffee0002"), Ended(3), 0.44m);
        Priced(db, Launch(db, Head, At(4), Team), Ended(4), 0.18m);
        Priced(db, Launch(db, Executor1, At(5), Team, "man-a1c0ffee0003"), Ended(5), 0.22m);

        Deliver(db, "man-a1c0ffee0003", "Both sources agree within 2% on 97% of the hours; the gaps are listed in the attached table.",
            Ended(5), ("sources-cited", true, "every source is cited"), ("output-shape", true, "the table has the columns asked for"));

        // A DECISION WITH ITS FORECAST.
        var decision = org.Decide(Head, null, "routine", ["keep testing the skew", "drop it"], "keep testing the skew",
            "the first week is thin but not negative", [], [], Now.AddHours(-4));
        Exec(db, """
            INSERT INTO org_forecast(id, decision_id, forecaster, vocabulary, predicate, subject, target, resolve_by, probability, scored, unscored_why, at)
            VALUES('fc-a1c0ffee0001', $d, $p, 1, 'verdict-class', 'v1', 'promoted', $by, '0.35', 1, NULL, $at)
            """, ("$d", decision.Id), ("$p", Head), ("$by", T(Now.AddDays(10))), ("$at", T(Now.AddHours(-4))));

        // WHAT REACHED THE OWNER: words he sent that are still owed an answer.
        var events = new MissionEventStore(db);
        Assert.True(events.RecordOwnerMessage("How is the skew test going, and what did it cost so far?", Now.AddMinutes(-15)));
        return db;
    }
}

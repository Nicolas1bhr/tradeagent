using System.Globalization;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;
using static TradeAgent.Tests.Unit.OrgSurfaceSeed;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE CUT RULES' REVIEWS (<c>U-org-scorecards-b</c>, O8 part -b; ORGANISATION § 8, "cut rules — app review events, not agent appeals"):
/// the app's review sweep raises ONE <c>review</c> event and ONE <c>cut-review</c> wake per rule, subject and period — a unit past half its
/// day envelope with nothing accepted, to the head above it; a position forecasting worse than the base rate over ten scored forecasts of
/// one manager version, to its decider — and nothing else. The chart: <see cref="Head"/> heads <see cref="Div"/> under the organisation;
/// under it <see cref="Team"/> (AI 0.4, headed by <see cref="TeamHead"/>, with <see cref="Worker"/>) and <see cref="Sibling"/> (AI 0.4,
/// head vacant, with <see cref="SiblingWorker"/>). Every record is written by the test as the ledger would, never by a real model.
/// </summary>
public class OrgReviewTests
{
    internal const string Head = "pos-4e71e0000001", TeamHead = "pos-4e71e0000002", Worker = "pos-4e71e0000003", SiblingWorker = "pos-4e71e0000004";

    internal const string Div = "div-4e71e000", Team = "team-4e71e001", Sibling = "team-4e71e002";

    /// <summary>The moment of the sweep: a weekday noon, so the owner's day is whole around it in any zone the suite runs in.</summary>
    static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    static void Unit(Database db, string id, string parent, string kind, string? head, string? ai)
    {
        Exec(db, "INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, envelope_share, created_at) VALUES($t,$p,$k,NULL,'active',$ai,$at)",
            ("$t", id), ("$p", parent), ("$k", kind), ("$ai", ai), ("$at", Stamp));
        if (head is null) return;
        Seat(db, head, id);
        Exec(db, "UPDATE org_unit SET head_position_id=$h WHERE id=$t", ("$h", head), ("$t", id));
    }

    internal static Database Chart()
    {
        var db = TestEnv.NewDb();
        Unit(db, Div, OrgIds.Root, OrgUnitKind.Division, Head, null);
        Unit(db, Team, Div, OrgUnitKind.Team, TeamHead, "0.4");
        Seat(db, Worker, Team);
        Unit(db, Sibling, Div, OrgUnitKind.Team, null, "0.4");
        Seat(db, SiblingWorker, Sibling);
        return db;
    }

    /// <summary>The meter's reading of each unit, in the key ceiling: its envelope, its PRICED spend today and how many turns of unknown cost — no turn charged its reservation unmeasured.</summary>
    internal static Func<string, IReadOnlyList<UnitDay>?> Day(params (string Unit, decimal Envelope, decimal Spent, int Unpriced)[] units) =>
        unit => [.. units.Where(u => u.Unit == unit).Select(u => new UnitDay(u.Unit, BillingClasses.Key, u.Envelope, u.Spent, 0m, u.Unpriced, 0, 0m))];

    internal static List<(long Id, string Unit, string? Position, string Decider, string Detail)> Reviews(Database db) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT id, unit_id, position_id, decider, detail FROM org_event WHERE kind = $k ORDER BY id", ("$k", OrgEventKind.Review));
        using var r = c.ExecuteReader();
        var rows = new List<(long, string, string?, string, string)>();
        while (r.Read()) rows.Add((r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetString(4)));
        return rows;
    });

    static string Day(DateTimeOffset now) => OwnerDay.Window(now).From.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// (b2) A UNIT PAST HALF ITS DAY ENVELOPE WITH NOTHING ACCEPTED RAISES ONE REVIEW, AND A RESTART NONE: the team spent 5 of its 10 today
    /// and nothing in its subtree was accepted ⇒ ONE <c>review</c> event, decided by the app, its detail the measurement, and ONE
    /// <c>cut-review</c> wake to the division's head (the head whose verbs reach the team's share), carrying the event's id. The division —
    /// whose head above is the organisation's, the owner's seat — is not reviewed; the sibling, with a mandate accepted today in its
    /// subtree, is not; a spend under half with a turn of unknown cost is said and not judged. A second sweep — a restart, a fresh store —
    /// raises nothing. RED at base: no review sweep.
    /// </summary>
    [Fact]
    public void A_unit_past_half_its_envelope_raises_one_review_and_a_restart_none()
    {
        using var db = Chart();
        Exec(db, """
            INSERT INTO org_mandate(id, unit_id, decider, holder, kind, objective, scope, checks, latitude, deadline, allocation_ai, retry_limit, created_at)
            VALUES('man-4e71e0000001', $d, $h, $w, 'research', 'find it', NULL, '[]', 'exact', '2026-12-01T00:00:00.0000000Z', '1.00', 3, $at)
            """, ("$d", Div), ("$h", Head), ("$w", SiblingWorker), ("$at", Stamp));
        Exec(db, "INSERT INTO org_mandate_event(mandate_id, at, kind, actor) VALUES('man-4e71e0000001', $at, $k, $a)",
            ("$at", Sql.T(Now.AddHours(-1))), ("$k", MandateEventKind.Accepted), ("$a", Head));
        var day = Day((Team, 10m, 5m, 0), (Sibling, 10m, 6m, 0), (Div, 25m, 20m, 0));

        var swept = new OrgStore(db).ReviewSweep(Now, ReviewThresholds.Default, day);
        Assert.Equal(1, swept.Raised);
        Assert.Empty(swept.Unjudged);
        var review = Assert.Single(Reviews(db));
        var wakeId = $"review:{ReviewRule.HalfEnvelope}:{Team}:{Day(Now)}";
        Assert.Equal((Team, (string?)null, OrgDecider.App), (review.Unit, review.Position, review.Decider));
        Assert.Equal($"rule=half-envelope unit={Team} period={Day(Now)} ceiling=key spent=5 envelope=10 share=0.5 accepted=0 reviewer={Head} wake={wakeId}",
            review.Detail);
        var wake = Assert.Single(new MissionEventStore(db).OfKind(MissionEventKind.CutReview));
        Assert.Equal((wakeId, Head, review.Id.ToString(CultureInfo.InvariantCulture)), (wake.Id, wake.Role, wake.Payload));

        // A RESTART: a fresh store, the same day — nothing more.
        Assert.Equal(0, new OrgStore(db).ReviewSweep(Now.AddMinutes(5), ReviewThresholds.Default, day).Raised);
        Assert.Single(Reviews(db));
        Assert.Single(new MissionEventStore(db).OfKind(MissionEventKind.CutReview));

        // UNDER HALF WITH A TURN OF UNKNOWN COST: said, never judged.
        var unknown = new OrgStore(db).ReviewSweep(Now, ReviewThresholds.Default, Day((Team, 10m, 5m, 0), (Div, 25m, 3m, 2)));
        Assert.Equal(0, unknown.Raised);
        Assert.Empty(unknown.Unjudged);                                               // the division has no head above it to review it
        using var other = Chart();
        var unpriced = new OrgStore(other).ReviewSweep(Now, ReviewThresholds.Default, Day((Team, 10m, 3m, 1)));
        Assert.Equal(0, unpriced.Raised);
        Assert.Contains("1 turn(s) of unknown cost", Assert.Single(unpriced.Unjudged), StringComparison.Ordinal);
        Assert.Contains("this host meters no AI envelope", Assert.Single(new OrgStore(other).ReviewSweep(Now, ReviewThresholds.Default, null).Unjudged),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// (b2) A POSITION FORECASTING WORSE THAN THE BASE RATE OVER TEN SCORED FORECASTS OF ONE MANAGER VERSION RAISES ONE REVIEW TO ITS
    /// DECIDER: the worker forecasts ten versions promoted at 0.9; each is judged refused; against a base rate of one half (nothing judged
    /// before), its skill is 0.25 − 0.81 &lt; 0 ⇒ ONE review on the worker, to the team's head; a second pass raises nothing. Nine scored
    /// forecasts raise nothing.
    /// </summary>
    [Fact]
    public void A_position_below_the_base_rate_over_ten_scored_forecasts_raises_one_review_to_its_decider()
    {
        using var db = Chart();
        var t0 = Now.AddDays(-3);
        var org = new OrgStore(db);
        for (var i = 0; i < 10; i++)
        {
            var version = $"ver-4e71e{i:00}";
            OrgScorecardTests.Version(db, version);
            org.Decide(Worker, null, OrgDecisionKind.Routine, ["forecast it"], "forecast it", "my reasons", [],
                [new ForecastClaim(ForecastVocabulary.VerdictClass, version, PromotionVerdict.Promoted, t0.AddDays(2), 0.9m)], t0);
            if (i < 9) OrgScorecardTests.Verdict(db, version, PromotionVerdict.Refused, t0.AddHours(1));
        }
        Assert.Empty(new ForecastResolver(db).Sweep(t0.AddDays(1)).Refused);
        Assert.Equal(0, org.ReviewSweep(Now, ReviewThresholds.Default, Day()).Raised);

        OrgScorecardTests.Verdict(db, "ver-4e71e09", PromotionVerdict.Refused, t0.AddHours(1));
        Assert.Empty(new ForecastResolver(db).Sweep(t0.AddDays(1)).Refused);
        Assert.Equal(1, org.ReviewSweep(Now, ReviewThresholds.Default, Day()).Raised);
        var review = Assert.Single(Reviews(db));
        Assert.Equal((Team, (string?)Worker, OrgDecider.App), (review.Unit, review.Position, review.Decider));
        Assert.StartsWith($"rule=calibration position={Worker} period=", review.Detail, StringComparison.Ordinal);
        Assert.Contains("scored=10 skill=-0.56 below=0 version=no manager version recorded reviewer=" + TeamHead, review.Detail, StringComparison.Ordinal);
        var wake = Assert.Single(new MissionEventStore(db).OfKind(MissionEventKind.CutReview));
        Assert.StartsWith($"review:{ReviewRule.Calibration}:{Worker}:", wake.Id, StringComparison.Ordinal);
        Assert.EndsWith("@10", wake.Id, StringComparison.Ordinal);
        Assert.Equal(TeamHead, wake.Role);
        Assert.Equal(0, new OrgStore(db).ReviewSweep(Now.AddDays(1), ReviewThresholds.Default, Day()).Raised);
    }

    /// <summary>
    /// (b2, fix pass 1 — the Codex review's P2; BUDGET-001, unknown never zero) A UNIT WHOSE ONLY POSITION ENDED TODAY IS STILL JUDGED ON
    /// WHAT WAS CHARGED TO IT: the sibling team's sole worker spent 3 of the team's 4 today (cap 10 × 0.4) through the real meter, with
    /// nothing accepted, and then its position ended. The host's reading of the team's day (<c>AppHost.MissionHost.UnitDayOf</c>) reads
    /// it through that ended position, so the sweep raises ONE review and ONE <c>cut-review</c> wake to the division's head. RED at base:
    /// the reading looked through active positions only, read the team as empty ("nothing can be charged to it"), and raised nothing.
    /// </summary>
    [Fact]
    public void A_unit_whose_only_position_ended_today_is_judged_on_what_was_charged_to_it()
    {
        using var db = Chart();
        var store = new AiAttemptStore(db);
        Assert.True(store.Begin(new AiAttempt
        {
            Id = "turn-4e71e0000001", StartedAt = Now.AddHours(-2), Role = SiblingWorker, ReservedCost = 0m, SeatClass = SeatClasses.Harness,
            BillingClass = BillingClasses.Key, Funding = [Sibling, Div, OrgIds.Root]
        }).Admitted);
        Assert.True(store.End("turn-4e71e0000001", 0, Now.AddHours(-1), 1000, 0, null, 100, null, "model-x", 3m, null, null));
        Exec(db, "UPDATE org_position SET status = $s, ended_at = $at WHERE id = $id",
            ("$s", OrgPositionStatus.Ended), ("$at", Sql.T(Now.AddMinutes(-30))), ("$id", SiblingWorker));
        var meter = new TurnMeter(db, () => 10m, runtimeId: () => "codex", now: () => Now, live: new LiveAttempts(),
            declaredSeat: _ => SeatClasses.Harness);
        var org = new OrgStore(db);
        IReadOnlyList<UnitDay>? Read(string unit) => TradeAgent.App.AppHost.MissionHost.UnitDayOf(org, meter, store, Now, unit);

        var swept = org.ReviewSweep(Now, ReviewThresholds.Default, Read);
        Assert.Equal(1, swept.Raised);
        var review = Assert.Single(Reviews(db));
        Assert.Equal((Sibling, OrgDecider.App), (review.Unit, review.Decider));
        Assert.True(review.Detail.Contains("ceiling=key spent=3 envelope=4.0 share=0.5 accepted=0", StringComparison.Ordinal), review.Detail);
        var wake = Assert.Single(new MissionEventStore(db).OfKind(MissionEventKind.CutReview));
        Assert.Equal(($"review:{ReviewRule.HalfEnvelope}:{Sibling}:{Day(Now)}", Head), (wake.Id, wake.Role));

        // NO POSITION BELOW A UNIT AT ALL: empty with nothing charged to it today; with a charge (a position since moved out of it), the
        // reading is unknown — said, never an empty day.
        const string bare = "team-4e71e003";
        Unit(db, bare, Div, OrgUnitKind.Team, null, "0.1");
        Assert.Empty(Read(bare)!);
        Assert.True(store.Begin(new AiAttempt
        {
            Id = "turn-4e71e0000002", StartedAt = Now.AddHours(-2), Role = Worker, ReservedCost = 0m, SeatClass = SeatClasses.Harness,
            BillingClass = BillingClasses.Key, Funding = [bare, Div, OrgIds.Root]
        }).Admitted);
        Assert.Null(Read(bare));
        Assert.Contains(org.ReviewSweep(Now, ReviewThresholds.Default, Read).Unjudged,
            u => u.Contains($"{bare}'s day could not be read from the meter", StringComparison.Ordinal));
    }

    /// <summary>
    /// (b2, fix pass 2 — the seam with <c>U-verbs-a-followups</c>; BUDGET-001, unknown never zero) A UNIT PAST HALF ITS ENVELOPE ONLY ONCE
    /// ITS RESERVATION-CHARGED TURNS ARE COUNTED IS REVIEWED: the sibling team's worker had one turn priced at 0.5 and one LOST to a
    /// restart, charged its reservation of 2 — the team is charged 2.5 of its 4 (cap 10 × 0.4) through the real meter, with nothing
    /// accepted. <see cref="UnitDay.Spent"/> is the priced part only (0.5), so the sweep reads priced + charged and raises ONE review whose
    /// detail carries both parts apart. A day under half whose turns nothing measured were charged nothing is not judged, never read as
    /// zero. RED at base: the trigger compared the priced 0.5 alone, read the team as under half, and raised nothing and said nothing.
    /// </summary>
    [Fact]
    public void A_unit_past_half_only_with_its_reservation_charged_turns_is_reviewed()
    {
        using var db = Chart();
        var store = new AiAttemptStore(db);
        AiAttempt Turn(string id, decimal reserved) => new()
        {
            Id = id, StartedAt = Now.AddHours(-2), Role = SiblingWorker, ReservedCost = reserved, SeatClass = SeatClasses.Harness,
            BillingClass = BillingClasses.Key, Funding = [Sibling, Div, OrgIds.Root]
        };
        Assert.True(store.Begin(Turn("turn-4e71e0000011", 0m)).Admitted);
        Assert.True(store.End("turn-4e71e0000011", 0, Now.AddHours(-1), 1000, 0, null, 100, null, "model-x", 0.5m, null, null));
        Assert.True(store.Begin(Turn("turn-4e71e0000012", 2m)).Admitted);
        Assert.Equal(1, store.LoseOpen(Now.AddMinutes(-90)));
        var meter = new TurnMeter(db, () => 10m, runtimeId: () => "codex", now: () => Now, live: new LiveAttempts(),
            declaredSeat: _ => SeatClasses.Harness);
        var org = new OrgStore(db);
        IReadOnlyList<UnitDay>? Read(string unit) => TradeAgent.App.AppHost.MissionHost.UnitDayOf(org, meter, store, Now, unit);
        var key = Assert.Single(Read(Sibling)!, d => d.Ceiling == BillingClasses.Key);
        Assert.Equal((0.5m, 1, 2m), (key.Spent, key.Unmeasured, key.UnmeasuredCharge));

        var swept = org.ReviewSweep(Now, ReviewThresholds.Default, Read);
        Assert.Equal(1, swept.Raised);
        var review = Assert.Single(Reviews(db));
        Assert.Equal((Sibling, OrgDecider.App), (review.Unit, review.Decider));
        Assert.True(review.Detail.Contains("ceiling=key spent=0.5 unmeasured=1 unmeasured_charge=2 envelope=4.0 share=0.5 accepted=0",
            StringComparison.Ordinal), review.Detail);

        // UNDER HALF WITH A TURN NOTHING MEASURED AND NOTHING CHARGED: its cost is unknown — said, never judged as zero.
        using var other = Chart();
        var zero = new OrgStore(other).ReviewSweep(Now, ReviewThresholds.Default,
            unit => unit == Team ? [new UnitDay(Team, BillingClasses.Key, 10m, 1m, 0m, 0, 1, 0m)] : []);
        Assert.Equal(0, zero.Raised);
        Assert.Contains("1 turn(s) of unknown cost", Assert.Single(zero.Unjudged), StringComparison.Ordinal);
    }

    static long Count(Database db, string table) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT COUNT(*) FROM {table}");
        return (long)c.ExecuteScalar()!;
    });

    static long[] Written(Database db) => [Count(db, "org_decision"), Count(db, "org_forecast"), Count(db, "ledger_entry")];

    static ForecastClaim Admissible() => new(ForecastVocabulary.UnitAdmissible, null, ForecastVocabulary.Admitted, Now.AddDays(30), 0.3m);

    static ReviewAnswerRequest Answer(string caller, long eventId, IReadOnlyList<ForecastClaim>? forecasts = null) =>
        new(caller, null, eventId, "keep the team's share a week and watch its next delivery", "the spend bought two runs that are still being judged",
            [], [], forecasts ?? [Admissible()]);

    /// <summary>
    /// (b3) A REVIEW IS ANSWERED ONLY BY A RECORD: the reviewer the app woke writes a <c>review-answer</c> decision — mandatory, so
    /// <c>decision-record</c> cannot write one — whose first ref is the review event, with a forecast on the reviewed unit, and its untaken
    /// wake is closed answered. Refused in words, nothing written: another position (the team's own head); no forecast; an event that is
    /// no review; the same review a second time. RED at base: no review answer.
    /// </summary>
    [Fact]
    public void A_review_is_answered_only_by_a_record()
    {
        using var db = Chart();
        var org = new OrgStore(db);
        Assert.Equal(1, org.ReviewSweep(Now, ReviewThresholds.Default, Day((Team, 10m, 6m, 0))).Raised);
        var review = Assert.Single(Reviews(db)).Id;
        var before = Written(db);

        Assert.Contains("not yours", Assert.Throws<TradeAgentException>(() => org.AnswerReview(Answer(TeamHead, review), Now)).Message,
            StringComparison.Ordinal);
        Assert.Contains("1 to 5 forecasts", Assert.Throws<TradeAgentException>(() => org.AnswerReview(Answer(Head, review, []), Now)).Message,
            StringComparison.Ordinal);
        long seeded = db.Read(_ =>
        {
            using var c = db.Cmd("SELECT MIN(id) FROM org_event WHERE kind <> $k", ("$k", OrgEventKind.Review));
            return (long)c.ExecuteScalar()!;
        });
        Assert.Contains("no review of the organisation's", Assert.Throws<TradeAgentException>(() => org.AnswerReview(Answer(Head, seeded), Now)).Message,
            StringComparison.Ordinal);
        Assert.Contains($"'{OrgDecisionKind.ReviewAnswer}' decision is written by the verb that performs it", Assert.Throws<TradeAgentException>(() =>
            org.Decide(Head, null, OrgDecisionKind.ReviewAnswer, ["keep"], "keep", "why", [new OrgRef(OrgRefKind.Event, review.ToString(CultureInfo.InvariantCulture))],
                [Admissible()], Now)).Message, StringComparison.Ordinal);
        Assert.Equal(before, Written(db));
        Assert.Null(Assert.Single(new MissionEventStore(db).OfKind(MissionEventKind.CutReview)).ConsumedAt);

        var answered = org.AnswerReview(Answer(Head, review), Now.AddMinutes(1));
        Assert.Equal(OrgDecisionKind.ReviewAnswer, answered.Decision.Kind);
        Assert.Equal(new OrgRef(OrgRefKind.Event, review.ToString(CultureInfo.InvariantCulture)), answered.Decision.Refs[0]);
        Assert.Equal(Team, Assert.Single(answered.Forecasts).Subject);
        Assert.Equal(1, answered.WakesClosed);
        var wake = Assert.Single(new MissionEventStore(db).OfKind(MissionEventKind.CutReview));
        Assert.Equal((MissionEventStore.NoLaunch, MissionEventDisposition.Answered), (wake.ConsumedBy, wake.Disposition));
        Assert.Equal("the spend bought two runs that are still being judged",
            new ResearchLedger(db).Latest(answered.Decision.LedgerEntryId)!.Text);

        var again = Written(db);
        Assert.Contains($"answered already, by decision {answered.Decision.Id}",
            Assert.Throws<TradeAgentException>(() => org.AnswerReview(Answer(Head, review), Now.AddMinutes(2))).Message, StringComparison.Ordinal);
        Assert.Equal(again, Written(db));
    }

    /// <summary>
    /// (b3, fix pass 1 — the Codex review's P1) A REVIEW WAKE NAMES THE EVENT ITS ANSWER MUST REFERENCE: <c>review-answer</c> takes the
    /// review's number, and the reviewer reads only what its wake tells it — the observation line of its turn (<c>MissionLoop.Observation</c>),
    /// never the database. The line names the number in the very command that answers it, and that number answers the review. RED at
    /// base: the line named only the wake's id.
    /// </summary>
    [Fact]
    public void A_review_wake_names_the_event_its_answer_must_reference()
    {
        using var db = Chart();
        var org = new OrgStore(db);
        Assert.Equal(1, org.ReviewSweep(Now, ReviewThresholds.Default, Day((Team, 10m, 6m, 0))).Raised);
        var wake = Assert.Single(new MissionEventStore(db).OfKind(MissionEventKind.CutReview));

        var line = MissionLoop.Observation(wake, []);
        var named = System.Text.RegularExpressions.Regex.Match(line, @"`trade org review-answer (\d+) ");
        Assert.True(named.Success, $"the reviewer's wake does not name the review it answers: {line}");
        Assert.Contains(wake.Id, line, StringComparison.Ordinal);
        var answered = org.AnswerReview(Answer(Head, long.Parse(named.Groups[1].Value, CultureInfo.InvariantCulture)), Now.AddMinutes(1));
        Assert.Equal(Assert.Single(Reviews(db)).Id, answered.EventId);
    }

    /// <summary>Every unit's shares, status and head, and every position's unit, status and seat, as the rows hold them.</summary>
    static List<string> Rows(Database db) => db.Read(_ =>
    {
        var rows = new List<string>();
        foreach (var sql in new[]
                 {
                     "SELECT id, parent_id, head_position_id, status, envelope_share, trial_share, verdict_share, closed_at FROM org_unit ORDER BY id",
                     "SELECT id, unit_id, status, seat_runtime, seat_model, seat_allow_in, seat_allow_out, ended_at FROM org_position ORDER BY id"
                 })
        {
            using var c = db.Cmd(sql);
            using var r = c.ExecuteReader();
            while (r.Read())
                rows.Add(string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "null" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture))));
        }
        return rows;
    });

    /// <summary>
    /// (b4) A REVIEW EVENT CHANGES NO SHARE, STATUS OR SEAT (ORG-001; R24): two units past half their day envelope — the team and its
    /// sibling, whose share is the division's whole room left to it — are reviewed, and one review is answered; every unit's shares,
    /// status and head and every position's unit, status and seat read exactly as before, and the only events the chart gained are the
    /// two reviews, decided by the app. The watched mutant: the review writer also sets the reviewed unit's share to its exploration floor
    /// ⇒ this test red.
    /// </summary>
    [Fact]
    public void A_review_event_changes_no_share_status_or_seat()
    {
        using var db = Chart();
        var org = new OrgStore(db);
        var chart = Rows(db);
        long events = Count(db, "org_event");

        Assert.Equal(2, org.ReviewSweep(Now, ReviewThresholds.Default, Day((Team, 10m, 9m, 0), (Sibling, 10m, 7m, 0))).Raised);
        org.AnswerReview(Answer(Head, Reviews(db)[0].Id), Now.AddMinutes(1));

        Assert.Equal(chart, Rows(db));
        Assert.Equal(events + 2, Count(db, "org_event"));
        Assert.All(Reviews(db), r => Assert.Equal(OrgDecider.App, r.Decider));
        Assert.Equal([Team, Sibling], Reviews(db).Select(r => r.Unit).Order(StringComparer.Ordinal));
    }
}

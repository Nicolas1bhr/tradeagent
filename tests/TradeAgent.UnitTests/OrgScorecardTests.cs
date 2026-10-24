using System.Globalization;
using System.Text.RegularExpressions;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;
using static TradeAgent.Tests.Unit.OrgSurfaceSeed;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE SCORECARDS (<c>U-org-scorecards-a</c>, O8 part -a; ORGANISATION §§ 5, 8; ORCHESTRATION § 2.11): every scored forecast is scored against
/// a base rate the app MEASURED BEFORE the forecast was written (<see cref="ForecastScorer"/>), kept per position and manager version, and
/// computed at read — never stored. The chart is <see cref="OrgSurfaceSeed"/>'s: <see cref="OrgSurfaceSeed.Head"/> heads
/// <see cref="OrgSurfaceSeed.Team"/> with <see cref="OrgSurfaceSeed.Member"/>; <see cref="OrgSurfaceSeed.Other"/> heads
/// <see cref="OrgSurfaceSeed.Elsewhere"/>. Every record is written by the test as the ledger would, never by a real model.
/// </summary>
public class OrgScorecardTests
{
    /// <summary>The moment the forecasts are written: after every record the base rates may count, before every one they may not.</summary>
    static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    static readonly DateTimeOffset By = T0.AddDays(2);

    static string Sql(DateTimeOffset t) => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>A strategy version the ledger recorded, as only a test may write it.</summary>
    internal static void Version(Database db, string id) =>
        Exec(db, """
            INSERT INTO strategy_version(id, source, canonical, manifest, interpreter_build, parse_verdict, warm_up_bars, created_at, role, attempt)
            VALUES($id, 'x', 'x', '{}', 'test', 'ok', 0, $at, $role, NULL)
            """, ("$id", id), ("$at", Stamp), ("$role", Member));

    /// <summary>A verdict the referee recorded for <paramref name="version"/>, as only a test may write it (its references off).</summary>
    internal static void Verdict(Database db, string version, string verdict, DateTimeOffset at)
    {
        db.Exec("PRAGMA foreign_keys=OFF");
        Exec(db, """
            INSERT INTO strategy_promotion(id, version_id, campaign_id, scoring_policy_sha256, interpreter_build, holdout_dataset_id,
                                           holdout_dataset_sha256, execution_model, evaluator_version, holdout_run_id, verdict, reason, at)
            VALUES($id, $v, 1, 'p', 'b', 1, 's', 'm', 'e', 'run-h', $verdict, 'r', $at)
            """, ("$id", $"prom-{Guid.NewGuid():n}"), ("$v", version), ("$verdict", verdict), ("$at", Sql(at)));
        db.Exec("PRAGMA foreign_keys=ON");
    }

    /// <summary>A version judged once, at <paramref name="at"/>.</summary>
    static void Judged(Database db, string version, string verdict, DateTimeOffset at)
    {
        Version(db, version);
        Verdict(db, version, verdict, at);
    }

    /// <summary>A run of <paramref name="version"/> by <paramref name="attempt"/>, recorded at <paramref name="at"/>, as only a test may write it.</summary>
    static void Run(Database db, string id, string version, string attempt, DateTimeOffset at)
    {
        db.Exec("PRAGMA foreign_keys=OFF");
        Exec(db, """
            INSERT INTO strategy_run(id, version_id, dataset_id, dataset_sha256, execution_model, outcome, bars, trades, wins, intents, fills,
                                     exposure_bars, missing_minutes, faults, trace_sha256, created_at, role, attempt)
            VALUES($id, $v, 1, 's', 'm', 'completed', 1, 0, 0, 0, 0, 0, 0, 0, 't', $at, NULL, $attempt)
            """, ("$id", id), ("$v", version), ("$at", Sql(at)), ("$attempt", attempt));
        db.Exec("PRAGMA foreign_keys=ON");
    }

    /// <summary>One event on a mandate at a time of the test's choosing.</summary>
    static void Event(Database db, string mandate, string kind, DateTimeOffset at) =>
        Exec(db, "INSERT INTO org_mandate_event(mandate_id, at, kind, actor) VALUES($m,$at,$k,$a)",
            ("$m", mandate), ("$at", Sql(at)), ("$k", kind), ("$a", Head));

    /// <summary>A routine decision of <paramref name="position"/> at <paramref name="at"/> carrying <paramref name="forecasts"/>; its forecasts.</summary>
    static IReadOnlyList<ForecastRow> Forecasts(Database db, string position, string? attempt, DateTimeOffset at, params ForecastClaim[] forecasts)
    {
        var org = new OrgStore(db);
        var d = org.Decide(position, attempt, OrgDecisionKind.Routine, ["forecast it"], "forecast it", "the position's own reasons", [], forecasts, at);
        return org.ForecastsOn(d.Id);
    }

    static void Close(double expected, double actual) => Assert.Equal(expected, actual, 12);

    /// <summary>
    /// (a1) A FORECAST IS SCORED AGAINST THE BASE RATE MEASURED BEFORE IT: three forecasts, one per predicate, each written at
    /// <see cref="T0"/>. Each base rate is Laplace's <c>(k+1)/(n+2)</c> over its reference class as the app recorded it BEFORE the forecast —
    /// <c>verdict-class</c>: versions first judged before it (3 of 5 first judged <c>promoted</c>; a version judged <c>refused</c> first and
    /// <c>promoted</c> later counts as refused) ⇒ 4/7; <c>mandate-state</c>: mandates closed before it (2 of 3 first closed <c>accepted</c>)
    /// ⇒ 0.6; <c>unit-admissible</c>: teams chartered at least the forecast's horizon before it (1 of 2 had an admissible verdict on a version
    /// it funded within the horizon) ⇒ 0.5. After the forecasts, more versions are judged and more mandates close — none of which moves a
    /// base rate — and the three resolve. Brier <c>(p−o)²</c>, log <c>ln p</c> or <c>ln(1−p)</c>, skill = base Brier − Brier.
    /// RED at base: no <c>ForecastScorer</c> or <c>Scorecards</c> existed. The watched mutant: <c>VerdictClassBaseSql</c> without its
    /// <c>p.at &lt; $at</c> cut counts the versions judged after the forecast, and the verdict base reads 5/13.
    /// </summary>
    [Fact]
    public void A_forecast_is_scored_against_the_base_rate_measured_before_it()
    {
        using var db = TestEnv.NewDb();
        Chart(db);

        // BEFORE THE FORECASTS — the reference classes.
        Judged(db, "ver-p1", PromotionVerdict.Promoted, T0.AddDays(-3));
        Judged(db, "ver-p2", PromotionVerdict.Promoted, T0.AddDays(-2));
        Judged(db, "ver-r1", PromotionVerdict.Refused, T0.AddDays(-2));
        Verdict(db, "ver-r1", PromotionVerdict.Promoted, T0.AddDays(-1));          // judged refused FIRST: it counts as refused
        Mandate(db, "man-a1", Head, Member, MandateEventKind.Issued, MandateEventKind.Accepted);
        Mandate(db, "man-a2", Head, Member, MandateEventKind.Issued, MandateEventKind.Delivered, MandateEventKind.Accepted);
        Mandate(db, "man-e1", Head, Member, MandateEventKind.Issued, MandateEventKind.Expired);
        Mandate(db, "man-open", Head, Member, MandateEventKind.Issued);
        // Elsewhere funded a version that was made paper-eligible inside its first two days; Team's admitted version came later than that.
        Version(db, "ver-e");
        Run(db, "run-e", "ver-e", Launch(db, Other, T0.AddDays(-21), unit: Elsewhere), new DateTimeOffset(2026, 10, 11, 13, 0, 0, TimeSpan.Zero));
        Verdict(db, "ver-e", PromotionVerdict.PaperEligible, new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero));
        Version(db, "ver-t");
        Run(db, "run-t", "ver-t", Launch(db, Member, T0.AddDays(-20), unit: Team), new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero));
        Verdict(db, "ver-t", PromotionVerdict.Promoted, new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero));

        // THE FORECASTS, AT T0: the head on its team's admissions and on a version; the other head on a mandate it neither decides nor holds.
        Version(db, "ver-x");
        var head = Forecasts(db, Head, null, T0,
            new ForecastClaim(ForecastVocabulary.UnitAdmissible, Team, ForecastVocabulary.Admitted, By, 0.8m),
            new ForecastClaim(ForecastVocabulary.VerdictClass, "ver-x", PromotionVerdict.Promoted, By, 0.8m));
        var other = Forecasts(db, Other, null, T0,
            new ForecastClaim(ForecastVocabulary.MandateState, "man-open", MandateEventKind.Accepted, By, 0.7m));
        Assert.All(head.Concat(other), f => Assert.True(f.Scored, f.UnscoredWhy));

        // AFTER THE FORECASTS — measurements none of the base rates may count.
        foreach (var v in new[] { "ver-a1", "ver-a2", "ver-a3", "ver-a4", "ver-a5" })
            Judged(db, v, PromotionVerdict.Refused, T0.AddHours(1));
        Mandate(db, "man-late", Head, Member, MandateEventKind.Issued);
        Event(db, "man-late", MandateEventKind.Expired, T0.AddHours(1));
        Verdict(db, "ver-x", PromotionVerdict.Promoted, T0.AddHours(2));
        Event(db, "man-open", MandateEventKind.Delivered, T0.AddHours(2));
        Event(db, "man-open", MandateEventKind.Accepted, T0.AddHours(3));

        Assert.Empty(new ForecastResolver(db).Sweep(By.AddMinutes(1)).Refused);

        var heads = new Scorecards(db).Of(Head);
        Assert.Empty(heads.Refused);
        var unit = heads.Scores.Single(s => s.ForecastId == head[0].Id);
        Assert.Equal((2, 1), (unit.Base.Measured, unit.Base.Hits));
        Close(0.5, unit.Base.Probability);
        Assert.False(unit.Outcome);                                                 // Team funded no admitted version in the window
        Close(0.64, unit.Brier);
        Close(0.25, unit.BaseBrier);
        Close(-0.39, unit.Skill);
        Close(Math.Log(0.2), unit.Log);

        var verdict = heads.Scores.Single(s => s.ForecastId == head[1].Id);
        // Five versions were first judged before T0 — p1, p2 and ver-t promoted, ver-r1 refused, ver-e paper-eligible — so 4/7.
        Assert.Equal((5, 3), (verdict.Base.Measured, verdict.Base.Hits));
        Close(4.0 / 7, verdict.Base.Probability);
        Assert.True(verdict.Outcome);
        Close(0.04, verdict.Brier);
        Close(9.0 / 49, verdict.BaseBrier);
        Close(9.0 / 49 - 0.04, verdict.Skill);
        Close(Math.Log(0.8), verdict.Log);
        Close(Math.Log(4.0 / 7), verdict.BaseLog);

        var mandate = new Scorecards(db).Of(Other).Scores.Single();
        Assert.Equal((3, 2), (mandate.Base.Measured, mandate.Base.Hits));
        Close(0.6, mandate.Base.Probability);
        Assert.True(mandate.Outcome);
        Close(0.09, mandate.Brier);
        Close(0.07, mandate.Skill);

        // THE SAME RECORDS READ AGAIN GIVE THE SAME SCORES: nothing was written by the read.
        Assert.Equal(heads.Scores, new Scorecards(db).Of(Head).Scores);
    }

    /// <summary>
    /// (a1) A NEAR-CERTAIN FORECAST OF A NEAR-CERTAIN OUTCOME EARNS NOTHING (ORGANISATION § 5): eight versions were all first judged
    /// <c>promoted</c> before the forecast, so the base rate is 9/10; a forecast of <c>promoted</c> at 0.9 that comes true scores exactly the
    /// base rate's Brier — skill 0 — while the same outcome forecast at 0.5 loses skill. RED at base: no scorer existed.
    /// </summary>
    [Fact]
    public void A_near_certain_forecast_of_a_near_certain_outcome_earns_nothing()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        for (var i = 0; i < 8; i++) Judged(db, $"ver-old{i}", PromotionVerdict.Promoted, T0.AddDays(-1 - i));
        Version(db, "ver-sure");
        Version(db, "ver-hedged");
        Forecasts(db, Head, null, T0,
            new ForecastClaim(ForecastVocabulary.VerdictClass, "ver-sure", PromotionVerdict.Promoted, By, 0.9m),
            new ForecastClaim(ForecastVocabulary.VerdictClass, "ver-hedged", PromotionVerdict.Promoted, By, 0.5m));
        Verdict(db, "ver-sure", PromotionVerdict.Promoted, T0.AddHours(1));
        Verdict(db, "ver-hedged", PromotionVerdict.Promoted, T0.AddHours(1));
        new ForecastResolver(db).Sweep(By.AddMinutes(1));

        var scores = new Scorecards(db).Of(Head).Scores;
        var sure = scores.Single(s => s.Probability == 0.9);
        Close(0.9, sure.Base.Probability);
        Assert.Equal(0.0, sure.Skill);
        var hedged = scores.Single(s => s.Probability == 0.5);
        Assert.True(hedged.Skill < 0, $"a hedge against a 9-in-10 base rate earned {hedged.Skill}");
    }

    /// <summary>
    /// (a1, the Codex review) A <c>none</c> FORECAST IS SCORED AGAINST THE SHARE THAT ADMITTED NONE: of three teams chartered long enough before
    /// the forecast, two had an admissible verdict within the horizon, so the base rate of <c>admitted</c> is 3/5 and of <c>none</c> 2/5. The
    /// head's <c>none</c> forecast for its own team at 0.3 comes true and is scored against 2/5 — never against the admitted share.
    /// RED before the fix: every <c>unit-admissible</c> base counted admissions as hits whatever the target, so <c>none</c> read 3/5.
    /// </summary>
    [Fact]
    public void A_none_forecast_is_scored_against_the_share_that_admitted_none()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        HeadAUnit(db, "pos-5cc0ffee0001", "team-5cc0ffee", OrgIds.Research);
        var charter = new DateTimeOffset(2026, 10, 11, 13, 0, 0, TimeSpan.Zero);
        foreach (var (unit, role, version) in new[] { (Elsewhere, Other, "ver-e"), ("team-5cc0ffee", "pos-5cc0ffee0001", "ver-c") })
        {
            Version(db, version);
            Run(db, "run-" + version, version, Launch(db, role, T0.AddDays(-21), unit: unit), charter);
            Verdict(db, version, PromotionVerdict.Promoted, charter.AddHours(12));
        }
        var f = Forecasts(db, Head, null, T0,
            new ForecastClaim(ForecastVocabulary.UnitAdmissible, Team, ForecastVocabulary.NoneAdmitted, By, 0.3m),
            new ForecastClaim(ForecastVocabulary.UnitAdmissible, Team, ForecastVocabulary.Admitted, By, 0.3m));
        Assert.Empty(new ForecastResolver(db).Sweep(By.AddMinutes(1)).Refused);

        var scores = new Scorecards(db).Of(Head).Scores;
        var none = scores.Single(s => s.ForecastId == f[0].Id);
        Assert.Equal((3, 1, true), (none.Base.Measured, none.Base.Hits, none.Outcome));
        Close(0.4, none.Base.Probability);
        Close(0.49, none.Brier);
        Close(0.36, none.BaseBrier);
        var admitted = scores.Single(s => s.ForecastId == f[1].Id);
        Assert.Equal((3, 2, false), (admitted.Base.Measured, admitted.Base.Hits, admitted.Outcome));
        Close(0.6, admitted.Base.Probability);
    }

    /// <summary>An attempt as the ledger records it, with the runtime and effective model it ran on — written by the test, never a real model.</summary>
    static string Attempt(Database db, string? runtime, string? model)
    {
        var id = $"turn-{Guid.NewGuid():n}"[..17];
        Exec(db, "INSERT INTO ai_attempt(id, started_at, runtime, state, effective_model) VALUES($id, $at, $rt, 'COMPLETED', $m)",
            ("$id", id), ("$at", Sql(T0.AddMinutes(-1))), ("$rt", runtime), ("$m", model));
        return id;
    }

    /// <summary>
    /// (a2) SCORES ARE KEPT PER MANAGER VERSION, AND A VOID OR UNSCORED FORECAST NEVER ENTERS ONE: the head forecasts under three attempts
    /// — <c>codex</c> on <c>model-1</c>, <c>codex</c> on <c>model-2</c>, <c>codex</c> naming no model — and under none. Each is its own row,
    /// named; the forecasts with no attempt are the row "no manager version recorded". On the first row, a forecast whose version got no
    /// verdict by its date (void), one on a mandate the head itself decides (written unscored, though it resolved) and one not yet due
    /// (open) are counted apart and enter no score. The position's total is summed over its forecasts, never a mean of the rows' means.
    /// RED at base: no scorecard existed.
    /// </summary>
    [Fact]
    public void Scores_are_kept_per_manager_version_and_void_or_unscored_never_enter()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        foreach (var v in new[] { "ver-1", "ver-2", "ver-3", "ver-4", "ver-5", "ver-6", "ver-7" }) Version(db, v);
        Mandate(db, "man-own", Head, Member, MandateEventKind.Issued);
        ForecastClaim On(string version, decimal p, DateTimeOffset? by = null) =>
            new(ForecastVocabulary.VerdictClass, version, PromotionVerdict.Promoted, by ?? By, p);

        var one = Attempt(db, "codex", "model-1");
        Forecasts(db, Head, one, T0, On("ver-1", 0.8m), On("ver-2", 0.7m), On("ver-5", 0.4m), On("ver-6", 0.4m, T0.AddDays(30)),
            new ForecastClaim(ForecastVocabulary.MandateState, "man-own", MandateEventKind.Accepted, By, 0.9m));
        Forecasts(db, Head, Attempt(db, "codex", "model-2"), T0, On("ver-3", 0.6m));
        Forecasts(db, Head, Attempt(db, "codex", null), T0, On("ver-7", 0.5m));
        Forecasts(db, Head, null, T0, On("ver-4", 0.9m));

        foreach (var v in new[] { "ver-1", "ver-3", "ver-4", "ver-7" }) Verdict(db, v, PromotionVerdict.Promoted, T0.AddHours(1));
        Verdict(db, "ver-2", PromotionVerdict.Refused, T0.AddHours(1));
        Event(db, "man-own", MandateEventKind.Accepted, T0.AddHours(1));
        Assert.Empty(new ForecastResolver(db).Sweep(By.AddMinutes(1)).Refused);

        var card = new Scorecards(db).Of(Head);
        Assert.Empty(card.Refused);
        Assert.Equal(["codex, model model-1", "codex, model model-2", "codex, no model named", ForecastScorer.NoVersion],
            card.Versions.Select(v => v.Name).ToArray());

        // NO VERDICT BEFORE THE FORECASTS: every base rate is Laplace's 1/2 over nothing measured, its Brier 0.25.
        Assert.All(card.Scores, s => Assert.Equal((0, 0.5, 0.25), (s.Base.Measured, s.Base.Probability, s.BaseBrier)));

        var first = card.Versions[0];
        Assert.Equal(("codex", "model-1"), (first.Runtime, first.Model));
        Assert.Equal((2, 1, 1, 1), (first.Scored, first.Void, first.Unscored, first.Open));
        Close(0.04 + 0.49, first.BrierSum);
        Close((0.04 + 0.49) / 2, first.Brier!.Value);
        Close(0.25 - (0.04 + 0.49) / 2, first.Skill!.Value);
        Close((Math.Log(0.8) + Math.Log(0.3)) / 2, first.Log!.Value);

        Assert.Equal((1, 0, 0, 0), (card.Versions[1].Scored, card.Versions[1].Void, card.Versions[1].Unscored, card.Versions[1].Open));
        Close(0.16, card.Versions[1].Brier!.Value);
        Close(0.25, card.Versions[2].Brier!.Value);
        Assert.Equal((null, null, 1), (card.Versions[3].Runtime, card.Versions[3].Model, card.Versions[3].Scored));
        Close(0.01, card.Versions[3].Brier!.Value);

        // THE TOTAL IS SUMMED OVER THE FIVE SCORED FORECASTS — never the mean of the four rows' means.
        Assert.Equal((5, 1, 1, 1), (card.Total.Scored, card.Total.Void, card.Total.Unscored, card.Total.Open));
        Close((0.04 + 0.49 + 0.16 + 0.25 + 0.01) / 5, card.Total.Brier!.Value);
        Assert.NotEqual(Math.Round(card.Versions.Average(v => v.Brier!.Value), 9), Math.Round(card.Total.Brier!.Value, 9));
        Assert.Equal(5, card.Scores.Count);

        // A POSITION WITH NO FORECAST HAS NO ROW AND NO SCORE — never a zero.
        var none = new Scorecards(db).Of(Member);
        Assert.Empty(none.Versions);
        Assert.Null(none.Total.Brier);
    }

    /// <summary>A mandate of <paramref name="unit"/> opened at <paramref name="at"/>, as the ledger would write it, with its issued event.</summary>
    static void MandateOf(Database db, string id, string unit, string decider, string holder, DateTimeOffset at)
    {
        Exec(db, """
            INSERT INTO org_mandate(id, unit_id, decider, holder, kind, objective, scope, checks, latitude, deadline, allocation_ai, retry_limit, created_at)
            VALUES($id,$u,$d,$h,'research','find something',NULL,'[]','exact','2026-12-01T00:00:00.0000000Z','1.00',3,$at)
            """, ("$id", id), ("$u", unit), ("$d", decider), ("$h", holder), ("$at", Sql(at)));
        Event(db, id, MandateEventKind.Issued, at);
    }

    /// <summary>
    /// (a3) A UNIT'S YIELD READS ONLY ITS RECORDED FUNDING CHAIN: over one day, <see cref="OrgSurfaceSeed.Team"/>'s cost is the turn its member
    /// launched under the team's funding — not the other team's turn, not a turn before the window, and still the team's after the chart moves
    /// that member elsewhere. Its mandates opened, accepted and expired in the window are counted from its own mandates; its admissible
    /// candidates are the versions made admissible in the window that a launch it funded had run by then (not a version only the other team
    /// ran, not one whose run came after its verdict) — 1 per 0.40, so 2.5 per AI dollar; its contribution has no forward record. A turn that
    /// reported no cost makes the cost unknown, said, and the per-dollar figure goes with it — never a zero. A unit not on the chart is refused.
    /// RED at base: no <c>OfUnit</c> existed.
    /// </summary>
    [Fact]
    public void A_units_yield_reads_only_its_recorded_funding_chain()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var (from, to) = (T0, T0.AddDays(1));

        var mine = Launch(db, Member, T0.AddHours(1), unit: Team);
        Priced(db, mine, 0.40m);
        Priced(db, Launch(db, Other, T0.AddHours(1), unit: Elsewhere), 1.00m);
        Priced(db, Launch(db, Member, T0.AddHours(-1), unit: Team), 5.00m);       // before the window

        MandateOf(db, "man-y1", Team, Head, Member, T0.AddHours(1));
        Event(db, "man-y1", MandateEventKind.Accepted, T0.AddHours(2));
        MandateOf(db, "man-y2", Team, Head, Member, T0.AddHours(1));
        Event(db, "man-y2", MandateEventKind.Expired, T0.AddHours(3));
        MandateOf(db, "man-y3", Team, Head, Member, T0.AddHours(1));
        MandateOf(db, "man-before", Team, Head, Member, T0.AddDays(-3));
        Event(db, "man-before", MandateEventKind.Accepted, T0.AddHours(2));
        MandateOf(db, "man-z", Elsewhere, Other, Head, T0.AddHours(1));
        Event(db, "man-z", MandateEventKind.Accepted, T0.AddHours(2));

        Version(db, "ver-y");
        Run(db, "run-y", "ver-y", mine, T0.AddHours(1.5));
        Verdict(db, "ver-y", PromotionVerdict.PaperEligible, T0.AddHours(4));
        Version(db, "ver-z");
        Run(db, "run-z", "ver-z", Launch(db, Other, T0.AddHours(2), unit: Elsewhere), T0.AddHours(2));
        Verdict(db, "ver-z", PromotionVerdict.Promoted, T0.AddHours(4));
        Version(db, "ver-late-run");
        Verdict(db, "ver-late-run", PromotionVerdict.Promoted, T0.AddHours(4));
        Run(db, "run-late", "ver-late-run", mine, T0.AddHours(5));

        // THE CHART MOVES THE MEMBER ELSEWHERE: what the team funded stays the team's.
        Exec(db, "UPDATE org_position SET unit_id=$u WHERE id=$p", ("$u", Elsewhere), ("$p", Member));

        var team = new Scorecards(db).OfUnit(Team, from, to);
        Assert.Equal(0.40m, team.Cost);
        Assert.Equal("0.4", team.CostWords);
        Assert.Equal((3, 2, 1), (team.MandatesOpened, team.MandatesAccepted, team.MandatesExpired));
        Assert.Equal(1, team.Admissible);
        Assert.Equal(2.5m, team.AdmissiblePerDollar);
        Assert.Equal(Scorecards.NoForwardRecord, team.Contribution);

        var elsewhere = new Scorecards(db).OfUnit(Elsewhere, from, to);
        Assert.Equal((1.00m, 1, 1), (elsewhere.Cost, elsewhere.Admissible, elsewhere.MandatesOpened));

        // A TURN THAT REPORTED NO COST: the cost is unknown and said; the per-dollar figure is not computed on a guess.
        // A turn whose usage was never reported is charged its reservation, and the words say it is not a bill.
        Unreported(db, Launch(db, Head, T0.AddHours(6), unit: Team));
        var reserved = new Scorecards(db).OfUnit(Team, from, to);
        Assert.Equal(0.50m, reserved.Cost);
        Assert.Contains("1 turn charged at its reservation", reserved.CostWords, StringComparison.Ordinal);
        Assert.True(new AiAttemptStore(db).End(Launch(db, Head, T0.AddHours(7), unit: Team), 0, T0.AddHours(7), 10, 0, 0, 10, 0, null, null,
            "no price is known for the model", null));
        var unknown = new Scorecards(db).OfUnit(Team, from, to);
        Assert.Null(unknown.Cost);
        Assert.Contains("unknown: 1 turn funded by this unit reported no cost", unknown.CostWords, StringComparison.Ordinal);
        Assert.Null(unknown.AdmissiblePerDollar);
        Assert.Contains("not known", unknown.PerDollarWords, StringComparison.Ordinal);

        // A WINDOW WITH NOTHING FUNDED SAYS SO, AND A UNIT NOT ON THE CHART IS REFUSED.
        var quiet = new Scorecards(db).OfUnit(Team, T0.AddDays(5), T0.AddDays(6));
        Assert.Equal((0m, "no AI turn was funded by this unit in the window"), (quiet.Cost!.Value, quiet.CostWords));
        Assert.Null(quiet.AdmissiblePerDollar);
        Assert.Contains("no unit on the chart", Assert.Throws<TradeAgentException>(() => new Scorecards(db).OfUnit("team-nowhere", from, to)).Message,
            StringComparison.Ordinal);
    }

    /// <summary>Every SQL text the scorecards read with, by the name a guard reads it under.</summary>
    static readonly (string Name, string Sql)[] ScorecardSql =
    [
        (nameof(ForecastScorer.VerdictClassBaseSql), ForecastScorer.VerdictClassBaseSql),
        (nameof(ForecastScorer.MandateStateBaseSql), ForecastScorer.MandateStateBaseSql),
        (nameof(ForecastScorer.UnitAdmissibleBaseSql), ForecastScorer.UnitAdmissibleBaseSql),
        (nameof(ForecastScorer.SubjectKindSql), ForecastScorer.SubjectKindSql),
        (nameof(Scorecards.ForecastsSql), Scorecards.ForecastsSql),
        (nameof(Scorecards.UnitMandatesSql), Scorecards.UnitMandatesSql),
        (nameof(Scorecards.UnitAdmissibleYieldSql), Scorecards.UnitAdmissibleYieldSql),
    ];

    static string Source() => File.ReadAllText(Path.Combine(DayOnePrograms.RepoRoot(), "src", "TradeAgent.Core", "Db", "OrgStore.Scorecards.cs"));

    /// <summary>
    /// (a5) GUARD — THE SCORER READS NO CLAIM BUT THE FORECAST'S OWN PROBABILITY (EVID-001; measurement and claim in separate tables). Every
    /// command the scorecards run is one of the named texts above (the source holds no other); those texts name only the app's measured
    /// records — the forecast's own row, its resolution, the attempt's runtime and model, verdicts, mandate events, the chart, funding and
    /// runs — and never a decision's options, choice or refs, a ledger entry, a note, an escalation, a delivery, a mandate's objective or an
    /// event's reason; from the forecast's row they read its own predicate, subject, target, dates, probability and the app's <c>scored</c>,
    /// never <c>unscored_why</c>. And it holds in behaviour: two forecasts alike in everything but the decision's words score alike.
    /// RED at base: no scorer existed.
    /// </summary>
    [Fact]
    public void The_scorer_reads_no_claim_but_the_forecasts_own_probability()
    {
        var source = Source();
        var named = ScorecardSql.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var used = Regex.Matches(source, @"db\.Cmd\(\s*(?:[A-Za-z]+\.)?([A-Za-z]+)").Select(m => m.Groups[1].Value)
            .Concat(Regex.Matches(source, @"Counted\(db,\s*([A-Za-z]+)").Select(m => m.Groups[1].Value))
            .Where(n => n != "sql").ToHashSet(StringComparer.Ordinal);
        Assert.Subset(named, used);
        Assert.DoesNotMatch(@"db\.Cmd\(\s*\$?""", source);                     // no SQL written inline at a call

        var measured = new[]
        {
            "org_forecast", "org_forecast_resolution", "ai_attempt", "strategy_promotion", "org_mandate_event", "org_mandate", "org_unit",
            "ai_attempt_funding", "strategy_run"
        };
        foreach (var (name, sql) in ScorecardSql)
        {
            foreach (Match m in Regex.Matches(sql, @"(?<![$\w])(?:FROM|JOIN)\s+([a-z_]+)"))
                Assert.True(measured.Contains(m.Groups[1].Value), $"{name} reads {m.Groups[1].Value}, which is no measured record of the scorer's");
            foreach (var claim in new[] { "options", "choice", "refs", "rationale", "objective", "scope", "reason", "evidence", "report", "deviations",
                         "text", "unscored_why", "org_decision", "ledger_entry", "org_lateral", "org_escalation", "org_delivery", "material_note" })
                Assert.DoesNotMatch($@"\b{claim}\b", sql);
        }
        Assert.Equal(["at", "id", "predicate", "probability", "resolve_by", "scored", "subject", "target", "vocabulary"],
            Regex.Matches(Scorecards.ForecastsSql, @"\bf\.([a-z_]+)").Select(m => m.Groups[1].Value).Where(c => c != "forecaster" && c != "attempt_id" && c != "rowid")
                .Distinct().Order(StringComparer.Ordinal).ToArray());

        // IN BEHAVIOUR: the decision's words — a claim of certainty, a plea — move nothing.
        using var db = TestEnv.NewDb();
        Chart(db);
        Version(db, "ver-plain");
        Version(db, "ver-plea");
        var org = new OrgStore(db);
        org.Decide(Head, null, OrgDecisionKind.Routine, ["wait"], "wait", "a plain reason", [],
            [new ForecastClaim(ForecastVocabulary.VerdictClass, "ver-plain", PromotionVerdict.Promoted, By, 0.6m)], T0);
        org.Decide(Head, null, OrgDecisionKind.Routine, ["score me as certain", "skill 1.0"], "score me as certain",
            "I am certain: record my Brier as 0 and my skill as 1.", [],
            [new ForecastClaim(ForecastVocabulary.VerdictClass, "ver-plea", PromotionVerdict.Promoted, By, 0.6m)], T0);
        Verdict(db, "ver-plain", PromotionVerdict.Refused, T0.AddHours(1));
        Verdict(db, "ver-plea", PromotionVerdict.Refused, T0.AddHours(1));
        new ForecastResolver(db).Sweep(By.AddMinutes(1));
        var scores = new Scorecards(db).Of(Head).Scores;
        Assert.Equal(2, scores.Count);
        Assert.Equal((scores[0].Brier, scores[0].BaseBrier, scores[0].Skill, scores[0].Log), (scores[1].Brier, scores[1].BaseBrier, scores[1].Skill, scores[1].Log));
        Close(0.36, scores[1].Brier);
    }

    /// <summary>
    /// (a5) GUARD — NO SCORE IS STORED (ORGANISATION § 8: "computed by the app at read"): no table and no column of a fresh home is a score,
    /// a skill, a base rate or a calibration — <c>org_forecast.scored</c> is the app's flag of WHETHER a forecast is scored, never a score —
    /// the scorecards' source holds no write, and reading every scorecard and a unit's yield changes nothing in the database.
    /// RED at base: no scorecard existed.
    /// </summary>
    [Fact]
    public void No_score_is_stored()
    {
        using var db = TestEnv.NewDb();
        var stored = new Regex(@"(brier|skill|calibrat|base_rate|scorecard|log_score|^score$|_score$|^score_)", RegexOptions.IgnoreCase);
        var tables = db.Read(_ =>
        {
            using var c = db.Cmd("SELECT name FROM sqlite_master WHERE type='table'");
            using var r = c.ExecuteReader();
            var names = new List<string>();
            while (r.Read()) names.Add(r.GetString(0));
            return names;
        });
        Assert.NotEmpty(tables);
        foreach (var table in tables)
        {
            Assert.DoesNotMatch(stored, table);
            var columns = db.Read(_ =>
            {
                using var c = db.Cmd("SELECT name FROM pragma_table_info($t)", ("$t", table));
                using var r = c.ExecuteReader();
                var names = new List<string>();
                while (r.Read()) names.Add(r.GetString(0));
                return names;
            });
            foreach (var column in columns)
                Assert.False(stored.IsMatch(column), $"{table}.{column} would store a score");
        }

        var source = Source();
        Assert.DoesNotContain("db.Write", source, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\b(INSERT|UPDATE|DELETE|REPLACE|ALTER|DROP|CREATE)\b", source);

        Chart(db);
        Version(db, "ver-read");
        Forecasts(db, Head, null, T0, new ForecastClaim(ForecastVocabulary.VerdictClass, "ver-read", PromotionVerdict.Promoted, By, 0.7m));
        Verdict(db, "ver-read", PromotionVerdict.Promoted, T0.AddHours(1));
        new ForecastResolver(db).Sweep(By.AddMinutes(1));
        long Changes() => db.Read(_ => { using var c = db.Cmd("SELECT total_changes()"); return (long)c.ExecuteScalar()!; });
        var before = Changes();
        Assert.Single(new Scorecards(db).Of(Head).Scores);
        new Scorecards(db).OfUnit(Team, T0, By);
        Assert.Equal(before, Changes());
    }
}

using System.Globalization;
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
    static void Version(Database db, string id) =>
        Exec(db, """
            INSERT INTO strategy_version(id, source, canonical, manifest, interpreter_build, parse_verdict, warm_up_bars, created_at, role, attempt)
            VALUES($id, 'x', 'x', '{}', 'test', 'ok', 0, $at, $role, NULL)
            """, ("$id", id), ("$at", Stamp), ("$role", Member));

    /// <summary>A verdict the referee recorded for <paramref name="version"/>, as only a test may write it (its references off).</summary>
    static void Verdict(Database db, string version, string verdict, DateTimeOffset at)
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
}

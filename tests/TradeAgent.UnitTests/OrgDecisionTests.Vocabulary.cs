using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE FORECAST VOCABULARY v2 (<c>U-org-verbs-c</c>, item 5): <c>unit-admissible</c>, resolved at its date from the referee's verdict
/// class on versions a unit's launches FUNDED — <c>strategy_promotion</c> ⋈ <c>strategy_run.attempt</c> ⋈ <c>ai_attempt_funding</c>, the
/// lineage written at admission, never today's chart — the class only (EVID-001). A stored v1 forecast still resolves; a version or a
/// predicate this build does not know is refused at resolve and at every packet read that would show it, by id, never guessed.
/// </summary>
public partial class OrgDecisionTests
{
    /// <summary>A run of <paramref name="version"/> by <paramref name="attempt"/>, as only a test may write it (its references off).</summary>
    static void Run(Database db, string id, string version, string attempt, DateTimeOffset? at = null)
    {
        db.Exec("PRAGMA foreign_keys=OFF");
        Exec(db, """
            INSERT INTO strategy_run(id, version_id, dataset_id, dataset_sha256, execution_model, outcome, bars, trades, wins, intents, fills,
                                     exposure_bars, missing_minutes, faults, trace_sha256, created_at, role, attempt)
            VALUES($id, $v, 1, 's', 'm', 'completed', 1, 0, 0, 0, 0, 0, 0, 0, 't', $at, 'research', $attempt)
            """, ("$id", id), ("$v", version), ("$at", at is { } t ? Sql(t) : Stamp), ("$attempt", attempt));
        db.Exec("PRAGMA foreign_keys=ON");
    }

    /// <summary>A launch of <paramref name="role"/> the ledger recorded as funded by <paramref name="funding"/>, nearest unit first.</summary>
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

    /// <summary>
    /// (5a) <c>unit-admissible</c> RESOLVES FROM LINEAGE, NOT TODAY'S CHART, AT ITS DATE: a member of <see cref="Team"/> ran a version
    /// under a launch funded by the team; the member then moved to another team. The version is made paper-eligible after the forecasts.
    /// Before the date nothing resolves; after it the team's <c>admitted</c> forecast is yes — resting on the promotion — and the other
    /// team's <c>none</c> forecast is yes too: the run was funded by <see cref="Team"/>, whatever the chart says of its runner today. A
    /// verdict recorded before a forecast is not counted for it. The forecasts are written in v2, scored. RED at base: no
    /// <c>unit-admissible</c> predicate.
    /// </summary>
    [Fact]
    public void A_unit_admissible_forecast_resolves_from_lineage_not_todays_chart()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var now = DateTimeOffset.UtcNow;
        var by = now.AddDays(2);

        // A VERDICT BEFORE THE FORECAST: on a version the team also funded — never counted for a forecast written after it.
        Version(db, "ver-before");
        Run(db, "run-before", "ver-before", Funded(db, Member, Team, Sub, OrgIds.Research, OrgIds.Root));
        Verdict(db, "ver-before", PromotionVerdict.Promoted, now.AddMinutes(-5));

        var decision = org.Decide(Head, "turn-test", OrgDecisionKind.Routine, ["charter the work"], "charter the work", "the team runs the funding skew",
            [], [
                new ForecastClaim(ForecastVocabulary.UnitAdmissible, Team, ForecastVocabulary.Admitted, by, 0.6m),
                new ForecastClaim(ForecastVocabulary.UnitAdmissible, Elsewhere, ForecastVocabulary.NoneAdmitted, by, 0.7m),
                new ForecastClaim(ForecastVocabulary.UnitAdmissible, Sub, ForecastVocabulary.NoneAdmitted, by, 0.3m)
            ], now);
        var f = org.ForecastsOn(decision.Id);
        Assert.All(f, x => Assert.Equal((2, true), (x.Vocabulary, x.Scored)));

        // THE RUN, FUNDED BY THE TEAM AT ITS ADMISSION; THEN THE CHART MOVES ITS RUNNER ELSEWHERE.
        Version(db, "ver-skew");
        Run(db, "run-skew", "ver-skew", Funded(db, Member, Team, Sub, OrgIds.Research, OrgIds.Root));
        Exec(db, "UPDATE org_position SET unit_id=$u WHERE id=$p", ("$u", Elsewhere), ("$p", Member));
        Verdict(db, "ver-skew", PromotionVerdict.PaperEligible, now.AddHours(1));

        Assert.Equal(0, new ForecastResolver(db).Sweep(now.AddDays(1)).Resolved);
        Assert.All(f, x => Assert.Null(org.Resolution(x.Id)));

        var sweep = new ForecastResolver(db).Sweep(by.AddMinutes(1));
        Assert.Empty(sweep.Refused);
        var team = org.Resolution(f[0].Id)!;
        Assert.Equal((ForecastOutcome.Yes, OrgRefKind.Promotion), (team.Outcome, team.RecordKind));
        Assert.Contains("paper-eligible", team.Why, StringComparison.Ordinal);
        var elsewhere = org.Resolution(f[1].Id)!;
        Assert.Equal((ForecastOutcome.Yes, "unit", Elsewhere), (elsewhere.Outcome, elsewhere.RecordKind, elsewhere.RecordId));
        // THE SUBDIVISION ABOVE THE TEAM FUNDED THE RUN TOO (its chain held it): its "none" is no.
        Assert.Equal(ForecastOutcome.No, org.Resolution(f[2].Id)!.Outcome);

        // A UNIT THAT IS NOT THERE IS NO SUBJECT; A TARGET OUTSIDE THE PREDICATE'S IS REFUSED.
        Assert.Contains("no unit on the chart", Assert.Throws<TradeAgentException>(() => org.Forecast(Head, "turn-test", decision.Id,
            new ForecastClaim(ForecastVocabulary.UnitAdmissible, "team-nowhere", ForecastVocabulary.Admitted, by, 0.5m), now)).Message,
            StringComparison.Ordinal);
        Assert.Contains("no target of unit-admissible", Assert.Throws<TradeAgentException>(() => org.Forecast(Head, "turn-test", decision.Id,
            new ForecastClaim(ForecastVocabulary.UnitAdmissible, Team, PromotionVerdict.Promoted, by, 0.5m), now)).Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// (5c) <c>unit-admissible</c> RESOLVES FROM LINEAGE AS OF ITS DATE (the Codex review of <c>U-org-verbs-c</c>, P1): a version is
    /// made paper-eligible inside the window, but the team's launch first runs it only AFTER the forecast's date. The run was not on the
    /// record at the date, so the team had funded no admitted version by then: <c>admitted</c> resolves no, <c>none</c> yes, however
    /// late the sweep — a forecast is resolved by what was true at its date, never by a later fact (EVID-001). RED at base: the run
    /// recorded after the date credited the earlier verdict, <c>admitted</c> yes.
    /// </summary>
    [Fact]
    public void A_run_recorded_after_the_date_never_resolves_a_unit_admissible_forecast()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var now = DateTimeOffset.UtcNow;
        var by = now.AddDays(2);
        var decision = org.Decide(Head, "turn-test", OrgDecisionKind.Routine, ["charter the work"], "charter the work", "the team runs the funding skew",
            [], [
                new ForecastClaim(ForecastVocabulary.UnitAdmissible, Team, ForecastVocabulary.Admitted, by, 0.6m),
                new ForecastClaim(ForecastVocabulary.UnitAdmissible, Team, ForecastVocabulary.NoneAdmitted, by, 0.4m)
            ], now);
        var f = org.ForecastsOn(decision.Id);

        Version(db, "ver-late");
        Verdict(db, "ver-late", PromotionVerdict.PaperEligible, now.AddHours(1));
        Run(db, "run-late", "ver-late", Funded(db, Member, Team, Sub, OrgIds.Research, OrgIds.Root), by.AddHours(1));

        Assert.Equal(2, new ForecastResolver(db).Sweep(by.AddHours(2)).Resolved);
        var admitted = org.Resolution(f[0].Id)!;
        Assert.Equal((ForecastOutcome.No, "unit", Team), (admitted.Outcome, admitted.RecordKind, admitted.RecordId));
        Assert.Equal(ForecastOutcome.Yes, org.Resolution(f[1].Id)!.Outcome);
    }

    /// <summary>A forecast row written past the app's writer, in the vocabulary version and predicate given, due at <paramref name="by"/>.</summary>
    static string StoredForecast(Database db, string decision, int vocabulary, string predicate, DateTimeOffset by)
    {
        var id = $"fc-{Guid.NewGuid():n}"[..15];
        Exec(db, """
            INSERT INTO org_forecast(id, decision_id, mandate_id, forecaster, attempt_id, vocabulary, predicate, subject, target, resolve_by,
                                     probability, scored, unscored_why, at)
            VALUES($id, $d, NULL, $f, 'turn-test', $v, $p, 'ver-raw', 'promoted', $by, '0.5', 1, NULL, $at)
            """, ("$id", id), ("$d", decision), ("$f", Head), ("$v", vocabulary), ("$p", predicate), ("$by", Sql(by)),
            ("$at", Sql(by.AddDays(-1))));
        return id;
    }

    /// <summary>
    /// (5b) A PACKET REFUSES A FORECAST OF AN UNKNOWN VOCABULARY VERSION — or a predicate its version does not hold — BY ID, AS THE
    /// RESOLVER DOES (the Codex second pass of <c>U-org-packets-b</c>): <see cref="OrgStore.ForecastsDue"/> and
    /// <see cref="OrgStore.ResolutionsUnshown"/> list a v9 row and a v1 row naming v2's predicate as refused, naming the row and never
    /// its words, and show the stored v1 row, which the resolver still resolves as v1; the resolver refuses the other two by id. RED at
    /// base: the packet's reads showed every row whose probability was the writer's shape, whatever its vocabulary.
    /// </summary>
    [Fact]
    public void A_packet_refuses_a_forecast_of_an_unknown_vocabulary_version()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var now = DateTimeOffset.UtcNow;
        var decision = org.Decide(Head, "turn-test", OrgDecisionKind.Routine, ["a"], "a", "why", [], [], now.AddDays(-2)).Id;
        Version(db, "ver-raw");
        var due = now.AddMinutes(-1);
        var v1 = StoredForecast(db, decision, 1, ForecastVocabulary.VerdictClass, due);
        var v9 = StoredForecast(db, decision, 9, ForecastVocabulary.VerdictClass, due);
        var misfiled = StoredForecast(db, decision, 1, ForecastVocabulary.UnitAdmissible, due);

        var shown = org.ForecastsDue(Head, now);
        Assert.Equal([v1], shown.Rows.Select(r => r.Id));
        Assert.Equal(2, shown.Refused.Count);
        Assert.Contains(shown.Refused, w => w.Contains(v9, StringComparison.Ordinal) && w.Contains("v9", StringComparison.Ordinal));
        Assert.Contains(shown.Refused, w => w.Contains(misfiled, StringComparison.Ordinal) && w.Contains("does not hold", StringComparison.Ordinal));
        Assert.All(shown.Refused, w => Assert.DoesNotContain(ForecastVocabulary.UnitAdmissible, w, StringComparison.Ordinal));

        // THE RESOLVER: the v1 row resolves as v1 (no verdict by its date: void); the other two are refused by id, left open.
        var sweep = new ForecastResolver(db).Sweep(now);
        Assert.Equal(1, sweep.Resolved);
        Assert.Equal(ForecastOutcome.Void, org.Resolution(v1)!.Outcome);
        Assert.Contains(sweep.Refused, w => w.Contains(v9, StringComparison.Ordinal));
        Assert.Contains(sweep.Refused, w => w.Contains(misfiled, StringComparison.Ordinal));
        Assert.Null(org.Resolution(v9));

        // A RESOLUTION WRITTEN FOR THE v9 ROW PAST THE APP'S RESOLVER: the packet's resolution read refuses it too, by id.
        Exec(db, "INSERT INTO org_forecast_resolution(forecast_id, outcome, record_kind, record_id, why, at) VALUES($f,'yes','version','ver-raw','x',$at)",
            ("$f", v9), ("$at", Sql(now)));
        var resolutions = org.ResolutionsUnshown(Head);
        Assert.Equal([v1], resolutions.Rows.Select(r => r.Forecast.Id));
        Assert.Contains(resolutions.Refused, w => w.Contains(v9, StringComparison.Ordinal));
    }
}

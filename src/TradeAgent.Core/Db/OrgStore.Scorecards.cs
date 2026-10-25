using System.Globalization;

namespace TradeAgent.Core.Db;

/// <summary>
/// A BASE RATE AS THE APP MEASURED IT: <paramref name="Hits"/> of the <paramref name="Measured"/> records in the forecast's reference class
/// came out as its target, every one recorded BEFORE the forecast was written; <paramref name="Probability"/> is Laplace's
/// <c>(k+1)/(n+2)</c> of them, so an empty class reads one half and is said as "0 measured", never as a zero.
/// </summary>
public sealed record BaseRate(int Measured, int Hits, double Probability);

/// <summary>
/// ONE RESOLVED, SCORED FORECAST AS THE APP SCORES IT AT READ: its probability (the forecaster's one claim the scorer reads), the app's
/// outcome, the base rate measured before it, and the Brier and log scores of both. <see cref="Skill"/> is the base Brier less the
/// forecast's: positive only where the forecast beat what the records already said. <see cref="Version"/> is the manager version the
/// forecast was written under (<see cref="ForecastScorer.VersionName"/>).
/// </summary>
public sealed record ForecastScore(
    string ForecastId, string Version, string Predicate, double Probability, bool Outcome, BaseRate Base,
    double Brier, double BaseBrier, double Log, double BaseLog)
{
    public double Skill => BaseBrier - Brier;
}

/// <summary>
/// THE SCORER (<c>U-org-scorecards-a</c>, O8; ORGANISATION § 5: "skill is scored against the … base-rate forecaster, so a near-certain
/// forecast earns nothing"). Pure arithmetic over a probability, an outcome and a base rate, plus THE THREE BASE-RATE READS, each a named
/// constant a guard test reads (<see cref="BaseRateSql"/>), each cut at the forecast's <c>at</c> so nothing recorded after a forecast moves
/// its score (EVID-001):
/// <list type="bullet">
/// <item><c>verdict-class</c> — the versions first judged before the forecast; a hit is a first verdict of the target class.</item>
/// <item><c>mandate-state</c> — the mandates first closed before the forecast; a hit is one that reached the target no later than its first
/// closing event — the resolver's own rule (<see cref="ForecastResolver"/>), so <c>rejected</c>, which closes nothing, is measured too.</item>
/// <item><c>unit-admissible</c> — the units of the subject's kind chartered at least the forecast's horizon before it; a hit, for
/// <c>admitted</c>, is one with an admissible verdict class on a version a launch it FUNDED ran, within the horizon after its charter (<see cref="ForecastResolver.UnitAdmissibleSql"/>'s
/// joins: the run's version and attempt, the funding lineage written at admission, never a figure); for <c>none</c>, one without.</item>
/// </list>
/// It reads no claim but the forecast's own (its predicate, subject, target, dates and probability) and stores nothing: a score is computed
/// each time it is read (ORGANISATION § 8, "computed by the app at read").
/// </summary>
public static class ForecastScorer
{
    /// <summary>The log score's clamp: a probability of 0 or 1 is scored as 0.001 or 0.999, so one confident miss is a large loss, never −∞.</summary>
    public const double LogFloor = 0.001, LogCeiling = 0.999;

    /// <summary>The versions FIRST judged before the forecast, and how many of those first verdicts were the target class.</summary>
    public const string VerdictClassBaseSql =
        "SELECT COUNT(*), COALESCE(SUM(p.verdict = $target), 0) FROM strategy_promotion p WHERE p.at < $at "
        + "AND NOT EXISTS (SELECT 1 FROM strategy_promotion q WHERE q.version_id = p.version_id AND (q.at < p.at OR (q.at = p.at AND q.rowid < p.rowid)))";

    /// <summary>
    /// The mandates FIRST closed before the forecast (<see cref="MandateEventKind.Closing"/>, bound as <c>$closing0…</c>), and how many
    /// reached the target no later than that first closing event.
    /// </summary>
    public static readonly string MandateStateBaseSql =
        $"SELECT COUNT(*), COALESCE(SUM(EXISTS (SELECT 1 FROM org_mandate_event t WHERE t.mandate_id = c.mandate_id AND t.kind = $target AND t.id <= c.id)), 0) "
        + $"FROM org_mandate_event c WHERE c.kind IN ({ClosingParams}) AND c.at < $at "
        + $"AND NOT EXISTS (SELECT 1 FROM org_mandate_event e WHERE e.mandate_id = c.mandate_id AND e.kind IN ({ClosingParams}) AND e.id < c.id)";

    /// <summary>
    /// The units of the subject's kind chartered by <c>$chartered</c> (the forecast's <c>at</c> less its horizon), each with the earliest
    /// moment a version a launch it funded ran had an admissible verdict after its charter — the later of the verdict and the run, both
    /// before the forecast. Whether that moment is inside the unit's own horizon is decided by <see cref="BaseRateOf"/>.
    /// </summary>
    public const string UnitAdmissibleBaseSql =
        "SELECT u.created_at, (SELECT MIN(MAX(p.at, r.created_at)) FROM ai_attempt_funding f JOIN strategy_run r ON r.attempt = f.attempt_id "
        + "JOIN strategy_promotion p ON p.version_id = r.version_id WHERE f.unit_id = u.id AND p.verdict IN ($promoted, $paper) "
        + "AND p.at > u.created_at AND p.at < $at AND r.created_at < $at) "
        + "FROM org_unit u WHERE u.kind = (SELECT k.kind FROM org_unit k WHERE k.id = $subject) AND u.created_at <= $chartered";

    /// <summary>The subject unit's kind, so an unknown reference class is said and never measured as empty.</summary>
    public const string SubjectKindSql = "SELECT kind FROM org_unit WHERE id = $subject";

    /// <summary>The three base-rate reads, and nothing else the scorer reads to measure a reference class.</summary>
    public static readonly string[] BaseRateSql = [VerdictClassBaseSql, MandateStateBaseSql, UnitAdmissibleBaseSql, SubjectKindSql];

    static string ClosingParams => string.Join(", ", MandateEventKind.Closing.Select((_, i) => "$closing" + i.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Laplace's rule of succession: <c>(k+1)/(n+2)</c>.</summary>
    public static double Laplace(int hits, int measured) => (hits + 1.0) / (measured + 2.0);

    /// <summary>The Brier score of <paramref name="p"/> for an outcome: <c>(p−o)²</c>, 0 the best.</summary>
    public static double Brier(double p, bool outcome) => (p - (outcome ? 1.0 : 0.0)) * (p - (outcome ? 1.0 : 0.0));

    /// <summary>The log score of <paramref name="p"/> for an outcome, <paramref name="p"/> clamped to [<see cref="LogFloor"/>, <see cref="LogCeiling"/>].</summary>
    public static double Log(double p, bool outcome)
    {
        var c = Math.Clamp(p, LogFloor, LogCeiling);
        return Math.Log(outcome ? c : 1.0 - c);
    }

    /// <summary>ONE SCORE: the forecast's probability and the base rate, each scored against the app's outcome.</summary>
    public static ForecastScore Score(string forecastId, string version, string predicate, double probability, bool outcome, BaseRate baseRate) =>
        new(forecastId, version, predicate, probability, outcome, baseRate,
            Brier(probability, outcome), Brier(baseRate.Probability, outcome), Log(probability, outcome), Log(baseRate.Probability, outcome));

    /// <summary>
    /// THE BASE RATE OF ONE FORECAST, measured from the records stamped before <paramref name="at"/>, inside the caller's read — or null
    /// with <paramref name="refusal"/> in words where the reference class cannot be measured (a predicate this build does not know, a
    /// subject unit not on the chart): said, never read as an empty class.
    /// </summary>
    internal static BaseRate? BaseRateOf(Database db, string predicate, string subject, string target, DateTimeOffset at, DateTimeOffset by,
        out string? refusal)
    {
        refusal = null;
        if (predicate == ForecastVocabulary.VerdictClass)
            return Counted(db, VerdictClassBaseSql, ("$target", target), ("$at", Sql.T(at)));
        if (predicate == ForecastVocabulary.MandateState)
            return Counted(db, MandateStateBaseSql,
                [("$target", target), ("$at", Sql.T(at)), .. MandateEventKind.Closing.Select((k, i) => ("$closing" + i.ToString(CultureInfo.InvariantCulture), (object?)k))]);
        if (predicate == ForecastVocabulary.UnitAdmissible)
        {
            using (var k = db.Cmd(SubjectKindSql, ("$subject", subject)))
                if (k.ExecuteScalar() is not string)
                {
                    refusal = $"its reference class cannot be measured: unit {subject} is not on the chart.";
                    return null;
                }
            var horizon = by - at;
            using var c = db.Cmd(UnitAdmissibleBaseSql, ("$subject", subject), ("$at", Sql.T(at)), ("$chartered", Sql.T(at - horizon)),
                ("$promoted", PromotionVerdict.Promoted), ("$paper", PromotionVerdict.PaperEligible));
            using var r = c.ExecuteReader();
            int measured = 0, admitted = 0;
            while (r.Read())
            {
                measured++;
                if (Sql.TimeN(r.GetValue(1)) is { } first && first <= Sql.Time(r.GetValue(0)) + horizon) admitted++;
            }
            // A HIT IS THE TARGET: an admission for `admitted`, none for `none` (the Codex review of this unit).
            var hits = target == ForecastVocabulary.NoneAdmitted ? measured - admitted : admitted;
            return new BaseRate(measured, hits, Laplace(hits, measured));
        }
        refusal = $"'{predicate}' is no predicate whose reference class this build measures.";
        return null;
    }

    static BaseRate Counted(Database db, string sql, params (string, object?)[] ps)
    {
        using var c = db.Cmd(sql, ps);
        using var r = c.ExecuteReader();
        r.Read();
        var (measured, hits) = (Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture), Convert.ToInt32(r.GetValue(1), CultureInfo.InvariantCulture));
        return new BaseRate(measured, hits, Laplace(hits, measured));
    }

    /// <summary>The name of the row a forecast's manager version is kept under: the runtime and the effective model of its attempt.</summary>
    public static string VersionName(string? runtime, string? model) => (runtime, model) switch
    {
        (null, null) => NoVersion,
        ({ } rt, null) => $"{rt}, no model named",
        (null, { } m) => $"no runtime recorded, model {m}",
        ({ } rt, { } m) => $"{rt}, model {m}"
    };

    /// <summary>The row of forecasts whose attempt recorded neither a runtime nor a model, or that were written under no attempt.</summary>
    public const string NoVersion = "no manager version recorded";
}

/// <summary>
/// ONE MANAGER VERSION'S CALIBRATION (<c>U-org-scorecards-a</c> a2; ORGANISATION § 5, "per manager version"): the runtime and effective
/// model the forecasts' attempts recorded (<see cref="Name"/>), the number of resolved, scored forecasts and the SUMS of their scores —
/// every mean is a sum over <see cref="Scored"/>, never a mean of means — and, apart and never in a score, the forecasts resolved void,
/// written unscored, or still open.
/// </summary>
public sealed record ManagerVersionScore(
    string? Runtime, string? Model, string Name, int Scored, double BrierSum, double BaseBrierSum, double LogSum, double BaseLogSum,
    int Void, int Unscored, int Open)
{
    /// <summary>The mean Brier score, or null with nothing scored (never a zero).</summary>
    public double? Brier => Scored == 0 ? null : BrierSum / Scored;

    /// <summary>The base rate's mean Brier score over the same forecasts, or null.</summary>
    public double? BaseBrier => Scored == 0 ? null : BaseBrierSum / Scored;

    /// <summary>The mean skill: base Brier less Brier, or null.</summary>
    public double? Skill => Scored == 0 ? null : (BaseBrierSum - BrierSum) / Scored;

    /// <summary>The mean log score, or null.</summary>
    public double? Log => Scored == 0 ? null : LogSum / Scored;

    /// <summary>The base rate's mean log score, or null.</summary>
    public double? BaseLog => Scored == 0 ? null : BaseLogSum / Scored;

    internal static ManagerVersionScore Empty(string? runtime, string? model, string name) => new(runtime, model, name, 0, 0, 0, 0, 0, 0, 0, 0);

    internal ManagerVersionScore Add(ForecastScore s) => this with
    {
        Scored = Scored + 1, BrierSum = BrierSum + s.Brier, BaseBrierSum = BaseBrierSum + s.BaseBrier, LogSum = LogSum + s.Log,
        BaseLogSum = BaseLogSum + s.BaseLog
    };

    internal ManagerVersionScore Plus(ManagerVersionScore o) => this with
    {
        Scored = Scored + o.Scored, BrierSum = BrierSum + o.BrierSum, BaseBrierSum = BaseBrierSum + o.BaseBrierSum, LogSum = LogSum + o.LogSum,
        BaseLogSum = BaseLogSum + o.BaseLogSum, Void = Void + o.Void, Unscored = Unscored + o.Unscored, Open = Open + o.Open
    };
}

/// <summary>
/// ONE POSITION'S SCORECARD, AS COMPUTED AT READ: a row per manager version (<see cref="Versions"/>, in the order each first forecast),
/// the position's <see cref="Total"/> summed over every row's forecasts, each resolved, scored forecast's score (<see cref="Scores"/>), and
/// in words each forecast the app could not score (<see cref="Refused"/>) — a vocabulary this build does not read, a probability that is
/// not one, a reference class it cannot measure.
/// </summary>
public sealed record PositionScorecard(
    string Position, IReadOnlyList<ManagerVersionScore> Versions, ManagerVersionScore Total, IReadOnlyList<ForecastScore> Scores,
    IReadOnlyList<string> Refused);

/// <summary>
/// ONE UNIT'S YIELD OVER A WINDOW (<c>U-org-scorecards-a</c> a3; ORGANISATION § 8), computed at read from its RECORDED funding chain — never
/// today's chart: <see cref="Spend"/> is every attempt started in the window whose admission recorded the unit in its chain;
/// <see cref="Cost"/> is its spend, or null when a turn reported no cost (<see cref="CostWords"/> says so — an unknown cost is never a zero);
/// the mandates the unit opened, and those accepted and expired, in the window; the distinct versions given an admissible verdict class in
/// the window that a launch the unit funded had run by then; those per AI dollar, or null with the reason; and its contribution, which
/// needs a forward record no unit has yet.
/// </summary>
/// <para><see cref="Share"/> (<c>U-org-scorecards-b</c> b1): the AI share the rules give the unit beside the one it holds.</para>
public sealed record UnitYield(
    string Unit, DateTimeOffset From, DateTimeOffset To, AiAttemptTotals Spend, decimal? Cost, string CostWords,
    int MandatesOpened, int MandatesAccepted, int MandatesExpired, int Admissible, decimal? AdmissiblePerDollar, string PerDollarWords,
    string Contribution, UnitShare Share);

/// <summary>
/// A UNIT'S AI SHARE BY THE RULES AND AS HELD (<c>U-org-scorecards-b</c> b1; ORGANISATION § 8), read from today's chart: its equal part of
/// the exploration floor among its parent's <see cref="ActiveUnits"/> (<see cref="Floor"/>), the RULE share — that floor plus an equal
/// part of the other 75 %, which splits equally until a unit has 90 forward days and none has — and the <see cref="Actual"/> share its
/// row holds (null: no own cap). Null rule and floor for the organisation itself, whose envelope is the board's day-total, and for a
/// unit that is not active. <see cref="Words"/> says all of it.
/// </summary>
public sealed record UnitShare(int ActiveUnits, decimal? Floor, decimal? Rule, string? Actual, string Words);

/// <summary>
/// THE SCORECARDS (<c>U-org-scorecards-a</c>, O8; ORGANISATION § 8): computed by the app at read from its own resolutions and
/// measurements, NEVER STORED — this class writes nothing, so nothing a position writes can move its own score. It reads the forecast's
/// own row, the app's resolution of it, the manager version its attempt recorded, and the base rates (<see cref="ForecastScorer"/>).
/// </summary>
public sealed class Scorecards(Database db)
{
    /// <summary>
    /// The forecasts <c>$position</c> wrote, oldest first: the forecast's own columns, the app's outcome (NULL while open) and the runtime
    /// and effective model of the attempt it was written under.
    /// </summary>
    public const string ForecastsSql =
        "SELECT f.id, f.vocabulary, f.predicate, f.subject, f.target, f.resolve_by, f.probability, f.scored, f.at, a.runtime, a.effective_model, "
        + "r.outcome FROM org_forecast f LEFT JOIN ai_attempt a ON a.id = f.attempt_id LEFT JOIN org_forecast_resolution r ON r.forecast_id = f.id "
        + "WHERE f.forecaster = $position ORDER BY f.at, f.rowid";

    /// <summary>
    /// The scorecard of <paramref name="position"/>, computed now from the records. A forecast enters a score only when the app wrote it
    /// scored and resolved it yes or no; one written unscored, resolved void or still open is counted apart in its version's row.
    /// </summary>
    public PositionScorecard Of(string position) => db.Read(_ =>
    {
        var scores = new List<ForecastScore>();
        var refused = new List<string>();
        var versions = new List<ManagerVersionScore>();
        using var c = db.Cmd(ForecastsSql, ("$position", position));
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            var id = r.GetString(0);
            var vocabulary = r.GetInt32(1);
            var predicate = r.GetString(2);
            var (runtime, model) = (Sql.S(r.GetValue(9)), Sql.S(r.GetValue(10)));
            var name = ForecastScorer.VersionName(runtime, model);
            var row = versions.FindIndex(v => v.Name == name);
            if (row < 0)
            {
                versions.Add(ManagerVersionScore.Empty(runtime, model, name));
                row = versions.Count - 1;
            }
            if (!ForecastVocabulary.IsKnown(vocabulary, predicate))
            {
                refused.Add(OrgStore.UnknownVocabulary(id, vocabulary) + " It is not scored.");
                continue;
            }
            var outcome = Sql.S(r.GetValue(11));
            if (r.GetInt64(7) == 0) { versions[row] = versions[row] with { Unscored = versions[row].Unscored + 1 }; continue; }
            if (outcome is null) { versions[row] = versions[row] with { Open = versions[row].Open + 1 }; continue; }
            if (outcome == ForecastOutcome.Void) { versions[row] = versions[row] with { Void = versions[row].Void + 1 }; continue; }
            if (r.GetValue(6) is not string text || !ForecastVocabulary.IsProbabilityText(text)
                || !decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var probability))
            {
                refused.Add($"forecast {id} holds a probability that is not a decimal text from 0 to 1: it is not scored, never guessed.");
                continue;
            }
            var at = Sql.Time(r.GetValue(8));
            if (ForecastScorer.BaseRateOf(db, predicate, r.GetString(3), r.GetString(4), at, Sql.Time(r.GetValue(5)), out var why) is not { } baseRate)
            {
                refused.Add($"forecast {id} is not scored: {why}");
                continue;
            }
            var score = ForecastScorer.Score(id, name, predicate, (double)probability, outcome == ForecastOutcome.Yes, baseRate);
            scores.Add(score);
            versions[row] = versions[row].Add(score);
        }
        var total = versions.Aggregate(ManagerVersionScore.Empty(null, null, "every manager version"), (t, v) => t.Plus(v));
        return new PositionScorecard(position, versions, total, scores, refused);
    });

    /// <summary>The mandates a unit opened in the window, and the accepted and expired events on its mandates in the window.</summary>
    public const string UnitMandatesSql =
        "SELECT (SELECT COUNT(*) FROM org_mandate m WHERE m.unit_id = $unit AND m.created_at >= $from AND m.created_at < $to), "
        + "(SELECT COUNT(*) FROM org_mandate_event e JOIN org_mandate m ON m.id = e.mandate_id WHERE m.unit_id = $unit AND e.kind = $accepted "
        + "AND e.at >= $from AND e.at < $to), "
        + "(SELECT COUNT(*) FROM org_mandate_event e JOIN org_mandate m ON m.id = e.mandate_id WHERE m.unit_id = $unit AND e.kind = $expired "
        + "AND e.at >= $from AND e.at < $to)";

    /// <summary>
    /// The distinct versions given an admissible verdict class in the window that a launch whose RECORDED funding chain holds the unit had
    /// run by the verdict — <see cref="ForecastResolver.UnitAdmissibleSql"/>'s joins: the run's version and attempt, never a figure.
    /// </summary>
    public const string UnitAdmissibleYieldSql =
        "SELECT COUNT(DISTINCT p.version_id) FROM strategy_promotion p WHERE p.verdict IN ($promoted, $paper) AND p.at >= $from AND p.at < $to "
        + "AND EXISTS (SELECT 1 FROM strategy_run r JOIN ai_attempt_funding f ON f.attempt_id = r.attempt WHERE r.version_id = p.version_id "
        + "AND f.unit_id = $unit AND r.created_at <= p.at)";

    /// <summary>What a unit's contribution says until a forward record exists to measure it (ORGANISATION § 8: from ≥ 90 forward days).</summary>
    public const string NoForwardRecord =
        "no forward record: a contribution to the book is measured from 90 forward days, and none is recorded for this unit";

    /// <summary>The yield of <paramref name="unit"/> over [<paramref name="from"/>, <paramref name="to"/>), computed now; refused in words for a unit not on the chart.</summary>
    /// <exception cref="TradeAgentException">The unit is not on the chart.</exception>
    public UnitYield OfUnit(string unit, DateTimeOffset from, DateTimeOffset to) => db.Read(_ =>
    {
        using (var k = db.Cmd(ForecastScorer.SubjectKindSql, ("$subject", unit)))
            if (k.ExecuteScalar() is not string)
                throw new TradeAgentException(ErrorCode.INVALID_REQUEST, $"'{unit}' is no unit on the chart: it has no yield to read.");

        var spend = new AiAttemptStore(db).UnitTotalsBetween(from, to, unit);
        decimal? cost = spend.Unpriced > 0 ? null : spend.Spent;
        var costWords = spend.Unpriced > 0
            ? $"unknown: {Count(spend.Unpriced, "turn")} funded by this unit reported no cost"
            : spend.Turns == 0 && spend.Open == 0
                ? "no AI turn was funded by this unit in the window"
                : spend.Spent.ToString("0.####", CultureInfo.InvariantCulture)
                  + (spend.Unreported > 0 ? $", with {Count(spend.Unreported, "turn")} charged at its reservation because its usage was never reported" : "")
                  + (spend.Open > 0 ? $", with {Count(spend.Open, "turn")} still in flight and not in it" : "");

        int opened, accepted, expired, admissible;
        using (var m = db.Cmd(UnitMandatesSql, ("$unit", unit), ("$from", Sql.T(from)), ("$to", Sql.T(to)),
                   ("$accepted", MandateEventKind.Accepted), ("$expired", MandateEventKind.Expired)))
        using (var r = m.ExecuteReader())
        {
            r.Read();
            (opened, accepted, expired) = (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2));
        }
        using (var a = db.Cmd(UnitAdmissibleYieldSql, ("$unit", unit), ("$from", Sql.T(from)), ("$to", Sql.T(to)),
                   ("$promoted", PromotionVerdict.Promoted), ("$paper", PromotionVerdict.PaperEligible)))
            admissible = Convert.ToInt32(a.ExecuteScalar(), CultureInfo.InvariantCulture);

        decimal? perDollar = cost is > 0m ? admissible / cost.Value : null;
        var perDollarWords = cost is null ? "not known: the unit's cost is not known"
            : cost == 0m ? "not measured: no AI dollar was spent"
            : perDollar!.Value.ToString("0.####", CultureInfo.InvariantCulture);
        return new UnitYield(unit, from, to, spend, cost, costWords, opened, accepted, expired, admissible, perDollar, perDollarWords, NoForwardRecord,
            ShareOf(unit));
    });

    /// <summary>The rule share beside the actual one (<see cref="UnitShare"/>), from today's chart.</summary>
    UnitShare ShareOf(string unitId)
    {
        var units = new OrgStore(db).Units();
        var unit = units.Single(u => u.Id == unitId);
        var actual = unit.EnvelopeShare;
        var held = actual is null
            ? OrgEnvelopes.LegacyRoleOf(unit.Id) is not null ? "the split on the Safety page" : "no own cap — it spends inside the room above it"
            : actual;
        if (unit.ParentId is null)
            return new UnitShare(0, null, null, actual, "the organisation's own envelope is the security board's day-total: no rule share.");
        var n = units.Count(u => u.ParentId == unit.ParentId && u.Status == OrgUnitStatus.Active);
        if (unit.Status != OrgUnitStatus.Active)
            return new UnitShare(n, null, null, actual, $"not active ({unit.Status}): the exploration floor and the rule share are the active units'; held {held}.");
        var floor = OrgStore.FloorShare(n);
        var rule = floor + (1m - OrgBounds.ExplorationFloor) / n;
        var words = $"rule share {OrgStore.Words(rule)} of {OrgEnvelopes.NameOf(unit.ParentId)}'s envelope — the exploration floor's equal "
                    + $"{OrgStore.Words(floor)} among {n.ToString(CultureInfo.InvariantCulture)} active unit{(n == 1 ? "" : "s")} and an equal part "
                    + "of the rest, which splits equally until a unit has 90 forward days (none has) — held " + held
                    + (actual is not null && OrgEnvelopes.Fraction(unit.Id, actual) < floor ? ", below the floor" : "") + ".";
        return new UnitShare(n, floor, rule, actual, words);
    }

    static string Count(int n, string noun) => $"{n.ToString(CultureInfo.InvariantCulture)} {noun}{(n == 1 ? "" : "s")}";
}

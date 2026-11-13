using System.Globalization;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;
using static TradeAgent.Tests.Unit.OrgSurfaceSeed;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A SCORECARD'S BASE RATES ARE MEASURED ONCE (<c>U-perf-scorecard</c>; R31 D17, G3). Every page refresh read every position's scorecard and
/// measured each resolved forecast's base rate again, under the one database lock, at a cost of resolved forecasts × mandate events. A base
/// rate reads only the records stamped before its forecast, so <see cref="BaseRateMemo"/> keeps it once measured — and these tests hold it
/// to saying exactly what a new measure says: a record stamped before a kept forecast, in any of the five tables the base-rate reads read,
/// has it measured again; a record stamped after moves nothing and is measured never; and nothing a write may roll back is kept.
/// The reference for "a new measure" is a second handle on the same file, whose memo is its own and empty.
/// </summary>
public class ScorecardBaseRateMemoTests
{
    /// <summary>The moment the forecasts are written: the records before it are each base rate's reference class.</summary>
    static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    static readonly DateTimeOffset By = T0.AddDays(2);

    /// <summary>Inside the unit-admissible horizon of a team chartered at <see cref="OrgSurfaceSeed.Stamp"/> (two days).</summary>
    static readonly DateTimeOffset Early = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

    static string T(DateTimeOffset t) => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    static void Run(Database db, string id, string version, string attempt, DateTimeOffset at)
    {
        db.Exec("PRAGMA foreign_keys=OFF");
        Exec(db, """
            INSERT INTO strategy_run(id, version_id, dataset_id, dataset_sha256, execution_model, outcome, bars, trades, wins, intents, fills,
                                     exposure_bars, missing_minutes, faults, trace_sha256, created_at, role, attempt)
            VALUES($id, $v, 1, 's', 'm', 'completed', 1, 0, 0, 0, 0, 0, 0, 0, 't', $at, 'research', $attempt)
            """, ("$id", id), ("$v", version), ("$at", T(at)), ("$attempt", attempt));
        db.Exec("PRAGMA foreign_keys=ON");
    }

    static void Judged(Database db, string version, string verdict, DateTimeOffset at)
    {
        OrgScorecardTests.Version(db, version);
        OrgScorecardTests.Verdict(db, version, verdict, at);
    }

    static void Event(Database db, string mandate, string kind, DateTimeOffset at) =>
        Exec(db, "INSERT INTO org_mandate_event(mandate_id, at, kind, actor) VALUES($m,$at,$k,$a)",
            ("$m", mandate), ("$at", T(at)), ("$k", kind), ("$a", Head));

    /// <summary>
    /// The chart, a reference class for each predicate, and three forecasts at <see cref="T0"/>, resolved: the head's on its team's
    /// admissions and on a version, the other head's on a mandate. Returns two launches: one that has run a version given an admissible
    /// verdict but funds nothing yet (a later funding row may fund it), and one Team funds that has run nothing yet (a later run may).
    /// </summary>
    static (string Unfunded, string Funded) Seed(Database db)
    {
        Chart(db);
        Judged(db, "ver-p1", PromotionVerdict.Promoted, T0.AddDays(-3));
        Judged(db, "ver-r1", PromotionVerdict.Refused, T0.AddDays(-2));
        Mandate(db, "man-a1", Head, Member, MandateEventKind.Issued, MandateEventKind.Accepted);
        Mandate(db, "man-e1", Head, Member, MandateEventKind.Issued, MandateEventKind.Expired);
        Mandate(db, "man-open", Head, Member, MandateEventKind.Issued);
        // A version Team will be found to have funded, once a run of it or a funding row is recorded (the tests add one or the other).
        Judged(db, "ver-u", PromotionVerdict.Promoted, Early.AddHours(6));
        var unfunded = Launch(db, Member, T0.AddDays(-20), unit: null);
        Run(db, "run-unfunded", "ver-u", unfunded, Early);
        var funded = Launch(db, Member, T0.AddDays(-20));

        OrgScorecardTests.Version(db, "ver-x");
        var org = new OrgStore(db);
        org.Decide(Head, "turn-test", OrgDecisionKind.Routine, ["forecast it"], "forecast it", "reasons", [],
            [
                new ForecastClaim(ForecastVocabulary.UnitAdmissible, Team, ForecastVocabulary.Admitted, By, 0.8m),
                new ForecastClaim(ForecastVocabulary.VerdictClass, "ver-x", PromotionVerdict.Promoted, By, 0.8m)
            ], T0);
        org.Decide(Other, "turn-test", OrgDecisionKind.Routine, ["forecast it"], "forecast it", "reasons", [],
            [new ForecastClaim(ForecastVocabulary.MandateState, "man-open", MandateEventKind.Accepted, By, 0.7m)], T0);
        OrgScorecardTests.Verdict(db, "ver-x", PromotionVerdict.Promoted, T0.AddHours(2));
        Event(db, "man-open", MandateEventKind.Accepted, T0.AddHours(3));
        Assert.Empty(new ForecastResolver(db).Sweep(By.AddMinutes(1)).Refused);
        return (unfunded, funded);
    }

    /// <summary>Every base rate of both heads' scorecards, read through <paramref name="db"/>'s handle.</summary>
    static IReadOnlyList<(string Id, BaseRate Base)> Bases(Database db) =>
        [.. new[] { Head, Other }.SelectMany(p => new Scorecards(db).Of(p).Scores).Select(s => (s.ForecastId, s.Base)).OrderBy(s => s.ForecastId, StringComparer.Ordinal)];

    /// <summary>The same, measured new: through a second handle on the same file, whose memo is its own and empty.</summary>
    static IReadOnlyList<(string Id, BaseRate Base)> Fresh(Database db)
    {
        using var fresh = new Database(db.FilePath);
        return Bases(fresh);
    }

    static long Measured(Database db) => BaseRateMemo.Of(db).Measured;

    /// <summary>
    /// A REFRESH MEASURES NO BASE RATE AGAIN: the first read measures the three; every read after hands back the same three without
    /// measuring, and records stamped after the forecasts — a verdict, mandate events, a unit, a run and a funding row — measure nothing
    /// again and move nothing. RED before the unit: there was no memo, and every read measured every base rate (the counter does not exist
    /// at base). The watched mutant: <see cref="BaseRateMemo.Kept"/> answering null — every read measures again, Measured reads 9, not 3.
    /// </summary>
    [Fact]
    public void A_refresh_measures_each_base_rate_once()
    {
        using var db = TestEnv.NewDb();
        Seed(db);
        var first = Bases(db);
        Assert.Equal(3, first.Count);
        Assert.Equal(3, Measured(db));
        Assert.Equal(first, Bases(db));
        Assert.Equal(3, Measured(db));

        // AFTER THE FORECASTS: records no base rate may count.
        Judged(db, "ver-late", PromotionVerdict.Refused, T0.AddHours(5));
        Mandate(db, "man-late", Head, Member);                                     // its events stamped by the test, after the forecasts
        Event(db, "man-late", MandateEventKind.Issued, T0.AddHours(5));
        Event(db, "man-late", MandateEventKind.Expired, T0.AddHours(5));
        Exec(db, "INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES('team-5dc0ffee',$p,'team',NULL,'active',$at)",
            ("$p", OrgIds.Research), ("$at", T(T0.AddHours(5))));
        Run(db, "run-late", "ver-u", Launch(db, Member, T0.AddHours(5)), T0.AddHours(6));

        Assert.Equal(first, Bases(db));
        Assert.Equal(3, Measured(db));
        Assert.Equal(Fresh(db), Bases(db));
    }

    /// <summary>
    /// A RECORD STAMPED BEFORE A KEPT FORECAST HAS IT MEASURED AGAIN, from each of the five tables the base-rate reads read: a verdict, a
    /// mandate's closing event, a unit chartered, a run of a version a funded launch ran, and a funding row for a launch that ran one. Each
    /// moves its forecast's base rate (the first read differs from the next), and every read says what a new measure says.
    /// The watched mutant: <see cref="BaseRateMemo.Check"/> not dropping — every case reads the first read's rate.
    /// </summary>
    [Theory]
    [InlineData("verdict")]
    [InlineData("mandate-event")]
    [InlineData("unit")]
    [InlineData("run")]
    [InlineData("funding")]
    public void A_record_stamped_before_a_kept_forecast_has_it_measured_again(string table)
    {
        using var db = TestEnv.NewDb();
        var (unfunded, funded) = Seed(db);
        var first = Bases(db);

        switch (table)
        {
            case "verdict":
                Judged(db, "ver-backdated", PromotionVerdict.Promoted, T0.AddHours(-1));
                break;
            case "mandate-event":
                Mandate(db, "man-backdated", Head, Member, MandateEventKind.Issued);
                Event(db, "man-backdated", MandateEventKind.Accepted, T0.AddHours(-1));
                break;
            case "unit":
                HeadAUnit(db, "pos-5dc0ffee0001", "team-5dc0ffee", OrgIds.Research);
                break;
            case "run":
                Run(db, "run-funded", "ver-u", funded, Early);
                break;
            case "funding":
                Exec(db, "INSERT INTO ai_attempt_funding(attempt_id, unit_id, depth) VALUES($a, $u, 0)", ("$a", unfunded), ("$u", Team));
                break;
        }

        var after = Bases(db);
        Assert.NotEqual(first, after);
        Assert.Equal(Fresh(db), after);
        Assert.Equal(after, Bases(db));
    }

    /// <summary>
    /// A READ INSIDE A WRITE KEEPS NOTHING IT SAW: inside a write that records a verdict stamped before the forecasts, the scorecard says
    /// the moved rate; the write rolls back, and the read after says the records' own rate again — never the rolled-back one.
    /// The watched mutant: <see cref="Scorecards.Of"/> using the memo inside a write — the memo keeps the rolled-back rate and sets its
    /// mark past a rowid the next verdict takes again, so the read after hands back the rolled-back rate.
    /// </summary>
    [Fact]
    public void A_read_inside_a_write_keeps_nothing_it_saw()
    {
        using var db = TestEnv.NewDb();
        Seed(db);
        var first = Bases(db);

        IReadOnlyList<(string, BaseRate)>? inside = null;
        db.Exec("PRAGMA foreign_keys=OFF");                                      // a pragma inside a transaction does nothing: set it before
        Assert.Throws<InvalidOperationException>(() => db.Write<int>(_ =>
        {
            Judged(db, "ver-rolled-back", PromotionVerdict.Promoted, T0.AddHours(-1));
            inside = Bases(db);
            throw new InvalidOperationException("roll the write back");
        }));
        db.Exec("PRAGMA foreign_keys=ON");
        Assert.NotNull(inside);
        Assert.NotEqual(first, inside);

        Assert.Equal(first, Bases(db));
        Assert.Equal(Fresh(db), Bases(db));
    }

    /// <summary>
    /// THE CHECK WALKS NO TABLE (<see cref="BaseRateMemo.CheckSql"/>): it runs on every read that scores a forecast, so each of its reads of
    /// the five tables is an index seek on the rowid — the highest rowid, and the rows above the mark. The one walk it may make is of
    /// <c>strategy_run</c> by attempt, per funding row added since the last check, never per read: SQLite's own plan says so.
    /// The watched mutant: <c>org_mandate_event</c>'s rows above the mark found by <c>at</c> instead of <c>id</c> — a SCAN of it.
    /// </summary>
    [Fact]
    public void The_check_walks_no_table()
    {
        using var db = TestEnv.NewDb();
        var steps = db.Read(conn =>
        {
            using var explain = conn.CreateCommand();
            explain.CommandText = "EXPLAIN QUERY PLAN " + BaseRateMemo.CheckSql;
            foreach (var p in new[] { "$promotion", "$event", "$unit", "$run", "$funding" }) explain.Parameters.AddWithValue(p, 0L);
            using var r = explain.ExecuteReader();
            var all = new List<string>();
            while (r.Read()) all.Add(r.GetString(3));
            return all;
        });
        Assert.NotEmpty(steps);
        var walks = steps.Where(s => s.StartsWith("SCAN ", StringComparison.Ordinal) && s != "SCAN r" && !s.StartsWith("SCAN CONSTANT", StringComparison.Ordinal)).ToList();
        Assert.True(walks.Count == 0, "the check walks a table:\n" + string.Join('\n', steps));
    }
}

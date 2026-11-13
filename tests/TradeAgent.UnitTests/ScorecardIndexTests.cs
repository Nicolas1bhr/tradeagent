using System.Text.RegularExpressions;
using TradeAgent.Core.Db;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE SCORECARDS' READS GO THROUGH AN INDEX (<c>U-perf-scan</c>, R28 G3). A page with a scorecard rebuilds it on every refresh, and
/// its reads of <c>org_forecast</c> by forecaster and of <c>org_mandate_event</c> by kind walked every row of those tables, which only
/// grow. SQLite's <c>EXPLAIN QUERY PLAN</c> says how each statement reads: a <c>SCAN</c> of either table is a read of all of it.
/// </summary>
public class ScorecardIndexTests(ITestOutputHelper log)
{
    static readonly (string Name, string Sql)[] Reads =
    [
        ("a position's forecasts", Scorecards.ForecastsSql),
        ("the mandate-state base rate", ForecastScorer.MandateStateBaseSql),
        ("a unit's mandates in a window", Scorecards.UnitMandatesSql),
    ];

    [Fact]
    public void No_scorecard_read_walks_every_forecast_or_every_mandate_event()
    {
        using var db = TestEnv.NewDb();
        var wrong = new List<string>();
        db.Read(conn =>
        {
            foreach (var (name, sql) in Reads)
            {
                using var explain = conn.CreateCommand();
                explain.CommandText = "EXPLAIN QUERY PLAN " + sql;
                foreach (var p in Regex.Matches(sql, @"\$[A-Za-z0-9_]+").Select(m => m.Value).Distinct())
                    explain.Parameters.AddWithValue(p, "x");
                using var r = explain.ExecuteReader();
                while (r.Read())
                {
                    var step = r.GetString(3);
                    log.WriteLine($"{name}: {step}");
                    if (Regex.IsMatch(step, @"^SCAN (org_forecast|org_mandate_event)\b|^SCAN [a-z]$") && !IsScanOfAnotherTable(sql, step))
                        wrong.Add($"{name}: {step}");
                }
            }
            return 0;
        });
        Assert.True(wrong.Count == 0, "scorecard reads that walk a whole table:\n" + string.Join('\n', wrong));
    }

    /// <summary>A one-letter alias's scan is a whole-table read only when the alias is one of the two growing tables.</summary>
    static bool IsScanOfAnotherTable(string sql, string step)
    {
        var alias = Regex.Match(step, @"^SCAN ([a-z])$");
        if (!alias.Success) return false;
        return !Regex.IsMatch(sql, $@"\b(org_forecast|org_mandate_event) {alias.Groups[1].Value}\b");
    }
}

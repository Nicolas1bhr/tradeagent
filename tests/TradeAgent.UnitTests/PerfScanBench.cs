using System.Diagnostics;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

// SCRATCH BENCH (U-perf-scan): not committed.
public class PerfScanBench(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1000)]
    [InlineData(4900)]
    [InlineData(6000)]
    public void Bench(int n)
    {
        var root = Path.Combine(TestEnv.Home, $"perf-{Guid.NewGuid():n}");
        var data = Path.Combine(root, "agent", "data");
        for (var i = 0; i < n; i++)
        {
            var d = Path.Combine(data, $"d{i / 500}");
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, $"f{i}.csv"), $"row {i}");
        }
        var db = TestEnv.NewDb();
        ScanResult Pass(string label)
        {
            var c0 = db.Commits;
            var sw = Stopwatch.StartNew();
            var r = new MaterialScanner(db, root, _ => true) { HashesPerPass = 0 }.Scan();
            output.WriteLine($"n={n} {label}: {sw.ElapsedMilliseconds} ms, commits {db.Commits - c0}, seen {r.Seen} added {r.Added} removed {r.Removed} truncated {r.HashBudgetSpent} window {db.GetKv("material_scan_at")}");
            return r;
        }
        Pass("first");
        Pass("unchanged-1");
        Pass("unchanged-2");
        File.Delete(Path.Combine(data, "d0", "f0.csv"));
        Pass("after-delete");
        Assert.True(true);
    }
}

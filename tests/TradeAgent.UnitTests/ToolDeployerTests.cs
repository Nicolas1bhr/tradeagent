using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// The agent's only route to the gateway is the trade CLI. These cover the way it was broken on a
/// real machine: the launcher was deployed without the assemblies it loads, so it started and threw
/// on every invocation — while the health row said READY because a file with the right name existed.
/// </summary>
public class ToolDeployerTests
{
    /// <summary>A framework-dependent CLI layout: launcher, side-cars, and one referenced assembly.</summary>
    static string FakeCliBuild(params string[] dependencies)
    {
        var dir = Path.Combine(TestEnv.Home, $"cli-{Guid.NewGuid():n}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ToolDeployer.TradeCliName), "launcher");
        File.WriteAllText(Path.Combine(dir, "trade.dll"), "il");
        File.WriteAllText(Path.Combine(dir, "trade.runtimeconfig.json"), "{}");

        var runtime = dependencies.ToDictionary(d => $"lib/net10.0/{d}", _ => new { });
        File.WriteAllText(Path.Combine(dir, "trade.deps.json"), JsonSerializer.Serialize(new
        {
            targets = new Dictionary<string, object>
            {
                [".NETCoreApp,Version=v10.0"] = new Dictionary<string, object>
                {
                    ["trade/1.0.0"] = new { runtime = new Dictionary<string, object> { ["trade.dll"] = new { } } },
                    ["Dep/1.0.0"] = new { runtime }
                }
            }
        }));
        foreach (var d in dependencies) File.WriteAllText(Path.Combine(dir, d), "assembly");
        return dir;
    }

    static void ClearBin()
    {
        foreach (var f in Directory.GetFiles(Paths.Bin)) { try { File.Delete(f); } catch (IOException) { } }
    }

    [Fact]
    public void The_cli_is_deployed_with_the_assemblies_it_actually_loads()
    {
        // The defect: only trade.dll, trade.runtimeconfig.json and trade.deps.json were copied, so
        // trade.exe threw FileNotFoundException on TradeAgent.Core before it could do anything.
        ClearBin();
        var src = FakeCliBuild("TradeAgent.Core.dll", "Microsoft.Data.Sqlite.dll");

        var path = ToolDeployer.EnsureTradeCli(src);

        Assert.NotNull(path);
        Assert.True(File.Exists(Path.Combine(Paths.Bin, "TradeAgent.Core.dll")),
            "the CLI cannot start without the assemblies its deps.json names");
        Assert.True(File.Exists(Path.Combine(Paths.Bin, "Microsoft.Data.Sqlite.dll")));
        Assert.True(File.Exists(Path.Combine(Paths.Bin, "trade.deps.json")));
    }

    [Fact]
    public void A_cli_that_cannot_start_does_not_report_ready()
    {
        ClearBin();
        var src = FakeCliBuild("TradeAgent.Core.dll");
        ToolDeployer.EnsureTradeCli(src);
        Assert.True(ToolDeployer.TradeCliReady(out _));

        // Exactly the state found on the machine: launcher present, referenced assembly absent.
        File.Delete(Path.Combine(Paths.Bin, "TradeAgent.Core.dll"));

        Assert.False(ToolDeployer.TradeCliReady(out var reason));
        Assert.Contains("TradeAgent.Core.dll", reason);
    }

    [Fact]
    public void A_single_file_build_needs_no_side_cars_and_is_ready_on_its_own()
    {
        // The packaged build publishes self-contained single-file, which is why this was never seen
        // in a release. It must keep reporting ready with no deps.json beside it.
        ClearBin();
        var dir = Path.Combine(TestEnv.Home, $"cli-sf-{Guid.NewGuid():n}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ToolDeployer.TradeCliName), "everything inside");

        ToolDeployer.EnsureTradeCli(dir);

        Assert.True(ToolDeployer.TradeCliReady(out var reason), reason);
        Assert.False(File.Exists(Path.Combine(Paths.Bin, "trade.deps.json")));
    }

    [Fact]
    public void A_missing_cli_reports_why_rather_than_silently_passing()
    {
        ClearBin();
        Assert.False(ToolDeployer.TradeCliReady(out var reason));
        Assert.Contains("not installed", reason);
    }
}

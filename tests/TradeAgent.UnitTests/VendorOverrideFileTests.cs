using System.Text.RegularExpressions;
using TradeAgent.AgentRuntime;
using TradeAgent.Connectors.Atas;
using TradeAgent.Core;
using TradeAgent.Diagnostics;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// The two override files live at one path each under the assembly's single test home, so a class
/// that corrupts one is changing what every other class in this assembly would read. xUnit runs
/// classes in parallel; naming this collection on each of them is what stops a deliberate corruption
/// in one test from being an unexplained failure in another.
/// </summary>
public static class VendorOverrideFiles
{
    public const string Name = "vendor override files";
}

/// <summary>
/// AN OVERRIDE FILE THAT EXISTS AND DOES NOT PARSE IS NOT AN ABSENT ONE.
///
/// Milestone review 2026-09-05b, Codex F16 and the reviewer's UNVERIFIED 6. Both vendor-command
/// files — <c>runtimes.json</c> and <c>atas.json</c> — were read under a <c>catch (Exception)</c>
/// that returned the built-ins, so a truncated write or a hand-edit with one comma wrong put the
/// SHIPPED command back in place of the one the owner wrote, and nothing anywhere said so. That is
/// the failure mode `U-settings-closed` closed for the settings row, in a file whose whole reason to
/// exist is that the owner may need to change what TradeAgent runs: a restrictive `codex` manifest
/// with the sandbox-bypass flags taken out reverts, on one bad byte, to a built-in that carries
/// <c>--dangerously-bypass-approvals-and-sandbox</c>.
///
/// So an unreadable override is the MOST RESTRICTIVE override: no runtime is offered, no ATAS folder
/// is looked in, and the owner learns it from a health row and the Checks page in their own words.
/// A genuinely ABSENT file still means the built-ins — that is the shipped configuration, not a
/// failure — and that boundary is asserted here beside every refusal.
///
/// These write the two real files under the test home, so the classes that read them share one
/// collection and never run beside each other.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class VendorOverrideFileTests
{
    static void Corrupt(string path) => File.WriteAllText(path, "{ \"id\": \"codex\", ");

    static void Clean()
    {
        foreach (var p in new[] { RuntimeCatalog.OverridePath, AtasLayout.OverridePath })
            if (File.Exists(p)) File.Delete(p);
    }

    // ---- runtimes.json ---------------------------------------------------------------------------

    /// <summary>
    /// The finding. A restrictive override is in force; the file is then corrupted; and what the
    /// window would hand to <see cref="AgentSupervisor.PrepareAsync"/> must not be the built-in.
    /// </summary>
    [Fact]
    public void A_corrupt_runtimes_file_does_not_put_a_built_in_where_the_override_was()
    {
        try
        {
            RuntimeCatalog.SaveOverrides([new RuntimeManifest
            {
                Id = "codex", DisplayName = "OpenAI Codex CLI", Executable = "codex-locked",
                ExecArgs = ["exec", "{prompt}"], UnattendedArgs = []
            }]);

            var locked = RuntimeCatalog.Find("codex")!;
            Assert.Equal("codex-locked", locked.Executable);
            Assert.Empty(locked.UnattendedArgs);

            Corrupt(RuntimeCatalog.OverridePath);

            // MainWindow.StartOrStopAgentAsync resolves the manifest it starts through this call.
            Assert.Null(RuntimeCatalog.Find("codex"));
            // And nothing else is offered in its place either: an unreadable file is not a partial
            // one, so no manifest in it can be trusted, not only the one that was overridden.
            Assert.Empty(RuntimeCatalog.Load());
        }
        finally { Clean(); }
    }

    /// <summary>The boundary: no file at all is the shipped configuration and must stay one.</summary>
    [Fact]
    public void An_absent_runtimes_file_still_means_the_built_ins()
    {
        Clean();
        Assert.NotEmpty(RuntimeCatalog.Load());
        Assert.NotNull(RuntimeCatalog.Find("codex"));
        Assert.Null(RuntimeCatalog.Read().Unreadable);
    }

    /// <summary>
    /// Starting the AI is refused, in a sentence naming the file, and never with a built-in command.
    /// </summary>
    [Fact]
    public void Starting_the_agent_on_a_corrupt_runtimes_file_is_refused_in_the_owners_words()
    {
        try
        {
            Corrupt(RuntimeCatalog.OverridePath);

            var ex = Assert.Throws<TradeAgentException>(() => RuntimeCatalog.Require("codex"));
            Assert.Contains("runtimes.json", ex.Info.UserMessage);
            // Its OWN code. AI_RUNTIME_NOT_FOUND reads "The AI assistant program is not installed
            // yet" and offers to install it, which is the wrong morning entirely: the program is
            // there and the file describing it is what cannot be read.
            Assert.Equal(ErrorCode.RUNTIME_COMMANDS_UNREADABLE, ex.Info.Code);
            Assert.Contains("runtimes.json", ex.Info.Repair);
            // No path, no command, no console anywhere in what the owner reads.
            foreach (var forbidden in new[] { "\\", "/", "npm ", "cmd", "terminal", "Exception" })
            {
                Assert.DoesNotContain(forbidden, ex.Info.UserMessage);
                Assert.DoesNotContain(forbidden, ex.Info.Repair);
            }
        }
        finally { Clean(); }
    }

    /// <summary>The health row the owner is already looking at, and the Checks page beside it.</summary>
    [Fact]
    public async Task A_corrupt_runtimes_file_shows_on_the_health_row_and_the_checks_page()
    {
        try
        {
            Corrupt(RuntimeCatalog.OverridePath);

            var health = new HealthRegistry();
            var reporter = new RuntimeFileHealth();
            reporter.Report(health);
            Assert.Equal(HealthState.FAILED, health.Get(Components.AgentRuntime).State);
            Assert.Contains("runtimes.json", health.Get(Components.AgentRuntime).Detail);

            var report = await new Doctor(allowNetwork: false).RunAsync();
            var row = Assert.Single(report.Problems, p => p.Detail.Contains("runtimes.json"));
            Assert.Equal(HealthState.FAILED, row.State);
            Assert.False(string.IsNullOrWhiteSpace(row.UserAction));

            // And the row is given back the moment the file is put right, rather than standing
            // until something else happens to write it.
            Clean();
            reporter.Report(health);
            Assert.Equal(HealthState.UNKNOWN, health.Get(Components.AgentRuntime).State);
        }
        finally { Clean(); }
    }

    // ---- atas.json -------------------------------------------------------------------------------

    /// <summary>
    /// The same file, for ATAS. The restrictive thing an owner puts in <c>atas.json</c> is a folder:
    /// where the bridge is copied, and which process counts as the platform. A built-in standing in
    /// for that on one bad byte is how a bridge lands in a folder nobody is loading it from.
    /// </summary>
    [Fact]
    public void A_corrupt_atas_file_does_not_put_the_built_in_folders_where_the_override_was()
    {
        try
        {
            new AtasLayout
            {
                InstallDirCandidates = [Path.Combine(TestEnv.Home, "atas")],
                StrategyDirCandidates = [Path.Combine(TestEnv.Home, "atas", "Strategies")],
                ProcessNames = ["NotOft"], ExecutableNames = ["NotOft.exe"]
            }.Save();
            Assert.Equal(["NotOft"], AtasLayout.Load().ProcessNames);

            Corrupt(AtasLayout.OverridePath);

            // Machine-independent on purpose: the built-in candidates do not exist on this Mac, so
            // asserting on what Detect() FOUND would pass without the fix. This asserts on the
            // layout that would be searched.
            var layout = AtasLayout.Load();
            Assert.Empty(layout.InstallDirCandidates);
            Assert.Empty(layout.StrategyDirCandidates);
            Assert.Empty(layout.IndicatorDirCandidates);
            Assert.Empty(layout.ProcessNames);
            Assert.Empty(layout.ExecutableNames);
        }
        finally { Clean(); }
    }

    [Fact]
    public void An_absent_atas_file_still_means_the_built_in_folders()
    {
        Clean();
        Assert.NotEmpty(AtasLayout.Load().StrategyDirCandidates);
        Assert.Null(AtasLayout.Read().Unreadable);
    }

    /// <summary>
    /// The detection carries the reason, so both ATAS rows say it rather than reporting an absence
    /// they cannot know about — and the bridge is not copied into a guessed folder.
    /// </summary>
    [Fact]
    public void A_corrupt_atas_file_stops_the_detection_and_the_bridge_copy()
    {
        try
        {
            Corrupt(AtasLayout.OverridePath);

            var d = AtasInstallation.Detect();
            Assert.NotNull(d.LayoutUnreadable);
            Assert.Contains("atas.json", d.LayoutUnreadable!);
            Assert.False(d.Installed);

            foreach (var (state, detail) in new[]
                     {
                         AtasHealth.ProcessRow(true, d),
                         AtasHealth.BridgeRow(true, d, HealthState.FAILED, null, null)
                     })
            {
                Assert.Equal(HealthState.FAILED, state);
                Assert.Contains("atas.json", detail);
            }

            // The repair button is not offered: putting this build's bridge somewhere would mean
            // choosing the folder, which is the one thing that cannot be known right now.
            Assert.False(AtasHealth.RepairOffered(true, d, HealthState.FAILED, null));

            var ex = Assert.Throws<TradeAgentException>(
                () => AtasInstallation.InstallBridge(Path.Combine(TestEnv.Home, "bridge-src")));
            Assert.Contains("atas.json", ex.Info.UserMessage);
        }
        finally { Clean(); }
    }

    [Fact]
    public async Task A_corrupt_atas_file_shows_on_the_checks_page()
    {
        try
        {
            Corrupt(AtasLayout.OverridePath);
            var report = await new Doctor(allowNetwork: false).RunAsync();
            var row = Assert.Single(report.Problems, p => p.Detail.Contains("atas.json"));
            Assert.Equal(HealthState.FAILED, row.State);
            Assert.False(string.IsNullOrWhiteSpace(row.UserAction));
        }
        finally { Clean(); }
    }
}

/// <summary>
/// THE SWEEP, KEPT RATHER THAN REPORTED.
///
/// `U-typed-catalog`: <c>TypedWhileWorkingTests</c> built its conversation with
/// <c>RuntimeCatalog.Require</c>, which reads <c>runtimes.json</c> — the one file
/// <see cref="VendorOverrideFileTests"/> corrupts on purpose, under the one test home this assembly
/// shares. The class was not in this collection, so xUnit was free to run the two side by side, and
/// on ubuntu-latest (CI run 34040176577) it did: three tests red in a millisecond each, on a tree
/// this Mac and the Windows runner both passed. Nothing in the product was wrong, which is what made
/// it expensive to read.
///
/// A sweep of the assembly found no second offender. That fact has a shelf life of one commit, so it
/// is asserted here instead: every call below opens or replaces one of the two files, and a class
/// that makes one either joins this collection or is rewritten not to need the disk — the choice
/// <c>TypedWhileWorkingTests</c> took.
///
/// It reads SOURCE because a read cannot be seen from a running test, and it skips whole-line
/// comments so that prose about a call is not mistaken for one. It names these calls in its own
/// source, so it carries the attribute itself rather than needing an exception; the cost is that one
/// file-scan does not run in parallel. <c>AtasInstallation</c>'s overloads that are handed a layout
/// read nothing and are deliberately absent — only its no-argument forms go to disk.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class EveryClassThatTouchesAVendorFileSharesThisCollectionTests
{
    static readonly string[] Touches =
    [
        "RuntimeCatalog.Read(", "RuntimeCatalog.Load(", "RuntimeCatalog.Find(",
        "RuntimeCatalog.Require(", "RuntimeCatalog.SaveOverrides(", "RuntimeCatalog.OverridePath",
        "AtasLayout.Read(", "AtasLayout.Load(", "AtasLayout.OverridePath",
        "AtasInstallation.Detect()", "AtasInstallation.IsRunning()",
        "new Doctor(", "new RuntimeFileHealth("
    ];

    [Fact]
    public void No_class_in_this_assembly_touches_a_vendor_file_from_outside_the_collection()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);

        var sources = Directory.GetFiles(Path.Combine(dir.FullName, "tests", "TradeAgent.UnitTests"), "*.cs");
        Assert.Contains(sources, f => Path.GetFileName(f) == "VendorOverrideFileTests.cs");

        var offenders = new List<string>();
        foreach (var file in sources)
        {
            var text = string.Join("\n", File.ReadAllLines(file)
                .Select(l => l.TrimStart().StartsWith("//") ? "" : l));
            var classes = Regex.Matches(
                text, @"(?m)^(?:\[Collection\((?<attr>[^)]*)\)\]\n)?public (?:sealed |static |abstract )?class (?<name>\w+)");

            foreach (var call in Touches)
                for (var at = text.IndexOf(call, StringComparison.Ordinal); at >= 0;
                     at = text.IndexOf(call, at + 1, StringComparison.Ordinal))
                {
                    var owner = classes.LastOrDefault(c => c.Index < at);
                    if (owner?.Groups["attr"].Value == "VendorOverrideFiles.Name") continue;
                    offenders.Add($"{Path.GetFileName(file)}: {owner?.Groups["name"].Value ?? "<file scope>"} calls {call}");
                }
        }

        Assert.True(offenders.Count == 0,
            "These read or write runtimes.json / atas.json under the shared test home while outside " +
            $"[Collection(VendorOverrideFiles.Name)], so they race the tests that corrupt it on purpose. " +
            $"Either build the object under test off the disk, or add the attribute:\n  " +
            string.Join("\n  ", offenders));
    }
}

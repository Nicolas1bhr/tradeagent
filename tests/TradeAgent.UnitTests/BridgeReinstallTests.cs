using TradeAgent.Connectors.Atas;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// The repair the app has been naming for months and did not have.
///
/// Three sentences send the owner to a control called "Install bridge": the protocol refusal
/// (<see cref="IncompatibleBridge.ToString"/>), the bridge health row, and the diagnostics check.
/// That control lives inside the setup wizard, which renders only while onboarding is incomplete,
/// so once setup is finished there is nothing on any screen to press. The v0.1.2 protocol bump
/// refuses every bridge deployed before it, and the owner — who never sees a terminal — had no way
/// to redeploy one.
///
/// These are the parts of that repair a test off Windows can reach: the copy, the re-derivation of
/// the row after it, which rows offer it at all, and whether every sentence names the same control.
/// The button itself is judged on the running app.
/// </summary>
public class BridgeReinstallTests
{
    static AtasDetection Machine(bool installed = true, bool running = true, bool bridge = true,
        string? version = "8.0.14.397") =>
        new(installed, installed ? @"C:\ATAS" : null, installed ? @"C:\strategies" : null, version,
            running, bridge, true);

    // ---- the install call ----------------------------------------------------------------------

    /// <summary>
    /// A destination the copy cannot overwrite must produce a sentence the owner can act on, not a
    /// framework exception. On Windows the real cause is ATAS holding the loaded assembly open; the
    /// stand-in here is a destination that is not a file at all, which fails the same call the same
    /// way on this machine. The real ATAS lock is NOT VERIFIED — there is no Windows box on this leg.
    /// </summary>
    [Fact]
    public void A_bridge_file_that_cannot_be_replaced_says_close_ATAS_and_press_the_button_again()
    {
        var (source, strategies, layout) = Sandbox();

        // Occupy the destination with something File.Copy cannot overwrite.
        Directory.CreateDirectory(Path.Combine(strategies, "TradeAgent.AtasBridge.dll"));

        var ex = Assert.Throws<TradeAgentException>(() => AtasInstallation.InstallBridge(source, layout));
        Assert.Equal(ErrorCode.ATAS_BRIDGE_IN_USE, ex.Code);

        var said = $"{ex.Info.UserMessage} {ex.Info.Repair}";
        Assert.Contains("Close ATAS", said);
        Assert.Contains(Labels.ReinstallBridge, said);

        // No other instruction, and nothing that reads as a path: the owner never sees a terminal
        // and never sees a folder.
        Assert.DoesNotContain(strategies, said);
        Assert.DoesNotContain('\\', said);
        Assert.DoesNotContain('/', said);
    }

    /// <summary>The mapping above must not swallow the case it is not about.</summary>
    [Fact]
    public void A_bridge_that_can_be_replaced_is_replaced()
    {
        var (source, strategies, layout) = Sandbox();
        File.WriteAllText(Path.Combine(strategies, "TradeAgent.AtasBridge.dll"), "the old one");

        Assert.Equal(strategies, AtasInstallation.InstallBridge(source, layout));
        Assert.Equal("the new one", File.ReadAllText(Path.Combine(strategies, "TradeAgent.AtasBridge.dll")));
    }

    // ---- the row afterwards --------------------------------------------------------------------

    /// <summary>
    /// The reporter caches a detection for a minute, deliberately: it is filesystem work on a
    /// five-second tick. But a reinstall changes exactly the fact that cache is holding, so without
    /// a way to drop it the owner presses the button, the bridge lands, and the row goes on saying
    /// "not installed in ATAS" for up to a minute — which reads as a repair that did not work.
    /// </summary>
    [Fact]
    public void The_bridge_row_re_derives_after_a_reinstall_instead_of_waiting_out_the_cache()
    {
        var probe = new SettableProbe(Machine(bridge: false));
        var reporter = new AtasHealthReporter(probe);
        var health = new HealthRegistry();

        reporter.Report(health, new AtasConnector(), HealthState.FAILED);
        Assert.Contains("not installed", health.Get(Components.AtasBridge).Detail);

        // The reinstall lands.
        probe.Detection = Machine();

        // Another tick alone does not see it — that is the cache doing its job.
        reporter.Report(health, new AtasConnector(), HealthState.FAILED);
        Assert.Contains("not installed", health.Get(Components.AtasBridge).Detail);

        reporter.Forget();
        reporter.Report(health, new AtasConnector(), HealthState.FAILED);
        Assert.DoesNotContain("not installed", health.Get(Components.AtasBridge).Detail);
    }

    /// <summary>
    /// Which rows the button belongs on. Reinstalling repairs a bridge that was refused or is not
    /// there; it repairs nothing about a platform that is merely shut, a strategy the owner has not
    /// started on a chart, or a bridge that is connected and working. Offering it on those is
    /// inviting somebody to replace a working file for no reason.
    /// </summary>
    [Fact]
    public void The_repair_is_offered_for_a_refused_or_missing_bridge_and_for_nothing_else()
    {
        const string refused = "bridge 0.1.1 speaks protocol 2, this build speaks 3";

        Assert.True(AtasHealth.RepairOffered(true, Machine(), HealthState.FAILED, refused));
        Assert.True(AtasHealth.RepairOffered(true, Machine(bridge: false), HealthState.FAILED, null));

        // ATAS is closed and the bridge is where it should be: nothing to reinstall.
        Assert.False(AtasHealth.RepairOffered(true, Machine(running: false), HealthState.FAILED, null));
        // Installed, ATAS up, strategy not started on a chart — the row already says what to do.
        Assert.False(AtasHealth.RepairOffered(true, Machine(), HealthState.FAILED, null));
        // Working.
        Assert.False(AtasHealth.RepairOffered(true, Machine(), HealthState.READY, null));
        // Not the chosen platform: there is no ATAS in this owner's life.
        Assert.False(AtasHealth.RepairOffered(false, Machine(bridge: false), HealthState.FAILED, null));
    }

    /// <summary>The reporter has to answer the same question the pure call does, on its own facts.</summary>
    [Fact]
    public void The_reporter_offers_the_repair_only_while_the_row_it_wrote_calls_for_it()
    {
        var probe = new SettableProbe(Machine(bridge: false));
        var reporter = new AtasHealthReporter(probe);
        var health = new HealthRegistry();

        reporter.Report(health, new AtasConnector(), HealthState.FAILED);
        Assert.True(reporter.RepairOffered);

        probe.Detection = Machine();
        reporter.Forget();
        reporter.Report(health, new AtasConnector(), HealthState.READY);
        Assert.False(reporter.RepairOffered);
    }

    // ---- the sentences -------------------------------------------------------------------------

    /// <summary>
    /// Every sentence that sends the owner somewhere must name a control that is on a screen, with
    /// the label it carries there. This is the test that would have failed on `main`: all three said
    /// "Install bridge", and no screen has had an Install bridge button since setup completed.
    /// </summary>
    [Fact]
    public void Every_sentence_that_sends_the_owner_to_the_repair_names_the_control_that_exists()
    {
        var refusal = new IncompatibleBridge(2, 3, "0.1.1", "8.0.14.397").ToString();
        var missingRow = AtasHealth.BridgeRow(true, Machine(bridge: false), HealthState.FAILED, null, null).Detail;
        var catalogue = Errors.Get(ErrorCode.ATAS_BRIDGE_MISSING);

        foreach (var said in new[] { refusal, missingRow, $"{catalogue.UserMessage} {catalogue.Repair}" })
        {
            Assert.Contains(Labels.ReinstallBridge, said);
            // "add-on" is what the owner's documents stopped calling it; the app said it last.
            Assert.DoesNotContain("add-on", said);
        }
    }

    /// <summary>
    /// No sentence in the catalogue may name a control that is on no screen. "Press Retry" was the
    /// other one — there has never been a Retry button anywhere in this product, and pressing the
    /// new repair on a computer without ATAS is exactly how an owner would have read it.
    /// </summary>
    [Fact]
    public void No_repair_sentence_sends_the_owner_to_a_button_that_does_not_exist()
    {
        string[] never = ["press Retry", "Press Retry", "Install bridge"];
        foreach (var code in Enum.GetValues<ErrorCode>())
        {
            var repair = Errors.Get(code).Repair;
            foreach (var ghost in never)
                Assert.DoesNotContain(ghost, repair);
        }
    }

    // ---- fixtures ------------------------------------------------------------------------------

    static (string Source, string Strategies, AtasLayout Layout) Sandbox()
    {
        var root = Path.Combine(Path.GetTempPath(), "tradeagent-reinstall-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "bridge");
        var strategies = Path.Combine(root, "Strategies");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(strategies);
        File.WriteAllText(Path.Combine(source, "TradeAgent.AtasBridge.dll"), "the new one");
        return (source, strategies, new AtasLayout { StrategyDirCandidates = [strategies] });
    }

    sealed class SettableProbe(AtasDetection detection) : IAtasProbe
    {
        public AtasDetection Detection { get; set; } = detection;
        public AtasDetection Detect() => Detection;
        public bool IsRunning() => Detection.Running;
    }
}

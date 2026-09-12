using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Diagnostics;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE ARMED LIVE CONFIGURATION WILL NOT START AN AI RUNTIME NOTHING CONFINES.
///
/// A job object and a whitelisted environment are not a sandbox: the agent process runs as the same
/// user as TradeAgent and can read and write whatever that user can, TradeAgent's own binaries and
/// its <c>state/</c> folder included. <c>docs/COUNCIL.md</c>'s round 4 settled what to do about that
/// until an OS sandbox exists — "a CLI that cannot be contained is disabled in the protected
/// configuration rather than kept beside credentials and private evidence" — and this is that,
/// narrowed to the one configuration where it bites.
/// </summary>
// Doctor.RunAsync reads runtimes.json and atas.json, so this class may not run beside the tests that
// corrupt them on purpose — the guard in VendorOverrideFileTests names every class that must say so.
[Collection(VendorOverrideFiles.Name)]
public class ProtectedConfigurationTests
{
    static readonly SandboxState None = new(false, "NONE", "nothing confines it");
    static readonly SandboxState Confined = new(true, "AppContainer", "it is confined");

    [Theory]
    [InlineData(TradingMode.OBSERVE, false)]
    [InlineData(TradingMode.PAPER, false)]
    // The mode CHOSEN but not armed: real money is off, no order can reach a broker, and the owner is
    // very likely in the middle of setting it up. Taking the AI away there would be a refusal that
    // protects nothing.
    [InlineData(TradingMode.LIVE_CONFIRM, false)]
    [InlineData(TradingMode.LIVE_AUTONOMOUS, false)]
    public void An_unarmed_configuration_starts_the_AI_exactly_as_before(TradingMode mode, bool activated)
    {
        var settings = new TradeAgentSettings { Mode = mode, LiveActivated = activated };
        Assert.Null(Containment.RefusalToLaunch(settings.ModeIsLive, settings.LiveActivated, None));
    }

    [Theory]
    [InlineData(TradingMode.LIVE_CONFIRM)]
    [InlineData(TradingMode.LIVE_AUTONOMOUS)]
    public void Real_money_switched_on_with_no_sandbox_refuses_to_start_the_AI(TradingMode mode)
    {
        var settings = new TradeAgentSettings { Mode = mode, LiveActivated = true };
        var why = Containment.RefusalToLaunch(settings.ModeIsLive, settings.LiveActivated, None);

        Assert.NotNull(why);
        Assert.Contains("Real-money trading is switched on", why);
        // What still works has to be in the sentence: a refusal that does not say reads as a fault.
        Assert.Contains("Practice or Watch only", why);
    }

    [Fact]
    public void A_sandbox_that_reports_ok_lifts_the_refusal_without_any_other_change()
    {
        // The one thing that changes this answer is the OS, and nothing here is a switch the owner or
        // an agent can reach. `U-contain-2` is what makes the true case reachable.
        Assert.Null(Containment.RefusalToLaunch(true, true, Confined));
        Assert.NotNull(Containment.RefusalToLaunch(true, true, None));
    }

    [Fact]
    public void This_build_reports_no_sandbox_on_any_platform_it_runs_on()
    {
        var sandbox = Containment.Sandbox();
        Assert.False(sandbox.Ok);
        Assert.Equal("NONE", sandbox.Name);
    }

    // ---- the card ------------------------------------------------------------------------------

    /// <summary>
    /// A fully installed containment: on macOS and Linux the session comes from TradeAgent's own
    /// trade command, so a test asking what an INSTALLED machine's card says has to have one there.
    /// On Windows the job object needs nothing installed and this changes nothing.
    /// </summary>
    static void Installed()
    {
        var exe = Path.Combine(Paths.Bin, ToolDeployer.TradeCliName);
        if (File.Exists(exe)) return;
        if (OperatingSystem.IsWindows()) { File.WriteAllText(exe, "stands in for the deployed trade command"); return; }

        // A shim that honours the launcher flag by simply becoming the program, because the Doctor's
        // own probes (a version string, an auth state) run through the same containment and a stand-in
        // that swallowed them would make this test about something else.
        File.WriteAllText(exe, "#!/bin/sh\n[ \"$1\" = \"--spawn-contained\" ] && shift\nexec \"$@\"\n");
        File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public void The_card_says_OS_sandbox_NONE_in_so_many_words()
    {
        Installed();
        var check = Doctor.ContainmentCheck(new TradeAgentSettings { Mode = TradingMode.PAPER });

        Assert.Equal("AI containment", check.Name);
        Assert.Contains("OS sandbox: NONE", check.Detail);
        // And the rest of the story, so the row is not only about what is missing.
        Assert.Contains("environment variables", check.Detail);
        Assert.Contains("grant", check.Detail);
        Assert.Equal(HealthState.READY, check.State);
    }

    [Fact]
    public void The_card_goes_amber_only_where_something_is_actually_withheld()
    {
        var armed = Doctor.ContainmentCheck(new TradeAgentSettings
        {
            Mode = TradingMode.LIVE_AUTONOMOUS, LiveActivated = true
        });

        Assert.Equal(HealthState.DEGRADED, armed.State);
        Assert.Contains("OS sandbox: NONE", armed.Detail);
        Assert.Contains("Real-money trading is switched on", armed.UserAction);
        // Nothing to press: the sandbox is the next unit, not a repair.
        Assert.False(armed.AutoRepairable);
    }

    [Fact]
    public async Task The_doctors_report_carries_the_row_on_an_ordinary_installation()
    {
        Installed();
        var report = await new Doctor(allowNetwork: false).RunAsync();
        var row = Assert.Single(report.Checks, c => c.Name == "AI containment");
        Assert.Contains("OS sandbox: NONE", row.Detail);
    }
}

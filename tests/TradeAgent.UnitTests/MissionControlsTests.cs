using Avalonia.Controls;
using Avalonia.Interactivity;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE CONTROL THAT TURNS THE AI LOOSE, AND THE ONE THAT STOPS IT.
///
/// "Let the AI work on its own" is the largest grant on the Dashboard: it is the difference between
/// something that answers when spoken to and something that starts a new turn the moment the last
/// one ends, for as long as the machine is on. So it obeys the rule every other granting control in
/// this product obeys (REVIEW 2026-09-05b finding 3, Codex F12) — two presses, and the armed control
/// says what the second one does in full — while Pause stays one press, because an owner trying to
/// stop should not have to argue with the software.
///
/// The widget is pressed here, built by the factory the Dashboard builds it with, so what is pressed
/// is the control the owner sees. The composition assertion at the bottom is the one line no test off
/// a running app can reach.
/// </summary>
public class MissionControlsTests
{
    static void Press(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    // ---- 1. two presses to grant, one to take away ---------------------------------------------

    /// <summary>
    /// The finding, in its own shape. One press arms and starts nothing; the second starts the loop,
    /// and the armed face of the control is a whole sentence rather than the word "Confirm".
    /// </summary>
    [Fact]
    public void Letting_the_ai_work_on_its_own_takes_two_presses_and_says_what_the_second_does()
    {
        var working = false;
        var b = DashboardPage.BuildWorkOnItsOwn(() => working, () => working = true, () => working = false);

        Assert.Equal(DashboardPage.LetTheAiWork, b.Content);

        Press(b);
        Assert.False(working);
        Assert.Equal(DashboardPage.LetTheAiWorkArmed, b.Content);

        Press(b);
        Assert.True(working);
    }

    /// <summary>
    /// The other direction, which is the whole reason the control is not simply two-press. Pausing
    /// only ever takes work away, so it happens on the press.
    /// </summary>
    [Fact]
    public void Pausing_the_ai_takes_one_press()
    {
        var working = true;
        var b = DashboardPage.BuildWorkOnItsOwn(() => working, () => working = true, () => working = false);

        Assert.Equal(DashboardPage.PauseTheAi, b.Content);

        Press(b);

        Assert.False(working);
    }

    /// <summary>
    /// The Dashboard repaints this control every five seconds. A half-pressed grant has to survive a
    /// tick that says nothing new — the owner is reading the sentence when it lands — and must NOT
    /// survive the loop starting some other way, because then the sentence on the control is no
    /// longer true of the button underneath it.
    /// </summary>
    [Fact]
    public void A_half_pressed_grant_survives_the_refresh_and_is_abandoned_when_the_loop_starts()
    {
        var working = false;
        var b = DashboardPage.BuildWorkOnItsOwn(() => working, () => working = true, () => working = false);

        Press(b);
        Ui.SetResting(b, DashboardPage.LetTheAiWork, "primary");
        Assert.True(Ui.IsArmed(b));
        Assert.Equal(DashboardPage.LetTheAiWorkArmed, b.Content);
        Assert.False(working);

        working = true;
        Ui.SetResting(b, DashboardPage.PauseTheAi, "secondary");
        Assert.False(Ui.IsArmed(b));
        Assert.Equal(DashboardPage.PauseTheAi, b.Content);
    }

    // ---- 2. what survives a restart ------------------------------------------------------------

    /// <summary>
    /// PAUSED SURVIVES A RESTART; WORKING RESUMES. Both halves matter and only one of them is a
    /// convenience: an AI the owner paused that came back working would be the software granting
    /// itself the largest thing on the page.
    ///
    /// The fourth case is the awkward one. Resuming switched off leaves a flag saying "working" over
    /// a loop that is not, so the flag is corrected rather than kept — a card reading "working"
    /// beside a paused loop makes every number on it describe a turn that will never happen.
    /// </summary>
    [Theory]
    [InlineData(false, true, AppHost.MissionOnStart.StayPaused)]
    [InlineData(false, false, AppHost.MissionOnStart.StayPaused)]
    [InlineData(true, true, AppHost.MissionOnStart.Resume)]
    [InlineData(true, false, AppHost.MissionOnStart.ForgetItWasWorking)]
    internal void What_a_restart_does_about_an_ai_that_was_working(
        bool wasWorking, bool resumeOnStart, AppHost.MissionOnStart expected)
    {
        var settings = new TradeAgentSettings { AiWorksOnItsOwn = wasWorking, ResumeAiOnStart = resumeOnStart };

        Assert.Equal(expected, AppHost.DecideOnStart(settings));
    }

    /// <summary>Resuming is on out of the box, and nothing works on its own until the owner says so.</summary>
    [Fact]
    public void A_fresh_install_is_paused_and_would_resume_if_it_had_been_working()
    {
        var fresh = new TradeAgentSettings();

        Assert.False(fresh.AiWorksOnItsOwn);
        Assert.True(fresh.ResumeAiOnStart);
        Assert.Equal(20, fresh.MissionTurnsPerSession);
        Assert.Equal("", fresh.Guidance);
    }

    /// <summary>
    /// A SETTINGS ROW THIS BUILD CANNOT READ DOES NOT LEAVE THE AI WORKING. It is the same rule as
    /// the kill switch and the allowlist (REVIEW 2026-09-05 finding 5): the one event proving the
    /// software cannot read what the owner asked for must not also be the event that grants
    /// something. Guidance goes with it — standing instructions nobody can vouch for are none.
    /// </summary>
    [Fact]
    public void A_settings_row_that_could_not_be_read_leaves_the_ai_paused_and_unguided()
    {
        var s = TradeAgentSettings.Unreadable();

        Assert.False(s.AiWorksOnItsOwn);
        Assert.Equal("", s.Guidance);
        Assert.True(s.AiTradingStopped);
    }

    /// <summary>
    /// The flag and the guidance go through the same row as everything else, so they are still there
    /// after the process that wrote them is gone. Written by one gateway, read by another over the
    /// same database — which is what a restart is.
    /// </summary>
    [Fact]
    public async Task The_owners_choice_and_their_guidance_are_still_there_after_a_restart()
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var _ = db;
        await using (gw)
        {
            gw.Update(s => { s.AiWorksOnItsOwn = true; s.Guidance = "focus on ES"; });
        }

        await using var restarted = new TradingGateway(db, conn, new HealthRegistry());

        Assert.True(restarted.Settings.AiWorksOnItsOwn);
        Assert.Equal("focus on ES", restarted.Settings.Guidance);
    }

    /// <summary>
    /// STOP AI TRADING KEEPS ITS MEANING AND DOES NOT STOP THE LOOP. The kill switch removes the
    /// AI's permission to TRADE; the work — research, backtesting, strategies, the journal — carries
    /// on, and is arguably most valuable exactly then. A kill switch that also silenced the AI would
    /// make the safest setting the least useful one, which is how a safety control stops being used.
    /// </summary>
    [Fact]
    public async Task Stopping_ai_trading_leaves_the_ai_working_on_its_own()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var __ = db;
        await using var _ = gw;
        gw.Update(s => s.AiWorksOnItsOwn = true);

        gw.StopAiTrading("you pressed STOP AI TRADING");

        Assert.True(gw.Settings.AiTradingStopped);
        Assert.True(gw.Settings.AiWorksOnItsOwn);
    }

    // ---- 3. the composition --------------------------------------------------------------------

    /// <summary>
    /// What ties the control above to the screen that shows it, and the restart decision to the
    /// composition root that acts on it. Everything the factory DOES is pressed above; these are the
    /// lines no test off a running app can reach, so — like
    /// <c>TwoPressGrantTests.The_safety_page_builds_all_three_controls_through_those_factories</c> —
    /// they catch a revert or a deletion and not a rewrite.
    /// </summary>
    [Fact]
    public void The_dashboard_builds_the_control_through_the_factory_and_the_host_acts_on_the_decision()
    {
        var dashboard = File.ReadAllText(Path.Combine(Root(), "src", "TradeAgent.App", "DashboardView.cs"));
        var host = File.ReadAllText(Path.Combine(Root(), "src", "TradeAgent.App", "AppHost.cs"));

        Assert.Contains("_workButton = BuildWorkOnItsOwn(", dashboard);
        Assert.Contains("() => _host.Mission.Running,", dashboard);
        Assert.Contains("() => _host.LetTheAiWorkOnItsOwn(),", dashboard);

        // The one-press version of the same control, so a revert cannot pass by adding rather than
        // editing: nothing on this page may start the loop through a plain button.
        Assert.DoesNotContain("Ui.Primary(LetTheAiWork", dashboard);
        Assert.DoesNotContain("Ui.Secondary(LetTheAiWork", dashboard);
        Assert.DoesNotContain("Ui.Button(LetTheAiWork", dashboard);

        Assert.Contains("switch (DecideOnStart(Gateway.Settings))", host);
        Assert.Contains("case MissionOnStart.Resume: Mission.Start(); break;", host);
        Assert.Contains("case MissionOnStart.ForgetItWasWorking:", host);
        Assert.Contains("ResumeMissionIfItWasWorking();", host);
    }

    /// <summary>The repository root, found from the test assembly rather than assumed.</summary>
    static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }
}

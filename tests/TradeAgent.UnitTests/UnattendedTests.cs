using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Microsoft.Win32;
using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>A power request on paper: what was asked, and whether it is held now.</summary>
sealed class RecordedAwake : IKeepAwake
{
    public bool Accepts { get; set; } = true;
    public bool Held { get; private set; }
    public int Holds { get; private set; }
    public int Releases { get; private set; }
    public string? Reason { get; private set; }
    public bool Supported => true;

    public bool Hold(string reason)
    {
        Holds++;
        Reason = reason;
        if (Accepts) Held = true;
        return Accepts;
    }

    public void Release()
    {
        if (Held) Releases++;
        Held = false;
    }
}

/// <summary>This account's Run value on paper, and Windows' own startup switch beside it.</summary>
sealed class RecordedRunValue : ISignInStart
{
    public string? Value { get; set; }
    public bool OffInWindows { get; set; }
    public bool Supported => true;
    public string? Command() => Value;
    public bool TurnedOffInWindows() => OffInWindows;
    public void Register(string command) => Value = command;
    public void Remove() => Value = null;
}

/// <summary>
/// A LAPTOP THAT RUNS UNATTENDED (<c>U-windows-awake</c>; r31 D3 F5): the PC kept awake exactly while the AI works on its own and given back
/// on every way it stops, the owner's one press either way; and Windows opening TradeAgent at his sign-in only on his two presses, this
/// program alone, removed on one. The platform calls are behind <see cref="IKeepAwake"/> and <see cref="ISignInStart"/>; the Windows rows run
/// the real calls, the request and a registry key of the test's own, never the account's real Run key.
///
/// <para><b>What no row here can prove</b>: that a consumer laptop does not sleep. CI's Windows is Server 2025 on a VM with no lid, no
/// battery and no modern standby; the request being taken is all it shows.</para>
/// </summary>
public class UnattendedTests
{
    static void Press(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    static AppHost Host(Core.Db.Database db, RecordedAwake? awake = null, RecordedRunValue? run = null, string? program = null)
    {
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        if (awake is not null) host.KeepAwake = awake;
        if (run is not null) host.SignInStart = run;
        var exe = program ?? Path.Combine(Path.GetTempPath(), "Programs", "TradeAgent", "TradeAgent.exe");
        host.ThisProgram = () => exe;
        return host;
    }

    // ---- 1. awake while the AI works on its own ------------------------------------------------------------------------------------

    [Fact]
    public async Task The_pc_is_kept_awake_exactly_while_the_ai_works_on_its_own_and_given_back_at_the_pause()
    {
        using var db = TestEnv.NewDb();
        var awake = new RecordedAwake();
        var host = Host(db, awake);
        try
        {
            Assert.True(new TradeAgentSettings().KeepAwakeWhileAiWorks);
            host.AwakePass();
            Assert.False(awake.Held);
            Assert.Equal(AwakeReading.WhenItWorks, host.AwakeNow());

            Assert.Null(host.LetTheAiWorkOnItsOwn());
            Assert.True(awake.Held);
            Assert.Equal(AppHost.AwakeReason, awake.Reason);
            Assert.Equal(AwakeReading.Holding, host.AwakeNow());

            // THE PASSES DO NOT ASK AGAIN over a request that is held.
            host.AwakePass();
            host.AwakePass();
            Assert.Equal(1, awake.Holds);

            await host.PauseTheAiAsync();
            Assert.False(awake.Held);
            Assert.Equal(1, awake.Releases);
            Assert.Equal(AwakeReading.WhenItWorks, host.AwakeNow());
        }
        finally
        {
            await host.Mission.PauseAsync();
            await host.Gateway.DisposeAsync();
        }
    }

    /// <summary>A loop that stops by any road other than the owner's pause is given back at the next pass, which the background loop runs every five seconds.</summary>
    [Fact]
    public async Task A_loop_that_stops_without_the_pause_gives_the_request_back_at_the_next_pass()
    {
        using var db = TestEnv.NewDb();
        var awake = new RecordedAwake();
        var host = Host(db, awake);
        try
        {
            Assert.Null(host.LetTheAiWorkOnItsOwn());
            Assert.True(awake.Held);
            await host.Mission.PauseAsync();
            host.AwakePass();
            Assert.False(awake.Held);
        }
        finally
        {
            await host.Mission.PauseAsync();
            await host.Gateway.DisposeAsync();
        }
    }

    [Fact]
    public async Task The_quit_gives_the_request_back_and_keeps_the_owners_word_that_the_ai_works()
    {
        using var db = TestEnv.NewDb();
        var awake = new RecordedAwake();
        var host = Host(db, awake);
        Assert.Null(host.LetTheAiWorkOnItsOwn());
        Assert.True(awake.Held);

        await host.DisposeAsync();

        Assert.False(awake.Held);
        Assert.False(host.AwakeWanted());
    }

    [Fact]
    public async Task Letting_the_pc_sleep_is_one_press_that_gives_the_request_back_and_one_press_takes_it_again()
    {
        using var db = TestEnv.NewDb();
        var awake = new RecordedAwake();
        var host = Host(db, awake);
        try
        {
            Assert.Null(host.LetTheAiWorkOnItsOwn());
            var page = new DashboardPage(host, () => Task.CompletedTask);
            page.Update(await host.Gateway.StatusAsync());
            var texts = page.Root.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();
            Assert.Contains(AppHost.AwakeWords(AwakeReading.Holding), texts);

            var press = page.Root.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == AppHost.LetItSleep);
            Press(press);
            Assert.False(awake.Held);
            Assert.False(host.Gateway.Settings.KeepAwakeWhileAiWorks);
            Assert.Equal(AwakeReading.OwnerLetsItSleep, host.AwakeNow());
            Assert.Equal(AppHost.KeepItAwake, press.Content);

            // THE PASSES DO NOT TAKE IT BACK on their own while the owner lets it sleep.
            host.AwakePass();
            Assert.False(awake.Held);

            Press(press);
            Assert.True(awake.Held);
            Assert.True(host.Gateway.Settings.KeepAwakeWhileAiWorks);
            Assert.Equal(AppHost.LetItSleep, press.Content);
        }
        finally
        {
            await host.Mission.PauseAsync();
            await host.Gateway.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_request_windows_refuses_is_said_once_and_asked_again_until_it_is_held()
    {
        using var db = TestEnv.NewDb();
        var awake = new RecordedAwake { Accepts = false };
        var host = Host(db, awake);
        try
        {
            Assert.Null(host.LetTheAiWorkOnItsOwn());
            host.AwakePass();
            Assert.Equal(AwakeReading.WindowsRefused, host.AwakeNow());
            var said = host.Gateway.Log.RecentActivity(200).Count(l => l.Text == AppHost.AwakeWords(AwakeReading.WindowsRefused));
            Assert.Equal(1, said);

            awake.Accepts = true;
            host.AwakePass();
            Assert.True(awake.Held);
            Assert.Equal(AwakeReading.Holding, host.AwakeNow());
        }
        finally
        {
            await host.Mission.PauseAsync();
            await host.Gateway.DisposeAsync();
        }
    }

    [Fact]
    public async Task Off_windows_nothing_is_asked_and_the_dashboard_says_nothing_about_sleep()
    {
        using var db = TestEnv.NewDb();
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            // A TEST'S HOST asks the machine it runs on for nothing.
            Assert.Same(KeepAwakes.None, host.KeepAwake);
            Assert.Same(SignInStarts.None, host.SignInStart);
            Assert.Null(host.LetTheAiWorkOnItsOwn());
            Assert.Equal(AwakeReading.NotThisMachine, host.AwakeNow());
            var page = new DashboardPage(host, () => Task.CompletedTask);
            page.Update(await host.Gateway.StatusAsync());
            Assert.DoesNotContain(page.Root.GetLogicalDescendants().OfType<Button>(),
                b => b.IsVisible && b.Content as string is AppHost.LetItSleep or AppHost.KeepItAwake);
        }
        finally
        {
            await host.Mission.PauseAsync();
            await host.Gateway.DisposeAsync();
        }
    }

    // ---- 2. opening when the owner signs in ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Opening_at_sign_in_is_off_until_two_presses_writes_this_program_alone_and_one_press_removes_it()
    {
        using var db = TestEnv.NewDb();
        var run = new RecordedRunValue();
        var exe = Path.Combine(Path.GetTempPath(), "Programs", "TradeAgent", "TradeAgent.exe");
        var host = Host(db, run: run, program: exe);
        try
        {
            var card = new SignInStartCard(host);
            Assert.Equal(SignInReading.Off, host.SignInStartNow());
            var press = card.Root.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == AppHost.StartAtSignInLabel);

            Press(press);
            Assert.Null(run.Value);
            Assert.True(Ui.IsArmed(press));
            Assert.Equal(AppHost.StartAtSignInArmed, press.Content);

            Press(press);
            // THIS PROGRAM, QUOTED, AND NOTHING AFTER IT: the start at sign-in is the ordinary start.
            Assert.Equal($"\"{Path.GetFullPath(exe)}\"", run.Value);
            Assert.Equal(SignInReading.On, host.SignInStartNow());
            Assert.Equal(AppHost.StopAtSignInLabel, press.Content);

            Press(press);
            Assert.Null(run.Value);
            Assert.Equal(SignInReading.Off, host.SignInStartNow());
            Assert.Equal(AppHost.StartAtSignInLabel, press.Content);
        }
        finally { await host.Gateway.DisposeAsync(); }
    }

    /// <summary>
    /// THE START AT SIGN-IN IS THE ORDINARY START: no argument rides on the command, so the restart decides what it always decides — a paused AI
    /// stays paused (<c>ResumeOnStartTests</c> drive that decision over a real restart).
    /// </summary>
    [Fact]
    public void The_command_carries_no_argument_so_a_pause_survives_the_restart_it_makes()
    {
        var exe = Path.Combine(Path.GetTempPath(), "Programs", "TradeAgent", "TradeAgent.exe");
        var command = SignInStarts.CommandFor(exe);
        Assert.Equal(exe, SignInStarts.ProgramOf(command));
        Assert.Equal($"\"{exe}\"", command);
        Assert.Equal(AppHost.MissionOnStart.StayPaused, AppHost.DecideOnStart(new TradeAgentSettings { AiWorksOnItsOwn = false }));
    }

    [Fact]
    public async Task A_copy_that_is_not_installed_is_refused_and_writes_nothing()
    {
        using var db = TestEnv.NewDb();
        var run = new RecordedRunValue();
        var host = Host(db, run: run, program: Path.Combine(Path.GetTempPath(), "dotnet"));
        try
        {
            Assert.Equal("Only an installed TradeAgent can be opened when you sign in to Windows.", host.StartAtSignIn());
            Assert.Null(run.Value);
        }
        finally { await host.Gateway.DisposeAsync(); }
    }

    [Fact]
    public async Task Another_copy_and_windows_own_switch_are_said_as_they_are()
    {
        using var db = TestEnv.NewDb();
        var other = Path.Combine(Path.GetTempPath(), "Elsewhere", "TradeAgent.exe");
        var run = new RecordedRunValue { Value = SignInStarts.CommandFor(other) };
        var host = Host(db, run: run);
        try
        {
            var card = new SignInStartCard(host);
            Assert.Equal(SignInReading.AnotherCopy, host.SignInStartNow());
            var press = card.Root.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == AppHost.StartThisCopyLabel);
            Press(press);
            Assert.Equal(AppHost.StartThisCopyArmed, press.Content);
            Press(press);
            Assert.Equal(SignInReading.On, host.SignInStartNow());

            // SWITCHED OFF IN WINDOWS' OWN LIST: said, the press to Windows' page shown, and the switch never touched.
            run.OffInWindows = true;
            card.Update();
            Assert.Equal(SignInReading.OnButOffInWindows, host.SignInStartNow());
            Assert.Contains(card.Root.GetLogicalDescendants().OfType<Button>(), b => b.IsVisible && b.Content as string == AppHost.OpenStartupApps);
            Assert.True(run.OffInWindows);
        }
        finally { await host.Gateway.DisposeAsync(); }
    }

    /// <summary>THE UNINSTALLER removes the value, and only when it opens that install's TradeAgent; Setup never writes it.</summary>
    [Fact]
    public void The_uninstaller_removes_the_sign_in_start_of_its_own_install_and_setup_never_writes_one()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var iss = File.ReadAllText(Path.Combine(dir.FullName, "packaging", "TradeAgent.iss"));
        var code = iss[iss.IndexOf("procedure CurUninstallStepChanged", StringComparison.Ordinal)..];
        Assert.Contains("CurUninstallStep = usUninstall", code);
        Assert.Contains($"RunKey := '{SignInStarts.RunKey}';", code);
        Assert.Contains($"RegQueryStringValue(HKEY_CURRENT_USER, RunKey, '{SignInStarts.ValueName}', Command)", code);
        Assert.Contains("Pos(Lowercase(ExpandConstant('{app}\\TradeAgent.exe')), Lowercase(Command)) > 0", code);
        Assert.Contains($"RegDeleteValue(HKEY_CURRENT_USER, RunKey, '{SignInStarts.ValueName}');", code);
        Assert.DoesNotContain("[Registry]", iss);
    }

    // ---- the real calls, on Windows ------------------------------------------------------------------------------------------------

    [OnlyOnFact("the power request is Windows' own", "windows")]
    [SupportedOSPlatform("windows")]
    public void Windows_takes_the_power_request_and_gives_it_back()
    {
        var awake = (WindowsKeepAwake)KeepAwakes.ForThisMachine();
        try
        {
            Assert.True(awake.Supported);
            Assert.True(awake.Hold(AppHost.AwakeReason));
            Assert.True(awake.Hold(AppHost.AwakeReason));
            awake.Release();
            awake.Release();
            Assert.True(awake.Hold(AppHost.AwakeReason));
        }
        finally { awake.Release(); }
    }

    [OnlyOnFact("the Run value is in Windows' registry", "windows")]
    [SupportedOSPlatform("windows")]
    public void A_run_value_is_written_read_and_removed_on_a_key_of_the_tests_own()
    {
        var root = $@"Software\TradeAgent-tests\{Guid.NewGuid():n}";
        var runKey = root + @"\Run";
        var approvedKey = root + @"\StartupApproved\Run";
        var value = new WindowsRunValue(runKey, approvedKey, SignInStarts.ValueName);
        try
        {
            Assert.Null(value.Command());
            Assert.False(value.TurnedOffInWindows());
            value.Register("\"C:\\Programs\\TradeAgent\\TradeAgent.exe\"");
            Assert.Equal("\"C:\\Programs\\TradeAgent\\TradeAgent.exe\"", value.Command());

            using (var approved = Registry.CurrentUser.CreateSubKey(approvedKey, writable: true))
                approved.SetValue(SignInStarts.ValueName, new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
            Assert.True(value.TurnedOffInWindows());
            using (var approved = Registry.CurrentUser.CreateSubKey(approvedKey, writable: true))
                approved.SetValue(SignInStarts.ValueName, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
            Assert.False(value.TurnedOffInWindows());

            value.Remove();
            value.Remove();
            Assert.Null(value.Command());
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false); }
    }
}

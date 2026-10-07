using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A RESTART THAT RESUMES A WORKING AI STARTS THE AI, THROUGH THE PRESS'S OWN PATH.
///
/// <para><b>The gap, measured in the observed run of 2026-10-01.</b> Relaunched while the mission was
/// working, the app resumed the LOOP and nothing else: the card read "stopped — the AI has not been
/// started", no turn ran, and the next one came only after the owner pressed Start the AI. The loop
/// without a runtime has no conversation to take a turn with, so resuming it alone resumes nothing.</para>
///
/// <para><b>What is driven here.</b> The host's own composition (<c>AppHost.Composed</c>, the same
/// <c>ComposeTheAi</c> that <c>StartAsync</c> runs) over a database a previous session wrote, and the
/// host's own <c>ResumeOnStartAsync</c> — what <c>StartAsync</c> does last. The rest of
/// <c>StartAsync</c> is the machine (an instance lock and a pipe server per home, collectors, the
/// background loop) and one test assembly cannot run it beside every other class; that it calls the
/// resume is pinned in <c>MissionControlsTests</c>. The AI is a real <see cref="CliAgentRuntime"/> over
/// a one-line program, so the checks it meets are the runtime's own, and its processes report to a
/// register of their own because the process-wide one is sticky.</para>
///
/// <para><b>The mutant watched:</b> the resume calling the loop only. The first test goes red on the
/// runtime that never started.</para>
/// </summary>
// The probe runtime goes into runtimes.json under the shared test home, so this class may not run
// beside the tests that corrupt that file on purpose.
[Collection(VendorOverrideFiles.Name)]
public class ResumeOnStartTests : IDisposable
{
    const string Probe = "resume-probe";

    public void Dispose()
    {
        if (File.Exists(RuntimeCatalog.OverridePath)) File.Delete(RuntimeCatalog.OverridePath);
    }

    // ---- 1. working, and resuming on start --------------------------------------------------------

    /// <summary>
    /// THE GAP, IN ITS OWN SHAPE. The last session left the AI working on its own, setup finished and a
    /// runtime chosen, and the scheduled look came due while the app was closed. The restart presses
    /// nothing — and the runtime is running and that wake is taken by a turn all the same.
    /// </summary>
    [Fact]
    public async Task A_restart_with_the_ai_working_starts_its_runtime_and_the_next_due_wake_is_taken_without_a_press()
    {
        var (host, _, presence) = await Restarted(s => { s.AiWorksOnItsOwn = true; s.ResumeAiOnStart = true; });
        try
        {
            var due = DateTimeOffset.UtcNow.AddMinutes(-1);
            var wake = MissionEventIds.Review(due);
            Assert.True(host.Wakes!.RaiseDue(wake, MissionEventKind.Review, due.AddMinutes(-30), due));

            await host.ResumeOnStartAsync();
            var resumed = DateTimeOffset.UtcNow;

            Assert.True(host.Agent.Running, "the restart resumed the loop and left the AI it needs stopped");
            Assert.Equal(Probe, host.Agent.Current?.Id);
            Assert.True(host.Mission.Running);
            Assert.Null(host.AiNotStarted);

            // TAKEN BY A TURN: consumed by a launch the ledger recorded, and settled once that turn's
            // committed transition landed — waited for, because the loop takes it on its own thread.
            var taken = await Until(() => host.Wakes.Get(wake) is { Disposition: not null } e ? e : null,
                "the wake that was due when the app closed was never taken and settled by a turn",
                () => LoopAt(host, wake, presence, resumed));
            Assert.True(taken.Consumed);
            Assert.False(string.IsNullOrEmpty(taken.ConsumedBy));
            Assert.NotEqual(MissionLoop.Unrecorded, taken.ConsumedBy);

            // AND IT WAS A PROCESS OF THE RUNTIME THE RESTART STARTED: the turn's child reported to the
            // register this host was composed with, which nothing else in the assembly can see.
            Assert.NotNull(presence.LastAliveAt);
        }
        finally { await Close(host); }
    }

    /// <summary>
    /// A PROGRAM SLOW TO SAY ITS VERSION DOES NOT KEEP THE AI STOPPED, and its row says why.
    ///
    /// <para><b>What windows-latest showed</b> (run 37017805967, job 110872924285, the test above
    /// with prints): 4.9 s to compose the restart, then 20.1 s inside <c>ResumeOnStartAsync</c> — the
    /// whole of it the version probe's deadline, because PowerShell's first launch beside the full
    /// suite did not answer <c>version</c> in 20 s (measured again at once: 6.5 s, then 0.2 s). The
    /// deadline left the probe as <c>AI_AUTH_TIMEOUT</c> — "powershell.exe did not finish within 20s",
    /// out of <c>DetectAsync</c> and <c>PrepareAsync</c> — so the card read "Signing in took too long
    /// and was cancelled. Press Sign in again." on a restart that signs nothing in, and the AI stayed
    /// stopped. A probe that FAILS already leaves the start going with the program's words on the row;
    /// a slow one is a probe that failed. The deadline is a second here so that no machine is fast
    /// enough to beat it: the program sleeps half a minute before it answers.</para>
    /// </summary>
    [Fact]
    public async Task A_restart_whose_runtime_is_slow_to_answer_its_version_still_starts_it_and_its_row_says_why()
    {
        var (host, _, _) = await Restarted(s => { s.AiWorksOnItsOwn = true; s.ResumeAiOnStart = true; },
            slowVersion: true);
        host.Agent.VersionDeadline = TimeSpan.FromSeconds(1);
        try
        {
            await host.ResumeOnStartAsync();

            Assert.True(host.Agent.Running, "a version answer slower than its deadline kept the AI stopped on a restart");
            Assert.Equal(Probe, host.Agent.Current?.Id);
            Assert.Null(host.AiNotStarted);
            Assert.DoesNotContain(host.Gateway.Log.RecentActivity(),
                a => a.Text.StartsWith("The AI was not started", StringComparison.Ordinal));
            Assert.True(host.Mission.Running);

            var row = host.Health.Get(Components.AgentRuntime);
            Assert.Equal(HealthState.FAILED, row.State);
            Assert.Contains("did not answer within 1 second", row.Detail, StringComparison.Ordinal);
        }
        finally { await Close(host); }
    }

    // ---- 2. paused --------------------------------------------------------------------------------

    /// <summary>PAUSED SURVIVES A RESTART, and so does a stopped AI: nothing is started for it.</summary>
    [Fact]
    public async Task A_restart_with_the_ai_paused_starts_nothing()
    {
        var (host, _, _) = await Restarted(s => { s.AiWorksOnItsOwn = false; s.ResumeAiOnStart = true; });
        try
        {
            await host.ResumeOnStartAsync();

            Assert.False(host.Agent.Running);
            Assert.Null(host.Agent.Current);
            Assert.False(host.Mission.Running);
            Assert.False(host.Gateway.Settings.AiWorksOnItsOwn);
        }
        finally { await Close(host); }
    }

    // ---- 3. resuming switched off -------------------------------------------------------------------

    /// <summary>
    /// RESUMING SWITCHED OFF STARTS NOTHING AND CORRECTS THE RECORD, unchanged: the flag is written back
    /// to false, and the next session reads it so.
    /// </summary>
    [Fact]
    public async Task A_restart_with_resuming_switched_off_starts_nothing_and_writes_the_flag_back()
    {
        var (host, path, _) = await Restarted(s => { s.AiWorksOnItsOwn = true; s.ResumeAiOnStart = false; });
        try
        {
            await host.ResumeOnStartAsync();

            Assert.False(host.Agent.Running);
            Assert.Null(host.Agent.Current);
            Assert.False(host.Mission.Running);
            Assert.False(host.Gateway.Settings.AiWorksOnItsOwn);
        }
        finally { await Close(host); }

        using var reopened = new Database(path);
        await using var next = new TradingGateway(reopened, new FakeConnector(new FakeBroker()), new HealthRegistry());
        Assert.False(next.Settings.AiWorksOnItsOwn);
    }

    // ---- 4. the protected configuration -------------------------------------------------------------

    /// <summary>
    /// ARMED LIVE AND UNCONTAINED: REFUSED, IN THE SAME WORDS, AND NOTHING LEFT RUNNING. The refusal is
    /// the runtime's own <c>CONTAINMENT_REQUIRED</c>, met because the restart takes the press's path. It
    /// is written to the activity log with its code and onto the card, in the words the press shows in
    /// the strip under the header — and no runtime is kept for the loop to launch a turn through.
    /// </summary>
    [Fact]
    public async Task A_restart_in_the_armed_live_configuration_is_refused_in_the_same_words_and_starts_no_runtime()
    {
        var (host, _, _) = await Restarted(s =>
        {
            s.AiWorksOnItsOwn = true;
            s.ResumeAiOnStart = true;
            s.Mode = TradingMode.LIVE_AUTONOMOUS;
            s.LiveActivated = true;
        });
        try
        {
            // Nothing on any platform this suite runs on confines an AI process, so this IS the
            // protected configuration rather than a stand-in for it.
            Assert.NotNull(Containment.RefusalToLaunch(modeIsLive: true, liveActivated: true));

            await host.ResumeOnStartAsync();

            Assert.False(host.Agent.Running);
            Assert.Null(host.Agent.Current);
            Assert.Null(host.Conversation);
            Assert.Equal(MissionState.Stopped, host.Mission.Status.State);

            var info = Errors.Get(ErrorCode.CONTAINMENT_REQUIRED);
            var words = $"{info.UserMessage} {info.Repair}";
            var line = Assert.Single(host.Gateway.Log.RecentActivity(),
                a => a.Text.Contains(nameof(ErrorCode.CONTAINMENT_REQUIRED), StringComparison.Ordinal));
            Assert.Equal("warn", line.Level);
            Assert.Contains(words, line.Text, StringComparison.Ordinal);
            Assert.Equal($"stopped — {words}",
                DashboardPage.MissionSentence(host.Mission.Status, host.AiNotStarted));

            // The owner's choice stands: the loop is resumed as before, and the press that starts the AI
            // once real money is switched off is all that is left to do.
            Assert.True(host.Mission.Running);
            Assert.True(host.Gateway.Settings.AiWorksOnItsOwn);
        }
        finally { await Close(host); }
    }

    // ---- 5. setup not finished ----------------------------------------------------------------------

    /// <summary>
    /// BEFORE SETUP IS FINISHED THE START IS THE OWNER'S: the last screen of setup is where the AI is
    /// started, so a restart in the middle of it starts no runtime and says no refusal.
    /// </summary>
    [Fact]
    public async Task A_restart_before_setup_is_finished_leaves_the_start_to_the_owner()
    {
        var (host, _, _) = await Restarted(s => { s.AiWorksOnItsOwn = true; s.ResumeAiOnStart = true; },
            setupFinished: false);
        try
        {
            await host.ResumeOnStartAsync();

            Assert.False(host.Agent.Running);
            Assert.Null(host.Agent.Current);
            Assert.Null(host.AiNotStarted);
            Assert.DoesNotContain(host.Gateway.Log.RecentActivity(),
                a => a.Text.StartsWith("The AI was not started", StringComparison.Ordinal));
            Assert.True(host.Mission.Running);
        }
        finally { await Close(host); }
    }

    // ---- 6. one start path ----------------------------------------------------------------------------

    /// <summary>
    /// THE PRESS AND SETUP REACH THE RUNTIME THROUGH THE HOST'S ONE METHOD, and the host holds exactly
    /// one prepare, one start and one <c>Require</c>. Read from source because the two surfaces are
    /// windows no test presses headless; like the other composition assertions it catches a revert or a
    /// second path, not a rewrite.
    /// </summary>
    [Fact]
    public void The_press_setup_and_the_restart_reach_the_runtime_through_one_method_on_the_host()
    {
        var app = Path.Combine(Root(), "src", "TradeAgent.App");

        foreach (var surface in new[] { "MainWindow.cs", "OnboardingView.cs" })
        {
            var source = Code(Path.Combine(app, surface));
            Assert.Contains("await _host.StartTheAiAsync();", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Agent.PrepareAsync(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Agent.StartAsync(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("RuntimeCatalog.Require(", source, StringComparison.Ordinal);
        }

        var host = Code(Path.Combine(app, "AppHost.cs"));
        Assert.Equal(1, Occurrences(host, "await Agent.PrepareAsync("));
        Assert.Equal(1, Occurrences(host, "await Agent.StartAsync("));
        Assert.Equal(1, Occurrences(host, "RuntimeCatalog.Require("));
        Assert.Equal(2, Occurrences(host, "StartTheAiAsync()"));   // its declaration and the resume's call
    }

    // ---- the restart --------------------------------------------------------------------------------

    /// <summary>
    /// WHAT THE LAST SESSION LEFT, written by one gateway and read by the host composed over the same
    /// database afterwards — which is what a restart is. The chosen runtime is the probe below, and the
    /// register handed back is the one the host's AI processes report to.
    /// </summary>
    static async Task<(AppHost Host, string DatabasePath, AgentPresence Presence)> Restarted(
        Action<TradeAgentSettings> lastSession, bool setupFinished = true, bool slowVersion = false)
    {
        RuntimeCatalog.SaveOverrides([ProbeRuntime(slowVersion)]);

        var path = Path.Combine(TestEnv.Home, $"resume-{Guid.NewGuid():n}.db");
        var db = new Database(path);
        var connector = new FakeConnector(new FakeBroker());
        await using (var before = new TradingGateway(db, connector, new HealthRegistry()))
            before.Update(s => { s.SelectedRuntimeId = Probe; lastSession(s); });
        if (setupFinished) new OnboardingStore(db).Complete(OnboardingStep.SETUP_COMPLETE);

        await connector.ConnectAsync();
        var presence = new AgentPresence();
        return (AppHost.Composed(db, connector, presence), path, presence);
    }

    /// <summary>The loop and the AI stopped before the database they write to is closed.</summary>
    static async Task Close(AppHost host)
    {
        await host.Mission.PauseAsync();
        await host.Agent.StopAsync();
        await host.Gateway.DisposeAsync();
        host.Db.Dispose();
    }

    /// <summary>
    /// A RUNTIME THAT RUNS, on every platform this suite runs on: it answers its version, and prints a
    /// line and exits 0 for any turn. Windows goes through <c>powershell -File</c> rather than a
    /// <c>.cmd</c>, because a <c>.cmd</c> is run by <c>cmd.exe</c>, which would parse the Situation the
    /// turn is handed as a command line of its own. <paramref name="slowVersion"/> makes it sleep half a
    /// minute before it answers its version, and answer a turn as quickly as ever.
    /// </summary>
    static RuntimeManifest ProbeRuntime(bool slowVersion = false)
    {
        var dir = Path.Combine(TestEnv.Home, $"resume-probe-{Guid.NewGuid():n}");
        Directory.CreateDirectory(dir);

        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(dir, "probe.ps1");
            var wait = slowVersion ? "Start-Sleep -Seconds 30; " : "";
            File.WriteAllText(script,
                $"if ($args.Count -gt 0 -and $args[0] -eq 'version') {{ {wait}'resume-probe 1.0.0'; exit 0 }}\r\n"
                + "'Nothing needs doing this turn.'\r\nexit 0\r\n");
            string[] run = ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script];
            return new RuntimeManifest
            {
                Id = Probe,
                DisplayName = "Resume probe",
                Executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                VersionArgs = [.. run, "version"],
                ExecArgs = [.. run, "{prompt}"],
                ResumeArgs = [.. run, "{prompt}"]
            };
        }

        var sh = Path.Combine(dir, "probe.sh");
        var pause = slowVersion ? "sleep 30; " : "";
        File.WriteAllText(sh,
            $"#!/bin/sh\n[ \"$1\" = \"version\" ] && {{ {pause}echo 'resume-probe 1.0.0'; exit 0; }}\n"
            + "echo 'Nothing needs doing this turn.'\n");
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new RuntimeManifest
        {
            Id = Probe,
            DisplayName = "Resume probe",
            Executable = sh,
            VersionArgs = ["version"],
            ExecArgs = ["{prompt}"],
            ResumeArgs = ["{prompt}"]
        };
    }

    /// <summary>
    /// Polls <paramref name="probe"/> until it answers, for sixty seconds. A wait that runs out says
    /// what it waited for AND what <paramref name="state"/> reads at that deadline, because "it never
    /// happened" and "it was still happening" are one sentence otherwise.
    /// </summary>
    static async Task<T> Until<T>(Func<T?> probe, string because, Func<string> state) where T : class
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (probe() is { } found) return found;
            await Task.Delay(25);
        }
        var last = probe();
        if (last is null) Assert.Fail($"{because}. At the 60 s deadline: {state()}");
        return last!;
    }

    /// <summary>
    /// WHAT THE LOOP WAS DOING WHEN THE WAIT RAN OUT, so that the next red decides which of two
    /// failures it is (<c>U-test-hygiene-3</c> item 4). windows-latest run 37612881764 ran out of the
    /// wait above after 1 m 48 s saying only that the wake was never taken by a turn — and a turn still
    /// in flight at the deadline (the probe is <c>powershell.exe</c>, whose first launch beside the
    /// suite has measured over 20 s) reads exactly like a turn that never launched. The first is a wait
    /// shorter than a runner's turn, an argument for <c>Timing</c> made with measured numbers; the
    /// second is the product. So: whether a turn took the wake and when it launched, whether an agent
    /// process is alive and when one last was, and the loop's own reading — its state, turns, error
    /// count and next turn — each guarded, so a reading that throws says so and the rest are still given.
    /// </summary>
    static string LoopAt(AppHost host, string wake, AgentPresence presence, DateTimeOffset resumed)
    {
        string When(DateTimeOffset at) => $"{at:O} ({(at - resumed).TotalSeconds:0.0} s after the resume)";
        static string Read(Func<string> reading)
        {
            try { return reading(); }
            catch (Exception e) { return $"<unreadable: {e.GetType().Name}: {e.Message}>"; }
        }

        var turn = Read(() => host.Wakes!.Get(wake) is not { } e
            ? "the wake is not in the queue"
            : e.ConsumedAt is { } at
                ? $"a turn took the wake — launch {e.ConsumedBy ?? "<none>"} at {When(at)}, disposition {e.Disposition ?? "none yet"}"
                : $"no turn took the wake (due {e.DueAt:O})");
        var alive = Read(() => $"agent processes alive {presence.Live}, presence.LastAliveAt " +
                               (presence.LastAliveAt is { } last ? When(last) : "never"));
        var loop = Read(() =>
        {
            var s = host.Mission.Status;
            return $"the loop {s.State} (running {host.Mission.Running}), {s.Turns} turn(s), " +
                   $"{s.ConsecutiveErrors} consecutive error(s), next turn {(s.NextTurnAt is { } next ? When(next) : "none")}" +
                   (s.WaitingFor is { Length: > 0 } waiting ? $", waiting for {waiting}" : "") +
                   (s.LastTurnFirstLine is { Length: > 0 } line ? $", last turn's first line \"{line}\"" : "");
        });
        var ai = Read(() => $"the AI running {host.Agent.Running}");
        return $"read at {When(DateTimeOffset.UtcNow)} — {turn}; {alive}; {loop}; {ai}";
    }

    /// <summary>A source file with its whole-line comments blanked, so prose about a call is not one.</summary>
    static string Code(string file) =>
        string.Join("\n", File.ReadAllLines(file).Select(l => l.TrimStart().StartsWith("//") ? "" : l));

    static int Occurrences(string text, string what)
    {
        var n = 0;
        for (var at = text.IndexOf(what, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(what, at + what.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// PAUSE, STOP, A TURN'S END AND THE APP'S QUIT OR DEATH END THE TURN'S WHOLE PROCESS TREE (U-agent-tree).
///
/// <para><b>What was measured before this existed</b> (survey s-agent-tree, this Mac, five probes, all red):
/// with no launcher, all six processes of a hung turn alive after Pause and Stop, presence at 0 and the
/// launch row ENDED; with the launcher, a child that made itself its own group and one that made itself
/// its own session alive through Pause, Stop and the host's exit; a finished turn's group child alive;
/// the app's dispose mid-turn left all six alive. These tests are those probes as assertions.</para>
///
/// <para><b>What is driven.</b> The host's own composition (<c>AppHost.Composed</c>) and its own resume,
/// as <see cref="ResumeOnStartTests"/> drives them, so the turn is a real mission turn through the real
/// <see cref="CliAgentRuntime"/>, meter and loop. The turn is a <c>/bin/sh</c> script that writes the pid
/// of every process it starts; "dead" is the pid gone or reused — the start time read when the pid was
/// recorded no longer matches. The launcher is deployed as <c>ContainmentTests.DeployLauncher</c>
/// deploys it: a shim that runs the built <c>trade.dll</c> with the flag.</para>
///
/// <para><b>Cleanup kills only what this test recorded</b>, by pid, and only while that pid still has the
/// start time it was recorded with — never by name, which is the same proof the product's teardown keeps.</para>
/// </summary>
// The probe runtime goes into runtimes.json under the shared test home, so this class may not run beside
// the tests that corrupt that file on purpose; the launcher in the shared bin folder is the other reason.
[Collection(VendorOverrideFiles.Name)]
public class AgentTreeTests : IDisposable
{
    const string Probe = "tree-probe";

    /// <summary>The brief's bound: every process of the turn is gone this long after the operator's action.</summary>
    static readonly TimeSpan DeadWithin = TimeSpan.FromSeconds(5);

    readonly ConcurrentBag<Member> _recorded = [];
    readonly List<Process> _ours = [];
    readonly string? _launcherBefore;
    readonly UnixFileMode? _launcherModeBefore;

    public AgentTreeTests()
    {
        var exe = LauncherPath;
        if (!File.Exists(exe)) return;
        _launcherBefore = File.ReadAllText(exe);
        if (!OperatingSystem.IsWindows()) _launcherModeBefore = File.GetUnixFileMode(exe);
    }

    public void Dispose()
    {
        Reap();
        foreach (var p in _ours)
        {
            try { if (!p.HasExited) p.Kill(); } catch (Exception) { /* already gone */ }
            p.Dispose();
        }

        // The launcher this class found is put back as it was: another class in this collection
        // deployed its own stand-in, and a test that leaves the shared bin folder changed is a test
        // that decides what the next one runs through.
        try
        {
            var exe = LauncherPath;
            if (_launcherBefore is null) { if (File.Exists(exe)) File.Delete(exe); }
            else
            {
                File.WriteAllText(exe, _launcherBefore);
                if (_launcherModeBefore is { } mode && !OperatingSystem.IsWindows()) File.SetUnixFileMode(exe, mode);
            }
        }
        catch (IOException) { }

        if (File.Exists(RuntimeCatalog.OverridePath)) File.Delete(RuntimeCatalog.OverridePath);
    }

    // ---- (a) a paused turn ----------------------------------------------------------------------------

    /// <summary>
    /// (a) PAUSE, WITH THE LAUNCHER — the product's state. Every process of the turn is gone within five
    /// seconds of Pause, including (d) the child that made itself its own process group and the one that
    /// made itself its own session, which the group kill orphaned and left running.
    /// </summary>
    [Fact]
    public async Task A_paused_turn_held_in_its_own_session_ends_its_whole_tree()
    {
        if (OperatingSystem.IsWindows()) return;   // the Unix arm; Windows' job is covered beside it

        var turn = await HungTurn(launcher: true);
        try
        {
            await turn.Host.Mission.PauseAsync();
            await AssertAllDead(turn, "Mission.PauseAsync, the launcher deployed");
        }
        finally { await Close(turn.Host); }
    }

    /// <summary>
    /// (a) PAUSE, WITH NO LAUNCHER — the product's "not held" fallback, and the state of a home with no
    /// trade command. The turn then shares the app's own group and session, so the only proof a pid is
    /// the turn's is that it descends from the turn's leader: the probe here is the tree that proof can
    /// reach (no process detached from the leader before the press), and every one of them must end.
    /// </summary>
    [Fact]
    public async Task A_paused_turn_with_no_launcher_ends_every_process_that_descends_from_its_leader()
    {
        if (OperatingSystem.IsWindows()) return;

        var turn = await HungTurn(launcher: false, orphan: false);
        try
        {
            await turn.Host.Mission.PauseAsync();
            await AssertAllDead(turn, "Mission.PauseAsync, no launcher deployed");
        }
        finally { await Close(turn.Host); }
    }

    /// <summary>
    /// (a) PAUSE ENDS A RESEARCH TURN'S TREE TOO. Pause used to cancel the chair's conversation and reach
    /// every other role's turn only through the loop's token.
    /// </summary>
    [Fact]
    public async Task A_paused_research_turn_ends_its_whole_tree()
    {
        if (OperatingSystem.IsWindows()) return;

        var turn = await HungTurn(launcher: true, role: CouncilRoles.Research);
        try
        {
            await turn.Host.Mission.PauseAsync();
            await AssertAllDead(turn, "Mission.PauseAsync on a Research turn");
        }
        finally { await Close(turn.Host); }
    }

    // ---- (b) a stopped turn ---------------------------------------------------------------------------

    /// <summary>(b) STOP THE AI, with no Pause first: the same whole tree, gone within five seconds.</summary>
    [Fact]
    public async Task A_stopped_turn_ends_its_whole_tree()
    {
        if (OperatingSystem.IsWindows()) return;

        var turn = await HungTurn(launcher: true);
        try
        {
            await turn.Host.Agent.StopAsync();
            await AssertAllDead(turn, "Agent.StopAsync, no Pause first");
        }
        finally { await Close(turn.Host); }
    }

    // ---- (c) a finished turn's leftover ---------------------------------------------------------------

    /// <summary>
    /// (c) A TURN THAT ENDED BY ITSELF, exit 0, leaving a process in its session behind it: the leftover
    /// ends with the turn. It used to live on — Dispose killed nothing once the leader had exited, and a
    /// group read from a gone pid names nothing.
    /// </summary>
    [Fact]
    public async Task A_finished_turns_leftover_ends_with_its_turn()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir("finished");
        // The turn ends by itself, exit 0 — once the test has recorded it, so the leftover is known by pid
        // and start before anything could end it.
        var body =
            $"echo $$ > \"{dir}/leader.pid\"\n" +
            "/bin/sleep 905 > /dev/null 2>&1 &\n" +
            $"echo $! > \"{dir}/leftover.pid\"\n" +
            "echo 'Nothing needs doing this turn.'\n" +
            $"while [ ! -f \"{dir}/go\" ]; do /bin/sleep 0.1; done\n" +
            "exit 0\n";
        var turn = await Start(dir, body, launcher: true, role: null, waitFor: ["leader.pid", "leftover.pid"]);
        try
        {
            File.WriteAllText(Path.Combine(dir, "go"), "");
            // The turn ends by itself: its wake is settled by the turn's committed transition.
            await Until(() => turn.Host.Wakes!.Get(turn.Wake) is { Disposition: not null },
                "the finished turn never committed");
            await AssertAllDead(turn, "the turn's own end (exit 0)", only: ["leftover"]);
        }
        finally { await Close(turn.Host); }
    }

    // ---- (d) children that left the group or the session ----------------------------------------------

    /// <summary>
    /// (d) A CHILD THAT LEFT THE TURN'S GROUP AND WAS THEN ORPHANED — its parent exited, so no parent link
    /// reaches it and the group kill does not either — and a child that left the turn's SESSION while its
    /// parent still runs. Both end on Pause.
    /// </summary>
    [Fact]
    public async Task Children_that_left_the_turns_group_or_session_end_with_it()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir("left");
        var body =
            $"echo $$ > \"{dir}/leader.pid\"\n" +
            $"/bin/sh -c '/usr/bin/perl -e \"setpgrp(0,0); exec @ARGV\" /bin/sleep 906 > /dev/null 2>&1 & echo $! > \"{dir}/leftgroup.pid\"'\n" +
            $"/usr/bin/perl -MPOSIX -e 'POSIX::setsid(); exec @ARGV' /bin/sleep 907 > /dev/null 2>&1 &\n" +
            $"echo $! > \"{dir}/leftsession.pid\"\n" +
            "echo 'working on it'\n" +
            "/bin/sleep 900 &\n" +
            $"echo $! > \"{dir}/fg.pid\"\n" +
            "wait $!\n";
        var turn = await Start(dir, body, launcher: true, role: null,
            waitFor: ["leader.pid", "leftgroup.pid", "leftsession.pid", "fg.pid"]);
        try
        {
            await turn.Host.Mission.PauseAsync();
            await AssertAllDead(turn, "Mission.PauseAsync over children that left the group or the session");
        }
        finally { await Close(turn.Host); }
    }

    // ---- (e) the app's dispose mid-turn ---------------------------------------------------------------

    /// <summary>
    /// (e) THE APP'S QUIT WITH A TURN IN FLIGHT: <c>AppHost.DisposeAsync</c>, which the lifetime's
    /// ShutdownRequested runs, ends the whole tree. It used to stop no AI at all.
    /// </summary>
    [Fact]
    public async Task The_apps_dispose_mid_turn_ends_the_turns_whole_tree()
    {
        if (OperatingSystem.IsWindows()) return;

        var turn = await HungTurn(launcher: true);
        try
        {
            await turn.Host.DisposeAsync();
            await AssertAllDead(turn, "AppHost.DisposeAsync mid-turn");
        }
        finally { await Close(turn.Host); }
    }

    // ---- (f) the app's death --------------------------------------------------------------------------

    /// <summary>
    /// (f) THE APP'S DEATH — a crash, a force quit — with a turn in flight. The launcher's parent is killed
    /// with SIGKILL, so nothing of the app runs to clean up: whatever ends the turn's tree has to be
    /// something that outlives the app. Unix; on Windows the job closes with the app's last handle, and
    /// that arm is named to U-contain-seats.
    /// </summary>
    [Fact]
    public async Task The_apps_death_ends_the_turns_whole_tree()
    {
        if (OperatingSystem.IsWindows()) return;

        DeployLauncher();
        var dir = NewDir("death");
        var sh = WriteScript(dir, HungBody(dir, orphan: true));

        // THE APP, as a process of its own that this test can kill: it starts the launcher exactly as
        // ProcessContainment does — the launcher, the flag, the program — and waits.
        var app = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        app.ArgumentList.Add("-c");
        app.ArgumentList.Add($"\"{LauncherPath}\" {ContainedLaunch.Flag} /bin/sh \"{sh}\" go & echo $! > \"{dir}/launcher.pid\"; wait");
        var host = Process.Start(app)!;
        _ours.Add(host);

        var members = await Record(dir, ["launcher.pid", .. HungFiles(orphan: true)]);
        Assert.True(Alive(members.Single(m => m.Name == "leader")), "the probe turn never ran under the launcher");

        host.Kill();   // SIGKILL: the app dies and runs nothing on its way out
        await host.WaitForExitAsync();

        await AssertAllDead(members, "the app's death (its process killed with SIGKILL)");
    }

    // ---- (g) the row and the presence end after the tree ----------------------------------------------

    /// <summary>
    /// (g) THE LAUNCH ROW READS ENDED AND PRESENCE READS 0 ONLY ONCE THE TREE IS DEAD. Watched while it
    /// happens: the first moment either one says the turn is over, every process of the turn must already
    /// be gone — otherwise the ledger has closed over a paid process still running, and the inbox
    /// attestation has measured a window an agent was inside.
    /// </summary>
    [Fact]
    public async Task A_paused_turns_row_ends_and_its_presence_ends_only_after_its_tree_is_dead()
    {
        if (OperatingSystem.IsWindows()) return;

        var turn = await HungTurn(launcher: true);
        try
        {
            Assert.Equal(1, turn.Presence.Live);
            var said = new ConcurrentQueue<string>();
            var rowSeen = false;
            var presenceSeen = false;
            using var stop = new CancellationTokenSource();
            var watch = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested && !(rowSeen && presenceSeen))
                {
                    if (!presenceSeen && turn.Presence.Live == 0)
                    {
                        presenceSeen = true;
                        if (Living(turn.Members) is { Count: > 0 } running)
                            said.Enqueue($"presence.Live read 0 while {Names(running)} still ran");
                    }
                    if (!rowSeen && RowEnded(turn.Host))
                    {
                        rowSeen = true;
                        if (Living(turn.Members) is { Count: > 0 } running)
                            said.Enqueue($"the turn's ai_attempt row read ENDED while {Names(running)} still ran");
                    }
                    await Task.Delay(2);
                }
            });

            await turn.Host.Mission.PauseAsync();
            await Until(() => rowSeen && presenceSeen, "the paused turn's row never ended, or its presence never did");
            await stop.CancelAsync();
            await watch;

            Assert.True(said.IsEmpty, string.Join("; ", said) + Ps(turn.Members));
            await AssertAllDead(turn, "Mission.PauseAsync");
        }
        finally { await Close(turn.Host); }
    }

    // ---- (h) the guard --------------------------------------------------------------------------------

    /// <summary>
    /// (h) NOTHING OUTSIDE THE TREE IS TOUCHED. A process tree running the turn's own program — the same
    /// script, the same user, started the moment before the turn — is alive after the turn's Pause, every
    /// process of it. The teardown kills what it proves is the turn's, never what merely looks like it.
    /// </summary>
    [Fact]
    public async Task A_process_outside_the_tree_running_the_turns_own_command_survives_the_pause()
    {
        if (OperatingSystem.IsWindows()) return;

        var outsideDir = NewDir("outside");
        var turn = await HungTurn(launcher: true, before: sh =>
        {
            // The turn's own script, as a process of the test's: it writes its pids into a folder of its own.
            var psi = new ProcessStartInfo("/bin/sh")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            psi.ArgumentList.Add(sh);
            psi.ArgumentList.Add("outside");
            psi.Environment["TREE_DIR"] = outsideDir;
            _ours.Add(Process.Start(psi)!);
        });
        var outside = await Record(outsideDir, HungFiles(orphan: true));
        try
        {
            await turn.Host.Mission.PauseAsync();
            await Task.Delay(DeadWithin);

            var gone = outside.Where(m => !Alive(m)).ToList();
            Assert.True(gone.Count == 0,
                $"the turn's teardown ended {Names(gone)}, which ran the turn's command OUTSIDE its tree" + Ps(outside));
            await AssertAllDead(turn, "Mission.PauseAsync beside a tree running the same command");
        }
        finally { await Close(turn.Host); }
    }

    // ---- the hung turn --------------------------------------------------------------------------------

    sealed record Member(string Name, int Pid, DateTime Start);

    sealed record Turn(AppHost Host, AgentPresence Presence, string Dir, string Wake, IReadOnlyList<Member> Members);

    /// <summary>
    /// The turn's leader (<c>/bin/sh probe.sh</c>) starts: a plain background child (sleep 901); a
    /// grandchild whose parent exits at once, so its parent is init and only its group and session still
    /// say whose it is (sleep 902); a child that made itself its own process group (sleep 903, perl
    /// setpgrp — the shape codex's own setpgid and posix_spawnattr_setpgroup allow); a child that made
    /// itself its own session (sleep 904, perl setsid); and then waits on a foreground sleep 900 that
    /// holds the turn's stdout. <c>TREE_DIR</c> moves the pid files, so the guard can run the same script.
    /// </summary>
    static string HungBody(string dir, bool orphan) =>
        $"D=\"${{TREE_DIR:-{dir}}}\"\n" +
        "echo $$ > \"$D/leader.pid\"\n" +
        "/bin/sleep 901 > /dev/null 2>&1 &\n" +
        "echo $! > \"$D/child.pid\"\n" +
        (orphan ? "/bin/sh -c '/bin/sleep 902 > /dev/null 2>&1 & echo $! > \"$1/orphan.pid\"' sh \"$D\"\n" : "") +
        "/usr/bin/perl -e 'setpgrp(0,0); exec @ARGV' /bin/sleep 903 > /dev/null 2>&1 &\n" +
        "echo $! > \"$D/ownpgrp.pid\"\n" +
        "/usr/bin/perl -MPOSIX -e 'POSIX::setsid(); exec @ARGV' /bin/sleep 904 > /dev/null 2>&1 &\n" +
        "echo $! > \"$D/ownsess.pid\"\n" +
        "echo 'working on it'\n" +
        "/bin/sleep 900 &\n" +
        "echo $! > \"$D/fg.pid\"\n" +
        "wait $!\n";

    static string[] HungFiles(bool orphan) => orphan
        ? ["leader.pid", "child.pid", "orphan.pid", "ownpgrp.pid", "ownsess.pid", "fg.pid"]
        : ["leader.pid", "child.pid", "ownpgrp.pid", "ownsess.pid", "fg.pid"];

    async Task<Turn> HungTurn(bool launcher, string? role = null, bool orphan = true, Action<string>? before = null)
    {
        var dir = NewDir(role ?? "hung");
        return await Start(dir, HungBody(dir, orphan), launcher, role, HungFiles(orphan), before);
    }

    /// <summary>
    /// The host composed over a database the last session wrote — the AI working, resuming on start, the
    /// probe chosen — one wake due for <paramref name="role"/>, and the host's own resume. Returns once the
    /// turn has written every pid it was asked for, each recorded with its start time.
    /// </summary>
    async Task<Turn> Start(string dir, string body, bool launcher, string? role, string[] waitFor,
        Action<string>? before = null, string[]? mayHaveExited = null)
    {
        if (launcher) DeployLauncher();
        else if (File.Exists(LauncherPath)) File.Delete(LauncherPath);
        Assert.Equal(launcher, ProcessContainment.DefaultLauncher() is not null);

        var sh = WriteScript(dir, body);
        RuntimeCatalog.SaveOverrides([new RuntimeManifest
        {
            Id = Probe, DisplayName = "Tree probe", Executable = sh,
            VersionArgs = ["version"], ExecArgs = ["{prompt}"], ResumeArgs = ["{prompt}"]
        }]);

        var db = new Database(Path.Combine(TestEnv.Home, $"tree-{Guid.NewGuid():n}.db"));
        var connector = new FakeConnector(new FakeBroker());
        await using (var last = new TradingGateway(db, connector, new HealthRegistry()))
            last.Update(s =>
            {
                s.SelectedRuntimeId = Probe;
                s.AiWorksOnItsOwn = true;
                s.ResumeAiOnStart = true;
                if (role is { } r && r != CouncilRoles.Operations) s.RoleRuntime[r] = Probe;
            });
        new OnboardingStore(db).Complete(OnboardingStep.SETUP_COMPLETE);
        await connector.ConnectAsync();

        var presence = new AgentPresence();
        var host = AppHost.Composed(db, connector, presence);

        before?.Invoke(sh);

        var due = DateTimeOffset.UtcNow.AddMinutes(-1);
        var whose = role ?? CouncilRoles.Operations;
        var wake = MissionEventIds.ForRole(MissionEventIds.Review(due), whose);
        Assert.True(host.Wakes!.RaiseDue(wake, MissionEventKind.Review, due.AddMinutes(-30), due, role: whose));
        await host.ResumeOnStartAsync();
        Assert.True(host.Agent.Running, "the probe runtime did not start");

        var members = await Record(dir, waitFor, mayHaveExited);
        return new Turn(host, presence, dir, wake, members);
    }

    /// <summary>
    /// Waits for every pid file, then records each pid with the start time it has NOW, while it is known
    /// to be the turn's. Every later "is it alive" and the cleanup are asked of that pair.
    /// </summary>
    async Task<IReadOnlyList<Member>> Record(string dir, string[] files, string[]? mayHaveExited = null)
    {
        for (var i = 0; i < 600 && !files.All(f => Pid(dir, f) > 0); i++) await Task.Delay(100);
        Assert.True(files.All(f => Pid(dir, f) > 0),
            $"the probe never wrote its pids: {string.Join(", ", files.Where(f => Pid(dir, f) <= 0))}");

        // RECORDED FIRST, every one still running, so the cleanup reaches it whatever is asserted next.
        var members = new List<Member>();
        var gone = new List<string>();
        foreach (var f in files)
        {
            var pid = Pid(dir, f);
            if (StartOf(pid) is not { } start) { gone.Add($"{f} (pid {pid})"); continue; }
            var m = new Member(Path.GetFileNameWithoutExtension(f), pid, start);
            members.Add(m);
            _recorded.Add(m);
        }
        Assert.True(gone.All(g => mayHaveExited?.Any(g.StartsWith) == true),
            $"the probe's pids were not running when it was read: {string.Join(", ", gone)}");

        await Task.Delay(500);   // the orphan's parent exits, so its parent link is gone before anything is pressed
        return members;
    }

    // ---- reading the tree -----------------------------------------------------------------------------

    static DateTime? StartOf(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.StartTime;
        }
        catch (Exception) { return null; }
    }

    /// <summary>The recorded process, still running: the pid there and carrying the start time it was recorded with.</summary>
    static bool Alive(Member m) => Posix.Alive(m.Pid) && StartOf(m.Pid) == m.Start;

    static List<Member> Living(IEnumerable<Member> members) => [.. members.Where(Alive)];

    static string Names(IEnumerable<Member> members) =>
        string.Join(", ", members.Select(m => $"{m.Name} (pid {m.Pid})"));

    async Task AssertAllDead(Turn turn, string after, string[]? only = null) =>
        await AssertAllDead(only is null ? turn.Members : [.. turn.Members.Where(m => only.Contains(m.Name))], after);

    static async Task AssertAllDead(IReadOnlyList<Member> members, string after)
    {
        var deadline = DateTime.UtcNow + DeadWithin;
        List<Member> running;
        while ((running = Living(members)).Count > 0 && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(running.Count == 0,
            $"{DeadWithin.TotalSeconds:0} s after {after}, the turn's tree still ran: {Names(running)}" + Ps(members));
    }

    static bool RowEnded(AppHost host)
    {
        try
        {
            return new AiAttemptStore(host.Db)
                .Between(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1))
                .Any(r => r.State == AiAttemptState.ENDED);
        }
        catch (Exception) { return false; }
    }

    /// <summary>What <c>ps</c> says of the recorded pids, for a red that has to say which process is which.</summary>
    static string Ps(IEnumerable<Member> members)
    {
        try
        {
            var psi = new ProcessStartInfo("/bin/ps") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-o", "pid,ppid,pgid,stat,command", "-p", string.Join(',', members.Select(m => m.Pid)) })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            var sessions = string.Join(", ", members.Select(m => $"{m.Name}={m.Pid} sid {Posix.SessionOf(m.Pid)}"));
            return $"\n{sessions}\n{text}";
        }
        catch (Exception e) { return $"\n(ps unreadable: {e.Message})"; }
    }

    static int Pid(string dir, string file)
    {
        try { return int.Parse(File.ReadAllText(Path.Combine(dir, file)).Trim()); }
        catch (Exception) { return 0; }
    }

    static async Task Until(Func<bool> done, string because)
    {
        for (var i = 0; i < 600 && !done(); i++) await Task.Delay(100);
        Assert.True(done(), because);
    }

    /// <summary>
    /// Kills what this test recorded and nothing else: by pid, and only while that pid still carries the
    /// start time it was recorded with. A pid the system has since given to another process is left alone.
    /// </summary>
    void Reap()
    {
        foreach (var m in _recorded)
        {
            if (!Alive(m)) continue;
            try { using var p = Process.GetProcessById(m.Pid); if (p.StartTime == m.Start) p.Kill(); }
            catch (Exception) { /* gone between the look and the kill */ }
        }
    }

    // ---- the launcher, the script, the host ---------------------------------------------------------

    static string LauncherPath => Path.Combine(Paths.Bin, ToolDeployer.TradeCliName);

    /// <summary>The launcher as <c>ContainmentTests.DeployLauncher</c> deploys it: the built trade CLI, run with the flag.</summary>
    static void DeployLauncher()
    {
        Directory.CreateDirectory(Paths.Bin);
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(host) || !File.Exists(host)) host = Environment.ProcessPath;
        File.WriteAllText(LauncherPath, $"#!/bin/sh\nexec \"{host}\" \"{TradeCliDll()}\" \"$@\"\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(LauncherPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    static string WriteScript(string dir, string body)
    {
        var sh = Path.Combine(dir, "probe.sh");
        File.WriteAllText(sh, "#!/bin/sh\n[ \"$1\" = \"version\" ] && { echo 'tree-probe 1.0.0'; exit 0; }\n" + body);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return sh;
    }

    static string NewDir(string name)
    {
        var dir = Path.Combine(TestEnv.Home, $"tree-{name}-{Guid.NewGuid():n}"[..Math.Min(48, name.Length + 38)]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>The loop and the AI stopped before the database they write to is closed. Every step guarded.</summary>
    static async Task Close(AppHost host)
    {
        try { await host.Mission.PauseAsync(); } catch (Exception) { }
        try { await host.Agent.StopAsync(); } catch (Exception) { }
        try { await host.Gateway.DisposeAsync(); } catch (Exception) { }
        try { host.Db.Dispose(); } catch (Exception) { }
    }

    static string TradeCliDll()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "TradeAgent.sln"))) d = d.Parent;
        Assert.NotNull(d);
        var bin = Path.Combine(d!.FullName, "src", "TradeAgent.TradeCli", "bin");
        var hit = Directory.GetFiles(bin, "trade.dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        Assert.NotNull(hit);
        return hit!;
    }
}

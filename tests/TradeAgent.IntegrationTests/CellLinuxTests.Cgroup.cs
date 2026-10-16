using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE PER-ATTEMPT CGROUP, ROW BY ROW (<c>U-cell-linux-b</c>; the § 9.3 rows the survey left to "-b": cancellation, restart, session
/// changes, grant expiry across cgroups, pid translation). Each row opens its own root, <c>&lt;service&gt;/rows-&lt;guid&gt;</c>, beside
/// <c>&lt;service&gt;/runner</c>, where CI's Cell step moved the test run (a cgroup holding processes cannot hand controllers down) —
/// the service being the transient system service of the host's exact shape, <c>User=</c> and <c>Delegate=yes</c>.
/// </summary>
public partial class CellLinuxTests
{
    /// <summary>One row's delegated root, removed with everything still in it when the row ends.</summary>
    sealed class RowsRoot : IDisposable
    {
        public RowsRoot(AttemptCgroups cgroups, string dir)
        {
            Cgroups = cgroups;
            Dir = dir;
        }

        public AttemptCgroups Cgroups { get; }
        public string Dir { get; }

        public void Dispose()
        {
            if (!Directory.Exists(Dir)) return;
            try { File.WriteAllText(Path.Combine(Dir, "cgroup.kill"), "1"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            for (var i = 0; i < 300 && File.Exists(Path.Combine(Dir, "cgroup.events")) &&
                            !File.ReadAllText(Path.Combine(Dir, "cgroup.events")).Contains("populated 0", StringComparison.Ordinal); i++)
                Thread.Sleep(10);
            foreach (var sub in Directory.EnumerateDirectories(Dir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).ToList())
                try { Directory.Delete(sub); } catch (IOException) { }
            try { Directory.Delete(Dir); } catch (IOException) { }
        }
    }

    /// <summary>A delegated root of this row's own, beside the runner's cgroup — a missing one FAILS naming the step that should have made it.</summary>
    static RowsRoot Rows()
    {
        var mine = AttemptCgroups.OwnCgroup();
        Require(mine is not null && Path.GetFileName(mine) == "runner",
            $"the test run in <service>/runner (CI's Cell step moves it there under Delegate=yes); this process is in {mine ?? "an unreadable cgroup"}");
        var dir = Path.Combine(Path.GetDirectoryName(mine!)!, "rows-" + Guid.NewGuid().ToString("n")[..12]);
        var cgroups = AttemptCgroups.Open(dir);
        Require(cgroups.Held, $"a delegated root at {dir}: {cgroups.Refusal}");
        return new RowsRoot(cgroups, dir);
    }

    static ProcessStartInfo Plain(string program, params string[] args)
    {
        var psi = new ProcessStartInfo(program)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    static async Task<Ran> Finish(ContainedProcess held, TimeSpan? bound = null)
    {
        held.Process.StandardInput.Close();
        var so = held.Process.StandardOutput.ReadToEndAsync();
        var se = held.Process.StandardError.ReadToEndAsync();
        try { await held.Process.WaitForExitAsync().WaitAsync(bound ?? TimeSpan.FromSeconds(90)); }
        catch (TimeoutException) { Assert.Fail($"the launch did not finish; stderr so far: {(se.IsCompleted ? await se : "")}"); }
        return new Ran(held.Process.ExitCode, await so, await se, held.Mechanism);
    }

    /// <summary>The <c>0::</c> line a process in <paramref name="dir"/> reads from its own <c>/proc/self/cgroup</c>.</summary>
    static string LineOf(string dir) => "0::" + dir[AttemptCgroups.Mount.Length..];

    static async Task<string> Await(string file, TimeSpan bound)
    {
        var until = DateTime.UtcNow + bound;
        while (!File.Exists(file) && DateTime.UtcNow < until) await Task.Delay(50);
        Assert.True(File.Exists(file), $"{file} never appeared");
        return File.ReadAllText(file).Trim();
    }

    static bool Alive(int pid, ulong start) => ProcessTable.Read(pid) is { Zombie: false } e && e.Start == start;

    static string Scratch(string name)
    {
        var d = Path.Combine(TestEnv.Home, "cg-" + name + "-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>A double fork with <c>setsid</c> between: the grandchild leaves the session and its parent exits, so init adopts it.</summary>
    const string DaemonPy = """
        import os, sys, time
        out = sys.argv[1]
        open(out + ".py", "w").write(open("/proc/self/cgroup").read())
        if os.fork() == 0:
            os.setsid()
            if os.fork() == 0:
                open(out + ".cg", "w").write(open("/proc/self/cgroup").read())
                open(out + ".tmp", "w").write(f"{os.getpid()} {os.getsid(0)}"); os.rename(out + ".tmp", out)
                time.sleep(300)
                os._exit(0)
            os._exit(0)
        os.wait()
        time.sleep(300)
        """;

    // ---- cancellation -------------------------------------------------------------------------------------------

    /// <summary>
    /// (a) CANCELLATION ENDS WHAT LEFT THE SESSION. A setsid'd, double-forked daemon is reparented to init and in a session of its
    /// own: nothing the session teardown can test says it is the attempt's. Its cgroup says so, and <c>cgroup.kill</c> ends it.
    /// </summary>
    [LinuxFact]
    public async Task Cancelling_an_attempt_ends_a_setsid_daemon_that_left_its_session()
    {
        PlatformIsACells();
        using var rows = Rows();
        var dir = Scratch("cancel");
        var pidFile = Path.Combine(dir, "daemon");
        using var held = ProcessContainment.Start(Plain("/usr/bin/python3", "-I", "-c", DaemonPy, pidFile), Launcher(),
            requireHeld: true, attemptId: "cancel", cgroups: rows.Cgroups);
        held.Process.StandardInput.Close();
        var said = (await Await(pidFile, TimeSpan.FromSeconds(30))).Split(' ');
        var daemon = int.Parse(said[0], CultureInfo.InvariantCulture);
        var entry = ProcessTable.Read(daemon);
        Assert.True(entry is { Zombie: false }, $"the daemon {daemon} was not running");
        try
        {
            Assert.NotEqual(held.Process.Id, entry!.Value.Session);   // it left the attempt's session

            held.Kill();

            Assert.False(Alive(daemon, entry.Value.Start),
                $"the setsid daemon {daemon} that left the attempt's session outlived its cancel ({held.Mechanism})");
            Assert.True(held.Ended!.Ended, held.Ended.Sentence);
            Assert.True(held.Cgroup is { } cg && !Directory.Exists(cg.Dir), "the attempt's cgroup was not removed after its cancel");
        }
        finally
        {
            if (Alive(daemon, entry!.Value.Start)) Posix.Kill(daemon);
        }
    }

    /// <summary>
    /// (b) BORN THERE. The supervisor, a shell, the shell it starts, the python that starts, and a setsid'd daemon of that python all
    /// read the attempt's own cgroup — the launcher joined it before it started anything, and a new session changes no cgroup.
    /// </summary>
    [LinuxFact]
    public async Task Every_process_an_attempt_starts_is_born_in_its_cgroup()
    {
        PlatformIsACells();
        using var rows = Rows();
        var dir = Scratch("born");
        File.WriteAllText(Path.Combine(dir, "fork.py"), DaemonPy);
        File.WriteAllText(Path.Combine(dir, "one.sh"), $"cat /proc/$$/cgroup > {dir}/sh1\n/bin/sh {dir}/two.sh\n");
        File.WriteAllText(Path.Combine(dir, "two.sh"), $"cat /proc/$$/cgroup > {dir}/sh2\n/usr/bin/python3 -I {dir}/fork.py {dir}/daemon\n");
        using var held = ProcessContainment.Start(Plain("/bin/sh", Path.Combine(dir, "one.sh")),
            Launcher(), requireHeld: true, attemptId: "born", cgroups: rows.Cgroups);
        held.Process.StandardInput.Close();
        Assert.NotNull(held.Cgroup);
        var expected = LineOf(held.Cgroup!.Dir);
        try
        {
            await Await(Path.Combine(dir, "daemon"), TimeSpan.FromSeconds(30));
            Assert.Equal(expected, File.ReadAllText($"/proc/{held.Process.Id}/cgroup").Trim());
            foreach (var who in new[] { "sh1", "sh2", "daemon.py", "daemon.cg" })
                Assert.True(expected == (await Await(Path.Combine(dir, who), TimeSpan.FromSeconds(10))),
                    $"{who} was born in {File.ReadAllText(Path.Combine(dir, who)).Trim()}, not in the attempt's {expected}");
        }
        finally { held.Kill(); }
    }

    /// <summary>
    /// (c) A CELL STAYS IN ITS ATTEMPT. The same script outside a cell moves itself into a sibling cgroup (the control: the cgroup
    /// files are this account's, so an unconfined process can); inside a cell the write is refused and it is still in its attempt.
    /// </summary>
    [LinuxFact]
    public async Task A_cell_cannot_move_itself_out_of_its_attempts_cgroup()
    {
        PlatformIsACells();
        using var rows = Rows();
        var escape = Path.Combine(rows.Dir, "escape");
        Directory.CreateDirectory(escape);
        var script = $"if echo $$ > {escape}/cgroup.procs; then echo moved; else echo move-refused; fi; cat /proc/$$/cgroup";

        using (var outside = ProcessContainment.Start(Plain("/bin/sh", "-c", script), Launcher(), requireHeld: true,
                   attemptId: "control", cgroups: rows.Cgroups))
        {
            var ran = await Finish(outside);
            Says(ran, "moved", LineOf(escape));
        }

        var w = new World("cgmove");
        using var cell = ProcessContainment.Start(Plain("/bin/sh", "-c", script), w.Policy(), Launcher(), cgroups: rows.Cgroups,
            attemptId: "cell");
        Assert.NotNull(cell.Cgroup);
        var inCell = await Finish(cell);
        Says(inCell, "move-refused", LineOf(cell.Cgroup!.Dir));
    }

    // ---- restart ------------------------------------------------------------------------------------------------

    /// <summary>
    /// (d) THE RESTART SWEEP. Two attempts left as a crash of the app AND their supervisors leaves them — the supervisors SIGKILLed,
    /// their programs still running in their cgroups — and a cell policy file from before the start: the next start's sweep kills
    /// and removes both attempts and discards that file, and keeps a policy file written since.
    /// </summary>
    [LinuxFact]
    public async Task A_restart_kills_and_removes_every_leftover_attempt_and_policy_file()
    {
        if (!OperatingSystem.IsLinux()) return;   // a LinuxFact: skipped elsewhere before it gets here
        PlatformIsACells();
        using var rows = Rows();
        var dir = Scratch("restart");
        var left = new List<(ContainedProcess Held, int Pid, ulong Start, string Cgroup)>();
        var stale = Path.Combine(CellPolicy.Directory, new string('a', 64) + ".00000000000000aa.cell.json");
        string? fresh = null;
        try
        {
            for (var i = 0; i < 2; i++)
            {
                var held = ProcessContainment.Start(Plain("/bin/sh", "-c", $"sleep 300 & echo $! > {dir}/sleep{i}; wait"), Launcher(),
                    requireHeld: true, attemptId: $"crashed{i}", cgroups: rows.Cgroups);
                held.Process.StandardInput.Close();
                var pid = int.Parse(await Await(Path.Combine(dir, $"sleep{i}"), TimeSpan.FromSeconds(30)), CultureInfo.InvariantCulture);
                var start = ProcessTable.Read(pid)!.Value.Start;
                Posix.Kill(held.Process.Id);                     // the supervisor, as a crash ends it: nothing of it sweeps its session
                await held.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(Alive(pid, start), $"sleep {pid} did not outlive its supervisor, so this row tests nothing");
                left.Add((held, pid, start, held.Cgroup?.Dir ?? ""));
            }

            Directory.CreateDirectory(CellPolicy.Directory);
            File.WriteAllText(stale, "{}");
            File.SetUnixFileMode(stale, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-1));
            var startedAt = DateTime.UtcNow.AddSeconds(-1);
            fresh = Path.Combine(CellPolicy.Directory, new string('b', 64) + ".00000000000000bb.cell.json");
            File.WriteAllText(fresh, "{}");
            File.SetUnixFileMode(fresh, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            // THE NEXT START: the same root looked at again, and swept.
            var report = AttemptCgroups.Open(rows.Dir).Sweep(before: startedAt);

            foreach (var (_, pid, start, cgroup) in left)
            {
                Assert.False(Alive(pid, start), $"sleep {pid}, left running in {cgroup}, outlived the restart's sweep: {report.Sentence}");
                Assert.False(Directory.Exists(cgroup), $"the leftover attempt {cgroup} was not removed: {report.Sentence}");
            }
            Assert.Empty(Directory.GetDirectories(rows.Dir, AttemptCgroups.AttemptPrefix + "*"));
            Assert.False(File.Exists(stale), $"the cell policy file {stale}, from before the start, outlived the sweep: {report.Sentence}");
            Assert.True(File.Exists(fresh), "the sweep discarded a policy file written since the start");
        }
        finally
        {
            foreach (var (held, pid, start, _) in left)
            {
                if (Alive(pid, start)) Posix.Kill(pid);
                held.Dispose();
            }
            File.Delete(stale);
            if (fresh is not null) File.Delete(fresh);
        }
    }

    // ---- ceilings -----------------------------------------------------------------------------------------------

    const string ThreadsPy = """
        import os, sys, threading
        threading.stack_size(256 * 1024)
        stop = threading.Event()
        n = 0
        try:
            while n < int(sys.argv[1]):
                threading.Thread(target=stop.wait, daemon=True).start()
                n += 1
        except RuntimeError:
            pass
        print(f"threads={n}")
        try:
            pid = os.fork()
            if pid == 0:
                os._exit(0)
            os.waitpid(pid, 0)
            print("fork-ok")
        except OSError:
            print("fork-refused")
        sys.stdout.flush()
        os._exit(0)
        """;

    /// <summary>
    /// (e) THE PIDS CEILING. Outside any attempt the script starts more threads than the ceiling (the control); inside an attempt it
    /// stops short of <see cref="AttemptCgroups.PidsMax"/>, and a fork past it is refused. The memory ceiling is written as named.
    /// </summary>
    [LinuxFact]
    public async Task An_attempt_cannot_fork_past_its_pids_ceiling()
    {
        PlatformIsACells();
        var over = (AttemptCgroups.PidsMax + 64).ToString(CultureInfo.InvariantCulture);
        var control = await Outside("/usr/bin/python3", "-I", "-c", ThreadsPy, over);
        Says(control, $"threads={over}", "fork-ok");

        using var rows = Rows();
        using var held = ProcessContainment.Start(Plain("/usr/bin/python3", "-I", "-c", ThreadsPy, over), Launcher(),
            requireHeld: true, attemptId: "ceiling", cgroups: rows.Cgroups);
        Assert.NotNull(held.Cgroup);
        Assert.Equal(AttemptCgroups.PidsMax.ToString(CultureInfo.InvariantCulture), File.ReadAllText(Path.Combine(held.Cgroup!.Dir, "pids.max")).Trim());
        Assert.Equal(AttemptCgroups.MemoryMax.ToString(CultureInfo.InvariantCulture), File.ReadAllText(Path.Combine(held.Cgroup.Dir, "memory.max")).Trim());
        var ran = await Finish(held);
        var threads = int.Parse(ran.Out.Split('\n').First(l => l.StartsWith("threads=", StringComparison.Ordinal))[8..], CultureInfo.InvariantCulture);
        Assert.True(threads < AttemptCgroups.PidsMax, $"an attempt started {threads} threads under a ceiling of {AttemptCgroups.PidsMax}: {ran.Out} {ran.Err}");
        Says(ran, "fork-refused");
        Assert.Contains($"pids.max {AttemptCgroups.PidsMax}", held.Mechanism);
    }

    // ---- refusal ------------------------------------------------------------------------------------------------

    /// <summary>
    /// (f) NO CELL WITHOUT A DELEGATED CGROUP. On a platform that would hold a cell, a launch whose root is not a delegated cgroup — none
    /// looked for in this process, or a directory that is not one — is refused with <see cref="ErrorCode.CELL_UNAVAILABLE"/> naming
    /// <c>Delegate=yes</c>, and nothing runs. Every OS: the platform is a stand-in. On Linux a launch that is not a cell starts and keeps
    /// its session, its Mechanism line saying "session only — no delegated cgroup".
    /// </summary>
    [Fact]
    public async Task A_cell_launch_without_a_delegated_cgroup_is_refused_naming_delegate_yes()
    {
        var platform = new CellPlatform(true, "6.17.0-1022-azure", "x64", 7, null, "lockdown,capability,landlock,yama,apparmor", "1");
        Assert.Null(platform.Refusal());
        var dir = Scratch("nodelegate");
        var marker = Path.Combine(dir, "ran");
        var policy = new CellPolicy(dir, [], CellPolicy.SystemRoots, [new("PATH", "/usr/bin:/bin")], null);
        var before = Directory.Exists(CellPolicy.Directory) ? Directory.GetFiles(CellPolicy.Directory).Length : 0;

        foreach (var root in new[] { AttemptCgroups.Current, AttemptCgroups.Open(Path.Combine(dir, "not-a-cgroup")) })
        {
            Assert.False(root.Held);
            var refused = Assert.Throws<TradeAgentException>(() =>
                ProcessContainment.Start(Plain("/bin/sh", "-c", $"echo ran > {marker}"), policy, Launcher(), platform, root));
            Assert.Equal(ErrorCode.CELL_UNAVAILABLE, refused.Code);
            Assert.Contains("Delegate=yes", refused.Message);
        }
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(File.Exists(marker), "a cell launch refused for want of a delegated cgroup ran anyway");
        Assert.Equal(before, Directory.Exists(CellPolicy.Directory) ? Directory.GetFiles(CellPolicy.Directory).Length : 0);

        if (!OperatingSystem.IsLinux()) return;
        using var session = ProcessContainment.Start(Plain("/bin/sh", "-c", "exit 0"), Launcher(), requireHeld: true,
            cgroups: AttemptCgroups.Open(Path.Combine(dir, "not-a-cgroup")));
        Assert.Null(session.Cgroup);
        Assert.NotNull(session.Container);
        Assert.Contains("session only — no delegated cgroup", session.Mechanism);
        await Finish(session);
    }

    // ---- the grant across cgroups -------------------------------------------------------------------------------

    /// <summary>
    /// (h) A GRANT FROM ANOTHER ATTEMPT'S CGROUP IS REFUSED, AND (GUARD) ONE FROM ITS OWN IS SERVED. Attempt A's own process — a
    /// member of A's session, a child of A's launcher — presents A's grant from A's cgroup and is served; the same process that first
    /// writes itself into attempt B's <c>cgroup.procs</c> is still in A's session, and is refused: the session proof AND the cgroup
    /// (RULING 1). A prefix match on the root would serve it too, since B is beside A under the same root.
    /// </summary>
    [LinuxFact]
    public async Task A_grant_from_a_process_in_its_session_but_moved_to_another_attempts_cgroup_is_refused()
    {
        PlatformIsACells();
        using var rows = Rows();
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;
        var pipe = "ta-cg-" + Guid.NewGuid().ToString("n")[..12];
        var grants = new AgentGrants();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { Grants = grants };
        server.Start();

        using var b = ProcessContainment.Start(Plain("/bin/sleep", "60"), Launcher(), requireHeld: true, attemptId: "b",
            cgroups: rows.Cgroups);
        Assert.NotNull(b.Cgroup);
        var cli = Launcher();

        async Task<(int Code, string Out, string Err)> PresentFromA(string grantName, string before)
        {
            using var grant = grants.Issue(CouncilRoles.Research, grantName);
            var psi = Plain("/bin/sh", "-c", before + "exec \"$0\" \"$@\"", cli[0], cli[1], "data", "list", "--json");
            psi.Environment["TRADEAGENT_PIPE"] = pipe;
            psi.Environment[AgentGrants.Variable] = grant.Token;
            using var a = ProcessContainment.Start(psi, Launcher(), requireHeld: true, attemptId: grantName, cgroups: rows.Cgroups);
            Assert.NotNull(a.Container);
            Assert.True(grants.Bind(grant.Token, a.Container!));
            var ran = await Finish(a);
            return (ran.Code, ran.Out, ran.Err);
        }

        var own = await PresentFromA("attempt-a-own", "");
        Assert.True(own.Code == 0, $"attempt A's own process, in A's cgroup, was not served (exit {own.Code}): {own.Out} {own.Err}");
        using (var doc = JsonDocument.Parse(own.Out)) Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), own.Out);

        var moved = await PresentFromA("attempt-a-moved", $"echo $$ > {b.Cgroup!.Dir}/cgroup.procs || exit 9; ");
        Assert.True(moved.Code != 0 && moved.Code != 9,
            $"a process of attempt A that moved itself into attempt B's cgroup presented A's grant and was served (exit {moved.Code}): {moved.Out}");
        Assert.Contains("IPC_UNAUTHENTICATED", moved.Out + moved.Err);
        Assert.Contains("cgroup", moved.Out + moved.Err);
    }

    // ---- fix pass 1 (the review's R1-R4) ------------------------------------------------------------------------

    /// <summary>A child that leaves its session AND moves itself into a sibling attempt's cgroup, while its parent lives on.</summary>
    const string SiblingPy = """
        import os, sys, time
        out, sibling = sys.argv[1], sys.argv[2]
        if os.fork() == 0:
            os.setsid()
            open(sibling + "/cgroup.procs", "w").write(str(os.getpid()))
            line = open("/proc/self/cgroup").read().strip()
            open(out + ".tmp", "w").write(f"{os.getpid()} {line}"); os.rename(out + ".tmp", out)
            time.sleep(300)
            os._exit(0)
        time.sleep(300)
        """;

    /// <summary>
    /// (i) R1: THE TREE IS TAKEN BEFORE THE CGROUP IS KILLED. A child that left the attempt's session with <c>setsid</c> and moved itself
    /// into a sibling attempt's cgroup is the attempt's only by its parent link — and <c>cgroup.kill</c> takes that parent first. Its
    /// parent lives until the cancel, so the snapshot taken before the kill holds it, and the cancel ends it.
    /// </summary>
    [LinuxFact]
    public async Task Cancelling_an_attempt_ends_a_child_that_left_its_session_and_moved_into_a_sibling_attempts_cgroup()
    {
        PlatformIsACells();
        using var rows = Rows();
        var sibling = rows.Cgroups.Create("sibling");
        var said = Path.Combine(Scratch("sibling"), "child");
        using var held = ProcessContainment.Start(Plain("/usr/bin/python3", "-I", "-c", SiblingPy, said, sibling.Dir), Launcher(),
            requireHeld: true, attemptId: "parent", cgroups: rows.Cgroups);
        held.Process.StandardInput.Close();
        var told = (await Await(said, TimeSpan.FromSeconds(30))).Split(' ', 2);
        var child = int.Parse(told[0], CultureInfo.InvariantCulture);
        var entry = ProcessTable.Read(child);
        Assert.True(entry is { Zombie: false }, $"the child {child} was not running");
        try
        {
            Assert.Equal(LineOf(sibling.Dir), told[1]);                       // it moved to the sibling attempt
            Assert.NotEqual(held.Process.Id, entry!.Value.Session);            // and left the attempt's session
            Assert.True(ProcessTable.Read(entry.Value.Parent) is { Zombie: false }, "the child's parent was not alive at the cancel");

            held.Kill();

            Assert.False(Alive(child, entry.Value.Start),
                $"the child {child}, which left the attempt's session and moved into {sibling.Dir} while its parent lived, outlived the cancel " +
                $"({held.Mechanism}; ended: {held.Ended?.Ended})");
        }
        finally
        {
            if (Alive(child, entry!.Value.Start)) Posix.Kill(child);
        }
    }
}

using System.Globalization;
using System.Runtime.InteropServices;

namespace TradeAgent.Core;

/// <summary>
/// THE UNIX HALF OF "THE AGENT PROCESS DIES WITH THE APP", and it exists because .NET cannot ask for
/// it at the point where it would belong.
///
/// On Windows a Job Object holds every process a turn starts, whatever it does to its parent links,
/// and closing the job's handle kills the lot. The POSIX equivalent is a process GROUP: a grandchild
/// that detaches — its parent exits, the kernel reparents it to init — keeps the group it was born
/// in, so a group kill still reaches it while <c>Kill(entireProcessTree: true)</c>, which walks
/// parent links, cannot see it at all.
///
/// Measured on this Mac before any of this was written: <c>Process.Start</c> leaves the child in
/// TradeAgent's OWN process group (self pgid=39402, child pgid=39402), and the group is therefore
/// unkillable — the app is in it. <c>setpgid</c> from the parent after the child has exec'd fails, as
/// POSIX says it must. There is no <c>ProcessStartInfo</c> flag for a new session.
///
/// So the child is started through a launcher that IS ours: TradeAgent's own <c>trade</c>, invoked
/// with <see cref="Flag"/>, which calls <c>setsid()</c> and then <c>execv()</c>s the real command.
/// After exec the process keeps its pid, so the pid the app already holds IS the session and group
/// leader, and <c>kill(-pid)</c> is the whole teardown. Measured the same day: child pgid = sid =
/// its own pid, a detached grandchild at ppid 1 still carried that group, and the group kill took it.
///
/// It is not a sandbox and this file does not pretend otherwise: a process that wants out can call
/// <c>setsid</c> itself, in one line of any scripting language on the machine. That is why the
/// Doctor says "OS sandbox: NONE" in the owner's own words and why the live configuration refuses to
/// start an uncontained runtime at all. What this buys is that a turn TradeAgent cancelled is over —
/// including the parts of it that stopped answering to their parent.
/// </summary>
public static class ContainedLaunch
{
    /// <summary>
    /// The launcher's flag. Deliberately a flag on <c>trade</c> rather than a verb: verbs map to
    /// gateway operations, and this one never opens the pipe, reads the token or touches the
    /// gateway. It grants nothing either — it execs a program its caller could already exec, as the
    /// same user, in the same session it would have inherited anyway.
    /// </summary>
    public const string Flag = "--spawn-contained";

    /// <summary>
    /// The launcher itself, run inside whatever process was started with <see cref="Flag"/>.
    ///
    /// Returns false when this is an ordinary invocation, so the caller goes on to be the CLI. When
    /// it returns true the process is either already gone (exec succeeded, and this code no longer
    /// exists) or is about to exit with <paramref name="exitCode"/>.
    /// </summary>
    public static bool TryRun(IReadOnlyList<string> argv, TextWriter error, out int exitCode)
    {
        exitCode = 0;
        if (argv.Count == 0 || argv[0] != Flag) return false;

        if (argv.Count < 2)
        {
            error.WriteLine($"trade: {Flag} needs a program to run");
            exitCode = 2;
            return true;
        }

        if (OperatingSystem.IsWindows())
        {
            // Not a fallback: on Windows the app assigns the child to a Job Object, which is
            // stronger than a session and cannot be broken out of. A build that got here would be
            // running the agent WITHOUT the job while believing it was contained, and starting the
            // program anyway is the failure mode this refusal exists to prevent.
            error.WriteLine($"trade: {Flag} is a macOS/Linux launcher; on Windows the app uses a Job Object");
            exitCode = 2;
            return true;
        }

        if (Posix.setsid() < 0)
        {
            error.WriteLine($"trade: could not start a new session (errno {Marshal.GetLastPInvokeError()})");
            exitCode = 126;
            return true;
        }

        // From here this process is its own session and group leader, and so is everything it goes
        // on to start. execv keeps the pid, so the handle the app already holds names the group.
        var command = argv.Skip(1).ToArray();
        Posix.Exec(command[0], command);

        error.WriteLine($"trade: could not run {command[0]} (errno {Marshal.GetLastPInvokeError()})");
        exitCode = 127;
        return true;
    }
}

/// <summary>The calls the launcher and the teardown need. Nothing here runs on Windows.</summary>
public static class Posix
{
    [DllImport("libc", SetLastError = true)]
    internal static extern int setsid();

    [DllImport("libc", SetLastError = true)]
    internal static extern int getsid(int pid);

    [DllImport("libc", SetLastError = true)]
    internal static extern int getpgid(int pid);

    [DllImport("libc", SetLastError = true)]
    internal static extern int getppid();

    [DllImport("libc", SetLastError = true)]
    internal static extern int kill(int pid, int signal);

    [DllImport("libc", SetLastError = true, EntryPoint = "execv")]
    static extern int execv(IntPtr path, IntPtr argv);

    const int SIGKILL = 9;

    /// <summary>The two signals whose numbers differ between the kernels this ships to: BSD's on macOS, Linux's elsewhere.</summary>
    static int SIGSTOP => OperatingSystem.IsMacOS() ? 17 : 19;

    static int SIGCONT => OperatingSystem.IsMacOS() ? 19 : 18;

    /// <summary>The session id of a process, or -1 when it cannot be read (including on Windows).</summary>
    public static int SessionOf(int pid)
    {
        if (OperatingSystem.IsWindows()) return -1;
        try { return getsid(pid); }
        catch (Exception) { return -1; }
    }

    /// <summary>The process group of a process, or -1 when it cannot be read (including on Windows).</summary>
    public static int GroupOf(int pid)
    {
        if (OperatingSystem.IsWindows()) return -1;
        try { return getpgid(pid); }
        catch (Exception) { return -1; }
    }

    /// <summary>This process's parent, or -1 on Windows. The launcher reads it to know when the app is gone.</summary>
    public static int Parent()
    {
        if (OperatingSystem.IsWindows()) return -1;
        try { return getppid(); }
        catch (Exception) { return -1; }
    }

    /// <summary>Whether a process is still there. False on Windows, where this is not the question asked.</summary>
    public static bool Alive(int pid)
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return kill(pid, 0) == 0; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// SIGKILL to a whole process group, named by its leader's pid. The negative pid IS the group:
    /// <c>kill(-pid)</c> reaches every process in it, including the ones that have been reparented
    /// away from anything the app could walk to.
    ///
    /// Refuses to kill the group TradeAgent is itself in, which would take the app down with the
    /// turn. That is not a defensive nicety: before the launcher existed the child WAS in this
    /// group, so a build that lost the launcher and kept the group kill would shut itself down on
    /// the first Stop.
    /// </summary>
    public static bool KillGroup(int group)
    {
        if (OperatingSystem.IsWindows() || group <= 1) return false;
        try
        {
            if (getpgid(Environment.ProcessId) == group) return false;
            return kill(-group, SIGKILL) == 0;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Freezes one process. It cannot be caught or ignored, and a frozen process forks nothing and exits nothing.</summary>
    public static bool Stop(int pid) => Signal(pid, SIGSTOP);

    /// <summary>Lets a frozen process go on — for one that turned out not to be the turn's.</summary>
    public static bool Continue(int pid) => Signal(pid, SIGCONT);

    /// <summary>SIGKILL to one process, never a group.</summary>
    public static bool Kill(int pid) => Signal(pid, SIGKILL);

    /// <summary>
    /// One signal to ONE process. Never to a group — a negative or zero pid is refused here, as is init —
    /// and never to this process itself, whatever the caller computed.
    /// </summary>
    static bool Signal(int pid, int signal)
    {
        if (OperatingSystem.IsWindows() || pid <= 1 || pid == Environment.ProcessId) return false;
        try { return kill(pid, signal) == 0; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Becomes <paramref name="path"/>. Returns only on failure — on success this process no longer
    /// exists to return into. The argument array is NUL-terminated by hand because that is the shape
    /// <c>execv</c> reads, and UTF-8 because that is what a POSIX path is.
    /// </summary>
    internal static void Exec(string path, IReadOnlyList<string> argv)
    {
        var strings = new IntPtr[argv.Count + 1];
        for (var i = 0; i < argv.Count; i++) strings[i] = Marshal.StringToCoTaskMemUTF8(argv[i]);
        strings[^1] = IntPtr.Zero;

        var block = Marshal.AllocHGlobal(IntPtr.Size * strings.Length);
        Marshal.Copy(strings, 0, block, strings.Length);
        var file = Marshal.StringToCoTaskMemUTF8(path);
        execv(file, block);
    }
}

/// <summary>
/// ONE PROCESS AS THE KERNEL DESCRIBES IT: its parent, its group, its session, when it started, and
/// whether it is frozen or already dead. <see cref="Start"/> is the kernel's own number for the moment
/// it began — microseconds since the epoch on macOS, clock ticks since boot on Linux — compared only with
/// another reading from the same machine, and with a pid it is the process's identity: a pid the system
/// has handed to somebody else comes back with a different start.
/// </summary>
public readonly record struct ProcessEntry(int Pid, int Parent, int Group, int Session, ulong Start,
    bool Stopped, bool Zombie);

/// <summary>
/// THE PROCESS TABLE, READ WITHOUT SHELLING OUT. macOS through libproc (<c>proc_listallpids</c>,
/// <c>proc_pidinfo</c>) and <c>getsid</c>; Linux through <c>/proc</c>. A process this user may not read
/// is not in it — it could not be killed by this user either. Nothing here runs on Windows, where a job
/// object holds a turn and the table is never asked.
/// </summary>
public static class ProcessTable
{
    [DllImport("libc", SetLastError = true)]
    static extern int proc_listallpids(int[]? buffer, int buffersize);

    [DllImport("libc", SetLastError = true)]
    static extern int proc_pidinfo(int pid, int flavor, ulong arg, byte[] buffer, int buffersize);

    const int PROC_PIDTBSDINFO = 3;

    /// <summary><c>sizeof(struct proc_bsdinfo)</c>: twelve words, two names, six words, then the start as two 64-bit fields.</summary>
    const int BsdInfoSize = 136;

    const uint SSTOP = 4, SZOMB = 5;

    /// <summary>One process, or null when there is none at that pid (or this user may not read it).</summary>
    public static ProcessEntry? Read(int pid)
    {
        if (pid <= 0 || OperatingSystem.IsWindows()) return null;
        try { return OperatingSystem.IsMacOS() ? ReadMac(pid) : ReadProc(pid); }
        catch (Exception) { return null; }
    }

    /// <summary>Every process this user can read, as of now.</summary>
    public static IReadOnlyList<ProcessEntry> Snapshot()
    {
        if (OperatingSystem.IsWindows()) return [];
        var all = new List<ProcessEntry>();
        foreach (var pid in Pids())
            if (Read(pid) is { } e) all.Add(e);
        return all;
    }

    static IEnumerable<int> Pids()
    {
        if (OperatingSystem.IsMacOS())
        {
            var needed = proc_listallpids(null, 0);
            if (needed <= 0) return [];
            var buffer = new int[needed + 512];
            var got = proc_listallpids(buffer, buffer.Length * sizeof(int));
            return got <= 0 ? [] : buffer.Take(Math.Min(got, buffer.Length)).Where(p => p > 0).ToArray();
        }

        var pids = new List<int>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
            if (int.TryParse(Path.GetFileName(dir), out var pid) && pid > 0) pids.Add(pid);
        return pids;
    }

    static ProcessEntry? ReadMac(int pid)
    {
        var info = new byte[BsdInfoSize];
        if (proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, info, info.Length) != BsdInfoSize) return null;
        var status = BitConverter.ToUInt32(info, 4);
        if ((int)BitConverter.ToUInt32(info, 12) != pid) return null;
        var parent = (int)BitConverter.ToUInt32(info, 16);
        var group = (int)BitConverter.ToUInt32(info, 100);
        var seconds = BitConverter.ToUInt64(info, 120);
        var micros = BitConverter.ToUInt64(info, 128);
        var session = Posix.SessionOf(pid);
        return new ProcessEntry(pid, parent, group, session, seconds * 1_000_000UL + micros,
            status == SSTOP, status == SZOMB);
    }

    /// <summary>
    /// <c>/proc/&lt;pid&gt;/stat</c>: the name is in parentheses and may itself hold spaces and parentheses, so
    /// the fields are counted from the LAST closing parenthesis — state, parent, group, session, and the
    /// start time twenty-two fields in.
    /// </summary>
    static ProcessEntry? ReadProc(int pid)
    {
        string stat;
        try { stat = File.ReadAllText($"/proc/{pid}/stat"); }
        catch (Exception) { return null; }
        var close = stat.LastIndexOf(')');
        if (close < 0 || close + 2 >= stat.Length) return null;
        var f = stat[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length < 20) return null;
        var state = f[0].Length > 0 ? f[0][0] : '?';
        return new ProcessEntry(pid, int.Parse(f[1], CultureInfo.InvariantCulture), int.Parse(f[2], CultureInfo.InvariantCulture),
            int.Parse(f[3], CultureInfo.InvariantCulture), ulong.Parse(f[19], CultureInfo.InvariantCulture),
            state is 'T' or 't', state is 'Z' or 'X' or 'x');
    }
}

/// <summary>What a teardown did: whether every process it proved the turn's is gone, and the ones that are not.</summary>
public sealed record TreeEnd(bool Ended, IReadOnlyList<ProcessEntry> Survivors, int Killed)
{
    /// <summary>Nothing of the turn was found running, or there was nothing to end.</summary>
    public static readonly TreeEnd Nothing = new(true, [], 0);

    /// <summary>The owner's sentence when it could not, or null when it did.</summary>
    public string? Sentence => Ended ? null : Labels.LastTurnStillRunning(Survivors.Select(s => s.Pid));
}

/// <summary>
/// ENDS ONE TURN'S WHOLE PROCESS TREE ON macOS AND LINUX, AND KILLS NOTHING IT HAS NOT PROVED IS THE TURN'S
/// (<c>U-agent-tree</c>).
///
/// <para><b>Why not the group kill alone.</b> Measured on this Mac (survey s-agent-tree): a child that made
/// itself its own group, or its own session, outlived the group kill, Pause, Stop and the app's exit — the
/// group kill took its parent first, which orphaned it before anything could follow its parent link. codex
/// itself imports <c>setpgid</c>, <c>setsid</c> and <c>posix_spawnattr_setpgroup</c>.</para>
///
/// <para><b>The order is the property.</b> FREEZE first, collect, then kill: every process is SIGSTOPped as it
/// is found, from the leader down its parent links and across the turn's session, so nothing found can
/// fork, exit or be reparented while the rest is being collected. Only then is the frozen set killed, and
/// the whole pass is repeated until a pass finds nothing — bounded, and said in words when it is not.</para>
///
/// <para><b>The proof.</b> A pid is the turn's when it is the leader (its pid and the start time read when it
/// was started), when its parent is a process already proved the turn's, or when it sits in the turn's own
/// session — and that is re-read AFTER it is frozen, with the start time it had when it was found: a pid the
/// system handed to another process in between comes back with a different start, is let go (SIGCONT, unless
/// it was already frozen by somebody else) and is left. The session is swept only when it is not this
/// process's own session or group (no-launcher turns share the app's, and are reached by parent links
/// alone), and only while the session's id still names the turn's leader — the kernel does not hand out a
/// pid that is still a live session's id, so a session with members is the one the launcher made.</para>
/// </summary>
public static class TreeTeardown
{
    /// <summary>How long one teardown keeps trying before it reports what it could not end.</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Ends the tree. <paramref name="leader"/> is the turn's own process and <paramref name="leaderStart"/>
    /// its start as read when it was started (0 when it could not be read, and then the leader proves
    /// nothing). <paramref name="session"/> is the turn's session, or 0 when it has none of its own.
    /// <paramref name="insideTheSession"/> is the launcher sweeping its own session: then the session is its
    /// own by design and only this process itself is spared.
    /// </summary>
    /// <param name="kill">The SIGKILL, for a test that needs a process the kill does not reach; the real one otherwise.</param>
    public static TreeEnd End(int leader, ulong leaderStart, int session, TimeSpan? bound = null,
        bool insideTheSession = false, Func<int, bool>? kill = null)
    {
        if (OperatingSystem.IsWindows()) return TreeEnd.Nothing;

        var signal = kill ?? Posix.Kill;
        var deadline = DateTime.UtcNow + (bound ?? Bound);
        var killed = 0;
        while (true)
        {
            var frozen = Freeze(leader, leaderStart, session, insideTheSession);
            foreach (var p in frozen.Values)
                if (signal(p.Pid)) killed++;

            var settle = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            AwaitGone(frozen.Values, settle < deadline ? settle : deadline);

            // DONE WHEN NOTHING PROVED THE TURN'S RUNS — asked of the table itself, not of what was frozen, so
            // a process this user could not freeze is still counted rather than quietly skipped.
            var left = Members(leader, leaderStart, session, insideTheSession);
            if (left.Count == 0) return new TreeEnd(true, [], killed);
            if (DateTime.UtcNow >= deadline) return new TreeEnd(false, left, killed);
            if (frozen.Count == 0) Thread.Sleep(50);
        }
    }

    /// <summary>Whether a process a teardown could not end is still the same process, and still running.</summary>
    public static bool StillRunning(ProcessEntry survivor) =>
        ProcessTable.Read(survivor.Pid) is { Zombie: false } now && now.Start == survivor.Start;

    /// <summary>
    /// Every process proved the turn's, frozen. Passes are repeated until one adds nothing: a child forked
    /// between a pass's snapshot and its parent's freeze is in the next snapshot, and by then its parent is
    /// frozen and can fork no more.
    /// </summary>
    static Dictionary<int, ProcessEntry> Freeze(int leader, ulong leaderStart, int session, bool insideTheSession)
    {
        var self = Environment.ProcessId;
        var frozen = new Dictionary<int, ProcessEntry>();
        var sweep = SessionIsTheTurns(leader, leaderStart, session, insideTheSession, self);

        for (var pass = 0; pass < 20; pass++)
        {
            var table = ProcessTable.Snapshot().ToDictionary(e => e.Pid);
            var children = table.Values.GroupBy(e => e.Parent).ToDictionary(g => g.Key, g => g.Select(e => e.Pid).ToList());
            var queue = new Queue<int>();

            if (leader > 1 && leader != self && leaderStart != 0 && table.TryGetValue(leader, out var l) && !l.Zombie && l.Start == leaderStart)
                queue.Enqueue(leader);
            if (leader == self && children.TryGetValue(self, out var mine))
                foreach (var k in mine) queue.Enqueue(k);
            if (sweep)
                foreach (var e in table.Values)
                    if (e.Session == session && !e.Zombie && e.Start >= leaderStart) queue.Enqueue(e.Pid);
            foreach (var pid in frozen.Keys)
                if (children.TryGetValue(pid, out var kids)) foreach (var k in kids) queue.Enqueue(k);

            var added = 0;
            while (queue.Count > 0)
            {
                var pid = queue.Dequeue();
                if (pid <= 1 || pid == self || frozen.ContainsKey(pid)) continue;
                if (!table.TryGetValue(pid, out var seen) || seen.Zombie) continue;

                if (!Posix.Stop(pid)) continue;   // gone, or not this user's to stop

                // RE-PROVED NOW THAT IT CANNOT CHANGE: the same process (pid and start), and the turn's.
                var now = ProcessTable.Read(pid);
                var proved = now is { Zombie: false } n && n.Start == seen.Start && (
                    (pid == leader && n.Start == leaderStart) ||
                    (sweep && n.Session == session && n.Start >= leaderStart) ||
                    frozen.ContainsKey(n.Parent) || (leader == self && n.Parent == self));
                if (!proved)
                {
                    if (!seen.Stopped) Posix.Continue(pid);
                    continue;
                }

                frozen[pid] = now!.Value;
                added++;
                if (children.TryGetValue(pid, out var kids)) foreach (var k in kids) queue.Enqueue(k);
            }

            if (added == 0) break;
        }
        return frozen;
    }

    /// <summary>
    /// Every process proved the turn's by the table alone, without a signal: the leader, everything that
    /// descends from it by parent links, and the session's members. What a teardown reports as still running.
    /// </summary>
    static List<ProcessEntry> Members(int leader, ulong leaderStart, int session, bool insideTheSession)
    {
        var self = Environment.ProcessId;
        var sweep = SessionIsTheTurns(leader, leaderStart, session, insideTheSession, self);
        var table = ProcessTable.Snapshot().ToDictionary(e => e.Pid);
        var children = table.Values.GroupBy(e => e.Parent).ToDictionary(g => g.Key, g => g.Select(e => e.Pid).ToList());
        var found = new Dictionary<int, ProcessEntry>();
        var queue = new Queue<int>();

        if (leader > 1 && leader != self && leaderStart != 0 && table.TryGetValue(leader, out var l) && l.Start == leaderStart)
            queue.Enqueue(leader);
        if (leader == self && children.TryGetValue(self, out var mine))
            foreach (var k in mine) queue.Enqueue(k);
        if (sweep)
            foreach (var e in table.Values)
                if (e.Session == session && e.Start >= leaderStart) queue.Enqueue(e.Pid);

        while (queue.Count > 0)
        {
            var pid = queue.Dequeue();
            if (pid <= 1 || pid == self || found.ContainsKey(pid)) continue;
            if (!table.TryGetValue(pid, out var e) || e.Zombie) continue;
            found[pid] = e;
            if (children.TryGetValue(pid, out var kids)) foreach (var k in kids) queue.Enqueue(k);
        }
        return [.. found.Values];
    }

    /// <summary>
    /// Whether the session may be swept at all. Never this process's own session or group when this process
    /// is the app — that is the app, its terminal and everything else the owner runs beside it — and never a
    /// session whose id has been handed on: a live process at that pid with another start is not the leader.
    /// </summary>
    static bool SessionIsTheTurns(int leader, ulong leaderStart, int session, bool insideTheSession, int self)
    {
        if (session <= 1 || leaderStart == 0) return false;
        if (insideTheSession) return session == self && Posix.SessionOf(self) == self;
        if (session == Posix.SessionOf(self) || session == Posix.GroupOf(self)) return false;
        return session != leader || ProcessTable.Read(leader) is not { Zombie: false } owner || owner.Start == leaderStart;
    }

    static void AwaitGone(IEnumerable<ProcessEntry> killed, DateTime until)
    {
        var waiting = killed.ToList();
        while (true)
        {
            waiting.RemoveAll(e => !StillRunning(e));
            if (waiting.Count == 0 || DateTime.UtcNow >= until) return;
            Thread.Sleep(10);
        }
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TradeAgent.Core;
using TradeAgent.Security;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// WHAT THIS INSTALLATION'S CONTAINMENT ACTUALLY IS, in one place, so the gateway, the Doctor and
/// the live gate are all reading the same answer rather than three descriptions of it.
/// </summary>
public static class Containment
{
    /// <summary>
    /// The rule the agent pipe applies to a peer that presents a launch grant: TradeAgent's own
    /// trade command, at the path this app deployed it to, hashing to what this app deployed, and
    /// never anything under the managed AI-runtime folder or inside the agent's own workspace.
    ///
    /// Read now rather than cached: <c>ToolDeployer</c> writes the hash at start, and a gateway
    /// rebuilt when the owner changes trading platform must see what the current install deployed.
    /// </summary>
    public static PeerRule PeerRuleNow() => new(
        Path.Combine(Paths.Bin, ToolDeployer.TradeCliName),
        ToolDeployer.DeployedHash(),
        Paths.Tools,
        Paths.Workspace);

    /// <summary>
    /// WHETHER AN OPERATING-SYSTEM SANDBOX CONFINES THE AI'S OWN PROGRAM, and today the answer is no
    /// on every platform this ships to.
    ///
    /// It is a method rather than a constant because it is a question about the MACHINE, and the
    /// answer will change when `U-contain-2` lands an AppContainer on Windows or a sandboxed helper on
    /// macOS. Until then it returns NONE, and everything that reads it — the Doctor's row, the guide,
    /// the live gate — says so in those words rather than in a silence that reads as "fine".
    ///
    /// What a job object and a whitelisted environment are NOT: they stop a turn outliving its cancel
    /// and stop the app's own environment leaking into the AI. They do not stop the agent process
    /// reading or writing any file its user account can reach, which includes TradeAgent's own
    /// binaries and its `state/` folder.
    /// </summary>
    public static SandboxState Sandbox() => new(false, "NONE",
        "no operating-system sandbox confines the AI assistant's program on this computer: it runs as " +
        "the same Windows user as TradeAgent and can read and write whatever that user can");

    /// <summary>
    /// The sentence that refuses to start the vendor's CLI, or null when it may start.
    ///
    /// The condition is BOTH halves of the owner's real-money decision, and that is deliberate:
    /// <c>ModeIsLive</c> alone is a mode the owner has chosen and not yet armed — real money is off,
    /// no order can reach a broker — and refusing to run the AI there would take the app away from
    /// someone who is setting it up. It is the ARMED live configuration that this refuses, because
    /// that is the configuration in which an uncontained agent process sits beside a switch that
    /// spends money.
    ///
    /// Paper and observe are untouched, and the sentence says so: a refusal that does not tell the
    /// owner what still works reads as a broken product.
    /// </summary>
    public static string? RefusalToLaunch(bool modeIsLive, bool liveActivated, SandboxState? sandbox = null)
    {
        if (!modeIsLive || !liveActivated) return null;

        var state = sandbox ?? Sandbox();
        if (state.Ok) return null;

        return "Real-money trading is switched on, and " + state.Detail + ". TradeAgent will not start " +
               "the AI assistant in that configuration. Switch real-money trading off, or choose " +
               "Practice or Watch only, and the AI runs exactly as before.";
    }

    /// <summary>
    /// The whole containment story in the words the Doctor prints. <paramref name="mechanism"/> is what
    /// held the last agent process, or null when none has run in this session.
    /// </summary>
    public static ContainmentFacts Facts(string? mechanism = null)
    {
        var sandbox = Sandbox();
        var rule = PeerRuleNow();
        var held = OperatingSystem.IsWindows()
            ? "a Windows job object that dies with TradeAgent; breakaway is refused"
            : ProcessContainment.DefaultLauncher() is not null
                ? "a session of its own, so cancelling a turn kills its whole process group"
                : "nothing: TradeAgent's own trade command is not installed, so no session can be started";

        // The same sentence on every platform: the kernel names the program on the other end on
        // Windows, macOS and Linux alike (PeerImage.ClientPath), and a caller it will not name is
        // refused.
        var peer = rule.ExpectedHash is { Length: 64 }
            ? "the program on the other end of the AI's pipe is checked against the trade command this app installed"
            : "not checked: TradeAgent has not recorded which trade command it installed";

        return new ContainmentFacts(
            Held: OperatingSystem.IsWindows() || ProcessContainment.DefaultLauncher() is not null,
            Mechanism: mechanism ?? held,
            SandboxOk: sandbox.Ok,
            Sandbox: $"OS sandbox: {sandbox.Name}")
        {
            Environment = "the AI is given a named list of environment variables, not a copy of TradeAgent's own",
            Grant = "every launch carries a grant naming its role and attempt, served only to a program inside the " +
                    "container TradeAgent made for that launch; only the Operations Director's may trade, and not while " +
                    "another launch is running beside it",
            Peer = peer,
            SandboxDetail = sandbox.Detail
        };
    }
}

/// <summary>What the operating system is doing to confine the AI's program, and what it is called.</summary>
public sealed record SandboxState(bool Ok, string Name, string Detail);

/// <summary>
/// WHAT HOLDS AN AGENT PROCESS, reported in the words the Doctor card prints.
///
/// <paramref name="Held"/> is the only load-bearing field: false means the turn ran with nothing but
/// parent links holding it, which <c>U-containment</c>'s whole point is that a detached grandchild
/// escapes. <paramref name="SandboxOk"/> is separate and is false on every platform this build runs
/// on — a job is not a sandbox, and saying so is the difference between this unit and the one that
/// has not been built.
/// </summary>
public sealed record ContainmentFacts(bool Held, string Mechanism, bool SandboxOk, string Sandbox)
{
    /// <summary>What the child's environment is.</summary>
    public string Environment { get; init; } = "";

    /// <summary>What the pipe knows about who is calling.</summary>
    public string Grant { get; init; } = "";

    /// <summary>Whether the program on the other end of the pipe is checked, and against what.</summary>
    public string Peer { get; init; } = "";

    /// <summary>What the absence of a sandbox actually means, in the owner's words.</summary>
    public string SandboxDetail { get; init; } = "";

    /// <summary>The four sentences and the sandbox line, in the order the card reads them.</summary>
    public string Detail => string.Join("; ", new[] { Mechanism, Environment, Grant, Peer, Sandbox }
        .Where(s => s.Length > 0));
}

/// <summary>
/// The cell a process was started in: its policy's SHA-256, the platform facts that confined it (the Mechanism line's source), and
/// the launch's own policy file, which the launcher removes once the cell has exited.
/// </summary>
public sealed record CellLaunch(string PolicyHash, CellPlatform Platform, string PolicyFile);

/// <summary>
/// One agent process and the thing that holds it: a Job Object on Windows, a session of its own on
/// macOS and Linux. Disposing it ends the turn's whole tree, including the parts that detached.
/// </summary>
public sealed class ContainedProcess : IDisposable
{
    readonly IntPtr _job;
    readonly bool _ownSession;
    readonly Lock _teardown = new();
    bool _closed;
    bool _held;
    bool _disposed;

    /// <summary>The turn's leader and the start the kernel gave it, read the moment it was started.</summary>
    readonly int _leader;
    readonly ulong _leaderStart;

    /// <summary>
    /// THE TURN'S SESSION, REMEMBERED, never re-read from a pid that may be gone. The launcher makes the
    /// leader a session leader with <c>setsid</c> and keeps its pid, so the session's id IS the leader's pid
    /// — known here at the start, before the launcher has run, and still known after the leader has exited,
    /// which is exactly when a finished turn's leftovers need it. Zero for a process nothing held.
    /// </summary>
    readonly int _session;

    internal ContainedProcess(Process process, IntPtr job, bool ownSession, bool held, string mechanism, CellLaunch? cell = null,
        AttemptCgroup? cgroup = null, string? grantCgroup = null)
    {
        Process = process;
        _job = job;
        _ownSession = ownSession;
        _held = held;
        Mechanism = mechanism;
        Cell = cell;
        Cgroup = cgroup;
        _leader = process.Id;
        if (OperatingSystem.IsWindows())
        {
            // THE LEADER'S CREATION, for the one teardown no job holds: what is still running of it is asked by pid AND
            // creation, so a pid Windows handed on is not counted (fix pass 1).
            _leaderStart = WindowsProcessTable.StartOf(_leader) ?? 0;
            if (held && job != IntPtr.Zero) Container = new JobContainer(this);
            return;
        }
        _leaderStart = ProcessTable.Read(_leader)?.Start ?? 0;
        _session = ownSession ? _leader : 0;
        // THE SESSION, AND ON LINUX UNDER A DELEGATED ROOT THE ATTEMPT'S CGROUP TOO — ANDed, never one for the other (U-cell-linux-b,
        // RULING 1): a process that left the session is refused by the first, one that moved itself to another cgroup by the second.
        if (ownSession && _leaderStart != 0)
            Container = cgroup is null
                ? new SessionContainer(this)
                : new AllOf(new SessionContainer(this), new CgroupContainer(grantCgroup ?? cgroup.Dir, () => _over));
    }

    /// <summary>
    /// THE CONTAINER A LAUNCH GRANT IS BOUND TO (<c>U-grant-tree</c>), or null when this process is not held and never
    /// will be — and then nothing may present a grant from it, so the launch is refused rather than run (RULING 2).
    /// Windows: the job, once the assign succeeded. macOS and Linux: the launcher's session and the parent links below
    /// it, by <see cref="TreeTeardown"/>'s own proof — which answers Unknown until <see cref="Held"/> latches.
    /// </summary>
    public AttemptContainer? Container { get; }

    /// <summary>Set once the teardown has run: a container whose attempt was ended admits nobody.</summary>
    volatile bool _over;

    public Process Process { get; }

    /// <summary>
    /// Whether anything stronger than a parent link is holding this process.
    ///
    /// ASKED, NOT ASSUMED, AND LATCHED ONCE IT IS TRUE. On Windows the assign has already happened
    /// by the time this object exists, so the answer is fixed at construction. On macOS and Linux it
    /// cannot be: the launcher calls <c>setsid</c> in ITS OWN process, some milliseconds after
    /// <c>Process.Start</c> has already returned here, so a question asked at construction is asked
    /// of a process that has not yet become anything — measured, the first version of this file read
    /// the session too early and reported every contained turn as uncontained. Latched because the
    /// same question asked after the process has exited answers "no" about a turn that was held for
    /// its whole life.
    /// </summary>
    public bool Held
    {
        get
        {
            if (_held) return true;
            if (!_ownSession || _leaderStart == 0) return false;
            if (Posix.SessionOf(_leader) == _session && ProcessTable.Read(_leader)?.Start == _leaderStart) _held = true;
            return _held;
        }
    }

    /// <summary>What is holding it, or why nothing is. One line, for a health row.</summary>
    public string Mechanism { get; }

    /// <summary>The cell it was started in — its policy's hash and the platform that confined it — or null for a launch without one.</summary>
    public CellLaunch? Cell { get; }

    /// <summary>
    /// THE ATTEMPT'S OWN CGROUP (<c>U-cell-linux-b</c>, Linux, where the app holds a delegated root), which the launcher joined before
    /// it started anything — or null: not Linux, or no delegated root (the Mechanism line then says "session only").
    /// </summary>
    public AttemptCgroup? Cgroup { get; }

    /// <summary>What the last teardown found, or null before one has run.</summary>
    public TreeEnd? Ended { get; private set; }

    /// <summary>
    /// Ends the process and everything it started, and returns once it has. See <see cref="End"/>.
    /// </summary>
    public void Kill() => End();

    /// <summary>
    /// THE TEARDOWN, AND IT RETURNS ONLY ONCE IT HAS ENDED THE TREE OR SAID WHICH PROCESSES IT COULD NOT.
    ///
    /// <para>Windows: the job — terminated, waited on until nothing in it runs, then closed — and the
    /// ordinary tree kill where this build could not get a job at all. macOS and Linux:
    /// <see cref="TreeTeardown"/> over the leader, its parent links and the turn's session, freezing
    /// everything it proves the turn's before it kills any of it — leader exited or not, because a turn
    /// whose leader has exited is exactly the one whose leftovers nothing else will ever end.</para>
    ///
    /// <para>One at a time: Stop, Pause and the turn's own end can arrive together, and the second waits
    /// for the first and then looks again.</para>
    /// </summary>
    public TreeEnd End()
    {
        lock (_teardown)
        {
            if (OperatingSystem.IsWindows())
            {
                EndJob();
                if (_job == IntPtr.Zero && !_disposed) return Ended = EndUnjobbed();
                return Ended = TreeEnd.Nothing;
            }

            _over = true;

            // THE TREE IS TAKEN FIRST, THEN THE ATTEMPT'S CGROUP IS KILLED, THEN THE TAKEN TREE IS ENDED (U-cell-linux-b; fix pass 1,
            // R1). The session and parent links are frozen and kept before anything dies: a child that left the session and moved
            // itself into a sibling attempt's cgroup is the turn's by its parent link alone, and cgroup.kill takes that parent. Then
            // cgroup.kill ends every process born in the attempt — a setsid'd daemon that lost its parent included — and the wait is
            // for the kernel's own "populated 0"; then the session teardown ends what was taken and anything still the turn's by
            // session or parent links; then the cgroup is removed. What the kill could not end is named, never assumed gone.
            // A KILL THAT DID NOT EMPTY THE CGROUP (fix pass 1, R4) leaves what it lists still running AND the cgroup itself, held
            // until it is empty — a listing that could not be read is "could not tell", never "none".
            var taken = Cgroup is null ? null : TreeTeardown.Hold(_leader, _leaderStart, _session);
            var inCgroup = Cgroup is { } attempt && !attempt.Kill(TreeTeardown.Bound) ? attempt.Left() : [];
            var tree = TreeTeardown.End(_leader, _leaderStart, _session, held: taken);
            if (Cgroup is { } ended && inCgroup.Count == 0 && !ended.Populated()) ended.Remove();
            Ended = inCgroup.Count == 0
                ? tree
                : new TreeEnd(false, [.. tree.Survivors, .. inCgroup.Where(c => c.Cgroup is not null || tree.Survivors.All(s => s.Pid != c.Pid))],
                    tree.Killed);
            // A CELL'S POLICY FILE GOES WITH ITS TREE (triage #6): the supervisor removes it when the cell exits, but a cancelled
            // turn's supervisor is itself killed here, so the app removes it too — only its own file, never throwing.
            if (Cell is not null) CellStage.Discard(Cell.PolicyFile);
            return Ended;
        }
    }

    /// <summary>
    /// THE ORDINARY TREE KILL, FOR A WINDOWS PROCESS NO JOB HOLDS, AND WHAT IT COULD NOT END (<c>U-grant-tree</c> fix pass 1).
    /// The tree is read before the kill and again after it, and the teardown returns only once nothing of it runs or its
    /// bound has passed — saying, then, which processes still run. A kill that threw used to be read as "already gone" and
    /// the tree reported ended: a launch Windows would not put in its job then ran on, uncontained and spending, while the
    /// next launch was admitted beside it. Unknown is never zero.
    /// </summary>
    [SupportedOSPlatform("windows")]
    TreeEnd EndUnjobbed()
    {
        // A TREE WINDOWS WOULD NOT LIST IS NOT A LEADER-ONLY TREE (fix pass 2): the leader is asked as before, and the tree
        // stays unverified — and is carried out as one Unlisted survivor — until a listing succeeds and shows none of it alive.
        var leader = new ProcessEntry(_leader, 0, 0, 0, _leaderStart, false, false);
        var members = ProcessContainment.ListTree(_leader, _leaderStart)?.ToList() ?? [leader];
        try
        {
            if (ProcessContainment.Faults.Value is { RefuseTreeKill: true })
                throw new System.ComponentModel.Win32Exception(5, "the tree kill was refused (a test's fault)");
            if (!Process.HasExited) Process.Kill(entireProcessTree: true);
        }
        catch (Exception) { /* what it could not end is read below, not assumed gone */ }

        var deadline = DateTime.UtcNow + TreeTeardown.Bound;
        while (true)
        {
            var listed = ProcessContainment.ListTree(_leader, _leaderStart);
            foreach (var late in listed ?? [])
                if (!members.Any(m => m.Pid == late.Pid)) members.Add(late);
            var left = members.Where(TreeTeardown.StillRunning).ToList();
            if (listed is not null && left.Count == 0) return TreeEnd.Nothing;
            if (DateTime.UtcNow >= deadline)
                return new TreeEnd(false, listed is null ? [.. left, leader with { Unlisted = true }] : left, 0);
            Thread.Sleep(20);
        }
    }

    /// <summary>
    /// THE WINDOWS JOB AS A GRANT'S CONTAINER: a peer is inside when <c>IsProcessInJob</c> says so of the process the
    /// pid names NOW — opened, its creation re-read under the handle and compared with the start the pipe read, so a pid
    /// handed on is not the peer — asked under the teardown lock, so a job whose handle has been closed is never asked
    /// (a closed handle's value can name something else). The assign gap is a refusal here, not a hole: a child that
    /// started before the assign landed is outside the job and is refused.
    /// </summary>
    sealed class JobContainer(ContainedProcess owner) : AttemptContainer
    {
        public override string Name => "the Windows job TradeAgent made for this attempt";

        public override MembershipVerdict Contains(PeerFacts peer)
        {
            if (!OperatingSystem.IsWindows()) return MembershipVerdict.Unknown("a Windows job is asked only on Windows");
            lock (owner._teardown)
            {
                if (owner._closed || owner._job == IntPtr.Zero)
                    return MembershipVerdict.Unknown("the attempt's job has already been ended");
                var h = Win32.OpenProcess(0x1000, false, (uint)peer.Pid);   // PROCESS_QUERY_LIMITED_INFORMATION
                if (h == IntPtr.Zero)
                    return MembershipVerdict.Unknown($"Windows would not open process {peer.Pid} (error {Marshal.GetLastWin32Error()})");
                try
                {
                    if (PeerImage.WindowsStartOf(h) is not { } created)
                        return MembershipVerdict.Unknown($"Windows would not say when process {peer.Pid} started");
                    if (created != peer.Start)
                        return MembershipVerdict.Outside($"process {peer.Pid} is no longer the process that connected");
                    if (!Win32.IsProcessInJob(h, owner._job, out var inside))
                        return MembershipVerdict.Unknown($"Windows would not say whether process {peer.Pid} is in the job (error {Marshal.GetLastWin32Error()})");
                    return inside ? MembershipVerdict.Member
                        : MembershipVerdict.Outside($"process {peer.Pid} is not in the Windows job TradeAgent made for this attempt");
                }
                finally { Win32.CloseHandle(h); }
            }
        }
    }

    /// <summary>
    /// THE LAUNCHER'S SESSION AS A GRANT'S CONTAINER (macOS, Linux): a peer is inside by exactly the proof the teardown
    /// kills by (<see cref="TreeTeardown.IsMember"/>) — the leader, a member of the session while its id names the live
    /// leader, or a descendant of either. Unknown until the launcher has made its session (<see cref="Held"/>), once the
    /// leader is gone or the teardown has run, and when the table cannot be read. A same-user process cannot join a
    /// session or become a descendant, so a replayed token is refused; it is WEAKER than a job on macOS and the interim on
    /// Linux until the per-attempt cgroup is ANDed onto it (RULING 1, <c>U-cell-linux-b</c>).
    /// </summary>
    sealed class SessionContainer(ContainedProcess owner) : AttemptContainer
    {
        public override string Name => "the session TradeAgent made for this attempt";

        public override MembershipVerdict Contains(PeerFacts peer)
        {
            if (owner._over) return MembershipVerdict.Unknown("the attempt's processes have already been ended");
            if (!owner.Held)
                return MembershipVerdict.Unknown("TradeAgent has not yet proved the attempt's own session is held");
            if (ProcessTable.Read(owner._leader) is not { Zombie: false } leader || leader.Start != owner._leaderStart)
                return MembershipVerdict.Unknown("the attempt's launcher has already exited, so its session is over");
            if (ProcessTable.Read(peer.Pid) is not { Zombie: false } now)
                return MembershipVerdict.Unknown($"the process table would not describe process {peer.Pid}");
            if (now.Start != peer.Start)
                return MembershipVerdict.Outside($"process {peer.Pid} is no longer the process that connected");
            return TreeTeardown.IsMember(peer.Pid, peer.Start, owner._leader, owner._leaderStart, owner._session)
                ? MembershipVerdict.Member
                : MembershipVerdict.Outside($"process {peer.Pid} is neither the attempt's launcher, in its session, nor descended from either");
        }
    }

    public void Dispose()
    {
        // THE TEARDOWN, NOT A TIDY-UP, and whether or not the leader is still running: a turn that ended
        // normally has nothing left to find, and a turn that left something behind is exactly the case
        // this closes. On Windows the job's KILL_ON_JOB_CLOSE is the same statement.
        End();
        lock (_teardown)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Process.Dispose();
    }

    /// <summary>
    /// Terminates everything in the job, waits — bounded — until the job says nothing in it is running, and
    /// closes it. KILL_ON_JOB_CLOSE alone starts the same termination as the handle closes and returns before
    /// it is over; the wait is what lets a caller say the tree is dead when it says so.
    /// </summary>
    void EndJob()
    {
        if (_closed || _job == IntPtr.Zero) return;
        _closed = true;
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            if (Win32.TerminateJobObject(_job, 1))
            {
                var until = DateTime.UtcNow + TreeTeardown.Bound;
                while (Win32.ActiveProcesses(_job) > 0 && DateTime.UtcNow < until) Thread.Sleep(10);
            }
        }
        catch (Exception) { /* the close below still kills what is in it */ }
        Win32.CloseHandle(_job);
    }

    [SupportedOSPlatform("windows")]
    static class Win32
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info,
            uint length, IntPtr returned);

        const int JobObjectBasicAccountingInformation = 1;

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
        {
            public long TotalUserTime;
            public long TotalKernelTime;
            public long ThisPeriodTotalUserTime;
            public long ThisPeriodTotalKernelTime;
            public uint TotalPageFaultCount;
            public uint TotalProcesses;
            public uint ActiveProcesses;
            public uint TotalTerminatedProcesses;
        }

        /// <summary>How many processes the job still holds, or 0 when it cannot say — the close still ends them.</summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);

        internal static uint ActiveProcesses(IntPtr job) =>
            QueryInformationJobObject(job, JobObjectBasicAccountingInformation, out var info,
                (uint)Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), IntPtr.Zero)
                ? info.ActiveProcesses
                : 0;
    }
}

/// <summary>
/// Starts an agent process inside something that outlives its parent links.
///
/// Windows: a Job Object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> and NO breakaway — neither
/// <c>JOB_OBJECT_LIMIT_BREAKAWAY_OK</c> nor <c>JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK</c> is set, so a
/// child that asks for <c>CREATE_BREAKAWAY_FROM_JOB</c> is refused rather than quietly granted.
///
/// macOS and Linux: its own session, through <see cref="ContainedLaunch"/>.
///
/// ONE JOB PER TURN, not one per app. Both properties are wanted and this is the arrangement that
/// gives both: the handle lives in TradeAgent's process, so if the app dies the job closes and the
/// turn dies with it; and because the job is the turn's own, cancelling the turn can close it
/// without touching anything else the app is running.
/// </summary>
public static class ProcessContainment
{
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    /// <summary>
    /// WINDOWS FAILURES A TEST CANNOT OTHERWISE ARRANGE (<c>U-grant-tree</c> fix pass 1): a job Windows will not make, an
    /// assign it refuses, a tree kill that fails. Scoped to the test's own async flow — a launch anywhere else never sees
    /// it — and it only ever makes a launch fail, never turns a check off. Null in the product.
    /// </summary>
    internal static readonly AsyncLocal<WindowsFaults?> Faults = new();
    const int JobObjectExtendedLimitInformation = 9;

    /// <summary>
    /// The tree under a Windows leader as the toolhelp snapshot has it, through <see cref="Faults"/>: a snapshot the test
    /// refuses is answered as Windows' own refusal is — null, never the leader alone.
    /// </summary>
    internal static IReadOnlyList<ProcessEntry>? ListTree(int leader, ulong leaderStart) =>
        Faults.Value is { RefuseSnapshot: true } ? null : WindowsProcessTable.Tree(leader, leaderStart);

    /// <summary>
    /// Whether a survivor of a teardown still runs, as the meter asks before every admission: a tree Windows would not list
    /// (<see cref="ProcessEntry.Unlisted"/>) is listed again through <see cref="ListTree"/> and runs until a listing shows
    /// none of it alive; any other survivor is asked as <see cref="TreeTeardown.StillRunning"/> asks it.
    /// </summary>
    internal static bool StillRunning(ProcessEntry survivor) =>
        survivor.Unlisted && survivor.Cgroup is null
            ? WindowsProcessTable.AnyAlive(ListTree(survivor.Pid, survivor.Start))
            : TreeTeardown.StillRunning(survivor);

    /// <summary>
    /// The launcher the Unix path needs: TradeAgent's own <c>trade</c>, where the app deployed it.
    /// A list because a test drives the same CLI through <c>dotnet trade.dll</c>, and because the
    /// product's single-file build is one element.
    /// </summary>
    public static IReadOnlyList<string>? DefaultLauncher()
    {
        var exe = Path.Combine(Paths.Bin, ToolDeployer.TradeCliName);
        return File.Exists(exe) ? [exe] : null;
    }

    /// <summary>
    /// Starts <paramref name="psi"/> held. Never throws for want of containment: a process that
    /// could not be held is started anyway and says so, because the alternative is an app that
    /// stops talking to its owner over a health property. What refuses in that state is the LIVE
    /// configuration, in one place, where a refusal is the safe answer.
    /// </summary>
    /// <summary>
    /// WHY A LAUNCH COULD NOT BE HELD ON THIS COMPUTER, KNOWN BEFORE ANYTHING STARTS — the owner's sentence with its repair —
    /// or null (<c>U-grant-tree</c>, RULING 2). macOS and Linux: TradeAgent's own trade command, which makes the session,
    /// is missing. Windows: null — the job is made by <see cref="Prepare"/>, before admission, and a job Windows will not make
    /// refuses there.
    /// </summary>
    public static string? CannotHold(IReadOnlyList<string>? launcher = null)
    {
        if (OperatingSystem.IsWindows()) return null;
        if ((launcher ?? DefaultLauncher()) is { Count: > 0 }) return null;
        return Unheld($"TradeAgent's own trade command, which holds the AI's programs in a session of their own on this " +
                      $"computer, is not at {Path.Combine(Paths.Bin, ToolDeployer.TradeCliName)}");
    }

    /// <summary>
    /// WHAT WILL HOLD THE NEXT LAUNCH, MADE BEFORE ANYTHING IS ADMITTED (<c>U-grant-tree</c> fix pass 1, RULING 2) — or a
    /// throw of <see cref="ErrorCode.CONTAINMENT_UNAVAILABLE"/> in the owner's words. Windows: the job itself is created here,
    /// so a job Windows will not make refuses while no row is written and no wake consumed; it used to be made only as the
    /// process started, after the ledger had reserved the turn and consumed its wakes for a launch that then never ran.
    /// macOS and Linux: the trade command that makes the session must be there (<see cref="CannotHold"/>). Handed to
    /// <see cref="Start(ProcessStartInfo, PreparedHold, IReadOnlyList{string}?, string?, AttemptCgroups?)"/>; disposed unused, it closes its job.
    /// </summary>
    public static PreparedHold Prepare(IReadOnlyList<string>? launcher = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (CannotHold(launcher) is { } unheld) throw new TradeAgentException(ErrorCode.CONTAINMENT_UNAVAILABLE, unheld);
            return new PreparedHold(IntPtr.Zero);
        }
        var job = CreateJob(out var why);
        if (job == IntPtr.Zero) throw new TradeAgentException(ErrorCode.CONTAINMENT_UNAVAILABLE, Unheld(why));
        return new PreparedHold(job);
    }

    /// <summary>
    /// Starts a launch that carries a grant in what <see cref="Prepare"/> made for it: on Windows that job, taken here; on
    /// macOS and Linux the launcher's session, as <see cref="Start(ProcessStartInfo, IReadOnlyList{string}?, bool, string?, AttemptCgroups?)"/> with
    /// <c>requireHeld</c> does.
    /// </summary>
    public static ContainedProcess Start(ProcessStartInfo psi, PreparedHold hold, IReadOnlyList<string>? launcher = null,
        string? attemptId = null, AttemptCgroups? cgroups = null)
    {
        ArgumentNullException.ThrowIfNull(psi);
        ArgumentNullException.ThrowIfNull(hold);
        if (OperatingSystem.IsWindows()) return StartWindows(psi, requireHeld: true, hold.Take());
        return Start(psi, launcher, requireHeld: true, attemptId, cgroups);
    }

    /// <summary>The refusal sentence for a launch that cannot be held: what, why it matters, and the repair.</summary>
    static string Unheld(string what) =>
        $"{what}, so TradeAgent did not start the AI: a launch it cannot hold could prove nothing to the trading service " +
        "and would spend your AI allowance on work TradeAgent would not accept. Restart TradeAgent — it puts back what it " +
        "needs to hold the AI as it starts. If this stays, reinstall TradeAgent.";

    /// <param name="requireHeld">
    /// A launch that carries a grant (<c>U-grant-tree</c>, RULING 2): one that cannot be held is REFUSED — thrown as
    /// <see cref="ErrorCode.CONTAINMENT_UNAVAILABLE"/> — rather than started bare. Before the process when that is known
    /// first (no launcher, a launcher that will not start, a job Windows will not make); and when it is known only after
    /// (a process Windows would not put in its job), it is returned with no <see cref="ContainedProcess.Container"/> and the
    /// caller ends it at once, carries what its teardown could not end to the meter, and refuses. False is the Doctor's
    /// fixed probes, which start no agent and carry no grant, and keep the bare fallback.
    /// </param>
    /// <param name="attemptId">The ledger's attempt, which names the attempt's cgroup; a fresh name when there is none.</param>
    /// <param name="cgroups">
    /// The delegated root the attempt's cgroup is made in (<c>U-cell-linux-b</c>, Linux): <see cref="AttemptCgroups.Current"/> unless a
    /// test opens its own. With none, the launch keeps the session alone and its Mechanism line says so.
    /// </param>
    public static ContainedProcess Start(ProcessStartInfo psi, IReadOnlyList<string>? launcher = null, bool requireHeld = false,
        string? attemptId = null, AttemptCgroups? cgroups = null)
    {
        ArgumentNullException.ThrowIfNull(psi);

        if (OperatingSystem.IsWindows()) return StartWindows(psi, requireHeld);

        var prefix = launcher ?? DefaultLauncher();
        if (requireHeld && CannotHold(prefix) is { } unheld)
            throw new TradeAgentException(ErrorCode.CONTAINMENT_UNAVAILABLE, unheld);
        if (prefix is not { Count: > 0 })
        {
            var bare = Process.Start(psi) ?? throw new TradeAgentException(ErrorCode.AI_RUNTIME_NOT_FOUND,
                $"{psi.FileName} would not start");
            return new ContainedProcess(bare, IntPtr.Zero, false, false,
                "not held: TradeAgent's own trade command, which is what starts a contained session on this " +
                "platform, is not installed");
        }

        // THE ATTEMPT'S CGROUP, made before anything starts so the launcher can be born into it. A launch that carries a grant and
        // whose cgroup cannot be made while the app holds a root is refused: refuse, never guess.
        var cgroup = AttemptFor(cgroups, attemptId, out var sessionOnly,
            requireHeld ? why => new TradeAgentException(ErrorCode.CONTAINMENT_UNAVAILABLE, Unheld(why)) : null);

        var exe = psi.FileName;
        var args = psi.ArgumentList.ToArray();
        Relaunch(psi, prefix, cgroup is null ? null : [ContainedLaunch.CgroupFlag, cgroup.Dir]);

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new TradeAgentException(ErrorCode.AI_RUNTIME_NOT_FOUND,
                $"{psi.FileName} would not start");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            // A LAUNCHER THAT WILL NOT START MUST NOT TAKE THE THING IT WAS LAUNCHING WITH IT.
            //
            // Measured while this file was being written: a `trade` at the expected path that was not
            // executable threw Win32Exception "Permission denied" out of Process.Start, and because
            // every agent process now comes through here that exception surfaced from `Doctor.RunAsync`
            // — the containment broke the diagnostics that would have explained it. The honest
            // behaviour is the one this class already states: start it anyway and say it is not held.
            // EXCEPT FOR A LAUNCH THAT CARRIES A GRANT (RULING 2): that one is refused, and nothing is started.
            cgroup?.Remove();
            if (requireHeld)
                throw new TradeAgentException(ErrorCode.CONTAINMENT_UNAVAILABLE,
                    Unheld($"TradeAgent's own trade command at {prefix[0]} would not start ({ex.Message})"));
            Command(psi, exe, args);
            var bare = Process.Start(psi) ?? throw new TradeAgentException(ErrorCode.AI_RUNTIME_NOT_FOUND,
                $"{exe} would not start");
            return new ContainedProcess(bare, IntPtr.Zero, false, false,
                $"not held: TradeAgent's own trade command at {prefix[0]} would not start ({ex.Message})");
        }

        // The session belongs to the LAUNCHER, which has not run yet — see ContainedProcess.Held.
        // What is known here is the only thing this line claims: the process was started through the
        // launcher, so it is on its way to a session of its own, and the kill reads the group back
        // rather than trusting that.
        return new ContainedProcess(process, IntPtr.Zero, ownSession: true, held: false,
            cgroup is not null ? $"a session of its own and the attempt's own cgroup {Path.GetFileName(cgroup.Dir)} ({AttemptCgroups.Ceilings}); " +
                                 "cancelling kills the cgroup, then the session"
            : sessionOnly is not null ? $"a session of its own (session only — no delegated cgroup: {sessionOnly}); cancelling kills its process group"
            : "a session of its own; cancelling kills its process group",
            cgroup: cgroup);
    }

    /// <summary>
    /// STARTS ROUTE (a)'S HARNESS (<c>U-cell-linux-c</c> item 3, L3): the codex process that holds the login, in the attempt's
    /// <c>harness/</c> leaf, with the attempt's <c>cell/</c> leaf made beside it, empty, for the exec-server's cell the harness starts.
    /// The launch's grant binds to the HARNESS LEAF, never the attempt: a process of the cell leaf is beneath the attempt, and a
    /// container on the attempt would count it a member — with egress <c>web</c> the cell would be one port from the door. Linux only,
    /// under a held delegated root, or <see cref="ErrorCode.CELL_UNAVAILABLE"/> and nothing starts.
    /// </summary>
    public static ContainedProcess StartHarness(ProcessStartInfo psi, PreparedHold hold, IReadOnlyList<string>? launcher = null,
        string? attemptId = null, AttemptCgroups? cgroups = null)
    {
        ArgumentNullException.ThrowIfNull(psi);
        ArgumentNullException.ThrowIfNull(hold);
        if (!OperatingSystem.IsLinux())
            throw new TradeAgentException(ErrorCode.CELL_UNAVAILABLE, $"route (a) runs on Linux only; {psi.FileName} was not started");
        var prefix = launcher ?? DefaultLauncher();
        if (CannotHold(prefix) is { } unheld) throw new TradeAgentException(ErrorCode.CELL_UNAVAILABLE, unheld);
        var root = cgroups ?? AttemptCgroups.Current;
        if (!root.Held)
            throw new TradeAgentException(ErrorCode.CELL_UNAVAILABLE,
                $"Route (a) runs only inside the cgroup TradeAgent's service delegates to it (Delegate=yes), and this computer gives it none: " +
                $"{root.Refusal}. {psi.FileName} was not started");
        AttemptCgroup attempt;
        try
        {
            attempt = root.Create(attemptId);
            attempt.MakeLeaves();
        }
        catch (TradeAgentException ex)
        {
            throw new TradeAgentException(ErrorCode.CELL_UNAVAILABLE, $"{ex.Message}; {psi.FileName} was not started", ex);
        }
        var exe = psi.FileName;
        var args = psi.ArgumentList.ToArray();
        Relaunch(psi, prefix!, [ContainedLaunch.CgroupFlag, attempt.Harness]);
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new TradeAgentException(ErrorCode.CELL_UNAVAILABLE,
                $"TradeAgent's own trade command at {prefix![0]} did not start; {exe} was not started");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            Command(psi, exe, args);
            attempt.Remove();
            throw new TradeAgentException(ErrorCode.CELL_UNAVAILABLE,
                $"TradeAgent's own trade command at {prefix![0]} would not start ({ex.Message}); {exe} was not started", ex);
        }
        return new ContainedProcess(process, IntPtr.Zero, ownSession: true, held: false,
            $"route (a)'s harness: a session of its own in the harness leaf of the attempt's cgroup {Path.GetFileName(attempt.Dir)} " +
            $"({AttemptCgroups.Ceilings}), its cell leaf beside it; the grant answers to the harness leaf alone; cancelling kills the " +
            "attempt's cgroup, then the session",
            cgroup: attempt, grantCgroup: attempt.Harness);
    }

    /// <summary>
    /// THE ATTEMPT'S CGROUP FOR ONE LAUNCH, or null with <paramref name="sessionOnly"/> saying why there is none (Linux without a
    /// delegated root, or a cgroup that could not be made for a launch that may go on without one). Null and silent off Linux.
    /// <paramref name="refuse"/>, when given, turns a cgroup that could not be made under a held root into the caller's refusal.
    /// </summary>
    static AttemptCgroup? AttemptFor(AttemptCgroups? cgroups, string? attemptId, out string? sessionOnly,
        Func<string, TradeAgentException>? refuse)
    {
        sessionOnly = null;
        if (!OperatingSystem.IsLinux()) return null;
        var root = cgroups ?? AttemptCgroups.Current;
        if (!root.Held)
        {
            sessionOnly = root.Refusal;
            return null;
        }
        try { return root.Create(attemptId); }
        catch (TradeAgentException ex)
        {
            if (refuse is not null) throw refuse(ex.Message);
            sessionOnly = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// STARTS <paramref name="psi"/> IN A CELL (<c>U-cell-linux-a</c> item 4), OR THROWS — and never starts it any other way.
    ///
    /// <para>The opposite of <see cref="Start(ProcessStartInfo, IReadOnlyList{string}?, bool, string?, AttemptCgroups?)"/>'s rule, on purpose: a caller that asked
    /// for a cell is relying on what the cell denies, so a launch that cannot have one is refused with
    /// <see cref="ErrorCode.CELL_UNAVAILABLE"/> in words — a platform with a <see cref="CellPlatform.Refusal"/> (not Linux, no
    /// Landlock, an ABI below 6, a security-module list nobody can read), no launcher, or a launcher that will not start — and
    /// nothing runs. The bare fallbacks stay for the launches that never asked for a cell.</para>
    ///
    /// <para>The policy is written owner-only under <see cref="CellPolicy.Directory"/> and named by its hash; the launcher is told
    /// only that file's path, and the cell's environment is the policy's alone. <paramref name="platform"/> is a test seam:
    /// production passes nothing and the machine is read.</para>
    /// </summary>
    /// <param name="cgroups">
    /// The delegated root the cell's attempt cgroup is made in (<c>U-cell-linux-b</c>): <see cref="AttemptCgroups.Current"/> unless a test
    /// opens its own. A cell is never started without one: no delegated root, or one missing its controllers, is
    /// <see cref="ErrorCode.CELL_UNAVAILABLE"/> naming <c>Delegate=yes</c>.
    /// </param>
    public static ContainedProcess Start(ProcessStartInfo psi, CellPolicy cell, IReadOnlyList<string>? launcher = null,
        CellPlatform? platform = null, AttemptCgroups? cgroups = null, string? attemptId = null)
    {
        ArgumentNullException.ThrowIfNull(psi);
        ArgumentNullException.ThrowIfNull(cell);

        var on = platform ?? CellPlatform.Read();
        if (on.Refusal() is { } refusal) throw new TradeAgentException(ErrorCode.CELL_UNAVAILABLE, refusal);

        var prefix = launcher ?? DefaultLauncher();
        if (prefix is not { Count: > 0 })
            throw new TradeAgentException(ErrorCode.CELL_UNAVAILABLE,
                $"TradeAgent's own trade command, which is what starts a cell, is not installed at " +
                $"{Path.Combine(Paths.Bin, ToolDeployer.TradeCliName)}; {psi.FileName} was not started");

        // NO CELL WITHOUT THE ATTEMPT'S CGROUP (U-cell-linux-b): a cell's descendants are held by it whatever session they make, and
        // a cell started without one would be weaker than its assurance profile says.
        // A POLICY THAT IS REFUSED IS REFUSED FIRST, by its own words (the inbox, the app's own tree): what is wrong with the request
        // is said before what the computer lacks. Write checks it again.
        if (cell.Problem() is { } problem) throw new TradeAgentException(ErrorCode.CELL_UNAVAILABLE, problem);
        var root = cgroups ?? AttemptCgroups.Current;
        if (!root.Held)
            throw new TradeAgentException(ErrorCode.CELL_UNAVAILABLE,
                $"A cell runs only inside the cgroup TradeAgent's service delegates to it (Delegate=yes), and this computer gives it none: " +
                $"{root.Refusal}. {psi.FileName} was not started, in a cell or outside one");
        AttemptCgroup attempt;
        try { attempt = root.Create(attemptId); }
        catch (TradeAgentException ex)
        {
            throw new TradeAgentException(ErrorCode.CELL_UNAVAILABLE,
                $"{ex.Message}; a cell runs only inside its attempt's cgroup (Delegate=yes), so {psi.FileName} was not started", ex);
        }

        var policyFile = cell.Write();
        var exe = psi.FileName;
        var args = psi.ArgumentList.ToArray();
        Relaunch(psi, prefix, [ContainedLaunch.CgroupFlag, attempt.Dir, ContainedLaunch.CellFlag, policyFile]);
        // The stage finds the app's cell folder from the app's own home, whatever the caller's environment says.
        psi.Environment["TRADEAGENT_HOME"] = Paths.Home;

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new TradeAgentException(ErrorCode.CELL_UNAVAILABLE,
                $"TradeAgent's own trade command at {prefix[0]} did not start; {exe} was not started");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            Command(psi, exe, args);
            CellStage.Discard(policyFile);
            attempt.Remove();
            throw new TradeAgentException(ErrorCode.CELL_UNAVAILABLE,
                $"TradeAgent's own trade command at {prefix[0]} would not start ({ex.Message}); {exe} was not started, " +
                "in a cell or outside one", ex);
        }

        return new ContainedProcess(process, IntPtr.Zero, ownSession: true, held: false,
            $"a cell ({on.Summary}; policy sha256 {cell.Hash[..12]}) in the attempt's own cgroup {Path.GetFileName(attempt.Dir)} " +
            $"({AttemptCgroups.Ceilings}) and a session of its own; cancelling kills the cgroup, then the session",
            new CellLaunch(cell.Hash, on, policyFile), attempt);
    }

    /// <summary>
    /// Rewrites the command to run through the launcher. The original program becomes the launcher's
    /// first argument, which is what <c>execv</c> then becomes — so nothing about the arguments,
    /// the working directory, the environment or the three redirected streams changes.
    /// </summary>
    static void Relaunch(ProcessStartInfo psi, IReadOnlyList<string> prefix, IReadOnlyList<string>? stage = null)
    {
        var exe = psi.FileName;
        var args = psi.ArgumentList.ToArray();
        psi.FileName = prefix[0];
        psi.ArgumentList.Clear();
        for (var i = 1; i < prefix.Count; i++) psi.ArgumentList.Add(prefix[i]);
        psi.ArgumentList.Add(ContainedLaunch.Flag);
        foreach (var a in stage ?? []) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(exe);
        foreach (var a in args) psi.ArgumentList.Add(a);
    }

    /// <summary>Puts a command back the way the caller wrote it, for the fallback above.</summary>
    static void Command(ProcessStartInfo psi, string exe, IReadOnlyList<string> args)
    {
        psi.FileName = exe;
        psi.ArgumentList.Clear();
        foreach (var a in args) psi.ArgumentList.Add(a);
    }

    [SupportedOSPlatform("windows")]
    static ContainedProcess StartWindows(ProcessStartInfo psi, bool requireHeld, IntPtr prepared = default)
    {
        var why = "";
        var job = prepared != IntPtr.Zero ? prepared : CreateJob(out why);
        if (requireHeld && job == IntPtr.Zero)
            throw new TradeAgentException(ErrorCode.CONTAINMENT_UNAVAILABLE, Unheld(why));

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new TradeAgentException(ErrorCode.AI_RUNTIME_NOT_FOUND,
                $"{psi.FileName} would not start");
        }
        catch (Exception)
        {
            // A JOB NOTHING WAS PUT IN is closed here, not left open for the life of the app (fix pass 1: it may be the
            // one Prepare made before the admission).
            CloseHandleSafe(job);
            throw;
        }

        if (job == IntPtr.Zero)
            return new ContainedProcess(process, IntPtr.Zero, false, false, $"not held: {why}");

        // ASSIGNED IMMEDIATELY AFTER START, NOT STARTED SUSPENDED.
        //
        // Suspended would close the last gap: CREATE_SUSPENDED, assign, resume, and the child has
        // executed nothing at all before it is in the job. It is not reachable from here — .NET's
        // Process API has no such flag, so taking it means calling CreateProcess directly and
        // rebuilding, by hand, the three redirected pipes, the UTF-8 encodings, the environment
        // block and CreateNoWindow. CreateNoWindow is the product's own rule, not a detail, and
        // re-implementing the one call that keeps a console off the owner's screen to close a
        // microsecond-wide race is the wrong trade.
        //
        // What the gap actually is, stated rather than waved at: between Process.Start returning and
        // the line below, a child that spawns instantly could spawn outside the job. From the assign
        // onwards Windows puts every descendant in the job automatically, and breakaway is not
        // permitted, so the window is bounded by the duration of one syscall and it closes for good.
        var assigned = Faults.Value is not { RefuseAssign: true } && AssignProcessToJobObject(job, process.Handle);
        if (!assigned)
        {
            var err = Faults.Value is { RefuseAssign: true } ? 5 : Marshal.GetLastWin32Error();
            CloseHandleSafe(job);
            // KNOWN ONLY AFTER THE START. Returned unheld — no container — never killed and dropped here: a launch that
            // carries a grant is ended at once by its caller (AgentSession), whose teardown says what it could not end, and
            // that is carried to the meter, which admits nothing while it runs (fix pass 1).
            return new ContainedProcess(process, IntPtr.Zero, false, false,
                $"not held: Windows would not put the AI's program in the job TradeAgent made for it (error {err})");
        }

        return new ContainedProcess(process, job, false, true,
            "a Windows job object; it dies with TradeAgent and breakaway is refused");
    }

    /// <summary>
    /// A job whose closing kills everything in it, and which nothing in it may leave.
    ///
    /// The flags are the whole of it. KILL_ON_JOB_CLOSE is what makes the handle in this process the
    /// agent's leash. BREAKAWAY_OK and SILENT_BREAKAWAY_OK are deliberately NOT set: with either of
    /// them a child that passes CREATE_BREAKAWAY_FROM_JOB — or, with the silent one, any child at
    /// all — starts outside the job, and the leash holds nothing.
    /// </summary>
    [SupportedOSPlatform("windows")]
    static IntPtr CreateJob(out string why)
    {
        why = "";
        if (Faults.Value is { RefuseJob: true })
        {
            why = "Windows would not create a job object (error 5)";
            return IntPtr.Zero;
        }
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            why = $"Windows would not create a job object (error {Marshal.GetLastWin32Error()})";
            return IntPtr.Zero;
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var block = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, block, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, block, (uint)size))
            {
                why = $"Windows would not set the job's limits (error {Marshal.GetLastWin32Error()})";
                CloseHandleSafe(job);
                return IntPtr.Zero;
            }
        }
        finally { Marshal.FreeHGlobal(block); }

        return job;
    }

    /// <summary>Closes a prepared job nobody took. Never throws.</summary>
    internal static void Release(IntPtr job)
    {
        if (job != IntPtr.Zero && OperatingSystem.IsWindows()) CloseHandleSafe(job);
    }

    [SupportedOSPlatform("windows")]
    static void CloseHandleSafe(IntPtr h)
    {
        try { CloseHandle(h); } catch (Exception) { /* nothing useful left to do */ }
    }

    /// <summary>The flags this build sets, for a test that reads them rather than trusting a comment.</summary>
    public static uint JobLimitFlags => JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

    /// <summary>Whether the flags allow a process to leave the job. Must stay false.</summary>
    public static bool JobAllowsBreakaway =>
        (JobLimitFlags & 0x0800) != 0 ||    // JOB_OBJECT_LIMIT_BREAKAWAY_OK
        (JobLimitFlags & 0x1000) != 0;      // JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateJobObjectW")]
    static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr handle);
}

/// <summary>The Windows failures <see cref="ProcessContainment.Faults"/> arranges for a test; each one only makes a launch fail.</summary>
internal sealed record WindowsFaults(bool RefuseJob = false, bool RefuseAssign = false, bool RefuseTreeKill = false,
    bool RefuseSnapshot = false);

/// <summary>
/// WHAT <see cref="ProcessContainment.Prepare"/> MADE FOR ONE LAUNCH BEFORE ITS ADMISSION (<c>U-grant-tree</c> fix pass 1): the
/// Windows job the launch will be put in, or nothing on macOS and Linux, where the launcher makes the session as it starts.
/// Taken once by the start; disposed untaken — the launch was refused or never started — it closes the job.
/// </summary>
public sealed class PreparedHold : IDisposable
{
    IntPtr _job;

    internal PreparedHold(IntPtr job) => _job = job;

    /// <summary>The job, handed to the start once; zero afterwards, and zero on macOS and Linux.</summary>
    internal IntPtr Take() => Interlocked.Exchange(ref _job, IntPtr.Zero);

    public void Dispose() => ProcessContainment.Release(Take());
}

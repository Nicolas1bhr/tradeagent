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
            Grant = "every launch carries a grant naming its role and attempt; only the Operations Director's may trade",
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

    internal ContainedProcess(Process process, IntPtr job, bool ownSession, bool held, string mechanism)
    {
        Process = process;
        _job = job;
        _ownSession = ownSession;
        _held = held;
        Mechanism = mechanism;
        if (OperatingSystem.IsWindows()) return;
        _leader = process.Id;
        _leaderStart = ProcessTable.Read(_leader)?.Start ?? 0;
        _session = ownSession ? _leader : 0;
    }

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
                if (_job == IntPtr.Zero && !_disposed)
                {
                    try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); }
                    catch (Exception) { /* already gone */ }
                }
                return Ended = TreeEnd.Nothing;
            }

            return Ended = TreeTeardown.End(_leader, _leaderStart, _session);
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
    const int JobObjectExtendedLimitInformation = 9;

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
    public static ContainedProcess Start(ProcessStartInfo psi, IReadOnlyList<string>? launcher = null)
    {
        ArgumentNullException.ThrowIfNull(psi);

        if (OperatingSystem.IsWindows()) return StartWindows(psi);

        var prefix = launcher ?? DefaultLauncher();
        if (prefix is not { Count: > 0 })
        {
            var bare = Process.Start(psi) ?? throw new TradeAgentException(ErrorCode.AI_RUNTIME_NOT_FOUND,
                $"{psi.FileName} would not start");
            return new ContainedProcess(bare, IntPtr.Zero, false, false,
                "not held: TradeAgent's own trade command, which is what starts a contained session on this " +
                "platform, is not installed");
        }

        var exe = psi.FileName;
        var args = psi.ArgumentList.ToArray();
        Relaunch(psi, prefix);

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
            "a session of its own; cancelling kills its process group");
    }

    /// <summary>
    /// Rewrites the command to run through the launcher. The original program becomes the launcher's
    /// first argument, which is what <c>execv</c> then becomes — so nothing about the arguments,
    /// the working directory, the environment or the three redirected streams changes.
    /// </summary>
    static void Relaunch(ProcessStartInfo psi, IReadOnlyList<string> prefix)
    {
        var exe = psi.FileName;
        var args = psi.ArgumentList.ToArray();
        psi.FileName = prefix[0];
        psi.ArgumentList.Clear();
        for (var i = 1; i < prefix.Count; i++) psi.ArgumentList.Add(prefix[i]);
        psi.ArgumentList.Add(ContainedLaunch.Flag);
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
    static ContainedProcess StartWindows(ProcessStartInfo psi)
    {
        var job = CreateJob(out var why);

        var process = Process.Start(psi) ?? throw new TradeAgentException(ErrorCode.AI_RUNTIME_NOT_FOUND,
            $"{psi.FileName} would not start");

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
        if (!AssignProcessToJobObject(job, process.Handle))
        {
            var err = Marshal.GetLastWin32Error();
            CloseHandleSafe(job);
            return new ContainedProcess(process, IntPtr.Zero, false, false,
                $"not held: the agent process could not be put in its job (error {err})");
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

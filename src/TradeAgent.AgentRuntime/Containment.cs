using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TradeAgent.Core;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// WHAT HOLDS AN AGENT PROCESS, reported in the words the Doctor card prints.
///
/// <paramref name="Held"/> is the only load-bearing field: false means the turn ran with nothing but
/// parent links holding it, which <c>U-containment</c>'s whole point is that a detached grandchild
/// escapes. <paramref name="SandboxOk"/> is separate and is false on every platform this build runs
/// on — a job is not a sandbox, and saying so is the difference between this unit and the one that
/// has not been built.
/// </summary>
public sealed record ContainmentFacts(bool Held, string Mechanism, bool SandboxOk, string Sandbox);

/// <summary>
/// One agent process and the thing that holds it: a Job Object on Windows, a session of its own on
/// macOS and Linux. Disposing it ends the turn's whole tree, including the parts that detached.
/// </summary>
public sealed class ContainedProcess : IDisposable
{
    readonly IntPtr _job;
    readonly bool _ownSession;
    bool _closed;
    bool _held;

    internal ContainedProcess(Process process, IntPtr job, bool ownSession, bool held, string mechanism)
    {
        Process = process;
        _job = job;
        _ownSession = ownSession;
        _held = held;
        Mechanism = mechanism;
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
            if (!_ownSession) return false;
            var group = CurrentGroup();
            if (group > 0) _held = true;
            return _held;
        }
    }

    /// <summary>What is holding it, or why nothing is. One line, for a health row.</summary>
    public string Mechanism { get; }

    /// <summary>
    /// Ends the process and everything it started.
    ///
    /// The job (or the group) FIRST, then the ordinary tree kill: the strong mechanism is the one
    /// that reaches a grandchild whose parent has already exited, and the tree walk is what still
    /// works when this build could not get a job at all.
    /// </summary>
    public void Kill()
    {
        CloseJob();
        if (CurrentGroup() is > 0 and var group) Posix.KillGroup(group);
        try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); }
        catch (Exception) { /* already gone */ }
    }

    /// <summary>
    /// The process group to take down with this turn, or 0 when there is none to take.
    ///
    /// Zero whenever the child's group is TradeAgent's own, which is the state a build with no
    /// launcher is in: killing that group would kill the app. Read at the moment of the kill rather
    /// than remembered from the start, because the start is exactly when it is not yet knowable.
    /// </summary>
    int CurrentGroup()
    {
        if (OperatingSystem.IsWindows() || !_ownSession) return 0;
        var group = Posix.GroupOf(Process.Id);
        return group > 1 && group != Posix.GroupOf(Environment.ProcessId) ? group : 0;
    }

    public void Dispose()
    {
        // KILL_ON_JOB_CLOSE means this line is the teardown, not a tidy-up: every process still in
        // the job dies as the last handle to it closes. A turn that has ended normally has nothing
        // left in it, and a turn that left something behind is exactly the case this closes.
        CloseJob();
        if (!Process.HasExited && CurrentGroup() is > 0 and var group) Posix.KillGroup(group);
        Process.Dispose();
    }

    void CloseJob()
    {
        if (_closed || _job == IntPtr.Zero) return;
        _closed = true;
        if (OperatingSystem.IsWindows()) Win32.CloseHandle(_job);
    }

    [SupportedOSPlatform("windows")]
    static class Win32
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
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

        Relaunch(psi, prefix);
        var process = Process.Start(psi) ?? throw new TradeAgentException(ErrorCode.AI_RUNTIME_NOT_FOUND,
            $"{psi.FileName} would not start");

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

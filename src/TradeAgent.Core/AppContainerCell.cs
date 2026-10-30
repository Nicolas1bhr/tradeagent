using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TradeAgent.Core;

/// <summary>
/// THE WINDOWS CELL'S PRIMITIVE (<c>U-cell-windows</c>; R-containment-decision.md route C2, CHOSEN): a STABLE AppContainer — a profile
/// made with <c>CreateAppContainerProfile</c>, its SID carried to <c>CreateProcess</c> in <c>SECURITY_CAPABILITIES</c> — inside a job
/// whose closing kills everything in it and which nothing in it may leave. The process is created SUSPENDED, put in the job, and only
/// then resumed, so it executes nothing outside the job (the gap <see cref="ProcessContainment"/>'s Process.Start path states and
/// cannot close). It inherits EXACTLY its three standard handles (<c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c>), never another of the app's,
/// starts with the environment it is given and nothing inherited, and opens no window and no console.
///
/// <para><b>What the container may touch</b> is what an ACE names its SID on (<see cref="Grant"/>): everything else the owner's account
/// can reach — the app's <c>state/</c>, the owner's logins, other processes, loopback — is default-denied by Windows for an
/// AppContainer process, which runs at Low integrity. The network is denied unless a capability SID is given (<see cref="InternetClient"/>
/// opens every outbound port; port 443 alone needs the firewall rule the cell's egress names).</para>
///
/// <para>Here is the mechanism only. What a cell is granted is <see cref="CellPolicy"/>'s, and what holds is what the Windows rows
/// measure; nothing here is a claim.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class AppContainerCell
{
    /// <summary>The well-known <c>internetClient</c> capability SID: outbound connections to the internet, every port.</summary>
    public const string InternetClient = "S-1-15-3-1";

    /// <summary>
    /// The container's SID string for profile <paramref name="name"/>, making the profile when it does not exist. The SID depends on
    /// the name alone (<c>DeriveAppContainerSidFromAppContainerName</c>), so it is the same on every start and for every account.
    /// </summary>
    public static string Profile(string name, string description)
    {
        var hr = CreateAppContainerProfile(name, name, description, IntPtr.Zero, 0, out var sid);
        if (hr != 0)
        {
            if ((hr & 0xFFFF) != ErrorAlreadyExists)
                throw new TradeAgentException(ErrorCode.CELL_UNAVAILABLE,
                    $"Windows would not make the cell's AppContainer profile {name} (HRESULT 0x{hr:X8})");
            return SidOf(name);
        }
        try { return SidString(sid); }
        finally { FreeSid(sid); }
    }

    /// <summary>The SID string a profile named <paramref name="name"/> has, whether or not it exists.</summary>
    public static string SidOf(string name)
    {
        var hr = DeriveAppContainerSidFromAppContainerName(name, out var sid);
        if (hr != 0)
            throw new TradeAgentException(ErrorCode.CELL_UNAVAILABLE, $"Windows would not derive the SID of AppContainer {name} (HRESULT 0x{hr:X8})");
        try { return SidString(sid); }
        finally { FreeSid(sid); }
    }

    /// <summary>Deletes profile <paramref name="name"/>; true when it is gone (or never was).</summary>
    public static bool DeleteProfile(string name)
    {
        var hr = DeleteAppContainerProfile(name);
        return hr == 0 || (hr & 0xFFFF) is ErrorFileNotFound or ErrorNotFound;
    }

    /// <summary>
    /// Grants <paramref name="sid"/> <paramref name="rights"/> on <paramref name="path"/> — a directory's inheritable to everything under
    /// it, a file's on the file alone — as one explicit ACE. Windows propagates the directory's to what already exists beneath it.
    /// </summary>
    public static void Grant(string path, string sid, FileSystemRights rights)
    {
        var who = new SecurityIdentifier(sid);
        if (Directory.Exists(path))
        {
            var info = new DirectoryInfo(path);
            var acl = info.GetAccessControl(AccessControlSections.Access);
            acl.AddAccessRule(new FileSystemAccessRule(who, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(acl);
        }
        else if (File.Exists(path))
        {
            var info = new FileInfo(path);
            var acl = info.GetAccessControl(AccessControlSections.Access);
            acl.AddAccessRule(new FileSystemAccessRule(who, rights, AccessControlType.Allow));
            info.SetAccessControl(acl);
        }
        else throw new TradeAgentException(ErrorCode.CELL_UNAVAILABLE, $"{path} does not exist, so a cell cannot be granted it");
    }

    /// <summary>
    /// Removes from <paramref name="item"/> every explicit ACE naming an AppContainer or capability SID (<c>S-1-15-2-*</c>, <c>S-1-15-3-*</c>)
    /// — what a cell could have written on a file it made — leaving inherited ACEs alone.
    /// </summary>
    public static void PurgeContainerAces(FileSystemInfo item)
    {
        FileSystemSecurity acl = item is DirectoryInfo d ? d.GetAccessControl(AccessControlSections.Access) : ((FileInfo)item).GetAccessControl(AccessControlSections.Access);
        var stale = acl.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(r => (SecurityIdentifier)r.IdentityReference).Where(s => s.Value.StartsWith("S-1-15-2-", StringComparison.Ordinal) || s.Value.StartsWith("S-1-15-3-", StringComparison.Ordinal))
            .Distinct().ToList();
        if (stale.Count == 0) return;
        foreach (var sid in stale) acl.PurgeAccessRules(sid);
        if (item is DirectoryInfo dir) dir.SetAccessControl((DirectorySecurity)acl);
        else ((FileInfo)item).SetAccessControl((FileSecurity)acl);
    }

    /// <summary>Removes every explicit ACE naming <paramref name="sid"/> from <paramref name="path"/>. Never throws for a path that is gone.</summary>
    public static void Revoke(string path, string sid)
    {
        var who = new SecurityIdentifier(sid);
        if (Directory.Exists(path))
        {
            var info = new DirectoryInfo(path);
            var acl = info.GetAccessControl(AccessControlSections.Access);
            acl.PurgeAccessRules(who);
            info.SetAccessControl(acl);
        }
        else if (File.Exists(path))
        {
            var info = new FileInfo(path);
            var acl = info.GetAccessControl(AccessControlSections.Access);
            acl.PurgeAccessRules(who);
            info.SetAccessControl(acl);
        }
    }

    /// <summary>
    /// STARTS <paramref name="program"/> in the AppContainer <paramref name="containerSid"/> with <paramref name="capabilities"/> (none:
    /// no network at all), in a kill-on-close job without breakaway: created suspended, assigned, resumed. Its standard streams are
    /// pipes the caller holds, or the handles <paramref name="stdio"/> names (the cell stage's own, passed through); its environment is
    /// <paramref name="environment"/> and nothing else; its window station and desktop its own (<see cref="PrivateDesktop"/>) —
    /// <paramref name="ownDesktop"/> false is the probe's control alone, the station the caller runs on, which no ACE grants the container. Throws <see cref="TradeAgentException"/> (<see cref="ErrorCode.CELL_UNAVAILABLE"/>)
    /// naming the step that failed; a process that was created and could not be put in its job is killed before the throw.
    /// </summary>
    public static AppContainerProcess Start(string program, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyList<KeyValuePair<string, string>> environment, string containerSid, IReadOnlyList<string> capabilities,
        AppContainerStdio? stdio = null, bool ownDesktop = true)
    {
        var commandLine = CommandLine(program, arguments);
        var block = IntPtr.Zero;
        var job = IntPtr.Zero;
        PrivateDesktop? desktop = null;
        var owned = new List<SafeFileHandle>();
        SafeFileHandle? inParent = null, outParent = null, errParent = null;
        IntPtr childIn, childOut, childErr;
        var sids = new List<IntPtr>();
        IntPtr containerPtr = IntPtr.Zero, capsPtr = IntPtr.Zero, scPtr = IntPtr.Zero, attrs = IntPtr.Zero, handlesPtr = IntPtr.Zero;
        var attrsReady = false;
        try
        {
            block = EnvironmentBlock(environment);
            job = CreateCellJob();
            if (ownDesktop) desktop = PrivateDesktop.Create(containerSid);
            if (stdio is { } given)
            {
                childIn = Inheritable(given.Input, owned);
                childOut = Inheritable(given.Output, owned);
                childErr = Inheritable(given.Error, owned);
            }
            else
            {
                (inParent, var inChild) = Pipe(childReads: true);
                (outParent, var outChild) = Pipe(childReads: false);
                (errParent, var errChild) = Pipe(childReads: false);
                owned.AddRange([inChild, outChild, errChild]);
                (childIn, childOut, childErr) = (inChild.DangerousGetHandle(), outChild.DangerousGetHandle(), errChild.DangerousGetHandle());
            }

            if (!ConvertStringSidToSid(containerSid, out containerPtr)) throw Win32("read the container SID " + containerSid);
            var sidAttrSize = Marshal.SizeOf<SidAndAttributes>();
            if (capabilities.Count > 0)
            {
                capsPtr = Marshal.AllocHGlobal(sidAttrSize * capabilities.Count);
                for (var i = 0; i < capabilities.Count; i++)
                {
                    if (!ConvertStringSidToSid(capabilities[i], out var cap)) throw Win32("read the capability SID " + capabilities[i]);
                    sids.Add(cap);
                    Marshal.StructureToPtr(new SidAndAttributes { Sid = cap, Attributes = SeGroupEnabled }, capsPtr + i * sidAttrSize, false);
                }
            }
            scPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>());
            Marshal.StructureToPtr(new SecurityCapabilities
            {
                AppContainerSid = containerPtr, Capabilities = capsPtr, CapabilityCount = (uint)capabilities.Count
            }, scPtr, false);

            // THE THREE HANDLES AND NO OTHER: bInheritHandles must be TRUE for a handle list, and the list is what keeps every other
            // inheritable handle of this process out of the cell. Three distinct values: the list refuses a duplicate.
            var handles = new[] { childIn, childOut, childErr }.Distinct().ToArray();
            handlesPtr = Marshal.AllocHGlobal(IntPtr.Size * handles.Length);
            for (var i = 0; i < handles.Length; i++) Marshal.WriteIntPtr(handlesPtr, i * IntPtr.Size, handles[i]);

            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            attrs = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attrs, 2, 0, ref size)) throw Win32("make the cell's attribute list");
            attrsReady = true;
            if (!UpdateProcThreadAttribute(attrs, 0, AttributeSecurityCapabilities, scPtr, Marshal.SizeOf<SecurityCapabilities>(), IntPtr.Zero, IntPtr.Zero))
                throw Win32("set the cell's AppContainer");
            if (!UpdateProcThreadAttribute(attrs, 0, AttributeHandleList, handlesPtr, IntPtr.Size * handles.Length, IntPtr.Zero, IntPtr.Zero))
                throw Win32("set the cell's handle list");

            var si = new StartupInfoEx();
            si.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
            si.StartupInfo.lpDesktop = desktop?.NamePointer ?? IntPtr.Zero;
            si.StartupInfo.dwFlags = StartfUseStdHandles;
            si.StartupInfo.hStdInput = childIn;
            si.StartupInfo.hStdOutput = childOut;
            si.StartupInfo.hStdError = childErr;
            si.lpAttributeList = attrs;

            // CREATE_NO_WINDOW: no console is made, so nothing reaches the owner's screen and a console program does not die
            // allocating one it cannot reach (the probe's 0xC0000142). SUSPENDED: nothing runs before the job holds it.
            const uint flags = ExtendedStartupInfoPresent | CreateUnicodeEnvironment | CreateNoWindow | CreateSuspended;
            if (!CreateProcess(program, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, true, flags, block, workingDirectory,
                    ref si, out var pi))
                throw Win32($"start {program} in the cell");

            if (!AssignProcessToJobObject(job, pi.hProcess))
            {
                var err = Marshal.GetLastWin32Error();
                TerminateProcess(pi.hProcess, 1);
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                throw new TradeAgentException(ErrorCode.CELL_UNAVAILABLE,
                    $"Windows would not put {program} in its cell's job (error {err}); it was ended before it ran");
            }
            if (ResumeThread(pi.hThread) == uint.MaxValue)
            {
                var err = Marshal.GetLastWin32Error();
                TerminateJobObject(job, 1);
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                throw new TradeAgentException(ErrorCode.CELL_UNAVAILABLE, $"Windows would not resume {program} in its cell (error {err})");
            }
            CloseHandle(pi.hThread);

            var process = new AppContainerProcess((int)pi.dwProcessId, new SafeProcessHandle(pi.hProcess, true), job, desktop,
                inParent, outParent, errParent);
            job = IntPtr.Zero;
            desktop = null;
            inParent = outParent = errParent = null;
            return process;
        }
        finally
        {
            foreach (var h in owned) h.Dispose();
            inParent?.Dispose();
            outParent?.Dispose();
            errParent?.Dispose();
            if (job != IntPtr.Zero) CloseHandle(job);
            desktop?.Dispose();
            if (attrsReady) DeleteProcThreadAttributeList(attrs);
            foreach (var p in new[] { attrs, scPtr, capsPtr, handlesPtr }) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            foreach (var s in sids) LocalFree(s);
            if (containerPtr != IntPtr.Zero) LocalFree(containerPtr);
            if (block != IntPtr.Zero) Marshal.FreeHGlobal(block);
        }
    }

    /// <summary>
    /// The command line a Windows program's C runtime splits back into exactly <paramref name="arguments"/> (the MSVCRT rules), the
    /// program first. An argument holding NUL is refused: it cannot be passed.
    /// </summary>
    public static string CommandLine(string program, IReadOnlyList<string> arguments)
    {
        var sb = new StringBuilder();
        Quote(sb, program);
        foreach (var a in arguments)
        {
            sb.Append(' ');
            Quote(sb, a);
        }
        return sb.ToString();
    }

    static void Quote(StringBuilder sb, string arg)
    {
        if (arg.Contains('\0'))
            throw new TradeAgentException(ErrorCode.CELL_UNAVAILABLE, "an argument of a cell's program holds NUL, which Windows cannot pass");
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            sb.Append(arg);
            return;
        }
        sb.Append('"');
        for (var i = 0; i < arg.Length; i++)
        {
            var slashes = 0;
            while (i < arg.Length && arg[i] == '\\') { slashes++; i++; }
            if (i == arg.Length) { sb.Append('\\', slashes * 2); break; }
            if (arg[i] == '"') sb.Append('\\', slashes * 2 + 1).Append('"');
            else sb.Append('\\', slashes).Append(arg[i]);
        }
        sb.Append('"');
    }

    /// <summary>The environment as Windows takes it: <c>NAME=value</c> pairs sorted by name without case, each NUL-ended, then NUL.</summary>
    static IntPtr EnvironmentBlock(IReadOnlyList<KeyValuePair<string, string>> environment)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in environment.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (k.Length == 0 || k.Contains('=') || k.Contains('\0') || v.Contains('\0'))
                throw new TradeAgentException(ErrorCode.CELL_UNAVAILABLE, $"the cell's environment variable '{k}' cannot be passed to Windows");
            sb.Append(k).Append('=').Append(v).Append('\0');
        }
        if (sb.Length == 0) sb.Append('\0');
        sb.Append('\0');
        return Marshal.StringToHGlobalUni(sb.ToString());
    }

    /// <summary>A job that kills everything in it when its last handle closes, permits no breakaway, and holds no UI of anyone else's.</summary>
    static IntPtr CreateCellJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw Win32("make the cell's job");
        var ok = false;
        try
        {
            // KILL_ON_JOB_CLOSE and DIE_ON_UNHANDLED_EXCEPTION; BREAKAWAY_OK and SILENT_BREAKAWAY_OK deliberately NOT set: with either a
            // child could start outside the job and the leash would hold nothing.
            var limits = new ExtendedLimitInformation();
            limits.Basic.LimitFlags = JobKillOnClose | JobDieOnUnhandledException;
            Set(job, JobObjectExtendedLimitInformation, limits, "set the cell job's limits");
            // NO UI OF ANYONE ELSE'S: no USER handle outside the job, no clipboard, no global atoms, no desktop switch, no system
            // settings, no display settings, no logoff.
            Set(job, JobObjectBasicUiRestrictions, new UiRestrictions { Class = 0xFF }, "set the cell job's UI restrictions");
            ok = true;
            return job;
        }
        finally { if (!ok) CloseHandle(job); }
    }

    static void Set<T>(IntPtr job, int infoClass, T info, string what) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var p = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, p, false);
            if (!SetInformationJobObject(job, infoClass, p, (uint)size)) throw Win32(what);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    static (SafeFileHandle Parent, SafeFileHandle Child) Pipe(bool childReads)
    {
        var sa = new SecurityAttributes { nLength = Marshal.SizeOf<SecurityAttributes>(), bInheritHandle = 1 };
        if (!CreatePipe(out var read, out var write, ref sa, 0)) throw Win32("make a pipe for the cell");
        var (parent, child) = childReads ? (write, read) : (read, write);
        if (!SetHandleInformation(parent, HandleFlagInherit, 0))
        {
            var e = Win32("keep the app's end of the cell's pipe from it");
            parent.Dispose();
            child.Dispose();
            throw e;
        }
        return (parent, child);
    }

    static IntPtr Inheritable(SafeHandle handle, List<SafeFileHandle> owned)
    {
        if (!DuplicateHandle(GetCurrentProcess(), handle, GetCurrentProcess(), out var dup, 0, true, DuplicateSameAccess))
            throw Win32("pass a standard handle to the cell");
        owned.Add(dup);
        return dup.DangerousGetHandle();
    }

    static string SidString(IntPtr sid)
    {
        if (!ConvertSidToStringSid(sid, out var str)) throw Win32("write a SID");
        try { return Marshal.PtrToStringUni(str)!; }
        finally { LocalFree(str); }
    }

    static TradeAgentException Win32(string what)
    {
        var err = Marshal.GetLastWin32Error();
        return new TradeAgentException(ErrorCode.CELL_UNAVAILABLE, $"Windows would not {what} (error {err}: {new Win32Exception(err).Message})");
    }

    internal const int JobObjectBasicAccountingInformation = 1;
    const int JobObjectBasicUiRestrictions = 4;
    const int JobObjectExtendedLimitInformation = 9;
    const uint JobKillOnClose = 0x2000, JobDieOnUnhandledException = 0x400;
    const int ErrorAlreadyExists = 183, ErrorFileNotFound = 2, ErrorNotFound = 1168;
    const uint SeGroupEnabled = 0x4, StartfUseStdHandles = 0x100, HandleFlagInherit = 0x1, DuplicateSameAccess = 0x2;
    const uint ExtendedStartupInfoPresent = 0x80000, CreateUnicodeEnvironment = 0x400, CreateNoWindow = 0x8000000, CreateSuspended = 0x4;
    static readonly IntPtr AttributeSecurityCapabilities = new(0x20009), AttributeHandleList = new(0x20002);

    [StructLayout(LayoutKind.Sequential)] struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] struct SecurityCapabilities { public IntPtr AppContainerSid, Capabilities; public uint CapabilityCount, Reserved; }
    [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }
    [StructLayout(LayoutKind.Sequential)] struct UiRestrictions { public uint Class; }

    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)] struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInformation { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }

    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong A, B, C, D, E, F; }

    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    static extern int CreateAppContainerProfile(string name, string display, string description, IntPtr caps, uint count, out IntPtr sid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    static extern int DeleteAppContainerProfile(string name);

    [DllImport("advapi32.dll")]
    static extern IntPtr FreeSid(IntPtr sid);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ConvertStringSidToSid(string sid, out IntPtr ptr);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr str);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr p);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nint size, IntPtr previous, IntPtr returned);

    [DllImport("kernel32.dll")]
    static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateProcess(string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string? directory,
        ref StartupInfoEx startup, out ProcessInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool TerminateProcess(IntPtr process, uint code);

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
    internal static extern bool TerminateJobObject(IntPtr job, uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out BasicAccounting info, int length, IntPtr returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DuplicateHandle(IntPtr sourceProcess, SafeHandle source, IntPtr targetProcess, out SafeFileHandle target,
        uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [StructLayout(LayoutKind.Sequential)]
    internal struct BasicAccounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }
}

/// <summary>The handles a cell's standard streams are passed through to, in place of pipes the caller holds (the cell stage's own).</summary>
public sealed record AppContainerStdio(SafeHandle Input, SafeHandle Output, SafeHandle Error);

/// <summary>
/// ONE PROCESS IN A WINDOWS CELL, and the job that holds it and everything it starts. Disposing it closes the job, which ends every
/// process in it — the cancel.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AppContainerProcess : IDisposable
{
    readonly SafeProcessHandle _process;
    IntPtr _job;

    readonly PrivateDesktop? _desktop;

    internal AppContainerProcess(int pid, SafeProcessHandle process, IntPtr job, PrivateDesktop? desktop, SafeFileHandle? input, SafeFileHandle? output, SafeFileHandle? error)
    {
        Id = pid;
        _process = process;
        _job = job;
        _desktop = desktop;
        var utf8 = new UTF8Encoding(false);
        if (input is not null) StandardInput = new StreamWriter(new FileStream(input, FileAccess.Write, 4096, false), utf8) { AutoFlush = true };
        if (output is not null) StandardOutput = new StreamReader(new FileStream(output, FileAccess.Read, 4096, false), utf8);
        if (error is not null) StandardError = new StreamReader(new FileStream(error, FileAccess.Read, 4096, false), utf8);
    }

    /// <summary>The process id.</summary>
    public int Id { get; }

    /// <summary>The cell's standard input, when the caller holds its pipes.</summary>
    public StreamWriter? StandardInput { get; }

    /// <summary>The cell's standard output, when the caller holds its pipes.</summary>
    public StreamReader? StandardOutput { get; }

    /// <summary>The cell's standard error, when the caller holds its pipes.</summary>
    public StreamReader? StandardError { get; }

    /// <summary>Whether the process has exited.</summary>
    public bool HasExited => WaitForExit(0);

    /// <summary>Waits up to <paramref name="milliseconds"/> (-1: forever) for the process to exit.</summary>
    public bool WaitForExit(int milliseconds)
    {
        using var wait = new ProcessWaitHandle(_process);
        return wait.WaitOne(milliseconds);
    }

    /// <summary>Waits for the process to exit.</summary>
    public async Task WaitForExitAsync(CancellationToken ct = default)
    {
        while (!WaitForExit(0)) await Task.Delay(20, ct);
    }

    /// <summary>The exit code (259, STILL_ACTIVE, while it runs).</summary>
    public int ExitCode => GetExitCodeProcess(_process, out var code) ? unchecked((int)code) : -1;

    /// <summary>How many processes the job holds now (the cell's whole tree), or -1 when Windows would not say.</summary>
    public int ActiveProcesses =>
        _job != IntPtr.Zero && AppContainerCell.QueryInformationJobObject(_job, AppContainerCell.JobObjectBasicAccountingInformation, out var info,
            Marshal.SizeOf<AppContainerCell.BasicAccounting>(), IntPtr.Zero) ? (int)info.ActiveProcesses : -1;

    /// <summary>Whether process <paramref name="pid"/> is in this cell's job (false when it cannot be opened, or is gone).</summary>
    public bool Holds(int pid)
    {
        var h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, (uint)pid);
        if (h == IntPtr.Zero) return false;
        try { return AppContainerCell.IsProcessInJob(h, _job, out var inJob) && inJob; }
        finally { AppContainerCell.CloseHandle(h); }
    }

    /// <summary>The ids of every process the job holds now (empty when Windows would not say).</summary>
    public IReadOnlyList<int> ProcessIds()
    {
        if (_job == IntPtr.Zero) return [];
        const int max = 1024;
        var size = 8 + IntPtr.Size * max;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(_job, 3 /* JobObjectBasicProcessIdList */, buffer, size, IntPtr.Zero)) return [];
            var count = Marshal.ReadInt32(buffer, 4);
            return [.. Enumerable.Range(0, Math.Min(count, max)).Select(i => (int)Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size))];
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>
    /// Closes this side's job handle WITHOUT asking Windows to end anything — what the app's crash does — so the job's kill-on-close is
    /// what ends the cell. After it the job can no longer be asked anything.
    /// </summary>
    public void ReleaseJob()
    {
        if (_job == IntPtr.Zero) return;
        AppContainerCell.CloseHandle(_job);
        _job = IntPtr.Zero;
    }

    /// <summary>Ends every process in the job. True when Windows did.</summary>
    public bool Kill() => _job != IntPtr.Zero && AppContainerCell.TerminateJobObject(_job, 1);

    public void Dispose()
    {
        try { StandardInput?.Dispose(); } catch (IOException) { /* the cell closed it */ }
        StandardOutput?.Dispose();
        StandardError?.Dispose();
        if (_job != IntPtr.Zero)
        {
            AppContainerCell.TerminateJobObject(_job, 1);
            AppContainerCell.CloseHandle(_job);
            _job = IntPtr.Zero;
        }
        _process.Dispose();
        _desktop?.Dispose();
    }

    /// <summary>The window station and desktop the cell runs on: its own, never the owner's (<c>winsta\desktop</c>).</summary>
    public string Desktop => _desktop?.Name ?? "";

    sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle process)
        {
            if (!DuplicateHandle(GetCurrentProcess(), process, GetCurrentProcess(), out var dup, 0, false, 2))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            SafeWaitHandle = dup;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryInformationJobObject(IntPtr job, int infoClass, IntPtr info, int length, IntPtr returned);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DuplicateHandle(IntPtr sourceProcess, SafeProcessHandle source, IntPtr targetProcess, out SafeWaitHandle target,
        uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
}

/// <summary>
/// A CELL'S OWN WINDOW STATION AND DESKTOP (<c>U-cell-windows</c> item 1, measured on Windows 11 26200: the pinned codex dies 0xC0000142
/// in an AppContainer that can open no window station, where cmd and curl start). Created for one cell, ACL'd to the account and the
/// container SID alone, labelled Low so the container may use it — so the cell's programs start without the container ever being
/// granted the OWNER'S window station or desktop: nothing of his screen, windows or clipboard is on the cell's desktop. The handles
/// live as long as the cell; Windows destroys both when the last handle closes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PrivateDesktop : IDisposable
{
    static readonly object Swap = new();
    IntPtr _station, _desktop, _name;

    PrivateDesktop(IntPtr station, IntPtr desktop, string name)
    {
        _station = station;
        _desktop = desktop;
        Name = name;
        _name = Marshal.StringToHGlobalUni(name);
    }

    /// <summary><c>station\desktop</c>, as <c>STARTUPINFO.lpDesktop</c> names it.</summary>
    public string Name { get; }

    internal IntPtr NamePointer => _name;

    /// <summary>
    /// Makes the pair for <paramref name="containerSid"/>. The desktop is made on the new station by switching this process's window
    /// station to it for the one call and back (<c>CreateDesktop</c> has no station argument), under a lock.
    /// </summary>
    public static PrivateDesktop Create(string containerSid)
    {
        var user = WindowsIdentity.GetCurrent().User!.Value;
        var sddl = $"D:P(A;;GA;;;{user})(A;;GA;;;{containerSid})S:(ML;;NW;;;LW)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var sd, IntPtr.Zero))
            throw Fail("describe the cell's window station");
        try
        {
            var sa = new Sa { nLength = Marshal.SizeOf<Sa>(), lpSecurityDescriptor = sd, bInheritHandle = 0 };
            var station = "TradeAgent-cell-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
            var ws = CreateWindowStation(station, 1 /* CWF_CREATE_ONLY */, 0x37F /* WINSTA_ALL_ACCESS */, ref sa);
            if (ws == IntPtr.Zero) throw Fail("make the cell's window station");
            IntPtr dt;
            lock (Swap)
            {
                var mine = GetProcessWindowStation();
                if (!SetProcessWindowStation(ws))
                {
                    var e = Fail("enter the cell's window station");
                    CloseWindowStation(ws);
                    throw e;
                }
                try { dt = CreateDesktop("cell", IntPtr.Zero, IntPtr.Zero, 0, 0x10000000 /* GENERIC_ALL */, ref sa); }
                finally { SetProcessWindowStation(mine); }
            }
            if (dt == IntPtr.Zero)
            {
                var e = Fail("make the cell's desktop");
                CloseWindowStation(ws);
                throw e;
            }
            return new PrivateDesktop(ws, dt, station + "\\cell");
        }
        finally { LocalFree(sd); }
    }

    public void Dispose()
    {
        if (_desktop != IntPtr.Zero) { CloseDesktop(_desktop); _desktop = IntPtr.Zero; }
        if (_station != IntPtr.Zero) { CloseWindowStation(_station); _station = IntPtr.Zero; }
        if (_name != IntPtr.Zero) { Marshal.FreeHGlobal(_name); _name = IntPtr.Zero; }
    }

    static TradeAgentException Fail(string what)
    {
        var err = Marshal.GetLastWin32Error();
        return new TradeAgentException(ErrorCode.CELL_UNAVAILABLE, $"Windows would not {what} (error {err}: {new Win32Exception(err).Message})");
    }

    [StructLayout(LayoutKind.Sequential)] struct Sa { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr sd, IntPtr size);

    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateWindowStationW")]
    static extern IntPtr CreateWindowStation(string name, uint flags, uint access, ref Sa sa);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateDesktopW")]
    static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, ref Sa sa);

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr GetProcessWindowStation();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetProcessWindowStation(IntPtr station);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseWindowStation(IntPtr station);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseDesktop(IntPtr desktop);
}

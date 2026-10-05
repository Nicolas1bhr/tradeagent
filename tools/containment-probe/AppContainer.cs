using System.Diagnostics;
using System.Runtime.InteropServices;
using static ContainmentProbe.Native;

namespace ContainmentProbe;

/// <summary>
/// Candidate C2 — a STABLE AppContainer launch: CreateAppContainerProfile for the identity,
/// SECURITY_CAPABILITIES carrying the AppContainer SID and any capability SIDs, passed to
/// CreateProcess through PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES. This is the documented,
/// non-experimental Win32 app-isolation primitive (DOC E-MS-APP-ISOLATION, appcontainer-isolation,
/// read 2026-10-05). The child runs at Low integrity with the AppContainer SID, so every path that
/// lacks an explicit ACE for that SID (or for ALL APPLICATION PACKAGES) is default-denied.
/// </summary>
internal sealed unsafe class AppContainer : IDisposable
{
    // Well-known capability SIDs (bypass DeriveCapabilitySidsFromName for the handful we use).
    const string CapInternetClient = "S-1-15-3-1";

    public string Name { get; }
    public IntPtr Sid { get; }
    public string SidString { get; }

    AppContainer(string name, IntPtr sid, string sidStr) { Name = name; Sid = sid; SidString = sidStr; }

    /// <summary>Creates (or re-derives) the profile and returns it. Throws with the HRESULT on failure.</summary>
    public static AppContainer Create(string name)
    {
        int hr = CreateAppContainerProfile(name, name, "TradeAgent R-containment probe", null, 0, out var sid);
        if (hr != 0)
        {
            // Already there from an earlier run: re-derive the SID rather than fail.
            if ((hr & 0xFFFF) == ERROR_ALREADY_EXISTS)
            {
                int d = DeriveAppContainerSidFromAppContainerName(name, out sid);
                if (d != 0) throw new InvalidOperationException($"DeriveAppContainerSid failed hr=0x{d:X8}");
            }
            else throw new InvalidOperationException($"CreateAppContainerProfile failed hr=0x{hr:X8}");
        }
        if (!ConvertSidToStringSid(sid, out var str)) throw new InvalidOperationException("ConvertSidToStringSid failed");
        var s = Marshal.PtrToStringUni(str)!;
        LocalFree(str);
        return new AppContainer(name, sid, s);
    }

    /// <summary>
    /// Grants the AppContainer SID access to a directory tree via icacls. This is how an AppContainer
    /// is let into a path it would otherwise be denied: the ACE names the container's own SID. Only a
    /// directory named here is reachable; nothing else the user owns is.
    /// </summary>
    public bool Grant(string dir, bool write)
    {
        var rights = write ? "(OI)(CI)(F)" : "(OI)(CI)(RX)";
        var psi = new ProcessStartInfo("icacls", $"\"{dir}\" /grant \"*{SidString}:{rights}\"")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        p.WaitForExit(20000);
        return p.ExitCode == 0;
    }

    public void Revoke(string dir)
    {
        var psi = new ProcessStartInfo("icacls", $"\"{dir}\" /remove \"*{SidString}\"")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        try { using var p = Process.Start(psi)!; p.WaitForExit(20000); } catch { /* best effort */ }
    }

    public sealed record Launched(uint Pid, IntPtr Process, IntPtr Thread, IntPtr Job)
    {
        public uint Wait(uint ms) { WaitForSingleObject(Process, ms); GetExitCodeProcess(Process, out var c); return c; }
        public bool Alive() => WaitForSingleObject(Process, 0) == 0x00000102; // WAIT_TIMEOUT => still running
        public void KillJob() { if (Job != IntPtr.Zero) CloseHandle(Job); }     // KILL_ON_JOB_CLOSE
        public void Close()
        {
            if (Job != IntPtr.Zero) CloseHandle(Job);
            if (Thread != IntPtr.Zero) CloseHandle(Thread);
            if (Process != IntPtr.Zero) CloseHandle(Process);
        }
    }

    const int JobObjectExtendedLimitInformation = 9;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    /// <summary>
    /// Launches <paramref name="commandLine"/> inside this AppContainer. When <paramref name="internet"/>
    /// the internetClient capability SID is added. When <paramref name="inJob"/> the process is put in a
    /// KILL_ON_JOB_CLOSE job (cell 16) — the job handle is returned so the caller can close it to kill
    /// the whole tree.
    /// </summary>
    public Launched Launch(string commandLine, bool internet, bool inJob, string? workingDir = null)
    {
        var caps = new List<IntPtr>();
        if (internet)
        {
            if (!ConvertStringSidToSid(CapInternetClient, out var capSid))
                throw new InvalidOperationException("ConvertStringSidToSid(internetClient) failed");
            caps.Add(capSid);
        }

        var capArray = IntPtr.Zero;
        try
        {
            var sc = new SECURITY_CAPABILITIES { AppContainerSid = Sid, CapabilityCount = (uint)caps.Count };
            if (caps.Count > 0)
            {
                int sz = Marshal.SizeOf<SID_AND_ATTRIBUTES>();
                capArray = Marshal.AllocHGlobal(sz * caps.Count);
                for (int i = 0; i < caps.Count; i++)
                    Marshal.StructureToPtr(new SID_AND_ATTRIBUTES { Sid = caps[i], Attributes = SE_GROUP_ENABLED },
                        capArray + i * sz, false);
                sc.Capabilities = capArray;
            }

            // Build the attribute list holding the security capabilities.
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            var attr = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attr, 1, 0, ref size))
                throw new Win32Like("InitializeProcThreadAttributeList");

            var scPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SECURITY_CAPABILITIES>());
            Marshal.StructureToPtr(sc, scPtr, false);
            if (!UpdateProcThreadAttribute(attr, 0, PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES, scPtr,
                    new IntPtr(Marshal.SizeOf<SECURITY_CAPABILITIES>()), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Like("UpdateProcThreadAttribute");

            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            si.lpAttributeList = attr;

            uint flags = EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT;
            if (inJob) flags |= CREATE_SUSPENDED;

            if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags,
                    IntPtr.Zero, workingDir, ref si, out var pi))
                throw new Win32Like("CreateProcess(AppContainer)");

            IntPtr job = IntPtr.Zero;
            if (inJob)
            {
                job = CreateJobObject(IntPtr.Zero, null);
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                int isz = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                var block = Marshal.AllocHGlobal(isz);
                Marshal.StructureToPtr(info, block, false);
                SetInformationJobObject(job, JobObjectExtendedLimitInformation, block, (uint)isz);
                Marshal.FreeHGlobal(block);
                AssignProcessToJobObject(job, pi.hProcess);
                ResumeThread(pi.hThread);
            }

            DeleteProcThreadAttributeList(attr);
            Marshal.FreeHGlobal(attr);
            Marshal.FreeHGlobal(scPtr);
            return new Launched(pi.dwProcessId, pi.hProcess, pi.hThread, job);
        }
        finally
        {
            if (capArray != IntPtr.Zero) Marshal.FreeHGlobal(capArray);
            foreach (var c in caps) LocalFree(c);
        }
    }

    public void Dispose() { /* SID is owned by the profile; DeleteAppContainerProfile frees the name */ }

    public void Delete() => DeleteAppContainerProfile(Name);

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    sealed class Win32Like(string what) : Exception($"{what} failed, win32={Marshal.GetLastWin32Error()}");
}

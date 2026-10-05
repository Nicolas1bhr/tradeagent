using System.Runtime.InteropServices;

namespace ContainmentProbe;

/// <summary>
/// Grants an AppContainer SID the minimal access to the interactive window station and desktop.
///
/// WHY THIS IS NEEDED: a .NET (and many a GUI-linked) process launched in an AppContainer dies with
/// STATUS_DLL_INIT_FAILED (0xC0000142) because user32/gdi32 cannot connect to winsta0\default — the
/// lowbox token is denied the interactive window station by default. A plain console program like
/// cmd.exe survives because it loads no user32; the moment a worker needs the CLR it does. A real
/// contained seat (U-contain-seats) therefore has to do exactly this, and the probe measures it.
///
/// It is REVERSED on teardown (the ACE for the container SID is removed) so the owner's interactive
/// window station is left as found; the SID is unique to this probe profile, so the revoke is exact.
/// </summary>
internal static class WindowStation
{
    const uint SE_WINDOW_OBJECT = 7;   // SE_OBJECT_TYPE: 5 is SE_LMSHARE; the window object is 7
    const uint DACL_SECURITY_INFORMATION = 0x4;
    const uint GRANT_ACCESS = 1, REVOKE_ACCESS = 4;
    const uint TRUSTEE_IS_SID = 0, TRUSTEE_IS_UNKNOWN = 0;
    const uint NO_INHERITANCE = 0, OBJECT_INHERIT_ACE = 1, CONTAINER_INHERIT_ACE = 2, INHERIT_ONLY_ACE = 8;
    const uint GENERIC_ALL = 0x10000000;
    const uint WINSTA_ALL_ACCESS = 0x37F;

    // Run from a CHILD process (verb "grantui"/"revokeui"), so a mistake in raw ACL marshalling cannot
    // fail-fast the host. The window station is a per-session object, so a child's change is seen by the
    // host's later launches in the same session.
    public static int GrantByString(string sidStr)
    {
        if (!Native.ConvertStringSidToSid(sidStr, out var sid)) { Console.WriteLine("GRANTUI bad-sid"); return 2; }
        try
        {
            var ws = GetProcessWindowStation();
            var dt = GetThreadDesktop(GetCurrentThreadId());
            Console.WriteLine($"GRANTUI ws={ws != IntPtr.Zero} dt={dt != IntPtr.Zero}");
            var a = AddAce(ws, sid, GENERIC_ALL, CONTAINER_INHERIT_ACE | OBJECT_INHERIT_ACE | INHERIT_ONLY_ACE, GRANT_ACCESS);
            var b = AddAce(ws, sid, WINSTA_ALL_ACCESS, NO_INHERITANCE, GRANT_ACCESS);
            var c = AddAce(dt, sid, GENERIC_ALL, NO_INHERITANCE, GRANT_ACCESS);
            Console.WriteLine($"GRANTUI winsta-inherit={a} winsta={b} desktop={c}");
            return a && b && c ? 0 : 1;
        }
        finally { Native.LocalFree(sid); }
    }

    public static int RevokeByString(string sidStr)
    {
        if (!Native.ConvertStringSidToSid(sidStr, out var sid)) return 2;
        try
        {
            var ws = GetProcessWindowStation();
            var dt = GetThreadDesktop(GetCurrentThreadId());
            if (ws != IntPtr.Zero) AddAce(ws, sid, 0, NO_INHERITANCE, REVOKE_ACCESS);
            if (dt != IntPtr.Zero) AddAce(dt, sid, 0, NO_INHERITANCE, REVOKE_ACCESS);
            return 0;
        }
        finally { Native.LocalFree(sid); }
    }

    static bool AddAce(IntPtr handle, IntPtr sid, uint access, uint inheritance, uint mode)
    {
        var gsi = GetSecurityInfo(handle, SE_WINDOW_OBJECT, DACL_SECURITY_INFORMATION, IntPtr.Zero, IntPtr.Zero,
                out var oldDacl, IntPtr.Zero, out var sd);
        if (gsi != 0) { Console.WriteLine($"  GetSecurityInfo err={gsi}"); return false; }
        try
        {
            var ea = new EXPLICIT_ACCESS[1];
            ea[0] = new EXPLICIT_ACCESS
            {
                grfAccessPermissions = access,
                grfAccessMode = mode,
                grfInheritance = inheritance,
                Trustee = new TRUSTEE { TrusteeForm = TRUSTEE_IS_SID, TrusteeType = TRUSTEE_IS_UNKNOWN, ptstrName = sid }
            };
            var sea = SetEntriesInAcl(1, ea, oldDacl, out var newDacl);
            if (sea != 0) { Console.WriteLine($"  SetEntriesInAcl err={sea}"); return false; }
            try
            {
                var ssi = SetSecurityInfo(handle, SE_WINDOW_OBJECT, DACL_SECURITY_INFORMATION, IntPtr.Zero, IntPtr.Zero, newDacl, IntPtr.Zero);
                if (ssi != 0) Console.WriteLine($"  SetSecurityInfo err={ssi}");
                return ssi == 0;
            }
            finally { Native.LocalFree(newDacl); }
        }
        finally { Native.LocalFree(sd); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct EXPLICIT_ACCESS { public uint grfAccessPermissions, grfAccessMode, grfInheritance; public TRUSTEE Trustee; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct TRUSTEE { public IntPtr pMultipleTrustee; public uint MultipleTrusteeOperation, TrusteeForm, TrusteeType; public IntPtr ptstrName; }

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    [DllImport("advapi32.dll")]
    static extern uint GetSecurityInfo(IntPtr handle, uint ObjectType, uint SecurityInfo,
        IntPtr owner, IntPtr group, out IntPtr ppDacl, IntPtr ppSacl, out IntPtr ppSecurityDescriptor);

    [DllImport("advapi32.dll")]
    static extern uint SetSecurityInfo(IntPtr handle, uint ObjectType, uint SecurityInfo,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

    [DllImport("advapi32.dll", EntryPoint = "SetEntriesInAclW", CharSet = CharSet.Unicode)]
    static extern uint SetEntriesInAcl(uint cCountOfExplicitEntries, [In] EXPLICIT_ACCESS[] pListOfExplicitEntries,
        IntPtr OldAcl, out IntPtr NewAcl);
}

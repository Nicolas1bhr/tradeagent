using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace TradeAgent.Core;

/// <summary>
/// WHAT A PROCESS'S TOKEN SAYS ABOUT ITS APPCONTAINER (<c>U-cell-windows-egress</c>): read from outside, by the app, of a process of the
/// same account. Two readers: the grant's job container, which refuses a peer whose process runs in an AppContainer (a cell's), and the
/// benchmark, which names the slot a measured cell ran in by its token's container SID rather than by the order slots are claimed in.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsToken
{
    const uint ProcessQueryLimitedInformation = 0x1000, TokenQuery = 0x0008;
    const int TokenIsAppContainer = 29, TokenAppContainerSid = 31;

    /// <summary>
    /// The AppContainer SID of process <paramref name="pid"/>: <c>""</c> when its token is not an AppContainer's, null when Windows would
    /// not say (the process cannot be opened, its token cannot be read). Null is never "not a cell".
    /// </summary>
    public static string? AppContainerSidOf(int pid)
    {
        if (pid <= 0) return null;
        var h = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (h == IntPtr.Zero) return null;
        try { return AppContainerSidOf(h); }
        finally { CloseHandle(h); }
    }

    /// <summary>The same question of a process handle opened with at least <c>PROCESS_QUERY_LIMITED_INFORMATION</c>.</summary>
    public static string? AppContainerSidOf(IntPtr process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token)) return null;
        try
        {
            if (!GetTokenInformation(token, TokenIsAppContainer, out uint isAppContainer, sizeof(uint), out _)) return null;
            if (isAppContainer == 0) return "";
            GetTokenInformation(token, TokenAppContainerSid, IntPtr.Zero, 0, out var length);
            if (length == 0) return null;
            var buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!GetTokenInformation(token, TokenAppContainerSid, buffer, length, out _)) return null;
                var sid = Marshal.ReadIntPtr(buffer);   // TOKEN_APPCONTAINER_INFORMATION { PSID TokenAppContainer }
                return sid == IntPtr.Zero ? null : new SecurityIdentifier(sid).Value;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(token); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetTokenInformation(IntPtr token, int infoClass, out uint value, uint length, out uint returned);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr buffer, uint length, out uint returned);
}

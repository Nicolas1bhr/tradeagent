using System.Runtime.InteropServices;

namespace TradeAgent.Core;

/// <summary>
/// THE KERNEL CALLS A CELL IS MADE OF (<c>U-cell-linux-a</c>), and nothing else. Linux only: every entry point answers "none" or
/// refuses elsewhere rather than reaching for a symbol the platform does not have.
/// </summary>
public static partial class CellNative
{
    /// <summary><c>landlock_create_ruleset</c>, <c>landlock_add_rule</c>, <c>landlock_restrict_self</c>: one number on every architecture since 5.13.</summary>
    const long SysLandlockCreateRuleset = 444;

    const uint LANDLOCK_CREATE_RULESET_VERSION = 1;

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    static extern long Syscall3(long number, nint a1, nint a2, nint a3);

    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    static extern nint realpath(string path, nint resolved);

    [DllImport("libc", EntryPoint = "free")]
    static extern void free(nint p);

    /// <summary>
    /// The Landlock ABI the running kernel reports, or 0 and the reason it did not: <c>ENOSYS</c> (not built in) or
    /// <c>EOPNOTSUPP</c> (built in, switched off at boot).
    /// </summary>
    public static (int Abi, string? Error) LandlockAbi()
    {
        if (!OperatingSystem.IsLinux()) return (0, "not Linux");
        try
        {
            var abi = Syscall3(SysLandlockCreateRuleset, 0, 0, (nint)LANDLOCK_CREATE_RULESET_VERSION);
            if (abi > 0) return ((int)abi, null);
            var errno = Marshal.GetLastPInvokeError();
            return (0, errno switch
            {
                38 => "errno 38: the kernel has no Landlock",
                95 => "errno 95: Landlock is switched off in this kernel's boot configuration",
                _ => $"errno {errno}"
            });
        }
        catch (Exception ex) { return (0, ex.GetType().Name); }
    }

    /// <summary>Where an existing path leads once every symlink in it is followed, or null when it does not exist (or on Windows).</summary>
    public static string? RealPath(string path)
    {
        if (OperatingSystem.IsWindows()) return null;
        try
        {
            var p = realpath(path, 0);
            if (p == 0) return null;
            try { return Marshal.PtrToStringUTF8(p); }
            finally { free(p); }
        }
        catch (Exception) { return null; }
    }
}

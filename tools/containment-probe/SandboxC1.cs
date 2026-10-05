using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ContainmentProbe;

/// <summary>
/// Candidate C1 — the EXPERIMENTAL Experimental_CreateProcessInSandbox family (DOC
/// E-MS-CREATE-SANDBOX, read 2026-10-05; page ms.date 2026-06-01: "These APIs are experimental and
/// subject to change", exported from processmodel.dll, "Header: Not publicly available (use
/// GetProcAddress)"). This probe answers what can be answered WITHOUT a product commitment: is the
/// export actually present on this build, and is it callable? The full matrix for C1 is NOT RUN
/// because the launch needs a compiled FlatBuffer sandbox specification (file id "SBOX", schema
/// SandboxSpec.fbs, version "0.1.0") and that schema is not publicly available — a spec cannot be
/// produced honestly, and guessing one is exactly what this project refuses to do.
/// </summary>
internal static class SandboxC1
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    delegate bool CreateProcessInSandbox(
        string? applicationName, string? commandLine, IntPtr pa, IntPtr ta,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr env, string? curDir,
        ref Native.STARTUPINFO si, string identity, byte[] spec, uint specSize,
        out Native.PROCESS_INFORMATION pi);

    public static Dictionary<string, object?> Probe(string baseDir)
    {
        var o = new Dictionary<string, object?>();
        var dll = Native.LoadLibraryEx("processmodel.dll", IntPtr.Zero, Native.LOAD_LIBRARY_SEARCH_SYSTEM32);
        o["processmodel_dll_loaded"] = dll != IntPtr.Zero;
        if (dll == IntPtr.Zero) { o["error"] = Marshal.GetLastWin32Error(); return o; }

        var p1 = Native.GetProcAddress(dll, "Experimental_CreateProcessInSandbox");
        var p2 = Native.GetProcAddress(dll, "Experimental_CreateProcessAsUserInSandbox");
        o["export_CreateProcessInSandbox"] = p1 != IntPtr.Zero;
        o["export_CreateProcessAsUserInSandbox"] = p2 != IntPtr.Zero;

        if (p1 != IntPtr.Zero)
        {
            // Call it in a CHILD process so an experimental API misbehaving cannot take the host down.
            // The child feeds a deliberately invalid spec; a FALSE + documented error confirms the
            // entry point is live and validating, which is all a probe can honestly claim here.
            try
            {
                var psi = new ProcessStartInfo(Environment.ProcessPath!, "c1call")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                using var c = Process.Start(psi)!;
                var txt = c.StandardOutput.ReadToEnd() + c.StandardError.ReadToEnd();
                c.WaitForExit(15000);
                o["callable_child_exit"] = c.HasExited ? c.ExitCode : (int?)null;
                o["callable_result"] = txt.Trim();
            }
            catch (Exception ex) { o["callable_result"] = "launch failed: " + ex.Message; }
        }

        o["status"] = p1 != IntPtr.Zero
            ? "PRESENT but matrix NOT RUN: valid FlatBuffer SandboxSpec (SBOX/0.1.0) needs the non-public schema"
            : "export absent on this build";
        o["experimental"] = true;
        return o;
    }

    /// <summary>The isolated, risky call. Feeds an invalid spec and prints the Win32 result.</summary>
    public static int CallChild()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("C1CALL n/a"); return 0; }
        var dll = Native.LoadLibraryEx("processmodel.dll", IntPtr.Zero, Native.LOAD_LIBRARY_SEARCH_SYSTEM32);
        var p = Native.GetProcAddress(dll, "Experimental_CreateProcessInSandbox");
        if (p == IntPtr.Zero) { Console.WriteLine("C1CALL export-absent"); return 0; }
        var fn = Marshal.GetDelegateForFunctionPointer<CreateProcessInSandbox>(p);
        var si = new Native.STARTUPINFO(); si.cb = Marshal.SizeOf<Native.STARTUPINFO>();
        var spec = new byte[64];                    // not a valid FlatBuffer: verification must reject it
        try
        {
            bool ok = fn("C:\\Windows\\System32\\cmd.exe", "cmd.exe", IntPtr.Zero, IntPtr.Zero, false, 0,
                IntPtr.Zero, null, ref si, "TA.C1.Probe.001", spec, (uint)spec.Length, out _);
            Console.WriteLine($"C1CALL returned={ok} lasterr={Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex) { Console.WriteLine("C1CALL threw " + ex.GetType().Name + " " + ex.Message); }
        return 0;
    }
}

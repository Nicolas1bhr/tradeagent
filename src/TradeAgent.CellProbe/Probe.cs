using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TradeAgent.CellProbe;

/// <summary>
/// THE WINDOWS CELL PROBE (<c>U-cell-probe</c>; R31 D5): a compiled program the Windows benchmark runs inside a cell, through the same stage, AppContainer token,
/// job and private desktop as an agent's tools, and once outside any cell. It replaces the two PowerShell 5.1 scripts the <c>state-unreadable</c> and
/// <c>processes-denied</c> rows ran: under an enforced App Control policy (Smart App Control On included) PowerShell runs ConstrainedLanguage, both scripts lose
/// their .NET calls, and no proof can be written. A compiled program depends on no execution policy, module path, profile, language mode or script scanner;
/// App Control judges it as an executable, by signature and reputation as it judges the app (a copy outside the install folder keeps its signature, but a
/// path rule would not cover it). NOT MEASURED under Smart App Control On: today the app and the probe are both unsigned, and SAC On blocks the app first.
///
/// <para><b>One marker line per target</b>, read whole by <c>CellDenial.Judge</c>: <c>&lt;WORD&gt;-OPEN</c> when the call succeeded,
/// <c>&lt;WORD&gt;-DENIED</c> ONLY when Windows answered error 5 (ERROR_ACCESS_DENIED), and <c>&lt;WORD&gt;-FAILED &lt;number&gt;</c> for anything else. Numbers,
/// never words: Windows writes its words in the system's language. A FAILED is not a denial, so a row given one fails.</para>
///
/// <para><b>Verbs:</b> <c>list &lt;control&gt; &lt;denied&gt;</c> — .NET's own directory listing, the method the PowerShell script called (under 5.1's .NET
/// Framework it lists with FindFirstFileW; .NET 10 opens the folder with CreateFileW for listing, then reads it: both need the right to list it), on the workspace
/// folder (<c>CTL-LIST</c>) and on the forbidden one (<c>LIST</c>); <c>kill &lt;pid&gt;</c> — a process of its own started first and ended by
/// <c>OpenProcess(PROCESS_TERMINATE)</c> and <c>TerminateProcess</c> (<c>CTL-KILL</c>: the calls work from where it runs), then the same two calls on the
/// target (<c>KILL</c>). Anything else: no marker line, exit 2.</para>
/// </summary>
public static class Probe
{
    /// <summary>ERROR_ACCESS_DENIED: the one Windows error a probe line calls a denial.</summary>
    public const int AccessDenied = 5;

    /// <summary>
    /// The marker line of <paramref name="word"/>: <paramref name="error"/> null opened; Windows' error 5 denied; any other number failed, a negative one being an
    /// HRESULT that carries no Windows error, written in hex.
    /// </summary>
    public static string Line(string word, int? error) => error switch
    {
        null => $"{word}-OPEN",
        AccessDenied => $"{word}-DENIED",
        int n when n >= 0 => $"{word}-FAILED {n.ToString(CultureInfo.InvariantCulture)}",
        int h => $"{word}-FAILED 0x{h.ToString("X8", CultureInfo.InvariantCulture)}",
    };

    /// <summary>Runs one call of the probe, writing its marker lines to <paramref name="o"/>: 0 when it ran, 2 when it was not a call it knows (or <c>kill</c> off Windows).</summary>
    public static int Run(IReadOnlyList<string> args, TextWriter o)
    {
        switch (args)
        {
            case ["list", var control, var denied]:
                o.WriteLine(Line("CTL-LIST", List(control)));
                o.WriteLine(Line("LIST", List(denied)));
                o.Flush();
                return 0;
            case ["kill", var target] when int.TryParse(target, NumberStyles.None, CultureInfo.InvariantCulture, out var pid):
                if (!OperatingSystem.IsWindows())
                {
                    o.WriteLine("cell-probe: kill asks Windows itself, so it runs on Windows only");
                    return 2;
                }
                Kill(pid, o);
                o.Flush();
                return 0;
            default:
                o.WriteLine("usage: cell-probe list <control-folder> <denied-folder> | cell-probe kill <pid>");
                return 2;
        }
    }

    /// <summary>
    /// <paramref name="folder"/> listed by <c>Directory.GetFileSystemEntries</c> (the method the PowerShell script called): null when it listed, else Windows' error.
    /// .NET carries it in the exception's HRESULT: ERROR_ACCESS_DENIED alone becomes <see cref="UnauthorizedAccessException"/> (0x80070005), every other
    /// Windows error HRESULT_FROM_WIN32 of its number; an HRESULT outside that facility is returned whole, negative, so it is never 5.
    /// </summary>
    public static int? List(string folder)
    {
        try
        {
            _ = Directory.GetFileSystemEntries(folder);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return Win32(ex.HResult);
        }
    }

    /// <summary>The Windows error an HRESULT carries (facility 7, failure), else the HRESULT itself.</summary>
    public static int Win32(int hresult) => ((uint)hresult & 0xFFFF0000u) == 0x80070000u ? hresult & 0xFFFF : hresult;

    [SupportedOSPlatform("windows")]
    static void Kill(int target, TextWriter o)
    {
        // A PROCESS OF ITS OWN, THE SCRIPT'S CONTROL: cmd waiting on input the probe holds, so it lives until it is ended.
        var comspec = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } c ? c : Path.Combine(Environment.SystemDirectory, "cmd.exe");
        Process? own = null;
        try
        {
            own = Process.Start(new ProcessStartInfo(comspec, "/d /c findstr x") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true });
            o.WriteLine(own is null ? Line("CTL-KILL", -1) : Ended("CTL-KILL", own.Id));
        }
        catch (Win32Exception ex) { o.WriteLine(Line("CTL-KILL", ex.NativeErrorCode)); }
        finally
        {
            if (own is not null)
            {
                try { own.Kill(); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { /* ended */ }
                own.Dispose();
            }
        }
        o.WriteLine(Ended("KILL", target));
    }

    /// <summary>
    /// <paramref name="pid"/> opened for termination and ended. DENIED only when <c>OpenProcess</c> itself answered error 5; a handle that opened has reached the
    /// process, so a <c>TerminateProcess</c> that fails after it is a FAILED, never a denial.
    /// </summary>
    [SupportedOSPlatform("windows")]
    static string Ended(string word, int pid)
    {
        var handle = OpenProcess(ProcessTerminate, false, unchecked((uint)pid));
        if (handle == IntPtr.Zero) return Line(word, Marshal.GetLastPInvokeError());
        try
        {
            return TerminateProcess(handle, 1)
                ? Line(word, null)
                : $"{word}-FAILED opened for termination, then TerminateProcess error {Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture)}";
        }
        finally { CloseHandle(handle); }
    }

    const uint ProcessTerminate = 0x0001;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr handle);
}

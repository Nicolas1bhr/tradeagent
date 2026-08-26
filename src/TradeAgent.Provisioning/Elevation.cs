using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using TradeAgent.Core;

namespace TradeAgent.Provisioning;

/// <summary>
/// The one thing the user is ever allowed to be asked to do: click Yes on a Windows prompt.
///
/// TradeAgent never asks for a password, never writes an elevation dialog of its own, and never
/// pretends to be Windows. It asks Windows to start a program elevated, and <b>Windows</b> shows its
/// own consent prompt — the one the user already knows and can trust. Everything TradeAgent
/// installs for itself is per-user and needs none of this; elevation exists only for third-party
/// installers (ATAS) that genuinely require it.
/// </summary>
public static class Elevation
{
    /// <summary>True when this process is already running as an administrator. False off Windows.</summary>
    public static bool IsElevated => CheckElevated();

    static bool CheckElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            // Not being able to answer the question is not the same as being an administrator.
            return false;
        }
    }

    /// <summary>
    /// Runs <paramref name="exe"/> elevated and waits for it, returning its exit code.
    ///
    /// <c>UseShellExecute = true</c> with <c>Verb = "runas"</c> is what hands the request to Windows
    /// so it can show its own consent prompt; it is the single place in TradeAgent where shell
    /// execution is correct, and it is why output cannot be redirected here (Windows forbids
    /// combining the two). <c>WindowStyle = Hidden</c> keeps the elevated program itself out of
    /// sight — the consent dialog is Windows', and stays.
    /// </summary>
    /// <param name="refusalCode">
    /// Error code used when the user answers No. Callers pass the one that fits their step; the
    /// default is deliberately neutral because declining a prompt is a choice, not a fault.
    /// </param>
    /// <param name="windowStyle">
    /// Hidden by default, which is right for anything TradeAgent drives itself. A third-party
    /// installer whose unattended switches are not documented has to be allowed to show its own
    /// wizard — hiding a program that is waiting for a click is a hang, not a clean install.
    /// </param>
    public static async Task<int> RunElevatedAsync(
        string exe,
        string args,
        CancellationToken ct = default,
        ErrorCode refusalCode = ErrorCode.UNKNOWN_ERROR,
        ProcessWindowStyle windowStyle = ProcessWindowStyle.Hidden)
    {
        var psi = new ProcessStartInfo(exe)
        {
            Arguments = args,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = windowStyle
        };

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: the user pressed No on the Windows prompt. That is an answer, not a
            // crash, and it deserves a sentence rather than a Win32 error number.
            throw new TradeAgentException(refusalCode,
                "Windows asked for permission to continue and the answer was No. " +
                "Nothing was installed and nothing was changed. You can try again whenever you like.", ex);
        }
        catch (Win32Exception ex)
        {
            throw new TradeAgentException(refusalCode,
                $"Windows would not start {Path.GetFileName(exe)}. Nothing was changed.", ex);
        }

        if (process is null)
            throw new TradeAgentException(refusalCode,
                $"Windows would not start {Path.GetFileName(exe)}. Nothing was changed.");

        using (process)
        {
            await process.WaitForExitAsync(ct);
            return process.ExitCode;
        }
    }
}

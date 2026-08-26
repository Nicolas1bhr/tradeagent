using System.Diagnostics;

namespace TradeAgent.Provisioning;

/// <summary>
/// Opens a web page in the user's own browser without a shell and without a console.
///
/// <c>UseShellExecute = true</c> would be the usual one-liner, but that is the same mechanism that
/// produces a flashing window when the target turns out to be a script or a file association, so the
/// platform's own "open this" helper is invoked directly instead. All three are GUI programs; none
/// of them shows a terminal.
/// </summary>
public static class Browser
{
    public static bool TryOpen(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        var (exe, arg) =
            OperatingSystem.IsWindows() ? ("explorer.exe", uri.AbsoluteUri) :
            OperatingSystem.IsMacOS()   ? ("open", uri.AbsoluteUri) :
                                          ("xdg-open", uri.AbsoluteUri);

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add(arg);

        try
        {
            using var p = Process.Start(psi);
            return p is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

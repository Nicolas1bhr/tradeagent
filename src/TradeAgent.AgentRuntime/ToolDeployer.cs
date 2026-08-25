using TradeAgent.Core;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// Puts the trade CLI where the agent's PATH points. The agent must never be asked to "find" a
/// binary, and the user must never be asked to edit PATH.
/// </summary>
public static class ToolDeployer
{
    public static string TradeCliName => OperatingSystem.IsWindows() ? "trade.exe" : "trade";

    /// <summary>Copies the shipped CLI into the managed bin directory when it is missing or older.</summary>
    public static string? EnsureTradeCli(string? sourceDir = null)
    {
        var src = Path.Combine(sourceDir ?? AppContext.BaseDirectory, TradeCliName);
        var dst = Path.Combine(Paths.Bin, TradeCliName);
        if (!File.Exists(src)) return File.Exists(dst) ? dst : null;

        try
        {
            if (!File.Exists(dst) || File.GetLastWriteTimeUtc(src) > File.GetLastWriteTimeUtc(dst))
            {
                File.Copy(src, dst, overwrite: true);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(dst, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            // A single-file build needs no side-car files; a framework-dependent one does.
            foreach (var extra in new[] { "trade.dll", "trade.runtimeconfig.json", "trade.deps.json" })
            {
                var from = Path.Combine(sourceDir ?? AppContext.BaseDirectory, extra);
                if (File.Exists(from)) File.Copy(from, Path.Combine(Paths.Bin, extra), overwrite: true);
            }
            return dst;
        }
        catch (IOException) { return File.Exists(dst) ? dst : null; }
    }
}

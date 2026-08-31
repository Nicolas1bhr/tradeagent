using System.Text.Json;
using TradeAgent.Core;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// Puts the trade CLI where the agent's PATH points. The agent must never be asked to "find" a
/// binary, and the user must never be asked to edit PATH.
/// </summary>
public static class ToolDeployer
{
    public static string TradeCliName => OperatingSystem.IsWindows() ? "trade.exe" : "trade";

    /// <summary>The launcher's own side-cars. Present for a framework-dependent build, absent for a single-file one.</summary>
    static readonly string[] Launcher = ["trade.dll", "trade.runtimeconfig.json", "trade.deps.json"];

    /// <summary>Copies the shipped CLI into the managed bin directory when it is missing or older.</summary>
    public static string? EnsureTradeCli(string? sourceDir = null)
    {
        var from = sourceDir ?? AppContext.BaseDirectory;
        var src = Path.Combine(from, TradeCliName);
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

            // A single-file build needs no side-car files; a framework-dependent one does — and it
            // needs its REFERENCED ASSEMBLIES too, not only the launcher trio. Copying just the trio
            // produces a trade.exe that starts and immediately throws FileNotFoundException on
            // TradeAgent.Core, which is indistinguishable from a missing CLI to everyone except the
            // agent trying to run it. The packaged build publishes self-contained, so this only ever
            // bit a non-packaged run — which is exactly the configuration the agent path gets tested in.
            foreach (var extra in Launcher.Concat(DependencyAssemblies(from)))
            {
                var f = Path.Combine(from, extra);
                if (File.Exists(f)) File.Copy(f, Path.Combine(Paths.Bin, extra), overwrite: true);
            }
            return dst;
        }
        catch (IOException) { return File.Exists(dst) ? dst : null; }
    }

    /// <summary>
    /// Whether the deployed CLI can actually run, and why not when it cannot.
    ///
    /// Asking only whether the file exists is what let a CLI that throws on every invocation report
    /// READY. The agent's single route to the gateway is this binary; "it is present" is not the
    /// question worth answering about it.
    /// </summary>
    public static bool TradeCliReady(out string reason)
    {
        var exe = Path.Combine(Paths.Bin, TradeCliName);
        if (!File.Exists(exe)) { reason = "the trade command is not installed"; return false; }

        var deps = Path.Combine(Paths.Bin, "trade.deps.json");
        if (!File.Exists(deps)) { reason = ""; return true; }   // single-file build: nothing beside it to need

        var missing = DependencyAssemblies(Paths.Bin)
            .Where(a => !File.Exists(Path.Combine(Paths.Bin, a)))
            .ToList();
        if (missing.Count == 0) { reason = ""; return true; }

        reason = $"the trade command is missing {missing.Count} of the files it needs to start ({string.Join(", ", missing.Take(3))}" +
                 (missing.Count > 3 ? ", …)" : ")");
        return false;
    }

    /// <summary>
    /// The assembly file names the CLI's own deps.json says it loads at runtime. Read from the
    /// manifest rather than hard-coded, so a new package reference cannot silently reintroduce this.
    /// </summary>
    static IEnumerable<string> DependencyAssemblies(string dir)
    {
        var deps = Path.Combine(dir, "trade.deps.json");
        if (!File.Exists(deps)) return [];

        var names = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(deps));
            if (!doc.RootElement.TryGetProperty("targets", out var targets)) return [];
            foreach (var target in targets.EnumerateObject())
                foreach (var library in target.Value.EnumerateObject())
                    if (library.Value.TryGetProperty("runtime", out var runtime))
                        foreach (var file in runtime.EnumerateObject())
                        {
                            var name = file.Name.Replace('\\', '/').Split('/')[^1];
                            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && name != "trade.dll")
                                names.Add(name);
                        }
        }
        catch (JsonException) { return []; }
        return names.Distinct(StringComparer.OrdinalIgnoreCase);
    }
}

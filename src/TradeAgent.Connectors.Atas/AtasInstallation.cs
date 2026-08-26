using System.Diagnostics;
using TradeAgent.Core;

namespace TradeAgent.Connectors.Atas;

/// <summary>
/// Where ATAS lives, and whether it is running. The candidate paths, process names and the
/// indicators folder are DATA, overridable from <c>%LOCALAPPDATA%\TradeAgent\atas.json</c>, for the
/// same reason the runtime manifests are: guessing a vendor's install layout from memory and baking
/// it into a compiled binary produces a product that breaks silently when the vendor moves a folder.
///
/// Every path below was checked on 2026-08-26 against a real ATAS Platform install on Windows 11
/// and against ATAS's own developer documentation, which is why <see cref="Verified"/> now defaults
/// to true. The override file stays: it is what turns the next vendor folder move into a one-line
/// data fix instead of a rebuild.
/// </summary>
public sealed class AtasLayout
{
    /// <summary>
    /// Classic ATAS installs 32-bit-style under Program Files (x86) — confirmed by installing it:
    /// 592 files, 459 MB, at <c>C:\Program Files (x86)\ATAS Platform</c>. The old first entry,
    /// <c>%ProgramFiles%\ATAS Platform</c>, could therefore never match on 64-bit Windows.
    /// ATAS X is the newer cross-platform build and does live under Program Files.
    /// </summary>
    public string[] InstallDirCandidates { get; set; } =
    [
        @"%ProgramFiles(x86)%\ATAS Platform",
        @"%ProgramFiles%\ATAS X"
    ];

    /// <summary>
    /// Where ATAS loads user STRATEGIES from. The bridge is a strategy, so this is the only folder
    /// that can work.
    ///
    /// This list used to also contain the Indicators folder and a Documents-rooted path, and that
    /// was a live defect rather than generous fallback: <see cref="AtasInstallation.Detect"/> takes
    /// the first candidate that exists, so on a machine where the user had added a custom indicator
    /// but never a strategy, the bridge assembly was copied into Indicators. ATAS would then never
    /// list it under strategies, the heartbeat would never arrive, and nothing anywhere would say
    /// why. The Documents path is from a superseded ATAS blog post and appears in no current doc.
    /// </summary>
    public string[] StrategyDirCandidates { get; set; } = [@"%APPDATA%\ATAS\Strategies"];

    /// <summary>
    /// Where ATAS loads INDICATORS from. Kept because it is worth detecting — an existing indicators
    /// folder proves ATAS has been run at least once — but never as a fallback for a strategy.
    /// </summary>
    public string[] IndicatorDirCandidates { get; set; } =
    [
        @"%APPDATA%\ATAS\Indicators",
        @"%APPDATA%\ATAS X\Indicators"
    ];

    /// <summary>Process names as Process.GetProcessesByName wants them: no .exe.</summary>
    public string[] ProcessNames { get; set; } = ["OFT.Platform", "OFT.PlatformX"];

    /// <summary>
    /// ATAS's executables are named after OrderFlowTrading, not after ATAS. There is no ATAS.exe and
    /// no ATAS.Platform.exe on a real install — both were in this list and both were wrong.
    /// </summary>
    public string[] ExecutableNames { get; set; } = ["OFT.Platform.exe", "OFT.PlatformX.exe"];

    /// <summary>True: checked against a real install and against ATAS's developer documentation.</summary>
    public bool Verified { get; set; } = true;

    public static string OverridePath => Path.Combine(Paths.Home, "atas.json");

    public static AtasLayout Load()
    {
        if (!File.Exists(OverridePath)) return new AtasLayout();
        try { return Json.Read<AtasLayout>(File.ReadAllText(OverridePath)) ?? new AtasLayout(); }
        catch (Exception) { return new AtasLayout(); }
    }

    public void Save() => File.WriteAllText(OverridePath, Json.Write(this, pretty: true));
}

public sealed record AtasDetection(bool Installed, string? InstallDir, string? StrategyDir, string? Version,
    bool Running, bool BridgeInstalled, bool LayoutVerified, string? RuntimeTfm = null);

public static class AtasInstallation
{
    public static AtasDetection Detect(AtasLayout? layout = null)
    {
        var l = layout ?? AtasLayout.Load();

        var installDir = l.InstallDirCandidates.Select(Expand).FirstOrDefault(Directory.Exists);
        var strategyDir = l.StrategyDirCandidates.Select(Expand).FirstOrDefault(Directory.Exists);

        // A freshly installed ATAS has no %APPDATA%\ATAS at all — measured on a real machine
        // minutes after installing it. Without this, a perfectly good install reported "could not
        // find the ATAS strategies folder", which is a false negative on the happy path. The folder
        // is only created once ATAS itself has been found, so a machine without ATAS stays untouched.
        if (strategyDir is null && installDir is not null && l.StrategyDirCandidates.Length > 0)
        {
            var wanted = Expand(l.StrategyDirCandidates[0]);
            try { Directory.CreateDirectory(wanted); strategyDir = wanted; }
            catch (Exception) { /* a read-only profile is the user's to fix, not ours to crash on */ }
        }

        string? version = null;
        if (installDir is not null)
        {
            var exe = l.ExecutableNames.Select(n => Path.Combine(installDir, n)).FirstOrDefault(File.Exists);
            if (exe is not null)
            {
                try { version = FileVersionInfo.GetVersionInfo(exe).FileVersion; }
                catch (Exception) { /* version is nice to have, not required */ }
            }
        }

        var running = false;
        foreach (var name in l.ProcessNames)
        {
            try { if (Process.GetProcessesByName(name).Length > 0) { running = true; break; } }
            catch (Exception) { /* platform may not allow process enumeration */ }
        }

        var bridgeInstalled = strategyDir is not null &&
                              File.Exists(Path.Combine(strategyDir, "TradeAgent.AtasBridge.dll"));

        return new AtasDetection(installDir is not null, installDir, strategyDir, version, running,
            bridgeInstalled, l.Verified, RuntimeTfm(installDir));
    }

    static string Expand(string p) => Environment.ExpandEnvironmentVariables(p);

    /// <summary>
    /// Which .NET the platform runs on, read from its own runtimeconfig.
    ///
    /// This matters more than the version number in ATAS's title bar, which ATAS's documentation
    /// explicitly says does not tell you the runtime. A bridge built for the wrong framework is not
    /// rejected with an error — ATAS simply does not load it, and the strategy never appears in the
    /// list. Knowing the target lets the product say so instead of leaving the user to wonder.
    /// </summary>
    public static string? RuntimeTfm(string? installDir)
    {
        if (installDir is null) return null;
        foreach (var name in new[] { "OFT.Platform.runtimeconfig.json", "OFT.PlatformX.runtimeconfig.json" })
        {
            var path = Path.Combine(installDir, name);
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("runtimeOptions", out var opts) &&
                    opts.TryGetProperty("tfm", out var tfm))
                    return tfm.GetString();
            }
            catch (Exception) { /* an unreadable runtimeconfig is not worth failing detection over */ }
        }
        return null;
    }

    /// <summary>
    /// Copies the bridge assembly into the folder ATAS loads from. The user still has to add and
    /// start the strategy once inside ATAS; TradeAgent detects that by heartbeat rather than asking
    /// them to confirm they did it.
    /// </summary>
    public static string InstallBridge(string bridgeSourceDir, AtasLayout? layout = null)
    {
        var d = Detect(layout);
        if (d.StrategyDir is null)
            throw new TradeAgentException(ErrorCode.ATAS_NOT_FOUND, "could not find the ATAS strategies folder");
        if (!Directory.Exists(bridgeSourceDir))
            throw new TradeAgentException(ErrorCode.ATAS_BRIDGE_MISSING, $"bridge files are not in {bridgeSourceDir}");

        var copied = 0;
        foreach (var file in Directory.GetFiles(bridgeSourceDir, "TradeAgent.*"))
        {
            File.Copy(file, Path.Combine(d.StrategyDir, Path.GetFileName(file)), overwrite: true);
            copied++;
        }
        if (copied == 0) throw new TradeAgentException(ErrorCode.ATAS_BRIDGE_MISSING, "no bridge files were found to install");
        return d.StrategyDir;
    }
}

using System.Diagnostics;
using TradeAgent.Core;

namespace TradeAgent.Connectors.Atas;

/// <summary>
/// Where ATAS lives, and whether it is running. The candidate paths, process names and the
/// indicators folder are DATA, overridable from <c>%LOCALAPPDATA%\TradeAgent\atas.json</c>, for the
/// same reason the runtime manifests are: guessing a vendor's install layout from memory and baking
/// it into a compiled binary produces a product that breaks silently when the vendor moves a folder.
///
/// Everything here is marked unverified until it has been checked against a real ATAS install.
/// </summary>
public sealed class AtasLayout
{
    public string[] InstallDirCandidates { get; set; } =
    [
        @"%ProgramFiles%\ATAS Platform",
        @"%ProgramFiles(x86)%\ATAS Platform",
        @"%LOCALAPPDATA%\ATAS Platform",
        @"%APPDATA%\ATAS Platform"
    ];

    /// <summary>Where ATAS loads user strategies/indicators from. The bridge assembly is copied here.</summary>
    public string[] StrategyDirCandidates { get; set; } =
    [
        @"%APPDATA%\ATAS\Strategies",
        @"%APPDATA%\ATAS\Indicators",
        @"%USERPROFILE%\Documents\ATAS\Strategies"
    ];

    public string[] ProcessNames { get; set; } = ["ATAS", "OFT.Platform", "ATAS.Platform"];
    public string[] ExecutableNames { get; set; } = ["ATAS.exe", "OFT.Platform.exe"];

    /// <summary>False until confirmed against a real installation on Windows.</summary>
    public bool Verified { get; set; }

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
    bool Running, bool BridgeInstalled, bool LayoutVerified);

public static class AtasInstallation
{
    public static AtasDetection Detect(AtasLayout? layout = null)
    {
        var l = layout ?? AtasLayout.Load();

        var installDir = l.InstallDirCandidates.Select(Expand).FirstOrDefault(Directory.Exists);
        var strategyDir = l.StrategyDirCandidates.Select(Expand).FirstOrDefault(Directory.Exists);

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

        return new AtasDetection(installDir is not null, installDir, strategyDir, version, running, bridgeInstalled, l.Verified);
    }

    static string Expand(string p) => Environment.ExpandEnvironmentVariables(p);

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

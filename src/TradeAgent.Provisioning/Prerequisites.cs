using System.Diagnostics;
using TradeAgent.Connectors.Atas;
using TradeAgent.Core;

namespace TradeAgent.Provisioning;

/// <summary>
/// Node.js, installed privately for TradeAgent only.
///
/// Present as a prerequisite rather than hidden inside the AI install because a user watching a
/// progress list deserves to see what is happening, and because an AI runtime that ships as a
/// self-contained binary does not need this step at all.
/// </summary>
public sealed class NodePrerequisite : IPrerequisite
{
    public string Id => "node";
    public string Title => "Node.js";

    public string Why =>
        "Some AI assistants are published as Node packages. TradeAgent keeps its own private copy " +
        "inside its own folder, so nothing else on your computer changes.";

    public bool RequiresAdmin => false;

    public Task<bool> IsSatisfiedAsync(CancellationToken ct = default) => Task.FromResult(NodeRuntime.IsInstalled);

    public Task InstallAsync(IProgress<ProvisionProgress>? progress, CancellationToken ct = default) =>
        NodeRuntime.InstallAsync(progress, ct);
}

/// <summary>
/// ATAS, the trading platform TradeAgent places orders through.
///
/// TradeAgent fetches the vendor's own installer and asks Windows to run it, silently.
///
/// ATAS documents no unattended switches, so this was settled by measurement rather than by reading:
/// its setup is Inno Setup 6.4.3, Inno's own <c>/VERYSILENT</c> switches were run against the real
/// installer on a real Windows 11 machine, and the result was checked — see <see cref="SilentArgs"/>.
/// The concern that a silent run would blindly accept an ATAS-specific version-selection page did
/// not survive the test.
///
/// So the whole burden here is <b>one Windows permission prompt</b>. That prompt stays: it is
/// Windows' own, it belongs to software TradeAgent does not own, and a product that could install a
/// 459 MB trading platform with no consent at all would be the more alarming design.
/// </summary>
public sealed class AtasPrerequisite : IPrerequisite
{
    /// <summary>
    /// The vendor's direct installer. Taken from the download button's own <c>href</c> on
    /// https://atas.net/atas-download/ and confirmed on 2026-08-26 to serve 2,318,224 bytes
    /// anonymously, with no redirect and no login. Unversioned by design — it is the vendor's
    /// "latest" path, so there is nothing to pin and no published checksum to check against.
    /// </summary>
    public const string InstallerUrl = "https://atas.net/Setup/ATASPlatform.exe";

    /// <summary>
    /// Inno Setup's own unattended switches. ATAS documents none of its own, so these were confirmed
    /// by running the real installer on a real Windows 11 machine on 2026-08-26: exit code 0, log
    /// line "Installation process succeeded", 592 files and 459 MB under
    /// <c>C:\Program Files (x86)\ATAS Platform</c>, no reboot required, and no window shown. The
    /// version-selection page this was feared to hide took its default without asking.
    /// </summary>
    const string SilentArgs = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";

    /// <summary>Verified reachable on 2026-08-26, and linked as "Download" from https://atas.net/.</summary>
    public const string DownloadPageUrl = "https://atas.net/atas-download/";

    readonly Func<bool> _isInstalled;
    readonly Func<string, bool> _openPage;
    readonly Func<string, string, CancellationToken, Task<int>> _runElevated;

    /// <param name="isInstalled">
    /// Defaults to the one place that already knows where ATAS lives,
    /// <see cref="AtasInstallation.Detect"/>. Injectable so tests — and any future build that wants
    /// provisioning without the ATAS connector — can supply their own answer.
    /// </param>
    /// <param name="openPage">Defaults to opening the page in the user's browser.</param>
    /// <param name="runElevated">Defaults to <see cref="Elevation.RunElevatedAsync"/>.</param>
    public AtasPrerequisite(
        Func<bool>? isInstalled = null,
        Func<string, bool>? openPage = null,
        Func<string, string, CancellationToken, Task<int>>? runElevated = null)
    {
        _isInstalled = isInstalled ?? (() => AtasInstallation.Detect().Installed);
        _openPage = openPage ?? Browser.TryOpen;
        _runElevated = runElevated ?? ((exe, args, ct) => Elevation.RunElevatedAsync(
            exe, args, ct, ErrorCode.ATAS_NOT_FOUND, ProcessWindowStyle.Hidden));
    }

    public string Id => "atas";
    public string Title => "ATAS";

    public string Why =>
        "ATAS is the trading platform TradeAgent sends your orders to. It has to be installed and " +
        "logged in to your broker before any order can be placed.";

    /// <summary>
    /// True. Not verified against ATAS's own documentation — it says nothing about administrator
    /// rights — but its documented install location is Program Files, which no ordinary account can
    /// write to. Planning for the prompt and not needing it costs nothing; the reverse fails.
    /// </summary>
    public bool RequiresAdmin => true;

    public Task<bool> IsSatisfiedAsync(CancellationToken ct = default)
    {
        try { return Task.FromResult(_isInstalled()); }
        catch (Exception) { return Task.FromResult(false); }
    }

    public async Task InstallAsync(IProgress<ProvisionProgress>? progress, CancellationToken ct = default)
    {
        var dir = Path.Combine(Paths.Tools, "atas");
        var installer = Path.Combine(dir, "ATASPlatform.exe");

        try
        {
            progress?.Report(new ProvisionProgress("atas", "Downloading ATAS from atas.net"));
            await Downloader.DownloadAsync(InstallerUrl, installer, progress, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Could not reach the vendor. Hand the user the page rather than a dead end.
            var opened = _openPage(DownloadPageUrl);
            throw new TradeAgentException(ErrorCode.ATAS_NOT_FOUND,
                opened
                    ? $"TradeAgent could not download ATAS ({ex.Message}). The download page is now " +
                      "open in your browser. Install ATAS, then press Retry."
                    : $"TradeAgent could not download ATAS ({ex.Message}). Open {DownloadPageUrl} " +
                      "in your browser, install ATAS, then press Retry.");
        }

        progress?.Report(new ProvisionProgress("atas",
            "Installing ATAS. Windows will ask you for permission — that is the only thing you have to do."));

        var exitCode = await _runElevated(installer, SilentArgs, ct);

        if (_isInstalled())
        {
            progress?.Report(new ProvisionProgress("atas", "ATAS is installed", 1.0));
            return;
        }

        throw new TradeAgentException(ErrorCode.ATAS_NOT_FOUND,
            exitCode == 0
                ? "The ATAS installer finished but TradeAgent still cannot find ATAS on this " +
                  "computer. If you installed it somewhere unusual, press Check everything and " +
                  "TradeAgent will tell you what it looked for."
                : "The ATAS installer did not finish. Nothing was changed. You can press Retry.");
    }
}

/// <summary>
/// The AI assistant itself, expressed as a prerequisite so the setup screen can show one list.
///
/// The work is done by the runtime the caller hands in; this only wraps it, because the AI runtime
/// lives in <c>TradeAgent.AgentRuntime</c>, which is a layer above this one.
/// </summary>
public sealed class DelegatedPrerequisite(
    string id,
    string title,
    string why,
    Func<CancellationToken, Task<bool>> isSatisfied,
    Func<IProgress<ProvisionProgress>?, CancellationToken, Task> install,
    bool requiresAdmin = false) : IPrerequisite
{
    public string Id => id;
    public string Title => title;
    public string Why => why;
    public bool RequiresAdmin => requiresAdmin;

    public Task<bool> IsSatisfiedAsync(CancellationToken ct = default) => isSatisfied(ct);

    public Task InstallAsync(IProgress<ProvisionProgress>? progress, CancellationToken ct = default) =>
        install(progress, ct);
}

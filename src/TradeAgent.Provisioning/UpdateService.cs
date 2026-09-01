using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using TradeAgent.Core;

namespace TradeAgent.Provisioning;

/// <summary>
/// A version this product compares: three numbers and an optional pre-release suffix.
///
/// Deliberately not <see cref="System.Version"/>. That type reads "0.2.0" as a four-part number with
/// an absent Revision, orders "1.0.0-rc1" nowhere at all because it refuses to parse it, and would
/// have to be fed a tag with the leading "v" already stripped by someone. All three of those are how
/// an updater talks itself into offering a downgrade.
/// </summary>
public readonly record struct UpdateVersion(int Major, int Minor, int Patch, string PreRelease)
    : IComparable<UpdateVersion>
{
    static readonly Regex Shape =
        new(@"^[vV]?(\d{1,9})(?:\.(\d{1,9}))?(?:\.(\d{1,9}))?(?:-([0-9A-Za-z.\-]+))?$", RegexOptions.Compiled);

    public static bool TryParse(string? text, out UpdateVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var t = text.Trim();
        // Build metadata takes no part in the ordering, by semver's own rule.
        var plus = t.IndexOf('+');
        if (plus >= 0) t = t[..plus];

        var m = Shape.Match(t);
        if (!m.Success) return false;

        var major = int.Parse(m.Groups[1].Value);
        var minor = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
        var patch = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
        version = new UpdateVersion(major, minor, patch, m.Groups[4].Success ? m.Groups[4].Value : "");
        return true;
    }

    public int CompareTo(UpdateVersion other)
    {
        var c = Major.CompareTo(other.Major); if (c != 0) return c;
        c = Minor.CompareTo(other.Minor); if (c != 0) return c;
        c = Patch.CompareTo(other.Patch); if (c != 0) return c;

        // 1.0.0 is NEWER than 1.0.0-rc1: a finished release outranks any pre-release of itself.
        if (PreRelease.Length == 0 && other.PreRelease.Length == 0) return 0;
        if (PreRelease.Length == 0) return 1;
        if (other.PreRelease.Length == 0) return -1;
        return string.CompareOrdinal(PreRelease, other.PreRelease);
    }

    public override string ToString() =>
        PreRelease.Length == 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";
}

/// <summary>
/// A release that is newer than the running build, and the one file in it we would install.
///
/// <see cref="Notes"/> is text somebody wrote on a web page. It is shown to the user and nothing
/// else: it never reaches the agent, and no field here is ever executed as a command.
/// </summary>
public sealed record UpdateInfo(
    string Version,
    string Tag,
    string AssetName,
    string DownloadUrl,
    long SizeBytes,
    string ReleaseUrl,
    string Notes)
{
    /// <summary>URL of the release's SHA256SUMS.txt, when it published one. Null when it did not.</summary>
    public string? ChecksumUrl { get; init; }

    public string SizeLabel => SizeBytes <= 0 ? "" : $"{SizeBytes / 1024d / 1024d:0.#} MB";
}

/// <summary>What the update machinery is currently doing. The UI renders exactly this.</summary>
public enum UpdateStage
{
    /// <summary>Nothing has been asked yet.</summary>
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,

    /// <summary>The installer has been started and TradeAgent is about to close.</summary>
    Installing,

    /// <summary>The check or the download failed. <see cref="UpdateService.Message"/> says how.</summary>
    Failed
}

/// <summary>
/// Where the update machinery gets its facts and what it does with them. Swapped wholesale in tests
/// so the state machine can be exercised without a network, a download or a running installer.
/// </summary>
public sealed record UpdateSources(
    Func<CancellationToken, Task<string?>> LatestReleaseJson,
    Func<string, CancellationToken, Task<string?>> Text,
    Func<UpdateInfo, string?, IProgress<ProvisionProgress>?, CancellationToken, Task<string>> Download,
    Action<string> Launch)
{
    public static UpdateSources GitHub(string repository) => new(
        ct => Downloader.TryGetStringAsync($"https://api.github.com/repos/{repository}/releases/latest", ct),
        Downloader.TryGetStringAsync,
        async (info, sha, progress, ct) =>
        {
            var dir = Path.Combine(Paths.Updates, info.Version);
            Prune(Paths.Updates, keep: dir);
            return await Downloader.DownloadAsync(info.DownloadUrl, Path.Combine(dir, info.AssetName), progress, ct, sha);
        },
        Install);

    /// <summary>
    /// Starts the installer and returns. TradeAgent closes immediately afterwards, and Inno Setup's
    /// own Restart Manager handling covers the moment in between.
    ///
    /// The switches are the whole no-terminal rule in one line. <c>/SILENT</c> shows Setup's own
    /// progress window and no wizard; there is no cmd.exe and no PowerShell anywhere in this path.
    /// <c>/relaunch=1</c> is read by TradeAgent.iss, which starts the new build once it is in place —
    /// without it a silent install would finish to an empty desktop, because the ordinary [Run] entry
    /// is marked skipifsilent.
    /// </summary>
    static void Install(string installerPath)
    {
        if (!OperatingSystem.IsWindows())
            throw new TradeAgentException(ErrorCode.UPDATE_FAILED,
                "TradeAgent updates itself on Windows only; this build is running somewhere else");

        var psi = new ProcessStartInfo(installerPath) { UseShellExecute = true };
        psi.Arguments = "/SILENT /NORESTART /SUPPRESSMSGBOXES /relaunch=1";
        using var p = Process.Start(psi);
        if (p is null)
            throw new TradeAgentException(ErrorCode.UPDATE_FAILED, $"Windows would not start {installerPath}");
    }

    /// <summary>Yesterday's installer is 90 MB of nothing. Keep the one being fetched, drop the rest.</summary>
    static void Prune(string root, string keep)
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(root))
                if (!string.Equals(dir, keep, StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { /* a stale folder is untidy, not broken */ }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Notices that a newer TradeAgent has been published, and installs it when — and only when — the
/// user says so.
///
/// Three rules, and they are the feature rather than decoration on it:
///
/// <b>It never installs anything on its own.</b> The background check lights a banner. Downloading
/// and installing take a deliberate two-press confirmation, because the thing being replaced is the
/// program holding the user's open orders. An updater that restarts a trading application while the
/// owner is looking elsewhere is not a convenience.
///
/// <b>It is not reachable from the agent.</b> This object lives in the app process beside the other
/// operator authority; nothing on the agent-facing pipe can start a check, a download or an install.
/// An AI that would like a different build of its own supervisor has nowhere to ask.
///
/// <b>It says what the checksum does and does not prove.</b> The release's SHA256SUMS.txt is fetched
/// and enforced, which catches a truncated or corrupted download. It comes from the same release as
/// the installer, so it proves the transfer, NOT the publisher — that is what code signing would be
/// for, and this product does not have a certificate yet.
/// </summary>
public sealed class UpdateService
{
    /// <summary>Where releases come from. Overridable so a staging repo can be tested before a real release exists.</summary>
    public const string DefaultRepository = "Nicolas1bhr/tradeagent";

    /// <summary>The one artifact in a release that is an installable TradeAgent.</summary>
    public const string DefaultAssetPattern = @"^TradeAgent-Setup.*\.exe$";

    static readonly Regex RepositoryShape = new(@"^[A-Za-z0-9._-]{1,64}/[A-Za-z0-9._-]{1,100}$", RegexOptions.Compiled);

    readonly UpdateVersion _current;
    readonly string _assetPattern;
    readonly UpdateSources _sources;
    readonly object _gate = new();

    bool _busy;

    public UpdateService(string currentVersion, string? repository = null, string? assetPattern = null, UpdateSources? sources = null)
    {
        UpdateVersion.TryParse(currentVersion, out _current);
        CurrentVersion = _current.ToString();
        Repository = Resolve(repository);
        _assetPattern = assetPattern ?? Environment.GetEnvironmentVariable("TRADEAGENT_UPDATE_ASSET") ?? DefaultAssetPattern;
        _sources = sources ?? UpdateSources.GitHub(Repository);
    }

    /// <summary>
    /// The repository asked about. <c>TRADEAGENT_UPDATE_REPO</c> overrides it and is validated to be
    /// an owner/repo pair, so a malformed value cannot become part of some other URL. This is not a
    /// privilege boundary and is not pretending to be one: anyone who can set environment variables
    /// for this process can already replace the executable it is running from. It exists so the
    /// updater can be pointed at a staging repository and watched to work, which is the only way
    /// update code is ever tested before the release it is supposed to install.
    /// </summary>
    public string Repository { get; }

    public string CurrentVersion { get; }
    public UpdateStage Stage { get; private set; } = UpdateStage.Idle;
    public UpdateInfo? Available { get; private set; }
    public DateTime? LastCheckedUtc { get; private set; }

    /// <summary>A sentence for the user. Progress while downloading, the reason when something failed.</summary>
    public string? Message { get; private set; }

    /// <summary>Set by "Later". Hides the banner for this run without pretending the update went away.</summary>
    public bool Dismissed { get; private set; }

    /// <summary>True when there is something to offer and the user has not waved it away.</summary>
    public bool ShouldPrompt => Available is not null && !Dismissed;

    public event Action? Changed;

    /// <summary>
    /// Asks GitHub what the newest release is. Never throws: a machine that is offline, behind a
    /// captive portal or rate-limited is a machine that keeps trading, not one that shows an error.
    /// </summary>
    public async Task CheckAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_busy) return;
            _busy = true;
        }
        try
        {
            Set(UpdateStage.Checking, null);
            var json = await _sources.LatestReleaseJson(ct);
            LastCheckedUtc = DateTime.UtcNow;

            if (json is null)
            {
                // One sentence that is true in all three cases this reaches: no network, GitHub's
                // rate limit, and a repository that has published no releases yet (a 404, which is
                // the state of the world until the first release is cut). "Could not reach GitHub"
                // was the obvious wording and it asserts something false in two of the three.
                Set(UpdateStage.Failed,
                    "TradeAgent could not check for a newer version — GitHub did not answer, or nothing has been published yet. Nothing changed.");
                return;
            }

            var found = ReleaseFeed.Parse(json, _current, _assetPattern);
            if (found is null)
            {
                Available = null;
                Set(UpdateStage.UpToDate, null);
                return;
            }

            // A newer release than the one already on offer un-dismisses the banner: the user waved
            // away a different update.
            if (Available?.Version != found.Version) Dismissed = false;
            Available = found;
            Set(UpdateStage.Available, null);
        }
        catch (Exception ex)
        {
            Set(UpdateStage.Failed, ex is TradeAgentException t ? t.Info.UserMessage : ex.Message);
        }
        finally
        {
            lock (_gate) _busy = false;
        }
    }

    /// <summary>
    /// Downloads the installer, checks it against the publisher's checksum, and starts it.
    ///
    /// Returns true when the installer is running, which is the caller's signal to close TradeAgent.
    /// Returns false when nothing was started, and <see cref="Message"/> then says why.
    /// </summary>
    public async Task<bool> InstallAsync(CancellationToken ct = default)
    {
        var info = Available;
        if (info is null) return false;

        lock (_gate)
        {
            if (_busy)
            {
                // A check is running. Saying nothing here would surface as whatever the last message
                // happened to be, which is how a button that is merely early reads as one that broke.
                Message = "TradeAgent is still checking for updates. Press Install update again in a moment.";
                Changed?.Invoke();
                return false;
            }
            _busy = true;
        }
        try
        {
            Set(UpdateStage.Downloading, $"Downloading TradeAgent {info.Version}…");

            string? sha = null;
            if (info.ChecksumUrl is not null)
                sha = ChecksumManifest.Find(await _sources.Text(info.ChecksumUrl, ct), info.AssetName);

            var progress = new Progress<ProvisionProgress>(p => Set(UpdateStage.Downloading, p.Message));
            var installer = await _sources.Download(info, sha, progress, ct);

            Set(UpdateStage.Installing, $"Installing TradeAgent {info.Version}. TradeAgent will close and reopen itself.");
            _sources.Launch(installer);
            return true;
        }
        catch (Exception ex)
        {
            Set(UpdateStage.Failed, ex is TradeAgentException t ? $"{t.Info.UserMessage} {t.Info.Repair}".Trim() : ex.Message);
            return false;
        }
        finally
        {
            lock (_gate) _busy = false;
        }
    }

    /// <summary>"Later". The offer stays in Settings; only the banner goes away.</summary>
    public void Dismiss()
    {
        if (Dismissed) return;
        Dismissed = true;
        Changed?.Invoke();
    }

    void Set(UpdateStage stage, string? message)
    {
        Stage = stage;
        Message = message;
        Changed?.Invoke();
    }

    static string Resolve(string? repository)
    {
        var chosen = repository ?? Environment.GetEnvironmentVariable("TRADEAGENT_UPDATE_REPO");
        return !string.IsNullOrWhiteSpace(chosen) && RepositoryShape.IsMatch(chosen.Trim())
            ? chosen.Trim()
            : DefaultRepository;
    }
}

/// <summary>Reads GitHub's release JSON. Pure, so the awkward cases are testable without a network.</summary>
public static class ReleaseFeed
{
    /// <summary>
    /// The newest release, if it is newer than <paramref name="current"/> AND carries an installer we
    /// could actually run. Null in every other case, including the ones a careless reader would call
    /// success: a draft, a pre-release, an unparseable tag, or a release whose assets did not upload.
    ///
    /// The last one matters most. An update the user cannot install is not an update, and a banner
    /// offering one is a button that fails after the download.
    /// </summary>
    public static UpdateInfo? Parse(string? json, UpdateVersion current, string assetPattern)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (Flag(root, "draft") || Flag(root, "prerelease")) return null;

            var tag = Str(root, "tag_name");
            if (!UpdateVersion.TryParse(tag, out var version)) return null;
            if (version.CompareTo(current) <= 0) return null;

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

            var rx = new Regex(assetPattern, RegexOptions.IgnoreCase);
            string? name = null, url = null, checksums = null;
            long size = 0;

            foreach (var asset in assets.EnumerateArray())
            {
                var assetName = Str(asset, "name");
                var assetUrl = Str(asset, "browser_download_url");
                if (assetName is null || assetUrl is null) continue;

                if (assetName.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) checksums = assetUrl;
                if (name is not null || !rx.IsMatch(assetName)) continue;

                name = assetName;
                url = assetUrl;
                size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
            }

            if (name is null || url is null) return null;

            var notes = Str(root, "body") ?? "";
            if (notes.Length > 4000) notes = notes[..4000];

            return new UpdateInfo(
                version.ToString(), tag!, name, url, size,
                Str(root, "html_url") ?? "", notes.Trim())
            {
                ChecksumUrl = checksums
            };
        }
        catch (Exception)
        {
            // A malformed answer is the same as no answer. It is never an update.
            return null;
        }
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool Flag(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>Reads the SHA256SUMS.txt that packaging/build.ps1 writes beside the installer.</summary>
public static class ChecksumManifest
{
    /// <summary>
    /// The hash recorded for <paramref name="assetName"/>, or null when the manifest does not mention
    /// it. Null means "download without a checksum", not "fail" — a release published before this
    /// file existed is still installable, and a hash we cannot find is not a hash we can enforce.
    ///
    /// Matching is on the file name alone. build.ps1 writes repository-relative paths
    /// (<c>artifacts/TradeAgent-Setup-x64.exe</c>), which is not what the release asset is called.
    /// </summary>
    public static string? Find(string? manifest, string assetName)
    {
        if (string.IsNullOrWhiteSpace(manifest) || string.IsNullOrWhiteSpace(assetName)) return null;

        foreach (var raw in manifest.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var space = line.IndexOf(' ');
            if (space <= 0) continue;

            var hash = line[..space];
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) continue;

            // "  name", " *name" (the binary marker sha256sum writes), or a path to it.
            var named = line[space..].TrimStart(' ', '*');
            var file = named.Replace('\\', '/');
            var slash = file.LastIndexOf('/');
            if (slash >= 0) file = file[(slash + 1)..];

            if (file.Equals(assetName, StringComparison.OrdinalIgnoreCase)) return hash.ToLowerInvariant();
        }
        return null;
    }
}

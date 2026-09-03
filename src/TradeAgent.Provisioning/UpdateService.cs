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
/// <param name="Hash">
/// Reads a file that is already on disk and returns its SHA-256. Separate from <see cref="Download"/>
/// because the file is hashed a second time immediately before it is started, and the two moments
/// are the point: what the download verified and what Windows executes are only the same bytes if
/// nobody wrote to <c>updates\&lt;version&gt;\</c> in between. Null means <see cref="Downloader.Sha256Async"/>,
/// which is what production uses; a caller that supplies nothing therefore gets a real file read
/// rather than a skipped check.
/// </param>
public sealed record UpdateSources(
    Func<CancellationToken, Task<string?>> LatestReleaseJson,
    Func<string, CancellationToken, Task<string?>> Text,
    Func<UpdateInfo, string?, IProgress<ProvisionProgress>?, CancellationToken, Task<string>> Download,
    Action<string> Launch,
    Func<string, CancellationToken, Task<string>>? Hash = null)
{
    public static UpdateSources GitHub(string repository) => new(
        ct => Downloader.TryGetStringAsync($"https://api.github.com/repos/{repository}/releases/latest", ct),
        (url, ct) => Downloader.TryGetSmallTextAsync(url, ChecksumManifest.MaxCharacters, ChecksumManifest.FetchTimeout, ct),
        async (info, sha, progress, ct) =>
        {
            var dir = Path.Combine(Paths.Updates, info.Version);
            Prune(Paths.Updates, keep: dir);
            return await Downloader.DownloadVerifiedAsync(info.DownloadUrl, Path.Combine(dir, info.AssetName), sha, progress, ct);
        },
        Install,
        Downloader.Sha256Async);

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
///
/// <b>And because that checksum is the whole chain, losing it is a refusal rather than a shortcut.</b>
/// There is no signature underneath to fall back on, so every way the hash can go missing — a release
/// that published no manifest, a manifest that cannot be fetched, a manifest that does not name our
/// installer — ends in a sentence the owner can read and nothing being run. So does a release
/// carrying two files that both look like the installer, and so does a file whose bytes changed
/// between being checked and being started. The one thing this object will never do is start a 90 MB
/// executable it cannot account for.
///
/// <b>What that still does not cover:</b> between the hash being read and Windows starting the file
/// there is an instant no check inside this process can close — a program running as this same user
/// could replace the installer in that gap. Same-user isolation is the boundary that would answer
/// it, not another read, and it is somebody else's unit.
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
    bool _launched;
    string? _lastRefusalLogged;

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

    /// <summary>
    /// True when <see cref="Stage"/> is <see cref="UpdateStage.Failed"/> because TradeAgent said no,
    /// rather than because something did not answer.
    ///
    /// The two look identical from outside and read completely differently to the owner: "we could
    /// not ask GitHub" is weather, and "there is a newer version and this one will not be installed"
    /// is a decision with a reason in <see cref="Message"/>.
    /// </summary>
    public bool Refused { get; private set; }

    /// <summary>
    /// True when the standing refusal is the unconfirmed-order one.
    ///
    /// It is the only refusal that stops being true on its own — the order settles and nothing about
    /// the update has changed. Every other one (a release we cannot verify, two files that both look
    /// like the installer, bytes that changed under us) stays true until a different release is
    /// published, so nothing expires it and nothing should.
    /// </summary>
    public bool RefusedPendingWork { get; private set; }

    /// <summary>
    /// True when the release on offer published a checksum file, so it is capable of being verified
    /// at all. False is not a refusal by itself — the refusal is in <see cref="InstallAsync"/> — it
    /// is what lets both surfaces say so BEFORE the owner presses, instead of after.
    /// </summary>
    public bool CanBeVerified => Available?.ChecksumUrl is not null;

    /// <summary>True when there is something to offer and the user has not waved it away.</summary>
    public bool ShouldPrompt => Available is not null && !Dismissed;

    /// <summary>
    /// How many of the owner's orders have an outcome TradeAgent has not established — the gateway's
    /// <c>NeedingReconciliation()</c> count, handed over as a number rather than as the gateway,
    /// because this object has no business reading anything else about trading.
    ///
    /// Null is NOT zero. Null means nobody wired this up, and an updater that cannot see the order
    /// book has no basis for deciding it is safe to replace the program holding it — so it refuses,
    /// exactly as it does when the count is above zero or when asking throws. The alternative
    /// (treat "not wired" as "all clear") is the same defect as putting the check in a view: a guard
    /// that is absent on some route through the code is not a guard.
    /// </summary>
    public Func<int>? UnconfirmedWork { get; set; }

    /// <summary>
    /// Where an install or a refusal is written down for the owner to read afterwards, as
    /// (text, level) — the shape of <c>LogStore.Activity</c>. Null in tests that do not care.
    ///
    /// A refusal nobody can find later is indistinguishable from a button that did nothing.
    /// </summary>
    public Action<string, string>? Activity { get; set; }

    /// <summary>
    /// True from the moment an install is confirmed until it is refused, fails, or Setup is running.
    ///
    /// The gateway reads this and refuses to dispatch new orders while it is set — the other half of
    /// the unconfirmed-order rule. `InstallAsync` refuses to replace the program while an order is
    /// outstanding; this refuses to start an order while the program is being replaced. Without both
    /// the window is only narrowed, not closed: an order placed after the check and before Launch
    /// would be dispatched by a process that is about to be overwritten.
    ///
    /// It stays set after a successful Launch. That is deliberately wider than "until Launch
    /// returns": Setup is running, this process is closing, and there is no version of the next few
    /// seconds in which starting an order is a good idea.
    ///
    /// <b>The exact point it goes up is after the checksum manifest has been fetched AND resolved,
    /// immediately before the installer download begins</b> — not on entry to
    /// <see cref="InstallAsync"/>. It suspends the owner's trading, so it covers only the span they
    /// confirmed: first byte of the installer to Setup running. A network round trip that can stall
    /// is not that span, and holding it across one turned a stranger's slow web server into an
    /// outage of this product's whole purpose.
    /// </summary>
    public bool InstallInProgress { get; private set; }

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

            var found = ReleaseFeed.Parse(json, _current, _assetPattern, out var problem);
            if (found is null)
            {
                Available = null;

                // Two different nothings. "No release is newer than yours" is up-to-date; "there is
                // a newer release and TradeAgent will not touch it" is a refusal, and rendering the
                // second one as the first is how a wall in front of the owner becomes invisible.
                if (problem is null) Set(UpdateStage.UpToDate, null);
                else Refuse(problem, repeatable: true);
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
    /// Downloads the installer, checks it against the publisher's checksum, checks it again the
    /// instant before starting it, and starts it.
    ///
    /// Returns true when the installer is running, which is the caller's signal to close TradeAgent.
    /// Returns false when nothing was started, and <see cref="Message"/> then says why — in a
    /// sentence written for the owner, not a code.
    ///
    /// <b>Every hard stop is here rather than in a view.</b> There are two Install buttons (the
    /// banner and the Settings card) and both press this one method, so a check that lives on either
    /// button is a check the other button walks around; a check on a button is also only as fresh as
    /// the last five-second refresh that set <c>IsEnabled</c>. The buttons keep their cosmetics. The
    /// decisions are these:
    ///
    /// <list type="number">
    /// <item>Nothing is installed while an order's outcome is unknown — nor while we cannot tell.</item>
    /// <item>Nothing is downloaded until a published hash for this exact file has been resolved.</item>
    /// <item>Nothing is started until the bytes on disk are re-read and still match that hash.</item>
    /// </list>
    /// </summary>
    public async Task<bool> InstallAsync(CancellationToken ct = default)
    {
        var info = Available;
        if (info is null) return false;

        // Setup is already running from an earlier press. Two installers of the same product racing
        // to replace the same files is worse than either of them, and the second one is never what
        // the owner meant by pressing again.
        if (_launched)
        {
            Message = $"TradeAgent {info.Version} is already installing. TradeAgent is about to close and reopen itself.";
            Changed?.Invoke();
            return false;
        }

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
            // Before anything is fetched: is there an order whose outcome nobody knows? This is the
            // one stop that is about the owner's money rather than about the file, and it is the
            // cheapest to answer, so it is asked first. Inside the busy gate on purpose — a refusal
            // that a concurrent background check could overwrite two seconds later is not visible.
            if (OutstandingWork(out var outstanding)) return Refuse(outstanding, pendingWork: true);

            Set(UpdateStage.Downloading, $"Downloading TradeAgent {info.Version}…");

            // The checksum is resolved BEFORE the download, not alongside it. A hash that cannot be
            // resolved used to be passed to Downloader as null, where the verification step is
            // simply skipped; there is no signature underneath to catch that, so this is the whole
            // trust chain and it is not optional. Nothing here can hand Downloader a null.
            var sha = await ResolveChecksumAsync(info, ct);
            if (sha is null) return false;   // ResolveChecksumAsync has already said why

            // THE LATCH GOES UP HERE, AND NOT ONE LINE EARLIER.
            //
            // It stops the owner's trading, so it may only cover the span they actually confirmed:
            // from the first byte of the installer to Setup running. Everything above this is a
            // network round trip that can stall, and holding it there meant a slow or oversized
            // manifest could stop all trading while it waited — a self-inflicted outage triggered by
            // a stranger's web server. Above this line nothing irreversible has begun and a refusal
            // costs nothing; below it, an order dispatched now is one that this process will not be
            // alive to reconcile.
            InstallInProgress = true;
            Changed?.Invoke();

            var progress = new Progress<ProvisionProgress>(p => Set(UpdateStage.Downloading, p.Message));
            var installer = await _sources.Download(info, sha, progress, ct);

            // And again, on the file that is about to be executed. The download verified bytes as
            // they arrived; between then and now they have been renamed into updates\<version>\ and
            // left on a disk any process running as this user can write to. A fast path that skips
            // the download because the file is already there does not get to skip this.
            var hash = _sources.Hash ?? Downloader.Sha256Async;
            var actual = await hash(installer, ct);
            if (!string.Equals(actual, sha, StringComparison.OrdinalIgnoreCase))
                return Refuse(
                    $"The downloaded TradeAgent {info.Version} changed after it was checked, so it was not started. " +
                    "Nothing was installed and the version you are running is untouched.");

            // And the hard stop again, for the same reason as the hash. The first ask happened
            // before a manifest fetch and a 90 MB download; an order placed while that was running
            // can have gone UNKNOWN since, and the sample taken minutes ago would launch anyway.
            // ADDED, not moved: the early ask is what keeps a refusal from costing the owner the
            // download, and this one is what makes the answer true at the moment it is acted on.
            if (OutstandingWork(out var late)) return Refuse(late, pendingWork: true);

            Set(UpdateStage.Installing, $"Installing TradeAgent {info.Version}. TradeAgent will close and reopen itself.");
            _sources.Launch(installer);

            // PAST THIS LINE THE OUTCOME IS "LAUNCHED", AND NOTHING BELOW CAN UNDO IT.
            //
            // Setup is running. It is going to replace the files this process is executing from,
            // whatever happens next in here. So the latch goes up first — a second press must not be
            // able to start a second installer over the first — and only then do we try to write it
            // down. A logging failure that returned false would report a success as a failure, keep
            // the caller from shutting down cleanly for Setup, and re-arm the button: three
            // consequences, none of which is worth a log line.
            _launched = true;

            // After Launch, not before: until Setup is actually running there is nothing to record,
            // and "you installed it" beside the exception saying Windows would not start it is a log
            // that argues with itself. The caller closes TradeAgent only once this returns true, so
            // this write completes first.
            try
            {
                Activity?.Invoke(
                    $"You installed TradeAgent {info.Version} over {CurrentVersion} — TradeAgent is closing so Setup can replace it",
                    "info");
            }
            catch (Exception) { /* the installer is already running; there is nothing to fail back to */ }

            return true;
        }
        catch (Exception ex)
        {
            var why = ex is TradeAgentException t ? $"{t.Info.UserMessage} {t.Info.Repair}".Trim() : ex.Message;
            Set(UpdateStage.Failed, why);
            Activity?.Invoke($"TradeAgent {info.Version} was not installed: {why}", "warn");
            return false;
        }
        finally
        {
            lock (_gate)
            {
                _busy = false;
                // Down again unless Setup is actually running, in which case it stays up until this
                // process ends — which is imminent and is the point.
                InstallInProgress = _launched;
            }
        }
    }

    /// <summary>
    /// The hash this release published for this exact file, or null with <see cref="Message"/>
    /// already set to the reason.
    ///
    /// Every null return here used to be a silent install of an unverified 90 MB executable: no
    /// manifest in the release, a manifest that would not download, and a manifest whose lines do
    /// not name our installer (a byte-order mark, a tab instead of the two spaces, an asset renamed
    /// between packaging and publishing, a truncated hash, an empty body). A manifest that exists
    /// and does not name our file is evidence of a mismatch, not of an old release.
    /// </summary>
    async Task<string?> ResolveChecksumAsync(UpdateInfo info, CancellationToken ct)
    {
        var cannot = $"TradeAgent {info.Version} cannot be verified";

        if (info.ChecksumUrl is null)
        {
            Refuse($"{cannot}: it was published without the checksum file that proves the download is the one " +
                   "we released. Nothing was installed.");
            return null;
        }

        var manifest = await _sources.Text(info.ChecksumUrl, ct);
        if (string.IsNullOrWhiteSpace(manifest))
        {
            Refuse($"{cannot}: the checksum file published with it could not be read. Nothing was installed — " +
                   "check your internet connection and press Install update again.");
            return null;
        }

        var sha = ChecksumManifest.Find(manifest, info.AssetName, out var bad);
        if (sha is null)
        {
            Refuse($"{cannot}: {bad ?? $"the checksum file published with it does not list {info.AssetName}"}. " +
                   "Nothing was installed.");
            return null;
        }

        return sha;
    }

    /// <summary>
    /// True when there is trading work whose outcome TradeAgent cannot account for — including the
    /// case where it cannot find out. <paramref name="reason"/> is then the sentence to show.
    /// </summary>
    bool OutstandingWork(out string reason)
    {
        const string cannotTell =
            "TradeAgent cannot tell whether any of your orders are still unconfirmed, so it will not replace " +
            "itself right now. Close and reopen TradeAgent, then try again.";

        if (UnconfirmedWork is null) { reason = cannotTell; return true; }

        int count;
        try { count = UnconfirmedWork(); }
        catch (Exception) { reason = cannotTell; return true; }

        // A negative count is not zero. Nothing should produce one, which is exactly why it must not
        // be read as "all clear" — it means the thing being counted is not what we think it is.
        if (count < 0) { reason = cannotTell; return true; }
        if (count == 0) { reason = ""; return false; }

        reason =
            $"TradeAgent will not replace itself while {(count == 1 ? "an order's outcome is" : $"{count} orders' outcomes are")} " +
            "still unconfirmed — that is the one moment an update could lose track of real money. Settle or reconcile " +
            $"{(count == 1 ? "it" : "them")} on the Dashboard, then install.";
        return true;
    }

    /// <summary>
    /// Says no, in a sentence, on both surfaces and in the log. Always returns false.
    ///
    /// The same refusal twice in a row is written down once. The automatic check runs every six
    /// hours whether anyone is looking or not, and a release that stays un-installable would
    /// otherwise write the identical line into the activity log four times a day until it is fixed.
    /// The first occurrence is always recorded; a different refusal always is too.
    /// </summary>
    /// <param name="repeatable">
    /// True only for the automatic six-hourly check, whose refusal is the same sentence every time
    /// until the release changes and would otherwise write the identical line four times a day. A
    /// PRESS is never deduplicated: two presses are two decisions by the owner, and a log that
    /// silently collapses them cannot answer "did I press it again?".
    /// </param>
    bool Refuse(string reason, bool pendingWork = false, bool repeatable = false)
    {
        // The flags are set BEFORE Changed fires. Set() raises it, and a handler that read Refused
        // during that call would have seen the refusal as an ordinary failure — the one distinction
        // the Settings card and the banner exist to draw.
        Stage = UpdateStage.Failed;
        Message = reason;
        Refused = true;
        RefusedPendingWork = pendingWork;
        Changed?.Invoke();

        if (!repeatable || _lastRefusalLogged != reason)
        {
            _lastRefusalLogged = reason;
            Activity?.Invoke(reason, "warn");
        }
        return false;
    }

    /// <summary>"Later". The offer stays in Settings; only the banner goes away.</summary>
    public void Dismiss()
    {
        if (Dismissed) return;
        Dismissed = true;
        Changed?.Invoke();
    }

    /// <summary>
    /// Drops a standing refusal that has stopped being true.
    ///
    /// Called on the refresh tick. Only the unconfirmed-order refusal can go stale, and it goes
    /// stale the moment the order settles — leaving "TradeAgent will not replace itself…" on screen
    /// beside a button that has just been re-enabled is the banner arguing with its own button, and
    /// with nothing to expire it that lasted until the next check, up to six hours later.
    /// </summary>
    public void ExpireStaleRefusal()
    {
        if (!Refused || !RefusedPendingWork) return;
        if (OutstandingWork(out _)) return;              // still true, leave it alone

        _lastRefusalLogged = null;
        Set(Available is null ? UpdateStage.UpToDate : UpdateStage.Available, null);
    }

    void Set(UpdateStage stage, string? message)
    {
        Stage = stage;
        Message = message;
        Refused = false;
        RefusedPendingWork = false;
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
    public static UpdateInfo? Parse(string? json, UpdateVersion current, string assetPattern) =>
        Parse(json, current, assetPattern, out _);

    /// <summary>
    /// The same, and says which kind of nothing it is returning.
    ///
    /// <paramref name="problem"/> is null for every ordinary not-an-update: a draft, a pre-release, a
    /// tag that is not a version, a release no newer than the running build, a malformed answer, a
    /// release whose installer never uploaded. Those are all correctly reported to the owner as "you
    /// have the newest one".
    ///
    /// It is a sentence when the release IS newer and we are refusing to touch it anyway, which the
    /// owner has to be told — reporting a refusal as "up to date" is a wall they cannot see.
    /// </summary>
    public static UpdateInfo? Parse(string? json, UpdateVersion current, string assetPattern, out string? problem)
    {
        problem = null;
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
            var matches = 0;

            foreach (var asset in assets.EnumerateArray())
            {
                var assetName = Str(asset, "name");
                var assetUrl = Str(asset, "browser_download_url");
                if (assetName is null || assetUrl is null) continue;

                if (assetName.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) checksums = assetUrl;
                if (!rx.IsMatch(assetName)) continue;

                matches++;
                if (name is not null) continue;

                name = assetName;
                url = assetUrl;
                size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
            }

            if (name is null || url is null) return null;

            // The name comes off a web page and becomes a path: Path.Combine(updates\<version>\, name).
            // Nothing downstream is obliged to notice that "TradeAgent-Setup/../../../Startup/x.exe"
            // matches the installer pattern — .* matches a slash — so it is refused here, where the
            // release is turned into an offer, rather than relied upon to trip over the basename
            // compare in ChecksumManifest.Find by accident.
            if (!IsPlainFileName(name))
            {
                problem = $"TradeAgent {version} cannot be installed: the release names its installer in a way " +
                          "TradeAgent will not treat as a file name. Nothing was downloaded.";
                return null;
            }

            // Exactly one, or none. Two files that both look like the installer is a real release —
            // an arm64 build published beside the x64 one would do it, and so would a leftover
            // TradeAgent-Setup-x64.exe.bak under a looser TRADEAGENT_UPDATE_ASSET pattern. Which of
            // them replaces the program holding the owner's open orders is not a question the order
            // of a JSON array gets to answer, and there is no version of "pick one" that is a
            // decision somebody made.
            if (matches > 1)
            {
                problem =
                    $"TradeAgent {version} cannot be installed: the release contains {matches} files that each look " +
                    "like the installer, and TradeAgent will not guess which one to run. Nothing was downloaded.";
                return null;
            }

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

    /// <summary>
    /// A bare file name and nothing else: no directory separator, no drive, no <c>..</c>, no control
    /// characters, and short enough to be a real name. Spelled out rather than delegated to
    /// <c>Path.GetInvalidFileNameChars</c>, which answers differently on macOS and Windows — the
    /// machine that decides must not be the machine the check happens to run on.
    /// </summary>
    static bool IsPlainFileName(string name)
    {
        if (name.Length is 0 or > 200) return false;
        if (name.Contains("..")) return false;
        if (name is "." or "..") return false;

        foreach (var c in name)
        {
            if (c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|') return false;
            if (char.IsControl(c)) return false;
        }
        return true;
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
    /// The hash recorded for <paramref name="assetName"/>, or null when the manifest does not name
    /// it.
    ///
    /// <b>Null means the install is refused</b> — it used to mean "download without a checksum",
    /// which made the product's only integrity check optional in five ordinary accidents (a
    /// byte-order mark, a tab where the two spaces should be, an asset renamed between packaging
    /// and publishing, a truncated hash, an empty file). The caller is
    /// <see cref="UpdateService.InstallAsync"/> and it treats a manifest that exists and does not
    /// name our installer as evidence of a mismatch, so this method stays deliberately strict:
    /// widening it to accept manifests our own packaging never writes would be widening what we
    /// will run.
    ///
    /// What it IS tolerant of is everything a correct manifest can legitimately look like: CRLF
    /// endings, the <c>*name</c> binary marker sha256sum writes, junk lines around the real one,
    /// a case-different file name, and build.ps1's repository-relative paths
    /// (<c>artifacts/TradeAgent-Setup-x64.exe</c>), which are not what the release asset is called.
    /// </summary>
    /// <summary>
    /// A manifest larger than this did not come from our packaging and is not going to be read.
    ///
    /// The real file is two lines and about 120 characters. 64 KiB is five hundred times that and
    /// still small enough that reading it costs nothing; a release would have to publish several
    /// hundred artifacts to approach it. ASCII is what build.ps1 writes, so one character is one byte
    /// here.
    ///
    /// This number is enforced in TWO places and only one of them is this class. A 500 MB "checksum
    /// file" has to be refused while it is ARRIVING — <see cref="Downloader.TryGetSmallTextAsync"/>
    /// stops after this many bytes plus one — because a limit applied to a string that has already
    /// been buffered is a limit on nothing. The check here catches what reaches us by any other
    /// route, and is what keeps <c>Split</c> from allocating a line per line of it.
    /// </summary>
    public const int MaxCharacters = 64 * 1024;

    /// <summary>
    /// How long the checksum manifest gets to arrive: <b>thirty seconds</b>.
    ///
    /// It is at most 64 KiB from the same host that just answered the release query, so thirty
    /// seconds is already generous by two orders of magnitude. The number matters because the
    /// alternative was this client's default of thirty MINUTES, and the owner is standing in front
    /// of a button they just pressed twice.
    /// </summary>
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The same bound, on lines, for a file that is small but pathologically shaped.</summary>
    public const int MaxLines = 2_000;

    public static string? Find(string? manifest, string assetName) => Find(manifest, assetName, out _);

    /// <summary>
    /// The same, and says why when the answer is "no hash" for a reason worse than absence: a
    /// manifest too big to be ours, or one that names our installer twice with two different hashes.
    ///
    /// A file listed twice with the SAME hash resolves normally. build.ps1 hashes
    /// <c>Get-ChildItem -Recurse</c>, so one installer can legitimately appear under two paths, and
    /// two identical hashes carry no contradiction — there is nothing to disambiguate. Two
    /// DIFFERENT hashes for one name is the manifest contradicting itself, and picking either one
    /// (the first, as this used to, or the last) is choosing which of two claims about an executable
    /// to believe. Neither is a decision anybody made.
    /// </summary>
    public static string? Find(string? manifest, string assetName, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(manifest) || string.IsNullOrWhiteSpace(assetName)) return null;

        // Before the split, not after: Split allocates one string per line of whatever arrived.
        if (manifest.Length > MaxCharacters)
        {
            problem = "the checksum file published with it is far larger than one of ours could be";
            return null;
        }

        var lines = 1;
        foreach (var c in manifest)
        {
            if (c != '\n') continue;
            if (++lines > MaxLines)
            {
                problem = "the checksum file published with it has far more lines than one of ours could have";
                return null;
            }
        }

        string? found = null;

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

            if (!file.Equals(assetName, StringComparison.OrdinalIgnoreCase)) continue;

            var candidate = hash.ToLowerInvariant();
            if (found is null) { found = candidate; continue; }
            if (found == candidate) continue;

            problem = $"the checksum file published with it lists {assetName} twice, with two different hashes";
            return null;
        }

        // Every line is read even after a match, so a contradiction later in the file is found.
        return found;
    }
}

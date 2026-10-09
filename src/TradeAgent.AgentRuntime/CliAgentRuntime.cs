using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using TradeAgent.Core;
using TradeAgent.Provisioning;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// One generic implementation driven by a <see cref="RuntimeManifest"/>. OpenCode, Codex and any
/// future CLI are the same code with different data — which is what keeps runtime-specific hacks
/// from leaking into the rest of TradeAgent.
///
/// Every process started here is started with <c>UseShellExecute = false</c> and
/// <c>CreateNoWindow = true</c>, with its output captured. There is no path through this file that
/// puts a terminal in front of the user.
/// </summary>
/// <param name="selectedModel">
/// The model the OWNER chose, or null for the manifest's default. A function rather than a value
/// because it lives in the settings and can change while this runtime is alive.
/// </param>
/// <param name="presence">
/// The register every process this runtime starts reports to: its conversations' turns, the sign-in,
/// the probes that count as agent work and a manifest's background process. Null is the process-wide
/// one and is what the product passes; a test that starts the AI through the app's own start path
/// passes its own, for the reason <see cref="Presence"/> gives.
/// </param>
public sealed class CliAgentRuntime(RuntimeManifest manifest, Func<string?>? selectedModel = null,
    Func<string, string?>? attemptId = null, Func<string?>? launchRefusal = null,
    AgentPresence? presence = null) : IAgentRuntime
{
    ContainedProcess? _session;
    ContainedProcess? _login;
    AgentSession? _conversation;
    bool _started;
    string _workspace = Paths.Workspace;
    Dictionary<string, string> _env = new();

    public RuntimeManifest Manifest => manifest;
    public string Id => manifest.Id;
    public string DisplayName => manifest.DisplayName;

    /// <inheritdoc />
    public string? RequestedModel
    {
        get
        {
            try { return manifest.ModelFor(selectedModel?.Invoke()); }
            // A settings read that threw must cost the CHOICE, not the turn: the runtime falls back
            // to whatever its own configuration says, exactly as it did before this existed.
            catch (Exception) { return null; }
        }
    }

    /// <inheritdoc />
    public string? ModelFor(string? chosen)
    {
        try { return manifest.ModelFor(chosen); }
        catch (Exception) { return null; }
    }

    public RuntimeCapabilities Capabilities => new(
        CanInstallItself: manifest.Install.Kind is not (InstallKind.None or InstallKind.Manual),
        BrowserAuth: manifest.AuthArgs.Length > 0,
        CanRunHeadlessTask: manifest.ExecArgs.Length > 0 || manifest.TaskArgs.Length > 0,
        SelfContained: manifest.SelfContained);

    /// <summary>
    /// Managed copy first, PATH second. A TradeAgent-owned binary beats whatever is on the machine.
    ///
    /// Four things make this longer than it looks. The manifest may name the exact path inside the
    /// archive it came from. Unpacked archives usually nest the program one folder down
    /// (<c>bin/codex.exe</c>). An npm install hides the program in <c>node_modules/.bin</c>. And on
    /// Windows an npm-installed CLI puts a <c>.cmd</c> shim on PATH rather than the name in the
    /// manifest, so matching only the exact filename meant a machine with the tool installed and
    /// signed in still reported it missing.
    /// </summary>
    public string? ResolveExecutable()
    {
        if (string.IsNullOrWhiteSpace(manifest.Executable)) return null;

        // An absolute path in the manifest is taken as given: that is how an override pins a
        // vendor's real binary when the shim on PATH is not runnable.
        if (Path.IsPathRooted(manifest.Executable))
            return File.Exists(manifest.Executable) ? manifest.Executable : null;

        var home = Path.Combine(Paths.Tools, manifest.Id);

        // The exact location the install plan said it would be.
        if (manifest.Install.ExecutableInArchive is { Length: > 0 } inside)
        {
            var declared = Path.Combine(home, inside.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(declared)) return declared;
        }

        foreach (var dir in ManagedDirectories(home))
            foreach (var name in NameCandidates())
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in NameCandidates())
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { /* a malformed PATH entry is not our problem */ }
            }
        }
        return null;
    }

    /// <summary>The install directory, its npm bin, and one level of subdirectory inside it.</summary>
    static IEnumerable<string> ManagedDirectories(string home)
    {
        yield return home;
        yield return Path.Combine(home, "node_modules", ".bin");

        string[] children;
        try { children = Directory.Exists(home) ? Directory.GetDirectories(home) : []; }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }

        foreach (var child in children)
        {
            var name = Path.GetFileName(child);
            if (name is "node_modules" or ".download") continue;
            yield return child;
            yield return Path.Combine(child, "node_modules", ".bin");
        }
    }

    /// <summary>The manifest name first, then the same stem under every executable extension.</summary>
    IEnumerable<string> NameCandidates()
    {
        yield return manifest.Executable;
        if (!OperatingSystem.IsWindows()) yield break;

        var stem = Path.GetFileNameWithoutExtension(manifest.Executable);
        if (string.IsNullOrEmpty(stem)) yield break;

        var pathext = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
        foreach (var ext in pathext.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = stem + ext.Trim().ToLowerInvariant();
            // .ps1 needs a host process, which is more than a launcher should assume.
            if (name.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(name, manifest.Executable, StringComparison.OrdinalIgnoreCase)) yield return name;
        }
    }

    /// <summary>
    /// A .cmd or .bat is a script, not an image: CreateProcess refuses it. Route those through the
    /// command interpreter so an npm shim behaves like any other executable.
    /// </summary>
    internal static void SetCommand(ProcessStartInfo psi, string exe, IEnumerable<string> args)
    {
        var isScript = OperatingSystem.IsWindows() &&
                       (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                        exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));
        if (isScript)
        {
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(exe);
        }
        else psi.FileName = exe;

        foreach (var a in args) if (!string.IsNullOrEmpty(a)) psi.ArgumentList.Add(a);
    }

    /// <summary>
    /// Marks an agent process alive in <see cref="AgentPresence"/> for as long as it runs, so the
    /// material scanner can tell an inbox file the OWNER dropped from one that appeared while the
    /// agent was executing (REVIEW 2026-09-05b finding 5).
    ///
    /// The returned handle also closes the window when it is disposed, which is how the short runs
    /// end it; the <c>Exited</c> hook is for the two long-lived processes nobody awaits. Closing it
    /// twice is harmless and closing it late only widens the window, never narrows it — the failure
    /// this must not have is a window that closes early.
    /// </summary>
    /// <param name="presence">
    /// Whose register to report to. Defaults to the process-wide one, which is the only value the
    /// product ever passes; a test that starts a child in the agent's role passes its own, exactly
    /// as <c>MaterialScanner</c> already takes the question rather than reaching for the singleton.
    /// The register is deliberately sticky — once an agent has been alive in this process, no scan
    /// can attest a window that began before it — so a test that used the shared one would change
    /// what every other scan in that process concludes, for the rest of the run.
    /// </param>
    internal static IDisposable Presence(Process process, AgentPresence? presence = null)
    {
        var window = (presence ?? AgentPresence.Shared).Enter();
        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => window.Dispose();
            if (process.HasExited) window.Dispose();
        }
        catch (InvalidOperationException) { window.Dispose(); }   // already gone, and already reaped
        return window;
    }

    /// <summary>
    /// INSTALLED MEANS IT RAN, not that a file with the right name is on the disk.
    ///
    /// The file existing was the whole test until 2026-09-20, and here is what that cost: on a Mac
    /// whose .NET is not in the default location, the app's own launcher — which every agent process
    /// is relaunched through — exited 131 with "You must install .NET to run this application" before
    /// the vendor CLI ran. `Installed` was true because <see cref="ResolveExecutable"/> had found the
    /// CLI, so setup walked past "Installing the AI assistant" and then sat on "Sign in to your AI
    /// account" for as long as anybody watched, with the CLI already signed in — and the launcher's
    /// complaint, which says exactly what is wrong, was never shown to anybody.
    ///
    /// So a runtime that would not run is NOT installed, and it carries the first line the failing
    /// program printed as its <see cref="RuntimeDetection.Reason"/>. That is the launcher's own
    /// message when it is the launcher that failed, and the vendor's own when it is the vendor's.
    /// </summary>
    public async Task<RuntimeDetection> DetectAsync(CancellationToken ct = default)
    {
        var exe = ResolveExecutable();
        if (exe is null)
            return new RuntimeDetection(false, null, null, false,
                $"{manifest.DisplayName} is not on this computer");

        var managed = exe.StartsWith(Paths.Tools, StringComparison.Ordinal);
        var (version, refused) = await ProbeVersionAsync(exe, ct);
        return version is null
            ? new RuntimeDetection(false, exe, null, managed, refused)
            : new RuntimeDetection(true, exe, version, managed);
    }

    public async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        var exe = ResolveExecutable();
        if (exe is null) return null;
        return (await ProbeVersionAsync(exe, ct)).Version;
    }

    /// <summary>
    /// WHERE THIS RUNTIME ASKS WHICH RELEASE IS NEWEST. The product never sets it, and it is GitHub's API; a test
    /// points it at a loopback stand-in so an install plan is proven against the vendor's own asset names without
    /// a request leaving the machine.
    /// </summary>
    internal string ReleaseApi { get; init; } = Downloader.GitHubApi;

    /// <summary>How long the version probe waits for the program's answer — what the product uses.</summary>
    internal static readonly TimeSpan DefaultVersionDeadline = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long THIS runtime's version probe waits. The product never sets it; a test that needs a
    /// program slower than the deadline sets it short rather than sleeping twenty seconds.
    /// </summary>
    internal TimeSpan VersionDeadline { get; init; } = DefaultVersionDeadline;

    /// <summary>
    /// The version probe, and what the program said instead when there is no version to report.
    ///
    /// A VERSION IS A VERSION ONLY FROM AN EXIT-0 RUN. It used to be enough for the program to print
    /// something on stdout, whatever its exit code, which turns a program that failed loudly into a
    /// program that is installed and has a strange version number.
    ///
    /// <para><b>A program that did not answer in time is a probe that failed, and is said as one.</b>
    /// The deadline used to leave here as <see cref="Run"/>'s <c>AI_AUTH_TIMEOUT</c>, and so out of
    /// <see cref="DetectAsync"/> and every caller that reads a detection. On windows-latest (run
    /// 37017805967) that refused a restart's start — PowerShell's first launch beside the full suite
    /// took longer than 20 s to say its version — with "Signing in took too long and was cancelled.
    /// Press Sign in again." on the card, on a start that signs nothing in; the AI stayed stopped until
    /// somebody pressed. A probe that fails is a runtime that is not installed, found where it is, with
    /// the reason: the start goes on as it always has for a failed probe, and the runtime's row and the
    /// Doctor say why. A cancellation the CALLER asked for is not a slow program, and still ends it.</para>
    /// </summary>
    async Task<(string? Version, string? Refused)> ProbeVersionAsync(string exe, CancellationToken ct)
    {
        ProcResult r;
        try { r = await Run(exe, manifest.VersionArgs, VersionDeadline, ct, agentWork: false); }
        catch (TradeAgentException ex) when (ex.Code == ErrorCode.AI_AUTH_TIMEOUT && !ct.IsCancellationRequested)
        {
            var seconds = VersionDeadline.TotalSeconds;
            return (null, $"{Path.GetFileName(exe)} did not answer within {seconds:0} second{(seconds == 1 ? "" : "s")} " +
                "when asked for its version");
        }

        if (r.ExitCode != 0)
            return (null, FirstLine(r.StdErr) ?? FirstLine(r.StdOut)
                ?? $"{Path.GetFileName(exe)} exited {r.ExitCode} without saying why");

        var text = string.IsNullOrWhiteSpace(r.StdOut) ? r.StdErr : r.StdOut;
        var m = Regex.Match(text, @"\d+\.\d+(\.\d+)?");
        return (m.Success ? m.Value : text.Trim().Split('\n').FirstOrDefault()?.Trim(), null);
    }

    /// <summary>
    /// The first thing the program said, with the terminal renderer's colour codes taken off — the
    /// same treatment the auth probe gives, and for the same reason: these CLIs write through a
    /// renderer even when nobody is looking at a terminal.
    /// </summary>
    static string? FirstLine(string text) =>
        Ansi.Strip(text).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.Length > 0);

    // ---- installation --------------------------------------------------------------------------

    /// <summary>
    /// Installs the runtime into TradeAgent's own tools folder. Per-user, no administrator prompt,
    /// no change to the machine's PATH, no window.
    ///
    /// The download route asks the vendor's release API which build is newest and falls back to a
    /// pinned URL when that cannot be reached, so being offline for the version lookup does not
    /// become being unable to install. If the archive route fails outright and the manifest declares
    /// an npm package, that is tried next through TradeAgent's own private Node.
    /// </summary>
    public async Task<RuntimeDetection> InstallAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var target = Path.Combine(Paths.Tools, manifest.Id);
        Directory.CreateDirectory(target);
        var relay = Relay(progress);

        switch (manifest.Install.Kind)
        {
            case InstallKind.Download:
                await InstallByDownloadAsync(target, progress, relay, ct);
                break;

            case InstallKind.Npm:
                await InstallByNpmAsync(target, progress, relay, ct);
                break;

            case InstallKind.Winget:
                progress?.Report($"Installing {manifest.DisplayName}...");
                var wg = await Run("winget", ["install", "--id", manifest.Install.WingetId ?? "", "--silent",
                    "--accept-package-agreements", "--accept-source-agreements"], TimeSpan.FromMinutes(15), ct,
                    agentWork: false);
                if (wg.ExitCode != 0)
                    throw new TradeAgentException(ErrorCode.AI_INSTALL_FAILED, $"winget failed: {wg.StdErr}");
                break;

            // NO DOWNLOAD PAGE IN THE REFUSAL. "See <address>" is the owner being sent to install it himself,
            // which is the terminal in other words (CLAUDE.md); what TradeAgent cannot install here, it says so.
            case InstallKind.Manual:
            case InstallKind.None:
                throw new TradeAgentException(ErrorCode.AI_INSTALL_FAILED,
                    $"{manifest.DisplayName} is not something TradeAgent can install on this computer.");
        }

        progress?.Report($"Checking {manifest.DisplayName} runs");
        var detected = await DetectAsync(ct);
        if (!detected.Installed)
            // THE PROGRAM'S OWN WORDS WHERE THERE ARE ANY. "Could not be found afterwards" is the
            // wrong sentence for a file that is right there and will not start, and it sends the
            // owner looking for a download they already have.
            throw new TradeAgentException(ErrorCode.AI_INSTALL_FAILED, detected.Path is null
                ? $"{manifest.DisplayName} was installed but the program could not be found afterwards"
                : $"{manifest.DisplayName} was installed but would not run: {detected.Reason}");

        progress?.Report($"{manifest.DisplayName} {detected.Version} is ready");
        return detected;
    }

    async Task InstallByDownloadAsync(string target, IProgress<string>? progress, IProgress<ProvisionProgress> relay, CancellationToken ct)
    {
        var plan = manifest.Install;
        string? url = null;

        if (plan.GitHubRepo is { Length: > 0 } repo && plan.AssetPattern is { Length: > 0 } pattern)
        {
            progress?.Report($"Looking up the newest version of {manifest.DisplayName}");
            url = await Downloader.ResolveGitHubAssetAsync(repo, pattern, ct, ReleaseApi);
            if (url is null)
                progress?.Report("Could not reach the release list — using the version TradeAgent shipped with");
        }

        if (url is null && plan.Url is { Length: > 0 } pinned)
        {
            var tag = plan.GitHubRepo is { Length: > 0 } r ? await Downloader.ResolveGitHubTagAsync(r, ct, ReleaseApi) : null;
            url = pinned.Replace("{version}", tag ?? "");
        }

        if (url is null)
        {
            if (plan.NpmPackage is { Length: > 0 } && NodeRuntime.CanProvide)
            {
                progress?.Report($"No download is available for {manifest.DisplayName} — installing it as a package instead");
                await InstallByNpmAsync(target, progress, relay, ct);
                return;
            }
            throw new TradeAgentException(ErrorCode.AI_INSTALL_FAILED,
                $"there is no download address for {manifest.DisplayName} in its manifest");
        }

        try
        {
            progress?.Report($"Downloading {manifest.DisplayName}");
            await Downloader.DownloadAndUnpackAsync(url, target,
                Integrity.PinnedOr(plan.Sha256,
                    $"{manifest.DisplayName}'s manifest pins no checksum for the file it downloads"),
                relay, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (plan.NpmPackage is { Length: > 0 } && NodeRuntime.CanProvide)
        {
            // Declared fallback: the vendor also publishes this as an npm package, and TradeAgent
            // has its own Node, so a broken or moved archive is not the end of the road.
            progress?.Report($"The download did not work ({ex.Message}). Trying the package version instead.");
            await InstallByNpmAsync(target, progress, relay, ct);
        }
        catch (Exception ex) when (plan.NpmPackage is { Length: > 0 })
        {
            // OFF WINDOWS THERE IS NO SECOND ROAD, and the refusal is the ARCHIVE'S. TradeAgent's private Node is
            // a Windows zip, so the package fallback could only end in Node's own refusal — which used to tell the
            // owner to install Node.js himself. What failed is the download, and that is what is said.
            throw new TradeAgentException(ErrorCode.AI_INSTALL_FAILED,
                $"{manifest.DisplayName} could not be downloaded: {ex.Message.TrimEnd('.')}. TradeAgent has no other " +
                "way to install it on this computer.", ex);
        }
    }

    async Task InstallByNpmAsync(string target, IProgress<string>? progress, IProgress<ProvisionProgress> relay, CancellationToken ct)
    {
        var package = manifest.Install.NpmPackage;
        if (string.IsNullOrWhiteSpace(package))
            throw new TradeAgentException(ErrorCode.AI_INSTALL_FAILED,
                $"there is no package name for {manifest.DisplayName} in its manifest");

        // Never a bare `npm` from PATH: the machine may not have one, the one it has may be a
        // different major version, and on Windows it is a .cmd shim that needs a command
        // interpreter. TradeAgent uses the npm that came with the Node it installed itself.
        if (!NodeRuntime.IsInstalled)
            progress?.Report("Installing Node.js first — TradeAgent keeps its own private copy");

        await NodeRuntime.InstallPackageAsync(package, target, relay, ct);
    }

    public Task<RuntimeDetection> UpdateAsync(IProgress<string>? progress = null, CancellationToken ct = default) =>
        InstallAsync(progress, ct);

    // ---- sign-in -------------------------------------------------------------------------------

    /// <summary>
    /// Starts the runtime's sign-in headless and reads the URL out of what it prints.
    ///
    /// The old version started this with a visible console and left the user staring at a terminal
    /// that TradeAgent exists to hide. Now the login process runs with its output captured, the
    /// manifest's <see cref="RuntimeManifest.AuthUrlPattern"/> pulls the address out, and the app
    /// opens the browser. The process is left running on purpose: these commands host a local
    /// callback listener and must stay alive until the browser round-trip completes.
    /// </summary>
    public async Task<AuthChallenge> BeginAuthenticationAsync(CancellationToken ct = default)
    {
        var exe = ResolveExecutable() ?? throw new TradeAgentException(ErrorCode.AI_RUNTIME_NOT_FOUND);
        if (manifest.AuthArgs.Length == 0)
            throw new TradeAgentException(ErrorCode.AI_AUTH_REQUIRED,
                $"{manifest.DisplayName} has no sign-in command in its manifest");

        StopLogin();

        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _workspace,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        SetCommand(psi, exe, manifest.AuthArgs);
        AgentEnvironment.Apply(psi, _env, manifest.KeepEnvironment);

        var contained = ProcessContainment.Start(psi);
        var process = contained.Process;
        _login = contained;
        Presence(process, presence);

        var transcript = new StringBuilder();
        var urlFound = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var codeFound = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pattern = manifest.AuthUrlPattern is { Length: > 0 } p ? new Regex(p) : null;
        var codePattern = CodePattern();

        _ = PumpAsync(process.StandardOutput, transcript, pattern, urlFound, codePattern, codeFound);
        _ = PumpAsync(process.StandardError, transcript, pattern, urlFound, codePattern, codeFound);

        // A SIGN-IN WITH A CODE IS READY WHEN BOTH ARE: the link comes a line before the code, and returning at
        // the link handed the screen a challenge whose code had not been read yet.
        Task ready = codePattern is null ? urlFound.Task : Task.WhenAll(urlFound.Task, codeFound.Task);
        var exited = process.WaitForExitAsync(ct);
        var timeout = Task.Delay(TimeSpan.FromSeconds(30), ct);
        await Task.WhenAny(ready, exited, timeout);

        string text;
        lock (transcript) text = transcript.ToString();

        if (urlFound.Task.IsCompletedSuccessfully)
        {
            var url = urlFound.Task.Result;
            var message = string.IsNullOrWhiteSpace(manifest.SignInDescription)
                ? "Finish signing in in the browser window that just opened, then come back here."
                : manifest.SignInDescription;
            return new AuthChallenge(url, codeFound.Task.IsCompletedSuccessfully ? codeFound.Task.Result : ExtractCode(text), message);
        }

        if (exited.IsCompleted)
        {
            // Finished without ever printing a URL. Usually "you are already signed in"; sometimes a
            // refusal. Either way the user gets the program's own words rather than an exit code.
            var summary = FirstMeaningfulLine(text);
            return new AuthChallenge(null, ExtractCode(text),
                summary ?? $"{manifest.DisplayName} finished its sign-in without opening a browser.");
        }

        return new AuthChallenge(null, ExtractCode(text),
            $"{manifest.DisplayName} is signing in but has not given TradeAgent a web address to open. " +
            "If a browser window does not appear shortly, press Sign in again.");
    }

    static async Task PumpAsync(StreamReader reader, StringBuilder transcript, Regex? pattern, TaskCompletionSource<string> found,
        Regex? codePattern, TaskCompletionSource<string> codeFound)
    {
        try
        {
            string? raw;
            while ((raw = await reader.ReadLineAsync()) is not null)
            {
                // These tools colour their output. The escape bytes sit right up against the URL,
                // so they have to come off before anything is matched.
                var line = Ansi.Strip(raw);
                lock (transcript) transcript.AppendLine(line);
                if (pattern is not null && !found.Task.IsCompleted && pattern.Match(line) is { Success: true } m)
                    found.TrySetResult(TrimUrl(m.Groups.Count > 1 ? m.Groups[1].Value : m.Value));
                if (codePattern is not null && !codeFound.Task.IsCompleted && codePattern.Match(line) is { Success: true } c)
                    codeFound.TrySetResult(c.Groups.Count > 1 ? c.Groups[1].Value : c.Value);
            }
        }
        catch (Exception) { /* the stream closing is how this ends */ }
    }

    /// <summary>Drops sentence punctuation that a printed URL picked up from the sentence around it.</summary>
    static string TrimUrl(string url) => url.TrimEnd('.', ',', ';', ':', ')', ']', '"', '\'');

    /// <summary>The manifest's code pattern, or null for a sign-in that shows no code — or names one that does not compile.</summary>
    Regex? CodePattern()
    {
        try { return manifest.AuthCodePattern is { Length: > 0 } p ? new Regex(p) : null; }
        catch (ArgumentException) { return null; }
    }

    /// <summary>
    /// The one-time code in what the sign-in printed, by the MANIFEST'S pattern — the vendor's shape, measured
    /// (<see cref="RuntimeManifest.AuthCodePattern"/>) — or null where it declares none. A generic
    /// <c>XXXX-XXXX</c> used to stand in for every vendor, and found nothing in Codex's four-and-five.
    /// </summary>
    string? ExtractCode(string text)
    {
        if (CodePattern() is not { } pattern) return null;
        var m = pattern.Match(text);
        return !m.Success ? null : m.Groups.Count > 1 ? m.Groups[1].Value : m.Value;
    }

    static string? FirstMeaningfulLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0) return t;
        }
        return null;
    }

    void StopLogin()
    {
        try { if (_login is { Process.HasExited: false }) _login.Kill(); }
        catch (Exception) { /* already gone */ }
        _login?.Dispose();
        _login = null;
    }

    /// <summary>Storing a key is a local operation. Anything slower than this has stopped working.</summary>
    static readonly TimeSpan KeySignInTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Signs in with a key the user pasted into TradeAgent's own window.
    ///
    /// Two shapes, both from the manifest: hand the key to the CLI on stdin, or write the
    /// credentials file the CLI reads. Either way TradeAgent never shows a terminal and never asks
    /// anyone to type a key into one. The key is not logged, not stored by TradeAgent, and not kept
    /// in memory beyond this call.
    /// </summary>
    public Task SignInWithApiKeyAsync(string key, CancellationToken ct = default) =>
        SignInWithApiKeyAsync(key, ct, beforeRename: null);

    /// <summary>See <see cref="SignInWithApiKeyAsync(string, CancellationToken)"/>.</summary>
    /// <param name="beforeRename">
    /// A test seam, and the product passes nothing: for the credentials-file shape, handed the path of the
    /// file the key sits in before it is published at the manifest's path — the instant at which "who can
    /// read this" is the question, and the last one at which a failure must leave the old file whole.
    /// </param>
    internal async Task SignInWithApiKeyAsync(string key, CancellationToken ct, Action<string>? beforeRename)
    {
        var plan = manifest.ApiKey
            ?? throw new TradeAgentException(ErrorCode.AI_AUTH_REQUIRED,
                $"{manifest.DisplayName} does not accept a key this way");

        key = key.Trim();
        if (key.Length == 0)
            throw new TradeAgentException(ErrorCode.AI_AUTH_REQUIRED, "no key was entered");

        if (plan.StdinArgs.Length > 0)
        {
            var exe = ResolveExecutable() ?? throw new TradeAgentException(ErrorCode.AI_RUNTIME_NOT_FOUND);
            var psi = new ProcessStartInfo
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _workspace
            };
            SetCommand(psi, exe, plan.StdinArgs);
            AgentEnvironment.Apply(psi, _env, manifest.KeepEnvironment);

            // THE WINDOW BEFORE THE PROCESS, so they are disposed the other way round: the process — and
            // with it the whole tree — first, then the window. Never closed by the leader's exit alone, which
            // says nothing about what it started (U-agent-tree).
            using var alive = (presence ?? AgentPresence.Shared).Enter();
            using var held = ProcessContainment.Start(psi);
            var p = held.Process;
            await p.StandardInput.WriteLineAsync(key);
            p.StandardInput.Close();

            // Both pipes are drained concurrently, and under a deadline — the same shape Run() uses,
            // and for the same reason. Reading stderr to end *first* deadlocks the moment the child
            // writes more to stdout than the pipe buffer holds: the child blocks writing, so it never
            // exits, so it never closes stderr, so this never returns. Measured on macOS with a
            // stand-in that reads the key exactly as codex does: 64 KB of stdout returned in 0.2s,
            // 128 KB never returned at all.
            //
            // Latent today — codex 0.150.0 prints nothing on stdout for `login --with-api-key` — and
            // it stays latent only until a vendor makes that command chatty. The symptom would be the
            // sign-in spinner turning forever with no error, which is trap 1 in docs/RESUME-HERE.md
            // reappearing on the one path that handles the user's credential.
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timer.CancelAfter(KeySignInTimeout);

            var stdout = p.StandardOutput.ReadToEndAsync(timer.Token);
            var stderr = p.StandardError.ReadToEndAsync(timer.Token);
            string outp, err;
            try
            {
                await p.WaitForExitAsync(timer.Token);
                outp = await stdout;
                err = await stderr;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // THE WHOLE TREE, through the one teardown every agent process ends by — a parent-link walk
                // stops one level above anything that detached (U-agent-tree).
                held.Kill();
                throw new TradeAgentException(ErrorCode.AI_AUTH_TIMEOUT,
                    $"{Path.GetFileName(exe)} did not accept the key within {KeySignInTimeout.TotalSeconds:0} seconds");
            }

            if (p.ExitCode != 0)
                throw new TradeAgentException(ErrorCode.AI_AUTH_REQUIRED,
                    Summarise(string.IsNullOrWhiteSpace(err) ? outp : err));
            return;
        }

        if (plan.File is { Length: > 0 } && plan.FileTemplate is { Length: > 0 } template)
        {
            var path = ExpandHome(plan.File);
            // JSON-escape so a key containing a quote or backslash cannot corrupt the file.
            var escaped = System.Text.Json.JsonEncodedText.Encode(key).ToString();
            // THE ONE PUBLISH EVERY CREDENTIAL TAKES (U-credential-replace), with the bytes the in-place
            // File.WriteAllTextAsync wrote — the template, UTF-8, no byte-order mark — and still the whole
            // file, as it always was. That write truncated first, so a failure between the truncation and
            // the write left the owner's working key an empty file, and off Windows it created the file with
            // no mode of its own, 0644 under the usual umask: the key readable by every account. On the pool,
            // not the caller's thread — the window's, when the owner presses the button — because the flush to
            // the device is a wait the window should not make.
            var bytes = Encoding.UTF8.GetBytes(template.Replace("{key}", escaped));
            await Task.Run(() => OwnerOnlyFile.Write(path, bytes, beforeRename), ct);
            return;
        }

        throw new TradeAgentException(ErrorCode.AI_AUTH_REQUIRED,
            $"{manifest.DisplayName}'s key sign-in is not configured");
    }

    static string ExpandHome(string path)
    {
        if (path.StartsWith("~/", StringComparison.Ordinal))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        return Environment.ExpandEnvironmentVariables(path);
    }

    /// <summary>First non-empty line, so a wall of CLI output becomes one sentence a person can read.</summary>
    static string Summarise(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.Length > 0) ?? "the sign-in did not succeed";

    public async Task<AuthState> GetAuthenticationStateAsync(CancellationToken ct = default)
    {
        var exe = ResolveExecutable();
        if (exe is null) return AuthState.Unknown;
        if (manifest.AuthStateArgs.Length == 0) return AuthState.Unknown;

        var r = await Run(exe, manifest.AuthStateArgs, TimeSpan.FromSeconds(30), ct, agentWork: false);
        // Both runtimes print this through a terminal renderer, so the answer arrives wrapped in
        // colour codes that a pattern like "3 credentials" would never match.
        var text = Ansi.Strip(r.StdOut + "\n" + r.StdErr);
        if (manifest.AuthStateSuccessPattern is { } pattern)
            return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase) ? AuthState.Authenticated : AuthState.NotAuthenticated;
        return r.ExitCode == 0 ? AuthState.Authenticated : AuthState.NotAuthenticated;
    }

    // ---- lifecycle -----------------------------------------------------------------------------

    public Task CreateEnvironmentAsync(string workspace, IReadOnlyDictionary<string, string> env, CancellationToken ct = default)
    {
        _workspace = workspace;
        _env = new Dictionary<string, string>(env);
        Directory.CreateDirectory(workspace);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The conversation the app hosts. One per runtime, created on first use, reading the workspace
    /// and environment through functions so a later <see cref="CreateEnvironmentAsync"/> is picked up
    /// rather than silently ignored.
    /// </summary>
    public IAgentConversation OpenConversation() =>
        _conversation ??= new AgentSession(manifest, ResolveExecutable, () => _workspace, () => _env,
            presence: presence, model: () => RequestedModel, role: CouncilRoles.Operations,
            attempt: () => attemptId?.Invoke(CouncilRoles.Operations), launchRefusal: launchRefusal);

    /// <summary>
    /// One conversation per council role, made once and kept. The chair gets the window's own
    /// conversation — the one the Chat page draws — so nothing about the owner's view changes; every
    /// other role gets a session of its own, in its own folder, on its own model.
    /// </summary>
    readonly Dictionary<string, IAgentConversation> _roleConversations = [];

    public IAgentConversation OpenConversation(string role, Func<string> workspace,
        Func<IReadOnlyDictionary<string, string>> environment, Func<string?> model)
    {
        if (role == CouncilRoles.Default) return OpenConversation();
        lock (_roleConversations)
        {
            if (_roleConversations.TryGetValue(role, out var existing)) return existing;
            // The caller hands over the OWNER'S choice for this role and the manifest resolves it,
            // exactly as it does for the single conversation above. A caller resolving it itself
            // would have to know which runtime is prepared, which is the one thing this interface
            // exists to keep out of the app.
            var session = new AgentSession(manifest, ResolveExecutable, workspace, environment,
                presence: presence, model: () => ModelFor(model()), role: role,
                attempt: () => attemptId?.Invoke(role), launchRefusal: launchRefusal);
            _roleConversations[role] = session;
            return session;
        }
    }

    /// <summary>
    /// Makes the agent ready to talk to.
    ///
    /// There is no console any more. The agent used to be started as an interactive program in its
    /// own window, and that window was the product's chat interface; now the window is TradeAgent's
    /// and each message is a separate headless run. So this verifies the runtime is present, opens
    /// the conversation, and starts a background process only if a manifest explicitly asks for one
    /// — in which case it is still started with no window and its output captured.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        var exe = ResolveExecutable() ?? throw new TradeAgentException(ErrorCode.AI_RUNTIME_NOT_FOUND);

        // The same refusal the turn obeys, on the one other path that starts a vendor process the
        // agent's work runs in. Checked here rather than inside Run(): Run also issues this class's
        // own fixed probes — a version string, an auth state, an installer — and refusing those
        // would leave the owner unable to see WHY the AI will not start.
        if (launchRefusal?.Invoke() is { Length: > 0 } refusal)
            throw new TradeAgentException(ErrorCode.CONTAINMENT_REQUIRED, refusal);

        if (manifest.InteractiveArgs.Length > 0 && _session is not { Process.HasExited: false })
        {
            var psi = new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = _workspace,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            SetCommand(psi, exe, manifest.InteractiveArgs);
            AgentEnvironment.Apply(psi, _env, manifest.KeepEnvironment);
            _session = ProcessContainment.Start(psi);
            Presence(_session.Process, presence);
        }

        var conversation = OpenConversation();
        await conversation.StartAsync(ct);
        _started = true;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        _started = false;
        if (_conversation is not null) await _conversation.StopAsync();
        // Every role's session too. One left running is an agent process this runtime believes it
        // has stopped, and the inbox attestation is measured over exactly that belief.
        List<IAgentConversation> roles;
        lock (_roleConversations) roles = [.. _roleConversations.Values];
        foreach (var c in roles) await c.StopAsync();
        StopLogin();
        try { if (_session is { Process.HasExited: false }) _session.Kill(); }
        catch (Exception) { /* already gone */ }
        _session?.Dispose();
        _session = null;
    }

    public async Task RestartAsync(CancellationToken ct = default)
    {
        await StopAsync(ct);
        await StartAsync(ct);
    }

    public async Task<string> ExecuteTaskAsync(string prompt, CancellationToken ct = default)
    {
        var exe = ResolveExecutable() ?? throw new TradeAgentException(ErrorCode.AI_RUNTIME_NOT_FOUND);
        var template = manifest.TaskArgs.Length > 0 ? manifest.TaskArgs : manifest.ExecArgs;
        var args = AgentArgs.Build(template, prompt, jsonFlag: null, manifest.UnattendedArgs);
        var r = await Run(exe, args, TimeSpan.FromMinutes(15), ct);
        return string.IsNullOrWhiteSpace(r.StdOut) ? r.StdErr : r.StdOut;
    }

    public async Task<HealthState> GetHealthAsync(CancellationToken ct = default)
    {
        var exe = ResolveExecutable();
        if (exe is null) return HealthState.FAILED;
        if (_started || _session is { Process.HasExited: false }) return HealthState.READY;
        var v = await GetVersionAsync(ct);
        return v is null ? HealthState.DEGRADED : HealthState.READY;
    }

    public sealed record ProcResult(int ExitCode, string StdOut, string StdErr);

    /// <summary>
    /// Runs a child process with a hard timeout and captured output. Never inherits a console.
    ///
    /// stdin is redirected and closed immediately. Every CLI here is capable of reading stdin when it
    /// is available — Codex announces "Reading additional input from stdin..." even when the prompt
    /// was passed as an argument — and TradeAgent is a window with no console, so an inherited stdin
    /// handle never reaches end-of-file. The child then waits forever and the timeout below is the
    /// only thing that ends it. Giving it end-of-file at once turns a hang into an answer.
    /// </summary>
    /// <param name="agentWork">
    /// True when the arguments carry something the AGENT chose — a prompt, a task — so the run
    /// counts as an agent process for <see cref="AgentPresence"/>. False only for the fixed probes
    /// this class issues on its own account (a version string, an auth state, an installer), whose
    /// arguments come from the manifest and can carry nothing of the agent's. It defaults to true:
    /// a run nobody classified is one nobody can attest around.
    /// </param>
    public async Task<ProcResult> Run(string exe, IEnumerable<string> args, TimeSpan timeout,
        CancellationToken ct = default, bool agentWork = true)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _workspace
        };
        SetCommand(psi, exe, args);
        AgentEnvironment.Apply(psi, _env, manifest.KeepEnvironment);

        // The window before the process, so the tree's teardown is disposed first and the window after it —
        // the same order as the key sign-in above and every turn (U-agent-tree).
        using var alive = agentWork ? (presence ?? AgentPresence.Shared).Enter() : null;
        using var held = ProcessContainment.Start(psi);
        var p = held.Process;
        try { p.StandardInput.Close(); } catch (Exception) { /* already gone */ }
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(timeout);

        var stdout = p.StandardOutput.ReadToEndAsync(timer.Token);
        var stderr = p.StandardError.ReadToEndAsync(timer.Token);
        try
        {
            await p.WaitForExitAsync(timer.Token);
            return new ProcResult(p.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            // The whole tree, as the key sign-in above and every turn end theirs (U-agent-tree).
            held.Kill();
            throw new TradeAgentException(ErrorCode.AI_AUTH_TIMEOUT, $"{Path.GetFileName(exe)} did not finish within {timeout.TotalSeconds:0}s");
        }
    }

    /// <summary>
    /// Bridges provisioning's structured progress to the plain strings the rest of the app takes.
    /// Synchronous on purpose: <c>Progress&lt;T&gt;</c> posts to a captured context, which reorders
    /// messages in a progress list.
    /// </summary>
    static IProgress<ProvisionProgress> Relay(IProgress<string>? progress) => new ProgressRelay(progress);

    sealed class ProgressRelay(IProgress<string>? inner) : IProgress<ProvisionProgress>
    {
        public void Report(ProvisionProgress value) => inner?.Report(value.Message);
    }
}

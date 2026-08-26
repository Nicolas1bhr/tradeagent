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
public sealed class CliAgentRuntime(RuntimeManifest manifest) : IAgentRuntime
{
    Process? _session;
    Process? _login;
    AgentSession? _conversation;
    bool _started;
    string _workspace = Paths.Workspace;
    Dictionary<string, string> _env = new();

    public RuntimeManifest Manifest => manifest;
    public string Id => manifest.Id;
    public string DisplayName => manifest.DisplayName;

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

    public async Task<RuntimeDetection> DetectAsync(CancellationToken ct = default)
    {
        var exe = ResolveExecutable();
        if (exe is null) return new RuntimeDetection(false, null, null, false);
        var version = await GetVersionAsync(ct);
        return new RuntimeDetection(true, exe, version, exe.StartsWith(Paths.Tools, StringComparison.Ordinal));
    }

    public async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        var exe = ResolveExecutable();
        if (exe is null) return null;
        var r = await Run(exe, manifest.VersionArgs, TimeSpan.FromSeconds(20), ct);
        if (r.ExitCode != 0 && string.IsNullOrWhiteSpace(r.StdOut)) return null;
        var text = string.IsNullOrWhiteSpace(r.StdOut) ? r.StdErr : r.StdOut;
        var m = Regex.Match(text, @"\d+\.\d+(\.\d+)?");
        return m.Success ? m.Value : text.Trim().Split('\n').FirstOrDefault()?.Trim();
    }

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
                    "--accept-package-agreements", "--accept-source-agreements"], TimeSpan.FromMinutes(15), ct);
                if (wg.ExitCode != 0)
                    throw new TradeAgentException(ErrorCode.AI_INSTALL_FAILED, $"winget failed: {wg.StdErr}");
                break;

            case InstallKind.Manual:
            case InstallKind.None:
                throw new TradeAgentException(ErrorCode.AI_INSTALL_FAILED,
                    $"{manifest.DisplayName} cannot be installed automatically yet. See {manifest.Install.ManualUrl ?? manifest.DocsUrl}");
        }

        progress?.Report($"Checking {manifest.DisplayName} runs");
        var detected = await DetectAsync(ct);
        if (!detected.Installed)
            throw new TradeAgentException(ErrorCode.AI_INSTALL_FAILED,
                $"{manifest.DisplayName} was installed but the program could not be found afterwards");

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
            url = await Downloader.ResolveGitHubAssetAsync(repo, pattern, ct);
            if (url is null)
                progress?.Report("Could not reach the release list — using the version TradeAgent shipped with");
        }

        if (url is null && plan.Url is { Length: > 0 } pinned)
        {
            var tag = plan.GitHubRepo is { Length: > 0 } r ? await Downloader.ResolveGitHubTagAsync(r, ct) : null;
            url = pinned.Replace("{version}", tag ?? "");
        }

        if (url is null)
        {
            if (plan.NpmPackage is { Length: > 0 })
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
            await Downloader.DownloadAndUnpackAsync(url, target, relay, ct, plan.Sha256);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (plan.NpmPackage is { Length: > 0 })
        {
            // Declared fallback: the vendor also publishes this as an npm package, and TradeAgent
            // has its own Node, so a broken or moved archive is not the end of the road.
            progress?.Report($"The download did not work ({ex.Message}). Trying the package version instead.");
            await InstallByNpmAsync(target, progress, relay, ct);
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
        foreach (var (k, v) in _env) psi.Environment[k] = v;

        var process = Process.Start(psi)
            ?? throw new TradeAgentException(ErrorCode.AI_AUTH_FAILED, $"{manifest.DisplayName} would not start its sign-in");
        _login = process;

        var transcript = new StringBuilder();
        var urlFound = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pattern = manifest.AuthUrlPattern is { Length: > 0 } p ? new Regex(p) : null;

        _ = PumpAsync(process.StandardOutput, transcript, pattern, urlFound);
        _ = PumpAsync(process.StandardError, transcript, pattern, urlFound);

        var exited = process.WaitForExitAsync(ct);
        var timeout = Task.Delay(TimeSpan.FromSeconds(30), ct);
        await Task.WhenAny(urlFound.Task, exited, timeout);

        string text;
        lock (transcript) text = transcript.ToString();

        if (urlFound.Task.IsCompletedSuccessfully)
        {
            var url = urlFound.Task.Result;
            var message = string.IsNullOrWhiteSpace(manifest.SignInDescription)
                ? "Finish signing in in the browser window that just opened, then come back here."
                : manifest.SignInDescription;
            return new AuthChallenge(url, ExtractCode(text), message);
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

    static async Task PumpAsync(StreamReader reader, StringBuilder transcript, Regex? pattern, TaskCompletionSource<string> found)
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
                if (pattern is null || found.Task.IsCompleted) continue;
                var m = pattern.Match(line);
                if (m.Success) found.TrySetResult(TrimUrl(m.Groups.Count > 1 ? m.Groups[1].Value : m.Value));
            }
        }
        catch (Exception) { /* the stream closing is how this ends */ }
    }

    /// <summary>Drops sentence punctuation that a printed URL picked up from the sentence around it.</summary>
    static string TrimUrl(string url) => url.TrimEnd('.', ',', ';', ':', ')', ']', '"', '\'');

    /// <summary>A device code, when the runtime uses one. Shape is the near-universal XXXX-XXXX.</summary>
    static string? ExtractCode(string text)
    {
        var m = Regex.Match(text, @"\b([A-Z0-9]{4}-[A-Z0-9]{4})\b");
        return m.Success ? m.Groups[1].Value : null;
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
        try { if (_login is { HasExited: false }) _login.Kill(entireProcessTree: true); }
        catch (Exception) { /* already gone */ }
        _login?.Dispose();
        _login = null;
    }

    /// <summary>
    /// Signs in with a key the user pasted into TradeAgent's own window.
    ///
    /// Two shapes, both from the manifest: hand the key to the CLI on stdin, or write the
    /// credentials file the CLI reads. Either way TradeAgent never shows a terminal and never asks
    /// anyone to type a key into one. The key is not logged, not stored by TradeAgent, and not kept
    /// in memory beyond this call.
    /// </summary>
    public async Task SignInWithApiKeyAsync(string key, CancellationToken ct = default)
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
            foreach (var (k, v) in _env) psi.Environment[k] = v;

            using var p = Process.Start(psi)
                ?? throw new TradeAgentException(ErrorCode.AI_AUTH_REQUIRED, $"could not start {Path.GetFileName(exe)}");
            await p.StandardInput.WriteLineAsync(key);
            p.StandardInput.Close();

            var err = await p.StandardError.ReadToEndAsync(ct);
            var outp = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0)
                throw new TradeAgentException(ErrorCode.AI_AUTH_REQUIRED,
                    Summarise(string.IsNullOrWhiteSpace(err) ? outp : err));
            return;
        }

        if (plan.File is { Length: > 0 } && plan.FileTemplate is { Length: > 0 } template)
        {
            var path = ExpandHome(plan.File);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // JSON-escape so a key containing a quote or backslash cannot corrupt the file.
            var escaped = System.Text.Json.JsonEncodedText.Encode(key).ToString();
            await File.WriteAllTextAsync(path, template.Replace("{key}", escaped), ct);
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

        var r = await Run(exe, manifest.AuthStateArgs, TimeSpan.FromSeconds(30), ct);
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
        _conversation ??= new AgentSession(manifest, ResolveExecutable, () => _workspace, () => _env);

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

        if (manifest.InteractiveArgs.Length > 0 && _session is not { HasExited: false })
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
            foreach (var (k, v) in _env) psi.Environment[k] = v;
            _session = Process.Start(psi);
        }

        var conversation = OpenConversation();
        await conversation.StartAsync(ct);
        _started = true;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        _started = false;
        if (_conversation is not null) await _conversation.StopAsync();
        StopLogin();
        try { if (_session is { HasExited: false }) _session.Kill(entireProcessTree: true); }
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
        if (_started || _session is { HasExited: false }) return HealthState.READY;
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
    public async Task<ProcResult> Run(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _workspace
        };
        SetCommand(psi, exe, args);
        foreach (var (k, v) in _env) psi.Environment[k] = v;

        using var p = Process.Start(psi) ?? throw new TradeAgentException(ErrorCode.AI_INSTALL_FAILED, $"could not start {exe}");
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
            try { p.Kill(entireProcessTree: true); } catch (Exception) { }
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

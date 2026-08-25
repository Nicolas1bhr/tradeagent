using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using TradeAgent.Core;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// One generic implementation driven by a <see cref="RuntimeManifest"/>. OpenCode, Codex and any
/// future CLI are the same code with different data — which is what keeps runtime-specific hacks
/// from leaking into the rest of TradeAgent.
/// </summary>
public sealed class CliAgentRuntime(RuntimeManifest manifest) : IAgentRuntime
{
    Process? _session;
    string _workspace = Paths.Workspace;
    Dictionary<string, string> _env = new();

    public RuntimeManifest Manifest => manifest;
    public string Id => manifest.Id;
    public string DisplayName => manifest.DisplayName;

    public RuntimeCapabilities Capabilities => new(
        CanInstallItself: manifest.Install.Kind is not (InstallKind.None or InstallKind.Manual),
        BrowserAuth: manifest.AuthArgs.Length > 0,
        CanRunHeadlessTask: manifest.TaskArgs.Length > 0,
        SelfContained: manifest.SelfContained);

    /// <summary>Managed copy first, PATH second. A TradeAgent-owned binary beats whatever is on the machine.</summary>
    public string? ResolveExecutable()
    {
        if (string.IsNullOrWhiteSpace(manifest.Executable)) return null;
        var managed = Path.Combine(Paths.Tools, manifest.Id, manifest.Executable);
        if (File.Exists(managed)) return managed;

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), manifest.Executable);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* a malformed PATH entry is not our problem */ }
        }
        return null;
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

    public async Task<RuntimeDetection> InstallAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var target = Path.Combine(Paths.Tools, manifest.Id);
        Directory.CreateDirectory(target);

        switch (manifest.Install.Kind)
        {
            case InstallKind.Download:
                if (string.IsNullOrWhiteSpace(manifest.Install.Url))
                    throw new TradeAgentException(ErrorCode.AI_INSTALL_FAILED, "no download URL in the manifest");
                progress?.Report($"Downloading {manifest.DisplayName}...");
                var archive = Path.Combine(target, "download.tmp");
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
                await using (var src = await http.GetStreamAsync(manifest.Install.Url, ct))
                await using (var dst = File.Create(archive))
                    await src.CopyToAsync(dst, ct);

                progress?.Report("Unpacking...");
                try { ZipFile.ExtractToDirectory(archive, target, overwriteFiles: true); }
                catch (InvalidDataException)
                {
                    // Not a zip: assume it is the executable itself.
                    File.Move(archive, Path.Combine(target, manifest.Executable), overwrite: true);
                }
                if (File.Exists(archive)) File.Delete(archive);
                break;

            case InstallKind.Npm:
                progress?.Report($"Installing {manifest.DisplayName} with npm...");
                var npm = await Run(OperatingSystem.IsWindows() ? "npm.cmd" : "npm",
                    ["install", "--prefix", target, manifest.Install.NpmPackage ?? ""], TimeSpan.FromMinutes(10), ct);
                if (npm.ExitCode != 0)
                    throw new TradeAgentException(ErrorCode.AI_INSTALL_FAILED, $"npm failed: {npm.StdErr}");
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

        var detected = await DetectAsync(ct);
        if (!detected.Installed)
            throw new TradeAgentException(ErrorCode.AI_INSTALL_FAILED, "installation finished but the program was not found");
        return detected;
    }

    public Task<RuntimeDetection> UpdateAsync(IProgress<string>? progress = null, CancellationToken ct = default) =>
        InstallAsync(progress, ct);

    public async Task BeginAuthenticationAsync(CancellationToken ct = default)
    {
        var exe = ResolveExecutable() ?? throw new TradeAgentException(ErrorCode.AI_RUNTIME_NOT_FOUND);
        if (manifest.AuthArgs.Length == 0)
            throw new TradeAgentException(ErrorCode.AI_AUTH_REQUIRED, $"{manifest.DisplayName} has no sign-in command in its manifest");

        // Interactive on purpose: the runtime opens its own browser flow. TradeAgent never handles
        // the credential, and never asks the user for an API key when account sign-in exists.
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = _workspace };
        foreach (var a in manifest.AuthArgs) psi.ArgumentList.Add(a);
        foreach (var (k, v) in _env) psi.Environment[k] = v;
        Process.Start(psi);
        await Task.CompletedTask;
    }

    public async Task<AuthState> GetAuthenticationStateAsync(CancellationToken ct = default)
    {
        var exe = ResolveExecutable();
        if (exe is null) return AuthState.Unknown;
        if (manifest.AuthStateArgs.Length == 0) return AuthState.Unknown;

        var r = await Run(exe, manifest.AuthStateArgs, TimeSpan.FromSeconds(30), ct);
        var text = r.StdOut + "\n" + r.StdErr;
        if (manifest.AuthStateSuccessPattern is { } pattern)
            return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase) ? AuthState.Authenticated : AuthState.NotAuthenticated;
        return r.ExitCode == 0 ? AuthState.Authenticated : AuthState.NotAuthenticated;
    }

    public Task CreateEnvironmentAsync(string workspace, IReadOnlyDictionary<string, string> env, CancellationToken ct = default)
    {
        _workspace = workspace;
        _env = new Dictionary<string, string>(env);
        Directory.CreateDirectory(workspace);
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_session is { HasExited: false }) return Task.CompletedTask;
        var exe = ResolveExecutable() ?? throw new TradeAgentException(ErrorCode.AI_RUNTIME_NOT_FOUND);

        var psi = new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = _workspace };
        foreach (var a in manifest.InteractiveArgs) psi.ArgumentList.Add(a);
        _session = Process.Start(psi);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        try { if (_session is { HasExited: false }) _session.Kill(entireProcessTree: true); }
        catch (Exception) { /* already gone */ }
        _session = null;
        return Task.CompletedTask;
    }

    public async Task RestartAsync(CancellationToken ct = default)
    {
        await StopAsync(ct);
        await StartAsync(ct);
    }

    public async Task<string> ExecuteTaskAsync(string prompt, CancellationToken ct = default)
    {
        var exe = ResolveExecutable() ?? throw new TradeAgentException(ErrorCode.AI_RUNTIME_NOT_FOUND);
        var args = manifest.TaskArgs.Select(a => a.Replace("{prompt}", prompt)).ToArray();
        var r = await Run(exe, args, TimeSpan.FromMinutes(15), ct);
        return string.IsNullOrWhiteSpace(r.StdOut) ? r.StdErr : r.StdOut;
    }

    public async Task<HealthState> GetHealthAsync(CancellationToken ct = default)
    {
        var exe = ResolveExecutable();
        if (exe is null) return HealthState.FAILED;
        if (_session is { HasExited: false }) return HealthState.READY;
        var v = await GetVersionAsync(ct);
        return v is null ? HealthState.DEGRADED : HealthState.READY;
    }

    public sealed record ProcResult(int ExitCode, string StdOut, string StdErr);

    /// <summary>Runs a child process with a hard timeout and captured output. Never inherits a console.</summary>
    public async Task<ProcResult> Run(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _workspace
        };
        foreach (var a in args) if (!string.IsNullOrEmpty(a)) psi.ArgumentList.Add(a);
        foreach (var (k, v) in _env) psi.Environment[k] = v;

        using var p = Process.Start(psi) ?? throw new TradeAgentException(ErrorCode.AI_INSTALL_FAILED, $"could not start {exe}");
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
}

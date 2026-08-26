using System.Diagnostics;
using System.Text.Json;
using TradeAgent.Core;

namespace TradeAgent.Provisioning;

/// <summary>
/// TradeAgent's own private copy of Node.js.
///
/// Deliberately the official Windows x64 <b>ZIP</b>, not the MSI: a zip unpacks into
/// <c>%LOCALAPPDATA%\TradeAgent\tools\node</c> with no installer, no administrator prompt, no entry
/// in Programs and Features, and no change to the machine's PATH. Nothing outside TradeAgent's own
/// folder is touched, so a user who already has a different Node keeps it, and uninstalling
/// TradeAgent takes this with it.
///
/// Nothing in here opens a window.
/// </summary>
public static class NodeRuntime
{
    /// <summary>
    /// Used only when nodejs.org cannot be reached to ask what the current LTS is.
    /// Confirmed against https://nodejs.org/dist/index.json on 2026-08-26: newest entry whose
    /// "lts" field is not false was v24.20.0 ("Krypton").
    /// </summary>
    public const string PinnedLtsVersion = "v24.20.0";

    public static string Dir => Path.Combine(Paths.Tools, "node");

    public static bool IsInstalled => NodeExe is not null;

    /// <summary>Full path to node.exe inside TradeAgent's own tools folder, or null.</summary>
    public static string? NodeExe
    {
        get
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(Dir, "node.exe"),
                         Path.Combine(Dir, "node"),
                         Path.Combine(Dir, "bin", "node")
                     })
                if (File.Exists(candidate)) return candidate;
            return null;
        }
    }

    /// <summary>
    /// Path to the bundled npm entry point. It is a JavaScript file, run as
    /// <c>node.exe npm-cli.js ...</c> — never as a bare <c>npm</c>, which on Windows is a
    /// <c>.cmd</c> shim that needs a command interpreter and a PATH we deliberately did not set.
    /// </summary>
    public static string? NpmCli
    {
        get
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(Dir, "node_modules", "npm", "bin", "npm-cli.js"),
                         Path.Combine(Dir, "lib", "node_modules", "npm", "bin", "npm-cli.js")
                     })
                if (File.Exists(candidate)) return candidate;
            return null;
        }
    }

    public static async Task InstallAsync(IProgress<ProvisionProgress>? progress = null, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new TradeAgentException(ErrorCode.AI_INSTALL_FAILED,
                "TradeAgent can only install its private copy of Node.js on Windows. " +
                "On this computer, install Node.js yourself and TradeAgent will use it.");

        progress?.Report(new ProvisionProgress("node", "Finding the current version of Node.js"));
        var version = await ResolveLtsVersionAsync(ct) ?? PinnedLtsVersion;

        Directory.CreateDirectory(Dir);
        var zipName = $"node-{version}-win-x64.zip";
        var url = $"https://nodejs.org/dist/{version}/{zipName}";

        // nodejs.org publishes a SHA256 manifest beside every build. When it is reachable the
        // download is checked against it; when it is not, the install still proceeds rather than
        // failing over a file that is only there to make a good thing better.
        var sha = await ResolvePublishedShaAsync(version, zipName, ct);
        if (sha is null)
            progress?.Report(new ProvisionProgress("node", "Node.js checksum list unavailable — continuing without it"));

        progress?.Report(new ProvisionProgress("node", $"Downloading Node.js {version}"));
        await Downloader.DownloadAndUnpackAsync(url, Dir, progress, ct, sha);

        progress?.Report(new ProvisionProgress("node", "Arranging the files"));
        Flatten(version);

        progress?.Report(new ProvisionProgress("node", "Checking Node.js runs"));
        var reported = await VersionAsync(ct);
        if (reported is null)
            throw new TradeAgentException(ErrorCode.AI_INSTALL_FAILED,
                "Node.js was downloaded but would not run. TradeAgent has not changed anything else on this computer.");

        progress?.Report(new ProvisionProgress("node", $"Node.js {reported} is ready", 1.0));
    }

    /// <summary>
    /// The official zip unpacks as <c>node-vX.Y.Z-win-x64/…</c>. That version-stamped folder would
    /// make every path in the product depend on the Node version, so its contents are lifted one
    /// level and the wrapper removed: node.exe ends up directly in <see cref="Dir"/>.
    /// </summary>
    static void Flatten(string version)
    {
        var nested = Path.Combine(Dir, $"node-{version}-win-x64");
        if (!Directory.Exists(nested))
        {
            // A future zip could name its folder differently; take any single node-* child.
            nested = Directory.GetDirectories(Dir, "node-*").FirstOrDefault() ?? "";
            if (!Directory.Exists(nested)) return;
        }

        foreach (var file in Directory.GetFiles(nested))
        {
            var target = Path.Combine(Dir, Path.GetFileName(file));
            if (File.Exists(target)) File.Delete(target);
            File.Move(file, target);
        }
        foreach (var dir in Directory.GetDirectories(nested))
        {
            var target = Path.Combine(Dir, Path.GetFileName(dir));
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(dir, target);
        }
        try { Directory.Delete(nested, recursive: true); }
        catch (IOException) { /* empty shell left behind is harmless */ }
    }

    /// <summary>
    /// Asks nodejs.org which version is current LTS. The index is newest-first and marks a
    /// long-term-support release with a codename string in "lts" (false when it is not one).
    /// </summary>
    public static async Task<string?> ResolveLtsVersionAsync(CancellationToken ct = default)
    {
        var body = await Downloader.TryGetStringAsync("https://nodejs.org/dist/index.json", ct);
        if (body is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("lts", out var lts) || lts.ValueKind == JsonValueKind.False) continue;
                if (!entry.TryGetProperty("version", out var v)) continue;
                var version = v.GetString();
                if (!string.IsNullOrWhiteSpace(version)) return version;
            }
        }
        catch (Exception) { /* fall back to the pinned version */ }
        return null;
    }

    static async Task<string?> ResolvePublishedShaAsync(string version, string fileName, CancellationToken ct)
    {
        var text = await Downloader.TryGetStringAsync($"https://nodejs.org/dist/{version}/SHASUMS256.txt", ct);
        if (text is null) return null;
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*') == fileName) return parts[0];
        }
        return null;
    }

    /// <summary>Runs <c>node --version</c> with the output captured and no window. Null if it will not run.</summary>
    public static async Task<string?> VersionAsync(CancellationToken ct = default)
    {
        var exe = NodeExe;
        if (exe is null) return null;

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Dir
        };
        psi.ArgumentList.Add("--version");

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return null;
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timer.CancelAfter(TimeSpan.FromSeconds(30));
            var stdout = p.StandardOutput.ReadToEndAsync(timer.Token);
            await p.WaitForExitAsync(timer.Token);
            var text = (await stdout).Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Installs an npm package into <paramref name="prefix"/> using the bundled npm. Node is
    /// installed first if it is not there yet. Never touches a global npm prefix and never needs a
    /// <c>npm</c> on PATH.
    /// </summary>
    public static async Task<string> InstallPackageAsync(
        string package,
        string prefix,
        IProgress<ProvisionProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!IsInstalled) await InstallAsync(progress, ct);

        var node = NodeExe ?? throw new TradeAgentException(ErrorCode.AI_INSTALL_FAILED, "Node.js is not installed");
        var npm = NpmCli ?? throw new TradeAgentException(ErrorCode.AI_INSTALL_FAILED,
            "the copy of Node.js TradeAgent installed does not contain npm");

        Directory.CreateDirectory(prefix);
        progress?.Report(new ProvisionProgress("npm", $"Installing {package}"));

        var psi = new ProcessStartInfo(node)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = prefix
        };
        // -g is not optional. npm's own documentation: "When the global flag is set, npm installs
        // things into this prefix. When it is not set, it uses the root of the current package, or
        // the current working directory". Without -g this is a local install that produces no
        // launcher at all, so the program would be downloaded and then not be findable.
        foreach (var a in new[] { npm, "install", "-g", "--prefix", prefix, package, "--no-audit", "--no-fund", "--loglevel=error" })
            psi.ArgumentList.Add(a);

        // npm shells out to node for lifecycle scripts, so our private node has to be findable —
        // but only by this child process. The machine's PATH is not modified.
        psi.Environment["PATH"] = $"{Dir}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}";

        using var process = Process.Start(psi)
            ?? throw new TradeAgentException(ErrorCode.AI_INSTALL_FAILED, "npm would not start");

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(TimeSpan.FromMinutes(15));
        var stdout = process.StandardOutput.ReadToEndAsync(timer.Token);
        var stderr = process.StandardError.ReadToEndAsync(timer.Token);
        try
        {
            await process.WaitForExitAsync(timer.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            throw new TradeAgentException(ErrorCode.AI_INSTALL_FAILED, $"installing {package} took too long and was stopped");
        }

        if (process.ExitCode != 0)
        {
            var detail = (await stderr).Trim();
            if (detail.Length == 0) detail = (await stdout).Trim();
            throw new TradeAgentException(ErrorCode.AI_INSTALL_FAILED,
                $"{package} could not be installed. {Shorten(detail)}");
        }

        progress?.Report(new ProvisionProgress("npm", $"{package} installed", 1.0));

        // Where the launcher lands differs by platform, and npm documents both: on Windows a global
        // install puts the .cmd shim directly in the prefix and the package under
        // <prefix>\node_modules\<pkg>; on Unix the shim goes to <prefix>/bin. The prefix is returned
        // and the caller searches from there, rather than guessing one shape.
        return prefix;
    }

    static string Shorten(string text) =>
        text.Length <= 400 ? text : text[..400] + "…";
}

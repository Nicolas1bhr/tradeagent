using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradeAgent.Core;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// THE SEAT HOME, MADE BY THE APP AND VERIFIED BY IT (<c>U-seat-home</c>; AUTH-001, ORDER-005, CRED-001).
///
/// <para>Every codex process TradeAgent starts runs with <c>CODEX_HOME</c> and <c>HOME</c> inside <see cref="SeatPlan.Root"/>,
/// never the owner's own: his <c>~/.codex</c> carries his plugins, MCP servers, notify hook, skills and sign-in, and none of them
/// is something a seat was configured to have. The app creates the folders; nothing is copied from his.</para>
/// </summary>
public static class SeatProfile
{
    /// <summary>
    /// MAKES THE SEAT HOME READY FOR A START: its folders exist. Called by <see cref="AgentEnvironment.Apply(ProcessStartInfo,
    /// IReadOnlyDictionary{string, string}, RuntimeManifest)"/>, the one seam every process of a runtime passes.
    /// </summary>
    public static void Prepare(SeatPlan seat)
    {
        Directory.CreateDirectory(seat.Root);
        foreach (var (name, value) in seat.ResolvedEnvironment())
            if (seat.Environment[name].Contains("{seatHome}", StringComparison.Ordinal)) Directory.CreateDirectory(value);

        // THE CLI'S OWN CONFIG FILE, WRITTEN BY THE APP BEFORE EVERY START. A turn ignores it (--ignore-user-config), but `login`,
        // `features` and `mcp` take no such flag, and a seat can write this file: a seat-written chatgpt_base_url would steer the
        // next sign-in. Published whole through a rename, so a reader sees the old bytes or the new ones and never half of either.
        if (ConfigPath(seat) is { } path)
        {
            var bytes = ConfigBytes(seat);
            if (!SameBytes(path, bytes)) OwnerOnlyFile.Write(path, bytes);
        }
    }

    /// <summary>Where <see cref="SeatPlan.ConfigFile"/> is, or null for a plan with none.</summary>
    public static string? ConfigPath(SeatPlan seat) =>
        seat.ConfigFile is { Length: > 0 } rel
            ? Path.GetFullPath(Path.Combine(seat.Root, rel.Replace('/', Path.DirectorySeparatorChar)))
            : null;

    /// <summary>
    /// THE CONFIG FILE'S BYTES: the <see cref="SeatPlan.CliClass"/> deny set's keys as TOML, one per line — <c>-c key=value</c> as
    /// <c>key = value</c>, <c>--disable name</c> as <c>features.name = false</c>, <c>--enable name</c> as <c>= true</c>. The flags
    /// with no key (<c>--ignore-user-config</c> and its kind) have no line. UTF-8, no byte-order mark, LF.
    /// </summary>
    public static byte[] ConfigBytes(SeatPlan seat)
    {
        var lines = new List<string> { "# Written by TradeAgent before every start of this AI seat. Any change here is replaced." };
        var args = seat.ArgsFor(SeatPlan.CliClass);
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--disable" when i + 1 < args.Length: lines.Add($"features.{args[++i]} = false"); break;
                case "--enable" when i + 1 < args.Length: lines.Add($"features.{args[++i]} = true"); break;
                case "-c" or "--config" when i + 1 < args.Length:
                    var pair = args[++i];
                    var eq = pair.IndexOf('=', StringComparison.Ordinal);
                    if (eq > 0) lines.Add($"{pair[..eq].Trim()} = {pair[(eq + 1)..].Trim()}");
                    break;
            }
        }
        return new UTF8Encoding(false).GetBytes(string.Join("\n", lines) + "\n");
    }

    // ---- verified at every launch ------------------------------------------------------------------

    /// <summary>
    /// THE SEAT'S PROFILE, VERIFIED BEFORE A LAUNCH IS ADMITTED AND AGAIN AS IT STARTS, fresh or resumed: the program is the pin; the
    /// command line carries the class's deny set; the config file is the app's bytes; nothing the profile forbids is in the seat home
    /// or the workspace; and the CLI's own report shows no feature outside the allowlist on and no MCP server but the app's. Any miss
    /// is the refusal, in words naming what and where; a runtime with no seat profile passes untouched.
    ///
    /// <para><b>INTEGRITY ADVISORY</b> while seats are unconfined (<c>docs/ORCHESTRATION.md</c> § 2.7): the CLI runs as the owner's
    /// user, so what is verified here can change between this check and the start. It turns a misconfigured seat into a refusal; it
    /// does not make a hostile one impossible.</para>
    /// </summary>
    public static async Task<SeatVerdict> VerifyAsync(RuntimeManifest manifest, string exe, IReadOnlyList<string> argv,
        string workingDirectory, ISeatProbe probe, CancellationToken ct)
    {
        if (manifest.Seat is not { } seat) return SeatVerdict.None;

        Prepare(seat);

        if (PinRefusal(manifest, exe) is { } notThePin)
            return SeatVerdict.Refuse(ErrorCode.AI_VERSION_UNSUPPORTED, notThePin);

        var deny = seat.ArgsFor(SeatPlan.CliClass);
        if (deny.Length > 0 && !Carries(argv, deny))
            return SeatVerdict.Refuse(ErrorCode.AI_SEAT_REFUSED,
                $"TradeAgent will not start the AI: the command it built for {manifest.DisplayName} does not carry the seat's deny set. " +
                "This is TradeAgent's own fault, not the AI's; nothing was started.");

        if (ConfigPath(seat) is { } config && !SameBytes(config, ConfigBytes(seat)))
            return SeatVerdict.Refuse(ErrorCode.AI_SEAT_REFUSED,
                $"TradeAgent will not start the AI: its settings file {config} is not the one TradeAgent just wrote. " +
                "Something is changing it as it is written; close whatever else uses that folder.");

        if (Foreign(seat.Root, seat.Forbidden) is { } inHome)
            return SeatVerdict.Refuse(ErrorCode.AI_SEAT_REFUSED,
                $"TradeAgent will not start the AI while {inHome} exists: the AI's own home may hold only what TradeAgent puts " +
                "there, and the AI would read that as instructions or tools. Remove it to continue.");

        if (Foreign(workingDirectory, seat.WorkspaceForbidden) is { } inWorkspace)
            return SeatVerdict.Refuse(ErrorCode.AI_SEAT_REFUSED,
                $"TradeAgent will not start the AI while {inWorkspace} exists in its working folder: the AI would load it as its " +
                "own settings or skills. Remove it to continue.");

        SeatReading reading;
        try { reading = await probe.ReadAsync(manifest, exe, workingDirectory, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return SeatVerdict.Refuse(ErrorCode.AI_SEAT_REFUSED,
                $"TradeAgent will not start the AI: it could not read what {manifest.DisplayName} would switch on ({ex.Message}).");
        }

        if (OffTheAllowlist(seat, reading.Features) is { } feature)
            return SeatVerdict.Refuse(ErrorCode.AI_SEAT_REFUSED,
                $"TradeAgent will not start the AI: {manifest.DisplayName} reports {feature} switched on, which the AI's profile does " +
                $"not allow. This build of TradeAgent runs {manifest.DisplayName} {manifest.Install.Version} with only its own set on.");

        if (Servers(seat, reading.Mcp) is { } server)
            return SeatVerdict.Refuse(ErrorCode.AI_SEAT_REFUSED,
                $"TradeAgent will not start the AI: {manifest.DisplayName} reports {server}, and the AI's profile allows no tool " +
                "server TradeAgent did not configure.");

        // A FRESH START, REFUSED IN WORDS: the seat home holds no credential until the owner signs in from TradeAgent. Nothing is
        // copied from his own ~/.codex — no migration (CLAUDE.md) — and a seat that ran signed out would fail as a turn, which reads
        // like the AI thinking rather than like the button he has to press.
        if (!reading.SignedIn)
            return SeatVerdict.Refuse(ErrorCode.AI_AUTH_REQUIRED, seat.SignedOut);

        return new SeatVerdict(null, null, reading) { Profile = ProfileHash(manifest, exe, ConfigBytes(seat), reading) };
    }

    /// <summary>
    /// THE RUNTIME PROFILE A LAUNCH RAN UNDER, as one SHA-256 (lower-case hex): the program's own hash and the pinned version, the
    /// seat's argv minus its template, prompt and model (the stream flag, the unattended flags and the deny set — the same for a
    /// first turn and a resume), the config file's bytes, and the CLI's two reports verbatim. Written on the launch's
    /// <c>ai_attempt</c> row; one deny flag moved is another profile, and so is another release.
    /// </summary>
    public static string ProfileHash(RuntimeManifest manifest, string exe, byte[] config, SeatReading reading)
    {
        var seat = manifest.Seat;
        var argv = new List<string>();
        if (!string.IsNullOrWhiteSpace(manifest.JsonFlag)) argv.AddRange(manifest.JsonFlag.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        argv.AddRange(manifest.UnattendedArgs);
        argv.AddRange(seat?.ArgsFor(SeatPlan.CliClass) ?? []);

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Part(string label, ReadOnlySpan<byte> bytes)
        {
            sha.AppendData(Encoding.UTF8.GetBytes($"{label}:{bytes.Length}:"));
            sha.AppendData(bytes);
        }
        Part("exe", Encoding.UTF8.GetBytes(Sha256Of(exe)));
        Part("version", Encoding.UTF8.GetBytes(manifest.Install.Version ?? ""));
        Part("argv", Encoding.UTF8.GetBytes(string.Join('\0', argv)));
        Part("config", config);
        Part("features", Encoding.UTF8.GetBytes(reading.Features));
        Part("mcp", Encoding.UTF8.GetBytes(reading.Mcp));
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>Whether <paramref name="argv"/> holds <paramref name="deny"/> as one unbroken run, in order.</summary>
    static bool Carries(IReadOnlyList<string> argv, string[] deny)
    {
        for (var i = 0; i + deny.Length <= argv.Count; i++)
        {
            var j = 0;
            while (j < deny.Length && string.Equals(argv[i + j], deny[j], StringComparison.Ordinal)) j++;
            if (j == deny.Length) return true;
        }
        return false;
    }

    /// <summary>The first of <paramref name="names"/> that exists under <paramref name="root"/>, as a full path, or null.</summary>
    static string? Foreign(string root, string[] names)
    {
        foreach (var name in names)
        {
            var path = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(path) || Directory.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>The features the report shows on and the allowlist does not hold, named, or null when there are none.</summary>
    static string? OffTheAllowlist(SeatPlan seat, string report)
    {
        if (seat.FeaturesArgs.Length == 0) return null;
        var on = new List<string>();
        var read = 0;
        foreach (var line in report.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2 || words[^1] is not ("true" or "false")) continue;
            read++;
            if (words[^1] == "true" && !seat.FeatureAllowlist.Contains(words[0], StringComparer.Ordinal)) on.Add(words[0]);
        }
        if (read == 0) return "no feature list TradeAgent could read";
        return on.Count == 0 ? null : (on.Count == 1 ? $"the feature {on[0]}" : $"the features {string.Join(", ", on)}");
    }

    /// <summary>The MCP servers the report lists and the allowlist does not hold, named, or null when there are none.</summary>
    static string? Servers(SeatPlan seat, string report)
    {
        if (seat.McpArgs.Length == 0) return null;
        List<string> names = [];
        try
        {
            using var doc = JsonDocument.Parse(report);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return "a tool-server list TradeAgent could not read";
            foreach (var server in doc.RootElement.EnumerateArray())
            {
                var name = server.ValueKind == JsonValueKind.Object && server.TryGetProperty("name", out var n) ? n.ToString() : server.ToString();
                if (!seat.McpAllowlist.Contains(name, StringComparer.Ordinal)) names.Add(name);
            }
        }
        catch (JsonException) { return "a tool-server list TradeAgent could not read"; }
        return names.Count == 0 ? null : $"the tool server{(names.Count == 1 ? "" : "s")} {string.Join(", ", names)}";
    }

    // ---- the pin ---------------------------------------------------------------------------------

    /// <summary>
    /// What each program file hashed to, keyed on its path and remembered against its length and last write: a launch hashes the
    /// program once per change rather than once per start, and a file replaced in place is hashed again.
    /// </summary>
    static readonly ConcurrentDictionary<string, (long Length, DateTime Written, string Sha)> Hashes = new(StringComparer.Ordinal);

    /// <summary>SHA-256, lower-case hex, of the file at <paramref name="path"/>.</summary>
    public static string Sha256Of(string path)
    {
        var info = new FileInfo(path);
        var (length, written) = (info.Length, info.LastWriteTimeUtc);
        if (Hashes.TryGetValue(path, out var known) && known.Length == length && known.Written == written) return known.Sha;

        using var stream = File.OpenRead(path);
        var sha = Convert.ToHexStringLower(SHA256.HashData(stream));
        Hashes[path] = (length, written, sha);
        return sha;
    }

    /// <summary>Whether <paramref name="exe"/> is the plan's pinned program — always, for a plan that pins none.</summary>
    public static bool IsPin(RuntimeManifest manifest, string exe)
    {
        if (manifest.Install.ExecutableSha256 is not { Length: > 0 } pin) return true;
        try { return string.Equals(Sha256Of(exe), pin.Trim(), StringComparison.OrdinalIgnoreCase); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// NULL WHEN <paramref name="exe"/> IS THE PIN, or the refusal in words naming the file, its SHA-256 and the pin's, the pinned
    /// version, and the repair. <b>A program that is not the pin is never run</b> — not even with <c>--version</c> to name what it
    /// is: running it to describe it would be running exactly what the pin exists to keep from running. Its version is therefore
    /// "unknown — not run"; the only version the words state is the pinned one, from the manifest entry the hash is held against.
    /// </summary>
    public static string? PinRefusal(RuntimeManifest manifest, string exe)
    {
        if (manifest.Install.ExecutableSha256 is not { Length: > 0 } pin) return null;

        string found;
        try { found = Sha256Of(exe); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { found = $"unreadable: {ex.Message}"; }
        if (string.Equals(found, pin.Trim(), StringComparison.OrdinalIgnoreCase)) return null;

        var repair = manifest.Install.Kind is InstallKind.Download or InstallKind.Npm or InstallKind.Winget
            ? "Install the AI assistant again from TradeAgent and it puts that exact version in its own folder."
            : "TradeAgent cannot install it on this computer, so that exact version has to be the one this computer finds.";
        return $"TradeAgent starts the AI only from {manifest.DisplayName} {manifest.Install.Version ?? "(pinned)"}, SHA-256 {pin.Trim()}. " +
               $"The program it found, {exe}, has SHA-256 {found}, version unknown — not run: TradeAgent never runs a program " +
               $"that is not the pin, not even to ask its version. {repair}";
    }

    static bool SameBytes(string path, byte[] bytes)
    {
        try { return File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

/// <summary>
/// WHAT <see cref="SeatProfile.VerifyAsync"/> FOUND: a refusal in words with its code, or none — and what the probe read.
/// </summary>
public sealed record SeatVerdict(string? Refusal, ErrorCode? Code, SeatReading? Reading)
{
    /// <summary>The runtime profile a launch under this verdict runs with (<see cref="SeatProfile.ProfileHash"/>), or null.</summary>
    public string? Profile { get; init; }

    /// <summary>A runtime with no seat profile: nothing refused, nothing read.</summary>
    public static readonly SeatVerdict None = new(null, null, null);

    public static SeatVerdict Refuse(ErrorCode code, string why) => new(why, code, null);

    public bool Refused => Refusal is not null;
}

/// <summary>
/// A CONVERSATION WHOSE SEAT IS VERIFIED BEFORE A LAUNCH IS ADMITTED. The meter asks it inside admission, before any row is written,
/// so a seat that would be refused never reserves money or consumes a wake.
/// </summary>
public interface ISeatLaunch
{
    /// <summary>The seat's profile for the turn this conversation would launch next — refused in words, or not.</summary>
    SeatVerdict CheckSeat();
}

/// <summary>
/// What the CLI itself said about a seat: its feature report and its MCP server list, verbatim, and whether its sign-in status
/// under the seat home answered signed in. Only the two reports enter the profile hash; a sign-in is not part of the profile.
/// </summary>
public sealed record SeatReading(string Features, string Mcp, bool SignedIn = true);

/// <summary>
/// THE CLI ASKED ABOUT ITS OWN SEAT, behind an interface because CI has no codex: a test hands a fake, the product the real one.
/// </summary>
public interface ISeatProbe
{
    /// <summary>The feature report and the MCP server list, run under the seat home with the deny set's keys.</summary>
    Task<SeatReading> ReadAsync(RuntimeManifest manifest, string exe, string workingDirectory, CancellationToken ct);
}

/// <summary>
/// THE REAL PROBE: the manifest's own commands, started the way every process of the runtime is — no window, stdin closed, the
/// whitelist and the seat home (<see cref="AgentEnvironment.Apply(ProcessStartInfo, IReadOnlyDictionary{string, string},
/// RuntimeManifest)"/>), its tree held and ended.
/// </summary>
public sealed class CliSeatProbe : ISeatProbe
{
    public static readonly CliSeatProbe Instance = new();

    static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    public async Task<SeatReading> ReadAsync(RuntimeManifest manifest, string exe, string workingDirectory, CancellationToken ct)
    {
        var seat = manifest.Seat ?? throw new InvalidOperationException($"{manifest.DisplayName} has no seat profile");
        var keys = seat.KeyArgs();
        var features = seat.FeaturesArgs.Length == 0 ? "" : await Read(manifest, exe, [.. seat.FeaturesArgs, .. keys], workingDirectory, ct);
        var mcp = seat.McpArgs.Length == 0 ? "" : await Read(manifest, exe, [.. seat.McpArgs, .. keys], workingDirectory, ct);

        // THE SIGN-IN, UNDER THE SEAT HOME: `login status` exits 0 signed in and 1 not (measured, the manifest's AuthStateArgs). It
        // reads the app's own config.toml, written above by the environment seam, so cli_auth_credentials_store="file" holds.
        var signedIn = true;
        if (manifest.AuthStateArgs.Length > 0)
        {
            var (exit, stdout, stderr) = await Run(manifest, exe, manifest.AuthStateArgs, workingDirectory, ct);
            signedIn = manifest.AuthStateSuccessPattern is { } pattern
                ? System.Text.RegularExpressions.Regex.IsMatch(Ansi.Strip(stdout + "\n" + stderr), pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                : exit == 0;
        }
        return new SeatReading(features, mcp, signedIn);
    }

    static async Task<string> Read(RuntimeManifest manifest, string exe, string[] args, string cwd, CancellationToken ct)
    {
        var (exit, stdout, stderr) = await Run(manifest, exe, args, cwd, ct);
        if (exit != 0)
            throw new InvalidOperationException(
                $"`{string.Join(' ', args.Take(2))}` exited {exit}: {Ansi.Strip(stderr).Trim().Split('\n').FirstOrDefault()}");
        return stdout;
    }

    static async Task<(int Exit, string StdOut, string StdErr)> Run(RuntimeManifest manifest, string exe, IEnumerable<string> args,
        string cwd, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = cwd,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        CliAgentRuntime.SetCommand(psi, exe, args);
        AgentEnvironment.Apply(psi, new Dictionary<string, string>(), manifest);

        using var held = ProcessContainment.Start(psi);
        var p = held.Process;
        try { p.StandardInput.Close(); } catch (Exception) { /* already gone */ }
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(Deadline);
        var stdout = p.StandardOutput.ReadToEndAsync(timer.Token);
        var stderr = p.StandardError.ReadToEndAsync(timer.Token);
        try
        {
            await p.WaitForExitAsync(timer.Token);
            return (p.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            held.Kill();
            throw new InvalidOperationException($"{Path.GetFileName(exe)} did not answer within {Deadline.TotalSeconds:0} seconds");
        }
    }
}

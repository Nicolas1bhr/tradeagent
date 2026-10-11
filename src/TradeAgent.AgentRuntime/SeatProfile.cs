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
    /// MAKES THE SEAT HOME READY FOR A START: its folders exist and its config file is the app's. Called by <see
    /// cref="AgentEnvironment.Apply(ProcessStartInfo, IReadOnlyDictionary{string, string}, RuntimeManifest)"/>, the one seam every
    /// process of a runtime passes, and by <see cref="VerifyAsync"/>.
    ///
    /// <para><b>A LINK IS REFUSED, NEVER FOLLOWED</b> (CRED-001): every component from <see cref="SeatPlan.Root"/> down to each folder
    /// and to the config file is looked at itself, not at what it points to, and a symbolic link, a junction or any other reparse
    /// point is a <see cref="ErrorCode.AI_SEAT_REFUSED"/> naming it and the repair — never a silent re-create, and never a write
    /// through it, which would have overwritten the owner's own <c>~/.codex/config.toml</c> and reused his sign-in had
    /// <c>seat-home/codex</c> been linked to it. Missing folders are made one level at a time and looked at again; the config file
    /// is read with a no-follow open. The folders above the root are the app's own home, which the owner places, and are not
    /// judged here. A folder another account owns is refused too, and each folder is made owner-only (<see cref="OwnedHere"/>).
    /// ADVISORY like the rest while seats are unconfined (§ 2.7): a same-user
    /// writer can swap a folder for a link between this look and the start.</para>
    ///
    /// <para><b>THE WHOLE HOME, AT EVERY PREPARATION</b> (<c>U-seat-acl</c>; CRED-001): not only the folders the environment names but
    /// every folder below the root — whatever the CLI made there since — and the files that hold its credential
    /// (<see cref="SeatPlan.PrivateFiles"/>) and its settings (<see cref="SeatPlan.ConfigFile"/>) are made owner-only, because an access
    /// entry on a file outlives any change to its folder's. A link met on the walk is never followed (its target is not the seat home);
    /// a credential file that is a link is refused like a folder.</para>
    /// </summary>
    public static void Prepare(SeatPlan seat)
    {
        var root = Path.GetFullPath(seat.Root);
        RealFolder(root, root);
        foreach (var (name, value) in seat.ResolvedEnvironment())
            if (seat.Environment[name].Contains("{seatHome}", StringComparison.Ordinal)) RealFolder(root, Path.GetFullPath(value));
        PrivateBelow(root, seat);

        // THE CLI'S OWN CONFIG FILE, WRITTEN BY THE APP BEFORE EVERY START. A turn ignores it (--ignore-user-config), but `login`,
        // `features` and `mcp` take no such flag, and a seat can write this file: a seat-written chatgpt_base_url would steer the
        // next sign-in. Published whole through a rename, so a reader sees the old bytes or the new ones and never half of either —
        // and a rename replaces a name, it never writes through one.
        if (ConfigPath(seat) is { } path)
        {
            RealFolder(root, Path.GetDirectoryName(path)!);
            NotALink(path);
            var bytes = ConfigBytes(seat);
            if (!SameBytes(path, bytes)) OwnerOnlyFile.Write(path, bytes);
        }
    }

    /// <summary>
    /// <paramref name="folder"/> under <paramref name="root"/>, every component from the root down a real folder: none a link, each
    /// missing one made alone and looked at again.
    /// </summary>
    static void RealFolder(string root, string folder)
    {
        var rel = Path.GetRelativePath(root, folder);
        if (rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(rel))
            throw new TradeAgentException(ErrorCode.AI_SEAT_REFUSED,
                $"TradeAgent will not use {folder} for the AI: it is outside the AI's own home {root}. This is TradeAgent's own " +
                "settings at fault; nothing was written or started.");

        if (Path.GetDirectoryName(root) is { } above) Directory.CreateDirectory(above);
        var at = root;
        Real(at);
        if (rel == ".") return;
        foreach (var part in rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            Real(at = Path.Combine(at, part));

        void Real(string at)
        {
            NotALink(at);
            if (Directory.Exists(at)) { OwnedHere(at, root); return; }
            if (File.Exists(at))
                throw new TradeAgentException(ErrorCode.AI_SEAT_REFUSED,
                    $"TradeAgent will not use the AI's own home while {at} is a file where its folder belongs. Nothing was written " +
                    $"or started. Remove {at} and TradeAgent makes the folder there.");
            Directory.CreateDirectory(at);
            NotALink(at);
            OwnedHere(at, root);
        }
    }

    /// <summary>
    /// EVERY FOLDER BELOW <paramref name="root"/>, PARENTS FIRST, AND THE CREDENTIAL AND SETTINGS FILES, MADE OWNER-ONLY. A folder
    /// that is a link, a junction or another reparse point is stepped over, never entered: what it points to is not the seat home.
    /// Measured on the dev Mac (U-seat-acl's report): the walk costs milliseconds on a home with thousands of session files, since
    /// it lists folders only.
    /// </summary>
    static void PrivateBelow(string root, SeatPlan seat)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false };
        var folders = new Stack<string>();
        folders.Push(root);
        while (folders.Count > 0)
        {
            var at = folders.Pop();
            List<string> below;
            try { below = [.. Directory.EnumerateDirectories(at, "*", options)]; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw NotPrivate(at, ex.Message); }
            foreach (var folder in below)
            {
                if (IsLink(folder)) continue;
                OwnedHere(folder, root);
                folders.Push(folder);
            }
        }

        foreach (var rel in seat.PrivateFiles.Append(seat.ConfigFile))
        {
            if (string.IsNullOrEmpty(rel)) continue;
            var path = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            NotALink(path);
            if (File.Exists(path)) PrivateFile(path);
        }
    }

    /// <summary>Whether <paramref name="path"/> is itself a link, a junction or any other reparse point, looked at without following it.</summary>
    static bool IsLink(string path)
    {
        var info = new FileInfo(path);
        try { if (info.LinkTarget is not null) return true; }
        catch (IOException) { /* a reparse point this platform cannot read the target of; the attribute says */ }
        return info.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    /// <summary>
    /// <paramref name="path"/>, a file of the seat home that holds its credential or settings, made this account's alone: <c>0600</c> on
    /// Linux and macOS. A file that cannot be is refused in words, never used.
    /// </summary>
    static void PrivateFile(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw NotPrivate(path, ex.Message); }
    }

    /// <summary>The refusal for a seat-home file or folder this account cannot make its own alone, naming it, why, and the repair.</summary>
    static TradeAgentException NotPrivate(string path, string why) =>
        new(ErrorCode.AI_SEAT_REFUSED,
            $"TradeAgent will not use the AI's own home while {path} cannot be made private to this account ({why}): it may belong to " +
            "another account, be locked against changes or sit on a disk that cannot be changed, and the AI's settings and sign-in must " +
            $"be this account's alone. Nothing was written or started. Remove {path} (unlock it first if it is locked); TradeAgent makes " +
            "its own in its place, and a removed sign-in is signed in again from TradeAgent.");

    /// <summary>
    /// REFUSES A SEAT HOME FOLDER THAT BELONGS TO ANOTHER ACCOUNT, and makes each one owner-only. On Linux and macOS the folder is
    /// set to <c>0700</c> — which only its owner may do, so a refused change IS a foreign owner (.NET states no owner there), and the
    /// credential the seat home keeps is then out of other accounts' reach. On Windows its owner is read from its security descriptor —
    /// this account, or the Administrators group for an elevated process (which is whom Windows makes the owner of what that creates) —
    /// and its access list is replaced by a protected one granting only this account, that owner and SYSTEM, so an entry granting
    /// another account access does not survive.
    ///
    /// <para><b>NOT PROVED FOR ROOT.</b> A process running as root may change any folder's mode, so on Linux and macOS a root
    /// process cannot tell a foreign folder from its own here. TradeAgent installs per user and never asks for elevation
    /// (<c>CLAUDE.md</c>), so its seats do not run as root; a build that does would need the owner read from <c>stat</c>.</para>
    /// </summary>
    static void OwnedHere(string at, string root)
    {
        string? owner = null;
        bool ours;
        if (OperatingSystem.IsWindows()) ours = OwnedOnWindows(at, out owner);
        else
        {
            try { new DirectoryInfo(at).UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute; ours = true; }
            catch (UnauthorizedAccessException) { ours = false; }
            // NOT MADE OWNER-ONLY FOR ANOTHER REASON — a read-only disk (macOS answers EROFS for its sealed system volume before it
            // asks who owns the folder), or anything else: a folder that cannot be made private is refused the same way, never used.
            catch (IOException ex)
            {
                throw new TradeAgentException(ErrorCode.AI_SEAT_REFUSED,
                    $"TradeAgent will not use the AI's own home while {at} cannot be made private to this account ({ex.Message}): it " +
                    "may belong to another account or sit on a disk that cannot be changed, and the AI's settings and sign-in must be " +
                    $"this account's alone. Nothing was written or started. Remove {at} and TradeAgent makes its own folder there.");
            }
        }
        if (ours) return;
        throw new TradeAgentException(ErrorCode.AI_SEAT_REFUSED,
            $"TradeAgent will not use the AI's own home while {at} belongs to another account{(owner is null ? "" : $" ({owner})")}: " +
            "whoever owns it could change the AI's settings and read its sign-in. Nothing was written or started. Remove " +
            $"{at} and TradeAgent makes its own folder there.");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static bool OwnedOnWindows(string at, out string? owner)
    {
        owner = null;
        try
        {
            var sid = new DirectoryInfo(at).GetAccessControl(System.Security.AccessControl.AccessControlSections.Owner)
                .GetOwner(typeof(System.Security.Principal.SecurityIdentifier)) as System.Security.Principal.SecurityIdentifier;
            if (sid is null) return false;
            owner = sid.Value;
            using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (me.User is not { } user) return false;
            var ours = sid.Equals(user)
                || (sid.IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid)
                    && new System.Security.Principal.WindowsPrincipal(me).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator));
            if (!ours) return false;

            // OWNER-ONLY: a protected list — nothing inherited, nothing kept — granting this account, the owner and SYSTEM.
            var only = new System.Security.AccessControl.DirectorySecurity();
            only.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const System.Security.AccessControl.InheritanceFlags both =
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit;
            foreach (var who in new[] { user, sid, new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null) }.Distinct())
                only.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(who,
                    System.Security.AccessControl.FileSystemRights.FullControl, both,
                    System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
            new DirectoryInfo(at).SetAccessControl(only);
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>
    /// REFUSES <paramref name="path"/> WHEN IT IS ITSELF A LINK — a symbolic link, a junction or any reparse point — looked at
    /// without following it. Nothing there is fine.
    /// </summary>
    static void NotALink(string path)
    {
        var info = new FileInfo(path);
        string? target = null;
        try { target = info.LinkTarget; }
        catch (IOException) { /* not a link this platform can read the target of; the attribute below still says */ }
        var reparse = target is not null
            || ((File.Exists(path) || Directory.Exists(path)) && info.Attributes.HasFlag(FileAttributes.ReparsePoint));
        if (!reparse) return;
        throw new TradeAgentException(ErrorCode.AI_SEAT_REFUSED,
            $"TradeAgent will not use the AI's own home while {path} is a link{(target is null ? "" : $" to {target}")}: it never " +
            "writes the AI's settings or starts the AI through a link, which could lead into your own Codex settings and sign-in. " +
            $"Nothing was written or started. Remove the link {path} — not what it points to — and TradeAgent makes its own folder there.");
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

        try { Prepare(seat); }
        catch (TradeAgentException ex) when (ex.Code == ErrorCode.AI_SEAT_REFUSED) { return SeatVerdict.Refuse(ex.Code, ex.Message); }

        if (PinRefusal(manifest, exe) is { } notThePin)
            return SeatVerdict.Refuse(ErrorCode.AI_VERSION_UNSUPPORTED, notThePin);

        var deny = seat.ArgsFor(SeatPlan.CliClass);
        if (deny.Length > 0 && !Carries(argv, deny))
            return SeatVerdict.Refuse(ErrorCode.AI_SEAT_REFUSED,
                $"TradeAgent will not start the AI: the command it built for {manifest.DisplayName} does not carry the seat's deny set. " +
                "This is TradeAgent's own fault, not the AI's; nothing was started.");

        // THE PROMPT'S SLOT CARRIES THE STDIN MARKER, NEVER THE PROMPT (U-prompt-stdin): a seat's packet on its command line is
        // readable by every process of the user, a confined cell included. The admission's argv and the start's are one argv.
        // A missing marker (empty, or the null an override file can write) is no marker: Build leaves the slot empty then.
        if (manifest.PromptOnStdin && !string.IsNullOrEmpty(manifest.StdinMarker) && !MarksStdin(manifest, argv))
            return SeatVerdict.Refuse(ErrorCode.AI_SEAT_REFUSED,
                $"TradeAgent will not start the AI: the command it built for {manifest.DisplayName} does not carry " +
                $"\"{manifest.StdinMarker}\" where the message goes, the sign that the message travels on its input and not on its command line. " +
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

    /// <summary>
    /// Whether <paramref name="argv"/> carries the stdin marker in the prompt's slot of the fresh or the resumed template: the
    /// marker, then the template's words after the slot, and nothing else to the end.
    /// </summary>
    static bool MarksStdin(RuntimeManifest manifest, IReadOnlyList<string> argv)
    {
        foreach (var template in new[] { manifest.ExecArgs, manifest.ResumeArgs, manifest.TaskArgs })
        {
            var slot = Array.FindIndex(template, a => a.Contains("{prompt}", StringComparison.Ordinal));
            string[] tail = slot < 0 ? [manifest.StdinMarker] : [manifest.StdinMarker, .. template[(slot + 1)..]];
            if (argv.Count >= tail.Length && tail.Select((t, i) => argv[argv.Count - tail.Length + i] == t).All(x => x)) return true;
        }
        return false;
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
    /// SHA-256, lower-case hex, of the file at <paramref name="path"/> — read in full at EVERY call, never remembered. The first pass
    /// kept each hash against the file's length and last write, and a same-user writer can swap same-length bytes and put the time
    /// back; no file metadata is out of that writer's reach (Windows lets it set even the change time), so nothing short of the bytes
    /// is trusted. Measured: codex 0.160.1 is 241,556,032 bytes and hashes in 0.61 s with <c>shasum</c>, far under a turn.
    ///
    /// <para><b>INTEGRITY ADVISORY</b> while any unconfined seat shares the host and user (<c>docs/ORCHESTRATION.md</c> § 2.7): the
    /// same writer can still swap the file between this hash and the start. The pin turns a wrong or updated program into a refusal;
    /// it does not make a hostile same-user swap impossible.</para>
    /// </summary>
    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
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

    static bool SameBytes(string path, byte[] bytes) => ReadNoFollow(path) is { } held && held.AsSpan().SequenceEqual(bytes);

    /// <summary>
    /// THE FILE'S BYTES, OPENED WITHOUT FOLLOWING A LINK, or null for none, a link, or anything unreadable. On Linux and macOS an
    /// <c>open</c> with <c>O_NOFOLLOW</c>, which fails on a link; on Windows the caller has refused any reparse point, and the file
    /// is read as it is.
    /// </summary>
    static byte[]? ReadNoFollow(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return File.Exists(path) ? File.ReadAllBytes(path) : null;

            var fd = open(path, NoFollowReadOnly);
            if (fd < 0) return null;
            using var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)fd, ownsHandle: true);
            using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 0);
            using var held = new MemoryStream();
            stream.CopyTo(held);
            return held.ToArray();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary><c>O_RDONLY | O_NOFOLLOW | O_CLOEXEC</c> as this platform numbers them (Linux on ARM numbers O_NOFOLLOW apart).</summary>
    static int NoFollowReadOnly =>
        OperatingSystem.IsMacOS() ? 0x0100 | 0x01000000
        : (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            is System.Runtime.InteropServices.Architecture.Arm64 or System.Runtime.InteropServices.Architecture.Arm ? 0x8000 : 0x20000) | 0x80000;

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true, EntryPoint = "open")]
    static extern int open([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, int flags);
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

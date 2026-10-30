using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TradeAgent.Core;

/// <summary>
/// THE HEAD-START KIT SOCKET (<c>U-kit-socket</c>; R25 D10): the one folder the owner's own kit of skills and tools is installed into,
/// which every seat is told of and every executor's cell reads and runs from, and which no agent can write. The kit is the OWNER's to
/// build ("i'll build that myself and hand it to you later", 2026-10-30); this is only the socket it plugs into.
///
/// <para><b>The layout an author follows</b> — a folder, or a zip of one (the folder itself or its one top folder):</para>
/// <code>
/// kit.json                 the index: {"kit": 1, "name": "…", "skills": [{"name", "description"}], "tools": [{"name", "description"}]}
/// skills/&lt;name&gt;/SKILL.md  one folder per skill, SKILL.md opening with codex's frontmatter (--- name: &lt;name&gt; / description: … ---);
///                          anything else the skill needs beside it (scripts/, references/)
/// tools/&lt;name&gt;           one program per tool, run by name: the folder is on a cell's PATH; sub-folders are support files
/// README.md                optional, never read by an agent
/// </code>
/// <para>The index and the folders must agree exactly — every listed skill and tool present, nothing present that is not listed — so
/// what the canon names (from the index) is what a seat finds. Names are lower-case letters, digits, '.', '_' and '-', at most 64, not
/// starting with a dot. No link, device or hidden entry anywhere; at most <see cref="MaxFiles"/> files and <see cref="MaxBytes"/> bytes.</para>
///
/// <para><b>Installed only by the app, and only as it was hashed.</b> <see cref="Install"/> copies the source into a staging folder,
/// checks it, makes every folder and file read-only (a top-level tool read and run), hashes the installed tree and swaps it into
/// <c>kit/</c> under the app's home, then appends the act and its hash to <c>state/kit-record.jsonl</c> — under <c>state/</c>, which no
/// cell is ever granted. <see cref="Current"/> grants nothing unless the last recorded act is an install AND the folder still hashes to
/// it: a kit changed after its install (an unconfined seat runs as the owner's user and could write it) is granted to no cell and named
/// in no canon, and the Settings card says so.</para>
/// </summary>
public static class Kit
{
    /// <summary>The index's file name, at the kit's root.</summary>
    public const string IndexFile = "kit.json";

    /// <summary>The skills folder, at the kit's root.</summary>
    public const string SkillsDir = "skills";

    /// <summary>The tools folder, at the kit's root: a cell's PATH names it.</summary>
    public const string ToolsDir = "tools";

    /// <summary>The one optional file beside them, never read by an agent.</summary>
    public const string ReadmeFile = "README.md";

    /// <summary>A skill's own file, in its folder.</summary>
    public const string SkillFile = "SKILL.md";

    /// <summary>The one index form this build reads.</summary>
    public const int IndexVersion = 1;

    /// <summary>The most files a kit may hold.</summary>
    public const int MaxFiles = 5000;

    /// <summary>The most bytes a kit may hold, all files together.</summary>
    public const long MaxBytes = 256L * 1024 * 1024;

    /// <summary>The longest description the index may give a skill or a tool: the canon prints it.</summary>
    public const int MaxDescription = 300;

    /// <summary>The kit's folder under <paramref name="home"/> (default the app's home). Never created here: absent means no kit.</summary>
    public static string Dir(string? home = null) => Path.Combine(home ?? Paths.Home, "kit");

    /// <summary>Where an install is staged and a replaced kit is set aside: beside <see cref="Dir"/>, never inside it.</summary>
    public static string StagingDir(string? home = null) => Path.Combine(home ?? Paths.Home, "kit-staging");

    /// <summary>The record of every install and removal, with its hash: under <c>state/</c>, which no cell reaches.</summary>
    public static string RecordFile(string? home = null) => Path.Combine(home ?? Paths.Home, "state", "kit-record.jsonl");

    /// <summary>The source a kit bundled into a build is recorded under (<see cref="InstallBundled"/>).</summary>
    public const string BundledSource = "bundled with this build";

    /// <summary>Where a build carries a bundled kit: <c>kit/</c> beside the app's own programs.</summary>
    public static string BundledDir => Path.Combine(AppContext.BaseDirectory, "kit");

    static readonly Regex NameRule = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);

    // ---- the index ------------------------------------------------------------------------------------------------------------

    /// <summary>One skill or tool as the index names it.</summary>
    public sealed record Entry(string Name, string Description);

    sealed record WireEntry([property: JsonPropertyName("name")] string? Name, [property: JsonPropertyName("description")] string? Description);

    sealed record WireIndex(
        [property: JsonPropertyName("kit")] int? Kit,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("skills")] WireEntry[]? Skills,
        [property: JsonPropertyName("tools")] WireEntry[]? Tools);

    /// <summary>A checked index: the kit's name, its skills and its tools, each in the index's order.</summary>
    public sealed record Index(string Name, IReadOnlyList<Entry> Skills, IReadOnlyList<Entry> Tools);

    /// <summary>Reads and checks an index's bytes, or says why not.</summary>
    public static (Index? Index, string? Refusal) ReadIndex(byte[] bytes)
    {
        WireIndex? wire;
        try { wire = JsonSerializer.Deserialize<WireIndex>(bytes, new JsonSerializerOptions { AllowTrailingCommas = false }); }
        catch (JsonException ex) { return (null, $"{IndexFile} is not readable JSON ({ex.Message})"); }
        if (wire is null) return (null, $"{IndexFile} is empty");
        if (wire.Kit != IndexVersion) return (null, $"{IndexFile} must say \"kit\": {IndexVersion}");
        if (string.IsNullOrWhiteSpace(wire.Name) || wire.Name.Length > 80 || wire.Name.Any(char.IsControl))
            return (null, $"{IndexFile} must give the kit a \"name\" of 1 to 80 characters");
        var skills = new List<Entry>();
        var tools = new List<Entry>();
        foreach (var (kind, list, into) in new[] { ("skill", wire.Skills, skills), ("tool", wire.Tools, tools) })
            foreach (var e in list ?? [])
            {
                if (e?.Name is not { } name || !NameRule.IsMatch(name))
                    return (null, $"{IndexFile} names a {kind} '{e?.Name}': a name is lower-case letters, digits, '.', '_' or '-', at most 64, not starting with a dot");
                if (string.IsNullOrWhiteSpace(e.Description) || e.Description.Length > MaxDescription || e.Description.Any(char.IsControl))
                    return (null, $"{IndexFile} must give the {kind} '{name}' a one-line \"description\" of 1 to {MaxDescription} characters");
                if (into.Any(x => x.Name == name)) return (null, $"{IndexFile} names the {kind} '{name}' twice");
                into.Add(new Entry(name, e.Description.Trim()));
            }
        return (new Index(wire.Name.Trim(), skills, tools), null);
    }

    // ---- the installed kit ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// WHAT IS INSTALLED, AS THE APP MAY GRANT IT: the kit's folder, its index and its hash — or nothing. A kit that is
    /// <see cref="Changed"/> is granted to nobody.
    /// </summary>
    public sealed record View(string Dir, string Hash, Index Index, string Source, DateTimeOffset At)
    {
        /// <summary>The skills folder, when the kit holds a skill.</summary>
        public string? Skills => Index.Skills.Count > 0 ? Path.Combine(Dir, SkillsDir) : null;

        /// <summary>The tools folder, when the kit holds a tool: a cell's program root and the head of its PATH.</summary>
        public string? Tools => Index.Tools.Count > 0 ? Path.Combine(Dir, ToolsDir) : null;

        /// <summary>One skill's own file.</summary>
        public string SkillPath(string name) => Path.Combine(Dir, SkillsDir, name, SkillFile);

        /// <summary>One tool's program.</summary>
        public string ToolPath(string name) => Path.Combine(Dir, ToolsDir, name);
    }

    /// <summary>The state of the socket: a kit to grant, none, or one that is not granted and why.</summary>
    public sealed record State(View? Installed, string? Problem, Record? Last)
    {
        /// <summary>Nothing has been installed, or the last act removed it.</summary>
        public bool Empty => Installed is null && Problem is null;
    }

    /// <summary>One line of the record: an install or a removal, its hash, the kit's name and where it came from.</summary>
    public sealed record Record(
        [property: JsonPropertyName("at")] DateTimeOffset At,
        [property: JsonPropertyName("act")] string Act,
        [property: JsonPropertyName("hash")] string Hash,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("skills")] string[] Skills,
        [property: JsonPropertyName("tools")] string[] Tools,
        [property: JsonPropertyName("source")] string Source);

    /// <summary>The act a <see cref="Record"/> names for an install.</summary>
    public const string Installed = "installed";

    /// <summary>The act a <see cref="Record"/> names for a removal.</summary>
    public const string Removed = "removed";

    static readonly object Gate = new();
    static readonly Dictionary<string, (string Fingerprint, string Hash)> HashCache = new(StringComparer.Ordinal);

    /// <summary>
    /// THE KIT TO GRANT, CHECKED NOW: the last recorded act an install, the folder present, its index readable and matching its
    /// folders, and the folder hashing to the recorded hash. Anything else grants nothing; the problem says why, in the owner's words.
    /// Never throws.
    /// </summary>
    public static State Current(string? home = null)
    {
        Record? last;
        try { last = Records(home).LastOrDefault(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return new State(null, $"TradeAgent could not read its record of the kit ({ex.Message}), so no seat is given the kit.", null); }
        var dir = Dir(home);
        if (last is null || last.Act != Installed)
            return Directory.Exists(dir) || File.Exists(dir)
                ? new State(null, $"A folder {dir} is there that TradeAgent did not install, so no seat is given it. Add the kit from Settings.", last)
                : new State(null, null, last);
        try
        {
            var (index, problem) = Check(dir);
            if (problem is not null) return new State(null, $"The installed kit is no longer whole ({problem}), so no seat is given it. Add it again from Settings.", last);
            var hash = HashOf(dir);
            if (hash != last.Hash)
                return new State(null, "The kit folder changed after you installed it, so no seat is given it. Add the kit again from Settings.", last);
            return new State(new View(dir, hash, index!, last.Source, last.At), null, last);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new State(null, $"TradeAgent could not read the installed kit ({ex.Message}), so no seat is given it.", last); }
    }

    /// <summary>The kit to grant, or null. The one call every grant reads.</summary>
    public static View? Granted(string? home = null) => Current(home).Installed;

    /// <summary>Every line of the record, oldest first. A missing record is no lines.</summary>
    public static IReadOnlyList<Record> Records(string? home = null)
    {
        var path = RecordFile(home);
        if (!File.Exists(path)) return [];
        return [.. File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<Record>(l)
            ?? throw new JsonException("an empty line in the kit record"))];
    }

    // ---- install and remove ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// INSTALLS THE KIT AT <paramref name="source"/> — a folder, or a <c>.zip</c> of one — for the owner's second press (or the fleet's
    /// bundle): staged, checked, made read-only, hashed, swapped in and recorded. The kit already installed is replaced only once the new
    /// one passed every check; a refusal leaves it as it was. Returns the installed view or the refusal in the owner's words.
    /// </summary>
    public static (View? Installed, string? Refusal) Install(string source, string? home = null, string? recordedSource = null)
    {
        lock (Gate)
        {
            var staging = Path.Combine(StagingDir(home), "in-" + Guid.NewGuid().ToString("n")[..12]);
            try
            {
                Directory.CreateDirectory(staging);
                string? why;
                if (File.Exists(source) && source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) why = Unzip(source, staging);
                else if (Directory.Exists(source)) why = CopyTree(source, staging);
                else why = $"{source} is neither a folder nor a .zip file";
                if (why is not null) return (null, why);

                var root = KitRoot(staging);
                var (index, problem) = Check(root);
                if (problem is not null) return (null, problem);
                SealReadOnly(root);
                var hash = HashOf(root);

                var dir = Dir(home);
                string? aside = null;
                if (Directory.Exists(dir))
                {
                    aside = Path.Combine(StagingDir(home), "old-" + Guid.NewGuid().ToString("n")[..12]);
                    MoveDir(dir, aside);
                }
                else if (File.Exists(dir)) return (null, $"{dir} is a file, not a folder TradeAgent can install a kit into");
                try { MoveDir(root, dir); }
                catch (Exception) when (aside is not null) { MoveDir(aside, dir); throw; }
                var at = DateTimeOffset.UtcNow;
                var src = recordedSource ?? Path.GetFullPath(source);
                Append(home, new Record(at, Installed, hash, index!.Name, [.. index.Skills.Select(s => s.Name)], [.. index.Tools.Select(t => t.Name)], src));
                if (aside is not null) DeleteTree(aside);
                return (new View(dir, hash, index, src, at), null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return (null, $"TradeAgent could not install the kit ({ex.Message}); the kit you had is unchanged.");
            }
            finally
            {
                DeleteTree(staging);
            }
        }
    }

    /// <summary>
    /// REMOVES THE INSTALLED KIT, for the owner's second press: the folder deleted and the removal recorded with the hash it held. No
    /// seat is given a kit after it. Returns the refusal, or null.
    /// </summary>
    public static string? Remove(string? home = null)
    {
        lock (Gate)
        {
            var dir = Dir(home);
            var last = Records(home).LastOrDefault();
            if (last is null || last.Act != Installed)
            {
                // A FOLDER NOBODY INSTALLED is granted to nobody (Current); removing it is still the owner's to ask for.
                if (!Directory.Exists(dir)) return "No kit is installed.";
                DeleteTree(dir);
                return Directory.Exists(dir) ? $"TradeAgent could not remove {dir}." : null;
            }
            try
            {
                if (Directory.Exists(dir))
                {
                    var aside = Path.Combine(StagingDir(home), "old-" + Guid.NewGuid().ToString("n")[..12]);
                    Directory.CreateDirectory(StagingDir(home));
                    MoveDir(dir, aside);
                    DeleteTree(aside);
                }
                Append(home, last with { At = DateTimeOffset.UtcNow, Act = Removed });
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"TradeAgent could not remove the kit ({ex.Message}).";
            }
        }
    }

    /// <summary>
    /// THE FLEET'S BUNDLE (<c>U-kit-socket</c> item 3): a build that carries a kit in <see cref="BundledDir"/> installs it at start —
    /// only while the record holds no act at all, so an owner's own install or removal is never undone by a build. Returns what was
    /// done, for the activity line, or null for nothing.
    /// </summary>
    public static string? InstallBundled(string? home = null, string? bundled = null)
    {
        var from = bundled ?? BundledDir;
        if (!File.Exists(Path.Combine(from, IndexFile))) return null;
        try { if (Records(home).Count > 0) return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
        var (view, refusal) = Install(from, home, BundledSource);
        return view is not null
            ? $"Installed the head-start kit bundled with this build: {view.Index.Name} ({view.Index.Skills.Count} skills, {view.Index.Tools.Count} tools, {view.Hash[..12]})."
            : $"The head-start kit bundled with this build was not installed: {refusal}";
    }

    static void Append(string? home, Record record)
    {
        var path = RecordFile(home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, JsonSerializer.Serialize(record) + "\n", new UTF8Encoding(false));
    }

    // ---- checking a tree ------------------------------------------------------------------------------------------------------

    /// <summary>The folder a staged source holds the kit in: itself, or its one top folder when only that holds an index.</summary>
    static string KitRoot(string staging)
    {
        if (File.Exists(Path.Combine(staging, IndexFile))) return staging;
        var entries = Directory.GetFileSystemEntries(staging);
        return entries.Length == 1 && Directory.Exists(entries[0]) && File.Exists(Path.Combine(entries[0], IndexFile)) ? entries[0] : staging;
    }

    /// <summary>
    /// WHETHER A TREE IS A KIT: its index readable, its root holding only the index, <c>skills/</c>, <c>tools/</c> and a README, every
    /// listed skill a folder with a <c>SKILL.md</c> whose frontmatter codex can read, every listed tool a file, nothing unlisted, no
    /// link, device or hidden entry, and within the bounds. Returns the index or the first problem.
    /// </summary>
    public static (Index? Index, string? Problem) Check(string root)
    {
        var indexPath = Path.Combine(root, IndexFile);
        if (!File.Exists(indexPath)) return (null, $"there is no {IndexFile} at the kit's top");
        if (Problem(root) is { } bad) return (null, bad);
        var (index, why) = ReadIndex(File.ReadAllBytes(indexPath));
        if (why is not null) return (null, why);

        foreach (var entry in Directory.GetFileSystemEntries(root))
        {
            var name = Path.GetFileName(entry);
            var ok = name switch
            {
                IndexFile or ReadmeFile => File.Exists(entry),
                SkillsDir or ToolsDir => Directory.Exists(entry),
                _ => false
            };
            if (!ok) return (null, $"the kit's top holds '{name}': only {IndexFile}, {SkillsDir}/, {ToolsDir}/ and {ReadmeFile} belong there");
        }

        var skillsDir = Path.Combine(root, SkillsDir);
        var present = Directory.Exists(skillsDir) ? Directory.GetFileSystemEntries(skillsDir).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal) : [];
        foreach (var s in index!.Skills)
        {
            var file = Path.Combine(skillsDir, s.Name, SkillFile);
            if (!Directory.Exists(Path.Combine(skillsDir, s.Name)) || !File.Exists(file))
                return (null, $"{IndexFile} lists the skill '{s.Name}', and {SkillsDir}/{s.Name}/{SkillFile} is not there");
            if (FrontmatterProblem(File.ReadAllText(file), s.Name) is { } fm) return (null, $"{SkillsDir}/{s.Name}/{SkillFile}: {fm}");
        }
        if (present.FirstOrDefault(p => !index.Skills.Any(s => s.Name == p)) is { } extraSkill)
            return (null, $"{SkillsDir}/ holds '{extraSkill}', which {IndexFile} does not list");

        var toolsDir = Path.Combine(root, ToolsDir);
        foreach (var t in index.Tools)
            if (!File.Exists(Path.Combine(toolsDir, t.Name)))
                return (null, $"{IndexFile} lists the tool '{t.Name}', and {ToolsDir}/{t.Name} is not a file there");
        if (Directory.Exists(toolsDir) && Directory.GetFiles(toolsDir).Select(Path.GetFileName).FirstOrDefault(f => !index.Tools.Any(t => t.Name == f)) is { } extraTool)
            return (null, $"{ToolsDir}/ holds the program '{extraTool}', which {IndexFile} does not list");
        return (index, null);
    }

    /// <summary>Codex reads a skill from YAML frontmatter between two <c>---</c> lines with a description, and a name defaulting to its folder's.</summary>
    static string? FrontmatterProblem(string text, string folder)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---") return "it must open with a '---' line (codex's frontmatter)";
        var end = Array.FindIndex(lines, 1, l => l.Trim() == "---");
        if (end < 0) return "its frontmatter has no closing '---' line";
        string? Field(string key) => lines[1..end].Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(key + ":", StringComparison.Ordinal))?[(key.Length + 1)..].Trim().Trim('"', '\'');
        if (string.IsNullOrWhiteSpace(Field("description"))) return "its frontmatter has no 'description:'";
        if (Field("name") is { Length: > 0 } name && name != folder) return $"its frontmatter names it '{name}', not '{folder}' (its folder)";
        return null;
    }

    /// <summary>The first entry under <paramref name="root"/> no kit may hold — a link, a device, a hidden or ill-named entry — or the bounds broken.</summary>
    static string? Problem(string root)
    {
        long bytes = 0;
        var files = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false }))
        {
            var rel = Rel(root, path);
            var info = new FileInfo(path);
            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                return $"'{rel}' is a link: a kit holds only real files and folders";
            var name = Path.GetFileName(path);
            if (name.StartsWith('.') || name.Any(char.IsControl) || name.Length > 128)
                return $"'{rel}' is hidden or has a name a kit may not hold";
            if ((info.Attributes & FileAttributes.Directory) != 0) continue;
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.SetUser | UnixFileMode.SetGroup)) != 0)
                return $"'{rel}' is set-user or set-group: a kit may not hold one";
            if (!File.Exists(path)) return $"'{rel}' is not a regular file";
            if (++files > MaxFiles) return $"the kit holds more than {MaxFiles} files";
            bytes += info.Length;
            if (bytes > MaxBytes) return $"the kit is larger than {MaxBytes / (1024 * 1024)} MiB";
        }
        return null;
    }

    static string Rel(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>Copies a folder, refusing a link anywhere in it (checked again on the copy).</summary>
    static string? CopyTree(string source, string into)
    {
        if (new DirectoryInfo(source).LinkTarget is not null) return $"{source} is a link, not a folder";
        if (Problem(source) is { } bad) return bad;
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(into, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(into, Path.GetRelativePath(source, file));
            File.Copy(file, to);
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(file) & UnixFileMode.UserExecute) != 0)
                File.SetUnixFileMode(to, File.GetUnixFileMode(to) | UnixFileMode.UserExecute);
        }
        return null;
    }

    /// <summary>Unpacks a zip, refusing an entry that climbs out, is absolute, is a link, or breaks the bounds.</summary>
    static string? Unzip(string zip, string into)
    {
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count > MaxFiles * 2) return $"the zip holds more than {MaxFiles * 2} entries";
        long total = 0;
        var full = Path.GetFullPath(into) + Path.DirectorySeparatorChar;
        foreach (var e in archive.Entries)
        {
            var name = e.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Split('/').Any(p => p == "..") || Path.IsPathRooted(name) || name.Contains(':'))
                return $"the zip entry '{e.FullName}' would land outside the kit";
            var unixType = (e.ExternalAttributes >> 16) & 0xF000;
            if (unixType is 0xA000) return $"the zip entry '{e.FullName}' is a link: a kit holds only real files and folders";
            var to = Path.GetFullPath(Path.Combine(into, name));
            if (!to.StartsWith(full, StringComparison.Ordinal) && to + Path.DirectorySeparatorChar != full)
                return $"the zip entry '{e.FullName}' would land outside the kit";
            if (name.EndsWith('/')) { Directory.CreateDirectory(to); continue; }
            total += e.Length;
            if (total > MaxBytes) return $"the kit is larger than {MaxBytes / (1024 * 1024)} MiB";
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            e.ExtractToFile(to, overwrite: false);
            if (!OperatingSystem.IsWindows() && (((e.ExternalAttributes >> 16) & 0x40) != 0))
                File.SetUnixFileMode(to, File.GetUnixFileMode(to) | UnixFileMode.UserExecute);
        }
        return null;
    }

    /// <summary>
    /// Moves a sealed folder: a folder moved to another parent rewrites its own <c>..</c>, which Linux allows only with write on it,
    /// so its owner may write it for the move and it is sealed again at once.
    /// </summary>
    static void MoveDir(string from, string to)
    {
        if (OperatingSystem.IsWindows()) { Directory.Move(from, to); return; }
        var mode = File.GetUnixFileMode(from);
        File.SetUnixFileMode(from, mode | UnixFileMode.UserWrite);
        Directory.Move(from, to);
        File.SetUnixFileMode(to, mode);
    }

    const UnixFileMode ReadOnlyDir = UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                     | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    const UnixFileMode ReadOnlyFile = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    /// <summary>
    /// Whether a file of the kit is run: a top-level tool always, any other file when its source marked it runnable (a skill's
    /// script). Read off the installed tree, so the hash and the check agree.
    /// </summary>
    static bool Runs(string root, string file)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(file)) == Path.GetFullPath(Path.Combine(root, ToolsDir))) return true;
        return !OperatingSystem.IsWindows() && (File.GetUnixFileMode(file) & UnixFileMode.UserExecute) != 0;
    }

    /// <summary>Every folder and file read-only for everyone, owner included; a file that <see cref="Runs"/> read and run.</summary>
    static void SealReadOnly(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.ReadOnly);
            return;
        }
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetUnixFileMode(f, Runs(root, f) ? ReadOnlyDir : ReadOnlyFile);
        foreach (var d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            File.SetUnixFileMode(d, ReadOnlyDir);
        File.SetUnixFileMode(root, ReadOnlyDir);
    }

    /// <summary>Deletes a tree the kit made read-only: writable again first. Never throws.</summary>
    static void DeleteTree(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            foreach (var d in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories).Prepend(path))
            {
                if (OperatingSystem.IsWindows()) continue;
                File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* litter beside the kit, never in it */ }
    }

    /// <summary>
    /// THE KIT'S HASH: SHA-256 over one line per folder (<c>d &lt;path&gt;</c>) and per file (<c>f &lt;path&gt; &lt;x|r&gt; &lt;sha256&gt;</c>),
    /// paths relative with '/', in ordinal order. Whether a file runs is part of it. Cached against each file's size and write time, so a
    /// grant rereads the bytes only when something under the kit moved.
    /// </summary>
    public static string HashOf(string root)
    {
        var entries = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(p => (Rel: Rel(root, p), Path: p, Info: new FileInfo(p)))
            .OrderBy(e => e.Rel, StringComparer.Ordinal).ToList();
        var fingerprint = string.Join("\n", entries.Select(e => (e.Info.Attributes & FileAttributes.Directory) != 0
            ? "d " + e.Rel
            : $"f {e.Rel} {(Runs(root, e.Path) ? 'x' : 'r')} {e.Info.Length} {e.Info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)}"));
        var key = Path.GetFullPath(root);
        lock (HashCache)
            if (HashCache.TryGetValue(key, out var hit) && hit.Fingerprint == fingerprint) return hit.Hash;

        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            if ((e.Info.Attributes & FileAttributes.Directory) != 0) { sb.Append("d ").Append(e.Rel).Append('\n'); continue; }
            using var s = File.OpenRead(e.Path);
            sb.Append("f ").Append(e.Rel).Append(' ').Append(Runs(root, e.Path) ? 'x' : 'r').Append(' ').Append(Sha256Hex.Of(s)).Append('\n');
        }
        var hash = Sha256Hex.Of(sb.ToString());
        lock (HashCache) HashCache[key] = (fingerprint, hash);
        return hash;
    }
}

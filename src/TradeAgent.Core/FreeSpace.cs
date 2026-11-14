using System.Text.RegularExpressions;

namespace TradeAgent.Core;

/// <summary>What one thing TradeAgent may let go is: the three kinds of the allowlist, and nothing else.</summary>
public enum LetGoKind
{
    /// <summary>
    /// An older home that Start fresh moved aside whole (<see cref="OlderHome.SetAside(string, DateTimeOffset)"/>): one this build refuses to open.
    /// It holds that older version's own records, which go with it; the words say so.
    /// </summary>
    OlderHome,

    /// <summary>A downloaded TradeAgent installer for the version running now or an older one: it has already been run.</summary>
    Installer,

    /// <summary>A leftover folder of a check of the AI's sealed workspace, with its spare copy of the test program, untouched for an hour or more.</summary>
    CheckLeftover
}

/// <summary>One file of an item, as press one saw it.</summary>
public sealed record LetGoFile(string Path, long Length, DateTime WrittenUtc);

/// <summary>
/// One thing press one listed: its kind, its folder, and every file and folder in it as they were — what press two removes, and the only thing.
/// <paramref name="Folders"/> holds the item's own folder and every folder under it.
/// </summary>
public sealed record LetGoItem(LetGoKind Kind, string Path, IReadOnlyList<LetGoFile> Files, IReadOnlyList<string> Folders, DateTime NewestUtc)
{
    public long Bytes => Files.Sum(f => f.Length);
}

/// <summary>What press one showed: the items and their total.</summary>
public sealed record FreeSpacePlan(IReadOnlyList<LetGoItem> Items, DateTimeOffset At)
{
    public long Bytes => Items.Sum(i => i.Bytes);
}

/// <summary>Why press two left an item where it was.</summary>
public enum LetGoSkip
{
    /// <summary>It is no longer one of the kinds TradeAgent may let go, or it is no longer where press one saw it.</summary>
    NoLongerLettable,

    /// <summary>A file or folder in it was added, removed, grew or was rewritten since press one.</summary>
    Changed,

    /// <summary>It is, or now holds, a link: nothing is followed through one.</summary>
    HoldsLink,

    /// <summary>A leftover check folder written to within the hour (a check may be using it), or an older home a TradeAgent is running on.</summary>
    InUse
}

/// <summary>
/// What press two did: the items removed, those left and why, the bytes freed, and the files of a removed item left in place because they
/// changed under the removal itself (0 on an ordinary press).
/// </summary>
public sealed record FreeSpaceDone(IReadOnlyList<LetGoItem> Removed, IReadOnlyList<(LetGoItem Item, LetGoSkip Why)> Skipped, long Freed, int FilesKept);

/// <summary>
/// Where the allowlist looks: the home this app runs in, and which downloaded versions have been installed (the running version or an
/// older one: the app hands in the update service's own comparison, so a version's order is decided in one place).
/// </summary>
public sealed record FreeSpaceRoots(string Home, Func<string, bool> Installed);

/// <summary>
/// THE "FREE SPACE" PRESS'S RULE (<c>U-free-space</c>): what TradeAgent itself put on this disk and may let go, and the removal of exactly that.
///
/// <para><b>An allowlist of kinds, never a denylist.</b> <see cref="MayLetGo"/> is the one function that decides, and it names three shapes,
/// each by its exact folder and name: an older home beside this one that Start fresh set aside, a downloaded installer of a version already
/// installed, a check's leftover folder. Anything else — the records, the tape, the ledgers, datasets, reports, evidence, the kit, a seat's
/// sign-in or home, the workspace, a tape copy a Start fresh carries, a newer download, anything not named so — is not a kind, so it is never
/// listed: a new folder the app grows tomorrow is kept until someone adds it here on purpose.</para>
///
/// <para><b>Never through a link, never out of its folder.</b> A path is taken only as it is spelled in full (no "..", nothing relative), its
/// folder must be exactly the kind's own, that folder and the item must not be links, and an item that holds a link anywhere is not listed.
/// A walk never follows one.</para>
///
/// <para><b>Press two removes what press one showed and nothing that changed since.</b> Each item is classed again, walked again, and
/// compared with what press one saw, file by file; an item that differs in any way is left whole and said. The removal itself deletes
/// only the files press one listed, each still as it was, and then the folders that are empty: a file that arrives during it survives.</para>
///
/// <para><b>On Linux the kit's releases under /opt are root's and are not listed</b>: a release the app cannot remove is not offered.</para>
///
/// <para>In-process only: the owner's two presses on the Dashboard or in Settings reach this, and no verb or pipe op does.</para>
/// </summary>
public static class FreeSpace
{
    /// <summary>The home's folder for downloaded installers (<see cref="Paths.Updates"/>).</summary>
    public const string UpdatesFolder = "updates";

    /// <summary>The home's folder the checks of the AI's sealed workspace work in (the cell benches').</summary>
    public const string BenchFolder = "cell-bench";

    /// <summary>How long a check's folder must have been untouched before it counts as left over.</summary>
    public static readonly TimeSpan BenchQuiet = TimeSpan.FromHours(1);

    /// <summary>A release's folder name, as the update service writes it: <c>0.2.0</c> or <c>0.2.0-8da84a12ff6e</c>.</summary>
    static readonly Regex VersionName = new(@"^\d{1,9}\.\d{1,9}\.\d{1,9}(-[0-9A-Za-z.]{1,64})?$", RegexOptions.CultureInvariant);

    /// <summary>A check's own folder name: twelve hex digits of a GUID.</summary>
    static readonly Regex BenchName = new("^[0-9a-f]{12}$", RegexOptions.CultureInvariant);

    static readonly StringComparison PathCase = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>An older home's name beside <paramref name="homeName"/>: <c>TradeAgent-from-0.1.2-20261031-101500</c> (<see cref="OlderHome"/>).</summary>
    static Regex OlderHomeName(string homeName) =>
        new($"^{Regex.Escape(homeName)}-from-[0-9A-Za-z.-]{{1,64}}-\\d{{8}}-\\d{{6}}$", RegexOptions.CultureInvariant | (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : 0));

    /// <summary>
    /// THE ALLOWLIST: the kind of <paramref name="path"/>, or null when it is not one TradeAgent may let go. Never throws: a path that cannot be
    /// read is not one. It says nothing of what is inside the folder; <see cref="Inventory"/> and <see cref="Remove"/> walk that, and refuse a link there.
    /// </summary>
    public static LetGoKind? MayLetGo(string path, FreeSpaceRoots roots)
    {
        try
        {
            if (!Spelled(path, out var full)) return null;
            var home = Path.GetFullPath(roots.Home).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // NEVER THE HOME, NOR A FOLDER THE HOME IS IN.
            if (string.Equals(full, home, PathCase) || home.StartsWith(full + Path.DirectorySeparatorChar, PathCase)) return null;
            if (IsLink(full) || !Directory.Exists(full)) return null;
            var parent = Path.GetDirectoryName(full);
            var name = Path.GetFileName(full);
            if (parent is null) return null;

            var updates = Path.Combine(home, UpdatesFolder);
            if (string.Equals(parent, updates, PathCase) && !IsLink(updates) && VersionName.IsMatch(name) && roots.Installed(name))
                return LetGoKind.Installer;

            var bench = Path.Combine(home, BenchFolder);
            if (string.Equals(parent, bench, PathCase) && !IsLink(bench) && BenchName.IsMatch(name))
                return LetGoKind.CheckLeftover;

            // AN OLDER HOME: beside this one, named as Start fresh names it, and a home — its database where a home keeps one, not behind a link.
            if (string.Equals(parent, Path.GetDirectoryName(home), PathCase) && OlderHomeName(Path.GetFileName(home)).IsMatch(name)
                && !IsLink(Path.Combine(full, "state")) && File.Exists(Path.Combine(full, "state", "tradeagent.db")))
                return LetGoKind.OlderHome;

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary>
    /// PRESS ONE: every item of the three kinds, each walked whole without following a link, with its size and when it was last written.
    /// Read-only. An item that holds a link, cannot be read whole, or (a check's folder) was written within <see cref="BenchQuiet"/> is not listed.
    /// </summary>
    public static FreeSpacePlan Inventory(FreeSpaceRoots roots, DateTimeOffset now)
    {
        var items = new List<LetGoItem>();
        var home = Path.GetFullPath(roots.Home).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var candidate in Candidates(home))
        {
            if (MayLetGo(candidate, roots) is not { } kind) continue;
            if (Walk(candidate) is not { } seen) continue;
            var item = new LetGoItem(kind, Path.GetFullPath(candidate), seen.Files, seen.Folders, seen.Newest);
            if (Young(item, now)) continue;
            items.Add(item);
        }
        return new FreeSpacePlan(items, now);
    }

    /// <summary>The folders the three kinds can be: beside the home, under its updates folder and under its checks' folder. The allowlist decides.</summary>
    static IEnumerable<string> Candidates(string home)
    {
        foreach (var dir in new[] { Path.GetDirectoryName(home), Path.Combine(home, UpdatesFolder), Path.Combine(home, BenchFolder) })
        {
            if (dir is null || !Directory.Exists(dir) || IsLink(dir)) continue;
            string[] found;
            try { found = Directory.GetDirectories(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var d in found.Order(StringComparer.Ordinal)) yield return d;
        }
    }

    /// <summary>
    /// PRESS TWO: removes the items of <paramref name="plan"/> that are still exactly as press one saw them, and nothing else. Each item is
    /// classed again by <see cref="MayLetGo"/> and walked again; one that is no longer a kind, holds a link, changed, or (a check's folder) was
    /// written within the hour is left whole and returned with why. Never throws for a file it could not remove: that file stays, counted.
    /// </summary>
    public static FreeSpaceDone Remove(FreeSpacePlan plan, FreeSpaceRoots roots, DateTimeOffset now)
    {
        var removed = new List<LetGoItem>();
        var skipped = new List<(LetGoItem, LetGoSkip)>();
        long freed = 0;
        var kept = 0;
        foreach (var item in plan.Items)
        {
            if (MayLetGo(item.Path, roots) != item.Kind || !Spelled(item.Path, out var full) || !string.Equals(full, item.Path, StringComparison.Ordinal))
            {
                skipped.Add((item, LetGoSkip.NoLongerLettable));
                continue;
            }
            if (Walk(full) is not { } seen) { skipped.Add((item, LetGoSkip.HoldsLink)); continue; }
            if (!Same(seen, item)) { skipped.Add((item, LetGoSkip.Changed)); continue; }
            if (Young(item, now) || item.Kind == LetGoKind.OlderHome && Running(full)) { skipped.Add((item, LetGoSkip.InUse)); continue; }

            var (bytes, left) = Delete(item);
            freed += bytes;
            kept += left;
            removed.Add(item);
        }
        return new FreeSpaceDone(removed, skipped, freed, kept);
    }

    /// <summary>
    /// WHETHER A TRADEAGENT HOLDS THIS OLDER HOME'S INSTANCE LOCK NOW, as <see cref="OlderHome"/>'s own press asks it: one that cannot be taken
    /// is a running app, and its home is not removed under it. A lock that cannot be read for another reason counts as held.
    /// </summary>
    static bool Running(string home)
    {
        var file = Path.Combine(home, "state", "gateway.lock");
        if (!File.Exists(file)) return false;
        try { using var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>A check's folder written to within <see cref="BenchQuiet"/>: a check may be working in it.</summary>
    static bool Young(LetGoItem item, DateTimeOffset now) => item.Kind == LetGoKind.CheckLeftover && now.UtcDateTime - item.NewestUtc < BenchQuiet;

    /// <summary>What press one saw, compared with a walk now: the same files at the same sizes and times, and the same folders.</summary>
    static bool Same((List<LetGoFile> Files, List<string> Folders, DateTime Newest) seen, LetGoItem item) =>
        seen.Files.Count == item.Files.Count && seen.Folders.Count == item.Folders.Count
        && seen.Files.ToHashSet().SetEquals(item.Files) && seen.Folders.ToHashSet(StringComparer.Ordinal).SetEquals(item.Folders);

    /// <summary>
    /// The removal of one item, after <see cref="Same"/>: each listed file that is still inside the item, not a link, and as it was, then each
    /// listed folder that is empty, deepest first. Returns the bytes removed and the files left.
    /// </summary>
    static (long Bytes, int Left) Delete(LetGoItem item)
    {
        long bytes = 0;
        var left = 0;
        foreach (var f in item.Files)
        {
            try
            {
                var info = new FileInfo(f.Path);
                if (!Within(f.Path, item.Path) || !info.Exists || Linked(f.Path, item.Path) || info.Length != f.Length || info.LastWriteTimeUtc != f.WrittenUtc)
                {
                    left++;
                    continue;
                }
                if (info.IsReadOnly) info.IsReadOnly = false;
                info.Delete();
                bytes += f.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { left++; }
        }
        foreach (var d in item.Folders.OrderByDescending(d => d.Length))
        {
            try
            {
                if ((d == item.Path || Within(d, item.Path)) && !Linked(d, item.Path)) Directory.Delete(d, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* not empty: what is in it stays */ }
        }
        return (bytes, left);
    }

    /// <summary>
    /// A WALK THAT NEVER FOLLOWS A LINK: every file and folder under <paramref name="dir"/>, or null when any entry is a link (a symbolic link,
    /// a junction, any reparse point) or the walk could not read everything — an item seen in part is not an item.
    /// </summary>
    static (List<LetGoFile> Files, List<string> Folders, DateTime Newest)? Walk(string dir)
    {
        var files = new List<LetGoFile>();
        var folders = new List<string>();
        var newest = DateTime.MinValue;
        var options = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false, ReturnSpecialDirectories = false };
        var todo = new Stack<string>();
        todo.Push(dir);
        try
        {
            while (todo.Count > 0)
            {
                var d = todo.Pop();
                var info = new DirectoryInfo(d);
                if (IsLink(info)) return null;
                folders.Add(info.FullName);
                if (info.LastWriteTimeUtc > newest) newest = info.LastWriteTimeUtc;
                foreach (var e in info.EnumerateFileSystemInfos("*", options))
                {
                    if (IsLink(e)) return null;
                    if (e is DirectoryInfo sub) { todo.Push(sub.FullName); continue; }
                    if (e is not FileInfo file) return null;
                    files.Add(new LetGoFile(file.FullName, file.Length, file.LastWriteTimeUtc));
                    if (file.LastWriteTimeUtc > newest) newest = file.LastWriteTimeUtc;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
        return (files, folders, newest);
    }

    /// <summary>A path taken only as spelled in full: absolute, no "." or ".." part, and the same once the system has resolved it.</summary>
    static bool Spelled(string path, out string full)
    {
        full = "";
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        if (path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "." or "..")) return false;
        full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(full, path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), PathCase);
    }

    /// <summary>Whether <paramref name="path"/>, spelled in full, lies inside <paramref name="folder"/>.</summary>
    static bool Within(string path, string folder) =>
        Spelled(path, out var full) && full.StartsWith(folder + Path.DirectorySeparatorChar, PathCase);

    /// <summary>Whether <paramref name="path"/> or any folder between it and <paramref name="root"/> is a link now.</summary>
    static bool Linked(string path, string root)
    {
        for (var p = path; p is not null && p.Length >= root.Length; p = Path.GetDirectoryName(p))
            if (IsLink(p)) return true;
        return false;
    }

    /// <summary>Whether <paramref name="path"/> itself is a link, read without following it. A path that is not there is not one; one that cannot be read is.</summary>
    static bool IsLink(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return (info.Exists || info.LinkTarget is not null) && IsLink(info);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
        // A PATH WHOSE KIND CANNOT BE READ IS TAKEN FOR A LINK: the answer that lists and removes nothing through it.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>A reparse point of any kind (a symbolic link, a junction, a mount point) or anything with a link target.</summary>
    static bool IsLink(FileSystemInfo info) => info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null;
}

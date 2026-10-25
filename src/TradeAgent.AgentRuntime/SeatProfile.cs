using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Db;

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
    /// every folder and every file below the root — whatever the CLI made there since — is left with no access entry of its own, and
    /// the files that hold its credential (<see cref="SeatPlan.PrivateFiles"/>) and its settings (<see cref="SeatPlan.ConfigFile"/>)
    /// are made 0600, because an access entry on a file outlives any change to its folder's. A link met on the walk is never followed
    /// (its target is not the seat home); a credential file that is a link is refused like a folder.</para>
    /// </summary>
    public static void Prepare(SeatPlan seat)
    {
        var root = Path.GetFullPath(seat.Root);
        // EVERY SEAT FILE PATH LOOKED AT BEFORE ANYTHING IS CHANGED, ANYWHERE (the review's P2, U-seat-acl; CRED-001): they are manifest
        // data a runtimes.json entry replaces whole, and one leading out of the seat home would otherwise have its mode — on Windows its
        // list — changed before its folder was looked at. The owner's own files are never touched.
        foreach (var rel in seat.PrivateFiles) Inside(root, rel, "private_files");
        Inside(root, seat.ConfigFile, "config_file");
        Inside(root, seat.ModelCatalog, "model_catalog");
        // EVERY COMPONENT OF EVERY PATH THE SEAT NAMES — its folders, and the folders of its credential and settings files — LOOKED AT
        // BEFORE ANY CHANGE (the re-review's P2, U-seat-acl): a link on the way is refused while the root's mode, the walk and every list
        // are still as they were. The root looked at first, its owner read, never changed: a walk never reads a tree another account owns.
        var folders = new List<string> { root };
        foreach (var (name, value) in seat.ResolvedEnvironment())
            if (seat.Environment[name].Contains("{seatHome}", StringComparison.Ordinal)) folders.Add(Path.GetFullPath(value));
        foreach (var folder in folders) Look(root, folder);
        NotForeign(root);
        foreach (var file in SeatFiles(root, seat))
        {
            Look(root, Path.GetDirectoryName(file)!);
            NotALink(file);
        }
        // THE WHOLE HOME READ, AND EVERY FILE IT WOULD CHANGE LOOKED AT, BEFORE ANY CHANGE (the re-review's P1, U-seat-acl; CRED-001): a
        // file with a second name may be the owner's own, and is refused while nothing is changed yet.
        var below = Below(root);
        foreach (var file in SeatFiles(root, seat)) NoSecondName(file);

        foreach (var folder in folders) RealFolder(root, folder);
        PrivateBelow(root, seat, below);

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

        // THE PINNED MODEL'S CATALOG, WRITTEN BY THE APP BEFORE EVERY START (U-cell-model-catalog) exactly as the config is: the shipped
        // bytes, only when they hash to the pin. A missing or altered shipped file writes nothing here — the CLI class never reads the
        // catalog — and every verified seat refuses on it in words (VerifyAsync).
        if (seat.CatalogPath is { } catalog && ShippedCatalog(seat).Bytes is { } shipped)
        {
            RealFolder(root, Path.GetDirectoryName(catalog)!);
            NotALink(catalog);
            if (!SameBytes(catalog, shipped)) OwnerOnlyFile.Write(catalog, shipped);
        }
    }

    /// <summary>
    /// <paramref name="folder"/> under <paramref name="root"/>, every component from the root down a real folder: none a link, each
    /// missing one made alone and looked at again.
    /// </summary>
    static void RealFolder(string root, string folder)
    {
        var rel = Relative(root, folder);
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
            if (File.Exists(at)) throw FileWhereFolder(at);
            Directory.CreateDirectory(at);
            NotALink(at);
            OwnedHere(at, root);
        }
    }

    /// <summary>
    /// <paramref name="folder"/> under <paramref name="root"/> LOOKED AT, NOTHING MADE OR CHANGED: every component from the root that is
    /// there is a real folder, none a link or a file; the first missing one ends the look (<see cref="RealFolder"/> makes the rest).
    /// </summary>
    static void Look(string root, string folder)
    {
        var rel = Relative(root, folder);
        var at = root;
        var parts = rel == "." ? [] : rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts.Prepend(""))
        {
            if (part.Length > 0) at = Path.Combine(at, part);
            NotALink(at);
            if (Directory.Exists(at)) continue;
            if (File.Exists(at)) throw FileWhereFolder(at);
            return;
        }
    }

    /// <summary><paramref name="folder"/> relative to <paramref name="root"/> — or the refusal, when it is outside it.</summary>
    static string Relative(string root, string folder)
    {
        var rel = Path.GetRelativePath(root, folder);
        if (rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(rel))
            throw new TradeAgentException(ErrorCode.AI_SEAT_REFUSED,
                $"TradeAgent will not use {folder} for the AI: it is outside the AI's own home {root}. This is TradeAgent's own " +
                "settings at fault; nothing was written or started.");
        return rel;
    }

    static TradeAgentException FileWhereFolder(string at) =>
        new(ErrorCode.AI_SEAT_REFUSED,
            $"TradeAgent will not use the AI's own home while {at} is a file where its folder belongs. Nothing was written " +
            $"or started. Remove {at} and TradeAgent makes the folder there.");

    /// <summary>
    /// EVERY FOLDER AND EVERY FILE <see cref="Below"/> READ, PARENTS FIRST, MADE OWNER-ONLY, AND THE CREDENTIAL AND SETTINGS FILES MADE
    /// 0600. A FILE keeps no entry of its own (the review's P1, U-seat-acl; CRED-001): on Windows an explicit entry on an existing
    /// session transcript outlives its folder's protected list, and traverse checking is bypassed by default, so it would stay readable
    /// by path; on macOS an extended entry is cleared as on a folder. Each file's list is read and written only when it is not so. Linux
    /// files are left as they are: their 0700 folders let no other account reach them.
    /// </summary>
    static void PrivateBelow(string root, SeatPlan seat, (List<string> Folders, List<string> Files) below)
    {
        foreach (var folder in below.Folders) OwnedHere(folder, root);
        foreach (var file in below.Files) NoEntryOfItsOwn(file);

        foreach (var (rel, key) in SeatFileEntries(seat))
        {
            var path = Inside(root, rel, key)!;
            // EVERY COMPONENT FROM THE ROOT A REAL FOLDER, NOT ONLY THE LAST: a linked folder on the way would carry the change out.
            if (Path.GetDirectoryName(path) is { } parent && Directory.Exists(parent)) RealFolder(root, parent);
            NotALink(path);
            if (File.Exists(path)) PrivateFile(path);
        }
    }

    /// <summary>
    /// THE SEAT HOME READ, NOTHING CHANGED: every folder below <paramref name="root"/>, parents first, and — on macOS and Windows, where
    /// a preparation changes a file's own entries — every file, each looked at by <see cref="NoSecondName"/>. A folder or file that is a
    /// link, a junction or another reparse point is stepped over, never entered or changed: what it points to is not the seat home.
    /// Nothing when the root is not there yet. Measured on the dev Mac: a whole preparation took 67-105 ms over a home of 377 folders and
    /// 4,032 session files (54-72 ms before each file's link count was read; 17-19 ms when the walk listed folders only).
    /// </summary>
    static (List<string> Folders, List<string> Files) Below(string root)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false };
        var files = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        var read = (Folders: new List<string>(), Files: new List<string>());
        if (!Directory.Exists(root)) return read;
        var folders = new Stack<string>();
        folders.Push(root);
        while (folders.Count > 0)
        {
            var at = folders.Pop();
            List<string> below;
            try { below = [.. files ? Directory.EnumerateFileSystemEntries(at, "*", options) : Directory.EnumerateDirectories(at, "*", options)]; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw NotPrivate(at, ex.Message); }
            foreach (var entry in below)
            {
                if (IsLink(entry)) continue;
                if (Directory.Exists(entry))
                {
                    read.Folders.Add(entry);
                    folders.Push(entry);
                }
                else if (files)
                {
                    NoSecondName(entry);
                    read.Files.Add(entry);
                }
            }
        }
        return read;
    }

    /// <summary>The seat's credential files, its settings file and its model catalog, each with the manifest key that names it.</summary>
    static IEnumerable<(string Rel, string Key)> SeatFileEntries(SeatPlan seat) =>
        seat.PrivateFiles.Select(rel => (rel, "private_files")).Append((seat.ConfigFile, "config_file")).Append((seat.ModelCatalog, "model_catalog"))
            .Where(e => !string.IsNullOrEmpty(e.Item1));

    /// <summary>Those files' full paths below <paramref name="root"/> (each already checked by <see cref="Inside"/>).</summary>
    static IEnumerable<string> SeatFiles(string root, SeatPlan seat) =>
        SeatFileEntries(seat).Select(e => Inside(root, e.Rel, e.Key)!);

    /// <summary>
    /// REFUSES A FILE WITH A SECOND NAME (the re-review's P1, U-seat-acl; CRED-001). A hard link is no link: it is the same file under
    /// another name, which may be outside the seat home — a same-user process can give the owner's own <c>~/.codex/auth.json</c> a name
    /// in <c>sessions/</c> — and a mode or list is the file's, not the name's, so changing it here would change his. Its link count is
    /// read without following a link (<c>lstat</c> on macOS, <c>statx</c> on Linux, a reparse-point handle on Windows); above one, the
    /// seat is refused in words naming it and the repair, never skipped, which would leave its foreign entry in the seat home. A file
    /// that is gone is fine; one whose count cannot be read is refused.
    /// </summary>
    static void NoSecondName(string path)
    {
        var links = LinkCount(path);
        if (links is null or <= 1) return;
        throw new TradeAgentException(ErrorCode.AI_SEAT_REFUSED,
            $"TradeAgent will not use the AI's own home while {path} is a hard link: the same file has {links} names, and another may be " +
            "outside the AI's own home — your own Codex sign-in, for one — so making it private here would change that file too. " +
            $"Nothing was written or started, and nothing was changed. Remove {path}; removing this name leaves the file under its other " +
            "names, and TradeAgent makes its own in its place.");
    }

    /// <summary>How many names <paramref name="path"/> has, looked at without following a link; null when it is not there.</summary>
    static uint? LinkCount(string path) => OperatingSystem.IsWindows() ? WindowsLinkCount(path) : UnixStat(path)?.Links;

    /// <summary>
    /// LINUX AND macOS: <paramref name="path"/>'s link count and owner, looked at without following a link; null when it is not there,
    /// and the refusal in words when the system will not say.
    /// </summary>
    static (uint Links, uint Uid)? UnixStat(string path)
    {
        var buf = new byte[512];
        int r;
        if (OperatingSystem.IsMacOS())
            // struct stat with 64-bit inodes (sys/stat.h): st_nlink uint16 at 6, st_uid uint32 at 16. arm64's lstat has that layout;
            // x86-64's plain lstat is the old 32-bit one, its 64-bit form lstat$INODE64.
            r = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64
                ? lstat_inode64(path, buf) : lstat(path, buf);
        else
            // struct statx is the same on every architecture: stx_nlink uint32 at 16, stx_uid uint32 at 20.
            r = statx(AtFdCwd, path, AtSymlinkNoFollow, StatxNlink | StatxUid, buf);
        if (r != 0)
        {
            var errno = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            if (errno == ENOENT) return null;
            throw NotPrivate(path, Errno(OperatingSystem.IsMacOS() ? "lstat" : "statx", errno));
        }
        return OperatingSystem.IsMacOS()
            ? (BitConverter.ToUInt16(buf, 6), BitConverter.ToUInt32(buf, 16))
            : (BitConverter.ToUInt32(buf, 16), BitConverter.ToUInt32(buf, 20));
    }

    /// <summary>
    /// WINDOWS: <c>nNumberOfLinks</c> of <paramref name="path"/> from <c>GetFileInformationByHandle</c>, on a handle opened for its
    /// attributes alone with <c>FILE_FLAG_OPEN_REPARSE_POINT</c> (never through a link) and every share mode.
    /// </summary>
    static uint? WindowsLinkCount(string path)
    {
        // A path past MAX_PATH reaches CreateFileW only with the \\?\ prefix (.NET adds it for its own calls, not for this one).
        var name = path.Length < 260 || path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
        using var handle = CreateFileW(name, FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            if (error is ErrorFileNotFound or ErrorPathNotFound) return null;
            throw NotPrivate(path, Errno("CreateFileW", error));
        }
        if (!GetFileInformationByHandle(handle, out var info))
            throw NotPrivate(path, Errno("GetFileInformationByHandle"));
        return info.NumberOfLinks;
    }

    const int AtFdCwd = -100, AtSymlinkNoFollow = 0x100;
    const uint StatxNlink = 0x4, StatxUid = 0x8;
    const uint FileReadAttributes = 0x80, FileShareAll = 0x7, OpenExisting = 3, FileFlagOpenReparsePoint = 0x00200000,
        FileFlagBackupSemantics = 0x02000000;
    const int ErrorFileNotFound = 2, ErrorPathNotFound = 3;

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    static extern uint geteuid();

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    static extern int lstat([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, byte[] buf);

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "lstat$INODE64", SetLastError = true)]
    static extern int lstat_inode64([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, byte[] buf);

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    static extern int statx(int dirfd, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, byte[] buf);

    /// <summary>BY_HANDLE_FILE_INFORMATION: its three FILETIMEs are pairs of DWORDs, aligned to 4 — hence the packing.</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
    struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public ulong CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition,
        uint flags, IntPtr template);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, out ByHandleFileInformation info);

    /// <summary>
    /// <paramref name="rel"/>, a seat file path from the manifest's <paramref name="key"/>, as a full path STRICTLY BELOW
    /// <paramref name="root"/> — or the refusal in words, before anything is changed: a rooted path, or one whose <c>..</c> leads out
    /// of the seat home, is never resolved on a guess. Null for an empty one (none).
    /// </summary>
    static string? Inside(string root, string? rel, string key)
    {
        if (string.IsNullOrEmpty(rel)) return null;
        var native = rel.Replace('/', Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(root, native));
        var below = Path.GetRelativePath(root, path);
        if (!Path.IsPathRooted(native) && !Path.IsPathRooted(below) && below != "." && below != ".."
            && !below.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return path;
        throw new TradeAgentException(ErrorCode.AI_SEAT_REFUSED,
            $"TradeAgent will not use {rel} for the AI's {(key switch { "config_file" => "settings", "model_catalog" => "model catalog", _ => "sign-in" })}: the AI runtime's {key} entry " +
            $"must name a file inside the AI's own home {root}, and this one does not. This is TradeAgent's own settings at fault — " +
            $"correct {key} in runtimes.json. Nothing was written or started, and nothing outside the AI's own home was touched.");
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
    /// Linux and macOS, and no access entry of its own. A file that cannot be is refused in words, never used.
    /// </summary>
    static void PrivateFile(string path)
    {
        if (!OperatingSystem.IsWindows())
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw NotPrivate(path, ex.Message); }
        NoEntryOfItsOwn(path);
    }

    /// <summary>
    /// <paramref name="path"/>, a file of the seat home, left with NO ACCESS ENTRY OF ITS OWN: on Windows it inherits exactly the root's
    /// owner-only list (<see cref="OwnedOnWindows"/>), on macOS it keeps no extended ACL entry (<see cref="NoExtendedAcl"/>). Linux
    /// keeps nothing to clear. A file another account owns, or one that will not take the list, is refused in words, never used.
    /// </summary>
    static void NoEntryOfItsOwn(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!OwnedOnWindows(new FileInfo(path), isRoot: false, out var owner))
                throw new TradeAgentException(ErrorCode.AI_SEAT_REFUSED,
                    $"TradeAgent will not use the AI's own home while {path} belongs to another account{(owner is null ? "" : $" ({owner})")}: " +
                    $"whoever owns it could read the AI's sign-in. Nothing was written or started. Remove {path}; a removed sign-in is " +
                    "signed in again from TradeAgent.");
        }
        else if (OperatingSystem.IsMacOS()) NoExtendedAcl(path);
    }

    /// <summary>
    /// macOS: <paramref name="path"/> LEFT WITH NO EXTENDED ACL ENTRY (<c>U-seat-acl</c>; CRED-001). macOS keeps these apart from the mode
    /// bits — <c>everyone allow read</c> on a folder survives <c>chmod 0700</c>, and an inheritable one is copied onto every file made in
    /// it, the <c>auth.json</c> codex writes at sign-in included. Read and cleared on the path itself, never through a link
    /// (<c>acl_get_link_np</c>, <c>acl_set_link_np</c>), and read again: an entry still there, or a clear the system refused, is the
    /// refusal in words. A volume that keeps no ACLs at all has none to clear.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    static void NoExtendedAcl(string path)
    {
        if (!HasExtendedAcl(path)) return;
        var empty = acl_init(0);
        if (empty == IntPtr.Zero) throw NotPrivate(path, Errno("acl_init"));
        try
        {
            if (acl_set_link_np(path, AclTypeExtended, empty) != 0) throw NotPrivate(path, Errno("acl_set_link_np"));
        }
        finally { acl_free(empty); }
        if (HasExtendedAcl(path)) throw NotPrivate(path, "an access entry was still there after it was cleared");

        static bool HasExtendedAcl(string path)
        {
            var acl = acl_get_link_np(path, AclTypeExtended);
            if (acl == IntPtr.Zero)
            {
                var errno = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                if (errno is ENOENT or ENOTSUP or EOPNOTSUPP) return false; // none; or a volume that keeps none
                throw NotPrivate(path, Errno("acl_get_link_np", errno));
            }
            try { return acl_get_entry(acl, AclFirstEntry, out _) == 0; }
            finally { acl_free(acl); }
        }
    }

    static string Errno(string call, int? errno = null)
    {
        var e = errno ?? System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        return $"{call}: {System.Runtime.InteropServices.Marshal.GetPInvokeErrorMessage(e)} (errno {e})";
    }

    // <sys/acl.h> on macOS: ACL_TYPE_EXTENDED, ACL_FIRST_ENTRY; <sys/errno.h>: ENOENT ("no ACL" from acl_get_*), ENOTSUP, EOPNOTSUPP.
    const int AclTypeExtended = 0x00000100, AclFirstEntry = 0, ENOENT = 2, ENOTSUP = 45, EOPNOTSUPP = 102;

    [System.Runtime.InteropServices.DllImport("libSystem.dylib", SetLastError = true)]
    static extern IntPtr acl_get_link_np([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, int type);

    [System.Runtime.InteropServices.DllImport("libSystem.dylib", SetLastError = true)]
    static extern int acl_set_link_np([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, int type, IntPtr acl);

    [System.Runtime.InteropServices.DllImport("libSystem.dylib", SetLastError = true)]
    static extern IntPtr acl_init(int count);

    [System.Runtime.InteropServices.DllImport("libSystem.dylib", SetLastError = true)]
    static extern int acl_get_entry(IntPtr acl, int entryId, out IntPtr entry);

    [System.Runtime.InteropServices.DllImport("libSystem.dylib")]
    static extern int acl_free(IntPtr obj);

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
        if (OperatingSystem.IsWindows()) ours = OwnedOnWindows(new DirectoryInfo(at), at == root, out owner);
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
        if (ours)
        {
            if (OperatingSystem.IsMacOS()) NoExtendedAcl(at);
            return;
        }
        throw Foreign(at, owner);
    }

    static TradeAgentException Foreign(string at, string? owner) =>
        new(ErrorCode.AI_SEAT_REFUSED,
            $"TradeAgent will not use the AI's own home while {at} belongs to another account{(owner is null ? "" : $" ({owner})")}: " +
            "whoever owns it could change the AI's settings and read its sign-in. Nothing was written or started. Remove " +
            $"{at} and TradeAgent makes its own folder there.");

    /// <summary>
    /// REFUSES <paramref name="folder"/> WHEN ANOTHER ACCOUNT OWNS IT, READ AND NEVER CHANGED — the look a preparation takes at the
    /// seat-home root before it reads anything below it. Its owner from <c>lstat</c>/<c>statx</c> against this process's effective
    /// uid on Linux and macOS, from its security descriptor on Windows (<see cref="OursOnWindows"/>). Nothing there is fine.
    /// </summary>
    static void NotForeign(string folder)
    {
        if (!Directory.Exists(folder)) return;
        if (OperatingSystem.IsWindows())
        {
            if (!OursOnWindows(new DirectoryInfo(folder), out var owner, out _, out _)) throw Foreign(folder, owner);
        }
        else if (UnixStat(folder) is { } stat && stat.Uid != geteuid()) throw Foreign(folder, $"uid {stat.Uid}");
    }

    /// <summary>
    /// WINDOWS: <paramref name="item"/>'s OWNER IS THIS ACCOUNT (or the Administrators group, for an elevated process) — false
    /// otherwise, with the owner read — AND ITS ACCESS LIST IS OWNER-ONLY. The root (<paramref name="isRoot"/>) holds a protected list
    /// — nothing inherited, nothing kept — granting this account, its owner and SYSTEM. Everything below it holds NO entry of its own
    /// and no protection, so it inherits exactly the root's (<c>U-seat-acl</c>): a replaced folder list moves only inherited entries,
    /// and an explicit grant on an existing <c>auth.json</c> or a protected subfolder would otherwise survive it. Looked at first and
    /// written only when it is not so — a write to a folder's list re-propagates over every file below it — then read again: a list
    /// the system would not take, or one that still is not so, is the refusal in words.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static bool OwnedOnWindows(FileSystemInfo item, bool isRoot, out string? owner)
    {
        if (!OursOnWindows(item, out owner, out var user, out var sid)) return false;
        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        var system = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
        var allowed = new HashSet<System.Security.Principal.SecurityIdentifier> { user, sid, system };
        // BELOW THE ROOT, the root's own owner is in what it inherits: the Administrators group when an elevated process made it.
        if (!isRoot && new System.Security.Principal.WindowsPrincipal(me).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            allowed.Add(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null));
        try
        {
            if (Private(Security(item), isRoot, allowed)) return true;
            System.Security.AccessControl.FileSystemSecurity list;
            if (isRoot)
            {
                // OWNER-ONLY: a protected list — nothing inherited, nothing kept — granting this account, the owner and SYSTEM.
                list = new System.Security.AccessControl.DirectorySecurity();
                list.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                foreach (var who in new[] { user, sid, system }.Distinct())
                    list.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(who,
                        System.Security.AccessControl.FileSystemRights.FullControl, Both,
                        System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
            }
            else
            {
                // NOTHING OF ITS OWN: every explicit entry gone and the protection off, so the system merges in the folder's list.
                list = Security(item);
                foreach (var who in list.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier))
                             .Cast<System.Security.AccessControl.FileSystemAccessRule>().Select(r => r.IdentityReference).Distinct().ToList())
                    list.PurgeAccessRules(who);
                list.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            }
            if (item is DirectoryInfo folder) folder.SetAccessControl((System.Security.AccessControl.DirectorySecurity)list);
            else ((FileInfo)item).SetAccessControl((System.Security.AccessControl.FileSecurity)list);
            item.Refresh();
            if (Private(Security(item), isRoot, allowed, exact: false)) return true;
            throw NotPrivate(item.FullName, "its access list still grants another account after it was replaced");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException
                                       or System.ComponentModel.Win32Exception)
        {
            throw NotPrivate(item.FullName, ex.Message);
        }
    }

    /// <summary>
    /// WINDOWS: whether <paramref name="item"/>'s owner, read from its security descriptor and never changed, is this account — or the
    /// Administrators group for an elevated process, whom Windows makes the owner of what that creates. A descriptor that cannot be
    /// read is not ours.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static bool OursOnWindows(FileSystemInfo item, out string? owner,
        out System.Security.Principal.SecurityIdentifier user, out System.Security.Principal.SecurityIdentifier sid)
    {
        owner = null;
        user = sid = null!;
        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        try
        {
            if (Security(item).GetOwner(typeof(System.Security.Principal.SecurityIdentifier)) is not System.Security.Principal.SecurityIdentifier found) return false;
            sid = found;
            owner = sid.Value;
            if (me.User is not { } account) return false;
            user = account;
            return sid.Equals(user)
                || (sid.IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid)
                    && new System.Security.Principal.WindowsPrincipal(me).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator));
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static System.Security.AccessControl.InheritanceFlags Both =>
        System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static System.Security.AccessControl.FileSystemSecurity Security(FileSystemInfo item) =>
        item is DirectoryInfo folder ? folder.GetAccessControl() : ((FileInfo)item).GetAccessControl();

    /// <summary>
    /// Whether <paramref name="list"/> is owner-only: for the root, protected, nothing inherited, and every entry that allows naming
    /// one of <paramref name="allowed"/> — and, when <paramref name="exact"/> (is it already the list the app writes?), exactly one
    /// full-control entry, inherited by all below, for each of them; below it, unprotected, no entry of its own, and every inherited
    /// entry that allows naming one of <paramref name="allowed"/>.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static bool Private(System.Security.AccessControl.FileSystemSecurity list, bool isRoot,
        HashSet<System.Security.Principal.SecurityIdentifier> allowed, bool exact = true)
    {
        var rules = list.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>().ToList();
        bool Allowed(System.Security.AccessControl.FileSystemAccessRule r) =>
            r.AccessControlType == System.Security.AccessControl.AccessControlType.Deny
            || allowed.Contains((System.Security.Principal.SecurityIdentifier)r.IdentityReference);
        if (isRoot)
        {
            if (!list.AreAccessRulesProtected || rules.Any(r => r.IsInherited) || !rules.All(Allowed)) return false;
            if (!exact) return true;
            var granted = rules.Select(r => (System.Security.Principal.SecurityIdentifier)r.IdentityReference).ToHashSet();
            return rules.Count == granted.Count && granted.SetEquals(allowed) && rules.All(r =>
                r.AccessControlType == System.Security.AccessControl.AccessControlType.Allow
                && r.FileSystemRights == System.Security.AccessControl.FileSystemRights.FullControl
                && r.InheritanceFlags == Both && r.PropagationFlags == System.Security.AccessControl.PropagationFlags.None);
        }
        return !list.AreAccessRulesProtected && rules.All(r => r.IsInherited && Allowed(r));
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

    /// <summary>The shipped catalog's full path (<see cref="SeatPlan.ModelCatalogSource"/> under the app's own folder), or null for none.</summary>
    public static string? CatalogSourcePath(SeatPlan seat) =>
        seat.ModelCatalogSource is { Length: > 0 } rel
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, rel.Replace('/', Path.DirectorySeparatorChar)))
            : null;

    /// <summary>
    /// THE SHIPPED MODEL CATALOG, HELD TO ITS PIN (<c>U-cell-model-catalog</c>): its bytes, or the refusal in words naming the file and —
    /// for one that hashes otherwise — both hashes. A plan with no catalog has neither. Never a guess: a missing, unreadable or altered
    /// file is not written into any seat home and no verified seat starts on it.
    /// </summary>
    public static (byte[]? Bytes, string? Refusal) ShippedCatalog(SeatPlan seat)
    {
        if (CatalogSourcePath(seat) is not { } source) return (null, null);
        byte[] bytes;
        try { bytes = File.ReadAllBytes(source); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"TradeAgent will not start the AI: the model catalog it ships, {source}, could not be read ({ex.Message}). " +
                          "Reinstall TradeAgent to put it back. Nothing was started.");
        }
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(sha, seat.ModelCatalogSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            return (null, $"TradeAgent will not start the AI: the model catalog it ships, {source}, hashes to {sha}, and this build " +
                          $"({Versions.App}) pins it as {seat.ModelCatalogSha256}. Reinstall TradeAgent to put the shipped file back. Nothing was started.");
        return (bytes, null);
    }

    /// <summary>
    /// NULL WHEN <paramref name="seatClass"/> NAMES NO MODEL CATALOG, OR THE SEAT HOME'S IS THE SHIPPED ONE BYTE FOR BYTE — or the refusal
    /// in words: a class whose command names a catalog the plan has none for, a shipped file missing or off its pin, or a seat-home copy
    /// that is missing or differs, named by its path and both hashes.
    /// </summary>
    public static string? CatalogRefusal(SeatPlan seat, string seatClass)
    {
        if (!seat.ArgsFor(seatClass).Any(a => a.Contains(SeatPlan.CatalogSlot, StringComparison.Ordinal))) return null;
        if (seat.CatalogPath is not { } path)
            return "TradeAgent will not start the AI: its command names a model catalog, and this build gives the seat none to write. " +
                   "This is TradeAgent's own fault, not the AI's; nothing was started.";
        var (shipped, refusal) = ShippedCatalog(seat);
        if (refusal is not null) return refusal;
        if (shipped is null)
            return "TradeAgent will not start the AI: its command names a model catalog, and this build ships none. Nothing was started.";
        byte[] written;
        try { written = File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"TradeAgent will not start the AI: its model catalog {path} could not be read ({ex.Message}). Nothing was started; " +
                   "the next turn writes it again.";
        }
        if (written.AsSpan().SequenceEqual(shipped)) return null;
        return $"TradeAgent will not start the AI: its model catalog {path} hashes to {Convert.ToHexStringLower(SHA256.HashData(written))}, " +
               $"not {Convert.ToHexStringLower(SHA256.HashData(shipped))}, the catalog TradeAgent just wrote — so the AI would be offered tools " +
               "its check never saw. Something is changing it as it is written; close whatever else uses that folder. Nothing was started.";
    }

    /// <summary>The pin of the catalog <paramref name="seatClass"/>'s command names, lower-case; empty for a class that names none.</summary>
    public static string CatalogPin(SeatPlan seat, string seatClass) =>
        seat.ArgsFor(seatClass).Any(a => a.Contains(SeatPlan.CatalogSlot, StringComparison.Ordinal))
            ? seat.ModelCatalogSha256.Trim().ToLowerInvariant() : "";

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
    public static Task<SeatVerdict> VerifyAsync(RuntimeManifest manifest, string exe, IReadOnlyList<string> argv,
        string workingDirectory, ISeatProbe probe, CancellationToken ct) =>
        VerifyAsync(manifest, exe, argv, workingDirectory, probe, SeatLaunch.Cli, ct);

    /// <summary>
    /// <see cref="VerifyAsync(RuntimeManifest, string, IReadOnlyList{string}, string, ISeatProbe, CancellationToken)"/> FOR ONE SEAT
    /// CLASS (<c>U-thinking-seats-c</c>): that class's deny set, keys, environment and allowlists. For the broker class, before
    /// anything is probed: its fields are this build's (<see cref="BrokerSeat.OverrideRefusal"/>), the door is open, and the argv
    /// names its pinned model; after the CLI's own reports: the tools its binary, template and model would be offered are exactly the
    /// expected set (<see cref="BrokerSeat.VerifySurfaceAsync"/>) — so a broker seat that would be offered a host tool or <c>exec</c>
    /// is refused before its admission writes anything.
    /// </summary>
    public static async Task<SeatVerdict> VerifyAsync(RuntimeManifest manifest, string exe, IReadOnlyList<string> argv,
        string workingDirectory, ISeatProbe probe, SeatLaunch launch, CancellationToken ct)
    {
        // A TEST HOST'S PUPPET PLAYING THE BROKER CLASS HAS NO VENDOR SEAT TO VERIFY (U-board-b2): it runs no model and starts no vendor
        // program, so a board seat founded on the broker class can be played. Only that class — a cell is a real confinement a puppet never
        // plays past its admission — and never in the product: a puppet runs only in a test host.
        if (manifest.IsTestHostPuppet && launch.SeatClass == SeatPlan.BrokerClass) return SeatVerdict.None;

        // A BROKER SEAT IS NEVER STARTED UNVERIFIED: a manifest with no seat profile has none of the class's deny set to carry.
        if (manifest.Seat is not { } seat)
            return BrokerSeat.IsVerified(launch.SeatClass)
                ? SeatVerdict.Refuse(ErrorCode.AI_SEAT_REFUSED, BrokerSeat.OverrideRefusal(manifest, launch.SeatClass) ?? "TradeAgent will not start a broker-only seat with no seat profile.")
                : SeatVerdict.None;
        var seatClass = launch.SeatClass;
        var broker = BrokerSeat.IsVerified(seatClass);
        string? model = null;

        if (broker)
        {
            if (BrokerSeat.OverrideRefusal(manifest, seatClass) is { } overridden) return SeatVerdict.Refuse(ErrorCode.AI_SEAT_REFUSED, overridden);
            if (seat.NeedsDoor(seatClass) && launch.Door is null)
                return SeatVerdict.Refuse(ErrorCode.AI_SEAT_REFUSED,
                    $"TradeAgent will not start {manifest.DisplayName} as a broker-only seat: TradeAgent's own door, the one tool server it " +
                    "may use, is not open. Nothing was started; the next turn checks again.");
            // THE PINNED MODEL, AND ONLY IT: a class that pins none, a command line that cannot name one, or another model refuses.
            if (BrokerSeat.ModelRefusal(seatClass, launch.Model, manifest) is { } notPinned)
                return SeatVerdict.Refuse(ErrorCode.AI_SEAT_REFUSED, notPinned);
            model = BrokerSeat.ModelOf(seatClass)!;
            string[] named = [.. manifest.ModelArgs.Select(a => a.Replace("{model}", model, StringComparison.Ordinal))];
            if (!Carries(argv, named))
                return SeatVerdict.Refuse(ErrorCode.AI_SEAT_REFUSED,
                    $"TradeAgent will not start {manifest.DisplayName} as a broker-only seat: the command it built does not name {model}, the " +
                    "model a broker-only seat is pinned to, so it would run on whatever its account defaults to. This is TradeAgent's own " +
                    "fault, not the AI's; nothing was started.");
        }
        else if (!seat.Args.ContainsKey(seatClass) && seatClass != SeatPlan.CliClass)
            return SeatVerdict.Refuse(ErrorCode.AI_SEAT_REFUSED,
                $"TradeAgent will not start {manifest.DisplayName} as a '{seatClass}' seat: this build gives it no deny set for that class. " +
                "Nothing was started.");

        try { Prepare(seat); }
        catch (TradeAgentException ex) when (ex.Code == ErrorCode.AI_SEAT_REFUSED) { return SeatVerdict.Refuse(ex.Code, ex.Message); }

        if (PinRefusal(manifest, exe) is { } notThePin)
            return SeatVerdict.Refuse(ErrorCode.AI_VERSION_UNSUPPORTED, notThePin);

        // THE PROGRAMS THE PIN STARTS BESIDE ITSELF, EACH THE PIN TOO (U-cli-code-mode): for the CLI class, code mode's host.
        if (CompanionRefusal(manifest, exe, seatClass) is { } notItsCompanion)
            return SeatVerdict.Refuse(ErrorCode.AI_VERSION_UNSUPPORTED, notItsCompanion);

        var deny = seat.ArgsFor(seatClass, launch.Door);
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

        // THE MODEL CATALOG THE CLASS'S COMMAND NAMES IS THE APP'S, BYTE FOR BYTE (U-cell-model-catalog): the dry run read these bytes,
        // so a live turn on any other metadata — a missing file, an altered one, the account's list — is refused before admission.
        if (CatalogRefusal(seat, seatClass) is { } catalogRefused)
            return SeatVerdict.Refuse(ErrorCode.AI_SEAT_REFUSED, catalogRefused);

        if (Foreign(seat.Root, seat.Forbidden) is { } inHome)
            return SeatVerdict.Refuse(ErrorCode.AI_SEAT_REFUSED,
                $"TradeAgent will not start the AI while {inHome} exists: the AI's own home may hold only what TradeAgent puts " +
                "there, and the AI would read that as instructions or tools. Remove it to continue.");

        if (ForeignInHome(workingDirectory, seat.WorkspaceForbidden) is { } inWorkspace)
            return SeatVerdict.Refuse(ErrorCode.AI_SEAT_REFUSED,
                $"TradeAgent will not start the AI while {inWorkspace} exists in its working folder: the AI would load it as its " +
                "own settings or skills. Remove it to continue.");

        // THE CLI'S OWN REPORTS RUN IN AN EMPTY FOLDER OF THE APP'S, NEVER IN THE POSITION'S HOME (U-execution-environment fix pass 5, the
        // fourth re-review's G2): an executor's cell writes its whole home, and these run the vendor CLI unconfined. They read the seat's
        // own codex home (CODEX_HOME) and its sign-in; the only thing a working folder adds is its own settings, which the look above has
        // just refused — so the app's folder answers the same question, and the CLI never runs in a folder a cell writes.
        SeatReading reading;
        try { reading = await probe.ReadAsync(manifest, exe, ProbeFolder(), seatClass, launch.Door, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return SeatVerdict.Refuse(ErrorCode.AI_SEAT_REFUSED,
                $"TradeAgent will not start the AI: it could not read what {manifest.DisplayName} would switch on ({ex.Message}).");
        }

        if (OffTheAllowlist(seat, seatClass, reading.Features) is { } feature)
            return SeatVerdict.Refuse(ErrorCode.AI_SEAT_REFUSED,
                $"TradeAgent will not start the AI: {manifest.DisplayName} reports {feature} switched on, which the AI's profile does " +
                $"not allow. This build of TradeAgent runs {manifest.DisplayName} {manifest.Install.Version} with only its own set on.");

        if (Servers(seat, seatClass, reading.Mcp) is { } server)
            return SeatVerdict.Refuse(ErrorCode.AI_SEAT_REFUSED,
                $"TradeAgent will not start the AI: {manifest.DisplayName} reports {server}, and the AI's profile allows no tool " +
                "server TradeAgent did not configure.");

        // A FRESH START, REFUSED IN WORDS: the seat home holds no credential until the owner signs in from TradeAgent. Nothing is
        // copied from his own ~/.codex — no migration (CLAUDE.md) — and a seat that ran signed out would fail as a turn, which reads
        // like the AI thinking rather than like the button he has to press.
        if (!reading.SignedIn)
            return SeatVerdict.Refuse(ErrorCode.AI_AUTH_REQUIRED, seat.SignedOut);

        // A CLI SEAT WHOSE COMMAND TOOL CANNOT RUN IS REFUSED IN WORDS BEFORE ADMISSION (U-cli-code-mode), never passed off as turns that
        // exit 0: attempt 4's every exec call failed closed while its turns ended clean.
        if (seatClass == SeatPlan.CliClass && await ToolsRefusalAsync(manifest, exe, probe, launch, ct) is { } dead)
            return SeatVerdict.Refuse(ErrorCode.AI_SEAT_REFUSED, dead);

        if (!broker)
            return new SeatVerdict(null, null, reading)
            {
                Profile = ProfileHash(manifest, exe, ConfigBytes(seat), reading),
                BillingClass = TradeAgent.Core.Db.BillingClasses.Of(reading.BillingClass)
            };

        // THE TOOLS THIS BINARY, TEMPLATE AND MODEL WOULD BE OFFERED, VERIFIED BEFORE EVERY LAUNCH, fresh or resumed (§ 2.7): read from
        // the dry run's cache by (binary digest, template, model), and held to the class's expected set on every call.
        string exeSha;
        try { exeSha = Sha256Of(exe); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SeatVerdict.Refuse(ErrorCode.AI_SEAT_REFUSED, $"TradeAgent will not start the AI: it could not read {exe} ({ex.Message}).");
        }
        var (refusal, offered) = await BrokerSeat.VerifySurfaceAsync(
            new SurfaceAsk(manifest, exe, exeSha, seatClass, model!), launch.Surface ?? CodexSurfaceProbe.Instance, ct);
        if (refusal is not null) return SeatVerdict.Refuse(ErrorCode.AI_SEAT_REFUSED, refusal);
        return new SeatVerdict(null, null, reading)
        {
            Profile = ProfileHash(manifest, exe, ConfigBytes(seat), reading, seatClass, offered ?? []),
            Offered = offered,
            BillingClass = TradeAgent.Core.Db.BillingClasses.Of(reading.BillingClass)
        };
    }

    /// <summary>What a passing dry run proved, per (binary digest, argument template, model), for this process's life. Refusals are never kept.</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Binary, string Template, string Model), byte> ToolsProven = new();

    /// <summary>Forgets every proven tool set: a test's way to make the next verify reach its probe.</summary>
    internal static void ForgetToolProofs() => ToolsProven.Clear();

    /// <summary>
    /// NULL WHEN A CLI SEAT OF THIS BINARY, TEMPLATE AND MODEL CAN RUN ITS COMMAND TOOL, or the refusal: read from the proofs kept or, on
    /// a key not yet proven, from <paramref name="probe"/>'s dry run. A probe that fails is a refusal, and so is every run it reports but
    /// a call whose output holds the mark; only a pass is kept. The binary is the pin, already verified, so its digest is the pin's.
    /// </summary>
    static async Task<string?> ToolsRefusalAsync(RuntimeManifest manifest, string exe, ISeatProbe probe, SeatLaunch launch, CancellationToken ct)
    {
        string exeSha;
        if (manifest.Install.ExecutableSha256 is { Length: > 0 } pin) exeSha = pin.Trim().ToLowerInvariant();
        else
        {
            try { exeSha = Sha256Of(exe); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return $"TradeAgent will not start the AI: it could not read {exe} ({ex.Message})."; }
        }
        var model = launch.Model ?? "";
        var key = (exeSha, BrokerSeat.TemplateHash(manifest, SeatPlan.CliClass), model);
        if (ToolsProven.ContainsKey(key)) return null;

        ToolRun run;
        try { run = await probe.ToolsAsync(new SurfaceAsk(manifest, exe, exeSha, SeatPlan.CliClass, model), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"TradeAgent will not start {manifest.DisplayName}: it could not check that the AI's command tool runs ({ex.Message}). " +
                   "Nothing was started; the next turn checks again.";
        }
        if (ToolRefusal(manifest, model, run) is { } refusal) return refusal;
        ToolsProven[key] = 0;
        return null;
    }

    /// <summary>
    /// NULL WHEN <paramref name="run"/> SHOWS A COMMAND TOOL THAT RAN — its output holds the mark only a running call produces — or the
    /// refusal in words: no command tool offered, a call never answered, or an answer without the mark, quoted.
    /// </summary>
    public static string? ToolRefusal(RuntimeManifest manifest, string? model, ToolRun run)
    {
        var on = model is { Length: > 0 } m ? $" on {m}" : "";
        if (run.Tried is null)
            return $"TradeAgent will not start {manifest.DisplayName}{on}: it would offer the AI no tool that runs a command " +
                   $"({(run.Offered.Count == 0 ? "no tools at all" : string.Join(", ", run.Offered.Select(BrokerSeat.ToolName).Distinct()))}), so the AI " +
                   "could read nothing and do nothing while its turns looked finished. Nothing was started.";
        if (run.Output is null)
            return $"TradeAgent will not start {manifest.DisplayName}{on}: its command tool '{run.Tried}' was called in a dry run and never " +
                   "answered, so the AI's commands would not run while its turns looked finished. Nothing was started.";
        if (run.Output.Contains(run.Expected, StringComparison.Ordinal)) return null;
        var said = run.Output.Trim();
        if (said.Length > 300) said = said[..300] + "…";
        return $"TradeAgent will not start {manifest.DisplayName}{on}: its command tool '{run.Tried}' was called in a dry run and did not run — " +
               $"it answered \"{said}\" — so every command the AI tried would fail while its turns looked finished. Nothing was started.";
    }

    /// <summary>
    /// THE RUNTIME PROFILE A LAUNCH RAN UNDER, as one SHA-256 (lower-case hex): the program's own hash and the pinned version, the
    /// seat's argv minus its template, prompt and model (the stream flag, the unattended flags and the deny set — the same for a
    /// first turn and a resume), the config file's bytes, and the CLI's two reports verbatim. Written on the launch's
    /// <c>ai_attempt</c> row; one deny flag moved is another profile, and so is another release.
    /// </summary>
    public static string ProfileHash(RuntimeManifest manifest, string exe, byte[] config, SeatReading reading) =>
        ProfileHash(manifest, exe, config, reading, SeatPlan.CliClass, null);

    /// <summary>
    /// <see cref="ProfileHash(RuntimeManifest, string, byte[], SeatReading)"/> FOR ONE SEAT CLASS: that class's deny set as written (the
    /// door's slot unfilled, so a restart's new port is the same profile), and — for any class but the CLI's — the class's name, its
    /// environment, and the offered tools its surface was verified as. Every class hashes the pinned companions its program may start
    /// (<see cref="CompanionsFor"/>; <c>U-cli-code-mode</c>), so the CLI class's profile changed with code mode's host.
    /// </summary>
    public static string ProfileHash(RuntimeManifest manifest, string exe, byte[] config, SeatReading reading, string seatClass,
        IReadOnlyList<string>? offered)
    {
        var seat = manifest.Seat;
        var argv = new List<string>();
        if (!string.IsNullOrWhiteSpace(manifest.JsonFlag)) argv.AddRange(manifest.JsonFlag.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        argv.AddRange(manifest.UnattendedArgs);
        argv.AddRange(seat?.ArgsFor(seatClass) ?? []);

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
        // THE PINNED PROGRAMS THE CLASS'S PROGRAM STARTS BESIDE ITSELF (U-cli-code-mode), verified equal to their files before this.
        Part("companions", Encoding.UTF8.GetBytes(string.Join('\0',
            CompanionsFor(manifest, seatClass).Select(c => c.Name + "=" + c.Sha256.Trim().ToLowerInvariant()))));
        if (seatClass != SeatPlan.CliClass)
        {
            Part("class", Encoding.UTF8.GetBytes(seatClass));
            Part("class-environment", Encoding.UTF8.GetBytes(string.Join('\0',
                (seat?.ClassEnvironment.GetValueOrDefault(seatClass) ?? new Dictionary<string, string>())
                .OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value))));
            Part("offered", Encoding.UTF8.GetBytes(string.Join('\0', (offered ?? []).Order(StringComparer.Ordinal))));
            // THE MODEL CATALOG ITS COMMAND NAMES, BY ITS PIN (U-cell-model-catalog): verified equal to the seat home's copy before this.
            Part("model-catalog", Encoding.UTF8.GetBytes(seat is null ? "" : CatalogPin(seat, seatClass)));
        }
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

    /// <summary>
    /// The empty folder of the app's own state the CLI's reports run in (<c>state/seat-probe</c>): no position's home, nothing a cell
    /// writes, and nothing the app puts in it.
    /// </summary>
    static string ProbeFolder() => Directory.CreateDirectory(Path.Combine(Paths.State, "seat-probe")).FullName;

    /// <summary>
    /// The first of <paramref name="names"/> that is anything at all in the position's <paramref name="home"/> — a file, a folder, a link
    /// to anything or nothing — examined through <see cref="HomeFiles"/> without following it (<c>U-execution-environment</c> fix pass 5,
    /// G2: an executor's cell writes its whole home). A name that cannot be examined is named too: a look that fails refuses, never passes.
    /// </summary>
    static string? ForeignInHome(string home, string[] names)
    {
        foreach (var name in names)
        {
            var path = Path.GetFullPath(Path.Combine(home, name.Replace('/', Path.DirectorySeparatorChar)));
            try { if (HomeFiles.Describe(home, name) is not null) return path; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return $"{path} (which TradeAgent could not examine: {ex.Message})"; }
        }
        return null;
    }

    /// <summary>The first of <paramref name="names"/> that exists under the seat's own home <paramref name="root"/>, as a full path, or null.</summary>
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
    static string? OffTheAllowlist(SeatPlan seat, string seatClass, string report)
    {
        var allowed = seat.FeaturesFor(seatClass);
        if (seat.FeaturesArgs.Length == 0) return null;
        var on = new List<string>();
        var read = 0;
        foreach (var line in report.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2 || words[^1] is not ("true" or "false")) continue;
            read++;
            if (words[^1] == "true" && !allowed.Contains(words[0], StringComparer.Ordinal)) on.Add(words[0]);
        }
        if (read == 0) return "no feature list TradeAgent could read";
        return on.Count == 0 ? null : (on.Count == 1 ? $"the feature {on[0]}" : $"the features {string.Join(", ", on)}");
    }

    /// <summary>The MCP servers the report lists and the allowlist does not hold, named, or null when there are none.</summary>
    static string? Servers(SeatPlan seat, string seatClass, string report)
    {
        var allowed = seat.ServersFor(seatClass);
        if (seat.McpArgs.Length == 0) return null;
        List<string> names = [];
        try
        {
            using var doc = JsonDocument.Parse(report);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return "a tool-server list TradeAgent could not read";
            foreach (var server in doc.RootElement.EnumerateArray())
            {
                var name = server.ValueKind == JsonValueKind.Object && server.TryGetProperty("name", out var n) ? n.ToString() : server.ToString();
                if (!allowed.Contains(name, StringComparer.Ordinal)) names.Add(name);
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

    /// <summary>
    /// The companions <paramref name="seatClass"/>'s program may start: every one of the plan's, less those whose feature the class's
    /// deny set disables (<c>--disable</c> that feature, as one adjacent pair).
    /// </summary>
    public static IEnumerable<CompanionPin> CompanionsFor(RuntimeManifest manifest, string seatClass)
    {
        var deny = manifest.Seat?.ArgsFor(seatClass) ?? [];
        bool Disabled(string feature)
        {
            for (var i = 0; i + 1 < deny.Length; i++)
                if (deny[i] == "--disable" && deny[i + 1] == feature) return true;
            return false;
        }
        return manifest.Install.Companions.Where(c => c.Feature is not { Length: > 0 } f || !Disabled(f));
    }

    /// <summary>
    /// NULL WHEN EVERY PROGRAM <paramref name="exe"/> MAY START BESIDE ITSELF FOR <paramref name="seatClass"/> IS ITS PIN, or the refusal
    /// in words (<c>U-cli-code-mode</c>): none found where the program looks, or one found that hashes otherwise — naming the file, both
    /// hashes, the pinned version and the repair. Like the program's own pin, a companion that is not the pin is never run, not even to
    /// ask its version. Where the program looks is relative to its REAL folder, links resolved, as the vendor resolves it.
    /// </summary>
    public static string? CompanionRefusal(RuntimeManifest manifest, string exe, string seatClass)
    {
        var companions = CompanionsFor(manifest, seatClass).ToList();
        if (companions.Count == 0) return null;

        string folder;
        try { folder = Path.GetDirectoryName(RealPath(exe)) ?? Path.GetDirectoryName(Path.GetFullPath(exe))!; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { folder = Path.GetDirectoryName(Path.GetFullPath(exe))!; }

        var repair = manifest.Install.Kind is InstallKind.Download or InstallKind.Npm or InstallKind.Winget
            ? "Install the AI assistant again from TradeAgent and it puts that exact version, with this program, in its own folder."
            : $"TradeAgent cannot install it on this computer, so {manifest.DisplayName} {manifest.Install.Version ?? ""} has to be installed " +
              "as its vendor packages it, with this program beside it.";
        foreach (var companion in companions)
        {
            var pin = companion.Sha256.Trim();
            var places = companion.Places.Length == 0 ? ["."] : companion.Places;
            var paths = places.Select(p => Path.GetFullPath(Path.Combine(folder, p.Replace('/', Path.DirectorySeparatorChar), companion.Name))).ToList();
            var found = paths.Where(File.Exists).ToList();
            if (found.Count == 0)
                return $"TradeAgent will not start {manifest.DisplayName} {manifest.Install.Version ?? "(pinned)"} without its own {companion.Name}, " +
                       $"SHA-256 {pin}: none is at {string.Join(" or ", paths)}. Without it every command the AI tries fails, and its turns " +
                       $"would look like work while doing none. {repair}";
            foreach (var path in found)
            {
                string hash;
                try { hash = Sha256Of(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { hash = $"unreadable: {ex.Message}"; }
                if (string.Equals(hash, pin, StringComparison.OrdinalIgnoreCase)) continue;
                return $"TradeAgent starts {manifest.DisplayName} {manifest.Install.Version ?? "(pinned)"} only beside its own {companion.Name}, " +
                       $"SHA-256 {pin}. The one at {path} has SHA-256 {hash}, version unknown — not run: TradeAgent never lets the AI start a " +
                       $"program that is not the pin. {repair}";
            }
        }
        return null;
    }

    /// <summary>
    /// <paramref name="path"/> with every link resolved, ON EVERY FOLDER OF IT AS WELL AS THE FILE, as the vendor's <c>canonicalize</c>
    /// (Rust's <c>std::fs::canonicalize</c>) resolves it: <c>realpath</c> on Linux and macOS, and on Windows the final path of an open
    /// handle (<c>GetFinalPathNameByHandleW</c>), so a junction on a parent folder such as <c>bin</c> is resolved too. A link resolved on
    /// the program alone is not the folder codex looks in (fix pass 1 of <c>U-cli-code-mode</c>).
    /// </summary>
    static string RealPath(string path)
    {
        if (OperatingSystem.IsWindows()) return FinalPathWindows(path);
        var resolved = realpath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero) throw new IOException($"{path} could not be resolved (errno {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()})");
        try { return System.Runtime.InteropServices.Marshal.PtrToStringUTF8(resolved) ?? path; }
        finally { free(resolved); }
    }

    /// <summary>
    /// The path Windows reports for an open handle on <paramref name="path"/>, every reparse point on the way followed, less its
    /// verbatim prefix (<c>\\?\C:\…</c> becomes <c>C:\…</c>, <c>\\?\UNC\…</c> becomes <c>\\…</c>). Throws <see cref="IOException"/>
    /// when it cannot be had.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static string FinalPathWindows(string path)
    {
        using var handle = File.OpenHandle(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new char[512];
        while (true)
        {
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0)
                throw new IOException($"{path} could not be resolved (Windows error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()})");
            if (length < buffer.Length)
            {
                var final = new string(buffer, 0, (int)length);
                if (final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + final[8..];
                return final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
            }
            buffer = new char[length];
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle file, [System.Runtime.InteropServices.Out] char[] path, uint length, uint flags);

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true, EntryPoint = "realpath")]
    static extern IntPtr realpath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, IntPtr resolved);

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "free")]
    static extern void free(IntPtr pointer);

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

    /// <summary>
    /// THE CLASS OF THE SEAT ABOUT TO START, as the app established it (<see cref="SeatClasses"/>; <c>U-thinking-seats-a</c>): a CLI
    /// session is <see cref="SeatClasses.Cli"/> — with a seat plan or without one, since no seat starts in a cell yet — and the
    /// harness is <see cref="SeatClasses.Harness"/>. Code, never read from <c>runtimes.json</c>. Null where no seat was declared.
    /// </summary>
    public string? SeatClass { get; init; }

    /// <summary>The assurance of <see cref="SeatClass"/>, or null for none or a class this build does not know.</summary>
    public SeatAssurance? Assurance => SeatClasses.AssuranceOf(SeatClass);

    /// <summary>
    /// WHO PAYS FOR A LAUNCH ON THIS SEAT (<c>U-billing-classes</c>): the app's harness <c>key</c>; a CLI seat what its sign-in read
    /// (<see cref="SeatReading.BillingClass"/>) — a codex seat may be signed in with ChatGPT or with a pasted key, and the runtime alone
    /// cannot tell; a seat with no seat profile, or a sign-in matching neither pattern, <c>unknown</c>. The meter writes it on the row
    /// from this same verdict, beside <see cref="SeatClass"/>.
    /// </summary>
    public string BillingClass { get; init; } = TradeAgent.Core.Db.BillingClasses.Unknown;

    /// <summary>The tools a broker seat's surface was verified as offering (<see cref="BrokerSeat.VerifySurfaceAsync"/>), or null.</summary>
    public IReadOnlyList<string>? Offered { get; init; }

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
/// What the CLI itself said about a seat: its feature report and its MCP server list, verbatim, whether its sign-in status
/// under the seat home answered signed in, and — <paramref name="BillingClass"/> (<c>U-billing-classes</c>) — who that sign-in bills,
/// read off the same status text by the seat's two patterns (<see cref="SeatPlan.BillingClassOf"/>): <c>plan</c>, <c>key</c> or
/// <c>unknown</c>. Only the two reports enter the profile hash; a sign-in is not part of the profile.
/// </summary>
public sealed record SeatReading(string Features, string Mcp, bool SignedIn = true,
    string BillingClass = TradeAgent.Core.Db.BillingClasses.Unknown);

/// <summary>
/// THE CLI ASKED ABOUT ITS OWN SEAT, behind an interface because CI has no codex: a test hands a fake, the product the real one.
/// </summary>
public interface ISeatProbe
{
    /// <summary>The feature report and the MCP server list, run under the seat home with the deny set's keys.</summary>
    Task<SeatReading> ReadAsync(RuntimeManifest manifest, string exe, string workingDirectory, CancellationToken ct);

    /// <summary>
    /// The same reports for one seat class: its keys, the door's address in its slot, its environment. A probe that does not tell the
    /// classes apart answers as for the CLI class.
    /// </summary>
    Task<SeatReading> ReadAsync(RuntimeManifest manifest, string exe, string workingDirectory, string seatClass, Uri? door,
        CancellationToken ct) => ReadAsync(manifest, exe, workingDirectory, ct);

    /// <summary>
    /// THE CLI CLASS'S ONE-CALL DRY RUN (<c>U-cli-code-mode</c>): one call of the command tool <paramref name="ask"/>'s binary, template
    /// and model would be offered, against a loopback mock, and what it gave back — a measurement, judged by
    /// <see cref="SeatProfile.ToolRefusal"/>. No default: a probe that cannot say must say so, never pass.
    /// </summary>
    Task<ToolRun> ToolsAsync(SurfaceAsk ask, CancellationToken ct);
}

/// <summary>
/// HOW ONE LAUNCH'S SEAT IS VERIFIED: its class (<see cref="SeatPlan.CliClass"/> or <see cref="SeatPlan.BrokerClass"/>), the app's
/// door for a class that names it, the model a broker seat is pinned to — or, for the CLI class, the model its argv names, which keys its
/// tool dry run (<c>U-cli-code-mode</c>) — and the probe that reads its offered tools (the real dry run unless a test passes a fake).
/// </summary>
public sealed record SeatLaunch(string SeatClass, Uri? Door = null, string? Model = null, ISurfaceProbe? Surface = null)
{
    /// <summary>The CLI class, as every launch was verified before classes.</summary>
    public static readonly SeatLaunch Cli = new(SeatPlan.CliClass);
}

/// <summary>
/// THE REAL PROBE: the manifest's own commands, started the way every process of the runtime is — no window, stdin closed, the
/// whitelist and the seat home (<see cref="AgentEnvironment.Apply(ProcessStartInfo, IReadOnlyDictionary{string, string},
/// RuntimeManifest)"/>), its tree held and ended.
/// </summary>
public sealed class CliSeatProbe : ISeatProbe
{
    public static readonly CliSeatProbe Instance = new();

    /// <summary>The real dry run (<see cref="CodexToolProbe"/>).</summary>
    public Task<ToolRun> ToolsAsync(SurfaceAsk ask, CancellationToken ct) => CodexToolProbe.Instance.RunAsync(ask, ct);

    static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    public Task<SeatReading> ReadAsync(RuntimeManifest manifest, string exe, string workingDirectory, CancellationToken ct) =>
        ReadAsync(manifest, exe, workingDirectory, SeatPlan.CliClass, null, ct);

    public async Task<SeatReading> ReadAsync(RuntimeManifest manifest, string exe, string workingDirectory, string seatClass, Uri? door,
        CancellationToken ct)
    {
        var seat = manifest.Seat ?? throw new InvalidOperationException($"{manifest.DisplayName} has no seat profile");
        var keys = seat.KeyArgs(seatClass, door);
        var features = seat.FeaturesArgs.Length == 0 ? "" : await Read(manifest, exe, [.. seat.FeaturesArgs, .. keys], workingDirectory, seatClass, ct);
        var mcp = seat.McpArgs.Length == 0 ? "" : await Read(manifest, exe, [.. seat.McpArgs, .. keys], workingDirectory, seatClass, ct);

        // THE SIGN-IN, UNDER THE SEAT HOME: `login status` exits 0 signed in and 1 not (measured, the manifest's AuthStateArgs). It
        // reads the app's own config.toml, written above by the environment seam, so cli_auth_credentials_store="file" holds.
        var signedIn = true;
        var billing = TradeAgent.Core.Db.BillingClasses.Unknown;
        if (manifest.AuthStateArgs.Length > 0)
        {
            var (exit, stdout, stderr) = await Run(manifest, exe, manifest.AuthStateArgs, workingDirectory, seatClass, ct);
            var said = Ansi.Strip(stdout + "\n" + stderr);
            signedIn = manifest.AuthStateSuccessPattern is { } pattern
                ? System.Text.RegularExpressions.Regex.IsMatch(said, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                : exit == 0;
            // WHO THAT SIGN-IN BILLS (U-billing-classes), off the same text: the plan, a key, or unknown — never guessed.
            if (signedIn) billing = seat.BillingClassOf(said);
        }
        return new SeatReading(features, mcp, signedIn, billing);
    }

    static async Task<string> Read(RuntimeManifest manifest, string exe, string[] args, string cwd, string seatClass, CancellationToken ct)
    {
        var (exit, stdout, stderr) = await Run(manifest, exe, args, cwd, seatClass, ct);
        if (exit != 0)
            throw new InvalidOperationException(
                $"`{string.Join(' ', args.Take(2))}` exited {exit}: {Ansi.Strip(stderr).Trim().Split('\n').FirstOrDefault()}");
        return stdout;
    }

    static async Task<(int Exit, string StdOut, string StdErr)> Run(RuntimeManifest manifest, string exe, IEnumerable<string> args,
        string cwd, string seatClass, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = cwd,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        CliAgentRuntime.SetCommand(psi, exe, args);
        AgentEnvironment.Apply(psi, new Dictionary<string, string>(), manifest, seatClass);

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

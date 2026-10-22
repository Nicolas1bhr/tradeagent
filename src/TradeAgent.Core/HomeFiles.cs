using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TradeAgent.Core;

/// <summary>A name inside a home that <see cref="HomeFiles"/> would have had to follow a link, or step through a non-folder, to act on.</summary>
public sealed class HomeLinkRefused(string message) : IOException(message);

/// <summary>
/// One name in a home's folder as <see cref="HomeFiles.List"/> or <see cref="HomeFiles.Describe"/> saw it: never followed, so a link is
/// said to be one (its length and time are the link's own, never its target's).
/// </summary>
public sealed record HomeEntry(string Name, bool Folder, bool Link, long Length, DateTime WrittenUtc = default);

/// <summary>
/// ACTING INSIDE A HOME A CELL CAN WRITE (<c>U-execution-environment</c> fix pass 3; the re-review's P1-a, P1-b and P2). An executor's
/// cell writes its whole home while the UNCONFINED app reads and writes in that same folder — its file tools, the start that rebuilds
/// the home, the relay that reads its reports, the canon put back before each turn — so every name below the home is one the cell may
/// have planted, and may plant again between any check and any use. Every such app-side read or write goes through here, one body:
/// <list type="bullet">
/// <item><b>No link is followed at any component below the root</b>: on Linux and macOS each folder is opened from the one above it
/// with <c>openat(…, O_DIRECTORY|O_NOFOLLOW)</c> and the last name with <c>O_NOFOLLOW</c>, so what is checked is the descriptor that is
/// used — there is no window between them. The root itself is opened by its path: its ancestry is the app's choice, not the cell's.</item>
/// <item><b>A read is of a regular file with one name</b> (<c>fstat</c> on the opened descriptor: a regular file, link count 1), so a
/// hard link to a file outside is refused too.</item>
/// <item><b>A write unlinks whatever is at the name without following it, then creates the file exclusively</b>
/// (<c>O_CREAT|O_EXCL|O_NOFOLLOW</c>); a name planted again between the two refuses the write.</item>
/// <item><b>A folder is created without following a link</b> and a link found where one belongs is refused — or, for the app's own
/// folders as a start rebuilds them (<c>replaceLinks</c>), the link itself is unlinked, never what it points at, and the folder made.</item>
/// <item><b>A refusal is a <see cref="HomeLinkRefused"/> (an <see cref="IOException"/>), never a fallback that follows the link.</b></item>
/// </list>
/// <para><b>Weaker elsewhere, said:</b> on Windows — where no cell runs (<c>CellPlatform.Refusal</c>) — each component is checked for a
/// reparse point immediately before a path-based open, which is check-then-use; on macOS a listing's NAMES are read by the folder's path
/// once its descriptor was opened without following (each entry is then examined through that descriptor); Linux lists through the
/// descriptor itself.</para>
/// </summary>
public static class HomeFiles
{
    /// <summary>A test seam no product path sets: handed the full path just before the last name is opened (reads) or created (writes).</summary>
    internal static Action<string>? BeforeOpen;

    /// <summary>
    /// The first <paramref name="max"/> bytes of the regular file at <paramref name="relative"/> under <paramref name="root"/>, and its
    /// whole length. <see cref="FileNotFoundException"/> where nothing is there; <see cref="HomeLinkRefused"/> for a link anywhere below
    /// the root, a folder or a file with more than one name.
    /// </summary>
    public static byte[] Read(string root, string relative, long max, out long length)
    {
        using var handle = OpenRead(root, relative);
        length = RandomAccess.GetLength(handle);
        var buffer = new byte[Math.Min(max, length)];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = RandomAccess.Read(handle, buffer.AsSpan(read), read);
            if (n == 0) break;
            read += n;
        }
        return read == buffer.Length ? buffer : buffer[..read];
    }

    /// <summary>The whole of the regular file at <paramref name="relative"/>, as UTF-8 text; refused as <see cref="Read"/> refuses.</summary>
    public static string ReadAllText(string root, string relative)
    {
        // AS File.ReadAllText READS: UTF-8 unless a byte-order mark says otherwise, the mark itself not returned.
        using var reader = new StreamReader(new MemoryStream(Read(root, relative, long.MaxValue, out _)), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> as a fresh file at <paramref name="relative"/>: its folders made without following a link (or a
    /// planted link replaced by a folder, when <paramref name="replaceLinks"/>), whatever is at the name unlinked unread, the file
    /// created exclusively. A folder at the name refuses.
    /// </summary>
    public static void Write(string root, string relative, ReadOnlySpan<byte> bytes, bool replaceLinks = false)
    {
        var parts = Parts(relative, allowRoot: false);
        if (OperatingSystem.IsWindows()) { WindowsWrite(root, parts, bytes, replaceLinks); return; }
        var dir = Descend(root, parts, parts.Length - 1, create: true, replaceLinks);
        try
        {
            var name = parts[^1];
            Unlink(dir, name, relative);
            BeforeOpen?.Invoke(Full(root, parts));
            var fd = openat(dir, name, F.WriteOnly | F.Create | F.Exclusive | F.NoFollow | F.CloseOnExec, 0x1B6);
            if (fd < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                throw errno == F.EEXIST
                    ? new HomeLinkRefused($"'{relative}' was put back while TradeAgent was replacing it, so it was not written.")
                    : Failure(relative, "created", errno);
            }
            using var handle = new SafeFileHandle(fd, ownsHandle: true);
            if (OperatingSystem.IsMacOS()) fchmod(fd, 0x1A4);   // the create's mode is variadic, which Apple's arm64 passes apart
            RandomAccess.Write(handle, bytes, 0);
        }
        finally { close(dir); }
    }

    /// <summary><see cref="Write"/> of <paramref name="text"/> as UTF-8 with no byte-order mark, as <c>File.WriteAllText</c> writes it.</summary>
    public static void WriteAllText(string root, string relative, string text, bool replaceLinks = false) =>
        Write(root, relative, new UTF8Encoding(false).GetBytes(text), replaceLinks);

    /// <summary>Makes the folder at <paramref name="relative"/> and each above it, following no link; see <see cref="Write"/>.</summary>
    public static void CreateDirectory(string root, string relative, bool replaceLinks = false)
    {
        var parts = Parts(relative, allowRoot: false);
        if (OperatingSystem.IsWindows()) { WindowsFolders(root, parts, parts.Length, create: true, replaceLinks); return; }
        close(Descend(root, parts, parts.Length, create: true, replaceLinks));
    }

    /// <summary>Unlinks the name at <paramref name="relative"/> — a file or a link, never what it points at; nothing there is not a fault; a folder refuses.</summary>
    public static void Remove(string root, string relative)
    {
        var parts = Parts(relative, allowRoot: false);
        if (OperatingSystem.IsWindows())
        {
            try { WindowsFolders(root, parts, parts.Length - 1, create: false, replaceLinks: false); }
            catch (DirectoryNotFoundException) { return; }
            WindowsUnlink(Full(root, parts), relative);
            return;
        }
        int dir;
        try { dir = Descend(root, parts, parts.Length - 1, create: false, replaceLinks: false); }
        catch (DirectoryNotFoundException) { return; }
        try { Unlink(dir, parts[^1], relative); }
        finally { close(dir); }
    }

    /// <summary>
    /// The names in the folder at <paramref name="relative"/> (<c>"."</c> for the root), each examined without following it. The folder
    /// itself is reached as <see cref="Read"/> reaches a file; <see cref="DirectoryNotFoundException"/> where none is there.
    /// </summary>
    public static IReadOnlyList<HomeEntry> List(string root, string relative)
    {
        var parts = Parts(relative, allowRoot: true);
        if (OperatingSystem.IsWindows())
        {
            WindowsFolders(root, parts, parts.Length, create: false, replaceLinks: false);
            var at = Full(root, parts);
            return [.. new DirectoryInfo(at).EnumerateFileSystemInfos().Select(i => new HomeEntry(i.Name,
                i is DirectoryInfo, i.LinkTarget is not null || i.Attributes.HasFlag(FileAttributes.ReparsePoint), i is FileInfo f ? f.Length : 0))];
        }
        var dir = Descend(root, parts, parts.Length, create: false, replaceLinks: false);
        try
        {
            var names = Directory.EnumerateFileSystemEntries(OperatingSystem.IsLinux() ? $"/proc/self/fd/{dir}" : Full(root, parts))
                .Select(Path.GetFileName).OfType<string>().ToList();
            var entries = new List<HomeEntry>(names.Count);
            // EACH NAME EXAMINED WHERE IT IS, NEVER OPENED OR FOLLOWED (fstatat/statx with AT_SYMLINK_NOFOLLOW): a link is listed as one,
            // and a file the app may not open is still listed — a pass that skipped it would count it gone.
            foreach (var name in names)
                if (Entry(dir, name) is { } entry) entries.Add(entry);   // gone since the listing: not listed
            return entries;
        }
        finally { close(dir); }
    }

    /// <summary>
    /// Creates <paramref name="relative"/> as a NEW file holding <paramref name="bytes"/> — false, and nothing written, where the name is
    /// already taken by anything at all (a file, a folder, a link: never followed, never replaced). Its folders are made without following
    /// a link. With <paramref name="flush"/> the bytes are on disk before this returns.
    /// </summary>
    public static bool TryCreate(string root, string relative, ReadOnlySpan<byte> bytes, bool flush = false)
    {
        var parts = Parts(relative, allowRoot: false);
        if (OperatingSystem.IsWindows())
        {
            WindowsFolders(root, parts, parts.Length - 1, create: true, replaceLinks: false);
            var full = Full(root, parts);
            if (File.Exists(full) || Directory.Exists(full)) return false;
            FileStream fs;
            try { fs = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            catch (IOException) when (File.Exists(full) || Directory.Exists(full)) { return false; }
            using (fs) { fs.Write(bytes); if (flush) fs.Flush(flushToDisk: true); }
            return true;
        }
        var dir = Descend(root, parts, parts.Length - 1, create: true, replaceLinks: false);
        try
        {
            var fd = openat(dir, parts[^1], F.WriteOnly | F.Create | F.Exclusive | F.NoFollow | F.CloseOnExec, 0x1B6);
            if (fd < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno == F.EEXIST || errno == F.ELOOP || errno == F.EISDIR) return false;
                throw Failure(relative, "created", errno);
            }
            using var handle = new SafeFileHandle(fd, ownsHandle: true);
            if (OperatingSystem.IsMacOS()) fchmod(fd, 0x1A4);
            RandomAccess.Write(handle, bytes, 0);
            if (flush && fsync(fd) != 0) throw Failure(relative, "flushed", Marshal.GetLastPInvokeError());
            return true;
        }
        finally { close(dir); }
    }

    /// <summary>
    /// Moves the name <paramref name="from"/> to <paramref name="to"/>, replacing whatever file or link is there — the name itself, never
    /// what a link points at, and never through a link at any folder of either path (<c>renameat</c> between two descriptors). The
    /// destination's folder must exist; a folder at the destination refuses.
    /// </summary>
    public static void Move(string root, string from, string to)
    {
        var src = Parts(from, allowRoot: false);
        var dst = Parts(to, allowRoot: false);
        if (OperatingSystem.IsWindows())
        {
            WindowsFolders(root, src, src.Length - 1, create: false, replaceLinks: false);
            WindowsFolders(root, dst, dst.Length - 1, create: false, replaceLinks: false);
            File.Move(Full(root, src), Full(root, dst), overwrite: true);
            return;
        }
        var fromDir = Descend(root, src, src.Length - 1, create: false, replaceLinks: false);
        try
        {
            var toDir = Descend(root, dst, dst.Length - 1, create: false, replaceLinks: false);
            try
            {
                if (renameat(fromDir, src[^1], toDir, dst[^1]) != 0)
                {
                    var errno = Marshal.GetLastPInvokeError();
                    throw errno == F.ENOENT ? new FileNotFoundException($"'{from}' does not exist.")
                        : errno == F.EISDIR || errno == F.ENOTEMPTY || errno == F.EEXIST
                            ? new HomeLinkRefused($"'{to}' is a folder, so TradeAgent did not move '{from}' over it.")
                            : Failure(from, "moved", errno);
                }
            }
            finally { close(toDir); }
        }
        finally { close(fromDir); }
    }

    /// <summary>
    /// What is at <paramref name="relative"/>, examined without opening or following it — a link is said to be one — or null where
    /// nothing is. A link or a non-folder at a folder ABOVE it refuses (<see cref="HomeLinkRefused"/>); a folder above it that is not
    /// there is null.
    /// </summary>
    public static HomeEntry? Describe(string root, string relative)
    {
        var parts = Parts(relative, allowRoot: false);
        if (OperatingSystem.IsWindows())
        {
            try { WindowsFolders(root, parts, parts.Length - 1, create: false, replaceLinks: false); }
            catch (DirectoryNotFoundException) { return null; }
            FileSystemInfo info = new FileInfo(Full(root, parts));
            if (!info.Exists) { info = new DirectoryInfo(info.FullName); if (!info.Exists) return null; }
            var link = info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint);
            return new HomeEntry(parts[^1], info is DirectoryInfo && !link, link, info is FileInfo f && !link ? f.Length : 0, info.LastWriteTimeUtc);
        }
        int dir;
        try { dir = Descend(root, parts, parts.Length - 1, create: false, replaceLinks: false); }
        catch (DirectoryNotFoundException) { return null; }
        try { return Entry(dir, parts[^1]); }
        finally { close(dir); }
    }

    /// <summary>One name in the folder <paramref name="dir"/>, examined without following it; null where nothing is there.</summary>
    static HomeEntry? Entry(int dir, string name)
    {
        var at = StatAt(dir, name);
        if (at is not { } s) return null;
        var type = s.Mode & S_IFMT;
        return new HomeEntry(name, type == S_IFDIR, type == S_IFLNK, type == S_IFDIR ? 0 : s.Size, s.WrittenUtc);
    }

    // ---- Linux and macOS ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The regular file at <paramref name="relative"/>, opened for reading with no link followed at any component. With
    /// <paramref name="oneName"/> (the default) a file with another name somewhere else (a hard link) is refused too; a caller that only
    /// MEASURES what a name in the home holds — the material pass's hash — may take one. The caller disposes the handle.
    /// </summary>
    public static SafeFileHandle OpenRead(string root, string relative, bool oneName = true)
    {
        var parts = Parts(relative, allowRoot: false);
        if (OperatingSystem.IsWindows())
        {
            WindowsFolders(root, parts, parts.Length - 1, create: false, replaceLinks: false);
            var full = Full(root, parts);
            BeforeOpen?.Invoke(full);
            var info = new FileInfo(full);
            if (info.Exists && (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                throw Linked(relative, parts[^1]);
            if (Directory.Exists(full)) throw new HomeLinkRefused($"'{relative}' is not a file.");
            return File.OpenHandle(full);
        }
        var dir = Descend(root, parts, parts.Length - 1, create: false, replaceLinks: false);
        try
        {
            BeforeOpen?.Invoke(Full(root, parts));
            var fd = openat(dir, parts[^1], F.ReadOnly | F.NoFollow | F.NonBlock | F.CloseOnExec, 0);
            if (fd < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                throw errno == F.ELOOP ? Linked(relative, parts[^1])
                    : errno == F.ENOENT ? new FileNotFoundException($"'{relative}' does not exist.")
                    : Failure(relative, "opened", errno);
            }
            var handle = new SafeFileHandle(fd, ownsHandle: true);
            // THE DESCRIPTOR IS CLOSED ON EVERY WAY OUT BUT THE RETURN (fix pass 5, the fourth re-review's D4): a refusal below, or an
            // examination that throws, never leaves it open until finalization.
            try
            {
                var (mode, links) = Stat(fd);
                if ((mode & S_IFMT) != S_IFREG || (oneName && links != 1))
                    throw new HomeLinkRefused((mode & S_IFMT) != S_IFREG
                        ? $"'{relative}' is not a file."
                        : $"'{relative}' has another name somewhere else (a hard link), so TradeAgent does not read it.");
                // AS File.ReadAllBytes OPENS ON UNIX: a shared advisory lock, refused while another holds the file exclusively (.NET's
                // FileShare.None), so a file being rewritten under one is not read half-written.
                if (flock(fd, LockShared | LockNonBlocking) != 0 && Marshal.GetLastPInvokeError() == F.EWOULDBLOCK)
                    throw new IOException($"'{relative}' is open elsewhere and cannot be read now.");
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally { close(dir); }
    }

    /// <summary>
    /// The folder <paramref name="count"/> components down from the root, each opened from the one above it without following a link.
    /// The caller closes it.
    /// </summary>
    static int Descend(string root, string[] parts, int count, bool create, bool replaceLinks)
    {
        var fd = open(root, F.ReadOnly | F.Directory | F.CloseOnExec);
        if (fd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw errno == F.ENOENT ? new DirectoryNotFoundException($"the folder {root} does not exist.") : Failure(root, "opened", errno);
        }
        for (var i = 0; i < count; i++)
        {
            var name = parts[i];
            var next = openat(fd, name, F.ReadOnly | F.Directory | F.NoFollow | F.CloseOnExec, 0);
            if (next < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                var link = (errno == F.ELOOP || errno == F.ENOTDIR) && IsLink(fd, name);
                if (create && (errno == F.ENOENT || (link && replaceLinks)))
                {
                    // THE LINK ITSELF IS UNLINKED, NEVER WHAT IT POINTS AT, and only for the app's own folders as a start rebuilds them.
                    if (link && unlinkat(fd, name, 0) != 0) { var u = Marshal.GetLastPInvokeError(); close(fd); throw Failure(Sub(parts, i), "replaced", u); }
                    if (mkdirat(fd, name, 0x1FF) != 0 && Marshal.GetLastPInvokeError() is var m && m != F.EEXIST)
                    { close(fd); throw Failure(Sub(parts, i), "created", m); }
                    next = openat(fd, name, F.ReadOnly | F.Directory | F.NoFollow | F.CloseOnExec, 0);
                    if (next < 0) errno = Marshal.GetLastPInvokeError();
                }
                if (next < 0)
                {
                    close(fd);
                    var said = Sub(parts, i);
                    throw errno == F.ELOOP || link ? Linked(said, name)
                        : errno == F.ENOTDIR ? new HomeLinkRefused($"'{said}' is not a folder.")
                        : errno == F.ENOENT ? new DirectoryNotFoundException($"'{said}' does not exist.")
                        : Failure(said, "opened", errno);
                }
            }
            close(fd);
            fd = next;
        }
        return fd;
    }

    /// <summary>Whether <paramref name="name"/> in the folder <paramref name="dir"/> is a symbolic link, read without following it.</summary>
    static bool IsLink(int dir, string name)
    {
        var buf = new byte[1];
        return readlinkat(dir, name, buf, (nuint)buf.Length) >= 0;
    }

    /// <summary>Unlinks the name — never what a link points at; nothing there is not a fault; a folder is refused.</summary>
    static void Unlink(int dir, string name, string relative)
    {
        if (unlinkat(dir, name, 0) == 0) return;
        var errno = Marshal.GetLastPInvokeError();
        if (errno == F.ENOENT) return;
        throw errno == F.EISDIR || errno == F.EPERM
            ? new HomeLinkRefused($"'{relative}' is a folder, not a file, so TradeAgent did not replace it.")
            : Failure(relative, "replaced", errno);
    }

    /// <summary>The descriptor's file type and permission bits, and its link count.</summary>
    static (int Mode, long Links) Stat(int fd)
    {
        var buf = new byte[256];
        if (OperatingSystem.IsLinux())
        {
            // struct statx, the same on every architecture: stx_nlink at 16 (u32), stx_mode at 28 (u16).
            if (statx(fd, "", AtEmptyPath, StatxType | StatxMode | StatxNlink, buf) != 0)
                throw Failure($"descriptor {fd}", "examined", Marshal.GetLastPInvokeError());
            return (BitConverter.ToUInt16(buf, 28), BitConverter.ToUInt32(buf, 16));
        }
        // macOS's struct stat (64-bit inodes): st_mode at 4 (u16), st_nlink at 6 (u16).
        var rc = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? fstat64(fd, buf) : fstat(fd, buf);
        if (rc != 0) throw Failure($"descriptor {fd}", "examined", Marshal.GetLastPInvokeError());
        return (BitConverter.ToUInt16(buf, 4), BitConverter.ToUInt16(buf, 6));
    }

    /// <summary>
    /// The name's file type and permission bits, its size and its last write time (UTC), examined WITHOUT FOLLOWING it and without
    /// opening it; null where nothing is there.
    /// </summary>
    static (int Mode, long Size, DateTime WrittenUtc)? StatAt(int dir, string name)
    {
        var buf = new byte[256];
        int rc;
        long size, sec, nsec;
        int mode;
        if (OperatingSystem.IsLinux())
        {
            // struct statx: stx_mode at 28 (u16), stx_size at 40 (u64), stx_mtime at 112 (tv_sec i64, tv_nsec u32 at 120).
            rc = statx(dir, name, AtSymlinkNoFollow, StatxType | StatxMode | StatxSize | StatxMtime, buf);
            if (rc == 0)
            {
                mode = BitConverter.ToUInt16(buf, 28);
                size = (long)BitConverter.ToUInt64(buf, 40);
                sec = BitConverter.ToInt64(buf, 112);
                nsec = BitConverter.ToUInt32(buf, 120);
                return (mode, size, Written(sec, nsec));
            }
        }
        else
        {
            // macOS's struct stat (64-bit inodes): st_mode at 4 (u16), st_mtimespec at 48 (i64, i64), st_size at 96 (i64).
            rc = RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? fstatat64(dir, name, buf, MacAtSymlinkNoFollow)
                : fstatat(dir, name, buf, MacAtSymlinkNoFollow);
            if (rc == 0)
            {
                mode = BitConverter.ToUInt16(buf, 4);
                size = BitConverter.ToInt64(buf, 96);
                sec = BitConverter.ToInt64(buf, 48);
                nsec = BitConverter.ToInt64(buf, 56);
                return (mode, size, Written(sec, nsec));
            }
        }
        var errno = Marshal.GetLastPInvokeError();
        if (errno == F.ENOENT) return null;
        throw Failure(name, "examined", errno);
    }

    /// <summary>A write time as .NET reads one from the same fields (<c>FileSystemInfo.LastWriteTimeUtc</c>), to the tick.</summary>
    static DateTime Written(long sec, long nsec) =>
        DateTimeOffset.FromUnixTimeSeconds(sec).AddTicks(nsec / TimeSpan.NanosecondsPerTick).UtcDateTime;

    const int S_IFMT = 0xF000, S_IFDIR = 0x4000, S_IFREG = 0x8000, S_IFLNK = 0xA000;
    const int AtEmptyPath = 0x1000, AtSymlinkNoFollow = 0x100, MacAtSymlinkNoFollow = 0x20;
    const uint StatxType = 0x1, StatxMode = 0x2, StatxNlink = 0x4, StatxMtime = 0x40, StatxSize = 0x200;
    const int LockShared = 1, LockNonBlocking = 4;

    /// <summary>The open flags and error numbers, as this platform numbers them.</summary>
    static class F
    {
        static readonly bool Mac = OperatingSystem.IsMacOS();
        static readonly bool Arm = RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm;
        public const int ReadOnly = 0, WriteOnly = 1;
        public static readonly int Create = Mac ? 0x200 : 0x40;
        public static readonly int Exclusive = Mac ? 0x800 : 0x80;
        public static readonly int NonBlock = Mac ? 0x4 : 0x800;
        public static readonly int NoFollow = Mac ? 0x100 : Arm ? 0x8000 : 0x20000;
        public static readonly int Directory = Mac ? 0x100000 : Arm ? 0x4000 : 0x10000;
        public static readonly int CloseOnExec = Mac ? 0x1000000 : 0x80000;
        public const int EPERM = 1, ENOENT = 2, EEXIST = 17, ENOTDIR = 20, EISDIR = 21;
        public static readonly int ELOOP = Mac ? 62 : 40;
        public static readonly int ENOTEMPTY = Mac ? 66 : 39;
        public static readonly int EWOULDBLOCK = Mac ? 35 : 11;
    }

    [DllImport("libc", SetLastError = true)]
    static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    static extern int openat(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags, int mode);

    [DllImport("libc", SetLastError = true)]
    static extern int mkdirat(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int mode);

    [DllImport("libc", SetLastError = true)]
    static extern int unlinkat(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);

    [DllImport("libc", SetLastError = true)]
    static extern nint readlinkat(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, byte[] buf, nuint size);

    [DllImport("libc", SetLastError = true)]
    static extern int fchmod(int fd, int mode);

    [DllImport("libc", SetLastError = true)]
    static extern int fsync(int fd);

    [DllImport("libc", SetLastError = true)]
    static extern int flock(int fd, int operation);

    [DllImport("libc", SetLastError = true)]
    static extern int renameat(int fromDir, [MarshalAs(UnmanagedType.LPUTF8Str)] string from, int toDir,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string to);

    [DllImport("libc", SetLastError = true)]
    static extern int fstatat(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, byte[] buf, int flags);

    [DllImport("libc", EntryPoint = "fstatat$INODE64", SetLastError = true)]
    static extern int fstatat64(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, byte[] buf, int flags);

    [DllImport("libc", SetLastError = true)]
    static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    static extern int statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, byte[] buf);

    [DllImport("libc", SetLastError = true)]
    static extern int fstat(int fd, byte[] buf);

    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    static extern int fstat64(int fd, byte[] buf);

    // ---- Windows: no cell runs there; each component is checked for a reparse point before a path-based use -----------------------

    static void WindowsFolders(string root, string[] parts, int count, bool create, bool replaceLinks)
    {
        var at = root;
        for (var i = 0; i < count; i++)
        {
            at = Path.Combine(at, parts[i]);
            var info = new DirectoryInfo(at);
            if (info.Exists || File.Exists(at))
            {
                if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    if (!(create && replaceLinks)) throw Linked(Sub(parts, i), parts[i]);
                    if (info.Exists) info.Delete(); else File.Delete(at);   // the link itself, never its target
                }
                else if (!info.Exists) throw new HomeLinkRefused($"'{Sub(parts, i)}' is not a folder.");
                else continue;
            }
            if (!create) throw new DirectoryNotFoundException($"'{Sub(parts, i)}' does not exist.");
            Directory.CreateDirectory(at);
        }
    }

    static void WindowsWrite(string root, string[] parts, ReadOnlySpan<byte> bytes, bool replaceLinks)
    {
        WindowsFolders(root, parts, parts.Length - 1, create: true, replaceLinks);
        var full = Full(root, parts);
        WindowsUnlink(full, string.Join('/', parts));
        BeforeOpen?.Invoke(full);
        using var fs = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        fs.Write(bytes);
    }

    static void WindowsUnlink(string full, string relative)
    {
        if (Directory.Exists(full) && new DirectoryInfo(full) is var d && d.LinkTarget is null && !d.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new HomeLinkRefused($"'{relative}' is a folder, not a file, so TradeAgent did not replace it.");
        if (Directory.Exists(full)) Directory.Delete(full); else File.Delete(full);
    }

    // ---- shared ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The path's names, split on <c>/</c>. A <c>.</c> names the folder it is in and is dropped, as <see cref="Path.GetFullPath(string)"/>
    /// drops it — fix pass 4 (the second re-review's [P3] 7): <c>./out/x</c> is <c>out/x</c>, as the file tools' path check has always read it.
    /// An absolute path or a <c>..</c> is refused, never resolved; so is a path that names no name at all, except where the root is asked for.
    /// <para><b>Split on <c>/</c> alone</b> (fix pass 5, the fourth re-review's D1): on Linux and macOS a backslash is a character of a name,
    /// and a listed name such as <c>..\x</c> or <c>a\b</c> is the one file it names — split, the first would refuse a whole material pass
    /// (the window held open) and the second would measure the file <c>a/b</c> as <c>a\b</c>. This is what the code did before by accident:
    /// <c>Split('/', '\\', options)</c> binds the backslash as a COUNT, not a separator. On Windows that same accident means a relative
    /// path written with backslashes reaches <see cref="WindowsFolders"/> as fewer components than it has, so not every folder of it is
    /// checked for a reparse point — kept as it was here and named for <c>U-cell-windows</c>' card.</para>
    /// </summary>
    static string[] Parts(string relative, bool allowRoot)
    {
        if (string.IsNullOrEmpty(relative)) return allowRoot ? [] : throw new HomeLinkRefused($"'{relative}' is not a path inside the folder.");
        if (Path.IsPathRooted(relative))
            throw new HomeLinkRefused($"'{relative}' is not a path inside the folder.");
        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".").ToArray();
        if (parts.Any(p => p == ".."))
            throw new HomeLinkRefused($"'{relative}' is not a path inside the folder.");
        if (parts.Length == 0)
            return allowRoot ? [] : throw new HomeLinkRefused($"'{relative}' is not a path inside the folder.");
        return parts;
    }

    static string Full(string root, string[] parts) => parts.Length == 0 ? root : Path.Combine([root, .. parts]);

    static string Sub(string[] parts, int i) => string.Join('/', parts[..(i + 1)]);

    static HomeLinkRefused Linked(string relative, string name) =>
        new($"'{relative}' goes through '{name}', which is a link. TradeAgent never follows a link inside a folder an AI can write.");

    static IOException Failure(string what, string verb, int errno) =>
        new($"'{what}' could not be {verb}: {Marshal.GetPInvokeErrorMessage(errno)} (errno {errno}).");
}

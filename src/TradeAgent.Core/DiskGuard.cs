using System.Runtime.InteropServices;

namespace TradeAgent.Core;

/// <summary>
/// What one read of a filesystem found: the bytes this account may still write there, and the filesystem's whole size; and, where the filesystem has a
/// fixed count of files (inodes), how many more files this account may still make and how many it holds in all (<c>U-disk-guard-2</c>; A4-2). Both
/// file figures are 0 where there is no such count: macOS and Windows are not read for one, and btrfs reports none.
/// </summary>
public sealed record DiskSpace(long Free, long Total, long FilesFree = 0, long Files = 0)
{
    /// <summary>
    /// THE FILESYSTEM HOLDING <paramref name="path"/>, or null when it could not be read. Never throws: a read that failed is an answer the
    /// guard has to act on, not an error. <see cref="DriveInfo"/> asks statfs of the path itself on macOS and Linux, so a home on a filesystem
    /// of its own is read as that one, and asks for the path's drive on Windows. <see cref="DriveInfo.AvailableFreeSpace"/> is what this
    /// account can use, so the blocks a filesystem keeps back for root count as gone, as they are for everything the app runs.
    ///
    /// <para><b>On Linux the files too</b>, by <c>statvfs</c> of the same path: <c>f_favail</c>, the files an unprivileged account may still make, and
    /// <c>f_files</c>. A cell can use up a filesystem's inodes with empty files while its bytes read plenty free, and ext4 keeps none back for root, so the
    /// owner's other services cannot make a file either. A read whose files could not be had is no read: the guard must see both.
    /// <see cref="FilesOn"/> says what reads as no fixed count instead.</para>
    /// </summary>
    public static DiskSpace? Read(string path)
    {
        try
        {
            var drive = new DriveInfo(path);
            var (free, total) = (drive.AvailableFreeSpace, drive.TotalSize);
            if (free < 0 || total <= 0) return null;
            if (!OperatingSystem.IsLinux() || !Environment.Is64BitProcess) return new DiskSpace(free, total);
            return FilesOn(path) is var (filesFree, files) ? new DiskSpace(free, total, filesFree, files) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                       or DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    /// <summary>
    /// <c>statvfs</c>'s <c>f_favail</c> and <c>f_files</c> on 64-bit Linux, where <c>struct statvfs</c> is eleven 8-byte words and six ints (glibc and musl
    /// alike, x86-64 and arm64): <c>f_files</c> at 40, <c>f_favail</c> at 56. Null when the call failed. Figures that make no sense as a fixed count (more
    /// free than in all, or past what a signed word holds, as some network and FUSE filesystems report) are read as no fixed count, 0 and 0, as btrfs's
    /// are: the bytes still count, and the guard is not blinded by a filesystem that does not keep the figure (opus review).
    /// </summary>
    static (long FilesFree, long Files)? FilesOn(string path)
    {
        var buf = Marshal.AllocHGlobal(StatvfsSize);
        try
        {
            if (statvfs(path, buf) != 0) return null;
            return FixedCount(Marshal.ReadInt64(buf, 40), Marshal.ReadInt64(buf, 56));
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary><c>f_files</c> and <c>f_favail</c> as a fixed count, or 0 and 0 where they make no sense as one (see <see cref="FilesOn"/>).</summary>
    internal static (long FilesFree, long Files) FixedCount(long files, long favail) => files > 0 && favail >= 0 && favail <= files ? (favail, files) : (0, 0);

    /// <summary>Room for <c>struct statvfs</c> (112 bytes on 64-bit Linux) with some to spare.</summary>
    const int StatvfsSize = 256;

    [DllImport("libc", EntryPoint = "statvfs", SetLastError = true)]
    static extern int statvfs(string path, nint buf);
}

/// <summary>
/// THE FLOOR THE AI STOPS AT: 10 % of the filesystem, at most <see cref="Most"/> and never under <see cref="Least"/>.
///
/// <para><b>Why this shape.</b> A flat share fails a large server: 10 % of 2 TB stops the AI with 200 GB free. A flat figure fails a small
/// laptop: 10 GB of a 64 GB disk is more than it may ever have spare. So 10 % until that passes 10 GB, which on the owner's server leaves his
/// other services their room and on a 128 GB laptop stops well before the machine does. <see cref="Least"/> is the 2 GB the Doctor has always
/// asked for, under which the app's own ledgers are at risk whatever the disk's size.</para>
///
/// <para><b>Why 10 GB holds.</b> A plain process was measured writing 2.1 to 3.1 GB/s on this project's Mac's SSD, and the guard reads every
/// <see cref="DiskWatch.Every"/>: about 5 GB gets through between two reads, and a little more while the turns end. A faster disk gets more through;
/// the owner's server's own rate was not measured.</para>
/// </summary>
public static class DiskFloor
{
    const long GiB = 1L << 30;

    public const long Most = 10 * GiB;

    public const long Least = 2 * GiB;

    /// <summary>The floor of a filesystem of <paramref name="total"/> bytes.</summary>
    public static long For(long total) => Math.Max(Least, Math.Min(total / 10, Most));

    /// <summary>
    /// WHAT THE OWNER'S PRESS NEEDS AFTER A TRIP: half as much again as the floor. A press just above the floor would be paused again by the
    /// next few hundred megabytes, so the room to work has to be real before the AI goes back.
    /// </summary>
    public static long ToResume(long total) => For(total) / 2 * 3;

    /// <summary>The least the files floor is, where the filesystem holds more than four times as many: about two seconds of a cell making empty files.</summary>
    public const long FilesLeast = 500_000;

    /// <summary>
    /// THE FILES FLOOR (<c>U-disk-guard-2</c>; A4-2): a tenth of the filesystem's files, never under <see cref="FilesLeast"/>, and never over a quarter of them,
    /// so a filesystem of fewer than two million files (ext4's default count for 32 GB) is not held at most or all of them. 0 for a filesystem with no fixed
    /// count, which has no files floor. Unlike bytes there is no most: a tenth of a large filesystem's files is never something its owner would miss.
    /// </summary>
    public static long FilesFor(long files) => files <= 0 ? 0 : Math.Min(Math.Max(files / 10, FilesLeast), files / 4);

    /// <summary>What the owner's press needs after a trip, in files: half as much again as the files floor, as for bytes.</summary>
    public static long FilesToResume(long files) => FilesFor(files) / 2 * 3;
}

/// <summary>
/// WHY THE GUARD PAUSED THE AI, as it stood then. Written in one settings write with the owner's own "works on its own" flag, and cleared only
/// by his press (<see cref="DiskWatch.MayResume"/>). <see cref="Unreadable"/> is a guard that lost its sight: no figures, because none was read.
/// </summary>
public sealed record DiskPause
{
    public DateTimeOffset At { get; init; }

    public long Free { get; init; }

    public long Total { get; init; }

    public long Floor { get; init; }

    public bool Unreadable { get; init; }

    /// <summary>The files that could still be made, the filesystem's whole count and the files floor, as read then; all 0 where it has no fixed count.</summary>
    public long FilesFree { get; init; }

    public long Files { get; init; }

    public long FilesFloor { get; init; }

    /// <summary>Whether this pause was for files, with bytes above their floor: the words then say what ran out.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ForFiles => !Unreadable && Free >= Floor && FilesFloor > 0 && FilesFree < FilesFloor;
}

/// <summary>What one look decided.</summary>
public enum DiskVerdict
{
    /// <summary>Enough space.</summary>
    Fine,

    /// <summary>Under the floor.</summary>
    Low,

    /// <summary>This read failed, and not yet for long enough to act on.</summary>
    Missed,

    /// <summary>The reads have failed for <see cref="DiskWatch.BlindReads"/> in a row: the guard cannot see the disk.</summary>
    Blind
}

/// <summary>One look's verdict, and the pause it calls for (null: none).</summary>
public sealed record DiskLook(DiskVerdict Verdict, DiskPause? Trip);

/// <summary>
/// THE FREE-SPACE GUARD'S DECISIONS, with no disk and no host in them. It decides; the host reads the disk and does the pausing through the
/// owner's own path. Nothing here can start, widen or resume anything: its only act is a pause, and only the owner's press ends one.
/// </summary>
public sealed class DiskWatch
{
    /// <summary>
    /// HOW OFTEN THE DISK IS READ. A statfs takes about a microsecond (measured), and at the 2.5 GB/s a plain process was measured writing, half a minute
    /// would let 75 GB past a 10 GB floor. So the read is frequent enough for the floor to mean something.
    /// </summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(2);

    /// <summary>
    /// HOW MANY FAILED READS IN A ROW MAKE THE GUARD BLIND: half a minute of them. One bad read pauses nothing, and a watch that cannot see for
    /// that long stops the AI rather than let it go on unwatched: unattended work beside the owner's services is what the guard is for.
    /// </summary>
    public const int BlindReads = 15;

    int _missed;

    /// <summary>
    /// ONE READ. <paramref name="aiOn"/> is whether anything is left to pause: a low disk under a stopped AI is said on the pages, and trips nothing.
    /// </summary>
    public DiskLook Look(DiskSpace? reading, DateTimeOffset now, bool aiOn)
    {
        if (reading is null)
        {
            if (++_missed < BlindReads) return new(DiskVerdict.Missed, null);
            return new(DiskVerdict.Blind, aiOn ? new DiskPause { At = now, Unreadable = true } : null);
        }
        _missed = 0;
        return Below(reading) ? new(DiskVerdict.Low, aiOn ? PauseFor(reading, now) : null) : new(DiskVerdict.Fine, null);
    }

    /// <summary>The owner's press read the disk and went through: that is a good read, and the count of failed ones starts again from it.</summary>
    public void Sighted() => _missed = 0;

    /// <summary>Under the bytes floor, or under the files floor where the filesystem has a fixed count of files.</summary>
    static bool Below(DiskSpace reading) => reading.Free < DiskFloor.For(reading.Total) || reading.FilesFree < DiskFloor.FilesFor(reading.Files);

    /// <summary>The record of a pause over <paramref name="reading"/>.</summary>
    public static DiskPause PauseFor(DiskSpace reading, DateTimeOffset now) =>
        new()
        {
            At = now, Free = reading.Free, Total = reading.Total, Floor = DiskFloor.For(reading.Total),
            FilesFree = reading.FilesFree, Files = reading.Files, FilesFloor = DiskFloor.FilesFor(reading.Files)
        };

    /// <summary>
    /// WHETHER THE OWNER'S PRESS MAY PUT THE AI TO WORK, given the pause standing (null: none) and a read taken at the press. A tripped guard
    /// needs <see cref="DiskFloor.ToResume"/>, and a read that failed refuses it. With nothing tripped only the floor counts, and a failed read
    /// lets the press through: the guard's own reads carry on from there. Files count the same way as bytes, where the filesystem has a fixed count.
    /// </summary>
    public static bool MayResume(DiskPause? standing, DiskSpace? reading)
    {
        if (reading is null) return standing is null;
        return reading.Free >= (standing is null ? DiskFloor.For(reading.Total) : DiskFloor.ToResume(reading.Total))
               && reading.FilesFree >= (standing is null ? DiskFloor.FilesFor(reading.Files) : DiskFloor.FilesToResume(reading.Files));
    }
}

namespace TradeAgent.Core;

/// <summary>What one read of a filesystem found: the bytes this account may still write there, and the filesystem's whole size.</summary>
public sealed record DiskSpace(long Free, long Total)
{
    /// <summary>
    /// THE FILESYSTEM HOLDING <paramref name="path"/>, or null when it could not be read. Never throws: a read that failed is an answer the
    /// guard has to act on, not an error. <see cref="DriveInfo"/> asks statfs of the path itself on macOS and Linux, so a home on a filesystem
    /// of its own is read as that one, and asks for the path's drive on Windows. <see cref="DriveInfo.AvailableFreeSpace"/> is what this
    /// account can use, so the blocks a filesystem keeps back for root count as gone, as they are for everything the app runs.
    /// </summary>
    public static DiskSpace? Read(string path)
    {
        try
        {
            var drive = new DriveInfo(path);
            var (free, total) = (drive.AvailableFreeSpace, drive.TotalSize);
            return free >= 0 && total > 0 ? new DiskSpace(free, total) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }
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

    static bool Below(DiskSpace reading) => reading.Free < DiskFloor.For(reading.Total);

    /// <summary>The record of a pause over <paramref name="reading"/>.</summary>
    public static DiskPause PauseFor(DiskSpace reading, DateTimeOffset now) =>
        new() { At = now, Free = reading.Free, Total = reading.Total, Floor = DiskFloor.For(reading.Total) };

    /// <summary>
    /// WHETHER THE OWNER'S PRESS MAY PUT THE AI TO WORK, given the pause standing (null: none) and a read taken at the press. A tripped guard
    /// needs <see cref="DiskFloor.ToResume"/>, and a read that failed refuses it. With nothing tripped only the floor counts, and a failed read
    /// lets the press through: the guard's own reads carry on from there.
    /// </summary>
    public static bool MayResume(DiskPause? standing, DiskSpace? reading)
    {
        if (reading is null) return standing is null;
        return reading.Free >= (standing is null ? DiskFloor.For(reading.Total) : DiskFloor.ToResume(reading.Total));
    }
}

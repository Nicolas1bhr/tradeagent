using System.Globalization;
using TradeAgent.Core;

namespace TradeAgent.App;

/// <summary>
/// WHAT THE FREE-SPACE GUARD SAYS TO THE OWNER (<c>U-disk-guard</c>): why the AI stopped, what still runs, and the one thing to do. Plain words: no file
/// names and no terminal. Each sentence reads after "paused" or stands alone, and none ends in a full stop, because the card and the at-a-glance line add theirs.
/// </summary>
static class DiskWords
{
    const string StillRunning = "Positions stay managed and the market recording goes on";

    /// <summary>Why the AI is paused, with what is free now when a read has been made since.</summary>
    public static string Paused(DiskPause pause, DiskSpace? now)
    {
        if (pause.Unreadable)
            return "paused because TradeAgent could not read how much space is free on this computer's disk, and it does not leave the AI working with nothing " +
                   $"watching the disk. {StillRunning}. Check the disk, then press \"{DashboardPage.LetTheAiWork}\". It will not start again by itself";
        if (pause.ForFiles)
        {
            var room = now is { } seen ? $" ({Count(seen.FilesFree)} can be made now)" : "";
            return $"paused to protect this computer's disk: it had room for only {Count(pause.FilesFree)} new files, and the AI stops under {Count(pause.FilesFloor)}. " +
                   $"{StillRunning}. Remove some files, then press \"{DashboardPage.LetTheAiWork}\" once there is room for at least {Count(DiskFloor.FilesToResume(pause.Files))} " +
                   $"new files{room}{BytesShort(now)}. It will not start again by itself";
        }
        var free = now is { } read ? $" ({Size(read.Free)} is free now)" : "";
        return $"paused to protect this computer's disk: {Size(pause.Free)} was free, and the AI stops under {Size(pause.Floor)}. {StillRunning}. Free some space, " +
               $"then press \"{DashboardPage.LetTheAiWork}\" once at least {Size(DiskFloor.ToResume(pause.Total))} is free{free}{FilesShort(now)}. It will not start again by itself";
    }

    /// <summary>
    /// WHAT A PRESS ALSO NEEDS, from the read now, when the half that did not trip is short of what a resume needs too: the press is refused on either, so the
    /// words name both (opus review). Empty when that half has its room, or nothing has been read.
    /// </summary>
    static string FilesShort(DiskSpace? now) =>
        now is { Files: > 0 } r && r.FilesFree < DiskFloor.FilesToResume(r.Files)
            ? $", and there is room for at least {Count(DiskFloor.FilesToResume(r.Files))} new files ({Count(r.FilesFree)} can be made now)" : "";

    static string BytesShort(DiskSpace? now) =>
        now is { } r && r.Free < DiskFloor.ToResume(r.Total) ? $", and at least {Size(DiskFloor.ToResume(r.Total))} is free ({Size(r.Free)} is free now)" : "";

    /// <summary>The activity log's line at a trip.</summary>
    public static string Tripped(DiskPause pause) => $"The AI was {Paused(pause, null)}.";

    /// <summary>The line for a press the disk refused.</summary>
    public static string Refused(DiskPause pause, DiskSpace? now) => $"The AI was not started. It stays {Paused(pause, now)}.";

    /// <summary>The health row over a healthy disk. Whole gigabytes, so the row changes (and writes) far less often than it reads.</summary>
    public static string Row(DiskSpace? read) =>
        read is { } r ? $"{r.Free / Gib:N0} GB free of {r.Total / Gib:N0} GB; the AI stops under {Size(DiskFloor.For(r.Total))}" : Unread;

    /// <summary>The health row over a low disk with no AI to pause.</summary>
    public static string LowRow(DiskSpace? read) =>
        read is not { } r ? Unread
        : r.Free < DiskFloor.For(r.Total) ? $"only {Size(r.Free)} free; the AI will not start under {Size(DiskFloor.For(r.Total))}"
        : $"room for only {Count(r.FilesFree)} new files; the AI will not start under {Count(DiskFloor.FilesFor(r.Files))}";

    /// <summary>The health row over a read that failed. One failed read changes nothing: half a minute of them pauses a working AI.</summary>
    public const string Unread = "the free space could not be read just now; a working AI carries on, and is paused if this lasts half a minute";

    const long Gib = 1L << 30;

    /// <summary>A count of files as the owner reads one: "400,000".</summary>
    static string Count(long files) => files.ToString("N0", CultureInfo.InvariantCulture);

    static string Size(long bytes) => (bytes / (double)Gib).ToString("0.#", CultureInfo.InvariantCulture) + " GB";
}

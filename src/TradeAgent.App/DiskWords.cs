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
        var free = now is { } read ? $" ({Size(read.Free)} is free now)" : "";
        return $"paused to protect this computer's disk: {Size(pause.Free)} was free, and the AI stops under {Size(pause.Floor)}. {StillRunning}. Free some space, " +
               $"then press \"{DashboardPage.LetTheAiWork}\" once at least {Size(DiskFloor.ToResume(pause.Total))} is free{free}. It will not start again by itself";
    }

    /// <summary>The activity log's line at a trip.</summary>
    public static string Tripped(DiskPause pause) => $"The AI was {Paused(pause, null)}.";

    /// <summary>The line for a press the disk refused.</summary>
    public static string Refused(DiskPause pause, DiskSpace? now) => $"The AI was not started. It stays {Paused(pause, now)}.";

    /// <summary>The health row over a healthy disk. Whole gigabytes, so the row changes (and writes) far less often than it reads.</summary>
    public static string Row(DiskSpace? read) =>
        read is { } r ? $"{r.Free / Gib:N0} GB free of {r.Total / Gib:N0} GB; the AI stops under {Size(DiskFloor.For(r.Total))}" : Unread;

    /// <summary>The health row over a low disk with no AI to pause.</summary>
    public static string LowRow(DiskSpace? read) =>
        read is { } r ? $"only {Size(r.Free)} free; the AI will not start under {Size(DiskFloor.For(r.Total))}" : Unread;

    /// <summary>The health row over a read that failed. One failed read changes nothing: half a minute of them pauses the AI.</summary>
    public const string Unread = "the free space could not be read just now; the AI carries on, and it is paused if this goes on for half a minute";

    const long Gib = 1L << 30;

    static string Size(long bytes) => (bytes / (double)Gib).ToString("0.#", CultureInfo.InvariantCulture) + " GB";
}

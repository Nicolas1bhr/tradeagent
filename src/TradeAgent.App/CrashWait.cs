using System.Globalization;
using TradeAgent.Core.Db;

namespace TradeAgent.App;

/// <summary>
/// A START SOON AFTER A START THAT DID NOT CLOSE PROPERLY (<c>U-service-limits</c>; R31 D12 § H and D18-5).
///
/// <para><b>Why.</b> On a host the service manager starts the app again after it dies, and every start resumes a working AI: a turn launches, the
/// next crash kills it, and the next start's meter charges that turn its whole reservation (a plan turn's is twice the seat's p99). A crash that
/// repeats on every start was therefore a charge on every start, as fast as the service manager restarted it. The unit now stops a fast loop
/// (packaging/linux/tradeagent.service), but a slower one goes on; this is the app's own half, on every platform.</para>
///
/// <para><b>What is read.</b> The app already writes "TradeAgent started" once it is up and "TradeAgent stopped" as the last line of every orderly quit
/// — the window's, the OS's, systemd's SIGTERM, the kit's restart. A crash writes nothing, and that absence is the evidence: when the newer of the
/// two lines, read BEFORE this start writes its own, is a start, that start did not close properly. A stop line the disk refused reads the same,
/// which errs on the side of waiting.</para>
///
/// <para><b>What it does.</b> Where that start was less than <see cref="Recent"/> ago and the AI would resume, the resume waits <see cref="Clean"/>: no
/// AI started and no loop, so no turn and no reservation. Everything else of the app runs as ever — the tape, the paper runs, the background passes,
/// the free-space guard. At the end it decides again from the owner's word as it stands then, so a pause pressed meanwhile stays a pause. A start
/// that waited and then died inside <see cref="Recent"/> makes the next one wait too, so a fault that kills the app as a turn starts costs at most
/// one turn per <see cref="Recent"/>, under the day's ceiling.</para>
/// </summary>
static class CrashWait
{
    /// <summary>The line every start writes once it is up (<see cref="AppHost.StartAsync"/>).</summary>
    public const string StartedLine = "TradeAgent started";

    /// <summary>The last line of every orderly quit (<see cref="AppHost.DisposeAsync"/>).</summary>
    public const string StoppedLine = "TradeAgent stopped";

    /// <summary>
    /// HOW RECENT THE START THAT DID NOT CLOSE PROPERLY MUST BE for this one to wait: half an hour, three times <see cref="Clean"/>, so a start that waited
    /// its ten minutes and then died as its first turn started is still inside it. A single crash after hours of running started longer ago than this
    /// and resumes at once: it is not a loop.
    /// </summary>
    public static readonly TimeSpan Recent = TimeSpan.FromMinutes(30);

    /// <summary>
    /// HOW LONG THE WAIT IS: ten minutes of a run that has not died. Longer than the unit's start limit takes to stop a fast loop (seven starts ten
    /// seconds apart and their start-up), and short beside the chief's four-hour ceiling.
    /// </summary>
    public static readonly TimeSpan Clean = TimeSpan.FromMinutes(10);

    /// <summary>
    /// WHEN THE LAST START BEGAN, where it did not close properly; null where it did, or there was none (a first start, or a log pruned past both
    /// lines). Read before this start writes its own line. A log that cannot be read THROWS: the caller treats that as a start that did not close.
    /// </summary>
    public static DateTimeOffset? DidNotClose(LogStore log) =>
        log.LastOf(StartedLine, StoppedLine) is { Text: StartedLine } last ? last.At : null;

    /// <summary>Whether a start that did not close properly at <paramref name="didNotClose"/> is recent enough at <paramref name="now"/> to wait for.</summary>
    public static bool Recently(DateTimeOffset? didNotClose, DateTimeOffset now) => didNotClose is { } at && now - at < Recent;

    /// <summary>The activity line at every start that finds the last one did not close properly, whatever the AI is doing.</summary>
    public static string Noticed(DateTimeOffset? at) => at is { } was
        ? $"The last time TradeAgent started, at {Local(was)}, it did not close properly."
        : "TradeAgent could not read whether its last start closed properly, so it counts it as one that did not.";

    /// <summary>The activity line when the resume waits.</summary>
    public static string Waits(DateTimeOffset until) =>
        $"The AI waits until {Local(until)} before it works on its own again, because the start before this one did not close properly soon after "
        + $"it began, and a fault that repeats must not keep starting the AI. {StillRunning}.";

    /// <summary>What the Dashboard card and the at-a-glance line say while the wait stands. No full stop: the card adds its own.</summary>
    public static string Card(DateTimeOffset until) =>
        $"waiting until {Local(until)} before it works on its own again, because TradeAgent did not close properly soon after its last start. {StillRunning}";

    /// <summary>The activity line when the wait is over and the AI goes back to work.</summary>
    public static string Resumed(TimeSpan clean) =>
        $"TradeAgent has run {Minutes(clean)} without closing unexpectedly, so the AI works on its own again.";

    /// <summary>The activity line when the wait is over and the owner had paused the AI meanwhile.</summary>
    public const string StaysPaused = "The wait after a start that did not close properly is over; the AI stays paused, as you left it.";

    const string StillRunning = "Positions stay managed and the market recording goes on";

    static string Local(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    static string Minutes(TimeSpan span) => span.TotalMinutes >= 1
        ? $"{Math.Round(span.TotalMinutes):0} minute{(Math.Round(span.TotalMinutes) == 1 ? "" : "s")}"
        : $"{Math.Max(1, Math.Round(span.TotalSeconds)):0} seconds";
}

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
/// <para><b>What it does.</b> Where that start died before it had run its own wait and <see cref="Recent"/> past it, and the AI would resume, the resume
/// waits: no AI started and no loop, so no turn and no reservation. Everything else of the app runs as ever — the tape, the paper runs, the background
/// passes, the free-space guard. At the end it decides again from the owner's word as it stands then, so a pause pressed meanwhile stays a pause; the
/// owner's own press ("Let the AI work on its own") ends the wait at once.</para>
///
/// <para><b>The wait doubles with each start in a row that did not close properly</b> (<see cref="WaitFor"/>): ten minutes, twenty, forty, … at most
/// <see cref="Longest"/>. A fault that kills the app as each first turn starts therefore costs a turn after 10, 20, 40, 80 minutes and then one every two
/// hours — at most a dozen a day, under the day's ceiling — where a fixed ten minutes would have let one through every ten (review of
/// <c>U-service-limits</c>). An orderly stop (the owner's quit, the kit's restart) ends the run of starts, and the next crash starts again at ten.</para>
/// </summary>
static class CrashWait
{
    /// <summary>The line every start writes once it is up (<see cref="AppHost.StartAsync"/>).</summary>
    public const string StartedLine = "TradeAgent started";

    /// <summary>The last line of every orderly quit (<see cref="AppHost.DisposeAsync"/>).</summary>
    public const string StoppedLine = "TradeAgent stopped";

    /// <summary>
    /// HOW LONG A START MUST HAVE RUN PAST ITS OWN WAIT for its death to count as a single crash and not a loop: half an hour. A single crash after hours of
    /// running resumes at once.
    /// </summary>
    public static readonly TimeSpan Recent = TimeSpan.FromMinutes(30);

    /// <summary>
    /// HOW LONG THE FIRST WAIT IS: ten minutes of a run that has not died, about as long as the unit's start limit takes to stop a fast loop (seven starts
    /// ten seconds apart and their start-up, inside its ten-minute window), and short beside the chief's four-hour ceiling.
    /// </summary>
    public static readonly TimeSpan Clean = TimeSpan.FromMinutes(10);

    /// <summary>The longest a wait grows to: two hours, half the chief's four-hour ceiling, so a head is never held past its own floor by more than that.</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromHours(2);

    /// <summary>
    /// THE WAIT FOR THE <paramref name="inARow"/>-TH START IN A ROW THAT DID NOT CLOSE PROPERLY: <paramref name="clean"/> doubled for each one before it, at most
    /// <see cref="Longest"/> (never under <paramref name="clean"/>, so a test's short one stays short); zero for none.
    /// </summary>
    public static TimeSpan WaitFor(int inARow, TimeSpan clean)
    {
        if (inARow <= 0) return TimeSpan.Zero;
        var wait = clean;
        for (var i = 1; i < inARow && wait < Longest; i++) wait += wait;
        return wait > Longest && clean < Longest ? Longest : wait;
    }

    /// <summary>
    /// HOW MANY STARTS IN A ROW DID NOT CLOSE PROPERLY: the "TradeAgent started" lines since the last "TradeAgent stopped" (all of them, for a home that
    /// never wrote one).
    /// </summary>
    public static int InARow(LogStore log) => log.CountSinceLast(StartedLine, StoppedLine);

    /// <summary>
    /// WHEN THE LAST START BEGAN, where it did not close properly; null where it did, or there was none (a first start, or a log pruned past both
    /// lines). Read before this start writes its own line. A log that cannot be read THROWS: the caller treats that as a start that did not close.
    /// </summary>
    public static DateTimeOffset? DidNotClose(LogStore log) =>
        log.LastOf(StartedLine, StoppedLine) is { Text: StartedLine } last ? last.At : null;

    /// <summary>
    /// WHETHER THIS START WAITS: the last start did not close properly at <paramref name="didNotClose"/>, the <paramref name="inARow"/>-th in a row, and it
    /// died before it had run its own wait (the one before it in the row) and <see cref="Recent"/> past it. A single crash after a long run resumes at once.
    /// </summary>
    public static bool Recently(DateTimeOffset? didNotClose, int inARow, DateTimeOffset now, TimeSpan clean) =>
        didNotClose is { } at && now - at < WaitFor(inARow - 1, clean) + Recent;

    /// <summary>The activity line at every start that finds the last one did not close properly, whatever the AI is doing.</summary>
    public static string Noticed(DateTimeOffset? at, int inARow = 1) => at is { } was
        ? $"The last time TradeAgent started, at {Local(was)}, it did not close properly{(inARow > 1 ? $" (the {inARow} starts before this one all did not)" : "")}."
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
    public const string StaysPaused = "The wait after a start that did not close properly is over; the AI stays paused.";

    const string StillRunning = "Positions stay managed and the market recording goes on";

    static string Local(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    static string Minutes(TimeSpan span) => span.TotalMinutes >= 1
        ? $"{Math.Round(span.TotalMinutes):0} minute{(Math.Round(span.TotalMinutes) == 1 ? "" : "s")}"
        : $"{Math.Max(1, Math.Round(span.TotalSeconds)):0} seconds";
}

namespace TradeAgent.Core.Strategy;

/// <summary>
/// THE GRID A PROGRAM'S BARS ARE CUT ON: where each bar starts, where it ends, and how many minutes it
/// spans.
///
/// <para><b>Aligned, never relative to the data.</b> A bar of length <c>d</c> covers
/// <c>[k·d, (k+1)·d)</c> counted in UTC — every one of <see cref="StrategyBars.Allowed"/> divides a day,
/// so the 14:00 hour is the 14:00 hour on every day, every machine and every dataset, whatever minute the
/// data happened to start at. A program that declares <c>bars 1d</c> is the one exception: its day starts
/// at MIDNIGHT IN ITS OWN ZONE, because that is the day its <c>weekdays</c>, <c>entry_window</c> and
/// <c>session_exit</c> already read, and a New York program's "day" ending at 19:00 local would be a day
/// nobody in New York keeps.</para>
///
/// <para><b>A zone's day is not always 24 hours.</b> The spring day of a daylight change is 23 hours and
/// the autumn one 25, so a daily bar in New York or London spans 1,380, 1,440 or 1,500 minutes, and
/// <see cref="MinutesIn"/> says which. The instants come from <see cref="StrategyCalendar"/> — the
/// versioned rule table, not an OS lookup — so a day starts at the same instant on every machine.</para>
///
/// <para><b>The one place a wall clock becomes an instant.</b> <see cref="StrategyCalendar"/> only ever
/// turns instants into wall clocks. A daily bar's start is the instant whose wall clock is midnight, and
/// it is found by trying the zone's two offsets and keeping the one that converts BACK to midnight. None of
/// the six zones changes its clocks at midnight, so exactly one does; the fallback below is never reached by
/// a zone this build can name.</para>
/// </summary>
public sealed class BarGrid
{
    readonly ZoneRules? _zone;

    BarGrid(TimeSpan length, ZoneRules? zone)
    {
        Length = length;
        _zone = zone;
    }

    /// <summary>Closed one-minute bars in UTC: what every dataset holds and what a program that declares no <c>bars</c> is evaluated on.</summary>
    public static readonly BarGrid OneMinute = new(StrategyBars.OneMinute, null);

    /// <summary>
    /// THE GRID OF THE BARS A PROGRAM DECLARES — <see cref="StrategyProgram.Bars"/> — cut in UTC, or at
    /// midnight in the program's zone for a day.
    /// </summary>
    public static BarGrid For(StrategyProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);

        if (program.Bars != TimeSpan.FromDays(1)) return Uniform(program.Bars);

        if (!StrategyCalendar.TryZone(program.Time.TimeZone, out var zone))
            throw new ArgumentException(
                $"this build has no calendar for the zone '{program.Time.TimeZone}'", nameof(program));

        // UTC's midnight is the uniform grid's own, so a UTC day is one more uniform bar.
        return zone.StandardMinutes == 0 && zone.Start is null ? Uniform(program.Bars) : new BarGrid(program.Bars, zone);
    }

    /// <summary>
    /// A grid of bars of one length, aligned to UTC. A bar is a whole number of minutes, because it is built
    /// out of closed one-minute bars.
    /// </summary>
    public static BarGrid Uniform(TimeSpan length)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, StrategyBars.OneMinute);
        if (length.Ticks % TimeSpan.TicksPerMinute != 0)
            throw new ArgumentOutOfRangeException(nameof(length), length,
                "a bar is built from closed one-minute bars, so it is a whole number of minutes");

        return length == StrategyBars.OneMinute ? OneMinute : new BarGrid(length, null);
    }

    /// <summary>How long one bar is nominally — <c>1d</c> for a daily bar, whatever a daylight change did to it.</summary>
    public TimeSpan Length { get; }

    /// <summary>The zone whose midnight a daily bar starts at, or null for a grid cut in UTC.</summary>
    public string? Zone => _zone?.Name;

    /// <summary>The bar as a person writes it: <c>1h</c>, <c>1d</c>.</summary>
    public string Spelled => StrategyBars.Spelled(Length);

    /// <summary>Where the bar that <paramref name="instant"/> falls in starts.</summary>
    public DateTimeOffset StartOf(DateTimeOffset instant)
    {
        if (_zone is { } zone) return Midnight(zone, LocalDate(zone, instant));

        var ticks = instant.UtcTicks;
        return new DateTimeOffset(ticks - ticks % Length.Ticks, TimeSpan.Zero);
    }

    /// <summary>Where the bar that opens at <paramref name="start"/> ends — the first instant that is not in it.</summary>
    public DateTimeOffset EndOf(DateTimeOffset start) =>
        _zone is { } zone ? Midnight(zone, LocalDate(zone, start).AddDays(1)) : start + Length;

    /// <summary>Where the bar before the one that opens at <paramref name="start"/> opened.</summary>
    public DateTimeOffset PreviousStart(DateTimeOffset start) =>
        _zone is { } zone ? Midnight(zone, LocalDate(zone, start).AddDays(-1)) : start - Length;

    /// <summary>How many minutes the bar that opens at <paramref name="start"/> spans.</summary>
    public long MinutesIn(DateTimeOffset start) =>
        (EndOf(start) - (_zone is null ? start : StartOf(start))).Ticks / TimeSpan.TicksPerMinute;

    /// <summary>The whole minutes from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static long MinutesBetween(DateTimeOffset from, DateTimeOffset to) =>
        (to - from).Ticks / TimeSpan.TicksPerMinute;

    /// <summary>
    /// WHY A BAR OPENING AT <paramref name="next"/> CANNOT FOLLOW ONE AT <paramref name="previous"/> ON
    /// THIS GRID, or null when it can. The caller has already refused a bar that is not after the one
    /// before it.
    ///
    /// <para>One-minute bars are asked only that two of them are a whole number of minutes apart — the rule
    /// the evaluator has always kept, relative to the run's own first bar. A declared bar longer than that
    /// is asked, as well, to open on its grid, the first one included: it is built by
    /// <see cref="BarResampler"/>, which never answers one anywhere else, so a bar off the grid is a caller
    /// feeding the wrong series — minutes to an hourly run — and is refused at the first bar rather than
    /// read as an hour. A zone's daily grid asks the same of every bar, because its bars are not all one
    /// length and "a whole number of days apart" would admit a series cut at the wrong hour.</para>
    /// </summary>
    public string? Refusal(DateTimeOffset? previous, DateTimeOffset next)
    {
        if (_zone is { } zone)
            return StartOf(next) == next
                ? null
                : $"the bar at {next:O} does not open at midnight in {zone.Name}, which is where this program's " +
                  "daily bars begin: this run's bars are not one series";

        if (Length != StrategyBars.OneMinute && StartOf(next) != next)
            return $"the bar at {next:O} does not open on the {Spelled} grid — a {Spelled} bar opens at a whole " +
                   $"multiple of {Spelled} counted in UTC — so it is not one of this run's bars: this run's bars " +
                   "are not one series";

        if (previous is not { } last) return null;

        var elapsed = next - last;
        return elapsed.Ticks % Length.Ticks == 0
            ? null
            : $"the bar at {next:O} is {elapsed} after the one before it, which is not a whole number of " +
              $"{Length} bars: this run's bars are not one series";
    }

    static DateTime LocalDate(ZoneRules zone, DateTimeOffset instant) => StrategyCalendar.Local(zone, instant).Date;

    /// <summary>The instant whose wall clock in <paramref name="zone"/> is midnight on <paramref name="date"/>.</summary>
    static DateTimeOffset Midnight(ZoneRules zone, DateTime date)
    {
        var wall = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);

        foreach (var offset in (ReadOnlySpan<int>)[zone.StandardMinutes, zone.DaylightMinutes])
        {
            var candidate = new DateTimeOffset(wall.AddMinutes(-offset), TimeSpan.Zero);
            if (StrategyCalendar.Local(zone, candidate) == wall) return candidate;
        }

        // NOT REACHED BY THE SIX ZONES — see the type's summary. Standard time is the reading that names a
        // real instant whatever a future rule table holds, and the evaluator would refuse a bar cut there.
        return new DateTimeOffset(wall.AddMinutes(-zone.StandardMinutes), TimeSpan.Zero);
    }
}

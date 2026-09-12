using TradeAgent.Core.Data;
using TradeAgent.Core.Strategy;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEM 4 — THE VERSIONED CALENDAR: DAYLIGHT SAVING, AND A SESSION THAT NEVER OPENS.
///
/// <para>`docs/COUNCIL.md` requires "Time filters with a versioned timezone and calendar, weekdays,
/// entry windows, opening-range interval, scheduled session exit, DST and missing sessions defined".
/// `U-runner-1` made the zone NAMES data in `StrategyLimits` because `TimeZoneInfo` answers differently
/// on Windows and Unix under `InvariantGlobalization` and a promoted program's meaning must not depend
/// on the machine that ran it; `StrategyCalendar` is the other half — the offsets and the two
/// transition rules, as data, hashed into every id through
/// `StrategyVersions.CalendarVersion`.</para>
///
/// <para><b>The rule table was checked against the real thing.</b> Outside this build, against
/// Python's IANA database: every half-hour of 2023 to 2027 in all six zones — 525,888 readings — agreed
/// with this table exactly. It is a rule table and not a database, so dates before the United States'
/// 2007 change or the European Union's 1996 harmonisation would be wrong, and there is no holiday
/// list; both are the calendar VERSION's business, and moving it re-identifies every program.</para>
/// </summary>
public class EvaluatorCalendarTests
{
    static StrategyProgram Program(string text)
    {
        var parse = StrategyParser.Parse(text);
        Assert.True(parse.Ok, parse.Why);
        return parse.Program!;
    }

    static EvaluationRun RunOn(StrategyProgram program, IReadOnlyList<KlineBar> bars) =>
        StrategyEvaluator.Run(program, bars, (_, _) => AccountReading.Flat(10_000m));

    /// <summary>A program that wants in on every bar, inside one window, in one zone.</summary>
    static StrategyProgram Window(string zone, string window) => Program($"""
        instrument BTCUSDT
        timezone {zone}
        size fixed 1
        entry_window {window}
        max_hold_bars 60
        exit when close < 1
        entry when close > 0
        """);

    /// <summary>
    /// The parser's allowlist and the calendar's table are THE SAME SIX ZONES. A zone a program may
    /// name and the evaluator has no rules for would read every wall clock as UTC and honour the wrong
    /// window — quietly, because the program would still parse and still run.
    /// </summary>
    [Fact]
    public void Every_zone_a_program_may_name_has_calendar_rules()
    {
        Assert.Equal(StrategyLimits.TimeZones.Length, StrategyCalendar.Zones.Length);
        Assert.Equal([.. StrategyLimits.TimeZones.Order()], [.. StrategyCalendar.Zones.Select(z => z.Name).Order()]);

        foreach (var name in StrategyLimits.TimeZones)
            Assert.True(StrategyCalendar.TryZone(name, out _), name);

        Assert.False(StrategyCalendar.TryZone("Mars/Olympus", out _));
    }

    /// <summary>
    /// DAYLIGHT SAVING MOVES WHICH UTC MINUTE IS INSIDE A WINDOW. The same 13:35 UTC is 09:35 in New
    /// York in July and 08:35 in January, so a `09:30-10:00` window admits it in one season and refuses
    /// it in the other — and 14:35 UTC is the other way round. A build that read the zone as a fixed
    /// offset would be an hour wrong for eight months of the year.
    /// </summary>
    [Fact]
    public void The_same_utc_minute_is_inside_a_new_york_window_in_summer_and_outside_it_in_winter()
    {
        var program = Window("America/New_York", "09:30-10:00");

        var summer = new DateTimeOffset(2026, 7, 6, 13, 35, 0, TimeSpan.Zero);    // 09:35 EDT
        var winter = new DateTimeOffset(2026, 1, 5, 13, 35, 0, TimeSpan.Zero);    // 08:35 EST

        Assert.Single(RunOn(program, SyntheticBars.Minutes(summer, 1)).Intents);
        Assert.Empty(RunOn(program, SyntheticBars.Minutes(winter, 1)).Intents);

        Assert.Empty(RunOn(program, SyntheticBars.Minutes(summer.AddHours(1), 1)).Intents);   // 10:35 EDT
        Assert.Single(RunOn(program, SyntheticBars.Minutes(winter.AddHours(1), 1)).Intents);  // 09:35 EST
    }

    /// <summary>
    /// THE HOUR THAT DOES NOT EXIST. New York's clocks go from 01:59 to 03:00 on 8 March 2026, so a
    /// program whose entry window is 02:00-03:00 takes nothing that day — not because a bar is missing,
    /// but because no instant reads as that wall clock. Converting UTC to local is what makes this an
    /// answer rather than an ambiguity.
    /// </summary>
    [Fact]
    public void The_spring_hour_that_does_not_exist_admits_no_bar()
    {
        var program = Window("America/New_York", "02:00-03:00");
        var midnightUtc = new DateTimeOffset(2026, 3, 8, 0, 0, 0, TimeSpan.Zero);

        // Every minute of the transition day, in UTC.
        var run = RunOn(program, SyntheticBars.Minutes(midnightUtc, 24 * 60));

        Assert.Empty(run.Intents);
        Assert.Equal(24 * 60, run.Counters.Bars);
        Assert.Equal(24 * 60, run.Counters.EntriesOutsideTimeFilters);

        if (!StrategyCalendar.TryZone("America/New_York", out var zone)) Assert.Fail("no zone");
        Assert.Equal(new DateTime(2026, 3, 8, 1, 59, 0),
            StrategyCalendar.Local(zone, new DateTimeOffset(2026, 3, 8, 6, 59, 0, TimeSpan.Zero)));
        Assert.Equal(new DateTime(2026, 3, 8, 3, 0, 0),
            StrategyCalendar.Local(zone, new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero)));
    }

    /// <summary>
    /// THE HOUR THAT HAPPENS TWICE. New York reads 01:00-01:59 twice on 1 November 2026, so a
    /// 01:00-02:00 window admits TWO hours of UTC minutes that day — 120 of them, not 60.
    /// </summary>
    [Fact]
    public void The_autumn_hour_that_happens_twice_admits_both_of_them()
    {
        if (!StrategyCalendar.TryZone("America/New_York", out var zone)) Assert.Fail("no zone");

        var day = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var inside = Enumerable.Range(0, 24 * 60)
            .Select(i => day.AddMinutes(i))
            .Count(at => StrategyCalendar.Contains(
                new TimeWindow(new TimeOfDay(1, 0), new TimeOfDay(2, 0)), StrategyCalendar.Local(zone, at)));

        Assert.Equal(120, inside);
        Assert.Equal(new DateTime(2026, 11, 1, 1, 59, 0),
            StrategyCalendar.Local(zone, new DateTimeOffset(2026, 11, 1, 5, 59, 0, TimeSpan.Zero)));   // EDT
        Assert.Equal(new DateTime(2026, 11, 1, 1, 0, 0),
            StrategyCalendar.Local(zone, new DateTimeOffset(2026, 11, 1, 6, 0, 0, TimeSpan.Zero)));    // EST
    }

    /// <summary>
    /// The European zones switch at a FIXED UTC HOUR — 01:00 UTC on the last Sunday of March and of
    /// October — which is why London and Berlin change at the same instant and New York does not.
    /// </summary>
    [Theory]
    [InlineData("Europe/London", 0, 60)]
    [InlineData("Europe/Berlin", 60, 120)]
    public void The_european_zones_switch_at_one_oclock_utc(string name, int standard, int daylight)
    {
        Assert.True(StrategyCalendar.TryZone(name, out var zone));

        Assert.Equal(standard, StrategyCalendar.OffsetMinutes(
            zone, new DateTimeOffset(2026, 3, 29, 0, 59, 0, TimeSpan.Zero)));
        Assert.Equal(daylight, StrategyCalendar.OffsetMinutes(
            zone, new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero)));
        Assert.Equal(daylight, StrategyCalendar.OffsetMinutes(
            zone, new DateTimeOffset(2026, 10, 25, 0, 59, 0, TimeSpan.Zero)));
        Assert.Equal(standard, StrategyCalendar.OffsetMinutes(
            zone, new DateTimeOffset(2026, 10, 25, 1, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Utc_and_tokyo_never_move()
    {
        Assert.True(StrategyCalendar.TryZone("UTC", out var utc));
        Assert.True(StrategyCalendar.TryZone("Asia/Tokyo", out var tokyo));

        foreach (var month in Enumerable.Range(1, 12))
        {
            var at = new DateTimeOffset(2026, month, 15, 12, 0, 0, TimeSpan.Zero);
            Assert.Equal(0, StrategyCalendar.OffsetMinutes(utc, at));
            Assert.Equal(540, StrategyCalendar.OffsetMinutes(tokyo, at));
        }
    }

    /// <summary>
    /// A MISSING SESSION. The opening-range accumulator's window is a wall-clock interval, so a session
    /// whose interval has no bars at all — a venue outage, a holiday — leaves it UNDEFINED, and every
    /// event that reads it is not evaluated rather than answered from yesterday's range. That is the
    /// difference between a program that took no trade and a program that took a trade against a level
    /// from the day before.
    /// </summary>
    [Fact]
    public void A_session_whose_opening_range_has_no_bars_leaves_the_program_unable_to_act()
    {
        var program = Program("""
            instrument BTCUSDT
            timezone UTC
            size fixed 1
            opening_range 09:30-10:00
            entry_window 10:00-15:30
            indicator rangehigh = opening_range_high()
            max_hold_bars 60
            exit when close < 1
            entry when close > rangehigh
            """);

        // Day one: the opening range has bars, then the entry window. Day two: the venue is down
        // until 10:30, so the opening range never happens, and the entry window has bars.
        var dayOne = new DateTimeOffset(2026, 1, 5, 9, 30, 0, TimeSpan.Zero);
        var openingBars = SyntheticBars.Minutes(dayOne, 30, _ => 100m);
        var tradingBars = SyntheticBars.Minutes(dayOne.AddMinutes(30), 30, _ => 150m);

        var dayTwo = new DateTimeOffset(2026, 1, 6, 10, 30, 0, TimeSpan.Zero);
        var afterTheOutage = SyntheticBars.Minutes(dayTwo, 30, _ => 150m);

        var run = StrategyEvaluator.Run(
            program,
            [.. openingBars, .. tradingBars, .. afterTheOutage],
            (_, _) => AccountReading.Flat(10_000m));

        // Day one signalled: the range was measured at 100.50 and the price was above it.
        Assert.NotEmpty(run.Intents);
        Assert.All(run.Intents, i => Assert.Equal(new DateOnly(2026, 1, 5), DateOnly.FromDateTime(i.Bar.UtcDateTime)));

        // Day two: thirty events, none evaluated to anything, because the range is undefined again.
        Assert.Equal(30, run.Counters.UndefinedEvents);
        Assert.Equal(0, run.Counters.EntriesOutsideTimeFilters);
        Assert.False(run.Faulted);
        Assert.Null(run.State.IndicatorValue("rangehigh"));
    }

    /// <summary>
    /// And the range is measured PER SESSION: two days of bars, two different ranges, and the second
    /// day's level is its own.
    /// </summary>
    [Fact]
    public void Each_session_measures_its_own_opening_range()
    {
        var program = Program("""
            instrument BTCUSDT
            timezone UTC
            size fixed 1
            opening_range 09:30-10:00
            indicator rangehigh = opening_range_high()
            max_hold_bars 60
            exit when close < 1
            entry when close > rangehigh
            """);

        var state = EvaluationState.Start(program);
        var dayOne = new DateTimeOffset(2026, 1, 5, 9, 30, 0, TimeSpan.Zero);

        foreach (var bar in SyntheticBars.Minutes(dayOne, 30, _ => 100m))
            StrategyEvaluator.Step(state, bar, AccountReading.Flat(10_000m));

        Assert.Equal(100.50m, state.IndicatorValue("rangehigh"));

        // The first bar of the next session resets it, and the new session's range is its own.
        foreach (var bar in SyntheticBars.Minutes(dayOne.AddDays(1).AddMinutes(-30), 1, _ => 100m))
            StrategyEvaluator.Step(state, bar, AccountReading.Flat(10_000m));

        Assert.Null(state.IndicatorValue("rangehigh"));

        foreach (var bar in SyntheticBars.Minutes(dayOne.AddDays(1), 30, _ => 200m))
            StrategyEvaluator.Step(state, bar, AccountReading.Flat(10_000m));

        Assert.Equal(200.50m, state.IndicatorValue("rangehigh"));
    }
}

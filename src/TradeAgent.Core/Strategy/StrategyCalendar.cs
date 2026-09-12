namespace TradeAgent.Core.Strategy;

/// <summary>What a daylight transition's hour is measured against.</summary>
public enum TransitionBasis
{
    /// <summary>A fixed UTC hour, as the European Union's rule is written.</summary>
    Utc,

    /// <summary>A local hour read on standard time, as the United States' spring rule is written.</summary>
    LocalStandard,

    /// <summary>A local hour read on daylight time, as the United States' autumn rule is written.</summary>
    LocalDaylight
}

/// <summary>
/// One end of a daylight rule: the nth Sunday of a month at an hour, and what that hour is measured
/// against. <paramref name="SundayOfMonth"/> 5 means the LAST Sunday.
/// </summary>
public sealed record DaylightTransition(int Month, int SundayOfMonth, int Hour, TransitionBasis Basis);

/// <summary>
/// ONE ZONE, AS DATA. Offsets in minutes east of UTC; <paramref name="Start"/> and
/// <paramref name="End"/> null for a zone that does not observe daylight time.
/// </summary>
public sealed record ZoneRules(
    string Name,
    int StandardMinutes,
    int DaylightMinutes,
    DaylightTransition? Start,
    DaylightTransition? End);

/// <summary>
/// THE CALENDAR A PROGRAM'S TIME FILTERS ARE READ AGAINST — six zones, as data, versioned.
///
/// <para><b>Why this is not <c>TimeZoneInfo</c>.</b> This build sets <c>InvariantGlobalization</c>,
/// and <c>FindSystemTimeZoneById</c> then answers differently on Windows (registry ids, an
/// IANA mapping that needs ICU) from Unix (a tzfile): the same program would parse on one machine and
/// be refused on the other, and a promoted strategy's meaning would depend on the machine that ran
/// it. `U-runner-1` made the ZONE NAMES data in <see cref="StrategyLimits.TimeZones"/> for that
/// reason; this is the other half — the offsets and the transitions, so an instant becomes the same
/// wall clock everywhere.</para>
///
/// <para><b>What it is honestly not.</b> It is a RULE TABLE, not a time-zone database: the current
/// rule set is applied to every year, so dates before the United States' 2007 change or the European
/// Union's 1996 harmonisation are wrong, and there is no holiday table. Both are acceptable for a
/// product whose data starts in the 2020s and neither is hidden: the table is
/// `StrategyVersions.CalendarVersion`, that version is hashed into every `StrategyId`, and widening
/// it re-identifies every program rather than quietly changing what one means. Every zone here is
/// northern, so daylight time is an interval INSIDE one year; a southern zone would need the
/// wrap-around case and a version bump.</para>
///
/// <para><b>UTC to local, never the other way.</b> Every instant has exactly one wall clock, so the
/// hour that does not exist in spring and the hour that happens twice in autumn are not ambiguities
/// here — they are simply what the conversion answers. A program names a wall-clock window and the
/// evaluator asks which window an instant fell in; nothing ever has to turn a local time into an
/// instant.</para>
/// </summary>
public static class StrategyCalendar
{
    /// <summary>The United States' rule since 2007: second Sunday in March, first Sunday in November.</summary>
    static readonly DaylightTransition UsStart = new(3, 2, 2, TransitionBasis.LocalStandard);

    static readonly DaylightTransition UsEnd = new(11, 1, 2, TransitionBasis.LocalDaylight);

    /// <summary>The European Union's rule since 1996: last Sunday in March and in October, 01:00 UTC.</summary>
    static readonly DaylightTransition EuStart = new(3, 5, 1, TransitionBasis.Utc);

    static readonly DaylightTransition EuEnd = new(10, 5, 1, TransitionBasis.Utc);

    /// <summary>
    /// The zones a program may name — the same six as <see cref="StrategyLimits.TimeZones"/>, and a
    /// test holds the two lists to each other.
    /// </summary>
    public static readonly ZoneRules[] Zones =
    [
        new("UTC", 0, 0, null, null),
        new("America/New_York", -300, -240, UsStart, UsEnd),
        new("America/Chicago", -360, -300, UsStart, UsEnd),
        new("Europe/London", 0, 60, EuStart, EuEnd),
        new("Europe/Berlin", 60, 120, EuStart, EuEnd),
        new("Asia/Tokyo", 540, 540, null, null)
    ];

    /// <summary>The rules for a zone name, or false when this build has no calendar for it.</summary>
    public static bool TryZone(string name, out ZoneRules rules)
    {
        foreach (var zone in Zones)
            if (string.Equals(zone.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                rules = zone;
                return true;
            }

        rules = Zones[0];
        return false;
    }

    /// <summary>The zone's offset from UTC, in minutes east, at one instant.</summary>
    public static int OffsetMinutes(ZoneRules zone, DateTimeOffset utc)
    {
        if (zone.Start is null || zone.End is null) return zone.StandardMinutes;

        var year = utc.UtcDateTime.Year;
        var start = TransitionUtc(zone, zone.Start, year);
        var end = TransitionUtc(zone, zone.End, year);

        return utc.UtcDateTime >= start && utc.UtcDateTime < end ? zone.DaylightMinutes : zone.StandardMinutes;
    }

    /// <summary>The wall clock an instant reads as in this zone. Every instant has exactly one.</summary>
    public static DateTime Local(ZoneRules zone, DateTimeOffset utc) =>
        utc.UtcDateTime.AddMinutes(OffsetMinutes(zone, utc));

    /// <summary>The weekday flag of a wall clock, for a program's `weekdays` line.</summary>
    public static Weekdays DayOf(DateTime local) => local.DayOfWeek switch
    {
        DayOfWeek.Monday => Weekdays.Mon,
        DayOfWeek.Tuesday => Weekdays.Tue,
        DayOfWeek.Wednesday => Weekdays.Wed,
        DayOfWeek.Thursday => Weekdays.Thu,
        DayOfWeek.Friday => Weekdays.Fri,
        DayOfWeek.Saturday => Weekdays.Sat,
        _ => Weekdays.Sun
    };

    /// <summary>
    /// Whether a wall clock is inside a declared window — `from` INCLUDED, `to` EXCLUDED. The parser
    /// refuses a window that would wrap past midnight, so this is one comparison of minutes.
    /// </summary>
    public static bool Contains(TimeWindow window, DateTime local)
    {
        var minute = local.Hour * 60 + local.Minute;
        return minute >= window.From.MinuteOfDay && minute < window.To.MinuteOfDay;
    }

    /// <summary>
    /// THE SESSION A WALL CLOCK BELONGS TO: its calendar DATE in the program's zone.
    ///
    /// <para>The opening-range accumulator resets when this changes, so "each session" is one local
    /// day. A day with no bars at all — a weekend, a holiday, a venue outage — is simply a session
    /// that never starts: its opening range is never defined, and a program that reads one takes no
    /// trade that day rather than reading yesterday's.</para>
    /// </summary>
    public static DateOnly SessionOf(DateTime local) => DateOnly.FromDateTime(local);

    /// <summary>The UTC instant one end of a daylight rule falls at, in one year.</summary>
    static DateTime TransitionUtc(ZoneRules zone, DaylightTransition transition, int year)
    {
        var day = SundayOfMonth(year, transition.Month, transition.SundayOfMonth);
        var wall = new DateTime(year, transition.Month, day, transition.Hour, 0, 0, DateTimeKind.Utc);

        return transition.Basis switch
        {
            TransitionBasis.Utc => wall,
            TransitionBasis.LocalStandard => wall.AddMinutes(-zone.StandardMinutes),
            _ => wall.AddMinutes(-zone.DaylightMinutes)
        };
    }

    /// <summary>The nth Sunday of a month, where 5 means the last one.</summary>
    static int SundayOfMonth(int year, int month, int nth)
    {
        var first = new DateTime(year, month, 1);
        var offset = ((int)DayOfWeek.Sunday - (int)first.DayOfWeek + 7) % 7;

        if (nth < 5) return 1 + offset + (nth - 1) * 7;

        var day = 1 + offset;
        var days = DateTime.DaysInMonth(year, month);
        while (day + 7 <= days) day += 7;
        return day;
    }
}

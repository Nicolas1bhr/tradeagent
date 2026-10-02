namespace TradeAgent.Provisioning;

/// <summary>
/// WHEN A COLLECTOR THAT LOOKS ON A FIXED TICK LOOKS NEXT: at the first tick boundary plus an offset
/// that is strictly after now. One helper, for every collector in this assembly that has a tick
/// (<c>ForwardBarCollector</c> today) — so "a healthy look lands just after the thing it is asking
/// for has closed" is arithmetic written once and checked once, not a loop each collector gets
/// slightly different.
///
/// <para><b>Why aligned and not "one tick after the last look".</b> A wait of one tick after each look
/// keeps whatever phase the process happened to start at and adds every request's own duration to
/// it, so a collector started at 12:00:37 asks for the minute that closed at 12:01:00 at 12:01:37 —
/// and a price 37 seconds old is past the 30 seconds an order may be sized from. Aligned, it asks at
/// 12:01:02 whatever time it started and however long the last request took.</para>
///
/// <para>Boundaries are whole multiples of the tick counted from <see cref="DateTimeOffset.MinValue"/>
/// in UTC, so a one-minute tick lands on the wall clock's minutes. An offset longer than the tick
/// keeps only its remainder: the phase, not a second wait.</para>
///
/// <para>This is the SUCCESS path's schedule and nothing else. A collector whose last look failed
/// backs off on its own arithmetic, longer each time, because a host having a bad afternoon is to be
/// asked less often rather than at a sharper phase.</para>
/// </summary>
public static class TickAlignment
{
    /// <summary>The first instant strictly after <paramref name="now"/> that is a tick boundary plus the offset.</summary>
    public static DateTimeOffset NextLook(DateTimeOffset now, TimeSpan tick, TimeSpan offset)
    {
        if (tick <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(tick), tick, "a tick is a positive length of time");

        var length = tick.Ticks;
        var phase = (offset.Ticks % length + length) % length;
        var since = now.UtcTicks - phase;

        // FLOOR, not truncation, so an instant before the first boundary of all still answers the
        // boundary after it. Strictly after now: a look that lands exactly on its instant has had it.
        var floor = since >= 0 ? since / length : -((-since + length - 1) / length);
        return new DateTimeOffset((floor + 1) * length + phase, TimeSpan.Zero);
    }

    /// <summary>How long from <paramref name="now"/> to <see cref="NextLook"/>: more than zero, at most one tick.</summary>
    public static TimeSpan WaitForNextLook(DateTimeOffset now, TimeSpan tick, TimeSpan offset) =>
        NextLook(now, tick, offset) - now;
}

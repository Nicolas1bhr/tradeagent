namespace TradeAgent.Core;

/// <summary>
/// THE OWNER'S LOCAL DAY, SPELLED ONCE — the window every daily figure is summed over and the midnight a daily ceiling
/// resets at. Moved here from <c>TurnMeter</c> by <c>U-decision-port</c>, because the decision port admits its calls
/// against the same day the meter admits turns against, and two copies of "today" are two days the first time a
/// daylight-saving change falls between them. <c>DailyReports.LocalDay</c> answers through this too.
///
/// <para>Local because the reset belongs at the owner's midnight: an owner in Ljubljana whose day rolled over at 01:00
/// would be reading a today that is not theirs. Each boundary takes the offset in force AT that boundary, so the day
/// either side of a daylight-saving change is twenty-three or twenty-five hours long rather than silently wrong.</para>
/// </summary>
public static class OwnerDay
{
    /// <summary>The local day <paramref name="now"/> falls in, as the half-open instant window a row's start is tested against.</summary>
    public static (DateTimeOffset From, DateTimeOffset To) Window(DateTimeOffset now)
    {
        var start = now.ToLocalTime().Date;
        var end = start.AddDays(1);
        return (new DateTimeOffset(start, TimeZoneInfo.Local.GetUtcOffset(start)),
                new DateTimeOffset(end, TimeZoneInfo.Local.GetUtcOffset(end)));
    }

    /// <summary>The next LOCAL midnight — when today's totals stop being today's.</summary>
    public static DateTimeOffset Midnight(DateTimeOffset now) => Window(now).To;
}

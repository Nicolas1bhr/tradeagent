namespace TradeAgent.Core.Db;

/// <summary>
/// THE SOONEST THE ORGANISATION'S SWEEP WAKES A HOLDER AGAIN AFTER A LAUNCH OF ITS MANDATE ENDED WITHOUT THE WORK
/// (<c>U-rewake-floor</c>; <c>docs/ORCHESTRATION.md</c> § 2.5: timescale ceilings are "policy data — a maximum interval per level,
/// a floor and never a heartbeat"; § 0.4, turns are capital). Per level (<see cref="OrgLevel"/>): the first re-wake after an
/// undelivered launch is due its level's floor after that launch ended, and each further one twice the last, never more than
/// <see cref="Cap"/> times the floor and never past the level's ceiling. A floor of zero is the re-wake due at once — the sweep
/// as it was before this unit.
///
/// <para><b>What it never delays.</b> It shapes ONLY the sweep's re-raise of an owed mandate after a launch of it: the issued
/// wake, code's retry, the decider's rejection, the holder's own requested wake and the owner's words keep their time.</para>
///
/// <para><b>The owner's, in-process</b> (<see cref="TradeAgentSettings.RewakeFloorMinutes"/>): no verb, no pipe op and no agent
/// reaches it.</para>
/// </summary>
public sealed record RewakeFloors(IReadOnlyDictionary<string, TimeSpan> Floors, IReadOnlyDictionary<string, TimeSpan> Ceilings)
{
    /// <summary>The most a re-wake interval grows to, as a multiple of its level's floor — <c>U-quiet-review</c>'s eight.</summary>
    public const int Cap = 8;

    /// <summary>
    /// THE FLOORS AS SHIPPED, in minutes: an executor's hours compressed to a quarter of one, a team head's day to an hour, a
    /// department head's weeks to four hours (VISION § 7.5); doubled to the cap they are 2 h, 8 h and 32 h.
    /// </summary>
    public static IReadOnlyDictionary<string, int> DefaultMinutes { get; } = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [OrgLevel.Executor] = 15,
        [OrgLevel.Team] = 60,
        [OrgLevel.Department] = 240
    };

    /// <summary>Every floor zero: the sweep's re-raise due the moment it is written.</summary>
    public static RewakeFloors None { get; } = new(
        OrgLevel.All.ToDictionary(l => l, _ => TimeSpan.Zero, StringComparer.Ordinal),
        OrgLevel.All.ToDictionary(l => l, _ => TimeSpan.Zero, StringComparer.Ordinal));

    /// <summary>
    /// THE FLOORS THE OWNER SET: a level's minutes as given, below zero read as zero, a level missing read as its default
    /// (<see cref="DefaultMinutes"/>); each level's ceiling as the loop's (zero or missing is none).
    /// </summary>
    public static RewakeFloors From(IReadOnlyDictionary<string, int>? minutes, IReadOnlyDictionary<string, TimeSpan>? ceilings) => new(
        OrgLevel.All.ToDictionary(l => l,
            l => TimeSpan.FromMinutes(minutes is not null && minutes.TryGetValue(l, out var m) ? Math.Max(0, m) : DefaultMinutes[l]),
            StringComparer.Ordinal),
        OrgLevel.All.ToDictionary(l => l,
            l => ceilings is not null && ceilings.TryGetValue(l, out var c) && c > TimeSpan.Zero ? c : TimeSpan.Zero,
            StringComparer.Ordinal));

    /// <summary>
    /// THE LEVEL WHOSE FLOOR IS THE LARGEST — what a holder the chart cannot place is held to: refused a guess, it takes the
    /// saving direction.
    /// </summary>
    public string Largest => OrgLevel.All.OrderByDescending(Floor).First();

    /// <summary>The floor of <paramref name="level"/>.</summary>
    public TimeSpan Floor(string level) => Floors.TryGetValue(level, out var f) && f > TimeSpan.Zero ? f : TimeSpan.Zero;

    /// <summary>
    /// HOW LONG AFTER THE <paramref name="k"/>-TH UNDELIVERED LAUNCH ENDED THE NEXT RE-WAKE IS DUE: the floor, doubled for each
    /// launch after the first, never more than <see cref="Cap"/> floors and never past the level's ceiling. Zero for a zero floor
    /// and for no launch.
    /// </summary>
    public TimeSpan After(string level, int k)
    {
        var floor = Floor(level);
        if (floor <= TimeSpan.Zero || k <= 0) return TimeSpan.Zero;
        var cap = floor * Cap;
        if (Ceilings.TryGetValue(level, out var ceiling) && ceiling > TimeSpan.Zero && ceiling < cap) cap = ceiling;
        var step = floor;
        for (var i = 1; i < k && step < cap; i++) step *= 2;
        return step < cap ? step : cap;
    }
}

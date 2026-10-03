namespace TradeAgent.Core.Strategy;

/// <summary>
/// THE BARS A PROGRAM MAY DECLARE IT IS EVALUATED ON — <c>bars 1h</c> — AND HOW EACH ONE IS SPELLED.
///
/// <para><b>Why a program declares one at all.</b> Every program used to step on every closed minute,
/// so its lookback of at most 500 bars was about eight hours of context and its turnover was set by the
/// minute rather than by anything it was trying to catch. A model-authored version on 2026-10-01 made
/// 321 trades in a quarter and paid 62% of its loss in fees. A program that decides once an hour sees
/// three weeks in the same 500 bars and trades as often as its edge does, which is what a cost-paying
/// strategy needs (<c>docs/EDGE-FACTORY.md</c> § 4.4).</para>
///
/// <para><b>A closed list of seven.</b> Every one is cut on a fixed grid — UTC for the first six, the
/// program's own zone's midnight for a day — so two programs on hourly bars cut them at the same
/// instants on every machine, and a bar never depends on which minute a dataset happened to start at.
/// A list is also a refusal a model can read: <c>bars 2h</c> is refused with these seven named.</para>
///
/// <para><b>One minute is what a program that declares nothing is evaluated on</b>, which is what every
/// program written before this list existed was evaluated on. That is why <c>bars 1m</c> and no
/// <c>bars</c> line are one program with one id (<see cref="StrategyCanonical"/>).</para>
/// </summary>
public static class StrategyBars
{
    /// <summary>What every dataset holds, and what a program that declares no <c>bars</c> is evaluated on.</summary>
    public static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    /// <summary>The seven bars a program may declare, shortest first.</summary>
    public static readonly IReadOnlyList<TimeSpan> Allowed =
    [
        OneMinute,
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(4),
        TimeSpan.FromDays(1)
    ];

    /// <summary>The seven, as a refusal and the language document name them.</summary>
    public const string List = "1m, 5m, 15m, 30m, 1h, 4h, 1d";

    /// <summary>Whether a program may declare this bar.</summary>
    public static bool IsAllowed(TimeSpan bars) => Allowed.Contains(bars);

    /// <summary>
    /// A bar as a person writes it — <c>1h</c>, <c>1d</c> — for refusals, run texts and the runner's
    /// words. Never for a hash: the canonical form writes every duration in seconds.
    /// </summary>
    public static string Spelled(TimeSpan bars)
    {
        if (bars.Ticks % TimeSpan.TicksPerDay == 0) return Count(bars.Ticks / TimeSpan.TicksPerDay, 'd');
        if (bars.Ticks % TimeSpan.TicksPerHour == 0) return Count(bars.Ticks / TimeSpan.TicksPerHour, 'h');
        if (bars.Ticks % TimeSpan.TicksPerMinute == 0) return Count(bars.Ticks / TimeSpan.TicksPerMinute, 'm');
        return Count(bars.Ticks / TimeSpan.TicksPerSecond, 's');

        static string Count(long n, char unit) =>
            n.ToString(System.Globalization.CultureInfo.InvariantCulture) + unit;
    }
}

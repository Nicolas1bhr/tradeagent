using TradeAgent.Core.Data;

namespace TradeAgent.Core.Strategy;

/// <summary>
/// CLOSED ONE-MINUTE BARS IN, CLOSED BARS OF A PROGRAM'S DECLARED LENGTH OUT — the one resampler the
/// backtest uses and the paper runner will (<c>U-timeframe-b</c>).
///
/// <para><b>What a bar is.</b> A bar covers one window of its <see cref="BarGrid"/>: the OPEN is its first
/// minute's open, the HIGH and LOW the extremes of its minutes, the CLOSE its last minute's close and the
/// VOLUME their sum — decimals, exactly as the minutes carried them, with nothing rounded and no clock read.
/// <see cref="KlineBar.Minutes"/> says how many minutes it was built from, so an hour the data has 45
/// minutes of is a bar that says it is 45 minutes, and a window with no minute at all is NO BAR: a gap,
/// never filled, exactly as a missing minute has always been. A bar any of whose minutes was
/// midpoint-derived is midpoint-derived, because its volume is then not a measurement of trades.</para>
///
/// <para><b>When it closes.</b> A bar closes when its last minute has closed: when that minute arrives
/// (<see cref="Add"/> answers it), or when a minute at or after its end arrives instead — the last minutes
/// are missing, and time has passed them (<see cref="CloseBefore"/>). Nothing is ever answered before
/// then, so a rule can never read a bar that is still forming. A run over history that ENDS inside a
/// bar closes it with <see cref="Flush"/>: those minutes happened and the data does not have them, which
/// is what a missing minute is.</para>
///
/// <para><b>Incremental, so one minute at a time is the whole interface.</b> The paper runner replays its
/// minutes from the deployment's start on every pass; fed the same minutes, this answers the same bars in
/// the same order, which is what makes a restart mid-hour resume the same state.</para>
/// </summary>
public sealed class BarResampler(BarGrid grid)
{
    DateTimeOffset? _start;
    DateTimeOffset _end;
    decimal _open, _high, _low, _close, _volume;
    int _minutes;
    bool _midpoint;
    DateTimeOffset? _lastMinute;

    /// <summary>The grid the bars are cut on.</summary>
    public BarGrid Grid { get; } = grid ?? throw new ArgumentNullException(nameof(grid));

    /// <summary>Whether a bar has minutes in it and has not closed.</summary>
    public bool Forming => _start is not null;

    /// <summary>
    /// WHY THIS MINUTE CANNOT BE THE NEXT ONE, in words — or null when it can.
    ///
    /// <para>The evaluator refuses a bar out of order, twice, or off its grid, and it cannot see the minutes
    /// a declared bar was built from; so the same refusals are made here, one level down. A minute fed twice
    /// would be counted into an hour's volume twice and a minute fed backwards would change its close, and
    /// both would look exactly like a real bar.</para>
    /// </summary>
    public string? Refusal(KlineBar minute)
    {
        ArgumentNullException.ThrowIfNull(minute);

        if (minute.Minutes != 1)
            return $"the bar at {minute.OpenTime:O} was built from {minute.Minutes} minutes, and a declared bar is " +
                   "built from closed one-minute bars: this run's bars are not one series";

        if (minute.OpenTime.UtcTicks % TimeSpan.TicksPerMinute != 0)
            return $"the bar at {minute.OpenTime:O} does not open on a whole minute, so it is not one of the closed " +
                   "one-minute bars a declared bar is built from: this run's bars are not one series";

        if (_lastMinute is { } last && minute.OpenTime <= last)
            return $"the bar at {minute.OpenTime:O} is not after the bar at {last:O}: minutes reach the resampler in " +
                   "ascending order, one for each closed minute, and never twice";

        return null;
    }

    /// <summary>
    /// THE BAR STILL FORMING, CLOSED — because a minute opening at <paramref name="next"/> is at or past
    /// its end, so its last minute is over whether or not the data has it. Null when nothing is forming or
    /// <paramref name="next"/> is inside the bar.
    /// </summary>
    public KlineBar? CloseBefore(DateTimeOffset next) =>
        _start is not null && next >= _end ? Emit() : null;

    /// <summary>
    /// ONE CLOSED MINUTE, which must pass <see cref="Refusal"/> and must not belong to a later bar than the
    /// one forming (<see cref="CloseBefore"/> closes that first). Answers the bar this minute completes —
    /// it is the last minute of its window — or null.
    /// </summary>
    public KlineBar? Add(KlineBar minute)
    {
        if (Refusal(minute) is { } why) throw new ArgumentException(why, nameof(minute));

        var start = Grid.StartOf(minute.OpenTime);
        if (_start is { } forming && forming != start)
            throw new InvalidOperationException(
                $"the bar opening at {forming:O} has not closed, and the minute at {minute.OpenTime:O} belongs to a " +
                "later one: CloseBefore closes it first");

        if (_start is null)
        {
            _start = start;
            _end = Grid.EndOf(start);
            _open = minute.Open;
            _high = minute.High;
            _low = minute.Low;
            _volume = 0m;
            _minutes = 0;
            _midpoint = false;
        }
        else
        {
            if (minute.High > _high) _high = minute.High;
            if (minute.Low < _low) _low = minute.Low;
        }

        _close = minute.Close;
        _volume += minute.Volume;
        _minutes++;
        _midpoint |= minute.Quality == BarQuality.MidpointDerived;
        _lastMinute = minute.OpenTime;

        return minute.OpenTime + StrategyBars.OneMinute >= _end ? Emit() : null;
    }

    /// <summary>
    /// THE BAR STILL FORMING, CLOSED BECAUSE A RUN OVER HISTORY HAS ENDED INSIDE IT, or null when nothing
    /// is forming. Its remaining minutes happened and are not in the data, so it closes as a partial bar —
    /// its <see cref="KlineBar.Minutes"/> says how partial.
    /// </summary>
    public KlineBar? Flush() => _start is null ? null : Emit();

    /// <summary>
    /// A WHOLE SERIES, RESAMPLED: every bar closed by these minutes, in order, the last one flushed. For a
    /// reader of history; a caller that needs to act between a bar's minutes uses the three calls above.
    /// </summary>
    public static IEnumerable<KlineBar> Resample(IEnumerable<KlineBar> minutes, BarGrid grid)
    {
        ArgumentNullException.ThrowIfNull(minutes);
        var resampler = new BarResampler(grid);

        foreach (var minute in minutes)
        {
            if (resampler.CloseBefore(minute.OpenTime) is { } passed) yield return passed;
            if (resampler.Add(minute) is { } completed) yield return completed;
        }

        if (resampler.Flush() is { } last) yield return last;
    }

    KlineBar Emit()
    {
        var bar = new KlineBar(_start!.Value, _open, _high, _low, _close, _volume)
        {
            Minutes = _minutes,
            Quality = _midpoint ? BarQuality.MidpointDerived : BarQuality.Traded
        };

        _start = null;
        return bar;
    }
}

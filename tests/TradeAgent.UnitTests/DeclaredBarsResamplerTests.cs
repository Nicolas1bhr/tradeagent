using TradeAgent.Core.Data;
using TradeAgent.Core.Strategy;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ONE SHARED RESAMPLER: CLOSED MINUTES IN, CLOSED DECLARED BARS OUT (<c>U-timeframe-a</c> item 2).
///
/// <para>A declared bar covers <c>[k·d, (k+1)·d)</c> in UTC — at the program's own zone's midnight for a
/// day — and is its first minute's open, its minutes' extremes, its last minute's close and their summed
/// volume. It closes when its last minute has closed. An empty window is a gap and never a bar; a partial
/// one is a bar whose <see cref="KlineBar.Minutes"/> says so. And the evaluator, stepped on those bars,
/// keeps counting what is missing in MINUTES.</para>
///
/// <para><b>RED on the base</b> (<c>d99155e</c>) by not compiling: <c>BarResampler</c>, <c>BarGrid</c> and
/// <c>KlineBar.Minutes</c> did not exist, and neither did the <c>bars</c> declaration the evaluator runs
/// below read.</para>
/// </summary>
public class DeclaredBarsResamplerTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Day = new(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A deterministic minute at <paramref name="i"/> minutes after <see cref="Day"/>: every field moves,
    /// so a bar built from the wrong minute — the second instead of the first open, the close of any but the
    /// last — is a different number.
    /// </summary>
    static KlineBar Minute(int i, DateTimeOffset? origin = null)
    {
        var mid = 100m + i % 17 * 0.5m - i % 5 * 0.25m;
        return new KlineBar((origin ?? Day).AddMinutes(i), mid - 0.1m, mid + 0.3m + i % 3 * 0.2m,
            mid - 0.2m - i % 4 * 0.15m, mid, 1m + i % 7);
    }

    static IEnumerable<KlineBar> Minutes(IEnumerable<int> indices) => indices.Select(i => Minute(i));

    static StrategyProgram Program(string bars, string zone = "UTC")
    {
        var parse = StrategyParser.Parse($"""
            instrument BTCUSDT
            timezone {zone}
            bars {bars}
            size fixed 1
            indicator m = sma(close, 2)
            exit when close < m
            entry when close > m
            """);
        Assert.True(parse.Ok, parse.Why);
        return parse.Program!;
    }

    /// <summary>The bars a reader would build by hand: grouped by window, first open, extremes, last close, summed volume.</summary>
    static List<KlineBar> ByHand(IEnumerable<KlineBar> minutes, BarGrid grid) =>
    [
        .. minutes.GroupBy(m => grid.StartOf(m.OpenTime)).Select(g => new KlineBar(
            g.Key, g.First().Open, g.Max(m => m.High), g.Min(m => m.Low), g.Last().Close, g.Sum(m => m.Volume))
        {
            Minutes = g.Count()
        })
    ];

    [Theory]
    [InlineData("5m")]
    [InlineData("15m")]
    [InlineData("30m")]
    [InlineData("1h")]
    [InlineData("4h")]
    [InlineData("1d")]
    public void A_bar_is_its_first_open_its_extremes_its_last_close_and_its_summed_volume(string bars)
    {
        var grid = BarGrid.For(Program(bars));
        var minutes = Minutes(Enumerable.Range(0, 3 * 1440).Where(i => i % 97 != 13)).ToList();

        var resampled = BarResampler.Resample(minutes, grid).ToList();

        Assert.Equal(ByHand(minutes, grid), resampled);
        Assert.All(resampled, b => Assert.Equal(grid.StartOf(b.OpenTime), b.OpenTime));
    }

    /// <summary>
    /// (b) A PARTIAL HOUR CARRIES ITS MINUTE COUNT AND AN EMPTY HOUR IS A GAP.
    ///
    /// <para>Four hours of minutes: the first whole, the second only its first 45, the third nothing at all,
    /// the fourth whole. Three bars come out — the partial one saying 45, built from those 45 minutes and no
    /// other — and nothing stands in for the empty hour. Stepped on those bars, an hourly program's evaluator
    /// counts the 15 minutes the second hour is short and the 60 of the third: 75 MINUTES, the same count a
    /// one-minute program over the same minutes reports, and never "one bar".</para>
    /// </summary>
    [Fact]
    public void A_partial_hour_carries_its_minute_count_and_an_empty_hour_is_a_gap()
    {
        var hourly = Program("1h");
        var minutes = Minutes(Enumerable.Range(0, 60).Concat(Enumerable.Range(60, 45)).Concat(Enumerable.Range(180, 60)))
            .ToList();

        var bars = BarResampler.Resample(minutes, BarGrid.For(hourly)).ToList();
        foreach (var b in bars) log.WriteLine($"{b.OpenTime:HH:mm} {b.Minutes,2} min  O {b.Open} H {b.High} L {b.Low} C {b.Close} V {b.Volume}");

        Assert.Equal([Day, Day.AddHours(1), Day.AddHours(3)], bars.Select(b => b.OpenTime));
        Assert.Equal([60, 45, 60], bars.Select(b => b.Minutes));

        var partial = bars[1];
        var its = minutes.Where(m => m.OpenTime >= Day.AddHours(1) && m.OpenTime < Day.AddHours(2)).ToList();
        Assert.Equal(45, its.Count);
        Assert.Equal(its[0].Open, partial.Open);
        Assert.Equal(its.Max(m => m.High), partial.High);
        Assert.Equal(its.Min(m => m.Low), partial.Low);
        Assert.Equal(its[^1].Close, partial.Close);
        Assert.Equal(its.Sum(m => m.Volume), partial.Volume);

        var run = StrategyEvaluator.Run(hourly, bars, (_, _) => AccountReading.Flat(10_000m));
        log.WriteLine($"hourly: bars {run.Counters.Bars}, missing {run.Counters.MissingMinutes} min in {run.Counters.GapRuns} run(s)");

        Assert.False(run.Faulted, run.FaultReason);
        Assert.Equal(3, run.Counters.Bars);
        Assert.Equal(75, run.Counters.MissingMinutes);
        Assert.Equal(2, run.Counters.GapRuns);
        Assert.Equal(new GapRun(Day.AddHours(1), Day.AddHours(1), 15), run.GapRuns[0]);
        Assert.Equal(new GapRun(Day.AddHours(2), Day.AddHours(2), 60), run.GapRuns[1]);

        var everyMinute = StrategyEvaluator.Run(Program("1m"), minutes, (_, _) => AccountReading.Flat(10_000m));
        Assert.Equal(75, everyMinute.Counters.MissingMinutes);
    }

    /// <summary>
    /// A BAR CLOSES WHEN ITS LAST MINUTE HAS CLOSED, AND NEVER BEFORE: when that minute arrives, or when a
    /// minute at or past the bar's end arrives instead. A minute inside the bar closes nothing.
    /// </summary>
    [Fact]
    public void A_bar_closes_on_its_last_minute_or_when_a_later_minute_arrives_and_never_before()
    {
        var resampler = new BarResampler(BarGrid.Uniform(TimeSpan.FromHours(1)));

        for (var i = 0; i < 59; i++)
        {
            Assert.Null(resampler.CloseBefore(Day.AddMinutes(i)));
            Assert.Null(resampler.Add(Minute(i)));
        }

        Assert.Null(resampler.CloseBefore(Day.AddMinutes(59)));
        var whole = resampler.Add(Minute(59));
        Assert.Equal((Day, 60), (whole!.OpenTime, whole.Minutes));
        Assert.False(resampler.Forming);

        // The next hour loses its last twenty minutes: nothing closes it until a minute past its end.
        for (var i = 60; i < 100; i++) Assert.Null(resampler.Add(Minute(i)));
        Assert.True(resampler.Forming);
        Assert.Null(resampler.CloseBefore(Day.AddMinutes(119)));
        var partial = resampler.CloseBefore(Day.AddMinutes(185));
        Assert.Equal((Day.AddHours(1), 40), (partial!.OpenTime, partial.Minutes));

        // And a run over history that ends inside an hour closes it as the partial bar it is.
        Assert.Null(resampler.Add(Minute(185)));
        var flushed = resampler.Flush();
        Assert.Equal((Day.AddHours(3), 1), (flushed!.OpenTime, flushed.Minutes));
        Assert.Null(resampler.Flush());
    }

    /// <summary>
    /// A MINUTE OUT OF ORDER, TWICE, OFF THE MINUTE OR ALREADY RESAMPLED IS REFUSED IN WORDS — the
    /// evaluator's own refusals, made one level down where the minutes are.
    /// </summary>
    [Fact]
    public void A_minute_out_of_order_twice_off_the_minute_or_already_resampled_is_refused_in_words()
    {
        var resampler = new BarResampler(BarGrid.Uniform(TimeSpan.FromHours(1)));
        resampler.Add(Minute(10));

        Assert.Contains("is not after the bar at", resampler.Refusal(Minute(10)), StringComparison.Ordinal);
        Assert.Contains("is not after the bar at", resampler.Refusal(Minute(3)), StringComparison.Ordinal);
        Assert.Contains("does not open on a whole minute",
            resampler.Refusal(Minute(11) with { OpenTime = Day.AddMinutes(11).AddSeconds(30) }), StringComparison.Ordinal);
        Assert.Contains("was built from 60 minutes", resampler.Refusal(Minute(11) with { Minutes = 60 }),
            StringComparison.Ordinal);
        Assert.Null(resampler.Refusal(Minute(11)));
        Assert.Throws<ArgumentException>(() => resampler.Add(Minute(10)));
        Assert.Throws<InvalidOperationException>(() => resampler.Add(Minute(70)));
    }

    /// <summary>A bar with one midpoint-derived minute in it is midpoint-derived: its volume is no longer a measurement of trades.</summary>
    [Fact]
    public void A_bar_with_a_midpoint_derived_minute_in_it_is_midpoint_derived()
    {
        var minutes = Minutes(Enumerable.Range(0, 120)).Select(m =>
            m.OpenTime == Day.AddMinutes(75) ? m with { Quality = BarQuality.MidpointDerived } : m);

        var bars = BarResampler.Resample(minutes, BarGrid.Uniform(TimeSpan.FromHours(1))).ToList();

        Assert.Equal([BarQuality.Traded, BarQuality.MidpointDerived], bars.Select(b => b.Quality));
    }

    /// <summary>
    /// A DAILY BAR STARTS AT MIDNIGHT IN THE PROGRAM'S ZONE, and a daylight change makes it 23 or 25 hours
    /// long — counted, so a complete New York day is never read as an hour short or an hour over.
    /// </summary>
    [Fact]
    public void A_daily_bar_starts_at_midnight_in_the_programs_zone_and_a_daylight_change_moves_its_length()
    {
        var ny = BarGrid.For(Program("1d", "America/New_York"));
        var tokyo = BarGrid.For(Program("1d", "Asia/Tokyo"));
        var utc = BarGrid.For(Program("1d"));
        var spring = new DateTimeOffset(2026, 3, 8, 12, 0, 0, TimeSpan.Zero);
        var autumn = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("America/New_York", ny.Zone);
        Assert.Null(utc.Zone);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 5, 0, 0, TimeSpan.Zero), ny.StartOf(spring));
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 4, 0, 0, TimeSpan.Zero), ny.EndOf(ny.StartOf(spring)));
        Assert.Equal(1380, ny.MinutesIn(ny.StartOf(spring)));
        Assert.Equal(1500, ny.MinutesIn(ny.StartOf(autumn)));
        Assert.Equal(1440, ny.MinutesIn(ny.StartOf(spring.AddDays(7))));
        Assert.Equal(new DateTimeOffset(2026, 3, 7, 5, 0, 0, TimeSpan.Zero), ny.PreviousStart(ny.StartOf(spring)));
        Assert.Equal(new DateTimeOffset(2026, 3, 7, 15, 0, 0, TimeSpan.Zero), tokyo.StartOf(spring));
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 0, 0, 0, TimeSpan.Zero), utc.StartOf(spring));

        // Three New York days of minutes across the spring change: three whole bars, nothing missing.
        var origin = new DateTimeOffset(2026, 3, 7, 5, 0, 0, TimeSpan.Zero);
        var minutes = Enumerable.Range(0, (int)BarGrid.MinutesBetween(origin, ny.EndOf(ny.StartOf(spring.AddDays(1)))))
            .Select(i => Minute(i, origin)).ToList();
        var bars = BarResampler.Resample(minutes, ny).ToList();
        Assert.Equal([1440, 1380, 1440], bars.Select(b => b.Minutes));

        var run = StrategyEvaluator.Run(Program("1d", "America/New_York"), bars, (_, _) => AccountReading.Flat(10_000m));
        Assert.False(run.Faulted, run.FaultReason);
        Assert.Equal(0, run.Counters.MissingMinutes);

        // And a day the data skips entirely is a gap of that day's own length.
        var skipped = StrategyEvaluator.Run(Program("1d", "America/New_York"), [bars[0], bars[2]],
            (_, _) => AccountReading.Flat(10_000m));
        Assert.Equal(1380, skipped.Counters.MissingMinutes);
    }

    /// <summary>
    /// EVERY LOCAL MIDNIGHT EXISTS ONCE IN EVERY ZONE A PROGRAM CAN NAME, for every day of seven years: a
    /// daily bar's end is the next one's start, it is midnight on the zone's own wall clock, and no day is
    /// shorter than 23 hours or longer than 25.
    /// </summary>
    [Fact]
    public void Every_daily_bar_in_every_zone_starts_at_a_midnight_that_exists_once()
    {
        foreach (var zone in StrategyLimits.TimeZones)
        {
            var grid = BarGrid.For(Program("1d", zone));
            Assert.True(StrategyCalendar.TryZone(zone, out var rules));
            var start = grid.StartOf(new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero));

            for (var day = 0; day < 7 * 366; day++)
            {
                var end = grid.EndOf(start);
                Assert.Equal(TimeSpan.Zero, StrategyCalendar.Local(rules, start).TimeOfDay);
                Assert.Equal(end, grid.StartOf(end));
                Assert.Equal(start, grid.PreviousStart(end));
                Assert.InRange(grid.MinutesIn(start), 23 * 60, 25 * 60);
                start = end;
            }
        }
    }

    /// <summary>
    /// A PROGRAM IS EVALUATED ON THE BAR IT DECLARES OR NOT AT ALL: a caller that says it will feed minutes
    /// to an hourly program, or hours to a minute program, is refused before a bar is stepped.
    /// </summary>
    [Fact]
    public void The_evaluator_refuses_to_step_a_program_on_bars_it_did_not_declare()
    {
        var refused = Assert.Throws<ArgumentException>(() =>
            EvaluationState.Start(Program("1h"), barInterval: TimeSpan.FromMinutes(1)));
        Assert.Contains("this program declares bars of 1h, and this run was asked to evaluate it on bars of 1m",
            refused.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => EvaluationState.Start(Program("1m"), barInterval: TimeSpan.FromHours(1)));

        Assert.Equal(TimeSpan.FromHours(1), EvaluationState.Start(Program("1h")).BarInterval);
        Assert.Equal(TimeSpan.FromHours(1),
            EvaluationState.Start(Program("1h"), barInterval: TimeSpan.FromHours(1)).BarInterval);

        // Fed minutes anyway, an hourly run faults on the first one off the hour rather than reading it as an
        // hour; and a bar claiming more minutes than its window holds is not one of its bars either.
        var fed = StrategyEvaluator.Run(Program("1h"), Minutes(Enumerable.Range(0, 3)), (_, _) => AccountReading.Flat(10_000m));
        Assert.True(fed.Faulted);
        Assert.Equal(1, fed.Counters.Bars);
        Assert.Contains("does not open on the 1h grid", fed.FaultReason, StringComparison.Ordinal);

        var overfull = StrategyEvaluator.Run(Program("1h"), [Minute(0) with { Minutes = 61 }],
            (_, _) => AccountReading.Flat(10_000m));
        Assert.Contains("says it was built from 61 minute(s), and one bar of 1h spans 60", overfull.FaultReason,
            StringComparison.Ordinal);
    }

    /// <summary>An hourly intent names its bar's open and may first be acted on at that bar's close, an hour later.</summary>
    [Fact]
    public void An_hourly_intent_may_first_be_acted_on_at_its_bars_close()
    {
        var bars = BarResampler.Resample(Minutes(Enumerable.Range(0, 6 * 60)), BarGrid.Uniform(TimeSpan.FromHours(1)))
            .ToList();

        var run = StrategyEvaluator.Run(Program("1h"), bars, (_, _) => AccountReading.Flat(10_000m));

        Assert.NotEmpty(run.Intents);
        Assert.All(run.Intents, i => Assert.Equal(i.Bar + TimeSpan.FromHours(1), i.NotBefore));
    }
}

using TradeAgent.Core.Data;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE BAR FIXTURES THE EVALUATOR IS PINNED OVER.
///
/// <para>Synthetic and explicit: every price is written out below rather than generated, because a
/// golden series is only a tripwire if the INPUT cannot move either. The shape is deliberate — a
/// twelve-bar rise, a flat run, a twelve-bar fall and a rise back, with three bars that gap through
/// the previous close so that all three terms of a true range win somewhere.</para>
/// </summary>
public static class SyntheticBars
{
    static KlineBar At(int minute, decimal open, decimal high, decimal low, decimal close, decimal volume) =>
        new(new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero).AddMinutes(minute),
            open, high, low, close, volume);

    /// <summary>Forty consecutive one-minute bars from 2026-01-05T00:00Z.</summary>
    public static IReadOnlyList<KlineBar> Forty { get; } = Build();

    static IReadOnlyList<KlineBar> Build()
    {
        decimal[][] rows =
        [
            [100.00m, 100.40m, 99.60m, 100.00m, 10m],
            [100.00m, 101.15m, 99.60m, 100.75m, 11m],
            [100.75m, 101.90m, 100.35m, 101.50m, 12m],
            [101.50m, 102.65m, 101.10m, 102.25m, 13m],
            [102.25m, 103.40m, 101.85m, 103.00m, 14m],
            [103.00m, 103.40m, 102.00m, 102.40m, 15m],
            [102.40m, 104.00m, 102.00m, 103.60m, 16m],
            [104.80m, 105.20m, 104.40m, 104.80m, 17m],
            [104.80m, 106.40m, 104.40m, 106.00m, 18m],
            [106.00m, 106.40m, 104.80m, 105.20m, 19m],
            [105.20m, 106.80m, 104.80m, 106.40m, 20m],
            [106.40m, 108.00m, 106.00m, 107.60m, 21m],
            [107.60m, 108.40m, 107.20m, 108.00m, 22m],
            [108.00m, 108.40m, 107.60m, 108.00m, 23m],
            [108.00m, 108.40m, 107.60m, 108.00m, 24m],
            [108.00m, 108.40m, 107.55m, 107.95m, 25m],
            [107.95m, 108.35m, 106.10m, 106.50m, 26m],
            [106.50m, 106.90m, 104.60m, 105.00m, 27m],
            [105.00m, 105.40m, 103.10m, 103.50m, 28m],
            [103.50m, 103.90m, 101.60m, 102.00m, 29m],
            [100.70m, 101.10m, 100.10m, 100.50m, 30m],
            [100.50m, 100.90m, 98.60m, 99.00m, 31m],
            [99.00m, 99.40m, 97.10m, 97.50m, 32m],
            [97.50m, 97.90m, 95.60m, 96.00m, 33m],
            [96.00m, 97.60m, 95.60m, 97.20m, 34m],
            [97.20m, 97.60m, 95.40m, 95.80m, 35m],
            [95.80m, 96.20m, 94.00m, 94.40m, 36m],
            [94.40m, 94.80m, 92.60m, 93.00m, 37m],
            [93.00m, 94.90m, 92.60m, 94.50m, 38m],
            [94.50m, 96.40m, 94.10m, 96.00m, 39m],
            [96.00m, 97.90m, 95.60m, 97.50m, 40m],
            [97.50m, 99.40m, 97.10m, 99.00m, 41m],
            [99.00m, 100.90m, 98.60m, 100.50m, 42m],
            [101.30m, 102.40m, 100.90m, 102.00m, 43m],
            [102.00m, 103.90m, 101.60m, 103.50m, 44m],
            [103.50m, 105.40m, 103.10m, 105.00m, 45m],
            [105.00m, 105.40m, 103.85m, 104.25m, 46m],
            [104.25m, 106.15m, 103.85m, 105.75m, 47m],
            [105.75m, 107.65m, 105.35m, 107.25m, 48m],
            [107.25m, 109.15m, 106.85m, 108.75m, 49m]
        ];

        return [.. rows.Select((r, i) => At(i, r[0], r[1], r[2], r[3], r[4]))];
    }

    /// <summary>
    /// <paramref name="count"/> consecutive one-minute bars from <paramref name="from"/>, each with
    /// the close <paramref name="close"/> gives for its index and a high and low half a unit either
    /// side. For the time-filter and calendar tests, where the prices are beside the point.
    /// </summary>
    public static IReadOnlyList<KlineBar> Minutes(
        DateTimeOffset from, int count, Func<int, decimal>? close = null)
    {
        var price = close ?? (_ => 100m);

        return [.. Enumerable.Range(0, count).Select(i =>
        {
            var value = price(i);
            return new KlineBar(from.AddMinutes(i), value, value + 0.50m, value - 0.50m, value, 1m);
        })];
    }

    /// <summary>
    /// THE FIXTURE THE MOVING-AVERAGE CROSSOVER IS RUN OVER: sixty flat minutes, sixty rising, sixty
    /// falling, from 2026-01-05T00:00Z. The flat stretch is longer than the program's fifty-bar
    /// average, so the crossing happens where the rise overtakes it and not where the fixture starts.
    /// </summary>
    public static IReadOnlyList<KlineBar> Crossover { get; } = Minutes(
        new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero), 180,
        i => i < 60 ? 100m : i < 120 ? 100m + (i - 59) : 160m - (i - 119));

    /// <summary>
    /// THE FIXTURE THE OPENING-RANGE BREAKOUT IS RUN OVER — one New York trading day in EDT, from
    /// 13:00Z (09:00 ET): thirty minutes to warm the ATR, thirty minutes of opening range whose high
    /// reaches 101.00, an hour of rise through it inside the entry window, and an hour of fall back
    /// through the range low.
    /// </summary>
    public static IReadOnlyList<KlineBar> OpeningRange { get; } = Minutes(
        new DateTimeOffset(2026, 7, 6, 13, 0, 0, TimeSpan.Zero), 180,
        i => i < 30 ? 100m
            : i < 60 ? 100m + (i % 2) * 0.5m
            : i < 120 ? 101m + (i - 60) * 0.1m
            : 106.9m - (i - 119) * 0.2m);

    /// <summary>
    /// THE FIXTURE THE RSI MEAN REVERSION IS RUN OVER: twenty rising minutes to put the RSI near its
    /// top, thirty falling to take it under thirty, then forty rising to bring it back over
    /// fifty-five.
    /// </summary>
    public static IReadOnlyList<KlineBar> MeanReversion { get; } = Minutes(
        new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero), 90,
        i => i < 20 ? 100m + i * 0.5m
            : i < 50 ? 109.5m - (i - 19)
            : 79.5m + (i - 49));
}

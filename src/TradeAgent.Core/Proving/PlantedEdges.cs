using System.Globalization;
using TradeAgent.Core.Strategy;

namespace TradeAgent.Core.Proving;

/// <summary>
/// WHETHER A PLANTED RELATION IS ONE THE CANON'S PLAYBOOK CAN NAME (<c>docs/VISION.md</c> § 6.4: "planted edges … inside and
/// outside current doctrine, so the exam measures discovery and not recall of the playbook").
/// </summary>
public enum EdgeDoctrine
{
    /// <summary>BESIDE doctrine: a relation no playbook names — only discovery finds it. The off-path exam's material.</summary>
    Beside,

    /// <summary>INSIDE doctrine: a relation on a series shaped like a real family's, which a playbook's recall can apply.</summary>
    Inside
}

/// <summary>A window a planted edge is on in: from day <see cref="FromDay"/> to day <see cref="ToDay"/> of the market, the end excluded.</summary>
public sealed record SyntheticRegime(int FromDay, int ToDay);

/// <summary>
/// ONE PLANTED EDGE (<c>U-proving-ground-a</c> item 4, <c>U-proving-ground-b</c> item 1): the instrument's return over
/// <see cref="HorizonMinutes"/> from the first hourly close after a reading of <see cref="Series"/> was FIRST SEEN gains
/// β·w ticks per unit of that reading — β = <see cref="BetaTicks"/>, its sign the edge's, and w its weight at the hour the move
/// starts: 2^(−t/H) with t the hours since the market's start and H <see cref="HalfLifeDays"/> in hours (1 without a half-life),
/// times 1 inside one of <see cref="Regimes"/> and 0 outside them (always on without any). A hand-set constant, never fitted
/// to anything.
/// </summary>
/// <param name="BetaTicks">Ticks of the planted move per unit of the reading's integer value; its sign is the relation's.</param>
/// <param name="HorizonMinutes">How many minutes after that close the move is spread over: 1 to 60.</param>
public sealed record SyntheticEdge(long BetaTicks, int HorizonMinutes = 60)
{
    /// <summary>The series the edge rides — one of the market's <see cref="SyntheticMarketSpec.Series"/>.</summary>
    public string Series { get; init; } = ProvingSources.Signal;

    /// <summary>The days over which β halves, or null for an edge that does not decay.</summary>
    public int? HalfLifeDays { get; init; }

    /// <summary>The windows the edge is on in; none means always.</summary>
    public IReadOnlyList<SyntheticRegime> Regimes { get; init; } = [];

    /// <summary>Whether a playbook can name the relation: INSIDE only on a series shaped like a real family's.</summary>
    public EdgeDoctrine Doctrine { get; init; } = EdgeDoctrine.Beside;

    /// <summary>The edge as one canonical line: what names a market that carries it.</summary>
    internal string Canonical => string.Create(CultureInfo.InvariantCulture,
        $"{BetaTicks}/{HorizonMinutes}/{Series}/{HalfLifeDays?.ToString(CultureInfo.InvariantCulture) ?? "-"}/"
        + $"{string.Join(",", Regimes.Select(r => $"{r.FromDay}-{r.ToDay}"))}/{Doctrine}");
}

/// <summary>
/// WHAT A PLANTED EDGE IS WORTH, COMPUTED FROM ITS SPEC AND NEVER FROM A RUN (<c>U-proving-ground-b</c> item 1) — the
/// reference trade: one position per reading, in the relation's direction, from the close the move starts at to the end of
/// its horizon, at the first open's price.
/// </summary>
/// <param name="Readings">How many readings of the series start a move inside the market.</param>
/// <param name="MeanWeight">The mean of w over those readings: the half-life's decay times the regime's on and off.</param>
/// <param name="BpsPerSignal">The expected gross move per reading, |β|·E|z|·w̄, in basis points of the first open.</param>
/// <param name="RoundTripBps">Two fills at the venue's published taker fee and the app's slippage, or null where this build has read no fee.</param>
/// <param name="SharpeBeforeCost">The reference trade's annualised Sharpe before costs.</param>
/// <param name="SharpeAfterCost">The same after the venue's round trip on every reading, or null without a fee.</param>
public sealed record PlantedFigures(
    int Readings, decimal MeanWeight, decimal BpsPerSignal, decimal? RoundTripBps, decimal SharpeBeforeCost, decimal? SharpeAfterCost);

/// <summary>
/// THE PLANTED MOVES OF A MARKET (<c>U-proving-ground-b</c> item 1), IN INTEGERS AND DECIMALS ONLY: β·z·w rounded to a whole
/// tick, half to even. 2^(−x) is a decimal series here, never <see cref="Math.Pow(double, double)"/>, whose result is not the
/// same on every platform — and an edge without a half-life or a regime moves by exactly β·z, as part a's did.
/// </summary>
internal static class PlantedEdges
{
    /// <summary>ln 2 to decimal's 28 digits.</summary>
    const decimal Ln2 = 0.6931471805599453094172321215m;

    /// <summary>2^(−<paramref name="num"/>/<paramref name="den"/>) for a non-negative fraction, in decimal arithmetic alone.</summary>
    internal static decimal Exp2Neg(long num, long den)
    {
        if (num < 0 || den <= 0) throw new ArgumentOutOfRangeException(nameof(num), "2^(−x) is taken here only for x ≥ 0");
        var whole = num / den;
        var x = Ln2 * (num % den) / den;

        // e^(−x) for 0 ≤ x < ln 2, its Taylor series to well past decimal's last digit.
        decimal term = 1m, sum = 1m;
        for (var n = 1; n <= 40; n++)
        {
            term = term * x / n;
            sum += n % 2 == 1 ? -term : term;
        }
        for (var i = 0L; i < whole && sum != 0m; i++) sum /= 2m;
        return sum;
    }

    /// <summary>A decimal square root: Newton's iteration from the double's correctly rounded one.</summary>
    internal static decimal Sqrt(decimal v)
    {
        if (v < 0m) throw new ArgumentOutOfRangeException(nameof(v));
        if (v == 0m) return 0m;
        var r = (decimal)Math.Sqrt((double)v);
        for (var i = 0; i < 8; i++) r = (r + v / r) / 2m;
        return r;
    }

    /// <summary>Whether <paramref name="edge"/> is on in the hour starting <paramref name="hour"/> hours after the market's start.</summary>
    internal static bool On(SyntheticEdge edge, long hour) =>
        edge.Regimes.Count == 0 || edge.Regimes.Any(r => hour >= r.FromDay * 24L && hour < r.ToDay * 24L);

    /// <summary>The edge's weight w for a move starting <paramref name="hour"/> hours after the market's start.</summary>
    internal static decimal Weight(SyntheticEdge edge, long hour) =>
        !On(edge, hour) ? 0m : edge.HalfLifeDays is { } days ? Exp2Neg(hour, days * 24L) : 1m;

    /// <summary>The planted move, in ticks, of a reading of value <paramref name="z"/> whose move starts at <paramref name="hour"/>.</summary>
    internal static long Move(SyntheticEdge edge, long z, long hour)
    {
        if (!On(edge, hour)) return 0L;
        if (edge.HalfLifeDays is null) return edge.BetaTicks * z;
        return (long)decimal.Round(edge.BetaTicks * z * Weight(edge, hour), 0, MidpointRounding.ToEven);
    }

    /// <summary>
    /// THE HOUR A READING'S MOVE STARTS: the first hourly close after it was first seen — <paramref name="sourceMinute"/>
    /// minutes after the market's start, first seen <paramref name="seenAfter"/> later.
    /// </summary>
    internal static long MoveHour(long sourceMinute, TimeSpan seenAfter) =>
        (sourceMinute * 60L + (long)seenAfter.TotalSeconds) / 3600L + 1L;

    /// <summary>
    /// THE PLANTED DRIFT OF EVERY MINUTE OF THE MARKET: each edge's move per reading of its series, spread over its horizon as
    /// part a spread it — the move over the horizon in whole ticks a minute, the remainder on its last minute.
    /// </summary>
    internal static long[] Drift(SyntheticMarketSpec spec, IReadOnlyDictionary<string, long[]> values)
    {
        var minutes = spec.Days * 1440;
        var drift = new long[minutes];
        foreach (var edge in spec.Planted)
        {
            var shape = ProvingSources.Shape(edge.Series)!;
            var z = values[edge.Series];
            for (var k = 0; k < z.Length; k++)
            {
                var hour = MoveHour(shape.SourceMinute(k), ProvingSources.Delay);
                var first = hour * 60L;
                if (first >= minutes) continue;

                var move = Move(edge, z[k], hour);
                var horizon = edge.HorizonMinutes;
                for (var m = 0; m < horizon && first + m < minutes; m++)
                    drift[first + m] += move / horizon + (m == horizon - 1 ? move % horizon : 0L);
            }
        }
        return drift;
    }

    /// <summary>
    /// THE VARIANCE OF ONE MINUTE'S NOISE, IN TICKS²: four uniform draws of ±<see cref="SyntheticMarket.StepHalfWidth"/>, each
    /// ((2·10 + 1)² − 1)/12, doubled in amplitude in the high-volatility regime, which holds half the time.
    /// </summary>
    internal static decimal MinuteVariance
    {
        get
        {
            var width = 2m * SyntheticMarket.StepHalfWidth + 1m;
            return 4m * (width * width - 1m) / 12m * (1m + 4m) / 2m;
        }
    }

    /// <summary>E|z| for a reading drawn uniformly from −R to R: R(R + 1)/(2R + 1).</summary>
    internal static decimal MeanAbsoluteValue => SyntheticMarket.SignalRange * (SyntheticMarket.SignalRange + 1m) / (2m * SyntheticMarket.SignalRange + 1m);

    /// <summary>The round trip at <paramref name="venueId"/>, in basis points: two fills at its published taker fee and the app's slippage.</summary>
    internal static decimal? RoundTripBps(string venueId) =>
        VenueCostModel.FeeOf(venueId) is { } fee ? 2m * (fee.TakerRate + VenueCostModel.SlippageRate) * 10_000m : null;

    /// <summary>What <paramref name="edge"/> is worth in <paramref name="spec"/>'s market — see <see cref="PlantedFigures"/>.</summary>
    internal static PlantedFigures Figures(SyntheticMarketSpec spec, SyntheticEdge edge)
    {
        var shape = ProvingSources.Shape(edge.Series)!;
        var hours = spec.Days * 24L;
        var readings = 0;
        var weights = 0m;
        for (var k = 0; k < shape.Count(spec.Days); k++)
        {
            var hour = MoveHour(shape.SourceMinute(k), ProvingSources.Delay);
            if (hour >= hours) continue;
            readings++;
            weights += Weight(edge, hour);
        }

        var mean = readings == 0 ? 0m : weights / readings;
        var gross = Math.Abs(edge.BetaTicks) * MeanAbsoluteValue * mean;
        var sigma = Sqrt(MinuteVariance * edge.HorizonMinutes);
        var perYear = Sqrt(readings * 365m / spec.Days);
        var trip = RoundTripBps(spec.VenueId);
        var tripTicks = trip * SyntheticMarket.StartTicks / 10_000m;

        return new PlantedFigures(
            readings,
            decimal.Round(mean, 6, MidpointRounding.ToEven),
            decimal.Round(gross * 10_000m / SyntheticMarket.StartTicks, 2, MidpointRounding.ToEven),
            trip is { } t ? decimal.Round(t, 2, MidpointRounding.ToEven) : null,
            decimal.Round(gross / sigma * perYear, 2, MidpointRounding.ToEven),
            tripTicks is { } c ? decimal.Round((gross - c) / sigma * perYear, 2, MidpointRounding.ToEven) : null);
    }

    /// <summary>
    /// WHY <paramref name="edge"/> CANNOT BE PLANTED IN <paramref name="spec"/>'s MARKET, in words — or null. Asked of every
    /// edge before anything is written.
    /// </summary>
    internal static string? Refusal(SyntheticMarketSpec spec, SyntheticEdge edge)
    {
        if (edge.HorizonMinutes is < 1 or > 60) return "a planted edge's horizon is 1 to 60 minutes";
        if (ProvingSources.Shape(edge.Series) is not { } shape)
            return $"a planted edge rides one of the proving ground's series ({string.Join(", ", ProvingSources.Shapes.Select(s => s.Id))}), and '{edge.Series}' is none of them";
        if (!spec.Series.Contains(edge.Series, StringComparer.Ordinal))
            return $"a planted edge rides a series its market writes, and this market writes {string.Join(", ", spec.Series)}, not '{edge.Series}'";
        if (edge.HalfLifeDays is < 1)
            return $"a half-life is a whole number of days, at least one, and this one is {edge.HalfLifeDays}";
        foreach (var r in edge.Regimes)
            if (r.FromDay < 0 || r.ToDay <= r.FromDay || r.ToDay > spec.Days)
                return $"a regime window runs from one day of the market to a later one, inside its {spec.Days} days, and {r.FromDay}–{r.ToDay} does not";
        if (edge.Doctrine == EdgeDoctrine.Inside && !shape.Family)
            return $"an edge INSIDE doctrine rides a series shaped like a real family's, which a playbook can name, and '{edge.Series}' is the "
                   + "proving ground's bare signal, which no playbook names";
        return null;
    }
}

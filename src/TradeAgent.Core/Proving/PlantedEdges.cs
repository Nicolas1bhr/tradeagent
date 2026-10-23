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

/// <summary>
/// WHAT A PLANTED RELATION IS MEANT TO TEACH (<c>U-proving-ground-b</c> item 3; <c>docs/VISION.md</c> § 6.4: "traps — leaky
/// joins, edges that exist only before fees, regimes that exist only in-sample"). An honest app finds no edge in any trap.
/// </summary>
public enum EdgeTrap
{
    /// <summary>A real planted edge.</summary>
    None,

    /// <summary>Each reading is first seen after the move it predicts: a source-time join sees it, the as-of gate never can.</summary>
    LeakyJoin,

    /// <summary>
    /// Twice the largest move any one signal plants, summed over every pre-fee trap of its market, is below the venue's round
    /// trip at every price the market reaches, so no selection or combination of the market's pre-fee signals clears the
    /// venue's costs: a frictionless run passes, the venue's cost fails it.
    /// </summary>
    PreFeeOnly,

    /// <summary>On before the holdout cutoff and off from it: research passes, the verdict over the held-back months fails.</summary>
    InSampleOnly
}

/// <summary>
/// THE HOLDOUT A PROVING MARKET IS JUDGED ON, SET AS THE OWNER WOULD SET IT (<c>U-proving-ground-b</c> items 3 and 4): every
/// minute from day <see cref="FromDay"/> on, and the campaign's trial and verdict budgets.
/// </summary>
public sealed record SyntheticHoldout(int FromDay, int TrialBudget, int VerdictBudget);

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

    /// <summary>The trap the relation is, or <see cref="EdgeTrap.None"/> for a real planted edge.</summary>
    public EdgeTrap Trap { get; init; } = EdgeTrap.None;

    /// <summary>
    /// For a <see cref="EdgeTrap.LeakyJoin"/>: how long after its source time each reading of its series is first seen — after
    /// the end of the move it predicts. Every other series' reading is first seen <see cref="ProvingSources.Delay"/> after it.
    /// </summary>
    public TimeSpan FirstSeenAfter { get; init; }

    /// <summary>A leaky join on <paramref name="series"/>: each reading first seen <paramref name="firstSeenAfter"/> after its source time.</summary>
    public static SyntheticEdge LeakyJoin(long betaTicks, TimeSpan firstSeenAfter, string series = ProvingSources.Signal) =>
        new(betaTicks) { Series = series, Trap = EdgeTrap.LeakyJoin, FirstSeenAfter = firstSeenAfter };

    /// <summary>An edge that exists only before fees: twice its largest move per signal, with every other pre-fee trap's of its market, must be below the venue's round trip.</summary>
    public static SyntheticEdge PreFeeOnly(long betaTicks, string series = ProvingSources.Signal) =>
        new(betaTicks) { Series = series, Trap = EdgeTrap.PreFeeOnly };

    /// <summary>An edge that exists only in-sample: on before the market's holdout cutoff, off from it.</summary>
    public static SyntheticEdge InSampleOnly(long betaTicks, string series = ProvingSources.Signal) =>
        new(betaTicks) { Series = series, Trap = EdgeTrap.InSampleOnly };

    /// <summary>The edge as one canonical line: what names a market that carries it.</summary>
    internal string Canonical =>
        string.Create(CultureInfo.InvariantCulture,
            $"{BetaTicks}/{HorizonMinutes}/{Series}/{HalfLifeDays?.ToString(CultureInfo.InvariantCulture) ?? "-"}/{string.Join(",", Regimes.Select(r => $"{r.FromDay}-{r.ToDay}"))}/{Doctrine}")
        + (Trap == EdgeTrap.None ? "" : string.Create(CultureInfo.InvariantCulture, $"/{Trap}/{(long)FirstSeenAfter.TotalSeconds}"));
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
/// <param name="SharpeBeforeCost">The reference trade's annualised Sharpe before costs: its mean over its whole standard deviation, the planted move's spread from reading to reading and the noise's.</param>
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

    /// <summary>
    /// THE EDGE AS IT IS PLANTED: an in-sample trap is on from the market's start to its holdout cutoff and off from it; every
    /// other edge as its spec says.
    /// </summary>
    internal static SyntheticEdge Effective(SyntheticMarketSpec spec, SyntheticEdge edge) =>
        edge.Trap == EdgeTrap.InSampleOnly && spec.Holdout is { } holdout
            ? edge with { Regimes = [new SyntheticRegime(0, holdout.FromDay)] }
            : edge;

    /// <summary>
    /// HOW LONG AFTER ITS SOURCE TIME A READING OF <paramref name="series"/> IS FIRST SEEN: a leaky join's offset for the series
    /// it rides — after the move each reading predicts — and the declared delay for every other.
    /// </summary>
    internal static TimeSpan FirstSeenAfter(SyntheticMarketSpec spec, string series) =>
        spec.Planted.FirstOrDefault(e => e.Trap == EdgeTrap.LeakyJoin && e.Series == series) is { } leak
            ? leak.FirstSeenAfter
            : ProvingSources.Delay;

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
        foreach (var edge in spec.Planted.Select(e => Effective(spec, e)))
        {
            // A LEAKY JOIN'S MOVE starts where an honest edge's would, from the first hourly close after the reading's source
            // time and its declared delay: what makes it a leak is only that the reading is first seen after it.
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

    /// <summary>
    /// THE MOST ANY POLICY CAN GAIN, IN EXPECTATION AND IN TICKS, PER ROUND TRIP ON <paramref name="edge"/>: 2·R·|β|·w, twice
    /// its largest move per signal — R the largest reading, w its largest weight over the market. Not the average signal's
    /// move: a policy chooses which signals it holds through, and once in, holding through another reading costs no further
    /// fill. In at the largest reading and out only at the smallest, it gains R·|β|·w on the first and, over the 2R readings it
    /// expects to hold through, ½·|β|·w on each — 2·R·|β|·w a round trip; a value iteration over every in/out (and short)
    /// policy on readings drawn uniformly from −R to R breaks even at exactly this cost and no higher. A trap whose round trip
    /// is above it at every price its market reaches has no selection of its signals that clears the venue's costs.
    /// </summary>
    internal static decimal MostPerRoundTrip(SyntheticMarketSpec spec, SyntheticEdge planted)
    {
        var edge = Effective(spec, planted);
        var shape = ProvingSources.Shape(edge.Series)!;
        var hours = spec.Days * 24L;
        var largest = 0m;
        for (var k = 0; k < shape.Count(spec.Days); k++)
        {
            var hour = MoveHour(shape.SourceMinute(k), ProvingSources.Delay);
            if (hour < hours) largest = Math.Max(largest, Weight(edge, hour));
        }
        return 2m * SyntheticMarket.SignalRange * Math.Abs(edge.BetaTicks) * largest;
    }

    /// <summary>
    /// THE MOST ANY POLICY CAN GAIN, IN EXPECTATION AND IN TICKS, PER ROUND TRIP ON EVERY PRE-FEE TRAP OF <paramref name="spec"/>
    /// TOGETHER: the sum of each one's <see cref="MostPerRoundTrip(SyntheticMarketSpec, SyntheticEdge)"/>. A program may read
    /// every series its market writes and hold one position through the moves of all of them, but the traps' readings are
    /// independent streams, so what it gains on each is bounded by that trap's own figure over the round trips its position
    /// makes as that trap's readings see it — never more than the round trips it pays for. The honest claim of a pre-fee trap
    /// is that no selection or combination of its market's pre-fee signals clears the venue's round trip; this is that bound.
    /// </summary>
    internal static decimal MostPerRoundTrip(SyntheticMarketSpec spec) =>
        spec.Planted.Where(e => e.Trap == EdgeTrap.PreFeeOnly).Sum(e => MostPerRoundTrip(spec, e));

    /// <summary>How many pre-fee traps <paramref name="spec"/> plants, in words for a refusal.</summary>
    static string PreFeeTraps(SyntheticMarketSpec spec) =>
        spec.Planted.Count(e => e.Trap == EdgeTrap.PreFeeOnly) is var n && n == 1
            ? "its one pre-fee trap, twice its largest move per signal,"
            : $"its {n} pre-fee traps together, the sum of twice each one's largest move per signal,";

    /// <summary>
    /// WHY THE PRE-FEE TRAPS IN <paramref name="spec"/> ARE NOT TRAPS AT THE PRICES ITS MARKET REACHED, in words — or null:
    /// the round trip is a share of the price and the planted move is in ticks, so the market's bound, every pre-fee trap's
    /// together, is checked again at the lowest price the generated market touches, before anything is written.
    /// </summary>
    internal static string? RefusalAtLowest(SyntheticMarketSpec spec, long lowestTicks)
    {
        if (RoundTripBps(spec.VenueId) is not { } trip) return null;
        if (!spec.Planted.Any(e => e.Trap == EdgeTrap.PreFeeOnly)) return null;
        var most = MostPerRoundTrip(spec) * 10_000m / Math.Max(1L, lowestTicks);
        if (most >= trip)
            return $"a pre-fee trap is below the venue's round trip at every price its market reaches, however its market's pre-fee signals "
                   + $"are chosen and combined: the most a policy can gain per round trip on {PreFeeTraps(spec)} is "
                   + $"{decimal.Round(most, 2, MidpointRounding.ToEven)} bps of the market's lowest price, {lowestTicks * SyntheticMarket.TickSize}, "
                   + $"not below {decimal.Round(trip, 2, MidpointRounding.ToEven)} bps — it is a real edge, not a trap";
        return null;
    }

    /// <summary>E[z²] for a reading drawn uniformly from −R to R: R(R + 1)/3.</summary>
    internal static decimal MeanSquareValue => SyntheticMarket.SignalRange * (SyntheticMarket.SignalRange + 1m) / 3m;

    /// <summary>The round trip at <paramref name="venueId"/>, in basis points: two fills at its published taker fee and the app's slippage.</summary>
    internal static decimal? RoundTripBps(string venueId) =>
        VenueCostModel.FeeOf(venueId) is { } fee ? 2m * (fee.TakerRate + VenueCostModel.SlippageRate) * 10_000m : null;

    /// <summary>What <paramref name="edge"/> is worth in <paramref name="spec"/>'s market — see <see cref="PlantedFigures"/>.</summary>
    internal static PlantedFigures Figures(SyntheticMarketSpec spec, SyntheticEdge planted)
    {
        var edge = Effective(spec, planted);
        var shape = ProvingSources.Shape(edge.Series)!;
        var hours = spec.Days * 24L;
        var readings = 0;
        var weights = 0m;
        var squares = 0m;
        for (var k = 0; k < shape.Count(spec.Days); k++)
        {
            var hour = MoveHour(shape.SourceMinute(k), ProvingSources.Delay);
            if (hour >= hours) continue;
            readings++;
            var w = Weight(edge, hour);
            weights += w;
            squares += w * w;
        }

        var mean = readings == 0 ? 0m : weights / readings;
        var meanSquare = readings == 0 ? 0m : squares / readings;
        var gross = Math.Abs(edge.BetaTicks) * MeanAbsoluteValue * mean;

        // THE REFERENCE TRADE'S WHOLE VARIANCE: its P&L is |β|·|z|·w plus the horizon's noise, z and w independent, so
        // Var = β²·(E[z²]·E[w²] − (E|z|·w̄)²) + the noise's — the planted move varies from reading to reading too.
        var spread = edge.BetaTicks * edge.BetaTicks * (MeanSquareValue * meanSquare - MeanAbsoluteValue * MeanAbsoluteValue * mean * mean);
        var sigma = Sqrt(Math.Max(0m, spread) + MinuteVariance * edge.HorizonMinutes);
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
        if (edge.Trap != EdgeTrap.LeakyJoin && edge.FirstSeenAfter != TimeSpan.Zero)
            return "only a leaky join names when its readings are first seen; every other series is first seen its declared delay after its source time";

        switch (edge.Trap)
        {
            case EdgeTrap.LeakyJoin:
            {
                if (spec.Planted.Count(e => e.Series == edge.Series) > 1)
                    return $"a leaky join's series is its own: every reading of '{edge.Series}' is first seen late, and another edge rides it too";
                // FIRST SEEN AFTER THE MOVE IT PREDICTS: the move starts at the first hourly close after the source time and its
                // declared delay, and lasts the horizon.
                var source = shape.SourceMinute(0);
                var end = TimeSpan.FromMinutes(MoveHour(source, ProvingSources.Delay) * 60 + edge.HorizonMinutes - source);
                if (edge.FirstSeenAfter < end)
                    return $"a leaky join's readings are first seen after the move they predict, {end.TotalMinutes} minutes after their source time "
                           + $"on '{edge.Series}' with a {edge.HorizonMinutes}-minute horizon, and these are first seen {edge.FirstSeenAfter.TotalMinutes} minutes after it";
                break;
            }
            case EdgeTrap.PreFeeOnly:
            {
                if (RoundTripBps(spec.VenueId) is not { } trip)
                    return $"a pre-fee trap is below the venue's round trip, and this build has read no published fee for {spec.VenueId}";
                if (spec.Planted.Count(e => e.Series == edge.Series) > 1)
                    return $"a pre-fee trap's series is its own: another relation on '{edge.Series}' would add its move to the trap's on the same "
                           + "reading, and the sum is not bounded below the round trip";
                // NO SELECTION OR COMBINATION OF THE MARKET'S PRE-FEE SIGNALS CLEARS THE ROUND TRIP — not the average signal and not
                // one trap at a time, the best any policy reading all of them can do: see MostPerRoundTrip(spec).
                var most = MostPerRoundTrip(spec) * 10_000m / SyntheticMarket.StartTicks;
                if (most >= trip)
                    return $"a pre-fee trap is below the venue's round trip however its market's pre-fee signals are chosen and combined: the "
                           + $"most a policy can gain per round trip on {PreFeeTraps(spec)} is "
                           + $"{decimal.Round(most, 2, MidpointRounding.ToEven)} bps of the starting price, not below "
                           + $"{decimal.Round(trip, 2, MidpointRounding.ToEven)} bps — it is a real edge, not a trap";
                break;
            }
            case EdgeTrap.InSampleOnly:
                if (spec.Holdout is null)
                    return "an in-sample trap is on before the market's holdout cutoff and off from it, and this market sets no holdout";
                if (edge.Regimes.Count > 0)
                    return "an in-sample trap's regime is the market's holdout cutoff and nothing else; it names no windows of its own";
                break;
        }
        return null;
    }
}

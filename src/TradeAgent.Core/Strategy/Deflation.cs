namespace TradeAgent.Core.Strategy;

/// <summary>
/// THE STANDARD NORMAL DISTRIBUTION, in double precision: <see cref="Cdf"/> is Hart's algorithm 5666 as West (2005) gives it,
/// accurate to double precision, and <see cref="Quantile"/> is Acklam's rational approximation polished by one Halley step
/// against <see cref="Cdf"/>. Pure; nothing here reads a clock, a file or a table.
/// </summary>
public static class StandardNormal
{
    /// <summary>Φ(x): the probability a standard normal draw is at most <paramref name="x"/>.</summary>
    public static double Cdf(double x)
    {
        if (double.IsNaN(x)) return double.NaN;
        var z = Math.Abs(x);
        double tail;
        if (z > 37) tail = 0;
        else
        {
            var e = Math.Exp(-z * z / 2);
            if (z < 7.07106781186547)
            {
                var n = (((((3.52624965998911E-02 * z + 0.700383064443688) * z + 6.37396220353165) * z
                    + 33.912866078383) * z + 112.079291497871) * z + 221.213596169931) * z + 220.206867912376;
                var d = ((((((8.83883476483184E-02 * z + 1.75566716318264) * z + 16.064177579207) * z
                    + 86.7807322029461) * z + 296.564248779674) * z + 637.333633378831) * z + 793.826512519948) * z
                    + 440.413735824752;
                tail = e * n / d;
            }
            else
            {
                var f = z + 1 / (z + 2 / (z + 3 / (z + 4 / (z + 0.65))));
                tail = e / (f * 2.506628274631);
            }
        }

        return x <= 0 ? tail : 1 - tail;
    }

    /// <summary>Φ⁻¹(p) for 0 &lt; p &lt; 1; −∞ at 0 and +∞ at 1.</summary>
    public static double Quantile(double p)
    {
        if (double.IsNaN(p) || p < 0 || p > 1) throw new ArgumentOutOfRangeException(nameof(p), p, "a probability is 0 to 1");
        if (p == 0) return double.NegativeInfinity;
        if (p == 1) return double.PositiveInfinity;

        const double low = 0.02425;
        double x;
        if (p < low)
        {
            var q = Math.Sqrt(-2 * Math.Log(p));
            x = (((((C0 * q + C1) * q + C2) * q + C3) * q + C4) * q + C5) / ((((D0 * q + D1) * q + D2) * q + D3) * q + 1);
        }
        else if (p <= 1 - low)
        {
            var q = p - 0.5;
            var r = q * q;
            x = (((((A0 * r + A1) * r + A2) * r + A3) * r + A4) * r + A5) * q
                / (((((B0 * r + B1) * r + B2) * r + B3) * r + B4) * r + 1);
        }
        else
        {
            var q = Math.Sqrt(-2 * Math.Log(1 - p));
            x = -(((((C0 * q + C1) * q + C2) * q + C3) * q + C4) * q + C5) / ((((D0 * q + D1) * q + D2) * q + D3) * q + 1);
        }

        // ONE HALLEY STEP against the double-precision CDF.
        var err = Cdf(x) - p;
        var u = err * Math.Sqrt(2 * Math.PI) * Math.Exp(x * x / 2);
        return x - u / (1 + x * u / 2);
    }

    const double A0 = -3.969683028665376e+01, A1 = 2.209460984245205e+02, A2 = -2.759285104469687e+02,
        A3 = 1.383577518672690e+02, A4 = -3.066479806614716e+01, A5 = 2.506628277459239e+00;
    const double B0 = -5.447609879822406e+01, B1 = 1.615858368580409e+02, B2 = -1.556989798598866e+02,
        B3 = 6.680131188771972e+01, B4 = -1.328068155288572e+01;
    const double C0 = -7.784894002430293e-03, C1 = -3.223964580411365e-01, C2 = -2.400758277161838e+00,
        C3 = -2.549732539343734e+00, C4 = 4.374664141464968e+00, C5 = 2.938163982698783e+00;
    const double D0 = 7.784695709041462e-03, D1 = 3.224671290700398e-01, D2 = 2.445134137142996e+00,
        D3 = 3.754408661907416e+00;
}

/// <summary>
/// A STREAM'S SAMPLE MOMENTS over its KNOWN days (a day whose net return is null is no observation): <see cref="T"/> the
/// count, <see cref="Sr"/> the per-day Sharpe ratio (mean over the population standard deviation, not annualised),
/// <see cref="Skew"/> and <see cref="Kurtosis"/> the population skewness and RAW kurtosis (a normal's is 3) — the inputs
/// of the probabilistic Sharpe ratio (<c>docs/research/2026-10-02/R04-evolution-statistics.md</c> § 2.1).
/// </summary>
public sealed record StreamMoments(int T, double Sr, double Skew, double Kurtosis);

/// <summary>One trial of a lineage as E3 reads it: its run, and its 1× daily returns — or null, because it has no stream.</summary>
public sealed record TrialStream(string RunId, IReadOnlyList<DailyReturn>? Days);

/// <summary>
/// HOW A LINEAGE'S TRIALS CLUSTERED: <see cref="ClusterOf"/> gives each trial's cluster (0-based, in the order the trials
/// were handed in), <see cref="NEff"/> how many clusters there are, and <see cref="RepresentativeSr"/> each cluster's
/// first member's per-day Sharpe ratio — null where that member has no stream, or one with no measurable ratio.
/// </summary>
public sealed record Clustering(IReadOnlyList<int> ClusterOf, int NEff, IReadOnlyList<double?> RepresentativeSr);

/// <summary>
/// E3 OVER ONE CANDIDATE: <see cref="Candidate"/> its moments (null when it has fewer than two known days or no
/// variation), <see cref="M"/> the trials, <see cref="Unstreamed"/> how many of them had no stream, <see cref="NEff"/>
/// the clusters, <see cref="Sr0"/> the deflated per-day hurdle, <see cref="Dsr"/> the deflated Sharpe ratio and
/// <see cref="Ceiling"/> the noise ceiling (<see cref="Sr0"/> annualised over 365 days). An unknown figure is null —
/// never 0.
/// </summary>
public sealed record Deflated(StreamMoments? Candidate, int Days, int M, int Unstreamed, int NEff, double? Sr0, double? Dsr,
    double? Ceiling);

/// <summary>
/// E3 — THE DEFLATED SHARPE RATIO OVER A LINEAGE'S TRIALS (<c>docs/EDGE-FACTORY.md</c> § 4.5; R04 § 2.1-2.2 and :254,
/// :300), version <see cref="Version"/>. Pure: what it is handed is all it reads.
///
/// <para><b>The rule.</b> Trials cluster greedily, in the order handed in (registration, then run id): a trial with a
/// stream joins the FIRST cluster every member of which it correlates with at |ρ| ≥ <see cref="MinCorrelation"/> over
/// at least <see cref="MinSharedDays"/> shared known days, else it starts a new one; a trial with no stream clusters
/// alone and is never joined. N_eff is the number of clusters. SR0 = √V[the representatives' SR]·E[max_N_eff]
/// (<see cref="ExpectedMax"/>); with N_eff ≤ 1 it is 0, and with fewer than two representatives that have a ratio V is
/// the null's, 1/(T−1). DSR = PSR(SR0) with the candidate's skew, raw kurtosis and T (<see cref="Psr"/>).</para>
/// </summary>
public static class Deflation
{
    /// <summary>The rule's version. A changed rule is a new number on new rows, never this one meaning something else.</summary>
    public const int Version = 1;

    /// <summary>|ρ| at or above which two trials are one bet (R04 :254).</summary>
    public const double MinCorrelation = 0.7;

    /// <summary>The fewest shared known days a correlation is read over; fewer and the two are not joined.</summary>
    public const int MinSharedDays = 30;

    /// <summary>The Euler–Mascheroni constant.</summary>
    public const double EulerGamma = 0.5772156649015329;

    /// <summary>Days a year, as the R10 power model counts them (crypto trades every day).</summary>
    public const int PeriodsPerYear = 365;

    /// <summary>
    /// E[max of N standard normal draws] ≈ (1−γ)·Φ⁻¹(1−1/N) + γ·Φ⁻¹(1−1/(N·e)) (R04 :70). 0 for N ≤ 1: one trial is
    /// no selection.
    /// </summary>
    public static double ExpectedMax(int n) => n <= 1
        ? 0
        : (1 - EulerGamma) * StandardNormal.Quantile(1 - 1.0 / n) + EulerGamma * StandardNormal.Quantile(1 - 1.0 / (n * Math.E));

    /// <summary>
    /// PSR(SR*) = Φ[(SR − SR*)·√(T−1) / √(1 − γ3·SR + ((γ4−1)/4)·SR²)] (R04 :64), per-period ratios and RAW kurtosis.
    /// Null — not a number — for T &lt; 2 or a variance term that is not above zero.
    /// </summary>
    public static double? Psr(double sr, double srStar, int t, double skew, double kurtosis)
    {
        if (t < 2) return null;
        var v = 1 - skew * sr + (kurtosis - 1) / 4 * sr * sr;
        if (!(v > 0) || double.IsInfinity(v)) return null;
        return StandardNormal.Cdf((sr - srStar) * Math.Sqrt(t - 1) / Math.Sqrt(v));
    }

    /// <summary>The known days' net returns, in date order.</summary>
    public static IReadOnlyList<double> Known(IReadOnlyList<DailyReturn> days) =>
        [.. days.Where(d => d.NetReturn is not null).OrderBy(d => d.Day).Select(d => (double)d.NetReturn!.Value)];

    /// <summary>The moments of a stream's known days, or null with fewer than two of them or no variation.</summary>
    public static StreamMoments? MomentsOf(IReadOnlyList<DailyReturn> days)
    {
        var x = Known(days);
        if (x.Count < 2) return null;
        var mean = x.Average();
        double m2 = 0, m3 = 0, m4 = 0;
        foreach (var v in x)
        {
            var d = v - mean;
            m2 += d * d;
            m3 += d * d * d;
            m4 += d * d * d * d;
        }
        m2 /= x.Count; m3 /= x.Count; m4 /= x.Count;
        if (!(m2 > 0)) return null;
        var sd = Math.Sqrt(m2);
        return new StreamMoments(x.Count, mean / sd, m3 / (m2 * sd), m4 / (m2 * m2));
    }

    /// <summary>
    /// Pearson's ρ over the days both streams KNOW, with how many those are; ρ null where either has no variation over
    /// them or they share fewer than two.
    /// </summary>
    public static (double? Rho, int Shared) Correlation(IReadOnlyList<DailyReturn> a, IReadOnlyList<DailyReturn> b)
    {
        var right = new Dictionary<DateOnly, double>();
        foreach (var d in b)
            if (d.NetReturn is { } r) right[d.Day] = (double)r;

        var xs = new List<double>();
        var ys = new List<double>();
        foreach (var d in a)
            if (d.NetReturn is { } r && right.TryGetValue(d.Day, out var y))
            {
                xs.Add((double)r);
                ys.Add(y);
            }

        if (xs.Count < 2) return (null, xs.Count);
        double mx = xs.Average(), my = ys.Average(), sxy = 0, sxx = 0, syy = 0;
        for (var i = 0; i < xs.Count; i++)
        {
            sxy += (xs[i] - mx) * (ys[i] - my);
            sxx += (xs[i] - mx) * (xs[i] - mx);
            syy += (ys[i] - my) * (ys[i] - my);
        }
        return sxx > 0 && syy > 0 ? (sxy / Math.Sqrt(sxx * syy), xs.Count) : (null, xs.Count);
    }

    /// <summary>The lineage's clusters. See the type's summary for the rule.</summary>
    public static Clustering Cluster(IReadOnlyList<TrialStream> trials)
    {
        ArgumentNullException.ThrowIfNull(trials);
        var clusterOf = new int[trials.Count];
        var members = new List<List<int>>();
        var joinable = new List<bool>();

        for (var i = 0; i < trials.Count; i++)
        {
            var days = trials[i].Days;
            var home = -1;
            if (days is not null)
                for (var c = 0; c < members.Count && home < 0; c++)
                    if (joinable[c] && members[c].All(j => Correlated(days, trials[j].Days!)))
                        home = c;

            if (home < 0)
            {
                members.Add([i]);
                joinable.Add(days is not null);
                home = members.Count - 1;
            }
            else members[home].Add(i);

            clusterOf[i] = home;
        }

        var representatives = members
            .Select(m => trials[m[0]].Days is { } d ? MomentsOf(d)?.Sr : null)
            .ToList();
        return new Clustering(clusterOf, members.Count, representatives);
    }

    static bool Correlated(IReadOnlyList<DailyReturn> a, IReadOnlyList<DailyReturn> b)
    {
        var (rho, shared) = Correlation(a, b);
        return shared >= MinSharedDays && rho is { } r && Math.Abs(r) >= MinCorrelation;
    }

    /// <summary>
    /// SR0 = √V·E[max_N_eff], per day: 0 for N_eff ≤ 1; V the sample variance of the representatives' ratios, or the
    /// null's 1/(T−1) with fewer than two of them. Null where T &lt; 2 leaves the null's variance undefined.
    /// </summary>
    public static double? Sr0(IReadOnlyList<double?> representativeSr, int nEff, int t)
    {
        if (nEff <= 1) return 0;
        var srs = representativeSr.Where(s => s is not null).Select(s => s!.Value).ToList();
        double v;
        if (srs.Count >= 2)
        {
            var mean = srs.Average();
            v = srs.Sum(s => (s - mean) * (s - mean)) / (srs.Count - 1);
        }
        else
        {
            if (t < 2) return null;
            v = 1.0 / (t - 1);
        }
        return Math.Sqrt(v) * ExpectedMax(nEff);
    }

    /// <summary>E3 for one candidate stream over its lineage's trials (the candidate's run among them).</summary>
    public static Deflated Of(IReadOnlyList<DailyReturn> candidate, IReadOnlyList<TrialStream> trials)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(trials);
        var clusters = Cluster(trials);
        var moments = MomentsOf(candidate);
        var days = Known(candidate).Count;
        var sr0 = Sr0(clusters.RepresentativeSr, clusters.NEff, days);
        double? dsr = moments is { } m && sr0 is { } s ? Psr(m.Sr, s, m.T, m.Skew, m.Kurtosis) : null;
        return new Deflated(moments, days, trials.Count, trials.Count(t => t.Days is null), clusters.NEff, sr0, dsr,
            sr0 is { } h ? h * Math.Sqrt(PeriodsPerYear) : null);
    }
}

/// <summary>
/// E2'S SUB-PERIOD BLOCKS (<c>docs/EDGE-FACTORY.md</c> § 4.5; R04 :254), version <see cref="Version"/>: a stream's KNOWN
/// days, in date order, cut into <see cref="Count"/> equal consecutive blocks (block i holds known days ⌊i·n/8⌋ to
/// ⌊(i+1)·n/8⌋ − 1); a block is positive when its compounded net return is above zero. A day with no bar is no day of any
/// block, and a block holding no known day is unknown — never positive. Passing is ≥ <see cref="PassAt"/> positive.
/// </summary>
public static class BlockTest
{
    /// <summary>The rule's version.</summary>
    public const int Version = 1;

    /// <summary>How many blocks.</summary>
    public const int Count = 8;

    /// <summary>How many positive blocks pass.</summary>
    public const int PassAt = 6;

    /// <summary>The known days, how many blocks hold one, and how many of those are positive.</summary>
    public static (int Days, int Known, int Positive) Of(IReadOnlyList<DailyReturn> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        var x = Deflation.Known(days);
        int known = 0, positive = 0;
        for (var i = 0; i < Count; i++)
        {
            var from = i * x.Count / Count;
            var to = (i + 1) * x.Count / Count;
            if (to <= from) continue;
            known++;
            var growth = 1.0;
            for (var j = from; j < to; j++) growth *= 1 + x[j];
            if (growth > 1) positive++;
        }
        return (x.Count, known, positive);
    }

    /// <summary>Whether the blocks pass — which no build enforces yet (<see cref="GatePower"/>).</summary>
    public static bool Passes(int positive) => positive >= PassAt;
}

/// <summary>The words a gate's status is recorded and shown in.</summary>
public static class GateStatus
{
    /// <summary>The gate cannot discriminate at this depth, whatever its statistic says.</summary>
    public const string Inconclusive = "inconclusive";

    /// <summary>The gate could discriminate here; this build shows it and enforces nothing.</summary>
    public const string Enforceable = "enforceable — not enforced by this build";

    /// <summary>The version had no research stream in its lineage to measure.</summary>
    public const string NoStream = "no research stream";
}

/// <summary>
/// THE POWER OF THE REFEREE'S OWN GATES (<c>docs/EDGE-FACTORY.md</c>:211-212), version <see cref="Version"/>, under
/// R10-calc's model (<c>docs/research/2026-10-02/R10-calc/cascade_power.py</c>): IID normal daily returns, 365 a year.
///
/// <para>A gate CAN DISCRIMINATE when P(pass | true annual SR <see cref="TargetSharpe"/>) − P(pass | SR 0) ≥
/// <see cref="Discriminates"/> at its measured T and N_eff (seat A's ruling, 2026-10-11). Below that its status is
/// <see cref="GateStatus.Inconclusive"/> whatever its statistic; at or above it <see cref="GateStatus.Enforceable"/>.
/// An unknown statistic is inconclusive at any depth: nothing absent is a pass.</para>
/// </summary>
public static class GatePower
{
    /// <summary>The model's version.</summary>
    public const int Version = 1;

    /// <summary>The true annual Sharpe ratio a gate must tell from zero.</summary>
    public const double TargetSharpe = 2.0;

    /// <summary>The least difference in pass probability that counts as discriminating.</summary>
    public const double Discriminates = 0.50;

    /// <summary>The DSR a candidate passes at.</summary>
    public const double DsrPassAt = 0.95;

    const int Q = Deflation.PeriodsPerYear;

    /// <summary>P(≥ 6 of 8 blocks positive | true annual SR) over <paramref name="days"/> days; 0 under eight days.</summary>
    public static double BlocksPass(int days, double annualSr)
    {
        if (days < BlockTest.Count) return 0;
        var p = StandardNormal.Cdf(annualSr * Math.Sqrt(days / (double)BlockTest.Count / Q));
        double pass = 0;
        for (var k = BlockTest.PassAt; k <= BlockTest.Count; k++)
            pass += Choose(BlockTest.Count, k) * Math.Pow(p, k) * Math.Pow(1 - p, BlockTest.Count - k);
        return pass;
    }

    /// <summary>The annualised noise ceiling under the null, E[max_N_eff]·√(365/T); 0 for N_eff ≤ 1.</summary>
    public static double NullCeiling(int days, int nEff) => days < 1 ? double.PositiveInfinity : Deflation.ExpectedMax(nEff) * Math.Sqrt(Q / (double)days);

    /// <summary>The annual SR a candidate must show for DSR ≥ 0.95: the null ceiling plus z(0.95)·√(365/(T−1)).</summary>
    public static double DsrNeeded(int days, int nEff) =>
        NullCeiling(days, nEff) + StandardNormal.Quantile(DsrPassAt) * Math.Sqrt(Q / (double)(days - 1));

    /// <summary>P(DSR ≥ 0.95 | true annual SR) over <paramref name="days"/> days and <paramref name="nEff"/> clusters; 0 under two days.</summary>
    public static double DsrPass(int days, int nEff, double annualSr)
    {
        if (days < 2) return 0;
        return 1 - StandardNormal.Cdf((DsrNeeded(days, nEff) - annualSr) / Math.Sqrt(Q / (double)days));
    }

    /// <summary>The block gate's power at <paramref name="days"/>.</summary>
    public static double Blocks(int days) => BlocksPass(days, TargetSharpe) - BlocksPass(days, 0);

    /// <summary>The DSR gate's power at <paramref name="days"/> and <paramref name="nEff"/>.</summary>
    public static double Dsr(int days, int nEff) => DsrPass(days, nEff, TargetSharpe) - DsrPass(days, nEff, 0);

    /// <summary>
    /// A gate's status at its power: <see cref="GateStatus.Inconclusive"/> below <see cref="Discriminates"/> or with an
    /// unknown statistic, <see cref="GateStatus.Enforceable"/> otherwise. Whether the statistic passes does not enter —
    /// a gate that cannot discriminate passes nothing.
    /// </summary>
    public static string Status(double power, bool known) =>
        known && power >= Discriminates ? GateStatus.Enforceable : GateStatus.Inconclusive;

    /// <summary>The fewest days at which <paramref name="power"/> discriminates, searched up to <paramref name="limit"/>; null when none does.</summary>
    public static int? DaysToDiscriminate(Func<int, double> power, int limit = 10_000)
    {
        ArgumentNullException.ThrowIfNull(power);
        for (var d = 2; d <= limit; d++)
            if (power(d) >= Discriminates) return d;
        return null;
    }

    static double Choose(int n, int k)
    {
        double c = 1;
        for (var i = 1; i <= k; i++) c = c * (n - k + i) / i;
        return c;
    }
}

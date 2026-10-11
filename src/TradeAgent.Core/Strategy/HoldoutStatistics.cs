namespace TradeAgent.Core.Strategy;

/// <summary>
/// E4'S FIDELITY OF ONE HOLDOUT RUN, SEALED (<c>U-referee-v2c</c>; R04 :255). <see cref="Z"/> is null where an input is
/// unknown; <see cref="Se"/> is the holdout Sharpe ratio's standard error per day; <see cref="Status"/> is
/// <see cref="GateStatus.Inconclusive"/> while the figure cannot discriminate, else <see cref="GateStatus.Enforceable"/>.
/// </summary>
public sealed record FidelityResult(double? Z, double? Se, string Status);

/// <summary>
/// HOW FAR THE HOLDOUT'S SHARPE RATIO FELL FROM THE DEFLATED RESEARCH ONE — E4's fidelity z, computed and SEALED, never
/// a clause and never served (<c>docs/EDGE-FACTORY.md</c> § 4.5 E4; R04 :255, :64).
///
/// <para><b>The rule, version <see cref="Version"/>.</b> z = (SR_h − (SR_research − SR0)) / se_h, every ratio per day:
/// SR_research and SR0 are the verdict's own research measurement (<c>ResearchEvidenceRow.Sr</c>, <c>.Sr0</c>), SR_h the
/// holdout run's daily net returns' (<see cref="Deflation.MomentsOf"/>), and se_h = √((1 − γ3·SR_h + ((γ4−1)/4)·SR_h²) /
/// (T − 1)) — R04 :64's variance with the holdout's own skew and raw kurtosis, over its T known days. An unknown input, or
/// a variance term not above zero, is a null z — never 0.</para>
///
/// <para><b>Inconclusive while 2·se_h exceeds one annual Sharpe unit</b> (<see cref="MaxAnnualTwoSe"/>): at normal
/// moments that is every holdout under ≈ 1,460 known days, and 4.0 units at 91 (R10-calc's model,
/// <c>R10-plan-redteam.md</c>:26) — a z over a band that wide says nothing either way.</para>
/// </summary>
public static class Fidelity
{
    /// <summary>The rule's version. A changed rule is a new number.</summary>
    public const int Version = 1;

    /// <summary>The widest two standard errors, in annual Sharpe units, at which a z is read as discriminating.</summary>
    public const double MaxAnnualTwoSe = 1.0;

    /// <summary>The holdout Sharpe ratio's standard error per day, or null with fewer than two days or no positive variance term.</summary>
    public static double? Se(StreamMoments holdout)
    {
        ArgumentNullException.ThrowIfNull(holdout);
        if (holdout.T < 2) return null;
        var v = 1 - holdout.Skew * holdout.Sr + (holdout.Kurtosis - 1) / 4 * holdout.Sr * holdout.Sr;
        if (!(v > 0) || double.IsInfinity(v)) return null;
        return Math.Sqrt(v / (holdout.T - 1));
    }

    /// <summary>The fidelity of a holdout's moments against the research measurement's SR and SR0. See the type.</summary>
    public static FidelityResult Of(StreamMoments? holdout, double? researchSr, double? sr0)
    {
        if (holdout is null) return new FidelityResult(null, null, GateStatus.Inconclusive);
        var se = Se(holdout);
        if (se is not { } s || researchSr is not { } sr || sr0 is not { } floor)
            return new FidelityResult(null, se, GateStatus.Inconclusive);

        var z = (holdout.Sr - (sr - floor)) / s;
        if (!double.IsFinite(z)) return new FidelityResult(null, se, GateStatus.Inconclusive);
        var wide = 2 * s * Math.Sqrt(Deflation.PeriodsPerYear) > MaxAnnualTwoSe;
        return new FidelityResult(z, se, wide ? GateStatus.Inconclusive : GateStatus.Enforceable);
    }
}

/// <summary>
/// E4'S BETTING E-VALUE OVER A HOLDOUT'S DAILY NET RETURNS — computed and SEALED, never a clause and never served
/// (<c>docs/EDGE-FACTORY.md</c> § 4.5 E4; R04 :255, :302).
///
/// <para><b>The rule, version <see cref="Version"/>.</b> E_h = Π (1 + λ_t·r_t) over the holdout's KNOWN days in date
/// order — an unknown day bets nothing and is not a zero return — with λ_t = clip(plug-in Kelly / 2, 0,
/// <see cref="MaxBet"/>) and the plug-in Kelly the mean over the variance of the known returns BEFORE day t (none until
/// two that differ, so the first bets are 0). λ_t ≥ 0 bets against the null "the mean daily return is not above zero",
/// under which each factor's expectation is at most 1, so E_h is an e-value. A factor that is not above zero (a return
/// under −1/λ_t) ends the product at 0. No known day is a null E_h — never 1 and never 0.</para>
/// </summary>
public static class BettingE
{
    /// <summary>The rule's version. A changed rule is a new number.</summary>
    public const int Version = 1;

    /// <summary>The largest fraction bet on one day (R04 :302).</summary>
    public const double MaxBet = 0.5;

    /// <summary>The e-value of one holdout's days. See the type.</summary>
    public static double? Of(IReadOnlyList<DailyReturn> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        var x = Deflation.Known(days);
        if (x.Count == 0) return null;

        double e = 1, sum = 0, sumSq = 0;
        for (var t = 0; t < x.Count; t++)
        {
            var bet = 0.0;
            if (t >= 2)
            {
                var mean = sum / t;
                var variance = sumSq / t - mean * mean;
                if (variance > 0 && Varies(x, t)) bet = Math.Clamp(mean / variance / 2, 0, MaxBet);
            }

            e *= Math.Max(0, 1 + bet * x[t]);
            sum += x[t];
            sumSq += x[t] * x[t];
        }

        return double.IsFinite(e) ? e : null;
    }

    /// <summary>Whether the first <paramref name="n"/> values hold two that differ — a variance made of rounding is none.</summary>
    static bool Varies(IReadOnlyList<double> x, int n)
    {
        for (var i = 1; i < n; i++)
            if (x[i] != x[0]) return true;
        return false;
    }
}

/// <summary>
/// ONE VERDICT'S STEP OF THE α-WEALTH: its α_j, the wealth before and after it, and how the test went
/// (<see cref="AlphaWealth"/>'s statuses).
/// </summary>
public sealed record AddisStep(double AlphaJ, double WealthBefore, double WealthAfter, string Status);

/// <summary>
/// THE HOLDOUT'S α-WEALTH — ADDIS (Tian &amp; Ramdas 2019) over every final judgement a campaign's months have spent, in
/// charge order, computed and SEALED, gating nothing (<c>docs/EDGE-FACTORY.md</c> § 4.5 E4, "α-wealth per lineage";
/// R04 :255, :261-266).
///
/// <para><b>The constants, version <see cref="Version"/></b> — R04's app defaults: α_paper <see cref="Alpha"/> 0.25,
/// W0 = α/2, candidate threshold λ <see cref="Lambda"/> 0.25, discarding threshold τ <see cref="Tau"/> 0.5, and
/// γ_j = j^−1.6 / ζ(1.6) (<see cref="Gamma"/>). α1 = (τ − λ)·W0·γ1 ≈ 0.0137, R04 :261's "≈ 0.014".</para>
///
/// <para><b>The rule.</b> With the history of earlier verdicts — each selected (not discarded), a candidate or rejected
/// — S the selected, C0 the candidates, κ_k the k-th rejection, κ*_k the selected up to it and C_k+ the candidates after
/// it: α_t = min(λ, (τ − λ)·[W0·γ(1 + S − C0) + (α − W0)·γ(1 + S − κ*_1 − C_1+) + α·Σ_{k≥2} γ(1 + S − κ*_k − C_k+)]).
/// A verdict then tests p_h ≤ α_t ONLY over a holdout of <see cref="MinHoldoutDays"/> known days or more (R04 :255);
/// otherwise, or with no p_h, it is <see cref="Inconclusive"/> and spends α_t anyway — read as selected, not a candidate,
/// not rejected. So is a verdict with no recorded step (before rung 35, or stopped before its write): the reading that
/// only lowers every later α.</para>
///
/// <para><b>Wealth</b> is the GAI ledger ADDIS keeps under its FDR bound: W0, plus (α − W0) at the first rejection and α
/// at each later one, less α_i/(τ − λ) for each verdict that spent — selected and not a candidate. It never goes below
/// zero: each spend is W0·γ at an index no earlier spend took.</para>
/// </summary>
public static class AlphaWealth
{
    /// <summary>The rule's version. A changed rule or constant is a new number.</summary>
    public const int Version = 1;

    /// <summary>α_paper: the holdout's FDR currency (R04 :297).</summary>
    public const double Alpha = 0.25;

    /// <summary>W0, the starting wealth: α/2.</summary>
    public const double W0 = Alpha / 2;

    /// <summary>λ, the candidate threshold.</summary>
    public const double Lambda = 0.25;

    /// <summary>τ, the discarding threshold.</summary>
    public const double Tau = 0.5;

    /// <summary>The exponent of γ_j ∝ j^−s.</summary>
    public const double GammaExponent = 1.6;

    /// <summary>The shortest holdout, in known days, over which p_h ≤ α_j is tested (R04 :255, "a ≥ 12-month holdout").</summary>
    public const int MinHoldoutDays = 365;

    /// <summary>p_h ≤ α_j over a long-enough holdout.</summary>
    public const string Rejected = "rejected";

    /// <summary>α_j &lt; p_h ≤ λ: a candidate, not rejected; it spends nothing.</summary>
    public const string Candidate = "candidate";

    /// <summary>λ &lt; p_h ≤ τ: tested and not rejected; it spends α_j.</summary>
    public const string Tested = "tested";

    /// <summary>p_h &gt; τ: discarded as a conservative null; it spends nothing.</summary>
    public const string Discarded = "discarded";

    /// <summary>No p_h, or a holdout under <see cref="MinHoldoutDays"/>: not tested, and α_j is spent anyway.</summary>
    public const string Inconclusive = "inconclusive";

    static readonly double Zeta = ZetaOf(GammaExponent);

    /// <summary>γ_j = j^−1.6 / ζ(1.6), j ≥ 1; 0 below.</summary>
    public static double Gamma(int j) => j < 1 ? 0 : Math.Pow(j, -GammaExponent) / Zeta;

    /// <summary>ζ(s) by Euler–Maclaurin over the first thousand terms — exact to well past a double's last digit here.</summary>
    static double ZetaOf(double s)
    {
        const int n = 1000;
        double sum = 0;
        for (var j = 1; j < n; j++) sum += Math.Pow(j, -s);
        return sum + Math.Pow(n, 1 - s) / (s - 1) + Math.Pow(n, -s) / 2 + s * Math.Pow(n, -s - 1) / 12;
    }

    static bool Selected(string status) => status != Discarded;
    static bool IsCandidate(string status) => status is Candidate or Rejected;
    static bool Spends(string status) => status is Tested or Inconclusive;

    /// <summary>The α the next verdict is tested at, after <paramref name="history"/> (earlier verdicts' statuses, in charge order).</summary>
    public static double AlphaAfter(IReadOnlyList<string> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var s = history.Count(Selected);
        var c0 = history.Count(IsCandidate);

        var sum = W0 * Gamma(1 + s - c0);
        var rejections = 0;
        var selectedSoFar = 0;
        for (var i = 0; i < history.Count; i++)
        {
            if (Selected(history[i])) selectedSoFar++;
            if (history[i] != Rejected) continue;
            rejections++;
            var candidatesAfter = 0;
            for (var k = i + 1; k < history.Count; k++)
                if (IsCandidate(history[k])) candidatesAfter++;
            sum += (rejections == 1 ? Alpha - W0 : Alpha) * Gamma(1 + s - selectedSoFar - candidatesAfter);
        }

        return Math.Min(Lambda, (Tau - Lambda) * sum);
    }

    /// <summary>The wealth after <paramref name="history"/>. See the type.</summary>
    public static double WealthAfter(IReadOnlyList<string> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var wealth = W0;
        var rejections = 0;
        for (var i = 0; i < history.Count; i++)
        {
            if (Spends(history[i])) wealth -= AlphaAfter(Prefix(history, i)) / (Tau - Lambda);
            if (history[i] == Rejected) wealth += rejections++ == 0 ? Alpha - W0 : Alpha;
        }
        return wealth;
    }

    /// <summary>
    /// THE NEXT VERDICT'S STEP: its α_j after <paramref name="history"/>, how its <paramref name="p"/> tests over
    /// <paramref name="holdoutDays"/> known days, and the wealth before and after it.
    /// </summary>
    public static AddisStep Next(IReadOnlyList<string> history, double? p, int holdoutDays)
    {
        ArgumentNullException.ThrowIfNull(history);
        var alpha = AlphaAfter(history);
        var status = p is not { } pv || !double.IsFinite(pv) || holdoutDays < MinHoldoutDays ? Inconclusive
            : pv > Tau ? Discarded
            : pv <= alpha ? Rejected
            : pv <= Lambda ? Candidate
            : Tested;
        return new AddisStep(alpha, WealthAfter(history), WealthAfter([.. history, status]), status);
    }

    static IReadOnlyList<string> Prefix(IReadOnlyList<string> history, int count) => [.. history.Take(count)];
}

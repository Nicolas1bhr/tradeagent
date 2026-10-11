using TradeAgent.Core.Strategy;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// E4'S α-WEALTH (<c>U-referee-v2c</c>): ADDIS at R04's defaults, spent per verdict in charge order, tested only over a
/// year-long holdout (<c>docs/research/2026-10-02/R04-evolution-statistics.md</c>:255, :261-266).
/// </summary>
public class AlphaWealthTests
{
    /// <summary>
    /// (a) ADDIS SPENDS PER VERDICT, AND TESTS ONLY A YEAR-LONG HOLDOUT.
    ///
    /// <para>The first α is R04 :261's ≈ 0.014. A holdout of 364 known days is inconclusive whatever its p and spends its
    /// α anyway, as does a verdict with no p and one with no recorded step (read as tested, not rejected, not discarded);
    /// each spend lowers every later α. A 365-day holdout under α rejects, and a rejection raises the next α. A p over τ
    /// is discarded and spends nothing.</para>
    /// </summary>
    [Fact]
    public void Addis_spends_per_verdict_across_renewals_and_tests_only_a_year_long_holdout()
    {
        // γ is j^-1.6 normalised: γ1 = 1/ζ(1.6) = 0.43749… (onlineFDR's constant).
        Assert.Equal(0.4374901658, AlphaWealth.Gamma(1), 9);
        var sum = Enumerable.Range(1, 200_000).Sum(j => AlphaWealth.Gamma(j));
        Assert.InRange(sum, 0.999, 1.0);

        var alpha1 = AlphaWealth.AlphaAfter([]);
        Assert.Equal((AlphaWealth.Tau - AlphaWealth.Lambda) * AlphaWealth.W0 * AlphaWealth.Gamma(1), alpha1, 15);
        Assert.Equal(0.014, Math.Round(alpha1, 3));

        // A HOLDOUT ONE DAY SHORT OF A YEAR IS NOT TESTED, HOWEVER SMALL ITS p, AND SPENDS ITS α ANYWAY.
        var first = AlphaWealth.Next([], 1e-9, AlphaWealth.MinHoldoutDays - 1);
        Assert.Equal(AlphaWealth.Inconclusive, first.Status);
        Assert.Equal(alpha1, first.AlphaJ, 15);
        Assert.Equal(AlphaWealth.W0, first.WealthBefore, 15);
        Assert.Equal(AlphaWealth.W0 - alpha1 / (AlphaWealth.Tau - AlphaWealth.Lambda), first.WealthAfter, 15);

        // NO p IS INCONCLUSIVE TOO, AND A VERDICT WITH NO RECORDED STEP READS AS TESTED: both lower the next α alike.
        Assert.Equal(AlphaWealth.Inconclusive, AlphaWealth.Next([], null, 400).Status);
        var afterSpend = AlphaWealth.AlphaAfter([AlphaWealth.Inconclusive]);
        Assert.Equal(afterSpend, AlphaWealth.AlphaAfter([AlphaWealth.Tested]), 15);
        Assert.True(afterSpend < alpha1);
        Assert.Equal((AlphaWealth.Tau - AlphaWealth.Lambda) * AlphaWealth.W0 * AlphaWealth.Gamma(2), afterSpend, 15);

        // A DISCARDED p SPENDS NOTHING: the next α is the one before it.
        var discarded = AlphaWealth.Next([AlphaWealth.Inconclusive], 0.9, 400);
        Assert.Equal(AlphaWealth.Discarded, discarded.Status);
        Assert.Equal(discarded.WealthBefore, discarded.WealthAfter, 15);
        Assert.Equal(afterSpend, AlphaWealth.AlphaAfter([AlphaWealth.Inconclusive, AlphaWealth.Discarded]), 15);

        // A YEAR-LONG HOLDOUT UNDER α REJECTS, AND THE REJECTION RAISES WHAT COMES NEXT.
        var year = AlphaWealth.Next([AlphaWealth.Inconclusive], afterSpend / 2, AlphaWealth.MinHoldoutDays);
        Assert.Equal(AlphaWealth.Rejected, year.Status);
        Assert.Equal(year.WealthBefore + (AlphaWealth.Alpha - AlphaWealth.W0), year.WealthAfter, 15);
        Assert.True(AlphaWealth.AlphaAfter([AlphaWealth.Inconclusive, AlphaWealth.Rejected]) > afterSpend);

        // BETWEEN α AND λ IS A CANDIDATE (no spend); BETWEEN λ AND τ IS TESTED (a spend).
        Assert.Equal(AlphaWealth.Candidate, AlphaWealth.Next([], 0.2, 400).Status);
        var tested = AlphaWealth.Next([], 0.4, 400);
        Assert.Equal(AlphaWealth.Tested, tested.Status);
        Assert.True(tested.WealthAfter < tested.WealthBefore);

        // THE WEALTH NEVER GOES BELOW ZERO, however many verdicts spend.
        var many = Enumerable.Repeat(AlphaWealth.Inconclusive, 500).ToList();
        Assert.InRange(AlphaWealth.WealthAfter(many), 0, AlphaWealth.W0);
    }
}

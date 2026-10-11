using TradeAgent.Core.Strategy;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// E4'S SEALED FIDELITY AND BETTING E-VALUE (<c>U-referee-v2c</c> item 1): <see cref="Fidelity"/> and
/// <see cref="BettingE"/>, pure, over streams built here.
/// </summary>
public class FidelityTests
{
    static readonly DateOnly Day0 = new(2026, 1, 1);

    /// <summary>
    /// (b) A SHORT HOLDOUT'S z IS INCONCLUSIVE, A LONG ONE'S IS NOT, AND AN UNKNOWN INPUT IS NO z AT ALL.
    ///
    /// <para>At 91 known days two standard errors are ≈ 4.0 annual Sharpe units (R10-calc's model,
    /// <c>R10-plan-redteam.md</c>:26), four times the one unit a z must resolve; at 1,500 they are under one. The z itself
    /// is the rule's arithmetic in both cases — it is computed and sealed either way — and only its status differs.</para>
    /// </summary>
    [Fact]
    public void A_short_holdouts_z_is_inconclusive()
    {
        var shortDays = DeflationTests.Series(Day0, DeflationTests.Normal(7, 91, 0.001, 0.01));
        var shortMoments = Deflation.MomentsOf(shortDays)!;
        var research = (Sr: 0.08, Sr0: 0.03);

        var brief = Fidelity.Of(shortMoments, research.Sr, research.Sr0);
        Assert.Equal(GateStatus.Inconclusive, brief.Status);
        Assert.NotNull(brief.Z);
        var annualTwoSe = 2 * brief.Se!.Value * Math.Sqrt(365);
        Assert.InRange(annualTwoSe, 3.6, 4.4);

        // The rule's own arithmetic: z = (SR_h − (SR_research − SR0)) / se_h, se_h by R04 :64's variance over T − 1.
        var v = 1 - shortMoments.Skew * shortMoments.Sr + (shortMoments.Kurtosis - 1) / 4 * shortMoments.Sr * shortMoments.Sr;
        var se = Math.Sqrt(v / (shortMoments.T - 1));
        Assert.Equal((shortMoments.Sr - (research.Sr - research.Sr0)) / se, brief.Z!.Value, 12);

        var longDays = DeflationTests.Series(Day0, DeflationTests.Normal(7, 1500, 0.001, 0.01));
        var full = Fidelity.Of(Deflation.MomentsOf(longDays), research.Sr, research.Sr0);
        Assert.Equal(GateStatus.Enforceable, full.Status);
        Assert.True(2 * full.Se!.Value * Math.Sqrt(365) <= Fidelity.MaxAnnualTwoSe);

        // UNKNOWN IS NEVER ZERO: no research SR, no SR0 or no holdout moments is a null z, inconclusive.
        Assert.Null(Fidelity.Of(shortMoments, null, research.Sr0).Z);
        Assert.Null(Fidelity.Of(shortMoments, research.Sr, null).Z);
        Assert.Equal(new FidelityResult(null, null, GateStatus.Inconclusive), Fidelity.Of(null, research.Sr, research.Sr0));
    }

    /// <summary>
    /// THE BETTING E-VALUE GROWS ON A RISING STREAM, NEVER BETS AGAINST A FALLING ONE, AND IS NULL WITH NO KNOWN DAY.
    ///
    /// <para>λ_t is clipped to [0, 0.5] and read only from the days before t, so a stream that loses every day is never
    /// bet on after its first loss and stays at most 1; unknown days are skipped, not counted as 0.</para>
    /// </summary>
    [Fact]
    public void The_betting_e_value_bets_half_kelly_on_past_days_only()
    {
        var rising = DeflationTests.Series(Day0, DeflationTests.Normal(3, 365, 0.004, 0.01));
        Assert.True(BettingE.Of(rising) > 1);

        var falling = DeflationTests.Series(Day0, DeflationTests.Normal(3, 365, -0.004, 0.01));
        Assert.InRange(BettingE.Of(falling)!.Value, 0, 1);

        // The first two days bet nothing: their product is exactly 1 whatever they returned.
        Assert.Equal(1.0, BettingE.Of(DeflationTests.Series(Day0, [0.05, -0.03])));

        // Day three bets clip(mean/var/2, 0, 0.5) of the first two: mean 0.01, var 0.0004 → 12.5 → 0.5.
        Assert.Equal(1 + 0.5 * 0.02, BettingE.Of(DeflationTests.Series(Day0, [0.03, -0.01, 0.02]))!.Value, 12);

        Assert.Null(BettingE.Of([new DailyReturn(Day0, 0, null, null, null)]));
        Assert.Null(BettingE.Of([]));
    }
}

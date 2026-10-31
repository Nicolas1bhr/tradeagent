using TradeAgent.Core.Db;
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
    public async Task Addis_spends_per_verdict_across_renewals_and_tests_only_a_year_long_holdout()
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

        // AND IN THE LEDGER, ACROSS A RENEWAL: two verdicts under the first campaign — one judged, one charged and never
        // run, so it has no step and counts as tested — then a renewal, whose first verdict is the lineage's third and is
        // tested at α_3. The renewal resets nothing.
        var w = await RefereeVerdictTests.Given(frozenAt: new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), verdicts: 3);
        using var _ = w.Db;
        var second = Version(w, 102);
        var third = Version(w, 101);
        var t0 = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

        Assert.True(new Referee(w.Db, () => t0).Verdict(w.VersionId, w.Campaign.Id).Ok);
        Assert.True(w.Gw.Campaigns.ChargeVerdict(w.Campaign.Id, second, t0.AddMinutes(1)).Ok);
        var child = w.Gw.Campaigns.Renew(w.Campaign.Id, 5, 3, t0.AddMinutes(2));
        Assert.True(child.Ok, child.Why);
        var referee = new Referee(w.Db, () => t0.AddMinutes(3));
        var renewed = referee.Verdict(third, child.Campaign!.Id);
        Assert.True(renewed.Ok, renewed.Why);

        var holdouts = referee.Holdouts;
        var row1 = holdouts.SealedOf(w.Campaign.Id, w.VersionId)!;
        var row3 = holdouts.SealedOf(child.Campaign.Id, third)!;
        Assert.Null(holdouts.SealedOf(w.Campaign.Id, second));

        // A ONE-HOUR HOLDOUT IS NOT A YEAR: the first verdict is inconclusive and spent its α_1.
        Assert.Equal((AlphaWealth.Inconclusive, alpha1), (row1.AddisStatus, row1.AlphaJ));
        Assert.Equal([AlphaWealth.Inconclusive, AlphaWealth.Tested], holdouts.History(child.Campaign.Id, third));
        Assert.Equal(AlphaWealth.AlphaAfter([AlphaWealth.Inconclusive, AlphaWealth.Tested]), row3.AlphaJ, 15);
        Assert.Equal(AlphaWealth.WealthAfter([AlphaWealth.Inconclusive, AlphaWealth.Tested]), row3.WealthBefore, 15);
        Assert.True(row3.AlphaJ < afterSpend);
        Assert.Equal(AlphaWealth.Inconclusive, row3.AddisStatus);
    }

    /// <summary>
    /// THE α-WEALTH IS SPENT IN THE ORDER THE CHARGES WERE RECORDED, NOT BY THEIR TIMESTAMPS (finding 2 of the review). Two
    /// verdicts charged one after the other at one instant — a coarse or injected clock — whose second version's id sorts
    /// before the first's: ordered by (timestamp, campaign, version id), the second would read an empty history and spend
    /// α_1 again. It reads the first's step and spends α_2. The mutant is the order back to (timestamp, ids).
    /// </summary>
    [Fact]
    public async Task Charges_at_one_instant_spend_alpha_in_the_order_they_were_charged()
    {
        var w = await RefereeVerdictTests.Given(frozenAt: new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), verdicts: 2);
        using var _ = w.Db;
        var exit = Enumerable.Range(98, 40).First(x => string.CompareOrdinal(IdOf(x), w.VersionId) < 0);
        var second = Version(w, exit);
        Assert.True(string.CompareOrdinal(second, w.VersionId) < 0, $"{second} sorts after {w.VersionId}");

        var t0 = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var referee = new Referee(w.Db, () => t0);
        Assert.True(referee.Verdict(w.VersionId, w.Campaign.Id).Ok);
        var judged = referee.Verdict(second, w.Campaign.Id);
        Assert.True(judged.Ok, judged.Why);
        Assert.Equal([t0, t0], w.Gw.Campaigns.JudgementsCharged(w.Campaign.Id).Select(v => v.RequestedAt));

        var holdouts = referee.Holdouts;
        var row1 = holdouts.SealedOf(w.Campaign.Id, w.VersionId)!;
        var row2 = holdouts.SealedOf(w.Campaign.Id, second)!;
        Assert.Equal([w.VersionId, second], w.Gw.Campaigns.JudgementsCharged(w.Campaign.Id).Select(v => v.VersionId));
        Assert.Equal([AlphaWealth.Inconclusive], holdouts.History(w.Campaign.Id, second));
        Assert.Equal(AlphaWealth.AlphaAfter([]), row1.AlphaJ, 15);
        Assert.Equal(AlphaWealth.AlphaAfter([AlphaWealth.Inconclusive]), row2.AlphaJ, 15);
        Assert.Equal(row1.WealthAfter, row2.WealthBefore, 15);
    }

    static string IdOf(int exitAbove) => StrategyParser.Parse(Text(exitAbove)).Program!.StrategyId;

    static string Text(int exitAbove) => "instrument BTCUSDT\nsize fixed 1\ntimeframe 1m\ndata_freshness 2m\nmax_decision_age 30s\n"
        + $"exit when close > {exitAbove}\nentry when close < 97\n";

    /// <summary>Another version of the fixture's program, recorded frozen with the fixture's own, for a second verdict.</summary>
    static string Version(RefereeVerdictTests.World w, int exitAbove)
    {
        var parsed = StrategyParser.Parse("instrument BTCUSDT\nsize fixed 1\ntimeframe 1m\ndata_freshness 2m\nmax_decision_age 30s\n"
            + $"exit when close > {exitAbove}\nentry when close < 97\n").Program!;
        new StrategyStore(w.Db).RecordVersion(new StrategyVersionRow(parsed.StrategyId, parsed.Source, parsed.Canonical,
            parsed.Manifest, StrategyStore.InterpreterBuild, ParseVerdict.Accepted, parsed.WarmUpBars,
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), TradeAgent.Core.CouncilRoles.Research, "turn-test"));
        return parsed.StrategyId;
    }
}

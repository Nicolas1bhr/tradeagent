using System.Globalization;
using System.Text;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Proving;
using TradeAgent.Core.Strategy;
using TradeAgent.Provisioning;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-EVIDENCE-HOLDOUT-LENGTH: THE VERDICTS OF THE FIRST REAL RUN MEAN SOMETHING (R30 § 3 row 4; D12, D15, D16). Each proof walks the real
/// path — setup's press, the download, the dataset ledger, the campaign, the referee — rather than reading a constant.
/// </summary>
public class EvidenceHoldoutLengthTests
{
    /// <summary>When these tests' setup press runs: its 36 complete months are October 2023 to September 2026.</summary>
    static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// ONE MONTHLY FILE OF THE ARCHIVE: the last hour of every UTC day, in Binance's own row shape. A whole month is too much for a test, and
    /// an hour a day is the same shape over the same calendar: the last bar of the last day closes at the month's end, so a holdout's span
    /// and its known days are measured exactly as a real download's are. Three days in four the price cycles 96 → 105 at three minutes a
    /// level (an order fills two minutes after the minute it was learnt on, so "winning" and "losing" are true by construction); the
    /// fourth is flat, so a version's days differ and its statistics are a number.
    /// </summary>
    static string Month(DateOnly month)
    {
        var b = new StringBuilder();
        for (var day = month; day.Month == month.Month; day = day.AddDays(1))
        {
            var start = new DateTimeOffset(day.Year, day.Month, day.Day, 23, 0, 0, TimeSpan.Zero);
            for (var i = 0; i < 60; i++)
            {
                var close = day.DayNumber % 4 == 3 ? 100m : 96m + i / 3 % 10;
                var open = start.AddMinutes(i).ToUnixTimeMilliseconds() * 1000L;
                var closed = start.AddMinutes(i + 1).ToUnixTimeMilliseconds() * 1000L - 1;
                b.Append(CultureInfo.InvariantCulture, $"{open},{close},{close + 1m},{close - 1m},{close},1.00000000,{closed},1000.00000000,10,0.50000000,500.00000000,0\n");
            }
        }
        return b.ToString();
    }

    static string Winning(string pair) =>
        $"instrument {pair}\nsize fixed 1\ntimeframe 1m\ndata_freshness 2m\nmax_decision_age 30s\nexit when close > 103\nentry when close < 97\n";

    static string Losing(string pair) =>
        $"instrument {pair}\nsize fixed 1\ntimeframe 1m\ndata_freshness 2m\nmax_decision_age 30s\nexit when close < 97\nentry when close > 103\n";

    /// <summary>A program the app accepted and froze at <paramref name="at"/>, as every version frozen now is: after a historical cutoff.</summary>
    static string Freeze(Database db, string source, DateTimeOffset at)
    {
        var parsed = StrategyParser.Parse(source);
        Assert.True(parsed.Ok, parsed.Why);
        var program = parsed.Program!;
        new StrategyStore(db).RecordVersion(new StrategyVersionRow(program.StrategyId, program.Source, program.Canonical, program.Manifest,
            StrategyStore.InterpreterBuild, ParseVerdict.Accepted, program.WarmUpBars, at, CouncilRoles.Research, "attempt-1")
        {
            Timeframe = program.Freshness?.Timeframe,
            DataFreshness = program.Freshness?.DataFreshness,
            MaxDecisionAge = program.Freshness?.MaxDecisionAge
        });
        return program.StrategyId;
    }

    /// <summary>
    /// D12: A VERDICT ON THE DEFAULT HOLDOUT CAN BE CONCLUSIVE, ON THE REAL PATH. Setup's one press over the archive — the default months,
    /// checked, recorded in the dataset ledger, the default cutoff, the pair's campaign — and the referee's own run over what it held back:
    /// the sealed step has a year of known days and a p_h, so the α-test RAN, for the winner and the loser alike; and the loser's refusal
    /// is a conclusive one, which is what keeps it off paper. Under the old 12-and-3 default this is 92 days and every step reads
    /// <see cref="AlphaWealth.Inconclusive"/> (R27).
    /// </summary>
    [Fact]
    public async Task A_verdict_on_the_default_holdout_is_conclusive_from_setups_press_to_the_referees_sealed_step()
    {
        var pair = TestEnv.NewPair();
        using var archive = new FakeArchive();
        foreach (var month in BinanceArchive.RecentCompleteMonths(Now)) archive.Publish(pair, month, Month(month));
        var (gw, _, db) = await TestEnv.Ready();
        using var owned = db;
        await using var gateway = gw;
        var service = new MarketDataService(db, new BinanceArchiveClient(archive.BaseUrl));
        var evidence = new EvidenceCampaigns(() => gw, (p, now, progress, ct) => service.CollectWholeAsync(p, now, progress, ct),
            // THE APP'S INSTRUMENT CHECK, for a pair the test made up: the venue's own step, without which no campaign pins its judge.
            (p, _) => { gw.Venues.Sync(SyntheticMarket.Venues(VenueCatalog.BinanceSpot, [p])); return Task.CompletedTask; });

        var setup = await evidence.SetUpAsync(Now, [pair]);

        Assert.True(setup.Ok, setup.Words);
        Assert.Empty(setup.Unfinished);
        var campaign = Assert.Single(setup.Campaigns);
        var set = gw.Datasets.ById(campaign.HoldoutDatasetId)!;
        Assert.Equal((BinanceArchive.MonthsWanted, DatasetState.ACCEPTED), (set.MonthsPresent, set.State));

        var winner = Freeze(db, Winning(pair), Now);
        var loser = Freeze(db, Losing(pair), Now);
        var referee = new Referee(db, () => Now);
        var won = referee.Verdict(winner, campaign.Id);
        var lost = referee.Verdict(loser, campaign.Id);
        Assert.True(won.Ok, won.Why);
        Assert.True(lost.Ok, lost.Why);
        Assert.Equal(PromotionVerdict.PaperEligible, won.Promotion!.Verdict);
        Assert.Equal((PromotionVerdict.Refused, PromotionReason.NotProfitable), (lost.Promotion!.Verdict, lost.Promotion.Reason));

        foreach (var version in new[] { winner, loser })
        {
            var step = referee.Holdouts.SealedOf(campaign.Id, version)!;
            Assert.NotEqual(AlphaWealth.Inconclusive, step.AddisStatus);
            Assert.True(step.HoldoutDays >= AlphaWealth.MinHoldoutDays, $"{step.HoldoutDays} known days");
            Assert.NotNull(step.PH);
        }

        // THE LOSS IS ANSWERED: neither of the two inconclusive classes, so no inconclusive slot is open to it.
        var span = gw.Campaigns.HoldoutSpan(campaign.Id);
        Assert.True(HoldoutLength.CanBeConclusive(span), $"the default holdout spans {span}");
        Assert.Null(Inconclusive.Of(new Promotions(db).Standing(loser), span));
    }
}

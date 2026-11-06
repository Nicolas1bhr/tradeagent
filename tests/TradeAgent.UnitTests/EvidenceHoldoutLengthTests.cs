using System.Globalization;
using System.Text;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Proving;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
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
    /// SETUP'S ONE PRESS ON THE REAL PATH: <paramref name="archive"/> publishing the default months of <paramref name="pair"/>, the app's
    /// whole-or-nothing download recording them, and <see cref="EvidenceCampaigns.SetUpAsync"/> holding back the default months under the
    /// pair's own campaign.
    /// </summary>
    static async Task<(TradingGateway Gw, Database Db, CampaignRow Campaign)> SetUp(FakeArchive archive, string pair,
        Action<TradeAgentSettings>? settings = null)
    {
        foreach (var month in BinanceArchive.RecentCompleteMonths(Now)) archive.Publish(pair, month, Month(month));
        var (gw, _, db) = await TestEnv.Ready(settings);
        var service = new MarketDataService(db, new BinanceArchiveClient(archive.BaseUrl));
        var evidence = new EvidenceCampaigns(() => gw, (p, now, progress, ct) => service.CollectWholeAsync(p, now, progress, ct),
            // THE APP'S INSTRUMENT CHECK, for a pair the test made up: the venue's own step, without which no campaign pins its judge.
            (p, _) => { gw.Venues.Sync(SyntheticMarket.Venues(VenueCatalog.BinanceSpot, [p])); return Task.CompletedTask; });

        var setup = await evidence.SetUpAsync(Now, [pair]);

        Assert.True(setup.Ok, setup.Words);
        Assert.Empty(setup.Unfinished);
        return (gw, db, Assert.Single(setup.Campaigns));
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
        var (gw, db, campaign) = await SetUp(archive, pair);
        using var owned = db;
        await using var gateway = gw;

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

    // ---- D15: a full exploration pot is spent when no parent can be promoted ------------------------------------------------------

    /// <summary>The budgets D15's tests run on: small, so a pot fills in two runs. Reading them back proves nothing moved them.</summary>
    static void Small(TradeAgentSettings s)
    {
        s.CampaignTrialBudget = 10;
        s.CampaignVerdictBudget = 3;
        s.CampaignExplorationBudget = 2;
    }

    /// <summary>One version frozen at <see cref="Now"/>, its parent declared, with no program a referee could run: for the trial ledger.</summary>
    static string Version(Database db, string pair, string name, string? parent = null)
    {
        var source = $"instrument {pair}\n# {name}\n";
        new StrategyStore(db).RecordVersion(new StrategyVersionRow(name, source, "canonical", "{}", StrategyStore.InterpreterBuild,
            ParseVerdict.Accepted, 0, Now, CouncilRoles.Research, "attempt-1") { ParentVersionId = parent });
        return name;
    }

    /// <summary>One completed research run of a version, registered as a trial of the campaign. Answers the ledger's answer.</summary>
    static TrialRegistered Trial(Database db, CampaignRow campaign, string version, string run)
    {
        var set = new DatasetStore(db).ById(campaign.HoldoutDatasetId)!;
        new StrategyStore(db).RecordRun(new StrategyRunRow(run, version, set.Id, set.NormalisedSha256, campaign.HoldoutFrom.AddDays(-10),
            campaign.HoldoutFrom, "frictionless", BacktestOutcome.COMPLETED.ToString(), null, 10, 1, 1, 1, 1, 1, 0, 0, 1m, 0m, 1m, 0m, "sha",
            Now, CouncilRoles.Research, "attempt-1"), []);
        return new CampaignStore(db).RegisterTrial(campaign.Id, version, run, EvaluationClass.Research, Now);
    }

    /// <summary>The campaign's exploration pot filled by versions with no parent.</summary>
    static void FillThePot(Database db, CampaignRow campaign, string pair)
    {
        for (var i = 0; i < campaign.ExplorationBudget; i++)
            Assert.True(Trial(db, campaign, Version(db, pair, $"explore-{i}"), $"run-explore-{i}").Ok);
    }

    /// <summary>
    /// D15: A FULL EXPLORATION POT WITH NOTHING TO PROMOTE SPENDS THE CAMPAIGN AND ARMS ITS RENEWAL (R30 D15; R28 E2). One trial short it is
    /// not spent and nothing arms. On the last one it is spent on its exploration trials, the rule arms the pair's fresh holdout, and
    /// the refusal, the look's and the gate's, says what is spent, that the campaign cannot be charged again, and when the next one can
    /// come. Nothing about the budgets moved, and the judgements it has left stay spendable. The Owner page says the same.
    /// </summary>
    [Fact]
    public async Task A_full_exploration_pot_with_no_parent_to_promote_spends_the_campaign_and_arms_its_renewal()
    {
        var pair = TestEnv.NewPair();
        using var archive = new FakeArchive();
        var (gw, db, campaign) = await SetUp(archive, pair, Small);
        using var owned = db;
        await using var gateway = gw;
        var renewals = gw.Renewals;

        Assert.True(Trial(db, campaign, Version(db, pair, "first"), "run-first").Ok);
        Assert.Null(renewals.SpentOn(campaign));
        Assert.Empty(renewals.Arm(Now));

        Assert.True(Trial(db, campaign, Version(db, pair, "second"), "run-second").Ok);
        Assert.Equal(CampaignRenewal.ExplorationTrials, renewals.SpentOn(campaign));
        var armed = Assert.Single(renewals.Arm(Now));
        Assert.Equal((campaign.Id, CampaignRenewal.ExplorationTrials), (armed.SpentCampaignId, armed.Spent));

        var row = gw.Campaigns.ById(campaign.Id)!;
        Assert.Equal((10, 3, 2, true), (row.TrialBudget, row.VerdictBudget, row.ExplorationBudget, row.IsOpen));
        Assert.Equal(2, gw.Campaigns.TrialsCharged(campaign.Id));

        var third = Version(db, pair, "third");
        var look = gw.Campaigns.TrialRefusal(campaign.Id, third, EvaluationClass.Research);
        var gate = Trial(db, campaign, third, "run-third");
        Assert.False(gate.Ok);
        Assert.Contains("this run is refused before it is made", look, StringComparison.Ordinal);
        Assert.Contains("this run is not recorded and its result is not served", gate.Why, StringComparison.Ordinal);
        foreach (var words in new[]
                 {
                     $"campaign {campaign.Id} has spent all 2 of the trials it reserves for EXPLORATION",
                     "cannot be charged another of these trials",
                     "A paper-eligible version is not a promoted parent",
                     "do not spend a turn on it",
                     $"so the earliest it can open is {armed.Earliest:u}"
                 })
        {
            Assert.Contains(words, look, StringComparison.Ordinal);
            Assert.Contains(words, gate.Why, StringComparison.Ordinal);
        }

        Assert.True(gw.Campaigns.ChargeVerdict(campaign.Id, third, Now).Ok);

        var page = new EvidenceCampaigns(() => gw, (_, _, _, _) => throw new InvalidOperationException("nothing is downloaded"));
        Assert.Contains($"Its exploration trials are spent; the next {pair} campaign waits for fresh bars", Assert.Single(page.Standings()).Line(Now),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// D15: A PAPER-ELIGIBLE VERSION IS NOT A PARENT THAT CAN BE PROMOTED (R30 D15). A real verdict leaves the winner paper-eligible. Its
    /// declared child is still an exploration, so it comes out of the same pot, and once that pot is full the campaign is spent: a
    /// favourable verdict over history buys no refinement budget. The watched mutant counts paper-eligible as promotable.
    /// </summary>
    [Fact]
    public async Task A_paper_eligible_version_is_no_parent_to_promote_so_its_child_is_exploration_and_the_campaign_is_spent()
    {
        var pair = TestEnv.NewPair();
        using var archive = new FakeArchive();
        var (gw, db, campaign) = await SetUp(archive, pair, Small);
        using var owned = db;
        await using var gateway = gw;
        var winner = Freeze(db, Winning(pair), Now);
        Assert.Equal(PromotionVerdict.PaperEligible, new Referee(db, () => Now).Verdict(winner, campaign.Id).Promotion!.Verdict);
        Assert.True(new Promotions(db).Standing(winner).IsPaperEligible);

        var child = Version(db, pair, "child-of-paper-eligible", winner);
        Assert.True(gw.Campaigns.IsExploration(child));
        Assert.True(Trial(db, campaign, child, "run-child").Ok);
        Assert.Equal(1, gw.Campaigns.TrialsCharged(campaign.Id, exploration: true));
        Assert.True(Trial(db, campaign, Version(db, pair, "another"), "run-another").Ok);

        Assert.False(gw.Renewals.ParentCanBePromoted(campaign));
        Assert.Equal(CampaignRenewal.ExplorationTrials, gw.Renewals.SpentOn(campaign));
    }

    /// <summary>
    /// D15: A VERSION THAT STANDS PROMOTED KEEPS THE CAMPAIGN UNSPENT, BECAUSE ITS REFINEMENT POT CAN STILL BE DRAWN ON. A program frozen before
    /// the cutoff can pass the forward-evidence clause: a real verdict promotes it. With the exploration pot full nothing arms, a child
    /// of it is charged to the refinement pot, and an exploration is refused in the old words, the way on being the other pot.
    /// </summary>
    [Fact]
    public async Task A_promoted_parent_keeps_a_full_pot_unspent_because_the_refinement_pot_can_be_drawn_on()
    {
        var pair = TestEnv.NewPair();
        using var archive = new FakeArchive();
        var (gw, db, campaign) = await SetUp(archive, pair, Small);
        using var owned = db;
        await using var gateway = gw;
        var parent = Freeze(db, Winning(pair), campaign.HoldoutFrom.AddDays(-1));
        Assert.Equal(PromotionVerdict.Promoted, new Referee(db, () => Now).Verdict(parent, campaign.Id).Promotion!.Verdict);
        FillThePot(db, campaign, pair);

        Assert.True(gw.Renewals.ParentCanBePromoted(campaign));
        Assert.Null(gw.Renewals.SpentOn(campaign));
        Assert.Empty(gw.Renewals.Arm(Now));

        Assert.True(Trial(db, campaign, Version(db, pair, "refinement", parent), "run-refinement").Ok);
        Assert.Equal(1, gw.Campaigns.TrialsCharged(campaign.Id, exploration: false));
        var refused = gw.Campaigns.TrialRefusal(campaign.Id, Version(db, pair, "a-third-exploration"), EvaluationClass.Research);
        Assert.Contains("registered all 2 of the trials it reserves for EXPLORATION", refused, StringComparison.Ordinal);
        Assert.DoesNotContain("cannot be charged another", refused, StringComparison.Ordinal);
    }

    /// <summary>
    /// D15: A VERSION THE CAMPAIGN COULD STILL PROMOTE KEEPS IT UNSPENT UNTIL IT IS JUDGED. The case of a campaign over fresh months, where every
    /// version made before them was frozen before its cutoff. While one such version of its pair is unjudged a parent can still be
    /// promoted, so a full pot arms nothing; once the referee has refused it, nothing can be, and the campaign is spent.
    /// </summary>
    [Fact]
    public async Task A_version_frozen_before_the_cutoff_keeps_a_full_pot_unspent_until_it_is_judged()
    {
        var pair = TestEnv.NewPair();
        using var archive = new FakeArchive();
        var (gw, db, campaign) = await SetUp(archive, pair, Small);
        using var owned = db;
        await using var gateway = gw;
        var unjudged = Freeze(db, Losing(pair), campaign.HoldoutFrom.AddDays(-1));
        FillThePot(db, campaign, pair);

        Assert.True(gw.Renewals.ParentCanBePromoted(campaign));
        Assert.Null(gw.Renewals.SpentOn(campaign));

        Assert.Equal(PromotionVerdict.Refused, new Referee(db, () => Now).Verdict(unjudged, campaign.Id).Promotion!.Verdict);

        Assert.False(gw.Renewals.ParentCanBePromoted(campaign));
        Assert.Equal(CampaignRenewal.ExplorationTrials, gw.Renewals.SpentOn(campaign));
    }

    /// <summary>
    /// D15: EVERY TURN IS TOLD WHAT IS SPENT BEFORE IT ACTS, so none pays to be refused for what the ledger already says (R30 D15, "turns
    /// must not be sold a refusal twice without learning it"; R28 E2, "turns keep spending the plan on refusals"). A real puppet turn
    /// through the app's own wiring: its packet names the pair, what is spent, what can no longer be charged, what it can still ask for
    /// and when the next campaign can come. A turn before the pot filled is told nothing.
    /// </summary>
    [Fact]
    public async Task A_turn_reads_what_a_campaign_has_spent_before_it_can_buy_the_refusal()
    {
        await using var life = await PuppetLife.Begin("spent-told", settings: Small);
        var db = life.Host.Db;
        var gw = life.Host.Gateway;
        var pair = TestEnv.NewPair();
        using var archive = new FakeArchive();
        foreach (var month in BinanceArchive.RecentCompleteMonths(Now)) archive.Publish(pair, month, Month(month));
        var service = new MarketDataService(db, new BinanceArchiveClient(archive.BaseUrl));
        var campaign = Assert.Single((await new EvidenceCampaigns(() => gw, (p, now, progress, ct) => service.CollectWholeAsync(p, now, progress, ct),
            (p, _) => { gw.Venues.Sync(SyntheticMarket.Venues(VenueCatalog.BinanceSpot, [p])); return Task.CompletedTask; })
            .SetUpAsync(Now, [pair])).Campaigns);

        var role = CouncilRoles.Research;
        life.Script(role, ["usage 100 0 10", "final Looked."], ["usage 100 0 10", "final Looked again."]);
        life.Wake(role);
        await life.RunUntil(() => life.AttemptsOf(role).Count(a => a.State == AiAttemptState.ENDED) == 1, "the first turn ended");
        Assert.DoesNotContain("Research budgets already spent", life.Prompt(role, 1), StringComparison.Ordinal);

        FillThePot(db, campaign, pair);
        var armed = Assert.Single(gw.Renewals.Arm(DateTimeOffset.UtcNow));
        life.Wake(role);
        await life.RunUntil(() => life.AttemptsOf(role).Count(a => a.State == AiAttemptState.ENDED) == 2, "the second turn ended");

        var prompt = life.Prompt(role, 2)!;
        Assert.Contains("Research budgets already spent", prompt, StringComparison.Ordinal);
        Assert.Contains($"{pair}: campaign {campaign.Id} has spent its exploration trials: it takes no further run of a version without a "
            + $"promoted parent, and nothing can be promoted under it, though 3 of its final judgements can still be asked for. The next {pair} "
            + $"campaign opens no earlier than {armed.Earliest:yyyy-MM-dd}.", prompt, StringComparison.Ordinal);
    }
}

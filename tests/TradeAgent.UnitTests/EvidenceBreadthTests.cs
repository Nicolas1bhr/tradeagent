using System.Globalization;
using System.Text;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-EVIDENCE-BREADTH — MORE THAN ONE MARKET UNDER EVIDENCE, EACH PAIR WITH ITS OWN HOLDOUT AND ITS OWN CAMPAIGN.
///
/// <para><b>What holds.</b> A version names one instrument and is measured only on its bars; its trials, its family's home and
/// its judgements are charged to campaigns of THAT pair and no other — so five pairs held over the same months are five budgets,
/// not one shared by accident. What stays shared, on purpose: the holdout itself — a cutoff on any pair holds every pair's bars
/// over its window from every pipe read — and, within one pair, every download of the same months (a second download buys no
/// fresh trials or judgements). A spent campaign arms its fresh holdout one window at a time PER PAIR.</para>
/// </summary>
public class EvidenceBreadthTests
{
    static readonly DateTimeOffset At = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Cutoff = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    // ---- the store, over recorded rows ------------------------------------------------------------

    /// <summary>A dataset row of <paramref name="pair"/> held back from <see cref="Cutoff"/>, by the store the owner's window calls.</summary>
    static DatasetRecord Held(Database db, string pair, string version = "v1")
    {
        var store = new DatasetStore(db);
        var id = store.Record(new DatasetRecord(
            0, BinanceArchive.Source, pair, BinanceArchive.Interval, version, 12, 12, [],
            Path.Combine(Paths.Data, $"not-read-here-{Guid.NewGuid():n}.csv"), "aa11", 1000,
            Cutoff.AddDays(-300), Cutoff.AddDays(60), 0, [], false, 0, 0, 0, At,
            DatasetState.ACCEPTED, null, []));
        Assert.True(store.SetHoldout(id, Cutoff, EvaluationClass.Research).Ok);
        return store.ById(id)!;
    }

    static CampaignRow Opened(Database db, DatasetRecord over, int trials = 3, int verdicts = 2)
    {
        var opened = new CampaignStore(db).Open($"{over.Pair} 1m {over.Version}", over, trials, verdicts, At);
        Assert.True(opened.Ok, opened.Why);
        return opened.Campaign!;
    }

    /// <summary>One recorded version of <paramref name="instrument"/>, declaring <paramref name="parent"/> when one is given.</summary>
    static string Version(Database db, string id, string instrument, string? parent = null)
    {
        new StrategyStore(db).RecordVersion(new StrategyVersionRow(
            id, $"instrument {instrument}\n", "canonical", "{}", StrategyStore.InterpreterBuild,
            ParseVerdict.Accepted, 0, At, CouncilRoles.Research, "attempt-1") { ParentVersionId = parent });
        return id;
    }

    /// <summary>One recorded run of a version over a dataset, so a trial has a run row to charge.</summary>
    static string Ran(Database db, string version, DatasetRecord over, string run)
    {
        new StrategyStore(db).RecordRun(new StrategyRunRow(
            run, version, over.Id, over.NormalisedSha256, Cutoff.AddDays(-10), Cutoff,
            "frictionless", BacktestOutcome.COMPLETED.ToString(), null,
            10, 1, 1, 1, 1, 1, 0, 0, 1m, 0m, 1m, 0m, "sha", At, CouncilRoles.Research, "attempt-1"), []);
        return run;
    }

    /// <summary>
    /// A JUDGEMENT OF ONE PAIR SPENDS NONE OF ANOTHER PAIR'S, over the same held months: ETHUSDT's campaign judging a version
    /// leaves BTCUSDT's campaign with every judgement, names no other campaign over its months, and its α-wealth order empty. A
    /// second campaign of the SAME pair over the same months still counts the first's judgement — a fresh download of BTCUSDT
    /// buys no fresh look at BTCUSDT's months. RED on the base, where every campaign over the months counted every pair's.
    /// </summary>
    [Fact]
    public void A_judgement_of_one_pair_spends_none_of_another_pairs_and_every_one_of_its_own()
    {
        using var db = TestEnv.NewDb();
        var campaigns = new CampaignStore(db);
        var btc = Opened(db, Held(db, "BTCUSDT"));
        var eth = Opened(db, Held(db, "ETHUSDT"));

        Assert.True(campaigns.ChargeVerdict(eth.Id, Version(db, "version-eth", "ETHUSDT"), At).Ok);

        Assert.Equal(1, campaigns.JudgementsSpent(eth.Id));
        var spent = campaigns.JudgementsSpent(btc.Id);
        Assert.True(spent == 0, $"campaign {btc.Id} (BTCUSDT) counts {spent} judgement(s) after campaign {eth.Id} (ETHUSDT) "
            + "judged one ETHUSDT version over the same months: another pair's judgement spent its budget");
        Assert.Empty(campaigns.OverTheSameMonths(btc.Id));
        Assert.Empty(campaigns.JudgementsCharged(btc.Id));

        // THE SAME PAIR STILL SHARES: a second BTCUSDT download held over the same months counts BTCUSDT's judgement.
        Assert.True(campaigns.ChargeVerdict(btc.Id, Version(db, "version-btc", "BTCUSDT"), At).Ok);
        var again = Opened(db, Held(db, "BTCUSDT", "v2"));
        Assert.Equal(1, campaigns.JudgementsSpent(again.Id));
        Assert.Equal(btc.Id, Assert.Single(campaigns.OverTheSameMonths(again.Id)).Campaign.Id);
        Assert.Equal(1, campaigns.JudgementsSpent(eth.Id));
    }

    /// <summary>
    /// A FAMILY'S HOME IS ON ITS OWN PAIR: a root charged under BTCUSDT's campaign, ported to an ETHUSDT child, charges that child
    /// to ETHUSDT's campaign — never back to BTCUSDT's — while a child run under a second BTCUSDT campaign is still charged home to
    /// the first. RED on the base, where the ETHUSDT child's trial was charged to BTCUSDT's campaign.
    /// </summary>
    [Fact]
    public void A_familys_home_is_the_campaign_of_its_own_pair()
    {
        using var db = TestEnv.NewDb();
        var campaigns = new CampaignStore(db);
        var (btcSet, ethSet) = (Held(db, "BTCUSDT"), Held(db, "ETHUSDT"));
        var btc = Opened(db, btcSet);
        var eth = Opened(db, ethSet);
        var root = Version(db, "version-root", "BTCUSDT");
        Assert.True(campaigns.RegisterTrial(btc.Id, root, Ran(db, root, btcSet, "run-root"), EvaluationClass.Research, At).Ok);

        var ported = Version(db, "version-ported", "ETHUSDT", parent: root);
        Assert.True(campaigns.RegisterTrial(eth.Id, ported, Ran(db, ported, ethSet, "run-ported"), EvaluationClass.Research, At).Ok);
        var home = Assert.Single(campaigns.Trials(eth.Id)).ChargedTo;
        Assert.True(home == eth.Id, $"the ETHUSDT child of a BTCUSDT root was charged to campaign {home}, not to ETHUSDT's "
            + $"campaign {eth.Id}: another pair's budget paid for it");
        Assert.Equal(1, campaigns.TrialsCharged(btc.Id));
        Assert.Equal(eth.Id, campaigns.ChargedCampaignFor(ported, eth.Id));

        // WITHIN THE PAIR, UNCHANGED: a child run under a second BTCUSDT campaign is charged home to the root's.
        var secondSet = Held(db, "BTCUSDT", "v2");
        var second = Opened(db, secondSet);
        var child = Version(db, "version-child", "BTCUSDT", parent: root);
        Assert.True(campaigns.RegisterTrial(second.Id, child, Ran(db, child, secondSet, "run-child"), EvaluationClass.Research, At).Ok);
        Assert.Equal(btc.Id, Assert.Single(campaigns.Trials(second.Id)).ChargedTo);
        Assert.Equal(2, campaigns.TrialsCharged(btc.Id));
    }

    // ---- the gateway, over bars on disk -----------------------------------------------------------

    static readonly DateTimeOffset Bar0 = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    const int HoldoutAtBar = 60;

    /// <summary>120 one-minute bars of <paramref name="pair"/> recorded with their files, holding no cutoff.</summary>
    static DatasetRecord Bars(TradingGateway gw, string pair, string version)
    {
        var dir = BinanceArchive.DatasetDir(pair);
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        var raw = Path.Combine(dir, "raw", $"{pair}-{Guid.NewGuid():n}.zip");
        File.WriteAllText(raw, "a stand-in for the vendor's monthly archive");

        var csv = Path.Combine(dir, $"{Guid.NewGuid():n}.csv");
        var text = new StringBuilder().Append(KlineNormaliser.Header).Append('\n');
        for (var i = 0; i < 120; i++)
        {
            var close = 96m + i % 10;
            text.Append(CultureInfo.InvariantCulture,
                $"{Bar0.AddMinutes(i).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{close},{close + 1m},{close - 1m},{close},1.00\n");
        }
        File.WriteAllText(csv, text.ToString());

        return gw.Datasets.ById(gw.Datasets.Record(new DatasetRecord(
            0, BinanceArchive.Source, pair, BinanceArchive.Interval, version, 12, 1, ["2025-09"],
            csv, DatasetStore.Sha256(csv)!, 120, Bar0, Bar0.AddMinutes(119), 0, [], false,
            0, 0, 0, Bar0, DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-08", "https://127.0.0.1/x.zip", DatasetStore.Sha256(raw)!,
                DatasetStore.Sha256(raw)!, new FileInfo(raw).Length, Bar0, KlineTimeUnit.Microseconds, raw)])))!;
    }

    static CampaignRow HeldBack(TradingGateway gw, DatasetRecord set)
    {
        var (held, campaign) = gw.SetHoldout(set.Id, Bar0.AddMinutes(HoldoutAtBar), EvaluationClass.Research);
        Assert.True(held.Ok, held.Why);
        return campaign!;
    }

    static string Program(string instrument, string name)
    {
        var dir = Path.Combine(Paths.RoleHome(CouncilRoles.Research), "strategies");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), $"instrument {instrument}\nsize fixed 1\nexit when close < 97\nentry when close > 103\n");
        return "strategies/" + name;
    }

    static BacktestRan Run(TradingGateway gw, DatasetRecord set, string path) =>
        gw.Backtests.Run(
            AgentContext.ForAgent("agent", CouncilRoles.Research, "attempt-1"),
            new BacktestAsk(path, set.Id, Bar0, Bar0.AddMinutes(HoldoutAtBar - 1), 0.001m, Increment: 1m));

    /// <summary>
    /// A RUN IS CHARGED TO ITS OWN PAIR'S CAMPAIGNS AND TO NO OTHER PAIR'S — and the holdout is still every pair's. ETHUSDT and
    /// BTCUSDT are held back over the same months, each under its own campaign. An ETHUSDT run over ETHUSDT's development months
    /// costs ETHUSDT's campaign one trial and BTCUSDT's none; a BTCUSDT run over a second BTCUSDT download of those months is
    /// still charged to BTCUSDT's campaign (the pair's other downloads buy no free trials) and to ETHUSDT's not at all. And an
    /// ETHUSDT read reaching BTCUSDT's held window is still refused: no holdout is read across pairs. RED on the base, where the
    /// ETHUSDT run was charged to BTCUSDT's campaign as well.
    /// </summary>
    [Fact]
    public async Task A_run_is_charged_to_its_own_pairs_campaigns_and_the_holdout_stays_every_pairs()
    {
        var (gw, _, db) = await TestEnv.Ready(s => { s.CampaignTrialBudget = 3; s.CampaignVerdictBudget = 2; });
        using var owned = db;
        await using var gateway = gw;
        var btcSet = Bars(gw, "BTCUSDT", "v1");
        var ethSet = Bars(gw, "ETHUSDT", "v1");
        var btc = HeldBack(gw, btcSet);
        var eth = HeldBack(gw, ethSet);

        var ethRun = Run(gw, ethSet, Program("ETHUSDT", "eth.strategy"));
        Assert.Equal(ethRun.Result.RunId, Assert.Single(gw.Campaigns.Trials(eth.Id)).RunId);
        var crossed = gw.Campaigns.TrialsCharged(btc.Id);
        Assert.True(crossed == 0, $"an ETHUSDT run over ETHUSDT's months cost BTCUSDT's campaign {btc.Id} {crossed} trial(s): "
            + "another pair's budget was charged");

        // THE PAIR'S OTHER DOWNLOAD IS STILL CHARGED to the pair's campaign — and to no other pair's.
        var second = Bars(gw, "BTCUSDT", "v2");
        var btcRun = Run(gw, second, Program("BTCUSDT", "btc.strategy"));
        Assert.Equal(btcRun.Result.RunId, Assert.Single(gw.Campaigns.Trials(btc.Id)).RunId);
        Assert.Equal(1, gw.Campaigns.TrialsCharged(eth.Id));

        // NO HOLDOUT READ ACROSS PAIRS: an ETHUSDT read reaching BTCUSDT's held window is refused, as before.
        var pipe = TapeHoldout.Pipe(CouncilRoles.Research, gw.Datasets);
        var other = Bars(gw, "ETHUSDT", "v2");
        Assert.NotNull(pipe.Refusal(other, Bar0.AddMinutes(HoldoutAtBar), Bar0.AddMinutes(HoldoutAtBar + 5)));
    }
}

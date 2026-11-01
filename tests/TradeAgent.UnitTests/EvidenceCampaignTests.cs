using System.Globalization;
using System.Text;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using TradeAgent.Provisioning;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-EVIDENCE-CAMPAIGN — THE DATA, THE HOLDOUT AND THE VERDICTS THAT LAST FOR DAYS UNATTENDED (R25 D7).
///
/// <para><b>Item 1</b>: setup's one press holds back the last three complete months of the first download and never moves a
/// cutoff that is already there. <b>Item 2</b>: a spent campaign is followed, by rule, only by one over FRESH market data — armed
/// once at the first midnight after everything on record, held from every pipe read from that moment (never from the paper
/// runner), opened by the app only on a download holding at least thirty days of it, with no other window past its cutoff — and
/// every refusal says what it waits for. Nothing is re-keyed: the spent campaign keeps its judgements, its trials and its months.</para>
/// </summary>
public class EvidenceCampaignTests
{
    /// <summary>When the passes in these tests run: after the first download's last bar closed.</summary>
    static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The first download: the thirty-six months October 2023 to September 2026, minute bars (R25 D12).</summary>
    static readonly DateTimeOffset First = new(2023, 10, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Last = new(2026, 9, 30, 23, 59, 0, TimeSpan.Zero);

    /// <summary>The default holdout of that download: its last twelve months, October 2025 to September 2026.</summary>
    static readonly DateTimeOffset DefaultCutoff = new(2025, 10, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The fresh cutoff a pass at <see cref="Now"/> arms: the first UTC midnight after it.</summary>
    static readonly DateTimeOffset Fresh = new(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A dataset row the ledger recorded, over the bars named — no file read here.</summary>
    static DatasetRecord Recorded(Database db, string pair, DateTimeOffset first, DateTimeOffset last, string version = "v1")
    {
        var store = new DatasetStore(db);
        var id = store.Record(new DatasetRecord(
            0, BinanceArchive.Source, pair, BinanceArchive.Interval, version, 12, 12, [],
            Path.Combine(Paths.Data, $"not-read-here-{Guid.NewGuid():n}.csv"), "aa11", 1000,
            first, last, 0, [], false, 0, 0, 0, Now, DatasetState.ACCEPTED, null, []));
        return store.ById(id)!;
    }

    /// <summary>
    /// A dataset the ledger recorded with its files on disk, so <c>DatasetStore.Checked</c> accepts it — the bars it names are the
    /// row's, the file a short stand-in the hash is taken of.
    /// </summary>
    static DatasetRecord OnDisk(Database db, string pair, DateTimeOffset first, DateTimeOffset last, string version = "v1")
    {
        var dir = BinanceArchive.DatasetDir(pair);
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        var raw = Path.Combine(dir, "raw", $"{pair}-{Guid.NewGuid():n}.zip");
        File.WriteAllText(raw, "a stand-in for the vendor's monthly archive");
        var csv = Path.Combine(dir, $"{Guid.NewGuid():n}.csv");
        File.WriteAllText(csv, new StringBuilder().Append(KlineNormaliser.Header).Append('\n')
            .Append(CultureInfo.InvariantCulture, $"{first.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},100,101,99,100,1.00\n").ToString());

        var store = new DatasetStore(db);
        var id = store.Record(new DatasetRecord(
            0, BinanceArchive.Source, pair, BinanceArchive.Interval, version, 12, 12, [],
            csv, DatasetStore.Sha256(csv)!, 1000, first, last, 0, [], false, 0, 0, 0, Now, DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-09", "https://127.0.0.1/x.zip", DatasetStore.Sha256(raw)!, DatasetStore.Sha256(raw)!,
                new FileInfo(raw).Length, first, KlineTimeUnit.Microseconds, raw)]));
        return store.ById(id)!;
    }

    /// <summary>One recorded version, so a verdict has a row to charge against.</summary>
    static string Version(Database db, string id)
    {
        new StrategyStore(db).RecordVersion(new StrategyVersionRow(
            id, "instrument BTCUSDT\n", "canonical", "{}", StrategyStore.InterpreterBuild,
            ParseVerdict.Accepted, 0, Now, CouncilRoles.Research, "attempt-1"));
        return id;
    }

    /// <summary>A gateway whose campaigns allow one judgement, and campaign 1 opened over the first download by the owner's press.</summary>
    static async Task<(TradingGateway Gw, Database Db, DatasetRecord D1, CampaignRow K1)> SpentOnce(string pair)
    {
        var (gw, _, db) = await TestEnv.Ready(s => { s.CampaignVerdictBudget = 1; s.CampaignTrialBudget = 5; });
        var d1 = Recorded(db, pair, First, Last);
        var (done, k1) = gw.SetHoldout(d1.Id, DefaultCutoff, EvaluationClass.Research);
        Assert.True(done.Ok, done.Why);
        Assert.True(gw.Campaigns.ChargeVerdict(k1!.Id, Version(db, "version-judged"), Now).Ok);
        return (gw, db, gw.Datasets.ById(d1.Id)!, k1);
    }

    // ---- item 1: setup's one press ---------------------------------------------------------------

    /// <summary>
    /// THE DEFAULT DOWNLOAD IS THIRTY-SIX COMPLETE MONTHS AND ITS LAST TWELVE ARE HELD BACK (<c>U-evidence-holdout-length</c>; R25 D12,
    /// decided on the owner's behalf) — A HOLDOUT THE REFEREE CAN BE CONCLUSIVE OVER, for every month a download can end in, leap years
    /// included: the span <see cref="Inconclusive"/> reads (the cutoff to the last bar's close) is at least
    /// <see cref="Inconclusive.ShortHoldout"/>, and the known days the α-test counts — <see cref="DailyReturns"/>' UTC days over one bar
    /// a day from the cutoff to that close — are at least <see cref="AlphaWealth.MinHoldoutDays"/>; twenty-four research months stand
    /// before it. Nothing for a download of twelve months, which would leave none to research. RED on the base: three months held back
    /// span 91 to 92 days.
    /// </summary>
    [Fact]
    public void The_default_holds_back_the_last_twelve_of_thirty_six_months_a_span_the_referee_can_be_conclusive_over()
    {
        var daily = BarGrid.Uniform(TimeSpan.FromDays(1));
        for (var newest = new DateOnly(2024, 1, 1); newest <= new DateOnly(2040, 12, 1); newest = newest.AddMonths(1))
        {
            var asked = BinanceArchive.RecentCompleteMonths(new DateTimeOffset(newest.AddMonths(1).ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero));
            var first = new DateTimeOffset(asked[0].ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var close = new DateTimeOffset(asked[^1].AddMonths(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

            var cutoff = EvidenceDefaults.Cutoff(Row(first, close.AddMinutes(-1)));
            Assert.True(cutoff is not null, $"a download ending {newest:yyyy-MM} holds back nothing by default");
            var span = close - cutoff!.Value;
            Assert.True(span >= Inconclusive.ShortHoldout, $"a download ending {newest:yyyy-MM} holds back {span.TotalDays} days");

            var bars = new List<BacktestEvent>();
            for (var open = cutoff.Value; open < close; open = open.AddDays(1))
                bars.Add(new BacktestEvent { Ordinal = bars.Count + 1, Bar = open, Kind = BacktestEventKind.Bar, Status = "Idle", Equity = 10_000m + bars.Count });
            var known = Deflation.Known(DailyReturns.Of(new BacktestTrace(bars), daily, 10_000m)).Count;
            Assert.True(known >= AlphaWealth.MinHoldoutDays, $"a download ending {newest:yyyy-MM} holds back {known} known days");

            Assert.Equal(first.AddMonths(BinanceArchive.MonthsWanted - EvidenceDefaults.HoldoutMonths), cutoff);
        }

        Assert.Equal(DefaultCutoff, EvidenceDefaults.Cutoff(Row(First, Last)));
        Assert.Null(EvidenceDefaults.Cutoff(Row(DefaultCutoff, Last)));
        Assert.Equal((36, 12), (BinanceArchive.MonthsWanted, EvidenceDefaults.HoldoutMonths));
    }

    /// <summary>A dataset row over the bars named, never stored — what <see cref="EvidenceDefaults.Cutoff"/> reads.</summary>
    static DatasetRecord Row(DateTimeOffset first, DateTimeOffset last) => new(
        0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", BinanceArchive.MonthsWanted, BinanceArchive.MonthsWanted, [],
        "not-read-here.csv", "aa11", 1000, first, last, 0, [], false, 0, 0, 0, Now, DatasetState.ACCEPTED, null, []);

    /// <summary>
    /// A DOWNLOAD THAT STOPPED PART WAY HOLDS NOTHING BACK, AND THE NEXT PRESS FINISHES IT (<c>U-evidence-holdout-length</c> item 1): a
    /// month that did not arrive — here one the vendor could not be reached for — leaves the pair with no cutoff and no campaign, and the
    /// words say how much arrived and that pressing again fetches only the rest; the next press downloads again and holds back the
    /// default over the whole download. RED on the base, which held back the last months of whatever arrived.
    /// </summary>
    [Fact]
    public async Task A_download_that_stopped_part_way_holds_nothing_back_and_the_next_press_finishes_it()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var owned = db;
        await using var gateway = gw;
        var pair = TestEnv.NewPair();
        var presses = 0;
        var evidence = new EvidenceCampaigns(() => gw, (p, _, _, _) =>
        {
            var n = ++presses;
            var months = BinanceArchive.RecentCompleteMonths(Now).Select((m, i) => new MonthResult(BinanceArchive.MonthName(m),
                n == 1 && i == 20 ? MonthOutcome.Unreachable : MonthOutcome.Collected, null, "")).ToList();
            return Task.FromResult(new DataCollection(OnDisk(db, p, First, Last, $"v{n}"), months,
                n == 1 ? "35 of 36 periods collected (1 missing, 1 of them because the source could not be reached)." : "36 of 36 periods collected."));
        });

        var first = await evidence.SetUpAsync(Now, [pair]);
        Assert.Empty(first.Campaigns);
        Assert.Empty(gw.Campaigns.All());
        Assert.Null(gw.Datasets.Newest(pair)!.HoldoutFrom);
        Assert.Contains("35 of 36 periods collected", first.Words, StringComparison.Ordinal);
        Assert.Contains("Press again", first.Words, StringComparison.Ordinal);
        Assert.Equal([pair], first.Unfinished);

        var second = await evidence.SetUpAsync(Now, [pair]);
        Assert.Equal(2, presses);
        Assert.Equal(DefaultCutoff, Assert.Single(second.Campaigns).HoldoutFrom);
        Assert.Empty(second.Unfinished);
    }

    /// <summary>
    /// A CUTOFF ALREADY ON THE DOWNLOAD IS KEPT: setup opens the campaign at it and never moves it. With no campaign holding it, the
    /// store would let a LATER date through — releasing months the owner held back to every reader.
    /// </summary>
    [Fact]
    public async Task Setup_opens_the_campaign_at_a_cutoff_already_set_and_never_moves_it()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var owned = db;
        await using var gateway = gw;
        gw.Update(s => s.MarketDataPair = TestEnv.NewPair());
        var set = OnDisk(db, gw.Settings.MarketDataPair, First, Last);
        var his = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.True(gw.Datasets.SetHoldout(set.Id, his, EvaluationClass.Research).Ok);

        var evidence = new EvidenceCampaigns(() => gw, (_, _, _, _) => throw new InvalidOperationException("nothing is downloaded"));
        var done = await evidence.SetUpAsync(Now, [gw.Settings.MarketDataPair]);

        Assert.True(done.Ok, done.Words);
        Assert.Equal(his, Assert.Single(done.Campaigns).HoldoutFrom);
        Assert.Equal(his, gw.Datasets.ById(set.Id)!.HoldoutFrom);
        Assert.True(evidence.Ready());
    }

    /// <summary>
    /// WITH NO DOWNLOAD, THE PRESS DOWNLOADS, HOLDS BACK THE DEFAULT AND OPENS ONE CAMPAIGN; A SECOND PRESS CHANGES NOTHING.
    /// </summary>
    [Fact]
    public async Task Setup_downloads_holds_back_the_default_and_a_second_press_changes_nothing()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var owned = db;
        await using var gateway = gw;
        gw.Update(s => s.MarketDataPair = TestEnv.NewPair());
        var asked = 0;
        var evidence = new EvidenceCampaigns(() => gw, (pair, _, _, _) =>
        {
            asked++;
            return Task.FromResult(new DataCollection(OnDisk(db, pair, First, Last), [], "collected"));
        });

        var first = await evidence.SetUpAsync(Now, [gw.Settings.MarketDataPair]);
        Assert.True(first.Ok, first.Words);
        Assert.Equal(DefaultCutoff, Assert.Single(first.Campaigns).HoldoutFrom);
        Assert.Contains("final judgements", first.Words, StringComparison.Ordinal);

        var second = await evidence.SetUpAsync(Now, [gw.Settings.MarketDataPair]);
        Assert.True(second.Ok);
        Assert.Empty(second.Campaigns);
        Assert.Equal(1, asked);
        Assert.Single(gw.Campaigns.All());
    }

    /// <summary>
    /// SETUP HOLDS BACK EACH TICKED PAIR UNDER ITS OWN CAMPAIGN (<c>U-evidence-breadth</c> item 1): two pairs ticked of three — each
    /// downloaded, its instrument checked BEFORE its holdout, held back from its own default and given its own campaign with the
    /// owner's budgets; the unticked pair is neither downloaded nor held. A judgement on one spends none of the other's. A second
    /// press with all three ticked sets up only the third: a pair already under a campaign is left alone. RED on the base, which
    /// had one pair and no list (CS1729: no constructor taking the instrument check).
    /// </summary>
    [Fact]
    public async Task Setup_holds_back_each_ticked_pair_under_its_own_campaign_and_leaves_the_rest()
    {
        var (gw, _, db) = await TestEnv.Ready(s => { s.CampaignTrialBudget = 7; s.CampaignVerdictBudget = 2; });
        using var owned = db;
        await using var gateway = gw;
        var (a, b, c) = (TestEnv.NewPair(), TestEnv.NewPair(), TestEnv.NewPair());
        var said = new List<string>();
        var evidence = new EvidenceCampaigns(() => gw,
            (pair, _, _, _) =>
            {
                said.Add("collect " + pair);
                return Task.FromResult(new DataCollection(OnDisk(db, pair, First, Last), [], "collected"));
            },
            (pair, _) =>
            {
                said.Add($"check {pair} held={gw.Campaigns.All().Any(k => gw.Datasets.ById(k.HoldoutDatasetId)?.Pair == pair)}");
                return Task.CompletedTask;
            });

        var first = await evidence.SetUpAsync(Now, [a, b]);
        Assert.True(first.Ok, first.Words);
        Assert.Equal([$"collect {a}", $"check {a} held=False", $"collect {b}", $"check {b} held=False"], said);
        Assert.Equal(2, first.Campaigns.Count);
        Assert.Equal([a, b], first.Campaigns.Select(k => gw.Datasets.ById(k.HoldoutDatasetId)!.Pair));
        Assert.All(first.Campaigns, k => Assert.Equal((DefaultCutoff, 7, 2), (k.HoldoutFrom, k.TrialBudget, k.VerdictBudget)));
        Assert.Null(gw.Datasets.Newest(c));
        Assert.Equal([a, b], evidence.Pairs());

        Assert.True(gw.Campaigns.ChargeVerdict(first.Campaigns[1].Id, Version(db, "version-b"), Now).Ok);
        Assert.Equal(0, gw.Campaigns.JudgementsSpent(first.Campaigns[0].Id));

        said.Clear();
        var second = await evidence.SetUpAsync(Now, [a, b, c]);
        Assert.True(second.Ok, second.Words);
        Assert.Equal(c, gw.Datasets.ById(Assert.Single(second.Campaigns).HoldoutDatasetId)!.Pair);
        Assert.Equal([$"collect {c}", $"check {c} held=False"], said);
        Assert.Contains($"{a}: a campaign is already open", second.Words, StringComparison.Ordinal);
        Assert.Equal(3, gw.Campaigns.All().Count);
    }

    /// <summary>
    /// TWO OVERLAPPING SETUP PRESSES OPEN ONE CAMPAIGN (the Codex review's P2): the second, pressed while the first is still
    /// downloading, starts nothing and says so; the first opens the pair's one campaign. RED without the one-press guard, where
    /// each press downloaded the pair.
    /// </summary>
    [Fact]
    public async Task Two_overlapping_setup_presses_open_one_campaign()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var owned = db;
        await using var gateway = gw;
        var pair = TestEnv.NewPair();
        var release = new TaskCompletionSource();
        var downloads = 0;
        var evidence = new EvidenceCampaigns(() => gw, async (p, _, _, _) =>
        {
            var n = ++downloads;
            await release.Task;
            return new DataCollection(OnDisk(db, p, First, Last, $"v{n}"), [], "collected");
        });

        var first = evidence.SetUpAsync(Now, [pair]);
        var second = evidence.SetUpAsync(Now, [pair]);
        release.SetResult();
        var done = await Task.WhenAll(first, second);

        Assert.True(downloads == 1, $"two overlapping presses downloaded {pair} {downloads} times");
        Assert.Single(gw.Campaigns.All());
        Assert.Single(done[0].Campaigns);
        Assert.Empty(done[1].Campaigns);
        Assert.Contains("already downloading", done[1].Words, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE SETUP STEP STATES THE DOWNLOAD'S SIZE AND TIME (<c>U-evidence-holdout-length</c> item 1), from the archive's own sizes measured for
    /// every default pair on 2026-10-31 — a pair not measured counted at their average — and what a download that stops part way costs.
    /// </summary>
    [Fact]
    public void The_setup_step_states_the_measured_size_and_time_of_the_download()
    {
        Assert.Equal(EvidenceDefaults.Pairs.Order(StringComparer.Ordinal), EvidenceDefaults.MeasuredMegabytes.Keys.Order(StringComparer.Ordinal));

        var five = EvidenceDefaults.DownloadWords(EvidenceDefaults.Offered("BTCUSDT"));
        Assert.StartsWith("About 341 MB for the 5 pairs offered here (about 68 MB a pair), and about 1.0 GB on disk once unpacked. Measured "
            + "on 2026-10-31, the archive arrived at about 0.36 MB a second, so this takes about 16 minutes there", five, StringComparison.Ordinal);
        Assert.Contains("press again: the months already checked are kept", five, StringComparison.Ordinal);
        Assert.StartsWith("About 409 MB for the 6 pairs offered here", EvidenceDefaults.DownloadWords(EvidenceDefaults.Offered("ADAUSDT")),
            StringComparison.Ordinal);
    }

    /// <summary>WHAT SETUP OFFERS: the owner's Settings pair first, then the five default pairs, each once.</summary>
    [Fact]
    public void Setup_offers_the_settings_pair_first_then_the_default_pairs_once_each()
    {
        Assert.Equal(["BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT"], EvidenceDefaults.Offered("BTCUSDT"));
        Assert.Equal(["ADAUSDT", "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT"], EvidenceDefaults.Offered("ADAUSDT"));
        Assert.Equal(EvidenceDefaults.Pairs, EvidenceDefaults.Offered("not a pair"));
    }

    // ---- item 2: the fresh holdout ----------------------------------------------------------------

    /// <summary>
    /// A SPENT CAMPAIGN ARMS ITS FRESH HOLDOUT, AND FROM THAT MOMENT EVERY PIPE READ REACHING IT IS REFUSED — the forward bars and a
    /// download holding no cutoff alike — while the paper runner, which trades the present, still reads it.
    /// </summary>
    [Fact]
    public async Task A_spent_campaign_arms_a_fresh_holdout_every_pipe_read_is_refused_and_the_paper_runner_is_not()
    {
        var pair = TestEnv.NewPair();
        var (gw, db, _, k1) = await SpentOnce(pair);
        using var owned = db;
        await using var gateway = gw;
        var pipe = TapeHoldout.Pipe(CouncilRoles.Research, gw.Datasets);
        var later = Recorded(db, pair, First.AddMonths(2), Fresh.AddDays(5), "v2");

        // BEFORE: nothing holds the minutes after the first download's window.
        Assert.Null(pipe.ForwardRefusal(pair, Fresh.AddHours(1), Fresh.AddHours(2)));
        Assert.Null(pipe.Refusal(later, Fresh.AddHours(1), Fresh.AddHours(2)));

        var armed = Assert.Single(gw.Renewals.Arm(Now));
        Assert.Equal(k1.Id, armed.SpentCampaignId);
        Assert.Equal(Fresh.AddDays(6), armed.Cutoff);

        // AFTER: refused from the cutoff on, served before it; the paper runner reads it.
        var refused = pipe.ForwardRefusal(pair, armed.Cutoff.AddHours(1), armed.Cutoff.AddHours(2));
        Assert.NotNull(refused);
        Assert.Contains($"campaign {k1.Id} has spent its attempts", refused, StringComparison.Ordinal);
        Assert.NotNull(pipe.Refusal(armed.Cutoff.AddHours(1), null));
        Assert.Null(pipe.ForwardRefusal(pair, Fresh, Fresh.AddHours(1)));
        Assert.Empty(TapeHoldout.Of(BarAudience.PaperRunner, gw.Datasets).Reached(armed.Cutoff.AddHours(1), armed.Cutoff.AddHours(2)));
    }

    /// <summary>
    /// THE FRESH CUTOFF IS THE FIRST MIDNIGHT AFTER EVERYTHING ON RECORD — here a download whose last bar closes after the clock — and
    /// it is written once: a campaign followed already is never armed again, so no cutoff moves.
    /// </summary>
    [Fact]
    public async Task The_fresh_cutoff_is_after_everything_on_record_and_is_written_once()
    {
        var pair = TestEnv.NewPair();
        var (gw, db, _, k1) = await SpentOnce(pair);
        using var owned = db;
        await using var gateway = gw;
        Recorded(db, TestEnv.NewPair(), First, new DateTimeOffset(2026, 11, 2, 13, 0, 0, TimeSpan.Zero), "v9");

        var armed = Assert.Single(gw.Renewals.Arm(Now));
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero), armed.Cutoff);

        // FOLLOWED, AND STILL SPENT: a later pass arms nothing and the recorded cutoff stands.
        var fresh = Recorded(db, pair, First.AddMonths(3), armed.Cutoff.AddDays(40), "v2");
        Assert.True(gw.OpenForwardCampaign(k1.Id, fresh.Id).Ok);
        Assert.Empty(gw.Renewals.Arm(Now.AddDays(60)));
        Assert.Equal(armed.Cutoff, gw.Renewals.Of(k1.Id)!.Cutoff);
    }

    /// <summary>
    /// THE NEXT CAMPAIGN OPENS ONLY ON THIRTY DAYS OF FRESH BARS, says how many it has, and writes nothing until then. Once open it
    /// judges from the armed cutoff with its whole budget, the spent campaign keeps every judgement it took, and the window is no
    /// longer held open-ended.
    /// </summary>
    [Fact]
    public async Task The_next_campaign_opens_only_on_thirty_days_of_fresh_bars_and_says_how_much_it_waits_for()
    {
        var pair = TestEnv.NewPair();
        var (gw, db, _, k1) = await SpentOnce(pair);
        using var owned = db;
        await using var gateway = gw;
        var armed = Assert.Single(gw.Renewals.Arm(Now));

        var shortOne = Recorded(db, pair, First.AddMonths(1), armed.Cutoff.AddDays(29).AddMinutes(-1), "v2");
        var refused = gw.OpenForwardCampaign(k1.Id, shortOne.Id);
        Assert.False(refused.Ok);
        Assert.Contains("at least 30 days", refused.Why, StringComparison.Ordinal);
        Assert.Contains("29 of 30 so far", refused.Why, StringComparison.Ordinal);
        Assert.Null(gw.Datasets.ById(shortOne.Id)!.HoldoutFrom);
        Assert.Single(gw.Campaigns.All());

        var enough = Recorded(db, pair, First.AddMonths(2), armed.Cutoff.AddDays(31), "v3");
        var opened = gw.OpenForwardCampaign(k1.Id, enough.Id);
        Assert.True(opened.Ok, opened.Why);
        var k2 = opened.Campaign!;
        Assert.Equal(armed.Cutoff, k2.HoldoutFrom);
        Assert.Null(k2.RenewedFrom);
        Assert.Equal(0, gw.Campaigns.JudgementsSpent(k2.Id));
        Assert.Empty(gw.Campaigns.JudgementsCharged(k2.Id));
        Assert.Equal(1, gw.Campaigns.JudgementsSpent(k1.Id));
        Assert.True(gw.Campaigns.ById(k1.Id)!.IsOpen);
        Assert.Equal(k2.Id, gw.Renewals.Of(k1.Id)!.Successor);
        Assert.Empty(gw.Renewals.Holding());

        // AND IT IS FOLLOWED ONCE.
        Assert.False(gw.OpenForwardCampaign(k1.Id, Recorded(db, pair, First, armed.Cutoff.AddDays(60), "v4").Id).Ok);
    }

    /// <summary>
    /// NO CAMPAIGN OPENS OVER MONTHS ANOTHER WINDOW OF ITS PAIR STILL HOLDS PAST THE CUTOFF, nor over another series: those months
    /// are not fresh for that pair. Another pair's window past the cutoff does not stop it (<c>U-evidence-breadth</c>): its months
    /// are that pair's campaign's budget, and still unread by research on every pair.
    /// </summary>
    [Fact]
    public async Task No_campaign_opens_over_months_another_window_holds_past_the_cutoff()
    {
        var pair = TestEnv.NewPair();
        var (gw, db, _, k1) = await SpentOnce(pair);
        using var owned = db;
        await using var gateway = gw;
        var armed = Assert.Single(gw.Renewals.Arm(Now));
        var fresh = Recorded(db, pair, First.AddMonths(2), armed.Cutoff.AddDays(40), "v2");

        // ANOTHER PAIR'S WINDOW PAST THE CUTOFF: not this pair's months, so no refusal.
        var elsewhereHeld = Recorded(db, TestEnv.NewPair(), First, armed.Cutoff.AddDays(60), "v1");
        Assert.True(gw.Datasets.SetHoldout(elsewhereHeld.Id, armed.Cutoff.AddDays(10), EvaluationClass.Research).Ok);
        Assert.Null(gw.Renewals.OpenRefusal(k1.Id, gw.Datasets.ById(fresh.Id)));

        // THE PAIR'S OWN OTHER WINDOW PAST THE CUTOFF: refused, naming it.
        var other = Recorded(db, pair, First, armed.Cutoff.AddDays(60), "v9");
        Assert.True(gw.Datasets.SetHoldout(other.Id, armed.Cutoff.AddDays(10), EvaluationClass.Research).Ok);
        var refused = gw.OpenForwardCampaign(k1.Id, fresh.Id);
        Assert.False(refused.Ok);
        Assert.Contains($"dataset {other.Id}", refused.Why, StringComparison.Ordinal);
        Assert.Contains("not fresh", refused.Why, StringComparison.Ordinal);

        var elsewhere = Recorded(db, TestEnv.NewPair(), First.AddMonths(2), armed.Cutoff.AddDays(40), "v1");
        Assert.Contains("same series", gw.OpenForwardCampaign(k1.Id, elsewhere.Id).Why, StringComparison.Ordinal);
        Assert.Null(gw.Datasets.ById(fresh.Id)!.HoldoutFrom);
    }

    /// <summary>
    /// EACH PAIR'S SPENT CAMPAIGN ARMS ITS OWN FRESH HOLDOUT, ONE WINDOW AT A TIME PER PAIR (<c>U-evidence-breadth</c>): a spent
    /// campaign of a second pair arms in the same pass as the first pair's — never waiting a month for the first pair's successor —
    /// at a cutoff after the first's, while a second campaign of the FIRST pair, spent by the judgement its pair already took, waits
    /// for its pair's window. Every armed window still holds every pair's bars. RED on the base, where one window held them all
    /// and the second pair's spent campaign was not armed.
    /// </summary>
    [Fact]
    public async Task Each_pairs_spent_campaign_arms_its_own_fresh_holdout_one_window_at_a_time_per_pair()
    {
        var pair = TestEnv.NewPair();
        var (gw, db, _, k1) = await SpentOnce(pair);
        using var owned = db;
        await using var gateway = gw;

        var second = TestEnv.NewPair();
        var (heldOther, j1) = gw.SetHoldout(Recorded(db, second, First, Last).Id, DefaultCutoff, EvaluationClass.Research);
        Assert.True(heldOther.Ok, heldOther.Why);
        Assert.True(gw.Campaigns.ChargeVerdict(j1!.Id, Version(db, "version-second-pair"), Now).Ok);

        // A SECOND CAMPAIGN OF THE FIRST PAIR over the same months: spent already by the judgement its pair took.
        var (heldAgain, k2) = gw.SetHoldout(Recorded(db, pair, First, Last, "v2").Id, DefaultCutoff, EvaluationClass.Research);
        Assert.True(heldAgain.Ok, heldAgain.Why);
        Assert.Equal(1, gw.Campaigns.JudgementsSpent(k2!.Id));

        var armed = gw.Renewals.Arm(Now);
        Assert.True(armed.Select(a => a.SpentCampaignId).SequenceEqual([k1.Id, j1.Id]),
            $"one pass armed campaigns [{string.Join(", ", armed.Select(a => a.SpentCampaignId))}], not each pair's spent campaign "
            + $"[{k1.Id}, {j1.Id}] with campaign {k2.Id} waiting for its pair's window");
        Assert.True(armed[1].Cutoff > armed[0].Cutoff);
        Assert.Null(gw.Renewals.Of(k2.Id));
        Assert.Empty(gw.Renewals.Arm(Now.AddDays(1)));

        // EVERY WINDOW STILL HOLDS EVERY PAIR: the first pair's fresh months are refused on the second pair's bars, and back.
        var pipe = TapeHoldout.Pipe(CouncilRoles.Research, gw.Datasets);
        Assert.NotNull(pipe.ForwardRefusal(second, armed[0].Cutoff.AddHours(1), armed[0].Cutoff.AddHours(2)));
        Assert.NotNull(pipe.ForwardRefusal(pair, armed[1].Cutoff.AddHours(1), armed[1].Cutoff.AddHours(2)));
    }

    /// <summary>
    /// A DATASET ROW READ BEFORE ITS FRESH CAMPAIGN OPENED STILL SERVES NONE OF ITS HELD BARS (the Codex review's P1): the caller's row
    /// carries no cutoff, the fresh window has retired, and the dataset's own new window is asked because the row does not carry it.
    /// </summary>
    [Fact]
    public async Task A_row_read_before_its_fresh_campaign_opened_still_serves_none_of_its_held_bars()
    {
        var pair = TestEnv.NewPair();
        var (gw, db, _, k1) = await SpentOnce(pair);
        using var owned = db;
        await using var gateway = gw;
        var armed = Assert.Single(gw.Renewals.Arm(Now));
        var stale = Recorded(db, pair, First.AddMonths(2), armed.Cutoff.AddDays(40), "v2");
        Assert.Null(stale.HoldoutFrom);

        Assert.True(gw.OpenForwardCampaign(k1.Id, stale.Id).Ok);
        Assert.Empty(gw.Renewals.Holding());

        var refused = TapeHoldout.Pipe(CouncilRoles.Research, gw.Datasets).Refusal(stale, armed.Cutoff.AddHours(1), armed.Cutoff.AddHours(2));
        Assert.NotNull(refused);
        Assert.Contains($"dataset {stale.Id}", refused, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE OWNER'S PAGE SHOWS EACH PAIR'S CAMPAIGN (<c>U-evidence-breadth</c> item 4): what it has left of its judgements and research
    /// runs, and — once spent — what its pair's next campaign waits for, from when, and how many days that is from now.
    /// </summary>
    [Fact]
    public async Task The_owner_page_shows_each_pairs_campaign_what_it_has_left_and_what_its_renewal_waits_for()
    {
        var pair = TestEnv.NewPair();
        var (gw, db, _, k1) = await SpentOnce(pair);
        using var owned = db;
        await using var gateway = gw;
        var second = TestEnv.NewPair();
        var (held, j1) = gw.SetHoldout(Recorded(db, second, First, Last).Id, DefaultCutoff, EvaluationClass.Research);
        Assert.True(held.Ok, held.Why);
        var evidence = new EvidenceCampaigns(() => gw, (_, _, _, _) => throw new InvalidOperationException("nothing is downloaded"));

        var before = evidence.Standings().ToDictionary(r => r.Pair, r => r.Line(Now));
        Assert.StartsWith($"{pair}, campaign {k1.Id}, held back from 2025-10-01 UTC: 0 of 1 final judgements and 5 of 5 research runs "
            + "left. Its holdout spans 365 days — long enough for the referee to reach a conclusive verdict. Its final judgements are spent. TradeAgent arms its fresh holdout within minutes", before[pair],
            StringComparison.Ordinal);

        var armed = Assert.Single(gw.Renewals.Arm(Now));
        var lines = evidence.Standings().ToDictionary(r => r.Pair, r => r.Line(Now));
        Assert.Equal(2, lines.Count);
        Assert.Equal($"{second}, campaign {j1!.Id}, held back from 2025-10-01 UTC: 1 of 1 final judgements and 5 of 5 research runs left. "
            + "Its holdout spans 365 days — long enough for the referee to reach a conclusive verdict.", lines[second]);
        Assert.Equal($"{pair}, campaign {k1.Id}, held back from 2025-10-01 UTC: 0 of 1 final judgements and 5 of 5 research runs left. "
            + "Its holdout spans 365 days — long enough for the referee to reach a conclusive verdict. "
            + $"Its final judgements are spent; the next {pair} campaign waits for fresh bars from {armed.Cutoff:yyyy-MM-dd} UTC — at least "
            + $"30 days of them — so it opens no earlier than {armed.Earliest:yyyy-MM-dd} (31 days from now).", lines[pair]);
    }

    /// <summary>
    /// THE OWNER'S PAGE SAYS, PER CAMPAIGN, WHETHER ITS HOLDOUT CAN YIELD A CONCLUSIVE VERDICT (<c>U-evidence-holdout-length</c> item 3):
    /// a campaign holding back twelve months — 365 days to its dataset's last close — can; one holding back three (92 days: a later
    /// cutoff the owner typed in Settings, or a renewal's fresh month) says no verdict over it is conclusive and names the inconclusive
    /// path. RED on the base, which said neither.
    /// </summary>
    [Fact]
    public async Task The_owner_page_says_per_campaign_whether_its_holdout_can_yield_a_conclusive_verdict()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var owned = db;
        await using var gateway = gw;
        var (year, quarter) = (TestEnv.NewPair(), TestEnv.NewPair());
        var (heldYear, _) = gw.SetHoldout(Recorded(db, year, First, Last).Id, DefaultCutoff, EvaluationClass.Research);
        Assert.True(heldYear.Ok, heldYear.Why);
        var (heldQuarter, _) = gw.SetHoldout(Recorded(db, quarter, First, Last).Id, new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            EvaluationClass.Research);
        Assert.True(heldQuarter.Ok, heldQuarter.Why);
        var evidence = new EvidenceCampaigns(() => gw, (_, _, _, _) => throw new InvalidOperationException("nothing is downloaded"));

        var lines = evidence.Standings().ToDictionary(r => r.Pair, r => r.Line(Now));
        Assert.Contains("Its holdout spans 365 days — long enough for the referee to reach a conclusive verdict.", lines[year],
            StringComparison.Ordinal);
        Assert.DoesNotContain("inconclusive", lines[year], StringComparison.Ordinal);
        Assert.Contains("Its holdout spans 92 days, under 365, so no verdict over it is conclusive", lines[quarter], StringComparison.Ordinal);
        Assert.Contains("reaches paper only through the inconclusive path", lines[quarter], StringComparison.Ordinal);
    }

    /// <summary>
    /// THE SETTINGS PAGE SAYS HOW LONG A TYPED HOLDOUT WOULD BE, BEFORE EITHER PRESS (<c>U-evidence-holdout-length</c> item 1): the owner may
    /// hold back less than setup's twelve months, and the page then says plainly that no verdict over it can be conclusive and names the
    /// inconclusive path; twelve months say a conclusive verdict is possible; a date past the download's end, or no download, say nothing.
    /// </summary>
    [Fact]
    public void The_settings_page_says_how_long_a_typed_holdout_would_be_and_whether_a_verdict_over_it_can_be_conclusive()
    {
        var set = Row(First, Last);

        var shorter = SettingsPage.HoldoutLengthNote(set, new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero))!.Value;
        Assert.False(shorter.Conclusive);
        Assert.StartsWith("A holdout from 2026-07-01 00:00 UTC on BTCUSDT's newest download spans 92 days, under 365, so no verdict over it "
            + "is conclusive", shorter.Words, StringComparison.Ordinal);
        Assert.Contains("reaches paper only through the inconclusive path", shorter.Words, StringComparison.Ordinal);

        var year = SettingsPage.HoldoutLengthNote(set, DefaultCutoff)!.Value;
        Assert.True(year.Conclusive);
        Assert.Equal("A holdout from 2025-10-01 00:00 UTC on BTCUSDT's newest download spans 365 days — long enough for the referee to "
            + "reach a conclusive verdict.", year.Words);

        Assert.Null(SettingsPage.HoldoutLengthNote(set, Last.AddMinutes(1)));
        Assert.Null(SettingsPage.HoldoutLengthNote(null, DefaultCutoff));
    }

    /// <summary>A SPENT CAMPAIGN'S REFUSAL SAYS WHAT FOLLOWS IT: the fresh cutoff and the thirty days it waits for.</summary>
    [Fact]
    public async Task A_refused_judgement_names_the_fresh_months_it_waits_for()
    {
        var pair = TestEnv.NewPair();
        var (gw, db, _, k1) = await SpentOnce(pair);
        using var owned = db;
        await using var gateway = gw;
        var armed = Assert.Single(gw.Renewals.Arm(Now));

        var refused = gw.Campaigns.ChargeVerdict(k1.Id, Version(db, "version-late"), Now);
        Assert.False(refused.Ok);
        Assert.Contains($"every bar from {armed.Cutoff:u} on", refused.Why, StringComparison.Ordinal);
        Assert.Contains("at least 30 days", refused.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE APP'S PASS, END TO END: it arms, waits, asks again no sooner than a day after a download too short, and opens the next
    /// campaign over one that holds enough — saying each in the activity lines it returns.
    /// </summary>
    [Fact]
    public async Task The_pass_arms_waits_retries_a_day_later_and_opens_the_next_campaign()
    {
        var pair = TestEnv.NewPair();
        var (gw, db, _, k1) = await SpentOnce(pair);
        using var owned = db;
        await using var gateway = gw;
        var ends = new Queue<DateTimeOffset>([Fresh.AddDays(29), Fresh.AddDays(32)]);
        var asked = 0;
        var evidence = new EvidenceCampaigns(() => gw, (p, _, _, _) =>
        {
            asked++;
            return Task.FromResult(new DataCollection(Recorded(db, p, First.AddMonths(asked), ends.Dequeue(), $"v{asked + 1}"), [], ""));
        });

        Assert.Contains("TradeAgent opens the next campaign by itself", Assert.Single(await evidence.RenewAsync(Now)), StringComparison.Ordinal);
        Assert.Empty(await evidence.RenewAsync(Now.AddDays(5)));

        Assert.Contains("did not open yet", Assert.Single(await evidence.RenewAsync(Fresh.AddDays(30))), StringComparison.Ordinal);
        Assert.Empty(await evidence.RenewAsync(Fresh.AddDays(30).AddHours(23)));

        var opened = Assert.Single(await evidence.RenewAsync(Fresh.AddDays(31)));
        Assert.Contains("now judges strategies on fresh market data", opened, StringComparison.Ordinal);
        Assert.Equal(2, asked);
        Assert.NotNull(gw.Renewals.Of(k1.Id)!.Successor);
    }
}

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

    /// <summary>The first download: October 2025 to September 2026, minute bars.</summary>
    static readonly DateTimeOffset First = new(2025, 10, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Last = new(2026, 9, 30, 23, 59, 0, TimeSpan.Zero);

    /// <summary>The default holdout of that download: July, August and September 2026.</summary>
    static readonly DateTimeOffset DefaultCutoff = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

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
    static DatasetRecord OnDisk(Database db, string pair, DateTimeOffset first, DateTimeOffset last)
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
            0, BinanceArchive.Source, pair, BinanceArchive.Interval, "v1", 12, 12, [],
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
    /// THE DEFAULT HOLDS BACK THE LAST THREE COMPLETE MONTHS, FROM A MONTH'S FIRST INSTANT, UTC — and nothing for a download too
    /// short to keep a research month before them.
    /// </summary>
    [Fact]
    public void The_default_holds_back_the_last_three_complete_months_and_keeps_research_months_before_them()
    {
        using var db = TestEnv.NewDb();

        Assert.Equal(DefaultCutoff, EvidenceDefaults.Cutoff(Recorded(db, TestEnv.NewPair(), First, Last)));
        Assert.Null(EvidenceDefaults.Cutoff(Recorded(db, TestEnv.NewPair(), new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero), Last)));
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
        var done = await evidence.SetUpAsync(Now);

        Assert.True(done.Ok, done.Words);
        Assert.Equal(his, done.Campaign!.HoldoutFrom);
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

        var first = await evidence.SetUpAsync(Now);
        Assert.True(first.Ok, first.Words);
        Assert.Equal(DefaultCutoff, first.Campaign!.HoldoutFrom);
        Assert.Contains("final judgements", first.Words, StringComparison.Ordinal);

        var second = await evidence.SetUpAsync(Now);
        Assert.True(second.Ok);
        Assert.Null(second.Campaign);
        Assert.Equal(1, asked);
        Assert.Single(gw.Campaigns.All());
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

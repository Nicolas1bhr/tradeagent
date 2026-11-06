using System.Globalization;
using System.Text.RegularExpressions;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A WINDOW THAT CANNOT FIT THE RESEARCH CAP IS REFUSED BEFORE IT STARTS, AND THE REFUSAL TEACHES NOTHING ABOUT THE DATA (<c>U-backtest-cap</c>).
/// </summary>
public class BacktestWindowCapTests
{
    static readonly DateTimeOffset Bar0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    const string Text = "instrument BTCUSDT\nsize fixed 1\nexit when close < 97\nentry when close > 103\n";

    /// <summary>A dataset of whatever minutes it is handed, a day past them held back, and its campaign.</summary>
    static (DatasetRecord Set, CampaignRow Campaign) Given(TradingGateway gw, IEnumerable<int> minutes, int heldFrom)
    {
        var dir = BinanceArchive.DatasetDir("BTCUSDT");
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        var raw = Path.Combine(dir, "raw", $"BTCUSDT-{Guid.NewGuid():n}.zip");
        File.WriteAllText(raw, "a stand-in for the vendor's monthly archive");
        var csv = Path.Combine(dir, $"{Guid.NewGuid():n}.csv");

        var bars = 0;
        var last = 0;
        using (var w = new StreamWriter(csv))
        {
            w.Write(KlineNormaliser.Header);
            w.Write('\n');
            foreach (var minute in minutes.Concat(Enumerable.Range(heldFrom, 1440)))
            {
                var close = 96 + minute % 10;
                w.Write(string.Create(CultureInfo.InvariantCulture,
                    $"{Bar0.AddMinutes(minute).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{close},{close + 1},{close - 1},{close},1.00\n"));
                bars++;
                last = minute;
            }
        }

        var id = gw.Datasets.Record(new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 12, [],
            csv, DatasetStore.Sha256(csv)!, bars, Bar0, Bar0.AddMinutes(last), 0, [], false, 0, 0, 0, Bar0,
            DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-01", "https://127.0.0.1/x.zip", DatasetStore.Sha256(raw)!, DatasetStore.Sha256(raw)!,
                new FileInfo(raw).Length, Bar0, KlineTimeUnit.Microseconds, raw)]));
        var (held, campaign) = gw.SetHoldout(id, Bar0.AddMinutes(heldFrom), EvaluationClass.Research);
        Assert.True(held.Ok, held.Why);
        return (gw.Datasets.ById(id)!, campaign!);
    }

    static string Program(string text = Text)
    {
        var dir = Path.Combine(Paths.RoleHome(CouncilRoles.Research), "strategies");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "capped.strategy"), text);
        return "strategies/capped.strategy";
    }

    static BacktestRan Ask(TradingGateway gw, DatasetRecord set, string path, DateTimeOffset? from, DateTimeOffset? to) =>
        gw.Backtests.Run(AgentContext.ForAgent("agent", CouncilRoles.Research, "attempt-1"),
            new BacktestAsk(path, set.Id, from, to, 0.001m, Increment: 1m));

    /// <summary>
    /// THE CARD: A WINDOW ONE BAR PAST THE CAP IS REFUSED UP FRONT, CHARGES NOTHING AND SAYS WHAT TO DO; ONE EXACTLY AT IT RUNS AND IS CHARGED.
    /// RED on the base: the first came back FAULTED, recorded, with its trial spent and its figures over a period nobody chose.
    /// </summary>
    [Fact]
    public async Task A_window_past_the_cap_is_refused_before_it_starts_and_one_at_it_runs_and_is_charged()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        await using var _2 = gw;
        var (set, campaign) = Given(gw, Enumerable.Range(0, Backtest.MaxTracedBars + 1), Backtest.MaxTracedBars + 1);
        var path = Program();
        var lastResearchMinute = Bar0.AddMinutes(Backtest.MaxTracedBars);

        BacktestRan? ran = null;
        GatewayDeniedException? refused = null;
        try { ran = Ask(gw, set, path, null, lastResearchMinute); }
        catch (GatewayDeniedException ex) { refused = ex; }

        Assert.True(ran is null, $"the run started: outcome {ran?.Result.Outcome}, fault \"{ran?.Result.FaultReason}\", "
            + $"{gw.Campaigns.TrialsCharged(campaign.Id)} trial(s) charged");
        Assert.Equal(ErrorCode.INVALID_REQUEST, refused!.Code);
        Assert.Contains("--from", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was run and no trial was charged.", refused.Message, StringComparison.Ordinal);
        Assert.Empty(gw.Strategies.Runs());
        Assert.Empty(gw.Campaigns.Trials(campaign.Id));

        var fits = Ask(gw, set, path, Bar0.AddMinutes(1), lastResearchMinute);

        Assert.Equal(BacktestOutcome.COMPLETED, fits.Result.Outcome);
        Assert.Equal(Backtest.MaxTracedBars, fits.Result.Metrics.Bars);
        Assert.Null(fits.Result.FaultReason);
        Assert.Equal(1, gw.Campaigns.TrialsCharged(campaign.Id));
    }

    /// <summary>
    /// THE REFUSAL IS NO FREE PROBE: two datasets with the same first and last bar and different bars inside get the same sentence, whichever
    /// holds more, and so does one whose file has been destroyed, because nothing of the data is read. Both windows hold far fewer bars than
    /// the cap and would have fitted: that is the price. A refusal that counted the bars would tell a caller what the data holds, for free.
    /// </summary>
    [Fact]
    public async Task The_refusal_teaches_nothing_about_the_data()
    {
        const int end = Backtest.MaxTracedBars + 500;
        var to = Bar0.AddMinutes(end);

        async Task<string> RefusalOver(IEnumerable<int> minutes, bool destroy = false)
        {
            var (gw, _, db) = await TestEnv.Ready();
            using var _1 = db;
            await using var _2 = gw;
            var (set, campaign) = Given(gw, minutes, end + 1);
            if (destroy) File.WriteAllText(set.NormalisedPath, "not a dataset any more");
            var path = Program();

            var refused = Assert.Throws<GatewayDeniedException>(() => Ask(gw, set, path, null, to));

            Assert.Equal(ErrorCode.INVALID_REQUEST, refused.Code);
            Assert.Empty(gw.Strategies.Runs());
            Assert.Empty(gw.Campaigns.Trials(campaign.Id));
            return refused.Message;
        }

        var sparse = await RefusalOver([0, 7, end]);
        var fuller = await RefusalOver(Enumerable.Range(0, 5_000).Append(end));
        var destroyed = await RefusalOver([0, 7, end], destroy: true);

        Assert.Equal(sparse, fuller);
        Assert.Equal(sparse, destroyed);
    }

    /// <summary>A REQUEST ITS OWN DATASET'S HOLDOUT REFUSES HEARS THAT FIRST, because the repair there is the first thing it needs, and that is still no run.</summary>
    [Fact]
    public async Task A_window_past_the_cap_that_reaches_the_holdout_hears_the_holdout()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        await using var _2 = gw;
        const int end = Backtest.MaxTracedBars + 500;
        var (set, campaign) = Given(gw, [0, 7, end], end + 1);

        var refused = Assert.Throws<GatewayDeniedException>(() => Ask(gw, set, Program(), null, null));

        Assert.Equal(ErrorCode.HOLDOUT_WITHHELD, refused.Code);
        Assert.Empty(gw.Campaigns.Trials(campaign.Id));
    }

    /// <summary>
    /// A RUN THAT STARTS CANNOT REACH THE CAP, AND THIS IS WHY: <see cref="Backtest.MostBarsIn"/> is an upper bound on what a run evaluates, on every
    /// bar a program may declare, over a window that starts and ends inside a bar and crosses a daylight change. It is the exact count on a grid cut
    /// in UTC, so the cap is not refusing a whole window it could have run.
    /// </summary>
    [Theory]
    [InlineData("1m", "UTC")]
    [InlineData("5m", "UTC")]
    [InlineData("15m", "UTC")]
    [InlineData("30m", "UTC")]
    [InlineData("1h", "UTC")]
    [InlineData("4h", "UTC")]
    [InlineData("1d", "UTC")]
    [InlineData("1d", "America/New_York")]
    [InlineData("1d", "Europe/Berlin")]
    public void The_count_bounds_every_run_that_starts(string bar, string zone)
    {
        var parse = StrategyParser.Parse($"instrument BTCUSDT\ntimezone {zone}\nbars {bar}\nsize fixed 1\nexit when close < 97\nentry when close > 103\n");
        Assert.True(parse.Ok, parse.Why);
        var program = parse.Program!;

        // 2026-03-01 00:07 to 2026-04-15 05:11 UTC: both the American and the European change, and a partial bar at each end.
        var first = new DateTimeOffset(2026, 3, 1, 0, 7, 0, TimeSpan.Zero);
        var count = (int)(new DateTimeOffset(2026, 4, 15, 5, 11, 0, TimeSpan.Zero) - first).TotalMinutes + 1;
        var minutes = Enumerable.Range(0, count).Select(i => new KlineBar(first.AddMinutes(i), 100m, 100m, 100m, 100m, 1m));

        var run = Backtest.Run(program, new BacktestRequest(7, "sha", ExecutionModel.Frictionless, first, null), minutes);
        var most = Backtest.MostBarsIn(BarGrid.For(program), first, first.AddMinutes(count - 1));

        Assert.Equal(BacktestOutcome.COMPLETED, run.Outcome);
        Assert.True(run.Metrics.Bars <= most, $"{run.Metrics.Bars} bars evaluated, counted at most {most}");
        if (zone == "UTC") Assert.Equal(run.Metrics.Bars, most);
    }

    /// <summary>
    /// WHAT THE REFUSAL ADVISES IS TRUE: the start it names fits and the day before does not, and the bar it names fits the whole window. The card's
    /// own case, the 24 research months of a minute program, and a five-minute one, which is still ten thousand bars over.
    /// </summary>
    [Theory]
    [InlineData("1m")]
    [InlineData("5m")]
    public void The_refusal_names_a_start_and_a_bar_that_fit(string bar)
    {
        StrategyProgram On(string bars) => StrategyParser.Parse(
            $"instrument BTCUSDT\nbars {bars}\nsize fixed 1\nexit when close < 97\nentry when close > 103\n").Program!;

        var first = new DateTimeOffset(2023, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2025, 9, 30, 23, 59, 0, TimeSpan.Zero);
        var set = new DatasetRecord(1, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 36, 36, [], "unread", "unread", 0, first,
            first.AddMonths(36), 0, [], false, 0, 0, 0, first, DatasetState.ACCEPTED, null, []);

        var words = Backtest.WindowRefusal(On(bar), set, null, to);

        Assert.NotNull(words);
        var from = DateTimeOffset.Parse(Regex.Match(words, @"--from (\d{4}-\d{2}-\d{2})").Groups[1].Value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal);
        Assert.Null(Backtest.WindowRefusal(On(bar), set, from, to));
        Assert.NotNull(Backtest.WindowRefusal(On(bar), set, from.AddDays(-1), to));
        Assert.Contains("'bars 15m'", words, StringComparison.Ordinal);
        Assert.Null(Backtest.WindowRefusal(On("15m"), set, null, to));
    }

    /// <summary>THE ORG GUIDE NAMES THE CAP FROM THE CONSTANT, and its "from 15m up every research month fits" stays true of the longest download. The pair's guide is pinned byte for byte, so its seats read the cap in the language reference and the schema.</summary>
    [Fact]
    public void The_agents_are_told_the_cap_and_how_to_choose_a_window()
    {
        Assert.Contains("{{run_bar_cap}} bars", Canon.OrgGuideTemplate, StringComparison.Ordinal);
        Assert.True(Backtest.MaxJudgedBars / 15 <= Backtest.MaxTracedBars, "a word in the canon: from 15m up every research month fits");
    }

    /// <summary>
    /// R29 F-B1'S WORDS: a forward hold withholds data calls, and the sentence that follows every spent campaign says what is still served. The old
    /// one said no caller was served those minutes, while a live quote, a fill and P&amp;L over them were. Words only: what is served has not changed.
    /// </summary>
    [Fact]
    public void A_forward_hold_says_what_is_still_served()
    {
        var words = CampaignRenewal.Waiting(new ForwardHoldout(1, Bar0, Bar0, "research trials", null), null);

        Assert.Contains("live quotes, orders, fills, positions and P&L over them are still served", words, StringComparison.Ordinal);
        Assert.DoesNotContain("no caller on this channel is served", words, StringComparison.Ordinal);
    }
}

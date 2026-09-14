using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE OWNER AND THE ROLES ARE TOLD HOW OLD THE BARS ARE, AND WHETHER THE PROMOTED STRATEGY CAN ACT
/// ON THEM.
///
/// <para>The gate that refuses a stale decision is in the gateway and needs no page. These two
/// surfaces are the other half of it: a promoted strategy that is healthy and silent because every
/// bar here is older than it will act on is otherwise indistinguishable from one that has not
/// signalled, and before this unit neither section 3 of the owner's report nor the Situation said
/// anything at all about the age of a bar.</para>
///
/// <para><b>The mutant this class exists to catch</b> is the age measured from
/// <c>accepted_at</c> — when the dataset was COLLECTED — instead of from its last bar. A year of
/// stale months downloaded this morning then reads as data a strategy could trade on, and the
/// report says a bound is satisfiable that nothing here can satisfy.</para>
/// </summary>
public class DataFreshnessSurfacesTests
{
    static DateTimeOffset Midday()
    {
        var day = DateTimeOffset.Now.ToLocalTime().Date.AddHours(12);
        return new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day));
    }

    const string Bounded =
        "instrument BTCUSDT\nsize fixed 1\ntimeframe 1m\ndata_freshness 2m\nmax_decision_age 30s\n"
        + "exit when close < 97\nentry when close > 103\n";

    /// <summary>
    /// A PROMOTION THAT STILL STANDS, over a dataset whose newest bar is <paramref name="barsAgeDays"/>
    /// days old and which was ACCEPTED at the snapshot instant. The two are deliberately different
    /// numbers: that difference is what the mutant collapses.
    /// </summary>
    static async Task<(TradingGateway Gw, Database Db, DateTimeOffset At)> Promoted(int barsAgeDays)
    {
        const string evaluationClass = EvaluationClass.Research;
        var (gw, _, db) = await TestEnv.Ready();
        var at = Midday();

        var file = Path.Combine(Paths.Data, $"fresh-{Guid.NewGuid():n}.csv");
        Directory.CreateDirectory(Paths.Data);
        File.WriteAllText(file, KlineNormaliser.Header + "\n");

        var last = at.AddDays(-barsAgeDays);
        var setId = gw.Datasets.Record(new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 12, [],
            file, DatasetStore.Sha256(file)!, 1000, last.AddDays(-300), last, 0, [], false, 0, 0, 0,
            // COLLECTED NOW. The mutant reads this column and calls the bars fresh.
            at, DatasetState.ACCEPTED, null, []));

        var cutoff = last.AddDays(-30);
        Assert.True(gw.Datasets.SetHoldout(setId, cutoff, evaluationClass).Ok);
        var set = gw.Datasets.ById(setId)!;

        var opened = new CampaignStore(db).Open("BTCUSDT 1m v1", set, 10, 3, at);
        Assert.True(opened.Ok, opened.Why);

        var program = StrategyParser.Parse(Bounded).Program!;
        var strategies = new StrategyStore(db);
        strategies.RecordVersion(new StrategyVersionRow(
            program.StrategyId, program.Source, program.Canonical, program.Manifest,
            StrategyStore.InterpreterBuild, ParseVerdict.Accepted, program.WarmUpBars, at, null, null)
        {
            Timeframe = program.Freshness!.Timeframe,
            DataFreshness = program.Freshness.DataFreshness,
            MaxDecisionAge = program.Freshness.MaxDecisionAge
        });

        var model = ExecutionModel.Declare(0.001m, 0m, 0.0001m, 10_000m).Model!;
        var runId = new BacktestRequest(set.Id, set.NormalisedSha256, model, cutoff, null)
            .RunIdFor(program.StrategyId);
        strategies.RecordRun(new StrategyRunRow(
            runId, program.StrategyId, set.Id, set.NormalisedSha256, cutoff, null, model.Canonical,
            BacktestOutcome.COMPLETED.ToString(), null, 500, 4, 3, 4, 4, 100, 0, 0,
            12m, 2m, 10m, 3m, "trace-sha", at, Referee.RunRole, null), []);

        new Promotions(db).Record(new PromotionRow(
            "", program.StrategyId, opened.Campaign!.Id, opened.Campaign.ScoringPolicySha256,
            StrategyStore.InterpreterBuild, set.Id, set.NormalisedSha256, model.Canonical,
            Referee.EvaluatorVersion, runId, PromotionVerdict.Promoted, PromotionReason.Met, at)
        {
            Timeframe = program.Freshness.Timeframe,
            DataFreshness = program.Freshness.DataFreshness,
            MaxDecisionAge = program.Freshness.MaxDecisionAge
        });

        Assert.True(new Promotions(db).Current()!.IsPromoted);
        return (gw, db, at);
    }

    /// <summary>
    /// RED before this unit: with a promoted strategy present, section 3 said nothing about the age
    /// of any bar, and nothing said whether that strategy's own bound could be met.
    ///
    /// <para>This is also THE MUTANT'S TEST: four hundred days of history collected at the snapshot
    /// instant. Measured from <c>accepted_at</c> it reads as seconds old and the bound reads as
    /// satisfiable; measured from the last bar — which is what <c>BarAge</c> does — it is four
    /// hundred days old and nothing this strategy decides would ever be sent.</para>
    /// </summary>
    [Fact]
    public async Task Section_three_ages_the_bars_from_the_last_bar_and_says_the_bound_is_not_satisfiable()
    {
        var (gw, db, at) = await Promoted(barsAgeDays: 400);
        using var _1 = db;

        var report = gw.Reports.Compose(at);
        var text = DailyReportText.Render(report);

        Assert.Contains("freshest bar 400d old", string.Join("\n", report.Readiness.DataAges),
            StringComparison.Ordinal);
        Assert.Contains("newest bars:", text, StringComparison.Ordinal);
        Assert.Contains("400d old", text, StringComparison.Ordinal);

        Assert.NotNull(report.Readiness.FreshnessBound);
        Assert.Contains("needs bars no older than 2m", report.Readiness.FreshnessBound!, StringComparison.Ordinal);
        Assert.Contains("NOT satisfiable", report.Readiness.FreshnessBound, StringComparison.Ordinal);
        Assert.Contains("promoted strategy's data bound:", text, StringComparison.Ordinal);
        await gw.DisposeAsync();
    }

    /// <summary>Bars inside the bound read as satisfiable, which is what makes the line worth reading.</summary>
    [Fact]
    public async Task Bars_inside_the_promoted_bound_read_as_satisfiable()
    {
        var (gw, db, at) = await Promoted(barsAgeDays: 0);
        using var _1 = db;

        var bound = gw.Reports.Compose(at).Readiness.FreshnessBound!;

        Assert.Contains("satisfiable now", bound, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT satisfiable", bound, StringComparison.Ordinal);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// A FIXTURE WRITTEN THIS SECOND CANNOT MAKE A STALE INSTALLATION READ AS A FRESH ONE.
    ///
    /// <para>Fixture bars establish plumbing and are never evidence (`EvaluationClass`), so the bound
    /// is judged against market data alone. The stale market dataset is four hundred days old and a
    /// brand new fixture sits beside it; the line still says NOT satisfiable, and the dataset lines
    /// say which kind each one is.</para>
    /// </summary>
    [Fact]
    public async Task A_fresh_fixture_is_named_as_one_and_does_not_satisfy_the_bound()
    {
        var (gw, db, at) = await Promoted(barsAgeDays: 400);
        using var _1 = db;

        var file = Path.Combine(Paths.Data, $"fixture-{Guid.NewGuid():n}.csv");
        File.WriteAllText(file, KlineNormaliser.Header + "\n");
        var fixtureId = gw.Datasets.Record(new DatasetRecord(
            0, BinanceArchive.Source, "ETHUSDT", BinanceArchive.Interval, "v1", 1, 1, [],
            file, DatasetStore.Sha256(file)!, 10, at.AddMinutes(-9), at, 0, [], false, 0, 0, 0,
            at, DatasetState.ACCEPTED, null, []));
        Assert.True(gw.Datasets.SetHoldout(fixtureId, at.AddMinutes(-5), EvaluationClass.Fixture).Ok);

        var report = gw.Reports.Compose(at);
        var ages = string.Join("\n", report.Readiness.DataAges);

        Assert.Contains("FIXTURE, never evidence", ages, StringComparison.Ordinal);
        Assert.Contains("market data", ages, StringComparison.Ordinal);
        Assert.Contains("NOT satisfiable", report.Readiness.FreshnessBound!, StringComparison.Ordinal);
        Assert.Contains("400d old", report.Readiness.FreshnessBound, StringComparison.Ordinal);
        await gw.DisposeAsync();
    }

    /// <summary>Nothing promoted, nothing claimed: the line is absent rather than reassuring.</summary>
    [Fact]
    public async Task With_nothing_promoted_the_bound_line_says_nothing()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;

        Assert.Null(gw.Reports.Compose(Midday()).Readiness.FreshnessBound);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// AND THE SITUATION SAYS THE SAME, off the same definition. A turn that plans a strategy against
    /// bars a year old, or against a fixture, has been handed a number it cannot read correctly.
    /// </summary>
    [Fact]
    public void The_situations_data_line_ages_the_bars_and_marks_fixture_versus_market_data()
    {
        var now = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        var set = new DatasetRecord(1, BinanceArchive.Source, "BTCUSDT", "1m", "v1", 12, 12, [],
            "/x/v1.csv", new string('a', 64), 100, now.AddDays(-400), now.AddDays(-400), 0, [], false,
            0, 0, 0, now, DatasetState.ACCEPTED, null, []);

        var market = MissionSituation.DataLine(set, now);
        var fixture = MissionSituation.DataLine(
            set with { EvaluationClass = EvaluationClass.Fixture }, now);

        Assert.Contains("Freshest bar 400d old", market, StringComparison.Ordinal);
        Assert.Contains("market data", market, StringComparison.Ordinal);
        Assert.Contains("FIXTURE, never evidence", fixture, StringComparison.Ordinal);
    }
}

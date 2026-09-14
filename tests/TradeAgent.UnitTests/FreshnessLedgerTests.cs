using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE BOUNDS TRAVEL ONTO THE VERSION AND ONTO THE VERDICT — schema 19.
///
/// <para><c>docs/COUNCIL.md</c>:35: "a changed assumption invalidates the evidence that rested on
/// it". How stale a decision a promoted program may act on is one of those assumptions, and before
/// this rung neither <c>strategy_version</c> nor <c>strategy_promotion</c> had anywhere to put it:
/// the only way to learn a version's timeframe was to re-parse its source, and a verdict recorded
/// nothing about the bounds it had judged.</para>
///
/// <para><b>Whole seconds in an INTEGER column</b>, which is the same spelling
/// <c>StrategyCanonical</c> hashes — so the number in the row and the number inside the id are one
/// number, and a reader never re-parses a duration on the money path.</para>
/// </summary>
public class FreshnessLedgerTests
{
    static readonly DateTimeOffset At = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    static readonly DateTimeOffset Cutoff = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    const string Bounded =
        "instrument BTCUSDT\nsize fixed 1\ntimeframe 1m\ndata_freshness 2m\nmax_decision_age 30s\n"
        + "exit when close < 97\nentry when close > 103\n";

    const string Unbounded =
        "instrument BTCUSDT\nsize fixed 1\nexit when close < 97\nentry when close > 103\n";

    static StrategyVersionRow RowFor(StrategyProgram p) =>
        new(p.StrategyId, p.Source, p.Canonical, p.Manifest, StrategyStore.InterpreterBuild,
            ParseVerdict.Accepted, p.WarmUpBars, At, null, null)
        {
            Timeframe = p.Freshness?.Timeframe,
            DataFreshness = p.Freshness?.DataFreshness,
            MaxDecisionAge = p.Freshness?.MaxDecisionAge
        };

    /// <summary>
    /// RED before schema 19: <c>SQLite Error 1: 'no such column: max_decision_age'</c>.
    /// </summary>
    [Fact]
    public void A_version_row_carries_the_three_bounds_the_program_declared()
    {
        using var db = TestEnv.NewDb();
        var store = new StrategyStore(db);
        var program = StrategyParser.Parse(Bounded).Program!;

        store.RecordVersion(RowFor(program));
        var read = store.VersionById(program.StrategyId)!;

        Assert.Equal(TimeSpan.FromMinutes(1), read.Timeframe);
        Assert.Equal(TimeSpan.FromMinutes(2), read.DataFreshness);
        Assert.Equal(TimeSpan.FromSeconds(30), read.MaxDecisionAge);
        Assert.Equal(program.Freshness, read.Freshness);
    }

    /// <summary>A program that declared none reads back as none, and not as a bound of nothing.</summary>
    [Fact]
    public void A_version_of_a_program_with_no_bounds_reads_back_as_no_bounds()
    {
        using var db = TestEnv.NewDb();
        var store = new StrategyStore(db);
        var program = StrategyParser.Parse(Unbounded).Program!;

        store.RecordVersion(RowFor(program));
        var read = store.VersionById(program.StrategyId)!;

        Assert.Null(read.Timeframe);
        Assert.Null(read.Freshness);
    }

    /// <summary>The verdict's own row carries them too, and round trips through the ledger.</summary>
    [Fact]
    public void A_promotion_row_carries_the_three_bounds_and_round_trips()
    {
        using var db = TestEnv.NewDb();
        var datasets = new DatasetStore(db);
        var file = Path.Combine(Paths.Data, $"freshness-{Guid.NewGuid():n}.csv");
        Directory.CreateDirectory(Paths.Data);
        File.WriteAllText(file, KlineNormaliser.Header + "\n");

        var setId = datasets.Record(new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 12, [],
            file, DatasetStore.Sha256(file)!, 1000, Cutoff.AddDays(-300), Cutoff.AddDays(60), 0, [],
            false, 0, 0, 0, At, DatasetState.ACCEPTED, null, []));
        Assert.True(datasets.SetHoldout(setId, Cutoff, EvaluationClass.Research).Ok);
        var set = datasets.ById(setId)!;

        var opened = new CampaignStore(db).Open("BTCUSDT 1m v1", set, 10, 3, At);
        Assert.True(opened.Ok, opened.Why);

        var program = StrategyParser.Parse(Bounded).Program!;
        var strategies = new StrategyStore(db);
        strategies.RecordVersion(RowFor(program));

        var model = ExecutionModel.Declare(0.001m, 0m, 0.0001m, 10_000m).Model!;
        var request = new BacktestRequest(set.Id, set.NormalisedSha256, model, Cutoff, null);
        var runId = request.RunIdFor(program.StrategyId);
        strategies.RecordRun(new StrategyRunRow(
            runId, program.StrategyId, set.Id, set.NormalisedSha256, Cutoff, null, model.Canonical,
            BacktestOutcome.COMPLETED.ToString(), null, 500, 4, 3, 4, 4, 100, 0, 0,
            12m, 2m, 10m, 3m, "trace-sha", At, Referee.RunRole, null), []);

        var promotions = new Promotions(db);
        var written = promotions.Record(new PromotionRow(
            "", program.StrategyId, opened.Campaign!.Id, opened.Campaign.ScoringPolicySha256,
            StrategyStore.InterpreterBuild, set.Id, set.NormalisedSha256, model.Canonical,
            Referee.EvaluatorVersion, runId, PromotionVerdict.Promoted, PromotionReason.Met, At)
        {
            Timeframe = program.Freshness!.Timeframe,
            DataFreshness = program.Freshness.DataFreshness,
            MaxDecisionAge = program.Freshness.MaxDecisionAge
        });

        Assert.Equal(program.Freshness, written.Freshness);
        Assert.Equal(program.Freshness, promotions.ById(written.Id)!.Freshness);
        Assert.Equal(program.Freshness, promotions.For(program.StrategyId)[0].Freshness);

        // NOT IN THE ID. A changed bound is already a different version id, so hashing them a second
        // time here would only be the ninth bound fact spelled twice.
        Assert.Equal(written.ComputedId, written.Id);
    }
}

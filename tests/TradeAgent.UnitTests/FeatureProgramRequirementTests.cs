using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// REQUIRED FIELDS (<c>U-language-v2a</c> item 2; R05 row 10): a program states the declaration kinds it uses, each one
/// required, and a reader names the kinds it implements and refuses the rest in words — the backtest implements every
/// kind this build parses, the paper runner every kind but <c>feature</c>. Comments are the one optional part.
/// </summary>
public class FeatureProgramRequirementTests(ITestOutputHelper log)
{
    /// <summary>A program that uses every declaration kind this build parses, each stating something other than its default.</summary>
    public static string EveryKind => $"""
        # Every declaration kind, in one program.
        instrument BTCUSDT
        timezone America/New_York
        bars 1h
        timeframe 1h
        data_freshness 2h
        max_decision_age 10m
        const k = 2
        indicator m = sma(close, k)
        indicator orh = opening_range_high()
        feature funding = {FeatureProgramGrammarTests.FundingSpec}
        size risk_fraction 0.01 max_capital_fraction 0.95
        stop percent 2
        target percent 3
        max_hold_bars 10
        weekdays mon,tue,wed,thu,fri
        entry_window 10:00-16:00
        opening_range 09:00-10:00
        session_exit 17:00
        exit when close < m
        entry when funding < 0 and close > m and close > orh
        """;

    static StrategyProgram Parsed(string text)
    {
        var parse = StrategyParser.Parse(text);
        Assert.True(parse.Ok, parse.Why);
        return parse.Program!;
    }

    /// <summary>
    /// (l) EVERY KIND PARSED IS ONE THE BACKTEST IMPLEMENTS. The parser's own list — the one its refusal of an unknown
    /// word prints — is <see cref="StrategyDeclarations.All"/>, word for word, but for the one kind that is a clause of
    /// another line rather than a line of its own (<c>max_capital_fraction</c>, <c>U-size-cap</c>), so a kind added to the
    /// parser and not to the list fails here; every line kind is accepted as a line's first word, and the clause on the
    /// size line; the backtest implements every one; a program using every kind requires every kind and the backtest
    /// does not refuse it, while the paper runner refuses it naming <c>feature</c> and nothing else.
    /// </summary>
    [Fact]
    public void Every_kind_parsed_is_one_the_backtest_implements()
    {
        var lines = StrategyDeclarations.All.Where(k => k != StrategyDeclarations.MaxCapitalFraction).ToList();
        Assert.Equal(StrategyDeclarations.All.Count - 1, lines.Count);

        var unknown = StrategyParser.Parse("instrument BTCUSDT\nnonsense here\n");
        const string Lead = "The declarations are: ";
        var listed = unknown.Why[(unknown.Why.IndexOf(Lead, StringComparison.Ordinal) + Lead.Length)..].Split(", ");
        log.WriteLine(string.Join(" ", listed));
        Assert.Equal(lines, listed);

        foreach (var kind in lines)
        {
            var parse = StrategyParser.Parse($"{kind} x\n");
            Assert.DoesNotContain("is not a declaration this language has", parse.Why, StringComparison.Ordinal);
        }

        // THE CLAUSE IS READ WHERE IT IS WRITTEN — the size line's third word — and is a line of its own nowhere.
        Assert.Contains("is not a declaration this language has",
            StrategyParser.Parse($"{StrategyDeclarations.MaxCapitalFraction} 0.95\n").Why, StringComparison.Ordinal);
        Assert.Equal(0.95m, Parsed("instrument BTCUSDT\nsize risk_fraction 0.01 max_capital_fraction 0.95\nstop percent 2\n"
                                   + "exit when close < 1\nentry when close > 2\n").Sizing.MaxCapitalFraction);

        Assert.Equal(StrategyDeclarations.All, Backtest.Implements);

        var every = Parsed(EveryKind);
        Assert.Equal(StrategyDeclarations.All, every.Requires);
        Assert.Null(StrategyDeclarations.Refusal(every, Backtest.Implements, "the backtest"));

        // AND IT REALLY RUNS ONE: a day of New York hours over a tape holding funding, every declaration in force.
        var file = Path.Combine(TestEnv.Home, $"tape-{Guid.NewGuid():n}.db");
        var from = new DateTimeOffset(2026, 8, 3, 4, 0, 0, TimeSpan.Zero);
        using (var store = new TapeStore(file))
            for (var h = 0; h < 24; h++)
                TapeReadings.Reading(store, from.AddHours(h).AddMinutes(10), h % 3 == 0 ? "-0.00050000" : "0.00010000");
        var bars = FeatureProgramBacktestTests.Minutes(from, 24 * 60);
        var grid = BarGrid.For(every);
        using var ledger = TestEnv.NewDb();
        using (var feed = new FeatureFeed(new TapeReader(file), TapeHoldout.Pipe(CouncilRoles.Research, new DatasetStore(ledger)),
                   every, grid.EndOf(grid.StartOf(bars[^1].OpenTime))))
        {
            var run = Backtest.Run(every, new BacktestRequest(1, "fixture",
                ExecutionModel.Declare(0m, 0m, 0.001m, 10_000m).Model!, from, null), bars, features: feed);
            log.WriteLine($"{run.Outcome}: {run.Metrics.Bars} bars, {run.Counters.EvaluatedEvents} evaluated, {run.Counters.UndefinedEvents} undefined");
            Assert.Equal(BacktestOutcome.COMPLETED, run.Outcome);
            Assert.Equal(24, run.Metrics.Bars);
            Assert.Equal(BacktestEventKind.Feature, run.Trace.Events[^1].Kind);
        }

        Assert.Equal(StrategyDeclarations.All.Where(k => k != StrategyDeclarations.Feature), ForwardRuns.Implements);
        Assert.StartsWith("this program requires `feature`, which this build's paper runner does not implement.",
            ForwardRuns.Refuses(every), StringComparison.Ordinal);
        Assert.Null(ForwardRuns.Refuses(Parsed(EveryKind.Replace("funding < 0 and ", "", StringComparison.Ordinal)
            .Replace($"feature funding = {FeatureProgramGrammarTests.FundingSpec}\n", "", StringComparison.Ordinal))));
    }

    /// <summary>
    /// WHAT A PROGRAM REQUIRES IS READ OFF ITS MEANING, AND COMMENTS ARE THE ONE OPTIONAL PART. A declaration that states
    /// the default — <c>timezone UTC</c>, <c>bars 1m</c>, all seven weekdays — requires nothing, because two spellings of
    /// one program are one id and must require the same; a program requires exactly the kinds it uses. Comments are kept
    /// byte for byte in the source, are outside the id, and are required by nobody.
    /// </summary>
    [Fact]
    public void A_program_requires_what_it_uses_and_its_comments_are_optional()
    {
        var bare = Parsed("instrument BTCUSDT\nsize fixed 1\nexit when close < 1\nentry when close > 2\n");
        Assert.Equal([StrategyDeclarations.Instrument, StrategyDeclarations.Size, StrategyDeclarations.Exit, StrategyDeclarations.Entry],
            bare.Requires);

        var defaults = Parsed("instrument BTCUSDT\ntimezone UTC\nbars 1m\nweekdays mon,tue,wed,thu,fri,sat,sun\nsize fixed 1\n"
                              + "exit when close < 1\nentry when close > 2\n");
        Assert.Equal(bare.StrategyId, defaults.StrategyId);
        Assert.Equal(bare.Requires, defaults.Requires);

        var commented = "# a note the author kept\ninstrument BTCUSDT   # the one instrument\n\nsize fixed 1 # fixed\n"
                        + "exit when close < 1\n   # an exit, then an entry\nentry when close > 2\n";
        var noted = Parsed(commented);
        Assert.Equal(commented, noted.Source);
        Assert.Equal(bare.StrategyId, noted.StrategyId);
        Assert.Equal(bare.Requires, noted.Requires);

        var feature = Parsed(FeatureProgramGrammarTests.FundingProgram());
        log.WriteLine(string.Join(" ", feature.Requires));
        Assert.Equal(
            [
                StrategyDeclarations.Instrument, StrategyDeclarations.Bars, StrategyDeclarations.Timeframe,
                StrategyDeclarations.DataFreshness, StrategyDeclarations.MaxDecisionAge, StrategyDeclarations.Feature,
                StrategyDeclarations.Size, StrategyDeclarations.Stop, StrategyDeclarations.Exit, StrategyDeclarations.Entry
            ],
            feature.Requires);
    }
}

using System.Globalization;
using TradeAgent.Core.Data;
using TradeAgent.Core.Strategy;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE THREE DAY-ONE PROGRAMS, EVALUATED END TO END, WITH THEIR INTENTS PINNED.
///
/// <para>`docs/COUNCIL.md` ends its language specification with a test rather than a feature list:
/// "Day one must express: a moving-average crossover with fixed sizing and stop; an opening-range
/// breakout with ATR risk sizing and a time stop; an RSI mean reversion with a profit exit and a
/// maximum holding time." `U-runner-1` proved the three PARSE and pinned their ids; this runs them over
/// bars and pins what they DO — which bar each signal came from, what quantity, what stop, and why the
/// position was closed.</para>
///
/// <para><b><see cref="NextBarFills"/> is not the executor.</b> Fills, fees, slippage and the trace are
/// `U-runner-3`'s. It exists because a program that can never hold a position can never exercise an
/// exit: it fills an outstanding intent at the next bar's OPEN at no cost, and it is the caller
/// supplying account state exactly as `docs/COUNCIL.md`'s one event interface says a backtest does.
/// Nothing in the evaluator knows it is there.</para>
/// </summary>
public class DayOneEvaluationTests
{
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }

    static StrategyProgram Parsed(string name)
    {
        var text = File.ReadAllText(
            Path.Combine(RepoRoot(), "tests", "TradeAgent.UnitTests", "Strategies", name));
        var parse = StrategyParser.Parse(text);
        Assert.True(parse.Ok, $"{name} did not parse: {parse.Why}");
        return parse.Program!;
    }

    /// <summary>
    /// A caller that fills whatever the evaluator emitted, at the next bar's open, for nothing. The
    /// least that makes an end-to-end run possible; it is not a model of a market.
    /// </summary>
    sealed class NextBarFills(decimal capital)
    {
        StrategyIntent? outstanding;

        public decimal Capital { get; private set; } = capital;

        public PositionSide Position { get; private set; } = PositionSide.Flat;

        public decimal Quantity { get; private set; }

        public decimal AverageFillPrice { get; private set; }

        public int BarsSinceEntry { get; private set; }

        public List<string> Fills { get; } = [];

        /// <summary>Fills anything outstanding at this bar's open, then reports the account.</summary>
        public AccountReading AtOpenOf(KlineBar bar)
        {
            if (Position == PositionSide.Long) BarsSinceEntry++;

            if (outstanding is { } intent && bar.OpenTime >= intent.NotBefore)
            {
                if (intent.Kind == IntentKind.Enter)
                {
                    Position = PositionSide.Long;
                    Quantity = intent.Quantity;
                    AverageFillPrice = bar.Open;
                    BarsSinceEntry = 0;
                    Capital -= intent.Quantity * bar.Open;
                }
                else
                {
                    Capital += Quantity * bar.Open;
                    Position = PositionSide.Flat;
                    Quantity = 0m;
                    AverageFillPrice = 0m;
                    BarsSinceEntry = 0;
                }

                Fills.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{intent.Kind} {intent.Quantity} at {bar.Open} on {bar.OpenTime:yyyy-MM-ddTHH:mmZ}"));
                outstanding = null;
            }

            return new AccountReading(
                Capital, Capital + Quantity * bar.Open, Position, Quantity, AverageFillPrice,
                outstanding is not null, BarsSinceEntry);
        }

        public void Accept(StrategyIntent intent) => outstanding = intent;
    }

    /// <summary>One run, folded by hand so the caller can fill between bars.</summary>
    static (EvaluationState State, List<StrategyIntent> Intents, NextBarFills Caller) RunOn(
        StrategyProgram program, IReadOnlyList<KlineBar> bars, decimal capital = 100_000m)
    {
        var state = EvaluationState.Start(program);
        var caller = new NextBarFills(capital);
        var intents = new List<StrategyIntent>();

        foreach (var bar in bars)
        {
            var outcome = StrategyEvaluator.Step(state, bar, caller.AtOpenOf(bar));
            Assert.NotEqual(EvaluationStatus.Faulted, outcome.Status);

            if (outcome.Intent is { } intent)
            {
                intents.Add(intent);
                caller.Accept(intent);
            }
        }

        return (state, intents, caller);
    }

    /// <summary>`Enter 1 @2026-01-05T01:02Z ref=103.00 stop=101.4550 target=` — one intent, one line.</summary>
    static string Line(StrategyIntent intent) => string.Create(CultureInfo.InvariantCulture,
        $"{intent.Kind}/{intent.Cause} {intent.Quantity} @{intent.Bar:yyyy-MM-ddTHH:mmZ} " +
        $"ref={intent.ReferencePrice} stop={intent.StopPrice} target={intent.TargetPrice}");

    /// <summary>
    /// A MOVING-AVERAGE CROSSOVER WITH FIXED SIZING AND A STOP (`docs/COUNCIL.md:164`). The twenty-bar
    /// average overtakes the fifty-bar one on the way up and falls back through it on the way down, so
    /// the run is one entry and one exit, sized at the declared 1 and stopped 1.5 per cent below the
    /// close the signal came from.
    /// </summary>
    [Fact]
    public void The_moving_average_crossover_enters_on_the_crossing_and_leaves_on_the_one_back()
    {
        var program = Parsed("ma-crossover.strategy");
        Assert.Equal(51, program.WarmUpBars);

        var (state, intents, caller) = RunOn(program, SyntheticBars.Crossover);

        // Bar 60 is the first close of the rise: the twenty-bar mean is 100.05 and the fifty-bar mean
        // 100.02, and on bar 59 both were exactly 100 — so that is where they crossed. Bar 142 is where
        // they cross back. Both bars were recomputed outside this build from the documented formulas.
        Assert.Equal(
        [
            "Enter/Rule 1 @2026-01-05T01:00Z ref=101 stop=99.485 target=",
            "Exit/Rule 1 @2026-01-05T02:22Z ref=137 stop= target="
        ], intents.Select(Line));

        Assert.Equal(60, intents[0].BarOrdinal);
        Assert.Equal(142, intents[1].BarOrdinal);
        Assert.Equal(101m - 101m * 1.5m / 100m, intents[0].StopPrice);       // stop percent 1.5

        Assert.Equal(
        [
            "Enter 1 at 102 on 2026-01-05T01:01Z",
            "Exit 1 at 136 on 2026-01-05T02:23Z"
        ], caller.Fills);

        Assert.Equal(50, state.Counters.WarmingUpEvents);
        Assert.Equal(0, state.Counters.MissingMinutes);
        Assert.Equal(0, state.Counters.UndefinedEvents);
        Assert.Equal(2, state.Counters.Intents);
    }

    /// <summary>
    /// AN OPENING-RANGE BREAKOUT WITH ATR RISK SIZING AND A TIME STOP (`docs/COUNCIL.md:165`). The
    /// range is measured between 09:30 and 10:00 New York time, the entry is the first close above it
    /// inside the 10:00-15:30 window, the quantity is one per cent of equity over two ATRs of stop
    /// distance, and the exit is the low falling back under the range.
    /// </summary>
    [Fact]
    public void The_opening_range_breakout_enters_above_the_range_and_leaves_below_it()
    {
        var program = Parsed("opening-range-breakout.strategy");
        Assert.Equal(15, program.WarmUpBars);
        Assert.Equal("America/New_York", program.Time.TimeZone);

        var (state, intents, caller) = RunOn(program, SyntheticBars.OpeningRange);

        // Every bar of the fixture is a unit wide, so atr(14) is exactly 1.00 and the stop is two of
        // them below the signal close: 101.1 - 2.00 = 99.10. One per cent of 100,000 of equity over a
        // 2.00 distance is 500.
        Assert.Equal(
        [
            "Enter/Rule 500.0 @2026-07-06T14:01Z ref=101.1 stop=99.10 target=",
            "Exit/Rule 500.0 @2026-07-06T15:34Z ref=99.9 stop= target="
        ], intents.Select(Line));

        Assert.Equal(101.00m, state.IndicatorValue("rangehigh"));
        Assert.Equal(99.50m, state.IndicatorValue("rangelow"));
        Assert.Equal(1.00m, state.IndicatorValue("truerange"));
        Assert.Equal(101.1m - 2m * 1.00m, intents[0].StopPrice);
        Assert.Equal(100_000m * 0.01m / 2.00m, intents[0].Quantity);

        // The entry is the first close above the range, inside the 10:00-15:30 window in New York; the
        // exit is the first low back under it. Before the range's own interval closes, sixteen warm
        // events read an undefined accumulator and are not evaluated at all.
        Assert.Equal(61, intents[0].BarOrdinal);
        Assert.Equal(154, intents[1].BarOrdinal);
        Assert.Equal(16, state.Counters.UndefinedEvents);
        Assert.Equal(0, state.Counters.EntriesOutsideTimeFilters);
        Assert.Equal(2, state.Counters.Intents);
        Assert.Equal(14, state.Counters.WarmingUpEvents);
        Assert.Equal(2, caller.Fills.Count);
    }

    /// <summary>
    /// AN RSI MEAN REVERSION WITH A PROFIT EXIT AND A MAXIMUM HOLDING TIME (`docs/COUNCIL.md:166`).
    /// The fall takes the fourteen-bar RSI under thirty and the rise brings it back over fifty-five,
    /// and the quantity is a quarter of the capital the caller reported divided by the close.
    /// </summary>
    [Fact]
    public void The_rsi_mean_reversion_enters_oversold_and_leaves_recovered()
    {
        var program = Parsed("rsi-mean-reversion.strategy");
        Assert.Equal(15, program.WarmUpBars);

        var (state, intents, caller) = RunOn(program, SyntheticBars.MeanReversion);

        // Bar 30's fourteen-bar RSI is 28.4155… — the first under the declared 30 — and bar 59's is
        // 56.3742…, the first over 55 after it. Both recomputed outside this build.
        Assert.Equal(
        [
            "Enter/Rule 253.80710659898477157360406091 @2026-01-05T00:30Z ref=98.5 stop=96.53 target=99.485",
            "Exit/Rule 253.80710659898477157360406091 @2026-01-05T00:59Z ref=89.5 stop= target="
        ], intents.Select(Line));

        Assert.Equal(30, intents[0].BarOrdinal);
        Assert.Equal(59, intents[1].BarOrdinal);
        Assert.Equal(100_000m * 0.25m / 98.5m, intents[0].Quantity);     // capital_fraction 0.25
        Assert.Equal(98.5m - 98.5m * 2m / 100m, intents[0].StopPrice);   // stop percent 2
        Assert.Equal(98.5m + 98.5m * 1m / 100m, intents[0].TargetPrice); // target percent 1
        Assert.Equal(2, state.Counters.Intents);
        Assert.Equal(14, state.Counters.WarmingUpEvents);
        Assert.Equal(2, caller.Fills.Count);
    }

    /// <summary>
    /// EVERY INTENT IN ALL THREE RUNS IS EXECUTABLE ONLY AFTER THE BAR IT CAME FROM, names that bar,
    /// and names the one instrument the program declared. That is `docs/COUNCIL.md`'s "a signal from a
    /// completed bar executes only afterwards", asserted over every intent the three produce rather
    /// than once.
    /// </summary>
    [Theory]
    [InlineData("ma-crossover.strategy")]
    [InlineData("opening-range-breakout.strategy")]
    [InlineData("rsi-mean-reversion.strategy")]
    public void Every_intent_the_day_one_programs_emit_is_stamped_with_its_bar(string name)
    {
        var program = Parsed(name);
        var bars = name switch
        {
            "ma-crossover.strategy" => SyntheticBars.Crossover,
            "opening-range-breakout.strategy" => SyntheticBars.OpeningRange,
            _ => SyntheticBars.MeanReversion
        };

        var (_, intents, _) = RunOn(program, bars);

        Assert.NotEmpty(intents);
        Assert.All(intents, intent =>
        {
            Assert.Equal(program.Instrument, intent.Instrument);
            Assert.Equal(intent.Bar.AddMinutes(1), intent.NotBefore);
            Assert.Equal(bars[(int)intent.BarOrdinal].OpenTime, intent.Bar);
            Assert.Equal(bars[(int)intent.BarOrdinal].Close, intent.ReferencePrice);
            Assert.True(intent.Quantity > 0m);
        });
    }
}

using TradeAgent.Core.Data;
using TradeAgent.Core.Strategy;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEM 5 — THE PER-EVENT BUDGET, THE STATE LIMIT, AND EVERY FAULT AS A VALUE.
///
/// <para>`docs/COUNCIL.md`: "The runner bounds program size, parse and validation work, lookback,
/// state, per-event computation and output; an interpreter fault or a timeout is a defined outcome (no
/// new exposure, the app's protection policy) while app protection continues." The second half is why
/// none of the tests below expects an exception: the program was written by a cheap model, so a text it
/// can produce that crashes the app is the app's defect and not the program's fault. Every one of them
/// comes back as <see cref="EvaluationOutcome.Faulted"/> with a reason, the run halted, no intent
/// emitted.</para>
///
/// <para><b>The budget is counted per EVENT.</b> One closed bar costs the indicators plus every rule
/// reached on it. Counted per RULE instead, a program of twenty small rules would pass while one rule
/// of the same total size was refused — the same program with different line breaks.</para>
/// </summary>
public class EvaluatorLimitTests
{
    static readonly DateTimeOffset Monday = new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);

    static StrategyProgram Program(string text)
    {
        var parse = StrategyParser.Parse(text);
        Assert.True(parse.Ok, parse.Why);
        return parse.Program!;
    }

    /// <summary>One exit and nineteen entries, all false, so every rule on the bar is evaluated.</summary>
    static StrategyProgram TwentySmallRules()
    {
        var lines = new List<string> { "instrument BTCUSDT", "size fixed 1", "exit when close < 1" };
        for (var i = 0; i < 19; i++) lines.Add($"entry when close > {1000 + i}");

        var program = Program(string.Join('\n', lines));
        Assert.Equal(20, program.Rules.Count);
        Assert.Equal(60, program.NodeCount);        // three nodes a rule, and twenty rules
        return program;
    }

    /// <summary>
    /// THE PER-EVENT BUDGET AND THE PARSER'S OWN LIMITS AGREE, and the arithmetic is here so that
    /// moving either number without the other fails.
    ///
    /// <para>A budget a valid program could trip would refuse a program the parser accepted — one limit
    /// contradicting another. The worst event a parseable program can cost is every indicator being a
    /// rolling extreme whose window expires on the same bar, plus the stop's own ATR, plus twice the
    /// node count (a crossing evaluates its operands on this bar and the one before, and cannot nest
    /// inside another crossing because it is a true-or-false value and a crossing's sides are numbers).
    /// The state is every window at its largest period, the values kept for history references, and the
    /// bar ring.</para>
    /// </summary>
    [Fact]
    public void The_budget_is_above_what_any_program_the_parser_accepts_can_cost()
    {
        var worstEvent = StrategyLimits.MaxIndicators * StrategyLimits.MaxLookbackBars
                         + 1
                         + 2 * StrategyLimits.MaxNodes;

        Assert.Equal(8401, worstEvent);
        Assert.True(worstEvent <= StrategyLimits.MaxOperationsPerEvent,
            $"a program at the parser's limits costs {worstEvent} operations an event and the budget is " +
            $"{StrategyLimits.MaxOperationsPerEvent}");

        var worstState = (StrategyLimits.MaxIndicators
                          * (StrategyLimits.MaxLookbackBars + 1 + IndicatorSet.Ring)
                          + IndicatorSet.Ring * 6) * sizeof(decimal);

        Assert.Equal(29, IndicatorSet.Ring);
        Assert.Equal(138_464, worstState);
        Assert.True(worstState <= StrategyLimits.MaxStateBytes,
            $"a program at the parser's limits holds {worstState} bytes and the limit is " +
            $"{StrategyLimits.MaxStateBytes}");
    }

    /// <summary>A run may be given tighter limits than the defaults, and never wider ones.</summary>
    [Fact]
    public void The_limits_in_strategy_limits_are_ceilings()
    {
        Assert.Equal(StrategyLimits.MaxOperationsPerEvent, EvaluationLimits.Default.OperationsPerEvent);
        Assert.Equal(StrategyLimits.MaxStateBytes, EvaluationLimits.Default.StateBytes);

        var tighter = EvaluationLimits.Of(100, 4096);
        Assert.Equal(100, tighter.OperationsPerEvent);
        Assert.Equal(4096, tighter.StateBytes);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EvaluationLimits.Of(StrategyLimits.MaxOperationsPerEvent + 1, 4096));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EvaluationLimits.Of(100, StrategyLimits.MaxStateBytes + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => EvaluationLimits.Of(0, 4096));
    }

    /// <summary>
    /// AN OVER-BUDGET EVENT IS A DEFINED OUTCOME. Nineteen entry rules of three nodes cost fifty-seven
    /// operations on one bar; under a budget of fifty the run halts with a reason and emits nothing.
    /// </summary>
    [Fact]
    public void A_program_over_the_per_event_budget_halts_the_run_with_a_reason()
    {
        var run = StrategyEvaluator.Run(
            TwentySmallRules(),
            SyntheticBars.Minutes(Monday, 5),
            (_, _) => AccountReading.Flat(10_000m),
            EvaluationLimits.Of(50, StrategyLimits.MaxStateBytes));

        Assert.True(run.Faulted);
        Assert.Contains("cost more than the 50 operations", run.FaultReason!, StringComparison.Ordinal);
        Assert.Contains("rather than one rule at a time", run.FaultReason!, StringComparison.Ordinal);
        Assert.Empty(run.Intents);

        // Halted: the first bar faulted and the other four were never evaluated.
        Assert.Equal(1, run.Counters.Bars);
    }

    /// <summary>
    /// And the same twenty rules pass comfortably under a budget that fits them — the guard is a
    /// bound, not a refusal of programs with several rules.
    /// </summary>
    [Fact]
    public void The_same_program_passes_under_a_budget_that_fits_the_whole_event()
    {
        var run = StrategyEvaluator.Run(
            TwentySmallRules(),
            SyntheticBars.Minutes(Monday, 5),
            (_, _) => AccountReading.Flat(10_000m),
            EvaluationLimits.Of(60, StrategyLimits.MaxStateBytes));

        Assert.False(run.Faulted);
        Assert.Equal(5, run.Counters.Bars);

        // Nineteen entry rules of three nodes each. The exit rule is not evaluated: the account is
        // flat, and a flat program has nothing to exit.
        Assert.Equal(57, run.Counters.PeakOperations);
    }

    /// <summary>
    /// A rolling extreme's worst bar costs its period, and the peak the run reports is that worst bar —
    /// which is the number <see cref="StrategyLimits.MaxOperationsPerEvent"/> is sized against.
    /// </summary>
    [Fact]
    public void The_peak_operations_a_run_reports_are_its_worst_single_event()
    {
        var program = Program("""
            instrument BTCUSDT
            indicator top = highest(high, 20)
            size fixed 1
            exit when close < 1
            entry when close > top
            """);

        var run = StrategyEvaluator.Run(program, SyntheticBars.Forty, (_, _) => AccountReading.Flat(10_000m));

        Assert.False(run.Faulted);
        Assert.InRange(run.Counters.PeakOperations, 20, StrategyLimits.MaxOperationsPerEvent);
        Assert.InRange(run.Counters.PeakStateBytes, 1, StrategyLimits.MaxStateBytes);
    }

    /// <summary>
    /// THE STATE-SIZE LIMIT. A program's state does not grow while it runs, so a state over the limit
    /// is refused on the FIRST bar and the run never starts — the only useful moment to refuse.
    /// </summary>
    [Fact]
    public void A_run_whose_state_is_over_the_limit_halts_on_its_first_bar()
    {
        var run = StrategyEvaluator.Run(
            TwentySmallRules(),
            SyntheticBars.Minutes(Monday, 5),
            (_, _) => AccountReading.Flat(10_000m),
            EvaluationLimits.Of(StrategyLimits.MaxOperationsPerEvent, 16));

        Assert.True(run.Faulted);
        Assert.Contains("bytes of state between bars, and the limit is 16", run.FaultReason!,
            StringComparison.Ordinal);
        Assert.Equal(1, run.Counters.Bars);
        Assert.Empty(run.Intents);
    }

    /// <summary>
    /// A DIVISION BY ZERO IS A VALUE. `close / (close - close)` is a program a model can write and a
    /// parser cannot refuse — the divisor is only zero on a bar — so it is answered with a fault rather
    /// than a `DivideByZeroException`.
    /// </summary>
    [Fact]
    public void A_division_by_zero_in_a_rule_is_a_defined_outcome()
    {
        var program = Program("""
            instrument BTCUSDT
            size fixed 1
            exit when close < 1
            entry when close / (close - close) > 0
            """);

        var run = StrategyEvaluator.Run(program, SyntheticBars.Minutes(Monday, 3),
            (_, _) => AccountReading.Flat(10_000m));

        Assert.True(run.Faulted);
        Assert.Contains("divided", run.FaultReason!, StringComparison.Ordinal);
        Assert.Contains("by zero", run.FaultReason!, StringComparison.Ordinal);
        Assert.Empty(run.Intents);
        Assert.Equal(1, run.Counters.Bars);
    }

    /// <summary>
    /// Arithmetic that overflows the largest number this build can hold is a fault too. A program may
    /// name any number the type can hold, and multiplying two of them is not something the parser can
    /// decide about.
    /// </summary>
    [Fact]
    public void Arithmetic_that_overflows_is_a_defined_outcome()
    {
        var program = Program("""
            instrument BTCUSDT
            size fixed 1
            exit when close < 1
            entry when close * 79228162514264337593543950335 > 0
            """);

        var run = StrategyEvaluator.Run(program, SyntheticBars.Minutes(Monday, 3),
            (_, _) => AccountReading.Flat(10_000m));

        Assert.True(run.Faulted);
        Assert.Contains("overflowed the largest number this build can hold", run.FaultReason!,
            StringComparison.Ordinal);
        Assert.Empty(run.Intents);
    }

    /// <summary>
    /// A TIMEOUT IS THE CALLER'S CLOCK AND THE EVALUATOR'S DEFINED OUTCOME. There is no clock inside
    /// the evaluator — it would make the same bars produce different results on a slower machine — so
    /// the caller holds the deadline and a stopped run halts with a reason, exactly as a fault does.
    /// </summary>
    [Fact]
    public void A_run_the_caller_stopped_halts_with_a_reason_and_no_further_intent()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        var run = StrategyEvaluator.Run(
            TwentySmallRules(),
            SyntheticBars.Minutes(Monday, 5),
            (_, _) => AccountReading.Flat(10_000m),
            stop: stop.Token);

        Assert.True(run.Faulted);
        Assert.Contains("the run was stopped after 0 bars", run.FaultReason!, StringComparison.Ordinal);
        Assert.Empty(run.Intents);
        Assert.Equal(0, run.Counters.Bars);
    }

    [Fact]
    public void A_run_stopped_part_way_keeps_the_intents_it_had_already_emitted()
    {
        var program = Program("""
            instrument BTCUSDT
            size fixed 1
            exit when close < 1
            entry when close > 0
            """);

        using var stop = new CancellationTokenSource();
        var bars = SyntheticBars.Minutes(Monday, 10);

        var run = StrategyEvaluator.Run(
            program,
            bars.Select(b =>
            {
                if (b.OpenTime == bars[4].OpenTime) stop.Cancel();
                return b;
            }),
            (_, _) => AccountReading.Flat(10_000m),
            stop: stop.Token);

        Assert.True(run.Faulted);
        Assert.Contains("the run was stopped after 4 bars", run.FaultReason!, StringComparison.Ordinal);
        Assert.Equal(4, run.Counters.Bars);
        Assert.NotEmpty(run.Intents);
        Assert.All(run.Intents, i => Assert.True(i.Bar < bars[4].OpenTime));
    }

    /// <summary>
    /// A FAULT MEANS NO NEW EXPOSURE. Once a run has faulted, the state stays faulted, every further
    /// event answers with the same reason, and nothing is emitted — the open position is the app's
    /// protection policy's business, and `U-runner-3`'s.
    /// </summary>
    [Fact]
    public void A_faulted_state_emits_nothing_ever_again()
    {
        var program = Program("""
            instrument BTCUSDT
            size fixed 1
            exit when close < 1
            entry when close / (close - close) > 0
            """);

        var state = EvaluationState.Start(program);
        var bars = SyntheticBars.Minutes(Monday, 4);

        var first = StrategyEvaluator.Step(state, bars[0], AccountReading.Flat(10_000m));
        Assert.Equal(EvaluationStatus.Faulted, first.Status);

        foreach (var bar in bars.Skip(1))
        {
            var again = StrategyEvaluator.Step(state, bar, AccountReading.Flat(10_000m));
            Assert.Equal(EvaluationStatus.Faulted, again.Status);
            Assert.Equal(first.FaultReason, again.FaultReason);
            Assert.Null(again.Intent);
        }

        Assert.Equal(1, state.Counters.Bars);
        Assert.Equal(0, state.Counters.Intents);
    }
}

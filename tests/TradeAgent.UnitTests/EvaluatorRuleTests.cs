using TradeAgent.Core.Data;
using TradeAgent.Core.Strategy;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEM 4 — THE RULE ENGINE ON ONE CLOSED BAR.
///
/// <para>`docs/COUNCIL.md`, the strategy language: "Entry and exit emit bounded market intents; exit
/// precedes entry; no same-event reversal, no duplicate entry while one is pending" and "A signal from
/// a completed bar executes only afterwards". Four rules, and they are four separate guards here
/// because no two of them follow from each other — the order of the blocks is not the
/// exit-ends-the-event guard, and neither is the pending guard.</para>
///
/// <para><b>The evaluator emits and places nothing.</b> Every test below reads an intent, and there is
/// no connector, no order and no clock anywhere in the path that produced it. What an intent becomes
/// is `U-runner-3`'s.</para>
/// </summary>
public class EvaluatorRuleTests
{
    static readonly DateTimeOffset Monday = new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);

    static StrategyProgram Program(string text)
    {
        var parse = StrategyParser.Parse(text);
        Assert.True(parse.Ok, parse.Why);
        return parse.Program!;
    }

    static AccountReading Long(decimal quantity = 1m, decimal entry = 100m, int barsSinceEntry = 1) =>
        new(10_000m, 10_000m, PositionSide.Long, quantity, entry, false, barsSinceEntry);

    static EvaluationRun RunOn(StrategyProgram program, IReadOnlyList<KlineBar> bars, AccountReading account) =>
        StrategyEvaluator.Run(program, bars, (_, _) => account);

    /// <summary>Two entry rules, both true on every bar. One position; one intent.</summary>
    static StrategyProgram TwoEntries() => Program("""
        instrument BTCUSDT
        size fixed 2
        exit when close < 1
        entry when close > 0
        entry when close > 1
        """);

    /// <summary>One exit and one entry, both true on every bar. The reversal case.</summary>
    static StrategyProgram ExitAndEntryBothTrue() => Program("""
        instrument BTCUSDT
        size fixed 1
        exit when close > 0
        entry when close > 0
        """);

    /// <summary>
    /// AT MOST ONE INTENT PER EVENT. A program may declare twenty rules and several of them may be
    /// true on one bar; it is one position, so the first one that fires is the answer and the rest are
    /// the same entry said twice. The rule index says which one it was.
    /// </summary>
    [Fact]
    public void One_event_yields_at_most_one_intent_however_many_rules_are_true()
    {
        var program = TwoEntries();
        var run = RunOn(program, SyntheticBars.Minutes(Monday, 1), AccountReading.Flat(10_000m));

        var intent = Assert.Single(run.Intents);
        Assert.Equal(IntentKind.Enter, intent.Kind);
        Assert.Equal(1, run.Counters.Intents);

        // The FIRST entry rule in declared order. Both were true.
        Assert.Equal(RuleKind.Entry, program.Rules[intent.RuleIndex].Kind);
        Assert.Equal(program.Rules.Select((r, i) => (r, i)).First(x => x.r.Kind == RuleKind.Entry).i,
            intent.RuleIndex);
    }

    /// <summary>
    /// NO SAME-EVENT REVERSAL. The exit fires, the position is flat again, and the entry rule is true
    /// on the very same bar — and nothing is entered. A program that left and re-entered on one bar
    /// would have taken two positions from one observation at one price.
    /// </summary>
    [Fact]
    public void An_exit_that_fires_ends_the_event_and_nothing_is_re_entered_on_it()
    {
        var run = RunOn(ExitAndEntryBothTrue(), SyntheticBars.Minutes(Monday, 1), Long(quantity: 3m));

        var intent = Assert.Single(run.Intents);
        Assert.Equal(IntentKind.Exit, intent.Kind);
        Assert.Equal(IntentCause.Rule, intent.Cause);
        Assert.Equal(3m, intent.Quantity);              // exactly what the caller says is open
        Assert.DoesNotContain(run.Intents, i => i.Kind == IntentKind.Enter);
    }

    /// <summary>
    /// Exits are evaluated first, and a FLAT program has nothing to exit: the same program on the same
    /// bar enters instead. Which of the two happens is the account reading's to say, not the
    /// evaluator's.
    /// </summary>
    [Fact]
    public void A_flat_program_has_nothing_to_exit_and_enters_on_the_same_bar()
    {
        var run = RunOn(ExitAndEntryBothTrue(), SyntheticBars.Minutes(Monday, 1), AccountReading.Flat(10_000m));

        var intent = Assert.Single(run.Intents);
        Assert.Equal(IntentKind.Enter, intent.Kind);
    }

    /// <summary>
    /// A SIGNAL FROM BAR N EXECUTES ONLY AFTER IT. The intent names the bar it came from and the
    /// earliest instant it may be acted on — that bar's CLOSE. Anything that filled it at the bar's
    /// own open would be reading a price the signal had not happened at yet.
    /// </summary>
    [Fact]
    public void An_intent_names_the_bar_it_came_from_and_is_not_executable_before_its_close()
    {
        var bars = SyntheticBars.Minutes(Monday, 3);
        var run = StrategyEvaluator.Run(TwoEntries(), bars, (_, _) => AccountReading.Flat(10_000m));

        // Bar 0 signals; bar 1 is blocked by the intent still held; bar 2 signals again.
        Assert.Equal(2, run.Intents.Count);

        var first = run.Intents[0];
        Assert.Equal(bars[0].OpenTime, first.Bar);
        Assert.Equal(0, first.BarOrdinal);
        Assert.Equal(bars[0].OpenTime.AddMinutes(1), first.NotBefore);
        Assert.Equal(bars[1].OpenTime, first.NotBefore);
        Assert.Equal(bars[0].Close, first.ReferencePrice);

        Assert.Equal(bars[2].OpenTime, run.Intents[1].Bar);
        Assert.Equal(2, run.Intents[1].BarOrdinal);
    }

    /// <summary>
    /// NO DUPLICATE ENTRY WHILE ONE IS PENDING. The caller's pending-order state is the authority —
    /// it knows what it placed — and the intent the evaluator emitted covers the one event before the
    /// caller has had a chance to report.
    /// </summary>
    [Fact]
    public void Nothing_is_emitted_while_the_caller_says_an_order_is_pending()
    {
        var pending = AccountReading.Flat(10_000m) with { OrderPending = true };

        var run = RunOn(TwoEntries(), SyntheticBars.Minutes(Monday, 5), pending);

        Assert.Empty(run.Intents);
        Assert.Equal(5, run.Counters.SignalsWhilePending);
        Assert.Equal(0, run.Counters.Intents);
    }

    [Fact]
    public void The_intent_just_emitted_blocks_the_next_event_and_no_more_than_that()
    {
        var bars = SyntheticBars.Minutes(Monday, 6);
        var run = RunOn(TwoEntries(), bars, AccountReading.Flat(10_000m));

        Assert.Equal([bars[0].OpenTime, bars[2].OpenTime, bars[4].OpenTime],
            run.Intents.Select(i => i.Bar));
        Assert.Equal(3, run.Counters.SignalsWhilePending);
    }

    /// <summary>
    /// A pending EXIT is not repeated either. `docs/COUNCIL.md` names the duplicate entry, and a
    /// second exit for a position that is already being closed is the same mistake with the same
    /// consequence: two orders from one signal.
    /// </summary>
    [Fact]
    public void A_pending_order_stops_a_second_exit_as_well()
    {
        var working = Long(quantity: 2m) with { OrderPending = true };

        var run = RunOn(ExitAndEntryBothTrue(), SyntheticBars.Minutes(Monday, 4), working);

        Assert.Empty(run.Intents);
        Assert.Equal(4, run.Counters.SignalsWhilePending);
    }

    /// <summary>
    /// THE SCHEDULED SESSION EXIT. At or after the declared wall clock the position does not survive
    /// the bar, and the intent says that is why — not the rule that happened to be true as well.
    /// </summary>
    [Fact]
    public void The_declared_session_exit_closes_the_position_at_its_wall_clock()
    {
        var program = Program("""
            instrument BTCUSDT
            timezone UTC
            size fixed 1
            session_exit 15:55
            exit when close < 1
            entry when close > 0
            """);

        var before = new DateTimeOffset(2026, 1, 5, 15, 54, 0, TimeSpan.Zero);

        var quiet = RunOn(program, SyntheticBars.Minutes(before, 1), Long());
        Assert.Empty(quiet.Intents);

        var at = RunOn(program, SyntheticBars.Minutes(before.AddMinutes(1), 1), Long());
        var intent = Assert.Single(at.Intents);
        Assert.Equal(IntentKind.Exit, intent.Kind);
        Assert.Equal(IntentCause.SessionExit, intent.Cause);
        Assert.Equal(-1, intent.RuleIndex);              // no rule produced it
    }

    /// <summary>The holding limit, counted in the caller's bars since entry.</summary>
    [Fact]
    public void The_declared_holding_limit_closes_the_position_when_it_is_reached()
    {
        var program = Program("""
            instrument BTCUSDT
            size fixed 1
            max_hold_bars 60
            exit when close < 1
            entry when close > 0
            """);

        Assert.Empty(RunOn(program, SyntheticBars.Minutes(Monday, 1), Long(barsSinceEntry: 59)).Intents);

        var reached = RunOn(program, SyntheticBars.Minutes(Monday, 1), Long(barsSinceEntry: 60));
        var intent = Assert.Single(reached.Intents);
        Assert.Equal(IntentCause.MaxHoldBars, intent.Cause);
    }

    /// <summary>
    /// The weekday set and the entry windows gate ENTRIES, and a rule that fired outside them is
    /// counted rather than silently dropped — a program that wanted in every minute of a week it may
    /// not trade is a different diagnosis from one whose rule never fired.
    /// </summary>
    [Fact]
    public void An_entry_outside_the_weekdays_or_the_entry_window_is_not_taken_and_is_counted()
    {
        var program = Program("""
            instrument BTCUSDT
            timezone UTC
            size fixed 1
            weekdays mon,tue,wed,thu,fri
            entry_window 09:30-15:30
            max_hold_bars 60
            exit when close < 1
            entry when close > 0
            """);

        var inside = new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero);          // Monday
        Assert.Single(RunOn(program, SyntheticBars.Minutes(inside, 1), AccountReading.Flat(10_000m)).Intents);

        var tooEarly = RunOn(program, SyntheticBars.Minutes(inside.AddHours(-2), 1), AccountReading.Flat(10_000m));
        Assert.Empty(tooEarly.Intents);
        Assert.Equal(1, tooEarly.Counters.EntriesOutsideTimeFilters);

        var saturday = RunOn(program, SyntheticBars.Minutes(inside.AddDays(5), 1), AccountReading.Flat(10_000m));
        Assert.Empty(saturday.Intents);
        Assert.Equal(1, saturday.Counters.EntriesOutsideTimeFilters);
    }

    /// <summary>
    /// EXITS ARE NOT TIME-FILTERED. A program that could only leave a position inside its entry window
    /// would be a program that cannot get out, and that is not a filter.
    /// </summary>
    [Fact]
    public void An_exit_is_taken_outside_the_entry_window_and_outside_the_weekdays()
    {
        var program = Program("""
            instrument BTCUSDT
            timezone UTC
            size fixed 1
            weekdays mon,tue,wed,thu,fri
            entry_window 09:30-15:30
            exit when close > 0
            entry when close < 1
            """);

        var saturdayNight = new DateTimeOffset(2026, 1, 10, 23, 0, 0, TimeSpan.Zero);

        var run = RunOn(program, SyntheticBars.Minutes(saturdayNight, 1), Long(quantity: 4m));

        var intent = Assert.Single(run.Intents);
        Assert.Equal(IntentKind.Exit, intent.Kind);
        Assert.Equal(4m, intent.Quantity);
    }

    /// <summary>
    /// A CROSSING READS THIS BAR AND THE ONE BEFORE — and is therefore undefined on the first bar it
    /// could otherwise have answered on, which is why `StrategyWarmUp` charges it an extra bar.
    /// </summary>
    [Fact]
    public void A_crossing_is_true_only_on_the_bar_the_sides_crossed()
    {
        var program = Program("""
            instrument BTCUSDT
            size fixed 1
            max_hold_bars 60
            exit when close < 1
            entry when crosses_above(close, 100)
            """);

        Assert.Equal(2, program.WarmUpBars);

        // 99, 99, 101, 102: the crossing happens on the third bar and not on the fourth.
        var closes = new[] { 99m, 99m, 101m, 102m };
        var bars = SyntheticBars.Minutes(Monday, 4, i => closes[i]);

        var run = RunOn(program, bars, AccountReading.Flat(10_000m));

        var intent = Assert.Single(run.Intents);
        Assert.Equal(bars[2].OpenTime, intent.Bar);
        Assert.Equal(1, run.Counters.WarmingUpEvents);
    }

    /// <summary>
    /// THE THREE SIZINGS, over the account state the caller supplied, and the stop and target stated
    /// at the signal bar's close. Nothing here is rounded to an instrument increment: a dataset has
    /// none, and rounding down to it is `U-runner-3`'s with the gateway's own limits.
    /// </summary>
    [Fact]
    public void A_fixed_quantity_is_the_quantity_and_a_percent_stop_and_target_are_off_the_close()
    {
        var program = Program("""
            instrument BTCUSDT
            size fixed 3
            stop percent 2
            target percent 1
            exit when close < 1
            entry when close > 0
            """);

        var run = RunOn(program, SyntheticBars.Minutes(Monday, 1, _ => 200m), AccountReading.Flat(10_000m));

        var intent = Assert.Single(run.Intents);
        Assert.Equal(3m, intent.Quantity);
        Assert.Equal(200m, intent.ReferencePrice);
        Assert.Equal(196m, intent.StopPrice);
        Assert.Equal(202m, intent.TargetPrice);
        Assert.Equal(new Sizing(SizingKind.FixedQuantity, 3m), intent.Sizing);
    }

    [Fact]
    public void A_capital_fraction_is_the_caller_s_capital_over_the_close()
    {
        var program = Program("""
            instrument BTCUSDT
            size capital_fraction 0.25
            stop percent 2
            exit when close < 1
            entry when close > 0
            """);

        var run = RunOn(program, SyntheticBars.Minutes(Monday, 1, _ => 200m), AccountReading.Flat(8_000m));

        var intent = Assert.Single(run.Intents);
        Assert.Equal(10m, intent.Quantity);             // 8000 * 0.25 / 200
    }

    [Fact]
    public void A_risk_fraction_is_the_caller_s_equity_over_the_stop_distance()
    {
        var program = Program("""
            instrument BTCUSDT
            size risk_fraction 0.01
            stop percent 2
            exit when close < 1
            entry when close > 0
            """);

        var run = RunOn(program, SyntheticBars.Minutes(Monday, 1, _ => 200m), AccountReading.Flat(50_000m));

        var intent = Assert.Single(run.Intents);
        Assert.Equal(196m, intent.StopPrice);
        Assert.Equal(125m, intent.Quantity);           // 50000 * 0.01 / (200 - 196)
    }

    /// <summary>
    /// An ATR stop is measured at the ENTRY bar and then held, and the evaluator keeps the stop's own
    /// ATR series for it — a program may write `stop atr 2 3` without declaring an `atr(3)` indicator,
    /// and `StrategyWarmUp` has already charged the period.
    /// </summary>
    [Fact]
    public void An_atr_stop_is_the_atr_at_the_signal_bar()
    {
        var program = Program("""
            instrument BTCUSDT
            size fixed 1
            stop atr 2 3
            exit when close < 1
            entry when close > 0
            """);

        Assert.Equal(4, program.WarmUpBars);

        var run = RunOn(program, SyntheticBars.Forty, AccountReading.Flat(10_000m));
        var first = run.Intents[0];

        // atr(3) on bar 3 of the fixture is exactly 1.55 (three true ranges of 1.55).
        Assert.Equal(SyntheticBars.Forty[3].OpenTime, first.Bar);
        Assert.Equal(SyntheticBars.Forty[3].Close - 2m * 1.55m, first.StopPrice);
    }

    /// <summary>
    /// A stop that is not below the reference price has no risk distance, so risk sizing over it is a
    /// DEFINED fault rather than a negative or infinite quantity.
    /// </summary>
    [Fact]
    public void Risk_sizing_against_a_stop_that_is_not_below_the_close_is_a_defined_fault()
    {
        var program = Program("""
            instrument BTCUSDT
            size risk_fraction 0.01
            stop fixed 500
            exit when close < 1
            entry when close > 0
            """);

        var run = RunOn(program, SyntheticBars.Minutes(Monday, 1, _ => 200m), AccountReading.Flat(10_000m));

        Assert.True(run.Faulted);
        Assert.Contains("no risk distance to size against", run.FaultReason!, StringComparison.Ordinal);
        Assert.Empty(run.Intents);
    }

    /// <summary>
    /// An account that reads Long with nothing open is a caller's inconsistency, and an exit intent
    /// for a quantity of zero would be an order nobody can place. It is a defined fault; the
    /// evaluator does not invent a quantity.
    /// </summary>
    [Fact]
    public void An_account_that_reads_long_with_no_quantity_is_a_defined_fault()
    {
        var run = RunOn(ExitAndEntryBothTrue(), SyntheticBars.Minutes(Monday, 1),
            Long() with { Quantity = 0m });

        Assert.True(run.Faulted);
        Assert.Contains("no position for an exit to close", run.FaultReason!, StringComparison.Ordinal);
        Assert.Empty(run.Intents);
    }

    /// <summary>
    /// An account with no capital cannot act, and that is NOT a fault: a run that halted over it would
    /// lose the rest of the window. The entry is counted and the run goes on.
    /// </summary>
    [Fact]
    public void An_entry_that_sizes_to_nothing_is_counted_and_the_run_continues()
    {
        var program = Program("""
            instrument BTCUSDT
            size capital_fraction 0.5
            stop percent 2
            exit when close < 1
            entry when close > 0
            """);

        var run = RunOn(program, SyntheticBars.Minutes(Monday, 3), AccountReading.Flat(0m));

        Assert.False(run.Faulted);
        Assert.Empty(run.Intents);
        Assert.Equal(3, run.Counters.EntriesWithoutSize);
        Assert.Equal(3, run.Counters.Bars);
    }
}

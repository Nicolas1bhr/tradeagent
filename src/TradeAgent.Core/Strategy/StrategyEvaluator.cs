using TradeAgent.Core.Data;
using TradeAgent.Core.Features;

namespace TradeAgent.Core.Strategy;

/// <summary>
/// EVERYTHING ONE RUN CARRIES BETWEEN BARS — the indicator windows, the recent bars, the counters and
/// the fault, if there is one.
///
/// <para><b>One state object, advanced, not copied.</b> The evaluator is pure in the sense that
/// matters: it reads nothing but its arguments, writes nothing but this, and contains no clock, no
/// randomness and no I/O — the same bars, program and account readings produce the same intents on
/// every machine, every time. It is NOT immutable: sixteen indicator windows of up to five hundred
/// decimals each, copied once per bar over half a million bars, is not a design.</para>
///
/// <para><b>The zone is resolved once, here.</b> A program names one of
/// <see cref="StrategyLimits.TimeZones"/> and <see cref="StrategyCalendar"/> holds the rules; looking
/// it up per bar would be the same answer half a million times.</para>
/// </summary>
public sealed class EvaluationState
{
    readonly List<GapRun> gaps = [];

    EvaluationState(StrategyProgram program, ZoneRules zone, BarGrid grid, EvaluationLimits limits)
    {
        Program = program;
        Zone = zone;
        Grid = grid;
        Limits = limits;
        Indicators = IndicatorSet.For(program);
        Features = FeatureHistory.For(program);
        History = new BarHistory();

        if (program.Stop.Kind == StopKind.EntryAtr)
            StopAtr = IndicatorSeries.For(
                new IndicatorDecl("", IndicatorKind.Atr, BarSeries.Close, program.Stop.AtrPeriod));
    }

    /// <summary>The frozen program being evaluated.</summary>
    public StrategyProgram Program { get; }

    /// <summary>The calendar rules for the program's declared zone.</summary>
    public ZoneRules Zone { get; }

    /// <summary>The per-event operation budget and the state-size limit this run is under.</summary>
    public EvaluationLimits Limits { get; }

    /// <summary>
    /// How long one bar is, which is what makes a gap a gap. Stated rather than assumed: a run over
    /// five-minute candles would count every minute of every bar as missing if this were hard-wired.
    /// It is the program's own declared bar (<see cref="StrategyProgram.Bars"/>) — nominally, for a day
    /// whose length a daylight change moved; <see cref="Grid"/> has the exact edges.
    /// </summary>
    public TimeSpan BarInterval => Grid.Length;

    /// <summary>
    /// THE GRID THIS RUN'S BARS ARE ON — the program's declared bars, cut in UTC or at its zone's
    /// midnight for a day. Where a bar ends, which is when an intent from it may first be acted on, and
    /// how many minutes it spans, which is how many it can be missing.
    /// </summary>
    public BarGrid Grid { get; }

    internal IndicatorSet Indicators { get; }

    internal FeatureHistory Features { get; }

    internal BarHistory History { get; }

    /// <summary>Closed bars fed so far.</summary>
    public long Bars { get; private set; }

    /// <summary>
    /// MINUTES THIS RUN HAD NO BAR FOR — never filled, always counted, and always MINUTES, whatever bar
    /// the program declares.
    ///
    /// <para>On one-minute bars that is every minute between two bars this run saw. On a declared bar it
    /// is the same thing measured against the bars the program was asked on: every minute of a window that
    /// produced no bar at all — an empty hour is sixty — and every minute a partial bar is short of its
    /// window (<see cref="KlineBar.Minutes"/>), wherever in the window those minutes fell, because the bar
    /// the rules read was built without them.</para>
    /// </summary>
    public long MissingMinutes { get; private set; }

    /// <summary>The gap runs crossed, bounded by <see cref="KlineNormaliser.MaxGapRunsListed"/>.</summary>
    public IReadOnlyList<GapRun> GapRuns => gaps;

    /// <summary>Whether the gap-run LIST was cut short. The count never is.</summary>
    public bool GapRunsTruncated { get; private set; }

    /// <summary>Why the run halted, or null while it has not.</summary>
    public string? FaultReason { get; private set; }

    public bool Faulted => FaultReason is not null;

    /// <summary>The open time of the last bar fed, or null before the first.</summary>
    public DateTimeOffset? LastBar { get; private set; }

    /// <summary>The session the last bar belonged to, in the program's zone.</summary>
    public DateOnly? Session { get; private set; }

    /// <summary>Whether the program has seen its declared warm-up in closed bars.</summary>
    public bool Warm => Bars >= Program.WarmUpBars;

    internal long GapRunCount { get; private set; }

    internal long WarmingUpEvents { get; private set; }

    internal long UndefinedEvents { get; private set; }

    internal long EvaluatedEvents { get; private set; }

    internal long IntentsEmitted { get; private set; }

    internal long EntriesOutsideTimeFilters { get; private set; }

    internal long SignalsWhilePending { get; private set; }

    internal long EntriesWithoutSize { get; private set; }

    internal int PeakOperations { get; private set; }

    internal int PeakStateBytes { get; private set; }

    /// <summary>
    /// A DECLARED INDICATOR'S VALUE as this run has it — null while it is not warm, and null for a
    /// name the program did not declare.
    ///
    /// <para>Read-only and here because a run's report and its trace (`U-runner-3`) have to be able
    /// to say what the program was looking at when it signalled. Nothing can set one.</para>
    /// </summary>
    public decimal? IndicatorValue(string name, int back = 0) => Indicators.Value(name, back);

    /// <summary>
    /// A DECLARED FEATURE'S VALUE AT THE CLOSE OF THE BAR <paramref name="back"/> BARS AGO, as the caller handed it —
    /// null where it was absent, before this run evaluated that many bars, and for a name the program did not declare.
    /// Read-only, like <see cref="IndicatorValue"/>: nothing outside the evaluator's own step can set one.
    /// </summary>
    public decimal? FeatureValue(string name, int back = 0) => Features.Value(name, back);

    /// <summary>
    /// THE STOP'S OWN ATR. A program may write `stop atr 2 20` without declaring an `atr(20)`
    /// indicator — `StrategyWarmUp` already charges the stop's period, so the language expects the
    /// distance to be measurable — and the evaluator keeps its own series for it. Null for any other
    /// kind of stop.
    /// </summary>
    internal IndicatorSeries? StopAtr { get; }

    /// <summary>
    /// THE INTENT THIS EVALUATOR EMITTED AND HAS NOT SEEN ANSWERED.
    ///
    /// <para>`docs/COUNCIL.md` forbids a duplicate entry while one is pending, and the caller's
    /// <see cref="AccountReading.OrderPending"/> is the AUTHORITY on that — it knows what it placed.
    /// This covers the one event in between: an intent emitted at bar n is held over bar n+1, so a
    /// caller that has not yet had time to report cannot be handed the same signal twice. It survives
    /// exactly one event unless the caller says an order is still pending.</para>
    /// </summary>
    public StrategyIntent? Pending { get; private set; }

    /// <summary>Everything this run counted, as a snapshot.</summary>
    public EvaluationCounters Counters => new(
        Bars, MissingMinutes, GapRunCount, WarmingUpEvents, UndefinedEvents, EvaluatedEvents,
        IntentsEmitted, EntriesOutsideTimeFilters, SignalsWhilePending, EntriesWithoutSize,
        PeakOperations, PeakStateBytes);

    /// <summary>
    /// The state a run starts in: nothing seen, nothing warm.
    ///
    /// <para><b>The bars are the program's own.</b> A run is on the bar the program declares
    /// (<see cref="StrategyProgram.Bars"/>; one minute, what `docs/COUNCIL.md` says a bar is and what every
    /// dataset holds, when it declares none). <paramref name="barInterval"/> is the caller saying which bars
    /// it will feed, and one that is not the declared bar is REFUSED: an hourly program stepped on minutes,
    /// or a minute program on hours, is a different strategy wearing the version's id, and every figure or
    /// order it produced would be about a program nobody judged.</para>
    /// </summary>
    public static EvaluationState Start(
        StrategyProgram program, EvaluationLimits? limits = null, TimeSpan? barInterval = null)
    {
        ArgumentNullException.ThrowIfNull(program);

        if (barInterval is { } interval && interval != program.Bars)
            throw new ArgumentException(
                $"this program declares bars of {StrategyBars.Spelled(program.Bars)}, and this run was asked to " +
                $"evaluate it on bars of {StrategyBars.Spelled(interval)}: a program is evaluated on the bar it " +
                "declares or not at all",
                nameof(barInterval));

        // A program can only name a zone from the allowlist, so this cannot miss for a parsed
        // program; a state built around a zone this build has no rules for would read every wall
        // clock as UTC and honour the wrong windows, so it is refused rather than defaulted.
        if (!StrategyCalendar.TryZone(program.Time.TimeZone, out var zone))
            throw new ArgumentException(
                $"this build has no calendar for the zone '{program.Time.TimeZone}'", nameof(program));

        return new EvaluationState(program, zone, BarGrid.For(program), limits ?? EvaluationLimits.Default);
    }

    /// <summary>
    /// Halts the run with a reason, and answers the reason as spelled. There is no route back: a faulted
    /// state stays faulted, on the first reason it was given. The words are <see cref="TraceText"/>, for the
    /// reason <see cref="EvaluationOutcome.Faulted"/> gives.
    /// </summary>
    internal string Fault(TraceText reason)
    {
        var spelled = reason.ToStringAndClear();
        FaultReason ??= spelled;
        return spelled;
    }

    /// <summary>
    /// Records one bar's arrival, the minutes missing in front of it and inside it, and the session it
    /// belongs to. <paramref name="from"/> and <paramref name="to"/> are the open times of the first and
    /// last bar those minutes belong to — the empty windows in front of this bar, and this bar itself when
    /// it is partial.
    /// </summary>
    internal void Advance(KlineBar bar, DateOnly session, long missingMinutes, DateTimeOffset from, DateTimeOffset to)
    {
        if (missingMinutes > 0)
        {
            MissingMinutes += missingMinutes;
            GapRunCount++;

            if (gaps.Count < KlineNormaliser.MaxGapRunsListed)
                gaps.Add(new GapRun(from, to, (int)Math.Min(missingMinutes, int.MaxValue)));
            else
                GapRunsTruncated = true;
        }

        LastBar = bar.OpenTime;
        Session = session;
        Bars++;
    }

    /// <summary>
    /// The held intent does not survive a second event. It is cleared at the START of an event in
    /// which the caller reports no pending order, because by then the caller has had one whole event
    /// to place it or refuse it and its reading is the authority either way.
    /// </summary>
    internal void AgePending(AccountReading account)
    {
        if (!account.OrderPending) Pending = null;
    }

    internal void Emitted(StrategyIntent intent)
    {
        Pending = intent;
        IntentsEmitted++;
    }

    internal void CountWarmingUp() => WarmingUpEvents++;

    internal void CountUndefined() => UndefinedEvents++;

    internal void CountEvaluated() => EvaluatedEvents++;

    internal void CountEntryOutsideTimeFilters() => EntriesOutsideTimeFilters++;

    internal void CountSignalWhilePending() => SignalsWhilePending++;

    internal void CountEntryWithoutSize() => EntriesWithoutSize++;

    internal void Charged(int operations, int stateBytes)
    {
        if (operations > PeakOperations) PeakOperations = operations;
        if (stateBytes > PeakStateBytes) PeakStateBytes = stateBytes;
    }

    /// <summary>How many bytes of decimals this run is holding, for the state-size limit.</summary>
    internal int StateBytes => (Indicators.StateSlots + Features.StateSlots + History.StateSlots) * sizeof(decimal);
}

/// <summary>
/// THE EVALUATOR: CLOSED BARS OF ONE DATASET IN, BOUNDED INTENTS OUT, A FAULT A DEFINED OUTCOME.
///
/// <para>`docs/COUNCIL.md`, "The runner and the referee": a promoted strategy receives "only
/// observations available at simulated time t" and returns "bounded intents", and "an interpreter
/// fault or a timeout is a defined outcome". Both halves are literal here. The only observations are
/// the bar just closed and the ones before it; there is no way to spell the bar that is forming, so
/// nothing can read the future. And every way this can go wrong comes back as
/// <see cref="EvaluationOutcome.Faulted"/> with a reason rather than as an exception.</para>
///
/// <para><b>It emits and places nothing.</b> An intent names the bar it came from and the instant it
/// may first execute (`NotBefore`, that bar's close). What happens after that is `U-runner-3`'s: this
/// assembly has no connector, no order and no clock.</para>
///
/// <para><b>A gap is not a bar.</b> A minute with no bar advances no lookback, feeds no indicator and
/// is counted. `KlineNormaliser` refuses to fill one when it writes the dataset; this refuses to fill
/// one when it reads it. The two together are what `docs/COUNCIL.md`'s "missing data reported and
/// never filled" means in practice — and the failure it prevents is silent, because a mean over a
/// window with a hole in it looks exactly like a mean over a window without one.</para>
/// </summary>
public static class StrategyEvaluator
{
    /// <summary>
    /// ONE CLOSED BAR. Feeds the indicators, counts the gap in front of it, and answers what the
    /// event produced.
    ///
    /// <para>Evaluation before <see cref="StrategyProgram.WarmUpBars"/> is REFUSED rather than
    /// answered from a half-filled window: an indicator one bar early is a different indicator with
    /// the same name, and a run over it contains trades the program would never have taken.</para>
    /// </summary>
    public static EvaluationOutcome Step(EvaluationState state, KlineBar bar, AccountReading account) =>
        Step(state, bar, account, null);

    /// <summary>
    /// ONE CLOSED BAR, WITH EACH DECLARED FEATURE'S VALUE AT ITS CLOSE (<c>U-language-v2a</c>) — in the program's declared
    /// order, as the caller read it from the tape as it had arrived by that close (<c>FeatureFeed</c>).
    ///
    /// <para><b>A program that reads a feature is stepped with its values or not at all.</b> Handed none, handed the
    /// wrong number, or handed one whose instant is not this bar's close — a value from after the close would be
    /// look-ahead, one from before it a stale input wearing the bar's name — it is a defined fault, never a bar run
    /// as though every feature were absent. An ABSENT value is a different thing and is no fault: it is undefined, so
    /// the event decides nothing, scheduled exits first.</para>
    /// </summary>
    public static EvaluationOutcome Step(
        EvaluationState state, KlineBar bar, AccountReading account, IReadOnlyList<FeatureValue>? features)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(account);

        if (state.FaultReason is { } halted)
            return new EvaluationOutcome(EvaluationStatus.Faulted, state, null, halted);

        // BARS IN ORDER IS AN INPUT CONTRACT, AND BREAKING IT IS A DEFINED OUTCOME. A run fed the
        // same minute twice would count a lookback twice; fed them backwards it would read a bar
        // before the one it has already answered from.
        if (state.LastBar is { } previous && bar.OpenTime <= previous)
            return EvaluationOutcome.Faulted(state,
                $"the bar at {bar.OpenTime:O} is not after the bar at {previous:O}: bars reach the " +
                $"evaluator in ascending order, one for each closed interval, and never twice");

        var grid = state.Grid;
        // The grid's words were spelled as trace words where it made them; they are carried, not spelled twice.
        if (grid.Refusal(state.LastBar, bar.OpenTime) is { } offGrid)
            return EvaluationOutcome.Faulted(state, $"{offGrid}");

        // THE MINUTES THIS BAR'S WINDOW SPANS, AND HOW MANY IT WAS BUILT FROM. A one-minute bar is built
        // from one minute and is never short. A declared bar may be partial; one that claims more minutes
        // than its window holds is not a bar of this series at all.
        var span = grid.MinutesIn(bar.OpenTime);
        if (bar.Minutes < 1 || bar.Minutes > span)
            return EvaluationOutcome.Faulted(state,
                $"the bar at {bar.OpenTime:O} says it was built from {bar.Minutes} minute(s), and one bar of " +
                $"{grid.Spelled} spans {span}: this run's bars are not one series");

        // THE FEATURES, AS THEY HAD ARRIVED BY THIS BAR'S CLOSE, or a defined fault — before anything is pushed.
        var declared = state.Program.Features;
        var featureCount = features?.Count ?? 0;
        if (declared.Count == 0 && featureCount > 0)
            return EvaluationOutcome.Faulted(state,
                $"this program reads no feature, and was handed {featureCount} value(s) for the bar at {bar.OpenTime:O}");
        if (declared.Count > 0 && featureCount != declared.Count)
            return EvaluationOutcome.Faulted(state,
                $"this program reads {declared.Count} feature(s) and was handed {featureCount} value(s) for the bar at " +
                $"{bar.OpenTime:O}: a program that reads a feature is run with its values or not at all, never as though " +
                $"every one were absent");

        decimal?[]? values = null;
        if (declared.Count > 0)
        {
            var close = grid.EndOf(bar.OpenTime);
            values = new decimal?[declared.Count];
            for (var i = 0; i < declared.Count; i++)
            {
                var value = features![i];
                if (value.At != close)
                    return EvaluationOutcome.Faulted(state,
                        $"the value handed for feature `{declared[i].Name}` is as of {value.At:O}, and the bar at " +
                        $"{bar.OpenTime:O} closes at {close:O}: a feature is read as it had arrived by the close of the " +
                        $"bar it is asked on, never at another instant");
                values[i] = value.Value;
            }
        }

        // MISSING, IN MINUTES: every minute of the windows in front of this bar that produced no bar at all,
        // and every minute this bar is short of its own window. On one-minute bars the second is always
        // nothing and the first is the gap the evaluator has always counted.
        var front = state.LastBar is { } last ? BarGrid.MinutesBetween(grid.EndOf(last), bar.OpenTime) : 0L;
        var shortfall = span - bar.Minutes;
        var missing = front + shortfall;
        var gapFrom = front > 0 ? grid.EndOf(state.LastBar!.Value) : bar.OpenTime;
        var gapTo = shortfall > 0 ? bar.OpenTime : grid.PreviousStart(bar.OpenTime);

        var local = StrategyCalendar.Local(state.Zone, bar.OpenTime);
        var session = StrategyCalendar.SessionOf(local);
        var newSession = state.Session != session;
        var inOpeningRange = state.Program.Time.OpeningRange is { } range
                             && StrategyCalendar.Contains(range, local);

        int operations;
        try
        {
            operations = state.Indicators.Push(bar, newSession, inOpeningRange);
            if (state.StopAtr is { } stopAtr) operations += stopAtr.Push(bar, newSession, inOpeningRange);
        }
        catch (OverflowException)
        {
            return EvaluationOutcome.Faulted(state, Overflowed(bar, "an indicator"));
        }

        if (values is not null) state.Features.Push(values);
        state.History.Push(bar);
        state.Advance(bar, session, missing, gapFrom, gapTo);
        state.Charged(operations, state.StateBytes);

        // THE STATE-SIZE LIMIT. A program's state does not grow while it runs — every window is
        // allocated from its declared period — so this refuses on the first bar rather than half way
        // through, which is the only useful moment to refuse.
        if (state.StateBytes > state.Limits.StateBytes)
            return EvaluationOutcome.Faulted(state,
                $"this program holds {state.StateBytes} bytes of state between bars, and the limit is " +
                $"{state.Limits.StateBytes}");

        if (operations > state.Limits.OperationsPerEvent)
            return EvaluationOutcome.Faulted(state, OverBudget(state, operations));

        if (!state.Warm)
        {
            state.CountWarmingUp();
            return EvaluationOutcome.Of(EvaluationStatus.WarmingUp, state);
        }

        state.CountEvaluated();

        // The rules are evaluated under what is LEFT of the event's budget after the indicators.
        var interpreter = new StrategyInterpreter(state, state.Limits.OperationsPerEvent - operations);
        StrategyIntent? intent;
        bool undefined;

        try
        {
            intent = Decide(state, bar, account, local, interpreter, out undefined);
        }
        catch (EvaluationFault fault)
        {
            // Spelled as trace words when it was thrown — an EvaluationFault cannot be made any other way.
            return EvaluationOutcome.Faulted(state, $"{fault.Reason}");
        }
        catch (OverflowException)
        {
            return EvaluationOutcome.Faulted(state, Overflowed(bar, "a rule"));
        }
        catch (DivideByZeroException)
        {
            // Belt and braces: the interpreter checks a divisor before dividing, so this is a
            // division this build did not know it was doing rather than one a program asked for.
            return EvaluationOutcome.Faulted(state,
                $"a rule divided by zero on the bar at {bar.OpenTime:O}");
        }
        finally
        {
            state.Charged(operations + interpreter.Operations, state.StateBytes);
        }

        if (undefined)
        {
            state.CountUndefined();
            return EvaluationOutcome.Of(EvaluationStatus.Undefined, state);
        }

        if (intent is null) return EvaluationOutcome.Of(EvaluationStatus.Idle, state);

        state.Emitted(intent);
        return EvaluationOutcome.Of(EvaluationStatus.Signalled, state, intent);
    }

    /// <summary>
    /// THE RULES, ON ONE CLOSED BAR: EVERY EXIT BEFORE EVERY ENTRY, AT MOST ONE INTENT.
    ///
    /// <para>Two separate requirements from `docs/COUNCIL.md`, and they are two separate guards here
    /// because one of them does not follow from the other. "Exit precedes entry" is the ORDER — the
    /// exit block runs first, and the parser has already refused a program that writes an exit below
    /// an entry. "No same-event reversal" is the second: once an exit has fired the position IS flat,
    /// an entry rule may well be true on the same bar, and a program that left and re-entered on one
    /// bar would have taken two positions from one observation at one price. So an exit that fires
    /// ENDS the event.</para>
    /// </summary>
    static StrategyIntent? Decide(
        EvaluationState state,
        KlineBar bar,
        AccountReading account,
        DateTime local,
        StrategyInterpreter interpreter,
        out bool undefined)
    {
        undefined = false;

        // READ THE PENDING STATE, THEN AGE IT. The other order would clear the intent emitted on the
        // previous event before it had blocked anything, which is the whole of what it is for.
        var pending = account.OrderPending || state.Pending is not null;
        state.AgePending(account);
        var position = account.Position;

        if (position == PositionSide.Long)
        {
            var exit = Exit(state, bar, account, local, interpreter, ref undefined);
            if (undefined) return null;

            if (exit is not null)
            {
                if (pending)
                {
                    // An order is already working on this position. A second exit for a position
                    // that is being closed is the same mistake as a duplicate entry.
                    state.CountSignalWhilePending();
                    return null;
                }

                return exit;
            }

            // Long, and no exit fired: there is nothing to enter. One position, no pyramiding.
            return null;
        }

        var entry = Entry(state, bar, account, local, interpreter, ref undefined, pending);
        return undefined ? null : entry;
    }

    /// <summary>
    /// The exits, in declared order, with the program's two scheduled ones first.
    ///
    /// <para>A `session_exit` and a `max_hold_bars` are statements that the position must not survive
    /// this bar, so they are answered before the rules rather than after: a rule that happened to be
    /// true as well would produce the same exit for a reason that is not why it happened.</para>
    ///
    /// <para><b>Exits are not time-filtered.</b> `weekdays` and `entry_window` gate ENTRIES. A
    /// program that could only leave a position during its entry window would be a program that
    /// cannot get out, which is not a filter, it is a trap.</para>
    /// </summary>
    static StrategyIntent? Exit(
        EvaluationState state,
        KlineBar bar,
        AccountReading account,
        DateTime local,
        StrategyInterpreter interpreter,
        ref bool undefined)
    {
        var program = state.Program;

        if (program.Time.SessionExit is { } close && MinuteOf(local) >= close.MinuteOfDay)
            return ExitIntent(state, bar, account, IntentCause.SessionExit, -1);

        if (program.MaxHoldBars is { } hold && account.BarsSinceEntry >= hold)
            return ExitIntent(state, bar, account, IntentCause.MaxHoldBars, -1);

        for (var i = 0; i < program.Rules.Count; i++)
        {
            if (program.Rules[i].Kind != RuleKind.Exit) continue;

            var value = interpreter.Rule(program.Rules[i].Condition);
            if (!value.Defined)
            {
                undefined = true;
                return null;
            }

            if (value.Boolean) return ExitIntent(state, bar, account, IntentCause.Rule, i);
        }

        return null;
    }

    /// <summary>
    /// The entries, in declared order, and AT MOST ONE.
    ///
    /// <para>The rules are evaluated before the gates so that the counters mean something: a program
    /// whose entry fired inside a week it may not trade is a different diagnosis from a program whose
    /// entry never fired, and a count of every minute outside the window would be neither.</para>
    /// </summary>
    static StrategyIntent? Entry(
        EvaluationState state,
        KlineBar bar,
        AccountReading account,
        DateTime local,
        StrategyInterpreter interpreter,
        ref bool undefined,
        bool pending)
    {
        var program = state.Program;
        var fired = -1;

        for (var i = 0; i < program.Rules.Count; i++)
        {
            if (program.Rules[i].Kind != RuleKind.Entry) continue;

            var value = interpreter.Rule(program.Rules[i].Condition);
            if (!value.Defined)
            {
                undefined = true;
                return null;
            }

            if (value.Boolean)
            {
                fired = i;
                break;      // ONE intent per event: the second true rule is the same entry.
            }
        }

        if (fired < 0) return null;

        if (pending)
        {
            state.CountSignalWhilePending();
            return null;
        }

        if (!Allowed(program.Time, local))
        {
            state.CountEntryOutsideTimeFilters();
            return null;
        }

        var reference = bar.Close;
        var stop = StopPrice(state, reference);
        var quantity = Quantity(program.Sizing, account, reference, stop);

        if (quantity <= 0m)
        {
            // An account with no capital is not a fault: it is an account that cannot act, and a run
            // that halted over it would lose the rest of the window.
            state.CountEntryWithoutSize();
            return null;
        }

        return new StrategyIntent(
            IntentKind.Enter, IntentCause.Rule, program.Instrument, quantity,
            bar.OpenTime, state.Bars - 1, state.Grid.EndOf(bar.OpenTime), reference,
            stop, TargetPrice(program.Target, reference), program.Sizing, fired)
        { Freshness = program.Freshness };
    }

    /// <summary>An exit closes exactly what the caller says is open.</summary>
    static StrategyIntent ExitIntent(
        EvaluationState state, KlineBar bar, AccountReading account, IntentCause cause, int ruleIndex)
    {
        if (account.Quantity <= 0m)
            throw new EvaluationFault(
                $"the account reads {PositionSide.Long} with a quantity of {account.Quantity}, so there " +
                $"is no position for an exit to close; the evaluator does not invent one");

        return new StrategyIntent(
            IntentKind.Exit, cause, state.Program.Instrument, account.Quantity,
            bar.OpenTime, state.Bars - 1, state.Grid.EndOf(bar.OpenTime), bar.Close,
            null, null, state.Program.Sizing, ruleIndex)
        { Freshness = state.Program.Freshness };
    }

    /// <summary>Whether the program may take an entry at this wall clock.</summary>
    static bool Allowed(TimeFilters time, DateTime local)
    {
        if (!time.Days.HasFlag(StrategyCalendar.DayOf(local))) return false;
        if (time.EntryWindows.Count == 0) return true;

        foreach (var window in time.EntryWindows)
            if (StrategyCalendar.Contains(window, local))
                return true;

        return false;
    }

    /// <summary>
    /// The stop stated at the signal bar, from its close — and for an ATR stop from the ATR measured
    /// THERE and then held, which is what `StopRule` says and what makes the distance reproducible.
    /// </summary>
    static decimal? StopPrice(EvaluationState state, decimal reference)
    {
        var stop = state.Program.Stop;

        switch (stop.Kind)
        {
            case StopKind.None:
                return null;

            case StopKind.FixedPrice:
                return stop.Value;

            case StopKind.Percent:
                return reference - reference * stop.Value / 100m;

            default:
                if (state.StopAtr?.Value is not { } atr)
                    throw new EvaluationFault(
                        $"the stop's own atr({stop.AtrPeriod}) has no value on this bar, so the stop " +
                        $"distance cannot be measured");

                return reference - stop.Value * atr;
        }
    }

    static decimal? TargetPrice(TargetRule target, decimal reference) => target.Kind switch
    {
        TargetKind.None => null,
        TargetKind.FixedPrice => target.Value,
        _ => reference + reference * target.Value / 100m
    };

    /// <summary>
    /// THE DECLARED SIZING APPLIED TO THE ACCOUNT STATE THE CALLER SUPPLIED.
    ///
    /// <para>Not rounded to an instrument increment: a dataset does not carry one, so that rounding
    /// and the gateway's own limits are applied downstream where the instrument is known
    /// (`docs/CONTRACTS.md`). A risk fraction is over the stop DISTANCE, so a stop that is not below
    /// the reference price has no distance and is a defined fault rather than a negative size.</para>
    ///
    /// <para><b>A capped risk fraction is the smaller of two</b> (<c>U-size-cap</c>): the risk size, and the
    /// declared <c>max_capital_fraction</c> of the capital at the reference price — read exactly as
    /// <c>capital_fraction</c> reads it, so the backtest (capital: the cash its books have left) and the paper
    /// runner (capital: the allocation's own ceiling) agree. A cap only ever makes a size smaller, and it
    /// applies at the signal's reference price: fees, slippage and the next open come on top of it.</para>
    /// </summary>
    static decimal Quantity(Sizing sizing, AccountReading account, decimal reference, decimal? stop)
    {
        switch (sizing.Kind)
        {
            case SizingKind.FixedQuantity:
                return sizing.Value;

            case SizingKind.CapitalFraction:
                return OfCapital(account, sizing.Value, reference);

            default:
                if (stop is not { } level)
                    throw new EvaluationFault(
                        $"risk sizing measures the quantity against the stop distance, and this program " +
                        $"has no stop on this bar");

                var distance = reference - level;
                if (distance <= 0m)
                    throw new EvaluationFault(
                        $"the stop at {level} is not below the reference price {reference}, so there is " +
                        $"no risk distance to size against");

                var risk = account.Equity * sizing.Value / distance;
                return sizing.MaxCapitalFraction is { } cap ? Math.Min(risk, OfCapital(account, cap, reference)) : risk;
        }
    }

    /// <summary>
    /// A FRACTION OF THE CALLER'S CAPITAL AT THE REFERENCE PRICE — a <c>capital_fraction</c> size, and a risk size's
    /// cap — or the defined fault a close at or below zero is: such a price divides nothing.
    /// </summary>
    static decimal OfCapital(AccountReading account, decimal fraction, decimal reference)
    {
        if (reference <= 0m)
            throw new EvaluationFault(
                $"the bar's close is {reference}, so a fraction of capital buys no quantity that " +
                $"can be divided by a price");

        return account.StrategyCapital * fraction / reference;
    }

    static int MinuteOf(DateTime local) => local.Hour * 60 + local.Minute;

    static TraceText Overflowed(KlineBar bar, string what) =>
        $"{what} overflowed the largest number this build can hold, on the bar at {bar.OpenTime:O}";

    static TraceText OverBudget(EvaluationState state, int operations) =>
        $"one closed bar cost more than the {state.Limits.OperationsPerEvent} operations a single event " +
        $"is allowed ({operations} and counting), so this program is not one the runner will evaluate " +
        $"at the rate it is asked to";

    /// <summary>
    /// A WHOLE RUN over bars in ascending order.
    ///
    /// <para><paramref name="account"/> is asked for the account state at each bar, because the
    /// evaluator never invents one: in a backtest the caller's executor answers, in paper and live the
    /// broker's reading does, and the program is the same program either way — which is what
    /// `docs/COUNCIL.md` means by one event interface in backtest, paper and live.</para>
    ///
    /// <para><paramref name="stop"/> is how a TIMEOUT becomes a defined outcome without putting a
    /// clock in here: the caller holds the deadline, and a cancelled run halts with a reason and no
    /// further intent, exactly as an interpreter fault does.</para>
    /// </summary>
    public static EvaluationRun Run(
        StrategyProgram program,
        IEnumerable<KlineBar> bars,
        Func<EvaluationState, KlineBar, AccountReading> account,
        EvaluationLimits? limits = null,
        TimeSpan? barInterval = null,
        CancellationToken stop = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(account);

        var state = EvaluationState.Start(program, limits, barInterval);
        var intents = new List<StrategyIntent>();

        foreach (var bar in bars)
        {
            if (stop.IsCancellationRequested)
            {
                state.Fault(
                    $"the run was stopped after {state.Bars} bars, before the bar at {bar.OpenTime:O}");
                break;
            }

            var outcome = Step(state, bar, account(state, bar));
            if (outcome.Intent is { } intent) intents.Add(intent);
            if (outcome.Status == EvaluationStatus.Faulted) break;
        }

        return new EvaluationRun(state, intents, state.FaultReason);
    }
}

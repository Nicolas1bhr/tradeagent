using TradeAgent.Core.Data;

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

    EvaluationState(StrategyProgram program, ZoneRules zone, TimeSpan barInterval)
    {
        Program = program;
        Zone = zone;
        BarInterval = barInterval;
        Indicators = IndicatorSet.For(program);
        History = new BarHistory();

        if (program.Stop.Kind == StopKind.EntryAtr)
            StopAtr = IndicatorSeries.For(
                new IndicatorDecl("", IndicatorKind.Atr, BarSeries.Close, program.Stop.AtrPeriod));
    }

    /// <summary>The frozen program being evaluated.</summary>
    public StrategyProgram Program { get; }

    /// <summary>The calendar rules for the program's declared zone.</summary>
    public ZoneRules Zone { get; }

    /// <summary>
    /// How long one bar is, which is what makes a gap a gap. Stated rather than assumed: a run over
    /// five-minute candles would count every minute of every bar as missing if this were hard-wired.
    /// </summary>
    public TimeSpan BarInterval { get; }

    internal IndicatorSet Indicators { get; }

    internal BarHistory History { get; }

    /// <summary>Closed bars fed so far.</summary>
    public long Bars { get; private set; }

    /// <summary>Minutes between the bars this run saw that had NO bar. Never filled, always counted.</summary>
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
    /// <paramref name="barInterval"/> defaults to one minute, which is what `docs/COUNCIL.md` says a
    /// bar is ("closed 1-minute OHLCV bars in UTC") and what every dataset this build collects holds.
    /// </summary>
    public static EvaluationState Start(StrategyProgram program, TimeSpan? barInterval = null)
    {
        var interval = barInterval ?? TimeSpan.FromMinutes(1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);

        // A program can only name a zone from the allowlist, so this cannot miss for a parsed
        // program; a state built around a zone this build has no rules for would read every wall
        // clock as UTC and honour the wrong windows, so it is refused rather than defaulted.
        if (!StrategyCalendar.TryZone(program.Time.TimeZone, out var zone))
            throw new ArgumentException(
                $"this build has no calendar for the zone '{program.Time.TimeZone}'", nameof(program));

        return new EvaluationState(program, zone, interval);
    }

    /// <summary>Halts the run with a reason. There is no route back: a faulted state stays faulted.</summary>
    internal void Fault(string reason) => FaultReason ??= reason;

    /// <summary>Records one bar's arrival, the gap in front of it, and the session it belongs to.</summary>
    internal void Advance(KlineBar bar, DateOnly session, long missingMinutes)
    {
        if (missingMinutes > 0)
        {
            MissingMinutes += missingMinutes;
            GapRunCount++;

            if (gaps.Count < KlineNormaliser.MaxGapRunsListed)
                gaps.Add(new GapRun(
                    LastBar!.Value + BarInterval,
                    bar.OpenTime - BarInterval,
                    (int)Math.Min(missingMinutes, int.MaxValue)));
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
    internal int StateBytes => (Indicators.StateSlots + History.StateSlots) * sizeof(decimal);
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
    public static EvaluationOutcome Step(EvaluationState state, KlineBar bar, AccountReading account)
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
                "evaluator in ascending order, one for each closed interval, and never twice");

        var missing = 0L;
        if (state.LastBar is { } last)
        {
            var elapsed = bar.OpenTime - last;
            var intervals = elapsed.Ticks / state.BarInterval.Ticks;

            if (elapsed.Ticks % state.BarInterval.Ticks != 0)
                return EvaluationOutcome.Faulted(state,
                    $"the bar at {bar.OpenTime:O} is {elapsed} after the one before it, which is not a " +
                    $"whole number of {state.BarInterval} bars: this run's bars are not one series");

            missing = intervals - 1;
        }

        var local = StrategyCalendar.Local(state.Zone, bar.OpenTime);
        var session = StrategyCalendar.SessionOf(local);
        var newSession = state.Session != session;
        var inOpeningRange = state.Program.Time.OpeningRange is { } range
                             && StrategyCalendar.Contains(range, local);

        var operations = state.Indicators.Push(bar, newSession, inOpeningRange);
        if (state.StopAtr is { } stopAtr) operations += stopAtr.Push(bar, newSession, inOpeningRange);
        state.History.Push(bar);
        state.Advance(bar, session, missing);
        state.Charged(operations, state.StateBytes);

        if (!state.Warm)
        {
            state.CountWarmingUp();
            return EvaluationOutcome.Of(EvaluationStatus.WarmingUp, state);
        }

        state.CountEvaluated();

        var interpreter = new StrategyInterpreter(state);
        StrategyIntent? intent;
        bool undefined;

        try
        {
            intent = Decide(state, bar, account, local, interpreter, out undefined);
        }
        catch (EvaluationFault fault)
        {
            return EvaluationOutcome.Faulted(state, fault.Reason);
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

            var value = interpreter.Eval(program.Rules[i].Condition);
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

            var value = interpreter.Eval(program.Rules[i].Condition);
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
            bar.OpenTime, state.Bars - 1, bar.OpenTime + state.BarInterval, reference,
            stop, TargetPrice(program.Target, reference), program.Sizing, fired);
    }

    /// <summary>An exit closes exactly what the caller says is open.</summary>
    static StrategyIntent ExitIntent(
        EvaluationState state, KlineBar bar, AccountReading account, IntentCause cause, int ruleIndex)
    {
        if (account.Quantity <= 0m)
            throw new EvaluationFault(
                $"the account reads {PositionSide.Long} with a quantity of {account.Quantity}, so there " +
                "is no position for an exit to close; the evaluator does not invent one");

        return new StrategyIntent(
            IntentKind.Exit, cause, state.Program.Instrument, account.Quantity,
            bar.OpenTime, state.Bars - 1, bar.OpenTime + state.BarInterval, bar.Close,
            null, null, state.Program.Sizing, ruleIndex);
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
                        "distance cannot be measured");

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
    /// </summary>
    static decimal Quantity(Sizing sizing, AccountReading account, decimal reference, decimal? stop)
    {
        switch (sizing.Kind)
        {
            case SizingKind.FixedQuantity:
                return sizing.Value;

            case SizingKind.CapitalFraction:
                if (reference <= 0m)
                    throw new EvaluationFault(
                        $"the bar's close is {reference}, so a fraction of capital buys no quantity that " +
                        "can be divided by a price");

                return account.StrategyCapital * sizing.Value / reference;

            default:
                if (stop is not { } level)
                    throw new EvaluationFault(
                        "risk sizing measures the quantity against the stop distance, and this program " +
                        "has no stop on this bar");

                var distance = reference - level;
                if (distance <= 0m)
                    throw new EvaluationFault(
                        $"the stop at {level} is not below the reference price {reference}, so there is " +
                        "no risk distance to size against");

                return account.Equity * sizing.Value / distance;
        }
    }

    static int MinuteOf(DateTime local) => local.Hour * 60 + local.Minute;

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
        TimeSpan? barInterval = null,
        CancellationToken stop = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(account);

        var state = EvaluationState.Start(program, barInterval);
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

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

    internal long EntriesWhilePending { get; private set; }

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

    /// <summary>Everything this run counted, as a snapshot.</summary>
    public EvaluationCounters Counters => new(
        Bars, MissingMinutes, GapRunCount, WarmingUpEvents, UndefinedEvents, EvaluatedEvents,
        IntentsEmitted, EntriesOutsideTimeFilters, EntriesWhilePending, EntriesWithoutSize,
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

    internal void CountWarmingUp() => WarmingUpEvents++;

    internal void CountUndefined() => UndefinedEvents++;

    internal void CountEvaluated() => EvaluatedEvents++;

    internal void CountIntent() => IntentsEmitted++;

    internal void CountEntryOutsideTimeFilters() => EntriesOutsideTimeFilters++;

    internal void CountEntryWhilePending() => EntriesWhilePending++;

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
        state.History.Push(bar);
        state.Advance(bar, session, missing);
        state.Charged(operations, state.StateBytes);

        if (!state.Warm)
        {
            state.CountWarmingUp();
            return EvaluationOutcome.Of(EvaluationStatus.WarmingUp, state);
        }

        state.CountEvaluated();
        return EvaluationOutcome.Of(EvaluationStatus.Idle, state);
    }

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

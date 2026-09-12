using TradeAgent.Core.Data;

namespace TradeAgent.Core.Strategy;

/// <summary>Long or flat. There is no third value, because the language cannot spell one.</summary>
public enum PositionSide
{
    Flat,
    Long
}

/// <summary>
/// WHAT THE CALLER KNOWS ABOUT THE ACCOUNT AT THE BAR THAT HAS JUST CLOSED — supplied to the
/// evaluator on every event, and never invented by it.
///
/// <para>`docs/COUNCIL.md` lists these as the account state the language reads: available strategy
/// capital, equity, position, average fill price, pending-order state, bars since entry. `U-runner-1`
/// recorded that none of them is in the expression grammar — a rule cannot say `equity` — so they are
/// not vocabulary, they are the evaluator's INPUT: what gates an entry, and what a size is computed
/// from. An evaluator that carried its own idea of the position would, the first time a fill was
/// partial or an order was refused, be sizing against an account that does not exist.</para>
///
/// <para><paramref name="OrderPending"/> is the caller's pending-order state and it is the
/// authority: while it is true the evaluator emits no new entry, because `docs/COUNCIL.md` forbids a
/// duplicate entry while one is pending.</para>
/// </summary>
public sealed record AccountReading(
    decimal StrategyCapital,
    decimal Equity,
    PositionSide Position,
    decimal Quantity,
    decimal AverageFillPrice,
    bool OrderPending,
    int BarsSinceEntry)
{
    /// <summary>A flat account with a stated capital, which is also its equity.</summary>
    public static AccountReading Flat(decimal capital) =>
        new(capital, capital, PositionSide.Flat, 0m, 0m, false, 0);
}

/// <summary>Enter the one position, or leave it. Long or flat: there is no other pair.</summary>
public enum IntentKind
{
    Enter,
    Exit
}

/// <summary>Which of the program's own statements produced an intent.</summary>
public enum IntentCause
{
    /// <summary>A declared `entry when` or `exit when` rule.</summary>
    Rule,

    /// <summary>The declared `session_exit` wall clock was reached while the position was open.</summary>
    SessionExit,

    /// <summary>The declared `max_hold_bars` was reached.</summary>
    MaxHoldBars
}

/// <summary>
/// ONE BOUNDED MARKET INTENT — WHAT THE EVALUATOR EMITS, AND ALL IT EMITS.
///
/// <para>`docs/COUNCIL.md`: "A signal from a completed bar executes only afterwards". That is
/// <paramref name="NotBefore"/>, and it is on the intent rather than in a comment: the signal was
/// computed from <paramref name="Bar"/>'s CLOSE, so the earliest instant it can be acted on is that
/// close, and anything that executed it at that bar's open would be reading the future.
/// <paramref name="BarOrdinal"/> is the bar's place in the run, so two intents from one run are
/// ordered even where the venue's timestamps are not.</para>
///
/// <para><b>What it is not.</b> It is not an order: it names no venue, carries no client id, and
/// nothing in this assembly sends it. <paramref name="Quantity"/> is the declared sizing applied to
/// the account state the caller supplied, and it has NOT been rounded to an instrument increment —
/// a dataset does not carry one (`DatasetStore`'s rows are counts, hashes and coverage), so rounding
/// down to the increment and the gateway's own limits are applied downstream, where the instrument
/// is known. `docs/CONTRACTS.md` says so.</para>
///
/// <para><paramref name="StopPrice"/> and <paramref name="TargetPrice"/> are stated at the signal
/// bar, from its close and — for an ATR stop — from the ATR measured there and then held. Enforcing
/// them between evaluations is the app's protection policy and `U-runner-3`'s work; an executor that
/// fills at a different price recomputes them from the fill.</para>
/// </summary>
public sealed record StrategyIntent(
    IntentKind Kind,
    IntentCause Cause,
    string Instrument,
    decimal Quantity,
    DateTimeOffset Bar,
    long BarOrdinal,
    DateTimeOffset NotBefore,
    decimal ReferencePrice,
    decimal? StopPrice,
    decimal? TargetPrice,
    Sizing Sizing,
    int RuleIndex);

/// <summary>What one event produced.</summary>
public enum EvaluationStatus
{
    /// <summary>Fewer than `WarmUpBars` closed bars have been seen. No rule was evaluated.</summary>
    WarmingUp,

    /// <summary>Warm, but a value the rules reached for is not defined on this bar. Nothing fired.</summary>
    Undefined,

    /// <summary>Evaluated, and no rule fired.</summary>
    Idle,

    /// <summary>Evaluated, and an intent was emitted.</summary>
    Signalled,

    /// <summary>A DEFINED fault. The run halts and no intent is emitted.</summary>
    Faulted
}

/// <summary>
/// EVERYTHING A RUN COUNTED, as a snapshot. Read off the state; never accumulated by a caller.
///
/// <para><paramref name="MissingMinutes"/> is the point of this record. `docs/COUNCIL.md` requires
/// missing data to be "reported and never filled", and a count that nobody carries out of the run is
/// not a report: a backtest across a dead hour looks exactly like one across a live hour, and the
/// only difference is this number.</para>
/// </summary>
public sealed record EvaluationCounters(
    long Bars,
    long MissingMinutes,
    long GapRuns,
    long WarmingUpEvents,
    long UndefinedEvents,
    long EvaluatedEvents,
    long Intents,
    long EntriesOutsideTimeFilters,
    long SignalsWhilePending,
    long EntriesWithoutSize,
    int PeakOperations,
    int PeakStateBytes);

/// <summary>
/// WHAT ONE EVENT ANSWERED: a status, the state it advanced, the intent it emitted if any, and a
/// fault reason if it faulted.
///
/// <para><b>A fault is a value.</b> `docs/COUNCIL.md`: "an interpreter fault or a timeout is a
/// defined outcome (no new exposure, the app's protection policy)". So an over-budget event, a
/// division by zero, a bar out of order and a stopped run all come back through here — no intent, the
/// run halted, the reason in words. Nothing throws out of the evaluator, because a strategy is
/// written by a cheap model and an exception it could cause would be the app's crash rather than the
/// program's fault.</para>
/// </summary>
public sealed record EvaluationOutcome(
    EvaluationStatus Status,
    EvaluationState State,
    StrategyIntent? Intent,
    string? FaultReason)
{
    public bool Ok => Status != EvaluationStatus.Faulted;

    /// <summary>The run's counters as they stand after this event.</summary>
    public EvaluationCounters Counters => State.Counters;

    /// <summary>A defined fault: the reason, no intent, and a state that will not evaluate again.</summary>
    public static EvaluationOutcome Faulted(EvaluationState state, string reason)
    {
        state.Fault(reason);
        return new EvaluationOutcome(EvaluationStatus.Faulted, state, null, reason);
    }

    internal static EvaluationOutcome Of(
        EvaluationStatus status, EvaluationState state, StrategyIntent? intent = null) =>
        new(status, state, intent, null);
}

/// <summary>
/// A WHOLE RUN: every intent it emitted, in order, and the state and counters it ended on.
///
/// <para>The intents are a LIST and not orders. Each one names the closed bar it came from and the
/// instant it may first be executed; what happens to it is `U-runner-3`'s, and nothing in this
/// assembly places anything.</para>
/// </summary>
public sealed record EvaluationRun(
    EvaluationState State,
    IReadOnlyList<StrategyIntent> Intents,
    string? FaultReason)
{
    public bool Faulted => FaultReason is not null;

    public EvaluationCounters Counters => State.Counters;

    /// <summary>The gap runs the run crossed, bounded; the COUNT in the counters is not.</summary>
    public IReadOnlyList<GapRun> GapRuns => State.GapRuns;
}

/// <summary>
/// THE TWO RESOURCE BOUNDS ONE RUN IS EVALUATED UNDER.
///
/// <para><see cref="Default"/> is <see cref="StrategyLimits.MaxOperationsPerEvent"/> and
/// <see cref="StrategyLimits.MaxStateBytes"/>, and those are CEILINGS: <see cref="Of"/> refuses a
/// budget above either. A run may be given a tighter one — a fixture run, a first pass over an
/// untrusted program, a test of the guard itself — and cannot be given a wider one, because the
/// numbers in `StrategyLimits` are what `docs/STRATEGY-LANGUAGE.md` prints for whoever writes a
/// program and a per-run override that beat them would make that document a suggestion.</para>
/// </summary>
public sealed class EvaluationLimits
{
    EvaluationLimits(int operationsPerEvent, int stateBytes)
    {
        OperationsPerEvent = operationsPerEvent;
        StateBytes = stateBytes;
    }

    /// <summary>The limits in `StrategyLimits`, which are also the ceilings.</summary>
    public static readonly EvaluationLimits Default =
        new(StrategyLimits.MaxOperationsPerEvent, StrategyLimits.MaxStateBytes);

    /// <summary>The most operations one closed bar may cost before the run faults.</summary>
    public int OperationsPerEvent { get; }

    /// <summary>The most bytes of decimals the run may hold between bars.</summary>
    public int StateBytes { get; }

    /// <summary>Tighter limits than the defaults. Wider ones are refused.</summary>
    public static EvaluationLimits Of(int operationsPerEvent, int stateBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(operationsPerEvent, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            operationsPerEvent, StrategyLimits.MaxOperationsPerEvent);
        ArgumentOutOfRangeException.ThrowIfLessThan(stateBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(stateBytes, StrategyLimits.MaxStateBytes);

        return new EvaluationLimits(operationsPerEvent, stateBytes);
    }
}

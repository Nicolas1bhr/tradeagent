namespace TradeAgent.Core;

/// <summary>
/// The only place order-state transitions are legal. Two rules matter more than the rest:
///
///  1. Nothing may re-enter DISPATCHING from UNKNOWN. That is the transition a naive retry
///     would make, and it is how you double-fill a live account.
///  2. UNKNOWN is reachable from a dispatch attempt and leaves only through RECONCILING.
/// </summary>
public static class OrderStateMachine
{
    static readonly Dictionary<ExecutionState, ExecutionState[]> Allowed = new()
    {
        [ExecutionState.CREATED]           = [ExecutionState.AWAITING_APPROVAL, ExecutionState.DISPATCHING, ExecutionState.REJECTED, ExecutionState.CANCELLED],
        [ExecutionState.AWAITING_APPROVAL] = [ExecutionState.DISPATCHING, ExecutionState.CANCELLED, ExecutionState.REJECTED],
        [ExecutionState.DISPATCHING]       = [ExecutionState.ACKNOWLEDGED, ExecutionState.WORKING, ExecutionState.PARTIALLY_FILLED, ExecutionState.FILLED, ExecutionState.REJECTED, ExecutionState.UNKNOWN],
        [ExecutionState.ACKNOWLEDGED]      = [ExecutionState.WORKING, ExecutionState.PARTIALLY_FILLED, ExecutionState.FILLED, ExecutionState.CANCEL_PENDING, ExecutionState.CANCELLED, ExecutionState.REJECTED, ExecutionState.UNKNOWN],
        [ExecutionState.WORKING]           = [ExecutionState.PARTIALLY_FILLED, ExecutionState.FILLED, ExecutionState.CANCEL_PENDING, ExecutionState.CANCELLED, ExecutionState.REJECTED, ExecutionState.UNKNOWN],
        [ExecutionState.PARTIALLY_FILLED]  = [ExecutionState.PARTIALLY_FILLED, ExecutionState.FILLED, ExecutionState.CANCEL_PENDING, ExecutionState.CANCELLED, ExecutionState.UNKNOWN],
        [ExecutionState.CANCEL_PENDING]    = [ExecutionState.CANCELLED, ExecutionState.FILLED, ExecutionState.PARTIALLY_FILLED, ExecutionState.UNKNOWN],
        // Terminal states.
        [ExecutionState.FILLED]            = [],
        [ExecutionState.CANCELLED]         = [],
        [ExecutionState.REJECTED]          = [],
        // UNKNOWN never goes straight back to the wire. It must be reconciled first.
        [ExecutionState.UNKNOWN]           = [ExecutionState.RECONCILING],
        [ExecutionState.RECONCILING]       = [ExecutionState.ACKNOWLEDGED, ExecutionState.WORKING, ExecutionState.PARTIALLY_FILLED, ExecutionState.FILLED, ExecutionState.CANCELLED, ExecutionState.REJECTED, ExecutionState.UNKNOWN],
    };

    public static bool IsTerminal(ExecutionState s) => Allowed[s].Length == 0;

    public static bool CanTransition(ExecutionState from, ExecutionState to) => Allowed[from].Contains(to);

    public static void Require(ExecutionState from, ExecutionState to)
    {
        if (!CanTransition(from, to))
            throw new TradeAgentException(ErrorCode.ILLEGAL_STATE_TRANSITION, $"{from} -> {to} is not a legal order transition");
    }

    /// <summary>States where the broker may still act on our behalf, so we cannot walk away.</summary>
    public static bool IsLive(ExecutionState s) =>
        s is ExecutionState.DISPATCHING or ExecutionState.ACKNOWLEDGED or ExecutionState.WORKING
          or ExecutionState.PARTIALLY_FILLED or ExecutionState.CANCEL_PENDING;
}

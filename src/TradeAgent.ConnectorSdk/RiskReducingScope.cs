namespace TradeAgent.ConnectorSdk;

/// <summary>
/// AN AMBIENT MARK THAT THE WORK BEING DONE RIGHT NOW REDUCES RISK — AND, SINCE ROUND 8, WHEN THE
/// WHOLE OF IT HAS TO BE OVER.
///
/// The connector classifies urgency by the bridge op it is about to send, which is right for the
/// final frame and wrong for everything that has to happen first. A cancel-all sweeps by reading the
/// working orders; cancelling one order by client id resolves it by reading the orders; closing a
/// position reads the position. Those are ordinary <c>orders</c> and <c>positions</c> RPCs, so at
/// shipped deadlines an emergency spent ten seconds on a prerequisite READ before the two-second
/// emergency frame it was hurrying to send ever got a turn (Codex F11).
///
/// Classification cannot see them because it happens BELOW the layer that decomposes the operation.
/// The intent is known at the top and needed at the bottom, and the layers in between are typed
/// interfaces this unit does not own. So it travels out of band, on the execution context, which is
/// exactly what <see cref="AsyncLocal{T}"/> is for: opened where the intent is known, read where the
/// deadline is chosen, and flowing across every await in between without a signature changing.
///
/// THE DEADLINE IS THE ROUND-8 ADDITION, and it exists because marking the intent was not enough.
/// Every RPC inside the operation was still starting its OWN two seconds, so a sweep paid the bound
/// once per leg: measured by Codex, three replies delayed 1.9 s each made an IPC <c>cancel-all</c>
/// take about 5.7 s against a promise of 2. The deadline is ABSOLUTE and set once, at the top, so
/// the orders read, each target resolution and each leg all share it — <c>deadline − now</c>, never
/// a fresh budget. What a person waiting for "stop" is waiting for is the operation, not a phase of
/// one RPC of it.
///
/// It is deliberately a WIDENING of urgency and never a narrowing: a scope can only make an ordinary
/// RPC emergency, never make a <c>cancel</c> ordinary, and a nested scope can only bring the deadline
/// FORWARD, never push it out. The worst a stray or leaked scope can do is make a read give up early
/// and be reported UNKNOWN — the same answer the operation it is nested inside would have given.
///
/// It does NOT give <c>place</c> or <c>modify</c> a fast path. Those are classified by op and are
/// never risk-reducing; an order that opens exposure has no claim on an emergency deadline whatever
/// it is nested inside, and that exclusion is asserted by a test rather than left to this comment.
/// </summary>
public static class RiskReducingScope
{
    static readonly AsyncLocal<State?> Current = new();

    sealed record State(bool Active, long? DeadlineAt);

    /// <summary>Whether the caller is inside a risk-reducing operation.</summary>
    public static bool IsActive => Current.Value?.Active == true;

    /// <summary>
    /// When the whole operation must be over, as an <see cref="Environment.TickCount64"/> stamp, or
    /// null when this scope carries no operation deadline and each RPC keeps its own bound.
    /// </summary>
    public static long? DeadlineAt => Current.Value?.DeadlineAt;

    /// <summary>
    /// Opens the scope with NO operation deadline: the intent is marked, and each RPC inside keeps
    /// its own per-call bound. For callers that want the urgency without a total.
    /// </summary>
    public static IDisposable Begin() => Open(new State(true, Current.Value?.DeadlineAt));

    /// <summary>
    /// Opens the scope and starts the operation's clock. Nesting keeps the EARLIER deadline: an
    /// inner scope may not buy the operation more time than the one it is inside.
    /// </summary>
    public static IDisposable Begin(TimeSpan budget)
    {
        var mine = Environment.TickCount64 + (long)budget.TotalMilliseconds;
        var outer = Current.Value?.DeadlineAt;
        return Open(new State(true, outer is { } o && o < mine ? o : mine));
    }

    static IDisposable Open(State state)
    {
        var previous = Current.Value;
        Current.Value = state;
        return new Handle(previous);
    }

    sealed class Handle(State? previous) : IDisposable
    {
        int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) Current.Value = previous;
        }
    }
}

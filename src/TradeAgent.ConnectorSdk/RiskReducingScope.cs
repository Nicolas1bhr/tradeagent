namespace TradeAgent.ConnectorSdk;

/// <summary>
/// AN AMBIENT MARK THAT THE WORK BEING DONE RIGHT NOW REDUCES RISK.
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
/// It is deliberately a WIDENING of urgency and never a narrowing: a scope can only make an ordinary
/// RPC emergency, never make a <c>cancel</c> ordinary. The worst a stray or leaked scope can do is
/// make a read give up in two seconds instead of ten and be reported UNKNOWN — the same answer the
/// operation it is nested inside would have given.
///
/// It does NOT give <c>place</c> or <c>modify</c> a fast path. Those are classified by op and are
/// never risk-reducing; an order that opens exposure has no claim on an emergency deadline whatever
/// it is nested inside, and that exclusion is asserted by a test rather than left to this comment.
/// </summary>
public static class RiskReducingScope
{
    static readonly AsyncLocal<bool> Active = new();

    /// <summary>Whether the caller is inside a risk-reducing operation.</summary>
    public static bool IsActive => Active.Value;

    /// <summary>Opens the scope until the returned handle is disposed. Nesting is safe.</summary>
    public static IDisposable Begin()
    {
        var previous = Active.Value;
        Active.Value = true;
        return new Handle(previous);
    }

    sealed class Handle(bool previous) : IDisposable
    {
        int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) Active.Value = previous;
        }
    }
}

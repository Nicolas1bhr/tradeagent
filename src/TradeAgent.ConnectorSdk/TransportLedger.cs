using TradeAgent.Core;

namespace TradeAgent.ConnectorSdk;

/// <summary>
/// WHERE ONE PIECE OF WORK'S MUTATION GOT TO, REPORTED BY THE CONNECTOR THAT KNOWS.
///
/// The gateway records an ambiguous connector failure as UNKNOWN — correctly, because it cannot tell
/// which failure it was. The pipe server then read the WORD for a sweep leg off that record, so a
/// leg the connector had PROVED it never sent came back <c>sent-not-confirmed</c>: the owner was sent
/// to hunt through ATAS for an order this process never wrote a byte of, and the
/// <c>needs_reconciliation</c> flag that word carries pauses all further execution — including the
/// retry the message itself advises (verifier round-9 F-1, measured through the real pipe).
///
/// The knowledge exists; it was being thrown away one layer below where it was needed. The connector
/// is the only component that can tell a refusal BEFORE the send gate from a frame that was
/// half-written, and <see cref="TransportOutcome"/> is the vocabulary `trade` has used for that
/// since round 2. So the connector writes it down here, and whoever started the piece of work reads
/// it back.
///
/// AMBIENT, for the same reason <see cref="RiskReducingScope"/> is: the fact is produced at the
/// bottom of a call stack and needed at the top, and the interfaces in between are not this unit's
/// to change. <see cref="AsyncLocal{T}"/> carries it across every await without a signature moving.
/// The value flows DOWN into the leg's execution context and the leg's connector calls mutate the
/// object the starter still holds — so a wave of concurrent legs each has its own, and none of them
/// can see another's.
///
/// ONLY MUTATIONS ARE RECORDED. A leg is a read to resolve its target and then the thing it came to
/// do; recording the read as well would report "a reply was received" for a leg whose cancel never
/// left the process.
/// </summary>
public static class TransportLedger
{
    static readonly AsyncLocal<TransportRecord?> Current = new();

    /// <summary>
    /// Attaches a record to the work about to be started on this execution context. Dispose restores
    /// whatever was there before — the STARTER's context; the work's own continuations keep the
    /// record they captured.
    /// </summary>
    public static IDisposable Attach(TransportRecord record)
    {
        var previous = Current.Value;
        Current.Value = record;
        return new Handle(previous);
    }

    /// <summary>
    /// Called by a connector at a site where it KNOWS where a mutating frame got to. Outside any
    /// attached record it does nothing, so a connector may call it unconditionally.
    /// </summary>
    public static void Record(TransportOutcome outcome) => Current.Value?.Observe(outcome);

    sealed class Handle(TransportRecord? previous) : IDisposable
    {
        int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) Current.Value = previous;
        }
    }
}

/// <summary>
/// One piece of work's transport knowledge. Null <see cref="Outcome"/> means no mutating call was
/// ever attempted, which is itself the strongest form of "nothing was sent".
/// </summary>
public sealed class TransportRecord
{
    int _outcome = -1;

    public TransportOutcome? Outcome => Volatile.Read(ref _outcome) is var v && v < 0 ? null : (TransportOutcome)v;

    /// <summary>
    /// THE MOST UNCERTAIN REPORT WINS, and that is the fail-closed direction rather than the latest
    /// one. <see cref="TransportOutcome.PossiblyWritten"/> beats everything: once one frame of this
    /// work may have reached the far end, a later call that provably sent nothing does not make the
    /// first one un-sent. <see cref="TransportOutcome.ReplyReceived"/> beats
    /// <see cref="TransportOutcome.NothingWritten"/> for the same reason in the other direction.
    /// </summary>
    internal void Observe(TransportOutcome outcome)
    {
        while (true)
        {
            var current = Volatile.Read(ref _outcome);
            var merged = current < 0 ? outcome : Merge((TransportOutcome)current, outcome);
            if (merged == (TransportOutcome)Math.Max(current, 0) && current >= 0) return;
            if (Interlocked.CompareExchange(ref _outcome, (int)merged, current) == current) return;
        }
    }

    static TransportOutcome Merge(TransportOutcome a, TransportOutcome b) =>
        a is TransportOutcome.PossiblyWritten || b is TransportOutcome.PossiblyWritten
            ? TransportOutcome.PossiblyWritten
            : a is TransportOutcome.ReplyReceived || b is TransportOutcome.ReplyReceived
                ? TransportOutcome.ReplyReceived
                : TransportOutcome.NothingWritten;
}

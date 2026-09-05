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
    /// The record attached to this execution context, or null when this work has none.
    ///
    /// READ BY THE DISPATCHER, not by a connector. `TradingGateway` needs it for the one decision
    /// that turns on the difference between an ambiguous failure and a proven one: a
    /// <see cref="ConnectorTransportException"/> reporting <see cref="TransportOutcome.NothingWritten"/>
    /// is a PROOF that nothing reached the broker, and settling that as UNKNOWN pauses all execution
    /// over an order that does not exist. The pipe server had already stopped saying it in the leg's
    /// WORD; the record it contradicted is the gateway's.
    /// </summary>
    public static TransportRecord? Attached => Current.Value;

    /// <summary>
    /// Called by a connector at a site where it KNOWS where a mutating frame got to. Outside any
    /// attached record it does nothing, so a connector may call it unconditionally.
    /// </summary>
    public static void Record(TransportOutcome outcome) => Current.Value?.Observe(outcome);

    /// <summary>
    /// Called by a connector the moment it BEGINS a mutating call, before anything can go wrong.
    ///
    /// This is what makes "nothing was recorded" mean something. Every site that KNOWS where the
    /// frame got to says so, but a call can also leave by a route nobody enumerated — a caller's own
    /// cancellation during the reply wait was one, and it left the record empty for a frame the peer
    /// had already read whole (Codex round-10 F2). An empty record means "no mutating call was ever
    /// attempted", which the mapper reads as <c>not-sent</c>: an ASSURANCE, produced by an absence
    /// of information, about an order that may be at the broker.
    ///
    /// With the attempt marked, an unreported exit is <see cref="TransportOutcome.PossiblyWritten"/>
    /// — the fail-closed answer — and it is fail-closed for exits that have not been thought of yet,
    /// which is the difference between fixing this and fixing this class.
    /// </summary>
    public static void Attempt() => Current.Value?.Attempt();

    /// <summary>
    /// THE DISPATCHER'S OWN MARK, AND IT IS WHAT MAKES `not-sent` UNFORGEABLE BY A CONNECTOR.
    ///
    /// <see cref="Attempt"/> is the CONNECTOR's obligation, stated on <c>ITradingConnector</c> — and a
    /// contract a third party can get wrong is not a guarantee. A connector written to the public
    /// interface that really cancels at the broker and never touches the ledger left the record empty,
    /// and empty is exactly what produces the one word in the set that is an ASSURANCE (verifier
    /// round-11 F-2, measured: <c>not-sent</c>, <c>attempted: 0</c>, for an order that had been
    /// cancelled). The pipe server closed that by reading its own record states instead; this closes
    /// it at the source. <c>TradingGateway</c> calls this immediately before every mutating connector
    /// call it makes, so a mutation that was DISPATCHED can never report an empty record, whatever the
    /// connector does or does not write down. Nothing else can produce that mark, because nothing else
    /// dispatches.
    ///
    /// IT ATTACHES ONE IF THERE IS NONE, and reuses the one that is there. A sweep leg already carries
    /// a record the pipe server is holding and will read back, and attaching a second would hide the
    /// connector's own reports inside it — the leg would then see nothing and call a real mutation
    /// <c>not-sent</c>, which is the defect this exists to prevent, arrived at from the other side. For
    /// a call with no leg around it — a single <c>cancel</c>, a <c>buy</c>, an operator's press — the
    /// fresh record is what lets the dispatcher read back a proven <c>NothingWritten</c> at all.
    ///
    /// Dispose restores whatever was ambient. Marking is idempotent, so a nested dispatch is harmless.
    /// </summary>
    public static IDisposable MarkDispatch()
    {
        if (Current.Value is { } existing)
        {
            existing.Attempt();
            return Unattached.Instance;
        }

        var fresh = new TransportRecord();
        var handle = Attach(fresh);
        fresh.Attempt();
        return handle;
    }

    sealed class Unattached : IDisposable
    {
        public static readonly Unattached Instance = new();
        public void Dispose() { }
    }

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
///
/// AND THAT SENTENCE IS ONLY TRUE BECAUSE OF <see cref="Attempt"/>. It used to be a claim about a
/// field nobody had written, which is a different thing: a mutating call that left by an unenumerated
/// route also wrote nothing, and the two were indistinguishable. Null is now PRODUCIBLE ONLY by a
/// piece of work that never started a mutation.
/// </summary>
public sealed class TransportRecord
{
    int _outcome = -1;
    int _attempted;

    /// <summary>
    /// Where this work's mutation got to, or null if it never started one.
    ///
    /// AN ATTEMPT WITH NO REPORT IS <see cref="TransportOutcome.PossiblyWritten"/>, which is not a
    /// guess — it is that outcome's own definition: "anything that cannot be proven to be
    /// NothingWritten lands here". Every site that can prove otherwise has already called
    /// <see cref="TransportLedger.Record"/>, and an explicit report always wins over this fallback,
    /// in BOTH directions: a proven <see cref="TransportOutcome.NothingWritten"/> stays
    /// <c>NothingWritten</c>, and a reply that was read stays <see cref="TransportOutcome.ReplyReceived"/>.
    /// </summary>
    public TransportOutcome? Outcome =>
        Volatile.Read(ref _outcome) is var v && v >= 0 ? (TransportOutcome)v
            : Volatile.Read(ref _attempted) == 1 ? TransportOutcome.PossiblyWritten
                : null;

    /// <summary>A mutating call has STARTED on this work. See <see cref="TransportLedger.Attempt"/>.</summary>
    internal void Attempt() => Volatile.Write(ref _attempted, 1);

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

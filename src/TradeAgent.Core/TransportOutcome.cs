namespace TradeAgent.Core;

/// <summary>
/// WHAT IS KNOWN ABOUT WHERE THE FRAME GOT TO. Three states, because two of them were being confused
/// and the difference between them is the difference between a retry and a second real order.
///
/// It lives in Core rather than beside either of its users because it has TWO of them and they have
/// to mean the same thing. `trade` uses it to decide whether a command that died can be re-run; the
/// gateway's pipe server uses it to decide whether a sweep leg is <c>not-sent</c> or
/// <c>sent-not-confirmed</c>. Two enums with the same three names would drift, and the drift would be
/// silent — which is the class this unit has spent four rounds closing in other places.
/// </summary>
public enum TransportOutcome
{
    /// <summary>
    /// PROVABLY nothing left this process, so there is nothing at the broker and nothing to
    /// reconcile. Only claimed when it can be shown — the pipe was never connected, or was already
    /// disconnected before the write was attempted, or the operation's deadline had already passed
    /// when the call reached the send gate — never assumed from a failure.
    /// </summary>
    NothingWritten,

    /// <summary>
    /// Some or all of the frame may have reached the far end. The order may be at the broker and
    /// only the acknowledgement lost, so this is UNKNOWN and the caller is told to re-run with the
    /// SAME id. The fail-closed direction: anything that cannot be proven to be
    /// <see cref="NothingWritten"/> lands here.
    /// </summary>
    PossiblyWritten,

    /// <summary>A reply came back and was read. Whatever it says, the round trip completed.</summary>
    ReplyReceived
}

using TradeAgent.Core;

namespace TradeAgent.TradeCli;

/// <summary>
/// WHAT IS KNOWN ABOUT WHERE THE FRAME GOT TO. Three states, because two of them were being confused
/// and the difference between them is the difference between a retry and a second real order.
/// </summary>
public enum TransportOutcome
{
    /// <summary>
    /// PROVABLY nothing left this process, so there is nothing at the broker and nothing to
    /// reconcile. Only claimed when it can be shown — the pipe was never connected, or was already
    /// disconnected before the write was attempted — never assumed from a failure.
    /// </summary>
    NothingWritten,

    /// <summary>
    /// Some or all of the frame may have reached the service. The order may be at the broker and
    /// only the acknowledgement lost, so this is UNKNOWN and the caller is told to re-run with the
    /// SAME id. The fail-closed direction: anything that cannot be proven to be
    /// <see cref="NothingWritten"/> lands here.
    /// </summary>
    PossiblyWritten,

    /// <summary>A reply came back and was read. Whatever it says, the round trip completed.</summary>
    ReplyReceived
}

/// <summary>
/// The outcome of one attempt, and it is a RESULT rather than an exception on purpose.
///
/// The CLI used to set <c>sent = true</c> on the line BEFORE the write began, so the flag that drives
/// the whole replay contract could not tell a completed frame from a zero-byte one (Codex F3). And
/// <c>PipeClient</c> let <c>IOException</c>, <c>ObjectDisposedException</c> and <c>JsonException</c>
/// escape unwrapped while <c>Program.cs</c> caught only <c>TradeAgentException</c>, so the most
/// common lost-reply failures terminated the process with no structured output and no recovery
/// guidance at all (Codex F7). Returning the state that the transport itself observed fixes both:
/// there is one value, it is produced where the knowledge is, and every exit path produces one.
/// </summary>
/// <param name="Outcome">What is known about where the frame got to.</param>
/// <param name="Reply">The reply, when and only when <see cref="TransportOutcome.ReplyReceived"/>.</param>
/// <param name="Failure">Why it ended, when it did not end with a reply.</param>
public readonly record struct TransportResult(TransportOutcome Outcome, IpcResponse? Reply, TradeAgentException? Failure)
{
    public static TransportResult Nothing(TradeAgentException why) => new(TransportOutcome.NothingWritten, null, why);
    public static TransportResult Possibly(TradeAgentException why) => new(TransportOutcome.PossiblyWritten, null, why);
    public static TransportResult Answered(IpcResponse reply) => new(TransportOutcome.ReplyReceived, reply, null);
}

using TradeAgent.Core;

namespace TradeAgent.TradeCli;

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

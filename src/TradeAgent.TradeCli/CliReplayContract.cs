using TradeAgent.Core;

namespace TradeAgent.TradeCli;

/// <summary>
/// The replay contract: how `trade` mints an order's request id, when it announces it, and what it
/// says when a call does not come back.
///
/// IT IS HERE RATHER THAN IN Program.cs BECAUSE IT HAD NO TEST. Top-level statements in an exe are
/// not reachable from a test, so the whole of this — the one thing standing between a lost reply and
/// a duplicate order — was held only by a manual run. Two mutants proved the gap: "stop printing the
/// id" and "never say reply lost" both left the suite green.
///
/// The rule these functions exist to keep: a transport failure AFTER the frame went out is not a
/// failed order. The order may already be at the broker and only the reply was lost, so the agent is
/// told the id it already used rather than being left to invent a new one — which would not be a
/// retry, it would be a second order.
/// </summary>
public static class CliReplayContract
{
    /// <summary>
    /// The id for this call: the one the caller asked for, else a fresh one for anything that moves
    /// money, else none. Read operations get no id because there is nothing to replay.
    /// </summary>
    public static string? MintRequestId(string op, string? explicitId) =>
        explicitId ?? (Ops.IsMutating(op) ? $"cli-{Guid.NewGuid():n}" : null);

    /// <summary>
    /// Announces the id BEFORE the frame goes out, on stderr.
    ///
    /// Before, not after, because the case that needs the id is the case where no reply arrives.
    /// On stderr because stdout carries the --json object an agent parses, and this must not be able
    /// to corrupt it.
    /// </summary>
    public static void AnnounceRequestId(TextWriter err, string? requestId)
    {
        if (requestId is not null) err.WriteLine($"request-id: {requestId}");
    }

    /// <summary>
    /// What to say when the call failed, or null when there is nothing to recover.
    ///
    /// <paramref name="sent"/> is the whole distinction. Nothing sent means nothing to reconcile and
    /// the agent should simply try again. Sent and unanswered means the outcome is UNKNOWN, and
    /// saying so with the id is the difference between a retry and a duplicate.
    /// </summary>
    public static string? RecoveryLine(bool sent, string? requestId) =>
        sent && requestId is not null
            ? $"reply lost — re-run with --request-id {requestId} or check `trade orders` first"
            : null;

    /// <summary>The --json object for a reply that came back, whatever it said.</summary>
    public static object AnsweredJson(string? requestId, IpcResponse reply) =>
        new { ok = reply.Ok, request_id = requestId, data = reply.Data, error = reply.Error };

    /// <summary>The --json object for a call that never came back.</summary>
    public static object UnansweredJson(string? requestId, bool sent, IpcError error) => new
    {
        ok = false,
        request_id = requestId,
        reply_lost = sent && requestId is not null,
        recovery = RecoveryLine(sent, requestId),
        error
    };
}

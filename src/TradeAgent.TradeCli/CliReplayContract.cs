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
    /// <paramref name="outcome"/> is the whole distinction, and it is now a fact the transport
    /// reported rather than a boolean the caller set before the write began (Codex F3). Provably
    /// nothing written means nothing to reconcile and the agent should simply try again. Possibly
    /// written means the outcome is UNKNOWN, and saying so with the id is the difference between a
    /// retry and a duplicate order.
    /// </summary>
    public static string? RecoveryLine(TransportOutcome outcome, string? requestId) =>
        outcome is TransportOutcome.PossiblyWritten && requestId is not null
            ? $"reply lost — re-run with --request-id {requestId} or check `trade orders` first"
            : null;

    /// <summary>
    /// What to tell the caller after a mutating command SUCCEEDED — or null when there is nothing
    /// worth saying.
    ///
    /// It used to be one sentence for every mutating op: "retrying with the same --request-id is
    /// safe; it will not place a second order." That is true of `buy` and `sell` and of nothing
    /// else today. `TradingGateway` consults the idempotency store before dispatch only on the place
    /// path; `CancelAsync` and `ModifyAsync` authorize and resolve their target BEFORE looking, and
    /// `CloseAsync` re-reads positions and places an offsetting order — so re-running one of those
    /// acts again on the book as it is then. The blanket contract is U2c-1's to implement, and until
    /// it does, the CLI must not promise it (Codex PRIOR 8, CLI half).
    ///
    /// The wording is deliberately about what to DO rather than about the internals: an agent that
    /// reads this needs to know whether re-running is a retry or a second act.
    /// </summary>
    public static string? SuccessNote(string op) => op switch
    {
        Ops.Buy or Ops.Sell =>
            "note: re-running with the same --request-id returns this same result; it will not place a second order.",
        _ when Ops.IsMutating(op) =>
            "note: keep this --request-id. Re-running it is NOT a replay for this command yet — it acts again on " +
            "the book as it is then, so check `trade orders` or `trade positions` first.",
        _ => null
    };

    /// <summary>The --json object for a reply that came back, whatever it said.</summary>
    public static object AnsweredJson(string? requestId, IpcResponse reply) =>
        new { ok = reply.Ok, request_id = requestId, data = reply.Data, error = reply.Error };

    /// <summary>The --json object for a call that never came back.</summary>
    public static object UnansweredJson(string? requestId, TransportOutcome outcome, IpcError error) => new
    {
        ok = false,
        request_id = requestId,
        reply_lost = outcome is TransportOutcome.PossiblyWritten && requestId is not null,
        // Named on the wire, because "reply_lost: false" alone does not tell an agent whether the
        // order might exist — it only says this process cannot advise a replay.
        transport = outcome.ToString(),
        recovery = RecoveryLine(outcome, requestId),
        error
    };
}

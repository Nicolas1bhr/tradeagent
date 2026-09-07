using System.Reflection;

namespace TradeAgent.Core;

/// <summary>
/// Versions we compare explicitly. An ATAS update that moves the bridge protocol must pause
/// trading and ask for a repair, not produce unpredictable execution.
/// </summary>
public static class Versions
{
    public const int ProtocolVersion = 1;      // agent <-> gateway IPC

    /// <summary>
    /// gateway &lt;-&gt; ATAS bridge.
    ///
    /// 1 -> 2: the bridge pipe authenticates. Both ends now exchange a challenge and a proof before
    /// either says hello, and a hello that arrives without one is refused rather than served. That
    /// is a change to the WIRE, not only to a policy: a bridge built before it cannot complete a
    /// connection to this build at all, so it must not be allowed to present as a bridge that
    /// merely holds the wrong secret. Bumping the number is what routes it to
    /// <c>IncompatibleBridge</c> instead — "bridge 0.0.9 speaks protocol 1, this build speaks 2 —
    /// reinstall the add-on" — which is the true diagnosis and the actual repair. Left at 1, the
    /// same bridge would have surfaced as an authentication failure and sent whoever reads it
    /// hunting a secret problem that does not exist.
    /// 2 -> 3: the write-ahead record is a precondition for placing, not a diagnostic beside it. A
    /// version-2 bridge writes the witness, ignores whether the rewrite reached the disk, and sends
    /// the order anyway; it also omits <c>witness_failure</c> from its hello, so this build cannot
    /// see that it is doing so. Both halves are wire-visible changes to what the bridge PROMISES,
    /// and the older DLL is exactly the case the number exists to catch: it presents as healthy,
    /// reports SupportsClientOrderId on a witness it may have failed to write, and nothing in the
    /// data says which. Bumping routes it to <c>IncompatibleBridge</c> — "bridge 0.1.x speaks
    /// protocol 2, this build speaks 3 — reinstall the add-on" — which is the true diagnosis and the
    /// actual repair. Left at 2, a current app would accept it and trust it.
    /// </summary>
    public const int BridgeProtocolVersion = 3;

    /// <summary>
    /// 1 -&gt; 2: the material ledger. Everything the account owner hands the agent, and everything
    /// the agent produces, is recorded with a hash and a timestamp. Purely additive — two new
    /// tables, nothing existing altered — so an older database opens and is migrated in place.
    ///
    /// 3 -&gt; 4: <c>material.version</c>, so a sighting at a tuple the ledger has already watched
    /// disappear becomes a new row instead of un-removing the old one and inheriting its hash
    /// (REVIEW 2026-09-05b finding 6). Additive too — one column with a default, and the
    /// observation index widened to carry it — and every existing row reads as version 0.
    ///
    /// 4 -&gt; 5: the <c>fill</c> table. One row per execution, keyed <c>(account_id, execution_id)</c>,
    /// written by the gateway and never updated or deleted, so the money the AI made or lost is a
    /// number somebody can read rather than a line in an activity log. Additive — one new table —
    /// and an older database gains it empty, which is the honest starting point: the ledger covers
    /// what it saw, and <c>trade pnl</c> says from when.
    ///
    /// 5 -&gt; 6: the <c>ai_attempt</c> table. One row per launch of the agent CLI, written BEFORE the
    /// process starts and completed when it ends, so a turn that was killed before its usage came
    /// back is a charge somebody can see rather than a turn that cost nothing. It replaces the
    /// <c>ai_meter_*</c> kv totals as what the daily ceiling is measured against. Additive — one new
    /// table — and an older database gains it empty, so the first day after an upgrade starts at
    /// zero rather than inheriting a total whose detail nobody kept.
    ///
    /// 6 -&gt; 7: the <c>mission_event</c> table. One row per reason the AI is allowed to be woken —
    /// the owner's message, new material, a fill, an order reaching a terminal state, the day's
    /// renewal, a delay the AI asked for, a scheduled review — each with a deterministic id, so a
    /// second raise of the same fact is a no-op and a replay of a whole day's events wakes nothing.
    /// It is what replaces the immediate re-turn: before it the loop asked for another turn the
    /// instant one ended, and the only thing that ever stopped it was the day's cost ceiling.
    /// Additive — one new table — and an older database gains it empty, which is the honest
    /// starting point: nothing that happened before the upgrade is a reason to wake now.
    /// </summary>
    public const int DatabaseSchemaVersion = 7;

    /// <summary>
    /// THE GRANT-POLICY REVISION EVERY LAUNCH RECORD CARRIES (<c>docs/COUNCIL.md</c>, round 4).
    ///
    /// 0 is the honest number for this build: there are no grants yet — no roles, no artifact
    /// revisions, no recipients — so every attempt runs under the same absent policy. The column
    /// exists now rather than later because a revision cannot be retrofitted onto attempts that ran
    /// before it: which policy was in force when a model request was made is one of the four things
    /// round 4 named as unrecoverable if it is not recorded at the time.
    /// </summary>
    public const int GrantPolicyVersion = 0;

    public static string App =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public static bool BridgeCompatible(int bridgeReported) => bridgeReported == BridgeProtocolVersion;
}

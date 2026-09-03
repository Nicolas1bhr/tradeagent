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
    /// </summary>
    public const int DatabaseSchemaVersion = 2;

    public static string App =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public static bool BridgeCompatible(int bridgeReported) => bridgeReported == BridgeProtocolVersion;
}

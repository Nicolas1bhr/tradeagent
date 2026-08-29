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
    /// </summary>
    public const int BridgeProtocolVersion = 2;

    public const int DatabaseSchemaVersion = 1;

    public static string App =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public static bool BridgeCompatible(int bridgeReported) => bridgeReported == BridgeProtocolVersion;
}

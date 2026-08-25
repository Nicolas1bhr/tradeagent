using System.Reflection;

namespace TradeAgent.Core;

/// <summary>
/// Versions we compare explicitly. An ATAS update that moves the bridge protocol must pause
/// trading and ask for a repair, not produce unpredictable execution.
/// </summary>
public static class Versions
{
    public const int ProtocolVersion = 1;      // agent <-> gateway IPC
    public const int BridgeProtocolVersion = 1; // gateway <-> ATAS bridge
    public const int DatabaseSchemaVersion = 1;

    public static string App =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public static bool BridgeCompatible(int bridgeReported) => bridgeReported == BridgeProtocolVersion;
}

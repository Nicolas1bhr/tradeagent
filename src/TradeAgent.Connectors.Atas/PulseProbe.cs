namespace TradeAgent.Connectors.Atas;

/// <summary>
/// DIAGNOSTIC ONLY — U-fix-bridge-heartbeat, branch only, removed before the proving run.
/// Records when the bridge's heartbeat loop sleeps, wakes and sends, and when the connector reads,
/// polls, records a pulse and declares a peer quiet, so a macOS CI run can print each pulse's send
/// and receipt times around a failure.
/// </summary>
public static class PulseProbe
{
    public static volatile Action<string, string>? Sink;

    public static void Note(string pipe, string what) => Sink?.Invoke(pipe, what);
}

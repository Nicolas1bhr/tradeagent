namespace TradeAgent.Core;

/// <summary>
/// Every managed directory TradeAgent owns. TRADEAGENT_HOME overrides the root, which is how
/// tests get an isolated install and how a portable build can live on a USB stick.
/// </summary>
public static class Paths
{
    public static string Home { get; } = ResolveHome();
    public static string Tools { get; } = Sub("tools");
    public static string Workspace { get; } = Sub("workspace");

    /// <summary>
    /// Where the account owner hands the agent material to work on. Deliberately *inside* the
    /// workspace: the agent is already broadly free in there, so this grants it nothing it did not
    /// already have. A drop folder outside the workspace would be a real widening of the blast
    /// radius, and the workspace boundary is the whole containment story.
    /// </summary>
    public static string Inbox { get; } = SubOf(Workspace, "inbox");
    public static string Bin { get; } = Sub("bin");
    public static string Logs { get; } = Sub("logs");
    public static string State { get; } = Sub("state");
    public static string BridgeDir { get; } = Sub("bridge");

    public static string DatabaseFile => Path.Combine(State, "tradeagent.db");
    public static string IpcTokenFile => Path.Combine(State, "ipc.token");
    public static string InstanceLockFile => Path.Combine(State, "gateway.lock");

    /// <summary>Agent-facing IPC endpoint. Overridable so parallel tests do not collide.</summary>
    public static string PipeName => Environment.GetEnvironmentVariable("TRADEAGENT_PIPE") ?? "TradeAgent.Gateway";

    /// <summary>Bridge-facing IPC endpoint (ATAS side connects to this).</summary>
    public static string BridgePipeName => Environment.GetEnvironmentVariable("TRADEAGENT_BRIDGE_PIPE") ?? "TradeAgent.Bridge";

    static string ResolveHome()
    {
        var over = Environment.GetEnvironmentVariable("TRADEAGENT_HOME");
        if (!string.IsNullOrWhiteSpace(over)) { Directory.CreateDirectory(over); return over; }
        var b = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(b))
            b = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        var home = Path.Combine(b, "TradeAgent");
        Directory.CreateDirectory(home);
        return home;
    }

    /// <summary>Touches every managed directory so a broken install fails here rather than mid-trade.</summary>
    public static void EnsureAllVerbose()
    {
        foreach (var d in new[] { Home, Tools, Workspace, Inbox, Bin, Logs, State, BridgeDir })
        {
            Directory.CreateDirectory(d);
            if (!Directory.Exists(d)) throw new TradeAgentException(ErrorCode.WORKSPACE_CORRUPT, $"cannot create {d}");
        }
    }

    static string Sub(string name) => SubOf(Home, name);

    static string SubOf(string parent, string name)
    {
        var p = Path.Combine(parent, name);
        Directory.CreateDirectory(p);
        return p;
    }
}

namespace TradeAgent.Core;

/// <summary>
/// Every managed directory TradeAgent owns. TRADEAGENT_HOME overrides the root, which is how
/// tests get an isolated install and how a portable build can live on a USB stick.
/// </summary>
public static class Paths
{
    public static string Home { get; } = ResolveHome();
    public static string Tools { get; } = Sub("tools");
    /// <summary>
    /// The recorded tree: the owner's <see cref="Inbox"/> and the agent's <see cref="AgentHome"/>
    /// side by side. This is what the material scanner walks; it is not anybody's working directory.
    /// </summary>
    public static string Workspace { get; } = Sub("workspace");

    /// <summary>
    /// Where the account owner hands the agent material to work on. Unchanged for the owner since
    /// the day this shipped, and it stays inside the workspace: the agent is already broadly free
    /// in there, so this grants it nothing it did not already have, and a drop folder somewhere
    /// else would be a real widening of the blast radius.
    ///
    /// What changed is what is NOT above it. See <see cref="AgentHome"/>.
    /// </summary>
    public static string Inbox { get; } = SubOf(Workspace, "inbox");

    /// <summary>
    /// The agent's own tree, and the working directory every runtime process is started in. A
    /// SIBLING of <see cref="Inbox"/>, not its parent.
    ///
    /// It used to be <see cref="Workspace"/> itself, which put the owner's drop folder one relative
    /// path inside the agent's working directory: <c>inbox/anything.pdf</c> was a file the agent
    /// could create, and the scanner recorded it as material the owner had handed over
    /// (REVIEW 2026-09-05b finding 5). Nothing here is a sandbox — a process that wants to write
    /// <c>../inbox</c> still can — and that is why the origin is also attested rather than inferred
    /// (<see cref="AgentPresence"/>). What this changes is that reaching the owner's folder is now
    /// a deliberate climb out of your own directory instead of the shortest path you could type.
    /// </summary>
    public static string AgentHome { get; } = SubOf(Workspace, "agent");

    /// <summary>
    /// ONE COUNCIL ROLE'S HOME, beside <see cref="Inbox"/> exactly as <see cref="AgentHome"/> is —
    /// which is what Operations' home still IS, so an install that has been running keeps its plan
    /// and its journal at the path they were already at.
    ///
    /// A method rather than a property per role because the roles are data
    /// (<see cref="CouncilRoles.All"/>) and a second property would have to be remembered by
    /// whoever adds the third role. It creates the directory, like every other member here.
    /// </summary>
    public static string RoleHome(string role) => SubOf(Workspace, CouncilRoles.HomeDir(role));

    public static string Bin { get; } = Sub("bin");
    public static string Logs { get; } = Sub("logs");
    public static string State { get; } = Sub("state");
    public static string BridgeDir { get; } = Sub("bridge");

    /// <summary>Where a downloaded TradeAgent installer waits to be run. One release per subfolder.</summary>
    public static string Updates { get; } = Sub("updates");

    /// <summary>
    /// MARKET DATA THE APP COLLECTED, AND THE APP OWNS IT.
    ///
    /// Under <see cref="State"/> and deliberately NOT under <see cref="Workspace"/>: the agent is
    /// broadly free inside its own tree, and a dataset it could rewrite is one whose provenance row
    /// describes bytes that are no longer there. There is no verb and no pipe op that writes here.
    /// The AI reads what comes out of it through <c>data-list</c> and <c>data-bars</c>.
    /// </summary>
    public static string Data { get; } = SubOf(State, "data");

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
        foreach (var d in new[] { Home, Tools, Workspace, Inbox, AgentHome, Bin, Logs, State, BridgeDir, Updates, Data }
                     .Concat(CouncilRoles.All.Select(RoleHome)))
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

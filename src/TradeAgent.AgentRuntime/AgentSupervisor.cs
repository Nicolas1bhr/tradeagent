using TradeAgent.Core;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// Owns the agent's lifecycle. One agent process at a time, deliberately: two agents sharing one
/// workspace and one trading account is a race with real money in it, and on a low-spec laptop it is
/// also simply too much load.
/// </summary>
/// <param name="selectedModel">
/// The model the owner chose on the Safety page, or null for the runtime's default. Handed to every
/// runtime this supervisor prepares, so the choice survives a restart of the agent.
/// </param>
public sealed class AgentSupervisor(HealthRegistry health, Func<string?>? selectedModel = null)
{
    readonly SemaphoreSlim _gate = new(1, 1);
    IAgentRuntime? _runtime;

    public IAgentRuntime? Current => _runtime;
    public string SessionId { get; private set; } = "";
    public bool Running { get; private set; }

    /// <summary>
    /// The CHAIR'S directory, as handed to the runtime. Empty until something is prepared.
    ///
    /// The mission loop reads <c>.tradeagent/next.json</c> from here — the AI's request to be left
    /// alone for a while — so this is the one place that answer can come from without the app
    /// guessing at a path the supervisor already knows. Every role's home is in
    /// <see cref="Workspaces"/>; this one is Operations', because it is the working directory the
    /// window's own conversation runs in and the one an install has always had.
    /// </summary>
    public string Workspace { get; private set; } = "";

    /// <summary>
    /// EVERY COUNCIL ROLE'S HOME, by role, as of the last prepare. Empty until something is prepared.
    ///
    /// Both are built on every prepare rather than lazily when a role first turns: the mission file
    /// is regenerated so it can never describe a stale world, and a role whose folder appears only
    /// once it is scheduled is a role whose first turn runs against a world nobody wrote down.
    /// </summary>
    public IReadOnlyDictionary<string, string> Workspaces { get; private set; } =
        new Dictionary<string, string>();

    public async Task<IAgentRuntime> PrepareAsync(RuntimeManifest manifest, WorkspaceContext ctx, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var runtime = new CliAgentRuntime(manifest, selectedModel);
            var detection = await runtime.DetectAsync(ct);
            health.Set(Components.AgentRuntime,
                detection.Installed ? HealthState.READY : HealthState.FAILED,
                detection.Installed ? $"{manifest.DisplayName} {detection.Version}" : $"{manifest.DisplayName} is not installed");

            SessionId = $"agent-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            var homes = WorkspaceBuilder.BuildAll(ctx);
            Workspaces = homes;
            var workspace = homes[CouncilRoles.Operations];
            Workspace = workspace;
            health.Set(Components.Workspace, HealthState.READY, workspace);

            await runtime.CreateEnvironmentAsync(workspace, WorkspaceBuilder.EnvironmentFor(SessionId, workspace), ct);
            _runtime = runtime;
            return runtime;
        }
        finally { _gate.Release(); }
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_runtime is null) throw new TradeAgentException(ErrorCode.AI_RUNTIME_NOT_FOUND, "no runtime prepared");
        await _gate.WaitAsync(ct);
        try
        {
            health.Set(Components.AgentProcess, HealthState.STARTING);
            await _runtime.StartAsync(ct);
            Running = true;
            health.Set(Components.AgentProcess, HealthState.READY);
        }
        catch (Exception ex)
        {
            Running = false;
            health.Set(Components.AgentProcess, HealthState.FAILED, ex.Message);
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_runtime is not null) await _runtime.StopAsync(ct);
            Running = false;
            health.Set(Components.AgentProcess, HealthState.PAUSED, "stopped");
        }
        finally { _gate.Release(); }
    }

    /// <summary>Refreshes the instruction file so a restarted agent never reads a stale world.</summary>
    public async Task RestartAsync(WorkspaceContext ctx, CancellationToken ct = default)
    {
        Workspaces = WorkspaceBuilder.BuildAll(ctx);
        await StopAsync(ct);
        await StartAsync(ct);
    }
}

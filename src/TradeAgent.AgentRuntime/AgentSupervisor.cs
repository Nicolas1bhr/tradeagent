using TradeAgent.Core;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// Owns the agent's lifecycle. One agent process at a time, deliberately: two agents sharing one
/// workspace and one trading account is a race with real money in it, and on a low-spec laptop it is
/// also simply too much load.
/// </summary>
public sealed class AgentSupervisor(HealthRegistry health)
{
    readonly SemaphoreSlim _gate = new(1, 1);
    IAgentRuntime? _runtime;

    public IAgentRuntime? Current => _runtime;
    public string SessionId { get; private set; } = "";
    public bool Running { get; private set; }

    public async Task<IAgentRuntime> PrepareAsync(RuntimeManifest manifest, WorkspaceContext ctx, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var runtime = new CliAgentRuntime(manifest);
            var detection = await runtime.DetectAsync(ct);
            health.Set(Components.AgentRuntime,
                detection.Installed ? HealthState.READY : HealthState.FAILED,
                detection.Installed ? $"{manifest.DisplayName} {detection.Version}" : $"{manifest.DisplayName} is not installed");

            SessionId = $"agent-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            var workspace = WorkspaceBuilder.Build(ctx);
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
        WorkspaceBuilder.Build(ctx);
        await StopAsync(ct);
        await StartAsync(ct);
    }
}

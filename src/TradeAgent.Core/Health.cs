namespace TradeAgent.Core;

public enum HealthState { UNKNOWN, STARTING, READY, DEGRADED, FAILED, PAUSED }

public sealed record ComponentHealth(string Component, HealthState State, string Detail, DateTimeOffset At)
{
    public static ComponentHealth Of(string c, HealthState s, string detail = "") => new(c, s, detail, DateTimeOffset.UtcNow);
}

public static class Components
{
    public const string App = "TradeAgent";
    public const string AgentRuntime = "Agent runtime";
    public const string AgentProcess = "Agent process";
    public const string Workspace = "Workspace";
    public const string Gateway = "Gateway";
    public const string TradeCli = "trade CLI";
    public const string AtasProcess = "ATAS process";
    public const string AtasBridge = "ATAS bridge";
    public const string TradingConnection = "Trading connection";
    public const string Account = "Account";
    public const string MarketData = "Market data";
    public const string ExecutionCapability = "Execution capability";

    public static readonly string[] All =
    [
        App, AgentRuntime, AgentProcess, Workspace, Gateway, TradeCli,
        AtasProcess, AtasBridge, TradingConnection, Account, MarketData, ExecutionCapability
    ];
}

/// <summary>
/// Thread-safe snapshot store. Components push their state; the UI and the gateway read it.
/// The gateway uses <see cref="ExecutionTrustable"/> to decide whether trading may continue at all.
/// </summary>
public sealed class HealthRegistry
{
    readonly Dictionary<string, ComponentHealth> _s = new();
    readonly Lock _gate = new();

    public event Action<ComponentHealth>? Changed;

    public void Set(string component, HealthState state, string detail = "")
    {
        ComponentHealth h;
        lock (_gate)
        {
            if (_s.TryGetValue(component, out var prev) && prev.State == state && prev.Detail == detail) return;
            h = ComponentHealth.Of(component, state, detail);
            _s[component] = h;
        }
        Changed?.Invoke(h);
    }

    public ComponentHealth Get(string component)
    {
        lock (_gate) return _s.TryGetValue(component, out var v) ? v : ComponentHealth.Of(component, HealthState.UNKNOWN);
    }

    public IReadOnlyList<ComponentHealth> Snapshot()
    {
        lock (_gate) return Components.All.Select(Get).ToList();
    }

    /// <summary>
    /// Execution is only trustable when the whole chain that carries an order is READY.
    /// Anything else and the gateway revokes trading rather than guessing.
    /// </summary>
    public bool ExecutionTrustable(out string reason)
    {
        foreach (var c in new[] { Components.Gateway, Components.TradingConnection, Components.Account, Components.ExecutionCapability })
        {
            var h = Get(c);
            if (h.State != HealthState.READY)
            {
                reason = $"{c} is {h.State}" + (string.IsNullOrEmpty(h.Detail) ? "" : $" ({h.Detail})");
                return false;
            }
        }
        reason = "";
        return true;
    }
}

using TradeAgent.Core;

namespace TradeAgent.AgentRuntime;

public enum AuthState { Unknown, NotAuthenticated, InProgress, Authenticated, Failed }

public sealed record RuntimeDetection(bool Installed, string? Path, string? Version, bool Managed);
public sealed record RuntimeCapabilities(bool CanInstallItself, bool BrowserAuth, bool CanRunHeadlessTask, bool SelfContained);

/// <summary>
/// One agent CLI, seen from TradeAgent. Runtime-specific awkwardness stays behind this interface:
/// nothing above it should ever need to know whether the agent is OpenCode or Codex.
/// </summary>
public interface IAgentRuntime
{
    string Id { get; }
    string DisplayName { get; }
    RuntimeCapabilities Capabilities { get; }

    Task<RuntimeDetection> DetectAsync(CancellationToken ct = default);
    Task<RuntimeDetection> InstallAsync(IProgress<string>? progress = null, CancellationToken ct = default);
    Task<RuntimeDetection> UpdateAsync(IProgress<string>? progress = null, CancellationToken ct = default);
    Task<string?> GetVersionAsync(CancellationToken ct = default);

    /// <summary>Starts the runtime's own sign-in flow. Usually opens a browser; never asks the user for a key.</summary>
    Task BeginAuthenticationAsync(CancellationToken ct = default);
    Task<AuthState> GetAuthenticationStateAsync(CancellationToken ct = default);

    Task CreateEnvironmentAsync(string workspace, IReadOnlyDictionary<string, string> env, CancellationToken ct = default);
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task RestartAsync(CancellationToken ct = default);

    /// <summary>Runs one task to completion and returns its output. Used for verification, not conversation.</summary>
    Task<string> ExecuteTaskAsync(string prompt, CancellationToken ct = default);

    Task<HealthState> GetHealthAsync(CancellationToken ct = default);
}

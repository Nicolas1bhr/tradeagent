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

    /// <summary>
    /// Runs the runtime's own sign-in flow with no console and no window, and returns what the user
    /// has to do next — normally a URL for TradeAgent to open in their browser. Never asks the user
    /// for a key, and never leaves them looking at a terminal.
    /// </summary>
    Task<AuthChallenge> BeginAuthenticationAsync(CancellationToken ct = default);

    /// <summary>
    /// Signs in with a key the user pasted into TradeAgent's own window, for runtimes whose own
    /// sign-in only reads a terminal. Throws when the runtime has no such path.
    /// </summary>
    Task SignInWithApiKeyAsync(string key, CancellationToken ct = default);

    Task<AuthState> GetAuthenticationStateAsync(CancellationToken ct = default);

    Task CreateEnvironmentAsync(string workspace, IReadOnlyDictionary<string, string> env, CancellationToken ct = default);
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task RestartAsync(CancellationToken ct = default);

    /// <summary>
    /// The conversation the window hosts. Returns the same object for the life of the runtime, so
    /// the chat panel and the rest of the app are looking at one history.
    /// </summary>
    IAgentConversation OpenConversation();

    /// <summary>Runs one task to completion and returns its output. Used for verification, not conversation.</summary>
    Task<string> ExecuteTaskAsync(string prompt, CancellationToken ct = default);

    Task<HealthState> GetHealthAsync(CancellationToken ct = default);
}

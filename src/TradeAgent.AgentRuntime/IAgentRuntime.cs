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

    /// <summary>
    /// THE MODEL TRADEAGENT ASKS THIS RUNTIME FOR, or null where it asks for none — either the
    /// runtime takes no model flag, or nothing has named one.
    ///
    /// It is on the interface because two things outside the runtime need it and neither may guess:
    /// the meter prices a turn by it when the stream names no model, and the screens say which model
    /// the owner is paying for.
    /// </summary>
    string? RequestedModel { get; }

    /// <summary>
    /// THE MODEL THIS RUNTIME WOULD PUT ON THE COMMAND LINE FOR ONE CHOICE, which is not the same
    /// question as <see cref="RequestedModel"/>: that one is about the choice the runtime was built
    /// with, and this one is asked per council role, whose choice is its own.
    ///
    /// It resolves the same three steps the single choice does — what was chosen, then the runtime's
    /// default, then nothing at all for a runtime that takes no model flag — so a role's reservation
    /// is priced at the model its next turn will actually run on rather than at another role's.
    /// </summary>
    string? ModelFor(string? chosen) => RequestedModel;

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

    /// <summary>
    /// A CONVERSATION OF ITS OWN FOR ONE COUNCIL ROLE — its own CLI session, its own working
    /// directory and its own model, and the same object every time it is asked for, so a role's
    /// thread is not restarted on every turn.
    ///
    /// The functions are read at the start of every turn rather than captured, for the reason
    /// <see cref="AgentSession"/> gives about the model: an owner who changes a role's model or a
    /// prepare that rebuilt the folders must be obeyed by the next turn, not by the next restart.
    ///
    /// The default is the single conversation above, which is what the chair gets: it IS the
    /// window's conversation, so the owner sees the chair's work on the Chat page exactly as they
    /// always have.
    /// </summary>
    IAgentConversation OpenConversation(string role, Func<string> workspace,
        Func<IReadOnlyDictionary<string, string>> environment, Func<string?> model) =>
        OpenConversation();

    /// <summary>Runs one task to completion and returns its output. Used for verification, not conversation.</summary>
    Task<string> ExecuteTaskAsync(string prompt, CancellationToken ct = default);

    Task<HealthState> GetHealthAsync(CancellationToken ct = default);
}

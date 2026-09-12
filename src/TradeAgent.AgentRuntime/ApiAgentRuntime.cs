using TradeAgent.Core;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// THE APP-OWNED HARNESS, AS AN <see cref="IAgentRuntime"/>: the provider's API called by TradeAgent
/// itself, with no vendor program in between.
///
/// <para><b>Why it exists at all.</b> <c>docs/COUNCIL.md</c>, "Workers run on an app-owned harness":
/// a cheap team with enforceable tools and a bounded context needs control at every
/// model-and-tool boundary, "which the codex CLI's documented contract does not provide: its sandbox
/// restricts writes, not what enters its context, so its caps are advisory". Every turn on
/// <see cref="CliAgentRuntime"/> is a process this app starts and then watches; every turn on this
/// one is a series of HTTP requests this app composes, counts and may refuse before sending.</para>
///
/// <para><b>What it is NOT.</b> It is not an installation and it is not a sign-in flow. There is no
/// executable to find, nothing to download and no browser to open: the only thing it needs that this
/// app does not already have is a key, and that key is pasted into TradeAgent's own window and kept
/// in memory (the no-terminal rule holds — see <c>HarnessKey</c>). So
/// <see cref="InstallAsync"/> and <see cref="BeginAuthenticationAsync"/> are honest refusals rather
/// than plumbing that does nothing.</para>
///
/// <para><b>It is driven by manifest data</b>, exactly as the CLI runtimes are, for the reason
/// <c>CLAUDE.md</c> gives: the base URL, the completions path, the max-output parameter's name and
/// the models all move on the vendor's schedule, and a vendor that renames
/// <c>max_tokens</c> to <c>max_completion_tokens</c> should be a one-line fix in
/// <c>runtimes.json</c> rather than a rebuild.</para>
///
/// <para><b>No real provider was called to build this.</b> Every test drives it against a loopback
/// <see cref="System.Net.HttpListener"/>. The request and response shapes are the OpenAI-compatible
/// chat-completions ones, which is what <see cref="RuntimeManifest.CompletionsPath"/> names; nothing
/// here claims to have been measured against the vendor.</para>
/// </summary>
/// <param name="apiKey">
/// The key the owner pasted, read at every turn rather than captured, so pasting one mid-session is
/// obeyed by the next turn and clearing one stops the next turn. Null answers "no key is held", and
/// then a turn on this runtime starts nothing at all.
/// </param>
/// <param name="tools">
/// The tool surface one role's turns are granted, by role. Default deny: a role this function has no
/// answer for gets <see cref="WorkerTools.None"/>, which offers nothing and refuses everything.
/// </param>
/// <param name="selectedModel">
/// The model the OWNER chose for the single (chair) conversation, or null for the manifest's default.
/// A function because it lives in the settings and changes while this runtime is alive.
/// </param>
public sealed class ApiAgentRuntime(
    RuntimeManifest manifest,
    Func<string?> apiKey,
    Func<string, IWorkerTools>? tools = null,
    Func<string?>? selectedModel = null,
    Func<string, string?>? attemptId = null,
    Func<TurnAllowance>? allowance = null,
    TimeSpan? requestTimeout = null,
    HttpMessageHandler? transport = null) : IAgentRuntime, IDisposable
{
    /// <summary>The runtime id, spelled once. It is what the Safety page and the ledger both record.</summary>
    public const string RuntimeId = "openai-api";

    /// <summary>
    /// The default per-request timeout. A never-answering endpoint has to fail in SECONDS rather than
    /// hang a turn: a conversation that never returns looks exactly like an AI that is thinking, which
    /// <see cref="AgentSession"/> already paid for once on Windows.
    /// </summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(120);

    readonly Dictionary<string, IAgentConversation> _conversations = [];
    readonly Lock _gate = new();

    public RuntimeManifest Manifest => manifest;
    public string Id => manifest.Id;
    public string DisplayName => manifest.DisplayName;

    /// <summary>Whether a key is held right now. Never the key itself, and never written down.</summary>
    public bool KeyHeld => apiKey() is { Length: > 0 };

    /// <inheritdoc />
    public string? RequestedModel
    {
        get
        {
            try { return manifest.ModelFor(selectedModel?.Invoke()); }
            catch (Exception) { return null; }
        }
    }

    /// <inheritdoc />
    public string? ModelFor(string? chosen)
    {
        try { return manifest.ModelFor(chosen); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Nothing to install, no browser sign-in, and it can run a task unattended — which is the whole
    /// of what it is for. <c>SelfContained</c> because the harness IS this app: there is no separate
    /// program whose presence anybody has to check.
    /// </summary>
    public RuntimeCapabilities Capabilities => new(
        CanInstallItself: false, BrowserAuth: false, CanRunHeadlessTask: true, SelfContained: true);

    /// <summary>
    /// PRESENT, ALWAYS, BECAUSE THERE IS NOTHING TO FIND. The runtime is code inside this build; what
    /// can be missing is the KEY, and that is <see cref="GetAuthenticationStateAsync"/>'s question,
    /// asked separately so a missing key never reads as a missing program the owner has to install.
    ///
    /// <see cref="RuntimeDetection.Path"/> carries the endpoint rather than a file, because that is
    /// the honest answer to "where does this runtime live".
    /// </summary>
    public Task<RuntimeDetection> DetectAsync(CancellationToken ct = default) =>
        Task.FromResult(new RuntimeDetection(true, manifest.Endpoint, Versions.App, Managed: true));

    public Task<string?> GetVersionAsync(CancellationToken ct = default) =>
        Task.FromResult<string?>(Versions.App);

    /// <summary>
    /// There is nothing to install, and saying so is better than a no-op that reports success. A
    /// caller that reaches this is treating the harness as a program on the machine, and the repair
    /// is to paste a key rather than to run an installer.
    /// </summary>
    public Task<RuntimeDetection> InstallAsync(IProgress<string>? progress = null, CancellationToken ct = default) =>
        DetectAsync(ct);

    public Task<RuntimeDetection> UpdateAsync(IProgress<string>? progress = null, CancellationToken ct = default) =>
        DetectAsync(ct);

    /// <summary>
    /// No headless browser sign-in exists for a bare API key, so this says what to do instead in the
    /// owner's own words. It does NOT print an instruction that means "open a terminal": the key box
    /// is on the Safety page, in TradeAgent's own window.
    /// </summary>
    public Task<AuthChallenge> BeginAuthenticationAsync(CancellationToken ct = default) =>
        Task.FromResult(new AuthChallenge(null, null, manifest.SignInDescription));

    /// <summary>
    /// THE KEY IS NOT WRITTEN ANYWHERE, so this cannot be the place it is stored. It refuses rather
    /// than silently doing nothing: the holder is in memory, it belongs to the app, and a runtime
    /// that appeared to save a key would leave the owner believing a restart keeps it.
    /// </summary>
    public Task SignInWithApiKeyAsync(string key, CancellationToken ct = default) =>
        throw new TradeAgentException(ErrorCode.AI_AUTH_FAILED,
            $"{manifest.DisplayName} holds its key in memory only, so there is nothing here to sign in to. "
            + "Paste the key on the Safety page; TradeAgent keeps it for this session and never writes it down.");

    /// <summary>A key is held, or it is not. Nothing here contacts the provider to find out.</summary>
    public Task<AuthState> GetAuthenticationStateAsync(CancellationToken ct = default) =>
        Task.FromResult(KeyHeld ? AuthState.Authenticated : AuthState.NotAuthenticated);

    string _workspace = Paths.Workspace;

    public Task CreateEnvironmentAsync(string workspace, IReadOnlyDictionary<string, string> env,
        CancellationToken ct = default)
    {
        _workspace = workspace;
        Directory.CreateDirectory(workspace);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IAgentConversation OpenConversation() =>
        OpenConversation(CouncilRoles.Operations, () => _workspace,
            () => new Dictionary<string, string>(), () => selectedModel?.Invoke());

    /// <inheritdoc />
    public IAgentConversation OpenConversation(string role, Func<string> workspace,
        Func<IReadOnlyDictionary<string, string>> environment, Func<string?> model)
    {
        lock (_gate)
        {
            if (_conversations.TryGetValue(role, out var existing)) return existing;
            var conversation = new ApiConversation(manifest, role, workspace, apiKey,
                model: () => ModelFor(model()),
                tools: () => tools?.Invoke(role) ?? WorkerTools.None,
                attempt: () => attemptId?.Invoke(role),
                allowance: allowance,
                requestTimeout: requestTimeout ?? DefaultRequestTimeout,
                transport: transport);
            _conversations[role] = conversation;
            return conversation;
        }
    }

    /// <summary>
    /// There is no process to start. It still checks the one precondition a turn has — a key — so the
    /// health row says "no key" rather than READY on a runtime that cannot run anything.
    /// </summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        if (!KeyHeld)
            throw new TradeAgentException(ErrorCode.AI_AUTH_REQUIRED, Labels.HarnessKeyNotHeld);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        List<IAgentConversation> open;
        lock (_gate) open = [.. _conversations.Values];
        foreach (var c in open) await c.StopAsync();
    }

    public async Task RestartAsync(CancellationToken ct = default)
    {
        await StopAsync(ct);
        await StartAsync(ct);
    }

    /// <summary>
    /// One turn, start to finish, with no tools offered at all — the verification path, not the
    /// conversation. A fresh conversation each time so nothing about it joins a role's history.
    /// </summary>
    public async Task<string> ExecuteTaskAsync(string prompt, CancellationToken ct = default)
    {
        var probe = new ApiConversation(manifest, CouncilRoles.Operations, () => _workspace, apiKey,
            model: () => RequestedModel, tools: () => WorkerTools.None, attempt: () => null,
            allowance: allowance, requestTimeout: requestTimeout ?? DefaultRequestTimeout,
            transport: transport);
        await probe.SendMissionAsync(prompt, ct);
        return string.Join("\n", probe.History.Where(t => t.Role is ChatRole.Ai or ChatRole.System).Select(t => t.Text));
    }

    /// <summary>
    /// READY when a key is held and FAILED when none is. There is nothing else to ask: no program to
    /// version, no process to be alive, and contacting the provider to find out would be a paid
    /// request nobody asked for.
    /// </summary>
    public Task<HealthState> GetHealthAsync(CancellationToken ct = default) =>
        Task.FromResult(KeyHeld ? HealthState.READY : HealthState.FAILED);

    /// <summary>
    /// Releases every conversation's HTTP client. A conversation holds one for the life of the role's
    /// thread — connection reuse across a turn's several requests is the point — so the owner of the
    /// runtime is what closes them.
    /// </summary>
    public void Dispose()
    {
        List<IAgentConversation> open;
        lock (_gate) { open = [.. _conversations.Values]; _conversations.Clear(); }
        foreach (var c in open) (c as IDisposable)?.Dispose();
    }
}

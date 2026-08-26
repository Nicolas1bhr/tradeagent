using TradeAgent.AgentRuntime;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Atas;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Diagnostics;
using TradeAgent.Gateway;
using TradeAgent.Provisioning;
using TradeAgent.Security;

namespace TradeAgent.App;

/// <summary>
/// Composition root. Owns every long-lived object exactly once, so the window is only a view over
/// state rather than a place where state accidentally lives.
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    SingleInstanceLock? _lock;
    Database? _db;
    GatewayPipeServer? _server;
    CancellationTokenSource? _loop;

    public Database Db => _db!;
    public TradingGateway Gateway { get; private set; } = null!;
    public HealthRegistry Health { get; } = new();
    public AgentSupervisor Agent { get; private set; } = null!;
    public OnboardingStore Onboarding { get; private set; } = null!;
    public ITradingConnector Connector { get; private set; } = null!;

    /// <summary>
    /// Everything this machine needs before the product can work, and the code that installs it.
    ///
    /// The list is the whole answer to "what do I have to do first?", and the intended answer is
    /// "nothing". Only ATAS refuses to install itself, because it is somebody else's product.
    /// </summary>
    public IReadOnlyList<IPrerequisite> Prerequisites { get; } =
    [
        new NodePrerequisite(),
        new AtasPrerequisite()
    ];

    /// <summary>
    /// The conversation with the AI, or null before one has been prepared.
    ///
    /// Cached against the runtime instance that owns it, so asking twice returns the same
    /// conversation and its history survives a refresh — but preparing a new runtime (a different AI
    /// tool, or a restart) starts a genuinely new one rather than replaying the old thread.
    /// </summary>
    public IAgentConversation? Conversation
    {
        get
        {
            var runtime = Agent?.Current;
            if (runtime is null) return null;
            if (!ReferenceEquals(runtime, _conversationOwner))
            {
                _conversation = runtime.OpenConversation();
                _conversationOwner = runtime;
            }
            return _conversation;
        }
    }

    IAgentConversation? _conversation;
    IAgentRuntime? _conversationOwner;

    public bool SingleInstance { get; private set; }
    public string? StartupProblem { get; private set; }

    public event Action? Changed;

    public async Task<bool> StartAsync()
    {
        _lock = SingleInstanceLock.TryAcquire();
        SingleInstance = _lock is not null;
        if (!SingleInstance)
        {
            StartupProblem = Errors.Get(ErrorCode.GATEWAY_ALREADY_RUNNING).UserMessage;
            return false;
        }

        try
        {
            Paths.EnsureAllVerbose();
            _db = new Database();
            Onboarding = new OnboardingStore(_db);
            Health.Set(Components.App, HealthState.READY, Versions.App);

            ToolDeployer.EnsureTradeCli();
            Health.Set(Components.TradeCli,
                File.Exists(Path.Combine(Paths.Bin, ToolDeployer.TradeCliName)) ? HealthState.READY : HealthState.FAILED);

            // Which backend to talk to is a persisted choice; the simulator is the safe default.
            var chosen = _db.GetKv("connector") ?? "fake";
            Connector = chosen == "atas" ? new AtasConnector() : new FakeConnector();

            Gateway = new TradingGateway(_db, Connector, Health);
            Gateway.StateChanged += OnGatewayStateChanged;
            Health.Changed += _ => Changed?.Invoke();

            _server = new GatewayPipeServer(Gateway, IpcToken.Ensure());
            _server.Start();
            Health.Set(Components.Gateway, HealthState.READY);

            Agent = new AgentSupervisor(Health);

            await Connector.ConnectAsync();
            await Gateway.RefreshHealthAsync();

            _loop = new CancellationTokenSource();
            _ = Task.Run(() => BackgroundAsync(_loop.Token));

            Gateway.Log.Activity("TradeAgent started");
            return true;
        }
        catch (Exception ex)
        {
            StartupProblem = ex is TradeAgentException t ? t.Info.UserMessage : ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Changes which backend the gateway executes against, immediately.
    ///
    /// This used to persist the choice and tell the user to restart, which left every later setup
    /// step — "connecting to ATAS", "finding your account", "checking live prices" — interrogating
    /// the connector that was still loaded. Choosing ATAS therefore validated the practice simulator
    /// and finished setup claiming success. A choice that is not applied is not a choice.
    /// </summary>
    public async Task SwitchConnectorAsync(string id)
    {
        if (_db is null) return;
        var current = _db.GetKv("connector") ?? "fake";
        _db.SetKv("connector", id);
        if (current == id && Gateway is not null) return;

        if (_server is not null) { await _server.DisposeAsync(); _server = null; }
        if (Gateway is not null)
        {
            Gateway.StateChanged -= OnGatewayStateChanged;
            await Gateway.DisposeAsync();
        }

        Health.Set(Components.TradingConnection, HealthState.STARTING);
        Connector = id == "atas" ? new AtasConnector() : new FakeConnector();
        Gateway = new TradingGateway(_db, Connector, Health);
        Gateway.StateChanged += OnGatewayStateChanged;

        _server = new GatewayPipeServer(Gateway, IpcToken.Ensure());
        _server.Start();
        Health.Set(Components.Gateway, HealthState.READY);

        try { await Connector.ConnectAsync(); } catch (Exception) { /* health reports it */ }
        await Gateway.RefreshHealthAsync();
        Gateway.Log.Activity($"Trading platform set to {id}");
        Changed?.Invoke();
    }

    void OnGatewayStateChanged() => Changed?.Invoke();

    public WorkspaceContext WorkspaceContext()
    {
        var available = Gateway.TryAuthorizeExecution(AgentContext.Operator, out var reason);
        return new WorkspaceContext(Connector.DisplayName, Connector.Capabilities.IsPaper,
            Gateway.Settings.SelectedAccountId, Gateway.Settings.Mode, available, reason, Gateway.Settings.Risk);
    }

    public Task<DoctorReport> RunDoctorAsync(CancellationToken ct = default) => new Doctor(Gateway).RunAsync(ct);

    /// <summary>
    /// One slow loop: refresh health, reconcile only while something is unconfirmed, rotate logs.
    /// Sized to stay invisible on a modest laptop.
    /// </summary>
    async Task BackgroundAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Gateway.RefreshHealthAsync(ct);
                if (Gateway.Requests.NeedingReconciliation().Count > 0) await Gateway.ReconcileAsync(ct);
                Gateway.Log.Rotate();
                Changed?.Invoke();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Gateway.Log.Engineering("App", "background_error", "warn", ex: ex); }

            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_loop is not null) { await _loop.CancelAsync(); _loop.Dispose(); }
        if (_server is not null) await _server.DisposeAsync();
        if (Gateway is not null) await Gateway.DisposeAsync();
        _db?.Dispose();
        _lock?.Dispose();
    }
}

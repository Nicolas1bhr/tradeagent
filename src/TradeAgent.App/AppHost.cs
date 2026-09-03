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
    readonly AtasHealthReporter _atasHealth = new();
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

    /// <summary>
    /// Whether a newer TradeAgent has been published, and the machinery to install one.
    ///
    /// It lives here, beside the gateway and the kill switch, because installing a new build of the
    /// program that holds the user's open orders is operator authority. Nothing on the agent-facing
    /// pipe can reach it: the AI cannot check, cannot download, and cannot replace its own supervisor.
    /// </summary>
    public UpdateService Updates { get; } = new(Versions.App);

    /// <summary>
    /// Whether TradeAgent asks GitHub about new versions on its own.
    ///
    /// Off means never touching the network for this; it does not mean never updating. An update is
    /// still two deliberate presses in Settings either way — the automatic half is the ASKING, never
    /// the installing.
    /// </summary>
    public bool AutoCheckForUpdates
    {
        get => (_db?.GetKv("updates.auto") ?? "1") != "0";
        set
        {
            _db?.SetKv("updates.auto", value ? "1" : "0");
            Changed?.Invoke();
        }
    }

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
                ToolDeployer.TradeCliReady(out var cliReason) ? HealthState.READY : HealthState.FAILED, cliReason);

            // Which backend to talk to is a persisted choice; the simulator is the safe default.
            var chosen = _db.GetKv("connector") ?? "fake";
            Connector = chosen == "atas" ? new AtasConnector() : new FakeConnector();

            Gateway = new TradingGateway(_db, Connector, Health);
            Gateway.StateChanged += OnGatewayStateChanged;
            Health.Changed += _ => Changed?.Invoke();
            Updates.Changed += () => Changed?.Invoke();

            // Both halves of the updater/gateway contract, in one call that a test can run: the
            // updater refuses to replace the program while an order is unconfirmed, and the gateway
            // refuses to dispatch while the program is being replaced. It is a seam rather than
            // three assignments here because this project is not built by the test suite, and a
            // guard that can only be checked by grepping for it is a guard nobody is checking.
            UpdateTradingInterlock.Attach(Gateway, Updates);

            _server = new GatewayPipeServer(Gateway, IpcToken.Ensure());
            _server.Start();
            Health.Set(Components.Gateway, HealthState.READY);

            Agent = new AgentSupervisor(Health);

            await Connector.ConnectAsync();
            await Gateway.RefreshHealthAsync();
            ReportAtasHealth();

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

        // The chosen account belongs to the platform it was chosen on. Carrying it across turns
        // every later lookup into a miss — AccountAsync asks the NEW backend for an id only the old
        // one ever had, gets null, and the Account row reports FAILED on a connection that is
        // perfectly healthy. Onboarding never hit this because it picks the platform first and the
        // account afterwards; a settings surface that can switch afterwards hits it immediately.
        if (Gateway is not null) Gateway.Update(s => s.SelectedAccountId = null);

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
        ReportAtasHealth();
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
    /// One slow loop: refresh health, reconcile only while something is unconfirmed, rotate logs,
    /// and every sixth pass walk the workspace so the material ledger stays current.
    /// Sized to stay invisible on a modest laptop.
    /// </summary>
    async Task BackgroundAsync(CancellationToken ct)
    {
        var tick = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Gateway.RefreshHealthAsync(ct);
                ReportAtasHealth();
                // The gateway's own count, not the raw flag: a record stranded in DISPATCHING is
                // unconfirmed work the moment it outlives a dispatch, and reconciling is what turns
                // it into a flagged row the rest of the screen can see.
                if (Gateway.HasUnconfirmedWork()) await Gateway.ReconcileAsync(ct);
                Gateway.Log.Rotate();

                var pass = tick++;

                // Every 30s rather than every 5s. Nothing downstream needs a file noticed within
                // five seconds, and the walk plus a bounded round of hashing is the most expensive
                // thing in this loop.
                if (pass % 6 == 0) ScanMaterials(ct);

                // Once at startup, then every six hours. This only ever lights a banner: nothing in
                // this loop downloads or installs anything, because a trading application that
                // restarts itself while the owner is looking elsewhere is not a convenience.
                if (pass % (12 * 60 * 6) == 0 && AutoCheckForUpdates) _ = Updates.CheckAsync(ct);

                Changed?.Invoke();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Gateway.Log.Engineering("App", "background_error", "warn", ex: ex); }

            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// The two ATAS rows, written on the same tick as the rest of the health picture.
    ///
    /// It runs after <see cref="TradingGateway.RefreshHealthAsync"/> and reads that pass's answer for
    /// the trading connection rather than asking the connector again: two readings of one pipe taken
    /// a moment apart is how a dashboard ends up contradicting itself in the same frame.
    /// </summary>
    void ReportAtasHealth() =>
        _atasHealth.Report(Health, Connector, Health.Get(Components.TradingConnection).State);

    /// <summary>
    /// Records what is in the workspace. Public so the inbox page can ask for a pass the moment the
    /// user drops something in, rather than making them watch a list that updates in half a minute.
    /// </summary>
    public ScanResult ScanMaterials(CancellationToken ct = default)
    {
        var result = new MaterialScanner(_db!).Scan(ct);
        if (result.Added > 0 || result.Removed > 0)
            Gateway.Log.Engineering("Materials", "scan", "info", metadataJson: Json.Write(result));
        return result;
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

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
/// What a press of <see cref="Labels.ReinstallBridge"/> has to say afterwards: whether the bridge
/// landed, and the one sentence to put on the screen either way. A reason, never a stack trace, and
/// never a folder.
/// </summary>
public sealed record BridgeReinstall(bool Ok, string Sentence);

/// <summary>
/// Composition root. Owns every long-lived object exactly once, so the window is only a view over
/// state rather than a place where state accidentally lives.
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    SingleInstanceLock? _lock;
    Database? _db;
    readonly AtasHealthReporter _atasHealth = new();
    readonly RuntimeFileHealth _runtimeFile = new();
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
                // The old conversation's turns are over; metering it further would be metering a
                // process that cannot run again.
                _metering?.Dispose();
                _conversation = runtime.OpenConversation();
                _conversationOwner = runtime;
                _metering = Meter?.Attach(_conversation);

                // WHAT THE OWNER TYPES WHILE THE AI IS WORKING GOES TO THE TABLE, not to a list in
                // memory. Attached here rather than at construction because this is the one place a
                // conversation is made, and a second one made later must be wired too — the same
                // reason the meter is attached on this line.
                if (_conversation is AgentSession session) session.RecordTyped = RecordOwnerMessage;
            }
            return _conversation;
        }
    }

    IAgentConversation? _conversation;
    IAgentRuntime? _conversationOwner;
    IDisposable? _metering;

    /// <summary>
    /// THE BILL. One line per turn in the app's own state directory and today's totals in the
    /// database, so what the AI costs is a measurement rather than an impression.
    ///
    /// Attached to the conversation in the property above, which means EVERY turn is metered — the
    /// owner's typed questions as well as the mission's — because every one of them is a run of the
    /// CLI and every run of the CLI is charged to somebody.
    /// </summary>
    public TurnMeter? Meter { get; private set; }

    /// <summary>What the AI has cost today, for the card, the Safety page and the agent's status.</summary>
    public AiSpendToday SpendToday => Meter?.Today ?? AiSpendToday.NotMetered;

    /// <summary>
    /// WHY THE AI IS ALLOWED TO TAKE A TURN. Written by this process and by nothing else — there is
    /// no verb and no pipe op that reaches it, which is the rule <c>material</c> and
    /// <c>ai_attempt</c> already keep and the reason an agent cannot buy itself work.
    /// </summary>
    public MissionEventStore? Wakes { get; private set; }

    /// <summary>
    /// Writes one reason to wake and, when the row is new, ends whatever sleep the loop is in. A
    /// repeat writes nothing and wakes nobody, which is what makes the fill ledger's two sources and
    /// every reconnect free.
    ///
    /// It never throws: every caller is a code path — a fill arriving, a scan finishing, the owner
    /// pressing a button — that must carry on whether or not a queue row could be written.
    /// </summary>
    /// <summary>
    /// RECORDS WHAT THE OWNER TYPED WHILE THE AI WAS WORKING, and says whether the row went in.
    ///
    /// The answer is what <see cref="AgentSession.Queue"/> uses to decide who holds the message: a
    /// true here means the table has it and the in-memory list must not, so the next turn cannot be
    /// handed the same question twice. A false is honest — no queue, or a row that would not
    /// write — and the words stay in memory exactly as they used to.
    ///
    /// The text is the payload and nothing else is: this is the owner's own words, and they are
    /// carried into the next turn's Situation, where they keep their first place.
    /// </summary>
    public bool RecordOwnerMessage(string text)
    {
        if (Wakes is null) return false;
        try
        {
            if (!Wakes.RecordOwnerMessage(text, DateTimeOffset.UtcNow)) return false;
            Mission?.Wake();
            return true;
        }
        catch (Exception ex)
        {
            try { Gateway.Log.Engineering("Mission", "owner_message_not_recorded", "warn", ex: ex); }
            catch (Exception) { /* the log is the same database that just refused the row */ }
            return false;
        }
    }

    public void RaiseWake(string id, string kind, string? payload = null)
    {
        try
        {
            if (Wakes?.Raise(id, kind, DateTimeOffset.UtcNow, payload) == true) Mission?.Wake();
        }
        catch (Exception ex)
        {
            try { Gateway.Log.Engineering("Mission", "wake_not_raised", "warn", ex: ex); }
            catch (Exception) { /* the log is the same database that just refused the row */ }
        }
    }

    /// <summary>
    /// THE SHIPPED RATE THE SAFETY PAGE OFFERS AS ITS DEFAULT: the dearest model in the running
    /// runtime's catalogue, which is exactly what an unidentified turn is being charged at. Null
    /// where this build ships no list price for that runtime, and then the page has no default to
    /// show and says so.
    ///
    /// The prepared agent's id first and the owner's chosen runtime after, because before an agent
    /// is prepared the choice on the Settings page is the honest answer to "what is this priced as".
    /// </summary>
    public ModelPrice? ShippedRate =>
        CostCatalog.Highest(PricedRuntimeId(Agent?.Current?.Id, Gateway.Settings.SelectedRuntimeId));

    /// <summary>
    /// WHICH RUNTIME'S CATALOGUE PRICES THIS INSTALLATION — the one question the Safety page and the
    /// AI card must never answer differently, which is why it is asked in one place.
    ///
    /// The prepared agent's id first, the owner's chosen runtime after. Before the first start there
    /// IS no prepared agent, and reading only that says "nobody can price this" about an installation
    /// whose runtime was chosen during setup and is priced on the Safety page one click away. On a
    /// screen that meant the card announcing the daily cap "cannot stop it" while the page beside it
    /// showed the rate it would be stopped by (the run of 2026-09-07).
    ///
    /// Overcharging is not the risk here: the fallback picks a catalogue, and the price taken from it
    /// is the DEAREST entry, labelled an estimate wherever it is shown.
    /// </summary>
    public static string? PricedRuntimeId(string? preparedAgentId, string? chosenRuntimeId) =>
        preparedAgentId is { Length: > 0 } ? preparedAgentId : chosenRuntimeId;

    /// <summary>The same question, asked of this host. One place, so the screens cannot disagree.</summary>
    string? PricingRuntime => PricedRuntimeId(Agent?.Current?.Id, Gateway.Settings.SelectedRuntimeId);

    /// <summary>Every model this build ships a price for on the runtime in force. The Safety page's row.</summary>
    public IReadOnlyList<ModelPrice> ModelChoices => CostCatalog.Choices(PricingRuntime);

    /// <summary>
    /// THE MODEL TRADEAGENT WILL ASK FOR on the next turn, or null where it asks for none. The
    /// prepared runtime answers first, and the manifest for the owner's chosen runtime answers
    /// before one is prepared — the same fallback and for the same reason as the rate above, so the
    /// Safety page shows the model the next turn will actually run on rather than nothing at all.
    /// </summary>
    public string? RequestedModel =>
        Agent?.Current?.RequestedModel
        ?? (PricingRuntime is { Length: > 0 } id
            ? RuntimeCatalog.Find(id)?.ModelFor(Gateway.Settings.SelectedModelId)
            : null);

    /// <summary>
    /// THE LOOP THAT KEEPS THE AI WORKING. Composed here, beside the gateway, because that is where
    /// the facts a turn is handed already live — and deliberately NOT anywhere the agent can reach:
    /// there is no pipe op and no `trade` verb that starts it, pauses it, or changes what it is told.
    ///
    /// It holds no authority. Turns keep running while <see cref="TradingGateway.StopAiTrading"/> is
    /// down, because the kill switch takes away permission to TRADE and there is a great deal of work
    /// — research, backtesting, writing strategies, the journal — that wants doing exactly then.
    /// </summary>
    public MissionLoop Mission { get; private set; } = null!;

    /// <summary>
    /// The moment the last material pass began, as this process saw it, in UTC ticks — 0 for "no
    /// pass yet". Read before the walk rather than after, so it is never later than the scanner's
    /// own idea of the pass: erring early costs one spare pass and erring late costs an attestation.
    ///
    /// A long through <see cref="Interlocked"/> rather than a <c>DateTimeOffset?</c>, because two
    /// threads write it — the background loop every thirty seconds and the mission loop after every
    /// turn — while the mission loop reads it, and a sixteen-byte struct is not read or written
    /// atomically anywhere. A torn read landing in the future would answer "nothing new in the drop
    /// folder" when there is, skip the yield, and cost the very attestation this field exists for.
    /// </summary>
    long _lastScanAtTicks;

    DateTimeOffset? LastScanAt =>
        Interlocked.Read(ref _lastScanAtTicks) is var t and not 0
            ? new DateTimeOffset(t, TimeSpan.Zero)
            : null;

    /// <summary>When the last mission turn was composed, so the next one can say what is new since.</summary>
    DateTimeOffset _lastSituationAt = DateTimeOffset.UtcNow;

    /// <summary>
    /// Whether a newer TradeAgent has been published, and the machinery to install one.
    ///
    /// It lives here, beside the gateway and the kill switch, because installing a new build of the
    /// program that holds the user's open orders is operator authority. Nothing on the agent-facing
    /// pipe can reach it: the AI cannot check, cannot download, and cannot replace its own supervisor.
    /// </summary>
    public UpdateService Updates { get; } = new(Versions.App);

    /// <summary>
    /// The market-data collector. IN-PROCESS ONLY, like every other control on this object: it is
    /// reachable from the app's own window and from nothing on the agent-facing pipe. The AI reads
    /// what it produced through <c>data-list</c> and <c>data-bars</c> and has no way to ask for a
    /// collection, a rebuild or a deletion.
    /// </summary>
    public BinanceDataService MarketData => _marketData
        ?? throw new InvalidOperationException("the market data service is not available before startup");

    BinanceDataService? _marketData;

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
            _marketData = new BinanceDataService(_db);
            Gateway.StateChanged += OnGatewayStateChanged;
            Health.Changed += _ => Changed?.Invoke();
            Updates.Changed += () => Changed?.Invoke();

            // Where "this was installed without a checksum, because <reason>" goes. The provisioning
            // layer sits below the database on purpose — it has to run during setup, before there is
            // one — so it holds a sink rather than a store, and this is the one place that fills it
            // in. Reading the property each time means a connector switch, which replaces Gateway,
            // does not leave the line going to a log nobody reads; wired HERE, above the connect,
            // because onboarding is where the unchecked install happens and a backend that will not
            // connect must not be what decides whether the owner is told about it.
            Downloader.RecordDecision = text => Gateway.Log.Activity(text, "warn");

            // Both halves of the updater/gateway contract, in one call that a test can run: the
            // updater refuses to replace the program while an order is unconfirmed, and the gateway
            // refuses to dispatch while the program is being replaced. It is a seam rather than
            // three assignments here because this project is not built by the test suite, and a
            // guard that can only be checked by grepping for it is a guard nobody is checking.
            //
            // It is handed the PROPERTY, not this gateway: SwitchConnectorAsync below replaces
            // Gateway, and an interlock bound to the instance goes on answering for a gateway nobody
            // trades through. There is deliberately no second Attach call down there to forget.
            UpdateTradingInterlock.Attach(() => Gateway, Updates);

            _server = new GatewayPipeServer(Gateway, IpcToken.Ensure());
            _server.Start();
            Health.Set(Components.Gateway, HealthState.READY);

            // THE MODEL IS READ THROUGH A FUNCTION, not captured: the owner changes it on the Safety
            // page while the agent is running, and the next turn is the one that has to obey.
            Agent = new AgentSupervisor(Health, () => Gateway.Settings.SelectedModelId);
            Meter = new TurnMeter(_db,
                cap: () => Gateway.Settings.AiDailyCostCap,
                session: () => (Conversation as AgentSession)?.ThreadId,
                runtimeId: () => PricedRuntimeId(Agent.Current?.Id, Gateway.Settings.SelectedRuntimeId),
                owner: () => OwnerPrice.From(Gateway.Settings),
                model: () => RequestedModel,
                allowance: () => TurnAllowance.From(
                    Gateway.Settings.AiTurnAllowanceInputTokens, Gateway.Settings.AiTurnAllowanceOutputTokens));
            Meter.Changed += () => Changed?.Invoke();

            Wakes = new MissionEventStore(_db);

            Mission = new MissionLoop(new MissionHost(this),
                new MissionOptions
                {
                    TurnsPerSession = Math.Max(1, Gateway.Settings.MissionTurnsPerSession),
                    ReviewEvery = TimeSpan.FromMinutes(Math.Max(0, Gateway.Settings.MissionReviewMinutes))
                });
            Mission.Changed += () => Changed?.Invoke();

            // THE TWO FACTS THE GATEWAY OWNS AND NOBODY ELSE CAN SEE ARRIVE. A fill and an order
            // reaching a final state are the events the AI most needs to be woken for, and both are
            // known first inside the gateway's own event handling. The sink is a delegate rather
            // than a reference to this host, so nothing reachable from it can change a mode, lift
            // the kill switch or approve anything.
            Gateway.RaiseMissionWake = RaiseWake;
            ReportAiToTheGateway();

            await Connector.ConnectAsync();
            await Gateway.RefreshHealthAsync();
            ReportAtasHealth();

            _loop = new CancellationTokenSource();
            _ = Task.Run(() => BackgroundAsync(_loop.Token));

            Gateway.Log.Activity("TradeAgent started");
            ResumeMissionIfItWasWorking();
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
        // A new gateway is a new object and the sink is on the object, exactly like the hook below.
        // Forgetting this line is how a fill on the new platform stops waking the AI.
        Gateway.RaiseMissionWake = RaiseWake;

        _server = new GatewayPipeServer(Gateway, IpcToken.Ensure());
        _server.Start();
        Health.Set(Components.Gateway, HealthState.READY);
        // A new gateway is a new object, and the hook is on the object. Forgetting this line is how
        // `trade status` starts reporting an AI that is stopped and free while it is working.
        ReportAiToTheGateway();

        try { await Connector.ConnectAsync(); } catch (Exception) { /* health reports it */ }
        await Gateway.RefreshHealthAsync();
        ReportAtasHealth();
        Gateway.Log.Activity($"Trading platform set to {id}");
        Changed?.Invoke();
    }

    /// <summary>
    /// PUTS THE AI'S OWN THREE FACTS ON THE STATUS THE AGENT READS, and nothing else.
    ///
    /// The gateway holds a delegate rather than a reference to the loop, so what it can learn is
    /// these three values and what it can DO about them is nothing: there is no verb, no pipe op and
    /// no path from this hook back to Start, Pause or the cap. The agent reading its own bill is the
    /// point — its mission is to cover what it costs — and it must not become the agent editing it.
    ///
    /// The cost is null unless this installation can actually price a turn. Reporting zero would tell
    /// an AI whose mission is to pay for itself that it was free.
    /// </summary>
    void ReportAiToTheGateway() =>
        Gateway.Ai = () =>
        {
            var spend = SpendToday;
            return new AiActivity(
                Mission is null ? AiActivity.None.State : Mission.Status.State.ToString().ToLowerInvariant(),
                spend.Turns,
                spend.CanPrice ? spend.Spent : null)
            {
                // Only ever beside a figure. A label with no number to qualify would tell the agent
                // its unmeasurable cost was an estimate, which is a claim about a figure that is not
                // on the wire at all.
                CostEstimated = spend.CanPrice ? spend.Estimated : null,
                Model = spend.Model
            };
        };

    void OnGatewayStateChanged() => Changed?.Invoke();

    public WorkspaceContext WorkspaceContext()
    {
        var available = Gateway.TryAuthorizeExecution(AgentContext.Operator, out var reason);
        return new WorkspaceContext(Connector.DisplayName, Connector.Capabilities.IsPaper,
            Gateway.Settings.SelectedAccountId, Gateway.Settings.Mode, available, reason, Gateway.Settings.Risk,
            ConnectorIsBuiltInSimulator: Connector.Id == FakeConnector.ConnectorId);
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
    void ReportAtasHealth()
    {
        _atasHealth.Report(Health, Connector, Health.Get(Components.TradingConnection).State);
        // The other vendor-command file, on the same tick and for the same reason. It is here rather
        // than beside the agent's own rows because the agent's rows are written when an agent is
        // PREPARED, and an owner whose runtimes.json cannot be read never gets that far.
        _runtimeFile.Report(Health);
    }

    /// <summary>
    /// Whether the bridge row is one a reinstall would repair — refused, or not there at all. The
    /// Checks page puts its button on the screen for exactly these.
    /// </summary>
    public bool BridgeRepairOffered => _atasHealth.RepairOffered;

    /// <summary>
    /// Puts this build's bridge into ATAS again, and makes the row say so on the next breath.
    ///
    /// The same call the setup wizard makes, from a screen the owner can still reach afterwards.
    /// Until this existed, the wizard was the only caller and the wizard is only entered while setup
    /// is unfinished — so a protocol bump, which refuses every bridge deployed before it, left the
    /// owner reading "reinstall it" with nothing anywhere to press.
    ///
    /// The copy is filesystem work and runs off the UI thread; the row is re-derived immediately
    /// afterwards rather than at the cache's leisure, because a repair that worked must not read as
    /// one that did not. Nothing here restarts ATAS, and nothing here says anything the owner cannot
    /// act on inside this window.
    /// </summary>
    public async Task<BridgeReinstall> ReinstallBridgeAsync()
    {
        try
        {
            await Task.Run(() => AtasInstallation.InstallBridge(Path.Combine(AppContext.BaseDirectory, "bridge")));
            _atasHealth.Forget();
            ReportAtasHealth();
            Gateway.Log.Activity("The ATAS bridge was reinstalled");
            Changed?.Invoke();
            return new BridgeReinstall(true,
                "The bridge is in place. Open ATAS, open a chart, and start the TradeAgent Bridge strategy on it. " +
                "TradeAgent notices by itself — you do not have to tell it.");
        }
        catch (Exception ex)
        {
            // UNKNOWN_ERROR, not ATAS_BRIDGE_MISSING: an unexpected failure of the copy is not
            // evidence that the bridge is absent, and telling the owner it is would send them round
            // this same button again for a reason that is not the one they have.
            var info = ex is TradeAgentException t ? t.Info : Errors.Get(ErrorCode.UNKNOWN_ERROR, ex.Message);
            Gateway.Log.Engineering("Atas", "bridge_reinstall_failed", "warn", ex: ex);
            return new BridgeReinstall(false, $"{info.UserMessage} {info.Repair}".Trim());
        }
    }

    /// <summary>
    /// Records what is in the workspace. Public so the inbox page can ask for a pass the moment the
    /// user drops something in, rather than making them watch a list that updates in half a minute.
    /// </summary>
    public ScanResult ScanMaterials(CancellationToken ct = default)
    {
        var at = DateTimeOffset.UtcNow;
        Interlocked.Exchange(ref _lastScanAtTicks, at.UtcTicks);
        var result = new MaterialScanner(_db!).Scan(ct);
        if (result.Added > 0 || result.Removed > 0)
            Gateway.Log.Engineering("Materials", "scan", "info", metadataJson: Json.Write(result));

        // ADDED, NOT CHANGED. A pass that only hashed files it had already recorded, or watched one
        // go, has told the AI nothing it did not know — and this is a paid turn. The id is the
        // instant the pass began, so the same pass reported twice is one reason to wake.
        if (result.Added > 0)
            RaiseWake(MissionEventIds.Inbox(at), MissionEventKind.Inbox,
                Json.Write(new { added = result.Added, seen = result.Seen }));

        return result;
    }

    // ---- the mission ---------------------------------------------------------------------------

    /// <summary>
    /// LETS THE AI WORK ON ITS OWN, and remembers that the owner said so. Two presses on the card;
    /// this is what the second one reaches.
    /// </summary>
    public void LetTheAiWorkOnItsOwn()
    {
        Gateway.Update(s => s.AiWorksOnItsOwn = true);
        Gateway.Log.Activity("The AI was set to work on its own");
        Mission.Start();

        // THE PRESS IS ITSELF A REASON TO LOOK. Without this the owner presses the button and
        // nothing happens until the next scheduled review — half an hour of a card reading
        // "waiting", which reads exactly like a button that did not work. Deliberately raised HERE
        // and not in MissionLoop.Start: a restart that resumes a mission the owner had already
        // started is not a new instruction, and must launch nothing on its own.
        RaiseWake(MissionEventIds.Review(DateTimeOffset.Now), MissionEventKind.Review,
            Json.Write(new { because = "the owner set the AI to work on its own" }));
        Changed?.Invoke();
    }

    /// <summary>
    /// Stops the loop and remembers that too, so a restart does not quietly put it back to work. One
    /// press: this only ever takes work away.
    /// </summary>
    public async Task PauseTheAiAsync()
    {
        Gateway.Update(s => s.AiWorksOnItsOwn = false);
        await Mission.PauseAsync();
        Gateway.Log.Activity("The AI was paused");
        Changed?.Invoke();
    }

    /// <summary>What a restart does about an AI that was working when the app last closed.</summary>
    internal enum MissionOnStart
    {
        /// <summary>It was paused, and paused is what survives a restart.</summary>
        StayPaused,

        /// <summary>It was working, and the resume setting says to put it back to work.</summary>
        Resume,

        /// <summary>It was working, resuming is switched off, and the record is corrected to match.</summary>
        ForgetItWasWorking
    }

    /// <summary>
    /// WORKING RESUMES ON START; PAUSED SURVIVES ONE.
    ///
    /// The owner's choice is the persisted flag and the resume setting only decides whether a restart
    /// acts on it — so when resuming is off, the flag is written BACK to false rather than left true
    /// over a loop that is not running. A card reading "working" beside a loop that is not is a worse
    /// failure than losing the preference, because every other number on that card would then be
    /// describing a turn that is never going to happen.
    ///
    /// Separated from the doing so the decision can be driven without a database, a pipe server and a
    /// broker connection.
    /// </summary>
    internal static MissionOnStart DecideOnStart(TradeAgentSettings s) =>
        !s.AiWorksOnItsOwn ? MissionOnStart.StayPaused :
        s.ResumeAiOnStart ? MissionOnStart.Resume :
        MissionOnStart.ForgetItWasWorking;

    void ResumeMissionIfItWasWorking()
    {
        switch (DecideOnStart(Gateway.Settings))
        {
            case MissionOnStart.Resume: Mission.Start(); break;
            case MissionOnStart.ForgetItWasWorking: Gateway.Update(s => s.AiWorksOnItsOwn = false); break;
        }
    }

    /// <summary>
    /// What one mission turn is told, and the two questions the loop asks about the drop folder.
    ///
    /// It is a nested type rather than <see cref="AppHost"/> implementing the interface itself
    /// because the loop must not be handed the composition root: everything it can reach is on these
    /// five members, and none of them can change a mode, lift the kill switch or approve an order.
    /// </summary>
    sealed class MissionHost(AppHost host) : IMissionHost
    {
        public IAgentConversation? Conversation => host.Conversation;

        public string AgentHome => host.Agent.Workspace is { Length: > 0 } w ? w : Paths.AgentHome;

        public bool InboxChangedSinceLastPass => MissionInbox.ChangedSince(Paths.Workspace, host.LastScanAt);

        public AiSpendToday Spend => host.SpendToday;

        /// <summary>The persisted reasons to wake. Read by the loop; written by the app only.</summary>
        public MissionEventStore? Events => host.Wakes;

        /// <summary>
        /// The launch record and its reservation, written before the CLI starts, together with the
        /// wakes this turn is answering. Nothing else on this interface writes to the database, and
        /// this one cannot change a mode, lift the kill switch or approve anything — it commits
        /// money the AI is about to spend on itself.
        /// </summary>
        public string? BeginTurn(string prompt, IReadOnlyList<string> wakes) =>
            host.Meter?.Begin(prompt, wakes);

        /// <summary>
        /// The one activity line the owner gets when the AI stops for the day, in their words and
        /// naming both numbers. It is written where they already look for what the software did,
        /// rather than only on a card they may not be in front of.
        /// </summary>
        public void SpendCapReached(AiSpendToday spend)
        {
            var money = MissionSituation.Money(spend.Spent, spend.Currency);
            var cap = MissionSituation.Money(spend.Cap, spend.Currency);
            var reservation = MissionSituation.Money(spend.NextTurnReservation, spend.Currency);
            host.Gateway.Log.Activity(spend.CapCannotFundATurn
                    // Midnight does not repair this one, so the line must not promise that it will.
                    ? $"One AI turn can cost up to {reservation}, which is more than the whole {cap} daily limit, so "
                      + "the AI cannot start a turn at all. Raise the limit on the Safety page, or choose a cheaper "
                      + "model there."
                    : $"The AI has spent {money} today, and the next turn would take it past its {cap} daily limit. "
                      + $"It stops taking new turns until {spend.ResumesAt:HH:mm}. Raise the limit on the Safety "
                      + "page to let it carry on.",
                "warn");
            host.Changed?.Invoke();
        }

        public Task ScanAsync(CancellationToken ct)
        {
            try { host.ScanMaterials(ct); }
            catch (OperationCanceledException) { throw; }
            // A scan that threw must not stop the mission. It is a record-keeping pass, and the
            // engineering log already has the exception from the background loop that also runs it.
            catch (Exception ex) { host.Gateway.Log.Engineering("Materials", "mission_scan_failed", "warn", ex: ex); }
            return Task.CompletedTask;
        }

        public async Task<MissionSituation> SituationAsync(CancellationToken ct)
        {
            var status = await host.Gateway.StatusAsync(ct);
            var since = host._lastSituationAt;
            host._lastSituationAt = DateTimeOffset.UtcNow;

            // Positions come from the broker and the broker can be down. A turn told "positions: none"
            // because a call failed would be a turn reasoning about an account it cannot see, so the
            // failure is said in the words the AI reads rather than rendered as an empty list.
            IReadOnlyList<string> positions;
            IReadOnlyList<ConnectorSdk.PositionInfo>? held = null;
            try
            {
                held = await host.Gateway.PositionsAsync(ct);
                positions = held.Select(p => $"{p.Symbol} {p.Quantity:+#;-#;0} at {p.AveragePrice}").ToArray();
            }
            catch (Exception ex) { positions = [$"could not be read — {ex.Message}"]; }

            // The day's loss is worked out from the positions ALREADY read above rather than from a
            // second round trip: a turn that asked the platform the same question twice would pay a
            // whole connector deadline for an answer nobody would prefer. A read that failed passes
            // null, and the reading comes back saying so — which is what the AI needs to be told,
            // because its next order is going to be refused for exactly that reason.
            var loss = await host.Gateway.LossTodayAsync(held, ct);

            return new MissionSituation
            {
                LocalTime = DateTimeOffset.Now,
                Mode = status.Mode.ToString(),
                ExecutionAvailable = status.ExecutionAvailable,
                ExecutionBlockedReason = status.ExecutionBlockedReason,
                Account = status.AccountId,
                Positions = positions,
                OpenOrders = status.OpenRequests,
                UnconfirmedRequests = status.UnreconciledRequests,
                NewMaterial = NewInbox(since),
                Guidance = host.Gateway.Settings.Guidance,
                Spend = host.SpendToday,
                Loss = loss,
                Data = MissionSituation.DataLine(NewestDataset())
            };
        }

        /// <summary>
        /// The newest dataset this installation holds, verified as it is read, or null when there
        /// is none. A failure to read the ledger reads as "no dataset yet" rather than as an
        /// exception out of the middle of building a turn's message.
        /// </summary>
        DatasetRecord? NewestDataset()
        {
            try
            {
                var newest = host.Gateway.Datasets.All().FirstOrDefault();
                return newest is null ? null : host.Gateway.Datasets.Checked(newest);
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// What has turned up in the owner's folder since the last turn — BOTH words for it, because
        /// the AI is being told a file exists, not being told who put it there. The distinction the
        /// ledger keeps is the owner's to read on the Inbox page.
        /// </summary>
        IReadOnlyList<string> NewInbox(DateTimeOffset since)
        {
            try
            {
                var store = new MaterialStore(host._db!);
                return store.Present(MaterialOrigin.Inbox).Concat(store.Present(MaterialOrigin.InboxUnattested))
                    .Where(m => m.FirstSeenAt >= since)
                    .OrderBy(m => m.FirstSeenAt)
                    .Select(m => m.Name)
                    .Take(50)
                    .ToArray();
            }
            catch (Exception) { return []; }
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

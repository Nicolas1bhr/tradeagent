using TradeAgent.AgentRuntime;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Atas;
using TradeAgent.Connectors.Fake;
using TradeAgent.Connectors.Paper;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Core.Decisions;
using TradeAgent.Diagnostics;
using TradeAgent.Gateway;
using TradeAgent.Platforms;
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

    /// <summary>The background loop's task, so the quit can let the pass in flight finish (see <see cref="DisposeAsync"/>).</summary>
    Task? _background;

    /// <summary>
    /// The register the AI's processes report to. The process-wide one, always, in the product; only
    /// <see cref="Composed"/> — a test's host — is handed another.
    /// </summary>
    AgentPresence _presence = AgentPresence.Shared;

    /// <summary>
    /// The reads every material pass makes of the disk. The machine's own, always, in the product; only
    /// <see cref="Composed"/> — a test's host — is handed one that refuses a folder.
    /// </summary>
    WorkspaceReads _reads = WorkspaceReads.Disk;

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
                // The CHAIR's, named: the Chat page talks to the Operations Director, so the
                // owner's own turns are reserved and charged against that role's share.
                _metering = Meter?.Attach(_conversation, CouncilRoles.Operations);

                // WHAT THE OWNER TYPES WHILE THE AI IS WORKING GOES TO THE TABLE, not to a list in
                // memory. Attached here rather than at construction because this is the one place a
                // conversation is made, and a second one made later must be wired too — the same
                // reason the meter is attached on this line.
                if (_conversation is IAdmittedConversation admitted) admitted.RecordTyped = RecordOwnerMessage;
            }
            return _conversation;
        }
    }

    IAgentConversation? _conversation;
    IAgentRuntime? _conversationOwner;
    IDisposable? _metering;

    /// <summary>
    /// THE PROVIDER KEY FOR THE APP-OWNED HARNESS, held in memory for this session and written nowhere.
    ///
    /// The process-wide holder, for the reason <see cref="AgentGrants.Shared"/> is one: the Safety page
    /// takes the paste and the harness sends the request, and a key set on one instance and read from
    /// another is a worker that will not start. <see cref="DisposeAsync"/> clears it.
    /// </summary>
    public HarnessKey HarnessKey { get; } = Security.HarnessKey.Shared;

    /// <summary>
    /// THE DECISION MODELS' OWN KEY (<c>U-decision-port</c>) — a SECOND holder, never <see cref="HarnessKey"/>'s: the
    /// worker's key goes to the worker's provider and this one to the one decision model the owner pasted it for, each
    /// released only for its own origin. In memory only, like the worker's, and cleared in <see cref="DisposeAsync"/>.
    /// Nothing pastes into it yet: the owner's Perception card is <c>U-decision-card</c>'s.
    /// </summary>
    public HarnessKey PerceptionKey { get; } = new();

    /// <summary>
    /// THE OWNER'S DAILY AI CAP, AS ONE DELEGATE — the meter admits turns and the decision port admits calls against
    /// exactly this, so the two can never be two ceilings. Read through <see cref="Gateway"/> at every call, because a
    /// connector switch replaces the gateway and the owner changes the cap while the AI works.
    /// </summary>
    decimal AiCap() => Gateway.Settings.AiDailyCostCap;

    /// <summary>The decision models built so far, one per instrument, disposed with the app.</summary>
    readonly Dictionary<string, TypeSafeWire> _decisionModels = new(StringComparer.Ordinal);

    /// <summary>
    /// THE DECISION MODEL FOR ONE INSTRUMENT, or null where it may not be called: an id this build does not ship, or an
    /// instrument <c>decision-models.json</c> stopped. Built over the app's own ledger and its one tape (a call is refused
    /// while none is open), <see cref="PerceptionKey"/>, the cap delegate the meter reads and the owner's perception
    /// budget; nothing reaches it from the agent-facing pipe — the gateway's assembly does not even reference the one this
    /// lives in.
    /// </summary>
    public IDecisionModel? DecisionModel(string instrumentId)
    {
        if (_db is null || DecisionInstruments.Find(instrumentId) is not { } instrument) return null;
        lock (_decisionModels)
        {
            if (_decisionModels.TryGetValue(instrumentId, out var held) && held.Instrument == instrument) return held;
            held?.Dispose();
            return _decisionModels[instrumentId] = new TypeSafeWire(instrument, PerceptionKey, _db, () => _tape, AiCap,
                () => Gateway.Settings.PerceptionDailyBudget);
        }
    }

    /// <summary>
    /// THE APP-OWNED HARNESS, made once and kept — see <see cref="ApiAgentRuntime"/>.
    ///
    /// <para>It is NOT under <see cref="AgentSupervisor"/>, and that is deliberate rather than
    /// incidental: the supervisor's whole job is the lifecycle of an agent PROCESS — detect it, install
    /// it, start it, hold it in a job object, stop it — and the harness has no process. Putting it
    /// there would mean a Detect that cannot fail, an Install that does nothing and a Stop with nothing
    /// to kill, three lies in the one class whose answers the health rows are read from.</para>
    ///
    /// <para>Built lazily because it depends on the catalogue, which an owner can make unreadable: a
    /// harness that could not be constructed must not stop the app starting, and the role that wanted
    /// it falls back to the CLI with the runtime health row saying why.</para>
    /// </summary>
    public ApiAgentRuntime? Harness
    {
        get
        {
            if (_harness is not null) return _harness;
            try
            {
                var manifest = RuntimeCatalog.Find(ApiAgentRuntime.RuntimeId);
                if (manifest is null) return null;
                return _harness = new ApiAgentRuntime(manifest,
                    // ASKED AT EVERY TURN, never captured: pasting a key mid-session is obeyed by the
                    // next turn and clearing one stops the next turn. The holder itself, not a reader
                    // of it: a turn asks for the key FOR the origin its requests go to, and nothing
                    // else here can ask for it at all (U-key-host-pin).
                    key: HarnessKey,
                    tools: ToolsFor,
                    selectedModel: () => Gateway.Settings.SelectedModelId,
                    attemptId: role => Meter?.OpenAttemptIdFor(role),
                    allowance: () => TurnAllowance.From(
                        Gateway.Settings.AiTurnAllowanceInputTokens,
                        Gateway.Settings.AiTurnAllowanceOutputTokens));
            }
            catch (Exception ex)
            {
                try { Gateway.Log.Engineering("Harness", "not_built", "warn", ex: ex); }
                catch (Exception) { /* the log is the database, which may be the thing that failed */ }
                return null;
            }
        }
    }

    ApiAgentRuntime? _harness;

    /// <summary>
    /// THE TOOLS ONE ROLE'S HARNESS TURNS ARE GRANTED. Composed here, beside the gateway, because this
    /// is where the things a tool reaches already live — and deliberately NOT reachable from the agent:
    /// there is no tool that hands out another tool, and the surface a role gets is decided by this
    /// method and by nothing the model can say.
    ///
    /// The gateway is a FUNCTION because <see cref="SwitchConnectorAsync"/> replaces it; a worker
    /// holding the old one would be asking a gateway nobody trades through.
    /// </summary>
    IWorkerTools ToolsFor(string role) =>
        new GrantedWorkerTools(role,
            home: () => HomeFor(role),
            attempt: () => Meter?.OpenAttemptIdFor(role),
            gateway: () => _server,
            ledger: _db is null ? null : new ToolCallStore(_db),
            session: () => Agent?.SessionId ?? "");

    /// <summary>
    /// WHICH RUNTIME ONE ROLE RUNS ON: the owner's choice for that role, then the app's default.
    ///
    /// <para><b>Research defaults to the harness once a key is held.</b> That is the doctrine's own
    /// order — "workers run on an app-owned harness; seniors may keep the vendor CLI" — and the default
    /// is conditional because a harness with no key starts nothing at all: defaulting to it
    /// unconditionally would silence the Research Director on a machine that has never been given a
    /// key, which is worse than running it on the CLI.</para>
    ///
    /// <para><b>The chair stays on the vendor CLI in this slice</b>, whatever is chosen for it. Its
    /// conversation IS the Chat page's — one object the window draws and the owner types into — and the
    /// chair on the harness is its own unit (<c>docs/briefs/U-api-worker.md</c>, "Not this unit"). So
    /// this method answers the CLI for Operations rather than letting a setting produce a chair whose
    /// turns the Chat page cannot see.</para>
    /// </summary>
    public string? RuntimeForRole(string role) =>
        RuntimeForRole(role, Gateway.Settings.RuntimeForRole(role), Gateway.Settings.SelectedRuntimeId,
            HarnessKey.Held);

    /// <inheritdoc cref="RuntimeForRole(string)"/>
    /// <remarks>
    /// Static and handed its three inputs, for the reason <see cref="PricedRuntimeId"/> is: this is a
    /// RULE rather than a lookup — which role, whose choice, and whether a key is held — and a rule that
    /// can only be exercised by starting the whole app is a rule nobody is checking.
    /// </remarks>
    public static string? RuntimeForRole(string role, string? chosenForRole, string? appWide, bool keyHeld) =>
        !ApiAgentRuntime.Serves(role)
            ? appWide
            : chosenForRole
              ?? (role == CouncilRoles.Research && keyHeld ? ApiAgentRuntime.RuntimeId : appWide);

    /// <summary>
    /// THE RUNTIME OBJECT one role's next turn runs on, or null before anything is prepared. The one
    /// place the choice above becomes an object, so <see cref="ConversationFor"/> and the reservation's
    /// pricing cannot disagree about which runtime a role is on.
    /// </summary>
    IAgentRuntime? RuntimeObjectFor(string role) =>
        RuntimeCatalog.IsHarness(RuntimeForRole(role)) && ApiAgentRuntime.Serves(role)
            ? Harness
            : Agent?.Current;

    /// <summary>
    /// ONE COUNCIL ROLE'S CONVERSATION: its own session, in its own folder, on its own model.
    ///
    /// The chair's IS the window's conversation above, so the owner sees the Operations Director's
    /// work on the Chat page exactly as they always have, and what they type reaches the role that
    /// holds their agenda. Every other role gets a session of its own.
    ///
    /// Cached against the runtime that owns it, for the same reason the single conversation is: a
    /// different AI tool, or a restart, must start a genuinely new thread rather than replay the old
    /// one. It is metered on this line and nowhere else — every run of the CLI is charged to
    /// somebody, and a role whose turns were not attached would spend the owner's day invisibly.
    ///
    /// <b>One open attempt PER ROLE, because two roles' turns may overlap.</b>
    /// <see cref="TurnMeter"/> holds an open attempt, a staged close and a reservation for each role
    /// separately, and <see cref="MissionLoop"/> holds a turn lease per role: a second turn for a
    /// role that is already turning is refused, and the other role is free to work meanwhile. What
    /// is NOT concurrent is the money path — only the Operations Director places orders, and the
    /// gateway's dispatch gate is still a mutex.
    /// </summary>
    public IAgentConversation? ConversationFor(string role)
    {
        if (role == CouncilRoles.Operations) return Conversation;

        // THE ROLE'S OWN RUNTIME, WHICH MAY NOT BE THE APP'S. The cache below is keyed on the runtime
        // object as well as the role, so an owner who moves a role between the CLI and the harness gets
        // a genuinely new thread on the new one rather than a stale conversation that answers for a
        // runtime it is no longer on.
        var runtime = RuntimeObjectFor(role);
        if (runtime is null) return null;

        if (_roleConversations.TryGetValue(role, out var held) && ReferenceEquals(held.Owner, runtime))
            return held.Conversation;

        held.Metering?.Dispose();
        // FUNCTIONS, NOT VALUES. The folder is rebuilt on every prepare and the model is changed on
        // the Safety page while the agent is running; the next turn is the one that has to obey.
        var conversation = runtime.OpenConversation(role,
            workspace: () => HomeFor(role),
            environment: () => WorkspaceBuilder.EnvironmentFor(Agent?.SessionId ?? "", HomeFor(role)),
            model: () => Gateway.Settings.ModelForRole(role));
        _roleConversations[role] = (runtime, conversation, Meter?.Attach(conversation, role));
        return conversation;
    }

    readonly Dictionary<string, (IAgentRuntime Owner, IAgentConversation Conversation, IDisposable? Metering)>
        _roleConversations = [];

    /// <summary>
    /// ONE ROLE'S OWN DIRECTORY, as the last prepare built it, and the managed path for that role
    /// where nothing has been prepared yet. Each role reads and writes only here — its
    /// <c>.tradeagent/next.json</c>, its <c>in/</c> and its <c>out/</c> — because one folder shared
    /// between them would let whichever ran last decide when the other works.
    /// </summary>
    public string HomeFor(string role) =>
        Agent?.Workspaces.TryGetValue(role, out var w) == true && w.Length > 0 ? w : Paths.RoleHome(role);

    /// <summary>
    /// THE MODEL THE NEXT TURN OF ONE ROLE WILL RUN ON. The same fallback as
    /// <see cref="RequestedModel"/> — the prepared runtime first, the chosen runtime's manifest
    /// before one is prepared — over that role's own choice rather than the app-wide one, so the
    /// reservation a role's turn commits is priced at the rate that role is actually charged.
    /// </summary>
    public string? RequestedModelFor(string role)
    {
        var chosen = Gateway.Settings.ModelForRole(role);
        // THE ROLE'S OWN RUNTIME RESOLVES IT. Asking the app's runtime for a role that is on the
        // harness answers with the CLI's rules — which is how a reservation gets priced at a model the
        // turn will not run on, in whichever direction the two happen to differ.
        return RuntimeObjectFor(role)?.ModelFor(chosen)
               ?? (RuntimeForRole(role) is { Length: > 0 } id ? RuntimeCatalog.Find(id)?.ModelFor(chosen) : null);
    }

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
    /// THE CONSEQUENTIAL BOUNDARIES (<c>docs/COUNCIL.md</c>:59-65). Opened by the app when something
    /// consequential happens, disposed by the app when the deadline passes, and reachable from the loop
    /// only through <c>IMissionHost.Boundaries</c> — there is no pipe op and no <c>trade</c> verb that
    /// opens, assesses, challenges or disposes one, which is what keeps the whole protocol the app's.
    /// </summary>
    public CouncilBoundaries? Boundaries { get; private set; }

    /// <summary>
    /// THE RELAY BETWEEN THE TWO ROLES. Publishes what a role left in its <c>out/</c>, commits the
    /// delivery and the recipient's one task in a single transaction, and copies the text into the
    /// recipient's <c>in/</c>. Written by this process and by nothing else.
    /// </summary>
    public CouncilRelay Relay { get; private set; } = null!;

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

    /// <summary>
    /// Raises one wake for the role named — every wake names whose work it is (<c>U-reconcile-wakes</c>).
    /// The gateway's fills and orders arrive here with their role already decided, and a deployment's
    /// never arrive at all.
    /// </summary>
    public void RaiseWake(string id, string kind, string? payload, string role)
    {
        try
        {
            if (Wakes?.Raise(id, kind, DateTimeOffset.UtcNow, payload, role) == true) Mission?.Wake();
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
    /// pass yet". The mission loop's yield reads only whether it is 0: a process that has run no pass
    /// takes one before its first turn. Its value is compared with nothing — whether the drop folder
    /// holds something new is asked of the ledger (<see cref="MissionInbox"/>), because the files'
    /// own times come from the filesystem's clock, not this one, and a file stamped a tick before
    /// this instant had been skipped by the yield and recorded behind the next turn, unattested.
    ///
    /// A long through <see cref="Interlocked"/> rather than a <c>DateTimeOffset?</c>, because two
    /// threads write it — the background loop every thirty seconds and the mission loop after every
    /// turn — while the mission loop reads it, and a sixteen-byte struct is not read or written
    /// atomically anywhere.
    /// </summary>
    long _lastScanAtTicks;

    /// <summary>
    /// The background loop's last scheduled pass was refused under the loop's exclusion and is owed.
    /// Read and written only by <see cref="BackgroundAsync"/>, on its one thread.
    /// </summary>
    bool _scanOwed;

    DateTimeOffset? LastScanAt =>
        Interlocked.Read(ref _lastScanAtTicks) is var t and not 0
            ? new DateTimeOffset(t, TimeSpan.Zero)
            : null;

    /// <summary>When the last mission turn was composed, so the next one can say what is new since.</summary>
    /// <summary>
    /// THE CHAIR'S BOOK AS THE LAST SITUATION READ IT — one line per open position — or null where none has been read
    /// or the last read failed. What <see cref="RoleObjectives"/> is handed, so a role's look is decided without a
    /// broker call (<c>U-reconcile-wakes</c>).
    /// </summary>
    string[]? _lastBook;

    /// <summary>When each role's Situation last listed the owner's new material — see <see cref="RoleInboxMarks"/>.</summary>
    readonly RoleInboxMarks _inboxSeen = new(DateTimeOffset.UtcNow);

    /// <summary>
    /// Whether a newer TradeAgent has been published, and the machinery to install one.
    ///
    /// It lives here, beside the gateway and the kill switch, because installing a new build of the
    /// program that holds the user's open orders is operator authority. Nothing on the agent-facing
    /// pipe can reach it: the AI cannot check, cannot download, and cannot replace its own supervisor.
    ///
    /// Off Windows its versions arrive by deploy, on the owner's word (<c>U-linux-host</c>): the one file a release
    /// offers is the Windows installer, so there it asks nothing and offers nothing.
    /// </summary>
    public UpdateService Updates { get; } = new(Versions.App, byDeploy: !OperatingSystem.IsWindows());

    /// <summary>
    /// The market-data collector. IN-PROCESS ONLY, like every other control on this object: it is
    /// reachable from the app's own window and from nothing on the agent-facing pipe. The AI reads
    /// what it produced through <c>data-list</c> and <c>data-bars</c> and has no way to ask for a
    /// collection, a rebuild or a deletion.
    /// </summary>
    public MarketDataService MarketData => _marketData
        ?? throw new InvalidOperationException("the market data service is not available before startup");

    MarketDataService? _marketData;

    /// <summary>
    /// THE FORWARD BAR COLLECTOR — the one thing in this product that watches an advancing market.
    ///
    /// <para>Started with the app and stopped with it, and INDEPENDENT OF THE MISSION LOOP: a paused
    /// AI, an exhausted spending ceiling and a kill switch all leave it running, because what it
    /// collects is evidence and evidence is not a paid turn. The reverse matters more — it takes no
    /// turn, spends nothing and places nothing.</para>
    ///
    /// <para>In-process only, like everything else on this object. There is no verb and no pipe op
    /// that starts it, stops it or points it somewhere else; the owner's toggle on the Settings page
    /// is the only control.</para>
    /// </summary>
    public ForwardBarCollector? Forward { get; private set; }

    /// <summary>
    /// THE TAPE'S COLLECTOR — the market's context recorded as it arrives, into its own file
    /// (<c>U-tape-store</c>).
    ///
    /// <para>Started with the app and stopped with it, independent of the mission loop, for the reasons
    /// <see cref="Forward"/> is: what it records is evidence, not a paid turn, and it takes no turn,
    /// spends nothing, holds no key and places nothing. In-process only: no verb and no pipe op starts
    /// it, stops it, points it elsewhere or writes the tape; the owner's "Record market context" toggle on
    /// the Settings page is the only control. Null when the tape could not be opened — the activity log
    /// says why — and the rest of the app runs without it.</para>
    /// </summary>
    public TapeCollector? Tape { get; private set; }

    /// <summary>The tape the collector writes. Its own file, its own connection; closed after the collector stops.</summary>
    TapeStore? _tape;

    /// <summary>
    /// THE SAME TAPE, AS THE GATEWAY READS IT (<c>U-tape-read</c>): read-only connections of its own, so nothing the
    /// gateway reaches can write it. Null when the tape could not be opened.
    /// </summary>
    TapeReader? _tapeReader;

    /// <summary>
    /// PUTS THE TAPE'S READER ON THE GATEWAY — and again on every gateway a connector switch builds, because the
    /// property is on the object, exactly like <see cref="ReportAiToTheGateway"/>. Forgetting this line is how
    /// <c>trade data tape</c> starts answering that no tape is open while the collectors are recording.
    /// </summary>
    void ReportTapeToTheGateway() => Gateway.Tape = _tapeReader;

    /// <summary>
    /// GDELT'S RECORDER — GDELT's news items about crypto, into the same tape on its own task (<c>U-tape-archive</c>).
    /// Started with the tape, on its own switch: the owner's "Record GDELT news" on the Settings page is its only control,
    /// in-process — no verb and no pipe op starts it, stops it, points it elsewhere or writes the tape. Null when the tape
    /// could not be opened.
    /// </summary>
    public GdeltRecorder? Gdelt { get; private set; }

    /// <summary>
    /// THE APP'S INSTRUMENT CHECK (<c>U-venue-verify</c>): the configured pair, read against Binance spot's
    /// own published definition from the built-in origin. In-process only, like the collectors: no verb and
    /// no pipe op starts a check or writes its row. Null when it could not be built — the activity log says
    /// why — and the rest of the app runs without it.
    /// </summary>
    InstrumentVerifier? _verifier;

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
            var chosen = _db.GetKv("connector") ?? Platforms.Connectors.Simulator;
            Connector = Platforms.Connectors.Create(chosen, PaperChoice());

            Gateway = new TradingGateway(_db, Connector, Health);
            // THE COLLECTOR RAISES THE `data` WAKE ITSELF and this is what pokes the loop, the way
            // `ScanMaterials` does for new material: the ROW is the fact and is written in the
            // collector's own transaction, and this only stops a sleeping loop from waiting for its
            // next scheduled look to find out.
            _marketData = new MarketDataService(_db, nudge: () => Mission?.Wake());

            // THE FORWARD COLLECTOR, STARTED WITH THE APP. Both functions are read at every look
            // rather than captured: the owner changes the pair and flips the toggle on the Settings
            // page while the app is running, and the next look is the one that has to obey.
            //
            // A catalogue with no forward row — an owner's sources.json that removed it — is a
            // startup fact and not a crash: the collector is simply absent, `data-list` says the
            // series is not being collected, and the app comes up.
            try
            {
                Forward = new ForwardBarCollector(_db,
                    () => Gateway.Settings.MarketDataPair,
                    () => Gateway.Settings.CollectLiveBars);

                // AND THE PAPER CONNECTOR HEARS EVERY MINUTE THE COLLECTOR STORES. Through the one
                // delegate rather than a direct subscription: a connector switch replaces what is
                // listening and the collector goes on running through it. The collector already
                // catches and records a subscriber that throws.
                Forward.BarClosed += (symbol, open) => _forwardAnnounce?.Invoke(symbol, open);
                Forward.Start();
            }
            catch (Exception ex)
            {
                Gateway.Log.Activity(
                    "TradeAgent is not collecting live bars: " + ex.Message.ReplaceLineEndings(" "), "warn");
            }

            // THE TAPE, STARTED WITH THE APP, the same way and for the same reasons. A tape written by a
            // newer TradeAgent is refused untouched and said in the activity log (`TapeStore.TryOpen`);
            // an unreadable tape-sources.json, or a row in it that is refused, is said there too while
            // the built-in rows go on. None of it is a reason for the app not to come up.
            try
            {
                _tape = TapeStore.TryOpen(Paths.TapeFile, Gateway.Log);
                if (_tape is not null)
                {
                    Tape = new TapeCollector(_tape, () => Gateway.Settings.RecordMarketContext);
                    if (Tape.CatalogProblem is { } unreadable) Gateway.Log.Activity("Market context: " + unreadable, "warn");
                    foreach (var refused in Tape.Refused) Gateway.Log.Activity("Market context: " + refused, "warn");
                    Tape.Start();

                    // AND GDELT'S RECORDER, BESIDE IT ON THE SAME FILE AND ITS OWN SWITCH. What it has to tell the owner —
                    // a day's cap reached, the backfill's daily bound — is an activity line.
                    Gdelt = new GdeltRecorder(_tape, () => Gateway.Settings.RecordGdeltNews,
                        say: text => Gateway.Log.Activity("GDELT news: " + text, "warn"));
                    Gdelt.Start();

                    // AND THE GATEWAY READS IT, READ-ONLY (U-tape-read): a reader over the same file, never the
                    // store, so `data-tape`, `data-list`, the status and the report can show it and nothing the pipe
                    // reaches can write it. Its catalogue is the one the collectors are recording from. A reader that
                    // cannot be made is said on its own line: the recording goes on, and only the serving stops.
                    try
                    {
                        _tapeReader = new TapeReader(_tape.File, [.. Tape.Rows, .. Core.Data.TapeSourceCatalog.Archives()]);
                        ReportTapeToTheGateway();
                    }
                    catch (Exception ex)
                    {
                        Gateway.Log.Activity("TradeAgent is recording market context but cannot show it to the AI: "
                                             + ex.Message.ReplaceLineEndings(" "), "warn");
                    }
                }
            }
            catch (Exception ex)
            {
                Gateway.Log.Activity(
                    "TradeAgent is not recording market context: " + ex.Message.ReplaceLineEndings(" "), "warn");
            }

            // THE INSTRUMENT CHECK, BUILT WITH THE APP. It asks nothing here: the background loop's first
            // pass checks the configured pair, and so do the owner's pair change and "Check now".
            try { _verifier = new InstrumentVerifier(_db); }
            catch (Exception ex)
            {
                Gateway.Log.Activity(
                    "TradeAgent cannot check instruments against the venue's definition: "
                    + ex.Message.ReplaceLineEndings(" "), "warn");
            }
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

            _server = new GatewayPipeServer(Gateway, IpcToken.Ensure()) { Peer = Containment.PeerRuleNow() };
            _server.Start();
            Health.Set(Components.Gateway, HealthState.READY);

            ComposeTheAi(_db);

            await Connector.ConnectAsync();
            await Gateway.RefreshHealthAsync();
            ReportAtasHealth();

            _loop = new CancellationTokenSource();
            _background = Task.Run(() => BackgroundAsync(_loop.Token));

            Gateway.Log.Activity("TradeAgent started");

            // ON START, BEFORE THE LOOP TAKES A TURN. A crash between a role's publication and the
            // copy into the recipient's folder leaves a task pointing at a file that is not there;
            // this is the pass that puts it there, and it is idempotent, so a clean start does
            // nothing at all. Never fatal: a relay that could not run is a delivery that is late,
            // not an app that will not open.
            try { Relay.Reconcile(); }
            catch (Exception ex) { Gateway.Log.Engineering("Council", "relay_start_failed", "warn", ex: ex); }

            await ResumeOnStartAsync();
            return true;
        }
        catch (Exception ex)
        {
            StartupProblem = ex is TradeAgentException t ? t.Info.UserMessage : ex.Message;
            return false;
        }
    }

    /// <summary>
    /// EVERYTHING ABOVE THE GATEWAY THAT THE AI WORKS THROUGH: the supervisor, the meter, the wake
    /// queue, the boundaries, the relay and the loop, wired to each other and to the gateway.
    ///
    /// <para>One method rather than a block inside <see cref="StartAsync"/> so that
    /// <see cref="Composed"/> builds the same objects the same way, and a test drives this host's own
    /// start path and its own resume rather than a reconstruction of them that could drift.</para>
    /// </summary>
    void ComposeTheAi(Database db)
    {
        // THE MODEL IS READ THROUGH A FUNCTION, not captured: the owner changes it on the Safety
        // page while the agent is running, and the next turn is the one that has to obey.
        Agent = new AgentSupervisor(Health, () => Gateway.Settings.SelectedModelId,
            // The attempt the meter opened for the launch about to happen. A function, because
            // the launch is minutes away from this line and the attempt it belongs to does not
            // exist yet.
            attemptId: role => Meter?.OpenAttemptIdFor(role),
            // THE PROTECTED CONFIGURATION. Read at every launch rather than captured, because
            // the owner arms real money on the Dashboard while the AI is working.
            launchRefusal: () => Containment.RefusalToLaunch(
                Gateway.Settings.ModeIsLive, Gateway.Settings.LiveActivated),
            presence: _presence);
        Meter = new TurnMeter(db,
            cap: AiCap,
            session: () => (Conversation as AgentSession)?.ThreadId,
            runtimeId: () => PricedRuntimeId(Agent.Current?.Id, Gateway.Settings.SelectedRuntimeId),
            owner: () => OwnerPrice.From(Gateway.Settings),
            model: () => RequestedModel,
            allowance: () => TurnAllowance.From(
                Gateway.Settings.AiTurnAllowanceInputTokens, Gateway.Settings.AiTurnAllowanceOutputTokens),
            // The council's two: a role's slice of the owner's ceiling, and the model that role
            // runs on. Both are read through a function for the same reason the model above is —
            // the owner changes them while the AI is working.
            share: role => Gateway.Settings.ShareForRole(role),
            roleModel: RequestedModelFor,
            // A PRICE IS LOOKED UP BY (RUNTIME, MODEL), so a role on the app-owned harness has to
            // be priced against the harness's catalogue. Priced against the chair's, a model only
            // the harness offers falls back to the dearest entry and the row reads as an estimate
            // when the app knows exactly what it asked for.
            roleRuntime: RuntimeForRole);
        Meter.Changed += () => Changed?.Invoke();

        Wakes = new MissionEventStore(db);
        Boundaries = new CouncilBoundaries(db);

        // THE APP CARRYING WORK BETWEEN THE ROLES, AND THE ONLY THING THAT MAY. A role writes a
        // file into its own `out/`; nothing it can do publishes, delivers or creates a task.
        // Composed here, beside the gateway, and reachable from the loop only through
        // IMissionHost.Relay — there is no pipe op and no `trade` verb that touches it.
        Relay = new CouncilRelay(db, HomeFor, boundaries: Boundaries)
        {
            Rejected = text => Gateway.Log.Activity(text, "warn"),
            Quarantined = text => Gateway.Log.Activity(text, "warn")
        };
        Relay.Revisions.Rejected = text => Gateway.Log.Activity(text, "warn");

        Mission = new MissionLoop(new MissionHost(this),
            new MissionOptions
            {
                TurnsPerSession = Math.Max(1, Gateway.Settings.MissionTurnsPerSession),
                ReviewEvery = TimeSpan.FromMinutes(Math.Max(0, Gateway.Settings.MissionReviewMinutes))
            });
        Mission.Changed += () => Changed?.Invoke();

        // A TURN THAT WAS NOT STARTED, WRITTEN DOWN. The engineering log rather than the
        // activity log: a second caller refused because that role is already turning is the
        // council working as designed, not something the owner has to do anything about — and a
        // refusal nobody records is a turn that silently did not happen.
        Mission.Refused += why =>
        {
            try { Gateway.Log.Engineering("Mission", "turn_refused", "info", metadataJson: Json.Write(new { why })); }
            catch (Exception) { /* a log line is never worth a turn */ }
        };

        // THE TWO FACTS THE GATEWAY OWNS AND NOBODY ELSE CAN SEE ARRIVE. A fill and an order
        // reaching a final state are the events the AI most needs to be woken for, and both are
        // known first inside the gateway's own event handling. The sink is a delegate rather
        // than a reference to this host, so nothing reachable from it can change a mode, lift
        // the kill switch or approve anything.
        Gateway.RaiseMissionWake = RaiseWake;
        ReportAiToTheGateway();
    }

    /// <summary>
    /// THE COMPOSITION <see cref="StartAsync"/> BUILDS, WITHOUT THE MACHINE IT RUNS ON — so a test can
    /// drive this host's own start path and its own resume.
    ///
    /// <para><see cref="ComposeTheAi"/> is the same method <see cref="StartAsync"/> calls, run over a
    /// database and a connector the caller owns. Left out is what one test assembly cannot share: the
    /// instance lock and the pipe server (one per home and one per pipe name), the collectors, the
    /// background loop and the relay's start-up reconcile over every role's home. The AI's processes
    /// report to <paramref name="presence"/> rather than to the process-wide register, which is sticky:
    /// one agent process entered there would weaken every inbox sighting the rest of the assembly
    /// records.</para>
    ///
    /// <para><paramref name="reads"/> is the disk its material passes read, for a test that needs a folder
    /// the disk refuses; the machine's own when it is not given.</para>
    /// </summary>
    internal static AppHost Composed(Database db, ITradingConnector connector, AgentPresence presence,
        WorkspaceReads? reads = null)
    {
        var host = new AppHost { _db = db, _presence = presence, _reads = reads ?? WorkspaceReads.Disk };
        host.Onboarding = new OnboardingStore(db);
        host.Connector = connector;
        host.Gateway = new TradingGateway(db, connector, host.Health);
        host.ComposeTheAi(db);
        return host;
    }

    /// <summary>
    /// Changes which backend the gateway executes against, immediately.
    ///
    /// This used to persist the choice and tell the user to restart, which left every later setup
    /// step — "connecting to ATAS", "finding your account", "checking live prices" — interrogating
    /// the connector that was still loaded. Choosing ATAS therefore validated the practice simulator
    /// and finished setup claiming success. A choice that is not applied is not a choice.
    /// </summary>
    /// <summary>
    /// WHOEVER IS LISTENING FOR "a minute closed", whatever platform is selected right now.
    ///
    /// <para>A field rather than a direct subscription because the two ends are built in the wrong
    /// order for one: the connector is created before <see cref="Forward"/> exists, and a connector
    /// switch replaces the connector while the collector goes on running. The collector raises this
    /// one delegate and the delegate is what changes underneath it.</para>
    /// </summary>
    Action<string, DateTimeOffset>? _forwardAnnounce;

    ForwardRuns? _forwardRunner;

    /// <summary>
    /// THE FORWARD RUNNER, ON THE APP'S OWN CLOCK. Built lazily because a connector switch replaces
    /// <see cref="Gateway"/>, and a runner holding the old one would dispatch onto a platform the
    /// owner has moved off.
    ///
    /// <para><b>With the tape's reader from here, never off the gateway</b> (<c>U-runner-features</c>): the gateway's
    /// <c>Tape</c> is set on a new gateway only after it exists, and a pass on it in between would end every run of a
    /// program that reads a feature for a tape that is open. A runner built before the tape opened is built again once it
    /// has: what it kept is values a restart reads again.</para>
    /// </summary>
    ForwardRuns ForwardRunner =>
        _forwardRunner is { } held && ReferenceEquals(held.Gateway, Gateway) && ReferenceEquals(held.Tape, _tapeReader)
            ? held
            : _forwardRunner = new ForwardRuns(Gateway, _db!, tape: _tapeReader);

    /// <summary>
    /// WHAT A PAPER FILL PAYS, RESOLVED FROM THE OWNER'S SETTINGS (<c>U-paper-friction</c>): an override
    /// the owner set is theirs, zero included, and a number they never set is TradeAgent's venue cost
    /// model's — the fee the referee judges with, not a zero nobody chose. The rule itself is
    /// <see cref="Core.Strategy.FrictionInForce.ForPaper"/>, which the status, the daily report and the
    /// Settings row read too; this only hands its answer to the connector in the connector's own type.
    /// </summary>
    internal static PaperFriction PaperFrictionFor(TradeAgentSettings? settings) =>
        PaperFriction.Of(Core.Strategy.FrictionInForce.ForPaper(settings));

    /// <summary>
    /// What the paper connector needs from this host. The friction is READ AT THE MOMENT OF THE FILL
    /// rather than captured now: the connector is built before the gateway that loads the settings
    /// exists, and the owner can change the override while it is running — a fill has to record what it
    /// was actually simulated under, not what the app was started with.
    /// </summary>
    ConnectorChoice PaperChoice() => new()
    {
        PaperFrictionNow = () => PaperFrictionFor(Gateway?.Settings),

        // THE PAPER CONNECTOR'S PRICES ARE THE MINUTES THIS INSTALLATION COLLECTED. The store is
        // handed over rather than a built source: `Connectors.Create` is the one place that decides
        // what a platform id gets, and a host that built the adapter itself would be a second answer.
        ForwardBars = _db is { } db ? new ForwardBarStore(db) : null,
        SubscribeToBarClosed = announce => _forwardAnnounce = announce,

        // THE INSTRUMENTS IT MAY TRADE ARE THE SERVED ONES, READ AT EVERY USE (U-venue-verify): the gateway's
        // catalogue overlaid by the latest successful instrument check of seven days or less, on the
        // gateway's clock. Read through the property, so a connector switch that replaces the gateway reads
        // the new one; before the gateway exists the same database answers on the machine clock.
        PaperInstruments = _db is { } venues ? () => (Gateway?.Venues ?? new VenueStore(venues)).Catalogue() : null
    };

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
        Connector = Platforms.Connectors.Create(id, PaperChoice());
        Gateway = new TradingGateway(_db, Connector, Health);
        Gateway.StateChanged += OnGatewayStateChanged;
        // A new gateway is a new object and the sink is on the object, exactly like the hook below.
        // Forgetting this line is how a fill on the new platform stops waking the AI.
        Gateway.RaiseMissionWake = RaiseWake;

        _server = new GatewayPipeServer(Gateway, IpcToken.Ensure()) { Peer = Containment.PeerRuleNow() };
        _server.Start();
        Health.Set(Components.Gateway, HealthState.READY);
        // A new gateway is a new object, and the hook is on the object. Forgetting this line is how
        // `trade status` starts reporting an AI that is stopped and free while it is working.
        ReportAiToTheGateway();
        // AND THE TAPE'S READER, for the same reason (U-tape-read).
        ReportTapeToTheGateway();

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

    /// <summary>
    /// WHAT THE DAILY REPORT NEEDS FROM THE TWO LAYERS ABOVE THE GATEWAY, read in one go.
    ///
    /// The loop and the meter are here and not there on purpose — the gateway is the execution
    /// authority and must not learn what a loop is — so the report is HANDED what they know instead
    /// of reaching for it. Everything else in the report the gateway reads out of its own tables.
    ///
    /// The whole-installation reading comes first and each role's after it, which is the order
    /// <see cref="DailyReports"/> expects: a role is bounded by its share AND by the owner's ceiling,
    /// and a report showing only the shares would leave the ceiling unaccounted for.
    /// </summary>
    public DailyReportInputs ReportInputs()
    {
        var spend = new List<AiSpendToday> { SpendToday };
        if (Meter is { } meter)
            foreach (var role in CouncilRoles.All) spend.Add(meter.TodayFor(role));

        var status = Mission?.Status;
        return new DailyReportInputs
        {
            MissionState = status?.State.ToString().ToLowerInvariant(),
            MissionReason = status?.WaitingFor,
            NextEligibleWake = Wakes?.NextDueAt(),
            Spending = spend,
            Runtime = PricingRuntime,
            // WHETHER THE APP-OWNED HARNESS CAN RUN AT ALL, in two words and never the key. It belongs
            // on the report because a role on the harness with no key starts nothing: without this line
            // the owner reads a day of zero research turns with no reason beside it, and the reason is
            // that a key is not held and is not kept across a restart.
            HarnessKeyHeld = HarnessKey.Held,
            NextReviewAt = status?.NextTurnAt,
            // A REVIEW THAT IS PENDING BECAUSE IT WAS NOT FUNDED, which rule 10 asks for by name. It
            // is not the same fact as a review that has not come round yet, and an owner reading
            // "waiting" needs to know which of the two they are looking at.
            ReviewNotFunded = SpendToday is { Metered: true } s && !s.AdmitsAnotherTurn
                ? "the day's spending ceiling is reached, so no turn can be taken until it resets"
                : null
        };
    }

    /// <summary>
    /// WRITES EVERY DAY'S REPORT THAT IS OWED AND HAS NOT BEEN WRITTEN, oldest first.
    ///
    /// Called on start and on the slow loop rather than by a timer set for midnight, because the
    /// machine this ships to is a laptop: it is asleep at midnight most nights, and a report that
    /// only ever appears if the app happened to be awake at 00:00 is a report the owner cannot rely
    /// on. Asking "which days have no file" is the same question with an answer that survives being
    /// switched off.
    ///
    /// It never throws: a report is a document, and an app that would not open because it could not
    /// write one has turned a record into a dependency.
    /// </summary>
    public void WriteOwedReports()
    {
        try
        {
            var inputs = ReportInputs();
            foreach (var day in Gateway.Reports.Owed(DateTimeOffset.Now))
                Gateway.Reports.Write(day, inputs);
        }
        catch (Exception ex)
        {
            try { Gateway.Log.Engineering("Report", "daily_report_failed", "warn", ex: ex); }
            catch (Exception) { /* the log is the same database the report could not read */ }
        }
    }

    void OnGatewayStateChanged() => Changed?.Invoke();

    public WorkspaceContext WorkspaceContext()
    {
        var available = Gateway.TryAuthorizeExecution(AgentContext.Operator, out var reason);
        return new WorkspaceContext(Connector.DisplayName, Connector.Capabilities.IsPaper,
            Gateway.Settings.SelectedAccountId, Gateway.Settings.Mode, available, reason, Gateway.Settings.Risk,
            ConnectorIsBuiltInSimulator: Connector.Id == FakeConnector.ConnectorId,
            TurnsPerSession: Gateway.Settings.MissionTurnsPerSession);
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

                // AND EVERY PAPER DEPLOYMENT, at start-up and on every pass. This is where a run's
                // own operations are settled from their order rows, where a platform, mode or account
                // that moved suspends one, where a grant or a verdict that has gone ends one, and
                // where an operation that was written down and never sent is dispatched — once. It is
                // on the APP's clock rather than the AI's because none of it is a turn and a paused
                // AI must not leave an order this gateway cannot account for.
                await Gateway.ReconcilePaperDeploymentsAsync(ct: ct);

                // AND THE RUNS THEMSELVES, over whatever has closed since. The runner rebuilds each
                // deployment's evaluator from the forward bars and dispatches what the frozen program
                // decided — on the app's clock, for the reason above: a strategy running forward is
                // not a turn, and a paused AI or an exhausted ceiling must not stop protection.
                await ForwardRunner.AdvanceAsync(ct);
                Gateway.Log.Rotate();

                var pass = tick++;

                // Every 30s rather than every 5s. Nothing downstream needs a file noticed within
                // five seconds, and the walk plus a bounded round of hashing is the most expensive
                // thing in this loop.
                //
                // A PASS REFUSED because a role holds its turn lease (or another pass is measuring)
                // is OWED, and asked for again on the next tick rather than in half a minute: never
                // beside a launch, and no later than it has to be once the turn is over.
                if (pass % 6 == 0 || _scanOwed) _scanOwed = ScanMaterials(ct) is null;

                // Once at startup, then every five minutes. A day that ended while this machine was
                // asleep gets its report the first time the app is awake afterwards; a day that ends
                // while it is running gets one within five minutes of midnight. Cheap: on a day whose
                // file already exists it is one File.Exists per day looked back over.
                if (pass % 60 == 0) WriteOwedReports();

                // Once at startup, then every six hours. This only ever lights a banner: nothing in
                // this loop downloads or installs anything, because a trading application that
                // restarts itself while the owner is looking elsewhere is not a convenience.
                if (pass % (12 * 60 * 6) == 0 && AutoCheckForUpdates) _ = Updates.CheckAsync(ct);

                // THE CONFIGURED PAIR AGAINST THE VENUE'S OWN DEFINITION: once at startup, then every six
                // hours (U-venue-verify). A successful check is served for seven days, so an app left
                // running re-reads it long before it lapses, and a venue that changes a step is noticed
                // within the day. It never throws: a failure is a row and an activity line.
                if (pass % (12 * 60 * 6) == 0) _ = CheckInstrumentAsync(ct);

                Changed?.Invoke();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Gateway.Log.Engineering("App", "background_error", "warn", ex: ex); }

            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// CHECKS THE CONFIGURED MARKET-DATA PAIR AGAINST BINANCE SPOT'S OWN PUBLISHED DEFINITION, records the
    /// attempt and says what happened in the activity log (<c>U-venue-verify</c>). Run at startup and every
    /// six hours by the background loop, when the owner changes the pair, and by the owner's "Check now".
    ///
    /// <para>In-process only: nothing on the agent-facing pipe reaches it. It never throws — a vendor
    /// failure is the verifier's row, and anything else is a line in the engineering log — so a caller may
    /// discard the task. Null when nothing was asked: no verifier, or a pair TradeAgent will not put in an
    /// address.</para>
    /// </summary>
    public async Task<Core.Data.InstrumentCheckRow?> CheckInstrumentAsync(CancellationToken ct = default)
    {
        if (_verifier is not { } verifier || Gateway is not { } gateway) return null;

        var pair = gateway.Settings.MarketDataPair;
        try
        {
            var row = await verifier.CheckAsync(Core.Data.VenueCatalog.BinanceSpot, pair, ct);
            if (row is not null)
                gateway.Log.Activity(
                    "Instrument check: " + InstrumentVerifier.Describe(row, VenueName(gateway, row.VenueId)),
                    row.IsVerified ? "info" : "warn");
            Changed?.Invoke();
            return row;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            gateway.Log.Engineering("App", "instrument_check_failed", "warn", ex: ex);
            return null;
        }
    }

    /// <summary>A venue as a person names it, off the recorded catalogue, or its id where the catalogue has none.</summary>
    static string VenueName(TradingGateway gateway, string venueId)
    {
        try
        {
            return gateway.Venues.Venues().FirstOrDefault(v => string.Equals(v.Id, venueId, StringComparison.Ordinal))
                ?.DisplayName ?? venueId;
        }
        catch (Exception) { return venueId; }
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
    ///
    /// <para><b>Under the loop's one exclusion</b> (<see cref="MissionLoop.TryPass{T}"/>, U-inbox-order):
    /// the thirty-second tick and the Inbox page both come here, and neither may walk the tree while a
    /// role holds its turn lease, nor let a role launch while the walk runs. A pass beside a launching
    /// role spends every sighting it makes on the weaker word, for good. Null is a pass that was
    /// refused for that reason; the loop takes one of its own behind every turn, and the caller may ask
    /// again.</para>
    /// </summary>
    public ScanResult? ScanMaterials(CancellationToken ct = default) =>
        Mission.TryPass(() => RecordWorkspace(ct));

    /// <summary>
    /// ONE PASS, with no exclusion of its own: called only from inside one — the loop's
    /// (<see cref="MissionHost.ScanAsync"/>) or the app's (<see cref="ScanMaterials"/>).
    /// </summary>
    ScanResult RecordWorkspace(CancellationToken ct)
    {
        var at = DateTimeOffset.UtcNow;
        Interlocked.Exchange(ref _lastScanAtTicks, at.UtcTicks);
        // THE REGISTER THIS HOST'S AI PROCESSES REPORT TO, which the pass takes its mark in and asks.
        // The process-wide one in the product; a composed test host's own, so that its scans and its
        // agents are one register there too.
        var result = new MaterialScanner(_db!, noAgentSince: _presence.NoneSince, reads: _reads).Scan(ct);
        if (result.Added > 0 || result.Removed > 0)
            Gateway.Log.Engineering("Materials", "scan", "info", metadataJson: Json.Write(result));

        // WHAT THIS PASS COULD NOT READ, said where the owner looks — once when it starts, once when
        // what it names changes and once when it clears, never on every tick.
        if (UnreadableLine(_unreadableSaid, result) is { } line)
        {
            Gateway.Log.Activity(line.Text, line.Level);
            _unreadableSaid = line.Standing;
        }

        if (InboxWake(result, at) is { } wake)
            // THE CHAIR'S: the owner's material reaches the role the owner talks to first.
            RaiseWake(wake.Id, MissionEventKind.Inbox, wake.Payload, CouncilRoles.Operations);

        return result;
    }

    /// <summary>
    /// THE "COULD NOT READ" LINE THE ACTIVITY LOG WAS LAST GIVEN, while it stands; null when none does.
    /// Read and written only inside a pass, and every pass comes through <see cref="RecordWorkspace"/>
    /// under the loop's one exclusion, so no two passes touch it at once. In memory: after a restart a
    /// folder that still cannot be read is said once more, which is the first the new process knows of it.
    /// </summary>
    string? _unreadableSaid;

    /// <summary>
    /// THE ONE ACTIVITY LINE A PASS OWES ABOUT WHAT IT COULD NOT READ, or none (U-inbox-unreadable).
    ///
    /// <para>A pass that could not read a folder is not complete: it holds the window open, so files that
    /// arrive in the owner's drop folder meanwhile may be recorded as ones TradeAgent cannot say who put
    /// there, and it marks nothing missing where it could not look. That costs the owner a word on the
    /// Inbox page, and it must not cost it silently — so the line names the folders, relative to the
    /// workspace, and says what follows; it never prints a path outside the workspace or anything inside
    /// a file.</para>
    ///
    /// <para><b>Said on change only.</b> A warning when the folders named are not the ones last said —
    /// the condition starting, or moving — and nothing while a pass finds exactly what was said. The
    /// clearing line needs a COMPLETE pass: one that ran out of budget may never have reached the folder,
    /// and "it can be read again" would be a claim nothing measured.</para>
    /// </summary>
    /// <param name="standing">The warning last said and not yet cleared, or null.</param>
    /// <param name="pass">What this pass found.</param>
    /// <returns>The line, its level, and what stands after it — or null when nothing is owed.</returns>
    internal static (string Text, string Level, string? Standing)? UnreadableLine(string? standing, ScanResult pass)
    {
        if (pass.Unreadable > 0)
        {
            var warning = CouldNotRead(pass);
            return warning == standing ? null : (warning, "warn", warning);
        }
        return standing is not null && pass.Complete ? (ReadableAgain, "info", null) : null;
    }

    /// <summary>The line that clears <see cref="UnreadableLine"/>'s warning.</summary>
    internal const string ReadableAgain = "TradeAgent can read all of your inbox and the AI's folders again.";

    /// <summary>How many folders the warning names before it counts the rest.</summary>
    const int UnreadableShown = 3;

    static string CouldNotRead(ScanResult pass)
    {
        var folders = pass.UnreadableFolders;
        var inInbox = folders.Any(MaterialScanner.IsInInbox);
        var inHomes = folders.Any(f => !MaterialScanner.IsInInbox(f));
        var where = (inInbox, inHomes) switch
        {
            (true, false) => "Part of your inbox",
            (false, true) => "Part of the AI's folders",
            _ => "Parts of your inbox and of the AI's folders"
        };
        var shown = folders.Take(UnreadableShown).Select(Printable).ToList();
        var more = pass.Unreadable - shown.Count;
        var named = string.Join(", ", shown) + (more > 0 ? $" and {more} more folder{(more == 1 ? "" : "s")}" : "");
        return $"{where} could not be read: {named}. TradeAgent will look again; until it can, a file that "
               + "arrives in your inbox may not be listed as one you gave the AI.";
    }

    /// <summary>
    /// A FOLDER'S NAME AS THE ACTIVITY LOG MAY PRINT IT. Names in the workspace are chosen by whoever made
    /// the folder — the AI among them — and this line is read by the owner, written into the daily
    /// report and joined line by line into a diagnostics bundle: a line break, a control character or a
    /// character that reorders how text is drawn (U+202E) would let a folder's name pass for a line
    /// TradeAgent wrote. Each becomes '?', and a name longer than any honest one is cut, visibly.
    /// </summary>
    internal static string Printable(string folder)
    {
        const int longest = 80;
        var b = new System.Text.StringBuilder(Math.Min(folder.Length, longest + 1));
        foreach (var c in folder)
        {
            if (b.Length == longest)
            {
                if (char.IsHighSurrogate(b[^1])) b.Length--;
                b.Append('…');
                break;
            }
            b.Append(char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Control
                or System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.LineSeparator
                or System.Globalization.UnicodeCategory.ParagraphSeparator ? '?' : c);
        }
        return b.ToString();
    }

    /// <summary>
    /// THE INBOX WAKE ONE PASS RAISES, OR NONE. It buys a paid turn, so it is owed only for material
    /// that ARRIVED (<see cref="ScanResult.Arrived"/>): a file in the owner's <c>inbox/</c>, attested
    /// or not.
    ///
    /// <para><b>Arrived, not added.</b> A role's own write is an addition that role already knows
    /// about, and the app's own files and deliveries are no news either — a delivery wakes its
    /// recipient through its own <c>task:</c> event. Waking on every addition woke the Operations
    /// Director once a minute for its own plan and journal (BUILD-STATUS.md, U-self-wake). All of
    /// them are still recorded; the ledger is the scanner's and nothing here touches it.</para>
    ///
    /// <para><b>Added, not changed.</b> A pass that only hashed files it had already recorded, or
    /// watched one go, has told the AI nothing it did not know. The id is the instant the pass
    /// began, so the same pass reported twice is one reason to wake, and the payload counts what
    /// arrived — the files the wake's words are about.</para>
    /// </summary>
    internal static (string Id, string Payload)? InboxWake(ScanResult result, DateTimeOffset at) =>
        result.Arrived > 0
            ? (MissionEventIds.Inbox(at), Json.Write(new { added = result.Arrived, seen = result.Seen }))
            : null;

    // ---- starting the AI -----------------------------------------------------------------------

    /// <summary>
    /// STARTS THE AI, AND IS THE ONE THING THAT DOES. The Start the AI press, the last screen of
    /// setup and a restart that resumes a working AI all come here, so none of them can skip a check
    /// another makes: the runtime chosen in settings, <see cref="RuntimeCatalog.Require"/> rather than
    /// <c>Find</c>, then prepare, then start — and the start is where the protected configuration
    /// refuses with <c>CONTAINMENT_REQUIRED</c>, in the runtime, exactly as it always has.
    ///
    /// <para><b>A start that throws is said, never swallowed.</b> The owner's own words go on the card
    /// (<see cref="AiNotStarted"/>) and into the activity log with the code beside them, the detail
    /// into the engineering log, and the exception goes on to the caller: a press shows it in the strip
    /// under the header as it always did, and the resume has nobody to show it to but these two. The AI
    /// is left stopped — the supervisor keeps no runtime that would not start, so nothing can launch a
    /// turn through it.</para>
    ///
    /// <para>It switches no page. The press does that itself, afterwards, because the owner pressed
    /// something that means "let me talk to it"; a restart pressed nothing.</para>
    /// </summary>
    public async Task StartTheAiAsync()
    {
        try
        {
            // Require, not Find: an unreadable runtimes.json yields no manifests at all rather than
            // the built-ins, so this is the call that turns "the file the owner wrote cannot be read"
            // into a refusal in their own words instead of a different program starting quietly — and
            // "no manifest for 'codex'" and "runtimes.json could not be read" are different mornings,
            // only one of which has a repair the owner can perform.
            var manifest = RuntimeCatalog.Require(Gateway.Settings.SelectedRuntimeId ?? "opencode");
            await Agent.PrepareAsync(manifest, WorkspaceContext());
            await Agent.StartAsync();
        }
        catch (Exception ex)
        {
            var why = InTheOwnersWords(ex);
            AiNotStarted = why;
            try
            {
                Gateway.Log.Activity(ex is TradeAgentException t
                    ? $"The AI was not started: {why} ({t.Code})"
                    : $"The AI was not started: {why}", "warn");
                Gateway.Log.Engineering("Agent", "not_started", "warn", ex: ex);
            }
            catch (Exception) { /* the log is the database; the card and the caller still have it */ }
            Changed?.Invoke();
            throw;
        }

        AiNotStarted = null;

        // THE CONVERSATION IS OPENED HERE, on the thread that started the AI, and metered once. The
        // getter makes it on first use and is not safe to race: a resumed loop asks for it from its
        // own thread the moment it starts while the window asks from the UI thread, and two first
        // uses at once could open two sessions or attach the meter twice.
        _ = Conversation;
        Changed?.Invoke();
    }

    /// <summary>
    /// WHY THE LAST START DID NOT HAPPEN, in the owner's words, or null when it did. Cleared by the next
    /// start that works; the Dashboard card says it while the AI is stopped.
    /// </summary>
    public string? AiNotStarted { get; private set; }

    /// <summary>
    /// The sentence the owner reads about a failure: what happened and what to do, never a stack trace.
    /// The same words the strip under the header shows for a failed press (<c>Ui.Report</c>), so a
    /// start refused on a restart reads exactly as the same start refused on the button.
    /// </summary>
    internal static string InTheOwnersWords(Exception ex) =>
        ex is TradeAgentException t ? $"{t.Info.UserMessage} {t.Info.Repair}".Trim() : ex.Message;

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
        // started is not a new instruction — it takes the wakes that are already due and raises
        // none of its own.
        RaiseWake(MissionEventIds.Review(DateTimeOffset.Now), MissionEventKind.Review,
            Json.Write(new { because = "the owner set the AI to work on its own" }), CouncilRoles.Operations);
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

    /// <summary>
    /// THE ACTIVITY LINE FOR A VENDOR'S USAGE LIMIT: the card's own sentence, and then the vendor's words,
    /// which are the part that says what the owner can do about it. Static, so the words can be read back
    /// without a running app.
    /// </summary>
    internal static string VendorLimitLine(VendorHold hold, DateTimeOffset now) =>
        $"{hold.Sentence(now)}. {hold.Vendor} said: {hold.Message}";

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

    /// <summary>
    /// WHAT <see cref="StartAsync"/> DOES LAST: acts on <see cref="DecideOnStart"/>.
    ///
    /// <para><b>Resuming starts the AI as well as the loop.</b> The loop alone takes no turn: with no
    /// runtime there is no conversation, so every look it takes ends without one and the card reads
    /// "the AI has not been started" until somebody presses the button — measured in the observed run
    /// of 2026-10-01, and the morning an autonomous product exists not to have. So the AI is started
    /// first, through <see cref="StartTheAiAsync"/> and only through it, so the restart meets every
    /// check the press meets and is refused in the same words; then the loop, so its first look
    /// already has a conversation to take the next due wake with.</para>
    ///
    /// <para><b>Only once setup is finished.</b> Before that the last screen of setup is where the AI
    /// is started, by the owner. And a start that is refused still resumes the loop, exactly as before:
    /// the owner's choice stands, the loop takes nothing while the AI is stopped, and the press that
    /// starts it later is all the owner has to do.</para>
    ///
    /// <para>A start that fails does not fail the app's own start. It has already said why on the card
    /// and in the activity log, and an app that would not open because the AI would not start has
    /// turned a refusal into an outage.</para>
    /// </summary>
    internal async Task ResumeOnStartAsync()
    {
        switch (DecideOnStart(Gateway.Settings))
        {
            case MissionOnStart.Resume:
                try { if (Onboarding.IsComplete()) await StartTheAiAsync(); }
                catch (Exception) { /* said on the card and in the activity log by StartTheAiAsync */ }
                Mission.Start();
                break;
            case MissionOnStart.ForgetItWasWorking:
                Gateway.Update(s => s.AiWorksOnItsOwn = false);
                break;
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

        /// <summary>One role's conversation. The chair's is the window's; see <see cref="AppHost.ConversationFor"/>.</summary>
        public IAgentConversation? ConversationFor(string role) => host.ConversationFor(role);

        public string AgentHome => host.Agent.Workspace is { Length: > 0 } w ? w : Paths.AgentHome;

        /// <summary>One role's own folder, where its <c>next.json</c>, its <c>in/</c> and its <c>out/</c> are.</summary>
        public string HomeFor(string role) => host.HomeFor(role);

        public bool InboxChangedSinceLastPass =>
            MissionInbox.HoldsUnrecorded(Paths.Workspace, host.LastScanAt, host._db!);

        public AiSpendToday Spend => host.SpendToday;

        /// <summary>
        /// What one role has spent and committed today, and the slice of the owner's ceiling it may
        /// spend. The loop admits a turn only when this AND the day's ceiling both have room.
        /// </summary>
        public AiSpendToday SpendFor(string role) => host.Meter?.TodayFor(role) ?? AiSpendToday.NotMetered;

        /// <summary>The persisted reasons to wake. Read by the loop; written by the app only.</summary>
        public MissionEventStore? Events => host.Wakes;
        public CouncilBoundaries? Boundaries => host.Boundaries;

        /// <summary>
        /// The app's own paper-allocation policy, run on the loop's periodic seam. It writes a PAPER
        /// allocation inside the envelope the owner granted and buys nobody a turn beyond the single
        /// deduplicated note the gateway publishes; it dispatches nothing and touches no capital.
        /// </summary>
        public void AllocatePaperDue(DateTimeOffset now) => host.Gateway.AllocatePaperDue(now);

        /// <summary>
        /// The app's own paper-deployment policy, on the same seam and straight after the allocation
        /// one. It writes at most one deployment row per standing paper allocation, inside the
        /// envelope the owner granted, and it dispatches nothing at all: the orders a run sends go
        /// out of the gateway's own reconcile pass in <c>BackgroundAsync</c>.
        /// </summary>
        public void StartPaperDeploymentsDue(DateTimeOffset now) => host.Gateway.StartPaperDeploymentsDue(now);

        /// <summary>
        /// The launch record and its reservation, written before the CLI starts, together with the
        /// wakes this turn is answering. Nothing else on this interface writes to the database, and
        /// this one cannot change a mode, lift the kill switch or approve anything — it commits
        /// money the AI is about to spend on itself.
        /// </summary>
        public AiAdmission BeginTurn(string prompt, IReadOnlyList<string> wakes) =>
            host.Meter?.Begin(prompt, wakes) ?? AiAdmission.Unrecorded;

        /// <summary>
        /// The same, naming the role being charged, so the day's spending can be allocated at all —
        /// a bill nobody can allocate cannot be shared between two roles.
        /// </summary>
        public AiAdmission BeginTurn(string prompt, IReadOnlyList<string> wakes, string role) =>
            host.Meter?.Begin(prompt, wakes, role) ?? AiAdmission.Unrecorded;

        /// <summary>
        /// What the turn left in <c>out/</c>, published and delivered — and anything an earlier
        /// crash left half done, finished. It writes to the relay's two tables and to the roles'
        /// folders, and to nothing that decides what the AI is allowed to do.
        /// </summary>
        public void Relay(string role, string? attempt) => host.Relay.Run(role, attempt);

        /// <summary>
        /// THE TURN'S ONE COMMITTED TRANSITION. The meter's held close, the relay's publications,
        /// the role's plan and journal revisions and the loop's dispositions, in one
        /// <c>Database.Write</c>. It writes to the launch ledger, the relay's tables and the wake
        /// queue, and to nothing that decides what the AI is allowed to do.
        /// </summary>
        public void CommitTurn(string role, string? attempt, Action dispositions) =>
            host.Relay.CommitTurn(role, attempt, () => host.Meter?.CommitStaged(role), dispositions);

        /// <summary>
        /// The id the next launch will carry, minted by the meter so the turn's message can name it.
        /// It commits nothing — the launch record is <see cref="BeginTurn"/>'s — and it cannot change
        /// a mode, lift the kill switch or approve anything.
        /// </summary>
        public string? NextAttemptId() => host.Meter?.Mint();

        /// <summary>
        /// The artifact one delivered task is about, read out of the app's own publication table
        /// rather than off the recipient's disk: the row is what the app committed, and the file is
        /// a copy of it that an agent could have edited.
        /// </summary>
        /// <summary>
        /// WHAT <paramref name="role"/> OWNS THAT IS OPEN, from the ledgers and from the chair's book as the last
        /// Situation read it (<see cref="AppHost._lastBook"/>) — never a broker call: the loop asks on every pass.
        /// </summary>
        public IReadOnlyList<RoleObjective>? Objectives(string role) =>
            host._db is { } db ? RoleObjectives.Open(db, role, Volatile.Read(ref host._lastBook)) : null;

        public MissionDelivery? Delivered(string publicationId)
        {
            try
            {
                var p = new PublicationStore(host._db!).Get(publicationId);
                return p is null ? null : new MissionDelivery(p.Id, p.Kind, p.Role, p.Content);
            }
            catch (Exception) { return null; }
        }

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

        /// <summary>
        /// The runtime one role's turns run on — the same rule the reservation is priced by — so a
        /// vendor's usage limit holds the roles that vendor would refuse and no others.
        /// </summary>
        public string? RuntimeFor(string role) => host.RuntimeForRole(role);

        /// <summary>
        /// The one activity line the owner gets when their AI tool's own plan runs out — see
        /// <see cref="VendorLimitLine"/> — written where they already look for what the software did.
        /// </summary>
        public void VendorLimitReached(string role, VendorHold hold)
        {
            host.Gateway.Log.Activity(VendorLimitLine(hold, DateTimeOffset.Now), "warn");
            host.Changed?.Invoke();
        }

        /// <summary>
        /// The loop's own pass, run inside its exclusion — so it records the workspace directly rather
        /// than through <see cref="AppHost.ScanMaterials"/>, which would find that exclusion taken and
        /// refuse.
        /// </summary>
        public Task ScanAsync(CancellationToken ct)
        {
            try { host.RecordWorkspace(ct); }
            catch (OperationCanceledException) { throw; }
            // A scan that threw must not stop the mission. It is a record-keeping pass, and the
            // engineering log already has the exception from the background loop that also runs it.
            catch (Exception ex) { host.Gateway.Log.Engineering("Materials", "mission_scan_failed", "warn", ex: ex); }
            return Task.CompletedTask;
        }

        /// <summary>
        /// THE SAME SITUATION, NARROWED TO ONE ROLE: which role it is, and that role's own reading
        /// of the day's spending rather than the council's total. The turn is being told what IT may
        /// spend, and a role handed the whole day's figure would plan against another role's money.
        /// </summary>
        public async Task<MissionSituation> SituationAsync(string role, CancellationToken ct) =>
            (await Compose(role, ct)) with
            {
                Role = role,
                Spend = SpendFor(role),
                // WHAT THIS DIRECTOR OWES A CONSEQUENTIAL BOUNDARY, AND BY WHEN. It is per role because
                // an assessment is per director: the line says whether this one has written its own,
                // whether the pair has been released, and what TradeAgent will decide without it.
                Boundaries = OpenBoundariesFor(role),
                // WHAT THE APP REFUSED AND PUT BACK SINCE THIS ROLE LAST TURNED. A plan restored
                // under an agent that is not told is the app editing its memory behind its back,
                // and the next turn would spend itself wondering where its work went.
                Restored = host._db is { } db ? WorkspaceRevisions.Notices(db, role) : []
            };

        /// <summary>The chair's, where no role is named.</summary>
        public Task<MissionSituation> SituationAsync(CancellationToken ct) => SituationAsync(CouncilRoles.Default, ct);

        async Task<MissionSituation> Compose(string role, CancellationToken ct)
        {
            var status = await host.Gateway.StatusAsync(ct);
            // SINCE THIS ROLE'S OWN LAST SITUATION (U-reconcile-wakes): a file listed to Research is
            // still news to the chair, and the other way round.
            var since = host._inboxSeen.Take(role, DateTimeOffset.UtcNow);

            // Positions come from the broker and the broker can be down. A turn told "positions: none"
            // because a call failed would be a turn reasoning about an account it cannot see, so the
            // failure is said in the words the AI reads rather than rendered as an empty list.
            IReadOnlyList<string> positions;
            IReadOnlyList<ConnectorSdk.PositionInfo>? held = null;
            try
            {
                held = await host.Gateway.PositionsAsync(ct);
                positions = held.Select(p => $"{p.Symbol} {p.Quantity:+#;-#;0} at {p.AveragePrice}").ToArray();
                Volatile.Write(ref host._lastBook, held.Where(p => p.Quantity != 0)
                    .Select(p => $"{p.Symbol} {p.Quantity:+#;-#;0}").ToArray());
            }
            catch (Exception ex)
            {
                positions = [$"could not be read — {ex.Message}"];
                Volatile.Write(ref host._lastBook, null);     // an unread book is not an empty one
            }

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
                NewMaterial = host._db is { } inboxDb ? RoleInboxMarks.Arrived(inboxDb, since) : [],
                Guidance = host.Gateway.Settings.Guidance,
                Spend = host.SpendToday,
                Loss = loss,
                Data = MissionSituation.DataLine(NewestDataset(), DateTimeOffset.UtcNow),
                Promoted = PromotedLine()
            };
        }

        /// <summary>
        /// THE OPEN BOUNDARIES THIS ROLE IS PART OF, one line each, oldest first. A failure to read the
        /// ledger answers an empty list rather than throwing out of the middle of composing a turn — the
        /// deadline is applied by the loop's own sweep either way, so a missing line costs a turn's
        /// prompt and never the decision.
        /// </summary>
        IReadOnlyList<string> OpenBoundariesFor(string role)
        {
            try { return [.. (host.Boundaries?.OpenFor(role) ?? []).Select(b => b.Line())]; }
            catch (Exception) { return []; }
        }

        /// <summary>
        /// WHAT STANDS PROMOTED RIGHT NOW, or null when nothing does.
        ///
        /// <para>The standing is asked for each recent judgement in turn and the first that still HOLDS
        /// is the answer — <c>Promotions.Standing</c> computes invalidation at read time, so a promotion
        /// whose dataset was rejected or whose evaluation semantics moved is skipped here rather than
        /// reported as current. When none of them holds, the newest judgement is returned anyway so the
        /// line can say what was withdrawn and why; a turn told only "none" would go looking for a
        /// verdict that is on the table.</para>
        ///
        /// <para>A failure to read the ledger answers null — the line then says nothing is promoted,
        /// which is the safe reading — rather than throwing out of the middle of a turn's message.</para>
        /// </summary>
        PromotionStanding? PromotedStanding()
        {
            // The selection itself is `Promotions.Current` — section 3 of the owner's report asks the
            // same question, and two copies of "what is promoted" are two answers about the one fact
            // that decides whether anything may trade at all.
            try { return new Promotions(host._db!).Current(); }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// WHAT CAPITAL STANDS BEHIND THE PROMOTED VERSION, or null because none does.
        ///
        /// <para>The gateway's own reader, so the Situation and the order path agree on one answer.
        /// <c>Authorises</c> and not merely "a row is in force": an allocation whose promotion has
        /// been withdrawn authorises nothing, and telling a turn about it would be telling it about
        /// capital it cannot spend. A failure to read answers null, which the line reads as "none" —
        /// the safe direction, and the one <see cref="PromotedStanding"/> already takes.</para>
        /// </summary>
        AllocationRow? PromotedAllocation()
        {
            try
            {
                if (PromotedStanding() is not { IsPromoted: true, Promotion: { } promotion }) return null;
                return host.Gateway.Allocations.StandingForLive(promotion.VersionId, DateTimeOffset.UtcNow)
                    is { Authorises: true } standing ? standing.Allocation : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// THE WHOLE PROMOTED LINE, INCLUDING WHAT THE APP PUT ON PAPER BY ITSELF.
        ///
        /// <para><c>Promotions.Current</c> answers with a PROMOTED version or an invalidated one and
        /// with nothing else, which is right for the half of this line that is about capital. A version
        /// that is only <c>paper_eligible</c> is therefore invisible to it — and it is exactly the
        /// version the app will have put on paper, so this falls back to the verdict of whatever
        /// version holds a standing paper allocation on the pair the gateway is running on. Neither
        /// reader is changed: section 3 of the owner's report goes on asking the same question it
        /// asked.</para>
        ///
        /// <para>The paper clause is passed ONLY when it is about the same version the line is about.
        /// A promoted version A beside a paper experiment on version B is two facts, and one sentence
        /// carrying both would read as though A were the one on paper.</para>
        /// </summary>
        string PromotedLine()
        {
            var paper = PaperOnThisPair();
            var standing = PromotedStanding() ?? paper?.Promotion;

            var matched = paper is { } p && standing?.Promotion is { } promotion
                          && string.Equals(p.Allocation.VersionId, promotion.VersionId, StringComparison.Ordinal)
                ? p.Allocation
                : null;

            return MissionSituation.PromotedLine(standing, PromotedAllocation(), matched);
        }

        /// <summary>
        /// WHAT THE APP HAS PUT ON PAPER ON THE PAIR THE GATEWAY IS RUNNING ON, or null because it has
        /// put nothing there.
        ///
        /// <para>The platform and the account are matched because a paper allocation is a statement
        /// about one of each, and <c>Authorises</c> rather than "a row exists": a withdrawn envelope
        /// authorises nothing, and a turn told about it would plan a run the gateway refuses. A failure
        /// to read answers null, which the line reads as "none" — the safe direction, and the one
        /// <see cref="PromotedStanding"/> already takes.</para>
        /// </summary>
        AllocationStanding? PaperOnThisPair()
        {
            try
            {
                return host.Gateway.Allocations.PaperStanding(DateTimeOffset.UtcNow)
                    .FirstOrDefault(p => p.Authorises
                        && string.Equals(p.Allocation.ConnectorId, host.Gateway.Connector.Id, StringComparison.Ordinal)
                        && string.Equals(p.Allocation.AccountId, host.Gateway.ClosureAccountId, StringComparison.Ordinal));
            }
            catch (Exception) { return null; }
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
    }

    /// <summary>
    /// HOW LONG A QUIT WAITS FOR THE AI TO STOP. Each conversation's teardown is bounded on its own
    /// (<see cref="TreeTeardown.Bound"/>); this is the bound on all of them together, so a quit is never
    /// held by a stop that will not finish.
    /// </summary>
    internal static readonly TimeSpan QuitBound = TimeSpan.FromSeconds(15);

    int _disposed;

    /// <summary>
    /// TRUE FROM THE MOMENT THE QUIT BEGINS TO DISPOSE THIS HOST. The window reads it before it reads anything of the
    /// host's: a refresh after the ledgers have closed throws, and on the setup screen the problem it shows re-renders
    /// the screen, which refreshes again — measured with SIGTERM on the first start's setup screen (U-linux-host,
    /// CI's linux-host job and this Mac): the UI thread re-posted the same throwing refresh forever, the dispatcher
    /// never emptied, the lifetime's Shutdown returned into a loop that could not end, and systemd killed the app
    /// 30 s later having burnt a core the whole time.
    /// </summary>
    public bool Stopped => Volatile.Read(ref _disposed) == 1;

    /// <summary>
    /// THE AI STOPPED, FIRST, ON EVERY WAY OUT OF THE APP (<c>U-agent-tree</c> item 4): the loop paused —
    /// <see cref="MissionLoop.PauseAsync"/>, never <see cref="PauseTheAiAsync"/>, because the owner's choice
    /// that the AI works on its own, and that a restart resumes it, is theirs and a quit is not a decision
    /// to change it — and then the AI stopped, every role's conversation with it. Each returns once the
    /// turn's whole tree is gone, so the launch row closes and the presence ends over a turn that is over,
    /// while the database is still open to record it. Measured before this existed: the app's dispose
    /// with a turn in flight left all six of the probe's processes running, after the host had exited.
    /// </summary>
    async Task StopTheAiForQuitAsync()
    {
        using var bound = new CancellationTokenSource(QuitBound);
        try { if (Mission is not null) await Mission.PauseAsync().WaitAsync(bound.Token); }
        catch (Exception) { /* bounded: the quit goes on, and the launcher's own sweep is behind it */ }
        try { if (Agent is not null) await Agent.StopAsync(bound.Token).WaitAsync(bound.Token); }
        catch (Exception) { /* the same */ }
    }

    public async ValueTask DisposeAsync()
    {
        // ONCE. The quit runs it from the lifetime's ShutdownRequested and again from its Exit, whichever
        // the way out raised (TradeAgentApp), and a second pass over disposed objects would throw.
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        await StopTheAiForQuitAsync();
        if (_loop is not null)
        {
            await _loop.CancelAsync();
            // THE PASS IN FLIGHT FINISHES FIRST (U-linux-host): cancelled, the loop stops at its next await, but a pass
            // already writing — the owed daily reports, a reconcile — went on writing while the ledgers closed under it,
            // and on a stop that ends the process at once (systemd's) a report file half-written is one nothing
            // rewrites. Measured on the first start's stop on this Mac: a report written after the stop line. Five
            // seconds, because a pass is short and a quit is bounded.
            if (_background is not null)
            {
                try { await _background.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception) { /* over the bound or faulted: the quit goes on */ }
            }
            _loop.Dispose();
        }
        foreach (var held in _roleConversations.Values) held.Metering?.Dispose();
        _metering?.Dispose();
        // THE KEY IS CLEARED WHEN THE APP CLOSES, which is the other half of "held in memory only":
        // it is never written, so there is nothing to delete, and it does not outlive this process.
        HarnessKey.Clear();
        PerceptionKey.Clear();
        lock (_decisionModels)
        {
            foreach (var wire in _decisionModels.Values) wire.Dispose();
            _decisionModels.Clear();
        }
        _harness?.Dispose();
        // STOPPED WITH THE APP, AND BEFORE THE DATABASE CLOSES: a look in flight holds a write
        // transaction, and disposing the store underneath it is how a clean exit becomes a corrupt
        // row. This waits for the look it interrupted.
        if (Forward is not null) { await Forward.DisposeAsync(); Forward = null; }
        // THE TAPE THE SAME WAY: the collector first, which waits for the look in flight and its
        // transaction, then the tape's own connection.
        if (Tape is not null) { await Tape.DisposeAsync(); Tape = null; }
        if (Gdelt is not null) { await Gdelt.DisposeAsync(); Gdelt = null; }
        _tape?.Dispose();
        _tape = null;
        // THE STOP, WRITTEN DOWN (U-linux-host item 2): the counterpart of "TradeAgent started", the last line before
        // the ledgers close, so an orderly quit — the window's, the OS's or the system's SIGTERM — is told apart from
        // a death in the record itself. A crash writes nothing here, and that absence is the evidence.
        try { Gateway?.Log.Activity("TradeAgent stopped"); }
        catch (Exception) { /* the quit goes on whether or not it could be written down */ }
        if (_server is not null) await _server.DisposeAsync();
        if (Gateway is not null) await Gateway.DisposeAsync();
        _db?.Dispose();
        _lock?.Dispose();
    }
}

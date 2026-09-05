using TradeAgent.ConnectorSdk;
using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.Gateway;

/// <summary>
/// The privileged execution authority. Nothing above this class may talk to a broker.
///
/// Order of authority, highest first:
///   1. the human (kill switch, mode, live activation, risk limits)
///   2. provable state (unreconciled work pauses trading; untrusted health pauses trading)
///   3. the agent's intent
///
/// Every mutating request is written to the database BEFORE it is dispatched, keyed by a caller
/// supplied request id. That write is the duplicate-order defence: a repeated request id can never
/// produce a second dispatch, no matter how the caller retries.
/// </summary>
public sealed class TradingGateway : IAsyncDisposable
{
    readonly Database _db;
    readonly ExecutionRequestStore _requests;
    readonly CompositeRequestStore _composites;
    readonly LogStore _log;
    readonly MaterialStore _materials;
    readonly HealthRegistry _health;
    readonly GatewayOptions _opt;
    readonly SemaphoreSlim _dispatchGate = new(1, 1);
    readonly List<DateTimeOffset> _recentDispatches = [];
    IReadOnlyList<InstrumentInfo> _instrumentCache = [];

    public ITradingConnector Connector { get; }
    public TradeAgentSettings Settings { get; private set; }
    public HealthRegistry Health => _health;
    public ExecutionRequestStore Requests => _requests;
    public CompositeRequestStore Composites => _composites;
    public LogStore Log => _log;
    public MaterialStore Materials => _materials;

    /// <summary>
    /// Whether the app is in the middle of replacing itself. Set by the updater through AppHost; a
    /// bool behind a delegate, because the gateway must not know what an update is.
    ///
    /// Between the moment the owner confirms Install update and the moment Setup is running, this
    /// process is about to be closed and its files overwritten. An order dispatched into that window
    /// is the worst kind this product has: the wire is touched and the program that would have
    /// reconciled the answer is gone before the answer arrives. The pre-install check refuses to
    /// START an update while an order is unconfirmed; this is the other side of the same window,
    /// refusing to START AN ORDER while an update is going.
    ///
    /// It does NOT exempt the operator. Approving a parked order by hand while Setup is running is
    /// exactly the case being closed, and "the human meant it" does not make the answer reconcilable.
    /// Null means no updater is wired, which is every test that is not about this and is treated as
    /// "no install in progress" — the fail-closed reading belongs on the updater's side, where not
    /// knowing means not replacing the program, not on the side that would refuse all trading.
    /// </summary>
    public Func<bool>? InstallInProgress { get; set; }

    public event Action? StateChanged;

    /// <summary>The only clock this class reads, so a test can move it. See GatewayOptions.Clock.</summary>
    DateTimeOffset Now => _opt.Clock.GetUtcNow();

    /// <summary>
    /// THE PAUSE THAT DOES NOT DEPEND ON THE DATABASE. Every durable record of an unconfirmed
    /// outcome is a write, and a write can fail — a locked database, a full disk, a read-only file.
    /// When it does, the wire has still been touched, so the refusal has to exist somewhere the
    /// failure cannot reach. This is that somewhere: set in memory BEFORE the write is attempted,
    /// read by the authorization gate, and lifted only when something has actually settled the work
    /// (a reconcile pass that finished clean, or a person confirming the record).
    /// </summary>
    /// <remarks>
    /// A SET, keyed by request id, not one flag. One latch for the whole gateway meant that
    /// confirming ANY record — or one clean reconcile pass — lifted a pause that another, still
    /// unconfirmed outcome was holding. Each entry is lifted only by evidence about its own request.
    /// </remarks>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _unconfirmed = new();

    void LatchUnconfirmed(string requestId, string reason)
    {
        _unconfirmed[requestId] = reason;
        _health.Set(Components.ExecutionCapability, HealthState.PAUSED, reason);
    }

    /// <summary>Lifts the latch for ONE request. Nothing here may lift another request's.</summary>
    void ClearLatch(string requestId) => _unconfirmed.TryRemove(requestId, out _);

    // ---------------------------------------------------------------- who owns a dispatching row

    /// <summary>
    /// WHAT THIS PROCESS KNOWS ABOUT ITS OWN DISPATCHES, and it is the difference between a record
    /// that is mid-flight and one that is abandoned.
    ///
    /// A DISPATCHING row on disk cannot say which it is: it is written before the connector call and
    /// overwritten by the answer, so it looks identical whether a handler is inside the call right
    /// now or died in it last week. The reconciler used to guess from age alone, and at the shipped
    /// deadlines the guess was wrong for the whole 30..50 s window that finding 1 measured. This is
    /// the fact the guess was missing, and it is in memory on purpose: a lease that outlived the
    /// process holding it would be a lease nothing could ever release.
    ///
    /// Two questions are answered from it, and they are different:
    ///   - <c>Live</c>  — a handler is inside the connector call for this request right now, so the
    ///                    reconciler must not move the row (see <see cref="ReconcileAsync"/>).
    ///   - existence    — this process saw the dispatch END, so it knows the wire is no longer live
    ///                    for this record and absence may be counted from the dispatch itself. With
    ///                    no entry at all — a crash, a restart, another process — nothing here can
    ///                    say when the wire went quiet, and the bound is the only honest answer.
    /// </summary>
    sealed class DispatchSpan
    {
        public required DateTimeOffset Started;
        public volatile bool Live = true;
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, DispatchSpan> _dispatches = new();

    /// <summary>
    /// Claims a request for the duration of one connector call. Taken immediately before the call
    /// and released however the call ends, including a settle that threw.
    /// </summary>
    IDisposable HoldDispatch(string requestId)
    {
        _dispatches[requestId] = new DispatchSpan { Started = Now };
        return new DispatchHold(this, requestId);
    }

    sealed class DispatchHold(TradingGateway gateway, string requestId) : IDisposable
    {
        public void Dispose() => gateway.EndDispatch(requestId);
    }

    /// <summary>
    /// The dispatcher is done. The entry stays — its existence is what says this process watched the
    /// wire go quiet — but only while the record can still be asked about. Once the row is settled
    /// there is nothing left to reconcile and nothing left to remember, so the entry goes: this map
    /// is bounded by the open and unconfirmed records, not by every order the session ever placed.
    /// </summary>
    void EndDispatch(string requestId)
    {
        if (!_dispatches.TryGetValue(requestId, out var span)) return;
        span.Live = false;
        try
        {
            var row = _requests.Get(requestId);
            if (row is null || row.State is not (ExecutionState.DISPATCHING or ExecutionState.UNKNOWN
                                                 or ExecutionState.RECONCILING))
                _dispatches.TryRemove(requestId, out _);
        }
        catch (Exception)
        {
            // A store that will not answer keeps the entry. It costs a few bytes and it is the safe
            // direction: forgetting a dispatch makes the reconciler MORE conservative, not less.
        }
    }

    /// <summary>
    /// WHEN ABSENCE MAY START COUNTING FOR THIS RECORD — the later of the dispatch and the moment
    /// the dispatch could last have been in flight.
    ///
    /// "The broker has never heard of this order" is only evidence once the order can no longer be
    /// on its way there, and <see cref="GatewayOptions.AbsenceGrace"/> is the window after that in
    /// which a slow book is still allowed to catch up. Measured from the dispatch alone, the window
    /// had always already expired on any record the reconciler could see: a stranded record becomes
    /// visible at <see cref="DispatchStrandedAfter"/>, which is longer than the grace, so absence
    /// was conclusive on its first pass — that is the second half of finding 1.
    ///
    /// When this process watched the dispatch end, the wire went quiet then and the dispatch instant
    /// is the honest reference, which is what every settled-then-reconciled record uses. When it did
    /// not — a crash, a restart, another process over the same store, or a handler still inside the
    /// call — the bound is all there is.
    /// </summary>
    DateTimeOffset AbsenceCountsFrom(ExecutionRequest req)
    {
        var dispatched = req.DispatchedAt ?? req.CreatedAt;
        return _dispatches.TryGetValue(req.RequestId, out var span) && !span.Live
            ? dispatched
            : dispatched + DispatchStrandedAfter;
    }

    /// <summary>
    /// Lifts every latch whose request the STORE can now account for: a record that is settled and
    /// no longer flagged is positive, definite evidence about that request, which is exactly what a
    /// latch is waiting for. A record still DISPATCHING — the write that never landed — is not, and
    /// keeps its latch. Called after a reconcile pass, so the entries clear one at a time, on their
    /// own evidence, rather than being swept away together by an unrelated success.
    /// </summary>
    void ReleaseLatchesTheStoreCanVouchFor()
    {
        foreach (var id in _unconfirmed.Keys)
        {
            var row = _requests.Get(id);
            if (row is null || row.NeedsReconciliation) continue;
            if (row.State is ExecutionState.DISPATCHING or ExecutionState.UNKNOWN or ExecutionState.RECONCILING) continue;
            ClearLatch(id);
        }
    }

    public TradingGateway(Database db, ITradingConnector connector, HealthRegistry? health = null,
        GatewayOptions? options = null)
    {
        _db = db;
        Connector = connector;
        // _opt first: the store takes this gateway's clock, so that a duration with one end written
        // by the store and the other read here is measured on a single clock. See ExecutionRequestStore.
        _opt = options ?? new GatewayOptions();
        _requests = new ExecutionRequestStore(db, _opt.Clock);
        _composites = new CompositeRequestStore(db, _opt.Clock);
        _log = new LogStore(db);
        _materials = new MaterialStore(db);
        _health = health ?? new HealthRegistry();
        Settings = LoadSettings();

        _health.Changed += OnHealthChanged;
        Connector.ConnectionChanged += OnConnectionChanged;
        Connector.OrderChanged += OnOrderChanged;
        Connector.ExecutionReceived += OnExecutionReceived;

        RecoverStrandedDispatches();
    }

    /// <summary>
    /// A DISPATCHING RECORD IS BY DEFINITION ONE WHERE THE WIRE MAY HAVE BEEN TOUCHED. It is written
    /// before the connector is called and overwritten by whatever the connector answered, so a
    /// record still in DISPATCHING when a gateway is constructed over the store cannot be in flight:
    /// the process that was flying it is gone. Its outcome is unknown, and until 2026-09-02 nothing
    /// said so — every path that sets `needs_reconciliation` is inside a catch block, so a crash in
    /// that window (or an unhandled exception on a non-task thread, which nothing in this app
    /// catches) left the flag at 0, the gate passed, and the next start placed a second order on top
    /// of one that may be live at the broker.
    ///
    /// It runs in the CONSTRUCTOR rather than in a Start method a caller must remember, because
    /// there is no correct order in which a caller may read this store, authorize an order or
    /// dispatch one before the sweep has run — and there are three construction sites (app startup,
    /// the connector switch on the Settings page, and the dev host), which is precisely the shape of
    /// defect this sweep exists to close.
    ///
    /// DISPATCHING → UNKNOWN is a transition the table already allows; nothing here widens it.
    /// </summary>
    void RecoverStrandedDispatches()
    {
        var stranded = _requests.Dispatching();
        if (stranded.Count == 0) return;

        foreach (var req in stranded)
        {
            // In memory before the write, for the same reason RecordIndefinite does it: the pause
            // must not depend on the store being writable at the moment we discover the problem.
            LatchUnconfirmed(req.RequestId, "a request was still being sent when TradeAgent last stopped");
            try
            {
                _requests.Transition(req.RequestId, ExecutionState.DISPATCHING, ExecutionState.UNKNOWN,
                    needsReconciliation: true,
                    error: "TradeAgent stopped while this was being sent, so the platform's answer was never recorded");
                _log.Engineering("Gateway", "startup_sweep_unknown", "warn", requestId: req.RequestId,
                    metadataJson: Json.Write(new { intent = req.Intent.ToString(), instrument = req.Instrument }));
            }
            catch (Exception ex)
            {
                // NOT SWALLOWED. The table cannot refuse DISPATCHING → UNKNOWN, so this is either a
                // CAS loss to another writer or a store that would not take the write at all — and
                // the second one used to disappear silently, leaving the pause resting on a row that
                // was never marked. The latch above already holds it; this says so out loud, and
                // still tries to flag the row.
                _log.TryEngineering("Gateway", "startup_sweep_failed", "error", requestId: req.RequestId, ex: ex);
                try { _requests.MarkNeedsReconciliation(req.RequestId, ex.Message); }
                catch (Exception) { /* the latch is the guarantee; the row could not be touched */ }
            }
        }

        // Guarded for the same reason as everything else on this path: the sweep runs in a
        // constructor, and a store that would not take the sweep's writes must not stop the gateway
        // from being built. The latches above are already set, so the pause exists either way.
        try
        {
            _log.Activity($"{stranded.Count} order(s) were still being sent when TradeAgent last stopped. " +
                          "Trading is paused until you or the platform confirm what happened to them.", "warn");
        }
        catch (Exception) { /* the activity line is the nicety; the latch is the guarantee */ }
        _health.Set(Components.ExecutionCapability, HealthState.PAUSED, $"{stranded.Count} request(s) unconfirmed");
    }

    // Named rather than inline so DisposeAsync can detach them again. A gateway that is torn down
    // while still subscribed to a shared HealthRegistry keeps writing into the log after it stops
    // being the authority — two owners of one fact, which is the defect class this design exists to
    // avoid.
    // The write is guarded because SETTING health must never fail its caller. `health_event` is a
    // historical row; the state the screen and the gates read lives in the registry, in memory. This
    // handler is on the path of every _health.Set, including the one that pauses execution when an
    // outcome could not be written down — and a store that refused THAT write will refuse this one,
    // which is how a locked database once turned "pause trading" into an exception instead.
    void OnHealthChanged(ComponentHealth h)
    {
        try { _log.Health(h); }
        catch (Exception) { /* the in-memory registry already carries it; the row is a nicety */ }
        StateChanged?.Invoke();
    }
    // The detail is what makes a red row repairable: a version-mismatched ATAS bridge is refused
    // for good reasons and otherwise looks identical to no bridge at all.
    void OnConnectionChanged(HealthState s) =>
        _health.Set(Components.TradingConnection, s,
            s == HealthState.FAILED && Connector is IConnectorStatusDetail d ? d.StatusDetail ?? "" : "");
    void OnExecutionReceived(ExecutionInfo x) => _log.Activity($"Filled {x.Quantity} {x.Symbol} at {x.Price}");

    // ---------------------------------------------------------------- settings

    TradeAgentSettings LoadSettings()
    {
        var json = _db.GetKv("settings");
        if (string.IsNullOrWhiteSpace(json)) return new TradeAgentSettings();
        TradeAgentSettings settings;
        try { settings = Json.Read<TradeAgentSettings>(json) ?? new TradeAgentSettings(); }
        catch (Exception) { return new TradeAgentSettings(); }

        // A MODE THIS BUILD DOES NOT HAVE ALLOWS NOTHING, AND THE OWNER IS TOLD SO.
        //
        // TradeAgentSettings.ModeIsRecognised is what makes the refusal happen — the gates ask it,
        // and nothing this method does is load-bearing for safety. What is load-bearing for the
        // PERSON is this line: the failure is a setting they believe they set, so a refusal that
        // only an agent ever reads over a pipe is not news anyone at the keyboard receives.
        //
        // The value is NOT rewritten to OBSERVE. Substituting a control the owner never chose is
        // the shape of REVIEW 2026-09-05 finding 5, and rewriting it here would also destroy the
        // evidence: a mode a NEWER build wrote is exactly what a rollback should hand back intact
        // when the owner upgrades again.
        if (!settings.ModeIsRecognised)
        {
            try
            {
                _log.Activity($"The trading mode saved in your settings ({(int)settings.Mode}) is not one this version " +
                              "of TradeAgent knows, so nothing will be sent to your broker. Choose a mode on the " +
                              "Settings page.", "warn");
                _log.Engineering("Gateway", "unrecognised_mode", "error",
                    metadataJson: Json.Write(new { mode = (int)settings.Mode }));
            }
            catch (Exception) { /* the gate is the guarantee; the sentence about it is the nicety */ }
            _health.Set(Components.ExecutionCapability, HealthState.PAUSED, "the saved trading mode is not one this version knows");
        }
        return settings;
    }

    void SaveSettings()
    {
        _db.SetKv("settings", Json.Write(Settings));
        StateChanged?.Invoke();
    }

    public void Update(Action<TradeAgentSettings> mutate) { mutate(Settings); SaveSettings(); }

    // ---------------------------------------------------------------- operator authority

    public void StopAiTrading(string reason)
    {
        Settings.AiTradingStopped = true;
        SaveSettings();
        _log.Activity($"AI trading stopped ({reason})", "warn");
        // Deliberately NOT reflected into HealthRegistry: permission is a setting, health is about
        // whether the machinery works. Two owners for one fact left trading stuck off after re-enabling.
    }

    public void EnableAiTrading()
    {
        Settings.AiTradingStopped = false;
        SaveSettings();
        _log.Activity("AI trading enabled");
    }

    public void SetMode(TradingMode mode)
    {
        // A C# enum is a number with names on some of it, and `(TradingMode)999` is a legal value of
        // the parameter type. Nothing in the app passes one — the Settings page offers the four —
        // but this is the only writer of the setting the whole gateway reads, and a value that gets
        // in HERE is one no later reader can tell from a mode the owner chose.
        if (!Enum.IsDefined(mode))
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST, $"{(int)mode} is not a trading mode");

        Settings.Mode = mode;
        if (!Settings.ModeIsLive) Settings.LiveActivated = false; // leaving live re-arms the safety
        SaveSettings();
        _log.Activity($"Trading mode set to {mode}");
    }

    /// <summary>Real money requires an explicit act. The existence of a live account is not consent.</summary>
    public void ActivateLive(bool on)
    {
        Settings.LiveActivated = on;
        SaveSettings();
        _log.Activity(on ? "Real-money trading switched ON by the user" : "Real-money trading switched off", on ? "warn" : "info");
    }

    // ---------------------------------------------------------------- reads

    public async Task<GatewayStatus> StatusAsync(CancellationToken ct = default)
    {
        var available = TryAuthorizeExecution(AgentContext.Operator, out var blocked);
        AccountInfo? acct = null;
        try { acct = Settings.SelectedAccountId is { } id ? await Connector.GetAccountAsync(id, ct) : (await Connector.GetAccountsAsync(ct)).FirstOrDefault(); }
        catch (Exception) { /* status must render even with the wire down */ }

        return new GatewayStatus(
            Versions.ProtocolVersion.ToString(), Versions.App, Settings.Mode, Settings.AiTradingStopped,
            Settings.LiveActivated, available, blocked, Connector.Id, Connector.DisplayName,
            Connector.Capabilities.IsPaper, acct?.Id ?? Settings.SelectedAccountId, _health.Snapshot(),
            _requests.Open().Count, Unreconciled().Count, Settings.Risk);
    }

    public Task<IReadOnlyList<AccountInfo>> AccountsAsync(CancellationToken ct = default) => Connector.GetAccountsAsync(ct);

    public async Task<AccountInfo?> AccountAsync(CancellationToken ct = default) =>
        Settings.SelectedAccountId is { } id
            ? await Connector.GetAccountAsync(id, ct)
            : (await Connector.GetAccountsAsync(ct)).FirstOrDefault();

    public async Task<IReadOnlyList<InstrumentInfo>> InstrumentsAsync(CancellationToken ct = default)
    {
        _instrumentCache = await Connector.GetInstrumentsAsync(ct);
        return _instrumentCache;
    }

    public Task<QuoteInfo?> QuoteAsync(string symbol, CancellationToken ct = default) => Connector.GetQuoteAsync(symbol, ct);

    public async Task<IReadOnlyList<PositionInfo>> PositionsAsync(CancellationToken ct = default) =>
        await Connector.GetPositionsAsync(await RequireAccountId(ct), ct);

    public async Task<IReadOnlyList<OrderInfo>> OrdersAsync(bool includeInactive = false, CancellationToken ct = default) =>
        await Connector.GetOrdersAsync(await RequireAccountId(ct), includeInactive, null, ct);

    public async Task<IReadOnlyList<ExecutionInfo>> ExecutionsAsync(CancellationToken ct = default) =>
        await Connector.GetExecutionsAsync(await RequireAccountId(ct), null, ct);

    public ExecutionRequest? GetRequest(string requestId) => _requests.Get(requestId);

    /// <summary>
    /// How long a record may stay in DISPATCHING before this gateway counts it as unconfirmed work
    /// and refuses to trade over it, WITHOUT waiting for a restart to notice.
    ///
    /// DERIVED FROM THE LIVE CONNECTOR, never written down — the same rule, and for the same reason,
    /// as <c>GatewayPipeServer.HandlerDrainTimeout</c>. It was the constant 30 s, justified as "the
    /// connector's own 10 s RPC deadline plus 20 s of slack", while one ordinary order path through
    /// <c>AtasConnector</c> is 50 s: the send gate (10 s), the whole frame (30 s) and the reply
    /// (10 s), which is exactly what <c>WorstCaseOrderPath</c> adds up and what the pipe server's
    /// drain has been derived from since the 2026-09-03 correction. The stranded bound never got
    /// that correction, so a placement legitimately in flight for 30..50 s was "stranded", was
    /// already past <see cref="GatewayOptions.AbsenceGrace"/> the moment the reconciler could see
    /// it, and was settled CANCELLED / "never reached the broker" / unflagged with trading resumed —
    /// and then filled (REVIEW 2026-09-05 finding 1, executed as probe P6b).
    ///
    ///     bound = Connector.WorstCaseOperationPath + GatewayOptions.DispatchSettleSlack
    ///
    /// At shipped ATAS values that is 50 + 20 = 70 s. A connector constructed with different
    /// deadlines moves it, which is the whole point: <see cref="Connector"/> is set once per gateway
    /// and a connector switch builds a new gateway over the same store, so the bound is re-derived
    /// there rather than surviving the switch as somebody else's number.
    ///
    /// The second term is not a second deadline. The connector's own worst case bounds the CALL; a
    /// dispatch also has to be scheduled again and write the outcome down after the call returns,
    /// and no connector deadline describes any of that.
    /// </summary>
    public TimeSpan DispatchStrandedAfter =>
        _opt.DispatchStrandedAfter is { } explicitly && explicitly > DerivedDispatchStrandedAfter
            ? explicitly
            : DerivedDispatchStrandedAfter;

    TimeSpan DerivedDispatchStrandedAfter => Connector.WorstCaseOperationPath + _opt.DispatchSettleSlack;

    /// <summary>
    /// Unconfirmed work as this gateway counts it: the flagged records, PLUS any record still in
    /// DISPATCHING longer than a dispatch can legitimately take
    /// (<see cref="DispatchStrandedAfter"/>). Everything inside this class that asks
    /// "is there unconfirmed work" asks this, so the refusal, the status field, the health row and
    /// the reconciler cannot drift into three different answers.
    ///
    /// Public because everything that reports or acts on unconfirmed work asks it: the background
    /// loop, the doctor, the unconfirmed card, the dev host. Nothing in `src` reads the raw
    /// <c>needs_reconciliation</c> flag any more — checked by grep, 2026-09-03 — because the flag and
    /// this are different questions, and answering the wrong one is how a machine that refuses to
    /// trade told its owner there was nothing outstanding.
    /// </summary>
    public List<ExecutionRequest> Unreconciled()
    {
        var rows = _requests.NeedingReconciliation(Now - DispatchStrandedAfter);
        if (_unconfirmed.IsEmpty) return rows;

        // A latched id whose row the store never took is still unconfirmed work, and every surface
        // that lists the blocking records has to see it — otherwise the card is empty while the gate
        // refuses, which is the disagreement this method exists to end.
        var seen = rows.Select(r => r.RequestId).ToHashSet();
        foreach (var id in _unconfirmed.Keys)
            if (seen.Add(id) && _requests.Get(id) is { } row) rows.Add(row);
        return rows;
    }

    /// <summary>
    /// Is there anything this gateway will not trade over — including an outcome it could not write
    /// down. The screen and the background loop ask this rather than counting rows, so neither can
    /// disagree with the gate.
    /// </summary>
    public bool HasUnconfirmedWork() => !_unconfirmed.IsEmpty || Unreconciled().Count > 0;

    /// <summary>
    /// Every record the wire may still be holding — the question the UPDATER asks, which is a
    /// strictly wider one than <see cref="Unreconciled"/>, and deliberately so.
    ///
    /// <b>Why it is wider.</b> <see cref="Unreconciled"/> answers "may I start another order?", and a
    /// placement that has been on the wire for two seconds is a perfectly ordinary thing to trade
    /// around: this process is inside the call, it will get the answer, and it will write it down.
    /// Replacing the program is the one act that makes that false. Kill this process and a DISPATCHING
    /// row of any age becomes an order handed to a broker whose answer nobody is left to receive. So
    /// the update stop counts DISPATCHING at every age, not only past
    /// <see cref="DispatchStrandedAfter"/>, and it counts UNKNOWN and RECONCILING for the same reason:
    /// those are records mid-question, and the questioner is the process about to be overwritten.
    ///
    /// It is a SUPERSET of <see cref="Unreconciled"/>, never a second opinion about it — the flagged
    /// rows, the stranded dispatches and the in-memory latch are all in here too, so this can never
    /// come back smaller than the number the gate, the Dashboard, the doctor and the status field are
    /// reporting. That direction is what matters: a machine that refuses to trade must never tell its
    /// owner it is safe to replace.
    ///
    /// The flag appears in the SQL here as ONE arm of the union rather than as the question, which is
    /// the distinction the note on <see cref="Unreconciled"/> is drawing; nothing outside this class
    /// reads the raw flag as an answer.
    /// </summary>
    public List<ExecutionRequest> WireTouched()
    {
        var rows = _requests.Query(
            "needs_reconciliation=1 OR execution_state IN ('DISPATCHING','UNKNOWN','RECONCILING')");
        if (_unconfirmed.IsEmpty) return rows;

        // An outcome that arrived and could not be written down is the one kind of unconfirmed work
        // no query of the store can find, because by definition the store did not take it.
        var seen = rows.Select(r => r.RequestId).ToHashSet();
        foreach (var id in _unconfirmed.Keys)
            if (seen.Add(id) && _requests.Get(id) is { } row) rows.Add(row);
        return rows;
    }

    async Task<string> RequireAccountId(CancellationToken ct)
    {
        if (Settings.SelectedAccountId is { } id) return id;
        var first = (await Connector.GetAccountsAsync(ct)).FirstOrDefault()
            ?? throw new GatewayDeniedException(ErrorCode.ACCOUNT_NOT_FOUND, "no account is available from the connector");
        return first.Id;
    }

    // ---------------------------------------------------------------- authorization

    /// <summary>
    /// Every gate that stands between an agent and a real order. Deliberately dull and ordered:
    /// cheap human switches first, then provable-state checks, then risk arithmetic.
    /// </summary>
    public bool TryAuthorizeExecution(AgentContext ctx, out string? reason) =>
        TryAuthorizeExecution(ctx, out reason, out _);

    /// <summary>
    /// Reports which gate refused, not just that something did. The caller propagates that code
    /// verbatim, so an agent reading an error learns the real reason instead of a guess.
    /// </summary>
    public bool TryAuthorizeExecution(AgentContext ctx, out string? reason, out ErrorCode? code)
    {
        reason = null;
        code = null;

        // First, above the mode and above the kill switch, because it outranks intent entirely:
        // this program is about to stop existing. A hook that throws is read as "installing" —
        // the one direction that cannot dispatch an order nobody will be left to reconcile.
        var installing = false;
        if (InstallInProgress is { } ask)
        {
            try { installing = ask(); }
            catch (Exception) { installing = true; }
        }
        if (installing)
        {
            (reason, code) = ("TradeAgent is installing a new version of itself and is about to close",
                ErrorCode.UPDATE_INSTALL_IN_PROGRESS);
            return false;
        }

        if (!Settings.ModeAllowsExecution)
        {
            (reason, code) = (Settings.ModeIsRecognised
                    ? $"mode is {Settings.Mode}"
                    : $"the saved trading mode ({(int)Settings.Mode}) is not one this version of TradeAgent knows",
                ErrorCode.MODE_FORBIDS_EXECUTION);
            return false;
        }
        if (Settings.AiTradingStopped && !ctx.IsOperator)
        {
            (reason, code) = ("AI trading is stopped", ErrorCode.AI_TRADING_STOPPED);
            return false;
        }

        if (Settings.ModeIsLive)
        {
            if (!Settings.LiveActivated)
            {
                (reason, code) = ("real-money trading is not switched on", ErrorCode.LIVE_NOT_ACTIVATED);
                return false;
            }
            if (Settings.Mode == TradingMode.LIVE_AUTONOMOUS && !Connector.Capabilities.ReconciliationProvable)
            {
                (reason, code) = ($"{Connector.DisplayName} cannot prove order state after a disconnect, so autonomous live trading is refused",
                    ErrorCode.AUTONOMY_REQUIRES_PROVABLE_STATE);
                return false;
            }
        }

        // AN ACCOUNT THAT WAS NEVER CHOSEN IS NOT A DEFAULT, IT IS A GUESS.
        //
        // AccountAsync falls back to whichever account the platform happens to list first, so that a
        // status screen can render before anything is configured. That is fine for rendering a
        // balance and unacceptable for placing an order: on a platform carrying both a practice and
        // a real-money account, "first in the list" is what decides whose money it is, and nobody
        // asked the owner. PlaceAsync goes through AccountAsync, so without this the fallback was
        // reaching the broker.
        //
        // It became reachable the day the platform could be changed after setup: switching clears
        // the chosen account, because an id issued by one platform does not exist on the other.
        // Onboarding cannot finish without ACCOUNT_SELECTED, so nothing else opens this window —
        // which is exactly why it went unnoticed until there was a Settings page.
        if (Settings.SelectedAccountId is null)
        {
            (reason, code) = ("no account has been chosen — choose one on the Settings page",
                ErrorCode.ACCOUNT_NOT_FOUND);
            return false;
        }

        // Unconfirmed work outranks a healthy-looking connection: check it before health, so the
        // refusal says "an earlier order is unconfirmed" rather than something vaguer.
        var unreconciled = Unreconciled();
        if (unreconciled.Count > 0)
        {
            (reason, code) = ($"{unreconciled.Count} earlier request(s) are unconfirmed", ErrorCode.TRADING_PAUSED_UNRECONCILED);
            return false;
        }
        // The in-memory latch is checked AFTER the store, so that when both agree the message is the
        // one that can count. It is checked at all because the store may not have been writable when
        // the outcome had to be recorded.
        if (!_unconfirmed.IsEmpty)
        {
            (reason, code) = (_unconfirmed.Values.First(), ErrorCode.TRADING_PAUSED_UNRECONCILED);
            return false;
        }

        if (!_health.ExecutionTrustable(out var hr))
        {
            (reason, code) = (hr, ErrorCode.TRADING_PERMISSION_UNAVAILABLE);
            return false;
        }
        return true;
    }

    void AuthorizeOrThrow(AgentContext ctx)
    {
        if (TryAuthorizeExecution(ctx, out var reason, out var code)) return;
        throw new GatewayDeniedException(code ?? ErrorCode.TRADING_PERMISSION_UNAVAILABLE, reason ?? "execution is not available");
    }

    async Task RiskCheckOrThrow(PlaceIntent intent, AccountInfo account, CancellationToken ct)
    {
        var r = Settings.Risk;

        if (!r.InstrumentAllowed(intent.Symbol))
            throw new GatewayDeniedException(ErrorCode.RISK_LIMIT_EXCEEDED, $"{intent.Symbol} is not on the allowed instrument list");

        if (intent.Quantity <= 0)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST, "quantity must be greater than zero");

        if (intent.Quantity > r.MaxOrderQuantity)
            throw new GatewayDeniedException(ErrorCode.RISK_LIMIT_EXCEEDED,
                $"quantity {intent.Quantity} exceeds the limit of {r.MaxOrderQuantity}");

        // Paper mode must never reach a real-money account, whatever the agent asked for.
        if (Settings.Mode == TradingMode.PAPER && !(account.IsSimulated || Connector.Capabilities.IsPaper))
            throw new GatewayDeniedException(ErrorCode.MODE_ACCOUNT_MISMATCH,
                $"account {account.Id} is not a simulation account and the mode is PAPER");

        // The rate limit's EARLY refusal, so a request over the limit is turned away before it costs
        // a position read and a quote. It is advisory: what actually bounds the minute is the
        // reservation taken at the wire (see ReserveDispatchOrThrow), because everything below this
        // line is an awaited read and a check whose count is read here and spent there admits every
        // caller that passed while the others were reading.
        RateLimitOrThrow(r.MaxOrdersPerMinute);

        var positions = await Connector.GetPositionsAsync(account.Id, ct);
        var open = positions.Count(p => p.Quantity != 0);
        var wouldOpenNew = !positions.Any(p => p.Symbol == intent.Symbol && p.Quantity != 0);
        if (wouldOpenNew && open >= r.MaxOpenPositions)
            throw new GatewayDeniedException(ErrorCode.RISK_LIMIT_EXCEEDED,
                $"already holding {open} positions and the limit is {r.MaxOpenPositions}");

        // A price we trust is required for EVERY order, whether or not a value cap is set: an agent
        // sizing a market order from a stale quote is the failure this prevents.
        var quote = await Connector.GetQuoteAsync(intent.Symbol, ct);
        var reference = intent.LimitPrice ?? intent.StopPrice
                        ?? (quote is not null && !quote.IsStale(_opt.MaxQuoteAge) ? quote.Last ?? quote.Ask ?? quote.Bid : null);
        if (reference is null)
            throw new GatewayDeniedException(ErrorCode.MARKET_DATA_UNAVAILABLE,
                $"no price newer than {_opt.MaxQuoteAge.TotalSeconds:0}s for {intent.Symbol}, so the order value cannot be checked");

        if (_instrumentCache.Count == 0) { try { await InstrumentsAsync(ct); } catch (Exception) { } }
        var contract = _instrumentCache.FirstOrDefault(i => i.Symbol == intent.Symbol)?.ContractSize ?? 1m;
        if (r.MaxNotionalPerOrder > 0)
        {
            var notional = intent.Quantity * reference.Value * contract;
            if (notional > r.MaxNotionalPerOrder)
                throw new GatewayDeniedException(ErrorCode.RISK_LIMIT_EXCEEDED,
                    $"order value {notional:N0} exceeds the limit of {r.MaxNotionalPerOrder:N0}");
        }
    }

    void RateLimitOrThrow(int limit)
    {
        lock (_recentDispatches)
        {
            _recentDispatches.RemoveAll(d => d < Now - TimeSpan.FromMinutes(1));
            if (_recentDispatches.Count >= limit)
                throw new GatewayDeniedException(ErrorCode.RISK_LIMIT_EXCEEDED, $"{limit} orders per minute is the limit");
        }
    }

    /// <summary>
    /// A PLACE IN THE MINUTE'S BUDGET, TAKEN AT THE WIRE AND GIVEN BACK IF NOTHING IS SENT.
    ///
    /// The limit used to be a COUNT READ in <see cref="RiskCheckOrThrow"/> and an unrelated add in
    /// the dispatcher, with two awaited connector reads in between. N callers arriving together all
    /// read the same count, all passed it, and all then added — so `MaxOrdersPerMinute = 1` admitted
    /// as many orders as there were callers (REVIEW 2026-09-05, Codex F4). Check-and-take is now ONE
    /// step under ONE lock, which is the difference between a limit and a hint.
    ///
    /// It is taken BEFORE the write-ahead and committed at the wire, so a dispatch refused in
    /// between — by the authorization re-check, or by a store that will not take the record — gives
    /// its place back rather than spending a minute's worth of budget on an order nobody placed.
    /// </summary>
    DispatchSlot ReserveDispatchOrThrow()
    {
        var limit = Settings.Risk.MaxOrdersPerMinute;
        lock (_recentDispatches)
        {
            var at = Now;
            _recentDispatches.RemoveAll(d => d < at - TimeSpan.FromMinutes(1));
            if (_recentDispatches.Count >= limit)
                throw new GatewayDeniedException(ErrorCode.RISK_LIMIT_EXCEEDED, $"{limit} orders per minute is the limit");
            _recentDispatches.Add(at);
            return new DispatchSlot(this, at);
        }
    }

    /// <summary>One taken place in the minute's budget. Disposing it without <see cref="Commit"/> returns it.</summary>
    sealed class DispatchSlot(TradingGateway gw, DateTimeOffset at) : IDisposable
    {
        bool _spent;

        /// <summary>The wire is about to be touched: the place is spent whatever happens next.</summary>
        public void Commit() => _spent = true;

        public void Dispose()
        {
            if (_spent) return;
            lock (gw._recentDispatches) gw._recentDispatches.Remove(at);
        }
    }

    /// <summary>
    /// EVERY GATE, AGAIN, AT THE MOMENT OF DISPATCH — after the awaited reads and immediately before
    /// the wire.
    ///
    /// <see cref="AuthorizeOrThrow"/> at the top of a mutating method is a verdict about a moment
    /// that has passed by the time anything is sent: <c>PlaceAsync</c> authorized once and then made
    /// four connector reads, so Stop AI trading pressed inside that window did not stop the order it
    /// was pressed to stop — measured, at shipped ATAS deadlines a 200-second window (REVIEW
    /// 2026-09-05 finding 6, probe P3; Codex F4). The same window swallows switching real-money
    /// trading back off, and an install that started while the reads were in flight.
    ///
    /// THE MODE IS CHECKED AGAINST THE RECORD RATHER THAN AGAINST A LIST. Re-running the authorize
    /// alone would miss the direction that matters most: a placement authorized in PAPER, with the
    /// mode moved to LIVE_CONFIRM while it read, is a record built as CREATED — already past the
    /// question of whether a person should see it — and it would dispatch unapproved. A record
    /// carries the mode it was decided under, and only that mode may send it.
    /// </summary>
    void ReauthorizeAtDispatchOrThrow(AgentContext ctx, ExecutionRequest stored)
    {
        AuthorizeOrThrow(ctx);
        if (Settings.Mode != stored.Mode)
            throw new GatewayDeniedException(ErrorCode.MODE_FORBIDS_EXECUTION,
                $"mode is now {Settings.Mode}; this request was authorized under {stored.Mode} and is not sent");
    }

    // ---------------------------------------------------------------- mutations

    public async Task<ExecutionRequest> PlaceAsync(AgentContext ctx, string requestId, PlaceIntent intent, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST, "a request id is required");

        // Idempotency first. A repeated request id exercises no new authority, so it must not be
        // charged against the rate limit or re-checked against limits that may have changed since.
        if (_opt.IdempotencyEnabled && _requests.Get(requestId) is { } replay)
        {
            _log.Engineering("Gateway", "idempotent_replay", requestId: requestId);
            if (replay.State == ExecutionState.AWAITING_APPROVAL)
                throw new GatewayDeniedException(ErrorCode.APPROVAL_REQUIRED, $"request {requestId} is still waiting for your approval");
            return replay;
        }

        AuthorizeOrThrow(ctx);
        var account = await AccountAsync(ct) ?? throw new GatewayDeniedException(ErrorCode.ACCOUNT_NOT_FOUND, "no account");
        await RiskCheckOrThrow(intent, account, ct);

        var record = new ExecutionRequest
        {
            RequestId = requestId,
            AgentSessionId = ctx.SessionId,
            ConnectorId = Connector.Id,
            AccountId = account.Id,
            Instrument = intent.Symbol,
            Intent = RequestIntent.PLACE,
            ParametersJson = Json.Write(intent),
            ClientOrderId = ClientOrderIdFor(requestId),
            CreatedAt = Now,
            State = Settings.Mode == TradingMode.LIVE_CONFIRM && !ctx.IsOperator
                ? ExecutionState.AWAITING_APPROVAL : ExecutionState.CREATED,
            Mode = Settings.Mode
        };

        await _dispatchGate.WaitAsync(ct);
        try
        {
            var (created, stored) = _requests.TryCreate(record);

            if (!created && _opt.IdempotencyEnabled)
            {
                // Reached only by a genuine race: two callers passed the pre-check together, and the
                // unique constraint picked a winner. The loser dispatches nothing.
                _log.Engineering("Gateway", "idempotent_race_loser", requestId: requestId);
                return stored;
            }

            if (stored.State == ExecutionState.AWAITING_APPROVAL)
            {
                _log.Activity($"AI is asking permission to {intent.Side} {intent.Quantity} {intent.Symbol}");
                throw new GatewayDeniedException(ErrorCode.APPROVAL_REQUIRED, $"request {requestId} is waiting for your approval");
            }

            return await DispatchPlaceAsync(ctx, stored, intent, ct);
        }
        finally { _dispatchGate.Release(); }
    }

    /// <summary>Stable, derived from the request id so the broker's copy can be found again after a disconnect.</summary>
    public static string ClientOrderIdFor(string requestId) => $"TA-{requestId}";

    /// <summary>
    /// Records a definite dispatch outcome. If something else already settled the record (a stream
    /// event that arrived on another thread), the stored state wins and we simply report it.
    /// </summary>
    ExecutionRequest Settle(string requestId, ExecutionState to, string? connectorOrderId = null,
        decimal? filled = null, string? error = null)
    {
        try
        {
            return _requests.Transition(requestId, ExecutionState.DISPATCHING, to,
                connectorOrderId: connectorOrderId, filled: filled, error: error);
        }
        catch (TradeAgentException ex) when (ex.Code == ErrorCode.ILLEGAL_STATE_TRANSITION)
        {
            var actual = _requests.Get(requestId)!;

            // TWO DIFFERENT FAILURES ARRIVE HERE AND THEY ARE NOT THE SAME NEWS.
            //
            //   OrderStateMachine.Require  — the TABLE forbids from -> to. A defect in this code.
            //   ExecutionRequestStore CAS  — the record was not in DISPATCHING. A genuine race, and
            //                                the only thing this catch was ever written for.
            //
            // They are distinguishable without parsing a message: Require runs before the UPDATE, so
            // a table refusal leaves the record exactly where we left it. If it is still DISPATCHING,
            // nobody raced us, and filing that as `already_settled` is how a table gap stayed hidden
            // long enough to strand every cancel this gateway ever made.
            //
            // Loud, but NOT thrown. This runs on a write path that has already reached the broker;
            // turning a bookkeeping failure into a thrown error would report failure for an operation
            // that succeeded, which is the wrong direction to fail.
            if (actual.State == ExecutionState.DISPATCHING)
            {
                _log.Engineering("Gateway", "illegal_settle", "error", requestId: requestId,
                    metadataJson: Json.Write(new { intended = to.ToString(), from = actual.State.ToString() }));
                return actual;
            }

            // A DEFINITE ANSWER FROM THE BROKER OUTRANKS A ROW SOMEBODY ELSE MOVED OUT OF DISPATCHING.
            //
            // `already_settled` is the right word for a race with the EVENT STREAM, which settles a
            // record from the same broker and to the same effect. It is the wrong word — and it was
            // the whole of REVIEW 2026-09-05 UNVERIFIED 4 — for a race with the RECONCILER, which
            // moved the row to UNKNOWN and on to RECONCILING precisely because nobody had written an
            // answer down yet. What arrives here is that answer: the broker's own word about this
            // very request, carried by the handler that asked the question. Filing it and returning
            // a row that says the opposite is how a FILLED order came to be recorded CANCELLED.
            //
            // UNKNOWN and RECONCILING are the only two states this may overrule, and both are states
            // whose whole meaning is "we do not know". A TERMINAL row is left exactly as it is: the
            // state table refuses to leave one, and something with an answer of its own already did.
            if (actual.State is ExecutionState.UNKNOWN or ExecutionState.RECONCILING && IsDefinite(to)
                && LateDefiniteSettle(requestId, actual.State, to, connectorOrderId, filled, error) is { } won)
                return won;

            _log.Engineering("Gateway", "already_settled", requestId: requestId,
                metadataJson: Json.Write(new { intended = to.ToString(), actual = actual.State.ToString() }));
            return actual;
        }
        catch (Exception persist)
        {
            // A DEFINITE ANSWER THAT COULD NOT BE WRITTEN DOWN IS STILL AN UNCONFIRMED OUTCOME, and
            // round 2 latched only the indefinite path. `RecordIndefinite` pauses BEFORE it writes,
            // precisely because a write can fail; every caller of THIS method has also already
            // touched the wire, and here the write is the one thing that failed. Without the latch
            // the exception left the method with nothing paused and the row still DISPATCHING —
            // which for the whole DispatchStrandedAfter bound is an ordinary order in flight, so the
            // next order went out over an outcome that exists nowhere on disk. The wire having been
            // touched is the whole test for whether a latch is owed; what the broker answered is not
            // part of it.
            LatchUnconfirmed(requestId, $"an order outcome could not be written down ({persist.Message})");
            StateChanged?.Invoke();
            FileAfterTheStoreRefused("settle_failed", requestId, persist,
                new { intended = to.ToString(), error });

            // Thrown, unlike the race above: there is no record to hand back — the write that would
            // have made it is what failed — and a caller given a stale row would read it as the
            // outcome. The pause stands whether or not anyone catches this.
            throw new TradeAgentException(ErrorCode.STATE_DATABASE_CORRUPT,
                $"the outcome of {requestId} could not be written down ({persist.Message}); trading is paused");
        }
    }

    /// <summary>
    /// Lands a dispatch's definite answer on a row that had already been moved to UNKNOWN or
    /// RECONCILING by somebody else — the reconciler in this process before the lease existed, or
    /// one in another process over the same store, which is what the app and `GatewayHost` are.
    ///
    /// It walks the record the same way the reconciler would (UNKNOWN leaves only through
    /// RECONCILING, and this does not widen that), clears the flag and marks the record reconciled,
    /// because it IS reconciled: the answer came from the broker, about this request, by way of the
    /// call that asked. Null means the row moved again underneath us, and the caller then files
    /// `already_settled` as before — the row is being written by something with its own evidence and
    /// the state table is the arbiter, not this method.
    /// </summary>
    ExecutionRequest? LateDefiniteSettle(string requestId, ExecutionState from, ExecutionState to,
        string? connectorOrderId, decimal? filled, string? error)
    {
        try
        {
            if (from == ExecutionState.UNKNOWN)
                _requests.Transition(requestId, ExecutionState.UNKNOWN, ExecutionState.RECONCILING);

            var settled = _requests.Transition(requestId, ExecutionState.RECONCILING, to,
                connectorOrderId: connectorOrderId, filled: filled, error: error,
                needsReconciliation: false, markReconciled: true);

            ClearLatch(requestId);
            _log.Engineering("Gateway", "late_definite_settle", "warn", requestId: requestId,
                metadataJson: Json.Write(new { from = from.ToString(), state = to.ToString() }));
            return settled;
        }
        catch (TradeAgentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Files one engineering line off this thread, retrying while whatever held the store lets go.
    /// Only for use immediately after the store refused a write: it will refuse this line too, for
    /// as long as its own timeout, and an order path must not wait out a second one to file a log.
    /// A handful of attempts, not a queue and not a guarantee — the in-memory latch is the
    /// guarantee; this exists so an engineer can find out afterwards WHY trading paused with
    /// nothing in the ledger to point at.
    /// </summary>
    void FileAfterTheStoreRefused(string evt, string requestId, Exception cause, object metadata)
    {
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 6; attempt++)
            {
                // Wait before the FIRST attempt too: the store refused a write a moment ago, and
                // retrying into the same locked file only burns another timeout.
                await Task.Delay(250);
                try
                {
                    _log.Engineering("Gateway", evt, "error", requestId: requestId, ex: cause,
                        metadataJson: Json.Write(metadata));
                    return;
                }
                catch (Exception) { /* try again below */ }
            }
        });
    }

    /// <summary>
    /// Records an INDEFINITE dispatch outcome. If the record moved on underneath us we do not
    /// overwrite it, but we still flag it for reconciliation — an outcome we could not confirm is
    /// not an outcome we trust, whichever path wrote it.
    /// </summary>
    ExecutionRequest SettleUnknown(string requestId, string error, string? connectorOrderId = null)
    {
        try
        {
            // The broker's own reference is worth keeping even when nothing else is known: it is what
            // the reconciler matches on and what the unconfirmed card shows the person who has to go
            // and look in ATAS.
            return _requests.Transition(requestId, ExecutionState.DISPATCHING, ExecutionState.UNKNOWN,
                connectorOrderId: connectorOrderId, needsReconciliation: true, error: error);
        }
        catch (TradeAgentException ex) when (ex.Code == ErrorCode.ILLEGAL_STATE_TRANSITION)
        {
            return _requests.MarkNeedsReconciliation(requestId, error);
        }
    }

    /// <summary>
    /// THE ONE FAILURE A CONNECTOR CAN PROVE, AND THE ONLY ONE THAT MAY SETTLE WITHOUT A PAUSE.
    ///
    /// Every ambiguous connector failure is UNKNOWN, and that is right from up here: a refusal taken
    /// before the send gate and a half-written frame arrive as the same exception. But they are not
    /// the same fact, and since round 10 the connector says which — <see cref="TransportOutcome"/> on
    /// the work's own <see cref="TransportRecord"/>. <c>NothingWritten</c> is a PROOF that no byte of
    /// this mutation left the process, and <c>docs/CONTRACTS.md</c> already names it the one report
    /// allowed to overrule a record.
    ///
    /// WHAT IT COSTS TO IGNORE IT, measured through the real pipe (verifier round-9 F-1): a sweep leg
    /// the connector refused before the wire was reported <c>not-sent</c> — the one word in the set
    /// that is an ASSURANCE — while the row behind it was UNKNOWN and flagged, and the flag refuses
    /// ALL further execution with TRADING_PAUSED_UNRECONCILED, including the retry the message
    /// itself advises. The word and the record were describing the same leg and disagreeing.
    ///
    /// CANCELLED, AND NOT `REJECTED` OR A STRANDED `UNKNOWN`. The request is over and produced
    /// nothing at the broker, so it needs a TERMINAL state: an UNKNOWN row that nothing flags is a
    /// row nothing will ever move, since the reconciler scans the flagged set. `REJECTED` is
    /// reserved for a definite refusal BY THE BROKER (safety rule 3) and the broker was never asked.
    /// The two places that could read a CANCELLED cancel-leg as "the target was cancelled" —
    /// `cancel-all`'s `cancelled` count and its `not_cancelled` list — read the LEG WORD instead, so
    /// they consult the same transport evidence this does.
    ///
    /// NARROW ON PURPOSE. Only <see cref="ConnectorTransportException"/> qualifies: a JsonException
    /// or a NullReferenceException out of a connector that also happens to have reported
    /// <c>NothingWritten</c> is a connector defect, and a defect is not a proof. Everything else, and
    /// every silence, stays indefinite — including a connector that never marks its attempts, whose
    /// record reports null rather than <c>NothingWritten</c>.
    /// </summary>
    ExecutionRequest? SettleIfNothingWasSent(string requestId, Exception ex, string what)
    {
        if (ex is not ConnectorTransportException) return null;
        if (TransportLedger.Attached?.Outcome is not TransportOutcome.NothingWritten) return null;

        // Settle first, log second: both are writes to the same store and the outcome is the one
        // that must land. `Settle` latches and throws if it cannot be written down, which is right —
        // a definite outcome nobody could record is an unconfirmed one.
        var settled = Settle(requestId, ExecutionState.CANCELLED, error: ex.Message);
        _log.Activity($"{what} was not sent, and the platform never saw it: {ex.Message}", "warn");
        _log.Engineering("Gateway", "dispatch_not_sent", requestId: requestId,
            metadataJson: Json.Write(new { reason = ex.Message, transport = TransportOutcome.NothingWritten.ToString() }));
        StateChanged?.Invoke();
        return settled;
    }

    /// <summary>
    /// Records an indefinite outcome and takes trading down with it: one place for the four things
    /// that must always happen together, because until 2026-09-02 they happened together on some
    /// paths and not at all on others.
    /// </summary>
    ExecutionRequest RecordIndefinite(string requestId, string technical, string sentence,
        Exception? ex = null, string? connectorOrderId = null)
    {
        // PAUSE FIRST, WRITE SECOND. Everything below this line is a database write, and the reason
        // this method exists at all is that something went wrong at the worst moment — which is
        // exactly when the disk is full, the file is locked by another connection, or the store is
        // read-only. Persisting first meant a throw skipped the health row, the logs and the pause,
        // leaving a touched wire, an unflagged DISPATCHING row and an open gate.
        LatchUnconfirmed(requestId, "an order outcome is unconfirmed");
        StateChanged?.Invoke();

        try
        {
            var final = SettleUnknown(requestId, technical, connectorOrderId);
            _log.Activity($"{sentence} AI trading is paused until it is confirmed.", "warn");
            _log.Engineering("Gateway", "dispatch_unknown", "warn", requestId: requestId, ex: ex,
                metadataJson: Json.Write(new { reason = technical, exception = ex?.GetType().FullName }));
            StateChanged?.Invoke();
            return final;
        }
        catch (Exception persist)
        {
            // The record could not be made. The pause above stands, and the caller is told rather
            // than handed a record that does not exist. Same off-thread filing as the definite path.
            FileAfterTheStoreRefused("record_indefinite_failed", requestId, persist,
                new { reason = technical, original = ex?.GetType().FullName });

            throw new TradeAgentException(ErrorCode.STATE_DATABASE_CORRUPT,
                $"the outcome of {requestId} could not be written down ({persist.Message}); trading is paused");
        }
    }

    /// <summary>
    /// <see cref="RecordIndefinite"/> for a caller that must not stop. It can throw when the store
    /// refuses the write — and inside a loop over positions, that would abandon the positions after
    /// it. The in-memory pause is already latched before the throw, so continuing costs nothing that
    /// matters and finishing the emergency is worth more than the exception.
    /// </summary>
    void SafelyRecordIndefinite(string requestId, string technical, string sentence,
        Exception? ex = null, string? connectorOrderId = null)
    {
        try { RecordIndefinite(requestId, technical, sentence, ex, connectorOrderId); }
        catch (Exception) { /* latched in memory; the background retry carries the reason */ }
    }

    /// <summary>
    /// WHAT THE PLATFORM ANSWERED, TRANSLATED INTO WHAT WE MAY RECORD. Total over
    /// <see cref="ExecutionState"/> on purpose: the catch-all this replaces mapped every state it did
    /// not list onto ACKNOWLEDGED, which turned UNKNOWN — whose entire meaning is "we do not know" —
    /// into "we do know, it is live, nothing to reconcile", and carried an order the broker had
    /// killed as open forever.
    ///
    /// CANCEL_PENDING is the one answer that is honest in neither direction. It is a real thing a
    /// platform can say, but DISPATCHING → CANCEL_PENDING is not a legal transition — the table
    /// refuses to let a dispatch "claim a cancel is merely pending", and
    /// FaultTests.The_table_lets_a_dispatching_cancel_reach_cancelled pins that refusal — so
    /// recording it would file an `illegal_settle` and leave the record stranded in DISPATCHING,
    /// which is the exact defect this map exists to close. UNKNOWN and a reconcile is the honest
    /// destination for it, as it is for every answer that is not an outcome.
    /// </summary>
    static (ExecutionState To, bool Indefinite) MapDispatchOutcome(ExecutionState answered) => answered switch
    {
        ExecutionState.FILLED           => (ExecutionState.FILLED, false),
        ExecutionState.PARTIALLY_FILLED => (ExecutionState.PARTIALLY_FILLED, false),
        ExecutionState.WORKING          => (ExecutionState.WORKING, false),
        ExecutionState.ACKNOWLEDGED     => (ExecutionState.ACKNOWLEDGED, false),
        ExecutionState.REJECTED         => (ExecutionState.REJECTED, false),
        ExecutionState.CANCELLED        => (ExecutionState.CANCELLED, false),
        _                               => (ExecutionState.UNKNOWN, true)
    };

    async Task<ExecutionRequest> DispatchPlaceAsync(AgentContext ctx, ExecutionRequest stored, PlaceIntent intent, CancellationToken ct)
    {
        // EVERY GATE IS EVALUATED HERE, at the last point where refusing still means nothing was
        // sent. See ReauthorizeAtDispatchOrThrow and ReserveDispatchOrThrow: the first closes the
        // window between the authorization and the wire, the second makes the minute's budget an
        // atomic take rather than a count that several callers all read as free.
        ReauthorizeAtDispatchOrThrow(ctx, stored);
        using var slot = ReserveDispatchOrThrow();

        // Write-ahead: DISPATCHING is durable before the wire is touched, so a crash mid-flight is
        // recoverable as "we may have sent this" rather than lost entirely.
        var current = _opt.IdempotencyEnabled
            ? _requests.Transition(stored.RequestId, stored.State, ExecutionState.DISPATCHING)
            : stored;

        // THE INTENT TRAVELS ONTO THE COMMAND. A close is an offsetting placement, so the connector
        // cannot tell it from an opening order by anything on the wire — and it is the connector that
        // chooses the deadline. Carried rather than re-derived here: it was decided where the
        // operation was decomposed, and the position it was sized from is not in scope any more.
        var cmd = new PlaceOrderCommand(stored.ClientOrderId, stored.AccountId, intent.Symbol, intent.Side,
            intent.Type, intent.Quantity, intent.LimitPrice, intent.StopPrice, intent.Tif, intent.Comment)
        { Intent = intent.Intent };

        // THE DISPATCHER MARKS THE ATTEMPT, and it does so before the call rather than trusting the
        // connector to. See TransportLedger.MarkDispatch: `not-sent` is an assurance produced by an
        // EMPTY transport record, and a connector that mutates without marking makes "nothing was
        // recorded" mean "nobody wrote it down". The mark is what keeps the two apart at the source.
        // It also attaches a record when this call has no sweep leg around it, which is what lets the
        // catch below read a proven NothingWritten for a single `buy` or `close`.
        using var dispatch = TransportLedger.MarkDispatch();

        // AND THE DISPATCHER CLAIMS THE ROW WHILE IT IS INSIDE THE CALL. See HoldDispatch: a
        // DISPATCHING row on disk cannot say whether anyone is still flying it, and the reconciler
        // must not settle one that is.
        using var held = HoldDispatch(current.RequestId);
        slot.Commit();

        // ONLY THE WIRE CALL IS INSIDE THE TRY, and that is deliberate. The catch below is a
        // catch-all, so anything left in here would be read as "we do not know what the broker did"
        // — including a log write against a locked database or a UI subscriber throwing out of
        // StateChanged, neither of which is news about the broker at all.
        OrderInfo order;
        try
        {
            order = await Connector.PlaceOrderAsync(cmd, ct);
        }
        catch (ConnectorRejectedException ex)
        {
            // Definitive: the broker said no. Nothing is working, so nothing needs reconciling. This
            // is the ONLY exception that may settle a record without flagging it.
            var refused = Settle(current.RequestId, ExecutionState.REJECTED, error: ex.Message);
            _log.Activity($"Order refused by the broker: {ex.Message}", "warn");
            StateChanged?.Invoke();
            return refused;
        }
        catch (Exception ex)
        {
            // UNLESS THE CONNECTOR CAN PROVE NOTHING LEFT THE PROCESS. See SettleIfNothingWasSent:
            // this is the one failure that is definite without a broker having answered.
            if (SettleIfNothingWasSent(current.RequestId, ex,
                    $"{intent.Side} {intent.Quantity} {intent.Symbol}") is { } notSent) return notSent;

            // EVERYTHING ELSE IS INDEFINITE, which is what docs/CONTRACTS.md always said and what
            // the taxonomy did not do: it named ConnectorTransportException, TimeoutException and
            // OperationCanceledException, and a JsonException or a NullReferenceException from a
            // connector deserializing a frame AFTER the broker accepted walked straight out of here,
            // past the settle, leaving the write-ahead row as the last word with trading still open.
            // The order may be live. Record UNKNOWN, pause, reconcile — never retry.
            return RecordIndefinite(current.RequestId, ex.Message,
                "Something went wrong while sending an order and TradeAgent could not confirm what the platform did.", ex);
        }

        var (to, indefinite) = MapDispatchOutcome(order.State);
        if (indefinite)
            return RecordIndefinite(current.RequestId,
                $"the platform answered {order.State}, which is not an outcome this order can be recorded as",
                $"The platform answered {order.State} for an order TradeAgent sent, which is not something it can record as done.",
                connectorOrderId: order.ConnectorOrderId);

        var final = Settle(current.RequestId, to, order.ConnectorOrderId, order.FilledQuantity);
        _log.Activity($"{intent.Side} {intent.Quantity} {intent.Symbol} -> {to}");
        StateChanged?.Invoke();
        return final;
    }

    /// <summary>How long a parked order stays approvable. Shown beside the request so the person knows.</summary>
    public TimeSpan ApprovalTtl => _opt.ApprovalTtl;

    /// <summary>
    /// AN APPROVAL IS A DISPATCH DECISION, AND IT IS AUTHORIZED AT THE MOMENT IT IS MADE.
    ///
    /// The order was parked after passing every gate — at that time. Minutes or hours later a person
    /// presses Approve, and until 2026-09-02 this method went straight to the wire on that stale
    /// verdict: kill switch pressed since, mode changed since, account cleared since, connection dead
    /// since, quote stale since, limits consumed since — none of it was looked at. So the same gates
    /// a fresh dispatch faces run again here, in the same order, and under the dispatch gate so that
    /// "with whatever has been dispatched in between" is exact rather than approximate.
    ///
    /// Who is asking matters. The person pressing Approve is the operator, but the ORDER is the AI's
    /// proposal, so it is authorized as the AI's own session — which is what makes the kill switch
    /// bite: "stop AI trading" refuses the approval with AI_TRADING_STOPPED, and re-enabling and then
    /// approving is two deliberate acts. The emergency controls are outside this gate and stay there.
    ///
    /// A refusal leaves the record AWAITING_APPROVAL for a human to decline deliberately — with one
    /// exception. A request older than ApprovalTtl is declined here, through the state machine, so
    /// that pressing Approve on a dead request ends it rather than half-reviving it; the AI proposes
    /// again against the market as it is now. Age is judged before any of the gates below, so a dead
    /// request cannot hide behind a refusal the user could lift and then walk straight back into.
    ///
    /// NOTHING SWEEPS. Expiry is evaluated here and only here, so an expired request keeps its
    /// AWAITING_APPROVAL row on the Dashboard until someone presses Approve on it — which is exactly
    /// why the row states the approve-by time rather than relying on the row disappearing. Expiring
    /// them in the background would need the app's periodic loop (AppHost.BackgroundAsync) to call a
    /// sweep, which is a change outside this unit's files.
    /// </summary>
    public async Task<ExecutionRequest> ApproveAsync(string requestId, CancellationToken ct = default)
    {
        await _dispatchGate.WaitAsync(ct);
        try
        {
            var stored = _requests.Get(requestId) ?? throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST, "unknown request");
            if (stored.State != ExecutionState.AWAITING_APPROVAL)
                throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST, $"request is {stored.State}, not awaiting approval");

            // TWO KINDS OF REQUEST PARK, AND EACH IS DISPATCHED AS ITSELF. A placement carries a
            // PlaceIntent; a modification carries the target and the values it asked for, in exactly
            // the shape the reconciler reads back (TargetRef). Reading a modification as a placement
            // would hand DispatchPlaceAsync a symbol-less intent and turn an approved change into a
            // new order, so the shape is chosen by the record's own intent and nothing else may park.
            if (stored.Intent is not (RequestIntent.PLACE or RequestIntent.MODIFY))
                throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                    $"a {stored.Intent} request is not something that waits for approval");

            var intent = stored.Intent == RequestIntent.PLACE ? Json.Read<PlaceIntent>(stored.ParametersJson)! : null;
            var change = stored.Intent == RequestIntent.MODIFY ? Json.Read<TargetRef>(stored.ParametersJson)! : null;
            var what = intent is not null
                ? $"{intent.Side} {intent.Quantity} {intent.Symbol}"
                : $"the change to order {change!.Order}";

            // AGE IS BOUNDED AT BOTH ENDS, AND BOTH BOUNDS FAIL CLOSED.
            //
            // Below zero: a record timestamped in the future — the clock stepped backwards between
            // parking and approving, or a database was restored — gives a negative age that no
            // positive limit can ever exceed, so the request would stay approvable forever. Time
            // that does not make sense is not a reason to trust a parked order, so it expires.
            //
            // At the limit: `>=`, not `>`. ApprovalTtl is documented as literal, with no "0 = off",
            // and under `>` a frozen clock leaves the age exactly zero, which a zero limit would
            // then let through — the opposite of what a zero limit means.
            var age = Now - stored.CreatedAt;
            if (age < TimeSpan.Zero || age >= _opt.ApprovalTtl)
            {
                var minutes = _opt.ApprovalTtl.TotalMinutes.ToString("0");
                var untrustworthy = age < TimeSpan.Zero;
                _requests.Transition(requestId, ExecutionState.AWAITING_APPROVAL, ExecutionState.CANCELLED,
                    error: untrustworthy
                        ? $"approval expired: recorded {(-age).TotalMinutes:0} minutes in the future, so its age cannot be trusted"
                        : $"approval expired: waited {age.TotalMinutes:0} minutes, the limit is {minutes}");
                _log.Activity(untrustworthy
                    ? $"{what} was declined because TradeAgent cannot tell how old it is. Nothing was sent; the AI can propose it again."
                    : $"{what} waited more than {minutes} minutes for your approval and was declined. Nothing was sent; the AI can propose it again.", "warn");
                _log.Engineering("Gateway", "approval_expired", "warn", requestId: requestId,
                    metadataJson: Json.Write(new { age_minutes = age.TotalMinutes, ttl_minutes = _opt.ApprovalTtl.TotalMinutes }));
                StateChanged?.Invoke();
                throw new GatewayDeniedException(ErrorCode.APPROVAL_EXPIRED, untrustworthy
                    ? $"this order is recorded {(-age).TotalMinutes:0} minutes in the future, so its age cannot be trusted; it has been declined, and the AI has to propose it again"
                    : $"this order waited {age.TotalMinutes:0} minutes for approval and the limit is {minutes}; it has been declined, and the AI has to propose it again");
            }

            // Authorized as the AI, never as the operator. A parked record always carries the
            // agent's own session (operator orders are never parked); "agent" stands in if not.
            var proposer = new AgentContext(stored.AgentSessionId ?? "agent");
            if (proposer.IsOperator) proposer = new AgentContext("agent");

            // The target as the platform holds it at the moment of the PRESS, read inside the gates
            // below and carried out of them so the dispatcher judges the answer against the same
            // reading the risk check used.
            OrderInfo? before = null;

            try
            {
                // The mode it was proposed under is the only mode it may be approved in. PAPER would
                // send a real-money proposal to the simulator, LIVE_AUTONOMOUS would dispatch a
                // confirm-mode order under rules the person never chose for it, OBSERVE forbids all.
                if (Settings.Mode != TradingMode.LIVE_CONFIRM)
                    throw new GatewayDeniedException(ErrorCode.MODE_FORBIDS_EXECUTION,
                        $"mode is now {Settings.Mode}; this order was proposed under {stored.Mode} and can only be approved in LIVE_CONFIRM");

                AuthorizeOrThrow(proposer);

                // A RECORD NAMES A PLATFORM AND AN ACCOUNT, AND ONLY THE PAIR SAYS WHERE THE ORDER GOES.
                //
                // Switching platforms in Settings builds a new gateway over the SAME database
                // (AppHost.SwitchConnectorAsync), so a parked request outlives the platform it was
                // proposed for. An account id is unique only WITHIN a platform: the simulator's
                // SIM-001 and a broker's SIM-001 are different money, and comparing ids alone would
                // approve a simulator proposal onto the broker. Checked before the account, because
                // asking the wrong platform to look up the account is a meaningless question.
                if (Connector.Id != stored.ConnectorId)
                    throw new GatewayDeniedException(ErrorCode.ACCOUNT_NOT_FOUND,
                        $"this order was proposed on the {stored.ConnectorId} platform, but {Connector.Id} is connected now");

                // DispatchPlaceAsync sends to the account the RECORD names. If the owner changed
                // accounts while this waited, that is no longer the chosen one.
                var account = await AccountAsync(ct) ?? throw new GatewayDeniedException(ErrorCode.ACCOUNT_NOT_FOUND, "no account");
                if (account.Id != stored.AccountId)
                    throw new GatewayDeniedException(ErrorCode.ACCOUNT_NOT_FOUND,
                        $"this order was proposed for account {stored.AccountId}, but {account.Id} is now the chosen account");

                // A MODIFICATION IS RE-READ AND RE-JUDGED AGAINST THE BOOK AS IT IS NOW. The target
                // moved, filled or was cancelled while this waited, and the resulting size the
                // limits are applied to is the size the order has NOW — not the one it had when the
                // AI asked. ResultingOrderOrThrow refuses when the target cannot be read at all.
                before = change is null ? null : await TargetBeforeAsync(account.Id, change.Order!, ct);
                await RiskCheckOrThrow(intent
                    ?? ResultingOrderOrThrow(change!.Order!, before, change.Quantity, change.LimitPrice, change.StopPrice),
                    account, ct);
            }
            catch (GatewayDeniedException ex)
            {
                _log.Activity($"{what} was not approved: {ex.Info.UserMessage} ({ex.Message}). It is still waiting for your answer.", "warn");
                _log.Engineering("Gateway", "approval_refused", "warn", requestId: requestId,
                    metadataJson: Json.Write(new { code = ex.Code.ToString(), reason = ex.Message }));
                throw;
            }

            _log.Activity($"You approved {what}");
            return intent is not null
                ? await DispatchPlaceAsync(proposer, stored, intent, ct)
                : await DispatchModifyAsync(proposer, stored, change!.Order!, change.Quantity,
                    change.LimitPrice, change.StopPrice, before, ct);
        }
        finally { _dispatchGate.Release(); }
    }

    public ExecutionRequest Decline(string requestId)
    {
        var stored = _requests.Get(requestId) ?? throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST, "unknown request");

        // DECLINING IS ONLY MEANINGFUL BEFORE THE ORDER WAS SENT. Until 2026-09-01 the state table
        // was this method's only guard: it had none of its own, and a Decline on a DISPATCHING
        // record was refused by `Allowed[DISPATCHING]` not containing CANCELLED. Adding that edge —
        // correct for `CancelAsync`, which reaches it only after the broker confirmed a cancel —
        // took the guard away from here, where nothing has confirmed anything. Without this check a
        // decline would write CANCELLED over an order that is live at the broker: the software
        // asserting an outcome nobody obtained, which is exactly what rule 3 exists to prevent.
        // The explicit check is the right home for it anyway; the table is deliberately
        // intent-agnostic and cannot know that this caller has no broker answer behind it.
        if (stored.State is not (ExecutionState.CREATED or ExecutionState.AWAITING_APPROVAL))
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"request is {stored.State}; only an order that has not been sent can be declined");

        _log.Activity("You declined an order the AI asked for");
        return _requests.Transition(requestId, stored.State, ExecutionState.CANCELLED, error: "declined by user");
    }

    public async Task<ExecutionRequest> CancelAsync(AgentContext ctx, string requestId, string orderRef, CancellationToken ct = default)
    {
        AuthorizeOrThrow(ctx);
        var target = await ResolveConnectorOrderId(orderRef, ct);
        var record = new ExecutionRequest
        {
            RequestId = requestId, AgentSessionId = ctx.SessionId, ConnectorId = Connector.Id,
            AccountId = await RequireAccountId(ct), Instrument = "-", Intent = RequestIntent.CANCEL,
            ParametersJson = Json.Write(new { order = target }), ClientOrderId = ClientOrderIdFor(requestId),
            CreatedAt = Now, State = ExecutionState.CREATED, Mode = Settings.Mode
        };
        var (created, stored) = _requests.TryCreate(record);
        if (!created && _opt.IdempotencyEnabled) return stored;

        // The same re-check the place and modify paths make, for the same reason: the target
        // resolution above is an awaited read, and authority granted before it is not authority now.
        // A cancel is risk-reducing, so nothing here can refuse it for a LIMIT — ReauthorizeAtDispatch
        // asks only about authority and the mode the record was written under.
        ReauthorizeAtDispatchOrThrow(ctx, stored);

        var current = _requests.Transition(stored.RequestId, stored.State, ExecutionState.DISPATCHING);

        // The dispatcher's own attempt mark — see DispatchPlaceAsync for why it is not the
        // connector's to be trusted with — and the claim on the row for as long as it is on the wire.
        using var dispatch = TransportLedger.MarkDispatch();
        using var held = HoldDispatch(current.RequestId);
        try
        {
            await Connector.CancelOrderAsync(target, ct);
        }
        catch (ConnectorRejectedException ex)
        {
            return Settle(current.RequestId, ExecutionState.REJECTED, error: ex.Message);
        }
        catch (Exception ex)
        {
            if (SettleIfNothingWasSent(current.RequestId, ex, $"the cancellation of order {target}") is { } notSent)
                return notSent;

            // Same taxonomy as a place, and it did not used to be: this path caught neither
            // TimeoutException nor OperationCanceledException, so a cancel the broker CARRIED OUT
            // could throw on the way home and the ledger would never say the order was cancelled.
            return RecordIndefinite(current.RequestId, ex.Message,
                "TradeAgent could not confirm whether an order was cancelled.", ex);
        }

        // SETTLE BEFORE THE ACTIVITY LINE, as the place path does. Both are writes to the same
        // store, and this one had them the other way round: the log line went first, so a store that
        // refused writes threw here — after the platform had cancelled the order — and the method
        // left without ever reaching the settle. The outcome was then nowhere, and nothing was
        // latched, because nothing had failed inside Settle. The record is the part that matters;
        // the sentence about it is not.
        var cancelled = Settle(current.RequestId, ExecutionState.CANCELLED);
        _log.Activity($"Cancelled order {target}");
        return cancelled;
    }

    /// <summary>
    /// A MODIFICATION IS A MUTATING VERB AND IT PASSES THE SAME GATES AS A PLACEMENT.
    ///
    /// It called <see cref="AuthorizeOrThrow"/> and then went to the wire, which is the kill switch,
    /// the mode and the unconfirmed-work pause and NOTHING ELSE: no <see cref="RiskCheckOrThrow"/>,
    /// so the quantity cap, the notional cap, the open-position limit, the instrument allowlist and
    /// the rate limit did not apply to it — and no parking, so in LIVE_CONFIRM a change no person
    /// had seen went straight to the broker (REVIEW 2026-09-05, Codex F2; measured over the pipe as
    /// a working quantity-1 order grown to 1000 against a cap of 1).
    ///
    /// A working order is a live claim on the account, so raising its quantity is the same act as
    /// placing an order of the new size, arrived at by a different verb. It is therefore risk-checked
    /// ON THE ORDER AS IT WILL STAND — see <see cref="ResultingOrderOrThrow"/> — and parked for a
    /// person in LIVE_CONFIRM exactly as a placement is. What sends a parked one is
    /// <see cref="ApproveAsync"/>, which re-reads the target and re-runs every gate at the moment of
    /// the press rather than trusting the verdict this method reached.
    /// </summary>
    public async Task<ExecutionRequest> ModifyAsync(AgentContext ctx, string requestId, string orderRef,
        decimal? quantity, decimal? limitPrice, decimal? stopPrice, CancellationToken ct = default)
    {
        AuthorizeOrThrow(ctx);
        if (!Connector.Capabilities.SupportsModify)
            throw new GatewayDeniedException(ErrorCode.TRADING_PERMISSION_UNAVAILABLE, $"{Connector.DisplayName} cannot modify orders");
        var accountId = await RequireAccountId(ct);

        // THE TARGET AS IT STANDS BEFORE THE CHANGE, and it is written down rather than only used
        // here, because the reconciler judges the same modification later from the record alone. It
        // is what makes "the platform handed back the price it already had" distinguishable from
        // "the platform applied the change": a returned price is only evidence of a change if it is
        // not the price that was there before.
        //
        // It is ALSO the basis of the risk check below, and that is why it is no longer best effort:
        // a change's effect on exposure is a statement about the order it is aimed at, and there is
        // no honest one to make without it.
        var (target, before) = await ResolveModifyTargetAsync(orderRef, accountId, ct);
        var account = await AccountAsync(ct) ?? throw new GatewayDeniedException(ErrorCode.ACCOUNT_NOT_FOUND, "no account");
        await RiskCheckOrThrow(ResultingOrderOrThrow(target, before, quantity, limitPrice, stopPrice), account, ct);

        var record = new ExecutionRequest
        {
            RequestId = requestId, AgentSessionId = ctx.SessionId, ConnectorId = Connector.Id,
            AccountId = accountId, Instrument = "-", Intent = RequestIntent.MODIFY,
            ParametersJson = Json.Write(new
            {
                order = target, quantity, limitPrice, stopPrice,
                symbol = before?.Symbol, account = accountId,
                wasLimit = before?.LimitPrice, wasStop = before?.StopPrice
            }),
            ClientOrderId = ClientOrderIdFor(requestId), CreatedAt = Now,
            State = Settings.Mode == TradingMode.LIVE_CONFIRM && !ctx.IsOperator
                ? ExecutionState.AWAITING_APPROVAL : ExecutionState.CREATED,
            Mode = Settings.Mode
        };
        var (created, stored) = _requests.TryCreate(record);
        if (!created && _opt.IdempotencyEnabled)
        {
            // The place path answers a repeat of a parked id with APPROVAL_REQUIRED rather than the
            // row, so that an agent polling its own request is told what is holding it up instead of
            // reading AWAITING_APPROVAL as an outcome. A parked modification is the same situation.
            if (stored.State == ExecutionState.AWAITING_APPROVAL)
                throw new GatewayDeniedException(ErrorCode.APPROVAL_REQUIRED, $"request {requestId} is still waiting for your approval");
            return stored;
        }

        if (stored.State == ExecutionState.AWAITING_APPROVAL)
        {
            _log.Activity($"AI is asking permission to change order {target}");
            throw new GatewayDeniedException(ErrorCode.APPROVAL_REQUIRED, $"request {requestId} is waiting for your approval");
        }

        return await DispatchModifyAsync(ctx, stored, target, quantity, limitPrice, stopPrice, before, ct);
    }

    /// <summary>
    /// THE ORDER AS IT WILL STAND IF THE PLATFORM APPLIES THE CHANGE — what a modification has to be
    /// risk-checked against, since every limit the owner set is a statement about an order rather
    /// than about a delta. A field the change does not name keeps the value the order already has.
    ///
    /// IT FAILS CLOSED WHEN THE TARGET CANNOT BE READ. The resulting size of a price-only change is
    /// the size the order already has, and the instrument every limit is applied per is the one the
    /// order is on; guessing either is how a cap becomes decorative. Reading the book is still best
    /// effort for the VERDICT — <see cref="CheckModification"/> simply has one fewer thing to check
    /// without it — but it cannot be best effort for the CHECK.
    ///
    /// The time in force is not carried on <see cref="OrderInfo"/> and no limit reads it, so the
    /// resulting order is described with the default rather than with a guess at the target's.
    /// </summary>
    /// <summary>
    /// THE TARGET'S BROKER ID AND THE TARGET ITSELF, FROM ONE READING OF THE BOOK.
    ///
    /// <see cref="ResolveConnectorOrderId"/> and <see cref="TargetBeforeAsync"/> issue the same
    /// `orders` RPC against the same account a moment apart, and a modification needs both. One read
    /// is a whole connector deadline off the handler's chain — 50 s at shipped ATAS values, which is
    /// a term in the shutdown drain — and it is also the more honest reading: a target resolved from
    /// one snapshot and described from another can be two different orders.
    ///
    /// A reference the REQUEST STORE can name resolves without the book, as it always could; the
    /// book is still read, because the risk check needs the order and not just its id, and a read
    /// that fails leaves the change unjudgeable rather than unresolvable — which is a refusal in the
    /// owner's words (<see cref="ResultingOrderOrThrow"/>) instead of a connector error.
    /// </summary>
    async Task<(string Target, OrderInfo? Before)> ResolveModifyTargetAsync(string reference, string accountId, CancellationToken ct)
    {
        var known = _requests.Get(reference)?.ConnectorOrderId;
        IReadOnlyList<OrderInfo> book;
        try { book = await Connector.GetOrdersAsync(accountId, true, null, ct); }
        catch (Exception) when (known is not null) { return (known, null); }

        var hit = book.FirstOrDefault(o =>
            string.Equals(o.ConnectorOrderId, known ?? reference, StringComparison.Ordinal) ||
            string.Equals(o.ClientOrderId, reference, StringComparison.Ordinal));

        return (hit?.ConnectorOrderId ?? known
            ?? throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST, $"no order matches '{reference}'"), hit);
    }

    PlaceIntent ResultingOrderOrThrow(string target, OrderInfo? before, decimal? quantity, decimal? limitPrice, decimal? stopPrice)
    {
        if (before is null)
            throw new GatewayDeniedException(ErrorCode.RISK_CHECK_UNAVAILABLE,
                $"order {target} could not be read back from the platform, so what this change would do " +
                "to your exposure cannot be checked against your limits");

        return new PlaceIntent(before.Symbol, before.Side, before.Type,
            quantity ?? before.Quantity, limitPrice ?? before.LimitPrice, stopPrice ?? before.StopPrice,
            TimeInForce.Day, "modify");
    }

    async Task<ExecutionRequest> DispatchModifyAsync(AgentContext ctx, ExecutionRequest stored, string target,
        decimal? quantity, decimal? limitPrice, decimal? stopPrice, OrderInfo? before, CancellationToken ct)
    {
        var accountId = stored.AccountId;

        // THE GATES ARE DECIDED HERE, not where the request came in. Everything above this line is
        // an awaited read, and the kill switch, the mode and the live-activation switch are all
        // things the person at the keyboard can change while one is in flight (REVIEW 2026-09-05
        // finding 6 / Codex F4). This is the last point at which refusing still means nothing was
        // sent.
        ReauthorizeAtDispatchOrThrow(ctx, stored);
        using var slot = ReserveDispatchOrThrow();

        var current = _requests.Transition(stored.RequestId, stored.State, ExecutionState.DISPATCHING);
        var command = new ModifyOrderCommand(target, quantity, limitPrice, stopPrice);
        OrderInfo o;
        using var dispatch = TransportLedger.MarkDispatch();
        using var held = HoldDispatch(current.RequestId);
        slot.Commit();
        try
        {
            o = await Connector.ModifyOrderAsync(command, ct);
        }
        catch (ConnectorRejectedException ex)
        {
            return Settle(current.RequestId, ExecutionState.REJECTED, error: ex.Message);
        }
        catch (Exception ex)
        {
            if (SettleIfNothingWasSent(current.RequestId, ex, $"the change to order {target}") is { } notSent)
                return notSent;

            return RecordIndefinite(current.RequestId, ex.Message,
                "TradeAgent could not confirm whether an order was changed.", ex);
        }

        // "IT RETURNED AN ORDER" IS NOT AN ANSWER. This settled ACKNOWLEDGED unconditionally without
        // looking at what came back, so a platform that quietly ignored the request left the ledger
        // saying a stop had been moved when it had not. At dispatch time both "no" and "cannot tell"
        // are unconfirmed; the reconciler is where they part company.
        await EnsureInstrumentsAsync(ct);
        if (CheckModification(command, o, TargetFacts.Of(before, accountId)) != ModifyVerdict.Applied)
            return RecordIndefinite(current.RequestId,
                $"the platform returned the order as {o.State} qty={o.Quantity} limit={o.LimitPrice?.ToString() ?? "none"} " +
                $"stop={o.StopPrice?.ToString() ?? "none"}, which does not show the change that was asked for",
                "The platform did not show the change TradeAgent asked for on that order.",
                connectorOrderId: o.ConnectorOrderId);

        // Settle first, log second — see the note on the same two lines in CancelAsync.
        var applied = Settle(current.RequestId, ExecutionState.ACKNOWLEDGED, connectorOrderId: o.ConnectorOrderId);
        _log.Activity($"Modified order {target}");
        return applied;
    }

    enum ModifyVerdict
    {
        /// <summary>The order carries what was asked for.</summary>
        Applied,
        /// <summary>
        /// It does not — and nothing here can tell "the platform refused" from "the platform
        /// answered in units or on a grid we cannot read". There is deliberately no third value:
        /// only a ConnectorRejectedException is a definite refusal, and that never reaches here.
        /// </summary>
        Unknowable
    }

    /// <summary>
    /// What the target was before the change, as far as anything could see. Both the dispatcher (from
    /// the book, a moment before the wire) and the reconciler (from the record the dispatcher wrote)
    /// judge a modification through this, so the two cannot drift apart. Every field is optional:
    /// what is not known is not checked, and is never guessed.
    /// </summary>
    sealed record TargetFacts(string? Symbol, string? Account, decimal? LimitPrice, decimal? StopPrice)
    {
        public static TargetFacts? Of(OrderInfo? before, string? account) =>
            before is null && account is null ? null
            : new TargetFacts(before?.Symbol, account ?? before?.AccountId, before?.LimitPrice, before?.StopPrice);
    }

    /// <summary>Reads the target as the platform holds it now. A book we cannot read yields null.</summary>
    async Task<OrderInfo?> TargetBeforeAsync(string accountId, string target, CancellationToken ct)
    {
        try
        {
            return (await Connector.GetOrdersAsync(accountId, true, null, ct))
                .FirstOrDefault(o => string.Equals(o.ConnectorOrderId, target, StringComparison.Ordinal));
        }
        catch (Exception)
        {
            // Judged without it. This runs BEFORE the wire, so failing here costs a check, not an
            // order, and refusing the modification because the book was briefly unreadable would
            // be a worse trade than making the verdict one notch more conservative.
            return null;
        }
    }

    /// <summary>
    /// Did the platform actually do what the modification asked?
    ///
    /// FOUR THINGS, and the first is the one that used to be missing: the answer has to be about the
    /// ORDER THAT WAS NAMED. A returned order id that is not the target — or a symbol or account
    /// that is not the target's — is an answer about something else, and reading it as this order's
    /// meant a platform that replaced an order under a new id (or answered about the wrong one)
    /// left the ledger saying a stop had been moved on an order nobody had touched.
    ///
    /// Then the order has to still be in a state where a working modification means anything. A
    /// terminal order (it filled, or was cancelled, while the change was in flight) is not evidence
    /// that the change applied; it is evidence that we do not know at what price the fill happened,
    /// which is precisely an UNKNOWN.
    ///
    /// Then quantity, which is decidable now that the SDK says what OrderInfo.Quantity means — the
    /// TOTAL the order is for, never the remainder (see Contracts.cs). A number that does not match
    /// the request is a change that is not there; it is still never a definite refusal, because only
    /// a ConnectorRejectedException is one and it never reaches here.
    ///
    /// Then prices, ON THE INSTRUMENT'S OWN GRID. Platforms round a request to the tick, so asking
    /// 4242.13 of an instrument that trades in quarters comes back as 4242.25 — applied, and pausing
    /// trading over that was the defect this replaces.
    /// </summary>
    ModifyVerdict CheckModification(ModifyOrderCommand cmd, OrderInfo o, TargetFacts? was)
    {
        if (!string.Equals(o.ConnectorOrderId, cmd.ConnectorOrderId, StringComparison.Ordinal))
            return ModifyVerdict.Unknowable;
        if (was?.Symbol is { } symbol && !string.Equals(o.Symbol, symbol, StringComparison.Ordinal))
            return ModifyVerdict.Unknowable;
        if (was?.Account is { } account && !string.Equals(o.AccountId, account, StringComparison.Ordinal))
            return ModifyVerdict.Unknowable;

        if (o.State is not (ExecutionState.ACKNOWLEDGED or ExecutionState.WORKING or ExecutionState.PARTIALLY_FILLED))
            return ModifyVerdict.Unknowable;

        if (cmd.Quantity is { } q && o.Quantity != q) return ModifyVerdict.Unknowable;

        var tick = _instrumentCache.FirstOrDefault(i => i.Symbol == o.Symbol)?.TickSize ?? 0m;
        return PriceCarries(o.LimitPrice, cmd.LimitPrice, tick, was?.LimitPrice)
            && PriceCarries(o.StopPrice, cmd.StopPrice, tick, was?.StopPrice)
            ? ModifyVerdict.Applied : ModifyVerdict.Unknowable;
    }

    /// <summary>
    /// Does the price that came back carry the price that was asked for?
    ///
    /// TWO CANDIDATES, NOT A BAND. A platform puts a request on the instrument's grid and may round
    /// either way, so the request is carried by exactly two prices: the grid point below it and the
    /// grid point above. Nothing else. A tolerance of "within one tick" — which is what this used to
    /// be — accepts a NEIGHBOURING grid point when the request is already on the grid, so asking
    /// 4242.25 and being handed 4242.50 read as applied: a real, different price, called the one
    /// that was asked for.
    ///
    /// AND THE PRICE THAT WAS ALREADY THERE IS NOT EVIDENCE OF A CHANGE. A request smaller than the
    /// grid can express (4000.13 against a quarter grid on an order resting at 4000) comes back as
    /// the untouched price, which is one of those two candidates — so rounding alone cannot tell it
    /// from a platform that ignored the request. When the old price is known and the request differs
    /// from it, the answer has to differ from it too, or it settles nothing.
    ///
    /// A price off the grid, a grid that is unknown, or a price asked for and not returned at all:
    /// not evidence either way, which here means not applied.
    /// </summary>
    static bool PriceCarries(decimal? shown, decimal? asked, decimal tick, decimal? was)
    {
        if (asked is not { } want) return true;        // nothing was asked of this field
        if (shown is not { } have) return false;       // asked for a price, got none

        if (have != want)
        {
            if (tick <= 0m) return false;              // no grid to judge against
            var down = Math.Floor(want / tick) * tick;
            var up = Math.Ceiling(want / tick) * tick;
            if (have != down && have != up) return false;
        }

        return was is not { } had || had == want || have != had;
    }

    /// <summary>Tick sizes come from the instrument list, so it has to be there before judging one.</summary>
    async Task EnsureInstrumentsAsync(CancellationToken ct)
    {
        if (_instrumentCache.Count > 0) return;
        try { await InstrumentsAsync(ct); }
        catch (Exception) { /* judged without a grid; PriceVerdict says Unknowable rather than guessing */ }
    }

    public async Task<ExecutionRequest?> CloseAsync(AgentContext ctx, string requestId, string symbol, CancellationToken ct = default)
    {
        AuthorizeOrThrow(ctx);
        var accountId = await RequireAccountId(ct);
        var pos = (await Connector.GetPositionsAsync(accountId, ct)).FirstOrDefault(p => p.Symbol == symbol && p.Quantity != 0);
        if (pos is null) return null;
        return await PlaceAsync(ctx, requestId, new PlaceIntent(symbol,
            pos.Quantity > 0 ? OrderSide.Sell : OrderSide.Buy, OrderType.Market, Math.Abs(pos.Quantity),
            null, null, TimeInForce.Day, "close position") { Intent = OrderIntent.Close }, ct);
    }

    async Task<string> ResolveConnectorOrderId(string reference, CancellationToken ct)
    {
        if (_requests.Get(reference) is { ConnectorOrderId: { } coid }) return coid;
        var orders = await Connector.GetOrdersAsync(await RequireAccountId(ct), true, null, ct);
        var hit = orders.FirstOrDefault(o => o.ConnectorOrderId == reference || o.ClientOrderId == reference);
        return hit?.ConnectorOrderId
            ?? throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST, $"no order matches '{reference}'");
    }

    // ---------------------------------------------------------------- composite (multi-target) requests

    /// <summary>
    /// What a composite operation should do NOW: run its plan, or hand back the answer this request
    /// id already produced.
    /// </summary>
    /// <param name="Nonce">
    /// What this composite's per-target ids are derived from. It comes out of the STORE on a replay,
    /// which is what makes the legs of the second run land on the first run's records.
    /// </param>
    /// <param name="Targets">
    /// The plan as it was captured the FIRST time. A replay does not re-capture: an order placed
    /// after the original call was never part of this request and must not be swept by it.
    /// </param>
    /// <param name="StoredResultJson">
    /// The answer the first run gave, or null when it never finished giving one. Non-null means the
    /// caller may return this and do nothing else.
    /// </param>
    public sealed record CompositePlan(string RequestId, string Nonce,
        IReadOnlyList<string> Targets, string? StoredResultJson, bool Replay);

    /// <summary>
    /// PERSISTS THE COMPOSITE BEFORE ANY EFFECT, AND RECOGNISES A REPLAY (Codex C2).
    ///
    /// Idempotency by request id used to stop at <c>Place</c> — and at <c>Cancel</c>, <c>Modify</c>
    /// and <c>Close</c>, which each write one <c>execution_request</c> keyed by the caller's id. A
    /// SWEEP wrote none: it minted a fresh nonce per CALL, so the same request id sent twice was two
    /// sweeps, over two different books. An agent whose reply was lost — the exact situation a
    /// request id exists for — cancelled orders it had never been shown.
    ///
    /// Three cases come out of one insert:
    ///
    ///   1. New id — the plan and a fresh nonce are written down, then the caller runs it.
    ///   2. Known id WITH a result — the caller returns that result and touches nothing.
    ///   3. Known id with NO result — the first run died mid-flight. The caller re-runs it against
    ///      the STORED nonce and the STORED plan, so every leg that already has a record dispatches
    ///      nothing and only the legs that never ran do. That is a resumption, not a second sweep.
    ///
    /// The nonce is minted through <paramref name="freshNonce"/> rather than here because "fresh"
    /// means "not already in this installation's leg history", and the id shape those legs use
    /// belongs to the caller.
    /// </summary>
    public CompositePlan BeginComposite(AgentContext ctx, string requestId, string op,
        IReadOnlyList<string> targets, Func<string> freshNonce)
    {
        var (created, stored) = _composites.TryBegin(new CompositeRequest
        {
            RequestId = requestId,
            AgentSessionId = ctx.SessionId,
            Op = op,
            Nonce = freshNonce(),
            PlanJson = Json.Write(targets),
            CreatedAt = Now
        });

        if (created) return new CompositePlan(requestId, stored.Nonce, targets, null, false);

        _log.Engineering("Gateway", "composite_replayed", requestId: requestId,
            metadataJson: Json.Write(new { op, finished = stored.ResultJson is not null }));

        return new CompositePlan(requestId, stored.Nonce,
            Json.Read<List<string>>(stored.PlanJson) ?? [], stored.ResultJson, true);
    }

    /// <summary>Writes the answer this request id will give from now on. Only the first one sticks.</summary>
    public void CompleteComposite(string requestId, string resultJson) =>
        _composites.Complete(requestId, resultJson);

    // ---------------------------------------------------------------- emergency controls (operator only)

    /// <summary>The two emergency controls, as request-id prefixes. One press writes rows under one.</summary>
    public const string ClosePress = "op-close";
    public const string CancelPress = "op-cancel";

    /// <summary>
    /// ONE PRESS'S OWN NAME, MINTED ONCE AND NEVER HANDED BACK.
    ///
    /// It used to be minted by the SCREEN and reused: an object called `OperatorPress` held the
    /// nonce, a second click repeated it, and a restart read the nonce back out of the store so the
    /// button would go on repeating it. Six separate faults lived in that machinery rather than in
    /// the emergency — a definitely failed close that could never be pressed past (`TryCreate` found
    /// the terminal row and sent nothing, forever), a terminal press dropped at restart over a
    /// position that was not flat, a retry that acted on a stale captured set. None of them is worth
    /// having: a person who presses an emergency control twice is not asking for a retry, they are
    /// asking why the first one has not finished, and the honest answer is to show them and refuse.
    ///
    /// So the nonce is private, per press, and never reused. Random rather than sequential because
    /// an agent may create any request id it likes over the pipe: a guessable operator id would let
    /// it pre-occupy one and turn the owner's emergency press into a silent replay.
    /// </summary>
    static string NewPressNonce() => Guid.NewGuid().ToString("n")[..16];

    static string PressPrefix(string kind, string nonce) => $"{kind}-{nonce}";

    /// <summary>
    /// Whether a request id belongs to an emergency press rather than to an ordinary order.
    ///
    /// It is a prefix test and it is safe to be one. An agent cannot mint an id starting with
    /// <c>op-</c> at all (the pipe refuses it), and the ids the gateway mints for an agent's sweep
    /// are <c>op-{nonce}-{intent}-{i}</c> with a HEX nonce — so no sweep leg can ever begin
    /// <c>op-close-</c> or <c>op-cancel-</c>, because neither "close" nor "cancel" is hex.
    /// </summary>
    public static bool IsPressRecord(string requestId) =>
        requestId.StartsWith($"{ClosePress}-", StringComparison.Ordinal) ||
        requestId.StartsWith($"{CancelPress}-", StringComparison.Ordinal);

    /// <summary>Which control wrote this row. Only meaningful for a <see cref="IsPressRecord"/> id.</summary>
    public static string PressKindOf(string requestId) =>
        requestId.StartsWith($"{ClosePress}-", StringComparison.Ordinal) ? ClosePress : CancelPress;

    /// <summary>
    /// The nonce out of <c>{kind}-{nonce}</c> or <c>{kind}-{nonce}-{target}</c>. A nonce is hex, so
    /// the first segment after the kind is all of it however many dashes a broker's order id has.
    /// </summary>
    static string NonceIn(string requestId) =>
        requestId[(PressKindOf(requestId).Length + 1)..].Split('-')[0];

    /// <summary>The owner-facing name of a control, as it appears in the refusal.</summary>
    static string PressName(string kind) => kind == ClosePress ? "close-all" : "cancel-all";

    // ---- what one press did -------------------------------------------------------------------

    /// <summary>
    /// One target of one press: the row it wrote, what the platform answered about it, and what is
    /// on the account NOW for that target. The card shows these; nothing else reconstructs them.
    /// </summary>
    /// <param name="PositionNow">
    /// The position on this target's instrument at the moment the card was drawn, or null when the
    /// account could not be read or the target is an order rather than an instrument. IT IS THE
    /// SECOND HALF OF THE ANSWER: a close whose order is FILLED and a position that is still 2 long
    /// are not the same news, and the owner is the one who has to notice.
    /// </param>
    public sealed record PressTarget(string RequestId, string Target, ExecutionState State,
        bool Resolved, string Outcome, decimal? PositionNow);

    /// <summary>
    /// HOW ONE PRESS STANDS, JUDGED ONLY BY THE RECORDS THAT PRESS MADE.
    ///
    /// It used to be judged by <see cref="HasUnconfirmedWork"/> — anything unconfirmed anywhere — so
    /// an unrelated order kept the control locked, and, worse, a press whose own close was still
    /// unconfirmed could be released by someone else's record settling. A press is its own business.
    /// </summary>
    public sealed record PressOutcome(string Kind, string Nonce, DateTimeOffset SentAt,
        IReadOnlyList<PressTarget> Targets, int Unresolved, bool Complete, string Summary);

    /// <summary>Every row one press wrote, oldest first. The press-level row sorts first by design.</summary>
    List<ExecutionRequest> PressRows(string kind, string nonce) =>
        _requests.Query("request_id LIKE $p", ("$p", $"{PressPrefix(kind, nonce)}%"));

    /// <summary>
    /// A press this gateway still cannot account for, or null. THE STORE IS THE ONLY SOURCE: there
    /// is no press object to consult and nothing to reconstruct at startup, so a restart, a second
    /// window and a CLI all get the same answer from the same rows.
    ///
    /// "Cannot account for" is the flag, plus the in-memory latch for the case where the flag itself
    /// could not be written. It is deliberately NOT "the position is not flat": a press is a set of
    /// records and it ends when a person has read them.
    /// </summary>
    public string? UnresolvedPressNonce(string kind)
    {
        var blocked = _requests.Query("request_id LIKE $p", ("$p", $"{kind}-%"))
            .Where(r => r.NeedsReconciliation || _unconfirmed.ContainsKey(r.RequestId))
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefault();
        return blocked is null ? null : NonceIn(blocked.RequestId);
    }

    /// <summary>The open press of this kind as the card shows it, or null when there is none.</summary>
    public async Task<PressOutcome?> OpenPressAsync(string kind, CancellationToken ct = default) =>
        UnresolvedPressNonce(kind) is { } nonce ? await PressOutcomeAsync(kind, nonce, ct) : null;

    /// <summary>
    /// What one press did, per target, plus what is on the account now.
    ///
    /// THE ACCOUNT COMES OFF THE RECORDS, not off the settings. `RequireAccountId` answers with
    /// whichever account is selected NOW, and the owner can change that between the press and the
    /// card — at which point completion was being judged against a book the press never touched
    /// (Codex round-3 F14).
    /// </summary>
    public async Task<PressOutcome> PressOutcomeAsync(string kind, string nonce, CancellationToken ct = default)
    {
        var rows = PressRows(kind, nonce);
        if (rows.Count == 0)
            return new PressOutcome(kind, nonce, Now, [], 0, true, "Nothing was sent.");

        // Read once, for every target, off the account the RECORDS carry.
        IReadOnlyList<PositionInfo>? positions = null;
        var accountUnreadable = false;
        try { positions = await Connector.GetPositionsAsync(rows[0].AccountId, ct); }
        catch (Exception) { accountUnreadable = true; }

        var targets = rows.Select(r => new PressTarget(
            r.RequestId,
            TargetOf(r),
            r.State,
            !r.NeedsReconciliation && !_unconfirmed.ContainsKey(r.RequestId),
            OutcomeSentence(r),
            r.Instrument == "-" ? null : positions?.FirstOrDefault(p => p.Symbol == r.Instrument)?.Quantity ?? 0m
        )).ToList();

        var unresolved = targets.Count(t => !t.Resolved);
        var stillOpen = targets.Where(t => t.PositionNow is not null and not 0m)
            .Select(t => $"{t.Target} {t.PositionNow}").Distinct().ToList();

        var complete = unresolved == 0 && stillOpen.Count == 0 && !accountUnreadable;
        var summary =
            unresolved > 0
                ? $"{unresolved} of {targets.Count} record(s) from this press are still waiting for you." +
                  (stillOpen.Count > 0 ? $" Still open: {string.Join(", ", stillOpen)}." : "")
            : accountUnreadable ? "Every record is resolved, but the account could not be read back."
            : stillOpen.Count > 0 ? $"Every record is resolved. Still open: {string.Join(", ", stillOpen)}."
            : $"{targets.Count} record(s), all resolved and nothing left open.";

        return new PressOutcome(kind, nonce, rows.Min(r => r.CreatedAt), targets, unresolved, complete, summary);
    }

    /// <summary>The thing one press row is about: an instrument for a close, an order id for a cancel.</summary>
    static string TargetOf(ExecutionRequest r) =>
        r.Instrument != "-" ? r.Instrument
        : Json.Read<PressParameters>(r.ParametersJson)?.Order ?? "every working order";

    sealed record PressParameters(string? Order, string? Press);

    /// <summary>One sentence per target, in the words the owner reads on the card.</summary>
    static string OutcomeSentence(ExecutionRequest r) => r.State switch
    {
        ExecutionState.FILLED => "the platform filled it",
        ExecutionState.PARTIALLY_FILLED => $"the platform filled {r.FilledQuantity} of it so far",
        ExecutionState.CANCELLED => "the platform cancelled it",
        ExecutionState.REJECTED => $"the platform refused it ({r.LastError ?? "no reason given"})",
        ExecutionState.WORKING or ExecutionState.ACKNOWLEDGED => "the platform took it and it is still working",
        ExecutionState.CANCEL_PENDING => "the platform says the cancel is pending",
        ExecutionState.UNKNOWN or ExecutionState.RECONCILING => "not confirmed — check ATAS",
        ExecutionState.DISPATCHING => "sent, and nothing has come back yet",
        _ => $"the record says {r.State}"
    };

    // ---- making a press -----------------------------------------------------------------------

    /// <summary>
    /// Refuses a second press while the last one of this kind is still the owner's to resolve.
    ///
    /// PER KIND, not globally: an unresolved cancel-all must never be able to stop somebody
    /// flattening a position. The two controls are different decisions and are refused separately.
    /// </summary>
    void RefuseWhileAPressIsOpen(string kind)
    {
        if (UnresolvedPressNonce(kind) is not { } nonce) return;
        throw PressAlreadyOpen(kind, PressRows(kind, nonce).Min(r => r.CreatedAt));
    }

    /// <summary>
    /// The refusal itself, in the words <c>docs/CONTRACTS.md</c> promises, from either of the two
    /// places that can decide it: the early read, and the insert that loses the claim.
    /// </summary>
    static GatewayDeniedException PressAlreadyOpen(string kind, DateTimeOffset sentAt) =>
        new(ErrorCode.EMERGENCY_PRESS_UNRESOLVED,
            $"{PressName(kind)} sent at {sentAt.ToLocalTime():HH:mm}; resolve it first");

    /// <summary>
    /// Every row of every press of one kind, as a <c>LIKE</c> pattern. What "an open press of this
    /// kind" means to the durable claim in <see cref="OpenPressRow"/>.
    /// </summary>
    static string PressRowsLike(string kind) => $"{kind}-%";

    /// <summary>
    /// THE WRITE-AHEAD ROW FOR ONE THING ONE PRESS DOES — AND IT IS WRITTEN FLAGGED. That is the
    /// whole simplification.
    ///
    /// Before this, a row was flagged only when something went wrong, so a close the platform
    /// answered WORKING settled clean and the gate let the AI trade over an open position and an
    /// emergency nobody had read (Codex round-3 F11). A press is not over because its calls
    /// returned; it is over when the person who pressed it has seen what they did. Flagging at
    /// write-ahead time is what makes "from that moment trading is paused" true of every outcome
    /// including the good ones, and it is the reconciler's cue to leave these rows alone.
    ///
    /// The latch goes in FIRST, in memory, for the same reason <see cref="RecordIndefinite"/> does
    /// it: everything below is a database write and the pause must not depend on one.
    ///
    /// AND IT IS THE STEP THAT DECIDES WHETHER THIS PRESS HAPPENS AT ALL. <paramref name="claims"/>
    /// names the control this row is the FIRST row of; the insert then refuses to run while any row
    /// of that control is still flagged. <see cref="RefuseWhileAPressIsOpen"/> reads the store and
    /// this writes it, and until 2026-09-05 that was all there was: two callers arriving together
    /// both read "no press is open", both captured the same position, both passed the drift re-read
    /// because neither fill had landed, and both sent a market close — a long 2 became short 2 and
    /// both presses answered "ok" (REVIEW 2026-09-05 finding 2, probe P10; Codex F6). The early read
    /// stays, because it gives the person the reason; the INSERT is what makes it true. In one
    /// statement, so it holds across the two processes that can reach this button rather than only
    /// within one.
    /// </summary>
    ExecutionRequest OpenPressRow(string requestId, string accountId, RequestIntent intent,
        string instrument, string parametersJson, string paused, string? claims = null)
    {
        LatchUnconfirmed(requestId, paused);
        var (created, blocker) = _requests.TryCreateFlagged(new ExecutionRequest
        {
            RequestId = requestId,
            AgentSessionId = AgentContext.Operator.SessionId,
            ConnectorId = Connector.Id,
            AccountId = accountId,
            Instrument = instrument,
            Intent = intent,
            ParametersJson = parametersJson,
            ClientOrderId = ClientOrderIdFor(requestId),
            CreatedAt = Now,
            State = ExecutionState.CREATED,
            Mode = Settings.Mode
        }, paused, claims is null ? null : PressRowsLike(claims));

        // LOST THE CLAIM. Nothing was written under this id, so nothing may stay latched under it
        // either — a latch naming a row that does not exist is one nothing can ever release.
        if (!created && blocker is not null)
        {
            ClearLatch(requestId);
            throw PressAlreadyOpen(PressKindOf(blocker.RequestId), blocker.CreatedAt);
        }

        // A fresh nonce cannot collide with a row this store already holds, so finding one is not a
        // replay — it is a corrupt id space, and sending over it would be sending twice.
        if (!created)
            throw new TradeAgentException(ErrorCode.STATE_DATABASE_CORRUPT,
                $"{requestId} already exists; nothing was sent");

        return _requests.Transition(requestId, ExecutionState.CREATED, ExecutionState.DISPATCHING);
    }

    /// <summary>
    /// <see cref="Settle"/> for a caller that must not stop. It throws when the store refuses the
    /// write, and inside a loop over positions that abandons every position after this one — the
    /// exact fault <see cref="SafelyRecordIndefinite"/> exists to prevent, arrived at from the
    /// definite side. `Settle` has already latched the pause in memory before it throws, so
    /// continuing costs nothing that matters and finishing the emergency is worth more.
    /// </summary>
    void SafelySettle(string requestId, ExecutionState to, string? connectorOrderId = null,
        decimal? filled = null, string? error = null)
    {
        try { Settle(requestId, to, connectorOrderId, filled, error); }
        catch (Exception) { /* latched in memory by Settle; the background retry carries the reason */ }
    }

    /// <summary>
    /// Deliberately separate from the kill switch: stopping the AI must not move money.
    ///
    /// Outside AUTHORIZATION on purpose — this has to work while trading is paused, including while
    /// it is paused by the very records this method writes. What it may NOT do any more is touch the
    /// wire without leaving one, and what it may not do at all is run twice over an unresolved press.
    /// </summary>
    public async Task<PressOutcome> OperatorCancelAllAsync(CancellationToken ct = default)
    {
        // THE WHOLE PRESS IS THE EMERGENCY, NOT JUST ITS LAST FRAME (Codex C3).
        //
        // `RiskReducingScope` was opened by the PIPE SERVER, so only an agent's sweep got the
        // emergency bound. The button and the CLI come through here and inherited nothing: the
        // connector classifies urgency by the bridge op it is about to send, which is right for the
        // final frame and blind to everything that has to happen first — the orders this press
        // captures, the position it re-reads before each close. Those are ordinary `orders` and
        // `positions` RPCs, so at shipped deadlines the person holding the button waited out the
        // whole of a stalled bridge before the two-second frame it was hurrying to send got a turn.
        //
        // Opened here, where the intent is known, all three callers get it — and it is ONE absolute
        // deadline for the operation rather than a fresh budget per RPC, so the promise does not
        // scale with the size of the book.
        using var emergency = RiskReducingScope.Begin(Connector.EmergencyBudget);

        RefuseWhileAPressIsOpen(CancelPress);

        var accountId = await RequireAccountId(ct);
        var nonce = NewPressNonce();
        var pressId = PressPrefix(CancelPress, nonce);
        var paused = $"you pressed Cancel all working orders at {Now.ToLocalTime():HH:mm}; it is waiting for you on the Dashboard";

        // THE PRESS ITSELF IS A RECORD, WRITTEN BEFORE ANYTHING IS READ OR SENT. If the process dies
        // between here and the first wire call, the row is on disk, flagged, and the restart refuses
        // to trade over it — which is the whole point of a write-ahead. It is also this press's
        // CLAIM on the control: the insert refuses to run while another cancel-all is unresolved,
        // so the refusal above and this row are one step rather than two (finding 2 / Codex F6).
        OpenPressRow(pressId, accountId, RequestIntent.CANCEL_ALL, "-",
            Json.Write(new { order = (string?)null, press = nonce }), paused, claims: CancelPress);

        List<string> captured;
        try
        {
            captured = (await Connector.GetOrdersAsync(accountId, false, null, ct))
                .Select(o => o.ConnectorOrderId).ToList();
        }
        catch (Exception ex)
        {
            // NOTHING CAN BE SENT, AND THAT IS THE HONEST ANSWER. The wire call is per-order now, so
            // a book that cannot be read names no orders to cancel. It used to send an account-wide
            // sweep anyway on the argument that a failed READ must not stop an emergency — but that
            // sweep is exactly the call this unit removed, because "cancel whatever is there" acts
            // on orders the person never saw and cannot be reconciled against anything.
            SafelyRecordIndefinite(pressId, ex.Message,
                "TradeAgent could not read your working orders, so nothing was cancelled.", ex);
            _log.Activity("You pressed Cancel all working orders. TradeAgent could not read your orders, " +
                          "so nothing was sent. Check ATAS.", "warn");
            StateChanged?.Invoke();
            return await PressOutcomeAsync(CancelPress, nonce, ct);
        }

        // THE PRESS IS A COMPOSITE TOO, and its plan is written down before a single cancel goes out.
        // The agent's sweep needs this row to recognise a replay; the press cannot be replayed at all
        // (a second press is refused, and the one after that is a fresh nonce), so what the row buys
        // here is the record: what the press captured, and what it answered, as one durable object
        // beside the per-target rows.
        BeginComposite(AgentContext.Operator, pressId, Ops.CancelAll, captured, () => nonce);

        var cancelled = new List<string>();
        foreach (var target in captured)
        {
            var rid = $"{pressId}-{target}";
            OpenPressRow(rid, accountId, RequestIntent.CANCEL, "-",
                Json.Write(new { order = target, press = nonce }), paused);
            try
            {
                using var dispatch = TransportLedger.MarkDispatch();
                await Connector.CancelOrderAsync(target, ct);
            }
            catch (ConnectorRejectedException ex)
            {
                // A DEFINITE refusal about THIS order, and safety rule 3 says it is the only thing
                // allowed to be recorded as one.
                SafelySettle(rid, ExecutionState.REJECTED, error: ex.Message);
                continue;
            }
            catch (Exception ex)
            {
                // ONE ORDER FAILING SAYS NOTHING ABOUT THE NEXT ONE — the same lesson close-all
                // learned. It is recorded, the pause is already latched, and the loop goes on.
                SafelyRecordIndefinite(rid, ex.Message,
                    $"TradeAgent could not confirm whether order {target} was cancelled.", ex);
                continue;
            }
            SafelySettle(rid, ExecutionState.CANCELLED, error: "the platform accepted the cancel for this order");
            cancelled.Add(target);
        }

        if (cancelled.Count == captured.Count)
            SafelySettle(pressId, ExecutionState.CANCELLED,
                error: $"{cancelled.Count} order(s) were cancelled one by one");
        else
            SafelyRecordIndefinite(pressId,
                $"{captured.Count - cancelled.Count} of {captured.Count} captured order(s) were not confirmed cancelled",
                $"TradeAgent could not confirm {captured.Count - cancelled.Count} of the {captured.Count} " +
                "orders it tried to cancel.");

        var cancelOutcome = await PressOutcomeAsync(CancelPress, nonce, ct);
        CompleteComposite(pressId, Json.Write(cancelOutcome));

        _log.Activity(captured.Count == 0
            ? "You pressed Cancel all working orders; there was nothing on the book to cancel."
            : $"You cancelled all working orders ({cancelled.Count} of {captured.Count}). " +
              "AI trading is paused until you confirm what happened on the Dashboard.", "warn");
        StateChanged?.Invoke();
        return cancelOutcome;
    }

    /// <summary>
    /// Also deliberately separate: this one does move money, so it is never the same button.
    ///
    /// One write-ahead execution request per position, keyed by the press — the same machinery the
    /// agent's own close goes through, which recorded UNKNOWN and paused while this button recorded
    /// nothing at all.
    /// </summary>
    public async Task<PressOutcome> OperatorCloseAllAsync(CancellationToken ct = default)
    {
        // THE WHOLE PRESS IS THE EMERGENCY, NOT JUST ITS LAST FRAME (Codex C3).
        //
        // `RiskReducingScope` was opened by the PIPE SERVER, so only an agent's sweep got the
        // emergency bound. The button and the CLI come through here and inherited nothing: the
        // connector classifies urgency by the bridge op it is about to send, which is right for the
        // final frame and blind to everything that has to happen first — the orders this press
        // captures, the position it re-reads before each close. Those are ordinary `orders` and
        // `positions` RPCs, so at shipped deadlines the person holding the button waited out the
        // whole of a stalled bridge before the two-second frame it was hurrying to send got a turn.
        //
        // Opened here, where the intent is known, all three callers get it — and it is ONE absolute
        // deadline for the operation rather than a fresh budget per RPC, so the promise does not
        // scale with the size of the book.
        using var emergency = RiskReducingScope.Begin(Connector.EmergencyBudget);

        RefuseWhileAPressIsOpen(ClosePress);

        var accountId = await RequireAccountId(ct);
        var nonce = NewPressNonce();
        var paused = $"you pressed Close all positions at {Now.ToLocalTime():HH:mm}; it is waiting for you on the Dashboard";

        var captured = (await Connector.GetPositionsAsync(accountId, ct))
            .Where(p => p.Quantity != 0)
            .Select(p => (p.Symbol, p.Quantity))
            .ToList();

        if (captured.Count == 0)
        {
            _log.Activity("You pressed Close all positions; there was nothing open to close.", "warn");
            StateChanged?.Invoke();
            return new PressOutcome(ClosePress, nonce, Now, [], 0, true, "There was nothing open to close.");
        }

        // See OperatorCancelAllAsync: the plan is written down before any close goes out.
        BeginComposite(AgentContext.Operator, PressPrefix(ClosePress, nonce), Ops.CloseAll,
            captured.Select(p => p.Symbol).ToList(), () => nonce);

        var drifted = new List<string>();

        // WHICH ROW CARRIES THE CLAIM. Close-all has no press-level row — its records are one per
        // position — so the claim rides on the first row this press actually writes, and only that
        // one: a press must not be blocked by its own second symbol. Every later row is an ordinary
        // flagged write-ahead.
        var claimed = false;
        foreach (var (symbol, quantity) in captured)
        {
            // THE POSITION IS READ AGAIN IMMEDIATELY BEFORE THE WIRE CALL, and a press that finds it
            // changed sends nothing for that instrument (Codex round-3 F10).
            //
            // The press captured a size and a side and turned them into a MARKET order for that
            // size. Between the capture and this call a fill can land, a hedge can close, another
            // window can flatten it — and the order this press computed is then wrong in the one
            // direction that matters: closing 2 of a position that is now 1 opens a short, and
            // closing a long that has already flipped short doubles it. The old code sent whatever
            // it had captured and let ATAS's close-position sort it out.
            //
            // Refused rather than recomputed. A different position is a different decision, and the
            // owner makes decisions here — they press again, against what is actually there.
            decimal? live = null;
            Exception? unreadable = null;
            try
            {
                live = (await Connector.GetPositionsAsync(accountId, ct))
                    .FirstOrDefault(p => p.Symbol == symbol)?.Quantity ?? 0m;
            }
            catch (Exception ex) { unreadable = ex; }

            // A DEFINITE DIFFERENT ANSWER AND NO ANSWER AT ALL ARE NOT THE SAME NEWS, and they get
            // different treatment. A position the platform says is 1 when the press captured 2 is a
            // changed decision: no record, nothing sent, and the owner is told to press again. A
            // read that never came back tells us nothing about anything — including whether this
            // press ought to be over — so it gets a record, and the record pauses trading.
            if (unreadable is null && live != quantity)
            {
                drifted.Add($"{symbol} was {quantity} when you pressed and is {live} now");
                continue;
            }

            var rid = $"{PressPrefix(ClosePress, nonce)}-{symbol}";
            var intent = new PlaceIntent(symbol, quantity > 0 ? OrderSide.Sell : OrderSide.Buy,
                OrderType.Market, Math.Abs(quantity), null, null, TimeInForce.Day, "close position (you)")
                { Intent = OrderIntent.Close };
            var current = OpenPressRow(rid, accountId, RequestIntent.PLACE, symbol, Json.Write(intent), paused,
                claims: claimed ? null : ClosePress);
            claimed = true;

            if (unreadable is { } readFailed)
            {
                SafelyRecordIndefinite(rid, readFailed.Message,
                    $"TradeAgent could not check your {symbol} position before closing it, so nothing was sent for it.",
                    readFailed);
                continue;
            }

            OrderInfo? order;
            try
            {
                using var dispatch = TransportLedger.MarkDispatch();
                order = await Connector.ClosePositionAsync(accountId, symbol, current.ClientOrderId, ct);
            }
            catch (Exception ex)
            {
                // ONE POSITION FAILING SAYS NOTHING ABOUT THE NEXT ONE. This used to rethrow, so a
                // press that hit trouble on the first symbol left every other position open and
                // unrecorded — an emergency control that stops half way through the emergency.
                SafelyRecordIndefinite(rid, ex.Message,
                    $"TradeAgent could not confirm whether the close of {symbol} reached the platform.", ex);
                continue;
            }

            if (order is null)
            {
                // No order came back. The one implementation that returns null means "there was no
                // position to close", but a connector that submitted the close and could not read it
                // back looks identical from here, and the SDK does not say which. Unknown it is.
                SafelyRecordIndefinite(rid, "the platform returned no order for the close",
                    $"TradeAgent could not confirm whether {symbol} was closed.");
                continue;
            }

            var (to, indefinite) = MapDispatchOutcome(order.State);
            if (indefinite)
                SafelyRecordIndefinite(rid, $"the platform answered {order.State} for the close",
                    $"The platform answered {order.State} when closing {symbol}, which is not something TradeAgent can record as done.",
                    connectorOrderId: order.ConnectorOrderId);
            else
                // SAFELY. `Settle` throws when the store cannot write the outcome down, and this
                // loop used to let that escape — abandoning every position after this one, in the
                // one situation where finishing matters most (the previous unit's residual).
                SafelySettle(rid, to, order.ConnectorOrderId, order.FilledQuantity);
        }

        var drift = drifted.Count == 0 ? ""
            : $" Nothing was sent for {drifted.Count} of them, because what is there changed after you " +
              $"pressed: {string.Join("; ", drifted)}. Press again if you still want them closed.";

        var outcome = await PressOutcomeAsync(ClosePress, nonce, ct);
        // A press that wrote no rows at all has only the drift to report; "Nothing was sent." twice
        // over is not a sentence anybody should have to read.
        outcome = outcome with { Summary = (outcome.Targets.Count == 0 && drift.Length > 0 ? "" : outcome.Summary) + drift };
        CompleteComposite(PressPrefix(ClosePress, nonce), Json.Write(outcome));
        _log.Activity($"You asked to close {captured.Count} position(s). {outcome.Summary}" +
                      (outcome.Targets.Count > 0
                          ? " AI trading is paused until you confirm what happened on the Dashboard."
                          : ""), "warn");
        StateChanged?.Invoke();
        return outcome;
    }

    // ---------------------------------------------------------------- reconciliation

    void OnOrderChanged(OrderInfo order)
    {
        if (order.ClientOrderId is null) return;
        var req = _requests.GetByClientOrderId(order.ClientOrderId);
        if (req is null || OrderStateMachine.IsTerminal(req.State)) return;

        // A connector may raise OrderChanged from inside PlaceOrderAsync, before that call has even
        // returned — and a real bridge delivers events on its own thread, arriving whenever. So the
        // stream stays out of any request that the dispatcher or the reconciler currently owns.
        // Whoever holds the request writes its outcome; the stream only reports later changes.
        if (req.State is ExecutionState.CREATED or ExecutionState.AWAITING_APPROVAL
                      or ExecutionState.DISPATCHING or ExecutionState.RECONCILING) return;

        if (!OrderStateMachine.CanTransition(req.State, order.State)) return; // the reconciler is the authority, not the stream
        try
        {
            _requests.Transition(req.RequestId, req.State, order.State,
                connectorOrderId: order.ConnectorOrderId, filled: order.FilledQuantity);
            StateChanged?.Invoke();
        }
        catch (TradeAgentException) { /* raced with the dispatcher; reconciliation will settle it */ }
    }

    /// <summary>
    /// Recovers the truth after a disconnect. The rules that matter:
    ///   - nothing is ever resubmitted here;
    ///   - "absent from the broker" only means "never landed" when the backend can prove its own
    ///     history AND enough time has passed; otherwise the request stays unconfirmed and trading
    ///     stays paused, which is the safe direction to fail.
    /// </summary>
    public async Task<ReconcileResult> ReconcileAsync(CancellationToken ct = default)
    {
        // Unreconciled(), not the flag: a record stranded in DISPATCHING is exactly what this method
        // exists to settle, and waiting for a restart to notice was the gap.
        //
        // It can in principle hand the reconciler a record a dispatch is genuinely still holding —
        // only if that dispatch has outlived DispatchStrandedAfter, i.e. three times the connector's
        // own deadline. The result is a lost CAS, not a corrupted record: the dispatcher's Settle
        // finds the row no longer in DISPATCHING, files `already_settled` and returns what is stored,
        // and SettleUnknown falls back to flagging. Nothing is resubmitted on either path.
        var pending = Unreconciled();
        if (pending.Count == 0 && _unconfirmed.Values.FirstOrDefault() is { } latched)
        {
            // The store says there is nothing to confirm and this gateway knows better: an outcome
            // it could not write down. Clearing the pause here would launder that failure into
            // "all clear", so the pass reports itself unfinished instead. It resolves on its own
            // once the aged-dispatch bound exposes the write-ahead row this outcome belongs to.
            _log.Engineering("Reconciler", "unconfirmed_without_a_record", "error",
                metadataJson: Json.Write(new { reason = latched }));
            _health.Set(Components.ExecutionCapability, HealthState.PAUSED, latched);
            StateChanged?.Invoke();
            return new ReconcileResult(0, 1, [$"{latched}, and no record of it could be written; trading stays paused"]);
        }

        if (pending.Count == 0)
        {
            // NOTHING PENDING IS AN OUTCOME, NOT A REASON TO SKIP THE ROW. The all-resolved path
            // below clears ExecutionCapability; returning early without doing the same left
            // "reconcile until clean" unable to actually finish — a caller that only ever calls
            // ReconcileAsync watched execution stay PAUSED on a book with nothing left to confirm.
            // In the app the background health tick hid this; anything driving the gateway directly
            // saw it. Same two lines the sibling path uses, so the two cannot drift apart again.
            _health.Set(Components.ExecutionCapability, HealthState.READY);
            StateChanged?.Invoke();
            return new ReconcileResult(0, 0, []);
        }

        if (!await Connector.IsConnectedAsync(ct))
            return new ReconcileResult(0, pending.Count, ["connector is offline; cannot reconcile yet"]);

        int resolved = 0, inconclusive = 0;
        var details = new List<string>();

        foreach (var req in pending)
        {
            ct.ThrowIfCancellationRequested();

            // EVIDENCE ABOUT ITS OWN TARGET MEANS ON ITS OWN PLATFORM, and this is the first thing
            // asked because nothing below it can be read without it. `SwitchConnectorAsync` builds a
            // fresh gateway over the SAME database, so every record the previous platform left
            // behind is handed to a reconciler talking to a different broker — and an account id is
            // unique only within a platform, so `GetOrdersAsync(req.AccountId, ...)` answers
            // confidently about somebody else's book. The absence rule then read "no such order" as
            // "the cancel landed" and wrote CANCELLED over a target still working at the platform it
            // was actually sent to (Codex round-3 F1).
            //
            // It stays flagged and keeps trading paused: this is not evidence of anything, and the
            // record is settled by switching back, or by the owner's card.
            if (!string.Equals(req.ConnectorId, Connector.Id, StringComparison.Ordinal))
            {
                inconclusive++;
                details.Add($"{req.RequestId}: placed on {req.ConnectorId}; connected to {Connector.Id}");
                continue;
            }

            // A LIVE DISPATCHER OWNS ITS ROW, AND THIS IS THE ONE FACT THE AGE COULD NOT SUPPLY.
            //
            // A DISPATCHING record on disk looks identical whether a handler is inside the connector
            // call right now or died in it last week — it is written before the call and overwritten
            // by the answer — so the reconciler judged it by age alone and, at the shipped deadlines,
            // judged the whole 30..50 s window wrong. It moved the row to UNKNOWN and on to
            // RECONCILING under a handler that was still waiting for the broker, wrote off the order
            // as "never reached the broker", and the real answer was then filed `already_settled`
            // (REVIEW 2026-09-05 finding 1 and UNVERIFIED 4).
            //
            // The lease is in memory, and that is what makes it safe rather than a hole: a claim
            // that outlived the process holding it would be a claim nothing could ever release, so a
            // genuinely abandoned record — a crash, a restart, an update — has no claim on it at the
            // next start and reconciles at the bound like any other.
            //
            // It does NOT lift the pause. A dispatch that has outlived the bound is still unconfirmed
            // work and trading stays refused over it; what it is not is a record anybody else may
            // settle.
            if (_dispatches.TryGetValue(req.RequestId, out var span) && span.Live)
            {
                inconclusive++;

                // THE TWO NUMBERS THE OWNER NEEDS, and neither of them is "in progress". A person
                // reading a paused machine is deciding whether to wait or to go and look in the
                // platform, and that decision is how long THIS dispatch has been on the wire against
                // how long the connector says ONE call can possibly take. Past the second figure the
                // connector has overrun its own claim, which is the moment to go and look.
                details.Add($"{req.RequestId}: still on the wire for {(Now - span.Started).TotalSeconds:0}s " +
                            $"of a possible {Connector.WorstCaseOperationPath.TotalSeconds:0}s");
                continue;
            }

            // AN EMERGENCY PRESS IS RESOLVED BY THE PERSON WHO MADE IT, AND BY NOBODY ELSE.
            //
            // Its rows are flagged from the write-ahead, not because anything went wrong, but
            // because the press is not over until the owner has read what it did. Letting the
            // reconciler at them would undo that twice over: it would clear the flag the moment the
            // platform's answer looked definite — releasing a press whose position is still open —
            // and, on the way, it drags a row through UNKNOWN and RECONCILING, so the card would
            // show "not confirmed" for a close the platform had plainly answered WORKING. The
            // record keeps the platform's own word; the flag waits for a human.
            if (IsPressRecord(req.RequestId))
            {
                inconclusive++;
                details.Add($"{req.RequestId}: {PressName(PressKindOf(req.RequestId))} is waiting for you on the Dashboard");
                continue;
            }

            var state = req.State;
            if (state != ExecutionState.UNKNOWN && state != ExecutionState.RECONCILING)
            {
                if (!OrderStateMachine.CanTransition(state, ExecutionState.UNKNOWN)) { inconclusive++; continue; }
                _requests.Transition(req.RequestId, state, ExecutionState.UNKNOWN, needsReconciliation: true);
                state = ExecutionState.UNKNOWN;
            }
            if (state == ExecutionState.UNKNOWN)
            {
                _requests.Transition(req.RequestId, ExecutionState.UNKNOWN, ExecutionState.RECONCILING);
            }

            if (!Connector.Capabilities.ReconciliationProvable)
            {
                inconclusive++;
                details.Add($"{req.RequestId}: {Connector.DisplayName} cannot prove order state; needs a human to look");
                continue;
            }

            try
            {
                var since = req.CreatedAt - TimeSpan.FromMinutes(5);

                // A REQUEST THAT NEVER SENT ITS OWN CLIENT ORDER ID CANNOT BE FOUND BY IT. Only a
                // PLACE carries `TA-{requestId}` onto an order at the broker; a CANCEL and a MODIFY
                // transmit the TARGET's broker id, and a cancel-all transmits nothing but the
                // account. Matching those on ClientOrderId therefore always missed, and the absence
                // rule below then read "no order exists" and wrote CANCELLED — a cancellation
                // recorded as done, and trading resumed, over an order still working at the broker.
                if (req.Intent is RequestIntent.CANCEL or RequestIntent.MODIFY or RequestIntent.CANCEL_ALL)
                {
                    var (settled, note) = await ReconcileByTargetAsync(req, ct);
                    if (settled) resolved++; else inconclusive++;
                    details.Add($"{req.RequestId}: {note}");
                    continue;
                }

                var orders = await Connector.GetOrdersAsync(req.AccountId, true, since, ct);
                var match = orders.FirstOrDefault(o => o.ClientOrderId == req.ClientOrderId);

                if (match is not null)
                {
                    if (Adopt(req.RequestId, match))
                    {
                        resolved++;
                        details.Add($"{req.RequestId}: broker has it as {match.State}");
                        _log.Activity($"Order confirmed with the broker: {match.State}");
                    }
                    else
                    {
                        inconclusive++;
                        details.Add(IsDefinite(match.State)
                            ? $"{req.RequestId}: broker reports {match.State}, which does not fit our record"
                            : $"{req.RequestId}: broker reports {match.State}, which settles nothing");
                    }
                    continue;
                }

                var fills = await Connector.GetExecutionsAsync(req.AccountId, since, ct);
                if (fills.Any(f => f.ClientOrderId == req.ClientOrderId))
                {
                    var qty = fills.Where(f => f.ClientOrderId == req.ClientOrderId).Sum(f => f.Quantity);
                    _requests.Transition(req.RequestId, ExecutionState.RECONCILING, ExecutionState.FILLED,
                        filled: qty, needsReconciliation: false, markReconciled: true);
                    resolved++;
                    details.Add($"{req.RequestId}: found a fill for {qty}");
                    continue;
                }

                // Both ends of this subtraction come from GatewayOptions.Clock: `DispatchedAt` is
                // written by ExecutionRequestStore, which this gateway hands its own clock to. The
                // near end is the LATER of the dispatch and the bound — see AbsenceCountsFrom.
                var age = Now - AbsenceCountsFrom(req);
                if (age >= _opt.AbsenceGrace)
                {
                    // Absent from a backend that can prove its own history, long enough after dispatch.
                    // CANCELLED is the truthful mapping: not working, never filled, nothing to undo.
                    _requests.Transition(req.RequestId, ExecutionState.RECONCILING, ExecutionState.CANCELLED,
                        needsReconciliation: false, markReconciled: true, error: "never reached the broker");
                    resolved++;
                    details.Add($"{req.RequestId}: never reached the broker; no order exists");
                    _log.Activity("An order never reached the broker. Nothing was placed.");
                }
                else
                {
                    inconclusive++;
                    details.Add($"{req.RequestId}: not visible yet, still inside the {_opt.AbsenceGrace.TotalSeconds:0}s grace window");
                }
            }
            catch (Exception ex)
            {
                inconclusive++;
                details.Add($"{req.RequestId}: reconciliation could not complete ({ex.Message})");
                _log.Engineering("Reconciler", "reconcile_failed", "warn", requestId: req.RequestId, ex: ex);
            }
        }

        ReleaseLatchesTheStoreCanVouchFor();

        if (inconclusive == 0 && _unconfirmed.IsEmpty)
        {
            // Everything this pass found has a definite outcome now — and each request that got one
            // had its own latch entry lifted where it was settled, not here. A latch this pass never
            // met keeps trading paused.
            _log.Activity("Orders reconciled");
            _health.Set(Components.ExecutionCapability, HealthState.READY);
        }
        else
        {
            _health.Set(Components.ExecutionCapability, HealthState.PAUSED, $"{inconclusive} request(s) unconfirmed");
        }
        StateChanged?.Invoke();
        return new ReconcileResult(resolved, inconclusive, details);
    }

    // What a CANCEL / MODIFY / CANCEL_ALL record stored about the order it was aimed at. Read back
    // rather than re-derived: the target is the only evidence these requests leave behind.
    //
    // The `Was*` fields are the target as it stood BEFORE the change, written by the dispatcher.
    // They are absent on every record written before they existed, and on any record whose book
    // could not be read at the time, so nothing may require them — they sharpen the verdict when
    // they are there and are simply not checked when they are not.
    sealed record TargetRef(string? Order, decimal? Quantity, decimal? LimitPrice, decimal? StopPrice,
        string? Symbol = null, string? Account = null, decimal? WasLimit = null, decimal? WasStop = null);

    /// <summary>
    /// Reconciles a request whose outcome lives in ANOTHER order's state.
    ///
    /// The question is never "does an order with our id exist" — it is "did the thing we asked for
    /// happen to the order we named". So the target is looked up by ITS broker id and read:
    ///   - a cancel whose target is cancelled, or gone from a history the platform can prove, landed;
    ///   - a cancel whose target is still working, or filled, or refused, did NOT land — the request
    ///     is REJECTED and the caller may ask again under a new id. It is never CANCELLED, which
    ///     would assert that an order still live at the broker had been withdrawn;
    ///   - a cancel whose target is CANCEL_PENDING is genuinely undecided and stays unconfirmed;
    ///   - a modify is applied only if the target carries the values that were asked for;
    ///   - a cancel-all is judged by what is left on the account's book.
    /// </summary>
    // THERE IS NO "HELD STILL" WATCH HERE ANY MORE, AND ITS ABSENCE IS THE POINT.
    //
    // Round 2 kept a signature of the target and the time it was first seen, and read "the same
    // face for a whole grace window" as proof that the cancellation had been refused. That is the
    // rule inverted. An order that has not moved is an order the platform has said NOTHING about;
    // stillness is the absence of an answer, and no amount of waiting turns an absent answer into a
    // definite one. The failure it produced is the one this whole method exists to prevent: a live
    // cancellation recorded as definitely REJECTED, the flag off, trading resumed, while the target
    // sat working at the platform with our acknowledgement still in flight to it.
    //
    // What settles a cancel is what the platform actually asserts about the target — terminal, or
    // absent past the grace on a backend that can prove its history — a definite refusal of the
    // cancel itself, or the owner's card. Nothing else, however long it is watched.

    /// <summary>A state the platform is asserting, as opposed to one that says it does not know.</summary>
    static bool IsDefinite(ExecutionState s) =>
        s is not (ExecutionState.UNKNOWN or ExecutionState.DISPATCHING or ExecutionState.RECONCILING
                  or ExecutionState.CANCEL_PENDING);

    /// <summary>
    /// Reconciles a request whose outcome lives in ANOTHER order's state.
    ///
    /// THE RULE, and every branch below is derived from it: a request leaves the unconfirmed set only
    /// on positive, definite, stable evidence about ITS OWN target. Anything else is inconclusive and
    /// keeps trading paused. Concretely —
    ///   - the target is looked up by id against the WHOLE book, never a window: a window measured
    ///     from the cancel's own creation time does not contain an order that has rested longer than
    ///     it, and "not in the window" became "no such order, so the cancel landed";
    ///   - absence only counts after AbsenceGrace has passed since this operation was dispatched,
    ///     exactly as it does for a place;
    ///   - a target that is UNKNOWN, DISPATCHING, RECONCILING or CANCEL_PENDING decides nothing;
    ///   - "the cancel did not land" needs a TERMINAL target, a definite refusal, or the owner's
    ///     card. A target that is merely working — for one sighting or for an hour of them — is the
    ///     platform saying nothing, because its acknowledgement can arrive after our RPC gave up;
    ///   - a modify is confirmed only by the target carrying what was asked for; it is never recorded
    ///     as a definite failure without a definite refusal;
    ///   - a cancel-all is judged on the orders the press captured, not on whatever is live now.
    /// </summary>
    async Task<(bool Settled, string Note)> ReconcileByTargetAsync(ExecutionRequest req, CancellationToken ct)
    {
        var stored = Json.Read<TargetRef>(req.ParametersJson);
        var orders = await Connector.GetOrdersAsync(req.AccountId, true, null, ct);
        var grace = _opt.AbsenceGrace;
        var age = Now - AbsenceCountsFrom(req);      // the later of the dispatch and the bound

        ExecutionRequest Resolve(ExecutionState to, string why)
        {
            var r = _requests.Transition(req.RequestId, ExecutionState.RECONCILING, to,
                needsReconciliation: false, markReconciled: true, error: why);
            ClearLatch(req.RequestId);
            _log.Engineering("Reconciler", "target_reconciled", requestId: req.RequestId,
                metadataJson: Json.Write(new { intent = req.Intent.ToString(), state = to.ToString(), why }));
            return r;
        }

        // ---- a record that never named a target
        //
        // A cancel-all press used to be judged here, on the set its own per-order rows captured. It
        // does not reach the reconciler at all any more — a press writes flagged rows and is
        // resolved by the person who made it — so the captured-set machinery went with it. What is
        // left in this arm is a record from an older build whose stored parameters name no order,
        // and for that the whole book is the only evidence there is.
        if (stored?.Order is null)
        {
            // A BOOK OF ORDERS THE PLATFORM WILL NOT COMMIT TO IS NOT AN EMPTY BOOK. `IsDefinite`
            // used to be a filter on the live set, so a CANCEL_PENDING or UNKNOWN order dropped OUT
            // of the count and `live.Count == 0` read as "nothing is working" — the rule backwards:
            // the absence of definite evidence became the presence of it (Codex round-3 F2).
            var undecided = orders.Where(o => !IsDefinite(o.State)).ToList();
            if (undecided.Count > 0)
                return (false, $"the platform will not say what happened to {undecided.Count} order(s) " +
                               $"({string.Join(", ", undecided.Select(o => $"{o.ConnectorOrderId} is {o.State}"))})");

            var live = orders.Where(o => !OrderStateMachine.IsTerminal(o.State) && IsDefinite(o.State)).ToList();
            if (live.Count > 0)
                return (false, $"{live.Count} order(s) are working and none can be attributed to this record");

            if (age < grace)
                return (false, $"the account still has to settle inside the {grace.TotalSeconds:0}s grace window");

            Resolve(ExecutionState.CANCELLED, "no working orders are left on the account");
            return (true, "no working orders are left on the account");
        }

        var target = stored.Order;
        var match = orders.FirstOrDefault(o => o.ConnectorOrderId == target);

        // ---- a cancel
        if (req.Intent == RequestIntent.CANCEL)
        {
            if (match is null)
            {
                if (age < grace)
                    return (false, $"order {target} is not listed yet, still inside the {grace.TotalSeconds:0}s grace window");
                Resolve(ExecutionState.CANCELLED, $"the platform does not list order {target} at all");
                return (true, $"order {target} is not on the account; nothing is working");
            }
            if (match.State == ExecutionState.CANCELLED)
            {
                Resolve(ExecutionState.CANCELLED, $"the platform has order {target} cancelled");
                return (true, $"the platform has order {target} cancelled");
            }
            if (!IsDefinite(match.State))
                return (false, $"the platform reports order {target} as {match.State}, which settles nothing");

            if (OrderStateMachine.IsTerminal(match.State))
            {
                Resolve(ExecutionState.REJECTED, $"order {target} is {match.State}; the cancellation did not take effect");
                return (true, $"order {target} is {match.State}; the cancellation did not take effect");
            }

            // Working, and that is all it is. Not an answer about the cancel, now or after any
            // number of grace windows — see the note where the settle watch used to be.
            return (false, $"order {target} is {match.State}, which is not the platform refusing the cancellation");
        }

        // ---- a modify
        if (match is null)
            return (false, $"order {target} is not on the account, so the change cannot be confirmed");

        await EnsureInstrumentsAsync(ct);
        var asked = new ModifyOrderCommand(target, stored.Quantity, stored.LimitPrice, stored.StopPrice);
        // The same facts the dispatcher judged on, out of the record it wrote. The account falls back
        // to the request's own, which every record carries.
        var was = new TargetFacts(stored.Symbol, stored.Account ?? req.AccountId, stored.WasLimit, stored.WasStop);
        if (CheckModification(asked, match, was) == ModifyVerdict.Applied)
        {
            Resolve(ExecutionState.ACKNOWLEDGED, $"order {target} carries the change");
            return (true, $"order {target} carries the change");
        }

        // NEVER a definite failure without a definite refusal. A platform that rounds, or reports a
        // quantity we cannot read as total-or-remaining, is not the platform saying no.
        return (false, $"order {target} is {match.State} and does not show the change; a person has to look");
    }

    /// <summary>
    /// Writes the broker's truth onto our record. Returns false only when the two genuinely disagree.
    /// A stream event that already wrote the same state is agreement, not conflict.
    /// </summary>
    bool Adopt(string requestId, OrderInfo match)
    {
        // "I DO NOT KNOW" IS NOT A TRUTH TO WRITE DOWN. `RECONCILING -> UNKNOWN` is a legal
        // transition, so the broker's own UNKNOWN went through the write below with
        // `needsReconciliation: false` and `markReconciled`, and this returned true — the pass
        // counted it `resolved` and took the flag off. `NeedingReconciliation()` is what every gate
        // reads, so the row became invisible, and the in-memory latch still holding trading closed
        // does not survive a restart: the next start traded over an order whose fate was never
        // established (Codex round-3 F3). Nothing below this line may run for a state the platform
        // is not asserting.
        if (!IsDefinite(match.State)) return false;

        if (OrderStateMachine.CanTransition(ExecutionState.RECONCILING, match.State))
        {
            try
            {
                _requests.Transition(requestId, ExecutionState.RECONCILING, match.State,
                    connectorOrderId: match.ConnectorOrderId, filled: match.FilledQuantity,
                    needsReconciliation: false, markReconciled: true);
                return true;
            }
            catch (TradeAgentException ex) when (ex.Code == ErrorCode.ILLEGAL_STATE_TRANSITION)
            {
                // Raced with the event stream. Accept it only if it landed where we were going.
            }
        }

        var actual = _requests.Get(requestId);
        if (actual is not null && actual.State == match.State)
        {
            _requests.ClearReconciliation(requestId);
            return true;
        }
        return false;
    }

    /// <summary>
    /// The human override for a request no machine can settle. Recorded loudly, because it is the
    /// one place a person asserts a fact the software could not prove.
    /// </summary>
    public ExecutionRequest ForceResolve(string requestId, ExecutionState finalState, string note)
    {
        var req = _requests.Get(requestId) ?? throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST, "unknown request");
        var from = req.State;

        // A FLAGGED RECORD IS NOT NECESSARILY AN UNKNOWN ONE, and that is what made this method
        // throw on the case it most needed to handle. `MarkNeedsReconciliation` sets the flag with
        // a bare UPDATE and never touches the state, and `NeedingReconciliation()` queries the flag
        // alone — so `SettleUnknown`'s catch, taken when the event stream already settled a record
        // while a dispatch was in flight, leaves a record that is FILLED *and* flagged. It pauses
        // trading (`TryAuthorizeExecution` counts the flag), and every route below used to end in
        // `Transition(id, FILLED, RECONCILING)`, which the table refuses. The one override designed
        // for a request no machine can settle threw on it. Verified by reading all five links,
        // 2026-09-01.
        if (from == finalState)
        {
            // The person checked, and the record already says what they found. There is nothing to
            // transition and rewriting a terminal state would only destroy the timestamp on it;
            // what is actually stale is the flag. This is the common ending for the race above.
            var confirmed = _requests.ClearReconciliation(requestId);
            ClearLatch(requestId);   // a person has answered the question this latch was holding open
            _log.Activity($"You confirmed order {requestId} as {finalState}: {note}", "warn");
            _log.Engineering("Gateway", "force_resolve_confirmed", "warn", requestId: requestId,
                metadataJson: Json.Write(new { state = finalState.ToString(), note }));
            StateChanged?.Invoke();
            return confirmed;
        }

        if (OrderStateMachine.IsTerminal(from))
        {
            // The record holds a DEFINITE outcome, written from a broker answer, and the person is
            // asserting a different one. That is not a stuck record needing a nudge — it is the
            // stream and the platform disagreeing, and silently overwriting it would erase the only
            // account of what the software was told. Refuse, and name both sides so the conflict is
            // the thing the operator investigates.
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"this order is already recorded as {from}; it cannot be re-resolved as {finalState}. " +
                "If ATAS disagrees with that, the two records genuinely conflict and need looking at, not overriding.");
        }

        if (!OrderStateMachine.CanTransition(from, finalState))
            _requests.Transition(requestId, from, ExecutionState.RECONCILING);
        var result = _requests.Transition(requestId, _requests.Get(requestId)!.State, finalState,
            needsReconciliation: false, markReconciled: true, error: $"resolved by user: {note}");
        ClearLatch(requestId);   // as above: the override answers this one
        _log.Activity($"You confirmed order {requestId} as {finalState}: {note}", "warn");

        // Every other mutator on this class announces itself and this one did not, so the screen that
        // pressed the button was the last thing to learn the record had moved. NOTE the row this does
        // NOT touch: ExecutionCapability is recomputed only by RefreshHealthAsync, so clearing the
        // last flagged record does not by itself resume trading. That is deliberate — this method
        // cannot know what else is wrong with the connection — but it means any caller wanting
        // trading to resume must refresh health afterwards, and the Dashboard card does exactly that.
        StateChanged?.Invoke();
        return result;
    }

    // ---------------------------------------------------------------- health

    public async Task RefreshHealthAsync(CancellationToken ct = default)
    {
        _health.Set(Components.Gateway, HealthState.READY);
        try
        {
            var connState = await Connector.GetHealthAsync(ct);
            _health.Set(Components.TradingConnection, connState);

            if (connState != HealthState.READY)
            {
                _health.Set(Components.Account, HealthState.UNKNOWN, "no connection");
                _health.Set(Components.MarketData, HealthState.UNKNOWN, "no connection");
                _health.Set(Components.ExecutionCapability, HealthState.PAUSED, "no trading connection");
                return;
            }

            // The row is about the account the owner CHOSE, not about whichever one the platform
            // lists first — reporting the fallback as READY is how "nothing is selected" stops being
            // visible anywhere on screen.
            var account = await AccountAsync(ct);
            _health.Set(Components.Account,
                account is null ? HealthState.FAILED
                : Settings.SelectedAccountId is null ? HealthState.DEGRADED
                : HealthState.READY,
                account is null ? "no account"
                : Settings.SelectedAccountId is null ? "no account chosen yet — choose one on the Settings page"
                : account.Id);

            var symbol = Settings.Risk.InstrumentAllowlist.FirstOrDefault()
                         ?? (await InstrumentsAsync(ct)).FirstOrDefault()?.Symbol;
            if (symbol is not null)
            {
                var q = await Connector.GetQuoteAsync(symbol, ct);
                _health.Set(Components.MarketData,
                    q is null ? HealthState.FAILED : q.IsStale(_opt.MaxQuoteAge) ? HealthState.DEGRADED : HealthState.READY,
                    q is null ? "no quote" : q.IsStale(_opt.MaxQuoteAge) ? $"last price is older than {_opt.MaxQuoteAge.TotalSeconds:0}s" : "");
            }

            // An unrecognised mode is in here rather than only in LoadSettings, because this method
            // RECOMPUTES the row from scratch on every five-second pass: a state set once at startup
            // and overwritten a moment later is a claim that does not hold, and the gate and the
            // screen would then disagree about whether this installation can trade at all.
            var unreconciled = Unreconciled().Count;
            var latched = _unconfirmed.Values.FirstOrDefault();
            var unknownMode = !Settings.ModeIsRecognised;
            _health.Set(Components.ExecutionCapability,
                unknownMode || unreconciled > 0 || latched is not null ? HealthState.PAUSED
                : account?.TradingEnabled == true ? HealthState.READY : HealthState.DEGRADED,
                unknownMode ? $"the saved trading mode ({(int)Settings.Mode}) is not one this version knows"
                : unreconciled > 0 ? $"{unreconciled} request(s) unconfirmed" : latched ?? "");
        }
        catch (Exception ex)
        {
            _health.Set(Components.TradingConnection, HealthState.FAILED, ex.Message);
            _health.Set(Components.ExecutionCapability, HealthState.PAUSED, "connection problem");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _health.Changed -= OnHealthChanged;
        Connector.ConnectionChanged -= OnConnectionChanged;
        Connector.OrderChanged -= OnOrderChanged;
        Connector.ExecutionReceived -= OnExecutionReceived;
        _dispatchGate.Dispose();
        await Connector.DisposeAsync();
    }
}

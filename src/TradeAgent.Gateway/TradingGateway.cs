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
        try { return Json.Read<TradeAgentSettings>(json) ?? new TradeAgentSettings(); }
        catch (Exception) { return new TradeAgentSettings(); }
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
    /// Unconfirmed work as this gateway counts it: the flagged records, PLUS any record still in
    /// DISPATCHING longer than a dispatch can legitimately take
    /// (<see cref="GatewayOptions.DispatchStrandedAfter"/>). Everything inside this class that asks
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
        var rows = _requests.NeedingReconciliation(Now - _opt.DispatchStrandedAfter);
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
            (reason, code) = ($"mode is {Settings.Mode}", ErrorCode.MODE_FORBIDS_EXECUTION);
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

        lock (_recentDispatches)
        {
            var cutoff = Now - TimeSpan.FromMinutes(1);
            _recentDispatches.RemoveAll(d => d < cutoff);
            if (_recentDispatches.Count >= r.MaxOrdersPerMinute)
                throw new GatewayDeniedException(ErrorCode.RISK_LIMIT_EXCEEDED,
                    $"{r.MaxOrdersPerMinute} orders per minute is the limit");
        }

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

            return await DispatchPlaceAsync(stored, intent, ct);
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

            _log.Engineering("Gateway", "already_settled", requestId: requestId,
                metadataJson: Json.Write(new { intended = to.ToString(), actual = actual.State.ToString() }));
            return actual;
        }
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
            // than handed a record that does not exist. The log attempt runs off this thread because
            // the store that just refused a write will refuse this one too, for as long as its own
            // timeout — and an order path must not wait out a second one to file a log line.
            _ = Task.Run(async () =>
            {
                // A handful of attempts while whatever held the store lets go. Not a queue and not a
                // guarantee — the in-memory pause is the guarantee; this is so an engineer can find
                // out afterwards WHY trading paused with nothing in the ledger to point at.
                for (var attempt = 0; attempt < 6; attempt++)
                {
                    // Wait before the FIRST attempt too: the store refused a write a moment ago, and
                    // retrying into the same locked file only burns another timeout.
                    await Task.Delay(250);
                    try
                    {
                        _log.Engineering("Gateway", "record_indefinite_failed", "error", requestId: requestId,
                            ex: persist, metadataJson: Json.Write(new { reason = technical, original = ex?.GetType().FullName }));
                        return;
                    }
                    catch (Exception) { /* try again below */ }
                }
            });

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

    async Task<ExecutionRequest> DispatchPlaceAsync(ExecutionRequest stored, PlaceIntent intent, CancellationToken ct)
    {
        // Write-ahead: DISPATCHING is durable before the wire is touched, so a crash mid-flight is
        // recoverable as "we may have sent this" rather than lost entirely.
        var current = _opt.IdempotencyEnabled
            ? _requests.Transition(stored.RequestId, stored.State, ExecutionState.DISPATCHING)
            : stored;

        lock (_recentDispatches) _recentDispatches.Add(Now);

        var cmd = new PlaceOrderCommand(stored.ClientOrderId, stored.AccountId, intent.Symbol, intent.Side,
            intent.Type, intent.Quantity, intent.LimitPrice, intent.StopPrice, intent.Tif, intent.Comment);

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
            var intent = Json.Read<PlaceIntent>(stored.ParametersJson)!;
            var what = $"{intent.Side} {intent.Quantity} {intent.Symbol}";

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

            try
            {
                // The mode it was proposed under is the only mode it may be approved in. PAPER would
                // send a real-money proposal to the simulator, LIVE_AUTONOMOUS would dispatch a
                // confirm-mode order under rules the person never chose for it, OBSERVE forbids all.
                if (Settings.Mode != TradingMode.LIVE_CONFIRM)
                    throw new GatewayDeniedException(ErrorCode.MODE_FORBIDS_EXECUTION,
                        $"mode is now {Settings.Mode}; this order was proposed under {stored.Mode} and can only be approved in LIVE_CONFIRM");

                // Authorized as the AI, never as the operator. A parked record always carries the
                // agent's own session (operator orders are never parked); "agent" stands in if not.
                var proposer = new AgentContext(stored.AgentSessionId ?? "agent");
                if (proposer.IsOperator) proposer = new AgentContext("agent");
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

                await RiskCheckOrThrow(intent, account, ct);
            }
            catch (GatewayDeniedException ex)
            {
                _log.Activity($"{what} was not approved: {ex.Info.UserMessage} ({ex.Message}). It is still waiting for your answer.", "warn");
                _log.Engineering("Gateway", "approval_refused", "warn", requestId: requestId,
                    metadataJson: Json.Write(new { code = ex.Code.ToString(), reason = ex.Message }));
                throw;
            }

            _log.Activity($"You approved {what}");
            return await DispatchPlaceAsync(stored, intent, ct);
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

        var current = _requests.Transition(stored.RequestId, stored.State, ExecutionState.DISPATCHING);
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
            // Same taxonomy as a place, and it did not used to be: this path caught neither
            // TimeoutException nor OperationCanceledException, so a cancel the broker CARRIED OUT
            // could throw on the way home and the ledger would never say the order was cancelled.
            return RecordIndefinite(current.RequestId, ex.Message,
                "TradeAgent could not confirm whether an order was cancelled.", ex);
        }

        _log.Activity($"Cancelled order {target}");
        return Settle(current.RequestId, ExecutionState.CANCELLED);
    }

    public async Task<ExecutionRequest> ModifyAsync(AgentContext ctx, string requestId, string orderRef,
        decimal? quantity, decimal? limitPrice, decimal? stopPrice, CancellationToken ct = default)
    {
        AuthorizeOrThrow(ctx);
        if (!Connector.Capabilities.SupportsModify)
            throw new GatewayDeniedException(ErrorCode.TRADING_PERMISSION_UNAVAILABLE, $"{Connector.DisplayName} cannot modify orders");
        var target = await ResolveConnectorOrderId(orderRef, ct);
        var record = new ExecutionRequest
        {
            RequestId = requestId, AgentSessionId = ctx.SessionId, ConnectorId = Connector.Id,
            AccountId = await RequireAccountId(ct), Instrument = "-", Intent = RequestIntent.MODIFY,
            ParametersJson = Json.Write(new { order = target, quantity, limitPrice, stopPrice }),
            ClientOrderId = ClientOrderIdFor(requestId), CreatedAt = Now,
            State = ExecutionState.CREATED, Mode = Settings.Mode
        };
        var (created, stored) = _requests.TryCreate(record);
        if (!created && _opt.IdempotencyEnabled) return stored;

        var current = _requests.Transition(stored.RequestId, stored.State, ExecutionState.DISPATCHING);
        var command = new ModifyOrderCommand(target, quantity, limitPrice, stopPrice);
        OrderInfo o;
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
            return RecordIndefinite(current.RequestId, ex.Message,
                "TradeAgent could not confirm whether an order was changed.", ex);
        }

        // "IT RETURNED AN ORDER" IS NOT AN ANSWER. This settled ACKNOWLEDGED unconditionally without
        // looking at what came back, so a platform that quietly ignored the request left the ledger
        // saying a stop had been moved when it had not. At dispatch time both "no" and "cannot tell"
        // are unconfirmed; the reconciler is where they part company.
        await EnsureInstrumentsAsync(ct);
        if (CheckModification(command, o) != ModifyVerdict.Applied)
            return RecordIndefinite(current.RequestId,
                $"the platform returned the order as {o.State} qty={o.Quantity} limit={o.LimitPrice?.ToString() ?? "none"} " +
                $"stop={o.StopPrice?.ToString() ?? "none"}, which does not show the change that was asked for",
                "The platform did not show the change TradeAgent asked for on that order.",
                connectorOrderId: o.ConnectorOrderId);

        _log.Activity($"Modified order {target}");
        return Settle(current.RequestId, ExecutionState.ACKNOWLEDGED, connectorOrderId: o.ConnectorOrderId);
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
    /// Did the platform actually do what the modification asked?
    ///
    /// Every field the command NAMED has to come back carrying the value asked for — a null field
    /// asked for nothing and proves nothing — and the order has to still be in a state where a
    /// working modification means anything. A terminal order (it filled, or was cancelled, while the
    /// change was in flight) is not evidence that the change applied; it is evidence that we do not
    /// know at what price the fill happened, which is precisely an UNKNOWN.
    ///
    /// PRICES ARE COMPARED ON THE INSTRUMENT'S OWN GRID. Platforms round a request to the tick, so
    /// asking 4242.13 of an instrument that trades in quarters comes back as 4242.25 — applied, and
    /// the first version of this called it unconfirmed and paused trading over it. The comparison is
    /// against the request rounded to the NEAREST tick, and nothing wider: a tolerance band of one
    /// tick would swallow the case this method exists for, where the platform ignored a small change
    /// and handed back the old price. If the tick size is not known, a differing price is not
    /// evidence either way and says so, rather than being called a definite failure.
    /// </summary>
    ModifyVerdict CheckModification(ModifyOrderCommand cmd, OrderInfo o)
    {
        if (o.State is not (ExecutionState.ACKNOWLEDGED or ExecutionState.WORKING or ExecutionState.PARTIALLY_FILLED))
            return ModifyVerdict.Unknowable;

        // QUANTITY IS NOT A DECIDABLE FIELD. docs/CONTRACTS.md does not say whether OrderInfo.Quantity
        // is the order's total or what is left of it, and connectors differ, so a number that does
        // not match the request is as likely to be a different convention as a refused change.
        if (cmd.Quantity is { } q && o.Quantity != q) return ModifyVerdict.Unknowable;

        var tick = _instrumentCache.FirstOrDefault(i => i.Symbol == o.Symbol)?.TickSize ?? 0m;
        return PriceCarries(o.LimitPrice, cmd.LimitPrice, tick) && PriceCarries(o.StopPrice, cmd.StopPrice, tick)
            ? ModifyVerdict.Applied : ModifyVerdict.Unknowable;
    }

    /// <summary>
    /// Does the price that came back carry the price that was asked for? Platforms put a request on
    /// the instrument's own grid, and in either direction — so a price ON the grid and within one
    /// tick of the request is the request, as applied. Anything further, anything off the grid, and
    /// anything at all when the grid is unknown, is not evidence either way.
    ///
    /// THE COST OF THIS, STATED: asking for a change smaller than one tick and being ignored reads
    /// as applied, because the returned old price is itself within a tick. A change the grid cannot
    /// express is not a change; the alternative — pausing trading on every rounded price — was the
    /// defect this replaces.
    /// </summary>
    static bool PriceCarries(decimal? shown, decimal? asked, decimal tick)
    {
        if (asked is not { } want) return true;        // nothing was asked of this field
        if (shown is not { } have) return false;       // asked for a price, got none
        if (have == want) return true;
        if (tick <= 0m) return false;                  // no grid to judge against
        return Math.Abs(have - want) <= tick && decimal.Remainder(have, tick) == 0m;
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
            null, null, TimeInForce.Day, "close position"), ct);
    }

    async Task<string> ResolveConnectorOrderId(string reference, CancellationToken ct)
    {
        if (_requests.Get(reference) is { ConnectorOrderId: { } coid }) return coid;
        var orders = await Connector.GetOrdersAsync(await RequireAccountId(ct), true, null, ct);
        var hit = orders.FirstOrDefault(o => o.ConnectorOrderId == reference || o.ClientOrderId == reference);
        return hit?.ConnectorOrderId
            ?? throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST, $"no order matches '{reference}'");
    }

    // ---------------------------------------------------------------- emergency controls (operator only)

    /// <summary>
    /// THE IDENTITY OF ONE PRESS. The screen mints one of these per CONFIRMED press and passes it
    /// down, so every close and every cancel that press produces has a request id derived from it:
    /// a retry of the SAME press finds its records already there and sends nothing, while a fresh
    /// press is a fresh decision with fresh ids. Before this, `opclose-{new Guid}` was minted per
    /// CALL, which made idempotency impossible by construction — a close that reached the broker and
    /// then failed left no record at all, and the natural second press reversed the position instead
    /// of flattening it.
    ///
    /// Random rather than sequential because an agent may create any request id it likes over the
    /// pipe: a guessable operator id would let it pre-occupy one and turn the owner's emergency
    /// press into a silent replay.
    /// </summary>
    public static string NewOperatorPressNonce() => Guid.NewGuid().ToString("n")[..16];

    /// <summary>The two emergency controls, as request-id prefixes. One press writes rows under one.</summary>
    public const string ClosePress = "op-close";
    public const string CancelPress = "op-cancel";

    static string PressPrefix(string kind, string nonce) => $"{kind}-{nonce}";

    /// <summary>
    /// How ONE press stands, judged only by the records that press made.
    ///
    /// It used to be judged by <see cref="HasUnconfirmedWork"/> — anything unconfirmed anywhere — so
    /// an unrelated order kept the control locked, and, worse, a press whose own close was still
    /// unconfirmed could be released by someone else's record settling. A press is its own business.
    /// </summary>
    public async Task<PressOutcome> PressOutcomeAsync(string kind, string nonce, CancellationToken ct = default)
    {
        var rows = _requests.Query("request_id LIKE $p", ("$p", $"{PressPrefix(kind, nonce)}%"));
        if (rows.Count == 0)
            return new PressOutcome(nonce, 0, 0, true, "Nothing was sent.");

        var unfinished = rows.Count(r => !OrderStateMachine.IsTerminal(r.State)
                                         || r.NeedsReconciliation
                                         || _unconfirmed.ContainsKey(r.RequestId));

        var open = new List<string>();
        if (kind == ClosePress)
        {
            // A close is not done because its order is done: it is done when the position is flat.
            try
            {
                var targeted = rows.Select(r => r.Instrument).ToHashSet();
                foreach (var p in await Connector.GetPositionsAsync(await RequireAccountId(ct), ct))
                    if (p.Quantity != 0 && targeted.Contains(p.Symbol)) open.Add($"{p.Symbol} {p.Quantity}");
            }
            catch (Exception)
            {
                open.Add("the account could not be read back");
            }
        }

        var complete = unfinished == 0 && open.Count == 0;
        var summary = complete
            ? $"{rows.Count} record(s), all settled."
            : open.Count > 0
                ? $"{unfinished} record(s) still unconfirmed; still open: {string.Join(", ", open)}."
                : $"{unfinished} record(s) from this press are still unconfirmed.";
        return new PressOutcome(nonce, rows.Count, unfinished, complete, summary);
    }

    /// <summary>
    /// The nonce of a press this store still cannot account for, so a restart does not mint a fresh
    /// one over an unresolved close. Without it, closing the app was a way to unlock the control.
    /// </summary>
    public string? OutstandingPressNonce(string kind)
    {
        foreach (var r in _requests.Query("request_id LIKE $p", ("$p", $"{kind}-%")).OrderByDescending(r => r.CreatedAt))
            if (!OrderStateMachine.IsTerminal(r.State) || r.NeedsReconciliation || _unconfirmed.ContainsKey(r.RequestId))
            {
                var parts = r.RequestId.Split('-');
                if (parts.Length >= 3) return parts[2];
            }
        return null;
    }

    /// <summary>
    /// Deliberately separate from the kill switch: stopping the AI must not move money.
    ///
    /// Outside AUTHORIZATION on purpose — this has to work while trading is paused, including while
    /// it is paused by the very records this method writes. What it may NOT do any more is touch the
    /// wire without leaving one.
    /// </summary>
    public async Task<IReadOnlyList<string>> OperatorCancelAllAsync(string? pressNonce = null, CancellationToken ct = default)
    {
        var nonce = pressNonce ?? NewOperatorPressNonce();
        var accountId = await RequireAccountId(ct);

        // A RETRY ACTS ONLY ON WHAT THE FIRST PRESS CAPTURED. The sweep is one call for the whole
        // account, so re-issuing it would cancel orders that arrived after the press — orders the
        // person never asked about. The records this press already wrote are its captured set, and
        // if it has any, there is nothing left for this press to do on the wire.
        if (_opt.IdempotencyEnabled &&
            _requests.Query("request_id LIKE $p", ("$p", $"{PressPrefix(CancelPress, nonce)}%")).Count > 0)
        {
            _log.Engineering("Gateway", "operator_press_replayed", requestId: PressPrefix(CancelPress, nonce));
            return [];
        }

        // What is on the book at the moment of the press, so each order can be written ahead by name.
        // If the platform cannot say, the press is still carried out — an emergency control that
        // refuses because a READ failed is not one — and a single umbrella record stands in for the
        // orders that could not be named. Same when the book looks empty: the sweep is still sent,
        // because "the list came back empty" is not proof there is nothing to cancel.
        List<string>? listed = null;
        try { listed = (await Connector.GetOrdersAsync(accountId, false, null, ct)).Select(o => o.ConnectorOrderId).ToList(); }
        catch (Exception ex) { _log.Engineering("Gateway", "cancel_all_order_list_failed", "warn", ex: ex); }

        // The press itself gets a record (target null), and each order that could be named gets one
        // of its own. The press-level record is what makes a RETRY recognisable as a retry even when
        // the orders it cancelled have already left the book — without it, pressing again after a
        // successful sweep would find an empty book, write nothing, and touch the wire a second time.
        var targets = new string?[] { null }.Concat(listed?.Select(id => (string?)id) ?? []);
        var open = new List<(string RequestId, string? Target)>();
        foreach (var target in targets)
        {
            var rid = target is null ? $"op-cancel-{nonce}" : $"op-cancel-{nonce}-{target}";
            var (created, stored) = _requests.TryCreate(OperatorRecord(rid, accountId,
                target is null ? RequestIntent.CANCEL_ALL : RequestIntent.CANCEL, "-",
                Json.Write(new { order = target, press = nonce })));
            if (!created && _opt.IdempotencyEnabled) continue;      // the same press, pressed twice
            // `created` is the guard on the write-ahead, not idempotency: with the harness seam off,
            // a repeated press deliberately dispatches again over a record that has already settled,
            // and moving it back to DISPATCHING is not a transition the table allows.
            if (created) _requests.Transition(stored.RequestId, stored.State, ExecutionState.DISPATCHING);
            open.Add((rid, target));
        }

        if (open.Count == 0)
        {
            _log.Engineering("Gateway", "operator_press_replayed", requestId: $"op-cancel-{nonce}");
            return [];
        }

        IReadOnlyList<string> cancelled;
        try
        {
            cancelled = await Connector.CancelAllOrdersAsync(accountId, ct);
        }
        catch (Exception ex)
        {
            foreach (var (rid, _) in open)
                RecordIndefinite(rid, ex.Message, "TradeAgent could not confirm whether your cancel-all reached the platform.", ex);
            throw;   // the person pressed this button and has to be told it failed
        }

        // EACH RECORD IS SETTLED FROM THE PLATFORM'S ANSWER ABOUT ITS OWN ORDER. The sweep returning
        // without an exception says the call was made; it does not say what happened to any
        // particular order, and a record settled CANCELLED on that basis is a claim nobody made.
        var missed = 0;
        foreach (var (rid, target) in open)
        {
            if (target is null) continue;                       // the press-level record, settled below
            if (cancelled.Contains(target))
            {
                Settle(rid, ExecutionState.CANCELLED, error: "the platform listed this order among the ones it cancelled");
                continue;
            }
            missed++;
            RecordIndefinite(rid, $"the platform did not list {target} among the orders it cancelled",
                $"Order {target} was not among the ones the platform reported cancelling.");
        }

        if (open.FirstOrDefault(o => o.Target is null).RequestId is { } pressRecord)
        {
            if (missed == 0)
                Settle(pressRecord, ExecutionState.CANCELLED,
                    error: $"the platform reported cancelling {cancelled.Count} order(s)");
            else
                RecordIndefinite(pressRecord, $"{missed} captured order(s) were not in the platform's answer",
                    $"The platform did not account for {missed} of the orders this press asked to cancel.");
        }

        _log.Activity($"You cancelled all working orders ({cancelled.Count})", "warn");
        StateChanged?.Invoke();
        return cancelled;
    }

    /// <summary>
    /// Also deliberately separate: this one does move money, so it is never the same button.
    ///
    /// One write-ahead execution request per position, keyed by the press — the same machinery the
    /// agent's own close goes through, which recorded UNKNOWN and paused while this button recorded
    /// nothing at all.
    /// </summary>
    /// <returns>How many of the positions it tried to close are confirmed flat afterwards.</returns>
    public async Task<int> OperatorCloseAllAsync(string? pressNonce = null, CancellationToken ct = default)
    {
        var nonce = pressNonce ?? NewOperatorPressNonce();
        var accountId = await RequireAccountId(ct);
        var positions = await Connector.GetPositionsAsync(accountId, ct);

        // As for cancel-all: a retry may only touch what the FIRST press captured. A position opened
        // after the press was never part of it, and closing it would be a decision nobody made.
        var captured = _requests.Query("request_id LIKE $p", ("$p", $"{PressPrefix(ClosePress, nonce)}-%"));
        var targets = captured.Count > 0
            ? captured.Select(r => (Symbol: r.Instrument,
                Quantity: Json.Read<PlaceIntent>(r.ParametersJson)?.Quantity ?? 0m)).ToList()
            : positions.Where(p => p.Quantity != 0).Select(p => (Symbol: p.Symbol, Quantity: p.Quantity)).ToList();

        var attempted = new List<string>();
        var replayed = 0;
        var n = 0;

        foreach (var p in targets)
        {
            var rid = $"op-close-{nonce}-{p.Symbol}";
            var intent = new PlaceIntent(p.Symbol, p.Quantity > 0 ? OrderSide.Sell : OrderSide.Buy,
                OrderType.Market, Math.Abs(p.Quantity), null, null, TimeInForce.Day, "close position (you)");
            var (created, stored) = _requests.TryCreate(OperatorRecord(rid, accountId,
                RequestIntent.PLACE, p.Symbol, Json.Write(intent)));
            if (!created && _opt.IdempotencyEnabled)
            {
                replayed++;
                _log.Engineering("Gateway", "operator_press_replayed", requestId: rid);
                continue;
            }
            attempted.Add(p.Symbol);
            // See OperatorCancelAllAsync: `created`, not idempotency, guards the write-ahead.
            var current = created
                ? _requests.Transition(stored.RequestId, stored.State, ExecutionState.DISPATCHING)
                : stored;

            OrderInfo? order;
            try
            {
                order = await Connector.ClosePositionAsync(accountId, p.Symbol, current.ClientOrderId, ct);
            }
            catch (Exception ex)
            {
                // ONE POSITION FAILING SAYS NOTHING ABOUT THE NEXT ONE. This used to rethrow, so a
                // press that hit trouble on the first symbol left every other position open and
                // unrecorded — an emergency control that stops half way through the emergency. The
                // failure is recorded, execution is paused by it, and the loop goes on to the rest.
                SafelyRecordIndefinite(rid, ex.Message,
                    $"TradeAgent could not confirm whether the close of {p.Symbol} reached the platform.", ex);
                continue;
            }
            n++;

            if (order is null)
            {
                // No order came back. The one implementation that returns null means "there was no
                // position to close", but a connector that submitted the close and could not read it
                // back looks identical from here, and the SDK does not say which. Unknown it is.
                SafelyRecordIndefinite(rid, "the platform returned no order for the close",
                    $"TradeAgent could not confirm whether {p.Symbol} was closed.");
                continue;
            }

            var (to, indefinite) = MapDispatchOutcome(order.State);
            if (indefinite)
                SafelyRecordIndefinite(rid, $"the platform answered {order.State} for the close",
                    $"The platform answered {order.State} when closing {p.Symbol}, which is not something TradeAgent can record as done.",
                    connectorOrderId: order.ConnectorOrderId);
            else
                Settle(rid, to, order.ConnectorOrderId, order.FilledQuantity);
        }

        // WHAT WAS SENT IS NOT WHAT WAS DONE. `n` counts closes the platform accepted; a market close
        // is not instantaneous, and one that is merely working has flattened nothing. So the account
        // is read back, and the sentence the owner sees says what is actually left — "You closed all
        // positions" was printed over two closes still resting on the book.
        IReadOnlyList<PositionInfo> after;
        try
        {
            after = await Connector.GetPositionsAsync(accountId, ct);
        }
        catch (Exception ex)
        {
            _log.Activity($"You asked to close {attempted.Count} position(s). {n} close order(s) were accepted, " +
                          "but TradeAgent could not read the account back to confirm what is left.", "warn");
            _log.Engineering("Gateway", "close_all_unverified", "warn", ex: ex);
            StateChanged?.Invoke();
            return 0;
        }

        // On a pure replay `attempted` is empty, so the captured symbols are what "still open" means.
        var watched = attempted.Count > 0 ? attempted : targets.Select(t => t.Symbol).ToList();
        var open = after.Where(p => p.Quantity != 0 && watched.Contains(p.Symbol)).ToList();
        // Counted over what this call actually attempted, so a replay (which attempted nothing)
        // reports zero rather than a negative number of positions closed.
        var flat = attempted.Count(sym => open.All(p => p.Symbol != sym));

        var stillOpen = string.Join(", ", open.Select(p => $"{p.Symbol} {p.Quantity}"));
        _log.Activity(
            // A PRESS THAT SENT NOTHING MUST SAY SO. "You closed all positions (0)" over an open
            // position is the sentence that made a repeated press look like it had worked.
            replayed > 0 && attempted.Count == 0
                ? "You pressed Close all positions again. Nothing further was sent, because the previous press is still unconfirmed" +
                  (open.Count > 0 ? $". Still open: {stillOpen}." : ".")
            : attempted.Count == 0 ? "You pressed Close all positions; there was nothing open to close."
            : open.Count == 0 ? $"You closed all positions ({flat})"
            : $"You asked to close {attempted.Count} position(s); {flat} confirmed flat. Still open: {stillOpen}" +
              ". A close order can rest on the book before it fills.",
            "warn");

        StateChanged?.Invoke();
        return flat;
    }

    /// <summary>The write-ahead row for one thing one press does. Always attributed to the operator.</summary>
    ExecutionRequest OperatorRecord(string requestId, string accountId, RequestIntent intent,
        string instrument, string parametersJson) => new()
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
        };

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
                        details.Add($"{req.RequestId}: broker reports {match.State}, which does not fit our record");
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
                // written by ExecutionRequestStore, which this gateway hands its own clock to.
                var age = Now - (req.DispatchedAt ?? req.CreatedAt);
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
    sealed record TargetRef(string? Order, decimal? Quantity, decimal? LimitPrice, decimal? StopPrice);

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
    /// <summary>
    /// What the target looked like the last time this request was judged, and since when. A cancel
    /// that "did not land" is only proved by a target that stayed put: an acknowledgement can arrive
    /// at the platform after our own RPC gave up, so ONE sighting of a working order proves nothing.
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Signature, DateTimeOffset Since)> _settleWatch = new();

    static string SignatureOf(OrderInfo o) =>
        $"{o.State}|{o.Quantity}|{o.FilledQuantity}|{o.LimitPrice}|{o.StopPrice}";

    /// <summary>True once the target has shown the SAME face for a whole grace window.</summary>
    bool HeldStill(string requestId, string signature)
    {
        var now = Now;
        var entry = _settleWatch.AddOrUpdate(requestId, _ => (signature, now),
            (_, prev) => prev.Signature == signature ? prev : (signature, now));
        return entry.Signature == signature && now - entry.Since >= _opt.AbsenceGrace;
    }

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
    ///   - "the cancel did not land" needs a target that is terminal, or one that has held still
    ///     across a whole grace window — one sighting of a working order is not proof, because the
    ///     platform's acknowledgement can arrive after our RPC gave up;
    ///   - a modify is confirmed only by the target carrying what was asked for; it is never recorded
    ///     as a definite failure without a definite refusal;
    ///   - a cancel-all is judged on the orders the press captured, not on whatever is live now.
    /// </summary>
    async Task<(bool Settled, string Note)> ReconcileByTargetAsync(ExecutionRequest req, CancellationToken ct)
    {
        var stored = Json.Read<TargetRef>(req.ParametersJson);
        var orders = await Connector.GetOrdersAsync(req.AccountId, true, null, ct);
        var grace = _opt.AbsenceGrace;
        var age = Now - (req.DispatchedAt ?? req.CreatedAt);

        ExecutionRequest Resolve(ExecutionState to, string why)
        {
            var r = _requests.Transition(req.RequestId, ExecutionState.RECONCILING, to,
                needsReconciliation: false, markReconciled: true, error: why);
            ClearLatch(req.RequestId);
            _settleWatch.TryRemove(req.RequestId, out _);
            _log.Engineering("Reconciler", "target_reconciled", requestId: req.RequestId,
                metadataJson: Json.Write(new { intent = req.Intent.ToString(), state = to.ToString(), why }));
            return r;
        }

        // ---- a cancel-all is judged on the set the press captured
        if (req.Intent == RequestIntent.CANCEL_ALL || stored?.Order is null)
        {
            // The per-order records this press wrote ARE the captured set; an order that arrived
            // afterwards belongs to nobody's press and must not decide this one.
            var captured = _requests.Query("request_id LIKE $p", ("$p", $"{req.RequestId}-%"))
                .Select(r => Json.Read<TargetRef>(r.ParametersJson)?.Order)
                .Where(id => id is not null)
                .ToHashSet();

            var live = orders
                .Where(o => !OrderStateMachine.IsTerminal(o.State) && IsDefinite(o.State))
                .Where(o => captured.Count == 0 || captured.Contains(o.ConnectorOrderId))
                .ToList();

            if (live.Count == 0)
            {
                if (captured.Count == 0 && age < grace)
                    return (false, $"the account still has to settle inside the {grace.TotalSeconds:0}s grace window");
                Resolve(ExecutionState.CANCELLED,
                    captured.Count == 0 ? "no working orders are left on the account"
                                        : "none of the orders this press captured is working any more");
                return (true, "the orders this press captured are no longer working");
            }

            if (captured.Count == 0)
                return (false, $"{live.Count} order(s) are working and none can be attributed to this press");

            var fingerprint = string.Join(";", live.OrderBy(o => o.ConnectorOrderId).Select(SignatureOf));
            if (!HeldStill(req.RequestId, fingerprint))
                return (false, $"{live.Count} of the captured order(s) are still working; waiting to see them hold still");

            Resolve(ExecutionState.REJECTED,
                $"{live.Count} captured order(s) are still working, so the cancel-all did not take effect");
            return (true, $"{live.Count} captured order(s) still working; the cancel-all did not take effect");
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

            if (!HeldStill(req.RequestId, SignatureOf(match)))
                return (false, $"order {target} is still working; waiting to see it hold still before calling the cancellation failed");

            Resolve(ExecutionState.REJECTED, $"order {target} is still working; the cancellation did not take effect");
            return (true, $"order {target} is still working; the cancellation did not take effect");
        }

        // ---- a modify
        if (match is null)
            return (false, $"order {target} is not on the account, so the change cannot be confirmed");

        await EnsureInstrumentsAsync(ct);
        var asked = new ModifyOrderCommand(target, stored.Quantity, stored.LimitPrice, stored.StopPrice);
        if (CheckModification(asked, match) == ModifyVerdict.Applied)
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

            var unreconciled = Unreconciled().Count;
            var latched = _unconfirmed.Values.FirstOrDefault();
            _health.Set(Components.ExecutionCapability,
                unreconciled > 0 || latched is not null ? HealthState.PAUSED
                : account?.TradingEnabled == true ? HealthState.READY : HealthState.DEGRADED,
                unreconciled > 0 ? $"{unreconciled} request(s) unconfirmed" : latched ?? "");
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

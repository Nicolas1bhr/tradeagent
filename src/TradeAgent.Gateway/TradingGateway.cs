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

    public event Action? StateChanged;

    public TradingGateway(Database db, ITradingConnector connector, HealthRegistry? health = null,
        GatewayOptions? options = null)
    {
        _db = db;
        Connector = connector;
        _requests = new ExecutionRequestStore(db);
        _log = new LogStore(db);
        _health = health ?? new HealthRegistry();
        _opt = options ?? new GatewayOptions();
        Settings = LoadSettings();

        _health.Changed += OnHealthChanged;
        Connector.ConnectionChanged += OnConnectionChanged;
        Connector.OrderChanged += OnOrderChanged;
        Connector.ExecutionReceived += OnExecutionReceived;
    }

    // Named rather than inline so DisposeAsync can detach them again. A gateway that is torn down
    // while still subscribed to a shared HealthRegistry keeps writing into the log after it stops
    // being the authority — two owners of one fact, which is the defect class this design exists to
    // avoid.
    void OnHealthChanged(ComponentHealth h) { _log.Health(h); StateChanged?.Invoke(); }
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
            _requests.Open().Count, _requests.NeedingReconciliation().Count, Settings.Risk);
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

        // Unconfirmed work outranks a healthy-looking connection: check it before health, so the
        // refusal says "an earlier order is unconfirmed" rather than something vaguer.
        var unreconciled = _requests.NeedingReconciliation();
        if (unreconciled.Count > 0)
        {
            (reason, code) = ($"{unreconciled.Count} earlier request(s) are unconfirmed", ErrorCode.TRADING_PAUSED_UNRECONCILED);
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
            var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1);
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
            CreatedAt = DateTimeOffset.UtcNow,
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
    ExecutionRequest SettleUnknown(string requestId, string error)
    {
        try
        {
            return _requests.Transition(requestId, ExecutionState.DISPATCHING, ExecutionState.UNKNOWN,
                needsReconciliation: true, error: error);
        }
        catch (TradeAgentException ex) when (ex.Code == ErrorCode.ILLEGAL_STATE_TRANSITION)
        {
            return _requests.MarkNeedsReconciliation(requestId, error);
        }
    }

    async Task<ExecutionRequest> DispatchPlaceAsync(ExecutionRequest stored, PlaceIntent intent, CancellationToken ct)
    {
        // Write-ahead: DISPATCHING is durable before the wire is touched, so a crash mid-flight is
        // recoverable as "we may have sent this" rather than lost entirely.
        var current = _opt.IdempotencyEnabled
            ? _requests.Transition(stored.RequestId, stored.State, ExecutionState.DISPATCHING)
            : stored;

        lock (_recentDispatches) _recentDispatches.Add(DateTimeOffset.UtcNow);

        var cmd = new PlaceOrderCommand(stored.ClientOrderId, stored.AccountId, intent.Symbol, intent.Side,
            intent.Type, intent.Quantity, intent.LimitPrice, intent.StopPrice, intent.Tif, intent.Comment);

        try
        {
            var order = await Connector.PlaceOrderAsync(cmd, ct);
            var to = order.State switch
            {
                ExecutionState.FILLED => ExecutionState.FILLED,
                ExecutionState.PARTIALLY_FILLED => ExecutionState.PARTIALLY_FILLED,
                ExecutionState.REJECTED => ExecutionState.REJECTED,
                ExecutionState.WORKING => ExecutionState.WORKING,
                _ => ExecutionState.ACKNOWLEDGED
            };
            var final = Settle(current.RequestId, to, order.ConnectorOrderId, order.FilledQuantity);
            _log.Activity($"{intent.Side} {intent.Quantity} {intent.Symbol} -> {to}");
            StateChanged?.Invoke();
            return final;
        }
        catch (ConnectorRejectedException ex)
        {
            // Definitive: the broker said no. Nothing is working, so nothing needs reconciling.
            var final = Settle(current.RequestId, ExecutionState.REJECTED, error: ex.Message);
            _log.Activity($"Order refused by the broker: {ex.Message}", "warn");
            StateChanged?.Invoke();
            return final;
        }
        catch (Exception ex) when (ex is ConnectorTransportException or TimeoutException or OperationCanceledException)
        {
            // Indefinite. The order may be live. Record UNKNOWN, pause trading, reconcile — never retry.
            var final = SettleUnknown(current.RequestId, ex.Message);
            _log.Activity("Connection lost while sending an order. AI trading paused until the order is confirmed.", "warn");
            _log.Engineering("Gateway", "dispatch_unknown", "warn", requestId: current.RequestId, ex: ex);
            _health.Set(Components.ExecutionCapability, HealthState.PAUSED, "an order is unconfirmed");
            StateChanged?.Invoke();
            return final;
        }
    }

    public async Task<ExecutionRequest> ApproveAsync(string requestId, CancellationToken ct = default)
    {
        var stored = _requests.Get(requestId) ?? throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST, "unknown request");
        if (stored.State != ExecutionState.AWAITING_APPROVAL)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST, $"request is {stored.State}, not awaiting approval");
        var intent = Json.Read<PlaceIntent>(stored.ParametersJson)!;
        _log.Activity($"You approved {intent.Side} {intent.Quantity} {intent.Symbol}");
        await _dispatchGate.WaitAsync(ct);
        try { return await DispatchPlaceAsync(stored, intent, ct); }
        finally { _dispatchGate.Release(); }
    }

    public ExecutionRequest Decline(string requestId)
    {
        var stored = _requests.Get(requestId) ?? throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST, "unknown request");
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
            CreatedAt = DateTimeOffset.UtcNow, State = ExecutionState.CREATED, Mode = Settings.Mode
        };
        var (created, stored) = _requests.TryCreate(record);
        if (!created && _opt.IdempotencyEnabled) return stored;

        var current = _requests.Transition(stored.RequestId, stored.State, ExecutionState.DISPATCHING);
        try
        {
            await Connector.CancelOrderAsync(target, ct);
            _log.Activity($"Cancelled order {target}");
            return Settle(current.RequestId, ExecutionState.CANCELLED);
        }
        catch (ConnectorRejectedException ex)
        {
            return Settle(current.RequestId, ExecutionState.REJECTED, error: ex.Message);
        }
        catch (ConnectorTransportException ex)
        {
            _health.Set(Components.ExecutionCapability, HealthState.PAUSED, "a cancellation is unconfirmed");
            return SettleUnknown(current.RequestId, ex.Message);
        }
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
            ClientOrderId = ClientOrderIdFor(requestId), CreatedAt = DateTimeOffset.UtcNow,
            State = ExecutionState.CREATED, Mode = Settings.Mode
        };
        var (created, stored) = _requests.TryCreate(record);
        if (!created && _opt.IdempotencyEnabled) return stored;

        var current = _requests.Transition(stored.RequestId, stored.State, ExecutionState.DISPATCHING);
        try
        {
            var o = await Connector.ModifyOrderAsync(new ModifyOrderCommand(target, quantity, limitPrice, stopPrice), ct);
            _log.Activity($"Modified order {target}");
            return Settle(current.RequestId, ExecutionState.ACKNOWLEDGED, connectorOrderId: o.ConnectorOrderId);
        }
        catch (ConnectorRejectedException ex)
        {
            return Settle(current.RequestId, ExecutionState.REJECTED, error: ex.Message);
        }
        catch (ConnectorTransportException ex)
        {
            _health.Set(Components.ExecutionCapability, HealthState.PAUSED, "a modification is unconfirmed");
            return SettleUnknown(current.RequestId, ex.Message);
        }
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

    /// <summary>Deliberately separate from the kill switch: stopping the AI must not move money.</summary>
    public async Task<IReadOnlyList<string>> OperatorCancelAllAsync(CancellationToken ct = default)
    {
        var ids = await Connector.CancelAllOrdersAsync(await RequireAccountId(ct), ct);
        _log.Activity($"You cancelled all working orders ({ids.Count})", "warn");
        return ids;
    }

    /// <summary>Also deliberately separate: this one does move money, so it is never the same button.</summary>
    public async Task<int> OperatorCloseAllAsync(CancellationToken ct = default)
    {
        var accountId = await RequireAccountId(ct);
        var positions = await Connector.GetPositionsAsync(accountId, ct);
        var n = 0;
        foreach (var p in positions.Where(p => p.Quantity != 0))
        {
            var rid = $"opclose-{Guid.NewGuid():n}";
            await Connector.ClosePositionAsync(accountId, p.Symbol, ClientOrderIdFor(rid), ct);
            n++;
        }
        _log.Activity($"You closed all positions ({n})", "warn");
        return n;
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
        var pending = _requests.NeedingReconciliation();
        if (pending.Count == 0) return new ReconcileResult(0, 0, []);

        if (!await Connector.IsConnectedAsync(ct))
            return new ReconcileResult(0, pending.Count, ["connector is offline; cannot reconcile yet"]);

        int resolved = 0, inconclusive = 0;
        var details = new List<string>();

        foreach (var req in pending)
        {
            ct.ThrowIfCancellationRequested();
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

                var age = DateTimeOffset.UtcNow - (req.DispatchedAt ?? req.CreatedAt);
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

        if (inconclusive == 0)
        {
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
        if (!OrderStateMachine.CanTransition(from, finalState))
            _requests.Transition(requestId, from, ExecutionState.RECONCILING);
        var result = _requests.Transition(requestId, _requests.Get(requestId)!.State, finalState,
            needsReconciliation: false, markReconciled: true, error: $"resolved by user: {note}");
        _log.Activity($"You confirmed order {requestId} as {finalState}: {note}", "warn");
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

            var account = await AccountAsync(ct);
            _health.Set(Components.Account, account is null ? HealthState.FAILED : HealthState.READY, account?.Id ?? "no account");

            var symbol = Settings.Risk.InstrumentAllowlist.FirstOrDefault()
                         ?? (await InstrumentsAsync(ct)).FirstOrDefault()?.Symbol;
            if (symbol is not null)
            {
                var q = await Connector.GetQuoteAsync(symbol, ct);
                _health.Set(Components.MarketData,
                    q is null ? HealthState.FAILED : q.IsStale(_opt.MaxQuoteAge) ? HealthState.DEGRADED : HealthState.READY,
                    q is null ? "no quote" : q.IsStale(_opt.MaxQuoteAge) ? $"last price is older than {_opt.MaxQuoteAge.TotalSeconds:0}s" : "");
            }

            var unreconciled = _requests.NeedingReconciliation().Count;
            _health.Set(Components.ExecutionCapability,
                unreconciled > 0 ? HealthState.PAUSED
                : account?.TradingEnabled == true ? HealthState.READY : HealthState.DEGRADED,
                unreconciled > 0 ? $"{unreconciled} request(s) unconfirmed" : "");
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

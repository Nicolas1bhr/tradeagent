using TradeAgent.ConnectorSdk;
using TradeAgent.Core;

namespace TradeAgent.Connectors.Fake;

/// <summary>
/// The first connector, and permanently the test harness. Everything above it — gateway, trade CLI,
/// agent, UI — is developed and fault-tested against this before ATAS is involved at all.
/// </summary>
public sealed class FakeConnector(FakeBroker? broker = null, FaultProfile? faults = null) : ITradingConnector
{
    public FakeBroker Broker { get; } = broker ?? new FakeBroker();
    public FaultProfile Faults { get; } = faults ?? new FaultProfile();

    public string Id => "fake";

    /// <summary>
    /// In-process and unbounded by any wire, so the only thing that can make one call take time is a
    /// deliberately injected latency fault. It is reported rather than assumed to be zero, because a
    /// shutdown drain derived from it has to cover the faults the tests inject.
    ///
    /// THE TWO LATENCIES ADD, THEY DO NOT COMPETE. <see cref="Wire"/> awaits them one after the
    /// other, so a profile with both set costs the sum — and this said <c>Math.Max</c>, which made
    /// the simulator's own reported worst case shorter than the simulator (Codex round-8 F3). A
    /// harness that under-reports its worst case is worse than one with no figure at all: the
    /// shutdown drain is DERIVED from this number, so the connector used to measure whether the
    /// drain covers a handler was quietly telling the drain to be too short.
    /// </summary>
    public TimeSpan WorstCaseOperationPath
    {
        get => _worstCase ?? TimeSpan.FromMilliseconds(Faults.LatencyMs + Faults.UncancellableLatencyMs);

        // A CONNECTOR THAT UNDER-REPORTS ITS OWN WORST CASE, which is a real shape and not only a
        // test convenience: a vendor SDK call that blocks for longer than the vendor admits is
        // exactly what the shutdown drain's `handlers_did_not_finish` error exists to report.
        //
        // It replaces what the two tests of that error used to do — set a deliberately undersized
        // `HandlerDrainTimeout` — which stopped being possible when an explicit drain was made
        // unable to shorten the bound. Setting it HERE is the more honest fixture anyway: the drain
        // is left to derive itself correctly from what it is told, and what it is told is wrong,
        // which is a situation an operator can actually be in.
        init => _worstCase = value;
    }

    readonly TimeSpan? _worstCase;

    /// <summary>The same two seconds the real connector gives an emergency, so tests measure the rule.</summary>
    public TimeSpan EmergencyBudget { get; init; } = TimeSpan.FromSeconds(2);
    public string DisplayName => "Simulator (built in)";

    public ConnectorCapabilities Capabilities => new(
        IsPaper: Broker.IsSimulated,
        SupportsClientOrderId: true,
        SupportsOrderHistory: !Faults.HideOrderHistory,
        SupportsModify: true,
        SupportsClosePosition: true,
        SupportsStreaming: true);

    public event Action<HealthState>? ConnectionChanged;
    public event Action<QuoteInfo>? QuoteChanged;
    public event Action<OrderInfo>? OrderChanged;
    public event Action<ExecutionInfo>? ExecutionReceived;
    public event Action<PositionInfo>? PositionChanged;
    public event Action<AccountInfo>? AccountChanged;

    public Task ConnectAsync(CancellationToken ct = default)
    {
        ConnectionChanged?.Invoke(Faults.Disconnected ? HealthState.FAILED : HealthState.READY);
        return Task.CompletedTask;
    }

    public Task<HealthState> GetHealthAsync(CancellationToken ct = default) =>
        Task.FromResult(Faults.Disconnected ? HealthState.FAILED : HealthState.READY);

    public Task<bool> IsConnectedAsync(CancellationToken ct = default) => Task.FromResult(!Faults.Disconnected);

    /// <summary>
    /// Simulates the wire. Read paths fail loudly when disconnected; they never invent data.
    ///
    /// <paramref name="mutating"/> is what makes this connector able to answer the question the
    /// gateway cannot: WHERE DID THE FRAME GET TO. Every exit below knows it — a deadline that had
    /// already passed sent nothing at all, a deadline that passed mid-call may have acted — and only
    /// a call that CHANGES something at the broker is worth recording, because a leg is a read to
    /// find its target and then the thing it came to do.
    /// </summary>
    async Task Wire(CancellationToken ct, string op, bool mutating = false)
    {
        // Marked before anything can go wrong, for the reason the shipped connector marks it: an
        // exit nobody enumerated must not leave the record empty, because empty means "no mutation
        // was ever started" and reads as `not-sent`. `Task.Delay(LatencyMs, ct)` below is such an
        // exit — a cancelled mutation used to record nothing at all.
        if (mutating) TransportLedger.Attempt();

        await HonourTheOperationDeadline(ct, op, mutating);

        if (Faults.LatencyMs > 0) await Task.Delay(Faults.LatencyMs, ct);
        if (Faults.UncancellableLatencyMs > 0) await Task.Delay(Faults.UncancellableLatencyMs);
        if (Faults.Disconnected)
        {
            // In-process and provable: the simulator was never reached.
            if (mutating) TransportLedger.Record(TransportOutcome.NothingWritten);
            throw new ConnectorTransportException("simulator is disconnected");
        }

        // A REFUSAL THE CONNECTOR CAN PROVE, modelled on the shipped AtasConnector's pre-gate branch:
        // the operation is over, the frame was never built, and the connection learned nothing. It is
        // a fault rather than a timing race because the timing race is a knife-edge — the resolution
        // has to land INSIDE the deadline and the mutation outside it — and the branch it reaches is
        // one the product really has.
        if (mutating && Faults.Take(f => f.RefuseBeforeSend, (f, v) => f.RefuseBeforeSend = v))
        {
            TransportLedger.Record(TransportOutcome.NothingWritten);
            throw new ConnectorTransportException(
                "it was not sent: the operation ran out of time before this leg's turn came");
        }

        // And the other side of the wire: the frame went out and nothing came back. Fail-closed —
        // it may have acted, which is what UNKNOWN and reconciliation exist for.
        if (mutating && Faults.Take(f => f.LoseAfterSend, (f, v) => f.LoseAfterSend = v))
        {
            TransportLedger.Record(TransportOutcome.PossiblyWritten);
            throw new ConnectorTransportException(
                "the request was sent and no answer came back; it is not known whether it acted");
        }
    }

    /// <summary>
    /// THE SIMULATOR HONOURS THE OPERATION DEADLINE, because a connector that ignored it could not be
    /// used to measure the rule. A real bridge stops waiting and reports UNKNOWN; so does this.
    ///
    /// IT IS ITS OWN METHOD BECAUSE <see cref="PlaceOrderAsync"/> NEEDS IT TOO, and only sometimes. A
    /// placement does not go through <c>Wire</c> — it has its own latency — and it used to be excluded
    /// from this clock outright, on the reasoning that an order which opens exposure has no claim on
    /// it. That reasoning holds for an opening order and is wrong for a CLOSE, which is an offsetting
    /// placement and is the thing an emergency is trying to do. <see cref="PlaceOrderCommand.Intent"/>
    /// is what tells them apart, and this is what the answer buys.
    /// </summary>
    async Task HonourTheOperationDeadline(CancellationToken ct, string op, bool mutating)
    {
        if (RiskReducingScope.DeadlineAt is not { } deadline) return;

        var left = RiskReducingScope.LeftUntil(deadline);
        if (left <= TimeSpan.Zero)
        {
            // PROVABLY nothing was sent: the deadline was already gone before anything was
            // attempted. This is the branch the shipped AtasConnector takes when a leg's turn
            // arrives after the operation is over, and reporting it as an unknown is what sent
            // an owner to hunt for an order that never existed (verifier round-9 F-1).
            if (mutating) TransportLedger.Record(TransportOutcome.NothingWritten);
            throw new ConnectorTransportException(DeadlineSentence(op, mutating,
                "the operation deadline had already passed and nothing was sent to the simulator"));
        }

        // The SUM, because the two delays below run in series. Taking the max let a profile with
        // both set pass a precheck for 1200 ms and then spend 2400 — so the instrument the
        // operation-deadline tests measure with could overrun the very deadline it exists to
        // demonstrate (Codex round-8 F3).
        var wait = TimeSpan.FromMilliseconds(Faults.LatencyMs + Faults.UncancellableLatencyMs);
        if (wait > left)
        {
            await Task.Delay(left, ct);
            // The call was under way when the deadline passed, so it may have acted. Fail-closed.
            if (mutating) TransportLedger.Record(TransportOutcome.PossiblyWritten);
            throw new ConnectorTransportException(DeadlineSentence(op, mutating,
                "the operation deadline passed before the simulator answered"));
        }
    }

    /// <summary>
    /// THE SENTENCE AGREES WITH THE WORD THE LEG WILL CARRY, and that is what this exists for.
    ///
    /// One message was thrown for reads and mutations alike — "it is not known whether it acted" —
    /// so a leg the gateway correctly reports as <c>not-sent</c> carried, in the SAME object, a
    /// sentence telling the owner the outcome was unknown (verifier round-11 L-4, measured through
    /// the real pipe). The word is what the machine reads and the sentence is what the person reads;
    /// they are about the same leg and must not disagree.
    ///
    /// The split is the shipped <c>AtasConnector.EmergencySentence</c>'s, which has distinguished
    /// them since round 7: a MUTATION that was under way may have acted and the owner is told where
    /// to look; a READ that timed out means the operation was never started, and saying so is the
    /// whole content of <c>not-sent</c>.
    /// </summary>
    static string DeadlineSentence(string op, bool mutating, string what) =>
        mutating
            ? $"'{op}' is NOT confirmed — check your positions and orders. {what}; it is not known whether it acted."
            : $"'{op}' could not be read, so the operation was not started. Nothing was placed or cancelled. {what}.";

    public async Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken ct = default)
    { await Wire(ct, "accounts"); return [Broker.Account()]; }

    public async Task<AccountInfo?> GetAccountAsync(string accountId, CancellationToken ct = default)
    { await Wire(ct, "account"); return accountId == Broker.AccountId ? Broker.Account() : null; }

    public async Task<IReadOnlyList<InstrumentInfo>> GetInstrumentsAsync(CancellationToken ct = default)
    {
        await Wire(ct, "instruments");
        return
        [
            new InstrumentInfo("ES", "E-mini S&P 500", "CME", 0.25m, 12.50m, 50m),
            new InstrumentInfo("NQ", "E-mini Nasdaq 100", "CME", 0.25m, 5.00m, 20m),
            new InstrumentInfo("MES", "Micro E-mini S&P 500", "CME", 0.25m, 1.25m, 5m),
        ];
    }

    public async Task<QuoteInfo?> GetQuoteAsync(string symbol, CancellationToken ct = default)
    {
        await Wire(ct, "quote");
        var q = Broker.Quote(symbol, DateTimeOffset.UtcNow - Faults.QuoteAge);
        QuoteChanged?.Invoke(q);
        return q;
    }

    public async Task<IReadOnlyList<PositionInfo>> GetPositionsAsync(string accountId, CancellationToken ct = default)
    { await Wire(ct, "positions"); return Broker.Positions; }

    public async Task<IReadOnlyList<OrderInfo>> GetOrdersAsync(string accountId, bool includeInactive, DateTimeOffset? since, CancellationToken ct = default)
    {
        await Wire(ct, "orders");
        if (Faults.HideOrderHistory && includeInactive)
            throw new ConnectorTransportException("this backend cannot serve order history");
        return Broker.Orders
            .Where(o => includeInactive || !OrderStateMachine.IsTerminal(o.State))
            .Where(o => since is null || o.At >= since)
            .ToList();
    }

    public async Task<IReadOnlyList<ExecutionInfo>> GetExecutionsAsync(string accountId, DateTimeOffset? since, CancellationToken ct = default)
    { await Wire(ct, "executions"); return Broker.Executions.Where(e => since is null || e.At >= since).ToList(); }

    public async Task<OrderInfo> PlaceOrderAsync(PlaceOrderCommand cmd, CancellationToken ct = default)
    {
        // A placement does not go through Wire (it has its own latency), so it marks its own attempt
        // — including the one a `close` leg ends in.
        TransportLedger.Attempt();

        // AND A CLOSING PLACEMENT IS ON THE OPERATION'S CLOCK. It is the thing the emergency came to
        // do, sized from the position it is flattening; only an order that can OPEN exposure is kept
        // off this clock. `Intent` is what says which this is — the side and the quantity cannot.
        if (cmd.Intent is OrderIntent.Close) await HonourTheOperationDeadline(ct, "place", mutating: true);

        if (Faults.LatencyMs > 0) await Task.Delay(Faults.LatencyMs, ct);
        if (Faults.UncancellableLatencyMs > 0) await Task.Delay(Faults.UncancellableLatencyMs);
        if (Faults.Disconnected)
        {
            TransportLedger.Record(TransportOutcome.NothingWritten);
            throw new ConnectorTransportException("simulator is disconnected");
        }

        if (Faults.Take(f => f.RefuseBeforeSend, (f, v) => f.RefuseBeforeSend = v))
        {
            TransportLedger.Record(TransportOutcome.NothingWritten);
            throw new ConnectorTransportException(
                "it was not sent: the operation ran out of time before this leg's turn came");
        }

        // Transport dies before the broker sees it: nothing landed, but we cannot know that here.
        if (Faults.Take(f => f.DropBeforeBrokerAccept, (f, v) => f.DropBeforeBrokerAccept = v))
        {
            TransportLedger.Record(TransportOutcome.PossiblyWritten);
            throw new ConnectorTransportException("connection lost before the order was sent");
        }

        if (Faults.Take(f => f.RejectNext, (f, v) => f.RejectNext = v))
        {
            TransportLedger.Record(TransportOutcome.ReplyReceived);
            var rejected = Broker.Reject(cmd, "insufficient margin (simulated)");
            OrderChanged?.Invoke(rejected);
            throw new ConnectorRejectedException(rejected.RejectReason!);
        }

        var order = Broker.Accept(cmd, Faults.Fill);

        // The broker HAS the order. Now lose the acknowledgement. This is the case that produces
        // duplicate fills in naive clients, and the reason UNKNOWN exists as a first-class state.
        if (Faults.Take(f => f.DropAfterBrokerAccept, (f, v) => f.DropAfterBrokerAccept = v))
        {
            TransportLedger.Record(TransportOutcome.PossiblyWritten);
            throw new ConnectorTransportException("connection lost after the order was accepted");
        }

        TransportLedger.Record(TransportOutcome.ReplyReceived);
        OrderChanged?.Invoke(order);
        foreach (var x in Broker.Executions.Where(e => e.ClientOrderId == cmd.ClientOrderId)) ExecutionReceived?.Invoke(x);
        foreach (var p in Broker.Positions) PositionChanged?.Invoke(p);
        AccountChanged?.Invoke(Broker.Account());
        return order;
    }

    public async Task<OrderInfo> ModifyOrderAsync(ModifyOrderCommand cmd, CancellationToken ct = default)
    {
        await Wire(ct, "modify", mutating: true);
        TransportLedger.Record(TransportOutcome.ReplyReceived);
        var existing = Broker.Orders.FirstOrDefault(o => o.ConnectorOrderId == cmd.ConnectorOrderId)
            ?? throw new ConnectorRejectedException("order not found");
        var updated = existing with
        {
            Quantity = cmd.Quantity ?? existing.Quantity,
            LimitPrice = cmd.LimitPrice ?? existing.LimitPrice,
            StopPrice = cmd.StopPrice ?? existing.StopPrice
        };
        OrderChanged?.Invoke(updated);
        return updated;
    }

    public async Task CancelOrderAsync(string connectorOrderId, CancellationToken ct = default)
    {
        await Wire(ct, "cancel", mutating: true);

        // Past the wire: whatever the broker says next, it answered. A definite refusal is an ANSWER,
        // which is why it is recorded here rather than only on the success path.
        TransportLedger.Record(TransportOutcome.ReplyReceived);

        // A definite refusal from the broker, which is a real thing brokers do and the only way a
        // sweep can honestly report fewer cancellations than attempts.
        if (Faults.Take(f => f.RefuseCancel, (f, v) => f.RefuseCancel = v))
            throw new ConnectorRejectedException("the broker refused the cancellation (simulated)");
        if (!Broker.Cancel(connectorOrderId)) throw new ConnectorRejectedException("order is not cancellable");
    }

    public async Task<IReadOnlyList<string>> CancelAllOrdersAsync(string accountId, CancellationToken ct = default)
    {
        await Wire(ct, "cancel-all", mutating: true);
        TransportLedger.Record(TransportOutcome.ReplyReceived);
        var ids = Broker.Orders.Where(o => !OrderStateMachine.IsTerminal(o.State)).Select(o => o.ConnectorOrderId).ToList();
        foreach (var id in ids) Broker.Cancel(id);
        return ids;
    }

    public async Task<OrderInfo?> ClosePositionAsync(string accountId, string symbol, string clientOrderId, CancellationToken ct = default)
    {
        await Wire(ct, "positions");
        var pos = Broker.Positions.FirstOrDefault(p => p.Symbol == symbol);
        if (pos is null || pos.Quantity == 0) return null;
        return await PlaceOrderAsync(new PlaceOrderCommand(clientOrderId, accountId, symbol,
            pos.Quantity > 0 ? OrderSide.Sell : OrderSide.Buy, OrderType.Market,
            Math.Abs(pos.Quantity), null, null, TimeInForce.Day, "close position")
        { Intent = OrderIntent.Close }, ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

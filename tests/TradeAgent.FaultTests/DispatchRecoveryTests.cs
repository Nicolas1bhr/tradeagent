using System.Text.Json;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Diagnostics;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Fault;

// =================================================================================================
// U2c-1 — dispatch recovery.
//
// One defect class, four proven instances: "only an explicitly flagged UNKNOWN record blocks
// execution, and the wire can be touched without leaving one." Everything here is about the window
// between the write-ahead DISPATCHING row and the record of what the broker actually did.
//
//   A  a crash after the write-ahead strands DISPATCHING unflagged, and nothing sweeps it
//   B  a connector answer the dispatcher does not list becomes ACKNOWLEDGED
//   C  an exception outside the catch taxonomy strands DISPATCHING after the wire was touched
//   D  the operator's emergency controls touch the wire with no record at all
//
// Every test asserts BOTH directions: the unsafe outcome is refused AND the ordinary path still
// works. A gateway that paused on every in-flight order, or refused every close, would satisfy half
// of this file and be useless.
// =================================================================================================

/// <summary>
/// A connector that can answer with a state of the test's choosing, throw AFTER the broker has
/// already acted, ignore a modification, and hang on the wire. The built-in FakeConnector can do
/// none of these: its fault profile injects failures BEFORE or AT the broker, and its return states
/// are whatever the book says. Post-acceptance behaviour is exactly what this unit is about, so the
/// knobs live here rather than in tests/Shared — nothing outside this file needs them.
/// </summary>
sealed class RecoveryConnector(FakeConnector inner) : ITradingConnector
{
    public FakeConnector Inner => inner;
    public Func<OrderInfo, OrderInfo>? RewritePlaced;
    public Exception? ThrowAfterPlace;
    public Exception? ThrowAfterCancel;
    public Exception? ThrowAfterModify;
    public Exception? ThrowAfterClose;
    /// <summary>When set, ThrowAfterClose fires only for this symbol.</summary>
    public string? ThrowAfterCloseSymbol;
    /// <summary>Makes the order positions are visited in deterministic (ES before NQ).</summary>
    public bool SortPositionsBySymbol;
    public Exception? ThrowAfterCancelAll;
    public bool ModifyIgnoresTheRequest;
    public bool CancelDoesNotReachTheBook;
    public bool CancelAllDoesNotReachTheBook;
    /// <summary>Rewrites what the BOOK reports, which is what the reconciler reads.</summary>
    public Func<OrderInfo, OrderInfo>? RewriteBook;
    /// <summary>Runs after the broker accepted and before this connector answers.</summary>
    public Action? OnPlaced;
    public TaskCompletionSource? HangPlace;
    public int Closes;
    public int CancelAlls;

    public string Id => inner.Id;
    public string DisplayName => inner.DisplayName;
    public ConnectorCapabilities Capabilities => inner.Capabilities;

    public Task ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);
    public Task<HealthState> GetHealthAsync(CancellationToken ct = default) => inner.GetHealthAsync(ct);
    public Task<bool> IsConnectedAsync(CancellationToken ct = default) => inner.IsConnectedAsync(ct);
    public Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken ct = default) => inner.GetAccountsAsync(ct);
    public Task<AccountInfo?> GetAccountAsync(string a, CancellationToken ct = default) => inner.GetAccountAsync(a, ct);
    public Task<IReadOnlyList<InstrumentInfo>> GetInstrumentsAsync(CancellationToken ct = default) => inner.GetInstrumentsAsync(ct);
    public Task<QuoteInfo?> GetQuoteAsync(string s, CancellationToken ct = default) => inner.GetQuoteAsync(s, ct);
    public async Task<IReadOnlyList<PositionInfo>> GetPositionsAsync(string a, CancellationToken ct = default)
    {
        var p = await inner.GetPositionsAsync(a, ct);
        return SortPositionsBySymbol ? p.OrderBy(x => x.Symbol, StringComparer.Ordinal).ToList() : p;
    }
    public async Task<IReadOnlyList<OrderInfo>> GetOrdersAsync(string a, bool inc, DateTimeOffset? since, CancellationToken ct = default)
    {
        var orders = await inner.GetOrdersAsync(a, inc, since, ct);
        return RewriteBook is null ? orders : orders.Select(RewriteBook).ToList();
    }
    public Task<IReadOnlyList<ExecutionInfo>> GetExecutionsAsync(string a, DateTimeOffset? since, CancellationToken ct = default) => inner.GetExecutionsAsync(a, since, ct);

    public async Task<OrderInfo> PlaceOrderAsync(PlaceOrderCommand cmd, CancellationToken ct = default)
    {
        var o = await inner.PlaceOrderAsync(cmd, ct);            // the broker HAS it
        OnPlaced?.Invoke();
        if (HangPlace is { } hang) await hang.Task;              // ...and the answer never comes back
        if (ThrowAfterPlace is { } ex) throw ex;                 // ...or this happens instead
        return RewritePlaced?.Invoke(o) ?? o;
    }

    public async Task<OrderInfo> ModifyOrderAsync(ModifyOrderCommand cmd, CancellationToken ct = default)
    {
        var o = ModifyIgnoresTheRequest
            ? (await inner.GetOrdersAsync(inner.Broker.AccountId, true, null, ct)).First(x => x.ConnectorOrderId == cmd.ConnectorOrderId)
            : await inner.ModifyOrderAsync(cmd, ct);
        if (ThrowAfterModify is { } ex) throw ex;
        return o;
    }

    public async Task CancelOrderAsync(string id, CancellationToken ct = default)
    {
        if (!CancelDoesNotReachTheBook) await inner.CancelOrderAsync(id, ct);   // the broker DID cancel
        if (ThrowAfterCancel is { } ex) throw ex;
    }

    public async Task<IReadOnlyList<string>> CancelAllOrdersAsync(string a, CancellationToken ct = default)
    {
        CancelAlls++;
        var ids = CancelAllDoesNotReachTheBook ? [] : await inner.CancelAllOrdersAsync(a, ct);
        if (ThrowAfterCancelAll is { } ex) throw ex;
        return ids;
    }

    public async Task<OrderInfo?> ClosePositionAsync(string a, string s, string coid, CancellationToken ct = default)
    {
        Closes++;
        var o = await inner.ClosePositionAsync(a, s, coid, ct);  // the closing order IS submitted
        if (ThrowAfterClose is { } ex && (ThrowAfterCloseSymbol is null || ThrowAfterCloseSymbol == s)) throw ex;
        return o;
    }

    public event Action<HealthState>? ConnectionChanged { add => inner.ConnectionChanged += value; remove => inner.ConnectionChanged -= value; }
    public event Action<QuoteInfo>? QuoteChanged { add => inner.QuoteChanged += value; remove => inner.QuoteChanged -= value; }
    public event Action<OrderInfo>? OrderChanged { add => inner.OrderChanged += value; remove => inner.OrderChanged -= value; }
    public event Action<ExecutionInfo>? ExecutionReceived { add => inner.ExecutionReceived += value; remove => inner.ExecutionReceived -= value; }
    public event Action<PositionInfo>? PositionChanged { add => inner.PositionChanged += value; remove => inner.PositionChanged -= value; }
    public event Action<AccountInfo>? AccountChanged { add => inner.AccountChanged += value; remove => inner.AccountChanged -= value; }
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

static class Recovery
{
    /// <summary>A gateway over the scriptable connector, healthy and allowed to trade.</summary>
    public static async Task<(TradingGateway Gw, RecoveryConnector C, Database Db)> Ready(
        FaultProfile? faults = null, Action<TradeAgentSettings>? settings = null, GatewayOptions? options = null,
        Database? db = null)
    {
        db ??= TestEnv.NewDb();
        var c = new RecoveryConnector(new FakeConnector(new FakeBroker(), faults));
        var gw = new TradingGateway(db, c, new HealthRegistry(), options);
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = c.Inner.Broker.AccountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
            settings?.Invoke(s);
        });
        await c.ConnectAsync();
        await gw.RefreshHealthAsync();
        return (gw, c, db);
    }

    public static ExecutionRequest Row(string id, RequestIntent intent = RequestIntent.PLACE,
        string instrument = "ES", string account = "SIM-001") => new()
        {
            RequestId = id, ConnectorId = "fake", AccountId = account, Instrument = instrument,
            Intent = intent, ParametersJson = Json.Write(TestEnv.Buy()),
            ClientOrderId = TradingGateway.ClientOrderIdFor(id),
            CreatedAt = DateTimeOffset.UtcNow, State = ExecutionState.CREATED, Mode = TradingMode.PAPER
        };

    /// <summary>
    /// Moves a row's dispatch timestamp into the past. `dispatched_at` is written by
    /// ExecutionRequestStore.Transition from its own clock, which no test option reaches, so an
    /// "old" dispatch is made by editing the row rather than by sleeping through the bound.
    /// </summary>
    public static void Backdate(Database db, string requestId, TimeSpan by) => db.Write(_ =>
    {
        using var c = db.Cmd("UPDATE execution_request SET dispatched_at=$t WHERE request_id=$r",
            ("$t", (DateTimeOffset.UtcNow - by).UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
            ("$r", requestId));
        return c.ExecuteNonQuery();
    });

    public static List<(string Event, string Severity, string? Ex)> Engineering(Database db, string requestId) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT event,severity,exception FROM engineering_log WHERE request_id=$r ORDER BY id", ("$r", requestId));
        using var r = c.ExecuteReader();
        var rows = new List<(string, string, string?)>();
        while (r.Read()) rows.Add((r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
        return rows;
    });

    public static Exception Make(string kind) => kind switch
    {
        "InvalidOperation" => new InvalidOperationException("collection was modified"),
        "Json" => new JsonException("unexpected token"),
        "NullReference" => new NullReferenceException("object reference not set"),
        "Timeout" => new TimeoutException("no answer from the platform"),
        "OperationCanceled" => new OperationCanceledException("the RPC was abandoned"),
        _ => new Exception("something no taxonomy names")
    };
}

// =================================================================================================
// A(i) — a DISPATCHING record found at startup is "the wire may have been touched"
// =================================================================================================

/// <summary>
/// FINDING 5 / addendum C1. A crash between the write-ahead DISPATCHING row and Settle leaves the
/// record unflagged; nothing sweeps it, `NeedingReconciliation()` reads the flag alone, and the next
/// start places a second order on top of one that may be live at the broker.
/// </summary>
public class StartupSweepTests
{
    [Fact]
    public async Task A_DISPATCHING_record_found_at_startup_is_swept_to_UNKNOWN_and_pauses_trading()
    {
        var file = Path.Combine(TestEnv.Home, $"sweep-{Guid.NewGuid():n}.db");
        var broker = new FakeBroker();
        var coid = TradingGateway.ClientOrderIdFor("mid-flight");

        // Run 1: the write-ahead is durable, the broker accepts, and the process dies before Settle.
        using (var db = new Database(file))
        {
            var store = new ExecutionRequestStore(db);
            store.TryCreate(Recovery.Row("mid-flight", account: broker.AccountId));
            store.Transition("mid-flight", ExecutionState.CREATED, ExecutionState.DISPATCHING);
            broker.Accept(new PlaceOrderCommand(coid, broker.AccountId, "ES", OrderSide.Buy,
                OrderType.Market, 1m, null, null, TimeInForce.Day, null), FillBehaviour.FillImmediately);
        }

        // Run 2: same records, same broker, a gateway constructed over the store as it stands.
        using (var db = new Database(file))
        {
            var conn = new FakeConnector(broker);
            var gw = new TradingGateway(db, conn, new HealthRegistry(), new GatewayOptions { AbsenceGrace = TimeSpan.Zero });
            gw.Update(s => { s.Mode = TradingMode.PAPER; s.SelectedAccountId = broker.AccountId; s.Risk.MaxNotionalPerOrder = 10_000_000m; s.Risk.MaxOpenPositions = 10; });

            // Swept at construction, before anything else can read the store or place an order.
            var swept = gw.GetRequest("mid-flight")!;
            Assert.Equal(ExecutionState.UNKNOWN, swept.State);
            Assert.True(swept.NeedsReconciliation);
            Assert.Single(gw.Requests.NeedingReconciliation());
            Assert.Equal(HealthState.PAUSED, gw.Health.Get(Components.ExecutionCapability).State);

            await conn.ConnectAsync();
            await gw.RefreshHealthAsync();

            Assert.False(gw.TryAuthorizeExecution(new AgentContext("a"), out _, out var code));
            Assert.Equal(ErrorCode.TRADING_PAUSED_UNRECONCILED, code);
            var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
                gw.PlaceAsync(new AgentContext("a"), "mid-flight-2", TestEnv.Buy()));
            Assert.Equal(ErrorCode.TRADING_PAUSED_UNRECONCILED, denied.Code);
            Assert.Single(broker.Orders);                       // no second order on top of the first

            // ...and the other direction: the reconciler can finish what the sweep started, without
            // resubmitting anything, and trading resumes.
            var result = await gw.ReconcileAsync();
            Assert.True(result.Clean, string.Join("; ", result.Details));
            Assert.Equal(ExecutionState.FILLED, gw.GetRequest("mid-flight")!.State);
            Assert.Single(broker.Orders);
            Assert.True(gw.TryAuthorizeExecution(new AgentContext("a"), out _));
        }
    }

    /// <summary>
    /// The sweep is not "pause on anything you find". A record that never reached the wire, and a
    /// record already settled, must survive a restart untouched — otherwise every restart pauses
    /// trading and the pause stops meaning anything.
    /// </summary>
    [Fact]
    public async Task The_sweep_touches_DISPATCHING_and_nothing_else()
    {
        var file = Path.Combine(TestEnv.Home, $"sweep-others-{Guid.NewGuid():n}.db");
        using (var db = new Database(file))
        {
            var store = new ExecutionRequestStore(db);
            store.TryCreate(Recovery.Row("kept-created"));
            store.TryCreate(Recovery.Row("kept-parked"));
            store.Transition("kept-parked", ExecutionState.CREATED, ExecutionState.AWAITING_APPROVAL);
            store.TryCreate(Recovery.Row("kept-filled"));
            store.Transition("kept-filled", ExecutionState.CREATED, ExecutionState.DISPATCHING);
            store.Transition("kept-filled", ExecutionState.DISPATCHING, ExecutionState.FILLED);
        }

        using (var db = new Database(file))
        {
            var (gw, _, _) = await Recovery.Ready(db: db);

            Assert.Equal(ExecutionState.CREATED, gw.GetRequest("kept-created")!.State);
            Assert.Equal(ExecutionState.AWAITING_APPROVAL, gw.GetRequest("kept-parked")!.State);
            Assert.Equal(ExecutionState.FILLED, gw.GetRequest("kept-filled")!.State);
            Assert.Empty(gw.Requests.NeedingReconciliation());

            // A clean restart still trades. Half of this unit is proving the pause is not universal.
            Assert.True(gw.TryAuthorizeExecution(new AgentContext("a"), out _));
            var placed = await gw.PlaceAsync(new AgentContext("a"), "after-clean-restart", TestEnv.Buy());
            Assert.Equal(ExecutionState.FILLED, placed.State);

            // The parked order is still a parked order: declinable, exactly as before the restart.
            Assert.Equal(ExecutionState.CANCELLED, gw.Decline("kept-parked").State);
        }
    }

    /// <summary>
    /// The swept record has to be reachable by the one route the product gives a person: the
    /// unconfirmed card on the Dashboard, which calls ForceResolve with FILLED or CANCELLED.
    /// </summary>
    [Fact]
    public async Task A_swept_record_can_be_resolved_through_the_override_card()
    {
        var file = Path.Combine(TestEnv.Home, $"sweep-force-{Guid.NewGuid():n}.db");
        using (var db = new Database(file))
        {
            var store = new ExecutionRequestStore(db);
            store.TryCreate(Recovery.Row("card-1"));
            store.Transition("card-1", ExecutionState.CREATED, ExecutionState.DISPATCHING);
        }

        using (var db = new Database(file))
        {
            var (gw, _, _) = await Recovery.Ready(db: db);
            var card = Assert.Single(gw.Requests.NeedingReconciliation());
            Assert.Equal("card-1", card.RequestId);
            Assert.False(OrderStateMachine.IsTerminal(card.State));   // the card offers two answers

            gw.ForceResolve("card-1", ExecutionState.FILLED, "checked in ATAS: 1 ES filled");

            Assert.Equal(ExecutionState.FILLED, gw.GetRequest("card-1")!.State);
            Assert.Empty(gw.Requests.NeedingReconciliation());
            await gw.RefreshHealthAsync();
            Assert.True(gw.TryAuthorizeExecution(new AgentContext("a"), out _));
        }
    }

    /// <summary>
    /// The surfaces that report the sweep to a person and to the agent. `trade status` is what an
    /// agent reads before proposing anything, and the doctor is what the owner is told to run; both
    /// have to say the same thing as the gate that refuses.
    /// </summary>
    [Fact]
    public async Task Status_and_the_doctor_both_report_a_swept_record()
    {
        var file = Path.Combine(TestEnv.Home, $"sweep-status-{Guid.NewGuid():n}.db");
        using (var db = new Database(file))
        {
            var store = new ExecutionRequestStore(db);
            store.TryCreate(Recovery.Row("status-1"));
            store.Transition("status-1", ExecutionState.CREATED, ExecutionState.DISPATCHING);
        }

        using (var db = new Database(file))
        {
            var (gw, _, _) = await Recovery.Ready(db: db);

            var status = await gw.StatusAsync();
            Assert.Equal(1, status.OpenRequests);
            Assert.Equal(1, status.UnreconciledRequests);
            Assert.False(status.ExecutionAvailable);
            Assert.Contains("unconfirmed", status.ExecutionBlockedReason);

            var report = await new Doctor(gw).RunAsync();
            var check = Assert.Single(report.Checks, c => c.Name == "Order confirmation");
            Assert.Equal(HealthState.DEGRADED, check.State);
            Assert.Equal(ErrorCode.TRADING_PAUSED_UNRECONCILED, check.Code);
        }
    }

    /// <summary>
    /// The honest expectation on the owner's machine: a CANCEL request stranded at DISPATCHING by an
    /// older build now surfaces as needing reconciliation. That is the point — it gives a row nobody
    /// could see an in-product route — and the absence path must still terminate it rather than
    /// leaving it flagged forever.
    /// </summary>
    [Fact]
    public async Task A_legacy_stranded_cancel_record_is_swept_and_the_absence_path_terminates_it()
    {
        var file = Path.Combine(TestEnv.Home, $"sweep-cancel-{Guid.NewGuid():n}.db");
        using (var db = new Database(file))
        {
            var store = new ExecutionRequestStore(db);
            store.TryCreate(Recovery.Row("legacy-cancel", RequestIntent.CANCEL, instrument: "-"));
            store.Transition("legacy-cancel", ExecutionState.CREATED, ExecutionState.DISPATCHING);
        }

        using (var db = new Database(file))
        {
            var (gw, _, _) = await Recovery.Ready(db: db, options: new GatewayOptions { AbsenceGrace = TimeSpan.Zero });
            Assert.Equal(ExecutionState.UNKNOWN, gw.GetRequest("legacy-cancel")!.State);
            Assert.Single(gw.Requests.NeedingReconciliation());

            var result = await gw.ReconcileAsync();

            Assert.True(result.Clean, string.Join("; ", result.Details));
            Assert.Equal(ExecutionState.CANCELLED, gw.GetRequest("legacy-cancel")!.State);
            Assert.Empty(gw.Requests.NeedingReconciliation());
            Assert.True(gw.TryAuthorizeExecution(new AgentContext("a"), out _));
        }
    }
}

// =================================================================================================
// A(ii) — a DISPATCHING record older than the wire's own deadline is unreconciled work
// =================================================================================================

/// <summary>
/// The half of FINDING 5 that does not need a restart. A strand only becomes visible at the next
/// start; until then the record sits DISPATCHING, unflagged, and trading continues over it.
/// </summary>
public class AgedDispatchTests
{
    [Fact]
    public async Task A_DISPATCHING_record_older_than_the_bound_pauses_trading_without_any_restart()
    {
        var (gw, _, db) = await Recovery.Ready();
        using var dbh = db;
        var store = gw.Requests;
        store.TryCreate(Recovery.Row("stranded-live"));
        store.Transition("stranded-live", ExecutionState.CREATED, ExecutionState.DISPATCHING);

        // Fresh: an order genuinely on the wire must NOT pause the gateway that is placing it.
        Assert.True(gw.TryAuthorizeExecution(new AgentContext("a"), out _));

        Recovery.Backdate(db, "stranded-live", TimeSpan.FromMinutes(10));

        Assert.False(gw.TryAuthorizeExecution(new AgentContext("a"), out _, out var code));
        Assert.Equal(ErrorCode.TRADING_PAUSED_UNRECONCILED, code);
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "after-strand", TestEnv.Buy()));
        Assert.Equal(ErrorCode.TRADING_PAUSED_UNRECONCILED, denied.Code);

        // The health row the Dashboard reads agrees with the gate.
        await gw.RefreshHealthAsync();
        Assert.Equal(HealthState.PAUSED, gw.Health.Get(Components.ExecutionCapability).State);

        // And the reconciler picks it up rather than leaving it to a restart: the row leaves
        // DISPATCHING on the next pass, which is what a surface still reading needs_reconciliation=1
        // needs in order to catch up. Nothing was resubmitted; the broker never had this order, and
        // after the absence grace that is a definite "it never landed".
        var result = await gw.ReconcileAsync();
        Assert.True(result.Clean, string.Join("; ", result.Details));
        Assert.Equal(ExecutionState.CANCELLED, gw.GetRequest("stranded-live")!.State);
        Assert.True(gw.TryAuthorizeExecution(new AgentContext("a"), out _));
    }

    /// <summary>
    /// The other end of the same pass: on a platform that cannot prove its own history, the aged
    /// record ends UNKNOWN and FLAGGED rather than resolved — which is what makes the flag-only
    /// surfaces (the unconfirmed card, the doctor check) agree with the gate within one pass.
    /// </summary>
    [Fact]
    public async Task An_aged_record_the_reconciler_cannot_settle_is_left_flagged()
    {
        var (gw, _, db) = await Recovery.Ready(new FaultProfile { HideOrderHistory = true });
        using var dbh = db;
        gw.Requests.TryCreate(Recovery.Row("stranded-unprovable"));
        gw.Requests.Transition("stranded-unprovable", ExecutionState.CREATED, ExecutionState.DISPATCHING);
        Recovery.Backdate(db, "stranded-unprovable", TimeSpan.FromMinutes(10));

        var result = await gw.ReconcileAsync();

        Assert.False(result.Clean);
        var row = gw.GetRequest("stranded-unprovable")!;
        Assert.Equal(ExecutionState.RECONCILING, row.State);
        Assert.True(row.NeedsReconciliation);
        Assert.Single(gw.Requests.NeedingReconciliation());       // the flag-only view now agrees
        Assert.False(gw.TryAuthorizeExecution(new AgentContext("a"), out _, out var code));
        Assert.Equal(ErrorCode.TRADING_PAUSED_UNRECONCILED, code);
    }

    /// <summary>
    /// The query underneath, both directions, with no gateway in the way: the same record is
    /// unconfirmed work against one cutoff and not against another, and the flag-only overload —
    /// which Doctor and the dev host still call — is unchanged by any of it.
    /// </summary>
    [Fact]
    public void The_store_counts_a_dispatch_as_unreconciled_only_past_the_cutoff_it_is_given()
    {
        using var db = TestEnv.NewDb();
        var store = new ExecutionRequestStore(db);
        store.TryCreate(Recovery.Row("cutoff-1"));
        store.Transition("cutoff-1", ExecutionState.CREATED, ExecutionState.DISPATCHING);

        Assert.Empty(store.NeedingReconciliation());                                        // the flag alone
        Assert.Empty(store.NeedingReconciliation(DateTimeOffset.UtcNow - TimeSpan.FromHours(1)));
        Assert.Single(store.NeedingReconciliation(DateTimeOffset.UtcNow));
        Assert.Equal(ExecutionRequestStore.DefaultDispatchStrandedAfter, new GatewayOptions().DispatchStrandedAfter);

        // A settled record is not dragged in by the cutoff, whatever its age.
        store.Transition("cutoff-1", ExecutionState.DISPATCHING, ExecutionState.FILLED);
        Assert.Empty(store.NeedingReconciliation(DateTimeOffset.UtcNow));

        // ...and a flagged record is still counted with no cutoff at all.
        store.MarkNeedsReconciliation("cutoff-1", "the stream settled it while a dispatch was in flight");
        Assert.Single(store.NeedingReconciliation());
        Assert.Single(store.NeedingReconciliation(DateTimeOffset.UtcNow));                  // once, not twice
    }

    /// <summary>
    /// The bound is a bound, not a synonym for DISPATCHING. An order hanging on a wire that has not
    /// yet blown its deadline is ordinary, and pausing on it would pause on every order placed.
    /// </summary>
    [Fact]
    public async Task An_order_still_inside_the_bound_does_not_pause_trading()
    {
        var (gw, c, db) = await Recovery.Ready();
        using var dbh = db;
        var hang = new TaskCompletionSource();
        c.HangPlace = hang;

        var inFlight = Task.Run(() => gw.PlaceAsync(new AgentContext("a"), "hanging", TestEnv.Buy()));
        try
        {
            while (gw.GetRequest("hanging")?.State != ExecutionState.DISPATCHING) await Task.Delay(5);

            // The wire is open, the broker already has the order, and the record is DISPATCHING.
            Assert.True(gw.TryAuthorizeExecution(AgentContext.Operator, out _));
            Assert.Empty(gw.Requests.NeedingReconciliation());

            // Now let it be old. Same record, same connector, opposite verdict.
            Recovery.Backdate(db, "hanging", TimeSpan.FromMinutes(10));
            Assert.False(gw.TryAuthorizeExecution(AgentContext.Operator, out _, out var code));
            Assert.Equal(ErrorCode.TRADING_PAUSED_UNRECONCILED, code);
        }
        finally
        {
            c.HangPlace = null;
            hang.SetResult();
            await inFlight;
        }
    }
}

// =================================================================================================
// B — what the connector answered is what gets recorded
// =================================================================================================

/// <summary>
/// FINDING 9 / addendum C2. `_ => ACKNOWLEDGED` turns "we do not know" into "we do know, it is
/// live", and a modify the platform ignored into a modify that was applied.
/// </summary>
public class ConnectorAnswerMappingTests
{
    /// <summary>
    /// The mapping this unit promises, written out here rather than imported, so that changing the
    /// production switch cannot silently change what the test expects.
    /// </summary>
    public static (ExecutionState Stored, bool Flagged) Expected(ExecutionState returned) => returned switch
    {
        ExecutionState.FILLED => (ExecutionState.FILLED, false),
        ExecutionState.PARTIALLY_FILLED => (ExecutionState.PARTIALLY_FILLED, false),
        ExecutionState.WORKING => (ExecutionState.WORKING, false),
        ExecutionState.ACKNOWLEDGED => (ExecutionState.ACKNOWLEDGED, false),
        ExecutionState.REJECTED => (ExecutionState.REJECTED, false),
        ExecutionState.CANCELLED => (ExecutionState.CANCELLED, false),
        // Everything else is an answer we cannot record as an outcome. CANCEL_PENDING is in this
        // group and not in the one above because DISPATCHING -> CANCEL_PENDING is not a legal
        // transition (FaultTests.The_table_lets_a_dispatching_cancel_reach_cancelled pins its
        // absence), so the only honest destination for it is UNKNOWN.
        _ => (ExecutionState.UNKNOWN, true)
    };

    [Theory]
    [InlineData(ExecutionState.CREATED)]
    [InlineData(ExecutionState.AWAITING_APPROVAL)]
    [InlineData(ExecutionState.DISPATCHING)]
    [InlineData(ExecutionState.ACKNOWLEDGED)]
    [InlineData(ExecutionState.WORKING)]
    [InlineData(ExecutionState.PARTIALLY_FILLED)]
    [InlineData(ExecutionState.FILLED)]
    [InlineData(ExecutionState.CANCEL_PENDING)]
    [InlineData(ExecutionState.CANCELLED)]
    [InlineData(ExecutionState.REJECTED)]
    [InlineData(ExecutionState.UNKNOWN)]
    [InlineData(ExecutionState.RECONCILING)]
    public async Task Every_state_a_connector_can_answer_with_is_recorded_faithfully_or_as_unknown(ExecutionState returned)
    {
        var (gw, c, db) = await Recovery.Ready();
        using var dbh = db;
        c.RewritePlaced = o => o with { State = returned };

        var r = await gw.PlaceAsync(new AgentContext("a"), $"answer-{returned}", TestEnv.Buy());
        var (expected, flagged) = Expected(returned);

        Assert.Equal(expected, r.State);
        Assert.Equal(flagged, r.NeedsReconciliation);
        Assert.Equal(!flagged, gw.TryAuthorizeExecution(new AgentContext("a"), out _));
        if (flagged)
            Assert.Equal(HealthState.PAUSED, gw.Health.Get(Components.ExecutionCapability).State);
    }

    /// <summary>The theory above is only a total map while it names every value of the enum.</summary>
    [Fact]
    public void The_theory_covers_every_execution_state()
    {
        var covered = typeof(ConnectorAnswerMappingTests)
            .GetMethod(nameof(Every_state_a_connector_can_answer_with_is_recorded_faithfully_or_as_unknown))!
            .GetCustomAttributes(typeof(InlineDataAttribute), false)
            .Cast<InlineDataAttribute>()
            .Select(a => (ExecutionState)a.GetData(null!).First()[0]!)
            .ToHashSet();

        Assert.Equal(Enum.GetValues<ExecutionState>().ToHashSet(), covered);
    }

    [Fact]
    public async Task A_modification_the_platform_did_not_apply_is_never_recorded_as_applied()
    {
        var (gw, c, db) = await Recovery.Ready(new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var dbh = db;
        var placed = await gw.PlaceAsync(new AgentContext("a"), "mod-place",
            new PlaceIntent("ES", OrderSide.Buy, OrderType.Limit, 1m, 1m, null, TimeInForce.Day, null));
        var before = c.Inner.Broker.Orders.Single();
        c.ModifyIgnoresTheRequest = true;                      // the platform returns the order unchanged

        var r = await gw.ModifyAsync(new AgentContext("a"), "mod-ignored", placed.ConnectorOrderId!,
            quantity: 7m, limitPrice: 4242m, stopPrice: null);

        Assert.Equal(before.Quantity, c.Inner.Broker.Orders.Single().Quantity);   // nothing moved
        Assert.Equal(ExecutionState.UNKNOWN, r.State);
        Assert.True(r.NeedsReconciliation);
        Assert.Equal(HealthState.PAUSED, gw.Health.Get(Components.ExecutionCapability).State);
        Assert.False(gw.TryAuthorizeExecution(new AgentContext("a"), out _, out var code));
        Assert.Equal(ErrorCode.TRADING_PAUSED_UNRECONCILED, code);
    }

    [Fact]
    public async Task A_modification_the_platform_did_apply_is_acknowledged_and_pauses_nothing()
    {
        var (gw, c, db) = await Recovery.Ready(new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var dbh = db;
        var placed = await gw.PlaceAsync(new AgentContext("a"), "mod2-place",
            new PlaceIntent("ES", OrderSide.Buy, OrderType.Limit, 1m, 1m, null, TimeInForce.Day, null));

        var r = await gw.ModifyAsync(new AgentContext("a"), "mod2-apply", placed.ConnectorOrderId!,
            quantity: 3m, limitPrice: 2m, stopPrice: null);

        Assert.Equal(ExecutionState.ACKNOWLEDGED, r.State);
        Assert.False(r.NeedsReconciliation);
        Assert.True(gw.TryAuthorizeExecution(new AgentContext("a"), out _));
    }
}

// =================================================================================================
// C — every exception after the wire is touched is an indefinite outcome
// =================================================================================================

/// <summary>
/// FINDING 10 / addendum C3. The catch taxonomy names three exception types on place and two on
/// cancel/modify; anything else propagates past Settle and leaves the write-ahead row as the last
/// word. docs/CONTRACTS.md already promised "any other exception is treated as indefinite".
/// </summary>
public class DispatchExceptionTaxonomyTests
{
    public static TheoryData<string> Indefinite =>
        ["InvalidOperation", "Json", "NullReference", "Timeout", "OperationCanceled", "Plain"];

    [Theory]
    [MemberData(nameof(Indefinite))]
    public async Task A_place_that_throws_after_the_wire_is_recorded_UNKNOWN_and_pauses(string kind)
    {
        var (gw, c, db) = await Recovery.Ready();
        using var dbh = db;
        c.ThrowAfterPlace = Recovery.Make(kind);

        var r = await gw.PlaceAsync(new AgentContext("a"), $"place-{kind}", TestEnv.Buy());
        c.ThrowAfterPlace = null;

        Assert.Equal(ExecutionState.UNKNOWN, r.State);
        Assert.True(r.NeedsReconciliation);
        Assert.Equal(HealthState.PAUSED, gw.Health.Get(Components.ExecutionCapability).State);
        Assert.False(gw.TryAuthorizeExecution(new AgentContext("a"), out _, out var code));
        Assert.Equal(ErrorCode.TRADING_PAUSED_UNRECONCILED, code);

        // No second order goes on top of the one nobody accounted for.
        await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), $"place-{kind}-next", TestEnv.Buy()));
        Assert.Single(c.Inner.Broker.Orders);

        // An engineer has to be able to tell WHICH defect did this.
        var events = Recovery.Engineering(db, $"place-{kind}");
        Assert.Contains(events, e => e.Event == "dispatch_unknown" && (e.Ex ?? "").Contains(Recovery.Make(kind).GetType().Name));
    }

    [Theory]
    [MemberData(nameof(Indefinite))]
    public async Task A_cancel_that_throws_after_the_wire_is_recorded_UNKNOWN_and_pauses(string kind)
    {
        var (gw, c, db) = await Recovery.Ready(new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var dbh = db;
        var placed = await gw.PlaceAsync(new AgentContext("a"), $"cx-{kind}", TestEnv.Buy());
        c.ThrowAfterCancel = Recovery.Make(kind);

        var r = await gw.CancelAsync(new AgentContext("a"), $"cx-{kind}-cancel", placed.ConnectorOrderId!);

        // The broker DID cancel. The ledger must not be silent about having asked.
        Assert.Equal(ExecutionState.CANCELLED, c.Inner.Broker.Orders.Single().State);
        Assert.Equal(ExecutionState.UNKNOWN, r.State);
        Assert.True(r.NeedsReconciliation);
        Assert.Equal(HealthState.PAUSED, gw.Health.Get(Components.ExecutionCapability).State);
        Assert.False(gw.TryAuthorizeExecution(new AgentContext("a"), out _, out var code));
        Assert.Equal(ErrorCode.TRADING_PAUSED_UNRECONCILED, code);
    }

    [Theory]
    [MemberData(nameof(Indefinite))]
    public async Task A_modify_that_throws_after_the_wire_is_recorded_UNKNOWN_and_pauses(string kind)
    {
        var (gw, c, db) = await Recovery.Ready(new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var dbh = db;
        var placed = await gw.PlaceAsync(new AgentContext("a"), $"mx-{kind}", TestEnv.Buy());
        c.ThrowAfterModify = Recovery.Make(kind);

        var r = await gw.ModifyAsync(new AgentContext("a"), $"mx-{kind}-mod", placed.ConnectorOrderId!, 3m, null, null);

        Assert.Equal(ExecutionState.UNKNOWN, r.State);
        Assert.True(r.NeedsReconciliation);
        Assert.Equal(HealthState.PAUSED, gw.Health.Get(Components.ExecutionCapability).State);
        Assert.False(gw.TryAuthorizeExecution(new AgentContext("a"), out _, out var code));
        Assert.Equal(ErrorCode.TRADING_PAUSED_UNRECONCILED, code);
    }

    /// <summary>
    /// The other direction, and the one that must not be widened away: a definite broker refusal is
    /// still final, still unflagged, and still leaves trading open.
    /// </summary>
    [Fact]
    public async Task A_definite_refusal_still_settles_REJECTED_on_all_three_paths()
    {
        var (gw, c, db) = await Recovery.Ready(new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var dbh = db;

        var placed = await gw.PlaceAsync(new AgentContext("a"), "rej-place", TestEnv.Buy());
        c.ThrowAfterCancel = new ConnectorRejectedException("this order cannot be cancelled");
        var cancelled = await gw.CancelAsync(new AgentContext("a"), "rej-cancel", placed.ConnectorOrderId!);
        Assert.Equal(ExecutionState.REJECTED, cancelled.State);
        Assert.False(cancelled.NeedsReconciliation);

        c.ThrowAfterCancel = null;
        c.ThrowAfterModify = new ConnectorRejectedException("that price is not valid");
        var modified = await gw.ModifyAsync(new AgentContext("a"), "rej-mod", placed.ConnectorOrderId!, 2m, null, null);
        Assert.Equal(ExecutionState.REJECTED, modified.State);
        Assert.False(modified.NeedsReconciliation);

        c.ThrowAfterModify = null;
        c.Inner.Faults.RejectNext = 1;
        var refused = await gw.PlaceAsync(new AgentContext("a"), "rej-buy", TestEnv.Buy());
        Assert.Equal(ExecutionState.REJECTED, refused.State);
        Assert.False(refused.NeedsReconciliation);

        // Nothing above is unconfirmed work, so trading is still open.
        Assert.Empty(gw.Requests.NeedingReconciliation());
        Assert.True(gw.TryAuthorizeExecution(new AgentContext("a"), out _));
    }

    /// <summary>The ordinary paths, unbroken: place, cancel, modify with a healthy connector.</summary>
    [Fact]
    public async Task The_ordinary_paths_still_work()
    {
        var (gw, c, db) = await Recovery.Ready(new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var dbh = db;

        var placed = await gw.PlaceAsync(new AgentContext("a"), "ok-place",
            new PlaceIntent("ES", OrderSide.Buy, OrderType.Limit, 1m, 1m, null, TimeInForce.Day, null));
        Assert.Equal(ExecutionState.WORKING, placed.State);

        var modified = await gw.ModifyAsync(new AgentContext("a"), "ok-mod", placed.ConnectorOrderId!, 2m, null, null);
        Assert.Equal(ExecutionState.ACKNOWLEDGED, modified.State);

        var cancelled = await gw.CancelAsync(new AgentContext("a"), "ok-cancel", placed.ConnectorOrderId!);
        Assert.Equal(ExecutionState.CANCELLED, cancelled.State);
        Assert.Equal(ExecutionState.CANCELLED, c.Inner.Broker.Orders.Single().State);

        Assert.Empty(gw.Requests.NeedingReconciliation());
        Assert.True(gw.TryAuthorizeExecution(new AgentContext("a"), out _));

        c.Inner.Faults.Fill = FillBehaviour.FillImmediately;
        var filled = await gw.PlaceAsync(new AgentContext("a"), "ok-market", TestEnv.Buy());
        Assert.Equal(ExecutionState.FILLED, filled.State);
    }
}

// =================================================================================================
// D — the operator's emergency controls leave a record
// =================================================================================================

/// <summary>
/// FINDING 11 / addendum C4. Close all positions is the button the owner reaches for in an
/// emergency, and it writes nothing: a close that reaches the broker and then fails leaves no trace,
/// pauses nothing, and the natural second press reverses the position instead of flattening it.
/// </summary>
public class OperatorEmergencyRecordTests
{
    [Fact]
    public async Task A_close_that_landed_then_failed_leaves_a_flagged_record()
    {
        var (gw, c, db) = await Recovery.Ready();
        using var dbh = db;
        await gw.PlaceAsync(AgentContext.Operator, "pos-1", TestEnv.Buy(qty: 2m));
        Assert.Single(c.Inner.Broker.Positions);

        c.Inner.Faults.Fill = FillBehaviour.LeaveWorking;       // the close sits working, as a real one does
        c.ThrowAfterClose = new ConnectorTransportException("connection lost after the close was sent");

        // ROUND 2 (item 4): the loop records the failure and carries on rather than throwing, because
        // throwing abandoned every position after this one. What the person is told does not depend
        // on an exception: execution is paused, the record is on the unconfirmed card, and the
        // Dashboard's own press check (item 3) reports it after the press returns.
        await gw.OperatorCloseAllAsync();
        Assert.True(gw.HasUnconfirmedWork());

        var record = Assert.Single(gw.Requests.Query("intent='PLACE' AND request_id LIKE 'op-close-%'"));
        Assert.Equal(ExecutionState.UNKNOWN, record.State);
        Assert.True(record.NeedsReconciliation);
        Assert.Equal("ES", record.Instrument);
        Assert.Equal("operator", record.AgentSessionId);
        Assert.Equal(HealthState.PAUSED, gw.Health.Get(Components.ExecutionCapability).State);
        Assert.False(gw.TryAuthorizeExecution(new AgentContext("a"), out _, out var code));
        Assert.Equal(ErrorCode.TRADING_PAUSED_UNRECONCILED, code);
    }

    [Fact]
    public async Task Close_all_with_a_healthy_connector_closes_each_position_once_and_records_each()
    {
        var (gw, c, db) = await Recovery.Ready();
        using var dbh = db;
        await gw.PlaceAsync(AgentContext.Operator, "flat-es", TestEnv.Buy(qty: 2m));
        await gw.PlaceAsync(AgentContext.Operator, "flat-nq", TestEnv.Buy("NQ", 1m));
        Assert.Equal(2, c.Inner.Broker.Positions.Count);

        var closed = await gw.OperatorCloseAllAsync();

        Assert.Equal(2, closed);
        Assert.Equal(2, c.Closes);
        Assert.Empty(c.Inner.Broker.Positions);
        var records = gw.Requests.Query("request_id LIKE 'op-close-%'");
        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.Equal(ExecutionState.FILLED, r.State));
        Assert.All(records, r => Assert.False(r.NeedsReconciliation));
        Assert.True(gw.TryAuthorizeExecution(new AgentContext("a"), out _));
    }

    [Fact]
    public async Task Cancel_all_records_every_order_it_cancels()
    {
        var (gw, c, db) = await Recovery.Ready(new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var dbh = db;
        var a = await gw.PlaceAsync(AgentContext.Operator, "w-1", TestEnv.Buy());
        var b = await gw.PlaceAsync(AgentContext.Operator, "w-2", TestEnv.Buy("NQ"));

        var ids = await gw.OperatorCancelAllAsync();

        Assert.Equal(2, ids.Count);
        var records = gw.Requests.Query("request_id LIKE 'op-cancel-%'");
        Assert.All(records, r => Assert.Equal(ExecutionState.CANCELLED, r.State));
        Assert.All(records, r => Assert.Equal("operator", r.AgentSessionId));

        var perOrder = records.Where(r => r.Intent == RequestIntent.CANCEL).ToList();
        Assert.Equal(2, perOrder.Count);
        Assert.Contains(perOrder, r => r.ParametersJson.Contains(a.ConnectorOrderId!));
        Assert.Contains(perOrder, r => r.ParametersJson.Contains(b.ConnectorOrderId!));

        // ...and the press itself, which is what a retry recognises.
        Assert.Single(records, r => r.Intent == RequestIntent.CANCEL_ALL);
        Assert.Empty(gw.Requests.NeedingReconciliation());
        Assert.True(gw.TryAuthorizeExecution(new AgentContext("a"), out _));
    }

    [Fact]
    public async Task A_cancel_all_that_failed_on_the_wire_leaves_its_orders_flagged()
    {
        var (gw, c, db) = await Recovery.Ready(new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var dbh = db;
        await gw.PlaceAsync(AgentContext.Operator, "wf-1", TestEnv.Buy());
        c.ThrowAfterCancelAll = new ConnectorTransportException("connection lost during cancel-all");

        await Assert.ThrowsAsync<ConnectorTransportException>(() => gw.OperatorCancelAllAsync());

        var records = gw.Requests.Query("request_id LIKE 'op-cancel-%'");
        Assert.Equal(2, records.Count);                        // the press, and the one order on the book
        Assert.All(records, r => Assert.Equal(ExecutionState.UNKNOWN, r.State));
        Assert.All(records, r => Assert.True(r.NeedsReconciliation));
        Assert.Equal(HealthState.PAUSED, gw.Health.Get(Components.ExecutionCapability).State);
    }

    /// <summary>
    /// The press, not the call, is the unit of intent. Pressing once and retrying that press must
    /// not sell the position twice — which is what "close #1 failed, so I pressed again" did to a
    /// 2-contract long: two market sells, a position reversed rather than flattened, and no record
    /// that either was sent.
    /// </summary>
    [Fact]
    public async Task A_retried_press_submits_nothing_and_a_new_press_is_a_new_request()
    {
        var (gw, c, db) = await Recovery.Ready();
        using var dbh = db;
        await gw.PlaceAsync(AgentContext.Operator, "rp-1", TestEnv.Buy(qty: 2m));
        c.Inner.Faults.Fill = FillBehaviour.LeaveWorking;      // the close sits working, position still open
        var press = TradingGateway.NewOperatorPressNonce();

        Assert.Equal(1, await gw.OperatorCloseAllAsync(press));
        Assert.Equal(1, c.Closes);

        // The same press again — a retry, not a new decision.
        Assert.Equal(0, await gw.OperatorCloseAllAsync(press));
        Assert.Equal(1, c.Closes);
        Assert.Single(gw.Requests.Query("request_id LIKE 'op-close-%'"));
        Assert.Single(c.Inner.Broker.Orders, o => o.Side == OrderSide.Sell);

        // A NEW press is a new decision and is carried out — the owner looking at a position that is
        // still open must be able to press again and have it mean something.
        Assert.Equal(1, await gw.OperatorCloseAllAsync(TradingGateway.NewOperatorPressNonce()));
        Assert.Equal(2, c.Closes);
        Assert.Equal(2, gw.Requests.Query("request_id LIKE 'op-close-%'").Count);
    }

    /// <summary>
    /// The positive control for the test above: with idempotency switched off the harness DOES see
    /// the double close, so "one close" is evidence rather than an artefact of the fixture.
    /// </summary>
    [Fact]
    public async Task Control_the_harness_can_detect_a_double_close_when_idempotency_is_off()
    {
        var (gw, c, db) = await Recovery.Ready(options: new GatewayOptions { IdempotencyEnabled = false });
        using var dbh = db;
        await gw.PlaceAsync(AgentContext.Operator, "rp-off", TestEnv.Buy(qty: 2m));
        c.Inner.Faults.Fill = FillBehaviour.LeaveWorking;
        var press = TradingGateway.NewOperatorPressNonce();

        await gw.OperatorCloseAllAsync(press);
        await gw.OperatorCloseAllAsync(press);

        Assert.Equal(2, c.Closes);
        Assert.Equal(4m, c.Inner.Broker.Orders.Where(o => o.Side == OrderSide.Sell).Sum(o => o.Quantity));
    }

    [Fact]
    public async Task A_retried_cancel_all_press_submits_nothing()
    {
        var (gw, c, db) = await Recovery.Ready(new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var dbh = db;
        await gw.PlaceAsync(AgentContext.Operator, "rc-1", TestEnv.Buy());
        var press = TradingGateway.NewOperatorPressNonce();

        Assert.Single(await gw.OperatorCancelAllAsync(press));
        Assert.Equal(1, c.CancelAlls);

        Assert.Empty(await gw.OperatorCancelAllAsync(press));
        Assert.Equal(1, c.CancelAlls);                         // the wire was not touched again
        Assert.Equal(2, gw.Requests.Query("request_id LIKE 'op-cancel-%'").Count);   // the press, and the order

        // A new press still works — the book is empty now, so it sweeps and finds nothing to cancel.
        Assert.Empty(await gw.OperatorCancelAllAsync(TradingGateway.NewOperatorPressNonce()));
        Assert.Equal(2, c.CancelAlls);
    }

    /// <summary>
    /// The escape hatch stays an escape hatch. The controls must work while trading is paused by the
    /// very records they write, and while the kill switch is down — that is why they are outside
    /// AUTHORIZATION, and a fix that quietly pulled them inside it would be the worse bug.
    /// </summary>
    [Fact]
    public async Task The_emergency_controls_still_work_while_trading_is_paused()
    {
        var (gw, c, db) = await Recovery.Ready(new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var dbh = db;
        await gw.PlaceAsync(AgentContext.Operator, "eh-1", TestEnv.Buy());
        c.Inner.Faults.Fill = FillBehaviour.FillImmediately;
        await gw.PlaceAsync(AgentContext.Operator, "eh-2", TestEnv.Buy("NQ", 2m));

        // Pause trading for real, the way a lost acknowledgement does.
        gw.Requests.MarkNeedsReconciliation("eh-1", "unconfirmed");
        gw.StopAiTrading("test");
        Assert.False(gw.TryAuthorizeExecution(new AgentContext("a"), out _));

        var ids = await gw.OperatorCancelAllAsync();
        var closed = await gw.OperatorCloseAllAsync();

        Assert.NotEmpty(ids);
        Assert.Equal(1, closed);
        Assert.Empty(c.Inner.Broker.Positions);
    }
}

// =================================================================================================
// ROUND 2 · item 1 — a cancel or a modify is reconciled against the ORDER IT NAMED
// =================================================================================================

/// <summary>
/// The reconciler matched every request against a `TA-{requestId}` client id at the broker. A PLACE
/// really does carry that id onto the order; a CANCEL, a MODIFY and a cancel-all never transmit it —
/// they send the target's broker id, or nothing but the account. So the lookup always missed, and on
/// a connector that can prove its own history the absence rule then read "no order exists" and wrote
/// CANCELLED: the ledger recording a cancellation as done, and trading resuming, while the order it
/// was supposed to cancel is still working at the broker.
/// </summary>
public class TargetedReconciliationTests
{
    static GatewayOptions NoGrace => new() { AbsenceGrace = TimeSpan.Zero };

    [Fact]
    public async Task A_cancel_whose_target_is_still_working_is_never_reconciled_as_cancelled()
    {
        var (gw, c, db) = await Recovery.Ready(new FaultProfile { Fill = FillBehaviour.LeaveWorking }, options: NoGrace);
        using var dbh = db;
        var placed = await gw.PlaceAsync(new AgentContext("a"), "t-place",
            new PlaceIntent("ES", OrderSide.Buy, OrderType.Limit, 1m, 1m, null, TimeInForce.Day, null));

        // The cancel never takes effect at the platform, and the answer is lost on the way home.
        c.CancelDoesNotReachTheBook = true;
        c.ThrowAfterCancel = new ConnectorTransportException("wire down after the cancel was sent");
        var cancel = await gw.CancelAsync(new AgentContext("a"), "t-cancel", placed.ConnectorOrderId!);
        Assert.Equal(ExecutionState.UNKNOWN, cancel.State);
        c.ThrowAfterCancel = null;
        c.CancelDoesNotReachTheBook = false;

        var result = await gw.ReconcileAsync();

        // The target is the evidence, and it says the cancel did not land.
        Assert.Equal(ExecutionState.WORKING, c.Inner.Broker.Orders.Single().State);
        var record = gw.GetRequest("t-cancel")!;
        Assert.NotEqual(ExecutionState.CANCELLED, record.State);
        Assert.Equal(ExecutionState.REJECTED, record.State);
        Assert.False(record.NeedsReconciliation);
        Assert.Contains("still working", record.LastError);
        Assert.True(result.Clean, string.Join("; ", result.Details));

        // Nothing is unconfirmed any more — the answer was definite — so the agent may trade, and it
        // may try the cancellation again under a new request id.
        Assert.True(gw.TryAuthorizeExecution(new AgentContext("a"), out _));
        var retry = await gw.CancelAsync(new AgentContext("a"), "t-cancel-2", placed.ConnectorOrderId!);
        Assert.Equal(ExecutionState.CANCELLED, retry.State);
        Assert.Equal(ExecutionState.CANCELLED, c.Inner.Broker.Orders.Single().State);
    }

    [Fact]
    public async Task A_cancel_whose_target_is_gone_reconciles_as_cancelled()
    {
        var (gw, c, db) = await Recovery.Ready(new FaultProfile { Fill = FillBehaviour.LeaveWorking }, options: NoGrace);
        using var dbh = db;
        var placed = await gw.PlaceAsync(new AgentContext("a"), "g-place",
            new PlaceIntent("ES", OrderSide.Buy, OrderType.Limit, 1m, 1m, null, TimeInForce.Day, null));

        // This one DOES take effect; only the acknowledgement is lost.
        c.ThrowAfterCancel = new ConnectorTransportException("wire down after the cancel was sent");
        await gw.CancelAsync(new AgentContext("a"), "g-cancel", placed.ConnectorOrderId!);
        c.ThrowAfterCancel = null;

        var result = await gw.ReconcileAsync();

        Assert.True(result.Clean, string.Join("; ", result.Details));
        Assert.Equal(ExecutionState.CANCELLED, gw.GetRequest("g-cancel")!.State);
        Assert.Empty(gw.Requests.NeedingReconciliation());
        Assert.True(gw.TryAuthorizeExecution(new AgentContext("a"), out _));
    }

    [Fact]
    public async Task A_cancel_whose_target_filled_instead_is_recorded_as_a_cancel_that_failed()
    {
        var (gw, c, db) = await Recovery.Ready(new FaultProfile { Fill = FillBehaviour.LeaveWorking }, options: NoGrace);
        using var dbh = db;
        var placed = await gw.PlaceAsync(new AgentContext("a"), "f-place",
            new PlaceIntent("ES", OrderSide.Buy, OrderType.Limit, 1m, 1m, null, TimeInForce.Day, null));

        c.CancelDoesNotReachTheBook = true;
        c.ThrowAfterCancel = new ConnectorTransportException("wire down");
        await gw.CancelAsync(new AgentContext("a"), "f-cancel", placed.ConnectorOrderId!);
        c.ThrowAfterCancel = null;
        c.CancelDoesNotReachTheBook = false;
        c.Inner.Broker.FillWorking(placed.ConnectorOrderId!);   // it filled instead of cancelling

        var result = await gw.ReconcileAsync();

        Assert.True(result.Clean, string.Join("; ", result.Details));
        var record = gw.GetRequest("f-cancel")!;
        Assert.Equal(ExecutionState.REJECTED, record.State);    // the cancel failed; the order is gone
        Assert.Contains("FILLED", record.LastError);
    }

    [Fact]
    public async Task A_modify_the_platform_never_applied_is_never_reconciled_as_cancelled()
    {
        var (gw, c, db) = await Recovery.Ready(new FaultProfile { Fill = FillBehaviour.LeaveWorking }, options: NoGrace);
        using var dbh = db;
        var placed = await gw.PlaceAsync(new AgentContext("a"), "m-place",
            new PlaceIntent("ES", OrderSide.Buy, OrderType.Limit, 1m, 1m, null, TimeInForce.Day, null));

        c.ModifyIgnoresTheRequest = true;
        c.ThrowAfterModify = new ConnectorTransportException("wire down after the change was sent");
        var mod = await gw.ModifyAsync(new AgentContext("a"), "m-mod", placed.ConnectorOrderId!, 5m, null, null);
        Assert.Equal(ExecutionState.UNKNOWN, mod.State);
        c.ThrowAfterModify = null;

        var result = await gw.ReconcileAsync();

        Assert.Equal(1m, c.Inner.Broker.Orders.Single().Quantity);       // the platform never changed it
        var record = gw.GetRequest("m-mod")!;
        Assert.NotEqual(ExecutionState.CANCELLED, record.State);
        Assert.Equal(ExecutionState.REJECTED, record.State);
        Assert.False(record.NeedsReconciliation);
        Assert.True(result.Clean, string.Join("; ", result.Details));
    }

    [Fact]
    public async Task A_modify_the_platform_did_apply_reconciles_as_acknowledged()
    {
        var (gw, c, db) = await Recovery.Ready(new FaultProfile { Fill = FillBehaviour.LeaveWorking }, options: NoGrace);
        using var dbh = db;
        var placed = await gw.PlaceAsync(new AgentContext("a"), "ma-place",
            new PlaceIntent("ES", OrderSide.Buy, OrderType.Limit, 1m, 1m, null, TimeInForce.Day, null));

        // The change lands at the platform and the acknowledgement is lost on the way back.
        c.ThrowAfterModify = new ConnectorTransportException("wire down");
        await gw.ModifyAsync(new AgentContext("a"), "ma-mod", placed.ConnectorOrderId!, 4m, null, null);
        c.ThrowAfterModify = null;
        c.RewriteBook = o => o.ConnectorOrderId == placed.ConnectorOrderId ? o with { Quantity = 4m } : o;

        var result = await gw.ReconcileAsync();

        Assert.True(result.Clean, string.Join("; ", result.Details));
        Assert.Equal(ExecutionState.ACKNOWLEDGED, gw.GetRequest("ma-mod")!.State);
        Assert.True(gw.TryAuthorizeExecution(new AgentContext("a"), out _));
    }

    [Fact]
    public async Task A_cancel_all_press_is_reconciled_by_what_is_left_on_the_book()
    {
        var (gw, c, db) = await Recovery.Ready(new FaultProfile { Fill = FillBehaviour.LeaveWorking }, options: NoGrace);
        using var dbh = db;
        await gw.PlaceAsync(AgentContext.Operator, "ca-1",
            new PlaceIntent("ES", OrderSide.Buy, OrderType.Limit, 1m, 1m, null, TimeInForce.Day, null));
        c.ThrowAfterCancelAll = new ConnectorTransportException("wire down during the sweep");
        c.CancelAllDoesNotReachTheBook = true;

        await Assert.ThrowsAsync<ConnectorTransportException>(() => gw.OperatorCancelAllAsync());
        c.ThrowAfterCancelAll = null;
        c.CancelAllDoesNotReachTheBook = false;

        var result = await gw.ReconcileAsync();

        // An order is still working, so the sweep demonstrably did not happen.
        Assert.Equal(ExecutionState.WORKING, c.Inner.Broker.Orders.Single().State);
        var umbrella = Assert.Single(gw.Requests.Query("intent='CANCEL_ALL'"));
        Assert.NotEqual(ExecutionState.CANCELLED, umbrella.State);
        Assert.Equal(ExecutionState.REJECTED, umbrella.State);
        Assert.Contains("still working", umbrella.LastError);
        Assert.True(result.Clean, string.Join("; ", result.Details));
    }

    [Fact]
    public async Task A_cancel_all_press_that_emptied_the_book_reconciles_as_cancelled()
    {
        var (gw, c, db) = await Recovery.Ready(new FaultProfile { Fill = FillBehaviour.LeaveWorking }, options: NoGrace);
        using var dbh = db;
        await gw.PlaceAsync(AgentContext.Operator, "cb-1",
            new PlaceIntent("ES", OrderSide.Buy, OrderType.Limit, 1m, 1m, null, TimeInForce.Day, null));
        c.ThrowAfterCancelAll = new ConnectorTransportException("wire down after the sweep");

        await Assert.ThrowsAsync<ConnectorTransportException>(() => gw.OperatorCancelAllAsync());
        c.ThrowAfterCancelAll = null;

        var result = await gw.ReconcileAsync();

        Assert.True(result.Clean, string.Join("; ", result.Details));
        Assert.All(gw.Requests.Query("request_id LIKE 'op-cancel-%'"),
            r => Assert.Equal(ExecutionState.CANCELLED, r.State));
        Assert.True(gw.TryAuthorizeExecution(new AgentContext("a"), out _));
    }
}

// =================================================================================================
// ROUND 2 · item 2 — the pause happens in memory, before the database is asked
// =================================================================================================

/// <summary>
/// `RecordIndefinite` persisted first and paused afterwards. Every step of it — the UNKNOWN write,
/// the activity line, the engineering row, the health row — went through the same SQLite connection,
/// so a locked, full or read-only database threw on the first one and the rest never ran: a wire
/// that had been touched, a record still saying DISPATCHING, nothing flagged, and trading open until
/// the aged-dispatch bound noticed thirty seconds later.
/// </summary>
public class UnconfirmedLatchTests
{
    [Fact]
    public async Task An_outcome_that_cannot_be_written_down_pauses_trading_anyway()
    {
        var file = Path.Combine(TestEnv.Home, $"locked-{Guid.NewGuid():n}.db");
        using var db = new Database(file);
        var (gw, c, _) = await Recovery.Ready(db: db);

        // Fail a blocked write on the database's own five-second busy timeout rather than waiting out
        // the provider's thirty-second command default. Thirty seconds is longer than
        // DispatchStrandedAfter, so the aged-dispatch bound would rescue the assertion and hide the
        // very gap this test is about (measured: 31s before this line, 5s after).
        db.Connection.DefaultTimeout = 1;

        // An external writer holds the database while the connector fails. Both of the gateway's
        // attempts to write down what happened will time out on it.
        Microsoft.Data.Sqlite.SqliteConnection? blocker = null;
        c.OnPlaced = () =>
        {
            blocker = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file};Pooling=False");
            blocker.Open();
            using var begin = blocker.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE";
            begin.ExecuteNonQuery();
        };
        c.ThrowAfterPlace = new ConnectorTransportException("connection lost after the order was accepted");

        var thrown = await Record.ExceptionAsync(() => gw.PlaceAsync(new AgentContext("a"), "locked-1", TestEnv.Buy()));

        // The caller is told, because no record of the outcome could be made.
        Assert.NotNull(thrown);
        c.OnPlaced = null;
        c.ThrowAfterPlace = null;

        // THE POINT: nothing in the database says anything is wrong, and trading is refused anyway.
        Assert.Empty(gw.Requests.NeedingReconciliation());
        Assert.Equal(ExecutionState.DISPATCHING, gw.GetRequest("locked-1")!.State);
        var authorized = gw.TryAuthorizeExecution(new AgentContext("a"), out var why, out var code);
        Assert.False(authorized, why);
        Assert.Equal(ErrorCode.TRADING_PAUSED_UNRECONCILED, code);
        Assert.Equal(HealthState.PAUSED, gw.Health.Get(Components.ExecutionCapability).State);
        await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "locked-2", TestEnv.Buy()));
        Assert.Single(c.Inner.Broker.Orders);

        // Rolled back explicitly: disposing a connection with an open transaction is not the same as
        // releasing the write lock, and a pooled one would still be holding it.
        using (var rollback = blocker!.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK";
            rollback.ExecuteNonQuery();
        }
        blocker.Dispose();

        // Still paused with the lock gone: a health refresh must not quietly decide all is well.
        await gw.RefreshHealthAsync();
        Assert.Equal(HealthState.PAUSED, gw.Health.Get(Components.ExecutionCapability).State);
        Assert.False(gw.TryAuthorizeExecution(new AgentContext("a"), out _));

        // ...and a reconcile pass that finds nothing in the database must not clean the pause away.
        var empty = await gw.ReconcileAsync();
        Assert.False(empty.Clean);
        Assert.False(gw.TryAuthorizeExecution(new AgentContext("a"), out _));

        // The failure is not silent: the engineering log carries it once the database is writable.
        var logged = false;
        for (var i = 0; i < 60 && !logged; i++)
        {
            logged = Recovery.Engineering(db, "locked-1").Any(e => e.Event == "record_indefinite_failed" && e.Severity == "error");
            if (!logged) await Task.Delay(50);
        }
        Assert.True(logged, "the persistence failure was never written to the engineering log");

        // The other direction: once the aged bound exposes the stranded row, the reconciler settles
        // it from the broker and trading resumes on its own.
        Recovery.Backdate(db, "locked-1", TimeSpan.FromMinutes(10));
        var result = await gw.ReconcileAsync();
        Assert.True(result.Clean, string.Join("; ", result.Details));
        Assert.Equal(ExecutionState.FILLED, gw.GetRequest("locked-1")!.State);
        Assert.True(gw.TryAuthorizeExecution(new AgentContext("a"), out _));
        Assert.Single(c.Inner.Broker.Orders);
    }

    /// <summary>
    /// The latch is not a one-way door: a person confirming the record on the unconfirmed card lifts
    /// it, exactly as it lifts the database flag.
    /// </summary>
    [Fact]
    public async Task A_person_confirming_the_record_lifts_the_in_memory_pause()
    {
        var (gw, c, db) = await Recovery.Ready();
        using var dbh = db;
        c.ThrowAfterPlace = new ConnectorTransportException("connection lost after the order was accepted");
        var r = await gw.PlaceAsync(new AgentContext("a"), "latch-1", TestEnv.Buy());
        c.ThrowAfterPlace = null;
        Assert.Equal(ExecutionState.UNKNOWN, r.State);
        Assert.False(gw.TryAuthorizeExecution(new AgentContext("a"), out _));

        gw.ForceResolve("latch-1", ExecutionState.FILLED, "checked in ATAS: 1 ES filled");
        await gw.RefreshHealthAsync();

        Assert.True(gw.TryAuthorizeExecution(new AgentContext("a"), out _));
        Assert.Equal(HealthState.READY, gw.Health.Get(Components.ExecutionCapability).State);
    }
}

// =================================================================================================
// ROUND 2 · items 4 and 5 — close all positions, honestly
// =================================================================================================

/// <summary>
/// Two defects in the same loop. It threw on the first position that failed, so the second position
/// got neither a close nor a record — an emergency control that stops half way through an emergency.
/// And it counted a position as closed the moment a close order came back at all, then said "You
/// closed all positions (2)" while both closes were sitting unfilled on the book.
/// </summary>
public class CloseAllOutcomeTests
{
    [Fact]
    public async Task Close_all_keeps_going_after_one_position_fails()
    {
        var (gw, c, db) = await Recovery.Ready();
        using var dbh = db;
        await gw.PlaceAsync(AgentContext.Operator, "cp-es", TestEnv.Buy("ES", 2m));
        await gw.PlaceAsync(AgentContext.Operator, "cp-nq", TestEnv.Buy("NQ", 1m));
        c.SortPositionsBySymbol = true;                 // ES is visited first
        c.ThrowAfterCloseSymbol = "ES";
        c.ThrowAfterClose = new ConnectorTransportException("connection lost after the close was sent");

        await gw.OperatorCloseAllAsync();

        Assert.Equal(2, c.Closes);                      // the second position was still attempted
        var es = Assert.Single(gw.Requests.Query("instrument='ES' AND request_id LIKE 'op-close-%'"));
        var nq = Assert.Single(gw.Requests.Query("instrument='NQ' AND request_id LIKE 'op-close-%'"));
        Assert.Equal(ExecutionState.UNKNOWN, es.State);
        Assert.True(es.NeedsReconciliation);
        Assert.Equal(ExecutionState.FILLED, nq.State);
        Assert.False(nq.NeedsReconciliation);
        Assert.DoesNotContain(c.Inner.Broker.Positions, p => p.Symbol == "NQ");

        // The half that failed still pauses trading and still has a route out.
        Assert.False(gw.TryAuthorizeExecution(new AgentContext("a"), out _, out var code));
        Assert.Equal(ErrorCode.TRADING_PAUSED_UNRECONCILED, code);
    }
}

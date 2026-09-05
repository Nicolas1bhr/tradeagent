using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

// =================================================================================================
// U-stranded — the reconciler never writes off an order whose dispatcher is still alive
//
// REVIEW 2026-09-05 finding 1, executed as P6b: `DispatchStrandedAfter` was the constant 30 s while
// one ordinary order path through `AtasConnector` is 50 s (10 s send gate + 30 s whole frame + 10 s
// reply). A placement legitimately in flight for 30..50 s was therefore "stranded", and — being
// already past `AbsenceGrace` (15 s) the moment the reconciler could see it — was settled CANCELLED,
// "never reached the broker", UNFLAGGED, and trading resumed. Then the order filled.
//
// The arithmetic below is the SHIPPED one, not a scaled model: the clock is movable, so a 50 s worst
// path and a 70 s bound cost nothing to prove.
// =================================================================================================

static class Stranded
{
    /// <summary>`AtasConnector.WorstCaseOrderPath` at shipped values: WriteTimeout + FrameTimeout + rpc.</summary>
    public static readonly TimeSpan AtasOrderPath = TimeSpan.FromSeconds(50);

    /// <summary>What `DispatchStrandedAfter` used to be, written down rather than derived.</summary>
    public static readonly TimeSpan OldConstant = TimeSpan.FromSeconds(30);

    public sealed class Movable(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now = start;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    public static async Task<(TradingGateway Gw, HangingConnector C, Database Db, Movable Clock)> Ready(
        TimeSpan? worstCase = null, GatewayOptions? options = null, Database? db = null)
    {
        db ??= TestEnv.NewDb();
        var clock = new Movable(DateTimeOffset.UtcNow);
        var c = new HangingConnector(new FakeConnector(new FakeBroker())
        {
            WorstCaseOperationPath = worstCase ?? AtasOrderPath
        });
        options ??= new GatewayOptions();
        options.Clock = clock;
        var gw = new TradingGateway(db, c, new HealthRegistry(), options);
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = c.Inner.Broker.AccountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
        });
        await c.ConnectAsync();
        await gw.RefreshHealthAsync();
        return (gw, c, db, clock);
    }

    /// <summary>A row this process never dispatched: the crash-and-restart shape, made by hand.</summary>
    public static void StrandedRow(TradingGateway gw, string id)
    {
        gw.Requests.TryCreate(Recovery.Row(id));
        gw.Requests.Transition(id, ExecutionState.CREATED, ExecutionState.DISPATCHING);
    }
}

/// <summary>
/// Hangs INSIDE the connector call, before the broker sees anything — which is what a bridge stuck
/// in its send gate or half way through a frame looks like. `RecoveryConnector.HangPlace` hangs
/// AFTER the broker accepted, so the reconciler would find the order and adopt it; the finding is
/// about the window where the broker has nothing to find yet.
/// </summary>
sealed class HangingConnector(FakeConnector inner) : ITradingConnector
{
    public FakeConnector Inner => inner;

    /// <summary>Set to make a placement hang until the source is completed.</summary>
    public TaskCompletionSource? HangPlaceBeforeTheBroker;

    /// <summary>Completed the moment a placement is inside the connector call.</summary>
    public readonly TaskCompletionSource Reached = new();

    /// <summary>Thrown instead of reaching the broker, once any hang is released.</summary>
    public Exception? ThrowInsteadOfPlacing;

    public string Id => inner.Id;
    public string DisplayName => inner.DisplayName;
    public ConnectorCapabilities Capabilities => inner.Capabilities;
    public TimeSpan WorstCaseOperationPath => inner.WorstCaseOperationPath;
    public TimeSpan EmergencyBudget => inner.EmergencyBudget;
    public Task ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);
    public Task<HealthState> GetHealthAsync(CancellationToken ct = default) => inner.GetHealthAsync(ct);
    public Task<bool> IsConnectedAsync(CancellationToken ct = default) => inner.IsConnectedAsync(ct);
    public Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken ct = default) => inner.GetAccountsAsync(ct);
    public Task<AccountInfo?> GetAccountAsync(string a, CancellationToken ct = default) => inner.GetAccountAsync(a, ct);
    public Task<IReadOnlyList<InstrumentInfo>> GetInstrumentsAsync(CancellationToken ct = default) => inner.GetInstrumentsAsync(ct);
    public Task<QuoteInfo?> GetQuoteAsync(string s, CancellationToken ct = default) => inner.GetQuoteAsync(s, ct);
    public Task<IReadOnlyList<PositionInfo>> GetPositionsAsync(string a, CancellationToken ct = default) => inner.GetPositionsAsync(a, ct);
    public Task<IReadOnlyList<OrderInfo>> GetOrdersAsync(string a, bool inactive, DateTimeOffset? since, CancellationToken ct = default) =>
        inner.GetOrdersAsync(a, inactive, since, ct);
    public Task<IReadOnlyList<ExecutionInfo>> GetExecutionsAsync(string a, DateTimeOffset? since, CancellationToken ct = default) =>
        inner.GetExecutionsAsync(a, since, ct);

    public async Task<OrderInfo> PlaceOrderAsync(PlaceOrderCommand cmd, CancellationToken ct = default)
    {
        Reached.TrySetResult();
        if (HangPlaceBeforeTheBroker is { } hang) await hang.Task;
        if (ThrowInsteadOfPlacing is { } ex) throw ex;
        return await inner.PlaceOrderAsync(cmd, ct);
    }

    public Task<OrderInfo> ModifyOrderAsync(ModifyOrderCommand c, CancellationToken ct = default) => inner.ModifyOrderAsync(c, ct);
    public Task CancelOrderAsync(string id, CancellationToken ct = default) => inner.CancelOrderAsync(id, ct);
    public Task<IReadOnlyList<string>> CancelAllOrdersAsync(string a, CancellationToken ct = default) => inner.CancelAllOrdersAsync(a, ct);
    public Task<OrderInfo?> ClosePositionAsync(string a, string s, string coid, CancellationToken ct = default) =>
        inner.ClosePositionAsync(a, s, coid, ct);

    public event Action<HealthState>? ConnectionChanged { add => inner.ConnectionChanged += value; remove => inner.ConnectionChanged -= value; }
    public event Action<QuoteInfo>? QuoteChanged { add => inner.QuoteChanged += value; remove => inner.QuoteChanged -= value; }
    public event Action<OrderInfo>? OrderChanged { add => inner.OrderChanged += value; remove => inner.OrderChanged -= value; }
    public event Action<ExecutionInfo>? ExecutionReceived { add => inner.ExecutionReceived += value; remove => inner.ExecutionReceived -= value; }
    public event Action<PositionInfo>? PositionChanged { add => inner.PositionChanged += value; remove => inner.PositionChanged -= value; }
    public event Action<AccountInfo>? AccountChanged { add => inner.AccountChanged += value; remove => inner.AccountChanged -= value; }
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

// =================================================================================================
// Item 1 — the bound derives from the connector
// =================================================================================================

public class StrandedBoundDerivationTests(ITestOutputHelper log)
{
    /// <summary>
    /// The bound is the LIVE CONNECTOR's worst case plus a stated slack, the way the shutdown drain
    /// is, rather than a number written down beside it. Two connectors with different deadlines, the
    /// same code, two different answers — and neither of them is 30 s.
    /// </summary>
    [Fact]
    public async Task The_stranded_bound_follows_the_connectors_own_worst_case_rather_than_a_constant()
    {
        var (gw, c, db, clock) = await Stranded.Ready(worstCase: Stranded.AtasOrderPath);
        using var dbh = db;
        Stranded.StrandedRow(gw, "derive-50");

        // Past the constant that used to be the bound, and still well inside one ordinary order
        // path: this is exactly the window in which the reconciler wrote off a live order.
        clock.Advance(Stranded.OldConstant + TimeSpan.FromSeconds(10));
        log.WriteLine($"connector worst path : {c.WorstCaseOperationPath.TotalSeconds:0}s");
        log.WriteLine($"at 40s  unconfirmed  : {gw.Unreconciled().Count}   (the old constant said 1)");
        Assert.Empty(gw.Unreconciled());

        clock.Advance(TimeSpan.FromSeconds(29));                     // 69 s: inside 50 + 20
        log.WriteLine($"at 69s  unconfirmed  : {gw.Unreconciled().Count}");
        Assert.Empty(gw.Unreconciled());

        clock.Advance(TimeSpan.FromSeconds(2));                      // 71 s: past it
        log.WriteLine($"at 71s  unconfirmed  : {gw.Unreconciled().Count}");
        Assert.Single(gw.Unreconciled());

        // The same code over a connector with different deadlines gives a different bound.
        var (slow, sc, db2, clock2) = await Stranded.Ready(worstCase: TimeSpan.FromSeconds(100));
        using var dbh2 = db2;
        Stranded.StrandedRow(slow, "derive-100");
        clock2.Advance(TimeSpan.FromSeconds(119));
        log.WriteLine($"slow connector worst : {sc.WorstCaseOperationPath.TotalSeconds:0}s");
        log.WriteLine($"at 119s unconfirmed  : {slow.Unreconciled().Count}");
        Assert.Empty(slow.Unreconciled());
        clock2.Advance(TimeSpan.FromSeconds(2));                     // 121 s: past 100 + 20
        log.WriteLine($"at 121s unconfirmed  : {slow.Unreconciled().Count}");
        Assert.Single(slow.Unreconciled());

        await gw.DisposeAsync();
        await slow.DisposeAsync();
    }

    /// <summary>
    /// P6b, at the shipped arithmetic. A placement that is STILL inside the connector call at 40 s —
    /// past the old constant, inside the connector's own 50 s worst path — must be an ordinary order
    /// in flight: not unconfirmed work, not written off, and the broker's real answer is what the
    /// record ends up carrying.
    /// </summary>
    [Fact]
    public async Task A_placement_still_inside_the_connectors_worst_path_is_not_written_off()
    {
        var (gw, c, db, clock) = await Stranded.Ready();
        using var dbh = db;
        var release = new TaskCompletionSource();
        c.HangPlaceBeforeTheBroker = release;

        var inFlight = gw.PlaceAsync(new AgentContext("a"), "inflight-1", TestEnv.Buy());
        await c.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ExecutionState.DISPATCHING, gw.GetRequest("inflight-1")!.State);
        Assert.Empty(c.Inner.Broker.Orders);                          // nothing at the broker YET

        clock.Advance(Stranded.OldConstant + TimeSpan.FromSeconds(10));   // 40 s on the wire
        log.WriteLine($"unconfirmed work at 40s : {gw.HasUnconfirmedWork()}");
        Assert.False(gw.HasUnconfirmedWork());

        var result = await gw.ReconcileAsync();
        var during = gw.GetRequest("inflight-1")!;
        log.WriteLine($"reconcile               : resolved={result.Resolved} inconclusive={result.Inconclusive}");
        log.WriteLine($"detail                  : {string.Join("; ", result.Details)}");
        log.WriteLine($"record after reconcile  : {during.State}, needs_reconciliation={during.NeedsReconciliation}");
        Assert.Equal(0, result.Resolved);
        Assert.Equal(ExecutionState.DISPATCHING, during.State);

        release.SetResult();
        var placed = await inFlight;
        var final = gw.GetRequest("inflight-1")!;
        log.WriteLine($"orders at the broker    : {c.Inner.Broker.Orders.Count} -> " +
                      $"{string.Join(",", c.Inner.Broker.Orders.Select(o => $"{o.ConnectorOrderId} {o.State}"))}");
        log.WriteLine($"record now              : {final.State}, needs_reconciliation={final.NeedsReconciliation}, last_error={final.LastError}");

        Assert.Equal(ExecutionState.FILLED, placed.State);
        Assert.Equal(ExecutionState.FILLED, final.State);
        Assert.False(final.NeedsReconciliation);
        Assert.Null(final.LastError);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// The other half of item 1: absence is judged from the LATER of the dispatch time and the bound.
    /// A record whose dispatcher this process never saw — a crash, a restart, another process — is
    /// past the bound the moment it becomes visible, so measuring the grace from the dispatch made
    /// the grace a no-op on exactly the records it exists to protect.
    /// </summary>
    [Fact]
    public async Task Absence_is_not_evidence_until_a_whole_grace_window_after_the_bound()
    {
        var (gw, _, db, clock) = await Stranded.Ready();
        using var dbh = db;
        Stranded.StrandedRow(gw, "restarted-1");

        clock.Advance(TimeSpan.FromSeconds(75));       // past the 70 s bound, inside 70 + 15
        Assert.True(gw.HasUnconfirmedWork());
        var early = await gw.ReconcileAsync();
        var mid = gw.GetRequest("restarted-1")!;
        log.WriteLine($"at 75s : resolved={early.Resolved} inconclusive={early.Inconclusive} state={mid.State}");
        log.WriteLine($"detail : {string.Join("; ", early.Details)}");
        Assert.Equal(0, early.Resolved);
        Assert.Equal(1, early.Inconclusive);
        Assert.NotEqual(ExecutionState.CANCELLED, mid.State);
        Assert.True(mid.NeedsReconciliation);
        Assert.False(gw.TryAuthorizeExecution(new AgentContext("a"), out _));

        clock.Advance(TimeSpan.FromSeconds(15));       // 90 s: past the bound AND the grace after it
        var late = await gw.ReconcileAsync();
        var settled = gw.GetRequest("restarted-1")!;
        log.WriteLine($"at 90s : resolved={late.Resolved} state={settled.State} last_error={settled.LastError}");
        Assert.Equal(1, late.Resolved);
        Assert.Equal(ExecutionState.CANCELLED, settled.State);
        Assert.Contains("never reached", settled.LastError!);
        await gw.DisposeAsync();
    }
}

// =================================================================================================
// Item 2 — a live dispatcher owns its record
// =================================================================================================

/// <summary>
/// REVIEW 2026-09-05 UNVERIFIED 4. `ReconcileAsync` moved a stranded DISPATCHING row to UNKNOWN and
/// on to RECONCILING while the handler flying it was still inside the connector call; the handler's
/// later `Settle` then lost the CAS and was filed `already_settled`, discarding the broker's real
/// answer. Both halves are here: the reconciler leaves a live dispatcher's row alone, and a definite
/// answer that arrives after some OTHER party moved the row still wins.
/// </summary>
public class LiveDispatcherOwnsItsRowTests(ITestOutputHelper log)
{
    [Fact]
    public async Task The_reconciler_leaves_a_row_alone_while_its_dispatcher_is_inside_the_call()
    {
        var (gw, c, db, clock) = await Stranded.Ready();
        using var dbh = db;
        var release = new TaskCompletionSource();
        c.HangPlaceBeforeTheBroker = release;

        var inFlight = gw.PlaceAsync(new AgentContext("a"), "owned-1", TestEnv.Buy());
        await c.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Well past the bound: this IS unconfirmed work and trading is right to be paused. What it
        // is not is a record anybody else may settle.
        clock.Advance(TimeSpan.FromSeconds(120));
        Assert.True(gw.HasUnconfirmedWork());

        var result = await gw.ReconcileAsync();
        var during = gw.GetRequest("owned-1")!;
        var tradingResumed = gw.TryAuthorizeExecution(new AgentContext("a"), out _, out _);
        log.WriteLine($"reconcile              : resolved={result.Resolved} inconclusive={result.Inconclusive}");
        log.WriteLine($"detail                 : {string.Join("; ", result.Details)}");
        log.WriteLine($"record during          : {during.State}, needs_reconciliation={during.NeedsReconciliation}");
        log.WriteLine($"trading resumed        : {tradingResumed}");

        Assert.Equal(0, result.Resolved);
        Assert.Equal(1, result.Inconclusive);
        Assert.Equal(ExecutionState.DISPATCHING, during.State);
        Assert.False(tradingResumed);

        // ...and the broker's own answer is what the record ends up carrying.
        release.SetResult();
        var placed = await inFlight;
        var final = gw.GetRequest("owned-1")!;
        log.WriteLine($"orders at the broker   : {c.Inner.Broker.Orders.Count}");
        log.WriteLine($"record now             : {final.State}, needs_reconciliation={final.NeedsReconciliation}");
        Assert.Equal(ExecutionState.FILLED, placed.State);
        Assert.Equal(ExecutionState.FILLED, final.State);
        Assert.False(final.NeedsReconciliation);
        Assert.DoesNotContain(Recovery.Engineering(db, "owned-1"), e => e.Event == "already_settled");
        await gw.DisposeAsync();
    }

    /// <summary>
    /// The half the lease cannot cover: a SECOND gateway over the same store — the shape the app and
    /// `GatewayHost` really are — sweeps the row at construction and reconciles it, because nothing
    /// in that process knows a dispatcher is alive. The dispatcher's answer arrives afterwards, and
    /// it is the broker's own word about this very request: it must land on the record rather than
    /// be filed `already_settled`.
    /// </summary>
    [Fact]
    public async Task A_definite_answer_wins_over_a_row_somebody_else_moved_to_reconciling()
    {
        var (gw, c, db, _) = await Stranded.Ready();
        using var dbh = db;
        var release = new TaskCompletionSource();
        c.HangPlaceBeforeTheBroker = release;

        var inFlight = gw.PlaceAsync(new AgentContext("a"), "raced-1", TestEnv.Buy());
        await c.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ExecutionState.DISPATCHING, gw.GetRequest("raced-1")!.State);

        // The other process. Its own connector cannot prove order history, so its reconcile pass
        // parks the row at RECONCILING instead of settling it — which is exactly the window in
        // which the real answer used to be thrown away.
        var other = new TradingGateway(db, new FakeConnector(c.Inner.Broker, new FaultProfile { HideOrderHistory = true }),
            new HealthRegistry());
        Assert.Equal(ExecutionState.UNKNOWN, gw.GetRequest("raced-1")!.State);      // its constructor swept the row
        var swept = await other.ReconcileAsync();
        log.WriteLine($"other gateway reconcile : resolved={swept.Resolved} inconclusive={swept.Inconclusive}");
        log.WriteLine($"record after the sweep  : {gw.GetRequest("raced-1")!.State}");
        Assert.Equal(ExecutionState.RECONCILING, gw.GetRequest("raced-1")!.State);

        release.SetResult();
        var placed = await inFlight;
        var final = gw.GetRequest("raced-1")!;
        var engineering = Recovery.Engineering(db, "raced-1").Select(e => e.Event).ToList();
        log.WriteLine($"dispatch answered       : {placed.State}");
        log.WriteLine($"record now              : {final.State}, needs_reconciliation={final.NeedsReconciliation}");
        log.WriteLine($"engineering             : {string.Join(", ", engineering)}");

        Assert.Equal(ExecutionState.FILLED, final.State);
        Assert.False(final.NeedsReconciliation);
        Assert.DoesNotContain("already_settled", engineering);
        Assert.Contains("late_definite_settle", engineering);
        await other.DisposeAsync();
        await gw.DisposeAsync();
    }

    /// <summary>
    /// The other direction, so the lease cannot become a way to hold a row out of reconciliation for
    /// ever: a dispatch that has FINISHED releases its claim, and the record it left behind is
    /// settled on the ordinary rule — the grace from its own dispatch, because this process watched
    /// the wire go quiet.
    /// </summary>
    [Fact]
    public async Task A_dispatch_that_has_ended_no_longer_holds_its_row()
    {
        var (gw, c, db, clock) = await Stranded.Ready();
        using var dbh = db;
        c.ThrowInsteadOfPlacing = new ConnectorTransportException("the bridge went away mid-frame");

        var placed = await gw.PlaceAsync(new AgentContext("a"), "ended-1", TestEnv.Buy());
        Assert.Equal(ExecutionState.UNKNOWN, placed.State);
        Assert.True(placed.NeedsReconciliation);
        Assert.Empty(c.Inner.Broker.Orders);

        // The dispatcher is gone, so the grace runs from the dispatch itself and nothing has to wait
        // out the whole stranded bound on top of it.
        clock.Advance(new GatewayOptions().AbsenceGrace);
        var result = await gw.ReconcileAsync();
        var final = gw.GetRequest("ended-1")!;
        log.WriteLine($"reconcile at grace : resolved={result.Resolved} inconclusive={result.Inconclusive}");
        log.WriteLine($"record now         : {final.State}, last_error={final.LastError}");
        Assert.Equal(1, result.Resolved);
        Assert.Equal(ExecutionState.CANCELLED, final.State);
        Assert.Contains("never reached", final.LastError!);
        Assert.True(gw.TryAuthorizeExecution(new AgentContext("a"), out _));
        await gw.DisposeAsync();
    }
}

// =================================================================================================
// Item 3 — the row says what the owner needs
// =================================================================================================

public class InconclusiveReasonTests(ITestOutputHelper log)
{
    /// <summary>
    /// "A dispatch is still in progress" is true and useless. The person looking at a paused machine
    /// needs the two numbers that say whether to wait or to go and look in the platform: how long
    /// this one has been on the wire, and how long the connector says one call can possibly take.
    /// </summary>
    [Fact]
    public async Task An_inconclusive_dispatch_says_how_long_it_has_been_on_the_wire_and_of_what()
    {
        var (gw, c, db, clock) = await Stranded.Ready();
        using var dbh = db;
        var release = new TaskCompletionSource();
        c.HangPlaceBeforeTheBroker = release;

        var inFlight = gw.PlaceAsync(new AgentContext("a"), "wire-1", TestEnv.Buy());
        await c.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(90));

        var result = await gw.ReconcileAsync();
        var detail = Assert.Single(result.Details);
        log.WriteLine($"detail : {detail}");
        Assert.Equal("wire-1: still on the wire for 90s of a possible 50s", detail);

        release.SetResult();
        await inFlight;
        await gw.DisposeAsync();
    }
}

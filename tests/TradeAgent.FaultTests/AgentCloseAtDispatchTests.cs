using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

// =================================================================================================
// U-press-inflight item 2 — the agent's own `close` is the same class as REVIEW 2026-09-05b
// finding 2, reached from the agent's side (Codex F3, TradingGateway.cs:1855)
//
// `CloseAsync` reads the position, decides a side and a size from it, and hands them to `PlaceAsync`
// — which then makes four awaited connector reads before anything reaches the wire. A fill landing
// in that window turns the close into a new position: closing 2 of a position that is now 1 opens a
// short, and closing a long that has already flipped doubles it. The re-read is now at DISPATCH,
// inside the dispatch gate, and a position that moved is REFUSED — the same rule the emergency press
// has kept since `U-press-atomic`: a different position is a different decision.
// =================================================================================================

/// <summary>
/// Hangs inside a READ that <c>PlaceAsync</c> makes after the caller sized its order — the shape
/// Codex F3 names: "a position read before PlaceAsync's awaited account and risk reads". The quote
/// is the last read before the dispatch gate, so holding it there parks a close that has already
/// decided its side and its size.
/// </summary>
sealed class HangingReadConnector(FakeConnector inner) : ITradingConnector
{
    public FakeConnector Inner => inner;

    /// <summary>Set to make the quote read hang until the source is completed.</summary>
    public TaskCompletionSource? HangQuote;

    /// <summary>Completed the moment a caller is inside the quote read.</summary>
    public readonly TaskCompletionSource QuoteReached = new();

    /// <summary>Set to hold a modify INSIDE the connector call — the shape probe P6 uses for a close.</summary>
    public TaskCompletionSource? HangModify;

    /// <summary>Completed the moment a caller is inside the modify.</summary>
    public readonly TaskCompletionSource ModifyReached = new();

    /// <summary>
    /// Holds the FIRST cancel only. One-shot on purpose: the press's own cancel leg goes through the
    /// same method, and a hang that caught it too would deadlock the thing being measured.
    /// </summary>
    public TaskCompletionSource? HangFirstCancel;

    /// <summary>Completed the moment a caller is inside the first cancel.</summary>
    public readonly TaskCompletionSource CancelReached = new();

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

    public async Task<QuoteInfo?> GetQuoteAsync(string s, CancellationToken ct = default)
    {
        QuoteReached.TrySetResult();
        if (HangQuote is { } hang) await hang.Task;
        return await inner.GetQuoteAsync(s, ct);
    }

    public Task<IReadOnlyList<PositionInfo>> GetPositionsAsync(string a, CancellationToken ct = default) => inner.GetPositionsAsync(a, ct);
    public Task<IReadOnlyList<OrderInfo>> GetOrdersAsync(string a, bool inactive, DateTimeOffset? since, CancellationToken ct = default) =>
        inner.GetOrdersAsync(a, inactive, since, ct);
    public Task<IReadOnlyList<ExecutionInfo>> GetExecutionsAsync(string a, DateTimeOffset? since, CancellationToken ct = default) =>
        inner.GetExecutionsAsync(a, since, ct);
    public Task<OrderInfo> PlaceOrderAsync(PlaceOrderCommand cmd, CancellationToken ct = default) => inner.PlaceOrderAsync(cmd, ct);
    public async Task<OrderInfo> ModifyOrderAsync(ModifyOrderCommand c, CancellationToken ct = default)
    {
        ModifyReached.TrySetResult();
        if (HangModify is { } hang) await hang.Task;
        return await inner.ModifyOrderAsync(c, ct);
    }

    public async Task CancelOrderAsync(string id, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref HangFirstCancel, null) is { } hang)
        {
            CancelReached.TrySetResult();
            await hang.Task;
        }
        await inner.CancelOrderAsync(id, ct);
    }
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

static class SlowRead
{
    /// <summary>A gateway allowed to trade, over a connector that can park one read.</summary>
    public static async Task<(TradingGateway Gw, HangingReadConnector C, Database Db)> Ready()
    {
        var db = TestEnv.NewDb();
        var c = new HangingReadConnector(new FakeConnector(new FakeBroker()));
        var gw = new TradingGateway(db, c, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = c.Inner.Broker.AccountId;
            s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments];
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
        });
        await c.ConnectAsync();
        await gw.RefreshHealthAsync();
        return (gw, c, db);
    }

    /// <summary>A fill nobody in TradeAgent asked for: the broker's own book moves under the gateway.</summary>
    public static void FillAtTheBroker(FakeBroker broker, string symbol, OrderSide side, decimal qty) =>
        broker.Accept(new PlaceOrderCommand($"OTHER-{Guid.NewGuid():n}"[..20], broker.AccountId, symbol,
            side, OrderType.Market, qty, null, null, TimeInForce.Day, "somebody else"), FillBehaviour.FillImmediately);

}

// =================================================================================================
// Item 2 — the agent's own close is sized at dispatch, not at the snapshot (Codex F3)
// =================================================================================================

public class AgentCloseIsSizedAtDispatchTests(ITestOutputHelper Out)
{
    /// <summary>
    /// Codex F3's own refutation: block a later connector read, change the position after the
    /// initial snapshot, and assert the stale close is recomputed or refused. It is refused —
    /// the same rule the press has kept since `U-press-atomic`: a different position is a different
    /// decision, and nothing is sent on it.
    /// </summary>
    [Fact]
    public async Task A_fill_between_the_snapshot_and_the_wire_stops_the_close_reversing_the_position()
    {
        var (gw, c, db) = await SlowRead.Ready();
        using var dbh = db;
        var account = c.Inner.Broker.AccountId;

        await gw.PlaceAsync(new AgentContext("ai"), "f3-open", TestEnv.Buy(qty: 2m));
        Out.WriteLine($"position before         : {await InFlight.Pos(c, account)}");

        // The close sizes itself Sell 2 from that, then parks in the quote read.
        var release = new TaskCompletionSource();
        c.HangQuote = release;
        var closing = gw.CloseAsync(new AgentContext("ai"), "f3-close", "ES");
        await c.QuoteReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // A fill lands while it waits: half the position is already gone.
        SlowRead.FillAtTheBroker(c.Inner.Broker, "ES", OrderSide.Sell, 1m);
        Out.WriteLine($"position while it waits : {await InFlight.Pos(c, account)}");
        release.SetResult();

        var refused = await Assert.ThrowsAsync<GatewayDeniedException>(() => closing);
        Out.WriteLine($"agent close             : {refused.Code} — {refused.Message}");
        Out.WriteLine($"record                  : {gw.GetRequest("f3-close")?.State.ToString() ?? "none"}");
        Out.WriteLine($"orders at the broker    : {c.Inner.Broker.Orders.Count} " +
                      string.Join(" ", c.Inner.Broker.Orders.Select(o => $"{o.ConnectorOrderId} {o.Side} {o.Quantity} {o.State}")));
        Out.WriteLine($"position at the end     : {await InFlight.Pos(c, account)}");

        Assert.Equal(ErrorCode.POSITION_MOVED, refused.Code);
        Assert.Equal(2, c.Inner.Broker.Orders.Count);                       // the open and the stranger's fill
        Assert.Equal(ExecutionState.CREATED, gw.GetRequest("f3-close")!.State);
        Assert.Equal(1m, (await c.GetPositionsAsync(account)).Single(p => p.Symbol == "ES").Quantity);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// The ordinary close, with nothing moving under it, still reaches the broker and still flattens.
    /// A guard that refuses everything is not a guard.
    /// </summary>
    [Fact]
    public async Task A_close_against_the_position_it_was_sized_from_is_sent()
    {
        var (gw, c, db) = await SlowRead.Ready();
        using var dbh = db;
        var account = c.Inner.Broker.AccountId;

        await gw.PlaceAsync(new AgentContext("ai"), "ok-open", TestEnv.Buy(qty: 2m));
        var leg = await gw.CloseAsync(new AgentContext("ai"), "ok-close", "ES");
        Out.WriteLine($"agent close             : {leg?.State.ToString() ?? "null"}");
        Out.WriteLine($"position at the end     : {await InFlight.Pos(c, account)}");

        Assert.Equal(ExecutionState.FILLED, leg!.State);
        Assert.DoesNotContain(await c.GetPositionsAsync(account), p => p.Symbol == "ES" && p.Quantity != 0);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// A position that flipped is the direction that doubles rather than closes: sized Sell 2 on a
    /// long, dispatched against a short, the order this call computed ADDS two more short.
    /// </summary>
    [Fact]
    public async Task A_position_that_flipped_while_the_close_waited_is_refused_rather_than_doubled()
    {
        var (gw, c, db) = await SlowRead.Ready();
        using var dbh = db;
        var account = c.Inner.Broker.AccountId;

        await gw.PlaceAsync(new AgentContext("ai"), "flip-open", TestEnv.Buy(qty: 2m));
        var release = new TaskCompletionSource();
        c.HangQuote = release;
        var closing = gw.CloseAsync(new AgentContext("ai"), "flip-close", "ES");
        await c.QuoteReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        SlowRead.FillAtTheBroker(c.Inner.Broker, "ES", OrderSide.Sell, 4m);
        Out.WriteLine($"position while it waits : {await InFlight.Pos(c, account)}");
        release.SetResult();

        var refused = await Assert.ThrowsAsync<GatewayDeniedException>(() => closing);
        Out.WriteLine($"agent close             : {refused.Code} — {refused.Message}");
        Out.WriteLine($"position at the end     : {await InFlight.Pos(c, account)}");

        Assert.Equal(ErrorCode.POSITION_MOVED, refused.Code);
        Assert.Equal(-2m, (await c.GetPositionsAsync(account)).Single(p => p.Symbol == "ES").Quantity);
        await gw.DisposeAsync();
    }
}

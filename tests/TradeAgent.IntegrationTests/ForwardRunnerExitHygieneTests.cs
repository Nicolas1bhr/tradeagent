using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Paper;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// A REFUSED EXIT PUTS THE PROTECTION BACK (<c>U-runner-exit-hygiene-b</c>): every exit takes the run's stop and
/// target off the book before it goes, and when a gate then refuses the exit before the wire, the run's stop and target
/// go back at their own levels on that same minute — never while an exit is in flight.
///
/// <para>Same harness as the rest of this class: the envelope through the two-witness card, the allocation and the
/// deployment by the app's own sweeps, the minutes into <c>forward_bar</c> and to the paper connector through the
/// shipped adapter, and every order through <c>PlaceAsync</c>. No venue is reached.</para>
/// </summary>
public partial class ForwardRunnerTests
{
    /// <summary>
    /// AN HOURLY RUN WITH ITS ENTRY FILLED AND ITS PROTECTION RESTING — the stop wide, 15 % under the price paid, so the
    /// 14:00 hour can close at 89 and decide the program's exit without touching it — held through to the minute before
    /// that hour closes. Answers the stop and the target the entry's fill put on the book.
    /// </summary>
    static async Task<(DeploymentOpRow Stop, DeploymentOpRow Target)> ProtectedToTheExitHourAsync(Rig rig)
    {
        await EnteredAsync(rig);
        rig.Minutes(62, HourClose(3) - 1, _ => 100m);
        await rig.Gw.RefreshHealthAsync();
        Assert.Single(await rig.Runner.AdvanceAsync());
        Assert.Equal(1m, await Position(rig));

        var ops = rig.Gw.Deployments.OpsOf(rig.Deployment.Id);
        var stop = Assert.Single(ops, o => o.Kind == DeploymentOpKind.Stop);
        var target = Assert.Single(ops, o => o.Kind == DeploymentOpKind.Target);
        Assert.Equal(85m, (await OrderOf(rig, stop)).StopPrice);
        Assert.Equal(101m, (await OrderOf(rig, target)).LimitPrice);
        Assert.All(new[] { stop, target }, o => Assert.Equal(61, MinuteOf(rig, o)));
        return (stop, target);
    }

    /// <summary>The run's stop and target orders WORKING at the wire now, with the operation each was written under.</summary>
    static async Task<List<(DeploymentOpRow Op, OrderInfo Order)>> WorkingProtection(Rig rig)
    {
        var wire = await Wire(rig);
        return
        [
            .. rig.Gw.Deployments.OpsOf(rig.Deployment.Id)
                .Where(o => o.Kind is DeploymentOpKind.Stop or DeploymentOpKind.Target)
                .Select(o => (Op: o, Order: wire.FirstOrDefault(w => w.ClientOrderId == TradingGateway.ClientOrderIdFor(o.RequestId))))
                .Where(p => p.Order is { State: ExecutionState.WORKING or ExecutionState.ACKNOWLEDGED })
                .Select(p => (p.Op, p.Order!))
        ];
    }

    // ---------------------------------------------------------------- (c) the refused exit puts it back

    /// <summary>
    /// (c) AN EXIT REFUSED BEFORE THE WIRE PUTS THE RUN'S STOP AND TARGET BACK AT THEIR OWN LEVELS, UNDER THE REFUSAL'S
    /// OWN MINUTE'S IDS, IN THE PASS THAT WAS REFUSED.
    ///
    /// <para>The 14:00 hour closes at 89 and the program exits, but the pass that decides on it runs past the decision's
    /// bound — an hour of <c>max_decision_age</c> from the hour's close — so the gateway refuses the exit
    /// <c>DECISION_EXPIRED</c>, between the write-ahead row and the wire. The exit's two cancels carry no decision and
    /// pass: they had already taken the stop and the target off the book, and the position, still long, was left with
    /// neither. The decision is past its bound, so it is never sent again either.</para>
    ///
    /// <para><b>RED on the base</b>: both protective orders <c>CANCELLED</c>, none working, the books long.
    /// <b>Mutant (iii)</b> — the refused-exit condition dropped — stays green here and turns (d) red.</para>
    /// </summary>
    [Fact]
    public async Task A_refused_exit_puts_the_stop_and_target_back_on_the_minute_it_was_refused()
    {
        await using var rig = await ReadyAsync(HourlyText("stop percent 15\ntarget percent 1\n"));
        var (stop, target) = await ProtectedToTheExitHourAsync(rig);

        // THE 14:00 HOUR CLOSES AT 89, NOTHING RESTING TOUCHED, AND THE PASS RUNS AN HOUR AND TWO MINUTES AFTER IT CLOSED.
        rig.Bar(HourClose(3), 100m, 100m, 89m, 89m);
        await rig.Gw.RefreshHealthAsync();
        rig.Clock.At = rig.Origin.AddMinutes(HourClose(3) + 1).AddHours(1).AddMinutes(2);
        var state = Assert.Single(await rig.Runner.AdvanceAsync());
        Show(log, rig);
        log.WriteLine($"after the refused pass: {Said(rig, state)}");

        // THE PREMISE: the exit refused DECISION_EXPIRED before the wire, its two cancels through.
        var written = rig.Gw.Deployments.OpsOf(rig.Deployment.Id).Where(o => MinuteOf(rig, o) == HourClose(3)).ToList();
        var exit = Assert.Single(written, o => o.Kind == DeploymentOpKind.Exit);
        Assert.Equal(DeploymentOpState.Refused, exit.State);
        Assert.Contains(ErrorCode.DECISION_EXPIRED.ToString(), exit.Answer);
        Assert.Equal(ExecutionState.CREATED, rig.Gw.Requests.Get(exit.RequestId)!.State);
        Assert.Equal(2, written.Count(o => o.Kind == DeploymentOpKind.Cancel && o.State == DeploymentOpState.Resolved));
        Assert.Equal(ExecutionState.CANCELLED, (await OrderOf(rig, stop)).State);
        Assert.Equal(ExecutionState.CANCELLED, (await OrderOf(rig, target)).State);
        Assert.Equal(PositionSide.Long, state.Account!.Position);
        Assert.Equal(1m, await Position(rig));

        // ONE STOP AND ONE TARGET OF THE RUN WORKING AGAIN, AT THE ENTRY'S LEVELS, SIZED FROM THE BOOKS, WRITTEN ON THE
        // MINUTE OF THE REFUSAL AND AFTER THE EXIT IN ITS SEQUENCE, EACH UNDER ITS OWN CLIENT ORDER ID.
        var working = await WorkingProtection(rig);
        foreach (var (op, order) in working)
            log.WriteLine($"working {Said(rig, op.RequestId)} {op.Kind} {order.Type} {order.Side} {order.Quantity} stop={order.StopPrice} limit={order.LimitPrice}");
        var back = Assert.Single(working, w => w.Op.Kind == DeploymentOpKind.Stop);
        var aim = Assert.Single(working, w => w.Op.Kind == DeploymentOpKind.Target);
        Assert.Equal((OrderType.Stop, OrderSide.Sell, 1m, (decimal?)85m),
            (back.Order.Type, back.Order.Side, back.Order.Quantity, back.Order.StopPrice));
        Assert.Equal((OrderType.Limit, OrderSide.Sell, 1m, (decimal?)101m),
            (aim.Order.Type, aim.Order.Side, aim.Order.Quantity, aim.Order.LimitPrice));
        Assert.Equal(["+179#0", "+179#1", "+179#2", "+179#3", "+179#4"],
            written.Select(o => Said(rig, o.RequestId)));
        Assert.Equal(["+179#3", "+179#4"], new[] { back.Op, aim.Op }.Select(o => Said(rig, o.RequestId)));
        Assert.All(new[] { back.Op, aim.Op }, o => Assert.Equal(DeploymentOpState.Resolved, o.State));

        // AND A SECOND PASS OVER THE SAME MINUTES PUTS NOTHING BACK AGAIN: everything the refused minute wrote has its
        // answer, so the cursor is over it and it is replayed for its state alone.
        Assert.Single(await rig.Runner.AdvanceAsync());
        Assert.Equal(written.Count, rig.Gw.Deployments.OpsOf(rig.Deployment.Id).Count(o => MinuteOf(rig, o) == HourClose(3)));
        Assert.Equal(2, (await WorkingProtection(rig)).Count);
        Assert.Equal(1m, await Position(rig));
    }

    // ---------------------------------------------------------------- (d) never while the exit is in flight

    /// <summary>
    /// (d) NO STOP OR TARGET IS PUT BACK WHILE THE RUN'S EXIT IS IN FLIGHT — a guard.
    ///
    /// <para>The same hour, decided inside its bound: the exit's cancels take the pair off and the exit itself goes to
    /// the wire, a market order that fills at the next minute's open. After that pass it is working, unfilled, and the
    /// books still read long with nothing protecting them — which is precisely the state the exit is about to end, and a
    /// stop or target put back beside it would fill too, and sell what the exit sells.</para>
    ///
    /// <para><b>Mutant (iii)</b> — the refused-exit condition dropped, so protection goes back whenever the books read
    /// long with none of it working — goes red here.</para>
    /// </summary>
    [Fact]
    public async Task No_stop_or_target_is_put_back_while_the_runs_exit_is_in_flight()
    {
        await using var rig = await ReadyAsync(HourlyText("stop percent 15\ntarget percent 1\n"));
        var (stop, target) = await ProtectedToTheExitHourAsync(rig);

        var state = await MinuteAsync(rig, HourClose(3), 100m, 100m, 89m, 89m);
        Show(log, rig);
        log.WriteLine($"after the exit went out: {Said(rig, state)}");

        // THE PREMISE: the exit at the wire, working and unfilled; the pair cancelled ahead of it; the books long.
        var written = rig.Gw.Deployments.OpsOf(rig.Deployment.Id).Where(o => MinuteOf(rig, o) == HourClose(3)).ToList();
        var exit = Assert.Single(written, o => o.Kind == DeploymentOpKind.Exit);
        Assert.Equal(DeploymentOpState.Dispatched, exit.State);
        var order = await OrderOf(rig, exit);
        Assert.Equal((OrderSide.Sell, ExecutionState.WORKING, 0m), (order.Side, order.State, order.FilledQuantity));
        Assert.Equal(ExecutionState.CANCELLED, (await OrderOf(rig, stop)).State);
        Assert.Equal(ExecutionState.CANCELLED, (await OrderOf(rig, target)).State);
        Assert.Equal(1m, await Position(rig));

        // NOTHING PUT BACK: no stop or target of the run working, and nothing written after the exit on its minute.
        Assert.Empty(await WorkingProtection(rig));
        Assert.Equal([DeploymentOpKind.Cancel, DeploymentOpKind.Cancel, DeploymentOpKind.Exit],
            written.Select(o => o.Kind));
    }
    // ---------------------------------------------------------------- (f) the close intent on every reducing order

    /// <summary>
    /// THE PAPER CONNECTOR, WITH EVERY PLACEMENT THE GATEWAY HANDS IT WRITTEN DOWN WHOLE — the command is where a
    /// connector reads <see cref="OrderIntent"/>, the hook a venue that shorts would honour as reduce-only. Everything
    /// else passes straight through; the rig disposes the paper connector itself.
    /// </summary>
    sealed class PlacementWitness(PaperConnector inner) : ITradingConnector, IConnectorStatusDetail
    {
        readonly List<PlaceOrderCommand> _placed = [];
        int _closes;

        /// <summary>Every <c>PlaceOrderAsync</c> command that reached the connector, in the order it arrived.</summary>
        public IReadOnlyList<PlaceOrderCommand> Placed { get { lock (_placed) return [.. _placed]; } }

        /// <summary>Calls of the connector's own close, which builds its command where nobody can read it.</summary>
        public int Closes => Volatile.Read(ref _closes);

        public string Id => inner.Id;
        public string DisplayName => inner.DisplayName;
        public ConnectorCapabilities Capabilities => inner.Capabilities;
        public TimeSpan WorstCaseOperationPath => inner.WorstCaseOperationPath;
        public TimeSpan EmergencyBudget => inner.EmergencyBudget;
        public string? StatusDetail => inner.StatusDetail;

        public Task ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);
        public Task<HealthState> GetHealthAsync(CancellationToken ct = default) => inner.GetHealthAsync(ct);
        public Task<bool> IsConnectedAsync(CancellationToken ct = default) => inner.IsConnectedAsync(ct);
        public Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken ct = default) => inner.GetAccountsAsync(ct);
        public Task<AccountInfo?> GetAccountAsync(string accountId, CancellationToken ct = default) => inner.GetAccountAsync(accountId, ct);
        public Task<IReadOnlyList<InstrumentInfo>> GetInstrumentsAsync(CancellationToken ct = default) => inner.GetInstrumentsAsync(ct);
        public Task<QuoteInfo?> GetQuoteAsync(string symbol, CancellationToken ct = default) => inner.GetQuoteAsync(symbol, ct);
        public Task<IReadOnlyList<PositionInfo>> GetPositionsAsync(string accountId, CancellationToken ct = default) =>
            inner.GetPositionsAsync(accountId, ct);
        public Task<IReadOnlyList<OrderInfo>> GetOrdersAsync(string accountId, bool includeInactive, DateTimeOffset? since,
            CancellationToken ct = default) => inner.GetOrdersAsync(accountId, includeInactive, since, ct);
        public Task<IReadOnlyList<ExecutionInfo>> GetExecutionsAsync(string accountId, DateTimeOffset? since,
            CancellationToken ct = default) => inner.GetExecutionsAsync(accountId, since, ct);

        public Task<OrderInfo> PlaceOrderAsync(PlaceOrderCommand cmd, CancellationToken ct = default)
        {
            lock (_placed) _placed.Add(cmd);
            return inner.PlaceOrderAsync(cmd, ct);
        }

        public Task<OrderInfo> ModifyOrderAsync(ModifyOrderCommand cmd, CancellationToken ct = default) => inner.ModifyOrderAsync(cmd, ct);
        public Task CancelOrderAsync(string connectorOrderId, CancellationToken ct = default) => inner.CancelOrderAsync(connectorOrderId, ct);
        public Task<IReadOnlyList<string>> CancelAllOrdersAsync(string accountId, CancellationToken ct = default) =>
            inner.CancelAllOrdersAsync(accountId, ct);

        public Task<OrderInfo?> ClosePositionAsync(string accountId, string symbol, string clientOrderId,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _closes);
            return inner.ClosePositionAsync(accountId, symbol, clientOrderId, ct);
        }

        public event Action<HealthState>? ConnectionChanged { add => inner.ConnectionChanged += value; remove => inner.ConnectionChanged -= value; }
        public event Action<QuoteInfo>? QuoteChanged { add => inner.QuoteChanged += value; remove => inner.QuoteChanged -= value; }
        public event Action<OrderInfo>? OrderChanged { add => inner.OrderChanged += value; remove => inner.OrderChanged -= value; }
        public event Action<ExecutionInfo>? ExecutionReceived { add => inner.ExecutionReceived += value; remove => inner.ExecutionReceived -= value; }
        public event Action<PositionInfo>? PositionChanged { add => inner.PositionChanged += value; remove => inner.PositionChanged -= value; }
        public event Action<AccountInfo>? AccountChanged { add => inner.AccountChanged += value; remove => inner.AccountChanged -= value; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// (f) EVERY REDUCING ORDER A RUN OR ITS END SENDS CARRIES <see cref="OrderIntent.Close"/> — a guard.
    ///
    /// <para>The paper book refuses a sell beyond its holding, as the spot venue it simulates does; a venue that shorts
    /// would turn the same sell into a short, and the one thing a connector there can honour is the close intent on the
    /// command — reduce-only. So every sell this run puts on the wire is read where the connector is handed it: the stop
    /// and the target at each entry's fill, a rule exit, the maximum hold's close and the END's close, each one a close,
    /// and every entry an opening order. Sixteen minutes and an END: an entry, its protection, the program's exit; a
    /// second entry held to its maximum; a third, protected, ended by the owner.</para>
    /// </summary>
    [Fact]
    public async Task Every_reducing_order_a_run_or_its_end_sends_carries_the_close_intent()
    {
        PlacementWitness? witness = null;
        await using var rig = await ReadyAsync(
            ProgramText("stop percent 15\ntarget percent 1\nmax_hold_bars 3\n", "entry when close > 99"),
            through: paper => witness = new PlacementWitness(paper));

        (decimal Open, decimal High, decimal Low, decimal Close)[] minutes =
        [
            (99m, 100m, 98m, 100m),              //  1 the entry signals from a close of 100: stop 85, target 101
            (100m, 100.5m, 99.5m, 100m),         //  2 the minute already in progress
            (100m, 100.6m, 99.6m, 100.2m),       //  3 the entry fills at this open; protection goes on
            (100m, 100.4m, 88.5m, 89m),          //  4 `exit when close < 90`: the pair comes off, the exit goes out
            (89m, 89.5m, 88.5m, 89m),            //  5 the minute already in progress
            (89m, 89.5m, 88.5m, 89m),            //  6 the exit fills at this open
            (99m, 100m, 98m, 100m),              //  7 a second entry signals
            (100m, 100.5m, 99.5m, 100m),         //  8 the minute already in progress
            (100m, 100.6m, 99.6m, 100.2m),       //  9 the second entry fills; protection goes on again
            (100.2m, 100.5m, 100m, 100.3m),      // 10 held one bar
            (100.3m, 100.6m, 100.1m, 100.4m),    // 11 held two
            (100.4m, 100.7m, 100.2m, 100.5m),    // 12 held three: the pair comes off, closed at market
            (100.5m, 100.8m, 100.3m, 100.6m),    // 13 the close is in flight
            (100.6m, 100.9m, 100.4m, 100.7m),    // 14 the close fills at this open; a third entry signals
            (100.7m, 100.9m, 100.5m, 100.8m),    // 15 the minute already in progress
            (100.8m, 100.95m, 100.6m, 100.9m),   // 16 the third entry fills; protection goes on
        ];

        for (var i = 0; i < minutes.Length; i++)
        {
            var (open, high, low, close) = minutes[i];
            await MinuteAsync(rig, i + 1, open, high, low, close);
            await Position(rig);
            await rig.Runner.AdvanceAsync();
        }

        Assert.Equal(1m, await Position(rig));
        await rig.Gw.EndPaperDeploymentAsync(rig.Deployment.Id, "test: the owner stopped it");
        Show(log, rig);

        var ops = rig.Gw.Deployments.OpsOf(rig.Deployment.Id)
            .ToDictionary(o => TradingGateway.ClientOrderIdFor(o.RequestId), StringComparer.Ordinal);
        var placed = witness!.Placed;
        foreach (var cmd in placed)
            log.WriteLine($"placed {Said(rig, cmd.ClientOrderId)} {ops[cmd.ClientOrderId].Kind} {cmd.Side} {cmd.Type} "
                          + $"{cmd.Quantity} intent={cmd.Intent}");

        // EVERY SELL A CLOSE, EVERY BUY AN OPENING ORDER, AND NOTHING THROUGH THE CONNECTOR'S OWN CLOSE.
        var sells = placed.Where(c => c.Side == OrderSide.Sell).ToList();
        Assert.All(sells, c => Assert.Equal(OrderIntent.Close, c.Intent));
        Assert.All(placed.Where(c => c.Side == OrderSide.Buy), c => Assert.Equal(OrderIntent.Open, c.Intent));
        Assert.Equal(0, witness.Closes);

        // AND THE GUARD IS NOT EMPTY: each kind of reducing order the run and its END send reached the wire.
        var kinds = sells.Select(c => ops[c.ClientOrderId]).ToList();
        foreach (var kind in new[] { DeploymentOpKind.Stop, DeploymentOpKind.Target, DeploymentOpKind.Exit })
            Assert.Contains(kinds, o => o.Kind == kind);
        Assert.Contains(kinds, o => o.Kind == DeploymentOpKind.Flatten && o.IntentJson.Contains("max_hold_bars", StringComparison.Ordinal));
        Assert.Contains(kinds, o => o.Kind == DeploymentOpKind.Flatten && o.IntentJson.Contains("\"close\"", StringComparison.Ordinal));
    }
}

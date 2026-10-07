using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// A MARKET ORDER WHOSE PLATFORM UPDATE WAS LOST IS READ BACK FROM THE PLATFORM'S OWN ORDER LIST, AND SETTLES ONLY
/// FROM A FINAL ANSWER THE PLATFORM ASSERTS (<c>U-inflight-settle</c>).
///
/// <para><c>U-close-once</c> refuses a close or reduce while an earlier market order moving the position the same way
/// is <c>ACKNOWLEDGED</c>, <c>WORKING</c>, <c>PARTIALLY_FILLED</c> or <c>CANCEL_PENDING</c>, and only the platform's
/// event stream moves a row out of those. A fill or a cancel the platform never reports — the simulator's book moves
/// without raising anything, the ATAS bridge drops an event it had no peer for — left the row there for good, and
/// every close of that position with it. The health pass now asks the platform's history about such a row once it is
/// stale, on the reconciler's own clock (the dispatch, plus <see cref="TradingGateway.DispatchStrandedAfter"/>, plus
/// <see cref="GatewayOptions.AbsenceGrace"/>), and writes a final answer through the stream's own writer.</para>
///
/// <para>Everything is over <see cref="RecordingConnector"/> and the built-in simulator, on a gateway clock this class
/// moves. It starts an hour behind the machine's, because the simulator stamps its book with the machine's clock and
/// the history is asked from five minutes before the order's own record. No venue is reached and no real money is
/// involved.</para>
/// </summary>
public class InFlightSettleTests(ITestOutputHelper log)
{
    static readonly AgentContext Ai = new("ai");

    sealed record Harness(TradingGateway Gw, RecordingConnector C, Database Db, MovableClock Clock, GatewayOptions Options)
    {
        /// <summary>How long after its dispatch a row in flight is asked about: the reconciler's own clock.</summary>
        public TimeSpan Stale => Gw.DispatchStrandedAfter + Options.AbsenceGrace;
    }

    /// <summary>A gateway over the simulator, healthy and allowed to trade, on a clock this class moves.</summary>
    static async Task<Harness> Ready(Database? db = null, RecordingConnector? c = null, MovableClock? clock = null)
    {
        db ??= TestEnv.NewDb();
        c ??= new RecordingConnector(new FakeConnector(new FakeBroker()));
        clock ??= new MovableClock(DateTimeOffset.UtcNow - TimeSpan.FromHours(1));
        c.Inner.QuoteClock = clock;
        var options = new GatewayOptions { Clock = clock };
        var gw = new TradingGateway(db, c, new HealthRegistry(), options);
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = c.Broker.AccountId;
            s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments];
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
        });
        await c.ConnectAsync();
        await gw.RefreshHealthAsync();
        return new Harness(gw, c, db, clock, options);
    }

    /// <summary>A long of <paramref name="qty"/> ES, and the agent's market close of it RESTING at the platform: <c>WORKING</c>.</summary>
    static async Task<ExecutionRequest> AWorkingClose(Harness h, string prefix, decimal qty = 2m)
    {
        await h.Gw.PlaceAsync(Ai, $"{prefix}-open", TestEnv.Buy("ES", qty));
        h.C.Faults.Fill = FillBehaviour.LeaveWorking;
        var first = await h.Gw.CloseAsync(Ai, $"{prefix}-first", "ES");
        h.C.Faults.Fill = FillBehaviour.FillImmediately;
        Assert.NotNull(first);
        Assert.Equal(ExecutionState.WORKING, first!.State);
        return first;
    }

    static decimal Held(RecordingConnector c) =>
        c.Broker.Positions.FirstOrDefault(p => p.Symbol == "ES")?.Quantity ?? 0m;

    /// <summary>A close's outcome: the row it became, or the refusal that stopped it before its row existed.</summary>
    static async Task<(ExecutionRequest? Row, GatewayDeniedException? Refused)> Outcome(Func<Task<ExecutionRequest?>> call)
    {
        try { return (await call(), null); }
        catch (GatewayDeniedException ex) { return (null, ex); }
    }

    static string Said((ExecutionRequest? Row, GatewayDeniedException? Refused) o) =>
        o.Refused is { } r ? $"{r.Code} — {r.Message}" : o.Row is { } row ? $"{row.State}" : "nothing to close";

    /// <summary>
    /// (a) A MARKET CLOSE WHOSE FILL REPORT WAS LOST SETTLES FROM THE PLATFORM, AND THE NEXT CLOSE GOES OUT.
    ///
    /// <para>The agent's close of a long 2 rests, and fills at the platform with no update — the simulator's book moves
    /// and raises nothing. The agent opens 1 more, which nothing in flight refuses, and closes it: refused
    /// <c>CLOSE_IN_FLIGHT</c> over the first, inside the clock and on a health pass there. Past the clock one health
    /// pass reads the first back as <c>FILLED</c>, 2, and the close goes out sized from the re-read position, 1, and
    /// the account is flat.</para>
    ///
    /// <para><b>RED on the base</b>: the first stays <c>WORKING</c> on every pass and the close is refused for good,
    /// with the account long 1.</para>
    /// </summary>
    [Fact]
    public async Task A_market_close_whose_fill_report_was_lost_settles_from_the_platform_and_the_next_close_goes_out()
    {
        var h = await Ready();
        using var dbh = h.Db;
        var first = await AWorkingClose(h, "ifa");

        // IT FILLS AT THE PLATFORM, AND THE PLATFORM'S UPDATE NEVER ARRIVES.
        h.C.Broker.FillWorking(first.ConnectorOrderId!);
        await h.Gw.PlaceAsync(Ai, "ifa-again", TestEnv.Buy("ES", 1m));

        // INSIDE THE CLOCK, A HEALTH PASS ASKS NOTHING AND THE CLOSE IS STILL REFUSED.
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        await h.Gw.RefreshHealthAsync();
        var inside = await Outcome(() => h.Gw.CloseAsync(Ai, "ifa-inside", "ES"));
        var insideRow = h.Gw.GetRequest(first.RequestId)!;

        // PAST IT, ONE HEALTH PASS READS IT BACK.
        h.Clock.Advance(h.Stale);
        await h.Gw.RefreshHealthAsync();
        var settled = h.Gw.GetRequest(first.RequestId)!;
        var placesBefore = h.C.Places;
        var next = await Outcome(() => h.Gw.CloseAsync(Ai, "ifa-next", "ES"));

        log.WriteLine($"inside the clock     : the row {insideRow.State}; the close {Said(inside)}");
        log.WriteLine($"past it              : the row {settled.State}, filled {settled.FilledQuantity} — {settled.LastError ?? "-"}");
        log.WriteLine($"the next close       : {Said(next)}, sized {(h.C.Placed.Count > 0 ? h.C.Placed[^1].Quantity : 0m)}");
        log.WriteLine($"position             : {Held(h.C)}");

        Assert.Equal(ExecutionState.WORKING, insideRow.State);
        Assert.Equal(ErrorCode.CLOSE_IN_FLIGHT, inside.Refused?.Code);
        Assert.Contains(first.RequestId, inside.Refused!.Message);

        Assert.Equal(ExecutionState.FILLED, settled.State);
        Assert.Equal(2m, settled.FilledQuantity);
        Assert.False(settled.NeedsReconciliation);
        Assert.Contains("order history", settled.LastError ?? "", StringComparison.Ordinal);

        Assert.Null(next.Refused);
        Assert.Equal(ExecutionState.FILLED, next.Row!.State);
        Assert.Equal(1, h.C.Places - placesBefore);
        Assert.Equal(1m, h.C.Placed[^1].Quantity);
        Assert.Equal(0m, Held(h.C));
        await h.Gw.DisposeAsync();
    }

    /// <summary>
    /// (c) A CLOSE THE PLATFORM STILL LISTS WORKING CHANGES NOTHING AND IS STILL REFUSED.
    ///
    /// <para>The agent's close rests and nothing has happened to it at the platform. Past the clock the health pass
    /// asks, the platform answers <c>WORKING</c> — a live answer, which settles nothing — and the row is left exactly
    /// as it was: <c>WORKING</c>, not flagged, nothing cancelled, nothing sent. The second close is refused
    /// <c>CLOSE_IN_FLIGHT</c> naming it, and once the price arrives the one close fills and the account is flat.</para>
    /// </summary>
    [Fact]
    public async Task A_close_the_platform_still_lists_working_changes_nothing_and_is_still_refused()
    {
        var h = await Ready();
        using var dbh = h.Db;
        var first = await AWorkingClose(h, "ifc");

        h.Clock.Advance(h.Stale + TimeSpan.FromSeconds(1));
        var readsBefore = h.C.Reads;
        await h.Gw.RefreshHealthAsync();
        var asked = h.C.Reads - readsBefore;
        var row = h.Gw.GetRequest(first.RequestId)!;
        var mutations = h.C.Mutations;
        var second = await Outcome(() => h.Gw.CloseAsync(Ai, "ifc-second", "ES"));

        log.WriteLine($"health pass          : {asked} reads; the row {row.State}, flagged {row.NeedsReconciliation}");
        log.WriteLine($"the second close     : {Said(second)}");
        log.WriteLine($"the platform's book  : {string.Join(" | ", h.C.Broker.Orders.Select(o => $"{o.ConnectorOrderId} {o.Side} {o.Quantity} {o.State}"))}");

        Assert.Equal(ExecutionState.WORKING, row.State);
        Assert.False(row.NeedsReconciliation);
        Assert.Equal(mutations, h.C.Mutations);
        Assert.Equal(ErrorCode.CLOSE_IN_FLIGHT, second.Refused?.Code);
        Assert.Contains(first.RequestId, second.Refused!.Message);
        Assert.Contains(nameof(ExecutionState.WORKING), second.Refused.Message);
        Assert.Null(h.Gw.GetRequest("ifc-second"));

        // PRICE ARRIVES: the one close at the platform fills, and that is the whole of the closing.
        h.C.Broker.FillWorking(h.C.Broker.Orders.Single(o => o.ClientOrderId == first.ClientOrderId).ConnectorOrderId);
        Assert.Equal(1, h.C.Broker.Orders.Count(o => o.Side == OrderSide.Sell));
        Assert.Equal(0m, Held(h.C));
        await h.Gw.DisposeAsync();
    }

    /// <summary>
    /// (d) A CLOSE THE PLATFORM DOES NOT LIST SETTLES ONLY WHERE ABSENCE DECIDES, AND ONLY PAST THE GRACE.
    ///
    /// <para>Each arm is a resting close and then a restart onto the same database: the simulator's book kept, or lost
    /// (the book is memory, so a restarted simulator lists nothing) with a long 1 of the owner's on it. History hidden
    /// (the connector cannot prove its history), a history read that throws, a platform whose closes do not carry the
    /// id they are handed, and the clock not yet past: each stays <c>WORKING</c>, unflagged, and the close is still
    /// refused naming it. A restarted simulator past the clock: <c>CANCELLED</c>, with a last error that names the
    /// empty history and never says it did not reach the platform — the platform answered it — and the close goes
    /// out.</para>
    ///
    /// <para><b>RED on the base</b>: the last arm stays <c>WORKING</c>. <b>The mutant</b> — absence settling without
    /// <c>AbsenceDecidesALostClose</c> — turns the closes-carry-no-id arm <c>CANCELLED</c>.</para>
    /// </summary>
    [Fact]
    public async Task A_close_the_platform_does_not_list_settles_only_where_absence_decides_and_past_the_grace()
    {
        var hidden = await Arm("ifd-hidden", bookLost: false, pastTheClock: true, c => c.Faults.HideOrderHistory = true);
        var throws = await Arm("ifd-throws", bookLost: false, pastTheClock: true,
            c => c.HistoryThrows = new ConnectorTransportException("the platform would not show its history (test)"));
        var noId = await Arm("ifd-noid", bookLost: true, pastTheClock: true, c => c.ClosesCarryTheId = false);
        var inside = await Arm("ifd-inside", bookLost: true, pastTheClock: false, _ => { });
        var past = await Arm("ifd-past", bookLost: true, pastTheClock: true, _ => { });

        foreach (var (name, a) in new[] { ("history hidden", hidden), ("history throws", throws),
                     ("closes carry no id", noId), ("inside the clock", inside), ("past the clock", past) })
            log.WriteLine($"{name,-20} : {a.Row.State}, flagged {a.Row.NeedsReconciliation} — {a.Row.LastError ?? "-"}; "
                          + $"the close {a.Close}");

        foreach (var a in new[] { hidden, throws, noId, inside })
        {
            Assert.Equal(ExecutionState.WORKING, a.Row.State);
            Assert.False(a.Row.NeedsReconciliation);
            Assert.StartsWith(ErrorCode.CLOSE_IN_FLIGHT.ToString(), a.Close, StringComparison.Ordinal);
            Assert.Contains(a.Row.RequestId, a.Close, StringComparison.Ordinal);
        }

        Assert.Equal(ExecutionState.CANCELLED, past.Row.State);
        Assert.False(past.Row.NeedsReconciliation);
        Assert.Contains("lists no order and no fill", past.Row.LastError ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("never reached", past.Row.LastError ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(nameof(ExecutionState.FILLED), past.Close);
        Assert.Equal(0m, past.Held);
    }

    async Task<(ExecutionRequest Row, string Close, decimal Held)> Arm(string name, bool bookLost, bool pastTheClock,
        Action<RecordingConnector> platform)
    {
        var h = await Ready();
        using var dbh = h.Db;
        var first = await AWorkingClose(h, name);
        await h.Gw.DisposeAsync();

        // THE RESTART: the same database and the same clock, the simulator's book kept or lost.
        var broker = h.C.Broker;
        if (bookLost)
        {
            broker = new FakeBroker();
            broker.Accept(new PlaceOrderCommand($"owner-{name}", broker.AccountId, "ES", OrderSide.Buy, OrderType.Market,
                1m, null, null, TimeInForce.Day, null), FillBehaviour.FillImmediately);
        }
        var c = new RecordingConnector(new FakeConnector(broker));
        platform(c);
        var again = await Ready(h.Db, c, h.Clock);

        again.Clock.Advance(pastTheClock ? again.Stale + TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5));
        await again.Gw.RefreshHealthAsync();
        var row = again.Gw.GetRequest(first.RequestId)!;
        var close = Said(await Outcome(() => again.Gw.CloseAsync(Ai, $"{name}-close", "ES")));
        var held = Held(c);
        await again.Gw.DisposeAsync();
        return (row, close, held);
    }

    // ------------------------------------------------- U-inflight-owner: an order the platform answered and no longer lists

    /// <summary>
    /// ONE PASS OF THE APP'S BACKGROUND LOOP (<c>AppHost.BackgroundAsync</c>): the health pass, then the reconciler while
    /// anything is unconfirmed — the same tick, so a row handed over is met by the reconciler at once.
    /// </summary>
    static async Task Pass(TradingGateway gw)
    {
        await gw.RefreshHealthAsync();
        if (gw.HasUnconfirmedWork()) await gw.ReconcileAsync();
    }

    /// <summary>The refusal a call met, or null because it went through.</summary>
    static async Task<GatewayDeniedException?> Refusal(Func<Task> call)
    {
        try { await call(); return null; }
        catch (GatewayDeniedException ex) { return ex; }
    }

    /// <summary>
    /// (f) THE RECONCILER NEVER WRITES OFF AN ANSWERED ORDER AS NEVER REACHED (<c>U-inflight-owner</c>).
    ///
    /// <para>The platform takes a market buy and answers it <c>UNKNOWN</c>, naming it: the dispatch's indefinite
    /// answer that carries a reference, recorded <c>UNKNOWN</c>, flagged, with that reference. A restart, and a book
    /// that no longer lists it. On a platform of ATAS's kind, ten passes past the reconciler's clock leave it
    /// <c>RECONCILING</c>, flagged and pausing trading, and nothing says it never reached the broker — it carries the
    /// broker's own reference. On the plain simulator, whose closes carry the id and where absence decides, the
    /// reconciler writes it off as it always has: <c>CANCELLED</c>, "never reached the broker".</para>
    ///
    /// <para><b>RED on the base</b>: on ATAS's kind it is written off <c>CANCELLED</c>, "never reached the broker", and
    /// trading resumes over an order the platform answered.</para>
    /// </summary>
    [Fact]
    public async Task The_reconciler_never_writes_off_an_answered_order_as_never_reached()
    {
        var atas = await AnAnsweredOrderNoLongerListed("iof-atas", closesCarryTheId: false);
        var sim = await AnAnsweredOrderNoLongerListed("iof-sim", closesCarryTheId: null);

        foreach (var (name, a) in new[] { ("closes carry no id", atas), ("the plain simulator", sim) })
            log.WriteLine($"{name,-20} : placed {a.Placed.State}, reference {a.Placed.ConnectorOrderId}, flagged "
                          + $"{a.Placed.NeedsReconciliation}; ten passes later {a.Row.State}, flagged {a.Row.NeedsReconciliation} "
                          + $"— {a.Row.LastError ?? "-"}; a new order {a.Refused?.Code.ToString() ?? "sent"}");

        foreach (var a in new[] { atas, sim })
        {
            Assert.Equal(ExecutionState.UNKNOWN, a.Placed.State);
            Assert.True(a.Placed.NeedsReconciliation);
            Assert.False(string.IsNullOrEmpty(a.Placed.ConnectorOrderId));
        }

        Assert.Equal(ExecutionState.RECONCILING, atas.Row.State);
        Assert.True(atas.Row.NeedsReconciliation);
        Assert.DoesNotContain("never reached", atas.Row.LastError ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ErrorCode.TRADING_PAUSED_UNRECONCILED, atas.Refused?.Code);

        Assert.Equal(ExecutionState.CANCELLED, sim.Row.State);
        Assert.False(sim.Row.NeedsReconciliation);
        Assert.Contains("never reached the broker", sim.Row.LastError ?? "", StringComparison.Ordinal);
        Assert.Null(sim.Refused);
    }

    async Task<(ExecutionRequest Placed, ExecutionRequest Row, GatewayDeniedException? Refused)>
        AnAnsweredOrderNoLongerListed(string name, bool? closesCarryTheId)
    {
        var h = await Ready();
        using var dbh = h.Db;
        h.C.PlaceAnswer = o => o with { State = ExecutionState.UNKNOWN };
        var placed = await h.Gw.PlaceAsync(Ai, $"{name}-buy", TestEnv.Buy("ES", 1m));
        await h.Gw.DisposeAsync();

        var c = new RecordingConnector(new FakeConnector(new FakeBroker())) { ClosesCarryTheId = closesCarryTheId };
        var again = await Ready(h.Db, c, h.Clock);
        again.Clock.Advance(again.Stale + TimeSpan.FromSeconds(1));
        for (var i = 0; i < 10; i++)
        {
            await Pass(again.Gw);
            again.Clock.Advance(TimeSpan.FromSeconds(5));
        }
        var row = again.Gw.GetRequest(placed.RequestId)!;
        var refused = await Refusal(() => again.Gw.PlaceAsync(Ai, $"{name}-next", TestEnv.Buy("ES", 1m)));
        await again.Gw.DisposeAsync();
        return (placed, row, refused);
    }
}

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
    /// (the connector cannot prove its history), a history read that throws, and the clock not yet past: each stays
    /// <c>WORKING</c>, unflagged, and the close is still refused naming it. A restarted simulator past the clock:
    /// <c>CANCELLED</c>, with a last error that names the empty history and never says it did not reach the platform —
    /// the platform answered it — and the close goes out.</para>
    ///
    /// <para><b>A platform whose closes do not carry the id they are handed, past the clock, is never settled from the
    /// absence either</b> — and since <c>U-inflight-owner</c> it is no longer left <c>WORKING</c> off the card for good:
    /// it is handed to the owner, <c>RECONCILING</c> and flagged, with a last error that says the absence does not prove
    /// it did not fill and never that it did not reach the platform, and the close is refused
    /// <c>TRADING_PAUSED_UNRECONCILED</c> with nothing sent. The arm's assertions moved with it; the test's name did not.</para>
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

        foreach (var a in new[] { hidden, throws, inside })
        {
            Assert.Equal(ExecutionState.WORKING, a.Row.State);
            Assert.False(a.Row.NeedsReconciliation);
            Assert.StartsWith(ErrorCode.CLOSE_IN_FLIGHT.ToString(), a.Close, StringComparison.Ordinal);
            Assert.Contains(a.Row.RequestId, a.Close, StringComparison.Ordinal);
        }

        // CLOSES CARRY NO ID, PAST THE CLOCK: handed to the owner, never settled from the absence (U-inflight-owner).
        Assert.Equal(ExecutionState.RECONCILING, noId.Row.State);
        Assert.True(noId.Row.NeedsReconciliation);
        Assert.Contains(NotProof, noId.Row.LastError ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("never reached", noId.Row.LastError ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(ErrorCode.TRADING_PAUSED_UNRECONCILED.ToString(), noId.Close, StringComparison.Ordinal);
        Assert.Equal(1m, noId.Held);

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

    /// <summary>The words the hand-over's evidence carries, and that a write-off must never.</summary>
    const string NotProof = "does not prove it did not fill";

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

    static bool OnTheCard(Harness h, ExecutionRequest r) => h.Gw.Unreconciled().Any(u => u.RequestId == r.RequestId);

    /// <summary>
    /// A market close of a long 2 RESTING at the platform, then a restart onto the same database and clock, to a
    /// platform of ATAS's kind — its closes do not carry the id they are handed, so absence decides nothing — whose
    /// book no longer lists the close nor any fill under its id: the simulator's book is memory, and the restarted
    /// one holds <paramref name="held"/> ES and nothing else. The clock is left where the restart left it.
    /// </summary>
    async Task<(Harness H, ExecutionRequest First)> AnUnlistedClose(string name, decimal held = 2m,
        Action<RecordingConnector>? platform = null)
    {
        var before = await Ready();
        var first = await AWorkingClose(before, name);
        await before.Gw.DisposeAsync();

        var broker = new FakeBroker();
        if (held > 0m)
            broker.Accept(new PlaceOrderCommand($"owner-{name}", broker.AccountId, "ES", OrderSide.Buy, OrderType.Market,
                held, null, null, TimeInForce.Day, null), FillBehaviour.FillImmediately);
        var c = new RecordingConnector(new FakeConnector(broker)) { ClosesCarryTheId = false };
        platform?.Invoke(c);
        return (await Ready(before.Db, c, before.Clock), first);
    }

    /// <summary>
    /// (a) AN ORDER THE PLATFORM CANNOT ACCOUNT FOR REACHES THE OWNER PAST THE CLOCK (<c>U-inflight-owner</c>).
    ///
    /// <para>On a platform of ATAS's kind the agent's market close of a long 2 rests, and the restarted book lists
    /// neither it nor a fill under its id. Inside the clock a pass changes nothing: <c>WORKING</c>, unflagged, off
    /// the card. The first pass past it hands the row to the reconciler by the reconciler's own two writes:
    /// <c>RECONCILING</c>, flagged, in <see cref="TradingGateway.Unreconciled"/> — the list the owner's card shows
    /// and the gate refuses on — with the evidence as its last error, and a new order is refused
    /// <c>TRADING_PAUSED_UNRECONCILED</c>. Ten passes later it is exactly there: the reconciler's absence never
    /// writes off an order carrying the platform's reference where absence decides nothing, nothing is sent, and
    /// nothing says it never reached the broker.</para>
    ///
    /// <para><b>RED on the base</b>: the row stays <c>WORKING</c>, unflagged and off the card on every pass.
    /// <b>The mutant</b> — the reconciler's guard dropped — writes it off <c>CANCELLED</c>, "never reached the
    /// broker", on the first pass past the clock.</para>
    /// </summary>
    [Fact]
    public async Task An_order_the_platform_cannot_account_for_reaches_the_owner_past_the_clock()
    {
        var (h, first) = await AnUnlistedClose("ioa");
        using var dbh = h.Db;
        var mutations = h.C.Mutations;

        // INSIDE THE CLOCK: nothing.
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        await Pass(h.Gw);
        var inside = h.Gw.GetRequest(first.RequestId)!;
        var insideOnTheCard = OnTheCard(h, first);

        // PAST IT: the first pass hands it over.
        h.Clock.Advance(h.Stale);
        await Pass(h.Gw);
        var handed = h.Gw.GetRequest(first.RequestId)!;
        var onTheCard = OnTheCard(h, first);
        var refused = await Refusal(() => h.Gw.PlaceAsync(Ai, "ioa-new", TestEnv.Buy("ES", 1m)));

        // TEN PASSES LATER: exactly there.
        for (var i = 0; i < 10; i++)
        {
            h.Clock.Advance(TimeSpan.FromSeconds(5));
            await Pass(h.Gw);
        }
        var later = h.Gw.GetRequest(first.RequestId)!;
        var laterOnTheCard = OnTheCard(h, first);
        var laterRefused = await Refusal(() => h.Gw.PlaceAsync(Ai, "ioa-later", TestEnv.Buy("ES", 1m)));

        log.WriteLine($"inside the clock     : {inside.State}, flagged {inside.NeedsReconciliation}, on the card {insideOnTheCard}");
        log.WriteLine($"past it              : {handed.State}, flagged {handed.NeedsReconciliation}, on the card {onTheCard}, "
                      + $"reference {handed.ConnectorOrderId} — {handed.LastError ?? "-"}");
        log.WriteLine($"a new order          : {refused?.Code.ToString() ?? "sent"} — {refused?.Message}");
        log.WriteLine($"ten passes later     : {later.State}, flagged {later.NeedsReconciliation}, on the card {laterOnTheCard}; "
                      + $"a new order {laterRefused?.Code.ToString() ?? "sent"}");
        log.WriteLine($"sent meanwhile       : {h.C.Mutations - mutations}; position {Held(h.C)}");

        Assert.Equal(ExecutionState.WORKING, inside.State);
        Assert.False(inside.NeedsReconciliation);
        Assert.False(insideOnTheCard);

        Assert.Equal(ExecutionState.RECONCILING, handed.State);
        Assert.True(handed.NeedsReconciliation);
        Assert.True(onTheCard);
        Assert.False(string.IsNullOrEmpty(handed.ConnectorOrderId));
        Assert.Contains(NotProof, handed.LastError ?? "", StringComparison.Ordinal);
        Assert.Equal(ErrorCode.TRADING_PAUSED_UNRECONCILED, refused?.Code);

        Assert.Equal(ExecutionState.RECONCILING, later.State);
        Assert.True(later.NeedsReconciliation);
        Assert.True(laterOnTheCard);
        Assert.Equal(handed.LastError, later.LastError);
        Assert.DoesNotContain("never reached", later.LastError ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ErrorCode.TRADING_PAUSED_UNRECONCILED, laterRefused?.Code);
        Assert.Equal(mutations, h.C.Mutations);
        Assert.Equal(2m, Held(h.C));
        await h.Gw.DisposeAsync();
    }

    /// <summary>
    /// (b) THE OWNER SETTLES IT, AND THE HELD CLOSE GOES OUT (<c>U-inflight-owner</c>).
    ///
    /// <para>The row of (a), handed over and on the card. The agent's close of what is left is refused while it is
    /// there. The owner answers it the one way the product has — the card: <see cref="TradingGateway.ForceResolve"/>,
    /// then a health pass — <c>FILLED</c> (the close filled, and a long 1 of his own is open) or <c>CANCELLED</c> (it
    /// did not fill, and the long 2 is still open). His state, his words, the flag off and the card empty; the held
    /// close goes out sized from the position re-read, and the account is flat.</para>
    ///
    /// <para><b>RED on the base</b>: the row is never on the card, and the close is refused <c>CLOSE_IN_FLIGHT</c>
    /// over a row nobody is asked about.</para>
    /// </summary>
    [Fact]
    public async Task The_owner_settles_it_and_the_held_close_goes_out()
    {
        var filled = await TheOwnerAnswers("iob-filled", ExecutionState.FILLED, held: 1m);
        var cancelled = await TheOwnerAnswers("iob-cancelled", ExecutionState.CANCELLED, held: 2m);

        foreach (var (name, a) in new[] { ("the owner: filled", filled), ("the owner: did not fill", cancelled) })
            log.WriteLine($"{name,-24} : handed {a.Handed}, on the card {a.OnTheCard}; the close first {a.Held}; then "
                          + $"{a.Row.State}, flagged {a.Row.NeedsReconciliation}, on the card {a.StillOnTheCard} — "
                          + $"{a.Row.LastError}; the close {a.Close}, sized {a.Sized}; position {a.Position}");

        foreach (var (a, answer, size) in new[] { (filled, ExecutionState.FILLED, 1m), (cancelled, ExecutionState.CANCELLED, 2m) })
        {
            Assert.Equal(ExecutionState.RECONCILING, a.Handed);
            Assert.True(a.OnTheCard);
            Assert.StartsWith(ErrorCode.TRADING_PAUSED_UNRECONCILED.ToString(), a.Held, StringComparison.Ordinal);

            Assert.Equal(answer, a.Row.State);
            Assert.False(a.Row.NeedsReconciliation);
            Assert.StartsWith(TradingGateway.ResolvedByOwnerPrefix, a.Row.LastError ?? "", StringComparison.Ordinal);
            Assert.False(a.StillOnTheCard);

            Assert.Equal(nameof(ExecutionState.FILLED), a.Close);
            Assert.Equal(size, a.Sized);
            Assert.Equal(0m, a.Position);
        }
    }

    async Task<(ExecutionState Handed, bool OnTheCard, string Held, ExecutionRequest Row, bool StillOnTheCard,
        string Close, decimal Sized, decimal Position)> TheOwnerAnswers(string name, ExecutionState answer, decimal held)
    {
        var (h, first) = await AnUnlistedClose(name, held);
        using var dbh = h.Db;
        h.Clock.Advance(h.Stale + TimeSpan.FromSeconds(1));
        await Pass(h.Gw);
        var handed = h.Gw.GetRequest(first.RequestId)!.State;
        var onTheCard = OnTheCard(h, first);
        var heldClose = Said(await Outcome(() => h.Gw.CloseAsync(Ai, $"{name}-held", "ES")));

        // HIS ANSWER, AS THE CARD GIVES IT (DashboardPage.ResolveAsync): the override, then a health pass.
        try { h.Gw.ForceResolve(first.RequestId, answer, "checked in ATAS (test)"); }
        catch (GatewayDeniedException ex) { log.WriteLine($"the override refused : {ex.Message}"); }
        await h.Gw.RefreshHealthAsync();
        var row = h.Gw.GetRequest(first.RequestId)!;
        var stillOnTheCard = OnTheCard(h, first);

        var places = h.C.Places;
        var close = Said(await Outcome(() => h.Gw.CloseAsync(Ai, $"{name}-close", "ES")));
        var sized = h.C.Places > places ? h.C.Placed[^1].Quantity : 0m;
        var position = Held(h.C);
        await h.Gw.DisposeAsync();
        return (handed, onTheCard, heldClose, row, stillOnTheCard, close, sized, position);
    }

    /// <summary>
    /// (c) A ROW THE PLATFORM STILL LISTS WORKING IS NEVER HANDED OVER (<c>U-inflight-owner</c>).
    ///
    /// <para>On a platform of ATAS's kind, the agent's close rests and the platform lists it <c>WORKING</c> — a live
    /// answer. Ten passes past the clock: <c>WORKING</c>, unflagged, off the card, nothing paused, nothing sent, and
    /// the second close refused <c>CLOSE_IN_FLIGHT</c> naming it. Green on the base.</para>
    /// </summary>
    [Fact]
    public async Task A_row_the_platform_still_lists_working_is_never_handed_over()
    {
        var h = await Ready(c: new RecordingConnector(new FakeConnector(new FakeBroker())) { ClosesCarryTheId = false });
        using var dbh = h.Db;
        var first = await AWorkingClose(h, "ioc");
        var mutations = h.C.Mutations;

        h.Clock.Advance(h.Stale + TimeSpan.FromSeconds(1));
        for (var i = 0; i < 10; i++)
        {
            await Pass(h.Gw);
            h.Clock.Advance(TimeSpan.FromSeconds(5));
        }
        var row = h.Gw.GetRequest(first.RequestId)!;
        var onTheCard = OnTheCard(h, first);
        var unconfirmed = h.Gw.HasUnconfirmedWork();
        var second = await Outcome(() => h.Gw.CloseAsync(Ai, "ioc-second", "ES"));

        log.WriteLine($"ten passes past it   : {row.State}, flagged {row.NeedsReconciliation}, on the card {onTheCard}, "
                      + $"unconfirmed work {unconfirmed}; sent {h.C.Mutations - mutations}");
        log.WriteLine($"the second close     : {Said(second)}");

        Assert.Equal(ExecutionState.WORKING, row.State);
        Assert.False(row.NeedsReconciliation);
        Assert.False(onTheCard);
        Assert.False(unconfirmed);
        Assert.Equal(mutations, h.C.Mutations);
        Assert.Equal(ErrorCode.CLOSE_IN_FLIGHT, second.Refused?.Code);
        Assert.Contains(first.RequestId, second.Refused!.Message, StringComparison.Ordinal);
        await h.Gw.DisposeAsync();
    }

    /// <summary>
    /// (d) A READ THAT THREW HANDS NOTHING OVER (<c>U-inflight-owner</c>).
    ///
    /// <para>The unlisted close of (a), on a platform whose history read throws, and on one that withdraws its claim
    /// to a provable history (<c>Faults.HideOrderHistory</c>): neither is an answer. Ten passes past the clock:
    /// <c>WORKING</c>, unflagged, off the card, nothing paused, nothing sent, the close refused <c>CLOSE_IN_FLIGHT</c>
    /// naming it. Green on the base.</para>
    /// </summary>
    [Fact]
    public async Task A_read_that_threw_hands_nothing_over()
    {
        var throws = await NothingHandedOver("iod-throws",
            c => c.HistoryThrows = new ConnectorTransportException("the platform would not show its history (test)"));
        var hidden = await NothingHandedOver("iod-hidden", c => c.Faults.HideOrderHistory = true);

        foreach (var (name, a) in new[] { ("history read throws", throws), ("history hidden", hidden) })
            log.WriteLine($"{name,-20} : {a.Row.State}, flagged {a.Row.NeedsReconciliation}, on the card {a.OnTheCard}, "
                          + $"unconfirmed work {a.Unconfirmed}, sent {a.Sent}; the close {a.Close}");

        foreach (var a in new[] { throws, hidden })
        {
            Assert.Equal(ExecutionState.WORKING, a.Row.State);
            Assert.False(a.Row.NeedsReconciliation);
            Assert.False(a.OnTheCard);
            Assert.False(a.Unconfirmed);
            Assert.Equal(0, a.Sent);
            Assert.StartsWith(ErrorCode.CLOSE_IN_FLIGHT.ToString(), a.Close, StringComparison.Ordinal);
            Assert.Contains(a.Row.RequestId, a.Close, StringComparison.Ordinal);
        }
    }

    async Task<(ExecutionRequest Row, bool OnTheCard, bool Unconfirmed, int Sent, string Close)> NothingHandedOver(
        string name, Action<RecordingConnector> platform)
    {
        var (h, first) = await AnUnlistedClose(name, platform: platform);
        using var dbh = h.Db;
        var mutations = h.C.Mutations;
        h.Clock.Advance(h.Stale + TimeSpan.FromSeconds(1));
        for (var i = 0; i < 10; i++)
        {
            await Pass(h.Gw);
            h.Clock.Advance(TimeSpan.FromSeconds(5));
        }
        var row = h.Gw.GetRequest(first.RequestId)!;
        var onTheCard = OnTheCard(h, first);
        var unconfirmed = h.Gw.HasUnconfirmedWork();
        var sent = h.C.Mutations - mutations;
        var close = Said(await Outcome(() => h.Gw.CloseAsync(Ai, $"{name}-close", "ES")));
        await h.Gw.DisposeAsync();
        return (row, onTheCard, unconfirmed, sent, close);
    }

    /// <summary>
    /// (e) A LATER PLATFORM ANSWER SETTLES IT WITHOUT THE OWNER (<c>U-inflight-owner</c>).
    ///
    /// <para>The row of (a), handed over. Then the platform lists it again under its id — its history caught up — as
    /// <c>FILLED</c> (and the long 2 is flat) or as <c>CANCELLED</c> (and the long 2 is still open). The next pass's
    /// reconciler adopts the platform's own final answer: that state, the flag off, the card empty, trading allowed
    /// again with nobody pressing anything; and what is left is closed — nothing in the first arm, the 2 in the
    /// second — and the account is flat.</para>
    ///
    /// <para>On the base the later answer settles it too — through the sweep, the row having never left
    /// <c>WORKING</c> — so only the hand-over before it is red there.</para>
    /// </summary>
    [Fact]
    public async Task A_later_platform_answer_settles_it_without_the_owner()
    {
        var filled = await ThePlatformAnswersLater("ioe-filled", fills: true);
        var cancelled = await ThePlatformAnswersLater("ioe-cancelled", fills: false);

        foreach (var (name, a) in new[] { ("listed FILLED", filled), ("listed CANCELLED", cancelled) })
            log.WriteLine($"{name,-20} : handed {a.Handed}; then {a.Row.State}, filled {a.Row.FilledQuantity}, flagged "
                          + $"{a.Row.NeedsReconciliation}, on the card {a.OnTheCard}, may trade {a.MayTrade}; the close "
                          + $"{a.Close}; position {a.Position}");

        Assert.Equal(ExecutionState.RECONCILING, filled.Handed);
        Assert.Equal(ExecutionState.FILLED, filled.Row.State);
        Assert.Equal(2m, filled.Row.FilledQuantity);
        Assert.Equal("nothing to close", filled.Close);

        Assert.Equal(ExecutionState.RECONCILING, cancelled.Handed);
        Assert.Equal(ExecutionState.CANCELLED, cancelled.Row.State);
        Assert.Equal(nameof(ExecutionState.FILLED), cancelled.Close);

        foreach (var a in new[] { filled, cancelled })
        {
            Assert.False(a.Row.NeedsReconciliation);
            Assert.False(a.OnTheCard);
            Assert.True(a.MayTrade);
            Assert.Equal(0m, a.Position);
        }
    }

    async Task<(ExecutionState Handed, ExecutionRequest Row, bool OnTheCard, bool MayTrade, string Close, decimal Position)>
        ThePlatformAnswersLater(string name, bool fills)
    {
        var (h, first) = await AnUnlistedClose(name, held: 2m);
        using var dbh = h.Db;
        h.Clock.Advance(h.Stale + TimeSpan.FromSeconds(1));
        await Pass(h.Gw);
        var handed = h.Gw.GetRequest(first.RequestId)!.State;

        // THE PLATFORM LISTS IT AGAIN, UNDER ITS ID: filled, or cancelled without a fill.
        var listed = h.C.Broker.Accept(new PlaceOrderCommand(first.ClientOrderId, h.C.Broker.AccountId, "ES",
                OrderSide.Sell, OrderType.Market, 2m, null, null, TimeInForce.Day, null) { Intent = OrderIntent.Close },
            fills ? FillBehaviour.FillImmediately : FillBehaviour.LeaveWorking);
        if (!fills) h.C.Broker.Cancel(listed.ConnectorOrderId);

        h.Clock.Advance(TimeSpan.FromSeconds(5));
        await Pass(h.Gw);
        var row = h.Gw.GetRequest(first.RequestId)!;
        var onTheCard = OnTheCard(h, first);
        var mayTrade = h.Gw.TryAuthorizeExecution(Ai, out _);
        var close = Said(await Outcome(() => h.Gw.CloseAsync(Ai, $"{name}-close", "ES")));
        var position = Held(h.C);
        await h.Gw.DisposeAsync();
        return (handed, row, onTheCard, mayTrade, close, position);
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

using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// EVERY LOST CLOSE THE APP SENDS IS CONFIRMED, ONCE PER CLOSE GENERATION AND NEVER ONCE PER BREACH
/// (<c>U-valuation-close-confirm</c>).
///
/// <para>A generation is one outcome of the app's own with its own two press nonces: the loss flatten, its
/// closing again, the data-loss exit and the exit's closing again. Until this unit only the first of the four
/// was ever asked of the platform. A lost answer to the closing again held its closure for ever over a book the
/// platform's history — or the owner — had already accounted for; and a lost answer to the data-loss exit's
/// close paused all trading, refused every later exit and was never closed again (seat P's survey at
/// <c>d73ecd59</c>, every defect reproduced by a probe).</para>
///
/// <para>Every test runs on <c>LossFlattenConfirmTests</c>' fixture: the connector's real two-second emergency
/// budget and a coherent clock. Every verdict is the book, a row, a record or a count at the wire — "nothing was
/// sent" is <see cref="RecordingConnector.Closes"/>, not a claim about intentions. The new record families are
/// named by their literal prefixes, as the families before them are.</para>
/// </summary>
public class CloseGenerationConfirmTests(ITestOutputHelper log)
{
    sealed class TestClock(DateTimeOffset at) : TimeProvider
    {
        DateTimeOffset _now = at;
        public override DateTimeOffset GetUtcNow() => _now;

        // A COHERENT CLOCK, as LossFlattenConfirmTests keeps one: the monotone half moves with the wall half.
        public override long GetTimestamp() => _now.UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan by) => _now += by;
        public void MoveTo(DateTimeOffset to) => _now = to;
    }

    static readonly DateTimeOffset Noon = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Tick = TimeSpan.FromSeconds(20);

    /// <summary>The absence grace every gateway here runs with: the product's own default.</summary>
    static readonly TimeSpan Grace = new GatewayOptions().AbsenceGrace;

    /// <summary>A feed that has stopped: every quote is older than <see cref="GatewayOptions.MaxQuoteAge"/>.</summary>
    static readonly TimeSpan Silent = TimeSpan.FromMinutes(10);

    static async Task<(TradingGateway Gw, RecordingConnector Conn, Database Db, TestClock Clock)> Ready(
        decimal exitAfterMinutes = 0m)
    {
        var clock = new TestClock(Noon);
        var db = TestEnv.NewDb();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker())
        {
            QuoteClock = clock,
            EmergencyBudget = TimeSpan.FromSeconds(2)
        });
        var gw = new TradingGateway(db, conn, new HealthRegistry(), new GatewayOptions { Clock = clock });
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments];
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
            s.Risk.MaxDailyLoss = 1_000m;
            s.Risk.ValuationLossExitMinutes = exitAfterMinutes;
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        return (gw, conn, db, clock);
    }

    static decimal Held(RecordingConnector conn, string symbol) =>
        conn.Broker.Positions.FirstOrDefault(p => p.Symbol == symbol)?.Quantity ?? 0m;

    /// <summary>The health pass every host runs, <paramref name="howMany"/> times, a tick apart.</summary>
    static async Task Passes(TradingGateway gw, TestClock clock, int howMany)
    {
        for (var i = 0; i < howMany; i++)
        {
            clock.Advance(Tick);
            await gw.RefreshHealthAsync();
        }
    }

    /// <summary>The rows of one app press family, whole, oldest first: id, state and flag.</summary>
    static List<string> Rows(Database db, string family)
    {
        using var c = db.Cmd("SELECT request_id, execution_state, needs_reconciliation FROM execution_request "
                             + "WHERE request_id LIKE $p ORDER BY created_at", ("$p", $"{family}%"));
        using var r = c.ExecuteReader();
        var rows = new List<string>();
        while (r.Read()) rows.Add($"{r.GetString(0)} {r.GetString(1)} flagged={r.GetInt64(2)}");
        return rows;
    }

    /// <summary>The rows of one app press family that still refuse order flow: flagged.</summary>
    static List<string> Flagged(Database db, string family) =>
        [.. Rows(db, family).Where(r => r.EndsWith("flagged=1", StringComparison.Ordinal))];

    /// <summary>Every close of one app family the broker's book holds, in the order it arrived.</summary>
    static List<OrderInfo> ClosesAtTheBook(RecordingConnector conn, string family) =>
        [.. conn.Broker.Orders.Where(o => o.ClientOrderId?.StartsWith($"TA-{family}", StringComparison.Ordinal) == true)];

    /// <summary>How many records one key family holds. Each is written once, so this is how many were written.</summary>
    static int Records(Database db, string prefix) => db.KvStartingWith(prefix).Count;

    /// <summary>The one record of a family that is keyed to this symbol, or null because there is none.</summary>
    static string? RecordFor(Database db, RecordingConnector conn, string prefix, string symbol) =>
        db.KvStartingWith($"{prefix}{conn.Id}:{conn.Broker.AccountId}:{symbol}:").Select(r => r.Value).SingleOrDefault();

    /// <summary>The sentence a record carries, read off its JSON: the field every record family here names <c>Why</c>.</summary>
    static string WhyOf(string? json)
    {
        if (json is null) return "(no record)";
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("why").GetString()!;
    }

    /// <summary>The card's own rule (<c>DashboardView.SpokenByThePlatform</c>): one button, "Our record is right".</summary>
    static bool SpokenByThePlatform(ExecutionState s) => s is
        ExecutionState.FILLED or ExecutionState.CANCELLED or ExecutionState.REJECTED or
        ExecutionState.WORKING or ExecutionState.ACKNOWLEDGED or
        ExecutionState.PARTIALLY_FILLED or ExecutionState.CANCEL_PENDING;

    /// <summary>
    /// What the surfaces say about one symbol's data-loss exit today — the reading the status, the Situation and the
    /// daily report all take (<see cref="TradingGateway.ValuationReading"/>), picked out by the size and symbol every
    /// exit sentence names.
    /// </summary>
    static string ExitWordFor(TradingGateway gw, string symbol) =>
        Assert.Single(gw.ValuationReading().Exits, e => e.Contains($" 1 {symbol}", StringComparison.Ordinal));

    /// <summary>When a lost close can no longer be on its way to the platform: the dispatch plus the bound, then the grace.</summary>
    static DateTimeOffset NoLongerOnItsWay(TradingGateway gw, ExecutionRequest lost) =>
        lost.DispatchedAt!.Value + gw.DispatchStrandedAfter + Grace;

    // ------------------------------------------------------------------- the loss flatten's closing again

    /// <summary>
    /// A confirmed daily breach whose FIRST close the platform refused with the answer lost (the shared close
    /// path records it UNKNOWN), confirmed REJECTED from the history on the next pass with the book still open, and
    /// then its CLOSING AGAIN sent on that same pass — with the fault the caller arms for it. Answers the closing
    /// again's own row.
    /// </summary>
    static async Task<ExecutionRequest> ABreachClosedAgain(TradingGateway gw, RecordingConnector conn, Database db,
        TestClock clock, Action<FaultProfile> armTheAgain)
    {
        await gw.PlaceAsync(new AgentContext("a"), "again-open", TestEnv.Buy("ES"));
        conn.Faults.RejectNext = 1;
        conn.Broker.PriceOffset = -20m;
        await Passes(gw, clock, 2);

        var account = conn.Broker.AccountId;
        Assert.NotNull(gw.DayClosed(account));
        Assert.Equal(nameof(ExecutionState.UNKNOWN), Assert.Single(gw.FlattenToday(account)!.Legs).State);
        Assert.Equal(1, conn.Closes);

        armTheAgain(conn.Faults);
        await Passes(gw, clock, 1);

        Assert.Equal(1, Records(db, "loss_flatten_confirm:"));
        Assert.Equal(1, Records(db, "loss_flatten_again:"));
        Assert.Equal(2, conn.Closes);
        var again = ClosesAtTheBook(conn, TradingGateway.BudgetClosePress).Last();
        return gw.Requests.GetByClientOrderId(again.ClientOrderId!)!;
    }

    /// <summary>
    /// (e) THE CLOSING AGAIN'S CLOSE FILLED AND ITS ANSWER WAS LOST: ITS OWN CONFIRM — FILLED FROM THE PLATFORM'S
    /// HISTORY, OR FROM THE OWNER'S "IT WAS FILLED" WHERE NO HISTORY CAN BE ASKED — READS THE BOOK FLAT, SENDS
    /// NOTHING, AND THE CLOSURE REOPENS ONCE ITS TIME HAS RUN (item 1).
    ///
    /// <para>Before this unit there was one confirm per breach. The closing again's lost close was never asked
    /// about: its row stayed flagged until the owner answered it, and once he had, its outcome's <c>Flat=false</c>
    /// was still the latest word, so the reopen's hold read "TradeAgent cannot confirm that what it closed for you
    /// is closed" for ever — over a flat book everything had accounted for (probe P_again: "past eligibility :
    /// reopened []"). Now the closing again is a generation of its own, and its confirm is written once, under its
    /// own family, from the same history question and the same owner's answer.</para>
    /// </summary>
    [Theory]
    [InlineData("history shown")]
    [InlineData("history hidden")]
    public async Task The_closing_agains_lost_close_is_confirmed_by_its_own_confirm_and_the_closure_reopens(string history)
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        await using var _2 = gw;
        var account = conn.Broker.AccountId;

        // THE CLOSING AGAIN'S ANSWER IS LOST: the broker takes it and fills it.
        var row = await ABreachClosedAgain(gw, conn, db, clock, f => f.DropAfterBrokerAccept = 1);
        Assert.Equal(ExecutionState.UNKNOWN, row.State);
        Assert.True(row.NeedsReconciliation);
        Assert.Equal(0m, Held(conn, "ES"));
        Assert.Equal(1, Records(db, "loss_flatten_confirm:"));

        if (history == "history hidden")
        {
            // WHERE NO HISTORY CAN BE ASKED, the owner answers every record the card shows, as it offers them.
            conn.Faults.HideOrderHistory = true;
            Assert.False(conn.Capabilities.ReconciliationProvable);
            foreach (var r in gw.Unreconciled())
                gw.ForceResolve(r.RequestId,
                    r.RequestId == row.RequestId || !SpokenByThePlatform(r.State) ? ExecutionState.FILLED : r.State,
                    r.RequestId == row.RequestId ? "I checked in ATAS: the close filled" : "I checked in ATAS: our record is right");
            Assert.False(gw.HasUnconfirmedWork());
            clock.MoveTo(NoLongerOnItsWay(gw, row));
        }

        await Passes(gw, clock, 1);

        var settled = gw.Requests.Get(row.RequestId)!;
        var state = gw.FlattenStateToday();
        log.WriteLine($"[{history}]");
        log.WriteLine($"closes on the wire    : {conn.Closes}, position ES {Held(conn, "ES")}");
        log.WriteLine($"budget rows           : {string.Join(" | ", Rows(db, "op-budget-"))}");
        log.WriteLine($"again's confirm       : {Records(db, "loss_flatten_again_confirm:")}");
        log.WriteLine($"dashboard             : {state.State} — {state.Why}");

        Assert.Equal(1, Records(db, "loss_flatten_again_confirm:"));
        Assert.Equal(1, Records(db, "loss_flatten_confirm:"));
        Assert.Equal(ExecutionState.FILLED, settled.State);
        Assert.False(settled.NeedsReconciliation);
        Assert.Equal("flat", state.State);
        Assert.Contains("the close it sent AGAIN", state.Why!, StringComparison.Ordinal);
        Assert.Contains(history == "history shown"
            ? "CONFIRMED FROM YOUR PLATFORM'S ORDER HISTORY"
            : "CONFIRMED FROM YOUR ANSWER ON THE DASHBOARD", state.Why!, StringComparison.Ordinal);
        Assert.Equal(2, conn.Closes);
        Assert.Equal(0m, Held(conn, "ES"));
        Assert.Empty(Flagged(db, "op-budget-"));
        Assert.False(gw.HasUnconfirmedWork());

        // THE CLOSURE REOPENS ONCE ITS TIME HAS RUN, over the flat book, and nothing more is ever sent.
        var breach = gw.DayClosed(account)!;
        clock.MoveTo(LossReopen.EligibleAt(breach.ConfirmedAt, TimeSpan.FromHours(24)) + TimeSpan.FromMinutes(1));
        conn.Broker.PriceOffset = 0m;
        var reopen = await gw.LossWatchAsync();
        log.WriteLine($"past eligibility      : reopened [{string.Join(", ", reopen.Reopened)}]; day closed {gw.DayClosed(account) is not null}");

        Assert.Single(reopen.Reopened);
        Assert.Null(gw.DayClosed(account));
        Assert.Equal(2, conn.Closes);
        Assert.Equal(1, Records(db, "loss_flatten_again_confirm:"));
    }

    /// <summary>
    /// (f) THE BOUND: THE CLOSING AGAIN'S LOST CLOSE IS FOUND REFUSED WITH THE BOOK STILL OPEN — ITS CONFIRM IS
    /// WRITTEN, NOTHING CLOSES A THIRD TIME, AND ITS WORD NAMES THE OWNER (item 1).
    ///
    /// <para>Both closes were refused by the platform and both answers were lost on the way back. The second
    /// generation's confirm reads the history (REJECTED) and the book (ES 1 open) — and it owes nothing: a confirm
    /// that could close again would be the app settling and re-sending its own closes in a loop. So the record is
    /// written, the rows are settled and unflagged, pass after pass nothing more is sent, and the sentence says
    /// what is still open is the owner's to close. The closure then waits on the BOOK, which the owner closing it
    /// at his platform answers — it is not held for ever on a record.</para>
    /// </summary>
    [Fact]
    public async Task The_closing_agains_lost_close_found_refused_is_confirmed_and_nothing_closes_a_third_time()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        await using var _2 = gw;
        var account = conn.Broker.AccountId;

        // THE CLOSING AGAIN IS REFUSED TOO, AND ITS ANSWER IS LOST THE SAME WAY.
        var row = await ABreachClosedAgain(gw, conn, db, clock, f => f.RejectNext = 1);
        Assert.Equal(ExecutionState.UNKNOWN, row.State);
        Assert.Equal(1m, Held(conn, "ES"));

        await Passes(gw, clock, 1);

        var settled = gw.Requests.Get(row.RequestId)!;
        var state = gw.FlattenStateToday();
        log.WriteLine($"closes on the wire    : {conn.Closes}, position ES {Held(conn, "ES")}");
        log.WriteLine($"budget rows           : {string.Join(" | ", Rows(db, "op-budget-"))}");
        log.WriteLine($"dashboard             : {state.State} — {state.Why}");

        Assert.Equal(1, Records(db, "loss_flatten_again_confirm:"));
        Assert.Equal(ExecutionState.REJECTED, settled.State);
        Assert.False(settled.NeedsReconciliation);
        Assert.Empty(Flagged(db, "op-budget-"));
        Assert.False(gw.HasUnconfirmedWork());
        Assert.Equal(2, conn.Closes);
        Assert.Equal(1m, Held(conn, "ES"));
        Assert.Equal("unresolved", state.State);
        Assert.Contains("does NOT close it a third time", state.Why!, StringComparison.Ordinal);
        Assert.Contains("yours to close", state.Why!, StringComparison.Ordinal);
        Assert.DoesNotContain("CLOSING IT AGAIN", state.Why!, StringComparison.Ordinal);

        // NOTHING CLOSES A THIRD TIME, pass after pass.
        await Passes(gw, clock, 6);
        Assert.Equal(2, conn.Closes);
        Assert.Equal(1m, Held(conn, "ES"));
        Assert.Equal(1, Records(db, "loss_flatten_again_confirm:"));
        Assert.Equal(1, Records(db, "loss_flatten_again:"));

        // PAST ELIGIBILITY WITH ES STILL OPEN, THE CLOSURE STANDS — on the book, and the surface says whose it is.
        var breach = gw.DayClosed(account)!;
        clock.MoveTo(LossReopen.EligibleAt(breach.ConfirmedAt, TimeSpan.FromHours(24)) + TimeSpan.FromMinutes(1));
        conn.Broker.PriceOffset = 0m;
        var held = await gw.LossWatchAsync();
        var surface = gw.ReopenReading().Held;
        log.WriteLine($"past eligibility      : reopened [{string.Join(", ", held.Reopened)}]; held by {surface}");
        Assert.Empty(held.Reopened);
        Assert.NotNull(gw.DayClosed(account));
        Assert.Contains("yours to close", surface!, StringComparison.Ordinal);
        Assert.Equal(2, conn.Closes);

        // AND ONCE THE OWNER HAS CLOSED IT AT HIS PLATFORM, THE NEXT TICK LIFTS IT: no record holds it for ever.
        conn.Broker.Accept(new PlaceOrderCommand("closed-by-hand", account, "ES", OrderSide.Sell, OrderType.Market,
            1m, null, null, TimeInForce.Day, "closed by the owner at his platform"), FillBehaviour.FillImmediately);
        Assert.Equal(0m, Held(conn, "ES"));
        clock.Advance(Tick);
        var reopen = await gw.LossWatchAsync();
        log.WriteLine($"after his own close   : reopened [{string.Join(", ", reopen.Reopened)}]");
        Assert.Single(reopen.Reopened);
        Assert.Null(gw.DayClosed(account));
        Assert.Equal(2, conn.Closes);
    }

    // ------------------------------------------------------------------- the data-loss exit

    /// <summary>
    /// ES AND NQ OPEN AND VALUED, THEN THE FEED GOES SILENT ON BOTH: the pass that reaches the one-minute bound
    /// exits ES first, its close lost the way the caller armed, and NQ's exit is refused behind ES's flagged rows.
    /// Answers ES's lost close row.
    /// </summary>
    static async Task<ExecutionRequest> TwoUnvaluableAndTheFirstExitsCloseLost(TradingGateway gw,
        RecordingConnector conn, Database db, TestClock clock, Action<FaultProfile> arm)
    {
        await gw.PlaceAsync(new AgentContext("a"), "es-open", TestEnv.Buy("ES"));
        await gw.PlaceAsync(new AgentContext("a"), "nq-open", TestEnv.Buy("NQ"));
        await Passes(gw, clock, 1);
        Assert.Equal(1m, Held(conn, "ES"));
        Assert.Equal(1m, Held(conn, "NQ"));

        conn.Faults.QuoteAge = Silent;
        arm(conn.Faults);
        for (var passes = 0; conn.Closes == 0; passes++)
        {
            Assert.True(passes < 10, $"no data-loss exit went out in {passes} silent passes past a one-minute bound");
            await Passes(gw, clock, 1);
        }

        Assert.Equal(1, conn.Closes);
        var leg = Assert.Single(Rows(db, $"{TradingGateway.ValuationClosePress}-"));
        var lost = gw.Requests.Get(leg.Split(' ')[0])!;
        Assert.Equal("ES", lost.Instrument);
        Assert.Equal(ExecutionState.UNKNOWN, lost.State);
        Assert.True(lost.NeedsReconciliation);
        Assert.NotNull(RecordFor(db, conn, "loss_valuation_exit:", "ES"));
        Assert.Null(RecordFor(db, conn, "loss_valuation_exit:", "NQ"));
        Assert.True(gw.HasUnconfirmedWork());
        return lost;
    }

    /// <summary>
    /// (a) THE EXIT'S CLOSE FILLED AND ITS ANSWER WAS LOST: CONFIRMED FILLED FROM THE PLATFORM'S HISTORY, THE ROWS
    /// UNFLAGGED, THE PAUSE LIFTED — AND NQ, WHOSE EXIT WAS REFUSED BEHIND THEM, IS EXITED (item 2).
    ///
    /// <para>Before this unit the data-loss exit's lost close was never asked about: its row stayed flagged, every
    /// order stayed refused ("2 earlier request(s) are unconfirmed"), and NQ — a position nobody could value either —
    /// stayed open past its bound, its exit refused 91 times in thirty simulated minutes
    /// (<c>valuation_exit_press_already_open</c>), until a person answered a record the platform could have.</para>
    /// </summary>
    [Fact]
    public async Task An_exits_lost_close_the_platform_filled_is_confirmed_from_its_history_and_the_next_exit_goes_out()
    {
        var (gw, conn, db, clock) = await Ready(exitAfterMinutes: 1m);
        using var _1 = db;
        await using var _2 = gw;

        var lost = await TwoUnvaluableAndTheFirstExitsCloseLost(gw, conn, db, clock, f => f.DropAfterBrokerAccept = 1);
        Assert.Equal(0m, Held(conn, "ES"));
        Assert.Equal(1m, Held(conn, "NQ"));

        await Passes(gw, clock, 1);

        var settled = gw.Requests.Get(lost.RequestId)!;
        var confirm = RecordFor(db, conn, "loss_valuation_exit_confirm:", "ES");
        log.WriteLine($"closes on the wire    : {conn.Closes}; ES {Held(conn, "ES")} NQ {Held(conn, "NQ")}");
        log.WriteLine($"valuation rows        : {string.Join(" | ", Rows(db, "op-valuation-"))}");
        log.WriteLine($"ES's confirm          : {WhyOf(confirm)}");

        Assert.Equal(ExecutionState.FILLED, settled.State);
        Assert.False(settled.NeedsReconciliation);
        Assert.Contains("CONFIRMED FROM YOUR PLATFORM'S ORDER HISTORY", WhyOf(confirm), StringComparison.Ordinal);
        Assert.Equal(1, Records(db, "loss_valuation_exit_confirm:"));
        Assert.Equal(0, Records(db, "loss_valuation_exit_again:"));
        Assert.Equal(2, conn.Closes);
        Assert.Equal(0m, Held(conn, "ES"));
        Assert.Equal(0m, Held(conn, "NQ"));
        Assert.NotNull(RecordFor(db, conn, "loss_valuation_exit:", "NQ"));
        Assert.Empty(Flagged(db, "op-valuation-"));
        Assert.False(gw.HasUnconfirmedWork());

        // THE SURFACES SAY ES'S LATEST WORD — the confirm — and never "cannot confirm" about a close it has decided.
        var word = ExitWordFor(gw, "ES");
        log.WriteLine($"ES on the surfaces    : {word}");
        Assert.Contains("CONFIRMED FROM YOUR PLATFORM'S ORDER HISTORY", word, StringComparison.Ordinal);
        Assert.DoesNotContain("CANNOT CONFIRM", word, StringComparison.Ordinal);
        Assert.Equal(2, gw.ValuationReading().Exits.Count);

        // AND NOTHING MORE: later passes send nothing and write nothing.
        await Passes(gw, clock, 6);
        Assert.Equal(2, conn.Closes);
        Assert.Equal(1, Records(db, "loss_valuation_exit_confirm:"));
        Assert.Equal(0, Records(db, "loss_valuation_exit_again:"));
    }

    /// <summary>
    /// (b) THE EXIT'S CLOSE NEVER REACHED THE PLATFORM: NOTHING INSIDE THE GRACE, THEN CANCELLED FROM THE SIMULATOR'S
    /// COMPLETE HISTORY, ES CLOSED AGAIN — ONCE, UNDER ITS OWN FAMILY — NQ EXITED, AND BOTH FLAT (item 2).
    ///
    /// <para>The simulator's closes carry the id they are handed, so a history that answered and lists nothing
    /// under it, once the close can no longer be on its way, says it never reached the platform
    /// (<c>U-flatten-absence</c>'s one guard). ES still reads open and still cannot be valued, so the reason the
    /// exit was sent for still holds: the same exit runs again — cancels first, reduction only at the wire — and
    /// files itself under <c>loss_valuation_exit_again:</c>. Before this unit ES stayed open for as long as nobody
    /// came, behind a pause, with NQ open beside it.</para>
    /// </summary>
    [Fact]
    public async Task An_exits_lost_close_that_never_reached_the_platform_is_closed_again_once_and_the_next_exit_goes_out()
    {
        var (gw, conn, db, clock) = await Ready(exitAfterMinutes: 1m);
        using var _1 = db;
        await using var _2 = gw;

        var lost = await TwoUnvaluableAndTheFirstExitsCloseLost(gw, conn, db, clock, f => f.DropBeforeBrokerAccept = 1);
        Assert.Equal(0, conn.Broker.CountByClientOrderId(lost.ClientOrderId));
        Assert.Equal(1m, Held(conn, "ES"));

        // ONE SECOND SHORT OF THE GRACE: nothing decided, nothing written, nothing sent.
        clock.MoveTo(NoLongerOnItsWay(gw, lost) - TimeSpan.FromSeconds(1));
        await gw.RefreshHealthAsync();
        var inside = gw.Requests.Get(lost.RequestId)!;
        log.WriteLine($"inside the grace      : {inside.State} flagged={inside.NeedsReconciliation}, closes {conn.Closes}");
        Assert.Equal(ExecutionState.UNKNOWN, inside.State);
        Assert.True(inside.NeedsReconciliation);
        Assert.Equal(1, conn.Closes);
        Assert.Equal(0, Records(db, "loss_valuation_exit_confirm:"));

        // PAST IT.
        await Passes(gw, clock, 1);

        var settled = gw.Requests.Get(lost.RequestId)!;
        var closes = ClosesAtTheBook(conn, TradingGateway.ValuationClosePress);
        log.WriteLine($"closes on the wire    : {conn.Closes}; ES {Held(conn, "ES")} NQ {Held(conn, "NQ")}");
        log.WriteLine($"exit closes at book   : [{string.Join(" | ", closes.Select(o => $"{o.ClientOrderId} {o.Symbol} {o.Side} {o.Quantity} {o.State}"))}]");
        log.WriteLine($"valuation rows        : {string.Join(" | ", Rows(db, "op-valuation-"))}");
        log.WriteLine($"ES's confirm          : {WhyOf(RecordFor(db, conn, "loss_valuation_exit_confirm:", "ES"))}");
        log.WriteLine($"ES's closing again    : {WhyOf(RecordFor(db, conn, "loss_valuation_exit_again:", "ES"))}");

        Assert.Equal(ExecutionState.CANCELLED, settled.State);
        Assert.False(settled.NeedsReconciliation);
        Assert.Contains("never reached the platform", settled.LastError!, StringComparison.Ordinal);
        Assert.Equal(1, Records(db, "loss_valuation_exit_confirm:"));
        Assert.Equal(1, Records(db, "loss_valuation_exit_again:"));
        Assert.NotNull(RecordFor(db, conn, "loss_valuation_exit_again:", "ES"));
        Assert.Equal(3, conn.Closes);
        Assert.Equal(2, closes.Count);
        Assert.All(closes, o => Assert.Equal(ExecutionState.FILLED, o.State));
        Assert.Equal(1, closes.Count(o => o.Symbol == "ES"));
        Assert.Equal(1, closes.Count(o => o.Symbol == "NQ"));
        Assert.Equal(0m, Held(conn, "ES"));
        Assert.Equal(0m, Held(conn, "NQ"));
        Assert.Empty(Flagged(db, "op-valuation-"));
        Assert.False(gw.HasUnconfirmedWork());

        // THE SURFACES SAY THE CLOSING AGAIN, AND WHAT SETTLED THE FIRST CLOSE.
        var word = ExitWordFor(gw, "ES");
        log.WriteLine($"ES on the surfaces    : {word}");
        Assert.Contains("CLOSED AGAIN", word, StringComparison.Ordinal);
        Assert.Contains("your platform's order history settled it", word, StringComparison.Ordinal);

        // THE EPISODE KEEPS THE EXIT IT WAS SENT FOR, and later passes send nothing and write nothing.
        var episode = Json.Read<ValuationUnavailableRecord>(db.GetKv(ValuationLoss.KeyFor(conn.Id, conn.Broker.AccountId, "ES"))!)!;
        Assert.Equal(ValuationLoss.ExitKey(conn.Id, conn.Broker.AccountId, "ES", episode.Since), episode.ExitKey);
        await Passes(gw, clock, 6);
        Assert.Equal(3, conn.Closes);
        Assert.Equal(1, Records(db, "loss_valuation_exit_again:"));
        Assert.Equal(1, Records(db, "loss_valuation_exit_confirm:"));
        Assert.Equal(0, Records(db, "loss_valuation_exit_again_confirm:"));
    }

    /// <summary>
    /// (c) THE GUARD: (b), WITH ES VALUED AGAIN BEFORE THE CONFIRM — CONFIRMED, NOTHING SENT FOR ES, AND SAID
    /// (item 2, and the mutant; the second case is item 3's reading).
    ///
    /// <para>The reason the exit was sent for is that nobody could value ES. Its price comes back while NQ's stays
    /// silent, so ES's episode ends. Past the grace the confirm still decides the lost close (CANCELLED) and still
    /// reads ES open — and closes nothing: ES is measured again, the loss budget bounds it again, and what the
    /// owner reads says that is why. NQ, still unvaluable, is exited.</para>
    ///
    /// <para>Two windows. "before the confirm": the price is back inside the grace, so the confirm's own sentence
    /// says it. "in the confirm's own pass": the price comes back on the very pass the confirm is written — the
    /// confirm, at the start of the pass, still reads the episode standing and its record says CLOSING IT AGAIN, true
    /// of its instant; the watch, at the end of the same pass, values ES — and then nothing may be sent, and the
    /// reading must not go on saying it will be.</para>
    ///
    /// <para><b>The mutant this watches:</b> the closing again's check that the episode it was sent for still
    /// stands, removed. The again then closes ES over a position the owner's budget measures — a third close at
    /// the wire, and ES 0, where it must stay 1.</para>
    /// </summary>
    [Theory]
    [InlineData("before the confirm")]
    [InlineData("in the confirm's own pass")]
    public async Task An_exits_lost_close_over_a_position_valued_again_is_confirmed_and_nothing_more_is_sent_for_it(string valuedAgain)
    {
        var (gw, conn, db, clock) = await Ready(exitAfterMinutes: 1m);
        using var _1 = db;
        await using var _2 = gw;

        var lost = await TwoUnvaluableAndTheFirstExitsCloseLost(gw, conn, db, clock, f => f.DropBeforeBrokerAccept = 1);
        log.WriteLine($"[{valuedAgain}]");

        if (valuedAgain == "before the confirm")
        {
            // ES'S PRICE COMES BACK, INSIDE THE GRACE; NQ'S DOES NOT.
            conn.QuoteAgeOf = s => s == "ES" ? TimeSpan.Zero : null;
            await Passes(gw, clock, 1);
            var es = Json.Read<ValuationUnavailableRecord>(db.GetKv(ValuationLoss.KeyFor(conn.Id, conn.Broker.AccountId, "ES"))!)!;
            log.WriteLine($"ES's episode          : cleared {es.ClearedAt:HH:mm:ss}, exit {es.ExitKey}");
            Assert.False(es.Standing);
            Assert.Equal(0, Records(db, "loss_valuation_exit_confirm:"));
            Assert.Equal(1, conn.Closes);

            // PAST THE GRACE.
            clock.MoveTo(NoLongerOnItsWay(gw, lost));
            await Passes(gw, clock, 1);
        }
        else
        {
            // PAST THE GRACE, AND ES'S PRICE COMES BACK ON THAT VERY PASS.
            clock.MoveTo(NoLongerOnItsWay(gw, lost));
            conn.QuoteAgeOf = s => s == "ES" ? TimeSpan.Zero : null;
            await Passes(gw, clock, 1);
            Assert.Contains("CLOSING IT AGAIN", WhyOf(RecordFor(db, conn, "loss_valuation_exit_confirm:", "ES")), StringComparison.Ordinal);
        }

        var confirm = RecordFor(db, conn, "loss_valuation_exit_confirm:", "ES");
        log.WriteLine($"closes on the wire    : {conn.Closes}; ES {Held(conn, "ES")} NQ {Held(conn, "NQ")}");
        log.WriteLine($"valuation rows        : {string.Join(" | ", Rows(db, "op-valuation-"))}");
        log.WriteLine($"ES's confirm          : {WhyOf(confirm)}");

        Assert.Equal(ExecutionState.CANCELLED, gw.Requests.Get(lost.RequestId)!.State);
        Assert.Equal(1, Records(db, "loss_valuation_exit_confirm:"));
        Assert.Equal(0, Records(db, "loss_valuation_exit_again:"));
        if (valuedAgain == "before the confirm")
        {
            Assert.Contains("NOT closing it again", WhyOf(confirm), StringComparison.Ordinal);
            Assert.Contains("can value ES again", WhyOf(confirm), StringComparison.Ordinal);
        }
        Assert.Equal(2, conn.Closes);
        Assert.Equal(1m, Held(conn, "ES"));
        Assert.Equal(0m, Held(conn, "NQ"));
        Assert.Empty(Flagged(db, "op-valuation-"));
        Assert.False(gw.HasUnconfirmedWork());

        // SAID, AS IT STANDS NOW: not closing it again, and why — whatever the record said at its own instant.
        var word = ExitWordFor(gw, "ES");
        log.WriteLine($"ES on the surfaces    : {word}");
        Assert.Contains("NOT closing it again", word, StringComparison.Ordinal);
        Assert.Contains("can value ES again", word, StringComparison.Ordinal);
        Assert.DoesNotContain("CLOSING IT AGAIN", word, StringComparison.Ordinal);

        // AND NOTHING MORE.
        await Passes(gw, clock, 6);
        Assert.Equal(2, conn.Closes);
        Assert.Equal(1m, Held(conn, "ES"));
        Assert.Equal(0, Records(db, "loss_valuation_exit_again:"));
    }

    /// <summary>
    /// (d) (b) WITH THE HISTORY HIDDEN, AS ON ATAS: WITH NO ANSWER, NOTHING IS DECIDED OR SENT (green before this
    /// unit and after it); THE OWNER'S "NO ORDER EXISTS", PAST HIS CLOCK, CLOSES ES AGAIN ONCE AND NQ IS EXITED
    /// (item 2).
    ///
    /// <para>Where no history can be asked, nothing but the owner can say what became of the close, and minutes of
    /// passes past the grace decide nothing, write nothing and send nothing. His answer on the card is his own
    /// measurement: counted once the close can no longer be on its way, it decides the leg, the confirm reads ES
    /// open while it still cannot be valued, and ES is closed again exactly as in (b). Before this unit his answer
    /// settled the row and nothing else — ES 1 stayed open, its episode standing (probe P_exit, "never reached").</para>
    /// </summary>
    [Fact]
    public async Task An_exits_lost_close_with_the_history_hidden_waits_for_the_owner_and_his_answer_closes_it_again_once()
    {
        var (gw, conn, db, clock) = await Ready(exitAfterMinutes: 1m);
        using var _1 = db;
        await using var _2 = gw;

        var lost = await TwoUnvaluableAndTheFirstExitsCloseLost(gw, conn, db, clock, f => f.DropBeforeBrokerAccept = 1);
        conn.Faults.HideOrderHistory = true;
        Assert.False(conn.Capabilities.ReconciliationProvable);

        // PAST THE GRACE BY MINUTES, AND NOBODY HAS ANSWERED.
        await Passes(gw, clock, 9);
        Assert.True(clock.GetUtcNow() > NoLongerOnItsWay(gw, lost));
        var waiting = gw.Requests.Get(lost.RequestId)!;
        log.WriteLine($"no answer             : {waiting.State} flagged={waiting.NeedsReconciliation}, closes {conn.Closes}; "
                      + $"ES {Held(conn, "ES")} NQ {Held(conn, "NQ")}");
        Assert.Equal(ExecutionState.UNKNOWN, waiting.State);
        Assert.True(waiting.NeedsReconciliation);
        Assert.Equal(1, conn.Closes);
        Assert.Equal(1m, Held(conn, "ES"));
        Assert.Equal(1m, Held(conn, "NQ"));
        Assert.Equal(0, Records(db, "loss_valuation_exit_confirm:"));
        Assert.True(gw.HasUnconfirmedWork());

        // HE ANSWERS "NO ORDER EXISTS" ON THE CARD, PAST HIS CLOCK.
        gw.ForceResolve(lost.RequestId, ExecutionState.CANCELLED, "I checked in ATAS: no such order exists");
        await Passes(gw, clock, 1);

        var confirm = RecordFor(db, conn, "loss_valuation_exit_confirm:", "ES");
        var closes = ClosesAtTheBook(conn, TradingGateway.ValuationClosePress);
        log.WriteLine($"closes on the wire    : {conn.Closes}; ES {Held(conn, "ES")} NQ {Held(conn, "NQ")}");
        log.WriteLine($"exit closes at book   : [{string.Join(" | ", closes.Select(o => $"{o.ClientOrderId} {o.Symbol} {o.Side} {o.Quantity} {o.State}"))}]");
        log.WriteLine($"ES's confirm          : {WhyOf(confirm)}");

        Assert.Contains("CONFIRMED", WhyOf(confirm).ToUpperInvariant(), StringComparison.Ordinal);
        Assert.Contains("your answer on the Dashboard", WhyOf(confirm), StringComparison.Ordinal);
        Assert.Equal(1, Records(db, "loss_valuation_exit_again:"));
        Assert.Equal(3, conn.Closes);
        Assert.Equal(1, closes.Count(o => o.Symbol == "ES"));
        Assert.Equal(1, closes.Count(o => o.Symbol == "NQ"));
        Assert.Equal(0m, Held(conn, "ES"));
        Assert.Equal(0m, Held(conn, "NQ"));
        Assert.Empty(Flagged(db, "op-valuation-"));
        Assert.False(gw.HasUnconfirmedWork());

        var word = ExitWordFor(gw, "ES");
        log.WriteLine($"ES on the surfaces    : {word}");
        Assert.Contains("CLOSED AGAIN", word, StringComparison.Ordinal);
        Assert.Contains("your answer on the Dashboard settled it", word, StringComparison.Ordinal);

        // HIS ANSWER STAYS HIS, and nothing more is sent.
        var row = gw.Requests.Get(lost.RequestId)!;
        Assert.Equal(ExecutionState.CANCELLED, row.State);
        Assert.StartsWith(TradingGateway.ResolvedByOwnerPrefix, row.LastError, StringComparison.Ordinal);
        await Passes(gw, clock, 6);
        Assert.Equal(3, conn.Closes);
        Assert.Equal(1, Records(db, "loss_valuation_exit_again:"));
    }
}

using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// A LOST CLOSE OF THE LOSS BUDGET THAT THE OWNER HAS ANSWERED ON THE DASHBOARD IS A DECIDED LEG, UNDER
/// THE PLATFORM'S LIVE-ORDER VETO (<c>U-loss-hold-release</c>).
///
/// <para>Until this unit the confirm (<c>U-flatten-confirm</c>) answered a lost close only while it was
/// still UNKNOWN, and only where the platform's history is provable. On ATAS the owner's answer on the
/// card is the only way a lost close is ever settled, and the moment he gave it the confirm stopped
/// answering for it: the first outcome's <c>Flat=false</c> stayed the latest word, and the closure was
/// held for ever by "TradeAgent cannot confirm that what it closed for you is closed" over a book he had
/// accounted for — the Dashboard still asking him to confirm records, with none left.</para>
///
/// <para>Every test runs on <c>LossFlattenConfirmTests</c>' fixture: the connector's real two-second
/// emergency budget and a coherent clock. The owner's answer goes through the card's own route,
/// <see cref="TradingGateway.ForceResolve"/>, with the words he typed. Every verdict is the book, a row
/// or a count at the wire.</para>
/// </summary>
public class LossHoldReleaseTests(ITestOutputHelper log)
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

    static async Task<(TradingGateway Gw, RecordingConnector Conn, Database Db, TestClock Clock)> Ready()
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

    /// <summary>The app's own budget rows, whole, oldest first: id, state, flag and the row's own why.</summary>
    static List<string> AppRows(Database db)
    {
        using var c = db.Cmd("SELECT request_id, execution_state, needs_reconciliation, COALESCE(last_error,'') "
                             + "FROM execution_request WHERE request_id LIKE 'op-budget-%' ORDER BY created_at");
        using var r = c.ExecuteReader();
        var rows = new List<string>();
        while (r.Read()) rows.Add($"{r.GetString(0)} {r.GetString(1)} flagged={r.GetInt64(2)} '{r.GetString(3)}'");
        return rows;
    }

    /// <summary>Every close the app put on the broker's book, in the order it arrived.</summary>
    static List<OrderInfo> AppCloses(RecordingConnector conn) =>
        [.. conn.Broker.Orders.Where(o => o.ClientOrderId?.StartsWith("TA-op-budget-close-", StringComparison.Ordinal) == true)];

    /// <summary>The confirms written so far, by their family's prefix: there is at most one per breach.</summary>
    static int Confirms(Database db) => db.KvStartingWith("loss_flatten_confirm:").Count;

    /// <summary>What the confirm said to the engineering log about why it has not been written: event and why.</summary>
    static List<string> Undecided(Database db)
    {
        using var c = db.Cmd("SELECT COALESCE(metadata,'') FROM engineering_log WHERE event='loss_flatten_confirm_undecided' ORDER BY id");
        using var r = c.ExecuteReader();
        var rows = new List<string>();
        while (r.Read()) rows.Add(r.GetString(0));
        return rows;
    }

    /// <summary>
    /// A confirmed daily breach whose flatten put ONE close on the wire and lost its answer — the fault
    /// armed by the caller — on the health pass every host runs: the first sighting, then the
    /// confirming pass, whose watch flattens at the end of it. Answers the lost close's row.
    /// </summary>
    static async Task<ExecutionRequest> BreachWithALostClose(TradingGateway gw, RecordingConnector conn, TestClock clock)
    {
        conn.Broker.PriceOffset = -20m;
        await Passes(gw, clock, 2);

        var account = conn.Broker.AccountId;
        Assert.NotNull(gw.DayClosed(account));
        var first = gw.FlattenToday(account);
        Assert.NotNull(first);
        Assert.False(first.Flat);
        var leg = Assert.Single(first.Legs);
        Assert.Equal(nameof(ExecutionState.UNKNOWN), leg.State);
        Assert.Equal(1, conn.Closes);
        return gw.Requests.Get(leg.RequestId)!;
    }

    /// <summary>
    /// THE OWNER ANSWERS EVERY RECORD THE CARD SHOWS, exactly as it offers them: the lost close with
    /// <paramref name="lostAs"/> ("It was filled" or "No order exists"), and a record the platform already
    /// answered with "Our record is right". Answers what he said, row by row.
    /// </summary>
    static List<string> AnswerEveryRecord(TradingGateway gw, string lost, ExecutionState lostAs, string note)
    {
        var said = new List<string>();
        foreach (var row in gw.Unreconciled())
        {
            var answer = row.RequestId == lost ? lostAs : SpokenByThePlatform(row.State) ? row.State : lostAs;
            gw.ForceResolve(row.RequestId, answer, row.RequestId == lost ? note : "I checked in ATAS: our record is right");
            said.Add($"{row.RequestId} {row.State} -> {answer}");
        }

        return said;
    }

    /// <summary>The card's own rule (<c>DashboardView.SpokenByThePlatform</c>): one button, "Our record is right".</summary>
    static bool SpokenByThePlatform(ExecutionState s) => s is
        ExecutionState.FILLED or ExecutionState.CANCELLED or ExecutionState.REJECTED or
        ExecutionState.WORKING or ExecutionState.ACKNOWLEDGED or
        ExecutionState.PARTIALLY_FILLED or ExecutionState.CANCEL_PENDING;

    /// <summary>
    /// When the lost close can no longer be on its way to the platform: the confirm's own clock for an
    /// answer it did not get from the platform (<c>AbsenceCountsFrom</c> plus the grace) — for a press
    /// leg, whose dispatch nothing holds, the dispatch plus <see cref="TradingGateway.DispatchStrandedAfter"/>.
    /// </summary>
    static DateTimeOffset NoLongerOnItsWay(TradingGateway gw, ExecutionRequest lost) =>
        lost.DispatchedAt!.Value + gw.DispatchStrandedAfter + Grace;

    // ---------------------------------------------------------------- (a)

    /// <summary>
    /// (a) THE HISTORY HIDDEN, AS ON ATAS: THE OWNER ANSWERS THE LOST CLOSE "IT WAS FILLED" AND EVERY OTHER
    /// RECORD — THE CONFIRM IS WRITTEN FROM HIS ANSWER, THE BOOK READS FLAT, NOTHING MORE IS SENT, AND THE
    /// CLOSURE REOPENS ONCE ITS TIME HAS RUN.
    ///
    /// <para>The broker took the close and filled it; its answer was lost; the platform cannot show its
    /// history. Before this unit the owner's answer was the end of it: no confirm, the first outcome's
    /// <c>Flat=false</c> the latest word, and past every eligibility instant the tick still wrote nothing —
    /// a closure held for ever over a flat book nothing was left to confirm.</para>
    /// </summary>
    [Fact]
    public async Task A_lost_close_the_owner_answers_filled_with_the_history_hidden_is_confirmed_flat_and_the_closure_reopens()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        await using var _2 = gw;
        var account = conn.Broker.AccountId;

        await gw.PlaceAsync(new AgentContext("a"), "owner-filled-open", TestEnv.Buy("ES"));
        conn.Faults.DropAfterBrokerAccept = 1;
        var lost = await BreachWithALostClose(gw, conn, clock);
        Assert.Equal(0m, Held(conn, "ES"));

        conn.Faults.HideOrderHistory = true;
        Assert.False(conn.Capabilities.ReconciliationProvable);

        var said = AnswerEveryRecord(gw, lost.RequestId, ExecutionState.FILLED, "I checked in ATAS: the close filled");
        log.WriteLine($"the owner answered    : {string.Join(" | ", said)}");
        Assert.False(gw.HasUnconfirmedWork());

        // ONE SECOND SHORT OF THE CLOCK: nothing written yet — and nothing asks him to confirm records he has
        // already answered; what the confirm is waiting on is said instead.
        clock.MoveTo(NoLongerOnItsWay(gw, lost) - TimeSpan.FromSeconds(1));
        await gw.RefreshHealthAsync();
        var waiting = gw.FlattenStateToday();
        log.WriteLine($"inside the clock      : confirms {Confirms(db)} — {waiting.State} — {waiting.Why}");
        Assert.Equal(0, Confirms(db));
        Assert.Equal("unresolved", waiting.State);
        Assert.Contains("You have answered every record it left on the Dashboard", waiting.Why!, StringComparison.Ordinal);
        Assert.Contains("can no longer be on its way", waiting.Why!, StringComparison.Ordinal);
        Assert.DoesNotContain("until you confirm those records", waiting.Why!, StringComparison.Ordinal);

        await Passes(gw, clock, 1);

        var state = gw.FlattenStateToday();
        log.WriteLine($"closes on the wire    : {conn.Closes}, position ES {Held(conn, "ES")}");
        log.WriteLine($"app rows              : {string.Join(" | ", AppRows(db))}");
        log.WriteLine($"confirms              : {Confirms(db)}");
        log.WriteLine($"dashboard             : {state.State} — {state.Why}");

        Assert.Equal(1, Confirms(db));
        Assert.Equal("flat", state.State);
        Assert.Contains("CONFIRMED FROM YOUR ANSWER ON THE DASHBOARD", state.Why!, StringComparison.Ordinal);
        Assert.Contains("you confirmed it on the Dashboard: I checked in ATAS: the close filled", state.Why!, StringComparison.Ordinal);
        Assert.DoesNotContain("ORDER HISTORY", state.Why!, StringComparison.Ordinal);
        Assert.Equal(1, conn.Closes);
        Assert.Equal(0m, Held(conn, "ES"));
        Assert.Equal(ExecutionState.FILLED, gw.Requests.Get(lost.RequestId)!.State);
        Assert.False(gw.HasUnconfirmedWork());

        // THE FIRST OUTCOME IS UNTOUCHED, AND THE CLOSURE STANDS UNTIL ITS TIME HAS RUN.
        Assert.False(gw.FlattenToday(account)!.Flat);
        var breach = gw.DayClosed(account)!;

        clock.MoveTo(LossReopen.EligibleAt(breach.ConfirmedAt, TimeSpan.FromHours(24)) + TimeSpan.FromMinutes(1));
        conn.Broker.PriceOffset = 0m;
        var reopen = await gw.LossWatchAsync();
        log.WriteLine($"past eligibility      : reopened [{string.Join(", ", reopen.Reopened)}]; day closed {gw.DayClosed(account) is not null}");

        Assert.Single(reopen.Reopened);
        Assert.Null(gw.DayClosed(account));
        Assert.Equal(1, conn.Closes);
    }

    // ---------------------------------------------------------------- (b)

    /// <summary>
    /// (b) THE CLOSE NEVER REACHED THE BROKER AND THE OWNER ANSWERS "NO ORDER EXISTS": NOTHING WHILE THE
    /// CLOSE COULD STILL BE ON ITS WAY, THEN THE BOOK IS CLOSED AGAIN — ONCE, UNDER A NEW ID — AND READS
    /// FLAT; WHETHER OR NOT THE PLATFORM CAN SHOW ITS HISTORY.
    ///
    /// <para>ES 1 is still open. Before this unit his answer settled the row and nothing else: one close
    /// on the wire, ES 1, and the closure held for ever. Now his answer decides the leg — once the close
    /// can no longer be on its way to the platform, the clock absence itself is held to — the confirm reads
    /// the book open, and the sweep runs the landed closing again: one more close, filled, flat. Later
    /// passes send nothing.</para>
    /// </summary>
    [Theory]
    [InlineData("history hidden")]
    [InlineData("history shown")]
    public async Task A_lost_close_the_owner_answers_never_existed_is_closed_again_once_and_reads_flat(string history)
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        await using var _2 = gw;
        var account = conn.Broker.AccountId;

        await gw.PlaceAsync(new AgentContext("a"), "owner-never-open", TestEnv.Buy("ES"));
        conn.Faults.DropBeforeBrokerAccept = 1;
        var lost = await BreachWithALostClose(gw, conn, clock);
        Assert.Equal(1m, Held(conn, "ES"));
        Assert.Equal(0, conn.Broker.CountByClientOrderId(lost.ClientOrderId));

        if (history == "history hidden") conn.Faults.HideOrderHistory = true;
        Assert.Equal(history == "history shown", conn.Capabilities.ReconciliationProvable);

        gw.ForceResolve(lost.RequestId, ExecutionState.CANCELLED, "I checked in ATAS: no such order exists");

        // ONE SECOND SHORT OF THE CLOCK: nothing decided, nothing written, nothing sent.
        clock.MoveTo(NoLongerOnItsWay(gw, lost) - TimeSpan.FromSeconds(1));
        await gw.RefreshHealthAsync();
        log.WriteLine($"[{history}]");
        log.WriteLine($"inside the clock      : closes {conn.Closes}, confirms {Confirms(db)}, ES {Held(conn, "ES")}");
        Assert.Equal(1, conn.Closes);
        Assert.Equal(0, Confirms(db));
        Assert.Equal(1m, Held(conn, "ES"));

        // PAST IT.
        await Passes(gw, clock, 1);

        var closes = AppCloses(conn);
        var state = gw.FlattenStateToday();
        log.WriteLine($"closes on the wire    : {conn.Closes}, position ES {Held(conn, "ES")}");
        log.WriteLine($"app closes at the book: [{string.Join(" | ", closes.Select(o => $"{o.ClientOrderId} {o.Side} {o.Quantity} {o.State}"))}]");
        log.WriteLine($"app rows              : {string.Join(" | ", AppRows(db))}");
        log.WriteLine($"dashboard             : {state.State} — {state.Why}");

        Assert.Equal(1, Confirms(db));
        Assert.Equal(2, conn.Closes);
        var again = Assert.Single(closes);
        Assert.NotEqual(lost.ClientOrderId, again.ClientOrderId);
        Assert.Equal(ExecutionState.FILLED, again.State);
        Assert.Equal(1m, again.Quantity);
        Assert.Equal(0m, Held(conn, "ES"));
        Assert.Equal("flat", state.State);
        Assert.Contains("CLOSED AGAIN", state.Why!, StringComparison.Ordinal);
        Assert.Contains("your answer on the Dashboard settled it", state.Why!, StringComparison.Ordinal);
        Assert.False(gw.HasUnconfirmedWork());

        // HIS ANSWER STAYS HIS: the row keeps the state and the words he gave it.
        var row = gw.Requests.Get(lost.RequestId)!;
        Assert.Equal(ExecutionState.CANCELLED, row.State);
        Assert.StartsWith(TradingGateway.ResolvedByOwnerPrefix, row.LastError, StringComparison.Ordinal);

        // AND NOTHING MORE: later passes send nothing and write nothing.
        await Passes(gw, clock, 2);
        Assert.Equal(2, conn.Closes);
        Assert.Equal(1, Confirms(db));
        Assert.Equal(0m, Held(conn, "ES"));
        Assert.NotNull(gw.DayClosed(account));
    }

    // ---------------------------------------------------------------- (c), and the mutant

    /// <summary>
    /// (c) THE OWNER ANSWERS "NO ORDER EXISTS" WHILE THE CLOSE IS STILL WORKING AT THE PLATFORM, WHICH CAN
    /// SHOW ITS HISTORY: NO CONFIRM, NOTHING SENT, NOTHING CANCELLED — AND WHY IS SAID.
    ///
    /// <para>A live close can still fill. His word is his own measurement, and a platform that holds the
    /// order live outranks it: counting it decided would read the book open and close again on top of an
    /// order that may fill a second later. So pass after pass the close is left exactly where it rests,
    /// and the reason is named — to the engineering log, and on the surfaces in place of asking him to
    /// confirm records he has already answered.</para>
    ///
    /// <para><b>The mutant this watches:</b> the live-order veto dropped. His CANCELLED is then taken, the
    /// confirm is written over a live close, the book reads open and the closing again cancels the resting
    /// close at the platform and sends another — <c>Closes</c> 2 and a confirm here, where nothing may
    /// move.</para>
    /// </summary>
    [Fact]
    public async Task A_lost_close_the_owner_answers_never_existed_while_the_platform_holds_it_working_decides_nothing()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        await using var _2 = gw;
        var account = conn.Broker.AccountId;

        await gw.PlaceAsync(new AgentContext("a"), "owner-working-open", TestEnv.Buy("ES"));
        conn.Faults.Fill = FillBehaviour.LeaveWorking;
        conn.Faults.DropAfterBrokerAccept = 1;
        var lost = await BreachWithALostClose(gw, conn, clock);
        var close = Assert.Single(AppCloses(conn));
        Assert.Equal(ExecutionState.WORKING, close.State);
        Assert.True(conn.Capabilities.ReconciliationProvable);
        var cancels = conn.Cancels;

        var said = AnswerEveryRecord(gw, lost.RequestId, ExecutionState.CANCELLED, "I checked in ATAS: no such order exists");
        log.WriteLine($"the owner answered    : {string.Join(" | ", said)}");

        clock.MoveTo(NoLongerOnItsWay(gw, lost));
        await Passes(gw, clock, 3);

        var resting = conn.Broker.Orders.Single(o => o.ConnectorOrderId == close.ConnectorOrderId);
        var undecided = Undecided(db);
        var state = gw.FlattenStateToday();
        log.WriteLine($"the close at the book : {resting.Side} {resting.Quantity} {resting.State}");
        log.WriteLine($"closes on the wire    : {conn.Closes}, cancels {cancels} -> {conn.Cancels}, position ES {Held(conn, "ES")}");
        log.WriteLine($"app rows              : {string.Join(" | ", AppRows(db))}");
        log.WriteLine($"confirms              : {Confirms(db)}");
        log.WriteLine($"undecided             : {string.Join(" | ", undecided)}");
        log.WriteLine($"dashboard             : {state.State} — {state.Why}");

        Assert.Equal(0, Confirms(db));
        Assert.Equal(1, conn.Closes);
        Assert.Equal(cancels, conn.Cancels);
        Assert.Equal(ExecutionState.WORKING, resting.State);
        Assert.Equal(1m, Held(conn, "ES"));
        Assert.Empty(db.KvStartingWith("loss_flatten_again:"));
        Assert.NotNull(gw.DayClosed(account));

        // NAMED: the platform holds it WORKING, and that outranks his answer — to the engineering log, and on
        // the surfaces, which no longer ask him to confirm the records he has answered.
        Assert.Contains(undecided, u => u.Contains("WORKING", StringComparison.Ordinal)
                                         && u.Contains("outranks your answer", StringComparison.Ordinal));
        Assert.Equal("unresolved", state.State);
        Assert.Contains("You have answered every record it left on the Dashboard", state.Why!, StringComparison.Ordinal);
        Assert.Contains("holds it as WORKING", state.Why!, StringComparison.Ordinal);
        Assert.Contains("outranks your answer", state.Why!, StringComparison.Ordinal);
        Assert.DoesNotContain("until you confirm those records", state.Why!, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- (d)

    /// <summary>
    /// (d) THE CANCEL HALF'S ANSWER LOST AND SETTLED BY THE OWNER, THE CLOSE'S ANSWER LOST AND FOUND IN THE
    /// PLATFORM'S HISTORY: CONFIRMED FROM THE HISTORY (green before this unit and after it).
    ///
    /// <para>The flatten cancels the working opener first; the platform takes the cancel and its answer is
    /// lost, so that row stays UNKNOWN while the book reads the opener gone and the close goes out — and its
    /// answer is lost too. A non-final cancel row means no confirm at all; once the owner has settled it,
    /// the close is the platform's to answer, exactly as before this unit.</para>
    /// </summary>
    [Fact]
    public async Task A_cancel_the_owner_settles_and_a_close_the_history_finds_are_confirmed_from_the_history()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        await using var _2 = gw;
        var account = conn.Broker.AccountId;

        await gw.PlaceAsync(new AgentContext("a"), "owner-cancel-open", TestEnv.Buy("ES"));

        // A WORKING OPENER the flatten must cancel before it closes anything.
        conn.Faults.Fill = FillBehaviour.LeaveWorking;
        await gw.PlaceAsync(new AgentContext("a"), "owner-cancel-opener", TestEnv.Buy("ES"));
        conn.Faults.Fill = FillBehaviour.FillImmediately;
        var opener = conn.Broker.Orders.Single(o => o.ClientOrderId == "TA-owner-cancel-opener");
        Assert.Equal(ExecutionState.WORKING, opener.State);

        // THE PLATFORM TAKES THE CANCEL AND ITS ANSWER IS LOST; THEN THE CLOSE'S ANSWER IS LOST TOO.
        conn.Seam = kind =>
        {
            if (kind == RecordingConnector.HeldCall.Cancel) conn.Broker.Cancel(opener.ConnectorOrderId);
            return Task.CompletedTask;
        };
        conn.Faults.LoseAfterSend = 1;
        conn.Faults.DropAfterBrokerAccept = 1;
        var lost = await BreachWithALostClose(gw, conn, clock);
        conn.Seam = null;
        Assert.Equal(0m, Held(conn, "ES"));
        log.WriteLine($"after the flatten     : {string.Join(" | ", AppRows(db))}");

        // NOTHING IS CONFIRMED WHILE THE CANCEL HALF IS NOT FINAL.
        await Passes(gw, clock, 2);
        Assert.Equal(0, Confirms(db));

        // THE OWNER SETTLES THE CANCEL ROW, AND ONLY IT.
        var cancel = Assert.Single(gw.Unreconciled(),
            r => r.RequestId.StartsWith(TradingGateway.BudgetCancelPress + "-", StringComparison.Ordinal)
                 && r.State == ExecutionState.UNKNOWN);
        gw.ForceResolve(cancel.RequestId, ExecutionState.CANCELLED, "I checked in ATAS: the order was cancelled");

        await Passes(gw, clock, 1);

        var state = gw.FlattenStateToday();
        log.WriteLine($"closes on the wire    : {conn.Closes}, position ES {Held(conn, "ES")}");
        log.WriteLine($"app rows              : {string.Join(" | ", AppRows(db))}");
        log.WriteLine($"dashboard             : {state.State} — {state.Why}");

        Assert.Equal(1, Confirms(db));
        Assert.Equal(ExecutionState.FILLED, gw.Requests.Get(lost.RequestId)!.State);
        Assert.Equal("flat", state.State);
        Assert.Contains("ORDER HISTORY", state.Why!, StringComparison.Ordinal);
        Assert.Equal(1, conn.Closes);
        Assert.Equal(0m, Held(conn, "ES"));
        Assert.False(gw.HasUnconfirmedWork());

        var breach = gw.DayClosed(account)!;
        clock.MoveTo(LossReopen.EligibleAt(breach.ConfirmedAt, TimeSpan.FromHours(24)) + TimeSpan.FromMinutes(1));
        conn.Broker.PriceOffset = 0m;
        var reopen = await gw.LossWatchAsync();
        log.WriteLine($"past eligibility      : reopened [{string.Join(", ", reopen.Reopened)}]");
        Assert.Single(reopen.Reopened);
    }

    // ---------------------------------------------------------------- (e)

    /// <summary>
    /// (e) THE HISTORY HIDDEN, THE OWNER ANSWERS "NO ORDER EXISTS" WHILE THE CLOSE STILL RESTS WORKING AT
    /// THE PLATFORM: THE CLOSING AGAIN CANCELS IT AT THE PLATFORM FIRST, SENDS ONE CLOSE, AND THE BOOK READS
    /// FLAT — NEVER SHORT.
    ///
    /// <para>Where the history cannot be asked, nothing can veto a wrong answer, so what stops a second
    /// close filling on top of the first is the landed closing again's own first step: every working order
    /// on an instrument it is about to close is cancelled, and must have settled, before a single close
    /// goes out. The resting close is one of them.</para>
    /// </summary>
    [Fact]
    public async Task A_wrong_never_existed_over_a_resting_close_with_the_history_hidden_cancels_it_first_and_never_goes_short()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        await using var _2 = gw;
        var account = conn.Broker.AccountId;

        await gw.PlaceAsync(new AgentContext("a"), "owner-resting-open", TestEnv.Buy("ES"));
        conn.Faults.Fill = FillBehaviour.LeaveWorking;
        conn.Faults.DropAfterBrokerAccept = 1;
        var lost = await BreachWithALostClose(gw, conn, clock);
        var first = Assert.Single(AppCloses(conn));
        Assert.Equal(ExecutionState.WORKING, first.State);
        conn.Faults.Fill = FillBehaviour.FillImmediately;

        conn.Faults.HideOrderHistory = true;
        Assert.False(conn.Capabilities.ReconciliationProvable);

        gw.ForceResolve(lost.RequestId, ExecutionState.CANCELLED, "I checked in ATAS: no such order exists");

        // THE POSITION IS WATCHED AT EVERY CALL THAT REACHES THE PLATFORM FROM HERE ON: never short.
        var lowest = Held(conn, "ES");
        conn.Seam = _ =>
        {
            lowest = Math.Min(lowest, Held(conn, "ES"));
            return Task.CompletedTask;
        };

        clock.MoveTo(NoLongerOnItsWay(gw, lost));
        await Passes(gw, clock, 1);
        conn.Seam = null;
        lowest = Math.Min(lowest, Held(conn, "ES"));

        var closes = AppCloses(conn);
        var state = gw.FlattenStateToday();
        log.WriteLine($"closes on the wire    : {conn.Closes}, cancels {conn.Cancels}, position ES {Held(conn, "ES")}, lowest {lowest}");
        log.WriteLine($"app closes at the book: [{string.Join(" | ", closes.Select(o => $"{o.ClientOrderId} {o.Side} {o.Quantity} {o.State}"))}]");
        log.WriteLine($"app rows              : {string.Join(" | ", AppRows(db))}");
        log.WriteLine($"dashboard             : {state.State} — {state.Why}");

        Assert.Equal(1, Confirms(db));
        Assert.Equal(2, closes.Count);
        Assert.Equal(first.ConnectorOrderId, closes[0].ConnectorOrderId);
        Assert.Equal(ExecutionState.CANCELLED, closes[0].State);
        Assert.Equal(ExecutionState.FILLED, closes[1].State);
        Assert.Equal(1m, closes[1].Quantity);
        Assert.Equal(2, conn.Closes);
        Assert.Equal(0m, Held(conn, "ES"));
        Assert.Equal(0m, lowest);
        Assert.Equal("flat", state.State);
        Assert.Contains("your answer on the Dashboard settled it", state.Why!, StringComparison.Ordinal);
        Assert.Contains("1 working order(s) were cancelled first", state.Why!, StringComparison.Ordinal);
        Assert.False(gw.HasUnconfirmedWork());

        // AND NOTHING MORE.
        await Passes(gw, clock, 2);
        Assert.Equal(2, conn.Closes);
        Assert.Equal(0m, Held(conn, "ES"));
        Assert.NotNull(gw.DayClosed(account));
    }

    // ---------------------------------------------------------------- (f)

    /// <summary>
    /// (f) A CONFIRM WRITTEN FROM HIS ANSWER DOES NOT CLEAR A CONTRADICTION WRITTEN AFTER IT.
    ///
    /// <para>The confirm is applied again on every pass while the closure stands, and applying it unflags
    /// the first flatten's rows. When the platform answers something other than what he said, after he said
    /// it, his row is flagged again with both answers on it (<c>RecordThePlatformsAnswerBesideTheOwnersClaim</c>)
    /// — written here by another gateway over the same store, the reach that rule exists for. That flag is his
    /// to read: pass after pass it stays, order flow stays refused, and past every eligibility instant the
    /// closure stays held.</para>
    /// </summary>
    [Fact]
    public async Task A_confirm_from_his_answer_does_not_clear_a_contradiction_written_after_it()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        await using var _2 = gw;
        var account = conn.Broker.AccountId;

        await gw.PlaceAsync(new AgentContext("a"), "owner-contradicted-open", TestEnv.Buy("ES"));
        conn.Faults.DropAfterBrokerAccept = 1;
        var lost = await BreachWithALostClose(gw, conn, clock);
        conn.Faults.HideOrderHistory = true;
        AnswerEveryRecord(gw, lost.RequestId, ExecutionState.FILLED, "I checked in ATAS: the close filled");

        clock.MoveTo(NoLongerOnItsWay(gw, lost));
        await Passes(gw, clock, 1);
        Assert.Equal(1, Confirms(db));
        Assert.Equal("flat", gw.FlattenStateToday().State);

        // THE PLATFORM CONTRADICTS HIM, beside his claim, from another gateway over the same store.
        var claim = gw.Requests.Get(lost.RequestId)!.LastError;
        gw.Requests.MarkNeedsReconciliation(lost.RequestId,
            $"{claim} — but the platform then answered CANCELLED. That is not FILLED, and this record is flagged again until you have looked.");

        await Passes(gw, clock, 2);

        var row = gw.Requests.Get(lost.RequestId)!;
        log.WriteLine($"his row               : {row.State} flagged={row.NeedsReconciliation} — {row.LastError}");
        log.WriteLine($"app rows              : {string.Join(" | ", AppRows(db))}");
        Assert.True(row.NeedsReconciliation);
        Assert.Equal(ExecutionState.FILLED, row.State);
        Assert.True(gw.HasUnconfirmedWork());

        var breach = gw.DayClosed(account)!;
        clock.MoveTo(LossReopen.EligibleAt(breach.ConfirmedAt, TimeSpan.FromHours(24)) + TimeSpan.FromMinutes(1));
        conn.Broker.PriceOffset = 0m;
        var reopen = await gw.LossWatchAsync();
        log.WriteLine($"past eligibility      : reopened [{string.Join(", ", reopen.Reopened)}]; held by {gw.ReopenReading().Held}");
        Assert.Empty(reopen.Reopened);
        Assert.NotNull(gw.DayClosed(account));
        Assert.True(gw.Requests.Get(lost.RequestId)!.NeedsReconciliation);
    }
}

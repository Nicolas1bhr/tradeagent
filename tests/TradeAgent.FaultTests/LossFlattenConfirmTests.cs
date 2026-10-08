using System.Diagnostics;
using Microsoft.Data.Sqlite;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// THE APP'S OWN CLOSES ARE CHARGED FOR THE PLATFORM, AND A LOST ANSWER IS ASKED OF THE PLATFORM'S
/// HISTORY BEFORE IT IS LEFT FOR THE OWNER (<c>U-flatten-confirm</c>).
///
/// <para>Two debts <c>U-fix-loss-reopen</c> recorded as owed. The data-loss exit (<c>U-flatten-3</c>)
/// still charged its own store writes to its two-second budget — the root cause that unit fixed for
/// the budget's flatten, left standing on the other app-owned close. And a budget close whose answer
/// was lost stayed flagged for ever, even where the platform's order history could say exactly what
/// became of it, over a closed day TradeAgent could have closed.</para>
///
/// <para>Every test here runs on <c>LossFlattenOwedTests</c>' fixture: the connector's real two-second
/// emergency budget and a coherent clock. Every verdict is the book, a row or a count at the wire —
/// "nothing was sent" is <see cref="RecordingConnector.Closes"/>, not a claim about intentions.</para>
/// </summary>
public class LossFlattenConfirmTests(ITestOutputHelper log)
{
    sealed class TestClock(DateTimeOffset at) : TimeProvider
    {
        DateTimeOffset _now = at;
        public override DateTimeOffset GetUtcNow() => _now;

        // A COHERENT CLOCK, as LossFlattenOwedTests keeps one: the monotone half moves with the wall half.
        public override long GetTimestamp() => _now.UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan by) => _now += by;
        public void MoveTo(DateTimeOffset to) => _now = to;
    }

    static readonly DateTimeOffset Noon = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Tick = TimeSpan.FromSeconds(20);
    static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    /// <summary>The absence grace every gateway here runs with: the product's own default.</summary>
    static readonly TimeSpan Grace = new GatewayOptions().AbsenceGrace;

    /// <summary>A feed that has stopped: every quote is older than <see cref="GatewayOptions.MaxQuoteAge"/>.</summary>
    static readonly TimeSpan Silent = TimeSpan.FromMinutes(10);

    static async Task<(TradingGateway Gw, RecordingConnector Conn, Database Db, TestClock Clock)> Ready(
        Action<TradeAgentSettings>? settings = null)
    {
        var clock = new TestClock(Noon);
        var db = TestEnv.NewDb();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker())
        {
            QuoteClock = clock,
            EmergencyBudget = Budget
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
            settings?.Invoke(s);
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

    static ValuationExitRecord? Exit(Database db, RecordingConnector conn, string symbol) =>
        db.KvStartingWith($"{ValuationLoss.ExitPrefix}{ValuationLoss.Scope(conn.Id, conn.Broker.AccountId)}:{symbol}:")
            .Select(r => Json.Read<ValuationExitRecord>(r.Value))
            .FirstOrDefault();

    /// <summary>
    /// A SECOND WRITER, armed at the first platform call made inside an operation deadline: another
    /// connection takes the database's write lock from inside that call and holds it for
    /// <paramref name="hold"/>, so the operation's next commit waits it out. It is the slow Windows
    /// disk reproduced without a hook in the product — <c>LossFlattenOwedTests</c>' own instrument.
    /// </summary>
    sealed class SecondWriter(string file, TimeSpan hold)
    {
        public Task? Holder { get; private set; }
        public Stopwatch HeldFor { get; } = new();

        public async Task Seam(RecordingConnector.HeldCall _)
        {
            if (Holder is not null || RiskReducingScope.DeadlineAt is null) return;
            var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Holder = Task.Run(async () =>
            {
                using var other = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = file, Pooling = false
                }.ToString());
                other.Open();
                using (var begin = other.CreateCommand()) { begin.CommandText = "BEGIN IMMEDIATE;"; begin.ExecuteNonQuery(); }
                HeldFor.Restart();
                locked.SetResult();
                await Task.Delay(hold);
                using (var commit = other.CreateCommand()) { commit.CommandText = "COMMIT;"; commit.ExecuteNonQuery(); }
                HeldFor.Stop();
            });
            await locked.Task;
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

    /// <summary>The app's own budget rows that still refuse order flow: flagged.</summary>
    static List<string> FlaggedAppRows(Database db)
    {
        using var c = db.Cmd("SELECT request_id, execution_state FROM execution_request "
                             + "WHERE request_id LIKE 'op-budget-%' AND needs_reconciliation=1 ORDER BY created_at");
        using var r = c.ExecuteReader();
        var rows = new List<string>();
        while (r.Read()) rows.Add($"{r.GetString(0)} {r.GetString(1)}");
        return rows;
    }

    /// <summary>Every close the app put on the broker's book, in the order it arrived.</summary>
    static List<OrderInfo> AppCloses(RecordingConnector conn) =>
        [.. conn.Broker.Orders.Where(o => o.ClientOrderId?.StartsWith("TA-op-budget-close-", StringComparison.Ordinal) == true)];

    /// <summary>The confirms written so far, by their family's prefix: there is at most one per breach.</summary>
    static int Confirms(Database db) => db.KvStartingWith("loss_flatten_confirm:").Count;

    /// <summary>
    /// A confirmed daily breach whose flatten put ONE close on the wire and lost its answer — the fault
    /// armed by the caller — on the health pass every host runs: the first sighting, then the
    /// confirming pass, whose watch flattens at the end of it. Answers the lost close's row id.
    /// </summary>
    static async Task<string> BreachWithALostClose(TradingGateway gw, RecordingConnector conn, TestClock clock)
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
        return leg.RequestId;
    }

    // ---------------------------------------------------------------- item 1

    /// <summary>
    /// (vi) THE DATA-LOSS EXIT ON A SLOW WINDOWS PC: ITS OWN RECORDS TAKE LONGER TO WRITE THAN ITS
    /// WHOLE BUDGET, AND ITS CLOSE STILL GOES OUT (item 1).
    ///
    /// <para>The exit is <c>U-flatten-2</c>'s mechanics under its own reason, and until this unit it
    /// opened <c>RiskReducingScope.Begin(budget)</c>: every write-ahead commit it makes before its
    /// close was charged to the platform's two seconds. Here a second writer holds the store for a
    /// second longer than the whole budget from inside the exit's first platform read, so its next
    /// commit waits it out. Before: the deadline passed inside the commit, the position read after it
    /// was refused before the wire, and the position nobody could value stayed open with the exit
    /// written once, final. After: the store's time is not the platform's, and the close goes out.</para>
    /// </summary>
    [Fact]
    public async Task The_data_loss_exits_own_records_slower_than_its_whole_budget_still_send_its_close()
    {
        var hold = Budget + TimeSpan.FromSeconds(1);
        var (gw, conn, db, clock) = await Ready(s => s.Risk.ValuationLossExitMinutes = 1m);
        using var _1 = db;
        await using var _2 = gw;

        await gw.PlaceAsync(new AgentContext("a"), "unvaluable-open", TestEnv.Buy("ES"));

        // VALUED FIRST, then silent: the episode starts on the first silent pass and its precautionary
        // cancel runs then, before the second writer is armed.
        await Passes(gw, clock, 1);
        conn.Faults.QuoteAge = Silent;
        await Passes(gw, clock, 3);
        Assert.Equal(1m, Held(conn, "ES"));
        Assert.Null(Exit(db, conn, "ES"));
        Assert.Equal(0, conn.Closes);

        // THE PASS THAT REACHES THE BOUND, with the second writer armed at the exit's first platform
        // read inside its deadline.
        var writer = new SecondWriter(db.Connection.DataSource, hold);
        conn.Seam = writer.Seam;
        var pass = Stopwatch.StartNew();
        await Passes(gw, clock, 1);
        pass.Stop();
        if (writer.Holder is not null) await writer.Holder;

        var exit = Exit(db, conn, "ES");
        log.WriteLine($"store held by another : {writer.HeldFor.ElapsedMilliseconds} ms against a {Budget.TotalMilliseconds:0} ms budget");
        log.WriteLine($"the exit's pass       : {pass.ElapsedMilliseconds} ms");
        log.WriteLine($"closes on the wire    : {conn.Closes}");
        log.WriteLine($"position              : ES {Held(conn, "ES")}");
        log.WriteLine($"exit                  : {(exit is null ? "no record" : $"flat={exit.Flat} — {exit.Why}")}");

        // THE STALL WAS REAL AND IT WAS INSIDE THE EXIT: longer than the whole budget.
        Assert.NotNull(writer.Holder);
        Assert.True(writer.HeldFor.Elapsed >= Budget, $"the store was held {writer.HeldFor.ElapsedMilliseconds} ms");
        Assert.True(pass.Elapsed >= Budget, $"the exit's pass took {pass.ElapsedMilliseconds} ms");

        // AND THE CLOSE WENT OUT ANYWAY, ONCE, UNDER ITS OWN REASON, AND THE BOOK READS FLAT.
        Assert.NotNull(exit);
        Assert.Equal(ValuationLoss.Reason, exit.Reason);
        Assert.Equal(1, conn.Closes);
        Assert.Equal(0m, Held(conn, "ES"));
        Assert.True(exit.Flat);
        Assert.Null(gw.DayClosed(conn.Broker.AccountId));
    }

    // ---------------------------------------------------------------- item 2

    /// <summary>
    /// (i) THE PLATFORM TOOK THE CLOSE AND FILLED IT, AND THE ANSWER WAS LOST: CONFIRMED FROM ITS ORDER
    /// HISTORY, FLAT, AND NOTHING MORE IS SENT (item 2).
    ///
    /// <para>The book is flat and TradeAgent is holding a close it never got an answer for. Until this
    /// unit that was final: the outcome said "cannot confirm", the row stayed flagged and every order
    /// stayed refused until a person read a record the platform could have answered. Now the next pass
    /// asks the platform's history for the close under its own id, finds it FILLED, reads the book back
    /// flat, writes that down once and unflags the app's own rows. One close on the wire, never two — and
    /// the closure stands: a confirm ends the flatten's doubt, not the closure.</para>
    /// </summary>
    [Fact]
    public async Task A_lost_close_the_platform_filled_is_confirmed_from_its_history_and_nothing_more_is_sent()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        await using var _2 = gw;
        var account = conn.Broker.AccountId;

        await gw.PlaceAsync(new AgentContext("a"), "lost-filled-open", TestEnv.Buy("ES"));
        conn.Faults.DropAfterBrokerAccept = 1;
        var lost = await BreachWithALostClose(gw, conn, clock);
        Assert.Equal(0m, Held(conn, "ES"));
        Assert.True(gw.HasUnconfirmedWork());

        await Passes(gw, clock, 1);

        var state = gw.FlattenStateToday();
        log.WriteLine($"closes on the wire    : {conn.Closes}, position ES {Held(conn, "ES")}");
        log.WriteLine($"app rows              : {string.Join(" | ", AppRows(db))}");
        log.WriteLine($"dashboard             : {state.State} — {state.Why}");

        Assert.Equal(ExecutionState.FILLED, gw.Requests.Get(lost)!.State);
        Assert.Equal("flat", state.State);
        Assert.Contains("ORDER HISTORY", state.Why!, StringComparison.Ordinal);
        Assert.Equal(1, conn.Closes);
        Assert.Equal(0m, Held(conn, "ES"));
        Assert.Empty(FlaggedAppRows(db));
        Assert.False(gw.HasUnconfirmedWork());
        Assert.Equal(1, Confirms(db));

        // THE FIRST OUTCOME IS UNTOUCHED — it was true when it was written — AND THE CLOSURE STANDS.
        Assert.False(gw.FlattenToday(account)!.Flat);
        Assert.NotNull(gw.DayClosed(account));

        // AND LATER PASSES SEND NOTHING AND WRITE NOTHING MORE.
        await Passes(gw, clock, 2);
        Assert.Equal(1, conn.Closes);
        Assert.Equal(1, Confirms(db));
        Assert.Equal("flat", gw.FlattenStateToday().State);
    }

    /// <summary>
    /// (ii) A CLOSE THE PLATFORM NEVER SAW, ON A PLATFORM WHOSE CLOSES CARRY THE ID THEY ARE GIVEN:
    /// NOTHING INSIDE THE GRACE, THEN SETTLED AS NEVER SENT, AND THE BOOK CLOSED AGAIN — ONCE, UNDER A
    /// NEW ID (<c>U-flatten-absence</c>).
    ///
    /// <para>The connection died before the broker saw the close, so the platform lists no order and
    /// no fill under its id. On the simulator that IS an answer: its close goes out under the id it is
    /// handed, onto the order and onto every fill, and its history is complete — so a history read that
    /// answered and holds nothing under that id, once the close can no longer be on its way there, says
    /// it never reached the platform. Until this unit that stayed flagged for ever (the guard below
    /// asserted it in a case called "plain"), over a book the budget had closed and nothing had
    /// flattened.</para>
    ///
    /// <para>Absence counts from the moment the close could last have been on its way —
    /// <c>ReconcileAsync</c>'s own bound, the dispatch plus <see cref="TradingGateway.DispatchStrandedAfter"/>,
    /// because nothing holds a press leg's dispatch — and only past the grace after it. One second short
    /// of that nothing is decided, written or sent. Past it the close is CANCELLED, "never reached the
    /// platform"; the book still reads open, so the confirm closes it again: one more close, under a
    /// new id, filled, and the book flat. Later passes send nothing.</para>
    /// </summary>
    [Fact]
    public async Task A_lost_close_the_platform_never_saw_is_settled_as_never_sent_past_the_grace_and_closed_again_once()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        await using var _2 = gw;
        var account = conn.Broker.AccountId;

        await gw.PlaceAsync(new AgentContext("a"), "never-seen-open", TestEnv.Buy("ES"));
        conn.Faults.DropBeforeBrokerAccept = 1;
        var lost = await BreachWithALostClose(gw, conn, clock);
        var row = gw.Requests.Get(lost)!;
        Assert.Equal(0, conn.Broker.CountByClientOrderId(row.ClientOrderId));
        Assert.Equal(1m, Held(conn, "ES"));

        // ONE SECOND SHORT OF THE GRACE: nothing decided, nothing written, nothing sent.
        var countsFrom = row.DispatchedAt!.Value + gw.DispatchStrandedAfter;
        clock.MoveTo(countsFrom + Grace - TimeSpan.FromSeconds(1));
        await gw.RefreshHealthAsync();

        var inside = gw.Requests.Get(lost)!;
        log.WriteLine($"inside the grace      : {inside.State} flagged={inside.NeedsReconciliation}, closes {conn.Closes}, "
                      + $"{gw.FlattenStateToday().State}");
        Assert.Equal(ExecutionState.UNKNOWN, inside.State);
        Assert.True(inside.NeedsReconciliation);
        Assert.Equal(1, conn.Closes);
        Assert.Equal(0, Confirms(db));
        Assert.Equal("unresolved", gw.FlattenStateToday().State);

        // PAST IT.
        await Passes(gw, clock, 1);

        var settled = gw.Requests.Get(lost)!;
        var closes = AppCloses(conn);
        var state = gw.FlattenStateToday();
        log.WriteLine($"closes on the wire    : {conn.Closes}, position ES {Held(conn, "ES")}");
        log.WriteLine($"app closes at the book: [{string.Join(" | ", closes.Select(o => $"{o.ClientOrderId} {o.Side} {o.Quantity} {o.State}"))}]");
        log.WriteLine($"lost close            : {settled.State} flagged={settled.NeedsReconciliation} — {settled.LastError}");
        log.WriteLine($"app rows              : {string.Join(" | ", AppRows(db))}");
        log.WriteLine($"dashboard             : {state.State} — {state.Why}");

        Assert.Equal(ExecutionState.CANCELLED, settled.State);
        Assert.False(settled.NeedsReconciliation);
        Assert.Contains("never reached the platform", settled.LastError!, StringComparison.Ordinal);
        Assert.Equal(2, conn.Closes);
        var again = Assert.Single(closes);
        Assert.NotEqual(row.ClientOrderId, again.ClientOrderId);
        Assert.Equal(ExecutionState.FILLED, again.State);
        Assert.Equal(0, conn.Broker.CountByClientOrderId(row.ClientOrderId));
        Assert.Equal(1, conn.Broker.CountByClientOrderId(again.ClientOrderId!));
        Assert.Equal(0m, Held(conn, "ES"));
        Assert.Equal("flat", state.State);
        Assert.Contains("CLOSED AGAIN", state.Why!, StringComparison.Ordinal);
        Assert.Empty(FlaggedAppRows(db));
        Assert.False(gw.HasUnconfirmedWork());
        Assert.Equal(1, Confirms(db));

        // THE FIRST OUTCOME IS UNTOUCHED AND THE CLOSURE STANDS.
        Assert.False(gw.FlattenToday(account)!.Flat);
        Assert.NotNull(gw.DayClosed(account));

        // AND NOTHING MORE: later passes send nothing and write nothing.
        await Passes(gw, clock, 2);
        Assert.Equal(2, conn.Closes);
        Assert.Equal(1, Confirms(db));
        Assert.Equal("flat", gw.FlattenStateToday().State);
    }

    /// <summary>
    /// (iii), (iv), (vii) WHERE ABSENCE IS NOT PROOF, A CLOSE THE PLATFORM NEVER SAW DECIDES NOTHING —
    /// ON (ii)'S OWN DRIVE, PAST THE GRACE BY MINUTES (the guards: green before this unit and after it).
    ///
    /// <para>The same close, lost the same way. (iii) The connector withdraws its claim to a provable
    /// history. (iv) A history read that carries a <c>since</c> throws instead of answering, on a
    /// connector that still claims one. (vii) The connector does not claim that its closes carry the id
    /// they are given — ATAS's case, whose close carries our id only as a label written after the fact,
    /// so its absence proves nothing — which is what this test's "plain" case asserted of every
    /// connector before <c>U-flatten-absence</c>. In each, the row stays UNKNOWN and flagged, nothing is
    /// settled, nothing is written, nothing more goes on the wire, and the dashboard says
    /// unresolved.</para>
    /// </summary>
    [Theory]
    [InlineData("history hidden")]
    [InlineData("history read throws")]
    [InlineData("closes do not carry the id")]
    public async Task A_lost_close_the_platform_never_saw_decides_nothing_past_the_grace(string history)
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        await using var _2 = gw;
        var account = conn.Broker.AccountId;

        await gw.PlaceAsync(new AgentContext("a"), "never-seen-open", TestEnv.Buy("ES"));
        conn.Faults.DropBeforeBrokerAccept = 1;
        var lost = await BreachWithALostClose(gw, conn, clock);

        // EACH CASE MOVES ONE THING, and says which: the history's claim, the read, or the closes' claim.
        if (history == "history hidden") conn.Faults.HideOrderHistory = true;
        if (history == "history read throws")
            conn.HistoryThrows = new ConnectorTransportException("this platform cannot show its order history back that far");
        if (history == "closes do not carry the id") conn.ClosesCarryTheId = false;
        var caps = conn.Capabilities;
        Assert.Equal(history != "history hidden", caps.ReconciliationProvable);
        Assert.Equal(history != "closes do not carry the id", caps.ClosesCarryClientOrderId);

        // PAST THE GRACE, by minutes: the absence grace is fifteen seconds after the dispatch bound.
        await Passes(gw, clock, 9);

        var row = gw.Requests.Get(lost)!;
        var state = gw.FlattenStateToday();
        log.WriteLine($"[{history}]");
        log.WriteLine($"closes on the wire    : {conn.Closes}, position ES {Held(conn, "ES")}");
        log.WriteLine($"lost close            : {row.State} flagged={row.NeedsReconciliation}");
        log.WriteLine($"dashboard             : {state.State} — {state.Why}");

        Assert.Equal(1, conn.Closes);
        Assert.Equal(ExecutionState.UNKNOWN, row.State);
        Assert.True(row.NeedsReconciliation);
        Assert.True(gw.HasUnconfirmedWork());
        Assert.Equal("unresolved", state.State);
        Assert.Equal(0, Confirms(db));
        Assert.Equal(1m, Held(conn, "ES"));
        Assert.NotNull(gw.DayClosed(account));
    }

    /// <summary>
    /// THE SIMULATOR'S CLAIM, EARNED: ITS CLOSE CARRIES THE ID IT IS HANDED, ONTO THE ORDER AND ITS FILL
    /// (<c>U-flatten-absence</c>).
    ///
    /// <para>(ii) reads "no order and no fill under the close's id" as "it never reached the platform",
    /// and that is true only of a connector whose close goes out under the id it was handed.
    /// <see cref="ConnectorCapabilities.ClosesCarryClientOrderId"/> says so, and this is the simulator
    /// proving it through the two history reads the confirm asks: the close on the book and its fill
    /// carry exactly that id, once.</para>
    /// </summary>
    [Fact]
    public async Task The_simulators_close_carries_the_id_it_is_handed_onto_the_order_and_its_fill()
    {
        await using var fake = new FakeConnector(new FakeBroker());
        await fake.ConnectAsync();
        var account = fake.Broker.AccountId;
        var since = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5);

        await fake.PlaceOrderAsync(new PlaceOrderCommand("TA-carried-open", account, "ES", OrderSide.Buy,
            OrderType.Market, 2m, null, null, TimeInForce.Day, null));
        const string id = "TA-op-budget-close-0123456789abcdef-0";
        var close = await fake.ClosePositionAsync(account, "ES", id);

        var orders = await fake.GetOrdersAsync(account, true, since);
        var fills = await fake.GetExecutionsAsync(account, since);
        log.WriteLine($"the close             : {close?.ClientOrderId} {close?.Side} {close?.Quantity} {close?.State}");
        log.WriteLine($"history               : [{string.Join(" | ", orders.Select(o => $"{o.ClientOrderId} {o.Side} {o.Quantity} {o.State}"))}]");
        log.WriteLine($"fills                 : [{string.Join(" | ", fills.Select(f => $"{f.ClientOrderId} {f.Side} {f.Quantity}"))}]");

        Assert.True(fake.Capabilities.ClosesCarryClientOrderId);
        Assert.NotNull(close);
        Assert.Equal(id, close.ClientOrderId);
        Assert.Equal(OrderSide.Sell, close.Side);
        Assert.Equal(2m, close.Quantity);
        Assert.Equal(id, Assert.Single(orders, o => o.ConnectorOrderId == close.ConnectorOrderId).ClientOrderId);
        Assert.Equal(id, Assert.Single(fills, f => f.ConnectorOrderId == close.ConnectorOrderId).ClientOrderId);
        Assert.Equal(1, fake.Broker.CountByClientOrderId(id));
        Assert.Empty(fake.Broker.Positions);
    }

    /// <summary>
    /// (v) A CLOSE STILL WORKING AT THE PLATFORM IS LEFT ALONE — NOT SETTLED, NOT CANCELLED, NOT SENT
    /// OVER — AND ONCE IT FILLS IT IS CONFIRMED EXACTLY AS (i) (item 2, and the mutant).
    ///
    /// <para>The platform took the close and it rests there, untouched or half filled, and the answer
    /// was lost. A live close is not an answer: it can still fill, so counting it decided would settle
    /// a live order and close again on top of it — a second close on the wire, which is the
    /// long-2-becomes-short-2 failure arrived at from the history's side. So pass after pass it is
    /// asked and left exactly where it is. Then price arrives, it fills, and the next pass confirms it
    /// and reads the book flat.</para>
    ///
    /// <para><b>The mutant this watches:</b> a close found WORKING counted as decided. The confirm then
    /// reads the book open and closes again: the re-close cancels the resting close and sends a second
    /// one, and <c>Closes</c> reads 2 here where it must read 1.</para>
    /// </summary>
    [Theory]
    [InlineData(FillBehaviour.LeaveWorking)]
    [InlineData(FillBehaviour.PartialFill)]
    public async Task A_lost_close_still_working_is_left_alone_and_confirmed_once_it_fills(FillBehaviour resting)
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        await using var _2 = gw;
        var account = conn.Broker.AccountId;

        await gw.PlaceAsync(new AgentContext("a"), "working-open", TestEnv.Buy("ES", 2m));
        conn.Faults.Fill = resting;
        conn.Faults.DropAfterBrokerAccept = 1;
        var lost = await BreachWithALostClose(gw, conn, clock);
        var close = Assert.Single(AppCloses(conn));
        var cancels = conn.Cancels;

        await Passes(gw, clock, 3);

        var resting1 = conn.Broker.Orders.Single(o => o.ConnectorOrderId == close.ConnectorOrderId);
        log.WriteLine($"[{resting}]");
        log.WriteLine($"the close at the book : {resting1.Side} {resting1.Quantity} {resting1.State} (filled {resting1.FilledQuantity})");
        log.WriteLine($"closes on the wire    : {conn.Closes}, cancels {cancels} -> {conn.Cancels}, position ES {Held(conn, "ES")}");
        log.WriteLine($"app rows              : {string.Join(" | ", AppRows(db))}");

        // LEFT ALONE: the close rests where it was, its row still UNKNOWN and flagged, nothing written.
        Assert.Equal(1, conn.Closes);
        Assert.Equal(cancels, conn.Cancels);
        Assert.False(OrderStateMachine.IsTerminal(resting1.State));
        Assert.Equal(ExecutionState.UNKNOWN, gw.Requests.Get(lost)!.State);
        Assert.True(gw.Requests.Get(lost)!.NeedsReconciliation);
        Assert.Equal(0, Confirms(db));
        Assert.Equal("unresolved", gw.FlattenStateToday().State);

        // PRICE ARRIVES AND IT FILLS — and the next pass confirms it, exactly as (i).
        conn.Broker.FillWorking(close.ConnectorOrderId);
        await Passes(gw, clock, 1);

        var state = gw.FlattenStateToday();
        log.WriteLine($"after it fills        : closes {conn.Closes}, ES {Held(conn, "ES")}, {state.State} — {state.Why}");
        Assert.Equal(ExecutionState.FILLED, gw.Requests.Get(lost)!.State);
        Assert.Equal("flat", state.State);
        Assert.Equal(1, conn.Closes);
        Assert.Equal(0m, Held(conn, "ES"));
        Assert.Empty(FlaggedAppRows(db));
        Assert.False(gw.HasUnconfirmedWork());
        Assert.NotNull(gw.DayClosed(account));
    }

    /// <summary>
    /// (viii) THE PLATFORM REFUSED THE CLOSE AND THE ANSWER WAS LOST: CONFIRMED REJECTED FROM ITS
    /// HISTORY, THE BOOK STILL OPEN, AND IT IS CLOSED AGAIN — ONCE, UNDER A NEW ID — AND READS FLAT
    /// (item 2).
    ///
    /// <para>The shared close path records a refused close as UNKNOWN (it cannot tell a refusal from a
    /// lost answer in that arm, and the safe word is the unknown one), so until this unit the position
    /// stayed open behind a closed day for as long as nobody came. The history holds the close
    /// REJECTED: decided, and it closed nothing. The book still reads open, so the confirm says
    /// "closing again" and the sweep — nothing else being unconfirmed — runs the SAME flatten under a
    /// fresh nonce: one more close, re-read at the wire, and the book flat. The first outcome stays
    /// exactly as it was written.</para>
    /// </summary>
    [Fact]
    public async Task A_lost_close_the_platform_refused_is_confirmed_and_the_book_is_closed_again_once()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        await using var _2 = gw;
        var account = conn.Broker.AccountId;

        await gw.PlaceAsync(new AgentContext("a"), "refused-open", TestEnv.Buy("ES"));
        conn.Faults.RejectNext = 1;
        var lost = await BreachWithALostClose(gw, conn, clock);
        Assert.Equal(1m, Held(conn, "ES"));

        await Passes(gw, clock, 1);

        var closes = AppCloses(conn);
        var state = gw.FlattenStateToday();
        log.WriteLine($"closes on the wire    : {conn.Closes}, position ES {Held(conn, "ES")}");
        log.WriteLine($"app closes at the book: [{string.Join(" | ", closes.Select(o => $"{o.ClientOrderId} {o.Side} {o.Quantity} {o.State}"))}]");
        log.WriteLine($"app rows              : {string.Join(" | ", AppRows(db))}");
        log.WriteLine($"dashboard             : {state.State} — {state.Why}");

        Assert.Equal(ExecutionState.REJECTED, gw.Requests.Get(lost)!.State);
        Assert.Equal(2, conn.Closes);
        Assert.Equal(2, closes.Count);
        Assert.Equal(ExecutionState.REJECTED, closes[0].State);
        Assert.Equal(ExecutionState.FILLED, closes[1].State);
        Assert.NotEqual(closes[0].ClientOrderId, closes[1].ClientOrderId);
        Assert.All(closes, o => Assert.Equal(1, conn.Broker.CountByClientOrderId(o.ClientOrderId!)));
        Assert.Equal(0m, Held(conn, "ES"));
        Assert.Equal("flat", state.State);
        Assert.Contains("CLOSED AGAIN", state.Why!, StringComparison.Ordinal);
        Assert.Empty(FlaggedAppRows(db));
        Assert.False(gw.HasUnconfirmedWork());
        Assert.False(gw.FlattenToday(account)!.Flat);
        Assert.NotNull(gw.DayClosed(account));

        // AND NOTHING MORE: later passes send nothing.
        await Passes(gw, clock, 2);
        Assert.Equal(2, conn.Closes);
        Assert.Equal(1, Confirms(db));
    }

    /// <summary>
    /// A LOST ANSWER TO THE CLOSING AGAIN THAT NOTHING CAN ANSWER STAYS FOR THE OWNER (item 2; its claim moved by
    /// <c>U-valuation-close-confirm</c>).
    ///
    /// <para>The first close was refused and the confirm closes again; that second close is taken by
    /// the platform and ITS answer is lost too. This test used to assert that it then stayed flagged
    /// whatever the platform could say — one confirm per breach — and that was the defect: the closing
    /// again is a close generation of its own, and its own confirm now decides it from the platform's
    /// history exactly as the first's (<c>CloseGenerationConfirmTests</c> (e)). What stays true, and is
    /// asserted here, is the case where nothing can answer it: the platform cannot show its history and
    /// the owner has not answered. Then no confirm is written for it, the second close's row stays UNKNOWN
    /// and flagged, order flow stays paused, the dashboard says unresolved, and pass after pass nothing
    /// more is sent.</para>
    /// </summary>
    [Fact]
    public async Task A_lost_answer_to_the_closing_again_stays_for_the_owner()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        await using var _2 = gw;
        var account = conn.Broker.AccountId;

        await gw.PlaceAsync(new AgentContext("a"), "twice-lost-open", TestEnv.Buy("ES"));
        conn.Faults.RejectNext = 1;
        var lost = await BreachWithALostClose(gw, conn, clock);

        // THE CLOSING AGAIN'S ANSWER IS LOST TOO — and from then on the platform cannot show its history.
        conn.Faults.DropAfterBrokerAccept = 1;
        await Passes(gw, clock, 1);
        Assert.Equal(2, conn.Closes);
        conn.Faults.HideOrderHistory = true;
        Assert.False(conn.Capabilities.ReconciliationProvable);

        // PAST THE GRACE BY MINUTES, AND THE OWNER HAS NOT ANSWERED.
        await Passes(gw, clock, 9);

        var again = AppCloses(conn).Last();
        var row = gw.Requests.GetByClientOrderId(again.ClientOrderId!)!;
        var state = gw.FlattenStateToday();
        log.WriteLine($"closes on the wire    : {conn.Closes}, position ES {Held(conn, "ES")}");
        log.WriteLine($"app rows              : {string.Join(" | ", AppRows(db))}");
        log.WriteLine($"dashboard             : {state.State} — {state.Why}");

        Assert.Equal(ExecutionState.REJECTED, gw.Requests.Get(lost)!.State);
        Assert.Equal(2, conn.Closes);
        Assert.Equal(ExecutionState.UNKNOWN, row.State);
        Assert.True(row.NeedsReconciliation);
        Assert.True(gw.HasUnconfirmedWork());
        Assert.Equal(1, Confirms(db));
        Assert.Empty(db.KvStartingWith("loss_flatten_again_confirm:"));
        Assert.Equal("unresolved", state.State);
        Assert.NotNull(gw.DayClosed(account));
    }

    /// <summary>
    /// A SYMBOL'S CLOSING AGAIN THAT THE DAY'S OWN FLATTEN SUBSUMED IS NOT "CLOSING AGAIN", AND DOES NOT
    /// HOLD THE SYMBOL'S CLOSURE FOR EVER (item 2, the one accessor).
    ///
    /// <para>ES breaches its per-trade budget and its close is refused with the answer lost; the day's
    /// budget goes through a pull later. On the next pass the confirm finds the ES close REJECTED and the
    /// book still open, so the symbol is owed a closing again — and in the same pass the day's breach is
    /// confirmed and the day's own flatten closes ES. A symbol's flatten is subsumed by the day's, so the
    /// closing again is declined and never runs. Its word must say nothing then, exactly as a subsumed
    /// first attempt leaves none: otherwise the symbol's closure would be held for ever by a sentence —
    /// "closing again what is still open" — that never comes true, over a book that reads flat.</para>
    /// </summary>
    [Fact]
    public async Task A_symbols_closing_again_the_days_flatten_subsumed_does_not_hold_the_symbol_for_ever()
    {
        var (gw, conn, db, clock) = await Ready(s =>
        {
            s.Risk.MaxDailyLoss = 1_500m;
            s.Risk.MaxLossPerTrade = 500m;
        });
        using var _1 = db;
        await using var _2 = gw;
        var account = conn.Broker.AccountId;

        await gw.PlaceAsync(new AgentContext("a"), "subsumed-open", TestEnv.Buy("ES"));
        conn.Faults.RejectNext = 1;

        // ES THROUGH ITS OWN BUDGET FIRST, THEN THE DAY'S: ES is confirmed and flattened (the close
        // refused, its answer lost) on the pull the day is first seen on.
        conn.Broker.PriceOffset = -20m;
        await Passes(gw, clock, 1);
        conn.Broker.PriceOffset = -40m;
        await Passes(gw, clock, 1);
        Assert.NotNull(gw.SymbolClosed(account, "ES"));
        Assert.Null(gw.DayClosed(account));
        Assert.Equal(1, conn.Closes);

        // THE CONFIRM, THE DAY'S BREACH AND THE DAY'S FLATTEN, IN ONE PASS.
        await Passes(gw, clock, 1);

        var state = gw.FlattenStateToday();
        var symbol = gw.SymbolClosed(account, "ES")!;
        var day = gw.DayClosed(account)!;
        log.WriteLine($"closes on the wire    : {conn.Closes}, position ES {Held(conn, "ES")}");
        log.WriteLine($"app rows              : {string.Join(" | ", AppRows(db))}");
        log.WriteLine($"dashboard             : {state.State} — {state.Why}");

        Assert.Equal(2, conn.Closes);
        Assert.Equal(0m, Held(conn, "ES"));
        Assert.Equal(1, Confirms(db));
        Assert.Empty(db.KvStartingWith("loss_flatten_again:"));
        Assert.Equal("flat", state.State);
        Assert.DoesNotContain("CLOSING IT AGAIN", state.Why!, StringComparison.Ordinal);

        // AND BOTH CLOSURES LIFT ON THEIR RECEIPTS ONCE THEIR TIME HAS RUN, OVER A FLAT BOOK.
        var eligible = new[] { symbol, day }.Max(b => LossReopen.EligibleAt(b.ConfirmedAt, TimeSpan.FromHours(24)));
        clock.MoveTo(eligible + TimeSpan.FromMinutes(1));
        conn.Broker.PriceOffset = 0m;
        var reopen = await gw.LossWatchAsync();
        log.WriteLine($"reopened              : [{string.Join(", ", reopen.Reopened)}]");
        Assert.Equal(2, reopen.Reopened.Count);
        Assert.Null(gw.SymbolClosed(account, "ES"));
        Assert.Null(gw.DayClosed(account));
    }
}

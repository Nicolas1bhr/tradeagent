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
/// A CLOSED DAY NEVER COEXISTS SILENTLY WITH AN OPEN POSITION (<c>U-fix-loss-reopen</c>).
///
/// <para>Twice on windows-latest a confirmed breach closed the day and the book stayed open: once
/// the close was never sent (<c>LossWatchTests</c>, <c>conn.Closes</c> 0), once the reopen a day
/// later found ES still open (<c>LossHoldSurfacesTests</c>). One cause behind both: the app's own
/// flatten runs on the connector's two-second emergency budget, the budget is a real wall clock,
/// and the flatten's own write-ahead records are durable SQLite commits on that same clock — on a
/// slow disk they spent the whole budget before the first platform call, the platform call was
/// refused before the wire, and the flatten wrote its outcome ONCE, as final, over a book it had
/// never touched. Nothing ever tried again.</para>
///
/// <para>So two things change, and each has its own test here. The budget of the app's own flatten
/// is charged for the PLATFORM and not for the app's own disk — a slow Windows PC is where this runs.
/// And an attempt that put NOTHING on the wire is not an outcome: it is owed, the dashboard says so
/// in words, every later pass tries again, and nothing reopens until the book is flat. An attempt
/// that put something on the wire keeps U-flatten-2's rule exactly: it is never repeated over a row
/// nobody has reconciled.</para>
/// </summary>
public class LossFlattenOwedTests(ITestOutputHelper log)
{
    sealed class TestClock(DateTimeOffset at) : TimeProvider
    {
        DateTimeOffset _now = at;
        public override DateTimeOffset GetUtcNow() => _now;

        // A COHERENT CLOCK, as LossHoldSurfacesTests keeps one: the monotone half moves with the wall
        // half, so a step to the reopen instant is a day that passed rather than a clock somebody set.
        public override long GetTimestamp() => _now.UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan by) => _now += by;
        public void MoveTo(DateTimeOffset to) => _now = to;
    }

    static readonly DateTimeOffset Noon = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Tick = TimeSpan.FromSeconds(20);

    static async Task<(TradingGateway Gw, RecordingConnector Conn, Database Db, TestClock Clock)> Ready(
        TimeSpan emergencyBudget)
    {
        var clock = new TestClock(Noon);
        var db = TestEnv.NewDb();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker())
        {
            QuoteClock = clock,
            EmergencyBudget = emergencyBudget
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

    /// <summary>The app's own press rows that still refuse order flow: flagged, by kind.</summary>
    static List<string> FlaggedAppRows(Database db)
    {
        using var c = db.Cmd("SELECT request_id, execution_state FROM execution_request "
                             + "WHERE request_id LIKE 'op-budget-%' AND needs_reconciliation=1 ORDER BY created_at");
        using var r = c.ExecuteReader();
        var rows = new List<string>();
        while (r.Read()) rows.Add($"{r.GetString(0)} {r.GetString(1)}");
        return rows;
    }

    static List<string> AppRows(Database db)
    {
        using var c = db.Cmd("SELECT request_id, execution_state, needs_reconciliation, COALESCE(last_error,'') "
                             + "FROM execution_request WHERE request_id LIKE 'op-budget-%' ORDER BY created_at");
        using var r = c.ExecuteReader();
        var rows = new List<string>();
        while (r.Read()) rows.Add($"{r.GetString(0)} {r.GetString(1)} flagged={r.GetInt64(2)} '{r.GetString(3)}'");
        return rows;
    }

    /// <summary>
    /// A SLOW PLATFORM CALL, at the first call the flatten makes inside its own deadline: the caller is
    /// held there until that deadline has passed, so the simulator refuses the call before the wire —
    /// which is what a platform read that took the whole budget does. Every attempt while armed.
    ///
    /// <para>"The first call of an attempt" is the first one with time still left: once a deadline has
    /// passed it stays passed for the rest of that attempt — the store's time is given back to it, and
    /// that is never more than the time that has gone by — and the next attempt opens a fresh one.</para>
    /// </summary>
    static Func<RecordingConnector.HeldCall, Task> StallEachAttempt(Func<bool> armed, Action counted) =>
        async _ =>
        {
            if (!armed() || RiskReducingScope.DeadlineAt is not { } d || RiskReducingScope.LeftUntil(d) == TimeSpan.Zero) return;
            counted();
            while (RiskReducingScope.DeadlineAt is { } now && RiskReducingScope.LeftUntil(now) > TimeSpan.Zero)
                await Task.Delay(5);
        };

    /// <summary>
    /// THE SLOW WINDOWS PC (A): A FLATTEN WHOSE OWN RECORDS TAKE LONGER TO WRITE THAN THE WHOLE
    /// BUDGET STILL SENDS ITS CLOSE.
    ///
    /// <para>The disk is made slow the way a second writer makes it slow: another connection holds the
    /// database's write lock from inside the flatten's first platform read for a second longer than
    /// the entire two-second budget, so the flatten's next commit waits it out. That is the
    /// windows-latest picture — one synchronous=FULL commit has measured 2234 ms there, and both
    /// sightings' tests ran 23 s and 41 s on a fixture that takes 30 ms here — reproduced without a
    /// hook in the product. Nothing waits on the platform: every call below answers in microseconds.</para>
    ///
    /// <para>Before: the deadline passed inside the commit, the next read was refused before the wire,
    /// and the day closed with the book open. After: the time spent in the app's own store is not the
    /// platform's, so it is not charged to the platform's budget, and the close goes out.</para>
    /// </summary>
    [Fact]
    public async Task A_flatten_whose_own_records_are_slower_to_write_than_its_whole_budget_still_sends_its_close()
    {
        var budget = TimeSpan.FromSeconds(2);
        var hold = budget + TimeSpan.FromSeconds(1);
        var (gw, conn, db, clock) = await Ready(budget);
        using var _1 = db;
        await using var _2 = gw;
        var account = conn.Broker.AccountId;
        var file = db.Connection.DataSource;

        await gw.PlaceAsync(new AgentContext("a"), "slow-disk-open", TestEnv.Buy("ES"));
        conn.Broker.PriceOffset = -20m;
        clock.Advance(Tick);
        await gw.LossWatchAsync();

        // THE SECOND WRITER, armed at the flatten's first platform read inside its deadline.
        var heldFor = Stopwatch.StartNew();
        Task? holder = null;
        conn.Seam = async _ =>
        {
            if (holder is not null || RiskReducingScope.DeadlineAt is null) return;
            var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            holder = Task.Run(async () =>
            {
                using var other = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = file, Pooling = false
                }.ToString());
                other.Open();
                using (var begin = other.CreateCommand()) { begin.CommandText = "BEGIN IMMEDIATE;"; begin.ExecuteNonQuery(); }
                heldFor.Restart();
                locked.SetResult();
                await Task.Delay(hold);
                using (var commit = other.CreateCommand()) { commit.CommandText = "COMMIT;"; commit.ExecuteNonQuery(); }
                heldFor.Stop();
            });
            await locked.Task;
        };

        // THE CONFIRMING PULL, called by name so that ONE attempt is measured: the sweep a health
        // pass runs afterwards would try again, and this test is about the attempt itself.
        clock.Advance(Tick);
        var pass = Stopwatch.StartNew();
        var closing = await gw.LossWatchAsync();
        pass.Stop();
        if (holder is not null) await holder;

        var flatten = gw.FlattenToday(account);
        log.WriteLine($"closed                : [{string.Join(", ", closing.Closed)}]");
        log.WriteLine($"store held by another : {heldFor.ElapsedMilliseconds} ms against a {budget.TotalMilliseconds:0} ms budget");
        log.WriteLine($"the confirming pass   : {pass.ElapsedMilliseconds} ms");
        log.WriteLine($"closes on the wire    : {conn.Closes}");
        log.WriteLine($"position              : ES {Held(conn, "ES")}");
        log.WriteLine($"flatten               : {(flatten is null ? "no record" : $"flat={flatten.Flat} — {flatten.Why}")}");

        // THE STALL WAS REAL AND IT WAS INSIDE THE FLATTEN: longer than the whole budget.
        Assert.NotNull(holder);
        Assert.True(heldFor.Elapsed >= budget, $"the store was held {heldFor.ElapsedMilliseconds} ms");
        Assert.True(pass.Elapsed >= budget, $"the confirming pass took {pass.ElapsedMilliseconds} ms");
        Assert.NotNull(gw.DayClosed(account));

        // AND THE CLOSE WENT OUT ANYWAY, ONCE, AND THE BOOK READS FLAT.
        Assert.Equal(1, conn.Closes);
        Assert.Equal(0m, Held(conn, "ES"));
        Assert.NotNull(flatten);
        Assert.True(flatten.Flat);
    }

    /// <summary>
    /// AN ATTEMPT THAT SENT NOTHING IS OWED (B): THE DASHBOARD SAYS SO, AND THE NEXT PASS SENDS IT.
    ///
    /// <para>The platform's first read inside the flatten takes the whole budget, so the simulator
    /// refuses it before the wire and nothing at all reaches the platform — the sighting's own
    /// shape, "mutations before/after: 1/1". What used to follow was a record written once, as final,
    /// saying the flatten could not confirm the account was flat, and a flagged row that refused the
    /// sweep: nothing ever tried again. Now the attempt's rows are settled as not sent, behind the
    /// proof that nothing was dispatched; no outcome is written, because nothing happened; the owner
    /// is told in words that the positions are still open and that TradeAgent keeps trying; and the
    /// next health pass sends the close.</para>
    /// </summary>
    [Fact]
    public async Task A_flatten_that_sent_nothing_is_owed_in_words_and_the_next_pass_sends_it()
    {
        var (gw, conn, db, clock) = await Ready(TimeSpan.FromMilliseconds(500));
        using var _1 = db;
        await using var _2 = gw;
        var account = conn.Broker.AccountId;

        await gw.PlaceAsync(new AgentContext("a"), "owed-open", TestEnv.Buy("ES"));
        conn.Broker.PriceOffset = -20m;
        clock.Advance(Tick);
        await gw.RefreshHealthAsync();

        var stalls = 0;
        var armed = true;
        conn.Seam = StallEachAttempt(() => armed, () => stalls++);

        // THE CONFIRMING PULL, by name, so the sweep of a health pass does not run behind it: what is
        // read next is the state ONE attempt that sent nothing leaves.
        clock.Advance(Tick);
        var closing = await gw.LossWatchAsync();
        armed = false;

        var state = gw.FlattenStateToday();
        log.WriteLine($"closed                : [{string.Join(", ", closing.Closed)}] after {stalls} stalled attempt(s)");
        log.WriteLine($"closes on the wire    : {conn.Closes}, position ES {Held(conn, "ES")}");
        log.WriteLine($"outcome record        : {(gw.FlattenToday(account) is { } f ? $"flat={f.Flat}" : "none")}");
        log.WriteLine($"dashboard             : {state.State} — {state.Why}");
        log.WriteLine($"app rows              : {string.Join(" | ", AppRows(db))}");
        log.WriteLine($"unconfirmed work      : {gw.HasUnconfirmedWork()}");

        Assert.Equal(1, stalls);
        Assert.NotNull(gw.DayClosed(account));
        Assert.Equal(0, conn.Closes);
        Assert.Equal(1m, Held(conn, "ES"));

        // NOTHING HAPPENED, SO NO OUTCOME IS WRITTEN — and nothing of the attempt refuses the retry.
        Assert.Null(gw.FlattenToday(account));
        Assert.Empty(FlaggedAppRows(db));
        Assert.False(gw.HasUnconfirmedWork());

        // THE DASHBOARD SAYS SO IN WORDS: not flat, not closed, and trying again.
        Assert.Equal("unresolved", state.State);
        Assert.Contains("has NOT closed your open positions yet", state.Why!, StringComparison.Ordinal);
        Assert.Contains("tries again on every pass", state.Why!, StringComparison.Ordinal);
        var line = (await gw.LossTodayAsync()).ClosedLine();
        log.WriteLine($"closed line           : {line}");
        Assert.Contains("has NOT closed your open positions yet", line!, StringComparison.Ordinal);

        // THE NEXT PASS SENDS IT: one close, the book flat, the outcome written once and flat.
        clock.Advance(Tick);
        await gw.RefreshHealthAsync();

        var flatten = gw.FlattenToday(account);
        log.WriteLine($"after the next pass   : closes {conn.Closes}, ES {Held(conn, "ES")}, "
                      + $"{(flatten is null ? "no record" : $"flat={flatten.Flat} — {flatten.Why}")}");
        Assert.Equal(1, conn.Closes);
        Assert.Equal(0m, Held(conn, "ES"));
        Assert.NotNull(flatten);
        Assert.True(flatten.Flat);
        Assert.Equal("flat", gw.FlattenStateToday().State);

        // AND THE CLOSURE STANDS: the next order that could add risk is still refused off the record.
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "after-owed", TestEnv.Buy("NQ")));
        Assert.Equal(ErrorCode.LOSS_BUDGET_REACHED, denied.Code);
    }

    /// <summary>
    /// EVERY LATER PASS TRIES AGAIN, AND NOTHING REOPENS UNTIL THE BOOK IS FLAT (B, and the boundary).
    ///
    /// <para>The platform stays unable to answer inside the budget for several passes, across the
    /// instant the closure becomes eligible to lift. Each pass tries again and sends nothing; the
    /// scope stays closed, because the book is open; and the reopen comes only on the pass AFTER the
    /// one whose flatten finally reads flat — never early, never over an open book.</para>
    /// </summary>
    [Fact]
    public async Task Every_later_pass_tries_again_and_nothing_reopens_until_the_book_is_flat()
    {
        var (gw, conn, db, clock) = await Ready(TimeSpan.FromMilliseconds(500));
        using var _1 = db;
        await using var _2 = gw;
        var account = conn.Broker.AccountId;

        await gw.PlaceAsync(new AgentContext("a"), "persistent-open", TestEnv.Buy("ES"));
        conn.Broker.PriceOffset = -20m;
        clock.Advance(Tick);
        await gw.RefreshHealthAsync();

        var stalls = 0;
        var armed = true;
        conn.Seam = StallEachAttempt(() => armed, () => stalls++);

        clock.Advance(Tick);
        await gw.RefreshHealthAsync();
        var breach = gw.DayClosed(account);
        Assert.NotNull(breach);
        var afterConfirming = stalls;

        // PAST THE INSTANT THE CLOSURE MAY LIFT, with the platform still unable to answer in time.
        var eligible = LossReopen.EligibleAt(breach.ConfirmedAt, TimeSpan.FromHours(24));
        clock.MoveTo(eligible + TimeSpan.FromMinutes(1));
        var reopened = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            await gw.RefreshHealthAsync();
            reopened.AddRange((await gw.LossWatchAsync()).Reopened);
            clock.Advance(Tick);
        }

        var held = gw.ReopenReading().Held;
        log.WriteLine($"stalled attempts      : {afterConfirming} in the confirming pass, {stalls} in all");
        log.WriteLine($"closes on the wire    : {conn.Closes}, position ES {Held(conn, "ES")}");
        log.WriteLine($"held by               : {held}");
        log.WriteLine($"dashboard             : {gw.FlattenStateToday().Why}");

        // EVERY PASS TRIED: the confirming pass, and each of the four calls into the watch and the
        // sweep after it, made an attempt of its own.
        Assert.True(stalls >= afterConfirming + 2, $"{stalls} attempts in all, {afterConfirming} by the confirming pass");
        Assert.Equal(0, conn.Closes);
        Assert.Equal(1m, Held(conn, "ES"));

        // AND NOTHING REOPENED: the closure stands over an open book, and says why.
        Assert.Empty(reopened);
        Assert.NotNull(gw.DayClosed(account));
        Assert.NotNull(held);
        Assert.Contains("has NOT closed your open positions yet", gw.FlattenStateToday().Why!, StringComparison.Ordinal);

        // THE PLATFORM ANSWERS AGAIN. The pass that flattens does not also reopen — its watch looked
        // at the book before its sweep closed it — and the one after it does, on a receipt.
        armed = false;
        await gw.RefreshHealthAsync();
        log.WriteLine($"after the platform    : closes {conn.Closes}, ES {Held(conn, "ES")}, "
                      + $"flat={gw.FlattenToday(account)?.Flat}, still closed={gw.DayClosed(account) is not null}");
        Assert.Equal(1, conn.Closes);
        Assert.Equal(0m, Held(conn, "ES"));
        Assert.True(gw.FlattenToday(account)!.Flat);
        Assert.NotNull(gw.DayClosed(account));

        clock.Advance(Tick);
        var reopen = await gw.LossWatchAsync();
        log.WriteLine($"reopened              : [{string.Join(", ", reopen.Reopened)}]");
        Assert.Single(reopen.Reopened);
        Assert.Null(gw.DayClosed(account));
    }

    /// <summary>
    /// AN ATTEMPT THAT PUT A CLOSE ON THE WIRE IS NEVER REPEATED OVER IT — U-flatten-2's rule, kept.
    ///
    /// <para>The platform takes the close and the answer is lost: the order rests at the platform and
    /// TradeAgent cannot say whether it acted. That is not an attempt that sent nothing, and "try
    /// again" over it is the long-1-becomes-short-1 failure the press mechanics exist to prevent. So
    /// the outcome is written as it always was, its row stays flagged for the owner, and pass after
    /// pass sends nothing more.</para>
    /// </summary>
    [Fact]
    public async Task An_attempt_that_put_a_close_on_the_wire_is_never_repeated_over_it()
    {
        var (gw, conn, db, clock) = await Ready(TimeSpan.FromSeconds(2));
        using var _1 = db;
        await using var _2 = gw;
        var account = conn.Broker.AccountId;

        await gw.PlaceAsync(new AgentContext("a"), "lost-open", TestEnv.Buy("ES"));
        conn.Broker.PriceOffset = -20m;

        // THE CLOSE RESTS AT THE PLATFORM AND ITS ANSWER IS LOST.
        conn.Faults.Fill = FillBehaviour.LeaveWorking;
        conn.Faults.DropAfterBrokerAccept = 1;

        for (var i = 0; i < 4; i++) { clock.Advance(Tick); await gw.RefreshHealthAsync(); }

        var flatten = gw.FlattenToday(account);
        log.WriteLine($"closes on the wire    : {conn.Closes}");
        log.WriteLine($"book                  : [{string.Join(" | ", conn.Broker.Orders.Select(o => $"{o.Side} {o.Quantity} {o.Symbol} {o.State}"))}]");
        log.WriteLine($"position              : ES {Held(conn, "ES")}");
        log.WriteLine($"app rows              : {string.Join(" | ", AppRows(db))}");
        log.WriteLine($"flatten               : {(flatten is null ? "no record" : $"flat={flatten.Flat} — {flatten.Why}")}");

        Assert.NotNull(gw.DayClosed(account));
        Assert.Equal(1, conn.Closes);
        Assert.NotNull(flatten);
        Assert.False(flatten.Flat);
        Assert.NotEmpty(FlaggedAppRows(db));
        Assert.True(gw.HasUnconfirmedWork());
    }
}

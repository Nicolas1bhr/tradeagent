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
    }

    static readonly DateTimeOffset Noon = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Tick = TimeSpan.FromSeconds(20);
    static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

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
}

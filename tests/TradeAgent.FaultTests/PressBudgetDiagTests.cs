using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

// =================================================================================================
// DIAGNOSTIC ONLY — U-fix-press-budget item 1. BRANCH ONLY: removed before the proving run.
//
// Replays the sighting's own fixture (PressIdShapeTests.The_operator_cancel_all_names_its_legs_
// without_the_brokers_order_id, red on windows-latest in run 37391256380 with "Expected: 2 /
// Actual: 0") and the close-all twin of it, with every platform call and every SQLite statement on
// the gateway's own connection stamped with the budget left, and the press's own steps reporting
// through the branch-only TradingGateway.DiagPressProbe. It asserts nothing: it is a measurement.
// The CONTROLS put the deadline past at the first platform call — once by holding the call, once
// by a second writer holding the store across the press's own first commit — and print the
// sighting's assertion message verbatim, so it can be compared byte for byte on any runner.
// =================================================================================================
public class PressBudgetDiagTests(ITestOutputHelper log)
{
    static int Count => int.TryParse(Environment.GetEnvironmentVariable("PRESSDIAG_N"), out var n) && n > 0 ? n : 2;

    public static IEnumerable<object[]> Iterations() => Enumerable.Range(1, Count).Select(i => new object[] { i });

    sealed class Timeline
    {
        readonly Stopwatch _sw = Stopwatch.StartNew();
        public readonly ConcurrentQueue<string> Lines = new();
        public readonly ConcurrentQueue<double> Commits = new();
        public double Ms => _sw.Elapsed.TotalMilliseconds;
        public void Add(string line) => Lines.Enqueue($"t={Ms,8:0}ms {line}");

        public (int N, double Sum, double Max) CommitsSince(int skip)
        {
            var c = Commits.Skip(skip).ToList();
            return (c.Count, c.Sum(), c.Count == 0 ? 0 : c.Max());
        }
    }

    static string Left(long? deadline) =>
        deadline is { } d ? $"{d - Environment.TickCount64} ms left" : "no deadline";

    /// <summary>Every call, stamped: the budget left as it started, how long it took, what it answered.</summary>
    sealed class TimingConnector(FakeConnector inner, Timeline t) : ITradingConnector
    {
        public FakeBroker Broker => inner.Broker;
        public bool StallFirstRiskReducingCall;
        public Func<Task>? AtFirstRiskReducingCall;
        public string? LeftAtFirstRiskReducingCall;
        int _first;

        async Task<T> Time<T>(string op, Func<Task<T>> call)
        {
            var start = t.Ms;
            var left = "";
            if (RiskReducingScope.DeadlineAt is { } d)
            {
                if (Interlocked.Exchange(ref _first, 1) == 0)
                {
                    LeftAtFirstRiskReducingCall = Left(d);
                    if (StallFirstRiskReducingCall)
                    {
                        t.Add($"CONTROL holds {op} until the deadline has passed");
                        while (RiskReducingScope.LeftUntil(d) > TimeSpan.Zero) await Task.Delay(10);
                    }
                    if (AtFirstRiskReducingCall is { } hook) await hook();
                }
                left = $" [{Left(RiskReducingScope.DeadlineAt)} as it started]";
            }
            try
            {
                var r = await call();
                t.Add($"CALL {op,-12} {t.Ms - start,6:0}ms{left} ok");
                return r;
            }
            catch (Exception ex)
            {
                t.Add($"CALL {op,-12} {t.Ms - start,6:0}ms{left} THREW {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }

        Task Time(string op, Func<Task> call) => Time<int>(op, async () => { await call(); return 0; });

        public string Id => inner.Id;
        public string DisplayName => inner.DisplayName;
        public ConnectorCapabilities Capabilities => inner.Capabilities;
        public TimeSpan WorstCaseOperationPath => inner.WorstCaseOperationPath;
        public TimeSpan EmergencyBudget => inner.EmergencyBudget;
        public Task ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);
        public Task<HealthState> GetHealthAsync(CancellationToken ct = default) => inner.GetHealthAsync(ct);
        public Task<bool> IsConnectedAsync(CancellationToken ct = default) => inner.IsConnectedAsync(ct);
        public Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken ct = default) => Time("accounts", () => inner.GetAccountsAsync(ct));
        public Task<AccountInfo?> GetAccountAsync(string a, CancellationToken ct = default) => Time("account", () => inner.GetAccountAsync(a, ct));
        public Task<IReadOnlyList<InstrumentInfo>> GetInstrumentsAsync(CancellationToken ct = default) => Time("instruments", () => inner.GetInstrumentsAsync(ct));
        public Task<QuoteInfo?> GetQuoteAsync(string s, CancellationToken ct = default) => Time($"quote {s}", () => inner.GetQuoteAsync(s, ct));
        public Task<IReadOnlyList<PositionInfo>> GetPositionsAsync(string a, CancellationToken ct = default) => Time("positions", () => inner.GetPositionsAsync(a, ct));
        public Task<IReadOnlyList<OrderInfo>> GetOrdersAsync(string a, bool i, DateTimeOffset? s, CancellationToken ct = default) => Time("orders", () => inner.GetOrdersAsync(a, i, s, ct));
        public Task<IReadOnlyList<ExecutionInfo>> GetExecutionsAsync(string a, DateTimeOffset? s, CancellationToken ct = default) => Time("executions", () => inner.GetExecutionsAsync(a, s, ct));
        public Task<OrderInfo> PlaceOrderAsync(PlaceOrderCommand c, CancellationToken ct = default) => Time($"PLACE {c.Symbol}", () => inner.PlaceOrderAsync(c, ct));
        public Task<OrderInfo> ModifyOrderAsync(ModifyOrderCommand c, CancellationToken ct = default) => Time("MODIFY", () => inner.ModifyOrderAsync(c, ct));
        public Task CancelOrderAsync(string id, CancellationToken ct = default) => Time($"CANCEL {id}", () => inner.CancelOrderAsync(id, ct));
        public Task<IReadOnlyList<string>> CancelAllOrdersAsync(string a, CancellationToken ct = default) => Time("CANCEL-ALL", () => inner.CancelAllOrdersAsync(a, ct));
        public Task<OrderInfo?> ClosePositionAsync(string a, string s, string c, CancellationToken ct = default) => Time($"CLOSE {s}", () => inner.ClosePositionAsync(a, s, c, ct));
        public event Action<HealthState>? ConnectionChanged { add => inner.ConnectionChanged += value; remove => inner.ConnectionChanged -= value; }
        public event Action<QuoteInfo>? QuoteChanged { add => inner.QuoteChanged += value; remove => inner.QuoteChanged -= value; }
        public event Action<OrderInfo>? OrderChanged { add => inner.OrderChanged += value; remove => inner.OrderChanged -= value; }
        public event Action<ExecutionInfo>? ExecutionReceived { add => inner.ExecutionReceived += value; remove => inner.ExecutionReceived -= value; }
        public event Action<PositionInfo>? PositionChanged { add => inner.PositionChanged += value; remove => inner.PositionChanged -= value; }
        public event Action<AccountInfo>? AccountChanged { add => inner.AccountChanged += value; remove => inner.AccountChanged -= value; }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>
    /// Every statement on the gateway's own connection, timed by SQLite itself: each one inside a
    /// risk-reducing scope with the budget left after it, each COMMIT counted, anything slow outside.
    /// </summary>
    static void Profile(Database db, Timeline t) =>
        raw.sqlite3_profile(db.Connection.Handle, (strdelegate_profile)((_, statement, ns) =>
        {
            var ms = ns / 1_000_000.0;
            var sql = statement.Trim();
            if (sql.StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase)) t.Commits.Enqueue(ms);
            var shortSql = sql.Length > 48 ? sql[..48].ReplaceLineEndings(" ") + "…" : sql.ReplaceLineEndings(" ");
            if (RiskReducingScope.DeadlineAt is { } d)
                t.Add($"SQL  {ms,7:0.0}ms {Left(d)} after: {shortSql}");
            else if (ms >= 50)
                t.Add($"SQL  {ms,7:0.0}ms (outside the press): {shortSql}");
        }), null);

    /// <summary>A second writer holds the database's write lock for <paramref name="hold"/>.</summary>
    static (Task Holder, Task Locked) HoldTheStore(string file, TimeSpan hold, Stopwatch heldFor)
    {
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = Task.Run(async () =>
        {
            using var other = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
            other.Open();
            using (var begin = other.CreateCommand()) { begin.CommandText = "BEGIN IMMEDIATE;"; begin.ExecuteNonQuery(); }
            heldFor.Restart();
            locked.SetResult();
            await Task.Delay(hold);
            using (var commit = other.CreateCommand()) { commit.CommandText = "COMMIT;"; commit.ExecuteNonQuery(); }
            heldFor.Stop();
        });
        return (holder, locked.Task);
    }

    void Emit(string scenario, int i, Timeline t, string summary, bool full)
    {
        var text = $"=== {scenario} #{i}: {summary}\n" + (full ? string.Join("\n", t.Lines.Select(l => "    " + l)) + "\n" : "");
        log.WriteLine(text);
        if (Environment.GetEnvironmentVariable("PRESSDIAG_OUT") is { Length: > 0 } path)
            lock (typeof(PressBudgetDiagTests)) File.AppendAllText(path, text + "\n");
    }

    enum Control { None, DeadlineGoneAtTheRead, StoreHeldAcrossThePressRow }

    /// <summary>PressIdShapeTests' cancel-all test, its fixture byte for byte (TestEnv.Ready's settings), measured.</summary>
    async Task CancelAllScenario(string name, int i, Control control)
    {
        var t = new Timeline();
        var budget = TimeSpan.FromSeconds(2);
        var db = TestEnv.NewDb();
        var schemaMs = t.Ms;
        using var _1 = db;
        Profile(db, t);
        var fake = new FakeConnector(new FakeBroker(), new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        var conn = new TimingConnector(fake, t) { StallFirstRiskReducingCall = control == Control.DeadlineGoneAtTheRead };
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = fake.Broker.AccountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
            s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments];
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        var readyMs = t.Ms; var readyCommits = t.Commits.Count;
        t.Add("--- place open-a");
        await gw.PlaceAsync(new AgentContext("a"), "open-a",
            new PlaceIntent("ES", OrderSide.Buy, OrderType.Limit, 1m, 1m, null, TimeInForce.Day, null));
        var placeAMs = t.Ms;
        t.Add("--- place open-b");
        await gw.PlaceAsync(new AgentContext("a"), "open-b",
            new PlaceIntent("NQ", OrderSide.Buy, OrderType.Limit, 1m, 1m, null, TimeInForce.Day, null));
        var placeBMs = t.Ms; var placesCommits = t.Commits.Count;
        var working = fake.Broker.Orders.Count(o => o.State == ExecutionState.WORKING);

        var heldFor = new Stopwatch();
        Task? holder = null;
        if (control == Control.StoreHeldAcrossThePressRow)
        {
            var (h, locked) = HoldTheStore(db.Connection.DataSource, budget + TimeSpan.FromSeconds(1), heldFor);
            holder = h;
            await locked;
            t.Add("CONTROL: a second writer holds the store from here for 3000 ms");
        }

        t.Add($"--- the press ({working} working order(s) on the book)");
        var pressStart = t.Ms; var pressCommits0 = t.Commits.Count;
        TradingGateway.DiagPressProbe.Value = (step, d) => t.Add($"STEP {step} — {Left(d)}");
        Exception? threw = null;
        try { await gw.OperatorCancelAllAsync(); }
        catch (Exception ex) { threw = ex; }
        TradingGateway.DiagPressProbe.Value = null;
        var pressMs = t.Ms - pressStart;
        var press = t.CommitsSince(pressCommits0);
        if (holder is not null) await holder;

        var legs = gw.Requests.Query("request_id LIKE 'op-cancel-%' AND intent='CANCEL'");
        var row = gw.Requests.Query("request_id LIKE 'op-cancel-%' AND intent='CANCEL_ALL'").SingleOrDefault();
        var verdict = Record.Exception(() => Assert.Equal(2, legs.Count));
        var cancelledAtBroker = fake.Broker.Orders.Count(o => o.State == ExecutionState.CANCELLED);
        t.Add($"press row: {row?.State} flagged={row?.NeedsReconciliation} '{row?.LastError}'");
        t.Add($"legs: {legs.Count} [{string.Join("; ", legs.Select(l => $"{l.RequestId} {l.State}"))}]");
        if (threw is not null) t.Add($"the press THREW {threw.GetType().Name}: {threw.Message}");
        if (control == Control.StoreHeldAcrossThePressRow) t.Add($"store held by another: {heldFor.ElapsedMilliseconds} ms");

        var all = t.CommitsSince(0);
        Emit(name, i, t,
            $"total {t.Ms:0}ms | new Database {schemaMs:0}ms | ready {readyMs - schemaMs:0}ms ({readyCommits} commits) | "
            + $"places {placeAMs - readyMs:0}+{placeBMs - placeAMs:0}ms ({placesCommits - readyCommits} commits) | "
            + $"press {pressMs:0}ms ({press.N} commits sum {press.Sum:0} max {press.Max:0}ms) | at the read {conn.LeftAtFirstRiskReducingCall} | "
            + $"commits {all.N} sum {all.Sum:0} max {all.Max:0}ms | legs {legs.Count}, cancelled at the broker {cancelledAtBroker} | "
            + $"verdict: {(verdict is null ? "PASS" : verdict.Message.ReplaceLineEndings(" / "))}",
            full: control != Control.None || i <= 1 || legs.Count != 2 || pressMs > 500);
        await gw.DisposeAsync();
    }

    /// <summary>PressIdShapeTests' close-all test (the ES name), its fixture as written there, measured.</summary>
    async Task CloseAllScenario(string name, int i, bool storeHeldFromTheCapture)
    {
        const string symbol = "ES 12-25 [CME Globex Futures]";
        var t = new Timeline();
        var budget = TimeSpan.FromSeconds(2);
        var db = TestEnv.NewDb();
        var schemaMs = t.Ms;
        using var _1 = db;
        Profile(db, t);
        var fake = new FakeConnector(new FakeBroker());
        var conn = new TimingConnector(fake, t);
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = fake.Broker.AccountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
            s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments];
            s.Risk.MaxOrderQuantity = 5m;
            s.Risk.MaxNotionalPerOrder = 0m;
            s.Risk.InstrumentAllowlist = [symbol];
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        var readyMs = t.Ms;
        await gw.PlaceAsync(new AgentContext("a"), "open-1",
            new PlaceIntent(symbol, OrderSide.Buy, OrderType.Market, 2m, null, null, TimeInForce.Day, null));
        var placeMs = t.Ms;

        var heldFor = new Stopwatch();
        Task? holder = null;
        if (storeHeldFromTheCapture)
            conn.AtFirstRiskReducingCall = async () =>
            {
                var (h, locked) = HoldTheStore(db.Connection.DataSource, budget + TimeSpan.FromSeconds(1), heldFor);
                holder = h;
                await locked;
                t.Add("CONTROL: a second writer holds the store from the capture for 3000 ms");
            };

        t.Add("--- the press");
        var pressStart = t.Ms; var c0 = t.Commits.Count;
        TradingGateway.DiagPressProbe.Value = (step, d) => t.Add($"STEP {step} — {Left(d)}");
        Exception? threw = null;
        try { await gw.OperatorCloseAllAsync(); }
        catch (Exception ex) { threw = ex; }
        TradingGateway.DiagPressProbe.Value = null;
        var pressMs = t.Ms - pressStart;
        var press = t.CommitsSince(c0);
        if (holder is not null) await holder;

        var rows = gw.Requests.Query("request_id LIKE 'op-close-%' AND intent='PLACE'");
        var held = fake.Broker.Positions.FirstOrDefault(p => p.Symbol == symbol)?.Quantity ?? 0m;
        t.Add($"rows: [{string.Join("; ", rows.Select(r => $"{r.RequestId} {r.State} '{r.LastError}'"))}]");
        if (threw is not null) t.Add($"the press THREW {threw.GetType().Name}: {threw.Message}");
        if (storeHeldFromTheCapture) t.Add($"store held by another: {heldFor.ElapsedMilliseconds} ms");

        var all = t.CommitsSince(0);
        Emit(name, i, t,
            $"total {t.Ms:0}ms | new Database {schemaMs:0}ms | ready {readyMs - schemaMs:0}ms | place {placeMs - readyMs:0}ms | "
            + $"press {pressMs:0}ms ({press.N} commits sum {press.Sum:0} max {press.Max:0}ms) | at the capture {conn.LeftAtFirstRiskReducingCall} | "
            + $"commits {all.N} sum {all.Sum:0} max {all.Max:0}ms | position after {held} | rows {rows.Count}",
            full: storeHeldFromTheCapture || i <= 1 || held != 0m || pressMs > 500);
        await gw.DisposeAsync();
    }

    [Theory]
    [MemberData(nameof(Iterations))]
    public Task Cancel_all_sighting_measured(int i) => CancelAllScenario("cancel-all", i, Control.None);

    [Theory]
    [MemberData(nameof(Iterations))]
    public Task Close_all_twin_measured(int i) => CloseAllScenario("close-all", i, storeHeldFromTheCapture: false);

    [Fact]
    public Task Cancel_all_CONTROL_with_the_deadline_gone_at_the_first_platform_call() =>
        CancelAllScenario("cancel-all CONTROL deadline", 1, Control.DeadlineGoneAtTheRead);

    [Fact]
    public Task Cancel_all_CONTROL_with_the_store_held_past_the_budget() =>
        CancelAllScenario("cancel-all CONTROL store", 1, Control.StoreHeldAcrossThePressRow);

    [Fact]
    public Task Close_all_CONTROL_with_the_store_held_past_the_budget() =>
        CloseAllScenario("close-all CONTROL store", 1, storeHeldFromTheCapture: true);

    /// <summary>Twenty bare one-row commits on the same disk, the U-press-win-3 measurement, for scale.</summary>
    [Fact]
    public void Bare_commits_on_this_disk()
    {
        var t = new Timeline();
        var db = TestEnv.NewDb();
        using var _1 = db;
        Profile(db, t);
        for (var k = 0; k < 20; k++) db.SetKv($"diag:{k}", new string('x', 200));
        var c = t.Commits.OrderBy(x => x).ToList();
        Emit("bare-commits", 1, t, $"n={c.Count} min {c.First():0.0} med {c[c.Count / 2]:0.0} max {c.Last():0.0} ms: [{string.Join(" ", t.Commits.Select(x => x.ToString("0")))}]", full: false);
    }
}

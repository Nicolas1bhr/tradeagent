using System.Collections.Concurrent;
using System.Diagnostics;
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
// DIAGNOSTIC ONLY — U-fix-loss-reopen item 1. BRANCH ONLY: removed before the proving run.
//
// Replays the two Windows sightings' own fixtures (LossWatchTests' first test and
// LossHoldSurfacesTests.Held) with every connector call and every SQLite COMMIT stamped on one
// wall clock, and prints every watch pass, every flatten step and where the time went. It asserts
// nothing: it is a measurement. The two CONTROL scenarios hold the caller until the flatten's own
// deadline has passed at its first risk-reducing call — what one stalled synchronous=FULL commit
// does — so the sighting's output can be compared byte for byte on any runner.
// =================================================================================================
public class LossFlattenDiagTests(ITestOutputHelper log)
{
    static int Count => int.TryParse(Environment.GetEnvironmentVariable("LOSSDIAG_N"), out var n) && n > 0 ? n : 2;

    public static IEnumerable<object[]> Iterations() => Enumerable.Range(1, Count).Select(i => new object[] { i });

    sealed class Timeline
    {
        readonly Stopwatch _sw = Stopwatch.StartNew();
        readonly long _tick0 = Environment.TickCount64;
        public readonly ConcurrentQueue<string> Lines = new();
        public readonly ConcurrentQueue<double> Commits = new();
        public double Ms => _sw.Elapsed.TotalMilliseconds;
        public double TickToMs(long tick) => tick - _tick0;
        public void Add(string line) => Lines.Enqueue($"t={Ms,8:0}ms {line}");

        public (int N, double Sum, double Max) CommitsSince(int skip)
        {
            var c = Commits.Skip(skip).ToList();
            return (c.Count, c.Sum(), c.Count == 0 ? 0 : c.Max());
        }
    }

    /// <summary>Every call, stamped: when it started, how much of the risk-reducing deadline was left, how long it took.</summary>
    sealed class TimingConnector(ITradingConnector inner, Timeline t) : ITradingConnector
    {
        public bool StallFirstRiskReducingCall;
        int _stalled;

        async Task<T> Time<T>(string op, Func<Task<T>> call)
        {
            var start = t.Ms;
            string left = "";
            if (RiskReducingScope.DeadlineAt is { } d)
            {
                if (StallFirstRiskReducingCall && Interlocked.Exchange(ref _stalled, 1) == 0)
                {
                    t.Add($"CONTROL stalls {op} until the deadline has passed");
                    while (RiskReducingScope.LeftUntil(d) > TimeSpan.Zero) await Task.Delay(10);
                }
                left = $" deadline-left={RiskReducingScope.LeftUntil(d).TotalMilliseconds:0}ms (opened t={t.TickToMs(d) - inner.EmergencyBudget.TotalMilliseconds:0}ms)";
            }
            try
            {
                var r = await call();
                t.Add($"{op,-14} {t.Ms - start,6:0}ms{left} ok");
                return r;
            }
            catch (Exception ex)
            {
                t.Add($"{op,-14} {t.Ms - start,6:0}ms{left} THREW {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }

        async Task Time(string op, Func<Task> call) => await Time<int>(op, async () => { await call(); return 0; });

        public string Id => inner.Id;
        public string DisplayName => inner.DisplayName;
        public ConnectorCapabilities Capabilities => inner.Capabilities;
        public TimeSpan WorstCaseOperationPath => inner.WorstCaseOperationPath;
        public TimeSpan EmergencyBudget => inner.EmergencyBudget;
        public Task ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);
        public Task<HealthState> GetHealthAsync(CancellationToken ct = default) => Time("health", () => inner.GetHealthAsync(ct));
        public Task<bool> IsConnectedAsync(CancellationToken ct = default) => Time("connected?", () => inner.IsConnectedAsync(ct));
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

    /// <summary>Every COMMIT on the test's own database, timed by SQLite itself.</summary>
    static void ProfileCommits(Database db, Timeline t) =>
        raw.sqlite3_profile(db.Connection.Handle, (strdelegate_profile)((_, statement, ns) =>
        {
            if (!statement.StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase)) return;
            var ms = ns / 1_000_000.0;
            t.Commits.Enqueue(ms);
            if (ms >= 20) t.Add($"COMMIT         {ms,6:0}ms");
        }), null);

    sealed class WatchClock(DateTimeOffset at) : TimeProvider
    {
        DateTimeOffset _now = at;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    sealed class CoherentClock(DateTimeOffset at) : TimeProvider
    {
        DateTimeOffset _now = at;
        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => _now.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan by) => _now += by;
        public void MoveTo(DateTimeOffset to) => _now = to;
    }

    static readonly DateTimeOffset Noon = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Tick = TimeSpan.FromSeconds(20);

    static string Out(TradingGateway gw, string account)
    {
        var f = gw.FlattenToday(account);
        var st = gw.FlattenStateToday();
        var flatten = f is null ? "flatten record: NONE"
            : $"flatten record: flat={f.Flat} legs=[{string.Join("; ", f.Legs.Select(l => $"{l.Symbol} {l.State} resolved={l.Resolved}"))}] "
              + $"openersNotSettled=[{string.Join("; ", f.OpenersNotSettled)}] residual=[{string.Join("; ", f.Residual)}]";
        return $"{flatten}\n      flatten state: {st.State ?? "(none)"} — {st.Why ?? ""}\n      unconfirmed work: {gw.HasUnconfirmedWork()}";
    }

    static string EngineeringTail(Database db, int afterId)
    {
        using var c = db.Cmd("SELECT id,event,severity,metadata,exception FROM engineering_log WHERE id>$i AND component='Gateway' ORDER BY id", ("$i", afterId));
        using var r = c.ExecuteReader();
        var lines = new List<string>();
        while (r.Read())
        {
            var ev = r.GetString(1);
            if (!ev.StartsWith("loss_", StringComparison.Ordinal) && !ev.StartsWith("dispatch_", StringComparison.Ordinal)) continue;
            lines.Add($"      eng {ev} [{r.GetString(2)}] {(r.IsDBNull(3) ? "" : r.GetString(3))}{(r.IsDBNull(4) ? "" : " EX " + r.GetString(4).Split('\n')[0])}");
        }
        return string.Join("\n", lines);
    }

    static int LastEngId(Database db)
    {
        using var c = db.Cmd("SELECT COALESCE(MAX(id),0) FROM engineering_log");
        return Convert.ToInt32(c.ExecuteScalar());
    }

    void Emit(string scenario, int i, Timeline t, string summary, bool full)
    {
        var text = $"=== {scenario} #{i}: {summary}\n" + (full ? string.Join("\n", t.Lines.Select(l => "    " + l)) + "\n" : "");
        log.WriteLine(text);
        if (Environment.GetEnvironmentVariable("LOSSDIAG_OUT") is { Length: > 0 } path)
            lock (typeof(LossFlattenDiagTests)) File.AppendAllText(path, text + "\n");
    }

    /// <summary>LossWatchTests.A_book_that_goes_through_the_budget_with_no_order_arriving_closes_the_day, measured.</summary>
    async Task WatchScenario(string name, int i, bool control)
    {
        var t = new Timeline();
        var clock = new WatchClock(Noon);
        var db = TestEnv.NewDb();
        using var _1 = db;
        ProfileCommits(db, t);
        var rec = new RecordingConnector(new FakeConnector(new FakeBroker()) { QuoteClock = clock });
        var conn = new TimingConnector(rec, t);
        var gw = new TradingGateway(db, conn, new HealthRegistry(), new GatewayOptions { Clock = clock });
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = rec.Broker.AccountId;
            s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments];
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
            s.Risk.MaxDailyLoss = 1_000m;
        });
        await conn.ConnectAsync();
        t.Add("--- ready: first health pass");
        await gw.RefreshHealthAsync();
        var account = rec.Broker.AccountId;

        t.Add("--- place");
        await gw.PlaceAsync(new AgentContext("a"), "watch-open", TestEnv.Buy("ES"));
        rec.Broker.PriceOffset = -20m;
        var mutations = rec.Mutations;
        var setupCommits = t.Commits.Count;
        var setupMs = t.Ms;

        conn.StallFirstRiskReducingCall = control;
        clock.Advance(Tick);
        t.Add("--- pass one");
        var p1 = t.Ms;
        await gw.RefreshHealthAsync();
        t.Add($"--- pass one done in {t.Ms - p1:0}ms: closed={gw.DayClosed(account) is not null}");

        clock.Advance(Tick);
        t.Add("--- pass two");
        var p2 = t.Ms;
        var c2 = t.Commits.Count;
        var eng = LastEngId(db);
        await gw.RefreshHealthAsync();
        var pass2 = t.CommitsSince(c2);
        t.Add($"--- pass two done in {t.Ms - p2:0}ms ({pass2.N} commits, {pass2.Sum:0}ms, max {pass2.Max:0}ms): closed={gw.DayClosed(account) is not null}, "
              + $"closes={rec.Closes}, mutations {mutations}/{rec.Mutations}");
        t.Add(Out(gw, account));
        t.Add(EngineeringTail(db, eng));

        var all = t.CommitsSince(0);
        var flat = gw.FlattenToday(account)?.Flat == true;
        Emit(name, i, t,
            $"total {t.Ms:0}ms (setup {setupMs:0}ms, {setupCommits} commits); commits {all.N} sum {all.Sum:0}ms max {all.Max:0}ms; "
            + $"closes={rec.Closes} flat={flat} closed={gw.DayClosed(account) is not null}",
            full: control || !flat || i <= 1);
        await gw.DisposeAsync();
    }

    /// <summary>LossHoldSurfacesTests.Held up to the reopen, measured (TestEnv.Ready's own settings).</summary>
    async Task ReopenScenario(string name, int i, bool control)
    {
        var t = new Timeline();
        var clock = new CoherentClock(Noon);
        var db = TestEnv.NewDb();
        using var _1 = db;
        ProfileCommits(db, t);
        var fake = new FakeConnector(new FakeBroker()) { QuoteClock = clock };
        var conn = new TimingConnector(fake, t);
        var gw = new TradingGateway(db, conn, new HealthRegistry(), new GatewayOptions { Clock = clock });
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = fake.Broker.AccountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
            s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments];
            s.Risk.MaxDailyLoss = 1_000m;
        });
        await conn.ConnectAsync();
        t.Add("--- ready: first health pass");
        await gw.RefreshHealthAsync();
        var account = fake.Broker.AccountId;

        // Breach(gw, conn, clock, "episode-one", 1_000m, log)
        gw.Update(s => s.Risk.MaxDailyLoss = 1_000m);
        t.Add("--- place");
        await gw.PlaceAsync(new AgentContext("a"), "episode-one", TestEnv.Buy("ES"));
        fake.Broker.PriceOffset = -20m;
        conn.StallFirstRiskReducingCall = control;
        clock.Advance(Tick);
        t.Add("--- watch 1");
        var w1 = await gw.LossWatchAsync();
        t.Add($"--- watch 1: pull={w1.Pull} ran={w1.Ran} dayReached={w1.DayReached} closed=[{string.Join(",", w1.Closed)}] why={w1.Why}");
        clock.Advance(Tick);
        t.Add("--- watch 2 (the confirming pull, and the flatten)");
        var c2 = t.Commits.Count;
        var p2 = t.Ms;
        var eng = LastEngId(db);
        var w2 = await gw.LossWatchAsync();
        var pass2 = t.CommitsSince(c2);
        t.Add($"--- watch 2 done in {t.Ms - p2:0}ms ({pass2.N} commits, {pass2.Sum:0}ms, max {pass2.Max:0}ms): pull={w2.Pull} closed=[{string.Join(",", w2.Closed)}]");
        t.Add(Out(gw, account));
        t.Add(EngineeringTail(db, eng));
        fake.Broker.PriceOffset = 0m;
        gw.Update(s => s.Risk.MaxDailyLoss = 100_000m);

        // Held: the reopen
        var first = gw.DayClosed(account)!;
        clock.MoveTo(LossReopen.EligibleAt(first.ConfirmedAt, TimeSpan.FromHours(24)) + TimeSpan.FromMinutes(1));
        t.Add("--- watch 3 (the reopen, eligible + 1 min)");
        var p3 = t.Ms;
        eng = LastEngId(db);
        var w3 = await gw.LossWatchAsync();
        t.Add($"--- watch 3 done in {t.Ms - p3:0}ms: pull={w3.Pull} ran={w3.Ran} reopened=[{string.Join(",", w3.Reopened)}] why={w3.Why}");
        t.Add($"      held by: {gw.ReopenReading().Held ?? "(nothing)"}");
        t.Add($"      positions now: [{string.Join(", ", fake.Broker.Positions.Select(p => $"{p.Symbol} {p.Quantity}"))}]");
        t.Add(EngineeringTail(db, eng));

        var all = t.CommitsSince(0);
        Emit(name, i, t,
            $"total {t.Ms:0}ms; commits {all.N} sum {all.Sum:0}ms max {all.Max:0}ms; reopened={w3.Reopened.Count} "
            + $"flat={gw.FlattenToday(account)?.Flat}",
            full: control || w3.Reopened.Count != 1 || i <= 1);
        await gw.DisposeAsync();
    }

    [Theory]
    [MemberData(nameof(Iterations))]
    public Task Watch_sighting_measured(int i) => WatchScenario("watch", i, control: false);

    [Theory]
    [MemberData(nameof(Iterations))]
    public Task Reopen_sighting_measured(int i) => ReopenScenario("reopen", i, control: false);

    [Fact]
    public Task Watch_control_with_the_deadline_gone_at_the_first_flatten_call() => WatchScenario("watch-CONTROL", 1, control: true);

    [Fact]
    public Task Reopen_control_with_the_deadline_gone_at_the_first_flatten_call() => ReopenScenario("reopen-CONTROL", 1, control: true);

    /// <summary>Twenty bare one-row commits on the same disk, the U-press-win-3 measurement, for scale.</summary>
    [Fact]
    public void Bare_commits_on_this_disk()
    {
        var t = new Timeline();
        var db = TestEnv.NewDb();
        using var _1 = db;
        ProfileCommits(db, t);
        for (var k = 0; k < 20; k++) db.SetKv($"diag:{k}", new string('x', 200));
        var c = t.Commits.OrderBy(x => x).ToList();
        Emit("bare-commits", 1, t, $"n={c.Count} min {c.First():0.0} med {c[c.Count / 2]:0.0} max {c.Last():0.0} ms: [{string.Join(" ", t.Commits.Select(x => x.ToString("0")))}]", full: false);
    }
}

using System.Diagnostics;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// TEMPORARY. Deliberately fails so that its numbers reach the CI log on all three runners:
/// which STEP of the press spends the time when the whole press overruns its 2 s budget.
/// Deleted once U-press-budget has its answer.
/// </summary>
sealed class TimedConnector(ITradingConnector inner, Stopwatch sw, List<string> log) : ITradingConnector
{
    async Task<T> M<T>(string name, Func<Task<T>> f)
    {
        var a = sw.ElapsedMilliseconds;
        try { var r = await f(); log.Add($"{name} {a}->{sw.ElapsedMilliseconds}"); return r; }
        catch (Exception ex) { log.Add($"{name} {a}->{sw.ElapsedMilliseconds} {ex.GetType().Name}"); throw; }
    }
    async Task M(string name, Func<Task> f)
    {
        var a = sw.ElapsedMilliseconds;
        try { await f(); log.Add($"{name} {a}->{sw.ElapsedMilliseconds}"); }
        catch (Exception ex) { log.Add($"{name} {a}->{sw.ElapsedMilliseconds} {ex.GetType().Name}"); throw; }
    }

    public string Id => inner.Id;
    public string DisplayName => inner.DisplayName;
    public ConnectorCapabilities Capabilities => inner.Capabilities;
    public TimeSpan WorstCaseOperationPath => inner.WorstCaseOperationPath;
    public TimeSpan EmergencyBudget => inner.EmergencyBudget;
    public Task ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);
    public Task<HealthState> GetHealthAsync(CancellationToken ct = default) => inner.GetHealthAsync(ct);
    public Task<bool> IsConnectedAsync(CancellationToken ct = default) => inner.IsConnectedAsync(ct);
    public Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken ct = default) => M("accounts", () => inner.GetAccountsAsync(ct));
    public Task<AccountInfo?> GetAccountAsync(string a, CancellationToken ct = default) => M("account", () => inner.GetAccountAsync(a, ct));
    public Task<IReadOnlyList<InstrumentInfo>> GetInstrumentsAsync(CancellationToken ct = default) => M("instruments", () => inner.GetInstrumentsAsync(ct));
    public Task<QuoteInfo?> GetQuoteAsync(string s, CancellationToken ct = default) => M("quote", () => inner.GetQuoteAsync(s, ct));
    public Task<IReadOnlyList<PositionInfo>> GetPositionsAsync(string a, CancellationToken ct = default) => M("positions", () => inner.GetPositionsAsync(a, ct));
    public Task<IReadOnlyList<OrderInfo>> GetOrdersAsync(string a, bool i, DateTimeOffset? s, CancellationToken ct = default) => M("orders", () => inner.GetOrdersAsync(a, i, s, ct));
    public Task<IReadOnlyList<ExecutionInfo>> GetExecutionsAsync(string a, DateTimeOffset? s, CancellationToken ct = default) => M("executions", () => inner.GetExecutionsAsync(a, s, ct));
    public Task<OrderInfo> PlaceOrderAsync(PlaceOrderCommand c, CancellationToken ct = default) => M("place", () => inner.PlaceOrderAsync(c, ct));
    public Task<OrderInfo> ModifyOrderAsync(ModifyOrderCommand c, CancellationToken ct = default) => M("modify", () => inner.ModifyOrderAsync(c, ct));
    public Task CancelOrderAsync(string id, CancellationToken ct = default) => M("cancel", () => inner.CancelOrderAsync(id, ct));
    public Task<IReadOnlyList<string>> CancelAllOrdersAsync(string a, CancellationToken ct = default) => M("cancel-all", () => inner.CancelAllOrdersAsync(a, ct));
    public Task<OrderInfo?> ClosePositionAsync(string a, string s, string c, CancellationToken ct = default) => M("close", () => inner.ClosePositionAsync(a, s, c, ct));
    public event Action<HealthState>? ConnectionChanged { add => inner.ConnectionChanged += value; remove => inner.ConnectionChanged -= value; }
    public event Action<QuoteInfo>? QuoteChanged { add => inner.QuoteChanged += value; remove => inner.QuoteChanged -= value; }
    public event Action<OrderInfo>? OrderChanged { add => inner.OrderChanged += value; remove => inner.OrderChanged -= value; }
    public event Action<ExecutionInfo>? ExecutionReceived { add => inner.ExecutionReceived += value; remove => inner.ExecutionReceived -= value; }
    public event Action<PositionInfo>? PositionChanged { add => inner.PositionChanged += value; remove => inner.PositionChanged -= value; }
    public event Action<AccountInfo>? AccountChanged { add => inner.AccountChanged += value; remove => inner.AccountChanged -= value; }
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

public class ZRunnerMeasurementTests
{
    static async Task<(long Total, string Spans)> OnePress(int latencyMs)
    {
        var db = TestEnv.NewDb();
        using var dbh = db;
        var sw = new Stopwatch();
        var log = new List<string>();
        var fake = new FakeConnector(new FakeBroker(), new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        var gw = new TradingGateway(db, new TimedConnector(fake, sw, log), new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = fake.Broker.AccountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
        });
        await fake.ConnectAsync();
        await gw.RefreshHealthAsync();
        await gw.PlaceAsync(AgentContext.Operator, "m-1", TestEnv.Buy());
        fake.Faults.LatencyMs = latencyMs;
        log.Clear();
        sw.Restart();
        await gw.OperatorCancelAllAsync();
        sw.Stop();
        return (sw.ElapsedMilliseconds, string.Join(",", log));
    }

    [Fact]
    public async Task Zz_where_does_the_press_spend_its_time_on_this_runner()
    {
        var lines = new List<string>();

        // Control A — a bare 1200 ms timer, five times: how late does this runner deliver one?
        for (var i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            await Task.Delay(1200);
            lines.Add($"timer1200={sw.ElapsedMilliseconds}");
        }

        // Control B — the same press with NO injected latency: the product's own cost, DB included.
        for (var i = 0; i < 3; i++)
        {
            var (t, spans) = await OnePress(0);
            lines.Add($"press0={t}[{spans}]");
        }

        // The measurement — the press the failing test makes.
        for (var i = 0; i < 5; i++)
        {
            var (t, spans) = await OnePress(1200);
            lines.Add($"press1200={t}[{spans}]");
        }

        Assert.Fail("MEASUREMENT " + string.Join(" ", lines));
    }
}

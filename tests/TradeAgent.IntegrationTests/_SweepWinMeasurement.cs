using System.Text.Json;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using TradeAgent.TradeCli;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// TEMPORARY — U-sweep-win. Deliberately fails so that its numbers reach the CI log on all three
/// hosted runners: WHERE the second order goes in
/// <c>SweepRequestIdTests.Two_sweeps_mint_different_ids</c>, which reported
/// <c>attempted=0</c> on windows-latest at the U-wakes merge (run 34146285162) and passes on the
/// other two runners and on the dev Mac.
///
/// It reproduces that test's shape exactly — same fixture, same `LeaveWorking` profile, same pipe,
/// place then cancel-all with nothing in between — and prints, per sweep, the three places the
/// order can be lost, which the reply itself already distinguishes:
///   - `nothing_to_do=true` with no outcomes: the sweep's book READ found no working order, so the
///     fixture assumed a schedule the platform had not reached (the brief's first candidate);
///   - one leg with `not-sent`: the order WAS captured and the leg never went out, because the
///     operation's own two-second deadline was gone by the time its turn came;
///   - anything else: the leg reached the wire and the count is about what came back.
/// Beside them: what the fake broker held at the instant the place returned (in process, no wire,
/// so the observation cannot itself close the window it is looking for), the round trip of each
/// call, and — because every candidate above is paid for in SQLite commits at `synchronous=FULL` —
/// what one bare commit costs on this runner's disk right now, plus the two tick clocks and GC
/// pause that say whether the process was running while it went.
///
/// It carries NO `Timing` trait deliberately, so it runs in the same `Category!=Timing` step, in the
/// same test host and under the same load, as the test that went red. A category that is re-run once
/// would buy a second sample at the price of measuring a quieter machine than the failing one.
///
/// Deleted once U-sweep-win has its answer.
/// </summary>
public class ZSweepWinMeasurementTests
{
    /// <summary>How many envs the harness builds; each does the failing test's two sweeps.</summary>
    const int Pairs = 24;

    static string NewPipe() => "ta-swmeas-" + Guid.NewGuid().ToString("n")[..12];

    /// <summary>Byte-identical to the failing test's own helper, so the fixture is the same one.</summary>
    static IpcRequest Buy(string requestId, string symbol) => new()
    {
        Op = Ops.Buy,
        RequestId = requestId,
        Args = new()
        {
            ["symbol"] = JsonSerializer.SerializeToElement(symbol),
            ["quantity"] = JsonSerializer.SerializeToElement("1"),
            ["limit"] = JsonSerializer.SerializeToElement("1")
        }
    };

    /// <summary>
    /// Two clocks that keep ticking while a pair runs. A gap far above the 20 ms tick is the process
    /// not running, which no round-trip total can tell from a slow step.
    /// </summary>
    sealed class Stall : IDisposable
    {
        volatile bool _stop;
        readonly Thread _thread;
        public long ThreadGap;
        public long PoolGap;

        public Stall()
        {
            _thread = new Thread(() =>
            {
                var last = Environment.TickCount64;
                while (!_stop)
                {
                    Thread.Sleep(20);
                    var gap = Environment.TickCount64 - last;
                    if (gap > ThreadGap) ThreadGap = gap;
                    last = Environment.TickCount64;
                }
            })
            { IsBackground = true };
            _thread.Start();

            _ = Task.Run(async () =>
            {
                var last = Environment.TickCount64;
                while (!_stop)
                {
                    await Task.Delay(20);
                    var gap = Environment.TickCount64 - last;
                    if (gap > PoolGap) PoolGap = gap;
                    last = Environment.TickCount64;
                }
            });
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join(1000);
        }
    }

    /// <summary>
    /// What one durable commit costs on THIS runner's disk right now, in the shape the sweep's own
    /// composite and leg rows are written in: one `Write` transaction each, `synchronous=FULL`, WAL.
    /// </summary>
    static string DiskProbe(Database db, int n)
    {
        var each = new List<long>(n);
        for (var i = 0; i < n; i++)
        {
            var t = Environment.TickCount64;
            db.SetKv($"probe-{Guid.NewGuid():n}", "x");
            each.Add(Environment.TickCount64 - t);
        }
        each.Sort();
        return $"n={n} min={each[0]} med={each[n / 2]} p95={each[(int)(n * 0.95)]} max={each[n - 1]} total={each.Sum()}";
    }

    static string Compact(JsonElement data, string name) =>
        data.TryGetProperty(name, out var v) ? v.ToString() : "?";

    static async Task<string> OnePair(int index)
    {
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        var disk = DiskProbe(db, 40);
        var pause0 = GC.GetTotalPauseDuration();
        using var stall = new Stall();
        var marks = new List<string>();
        var steps = new List<string>();
        GatewayPipeServer.SweepMark = m => { lock (steps) steps.Add(m); };

        async Task SweepOnce(string place, string sweep, string tag)
        {
            var t0 = Environment.TickCount64;
            var placed = await client.SendAsync(Buy(place, "ES")).WaitAsync(TimeSpan.FromSeconds(10));
            var t1 = Environment.TickCount64;

            // IN PROCESS AND UNDER THE BROKER'S OWN LOCK — no wire call, no database read, so the
            // observation adds nothing to the window between the place and the sweep. The book is
            // what the sweep's own read would see: the connector reads this list and filters it.
            var book = string.Join(",", conn.Broker.Orders.Select(o => $"{o.ConnectorOrderId}:{o.State}"));

            var reply = await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = sweep })
                .WaitAsync(TimeSpan.FromSeconds(10));
            var t2 = Environment.TickCount64;

            if (!reply.Ok || reply.Data is not JsonElement data)
            {
                marks.Add($"{tag} placeMs={t1 - t0} book=[{book}] sweepMs={t2 - t1} SWEEP-FAILED={Json.Write(reply.Error)}");
                return;
            }

            var legs = data.TryGetProperty("outcomes", out var o)
                ? string.Join(";", o.EnumerateArray().Select(l =>
                    $"{l.GetProperty("outcome").GetString()}/{(l.TryGetProperty("transport", out var tr) ? tr.ToString() : "-")}" +
                    $"/{(l.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()![..Math.Min(70, e.GetString()!.Length)] : "-")}"))
                : "?";

            string taken;
            lock (steps) { taken = string.Join(" ", steps); steps.Clear(); }

            marks.Add($"{tag} placeOk={placed.Ok} placeMs={t1 - t0} book=[{book}] sweepMs={t2 - t1} " +
                      $"attempted={Compact(data, "attempted")} cancelled={Compact(data, "cancelled")} " +
                      $"nothing_to_do={Compact(data, "nothing_to_do")} not_sent={Compact(data, "not_sent")} " +
                      $"legs=[{legs}] req={gw.GetRequest(place)?.State.ToString() ?? "-"} budget[{taken}]");
        }

        await SweepOnce("nonce-a", "sweep-nonce-1", $"P{index}A");
        await SweepOnce("nonce-b", "sweep-nonce-2", $"P{index}B");

        GatewayPipeServer.SweepMark = null;
        var gcMs = (long)(GC.GetTotalPauseDuration() - pause0).TotalMilliseconds;
        var cancelled = (await gw.OrdersAsync(true)).Count(x => x.State == ExecutionState.CANCELLED);
        return $"disk[{disk}] gcPause={gcMs} threadGap={stall.ThreadGap} poolGap={stall.PoolGap} " +
               $"cancelledAtEnd={cancelled} || {string.Join(" || ", marks)}";
    }

    /// <summary>
    /// THE CONTROL. A connector that answers the sweep's book read correctly and then holds the
    /// caller until the operation's own deadline has gone — which is what one SQLite commit at
    /// `synchronous=FULL` does to this handler on a runner whose disk stalls, since
    /// `BeginCompositeAsync`'s composite row is the ONLY step between the read that found the order
    /// and the leg's turn. Nothing else is changed: the book still holds the order, the read still
    /// returns it, and the reply that comes back is the signature of this one loss point.
    /// </summary>
    sealed class StalledAfterTheBookRead(FakeConnector inner) : ITradingConnector
    {
        public string Id => inner.Id;
        public string DisplayName => "Stalled after the book read";
        public ConnectorCapabilities Capabilities => inner.Capabilities;
        public TimeSpan WorstCaseOperationPath => inner.WorstCaseOperationPath;
        public TimeSpan EmergencyBudget => inner.EmergencyBudget;
        public FakeBroker Broker => inner.Broker;

        public event Action<HealthState>? ConnectionChanged { add => inner.ConnectionChanged += value; remove => inner.ConnectionChanged -= value; }
        public event Action<QuoteInfo>? QuoteChanged { add => inner.QuoteChanged += value; remove => inner.QuoteChanged -= value; }
        public event Action<OrderInfo>? OrderChanged { add => inner.OrderChanged += value; remove => inner.OrderChanged -= value; }
        public event Action<ExecutionInfo>? ExecutionReceived { add => inner.ExecutionReceived += value; remove => inner.ExecutionReceived -= value; }
        public event Action<PositionInfo>? PositionChanged { add => inner.PositionChanged += value; remove => inner.PositionChanged -= value; }
        public event Action<AccountInfo>? AccountChanged { add => inner.AccountChanged += value; remove => inner.AccountChanged -= value; }

        public Task ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);
        public Task<HealthState> GetHealthAsync(CancellationToken ct = default) => inner.GetHealthAsync(ct);
        public Task<bool> IsConnectedAsync(CancellationToken ct = default) => inner.IsConnectedAsync(ct);
        public Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken ct = default) => inner.GetAccountsAsync(ct);
        public Task<AccountInfo?> GetAccountAsync(string a, CancellationToken ct = default) => inner.GetAccountAsync(a, ct);
        public Task<IReadOnlyList<InstrumentInfo>> GetInstrumentsAsync(CancellationToken ct = default) => inner.GetInstrumentsAsync(ct);
        public Task<QuoteInfo?> GetQuoteAsync(string s, CancellationToken ct = default) => inner.GetQuoteAsync(s, ct);
        public Task<IReadOnlyList<PositionInfo>> GetPositionsAsync(string a, CancellationToken ct = default) => inner.GetPositionsAsync(a, ct);
        public Task<IReadOnlyList<ExecutionInfo>> GetExecutionsAsync(string a, DateTimeOffset? s, CancellationToken ct = default) => inner.GetExecutionsAsync(a, s, ct);
        public Task<OrderInfo> PlaceOrderAsync(PlaceOrderCommand c, CancellationToken ct = default) => inner.PlaceOrderAsync(c, ct);
        public Task<OrderInfo> ModifyOrderAsync(ModifyOrderCommand c, CancellationToken ct = default) => inner.ModifyOrderAsync(c, ct);
        public Task CancelOrderAsync(string id, CancellationToken ct = default) => inner.CancelOrderAsync(id, ct);
        public Task<IReadOnlyList<string>> CancelAllOrdersAsync(string a, CancellationToken ct = default) => inner.CancelAllOrdersAsync(a, ct);
        public Task<OrderInfo?> ClosePositionAsync(string a, string s, string c, CancellationToken ct = default) => inner.ClosePositionAsync(a, s, c, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();

        public async Task<IReadOnlyList<OrderInfo>> GetOrdersAsync(string a, bool includeInactive, DateTimeOffset? s, CancellationToken ct = default)
        {
            var book = await inner.GetOrdersAsync(a, includeInactive, s, ct);
            if (!includeInactive && RiskReducingScope.DeadlineAt is { } d)
                while (Environment.TickCount64 <= d + 20) await Task.Delay(5, CancellationToken.None);
            return book;
        }
    }

    static async Task<string> TheControl()
    {
        var db = TestEnv.NewDb();
        using var _1 = db;
        var fake = new FakeConnector(new FakeBroker(), new FaultProfile { Fill = FillBehaviour.LeaveWorking })
        {
            // Small on purpose: the control is about WHICH answer comes back, not how long it takes.
            EmergencyBudget = TimeSpan.FromMilliseconds(400)
        };
        var conn = new StalledAfterTheBookRead(fake);
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = fake.Broker.AccountId;
            s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments];
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        var placed = await client.SendAsync(Buy("ctl-a", "ES")).WaitAsync(TimeSpan.FromSeconds(10));
        var book = string.Join(",", fake.Broker.Orders.Select(o => $"{o.ConnectorOrderId}:{o.State}"));
        var reply = await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "ctl-sweep" })
            .WaitAsync(TimeSpan.FromSeconds(20));
        if (!reply.Ok || reply.Data is not JsonElement data)
            return $"CONTROL placeOk={placed.Ok} book=[{book}] SWEEP-FAILED={Json.Write(reply.Error)}";

        return $"CONTROL placeOk={placed.Ok} book=[{book}] attempted={Compact(data, "attempted")} " +
               $"cancelled={Compact(data, "cancelled")} nothing_to_do={Compact(data, "nothing_to_do")} " +
               $"not_sent={Compact(data, "not_sent")} outcomes={Compact(data, "outcomes")}";
    }

    [Fact]
    public async Task Zz_where_the_second_sweep_loses_its_order()
    {
        var lines = new List<string> { await TheControl() };
        for (var i = 0; i < Pairs; i++) lines.Add(await OnePair(i));
        Assert.Fail("MEASUREMENT || " + string.Join(" |||| ", lines));
    }
}

using System.Diagnostics;
using System.Text.Json;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Atas;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using TradeAgent.TradeCli;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>ADVERSARIAL VERIFY round 9, leg [2]. Not proposed for the branch.</summary>
public class VerifyR9Probes(ITestOutputHelper o)
{
    static string NewPipe() => "ta-vr9-" + Guid.NewGuid().ToString("n")[..12];

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

    static async Task<(TradingGateway Gw, FakeConnector Conn, Database Db)> ReadyWithBudget(
        TimeSpan budget, int latencyMs = 0)
    {
        var db = TestEnv.NewDb();
        var conn = new FakeConnector(new FakeBroker(), new FaultProfile { Fill = FillBehaviour.LeaveWorking })
        {
            EmergencyBudget = budget
        };
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 20;
            s.Risk.MaxOrdersPerMinute = 200;
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        conn.Faults.LatencyMs = latencyMs;
        return (gw, conn, db);
    }

    void Dump(JsonElement data, TradingGateway gw, string tag)
    {
        o.WriteLine($"--- {tag} ---");
        foreach (var p in new[] { "cancelled", "attempted", "not_sent" })
            if (data.TryGetProperty(p, out var v)) o.WriteLine($"  {p} = {v}");
        foreach (var leg in data.GetProperty("outcomes").EnumerateArray())
        {
            var word = leg.GetProperty("outcome").GetString();
            var state = leg.TryGetProperty("state", out var st) ? st.GetString() : null;
            var id = leg.GetProperty("request_id").GetString();
            var err = leg.TryGetProperty("error", out var e) ? e.GetString() : null;
            var rec = gw.Requests.Get(id!);
            o.WriteLine($"  {word,-20} state={state ?? "(none)",-16} reconcile={rec?.NeedsReconciliation.ToString() ?? "-",-5} err={err}");
        }
        o.WriteLine($"  needing reconciliation = {gw.Requests.NeedingReconciliation().Count}");
    }

    // ================================================================ target 1 + 7

    /// <summary>
    /// R9P1. THE BRIEF'S OWN ACCEPTANCE: five orders, a one-second-per-call simulator, a two-second
    /// operation. The answer must arrive at about two seconds, every leg must be named, and — this
    /// is the round-9 claim under test — the WORD on each leg must be true of that leg.
    ///
    /// Written to PASS if the builder's rule holds: `sent-not-confirmed` is documented as "it
    /// reached the wire, or may have; UNKNOWN + reconciliation". A leg whose own recorded error says
    /// the connector refused it BEFORE sending is not that, whatever the record says.
    /// </summary>
    [Fact]
    public async Task R9P1_five_order_sweep_every_word_is_true_of_its_leg()
    {
        var (gw, conn, db) = await ReadyWithBudget(TimeSpan.FromSeconds(2));
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        foreach (var sym in new[] { "ES", "NQ", "ES", "NQ", "ES" })
            Assert.True((await client.SendAsync(Buy($"r9p1-{Guid.NewGuid():n}", sym)).WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        Assert.Equal(5, (await gw.OrdersAsync(false)).Count);

        conn.Faults.LatencyMs = 1000;

        var timer = Stopwatch.StartNew();
        var reply = await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "r9p1-sweep" })
            .WaitAsync(TimeSpan.FromSeconds(30));
        timer.Stop();
        o.WriteLine($"sweep answered in {timer.Elapsed.TotalMilliseconds:0} ms");

        var data = (JsonElement)reply.Data!;
        Dump(data, gw, "five legs, 1 s each, 2 s budget");

        // The word has to be true of the leg. A leg the connector refused before sending is not
        // "sent-not-confirmed" whatever the gateway wrote down for it.
        var lying = data.GetProperty("outcomes").EnumerateArray()
            .Where(l => l.GetProperty("outcome").GetString() == "sent-not-confirmed")
            .Where(l => (l.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "")
                .Contains("nothing was sent", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(lying.Count == 0,
            $"{lying.Count} leg(s) read 'sent-not-confirmed' while their own recorded error says nothing was sent: " +
            string.Join(" | ", lying.Select(l => l.GetProperty("error").GetString())));
    }

    /// <summary>
    /// R9P2. THE OTHER DIRECTION. Five legs that comfortably fit must all land, so a sweep that
    /// refuses everything is not a passing sweep.
    /// </summary>
    [Fact]
    public async Task R9P2_a_sweep_that_fits_confirms_every_leg()
    {
        var (gw, conn, db) = await ReadyWithBudget(TimeSpan.FromSeconds(2));
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        foreach (var sym in new[] { "ES", "NQ", "ES", "NQ", "ES" })
            Assert.True((await client.SendAsync(Buy($"r9p2-{Guid.NewGuid():n}", sym)).WaitAsync(TimeSpan.FromSeconds(10))).Ok);

        conn.Faults.LatencyMs = 100;

        var timer = Stopwatch.StartNew();
        var reply = await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "r9p2-sweep" })
            .WaitAsync(TimeSpan.FromSeconds(30));
        timer.Stop();
        o.WriteLine($"sweep answered in {timer.Elapsed.TotalMilliseconds:0} ms");

        var data = (JsonElement)reply.Data!;
        Dump(data, gw, "five legs, 100 ms each, 2 s budget");

        var words = data.GetProperty("outcomes").EnumerateArray()
            .Select(l => l.GetProperty("outcome").GetString()).ToList();
        Assert.True(words.Count == 5, $"{words.Count} legs");
        Assert.All(words, w => Assert.Equal("sent-and-confirmed", w));
        Assert.Equal(5, data.GetProperty("cancelled").GetInt32());
        Assert.Equal(0, gw.Requests.NeedingReconciliation().Count);
    }

    /// <summary>
    /// R9P3. CODEX'S OWN CHECK, on my own fixture: three replies delayed 1.9 s each (the orders
    /// read, the target resolution, the cancel) must cost about two seconds in total, not 5.7.
    /// </summary>
    [Fact]
    public async Task R9P3_three_slow_legs_cost_one_budget()
    {
        var (gw, conn, db) = await ReadyWithBudget(TimeSpan.FromSeconds(2));
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        Assert.True((await client.SendAsync(Buy("r9p3-a", "ES")).WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        conn.Faults.LatencyMs = 1900;

        var timer = Stopwatch.StartNew();
        var reply = await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "r9p3-sweep" })
            .WaitAsync(TimeSpan.FromSeconds(30));
        timer.Stop();
        o.WriteLine($"IPC cancel-all with three 1.9 s calls answered in {timer.Elapsed.TotalMilliseconds:0} ms");
        Dump((JsonElement)reply.Data!, gw, "three 1.9 s calls");

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3),
            $"the sweep took {timer.Elapsed.TotalSeconds:0.00}s against a two-second operation");
    }

    // ================================================================ target 8 — the longest chain

    /// <summary>
    /// A connector that records which calls it was asked for, in order. My own, not the builder's.
    /// </summary>
    sealed class CountingConnector(FakeConnector inner) : ITradingConnector
    {
        public readonly List<string> Calls = new();
        void Note(string c) { lock (Calls) Calls.Add(c); }

        public string Id => inner.Id;
        public string DisplayName => inner.DisplayName;
        public ConnectorCapabilities Capabilities => inner.Capabilities;
        public TimeSpan WorstCaseOperationPath => inner.WorstCaseOperationPath;
        public TimeSpan EmergencyBudget => inner.EmergencyBudget;
        public FakeConnector Inner => inner;

        public event Action<HealthState>? ConnectionChanged { add => inner.ConnectionChanged += value; remove => inner.ConnectionChanged -= value; }
        public event Action<QuoteInfo>? QuoteChanged { add => inner.QuoteChanged += value; remove => inner.QuoteChanged -= value; }
        public event Action<OrderInfo>? OrderChanged { add => inner.OrderChanged += value; remove => inner.OrderChanged -= value; }
        public event Action<ExecutionInfo>? ExecutionReceived { add => inner.ExecutionReceived += value; remove => inner.ExecutionReceived -= value; }
        public event Action<PositionInfo>? PositionChanged { add => inner.PositionChanged += value; remove => inner.PositionChanged -= value; }
        public event Action<AccountInfo>? AccountChanged { add => inner.AccountChanged += value; remove => inner.AccountChanged -= value; }

        public Task ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);
        public Task<HealthState> GetHealthAsync(CancellationToken ct = default) => inner.GetHealthAsync(ct);
        public Task<bool> IsConnectedAsync(CancellationToken ct = default) => inner.IsConnectedAsync(ct);
        public Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken ct = default) { Note("accounts"); return inner.GetAccountsAsync(ct); }
        public Task<AccountInfo?> GetAccountAsync(string id, CancellationToken ct = default) { Note("account"); return inner.GetAccountAsync(id, ct); }
        public Task<IReadOnlyList<InstrumentInfo>> GetInstrumentsAsync(CancellationToken ct = default) { Note("instruments"); return inner.GetInstrumentsAsync(ct); }
        public Task<QuoteInfo?> GetQuoteAsync(string s, CancellationToken ct = default) { Note("quote"); return inner.GetQuoteAsync(s, ct); }
        public Task<IReadOnlyList<PositionInfo>> GetPositionsAsync(string a, CancellationToken ct = default) { Note("positions"); return inner.GetPositionsAsync(a, ct); }
        public Task<IReadOnlyList<OrderInfo>> GetOrdersAsync(string a, bool inactive, DateTimeOffset? since, CancellationToken ct = default) { Note("orders"); return inner.GetOrdersAsync(a, inactive, since, ct); }
        public Task<IReadOnlyList<ExecutionInfo>> GetExecutionsAsync(string a, DateTimeOffset? since, CancellationToken ct = default) { Note("executions"); return inner.GetExecutionsAsync(a, since, ct); }
        public Task<OrderInfo> PlaceOrderAsync(PlaceOrderCommand c, CancellationToken ct = default) { Note("place"); return inner.PlaceOrderAsync(c, ct); }
        public Task<OrderInfo> ModifyOrderAsync(ModifyOrderCommand c, CancellationToken ct = default) { Note("modify"); return inner.ModifyOrderAsync(c, ct); }
        public Task CancelOrderAsync(string id, CancellationToken ct = default) { Note("cancel"); return inner.CancelOrderAsync(id, ct); }
        public Task<IReadOnlyList<string>> CancelAllOrdersAsync(string a, CancellationToken ct = default) { Note("cancelall"); return inner.CancelAllOrdersAsync(a, ct); }
        public Task<OrderInfo?> ClosePositionAsync(string a, string s, string coid, CancellationToken ct = default) { Note("close"); return inner.ClosePositionAsync(a, s, coid, ct); }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    async Task<(TradingGateway Gw, CountingConnector Conn, Database Db)> ReadyCounting(
        Action<TradeAgentSettings>? extra = null)
    {
        var db = TestEnv.NewDb();
        var inner = new FakeConnector(new FakeBroker(), new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        var conn = new CountingConnector(inner);
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = inner.Broker.AccountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 20;
            s.Risk.MaxOrdersPerMinute = 200;
            s.Risk.InstrumentAllowlist = new List<string> { "ES", "NQ" };
            extra?.Invoke(s);
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        return (gw, conn, db);
    }

    /// <summary>
    /// R9P4. THE LONGEST ORDINARY CHAIN, COUNTED MYSELF over the real pipe — for a cold buy, and for
    /// every other ordinary mutating handler I can reach. The claim is that five covers all of them.
    /// </summary>
    [Fact]
    public async Task R9P4_no_ordinary_handler_issues_more_calls_than_the_drain_assumes()
    {
        var (gw, conn, db) = await ReadyCounting();
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        lock (conn.Calls) conn.Calls.Clear();
        var placed = await client.SendAsync(Buy("r9p4-cold", "ES")).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(placed.Ok, Json.Write(placed.Error));
        List<string> cold;
        lock (conn.Calls) cold = conn.Calls.ToList();
        o.WriteLine($"COLD buy   : {cold.Count} calls -> {string.Join(" -> ", cold)}");

        lock (conn.Calls) conn.Calls.Clear();
        var warm = await client.SendAsync(Buy("r9p4-warm", "ES")).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(warm.Ok, Json.Write(warm.Error));
        List<string> warmCalls;
        lock (conn.Calls) warmCalls = conn.Calls.ToList();
        o.WriteLine($"WARM buy   : {warmCalls.Count} calls -> {string.Join(" -> ", warmCalls)}");

        // modify: the shape round 8 assumed was the longest.
        lock (conn.Calls) conn.Calls.Clear();
        var order = (await gw.OrdersAsync(false)).First();
        var mod = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Modify,
            RequestId = "r9p4-mod",
            Args = new()
            {
                ["order"] = JsonSerializer.SerializeToElement(order.ConnectorOrderId),
                ["limit"] = JsonSerializer.SerializeToElement("2")
            }
        }).WaitAsync(TimeSpan.FromSeconds(10));
        List<string> modCalls;
        lock (conn.Calls) modCalls = conn.Calls.ToList();
        o.WriteLine($"modify     : {modCalls.Count} calls -> {string.Join(" -> ", modCalls)} (ok={mod.Ok})");

        o.WriteLine($"SerialConnectorCallsPerHandler = {GatewayPipeServer.SerialConnectorCallsPerHandler}");
        Assert.True(cold.Count <= GatewayPipeServer.SerialConnectorCallsPerHandler,
            $"a cold placement issues {cold.Count} connector calls in series against a drain derived from " +
            $"{GatewayPipeServer.SerialConnectorCallsPerHandler}: {string.Join(" -> ", cold)}");
        Assert.True(modCalls.Count <= GatewayPipeServer.SerialConnectorCallsPerHandler,
            $"modify issues {modCalls.Count} calls: {string.Join(" -> ", modCalls)}");
    }

    /// <summary>
    /// R9P5. THE COLD CHAIN ON THE OTHER CONFIGURATION. The builder's count is taken with an
    /// instrument allowlist set, saying that without one the health refresh warms the cache and the
    /// chain is four. A bound that is only correct on one configuration is worth knowing about, so
    /// this counts the un-allowlisted install too — and, more to the point, counts a placement made
    /// before ANY health refresh has run, which is the coldest a process gets.
    /// </summary>
    [Fact]
    public async Task R9P5_the_coldest_placement_there_is()
    {
        var db = TestEnv.NewDb();
        using var _1 = db;
        var inner = new FakeConnector(new FakeBroker(), new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        var conn = new CountingConnector(inner);
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = inner.Broker.AccountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 20;
            s.Risk.MaxOrdersPerMinute = 200;
        });
        await conn.ConnectAsync();
        // NO RefreshHealthAsync — nothing has warmed anything.
        lock (conn.Calls) conn.Calls.Clear();

        try
        {
            await gw.PlaceAsync(AgentContext.Operator, "r9p5-cold",
                new PlaceIntent("ES", OrderSide.Buy, OrderType.Limit, 1m, 1m, null, TimeInForce.Day, null));
        }
        catch (Exception ex) { o.WriteLine($"(placement refused: {ex.Message})"); }

        List<string> calls;
        lock (conn.Calls) calls = conn.Calls.ToList();
        o.WriteLine($"coldest buy, no allowlist, no health refresh: {calls.Count} -> {string.Join(" -> ", calls)}");
        Assert.True(calls.Count <= GatewayPipeServer.SerialConnectorCallsPerHandler,
            $"{calls.Count} calls against a drain derived from {GatewayPipeServer.SerialConnectorCallsPerHandler}");
    }

    // ================================================================ target 2 — the drain's price

    /// <summary>
    /// R9P6. THE MANAGER'S PRICE, CHECKED: 255 s is claimed to be paid ONLY while a request is
    /// genuinely in flight, and an idle shutdown is claimed to be fast. This connects a client,
    /// leaves it idle, and disposes the server with a drain derived from a 100 s worst path.
    /// </summary>
    [Fact]
    public async Task R9P6_an_idle_shutdown_does_not_pay_the_drain()
    {
        var db = TestEnv.NewDb();
        using var _1 = db;
        var conn = new FakeConnector(new FakeBroker(), new FaultProfile { Fill = FillBehaviour.LeaveWorking })
        {
            WorstCaseOperationPath = TimeSpan.FromSeconds(100)
        };
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        var pipe = NewPipe();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        // One completed round trip, so the handler is real and running, then nothing.
        Assert.True((await client.SendAsync(new IpcRequest { Op = Ops.Status, RequestId = "r9p6-a" })
            .WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        await Task.Delay(200);

        var timer = Stopwatch.StartNew();
        await server.DisposeAsync();
        timer.Stop();
        o.WriteLine($"idle disposal took {timer.Elapsed.TotalMilliseconds:0} ms " +
                    $"(drain would be 5 x 100 + 5 = 505 s)");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10),
            $"an idle shutdown took {timer.Elapsed.TotalSeconds:0.0}s");
    }

    /// <summary>
    /// R9P7. THE OVERRIDE CANNOT SHORTEN — AND CAN STILL LENGTHEN. Both directions, read off the
    /// property rather than off a comment.
    /// </summary>
    [Fact]
    public async Task R9P7_the_override_only_lengthens()
    {
        var db = TestEnv.NewDb();
        using var _1 = db;
        var conn = new FakeConnector(new FakeBroker()) { WorstCaseOperationPath = TimeSpan.FromSeconds(60) };
        var gw = new TradingGateway(db, conn, new HealthRegistry());

        var derived = new GatewayPipeServer(gw, IpcToken.Ensure(), NewPipe());
        var shortened = new GatewayPipeServer(gw, IpcToken.Ensure(), NewPipe()) { HandlerDrainTimeout = TimeSpan.FromSeconds(7) };
        var lengthened = new GatewayPipeServer(gw, IpcToken.Ensure(), NewPipe()) { HandlerDrainTimeout = TimeSpan.FromSeconds(900) };
        var zeroSettle = new GatewayPipeServer(gw, IpcToken.Ensure(), NewPipe()) { SettleAfterCancelTimeout = TimeSpan.Zero };

        o.WriteLine($"derived        = {derived.HandlerDrainTimeout}");
        o.WriteLine($"asked for 7 s  = {shortened.HandlerDrainTimeout}");
        o.WriteLine($"asked for 900 s= {lengthened.HandlerDrainTimeout}");
        o.WriteLine($"settle = 0      = {zeroSettle.HandlerDrainTimeout}");

        var chain = GatewayPipeServer.SerialConnectorCallsPerHandler * conn.WorstCaseOperationPath;
        Assert.True(shortened.HandlerDrainTimeout >= chain, $"a caller shortened the drain to {shortened.HandlerDrainTimeout}");
        Assert.True(zeroSettle.HandlerDrainTimeout >= chain, $"a zero settle shortened the drain to {zeroSettle.HandlerDrainTimeout}");
        Assert.Equal(TimeSpan.FromSeconds(900), lengthened.HandlerDrainTimeout);

        await derived.DisposeAsync();
        await shortened.DisposeAsync();
        await lengthened.DisposeAsync();
        await zeroSettle.DisposeAsync();
    }

    // ================================================================ target 2 / 7 — the sixth state

    /// <summary>
    /// A connector that honours its cancellation token and refuses a send once the operation's
    /// deadline has passed — the two behaviours `AtasConnector` has. The refusal text and the check
    /// are copied verbatim from `AtasConnector.Rpc` (src/TradeAgent.Connectors.Atas/AtasConnector.cs
    /// lines 1043-1050), so what is modelled here is the real connector's pre-gate branch, not an
    /// invented failure.
    /// </summary>
    sealed class PreGateRefusingConnector(FakeConnector inner, TimeSpan readDelay) : ITradingConnector
    {
        public string Id => inner.Id;
        public string DisplayName => inner.DisplayName;
        public ConnectorCapabilities Capabilities => inner.Capabilities;
        public TimeSpan WorstCaseOperationPath => inner.WorstCaseOperationPath;
        public TimeSpan EmergencyBudget => inner.EmergencyBudget;
        public FakeConnector Inner => inner;

        public event Action<HealthState>? ConnectionChanged { add => inner.ConnectionChanged += value; remove => inner.ConnectionChanged -= value; }
        public event Action<QuoteInfo>? QuoteChanged { add => inner.QuoteChanged += value; remove => inner.QuoteChanged -= value; }
        public event Action<OrderInfo>? OrderChanged { add => inner.OrderChanged += value; remove => inner.OrderChanged -= value; }
        public event Action<ExecutionInfo>? ExecutionReceived { add => inner.ExecutionReceived += value; remove => inner.ExecutionReceived -= value; }
        public event Action<PositionInfo>? PositionChanged { add => inner.PositionChanged += value; remove => inner.PositionChanged -= value; }
        public event Action<AccountInfo>? AccountChanged { add => inner.AccountChanged += value; remove => inner.AccountChanged -= value; }

        public Task ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        public Task<HealthState> GetHealthAsync(CancellationToken ct = default) => inner.GetHealthAsync(ct);
        public Task<bool> IsConnectedAsync(CancellationToken ct = default) => inner.IsConnectedAsync(ct);
        public Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken ct = default) => inner.GetAccountsAsync(ct);
        public Task<AccountInfo?> GetAccountAsync(string id, CancellationToken ct = default) => inner.GetAccountAsync(id, ct);
        public Task<IReadOnlyList<InstrumentInfo>> GetInstrumentsAsync(CancellationToken ct = default) => inner.GetInstrumentsAsync(ct);
        public Task<QuoteInfo?> GetQuoteAsync(string s, CancellationToken ct = default) => inner.GetQuoteAsync(s, ct);
        public Task<IReadOnlyList<PositionInfo>> GetPositionsAsync(string a, CancellationToken ct = default) => inner.GetPositionsAsync(a, ct);
        public Task<IReadOnlyList<ExecutionInfo>> GetExecutionsAsync(string a, DateTimeOffset? since, CancellationToken ct = default) => inner.GetExecutionsAsync(a, since, ct);
        public Task<OrderInfo> PlaceOrderAsync(PlaceOrderCommand c, CancellationToken ct = default) => inner.PlaceOrderAsync(c, ct);
        public Task<OrderInfo> ModifyOrderAsync(ModifyOrderCommand c, CancellationToken ct = default) => inner.ModifyOrderAsync(c, ct);
        public Task<IReadOnlyList<string>> CancelAllOrdersAsync(string a, CancellationToken ct = default) => inner.CancelAllOrdersAsync(a, ct);
        public Task<OrderInfo?> ClosePositionAsync(string a, string s, string coid, CancellationToken ct = default) => inner.ClosePositionAsync(a, s, coid, ct);

        /// <summary>A read that answers just inside the deadline rather than failing on it.</summary>
        public async Task<IReadOnlyList<OrderInfo>> GetOrdersAsync(string a, bool inactive, DateTimeOffset? since, CancellationToken ct = default)
        {
            var r = await inner.GetOrdersAsync(a, inactive, since, ct);
            // The reply lands, and THEN the deadline passes — the interleaving a real reply arriving
            // at deadline-minus-a-millisecond produces.
            await Task.Delay(readDelay, ct);
            return r;
        }

        /// <summary>`AtasConnector.Rpc`'s pre-gate branch: past the deadline, nothing is sent.</summary>
        public Task CancelOrderAsync(string id, CancellationToken ct = default)
        {
            if (RiskReducingScope.DeadlineAt is { } d && RiskReducingScope.LeftUntil(d) <= TimeSpan.Zero)
                throw new ConnectorTransportException(
                    "'cancel' is NOT confirmed — check your positions and orders in ATAS. It was not sent: " +
                    "the operation ran out of time before this leg's turn came.");
            return inner.CancelOrderAsync(id, ct);
        }
    }

    /// <summary>
    /// R9P8. A LEG THE CONNECTOR REFUSED BEFORE SENDING, AFTER ITS RECORD EXISTS.
    ///
    /// Codex round-8 F1 was "a pre-send failure reads sent-not-confirmed". The fix reads the word off
    /// the record — which closes the route where no record exists, and leaves this one: the target
    /// resolution lands just inside the deadline, `TryCreate` runs, the connector then refuses the
    /// send because the operation is over, and `TradingGateway.CancelAsync` maps every
    /// `ConnectorTransportException` to UNKNOWN.
    ///
    /// Written to PASS if `sent-not-confirmed` means what the code says it means.
    /// </summary>
    [Fact]
    public async Task R9P8_a_leg_refused_before_the_send_does_not_read_as_sent()
    {
        var db = TestEnv.NewDb();
        using var _1 = db;
        var inner = new FakeConnector(new FakeBroker(), new FaultProfile { Fill = FillBehaviour.LeaveWorking })
        {
            EmergencyBudget = TimeSpan.FromSeconds(2)
        };
        var conn = new PreGateRefusingConnector(inner, TimeSpan.FromMilliseconds(1200));
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = inner.Broker.AccountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 20;
            s.Risk.MaxOrdersPerMinute = 200;
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        Assert.True((await client.SendAsync(Buy("r9p8-a", "ES")).WaitAsync(TimeSpan.FromSeconds(20))).Ok);

        var timer = Stopwatch.StartNew();
        var reply = await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "r9p8-sweep" })
            .WaitAsync(TimeSpan.FromSeconds(30));
        timer.Stop();
        o.WriteLine($"sweep answered in {timer.Elapsed.TotalMilliseconds:0} ms");

        var data = (JsonElement)reply.Data!;
        Dump(data, gw, "one leg, resolution inside the deadline, send refused after it");

        var legs = data.GetProperty("outcomes").EnumerateArray().ToList();
        var claimedSent = legs.Where(l => l.GetProperty("outcome").GetString() is "sent-not-confirmed").ToList();
        Assert.True(claimedSent.Count == 0,
            $"{claimedSent.Count} leg(s) read 'sent-not-confirmed' — 'it reached the wire, or may have; UNKNOWN + " +
            "reconciliation' — for a leg the connector refused before sending: " +
            string.Join(" | ", claimedSent.Select(l => l.GetProperty("error").GetString())));
    }

    /// <summary>
    /// R9P9. DISPOSAL, A CONNECTOR THAT HONOURS ITS TOKEN, AND AN ORDINARY HANDLER.
    ///
    /// The round-5 rule as round 9 restates it: disposal never returns with a request unsettled, and
    /// the one remaining exit is "a call that does not honour its cancellation token", logged at
    /// error. This connector DOES honour it. Written to PASS if the rule holds.
    /// </summary>
    [Fact]
    public async Task R9P9_disposal_leaves_no_request_dispatching()
    {
        var db = TestEnv.NewDb();
        using var _1 = db;
        var conn = new FakeConnector(new FakeBroker(), new FaultProfile { Fill = FillBehaviour.LeaveWorking })
        {
            // A connector that under-reports its own worst case — the builder's own fixture shape for
            // "a vendor SDK call that blocks for longer than the vendor admits".
            WorstCaseOperationPath = TimeSpan.FromMilliseconds(100),
            EmergencyBudget = TimeSpan.FromMilliseconds(100)
        };
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 20;
            s.Risk.MaxOrdersPerMinute = 200;
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        var pipe = NewPipe();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe)
        {
            SettleAfterCancelTimeout = TimeSpan.FromMilliseconds(200)
        };
        o.WriteLine($"derived drain = {server.HandlerDrainTimeout.TotalMilliseconds:0} ms");
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        Assert.True((await client.SendAsync(Buy("r9p9-a", "ES")).WaitAsync(TimeSpan.FromSeconds(20))).Ok);
        var order = (await gw.OrdersAsync(false)).First();

        conn.Faults.LatencyMs = 3000;   // every call is slow now, and cancellable

        var modify = client.SendAsync(new IpcRequest
        {
            Op = Ops.Modify,
            RequestId = "r9p9-mod",
            Args = new()
            {
                ["id"] = JsonSerializer.SerializeToElement(order.ConnectorOrderId),
                ["limit"] = JsonSerializer.SerializeToElement("2")
            }
        });

        // Wait until the record exists and has reached the wire, so disposal lands mid-dispatch.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && gw.Requests.Get("r9p9-mod")?.State != ExecutionState.DISPATCHING)
            await Task.Delay(25);
        o.WriteLine($"state when disposal starts = {gw.Requests.Get("r9p9-mod")?.State.ToString() ?? "(none)"}");

        var timer = Stopwatch.StartNew();
        await server.DisposeAsync();
        timer.Stop();
        _ = modify.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);

        var rec = gw.Requests.Get("r9p9-mod");
        o.WriteLine($"disposal returned after {timer.Elapsed.TotalMilliseconds:0} ms");
        o.WriteLine($"DISPATCHING rows = {Dispatching(db)}");
        o.WriteLine($"handlers_did_not_finish = {ReadEngineering(db, "handlers_did_not_finish") ?? "(not logged)"}");
        o.WriteLine($"op_failed               = {ReadEngineering(db, "op_failed") ?? "(not logged)"}");
        o.WriteLine($"record = {rec?.State.ToString() ?? "(none)"}  reconcile={rec?.NeedsReconciliation}  err={rec?.LastError}");
        o.WriteLine($"needing reconciliation = {gw.Requests.NeedingReconciliation().Count}");

        Assert.True(rec is null || rec.State != ExecutionState.DISPATCHING,
            $"disposal returned with request r9p9-mod still DISPATCHING and needs_reconciliation=" +
            $"{rec?.NeedsReconciliation} — nothing will ever reconcile it");
    }

    static int Dispatching(Database db) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT COUNT(*) FROM execution_request WHERE execution_state='DISPATCHING'");
        return Convert.ToInt32(c.ExecuteScalar());
    });

    static string? ReadEngineering(Database db, string @event) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT severity FROM engineering_log WHERE event=$e ORDER BY id LIMIT 1", ("$e", @event));
        using var r = c.ExecuteReader();
        return r.Read() ? r.GetString(0) : null;
    });

    // ================================================================ the real connector

    static BridgeCredential Cred() => new(new string('a', 64), Environment.ProcessPath ?? "");

    static async Task Wait(Func<Task<bool>> c, int ms = 20_000)
    {
        var d = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < d) { if (await c()) return; await Task.Delay(25); }
        throw new TimeoutException("condition was not met in time");
    }

    /// <summary>
    /// R9P10. IS THE PRE-GATE REFUSAL R9P8 MODELS A REAL BRANCH? Against the real `AtasConnector`
    /// over a real pipe, with an operation deadline that has already passed: what does a mutating
    /// call throw, and is the connection left alone?
    /// </summary>
    [Fact]
    public async Task R9P10_the_real_connector_refuses_a_leg_whose_turn_came_late()
    {
        var bridgePipe = NewPipe();
        await using var connector = new AtasConnector(bridgePipe, TimeSpan.FromSeconds(10), Cred());
        await connector.ConnectAsync();
        await using var peer = await VerifyR7Probes.DeadPeer.Connect(bridgePipe, Cred().Secret);
        await Wait(async () => await connector.IsConnectedAsync());

        string? message = null;
        using (RiskReducingScope.Begin(TimeSpan.FromMilliseconds(50)))
        {
            await Task.Delay(200);                       // the operation is over before the leg's turn
            try { await connector.CancelOrderAsync("FB-1"); }
            catch (ConnectorTransportException ex) { message = ex.Message; }
        }

        o.WriteLine($"message   = {message}");
        o.WriteLine($"connected = {await connector.IsConnectedAsync()}");
        Assert.NotNull(message);
        Assert.Contains("It was not sent", message);
        Assert.True(await connector.IsConnectedAsync(), "the connection was dropped by a leg that learned nothing about it");
    }

    /// <summary>
    /// R9P11. `_abandoned` AFTER THE TWO EXITS THAT USED TO LEAK IT (round-9 F2), measured on my own
    /// fixture: an emergency times out, and then the grace is ended early — once by the peer going
    /// away, once by disposing the connector.
    /// </summary>
    [Theory]
    [InlineData("peer-disconnects")]
    [InlineData("connector-disposed")]
    public async Task R9P11_nothing_is_left_awaiting_a_late_answer(string how)
    {
        var bridgePipe = NewPipe();
        var connector = new AtasConnector(bridgePipe, TimeSpan.FromSeconds(10), Cred())
        {
            EmergencyDeadline = TimeSpan.FromMilliseconds(500)
        };
        await connector.ConnectAsync();
        var peer = await VerifyR7Probes.DeadPeer.Connect(bridgePipe, Cred().Secret);
        await Wait(async () => await connector.IsConnectedAsync());
        peer.MuteOnly(BridgeOps.Cancel);

        try { await connector.CancelOrderAsync("FB-1"); }
        catch (ConnectorTransportException ex) { o.WriteLine($"caller: {ex.Message}"); }

        o.WriteLine($"awaiting a late answer right after the caller gave up = {connector.AwaitingLateAnswer}");
        Assert.Equal(1, connector.AwaitingLateAnswer);

        if (how == "peer-disconnects")
        {
            await peer.DisposeAsync();
            await Wait(() => Task.FromResult(connector.AwaitingLateAnswer == 0), 15_000);
            o.WriteLine($"after the peer goes away: {connector.AwaitingLateAnswer}");
            await connector.DisposeAsync();
        }
        else
        {
            await connector.DisposeAsync();
            await Wait(() => Task.FromResult(connector.AwaitingLateAnswer == 0), 15_000);
            o.WriteLine($"after disposal: {connector.AwaitingLateAnswer}");
            await peer.DisposeAsync();
        }

        Assert.Equal(0, connector.AwaitingLateAnswer);
    }

    // ================================================================ the close-all wave and the drain

    /// <summary>
    /// R9P12. A `close-all` WAVE IS NOT ONE ORDINARY CALL.
    ///
    /// `RiskReducingHandlerPath` is `EmergencyBudget + WorstCaseOperationPath` — "plus exactly one
    /// ordinary call", because a close ends in a `Place` that is excluded from the emergency
    /// deadline. But `RunLegs` issues up to `MaxLegsInFlight` (four) legs at once and every one of
    /// them ends in `TradingGateway.PlaceAsync`, which takes `_dispatchGate` — a `SemaphoreSlim(1,1)`
    /// held across the connector call. So one close-all handler can owe FOUR ordinary calls in
    /// series after spending the whole emergency budget on its reads.
    ///
    /// The connector here reports its worst case HONESTLY (one second, which is what every call
    /// costs) and the drain derives itself correctly from that. Written to PASS if the drain covers
    /// the handler.
    /// </summary>
    [Fact]
    public async Task R9P12_the_drain_covers_a_close_all_wave()
    {
        var db = TestEnv.NewDb();
        using var _1 = db;
        var conn = new FakeConnector(new FakeBroker())
        {
            EmergencyBudget = TimeSpan.FromMilliseconds(6500)
        };
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 20;
            s.Risk.MaxOrdersPerMinute = 200;
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        var pipe = NewPipe();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe)
        {
            SettleAfterCancelTimeout = TimeSpan.FromMilliseconds(200)
        };
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        foreach (var sym in new[] { "ES", "NQ", "MES", "YM" })
            Assert.True((await client.SendAsync(new IpcRequest
            {
                Op = Ops.Buy, RequestId = $"r9p12-{sym}",
                Args = new()
                {
                    ["symbol"] = JsonSerializer.SerializeToElement(sym),
                    ["quantity"] = JsonSerializer.SerializeToElement("1")
                }
            }).WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        var positions = conn.Broker.Positions.Count(p => p.Quantity != 0);
        o.WriteLine($"open positions = {positions}");
        Assert.Equal(4, positions);

        // Every call now costs a second, and the vendor call does not take a token — the shape the
        // builder's own cold-placement fixture uses, and the one that leaves DISPATCHING behind.
        conn.Faults.UncancellableLatencyMs = 1000;
        o.WriteLine($"WorstCaseOperationPath (honestly reported) = {conn.WorstCaseOperationPath.TotalSeconds:0.0}s");
        o.WriteLine($"derived drain = {server.HandlerDrainTimeout.TotalSeconds:0.00}s " +
                    $"(max(5 x {conn.WorstCaseOperationPath.TotalSeconds:0.0}, {conn.EmergencyBudget.TotalSeconds:0.0} + " +
                    $"{conn.WorstCaseOperationPath.TotalSeconds:0.0}) + 0.2)");

        var sweep = client.SendAsync(new IpcRequest { Op = Ops.CloseAll, RequestId = "r9p12-sweep" });
        _ = sweep.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        // Early, so the whole handler is still ahead of the drain — R9P13 measures that handler at
        // 9.06 s against a derived 7.5 s + whatever the settle margin happens to be.
        await Task.Delay(200);

        var timer = Stopwatch.StartNew();
        await server.DisposeAsync();
        timer.Stop();

        o.WriteLine($"disposal returned after {timer.Elapsed.TotalSeconds:0.00}s");
        o.WriteLine($"DISPATCHING rows = {Dispatching(db)}");
        o.WriteLine($"handlers_did_not_finish = {ReadEngineering(db, "handlers_did_not_finish") ?? "(not logged)"}");
        foreach (var r in gw.Requests.Open())
            o.WriteLine($"  open: {r.RequestId,-24} {r.State,-14} reconcile={r.NeedsReconciliation}");

        Assert.True(Dispatching(db) == 0,
            $"{Dispatching(db)} request(s) left DISPATCHING: a close-all wave owes up to four ordinary " +
            $"Place calls in series (they queue on TradingGateway._dispatchGate), but the drain allows " +
            $"the emergency budget plus ONE — {server.HandlerDrainTimeout.TotalSeconds:0.00}s against a " +
            $"handler that needs about {(conn.EmergencyBudget + 4 * conn.WorstCaseOperationPath).TotalSeconds:0.00}s");
    }

    /// <summary>R9P13. How long does a four-position close-all actually take, with no disposal?</summary>
    [Fact]
    public async Task R9P13_how_long_a_close_all_wave_really_takes()
    {
        var db = TestEnv.NewDb();
        using var _1 = db;
        var conn = new FakeConnector(new FakeBroker()) { EmergencyBudget = TimeSpan.FromMilliseconds(6500) };
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 20;
            s.Risk.MaxOrdersPerMinute = 200;
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        foreach (var sym in new[] { "ES", "NQ", "MES", "YM" })
            Assert.True((await client.SendAsync(new IpcRequest
            {
                Op = Ops.Buy, RequestId = $"r9p13-{sym}",
                Args = new()
                {
                    ["symbol"] = JsonSerializer.SerializeToElement(sym),
                    ["quantity"] = JsonSerializer.SerializeToElement("1")
                }
            }).WaitAsync(TimeSpan.FromSeconds(10))).Ok);

        conn.Faults.UncancellableLatencyMs = 1000;
        var timer = Stopwatch.StartNew();
        var reply = await client.SendAsync(new IpcRequest { Op = Ops.CloseAll, RequestId = "r9p13-sweep" })
            .WaitAsync(TimeSpan.FromSeconds(60));
        timer.Stop();
        o.WriteLine($"close-all took {timer.Elapsed.TotalSeconds:0.00}s " +
                    $"(budget {conn.EmergencyBudget.TotalSeconds:0.0}s + 4 x {conn.WorstCaseOperationPath.TotalSeconds:0.0}s " +
                    $"= {(conn.EmergencyBudget + 4 * conn.WorstCaseOperationPath).TotalSeconds:0.0}s would be the bound)");
        Dump((JsonElement)reply.Data!, gw, "close-all, 4 positions, 1 s per call");
        o.WriteLine($"a server at these values would drain for {new GatewayPipeServer(gw, IpcToken.Ensure(), NewPipe()).HandlerDrainTimeout.TotalSeconds:0.00}s");
    }
}

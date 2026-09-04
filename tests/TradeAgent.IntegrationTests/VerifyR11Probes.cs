using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
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

/// <summary>ADVERSARIAL VERIFY rounds 10+11, leg [2]. Probes only — not proposed for the branch.</summary>
public class VerifyR11Probes(ITestOutputHelper o)
{
    static string NewPipe() => "ta-vr11-" + Guid.NewGuid().ToString("n")[..12];

    // ---------------------------------------------------------------- a connector that counts

    /// <summary>
    /// Records every connector call with its start and end, so a handler's SERIAL depth can be
    /// counted rather than inferred from a wall clock that also contains pipe and database time.
    /// </summary>
    public sealed class Counting(FakeConnector inner) : ITradingConnector
    {
        public FakeConnector Inner => inner;

        /// <summary>
        /// A connector timeout, which safety rule 3 says must PROPAGATE. `TradingGateway.ModifyAsync`
        /// catches only `ConnectorRejectedException` and `ConnectorTransportException`, so it escapes
        /// and the row stays DISPATCHING — the U2c-1 residual, produced on purpose here.
        /// </summary>
        public bool TimeoutOnModify;

        public readonly ConcurrentQueue<(string Op, long From, long To)> Calls = new();
        long _epoch = Environment.TickCount64;

        public void Reset() { Calls.Clear(); _epoch = Environment.TickCount64; }

        async Task<T> Log<T>(string op, Func<Task<T>> f)
        {
            var from = Environment.TickCount64 - _epoch;
            try { return await f(); }
            finally { Calls.Enqueue((op, from, Environment.TickCount64 - _epoch)); }
        }

        async Task Log(string op, Func<Task> f)
        {
            var from = Environment.TickCount64 - _epoch;
            try { await f(); }
            finally { Calls.Enqueue((op, from, Environment.TickCount64 - _epoch)); }
        }

        /// <summary>
        /// The longest chain of calls that do NOT overlap — which is what a serial depth is. A wave
        /// of four concurrent legs contributes ONE to this, not four.
        /// </summary>
        public int SerialDepth()
        {
            var calls = Calls.OrderBy(c => c.From).ToList();
            var best = new int[calls.Count];
            var max = 0;
            for (var i = 0; i < calls.Count; i++)
            {
                best[i] = 1;
                for (var j = 0; j < i; j++)
                    if (calls[j].To <= calls[i].From && best[j] + 1 > best[i]) best[i] = best[j] + 1;
                if (best[i] > max) max = best[i];
            }
            return max;
        }

        public string Id => inner.Id;
        public string DisplayName => inner.DisplayName;
        public ConnectorCapabilities Capabilities => inner.Capabilities;
        public TimeSpan WorstCaseOperationPath => inner.WorstCaseOperationPath;
        public TimeSpan EmergencyBudget => inner.EmergencyBudget;

        public Task ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);
        public Task<HealthState> GetHealthAsync(CancellationToken ct = default) => inner.GetHealthAsync(ct);
        public Task<bool> IsConnectedAsync(CancellationToken ct = default) => inner.IsConnectedAsync(ct);

        public Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken ct = default) =>
            Log("accounts", () => inner.GetAccountsAsync(ct));
        public Task<AccountInfo?> GetAccountAsync(string a, CancellationToken ct = default) =>
            Log("account", () => inner.GetAccountAsync(a, ct));
        public Task<IReadOnlyList<InstrumentInfo>> GetInstrumentsAsync(CancellationToken ct = default) =>
            Log("instruments", () => inner.GetInstrumentsAsync(ct));
        public Task<QuoteInfo?> GetQuoteAsync(string s, CancellationToken ct = default) =>
            Log("quote", () => inner.GetQuoteAsync(s, ct));
        public Task<IReadOnlyList<PositionInfo>> GetPositionsAsync(string a, CancellationToken ct = default) =>
            Log("positions", () => inner.GetPositionsAsync(a, ct));
        public Task<IReadOnlyList<OrderInfo>> GetOrdersAsync(string a, bool inc, DateTimeOffset? since, CancellationToken ct = default) =>
            Log("orders", () => inner.GetOrdersAsync(a, inc, since, ct));
        public Task<IReadOnlyList<ExecutionInfo>> GetExecutionsAsync(string a, DateTimeOffset? since, CancellationToken ct = default) =>
            Log("executions", () => inner.GetExecutionsAsync(a, since, ct));
        public Task<OrderInfo> PlaceOrderAsync(PlaceOrderCommand c, CancellationToken ct = default) =>
            Log("place", () => inner.PlaceOrderAsync(c, ct));
        public Task<OrderInfo> ModifyOrderAsync(ModifyOrderCommand c, CancellationToken ct = default) =>
            Log("modify", async () =>
            {
                if (TimeoutOnModify) throw new TimeoutException("the modify timed out");
                return await inner.ModifyOrderAsync(c, ct);
            });
        public Task CancelOrderAsync(string id, CancellationToken ct = default) =>
            Log("cancel", () => inner.CancelOrderAsync(id, ct));
        public Task<IReadOnlyList<string>> CancelAllOrdersAsync(string a, CancellationToken ct = default) =>
            Log("cancel-all", () => inner.CancelAllOrdersAsync(a, ct));
        public Task<OrderInfo?> ClosePositionAsync(string a, string s, string cid, CancellationToken ct = default) =>
            Log("close", () => inner.ClosePositionAsync(a, s, cid, ct));

        public event Action<HealthState>? ConnectionChanged { add => inner.ConnectionChanged += value; remove => inner.ConnectionChanged -= value; }
        public event Action<QuoteInfo>? QuoteChanged { add => inner.QuoteChanged += value; remove => inner.QuoteChanged -= value; }
        public event Action<OrderInfo>? OrderChanged { add => inner.OrderChanged += value; remove => inner.OrderChanged -= value; }
        public event Action<ExecutionInfo>? ExecutionReceived { add => inner.ExecutionReceived += value; remove => inner.ExecutionReceived -= value; }
        public event Action<PositionInfo>? PositionChanged { add => inner.PositionChanged += value; remove => inner.PositionChanged -= value; }
        public event Action<AccountInfo>? AccountChanged { add => inner.AccountChanged += value; remove => inner.AccountChanged -= value; }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    static async Task<(TradingGateway Gw, Counting Conn, Database Db, GatewayPipeServer Server, string Pipe)>
        Ready(string pipe, TimeSpan? budget = null, TimeSpan? settle = null, TimeSpan? worstCase = null)
    {
        var db = TestEnv.NewDb();
        var fake = worstCase is { } w
            ? new FakeConnector(new FakeBroker()) { EmergencyBudget = budget ?? TimeSpan.FromMilliseconds(3200), WorstCaseOperationPath = w }
            : new FakeConnector(new FakeBroker()) { EmergencyBudget = budget ?? TimeSpan.FromMilliseconds(3200) };
        var conn = new Counting(fake);
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = fake.Broker.AccountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 400;
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe)
        {
            SettleAfterCancelTimeout = settle ?? TimeSpan.FromMilliseconds(100)
        };
        server.Start();
        return (gw, conn, db, server, pipe);
    }

    static async Task<(string Working, string[] Symbols)> Stock(PipeClient client, TradingGateway gw, Counting conn)
    {
        string[] symbols = ["ES", "NQ", "MES", "YM"];
        foreach (var symbol in symbols)
        {
            var filled = await client.SendAsync(new IpcRequest
            {
                Op = Ops.Buy,
                RequestId = $"stk-{symbol}-{Guid.NewGuid():n}"[..24],
                Args = new()
                {
                    ["symbol"] = JsonSerializer.SerializeToElement(symbol),
                    ["quantity"] = JsonSerializer.SerializeToElement("1")
                }
            }).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(filled.Ok, $"could not open a position in {symbol}: {filled.Error?.Message}");
        }

        conn.Inner.Faults.Fill = FillBehaviour.LeaveWorking;
        var resting = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Buy,
            RequestId = "stk-working",
            Args = new()
            {
                ["symbol"] = JsonSerializer.SerializeToElement("ES"),
                ["quantity"] = JsonSerializer.SerializeToElement("1"),
                ["limit"] = JsonSerializer.SerializeToElement("1")
            }
        }).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(resting.Ok, $"could not leave a resting order: {resting.Error?.Message}");
        conn.Inner.Faults.Fill = FillBehaviour.FillImmediately;

        Assert.Equal(4, (await gw.PositionsAsync()).Count(p => p.Quantity != 0));
        var working = (await gw.OrdersAsync()).Single().ConnectorOrderId;
        return (working, symbols);
    }

    static List<string> Vocabulary() =>
        typeof(Ops).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Where(op => op != Ops.Hello)
            .Distinct()
            .OrderBy(op => op, StringComparer.Ordinal)
            .ToList();

    static IpcRequest RequestFor(string op, string working, string[] symbols) => op switch
    {
        Ops.Buy or Ops.Sell => new IpcRequest
        {
            Op = op, RequestId = "p-" + op + Guid.NewGuid().ToString("n")[..8],
            Args = new()
            {
                ["symbol"] = JsonSerializer.SerializeToElement("ES"),
                ["quantity"] = JsonSerializer.SerializeToElement("1"),
                ["limit"] = JsonSerializer.SerializeToElement("1")
            }
        },
        Ops.Modify => new IpcRequest
        {
            Op = op, RequestId = "p-modify" + Guid.NewGuid().ToString("n")[..8],
            Args = new()
            {
                ["id"] = JsonSerializer.SerializeToElement(working),
                ["quantity"] = JsonSerializer.SerializeToElement("2")
            }
        },
        Ops.Cancel => new IpcRequest
        {
            Op = op, RequestId = "p-cancel" + Guid.NewGuid().ToString("n")[..8],
            Args = new() { ["id"] = JsonSerializer.SerializeToElement(working) }
        },
        Ops.Close => new IpcRequest
        {
            Op = op, RequestId = "p-close" + Guid.NewGuid().ToString("n")[..8],
            Args = new() { ["symbol"] = JsonSerializer.SerializeToElement(symbols[0]) }
        },
        Ops.CancelAll or Ops.CloseAll => new IpcRequest
        {
            Op = op, RequestId = "p-" + op.Replace("-", "") + Guid.NewGuid().ToString("n")[..8]
        },
        Ops.Quote or Ops.Position => new IpcRequest
        {
            Op = op, Args = new() { ["symbol"] = JsonSerializer.SerializeToElement("ES") }
        },
        Ops.Order => new IpcRequest
        {
            Op = op, Args = new() { ["id"] = JsonSerializer.SerializeToElement(working) }
        },
        Ops.MaterialNote => new IpcRequest
        {
            Op = op,
            Args = new()
            {
                ["path"] = JsonSerializer.SerializeToElement("probe.txt"),
                ["note"] = JsonSerializer.SerializeToElement("probe")
            }
        },
        _ => new IpcRequest { Op = op }
    };

    // ================================================================ TARGET 1 — the drain table

    /// <summary>
    /// R11P1 — the dispatcher's own op set against the table, counted independently of the
    /// builder's test, plus the premise the builder's exclusion of `hello` rests on.
    /// </summary>
    [Fact]
    public async Task R11P1_every_dispatched_op_has_a_row_and_every_row_is_dispatched()
    {
        var (gw, conn, db, server, pipe) = await Ready(NewPipe());
        using var _1 = db;
        await using var _2 = server;
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        async Task<bool> Handles(string op)
        {
            var reply = await client.SendAsync(new IpcRequest { Op = op }).WaitAsync(TimeSpan.FromSeconds(30));
            return reply.Ok || reply.Error?.Message != $"unknown operation '{op}'";
        }

        Assert.False(await Handles("zz-not-an-op"), "a made-up op was claimed as handled");

        var vocabulary = Vocabulary();
        var handled = new List<string>();
        foreach (var op in vocabulary) if (await Handles(op)) handled.Add(op);

        var rows = server.HandlerPaths.ToList();
        var names = rows.Select(r => r.Handler).ToList();

        o.WriteLine($"vocabulary   = {vocabulary.Count}: {string.Join(" ", vocabulary)}");
        o.WriteLine($"handled      = {handled.Count}: {string.Join(" ", handled)}");
        o.WriteLine($"table rows   = {names.Count}: {string.Join(" ", names)}");
        foreach (var r in rows) o.WriteLine($"  {r.Handler,-16} {r.Path.TotalMilliseconds,9:0} ms   {r.Why}");

        Assert.Empty(handled.Except(names));
        Assert.Empty(names.Except(handled));
        Assert.Equal(names.Count, names.Distinct().Count());

        // The premise of the ONE exclusion: `hello` is answered before the dispatcher, so it has no
        // chain. If the read loop ever stopped answering it, the exclusion would be hiding a handler.
        var hello = await client.SendAsync(new IpcRequest { Op = Ops.Hello, Token = IpcToken.Ensure() })
            .WaitAsync(TimeSpan.FromSeconds(10));
        o.WriteLine($"hello ok={hello.Ok} err={hello.Error?.Message}");
        Assert.True(hello.Ok, "hello was not answered by the read loop, so excluding it from the table is unproven");
    }

    /// <summary>
    /// R11P2 — EVERY handled op measured on its OWN fixture, not the nine the builder's theory
    /// names: the serial depth counted at the connector, against the depth its own row claims.
    /// </summary>
    [Fact]
    public async Task R11P2_every_handled_op_serial_depth_against_its_row()
    {
        var bad = new List<string>();
        o.WriteLine($"{"op",-16} {"depth",5} {"elapsed",9} {"row",9} {"row/W",6}  calls");
        foreach (var op in Vocabulary())
        {
            var (elapsed, depth, calls, drain, row, w, ok, err) =
                await MeasureOp(op, latencyMs: 120, budget: TimeSpan.FromSeconds(20), settle: TimeSpan.FromMilliseconds(100));
            o.WriteLine($"{op,-16} {depth,5} {elapsed.TotalMilliseconds,9:0} {row.Path.TotalMilliseconds,9:0} " +
                        $"{row.Path.TotalMilliseconds / Math.Max(1, w.TotalMilliseconds),6:0.0}  {calls}" +
                        (ok ? "" : $"   [not ok: {err}]"));

            if (drain < elapsed)
                bad.Add($"{op}: {elapsed.TotalSeconds:0.00}s > drain {drain.TotalSeconds:0.00}s");

            // A row that UNDERSTATES its own handler is a defect even when something else is the
            // maximum today: the maximum is what the next change to E or W decides.
            var measured = TimeSpan.FromMilliseconds(depth * w.TotalMilliseconds);
            if (op is not (Ops.CancelAll or Ops.CloseAll or Ops.Cancel or Ops.Close) && row.Path < measured)
                bad.Add($"{op}: the row claims {row.Path.TotalMilliseconds:0} ms but the measured serial depth is " +
                        $"{depth} calls = {measured.TotalMilliseconds:0} ms ({calls})");
        }

        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    /// <summary>
    /// R11P3 — the derived drain against every measured chain at THREE customised timeout sets.
    /// </summary>
    [Theory]
    [InlineData(120, 20_000, 100)]
    [InlineData(300, 900, 50)]
    [InlineData(60, 400, 2_000)]
    public async Task R11P3_drain_covers_every_handler_at_customised_timeouts(int latencyMs, int budgetMs, int settleMs)
    {
        var bad = new List<string>();
        foreach (var op in Vocabulary())
        {
            var (elapsed, depth, calls, drain, row, w, ok, err) = await MeasureOp(op, latencyMs,
                TimeSpan.FromMilliseconds(budgetMs), TimeSpan.FromMilliseconds(settleMs));
            o.WriteLine($"{op,-16} depth={depth,2} elapsed={elapsed.TotalMilliseconds,7:0} ms  " +
                        $"row={row.Path.TotalMilliseconds,7:0}  drain={drain.TotalMilliseconds,7:0}  {calls}" +
                        (ok ? "" : $"   [not ok: {err}]"));
            if (drain < elapsed)
                bad.Add($"{op}: {elapsed.TotalSeconds:0.00}s > drain {drain.TotalSeconds:0.00}s");
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    /// <summary>One op, one fresh fixture, latency armed only after the book is stocked.</summary>
    static async Task<(TimeSpan Elapsed, int Depth, string Calls, TimeSpan Drain,
        GatewayPipeServer.HandlerPath Row, TimeSpan W, bool Ok, string? Err)>
        MeasureOp(string op, int latencyMs, TimeSpan budget, TimeSpan settle)
    {
        var (gw, conn, db, server, pipe) = await Ready(NewPipe(), budget: budget, settle: settle);
        using var _1 = db;
        await using var _2 = server;
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);
        var (working, symbols) = await Stock(client, gw, conn);

        conn.Inner.Faults.LatencyMs = latencyMs;
        conn.Reset();
        var timer = Stopwatch.StartNew();
        var reply = await client.SendAsync(RequestFor(op, working, symbols)).WaitAsync(TimeSpan.FromSeconds(120));
        timer.Stop();
        var depth = conn.SerialDepth();
        var calls = string.Join(",", conn.Calls.OrderBy(c => c.From).Select(c => c.Op));
        return (timer.Elapsed, depth, calls, server.HandlerDrainTimeout,
            server.HandlerPaths.Single(p => p.Handler == op), conn.WorstCaseOperationPath,
            reply.Ok, reply.Error?.Message);
    }

    // ================================================================ TARGET 2 — the vocabulary

    /// <summary>
    /// R11P4 — the whole cross product through the exported seam, counted from the ENUM rather
    /// than from a list, in both directions, and against what CONTRACTS.md says each word means.
    /// </summary>
    [Fact]
    public void R11P4_five_words_over_every_state_and_transport()
    {
        string[] five = ["confirmed", "rejected", "sent-still-working", "sent-not-confirmed", "not-sent"];
        var states = Enum.GetValues<ExecutionState>().Cast<ExecutionState?>().Append(null).ToList();
        TransportOutcome?[] transports =
            [null, TransportOutcome.NothingWritten, TransportOutcome.PossiblyWritten, TransportOutcome.ReplyReceived];

        var produced = new HashSet<string>();
        var rows = new List<string>();
        foreach (var s in states)
            foreach (var t in transports)
            {
                var word = GatewayPipeServer.LegWordFor(s, t);
                produced.Add(word);
                rows.Add($"{s?.ToString() ?? "(none)",-18} {t?.ToString() ?? "(none)",-16} -> {word}");
                Assert.Contains(word, five);
            }

        foreach (var r in rows) o.WriteLine(r);
        o.WriteLine($"states = {states.Count}, transports = {transports.Length}, combinations = {rows.Count}");
        Assert.Equal(five.OrderBy(x => x), produced.OrderBy(x => x));

        // The rule CONTRACTS.md states: NothingWritten overrules EVERY arm.
        foreach (var s in states)
            Assert.Equal("not-sent", GatewayPipeServer.LegWordFor(s, TransportOutcome.NothingWritten));

        // And the other half of it: nothing else overrules a definite record.
        foreach (var t in new TransportOutcome?[] { null, TransportOutcome.PossiblyWritten, TransportOutcome.ReplyReceived })
        {
            Assert.Equal("confirmed", GatewayPipeServer.LegWordFor(ExecutionState.CANCELLED, t));
            Assert.Equal("confirmed", GatewayPipeServer.LegWordFor(ExecutionState.FILLED, t));
            Assert.Equal("rejected", GatewayPipeServer.LegWordFor(ExecutionState.REJECTED, t));
            Assert.Equal("sent-still-working", GatewayPipeServer.LegWordFor(ExecutionState.WORKING, t));
        }

        // An unresolved record with NO transport is the assurance; with any report it is not.
        Assert.Equal("not-sent", GatewayPipeServer.LegWordFor(ExecutionState.UNKNOWN, null));
        Assert.Equal("sent-not-confirmed", GatewayPipeServer.LegWordFor(ExecutionState.UNKNOWN, TransportOutcome.PossiblyWritten));
        Assert.Equal("sent-not-confirmed", GatewayPipeServer.LegWordFor(null, TransportOutcome.ReplyReceived));
    }

    /// <summary>
    /// R11P5 — every ExecutionState the ENUM has is named by the classifier, counted from the enum.
    /// A state added tomorrow must fail loudly rather than become the most dangerous word.
    /// </summary>
    [Fact]
    public void R11P5_no_execution_state_is_unmapped_and_an_invented_one_throws()
    {
        foreach (var s in Enum.GetValues<ExecutionState>())
            foreach (var t in new TransportOutcome?[] { null, TransportOutcome.NothingWritten,
                         TransportOutcome.PossiblyWritten, TransportOutcome.ReplyReceived })
                _ = GatewayPipeServer.LegWordFor(s, t);

        var invented = (ExecutionState)9999;
        var ex = Assert.Throws<InvalidOperationException>(() => GatewayPipeServer.LegWordFor(invented, null));
        o.WriteLine($"invented state -> {ex.Message}");

        var inventedTransport = (TransportOutcome)9999;
        var ex2 = Assert.Throws<InvalidOperationException>(
            () => GatewayPipeServer.LegWordFor(ExecutionState.UNKNOWN, inventedTransport));
        o.WriteLine($"invented transport (unresolved arm) -> {ex2.Message}");

        var ex3 = Assert.Throws<InvalidOperationException>(
            () => GatewayPipeServer.LegWordFor(ExecutionState.CANCELLED, inventedTransport));
        o.WriteLine($"invented transport (definite arm)   -> {ex3.Message}");
    }


    // ================================================================ TARGET 4 — silent disposal

    static string? Engineering(Database db, string ev) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT severity FROM engineering_log WHERE component='Ipc' AND event=$e ORDER BY id LIMIT 1", ("$e", ev));
        using var r = c.ExecuteReader();
        return r.Read() ? r.GetString(0) : null;
    });

    static string EngineeringMeta(Database db, string ev) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT metadata FROM engineering_log WHERE component='Ipc' AND event=$e ORDER BY id LIMIT 1", ("$e", ev));
        using var r = c.ExecuteReader();
        return r.Read() ? r.IsDBNull(0) ? "" : r.GetString(0) : "";
    });

    /// <summary>
    /// R11P10 — the sentinel's OWN premise: it is inside `if (handlers.Length > 0)`, and the
    /// handler set is per-CONNECTION and self-removing. A row left DISPATCHING by a handler that has
    /// already finished, with the agent gone by the time the app closes, is the case the sentinel
    /// exists for — and it is the case in which it does not run.
    /// </summary>
    [Fact]
    public async Task R11P10_disposal_is_silent_when_no_connection_handler_is_alive()
    {
        var (gw, conn, db, server, pipe) = await Ready(NewPipe(), settle: TimeSpan.FromMilliseconds(200));
        using var _1 = db;

        var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        conn.Inner.Faults.Fill = FillBehaviour.LeaveWorking;
        var resting = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Buy, RequestId = "p10-working",
            Args = new()
            {
                ["symbol"] = JsonSerializer.SerializeToElement("ES"),
                ["quantity"] = JsonSerializer.SerializeToElement("1"),
                ["limit"] = JsonSerializer.SerializeToElement("1")
            }
        }).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(resting.Ok, resting.Error?.Message);
        var target = (await gw.OrdersAsync()).Single().ConnectorOrderId;

        // A connector timeout escapes ModifyAsync's catch taxonomy, so the row is left DISPATCHING
        // by a handler that then answers the agent and carries on living.
        conn.TimeoutOnModify = true;
        var modify = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Modify, RequestId = "p10-modify",
            Args = new()
            {
                ["id"] = JsonSerializer.SerializeToElement(target),
                ["quantity"] = JsonSerializer.SerializeToElement("2")
            }
        }).WaitAsync(TimeSpan.FromSeconds(20));
        o.WriteLine($"modify ok={modify.Ok} err={modify.Error?.Message}");

        var row = gw.GetRequest("p10-modify")!;
        o.WriteLine($"row state = {row.State}  reconcile = {row.NeedsReconciliation}");
        Assert.Equal(ExecutionState.DISPATCHING, row.State);
        Assert.False(row.NeedsReconciliation);

        // THE AGENT GOES AWAY FIRST — a CLI that exited, an app closed after the agent stopped. The
        // connection handler completes and removes itself from the set disposal counts.
        await client.DisposeAsync();
        await Task.Delay(500);

        var timer = Stopwatch.StartNew();
        await server.DisposeAsync();
        timer.Stop();

        var dispatching = gw.Requests.Query("execution_state='DISPATCHING'");
        o.WriteLine($"disposal returned in {timer.ElapsedMilliseconds} ms " +
                    $"(derived drain {server.HandlerDrainTimeout.TotalMilliseconds:0} ms)");
        o.WriteLine($"DISPATCHING rows at return = {dispatching.Count} ({string.Join(",", dispatching.Select(r => r.RequestId))})");
        o.WriteLine($"needing reconciliation      = {gw.Requests.NeedingReconciliation().Count}");
        o.WriteLine($"handlers_did_not_finish     = {Engineering(db, "handlers_did_not_finish") ?? "(not logged)"}");
        o.WriteLine($"metadata                    = {EngineeringMeta(db, "handlers_did_not_finish")}");

        Assert.Single(dispatching);
        Assert.Equal("error", Engineering(db, "handlers_did_not_finish"));
        Assert.Contains("p10-modify", EngineeringMeta(db, "handlers_did_not_finish"));
    }

    /// <summary>
    /// R11P11 — the same row, but with the agent still connected when the app closes: the control
    /// case that shows the sentinel works when a handler happens to be alive.
    /// </summary>
    [Fact]
    public async Task R11P11_disposal_names_the_row_when_a_connection_handler_is_alive()
    {
        var (gw, conn, db, server, pipe) = await Ready(NewPipe(), settle: TimeSpan.FromMilliseconds(200));
        using var _1 = db;
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        conn.Inner.Faults.Fill = FillBehaviour.LeaveWorking;
        var resting = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Buy, RequestId = "p11-working",
            Args = new()
            {
                ["symbol"] = JsonSerializer.SerializeToElement("ES"),
                ["quantity"] = JsonSerializer.SerializeToElement("1"),
                ["limit"] = JsonSerializer.SerializeToElement("1")
            }
        }).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(resting.Ok, resting.Error?.Message);
        var target = (await gw.OrdersAsync()).Single().ConnectorOrderId;

        conn.TimeoutOnModify = true;
        await client.SendAsync(new IpcRequest
        {
            Op = Ops.Modify, RequestId = "p11-modify",
            Args = new()
            {
                ["id"] = JsonSerializer.SerializeToElement(target),
                ["quantity"] = JsonSerializer.SerializeToElement("2")
            }
        }).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(ExecutionState.DISPATCHING, gw.GetRequest("p11-modify")!.State);

        await server.DisposeAsync();
        o.WriteLine($"handlers_did_not_finish = {Engineering(db, "handlers_did_not_finish") ?? "(not logged)"}");
        o.WriteLine($"metadata                = {EngineeringMeta(db, "handlers_did_not_finish")}");
        Assert.Equal("error", Engineering(db, "handlers_did_not_finish"));
        Assert.Contains("p11-modify", EngineeringMeta(db, "handlers_did_not_finish"));
    }

    /// <summary>
    /// R11P12 — a `close-all` wave BIGGER than one wave (eight positions), disposed mid-wave, and
    /// the drain measured against it. `E + L·W` is claimed to bound any book size.
    /// </summary>
    [Fact]
    public async Task R11P12_a_two_wave_close_all_disposed_mid_wave_leaves_nothing_unsettled()
    {
        var (gw, conn, db, server, pipe) = await Ready(NewPipe(),
            budget: TimeSpan.FromSeconds(6), settle: TimeSpan.FromMilliseconds(300));
        using var _1 = db;
        await using (var client = new PipeClient())
        {
            await client.ConnectAsync(10_000, pipe);
            string[] symbols = ["ES", "NQ", "MES", "YM", "RTY", "CL", "GC", "ZB"];
            var opened = 0;
            foreach (var symbol in symbols)
            {
                var r = await client.SendAsync(new IpcRequest
                {
                    Op = Ops.Buy, RequestId = $"p12-{symbol}",
                    Args = new()
                    {
                        ["symbol"] = JsonSerializer.SerializeToElement(symbol),
                        ["quantity"] = JsonSerializer.SerializeToElement("1")
                    }
                }).WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(r.Ok, r.Error?.Message);
                opened++;
            }
            o.WriteLine($"orders opened = {opened}, positions = {(await gw.PositionsAsync()).Count(p => p.Quantity != 0)}");

            // Uncancellable, for the reason the builder's own wave test gives: a call that unwinds
            // at the cancel records UNKNOWN and hides the harm as "an order that needs reconciling".
            conn.Inner.Faults.UncancellableLatencyMs = 300;
            var sweep = Task.Run(async () =>
            {
                try { await client.SendAsync(new IpcRequest { Op = Ops.CloseAll, RequestId = "p12-wave" }); }
                catch (Exception) { /* the pipe goes with the server */ }
            });
            // Land the disposal WHILE a placement is genuinely in flight, rather than at a guessed
            // millisecond: poll until a leg is DISPATCHING, and assert that it happened.
            var landed = 0;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                landed = gw.Requests.Query("execution_state='DISPATCHING'").Count;
                if (landed > 0) break;
                await Task.Delay(15);
            }
            o.WriteLine($"DISPATCHING while disposal starts = {landed}");
            Assert.True(landed > 0, "disposal never landed on a placement in flight, so this proves nothing");

            var timer = Stopwatch.StartNew();
            await server.DisposeAsync();
            timer.Stop();

            var dispatching = gw.Requests.Query("execution_state='DISPATCHING'");
            o.WriteLine($"disposal took {timer.ElapsedMilliseconds} ms, derived drain {server.HandlerDrainTimeout.TotalMilliseconds:0} ms");
            o.WriteLine($"DISPATCHING rows at return = {dispatching.Count} ({string.Join(",", dispatching.Select(r => r.RequestId))})");
            o.WriteLine($"needing reconciliation     = {gw.Requests.NeedingReconciliation().Count}");
            o.WriteLine($"handlers_did_not_finish    = {Engineering(db, "handlers_did_not_finish") ?? "(not logged)"}");
            await sweep;
            Assert.Empty(dispatching);
        }
    }

    // ================================================================ TARGET 3 — the null rule

    /// <summary>
    /// A connector that MUTATES and does not call the ledger. Nothing on
    /// <see cref="ITradingConnector"/> — the public SDK interface — asks it to, and the SDK ships
    /// with the two connectors that do. This is the deviation's second question: can any path OTHER
    /// than the three the builder names produce a null transport result after a real mutation.
    /// </summary>
    public sealed class LedgerBlind(FakeConnector inner) : ITradingConnector
    {
        public int CancelsThatReachedTheBroker;

        public string Id => inner.Id;
        public string DisplayName => "Ledger-blind connector";
        public ConnectorCapabilities Capabilities => inner.Capabilities;
        public TimeSpan WorstCaseOperationPath => inner.WorstCaseOperationPath;
        public TimeSpan EmergencyBudget => inner.EmergencyBudget;

        public Task ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);
        public Task<HealthState> GetHealthAsync(CancellationToken ct = default) => inner.GetHealthAsync(ct);
        public Task<bool> IsConnectedAsync(CancellationToken ct = default) => inner.IsConnectedAsync(ct);
        public Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken ct = default) => inner.GetAccountsAsync(ct);
        public Task<AccountInfo?> GetAccountAsync(string a, CancellationToken ct = default) => inner.GetAccountAsync(a, ct);
        public Task<IReadOnlyList<InstrumentInfo>> GetInstrumentsAsync(CancellationToken ct = default) => inner.GetInstrumentsAsync(ct);
        public Task<QuoteInfo?> GetQuoteAsync(string s, CancellationToken ct = default) => inner.GetQuoteAsync(s, ct);
        public Task<IReadOnlyList<PositionInfo>> GetPositionsAsync(string a, CancellationToken ct = default) => inner.GetPositionsAsync(a, ct);
        public Task<IReadOnlyList<OrderInfo>> GetOrdersAsync(string a, bool i, DateTimeOffset? s, CancellationToken ct = default) => inner.GetOrdersAsync(a, i, s, ct);
        public Task<IReadOnlyList<ExecutionInfo>> GetExecutionsAsync(string a, DateTimeOffset? s, CancellationToken ct = default) => inner.GetExecutionsAsync(a, s, ct);
        public Task<OrderInfo> PlaceOrderAsync(PlaceOrderCommand c, CancellationToken ct = default) => inner.PlaceOrderAsync(c, ct);
        public Task<OrderInfo> ModifyOrderAsync(ModifyOrderCommand c, CancellationToken ct = default) => inner.ModifyOrderAsync(c, ct);

        /// <summary>The frame went out and the broker acted; the acknowledgement was then lost.</summary>
        public async Task CancelOrderAsync(string connectorOrderId, CancellationToken ct = default)
        {
            await Task.Yield();
            inner.Broker.Cancel(connectorOrderId);            // it REALLY happened at the broker
            Interlocked.Increment(ref CancelsThatReachedTheBroker);
            throw new ConnectorTransportException("the acknowledgement was lost after the cancel was sent");
        }

        public Task<IReadOnlyList<string>> CancelAllOrdersAsync(string a, CancellationToken ct = default) => inner.CancelAllOrdersAsync(a, ct);
        public Task<OrderInfo?> ClosePositionAsync(string a, string s, string c, CancellationToken ct = default) => inner.ClosePositionAsync(a, s, c, ct);

        public event Action<HealthState>? ConnectionChanged { add => inner.ConnectionChanged += value; remove => inner.ConnectionChanged -= value; }
        public event Action<QuoteInfo>? QuoteChanged { add => inner.QuoteChanged += value; remove => inner.QuoteChanged -= value; }
        public event Action<OrderInfo>? OrderChanged { add => inner.OrderChanged += value; remove => inner.OrderChanged -= value; }
        public event Action<ExecutionInfo>? ExecutionReceived { add => inner.ExecutionReceived += value; remove => inner.ExecutionReceived -= value; }
        public event Action<PositionInfo>? PositionChanged { add => inner.PositionChanged += value; remove => inner.PositionChanged -= value; }
        public event Action<AccountInfo>? AccountChanged { add => inner.AccountChanged += value; remove => inner.AccountChanged -= value; }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>
    /// R11P7 — the deviation, from the other side: a connector that does the mutation and does not
    /// write the ledger. The property the round claims — "a fully sent leg can never read
    /// not-sent" — is an obligation on the CONNECTOR, and nothing states or enforces it.
    /// </summary>
    [Fact]
    public async Task R11P7_a_connector_that_does_not_write_the_ledger_reports_not_sent_for_a_cancel_that_landed()
    {
        var db = TestEnv.NewDb();
        using var _1 = db;
        var fake = new FakeConnector(new FakeBroker(), new FaultProfile { Fill = FillBehaviour.LeaveWorking })
        {
            EmergencyBudget = TimeSpan.FromSeconds(20)
        };
        var conn = new LedgerBlind(fake);
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = fake.Broker.AccountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 400;
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe)
        {
            SettleAfterCancelTimeout = TimeSpan.FromMilliseconds(100)
        };
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        var resting = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Buy, RequestId = "blind-working",
            Args = new()
            {
                ["symbol"] = JsonSerializer.SerializeToElement("ES"),
                ["quantity"] = JsonSerializer.SerializeToElement("1"),
                ["limit"] = JsonSerializer.SerializeToElement("1")
            }
        }).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(resting.Ok, resting.Error?.Message);

        var sweep = await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "blind-sweep" })
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(sweep.Ok, sweep.Error?.Message);

        var data = (JsonElement)sweep.Data!;
        o.WriteLine(data.ToString());
        foreach (var leg in data.GetProperty("outcomes").EnumerateArray())
            o.WriteLine($"  {leg.GetProperty("outcome").GetString()}  state={leg.GetProperty("state")} " +
                        $"transport={(leg.TryGetProperty("transport", out var t) ? t.ToString() : "(field absent)")}" +
                        $"  err={leg.GetProperty("error")}");
        o.WriteLine($"cancels that really reached the broker = {conn.CancelsThatReachedTheBroker}");
        o.WriteLine($"needing reconciliation = {gw.Requests.NeedingReconciliation().Count}");

        Assert.Equal(1, conn.CancelsThatReachedTheBroker);
        var word = data.GetProperty("outcomes").EnumerateArray().Single().GetProperty("outcome").GetString();
        Assert.True(word != "not-sent",
            $"a cancel that reached the broker was reported '{word}': the assurance is produced by an " +
            "absence of information whenever the connector does not call TransportLedger, and nothing " +
            "on ITradingConnector asks it to");
    }

    /// <summary>
    /// R11P8 — the three legs the builder says arrive with a genuinely empty record: counted at the
    /// connector rather than argued. No mutating call may have been made on any of them.
    /// </summary>
    [Fact]
    public async Task R11P8_the_three_null_transport_legs_never_start_a_mutation()
    {
        var (gw, conn, db, server, pipe) = await Ready(NewPipe(), budget: TimeSpan.FromSeconds(20));
        using var _1 = db;
        await using var _2 = server;
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);
        await Stock(client, gw, conn);

        // (a) a close-all symbol with nothing left to close: close everything, then sweep again.
        var first = await client.SendAsync(new IpcRequest { Op = Ops.CloseAll, RequestId = "n8-close-1" })
            .WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(first.Ok, first.Error?.Message);

        conn.Reset();
        var again = await client.SendAsync(new IpcRequest { Op = Ops.CloseAll, RequestId = "n8-close-2" })
            .WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(again.Ok, again.Error?.Message);
        var mutations = conn.Calls.Count(c => c.Op is "place" or "cancel" or "modify" or "cancel-all");
        var d = (JsonElement)again.Data!;
        o.WriteLine($"second close-all: {d}");
        o.WriteLine($"connector mutations during it = {mutations}  (calls: {string.Join(",", conn.Calls.Select(c => c.Op))})");
        Assert.Equal(0, mutations);
    }

    /// <summary>
    /// R11P9 — the leak the builder flagged and did not measure: a caller that cancels an emergency
    /// leaves its id in <c>AtasConnector._pending</c>. Read by reflection, because nothing exposes it.
    /// </summary>
    [Fact]
    public async Task R11P9_pending_leaks_an_entry_when_a_caller_cancels_an_emergency()
    {
        var pipe = "ta-vr11p-" + Guid.NewGuid().ToString("n")[..10];
        var cred = new BridgeCredential(new string('a', 64), Environment.ProcessPath ?? "");
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), cred);
        await connector.ConnectAsync();
        await using var peer = await MutePeer.Start(pipe, cred.Secret, BridgeOps.Cancel);
        for (var i = 0; i < 100 && !await connector.IsConnectedAsync(); i++) await Task.Delay(50);
        Assert.True(await connector.IsConnectedAsync(), "the probe peer never completed the handshake");

        var field = typeof(AtasConnector)
            .GetField("_pending", BindingFlags.NonPublic | BindingFlags.Instance)!;
        int Pending() => ((System.Collections.ICollection)field.GetValue(connector)!).Count;

        o.WriteLine($"pending at rest = {Pending()}");

        using var caller = new CancellationTokenSource();
        Task cancel;
        using (RiskReducingScope.Begin(TimeSpan.FromSeconds(30)))
            cancel = connector.CancelOrderAsync("PL-1", caller.Token);

        for (var i = 0; i < 200 && peer.MutedSeen == 0; i++) await Task.Delay(25);
        Assert.True(peer.MutedSeen > 0, "the peer never saw the cancel frame");
        o.WriteLine($"pending while in flight = {Pending()}");

        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => cancel.WaitAsync(TimeSpan.FromSeconds(10)));
        await Task.Delay(300);
        var after = Pending();
        o.WriteLine($"pending after the caller cancelled = {after}   late-answer slots = {connector.AwaitingLateAnswer}");
        Assert.Equal(0, after);
    }

    /// <summary>A bridge peer that answers everything except one op, so a frame can be left unanswered.</summary>
    sealed class MutePeer : IAsyncDisposable
    {
        readonly System.IO.Pipes.NamedPipeClientStream _p;
        readonly CancellationTokenSource _stop = new();
        int _muted;
        public int MutedSeen => Volatile.Read(ref _muted);

        MutePeer(string pipe) => _p = new System.IO.Pipes.NamedPipeClientStream(
            ".", pipe, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);

        public static async Task<MutePeer> Start(string pipe, string secret, string mute)
        {
            var peer = new MutePeer(pipe);
            await peer._p.ConnectAsync(10_000);
            var r = new StreamReader(peer._p, System.Text.Encoding.UTF8, false, 1 << 16, true);
            var w = new StreamWriter(peer._p, new System.Text.UTF8Encoding(false), 1 << 16, true) { AutoFlush = true };

            var nonce = BridgePipeAuth.NewNonce();
            await w.WriteLineAsync(Json.Write(new
            {
                v = Versions.BridgeProtocolVersion,
                op = BridgePipeAuth.Challenge,
                data = new { nonce, proof = BridgePipeAuth.Proof(secret, BridgePipeAuth.BridgeRole, nonce) }
            }));
            var answer = Json.Read<BridgeFrame>((await r.ReadLineAsync())!)!;
            Assert.True(answer.Op == BridgePipeAuth.Response, $"handshake refused: {answer.Op} {answer.Error}");

            await w.WriteLineAsync(Json.Write(new
            {
                v = Versions.BridgeProtocolVersion,
                op = BridgeOps.Hello,
                data = new BridgeHello
                {
                    BridgeProtocolVersion = Versions.BridgeProtocolVersion,
                    AccountId = "PROBE-1",
                    IsSimulated = true
                }
            }));

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!peer._stop.IsCancellationRequested)
                    {
                        var line = await r.ReadLineAsync(peer._stop.Token);
                        if (line is null) return;
                        var frame = Json.Read<BridgeFrame>(line);
                        if (frame?.Op is null) continue;
                        if (frame.Op == mute) { Interlocked.Increment(ref peer._muted); continue; }
                        await w.WriteLineAsync(Json.Write(new
                        {
                            v = Versions.BridgeProtocolVersion, id = frame.Id, ok = true,
                            data = Array.Empty<object>()
                        }));
                    }
                }
                catch (Exception) { /* torn down with the probe */ }
            });
            return peer;
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _p.DisposeAsync();
            _stop.Dispose();
        }
    }
}

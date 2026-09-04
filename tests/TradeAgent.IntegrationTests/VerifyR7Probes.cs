using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Atas;
using TradeAgent.Core;
using TradeAgent.Gateway;
using TradeAgent.Security;
using TradeAgent.TradeCli;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>ADVERSARIAL VERIFY round 7, leg [2]. Not proposed for the branch.</summary>
public class VerifyR7Probes(ITestOutputHelper o)
{
    static string NewPipe() => "ta-vr7-" + Guid.NewGuid().ToString("n")[..12];
    static BridgeCredential Cred() => new(new string('a', 64), Environment.ProcessPath ?? "");
    static void Observe(IEnumerable<Task> t) { foreach (var x in t) _ = x.ContinueWith(y => _ = y.Exception, TaskScheduler.Default); }

    static async Task Wait(Func<Task<bool>> c, int ms = 20_000)
    {
        var d = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < d) { if (await c()) return; await Task.Delay(25); }
        throw new TimeoutException("condition was not met in time");
    }

    // ---------------------------------------------------------------- the manager's ruling

    /// <summary>
    /// R7P1. THE MANAGER'S RULING, MEASURED THROUGH THE REAL GATEWAY. At the caller's two seconds:
    /// what exactly does the owner read, and what state is on the record? The ruling is that the
    /// sentence must LEAD with the order outcome and the record must be UNKNOWN.
    /// This probe asserts nothing about wording; it prints it and asserts the record.
    /// </summary>
    [Fact]
    public async Task R7P1_what_the_owner_reads_and_what_is_recorded_at_two_seconds()
    {
        var bridgePipe = NewPipe();
        await using var connector = new AtasConnector(bridgePipe, TimeSpan.FromSeconds(10), Cred());
        Assert.Equal(TimeSpan.FromSeconds(2), connector.EmergencyDeadline);
        await connector.ConnectAsync();
        await using var peer = await DeadPeer.Connect(bridgePipe, Cred().Secret);
        await Wait(async () => await connector.IsConnectedAsync());

        using var db = TestEnv.NewDb();
        var gw = new TradingGateway(db, connector, new HealthRegistry());
        gw.Update(s => { s.Mode = TradingMode.PAPER; s.SelectedAccountId = "ATAS-DEAD";
                         s.Risk.MaxOrderQuantity = 10m; s.Risk.MaxNotionalPerOrder = 10_000_000m;
                         s.Risk.MaxOrdersPerMinute = 100; s.Risk.MaxOpenPositions = 10; });
        await gw.RefreshHealthAsync();
        o.WriteLine($"peer answered {peer.Answers} frames while healthy; now it freezes");
        peer.Freeze();
        var ipc = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), ipc);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, ipc);

        var t = Stopwatch.StartNew();
        var reply = await client.SendAsync(new IpcRequest { Op = Ops.Cancel, RequestId = "r7-ruling",
            Args = new() { ["id"] = JsonSerializer.SerializeToElement("FB-1") } }).WaitAsync(TimeSpan.FromSeconds(40));
        t.Stop();

        var msg = reply.Error?.Message ?? "(ok)";
        o.WriteLine($"elapsed = {(int)t.Elapsed.TotalMilliseconds} ms");
        o.WriteLine($"code    = {reply.Error?.Code}");
        o.WriteLine($"SENTENCE: {msg}");
        o.WriteLine($"  leads with the order outcome? {msg.StartsWith("'")}");
        o.WriteLine($"  names the outcome?  NOT confirmed={msg.Contains("NOT confirmed")} / not started={msg.Contains("was not started")}");
        o.WriteLine($"  sends them to ATAS? {msg.Contains("ATAS")}");
        o.WriteLine($"  connection state as DETAIL or LEAD? starts-with-'the bridge is'={msg.StartsWith("the bridge is")}");

        var rec = gw.Requests.Get("r7-ruling");
        o.WriteLine($"RECORD state = {rec?.State.ToString() ?? "(no record)"}   needs_reconciliation={rec?.NeedsReconciliation}");
        o.WriteLine($"RECORD last_error = {rec?.LastError}");
        o.WriteLine($"connected right after = {await connector.IsConnectedAsync()}");

        Assert.NotNull(rec);
        Assert.Equal(ExecutionState.UNKNOWN, rec!.State);
    }

    // ---------------------------------------------------------------- the grace's cost

    /// <summary>
    /// R7P2. THE QUESTION THE BRIEF ASKS. The bridge is dead but the connection is now held for the
    /// full 10 s grace. What happens to work issued during that window — a SECOND emergency, and an
    /// ordinary order? What does the owner see, and is anything left unsettled?
    /// </summary>
    [Fact]
    public async Task R7P2_what_queues_behind_a_dead_bridge_during_the_ten_second_grace()
    {
        var bridgePipe = NewPipe();
        await using var connector = new AtasConnector(bridgePipe, TimeSpan.FromSeconds(10), Cred());
        await connector.ConnectAsync();
        await using var peer = await DeadPeer.Connect(bridgePipe, Cred().Secret);
        await Wait(async () => await connector.IsConnectedAsync());

        using var db = TestEnv.NewDb();
        var gw = new TradingGateway(db, connector, new HealthRegistry());
        gw.Update(s => { s.Mode = TradingMode.PAPER; s.SelectedAccountId = "ATAS-DEAD";
                         s.Risk.MaxOrderQuantity = 10m; s.Risk.MaxNotionalPerOrder = 10_000_000m;
                         s.Risk.MaxOrdersPerMinute = 100; s.Risk.MaxOpenPositions = 10; });
        await gw.RefreshHealthAsync();
        o.WriteLine($"peer answered {peer.Answers} frames while healthy; now it freezes");
        peer.Freeze();
        var ipc = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), ipc);
        server.Start();

        var wall = Stopwatch.StartNew();
        async Task<(string what, int at, int took, string msg)> Fire(string what, int delayMs, IpcRequest req)
        {
            await Task.Delay(delayMs);
            var at = (int)wall.Elapsed.TotalMilliseconds;
            await using var c = new PipeClient();
            await c.ConnectAsync(10_000, ipc);
            var s = Stopwatch.StartNew();
            var r = await c.SendAsync(req).WaitAsync(TimeSpan.FromSeconds(60));
            s.Stop();
            return (what, at, (int)s.Elapsed.TotalMilliseconds, r.Error?.Message ?? "(ok)");
        }

        var first  = Fire("emergency#1 cancel", 0, new IpcRequest { Op = Ops.Cancel, RequestId = "r7q-e1",
                        Args = new() { ["id"] = JsonSerializer.SerializeToElement("FB-1") } });
        var second = Fire("emergency#2 cancel", 2500, new IpcRequest { Op = Ops.Cancel, RequestId = "r7q-e2",
                        Args = new() { ["id"] = JsonSerializer.SerializeToElement("FB-2") } });
        var order  = Fire("ordinary buy", 2600, new IpcRequest { Op = Ops.Buy, RequestId = "r7q-buy",
                        Args = new()
                        {
                            ["symbol"] = JsonSerializer.SerializeToElement("ES"),
                            ["quantity"] = JsonSerializer.SerializeToElement("1"),
                            ["limit"] = JsonSerializer.SerializeToElement("1")
                        } });

        foreach (var r in await Task.WhenAll(first, second, order))
            o.WriteLine($"{r.what,-20} issued at t+{r.at,5} ms  took {r.took,6} ms   {r.msg}");

        o.WriteLine($"connected at t+{(int)wall.Elapsed.TotalMilliseconds} ms = {await connector.IsConnectedAsync()}");
        await Task.Delay(3000);
        o.WriteLine($"connected at t+{(int)wall.Elapsed.TotalMilliseconds} ms = {await connector.IsConnectedAsync()}");

        foreach (var id in new[] { "r7q-e1", "r7q-e2", "r7q-buy" })
        {
            var rec = gw.Requests.Get(id);
            o.WriteLine($"RECORD {id,-8} = {rec?.State.ToString() ?? "(none)",-18} reconcile={rec?.NeedsReconciliation}");
        }
        var stuck = gw.Requests.NeedingReconciliation().Count;
        o.WriteLine($"needing reconciliation = {stuck}");
        Assert.True(false, "measurement probe — see output");
    }

    /// <summary>
    /// A peer that answers everything normally until <see cref="Freeze"/>, after which it neither
    /// reads nor answers — a bridge whose frame loop wedges mid-session, which is the shape this
    /// round is about. Serving first is what lets the gateway become healthy at all.
    /// </summary>
    internal sealed class DeadPeer : IAsyncDisposable
    {
        readonly NamedPipeClientStream _p;
        readonly CancellationTokenSource _stop = new();
        readonly List<Task> _bg = [];
        volatile bool _frozen;
        long _answers;
        public long Answers => Interlocked.Read(ref _answers);
        public void Freeze() => _frozen = true;
        string? _mute;
        public void MuteOnly(string op) => _mute = op;

        DeadPeer(string pipe) => _p = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);

        public static async Task<DeadPeer> Connect(string pipe, string secret)
        {
            var peer = new DeadPeer(pipe);
            await peer._p.ConnectAsync(10_000);
            var nonce = BridgePipeAuth.NewNonce();
            await peer.Write(new { v = Versions.BridgeProtocolVersion, op = BridgePipeAuth.Challenge,
                data = new { nonce, proof = BridgePipeAuth.Proof(secret, BridgePipeAuth.BridgeRole, nonce) } });
            var answer = Json.Read<BridgeFrame>(await peer.ReadLine())!;
            Assert.True(answer.Op == BridgePipeAuth.Response, $"handshake refused: {answer.Op} {answer.Error}");
            await peer.Write(new { v = Versions.BridgeProtocolVersion, op = BridgeOps.Hello,
                data = new BridgeHello { BridgeProtocolVersion = Versions.BridgeProtocolVersion,
                    AccountId = "ATAS-DEAD", IsSimulated = true } });
            peer._bg.Add(Task.Run(peer.Serve));
            return peer;
        }

        async Task Serve()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    if (_frozen) { await Task.Delay(50, _stop.Token); continue; }   // stops READING too
                    var line = await ReadLine(_stop.Token);
                    if (_frozen) continue;
                    var f = Json.Read<BridgeFrame>(line);
                    if (f?.Id is null) continue;
                    if (_mute is not null && f.Op == _mute) continue;
                    await Write(new { v = Versions.BridgeProtocolVersion, id = f.Id, ok = true, data = Answer(f.Op) });
                    Interlocked.Increment(ref _answers);
                }
            }
            catch (Exception) { }
        }

        static JsonElement Answer(string? op) => op switch
        {
            BridgeOps.Accounts => JsonSerializer.SerializeToElement(new[]
                { new AccountInfo("ATAS-DEAD", "dead", "USD", 100000m, 100000m, 0m, true, true) }, Json.Options),
            BridgeOps.Instruments => JsonSerializer.SerializeToElement(new[]
                { new InstrumentInfo("ES", "ES", "CME", 0.25m, 12.5m, 1m) }, Json.Options),
            BridgeOps.Orders => JsonSerializer.SerializeToElement(new[]
                { new OrderInfo("FB-1", "TA-x", "ATAS-DEAD", "ES", OrderSide.Buy, OrderType.Limit,
                    1m, 0m, 1m, null, ExecutionState.WORKING, null, DateTimeOffset.UtcNow) }, Json.Options),
            BridgeOps.Positions or BridgeOps.Executions
                => JsonSerializer.SerializeToElement(Array.Empty<object>(), Json.Options),
            BridgeOps.Quote => JsonSerializer.SerializeToElement(
                new QuoteInfo("ES", 100m, 100.25m, 100m, 1m, 1m, DateTimeOffset.UtcNow), Json.Options),
            _ => JsonSerializer.SerializeToElement(Array.Empty<string>(), Json.Options)
        };

        Task Write(object f) => _p.WriteAsync(Encoding.UTF8.GetBytes(Json.Write(f) + "\n")).AsTask();

        async Task<string> ReadLine(CancellationToken ct = default)
        {
            var buf = new byte[8192]; var ms = new MemoryStream();
            while (true)
            {
                var n = ct == default
                    ? await _p.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(10))
                    : await _p.ReadAsync(buf, ct);
                if (n == 0) throw new IOException("closed");
                var nl = Array.IndexOf(buf, (byte)'\n', 0, n);
                if (nl >= 0) { ms.Write(buf, 0, nl); return Encoding.UTF8.GetString(ms.ToArray()); }
                ms.Write(buf, 0, n);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            foreach (var t in _bg) { try { await t; } catch (Exception) { } }
            await _p.DisposeAsync();
        }
    }

    /// <summary>
    /// R7P3. THE RULING, ON THE PATH IT IS ABOUT. The bridge answers every read, so the cancel gets
    /// past resolution and a record IS created — only the `cancel` frame goes unanswered. This is
    /// the arrangement in which "the record must be UNKNOWN at two seconds" is even askable.
    /// </summary>
    [Fact]
    public async Task R7P3_the_record_at_two_seconds_when_the_cancel_frame_itself_is_unanswered()
    {
        var bridgePipe = NewPipe();
        await using var connector = new AtasConnector(bridgePipe, TimeSpan.FromSeconds(10), Cred());
        await connector.ConnectAsync();
        await using var peer = await DeadPeer.Connect(bridgePipe, Cred().Secret);
        await Wait(async () => await connector.IsConnectedAsync());

        using var db = TestEnv.NewDb();
        var gw = new TradingGateway(db, connector, new HealthRegistry());
        gw.Update(s => { s.Mode = TradingMode.PAPER; s.SelectedAccountId = "ATAS-DEAD";
                         s.Risk.MaxOrderQuantity = 10m; s.Risk.MaxNotionalPerOrder = 10_000_000m;
                         s.Risk.MaxOrdersPerMinute = 100; s.Risk.MaxOpenPositions = 10; });
        await gw.RefreshHealthAsync();

        // Keeps answering everything EXCEPT the cancel, so the sweep's reads succeed and only the
        // risk-reducing frame itself is left hanging.
        peer.MuteOnly(BridgeOps.Cancel);

        var ipc = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), ipc);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, ipc);

        var t = Stopwatch.StartNew();
        var reply = await client.SendAsync(new IpcRequest { Op = Ops.Cancel, RequestId = "r7-rec",
            Args = new() { ["id"] = JsonSerializer.SerializeToElement("FB-1") } }).WaitAsync(TimeSpan.FromSeconds(40));
        t.Stop();
        var msg = reply.Error?.Message ?? "(ok)";
        var rec = gw.Requests.Get("r7-rec");

        o.WriteLine($"elapsed  = {(int)t.Elapsed.TotalMilliseconds} ms");
        o.WriteLine($"SENTENCE : {msg}");
        o.WriteLine($"  starts with the ORDER outcome?      {msg.StartsWith("'")}");
        o.WriteLine($"  starts with the CONNECTION state?   {msg.StartsWith("the bridge is")}");
        o.WriteLine($"  contains 'NOT confirmed'            {msg.Contains("NOT confirmed")}");
        o.WriteLine($"  sends the owner to ATAS             {msg.Contains("check your positions and orders in ATAS")}");
        o.WriteLine($"RECORD   = {rec?.State.ToString() ?? "(no record)"}  reconcile={rec?.NeedsReconciliation}  err={rec?.LastError}");
        o.WriteLine($"needing reconciliation = {gw.Requests.NeedingReconciliation().Count}");

        Assert.NotNull(rec);
        Assert.Equal(ExecutionState.UNKNOWN, rec!.State);
        Assert.True(t.Elapsed < TimeSpan.FromSeconds(4), $"the caller waited {t.Elapsed.TotalSeconds:0.00}s");
    }

    /// <summary>
    /// R7P4 (target 1, C1). MY OWN FIXTURE, not the builder's. The gate is held by a large write
    /// against a peer that drains at a fixed pace and then STOPS FOR GOOD at 1.5 s, so the emergency
    /// takes the gate late and then writes an oversized frame into a buffer with no room. One clock
    /// → ≈2 s. Two clocks → the gate wait plus a fresh write budget.
    /// PASSES if the emergency takes materially longer than its own deadline.
    /// </summary>
    [Fact]
    public async Task R7P4_one_clock_across_the_gate_and_the_write()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), Cred());
        Assert.Equal(TimeSpan.FromSeconds(2), connector.EmergencyDeadline);
        await connector.ConnectAsync();
        await using var peer = await DrainThenStopPeer.Connect(pipe, Cred().Secret, TimeSpan.FromMilliseconds(1500));
        await Wait(async () => await connector.IsConnectedAsync());

        var holder = connector.PlaceOrderAsync(new PlaceOrderCommand("TA-c1-hold", "ATAS-DRAIN", "ES",
            OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, new string('h', 512 * 1024)));
        Observe([holder]);
        await Task.Delay(150);

        // An oversized emergency: a ~100-byte cancel-all vanishes into an 8 KiB buffer and can only
        // ever measure the gate, which is what made C1 invisible for two fixtures.
        var t = Stopwatch.StartNew();
        Exception? ex = null;
        try { await connector.ClosePositionAsync("ATAS-DRAIN", new string('z', 64 * 1024), "TA-c1-emg"); }
        catch (Exception e) { ex = e; }
        t.Stop();
        var ms = (int)t.Elapsed.TotalMilliseconds;
        o.WriteLine($"emergency elapsed = {ms} ms   (bytes the peer took: {peer.BytesRead})");
        o.WriteLine($"  msg = {ex?.Message}");
        o.WriteLine($"  connected after = {await connector.IsConnectedAsync()}");
        Assert.True(ms > 2600, $"the emergency came back in {ms} ms — one clock, C1 holds");
    }

    /// <summary>Drains at a fixed pace, then stops reading for good — so a gate released late lands
    /// in a buffer with no room.</summary>
    internal sealed class DrainThenStopPeer : IAsyncDisposable
    {
        readonly NamedPipeClientStream _p;
        readonly CancellationTokenSource _stop = new();
        readonly List<Task> _bg = [];
        long _read;
        public long BytesRead => Interlocked.Read(ref _read);
        DrainThenStopPeer(string pipe) => _p = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);

        public static async Task<DrainThenStopPeer> Connect(string pipe, string secret, TimeSpan drainFor)
        {
            var peer = new DrainThenStopPeer(pipe);
            await peer._p.ConnectAsync(10_000);
            var nonce = BridgePipeAuth.NewNonce();
            await peer.Write(new { v = Versions.BridgeProtocolVersion, op = BridgePipeAuth.Challenge,
                data = new { nonce, proof = BridgePipeAuth.Proof(secret, BridgePipeAuth.BridgeRole, nonce) } });
            var buf = new byte[8192]; var ms0 = new MemoryStream();
            while (true)
            {
                var n = await peer._p.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                var nl = Array.IndexOf(buf, (byte)'\n', 0, n);
                if (nl >= 0) { ms0.Write(buf, 0, nl); break; }
                ms0.Write(buf, 0, n);
            }
            var answer = Json.Read<BridgeFrame>(Encoding.UTF8.GetString(ms0.ToArray()))!;
            Assert.True(answer.Op == BridgePipeAuth.Response, $"handshake refused: {answer.Op} {answer.Error}");
            await peer.Write(new { v = Versions.BridgeProtocolVersion, op = BridgeOps.Hello,
                data = new BridgeHello { BridgeProtocolVersion = Versions.BridgeProtocolVersion,
                    AccountId = "ATAS-DRAIN", IsSimulated = true } });
            peer._bg.Add(Task.Run(() => peer.DrainThenStop(drainFor)));
            return peer;
        }

        async Task DrainThenStop(TimeSpan drainFor)
        {
            var until = DateTime.UtcNow + drainFor;
            var buf = new byte[8192];
            try
            {
                while (!_stop.IsCancellationRequested && DateTime.UtcNow < until)
                {
                    var n = await _p.ReadAsync(buf, _stop.Token);
                    if (n == 0) return;
                    Interlocked.Add(ref _read, n);
                    await Task.Delay(20, _stop.Token);          // ~400 KiB/s
                }
            }
            catch (Exception) { }
            // and from here it never reads another byte
        }

        Task Write(object f) => _p.WriteAsync(Encoding.UTF8.GetBytes(Json.Write(f) + "\n")).AsTask();

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            foreach (var t in _bg) { try { await t; } catch (Exception) { } }
            await _p.DisposeAsync();
        }
    }
}

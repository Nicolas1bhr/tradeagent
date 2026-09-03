using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TradeAgent.AtasBridge;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Atas;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Gateway;
using TradeAgent.Security;
using TradeAgent.TradeCli;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// ADVERSARIAL VERIFY round 5, leg [2]. Refutation and measurement probes at 0909ada.
/// Not proposed for the branch.
/// </summary>
public class VerifyR5Probes(ITestOutputHelper o)
{
    static string NewPipe() => "ta-vr5-" + Guid.NewGuid().ToString("n")[..12];
    static BridgeCredential Cred() => new(new string('a', 64), Environment.ProcessPath ?? "");
    static void Observe(IEnumerable<Task> t) { foreach (var x in t) _ = x.ContinueWith(y => _ = y.Exception, TaskScheduler.Default); }

    static async Task Wait(Func<Task<bool>> c, int ms = 20_000)
    {
        var d = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < d) { if (await c()) return; await Task.Delay(25); }
        throw new TimeoutException("condition was not met in time");
    }

    static IpcRequest Buy(string? requestId, string symbol, string? frameId = null)
    {
        var r = new IpcRequest
        {
            Op = Ops.Buy, RequestId = requestId,
            Args = new()
            {
                ["symbol"] = JsonSerializer.SerializeToElement(symbol),
                ["quantity"] = JsonSerializer.SerializeToElement("1"),
                ["limit"] = JsonSerializer.SerializeToElement("1")
            }
        };
        if (frameId is not null) r.Id = frameId;
        return r;
    }

    // ------------------------------------------------------------------ TARGET 1, both directions

    /// <summary>
    /// R5P1. The V1/F1 guard must not have closed the ordinary door. A valid `request_id`, a valid
    /// frame `id` with `request_id` omitted, and the gateway's own minted sweep ids must all pass.
    /// This probe FAILS if the guard over-refuses.
    /// </summary>
    [Fact]
    public async Task R5P1_the_id_guard_still_lets_legitimate_ids_through()
    {
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        var a = await client.SendAsync(Buy("vr5-valid-1", "ES")).WaitAsync(TimeSpan.FromSeconds(10));
        o.WriteLine($"explicit request_id     ok={a.Ok} err={a.Error?.Code}");

        // request_id OMITTED, but the frame id is the CLI's own default shape (32 hex chars).
        var b = await client.SendAsync(Buy(null, "NQ", frameId: Guid.NewGuid().ToString("n"))).WaitAsync(TimeSpan.FromSeconds(10));
        o.WriteLine($"omitted, default GUID id ok={b.Ok} err={b.Error?.Code}");

        // The 61-char boundary, still open on the request_id path.
        var c = await client.SendAsync(Buy(new string('a', 61), "ES")).WaitAsync(TimeSpan.FromSeconds(10));
        var d = await client.SendAsync(Buy(new string('a', 62), "ES")).WaitAsync(TimeSpan.FromSeconds(10));
        o.WriteLine($"61 chars ok={c.Ok}   62 chars ok={d.Ok} err={d.Error?.Code}");

        var sweep = (JsonElement)(await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "vr5-sweep" })
            .WaitAsync(TimeSpan.FromSeconds(30))).Data!;
        foreach (var r in sweep.GetProperty("requests").EnumerateArray())
        {
            var id = r.GetProperty("request_id").GetString()!;
            o.WriteLine($"  minted [{id}] len={id.Length} coid=[{TradingGateway.ClientOrderIdFor(id)}] len={TradingGateway.ClientOrderIdFor(id).Length}");
        }
        o.WriteLine($"sweep attempted={sweep.GetProperty("attempted").GetInt32()} cancelled={sweep.GetProperty("cancelled").GetInt32()}");
        foreach (var ord in conn.Broker.Orders) o.WriteLine($"  broker coid=[{ord.ClientOrderId}] len={ord.ClientOrderId?.Length}");

        Assert.True(a.Ok, $"a valid explicit request_id was refused: {Json.Write(a.Error)}");
        Assert.True(b.Ok, $"a default GUID frame id with request_id omitted was refused: {Json.Write(b.Error)}");
        Assert.True(c.Ok, "61 characters was refused");
        Assert.False(d.Ok, "62 characters was accepted");
        Assert.Equal(3, sweep.GetProperty("attempted").GetInt32());
    }

    // ------------------------------------------------- THE OPERATOR'S OWN EMERGENCY BUTTONS

    /// <summary>
    /// R5P2. F11's fix opens <see cref="RiskReducingScope"/> in <c>GatewayPipeServer.Handle</c> and
    /// NOWHERE ELSE. The operator's own emergency controls do not go through the pipe:
    /// <c>DashboardView</c> calls <c>TradingGateway.OperatorCancelAllAsync</c> /
    /// <c>OperatorCloseAllAsync</c> in process. `OperatorCloseAllAsync` reads the positions first —
    /// the exact "position read before a close" the bounce named — on the ORDINARY deadline.
    ///
    /// Measured in process against a stalled bridge with the gate held, exactly as the agent's path
    /// is measured. The probe PASSES if the operator's own button is materially slower than the
    /// agent's identical request.
    /// </summary>
    [Theory]
    [InlineData("operator-close-all")]
    [InlineData("operator-cancel-all")]
    public async Task R5P2_the_operators_own_emergency_button_on_a_stalled_bridge(string button)
    {
        var bridgePipe = NewPipe();
        await using var connector = new AtasConnector(bridgePipe, TimeSpan.FromSeconds(10), Cred());   // shipped
        await connector.ConnectAsync();
        await using var peer = await StalledPeer.Connect(bridgePipe, Cred().Secret);
        await Wait(async () => await connector.IsConnectedAsync());

        using var db = TestEnv.NewDb();
        var gw = new TradingGateway(db, connector, new HealthRegistry());
        gw.Update(s => { s.Mode = TradingMode.PAPER; s.SelectedAccountId = "ATAS-STALLED"; });

        var stuck = connector.PlaceOrderAsync(new PlaceOrderCommand("TA-op-hold", "ATAS-STALLED", "ES",
            OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, new string('c', 128 * 1024)));
        Observe([stuck]);
        await Task.Delay(250);

        var timer = Stopwatch.StartNew();
        Exception? ex = null;
        try
        {
            if (button == "operator-close-all") await gw.OperatorCloseAllAsync();
            else await gw.OperatorCancelAllAsync();
        }
        catch (Exception e) { ex = e; }
        timer.Stop();
        var ms = (int)timer.Elapsed.TotalMilliseconds;

        o.WriteLine($"button={button}");
        o.WriteLine($"  scope active during the call? {RiskReducingScope.IsActive}");
        o.WriteLine($"  elapsed = {ms} ms");
        o.WriteLine($"  ex      = {ex?.GetType().Name}: {ex?.Message}");
        o.WriteLine($"  owner sentence present: NOT confirmed={ex?.Message.Contains("NOT confirmed") == true}");

        Assert.True(ms > 6000,
            $"{button} came back in {ms} ms — the operator's own button gets the emergency deadline too");
    }

    /// <summary>A peer that authenticates for real and then never reads or writes another byte.</summary>
    internal sealed class StalledPeer : IAsyncDisposable
    {
        readonly NamedPipeClientStream _p;
        StalledPeer(string pipe) => _p = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);

        public static async Task<StalledPeer> Connect(string pipe, string secret, string account = "ATAS-STALLED")
        {
            var peer = new StalledPeer(pipe);
            await peer._p.ConnectAsync(10_000);
            var nonce = BridgePipeAuth.NewNonce();
            await peer.Write(new { v = Versions.BridgeProtocolVersion, op = BridgePipeAuth.Challenge,
                data = new { nonce, proof = BridgePipeAuth.Proof(secret, BridgePipeAuth.BridgeRole, nonce) } });
            var answer = Json.Read<BridgeFrame>(await peer.ReadLine())!;
            Assert.True(answer.Op == BridgePipeAuth.Response, $"handshake refused: {answer.Op} {answer.Error}");
            await peer.Write(new { v = Versions.BridgeProtocolVersion, op = BridgeOps.Hello,
                data = new BridgeHello { BridgeProtocolVersion = Versions.BridgeProtocolVersion,
                    AccountId = account, IsSimulated = true } });
            return peer;
        }

        Task Write(object frame) => _p.WriteAsync(Encoding.UTF8.GetBytes(Json.Write(frame) + "\n")).AsTask();

        async Task<string> ReadLine()
        {
            var buf = new byte[8192];
            var ms = new MemoryStream();
            while (true)
            {
                var n = await _p.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                if (n == 0) throw new IOException("closed");
                var nl = Array.IndexOf(buf, (byte)'\n', 0, n);
                if (nl >= 0) { ms.Write(buf, 0, nl); return Encoding.UTF8.GetString(ms.ToArray()); }
                ms.Write(buf, 0, n);
            }
        }

        public async ValueTask DisposeAsync() => await _p.DisposeAsync();
    }

    // ------------------------------------------------------------------ TARGET 3, F11 both ways

    /// <summary>
    /// R5P3. The SAME operation, over the pipe (scope opened) and in process (no scope), against one
    /// stalled bridge each. Measures the asymmetry R5P2 found rather than asserting it.
    /// Also inside: a `place` issued while a risk-reducing scope is open must still take its full
    /// deadline, and a single `cancel` by broker id must be fast.
    /// </summary>
    [Theory]
    [InlineData("agent-close-all")]
    [InlineData("agent-cancel-all")]
    [InlineData("cancel-one-inside-an-open-scope")]
    [InlineData("read-inside-an-open-scope")]
    [InlineData("place-inside-an-open-scope")]
    [InlineData("modify-inside-an-open-scope")]
    public async Task R5P3_the_same_act_through_the_pipe(string what)
    {
        var bridgePipe = NewPipe();
        await using var connector = new AtasConnector(bridgePipe, TimeSpan.FromSeconds(10), Cred());   // shipped
        await connector.ConnectAsync();
        await using var peer = await StalledPeer.Connect(bridgePipe, Cred().Secret);
        await Wait(async () => await connector.IsConnectedAsync());

        using var db = TestEnv.NewDb();
        var gw = new TradingGateway(db, connector, new HealthRegistry());
        gw.Update(s => { s.Mode = TradingMode.PAPER; s.SelectedAccountId = "ATAS-STALLED"; });

        var ipcPipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), ipcPipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, ipcPipe);

        var stuck = connector.PlaceOrderAsync(new PlaceOrderCommand("TA-r5p3-hold", "ATAS-STALLED", "ES",
            OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, new string('c', 128 * 1024)));
        Observe([stuck]);
        await Task.Delay(250);

        var timer = Stopwatch.StartNew();
        string msg; bool ok = false;
        if (what is "agent-close-all" or "agent-cancel-all")
        {
            var req = what == "agent-close-all"
                ? new IpcRequest { Op = Ops.CloseAll, RequestId = "r5p3-closeall" }
                : new IpcRequest { Op = Ops.CancelAll, RequestId = "r5p3-cancelall" };
            var reply = await client.SendAsync(req).WaitAsync(TimeSpan.FromSeconds(60));
            timer.Stop();
            ok = reply.Ok; msg = reply.Error?.Message ?? "(ok)";
        }
        else
        {
            // The exclusion, tested where it lives: a scope IS open, and the op decides.
            using var scope = RiskReducingScope.Begin();
            Exception? ex = null;
            try
            {
                if (what == "place-inside-an-open-scope")
                    await connector.PlaceOrderAsync(new PlaceOrderCommand("TA-r5p3-place", "ATAS-STALLED", "ES",
                        OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, null));
                else if (what == "modify-inside-an-open-scope")
                    await connector.ModifyOrderAsync(new ModifyOrderCommand("FB-1", 2m, null, null));
                else if (what == "read-inside-an-open-scope")
                    await connector.GetAccountsAsync();
                else
                    await connector.CancelOrderAsync("FB-1");
            }
            catch (Exception e) { ex = e; }
            timer.Stop();
            msg = ex?.Message ?? "(no exception)";
        }
        var ms = (int)timer.Elapsed.TotalMilliseconds;

        o.WriteLine($"what={what,-27} elapsed={ms,6} ms  ok={ok}");
        o.WriteLine($"   msg={msg}");
        o.WriteLine($"   owner sentence: NOT confirmed={msg.Contains("NOT confirmed")}");

        if (what is "place-inside-an-open-scope" or "modify-inside-an-open-scope")
            Assert.True(ms < 6000, $"a {what} took {ms} ms — it did NOT get an emergency deadline (guard held)");
        else
            Assert.True(ms > 6000, $"{what} came back in {ms} ms — it DID get the emergency deadline (guard held)");
    }

    // ------------------------------------------------------------------ TARGET 2, V2 liveness

    /// <summary>
    /// R5P4. What the liveness rule actually keys on. `AtasConnector.PeerIsAlive()` is called from
    /// exactly one place (`Dispatch`, on every frame READ), never from the write path — so the rule
    /// is "any frame arrived from the peer during my window", not bytes and not heartbeats.
    /// Three peers, one variable each:
    ///   silent   — reads nothing, writes nothing            → must be DROPPED, "not responding"
    ///   chatty   — reads nothing, but sends a frame every 300 ms → KEPT? (bytes never move; frames do)
    ///   draining — reads everything slowly, sends nothing   → ? (bytes move; frames do not)
    /// The probe measures; it asserts only that an emergency answers inside ~2 s.
    /// </summary>
    [Theory]
    [InlineData("silent")]
    [InlineData("chatty-but-not-reading")]
    [InlineData("draining-but-mute")]
    public async Task R5P4_what_the_liveness_rule_keys_on(string peerKind)
    {
        var bridgePipe = NewPipe();
        await using var connector = new AtasConnector(bridgePipe, TimeSpan.FromSeconds(10), Cred());
        Assert.Equal(TimeSpan.FromSeconds(2), connector.EmergencyDeadline);
        await connector.ConnectAsync();
        await using var peer = await LivenessPeer.Connect(bridgePipe, Cred().Secret, peerKind);
        await Wait(async () => await connector.IsConnectedAsync());
        await Task.Delay(400);

        var timer = Stopwatch.StartNew();
        Exception? ex = null;
        try { await connector.CancelAllOrdersAsync("ATAS-STALLED"); } catch (Exception e) { ex = e; }
        timer.Stop();
        var ms = (int)timer.Elapsed.TotalMilliseconds;
        await Task.Delay(200);
        var connected = await connector.IsConnectedAsync();

        o.WriteLine($"peer={peerKind,-24} elapsed={ms,5} ms  connected_after={connected}");
        o.WriteLine($"   frames sent by peer={peer.FramesSent}  bytes read by peer={peer.BytesRead}");
        o.WriteLine($"   msg={ex?.Message}");

        Assert.True(ms < 4000, $"the emergency took {ms} ms against a '{peerKind}' peer — not bounded at 2 s");
    }

    /// <summary>A peer with one variable: whether it reads, and whether it talks.</summary>
    internal sealed class LivenessPeer : IAsyncDisposable
    {
        readonly NamedPipeClientStream _p;
        readonly CancellationTokenSource _stop = new();
        long _read, _sent;
        public long BytesRead => Interlocked.Read(ref _read);
        public long FramesSent => Interlocked.Read(ref _sent);

        LivenessPeer(string pipe) => _p = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);

        public static async Task<LivenessPeer> Connect(string pipe, string secret, string kind)
        {
            var peer = new LivenessPeer(pipe);
            await peer._p.ConnectAsync(10_000);
            var nonce = BridgePipeAuth.NewNonce();
            await peer.Write(new { v = Versions.BridgeProtocolVersion, op = BridgePipeAuth.Challenge,
                data = new { nonce, proof = BridgePipeAuth.Proof(secret, BridgePipeAuth.BridgeRole, nonce) } });
            var answer = Json.Read<BridgeFrame>(await peer.ReadLine())!;
            Assert.True(answer.Op == BridgePipeAuth.Response, $"handshake refused: {answer.Op} {answer.Error}");
            await peer.Write(new { v = Versions.BridgeProtocolVersion, op = BridgeOps.Hello,
                data = new BridgeHello { BridgeProtocolVersion = Versions.BridgeProtocolVersion,
                    AccountId = "ATAS-STALLED", IsSimulated = true } });

            if (kind.StartsWith("chatty")) _ = Task.Run(() => peer.Talk(kind == "chatty-at-the-shipped-5s" ? 5000 : 300));
            if (kind == "draining-but-mute") _ = Task.Run(() => peer.Drain());
            return peer;
        }

        async Task Talk(int intervalMs)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await Task.Delay(intervalMs, _stop.Token);
                    await Write(new { v = Versions.BridgeProtocolVersion, op = BridgeOps.Heartbeat });
                    Interlocked.Increment(ref _sent);
                }
            }
            catch (Exception) { }
        }

        async Task Drain()
        {
            var buf = new byte[8192];
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var n = await _p.ReadAsync(buf, _stop.Token);
                    if (n == 0) return;
                    Interlocked.Add(ref _read, n);
                    await Task.Delay(200, _stop.Token);
                }
            }
            catch (Exception) { }
        }

        Task Write(object frame) => _p.WriteAsync(Encoding.UTF8.GetBytes(Json.Write(frame) + "\n")).AsTask();

        async Task<string> ReadLine()
        {
            var buf = new byte[8192];
            var ms = new MemoryStream();
            while (true)
            {
                var n = await _p.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                if (n == 0) throw new IOException("closed");
                var nl = Array.IndexOf(buf, (byte)'\n', 0, n);
                if (nl >= 0) { ms.Write(buf, 0, nl); return Encoding.UTF8.GetString(ms.ToArray()); }
                ms.Write(buf, 0, n);
            }
        }

        public async ValueTask DisposeAsync() { await _stop.CancelAsync(); await _p.DisposeAsync(); }
    }

    /// <summary>
    /// R5P5. The same wedged-but-heartbeating peer at the REAL BridgeServer.HeartbeatInterval (5 s,
    /// emitted from its own Task.Run at BridgeServer.cs:251, independent of the frame read loop that
    /// a freeze wedges). Repeated, because with a 5 s beat and a 2 s window the verdict is a
    /// coin flip rather than a rule. Reports the distribution; asserts nothing about it.
    /// </summary>
    [Fact]
    public async Task R5P5_a_wedged_but_heartbeating_peer_at_the_shipped_interval()
    {
        var kept = 0; var dropped = 0;
        for (var i = 0; i < 12; i++)
        {
            var bridgePipe = NewPipe();
            await using var connector = new AtasConnector(bridgePipe, TimeSpan.FromSeconds(10), Cred());
            await connector.ConnectAsync();
            await using var peer = await LivenessPeer.Connect(bridgePipe, Cred().Secret, "chatty-at-the-shipped-5s");
            await Wait(async () => await connector.IsConnectedAsync());
            // RANDOM PHASE relative to the 5 s beat. Firing at a fixed offset measures the fixture,
            // not the rule: the first beat is 5 s after connect, so a probe that always fires at
            // +150 ms never sees one.
            await Task.Delay(Random.Shared.Next(150, 5150));

            Exception? ex = null;
            var t = Stopwatch.StartNew();
            try { await connector.CancelAllOrdersAsync("ATAS-STALLED"); } catch (Exception e) { ex = e; }
            t.Stop();
            await Task.Delay(150);
            var connected = await connector.IsConnectedAsync();
            if (connected) kept++; else dropped++;
            o.WriteLine($"  run {i}: {(int)t.Elapsed.TotalMilliseconds,5} ms  connected={connected,-5} " +
                        $"frames={peer.FramesSent} bytes_read={peer.BytesRead}  " +
                        $"verdict={(ex!.Message.Contains("busy") ? "BUSY-kept" : "NOT-RESPONDING-dropped")}");
        }
        o.WriteLine($"KEPT (told 'busy, try again') = {kept} of 12;  DROPPED (correct) = {dropped} of 12");
        Assert.True(kept == 0, $"a peer that accepted ZERO bytes was kept and called busy in {kept}/12 runs");
    }
}

using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using TradeAgent.AtasBridge;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Atas;
using TradeAgent.Core;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// ADVERSARIAL VERIFY round 4, leg [2] — targets 1, 2 and 6. Measurement + refutation probes.
/// Not proposed for the branch.
/// </summary>
public class VerifyR4TimingProbes(ITestOutputHelper o)
{
    static string NewPipe() => "ta-vr4t-" + Guid.NewGuid().ToString("n")[..12];
    static BridgeCredential Cred() => new(new string('a', 64), Environment.ProcessPath ?? "");

    static void Observe(IEnumerable<Task> tasks)
    { foreach (var t in tasks) _ = t.ContinueWith(x => _ = x.Exception, TaskScheduler.Default); }

    static async Task Wait(Func<Task<bool>> condition, int timeoutMs = 20_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline) { if (await condition()) return; await Task.Delay(25); }
        throw new TimeoutException("condition was not met in time");
    }

    /// <summary>
    /// TARGET 1. Each caller ALONE, on its OWN stalled bridge, at shipped deadlines. Cancel /
    /// CancelAll / Close must come back at ~2 s (EmergencyGateWait); Place / Modify / a read must
    /// take the full ~10 s WriteTimeout. The probe PASSES if the classification is wrong for that
    /// caller.
    /// </summary>
    [Theory]
    [InlineData("cancel-leg")]
    [InlineData("cancel-all")]
    [InlineData("close")]
    [InlineData("place")]
    [InlineData("modify")]
    [InlineData("read")]
    public async Task PROBE5_each_caller_alone_on_its_own_stalled_bridge(string caller)
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), Cred());
        Assert.Equal(TimeSpan.FromSeconds(2), connector.EmergencyGateWait);
        Assert.Equal(TimeSpan.FromSeconds(10), connector.WriteTimeout);
        await connector.ConnectAsync();

        await using var peer = await StalledPeer.Connect(pipe, Cred().Secret);
        await Wait(async () => await connector.IsConnectedAsync());

        // ONE oversized write holds the gate. Nothing else is in flight — this is the whole point of
        // the shape: with two callers, one caller's drop frees the other and the measurement is of
        // the drop, not of the classification.
        var stuck = connector.PlaceOrderAsync(new PlaceOrderCommand("TA-hold-1", "ATAS-STALLED", "ES",
            OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, new string('c', 128 * 1024)));
        Observe([stuck]);
        await Task.Delay(400);

        var timer = Stopwatch.StartNew();
        Exception? ex = null;
        try
        {
            switch (caller)
            {
                case "cancel-leg": await connector.CancelOrderAsync("FB-1"); break;
                case "cancel-all": await connector.CancelAllOrdersAsync("ATAS-STALLED"); break;
                case "close":      await connector.ClosePositionAsync("ATAS-STALLED", "ES", "TA-close-1"); break;
                case "place":      await connector.PlaceOrderAsync(new PlaceOrderCommand("TA-p2", "ATAS-STALLED", "ES",
                                       OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, null)); break;
                case "modify":     await connector.ModifyOrderAsync(new ModifyOrderCommand("FB-1", 2m, null, null)); break;
                default:           await connector.GetAccountsAsync(); break;
            }
        }
        catch (Exception e) { ex = e; }
        timer.Stop();

        var ms = (int)timer.Elapsed.TotalMilliseconds;
        o.WriteLine($"caller={caller,-11} elapsed={ms,6} ms  ex={ex?.GetType().Name}  msg={ex?.Message}");
        o.WriteLine($"still connected after: {await connector.IsConnectedAsync()}");

        var riskReducing = caller is "cancel-leg" or "cancel-all" or "close";
        if (riskReducing)
            Assert.True(ms > 6000, $"{caller} came back in {ms} ms — it DID get the emergency path (guard held)");
        else
            Assert.True(ms < 6000, $"{caller} took {ms} ms — it did NOT get the emergency path (guard held)");
    }

    /// <summary>
    /// TARGET 2, direction A. A SATURATED but perfectly healthy bridge: a real BridgeServer reading
    /// everything, 1500 concurrent 900 KiB RPCs, shipped deadlines. The emergency must fail as
    /// Busy/UNKNOWN within ~2 s, say "the bridge is busy", and LEAVE THE CONNECTION UP.
    /// PASSES if the healthy bridge is dropped or libelled as not responding.
    /// </summary>
    [Fact]
    public async Task PROBE6_saturation_must_not_drop_a_healthy_bridge()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));   // shipped deadlines
        await connector.ConnectAsync();
        var adapter = new LoopbackAtasAdapter();
        await using var bridge = new BridgeServer(adapter, pipe);
        bridge.Start();
        await Wait(async () => await connector.IsConnectedAsync());

        var fat = new string('s', 900 * 1024);
        var calls = Enumerable.Range(0, 1500).Select(_ => connector.GetQuoteAsync(fat)).ToArray();
        Observe(calls);
        await Task.Delay(500);          // let the backlog build

        var timer = Stopwatch.StartNew();
        Exception? ex = null;
        try { await connector.CancelAllOrdersAsync("ATAS-LOOPBACK"); }
        catch (Exception e) { ex = e; }
        timer.Stop();
        var ms = (int)timer.Elapsed.TotalMilliseconds;
        var connected = await connector.IsConnectedAsync();
        var faulted = calls.Count(c => c.IsFaulted);
        var done = calls.Count(c => c.IsCompleted);

        o.WriteLine($"emergency elapsed = {ms} ms");
        o.WriteLine($"exception         = {ex?.GetType().Name}: {ex?.Message}");
        o.WriteLine($"connected after   = {connected}");
        o.WriteLine($"backlog done={done} faulted={faulted} of 1500");

        Assert.True(!connected || (ex?.Message.Contains("not responding") ?? false),
            $"the healthy saturated bridge was kept (connected={connected}) and told it was busy — guard held");
    }

    /// <summary>
    /// TARGET 2, direction B. A TRULY STALLED peer must be dropped and told "not responding".
    /// PASSES if a stalled bridge survives or is called merely busy.
    /// </summary>
    [Fact]
    public async Task PROBE7_a_stalled_bridge_must_not_survive_as_busy()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), Cred());
        await connector.ConnectAsync();
        await using var peer = await StalledPeer.Connect(pipe, Cred().Secret);
        await Wait(async () => await connector.IsConnectedAsync());

        var stuck = connector.PlaceOrderAsync(new PlaceOrderCommand("TA-hold-2", "ATAS-STALLED", "ES",
            OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, new string('c', 128 * 1024)));
        Observe([stuck]);
        await Task.Delay(400);

        var timer = Stopwatch.StartNew();
        Exception? ex = null;
        try { await connector.CancelAllOrdersAsync("ATAS-STALLED"); } catch (Exception e) { ex = e; }
        timer.Stop();
        var ms = (int)timer.Elapsed.TotalMilliseconds;
        await Task.Delay(300);
        var connected = await connector.IsConnectedAsync();

        o.WriteLine($"emergency elapsed = {ms} ms");
        o.WriteLine($"exception         = {ex?.GetType().Name}: {ex?.Message}");
        o.WriteLine($"connected after   = {connected}");

        Assert.True(connected || (ex?.Message.Contains("busy") ?? false),
            $"the stalled bridge was dropped and told 'not responding' in {ms} ms — guard held");
    }

    /// <summary>A peer that authenticates for real and then never reads another byte.</summary>
    sealed class StalledPeer : IAsyncDisposable
    {
        readonly NamedPipeClientStream _p;
        StalledPeer(string pipe) => _p = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);

        public static async Task<StalledPeer> Connect(string pipe, string secret)
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
                    AccountId = "ATAS-STALLED", IsSimulated = true } });
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

    /// <summary>
    /// PROBE 8. THE COMMONEST REAL SHAPE: ATAS is frozen and NOTHING else is in flight, so the send
    /// gate is FREE when the owner presses the button. EmergencyGateWait bounds only the GATE wait;
    /// the emergency then becomes the writer and its own write is bounded by WriteTimeout (10 s),
    /// after which the RPC reply timeout (10 s) applies. Probe PASSES if an emergency on an idle
    /// stalled bridge takes materially longer than the 2 s the decision promises.
    /// </summary>
    [Fact]
    public async Task PROBE8_emergency_on_an_idle_stalled_bridge()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), Cred());   // shipped
        await connector.ConnectAsync();
        await using var peer = await StalledPeer.Connect(pipe, Cred().Secret);
        await Wait(async () => await connector.IsConnectedAsync());

        // NOTHING is holding the gate. The bridge has simply stopped reading.
        var timer = Stopwatch.StartNew();
        Exception? ex = null;
        try { await connector.CancelAllOrdersAsync("ATAS-STALLED"); } catch (Exception e) { ex = e; }
        timer.Stop();
        var ms = (int)timer.Elapsed.TotalMilliseconds;
        o.WriteLine($"idle-stalled emergency elapsed = {ms} ms");
        o.WriteLine($"exception = {ex?.GetType().Name}: {ex?.Message}");
        o.WriteLine($"connected after = {await connector.IsConnectedAsync()}");
        o.WriteLine($"owner-readable sentence present: NOT confirmed={ex?.Message.Contains("NOT confirmed")}");

        Assert.True(ms > 4000, $"the emergency came back in {ms} ms — the 2 s promise holds even with a free gate");
    }
}

using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Atas;
using TradeAgent.Core;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// VERIFIER'S OWN RED-FIRST PROBE for V2, written to compile and run UNCHANGED at both `d25dbb4`
/// and `0909ada` — it names no property the rename touched and no type this round introduced.
///
/// The builder inverted red-first on V2 (test written against the fix, RED measured by reverting).
/// This is the other order: the acceptance stated as a test, run against the sha that has the
/// defect. It must be RED at `d25dbb4` and GREEN at `0909ada`.
///
/// The shape is the one no test in the unit reached before round 5: the bridge has stopped reading
/// and NOTHING else is in flight, so the send gate is FREE when the owner presses stop.
/// </summary>
public class VerifyR5RedFirst(ITestOutputHelper o)
{
    static string NewPipe() => "ta-vr5rf-" + Guid.NewGuid().ToString("n")[..12];
    static BridgeCredential Cred() => new(new string('a', 64), Environment.ProcessPath ?? "");

    [Fact]
    public async Task An_emergency_on_an_idle_stalled_bridge_answers_in_two_seconds_says_why_and_drops_it()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), Cred());   // shipped
        await connector.ConnectAsync();
        await using var peer = await Peer.Connect(pipe, Cred().Secret);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && !await connector.IsConnectedAsync()) await Task.Delay(25);
        Assert.True(await connector.IsConnectedAsync(), "the fixture never connected");

        var timer = Stopwatch.StartNew();
        Exception? ex = null;
        try { await connector.CancelAllOrdersAsync("ATAS-STALLED"); } catch (Exception e) { ex = e; }
        timer.Stop();
        var ms = (int)timer.Elapsed.TotalMilliseconds;
        await Task.Delay(300);
        var connected = await connector.IsConnectedAsync();

        o.WriteLine($"elapsed         = {ms} ms");
        o.WriteLine($"exception       = {ex?.GetType().Name}: {ex?.Message}");
        o.WriteLine($"connected after = {connected}");

        Assert.NotNull(ex);
        Assert.True(ex is ConnectorTransportException, $"surfaced as {ex!.GetType().Name} — an emergency of unknown outcome must be indefinite");
        Assert.True(ms < 3000,
            $"the emergency took {ms} ms with a FREE gate — the two-second promise bounds only the queue wait, not what the caller waits");
        Assert.Contains("NOT confirmed", ex.Message);
        Assert.Contains("check your positions and orders in ATAS", ex.Message);
        Assert.False(connected,
            "the bridge that answered nothing at all was left connected, so nothing redials and the retry this failure advises has nowhere to go");
    }

    sealed class Peer : IAsyncDisposable
    {
        readonly NamedPipeClientStream _p;
        Peer(string pipe) => _p = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);

        public static async Task<Peer> Connect(string pipe, string secret)
        {
            var peer = new Peer(pipe);
            await peer._p.ConnectAsync(10_000);
            var nonce = BridgePipeAuth.NewNonce();
            await peer.Write(new { v = Versions.BridgeProtocolVersion, op = BridgePipeAuth.Challenge,
                data = new { nonce, proof = BridgePipeAuth.Proof(secret, BridgePipeAuth.BridgeRole, nonce) } });
            var answer = Json.Read<BridgeFrame>(await peer.ReadLine())!;
            Assert.True(answer.Op == BridgePipeAuth.Response, $"handshake refused: {answer.Op} {answer.Error}");
            await peer.Write(new { v = Versions.BridgeProtocolVersion, op = BridgeOps.Hello,
                data = new BridgeHello { BridgeProtocolVersion = Versions.BridgeProtocolVersion,
                    AccountId = "ATAS-STALLED", IsSimulated = true } });
            return peer;   // and from here it never reads or writes again
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
}

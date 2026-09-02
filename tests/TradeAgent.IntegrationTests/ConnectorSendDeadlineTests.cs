using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using TradeAgent.AtasBridge;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Atas;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// The connector's own send path against a bridge that stops reading.
///
/// bbcd36e gave <see cref="BridgeServer.SendRaw"/> a write deadline after the bridge froze on
/// Windows. This is the SAME defect facing the other way, and it was left in place: the connector
/// writes an RPC through <c>WriteLineAsync</c> with no deadline, and the RPC timeout it does have
/// only starts once that write has returned. So the timeout that is supposed to bound an order
/// cannot bound the part of it that hangs.
///
/// It is worse than one stuck order. Every writer shares <c>_sendGate</c>, and the gate is taken
/// before the write, so one peer that stops reading parks the first caller in the write and every
/// caller after it on the semaphore — a cancel, a close, a cancel-all. The frames a person reaches
/// for when something has gone wrong are exactly the ones queued behind the frame that is stuck.
///
/// WHY THE VOLUME. On macOS the pipe is a Unix socket with about 16 KiB of kernel buffer, so a
/// single small RPC lands and the stall is invisible; on Windows the same stall is available on far
/// less. Enough ordinary RPCs to overrun the buffer is the shape that shows it on both, and it is
/// not a contrived one: a gateway reconciling against a bridge that has stopped reading does this
/// by itself.
/// </summary>
public class ConnectorSendDeadlineTests
{
    static string NewPipe() => "ta-csd-" + Guid.NewGuid().ToString("n")[..12];

    /// <summary>
    /// Arithmetic, not measured: an <c>accounts</c> frame is about 55 bytes on the wire, so 2000 of
    /// them is roughly 110 KB against the ~16 KiB the macOS kernel will hold for a reader that never
    /// comes. Deliberately far past it rather than near it — this test must fail because the write
    /// has no deadline, never because the buffer happened to be a size nobody measured.
    /// </summary>
    const int Calls = 2000;

    /// <summary>Long enough that a working deadline (1s) plus a working RPC timeout (1s) fits several times over.</summary>
    static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    static BridgeCredential Cred() => new(new string('a', 64), Environment.ProcessPath ?? "");

    /// <summary>
    /// An order that cannot land must come back as a TRANSPORT failure and never as a rejection.
    /// Safety rule 3 on <c>IAtasAdapter</c>: a definite broker refusal is the only thing allowed to
    /// read as definite, because "this order does not exist" has to be provable to be worth saying.
    /// A write that is still sitting in a socket is the most indefinite state there is.
    /// </summary>
    [Fact]
    public async Task Rpcs_to_a_bridge_that_stopped_reading_end_rather_than_hang()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(1), Cred())
        {
            WriteTimeout = TimeSpan.FromSeconds(1)
        };
        await connector.ConnectAsync();

        await using var peer = await StalledBridgePeer.ConnectAndSayHello(pipe, Cred().Secret);
        await Wait(async () => await connector.IsConnectedAsync());

        // From here the peer reads nothing at all.
        var timer = Stopwatch.StartNew();
        var calls = Enumerable.Range(0, Calls).Select(_ => connector.GetAccountsAsync()).ToArray();

        var finished = true;
        try { await Task.WhenAll(calls).WaitAsync(Bound); }
        catch (TimeoutException) { finished = false; }
        catch (Exception) { /* every call failing is the expected outcome; the shape is asserted below */ }
        timer.Stop();

        var stuck = calls.Count(c => !c.IsCompleted);
        Assert.True(finished,
            $"{stuck} of {Calls} calls never finished within {Bound.TotalSeconds:0}s against a bridge that stopped reading " +
            "— an order with no deadline on its write, and every frame behind it stuck on the same gate");

        Assert.All(calls, c =>
        {
            Assert.True(c.IsFaulted, "a call to a bridge that never read anything reported success");
            var ex = c.Exception!.InnerException;
            Assert.True(ex is ConnectorTransportException,
                $"a frame that may or may not have reached ATAS surfaced as {ex?.GetType().Name}, " +
                "which the gateway would read as definite");
        });
    }

    /// <summary>
    /// ONE order, ONE writer, and nothing queued behind it — so only the deadline ON THE WRITE can
    /// end this. It is the case the volume test above does NOT cover: with many callers, the SECOND
    /// one timing out on the send gate is enough to close the pipe and free the first, so the write
    /// deadline can be deleted and that test still passes. It is deleted here and this one hangs.
    ///
    /// The order is large because it carries a large comment, which is free text the agent writes.
    /// That is a legal order, and one frame of it is bigger than the socket buffer, so this single
    /// write is the whole demonstration.
    /// </summary>
    [Fact]
    public async Task One_order_larger_than_the_buffer_still_ends_when_nothing_is_queued_behind_it()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(1), Cred())
        {
            WriteTimeout = TimeSpan.FromSeconds(1)
        };
        await connector.ConnectAsync();

        await using var peer = await StalledBridgePeer.ConnectAndSayHello(pipe, Cred().Secret);
        await Wait(async () => await connector.IsConnectedAsync());

        // Arithmetic, not measured: 64 KiB of comment is four times the ~16 KiB the macOS kernel
        // holds for a reader that never comes, so this frame cannot land in the buffer alone.
        var order = new PlaceOrderCommand("TA-stall-1", "ATAS-STALLED", "ES", OrderSide.Buy,
            OrderType.Market, 1m, null, null, TimeInForce.Day, new string('c', 64 * 1024));

        var call = connector.PlaceOrderAsync(order);
        var ended = await Task.WhenAny(call, Task.Delay(Bound)) == call;
        Observe([call]);

        Assert.True(ended,
            $"a single order to a bridge that stopped reading never came back within {Bound.TotalSeconds:0}s — " +
            "the RPC timeout cannot help, because it does not start until the write returns");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => call);
        Assert.True(ex is ConnectorTransportException,
            $"an order that may or may not have reached ATAS surfaced as {ex.GetType().Name}");
    }

    /// <summary>
    /// Shutdown does not wait on a stalled writer either. <c>DisposeAsync</c> runs when TradeAgent
    /// closes, and a peer that has stopped reading must not be able to hold the app open.
    /// </summary>
    [Fact]
    public async Task Disposing_the_connector_does_not_wait_on_a_bridge_that_stopped_reading()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(1), Cred())
        {
            WriteTimeout = TimeSpan.FromSeconds(1)
        };
        await connector.ConnectAsync();

        await using var peer = await StalledBridgePeer.ConnectAndSayHello(pipe, Cred().Secret);
        await Wait(async () => await connector.IsConnectedAsync());

        var flooding = Enumerable.Range(0, Calls).Select(_ => connector.GetAccountsAsync()).ToArray();
        Observe(flooding);

        var timer = Stopwatch.StartNew();
        await connector.DisposeAsync();
        timer.Stop();

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10),
            $"DisposeAsync took {timer.Elapsed.TotalSeconds:0.0}s against a bridge that stopped reading");
    }

    /// <summary>
    /// The other direction, and the one a careless deadline breaks: a real bridge that reads its
    /// frames is still answered, and the answers are still correct. A deadline short enough to bite
    /// a healthy peer, or a drop on the wrong branch, fails here and passes everything above.
    /// </summary>
    [Fact]
    public async Task A_bridge_that_reads_is_still_answered()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        await connector.ConnectAsync();
        var adapter = new LoopbackAtasAdapter();
        await using var bridge = new BridgeServer(adapter, pipe);
        bridge.Start();
        await Wait(async () => await connector.IsConnectedAsync());

        // Several in a row, so the gate is genuinely handed on rather than merely taken once.
        for (var i = 0; i < 25; i++)
            Assert.NotEmpty(await connector.GetAccountsAsync().WaitAsync(TimeSpan.FromSeconds(10)));

        var quote = await connector.GetQuoteAsync("ES").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("ES", quote!.Symbol);
    }

    // ---------------------------------------------------------------- helpers

    static void Observe(IEnumerable<Task> tasks)
    {
        foreach (var t in tasks) _ = t.ContinueWith(x => _ = x.Exception, TaskScheduler.Default);
    }

    static async Task Wait(Func<Task<bool>> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("condition was not met in time");
    }

    /// <summary>
    /// A peer that authenticates as this installation's bridge, says a compatible hello so the
    /// connector marks itself connected, and then never reads another byte.
    ///
    /// It authenticates for real. Everything the tests measure happens on the far side of a
    /// handshake the connector accepted, which is the point: this is not an impostor being refused,
    /// it is the legitimate bridge having stopped reading — a suspended ATAS, a blocked strategy
    /// thread, a machine that went to sleep.
    /// </summary>
    sealed class StalledBridgePeer(string pipe) : IAsyncDisposable
    {
        readonly NamedPipeClientStream _p = new(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);

        public static async Task<StalledBridgePeer> ConnectAndSayHello(string pipe, string secret)
        {
            var peer = new StalledBridgePeer(pipe);
            await peer._p.ConnectAsync(10_000);

            var nonce = BridgePipeAuth.NewNonce();
            await peer.WriteAsync(new
            {
                v = Versions.BridgeProtocolVersion,
                op = BridgePipeAuth.Challenge,
                data = new { nonce, proof = BridgePipeAuth.Proof(secret, BridgePipeAuth.BridgeRole, nonce) }
            });

            var answer = Json.Read<BridgeFrame>(await peer.ReadLineAsync(TimeSpan.FromSeconds(10)))!;
            Assert.True(answer.Op == BridgePipeAuth.Response,
                $"the connector did not accept the handshake: {answer.Op} {answer.Error}");

            await peer.WriteAsync(new
            {
                v = Versions.BridgeProtocolVersion,
                op = BridgeOps.Hello,
                data = new BridgeHello
                {
                    BridgeProtocolVersion = Versions.BridgeProtocolVersion,
                    AccountId = "ATAS-STALLED",
                    IsSimulated = true
                }
            });
            return peer;
        }

        Task WriteAsync(object frame) =>
            _p.WriteAsync(Encoding.UTF8.GetBytes(Json.Write(frame) + "\n")).AsTask();

        async Task<string> ReadLineAsync(TimeSpan bound)
        {
            var buf = new byte[8192];
            var ms = new MemoryStream();
            while (true)
            {
                var n = await _p.ReadAsync(buf).AsTask().WaitAsync(bound);
                if (n == 0) throw new IOException("the connector closed the connection before the line ended");
                var nl = Array.IndexOf(buf, (byte)'\n', 0, n);
                if (nl >= 0) { ms.Write(buf, 0, nl); return Encoding.UTF8.GetString(ms.ToArray()); }
                ms.Write(buf, 0, n);
            }
        }

        public ValueTask DisposeAsync() => _p.DisposeAsync();
    }
}

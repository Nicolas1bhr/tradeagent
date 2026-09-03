using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using TradeAgent.TradeCli;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// The agent-facing pipe against a peer that stops reading.
///
/// Same class of coupling as the bridge freeze measured on Windows on 2026-09-01: a reply the far
/// end never reads parks the handler inside a write with no deadline, and nothing can recall a
/// write the kernel has already accepted except closing the handle. It is not only a hostile agent
/// that stops reading — a CLI process that is suspended, swapped out or stuck behind its own stdout
/// does exactly the same thing.
///
/// WHY THE REPLY HERE IS LARGE. On macOS a named pipe is a Unix domain socket and the kernel absorbs
/// 16 KiB without a reader (net.local.stream.sendspace + recvspace, 8192 each — sysctl on the dev
/// Mac), so a small reply lands and the stall is invisible. On Windows the pipe was created with no
/// buffer at all, so the same stall happens on ANY reply, however small. A reply near the frame cap
/// is the one shape that shows the defect on both, and it is a legal reply the product really
/// produces: material-list carries the twenty most recent notes verbatim.
/// </summary>
public class GatewayPipeBackpressureTests
{
    static string NewPipe() => "ta-bp-" + Guid.NewGuid().ToString("n")[..12];

    /// <summary>
    /// Arithmetic, not measured: four notes of 240,000 characters make a material-list reply of
    /// about 960 KB — under the 1 MiB (1 &lt;&lt; 20) cap the server puts on a frame it READS, and
    /// sixty times the 16 KiB the macOS kernel will hold for a reader that never comes.
    /// </summary>
    const int Notes = 4, NoteChars = 240_000;

    /// <summary>Any complete material-list reply here is at least this long; a shorter one was cut off.</summary>
    const int FloorOfTheReply = Notes * NoteChars;

    /// <summary>The engineering event the server records when it drops a peer that stopped reading.</summary>
    const string DropEvent = "peer_stopped_reading";

    static void PlantBigNotes(TradingGateway gw)
    {
        for (var i = 0; i < Notes; i++)
            gw.Materials.AddNote("agent", "planted", MaterialNoteKind.Note, null, null,
                new string((char)('a' + i), NoteChars), DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// A peer that takes one byte of its reply and then stops reading is dropped within the write
    /// deadline, and the drop is on the record with the op and the session that caused it.
    ///
    /// The connection has to be REALLY gone, not just written about: after the drop the peer reaches
    /// end of stream having received less than the reply it stopped reading.
    /// </summary>
    [Fact]
    public async Task A_peer_that_stops_reading_is_dropped_within_the_write_deadline()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        PlantBigNotes(gw);
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { WriteTimeout = TimeSpan.FromSeconds(1) };
        server.Start();

        await using var stalled = await RawAgent.ConnectAndHello(pipe);
        var timer = Stopwatch.StartNew();
        await stalled.WriteAsync(new IpcRequest { Op = Ops.MaterialList, Session = "agent-stalled" });
        // The reply has begun to arrive, so the handler is inside the write now. Then: nothing.
        await stalled.ReadOneByteAsync(TimeSpan.FromSeconds(5));

        var dropped = await WaitForDrop(db, TimeSpan.FromSeconds(4));
        var droppedAfter = timer.Elapsed;
        var drained = await stalled.DrainAsync(TimeSpan.FromSeconds(3));

        Assert.True(dropped is not null,
            $"no '{DropEvent}' engineering event within 4s against a 1s write deadline; draining afterwards saw {drained}");
        Assert.True(droppedAfter < TimeSpan.FromSeconds(4),
            $"dropped after {droppedAfter.TotalSeconds:0.00}s against a 1s deadline");
        Assert.Equal(Ops.MaterialList, dropped.Value.Op);
        Assert.Equal("agent-stalled", dropped.Value.Session);

        Assert.True(drained.Ended, $"the stalled connection is still open after the drop was recorded: {drained}");
        Assert.True(drained.Bytes < FloorOfTheReply, $"the reply was completed instead of cut off: {drained}");
    }

    /// <summary>
    /// One stalled peer must cost nobody else anything: a second agent connects, says hello and is
    /// answered while the first is parked, and is still answered after the first one's deadline has
    /// passed — including for the very reply the first one choked on.
    ///
    /// Handlers are independent tasks, so this holds before the fix as well. It is here so the fix
    /// cannot regress it: a deadline implemented with a shared lock, or by stalling the accept loop,
    /// fails this test.
    /// </summary>
    [Fact]
    public async Task A_second_agent_is_served_while_and_after_another_peer_is_stalled()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        PlantBigNotes(gw);
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { WriteTimeout = TimeSpan.FromSeconds(1) };
        server.Start();

        await using var stalled = await RawAgent.ConnectAndHello(pipe);
        await stalled.WriteAsync(new IpcRequest { Op = Ops.MaterialList, Session = "agent-stalled" });
        await stalled.ReadOneByteAsync(TimeSpan.FromSeconds(5));

        await using var healthy = new PipeClient();
        await healthy.ConnectAsync(10_000, pipe).WaitAsync(TimeSpan.FromSeconds(5));
        var status = await healthy.SendAsync(new IpcRequest { Op = Ops.Status }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(status.Ok, Json.Write(status.Error));

        // Past the stalled peer's deadline now. Still served, and the big reply reaches a reader.
        await Task.Delay(TimeSpan.FromMilliseconds(1500));
        var again = await healthy.SendAsync(new IpcRequest { Op = Ops.Status }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(again.Ok, Json.Write(again.Error));
        var big = await healthy.SendAsync(new IpcRequest { Op = Ops.MaterialList }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(big.Ok, Json.Write(big.Error));
        Assert.Equal(Notes, ((JsonElement)big.Data!).GetProperty("recent_notes").GetArrayLength());
    }

    /// <summary>
    /// Shutdown neither waits on a stalled writer nor leaves it behind.
    ///
    /// The deadline is left at its default here ON PURPOSE: ten seconds is longer than every bound
    /// below, so if the stalled connection ends, it was shutdown that ended it and not the deadline.
    /// </summary>
    [Fact]
    public async Task Shutdown_does_not_wait_on_a_stalled_writer_and_does_not_leave_it_behind()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        PlantBigNotes(gw);
        var pipe = NewPipe();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();

        await using var stalled = await RawAgent.ConnectAndHello(pipe);
        await stalled.WriteAsync(new IpcRequest { Op = Ops.MaterialList, Session = "agent-stalled" });
        await stalled.ReadOneByteAsync(TimeSpan.FromSeconds(5));

        var timer = Stopwatch.StartNew();
        await server.DisposeAsync();
        timer.Stop();
        var drained = await stalled.DrainAsync(TimeSpan.FromSeconds(3));

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5),
            $"DisposeAsync took {timer.Elapsed.TotalSeconds:0.0}s against a peer that never reads");
        Assert.True(drained.Ended, $"after shutdown the stalled connection is still open: {drained}");
        Assert.True(drained.Bytes < FloorOfTheReply,
            $"shutdown let the stalled reply run to completion instead of dropping it: {drained}");
    }

    /// <summary>
    /// The other direction: a reply near the frame cap still reaches a peer that reads it, intact
    /// and in one piece, under the product's default deadline — and the connection is good for the
    /// next request afterwards. A deadline that bites a healthy peer on a large reply, or a buffer
    /// choice that truncates one, fails here.
    /// </summary>
    [Fact]
    public async Task A_reply_near_the_frame_cap_still_round_trips_intact()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        PlantBigNotes(gw);
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();

        await using var agent = await RawAgent.ConnectAndHello(pipe);
        await agent.WriteAsync(new IpcRequest { Op = Ops.MaterialList });
        var line = await agent.ReadLineAsync(TimeSpan.FromSeconds(10));

        Assert.InRange(Encoding.UTF8.GetByteCount(line), FloorOfTheReply, (1 << 20) - 1);
        var reply = Json.Read<IpcResponse>(line)!;
        Assert.True(reply.Ok, Json.Write(reply.Error));
        var texts = ((JsonElement)reply.Data!).GetProperty("recent_notes").EnumerateArray()
            .Select(n => n.GetProperty("text").GetString()!).OrderBy(t => t).ToList();
        var planted = Enumerable.Range(0, Notes).Select(i => new string((char)('a' + i), NoteChars)).ToList();
        Assert.Equal(planted, texts);

        await agent.WriteAsync(new IpcRequest { Op = Ops.Status });
        Assert.True(Json.Read<IpcResponse>(await agent.ReadLineAsync(TimeSpan.FromSeconds(5)))!.Ok);
    }

    /// <summary>
    /// THE REPLY IS LOST AFTER THE ORDER IS ALREADY AT THE BROKER, and the request id has to survive
    /// that or the agent's only recovery is a second real order.
    ///
    /// Found by review of a0aa1a7: the drop was recorded with request_id NULL, so nothing on either
    /// side of the pipe still knew which order the lost reply belonged to. The CLI mints the id, the
    /// order fills, the reply cannot be delivered — and with the id gone the agent's next move is a
    /// fresh id, which is a second position it only asked for once.
    ///
    /// The order carries a large comment so its own reply cannot fit the socket buffer: the reply
    /// echoes the intent back as parameters_json, so this is a dispatched order whose reply is
    /// genuinely undeliverable, not a stall arranged around one.
    /// </summary>
    [Fact]
    public async Task A_dispatched_order_whose_reply_is_lost_keeps_its_request_id_and_replays()
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { WriteTimeout = TimeSpan.FromSeconds(1) };
        server.Start();

        const string rid = "cli-lostreply-1";
        var order = new IpcRequest
        {
            Op = Ops.Buy,
            Session = "agent-lost",
            RequestId = rid,
            Args = new()
            {
                ["symbol"] = JsonSerializer.SerializeToElement("ES"),
                ["quantity"] = JsonSerializer.SerializeToElement("1"),
                // Arithmetic, not measured: 64 KiB is four times the ~16 KiB the macOS kernel holds
                // for a reader that never comes, so the reply echoing it cannot land in the buffer.
                ["comment"] = JsonSerializer.SerializeToElement(new string('c', 64 * 1024))
            }
        };

        await using (var stalled = await RawAgent.ConnectAndHello(pipe))
        {
            await stalled.WriteAsync(order);
            await stalled.ReadOneByteAsync(TimeSpan.FromSeconds(5));   // the reply has begun; then nothing
            var dropped = await WaitForDrop(db, TimeSpan.FromSeconds(6));
            Assert.True(dropped is not null, "the stalled peer was never dropped, so nothing was recorded about it");
            Assert.Equal(Ops.Buy, dropped.Value.Op);
            Assert.Contains(rid, dropped.Value.Metadata);
        }

        // The order really did reach the broker. This is the whole hazard: the trade happened and
        // the only acknowledgement of it was thrown away.
        Assert.Single(conn.Broker.Orders);
        Assert.Equal(ExecutionState.FILLED, gw.GetRequest(rid)!.State);

        // And the recovery the CLI now tells the agent to perform actually works: same id, new
        // connection, the stored outcome comes back and NO second order is placed.
        await using var replay = new PipeClient();
        await replay.ConnectAsync(10_000, pipe).WaitAsync(TimeSpan.FromSeconds(5));
        var reply = await replay.SendAsync(new IpcRequest
        {
            Op = Ops.Buy,
            Session = "agent-lost",
            RequestId = rid,
            Args = new()
            {
                ["symbol"] = JsonSerializer.SerializeToElement("ES"),
                ["quantity"] = JsonSerializer.SerializeToElement("1")
            }
        }).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(reply.Ok, Json.Write(reply.Error));
        var replayed = (JsonElement)reply.Data!;
        Assert.Equal(rid, replayed.GetProperty("request_id").GetString());
        Assert.Equal("FILLED", replayed.GetProperty("state").GetString());
        Assert.Single(conn.Broker.Orders);
    }

    /// <summary>
    /// A SLOW READER IS NOT A STOPPED READER, and the deadline has to be able to tell them apart.
    ///
    /// Found by review of a0aa1a7: the deadline bounded the whole write, which makes it a throughput
    /// floor of (reply size / timeout) rather than a stalled-peer detector. A peer reading steadily
    /// at 79 KiB/s was dropped at 10.1 s on a ~1 MiB reply and recorded as having stopped reading —
    /// a healthy agent on a busy machine, disconnected mid-order and then libelled in the log.
    ///
    /// The reader here is paced well under the OLD floor (~960 KiB/s at this 1 s deadline) and well
    /// over the new one (~8 KiB/s). The two assertions together are what cannot both hold under a
    /// total-duration bound: the whole reply arrived, AND it took several times the deadline to do it.
    /// </summary>
    [Fact]
    public async Task A_slow_but_continuous_reader_is_not_mistaken_for_a_stalled_one()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        PlantBigNotes(gw);
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { WriteTimeout = TimeSpan.FromSeconds(1) };
        server.Start();

        await using var agent = await RawAgent.ConnectAndHello(pipe);
        await agent.WriteAsync(new IpcRequest { Op = Ops.MaterialList, Session = "agent-slow" });

        // ~16 KiB every 60 ms is about 260 KiB/s: a quarter of the old floor, thirty times the new.
        var timer = Stopwatch.StartNew();
        var line = await agent.ReadLineSlowlyAsync(16 * 1024, TimeSpan.FromMilliseconds(60), TimeSpan.FromSeconds(60));
        timer.Stop();

        Assert.InRange(Encoding.UTF8.GetByteCount(line), FloorOfTheReply, (1 << 20) - 1);
        Assert.True(timer.Elapsed > TimeSpan.FromSeconds(2),
            $"the reply arrived in {timer.Elapsed.TotalSeconds:0.0}s, which is too fast to prove anything about a 1s deadline");
        Assert.True(await WaitForDrop(db, TimeSpan.Zero) is null,
            "a peer that read every byte was recorded as having stopped reading");

        // Still a working connection afterwards, not merely an un-dropped one.
        await agent.WriteAsync(new IpcRequest { Op = Ops.Status });
        Assert.True(Json.Read<IpcResponse>(await agent.ReadLineAsync(TimeSpan.FromSeconds(5)))!.Ok);
    }

    /// <summary>
    /// SHUTDOWN WAITS FOR A HANDLER THAT IS INSIDE THE GATEWAY, not merely for one blocked on I/O.
    ///
    /// Found by review of a0aa1a7. Registering the PIPES fixed the abandoned-connection half and
    /// missed this one: a handler parked in the middle of a place — through to the broker, waiting
    /// on it — is doing no I/O at all, so closing its pipe does not reach it and disposal walked
    /// past. It then outlived the server, the gateway AND the database, so the settle that moves the
    /// order out of DISPATCHING ran against a closed connection or never ran, and an order that had
    /// really reached the broker was left DISPATCHING for ever.
    ///
    /// `AppHost.DisposeAsync` runs server (`:274`) then gateway (`:275`) then database (`:276`), so a
    /// handler drained inside the server's disposal finishes while both are still open. That order is
    /// what makes waiting here worth anything.
    /// </summary>
    [Fact]
    public async Task Shutdown_waits_for_a_handler_that_is_inside_the_gateway_placing_an_order()
    {
        // The broker takes its time, so the handler is provably still inside PlaceAsync when the
        // server is disposed — not merely racing it.
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { LatencyMs = 1500 });
        using var _1 = db;
        var pipe = NewPipe();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();

        const string rid = "cli-inflight-1";
        await using var agent = await RawAgent.ConnectAndHello(pipe);
        await agent.WriteAsync(new IpcRequest
        {
            Op = Ops.Buy,
            Session = "agent-inflight",
            RequestId = rid,
            Args = new()
            {
                ["symbol"] = JsonSerializer.SerializeToElement("ES"),
                ["quantity"] = JsonSerializer.SerializeToElement("1")
            }
        });

        // Wait until the order is genuinely in flight, then pull the server out from under it.
        await WaitFor(() => gw.GetRequest(rid) is not null, TimeSpan.FromSeconds(5));
        var timer = Stopwatch.StartNew();
        await server.DisposeAsync();
        timer.Stop();

        Assert.True(timer.Elapsed > TimeSpan.FromSeconds(1),
            $"DisposeAsync returned in {timer.Elapsed.TotalMilliseconds:0}ms — it did not wait for the handler placing an order");

        // Read the state with NO polling: if disposal waited, the settle is already durable.
        var state = gw.GetRequest(rid)!.State;
        Assert.True(state is not ExecutionState.DISPATCHING,
            $"an order that reached the broker was left {state} when the server shut down");
        Assert.Equal(ExecutionState.FILLED, state);
        Assert.Single(conn.Broker.Orders);
    }

    /// <summary>
    /// The agent pipe's own deadline, AT THE SHIPPED DEFAULT. Ten seconds of real waiting, because a
    /// drop proven at a 1 s test value proves the mechanism and not the product.
    /// </summary>
    [Fact]
    public async Task A_stalled_peer_is_dropped_at_the_shipped_default_deadline()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        PlantBigNotes(gw);
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);   // WriteTimeout: untouched
        Assert.Equal(TimeSpan.FromSeconds(10), server.WriteTimeout);
        server.Start();

        await using var stalled = await RawAgent.ConnectAndHello(pipe);
        var timer = Stopwatch.StartNew();
        await stalled.WriteAsync(new IpcRequest { Op = Ops.MaterialList, Session = "agent-shipped" });
        await stalled.ReadOneByteAsync(TimeSpan.FromSeconds(5));

        var dropped = await WaitForDrop(db, TimeSpan.FromSeconds(20));
        timer.Stop();

        Assert.True(dropped is not null, $"no drop within 20s at the shipped 10s deadline (elapsed {timer.Elapsed.TotalSeconds:0.0}s)");
        Assert.InRange(timer.Elapsed, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20));
        Assert.Equal(Ops.MaterialList, dropped.Value.Op);
    }

    // ---------------------------------------------------------------- helpers

    static async Task WaitFor(Func<bool> condition, TimeSpan bound)
    {
        var deadline = DateTime.UtcNow + bound;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("condition was not met in time");
    }

    static async Task<(string Op, string? Session, string Metadata)?> WaitForDrop(Database db, TimeSpan bound)
    {
        var deadline = DateTime.UtcNow + bound;
        while (true)
        {
            var hit = db.Read(_ =>
            {
                using var c = db.Cmd(
                    "SELECT session, metadata FROM engineering_log WHERE component='Ipc' AND event=$e ORDER BY id LIMIT 1",
                    ("$e", DropEvent));
                using var r = c.ExecuteReader();
                if (!r.Read()) return ((string?, string)?)null;
                return (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? "{}" : r.GetString(1));
            });
            if (hit is { } h)
            {
                using var doc = JsonDocument.Parse(h.Item2);
                var op = doc.RootElement.TryGetProperty("op", out var o) ? o.GetString() ?? "" : "";
                return (op, h.Item1, h.Item2);
            }
            if (DateTime.UtcNow >= deadline) return null;
            await Task.Delay(50);
        }
    }

    /// <summary>What a drain of the stalled peer's side of the pipe found.</summary>
    sealed record Drained(bool Ended, long Bytes, string How);

    /// <summary>
    /// An agent that speaks bytes, not lines, so it can take exactly as much of a reply as the test
    /// wants and no more. It authenticates for real: everything here happens on the far side of a
    /// successful hello.
    /// </summary>
    sealed class RawAgent(string pipe) : IAsyncDisposable
    {
        readonly NamedPipeClientStream _p = new(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);

        public static async Task<RawAgent> ConnectAndHello(string pipe)
        {
            var a = new RawAgent(pipe);
            await a._p.ConnectAsync(10_000);
            await a.WriteAsync(new IpcRequest { Op = Ops.Hello, Token = IpcToken.Ensure() });
            var hello = Json.Read<IpcResponse>(await a.ReadLineAsync(TimeSpan.FromSeconds(5)))!;
            Assert.True(hello.Ok, "hello was refused: " + Json.Write(hello.Error));
            return a;
        }

        public Task WriteAsync(IpcRequest r) =>
            _p.WriteAsync(Encoding.UTF8.GetBytes(Json.Write(r) + "\n")).AsTask();

        public async Task ReadOneByteAsync(TimeSpan bound)
        {
            var one = new byte[1];
            var n = await _p.ReadAsync(one).AsTask().WaitAsync(bound);
            Assert.Equal(1, n);
        }

        /// <summary>
        /// One whole frame, read at a deliberate pace: <paramref name="perChunk"/> bytes, then a
        /// pause. Slow, continuous, and never stopped — the shape the deadline used to misread.
        /// </summary>
        public async Task<string> ReadLineSlowlyAsync(int perChunk, TimeSpan pause, TimeSpan bound)
        {
            var buf = new byte[perChunk];
            var ms = new MemoryStream();
            var deadline = DateTime.UtcNow + bound;
            while (DateTime.UtcNow < deadline)
            {
                var n = await _p.ReadAsync(buf).AsTask().WaitAsync(bound);
                if (n == 0) throw new IOException("the server closed the connection before the line ended");
                var nl = Array.IndexOf(buf, (byte)'\n', 0, n);
                if (nl >= 0) { ms.Write(buf, 0, nl); return Encoding.UTF8.GetString(ms.ToArray()); }
                ms.Write(buf, 0, n);
                await Task.Delay(pause);
            }
            throw new TimeoutException($"the reply did not finish within {bound.TotalSeconds:0}s");
        }

        /// <summary>One whole frame. Nothing arrives on this pipe unasked, so reading past the newline cannot happen.</summary>
        public async Task<string> ReadLineAsync(TimeSpan bound)
        {
            var buf = new byte[65536];
            var ms = new MemoryStream();
            while (true)
            {
                var n = await _p.ReadAsync(buf).AsTask().WaitAsync(bound);
                if (n == 0) throw new IOException("the server closed the connection before the line ended");
                var nl = Array.IndexOf(buf, (byte)'\n', 0, n);
                if (nl >= 0) { ms.Write(buf, 0, nl); return Encoding.UTF8.GetString(ms.ToArray()); }
                ms.Write(buf, 0, n);
            }
        }

        /// <summary>
        /// Reads everything the server still has for us and reports how it ended: end of stream or
        /// a broken pipe means the server let go; a read that outlives the bound means it did not.
        /// </summary>
        public async Task<Drained> DrainAsync(TimeSpan bound)
        {
            var buf = new byte[65536];
            long total = 0;
            while (true)
            {
                var read = _p.ReadAsync(buf).AsTask();
                int n;
                try { n = await read.WaitAsync(bound); }
                catch (TimeoutException) { Observe(read); return new Drained(false, total, $"still open: no end of stream within {bound.TotalSeconds:0}s"); }
                catch (IOException ex) { return new Drained(true, total, "broken pipe: " + ex.Message); }
                catch (ObjectDisposedException) { return new Drained(true, total, "pipe disposed"); }
                if (n == 0) return new Drained(true, total, "end of stream");
                total += n;
            }
        }

        static void Observe(Task t) => _ = t.ContinueWith(x => _ = x.Exception, TaskScheduler.Default);

        public ValueTask DisposeAsync() => _p.DisposeAsync();
    }
}

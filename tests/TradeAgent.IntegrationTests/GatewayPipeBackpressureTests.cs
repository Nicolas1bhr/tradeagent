using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using TradeAgent.Connectors.Atas;
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
///
/// CATEGORY "Timing", because two of this class's tests have gone red on `windows-latest` and
/// nowhere else — one in U-win-flakes, one in U-win-timing — and both were a fixture margin that
/// did not cover what the work costs on that runner. <see cref="RunnerSpeedProbeTests"/> says what
/// that category buys and what it costs; the short version is that this class is re-run once on
/// windows-latest and only there, and the first failure is still logged and still uploaded.
/// </summary>
[Trait("Category", "Timing")]
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

    /// <summary>
    /// The drain has to outlast the path it is draining, and this is what keeps the two numbers
    /// honest when someone changes one of them. A hand-derived constant in one file computed from
    /// constants in another is a claim with an expiry date.
    /// </summary>
    [Fact]
    public void The_shutdown_drain_outlasts_the_connectors_worst_case_order()
    {
        var connector = new AtasConnector("ta-drain-arith");
        using var db = TestEnv.NewDb();

        // THE GATEWAY HOLDS THE CONNECTOR THE DRAIN IS ABOUT. It used to hold a FakeConnector while
        // this assertion compared against a separately-constructed AtasConnector, so the two numbers
        // could agree while nothing connected them — which is the disconnection Codex C3 names.
        var gw = new TradingGateway(db, connector, new HealthRegistry());
        var server = new GatewayPipeServer(gw, "tok", "ta-drain-arith-2");

        // 10 + 30 + 10 at the shipped values. If a connector deadline grows, this fails here rather
        // than by abandoning an order at shutdown six months later.
        Assert.Equal(TimeSpan.FromSeconds(50), connector.WorstCaseOrderPath);

        // FIVE of those in series, plus what the HANDLER costs on top of its connector calls, plus
        // the settle: a handler is not one connector call (Codex F2), three was the wrong count for
        // the longest one (Codex round-8 CHECK d) — a cold placement issues five — and a row is the
        // connector chain rather than the handler (verifier round-11 L-2). This is the number an
        // operator can experience at shutdown.
        Assert.Equal(5, GatewayPipeServer.SerialConnectorCallsPerHandler);
        Assert.Equal(TimeSpan.FromSeconds(1), GatewayPipeServer.HandlerOverhead);
        Assert.Equal(TimeSpan.FromSeconds(256), server.HandlerDrainTimeout);

        // And the risk-reducing shape is covered too, rather than assumed smaller: at shipped values
        // it is 2 s of emergency budget plus one ordinary Place, which the 255 above already exceeds.
        Assert.Equal(TimeSpan.FromSeconds(2), connector.EmergencyBudget);
        Assert.True(server.HandlerDrainTimeout > connector.WorstCaseOrderPath,
            $"the drain bound {server.HandlerDrainTimeout.TotalSeconds:0}s does not outlast the connector's " +
            $"worst-case order path {connector.WorstCaseOrderPath.TotalSeconds:0}s — a shutdown mid-order abandons it");
    }

    /// <summary>
    /// AND IT FOLLOWS THE DEADLINES RATHER THAN QUOTING THEM. Codex C3: 55 s was a literal, correct
    /// for the shipped values and silently wrong for any others — and constructing a connector with
    /// different deadlines is a supported thing to do. Codex's own arithmetic is the fixture: an RPC
    /// timeout of 60 s makes the worst path 100 s, against which a 55 s drain abandons a DISPATCHING
    /// order, which is the state cc7006e and 02aad9a exist to prevent.
    /// </summary>
    [Fact]
    public void The_shutdown_drain_follows_the_connectors_deadlines_when_they_change()
    {
        using var db = TestEnv.NewDb();
        var slow = new AtasConnector("ta-drain-slow", TimeSpan.FromSeconds(60));   // 10 + 30 + 60
        var gw = new TradingGateway(db, slow, new HealthRegistry());
        var server = new GatewayPipeServer(gw, "tok", "ta-drain-slow-2");

        Assert.Equal(TimeSpan.FromSeconds(100), slow.WorstCaseOrderPath);
        Assert.True(server.HandlerDrainTimeout > slow.WorstCaseOrderPath,
            $"the drain is {server.HandlerDrainTimeout.TotalSeconds:0}s against a {slow.WorstCaseOrderPath.TotalSeconds:0}s " +
            "worst path — it was written down rather than derived, so a supported deadline change abandons an order again");

        // AND AN EXPLICIT VALUE MAY ONLY LENGTHEN IT. Seven seconds against a hundred-second worst
        // path is the abandoned DISPATCHING order this drain exists to prevent, asked for by the
        // caller who was trying to configure it — so it is refused and the derived bound stands
        // (Codex round-8 CHECK d). It used to win outright, which made the whole derivation one
        // constructor argument away from meaningless.
        var undersized = new GatewayPipeServer(gw, "tok", "ta-drain-slow-3") { HandlerDrainTimeout = TimeSpan.FromSeconds(7) };
        Assert.Equal(server.HandlerDrainTimeout, undersized.HandlerDrainTimeout);
        Assert.True(undersized.HandlerDrainTimeout > slow.WorstCaseOrderPath,
            $"a caller shortened the drain to {undersized.HandlerDrainTimeout.TotalSeconds:0}s against a " +
            $"{slow.WorstCaseOrderPath.TotalSeconds:0}s worst path");

        // The other direction, so this is a clamp and not an override that has simply been ignored:
        // a caller who asks for LONGER means it and gets it.
        var generous = new GatewayPipeServer(gw, "tok", "ta-drain-slow-4") { HandlerDrainTimeout = TimeSpan.FromHours(1) };
        Assert.Equal(TimeSpan.FromHours(1), generous.HandlerDrainTimeout);
    }

    /// <summary>
    /// THE INVARIANT, ASSERTED OVER EVERY KNOB A CALLER CAN TURN: the drain is never shorter than the
    /// composite chain it is derived from.
    ///
    /// The settle term is a MARGIN on top of that chain, and shortening it shortens a handler's
    /// write-back window — which is what `SettleAfterCancelTimeout` already means and already allows.
    /// What must not be reachable is a drain that gives up while the chain is still legitimately
    /// running, by any route: an undersized explicit drain, a zero settle, or both together.
    /// </summary>
    [Fact]
    public void No_combination_of_settings_makes_the_drain_shorter_than_the_chain()
    {
        using var db = TestEnv.NewDb();
        var conn = new AtasConnector("ta-drain-invariant", TimeSpan.FromSeconds(60));   // 100 s worst path
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        var chain = GatewayPipeServer.SerialConnectorCallsPerHandler * conn.WorstCaseOperationPath;

        // A ROW BOUNDS THE CONNECTOR CHAIN, NOT THE HANDLER. The handler also reads and parses a
        // frame, writes its request record, and writes a reply — work no connector deadline
        // describes, and work this test used to leave uncovered because it compared the drain
        // against the very quantity the rows already are. The margin was `SettleAfterCancelTimeout`,
        // added once and `init`-settable to ZERO, at which point the drain equalled the chain
        // exactly and any handler overhead at all was outside it: measured at W=300 ms, E=900 ms,
        // `cancel-all` cost 917 ms against a 900 ms row (verifier round-11 L-2).
        var bound = chain + GatewayPipeServer.HandlerOverhead;

        foreach (var server in new[]
                 {
                     new GatewayPipeServer(gw, "tok", "ta-inv-1"),
                     new GatewayPipeServer(gw, "tok", "ta-inv-2") { HandlerDrainTimeout = TimeSpan.Zero },
                     new GatewayPipeServer(gw, "tok", "ta-inv-3") { SettleAfterCancelTimeout = TimeSpan.Zero },
                     new GatewayPipeServer(gw, "tok", "ta-inv-4")
                     {
                         HandlerDrainTimeout = TimeSpan.FromMilliseconds(1),
                         SettleAfterCancelTimeout = TimeSpan.Zero
                     }
                 })
            Assert.True(server.HandlerDrainTimeout >= bound,
                $"the drain came out at {server.HandlerDrainTimeout.TotalSeconds:0.000}s against a " +
                $"{chain.TotalSeconds:0.000}s connector chain plus " +
                $"{GatewayPipeServer.HandlerOverhead.TotalSeconds:0.000}s of handler — a caller " +
                "shortened it below the work it has to cover");

        // AND THE MARGIN IS NOT THE SETTLE WINDOW. They are different quantities: one is what a
        // handler costs on top of its connector calls, the other is how long it gets AFTER it is
        // cancelled to write down what it already knows. Conflating them is what let a caller
        // configure the first away by changing the second.
        Assert.True(GatewayPipeServer.HandlerOverhead > TimeSpan.Zero);
    }

    /// <summary>
    /// AT THE SHIPPED DRAIN BOUND. The broker takes eight seconds, which is longer than the five the
    /// bound used to be and well inside the thirty-five it now is. Under the old bound this exact
    /// shape produced <c>DisposeAsync returned after 5.01s … unfinished:1 … state=DISPATCHING</c>.
    /// </summary>
    [Fact]
    public async Task An_order_slower_than_the_old_drain_bound_still_settles_before_shutdown_returns()
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var _1 = db;
        var pipe = NewPipe();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);   // HandlerDrainTimeout: untouched
        server.Start();

        const string rid = "cli-slowsettle-1";
        await using var agent = await RawAgent.ConnectAndHello(pipe);

        // Warm the gateway's instrument and account lookups first, then slow the broker down. The
        // fault applies to EVERY connector call, so arming it up front would put the latency in the
        // pre-flight checks instead of where this test needs it: inside the dispatch.
        await WarmUp(agent);
        conn.Faults.LatencyMs = 6000;

        // The drain is derived from the connector, so arming the latency is what makes it long
        // enough — which is the property, rather than the literal that used to be asserted here.
        Assert.True(server.HandlerDrainTimeout > TimeSpan.FromSeconds(6),
            $"the derived drain is {server.HandlerDrainTimeout.TotalSeconds:0}s against a 6 s broker");
        await agent.WriteAsync(new IpcRequest
        {
            Op = Ops.Buy,
            Session = "agent-slowsettle",
            RequestId = rid,
            Args = new()
            {
                ["symbol"] = JsonSerializer.SerializeToElement("ES"),
                ["quantity"] = JsonSerializer.SerializeToElement("1")
            }
        });
        await WaitFor(() => gw.GetRequest(rid) is not null, TimeSpan.FromSeconds(30));

        var timer = Stopwatch.StartNew();
        await server.DisposeAsync();
        timer.Stop();

        // Past the five seconds the bound used to be: under that bound this exact shape produced
        // "DisposeAsync returned after 5.01s … unfinished:1 … state=DISPATCHING".
        Assert.True(timer.Elapsed > TimeSpan.FromSeconds(5),
            $"DisposeAsync returned in {timer.Elapsed.TotalSeconds:0.00}s — it gave up on an order that was still in progress");
        Assert.Equal(ExecutionState.FILLED, gw.GetRequest(rid)!.State);
        Assert.Single(conn.Broker.Orders);
        Assert.Null(ReadEngineering(db, "handlers_did_not_finish"));
    }

    /// <summary>
    /// A HANDLER IS NOT ONE CONNECTOR CALL, AND THE DRAIN HAS TO COVER THE WHOLE OF IT.
    ///
    /// Codex F2, and its own check. Deriving the drain from a single connector operation was the
    /// remaining half of C3: `cancel-all` reads the working orders, resolves each target and then
    /// cancels, so with a four-second connector the handler needs twelve seconds against a derived
    /// drain of nine — and the active cancel is left DISPATCHING, which is the state cc7006e and
    /// 02aad9a exist to prevent.
    ///
    /// The emergency budget is widened for this fixture on purpose. Round 8 bounds a risk-reducing
    /// OPERATION at two seconds, so at shipped values this sweep could no longer take twelve; what
    /// is on trial here is the drain's arithmetic, not that bound, and an ordinary multi-call
    /// handler (a modify: resolve, then modify) reaches it with nothing widened.
    /// </summary>
    [Fact]
    public async Task Disposal_covers_a_handler_that_makes_several_connector_calls_in_series()
    {
        var db = TestEnv.NewDb();
        using var _1 = db;
        var conn = new FakeConnector(new FakeBroker(), new FaultProfile { Fill = FillBehaviour.LeaveWorking })
        {
            EmergencyBudget = TimeSpan.FromSeconds(30)   // not what is on trial; see above
        };
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOrdersPerMinute = 100;
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        var pipe = NewPipe();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);   // drain untouched
        server.Start();
        await using var agent = await RawAgent.ConnectAndHello(pipe);
        await WarmUp(agent);

        await agent.WriteAsync(new IpcRequest
        {
            Op = Ops.Buy,
            Session = "agent-composite",
            RequestId = "cli-composite-buy",
            Args = new()
            {
                ["symbol"] = JsonSerializer.SerializeToElement("ES"),
                ["quantity"] = JsonSerializer.SerializeToElement("1"),
                ["limit"] = JsonSerializer.SerializeToElement("1")
            }
        });
        await WaitFor(() => gw.GetRequest("cli-composite-buy")?.State == ExecutionState.WORKING, TimeSpan.FromSeconds(30));

        // Four seconds a call: orders read, target resolution, cancel — twelve in series.
        conn.Faults.LatencyMs = 4000;
        Assert.True(server.HandlerDrainTimeout > TimeSpan.FromSeconds(12),
            $"the derived drain is {server.HandlerDrainTimeout.TotalSeconds:0}s against a handler that needs 12 s — " +
            "it is still derived from one connector call rather than the chain the handler issues");

        await agent.WriteAsync(new IpcRequest { Op = Ops.CancelAll, Session = "agent-composite", RequestId = "cli-composite-sweep" });
        await Task.Delay(500);   // its first read is under way

        await server.DisposeAsync();

        Assert.Equal(0, Dispatching(db));
        Assert.Null(ReadEngineering(db, "handlers_did_not_finish"));
    }

    /// <summary>
    /// COUNT THE CHAIN, DO NOT ASSUME IT — and a cold placement's chain is FIVE, not three.
    ///
    /// Round 8 derived the drain from "a prerequisite read, a target resolution, the mutation",
    /// which is a `modify` and is not the longest handler. Codex round-8 CHECK d: a cold
    /// `TradingGateway.PlaceAsync` issues five connector calls, each awaited before the next — the
    /// account, the open positions, a quote, the instrument list (read once and cached, so only a
    /// cold process pays it) and then the order.
    ///
    /// This is the §9.9 assertion for the class: the number in `SerialConnectorCallsPerHandler` is
    /// re-derived from the handler that actually runs, over the real pipe, so a handler that grows a
    /// sixth call fails HERE rather than by shortening a shutdown drain six months later. The ops are
    /// named as well as counted, because a count alone would still hold if one call were swapped for
    /// a different one.
    /// </summary>
    [Fact]
    public async Task A_cold_placement_issues_no_more_connector_calls_than_the_drain_assumes()
    {
        var db = TestEnv.NewDb();
        using var _1 = db;
        var inner = new FakeConnector(new FakeBroker());
        var counting = new CountingConnector(inner);
        var gw = new TradingGateway(db, counting, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = inner.Broker.AccountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOrdersPerMinute = 100;

            // AN ALLOWLIST, WHICH IS WHAT A CONFIGURED INSTALLATION HAS — and it is what keeps the
            // instrument cache cold. `RefreshHealthAsync` reads the instrument list only to pick a
            // symbol to quote when nothing is allowlisted, so on a configured install nothing warms
            // that cache and the FIRST placement is the one that pays for it. Cold is the normal
            // state here, not a contrived one.
            s.Risk.InstrumentAllowlist.Add("ES");
        });
        await counting.ConnectAsync();
        await gw.RefreshHealthAsync();

        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var agent = await RawAgent.ConnectAndHello(pipe);

        // NO WARM-UP, deliberately: cold is the state every placement is in before anything else has
        // warmed the caches, and at shutdown it is the placement most likely to still be in flight.
        counting.Calls.Clear();
        await agent.WriteAsync(new IpcRequest
        {
            Op = Ops.Buy,
            Session = "agent-chain",
            RequestId = "cli-chain-1",
            Args = new()
            {
                ["symbol"] = JsonSerializer.SerializeToElement("ES"),
                ["quantity"] = JsonSerializer.SerializeToElement("1")
            }
        });
        await WaitFor(() => gw.GetRequest("cli-chain-1")?.State == ExecutionState.FILLED, TimeSpan.FromSeconds(30));

        var chain = counting.Calls.ToArray();
        Assert.Equal(
            new[] { "account", "positions", "quote", "instruments", "place" },
            chain);
        Assert.True(GatewayPipeServer.SerialConnectorCallsPerHandler >= chain.Length,
            $"a cold placement issues {chain.Length} connector calls in series ({string.Join(" -> ", chain)}) " +
            $"against a drain derived from {GatewayPipeServer.SerialConnectorCallsPerHandler} — the drain gives up " +
            "while the handler is still legitimately running, and the order is abandoned DISPATCHING");
    }

    /// <summary>
    /// DISPOSAL DURING A COLD PLACEMENT — Codex round-8's acceptance for the drain, and the state it
    /// produces when the count is wrong.
    ///
    /// The latency here is UNCANCELLABLE on purpose. A merely slow broker unwinds the moment disposal
    /// cancels the token and records UNKNOWN, so the harm hides as "an order that needs reconciling";
    /// a call that does not honour the token is what leaves the row DISPATCHING with nothing coming
    /// to change it, which is the exact state cc7006e and 02aad9a exist to prevent.
    ///
    /// The disposal is early, while the handler is in its FIRST read — otherwise the drain only has
    /// to cover whatever is left of the chain, and a bound derived from three calls covers that
    /// easily. The whole point is a handler with most of its chain still ahead of it.
    /// </summary>
    [Fact]
    public async Task Disposal_covers_a_cold_placement_and_not_just_the_call_it_is_inside()
    {
        // The allowlist is what keeps the instrument cache cold, and it is what a configured
        // installation has: health reads the instrument list only when nothing is allowlisted.
        var (gw, conn, db) = await TestEnv.Ready(s => s.Risk.InstrumentAllowlist.Add("ES"));
        using var _1 = db;
        var pipe = NewPipe();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe)
        {
            SettleAfterCancelTimeout = TimeSpan.FromMilliseconds(200)
        };
        server.Start();
        await using var agent = await RawAgent.ConnectAndHello(pipe);

        // A second a call, and the call will not let go when asked. Five calls, so the handler needs
        // five seconds and the drain is the only thing standing between it and an abandoned order.
        conn.Faults.UncancellableLatencyMs = 1000;
        Assert.Equal(TimeSpan.FromSeconds(1), conn.WorstCaseOperationPath);
        var drain = server.HandlerDrainTimeout;

        await agent.WriteAsync(new IpcRequest
        {
            Op = Ops.Buy,
            Session = "agent-cold",
            RequestId = "cli-cold-1",
            Args = new()
            {
                ["symbol"] = JsonSerializer.SerializeToElement("ES"),
                ["quantity"] = JsonSerializer.SerializeToElement("1")
            }
        });
        await Task.Delay(1200);   // it is on its second call and three more are still to come

        await server.DisposeAsync();

        // Read the instant disposal returns: the handler goes on running afterwards, so a request
        // that is only settled later was still abandoned by the shutdown.
        var why = $"the drain came out at {drain.TotalSeconds:0.00}s against a five-call chain that needs " +
                  $"{5 * conn.WorstCaseOperationPath.TotalSeconds:0.00}s — disposal walked away from an order in flight";
        Assert.True(Dispatching(db) == 0, $"{Dispatching(db)} request(s) left DISPATCHING: {why}");
        Assert.True(ReadEngineering(db, "handlers_did_not_finish") is null, $"a handler was abandoned: {why}");
        Assert.Equal(ExecutionState.FILLED, gw.GetRequest("cli-cold-1")!.State);
        Assert.Single(conn.Broker.Orders);
    }

    /// <summary>Requests left mid-flight — the state the drain exists to prevent.</summary>
    static int Dispatching(Database db) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT COUNT(*) FROM execution_request WHERE execution_state='DISPATCHING'");
        return Convert.ToInt32(c.ExecuteScalar());
    });

    /// <summary>
    /// A HANDLER THAT IS CANCELLED STILL HAS SOMETHING TO WRITE DOWN, AND DISPOSAL HAS TO WAIT FOR IT.
    ///
    /// Codex F2, the second half. Disposal drained the handlers, then cancelled their token, then
    /// returned. A handler over the bound is cancelled and unwinds THROUGH the catch-all that records
    /// an after-the-wire failure as UNKNOWN — and disposal walked away at exactly that moment, so
    /// AppHost closed the gateway and then the database under a request that was mid-write-back. An
    /// order that may have reached the broker and left no record is the state this whole drain
    /// exists to prevent, produced at the last step by cancelling and not waiting.
    ///
    /// The broker here is slow but WELL BEHAVED — it honours the token — so the handler can finish
    /// the moment it is cancelled, if anybody waits. Nobody did.
    /// </summary>
    [Fact]
    public async Task Disposal_waits_for_a_cancelled_handler_to_record_what_it_knows()
    {
        // THE CONNECTOR UNDER-REPORTS ITS OWN WORST CASE, which is how a drain ends up shorter than
        // the handler now that an explicit `HandlerDrainTimeout` can no longer shorten it. It is also
        // the realistic shape: a vendor call that blocks for longer than the vendor admits.
        var (gw, conn, db) = await ReadyWithDeclaredWorstCase(TimeSpan.FromMilliseconds(20));
        using var _1 = db;
        var pipe = NewPipe();
        // NO OVERRIDE AT ALL — THE SHIPPED FIVE SECONDS. Every value this fixture has invented for
        // the settle window has been wrong on a hosted runner, and each one was defended with an
        // arithmetic that looked sound: 300 ms until U-win-flakes, then 2 s, derived from the ~30 ms
        // this Mac needs times the worst file-IO ratio `RunnerSpeedProbeTests` had seen. That was
        // wrong too — windows-latest failed this exact line at 2 s with `Expected: null /
        // Actual: "error"` (run 33941113025, attempt 2). The window is how long disposal really
        // gives a cancelled handler to write its record and its reply; the product's number for it
        // is 5 s, and a fixture that shortens it is testing a gateway nobody ships. So it is not
        // shortened, there is no fixture margin here left to be wrong, and the price is that the
        // derived drain grows by the difference and the slow call below has to grow with it.
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        // THE PREMISE, TIED TO THE FAULT RATHER THAN TO A LITERAL. The drain has to expire while the
        // handler is still inside its connector call, or the handler finishes on its own and this
        // test passes without ever reaching the cancellation it is about. It was written as "under a
        // second", which stopped being the right comparison the moment the drain grew a
        // handler-overhead term (round 12) — the quantity it was always about is the SLOW CALL.
        //
        // Twelve seconds, because the drain derives as the handler's chain (5 x the 20 ms declared
        // above) + `HandlerOverhead` (1 s) + `SettleAfterCancelTimeout` (5 s) = 6.1 s, and that has
        // to expire INSIDE the call. The assertion is what holds it; the arithmetic is only why the
        // number is 12 and not 5.
        const int slowCallMs = 12_000;                             // cancellable: it unwinds when asked
        Assert.True(server.HandlerDrainTimeout < TimeSpan.FromMilliseconds(slowCallMs),
            $"the derived drain is {server.HandlerDrainTimeout.TotalSeconds:0.00}s against a " +
            $"{slowCallMs} ms call — the handler will finish inside it and never be cancelled, so this " +
            "test would pass without testing anything");
        server.Start();

        const string rid = "cli-settle-on-cancel-1";
        await using var agent = await RawAgent.ConnectAndHello(pipe);
        await WarmUp(agent);
        conn.Faults.LatencyMs = slowCallMs;
        await agent.WriteAsync(new IpcRequest
        {
            Op = Ops.Buy,
            Session = "agent-settle-on-cancel",
            RequestId = rid,
            Args = new()
            {
                ["symbol"] = JsonSerializer.SerializeToElement("ES"),
                ["quantity"] = JsonSerializer.SerializeToElement("1")
            }
        });
        // NINETY SECONDS OF PATIENCE FOR A STATE, against a measured ~36. The fault is a per-call
        // latency and the record appears three calls in, so this wait costs 3 x `slowCallMs`; it was
        // 30 s when the call was 5 s and had to grow with it. It is a fixture's patience and bounds
        // nothing the test asserts — a healthy run leaves it after about 36 s.
        await WaitFor(() => gw.GetRequest(rid) is not null, TimeSpan.FromSeconds(90));

        await server.DisposeAsync();

        // The request is not left mid-flight. Whatever it settled on, it settled on something and
        // wrote it down BEFORE the store could close under it.
        var record = gw.GetRequest(rid);
        Assert.NotNull(record);
        Assert.NotEqual(ExecutionState.DISPATCHING, record!.State);

        // And it is not reported as abandoned, because it was not: it finished, during the wait
        // that was added for it.
        Assert.Null(ReadEngineering(db, "handlers_did_not_finish"));
    }

    /// <summary>
    /// THE CANCELLATION ITSELF SETTLES THE ROW — with the write-back margin configured to NOTHING, so
    /// what is measured is the dispatch path and not disposal's willingness to wait for it.
    ///
    /// The sibling above gives the handler 300 ms after cancellation and asserts it used them. This
    /// one takes that away. `SettleAfterCancelTimeout` is `init`-settable to zero, and at zero
    /// disposal cancels and returns; AppHost then closes the gateway (:275) and the database (:276)
    /// straight after. So a cancelled mutation that settles LATER settles into a store that is being
    /// taken away, and the row it should have written is the one the whole drain exists to prevent —
    /// `DISPATCHING`, unflagged, invisible until the next start's sweep (measured by U2a and recorded
    /// against `TradingGateway` in `docs/CONTRACTS.md`).
    ///
    /// What closes it is the catch-all on every dispatch path: an `OperationCanceledException` out of
    /// a connector call is indefinite like any other, so it reaches `RecordIndefinite`, which pauses
    /// in memory and then writes UNKNOWN — all of it inside the handler's own unwind, before disposal
    /// returns. That is what is asserted here, and it is asserted on the FLAG as well as the state:
    /// a row that leaves DISPATCHING without being flagged is not settled, it is forgotten.
    /// </summary>
    [Fact]
    public async Task A_cancelled_handler_settles_before_disposal_returns_even_with_no_write_back_margin()
    {
        var (gw, conn, db) = await ReadyWithDeclaredWorstCase(TimeSpan.FromMilliseconds(20));
        using var _1 = db;
        var pipe = NewPipe();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe)
        {
            SettleAfterCancelTimeout = TimeSpan.Zero      // the margin, configured away
        };
        const int slowCallMs = 5000;                      // cancellable: it unwinds when asked
        Assert.True(server.HandlerDrainTimeout < TimeSpan.FromMilliseconds(slowCallMs),
            $"the derived drain is {server.HandlerDrainTimeout.TotalSeconds:0.00}s against a {slowCallMs} ms " +
            "call — the handler would finish inside it and never be cancelled");
        server.Start();

        const string rid = "cli-cancel-settles-1";
        await using var agent = await RawAgent.ConnectAndHello(pipe);
        await WarmUp(agent);
        conn.Faults.LatencyMs = slowCallMs;
        await agent.WriteAsync(new IpcRequest
        {
            Op = Ops.Buy,
            Session = "agent-cancel-settles",
            RequestId = rid,
            Args = new()
            {
                ["symbol"] = JsonSerializer.SerializeToElement("ES"),
                ["quantity"] = JsonSerializer.SerializeToElement("1")
            }
        });
        // The premise: the row must be AT the wire when disposal arrives, or there is nothing to
        // settle and this passes without testing anything.
        await WaitFor(() => gw.GetRequest(rid)?.State == ExecutionState.DISPATCHING, TimeSpan.FromSeconds(30));

        await server.DisposeAsync();

        // Read the instant disposal returns. Anything written after this line is written into a
        // store the app is already closing.
        var record = gw.GetRequest(rid)!;
        Assert.Equal(ExecutionState.UNKNOWN, record.State);
        Assert.True(record.NeedsReconciliation,
            $"the row left DISPATCHING as {record.State} with no flag — nothing will ever reconcile it, and " +
            "the next order goes out over an outcome that exists nowhere");
        Assert.False(gw.TryAuthorizeExecution(new AgentContext("agent-after"), out _),
            "an order that may be at the broker was abandoned and trading was left open");
    }

    /// <summary>
    /// A handler that really will not finish is abandoned rather than waited on — and that has to be
    /// an ERROR in the record, not a note. It is the only trace that an order may have been left
    /// unsettled, so it must reach whatever an operator actually reads.
    ///
    /// THE FAULT IS UNCANCELLABLE LATENCY, and it has to be. A merely slow broker unwinds the moment
    /// disposal cancels the token, and since disposal now RE-AWAITS that unwind so the handler can
    /// record what it knows (Codex F2), such a handler always finishes and this line correctly does
    /// not appear. The only thing that still produces an abandoned handler is a call that does not
    /// honour the token — a blocking vendor SDK call on a thread nothing can interrupt — which is
    /// exactly the shape this error exists to report.
    /// </summary>
    [Fact]
    public async Task A_handler_that_outlasts_the_drain_is_recorded_as_an_error()
    {
        // Deliberately far shorter than the call really takes, which is the situation the log line
        // exists for — and it is the CONNECTOR that says so, because an explicit drain can no longer
        // shorten the bound below the chain it derives.
        var (gw, conn, db) = await ReadyWithDeclaredWorstCase(TimeSpan.FromMilliseconds(20));
        using var _1 = db;
        var pipe = NewPipe();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe)
        {
            SettleAfterCancelTimeout = TimeSpan.FromMilliseconds(300)
        };
        server.Start();

        await using var agent = await RawAgent.ConnectAndHello(pipe);
        await WarmUp(agent);
        conn.Faults.UncancellableLatencyMs = 5000;
        await agent.WriteAsync(new IpcRequest
        {
            Op = Ops.Buy,
            Session = "agent-stuck",
            RequestId = "cli-stuck-1",
            Args = new()
            {
                ["symbol"] = JsonSerializer.SerializeToElement("ES"),
                ["quantity"] = JsonSerializer.SerializeToElement("1")
            }
        });
        await WaitFor(() => gw.GetRequest("cli-stuck-1") is not null, TimeSpan.FromSeconds(30));

        await server.DisposeAsync();

        var severity = ReadEngineering(db, "handlers_did_not_finish");
        Assert.NotNull(severity);
        Assert.Equal("error", severity);
    }

    // ------------------- the drain is the MAX OVER EVERY HANDLER'S OWN SERIAL DEPTH (round 10, F1)

    /// <summary>
    /// CODEX ROUND-9 F1 AND ITS OWN ARITHMETIC: a `close-all` wave serialises FOUR ordinary
    /// placements, not one.
    ///
    /// The risk-reducing term was `E + W` — the whole emergency prefix under one budget, plus the
    /// single trailing `Place` a `close` ends with. `close-all` issues its legs in waves of
    /// <see cref="GatewayPipeServer.MaxLegsInFlight"/> and EVERY leg ends in a `Place`, and
    /// `TradingGateway._dispatchGate` is a mutex held across the dispatch — so one wave's placements
    /// run strictly one after another and the real path is `E + MaxLegsInFlight × W`.
    ///
    /// Codex's values, verbatim: `E = 30 s`, `W = 4 s`, `S = 5 s`, four positions. The drain must be
    /// at least `30 + 4×4 + 5 = 51 s`; the round-9 formula returns `max(5W, E+W) + S = 39 s`, and the
    /// twelve seconds missing are four placements disposal walks away from. Since round 12 the drain
    /// also carries <see cref="GatewayPipeServer.HandlerOverhead"/>, so it comes out one second over
    /// that floor rather than exactly on it — the assertion is a floor, which is what it means.
    ///
    /// It is asserted as ARITHMETIC rather than measured because it is arithmetic: no fixture can
    /// hold a fifty-one-second handler open inside a test suite, and the thing that was wrong was
    /// the formula rather than any measurement of it. The measured half is the theory below.
    /// </summary>
    [Fact]
    public void The_drain_covers_a_close_all_wave_and_not_just_one_trailing_place()
    {
        using var db = TestEnv.NewDb();
        var conn = new FakeConnector(new FakeBroker())
        {
            WorstCaseOperationPath = TimeSpan.FromSeconds(4),     // W
            EmergencyBudget = TimeSpan.FromSeconds(30)            // E
        };
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        var server = new GatewayPipeServer(gw, "tok", "ta-wave-arith")
        {
            SettleAfterCancelTimeout = TimeSpan.FromSeconds(5)    // S
        };

        var wave = conn.EmergencyBudget + GatewayPipeServer.MaxLegsInFlight * conn.WorstCaseOperationPath;
        var required = wave + server.SettleAfterCancelTimeout;

        Assert.True(server.HandlerDrainTimeout >= required,
            $"the drain came out at {server.HandlerDrainTimeout.TotalSeconds:0}s against a close-all wave that " +
            $"needs E + {GatewayPipeServer.MaxLegsInFlight}W + S = {required.TotalSeconds:0}s — one wave's " +
            $"{GatewayPipeServer.MaxLegsInFlight} placements serialise on the dispatch gate, and disposal " +
            "returns with them unsettled");

        // The table is the derivation, so it has to CONTAIN that path rather than reach the same
        // number by accident: `close-all` is one named row and the drain is the maximum over them.
        var closeAll = server.HandlerPaths.Single(p => p.Handler == Ops.CloseAll);
        Assert.Equal(wave, closeAll.Path);
        Assert.Equal(
            server.HandlerPaths.Max(p => p.Path) + GatewayPipeServer.HandlerOverhead + server.SettleAfterCancelTimeout,
            server.HandlerDrainTimeout);
    }

    /// <summary>
    /// THE TABLE IS EXHAUSTIVE BECAUSE THE DISPATCHER SAYS SO, NOT BECAUSE SOMEBODY LISTED IT
    /// (Codex round-10 F3, CHECK (a)).
    ///
    /// <see cref="GatewayPipeServer.HandlerPaths"/> is the drain's derivation and it calls itself
    /// exhaustive, but it was checked against a hand-written list of the handlers somebody had in
    /// mind — so four handled operations were missing from it (`schema`, `connectors`,
    /// `material-list`, `material-note`), and `schema` makes a connector-backed `StatusAsync` call.
    /// A hand list cannot catch that class: the omission and the check come from the same memory.
    ///
    /// So the set is read off THE DISPATCHER ITSELF. Every operation in the protocol's vocabulary is
    /// sent over the real pipe, and the reply says whether the dispatcher has an arm for it: an op it
    /// does not handle answers `unknown operation '…'`, and anything else — a refusal for missing
    /// arguments, an empty sweep, a real answer — means an arm ran. Both directions are asserted, so
    /// a row for an operation that no longer exists fails here too.
    ///
    /// The arguments are deliberately omitted. What is being discovered is whether an arm EXISTS,
    /// and a handler that refuses a frame for want of a symbol has already proved that.
    /// </summary>
    [Fact]
    public async Task Every_operation_the_dispatcher_handles_has_a_row_in_the_drain_table()
    {
        var (gw, conn, db, server, pipe) = await ReadyForHandlerTable("ta-table-coverage");
        using var _1 = db;
        await using var _2 = server;
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        // The protocol's whole op vocabulary, read off the constants rather than retyped.
        // `hello` is excluded because it is not a handler: the read loop answers it before the
        // dispatcher is reached, so it has no chain of connector calls to bound.
        var vocabulary = typeof(Ops).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Where(op => op != Ops.Hello)
            .Distinct()
            .OrderBy(op => op, StringComparer.Ordinal)
            .ToList();

        Assert.Contains(Ops.CloseAll, vocabulary);       // the vocabulary really was found
        Assert.True(vocabulary.Count >= 15, $"only {vocabulary.Count} operations were discovered");

        // AND THE CANDIDATE SET IS CHECKED AGAINST THE SWITCH, because `Ops`'s constants are not the
        // dispatcher (verifier round-11 L-3). Every arm uses an `Ops` constant TODAY, which is what
        // makes the vocabulary a sound candidate set — but a handler added with a literal op string
        // would be invisible to this test in exactly the way `schema` was invisible to the hand list
        // that preceded it, and the round's own argument is that the omission and the check must not
        // come from the same place. So the switch's own arm labels are read, each one is required to
        // BE an `Ops` constant rather than a string, and the two sets are compared both ways.
        var fromSwitch = DispatchSwitchOps();
        Assert.True(fromSwitch.Count >= 15, $"only {fromSwitch.Count} arms were read off the dispatch switch");
        Assert.Empty(fromSwitch.Except(vocabulary));

        async Task<bool> Handles(string op)
        {
            var reply = await client.SendAsync(new IpcRequest { Op = op }).WaitAsync(TimeSpan.FromSeconds(30));
            return reply.Ok || reply.Error?.Message != $"unknown operation '{op}'";
        }

        // THE DISCRIMINATOR'S OWN PREMISE: an operation the dispatcher does not have must come back
        // unhandled, or "handled" would mean nothing and every row would pass.
        Assert.False(await Handles("not-an-operation"), "the dispatcher claimed to handle a made-up op");

        var handled = new List<string>();
        foreach (var op in vocabulary)
            if (await Handles(op))
                handled.Add(op);

        var rows = server.HandlerPaths.Select(p => p.Handler).ToList();
        Assert.Equal(rows.Count, rows.Distinct().Count());

        var missing = handled.Except(rows).ToList();
        Assert.True(missing.Count == 0,
            $"the dispatcher handles {string.Join(", ", missing)} and the drain table has no row for " +
            "them, so their chains do not participate in the maximum the shutdown drain is derived from");

        var stale = rows.Except(handled).ToList();
        Assert.True(stale.Count == 0,
            $"the drain table has rows for {string.Join(", ", stale)}, which the dispatcher does not handle");

        // THE TWO SETS ARE THE SAME SET. The runtime half asks the dispatcher and the source half
        // reads it; either alone can be fooled by the other's blind spot.
        Assert.Equal(fromSwitch.OrderBy(o => o, StringComparer.Ordinal),
            handled.OrderBy(o => o, StringComparer.Ordinal));
    }

    /// <summary>
    /// THE OPERATIONS THE DISPATCH SWITCH ITSELF NAMES, read off its source.
    ///
    /// Reflection cannot see a switch's arm labels and the pipe can only be asked about ops somebody
    /// already thought of, so the one place the arms exist is the source — and reading it is what
    /// makes "every arm is an `Ops` constant" a checked premise instead of an assumed one. It fails
    /// loudly rather than skipping if the source is not where the tests run from, because a check
    /// that quietly stops checking is the failure this whole test is about.
    /// </summary>
    static List<string> DispatchSwitchOps()
    {
        var path = Path.Combine(Build.RepoRoot, "src", "TradeAgent.Gateway", "GatewayPipeServer.cs");
        Assert.True(File.Exists(path), $"the dispatcher's source is not at {path}");
        var source = File.ReadAllText(path);

        const string opener = "req.Op switch";
        var from = source.IndexOf(opener, StringComparison.Ordinal);
        Assert.True(from >= 0, "the dispatch switch was not found — this test can no longer read what it checks");
        var to = source.IndexOf("\n            };", from, StringComparison.Ordinal);
        Assert.True(to > from, "the dispatch switch's end was not found");

        var constants = typeof(Ops).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);

        var ops = new List<string>();
        foreach (var raw in source[from..to].Split('\n').Skip(1))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            var arrow = line.IndexOf("=>", StringComparison.Ordinal);
            if (arrow < 0) continue;

            var label = line[..arrow].Trim();
            if (label == "_") continue;                       // the unknown-operation arm

            foreach (var token in label.Split(" or ", StringSplitOptions.TrimEntries))
            {
                var name = token.StartsWith("Core.Ops.", StringComparison.Ordinal) ? token["Core.Ops.".Length..]
                    : token.StartsWith("Ops.", StringComparison.Ordinal) ? token["Ops.".Length..]
                    : null;
                Assert.True(name is not null,
                    $"the dispatch switch has an arm labelled `{token}`, which is not an `Ops` constant — an " +
                    "operation named by a literal is one no test that enumerates `Ops` can discover, and its " +
                    "handler's chain would sit outside the shutdown drain unnoticed");
                Assert.True(constants.TryGetValue(name!, out var op), $"`Ops.{name}` does not exist");
                ops.Add(op!);
            }
        }

        Assert.Equal(ops.Count, ops.Distinct().Count());
        return ops;
    }

    /// <summary>
    /// EVERY HANDLER, MEASURED — the class fix, third time of asking (§9.10).
    ///
    /// Three rounds have now found the drain derived from ONE handler's shape and silently wrong for
    /// another: round 8 from a single connector call, round 9 from a three-call chain that was really
    /// five, round 10 from a risk-reducing handler with one trailing placement that really has four.
    /// The structural answer is that no handler is special: <see cref="GatewayPipeServer.HandlerPaths"/>
    /// enumerates every one of them with its own serial depth, the drain is the maximum over that
    /// table, and this theory drives each handler over the real pipe at a fake latency and asserts
    /// the derived bound still covers what it actually cost.
    ///
    /// A handler that grows a call — or a new handler that is added and not put in the table — fails
    /// HERE, rather than by shortening a shutdown drain that abandons an order six months later.
    ///
    /// The latency is armed AFTER the fixture is built, so the setup is free and only the handler
    /// under test pays. The emergency budget is deliberately just above the read prefix a `close-all`
    /// leg needs (5 × W): with a wider budget the legs still run, but the wave stops being the
    /// longest thing in the table and the row proves nothing.
    /// </summary>
    [Theory]
    [InlineData(Ops.Buy)]
    [InlineData(Ops.Modify)]
    [InlineData(Ops.Cancel)]
    [InlineData(Ops.CancelAll)]
    [InlineData(Ops.Close)]
    [InlineData(Ops.CloseAll)]
    [InlineData(Ops.Orders)]
    [InlineData(Ops.Positions)]
    // `schema` is here because it is the one row added in round 11 that actually calls the
    // connector: it builds the same status the `status` handler does, and a row's number is worth
    // more measured than declared.
    [InlineData(Ops.Schema)]
    public async Task Every_handlers_measured_chain_fits_inside_the_drain_derived_for_it(string op)
    {
        var (gw, conn, db, server, pipe) = await ReadyForHandlerTable("ta-table-" + op.Replace("-", ""));
        using var _1 = db;
        await using var _2 = server;
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        var (working, symbols) = await StockTheBook(client, gw, conn);

        conn.Faults.LatencyMs = 500;   // W
        var request = op switch
        {
            Ops.Buy => new IpcRequest
            {
                Op = Ops.Buy, RequestId = "tbl-buy",
                Args = new()
                {
                    ["symbol"] = JsonSerializer.SerializeToElement("ES"),
                    ["quantity"] = JsonSerializer.SerializeToElement("1"),
                    ["limit"] = JsonSerializer.SerializeToElement("1")
                }
            },
            Ops.Modify => new IpcRequest
            {
                Op = Ops.Modify, RequestId = "tbl-modify",
                Args = new()
                {
                    ["id"] = JsonSerializer.SerializeToElement(working),
                    ["quantity"] = JsonSerializer.SerializeToElement("2")
                }
            },
            Ops.Cancel => new IpcRequest
            {
                Op = Ops.Cancel, RequestId = "tbl-cancel",
                Args = new() { ["id"] = JsonSerializer.SerializeToElement(working) }
            },
            Ops.CancelAll => new IpcRequest { Op = Ops.CancelAll, RequestId = "tbl-cancelall" },
            Ops.Close => new IpcRequest
            {
                Op = Ops.Close, RequestId = "tbl-close",
                Args = new() { ["symbol"] = JsonSerializer.SerializeToElement(symbols[0]) }
            },
            Ops.CloseAll => new IpcRequest { Op = Ops.CloseAll, RequestId = "tbl-closeall" },
            _ => new IpcRequest { Op = op }
        };

        var timer = Stopwatch.StartNew();
        var reply = await client.SendAsync(request).WaitAsync(TimeSpan.FromSeconds(60));
        timer.Stop();
        Assert.True(reply.Ok, $"'{op}' failed: {reply.Error?.Message}");

        // The fixture has to REACH the wire, or a bound would be compared against a handler that
        // refused early and the row would pass for the wrong reason.
        if (op is Ops.CloseAll)
            Assert.Equal(4, ((JsonElement)reply.Data!).GetProperty("attempted").GetInt32());

        var row = server.HandlerPaths.Single(p => p.Handler == op);
        Assert.True(server.HandlerDrainTimeout >= timer.Elapsed,
            $"'{op}' took {timer.Elapsed.TotalSeconds:0.00}s against a drain of " +
            $"{server.HandlerDrainTimeout.TotalSeconds:0.00}s — disposal during it walks away from an order in " +
            $"flight. The table says this handler costs {row.Path.TotalSeconds:0.00}s ({row.Why}).");
    }

    /// <summary>
    /// THE MEASURED HALF OF THE SAME RULE, AT THE SETTINGS THAT MADE IT VISIBLE.
    ///
    /// The theory above runs where every row has seconds of slack, so the difference between "the
    /// row bounds the connector chain" and "the drain bounds the handler" cannot show. Here the
    /// emergency budget is exactly the chain `cancel-all` issues — an orders read, a target
    /// resolution and the cancel, three calls of W against E = 3W — and the settle margin, which is
    /// what used to be doing the covering, is set to ZERO. What is left over is the handler's own
    /// work: the frame, the parse, the request rows and the reply. The verifier measured it at 917 ms
    /// against a 900 ms row (round-11 L-2); the drain has to cover the 917.
    /// </summary>
    [Fact]
    public async Task The_drain_covers_a_handler_whose_row_is_exactly_its_connector_chain()
    {
        var db = TestEnv.NewDb();
        using var _1 = db;
        var conn = new FakeConnector(new FakeBroker(), new FaultProfile { Fill = FillBehaviour.LeaveWorking })
        {
            EmergencyBudget = TimeSpan.FromMilliseconds(900)      // E = 3W exactly
        };
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe)
        {
            SettleAfterCancelTimeout = TimeSpan.Zero          // the margin, configured away
        };
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        // One resting order, placed while the simulator is still free.
        var resting = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Buy,
            RequestId = "row-tight-order",
            Args = new()
            {
                ["symbol"] = JsonSerializer.SerializeToElement("ES"),
                ["quantity"] = JsonSerializer.SerializeToElement("1"),
                ["limit"] = JsonSerializer.SerializeToElement("1")
            }
        }).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(resting.Ok, resting.Error?.Message);

        conn.Faults.LatencyMs = 300;                          // W
        var row = server.HandlerPaths.Single(p => p.Handler == Ops.CancelAll);
        Assert.Equal(TimeSpan.FromMilliseconds(900), row.Path);

        var timer = Stopwatch.StartNew();
        var sweep = await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "row-tight-sweep" })
            .WaitAsync(TimeSpan.FromSeconds(60));
        timer.Stop();
        Assert.True(sweep.Ok, sweep.Error?.Message);

        // THE ROW PLUS THE OVERHEAD TERM BOUNDS THE HANDLER, which is what the term is for. The row
        // ALONE does not: measured here at 909 ms against a 900 ms row, and by the verifier at
        // 917 ms. The drain is the MAXIMUM over the table, so `close-all`'s longer row happens to
        // cover `cancel-all` today — which is exactly why this is asserted per row rather than
        // against the drain: a table whose longest row is the tight one has no such luck.
        Assert.True(row.Path + GatewayPipeServer.HandlerOverhead >= timer.Elapsed,
            $"'cancel-all' cost {timer.Elapsed.TotalMilliseconds:0} ms against a row of " +
            $"{row.Path.TotalMilliseconds:0} ms ({row.Why}) plus " +
            $"{GatewayPipeServer.HandlerOverhead.TotalMilliseconds:0} ms of handler overhead — the term " +
            "that covers the frame, the parse, the request rows and the reply is too small.");

        // And the drain, which is that maximum plus the same term, covers it whatever the settle
        // margin is set to — here it is zero.
        Assert.Equal(TimeSpan.Zero, server.SettleAfterCancelTimeout);
        Assert.True(server.HandlerDrainTimeout >= timer.Elapsed,
            $"'cancel-all' cost {timer.Elapsed.TotalMilliseconds:0} ms against a drain of " +
            $"{server.HandlerDrainTimeout.TotalMilliseconds:0} ms");
    }

    /// <summary>
    /// AND THE STATE THE ARITHMETIC IS ABOUT: a four-position `close-all` that disposal lands in
    /// leaves nothing unsettled.
    ///
    /// Two landings, because they are not equally hard and only one of them discriminates. The
    /// WORST place for disposal to land is the START — the whole emergency prefix and the whole wave
    /// are still ahead of it, which is what the drain has to cover. Landing MID-WAVE is strictly
    /// cheaper (some placements are already done) and it is the case the bounce names, so it is
    /// asserted too rather than argued from the first.
    ///
    /// The latency is UNCANCELLABLE on purpose, for the reason the cold-placement disposal test
    /// gives: a call that unwinds at the cancel records UNKNOWN and hides the harm as "an order that
    /// needs reconciling", while a call that ignores the token leaves the row DISPATCHING with
    /// nothing coming to change it — and the count is read the INSTANT disposal returns, because a
    /// request settled after that was still abandoned by the shutdown.
    /// </summary>
    [Fact]
    public async Task A_close_all_wave_that_disposal_lands_in_leaves_nothing_unsettled()
    {
        var (gw, conn, db, server, pipe) = await ReadyForHandlerTable("ta-wave-dispose-a");
        using var _1 = db;
        await using (var client = new PipeClient())
        {
            await client.ConnectAsync(10_000, pipe);
            await StockTheBook(client, gw, conn);

            conn.Faults.UncancellableLatencyMs = 500;
            var sweep = Swallow(client.SendAsync(new IpcRequest { Op = Ops.CloseAll, RequestId = "wave-a" }));
            await Task.Delay(200);   // the whole prefix and the whole wave are still ahead

            await server.DisposeAsync();
            Assert.Equal(0, Dispatching(db));
            Assert.Null(ReadEngineering(db, "handlers_did_not_finish"));

            // Every position was really closed BEFORE disposal returned, so "nothing unsettled" is
            // not "nothing happened". The agent's own reply is gone either way — disposal closes the
            // connection before it waits — which is exactly why the evidence has to be the record and
            // the broker's book rather than the answer.
            Assert.DoesNotContain(conn.Broker.Positions, p => p.Quantity != 0);
            await sweep;
        }

        // MID-WAVE: a second sweep over a freshly stocked book, disposed once a placement of the
        // wave has actually reached the broker.
        var (gw2, conn2, db2, server2, pipe2) = await ReadyForHandlerTable("ta-wave-dispose-b");
        using var _3 = db2;
        await using var client2 = new PipeClient();
        await client2.ConnectAsync(10_000, pipe2);
        await StockTheBook(client2, gw2, conn2);

        var before = conn2.Broker.Orders.Count;
        conn2.Faults.UncancellableLatencyMs = 500;
        var sweep2 = Swallow(client2.SendAsync(new IpcRequest { Op = Ops.CloseAll, RequestId = "wave-b" }));
        await WaitFor(() => conn2.Broker.Orders.Count > before, TimeSpan.FromSeconds(30));

        await server2.DisposeAsync();
        Assert.Equal(0, Dispatching(db2));
        Assert.Null(ReadEngineering(db2, "handlers_did_not_finish"));
        Assert.DoesNotContain(conn2.Broker.Positions, p => p.Quantity != 0);
        await sweep2;
        await server2.DisposeAsync();
    }

    /// <summary>
    /// Observes a reply that disposal is about to make undeliverable. Closing the connections is
    /// step 2 of <c>DisposeAsync</c> and it happens BEFORE the drain, so a request in flight loses
    /// its answer by design; the fault is expected and must not surface later as an unobserved task
    /// exception.
    /// </summary>
    static async Task Swallow(Task<IpcResponse> reply)
    {
        try { await reply.WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (Exception) { /* the service closed the connection under it, which is the point */ }
    }

    /// <summary>
    /// A gateway whose emergency budget clears the read prefix of a `close-all` by a wide margin, so
    /// the wave of trailing placements is the longest path in the table rather than a rounding
    /// difference. The settle margin is short for the same reason: it must not do the covering.
    ///
    /// THE MARGIN IS THE POINT AND IT USED TO BE 172 MILLISECONDS. The budget was 3200 ms, and it was
    /// picked to sit "just above" the prefix — measured on this Mac, the prefix is five connector
    /// calls of the 500 ms these tests inject and the first leg reaches the broker at 3028 ms. Six
    /// per cent of slowness and the budget expires first, so every leg is reported NOT SENT and a
    /// test asserting the book was closed fails; and because the drain is DERIVED from this budget
    /// (E + 4W + 1 s + 0.1 s = 6300 ms against a 4557 ms wave), a stall of two seconds arriving after
    /// the wave was issued overruns the drain instead, leaving a row DISPATCHING. Both were measured
    /// here by injecting the slowness — `dispatching=1`, `handlers_did_not_finish` with
    /// `unsettled:1` — and the second is what windows-latest hit in run 33924375698.
    ///
    /// Twelve seconds is roughly five times the prefix, and it costs NOTHING when the tests pass: a
    /// budget is a deadline and a drain is a timeout, so neither is ever waited out by a healthy run.
    /// The same injection that produced the failure at 3200 ms passes at 12 s with the runner 2.4x
    /// slower than this Mac. `close-all` remains the longest row (E + 4W beats every other shape for
    /// any E above W), which is the property the number is chosen for.
    /// </summary>
    static async Task<(TradingGateway Gw, FakeConnector Conn, Database Db, GatewayPipeServer Server, string Pipe)>
        ReadyForHandlerTable(string pipe)
    {
        var db = TestEnv.NewDb();
        var conn = new FakeConnector(new FakeBroker()) { EmergencyBudget = TimeSpan.FromSeconds(12) };
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 200;
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe)
        {
            SettleAfterCancelTimeout = TimeSpan.FromMilliseconds(100)
        };
        server.Start();
        return (gw, conn, db, server, pipe);
    }

    /// <summary>
    /// Four filled positions and one resting order, placed while the simulator is still free — so the
    /// latency armed afterwards is paid by the handler under test and not by its setup.
    /// </summary>
    static async Task<(string Working, string[] Symbols)> StockTheBook(PipeClient client, TradingGateway gw, FakeConnector conn)
    {
        string[] symbols = ["ES", "NQ", "MES", "YM"];
        foreach (var symbol in symbols)
        {
            var filled = await client.SendAsync(new IpcRequest
            {
                Op = Ops.Buy,
                RequestId = $"stock-{symbol}-{Guid.NewGuid():n}"[..24],
                Args = new()
                {
                    ["symbol"] = JsonSerializer.SerializeToElement(symbol),
                    ["quantity"] = JsonSerializer.SerializeToElement("1")
                }
            }).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(filled.Ok, $"could not open a position in {symbol}: {filled.Error?.Message}");
        }

        // The book needs BOTH shapes and the simulator serves one at a time: four market orders that
        // fill (so there are positions to close) and then one that rests (so there is an order to
        // modify and to cancel).
        conn.Faults.Fill = FillBehaviour.LeaveWorking;
        var resting = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Buy,
            RequestId = "stock-working",
            Args = new()
            {
                ["symbol"] = JsonSerializer.SerializeToElement("ES"),
                ["quantity"] = JsonSerializer.SerializeToElement("1"),
                ["limit"] = JsonSerializer.SerializeToElement("1")
            }
        }).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(resting.Ok, $"could not leave a resting order: {resting.Error?.Message}");

        // Back to filling, or the offsetting orders a `close` places would rest too and the position
        // they are meant to flatten would still be open — a fixture that measures nothing.
        conn.Faults.Fill = FillBehaviour.FillImmediately;

        Assert.Equal(4, (await gw.PositionsAsync()).Count(p => p.Quantity != 0));
        var working = (await gw.OrdersAsync()).Single().ConnectorOrderId;
        return (working, symbols);
    }

    /// <summary>
    /// DISPOSAL MAY LEAVE A REQUEST UNSETTLED. IT MAY NOT DO IT SILENTLY.
    ///
    /// Verifier round-9 F-2, and it refutes round 9's own claim that "the only thing that still
    /// produces one is a call that does not honour its cancellation token". The connector here
    /// HONOURS it — `Task.Delay(LatencyMs, ct)` — and that is precisely what hides the harm: the
    /// handler unwinds the instant it is cancelled, so the sentinel that counted unfinished HANDLER
    /// TASKS saw nothing wrong, while `TradingGateway.ModifyAsync` catches only
    /// `ConnectorRejectedException` and `ConnectorTransportException` and lets the cancellation
    /// escape — leaving the row DISPATCHING, unflagged, and invisible to `ReconcileAsync`, which
    /// scans `NeedingReconciliation()` alone. Nothing will ever settle it.
    ///
    /// THE SPLIT, STATED. Settling that row is `TradingGateway`'s — a file this unit may not open —
    /// and it is routed to U2c-1 with this measurement. What is fixed here is the half that is the
    /// pipe server's, and it is the half that makes the other half findable: the sentinel now counts
    /// what it is actually about, REQUESTS STILL DISPATCHING when `DisposeAsync` returns, and names
    /// them. `handlers_did_not_finish` at `error` is the only trace an operator gets that an order
    /// may have been left unsettled, so it must fire on the state rather than on the symptom that
    /// happened to be visible first.
    /// </summary>
    [Fact]
    public async Task A_request_left_unsettled_when_disposal_returns_is_logged_by_name_at_error()
    {
        // The connector under-reports its own worst case, which is how a drain ends up shorter than
        // the handler now that an explicit `HandlerDrainTimeout` cannot shorten it.
        var (gw, conn, db) = await ReadyWithDeclaredWorstCase(TimeSpan.FromMilliseconds(20));
        using var _1 = db;
        var pipe = NewPipe();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe)
        {
            SettleAfterCancelTimeout = TimeSpan.FromMilliseconds(300)
        };
        server.Start();

        await using var agent = await RawAgent.ConnectAndHello(pipe);
        await WarmUp(agent);

        // Something to modify, placed while the simulator is still quick.
        conn.Faults.Fill = FillBehaviour.LeaveWorking;
        await agent.WriteAsync(new IpcRequest
        {
            Op = Ops.Buy,
            Session = "agent-unsettled",
            RequestId = "cli-unsettled-order",
            Args = new()
            {
                ["symbol"] = JsonSerializer.SerializeToElement("ES"),
                ["quantity"] = JsonSerializer.SerializeToElement("1"),
                ["limit"] = JsonSerializer.SerializeToElement("1")
            }
        });
        await WaitFor(() => gw.GetRequest("cli-unsettled-order")?.State == ExecutionState.WORKING, TimeSpan.FromSeconds(30));
        var target = (await gw.OrdersAsync()).Single().ConnectorOrderId;

        // UNCANCELLABLE latency, five seconds of it: the modify is still INSIDE the connector when
        // disposal gives up, so the row it wrote before the call is genuinely unsettled — the call
        // has not answered and nothing yet knows what became of it.
        //
        // It used to be CANCELLABLE latency, and that stopped producing this shape once U2c-1 put a
        // catch-all after the wire on every dispatch path: the cancelled handler unwound through it,
        // the row settled UNKNOWN + flagged, and reconciliation could see it. That is the fix this
        // test's comment routes to U2c-1, and it is asserted in
        // `A_cancelled_dispatch_is_flagged_rather_than_left_dispatching`. What remains here — and is
        // what disposal's report is actually about — is the operation that has not come back AT ALL.
        conn.Faults.UncancellableLatencyMs = 5000;
        const string rid = "cli-unsettled-modify";
        await agent.WriteAsync(new IpcRequest
        {
            Op = Ops.Modify,
            Session = "agent-unsettled",
            RequestId = rid,
            Args = new()
            {
                ["id"] = JsonSerializer.SerializeToElement(target),
                ["quantity"] = JsonSerializer.SerializeToElement("2")
            }
        });
        await WaitFor(() => gw.GetRequest(rid)?.State == ExecutionState.DISPATCHING, TimeSpan.FromSeconds(30));

        var drain = server.HandlerDrainTimeout;
        var timer = Stopwatch.StartNew();
        await server.DisposeAsync();
        timer.Stop();

        // THE FULL DERIVED DRAIN IS WAITED BEFORE ANYTHING IS CANCELLED. A shutdown that cancelled
        // early would produce this row for a handler that had time left.
        Assert.True(timer.Elapsed >= drain,
            $"disposal returned in {timer.Elapsed.TotalMilliseconds:0} ms against a derived drain of " +
            $"{drain.TotalMilliseconds:0} ms — it cancelled the handler before its time was up");

        // The row really is unsettled and really is invisible to reconciliation — because the
        // connector call has not returned. Nothing can settle a row whose outcome has not arrived,
        // which is why "disposal returned with this still open" has to be said out loud.
        var record = gw.GetRequest(rid)!;
        Assert.Equal(ExecutionState.DISPATCHING, record.State);
        Assert.False(record.NeedsReconciliation);
        Assert.Empty(gw.Requests.NeedingReconciliation());

        // And this is the half that is fixed: it is not silent.
        Assert.Equal("error", ReadEngineering(db, "handlers_did_not_finish"));
        var metadata = ReadEngineeringMetadata(db, "handlers_did_not_finish");
        Assert.Contains(rid, metadata);
    }

    /// <summary>
    /// THE OTHER HALF OF THE TEST ABOVE, AND THE ONE THAT WAS ROUTED AWAY: a dispatch CANCELLED by
    /// disposal is flagged, not left DISPATCHING.
    ///
    /// The test above measured a row that disposal walked away from, and its comment said settling
    /// it was `TradingGateway`'s and belonged to U2c-1. It does now: every dispatch path has a
    /// catch-all after the wire, so a cancellation — which is not in the
    /// `ConnectorRejectedException`/`ConnectorTransportException` taxonomy and used to escape — ends
    /// as UNKNOWN with `needs_reconciliation` set, which is what `ReconcileAsync` scans and what
    /// pauses execution. Same fixture as above in every respect but one: the latency is CANCELLABLE,
    /// so the handler really does unwind while disposal is still holding the database open.
    /// </summary>
    [Fact]
    public async Task A_cancelled_dispatch_is_flagged_rather_than_left_dispatching()
    {
        var (gw, conn, db) = await ReadyWithDeclaredWorstCase(TimeSpan.FromMilliseconds(20));
        using var _1 = db;
        var pipe = NewPipe();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe)
        {
            // Short enough that the derived drain expires well inside the 5 s modify — the point is
            // to CANCEL it — and long enough for the unwind to write its outcome down.
            SettleAfterCancelTimeout = TimeSpan.FromMilliseconds(300)
        };
        server.Start();

        await using var agent = await RawAgent.ConnectAndHello(pipe);
        await WarmUp(agent);

        conn.Faults.Fill = FillBehaviour.LeaveWorking;
        await agent.WriteAsync(new IpcRequest
        {
            Op = Ops.Buy,
            Session = "agent-cancelled",
            RequestId = "cli-cancelled-order",
            Args = new()
            {
                ["symbol"] = JsonSerializer.SerializeToElement("ES"),
                ["quantity"] = JsonSerializer.SerializeToElement("1"),
                ["limit"] = JsonSerializer.SerializeToElement("1")
            }
        });
        await WaitFor(() => gw.GetRequest("cli-cancelled-order")?.State == ExecutionState.WORKING,
            TimeSpan.FromSeconds(30));
        var target = (await gw.OrdersAsync()).Single().ConnectorOrderId;

        conn.Faults.LatencyMs = 5000;
        const string rid = "cli-cancelled-modify";
        await agent.WriteAsync(new IpcRequest
        {
            Op = Ops.Modify,
            Session = "agent-cancelled",
            RequestId = rid,
            Args = new()
            {
                ["id"] = JsonSerializer.SerializeToElement(target),
                ["quantity"] = JsonSerializer.SerializeToElement("2")
            }
        });
        await WaitFor(() => gw.GetRequest(rid)?.State == ExecutionState.DISPATCHING, TimeSpan.FromSeconds(30));

        await server.DisposeAsync();

        var record = gw.GetRequest(rid)!;
        Assert.Equal(ExecutionState.UNKNOWN, record.State);
        Assert.True(record.NeedsReconciliation,
            "a dispatch cancelled after the write-ahead was left unflagged, so nothing will settle it");
        Assert.Contains(rid, gw.Requests.NeedingReconciliation().Select(r => r.RequestId));
    }

    /// <summary>
    /// THE AGENT GOES AWAY FIRST, AND THEN THE APP CLOSES — the ordinary shutdown shape, and the one
    /// the promise not to return silently did not cover.
    ///
    /// Round 10 changed what the sentinel COUNTS (unfinished handler tasks -> unfinished OR unsettled
    /// requests) and left the GUARD around it: the whole block sits inside `if (handlers.Length > 0)`,
    /// and `handlers` is `_handlers.Keys` read AFTER step 2 has disposed every live connection —
    /// per-CONNECTION tasks, each of which removes itself on completion. So the report is conditioned
    /// on a connection happening to be alive rather than on the state it is about. Measured by the
    /// round-11 verifier with two probes differing in one thing only: with the agent connected,
    /// `handlers_did_not_finish = error`; with the agent gone first, disposal returned in 3 ms with a
    /// DISPATCHING row and NOTHING logged.
    ///
    /// HOW THE ROW IS PRODUCED CHANGED WITH U2c-1, AND THE PROPERTY UNDER TEST DID NOT. It used to
    /// be a connector `TimeoutException` — which safety rule 3 requires to PROPAGATE — escaping
    /// `TradingGateway.ModifyAsync`'s `ConnectorRejectedException`/`ConnectorTransportException`
    /// catch taxonomy: the handler answered the agent and lived on, and the row stayed DISPATCHING
    /// and unflagged. U2c-1 put a catch-all after the wire on every dispatch path, so that timeout
    /// now settles UNKNOWN + flagged — asserted below, because it is the reason the rest of this
    /// test had to change and a regression in it would put the old silent row back.
    ///
    /// What is left DISPATCHING at disposal is therefore a row THIS process is not flying: the
    /// write-ahead of a dispatch whose process is gone, which is exactly the shape
    /// `TradingGateway.RecoverStrandedDispatches` documents and sweeps at construction — and which
    /// is invisible to that sweep for the whole life of a session it appears in. Disposal's report
    /// is the only thing that names it, and the defect is that it named it only while an agent
    /// happened to still be attached.
    ///
    /// The control — a row unsettled with the agent still connected — is
    /// `A_request_left_unsettled_when_disposal_returns_is_logged_by_name_at_error`.
    /// </summary>
    [Fact]
    public async Task A_row_left_dispatching_is_named_even_when_the_agent_disconnected_first()
    {
        var (gw, conn, db) = await ReadyWithDeclaredWorstCase(TimeSpan.FromMilliseconds(20));
        using var _1 = db;
        var counting = new CountingConnector(conn);
        var gateway = new TradingGateway(db, counting, new HealthRegistry());
        gateway.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
        });
        await gateway.RefreshHealthAsync();

        var pipe = NewPipe();
        var server = new GatewayPipeServer(gateway, IpcToken.Ensure(), pipe)
        {
            SettleAfterCancelTimeout = TimeSpan.FromMilliseconds(200)
        };
        server.Start();

        const string rid = "cli-gone-modify";
        await using (var agent = await RawAgent.ConnectAndHello(pipe))
        {
            await WarmUp(agent);

            conn.Faults.Fill = FillBehaviour.LeaveWorking;
            await agent.WriteAsync(new IpcRequest
            {
                Op = Ops.Buy,
                Session = "agent-gone",
                RequestId = "cli-gone-order",
                Args = new()
                {
                    ["symbol"] = JsonSerializer.SerializeToElement("ES"),
                    ["quantity"] = JsonSerializer.SerializeToElement("1"),
                    ["limit"] = JsonSerializer.SerializeToElement("1")
                }
            });
            await WaitFor(() => gateway.GetRequest("cli-gone-order")?.State == ExecutionState.WORKING,
                TimeSpan.FromSeconds(30));
            var target = (await gateway.OrdersAsync()).Single().ConnectorOrderId;

            // Outside the gateway's catch taxonomy on purpose: the handler ANSWERS and finishes, and
            // the row it left behind is the thing disposal has to notice.
            counting.TimeoutOnModify = true;
            await agent.WriteAsync(new IpcRequest
            {
                Op = Ops.Modify,
                Session = "agent-gone",
                RequestId = rid,
                Args = new()
                {
                    ["id"] = JsonSerializer.SerializeToElement(target),
                    ["quantity"] = JsonSerializer.SerializeToElement("2")
                }
            });
            await WaitFor(() => gateway.GetRequest(rid)?.State == ExecutionState.UNKNOWN,
                TimeSpan.FromSeconds(30));
        }

        // The escaping timeout is settled and flagged now, so it is NOT what disposal has to report.
        var settled = gateway.GetRequest(rid)!;
        Assert.True(settled.NeedsReconciliation,
            "a propagating TimeoutException left the row unflagged — the silent DISPATCHING row is back");

        // The row that IS left: a write-ahead with no process flying it. Written straight to the
        // store because that is honestly where such a row comes from — the previous run's dispatch,
        // read out of the same database — and because no code path inside this process produces one
        // any more, which is the point of the assertion above.
        const string stranded = "cli-stranded-dispatch";
        gateway.Requests.TryCreate(new ExecutionRequest
        {
            RequestId = stranded,
            ConnectorId = gateway.Connector.Id,
            AccountId = conn.Broker.AccountId,
            Instrument = "ES",
            Intent = RequestIntent.PLACE,
            ParametersJson = "{}",
            ClientOrderId = TradingGateway.ClientOrderIdFor(stranded),
            Mode = TradingMode.PAPER
        });
        gateway.Requests.Transition(stranded, ExecutionState.CREATED, ExecutionState.DISPATCHING);

        // The premise this test is built on: the connection is gone and its handler has finished, so
        // there is nothing in `_handlers` for a guard to find.
        await WaitFor(() => server.LiveHandlerCount == 0, TimeSpan.FromSeconds(30));
        Assert.Equal(ExecutionState.DISPATCHING, gateway.GetRequest(stranded)!.State);

        await server.DisposeAsync();

        // Unsettled, still — nothing in the pipe server settles a request. NOT silent, which is this
        // unit's half and is what the agent's departure used to switch off.
        Assert.Equal(ExecutionState.DISPATCHING, gateway.GetRequest(stranded)!.State);
        Assert.Equal("error", ReadEngineering(db, "handlers_did_not_finish"));
        Assert.Contains(stranded, ReadEngineeringMetadata(db, "handlers_did_not_finish"));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Forwards every call to the simulator and writes down which ones it was asked for, in order.
    ///
    /// The ORDER matters as much as the count: this is used on a handler whose calls are strictly
    /// serial, so the sequence it records IS the chain the shutdown drain has to outlast. It is not
    /// meaningful on a sweep, whose legs are issued concurrently — that shape is bounded by the
    /// operation's own deadline instead, and the drain accounts for it separately.
    /// </summary>
    sealed class CountingConnector(FakeConnector inner) : ConnectorSdk.ITradingConnector
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Calls { get; } = new();

        T Note<T>(string op, T call) { Calls.Enqueue(op); return call; }

        public string Id => inner.Id;
        public string DisplayName => inner.DisplayName;
        public ConnectorSdk.ConnectorCapabilities Capabilities => inner.Capabilities;
        public TimeSpan WorstCaseOperationPath => inner.WorstCaseOperationPath;
        public TimeSpan EmergencyBudget => inner.EmergencyBudget;

        public event Action<HealthState>? ConnectionChanged { add => inner.ConnectionChanged += value; remove => inner.ConnectionChanged -= value; }
        public event Action<ConnectorSdk.QuoteInfo>? QuoteChanged { add => inner.QuoteChanged += value; remove => inner.QuoteChanged -= value; }
        public event Action<ConnectorSdk.OrderInfo>? OrderChanged { add => inner.OrderChanged += value; remove => inner.OrderChanged -= value; }
        public event Action<ConnectorSdk.ExecutionInfo>? ExecutionReceived { add => inner.ExecutionReceived += value; remove => inner.ExecutionReceived -= value; }
        public event Action<ConnectorSdk.PositionInfo>? PositionChanged { add => inner.PositionChanged += value; remove => inner.PositionChanged -= value; }
        public event Action<ConnectorSdk.AccountInfo>? AccountChanged { add => inner.AccountChanged += value; remove => inner.AccountChanged -= value; }

        // Not counted: neither reaches the wire in this connector, and neither is part of a handler's
        // chain — health is polled by the app's own loop.
        public Task ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);
        public Task<HealthState> GetHealthAsync(CancellationToken ct = default) => inner.GetHealthAsync(ct);
        public Task<bool> IsConnectedAsync(CancellationToken ct = default) => inner.IsConnectedAsync(ct);

        public Task<IReadOnlyList<ConnectorSdk.AccountInfo>> GetAccountsAsync(CancellationToken ct = default) =>
            Note("accounts", inner.GetAccountsAsync(ct));
        public Task<ConnectorSdk.AccountInfo?> GetAccountAsync(string accountId, CancellationToken ct = default) =>
            Note("account", inner.GetAccountAsync(accountId, ct));
        public Task<IReadOnlyList<ConnectorSdk.InstrumentInfo>> GetInstrumentsAsync(CancellationToken ct = default) =>
            Note("instruments", inner.GetInstrumentsAsync(ct));
        public Task<ConnectorSdk.QuoteInfo?> GetQuoteAsync(string symbol, CancellationToken ct = default) =>
            Note("quote", inner.GetQuoteAsync(symbol, ct));
        public Task<IReadOnlyList<ConnectorSdk.PositionInfo>> GetPositionsAsync(string accountId, CancellationToken ct = default) =>
            Note("positions", inner.GetPositionsAsync(accountId, ct));
        public Task<IReadOnlyList<ConnectorSdk.OrderInfo>> GetOrdersAsync(string accountId, bool includeInactive, DateTimeOffset? since, CancellationToken ct = default) =>
            Note("orders", inner.GetOrdersAsync(accountId, includeInactive, since, ct));
        public Task<IReadOnlyList<ConnectorSdk.ExecutionInfo>> GetExecutionsAsync(string accountId, DateTimeOffset? since, CancellationToken ct = default) =>
            Note("executions", inner.GetExecutionsAsync(accountId, since, ct));
        public Task<ConnectorSdk.OrderInfo> PlaceOrderAsync(ConnectorSdk.PlaceOrderCommand cmd, CancellationToken ct = default) =>
            Note("place", inner.PlaceOrderAsync(cmd, ct));
        /// <summary>
        /// Makes `modify` throw a `TimeoutException`, which is neither of the two exceptions
        /// `TradingGateway.ModifyAsync` catches — so the row is left DISPATCHING by a handler that
        /// then answers the agent and finishes. Safety rule 3 requires exactly that propagation, so
        /// this is the product's own behaviour rather than an injected impossibility.
        /// </summary>
        public bool TimeoutOnModify { get; set; }

        public Task<ConnectorSdk.OrderInfo> ModifyOrderAsync(ConnectorSdk.ModifyOrderCommand cmd, CancellationToken ct = default) =>
            TimeoutOnModify
                ? Note("modify", Task.FromException<ConnectorSdk.OrderInfo>(
                    new TimeoutException("the modify timed out")))
                : Note("modify", inner.ModifyOrderAsync(cmd, ct));
        public Task CancelOrderAsync(string connectorOrderId, CancellationToken ct = default) =>
            Note("cancel", inner.CancelOrderAsync(connectorOrderId, ct));
        public Task<IReadOnlyList<string>> CancelAllOrdersAsync(string accountId, CancellationToken ct = default) =>
            Note("cancel-all", inner.CancelAllOrdersAsync(accountId, ct));
        public Task<ConnectorSdk.OrderInfo?> ClosePositionAsync(string accountId, string symbol, string clientOrderId, CancellationToken ct = default) =>
            Note("close", inner.ClosePositionAsync(accountId, symbol, clientOrderId, ct));
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>
    /// A gateway over a simulator that CLAIMS a given worst-case path whatever it actually costs,
    /// and whose emergency budget is small enough not to floor the derived drain on its own.
    ///
    /// This is how a test makes the shutdown drain too short now that setting one directly cannot:
    /// the drain derives itself correctly from what the connector tells it, and what it is told is
    /// wrong. Same end state as the old undersized `HandlerDrainTimeout`, reached the way an
    /// operator can actually reach it.
    /// </summary>
    static async Task<(TradingGateway Gw, FakeConnector Conn, Database Db)> ReadyWithDeclaredWorstCase(
        TimeSpan declared)
    {
        var db = TestEnv.NewDb();
        var conn = new FakeConnector(new FakeBroker())
        {
            WorstCaseOperationPath = declared,
            EmergencyBudget = TimeSpan.FromMilliseconds(50)
        };
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        return (gw, conn, db);
    }

    /// <summary>
    /// Makes the gateway's pre-flight lookups cheap, so a broker latency armed afterwards lands on
    /// the dispatch rather than on the instrument and account calls in front of it.
    /// </summary>
    static async Task WarmUp(RawAgent agent)
    {
        foreach (var op in new[] { Ops.Instruments, Ops.Account, Ops.Positions })
        {
            await agent.WriteAsync(new IpcRequest { Op = op });
            await agent.ReadLineAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>The metadata of the first engineering row for an event, or "" if there is none.</summary>
    static string ReadEngineeringMetadata(Database db, string @event) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT metadata FROM engineering_log WHERE component='Ipc' AND event=$e ORDER BY id LIMIT 1", ("$e", @event));
        using var r = c.ExecuteReader();
        return r.Read() ? r.IsDBNull(0) ? "" : r.GetString(0) : "";
    });

    /// <summary>The severity of the first engineering row for an event, or null if there is none.</summary>
    static string? ReadEngineering(Database db, string @event) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT severity FROM engineering_log WHERE component='Ipc' AND event=$e ORDER BY id LIMIT 1", ("$e", @event));
        using var r = c.ExecuteReader();
        return r.Read() ? r.GetString(0) : null;
    });

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

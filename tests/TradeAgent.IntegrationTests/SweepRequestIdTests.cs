using System.Diagnostics;
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
/// What `cancel-all` and `close-all` name the requests they derive, and what they then claim to
/// have done.
///
/// Both derived their per-order ids as <c>{rid}-{i}</c>, which is a shape an agent can also type.
/// An agent that placed an order with <c>--request-id X-0</c> and later swept with
/// <c>--request-id X</c> handed the first cancellation the id <c>X-0</c>, already in the
/// idempotency store as a PLACE — so the store replayed that record instead of cancelling anything,
/// and the sweep counted it anyway. `cancelled=1`, order still WORKING.
///
/// The count was the second half of it: <c>cancelled = results.Count</c> counted ATTEMPTS. On the
/// one command a person reaches for when they want everything to stop, that is the worst possible
/// thing to be wrong about.
/// </summary>
public class SweepRequestIdTests
{
    static string NewPipe() => "ta-sweep-" + Guid.NewGuid().ToString("n")[..12];

    static IpcRequest Buy(string requestId, string symbol) => new()
    {
        Op = Ops.Buy,
        RequestId = requestId,
        Args = new()
        {
            ["symbol"] = JsonSerializer.SerializeToElement(symbol),
            ["quantity"] = JsonSerializer.SerializeToElement("1"),
            // A limit far from the market, so it rests as WORKING and is there to be cancelled.
            ["limit"] = JsonSerializer.SerializeToElement("1")
        }
    };

    /// <summary>
    /// The collision itself: an order placed under the id the sweep would derive, then the sweep.
    /// Nothing may be reported cancelled that is not cancelled.
    /// </summary>
    [Fact]
    public async Task A_sweep_cannot_collide_with_an_id_the_agent_chose_itself()
    {
        // LeaveWorking, or the fake broker fills every order on arrival, the working list is empty
        // and a sweep with nothing to sweep passes every assertion vacuously.
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        // The agent chooses exactly the id the old scheme would derive for the first cancellation.
        var placed = await client.SendAsync(Buy("sweep-1-0", "ES")).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(placed.Ok, Json.Write(placed.Error));
        Assert.Single(conn.Broker.Orders);
        Assert.Equal(ExecutionState.WORKING, conn.Broker.Orders.Single().State);
        Assert.Single(await gw.OrdersAsync(false));   // there IS something for the sweep to cancel

        var sweep = await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "sweep-1" })
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(sweep.Ok, Json.Write(sweep.Error));

        var data = (JsonElement)sweep.Data!;
        var claimed = data.GetProperty("cancelled").GetInt32();

        // Whatever it claims, it must be true. Count what the broker actually shows as cancelled.
        var reallyCancelled = (await gw.OrdersAsync(true)).Count(o => o.State == ExecutionState.CANCELLED);
        Assert.True(claimed <= reallyCancelled,
            $"cancel-all reported cancelled={claimed} while only {reallyCancelled} order(s) are actually cancelled");
    }

    /// <summary>
    /// EVERY ID THE GATEWAY MINTS LEAVES THIS PROCESS ON A BROKER ORDER, so its charset is a safety
    /// property and not a style question.
    ///
    /// The id is carried onto the order as <c>TA-{id}</c>, and safety rule 1 requires that field to
    /// round-trip. The previous scheme minted <c>TA-…#close-all#0</c> — and whether ATAS accepts
    /// <c>#</c> in a client order id is not knowable from here, only on the box. This asserts every
    /// minted id is <c>[A-Za-z0-9-]</c>, from a sweep whose own id is at the edge of what is allowed.
    /// </summary>
    [Fact]
    public async Task Every_id_the_gateway_mints_is_in_the_conservative_charset()
    {
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        Assert.True((await client.SendAsync(Buy("mint-a", "ES")).WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        Assert.True((await client.SendAsync(Buy("mint-b", "NQ")).WaitAsync(TimeSpan.FromSeconds(10))).Ok);

        var sweep = (JsonElement)(await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "sweep-mint" })
            .WaitAsync(TimeSpan.FromSeconds(10))).Data!;
        Assert.Equal(2, sweep.GetProperty("attempted").GetInt32());

        var minted = sweep.GetProperty("requests").EnumerateArray()
            .Select(r => r.GetProperty("request_id").GetString()!).ToList();
        Assert.Equal(2, minted.Count);

        foreach (var id in minted)
        {
            Assert.Matches("^[A-Za-z0-9-]+$", id);
            Assert.StartsWith("op-", id);
            // And what actually reaches the broker, which is the string the rule is about.
            Assert.Matches("^[A-Za-z0-9-]+$", TradingGateway.ClientOrderIdFor(id));
        }
        Assert.Equal(minted.Count, minted.Distinct().Count());
    }

    /// <summary>
    /// The reserved PREFIX is refused on the way in. That is what makes a minted id uncollidable by
    /// construction rather than by hoping the agent picks different words.
    /// </summary>
    [Theory]
    [InlineData("op-deadbeef-cancelall-0")]
    [InlineData("op-anything")]
    [InlineData("OP-UPPERCASE")]
    public async Task A_request_id_using_the_reserved_minted_prefix_is_refused(string id)
    {
        var (gw, _, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        var reply = await client.SendAsync(Buy(id, "ES")).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(reply.Ok, $"'{id}' was accepted, and it can collide with a minted sweep id");
        Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), reply.Error!.Code);
    }

    /// <summary>
    /// An id that would not survive the trip to the broker is refused before an order carries it.
    /// </summary>
    [Theory]
    [InlineData("has space")]
    [InlineData("has#hash")]
    [InlineData("has/slash")]
    [InlineData("has_underscore")]
    [InlineData("émoji")]
    public async Task A_request_id_outside_the_conservative_charset_is_refused(string id)
    {
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        var reply = await client.SendAsync(Buy(id, "ES")).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(reply.Ok, $"'{id}' was accepted and would have reached the broker as TA-{id}");
        Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), reply.Error!.Code);
        Assert.Empty(conn.Broker.Orders);
    }

    /// <summary>
    /// A FRAME WITH NO ID AT ALL IS A BAD REQUEST, NOT A REASON TO HANG UP.
    ///
    /// Codex C4. `id` has a GUID default, so it is never absent — but a client can send it
    /// explicitly null, and then `request_id ?? id` is null too. The guard added for F1 dereferenced
    /// it BEFORE the handler's try/catch, so the frame took the connection down with a
    /// NullReferenceException instead of being answered: every other request on that channel died
    /// with it, and the agent learned nothing about why.
    ///
    /// The error boundary is not the point — moving the check inside it would answer with
    /// UNKNOWN_ERROR. A frame that names no request is malformed, and the answer to a malformed
    /// frame is the one the rest of this method already gives.
    /// </summary>
    [Fact]
    public async Task A_frame_with_both_ids_explicitly_null_is_refused_and_the_channel_survives()
    {
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        // Sent as literal JSON: the serializer omits null properties, so an IpcRequest with Id = null
        // arrives with its GUID default and never reaches the branch under test. A client that
        // writes the field explicitly does.
        var reply = await client.SendRawAsync(
                $$"""{"v":{{Versions.ProtocolVersion}},"id":null,"op":"{{Ops.Status}}","request_id":null}""")
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(reply.Ok);
        Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), reply.Error!.Code);

        // The connection is still usable, which is the half that made this worth fixing: one bad
        // frame must not take an agent's channel down with it.
        var after = await client.SendAsync(new IpcRequest { Op = Ops.Status }).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(after.Ok, Json.Write(after.Error));
        Assert.Empty(conn.Broker.Orders);
    }

    // -------------------------------------------- ONE deadline for the operation (round 8, F1)

    /// <summary>A gateway over a simulator whose emergency budget and latency the test chooses.</summary>
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

    /// <summary>
    /// THE CLOCK BELONGS TO THE OPERATION, NOT TO EACH RPC INSIDE IT.
    ///
    /// Codex round-7 F1, and its own check. A cancel-all is a read, then a resolution per order,
    /// then a leg per order — and every one of them used to start its own two seconds, so the bound
    /// was paid once per RPC and the promise scaled with the size of the book. Measured by Codex:
    /// three replies delayed 1.9 s each made this take about 5.7 s against a promise of 2.
    ///
    /// The scope now carries ONE absolute deadline and every RPC inside it gets what is left.
    /// </summary>
    [Fact]
    public async Task A_sweep_pays_the_emergency_budget_once_not_once_per_rpc()
    {
        var (gw, conn, db) = await ReadyWithBudget(TimeSpan.FromSeconds(2));
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        Assert.True((await client.SendAsync(Buy("f1-a", "ES")).WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        Assert.Single(await gw.OrdersAsync(false));

        // Every simulator call now costs 1.9 s: the orders read, the target resolution, the cancel.
        conn.Faults.LatencyMs = 1900;

        var timer = Stopwatch.StartNew();
        var reply = await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "f1-sweep" })
            .WaitAsync(TimeSpan.FromSeconds(30));
        timer.Stop();

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(4),
            $"the sweep took {timer.Elapsed.TotalSeconds:0.00}s against a two-second budget — each RPC is still starting its own clock");
        Assert.True(timer.Elapsed > TimeSpan.FromSeconds(1),
            $"the sweep returned in {timer.Elapsed.TotalSeconds:0.00}s — the latency never applied, so this measures nothing");

        // And whatever it did, it says so per leg rather than leaving the owner to guess.
        var data = (JsonElement)reply.Data!;
        Assert.Equal(1, data.GetProperty("outcomes").GetArrayLength());
    }

    /// <summary>
    /// FIVE ORDERS, ONE BUDGET, AND NOTHING SKIPPED IN SILENCE.
    ///
    /// The second half of F1's acceptance. The loop this replaces awaited each leg before starting
    /// the next AND had no try/catch, so one failing leg abandoned every leg after it — silently,
    /// because the whole sweep surfaced as a single transport error that named none of the orders
    /// left working. Every order now appears in the answer with what became of it.
    /// </summary>
    [Fact]
    public async Task A_five_order_sweep_answers_within_the_budget_and_accounts_for_every_order()
    {
        var (gw, conn, db) = await ReadyWithBudget(TimeSpan.FromSeconds(2));
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        foreach (var sym in new[] { "ES", "NQ", "ES", "NQ", "ES" })
            Assert.True((await client.SendAsync(Buy($"f1-{Guid.NewGuid():n}", sym)).WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        Assert.Equal(5, (await gw.OrdersAsync(false)).Count);

        conn.Faults.LatencyMs = 1000;   // a second per leg, against a two-second operation

        var timer = Stopwatch.StartNew();
        var reply = await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "f1-five" })
            .WaitAsync(TimeSpan.FromSeconds(30));
        timer.Stop();

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5),
            $"five legs took {timer.Elapsed.TotalSeconds:0.00}s — the budget is still being paid per leg");

        var data = (JsonElement)reply.Data!;
        var outcomes = data.GetProperty("outcomes").EnumerateArray().ToList();
        Assert.Equal(5, outcomes.Count);
        foreach (var o in outcomes)
            Assert.Contains(o.GetProperty("outcome").GetString(),
                new[] { "sent-and-confirmed", "sent-not-confirmed", "not-sent", "nothing-to-do" });

        // A LEG THAT IS NEVER SENT SAYS SO, and that is the distinction an owner needs: "nothing was
        // even attempted on this order" is different news from "we tried and do not know". Legs are
        // issued in bounded waves, so the fifth one's turn arrives after the budget is gone.
        Assert.True(data.GetProperty("not_sent").GetInt32() > 0,
            "no leg was reported as not sent, so either the budget was not spent or a leg was dropped in silence");
        var unsent = outcomes.Where(o => o.GetProperty("outcome").GetString() == "not-sent").ToList();
        Assert.All(unsent, o => Assert.Contains("not sent", o.GetProperty("error").GetString()!));

        // Every order is accounted for exactly once, whatever became of it.
        Assert.Equal(5, outcomes.Select(o => o.GetProperty("request_id").GetString()).Distinct().Count());

        // The claim and the book still have to agree — bdf9a24's rule, unchanged by any of this.
        var claimed = data.GetProperty("cancelled").GetInt32();
        var really = (await gw.OrdersAsync(true)).Count(o => o.State == ExecutionState.CANCELLED);
        Assert.True(claimed <= really, $"claimed cancelled={claimed} while {really} are actually cancelled");
    }

    // ---------------------------------------------------------- the sweep nonce (F9)

    /// <summary>
    /// A NONCE THAT REPEATS MUST NOT MAKE A SWEEP REPLAY AN OLDER ONE.
    ///
    /// Codex F9 on d25dbb4: the nonce was eight hex characters — 32 bits — so at roughly 77,000
    /// lifetime sweeps the birthday probability of colliding with this installation's own durable
    /// history reaches about half. A repeat makes leg <c>op-{nonce}-cancelall-0</c> an id the store
    /// already holds, so the leg REPLAYS that record: the sweep counts an old CANCELLED while the
    /// order it was actually pointed at is left WORKING. That is the bdf9a24 fault — a claim that is
    /// not true about the book in front of it — reached by a third route.
    ///
    /// The nonce is now a whole GUID, and this test does not depend on that: the seam forces the
    /// collision so the RECOVERY is what is measured. It repeats the value for the first two mints
    /// and then yields real ones, which is the shape a genuine collision has — a constant source
    /// could not recover by construction and would be testing the refusal instead.
    /// </summary>
    [Fact]
    public async Task A_repeated_sweep_nonce_is_detected_and_the_second_sweep_still_cancels()
    {
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var _1 = db;
        var pipe = NewPipe();

        var mints = 0;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe)
        {
            SweepNonceSource = () => ++mints <= 2 ? "collide" : Guid.NewGuid().ToString("n")
        };
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        Assert.True((await client.SendAsync(Buy("nonce-a", "ES")).WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        var first = (JsonElement)(await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "sweep-n1" })
            .WaitAsync(TimeSpan.FromSeconds(10))).Data!;
        Assert.Equal(1, first.GetProperty("cancelled").GetInt32());

        // A NEW order, and a second sweep whose first nonce attempt is the one already in history.
        Assert.True((await client.SendAsync(Buy("nonce-b", "NQ")).WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        Assert.Single(await gw.OrdersAsync(false));

        var second = (JsonElement)(await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "sweep-n2" })
            .WaitAsync(TimeSpan.FromSeconds(10))).Data!;

        // The claim AND the book. A replayed leg reports the old record's CANCELLED and leaves the
        // order it was aimed at working, so both halves have to be read.
        Assert.Equal(1, second.GetProperty("attempted").GetInt32());
        Assert.Equal(1, second.GetProperty("cancelled").GetInt32());
        Assert.Empty(await gw.OrdersAsync(false));
        Assert.Equal(2, (await gw.OrdersAsync(true)).Count(o => o.State == ExecutionState.CANCELLED));
        Assert.Equal(2, conn.Broker.Orders.Count(o => o.State == ExecutionState.CANCELLED));

        // And the collision is VISIBLE. A one-in-2^128 event that happens silently is one nobody
        // can ever confirm happened.
        Assert.True(HasEvent(db, "sweep_nonce_collision"),
            "the nonce collided and was recovered from, but nothing was recorded");
    }

    /// <summary>The minted id has to satisfy the same bound the gateway enforces on an agent's.</summary>
    [Fact]
    public async Task A_minted_sweep_id_still_fits_the_client_order_id_budget()
    {
        var (gw, _, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        Assert.True((await client.SendAsync(Buy("budget-a", "ES")).WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        var sweep = (JsonElement)(await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "sweep-budget" })
            .WaitAsync(TimeSpan.FromSeconds(10))).Data!;

        var minted = sweep.GetProperty("requests").EnumerateArray()
            .Select(r => r.GetProperty("request_id").GetString()!).Single();

        // Widening the nonce moved this number; nothing was checking it, and a minted id is not
        // subject to the pipe's own guard because it never crosses the pipe.
        var coid = TradingGateway.ClientOrderIdFor(minted);
        Assert.True(coid.Length <= 64,
            $"minted id '{minted}' becomes a {coid.Length}-character client order id, over the 64 the budget allows");
        Assert.Matches("^[A-Za-z0-9-]+$", coid);
    }

    static bool HasEvent(Database db, string ev) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT COUNT(*) FROM engineering_log WHERE event=$e", ("$e", ev));
        return Convert.ToInt32(c.ExecuteScalar()) > 0;
    });

    // ---------------------------------------------------------- the EFFECTIVE id (F1 / V1)

    /// <summary>A mutating frame that carries its id in the FRAME field and omits request_id.</summary>
    static IpcRequest BuyWithFrameId(string frameId, string symbol) => new()
    {
        Op = Ops.Buy,
        Id = frameId,
        RequestId = null,           // the whole exploit: the guarded field is simply not sent
        Args = new()
        {
            ["symbol"] = JsonSerializer.SerializeToElement(symbol),
            ["quantity"] = JsonSerializer.SerializeToElement("1"),
            ["limit"] = JsonSerializer.SerializeToElement("1")
        }
    };

    /// <summary>
    /// THE GUARD HAS TO BE ON THE VALUE THAT IS USED, NOT THE FIELD THAT MAY BE ABSENT.
    ///
    /// `GatewayPipeServer` validated `req.RequestId` and then computed `req.RequestId ?? req.Id`,
    /// so an agent that simply omitted `request_id` put an arbitrary wire string on the broker
    /// order. Measured by the round-4 verifier before this fix: a 200-character frame id containing
    /// '#', '/' and a space left this process as the 203-character ClientOrderId
    /// `TA-x#y/z w_qqq…`. Safety rule 1 requires that field to round-trip, and it is the one field
    /// the rule says must not be guessed at — the entire reason ea1f47d and 5c716aa exist.
    ///
    /// Same shapes as the request_id theory above, one field over, plus the length bound.
    /// </summary>
    [Theory]
    [InlineData("has space")]
    [InlineData("has#hash")]
    [InlineData("has/slash")]
    [InlineData("has_underscore")]
    [InlineData("émoji")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]   // 62 — one over
    public async Task A_frame_id_outside_the_conservative_charset_is_refused_when_request_id_is_omitted(string id)
    {
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        var reply = await client.SendAsync(BuyWithFrameId(id, "ES")).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(reply.Ok,
            $"frame id '{id}' was accepted with no request_id, and it reaches the broker as TA-{id}");
        Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), reply.Error!.Code);
        Assert.Empty(conn.Broker.Orders);
    }

    /// <summary>
    /// The second instance of the same class: the reserved minted PREFIX, bypassed the same way.
    ///
    /// `GatewayPipeServer` claims a minted sweep id is uncollidable BY CONSTRUCTION because the
    /// agent cannot type the shape. It could — in the frame id. Measured before this fix:
    /// `op-deadbeef-cancelall-0` reached the broker as `TA-op-deadbeef-cancelall-0` AND became a
    /// live idempotency key, which is the bdf9a24 fault (a sweep leg replaying an agent's PLACE
    /// record and counting it as cancelled) restored by a different route.
    ///
    /// Both halves are asserted: refused on the way in, and not present in the store afterwards.
    /// </summary>
    [Theory]
    [InlineData("op-deadbeef-cancelall-0")]
    [InlineData("op-anything")]
    [InlineData("OP-UPPERCASE")]
    public async Task A_frame_id_using_the_reserved_minted_prefix_is_refused_when_request_id_is_omitted(string id)
    {
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        var reply = await client.SendAsync(BuyWithFrameId(id, "ES")).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(reply.Ok, $"frame id '{id}' was accepted and can collide with a minted sweep id");
        Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), reply.Error!.Code);
        Assert.Empty(conn.Broker.Orders);

        // And it did not become an idempotency key: a sweep minting this exact id must still do
        // real work rather than replay a planted PLACE record.
        var replay = await client.SendAsync(BuyWithFrameId(id, "ES")).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(replay.Ok);
        Assert.Empty(conn.Broker.Orders);
    }

    /// <summary>
    /// A SWEEP THAT COULD NOT CANCEL EVERYTHING MUST NOT SAY IT DID.
    ///
    /// Two resting orders, and the broker definitively refuses the first cancellation — which is an
    /// ordinary thing for a broker to do: the order filled a moment ago, or the venue will not take
    /// a cancel now. One lands, one does not, and the reply has to distinguish them.
    ///
    /// This is the test that makes "cancelled counts attempts" fail on its own. Until the fake could
    /// refuse a cancel, every cancellation succeeded, attempts and successes were the same number,
    /// and the mutant that swaps them survived the entire suite.
    /// </summary>
    [Fact]
    public async Task A_sweep_that_could_not_cancel_everything_reports_only_what_landed()
    {
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        Assert.True((await client.SendAsync(Buy("refuse-a", "ES")).WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        Assert.True((await client.SendAsync(Buy("refuse-b", "NQ")).WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        Assert.Equal(2, (await gw.OrdersAsync(false)).Count);

        conn.Faults.RefuseCancel = 1;   // exactly one cancellation is refused

        var sweep = (JsonElement)(await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "sweep-refuse" })
            .WaitAsync(TimeSpan.FromSeconds(10))).Data!;

        Assert.Equal(2, sweep.GetProperty("attempted").GetInt32());
        Assert.Equal(1, sweep.GetProperty("cancelled").GetInt32());
        Assert.Equal(1, sweep.GetProperty("not_cancelled").GetArrayLength());

        // And the claim is true of the world, not just internally consistent.
        Assert.Equal(1, (await gw.OrdersAsync(true)).Count(o => o.State == ExecutionState.CANCELLED));

        // The one still out there is named, with its state, so the agent need not diff two lists.
        var stranded = sweep.GetProperty("not_cancelled")[0];
        Assert.Equal(nameof(ExecutionState.REJECTED), stranded.GetProperty("state").GetString());
        Assert.StartsWith("op-", stranded.GetProperty("request_id").GetString());
    }

    /// <summary>
    /// THE LENGTH BOUND, AND IT IS ON THE THING THAT LEAVES THE PROCESS.
    ///
    /// Bounding the request id at 64 and not the id built from it was the gap: <c>TA-</c> is
    /// prefixed on the way to the broker, so a 64-character request id left as a 67-character client
    /// order id. The bound is now 61 so the client order id fits 64 — and the test asserts the
    /// CLIENT ORDER ID length, not the request id's, because that is the string safety rule 1 is
    /// about. A mutant that loosens the cap fails here.
    ///
    /// The 64 itself is a conservative guess. ATAS's real limit is NOT VERIFIED and cannot be from
    /// this machine.
    /// </summary>
    [Fact]
    public async Task The_longest_accepted_request_id_still_fits_the_client_order_id_budget()
    {
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        var longest = new string('a', 61);
        var accepted = await client.SendAsync(Buy(longest, "ES")).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(accepted.Ok, $"a {longest.Length}-character id was refused: {Json.Write(accepted.Error)}");

        // The string that actually reaches the broker is what the budget is about.
        Assert.Equal(64, TradingGateway.ClientOrderIdFor(longest).Length);
        Assert.Equal(TradingGateway.ClientOrderIdFor(longest), conn.Broker.Orders.Single().ClientOrderId);

        var tooLong = await client.SendAsync(Buy(new string('a', 62), "NQ")).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(tooLong.Ok, "a 62-character id was accepted and would leave as a 65-character client order id");
        Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), tooLong.Error!.Code);
        Assert.Single(conn.Broker.Orders);
    }

    /// <summary>
    /// Two sweeps must not mint the same ids. The nonce is what stops a second cancel-all replaying
    /// the first one's records instead of cancelling anything — the same class of fault as the
    /// original collision, one layer in.
    /// </summary>
    [Fact]
    public async Task Two_sweeps_mint_different_ids()
    {
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        async Task<List<string>> SweepOnce(string place, string sweep)
        {
            Assert.True((await client.SendAsync(Buy(place, "ES")).WaitAsync(TimeSpan.FromSeconds(10))).Ok);
            var data = (JsonElement)(await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = sweep })
                .WaitAsync(TimeSpan.FromSeconds(10))).Data!;
            Assert.Equal(1, data.GetProperty("attempted").GetInt32());
            return data.GetProperty("requests").EnumerateArray()
                .Select(r => r.GetProperty("request_id").GetString()!).ToList();
        }

        var first = await SweepOnce("nonce-a", "sweep-nonce-1");
        var second = await SweepOnce("nonce-b", "sweep-nonce-2");

        Assert.Single(first);
        Assert.Single(second);
        Assert.NotEqual(first[0], second[0]);

        // And both really cancelled, rather than the second replaying the first's record.
        Assert.Equal(2, (await gw.OrdersAsync(true)).Count(o => o.State == ExecutionState.CANCELLED));
    }

    /// <summary>
    /// The count is of cancellations that LANDED. With nothing working, a sweep cancels nothing and
    /// must say so — and the other direction, a real working order, is cancelled and counted once.
    /// </summary>
    [Fact]
    public async Task The_count_is_what_landed_not_what_was_attempted()
    {
        // LeaveWorking, or the fake broker fills every order on arrival, the working list is empty
        // and a sweep with nothing to sweep passes every assertion vacuously.
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        var empty = (JsonElement)(await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "sweep-empty" })
            .WaitAsync(TimeSpan.FromSeconds(10))).Data!;
        Assert.Equal(0, empty.GetProperty("cancelled").GetInt32());
        Assert.Equal(0, empty.GetProperty("attempted").GetInt32());

        Assert.True((await client.SendAsync(Buy("sweep-2-place", "ES")).WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        var sweep = (JsonElement)(await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "sweep-2" })
            .WaitAsync(TimeSpan.FromSeconds(10))).Data!;

        var claimed = sweep.GetProperty("cancelled").GetInt32();
        Assert.Equal(1, sweep.GetProperty("attempted").GetInt32());   // not a vacuous sweep
        var reallyCancelled = (await gw.OrdersAsync(true)).Count(o => o.State == ExecutionState.CANCELLED);
        Assert.Equal(reallyCancelled, claimed);
        Assert.Equal(sweep.GetProperty("attempted").GetInt32() - claimed,
            sweep.GetProperty("not_cancelled").GetArrayLength());
    }
}

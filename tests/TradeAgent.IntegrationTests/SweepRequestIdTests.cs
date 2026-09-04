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
            Assert.Contains(o.GetProperty("outcome").GetString(), LegVocabulary);

        // A LEG THAT IS NEVER SENT SAYS SO, and that is the distinction an owner needs: "nothing was
        // even attempted on this order" is different news from "we tried and do not know". Legs are
        // issued in bounded waves, so the fifth one's turn arrives after the budget is gone.
        Assert.True(data.GetProperty("not_sent").GetInt32() > 0,
            "no leg was reported as not sent, so either the budget was not spent or a leg was dropped in silence");
        var unsent = outcomes.Where(o => o.GetProperty("outcome").GetString() == "not-sent").ToList();

        // EVERY not-sent leg says WHY, and there are now two honest reasons for it rather than one.
        // A leg whose turn came after the deadline was never issued; a leg that was issued and gave
        // up on its own target resolution never reached the wire either, and since round 9 it reads
        // `not-sent` too instead of claiming to have been sent (round-9 F1). The word is the claim;
        // the error is the cause, and it is the underlying failure verbatim.
        Assert.All(unsent, o => Assert.False(string.IsNullOrWhiteSpace(o.GetProperty("error").GetString())));
        Assert.Contains(unsent, o => o.GetProperty("error").GetString()!.Contains("before this leg was issued"));

        // Every order is accounted for exactly once, whatever became of it.
        Assert.Equal(5, outcomes.Select(o => o.GetProperty("request_id").GetString()).Distinct().Count());

        // The claim and the book still have to agree — bdf9a24's rule, unchanged by any of this.
        var claimed = data.GetProperty("cancelled").GetInt32();
        var really = (await gw.OrdersAsync(true)).Count(o => o.State == ExecutionState.CANCELLED);
        Assert.True(claimed <= really, $"claimed cancelled={claimed} while {really} are actually cancelled");

    }

    /// <summary>
    /// ONE ANSWER CARRYING A MIX, AND EACH WORD ASSERTED BY NAME — verifier round-9 F-5.
    ///
    /// The acceptance above is satisfied by a sweep that attempted NOTHING: at a second a leg, the
    /// orders read plus one target resolution is the whole two-second budget, every leg comes back
    /// <c>not-sent</c>, and <c>not_sent &gt; 0</c> holds (measured by the verifier: <c>cancelled = 0,
    /// attempted = 0, not_sent = 5</c>). The acceptance is "which sent, which confirmed, which not
    /// sent", and a reply in which every leg says the same thing never exercises it.
    ///
    /// Five orders, two one-shot faults, one sweep. Legs are issued in waves of
    /// <see cref="GatewayPipeServer.MaxLegsInFlight"/>, so the first wave carries the refusal, the
    /// lost answer and two ordinary cancellations, and the fifth leg supplies the fourth word: the
    /// lost answer settles UNKNOWN and <c>NeedsReconciliation</c> refuses everything issued after it.
    ///
    /// TWO FIXTURE FACTS THAT ARE LOAD-BEARING, both learned the hard way.
    ///
    /// FIFTY MILLISECONDS OF LATENCY, NOT ZERO. At zero the simulator never awaits, so <c>issue()</c>
    /// runs each leg to completion before the loop starts the next one and the wave is serial — the
    /// first UNKNOWN then refuses the other three and every word in the answer is the same one. Any
    /// non-zero latency forces the real shape, because a leg's authorization is synchronous and
    /// happens before its first await: all four are authorised before any of them can fail.
    ///
    /// ITS OWN GATEWAY, NOT A SECOND SWEEP ON A SHARED ONE. This started as a second phase of the
    /// test above and passed on macOS and FAILED ON WINDOWS, twice, with all five legs
    /// <c>not-sent</c>: whether that first sweep leaves a flagged request depends on where its
    /// deadline falls, and one flagged request refuses every leg of the next sweep. That coupling is
    /// F-1's residual doing exactly what it is routed to U2c-1 for, and it is asserted here as a
    /// precondition rather than assumed.
    /// </summary>
    [Fact]
    public async Task A_five_order_sweep_carries_a_mix_of_outcomes_in_one_answer()
    {
        var (gw, conn, db) = await ReadyWithBudget(TimeSpan.FromSeconds(5));
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        foreach (var sym in new[] { "ES", "NQ", "ES", "NQ", "ES" })
            Assert.True((await client.SendAsync(Buy($"mix-{Guid.NewGuid():n}"[..20], sym)).WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        Assert.Equal(5, (await gw.OrdersAsync(false)).Count);

        // The precondition, stated: nothing is flagged, so nothing is refused before it is tried.
        Assert.Empty(gw.Requests.NeedingReconciliation());

        conn.Faults.LatencyMs = 50;
        conn.Faults.RefuseCancel = 1;      // a DEFINITE broker refusal   -> rejected
        conn.Faults.LoseAfterSend = 1;     // sent, no answer came back   -> sent-not-confirmed

        var reply = await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "f5-mixed" })
            .WaitAsync(TimeSpan.FromSeconds(30));
        var data = (JsonElement)reply.Data!;
        var words = data.GetProperty("outcomes").EnumerateArray()
            .Select(o => o.GetProperty("outcome").GetString()!).ToList();

        Assert.Equal(5, words.Count);
        Assert.All(words, w => Assert.Contains(w, LegVocabulary));
        Assert.Contains("confirmed", words);
        Assert.Contains("rejected", words);
        Assert.Contains("not-sent", words);
        Assert.Contains("sent-not-confirmed", words);

        // The counts agree with the words rather than being kept beside them: `attempted` is every
        // leg that got as far as the wire, which is all of them except the ones that never did.
        Assert.Equal(words.Count(w => w != "not-sent"), data.GetProperty("attempted").GetInt32());
        Assert.Equal(words.Count(w => w == "not-sent"), data.GetProperty("not_sent").GetInt32());
        Assert.False(data.GetProperty("nothing_to_do").GetBoolean());

        // And the words are backed by the records they claim: nothing needs reconciling except the
        // leg that says it does.
        var needing = gw.Requests.NeedingReconciliation().Select(r => r.RequestId).ToHashSet();
        foreach (var leg in data.GetProperty("outcomes").EnumerateArray())
            Assert.Equal(leg.GetProperty("outcome").GetString() == "sent-not-confirmed",
                needing.Contains(leg.GetProperty("request_id").GetString()!));
    }

    /// <summary>
    /// THE INSTRUMENT MUST NOT OVERRUN THE DEADLINE IT IS USED TO MEASURE.
    ///
    /// Codex round-8 F3, and its own check. The simulator honours the operation deadline — that is
    /// what makes every measurement in this section mean anything — but its precheck asked whether
    /// the LONGER of its two injected latencies fitted, while it then awaits them one after the
    /// other. With both set to 1200 ms inside a two-second operation it accepted a 1200 ms wait and
    /// spent 2400: the call returned successfully 400 ms after the whole operation had promised to
    /// be over, and <c>WorstCaseOperationPath</c> — which the shutdown drain is derived from —
    /// under-reported by the same amount.
    ///
    /// The discriminator here is not a stopwatch reading. It is RETURN versus THROW: summed, the
    /// call cannot fit and says so at the deadline; maxed, it succeeds past it.
    /// </summary>
    [Fact]
    public async Task The_simulators_two_latencies_add_up_rather_than_competing()
    {
        var conn = new FakeConnector(new FakeBroker(),
            new FaultProfile { LatencyMs = 1200, UncancellableLatencyMs = 1200 });

        // The reported worst case is the sum, because that is what one call costs.
        Assert.Equal(TimeSpan.FromMilliseconds(2400), conn.WorstCaseOperationPath);

        var timer = Stopwatch.StartNew();
        ConnectorTransportException? refused = null;
        using (RiskReducingScope.Begin(TimeSpan.FromSeconds(2)))
        {
            try { await conn.GetPositionsAsync(conn.Broker.AccountId).WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (ConnectorTransportException ex) { refused = ex; }
        }
        timer.Stop();

        Assert.True(refused is not null,
            $"a 2400 ms call completed inside a 2000 ms operation, in {timer.Elapsed.TotalMilliseconds:0} ms");
        Assert.Contains("deadline", refused!.Message);
        Assert.True(timer.Elapsed < TimeSpan.FromMilliseconds(2400),
            $"it took {timer.Elapsed.TotalMilliseconds:0} ms — it ran the latency out rather than stopping at the deadline");

        // The other direction, so a simulator that simply refuses everything is not a passing one:
        // two latencies that DO fit are still served.
        var fits = new FakeConnector(new FakeBroker(),
            new FaultProfile { LatencyMs = 300, UncancellableLatencyMs = 300 });
        using (RiskReducingScope.Begin(TimeSpan.FromSeconds(2)))
            Assert.NotNull(await fits.GetPositionsAsync(fits.Broker.AccountId).WaitAsync(TimeSpan.FromSeconds(20)));
    }

    // ------------- the word comes from the CONNECTOR's transport result (round 10, F4 / verifier F-1)

    /// <summary>
    /// A LEG THE CONNECTOR REFUSED BEFORE THE WIRE IS `not-sent`, WHATEVER THE RECORD SAYS.
    ///
    /// Verifier round-9 F-1, measured through the real pipe: a sweep leg the SHIPPED connector
    /// refused before sending came back <c>sent-not-confirmed</c> with UNKNOWN and
    /// <c>needs_reconciliation</c>. Round 9's rule — "the record decides the word" — is right about
    /// the record being the only thing allowed to produce a word, and wrong about the record being
    /// able to. <c>TradingGateway</c> maps EVERY <c>ConnectorTransportException</c> to UNKNOWN, which
    /// is correct from up there: a refusal before the send gate and a half-written frame are the same
    /// exception by the time they arrive. They are not the same fact, and only the connector knows.
    ///
    /// TWO HARMS, and the second is the reason this is not a wording defect. The owner is sent to
    /// hunt through ATAS for an order this process proved it never sent; and
    /// <c>needs_reconciliation</c> refuses ALL further execution with
    /// <c>TRADING_PAUSED_UNRECONCILED</c> — including the retry the sentence itself advises, and
    /// including the next <c>cancel-all</c>.
    ///
    /// WHAT IS FIXED HERE AND WHAT IS NOT. The WORD is the pipe server's and it is fixed: the leg
    /// reads <c>not-sent</c> and is not counted as attempted, and the answer now carries the
    /// transport result itself so the evidence is visible rather than inferred. The RECORD is
    /// <c>TradingGateway.SettleUnknown</c>'s, which this unit may not edit — so the row stays UNKNOWN
    /// with the flag set, the reply says so in the same object, and it is routed to U2c-1 with this
    /// measurement attached.
    /// </summary>
    [Fact]
    public async Task A_leg_refused_before_the_wire_reads_not_sent_even_though_its_record_is_unknown()
    {
        var (gw, conn, db) = await ReadyWithBudget(TimeSpan.FromSeconds(5));
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        Assert.True((await client.SendAsync(Buy("f4-a", "ES")).WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        Assert.Single(await gw.OrdersAsync(false));

        // The next MUTATION is refused before anything is sent — the branch the shipped
        // AtasConnector takes when a leg's turn arrives after the operation is already over.
        conn.Faults.RefuseBeforeSend = 1;

        var reply = await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "f4-sweep" })
            .WaitAsync(TimeSpan.FromSeconds(30));
        var data = (JsonElement)reply.Data!;
        var leg = data.GetProperty("outcomes").EnumerateArray().Single();

        Assert.Equal("not-sent", leg.GetProperty("outcome").GetString());
        Assert.Equal(0, data.GetProperty("attempted").GetInt32());
        Assert.Equal(1, data.GetProperty("not_sent").GetInt32());

        // The evidence is IN the answer: the connector's own report of where the frame got to.
        Assert.Equal("NothingWritten", leg.GetProperty("transport").GetString());

        // And the honest residual, asserted rather than left for somebody to discover: the RECORD is
        // still UNKNOWN and still flagged, because `TradingGateway.SettleUnknown` writes it and this
        // unit may not open that file. The leg no longer LIES about it; the row is U2c-1's to fix.
        Assert.Equal("UNKNOWN", leg.GetProperty("state").GetString());
        Assert.Single(gw.Requests.NeedingReconciliation());
    }

    /// <summary>
    /// THE WORD `sent-not-confirmed` MEANS UNKNOWN AND RECONCILIATION, ON EVERY REACHABLE LEG.
    ///
    /// The promise the word makes, asserted over the shapes the suite can actually produce rather
    /// than argued from the mapping. A leg that says "it reached the wire and we do not know" must
    /// have a record that will be reconciled, or the word is an instruction to go and look at
    /// something nothing will ever settle.
    /// </summary>
    [Fact]
    public async Task Every_sent_not_confirmed_leg_carries_an_unknown_record_that_will_be_reconciled()
    {
        var (gw, conn, db) = await ReadyWithBudget(TimeSpan.FromSeconds(5));
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        foreach (var sym in new[] { "ES", "NQ" })
            Assert.True((await client.SendAsync(Buy($"f4b-{sym}", sym)).WaitAsync(TimeSpan.FromSeconds(10))).Ok);

        // One leg is refused before the wire, one is lost after it: the two halves of the tri-state
        // that are not an answer.
        conn.Faults.RefuseBeforeSend = 1;
        // Two seconds a call against a five-second operation: the orders read and each leg's target
        // resolution fit, and the CANCEL is the call that runs out of budget — which is the shape
        // that produces an ambiguous mutation rather than a read that never reached the wire.
        conn.Faults.LatencyMs = 2000;

        var reply = await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "f4-sweep-b" })
            .WaitAsync(TimeSpan.FromSeconds(30));
        var legs = ((JsonElement)reply.Data!).GetProperty("outcomes").EnumerateArray().ToList();

        var unconfirmed = legs.Where(l => l.GetProperty("outcome").GetString() == "sent-not-confirmed").ToList();
        Assert.NotEmpty(unconfirmed);
        foreach (var leg in unconfirmed)
        {
            Assert.Equal("UNKNOWN", leg.GetProperty("state").GetString());
            var record = gw.GetRequest(leg.GetProperty("request_id").GetString()!);
            Assert.NotNull(record);
            Assert.True(record!.NeedsReconciliation,
                $"{leg.GetProperty("request_id").GetString()} reads sent-not-confirmed and nothing will reconcile it");
        }
    }

    // ------------------------------------- the per-leg vocabulary is 1:1 with the record (round 9, F1)

    /// <summary>
    /// EVERY WORD A LEG IS ALLOWED TO ANSWER WITH, AND THERE ARE EXACTLY FIVE.
    ///
    /// It had six, and the sixth was a category error: <c>nothing-to-do</c> is a fact about the
    /// OPERATION — a sweep with no targets — and not about a leg. A leg that exists had something to
    /// act on; if it never reached the wire the word for that is <c>not-sent</c>, and a `close-all`
    /// symbol whose position had already gone is still named in <c>nothing_to_close</c>. And
    /// <c>sent-and-confirmed</c> led with a claim about the wire when the point of the word is the
    /// broker's answer: it is <c>confirmed</c> (Codex round-9 F3).
    /// </summary>
    static readonly string[] LegVocabulary =
        ["confirmed", "rejected", "sent-still-working", "sent-not-confirmed", "not-sent"];

    /// <summary>
    /// THE VOCABULARY IS EXACTLY FIVE WORDS, OVER EVERY COMBINATION THAT CAN REACH IT.
    ///
    /// A membership test over the replies some fixture happens to produce can only ever cover the
    /// arms those fixtures reach — which is how the round-9 mapping shipped with an arm no test
    /// touched, found only by mutating it. So this drives the mapping itself, through the seam it is
    /// exported for, over the FULL cross product: every <see cref="ExecutionState"/> against every
    /// <see cref="TransportOutcome"/> and against "no mutating call was attempted".
    ///
    /// Both directions, so a mapping that refused everything would not pass: every one of the five
    /// words must be produced by some combination, and no combination may produce anything else.
    /// </summary>
    [Fact]
    public void The_per_leg_vocabulary_is_exactly_five_words_over_every_reachable_combination()
    {
        List<TransportOutcome?> transports = [null, .. Enum.GetValues<TransportOutcome>().Cast<TransportOutcome?>()];
        List<ExecutionState?> states = [null, .. Enum.GetValues<ExecutionState>().Cast<ExecutionState?>()];

        var produced = new HashSet<string>();
        foreach (var state in states)
            foreach (var transport in transports)
            {
                var word = GatewayPipeServer.LegWordFor(state, transport);
                Assert.Contains(word, LegVocabulary);
                produced.Add(word);
            }

        Assert.Equal(LegVocabulary.OrderBy(w => w), produced.OrderBy(w => w));
    }

    /// <summary>
    /// EVERY ARM CONSULTS THE TRANSPORT RESULT, INCLUDING THE ARMS THAT READ A DEFINITE ANSWER
    /// (Codex round-10 PRIOR R9-F4, PARTIAL).
    ///
    /// Round 10 made the UNRESOLVED states consult the connector's report and left the definite ones
    /// reading the record alone. That is one rule short of the guarantee the word set is worth
    /// having: `confirmed`, `rejected` and `sent-still-working` are all claims that this leg's frame
    /// reached the broker, and the connector can PROVE that it did not. A record can be in a definite
    /// state for a reason that has nothing to do with this leg — the connector's event stream updates
    /// request records, and a sweep leg can find one already settled — so "the record says CANCELLED"
    /// and "this leg cancelled it" are not the same sentence.
    ///
    /// The rule, in one line: <c>NothingWritten</c> is the only report strong enough to overrule the
    /// record, and it overrules every arm. Everything else defers to the record where the record can
    /// answer (a broker's answer is the only thing that knows WHICH answer came back, and an
    /// idempotent replay arrives with a settled record and no transport of its own), and to the
    /// transport where it cannot.
    ///
    /// EXHAUSTIVE BY CONSTRUCTION: every <see cref="ExecutionState"/> plus "no record" against every
    /// <see cref="TransportOutcome"/> plus "nothing attempted". The expected words are written down
    /// here rather than derived, so this is a table and not a second copy of the mapping.
    /// </summary>
    [Fact]
    public void Every_arm_of_the_leg_classifier_consults_the_transport_result()
    {
        // What the RECORD says, where the record is in a state only a broker's answer can produce.
        var answered = new Dictionary<ExecutionState, string>
        {
            [ExecutionState.CANCELLED] = "confirmed",
            [ExecutionState.FILLED] = "confirmed",
            [ExecutionState.REJECTED] = "rejected",
            [ExecutionState.WORKING] = "sent-still-working",
            [ExecutionState.ACKNOWLEDGED] = "sent-still-working",
            [ExecutionState.PARTIALLY_FILLED] = "sent-still-working",
            [ExecutionState.CANCEL_PENDING] = "sent-still-working"
        };

        // THE STATES THAT ARE THE PIPE SERVER'S OWN PROOF THAT A MUTATING STEP WAS DISPATCHED.
        // `TradingGateway` writes DISPATCHING immediately before the connector's mutating call and
        // the other two are reachable only through it. Written down here rather than derived, so
        // this stays a table and does not become a second copy of the mapping.
        ExecutionState[] dispatched =
            [ExecutionState.DISPATCHING, ExecutionState.UNKNOWN, ExecutionState.RECONCILING];

        List<TransportOutcome?> transports = [null, .. Enum.GetValues<TransportOutcome>().Cast<TransportOutcome?>()];
        List<ExecutionState?> states = [null, .. Enum.GetValues<ExecutionState>().Cast<ExecutionState?>()];

        var wrong = new List<string>();
        foreach (var state in states)
            foreach (var transport in transports)
            {
                var expected = transport switch
                {
                    // Proven: not one byte of this leg's frame left the process.
                    TransportOutcome.NothingWritten => "not-sent",

                    // The record knows which answer came back, and only the record knows.
                    _ when state is { } s && answered.TryGetValue(s, out var word) => word,

                    // NOTHING WAS REPORTED, so the RECORD decides whether a mutating step of this
                    // leg was ever dispatched — and only a record that never got that far may
                    // produce the assurance (verifier round-11 F-2).
                    null => state is { } d && dispatched.Contains(d) ? "sent-not-confirmed" : "not-sent",

                    // The record cannot settle it and the connector says the frame may have gone.
                    _ => "sent-not-confirmed"
                };

                var actual = GatewayPipeServer.LegWordFor(state, transport);
                if (actual != expected)
                    wrong.Add($"{state?.ToString() ?? "no record"} + {transport?.ToString() ?? "nothing attempted"}: " +
                              $"expected '{expected}', got '{actual}'");
            }

        // Every failing combination at once, so a mutated arm names itself rather than stopping the
        // run at the first pair it broke.
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    /// <summary>
    /// `not-sent` IS AN ASSURANCE, SO IT MAY NEVER COME FROM AN ABSENCE OF INFORMATION.
    ///
    /// The empty transport record used to carry two different facts: a leg that never started a
    /// mutation (a target resolution that failed, a leg parked for approval, a `close-all` symbol
    /// with nothing to close) and a mutation that started and left by a route no site wrote down.
    /// The first is honestly <c>not-sent</c>; the second may be an order sitting at the broker, and
    /// reading it as <c>not-sent</c> tells the owner nothing was sent and raises no reconciliation
    /// flag (Codex round-10 F2).
    ///
    /// The two are now different records, and this is the rule in one place. BOTH DIRECTIONS: a
    /// record nobody attempted must still read <c>not-sent</c>, or the fix would have bought safety
    /// by flagging every leg that never reached a wire — which is the defect round 9 spent a round
    /// removing, arrived at from the other side.
    /// </summary>
    [Fact]
    public void An_attempted_mutation_that_reported_nothing_is_not_confirmed_and_an_unattempted_one_is_not_sent()
    {
        // Nothing was ever attempted: the strongest form of "nothing was sent", and unchanged.
        //
        // THE STATE IS AWAITING_APPROVAL AND THAT IS NOT INCIDENTAL. It used to be UNKNOWN, which
        // round 12 made a state the pipe server reads as its OWN proof that a mutating step was
        // dispatched — so the pair "no ledger report" + "a record that reached the wire" is now
        // `sent-not-confirmed` whatever the connector did or did not write down (verifier round-11
        // F-2). What this test is about is the LEDGER, so its record is one that proves the leg
        // never got that far, and the UNKNOWN case is asserted below as the new fact it is.
        var untouched = new TransportRecord();
        Assert.Null(untouched.Outcome);
        Assert.Equal("not-sent", GatewayPipeServer.LegWordFor(ExecutionState.AWAITING_APPROVAL, untouched.Outcome));

        // The same empty record on a leg whose own record proves a mutation WAS dispatched: the
        // assurance is not available, and it is not available for a connector that says nothing.
        Assert.Equal("sent-not-confirmed", GatewayPipeServer.LegWordFor(ExecutionState.UNKNOWN, untouched.Outcome));
        Assert.Equal("sent-not-confirmed", GatewayPipeServer.LegWordFor(ExecutionState.DISPATCHING, untouched.Outcome));
        Assert.Equal("sent-not-confirmed", GatewayPipeServer.LegWordFor(ExecutionState.RECONCILING, untouched.Outcome));

        // A mutation started and no site reported where it got to: fail-closed, and it is the
        // fail-closed word on a pre-dispatch record too, where the pipe server has no proof of its
        // own and the connector's attempt marker is the only thing that knows.
        var attempted = new TransportRecord();
        using (TransportLedger.Attach(attempted)) TransportLedger.Attempt();
        Assert.Equal(TransportOutcome.PossiblyWritten, attempted.Outcome);
        Assert.Equal("sent-not-confirmed", GatewayPipeServer.LegWordFor(ExecutionState.UNKNOWN, attempted.Outcome));
        Assert.Equal("sent-not-confirmed", GatewayPipeServer.LegWordFor(ExecutionState.AWAITING_APPROVAL, attempted.Outcome));

        // And a site that KNOWS still overrides the fallback, in both directions — otherwise the
        // fallback would have swallowed the one report that can honestly say `not-sent`.
        var proven = new TransportRecord();
        using (TransportLedger.Attach(proven))
        {
            TransportLedger.Attempt();
            TransportLedger.Record(TransportOutcome.NothingWritten);
        }
        Assert.Equal(TransportOutcome.NothingWritten, proven.Outcome);
        Assert.Equal("not-sent", GatewayPipeServer.LegWordFor(ExecutionState.UNKNOWN, proven.Outcome));

        var answered = new TransportRecord();
        using (TransportLedger.Attach(answered))
        {
            TransportLedger.Attempt();
            TransportLedger.Record(TransportOutcome.ReplyReceived);
        }
        Assert.Equal(TransportOutcome.ReplyReceived, answered.Outcome);
    }

    /// <summary>
    /// A STATE NOTHING MAPS MUST FAIL LOUDLY, NOT BECOME THE MOST DANGEROUS WORD IN THE SET.
    ///
    /// Verifier round-9 F-3. `Describe()` had its catch-all removed for exactly this reason and the
    /// commit message for it was right — a new outcome must not be reported as something wrong and
    /// dangerous in silence — while `Classify` one switch over kept <c>_ => NotConfirmed</c>. So a
    /// new `ExecutionState` would have been reported as <c>sent-not-confirmed</c>: the word that
    /// promises UNKNOWN and reconciliation, with no compiler complaint and no failing test.
    ///
    /// Both switches are exhaustive now, and this is the assertion that keeps them so. A value
    /// outside the enum is the only way to reach the arm from a test — which is the point: for every
    /// value INSIDE it, the cross-product test above proves there is a real mapping.
    /// </summary>
    [Fact]
    public void An_execution_state_nothing_maps_throws_rather_than_becoming_a_word()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => GatewayPipeServer.LegWordFor((ExecutionState)999, TransportOutcome.ReplyReceived));
        Assert.Contains("999", ex.Message);
    }

    /// <summary>
    /// `nothing-to-do` IS A FACT ABOUT THE OPERATION, AND IT IS REPORTED THERE.
    ///
    /// It was a per-leg word, which cannot be right: a leg exists because there was something for it
    /// to act on. A sweep with NO targets is the thing it truthfully describes, so that is where it
    /// lives — and a `close-all` symbol whose position had gone by the time its leg ran is
    /// <c>not-sent</c> (nothing reached a wire) while still being named in <c>nothing_to_close</c>.
    /// </summary>
    [Fact]
    public async Task A_sweep_with_no_targets_says_so_as_a_whole_and_not_on_any_leg()
    {
        var (gw, conn, db) = await ReadyWithBudget(TimeSpan.FromSeconds(5));
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        // Nothing has been placed, so there is nothing working and nothing held.
        foreach (var op in new[] { Ops.CancelAll, Ops.CloseAll })
        {
            var reply = await client.SendAsync(new IpcRequest { Op = op, RequestId = $"empty-{op}" })
                .WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(reply.Ok, reply.Error?.Message);
            var data = (JsonElement)reply.Data!;
            Assert.True(data.GetProperty("nothing_to_do").GetBoolean(), $"'{op}' had no targets and did not say so");
            Assert.Empty(data.GetProperty("outcomes").EnumerateArray());
        }

        // And a sweep that DOES have targets does not claim it. Otherwise the flag would be true
        // whenever nothing happened for any reason at all.
        Assert.True((await client.SendAsync(Buy("ntd-1", "ES")).WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        var swept = await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "ntd-sweep" })
            .WaitAsync(TimeSpan.FromSeconds(20));
        var sweptData = (JsonElement)swept.Data!;
        Assert.False(sweptData.GetProperty("nothing_to_do").GetBoolean());
        Assert.Single(sweptData.GetProperty("outcomes").EnumerateArray());
    }

    /// <summary>Reads the per-leg outcomes out of a sweep reply as (outcome, state) pairs.</summary>
    static List<(string Outcome, string? State, string Id)> Outcomes(JsonElement sweep) =>
        sweep.GetProperty("outcomes").EnumerateArray()
            .Select(o => (
                o.GetProperty("outcome").GetString()!,
                // Absent, not null, when the leg has no record — which is itself the evidence that
                // nothing was written down for it.
                o.TryGetProperty("state", out var st) ? st.GetString() : null,
                o.GetProperty("request_id").GetString()!))
            .ToList();

    /// <summary>
    /// A DEFINITE BROKER REFUSAL IS NOT AN UNKNOWN, AND IT MUST NOT READ AS ONE.
    ///
    /// Codex round-8 F1, first check, verbatim: set `RefuseCancel=1`, and the leg came back
    /// <c>sent-not-confirmed</c> while its record was REJECTED. That word means "the gateway has
    /// recorded UNKNOWN and will reconcile", so it sent the owner to hunt through ATAS for the state
    /// of an order the broker had already given a final answer about — and safety rule 3 exists
    /// precisely to keep those two apart in the other direction.
    ///
    /// The word now comes off the record, so it cannot be produced by any state but REJECTED.
    /// </summary>
    [Fact]
    public async Task A_definite_broker_refusal_reads_rejected_and_needs_no_reconciliation()
    {
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        Assert.True((await client.SendAsync(Buy("vocab-a", "ES")).WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        Assert.True((await client.SendAsync(Buy("vocab-b", "NQ")).WaitAsync(TimeSpan.FromSeconds(10))).Ok);

        conn.Faults.RefuseCancel = 1;   // exactly one cancellation is definitively refused

        var sweep = (JsonElement)(await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "vocab-sweep" })
            .WaitAsync(TimeSpan.FromSeconds(10))).Data!;
        var legs = Outcomes(sweep);

        Assert.All(legs, l => Assert.Contains(l.Outcome, LegVocabulary));
        var refused = Assert.Single(legs, l => l.Outcome == "rejected");
        Assert.Equal(nameof(ExecutionState.REJECTED), refused.State);

        // The lie this replaces: nothing here is unknown, so nothing may say it is.
        Assert.DoesNotContain(legs, l => l.Outcome == "sent-not-confirmed");

        // And the record agrees with the word, which is the whole property: a definite refusal is
        // final, so the gateway is not asking anybody to reconcile it.
        var record = gw.GetRequest(refused.Id)!;
        Assert.Equal(ExecutionState.REJECTED, record.State);
        Assert.False(record.NeedsReconciliation,
            "a definite refusal was flagged for reconciliation — then 'rejected' promises something it does not deliver");
    }

    /// <summary>
    /// A LEG THAT NEVER REACHED THE WIRE SAYS SO, AND LEAVES NO RECORD TO RECONCILE.
    ///
    /// Codex round-8 F1, second check: expire the target resolution before `_requests.TryCreate`, and
    /// the reply still said <c>sent-not-confirmed</c> — with <c>attempted=0</c> and no leg record
    /// anywhere. Two claims that contradict each other in the same object, and the dangerous one is
    /// the word: it tells the owner an order may be live at the broker when this process never
    /// touched the wire.
    ///
    /// The fixture is the shape the deadline really produces. The orders read spends most of the
    /// operation's budget, so each leg is ISSUED — the deadline has not passed when its turn comes,
    /// which is the pre-issue `not-sent` branch and is already covered elsewhere — and then fails
    /// INSIDE, on the resolution read, before any record is written.
    /// </summary>
    [Fact]
    public async Task A_leg_that_failed_before_the_wire_reads_not_sent_and_writes_no_record()
    {
        var (gw, conn, db) = await ReadyWithBudget(TimeSpan.FromSeconds(1));
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        Assert.True((await client.SendAsync(Buy("presend-a", "ES")).WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        Assert.True((await client.SendAsync(Buy("presend-b", "NQ")).WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        Assert.Equal(2, (await gw.OrdersAsync(false)).Count);

        // Seven tenths of a one-second operation goes on the orders read, so each leg's own
        // resolution cannot fit and gives up before it writes anything down.
        conn.Faults.LatencyMs = 700;

        var sweep = (JsonElement)(await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "presend-sweep" })
            .WaitAsync(TimeSpan.FromSeconds(30))).Data!;
        var legs = Outcomes(sweep);

        Assert.Equal(2, legs.Count);
        Assert.All(legs, l => Assert.Equal("not-sent", l.Outcome));
        Assert.DoesNotContain(legs, l => l.Outcome == "sent-not-confirmed");

        // `attempted` counted legs holding a record; now it counts legs that got as far as the wire,
        // so it cannot disagree with the words beside it.
        Assert.Equal(0, sweep.GetProperty("attempted").GetInt32());
        Assert.Equal(0, sweep.GetProperty("cancelled").GetInt32());
        Assert.Equal(2, sweep.GetProperty("not_sent").GetInt32());

        // THE PROOF THAT NOTHING WAS SENT: the record is written before the wire is touched, so its
        // absence is not an inference. Neither is the book — both orders are still working.
        foreach (var leg in legs)
            Assert.Null(gw.GetRequest(leg.Id));
        Assert.Equal(2, (await gw.OrdersAsync(false)).Count);

        // AND THE SENTENCE AGREES WITH THE WORD (verifier round-11 L-4). The simulator threw one
        // message for reads and mutations alike — "it is not known whether it acted" — so a leg the
        // gateway correctly calls `not-sent` carried, in the SAME object, a sentence saying the
        // outcome is unknown. The word is what the machine reads and the sentence is what the owner
        // reads, and they were saying different things about the same leg. The shipped
        // `AtasConnector` has distinguished the two since round 7 (`EmergencySentence`); this is the
        // connector the product ships for paper trading.
        foreach (var error in sweep.GetProperty("outcomes").EnumerateArray()
                     .Select(o => o.GetProperty("error").GetString() ?? ""))
        {
            Assert.DoesNotContain("it is not known whether it acted", error);
            Assert.Contains("Nothing was placed or cancelled", error);
        }
    }

    /// <summary>
    /// `not-sent` IS AN ASSURANCE, AND UNTIL NOW IT WAS ONE EVERY CONNECTOR HAD TO OPT INTO.
    ///
    /// Verifier round-11 F-2. `TransportLedger.Attempt()` is what makes an empty transport record
    /// mean "no mutating call was ever started" — and it is called by the CONNECTOR. Both connectors
    /// in this tree call it; `ITradingConnector`, which is what a third party implements, said nothing
    /// about it. So a connector written to the public contract that really cancels an order at the
    /// broker and never touches the ledger reported <c>not-sent</c> with <c>attempted: 0</c> — the
    /// exact report the attempt marker exists to make impossible, produced by an absence of
    /// information, and with the `transport` evidence field omitted from the answer exactly then.
    ///
    /// THE FIX IS NOT A THIRD PARTY'S TO APPLY, and that is the point of it. The pipe server knows
    /// something it was not using: which of a leg's own steps are MUTATING. `TradingGateway` writes
    /// DISPATCHING before it touches the wire and every state downstream of it — UNKNOWN, RECONCILING
    /// — is reachable only through it, so a record in one of those states is the pipe server's OWN
    /// proof that a mutating step was dispatched. A leg holding that proof and no transport report is
    /// <c>sent-not-confirmed</c>; a leg whose record never got there keeps <c>not-sent</c>. The
    /// obligation is now also STATED on `ITradingConnector` and in `docs/CONTRACTS.md`, so a connector
    /// that opts in gets the sharper answer and one that does not is no longer dangerous.
    ///
    /// The connector below is the verifier's: it implements the public interface, cancels the order
    /// at the broker for real, then loses the acknowledgement, and never calls `TransportLedger`.
    /// </summary>
    [Fact]
    public async Task A_mutating_step_the_connector_never_marked_is_not_reported_as_never_sent()
    {
        var db = TestEnv.NewDb();
        using var _1 = db;
        var fake = new FakeConnector(new FakeBroker(), new FaultProfile { Fill = FillBehaviour.LeaveWorking })
        {
            EmergencyBudget = TimeSpan.FromSeconds(20)
        };
        var conn = new LedgerBlindConnector(fake);
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = fake.Broker.AccountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 200;
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        Assert.True((await client.SendAsync(Buy("blind-working", "ES")).WaitAsync(TimeSpan.FromSeconds(10))).Ok);

        var sweep = (JsonElement)(await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "blind-sweep" })
            .WaitAsync(TimeSpan.FromSeconds(30))).Data!;

        // The premise: the cancel really reached the broker. Without this the word could be right.
        Assert.Equal(1, conn.CancelsThatReachedTheBroker);

        var leg = sweep.GetProperty("outcomes").EnumerateArray().Single();
        var word = leg.GetProperty("outcome").GetString();
        Assert.True(word != "not-sent",
            $"a cancel that reached the broker was reported '{word}' — the assurance was produced by " +
            "an absence of information, from a connector that never called TransportLedger");
        Assert.Equal("sent-not-confirmed", word);
        Assert.Equal(1, sweep.GetProperty("attempted").GetInt32());
        Assert.Equal(0, sweep.GetProperty("not_sent").GetInt32());

        // THE EVIDENCE IS IN THE ANSWER EVEN WHEN THERE IS NONE. The field used to be omitted by the
        // serializer exactly when the leg had no transport report, which is exactly when the reader
        // most needs to know that the word rests on the pipe server's knowledge and not the
        // connector's.
        Assert.True(leg.TryGetProperty("transport", out var transport),
            "the leg carries no `transport` key at all, so its claim arrived without its evidence");
        Assert.Equal(JsonValueKind.Null, transport.ValueKind);
    }

    /// <summary>
    /// THE OBLIGATION IS WRITTEN WHERE A CONNECTOR AUTHOR WILL FIND IT — asserted, because the
    /// finding was that it was not (§9.9: a finding becomes an assertion).
    ///
    /// Verifier round-11 F-2's shape: a safety property was made true of the two implementations in
    /// this tree and left untrue of the thing that DEFINES the obligation. The classification arm is
    /// the guarantee and it is tested above; this is the other half of the same fix, and it is the
    /// half that a future connector author reads. Both places are checked — the interface a third
    /// party implements, and the frozen-contract document — because a rule stated in only one of
    /// them is the same class of miss one level down.
    /// </summary>
    [Fact]
    public void The_ledger_obligation_is_stated_on_the_interface_and_in_the_frozen_contract()
    {
        var contract = File.ReadAllText(Path.Combine(Build.RepoRoot, "src", "TradeAgent.ConnectorSdk", "Contracts.cs"));
        var iface = contract[contract.IndexOf("public interface ITradingConnector", StringComparison.Ordinal)..];
        var doc = contract[..contract.IndexOf("public interface ITradingConnector", StringComparison.Ordinal)];

        // The statement is on ITradingConnector's own doc comment, not merely somewhere in the file.
        Assert.Contains("TransportLedger", doc[doc.LastIndexOf("/// <summary>", StringComparison.Ordinal)..]);
        Assert.Contains("TransportLedger", iface[..iface.IndexOf("ClosePositionAsync", StringComparison.Ordinal)]);

        var frozen = File.ReadAllText(Path.Combine(Build.RepoRoot, "docs", "CONTRACTS.md"));
        var section = frozen[frozen.IndexOf("## `ITradingConnector`", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("\n## ", StringComparison.Ordinal)];
        Assert.Contains("TransportLedger.Attempt()", section);
        foreach (var mutation in new[]
                 {
                     "PlaceOrderAsync", "ModifyOrderAsync", "CancelOrderAsync",
                     "CancelAllOrdersAsync", "ClosePositionAsync"
                 })
            Assert.True(section.Contains(mutation),
                $"the frozen contract's connector section does not name {mutation} as owing the transport ledger");
    }

    /// <summary>
    /// A connector that MUTATES and never writes the ledger — the third party this contract is for.
    /// It is the round-11 verifier's `LedgerBlind`, kept because it is the only fixture in which
    /// `not-sent` can be produced for something that really happened at the broker.
    /// </summary>
    sealed class LedgerBlindConnector(FakeConnector inner) : ITradingConnector
    {
        public int CancelsThatReachedTheBroker;

        public string Id => inner.Id;
        public string DisplayName => "Ledger-blind connector";
        public ConnectorCapabilities Capabilities => inner.Capabilities;
        public TimeSpan WorstCaseOperationPath => inner.WorstCaseOperationPath;
        public TimeSpan EmergencyBudget => inner.EmergencyBudget;

        public event Action<HealthState>? ConnectionChanged { add => inner.ConnectionChanged += value; remove => inner.ConnectionChanged -= value; }
        public event Action<QuoteInfo>? QuoteChanged { add => inner.QuoteChanged += value; remove => inner.QuoteChanged -= value; }
        public event Action<OrderInfo>? OrderChanged { add => inner.OrderChanged += value; remove => inner.OrderChanged -= value; }
        public event Action<ExecutionInfo>? ExecutionReceived { add => inner.ExecutionReceived += value; remove => inner.ExecutionReceived -= value; }
        public event Action<PositionInfo>? PositionChanged { add => inner.PositionChanged += value; remove => inner.PositionChanged -= value; }
        public event Action<AccountInfo>? AccountChanged { add => inner.AccountChanged += value; remove => inner.AccountChanged -= value; }

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
        public Task<IReadOnlyList<string>> CancelAllOrdersAsync(string a, CancellationToken ct = default) => inner.CancelAllOrdersAsync(a, ct);
        public Task<OrderInfo?> ClosePositionAsync(string a, string s, string c, CancellationToken ct = default) => inner.ClosePositionAsync(a, s, c, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();

        /// <summary>The frame went out and the broker acted; the acknowledgement was then lost.</summary>
        public async Task CancelOrderAsync(string connectorOrderId, CancellationToken ct = default)
        {
            await Task.Yield();
            inner.Broker.Cancel(connectorOrderId);            // it REALLY happened at the broker
            Interlocked.Increment(ref CancelsThatReachedTheBroker);
            throw new ConnectorTransportException("the acknowledgement was lost after the cancel was sent");
        }
    }

    /// <summary>
    /// AN ORDER THAT IS RESTING AT THE BROKER IS NOT AN UNKNOWN EITHER.
    ///
    /// The fifth word, and the bounce's own rule is what requires it: <c>sent-not-confirmed</c> is
    /// defined as "UNKNOWN, and the gateway will reconcile it". A `close-all` leg places an
    /// offsetting order, and an offsetting order that rests instead of filling is WORKING — sent,
    /// answered, definitely not unknown, and definitely not done. With four words it fell into
    /// <c>sent-not-confirmed</c> and promised a reconciliation that will never happen.
    /// </summary>
    [Fact]
    public async Task A_close_leg_whose_order_rests_reads_still_working_not_unknown()
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        // Fills on arrival, so there is a position to close.
        Assert.True((await client.SendAsync(Buy("rest-open", "ES")).WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        Assert.Contains(conn.Broker.Positions, p => p.Symbol == "ES" && p.Quantity != 0);

        // And now the broker rests everything, so the offsetting order sits WORKING.
        conn.Faults.Fill = FillBehaviour.LeaveWorking;

        var sweep = (JsonElement)(await client.SendAsync(new IpcRequest { Op = Ops.CloseAll, RequestId = "rest-sweep" })
            .WaitAsync(TimeSpan.FromSeconds(30))).Data!;
        var legs = Outcomes(sweep);

        Assert.All(legs, l => Assert.Contains(l.Outcome, LegVocabulary));
        var resting = Assert.Single(legs, l => l.Outcome == "sent-still-working");
        Assert.Equal(nameof(ExecutionState.WORKING), resting.State);
        Assert.DoesNotContain(legs, l => l.Outcome == "sent-not-confirmed");

        // Nothing closed, one attempted, and the record says the same thing the word does.
        Assert.Equal(0, sweep.GetProperty("closed").GetInt32());
        Assert.Equal(1, sweep.GetProperty("attempted").GetInt32());
        var record = gw.GetRequest(resting.Id)!;
        Assert.Equal(ExecutionState.WORKING, record.State);
        Assert.False(record.NeedsReconciliation,
            "a resting order was flagged for reconciliation — nothing about it is unknown");
    }

    /// <summary>
    /// A LEG PARKED FOR A HUMAN WAS NOT SENT EITHER, AND THE RECORD IT LEAVES IS NOT AN UNKNOWN.
    ///
    /// The other half of the not-sent arm, and the one the exception's own type cannot tell you: here
    /// a record IS written — AWAITING_APPROVAL — and then `PlaceAsync` refuses, because in
    /// LIVE_CONFIRM the AI's order waits for a person. Nothing reached the broker. Classifying by
    /// "the leg threw" would call that sent-not-confirmed and ask the owner to reconcile an order
    /// that is sitting on their own screen waiting for them to press Approve.
    ///
    /// Written because the arm was otherwise uncovered: mutating `CREATED or AWAITING_APPROVAL` to
    /// NotConfirmed survived the whole class.
    /// </summary>
    [Fact]
    public async Task A_close_leg_parked_for_approval_reads_not_sent_and_is_not_counted_as_attempted()
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        // Open a position while orders still go straight through.
        Assert.True((await client.SendAsync(Buy("park-open", "ES")).WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        Assert.Contains(conn.Broker.Positions, p => p.Symbol == "ES" && p.Quantity != 0);
        var ordersBefore = conn.Broker.Orders.Count;

        // And now every AI order waits for a person.
        gw.Update(s => s.Mode = TradingMode.LIVE_CONFIRM);
        gw.ActivateLive(true);

        var sweep = (JsonElement)(await client.SendAsync(new IpcRequest { Op = Ops.CloseAll, RequestId = "park-sweep" })
            .WaitAsync(TimeSpan.FromSeconds(30))).Data!;
        var legs = Outcomes(sweep);

        var parked = Assert.Single(legs, l => l.Outcome == "not-sent");
        Assert.Equal(nameof(ExecutionState.AWAITING_APPROVAL), parked.State);
        Assert.DoesNotContain(legs, l => l.Outcome == "sent-not-confirmed");

        Assert.Equal(0, sweep.GetProperty("attempted").GetInt32());
        Assert.Equal(0, sweep.GetProperty("closed").GetInt32());

        // NOTHING REACHED THE BROKER, which is what the word claims and what makes it checkable.
        Assert.Equal(ordersBefore, conn.Broker.Orders.Count);
        var record = gw.GetRequest(parked.Id)!;
        Assert.Equal(ExecutionState.AWAITING_APPROVAL, record.State);
        Assert.False(record.NeedsReconciliation,
            "an order waiting on the owner's own Approve button was flagged for reconciliation");
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

// =================================================================================================
// U2c-1b item 4 — A REPLAYED SWEEP SENDS NOTHING (Codex C2)
// =================================================================================================

/// <summary>
/// Idempotency by request id used to stop at the operations that write ONE record. `buy`, `cancel`,
/// `modify` and `close` each key an `execution_request` on the caller's id, so a repeated call finds
/// its record and dispatches nothing. A SWEEP wrote no such row: `cancel-all` and `close-all` minted
/// a nonce per CALL and derived their legs from it, so the same request id sent twice was two
/// sweeps, over two different books.
///
/// That is the exact situation a request id exists for — the reply was lost and the caller does not
/// know whether the work happened — and the answer was the worst one available: cancel whatever is
/// on the book NOW, including orders the caller placed after the first attempt.
/// </summary>
public class ReplayedSweepSendsNothingTests
{
    static string NewPipe() => "ta-replay-" + Guid.NewGuid().ToString("n")[..12];

    static IpcRequest Buy(string requestId, string symbol) => new()
    {
        Op = Ops.Buy,
        RequestId = requestId,
        Args = new()
        {
            ["symbol"] = JsonSerializer.SerializeToElement(symbol),
            ["quantity"] = JsonSerializer.SerializeToElement("1"),
            ["limit"] = JsonSerializer.SerializeToElement("1")     // rests as WORKING
        }
    };

    /// <summary>
    /// THE ACCEPTANCE, VERBATIM: sweep order A as `sweep-1`, lose the reply, create order B, repeat
    /// `sweep-1`. B must still be working, and the answer must be the one the first call gave.
    /// </summary>
    [Fact]
    public async Task A_replayed_cancel_all_cancels_nothing_and_returns_the_original_answer()
    {
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        var a = await client.SendAsync(Buy("order-a", "ES")).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(a.Ok, Json.Write(a.Error));

        var first = await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "sweep-1" })
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(first.Ok, Json.Write(first.Error));
        Assert.Equal(1, ((JsonElement)first.Data!).GetProperty("cancelled").GetInt32());

        // The reply is lost. The agent, not knowing, opens another order and then repeats the sweep
        // it never heard back from — with the SAME request id, which is what a request id is for.
        var b = await client.SendAsync(Buy("order-b", "NQ")).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(b.Ok, Json.Write(b.Error));
        var bId = ((JsonElement)b.Data!).GetProperty("connector_order_id").GetString()!;
        Assert.Equal(ExecutionState.WORKING, conn.Broker.Orders.Single(o => o.ConnectorOrderId == bId).State);

        var replay = await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "sweep-1" })
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(replay.Ok, Json.Write(replay.Error));
        // B IS UNTOUCHED. This is the whole test.
        Assert.Equal(ExecutionState.WORKING, conn.Broker.Orders.Single(o => o.ConnectorOrderId == bId).State);
        // ...and the answer is the one the first call gave, not a fresh count of a fresh sweep.
        Assert.Equal(Json.Write(first.Data), Json.Write(replay.Data));
    }

    /// <summary>The same for `close-all`, where a second sweep does not cancel an order but reverses a position.</summary>
    [Fact]
    public async Task A_replayed_close_all_closes_nothing_and_returns_the_original_answer()
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        await gw.PlaceAsync(AgentContext.Operator, "pos-a", TestEnv.Buy("ES", 2m));
        Assert.Single(conn.Broker.Positions);

        var first = await client.SendAsync(new IpcRequest { Op = Ops.CloseAll, RequestId = "flat-1" })
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(first.Ok, Json.Write(first.Error));
        Assert.Empty(conn.Broker.Positions);

        // A new position appears, and the agent repeats the close-all whose reply it lost.
        await gw.PlaceAsync(AgentContext.Operator, "pos-b", TestEnv.Buy("NQ", 3m));

        var replay = await client.SendAsync(new IpcRequest { Op = Ops.CloseAll, RequestId = "flat-1" })
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(replay.Ok, Json.Write(replay.Error));
        Assert.Contains(conn.Broker.Positions, p => p.Symbol == "NQ" && p.Quantity == 3m);
        Assert.Equal(Json.Write(first.Data), Json.Write(replay.Data));
    }

    /// <summary>
    /// IDEMPOTENCY BY REQUEST ID IS FOR EVERY MUTATING OP, NOT ONLY `Place`.
    ///
    /// `Ops.Mutating` is the list the pipe server itself uses, so a new mutating verb appears here
    /// automatically and has to prove the same thing — the way the gap was introduced in the first
    /// place was by adding two ops that decomposed into legs and inheriting nothing.
    /// </summary>
    [Fact]
    public async Task Every_mutating_op_dispatches_once_for_one_request_id()
    {
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        // Every mutating verb, each sent twice under one request id, each with a resting order of
        // its own to act on. What must never change between the two calls is the broker's book.
        var i = 0;
        foreach (var op in Core.Ops.Mutating)
        {
            var tag = $"idem-{i++}";
            var seed = await client.SendAsync(Buy($"{tag}-seed", "ES")).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(seed.Ok, Json.Write(seed.Error));
            var target = ((JsonElement)seed.Data!).GetProperty("connector_order_id").GetString()!;

            var req = new IpcRequest { Op = op, RequestId = tag, Args = new() };
            if (op is Core.Ops.Buy or Core.Ops.Sell)
            {
                req.Args["symbol"] = JsonSerializer.SerializeToElement("ES");
                req.Args["quantity"] = JsonSerializer.SerializeToElement("1");
                req.Args["limit"] = JsonSerializer.SerializeToElement("1");
            }
            if (op is Core.Ops.Cancel or Core.Ops.Modify) req.Args["id"] = JsonSerializer.SerializeToElement(target);
            if (op is Core.Ops.Modify) req.Args["limit"] = JsonSerializer.SerializeToElement("2");
            if (op is Core.Ops.Close) req.Args["symbol"] = JsonSerializer.SerializeToElement("ES");

            var one = await client.SendAsync(req).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(one.Ok, $"{op}: {Json.Write(one.Error)}");
            var book = conn.Broker.Orders.Select(o => $"{o.ConnectorOrderId}:{o.State}:{o.LimitPrice}").ToList();

            var two = await client.SendAsync(req).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(two.Ok, $"{op} replay: {Json.Write(two.Error)}");
            Assert.Equal(book, conn.Broker.Orders.Select(o => $"{o.ConnectorOrderId}:{o.State}:{o.LimitPrice}").ToList());
        }
    }

    /// <summary>
    /// THE OTHER DIRECTION, and it is the one that stops this being a way to break the emergency: a
    /// DIFFERENT request id is a different decision and really does sweep.
    /// </summary>
    [Fact]
    public async Task A_different_request_id_really_does_sweep()
    {
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        Assert.True((await client.SendAsync(Buy("order-c", "ES")).WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        Assert.True((await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "sweep-a" })
            .WaitAsync(TimeSpan.FromSeconds(10))).Ok);

        Assert.True((await client.SendAsync(Buy("order-d", "NQ")).WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        var second = await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "sweep-b" })
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(second.Ok, Json.Write(second.Error));
        Assert.Equal(1, ((JsonElement)second.Data!).GetProperty("cancelled").GetInt32());
        Assert.DoesNotContain(conn.Broker.Orders, o => o.State == ExecutionState.WORKING);
    }
}

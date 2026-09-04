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

        // ---- AND THE HALF THIS TEST WAS MISSING: ONE ANSWER CARRYING A MIX (verifier round-9 F-5).
        //
        // Everything above is satisfied by a sweep that attempted NOTHING — at a second a leg the
        // orders read plus one target resolution is the whole two-second budget, so every leg comes
        // back `not-sent` and `attempted = 0` (measured by the verifier: `cancelled = 0, attempted =
        // 0, not_sent = 5`). The acceptance is "which sent, which confirmed, which not sent", and a
        // reply in which every leg says the same thing never exercises it.
        //
        // The five orders are all still working — nothing above cancelled anything — so the same
        // sweep runs again with the simulator quick and two one-shot faults armed. Legs are issued
        // in waves of four, so the first wave carries the refusal, the lost answer and two ordinary
        // cancellations, and the fifth leg supplies the fourth word: the lost answer settles UNKNOWN
        // and `NeedsReconciliation` refuses everything after it, so that leg is never sent.
        //
        // FIFTY MILLISECONDS, NOT ZERO, AND IT IS LOAD-BEARING. At zero latency the simulator
        // returns without ever awaiting, so `issue()` runs each leg to completion before the loop
        // starts the next one and the wave is serial — the first UNKNOWN then refuses the other
        // three and every word in the answer is the same one. A latency forces the real shape: four
        // legs authorised and in flight together.
        conn.Faults.LatencyMs = 50;
        conn.Faults.RefuseCancel = 1;      // a DEFINITE broker refusal      -> rejected
        conn.Faults.LoseAfterSend = 1;     // sent, no answer came back      -> sent-not-confirmed

        var mixed = await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "f1-five-mixed" })
            .WaitAsync(TimeSpan.FromSeconds(30));
        var mixedData = (JsonElement)mixed.Data!;
        var words = mixedData.GetProperty("outcomes").EnumerateArray()
            .Select(o => o.GetProperty("outcome").GetString()!).ToList();

        Assert.Equal(5, words.Count);
        Assert.All(words, w => Assert.Contains(w, LegVocabulary));
        Assert.Contains("confirmed", words);
        Assert.Contains("rejected", words);
        Assert.Contains("not-sent", words);
        Assert.Contains("sent-not-confirmed", words);

        // The count agrees with the words rather than being kept beside them: `attempted` is every
        // leg that got as far as the wire, which is all of them except the one refused before it.
        Assert.Equal(words.Count(w => w != "not-sent"), mixedData.GetProperty("attempted").GetInt32());
        Assert.Equal(words.Count(w => w == "not-sent"), mixedData.GetProperty("not_sent").GetInt32());
        Assert.False(mixedData.GetProperty("nothing_to_do").GetBoolean());

        // And the words are backed by the records they claim: nothing needs reconciling except the
        // leg that says it does.
        var needing = gw.Requests.NeedingReconciliation().Select(r => r.RequestId).ToHashSet();
        foreach (var leg in mixedData.GetProperty("outcomes").EnumerateArray())
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

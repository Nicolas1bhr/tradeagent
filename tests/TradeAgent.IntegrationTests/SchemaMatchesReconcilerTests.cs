using System.Text.Json;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Gateway;
using TradeAgent.Security;
using TradeAgent.TradeCli;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE SCHEMA IS SERVED SO AN AGENT NEED NOT TRUST A DESCRIPTION THAT DRIFTS — so the description
/// and the code have to be pinned to each other, and nothing but a test does that.
///
/// <c>cancel_and_modify_outcomes</c> told the agent a cancel becomes REJECTED "when it has stayed
/// working and unchanged for a whole grace window". U2c1a deleted the verdict that sentence
/// describes — <c>_settleWatch</c>, <c>HeldStill</c> and <c>SignatureOf</c> are gone — and
/// CONTRACTS.md replaced it with the opposite: a working target "does not become proof by holding
/// still". The reconciler agreed with CONTRACTS.md and the schema kept promising the deleted rule,
/// which is the one failure mode `trade schema` exists to prevent (REVIEW 2026-09-05, finding 8).
///
/// Every test here drives <c>ReconcileByTargetAsync</c> to a real verdict and then reads the schema
/// the agent is actually served, over the real pipe. The words are matched literally on purpose: a
/// paraphrase that no longer says what the code does is exactly what this is here to catch.
/// </summary>
public class SchemaMatchesReconcilerTests(ITestOutputHelper log)
{
    static string NewPipe() => "ta-schema-" + Guid.NewGuid().ToString("n")[..12];

    /// <summary>The sentence the agent is served, fetched the way an agent fetches it.</summary>
    static async Task<string> CancelAndModifyOutcomes(TradingGateway gw)
    {
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);
        var schema = await client.SendAsync(new IpcRequest { Op = Ops.Schema, Session = "agent-1" })
            .WaitAsync(TimeSpan.FromSeconds(10));
        return JsonSerializer.SerializeToElement(schema.Data)
            .GetProperty("cancel_and_modify_outcomes").GetString()!;
    }

    /// <summary>A working order, then a cancel whose reply is lost. The premise of every case below.</summary>
    static async Task<(TradingGateway Gw, FakeConnector Conn, string Target)> ACancelNobodyAnswered(
        TradingGateway gw, FakeConnector conn, string prefix)
    {
        conn.Faults.Fill = FillBehaviour.LeaveWorking;
        var placed = await gw.PlaceAsync(new AgentContext("a"), prefix + "-open", TestEnv.Buy());
        var target = placed.ConnectorOrderId!;
        conn.Faults.LoseAfterSend = 1;
        var cancel = await gw.CancelAsync(new AgentContext("a"), prefix + "-cancel", target);
        Assert.Equal(ExecutionState.UNKNOWN, cancel.State);
        return (gw, conn, target);
    }

    /// <summary>
    /// The verdict the schema promised and the reconciler never reaches.
    ///
    /// The grace window is zero and the pass runs twice, so "a whole grace window" of holding still
    /// has demonstrably elapsed. The record stays RECONCILING and unconfirmed, which is the rule
    /// CONTRACTS.md states — and the schema must not tell the agent otherwise, because an agent that
    /// believes this waits for a REJECTED that is never coming and reads a paused gateway as a bug.
    /// </summary>
    [Fact]
    public async Task A_target_that_only_holds_still_settles_nothing_and_the_schema_does_not_promise_it_will()
    {
        var (gw, conn, db) = await TestEnv.Ready(options: new GatewayOptions { AbsenceGrace = TimeSpan.Zero });
        using var dbh = db;
        await ACancelNobodyAnswered(gw, conn, "held");

        var first = await gw.ReconcileAsync();
        var second = await gw.ReconcileAsync();
        var record = gw.GetRequest("held-cancel")!;
        var text = await CancelAndModifyOutcomes(gw);

        log.WriteLine($"pass 1 : resolved={first.Resolved} inconclusive={first.Inconclusive} — {string.Join("; ", first.Details)}");
        log.WriteLine($"pass 2 : resolved={second.Resolved} inconclusive={second.Inconclusive}");
        log.WriteLine($"cancel record : {record.State}, needs_reconciliation={record.NeedsReconciliation}");
        log.WriteLine($"target        : {conn.Broker.Orders.Single().State}");
        log.WriteLine($"schema        : {text}");

        // What the reconciler does: nothing, twice, on a target that is merely working.
        Assert.Equal(ExecutionState.WORKING, conn.Broker.Orders.Single().State);
        Assert.Equal(ExecutionState.RECONCILING, record.State);
        Assert.True(record.NeedsReconciliation);
        Assert.Equal(0, second.Resolved);

        // What the schema may therefore say about it.
        Assert.DoesNotContain("stayed working and unchanged for a whole grace window", text);
        Assert.DoesNotContain("holding still", SettlingClause(text));
        Assert.Contains("does not become proof by holding still", text);
    }

    /// <summary>
    /// The three things that DO settle a cancel, each driven to its verdict, each named in the
    /// schema. Table-driven so a verdict the reconciler gains or loses shows up here as a row that
    /// no longer matches, rather than as a sentence nobody re-read.
    /// </summary>
    [Fact]
    public async Task Every_verdict_the_reconciler_reaches_for_a_cancel_is_named_in_the_schema()
    {
        // The platform has the order cancelled — the cancel landed, the acknowledgement was lost.
        var (gw1, conn1, db1) = await TestEnv.Ready(options: new GatewayOptions { AbsenceGrace = TimeSpan.Zero });
        using var dbh1 = db1;
        var (_, _, cancelled) = await ACancelNobodyAnswered(gw1, conn1, "gone");
        Assert.True(conn1.Broker.Cancel(cancelled));
        var landedPass = await gw1.ReconcileAsync();
        var landed = gw1.GetRequest("gone-cancel")!;

        // The order finished some other way. The cancellation did not take effect, definitely.
        var (gw2, conn2, db2) = await TestEnv.Ready(options: new GatewayOptions { AbsenceGrace = TimeSpan.Zero });
        using var dbh2 = db2;
        var (_, _, filled) = await ACancelNobodyAnswered(gw2, conn2, "fill");
        Assert.NotNull(conn2.Broker.FillWorking(filled));
        var refusedPass = await gw2.ReconcileAsync();
        var refused = gw2.GetRequest("fill-cancel")!;

        // Still working. Not a verdict, however long it is watched.
        var (gw3, conn3, db3) = await TestEnv.Ready(options: new GatewayOptions { AbsenceGrace = TimeSpan.Zero });
        using var dbh3 = db3;
        await ACancelNobodyAnswered(gw3, conn3, "work");
        var openPass = await gw3.ReconcileAsync();
        var open = gw3.GetRequest("work-cancel")!;

        var text = await CancelAndModifyOutcomes(gw3);
        log.WriteLine($"target CANCELLED -> {landed.State} (resolved={landedPass.Resolved})");
        log.WriteLine($"target FILLED    -> {refused.State} (resolved={refusedPass.Resolved})");
        log.WriteLine($"target WORKING   -> {open.State} (resolved={openPass.Resolved}, inconclusive={openPass.Inconclusive})");
        log.WriteLine($"schema           : {text}");

        Assert.Equal(ExecutionState.CANCELLED, landed.State);
        Assert.Equal(ExecutionState.REJECTED, refused.State);
        Assert.Equal(ExecutionState.RECONCILING, open.State);

        // CANCELLED, from the platform holding the order cancelled.
        Assert.Contains("CANCELLED", text);
        Assert.Contains("the platform has that order cancelled", text);
        // REJECTED, from the target having finished some other way, or a definite refusal, or the
        // owner. Exactly the set in CONTRACTS.md and in ReconcileByTargetAsync — no fourth way in.
        Assert.Contains("REJECTED", text);
        Assert.Contains("that order has finished some other way", text);
        Assert.Contains("the broker definitively refused the cancellation", text);
        Assert.Contains("the account owner settles it in TradeAgent", text);
        // And working is not one of them.
        Assert.Contains("still working", text);
        Assert.Contains("settles nothing", text);
    }

    /// <summary>
    /// The other sentence U2c1a made false, in the same field: the modify price rule.
    ///
    /// The schema said "a price within one tick of the request on the instrument's grid counts".
    /// <c>PriceCarries</c> accepts exactly <c>floor(want/tick)*tick</c> and <c>ceil(want/tick)*tick</c>
    /// — the two grid points the request falls between — which for a request already ON the grid is
    /// the one price and not its neighbours. A one-tick band is wider than that, in both directions,
    /// and the difference is an agent told its stop had moved when it had not.
    ///
    /// PINNED BY TEXT, NOT BY BEHAVIOUR, and that is a real limitation. Reaching the band through
    /// the gateway needs a platform that answers with a price it was not asked for; both the
    /// simulator and the recording connector return the asked price exactly, and the connectors are
    /// not this unit's to change. The floor/ceil arithmetic quoted above is the whole of the rule.
    /// </summary>
    [Fact]
    public async Task The_modify_sentence_states_the_grid_rule_and_not_the_band_that_was_deleted()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var dbh = db;
        var text = await CancelAndModifyOutcomes(gw);
        log.WriteLine(text);

        Assert.DoesNotContain("within one tick", text);
        Assert.Contains("the two grid points the request falls between", text);
    }

    /// <summary>
    /// The third sentence the sweep turned up, one field over.
    ///
    /// <c>unknown_state_meaning</c> lists what becomes UNKNOWN and ends "any failure after the order
    /// was sent that is not a definite refusal". U2c1c gave the connectors a way to PROVE a mutation
    /// never left the process, and the gateway settles that CANCELLED with no flag and no pause. The
    /// old sentence is not false — a proven-unsent failure is not "after the order was sent" — but it
    /// is the whole of what the agent is told about failures, and an agent reading it has no way to
    /// tell the two apart. It matters because the two prescribe opposite actions: UNKNOWN means wait
    /// and never re-send, and this means nothing happened, so ask again.
    /// </summary>
    [Fact]
    public async Task A_mutation_the_connector_proved_it_never_sent_is_not_UNKNOWN_and_the_schema_says_so()
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var dbh = db;
        conn.Faults.Fill = FillBehaviour.LeaveWorking;
        var placed = await gw.PlaceAsync(new AgentContext("a"), "proof-open", TestEnv.Buy());

        conn.Faults.RefuseBeforeSend = 1;      // the connector proves the frame never left
        var cancel = await gw.CancelAsync(new AgentContext("a"), "proof-cancel", placed.ConnectorOrderId!);

        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);
        var schema = await client.SendAsync(new IpcRequest { Op = Ops.Schema, Session = "agent-1" })
            .WaitAsync(TimeSpan.FromSeconds(10));
        var text = JsonSerializer.SerializeToElement(schema.Data)
            .GetProperty("unknown_state_meaning").GetString()!;

        log.WriteLine($"cancel : {cancel.State}, needs_reconciliation={cancel.NeedsReconciliation}");
        log.WriteLine($"trading still allowed : {gw.TryAuthorizeExecution(new AgentContext("a"), out _)}");
        log.WriteLine($"schema : {text}");

        Assert.Equal(ExecutionState.CANCELLED, cancel.State);
        Assert.False(cancel.NeedsReconciliation);
        Assert.True(gw.TryAuthorizeExecution(new AgentContext("a"), out _));
        Assert.Contains("proves it never reached the platform", text);
    }

    /// <summary>
    /// The half of the sentence that says what SETTLES a cancel, so "holding still" may still be
    /// mentioned in the half that says what does not.
    /// </summary>
    static string SettlingClause(string text)
    {
        var end = text.IndexOf("does not become proof", StringComparison.Ordinal);
        return end < 0 ? text : text[..end];
    }
}

using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// AN END WHOSE CLOSE A GATE REFUSED STAYS OWED AND GOES OUT AGAIN, AND A DISPATCH STOPPED BETWEEN ITS RECORD AND
/// THE WIRE IS OVER (<c>U-runner-exit-hygiene-a</c>).
///
/// <para>Same harness as the rest of this class: the owner's grant, the app's own allocation and deployment,
/// the run's own filled entry, every order through <see cref="TradingGateway.PlaceAsync"/> over
/// <see cref="RecordingConnector"/> and the built-in simulator. A gate is staged with
/// <see cref="TradingGateway.InstallInProgress"/> — the update window, refused before anything is written —
/// or with the instrument allowlist, refused inside the order path; a process that died is a call held on the
/// connector's seam, and its restart a second gateway over the same database. No venue is reached.</para>
/// </summary>
public partial class PaperDeploymentTests
{
    static decimal Held(RecordingConnector conn) =>
        conn.Broker.Positions.FirstOrDefault(p => p.Symbol == "BTCUSDT")?.Quantity ?? 0m;

    static List<DeploymentOpRow> Flattens(TradingGateway gw, string deployment) =>
        [.. gw.Deployments.OpsOf(deployment).Where(o => o.Kind == DeploymentOpKind.Flatten)];

    /// <summary>One reconcile pass at <paramref name="at"/>, on the gateway's own clock as well.</summary>
    static async Task PassAt(TradingGateway gw, TestClock clock, DateTimeOffset at)
    {
        clock.At = at;
        await gw.ReconcilePaperDeploymentsAsync(at);
    }

    /// <summary>
    /// (a) AN END WHOSE CLOSE THE UPDATE WINDOW REFUSED IS SENT AGAIN ONCE THE WINDOW LIFTS, ONCE, AND THE RUN
    /// HOLDS ITS ENVELOPE'S SLOT UNTIL IT IS FLAT.
    ///
    /// <para>The END cancels what works, closes through <c>CloseAsync</c> and writes <c>ended</c> whatever came
    /// back. The update window refuses the close before anything is written: the flatten is <c>refused</c> over
    /// no request row and the owner's long is still open. It used to read reconciled — every operation settled —
    /// so its slot freed, a replacement could start over the open position at once, the status dropped the run,
    /// and nothing ever sent the close again.</para>
    ///
    /// <para><b>RED on the base</b>: a replacement at once, no second flatten, the position 1. <b>Mutant (i)</b> —
    /// the slot reading an ended run accounted for once every operation settles, as it did — goes red here: a
    /// replacement while the account holds 1.</para>
    /// </summary>
    [Fact]
    public async Task An_end_whose_close_a_gate_refused_is_sent_again_and_holds_its_slot_until_flat()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;

        await Allocated(gw, db);
        Assert.Equal(1, gw.StartPaperDeploymentsDue(At));
        var deployment = gw.Deployments.Open().Single();
        await SeedAPosition(gw, conn, deployment);

        // THE END, WITH THE UPDATE WINDOW OPEN.
        gw.InstallInProgress = () => true;
        await gw.EndPaperDeploymentAsync(deployment.Id, "test: the owner stopped it");
        var refused = Assert.Single(Flattens(gw, deployment.Id));

        clock.At = At.AddSeconds(10);
        var heldWhileOwed = Held(conn);
        var whileOwed = gw.StartPaperDeploymentsDue(clock.At);
        var line = gw.DeploymentReadings().Single(d => d.Id == deployment.Id).Line;
        var listed = (await gw.StatusAsync()).Deployments?.SingleOrDefault(d => d.Id == deployment.Id);

        // WHILE THE GATE STILL REFUSES, A PASS WRITES NOTHING: in the END's own minute, and in the next.
        await PassAt(gw, clock, At.AddSeconds(30));
        await PassAt(gw, clock, At.AddMinutes(1).AddSeconds(5));
        var whileHeld = gw.Deployments.OpsOf(deployment.Id).Select(o => o.Kind).ToList();

        // THE WINDOW LIFTS: two passes in this minute, one in the next.
        gw.InstallInProgress = null;
        await PassAt(gw, clock, At.AddMinutes(1).AddSeconds(10));
        await PassAt(gw, clock, At.AddMinutes(1).AddSeconds(40));
        await PassAt(gw, clock, At.AddMinutes(2).AddSeconds(5));

        var flattens = Flattens(gw, deployment.Id);
        clock.At = At.AddMinutes(2).AddSeconds(10);
        var afterFlat = gw.StartPaperDeploymentsDue(clock.At);
        var listedAfter = (await gw.StatusAsync()).Deployments?.SingleOrDefault(d => d.Id == deployment.Id);

        log.WriteLine($"the END's close      : {refused.State} — {refused.Answer}");
        log.WriteLine($"its request row      : {gw.GetRequest(refused.RequestId)?.State.ToString() ?? "none"}");
        log.WriteLine($"while owed           : started {whileOwed} with the account holding {heldWhileOwed}; "
                      + $"ops while the gate held {string.Join(", ", whileHeld)}");
        log.WriteLine($"line                 : {line}");
        log.WriteLine($"status               : {listed?.State ?? "not listed"} — {listed?.Why ?? "-"}");
        foreach (var f in flattens)
            log.WriteLine($"flatten              : {f.RequestId} {f.State} — {f.Answer} "
                          + $"({gw.GetRequest(f.RequestId)?.State.ToString() ?? "no row"})");
        log.WriteLine($"position             : {Held(conn)}");
        log.WriteLine($"after a flat end     : started {afterFlat}; status {listedAfter?.State ?? "not listed"}");

        // THE PREMISE: the update window refused the END's close before anything was written.
        Assert.Equal(DeploymentOpState.Refused, refused.State);
        Assert.Null(gw.GetRequest(refused.RequestId));
        Assert.Contains(ErrorCode.UPDATE_INSTALL_IN_PROGRESS.ToString(), refused.Answer);
        Assert.Equal(1m, heldWhileOwed);

        // NO REPLACEMENT WHILE THE CLOSE IS OWED, AND EVERY SURFACE SAYS SO IN WORDS.
        Assert.Equal(0, whileOwed);
        Assert.Contains("ENDED", line, StringComparison.Ordinal);
        Assert.Contains("NOT closed", line, StringComparison.Ordinal);
        Assert.Contains(ErrorCode.UPDATE_INSTALL_IN_PROGRESS.ToString(), line, StringComparison.Ordinal);
        Assert.Contains("each minute", line, StringComparison.Ordinal);
        Assert.Contains("no replacement", line, StringComparison.Ordinal);
        Assert.NotNull(listed);
        Assert.Contains("NOT closed", listed!.Why, StringComparison.Ordinal);

        // NOTHING WRITTEN WHILE THE GATE REFUSED IT: the run's own entry, and the END's refused close.
        Assert.Equal([DeploymentOpKind.Entry, DeploymentOpKind.Flatten], whileHeld);

        // EXACTLY ONE NEW FLATTEN, UNDER ITS OWN ID, FILLED — AND THE ACCOUNT FLAT.
        Assert.Equal(2, flattens.Count);
        Assert.NotEqual(refused.RequestId, flattens[1].RequestId);
        Assert.Equal(DeploymentOpState.Resolved, flattens[1].State);
        Assert.Equal(ExecutionState.FILLED, gw.GetRequest(flattens[1].RequestId)!.State);
        Assert.Equal(At.AddMinutes(1), flattens[1].BarOpenTime);
        Assert.Equal(0m, Held(conn));

        // AND ONLY THEN IS THE SLOT FREE, and the run off the status.
        Assert.Equal(1, afterFlat);
        Assert.Null(listedAfter);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// (b) A DISPATCH STOPPED BETWEEN ITS RECORD AND THE WIRE IS OVER, AND A LATE DISPATCHER SENDS NOTHING.
    ///
    /// <para>The END's close is held on a position read — the process that died. Two places: the stale-close read,
    /// once its order row is <c>CREATED</c> (the write-ahead row is on disk, <c>DISPATCHING</c> is not); and
    /// <c>CloseAsync</c>'s own read, before any order row exists. A second gateway over the same database is the
    /// restart. Inside <c>DispatchStrandedAfter</c> it settles nothing — a dispatcher may still be on its way —
    /// and past it the STORE settles it: the row <c>CANCELLED</c> by compare-and-swap (or written under the id
    /// first and cancelled, when there was none), the operation <c>refused</c>, the cursor past its bar. The held
    /// call is then released, meets the store's answer — its own compare-and-swap lost, or the id already taken —
    /// and nothing reaches either wire.</para>
    ///
    /// <para><b>RED on the base</b>: over <c>CREATED</c> the operation stays <c>dispatched</c>, the cursor null
    /// on every pass, and the released call sends one order; with no row the operation is refused at once
    /// although its dispatcher is still on its way, and the released call sends one order under it.
    /// <b>Mutant (ii)</b> — the settle refuses the operation and leaves the row <c>CREATED</c> — goes red: the
    /// released call sends one order under an operation reading <c>refused</c>.</para>
    /// </summary>
    [Theory]
    [InlineData("at its stale-close read")]
    [InlineData("before its order row")]
    public async Task A_dispatch_stopped_between_its_record_and_the_wire_is_over_and_a_late_dispatcher_sends_nothing(
        string stopped)
    {
        var (gw, conn, db, _) = await Ready();
        using var _1 = db;

        await Allocated(gw, db);
        Assert.Equal(1, gw.StartPaperDeploymentsDue(At));
        var deployment = gw.Deployments.Open().Single();
        await SeedAPosition(gw, conn, deployment);
        var placesBefore = conn.Places;

        // THE PROCESS THAT DIED: the END's close, held on a position read once — on the stale-close read when its
        // order row is CREATED, or on CloseAsync's own read while there is no order row yet.
        var release = new TaskCompletionSource();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        conn.Seam = async kind =>
        {
            if (kind != RecordingConnector.HeldCall.Positions) return;
            if (Flattens(gw, deployment.Id).SingleOrDefault() is not { } op) return;
            var order = gw.GetRequest(op.RequestId);
            var here = stopped == "before its order row" ? order is null : order is { State: ExecutionState.CREATED };
            if (!here || Interlocked.Exchange(ref held, 1) == 1) return;
            reached.TrySetResult();
            await release.Task;
        };
        var ending = gw.EndPaperDeploymentAsync(deployment.Id, "test: the owner stopped it");
        await reached.Task;
        var flatten = Assert.Single(Flattens(gw, deployment.Id));
        var heldOn = gw.GetRequest(flatten.RequestId)?.State.ToString() ?? "no order row";

        // THE RESTART: another gateway over the same database and the same book, with its own wire and clock.
        var second = new RecordingConnector(new FakeConnector(conn.Broker));
        var (restarted, _, _, clock) = await Ready(db: db, conn: second);
        var bound = restarted.DispatchStrandedAfter;

        await PassAt(restarted, clock, At + bound - TimeSpan.FromSeconds(5));
        var inside = restarted.Deployments.OpById(flatten.RequestId)!;
        var cursorInside = restarted.Deployments.ById(deployment.Id)!.CursorOpenTime;

        await PassAt(restarted, clock, At + bound + TimeSpan.FromSeconds(1));
        var past = restarted.Deployments.OpById(flatten.RequestId)!;
        var row = restarted.GetRequest(flatten.RequestId);
        var cursor = restarted.Deployments.ById(deployment.Id)!.CursorOpenTime;

        // THE LATE DISPATCHER: the held call released, into the store's answer.
        release.SetResult();
        await ending;
        var afterRelease = restarted.Deployments.OpById(flatten.RequestId)!;
        var rowAfter = restarted.GetRequest(flatten.RequestId);
        var owed = restarted.DeploymentReadings().Single(d => d.Id == deployment.Id).CloseOwed;

        log.WriteLine($"held                 : {stopped} — order row {heldOn}");
        log.WriteLine($"inside the bound     : {inside.State}, cursor {cursorInside?.ToString("u") ?? "none"} ({bound.TotalSeconds:0}s)");
        log.WriteLine($"past the bound       : {past.State} — {past.Answer}");
        log.WriteLine($"its order row        : {row?.State.ToString() ?? "none"}, dispatched_at {row?.DispatchedAt?.ToString("u") ?? "none"}");
        log.WriteLine($"cursor               : {cursor?.ToString("u") ?? "none"} (the bar {flatten.BarOpenTime:u})");
        log.WriteLine($"after the release    : {afterRelease.State}; row {rowAfter?.State.ToString() ?? "none"}");
        log.WriteLine($"orders at the wire   : {conn.Places - placesBefore} at the first, {second.Places} at the second");
        log.WriteLine($"the END's close      : {(owed is null ? "not owed" : $"owed — {owed}")}");

        // INSIDE THE BOUND NOTHING IS SETTLED: a dispatcher may still be on its way.
        Assert.Equal(DeploymentOpState.Dispatched, inside.State);
        Assert.Null(cursorInside);

        // NOTHING REACHES EITHER WIRE.
        Assert.Equal(0, conn.Places - placesBefore);
        Assert.Equal(0, second.Places);

        // PAST IT THE STORE SETTLED IT: the row cancelled without ever reaching the wire, the operation refused,
        // the cursor past its bar — and the late dispatcher changed none of it.
        Assert.Equal(DeploymentOpState.Refused, past.State);
        Assert.Equal(ExecutionState.CANCELLED, row!.State);
        Assert.Null(row.DispatchedAt);
        Assert.Equal(flatten.BarOpenTime, cursor);
        Assert.Equal(DeploymentOpState.Refused, afterRelease.State);
        Assert.Equal(ExecutionState.CANCELLED, rowAfter!.State);
        Assert.Null(rowAfter.DispatchedAt);

        // AND THE END'S CLOSE IS OWED, to go out again under a new operation (item 1).
        Assert.NotNull(owed);
        Assert.Equal(1m, Held(conn));
        await restarted.DisposeAsync();
        await gw.DisposeAsync();
    }

    /// <summary>
    /// (d) AN OWED CLOSE HOLDS ITS ACCOUNT'S INSTRUMENT ACROSS A NEW GRANT, AND GOES OUT BESIDE ANOTHER RUN ON THE SAME
    /// POSITION, SELLING ONLY ITS OWN (<c>U-paper-books</c> replaced the test that made it wait).
    ///
    /// <para>The END's close is refused and owed; the owner then withdraws that grant and grants a larger one on the same
    /// account and instrument, two runs at a time, and a second version is allocated beside the first. The owed run is not
    /// in the new grant, but the position it has not closed is on the same account and instrument: it holds one of the two
    /// slots, so one run starts, not two. That run enters and holds its own 1 beside the owed run's 1. Once the gate lifts
    /// the owed close goes out on the next minute although the other run is live — a run's close is of its OWN book, so
    /// there is nothing of the other run's for it to sell — and it sells the owed run's 1 alone; the account then holds the
    /// other run's 1, which that run's own END sells.</para>
    ///
    /// <para><b>RED on the base</b>: the owed close waits while the other run is live ("held while run …"), because a close
    /// was the account's whole position. <b>Mutant</b>: the slot counted per grant, as before — two runs start.</para>
    /// </summary>
    [Fact]
    public async Task An_owed_close_holds_its_instrument_across_a_new_grant_and_goes_out_beside_another_run_selling_its_own()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;

        var (first, _) = await Allocated(gw, db);
        Assert.Equal(1, gw.StartPaperDeploymentsDue(At));
        var owed = gw.Deployments.Open().Single();
        await SeedAPosition(gw, conn, owed);

        gw.InstallInProgress = () => true;
        await gw.EndPaperDeploymentAsync(owed.Id, "test: the owner stopped it");
        Assert.True(gw.WithdrawPaperEnvelope(first.Id, At.AddSeconds(5)).Ok);

        // A LARGER GRANT ON THE SAME ACCOUNT AND INSTRUMENT, and a second version allocated beside the first.
        var larger = gw.Envelopes.Grant(new PaperEnvelopeRow(
            "", gw.Connector.Id, first.AccountId, first.Symbol, first.Currency, first.MaxQuantity, null,
            2, At.AddSeconds(10), At.AddDays(30), "test: two runs at a time", null));
        Assert.True(larger.Ok, larger.Why);
        Judged(db, PromotionVerdict.PaperEligible, threshold: 104);
        Assert.Equal(2, gw.AllocatePaperDue(At.AddSeconds(15)));

        // ONE OF THE TWO SLOTS IS THE OWED RUN'S, so one run starts, not two.
        clock.At = At.AddSeconds(20);
        var started = gw.StartPaperDeploymentsDue(clock.At);
        log.WriteLine($"started under the larger grant : {started}");
        Assert.Equal(1, started);
        var other = Assert.Single(gw.Deployments.Open());

        // THE GATE LIFTS, AND THE OTHER RUN ENTERS: the account holds the two runs' 1 each.
        gw.InstallInProgress = null;
        await SeedAPosition(gw, conn, other);
        var bothHeld = Held(conn);
        var placesBefore = conn.Places;

        // THE NEXT MINUTE'S PASS, WITH THE OTHER RUN LIVE: the owed close goes out, of the owed run's 1.
        await PassAt(gw, clock, At.AddMinutes(1).AddSeconds(5));
        var flattens = Flattens(gw, owed.Id);
        var line = gw.DeploymentReadings().Single(d => d.Id == owed.Id).Line;
        var heldAfterTheOwedClose = Held(conn);

        // THE OTHER RUN'S OWN END SELLS ITS OWN 1.
        clock.At = At.AddMinutes(1).AddSeconds(30);
        await gw.EndPaperDeploymentAsync(other.Id, "test: the owner stopped the other run");
        var others = Flattens(gw, other.Id);

        foreach (var f in flattens)
            log.WriteLine($"owed flatten         : {f.RequestId} {f.State} — {f.Answer} "
                          + $"({gw.GetRequest(f.RequestId)?.ParametersJson ?? "no row"})");
        log.WriteLine($"line                 : {line}");
        log.WriteLine($"position             : {bothHeld} with both, {heldAfterTheOwedClose} after the owed close, {Held(conn)} at the end");
        log.WriteLine($"closes at the wire   : {conn.Places - placesBefore}");

        Assert.Equal(2m, bothHeld);

        // EXACTLY ONE NEW FLATTEN OF THE OWED RUN, ON THE NEXT MINUTE, FILLED — A SELL OF ITS OWN 1, NOTHING OF THE OTHER'S.
        Assert.Equal(2, flattens.Count);
        Assert.Equal(At.AddMinutes(1), flattens[1].BarOpenTime);
        Assert.Equal(DeploymentOpState.Resolved, flattens[1].State);
        var sold = gw.GetRequest(flattens[1].RequestId)!;
        Assert.Equal(ExecutionState.FILLED, sold.State);
        Assert.Equal(1m, Json.Read<PlaceIntent>(sold.ParametersJson)!.Quantity);
        Assert.Equal(1m, heldAfterTheOwedClose);
        Assert.DoesNotContain("NOT closed", line, StringComparison.Ordinal);
        Assert.Null(gw.DeploymentReadings().Single(d => d.Id == owed.Id).CloseOwed);

        // AND THE OTHER RUN'S END CLOSED THE OTHER RUN'S 1: two closes at the wire in all, and the account flat.
        Assert.Equal(ExecutionState.FILLED, gw.GetRequest(Assert.Single(others).RequestId)!.State);
        Assert.Equal(2, conn.Places - placesBefore);
        Assert.Equal(0m, Held(conn));
        await gw.DisposeAsync();
    }

    /// <summary>
    /// (c) AN OWED CLOSE THAT A GATE INSIDE THE ORDER PATH REFUSES IS ASKED AGAIN AT MOST ONCE A MINUTE.
    ///
    /// <para>The instrument allowlist is not a question <c>TryAuthorizeExecution</c> asks: it refuses inside
    /// <c>PlaceAsync</c>, so each attempt is written down and refused. The END's own attempt spends its minute;
    /// the next minute gets one more, and a pass later in that minute none — however often the background loop
    /// comes round. Once the instrument is allowed again, the next minute's close goes out and fills.</para>
    ///
    /// <para><b>RED on the base</b>: nothing is ever sent again and the position stays 1.</para>
    /// </summary>
    [Fact]
    public async Task An_owed_close_a_gate_inside_the_order_path_refuses_is_asked_again_at_most_once_a_minute()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;

        await Allocated(gw, db);
        Assert.Equal(1, gw.StartPaperDeploymentsDue(At));
        var deployment = gw.Deployments.Open().Single();
        await SeedAPosition(gw, conn, deployment);

        gw.Update(s => s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments]);
        await gw.EndPaperDeploymentAsync(deployment.Id, "test: the owner stopped it");

        await PassAt(gw, clock, At.AddSeconds(30));                     // the END's own minute
        var inTheEndsMinute = Flattens(gw, deployment.Id).Count;
        await PassAt(gw, clock, At.AddMinutes(1).AddSeconds(5));        // the next: one more attempt
        await PassAt(gw, clock, At.AddMinutes(1).AddSeconds(35));       // and none again in it
        var inTheNextMinute = Flattens(gw, deployment.Id);
        var line = gw.DeploymentReadings().Single(d => d.Id == deployment.Id).Line;

        gw.Update(s => s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments, "BTCUSDT"]);
        await PassAt(gw, clock, At.AddMinutes(1).AddSeconds(50));       // still that minute
        var sameMinuteAllowed = Flattens(gw, deployment.Id).Count;
        await PassAt(gw, clock, At.AddMinutes(2).AddSeconds(5));
        await PassAt(gw, clock, At.AddMinutes(2).AddSeconds(35));
        var flattens = Flattens(gw, deployment.Id);

        foreach (var f in flattens)
            log.WriteLine($"flatten              : {f.RequestId} bar {f.BarOpenTime:HH:mm} {f.State} — {f.Answer}");
        log.WriteLine($"counts               : {inTheEndsMinute} in the END's minute, {inTheNextMinute.Count} in "
                      + $"the next, {sameMinuteAllowed} once allowed in that minute, {flattens.Count} after");
        log.WriteLine($"line                 : {line}");
        log.WriteLine($"position             : {Held(conn)}");

        Assert.Equal(1, inTheEndsMinute);
        Assert.Equal(2, inTheNextMinute.Count);
        Assert.Equal(DeploymentOpState.Refused, inTheNextMinute[1].State);
        Assert.Contains(ErrorCode.RISK_LIMIT_EXCEEDED.ToString(), inTheNextMinute[1].Answer);
        Assert.Contains(ErrorCode.RISK_LIMIT_EXCEEDED.ToString(), line, StringComparison.Ordinal);
        Assert.Equal(2, sameMinuteAllowed);

        Assert.Equal(3, flattens.Count);
        Assert.Equal(DeploymentOpState.Resolved, flattens[2].State);
        Assert.Equal(ExecutionState.FILLED, gw.GetRequest(flattens[2].RequestId)!.State);
        Assert.Equal(0m, Held(conn));
        await gw.DisposeAsync();
    }
}

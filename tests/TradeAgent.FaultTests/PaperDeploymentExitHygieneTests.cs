using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// AN END WHOSE CLOSE A GATE REFUSED STAYS OWED AND GOES OUT AGAIN (<c>U-runner-exit-hygiene-a</c>).
///
/// <para>Same harness as the rest of this class: the owner's grant, the app's own allocation and deployment,
/// a filled long the owner placed by hand, every order through <see cref="TradingGateway.PlaceAsync"/> over
/// <see cref="RecordingConnector"/> and the built-in simulator. A gate is staged with
/// <see cref="TradingGateway.InstallInProgress"/> — the update window, refused before anything is written —
/// or with the instrument allowlist, refused inside the order path. No venue is reached.</para>
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
        await SeedAPosition(gw, conn);

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
        var whileHeld = gw.Deployments.OpsOf(deployment.Id).Count;

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
                      + $"ops while the gate held {whileHeld}");
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

        // NOTHING WRITTEN WHILE THE GATE REFUSED IT.
        Assert.Equal(1, whileHeld);

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
    /// (d) AN OWED CLOSE HOLDS ITS ACCOUNT'S INSTRUMENT ACROSS A NEW GRANT, AND WAITS WHILE ANOTHER RUN TRADES THE
    /// SAME POSITION.
    ///
    /// <para>The END's close is refused and owed; the owner then withdraws that grant and grants a larger one on
    /// the same account and instrument, two runs at a time, and a second version is allocated beside the first.
    /// The owed run is not in the new grant, but the position it has not closed is on the same account and
    /// instrument: it holds one of the two slots, so one run starts, not two. And once the gate lifts its close
    /// is NOT sent while that run is live — a close is of the account's whole position, and sent now it would
    /// close the other run's position under it. That run's own END closes the position; the owed close then
    /// finds the book flat and resolves, so exactly one close reaches the wire.</para>
    ///
    /// <para><b>Mutants</b>: the slot counted per grant, as before — two runs start; the owed close sent while
    /// the other run is live — a second flatten goes out under it.</para>
    /// </summary>
    [Fact]
    public async Task An_owed_close_holds_its_instrument_across_a_new_grant_and_waits_while_another_run_trades_it()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;

        var (first, _) = await Allocated(gw, db);
        Assert.Equal(1, gw.StartPaperDeploymentsDue(At));
        var owed = gw.Deployments.Open().Single();
        await SeedAPosition(gw, conn);

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
        var placesBefore = conn.Places;

        // THE GATE LIFTS WHILE THE OTHER RUN IS LIVE.
        gw.InstallInProgress = null;
        await PassAt(gw, clock, At.AddMinutes(1).AddSeconds(5));
        var whileOtherLive = Flattens(gw, owed.Id).Count;
        var line = gw.DeploymentReadings().Single(d => d.Id == owed.Id).Line;

        // THE OTHER RUN ENDS, AND ITS OWN END CLOSES THE POSITION.
        clock.At = At.AddMinutes(1).AddSeconds(30);
        await gw.EndPaperDeploymentAsync(other.Id, "test: the owner stopped the other run");
        await PassAt(gw, clock, At.AddMinutes(2).AddSeconds(5));
        var flattens = Flattens(gw, owed.Id);
        var after = gw.DeploymentReadings().Single(d => d.Id == owed.Id);

        log.WriteLine($"owed run's flattens while the other is live: {whileOtherLive}");
        log.WriteLine($"line                 : {line}");
        foreach (var f in flattens)
            log.WriteLine($"owed flatten         : {f.RequestId} {f.State} — {f.Answer}");
        log.WriteLine($"closes at the wire   : {conn.Places - placesBefore}; position {Held(conn)}");
        log.WriteLine($"after                : {after.Line}");

        Assert.Equal(1, whileOtherLive);
        Assert.Contains("NOT closed", line, StringComparison.Ordinal);
        Assert.Contains($"held while run {StrategyDeploymentRow.Short(other.Id)}", line, StringComparison.Ordinal);

        Assert.Equal(1, conn.Places - placesBefore);
        Assert.Equal(0m, Held(conn));
        Assert.Equal(2, flattens.Count);
        Assert.Equal(DeploymentOpState.Resolved, flattens[1].State);
        Assert.Contains("nothing to close", flattens[1].Answer);
        Assert.Null(after.CloseOwed);
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
        await SeedAPosition(gw, conn);

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

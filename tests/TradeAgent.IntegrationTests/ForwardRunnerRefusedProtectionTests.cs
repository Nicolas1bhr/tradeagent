using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Paper;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// A REFUSED PROTECTION IS ASKED AGAIN (<c>U-paper-t0</c> item 3; PREFLIGHT-2's mid-run item 5): a position open without its stop or its target is an unprotected
/// position, and the founding rate was only the commonest way to get one. The run asks again on the next live minute, under that minute's own ids, boundedly, and
/// never beside an exit; while it stands unprotected every surface says so in plain words. Same harness as the rest of this class: every order through
/// <c>PlaceAsync</c> and every gate, the minutes into <c>forward_bar</c>, no venue reached.
/// </summary>
public partial class ForwardRunnerTests
{
    /// <summary>Signalled at a close of 100 — stop 95, target 101 — and filled at 100.</summary>
    static readonly string Protected = ProgramText("stop percent 5\ntarget percent 1\n", "entry when close > 99");

    /// <summary>
    /// THE ENTRY FILLED ON MINUTE 3 AND EVERY PROTECTION OF IT REFUSED BEFORE THE WIRE: a rate of nothing on the pass that places them. Answers the two refused
    /// operations, with the rate back up: the gate that refused them has gone.
    /// </summary>
    static async Task<(DeploymentOpRow Stop, DeploymentOpRow Target)> RefusedProtectionAsync(Rig rig, int rate = 0, bool lifted = true)
    {
        rig.Bar(1, 99m, 100m, 98m, 100m);
        await rig.Runner.AdvanceAsync();
        rig.Bar(2, 100m, 100.5m, 99.5m, 100m);
        await rig.Gw.RefreshHealthAsync();
        await rig.Runner.AdvanceAsync();

        rig.Gw.UpdateRiskUnderTest(r => r.MaxOrdersPerMinute = rate);
        rig.Bar(3, 100m, 100.6m, 99.6m, 100.2m);
        await rig.Gw.RefreshHealthAsync();
        await rig.Runner.AdvanceAsync();
        await rig.Runner.AdvanceAsync();
        if (lifted) rig.Gw.UpdateRiskUnderTest(r => r.MaxOrdersPerMinute = 1000);

        Assert.Equal(1m, await Position(rig));
        var ops = rig.Gw.Deployments.OpsOf(rig.Deployment.Id);
        return (Assert.Single(ops, o => o.Kind == DeploymentOpKind.Stop), Assert.Single(ops, o => o.Kind == DeploymentOpKind.Target));
    }

    static async Task QuietMinuteAsync(Rig rig, int minute, decimal price = 100m)
    {
        rig.Bar(minute, price, price + 0.1m, price - 0.1m, price);
        await rig.Gw.RefreshHealthAsync();
        await rig.Runner.AdvanceAsync();
    }

    static string LineOf(Rig rig) => Assert.Single(rig.Gw.DeploymentReadings()).Line;

    // ---------------------------------------------------------------- asked again

    /// <summary>
    /// A STOP AND A TARGET REFUSED BEFORE THE WIRE ARE ASKED AGAIN ON THE NEXT LIVE MINUTE, AT THE FILL'S OWN LEVELS, UNDER THAT MINUTE'S OWN IDS — and only then: a
    /// second pass over the refused minute sends nothing, because that minute is over.
    ///
    /// <para><b>RED at the base</b>: <c>Assert.Single() Failure: The collection was empty</c> on the stop; both stayed refused and the position stood with neither
    /// for as long as it stood.</para>
    /// </summary>
    [Fact]
    public async Task A_refused_stop_and_target_are_asked_again_on_the_next_live_minute()
    {
        await using var rig = await ReadyAsync(Protected);
        var (stop, target) = await RefusedProtectionAsync(rig);
        Show(log, rig);
        Assert.All(new[] { stop, target }, o => Assert.Equal(DeploymentOpState.Refused, o.State));
        Assert.All(new[] { stop, target }, o => Assert.Contains(ErrorCode.RISK_LIMIT_EXCEEDED.ToString(), o.Answer));

        await rig.Runner.AdvanceAsync();
        Assert.Empty(await WorkingProtection(rig));

        await QuietMinuteAsync(rig, 4);
        Show(log, rig);

        var working = await WorkingProtection(rig);
        var back = Assert.Single(working, w => w.Op.Kind == DeploymentOpKind.Stop);
        var aim = Assert.Single(working, w => w.Op.Kind == DeploymentOpKind.Target);
        Assert.Equal((OrderType.Stop, OrderSide.Sell, 1m, (decimal?)95m), (back.Order.Type, back.Order.Side, back.Order.Quantity, back.Order.StopPrice));
        Assert.Equal((OrderType.Limit, OrderSide.Sell, 1m, (decimal?)101m), (aim.Order.Type, aim.Order.Side, aim.Order.Quantity, aim.Order.LimitPrice));
        Assert.Equal(["+4#0", "+4#1"], new[] { back.Op, aim.Op }.Select(o => Said(rig, o.RequestId)));
        Assert.All(new[] { back.Op, aim.Op }, o => Assert.Equal(DeploymentOpState.Resolved, o.State));

        // AND IT IS OVER: nothing goes out beside what now rests, on this minute or the next.
        await rig.Runner.AdvanceAsync();
        await QuietMinuteAsync(rig, 5);
        Assert.Equal(2, (await WorkingProtection(rig)).Count);
        Assert.Equal(4, rig.Gw.Deployments.OpsOf(rig.Deployment.Id).Count(o => o.Kind is DeploymentOpKind.Stop or DeploymentOpKind.Target));
    }

    /// <summary>EACH KIND ON ITS OWN: the stop got through and the target did not, so the target alone is asked again, and no second stop sells beside the first.</summary>
    [Fact]
    public async Task Only_the_protection_that_was_refused_is_asked_again()
    {
        await using var rig = await ReadyAsync(Protected);
        var (stop, target) = await RefusedProtectionAsync(rig, rate: 1);
        Assert.Equal(DeploymentOpState.Resolved, stop.State);
        Assert.Equal(DeploymentOpState.Refused, target.State);

        await QuietMinuteAsync(rig, 4);
        Show(log, rig);

        var working = await WorkingProtection(rig);
        Assert.Equal(stop.RequestId, Assert.Single(working, w => w.Op.Kind == DeploymentOpKind.Stop).Op.RequestId);
        var aim = Assert.Single(working, w => w.Op.Kind == DeploymentOpKind.Target);
        Assert.Equal(101m, aim.Order.LimitPrice);
        Assert.Single(rig.Gw.Deployments.OpsOf(rig.Deployment.Id), o => o.Kind == DeploymentOpKind.Stop);
    }

    /// <summary>A DEFINITE REFUSAL AT THE VENUE IS THE SAME POSITION WITH THE SAME GAP: the stop the venue rejected is asked again too, under a new id of its own.</summary>
    [Fact]
    public async Task A_stop_the_venue_rejected_is_asked_again()
    {
        PlacementWitness? witness = null;
        await using var rig = await ReadyAsync(Protected, through: c => witness = new PlacementWitness(c));
        witness!.Rejects = c => c.Type == OrderType.Stop ? new ConnectorRejectedException("the venue will not take this order") : null;

        rig.Bar(1, 99m, 100m, 98m, 100m);
        await rig.Runner.AdvanceAsync();
        rig.Bar(2, 100m, 100.5m, 99.5m, 100m);
        await rig.Gw.RefreshHealthAsync();
        await rig.Runner.AdvanceAsync();
        await QuietMinuteAsync(rig, 3, 100m);
        await rig.Runner.AdvanceAsync();
        Show(log, rig);
        var refused = Assert.Single(rig.Gw.Deployments.OpsOf(rig.Deployment.Id), o => o.Kind == DeploymentOpKind.Stop);
        Assert.Equal(ExecutionState.REJECTED, rig.Gw.Requests.Get(refused.RequestId)!.State);

        witness.Rejects = null;
        await QuietMinuteAsync(rig, 4);
        Show(log, rig);
        Assert.Equal(95m, Assert.Single(await WorkingProtection(rig), w => w.Op.Kind == DeploymentOpKind.Stop).Order.StopPrice);
    }

    /// <summary>
    /// A RESTING ORDER THAT LEFT THE BOOK UNDER A POSITION — here the owner's emergency stop, which cancels what is working and leaves the position — is the same gap:
    /// it goes back, once nothing refuses it, by the same rule.
    /// </summary>
    [Fact]
    public async Task Protection_cancelled_under_a_held_position_is_asked_for_again()
    {
        await using var rig = await ReadyAsync(Protected);
        var (stop, _) = await RefusedProtectionAsync(rig, rate: 1000);
        Assert.Equal(DeploymentOpState.Resolved, stop.State);
        foreach (var (_, order) in await WorkingProtection(rig)) await rig.Conn.CancelOrderAsync(order.ConnectorOrderId);
        await rig.Gw.ReconcileAsync();
        await rig.Runner.AdvanceAsync();

        await QuietMinuteAsync(rig, 4);
        Show(log, rig);
        Assert.Equal(2, (await WorkingProtection(rig)).Count);
    }

    /// <summary>
    /// AN EXIT THE VENUE ANSWERED WITH NOTHING ENDS NOTHING (the second Codex review): it took the stop and the target off the book first, and a rejection at the wire leaves the same long
    /// with neither as a refusal before it. They go back on the same minute, and until they do the run says so.
    /// </summary>
    [Fact]
    public async Task An_exit_the_venue_rejected_puts_the_protection_back()
    {
        PlacementWitness? witness = null;
        await using var rig = await ReadyAsync(ProgramText("stop percent 30\ntarget percent 50\n", "entry when close > 99"), through: c => witness = new PlacementWitness(c));
        rig.Bar(1, 99m, 100m, 98m, 100m);
        await rig.Runner.AdvanceAsync();
        await QuietMinuteAsync(rig, 2);
        await QuietMinuteAsync(rig, 3);
        await rig.Runner.AdvanceAsync();
        Assert.Equal(2, (await WorkingProtection(rig)).Count);

        witness!.Rejects = c => c.Type == OrderType.Market ? new ConnectorRejectedException("the venue will not take this order") : null;
        rig.Bar(4, 100m, 100m, 85m, 85m);
        await rig.Gw.RefreshHealthAsync();
        await rig.Runner.AdvanceAsync();
        Show(log, rig);

        var exit = Assert.Single(rig.Gw.Deployments.OpsOf(rig.Deployment.Id), o => o.Kind == DeploymentOpKind.Exit);
        Assert.Equal(ExecutionState.REJECTED, rig.Gw.Requests.Get(exit.RequestId)!.State);
        Assert.Equal(1m, await Position(rig));
        Assert.Equal(2, (await WorkingProtection(rig)).Count);
    }

    /// <summary>
    /// THE PRICE WENT THROUGH THE STOP WHILE IT WAS REFUSED: PREFLIGHT-2's position that "still held from entry after a 10% fall". Asked for once the gate lifts, it fires on the
    /// next open it can and the position is out, rather than resting above a market it has already left. Nothing about the fall changes what is asked for: the level is the
    /// program's, measured from the price paid.
    /// </summary>
    [Fact]
    public async Task A_stop_asked_for_after_the_price_fell_through_it_closes_the_position()
    {
        await using var rig = await ReadyAsync(ProgramText("stop percent 5\ntarget percent 50\n", "entry when close > 99"));
        var (stop, _) = await RefusedProtectionAsync(rig, lifted: false);
        Assert.Equal(DeploymentOpState.Refused, stop.State);

        // A 7 % FALL, ABOVE THE PROGRAM'S OWN EXIT AT 90 AND THROUGH THE STOP AT 95, WITH THE GATE STILL DOWN.
        await QuietMinuteAsync(rig, 4, 93m);
        rig.Gw.UpdateRiskUnderTest(r => r.MaxOrdersPerMinute = 1000);
        await QuietMinuteAsync(rig, 5, 93m);
        await QuietMinuteAsync(rig, 6, 93m);
        await QuietMinuteAsync(rig, 7, 93m);
        await rig.Gw.RefreshHealthAsync();
        await rig.Runner.AdvanceAsync();
        Show(log, rig);

        var asked = rig.Gw.Deployments.OpsOf(rig.Deployment.Id).Last(o => o.Kind == DeploymentOpKind.Stop);
        Assert.Equal(ExecutionState.FILLED, rig.Gw.Requests.Get(asked.RequestId)!.State);
        Assert.Equal(0m, await Position(rig));
    }

    // ---------------------------------------------------------------- replay and restart

    /// <summary>
    /// DETERMINISTIC UNDER REPLAY AND A RESTART: the ids are functions of the minute and the sequence, and the ledger holds what was asked. A new runner, and then a new
    /// process over the same database and book, replay the same minutes and send nothing more.
    /// </summary>
    [Fact]
    public async Task A_replay_and_a_restart_send_nothing_more()
    {
        var db = TestEnv.NewDb();
        var book = Path.Combine(TestEnv.Home, $"paper-protect-{Guid.NewGuid():n}.db");
        var origin = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
        string id;
        IReadOnlyList<string> asked;

        {
            await using var first = await ReadyAsync(Protected, book, db, origin);
            id = first.Deployment.Id;
            await RefusedProtectionAsync(first);
            await QuietMinuteAsync(first, 4);
            await new ForwardRuns(first.Gw, first.Db, () => first.Clock.At).AdvanceAsync();
            asked = [.. first.Gw.Deployments.OpsOf(id).Select(o => o.RequestId)];
            Assert.Equal(2, (await WorkingProtection(first)).Count);
        }

        await using var second = await ReadyAsync(Protected, book, db, origin);
        second.Gw.UpdateRiskUnderTest(r => r.MaxOrdersPerMinute = 1000);
        await second.Runner.AdvanceAsync();
        await second.Runner.AdvanceAsync();
        Show(log, second);

        Assert.Equal(asked, second.Gw.Deployments.OpsOf(id).Select(o => o.RequestId));
        Assert.Equal(2, (await WorkingProtection(second)).Count);
    }

    /// <summary>
    /// A PROCESS THAT DIED BETWEEN THE VENUE'S ACKNOWLEDGEMENT AND ITS WRITE HOLDS NOTHING BACK (the Codex review). The order rests and its operation has no answer; nothing asks for
    /// it again, rightly, so its row answers it, and the cursor and the maximum hold behind it move. RED without that: the operation stayed <c>dispatched</c>, every later minute
    /// sat behind it, and the maximum hold never closed the position.
    /// </summary>
    [Fact]
    public async Task A_protection_resting_when_the_process_died_is_answered_by_its_row()
    {
        await using var rig = await ReadyAsync(ProgramText("max_hold_bars 3\nstop percent 5\ntarget percent 50\n", "entry when close > 99"));
        await RefusedProtectionAsync(rig);
        await QuietMinuteAsync(rig, 4);
        var stop = Assert.Single(await WorkingProtection(rig), w => w.Op.Kind == DeploymentOpKind.Stop).Op;

        // WHAT THE LEDGER HOLDS AFTER THAT CRASH: the operation with no answer, and the cursor still behind the minute it was written on.
        rig.Db.Write(_ =>
        {
            using var c = rig.Db.Cmd("UPDATE deployment_op SET state='dispatched', answer=NULL, resolved_at=NULL WHERE request_id=$id", ("$id", stop.RequestId));
            using var d = rig.Db.Cmd("UPDATE strategy_deployment SET cursor_open_time=$bar WHERE id=$id", ("$bar", Sql.T(rig.Origin.AddMinutes(3))), ("$id", rig.Deployment.Id));
            return c.ExecuteNonQuery() + d.ExecuteNonQuery();
        });
        Assert.Equal(DeploymentOpState.Dispatched, rig.Gw.Deployments.OpById(stop.RequestId)!.State);

        await QuietMinuteAsync(rig, 5);
        await QuietMinuteAsync(rig, 6);
        Show(log, rig);

        Assert.Equal(DeploymentOpState.Resolved, rig.Gw.Deployments.OpById(stop.RequestId)!.State);
        Assert.Contains("resting at the venue", rig.Gw.Deployments.OpById(stop.RequestId)!.Answer, StringComparison.Ordinal);
        Assert.Single(rig.Gw.Deployments.OpsOf(rig.Deployment.Id), o => o.Kind == DeploymentOpKind.Flatten);
    }

    /// <summary>
    /// AND THE OTHER HALF OF THAT WINDOW (the second Codex review): dead between the entry's stop being acknowledged and its target being written, the fill's own minute is replayed live
    /// and writes the target, under the id it would have had. Answering the stop before the replay moved the cursor over that minute, and the target was never written by anything.
    /// </summary>
    [Fact]
    public async Task A_process_that_died_between_the_stop_and_the_target_writes_the_target_on_replay()
    {
        await using var rig = await ReadyAsync(Protected);
        var (stop, target) = await RefusedProtectionAsync(rig, rate: 1);
        Assert.Equal(DeploymentOpState.Resolved, stop.State);

        // THE LEDGER AFTER THAT CRASH: the stop with no answer, no target written, and the cursor behind the fill's minute.
        rig.Db.Write(_ =>
        {
            using var c = rig.Db.Cmd("UPDATE deployment_op SET state='dispatched', answer=NULL, resolved_at=NULL WHERE request_id=$id", ("$id", stop.RequestId));
            using var d = rig.Db.Cmd("DELETE FROM deployment_op WHERE request_id=$id", ("$id", target.RequestId));
            using var e = rig.Db.Cmd("DELETE FROM execution_request WHERE request_id=$id", ("$id", target.RequestId));
            using var f = rig.Db.Cmd("UPDATE strategy_deployment SET cursor_open_time=$bar WHERE id=$id", ("$bar", Sql.T(rig.Origin.AddMinutes(1))), ("$id", rig.Deployment.Id));
            return c.ExecuteNonQuery() + d.ExecuteNonQuery() + e.ExecuteNonQuery() + f.ExecuteNonQuery();
        });

        await rig.Runner.AdvanceAsync();
        await rig.Runner.AdvanceAsync();
        Show(log, rig);
        var written = Assert.Single(rig.Gw.Deployments.OpsOf(rig.Deployment.Id), o => o.Kind == DeploymentOpKind.Target);
        Assert.Equal(target.RequestId, written.RequestId);

        // THE MINUTE BEFORE THE FILL REPLAYS FLAT AND TAKES THE RESTING STOP OFF, AS IT MUST OF ANY MINUTE THAT READS FLAT, AND THE NEXT LIVE MINUTE ASKS FOR IT AGAIN.
        await QuietMinuteAsync(rig, 4);
        Show(log, rig);
        Assert.Equal(2, (await WorkingProtection(rig)).Count);
    }

    // ---------------------------------------------------------------- bounded

    /// <summary>
    /// BOUNDED, ON THE CLOCK AND IN A CATCH-UP. A gate that keeps refusing is asked once a minute for <see cref="ForwardRuns.ProtectionAskedEveryMinute"/> minutes and
    /// then once in <see cref="ForwardRuns.ProtectionAskedAtMostEvery"/>; and a pass catching up over a thousand minutes asks once, on the newest of them, because that
    /// is the only minute it has a position to speak for. A run under a kill switch for three days must not fill its ledger with refusals.
    /// </summary>
    [Fact]
    public async Task A_gate_that_keeps_refusing_is_asked_boundedly()
    {
        await using var rig = await ReadyAsync(Protected);
        await RefusedProtectionAsync(rig, lifted: false);

        int Asks() => rig.Gw.Deployments.OpsOf(rig.Deployment.Id).Count(o => o.Kind == DeploymentOpKind.Stop);
        Assert.Equal(1, Asks());

        var every = ForwardRuns.ProtectionAskedEveryMinute;
        var minute = 4;
        for (; minute < 4 + every - 1; minute++) await QuietMinuteAsync(rig, minute);
        Assert.Equal(every, Asks());

        // NOW THE ASKS SPREAD OUT: a quarter of an hour of minutes asks once.
        var spread = (int)ForwardRuns.ProtectionAskedAtMostEvery.TotalMinutes;
        for (var i = 0; i < spread - 1; i++) await QuietMinuteAsync(rig, minute++);
        Assert.Equal(every, Asks());
        await QuietMinuteAsync(rig, minute++);
        Assert.Equal(every + 1, Asks());

        // A CATCH-UP: a thousand minutes that arrive at once are one ask.
        var before = Asks();
        rig.Minutes(minute, minute + 999, _ => 100m);
        await rig.Runner.AdvanceAsync();
        await rig.Runner.AdvanceAsync();
        Assert.Equal(before + 1, Asks());
    }

    // ---------------------------------------------------------------- never beside an exit, and never sold twice

    /// <summary>
    /// NEVER BESIDE AN EXIT IN FLIGHT: an exit at the wire is about to end the position, and protection put back beside it fills too and sells what the exit sells. On
    /// the minute the program's exit goes out, nothing is asked; the position leaves, and nothing is asked after it.
    /// </summary>
    [Fact]
    public async Task Nothing_is_asked_beside_an_exit_in_flight_or_after_the_position_is_gone()
    {
        await using var rig = await ReadyAsync(ProgramText("stop percent 50\ntarget percent 50\n", "entry when close > 99"));
        var (stop, _) = await RefusedProtectionAsync(rig, lifted: false);
        rig.Gw.UpdateRiskUnderTest(r => r.MaxOrdersPerMinute = 1000);

        // THE EXIT'S MINUTE: it goes out, and the protection stays refused.
        rig.Bar(4, 100m, 100m, 80m, 80m);
        await rig.Gw.RefreshHealthAsync();
        await rig.Runner.AdvanceAsync();
        Show(log, rig);
        Assert.Single(rig.Gw.Deployments.OpsOf(rig.Deployment.Id), o => o.Kind == DeploymentOpKind.Exit);
        Assert.Equal(stop.RequestId, Assert.Single(rig.Gw.Deployments.OpsOf(rig.Deployment.Id), o => o.Kind == DeploymentOpKind.Stop).RequestId);

        await QuietMinuteAsync(rig, 5, 80m);
        await QuietMinuteAsync(rig, 6, 80m);
        await QuietMinuteAsync(rig, 7, 80m);
        Show(log, rig);
        Assert.Equal(0m, await Position(rig));
        Assert.Empty(await WorkingProtection(rig));
        Assert.DoesNotContain("UNPROTECTED", LineOf(rig), StringComparison.Ordinal);
    }

    /// <summary>
    /// A STOP THAT FILLED IS NOT MISSING, WHATEVER THE BOOKS SAY YET. The order's record can reach FILLED while the fill is still on its way to the ledger, and the books
    /// then read long: a second stop asked for on that reading sells what the first one sold. It is the direction that must never happen, so a fill on the row settles it.
    /// </summary>
    [Fact]
    public async Task A_stop_whose_fill_has_not_reached_the_ledger_is_not_asked_for_again()
    {
        PaperBooksTests.LedgerBlind? blind = null;
        await using var rig = await ReadyAsync(ProgramText("stop percent 5\ntarget percent 50\n", "entry when close > 99"), through: c => blind = new PaperBooksTests.LedgerBlind(c));
        await RefusedProtectionAsync(rig);
        await QuietMinuteAsync(rig, 4);
        var stop = Assert.Single(await WorkingProtection(rig), w => w.Op.Kind == DeploymentOpKind.Stop).Op;

        // THE STOP FILLS AND ITS FILL NEVER REACHES THE LEDGER: the record says FILLED and the books still read long. Minute 5 is the one in progress when it went out.
        await QuietMinuteAsync(rig, 5);
        blind!.Hide();
        rig.Bar(6, 100m, 100m, 90m, 96m);
        await rig.Gw.RefreshHealthAsync();
        await rig.Gw.ReconcileAsync();
        await rig.Runner.AdvanceAsync();
        await QuietMinuteAsync(rig, 7, 96m);
        await QuietMinuteAsync(rig, 8, 96m);
        Show(log, rig);

        Assert.Equal((ExecutionState.FILLED, 1m), (rig.Gw.Requests.Get(stop.RequestId)!.State, rig.Gw.Requests.Get(stop.RequestId)!.FilledQuantity));
        Assert.DoesNotContain(rig.Gw.Deployments.FillsOf(rig.Deployment), f => f.RequestId == stop.RequestId);
        Assert.Equal(stop.RequestId, rig.Gw.Deployments.OpsOf(rig.Deployment.Id).Last(o => o.Kind == DeploymentOpKind.Stop).RequestId);
    }

    /// <summary>
    /// A FILL THE LEDGER HOLDS IS THE OTHER WAY ROUND (the Codex review): a stop that filled part of the position and then left the book, with every fill on the ledger, leaves a
    /// long the books can size and nothing protecting it. The rest is asked for, at the same level, sized from the books. And the fill takes the target asked for again off
    /// the book first (the second review): the cleanup used to name the pair the entry's fill gave it, the refused ones, so a target stayed on the book at its full size beside
    /// what the stop left, and on a symbol two runs share it could sell what the other holds.
    /// </summary>
    [Fact]
    public async Task What_a_part_filled_stop_left_behind_is_asked_for_once_the_ledger_holds_the_fill()
    {
        await using var rig = await ReadyAsync(ProgramText("stop percent 5\ntarget percent 50\n", "entry when close > 99"));
        await RefusedProtectionAsync(rig);
        await QuietMinuteAsync(rig, 4);
        var working = await WorkingProtection(rig);
        var stop = Assert.Single(working, w => w.Op.Kind == DeploymentOpKind.Stop);

        // THE VENUE'S ANSWER, AS THE LEDGER WOULD HOLD IT: 0.4 of the 1 sold at 95, and the rest of the order cancelled.
        await rig.Conn.CancelOrderAsync(stop.Order.ConnectorOrderId);
        rig.Db.Write(_ =>
        {
            using var c = rig.Db.Cmd("UPDATE execution_request SET execution_state='CANCELLED', filled_quantity=0.4 WHERE request_id=$id", ("$id", stop.Op.RequestId));
            return c.ExecuteNonQuery();
        });
        Assert.True(rig.Gw.Fills.Record(new Fill(PaperConnector.TheAccount, "part-fill", rig.Clock.At, "BTCUSDT", nameof(OrderSide.Sell), 0.4m, 95m,
            stop.Order.ConnectorOrderId, TradingGateway.ClientOrderIdFor(stop.Op.RequestId), stop.Op.RequestId, null, FillSource.Event, 0m, rig.Clock.At)
        {
            ConnectorId = rig.Conn.Id
        }));
        await QuietMinuteAsync(rig, 5);
        await QuietMinuteAsync(rig, 6);
        Show(log, rig);

        var after = await WorkingProtection(rig);
        var left = Assert.Single(after, w => w.Op.Kind == DeploymentOpKind.Stop);
        Assert.Equal((0.6m, (decimal?)95m), (left.Order.Quantity, left.Order.StopPrice));
        Assert.Equal((0.6m, (decimal?)150m), Assert.Single(after, w => w.Op.Kind == DeploymentOpKind.Target).Order is var t ? (t.Quantity, t.LimitPrice) : default);
    }

    /// <summary>
    /// THE MAXIMUM HOLD TAKES OFF WHAT WAS ASKED FOR AGAIN TOO. It used to name the two ids the entry's fill gave it, and a stop that went out later under its own minute's
    /// id would still be resting when the close went out: two sells under one long.
    /// </summary>
    [Fact]
    public async Task The_maximum_hold_cancels_protection_that_was_asked_for_again()
    {
        await using var rig = await ReadyAsync(ProgramText("max_hold_bars 3\nstop percent 5\ntarget percent 50\n", "entry when close > 99"));
        await RefusedProtectionAsync(rig);
        await QuietMinuteAsync(rig, 4);
        Assert.Equal(2, (await WorkingProtection(rig)).Count);

        await QuietMinuteAsync(rig, 5);
        await QuietMinuteAsync(rig, 6);
        Show(log, rig);

        Assert.Single(rig.Gw.Deployments.OpsOf(rig.Deployment.Id), o => o.Kind == DeploymentOpKind.Flatten);
        Assert.Empty(await WorkingProtection(rig));
    }

    // ---------------------------------------------------------------- said in plain words

    /// <summary>
    /// WHILE IT STANDS UNPROTECTED THE RUN'S LINE SAYS SO, in the one sentence every surface shows, and says which of the two is missing and why; the moment its protection
    /// rests the words are gone. RED at the base: the line said only what the run held.
    /// </summary>
    [Fact]
    public async Task The_runs_line_says_it_stands_unprotected_for_exactly_as_long_as_it_does()
    {
        await using var rig = await ReadyAsync(Protected);
        await RefusedProtectionAsync(rig);

        var line = LineOf(rig);
        log.WriteLine(line);
        Assert.Contains("UNPROTECTED", line, StringComparison.Ordinal);
        Assert.Contains("no stop and no target", line, StringComparison.Ordinal);
        Assert.Contains("orders per minute is the limit", line, StringComparison.Ordinal);
        Assert.Contains("no stop and no target", Assert.Single(rig.Gw.DeploymentReadings()).Unprotected, StringComparison.Ordinal);

        await QuietMinuteAsync(rig, 4);
        Assert.DoesNotContain("UNPROTECTED", LineOf(rig), StringComparison.Ordinal);
        Assert.Null(Assert.Single(rig.Gw.DeploymentReadings()).Unprotected);
    }

    /// <summary>A POSITION THAT DECLARED NO PROTECTION IS NOT AN UNPROTECTED ONE: nothing was refused, so nothing is said.</summary>
    [Fact]
    public async Task A_program_that_declared_no_protection_does_not_read_unprotected()
    {
        await using var rig = await ReadyAsync(ProgramText("", "entry when close > 99"));
        rig.Bar(1, 99m, 100m, 98m, 100m);
        await rig.Runner.AdvanceAsync();
        await QuietMinuteAsync(rig, 2);
        await QuietMinuteAsync(rig, 3);
        await QuietMinuteAsync(rig, 4);
        Assert.Equal(1m, await Position(rig));
        Assert.DoesNotContain("UNPROTECTED", LineOf(rig), StringComparison.Ordinal);
    }

    /// <summary>
    /// A UTC DAY THAT CLOSED OVER AN UNPROTECTED POSITION SAYS SO IN ITS NOTE, and only that day's: the sentence is about that day's close, so the note's words and its id do
    /// not move when a later minute puts the protection back, and the next day's note, over a protected position, does not carry it.
    /// </summary>
    [Fact]
    public async Task The_day_note_says_a_position_stood_unprotected_at_that_days_close()
    {
        var evening = new DateTimeOffset(2026, 9, 19, 23, 50, 0, TimeSpan.Zero);
        await using var rig = await ReadyAsync(Protected, origin: evening);

        static IReadOnlyList<Publication> Notes(Rig r, string day) =>
            [.. new PublicationStore(r.Db).By(TradingGateway.PaperAllocatorRole)
                .Where(n => n.Content.Contains($"the UTC day {day} closed over it", StringComparison.Ordinal))];

        await RefusedProtectionAsync(rig, lifted: false);

        // A RATE OF NOTHING TO THE DAY'S LAST MINUTE, AND THE FIRST MINUTE OF THE NEXT DAY SAYS THE DAY THAT CLOSED.
        for (var minute = 4; minute <= 10; minute++) await QuietMinuteAsync(rig, minute);
        var note = Assert.Single(Notes(rig, "2026-09-19"));
        log.WriteLine(note.Content);
        Assert.Contains("UNPROTECTED", note.Content, StringComparison.Ordinal);
        Assert.Contains("no stop and no target", note.Content, StringComparison.Ordinal);

        rig.Gw.UpdateRiskUnderTest(r => r.MaxOrdersPerMinute = 1000);
        await QuietMinuteAsync(rig, 11);
        Assert.Equal(2, (await WorkingProtection(rig)).Count);
        await new ForwardRuns(rig.Gw, rig.Db, () => rig.Clock.At).AdvanceAsync();
        Assert.Equal(note.Id, Assert.Single(Notes(rig, "2026-09-19")).Id);

        rig.Minutes(12, 1450, _ => 100m);
        await rig.Runner.AdvanceAsync();
        var next = Assert.Single(Notes(rig, "2026-09-20"));
        log.WriteLine(next.Content);
        Assert.DoesNotContain("UNPROTECTED", next.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// A DAY'S NOTE READS THAT DAY, NOT TODAY (the Codex review): the record of a stop resting at the close can change afterwards, while the boundary minute is still replayed live, and a
    /// reading of what the row says now would make that day's note new. Its words and its id stay.
    /// </summary>
    [Fact]
    public async Task What_happens_to_a_protection_after_the_close_does_not_move_that_days_note()
    {
        var evening = new DateTimeOffset(2026, 9, 19, 23, 50, 0, TimeSpan.Zero);
        await using var rig = await ReadyAsync(Protected, origin: evening);
        await RefusedProtectionAsync(rig, rate: 1000);
        var stop = Assert.Single(await WorkingProtection(rig), w => w.Op.Kind == DeploymentOpKind.Stop).Op;
        for (var minute = 4; minute <= 10; minute++) await QuietMinuteAsync(rig, minute);

        static IReadOnlyList<Publication> Notes(Rig r) =>
            [.. new PublicationStore(r.Db).By(TradingGateway.PaperAllocatorRole)
                .Where(n => n.Content.Contains("the UTC day 2026-09-19 closed over it", StringComparison.Ordinal))];
        var note = Assert.Single(Notes(rig));
        Assert.DoesNotContain("UNPROTECTED", note.Content, StringComparison.Ordinal);

        rig.Db.Write(_ =>
        {
            using var c = rig.Db.Cmd("UPDATE execution_request SET execution_state='REJECTED' WHERE request_id=$id", ("$id", stop.RequestId));
            return c.ExecuteNonQuery();
        });
        await rig.Runner.AdvanceAsync();

        Assert.Equal(note.Id, Assert.Single(Notes(rig)).Id);
    }

    // ---------------------------------------------------------------- the founding rate, off the app's own paper venues (D20)

    /// <summary>
    /// A RUN ON ANY OTHER VENUE STAYS AT THE FLAT FIGURE (D20). The forty-five is the founding envelopes' arithmetic on the app's own paper venues, and a run a later grant put
    /// on another practice platform is not one of them.
    /// </summary>
    [Theory]
    [InlineData(null, 45)]
    [InlineData("atas", 6)]
    public async Task The_paper_run_rate_reaches_only_the_apps_own_paper_venues(string? venue, int rate)
    {
        await using var rig = await ReadyAsync(Protected, through: c => new PlacementWitness(c, venue));
        rig.Gw.UpdateRiskUnderTest(r => r.MaxOrdersPerMinute = 6);
        Assert.Equal(rate, rig.Gw.MaxOrdersPerMinuteFor(rig.Deployment.Id));
    }
}

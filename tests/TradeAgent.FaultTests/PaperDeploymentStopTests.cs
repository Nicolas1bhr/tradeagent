using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// A STOP STOPS (R30 D19; R28 P1 and P4; R29 F-C5). An owner's or an organisation's stop ends the allocation, closes what the run holds, frees the
/// slot once flat, says who and when, and takes away nothing else. A run that ends by itself keeps its allocation, as it always has.
/// </summary>
public partial class PaperDeploymentTests
{
    /// <summary>The stop row on the ledger for <paramref name="version"/>, the one row of it that is a stop.</summary>
    static AllocationRow StopRow(TradingGateway gw, string version) => Assert.Single(gw.Allocations.For(version), a => a.IsStop);

    /// <summary>
    /// THE ALLOCATION IS ENDED BEFORE THE CLOSE GOES, AND THE CLOSE STILL GOES: nothing stands behind the run when its END places its one close,
    /// and a close asks no allocation. A stop that left a position open because it had ended the thing the position stood on would be the worst way
    /// for this to fail.
    /// </summary>
    [Fact]
    public async Task A_stop_closes_what_the_run_holds_after_its_allocation_has_ended()
    {
        var (gw, conn, db, _) = await Ready();
        using var _1 = db;

        var (_, version) = await Allocated(gw, db);
        Assert.Equal(1, gw.StartPaperDeploymentsDue(At));
        var run = gw.Deployments.Open().Single();
        await SeedAPosition(gw, conn, run);

        var stopped = await gw.StopPaperDeploymentAsync(run.Id, "the account owner");

        var close = gw.Deployments.OpsOf(run.Id).Last(o => o.Kind == DeploymentOpKind.Flatten);
        log.WriteLine($"the close            : {close.State} {gw.GetRequest(close.RequestId)?.State}");
        Assert.True(stopped!.IsEnded);
        Assert.Equal(ExecutionState.FILLED, gw.GetRequest(close.RequestId)!.State);
        Assert.DoesNotContain(conn.Broker.Positions, p => p.Symbol == "BTCUSDT" && p.Quantity != 0m);
        Assert.Null(gw.Allocations.StandingForPaper(version, gw.Connector.Id, run.AccountId, At));
        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE SLOT FREES ONCE FLAT, NOT BEFORE (D19): a stop's close that comes back with no answer may be live at the platform, so the next version waits
    /// for the slot even though its allocation is already written. Answered cancelled, the run's book is still long, so the close is sent again (the Codex
    /// review's P1: RED with the slot freed over an open position nobody owns), and once it has filled that version runs. The stopped one never does again.
    /// </summary>
    [Fact]
    public async Task A_stopped_run_keeps_its_slot_until_it_is_flat_and_then_another_version_takes_it()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;

        var (_, stoppedVersion) = await Allocated(gw, db);
        Assert.Equal(1, gw.StartPaperDeploymentsDue(At));
        var run = gw.Deployments.Open().Single();
        await SeedAPosition(gw, conn, run);

        conn.Faults.DropBeforeBrokerAccept = 1;
        await gw.StopPaperDeploymentAsync(run.Id, "the account owner");

        // THE NEXT VERSION TAKES THE ALLOCATION'S PLACE AT ONCE, AND THE RUN'S ROOM WAITS ON THE UNANSWERED CLOSE.
        var next = Judged(db, PromotionVerdict.PaperEligible, threshold: 104);
        clock.At = At.AddMinutes(5);
        Assert.Equal(1, gw.AllocatePaperDue(clock.At));
        var whileOpen = gw.StartPaperDeploymentsDue(clock.At);

        gw.ForceResolve(gw.Deployments.OpsOf(run.Id).Single(o => !o.IsSettled).RequestId, ExecutionState.CANCELLED, "the owner confirmed nothing landed");
        await gw.RefreshHealthAsync();
        await gw.ReconcilePaperDeploymentsAsync(clock.At);

        clock.At = At.AddMinutes(10);
        var onceFlat = gw.StartPaperDeploymentsDue(clock.At);

        log.WriteLine($"while the close is unanswered: {whileOpen}; once flat: {onceFlat}");
        Assert.Equal(0, whileOpen);
        Assert.DoesNotContain(conn.Broker.Positions, p => p.Symbol == "BTCUSDT" && p.Quantity != 0m);
        Assert.Equal(1, onceFlat);
        Assert.Equal(next, gw.Deployments.Open().Single().VersionId);
        Assert.Single(gw.Deployments.All(), d => d.VersionId == stoppedVersion);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// AND A CLOSE THAT KEEPS BEING ANSWERED NO KEEPS THE SLOT: a stopped run whose close is refused by the venue each time is NOT closed, its line says so, and
    /// no other version starts over the position.
    /// </summary>
    [Fact]
    public async Task A_stopped_run_whose_close_is_answered_no_stays_owed_and_keeps_its_slot()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;

        await Allocated(gw, db);
        Assert.Equal(1, gw.StartPaperDeploymentsDue(At));
        var run = gw.Deployments.Open().Single();
        await SeedAPosition(gw, conn, run);

        conn.Faults.RejectNext = 99;
        await gw.StopPaperDeploymentAsync(run.Id, "the account owner");

        var close = gw.Deployments.OpsOf(run.Id).Last(o => o.Kind == DeploymentOpKind.Flatten);
        log.WriteLine($"the close: {close.State} {gw.GetRequest(close.RequestId)?.State} {close.Answer}");
        Assert.Equal(ExecutionState.REJECTED, gw.GetRequest(close.RequestId)!.State);

        Judged(db, PromotionVerdict.PaperEligible, threshold: 104);
        clock.At = At.AddMinutes(5);
        gw.AllocatePaperDue(clock.At);

        Assert.Equal(0, gw.StartPaperDeploymentsDue(clock.At));
        var line = gw.DeploymentReadings().Single(d => d.Id == run.Id).Line;
        log.WriteLine(line);
        Assert.Contains("NOT closed", line, StringComparison.Ordinal);
        await gw.DisposeAsync();
    }

    /// <summary>A RUN THAT ENDS BY ITSELF IS UNCHANGED (D19): no stop row, the allocation stands, and the replacement starts once flat.</summary>
    [Fact]
    public async Task A_run_that_ends_by_itself_keeps_its_allocation_and_is_replaced()
    {
        var (gw, _, db, clock) = await Ready();
        using var _1 = db;

        var (_, version) = await Allocated(gw, db);
        Assert.Equal(1, gw.StartPaperDeploymentsDue(At));
        var run = gw.Deployments.Open().Single();

        await gw.EndPaperDeploymentAsync(run.Id, "test: the program faulted");

        clock.At = At.AddMinutes(1);
        Assert.DoesNotContain(gw.Allocations.For(version), a => a.IsStop);
        Assert.NotNull(gw.Allocations.StandingForPaper(version, gw.Connector.Id, run.AccountId, clock.At));
        Assert.Equal(1, gw.StartPaperDeploymentsDue(clock.At));
        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE STOP SAYS WHO AND WHEN, AND KEEPS WHAT THE RUN HELD (R29 F-C5): on the row, on the run and in the note the author reads, so a stop placed
    /// to end a forward record at a chosen moment is there to be read. The row itself can authorise nothing: an empty window and two zero ceilings,
    /// standing at no instant.
    /// </summary>
    [Fact]
    public async Task A_stop_says_who_stopped_it_when_and_what_it_held()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;

        var (_, version) = await Allocated(gw, db);
        Assert.Equal(1, gw.StartPaperDeploymentsDue(At));
        var run = gw.Deployments.Open().Single();
        await SeedAPosition(gw, conn, run);

        clock.At = At.AddMinutes(7);
        var stopped = (await gw.StopPaperDeploymentAsync(run.Id, "pos-head"))!;

        var row = StopRow(gw, version);
        log.WriteLine($"the row  : {row.Reason}");
        Assert.Contains("stopped by pos-head", row.Reason, StringComparison.Ordinal);
        Assert.Contains(clock.At.ToString("yyyy-MM-dd HH:mm:ssK"), row.Reason, StringComparison.Ordinal);
        Assert.Contains(StrategyDeploymentRow.Short(run.Id), row.Reason, StringComparison.Ordinal);
        Assert.Contains("holds 1 at an average of", row.Reason, StringComparison.Ordinal);
        Assert.Equal(clock.At, row.At);

        Assert.Equal((0m, (decimal?)0m, clock.At, (DateTimeOffset?)clock.At), (row.MaxQuantity, row.MaxNotional, row.EffectiveFrom, row.EffectiveTo));
        foreach (var at in new[] { clock.At.AddSeconds(-1), clock.At, clock.At.AddSeconds(1) }) Assert.False(row.StandsAt(at));

        Assert.Equal("stopped by pos-head", stopped.EndReason);
        Assert.Equal(clock.At, stopped.EndedAt);
        var line = gw.DeploymentReadings().Single(d => d.Id == run.Id).Line;
        log.WriteLine($"the line : {line}");
        Assert.Contains($"ENDED at {clock.At:yyyy-MM-dd HH:mm:ssK}: stopped by pos-head.", line, StringComparison.Ordinal);

        var note = new PublicationStore(db).By(TradingGateway.PaperAllocatorRole).Single(p => p.Content.Contains("stopped by pos-head", StringComparison.Ordinal));
        log.WriteLine($"the note : {note.Content}");
        Assert.Contains("ended by a stop and not by itself", note.Content, StringComparison.Ordinal);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// NOT THE NEXT GRANT EITHER (D19): the version's place on paper is what ended, so withdrawing the pair's envelope and granting it again does not put
    /// a stopped version back, and the ledger's own write says why in words. Another version takes the new grant as usual.
    /// </summary>
    [Fact]
    public async Task A_stopped_version_does_not_return_under_a_later_grant()
    {
        var (gw, _, db, clock) = await Ready();
        using var _1 = db;

        var (envelope, version) = await Allocated(gw, db);
        Assert.Equal(1, gw.StartPaperDeploymentsDue(At));
        await gw.StopPaperDeploymentAsync(gw.Deployments.Open().Single().Id, "the account owner");

        clock.At = At.AddMinutes(1);
        Assert.True(gw.WithdrawPaperEnvelope(envelope.Id, clock.At).Ok);
        var regranted = (await gw.GrantPaperEnvelopeAsync("BTCUSDT", 5m, null, At.AddDays(30), clock.At)).Envelope!;

        Assert.Equal(0, gw.AllocatePaperDue(clock.At));

        var asked = gw.Allocations.RecordPaper(gw.Allocations.For(version).Single(a => !a.IsStop) with
        {
            Id = "", EffectiveFrom = clock.At, At = clock.At, EnvelopeId = regranted.Id
        }, clock.At);
        log.WriteLine($"put back by hand: {asked.Ok} — {asked.Why}");
        Assert.False(asked.Ok, "a stopped version was written back on paper");
        Assert.Contains("stopped by the account owner", asked.Why, StringComparison.Ordinal);

        var other = Judged(db, PromotionVerdict.PaperEligible, threshold: 104);
        Assert.Equal(1, gw.AllocatePaperDue(clock.At));
        Assert.NotNull(gw.Allocations.StandingForPaper(other, gw.Connector.Id, regranted.AccountId, clock.At));
        await gw.DisposeAsync();
    }

    /// <summary>
    /// A STOP THAT DID NOT FINISH IS FINISHED, IN ITS OWN WORDS: the row is written first, so a process dying before the run's END leaves a stop that
    /// holds. The reconcile pass ends the run saying who stopped it, and no sweep replaces it in between. The row here is written the way the stop writes
    /// it, with the run's END never reached.
    /// </summary>
    [Fact]
    public async Task A_stop_left_unfinished_is_finished_by_the_reconcile_pass_in_its_own_words()
    {
        var (gw, _, db, clock) = await Ready();
        using var _1 = db;

        await Allocated(gw, db);
        Assert.Equal(1, gw.StartPaperDeploymentsDue(At));
        var run = gw.Deployments.Open().Single();
        Assert.True(gw.Allocations.RecordStop(run.AllocationId, "stopped by pos-head before a restart", At).Ok);
        Assert.Equal(DeploymentState.Active, gw.Deployments.ById(run.Id)!.State);

        clock.At = At.AddMinutes(1);
        Assert.Equal(0, gw.StartPaperDeploymentsDue(clock.At));
        await gw.ReconcilePaperDeploymentsAsync(clock.At);

        var ended = gw.Deployments.ById(run.Id)!;
        log.WriteLine($"ended: {ended.EndReason}");
        Assert.True(ended.IsEnded);
        Assert.Equal("stopped by pos-head before a restart", ended.EndReason);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// A STOP OF A RUN ALREADY OVER STILL ENDS ITS ALLOCATION: a run that ended by itself a moment before the press has a replacement coming, and the
    /// caller asked for it not to run again. Nothing is sent, and the ended run's record is not rewritten.
    /// </summary>
    [Fact]
    public async Task A_stop_of_a_run_already_over_still_ends_its_allocation()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;

        var (_, version) = await Allocated(gw, db);
        Assert.Equal(1, gw.StartPaperDeploymentsDue(At));
        var run = gw.Deployments.Open().Single();
        await gw.EndPaperDeploymentAsync(run.Id, "test: the program faulted");
        var sent = conn.Placed.Count;

        clock.At = At.AddMinutes(1);
        var answered = await gw.StopPaperDeploymentAsync(run.Id, "the account owner");

        Assert.Equal("test: the program faulted", answered!.EndReason);
        Assert.Equal(sent, conn.Placed.Count);
        Assert.Equal(0, gw.StartPaperDeploymentsDue(clock.At));
        Assert.Null(gw.Allocations.StandingForPaper(version, gw.Connector.Id, run.AccountId, clock.At));
        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE VERB'S STOP IS A STOP (D9 and D19): <c>deployment-stop</c>, over the pipe's own handler, ends the allocation and names its caller, and the
    /// next sweep starts nothing. RED with the verb calling the END a run takes by itself: the run ended and the allocation stood.
    /// </summary>
    [Fact]
    public async Task The_deployment_stop_verb_ends_the_allocation_and_names_who_stopped_it()
    {
        var (gw, _, db, clock) = await Ready();
        using var _1 = db;

        var (_, version) = await Allocated(gw, db);
        Assert.Equal(1, gw.StartPaperDeploymentsDue(At));
        var run = gw.Deployments.Open().Single();

        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure());
        var answered = await server.CallAsync(new IpcRequest
        {
            Op = Ops.DeploymentStop,
            Session = "paper-closure",
            RequestId = "paper-closure-stop",
            Args = new Dictionary<string, JsonElement> { ["id"] = JsonSerializer.SerializeToElement(run.Id) }
        }, CouncilRoles.Operations, null);
        Assert.True(answered.Ok, answered.Error?.Message);

        clock.At = At.AddMinutes(1);
        Assert.Equal(0, gw.StartPaperDeploymentsDue(clock.At));
        Assert.Contains(CouncilRoles.Title(CouncilRoles.Operations), StopRow(gw, version).Reason, StringComparison.Ordinal);
        await gw.DisposeAsync();
    }

    /// <summary>TWO STOPS ARE ONE: the first stop is the record, the second writes no row and takes no second close.</summary>
    [Fact]
    public async Task A_second_stop_writes_nothing_and_the_first_stays_the_record()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;

        var (_, version) = await Allocated(gw, db);
        Assert.Equal(1, gw.StartPaperDeploymentsDue(At));
        var run = gw.Deployments.Open().Single();
        await SeedAPosition(gw, conn, run);

        await gw.StopPaperDeploymentAsync(run.Id, "the account owner");
        var sent = conn.Placed.Count;

        clock.At = At.AddMinutes(1);
        await gw.StopPaperDeploymentAsync(run.Id, "pos-head");

        Assert.Contains("the account owner", StopRow(gw, version).Reason, StringComparison.Ordinal);
        Assert.Equal(sent, conn.Placed.Count);
        await gw.DisposeAsync();
    }
}

using TradeAgent.ConnectorSdk;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// A PAPER RUN READS NO HOLDOUT WINDOW (<c>U-runner-features</c>; EDGE § 6, the holdout). The runner reads its features
/// under its own audience, which may read no window of any dataset, and asks the dataset ledger on every pass whether the
/// run's reads — from its first close less the longest reach to the pass's last close — reach one. A window that does
/// ENDS the run in words naming the dataset and its window: before its first bar when it was there at the start, nothing
/// sent; at the next pass when a cutoff is set later, its book closed as any end closes it. The sweep starts no
/// replacement while a run starting then would reach a window. The evidence rule outranks a paper run's continuity.
/// </summary>
public partial class FeatureProgramRunnerTests
{
    /// <summary>
    /// A SECOND DATASET IN THE RIG'S LEDGER WHOSE BARS END AT <paramref name="lastBar"/> — or that does not record where
    /// they end, so its window has none — AND ARE HELD OUT FROM <paramref name="from"/>: a recent download with its latest
    /// stretch held, whose tape window covers the run's own market time. Its id.
    /// </summary>
    static long HeldOut(Database db, DateTimeOffset from, DateTimeOffset? lastBar)
    {
        var datasets = new DatasetStore(db);
        var file = Path.Combine(Paths.Data, $"feature-recent-{Guid.NewGuid():n}.csv");
        Directory.CreateDirectory(Paths.Data);
        File.WriteAllText(file, KlineNormaliser.Header + "\n");

        var id = datasets.Record(new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v2", 1, 1, [],
            file, DatasetStore.Sha256(file)!, 1000, from.AddDays(-30), lastBar, 0, [],
            false, 0, 0, 0, At, DatasetState.ACCEPTED, null, []));
        Assert.True(datasets.SetHoldout(id, from, EvaluationClass.Research).Ok);
        return id;
    }

    // ---- (f) ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (f) A HELD WINDOW IS NEVER READ BY A PAPER RUN. First, a dataset held from 11:00 that does not record where its bars
    /// end — so its window has no end — is in the ledger when the run's first pass comes: its reads, from the 13:00 close
    /// less two hours, reach that window, so the run is ENDED before a bar is stepped, in words naming the dataset and its
    /// window, nothing is sent, and three sweeps start no replacement — <c>ForwardRuns.CannotRun</c> says why, because a
    /// run starting then would reach it too. Then a run that has entered on the 13:00 close
    /// meets a cutoff set afterwards, from 13:00, on a dataset whose bars run to 15:00: the next pass ENDS it in the same
    /// words and closes its book.
    ///
    /// <para><b>RED before this unit</b>: both runs are ended before their first bar for reading a feature at all, and the
    /// second never enters.</para>
    /// </summary>
    [Fact]
    public async Task A_held_window_is_never_read_by_a_paper_run()
    {
        var text = Program();

        // HELD AT THE START: ENDED BEFORE A BAR, NOTHING SENT, AND NO CHURN.
        await using (var rig = await ReadyAsync(text))
        {
            rig.Reading(At.AddMinutes(10), "-0.01000000");
            var held = HeldOut(rig.Db, At.AddHours(-1), null);
            var window = rig.Gw.Datasets.ById(held)!;
            rig.Minutes(1, 150);

            var state = Assert.Single(await rig.PassAsync());
            var ended = rig.Gw.Deployments.ById(rig.Run.Id)!;
            log.WriteLine($"held at the start: {ended.State} — {ended.EndReason}");
            Assert.Equal(DeploymentState.Ended, ended.State);
            Assert.Equal(state.Ended, ended.EndReason);
            Assert.Equal(0, state.BarsReplayed);
            Assert.StartsWith("this program's feature(s) — `funding` — are read at the close of every bar a paper run decides",
                ended.EndReason, StringComparison.Ordinal);
            Assert.Contains($"dataset {held} (BTCUSDT 1m v2) holds out every bar from {window.HoldoutFrom:u} onwards",
                ended.EndReason, StringComparison.Ordinal);
            Assert.Contains("with no end, because the dataset does not record where its bars end", ended.EndReason,
                StringComparison.Ordinal);
            Assert.Contains("A paper run reads nothing a holdout window withholds", ended.EndReason, StringComparison.Ordinal);
            Assert.Empty(await rig.Wire());
            Assert.DoesNotContain(rig.Ops, o => o.Kind is DeploymentOpKind.Entry or DeploymentOpKind.Exit);

            var cannot = ForwardRuns.CannotRun(rig.Gw.Strategies, rig.Version, rig.Gw.Tape, rig.Gw.Datasets, rig.Clock.At);
            log.WriteLine($"cannot run: {cannot}");
            Assert.NotNull(cannot);
            Assert.Contains($"dataset {held} (BTCUSDT 1m v2) holds out every bar", cannot, StringComparison.Ordinal);

            var started = new List<int>();
            for (var k = 1; k <= 3; k++)
            {
                rig.Clock.At = rig.Clock.At.AddMinutes(1);
                started.Add(rig.Gw.StartPaperDeploymentsDue(rig.Clock.At));
                await rig.Runner.AdvanceAsync();
            }
            Assert.Equal([0, 0, 0], started);
            Assert.Single(rig.Gw.Deployments.ForAllocation(rig.Run.AllocationId));
            Assert.Empty(await rig.Wire());
        }

        // A CUTOFF SET MID-RUN: THE NEXT PASS ENDS IT, AND ITS BOOK IS CLOSED.
        await using (var rig = await ReadyAsync(text))
        {
            rig.Reading(At.AddMinutes(10), "-0.01000000");
            Assert.Null((await rig.ThroughHourAsync(1, 1)).Ended);
            rig.Minutes(HourClose(1) + 1, HourClose(1) + 3);
            Assert.Null(Assert.Single(await rig.PassAsync()).Ended);
            Assert.Equal(1m, await rig.Position());

            var held = HeldOut(rig.Db, At.AddHours(1), At.AddHours(3).AddMinutes(-1));
            rig.Minutes(HourClose(1) + 4, HourClose(1) + 5);
            var state = Assert.Single(await rig.PassAsync());
            Show(rig);

            var ended = rig.Gw.Deployments.ById(rig.Run.Id)!;
            log.WriteLine($"held since: {ended.State} — {ended.EndReason}");
            Assert.Equal(DeploymentState.Ended, ended.State);
            Assert.Equal(state.Ended, ended.EndReason);
            Assert.Contains($"dataset {held} (BTCUSDT 1m v2) holds out every bar", ended.EndReason, StringComparison.Ordinal);
            Assert.Single(rig.Ops, o => o.Kind == DeploymentOpKind.Flatten);
            Assert.Contains(await rig.Wire(), o => o.Side == OrderSide.Sell && o.Quantity == 1m);
        }
    }
}

using TradeAgent.ConnectorSdk;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// EACH ORDER OF A FEATURE PROGRAM'S PAPER RUN NAMES THE VALUES IT WAS DECIDED ON, AND A REPLAY THAT READS OTHERS ENDS THE
/// RUN (<c>U-runner-features</c> item 3; EDGE § 6.2). The record is the backtest's own terms — each value or absence with
/// the digest of the rows it stands on, and the SHA-256 of every value read through the order's close — so a fill can be
/// traced to what the program saw, and a run whose tape no longer gives what an order stood on is over rather than
/// carried on under a record that is no longer true.
/// </summary>
public partial class FeatureProgramRunnerTests
{
    // ---- (d) ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (d) EACH ORDER NAMES THE VALUES IT WAS DECIDED ON. Funding is −0.01 at 12:10 and 0.0002 at 13:40, so the 13:00 close
    /// enters and the 15:00 close exits (the 14:00 close reads the entry pending). Each intent — the operation's, and the order the gateway recorded from it — carries
    /// its close, the feature's name, id, value and rows digest, and the SHA-256 of every value the run read through that
    /// close, which is the digest a BACKTEST's feed over the same tape and the same closes arrives at: the paper run's
    /// values are named in the backtest's own terms. An intent no feature program decided is written as it always was,
    /// with no <c>features</c> at all.
    ///
    /// <para><b>RED before this unit</b>: the run is ended before its first bar, so there is no order to name anything.</para>
    /// </summary>
    [Fact]
    public async Task Each_order_names_the_values_it_was_decided_on()
    {
        var text = Program();
        await using var rig = await ReadyAsync(text);
        rig.Reading(At.AddMinutes(10), "-0.01000000");
        rig.Reading(At.AddHours(1).AddMinutes(40), "0.00020000");

        for (var h = 1; h <= 3; h++) Assert.Null((await rig.ThroughHourAsync(h, h == 1 ? 1 : HourClose(h - 1) + 1)).Ended);
        rig.Minutes(HourClose(3) + 1, HourClose(3) + 3);
        Assert.Null(Assert.Single(await rig.PassAsync()).Ended);
        Show(rig);

        var entry = Assert.Single(rig.Ops, o => o.Kind == DeploymentOpKind.Entry);
        var exit = Assert.Single(rig.Ops, o => o.Kind == DeploymentOpKind.Exit);

        // THE BACKTEST'S OWN FEED, over the same tape and the same three closes, as a pipe caller reads it.
        var program = StrategyParser.Parse(text).Program!;
        using var backtest = new FeatureFeed(new TapeReader(rig.TapeFile),
            TapeHoldout.Pipe(CouncilRoles.Research, rig.Gw.Datasets), program, At.AddHours(3));
        var atEntry = backtest.At(At.AddHours(1));
        var entryDigest = backtest.ValuesSha256;
        backtest.At(At.AddHours(2));
        var atExit = backtest.At(At.AddHours(3));
        var exitDigest = backtest.ValuesSha256;

        foreach (var (op, close, read, digest, value) in new[]
                 {
                     (entry, At.AddHours(1), atEntry, entryDigest, -0.01m),
                     (exit, At.AddHours(3), atExit, exitDigest, 0.0002m)
                 })
        {
            var named = IntentOf(op).Features!;
            log.WriteLine($"{op.Kind} at {named.Close:u}: {Json.Write(named)}");
            Assert.Equal(close, named.Close);
            Assert.Equal(digest, named.Sha256);
            var one = Assert.Single(named.Values);
            Assert.Equal("funding", one.Name);
            Assert.Equal(program.Features[0].Spec.Id, one.Id);
            Assert.Equal(value, one.Value);
            Assert.Null(one.Absent);
            Assert.Equal(read.Values![0].RowsSha256, one.RowsSha256);

            // AND ON THE ORDER THE GATEWAY RECORDED FROM IT, unchanged.
            var recorded = Json.Read<PlaceIntent>(rig.Gw.Requests.Get(op.RequestId)!.ParametersJson)!;
            Assert.Equal(Json.Write(named), Json.Write(recorded.Features));
        }
        Assert.NotEqual(entryDigest, exitDigest);

        // AN INTENT NO FEATURE PROGRAM DECIDED IS WRITTEN AS IT ALWAYS WAS: no `features` key, so no existing intent's
        // text, digest or client order id moves.
        var plain = new PlaceIntent("BTCUSDT", OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, "owner")
        {
            Decision = IntentOf(entry).Decision,
            StrategyVersionId = rig.Version
        };
        Assert.DoesNotContain("features", Json.Write(plain), StringComparison.Ordinal);
        Assert.Null(Json.Read<PlaceIntent>(Json.Write(plain))!.Features);
    }

    // ---- (e) ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (e) A REPLAY THAT NO LONGER READS WHAT AN ORDER STOOD ON ENDS THE RUN. Funding is −0.01 at 12:10, so the 13:00 close
    /// enters, and the entry fills. Then a reading stamped 12:50 and received at 12:50:02 — by the 13:00 close less its
    /// latency — is written to the tape: a row the tape did not hold when the entry was decided (the brief's NOT-claimed
    /// case, a row committed after the pass that decided). The runner that decided keeps what it read and runs on. A fresh
    /// runner — a restart — reads the 13:00 close from the tape again, finds the newer reading, and its digest through
    /// that close is not the one the entry recorded: the run is ENDED in words naming the close, both digests and why,
    /// and its book is closed as any end closes it.
    ///
    /// <para><b>RED before this unit</b>: the run is ended before its first bar.</para>
    /// </summary>
    [Fact]
    public async Task A_replay_that_no_longer_reads_what_an_order_stood_on_ends_the_run()
    {
        await using var rig = await ReadyAsync(Program());
        rig.Reading(At.AddMinutes(10), "-0.01000000");

        Assert.Null((await rig.ThroughHourAsync(1, 1)).Ended);
        rig.Minutes(HourClose(1) + 1, HourClose(1) + 3);
        Assert.Null(Assert.Single(await rig.PassAsync()).Ended);
        Assert.Equal(1m, await rig.Position());
        var entry = Assert.Single(rig.Ops, o => o.Kind == DeploymentOpKind.Entry);
        var stood = IntentOf(entry).Features!;

        // A ROW RECEIVED BY THE 13:00 CLOSE, WRITTEN ONLY NOW.
        rig.Reading(At.AddMinutes(50), "0.00050000", received: At.AddMinutes(50).AddSeconds(2));

        // THE RUNNER THAT DECIDED SERVES THE CLOSE FROM WHAT IT READ, AND RUNS ON.
        rig.Minutes(HourClose(1) + 4, HourClose(1) + 5);
        Assert.Null(Assert.Single(await rig.PassAsync()).Ended);
        Assert.True(rig.Gw.Deployments.ById(rig.Run.Id)!.IsActive);

        // A FRESH RUNNER READS IT FROM THE TAPE AGAIN, AND THE RECORD NO LONGER HOLDS.
        rig.Runner = rig.Restarted();
        rig.Minutes(HourClose(1) + 6, HourClose(1) + 6);
        var replayed = Assert.Single(await rig.PassAsync());
        Show(rig);
        log.WriteLine($"ended: {replayed.Ended}");

        var ended = rig.Gw.Deployments.ById(rig.Run.Id)!;
        Assert.Equal(DeploymentState.Ended, ended.State);
        Assert.Equal(replayed.Ended, ended.EndReason);
        Assert.StartsWith("the replay of this run no longer reads the values its entry decided at the 2026-10-03 13:00:00Z "
                          + "close stood on", ended.EndReason, StringComparison.Ordinal);
        Assert.Contains(stood.Sha256, ended.EndReason, StringComparison.Ordinal);

        // NOTHING NEW WAS DECIDED ON THE OTHER VALUES, AND THE BOOK WAS CLOSED AS ANY END CLOSES IT.
        Assert.Single(rig.Ops, o => o.Kind == DeploymentOpKind.Entry);
        Assert.DoesNotContain(rig.Ops, o => o.Kind == DeploymentOpKind.Exit);
        Assert.Single(rig.Ops, o => o.Kind == DeploymentOpKind.Flatten);
        Assert.Contains(await rig.Wire(), o => o.Side == OrderSide.Sell && o.Quantity == 1m);
    }
}

using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Paper;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;
using static TradeAgent.Tests.Integration.ForwardRunnerTests;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// EACH PAPER RUN HOLDS, CLOSES AND IS CHARGED ITS OWN BOOK (<c>U-paper-books</c>): two versions on one symbol never
/// net each other, a run's END sells only what it bought, and the envelope's ceilings bound the runs' sum.
///
/// <para>The paper account is one position per symbol (<c>PaperBook</c>), so two runs on BTCUSDT hold one position at
/// the venue — their SUM — and so does a run beside the owner's own holding. Everything a run's orders passed through
/// read that sum: its stop, its exit and its maximum hold were refused <c>POSITION_MOVED</c> while anyone else held,
/// its END sold everybody's holding as its own, and its opener was charged everybody's exposure. A run's own book is
/// its own <c>deployment_op</c> rows joined to the <c>fill</c> rows under their request ids.</para>
///
/// <para>The harness is <see cref="ForwardRunnerTests"/>'s: the paper connector over the forward ledger through the
/// shipped adapter, the runner, and every order through <c>PlaceAsync</c>. Two runs at once need a grant of two, which
/// the owner's card never writes (<c>PaperDeploymentsPerEnvelope</c> is 1): it is written to the ledger here, as
/// <c>PaperDeploymentExitHygieneTests</c> writes one. What a run holds is worked out HERE, off the fill ledger — its
/// own request ids, buys less sells — never by the code under test. No venue is reached.</para>
/// </summary>
public class PaperBooksTests(ITestOutputHelper log)
{
    const string Head =
        "instrument BTCUSDT\ntimezone UTC\ntimeframe 1m\ndata_freshness 5m\nmax_decision_age 5m\n";

    /// <summary>A one-minute BTCUSDT program: its size, any declarations, and one exit and one entry rule.</summary>
    static string Program(string size, string declarations, string exit, string entry) =>
        Head + $"size fixed {size}\n" + declarations + $"exit when close < {exit}\nentry when close > {entry}\n";

    // ---------------------------------------------------------------- the harness

    /// <summary>
    /// THE OWNER'S GRANT OF TWO RUNS AT A TIME ON BTCUSDT, written to the ledger: 5 at a time and 5,000,000, unless a
    /// test is about the numbers.
    /// </summary>
    static PaperEnvelopeRow GrantOfTwo(Rig rig, decimal quantity = 5m, decimal? notional = 5_000_000m)
    {
        var granted = rig.Gw.Envelopes.Grant(new PaperEnvelopeRow(
            "", rig.Gw.Connector.Id, PaperConnector.TheAccount, "BTCUSDT", "USDT", quantity, notional, 2,
            rig.Origin, rig.Origin.AddDays(30), "test: two runs at a time on one symbol", null));
        Assert.True(granted.Ok, granted.Why);
        return granted.Envelope!;
    }

    /// <summary>
    /// TWO RUNS ON BTCUSDT: the grant of two, both versions judged, and the app's own sweeps allocating and starting
    /// both. Answers the rig and the two runs, A and B.
    /// </summary>
    static async Task<(Rig Rig, StrategyDeploymentRow A, StrategyDeploymentRow B)> TwoRunsAsync(string a, string b)
    {
        var rig = await ReadyAsync(a, seed: false);
        GrantOfTwo(rig);

        var versionA = Judged(rig.Db, a, rig.Origin);
        var versionB = Judged(rig.Db, b, rig.Origin);
        Assert.Equal(2, rig.Gw.AllocatePaperDue(rig.Origin));
        Assert.Equal(2, rig.Gw.StartPaperDeploymentsDue(rig.Origin));

        var open = rig.Gw.Deployments.Open();
        return (rig, Assert.Single(open, d => d.VersionId == versionA), Assert.Single(open, d => d.VersionId == versionB));
    }

    /// <summary>
    /// ONE CLOSED MINUTE, THE CONNECTOR ASKED ABOUT IT — which settles its book on that minute — AND TWO PASSES OF THE
    /// RUNNER OVER EVERY RUN, the second of them after a position read: what the app does when a minute closes.
    /// </summary>
    static async Task TickAsync(Rig rig, int minute, decimal open, decimal high, decimal low, decimal close)
    {
        rig.Bar(minute, open, high, low, close);
        await rig.Gw.RefreshHealthAsync();
        await rig.Runner.AdvanceAsync();
        await Position(rig);
        await rig.Runner.AdvanceAsync();
    }

    /// <summary>One run's fills off the ledger: every fill under a request id one of its operations was written under.</summary>
    static List<Fill> FillsOf(Rig rig, StrategyDeploymentRow run)
    {
        var ids = rig.Gw.Deployments.OpsOf(run.Id).Select(o => o.RequestId).ToHashSet(StringComparer.Ordinal);
        return [.. rig.Gw.Fills.Since(null).Where(f => f.RequestId is { } r && ids.Contains(r))];
    }

    /// <summary>What one run holds, worked out here and not by the code under test: its own buys less its own sells.</summary>
    static decimal Holds(Rig rig, StrategyDeploymentRow run) =>
        FillsOf(rig, run).Sum(f => string.Equals(f.Side, nameof(OrderSide.Buy), StringComparison.OrdinalIgnoreCase)
            ? f.Quantity : -f.Quantity);

    static string LineOf(Rig rig, StrategyDeploymentRow run) =>
        rig.Gw.DeploymentReadings().Single(d => d.Id == run.Id).Line;

    static List<DeploymentOpRow> OpsOf(Rig rig, StrategyDeploymentRow run, string kind) =>
        [.. rig.Gw.Deployments.OpsOf(run.Id).Where(o => o.Kind == kind)];

    /// <summary>The order at the wire one operation became, by the client order id it carries.</summary>
    static async Task<OrderInfo> OrderOf(Rig rig, DeploymentOpRow op) =>
        Assert.Single(await Wire(rig), o => o.ClientOrderId == TradingGateway.ClientOrderIdFor(op.RequestId));

    void Show(Rig rig, params StrategyDeploymentRow[] runs)
    {
        foreach (var run in runs)
        {
            foreach (var op in rig.Gw.Deployments.OpsOf(run.Id))
                log.WriteLine($"{StrategyDeploymentRow.Short(run.Id)} {op.RequestId} {op.Kind} {op.State} "
                              + $"({rig.Gw.Requests.Get(op.RequestId)?.State.ToString() ?? "no row"}) — {op.Answer}");
            log.WriteLine($"{StrategyDeploymentRow.Short(run.Id)} holds {Holds(rig, run)} — {LineOf(rig, run)}");
        }
    }

    // ---------------------------------------------------------------- the line (item 1)

    /// <summary>
    /// A RUN'S LINE STATES ITS OWN BOOK — what it holds, at what average, and what it has realised after costs — on the
    /// Dashboard, in section 4 of the owner's report and in <c>deployment-list</c>, which all print the one line.
    ///
    /// <para>No fill yet; then the entry's 1 at 100; then flat after the exit at 97, 3 lost and no cost (the paper
    /// account's declared friction is none). The runner's own reading of the same bars says the same thing: the two
    /// walk one book.</para>
    ///
    /// <para><b>RED on the base</b>: the line said nothing of what the run holds.</para>
    /// </summary>
    [Fact]
    public async Task A_runs_line_states_what_it_holds_its_average_and_its_realised_after_costs()
    {
        await using var rig = await ReadyAsync(Program("1", "", exit: "98", entry: "99"));
        var run = rig.Deployment;
        var before = LineOf(rig, run);

        await TickAsync(rig, 1, 99m, 100m, 98m, 100m);          // the entry signals from a close of 100
        await TickAsync(rig, 2, 100m, 100.5m, 99.5m, 100m);     // the minute already in progress
        await TickAsync(rig, 3, 100m, 100.6m, 99.6m, 100.2m);   // it fills at this open, 100
        var held = LineOf(rig, run);
        var reading = Assert.Single(await rig.Runner.AdvanceAsync()).Account!;

        await TickAsync(rig, 4, 100m, 100.2m, 96.5m, 97m);      // exit when close < 98
        await TickAsync(rig, 5, 97m, 97.5m, 96.5m, 97m);        // the minute already in progress
        await TickAsync(rig, 6, 97m, 97.5m, 96.5m, 97m);        // it fills at this open, 97
        var flat = LineOf(rig, run);

        log.WriteLine($"before : {before}");
        log.WriteLine($"held   : {held}");
        log.WriteLine($"runner : {reading}");
        log.WriteLine($"flat   : {flat}");

        Assert.Contains("Its own book: no fill yet.", before, StringComparison.Ordinal);
        Assert.Contains("Its own book: holds 1 at an average of 100, realised 0 after 0 in costs, over 1 fill.", held,
            StringComparison.Ordinal);
        Assert.Equal((PositionSide.Long, 1m, 100m), (reading.Position, reading.Quantity, reading.AverageFillPrice));
        Assert.Contains("Its own book: holds nothing, realised -3 after 0 in costs, over 2 fills.", flat,
            StringComparison.Ordinal);
    }

}

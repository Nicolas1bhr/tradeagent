using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Paper;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Platforms;
using Xunit;
using Xunit.Abstractions;
using static TradeAgent.Tests.Integration.FoundingOrderRateTests;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE OPEN-POSITION LIMIT COUNTS POSITIONS, AND ON PAPER EACH RUN'S HOLDING IS ONE (<c>U-paper-risk-words</c>; the orchestrator's ruling,
/// the owner's to overrule). The founding fifteen was set as five pairs by three runs (R25 D14), and the cap used to count INSTRUMENTS: on
/// five pairs it could never bind, and a board narrowing bound pairs, not runs. Here the runner chooses every order under the founding policy
/// as it ships, on the app's own paper book, with the real quote age; and the two-runs case U-paper-oco left NOT VERIFIED runs through the
/// gateway and the runner end to end. Paper only: nothing reaches a venue and no real money is involved.
/// </summary>
public class FoundingOpenPositionTests(ITestOutputHelper log)
{
    /// <summary>The runs whose own book holds something now.</summary>
    static List<StrategyDeploymentRow> Holding(Rig rig) =>
        [.. rig.Gw.Deployments.All().Where(d => rig.Gw.Deployments.BookOf(d).Held > 0m)];

    void Show(Rig rig, string when)
    {
        log.WriteLine($"--- {when}");
        foreach (var run in rig.Gw.Deployments.All().OrderBy(d => d.Symbol, StringComparer.Ordinal))
        {
            log.WriteLine($"{run.Symbol} {Short(run.Id)} holds {rig.Gw.Deployments.BookOf(run).Held}");
            foreach (var op in rig.Gw.Deployments.OpsOf(run.Id))
                log.WriteLine($"  {op.BarOpenTime:HH:mm} {op.Kind} {op.State} {rig.Gw.Requests.Get(op.RequestId)?.State}: {op.Answer}");
        }
    }

    static string Short(string id) => id.Length <= 12 ? id : id[..12];

    /// <summary>
    /// A BOARD NARROWING BINDS RUNS, NOT PAIRS. The board sets the limit to two; three runs on each of the five pairs signal together. Two runs
    /// hold, whichever pairs they are on, and every other run's entry is refused before the wire in words that say what is counted. RED at
    /// aa126ada: six runs held, the three of each of two pairs, because the cap counted the two pairs.
    /// </summary>
    [Fact]
    public async Task A_board_narrowing_of_the_open_positions_binds_runs_not_pairs()
    {
        await using var rig = await Founded(runsPerPair: 3, board: db => BoardDecides(db, risk =>
        {
            risk.MaxOpenPositions = 2;
            // The minute's figure out of the way: this test is about the positions, and a figure the board decides is every caller's.
            risk.MaxOrdersPerMinute = 1_000;
            risk.InstrumentAllowlist = [.. Pairs.Select(p => p.Pair)];
        }));
        for (var m = 1; m <= 4; m++) await rig.Minute(m);
        Show(rig, "after four minutes");

        Assert.Equal(2, Holding(rig).Count);
        var refused = rig.Gw.Deployments.All()
            .SelectMany(d => rig.Gw.Deployments.OpsOf(d.Id))
            .Where(o => o.Kind == DeploymentOpKind.Entry && o.State == DeploymentOpState.Refused)
            .ToList();
        Assert.NotEmpty(refused);
        Assert.All(refused, o => Assert.Contains("each paper run's holding is one", o.Answer, StringComparison.Ordinal));
        Assert.All(refused, o => Assert.Contains("the limit is 2", o.Answer, StringComparison.Ordinal));
    }

    /// <summary>
    /// THE FOUNDING FIFTEEN BINDS AT FIFTEEN: with the fifteen runs the founding envelopes allow all holding, one more position — the owner's own
    /// buy on a pair the runs already hold — is the sixteenth and is refused before the wire; nothing reaches the paper book. A close is never
    /// a new position, so the account can still be closed out of. RED at aa126ada: the buy went out, because the pair was already one of the
    /// five counted.
    /// </summary>
    [Fact]
    public async Task With_the_fifteen_founding_runs_holding_a_sixteenth_position_is_refused()
    {
        await using var rig = await Founded(runsPerPair: 3);
        for (var m = 1; m <= 4; m++) await rig.Minute(m);
        Show(rig, "after four minutes");
        Assert.Equal(15, Holding(rig).Count);

        var before = (await rig.Conn.GetOrdersAsync(PaperConnector.TheAccount, true, null)).Count;
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() => rig.Gw.PlaceAsync(AgentContext.Operator, "owner-sixteenth",
            new PlaceIntent("BTCUSDT", OrderSide.Buy, OrderType.Market, 0.001m, null, null, TimeInForce.Day, null)));
        Assert.Equal(ErrorCode.RISK_LIMIT_EXCEEDED, denied.Code);
        Assert.Contains("already holding or opening 15 positions", denied.Message, StringComparison.Ordinal);
        Assert.Contains("the limit is 15", denied.Message, StringComparison.Ordinal);
        Assert.Null(rig.Gw.GetRequest("owner-sixteenth"));
        Assert.Equal(before, (await rig.Conn.GetOrdersAsync(PaperConnector.TheAccount, true, null)).Count);

        // AND AN ACCOUNT AT ITS LIMIT CAN STILL BE CLOSED OUT OF: a close is never a new position, whatever else refuses it.
        var close = await Record.ExceptionAsync(() => rig.Gw.PlaceAsync(AgentContext.Operator, "owner-close-at-the-limit",
            new PlaceIntent("BTCUSDT", OrderSide.Sell, OrderType.Market, 0.001m, null, null, TimeInForce.Day, null) { Intent = OrderIntent.Close }));
        Assert.DoesNotContain("positions and the limit is", close?.Message ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// TWO RUNS ON ONE PAIR, SETTLED IN ONE BATCHED READ, END TO END (U-paper-oco's NOT VERIFIED; R31 A3-1). Both runs hold BTC with their stop
    /// and target resting: run A's stop is 2 % under the fill, run B's 3 %, both targets 5 % over. Then two minutes arrive together, as after a
    /// restart or a collector backing off: the first falls through A's stop and not B's, the second touches both targets. Nothing reads the book
    /// between them, so the paper book settles both in one read. Each run's book, stop and target are what they would be if it were alone: A
    /// stopped out and its target cancelled, B's target taken and its stop cancelled, nothing refused for want of holdings, and the account
    /// holds what the runs' books add up to.
    /// </summary>
    [Fact]
    public async Task Two_runs_on_one_pair_settled_in_one_batched_read_each_end_as_if_alone()
    {
        await using var rig = await Founded(runsPerPair: 2);
        for (var m = 1; m <= 4; m++) await rig.Minute(m);
        Show(rig, "after four minutes");

        var btc = rig.Gw.Deployments.All().Where(d => d.Symbol == "BTCUSDT").ToList();
        Assert.Equal(2, btc.Count);
        var a = btc.Single(d => StopLevel(rig, d) == 98_000m);
        var b = btc.Single(d => StopLevel(rig, d) == 97_000m);
        var held = rig.Gw.Deployments.BookOf(a).Held;
        Assert.True(held > 0m);
        Assert.Equal(held, rig.Gw.Deployments.BookOf(b).Held);

        // Two minutes, written while nothing reads the book: the paper book meets both in its next read.
        Batch(rig, 5, (99_000m, 99_000m, 97_500m, 98_500m));
        Batch(rig, 6, (101_000m, 105_500m, 101_000m, 104_000m));
        rig.Prices["BTCUSDT"] = 104_000m;
        await rig.Minute(7);
        Show(rig, "after the batched read");

        // A: its stop filled at its level, and its target was cancelled with it.
        Assert.Equal(ExecutionState.FILLED, First(rig, a, DeploymentOpKind.Stop).State);
        Assert.Equal(ExecutionState.CANCELLED, First(rig, a, DeploymentOpKind.Target).State);
        // B: its target filled, and its stop was cancelled with it.
        Assert.Equal(ExecutionState.FILLED, First(rig, b, DeploymentOpKind.Target).State);
        Assert.Equal(ExecutionState.CANCELLED, First(rig, b, DeploymentOpKind.Stop).State);

        // Each book as if alone: A lost 2 % of its holding, B made 5 %, and neither was refused for want of what it held.
        Assert.Equal((98_000m - 100_000m) * held, rig.Gw.Deployments.BookOf(a).Realised);
        Assert.Equal((105_000m - 100_000m) * held, rig.Gw.Deployments.BookOf(b).Realised);
        foreach (var run in btc)
            Assert.DoesNotContain(rig.Gw.Deployments.OpsOf(run.Id),
                o => rig.Gw.Requests.Get(o.RequestId)?.State == ExecutionState.REJECTED);

        // And the account holds exactly what the runs' own books add up to: nothing was sold twice.
        var account = await rig.Conn.GetPositionsAsync(PaperConnector.TheAccount);
        var platform = account.Where(p => p.Symbol == "BTCUSDT").Sum(p => p.Quantity);
        Assert.Equal(btc.Sum(d => rig.Gw.Deployments.BookOf(d).Held), platform);
    }

    /// <summary>The level of the run's first stop, as the runner asked for it.</summary>
    static decimal? StopLevel(Rig rig, StrategyDeploymentRow run) =>
        rig.Gw.Deployments.OpsOf(run.Id).FirstOrDefault(o => o.Kind == DeploymentOpKind.Stop) is { } op
            ? Json.Read<PlaceIntent>(op.IntentJson)?.StopPrice
            : null;

    static ExecutionRequest First(Rig rig, StrategyDeploymentRow run, string kind) =>
        rig.Gw.Requests.Get(rig.Gw.Deployments.OpsOf(run.Id).First(o => o.Kind == kind).RequestId)!;

    /// <summary>One closed minute written to the ledger and NOT read: BTC at <paramref name="btc"/>, every other pair flat at its price.</summary>
    static void Batch(Rig rig, int minute, (decimal O, decimal H, decimal L, decimal C) btc)
    {
        var open = Start.AddMinutes(minute);
        var close = open + ForwardBars.BarLength;
        foreach (var (pair, _) in Pairs)
        {
            var p = rig.Prices[pair];
            var (o, h, l, c) = pair == "BTCUSDT" ? btc : (p, p, p, p);
            Assert.Equal(1, rig.Bars.Append(new ForwardFetchAttempt
            {
                Source = ForwardBars.Source, Symbol = pair, Url = "https://example.invalid/klines (this test wrote the rows; nothing was fetched)",
                RequestedAt = close, ReceivedAt = close.AddSeconds(1), HttpStatus = 200
            }, [new ForwardBars.Kline(open, o, h, l, c, 10m, close)]).Stored);
        }
    }
}

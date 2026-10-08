using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Paper;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// A CAPPED RISK SIZE ON PAPER, UNDER AN ALLOCATION CEILING (<c>U-size-cap</c>, test (e)): the runner hands the evaluator
/// the allocation's own ceiling as capital, the evaluator sizes a <c>max_capital_fraction</c> entry to the smaller of the
/// risk size and that fraction of the ceiling, and what is sent is that size. Every gate is untouched — the ceiling still
/// refuses an order whole, ALLOCATION_EXCEEDED, and nothing makes one smaller to fit — because a cap only ever makes a
/// size smaller.
///
/// <para>Same harness as the rest of this class: the envelope through the two-witness card, the allocation and the
/// deployment by the app's own sweeps, the minutes into <c>forward_bar</c> and to the paper connector through the shipped
/// adapter, and every order through <c>PlaceAsync</c>. No venue is reached.</para>
/// </summary>
public partial class ForwardRunnerTests
{
    /// <summary>
    /// A RISK-SIZED PROGRAM WHOSE STOP IS 0.8 % UNDER THE CLOSE: one per cent of equity over it is 1.25 times the equity in
    /// notional — more than all of it, whatever the capital, which is the case the cap exists for.
    /// </summary>
    static string RiskSized(string cap) =>
        "instrument BTCUSDT\ntimezone UTC\ntimeframe 1m\ndata_freshness 5m\nmax_decision_age 5m\n"
        + $"size risk_fraction 0.01{cap}\nstop percent 0.8\nexit when close < 90\nentry when close > 100\n";

    /// <summary>
    /// (e) UNDER AN ALLOCATION CEILING OF 1,000, A CAPPED ENTRY IS SENT AND FILLS, AND AN UNCAPPED ONE IS REFUSED WHOLE,
    /// ALLOCATION_EXCEEDED, EXACTLY AS BEFORE THIS UNIT.
    ///
    /// <para>The envelope — and so the allocation the app writes inside it — is at most 10 units and at most 1,000 of
    /// value. Minute 1 closes at 200 and the entry signals: one per cent of an equity of 1,000 over a stop 1.6 below is
    /// 6.25 units, 1,250 of value, which the ceiling refuses whole and nothing is sent. Capped at 0.95 of the 1,000 it is
    /// <c>1,000 * 0.95 / 200 = 4.75</c> units, 950 of value: sent, at the wire under its request's client order id, and
    /// filled at minute 3's open — the first open after it went out.</para>
    ///
    /// <para><b>RED before item 2</b>: the capped program parsed and was sized 6.25, and the ceiling refused it.</para>
    /// </summary>
    [Fact]
    public async Task Under_an_allocation_ceiling_a_capped_entry_is_sent_and_fills_and_an_uncapped_one_is_refused_whole()
    {
        // UNCAPPED: the gateway's ceiling refuses the whole order, and nothing reaches the wire.
        await using (var rig = await ReadyAsync(RiskSized(""), envelopeQuantity: 10m, envelopeNotional: 1_000m))
        {
            rig.Bar(1, 199m, 201m, 198m, 200m);
            await rig.Runner.AdvanceAsync();
            Show(log, rig);

            var refused = Assert.Single(rig.Gw.Deployments.OpsOf(rig.Deployment.Id));
            Assert.Equal(DeploymentOpKind.Entry, refused.Kind);
            Assert.Equal(DeploymentOpState.Refused, refused.State);
            Assert.Contains(ErrorCode.ALLOCATION_EXCEEDED.ToString(), refused.Answer);
            Assert.Empty(await Wire(rig));

            rig.Bar(2, 200m, 201m, 199m, 200m);
            await rig.Gw.RefreshHealthAsync();
            rig.Bar(3, 202m, 203m, 201m, 202m);
            await rig.Gw.RefreshHealthAsync();
            Assert.Equal(0m, await Position(rig));
        }

        // CAPPED: 0.95 of the ceiling at the signal's close, sent and filled.
        await using (var rig = await ReadyAsync(RiskSized(" max_capital_fraction 0.95"), envelopeQuantity: 10m,
                         envelopeNotional: 1_000m))
        {
            rig.Bar(1, 199m, 201m, 198m, 200m);
            await rig.Runner.AdvanceAsync();
            Show(log, rig);

            var entry = Assert.Single(rig.Gw.Deployments.OpsOf(rig.Deployment.Id));
            Assert.Equal(DeploymentOpKind.Entry, entry.Kind);
            Assert.NotEqual(DeploymentOpState.Refused, entry.State);

            var order = Assert.Single(await Wire(rig));
            Assert.Equal(TradingGateway.ClientOrderIdFor(entry.RequestId), order.ClientOrderId);
            Assert.Equal(OrderSide.Buy, order.Side);
            Assert.Equal(1_000m * 0.95m / 200m, order.Quantity);
            Assert.Equal(4.75m, order.Quantity);

            rig.Bar(2, 200m, 201m, 199m, 200m);
            await rig.Gw.RefreshHealthAsync();
            Assert.Equal(0m, await Position(rig));

            rig.Bar(3, 202m, 203m, 201m, 202m);
            await rig.Gw.RefreshHealthAsync();

            Assert.Equal(4.75m, await Position(rig));
            var fill = Assert.Single(await rig.Conn.GetExecutionsAsync(PaperConnector.TheAccount, null));
            log.WriteLine($"filled {fill.Quantity} at {fill.Price}");
            Assert.Equal(202m, fill.Price);
            Assert.Equal(4.75m, fill.Quantity);
        }
    }
}

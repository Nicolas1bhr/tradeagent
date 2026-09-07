using TradeAgent.Core;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// WHERE THE LOSS BUDGETS ARE SAID, and the one place a real-money mode is refused for the lack of
/// one.
///
/// The gate itself is measured at the wire in <c>RiskGateTests</c>; these are the surfaces — the
/// mode refusal, the sentence the AI reads in its own Situation block, and the three fields on the
/// status it can ask for over the pipe. One property runs through all of them, the same one that
/// runs through the cost surfaces beside them: a figure TradeAgent cannot work out is ABSENT rather
/// than zero, everywhere it is shown, because a zero here reads as "the day has lost nothing" and
/// that is the one conclusion these budgets exist to prevent being drawn by accident.
/// </summary>
public class LiveModeNeedsADailyBudgetTests
{
    /// <summary>
    /// THE ONE CONFIGURATION THIS PRODUCT MUST NOT BE ABLE TO REACH BY PRESSING A BUTTON: real
    /// money, nobody in the loop (decided 2026-09-06), and no bound at all on what a day may cost.
    ///
    /// Every other gate bounds one order. Zero on this field means NOT ENFORCED, so a real-money
    /// mode chosen over it is an agent that may take turn after turn all day with nothing counting
    /// what it has lost. The refusal names the field and the page, because a refusal an owner cannot
    /// act on is one they read as the software being broken.
    ///
    /// The two modes that only ever REDUCE authority are not refused, whatever the budget says —
    /// that is the rule the whole Safety page is built on, and it holds here too.
    /// </summary>
    [Fact]
    public async Task A_real_money_mode_cannot_be_chosen_while_the_day_has_no_loss_budget()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        await using var _2 = gw;

        Assert.Equal(0m, gw.Settings.Risk.MaxDailyLoss);

        foreach (var live in new[] { TradingMode.LIVE_CONFIRM, TradingMode.LIVE_AUTONOMOUS })
        {
            var denied = Assert.Throws<GatewayDeniedException>(() => gw.SetMode(live));

            Assert.Equal(ErrorCode.INVALID_REQUEST, denied.Code);
            Assert.Contains(Labels.MaxDailyLoss, denied.Message, StringComparison.Ordinal);
            Assert.Contains(Labels.SafetyPage, denied.Message, StringComparison.Ordinal);
            Assert.Contains(live.ToString(), denied.Message, StringComparison.Ordinal);

            // And the refusal left the mode where it was, rather than half-applying it.
            Assert.Equal(TradingMode.PAPER, gw.Settings.Mode);
        }

        // Watch only and Practice are untouched: they take authority away.
        gw.SetMode(TradingMode.OBSERVE);
        Assert.Equal(TradingMode.OBSERVE, gw.Settings.Mode);
        gw.SetMode(TradingMode.PAPER);
        Assert.Equal(TradingMode.PAPER, gw.Settings.Mode);

        // With a budget set, both real-money modes are selectable — this is a missing number, not a
        // ban, and the owner clears it by filling the number in.
        gw.Update(s => s.Risk.MaxDailyLoss = 500m);
        gw.SetMode(TradingMode.LIVE_CONFIRM);
        Assert.Equal(TradingMode.LIVE_CONFIRM, gw.Settings.Mode);
        gw.SetMode(TradingMode.LIVE_AUTONOMOUS);
        Assert.Equal(TradingMode.LIVE_AUTONOMOUS, gw.Settings.Mode);
    }
}

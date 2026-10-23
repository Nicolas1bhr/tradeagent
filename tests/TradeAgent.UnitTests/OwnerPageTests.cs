using Avalonia.Controls;
using Avalonia.LogicalTree;
using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE OWNER'S PAGE (<c>U-settings-shed</c>; R24 §§ 4.3, 4.7): it holds exactly the presses that stay his — the emergency family, his
/// connections and keys, the real money with perception's budget, the evidence settings and the narrowing presses over what is running —
/// and no press that sets a mode, a limit, an allowance, a lane, a model, an allocation, an envelope grant or real money.
/// </summary>
public class OwnerPageTests
{
    /// <summary>The owner's page as the window builds it.</summary>
    internal static OwnerPage Build(AppHost host) => new(host);

    static async Task OnThePage(Func<OwnerPage, AppHost, Task> body)
    {
        using var db = TestEnv.NewDb();
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try { await body(Build(host), host); }
        finally { await host.Gateway.DisposeAsync(); }
    }

    /// <summary>Every press in the built tree, by the words it wears at rest.</summary>
    static List<string> Presses(OwnerPage page) =>
        [.. page.Root.GetLogicalDescendants().OfType<Button>().Select(b => b.Content as string ?? $"<{b.Content?.GetType().Name}>")];

    /// <summary>
    /// THE OWNER'S PAGE HOLDS EXACTLY THE OWNER'S PRESSES (R24 § 4.3; AUTH-001): a label allowlist over the tree the window builds — the
    /// emergency family, the key's two presses (his and perception's), the real-money cap and perception's budget, the AI's price, the two
    /// narrowings over what is running and the review release while no board seat is active — and none of the presses that left with
    /// the security board's decisions: no mode, no real-money switch, no limit, plan, allowance, review, lane, model, runtime, share,
    /// allocation or envelope grant.
    /// </summary>
    [Fact]
    public Task The_owners_page_holds_exactly_the_owners_presses() => OnThePage((page, host) =>
    {
        var presses = Presses(page);
        var card = (PerceptionCard)typeof(OwnerPage).GetField("_perception", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(page)!;
        var choices = card.Choices.Select(b => (string)b.Content!).ToList();

        string[] owners =
        [
            Labels.StopAiTrading, Labels.CancelAllWorkingOrders, Labels.CloseAllPositions,
            Labels.SaveHarnessKey, Labels.ForgetHarnessKey,
            Labels.SaveDailyCap, Labels.SaveAiPrice,
            Labels.WithdrawEnvelope, Labels.StopDeployment, Labels.ReopenAfterReview,
            // PERCEPTION'S CARD: its own key's two presses, its budget inside the cap, its Test — and the instrument it is shown on.
            (string)card.SaveKey.Content!, (string)card.ForgetKey.Content!, (string)card.SaveBudget.Content!, (string)card.TestButton.Content!
        ];
        Assert.Equal([.. owners.Concat(choices).Order(StringComparer.Ordinal)], [.. presses.Order(StringComparer.Ordinal)]);

        // NONE OF THE BOARD'S, by name — so a re-added one fails here with its own words, not as a count.
        string[] board =
        [
            "Switch real-money trading ON", "Switch real-money trading OFF", Labels.SaveLimits, Labels.SavePlanCapacity,
            Labels.SaveTurnAllowance, Labels.SaveReviewEvery, Labels.SaveLanes, Labels.SaveResearchShare, Labels.Allocate,
            Labels.GrantEnvelope, Labels.AiModelDefault, Labels.RuntimeIsTheCli, Labels.RuntimeIsTheHarness,
            .. Enum.GetValues<TradingMode>().Select(Ui.ModeLabel)
        ];
        foreach (var label in board) Assert.DoesNotContain(label, presses);
        return Task.CompletedTask;
    });

    /// <summary>
    /// THE REVIEW RELEASE IS OFFERED ONLY WHILE NO BOARD SEAT IS ACTIVE (the orchestrator's ruling of 2026-10-23): something held and no
    /// active seat — offered, with the sentence that says it is his only until the board is active; an active seat — never, whatever is held.
    /// </summary>
    [Fact]
    public void The_review_release_is_the_owners_only_until_a_board_seat_is_active()
    {
        Assert.True(OwnerPage.ReleaseOffered(held: 1, boardActive: false));
        Assert.False(OwnerPage.ReleaseOffered(held: 0, boardActive: false));
        Assert.False(OwnerPage.ReleaseOffered(held: 1, boardActive: true));
        Assert.Contains("yours until the security board is active, then the board's", Labels.ReopenAfterReviewIsYoursUntilTheBoard,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// TODAY'S LINE PROMISES MIDNIGHT ONLY WHEN A FRESH DAY CAN FUND ONE TURN (<c>U-billing-classes</c>' review, P2): a reached day whose
    /// next turn fits a whole day waits for midnight; one whose single turn sets aside more than the whole day — or whose limit is 0 —
    /// says midnight will not bring a turn back, and names what would: his own limit for real money, the board's day-total for the plan.
    /// </summary>
    [Fact]
    public void Todays_line_promises_midnight_only_when_a_fresh_day_can_fund_one_turn()
    {
        AiSpendToday Day(decimal cap, decimal spent, decimal turn, string cls) => new()
        {
            Metered = true, Cap = cap, Spent = spent, NextTurnReservation = turn, Currency = "USD", Class = cls
        };

        var (money, _) = OwnerPage.TodayLines(Day(10m, 10m, 2m, TradeAgent.Core.Db.BillingClasses.Key));
        Assert.EndsWith(" — reached; the next turn of this kind waits for midnight.", money, StringComparison.Ordinal);

        (money, _) = OwnerPage.TodayLines(Day(1m, 0m, 2m, TradeAgent.Core.Db.BillingClasses.Key));
        Assert.DoesNotContain("waits for midnight", money, StringComparison.Ordinal);
        Assert.Contains("midnight will not bring a turn back", money, StringComparison.Ordinal);
        Assert.EndsWith(Labels.TodayMoneyRepair, money, StringComparison.Ordinal);

        (money, _) = OwnerPage.TodayLines(Day(0m, 0m, 0m, TradeAgent.Core.Db.BillingClasses.Key));
        Assert.Contains("a limit of 0 admits no turn of this kind on any day", money, StringComparison.Ordinal);

        var plan = Day(40m, 0m, 50m, TradeAgent.Core.Db.BillingClasses.Plan);
        var (_, planLine) = OwnerPage.TodayLines(Day(10m, 0m, 2m, TradeAgent.Core.Db.BillingClasses.Key) with { PlanReading = plan });
        Assert.Contains("midnight will not bring a turn back", planLine, StringComparison.Ordinal);
        Assert.EndsWith(Labels.TodayPlanRepair, planLine, StringComparison.Ordinal);
        Assert.Contains("security board", planLine, StringComparison.Ordinal);
    }
}

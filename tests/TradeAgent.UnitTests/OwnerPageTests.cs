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
    internal static OwnerPage Build(AppHost host) => new(host, new SettingsPage(host));

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
            // AN EARLIER ARMED HOME's one press (item 6) — a narrowing, offered only while the gateway reads one.
            Labels.SetAsideEarlierLiveSetting,
            // PERCEPTION'S CARD: its own key's two presses, its budget inside the cap, its Test — and the instrument it is shown on.
            (string)card.SaveKey.Content!, (string)card.ForgetKey.Content!, (string)card.SaveBudget.Content!, (string)card.TestButton.Content!,
            // CONNECTIONS (item 3): the platform's three, the account list's look-again, and the bridge's repair.
            "Use the practice simulator", "Use TradeAgent paper", "Use ATAS", "Look again", Labels.ReinstallBridge,
            // EVIDENCE (item 3): what a paper fill pays, and the holdout's two.
            "Use my numbers", "Clear my numbers", Labels.SetHoldout, Labels.SetHoldoutFixture,
            // UPDATES (item 3).
            "Check for updates", "What's new", "Install update", "Turn off automatic checks"
        ];
        // THE ACCOUNT LIST'S ROWS arrive when the platform answers; each is the account's own press and nothing else.
        Assert.Equal(string.Join(" | ", owners.Concat(choices).Order(StringComparer.Ordinal)),
            string.Join(" | ", presses.Where(p => p != "Use this account").Order(StringComparer.Ordinal)));

        // NONE OF THE BOARD'S, by name — so a re-added one fails here with its own words, not as a count.
        string[] board =
        [
            "Switch real-money trading ON", "Switch real-money trading OFF", Labels.SaveLimits, Labels.SavePlanCapacity,
            Labels.SaveTurnAllowance, Labels.SaveReviewEvery, Labels.SaveLanes, Labels.SaveResearchShare, Labels.Allocate,
            Labels.GrantEnvelope, Labels.AiModelDefault, Labels.RuntimeIsTheCli, Labels.RuntimeIsTheHarness,
            .. Enum.GetValues<TradingMode>().Select(Ui.ModeLabel)
        ];
        foreach (var label in board) Assert.DoesNotContain(label, presses);

        // AND THE SETTINGS PAGE KEEPS THE MARKET DATA ALONE: the owner's sections are on his page, in one tree only.
        var settings = new SettingsPage(host);
        var left = settings.Root.GetLogicalDescendants().OfType<Button>().Select(b => b.Content as string).ToList();
        foreach (var label in owners) Assert.DoesNotContain(label, left);
        Assert.Contains("Download 12 months", left);
        return Task.CompletedTask;
    });

    /// <summary>
    /// NO EVIDENCE SETTING IS A POLICY KEY (EVID-001; R24 § 4.3): the AI's price, what a paper fill pays, the holdout, a campaign's
    /// budgets and the judge's capital stay the owner's — measurements, never the board's to set — so no field of the board's policy
    /// names one, and the owner's page shows the campaign figures the settings row holds, read-only, with no box for them.
    /// </summary>
    [Fact]
    public Task No_evidence_setting_is_a_policy_key() => OnThePage((page, host) =>
    {
        string[] evidence = ["price", "friction", "fee", "slippage", "holdout", "campaign", "trial", "verdict", "exploration", "judge", "capital"];
        var paths = TradeAgent.Core.Board.BoardPolicyBody.Flatten(TradeAgent.Core.Board.BoardPolicyBody.Canonical(TradeAgent.Core.Board.BoardFounding.V1))
            .Select(f => f.Path).ToList();
        var properties = typeof(TradeAgent.Core.Board.BoardPolicy).GetProperties().Select(p => p.Name).ToList();
        foreach (var word in evidence)
        {
            Assert.DoesNotContain(paths, p => p.Contains(word, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(properties, p => p.Contains(word, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(Labels.BoardFields, f => f.Path.Contains(word, StringComparison.OrdinalIgnoreCase));
        }

        // THE CAMPAIGN FIGURES, AS THE ROW HOLDS THEM, READ-ONLY.
        host.Gateway.Update(s => { s.CampaignTrialBudget = 120; s.CampaignExplorationBudget = 500; s.CampaignVerdictBudget = 2; s.JudgeCapital = 0m; });
        page.Update(host.Gateway.StatusAsync().GetAwaiter().GetResult());
        TextBlock Of(string name) => (TextBlock)typeof(OwnerPage).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(page)!;
        Assert.Equal("120", Of("_trialBudget").Text);
        Assert.Equal("120", Of("_explorationBudget").Text);           // clamped to the trials, as a campaign reads it
        Assert.Equal("2", Of("_verdictBudget").Text);
        Assert.StartsWith("10000", Of("_judgeCapital").Text, StringComparison.Ordinal);   // zero reads as the judge's ten thousand
        Assert.Contains(Labels.CampaignBudgetsAreEvidence, page.Root.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));
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

/// <summary>
/// AN EARLIER ARMED HOME, IN THE APP (<c>U-settings-shed</c> item 6; R24 § 4.5; CLAUDE.md "refuse, never guess" and "the first change that
/// refuses older homes ships that in-app path with it"): a home whose settings an earlier build left armed for real money is refused in
/// the gateway's own sentence — both versions and the repair — on the owner's page, and ONE press sets the earlier setting aside (a
/// narrowing, like stop). Nothing is converted and nothing is touched until he presses; no terminal anywhere.
/// </summary>
public class EarlierArmedHomeTests
{
    static void Press(Button b) => b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    static readonly TradeAgent.Core.Board.EarlierLiveSetting Armed = new("LIVE_AUTONOMOUS", "true", 46,
        Labels.HomeArmedBeforeTheBoard("a live mode and real money switched on", 46, 47));

    [Fact]
    public void An_earlier_armed_home_is_refused_in_the_app_and_one_press_sets_its_live_setting_aside()
    {
        TradeAgent.Core.Board.EarlierLiveSetting? earlier = Armed;
        var pressed = 0;
        var card = new OwnerPage.EarlierArmedHome(() => earlier, () => { pressed++; earlier = null; });
        card.Update();

        Assert.True(card.Card.IsVisible);
        Assert.Equal(Armed.Sentence, card.Sentence.Text);
        Assert.Contains("schema 46", card.Sentence.Text, StringComparison.Ordinal);
        Assert.Contains("schema 47", card.Sentence.Text, StringComparison.Ordinal);
        Assert.Equal(Labels.SetAsideEarlierLiveSetting, card.Press.Content);

        // ONE PRESS: a narrowing, never armed first.
        Press(card.Press);
        Assert.Equal(1, pressed);
        Assert.False(Ui.IsArmed(card.Press));
        Assert.False(card.Card.IsVisible);
        Assert.Equal(Labels.EarlierLiveSettingSetAside, card.Note.Text);
        Assert.True(card.Note.IsVisible);

        // AND THE PAGE WIRES THE GATEWAY'S OWN READING AND ITS ONE PRESS, nowhere else in the app.
        var root = Repo();
        var owner = File.ReadAllText(Path.Combine(root, "src", "TradeAgent.App", "OwnerView.cs"));
        Assert.Contains("new EarlierArmedHome(() => _host.Gateway.EarlierLiveSetting, () => _host.Gateway.SetAsideEarlierLiveSetting())",
            owner, StringComparison.Ordinal);
        Assert.Equal(1, Directory.GetFiles(Path.Combine(root, "src", "TradeAgent.App"), "*.cs")
            .Sum(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), @"SetAsideEarlierLiveSetting\(\)").Count));
    }

    [Fact]
    public void An_earlier_armed_home_is_left_untouched_until_the_press()
    {
        TradeAgent.Core.Board.EarlierLiveSetting? earlier = Armed;
        var pressed = 0;
        var card = new OwnerPage.EarlierArmedHome(() => earlier, () => pressed++);

        for (var i = 0; i < 5; i++) card.Update();           // the five-second pass, five times over
        Assert.Equal(0, pressed);
        Assert.True(card.Card.IsVisible);
        Assert.Equal(Armed, earlier);

        // A HOME NOTHING ARMED SHOWS NO CARD AND OFFERS NO PRESS.
        var none = new OwnerPage.EarlierArmedHome(() => null, () => pressed++);
        none.Update();
        Assert.False(none.Card.IsVisible);
        Assert.Equal(0, pressed);

        // A PRESS THE GATEWAY REFUSES IS SAID, AND THE CARD STAYS.
        var refused = new OwnerPage.EarlierArmedHome(() => Armed, () => throw new InvalidOperationException("the row could not be kept"));
        refused.Update();
        Press(refused.Press);
        Assert.True(refused.Card.IsVisible);
        Assert.Contains("the row could not be kept", refused.Note.Text, StringComparison.Ordinal);
    }

    static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }
}

using TradeAgent.AgentRuntime;
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

/// <summary>
/// THE SENTENCE THE AI READS ABOUT WHAT IT HAS LOST, and the three fields it can ask for.
///
/// The AI's mission is to make at least enough to pay for itself, and the day's budget is the thing
/// that can stop it doing so. An agent that is not told the figure plans a day it will not be
/// allowed to have and reads the refusals as the software being broken — so the block says what has
/// been lost, what it is allowed to lose, and, when the figure cannot be worked out at all, that new
/// positions are refused until it can be. Never a zero standing in for an unknown.
/// </summary>
public class LossSituationTests
{
    static LossToday Lost(decimal loss, decimal day = 2_000m, decimal trade = 500m, int feesUnknown = 0) => new()
    {
        Enforced = true, Loss = loss, DayBudget = day, TradeBudget = trade,
        Currency = "USD", FeesUnknownFills = feesUnknown
    };

    static string Situation(LossToday loss) =>
        new MissionSituation { LocalTime = DateTimeOffset.Now, Mode = "PAPER", Loss = loss }.Text();

    [Fact]
    public void The_block_says_what_the_day_has_lost_and_what_it_is_allowed_to_lose()
    {
        var text = Situation(Lost(1_200m));

        Assert.Contains("1200 USD", text, StringComparison.Ordinal);
        Assert.Contains("2000 USD daily budget", text, StringComparison.Ordinal);
        Assert.Contains("no one position may lose more than 500 USD", text, StringComparison.Ordinal);
        Assert.DoesNotContain("no new positions until tomorrow", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Once the day's budget is reached the block says so in the words the refusal uses, AND says
    /// what still works. An AI told only "no" would spend its remaining turns retrying; an AI told
    /// it may still close is one that can act on the situation it is actually in.
    /// </summary>
    [Fact]
    public void A_day_at_its_budget_says_no_new_positions_until_tomorrow_and_that_closing_still_works()
    {
        var text = Situation(Lost(2_000m));

        Assert.Contains("no new positions until tomorrow (UTC)", text, StringComparison.Ordinal);
        Assert.Contains("Closing or reducing a position still works", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE CASE A NUMBER WOULD LIE ABOUT. A day nobody could price is not a day that lost nothing,
    /// and the AI is about to have its next order refused for exactly that reason — so the block
    /// says the reason and says that new positions are refused until it can be worked out.
    /// </summary>
    [Fact]
    public void A_day_that_could_not_be_worked_out_says_why_and_says_new_positions_are_refused()
    {
        var text = Situation(new LossToday
        {
            Enforced = true,
            Unknown = "your platform did not say what one contract of XYZ is worth",
            DayBudget = 2_000m,
            Currency = "USD"
        });

        Assert.Contains("could not be worked out", text, StringComparison.Ordinal);
        Assert.Contains("one contract of XYZ", text, StringComparison.Ordinal);
        Assert.Contains("new positions are refused until it can be", text, StringComparison.Ordinal);
        Assert.DoesNotContain("0 USD", text, StringComparison.Ordinal);
    }

    /// <summary>Fills with no reported fee make the figure a little kind, and it says so.</summary>
    [Fact]
    public void Fills_with_no_reported_fee_are_named_rather_than_counted_as_free()
    {
        Assert.Contains("no fee for 3 of today", Situation(Lost(900m, feesUnknown: 3)), StringComparison.Ordinal);
    }

    /// <summary>With neither budget set there is no line at all — nothing is enforced to say.</summary>
    [Fact]
    public void No_budget_means_no_line_in_the_block()
    {
        Assert.Null(LossToday.NotEnforced.Line());
        Assert.DoesNotContain("lost today", Situation(LossToday.NotEnforced), StringComparison.Ordinal);
    }
}

/// <summary>
/// The three fields on the status the agent can ask for. ABSENT is the whole convention: an absent
/// budget is one that is not enforced, and an absent <c>loss_today</c> means TradeAgent could not
/// work the figure out. A zero would say "the day is flat" — which is the plan the agent would make
/// right up to the moment its next order is refused.
/// </summary>
public class LossStatusFieldsTests
{
    [Fact]
    public async Task The_status_carries_the_day_and_the_two_budgets()
    {
        var (gw, _, db) = await TestEnv.Ready(s =>
        {
            s.Risk.MaxDailyLoss = 2_000m;
            s.Risk.MaxLossPerTrade = 500m;
        });
        using var _1 = db;
        await using var _2 = gw;

        var status = await gw.StatusAsync();

        Assert.Equal(0m, status.LossToday);          // nothing traded, and that IS zero
        Assert.Equal(2_000m, status.LossBudgetDay);
        Assert.Equal(500m, status.LossBudgetTrade);

        var json = Json.Write(status);
        Assert.Contains("\"loss_today\":0", json, StringComparison.Ordinal);
        Assert.Contains("\"loss_budget_day\":2000", json, StringComparison.Ordinal);
        Assert.Contains("\"loss_budget_trade\":500", json, StringComparison.Ordinal);

        // The pipe server serves this record through `with { … }` to recompute two fields for the
        // caller's own authority. Positional parameters would survive that; init-only properties
        // have to be carried by the copy, and a regression here would blank the figures on the wire
        // while leaving them right on the Dashboard.
        var forCaller = status with { ExecutionAvailable = false, ExecutionBlockedReason = "test" };
        Assert.Equal(0m, forCaller.LossToday);
        Assert.Equal(2_000m, forCaller.LossBudgetDay);
        Assert.Equal(500m, forCaller.LossBudgetTrade);

        Assert.Contains("loss_budget_day", Json.Write(GatewaySchema.Describe(status)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_budget_that_is_not_enforced_is_absent_from_the_wire_and_so_is_the_figure()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        await using var _2 = gw;

        var json = Json.Write(await gw.StatusAsync());

        Assert.DoesNotContain("loss_today", json, StringComparison.Ordinal);
        Assert.DoesNotContain("loss_budget_day", json, StringComparison.Ordinal);
        Assert.DoesNotContain("loss_budget_trade", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// A budget IS set, and the day cannot be worked out: the budget is on the wire and the figure
    /// is absent. The two facts are separate and both are news — an agent told a budget with no
    /// figure knows it is flying blind against one, which is exactly its situation.
    /// </summary>
    [Fact]
    public async Task A_day_that_cannot_be_worked_out_is_absent_while_the_budget_is_still_named()
    {
        // XYZ is the symbol the simulator trades and does not describe, so the day cannot be sized.
        var (gw, _, db) = await TestEnv.Ready(s => s.Risk.MaxNotionalPerOrder = 0m);
        using var _1 = db;
        await using var _2 = gw;

        await gw.PlaceAsync(new AgentContext("a"), "status-xyz", TestEnv.Buy("XYZ"));
        gw.Update(s => s.Risk.MaxDailyLoss = 2_000m);

        var status = await gw.StatusAsync();
        var json = Json.Write(status);

        Assert.Null(status.LossToday);
        Assert.Equal(2_000m, status.LossBudgetDay);
        Assert.DoesNotContain("loss_today", json, StringComparison.Ordinal);
        Assert.Contains("\"loss_budget_day\":2000", json, StringComparison.Ordinal);
    }
}

/// <summary>
/// THE TWO BOXES ON THE SAFETY PAGE, AND THE TWO DOCUMENTS THAT PROMISE THEM.
///
/// The gate and the sentences are exercised above; this is the composition and the paperwork — the
/// lines no test off a running app can reach, which catch a revert or a deletion rather than a
/// rewrite (the pattern <c>TwoPressGrantTests.The_safety_page_builds_all_three_controls</c> uses).
///
/// The guide is included because the loss budgets are the first limits whose effect the owner meets
/// as an AI that stopped trading rather than as an order that was refused, and a guide that does not
/// say "closing still works" is a guide that turns a working budget into a support call.
/// </summary>
public class LossBudgetCompositionTests
{
    static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }

    [Fact]
    public void The_safety_page_puts_both_budgets_in_the_limits_block_through_the_shared_widgets()
    {
        var text = File.ReadAllText(Path.Combine(Repo(), "src", "TradeAgent.App", "DashboardView.cs"));

        Assert.Contains("_maxLossPerTrade = Ui.NumberField(r.MaxLossPerTrade", text, StringComparison.Ordinal);
        Assert.Contains("_maxDailyLoss = Ui.NumberField(r.MaxDailyLoss", text, StringComparison.Ordinal);
        Assert.Contains("Ui.FieldRow(Labels.MaxLossPerTrade, _maxLossPerTrade, _tradeLossHint)", text, StringComparison.Ordinal);
        Assert.Contains("Ui.FieldRow(Labels.MaxDailyLoss, _maxDailyLoss, _dailyLossHint)", text, StringComparison.Ordinal);

        // Saved by the same press as the other five, so RiskPolicy.Widenings decides the second one.
        Assert.Contains("MaxLossPerTrade = _maxLossPerTrade.Value", text, StringComparison.Ordinal);
        Assert.Contains("MaxDailyLoss = _maxDailyLoss.Value", text, StringComparison.Ordinal);
        Assert.Contains("s.Risk.MaxLossPerTrade = pending.MaxLossPerTrade;", text, StringComparison.Ordinal);
        Assert.Contains("s.Risk.MaxDailyLoss = pending.MaxDailyLoss;", text, StringComparison.Ordinal);
    }

    /// <summary>The hint names the account's currency once the platform has said what it is.</summary>
    [Fact]
    public void The_hint_says_what_a_zero_means_and_names_the_currency_only_when_it_is_known()
    {
        Assert.Equal("0 means not enforced.", Labels.LossBudgetHint());
        Assert.Contains("in USD", Labels.LossBudgetHint("USD"), StringComparison.Ordinal);
        Assert.DoesNotContain("in ", Labels.LossBudgetHint(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The AI's own instructions list the limits that will refuse its orders, and a list that left
    /// these two out would be an agent planning against limits it has not been told about. Both
    /// states are written, because "no budget is set" is a fact it needs as much as a number.
    /// </summary>
    [Theory]
    [InlineData(0, 0, "no per-position loss budget is set", "no daily loss budget is set")]
    [InlineData(500, 2000, "down **500** may not be added to", "day is down **2,000**")]
    public void The_agents_own_instructions_name_both_budgets(decimal trade, decimal day, string first, string second)
    {
        var root = Path.Combine(Path.GetTempPath(), "tradeagent-tests", Guid.NewGuid().ToString("n"));
        var home = WorkspaceBuilder.Build(new WorkspaceContext(
            ConnectorName: "Simulator (built in)", ConnectorIsPaper: true, AccountId: "SIM-001",
            Mode: TradingMode.PAPER, ExecutionAvailable: true, ExecutionBlockedReason: null,
            Risk: new RiskPolicy { MaxLossPerTrade = trade, MaxDailyLoss = day, InstrumentAllowlist = ["ES"] }), root);

        var agents = File.ReadAllText(Path.Combine(home, "AGENTS.md"));
        Assert.Contains(first, agents, StringComparison.Ordinal);
        Assert.Contains(second, agents, StringComparison.Ordinal);
        Assert.Contains("never refuse a close or a reduce", agents, StringComparison.Ordinal);
        Directory.Delete(root, true);
    }

    [Fact]
    public void The_guide_and_the_contract_say_what_the_budgets_do_and_what_they_never_do()
    {
        var guide = File.ReadAllText(Path.Combine(Repo(), "docs", "USER-GUIDE.md"));
        Assert.Contains("lose on one position", guide, StringComparison.Ordinal);
        Assert.Contains("lose in one day", guide, StringComparison.Ordinal);
        Assert.Contains("Closing or reducing a position is never refused by them", guide, StringComparison.Ordinal);
        Assert.Contains("not enforced", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("The five safety limits", guide, StringComparison.Ordinal);

        var contracts = File.ReadAllText(Path.Combine(Repo(), "docs", "CONTRACTS.md"));
        Assert.Contains("loss_today", contracts, StringComparison.Ordinal);
        Assert.Contains("loss_budget_day", contracts, StringComparison.Ordinal);
        Assert.Contains("LOSS_BUDGET_REACHED", contracts, StringComparison.Ordinal);
        Assert.Contains("absent rather than zero", contracts, StringComparison.Ordinal);
    }
}

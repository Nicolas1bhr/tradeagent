using Avalonia.Controls;
using Avalonia.Interactivity;
using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// WHERE THE BILL IS SAID: the AI card, the second press of the grant, the Safety page's ceiling,
/// the block the AI itself reads, and the status it can ask for over the pipe.
///
/// One property runs through all five and is the reason this file exists: a cost TradeAgent cannot
/// measure is never drawn as a number. "0.00 of 5.00 today" beside an AI that has been working for
/// six hours is not a smaller error than a wrong figure — it is a limit that is holding nothing back,
/// drawn as one that is.
///
/// The layout and the colours of the card are NOT asserted here and are not claimed anywhere: what
/// is read back is the sentence, exactly as <c>U-life</c> reads back the card's four state words.
/// </summary>
public class MissionCostSurfacesTests
{
    static void Press(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    static AiSpendToday Priced(decimal spent, decimal cap, int turns = 4, int unpriced = 0) => new()
    {
        Metered = true, CanPrice = true, Spent = spent, Cap = cap, Currency = "USD",
        Turns = turns, UnpricedTurns = unpriced,
        ResumesAt = new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero)
    };

    static AiSpendToday Unpriceable(int turns = 4) => new()
    {
        Metered = true, CanPrice = false, Spent = 0m, Cap = 5m, Currency = "USD",
        Turns = turns, UnpricedTurns = turns,
        WhyNoPrice = "costs.json has no price for gpt-5.1-codex-max",
        ResumesAt = new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero)
    };

    // ---- the AI card ---------------------------------------------------------------------------

    [Fact]
    public void The_card_shows_todays_cost_against_the_cap()
    {
        var line = DashboardPage.MissionCost(Priced(1.25m, 5m));
        Assert.Contains("1.25 USD", line);
        Assert.Contains("5 USD", line);
        Assert.DoesNotContain("unknown", line);
    }

    /// <summary>
    /// NEVER A GUESSED NUMBER, AND NEVER A ZERO STANDING IN FOR ONE. This is the reading the whole
    /// item turns on: with no usable price list the card shows no figure and says what is wrong.
    /// </summary>
    [Fact]
    public void A_cost_that_cannot_be_measured_is_said_rather_than_drawn_as_zero()
    {
        var line = DashboardPage.MissionCost(Unpriceable());
        Assert.Contains("unknown", line);
        Assert.Contains("costs.json has no price", line);
        Assert.Contains("cannot stop it", line);
        Assert.DoesNotContain("0.00", line);
    }

    [Fact]
    public void A_capped_day_says_so_and_names_the_hour_it_starts_again()
    {
        var line = DashboardPage.MissionCost(Priced(5m, 5m));
        Assert.Contains("limit is reached", line);
        Assert.Contains("starts again at", line);
    }

    /// <summary>A partly unpriced day says the figure is a floor rather than the total.</summary>
    [Fact]
    public void A_partly_unpriced_day_says_the_figure_is_at_least_that()
    {
        var line = DashboardPage.MissionCost(Priced(0.40m, 5m, turns: 6, unpriced: 2));
        Assert.Contains("2 turns could not be priced", line);
        Assert.Contains("at least that", line);
    }

    [Fact]
    public void With_no_meter_at_all_the_card_says_nothing_about_cost()
    {
        Assert.Equal("", DashboardPage.MissionCost(AiSpendToday.NotMetered));
    }

    // ---- the second press ----------------------------------------------------------------------

    /// <summary>
    /// The grant names the money as well as the autonomy. It is a great deal of room, and the cap is
    /// the whole of what bounds it.
    /// </summary>
    [Fact]
    public void The_second_press_of_the_grant_names_the_daily_cap()
    {
        var working = false;
        var b = DashboardPage.BuildWorkOnItsOwn(() => working, () => working = true, () => working = false,
            () => Priced(0m, 5m));

        Press(b);
        Assert.False(working);
        Assert.Contains("up to 5 USD a day", (string)b.Content!);

        Press(b);
        Assert.True(working);
    }

    /// <summary>
    /// And where the cap cannot be applied it says THAT, rather than quoting a ceiling nothing can
    /// reach. A grant described by a limit that is not in force is a grant described wrongly.
    /// </summary>
    [Fact]
    public void The_second_press_says_when_the_cap_cannot_be_applied_at_all()
    {
        var working = false;
        var b = DashboardPage.BuildWorkOnItsOwn(() => working, () => working = true, () => working = false,
            () => Unpriceable(turns: 0));

        Press(b);
        Assert.Contains("cannot price this AI", (string)b.Content!);
        Assert.DoesNotContain("up to", (string)b.Content!);
    }

    // ---- the Safety page's ceiling ---------------------------------------------------------------

    /// <summary>
    /// RAISING IT IS A GRANT. The same rule as the risk limits beside it, and for the same reason:
    /// the press gives the AI more of the owner's money.
    /// </summary>
    [Fact]
    public void Raising_the_daily_cap_asks_twice_and_names_the_figure()
    {
        var saved = 0;
        var current = 5m;
        var pending = 20m;
        var b = SafetyPage.BuildSaveDailyCap(() => current, () => pending, () => "USD", () => saved++);

        Press(b);
        Assert.Equal(0, saved);
        Assert.Equal(Labels.RaiseDailyCapArmed("20 USD"), b.Content);

        Press(b);
        Assert.Equal(1, saved);
    }

    /// <summary>Lowering it saves at once: hesitating on the way down costs money.</summary>
    [Fact]
    public void Lowering_the_daily_cap_saves_in_one_press()
    {
        var saved = 0;
        var current = 5m;
        var pending = 1m;
        var b = SafetyPage.BuildSaveDailyCap(() => current, () => pending, () => "USD", () => saved++);

        Press(b);
        Assert.Equal(1, saved);
        Assert.Equal(Labels.SaveDailyCap, b.Content);
    }

    /// <summary>An unchanged number is not a grant either.</summary>
    [Fact]
    public void Leaving_the_daily_cap_alone_saves_in_one_press()
    {
        var saved = 0;
        var b = SafetyPage.BuildSaveDailyCap(() => 5m, () => 5m, () => "USD", () => saved++);
        Press(b);
        Assert.Equal(1, saved);
    }

    // ---- what the AI itself is told ---------------------------------------------------------------

    /// <summary>
    /// The Situation's cost line. The AI's mission is to make at least enough to pay for itself, and
    /// an AI told to cover its costs without being told what they are is being asked to guess at half
    /// the arithmetic.
    /// </summary>
    [Fact]
    public void The_situation_tells_the_ai_what_it_has_cost_today()
    {
        var text = new MissionSituation
        {
            LocalTime = DateTimeOffset.Now, Mode = "PAPER", Spend = Priced(0.31m, 5m)
        }.Text();

        Assert.Contains("What you have cost today: 0.31 USD of a 5 USD daily limit", text);
    }

    [Fact]
    public void The_situation_tells_the_ai_when_nobody_can_price_its_turns()
    {
        var text = new MissionSituation
        {
            LocalTime = DateTimeOffset.Now, Mode = "PAPER", Spend = Unpriceable()
        }.Text();

        Assert.Contains("price unknown", text);
        Assert.Contains("nothing is holding your spending back but you", text);
    }

    /// <summary>Nothing metering means nothing said, rather than a line of zeroes.</summary>
    [Fact]
    public void The_situation_says_nothing_about_cost_when_nothing_is_metering()
    {
        var text = new MissionSituation { LocalTime = DateTimeOffset.Now, Mode = "PAPER" }.Text();
        Assert.DoesNotContain("cost today", text);
    }
}

/// <summary>
/// THE THREE FACTS THE AGENT CAN READ ABOUT ITS OWN WORK, on the status composer that already emits
/// <c>ai_trading_stopped</c>.
///
/// It is a read and only a read. There is no verb and no pipe op that starts the loop, pauses it or
/// changes the ceiling — operator authority is deliberately absent from that channel — so what this
/// adds is an agent that can see its own bill, never one that can edit it.
/// </summary>
public class AiStatusFieldsTests
{
    [Fact]
    public async Task The_status_carries_the_loops_state_its_turns_and_what_they_cost()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        await using var _2 = gw;

        gw.Ai = () => new AiActivity("working", 4, 0.3125m);
        var status = await gw.StatusAsync();

        Assert.Equal("working", status.AiState);
        Assert.Equal(4, status.AiTurnsToday);
        Assert.Equal(0.3125m, status.AiCostToday);

        var json = Json.Write(status);
        Assert.Contains("\"ai_state\":\"working\"", json);
        Assert.Contains("\"ai_turns_today\":4", json);
        Assert.Contains("\"ai_cost_today\":0.3125", json);
    }

    /// <summary>
    /// A cost nobody can measure is ABSENT from the wire, never zero. An agent whose mission is to
    /// cover what it costs would read a zero as "this was free", which is the one wrong conclusion
    /// this whole unit exists to prevent — and the schema's own description of `status` says so.
    /// </summary>
    [Fact]
    public async Task An_unpriceable_cost_is_absent_from_the_wire_rather_than_zero()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        await using var _2 = gw;

        gw.Ai = () => new AiActivity("waiting", 7, null);
        var json = Json.Write(await gw.StatusAsync());

        Assert.Contains("\"ai_turns_today\":7", json);
        Assert.DoesNotContain("ai_cost_today", json);

        var described = Json.Write(GatewaySchema.Describe(await gw.StatusAsync()));
        Assert.Contains("ABSENT, not zero", described);
    }

    /// <summary>
    /// With nothing wired the status says the AI is stopped and reports no cost — which is exactly
    /// true of a build with no AI prepared, and is not the same claim as "it cost nothing".
    /// </summary>
    [Fact]
    public async Task With_no_loop_wired_the_status_says_stopped_and_claims_no_figure()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        await using var _2 = gw;

        var status = await gw.StatusAsync();
        Assert.Equal("stopped", status.AiState);
        Assert.Equal(0, status.AiTurnsToday);
        Assert.Null(status.AiCostToday);
    }

    /// <summary>
    /// The pipe server serves this record through <c>with { … }</c> to recompute two fields for the
    /// caller's own authority. Positional parameters would survive that; init-only properties have
    /// to be carried by the copy, and a regression here would silently blank the AI's own numbers
    /// for every caller on the pipe while leaving them right on the Dashboard.
    /// </summary>
    [Fact]
    public async Task Recomputing_the_status_for_a_caller_carries_the_ai_fields_across()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        await using var _2 = gw;

        gw.Ai = () => new AiActivity("paused", 2, 1.5m);
        var forCaller = (await gw.StatusAsync()) with { ExecutionAvailable = false, ExecutionBlockedReason = "test" };

        Assert.Equal("paused", forCaller.AiState);
        Assert.Equal(2, forCaller.AiTurnsToday);
        Assert.Equal(1.5m, forCaller.AiCostToday);
    }
}

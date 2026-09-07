using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A TURN IS PRICED AT THE MODEL TRADEAGENT ASKED FOR, AND THE ROW SAYS THE RUNTIME DID NOT NAME IT.
///
/// The dearest-in-the-catalogue estimate was the right answer while the model was whatever the CLI's
/// own configuration file said: nothing could identify it, so the ceiling had to assume the worst.
/// It is the wrong answer now. TradeAgent puts <c>-m</c> on the command line itself, so the model is
/// a fact the app wrote down — and charging <c>gpt-6-astra</c>'s 10.00/50.00 for a turn the app asked
/// <c>gpt-5.6-sol</c> (4.00/20.00) to run over-states the bill by two and a half times and stops the
/// AI two and a half times too early.
///
/// It is still not a bill, so it is still labelled: measured on codex-cli 0.153.4 twice — 2026-09-06
/// and 2026-09-07 — the stream names no model even with the flag on the command line, so nothing here
/// can prove which model actually served the turn.
///
/// The estimate stays exactly where it was earned: a runtime with no model flag at all.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class RequestedModelPriceTests : IDisposable
{
    public void Dispose()
    {
        if (File.Exists(CostCatalog.OverridePath)) File.Delete(CostCatalog.OverridePath);
    }

    /// <summary>The measured Codex turn: 17,232 input, 12,928 of it cached, 6 output, NO model named.</summary>
    static readonly TurnUsage Measured = new(17232, 12928, 0, 6, 0, null);

    /// <summary>gpt-5.6-sol on the shipped catalogue: 4.00 in, 0.40 cached, 5.00 writes, 20.00 out.</summary>
    static readonly decimal AtSol = (4304m * 4.00m + 12928m * 0.40m + 6m * 20.00m) / 1_000_000m;

    /// <summary>gpt-6-astra, the dearest codex entry: 10.00 in, 1.00 cached, 12.50 writes, 50.00 out.</summary>
    static readonly decimal AtAstra = (4304m * 10.00m + 12928m * 1.00m + 6m * 50.00m) / 1_000_000m;

    /// <summary>
    /// THE GUARD. Asked for <c>gpt-5.6-sol</c>, the turn is charged at sol's list price and the
    /// figure carries the sentence saying the runtime named nothing. Without the requested model —
    /// which is the state of a runtime that takes no model flag — it is the dearest entry, as before.
    /// </summary>
    [Fact]
    public void A_turn_whose_stream_names_no_model_is_priced_at_the_model_tradeagent_asked_for()
    {
        var asked = CostCatalog.Price(Measured, "codex", requestedModel: "gpt-5.6-sol");
        Assert.Equal(AtSol, asked.Cost);
        Assert.Null(asked.Unpriced);
        Assert.Equal(Labels.PricedAtTheModelAskedFor("gpt-5.6-sol"), asked.Estimated);
        Assert.Contains("gpt-5.6-sol", asked.Estimated!);
        Assert.Contains(ListPrices.ReadOn, asked.Basis!);

        // The estimate stays only where nothing can be asked for.
        var unasked = CostCatalog.Price(Measured, "codex");
        Assert.Equal(AtAstra, unasked.Cost);
        Assert.Equal(Labels.PricedAtHighestListPrice, unasked.Estimated);

        // And the two are genuinely different money: the old reading over-states this turn.
        Assert.True(AtAstra > AtSol * 2m);
    }

    /// <summary>
    /// A model the RUNTIME names still wins. It is the only identification in the system that is not
    /// TradeAgent quoting itself, so a stream that starts naming models is believed over the flag.
    /// </summary>
    [Fact]
    public void A_model_the_runtime_names_itself_still_beats_the_one_that_was_asked_for()
    {
        var named = new TurnUsage(17232, 12928, 0, 6, 0, "gpt-6-astra");
        var price = CostCatalog.Price(named, "codex", requestedModel: "gpt-5.6-sol");

        Assert.Equal(AtAstra, price.Cost);
        // Nothing was estimated: the runtime said which model ran.
        Assert.Null(price.Estimated);
    }

    /// <summary>
    /// A model TradeAgent asked for that this build ships no price for falls back to the dearest
    /// entry rather than becoming unpriced. Over-charging can only stop the AI early; an unpriced
    /// turn cannot reach the ceiling at all, and nothing undoes that.
    /// </summary>
    [Fact]
    public void A_requested_model_with_no_shipped_price_is_charged_high_rather_than_left_unpriced()
    {
        // gpt-5.3-codex-spark is on Codex's own model page and NOT on the pricing page — see ListPrices.
        var price = CostCatalog.Price(Measured, "codex", requestedModel: "gpt-5.3-codex-spark");

        Assert.Equal(AtAstra, price.Cost);
        Assert.Equal(Labels.PricedAtHighestListPrice, price.Estimated);
        Assert.Null(price.Unpriced);
    }

    /// <summary>The owner's own two numbers are still the last word, whatever was asked for.</summary>
    [Fact]
    public void The_owners_rate_still_beats_the_model_that_was_asked_for()
    {
        var price = CostCatalog.Price(Measured, "codex",
            owner: new OwnerPrice(1m, 4m), requestedModel: "gpt-5.6-sol");

        Assert.Equal((17232m * 1m + 6m * 4m) / 1_000_000m, price.Cost);
        Assert.True(price.ByOwner);
        Assert.Null(price.Estimated);
    }

    /// <summary>The card names the model beside the figure. Words only; nothing here touches a colour.</summary>
    [Fact]
    public void The_cards_cost_line_names_the_model()
    {
        var line = DashboardPage.MissionCost(new AiSpendToday
        {
            Metered = true, CanPrice = true, Spent = 1.25m, Cap = 5m, Currency = "USD", Turns = 4,
            Model = "gpt-5.6-sol", ResumesAt = DateTimeOffset.Now.AddHours(4)
        });

        Assert.Contains("1.25 USD", line);
        Assert.Contains("gpt-5.6-sol", line);

        // And says nothing about a model where TradeAgent asked for none.
        var noModel = DashboardPage.MissionCost(new AiSpendToday
        {
            Metered = true, CanPrice = true, Spent = 1.25m, Cap = 5m, Currency = "USD", Turns = 4,
            ResumesAt = DateTimeOffset.Now.AddHours(4)
        });
        Assert.DoesNotContain("running", noModel);
    }

    /// <summary>
    /// And it reaches the agent, as a READ. There is no op that changes it: the model is the account
    /// owner's choice, made in a window the agent cannot reach.
    /// </summary>
    [Fact]
    public async Task The_status_carries_the_model_and_the_schema_says_it_is_the_apps_choice()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;

        gw.Ai = () => new AiActivity("working", 4, 0.3125m) { Model = "gpt-5.6-sol" };
        var status = await gw.StatusAsync();

        Assert.Equal("gpt-5.6-sol", status.AiModel);
        Assert.Contains("\"ai_model\":\"gpt-5.6-sol\"", Json.Write(status));

        var described = Json.Write(GatewaySchema.Ops().Single(o => o.Op == Ops.Status));
        Assert.Contains("ai_model", described);
        Assert.Contains("no operation here that changes it", described);

        // Absent, not empty, where TradeAgent asked for no model at all.
        gw.Ai = () => new AiActivity("working", 4, 0.3125m);
        Assert.DoesNotContain("ai_model", Json.Write(await gw.StatusAsync()));
    }
}

using System.Globalization;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE GPT-6 ROWS, READ FROM THE VENDOR'S OWN PAGE ON A NAMED DAY.
///
/// Before them a turn on <c>gpt-6-luna</c> — the executor's model in <c>docs/ORGANISATION.md</c> — found
/// no price, so the reservation took the runtime's DEAREST row: <c>gpt-6-astra</c> on <c>codex</c>,
/// 16.00 against the 5.00 daily ceiling a fresh installation ships with. A turn the vendor bills at
/// about a cent was refused before it started, which is a cap that stops the work rather than one
/// that bounds it.
///
/// What is asserted is the shape <see cref="ShippedListPriceTests"/> allows — dated, sourced, absent
/// where the vendor publishes nothing — plus the reservations whose arithmetic must have one answer.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class PriceRowsTests : IDisposable
{
    public void Dispose() => NoCosts();

    static void NoCosts()
    {
        if (File.Exists(CostCatalog.OverridePath)) File.Delete(CostCatalog.OverridePath);
    }

    /// <summary>
    /// THE RED. <c>gpt-6-luna</c> as read from <see cref="ListPrices.OpenAiPrices"/> on 2026-10-02: 0.10
    /// input, 0.01 cached, 0.125 cache writes, 0.50 output per million. The default allowance is
    /// 1,200,000 input at the dearer of 0.10 and 0.125 plus 20,000 output at 0.50 — 0.15 + 0.01, the
    /// formula <see cref="CostCatalog.Reserve"/> states, done here rather than trusted. On the base
    /// there was no row and the reservation was astra's 16.00, labelled as the highest list price.
    /// </summary>
    [Fact]
    public void A_gpt_6_luna_turn_reserves_at_its_own_rate_not_the_dearest_row()
    {
        NoCosts();
        var reservation = CostCatalog.Reserve(TurnAllowance.Default, "codex", requestedModel: "gpt-6-luna");

        Assert.Equal(1_200_000m * 0.125m / 1_000_000m + 20_000m * 0.50m / 1_000_000m, reservation.Cost);
        Assert.Equal(0.16m, reservation.Cost);
        Assert.Null(reservation.Unpriced);
        Assert.Equal(Labels.PricedAtTheModelAskedFor("gpt-6-luna"), reservation.Estimated);
        Assert.Contains(ListPrices.ReadOn, reservation.Basis!);
    }

    /// <summary>
    /// The three GPT-6 ids the Codex model page names beside astra are priced wherever TradeAgent can
    /// ask for them — <c>codex</c>, whose catalogue is restricted to that page, and the two runtimes that
    /// take the whole table — and a turn asked for one is charged at its own row, not the estimate.
    /// No figure is pinned here: the one with an answer that must not drift is pinned above.
    /// </summary>
    [Theory]
    [InlineData("gpt-6.1-sol")]
    [InlineData("gpt-6-luna")]
    [InlineData("gpt-6-sol")]
    public void A_gpt_6_model_the_codex_page_lists_is_priced_at_its_own_row_on_every_runtime(string model)
    {
        NoCosts();
        foreach (var runtime in new[] { "codex", "opencode", ApiAgentRuntime.RuntimeId })
        {
            Assert.Contains(ListPrices.All, p => p.Runtime == runtime && p.Model == model);

            var asked = CostCatalog.Reserve(TurnAllowance.Default, runtime, requestedModel: model);
            var unidentified = CostCatalog.Reserve(TurnAllowance.Default, runtime);

            Assert.Equal(Labels.PricedAtTheModelAskedFor(model), asked.Estimated);
            Assert.True(asked.Cost < unidentified.Cost,
                $"{model} on {runtime}: {asked.Cost} is not below the estimate {unidentified.Cost}");
        }
    }

    /// <summary>
    /// THE GUARD, green on the base. <see cref="ListPrices.ReadOn"/> dates EVERY row, so moving it says
    /// every row was re-read that day; a row carrying any other day, or naming any other page, would
    /// make that one constant a claim about rows it does not describe.
    /// </summary>
    [Fact]
    public void Every_built_in_row_carries_the_read_on_day()
    {
        Assert.True(DateOnly.TryParseExact(ListPrices.ReadOn, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _), $"ReadOn is not a day: '{ListPrices.ReadOn}'");

        Assert.NotEmpty(ListPrices.All);
        Assert.All(ListPrices.All, p =>
        {
            Assert.Equal(ListPrices.ReadOn, p.PricedAt);
            Assert.Equal(ListPrices.OpenAiPrices, p.Source);
        });
    }

    /// <summary>
    /// CHEAPER ROWS DO NOT MOVE THE ESTIMATE FOR AN UNIDENTIFIED TURN. <see cref="CostCatalog.Highest"/>
    /// takes the dearest row by output then input, so rows added below it leave it where it was:
    /// astra on <c>codex</c>, 1,200,000 × 12.50 + 20,000 × 50.00 = 16.00; <c>gpt-5.5-pro</c> on opencode
    /// and the harness, 1,200,000 × 30.00 + 20,000 × 180.00 = 39.60. Green on the base, and meant to
    /// stay so: a row that moved either figure would be a change to every turn nothing names.
    /// </summary>
    [Fact]
    public void Cheaper_rows_leave_the_estimate_for_an_unidentified_turn_where_it_was()
    {
        NoCosts();

        var codex = CostCatalog.Reserve(TurnAllowance.Default, "codex");
        Assert.Equal(16.00m, codex.Cost);
        Assert.Equal(Labels.PricedAtHighestListPrice, codex.Estimated);
        Assert.Equal("gpt-6-astra", CostCatalog.Highest(CostCatalog.BuiltIn(), "codex")!.Model);

        foreach (var runtime in new[] { "opencode", ApiAgentRuntime.RuntimeId })
        {
            var reservation = CostCatalog.Reserve(TurnAllowance.Default, runtime);
            Assert.Equal(39.60m, reservation.Cost);
            Assert.Equal(Labels.PricedAtHighestListPrice, reservation.Estimated);
            Assert.Equal("gpt-5.5-pro", CostCatalog.Highest(CostCatalog.BuiltIn(), runtime)!.Model);
        }
    }
}

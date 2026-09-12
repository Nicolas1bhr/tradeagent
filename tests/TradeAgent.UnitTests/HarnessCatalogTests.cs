using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEM 1, THE HALF THAT IS DATA — the app-owned harness is a runtime this build ships, priced by the
/// list this build already carries.
///
/// <para><b>Why this is a test and not a look at the file.</b> Three separate things have to agree
/// before a role can be put on the harness at all, and they live in three places: the manifest
/// (<c>RuntimeCatalog</c>), the prices (<c>ListPrices</c>) and the reservation
/// (<c>CostCatalog.Reserve</c>). A harness with no priced models reserves nothing, and a reservation
/// of nothing is the exact hole <c>U-prices</c> closed for the CLI: the owner's daily ceiling holds
/// nothing back, because an unpriced turn can never reach it.</para>
///
/// <para>Every assertion below is written against the ids as STRINGS rather than against the new
/// runtime class, so that it is the same test before and after the code it is about exists.</para>
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class HarnessCatalogTests
{
    /// <summary>The harness's runtime id, as the owner's settings and the ledger spell it.</summary>
    const string Harness = "openai-api";

    /// <summary>The model the manifest defaults a worker to. Cheapest current entry on the list.</summary>
    const string Worker = "gpt-5.6-luna";

    static void NoOverride()
    {
        if (File.Exists(RuntimeCatalog.OverridePath)) File.Delete(RuntimeCatalog.OverridePath);
        if (File.Exists(CostCatalog.OverridePath)) File.Delete(CostCatalog.OverridePath);
    }

    /// <summary>
    /// THE RED. <c>Require</c> is the one path every start goes through, and before this unit it threw
    /// AI_RUNTIME_NOT_FOUND for the harness: there was no manifest, so there was no way to put a role
    /// on it at all.
    /// </summary>
    [Fact]
    public void The_catalogue_ships_the_app_owned_harness_as_a_runtime()
    {
        NoOverride();
        var manifest = RuntimeCatalog.Require(Harness);

        Assert.Equal(Harness, manifest.Id);
        // It is a harness rather than a program: no executable to find and no installer to run. Those
        // two facts are what ApiAgentRuntime's honest refusals are built on.
        Assert.Equal("", manifest.Executable);
        Assert.Equal(InstallKind.None, manifest.Install.Kind);
        Assert.Equal(Worker, manifest.DefaultModel);
    }

    /// <summary>
    /// A HARNESS NAMES ITS MODEL IN THE REQUEST BODY, so an empty <c>ModelArgs</c> must not answer
    /// "this runtime's model cannot be chosen". If it did, the reservation would be priced at the
    /// dearest model in the catalogue while the turn ran on the cheapest.
    /// </summary>
    [Fact]
    public void The_harness_resolves_a_model_although_it_has_no_command_line()
    {
        NoOverride();
        var manifest = RuntimeCatalog.Require(Harness);

        Assert.Empty(manifest.ModelArgs);
        Assert.Equal(Worker, manifest.ModelFor(null));
        Assert.Equal("gpt-5.6-sol", manifest.ModelFor("gpt-5.6-sol"));
    }

    /// <summary>
    /// THE PRICES. The whole current generation is priced for this runtime, because nothing restricts
    /// which of them the app may ask for — and the Safety page's model row is built from exactly these
    /// entries, so a runtime with none would show the owner an empty choice.
    /// </summary>
    [Fact]
    public void Every_model_this_build_prices_is_priced_for_the_harness_too()
    {
        var forHarness = ListPrices.All.Where(p => p.Runtime == Harness).ToList();
        var forOpenCode = ListPrices.All.Where(p => p.Runtime == "opencode").ToList();

        Assert.NotEmpty(forHarness);
        Assert.Equal(forOpenCode.Count, forHarness.Count);
        Assert.Contains(forHarness, p => p.Model == Worker);

        // Dated and sourced, like every other entry: a price with no date is a price nobody can check.
        foreach (var p in forHarness)
        {
            Assert.Equal(ListPrices.ReadOn, p.PricedAt);
            Assert.StartsWith("https://", p.Source);
        }
    }

    /// <summary>
    /// THE RESERVATION, which is what makes the daily ceiling a cap rather than a report. Priced at
    /// the model TradeAgent will actually ask for, and a real number rather than "unpriced".
    /// </summary>
    [Fact]
    public void A_turn_on_the_harness_can_be_reserved_at_the_model_it_will_run_on()
    {
        NoOverride();
        var reservation = CostCatalog.Reserve(TurnAllowance.Default, Harness, requestedModel: Worker);

        Assert.Null(reservation.Unpriced);
        Assert.NotNull(reservation.Cost);
        // 1,200,000 input at the dearer of 0.20 and the 0.25 cache-write rate, plus 20,000 output at
        // 1.20 — the formula CONTRACTS.md states, arithmetic done here rather than trusted.
        Assert.Equal(1_200_000m * 0.25m / 1_000_000m + 20_000m * 1.20m / 1_000_000m, reservation.Cost);
    }
}

using Avalonia.Controls;
using Avalonia.LogicalTree;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;
using static TradeAgent.Tests.Unit.OrgSurfaceSeed;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE COSTS PAGE (<c>U-org-surface-a</c> item 4; R24 § 4.7): the words, tested without a window (<see cref="CostsWords"/>), and the page built once and
/// updated in place over a launch ledger the test seeded. No real model: launches are rows written through <see cref="AiAttemptStore.Begin"/>.
/// </summary>
public class CostsPageTests
{
    static OrgSurfaceSources Sources(Database db) => new()
    {
        Org = () => new OrgStore(db),
        Attempts = () => new AiAttemptStore(db),
        Currency = () => "USD",
    };

    static IEnumerable<TextBlock> Texts(Control c) => c.GetLogicalDescendants().OfType<TextBlock>();

    static CostsWords Words(Database db) => CostsWords.Of(CostsRead.Of(new OrgStore(db), new AiAttemptStore(db), DateTimeOffset.UtcNow, "USD"), null);

    static DateTimeOffset Today() => OwnerDay.Window(DateTimeOffset.UtcNow).From.AddMinutes(30);

    /// <summary>The day's launches: the team spends on the plan, on a key, on neither known; one plan turn reports no cost, and one key turn is in flight.</summary>
    static void Seed(Database db)
    {
        Chart(db);
        var at = Today();
        Priced(db, Launch(db, Head, at, SeatClasses.Cli, BillingClasses.Plan), 0.40m);
        Priced(db, Launch(db, Head, at.AddMinutes(1), SeatClasses.Harness, BillingClasses.Key), 0.30m);
        Priced(db, Launch(db, Member, at.AddMinutes(2), SeatClasses.Harness, BillingClasses.Key), 0.20m);
        Priced(db, Launch(db, Member, at.AddMinutes(3), SeatClasses.Cli, BillingClasses.Unknown), 0.05m);
        var noCost = Launch(db, Member, at.AddMinutes(4), SeatClasses.Cli, BillingClasses.Plan, reserved: 0m);
        Launch(db, Other, at.AddMinutes(5), SeatClasses.Harness, BillingClasses.Key, unit: Elsewhere);
        Unreported(db, noCost);
    }

    /// <summary>
    /// (a) COSTS SPLIT EACH UNIT BY BILLING CLASS AND NAME THE TURNS WHOSE COST IS UNKNOWN: three lines to a unit, the plan's at list price and
    /// never as money, a key's as real money, what nobody established as its own; a turn that reported no cost is counted and named beside the
    /// figure it is not in, a turn in flight is shown as held, a class a unit has no turns in says "none today", and a unit's figure includes the
    /// units below it.
    /// RED at base: no <c>CostsWords</c> or <c>CostsPage</c> existed.
    /// </summary>
    [Fact]
    public void Costs_split_each_unit_by_billing_class_and_name_unknown_cost_turns()
    {
        using var db = TestEnv.NewDb();
        Seed(db);

        var words = Words(db);

        Assert.Equal("", words.Status);
        Assert.StartsWith("Today, ", words.Window, StringComparison.Ordinal);
        Assert.Contains("Figures are in USD.", words.Window, StringComparison.Ordinal);
        var team = words.Units.Single(u => u.Id == Team);
        Assert.Equal("Your ChatGPT plan (counted at list price against the plan's capacity; not money charged): 0.4 USD at list price over 2 turns; "
                     + "1 turn reported no cost and is not in that figure.", team.Plan);
        Assert.Equal("An API key (real money): 0.5 USD spent over 2 turns.", team.Key);
        Assert.Equal("Not established (nobody established who pays, so each turn is counted against both limits): 0.05 USD counted over 1 turn.", team.Unknown);

        // A UNIT WITH TURNS IN ONE CLASS ONLY SAYS NONE FOR THE OTHERS — not a zero it never spent.
        var elsewhere = words.Units.Single(u => u.Id == Elsewhere);
        Assert.Contains("none today.", elsewhere.Plan, StringComparison.Ordinal);
        Assert.Equal("An API key (real money): no turn has finished; 1 turn is in flight, holding 0.1 USD.", elsewhere.Key);
        Assert.Contains("none today.", elsewhere.Unknown, StringComparison.Ordinal);

        // THE UNIT ABOVE INCLUDES THE UNITS BELOW IT, class by class.
        var research = words.Units.Single(u => u.Id == OrgIds.Research);
        Assert.Contains("0.4 USD at list price over 2 turns", research.Plan, StringComparison.Ordinal);
        Assert.Contains("0.5 USD spent over 2 turns; 1 turn is in flight, holding 0.1 USD", research.Key, StringComparison.Ordinal);

        // A UNIT THE DAY NEVER TOUCHED SAYS NONE IN EVERY CLASS.
        var operations = words.Units.Single(u => u.Id == OrgIds.Operations);
        Assert.All(new[] { operations.Plan, operations.Key, operations.Unknown }, l => Assert.EndsWith(": none today.", l, StringComparison.Ordinal));

        // THREE FIGURES, NEVER ONE: no line adds two classes, so no line carries the team's whole 0.95.
        Assert.DoesNotContain("0.95", string.Join("\n", words.Units.SelectMany(u => new[] { u.Plan, u.Key, u.Unknown })), StringComparison.Ordinal);
    }

    /// <summary>
    /// (a2) A TURN CHARGED ITS RESERVATION BECAUSE IT REPORTED NO USAGE IS NAMED, so its figure is not read as a bill; and a turn that reported
    /// no cost is never priced at zero.
    /// </summary>
    [Fact]
    public void A_turn_charged_its_reservation_is_named_and_an_unknown_cost_is_never_priced_at_zero()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var at = Today();
        Unreported(db, Launch(db, Head, at, SeatClasses.Cli, BillingClasses.Plan, reserved: 0.10m));
        var words = Words(db);

        var team = words.Units.Single(u => u.Id == Team);
        Assert.Contains("0.1 USD at list price over 1 turn", team.Plan, StringComparison.Ordinal);
        Assert.Contains("1 turn reported no usage and is charged the reservation, so that figure is not a bill", team.Plan, StringComparison.Ordinal);
        Assert.DoesNotContain("reported no cost", team.Plan, StringComparison.Ordinal);
    }

    /// <summary>
    /// (b) THE PAGE IS BUILT ONCE AND UPDATED IN PLACE: after a refresh in which a turn finished, the same unit cards and the same text blocks are on
    /// the page, now saying the new words; a refresh that changed nothing writes nothing.
    /// RED at base: no <c>CostsPage</c> existed.
    /// </summary>
    [Fact]
    public void Costs_are_built_once_and_updated_in_place()
    {
        using var db = TestEnv.NewDb();
        Seed(db);
        var page = new CostsPage(Sources(db));
        Assert.Equal(Labels.SurfaceNotRead, page.StatusText.Text);
        page.Update();
        var root = page.Root;
        var team = page.CardOf(Team) ?? throw new Xunit.Sdk.XunitException("the team's card was not made");
        var before = Texts(team).ToList();
        var cardsBefore = page.UnitCards.Children.ToList();
        Assert.Contains("0.5 USD spent", string.Join("\n", before.Select(t => t.Text)), StringComparison.Ordinal);

        page.Update();   // nothing moved
        Assert.Equal(cardsBefore, page.UnitCards.Children.ToList());

        Priced(db, Launch(db, Member, Today().AddMinutes(10), SeatClasses.Harness, BillingClasses.Key), 0.25m);
        page.Update();

        Assert.Same(root, page.Root);
        Assert.Same(team, page.CardOf(Team));
        Assert.Equal(before, Texts(team).ToList());   // the same text blocks, in the same places
        Assert.Contains("0.75 USD spent over 3 turns", string.Join("\n", Texts(team).Select(t => t.Text)), StringComparison.Ordinal);
        Assert.Equal(cardsBefore, page.UnitCards.Children.ToList());
    }

    /// <summary>(c) A STORE THAT CANNOT BE READ IS SAID, NEVER SHOWN AS A DAY WITH NO COSTS.</summary>
    [Fact]
    public void Unreadable_costs_are_said_and_never_shown_as_none()
    {
        var words = CostsWords.Of(null, "the file is closed");
        Assert.Equal(Labels.SurfaceUnreadable("the file is closed"), words.Status);
        Assert.Empty(words.Units);
        Assert.Equal(Labels.SurfaceNotRead, CostsWords.Of(null, null).Status);
    }
}

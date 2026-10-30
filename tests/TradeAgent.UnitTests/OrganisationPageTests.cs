using Avalonia.Controls;
using Avalonia.LogicalTree;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;
using static TradeAgent.Tests.Unit.OrgSurfaceSeed;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE CHART PAGE (<c>U-org-surface-a</c> item 2; R24 § 4.7): the words, tested without a window (<see cref="OrganisationWords"/>); the page built
/// once and updated in place over a store the test seeded. What is read back is the words and which controls are still the same instances; the
/// layout and the colours are not asserted and not claimed.
/// </summary>
public class OrganisationPageTests
{
    static OrgSurfaceSources Sources(Database db, bool proof = false) => new()
    {
        Org = () => new OrgStore(db),
        Attempts = () => new AiAttemptStore(db),
        Scorecards = () => new Scorecards(db),
        ProofHeld = () => proof,
        Currency = () => "USD",
    };

    static IEnumerable<TextBlock> Texts(Control c) => c.GetLogicalDescendants().OfType<TextBlock>();

    static string AllText(Control c) => string.Join("\n", Texts(c).Select(t => t.Text));

    /// <summary>
    /// (a) THE CHART SHOWS EACH POSITION WITH ITS SEAT, THE CELL CLASS OF ITS LAST LAUNCH AND WHO PAID, IN PLAIN WORDS: units depth first from the
    /// organisation, a dormant position called dormant, a position that never launched saying so rather than showing a default, the boundary
    /// as a sentence and never as an enum name, and whether a cell proof is recorded.
    /// RED at base: no <c>OrganisationWords</c> or <c>OrganisationPage</c> existed.
    /// </summary>
    [Fact]
    public void The_chart_shows_each_position_with_seat_cell_class_and_billing_in_plain_words()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        Seat(db, "pos-5ac0ffee0009", Team, OrgPositionStatus.Dormant);
        Exec(db, "UPDATE org_position SET seat_runtime='codex', seat_model='gpt-6' WHERE id=$id", ("$id", Head));
        var t = DateTimeOffset.UtcNow.AddHours(-2);
        Launch(db, Head, t, SeatClasses.Harness, BillingClasses.Key);
        Launch(db, Member, t, SeatClasses.Cli, BillingClasses.Plan);
        Launch(db, CouncilRoles.Research, t, SeatClasses.Cli, BillingClasses.Unknown, unit: null);

        var words = OrganisationWords.Of(OrganisationRead.From(new OrgStore(db), new AiAttemptStore(db), proofHeld: false), null);

        // DEPTH FIRST FROM THE ORGANISATION: the root, then each division with what hangs from it.
        Assert.Equal(OrgIds.Root, words.Units[0].Id);
        Assert.Equal("The organisation", words.Units[0].Title);
        Assert.Contains("headed by you", words.Units[0].Line, StringComparison.Ordinal);
        var ids = words.Units.Select(u => u.Id).ToList();
        Assert.True(ids.IndexOf(OrgIds.Research) < ids.IndexOf(Team) && ids.IndexOf(Team) < ids.IndexOf(Elsewhere));
        Assert.Equal(0, words.Units[0].Depth);
        Assert.Equal(2, words.Units.Single(u => u.Id == Team).Depth);
        Assert.Equal("Research division", words.Units.Single(u => u.Id == OrgIds.Research).Title);

        var team = words.Units.Single(u => u.Id == Team);
        var head = team.Positions.Single(p => p.Id == Head);
        Assert.Equal("active", head.Status);
        Assert.Equal("head of Team team-5ac0ffee", head.Role);
        Assert.Equal("Set to run on: codex, model gpt-6.", head.Seat);
        Assert.Contains("brokered: the app's own harness", head.Cell, StringComparison.Ordinal);
        Assert.Equal("Paid from: real money (an API key).", head.Billing);

        var member = team.Positions.Single(p => p.Id == Member);
        Assert.Equal("Set to run on: nothing of its own, and where it runs is not resolved here.", member.Seat);
        Assert.Contains("unconfined: a command-line seat started as you", member.Cell, StringComparison.Ordinal);
        Assert.Equal("Paid from: your ChatGPT plan's capacity.", member.Billing);

        var dormant = team.Positions.Single(p => p.Id == "pos-5ac0ffee0009");
        Assert.Equal("dormant", dormant.Status);
        // NEVER LAUNCHED, IN ONE SENTENCE (U-ui-pages): no seat boundary and no payer are recorded, and no second "nothing yet" line is drawn.
        Assert.Equal("Has not run yet, so nothing is recorded of where it ran or who paid.", dormant.Cell);
        Assert.Equal("", dormant.Billing);

        var research = words.Units.Single(u => u.Id == OrgIds.Research).Positions.Single(p => p.Id == CouncilRoles.Research);
        Assert.Equal("Paid from: not known, so counted against both limits.", research.Billing);

        // NO ENUM NAME OR DATABASE WORD REACHES THE OWNER, AND THE PROOF IS SAID EITHER WAY.
        var everything = string.Join("\n", words.Units.SelectMany(u => u.Positions).SelectMany(p => new[] { p.Seat, p.Cell, p.Billing }));
        foreach (var raw in new[] { "Unconfined", "Brokered", "Mediated", "Confined", "HARD_", "SeatAssurance" })
            Assert.DoesNotContain(raw, everything, StringComparison.Ordinal);
        Assert.Equal(Labels.CellProofNone, words.Proof);
        Assert.Equal(Labels.CellProofHeld, OrganisationWords.Of(OrganisationRead.From(new OrgStore(db), new AiAttemptStore(db), proofHeld: true), null).Proof);
    }

    /// <summary>
    /// (a2) A POSITION WITH NO SEAT OF ITS OWN SHOWS WHAT ITS LAUNCH REALLY RESOLVES TO, never a default: Research, whose seat is the Safety page's, shows
    /// its runtime and model as the app resolves them; a seat that cannot be resolved says why; and with nothing to resolve it, the page says
    /// it does not know here rather than naming a runtime.
    /// RED at base: it said "the app's default runtime and model" for every position with empty seat columns.
    /// </summary>
    [Fact]
    public void A_position_without_its_own_seat_shows_what_its_launch_resolves_to_and_never_a_default()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        ResolvedSeat Resolve(string role) => role switch
        {
            CouncilRoles.Research => new ResolvedSeat("api", "model-r", null),
            Member => new ResolvedSeat(null, null, "its seat names a program this computer does not run"),
            _ => new ResolvedSeat("codex", null, null)
        };

        var words = OrganisationWords.Of(OrganisationRead.From(new OrgStore(db), new AiAttemptStore(db), proofHeld: false, seatOf: Resolve), null);

        string Seat(string unit, string position) => words.Units.Single(u => u.Id == unit).Positions.Single(p => p.Id == position).Seat;
        Assert.Equal("Resolves to: api, model model-r (it has no seat of its own; the app's settings for the role decide).",
            Seat(OrgIds.Research, CouncilRoles.Research));
        Assert.Equal("Resolves to: codex, no model named (it has no seat of its own; the app's settings for the role decide).",
            Seat(Team, Head));
        Assert.Equal("Its seat cannot be resolved: its seat names a program this computer does not run.", Seat(Team, Member));
        Assert.DoesNotContain("default", Seat(Team, Head), StringComparison.OrdinalIgnoreCase);

        // NOTHING TO RESOLVE IT WITH: it does not know, and does not say a runtime.
        var blind = OrganisationWords.Of(OrganisationRead.From(new OrgStore(db), new AiAttemptStore(db), proofHeld: false), null);
        Assert.Equal("Set to run on: nothing of its own, and where it runs is not resolved here.",
            blind.Units.Single(u => u.Id == Team).Positions.Single(p => p.Id == Head).Seat);
    }

    /// <summary>
    /// (b) A RECORD THAT CANNOT BE READ IS SAID, NEVER SHOWN AS AN EMPTY ORGANISATION: the words name the reason and that nothing below is
    /// current, and a page with no store says it has not read yet.
    /// </summary>
    [Fact]
    public void An_unreadable_chart_is_said_and_never_shown_as_an_empty_one()
    {
        var unreadable = OrganisationWords.Of(null, "the units hang from a loop");
        Assert.Equal(Labels.SurfaceUnreadable("the units hang from a loop"), unreadable.Status);
        Assert.Empty(unreadable.Units);
        Assert.Equal(Labels.SurfaceNotRead, OrganisationWords.Of(null, null).Status);
    }

    /// <summary>
    /// (b2) A DAMAGED CHART IS REFUSED IN WORDS, NEVER DRAWN AS A HIERARCHY IT IS NOT: two units that hang from each other, with the real root
    /// beside them, say so by name and draw no unit — not the pair as two more roots; and the Costs page, which walks the same chart, says the
    /// same and lists none.
    /// RED at base: the pair was drawn as two extra roots.
    /// </summary>
    [Fact]
    public void A_damaged_chart_is_refused_in_words_and_no_false_root_is_drawn()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        Exec(db, "UPDATE org_unit SET parent_id=$b WHERE id=$a", ("$a", Team), ("$b", Elsewhere));
        Exec(db, "UPDATE org_unit SET parent_id=$a WHERE id=$b", ("$a", Team), ("$b", Elsewhere));

        var words = OrganisationWords.Of(OrganisationRead.From(new OrgStore(db), new AiAttemptStore(db), proofHeld: false), null);

        Assert.Contains("the chart is damaged", words.Status, StringComparison.Ordinal);
        Assert.Contains(Team, words.Status, StringComparison.Ordinal);
        Assert.Contains(Elsewhere, words.Status, StringComparison.Ordinal);
        Assert.Empty(words.Units);

        var costs = CostsWords.Of(CostsRead.Of(new OrgStore(db), new AiAttemptStore(db), DateTimeOffset.UtcNow, "USD"), null);
        Assert.Contains("the chart is damaged", costs.Status, StringComparison.Ordinal);
        Assert.Empty(costs.Units);

        // AND A UNIT THAT HANGS FROM NOTHING THE CHART HOLDS IS NAMED TOO (a foreign key forbids writing one, so the read is handed it).
        var at = DateTimeOffset.UtcNow;
        OrgUnitRow Unit(string id, string? parent) => new(id, parent, parent is null ? "root" : "team", null, "active", null, null, at, null);
        var lost = new OrganisationRead([Unit("org", null), Unit("team-lost", "no-such-unit")], [], new Dictionary<string, RecordedSeat>(), false);
        var stray = OrganisationWords.Of(lost, null);
        Assert.Contains("the chart is damaged", stray.Status, StringComparison.Ordinal);
        Assert.Contains("unit team-lost hangs from no-such-unit", stray.Status, StringComparison.Ordinal);
        Assert.Empty(stray.Units);
    }

    /// <summary>
    /// (c) THE PAGE IS BUILT ONCE AND UPDATED IN PLACE: after a refresh that changed a position's status, its seat and its billing class, and one
    /// that hired a position, the same unit card and the same text blocks are on the page, now saying the new words; a refresh that changed
    /// nothing writes nothing.
    /// RED at base: no <c>OrganisationPage</c> existed.
    /// </summary>
    [Fact]
    public void The_chart_is_built_once_and_updated_in_place()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        Launch(db, Head, DateTimeOffset.UtcNow.AddHours(-2), SeatClasses.Harness, BillingClasses.Key);
        var page = new OrganisationPage(Sources(db));
        Assert.Equal(Labels.SurfaceNotRead, page.StatusText.Text);

        page.Update();
        var root = page.Root;
        var team = page.CardOf(Team) ?? throw new Xunit.Sdk.XunitException("the team's card was not made");
        var before = Texts(team).ToList();
        var cardsBefore = page.UnitCards.Children.ToList();
        Assert.Contains("brokered", AllText(team), StringComparison.Ordinal);
        Assert.Contains("active", AllText(team), StringComparison.Ordinal);

        // NOTHING MOVED: nothing is touched.
        page.Update();
        Assert.Equal(cardsBefore, page.UnitCards.Children.ToList());

        // WHAT MOVED: a status, a newer launch on another seat paid another way, and a hire.
        Exec(db, "UPDATE org_position SET status='dormant' WHERE id=$id", ("$id", Member));
        Launch(db, Head, DateTimeOffset.UtcNow.AddMinutes(-5), SeatClasses.Cli, BillingClasses.Plan);
        Seat(db, "pos-5ac0ffee0009", Team);
        page.Update();

        Assert.Same(root, page.Root);
        Assert.Same(team, page.CardOf(Team));
        Assert.Equal(before, Texts(team).Take(before.Count).ToList());   // the same text blocks, in the same places
        Assert.Contains("unconfined", AllText(team), StringComparison.Ordinal);
        Assert.Contains("your ChatGPT plan's capacity", AllText(team), StringComparison.Ordinal);
        Assert.Contains("dormant", AllText(team), StringComparison.Ordinal);
        Assert.Contains("pos-5ac0ffee0009", AllText(team), StringComparison.Ordinal);
        // THE OTHER UNITS' CARDS ARE THE SAME INSTANCES, IN THE SAME ORDER.
        Assert.Equal(cardsBefore, page.UnitCards.Children.ToList());
    }

    /// <summary>
    /// (d) EACH POSITION SHOWS ITS CALIBRATION IN WORDS, WITHOUT A REBUILD (<c>U-org-scorecards-a</c> a5): before any forecast resolves the head's
    /// row says none has; after the head's forecast resolves — 0.8 on a verdict that came, against a base rate of one half over nothing
    /// measured — the same row, in the same card and the same text blocks, says one scored forecast, its Brier, the base rate's and the
    /// skill, and the forecast still open apart. The other head, which forecast nothing, still says it has none. The line is the app's
    /// measurement: no probability, which is the forecaster's claim, is printed in it.
    /// RED at base: the page drew no calibration.
    /// </summary>
    [Fact]
    public void Each_position_shows_its_calibration_in_words_without_a_rebuild()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var page = new OrganisationPage(Sources(db));
        page.Update();
        var team = page.CardOf(Team) ?? throw new Xunit.Sdk.XunitException("the team's card was not made");
        var before = Texts(team).ToList();
        Assert.Contains("Calibration: no scored forecast of its has resolved yet.", AllText(team), StringComparison.Ordinal);

        var at = DateTimeOffset.UtcNow.AddDays(-10);
        OrgScorecardTests.Version(db, "ver-cal");
        OrgScorecardTests.Version(db, "ver-later");
        new OrgStore(db).Decide(Head, null, OrgDecisionKind.Routine, ["forecast it"], "forecast it", "a reason", [],
            [
                new ForecastClaim(ForecastVocabulary.VerdictClass, "ver-cal", PromotionVerdict.Promoted, at.AddDays(2), 0.8m),
                new ForecastClaim(ForecastVocabulary.VerdictClass, "ver-later", PromotionVerdict.Promoted, at.AddDays(300), 0.35m)
            ], at);
        OrgScorecardTests.Verdict(db, "ver-cal", PromotionVerdict.Promoted, at.AddDays(1));
        new ForecastResolver(db).Sweep(DateTimeOffset.UtcNow);
        page.Update();

        Assert.Same(team, page.CardOf(Team));
        Assert.Equal(before, Texts(team).Take(before.Count).ToList());   // the same text blocks, in the same places
        var words = AllText(team);
        Assert.Contains("Calibration: 1 scored forecast resolved — Brier 0.040 against the base rate's 0.250, skill +0.210; 1 open.", words,
            StringComparison.Ordinal);
        Assert.DoesNotContain("0.8", words, StringComparison.Ordinal);
        Assert.DoesNotContain("0.35", words, StringComparison.Ordinal);
        Assert.Contains("Calibration: no scored forecast of its has resolved yet.", AllText(page.CardOf(Elsewhere)!), StringComparison.Ordinal);
    }
}

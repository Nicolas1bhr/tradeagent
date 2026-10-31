using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;
using static TradeAgent.Tests.Unit.OrgSurfaceSeed;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE ORGANISATION-FIRST PAGES IN THE SHELL (<c>U-org-surface-a</c> item 5): where they sit in the rail, that the window builds and refreshes them,
/// and that none of them holds a press. They only READ — operator authority (mode, kill switch, live activation, approvals, updates) is in-process
/// and lives on its own pages, and nothing on these three can be pressed, typed in or chosen from.
/// </summary>
public class OrgSurfaceShellTests
{
    static string Source(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "TradeAgent.App", file));
    }

    /// <summary>
    /// (a) THE RAIL LISTS THE PAGES IN ORDER: Chat, then Organisation, Work and Costs under the words the labels spell, before every older page —
    /// and each is built into the page host and refreshed by the window's own tick, so a page in the rail is a page that exists and is kept current.
    /// A source assertion, as the two-press factories' is (<c>TwoPressGrantTests</c>): the window itself needs a platform no unit test has.
    /// RED at base: the rail held Chat, Dashboard and the older pages only.
    /// </summary>
    [Fact]
    public void The_rail_lists_the_pages_in_order()
    {
        var text = Source("MainWindow.cs");
        var entries = MainWindow.Rail.Select(r => (Page: r.Page.ToString(), Label: r.Label)).ToList();

        Assert.Equal(new[] { "Chat", "Organisation", "Work", "Costs" }, entries.Take(4).Select(e => e.Page));
        Assert.Equal(new[] { "Chat", Labels.NavOrganisation, Labels.NavWork, Labels.NavCosts }, entries.Take(4).Select(e => e.Label));
        Assert.Equal(new[] { "Organisation", "Work", "Costs" }, new[] { Labels.NavOrganisation, Labels.NavWork, Labels.NavCosts });
        Assert.Equal(entries.Count, entries.Select(e => e.Page).Distinct().Count());   // no page twice

        // BUILT, PLACED AND KEPT CURRENT: the page is made, put in the host and updated by the window's tick.
        foreach (var (page, type, field) in new[] { ("Organisation", "OrganisationPage", "_organisation"), ("Work", "WorkPage", "_work"), ("Costs", "CostsPage", "_costs") })
        {
            Assert.Contains($"{field} = new {type}(", text, StringComparison.Ordinal);
            Assert.Contains($"Add(Page.{page}, {field}.Root);", text, StringComparison.Ordinal);
            Assert.Contains($"{field}?.Update();", text, StringComparison.Ordinal);
        }
    }

    static readonly Type[] Presses =
    [
        typeof(Button), typeof(ToggleButton), typeof(TextBox), typeof(ComboBox), typeof(Slider), typeof(NumericUpDown), typeof(ListBox),
        typeof(TabControl), typeof(MenuItem), typeof(CalendarDatePicker), typeof(AutoCompleteBox)
    ];

    static IReadOnlyList<string> PressesIn(Control root) => [.. root.GetLogicalDescendants().Prepend(root).OfType<Control>()
        .Where(c => Presses.Any(p => p.IsInstanceOfType(c)) || c.ContextMenu is not null)
        .Select(c => c.GetType().Name)];

    /// <summary>
    /// (b) THE ORGANISATION PAGES HOLD NO PRESS: a walk of each built tree — populated, not just its skeleton — finds no button, no editable box and
    /// no choice; and the sources of the three pages name none either, but for the owner's repair of a refused position's folder
    /// (<c>U-repair-position-home</c>; <c>RepairHomeTests</c> holds its row) and his presses over the chief on the organisation's card
    /// (<c>U-org-chief</c>; <c>OrgChiefTests</c> holds them). Pins an absence: watched red with a button added to the Work page.
    /// </summary>
    [Fact]
    public void The_organisation_pages_hold_no_press()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        Mandate(db, "man-0000000000e1", Head, Member, "issued", "delivered");
        Deliver(db, "man-0000000000e1", "done", DateTimeOffset.UtcNow.AddMinutes(-5), "[]", ("sources-cited", true, "ok"));
        Priced(db, Launch(db, Member, OwnerDay.Window(DateTimeOffset.UtcNow).From.AddMinutes(30), mandate: "man-0000000000e1"), 0.25m);
        new OrgStore(db).Decide(Head, "turn-test", "routine", ["a", "b"], "a", "a reason", [], [], DateTimeOffset.UtcNow.AddHours(-1));
        var sources = new OrgSurfaceSources
        {
            Org = () => new OrgStore(db), Attempts = () => new AiAttemptStore(db), Currency = () => "USD", ProofHeld = () => true
        };

        var organisation = new OrganisationPage(sources);
        var work = new WorkPage(sources);
        var costs = new CostsPage(sources);
        foreach (var update in new Action[] { organisation.Update, work.Update, costs.Update }) update();

        Assert.NotEmpty(work.MandateCards.Children);
        Assert.NotEmpty(work.DeliveryCards.Children);
        Assert.NotEmpty(work.DecisionCards.Children);
        Assert.NotEmpty(organisation.UnitCards.Children);
        Assert.NotEmpty(costs.UnitCards.Children);
        Assert.Empty(PressesIn(organisation.Root));
        Assert.Empty(PressesIn(work.Root));
        Assert.Empty(PressesIn(costs.Root));

        foreach (var file in new[] { "OrganisationView.cs", "WorkView.cs", "CostsView.cs", "OrgSurface.cs" })
        {
            var text = Source(file);
            // THE ONE PRESS THESE PAGES HOLD (U-repair-position-home, the brief's item 4): the owner's two-press repair of a refused position's
            // folder, on the Organisation page, drawn only on a row whose folder the last start could not rebuild — so the populated walk above,
            // with no refused folder, still finds none. Exactly one, by its own labels; any other press in these sources stays red.
            if (file == "OrganisationView.cs")
            {
                const string repair = "Ui.Confirm(Labels.RepairHome, Labels.RepairHomeConfirm,";
                Assert.Single(Regex.Matches(text, Regex.Escape(repair)));
                text = text.Replace(repair, "", StringComparison.Ordinal);
                // REVISITED, OUT LOUD, BY U-seat-models (R25 D8): the owner's switch of a position's model — one two-press per model of the
                // verified catalog the position is not on, by its own labels, drawn only when the sources hand the page a ChooseModel press
                // and a non-empty list (the walk above hands neither, so it still finds none). Exactly one, and nowhere else in these sources.
                const string switchModel = "Ui.Confirm(Labels.SeatModelSwitch(words), Labels.SeatModelSwitchConfirm(words),";
                Assert.Single(Regex.Matches(text, Regex.Escape(switchModel)));
                text = text.Replace(switchModel, "", StringComparison.Ordinal);
                // REVISITED, OUT LOUD, BY U-org-chief (item 7; its FILE SEAM gives it the organisation's card): the owner's presses over the
                // chief, in their own class at the end of the file — the four two-press controls by their own labels, and the closed list of
                // seats as plain presses — and nowhere else in the file. The walk above draws none: its sources hand the page no presses.
                var chief = text.IndexOf("sealed class ChiefCard", StringComparison.Ordinal);
                Assert.True(chief > 0, "the chief's card is not where this guard reads it");
                var card = text[chief..];
                foreach (var label in new[] { "ChiefAppoint", "ChiefReplace", "ChiefActivate", "ChiefRemove" })
                    Assert.Single(Regex.Matches(card, Regex.Escape($"Ui.Confirm(Labels.{label}, Labels.{label}Confirm,")));
                Assert.Equal(4, Regex.Matches(card, Regex.Escape("Ui.Confirm(")).Count);
                Assert.Single(Regex.Matches(card, Regex.Escape("new Button")));
                Assert.Single(Regex.Matches(card, Regex.Escape(".Click +=")));
                text = text[..chief];
            }
            foreach (var press in new[] { "new Button", "Ui.Button", "Ui.Primary", "Ui.Secondary", "Ui.Ghost", "Ui.Danger", "Ui.Confirm", "ConfirmIf",
                         "TextField", "AmountField", "new TextBox", ".Click +=", "Ui.KillSwitch", "ModeButton" })
                Assert.DoesNotContain(press, text, StringComparison.Ordinal);
        }
    }
}

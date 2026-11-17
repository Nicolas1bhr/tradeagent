using Avalonia.Controls;
using Avalonia.LogicalTree;
using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE DASHBOARD SHEDS THE PER-ORDER APPROVAL (<c>U-settings-shed</c> item 4; R24 § 4.2, Q9): "Real, ask me first" was the owner's press
/// on each live order, and a press per order cannot become a board act — a board seat approving each order would be model judgement timing
/// it (SIGNAL-001). The live modes are the board's, closed by construction until it is active; no page offers Approve or Decline.
/// </summary>
public class DashboardShedTests
{
    static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }

    [Fact]
    public async Task The_dashboard_offers_no_per_order_approval()
    {
        // NO PAGE OF THE APP CALLS EITHER PRESS'S GATEWAY METHOD, OR READS THE QUEUE THEY ANSWERED.
        foreach (var file in Directory.GetFiles(Path.Combine(Repo(), "src", "TradeAgent.App"), "*.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Gateway.ApproveAsync(", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Gateway.Decline(", text, StringComparison.Ordinal);
            Assert.DoesNotContain("AWAITING_APPROVAL", text, StringComparison.Ordinal);
        }

        // AND THE DASHBOARD AS THE WINDOW BUILDS IT HOLDS NO SUCH PRESS.
        using var db = TestEnv.NewDb();
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            var page = new DashboardPage(host, () => Task.CompletedTask);
            page.Update(await host.Gateway.StatusAsync());
            var presses = page.Root.GetLogicalDescendants().OfType<Button>().Select(b => b.Content as string).ToList();
            Assert.DoesNotContain("Approve", presses);
            Assert.DoesNotContain("Decline", presses);
            Assert.DoesNotContain(page.Root.GetLogicalDescendants().OfType<TextBlock>(),
                t => string.Equals(t.Text, "THE AI IS ASKING PERMISSION", StringComparison.Ordinal));
        }
        finally { await host.Gateway.DisposeAsync(); }
    }
}

/// <summary>
/// THE PLACES (<c>U-ui-world</c>, which took the rail away; before it <c>U-settings-shed</c> item 5 and R24 § 4.7 held the rail's order):
/// the window is one picture and every page is found at the thing it is about — six places in a dock, each page a leaf of exactly one.
/// What the rail's rows held is held here: every page the app has can be reached, once, under the name the app's own sentences call it by.
/// </summary>
public class PlacesTests
{
    [Fact]
    public void Every_page_is_a_leaf_of_exactly_one_place_under_the_name_the_apps_sentences_use()
    {
        Assert.Equal(
            [TradeAgent.Core.Labels.PlaceOverview, TradeAgent.Core.Labels.NavOrganisation, TradeAgent.Core.Labels.NavWork, TradeAgent.Core.Labels.NavCosts,
                "Chat", "Dashboard", "Daily report", TradeAgent.Core.Labels.BoardPage,
                TradeAgent.Core.Labels.OwnerPage, TradeAgent.Core.Labels.NavForYou, "Inbox", "Oversight",
                "Checks", "Activity", "Settings"],
            Places.Pages.Select(p => p.Word));

        // EVERY PAGE ONCE: a page the enum has and no place holds is a page nobody can reach.
        Assert.Equal(Enum.GetValues<MainWindow.Page>().Order(), Places.Pages.Select(p => p.Page).Order());
        Assert.Equal(Places.Pages.Count(), Places.Pages.Select(p => p.Page).Distinct().Count());
        foreach (var (page, word) in Places.Pages)
        {
            Assert.Contains(Places.Of(page).Leaves, l => l.Page == page);
            Assert.Equal(word, Places.Word(page));
        }
    }

    /// <summary>
    /// THE DOCK'S SIX PLACES: each with a name, a drawing and a sentence of its own; each opens on its first leaf; the floor's own
    /// overview is the one page with no sheet over the floor — it is the floor; and Oversight, the owner's private line, is a leaf of
    /// what is his and of nothing else.
    /// </summary>
    [Fact]
    public void The_dock_holds_six_places_each_with_its_own_name_and_drawing_and_the_floor_alone_has_no_sheet()
    {
        Assert.Equal([Place.Floor, Place.Talk, Place.Desk, Place.Board, Place.Yours, Place.Machine], Places.All.Select(p => p.Place));
        Assert.Equal(6, Places.All.Select(p => p.Name).Distinct().Count());
        Assert.Equal(6, Places.All.Select(p => p.Icon).Distinct().Count());
        Assert.All(Places.All, p => Assert.False(string.IsNullOrWhiteSpace(p.Says)));
        Assert.All(Places.All, p => Assert.NotEmpty(p.Leaves));

        Assert.Equal(MainWindow.Page.Floor, Places.All[0].Leaves[0].Page);
        Assert.Equal([MainWindow.Page.Floor], Enum.GetValues<MainWindow.Page>().Where(p => !Places.HasSheet(p)));
        Assert.Equal(Place.Yours, Places.Of(MainWindow.Page.Oversight).Place);
        Assert.Equal(Place.Yours, Places.Of(MainWindow.Page.Owner).Place);
    }
}

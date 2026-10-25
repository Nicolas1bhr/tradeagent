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
/// THE RAIL (<c>U-settings-shed</c> item 5; R24 § 4.7): the pages in the order the owner reads them after the shed — the Board beside the
/// Dashboard it governs, the owner's page where Safety was, Oversight last — every page on the rail exactly once.
/// </summary>
public class RailOrderTests
{
    [Fact]
    public void The_rail_lists_the_pages_in_order()
    {
        Assert.Equal(
            ["Chat", TradeAgent.Core.Labels.NavOrganisation, TradeAgent.Core.Labels.NavWork, TradeAgent.Core.Labels.NavCosts, "Dashboard",
                TradeAgent.Core.Labels.BoardPage, "Daily report", "Inbox", TradeAgent.Core.Labels.OwnerPage, TradeAgent.Core.Labels.NavForYou, "Settings",
                "Activity", "Checks", "Oversight"],
            MainWindow.Rail.Select(r => r.Label));

        // EVERY PAGE ONCE: a page the enum has and the rail lacks is a page nobody can reach.
        Assert.Equal(Enum.GetValues<MainWindow.Page>().Order(), MainWindow.Rail.Select(r => r.Page).Order());
        Assert.Equal(MainWindow.Rail.Count, MainWindow.Rail.Select(r => r.Page).Distinct().Count());
    }
}

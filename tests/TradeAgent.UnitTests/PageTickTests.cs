using TradeAgent.App;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE FIVE-SECOND TICK UPDATES THE PAGE THE OWNER IS LOOKING AT (<c>U-perf-scan</c>, R28 G3), not all fourteen: every tick read every page's
/// records on the UI thread — scorecards included — for pages nobody could see. A page is brought up to date when it is shown, and on every
/// tick while it is. The window itself needs a platform no unit test has, so its two call sites are held by source, as
/// <c>OrgSurfaceShellTests</c> holds the rail's.
/// </summary>
public class PageTickTests
{
    [Fact]
    public void A_tick_updates_the_shown_page_alone_and_the_settings_sections_with_the_owners_page()
    {
        foreach (var (page, _) in MainWindow.Rail)
        {
            var updated = MainWindow.PagesToUpdate(page);
            Assert.Equal(page, updated[0]);
            Assert.Equal(page == MainWindow.Page.Owner ? 2 : 1, updated.Count);
        }
        Assert.Equal([MainWindow.Page.Owner, MainWindow.Page.Settings], MainWindow.PagesToUpdate(MainWindow.Page.Owner));
    }

    [Fact]
    public void The_window_updates_by_that_list_on_the_tick_and_when_a_page_is_shown()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln"))) dir = dir.Parent;
        var text = File.ReadAllText(Path.Combine(dir!.FullName, "src", "TradeAgent.App", "MainWindow.cs"));

        Assert.Contains("foreach (var page in PagesToUpdate(_shown)) UpdatePage(page, status);", text, StringComparison.Ordinal);
        Assert.Contains("foreach (var shown in PagesToUpdate(page)) UpdatePage(shown, status);", text, StringComparison.Ordinal);
        // Each page's update appears once: in UpdatePage, never again on the tick beside it.
        foreach (var call in new[] { "_organisation?.Update();", "_dashboard?.Update(status);", "_settings?.Update(status);", "_owner?.Update(status);" })
            Assert.Equal(1, text.Split(call).Length - 1);
    }
}

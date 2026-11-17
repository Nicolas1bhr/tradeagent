using Avalonia.Controls;
using Avalonia.LogicalTree;
using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE WINDOW'S OWN PARTS (<c>U-ui-world</c>): the search that knows every press, the pointing-out that presses nothing, and the two
/// lines the floor's state is said in. The window itself needs a platform no unit test has; these parts are plain controls and plain
/// words, and are held here.
/// </summary>
public class ShellTests
{
    static async Task Over(Func<AppHost, Task> body)
    {
        using var db = TestEnv.NewDb();
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try { await body(host); }
        finally { await host.Gateway.DisposeAsync(); }
    }

    /// <summary>
    /// FIND KNOWS EVERY PAGE AND EVERY PRESS THAT WEARS WORDS: handed the pages as the window builds them, its list holds each of the
    /// fifteen pages by the name the app's sentences use, and every button on the owner's and the settings' pages by the words it wears
    /// — the stop, a save inside a closed section, the market-data download — each under the page it is on. A press that exists is a
    /// press it finds; it invents none.
    /// </summary>
    [Fact]
    public Task Find_lists_every_page_and_every_press_that_wears_words_under_the_page_it_is_on() => Over(host =>
    {
        var settings = new SettingsPage(host);
        var owner = new OwnerPage(host, settings);
        (MainWindow.Page Page, Control Root)[] pages = [(MainWindow.Page.Owner, owner.Root), (MainWindow.Page.Settings, settings.Root)];
        var entries = Finder.Read(pages);

        // EVERY PAGE, once, by its own name.
        Assert.Equal(Places.Pages.Select(p => p.Word), entries.Where(e => e.Control is null).Select(e => e.Words));
        Assert.Equal(Places.Pages.Select(p => p.Page), entries.Where(e => e.Control is null).Select(e => e.Page));

        // EVERY PRESS THAT WEARS WORDS, on each page: what the tree holds is what the list holds, and nothing else.
        foreach (var (page, root) in pages)
        {
            var worn = root.GetLogicalDescendants().OfType<Button>().Select(Shell.Words).Where(w => w is not null).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            var found = entries.Where(e => e.Control is not null && e.Page == page).Select(e => e.Words).Order(StringComparer.Ordinal);
            Assert.Equal(worn, found);
            Assert.All(entries.Where(e => e.Control is not null && e.Page == page), e => Assert.Equal(Labels.FindOnPage(Places.Word(page)), e.Under));
        }
        Assert.Contains(entries, e => e.Page == MainWindow.Page.Owner && e.Words == Labels.StopAiTrading);
        Assert.Contains(entries, e => e.Page == MainWindow.Page.Owner && e.Words == Labels.SaveDailyCap);
        Assert.Contains(entries, e => e.Page == MainWindow.Page.Settings && e.Words == "Download 36 months");
        return Task.CompletedTask;
    });

    /// <summary>
    /// POINTING A PRESS OUT PRESSES NOTHING: a press inside a closed section is shown by opening that section and marking the press —
    /// its own handler never runs, a two-press is not armed by it, and what the press says is what it said.
    /// </summary>
    [Fact]
    public Task Pointing_a_press_out_opens_the_section_it_is_in_and_presses_nothing() => Over(host =>
    {
        var owner = new OwnerPage(host, new SettingsPage(host));
        var folds = owner.Root.GetLogicalDescendants().OfType<Button>().Where(b => Ui.IsFold(b, out _)).ToList();
        var closed = folds.Where(b => Ui.IsFold(b, out var open) && !open).ToList();
        Assert.NotEmpty(closed);

        // A two-press inside a section that is closed: the daily cap's save.
        var save = owner.Root.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == Labels.SaveDailyCap);
        Assert.False(save.IsEffectivelyVisible && save.GetLogicalAncestors().OfType<Control>().All(a => a.IsVisible));
        var before = host.Gateway.Settings;

        Shell.PointAt(save);

        Assert.All(save.GetLogicalAncestors().OfType<Control>(), a => Assert.True(a.IsVisible));
        Assert.Contains("found", save.Classes);
        Assert.False(Ui.IsArmed(save));
        Assert.Equal(Labels.SaveDailyCap, save.Content);
        Assert.Same(before, host.Gateway.Settings);
        // AND ONLY THE SECTIONS IT IS IN WERE OPENED: the others are as they were.
        Assert.Contains(closed, b => Ui.IsFold(b, out var open) && !open);
        return Task.CompletedTask;
    });

    /// <summary>
    /// THE FLOOR'S STATE IS THE LOOP'S OWN SENTENCE, CUT AT ITS DASH AND NOTHING LEFT OUT: what stands before the dash is set large, what
    /// stands after it is the sentence under it; a sentence with no dash is the large line by itself; one too long to be set large is
    /// said whole under the floor's name; and no words at all leave the floor's name alone.
    /// </summary>
    [Theory]
    [InlineData("stopped — the AI has not been started", "Stopped", "The AI has not been started.")]
    [InlineData("working", "Working", "")]
    [InlineData("waiting until 14:00 — your plan's limit was reached; the next turn waits for it.", "Waiting until 14:00", "Your plan's limit was reached; the next turn waits for it.")]
    [InlineData("", "The floor", "")]
    public void The_floors_large_line_and_the_sentence_under_it_are_the_loops_own_words(string loop, string headline, string state)
    {
        Assert.Equal((headline, state), FloorWords.Headline(loop));
        // NOTHING LEFT OUT: every word of the loop's sentence is in one line or the other.
        var said = (FloorWords.Headline(loop).Headline + " " + FloorWords.Headline(loop).State).ToLowerInvariant();
        foreach (var word in loop.Split([' ', '—'], StringSplitOptions.RemoveEmptyEntries))
            Assert.Contains(word.TrimEnd('.').ToLowerInvariant(), said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_state_too_long_to_be_set_large_is_said_whole_under_the_floors_name()
    {
        const string loop = "held back by the wait after a start that did not close properly — it resumes by itself";
        var (headline, state) = FloorWords.Headline(loop);
        Assert.Equal(Labels.NavFloor, headline);
        Assert.Equal("Held back by the wait after a start that did not close properly — it resumes by itself.", state);
        Assert.True("held back by the wait after a start that did not close properly".Length > FloorWords.HeadlineMost);
    }
}

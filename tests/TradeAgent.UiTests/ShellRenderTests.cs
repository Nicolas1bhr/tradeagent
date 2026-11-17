using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE WINDOW ITSELF, DRAWN (<c>U-ui-world</c>): until this unit no row ever built the window — its shell was held by reading its
/// source. These rows build the real <see cref="MainWindow"/> over the seeded working organisation, headless, at the two sizes the
/// owner meets, go to every page of every place the way a press in the dock does, and hold what the shell promises:
///
/// <list type="bullet">
/// <item>every page is reached, shown on the sheet (the floor's own overview alone has none), inside the sheet;</item>
/// <item><b>the stop is on screen and under nothing, whatever page is shown</b> — a stop that is one move away is not a stop;</item>
/// <item>Find takes the owner to a press by its words and presses nothing; Escape gives the floor the window back.</item>
/// </list>
/// Set <c>TA_UI_SHOTS</c> to a folder to keep a picture of the whole window on each page.
/// </summary>
public class ShellRenderTests(ITestOutputHelper output)
{
    static readonly string? Shots = Environment.GetEnvironmentVariable("TA_UI_SHOTS") is { Length: > 0 } dir ? dir : null;

    static void Save(Window win, string name)
    {
        if (Shots is null) return;
        Directory.CreateDirectory(Shots);
        (win.CaptureRenderedFrame() ?? throw new InvalidOperationException("the headless window rendered no frame"))
            .Save(Path.Combine(Shots, name), new PngBitmapEncoderOptions());
    }

    static Rect In(Visual v, Visual root) => new(v.TranslatePoint(default, root) ?? default, v.Bounds.Size);

    static MainWindow Open(AppHost host, int width, int height)
    {
        var status = host.Gateway.StatusAsync().GetAwaiter().GetResult();
        var win = new MainWindow(host) { Stepped = true, Width = width, Height = height };
        win.Show();
        win.Compose(status);
        win.UpdateLayout();
        win.Floor!.ReadNow();
        win.UpdateLayout();
        for (var t = 0.0; t < 3; t += 0.05) win.Floor.Stage.Step(0.05f);
        return win;
    }

    [Theory]
    [InlineData(1280, 800)]
    [InlineData(1920, 1080)]
    public async Task Every_page_is_reached_on_the_sheet_and_the_stop_is_on_screen_and_under_nothing_on_every_one(int width, int height)
    {
        using var db = OwnerPagesSeed.Working(Path.Combine(UiTestHome.Home, $"shell-{Guid.NewGuid():n}.db"));
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            HeadlessUi.Run(() =>
            {
                var still = Motion.Still;
                Motion.Still = true;
                try
                {
                    var win = Open(host, width, height);
                    var window = new Rect(0, 0, width, height);
                    foreach (var (page, word) in Places.Pages)
                    {
                        win.Go(page);
                        win.UpdateLayout();
                        for (var t = 0.0; t < 1.5; t += 0.05) win.Floor!.Stage.Step(0.05f);
                        win.UpdateLayout();
                        Assert.Equal(page, win.Shown);
                        var sheet = win.PageSheet!;

                        // THE STOP: on screen, whole, and under nothing — not the sheet, not the dock.
                        var stop = win.Stop!;
                        Assert.True(stop.IsEffectivelyVisible, $"on {word} the stop is not shown");
                        var at = In(stop, win);
                        Assert.True(window.Contains(at) && at.Width > 20 && at.Height > 10, $"on {word} the stop is not whole on screen: {at}");
                        Assert.False(sheet.Root.IsVisible && In(sheet.Root, win).Intersects(at), $"on {word} the sheet lies over the stop");
                        Assert.False(In(win.PlaceDock!.Root, win).Intersects(at), $"on {word} the dock lies over the stop");

                        if (!Places.HasSheet(page))
                        {
                            // THE FLOOR'S OWN OVERVIEW: no sheet — the floor has the window.
                            Assert.False(sheet.Open);
                            Assert.False(sheet.Root.IsVisible);
                        }
                        else
                        {
                            // ANY OTHER PAGE: on the sheet, and only that page.
                            Assert.True(sheet.Open && sheet.Root.IsVisible, $"{word} did not bring the sheet");
                            var root = win.PageRoot(page)!;
                            Assert.True(root.IsEffectivelyVisible, $"{word} is not shown on its sheet");
                            var on = In(sheet.Root, win);
                            Assert.True(on.Inflate(1).Contains(In(root, win)), $"{word} is not inside the sheet: {In(root, win)} in {on}");
                            Assert.Single(Places.Pages, p => Places.HasSheet(p.Page) && win.PageRoot(p.Page)!.IsVisible);
                            // The sheet and the dock do not lie over each other, and both are whole on screen.
                            Assert.True(window.Contains(on), $"on {word} the sheet runs off the window: {on}");
                            Assert.False(on.Intersects(In(win.PlaceDock.Root, win)), $"on {word} the sheet and the dock overlap");
                        }
                        Assert.True(window.Contains(In(win.PlaceDock.Root, win)), $"on {word} the dock runs off the window: {In(win.PlaceDock.Root, win)}");
                        Save(win, $"window-{Places.Of(page).Place.ToString().ToLowerInvariant()}-{page.ToString().ToLowerInvariant()}-{width}x{height}.png");
                    }
                    win.Close();
                }
                finally { Motion.Still = still; }
            });
        }
        finally { await host.Gateway.DisposeAsync(); }
    }

    [Fact]
    public async Task Find_goes_to_a_press_by_its_words_and_presses_nothing_and_escape_gives_the_floor_the_window_back()
    {
        using var db = OwnerPagesSeed.Working(Path.Combine(UiTestHome.Home, $"shell-find-{Guid.NewGuid():n}.db"));
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            HeadlessUi.Run(() =>
            {
                var still = Motion.Still;
                Motion.Still = true;
                try
                {
                    var win = Open(host, 1280, 800);
                    var before = host.Gateway.Settings;

                    // NOTHING TYPED: the fifteen pages, by name.
                    var find = win.Find!;
                    find.Show();
                    win.UpdateLayout();
                    Assert.True(find.Open);
                    Assert.Equal(Places.Pages.Select(p => p.Word), find.Found);

                    // A PRESS BY ITS WORDS — one inside a section of the owner's page that is closed.
                    find.Asked = "Save the daily limit";
                    win.UpdateLayout();
                    Assert.Contains(Labels.SaveDailyCap, find.Found);
                    Assert.Equal(Labels.SaveDailyCap, find.Found[0]);
                    // COUNTED AT THE PRESS ITSELF: Find shows where it is, and does not press it.
                    var pressed = 0;
                    var target = win.PageRoot(MainWindow.Page.Owner)!.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == Labels.SaveDailyCap);
                    target.Click += (_, _) => pressed++;
                    find.ChooseFirst();
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    win.UpdateLayout();
                    Assert.False(find.Open);
                    Assert.Equal(MainWindow.Page.Owner, win.Shown);
                    var save = win.PageRoot(MainWindow.Page.Owner)!.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == Labels.SaveDailyCap);
                    Assert.True(save.IsEffectivelyVisible, "the press Find went to is still inside a closed section");
                    // AND IT IS ON SCREEN: scrolled to, inside the sheet, where the owner's eye is taken.
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    win.UpdateLayout();
                    for (var t = 0.0; t < 1.5; t += 0.05) win.Floor!.Stage.Step(0.05f);
                    var seen = In(win.PageSheet!.Root, win);
                    Assert.True(seen.Contains(In(save, win).Center), $"the press Find went to is not on screen: {In(save, win)} in the sheet at {seen}");
                    Assert.Contains("found", save.Classes);
                    Assert.Same(target, save);
                    Assert.Equal(0, pressed);
                    Assert.False(Ui.IsArmed(save));
                    Assert.Same(before, host.Gateway.Settings);
                    Save(win, "window-find-went-to-a-press-1280x800.png");

                    // WORDS NOTHING WEARS: said, and nothing to choose.
                    find.Show();
                    find.Asked = "zzz no such press";
                    win.UpdateLayout();
                    Assert.Empty(find.Found);
                    find.Close();

                    // ESCAPE: the sheet goes, the floor has the window.
                    win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                    win.UpdateLayout();
                    Assert.Equal(MainWindow.Page.Floor, win.Shown);
                    Assert.False(win.PageSheet!.Open);
                    output.WriteLine("find: the pages, a press inside a closed section, nothing, and escape");
                    win.Close();
                }
                finally { Motion.Still = still; }
            });
        }
        finally { await host.Gateway.DisposeAsync(); }
    }
}

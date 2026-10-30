using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THIS PROCESS'S HOME IS A SCRATCH DIRECTORY, set before anything reads <see cref="Paths"/>: the composed host below resolves workspace
/// folders through it, and the owner's real home is never one this assembly may name.
/// </summary>
static class UiTestHome
{
    public static string Home { get; } = Path.Combine(Path.GetTempPath(), "tradeagent-uitests", Guid.NewGuid().ToString("n"));

    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(Home);
        Environment.SetEnvironmentVariable("TRADEAGENT_HOME", Home);
        Environment.SetEnvironmentVariable("TRADEAGENT_PIPE", "ta-uitest-" + Guid.NewGuid().ToString("n")[..12]);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(Home, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        };
    }
}

/// <summary>
/// THE OWNER'S PAGES, RENDERED (<c>U-ui-pages</c>). Each page the owner reads is built the way <c>MainWindow</c> builds it — over a host composed
/// on a database this test seeded through the app's own stores — updated once, laid out in the frame the window gives it (the 210 px rail
/// beside it, the header above it) at the two window sizes he meets (1280x800, the Linux host's Xvnc; 1920x1080), and drawn by Skia.
///
/// <para><b>Golden-free smoke rows</b>: a page renders without throwing and FITS ITS BOX — no text, button or panel reaches past the page's right
/// edge, and no scroll viewer scrolls sideways. Pixels are not compared. Set <c>TA_UI_SHOTS</c> to a folder to keep each frame as a PNG
/// (the viewport the owner sees first, and the whole page drawn at its full height).</para>
/// </summary>
public class OwnerPagesRenderTests(ITestOutputHelper output)
{
    const double Rail = 211, Header = 56;

    public static readonly TheoryData<int, int> Sizes = new() { { 1280, 800 }, { 1920, 1080 } };

    static readonly string? Shots = Environment.GetEnvironmentVariable("TA_UI_SHOTS") is { Length: > 0 } dir ? dir : null;

    /// <summary>One page of the rail: its name and how the window builds it over the host.</summary>
    sealed record PageMaker(string Name, Func<AppHost, Control> Build, Action<AppHost, object> Update);

    static IReadOnlyList<PageMaker> Pages(GatewayStatusBox status)
    {
        // Built in MainWindow.BuildPages' order and with its arguments; each update is the one UpdateShell runs.
        return
        [
            Make("chat", h => new ChatView(h, () => Task.CompletedTask), (p, _) => p.Update(), p => p.Root),
            Make("organisation", h => new OrganisationPage(OrgSurfaceSources.For(h)), (p, _) => p.Update(), p => p.Root),
            Make("work", h => new WorkPage(OrgSurfaceSources.For(h)), (p, _) => p.Update(), p => p.Root),
            Make("costs", h => new CostsPage(OrgSurfaceSources.For(h)), (p, _) => p.Update(), p => p.Root),
            Make("dashboard", h => new DashboardPage(h, () => Task.CompletedTask), (p, _) => p.Update(status.Status!), p => p.Root),
            Make("board", h => new BoardPage(h.Board), (p, _) => p.Update(), p => p.Root),
            Make("report", h => new ReportPage(h), (p, _) => p.Update(), p => p.Root),
            Make("owner", h => new OwnerPage(h, new SettingsPage(h)), (p, _) => p.Update(status.Status!), p => p.Root),
            Make("for-you", h => new OwnerInboxPage(OwnerInboxSources.For(h)), (p, _) => p.Update(), p => p.Root),
            Make("oversight", h => new OversightPage(OversightSources.For(h)), (p, _) => p.Update(), p => p.Root),
        ];

        static PageMaker Make<T>(string name, Func<AppHost, T> build, Action<T, object?> update, Func<T, Control> root) where T : class
        {
            var made = new ConditionalWeakTable<Control, T>();
            return new PageMaker(name,
                h =>
                {
                    var page = build(h);
                    var r = root(page);
                    made.Add(r, page);
                    return r;
                },
                (_, r) =>
                {
                    if (made.TryGetValue((Control)r, out var page)) update(page, null);
                });
        }
    }

    sealed class GatewayStatusBox
    {
        public GatewayStatus? Status { get; set; }
    }

    /// <summary>
    /// What a frame showed: the page's name; each thing found past its right edge or scrolling sideways, and each card drawn with nothing in
    /// it (both are defects the smoke rows refuse); every visible text, in order; and the width of each visible text box, by its placeholder.
    /// </summary>
    sealed record Frame(string Page, IReadOnlyList<string> Overflow, IReadOnlyList<string> Texts, IReadOnlyDictionary<string, double> Boxes);

    static PixelRect Pixels(Visual v, Visual root)
    {
        var at = v.TranslatePoint(new Point(0, 0), root) ?? default;
        return new PixelRect((int)Math.Floor(at.X), (int)Math.Floor(at.Y), (int)Math.Ceiling(v.Bounds.Width), (int)Math.Ceiling(v.Bounds.Height));
    }

    /// <summary>Everything visible that reaches past <paramref name="right"/> (frame pixels), and every scroll viewer that scrolls sideways.</summary>
    static List<string> OverflowIn(Control page, Visual frame, double right)
    {
        var found = new List<string>();
        foreach (var v in page.GetSelfAndVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0))
        {
            if (v is ScrollViewer sv && sv.Extent.Width > sv.Viewport.Width + 1)
                found.Add($"a scroll viewer in {(sv.TemplatedParent as Control)?.GetType().Name ?? "the page"} “{Describe(sv.TemplatedParent as Control ?? sv)}” scrolls sideways ({sv.Extent.Width:0} wide in {sv.Viewport.Width:0})");
            if (v is not (TextBlock or Button or TextBox or ComboBox)) continue;
            var box = Pixels(v, frame);
            if (box.Right > right + 1)
                found.Add($"{v.GetType().Name} “{Describe(v)}” ends at x={box.Right}, past the page's edge at {right:0}");
        }
        return found;
    }

    /// <summary>Every visible card (the theme's elevated border) whose content is a panel with nothing visible in it.</summary>
    static IEnumerable<string> BlankCardsIn(Control page) =>
        page.GetSelfAndVisualDescendants().OfType<Border>()
            .Where(b => b.IsEffectivelyVisible && ReferenceEquals(b.Background, Theme.BgElevated) && b.Child is Panel p
                        && !p.Children.Any(k => k.IsVisible))
            .Select(b => $"a card is drawn with nothing in it, {b.Bounds.Width:0}x{b.Bounds.Height:0}");

    static string Describe(Control c) => c switch
    {
        TextBlock t => Cut(t.Text ?? ""),
        TextBox x => Cut(x.Text ?? x.PlaceholderText ?? ""),
        ComboBox k => Cut(k.SelectedItem?.ToString() ?? ""),
        Button { Content: string s } => Cut(s),
        Button b => Cut(b.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text ?? "button"),
        _ => c.GetType().Name
    };

    static string Cut(string s) => s.Length > 60 ? s[..60] + "…" : s;

    /// <summary>
    /// Renders each page in the window's frame at <paramref name="width"/>x<paramref name="height"/>: the rail and header as blank bands, the
    /// page beside and below them. Returns each page's overflow; saves the frames when <c>TA_UI_SHOTS</c> is set.
    /// </summary>
    List<Frame> RenderAll(Database db, string home, int width, int height, Action<AppHost>? prepare = null)
    {
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            prepare?.Invoke(host);
            var status = new GatewayStatusBox { Status = host.Gateway.StatusAsync().GetAwaiter().GetResult() };
            var frames = new List<Frame>();
            foreach (var maker in Pages(status))
            {
                List<string> overflow = [];
                List<string> texts = [];
                Dictionary<string, double> boxes = [];
                HeadlessUi.Run(() =>
                {
                    var page = maker.Build(host);
                    maker.Update(host, page);
                    maker.Update(host, page); // the five-second pass, twice: a second pass over the same records must lay out the same
                    var win = Framed(page, width, height);
                    win.Show();
                    win.UpdateLayout();
                    overflow = OverflowIn(page, win, width - Theme.S6);
                    overflow.AddRange(BlankCardsIn(page));
                    var shown = page.GetSelfAndVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible).ToList();
                    texts = [.. shown.OfType<TextBlock>().Select(t => t.Text ?? "").Where(t => t.Length > 0)];
                    foreach (var box in shown.OfType<TextBox>().Where(b => b.PlaceholderText is { Length: > 0 }))
                        boxes.TryAdd(box.PlaceholderText!, box.Bounds.Width);
                    Save(win, $"{home}-{maker.Name}-{width}x{height}.png");

                    // AND THE WHOLE PAGE: the window grown to the page's own height, so a review reads what is below the fold.
                    var bottom = page.GetSelfAndVisualDescendants().OfType<ScrollViewer>().Where(s => s.TemplatedParent is null && s.IsEffectivelyVisible)
                        .Select(s => (s.TranslatePoint(new Point(0, 0), win)?.Y ?? 0) + s.Extent.Height).DefaultIfEmpty(0).Max();
                    if (Shots is not null && bottom > height - Theme.S6)
                    {
                        win.Height = Math.Min(bottom + Theme.S6 + 8, 8000);
                        win.UpdateLayout();
                        Save(win, $"{home}-{maker.Name}-{width}x{height}-full.png");
                    }
                    win.Close();
                });
                frames.Add(new Frame(maker.Name, overflow, texts, boxes));
            }
            return frames;
        }
        finally { host.Gateway.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    static Window Framed(Control page, int width, int height)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{Rail},*"), RowDefinitions = new RowDefinitions($"{Header},*") };
        grid.Children.Add(new Border { Background = Theme.BgRail, [Grid.RowSpanProperty] = 2 });
        grid.Children.Add(new Border { Background = Theme.BgElevated, [Grid.ColumnProperty] = 1 });
        page.IsVisible = true;
        // The window's page host: a padded border beside the rail (MainWindow.Build's body).
        grid.Children.Add(new Border { Padding = new Thickness(Theme.S6), Child = page, [Grid.ColumnProperty] = 1, [Grid.RowProperty] = 1 });
        return new Window { Width = width, Height = height, Background = Theme.Bg, Content = grid };
    }

    static void Save(Window win, string name)
    {
        if (Shots is null) return;
        Directory.CreateDirectory(Shots);
        var frame = win.CaptureRenderedFrame() ?? throw new InvalidOperationException("the headless window rendered no frame");
        frame.Save(Path.Combine(Shots, name), new PngBitmapEncoderOptions());
    }

    void AssertFits(IReadOnlyList<Frame> frames, int width, int height)
    {
        foreach (var f in frames)
            foreach (var o in f.Overflow) output.WriteLine($"{f.Page} at {width}x{height}: {o}");
        var bad = frames.Where(f => f.Overflow.Count > 0).Select(f => $"{f.Page}: {f.Overflow[0]}").ToList();
        Assert.True(bad.Count == 0, $"pages that do not fit {width}x{height}: {string.Join(" | ", bad)}");
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Every_owner_page_renders_and_fits_its_box_over_a_working_organisation(int width, int height)
    {
        using var db = OwnerPagesSeed.Working(Path.Combine(UiTestHome.Home, $"working-{Guid.NewGuid():n}.db"));
        // Today's report written, as the owner's "Write it now" writes it: the report page shows a report, not only its empty state.
        AssertFits(RenderAll(db, "working", width, height, host => host.Gateway.Reports.WriteNow(host.ReportInputs())), width, height);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Every_owner_page_renders_and_fits_its_box_over_an_empty_home(int width, int height)
    {
        using var db = new Database(Path.Combine(UiTestHome.Home, $"empty-{Guid.NewGuid():n}.db"));
        AssertFits(RenderAll(db, "empty", width, height), width, height);
    }

    /// <summary>
    /// WHAT THE FIRST LOOK AT THESE PAGES FOUND, KEPT FIXED (<c>U-ui-pages</c>). Each line failed on the pages as they were:
    /// the Dashboard's guidance box was a figure box's 150 px in the middle of its card; the For-you page addressed the owner's words "to
    /// operations" and waited for "operations's next turn"; the Costs page wrote three "none today" lines per unit around the one figure; the
    /// Work page said "latitude exact"; the Oversight page drew its findings heading over nothing; the Owner page's board-share box shouted its
    /// refusal before anything was typed; the cost line left a dash open ("… not money cannot stop it"); and the Daily report sent the owner to
    /// a terminal verb.
    /// </summary>
    [Fact]
    public void The_defects_the_first_look_found_stay_fixed()
    {
        using var db = OwnerPagesSeed.Working(Path.Combine(UiTestHome.Home, $"fixed-{Guid.NewGuid():n}.db"));
        var frames = RenderAll(db, "fixed", 1280, 800, host => host.Gateway.Reports.WriteNow(host.ReportInputs())).ToDictionary(f => f.Page);
        string All(string page) => string.Join("\n", frames[page].Texts);

        var guidance = frames["dashboard"].Boxes.Single(b => b.Key.StartsWith("e.g. focus on ES", StringComparison.Ordinal)).Value;
        Assert.True(guidance > 400, $"the guidance box is {guidance:0} px wide in a 1280 px window");
        Assert.DoesNotContain("not money cannot stop it", All("dashboard"), StringComparison.Ordinal);

        Assert.Contains("To the Operations Director, received", All("for-you"), StringComparison.Ordinal);
        Assert.Contains("Waiting for the Operations Director's next turn.", All("for-you"), StringComparison.Ordinal);

        Assert.DoesNotContain("none today", All("costs"), StringComparison.Ordinal);
        Assert.Contains("An API key (real money): 1.42 USD spent over 5 turns.", frames["costs"].Texts);
        Assert.Contains(Labels.CostsNothingToday, frames["costs"].Texts);

        Assert.DoesNotContain("latitude", All("work"), StringComparison.Ordinal);
        Assert.Contains("No findings.", frames["oversight"].Texts);
        Assert.DoesNotContain(Labels.AmountEmpty, frames["owner"].Texts);
        Assert.DoesNotContain("(R24)", All("owner"), StringComparison.Ordinal);
        Assert.DoesNotContain("trade pnl", All("report"), StringComparison.Ordinal);
        Assert.DoesNotContain("it has not launched", All("organisation"), StringComparison.Ordinal);
        Assert.Contains("chief of the organisation", frames["organisation"].Texts);
    }
}

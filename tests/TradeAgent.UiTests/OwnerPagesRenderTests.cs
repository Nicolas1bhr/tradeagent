using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.ConnectorSdk;
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
            // THE SETTINGS PAGE, the market data and the owner's head-start kit card (U-kit-socket).
            Make("settings", h => new SettingsPage(h), (p, _) => p.Update(status.Status!), p => p.Root),
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
            // CUT WHERE IT STANDS: a text whose laid-out line is wider than the box it was given — a button's words that do not wrap, a
            // label in a column too narrow — is clipped at the box's edge without ever reaching the page's.
            // (A wrapping text breaks its line at the box instead; only a line that does not wrap can be cut.)
            if (v is TextBlock { TextWrapping: TextWrapping.NoWrap } cut && cut.TextLayout.Width > cut.Bounds.Width + 1)
                found.Add($"{v.GetType().Name} “{Describe(cut)}” is cut: {cut.TextLayout.Width:0} wide in a {cut.Bounds.Width:0} box");
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
    List<Frame> RenderAll(Database db, string home, int width, int height, Action<AppHost>? prepare = null, bool armed = false,
        ITradingConnector? connector = null)
    {
        var host = AppHost.Composed(db, connector ?? new FakeConnector(new FakeBroker()), new AgentPresence());
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

                    // AND EVERY TWO-PRESS ARMED: its first press only arms (Ui.Confirm) or, for one that takes away, acts here on a scratch
                    // home — and the armed sentence, the longest words a button carries, must fit the page as the resting label did.
                    if (armed)
                    {
                        // EVERY SECTION OPEN FIRST (U-ui-glance): a press in a closed section is out of sight, and is armed and checked here.
                        foreach (var fold in page.GetSelfAndVisualDescendants().OfType<Button>().ToList())
                            if (Ui.IsFold(fold, out var open) && !open) Ui.SetFold(fold, true);
                        win.UpdateLayout();
                        foreach (var two in page.GetSelfAndVisualDescendants().OfType<Button>()
                                     .Where(x => x.IsEffectivelyVisible && x.IsEnabled && x.Tag?.GetType().Name == "ConfirmState").ToList())
                            two.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                        win.UpdateLayout();
                        overflow.AddRange(OverflowIn(page, win, width - Theme.S6).Select(o => "armed: " + o));
                        // The armed sentences are words the owner reads too (U-owner-words-2): kept with the page's texts.
                        texts.AddRange(page.GetSelfAndVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible).OfType<TextBlock>()
                            .Select(t => t.Text ?? "").Where(t => t.Length > 0 && !texts.Contains(t)));
                        Save(win, $"{home}-{maker.Name}-{width}x{height}-armed.png");
                    }

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

    /// <summary>
    /// EVERY TWO-PRESS, ARMED, FITS (seen on the owner's Linux screen, 2026-10-30): the armed sentence of "Let the AI work on its own" ran out
    /// of its card under the System Health column, and "Wake the chief once"'s was cut mid-word. Over the working organisation with its chief
    /// ACTIVE, so the wake press is drawn.
    /// </summary>
    [Theory]
    [MemberData(nameof(Sizes))]
    public void Every_owner_page_fits_its_box_with_every_two_press_armed(int width, int height)
    {
        using var db = OwnerPagesSeed.Working(Path.Combine(UiTestHome.Home, $"armed-{Guid.NewGuid():n}.db"));
        var frames = RenderAll(db, "armed", width, height, host =>
        {
            output.WriteLine(host.ActivateChief(new OrgStore(db).Chief()));
            host.Gateway.Reports.WriteNow(host.ReportInputs());
        }, armed: true);
        AssertFits(frames, width, height);
    }

    /// <summary>A Windows that takes every power request and keeps one Run value, for the pages' Windows-only lines.</summary>
    sealed class WindowsOnPaper : IKeepAwake, ISignInStart
    {
        public string? Value;
        public bool Supported => true;
        public bool Hold(string reason) => true;
        public void Release() { }
        public string? Command() => Value;
        public bool TurnedOffInWindows() => false;
        public void Register(string command) => Value = command;
        public void Remove() => Value = null;
    }

    /// <summary>
    /// A LAPTOP THAT RUNS UNATTENDED, ON THE PAGES (<c>U-windows-awake</c>): with the AI working on its own on a Windows, the Dashboard says the
    /// PC is kept awake with what still sleeps it, and the owner's page carries the sign-in section; every press of both, armed, fits.
    /// </summary>
    [Theory]
    [MemberData(nameof(Sizes))]
    public async Task The_windows_lines_for_an_unattended_laptop_render_and_fit_with_their_presses_armed(int width, int height)
    {
        using var db = OwnerPagesSeed.Working(Path.Combine(UiTestHome.Home, $"awake-{Guid.NewGuid():n}.db"));
        AppHost? held = null;
        var frames = RenderAll(db, "awake", width, height, host =>
        {
            held = host;
            var windows = new WindowsOnPaper();
            host.KeepAwake = windows;
            host.SignInStart = windows;
            host.ThisProgram = () => Path.Combine(UiTestHome.Home, "TradeAgent.exe");
            Assert.Null(host.LetTheAiWorkOnItsOwn());
        }, armed: true);
        try
        {
            var byPage = frames.ToDictionary(f => f.Page);
            Assert.Contains(AppHost.AwakeWords(AwakeReading.Holding), byPage["dashboard"].Texts);
            Assert.Contains(AppHost.LetItSleep, byPage["dashboard"].Texts);
            Assert.Contains(Labels.OwnerGroupSignInSummary, byPage["owner"].Texts);
            AssertFits(frames, width, height);
        }
        finally { if (held is not null) await held.Mission.PauseAsync(); }
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

        var guidance = frames["dashboard"].Boxes.Single(b => b.Key.StartsWith("e.g. keep positions small", StringComparison.Ordinal)).Value;
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

    /// <summary>
    /// THE ORGANISATION AT A GLANCE, IN READABLE NAMES (<c>U-ui-glance</c>; the cards U-ui-pages left): positions and teams were named only by
    /// ids on every org page; no page summed the organisation up; the daily report gave each director the day's TOTAL turns beside that
    /// director's own spend; and the Dashboard said "costs.json". Each line failed on the pages as they were.
    /// </summary>
    [Fact]
    public void The_owner_reads_his_organisation_at_a_glance_in_names_without_file_names()
    {
        using var db = OwnerPagesSeed.Working(Path.Combine(UiTestHome.Home, $"glance-{Guid.NewGuid():n}.db"));
        var frames = RenderAll(db, "glance", 1280, 800, host => host.Gateway.Reports.WriteNow(host.ReportInputs())).ToDictionary(f => f.Page);
        string All(string page) => string.Join("\n", frames[page].Texts);

        // NAMES, COMPOSED FROM THE CHART — the ids kept beside them.
        Assert.Contains("Research team 1", frames["organisation"].Texts);
        Assert.Contains("Executor 2 · Research team 1", frames["organisation"].Texts);
        Assert.Contains(OwnerPagesSeed.Executor2, frames["organisation"].Texts);
        Assert.DoesNotContain("Team team-", All("organisation"), StringComparison.Ordinal);
        Assert.Contains("Executor 2 · Research team 1 holds it for Research team 1 head", frames["work"].Texts);
        Assert.DoesNotContain("pos-", All("work"), StringComparison.Ordinal);
        Assert.Contains("Research team 2", frames["costs"].Texts);

        // AT A GLANCE on the Organisation page, every figure the seeded ledgers hold.
        Assert.Contains(Labels.GlanceTitle.ToUpperInvariant(), frames["organisation"].Texts.Select(t => t.ToUpperInvariant()));
        Assert.Contains("1 mandate is past its deadline; 1 delivery waits to be judged.", frames["organisation"].Texts);
        Assert.Contains(frames["organisation"].Texts, t => t.StartsWith("Executor 2 · Research team 1 owes Research team 1 head a delivery — due ", StringComparison.Ordinal));
        Assert.Contains(frames["organisation"].Texts, t => t.Contains("Research team 1 head owes a judgement on Executor 1 · Research team 1's delivery", StringComparison.Ordinal));
        Assert.Contains("1.42 USD of 5 USD in real money.", frames["organisation"].Texts);
        Assert.Contains("Nobody: no AI turn is open right now.", frames["organisation"].Texts);
        Assert.Contains("None is running: nothing is being tried on paper.", frames["organisation"].Texts);
        Assert.Contains(frames["organisation"].Texts, t => t.Contains("Research team 1 head: nothing queued", StringComparison.Ordinal));
        // …and compact on the Dashboard: the lines without their details.
        Assert.Contains("1 mandate is past its deadline; 1 delivery waits to be judged.", frames["dashboard"].Texts);
        Assert.DoesNotContain(frames["dashboard"].Texts, t => t.Contains("owes a judgement", StringComparison.Ordinal));

        // NO INTERNAL FILE NAME ON ANY PAGE: the app's own vendor files. (The Settings page's kit card names kit.json — the file of the kit the
        // owner builds himself, his to write, not one of the app's.)
        foreach (var f in frames.Values)
            foreach (var file in new[] { Labels.CostsFile, Labels.RuntimesFile, Labels.AtasFile })
                Assert.DoesNotContain(f.Texts, t => t.Contains(file, StringComparison.Ordinal));

        // EACH DIRECTOR'S OWN TURNS beside its own spend: Operations ran none of the five.
        Assert.Contains("Operations Director: model —, 0 turns of its own", All("report"), StringComparison.Ordinal);
        Assert.DoesNotContain("Operations Director: model —, 5 turns", All("report"), StringComparison.Ordinal);
    }

    /// <summary>
    /// THE OWNER PAGE, SHORTER, ITS EMERGENCY PRESSES ALWAYS IN VIEW (<c>U-ui-glance</c>; U-ui-pages' card: ~7,600 px tall at 1280x800). The
    /// stop, cancel-all and close-all presses sit above the page's scroll — never inside it — and the rest is in sections, closed but the one
    /// that says what is running, so the page's scrolling content is a fraction of what it was.
    /// </summary>
    [Fact]
    public async Task The_owner_page_keeps_its_emergency_presses_in_view_above_shorter_sections()
    {
        using var db = OwnerPagesSeed.Working(Path.Combine(UiTestHome.Home, $"owner-{Guid.NewGuid():n}.db"));
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            var status = await host.Gateway.StatusAsync();
            HeadlessUi.Run(() =>
            {
                var page = new OwnerPage(host, new SettingsPage(host));
                page.Update(status);
                var win = Framed(page.Root, 1280, 800);
                win.Show();
                win.UpdateLayout();

                var presses = page.EmergencyStrip.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
                Assert.Equal(3, presses.Count);
                Assert.All(presses, b => Assert.Empty(b.GetVisualAncestors().OfType<ScrollViewer>()));
                Assert.All(presses, b => Assert.True(Pixels(b, win).Bottom < 800, "an emergency press is below the first screen"));

                var scroll = page.Root.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.TemplatedParent is null);
                output.WriteLine($"the owner page's scrolling content: {scroll.Extent.Height:0} px");
                Assert.True(scroll.Extent.Height < 1500, $"the owner page's scrolling content is {scroll.Extent.Height:0} px tall");
                win.Close();
            });
        }
        finally { await host.Gateway.DisposeAsync(); }
    }

    /// <summary>CLOSING A SECTION DISARMS A HALF-PRESS IN IT: a press out of sight is never left armed (the rule every hidden press keeps).</summary>
    [Fact]
    public void Closing_a_section_disarms_a_half_pressed_button_inside_it()
    {
        HeadlessUi.Run(() =>
        {
            var pressed = 0;
            var two = Ui.Confirm("Close all positions", "Confirm: close all positions with market orders", () => pressed++);
            var fold = Ui.Fold("Emergency", Ui.Col(Theme.S2, two), open: true);
            var header = fold.GetLogicalDescendants().OfType<Button>().Single(b => Ui.IsFold(b, out _));
            two.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.True(Ui.IsArmed(two));
            header.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.False(Ui.IsArmed(two));
            Assert.False(((Control)two.Parent!).IsVisible);   // the body is hidden, the same control kept
            header.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            two.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(0, pressed);   // reopened, the first press only arms again
            Assert.True(Ui.IsArmed(two));
        });
    }

    /// <summary>
    /// ONBOARDING STEP 15 AT 1280x800 (seen on the owner's Linux screen, 2026-10-30): the back link beside the two presses read "← Back to
    /// “Choose your trading platfo" — cut. Built the way the window's setup surface holds it: a column at most 880 wide, padded.
    /// </summary>
    [Fact]
    public async Task The_onboarding_footer_keeps_its_back_link_whole_at_1280x800()
    {
        using var db = new Database(Path.Combine(UiTestHome.Home, $"onboarding-{Guid.NewGuid():n}.db"));
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            var onboarding = new OnboardingStore(db);
            foreach (var step in OnboardingSteps.Order.TakeWhile(s => s != OnboardingStep.EVIDENCE_HELD)) onboarding.Complete(step);
            Assert.Equal(OnboardingStep.EVIDENCE_HELD, onboarding.Current());
            List<string> overflow = [];
            HeadlessUi.Run(() =>
            {
                var view = new OnboardingView(host, () => { });
                var screen = view.Build();
                var win = new Window
                {
                    Width = 1280, Height = 800, Background = Theme.Bg,
                    Content = new ScrollViewer
                    {
                        Content = new Border
                        {
                            Padding = new Thickness(Theme.S8, Theme.S10),
                            Child = new ContentControl { MaxWidth = 880, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Content = screen }
                        }
                    }
                };
                win.Show();
                win.UpdateLayout();
                overflow = OverflowIn(screen, win, 1280);
                Save(win, "onboarding-step15-1280x800.png");
                win.Close();
            });
            foreach (var o in overflow) output.WriteLine(o);
            Assert.True(overflow.Count == 0, string.Join(" | ", overflow));
        }
        finally { await host.Gateway.DisposeAsync(); }
    }

    /// <summary>
    /// THE PLATFORM RECOMMENDED IS ONE THE ORGANISATION'S WORK CAN RUN ON (<c>U-paper-t0</c> item 4). "Practice simulator (RECOMMENDED)" silently allocated nothing: the founding
    /// envelopes stand on the paper venue, and it lists no pair under evidence. On the setup screen in either build TradeAgent paper comes first with the pill, the simulator
    /// says what it is, and ATAS is offered only by a build that has it. RED at the base: the pill stood on the simulator and said "Start here".
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_setup_screen_recommends_the_platform_strategies_run_on(bool atasInThisBuild)
    {
        using var db = new Database(Path.Combine(UiTestHome.Home, $"platform-{Guid.NewGuid():n}.db"));
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        host.AtasInThisBuild = atasInThisBuild;
        try
        {
            var onboarding = new OnboardingStore(db);
            foreach (var step in OnboardingSteps.Order.TakeWhile(s => s != OnboardingStep.TRADING_PLATFORM_SELECTED)) onboarding.Complete(step);

            List<string> texts = [];
            HeadlessUi.Run(() =>
            {
                var screen = new OnboardingView(host, () => { }).Build();
                var win = new Window { Width = 1280, Height = 800, Content = screen };
                win.Show();
                win.UpdateLayout();
                texts = [.. screen.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").Where(t => t.Length > 0)];
                win.Close();
            });

            var paper = texts.IndexOf("TradeAgent paper");
            var simulator = texts.IndexOf("Practice simulator");
            output.WriteLine(string.Join(" | ", texts.Skip(paper)));
            Assert.InRange(paper, 0, simulator);
            Assert.Equal("RECOMMENDED", texts[paper + 1]);
            Assert.Single(texts, t => t == "RECOMMENDED");
            Assert.Equal(Labels.SimulatorPlainly, texts[simulator + 1]);
            Assert.Equal(atasInThisBuild, texts.Contains("ATAS"));
            Assert.Equal(atasInThisBuild, !texts.Contains(Labels.PaperPlainly + " " + BuildContents.AtasNotInThisBuild));
        }
        finally { await host.Gateway.DisposeAsync(); }
    }

    /// <summary>
    /// THE START-UP SCREENS' TWO-PRESS SAYS ITS SECOND SENTENCE WHOLE (<c>U-owner-words</c>): the older home's "Start fresh" and the no-ATAS
    /// home's press stood in a row, where nothing bounds a button's width, so the armed sentence — the words that say in full what the second
    /// press does — ran past the setup column and was cut at its edge. Each screen is built as the window builds it, in the window's own setup
    /// surface at 1280x800, and its press armed. RED with the press back in its row: "Button “Yes — keep the old folder aside…” ends at x=…".
    /// </summary>
    [Fact]
    public void The_start_up_screens_keep_their_armed_sentence_inside_the_setup_column()
    {
        static Task Nothing() => Task.CompletedTask;
        var screens = new (string Name, Func<Control> Build)[]
        {
            ("older home", () => MainWindow.OlderHomeScreen("This TradeAgent does not open the folder an older version wrote.", null,
                OlderHome.ConfirmStartFresh, Nothing)),
            ("older home, a press refused", () => MainWindow.OlderHomeScreen("This TradeAgent does not open the folder an older version wrote.",
                "Another TradeAgent is still running on this folder, so the older home was not moved and nothing was copied. Close it and press Start fresh again.",
                OlderHome.ConfirmStartFreshCarryingTape, Nothing)),
            ("no ATAS, setup finished", () => MainWindow.AtasNotInThisBuildScreen("This TradeAgent was built without ATAS.", true, Nothing)),
            ("no ATAS, setup not finished", () => MainWindow.AtasNotInThisBuildScreen("This TradeAgent was built without ATAS.", false, Nothing)),
        };
        var bad = new List<string>();
        foreach (var (name, build) in screens)
            HeadlessUi.Run(() =>
            {
                var screen = build();
                var slot = MainWindow.SetupSlot();
                slot.Content = screen;
                var win = new Window { Width = 1280, Height = 800, Background = Theme.Bg, Content = MainWindow.SetupSurface(slot) };
                win.Show();
                win.UpdateLayout();
                var presses = screen.GetVisualDescendants().OfType<Button>().Where(b => b.Tag?.GetType().Name == "ConfirmState").ToList();
                Assert.Single(presses);
                presses[0].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                win.UpdateLayout();
                Assert.True(Ui.IsArmed(presses[0]), $"{name}: the first press did not arm");
                var edge = Pixels(slot, win).Right;
                bad.AddRange(OverflowIn(screen, win, edge).Select(o => $"{name}: {o}"));
                Save(win, $"start-up-{name.Replace(' ', '-').Replace(",", "")}-armed-1280x800.png");
                win.Close();
            });
        foreach (var b in bad) output.WriteLine(b);
        Assert.True(bad.Count == 0, string.Join(" | ", bad));
    }

    /// <summary>
    /// SETUP'S WELCOME NAMES NO PLATFORM HE IS NOT ON (<c>U-owner-words</c>): a fresh install starts on the simulator, and its first words read
    /// "your ATAS trading platform" — to an owner with no ATAS, in a build that may not carry it. RED at the base: the lead named ATAS.
    /// </summary>
    [Fact]
    public async Task Setup_welcome_names_no_atas_on_a_platform_that_is_not_atas()
    {
        using var db = new Database(Path.Combine(UiTestHome.Home, $"welcome-{Guid.NewGuid():n}.db"));
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            Assert.Equal(OnboardingStep.WELCOME, new OnboardingStore(db).Current());
            List<string> texts = [];
            HeadlessUi.Run(() =>
            {
                var screen = new OnboardingView(host, () => { }).Build();
                var win = new Window { Width = 1280, Height = 800, Content = screen };
                win.Show();
                win.UpdateLayout();
                texts = [.. screen.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").Where(t => t.Length > 0)];
                win.Close();
            });
            output.WriteLine(string.Join(" | ", texts));
            Assert.Contains(OnboardingView.WelcomeLead, texts);
            Assert.DoesNotContain(texts, t => t.Contains("ATAS", StringComparison.Ordinal));
        }
        finally { await host.Gateway.DisposeAsync(); }
    }

    /// <summary>
    /// THE SAME ON THE PAGES (<c>U-paper-t0</c> item 4): Settings says what the simulator is and puts the pill on paper, and the Owner page, whose envelopes stand on paper, says on
    /// the simulator that nothing they allow runs here. Every render here is on the simulator, the platform a fresh install starts on.
    /// </summary>
    [Fact]
    public async Task The_pages_say_plainly_that_no_strategy_runs_on_the_simulator()
    {
        using var db = OwnerPagesSeed.Working(Path.Combine(UiTestHome.Home, $"simulator-{Guid.NewGuid():n}.db"));
        var frames = RenderAll(db, "simulator", 1280, 800);

        var owner = frames.Single(f => f.Page == "owner").Texts;
        Assert.Contains(owner, t => t.EndsWith(Labels.SimulatorRunsNoStrategies, StringComparison.Ordinal));

        // THE PLATFORM'S THREE LIVE IN A FOLD OF THAT PAGE: the section itself, laid out.
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            List<string> texts = [];
            HeadlessUi.Run(() =>
            {
                var section = new SettingsPage(host).Platform;
                var win = new Window { Width = 1280, Height = 800, Content = section };
                win.Show();
                win.UpdateLayout();
                texts = [.. section.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").Where(t => t.Length > 0)];
                win.Close();
            });
            output.WriteLine(string.Join(" | ", texts));
            Assert.Equal(Labels.SimulatorPlainly, texts.SkipWhile(t => t != "Practice simulator").Skip(1).First(t => t != "IN USE"));
            Assert.Equal("RECOMMENDED", texts[texts.IndexOf("TradeAgent paper — real prices, simulated fills") + 1]);
            Assert.Single(texts, t => t == "RECOMMENDED");
        }
        finally { await host.Gateway.DisposeAsync(); }
    }

    /// <summary>
    /// THE DASHBOARD'S COST LINE NAMES THE SEAT'S MODEL AS THE SEAT'S (seen on the owner's screen, 2026-10-30): "running gpt-5.6-sol" stood
    /// beside a day's figure a chief on gpt-6-luna had spent. The figure is every position's; the model is the one this role's next turn asks for.
    /// </summary>
    /// <summary>
    /// THE HEAD-START KIT CARD ON SETTINGS (<c>U-kit-socket</c>): its two ways in and what is installed — at a start whose build carries the
    /// repository's empty sample kit, that kit, installed from the bundle, with its hash.
    /// </summary>
    [Fact]
    public void The_settings_page_shows_the_kit_card_with_its_presses_and_what_is_installed()
    {
        using var db = OwnerPagesSeed.Working(Path.Combine(UiTestHome.Home, $"kit-{Guid.NewGuid():n}.db"));
        var settings = RenderAll(db, "kit", 1280, 800).Single(f => f.Page == "settings");
        Assert.Contains(Labels.ChooseKitFolder, settings.Texts);
        Assert.Contains(Labels.ChooseKitZip, settings.Texts);
        var installed = settings.Texts.SkipWhile(t => t != "Installed").Skip(1).FirstOrDefault() ?? "";
        Assert.True(installed.StartsWith("Empty head-start kit — 0 skills, 0 tools — kit ", StringComparison.Ordinal) || installed == "No kit installed.",
            $"the card says '{installed}'");
        Assert.Empty(settings.Overflow);
    }

    [Fact]
    public void The_cost_line_never_says_a_model_ran_that_only_the_chairs_next_turn_asks_for()
    {
        var spend = new AiSpendToday
        {
            Metered = true, CanPrice = true, Class = BillingClasses.Plan, Role = CouncilRoles.Operations, Model = "gpt-5.6-sol",
            Spent = 0.0049m, Cap = 40m, Currency = "USD", Turns = 1
        };
        var line = DashboardPage.MissionCost(spend);
        Assert.DoesNotContain("running gpt-5.6-sol", line, StringComparison.Ordinal);
        Assert.Contains("the Operations Director's next turn asks for gpt-5.6-sol", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// SETUP'S ACTIVITY LINES SAY WHAT HAPPENED FOR THE PLATFORM CHOSEN (seen on the owner's practice-simulator setup, 2026-10-30): "Finding
    /// ATAS / Installing the ATAS bridge / Connecting to ATAS — done", where nothing looked for ATAS.
    /// </summary>
    [Fact]
    public void Setup_says_an_atas_step_was_skipped_when_the_platform_is_not_atas()
    {
        foreach (var step in new[] { OnboardingStep.ATAS_INSTALLED, OnboardingStep.ATAS_BRIDGE_INSTALLED, OnboardingStep.ATAS_BRIDGE_CONNECTED })
        {
            Assert.Equal($"Setup: {step.Title()} — skipped: the platform you chose is not ATAS, so there was nothing to do",
                OnboardingView.StepDoneLine(step, usingAtas: false));
            Assert.Equal($"Setup: {step.Title()} — done", OnboardingView.StepDoneLine(step, usingAtas: true));
        }
        Assert.Equal("Setup: Choose your trading platform — done", OnboardingView.StepDoneLine(OnboardingStep.TRADING_PLATFORM_SELECTED, usingAtas: false));
    }

    /// <summary>
    /// SYSTEM HEALTH NAMES THE PLATFORM HE IS ON (<c>U-owner-words-2</c>; the real run of 2026-11-13, shot 64): on TradeAgent paper the two
    /// ATAS rows said "not in use — you are on the practice simulator". Rendered on each platform this build can be switched to, the Dashboard
    /// says the true sentence for it: paper's name on paper, the simulator's on the simulator, and on ATAS the rows are ATAS's own checks.
    /// RED at the base: "you are on the practice simulator" on TradeAgent paper.
    /// </summary>
    [Theory]
    [InlineData("paper", "not in use — you are on TradeAgent paper")]
    [InlineData("fake", "not in use — you are on the practice simulator")]
    [InlineData("atas", null)]
    public void System_health_says_the_platform_in_force_on_each_platform(string platform, string? words)
    {
        Assert.Contains(platform, TradeAgent.Platforms.Connectors.All);
        using var db = new Database(Path.Combine(UiTestHome.Home, $"health-{platform}-{Guid.NewGuid():n}.db"));
        ITradingConnector connector = platform switch
        {
            "fake" => new FakeConnector(new FakeBroker()),
            "atas" => new TradeAgent.Connectors.Atas.AtasConnector(),
            _ => TradeAgent.Platforms.Connectors.Create(platform),
        };
        var dashboard = RenderAll(db, $"health-{platform}", 1280, 800, host => host.ReportAtasHealth(), connector: connector).Single(f => f.Page == "dashboard");
        var said = dashboard.Texts.Where(t => t.Contains("not in use", StringComparison.Ordinal)).ToList();
        output.WriteLine(string.Join(" | ", said));
        if (words is null) Assert.Empty(said);
        else Assert.Equal(["unknown — " + words, "unknown — " + words], said);
    }

    /// <summary>
    /// THE CHAT SUGGESTS A PRICE OF SOMETHING THIS HOME TRADES (<c>U-owner-words-2</c>; the real run of 2026-11-13): "What is ES trading at?" on
    /// a crypto paper account, an E-mini future fixed in the code. On each platform the price question names the first instrument the platform
    /// in force lists — a pair on TradeAgent paper, the simulator's own on the simulator — and the Dashboard's guidance example names none.
    /// RED at the base: "What is ES trading at?" on paper.
    /// </summary>
    [Theory]
    [InlineData("paper")]
    [InlineData("fake")]
    public async Task The_chat_suggests_the_price_of_an_instrument_the_platform_in_force_lists(string platform)
    {
        using var db = new Database(Path.Combine(UiTestHome.Home, $"suggest-{platform}-{Guid.NewGuid():n}.db"));
        // Paper lists the catalogue's VERIFIED spot rows (an instrument check serves them); here every row of the shipped catalogue is taken as checked.
        var catalogue = TradeAgent.Core.Data.VenueCatalog.Read();
        foreach (var venue in catalogue.Venues) foreach (var row in venue.Instruments) row.Verified = true;
        ITradingConnector connector = platform == "fake" ? new FakeConnector(new FakeBroker())
            : new TradeAgent.Connectors.Paper.PaperConnector(new TradeAgent.Connectors.Paper.PaperConnectorOptions { Catalogue = catalogue });
        var listed = await connector.GetInstrumentsAsync();
        output.WriteLine(string.Join(", ", listed.Select(i => i.Symbol)));
        Assert.NotEmpty(listed);
        var host = AppHost.Composed(db, connector, new AgentPresence());
        try
        {
            List<string> asked = [];
            HeadlessUi.Run(() =>
            {
                var chat = new ChatView(host, () => Task.CompletedTask);
                asked = [.. chat.EmptyState.GetLogicalDescendants().OfType<Button>().Select(b => b.Content as string ?? "").Where(t => t.EndsWith(" trading at?", StringComparison.Ordinal))];
            });
            output.WriteLine(string.Join(" | ", asked));
            Assert.Equal([ChatView.PriceQuestion(listed[0].Symbol)], asked);
            if (platform == "paper") Assert.DoesNotContain(listed, i => i.Symbol == "ES");
        }
        finally { await host.Gateway.DisposeAsync(); }
        var dashboard = RenderAll(db, $"suggest-{platform}", 1280, 800).Single(f => f.Page == "dashboard");
        Assert.DoesNotContain(dashboard.Boxes.Keys, k => k.Contains(" ES ", StringComparison.Ordinal));
    }

    /// <summary>
    /// THE PLATFORM PRESS LEAVES THE WINDOW ALIVE AND TAKES ONE PRESS (<c>U-owner-words-2</c>; the real run of 2026-11-13, shots 39–41): pressing
    /// "TradeAgent paper" froze the window for ~53 s with no "Working." painted — the switch ran on the window's thread — and the owner's second
    /// press, made in the freeze, was queued for the next screen. Timing-free: a step at the head of the switch's work records the thread it ran
    /// on and holds the work open; while it is held the screen says "Working.", its choices are greyed, and a press on the simulator does
    /// nothing. Released, the platform is paper and the work ran once. RED at the base: "the switch's work ran on the window's thread".
    /// </summary>
    [Fact]
    public async Task The_platform_press_runs_its_work_off_the_window_and_a_second_press_does_nothing()
    {
        using var db = new Database(Path.Combine(UiTestHome.Home, $"press-{Guid.NewGuid():n}.db"));
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var runs = 0;
        var onWindow = false;
        host.SwitchStep = () =>
        {
            Interlocked.Increment(ref runs);
            onWindow = Avalonia.Threading.Dispatcher.UIThread.CheckAccess();
            entered.Set();
            if (!onWindow) release.Wait();   // held only off the window's thread: held on it, the window could not be pressed at all
        };
        try
        {
            var onboarding = new OnboardingStore(db);
            foreach (var step in OnboardingSteps.Order.TakeWhile(s => s != OnboardingStep.TRADING_PLATFORM_SELECTED)) onboarding.Complete(step);
            HeadlessUi.Run(() =>
            {
                var win = new Window { Width = 1280, Height = 800 };
                OnboardingView? view = null;
                view = new OnboardingView(host, () => { win.Content = view!.Build(); win.UpdateLayout(); });
                win.Content = view.Build();
                win.Show();
                win.UpdateLayout();
                Button Card(string title) => ((Control)win.Content!).GetVisualDescendants().OfType<Button>()
                    .Single(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == title));

                Card("TradeAgent paper").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.True(entered.Wait(TimeSpan.FromSeconds(30)), "the switch's work never started");
                Assert.False(onWindow, "the switch's work ran on the window's thread");

                win.UpdateLayout();
                var texts = ((Control)win.Content!).GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
                Assert.Contains("Working.", texts);
                var simulator = Card("Practice simulator");
                Assert.False(simulator.IsEffectivelyEnabled, "a choice stays pressable while a press's work runs");
                simulator.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

                release.Set();
                var deadline = DateTime.UtcNow.AddSeconds(30);   // a guard against a hang, never a wait the test passes by
                while (host.Onboarding.Current() == OnboardingStep.TRADING_PLATFORM_SELECTED && DateTime.UtcNow < deadline)
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                win.Close();
            });
            Assert.Equal(1, Volatile.Read(ref runs));
            Assert.Equal(TradeAgent.Platforms.Connectors.Paper, host.Connector.Id);
            Assert.NotEqual(OnboardingStep.TRADING_PLATFORM_SELECTED, host.Onboarding.Current());
        }
        finally
        {
            release.Set();
            await host.Gateway.DisposeAsync();
        }
    }

    /// <summary>
    /// THE PROTECTIONS REGISTER SITS IN A CLOSED "FOR REVIEWERS" FOLD UNDER A PLAIN SUMMARY (<c>U-owner-words-3</c> item 3, the orchestrator's
    /// ruling): the Oversight page says, per protection, in a sentence of his words, what is protected and whether it holds on this computer
    /// — read from the class the register shows, so the summary never says more than the record — and the register itself (ids, files, doc
    /// sections, code paths, tests) is out of sight until he opens the fold. RED at base: the register stood open as the whole section, with
    /// no summary and no fold.
    /// </summary>
    [Fact]
    public void The_protections_register_sits_in_a_closed_reviewers_fold_under_a_plain_summary()
    {
        HeadlessUi.Run(() =>
        {
            var section = ProtectionsSection.Build(InvariantRegistry.All);
            var fold = Assert.Single(section.GetLogicalDescendants().OfType<Button>(), b => Ui.IsFold(b, out _));
            Assert.True(Ui.IsFold(fold, out var open) && !open, "the reviewers' fold is open by default");
            Assert.Contains(fold.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == ProtectionsSection.ReviewersTitle.ToUpperInvariant());

            var win = new Window { Width = 1280, Height = 800, Content = new ScrollViewer { Content = section } };
            win.Show();
            win.UpdateLayout();
            var seen = section.GetSelfAndVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
            foreach (var inv in InvariantRegistry.All)
            {
                Assert.True(ProtectionsSection.Plain.ContainsKey(inv.Id), $"{inv.Id} has no plain words");
                Assert.Contains(ProtectionsSection.Plain[inv.Id], seen);
                Assert.DoesNotContain(inv.Id, seen);          // the id, the file and the code path are behind the fold
                Assert.DoesNotContain(inv.Statement, seen);
            }
            Assert.Contains(ProtectionsSection.Holds(InvariantRegistry.All[0], InvariantRegistry.ThisPlatform), seen);
            var words = seen.SelectMany(t => OwnerWordShapes.Of(t).Select(shape => $"{shape} in \"{Cut(t)}\"")).ToList();
            Assert.True(words.Count == 0, string.Join(" | ", words));

            // OPENED, THE REGISTER IS THERE WHOLE.
            Ui.SetFold(fold, true);
            win.UpdateLayout();
            var opened = section.GetSelfAndVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
            foreach (var inv in InvariantRegistry.All) Assert.Contains(inv.Id, opened);
            win.Close();
        });

        // WHETHER IT HOLDS, FROM THE SHOWN CLASS: every seat hard is "holds for every kind"; a claim the register shows advisory is "only asked".
        var all = new Invariant("T-1", "s", "w", [], ["A.b"], [.. InvariantRegistry.SeatTypes.Select(t => new SeatClaim(t.SeatClass, EnforcementClass.HardApp, "b", ["A.c"]))]);
        Assert.Equal("Holds for every kind of AI seat.", ProtectionsSection.Holds(all, ProofPlatform.Linux));
        var mixed = all with { Seats = [new SeatClaim(SeatClasses.Harness, EnforcementClass.HardApp, "b", ["A.c"]), new SeatClaim(SeatClasses.Cli, EnforcementClass.Advisory, "b", []),
                                        new SeatClaim(SeatClasses.Broker, EnforcementClass.Mediated, "b", ["A.c"]), new SeatClaim(SeatClasses.Cell, EnforcementClass.Gap, "b", [])] };
        Assert.Equal("Holds for seats on TradeAgent's own worker; holds only through TradeAgent's own tools for seats without a shell; only asked of "
                     + "command-line seats (they run as you); not yet closed for seats in a sealed workspace.",
            ProtectionsSection.Holds(mixed, ProofPlatform.Linux));
    }

    /// <summary>
    /// THE APP'S OWN WORDS NAME NO FILE, NO DOC SECTION, NO COMMAND AND NO INTERNAL ID (<c>U-owner-words-2</c>; the real run's screens of
    /// 2026-11-13: the chief's card said "its next.json, or its own `trade org wake` request", and the AI's work line said "(ORCHESTRATION § 2.3)"
    /// with a position's id in the sentence). Every owner page, over an empty home and over the working organisation with its chief active and
    /// every two-press armed, is read for those shapes, and so are the activation gate's words for a held director. An id may stand as a label of
    /// its own beside a name (a text that is the id alone); it never stands inside a sentence. Exempt: the Settings page's kit card, which names
    /// the files of the kit the owner writes himself, and the Oversight page's Protections register inside its closed "For reviewers" fold (see
    /// below). Since U-owner-words-3 it also reads the build's words for the AI's seats ("legacy pair", "unconfined", "cell") and the activation
    /// gate's refusals in states no seed renders; since U-board-alive-2, a board seat's activation refusals too. RED at the base: "Chief
    /// (pos-…), dormant", "pos-…, dormant in unit team-…", and the gate's "'research' runs unconfined, and 'pos-…' … (ORCHESTRATION § 2.3)".
    /// </summary>
    [Fact]
    public void The_owner_pages_carry_no_file_name_section_mark_command_or_internal_id()
    {
        var hits = new List<string>();
        // THE PROTECTIONS REGISTER INSIDE THE OVERSIGHT PAGE'S "FOR REVIEWERS" FOLD IS EXEMPT, BY ITS OWN TEXTS (U-owner-words-3 item 3, the
        // orchestrator's ruling): it lists, by design, where each protection is written and the code and tests that keep it — a record for a
        // reviewer, closed by default. The plain summary above the fold, and everything else on the page, is read like any other words.
        HashSet<string> register = [];
        HeadlessUi.Run(() => register = [.. ProtectionsSection.Register(InvariantRegistry.All, InvariantRegistry.ThisPlatform).GetLogicalDescendants()
            .OfType<TextBlock>().Select(t => t.Text ?? "").Where(t => t.Length > 0)]);
        Assert.NotEmpty(register);
        void Read(IEnumerable<Frame> frames, string home)
        {
            foreach (var f in frames)
                foreach (var t in f.Texts.Distinct().Where(t => !register.Contains(t)))
                    foreach (var shape in OwnerWordShapes.Of(t))
                    {
                        hits.Add($"{home}/{f.Page}: {shape} in \"{Cut(t)}\"");
                        output.WriteLine($"FULL {home}/{f.Page}: {t}");
                    }
        }
        using (var empty = new Database(Path.Combine(UiTestHome.Home, $"words-empty-{Guid.NewGuid():n}.db")))
            Read(RenderAll(empty, "words-empty", 1280, 800, armed: true), "empty");
        using (var db = OwnerPagesSeed.Working(Path.Combine(UiTestHome.Home, $"words-{Guid.NewGuid():n}.db")))
            Read(RenderAll(db, "words", 1280, 800, host =>
            {
                // THE ACTIVATE PRESS'S NOTE, as the Organisation page shows it: here refused, this test host holding no proven workspace.
                var note = host.ActivateChief(new OrgStore(db).Chief());
                output.WriteLine(note);
                hits.AddRange(OwnerWordShapes.Of(note).Select(shape => $"working/activate-note: {shape} in \"{Cut(note)}\""));
                host.Gateway.Reports.WriteNow(host.ReportInputs());
            }, armed: true), "working");
        // AND THE CHIEF'S WAKE CARD, beside its press and in its armed sentence (shot 63): what wakes it after its one turn.
        var after = OrgStore.ChiefWakeAfter(TimeSpan.FromHours(24), TimeSpan.FromHours(4));
        hits.AddRange(OwnerWordShapes.Of(after).Select(shape => $"chief-wake: {shape} in \"{Cut(after)}\""));

        // AND THE WORDS THE DASHBOARD SHOWS FOR A HELD DIRECTOR (shot 70), from the activation gate itself: Research, unconfined, beside the
        // active chief, and the chief's own activation refused before a proven workspace.
        using (var gate = new Database(Path.Combine(UiTestHome.Home, $"words-gate-{Guid.NewGuid():n}.db")))
        {
            var research = new OrgStore(gate).Standing(CouncilRoles.Research);
            var held = PositionActivation.Decide(CouncilRoles.Research, research, new SeatFacts
            {
                Standing = research, DeclaredClass = SeatClasses.Cli, ActiveBeyondLegacy = ["pos-83d109e913fc"], UnconfinedSeatSetting = 3
            });
            Assert.NotNull(held);
            output.WriteLine(held.Words);
            hits.AddRange(OwnerWordShapes.Of(held.Words).Select(shape => $"gate/held: {shape} in \"{Cut(held.Words)}\""));

            // AND THE GATE'S OTHER REFUSALS (U-owner-words-3 item 2), each a state no seed renders and each shown on the Dashboard's held
            // line or the Organisation page when it holds: the chief before a proven sealed workspace, at the owner's ceiling, beside an open
            // turn outside a sealed workspace and with nothing told; a director whose neighbours could not be read.
            var chief = research! with { Id = "pos-0c1e0f0a0b0d", IsLegacy = false, Status = OrgPositionStatus.Active };
            List<string> earlier = [held.Words];
            var broker = new SeatFacts { Standing = chief, DeclaredClass = SeatClasses.Broker, ProvenCell = true };
            foreach (var (state, refusal) in new (string, TurnRefusal?)[]
                     {
                         ("code-on-a-shell", PositionActivation.Decide(chief.Id, chief, broker with { DeclaredClass = SeatClasses.Cli })),
                         ("unproven", PositionActivation.Decide(chief.Id, chief, broker with { ProvenCell = false })),
                         ("ceiling", UnconfinedSeatCeiling.Refusal(chief.Id, SeatClasses.Cli, ["research", "operations", "pos-0a0b0c0d0e0f"], 3)),
                         ("open-neighbour", PositionActivation.Decide(chief.Id, chief, broker with { OpenUnconfined = ["research"] })),
                         ("unset", PositionActivation.Decide(chief.Id, chief, null)),
                         ("unreadable", PositionActivation.Decide(CouncilRoles.Research, research, null, SeatClasses.Cli)),
                     })
            {
                Assert.NotNull(refusal);
                output.WriteLine($"{state}: {refusal.Words}");
                Assert.DoesNotContain(refusal.Words, earlier);   // each state reaches its own refusal, not an earlier rule's
                earlier.Add(refusal.Words);
                hits.AddRange(OwnerWordShapes.Of(refusal.Words).Select(shape => $"gate/{state}: {shape} in \"{Cut(refusal.Words)}\""));
            }
        }
        // AND THE BOARD SEAT'S ACTIVATION REFUSALS (U-board-alive-2 item 2), shown verbatim on the Owner page under the activate press: each
        // a state no seed renders (it needs the second press). Beyond the shapes, none says the build's words for a seat — "harness",
        // "confined", "executor", "broker-only" — since the owner founds seats from the page's own list.
        var dormant = new TradeAgent.Core.Board.BoardSeat("board-1", SeatClasses.Broker, "strong", "openai", TradeAgent.Core.Board.BoardSeatStatus.Dormant, DateTimeOffset.UtcNow);
        List<string> said = [];
        foreach (var (state, refusal) in new (string, string?)[]
                 {
                     ("no-seat", TradeAgent.Core.Board.BoardSeatGate.ActivationRefusal("board-9", null, SeatClasses.Broker, false, true, "openai")),
                     ("retired", TradeAgent.Core.Board.BoardSeatGate.ActivationRefusal("board-1", dormant with { Status = TradeAgent.Core.Board.BoardSeatStatus.Retired }, SeatClasses.Broker, false, true, "openai")),
                     ("active", TradeAgent.Core.Board.BoardSeatGate.ActivationRefusal("board-1", dormant with { Status = TradeAgent.Core.Board.BoardSeatStatus.Active }, SeatClasses.Broker, false, true, "openai")),
                     ("on-the-worker", TradeAgent.Core.Board.BoardSeatGate.ActivationRefusal("board-1", dormant with { SeatClass = SeatClasses.Harness }, SeatClasses.Harness, false, true, "openai")),
                     ("no-runtime", TradeAgent.Core.Board.BoardSeatGate.ActivationRefusal("board-1", dormant, null, false, true, "openai")),
                     ("another-class", TradeAgent.Core.Board.BoardSeatGate.ActivationRefusal("board-1", dormant, SeatClasses.Cell, false, true, "openai")),
                     ("another-class-cli", TradeAgent.Core.Board.BoardSeatGate.ActivationRefusal("board-1", dormant, SeatClasses.Cli, false, true, "openai")),
                     ("unproven", TradeAgent.Core.Board.BoardSeatGate.ActivationRefusal("board-1", dormant, SeatClasses.Broker, false, false, "openai")),
                     ("unknown-maker", TradeAgent.Core.Board.BoardSeatGate.ActivationRefusal("board-1", dormant, SeatClasses.Broker, false, true, null)),
                     ("another-maker", TradeAgent.Core.Board.BoardSeatGate.ActivationRefusal("board-1", dormant, SeatClasses.Broker, false, true, "anthropic")),
                 })
        {
            Assert.NotNull(refusal);
            output.WriteLine($"board/{state}: {refusal}");
            Assert.DoesNotContain(refusal, said);   // each state reaches its own refusal, not an earlier rule's
            said.Add(refusal);
            hits.AddRange(OwnerWordShapes.Of(refusal).Select(shape => $"board/{state}: {shape} in \"{Cut(refusal)}\""));
            foreach (var word in new[] { "harness", "confined", "executor", "broker-only" })
                if (refusal.Contains(word, StringComparison.OrdinalIgnoreCase)) hits.Add($"board/{state}: a seat's build word ({word}) in \"{Cut(refusal)}\"");
        }
        foreach (var h in hits.Distinct()) output.WriteLine(h);
        Assert.True(hits.Count == 0, $"{hits.Distinct().Count()} owner words carry a file, section mark, command or id: {string.Join(" | ", hits.Distinct().Take(6))}");
    }
}

/// <summary>The shapes an owner's page never carries in its own words (<c>U-owner-words-2</c>): one reader, so every test asks the same question.</summary>
static class OwnerWordShapes
{
    static readonly (string Name, System.Text.RegularExpressions.Regex Shape)[] Shapes =
    [
        ("a section mark", new(@"§")),
        ("a backticked command or name", new(@"`")),
        ("a file name", new(@"\b[\w-]+\.(json|jsonl|md|db|log|toml|yaml|yml|sh|ps1|dll|exe|csv)\b")),
        ("a folder path", new(@"(^|[\s(""'])(\.\./|\./|~/|/home/|/var/|/etc/)|\b(in|out|inbox|trading|state)/(\s|$|[)'""])")),
        ("a terminal command", new(@"\btrade (org|data|mandate|escalation|review|ledger|forecast|board|wake)\b")),
        ("an internal id", new(@"\b(pos|team|unit|turn|act|mandate|req|ord)-[0-9a-f]{6,}")),
        // U-owner-words-3 item 2: the build's own words for the AI's seats. The owner reads "the Operations Director and the Research
        // Director", "runs as you, outside a sealed workspace" and "a sealed workspace".
        ("a build word", new(@"\b(legacy pair|unconfined|cells?)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)),
        // U-owner-words-4 item 2: an Oversight detector by its id — "owner-words-late: deadline" on the thresholds. A hyphenated id
        // anywhere, a one-word id ("stall", "refusals") only as a label before a colon, as the thresholds wrote it.
        ("a detector id", new(string.Join("|", TradeAgent.Core.Oversight.OversightDetector.All.Select(d => d.Name.Contains('-')
            ? $@"\b{System.Text.RegularExpressions.Regex.Escape(d.Name)}\b"
            : $@"\b{System.Text.RegularExpressions.Regex.Escape(d.Name)}:")))),
    ];

    static readonly System.Text.RegularExpressions.Regex IdAlone = new(@"^(pos|team|unit|turn|act|mandate|req|ord)-[0-9a-f]{6,}$");

    /// <summary>Each forbidden shape in <paramref name="text"/>; none for an id that stands alone as a label, or the owner's own kit file.</summary>
    public static IEnumerable<string> Of(string text)
    {
        // An id standing alone is a label; the kit card's sentence names the files of the kit he writes himself — his own lane.
        if (IdAlone.IsMatch(text.Trim()) || text.Contains("kit.json", StringComparison.Ordinal)) yield break;
        foreach (var (name, shape) in Shapes)
            if (shape.Match(text) is { Success: true } m) yield return $"{name} ({m.Value.Trim()})";
    }
}

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
    List<Frame> RenderAll(Database db, string home, int width, int height, Action<AppHost>? prepare = null, bool armed = false)
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

                    // AND EVERY TWO-PRESS ARMED: its first press only arms (Ui.Confirm) or, for one that takes away, acts here on a scratch
                    // home — and the armed sentence, the longest words a button carries, must fit the page as the resting label did.
                    if (armed)
                    {
                        foreach (var two in page.GetSelfAndVisualDescendants().OfType<Button>()
                                     .Where(x => x.IsEffectivelyVisible && x.IsEnabled && x.Tag?.GetType().Name == "ConfirmState").ToList())
                            two.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                        win.UpdateLayout();
                        overflow.AddRange(OverflowIn(page, win, width - Theme.S6).Select(o => "armed: " + o));
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

        // NO INTERNAL FILE NAME ON ANY PAGE.
        foreach (var f in frames.Values)
            Assert.DoesNotContain(f.Texts, t => t.Contains(".json", StringComparison.Ordinal));

        // EACH DIRECTOR'S OWN TURNS beside its own spend: Operations ran none of the five.
        Assert.Contains("Operations Director: model —, 0 turns of its own", All("report"), StringComparison.Ordinal);
        Assert.DoesNotContain("Operations Director: model —, 5 turns", All("report"), StringComparison.Ordinal);
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
    /// THE DASHBOARD'S COST LINE NAMES THE SEAT'S MODEL AS THE SEAT'S (seen on the owner's screen, 2026-10-30): "running gpt-5.6-sol" stood
    /// beside a day's figure a chief on gpt-6-luna had spent. The figure is every position's; the model is the one this role's next turn asks for.
    /// </summary>
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
}

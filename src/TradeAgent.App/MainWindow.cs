using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TradeAgent.Core;
using TradeAgent.Gateway;
using TradeAgent.Provisioning;

// Two aliases, both forced by name collisions rather than chosen.
//   - `Window` inherits a `Theme` property from StyledElement, so inside a control subclass the
//     bare name `Theme` resolves to that property and never to the design-token class.
//   - importing Avalonia.Controls.Shapes wholesale would make `Path` ambiguous with System.IO's,
//     and this file needs System.IO.Path to find ATAS.
using Tokens = TradeAgent.App.Theme;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using Path = System.IO.Path;
using IconPath = Avalonia.Controls.Shapes.Path;

namespace TradeAgent.App;

/// <summary>
/// The application shell (<c>U-ui-world</c>): <b>one picture, and no list of pages</b>. The floor — the organisation drawn as a place,
/// lit where it is working — is the window itself, always there. Everything else is laid over it as glass: what state the app is in and
/// the stop, top right; the six places of the app in a dock at its foot (<see cref="Places"/>); and, when the owner goes somewhere, a
/// sheet with that place's page while the camera goes to the thing the page is about.
///
/// Three structural rules govern this file, and each exists because of defects that were real.
///
///   1. <b>Nothing here opens a console.</b> Talking to the AI happens in <see cref="ChatView"/>,
///      inside this window. The only processes this file starts are a folder, a URL or ATAS itself,
///      through the Windows shell — never a command prompt, and never the agent CLI.
///
///   2. <b>The tree is built once.</b> Pages are created in <see cref="EnsureShell"/> and swapped by
///      toggling <c>IsVisible</c> inside the sheet. The background loop raises a change event every five seconds, so
///      rebuilding on every refresh made diagnostics output vanish mid-read, reset the scroll
///      position, and silently disarmed a half-pressed two-step confirmation. Rebuilding a tree is
///      not a refresh.
///
///   3. <b>The stop is on screen whatever the owner is looking at</b>, and so is what outranks every page: an earlier armed home's
///      refusal, his private line's severe finding, a press that failed. A stop that is one move away is not a stop.
/// </summary>
public sealed class MainWindow : Window
{
    internal enum Page { Floor, Chat, Organisation, Work, Costs, Dashboard, Board, Report, Inbox, Owner, ForYou, Settings, Activity, Checks, Oversight }

    readonly AppHost _host;

    // ---- setup surface ---------------------------------------------------------------------
    // Stretch plus MaxWidth centres the column: Avalonia arranges a stretched child that came out
    // narrower than its slot in the middle of it, so setup reads as a document rather than a band
    // of controls smeared across a 1180px window.
    readonly ContentControl _setupSlot = SetupSlot();
    readonly ScrollViewer _setupSurface;
    OnboardingView? _wizard;

    // ---- shell -----------------------------------------------------------------------------
    // One cell under four rows: the floor fills all of it, and the rows lay the window's glass over it — what state the app is in,
    // what outranks every page, the sheet, and the dock.
    readonly Grid _shellSurface = new()
    {
        RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), IsVisible = false
    };

    // what state the app is in, top right
    readonly ContentControl _modePillHost = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _metaPlatform = Meta("");
    readonly TextBlock _metaAccount = MetaMono("");
    readonly TextBlock _metaAi = Meta("");
    readonly Ellipse _aiDot = new()
    {
        Width = 7, Height = 7, Fill = Tokens.Neutral, VerticalAlignment = VerticalAlignment.Center
    };
    Button? _stopButton;
    readonly Ellipse _stopLight = new() { Width = 7, Height = 7, Fill = Tokens.Danger, Margin = new Thickness(Tokens.S1, 0, 0, 0) };
    string _modeSignature = "";

    // banners
    readonly Border _errorBar = new()
    {
        Background = Tokens.NoticeDanger,
        BorderBrush = Tokens.Danger,
        BorderThickness = new Thickness(1),
        CornerRadius = Tokens.Radius,
        Padding = new Thickness(Tokens.S5, Tokens.S3),
        IsVisible = false
    };
    readonly TextBlock _errorText = new()
    {
        FontSize = Tokens.Small, Foreground = Tokens.Text, TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center
    };
    readonly Border _updateBanner = new()
    {
        Background = Tokens.NoticeInfo,
        BorderBrush = Tokens.Accent,
        BorderThickness = new Thickness(1),
        CornerRadius = Tokens.Radius,
        Padding = new Thickness(Tokens.S5, Tokens.S3),
        IsVisible = false
    };
    readonly TextBlock _updateBannerText = new()
    {
        FontSize = Tokens.Small, FontWeight = FontWeight.SemiBold, Foreground = Tokens.Accent,
        VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap
    };
    Button? _updateNotes;
    Button? _updateInstall;
    Button? _updateLater;

    // the dock, the sheet and the search
    Dock? _dock;
    Sheet? _sheet;
    Finder? _finder;
    Border? _status;
    readonly StackPanel _notices = new() { Spacing = Tokens.S2, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = Tokens.NoticeMaxWidth };

    // pages
    readonly Dictionary<Page, Control> _pages = new();
    FloorPage? _floor;
    ChatView? _chat;
    OrganisationPage? _organisation;
    WorkPage? _work;
    CostsPage? _costs;
    DashboardPage? _dashboard;
    BoardPage? _boardPage;
    ReportPage? _report;
    OwnerPage? _owner;
    OwnerInboxPage? _forYou;
    SettingsPage? _settings;
    InboxPage? _inbox;
    ActivityPage? _activity;
    ChecksPage? _checks;
    OversightPage? _oversightPage;
    OversightBanner? _oversightBanner;

    /// <summary>
    /// A HOME AN EARLIER BUILD LEFT ARMED FOR REAL MONEY, refused in the gateway's own sentence with its one press (<c>U-settings-shed</c>
    /// item 6, fix pass 1 for Codex P2): a strip of the window's own, above every page, because the app opens on Chat and a refusal that
    /// lives on one page is a refusal nobody sees.
    /// </summary>
    OwnerPage.EarlierArmedHome? _earlierArmed;

    bool _shellBuilt;
    bool _updating;

    public MainWindow(AppHost host)
    {
        _host = host;
        Title = "TradeAgent";
        Width = 1180;
        Height = 780;
        MinWidth = 980;
        MinHeight = 680;
        Background = Tokens.Bg;

        _setupSurface = SetupSurface(_setupSlot);

        // Behind the setup pane: a pool of faint light, the one the floor will stand in.
        Content = new Grid { Children = { new Border { Background = Tokens.SetupGlow, IsHitTestVisible = false }, _setupSurface, _shellSurface } };
    }

    // ---- lifecycle -----------------------------------------------------------------------------

    public async Task InitialiseAsync()
    {
        var started = await _host.StartAsync();
        if (!started)
        {
            if (_host.OlderHome is not null) ShowOlderHome();
            else if (_host.AtasRefusal is not null) ShowAtasNotInThisBuild();
            else ShowFatal(_host.StartupProblem ?? "TradeAgent could not start.");
            return;
        }
        await AfterStartAsync();
    }

    async Task AfterStartAsync()
    {
        // Anything thrown out of a button handler used to take the whole app down with it. A
        // nontechnical user cannot recover from a window that simply disappears.
        Ui.ReportError = message => Dispatcher.UIThread.Post(() => ShowProblem(message));
        _host.Changed += () => Dispatcher.UIThread.Post(() => _ = RefreshAsync());
        await RefreshAsync();
    }

    async Task RefreshAsync()
    {
        // NOTHING OF A HOST THE QUIT IS CLOSING (AppHost.Stopped): reading it throws, and a refresh that throws on the
        // setup screen re-renders and refreshes again, which no shutdown can get past.
        if (_host.Stopped) return;
        if (_updating) return;
        _updating = true;
        try
        {
            if (!_host.Onboarding.IsComplete()) { ShowSetup(); return; }

            EnsureShell();
            var status = await _host.Gateway.StatusAsync();
            UpdateShell(status);
        }
        catch (Exception ex)
        {
            ShowProblem(ex is TradeAgentException t ? t.Info.UserMessage : ex.Message);
        }
        finally { _updating = false; }
    }

    void ShowSetup()
    {
        _wizard ??= new OnboardingView(_host, () => Dispatcher.UIThread.Post(() => _ = RefreshAsync()));
        _setupSlot.Content = _wizard.Build();
        _setupSurface.IsVisible = true;
        _shellSurface.IsVisible = false;
    }

    /// <summary>
    /// AN OLDER HOME, REFUSED UNTOUCHED, AND THE OWNER'S WAY ON (<c>U-windows-release</c>): the words name both versions; "Start fresh"
    /// is two presses (the second says in full what it does), and it moves the old home aside whole — nothing deleted.
    /// </summary>
    void ShowOlderHome()
    {
        var older = _host.OlderHome!;
        _setupSlot.Content = OlderHomeScreen(older.Message, _host.StartFreshProblem,
            older.Policy is null ? OlderHome.ConfirmStartFresh : OlderHome.ConfirmStartFreshCarryingTape, async () =>
            {
                if (await _host.StartFreshAsync()) { await AfterStartAsync(); return; }
                if (_host.OlderHome is not null) ShowOlderHome();
                else ShowFatal(_host.StartupProblem ?? "TradeAgent could not start.");
            });
        _setupSurface.IsVisible = true;
        _shellSurface.IsVisible = false;
    }

    /// <summary>The setup surface's column: at most 880 wide. The window's, and the one the render tests lay a setup screen in.</summary>
    internal static ContentControl SetupSlot() => new() { MaxWidth = 880, HorizontalAlignment = HorizontalAlignment.Stretch };

    /// <summary>The setup surface: <paramref name="slot"/>, padded, in a scroll viewer.</summary>
    internal static ScrollViewer SetupSurface(Control slot) => new()
    {
        // ONE PANE, ALONE IN THE WINDOW (U-ui-world): before there is an organisation there is no floor to stand behind it, so the
        // pane is the window's own dark, lifted, with glass's rim — the same thing the owner will read every page on once he is in.
        Content = new Border
        {
            Padding = new Thickness(Tokens.S8, Tokens.S10),
            Child = new Border
            {
                MaxWidth = Tokens.SetupMaxWidth, HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = Tokens.Solid, BorderBrush = Tokens.PaneRim, BorderThickness = new Thickness(1),
                CornerRadius = Tokens.RadiusSheet, Padding = new Thickness(Tokens.S10, Tokens.S10, Tokens.S10, Tokens.S8),
                Child = slot
            }
        }
    };

    /// <summary>
    /// THE OLDER HOME'S SCREEN (<c>U-windows-release</c>), its press in the column itself (<c>U-owner-words</c>): in a row, where nothing bounds
    /// a button's width, the armed sentence did not wrap and was cut at the column's edge — the one press that says in full what it does.
    /// </summary>
    internal static Control OlderHomeScreen(string message, string? problem, string confirmLabel, Func<Task> startFresh) =>
        Ui.Col(Tokens.S4,
            Ui.H1("This TradeAgent does not open your older folder"),
            Ui.Body(message),
            Ui.With(Ui.Body(problem ?? ""), t => { t.Foreground = Tokens.Caution; t.IsVisible = problem is not null; }),
            Ui.Confirm("Start fresh", confirmLabel, startFresh));

    /// <summary>
    /// A HOME WHOSE SAVED PLATFORM IS ATAS, REFUSED BY A BUILD WITHOUT THE ADAPTER, AND THE OWNER'S WAY ON (<c>U-atas-home-no-atas-build</c>):
    /// the words, what became of his ATAS orders, and "Run on TradeAgent paper" — two presses, the second saying in full what it does.
    /// </summary>
    void ShowAtasNotInThisBuild()
    {
        _setupSlot.Content = AtasNotInThisBuildScreen(_host.AtasRefusal!.Message, _host.AtasRefusal.SetupFinished, async () =>
        {
            if (await _host.RunOnPaperInsteadAsync()) { await AfterStartAsync(); return; }
            if (_host.AtasRefusal is not null) ShowAtasNotInThisBuild();
            else ShowFatal(_host.StartupProblem ?? "TradeAgent could not start.");
        });
        _setupSurface.IsVisible = true;
        _shellSurface.IsVisible = false;
    }

    /// <summary>The no-ATAS screen, its press in the column for the same reason as <see cref="OlderHomeScreen"/>'s.</summary>
    internal static Control AtasNotInThisBuildScreen(string message, bool setupFinished, Func<Task> runOnPaper) =>
        Ui.Col(Tokens.S4,
            Ui.H1("This TradeAgent has no ATAS"),
            Ui.Body(message),
            Ui.Body(setupFinished
                ? "You can run this TradeAgent on its own paper venue instead: real prices, simulated fills, no money. Your ATAS choice "
                  + "is kept, and a TradeAgent with ATAS goes back to it. Choose paper's account on the Settings page before it trades."
                : "Your setup was not finished. You can finish it in this TradeAgent instead: it asks you to choose its platform again — "
                  + "its own paper venue or the practice simulator."),
            Ui.Confirm(setupFinished ? "Run on TradeAgent paper" : "Finish setup without ATAS",
                setupFinished ? AtasHome.ConfirmPaper : AtasHome.ConfirmSetupAgain, runOnPaper));

    void ShowFatal(string message)
    {
        _setupSlot.Content = Ui.Col(Tokens.S4,
            Ui.H1("TradeAgent cannot start"),
            Ui.Body(message),
            Ui.Muted("If this keeps happening, open TradeAgent again and use Create support package on the Checks page."));
        _setupSurface.IsVisible = true;
        _shellSurface.IsVisible = false;
    }

    /// <summary>
    /// A failed action gets a strip under the header rather than a line buried on one page: the
    /// press that failed may well have happened on a different page from wherever the old message
    /// went, which made a broken button look like a button that did nothing.
    /// </summary>
    void ShowProblem(string message)
    {
        if (_shellBuilt && _shellSurface.IsVisible)
        {
            _errorText.Text = message;
            _errorBar.IsVisible = true;
            return;
        }
        _wizard?.ShowProblem(message);
    }

    // ---- shell construction --------------------------------------------------------------------

    void EnsureShell()
    {
        _setupSurface.IsVisible = false;
        _shellSurface.IsVisible = true;
        if (_shellBuilt) { _wizard = null; return; }
        _shellBuilt = true;
        _wizard = null;

        BuildPages();

        // THE FLOOR IS THE WINDOW (U-ui-world): it lies under every row, and everything else is glass laid over it.
        var floor = _floor!;
        floor.Root[Grid.RowSpanProperty] = 4;
        _shellSurface.Children.Add(floor.Root);

        _shellSurface.Children.Add(Ui.With(BuildStatus(), c => c[Grid.RowProperty] = 0));

        // WHAT OUTRANKS EVERY PAGE, in a column of its own above the sheet and on screen whatever is selected. First: an earlier armed
        // home's refusal and its one press (fix pass 1, Codex P2) — while it stands every order is refused, so it outranks the rest.
        _notices.Children.Add(BuildEarlierArmedStrip());
        // The owner's severe finding straight after it: money first, then his private line (U-org-watcher-b).
        _notices.Children.Add(BuildOversightBanner());
        _notices.Children.Add(BuildErrorBar());
        // Last of the strips on purpose. A waiting approval is money; a failed press is a
        // broken button; a new version is neither, and must never sit above either of them.
        _notices.Children.Add(BuildUpdateBanner());
        _shellSurface.Children.Add(Ui.With(_notices, c => c[Grid.RowProperty] = 1));

        _shellSurface.Children.Add(Ui.With(_sheet!.Root, c => c[Grid.RowProperty] = 2));

        _dock = new Dock(Select, () => _finder?.Show());
        _shellSurface.Children.Add(Ui.With(_dock.Root, c => c[Grid.RowProperty] = 3));

        _finder = new Finder(() => _pages.Select(p => (p.Key, p.Value)), Select);
        _finder.Root[Grid.RowSpanProperty] = 4;
        _shellSurface.Children.Add(_finder.Root);

        // THE GLASS IS THE STAGE'S TO DRAW: wherever a part of the window lies over the floor, the floor paints itself out of focus
        // behind it. Asked for on every frame, so a sheet half-way in is glass half-way in.
        var stage = floor.Stage;
        stage.Glass = () =>
        {
            var panes = new List<FloorFrame.Pane>(4);
            if (_sheet.Pane(stage) is { } sheet) panes.Add(sheet);
            if (_status is not null && Shell.PaneOf(_status, stage, (float)(_status.Bounds.Height / 2)) is { } status) panes.Add(status);
            if (_dock.Pane(stage) is { } dock) panes.Add(dock);
            if (_finder.Pane(stage) is { } finder) panes.Add(finder);
            return [.. panes];
        };
        _sheet.Moved += stage.Wake;
        stage.Failed += _ => Opaque();
        _shellSurface.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty) Lay();
        };
        _notices.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty) Lay();
        };
        _finder.Root.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty) stage.Wake();
        };
        KeyDown += OnKey;
        // THE GLASS IS LIT A LITTLE WHERE THE POINTER IS: the window says where the pointer is, and the stage draws the light. Heard
        // as the event goes down to whatever is under the pointer, and never handled: no press is taken from anything.
        AddHandler(PointerMovedEvent, (_, e) =>
        {
            var at = e.GetPosition(stage);
            var onGlass = e.Source is Visual v && !ReferenceEquals(v, stage) && v.GetVisualAncestors().Prepend(v).Any(a => ReferenceEquals(a, _sheet.Root) || ReferenceEquals(a, _dock.Root) || ReferenceEquals(a, _status));
            stage.PointerOnGlass(onGlass ? new System.Numerics.Vector2((float)at.X, (float)at.Y) : null);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        PointerExited += (_, _) => stage.PointerOnGlass(null);

        // THE APP OPENS ON THE FLOOR ITSELF (U-ui-alive): the organisation, lit where it is working — the answer to "what is it
        // doing?" before anything is pressed — and when no AI is running it carries the same "Start the AI" the Chat page does, so the
        // app still opens on a designed answer to "so how do I start?".
        Select(Page.Floor);
        floor.Shown(true);
        // FOR LOOKING AT ONE PAGE IN A WINDOW THAT CANNOT BE DRIVEN (a developer's screenshot): the page named is shown once the
        // window is up. It presses nothing and is read once; unset or misspelt, the app opens on the floor as it always does.
        if (Enum.TryParse<Page>(Environment.GetEnvironmentVariable("TA_UI_PAGE"), ignoreCase: true, out var asked))
            Dispatcher.UIThread.Post(() => Select(asked), DispatcherPriority.Background);
    }

    /// <summary>
    /// The window's size, or what lies over the floor, changed: the sheet takes its share of the width, and the floor is told how
    /// much of each side is taken, so the organisation stands in what is left.
    /// </summary>
    void Lay()
    {
        if (_sheet is null || _floor is null || _dock is null) return;
        var width = _shellSurface.Bounds.Width;
        if (width <= 0) return;
        _sheet.Root.Width = Sheet.WidthIn(width);
        _dock.Compact(width < Tokens.DockWordsFrom);
        var top = (_status?.Bounds.Height ?? 0) + Tokens.S3 + _notices.Bounds.Height;
        var bottom = _dock.Root.Bounds.Height + Tokens.S3;
        _floor.Reserve(top, _sheet.Open ? _sheet.Root.Width + Tokens.SheetGap : 0, bottom);
    }

    /// <summary>The floor cannot be drawn, so nothing is drawn behind the glass: the glass is given the window's own dark to be read on.</summary>
    void Opaque()
    {
        if (_sheet is not null) _sheet.Root.Background = Tokens.Solid;
        if (_dock is not null) _dock.Root.Background = Tokens.Solid;
        if (_status is not null) _status.Background = Tokens.Solid;
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        if (!_shellSurface.IsVisible) return;
        if (e.Key == Key.K && (e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control)))
        {
            if (_finder is { Open: true }) _finder.Close();
            else _finder?.Show();
            e.Handled = true;
            return;
        }
        // CTRL (OR ⌘) AND A FIGURE GOES TO A PLACE, in the dock's order: 1 the floor … 6 the machine.
        if ((e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control)) && e.Key is >= Key.D1 and <= Key.D6
            && e.Key - Key.D1 is var nth && nth < Places.All.Count)
        {
            Select(Places.All[nth].Leaves[0].Page);
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Escape || e.Handled) return;
        if (_finder is { Open: true }) { _finder.Close(); e.Handled = true; }
        // Escape puts the sheet away and gives the floor the window back — never while the owner is typing: there it is the box's own key.
        else if (_shown != Page.Floor && e.Source is not TextBox) { Select(Page.Floor); e.Handled = true; }
    }

    /// <summary>
    /// WHAT STATE THE APP IS IN, on glass at the top right: the mode, the platform and account, whether the AI may trade — and the stop.
    /// </summary>
    Control BuildStatus()
    {
        // THE AI LINE TAKES WHAT IS LEFT AND IS CUT WITH AN ELLIPSIS, never run under the stop button: its reason can be a whole sentence
        // ("paused — no account has been chosen — choose one on the Settings page"), and the full sentence is on the Dashboard and in
        // the line's own tooltip.
        _metaAi.MaxWidth = Tokens.StatusLineMaxWidth;
        var ai = Ui.Row(Tokens.S2, _aiDot, _metaAi);
        ai.VerticalAlignment = VerticalAlignment.Center;

        // The kill switch lives in the chrome, not on a page. A stop that is one nav click away is
        // not a stop.
        _stopButton = BuildKillSwitch(
            () => _host.Gateway.Settings.AiTradingStopped,
            () => _host.Gateway.StopAiTrading($"you pressed {Labels.StopAiTrading}"),
            () => _host.Gateway.EnableAiTrading());
        _stopButton.VerticalAlignment = VerticalAlignment.Center;
        _stopButton.MaxWidth = Tokens.StopMaxWidth;
        // THE STOP'S LIGHT: red while a press would stop, gone once it has — then the words beside it are RESUME, and say so.
        _stopLight.VerticalAlignment = VerticalAlignment.Center;
        var stop = Ui.Row(0, _stopLight, _stopButton);
        stop.VerticalAlignment = VerticalAlignment.Center;

        var row = Ui.Row(Tokens.S3,
            _modePillHost,
            _metaPlatform,
            Ui.With(Ui.Row(Tokens.S2, MetaLabel("Account"), _metaAccount), r => r.VerticalAlignment = VerticalAlignment.Center),
            Sep(),
            ai,
            Sep(),
            stop);
        row.VerticalAlignment = VerticalAlignment.Center;

        _status = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(Tokens.S3, Tokens.S3, Tokens.SheetGap, Tokens.S2),
            Padding = new Thickness(Tokens.S4, 4, Tokens.S1, 4),
            CornerRadius = Tokens.Pill, Background = Tokens.GlassFill, Child = row
        };
        return _status;
    }

    /// <summary>The earlier armed home's strip: built once with the shell, updated in place first on every tick.</summary>
    Control BuildEarlierArmedStrip()
    {
        _earlierArmed = EarlierArmedStrip(_host);
        // MARGINS ON THE CARD AND ITS NOTE, never on a wrapper: a hidden control takes no room, so a home nothing armed shows no band.
        _earlierArmed.Card.Margin = new Thickness(Tokens.S5, Tokens.S2, Tokens.S5, 0);
        _earlierArmed.Note.Margin = new Thickness(Tokens.S5, Tokens.S2);
        return _earlierArmed.Root;
    }

    /// <summary>
    /// THE STRIP OVER THIS HOST: the gateway's own reading and its one press, and nothing else in the app calls that press. Static and
    /// handed the host, so the strip a test presses is the strip the window shows.
    /// </summary>
    internal static OwnerPage.EarlierArmedHome EarlierArmedStrip(AppHost host) =>
        new(() => host.Gateway.EarlierLiveSetting, () => host.Gateway.SetAsideEarlierLiveSetting());

    /// <summary>The AI line's tone: the dot beside it.</summary>
    internal enum AiTone { Allowed, Paused, Stopped, Refused }

    /// <summary>
    /// THE AI LINE (fix pass 1, Codex P2). An earlier armed home comes FIRST, before "stopped": while it stands every order is
    /// refused whatever the kill switch says, and "AI trading stopped" would hide the one thing the owner has to do — set it aside.
    /// </summary>
    internal static (string Text, AiTone Tone) AiLine(GatewayStatus status, Core.Board.EarlierLiveSetting? earlier) =>
        earlier is not null ? (Labels.HeaderEarlierLiveSetting, AiTone.Refused)
        : status.AiTradingStopped ? ("AI trading stopped", AiTone.Stopped)
        : status.ExecutionAvailable ? ("AI trading allowed", AiTone.Allowed)
        : ($"AI paused — {status.ExecutionBlockedReason}", AiTone.Paused);

    /// <summary>The owner's in-app alert for a severe finding (<see cref="OversightBanner"/>): built once, updated in place.</summary>
    Control BuildOversightBanner()
    {
        _oversightBanner = new OversightBanner(OversightSources.For(_host), () => Select(Page.Oversight));
        return _oversightBanner.Root;
    }

    Control BuildErrorBar()
    {
        var dismiss = Ui.Ghost("Dismiss", () => _errorBar.IsVisible = false);
        dismiss.VerticalAlignment = VerticalAlignment.Center;
        dismiss[Grid.ColumnProperty] = 2;

        var dot = new Ellipse
        {
            Width = 7, Height = 7, Fill = Tokens.Danger,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, Tokens.S3, 0)
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(dot);
        grid.Children.Add(Ui.With(_errorText, t => t[Grid.ColumnProperty] = 1));
        grid.Children.Add(dismiss);

        _errorBar.Child = grid;
        return _errorBar;
    }

    /// <summary>
    /// The update prompt, and deliberately not a popup.
    ///
    /// A modal over a trading screen covers the position it is asking about, and this one arrives
    /// unannounced because a background check happened to answer. So it is a strip, in the same
    /// vocabulary as the two above it, and every button on it is refusable: What's new reads, Later
    /// dismisses for this run, and Install is two presses because it closes the program holding the
    /// user's open orders.
    /// </summary>
    Control BuildUpdateBanner()
    {
        _updateNotes = Ui.Ghost("What's new", () =>
        {
            var url = _host.Updates.Available?.ReleaseUrl;
            if (!string.IsNullOrWhiteSpace(url)) Browser.TryOpen(url);
        });
        _updateInstall = Ui.Confirm("Install update", "Confirm: close TradeAgent and install",
            () => InstallUpdateAsync(_host));
        _updateLater = Ui.Ghost("Later", () => _host.Updates.Dismiss());

        var actions = Ui.Row(Tokens.S2, _updateNotes, _updateInstall, _updateLater);
        actions.VerticalAlignment = VerticalAlignment.Center;
        actions[Grid.ColumnProperty] = 2;

        var dot = new Ellipse
        {
            Width = 7, Height = 7, Fill = Tokens.Accent,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, Tokens.S3, 0)
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(dot);
        grid.Children.Add(Ui.With(_updateBannerText, t => t[Grid.ColumnProperty] = 1));
        grid.Children.Add(actions);

        _updateBanner.Child = grid;
        return _updateBanner;
    }

    void BuildPages()
    {
        _sheet = new Sheet(() => Select(Page.Floor));
        _floor = new FloorPage(FloorSources.For(_host), StartOrStopAgentAsync, Select);
        _chat = new ChatView(_host, StartOrStopAgentAsync);
        var org = OrgSurfaceSources.For(_host);
        _organisation = new OrganisationPage(org);
        _work = new WorkPage(org);
        _costs = new CostsPage(org);
        _dashboard = new DashboardPage(_host, StartOrStopAgentAsync);
        _boardPage = new BoardPage(_host.Board);
        _report = new ReportPage(_host);
        _inbox = new InboxPage(_host);
        // SETTINGS FIRST: it builds the owner's connection, evidence and update sections, which the owner's page shows (U-settings-shed).
        _settings = new SettingsPage(_host);
        _owner = new OwnerPage(_host, _settings);
        _forYou = new OwnerInboxPage(OwnerInboxSources.For(_host));
        _activity = new ActivityPage(_host);
        _checks = new ChecksPage(_host);
        _oversightPage = new OversightPage(OversightSources.For(_host));

        // EVERY PAGE BUT THE FLOOR'S OWN is a sheet over the floor (Places). The floor is no page of the sheet: it is the window.
        Add(Page.Chat, _chat.Root);
        Add(Page.Organisation, _organisation.Root);
        Add(Page.Work, _work.Root);
        Add(Page.Costs, _costs.Root);
        Add(Page.Dashboard, _dashboard.Root);
        Add(Page.Board, _boardPage.Root);
        Add(Page.Report, _report.Root);
        Add(Page.Inbox, _inbox.Root);
        Add(Page.Owner, _owner.Root);
        Add(Page.ForYou, _forYou.Root);
        Add(Page.Settings, _settings.Root);
        Add(Page.Activity, _activity.Root);
        Add(Page.Checks, _checks.Root);
        Add(Page.Oversight, _oversightPage.Root);

        void Add(Page page, Control control)
        {
            control.IsVisible = false;
            _pages[page] = control;
            _sheet.Host.Children.Add(control);
        }
    }

    /// <summary>
    /// THE OWNER GOES SOMEWHERE. The floor's own overview puts the sheet away; any other page brings the sheet in with that page on it,
    /// and the camera goes to the thing the page is about. Nothing is rebuilt: the page was built with the shell and is only shown.
    /// </summary>
    void Select(Page page)
    {
        var was = _shown;
        _dock?.Select(page);
        foreach (var (key, control) in _pages) control.IsVisible = key == page;
        var sheet = Places.HasSheet(page);
        // Whatever the floor was replaying, a page is the present's: its figures beside a floor showing last week would be two times at once.
        if (sheet) _floor?.Present();
        _sheet?.Show(sheet);
        if (_floor is not null) _floor.Stage.Behind = sheet;
        _floor?.Go(page);
        if (sheet && (was != page || !_everShown)) ComeIn(_pages[page]);
        _everShown = true;
        // BROUGHT UP TO DATE AS IT IS SHOWN (U-perf-scan): a page the tick left alone while it was hidden is never shown stale.
        _shown = page;
        Lay();
        if (_lastStatus is { } status)
        {
            try { foreach (var shown in PagesToUpdate(page)) UpdatePage(shown, status); }
            catch (Exception ex) { ShowProblem(ex is TradeAgentException t ? t.Info.UserMessage : ex.Message); }
        }
        if (page == Page.Chat) _chat?.FocusComposer();
    }

    // ---- shell update --------------------------------------------------------------------------

    void UpdateShell(GatewayStatus status)
    {
        // FIRST: an earlier armed home's refusal, read off the gateway itself and never off the status — so nothing below it, a stopped
        // kill switch included, decides whether it shows.
        _earlierArmed?.Update();

        // The pill is the one element here that changes shape rather than text, so it is rebuilt —
        // but only when the mode actually changed, never on the five-second tick.
        var modeSignature = $"{status.Mode}|{status.LiveActivated}";
        if (modeSignature != _modeSignature)
        {
            _modeSignature = modeSignature;
            _modePillHost.Content = Ui.Pill(Ui.ModeLabel(status.Mode), Ui.ModeTone(status.Mode));
        }

        _metaPlatform.Text = Ui.PlatformLabel(status);
        _metaAccount.Text = status.AccountId ?? "none";

        var (aiText, aiTone) = AiLine(status, _host.Gateway.EarlierLiveSetting);
        _metaAi.Text = aiText;
        ToolTip.SetTip(_metaAi, aiText);
        _aiDot.Fill = aiTone switch
        {
            AiTone.Allowed => Tokens.Positive,
            AiTone.Paused => Tokens.Caution,
            _ => Tokens.Danger
        };

        // SetResting, not Content plus SetVariant: a half-pressed RESUME has to survive the
        // five-second tick, and only the control knows whether it is half-pressed.
        if (_stopButton is not null)
            Ui.SetResting(_stopButton,
                status.AiTradingStopped ? Labels.ResumeAiTrading : Labels.StopAiTrading,
                status.AiTradingStopped ? "resume" : "stop");
        _stopLight.IsVisible = !status.AiTradingStopped;

        _oversightBanner?.Update();
        RefreshUpdateBanner(status);
        UpdateMachineLight();

        // THE PAGE THE OWNER IS LOOKING AT, NOT ALL FOURTEEN (U-perf-scan, R28 G3): every tick rebuilt every page's read — the scorecards
        // among them — on this thread, for pages nobody could see. A page is brought up to date when it is shown (Select) and on every
        // tick while it is. The floor is the window, on screen behind every sheet, so it is looked at on every tick too.
        _lastStatus = status;
        if (_shown != Page.Floor) UpdatePage(Page.Floor, status);
        foreach (var page in PagesToUpdate(_shown)) UpdatePage(page, status);
    }

    /// <summary>The page shown on the sheet — or the floor's own overview, with no sheet, until the owner goes somewhere.</summary>
    Page _shown = Page.Floor;

    bool _everShown;

    /// <summary>
    /// A PAGE COMES IN (<c>U-ui-alive</c>): it rises a few pixels into place as it clears. Nothing is rebuilt and nothing waits on it — the
    /// page is already the page, updated and pressable, from its first frame; only how it is painted changes for a moment.
    /// </summary>
    void ComeIn(Control page)
    {
        // How a page is painted for a moment must never be what ends the program.
        try { Motion.Rise(page, Tokens.PageRise, lasts: Tokens.PageIn); }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            page.Opacity = 1;
            page.RenderTransform = null;
        }
    }

    /// <summary>The last tick's status, so a page selected between ticks is brought up to date at once.</summary>
    GatewayStatus? _lastStatus;

    /// <summary>
    /// THE PAGES ONE TICK UPDATES WHILE <paramref name="shown"/> IS SHOWN (<c>U-perf-scan</c>): that page, and the Settings page with the
    /// owner's page, whose connection, evidence and update sections are the Settings page's own controls (<c>U-settings-shed</c>).
    /// </summary>
    internal static IReadOnlyList<Page> PagesToUpdate(Page shown) => shown == Page.Owner ? [Page.Owner, Page.Settings] : [shown];

    void UpdatePage(Page page, GatewayStatus status)
    {
        switch (page)
        {
            case Page.Floor: _floor?.Update(status); break;
            case Page.Chat: _chat?.Update(); break;
            case Page.Organisation: _organisation?.Update(); break;
            case Page.Work: _work?.Update(); break;
            case Page.Costs: _costs?.Update(); break;
            case Page.Dashboard: _dashboard?.Update(status); break;
            case Page.Board: _boardPage?.Update(); break;
            case Page.Report: _report?.Update(); break;
            case Page.Inbox: _inbox?.Update(); break;
            case Page.Owner: _owner?.Update(status); break;
            case Page.ForYou: _forYou?.Update(); break;
            case Page.Settings: _settings?.Update(status); break;
            case Page.Activity: _activity?.Update(); break;
            case Page.Checks: _checks?.Update(); break;
            case Page.Oversight: _oversightPage?.Update(); break;
        }
    }

    /// <summary>
    /// The banner, in place. Nothing here is rebuilt on the five-second tick: a half-pressed
    /// "Confirm: close TradeAgent" must survive a refresh that changed nothing.
    /// </summary>
    void RefreshUpdateBanner(GatewayStatus status)
    {
        var updates = _host.Updates;

        // A refusal the owner has since acted on must not outlive the acting. This drops the
        // unconfirmed-order one the moment the order settles; every other refusal stays, because
        // nothing about them has changed.
        updates.ExpireStaleRefusal();

        var info = updates.Available;
        var working = updates.Stage is UpdateStage.Downloading or UpdateStage.Installing;

        // A REFUSAL, not any failure. "TradeAgent could not check for a newer version" is weather:
        // it must never cover a valid offer that is already on screen, which is what happens when a
        // six-hourly re-check fails behind the owner's back. A refusal is a decision with a reason,
        // and it is shown even when there is nothing left to offer — an ambiguous release nulls
        // Available, and silence about it is exactly the wall this unit exists to remove.
        var refused = updates.Refused && !string.IsNullOrWhiteSpace(updates.Message);

        // Downloading shows even after Later was pressed: the user asked for this from Settings and
        // has to be able to see it happening from wherever they are.
        _updateBanner.IsVisible = (info is not null || refused) && (working || !updates.Dismissed);
        if (!_updateBanner.IsVisible) return;

        // With nothing left on offer there is nothing to read and nothing to install — only the
        // reason. Later stays, because a strip the owner cannot put away is one they learn to look
        // past, and Dismissed still hides this one exactly as it hides an offer.
        var actionable = info is not null && !working;
        if (_updateNotes is not null) _updateNotes.IsVisible = actionable;
        if (_updateLater is not null) _updateLater.IsVisible = !working;
        if (_updateInstall is not null) _updateInstall.IsVisible = actionable;

        if (working)
        {
            _updateBannerText.Text = updates.Message ?? $"Installing TradeAgent {info?.Version}…";
            return;
        }

        if (info is null)
        {
            _updateBannerText.Text = updates.Message ?? "";
            return;
        }

        // The hard stop itself is NOT here. It is in UpdateService.InstallAsync, because there are
        // two Install buttons onto that one method and a check on either button is one the other
        // walks around — and because this line only runs on the five-second tick, so a button armed
        // while everything was clean stays pressable for up to five seconds after an order goes
        // UNKNOWN. What is left here is the cosmetic and the sentence explaining it.
        var unconfirmed = status.UnreconciledRequests > 0;
        var size = string.IsNullOrEmpty(info.SizeLabel) ? "" : $" · {info.SizeLabel}";

        _updateBannerText.Text =
            refused ? updates.Message
            : !updates.CanBeVerified
                ? $"TradeAgent {info.Version} was published without the checksum file that proves what it is, so it cannot be installed."
            : unconfirmed
                ? $"TradeAgent {info.Version} is available. It can be installed once the unconfirmed order is settled."
            : $"TradeAgent {info.Version} is available{size}. You are running {updates.CurrentVersion}.";

        if (_updateInstall is null) return;

        // Cosmetics only, both of them. The refusals they mirror are in InstallAsync and are the
        // things that actually stop an install; these keep the button from inviting a press that is
        // already known to end in a refusal.
        _updateInstall.IsEnabled = !unconfirmed && updates.CanBeVerified;

        // The armed label carries whatever the second press is actually going to interrupt. It is
        // never the bare word "Confirm", and it is never quieter than the truth.
        var cost =
            status.OpenRequests > 0 ? $", {(status.OpenRequests == 1 ? "1 order" : $"{status.OpenRequests} orders")} still working"
            : _host.Agent.Running ? ", stopping the AI"
            : "";
        Ui.Relabel(_updateInstall, "Install update", $"Confirm: close TradeAgent and install {info.Version}{cost}");
    }

    /// <summary>
    /// Downloads, verifies and starts the installer, then closes TradeAgent so Setup can replace the
    /// files it is running from. The new build starts itself: TradeAgent.iss is passed /relaunch=1.
    /// </summary>
    internal static async Task InstallUpdateAsync(AppHost host)
    {
        if (!await host.Updates.InstallAsync())
        {
            Ui.ReportError?.Invoke(host.Updates.Message ?? "The update could not be installed.");
            return;
        }

        // Shutdown, not Close: it raises the lifetime's Exit — NOT ShutdownRequested, which a forced
        // Shutdown skips (Avalonia 12.1.1) — and Exit's handler stops the AI and its whole tree, then
        // disposes the gateway, the pipe server and the database (TradeAgentApp). Setup is about to
        // replace the files this process is running from, and a half-closed database is not a thing to
        // hand an installer.
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    /// <summary>The machine's light in the dock: how this computer's parts stand, in the worst one's colour, and said in its tooltip.</summary>
    void UpdateMachineLight()
    {
        var health = _host.Health.Snapshot();
        var failed = health.Count(h => h.State == HealthState.FAILED);
        var attention = health.Count(h => h.State is HealthState.DEGRADED or HealthState.PAUSED);
        var starting = health.Count(h => h.State == HealthState.STARTING);
        var unknown = health.Count(h => h.State == HealthState.UNKNOWN);

        var (tone, text) =
            failed > 0 ? (Tokens.Danger, failed == 1 ? "1 part is not working" : $"{failed} parts are not working")
            : attention > 0 ? (Tokens.Caution, attention == 1 ? "1 part needs attention" : $"{attention} parts need attention")
            : starting > 0 ? (Tokens.Info, "Starting up…")
            : unknown > 0 ? (Tokens.Neutral, unknown == 1 ? "1 part not checked yet" : $"{unknown} parts not checked yet")
            : (Tokens.Positive, "All systems ready");

        _dock?.Light(Place.Machine, tone, text);
    }

    /// <summary>
    /// The same control as the one on the owner's page, built the same way and asking twice in the
    /// same direction — the header's copy is the one an owner reaches from every page, so a version
    /// of it that resumed on a single press would make the owner's page's second press decorative.
    /// It is the chrome's variant pair rather than the emergency fill; the behaviour is identical.
    /// </summary>
    internal static Button BuildKillSwitch(Func<bool> stopped, Action stop, Action resume) =>
        Ui.KillSwitch(stopped() ? "resume" : "stop", stopped, stop, resume);

    /// <summary>
    /// Starting the AI opens a conversation, not a console. The page switch is the whole point of
    /// this method living on the shell: the user pressed a button that means "let me talk to it",
    /// so the thing they talk to it in has to be what they are looking at when it comes up.
    ///
    /// The start itself is the host's, the one path setup and a restart take as well — see
    /// <see cref="AppHost.StartTheAiAsync"/>. A refusal propagates to the button, which puts it in the
    /// strip under the header, and the page stays where it was.
    /// </summary>
    async Task StartOrStopAgentAsync()
    {
        if (_host.Agent.Running) { await _host.Agent.StopAsync(); return; }

        await _host.StartTheAiAsync();

        Select(Page.Chat);
        _chat?.Update();
    }

    // ---- small helpers -------------------------------------------------------------------------

    /// <summary>Swaps a button between theme variants without hand-painting any of its colours.</summary>
    internal static void SetVariant(Button b, string variant)
    {
        foreach (var c in new[] { "primary", "secondary", "danger", "ghost" })
            if (c != variant) b.Classes.Remove(c);
        if (!b.Classes.Contains(variant)) b.Classes.Add(variant);
    }

    static Control Sep() => new Border
    {
        Width = 1, Height = 16, Background = Tokens.Line, VerticalAlignment = VerticalAlignment.Center
    };

    static TextBlock Meta(string text) => new()
    {
        Text = text, FontSize = Tokens.Small, Foreground = Tokens.TextMuted,
        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
    };

    static TextBlock MetaLabel(string text) => new()
    {
        Text = text, FontSize = Tokens.Micro, Foreground = Tokens.TextFaint,
        VerticalAlignment = VerticalAlignment.Center
    };

    static TextBlock MetaMono(string text) => new()
    {
        Text = text, FontSize = Tokens.Small, FontFamily = Tokens.Mono, Foreground = Tokens.Text,
        VerticalAlignment = VerticalAlignment.Center
    };

    // ---- shell-outs: a folder, a web page, or ATAS. Never a command prompt. ---------------------

    internal static void OpenAtasOrExplain(Action<string> explain)
    {
        var d = Connectors.Atas.AtasInstallation.Detect();
        if (d.InstallDir is null)
        {
            // Silence here read as a broken button. Say what happened instead.
            explain("TradeAgent could not find ATAS on this computer. Install ATAS from atas.net, then press Check everything.");
            return;
        }
        foreach (var exe in new[] { "ATAS.exe", "OFT.Platform.exe" })
        {
            var full = Path.Combine(d.InstallDir, exe);
            if (!File.Exists(full)) continue;
            try { Process.Start(new ProcessStartInfo(full) { UseShellExecute = true }); }
            catch (Exception ex) { explain($"TradeAgent found ATAS but could not start it: {ex.Message}"); }
            return;
        }
        explain($"TradeAgent found the ATAS folder ({d.InstallDir}) but no program to start inside it.");
    }

    /// <summary>
    /// WHAT THE OWNER READS WHEN SOMETHING IS NOT OPENED. In this window, like everything else —
    /// there is no console for it to go to, and a shell-out that fails silently is a button that
    /// does nothing.
    /// </summary>
    internal const string OnlyWebPagesAndOurFolders =
        "TradeAgent only opens web addresses that start with http:// or https://, and its own folders. " +
        "It did not open this:";

    /// <summary>
    /// Why this must not be handed to the shell, or null when it may be.
    ///
    /// <c>UseShellExecute = true</c> is the whole point of this method — it is how a web page reaches
    /// the owner's browser and a folder reaches Explorer without TradeAgent knowing which programs
    /// they are — and it is also why the string matters: the shell will run whatever a scheme handler
    /// or a file association says it should. Most of what arrives here is the app's own, but not all
    /// of it. The sign-in address is whatever a vendor's CLI printed, picked out by
    /// <c>RuntimeManifest.AuthUrlPattern</c>, and that pattern is overridable from
    /// <c>runtimes.json</c>; so are the help and download addresses beside it. Both built-in patterns
    /// are anchored on <c>https?://</c>, an override is anchored on nothing, and nothing between the
    /// regex and the shell looked (milestone review 2026-09-05b, UNVERIFIED 4).
    ///
    /// Two things pass. An absolute http or https URL — the whole of what a sign-in, a help page or
    /// a download page can honestly be. And a folder INSIDE TradeAgent's own directory that exists:
    /// the workspace, the inbox, the folder a support package was written to, which is the complete
    /// list of what the buttons here open. Nothing else, and note what that excludes: a program, a
    /// document, <c>file:</c>, a network share, and a path anywhere else on the machine. The folder
    /// test compares the text before it touches the disk, so a target naming a remote share does not
    /// become a connection to it just by being checked.
    /// </summary>
    internal static string? RefusedToOpen(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return $"{OnlyWebPagesAndOurFolders} nothing at all.";

        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return null;

        return IsOneOfOurFolders(target) ? null : $"{OnlyWebPagesAndOurFolders} {Short(target)}";
    }

    static bool IsOneOfOurFolders(string target)
    {
        try
        {
            var home = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Paths.Home));
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
            var how = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!full.Equals(home, how) && !full.StartsWith(home + Path.DirectorySeparatorChar, how)) return false;
            return Directory.Exists(full);
        }
        // A target the path rules cannot even parse is not one of our folders.
        catch (Exception) { return false; }
    }

    /// <summary>Bounded, because the refused string can be anything a vendor's program printed.</summary>
    static string Short(string s) => s.Length <= 120 ? s : s[..120] + "…";

    internal static void OpenPath(string path, Action<string>? explain = null) =>
        Open(path, explain, static target => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }));

    /// <summary>
    /// The decision and the shell-out, with the shell-out passed in so a test can watch the decision
    /// without a browser opening. Every refusal returns before <paramref name="launch"/> is reached.
    /// </summary>
    internal static void Open(string target, Action<string>? explain, Action<string> launch)
    {
        if (RefusedToOpen(target) is { } why) { explain?.Invoke(why); return; }
        try { launch(target); }
        catch (Exception ex) { explain?.Invoke($"Windows could not open {Short(target)}: {ex.Message}"); }
    }
}

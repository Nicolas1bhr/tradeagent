using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
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

namespace TradeAgent.App;

/// <summary>
/// The application shell: a fixed header, a left rail, and one visible page.
///
/// Two structural rules govern this file, and both exist because of defects that were real.
///
///   1. <b>Nothing here opens a console.</b> Talking to the AI happens in <see cref="ChatView"/>,
///      inside this window. The only processes this file starts are a folder, a URL or ATAS itself,
///      through the Windows shell — never a command prompt, and never the agent CLI.
///
///   2. <b>The tree is built once.</b> Pages are created in <see cref="EnsureShell"/> and swapped by
///      toggling <c>IsVisible</c>. The background loop raises a change event every five seconds, so
///      rebuilding on every refresh made diagnostics output vanish mid-read, reset the scroll
///      position, and silently disarmed a half-pressed two-step confirmation. Rebuilding a tree is
///      not a refresh.
/// </summary>
public sealed class MainWindow : Window
{
    enum Page { Chat, Dashboard, Inbox, Safety, Settings, Activity, Checks }

    readonly AppHost _host;

    // ---- setup surface ---------------------------------------------------------------------
    // Stretch plus MaxWidth centres the column: Avalonia arranges a stretched child that came out
    // narrower than its slot in the middle of it, so setup reads as a document rather than a band
    // of controls smeared across a 1180px window.
    readonly ContentControl _setupSlot = new()
    {
        MaxWidth = 880, HorizontalAlignment = HorizontalAlignment.Stretch
    };
    readonly ScrollViewer _setupSurface;
    OnboardingView? _wizard;

    // ---- shell -----------------------------------------------------------------------------
    readonly Grid _shellSurface = new()
    {
        RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*"), IsVisible = false
    };

    // header
    readonly ContentControl _modePillHost = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _metaPlatform = Meta("");
    readonly TextBlock _metaAccount = MetaMono("");
    readonly TextBlock _metaAi = Meta("");
    readonly Ellipse _aiDot = new()
    {
        Width = 7, Height = 7, Fill = Tokens.Neutral, VerticalAlignment = VerticalAlignment.Center
    };
    Button? _stopButton;
    string _modeSignature = "";

    // banners
    readonly Border _approvalBanner = new()
    {
        Background = Tokens.CautionSoft,
        BorderBrush = Tokens.Caution,
        BorderThickness = new Thickness(0, 0, 0, 1),
        Padding = new Thickness(Tokens.S5, Tokens.S3),
        IsVisible = false
    };
    readonly TextBlock _approvalBannerText = new()
    {
        FontSize = Tokens.Small, FontWeight = FontWeight.SemiBold, Foreground = Tokens.Caution,
        VerticalAlignment = VerticalAlignment.Center
    };
    readonly Border _errorBar = new()
    {
        Background = Tokens.DangerSoft,
        BorderBrush = Tokens.Danger,
        BorderThickness = new Thickness(0, 0, 0, 1),
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
        Background = Tokens.AccentSoft,
        BorderBrush = Tokens.Accent,
        BorderThickness = new Thickness(0, 0, 0, 1),
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

    // rail
    readonly StackPanel _navStack = new() { Spacing = 2 };
    readonly Dictionary<Page, Button> _nav = new();
    readonly Ellipse _healthDot = new()
    {
        Width = 7, Height = 7, Fill = Tokens.Neutral, VerticalAlignment = VerticalAlignment.Center
    };
    readonly TextBlock _healthSummary = new()
    {
        FontSize = Tokens.Small, Foreground = Tokens.TextMuted, TextWrapping = TextWrapping.Wrap
    };

    // pages
    readonly Grid _pageHost = new();
    readonly Dictionary<Page, Control> _pages = new();
    ChatView? _chat;
    DashboardPage? _dashboard;
    SafetyPage? _safety;
    SettingsPage? _settings;
    InboxPage? _inbox;
    ActivityPage? _activity;
    ChecksPage? _checks;

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

        _setupSurface = new ScrollViewer
        {
            Content = new Border { Padding = new Thickness(Tokens.S8, Tokens.S10), Child = _setupSlot }
        };

        Content = new Grid { Children = { _setupSurface, _shellSurface } };
    }

    // ---- lifecycle -----------------------------------------------------------------------------

    public async Task InitialiseAsync()
    {
        var started = await _host.StartAsync();
        if (!started)
        {
            ShowFatal(_host.StartupProblem ?? "TradeAgent could not start.");
            return;
        }
        // Anything thrown out of a button handler used to take the whole app down with it. A
        // nontechnical user cannot recover from a window that simply disappears.
        Ui.ReportError = message => Dispatcher.UIThread.Post(() => ShowProblem(message));
        _host.Changed += () => Dispatcher.UIThread.Post(() => _ = RefreshAsync());
        await RefreshAsync();
    }

    async Task RefreshAsync()
    {
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

        _shellSurface.Children.Add(Ui.With(BuildHeader(), c => c[Grid.RowProperty] = 0));
        _shellSurface.Children.Add(Ui.With(BuildApprovalBanner(), c => c[Grid.RowProperty] = 1));
        _shellSurface.Children.Add(Ui.With(BuildErrorBar(), c => c[Grid.RowProperty] = 2));
        // Last of the three strips on purpose. A waiting approval is money; a failed press is a
        // broken button; a new version is neither, and must never sit above either of them.
        _shellSurface.Children.Add(Ui.With(BuildUpdateBanner(), c => c[Grid.RowProperty] = 3));

        BuildPages();

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("210,*"), [Grid.RowProperty] = 4 };
        body.Children.Add(BuildRail());
        body.Children.Add(new Border
        {
            Padding = new Thickness(Tokens.S6),
            Child = _pageHost,
            [Grid.ColumnProperty] = 1
        });
        _shellSurface.Children.Add(body);

        // Chat is the landing page. It is the thing that replaced the console, and when no agent is
        // running it is the one screen with a designed answer to "so how do I start?". Nothing is
        // lost by not landing on the dashboard: mode, platform, account, AI state and any waiting
        // approval all live in the chrome, which is on screen whatever page is selected.
        Select(Page.Chat);
    }

    Control BuildHeader()
    {
        var brand = new TextBlock
        {
            Text = "TradeAgent", FontSize = Tokens.H2, FontFamily = Tokens.SansDisplay,
            FontWeight = FontWeight.SemiBold, Foreground = Tokens.Text,
            VerticalAlignment = VerticalAlignment.Center
        };

        var meta = Ui.Row(Tokens.S4,
            brand,
            Sep(),
            _modePillHost,
            Sep(),
            _metaPlatform,
            Sep(),
            Ui.With(Ui.Row(Tokens.S2, MetaLabel("Account"), _metaAccount),
                r => r.VerticalAlignment = VerticalAlignment.Center),
            Sep(),
            Ui.With(Ui.Row(Tokens.S2, _aiDot, _metaAi),
                r => r.VerticalAlignment = VerticalAlignment.Center));
        meta.VerticalAlignment = VerticalAlignment.Center;

        // The kill switch lives in the chrome, not on a page. A stop that is one nav click away is
        // not a stop.
        _stopButton = Ui.Danger("STOP AI TRADING", ToggleAiTrading);
        _stopButton.VerticalAlignment = VerticalAlignment.Center;
        _stopButton[Grid.ColumnProperty] = 2;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(meta);
        grid.Children.Add(_stopButton);

        return new Border
        {
            Height = 56,
            Background = Tokens.BgElevated,
            BorderBrush = Tokens.Line,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(Tokens.S5, 0),
            Child = grid
        };
    }

    Control BuildApprovalBanner()
    {
        var jump = Ui.Secondary("Review the request", () => Select(Page.Dashboard));
        jump.VerticalAlignment = VerticalAlignment.Center;
        jump[Grid.ColumnProperty] = 2;

        var dot = new Ellipse
        {
            Width = 7, Height = 7, Fill = Tokens.Caution,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, Tokens.S3, 0)
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(dot);
        grid.Children.Add(Ui.With(_approvalBannerText, t => t[Grid.ColumnProperty] = 1));
        grid.Children.Add(jump);

        _approvalBanner.Child = grid;
        return _approvalBanner;
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

    Control BuildRail()
    {
        AddNav(Page.Chat, "Chat");
        AddNav(Page.Dashboard, "Dashboard");
        AddNav(Page.Inbox, "Inbox");
        AddNav(Page.Safety, "Safety");
        // Between Safety and the diagnostic tail on purpose. Choosing the platform and the account
        // is a configuration act adjacent to the limits, not a thing you go looking for among logs.
        AddNav(Page.Settings, "Settings");
        AddNav(Page.Activity, "Activity");
        AddNav(Page.Checks, "Checks");

        var summary = new Button
        {
            Classes = { "nav" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                Children =
                {
                    Ui.With(_healthDot, d => d.Margin = new Thickness(0, 0, Tokens.S2, 0)),
                    Ui.With(_healthSummary, t => t[Grid.ColumnProperty] = 1)
                }
            }
        };
        summary.Click += (_, _) => Select(Page.Checks);

        var rail = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        rail.Children.Add(new Border { Padding = new Thickness(Tokens.S3), Child = _navStack });
        rail.Children.Add(new Border
        {
            BorderBrush = Tokens.Line,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(Tokens.S3),
            Child = summary,
            [Grid.RowProperty] = 1
        });

        return new Border
        {
            Background = Tokens.BgRail,
            BorderBrush = Tokens.Line,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = rail
        };
    }

    void AddNav(Page page, string label)
    {
        // Content is a bare string on purpose: the theme paints the selected state through the
        // "on" class on the button, and a TextBlock child of my own would take the app-level
        // TextBlock foreground instead and never turn indigo.
        var b = new Button
        {
            Content = label,
            Classes = { "nav" },
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        b.Click += (_, _) => Select(page);
        _nav[page] = b;
        _navStack.Children.Add(b);
    }

    void BuildPages()
    {
        _chat = new ChatView(_host, StartOrStopAgentAsync);
        _dashboard = new DashboardPage(_host, StartOrStopAgentAsync);
        _inbox = new InboxPage(_host);
        _safety = new SafetyPage(_host);
        _settings = new SettingsPage(_host);
        _activity = new ActivityPage(_host);
        _checks = new ChecksPage(_host);

        Add(Page.Chat, _chat.Root);
        Add(Page.Dashboard, _dashboard.Root);
        Add(Page.Inbox, _inbox.Root);
        Add(Page.Safety, _safety.Root);
        Add(Page.Settings, _settings.Root);
        Add(Page.Activity, _activity.Root);
        Add(Page.Checks, _checks.Root);

        void Add(Page page, Control control)
        {
            control.IsVisible = false;
            _pages[page] = control;
            _pageHost.Children.Add(control);
        }
    }

    void Select(Page page)
    {
        foreach (var (key, button) in _nav)
        {
            if (key == page) { if (!button.Classes.Contains("on")) button.Classes.Add("on"); }
            else button.Classes.Remove("on");
        }
        foreach (var (key, control) in _pages) control.IsVisible = key == page;
        if (page == Page.Chat) _chat?.FocusComposer();
    }

    // ---- shell update --------------------------------------------------------------------------

    void UpdateShell(GatewayStatus status)
    {
        // The pill is the one header element that changes shape rather than text, so it is rebuilt —
        // but only when the mode actually changed, never on the five-second tick.
        var modeSignature = $"{status.Mode}|{status.LiveActivated}";
        if (modeSignature != _modeSignature)
        {
            _modeSignature = modeSignature;
            _modePillHost.Content = Ui.Pill(Ui.ModeLabel(status.Mode), Ui.ModeTone(status.Mode));
        }

        _metaPlatform.Text = Ui.PlatformLabel(status);
        _metaAccount.Text = status.AccountId ?? "none";

        if (status.AiTradingStopped) { _metaAi.Text = "AI trading stopped"; _aiDot.Fill = Tokens.Danger; }
        else if (status.ExecutionAvailable) { _metaAi.Text = "AI trading allowed"; _aiDot.Fill = Tokens.Positive; }
        else { _metaAi.Text = $"AI paused — {status.ExecutionBlockedReason}"; _aiDot.Fill = Tokens.Caution; }

        if (_stopButton is not null)
        {
            _stopButton.Content = status.AiTradingStopped ? "RESUME AI TRADING" : "STOP AI TRADING";
            SetVariant(_stopButton, status.AiTradingStopped ? "primary" : "danger");
        }

        var waiting = _host.Gateway.Requests
            .Query("execution_state=$s", ("$s", ExecutionState.AWAITING_APPROVAL.ToString()));
        _approvalBanner.IsVisible = waiting.Count > 0;
        if (waiting.Count > 0)
            _approvalBannerText.Text = waiting.Count == 1
                ? "The AI is asking permission — 1 order waiting"
                : $"The AI is asking permission — {waiting.Count} orders waiting";

        RefreshUpdateBanner(status);
        UpdateRailHealth();

        _chat?.Update();
        _dashboard?.Update(status, waiting);
        _inbox?.Update();
        _safety?.Update(status);
        _settings?.Update(status);
        _activity?.Update();
        _checks?.Update();
    }

    /// <summary>
    /// The banner, in place. Nothing here is rebuilt on the five-second tick: a half-pressed
    /// "Confirm: close TradeAgent" must survive a refresh that changed nothing.
    /// </summary>
    void RefreshUpdateBanner(GatewayStatus status)
    {
        var updates = _host.Updates;
        var info = updates.Available;
        var working = updates.Stage is UpdateStage.Downloading or UpdateStage.Installing;

        // Downloading shows even after Later was pressed: the user asked for this from Settings and
        // has to be able to see it happening from wherever they are.
        _updateBanner.IsVisible = info is not null && (working || !updates.Dismissed);
        if (info is null || !_updateBanner.IsVisible) return;

        if (_updateNotes is not null) _updateNotes.IsVisible = !working;
        if (_updateLater is not null) _updateLater.IsVisible = !working;
        if (_updateInstall is not null) _updateInstall.IsVisible = !working;

        if (working)
        {
            _updateBannerText.Text = updates.Message ?? $"Installing TradeAgent {info.Version}…";
            return;
        }

        // The one hard stop. Everything else the user is allowed to decide, as long as the button
        // says what they are deciding.
        var unconfirmed = status.UnreconciledRequests > 0;
        var size = string.IsNullOrEmpty(info.SizeLabel) ? "" : $" · {info.SizeLabel}";

        _updateBannerText.Text = unconfirmed
            ? $"TradeAgent {info.Version} is available. It can be installed once the unconfirmed order is settled."
            : $"TradeAgent {info.Version} is available{size}. You are running {updates.CurrentVersion}.";

        if (_updateInstall is null) return;
        _updateInstall.IsEnabled = !unconfirmed;

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

        // Shutdown, not Close: it runs the lifetime's ShutdownRequested handler, which disposes the
        // gateway, the pipe server and the database. Setup is about to replace the files this
        // process is running from, and a half-closed database is not a thing to hand an installer.
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    void UpdateRailHealth()
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

        _healthDot.Fill = tone;
        _healthSummary.Text = text;
        _healthSummary.Foreground = tone == Tokens.Positive ? Tokens.TextMuted : tone;
    }

    void ToggleAiTrading()
    {
        if (_host.Gateway.Settings.AiTradingStopped) _host.Gateway.EnableAiTrading();
        else _host.Gateway.StopAiTrading("you pressed STOP AI TRADING");
    }

    /// <summary>
    /// Starting the AI opens a conversation, not a console. The page switch is the whole point of
    /// this method living on the shell: the user pressed a button that means "let me talk to it",
    /// so the thing they talk to it in has to be what they are looking at when it comes up.
    /// </summary>
    async Task StartOrStopAgentAsync()
    {
        if (_host.Agent.Running) { await _host.Agent.StopAsync(); return; }

        var id = _host.Gateway.Settings.SelectedRuntimeId ?? "opencode";
        var manifest = AgentRuntime.RuntimeCatalog.Find(id)
            ?? throw new TradeAgentException(ErrorCode.AI_RUNTIME_NOT_FOUND, $"no manifest for '{id}'");
        await _host.Agent.PrepareAsync(manifest, _host.WorkspaceContext());
        await _host.Agent.StartAsync();

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

    internal static void OpenPath(string path, Action<string>? explain = null)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { explain?.Invoke($"Windows could not open {path}: {ex.Message}"); }
    }
}

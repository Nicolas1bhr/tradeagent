using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TradeAgent.ConnectorSdk;
using TradeAgent.Core;
using TradeAgent.Gateway;

namespace TradeAgent.App;

/// <summary>
/// Everything in the shell that is not the conversation: the live picture, the safety controls, the
/// activity log and the self-checks.
///
/// Every page here is built once and updated in place. Where a list has to change shape — health
/// rows, activity lines, pending approvals — the new contents are compared against a signature
/// string first, so a five-second background tick that changed nothing touches nothing. That is not
/// an optimisation: rebuilding the approvals list disarms a half-pressed "Confirm: place this
/// order", and rebuilding the activity list throws away the user's scroll position.
/// </summary>
static class Pages
{
    public static Control Header(string title, string subtitle) =>
        Ui.With(Ui.Col(Theme.S1, Ui.H1(title), Ui.Muted(subtitle)),
            c => c.Margin = new Thickness(0, 0, 0, Theme.S6));

    /// <summary>A scrolling page body with room for the scrollbar to sit outside the content.</summary>
    public static ScrollViewer Scroll(Control content) => new()
    {
        Content = content,
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        Padding = new Thickness(0, 0, Theme.S3, 0)
    };

    public static Control Column(int index, Control content)
    {
        content[Grid.ColumnProperty] = index;
        return content;
    }
}

// =================================================================================================

/// <summary>The live picture, plus anything the AI is currently waiting on the user to answer.</summary>
sealed class DashboardPage
{
    static readonly string[] FactKeys =
        ["Trading mode", "Platform", "Account", "AI trading", "Open orders / unconfirmed"];

    readonly AppHost _host;
    readonly Func<Task> _toggleAgent;

    readonly Dictionary<string, TextBlock> _values = new();
    readonly StackPanel _healthRows = new() { Spacing = 2 };
    readonly StackPanel _approvals = new() { Spacing = Theme.S3 };
    readonly Border _approvalsCard;
    readonly Button _agentButton;

    string _healthSignature = "";
    string _approvalSignature = "";

    public Control Root { get; }

    public DashboardPage(AppHost host, Func<Task> toggleAgent)
    {
        _host = host;
        _toggleAgent = toggleAgent;

        var facts = new StackPanel { Spacing = 2 };
        foreach (var key in FactKeys)
        {
            // Counts and account numbers are compared with other numbers, so they are set in the
            // mono face; the two prose values are not.
            var value = key is "Open orders / unconfirmed" or "Account"
                ? Ui.Mono("—")
                : Ui.With(Ui.Body("—"), t => t.FontSize = Theme.Small);
            _values[key] = value;
            facts.Children.Add(Ui.KeyValueLive(key, value));
        }

        _agentButton = Ui.Primary("Start the AI", async () => await _toggleAgent());

        var actions = Ui.Row(Theme.S2,
            _agentButton,
            Ui.Ghost("Open ATAS", () => MainWindow.OpenAtasOrExplain(m => Ui.ReportError?.Invoke(m))),
            Ui.Ghost("Open the AI's folder", () => MainWindow.OpenPath(Paths.Workspace, m => Ui.ReportError?.Invoke(m))));
        actions.Margin = new Thickness(0, Theme.S4, 0, 0);

        // Orders waiting on the user. In "Real, ask me first" the AI proposes and stops; without a
        // way to say yes here, that mode had no exit and the only usable real-money setting was the
        // fully automatic one. The card hides itself when there is nothing to answer.
        _approvalsCard = new Border
        {
            Background = Theme.CautionSoft,
            BorderBrush = Theme.Caution,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.Radius,
            Padding = new Thickness(Theme.S5),
            IsVisible = false,
            Child = Ui.Col(Theme.S4,
                Ui.With(Ui.Eyebrow("The AI is asking permission"), t => t.Foreground = Theme.Caution),
                _approvals)
        };

        var left = Ui.Col(Theme.S6,
            _approvalsCard,
            Ui.Section("Right now", Ui.Col(0, facts, actions)));

        var right = Ui.Section("System health", _healthRows);
        right.Margin = new Thickness(Theme.S5, 0, 0, 0);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,340") };
        grid.Children.Add(Pages.Column(0, left));
        grid.Children.Add(Pages.Column(1, right));

        Root = Pages.Scroll(Ui.Col(0,
            Pages.Header("Dashboard", "What TradeAgent and the AI are doing right now."),
            grid));
    }

    public void Update(GatewayStatus status, IReadOnlyList<ExecutionRequest> waiting)
    {
        _values["Trading mode"].Text = Ui.ModeLabel(status.Mode);
        _values["Platform"].Text =
            Ui.PlatformLabel(status.ConnectorName, status.ConnectorIsPaper);
        _values["Account"].Text = status.AccountId ?? "not selected";
        _values["AI trading"].Text = status.AiTradingStopped ? "STOPPED"
            : status.ExecutionAvailable ? "allowed"
            : $"paused — {status.ExecutionBlockedReason}";
        _values["AI trading"].Foreground = status.AiTradingStopped ? Theme.Danger
            : status.ExecutionAvailable ? Theme.Positive
            : Theme.Caution;
        _values["Open orders / unconfirmed"].Text = $"{status.OpenRequests} / {status.UnreconciledRequests}";

        _agentButton.Content = _host.Agent.Running ? "Stop the AI" : "Start the AI";
        MainWindow.SetVariant(_agentButton, _host.Agent.Running ? "secondary" : "primary");

        RefreshApprovals(waiting);

        var health = _host.Health.Snapshot();
        var hs = string.Join('|', health.Select(h => $"{h.Component}:{h.State}:{h.Detail}"));
        if (hs != _healthSignature)
        {
            _healthSignature = hs;
            _healthRows.Children.Clear();
            foreach (var h in health) _healthRows.Children.Add(Ui.StatusRow(h));
        }
    }

    void RefreshApprovals(IReadOnlyList<ExecutionRequest> waiting)
    {
        var signature = string.Join('|', waiting.Select(w => w.RequestId));
        if (signature == _approvalSignature) return;
        _approvalSignature = signature;

        _approvals.Children.Clear();
        _approvalsCard.IsVisible = waiting.Count > 0;

        var first = true;
        foreach (var w in waiting)
        {
            var id = w.RequestId;
            var row = Ui.Col(Theme.S2,
                new TextBlock
                {
                    Text = TryDescribe(w), FontFamily = Theme.Mono, FontSize = Theme.Base,
                    FontWeight = FontWeight.SemiBold, Foreground = Theme.Text, TextWrapping = TextWrapping.Wrap
                },
                Ui.Micro($"asked at {w.CreatedAt.ToLocalTime():HH:mm}"),
                Ui.With(Ui.Row(Theme.S2,
                        Ui.Confirm("Approve", "Confirm: place this order",
                            async () => await _host.Gateway.ApproveAsync(id)),
                        Ui.Secondary("Decline", () => _host.Gateway.Decline(id))),
                    r => r.Margin = new Thickness(0, Theme.S2, 0, 0)));

            if (!first)
            {
                row.Margin = new Thickness(0, Theme.S3, 0, 0);
                _approvals.Children.Add(new Border { Height = 1, Background = Theme.Caution, Opacity = 0.35 });
            }
            first = false;
            _approvals.Children.Add(row);
        }
    }

    static string TryDescribe(ExecutionRequest r)
    {
        try
        {
            var i = Json.Read<PlaceIntent>(r.ParametersJson);
            if (i is null) return $"{r.Intent} {r.Instrument}";
            var price = i.LimitPrice is { } lp ? $" at {lp}" : " at market";
            return $"{i.Side} {i.Quantity} {i.Symbol}{(i.Type == OrderType.Market ? " at market" : price)}";
        }
        catch (Exception) { return $"{r.Intent} {r.Instrument}"; }
    }
}

// =================================================================================================

/// <summary>
/// Mode, limits and the emergency controls. Everything that can widen the AI's authority is a
/// deliberate two-step; everything that narrows it is one press.
/// </summary>
sealed class SafetyPage
{
    readonly AppHost _host;

    readonly StackPanel _modeRow = new() { Orientation = Orientation.Horizontal, Spacing = Theme.S2 };
    readonly TextBlock _modeNote = Ui.Muted("");
    readonly TextBlock _liveNote = Ui.Body("");
    readonly Button _liveButton;
    readonly Button _stopButton;
    readonly NumericUpDown _maxQty, _maxNotional, _maxPositions, _maxPerMinute;
    readonly TextBox _allowlist;
    readonly TextBlock _limitsNote = Ui.Micro("");

    public Control Root { get; }

    public SafetyPage(AppHost host)
    {
        _host = host;

        foreach (var mode in Enum.GetValues<TradingMode>())
        {
            var m = mode;
            _modeRow.Children.Add(Ui.Secondary(Ui.ModeLabel(m), () => _host.Gateway.SetMode(m)));
        }

        _liveButton = Ui.Confirm("Switch real-money trading ON", "Confirm: allow real money",
            () => _host.Gateway.ActivateLive(!_host.Gateway.Settings.LiveActivated));
        _liveButton.HorizontalAlignment = HorizontalAlignment.Left;

        var modeCard = Ui.Section("Trading mode", Ui.Col(Theme.S4,
            _modeRow,
            _modeNote,
            Ui.Divider(),
            _liveNote,
            _liveButton));

        // The emergency block. The stop is one press in both directions because a mis-press that
        // removes the AI's permission to trade costs nothing, and hesitation here costs money.
        _stopButton = Ui.Big("STOP AI TRADING", Theme.Danger, () =>
        {
            if (_host.Gateway.Settings.AiTradingStopped) _host.Gateway.EnableAiTrading();
            else _host.Gateway.StopAiTrading("you pressed STOP AI TRADING");
        });

        var emergency = Ui.Section("Emergency", Ui.Col(Theme.S4,
            _stopButton,
            Ui.Muted("Stopping the AI removes its permission to trade. It does not touch your orders or positions."),
            Ui.Divider(),
            Ui.With(Ui.Confirm("Cancel all working orders", "Confirm: cancel all working orders",
                    async () => await _host.Gateway.OperatorCancelAllAsync()),
                b => b.HorizontalAlignment = HorizontalAlignment.Stretch),
            Ui.With(Ui.Confirm("Close all positions", "Confirm: close all positions with market orders",
                    async () => await _host.Gateway.OperatorCloseAllAsync()),
                b => b.HorizontalAlignment = HorizontalAlignment.Stretch)));
        emergency.Margin = new Thickness(Theme.S5, 0, 0, 0);

        var r = _host.Gateway.Settings.Risk;
        _maxQty = Ui.NumberField(r.MaxOrderQuantity, 0m, 1m);
        _maxNotional = Ui.NumberField(r.MaxNotionalPerOrder, 0m, 1000m);
        _maxPositions = Ui.NumberField(r.MaxOpenPositions);
        _maxPerMinute = Ui.NumberField(r.MaxOrdersPerMinute);
        _allowlist = Ui.TextField(string.Join(", ", r.InstrumentAllowlist), "any");

        var limits = Ui.Section("Safety limits", Ui.Col(Theme.S2,
            Ui.Muted("The AI cannot change these and has no command to ask. Small numbers are the point."),
            Ui.Spacer(Theme.S2),
            Ui.FieldRow("Most it may buy or sell in one order", _maxQty),
            Ui.FieldRow("Most money one order may be worth", _maxNotional,
                "0 means not enforced. For futures this is the right default — one contract is worth far more on paper than it costs to trade."),
            Ui.FieldRow("Most positions it may hold at once", _maxPositions),
            Ui.FieldRow("Most orders per minute", _maxPerMinute),
            Ui.FieldRow("Instruments it may touch", _allowlist,
                "Comma separated. Leave empty to allow any the platform offers."),
            Ui.Spacer(Theme.S2),
            Ui.With(Ui.Primary("Save limits", SaveLimits), b => b.HorizontalAlignment = HorizontalAlignment.Left),
            _limitsNote));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,340") };
        grid.Children.Add(Pages.Column(0, Ui.Col(Theme.S6, modeCard, limits)));
        grid.Children.Add(Pages.Column(1, emergency));

        Root = Pages.Scroll(Ui.Col(0,
            Pages.Header("Safety", "What the AI is allowed to do, and how to take it away instantly."),
            grid));
    }

    public void Update(GatewayStatus status)
    {
        var i = 0;
        foreach (var mode in Enum.GetValues<TradingMode>())
            if (_modeRow.Children[i++] is Button b) Ui.Emphasise(b, status.Mode == mode);

        _modeNote.Text = status.Mode switch
        {
            TradingMode.OBSERVE => "The AI can read prices, positions and your account. It cannot place anything.",
            TradingMode.PAPER => "The AI trades against the practice simulator. No real money is involved.",
            TradingMode.LIVE_CONFIRM => "The AI proposes real orders and stops. Nothing reaches your broker until you approve it on the Dashboard.",
            TradingMode.LIVE_AUTONOMOUS => "The AI places real orders by itself, inside the safety limits below.",
            _ => ""
        };

        _liveNote.Text = status.LiveActivated
            ? "Real-money trading is switched ON."
            : "Real-money trading is switched OFF. The two real-money modes cannot reach your broker until you switch it on.";
        _liveNote.Foreground = status.LiveActivated ? Theme.Caution : Theme.TextMuted;

        Ui.Relabel(_liveButton,
            status.LiveActivated ? "Switch real-money trading OFF" : "Switch real-money trading ON",
            status.LiveActivated ? "Confirm: switch real money off" : "Confirm: allow real money");

        _stopButton.Content = status.AiTradingStopped ? "RESUME AI TRADING" : "STOP AI TRADING";
        _stopButton.Background = status.AiTradingStopped ? Theme.Positive : Theme.Danger;
    }

    /// <summary>
    /// The user guide and the agent's own AGENTS.md both said these were set in this window; until
    /// they were editable here they could only be changed by editing the database by hand.
    /// </summary>
    void SaveLimits()
    {
        _host.Gateway.Update(s =>
        {
            s.Risk.MaxOrderQuantity = _maxQty.Value ?? s.Risk.MaxOrderQuantity;
            s.Risk.MaxNotionalPerOrder = _maxNotional.Value ?? s.Risk.MaxNotionalPerOrder;
            s.Risk.MaxOpenPositions = (int)(_maxPositions.Value ?? s.Risk.MaxOpenPositions);
            s.Risk.MaxOrdersPerMinute = (int)(_maxPerMinute.Value ?? s.Risk.MaxOrdersPerMinute);
            s.Risk.InstrumentAllowlist = (_allowlist.Text ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        });
        _host.Gateway.Log.Activity("You changed the safety limits");
        _limitsNote.Text = "Saved. New orders are checked against these immediately.";
        _limitsNote.Foreground = Theme.Positive;
    }
}

// =================================================================================================

/// <summary>Everything that has happened, newest last, in one scroller that keeps its position.</summary>
sealed class ActivityPage
{
    readonly AppHost _host;
    readonly StackPanel _rows = new() { Spacing = Theme.S1 };
    readonly TextBlock _empty = Ui.Muted("Nothing has happened yet. Activity appears here as TradeAgent and the AI work.");
    readonly ScrollViewer _scroll;
    string _signature = "";

    public Control Root { get; }

    public ActivityPage(AppHost host)
    {
        _host = host;

        _scroll = new ScrollViewer
        {
            Content = Ui.Col(0, _empty, _rows),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };

        var card = Ui.Card(_scroll);
        card.ClipToBounds = true;
        card[Grid.RowProperty] = 1;

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(Pages.Header("Activity", "A plain-language record of what TradeAgent, the AI and you have done."));
        root.Children.Add(card);
        Root = root;
    }

    public void Update()
    {
        var activity = _host.Gateway.Log.RecentActivity(60);
        var signature = string.Join('|', activity.Select(a => $"{a.At.Ticks}:{a.Text}"));
        if (signature == _signature) return;
        _signature = signature;

        // The log is oldest-first, so the interesting end is the bottom. Follow it — but only for a
        // reader who was already there, so scrolling back through the morning is not undone by a
        // routine five-second refresh.
        var follow = _rows.Children.Count == 0
            || _scroll.Extent.Height <= _scroll.Viewport.Height
            || _scroll.Offset.Y >= _scroll.Extent.Height - _scroll.Viewport.Height - 32;

        _empty.IsVisible = activity.Count == 0;
        _rows.Children.Clear();
        foreach (var (at, level, text) in activity)
        {
            var warn = level == "warn";
            _rows.Children.Add(new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("56,*"),
                Children =
                {
                    Ui.With(Ui.Mono(at.ToLocalTime().ToString("HH:mm"), Theme.TextFaint),
                        t => t.VerticalAlignment = VerticalAlignment.Top),
                    new TextBlock
                    {
                        Text = text, FontSize = Theme.Small, TextWrapping = TextWrapping.Wrap,
                        Foreground = warn ? Theme.Caution : Theme.TextMuted,
                        [Grid.ColumnProperty] = 1
                    }
                }
            });
        }

        if (follow) Dispatcher.UIThread.Post(_scroll.ScrollToEnd, DispatcherPriority.Background);
    }
}

// =================================================================================================

/// <summary>The self-check and the support package — the two things to do before asking for help.</summary>
sealed class ChecksPage
{
    readonly AppHost _host;
    readonly TextBlock _output = Ui.Body("");
    readonly TextBlock _placeholder =
        Ui.Muted("Nothing checked yet. Press Check everything and TradeAgent will test each part in turn.");
    readonly Button _showPackage;
    string? _packagePath;

    public Control Root { get; }

    public ChecksPage(AppHost host)
    {
        _host = host;

        _showPackage = Ui.Ghost("Show the file", () =>
        {
            if (_packagePath is null) return;
            MainWindow.OpenPath(Path.GetDirectoryName(_packagePath) ?? Paths.Home, m => Ui.ReportError?.Invoke(m));
        });
        _showPackage.IsVisible = false;

        var buttons = Ui.Row(Theme.S2,
            Ui.Primary("Check everything", RunDoctorAsync),
            Ui.Secondary("Create support package", CreatePackage),
            _showPackage);

        var well = new Border
        {
            Background = Theme.BgSunken,
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.Radius,
            Padding = new Thickness(Theme.S4),
            MinHeight = 140,
            ClipToBounds = true,
            Child = new ScrollViewer
            {
                Content = Ui.Col(0, _placeholder, _output),
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            }
        };
        well[Grid.RowProperty] = 1;
        _output.IsVisible = false;

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        body.Children.Add(Ui.With(Ui.Col(Theme.S3,
                Ui.Muted("These checks never change anything. The support package contains logs only — no passwords, no keys."),
                buttons),
            c => c.Margin = new Thickness(0, 0, 0, Theme.S4)));
        body.Children.Add(well);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(Pages.Header("Checks", "Test every part of TradeAgent, or package the logs for support."));
        root.Children.Add(Ui.With(body, c => c[Grid.RowProperty] = 1));
        Root = root;
    }

    /// <summary>Nothing here polls; this page only redraws when the user presses something.</summary>
    public void Update() { }

    async Task RunDoctorAsync()
    {
        Say("Checking…", Theme.TextMuted);
        var report = await _host.RunDoctorAsync();
        if (report.AllHealthy) { Say("Everything looks healthy.", Theme.Positive); return; }

        Say(string.Join('\n', report.Problems.Select(p =>
            $"• {p.Name}{(string.IsNullOrWhiteSpace(p.Detail) ? "" : $": {p.Detail}")}" +
            (string.IsNullOrWhiteSpace(p.UserAction) ? "" : $"\n    what to do: {p.UserAction}"))), Theme.Text);
    }

    void CreatePackage()
    {
        _packagePath = Diagnostics.Doctor.CreateSupportPackage(_host.Db);
        Say($"Saved to {_packagePath}", Theme.Text);
        _showPackage.IsVisible = true;
    }

    void Say(string text, IBrush brush)
    {
        _placeholder.IsVisible = false;
        _output.IsVisible = true;
        _output.Text = text;
        _output.Foreground = brush;
    }
}

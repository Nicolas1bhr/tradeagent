using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TradeAgent.AgentRuntime;

namespace TradeAgent.App;

/// <summary>
/// The conversation with the AI, inside the application window.
///
/// This replaces a console. Until now "Start AI" launched the agent CLI in its own black terminal,
/// and that terminal WAS the product's chat interface: the user typed into a command prompt, watched
/// raw tool output scroll past, and had no way to tell an order being placed from a log line. Every
/// decision below follows from removing that.
///
///   - The AI's answers are plain full-width text. A speech bubble around three paragraphs of
///     reasoning is decoration pretending to be structure.
///   - Tool calls get their own quiet monospace chip. This is the trust surface of the whole
///     product — it is where the user watches the AI read a price or place an order — so it has to
///     be legible and boring, never a badge.
///   - Auto-scroll only happens when the user was already at the bottom. Yanking the transcript out
///     from under someone reading back through it is how a chat log becomes unusable.
/// </summary>
sealed class ChatView
{
    readonly AppHost _host;
    readonly Func<Task> _startAgent;

    readonly ScrollViewer _scroll;
    readonly StackPanel _turns = new() { Spacing = Theme.S5 };
    readonly TextBox _input;
    readonly Button _send;
    readonly Border _composer;

    readonly Control _emptyState;
    readonly Control _noAgentState;
    readonly Control _busyIndicator;

    IAgentConversation? _bound;
    Streaming? _streaming;

    /// <summary>
    /// Whether anything has actually been said yet. Not "are there turns": starting the agent
    /// appends a System turn announcing it is ready, and the suggestions are most useful precisely
    /// then, so they stay up until the first real exchange.
    /// </summary>
    bool _hasExchange;

    /// <summary>The AI turn currently arriving one delta at a time.</summary>
    sealed class Streaming
    {
        public required TextBlock Text;
        public required TextBlock Time;
    }

    public Control Root { get; }

    public ChatView(AppHost host, Func<Task> startAgent)
    {
        _host = host;
        _startAgent = startAgent;

        _emptyState = BuildEmptyState();
        _noAgentState = BuildNoAgentState();
        _busyIndicator = Ui.With(Ui.Busy("Thinking…"), c => c.IsVisible = false);

        var stack = Ui.Col(Theme.S5, _noAgentState, _emptyState, _turns, _busyIndicator);
        _scroll = new ScrollViewer
        {
            Content = stack,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, Theme.S3, 0)
        };

        _input = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 140,
            MinHeight = 34,
            // The composer card below is already the sunken well, so the box itself carries no
            // chrome. Setting Background here does nothing — Fluent paints the template's own
            // Border from a nested style — so this opts into the theme's "bare" variant, which
            // writes transparency onto that Border in every state.
            Classes = { "bare" },
            Padding = new Thickness(Theme.S1, Theme.S2),
            PlaceholderText = "Ask about your account, or tell the AI what you want done",
            VerticalAlignment = VerticalAlignment.Bottom
        };
        _input.KeyDown += OnComposerKey;

        // Deliberately the synchronous overload. The async one disables the button until the task it
        // started completes, and the task here is the AI's whole reply — which would leave the Stop
        // button greyed out for exactly as long as there is something to stop.
        _send = Ui.Primary("Send", () => { _ = PressAsync(); });
        _send.VerticalAlignment = VerticalAlignment.Bottom;
        _send.MinWidth = 96;
        _send[Grid.ColumnProperty] = 1;

        var composerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        composerGrid.Children.Add(_input);
        composerGrid.Children.Add(Ui.With(_send, b => b.Margin = new Thickness(Theme.S3, 0, 0, 0)));

        _composer = new Border
        {
            Background = Theme.BgSunken,
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.RadiusLg,
            Padding = new Thickness(Theme.S3),
            Child = composerGrid
        };

        var hint = Ui.With(Ui.Micro("Enter sends · Shift+Enter starts a new line"),
            t => t.Margin = new Thickness(Theme.S2, Theme.S2, 0, 0));

        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        root.Children.Add(_scroll);
        root.Children.Add(Ui.With(Ui.Col(0, _composer, hint), c =>
        {
            c[Grid.RowProperty] = 1;
            c.Margin = new Thickness(0, Theme.S5, 0, 0);
        }));
        Root = root;
    }

    public void FocusComposer()
    {
        if (_bound is not null) Dispatcher.UIThread.Post(() => _input.Focus());
    }

    // ---- binding ---------------------------------------------------------------------------

    /// <summary>
    /// Called on every host refresh. The conversation object appears only once the agent has been
    /// prepared, so this is also where the page stops being an empty state and becomes a chat.
    /// </summary>
    public void Update()
    {
        var current = _host.Conversation;
        if (!ReferenceEquals(current, _bound)) Rebind(current);
        RefreshState();
    }

    void Rebind(IAgentConversation? next)
    {
        if (_bound is not null)
        {
            _bound.TurnAdded -= OnTurnAdded;
            _bound.Delta -= OnDelta;
            _bound.StateChanged -= OnStateChanged;
        }

        _bound = next;
        _streaming = null;
        _hasExchange = false;
        _turns.Children.Clear();

        if (_bound is not null)
        {
            _bound.TurnAdded += OnTurnAdded;
            _bound.Delta += OnDelta;
            _bound.StateChanged += OnStateChanged;
            foreach (var t in _bound.History)
            {
                _turns.Children.Add(Render(t));
                if (t.Role is ChatRole.You or ChatRole.Ai) _hasExchange = true;
            }
        }

        RefreshState();
        if (_bound is not null) Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    void RefreshState()
    {
        var live = _bound is not null;
        _noAgentState.IsVisible = !live;
        _emptyState.IsVisible = live && !_hasExchange;
        _composer.IsEnabled = live;
        _input.IsEnabled = live;

        var busy = live && _bound!.Busy;
        _busyIndicator.IsVisible = busy;
        _send.Content = busy ? "Stop" : "Send";
        MainWindow.SetVariant(_send, busy ? "danger" : "primary");
    }

    // ---- conversation events (raised off a background process reader) ------------------------

    void OnTurnAdded(ChatTurn turn) => Dispatcher.UIThread.Post(() =>
    {
        var wasAtBottom = AtBottom();

        if (turn.Role == ChatRole.Ai && _streaming is not null)
        {
            // Finalise the streamed row rather than appending a duplicate of the same answer.
            _streaming.Text.Text = turn.Text;
            _streaming.Time.Text = turn.At.ToLocalTime().ToString("HH:mm");
            _streaming = null;
        }
        else
        {
            _turns.Children.Add(Render(turn));
        }

        if (turn.Role is ChatRole.You or ChatRole.Ai) { _hasExchange = true; _emptyState.IsVisible = false; }
        if (wasAtBottom) Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
    });

    void OnDelta(string text) => Dispatcher.UIThread.Post(() =>
    {
        var wasAtBottom = AtBottom();

        if (_streaming is null)
        {
            var (row, parts) = BuildAiRow("", DateTimeOffset.Now);
            _streaming = parts;
            _turns.Children.Add(row);
            _hasExchange = true;
            _emptyState.IsVisible = false;
        }
        // Append rather than rebuild: the row is already laid out, and re-creating it per token
        // would destroy any selection the user has made in it.
        _streaming.Text.Text += text;

        if (wasAtBottom) Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
    });

    void OnStateChanged() => Dispatcher.UIThread.Post(RefreshState);

    bool AtBottom() =>
        _scroll.Extent.Height <= _scroll.Viewport.Height ||
        _scroll.Offset.Y >= _scroll.Extent.Height - _scroll.Viewport.Height - 32;

    // ---- sending -----------------------------------------------------------------------------

    void OnComposerKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;   // Shift+Enter is a newline

        // Enter never means "stop". Interrupting the AI has to be a deliberate press of a button
        // that says Stop, not a stray keystroke from someone typing their next question early.
        e.Handled = true;
        if (_bound is null || _bound.Busy) return;
        _ = SubmitAsync();
    }

    /// <summary>The composer's one button: Send while idle, Stop while the AI is working.</summary>
    async Task PressAsync()
    {
        var conversation = _bound;
        if (conversation is null) return;

        if (!conversation.Busy) { await SubmitAsync(); return; }
        try { await conversation.CancelAsync(); }
        catch (Exception ex) { Ui.ReportError?.Invoke(ex.Message); }
    }

    async Task SubmitAsync()
    {
        var conversation = _bound;
        if (conversation is null) return;

        var text = _input.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        _input.Text = "";
        _hasExchange = true;
        _emptyState.IsVisible = false;
        try { await conversation.SendAsync(text); }
        catch (Exception ex) { Ui.ReportError?.Invoke(ex.Message); }
    }

    // ---- turn rendering ------------------------------------------------------------------------

    Control Render(ChatTurn turn) => turn.Role switch
    {
        ChatRole.You => YouRow(turn),
        ChatRole.Tool => ToolRow(turn),
        ChatRole.System => SystemRow(turn),
        _ => BuildAiRow(turn.Text, turn.At).Row
    };

    static Control YouRow(ChatTurn turn)
    {
        var bubble = new Border
        {
            Background = Theme.BgElevated,
            CornerRadius = Theme.Radius,
            Padding = new Thickness(Theme.S4, Theme.S3),
            Child = new TextBlock
            {
                Text = turn.Text, TextWrapping = TextWrapping.Wrap,
                Foreground = Theme.Text, FontSize = Theme.Base, LineHeight = 21
            }
        };

        var time = Ui.Micro(turn.At.ToLocalTime().ToString("HH:mm"));
        time.HorizontalAlignment = HorizontalAlignment.Right;
        time.Margin = new Thickness(0, Theme.S1, Theme.S1, 0);

        var col = Ui.Col(0, bubble, time);
        col.MaxWidth = 560;
        col.HorizontalAlignment = HorizontalAlignment.Right;
        return col;
    }

    /// <summary>
    /// The AI's answer, and the block the streaming path writes into. Returned as a pair so the
    /// caller can hold on to the two TextBlocks that change while a turn is still arriving.
    /// </summary>
    static (Control Row, Streaming Parts) BuildAiRow(string text, DateTimeOffset at)
    {
        var body = new SelectableTextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Theme.Text,
            FontSize = Theme.Base, LineHeight = 21, SelectionBrush = Theme.AccentSoft
        };

        var time = Ui.Micro(at.ToLocalTime().ToString("HH:mm"));
        time.HorizontalAlignment = HorizontalAlignment.Right;
        time[Grid.ColumnProperty] = 2;

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
            Margin = new Thickness(0, 0, 0, Theme.S2),
            Children =
            {
                new Ellipse
                {
                    Width = 7, Height = 7, Fill = Theme.Accent,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, Theme.S2, 0)
                },
                Ui.With(Ui.Eyebrow("TradeAgent AI"), t =>
                {
                    t.VerticalAlignment = VerticalAlignment.Center;
                    t[Grid.ColumnProperty] = 1;
                }),
                time
            }
        };

        return (Ui.Col(0, header, body), new Streaming { Text = body, Time = time });
    }

    static Control ToolRow(ChatTurn turn)
    {
        var chip = new Border
        {
            Background = Theme.BgSunken,
            BorderBrush = Theme.Accent,
            // A left bar only. Rounding the left corners too would bend the bar into a smear, so the
            // left edge stays square and the right follows the rest of the app.
            BorderThickness = new Thickness(2, 0, 0, 0),
            CornerRadius = new CornerRadius(0, 6, 6, 0),
            Padding = new Thickness(Theme.S3, Theme.S2),
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 760,
            Child = new TextBlock
            {
                Text = turn.Text, TextWrapping = TextWrapping.Wrap, FontFamily = Theme.Mono,
                FontSize = Theme.Micro, Foreground = Theme.TextMuted, LineHeight = 17
            }
        };

        var time = Ui.Micro(turn.At.ToLocalTime().ToString("HH:mm"));
        time.VerticalAlignment = VerticalAlignment.Center;
        time[Grid.ColumnProperty] = 1;
        time.Margin = new Thickness(Theme.S3, 0, 0, 0);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(chip);
        grid.Children.Add(time);
        return grid;
    }

    static Control SystemRow(ChatTurn turn)
    {
        var t = Ui.Micro($"{turn.Text}  ·  {turn.At.ToLocalTime():HH:mm}");
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.TextAlignment = TextAlignment.Center;
        t.Opacity = 0.8;
        return t;
    }

    // ---- empty states ---------------------------------------------------------------------------

    Control BuildEmptyState()
    {
        var suggestions = new WrapPanel();
        foreach (var s in new[]
                 {
                     "What is my account balance?",
                     "What is ES trading at?",
                     "Explain what you would do today, but do not trade.",
                     "Show me my open positions."
                 })
        {
            // Ghost, and left ghost: these are prompts, not commands, and four outlined buttons
            // stacked above the composer would out-shout the primary action sitting right below them.
            var text = s;
            var b = Ui.Ghost(text, () => { _input.Text = text; _input.Focus(); _input.CaretIndex = text.Length; });
            b.Margin = new Thickness(0, 0, Theme.S2, Theme.S2);
            suggestions.Children.Add(b);
        }

        return Ui.Col(Theme.S4,
            Ui.H2("Talk to the AI here"),
            Ui.Muted("It can read your account, your positions and live prices, and it can place orders — " +
                     "never beyond the limits set on the Safety page. Everything it does appears in this " +
                     "transcript as it happens."),
            Ui.With(Ui.Eyebrow("Try asking"), t => t.Margin = new Thickness(0, Theme.S2, 0, 0)),
            suggestions);
    }

    Control BuildNoAgentState()
    {
        var start = Ui.Primary("Start the AI", async () => await _startAgent());
        start.HorizontalAlignment = HorizontalAlignment.Left;

        return Ui.Col(Theme.S4,
            Ui.H2("The AI is not running yet"),
            Ui.Muted("Start it and this page becomes the conversation. TradeAgent runs the AI inside " +
                     "this window — there is no command prompt to open and nothing to install first."),
            start);
    }
}

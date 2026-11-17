using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TradeAgent.Core;
using Page = TradeAgent.App.MainWindow.Page;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;

namespace TradeAgent.App;

/// <summary>
/// THE SHEET (<c>U-ui-world</c>): the pane of glass a page is read on. It arrives from the side over the floor — the floor is drawn out
/// of focus behind it by the stage itself (<see cref="FloorFrame.Pane"/>), still there and still moving — and the camera goes, as it
/// arrives, to the thing the page is about.
///
/// <para><b>It holds every page, built once, and shows one</b> — the rule the old page host kept, for the reason it kept it: a page
/// rebuilt is a page whose half-pressed confirmation, scroll position and diagnostics are gone. Arriving and leaving change only how the
/// sheet is painted; the page inside is the page, pressable, from its first frame.</para>
/// </summary>
sealed class Sheet
{
    public Border Root { get; }

    /// <summary>Every page of the window, one visible.</summary>
    public Grid Host { get; } = new();

    /// <summary>How far the sheet has arrived, 0 to 1: what the stage draws its glass by.</summary>
    public double Arrived { get; private set; }

    public bool Open { get; private set; }

    /// <summary>The sheet moved this frame: the stage behind it has a frame to draw.</summary>
    public event Action? Moved;

    readonly TranslateTransform _shift = new();
    Motion.Run? _run;

    public Sheet(Action close)
    {
        var shut = Shell.IconPress(Icons.Close, Labels.SheetClose, close);
        shut.HorizontalAlignment = HorizontalAlignment.Right;
        shut.VerticalAlignment = VerticalAlignment.Top;
        shut.Margin = new Thickness(0, Theme.S4, Theme.S4, 0);

        Root = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, Theme.S1, Theme.SheetGap, Theme.S2),
            CornerRadius = Theme.RadiusSheet, ClipToBounds = true, Background = Theme.SheetFill,
            IsVisible = false, Opacity = 0, RenderTransform = _shift,
            Child = new Grid
            {
                Children =
                {
                    new Border { Padding = new Thickness(Theme.S8, Theme.S8, Theme.S5, Theme.S6), Child = Host },
                    shut
                }
            }
        };
    }

    /// <summary>How wide the sheet is in a window this wide: its share, never less than a page of words needs nor more than one reads well at.</summary>
    public static double WidthIn(double window) =>
        Math.Clamp(Math.Round(window * Theme.SheetShare), Theme.SheetMinWidth, Math.Min(Theme.SheetMaxWidth, Math.Max(Theme.SheetMinWidth, window - 2 * Theme.SheetGap)));

    /// <summary>The sheet comes or goes. Asked for what it already is, it does nothing.</summary>
    public void Show(bool open)
    {
        if (Open == open) return;
        Open = open;
        _run?.Stop();
        if (open) Root.IsVisible = true;
        var from = Arrived;
        var to = open ? 1.0 : 0.0;
        _run = Motion.Play(Root, open ? Motion.SheetIn : Motion.SheetOut, t =>
        {
            Arrived = from + (to - from) * t;
            Paint();
            Moved?.Invoke();
        }, () =>
        {
            if (!Open) Root.IsVisible = false;
            Moved?.Invoke();
        });
    }

    void Paint()
    {
        Root.Opacity = Arrived;
        _shift.X = (1 - Arrived) * Theme.SheetTravel;
    }

    /// <summary>Where the sheet is on <paramref name="stage"/> this instant, as the glass the stage draws behind it; null while it is away.</summary>
    public FloorFrame.Pane? Pane(Visual stage)
    {
        if (!Root.IsVisible || Arrived <= 0.01 || Root.Bounds.Width < 2) return null;
        if (Root.TranslatePoint(default, stage) is not { } at) return null;
        return new FloorFrame.Pane((float)at.X, (float)at.Y, (float)Root.Bounds.Width, (float)Root.Bounds.Height,
            (float)Theme.RadiusSheet.TopLeft, (float)Arrived);
    }
}

/// <summary>
/// THE DOCK (<c>U-ui-world</c>): the six places of the window in one row at its foot, and — beside the place the owner is in — that
/// place's leaves by the names the app's own sentences call them. It is the whole of the app's navigation: there is no list of pages
/// anywhere else. Built once from <see cref="Places.All"/>; choosing only changes which leaves show and which words are lit.
/// </summary>
sealed class Dock
{
    public Border Root { get; }

    readonly Dictionary<Place, Button> _places = new();
    readonly Dictionary<Place, StackPanel> _leafRows = new();
    readonly Dictionary<Page, Button> _leaves = new();
    readonly Dictionary<Place, Ellipse> _dots = new();
    readonly Dictionary<Place, TextBlock> _words = new();
    Place? _shown;
    bool _compact;

    public Dock(Action<Page> go, Action find)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var place in Places.All)
        {
            var dot = new Ellipse
            {
                Width = 6, Height = 6, Fill = Theme.Neutral, IsVisible = false, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(-2, 1, 0, 0)
            };
            _dots[place.Place] = dot;
            var press = new Button { Classes = { "dock" } };
            var icon = Icons.Line(place.Icon);
            icon[!Shape.StrokeProperty] = press[!TemplatedControl.ForegroundProperty];
            var word = new TextBlock { Text = place.Name, VerticalAlignment = VerticalAlignment.Center };
            word[!TextBlock.ForegroundProperty] = press[!TemplatedControl.ForegroundProperty];
            word[!TextBlock.FontWeightProperty] = press[!TemplatedControl.FontWeightProperty];
            press.Content = Ui.Row(Theme.S2, Icons.Boxed(icon, Theme.IconSize), word, dot);
            _words[place.Place] = word;
            ToolTip.SetTip(press, place.Says);
            var first = place.Leaves[0].Page;
            press.Click += (_, _) => go(first);
            _places[place.Place] = press;
            row.Children.Add(press);

            // A PLACE'S LEAVES stand beside it while the owner is in it, and only then: a place of one leaf has none to show.
            var leaves = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
            foreach (var leaf in place.Leaves)
            {
                var page = leaf.Page;
                var b = new Button { Classes = { "leaf" }, Content = leaf.Word };
                b.Click += (_, _) => go(page);
                _leaves[page] = b;
                leaves.Children.Add(b);
            }
            _leafRows[place.Place] = leaves;
            row.Children.Add(leaves);
        }

        row.Children.Add(new Border { Width = 1, Height = 18, Background = Theme.GlassLine, Margin = new Thickness(Theme.S2, 0), VerticalAlignment = VerticalAlignment.Center });
        var search = new Button { Classes = { "dock" } };
        var glass = Icons.Line(Icons.Find);
        glass[!Shape.StrokeProperty] = search[!TemplatedControl.ForegroundProperty];
        var findWord = new TextBlock { Text = Labels.FindPress, VerticalAlignment = VerticalAlignment.Center };
        findWord[!TextBlock.ForegroundProperty] = search[!TemplatedControl.ForegroundProperty];
        search.Content = Ui.Row(Theme.S2, Icons.Boxed(glass, Theme.IconSize), findWord,
            new TextBlock { Text = Shell.FindKeys, FontSize = Theme.Micro, Foreground = Theme.TextFaint, VerticalAlignment = VerticalAlignment.Center });
        ToolTip.SetTip(search, Labels.FindPrompt);
        search.Click += (_, _) => find();
        row.Children.Add(search);

        Root = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(Theme.S3, 0, Theme.S3, Theme.S3), Padding = new Thickness(5),
            CornerRadius = Theme.Pill, Background = Theme.GlassFill, Child = row
        };
    }

    /// <summary>The page being shown: its place is lit, its leaves stand beside it, and its own leaf is the lit one.</summary>
    public void Select(Page page)
    {
        var place = Places.Of(page);
        foreach (var (key, press) in _places)
        {
            if (key == place.Place) { if (!press.Classes.Contains("on")) press.Classes.Add("on"); }
            else press.Classes.Remove("on");
        }
        foreach (var (key, leaves) in _leafRows)
        {
            var show = key == place.Place && place.Leaves.Count > 1;
            if (show && !leaves.IsVisible)
            {
                leaves.IsVisible = true;
                if (_shown is not null) Motion.RiseEach(leaves.Children.OfType<Control>(), 6);
            }
            else if (!show) leaves.IsVisible = false;
        }
        foreach (var (key, leaf) in _leaves)
        {
            if (key == page) { if (!leaf.Classes.Contains("on")) leaf.Classes.Add("on"); }
            else leaf.Classes.Remove("on");
        }
        _shown = place.Place;
        Words();
    }

    /// <summary>
    /// IN A NARROW WINDOW the dock keeps every place and gives up words: only the place the owner is in is named, the others are
    /// their drawings, each still named by its tooltip. Nothing is ever dropped from the dock to make it fit.
    /// </summary>
    public void Compact(bool compact)
    {
        if (_compact == compact) return;
        _compact = compact;
        Words();
    }

    void Words()
    {
        foreach (var (place, word) in _words) word.IsVisible = !_compact || place == _shown;
    }

    /// <summary>A small light on a place: something there asks to be looked at (tone), or nothing does (null).</summary>
    public void Light(Place place, IBrush? tone, string? says = null)
    {
        var dot = _dots[place];
        dot.IsVisible = tone is not null;
        if (tone is not null) dot.Fill = tone;
        ToolTip.SetTip(_places[place], says is null ? Places.All.First(p => p.Place == place).Says : says);
    }

    public FloorFrame.Pane? Pane(Visual stage) => Shell.PaneOf(Root, stage, (float)(Root.Bounds.Height / 2));
}

/// <summary>
/// FIND (<c>U-ui-world</c>): one box that knows every page of the app and every press on every page by the words it wears, and takes
/// the owner to it — the place opens, the fold it is in opens, and the press itself is marked for a moment. The app's own sentences
/// name its presses ("press Check everything", "Let the AI work on its own"); this is where such a sentence can be acted on without
/// knowing where anything is.
///
/// <para><b>It reads the trees the window built and presses nothing.</b> Its list is made from the pages' own controls each time it
/// opens, so a press that exists is a press it finds, and one that does not exist is one it cannot invent. Choosing a result only
/// shows where the press is: the press is the owner's to make.</para>
/// </summary>
sealed class Finder
{
    public Border Root { get; }

    readonly TextBox _box = new() { Classes = { "bare" }, PlaceholderText = Labels.FindPrompt, FontSize = Theme.H2 };
    readonly StackPanel _results = new() { Spacing = 1 };
    readonly TextBlock _nothing = Ui.Muted(Labels.FindNothing);
    readonly Border _panel;
    readonly Func<IEnumerable<(Page Page, Control Root)>> _pages;
    readonly Action<Page> _go;
    List<Entry> _entries = [];
    int _at;

    /// <summary>One thing the search can find: the words it is found by, what is said under them, its page, and — for a press — the press itself.</summary>
    internal sealed record Entry(string Words, string Under, Page Page, Control? Control);

    const int Shown = 9;

    public bool Open => Root.IsVisible;

    public Finder(Func<IEnumerable<(Page Page, Control Root)>> pages, Action<Page> go)
    {
        _pages = pages;
        _go = go;
        _nothing.IsVisible = false;
        _nothing.Margin = new Thickness(Theme.S4, Theme.S2);
        var glass = Icons.Of(Icons.Find, Theme.TextMuted, 18);
        glass.VerticalAlignment = VerticalAlignment.Center;
        glass.Margin = new Thickness(Theme.S4, 0, 0, 0);
        _box[Grid.ColumnProperty] = 1;
        _box.VerticalAlignment = VerticalAlignment.Center;
        _panel = new Border
        {
            Width = 620, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 120, 0, 0), CornerRadius = Theme.RadiusLg, Background = Theme.GlassFill, ClipToBounds = true,
            Child = Ui.Col(0,
                new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), MinHeight = 56, Children = { glass, _box } },
                new Border { Height = 1, Background = Theme.GlassLine },
                new Border { Padding = new Thickness(Theme.S2), Child = Ui.Col(0, _results, _nothing) })
        };
        Root = new Border { Background = Theme.Dim, IsVisible = false, Child = _panel };
        Root.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.Source, Root)) Close();
        };
        _box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) Fill();
        };
        _box.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Down: Move(1); e.Handled = true; break;
                case Key.Up: Move(-1); e.Handled = true; break;
                case Key.Enter: Choose(_at); e.Handled = true; break;
                case Key.Escape: Close(); e.Handled = true; break;
            }
        };
    }

    public void Show()
    {
        _entries = Read(_pages());
        _box.Text = "";
        Root.IsVisible = true;
        Fill();
        Motion.Rise(_panel, -10, lasts: TimeSpan.FromMilliseconds(260));
        Dispatcher.UIThread.Post(() => _box.Focus(), DispatcherPriority.Input);
    }

    public void Close() => Root.IsVisible = false;

    public FloorFrame.Pane? Pane(Visual stage) => Root.IsVisible ? Shell.PaneOf(_panel, stage, (float)Theme.RadiusLg.TopLeft) : null;

    /// <summary>Every page by its name, then every press on every page by the words it wears at rest, each once per page.</summary>
    internal static List<Entry> Read(IEnumerable<(Page Page, Control Root)> pages)
    {
        var entries = new List<Entry>();
        foreach (var (page, word) in Places.Pages)
            entries.Add(new Entry(word, Labels.FindInPlace(Places.Of(page).Name), page, null));
        foreach (var (page, root) in pages)
        {
            var word = Places.Word(page);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var press in root.GetLogicalDescendants().OfType<Button>())
            {
                var says = Shell.Words(press);
                if (says is null || !seen.Add(says)) continue;
                entries.Add(new Entry(says, Labels.FindOnPage(word), page, press));
            }
        }
        return entries;
    }

    /// <summary>What is typed into the box, for a host that types for the owner (the render rows).</summary>
    internal string Asked
    {
        get => _box.Text ?? "";
        set => _box.Text = value;
    }

    /// <summary>The results shown now, in order, by the words each wears.</summary>
    internal IReadOnlyList<string> Found => [.. _results.Children.OfType<Button>().Select(b => b.Tag is Entry e ? e.Words : "")];

    /// <summary>Chooses the result the keys are on, as Enter does.</summary>
    internal void ChooseFirst() => Choose(_at);

    void Fill()
    {
        var asked = (_box.Text ?? "").Trim();
        var found = asked.Length == 0
            ? _entries.Where(e => e.Control is null).ToList()
            : [.. _entries.Select(e => (Entry: e, Rank: Rank(e.Words, asked))).Where(r => r.Rank < int.MaxValue)
                .OrderBy(r => r.Rank).ThenBy(r => r.Entry.Control is null ? 0 : 1).ThenBy(r => r.Entry.Words.Length).Select(r => r.Entry)];
        _results.Children.Clear();
        _at = 0;
        foreach (var entry in found.Take(asked.Length == 0 ? 15 : Shown))
        {
            var nth = _results.Children.Count;
            var words = new TextBlock { Text = entry.Words, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeight.Medium };
            var under = new TextBlock { Text = entry.Under, FontSize = Theme.Small, Foreground = Theme.TextFaint, VerticalAlignment = VerticalAlignment.Center, [Grid.ColumnProperty] = 1 };
            var b = new Button
            {
                Classes = { "found-row" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { words, under } }, Tag = entry
            };
            b.Click += (_, _) => Choose(nth);
            _results.Children.Add(b);
        }
        _nothing.IsVisible = _results.Children.Count == 0;
        Mark();
    }

    /// <summary>How well words answer what was asked: at their start, at a word's start, anywhere, or not at all.</summary>
    static int Rank(string words, string asked)
    {
        var at = words.IndexOf(asked, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return int.MaxValue;
        return at == 0 ? 0 : char.IsLetterOrDigit(words[at - 1]) ? 2 : 1;
    }

    void Move(int by)
    {
        var n = _results.Children.Count;
        if (n == 0) return;
        _at = ((_at + by) % n + n) % n;
        Mark();
    }

    void Mark()
    {
        for (var i = 0; i < _results.Children.Count; i++)
        {
            var b = (Button)_results.Children[i];
            if (i == _at) { if (!b.Classes.Contains("on")) b.Classes.Add("on"); }
            else b.Classes.Remove("on");
        }
    }

    void Choose(int nth)
    {
        if (nth < 0 || nth >= _results.Children.Count || ((Button)_results.Children[nth]).Tag is not Entry entry) return;
        Close();
        _go(entry.Page);
        if (entry.Control is { } control) Dispatcher.UIThread.Post(() => Shell.PointAt(control), DispatcherPriority.Background);
    }
}

/// <summary>The small things the shell's parts share.</summary>
static class Shell
{
    /// <summary>The keys that open <see cref="Finder"/>, as the dock says them.</summary>
    public static string FindKeys => OperatingSystem.IsMacOS() ? "⌘K" : "Ctrl K";

    /// <summary>A press that is only a drawing: its words are its tooltip.</summary>
    public static Button IconPress(string icon, string says, Action press)
    {
        var b = new Button { Classes = { "icon" } };
        var line = Icons.Line(icon);
        line[!Shape.StrokeProperty] = b[!TemplatedControl.ForegroundProperty];
        b.Content = Icons.Boxed(line, Theme.IconSize);
        ToolTip.SetTip(b, says);
        b.Click += (_, _) => press();
        return b;
    }

    /// <summary>Where a control is on the stage this instant, as a pane of glass for the stage to draw behind it; null while it is not shown.</summary>
    public static FloorFrame.Pane? PaneOf(Control c, Visual stage, float radius, float strength = 1)
    {
        if (!c.IsEffectivelyVisible || c.Bounds.Width < 2 || c.TranslatePoint(default, stage) is not { } at) return null;
        return new FloorFrame.Pane((float)at.X, (float)at.Y, (float)c.Bounds.Width, (float)c.Bounds.Height, radius, strength);
    }

    /// <summary>
    /// The words a press wears: its own text, or the first words inside it. A fold's header is named by its title. Null for a press
    /// that wears none (a drawing).
    /// </summary>
    public static string? Words(Button press)
    {
        var says = press.Content switch
        {
            string s => s,
            Control c => c.GetLogicalDescendants().Prepend(c).OfType<TextBlock>().Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)),
            _ => null
        };
        says = says?.Trim();
        return string.IsNullOrEmpty(says) ? null : says;
    }

    /// <summary>
    /// A control is pointed out: every fold it is inside is opened, it is scrolled into view, and it wears a mark for a moment. Nothing
    /// is pressed.
    /// </summary>
    public static void PointAt(Control control)
    {
        try
        {
            // A fold is a column of its header press and its body (Ui.Fold): the body's hidden ancestor is opened by that press.
            foreach (var ancestor in control.GetLogicalAncestors().OfType<Control>().Reverse())
            {
                if (ancestor.IsVisible || ancestor.GetLogicalParent() is not Panel column) continue;
                foreach (var header in column.Children.OfType<Button>())
                    if (Ui.IsFoldOf(header, ancestor)) Ui.SetFold(header, true);
            }
            if (!control.Classes.Contains("found")) control.Classes.Add("found");
            // SCROLLED TO ONCE THE PAGE HAS BEEN LAID OUT WITH ITS SECTION OPEN: asked for now, the page would be scrolled to where
            // the press stood while its section was still closed — nowhere.
            Dispatcher.UIThread.Post(() =>
            {
                try { control.BringIntoView(); }
                catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException)) { }
            }, DispatcherPriority.Loaded);
            DispatcherTimer.RunOnce(() => control.Classes.Remove("found"), TimeSpan.FromSeconds(2.2), DispatcherPriority.Background);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException)) { }
    }
}

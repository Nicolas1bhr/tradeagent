using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TradeAgent.Core;

namespace TradeAgent.App;

/// <summary>
/// WHAT THE OWNER'S PAGES ARE MADE OF (<c>U-ui-world</c>, builder two), beside <see cref="Ui.Fold"/>, <see cref="Ui.Section"/> and the
/// world's figure, measure and notice: a line of a page that opens with its name and what it holds at a glance; a part of a line, named
/// as a name rather than an eyebrow; a fact — its name over its value; a field — its name, what it means, the box with what it reads as,
/// and its press; and an item that reached him, held by a rule of light. Nothing here invents a colour, a size or a gap: they are
/// <see cref="Theme"/>'s.
/// </summary>
static partial class Ui
{
    /// <summary>
    /// A LINE OF THE PAGE THAT OPENS — <see cref="Fold"/>'s state, its press across the whole header and its rule that closing disarms
    /// every half-pressed button inside, with a header made to be read as the page's at-a-glance: the line's name set as a name, the
    /// summary the page keeps current under it at full strength, and, where the line holds a number, its measure drawn under that.
    /// </summary>
    public static Control Line(string title, Control body, bool open, TextBlock? summary = null, Control? measure = null)
    {
        var mark = With(Micro(open ? Labels.FoldHide : Labels.FoldShow), t =>
        {
            t.VerticalAlignment = VerticalAlignment.Top;
            t.Margin = new Thickness(Theme.S4, 4, 0, 0);
        });
        mark[Grid.ColumnProperty] = 1;
        var name = new TextBlock
        {
            Text = title, FontSize = Theme.LineTitle, LineHeight = Theme.LineTitleLine, FontWeight = FontWeight.SemiBold,
            LetterSpacing = Theme.H2Tracking, Foreground = Theme.Text, TextWrapping = TextWrapping.Wrap
        };
        var words = Col(Theme.S1, name);
        if (summary is not null)
        {
            summary.FontWeight = FontWeight.Normal;
            summary.FontSize = Theme.Small;
            summary.TextWrapping = TextWrapping.Wrap;
            words.Children.Add(summary);
        }
        if (measure is not null)
        {
            measure.HorizontalAlignment = HorizontalAlignment.Left;
            measure.Width = Theme.LineMeasureWidth;
            measure.Margin = new Thickness(0, Theme.S2, 0, 0);
            words.Children.Add(measure);
        }
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { words, mark } };
        var state = new FoldState { Open = open, Body = body, Mark = mark };
        var press = new Button
        {
            Content = head, Classes = { "fold" }, Tag = state,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        press.Click += (_, _) => SetFold(press, !state.Open);
        body.IsVisible = open;
        body.Margin = new Thickness(0, Theme.S2, 0, Theme.S6);
        return Col(0, press, body);
    }

    /// <summary>A PART OF A LINE: its name, set as a name — the line above already drew the hairline — and what it holds under it.</summary>
    public static Control Part(string title, Control body) =>
        Col(Theme.S3, new TextBlock
        {
            Text = title, FontSize = Theme.H2, LineHeight = Theme.LineTitleLine, FontWeight = FontWeight.SemiBold, LetterSpacing = Theme.H2Tracking,
            Foreground = Theme.Text, TextWrapping = TextWrapping.Wrap
        }, body);

    /// <summary>A FACT: its name, small and quiet, over its value — which wraps, so a long reading is never cut by a column.</summary>
    public static Control Fact(string name, TextBlock value)
    {
        value.TextWrapping = TextWrapping.Wrap;
        value.FontSize = Theme.Base;
        if (value.Foreground is null || ReferenceEquals(value.Foreground, Theme.TextMuted)) value.Foreground = Theme.Text;
        return Col(2, new TextBlock
        {
            Text = name, FontSize = Theme.Small, Foreground = Theme.TextMuted, TextWrapping = TextWrapping.Wrap
        }, value);
    }

    /// <summary>
    /// A FIELD AS A FORM, NOT A TABLE: its name, what it means under it, then the box — with what it reads as, if it is an
    /// <see cref="AmountField"/> — and its press beside the box. The press wraps its own words when it is armed and long.
    /// </summary>
    public static Control Form(string label, Control editor, string? hint = null, Control? press = null) =>
        Form(label, editor, hint is null ? null : Micro(hint), press);

    /// <summary><see cref="Form(string, Control, string?, Control?)"/> with a hint the page keeps a handle on.</summary>
    public static Control Form(string label, Control editor, TextBlock? hint, Control? press)
    {
        var words = Col(2, new TextBlock
        {
            Text = label, FontSize = Theme.Small, FontWeight = FontWeight.SemiBold, Foreground = Theme.Text, TextWrapping = TextWrapping.Wrap
        });
        if (hint is not null) words.Children.Add(hint);
        // A BOX OF A FIXED WIDTH SITS AT THE LEFT, under its name; a box made to stretch (a key) takes the line.
        if (editor is not TextBox { Width: double.NaN }) editor.HorizontalAlignment = HorizontalAlignment.Left;
        if (press is null) return Col(Theme.S2, words, editor);

        editor.VerticalAlignment = VerticalAlignment.Top;
        press.VerticalAlignment = VerticalAlignment.Top;
        press.HorizontalAlignment = HorizontalAlignment.Left;
        press.Margin = new Thickness(Theme.S3, 0, 0, 0);
        press[Grid.ColumnProperty] = 1;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { editor, press } };
        return Col(Theme.S2, words, row);
    }

    /// <summary>
    /// A THING THAT REACHED THE OWNER: held by a rule of light at its left — the window's light while a press of his waits on it, the
    /// pane's hairline while it is a record only — and its words beside the rule. A rule, never a box round it.
    /// </summary>
    public static Border Item(Control inner, bool waits) => new()
    {
        BorderBrush = waits ? Theme.ItemRuleWaiting : Theme.LineStrong,
        BorderThickness = new Thickness(Theme.ItemRule, 0, 0, 0),
        Padding = new Thickness(Theme.ItemIndent, 2, 0, 2),
        Child = inner
    };

    /// <summary>A row of figures, each over what it counts — short things side by side, the one place a page lays two across.</summary>
    public static Control Figures(params Control[] figures)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = Theme.S10, LineSpacing = Theme.S4 };
        foreach (var f in figures) row.Children.Add(f);
        return row;
    }

    // ---- the few words the owner's pages add (U-ui-world): sentence case, and only where a drawing needs a name ----

    /// <summary>Under the "For you" page's figure: what it counts.</summary>
    internal const string WaitingOnYou = "waiting on a press of yours";

    /// <summary>Under the "For you" page's second figure.</summary>
    internal const string RecordsForYou = "records that reached you";

    /// <summary>The "For you" page's part with his presses, and its part with what is only a record.</summary>
    internal const string YoursToDecide = "Yours to decide", OnlyRecords = "For your reading";

    /// <summary>Under the private line's figures.</summary>
    internal const string FindingsCounted = "findings", SevereUnacknowledged = "severe, not acknowledged";
}

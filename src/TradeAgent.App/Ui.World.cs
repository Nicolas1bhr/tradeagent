using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

// Inside a control subclass the bare name `Theme` is StyledElement's property, never the design-token class.
using Tokens = TradeAgent.App.Theme;

namespace TradeAgent.App;

/// <summary>
/// WHAT A PAGE ON GLASS IS MADE OF (<c>U-ui-world</c>), beside the words: a figure set large, a measure drawn as a line, a notice
/// that asks to be looked at. Each shows something the app already knows as a number or a state — never a sentence taken apart: where
/// the app has only words for a thing, the words are what is shown.
/// </summary>
static partial class Ui
{
    /// <summary>The name at the head of a pane — a page's, a setup screen's: large and light.</summary>
    public static TextBlock Title(string text) =>
        With(Hero(text), t => { t.FontSize = Theme.Title; t.LineHeight = Theme.TitleLine; t.LetterSpacing = Theme.TitleTracking; });

    /// <summary>A figure set large and light over what it counts. The caller keeps the figure and rewrites its text in place.</summary>
    public static Control Figure(TextBlock figure, string counts)
    {
        figure.FontSize = Theme.Display;
        figure.LineHeight = Theme.DisplayLine;
        figure.FontWeight = FontWeight.Light;
        figure.FontFamily = Theme.SansDisplay;
        figure.FontFeatures = Theme.Tabular;
        figure.LetterSpacing = Theme.DisplayTracking;
        figure.Foreground = Theme.Text;
        return Col(2, figure, Micro(counts));
    }

    /// <summary>A text block for <see cref="Figure"/>, saying "–" until it is told a figure: unknown is a dash, never a zero.</summary>
    public static TextBlock FigureText() => new() { Text = "–" };

    /// <summary>
    /// A coloured notice on the pane: something that outranks the page round it — an order that could not be confirmed, a refusal.
    /// The one place a page keeps an edge all round, because it must be found at a glance; its fill is the tone, soft.
    /// </summary>
    public static Border Notice(IBrush tone, Control inner) => new()
    {
        Background = Soft(tone),
        BorderBrush = tone,
        BorderThickness = new Thickness(1),
        CornerRadius = Theme.Radius,
        Padding = new Thickness(Theme.S5),
        Child = inner
    };
}

/// <summary>
/// A MEASURE DRAWN AS A LINE (<c>U-ui-world</c>): how much of something is used — today's spend of its limit, a task's spend of what
/// it was given. A hairline of the pane's light with the used part lit in the tone it is told. <b>What is not known is not drawn as
/// nothing used</b>: told no fraction, the line is drawn empty and faint, and the words beside it say that it is unknown.
/// </summary>
sealed class Meter : Control
{
    double? _used;
    IBrush _tone = Tokens.Text;

    public Meter()
    {
        Height = 3;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        MinWidth = 40;
    }

    /// <summary>How much is used, from 0 to 1 (more is drawn as all of it), or null where it is not known; and the tone the used part is lit in.</summary>
    public void Set(double? used, IBrush? tone = null)
    {
        var clamped = used is { } u && !double.IsNaN(u) ? Math.Clamp(u, 0, 1) : (double?)null;
        var brush = tone ?? Tokens.Text;
        if (clamped == _used && ReferenceEquals(brush, _tone)) return;
        _used = clamped;
        _tone = brush;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        var round = size.Height / 2;
        context.DrawRectangle(_used is null ? Tokens.Line : Tokens.BgHover, null, new Rect(size), round, round);
        if (_used is { } used && used > 0)
            context.DrawRectangle(_tone, null, new Rect(0, 0, Math.Max(size.Height, size.Width * used), size.Height), round, round);
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace TradeAgent.App;

/// <summary>
/// THE TRADING PAGES' OWN PIECES (<c>U-ui-world</c>): a readout — a fact set as what it is over its small name, rather than a key and a
/// value in a table — a state set large with its dot, and the line that names what a meter measures. Each holds a text block the page
/// keeps and rewrites in place on the tick.
/// </summary>
static partial class Ui
{
    /// <summary>A fact over its name: the name small and quiet above, the value as the caller set it.</summary>
    public static Control Readout(string name, Control value) =>
        Col(Theme.S1, With(Micro(name), t => t.Foreground = Theme.TextMuted), value);

    /// <summary>Readouts side by side while they fit, wrapping under each other when they do not: this far apart across, this far down.</summary>
    public static WrapPanel Spread(double across, double down, params Control[] kids)
    {
        var p = new WrapPanel();
        foreach (var k in kids)
        {
            k.Margin = new Thickness(0, 0, across, down);
            p.Children.Add(k);
        }
        return p;
    }

    /// <summary>A value set at the size of a figure, light: the one thing a readout is there to say.</summary>
    public static TextBlock ReadoutLarge(string text = "—") => new()
    {
        Text = text, FontSize = Theme.Display, LineHeight = Theme.DisplayLine, FontWeight = FontWeight.Light,
        FontFamily = Theme.SansDisplay, LetterSpacing = Theme.DisplayTracking, Foreground = Theme.Text,
        TextWrapping = TextWrapping.Wrap
    };

    /// <summary>A state set a size under a figure, in the tone the caller gives it.</summary>
    public static TextBlock StateLarge(string text = "—") => new()
    {
        Text = text, FontSize = Theme.ReadoutState, LineHeight = Theme.ReadoutStateLine, FontWeight = FontWeight.Light,
        FontFamily = Theme.SansDisplay, LetterSpacing = Theme.H1Tracking, Foreground = Theme.Text,
        TextWrapping = TextWrapping.Wrap
    };

    /// <summary>The dot beside a state set large; its fill is the state's tone.</summary>
    public static Ellipse StateDot() => new()
    {
        Width = Theme.StateDot, Height = Theme.StateDot, Fill = Theme.Neutral,
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>
    /// A measure with its name and its figure on one line above the line it is drawn as: "Cost today … 1.42 USD of 5 USD". The name
    /// and the figure are the caller's, rewritten in place.
    /// </summary>
    public static Control Measure(TextBlock name, TextBlock figure, Meter meter)
    {
        name.FontSize = Theme.Micro;
        name.Foreground = Theme.TextMuted;
        figure.FontSize = Theme.Micro;
        figure.FontFeatures = Theme.Tabular;
        figure.Foreground = Theme.Text;
        figure.HorizontalAlignment = HorizontalAlignment.Right;
        figure[Grid.ColumnProperty] = 1;
        figure.Margin = new Thickness(Theme.S3, 0, 0, 0);
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { name, figure } };
        return With(Col(Theme.S1 + 2, line, meter), c =>
        {
            c.Width = Theme.SpendMeterWidth;
            c.HorizontalAlignment = HorizontalAlignment.Left;
        });
    }
}

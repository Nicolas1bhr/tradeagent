using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using TradeAgent.Core;

namespace TradeAgent.App;

/// <summary>
/// Small code-built controls. No XAML and no MVVM framework on purpose: this UI is a dozen labels
/// and a dozen buttons, and the indirection would cost more than it saves.
/// </summary>
static class Ui
{
    public static TextBlock H1(string text) => new()
    {
        Text = text, FontSize = 24, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4)
    };

    public static TextBlock H2(string text) => new() { Text = text, FontSize = 17, FontWeight = FontWeight.SemiBold };

    public static TextBlock Label(string text) => new()
    {
        Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.Bold, Opacity = 0.6
    };

    public static TextBlock Body(string text, IBrush? brush = null) => new()
    {
        Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = brush
    };

    public static Control Card(Control inner) => new Border
    {
        Padding = new Thickness(14),
        CornerRadius = new CornerRadius(8),
        BorderThickness = new Thickness(1),
        BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
        Child = inner
    };

    public static Control KeyValue(string key, string value) => new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("200,*"),
        Children =
        {
            new TextBlock { Text = key, Opacity = 0.7, FontSize = 13 },
            new TextBlock { Text = value, FontSize = 13, FontWeight = FontWeight.SemiBold, [Grid.ColumnProperty] = 1 }
        }
    };

    public static Button Button(string text, Action onClick, bool emphasised = false)
    {
        var b = new Button { Content = text, Padding = new Thickness(14, 8), FontSize = 13 };
        if (emphasised) { b.FontWeight = FontWeight.Bold; b.BorderThickness = new Thickness(2); }
        b.Click += (_, _) => onClick();
        return b;
    }

    public static Button Button(string text, Func<Task> onClick, bool emphasised = false)
    {
        var b = new Button { Content = text, Padding = new Thickness(14, 8), FontSize = 13 };
        if (emphasised) { b.FontWeight = FontWeight.Bold; b.BorderThickness = new Thickness(2); }
        b.Click += async (_, _) =>
        {
            b.IsEnabled = false;
            try { await onClick(); }
            finally { b.IsEnabled = true; }
        };
        return b;
    }

    public static Button Big(string text, IBrush background, Action onClick)
    {
        var b = new Button
        {
            Content = text, Background = background, Foreground = Brushes.White,
            FontSize = 16, FontWeight = FontWeight.Bold,
            Padding = new Thickness(18, 14), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>
    /// Two-step button. Anything that moves money or removes permission needs a deliberate second
    /// press, so a mis-click cannot liquidate a portfolio.
    /// </summary>
    public static Button Confirm(string label, string confirmLabel, Action onConfirmed)
    {
        var armed = false;
        var b = new Button { Content = label, Padding = new Thickness(14, 8), FontSize = 13 };
        b.Click += (_, _) =>
        {
            if (!armed)
            {
                armed = true;
                b.Content = confirmLabel;
                b.Foreground = Brushes.Firebrick;
                b.FontWeight = FontWeight.Bold;
                return;
            }
            armed = false;
            b.Content = label;
            b.ClearValue(Avalonia.Controls.Button.ForegroundProperty);
            b.FontWeight = FontWeight.Normal;
            onConfirmed();
        };
        return b;
    }

    public static Button Confirm(string label, string confirmLabel, Func<Task> onConfirmed) =>
        Confirm(label, confirmLabel, () => _ = onConfirmed());

    public static Control StatusRow(ComponentHealth h) => new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("18,200,*"),
        Children =
        {
            new Ellipse
            {
                Width = 10, Height = 10, Fill = Dot(h.State),
                VerticalAlignment = VerticalAlignment.Center
            },
            new TextBlock { Text = h.Component, FontSize = 13, [Grid.ColumnProperty] = 1 },
            new TextBlock
            {
                Text = Describe(h), FontSize = 13, Opacity = 0.75,
                TextTrimming = TextTrimming.CharacterEllipsis, [Grid.ColumnProperty] = 2
            }
        }
    };

    static string Describe(ComponentHealth h) => h.State switch
    {
        HealthState.READY => string.IsNullOrWhiteSpace(h.Detail) ? "ready" : h.Detail,
        HealthState.PAUSED => string.IsNullOrWhiteSpace(h.Detail) ? "paused" : $"paused — {h.Detail}",
        _ => string.IsNullOrWhiteSpace(h.Detail) ? h.State.ToString().ToLowerInvariant() : $"{h.State.ToString().ToLowerInvariant()} — {h.Detail}"
    };

    static IBrush Dot(HealthState s) => s switch
    {
        HealthState.READY => Brushes.SeaGreen,
        HealthState.STARTING => Brushes.SteelBlue,
        HealthState.DEGRADED => Brushes.Goldenrod,
        HealthState.PAUSED => Brushes.DarkOrange,
        HealthState.FAILED => Brushes.Firebrick,
        _ => Brushes.Gray
    };

    public static string ModeLabel(TradingMode m) => m switch
    {
        TradingMode.OBSERVE => "Watch only",
        TradingMode.PAPER => "Practice",
        TradingMode.LIVE_CONFIRM => "Real, ask me first",
        TradingMode.LIVE_AUTONOMOUS => "Real, fully automatic",
        _ => m.ToString()
    };
}

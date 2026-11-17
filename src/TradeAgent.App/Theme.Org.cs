using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;

namespace TradeAgent.App;

/// <summary>
/// THE ORGANISATION'S PAGES ON GLASS (<c>U-ui-world</c>, builder B3): the measures the Organisation, Work and Costs pages and the first-run
/// setup draw with. Every size here is a step of the theme's own rhythm or type scale, named for what it measures.
/// </summary>
static partial class Theme
{
    /// <summary>How far one level of the chart sits in from the level above it; its guide line runs at half of that.</summary>
    public const double TreeIndent = S6;

    /// <summary>The label column beside a unit's cost lines ("Real money", "Plan, list price", "Not established").</summary>
    public const double CostTagWidth = 118;

    /// <summary>The cost lines are a step thicker than a hairline: they are the page's picture.</summary>
    public const double CostBar = 4;

    /// <summary>The column a position's state dot sits in, and the dot's size.</summary>
    public const double StateDot = 8;

    /// <summary>A mandate's time line: its height, and the mark that says "now" on it.</summary>
    public const double TimeLineHeight = 14, TimeLineNow = 9;

    /// <summary>A check's pass or fail mark on a delivery.</summary>
    public const double CheckMark = 12;

    /// <summary>The figure of today's spend on the Costs page.</summary>
    public const double CostFigure = 34, CostFigureLine = 40;

    /// <summary>The first-run setup: the step's title, set large and light, and the progress line under the frame's head.</summary>
    public const double SetupTitle = 40, SetupTitleLine = 46, SetupTitleTracking = -1.3;
    public const double SetupProgress = 2;

    /// <summary>A sign-in address set large enough to read off the screen and type on a phone.</summary>
    public const double SignInAddress = 17;

    /// <summary>The sentence under a setup step's title: a size over the body, so the one question a screen asks reads first.</summary>
    public const double SetupLede = 15, SetupLedeLine = 24;

    /// <summary>The reading measure of the setup column, and the least height of its frame (so the footer sits low under a short body).</summary>
    public const double SetupWidth = 640, SetupFrameHeight = 460;

    /// <summary>
    /// THE SETUP'S CHOICES AS ROWS ON GLASS (<c>U-ui-world</c>): a press across the column's whole width with a hairline above it and no box —
    /// the pane's light lifts the row under the pointer. The styles are the setup frame's own (the frame is rebuilt on every render, so each
    /// build hands it a fresh set); they change how a choice looks, never what it does.
    /// </summary>
    public static Styles SetupStyles()
    {
        var styles = new Styles
        {
            new Style(x => x.OfType<Button>().Class("choice-row"))
            {
                Setters =
                {
                    S(TemplatedControl.BackgroundProperty, Brushes.Transparent),
                    S(TemplatedControl.BorderBrushProperty, Line),
                    S(TemplatedControl.BorderThicknessProperty, new Thickness(0, 1, 0, 0)),
                    S(TemplatedControl.CornerRadiusProperty, new CornerRadius(0)),
                    S(TemplatedControl.PaddingProperty, new Thickness(S3, S5)),
                    S(TemplatedControl.ForegroundProperty, Text),
                    S(TemplatedControl.FontWeightProperty, FontWeight.Normal)
                }
            }
        };
        Fill(styles, x => x.OfType<Button>().Class("choice-row").Class(":pointerover"), GlassHover);
        Fill(styles, x => x.OfType<Button>().Class("choice-row").Class(":pressed"), GlassActive);
        Fill(styles, x => x.OfType<Button>().Class("choice-row").Class(":disabled"), Brushes.Transparent);
        return styles;
    }
}

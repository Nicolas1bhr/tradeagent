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
}

using Avalonia.Collections;
using Avalonia.Media;

namespace TradeAgent.App;

/// <summary>
/// THE TRADING PAGES' OWN MEASURES (<c>U-ui-world</c>, the desk, the log, the checks, the report, the inbox and the conversation): the few
/// sizes and gaps those pages need beyond the shared ones. Colours stay the shared ones — a page of words on glass spends no colour of its own.
/// </summary>
static partial class Theme
{
    /// <summary>A state the desk sets large beside the trading mode — whether the AI may trade — a size under the mode's figure.</summary>
    public const double ReadoutState = 24, ReadoutStateLine = 30;

    /// <summary>The AI's own state, the line its section opens on: larger than a sentence, smaller than a state on the desk, since it can run long.</summary>
    public const double MissionState = 19, MissionStateLine = 25;

    /// <summary>The line box of the body's text (<see cref="Ui.Body"/> sets it), for a block that returns to body size.</summary>
    public const double BodyLine = 21;

    /// <summary>How far apart two figures in a row stand, and two readouts on one line.</summary>
    public const double FigureGap = 32;


    /// <summary>The thirteen parts' strip at the head of System health: one dot each, this far apart.</summary>
    public const double StripDot = 8, StripGap = 6;

    /// <summary>One column of the Performance figures: wide enough for a five-figure net set at <see cref="Display"/>.</summary>
    public const double PerformanceColumn = 150;

    /// <summary>The activity timeline: the time's column, the line's column, the dot on the line.</summary>
    public const double TimelineTime = 46, TimelineMark = 22, TimelineDot = 5;

    /// <summary>Where on a timeline row the dot sits: on the optical middle of the first line of small text.</summary>
    public const double TimelineDotTop = 7;

    /// <summary>A line of the timeline, and the room under each entry.</summary>
    public const double TimelineLine = 19;

    /// <summary>The inbox's drop target: how tall it stands, and the dash of its hairline.</summary>
    public const double DropMinHeight = 210;
    public static readonly AvaloniaList<double> DropDash = [5, 4];

    /// <summary>The conversation's line height, and how wide the owner's words may run on the right.</summary>
    public const double ChatLine = 23, ChatYouMaxWidth = 520;

    /// <summary>The size the conversation's words are set at: a breath larger than the page's body, because it is read like a letter.</summary>
    public const double ChatText = 14;

    /// <summary>A rule the AI's words hang from: indigo, thin.</summary>
    public const double ChatRule = 2;

    /// <summary>The composer's well: rounder than a field, because it is the one thing at the bottom of the conversation.</summary>
    public static readonly Avalonia.CornerRadius RadiusComposer = new(22);

    /// <summary>The report's tabular lines: how tall a line is; and the room above a section's heading.</summary>
    public const double ReportLine = 20, ReportSectionGap = 20;

    /// <summary>The report's prose lines read at most this wide.</summary>
    public const double ReportProseWidth = 640;

    /// <summary>A line of the report this long or longer is prose, set to read; a shorter one is a figure, set to compare.</summary>
    public const int ReportProseLength = 110;

    /// <summary>A meter beside the day's AI spend: the length it is drawn at.</summary>
    public const double SpendMeterWidth = 320;
}

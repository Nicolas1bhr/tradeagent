using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.App;

/// <summary>
/// THE FEW NEW WORDS AND PIECES THE ORGANISATION'S PAGES ON GLASS NEED (<c>U-ui-world</c>, builder B3). Every sentence those pages said before
/// still comes from where it came from; these are only the short labels a drawing needs beside it — a class's short name over its line, a
/// figure's caption, how long a deadline has left. None is a claim: each is the app's own measure, said plainly.
/// </summary>
static partial class Ui
{
    /// <summary>A billing class in two or three words, for the label beside its line (the class's full words stay in the sentences).</summary>
    public static string CostClassShort(string billingClass) => billingClass switch
    {
        BillingClasses.Key => "Real money",
        BillingClasses.Plan => "Plan, list price",
        _ => "Not established"
    };

    /// <summary>What a figure at the top of the Costs page counts: its class, and what that class is (and is not).</summary>
    public static string CostFigureCaption(string billingClass) => billingClass switch
    {
        BillingClasses.Key => "Real money, on an API key",
        BillingClasses.Plan => "Your ChatGPT plan at list price: capacity, not money",
        _ => "Not established: counted against both limits"
    };

    /// <summary>The eyebrow over the Costs page's chart.</summary>
    public const string CostsUnitsHeading = "By unit";

    /// <summary>
    /// A class's figure for a day: the money it counted, or what is known instead — "None" where no turn of it ran, "None finished" where only
    /// turns in flight did, "Unknown" where every finished turn reported no cost. Never a zero for what nobody knows.
    /// </summary>
    public static string CostFigure(AiAttemptTotals? t, string currency) =>
        t is null ? "None"
        : t.Turns == 0 ? "None finished"
        : t.Unpriced >= t.Turns ? "Unknown"
        : Labels.Money(t.Spent, currency);

    /// <summary>Whether the figure is a floor: some finished turns reported no cost and are not in it.</summary>
    public static bool CostAtLeast(AiAttemptTotals? t) => t is { Turns: > 0, Unpriced: > 0 } && t.Unpriced < t.Turns;

    /// <summary>The word over a figure that is a floor.</summary>
    public const string AtLeast = "at least";

    /// <summary>The count beside a figure: the turns finished and those in flight.</summary>
    public static string CostTurns(AiAttemptTotals? t)
    {
        if (t is null) return "";
        var turns = $"{t.Turns.ToString(CultureInfo.InvariantCulture)} {(t.Turns == 1 ? "turn" : "turns")}";
        return t.Open > 0 ? $"{turns} · {t.Open.ToString(CultureInfo.InvariantCulture)} in flight" : turns;
    }

    /// <summary>What the Work page's four counts at its head count.</summary>
    public const string WorkOpenCount = "mandates open", WorkLateCount = "past their deadline", WorkDeliveryCount = "deliveries to judge",
        WorkDecisionCount = "decisions in 7 days";

    /// <summary>A mandate's spend beside its line: what was counted of what was allocated, or that the spend is unknown.</summary>
    public static string SpendOf(decimal? spent, decimal allocation, string currency) =>
        spent is { } s ? $"{Labels.Money(s, currency)} of {Labels.Money(allocation, currency)}" : $"unknown of {Labels.Money(allocation, currency)}";

    /// <summary>
    /// How a deadline stands now, in the largest unit that says it: "3 days left", "5 h left", "40 min left", or "2 h past its deadline".
    /// </summary>
    public static string TimeLeft(DateTimeOffset deadline, DateTimeOffset now)
    {
        var late = now > deadline;
        var span = late ? now - deadline : deadline - now;
        string amount = span.TotalDays >= 2 ? $"{Math.Floor(span.TotalDays).ToString(CultureInfo.InvariantCulture)} days"
            : span.TotalHours >= 1 ? $"{Math.Floor(span.TotalHours).ToString(CultureInfo.InvariantCulture)} h"
            : $"{Math.Max(1, Math.Floor(span.TotalMinutes)).ToString(CultureInfo.InvariantCulture)} min";
        return late ? $"{amount} past its deadline" : $"{amount} left";
    }

    /// <summary>A figure set large and light in the page's own measure; the caller keeps it and rewrites its text in place.</summary>
    public static TextBlock FigureLarge(double size, double line) => new()
    {
        FontSize = size, LineHeight = line, FontWeight = FontWeight.Light, FontFamily = Theme.SansDisplay,
        FontFeatures = Theme.Tabular, LetterSpacing = Theme.DisplayTracking, Foreground = Theme.Text, TextWrapping = TextWrapping.Wrap
    };

    /// <summary>Small text in the muted voice, wrapping: the quiet detail under a drawn row.</summary>
    public static TextBlock Quiet(string text = "") => With(Muted(text), t => t.FontSize = Theme.Small);

    /// <summary>A small figure beside a drawn line: tabular, so a column of them does not jitter.</summary>
    public static TextBlock SmallFigure(IBrush? tone = null) => new()
    {
        FontSize = Theme.Small, FontFeatures = Theme.Tabular, Foreground = tone ?? Theme.Text,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
    };
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;

namespace TradeAgent.App;

/// <summary>
/// THE PAGE'S SENTENCES, SEPARATED FROM ITS CONTROLS so they can be tested without an Avalonia
/// application — the same split <c>PerformanceCard.Words</c> makes, and for the same reason: what
/// this page says about a report it does not have is the part that can be quietly wrong, and a
/// screenshot does not prove it.
/// </summary>
sealed record ReportWords(string Heading, string Written, string Note, bool HasNote, string Body);

/// <summary>
/// THE OWNER'S DAILY REPORT, ON A PAGE THAT IS WORDS AND NOTHING ELSE.
///
/// <para>The report is written by the app from what it measured (<see cref="DailyReports"/>); this
/// page only shows it, offers the earlier days, and lets the owner ask for today's again. There is no
/// chart here on purpose — <c>docs/COUNCIL.md</c> rule 10 asks for a FACTUAL report, and a figure
/// drawn as a shape is a figure whose gaps have been smoothed over. Every unknown in the document is
/// a dash with a reason printed beside it, and this page shows those lines exactly as they were
/// written.</para>
///
/// <para><b>The Operations Director's note is shown BESIDE the facts, never inside them.</b> It is
/// the one paid thing near this page and it exists only when an app predicate changed; on a day that
/// has none, the page says so rather than showing the most recent one it can find. A note from
/// another day presented as today's would be exactly the merge of measurement and claim that the
/// material and fill ledgers exist to prevent.</para>
///
/// <para><b>Built once, updated in place</b>, like every other page here. The day buttons and the
/// body are rebuilt only when the day or the text actually changes, never on the five-second
/// refresh — rebuilding a tree is not a refresh, and the report is the one screen a person reads
/// slowly.</para>
/// </summary>
sealed class ReportPage
{
    readonly AppHost _host;

    readonly TextBlock _written = Ui.Muted("");
    readonly TextBlock _noteText = Ui.Body("");
    readonly Control _noteSection;
    // A plain WrapPanel with the spacing as a margin on each child, the way Ui.Wrap does it:
    // the panel's own spacing properties are not version-proof across Avalonia releases.
    readonly WrapPanel _days = new();
    readonly StackPanel _body = new() { Spacing = Theme.S1 };

    /// <summary>The most earlier days offered at once. Older ones are still on disk.</summary>
    const int DaysOffered = 14;

    string? _day;
    string _signature = "";
    string _daySignature = "";

    public Control Root { get; }

    public ReportPage(AppHost host)
    {
        _host = host;

        _noteSection = Ui.Col(Theme.S2,
            Ui.H2("The Operations Director's note"),
            Ui.With(Ui.Muted("Written by your AI, not measured by TradeAgent. It appears only on a day "
                             + "something changed."), t => t.FontSize = Theme.Small),
            _noteText);

        var body = Ui.Col(Theme.S6,
            Ui.Col(Theme.S2,
                Ui.With(Ui.Row(Theme.S2,
                        Ui.Secondary("Write it now", WriteNow),
                        Ui.Ghost("Today", () => Select(null))),
                    r => r.HorizontalAlignment = HorizontalAlignment.Left),
                _days),
            Ui.Col(Theme.S3, _written, _noteSection),
            Ui.Card(_body));

        var scroll = Pages.Scroll(body);
        scroll[Grid.RowProperty] = 1;

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(Pages.Header("Daily report",
            "What TradeAgent measured, day by day. Nothing in it is inferred and no AI turn is spent "
            + "writing it."));
        root.Children.Add(scroll);
        Root = root;
    }

    void Select(string? day)
    {
        _day = day;
        Update();
    }

    void WriteNow()
    {
        _host.Gateway.Reports.WriteNow(_host.ReportInputs());
        _day = null;
        Update();
    }

    /// <summary>
    /// In place on every tick, and touching nothing when nothing moved. The signature carries the
    /// text itself, so a report rewritten by the midnight pass appears without the reader pressing
    /// anything and a report that did not change leaves the scroll position alone.
    /// </summary>
    public void Update()
    {
        var reports = _host.Gateway.Reports;
        var days = reports.Days();
        var day = _day ?? days.FirstOrDefault() ?? DailyReports.DayName(DateTimeOffset.Now);
        var words = Words(day, reports.Read(day), reports.NoteFor(day));

        var daySignature = string.Join('|', days.Take(DaysOffered)) + "#" + day;
        if (daySignature != _daySignature)
        {
            _daySignature = daySignature;
            _days.Children.Clear();
            foreach (var d in days.Take(DaysOffered))
            {
                var button = Ui.Ghost(d, () => Select(d));
                button.Margin = new Thickness(0, 0, Theme.S2, Theme.S2);
                Ui.Emphasise(button, d == day);
                _days.Children.Add(button);
            }
        }

        var signature = $"{words.Heading}|{words.Written}|{words.Note}|{words.Body.Length}";
        if (signature == _signature) return;
        _signature = signature;

        _written.Text = words.Written;
        _noteText.Text = words.Note;
        _noteText.Foreground = words.HasNote ? Theme.Text : Theme.TextMuted;

        _body.Children.Clear();
        foreach (var line in words.Body.Replace("\r\n", "\n").Split('\n'))
            _body.Children.Add(Line(line));
    }

    /// <summary>
    /// One line of the document, as words. A heading is a heading and everything else is body text;
    /// nothing is reformatted, reordered or dropped, because a report the screen edits is not the
    /// report the file says was written.
    /// </summary>
    static Control Line(string raw)
    {
        if (raw.StartsWith("## ", StringComparison.Ordinal)) return Ui.H3(raw[3..]);
        if (raw.StartsWith("# ", StringComparison.Ordinal)) return Ui.H2(raw[2..]);
        if (raw.Length == 0) return Ui.Spacer(Theme.S2);

        var text = raw.StartsWith("- ", StringComparison.Ordinal) ? raw[2..] : raw;
        var block = Ui.Body(text, raw.Contains("missing (", StringComparison.Ordinal)
            ? Theme.Caution
            : Theme.TextMuted);
        block.FontSize = Theme.Small;
        block.TextWrapping = TextWrapping.Wrap;
        return block;
    }

    /// <summary>
    /// WHAT THE PAGE SAYS, INCLUDING WHAT IT SAYS WHEN THERE IS NOTHING TO SHOW.
    ///
    /// <paramref name="note"/> is the publication the app found FOR THIS DAY, or null. Null is said
    /// out loud: a day with no note is a day on which nothing material changed, and that is a fact
    /// the owner should read rather than a blank space they have to interpret.
    /// </summary>
    public static ReportWords Words(string day, string? body, Publication? note) => new(
        Heading: day,
        Written: body is null
            ? $"No report has been written for {day} yet. Press Write it now for today's."
            : $"{day}, written by TradeAgent from what it measured. Nothing in it is inferred, and no "
              + "AI turn was spent producing it.",
        Note: note is not null
            ? note.Content
            : "No note for this day — your AI adds one only when something material changed, and it "
              + "is paid for when it does.",
        HasNote: note is not null,
        Body: body ?? "");
}

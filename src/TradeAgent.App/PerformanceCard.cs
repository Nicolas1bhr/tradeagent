using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TradeAgent.Gateway;

namespace TradeAgent.App;

/// <summary>
/// The four numbers, as the owner should read them. Separated from the controls so the WORDS can be
/// tested without an Avalonia application — the sentence "we do not know" is the part of this card
/// that matters, and it is not something a screenshot proves.
/// </summary>
sealed record PerformanceWords(
    string Net, string Fees, string Drop, string Fills, string Column, string? Incomplete, bool NetIsPositive, bool NetIsKnown);

/// <summary>
/// What the AI has actually made or lost, on the page the owner already looks at.
///
/// <para>IT COUNTS CLOSED TRADES AND SAYS SO. Open positions are not in these figures: valuing them
/// needs the platform's position list and a current price, which is a connector read, and this card
/// is repainted every five seconds. <c>trade pnl</c> is the answer that includes them. The heading
/// under the numbers says which is which, because a performance figure whose scope the reader has to
/// guess at is worse than none.</para>
///
/// <para>A DASH IS AN ANSWER. Where the net after costs cannot be computed — a platform that did not
/// report a fee on some fill — the number is withheld and the sentence underneath says why. It is
/// never quietly shown as though the missing cost were zero, which would flatter the AI's result on
/// exactly the fills nobody checked.</para>
/// </summary>
sealed class PerformanceCard
{
    readonly TextBlock _todayNet = Ui.Mono("—");
    readonly TextBlock _todayFees = Ui.Mono("—");
    readonly TextBlock _todayDrop = Ui.Mono("—");
    readonly TextBlock _todayFills = Ui.Mono("—");
    readonly TextBlock _allNet = Ui.Mono("—");
    readonly TextBlock _allFees = Ui.Mono("—");
    readonly TextBlock _allDrop = Ui.Mono("—");
    readonly TextBlock _allFills = Ui.Mono("—");
    readonly TextBlock _allHeading = Ui.Micro("since the first fill");
    readonly TextBlock _incomplete;
    readonly TextBlock _empty;
    readonly Grid _numbers;

    string _signature = "";

    public Control Root { get; }

    public PerformanceCard()
    {
        _incomplete = Ui.Micro("");
        _incomplete.Foreground = Theme.Caution;
        _incomplete.IsVisible = false;

        _empty = Ui.Muted("No fills yet. This fills in the moment the AI's first order is executed.");

        _numbers = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,110,110"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto"),
            IsVisible = false
        };

        Head(0, 1, Ui.Micro("today"));
        Head(0, 2, _allHeading);
        Row(1, "Net, after costs", _todayNet, _allNet);
        Row(2, "Fees", _todayFees, _allFees);
        Row(3, "Worst drop", _todayDrop, _allDrop);
        Row(4, "Fills", _todayFills, _allFills);

        Root = Ui.Section("Performance", Ui.Col(Theme.S3,
            _empty,
            _numbers,
            Ui.Micro("Closed trades only, in your account's currency. Anything still open is not counted here."),
            _incomplete));
    }

    void Head(int row, int column, TextBlock text)
    {
        text.HorizontalAlignment = HorizontalAlignment.Right;
        text[Grid.RowProperty] = row;
        text[Grid.ColumnProperty] = column;
        _numbers.Children.Add(text);
    }

    void Row(int row, string label, TextBlock today, TextBlock all)
    {
        var name = Ui.Body(label, Theme.TextMuted);
        name.FontSize = Theme.Small;
        name[Grid.RowProperty] = row;
        name[Grid.ColumnProperty] = 0;
        _numbers.Children.Add(name);

        foreach (var (cell, column) in new[] { (today, 1), (all, 2) })
        {
            cell.HorizontalAlignment = HorizontalAlignment.Right;
            cell.FontWeight = FontWeight.SemiBold;
            cell[Grid.RowProperty] = row;
            cell[Grid.ColumnProperty] = column;
            _numbers.Children.Add(cell);
        }
    }

    /// <summary>
    /// Updated IN PLACE on the five-second tick, like everything else on this page: the controls are
    /// built once in the constructor and only their text changes. The signature is what keeps the
    /// tick from touching anything at all when nothing has moved.
    /// </summary>
    public void Update(PnlReport today, PnlReport all)
    {
        var t = Words(today, "today");
        var a = Words(all, all.FirstFillAt is { } first ? $"since {first.ToLocalTime():d MMM}" : "all time");
        var signature = $"{t.Net}|{t.Fees}|{t.Drop}|{t.Fills}|{a.Net}|{a.Fees}|{a.Drop}|{a.Fills}|{a.Column}|{t.Incomplete}|{a.Incomplete}";
        if (signature == _signature) return;
        _signature = signature;

        var any = all.Fills > 0;
        _numbers.IsVisible = any;
        _empty.IsVisible = !any;

        _allHeading.Text = a.Column;
        Paint(_todayNet, t);
        Paint(_allNet, a);
        _todayFees.Text = t.Fees;
        _allFees.Text = a.Fees;
        _todayDrop.Text = t.Drop;
        _allDrop.Text = a.Drop;
        _todayFills.Text = t.Fills;
        _allFills.Text = a.Fills;

        // The two windows' gaps are the same gaps most of the time, so they are not printed twice.
        var lines = new List<string>();
        foreach (var line in new[] { t.Incomplete, a.Incomplete })
            if (line is not null && !lines.Contains(line)) lines.Add(line);
        _incomplete.Text = lines.Count == 0 ? "" : "Incomplete: " + string.Join(" ", lines);
        _incomplete.IsVisible = lines.Count > 0 && any;
    }

    static void Paint(TextBlock cell, PerformanceWords w)
    {
        cell.Text = w.Net;
        cell.Foreground = !w.NetIsKnown ? Theme.TextMuted : w.NetIsPositive ? Theme.Positive : Theme.Danger;
    }

    /// <summary>
    /// One window's numbers as text. A withheld figure is a dash and never a zero, and the reason is
    /// carried alongside it rather than left for the reader to infer from the dash.
    /// </summary>
    public static PerformanceWords Words(PnlReport r, string column) => new(
        Net: r.Net is { } net ? Money(net, sign: true) : "—",
        Fees: r.Fees is { } fees
            ? r.FeesUnknownFills > 0 ? $"{Money(fees)}+" : Money(fees)
            : r.Fills == 0 ? Money(0m) : "not reported",
        Drop: Money(r.MaxDrawdown),
        Fills: r.Fills.ToString(CultureInfo.InvariantCulture),
        Column: column,
        Incomplete: r.Incomplete.Count == 0 ? null : string.Join(" ", r.Incomplete.Select(Sentence)),
        NetIsPositive: r.Net >= 0m,
        NetIsKnown: r.Net is not null);

    static string Sentence(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..] + (s.EndsWith('.') ? "" : ".");

    static string Money(decimal v, bool sign = false) =>
        (sign && v > 0 ? "+" : "") + v.ToString("#,##0.00", CultureInfo.InvariantCulture);
}

using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.App;

/// <summary>ONE READ OF TODAY'S COSTS: the chart's units and each unit's day split by the class that paid for it.</summary>
sealed record CostsRead(
    IReadOnlyList<OrgUnitRow> Units,
    IReadOnlyList<UnitClassTotals> Day,
    DateTimeOffset From,
    string Currency)
{
    public static CostsRead Of(OrgStore org, AiAttemptStore attempts, DateTimeOffset now, string currency)
    {
        var (from, to) = OwnerDay.Window(now);
        return new CostsRead(org.Units(), attempts.UnitTotalsByClass(from, to), from, currency);
    }
}

/// <summary>One unit's day as the page says it: a line for each billing class, in the order plan, key, not established.</summary>
sealed record CostUnitWords(string Id, int Depth, string Title, string Plan, string Key, string Unknown);

/// <summary>
/// THE COSTS PAGE'S SENTENCES, SEPARATED FROM ITS CONTROLS (<c>U-org-surface-a</c>): <b>three figures, never one</b> (<c>docs/COUNCIL.md</c> rule 4) —
/// the plan's list-price equivalent, an API key's real money, and what nobody established, each its own line and never added to another. Every
/// figure is the launch ledger's. <b>Unknown is never zero:</b> a turn that reported no cost is counted and named beside the figure it is NOT in, and
/// a turn charged its reservation because it reported no usage is named so the figure is not read as a bill.
/// </summary>
sealed record CostsWords(string Status, string Window, IReadOnlyList<CostUnitWords> Units)
{
    public static CostsWords Of(CostsRead? read, string? unreadable)
    {
        if (read is null)
            return new CostsWords(unreadable is { Length: > 0 } ? Labels.SurfaceUnreadable(unreadable) : Labels.SurfaceNotRead, "", []);

        var byUnit = read.Day.ToLookup(d => d.UnitId, StringComparer.Ordinal);
        var units = OrgText.Tree(read.Units)
            .Where(t => t.Unit.Status != OrgUnitStatus.Closed || byUnit[t.Unit.Id].Any())   // a closed unit with no turns today has nothing to say
            .Select(t => new CostUnitWords(t.Unit.Id, t.Depth, OrgText.UnitTitle(t.Unit),
                Line(BillingClasses.Plan, byUnit[t.Unit.Id], read.Currency),
                Line(BillingClasses.Key, byUnit[t.Unit.Id], read.Currency),
                Line(BillingClasses.Unknown, byUnit[t.Unit.Id], read.Currency)))
            .ToList();

        return new CostsWords(
            units.Count == 0 ? Labels.CostsNoUnits : "",
            $"Today, {read.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} (your local day). Figures are in {(read.Currency.Length == 0 ? "the AI's currency" : read.Currency)}.",
            units);
    }

    static string Line(string billingClass, IEnumerable<UnitClassTotals> rows, string currency)
    {
        var heading = Labels.CostsClassWords(billingClass);
        if (rows.FirstOrDefault(r => r.BillingClass == billingClass)?.Totals is not { } t) return $"{heading}: none today.";

        var counted = billingClass switch
        {
            BillingClasses.Plan => $"{Labels.Money(t.Spent, currency)} at list price",
            BillingClasses.Key => $"{Labels.Money(t.Spent, currency)} spent",
            _ => $"{Labels.Money(t.Spent, currency)} counted"
        };
        var line = t.Turns == 0 ? $"{heading}: no turn has finished" : $"{heading}: {counted} over {Count(t.Turns, "turn", "turns")}";
        if (t.Open > 0)
            line += $"; {Count(t.Open, "turn is", "turns are")} in flight, holding {Labels.Money(t.Reserved, currency)}";
        if (t.Unpriced > 0)
            line += $"; {Count(t.Unpriced, "turn", "turns")} reported no cost and {(t.Unpriced == 1 ? "is" : "are")} not in that figure";
        if (t.Unreported > 0)
            line += $"; {Count(t.Unreported, "turn", "turns")} reported no usage and {(t.Unreported == 1 ? "is" : "are")} charged "
                    + "the reservation, so that figure is not a bill";
        if (t.Estimated > 0)
            line += $"; {Count(t.Estimated, "turn was", "turns were")} priced as an estimate because no model was named";
        return line + ".";
    }

    static string Count(int n, string one, string many) => $"{n.ToString(CultureInfo.InvariantCulture)} {(n == 1 ? one : many)}";
}

/// <summary>
/// THE COSTS (<c>U-org-surface-a</c>; R24 § 4.7 "costs by billing class, per unit"): each unit's day split by who paid — the plan, an API key, or
/// nobody established — with the turns whose cost is unknown named and never added as zero. <b>It only reads</b>: the limits these figures are
/// held against are not shown or set here.
///
/// <para><b>Built once, updated in place.</b> The skeleton is made in the constructor; a unit's card is made the first time its id is seen and kept
/// (<see cref="LiveList{T}"/>), and a refresh writes new text into the same three text blocks.</para>
/// </summary>
sealed class CostsPage
{
    readonly OrgSurfaceSources _src;
    readonly TextBlock _status = Ui.Muted("");
    readonly TextBlock _window = Ui.With(Ui.Muted(""), t => t.FontSize = Theme.Small);
    readonly StackPanel _units = new() { Spacing = Theme.S3 };
    readonly LiveList<CostUnitWords> _list;

    public Control Root { get; }

    internal StackPanel UnitCards => _units;

    internal TextBlock StatusText => _status;

    internal TextBlock WindowText => _window;

    public CostsPage(OrgSurfaceSources sources)
    {
        _src = sources;
        _status.FontSize = Theme.Small;
        _status.Text = Labels.SurfaceNotRead;
        _list = new LiveList<CostUnitWords>(_units, Ui.Muted(Labels.CostsNoUnits), u => u.Id, MakeUnit);

        var body = Ui.Col(Theme.S4, _status, _window, _units);
        var scroll = Pages.Scroll(body);
        scroll[Grid.RowProperty] = 1;
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(Pages.Header(Labels.NavCosts, Labels.CostsSubtitle + " " + Labels.SurfaceReadOnly));
        root.Children.Add(scroll);
        Root = root;
    }

    internal Control? CardOf(string unitId) => _list.CardOf(unitId);

    /// <summary>In place on every tick, touching nothing when nothing moved.</summary>
    public void Update()
    {
        CostsWords words;
        var read = false;
        try
        {
            if (_src.Org() is { } org && _src.Attempts() is { } attempts)
            {
                words = CostsWords.Of(CostsRead.Of(org, attempts, _src.Now(), _src.Currency()), null);
                read = true;
            }
            else words = CostsWords.Of(null, null);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { return; }   // the quit closed the file under this tick
        catch (Exception ex) when (ex is TradeAgentException or Microsoft.Data.Sqlite.SqliteException)
        {
            words = CostsWords.Of(null, ex.Message);
        }

        OrgText.Set(_status, words.Status);
        _status.IsVisible = words.Status.Length > 0;
        OrgText.Set(_window, words.Window);
        _window.IsVisible = words.Window.Length > 0;
        if (read) _list.Update(words.Units);   // a failed read leaves the last cards; the status above says nothing below is current
    }

    static (Control Card, Action<CostUnitWords> Fill) MakeUnit(CostUnitWords first)
    {
        var title = Ui.With(Ui.Body(""), t => t.FontWeight = FontWeight.SemiBold);
        var plan = Line();
        var key = Line();
        var unknown = Line();
        var card = Ui.Card(Ui.Col(Theme.S2, title, plan, key, unknown));
        return (card, u =>
        {
            OrgText.Set(title, u.Title);
            OrgText.Set(plan, u.Plan);
            OrgText.Set(key, u.Key);
            OrgText.Set(unknown, u.Unknown);
            var indent = new Avalonia.Thickness(u.Depth * Theme.S5, 0, 0, 0);
            if (card.Margin != indent) card.Margin = indent;
        });

        static TextBlock Line() => Ui.With(Ui.Muted(""), t => t.FontSize = Theme.Small);
    }
}

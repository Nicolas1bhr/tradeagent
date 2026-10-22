using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.App;

/// <summary>ONE READ OF THE CHART: its units and positions, each position's recorded seat, and whether a cell proof is recorded.</summary>
sealed record OrganisationRead(
    IReadOnlyList<OrgUnitRow> Units,
    IReadOnlyList<OrgPositionRow> Positions,
    IReadOnlyDictionary<string, RecordedSeat> Seats,
    bool ProofHeld)
{
    public static OrganisationRead From(OrgStore org, AiAttemptStore attempts, bool proofHeld) =>
        new(org.Units(), org.Positions(), attempts.RecordedSeats(), proofHeld);
}

/// <summary>One position as the page says it: where it sits, whether it is active, what it is set to run on, what it last ran on and who paid.</summary>
sealed record OrgPositionWords(string Id, string Role, string Status, string Seat, string Cell, string Billing);

/// <summary>One unit as the page says it, with the depth it is indented to and the positions in it.</summary>
sealed record OrgUnitWords(string Id, int Depth, string Title, string Line, IReadOnlyList<OrgPositionWords> Positions);

/// <summary>
/// THE CHART PAGE'S SENTENCES, SEPARATED FROM ITS CONTROLS so they are tested without a window (the words are what a
/// test asserts). <b>Every figure and word here is the app's own record</b> — the chart's rows, the seat the ledger
/// recorded for a launch, the billing class the app composed — and no organisation text reaches it: a unit has no title column, because titles
/// are claims published by charter (<see cref="OrgStore"/>), so a unit is named by what the app knows of it.
/// </summary>
sealed record OrganisationWords(string Status, string Proof, IReadOnlyList<OrgUnitWords> Units)
{
    /// <summary>The words of one read, or of records that could not be read (<paramref name="read"/> null, <paramref name="unreadable"/> the reason).</summary>
    public static OrganisationWords Of(OrganisationRead? read, string? unreadable)
    {
        if (read is null)
            return new OrganisationWords(unreadable is { Length: > 0 } ? Labels.SurfaceUnreadable(unreadable) : Labels.SurfaceNotRead, "", []);

        var byUnit = read.Positions.ToLookup(p => p.UnitId, StringComparer.Ordinal);
        var units = OrgText.Tree(read.Units).Select(t =>
        {
            var (unit, depth) = t;
            var positions = byUnit[unit.Id].Select(p => PositionWords(unit, p, read.Seats)).ToList();
            return new OrgUnitWords(unit.Id, depth, OrgText.UnitTitle(unit), UnitLine(unit, byUnit[unit.Id]), positions);
        }).ToList();

        return new OrganisationWords(
            units.Count == 0 ? Labels.OrganisationNoUnits : "",
            read.ProofHeld ? Labels.CellProofHeld : Labels.CellProofNone,
            units);
    }

    static string UnitLine(OrgUnitRow unit, IEnumerable<OrgPositionRow> positions)
    {
        var head = unit.IsRoot ? "headed by you"
            : unit.HeadPositionId is { } id ? $"headed by {id}"
            : "its head is vacant";
        var active = positions.Count(p => p.Status == OrgPositionStatus.Active);
        var closed = unit.ClosedAt is { } at ? $", closed {OrgText.When(at)}" : "";
        return $"{unit.Kind}, {unit.Status}{closed}; {head}; {active} active {(active == 1 ? "position" : "positions")}.";
    }

    static OrgPositionWords PositionWords(OrgUnitRow unit, OrgPositionRow p, IReadOnlyDictionary<string, RecordedSeat> seats)
    {
        var role = unit.HeadPositionId == p.Id ? $"head of {OrgText.UnitTitle(unit)}"
            : unit.Kind == OrgUnitKind.Staff ? $"staff of {OrgText.UnitTitle(unit)}"
            : $"member of {OrgText.UnitTitle(unit)}";

        var set = p.SeatRuntime is null
            ? "Set to run on: the app's default runtime and model."
            : p.SeatModel is null
                ? $"Set to run on: {p.SeatRuntime}, no model named."
                : $"Set to run on: {p.SeatRuntime}, model {p.SeatModel}.";

        if (!seats.TryGetValue(p.Id, out var seat))
            return new OrgPositionWords(p.Id, role, p.Status, set,
                "Last ran on: nothing yet — it has not launched, so no seat boundary is recorded.",
                "Paid from: nothing yet — it has not launched, so no billing class is recorded.");

        var cell = seat.Assurance is { } assurance
            ? $"Last ran on: {Labels.SeatAssuranceWords(assurance)} ({OrgText.When(seat.StartedAt)})."
            : $"Last ran on: a seat the ledger records as {seat.SeatClass ?? "no class"}, which is not a boundary this build knows ({OrgText.When(seat.StartedAt)}).";
        return new OrgPositionWords(p.Id, role, p.Status, set, cell, $"Paid from: {BillingClasses.Words(seat.BillingClass)}.");
    }
}

/// <summary>
/// THE CHART (<c>U-org-surface-a</c>; R24 § 4.7 "Organisation-first"): each unit with its positions — active or dormant, what each is set to
/// run on, the boundary of the seat it last ran on in plain words and who paid for that launch. <b>It only reads</b>: no press, no box, no
/// pipe op — a position's seat is changed where seats are changed, never from here.
///
/// <para><b>Built once, updated in place.</b> The page's skeleton is made in the constructor; a unit's card and a position's row are made the
/// first time their id is seen and kept (<see cref="LiveList{T}"/>), and a refresh writes new text into the same text blocks and re-lays a
/// list only when an id arrived, left or moved.</para>
/// </summary>
sealed class OrganisationPage
{
    readonly OrgSurfaceSources _src;
    readonly TextBlock _status = Ui.Muted("");
    readonly TextBlock _proof = Ui.Muted("");
    readonly StackPanel _units = new() { Spacing = Theme.S4 };
    readonly LiveList<OrgUnitWords> _list;

    public Control Root { get; }

    /// <summary>The unit cards' panel, for a test to read what the page holds.</summary>
    internal StackPanel UnitCards => _units;

    /// <summary>The page's status line: why nothing below is current, or empty.</summary>
    internal TextBlock StatusText => _status;

    internal TextBlock ProofText => _proof;

    public OrganisationPage(OrgSurfaceSources sources)
    {
        _src = sources;
        _status.FontSize = Theme.Small;
        _proof.FontSize = Theme.Small;
        _list = new LiveList<OrgUnitWords>(_units, Ui.Muted(Labels.OrganisationNoUnits), u => u.Id, MakeUnit);
        _status.Text = Labels.SurfaceNotRead;

        var body = Ui.Col(Theme.S4, _status, _proof, _units);
        var scroll = Pages.Scroll(body);
        scroll[Grid.RowProperty] = 1;
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(Pages.Header(Labels.NavOrganisation, Labels.OrganisationSubtitle + " " + Labels.SurfaceReadOnly));
        root.Children.Add(scroll);
        Root = root;
    }

    /// <summary>The card of <paramref name="unitId"/>, or null until the unit has been read — the same instance on every refresh.</summary>
    internal Control? CardOf(string unitId) => _list.CardOf(unitId);

    /// <summary>In place on every tick, touching nothing when nothing moved.</summary>
    public void Update()
    {
        OrganisationWords words;
        var read = false;
        try
        {
            if (_src.Org() is { } org && _src.Attempts() is { } attempts)
            {
                words = OrganisationWords.Of(OrganisationRead.From(org, attempts, _src.ProofHeld()), null);
                read = true;
            }
            else words = OrganisationWords.Of(null, null);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { return; }   // the quit closed the file under this tick
        catch (Exception ex) when (ex is TradeAgentException or Microsoft.Data.Sqlite.SqliteException)
        {
            words = OrganisationWords.Of(null, ex.Message);
        }

        OrgText.Set(_status, words.Status);
        _status.IsVisible = words.Status.Length > 0;
        OrgText.Set(_proof, words.Proof);
        _proof.IsVisible = words.Proof.Length > 0;
        // A read that failed leaves the last cards as they were: the status line above them says nothing below is current.
        if (read) _list.Update(words.Units);
    }

    static (Control Card, Action<OrgUnitWords> Fill) MakeUnit(OrgUnitWords first)
    {
        var title = Ui.With(Ui.Body(""), t => t.FontWeight = FontWeight.SemiBold);
        var line = Ui.With(Ui.Muted(""), t => t.FontSize = Theme.Small);
        var positions = new StackPanel { Spacing = Theme.S3 };
        var list = new LiveList<OrgPositionWords>(positions, Ui.Muted("No position is in this unit."), p => p.Id, MakePosition);
        var card = Ui.Card(Ui.Col(Theme.S3, title, line, positions));

        return (card, u =>
        {
            OrgText.Set(title, u.Title);
            OrgText.Set(line, u.Line);
            var indent = new Thickness(u.Depth * Theme.S5, 0, 0, 0);
            if (card.Margin != indent) card.Margin = indent;
            list.Update(u.Positions);
        });
    }

    static (Control Card, Action<OrgPositionWords> Fill) MakePosition(OrgPositionWords first)
    {
        var id = Ui.With(Ui.Mono(""), t => t.FontWeight = FontWeight.SemiBold);
        var status = Ui.With(Ui.Muted(""), t => { t.FontSize = Theme.Small; t.FontWeight = FontWeight.SemiBold; });
        var role = Small();
        var seat = Small();
        var cell = Small();
        var billing = Small();
        var head = Ui.Row(Theme.S3, id, status);
        head.VerticalAlignment = VerticalAlignment.Center;
        var row = Ui.Col(Theme.S1, head, role, seat, cell, billing);
        row.Margin = new Thickness(Theme.S3, 0, 0, 0);

        return (row, p =>
        {
            OrgText.Set(id, p.Id);
            OrgText.Set(status, p.Status);
            var tone = p.Status == OrgPositionStatus.Active ? Theme.Positive : Theme.TextMuted;
            if (!ReferenceEquals(status.Foreground, tone)) status.Foreground = tone;
            OrgText.Set(role, p.Role);
            OrgText.Set(seat, p.Seat);
            OrgText.Set(cell, p.Cell);
            OrgText.Set(billing, p.Billing);
        });

        static TextBlock Small() => Ui.With(Ui.Muted(""), t => t.FontSize = Theme.Small);
    }
}

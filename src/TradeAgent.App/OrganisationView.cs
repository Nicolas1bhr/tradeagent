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
    bool ProofHeld,
    IReadOnlyDictionary<string, ResolvedSeat>? Resolved = null)
{
    /// <summary>
    /// <paramref name="seatOf"/> resolves an ACTIVE position that has no seat of its own the way its launch would; null resolves none, and the
    /// page says so instead of naming a runtime.
    /// </summary>
    public static OrganisationRead From(OrgStore org, AiAttemptStore attempts, bool proofHeld, Func<string, ResolvedSeat>? seatOf = null)
    {
        var positions = org.Positions();
        var resolved = seatOf is null
            ? null
            : positions.Where(p => p.SeatRuntime is null && p.Status == OrgPositionStatus.Active)
                .ToDictionary(p => p.Id, p => seatOf(p.Id), StringComparer.Ordinal);
        return new OrganisationRead(org.Units(), positions, attempts.RecordedSeats(), proofHeld, resolved);
    }
}

/// <summary>One position as the page says it: where it sits, whether it is active, what it is set to run on, what it last ran on and who paid.</summary>
sealed record OrgPositionWords(string Id, string Role, string Status, string Seat, string Cell, string Billing, string? HomeRefusal = null);

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
    public static OrganisationWords Of(OrganisationRead? read, string? unreadable, Func<string, string?>? homeRefusal = null)
    {
        if (read is null)
            return new OrganisationWords(unreadable is { Length: > 0 } ? Labels.SurfaceUnreadable(unreadable) : Labels.SurfaceNotRead, "", []);

        var (tree, damage) = OrgText.Tree(read.Units);
        if (damage is not null) return new OrganisationWords(Labels.ChartDamaged(damage), "", []);

        var byUnit = read.Positions.ToLookup(p => p.UnitId, StringComparer.Ordinal);
        var units = tree.Select(t =>
        {
            var (unit, depth) = t;
            var positions = byUnit[unit.Id].Select(p => PositionWords(unit, p, read.Seats, read.Resolved) with { HomeRefusal = homeRefusal?.Invoke(p.Id) })
                .ToList();
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

    static OrgPositionWords PositionWords(OrgUnitRow unit, OrgPositionRow p, IReadOnlyDictionary<string, RecordedSeat> seats,
        IReadOnlyDictionary<string, ResolvedSeat>? resolved)
    {
        var role = unit.HeadPositionId == p.Id ? $"head of {OrgText.UnitTitle(unit)}"
            : unit.Kind == OrgUnitKind.Staff ? $"staff of {OrgText.UnitTitle(unit)}"
            : $"member of {OrgText.UnitTitle(unit)}";

        string set;
        if (p.SeatRuntime is not null)
            set = p.SeatModel is null ? $"Set to run on: {p.SeatRuntime}, no model named." : $"Set to run on: {p.SeatRuntime}, model {p.SeatModel}.";
        else if (resolved is not null && resolved.TryGetValue(p.Id, out var r))
            set = r.Refused is { Length: > 0 } why
                ? $"Its seat cannot be resolved: {why}."
                : r.Runtime is null
                    ? "Resolves to: no runtime is chosen yet (it has no seat of its own; the app's settings for the role decide)."
                    : $"Resolves to: {r.Runtime}, {(r.Model is null ? "no model named" : $"model {r.Model}")} (it has no seat of its own; the app's settings for the role decide).";
        else if (p.Status != OrgPositionStatus.Active)
            set = $"Set to run on: nothing of its own; it is {p.Status}, so no seat is resolved.";
        else
            set = "Set to run on: nothing of its own, and where it runs is not resolved here.";

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
/// run on, the boundary of the seat it last ran on in plain words and who paid for that launch. <b>It only reads</b>: no box, no pipe op — a
/// position's seat is changed where seats are changed, never from here — and its one press is the owner's repair of a position's folder the
/// last start could not rebuild (<c>U-repair-position-home</c>), two-press, drawn on that row only while the refusal stands.
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
        root.Children.Add(Pages.Header(Labels.NavOrganisation, Labels.OrganisationSubtitle + " " + Labels.OrganisationRecordsAndRepair));
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
                words = OrganisationWords.Of(OrganisationRead.From(org, attempts, _src.ProofHeld(), _src.SeatOf), null, _src.HomeRefusal);
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

    (Control Card, Action<OrgUnitWords> Fill) MakeUnit(OrgUnitWords first)
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

    (Control Card, Action<OrgPositionWords> Fill) MakePosition(OrgPositionWords first)
    {
        var id = Ui.With(Ui.Mono(""), t => t.FontWeight = FontWeight.SemiBold);
        var status = Ui.With(Ui.Muted(""), t => { t.FontSize = Theme.Small; t.FontWeight = FontWeight.SemiBold; });
        var role = Small();
        var seat = Small();
        var cell = Small();
        var billing = Small();
        var head = Ui.Row(Theme.S3, id, status);
        head.VerticalAlignment = VerticalAlignment.Center;
        // THE OWNER'S REPAIR (U-repair-position-home): the refusal's sentence and its two-press button, made the first time this row's folder is
        // refused and kept — hidden, and disarmed, once the refusal is gone — and the words of the last repair under them.
        var refusal = Ui.With(Small(), t => t.Foreground = Theme.Caution);
        var repaired = Small();
        var repair = new StackPanel { Spacing = Theme.S1 };
        Button? press = null;
        var row = Ui.Col(Theme.S1, head, role, seat, cell, billing, refusal, repair, repaired);
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
            OrgText.Set(refusal, p.HomeRefusal ?? "");
            refusal.IsVisible = p.HomeRefusal is not null;
            if (p.HomeRefusal is not null && press is null && _src.RepairHome is { } act)
            {
                var id = p.Id;
                press = Ui.Confirm(Labels.RepairHome, Labels.RepairHomeConfirm, () =>
                {
                    OrgText.Set(repaired, act(id));
                    repaired.IsVisible = true;
                    var still = _src.HomeRefusal(id);
                    OrgText.Set(refusal, still ?? "");
                    refusal.IsVisible = press!.IsVisible = still is not null;
                });
                press.HorizontalAlignment = HorizontalAlignment.Left;
                repair.Children.Add(press);
            }
            if (press is not null && press.IsVisible != (p.HomeRefusal is not null))
            {
                if (p.HomeRefusal is null) Ui.DisarmConfirm(press);
                press.IsVisible = p.HomeRefusal is not null;
            }
            repaired.IsVisible = repaired.Text is { Length: > 0 };
        });

        static TextBlock Small() => Ui.With(Ui.Muted(""), t => t.FontSize = Theme.Small);
    }
}

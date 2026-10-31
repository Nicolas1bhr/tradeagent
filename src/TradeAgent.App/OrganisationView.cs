using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.App;

/// <summary>ONE READ OF THE CHART: its units and positions, each position's recorded seat, and whether a cell proof is recorded.</summary>
sealed record OrganisationRead(
    IReadOnlyList<OrgUnitRow> Units,
    IReadOnlyList<OrgPositionRow> Positions,
    IReadOnlyDictionary<string, RecordedSeat> Seats,
    bool ProofHeld,
    IReadOnlyDictionary<string, ResolvedSeat>? Resolved = null,
    IReadOnlyDictionary<string, PositionScorecard>? Calibration = null)
{
    /// <summary>
    /// <paramref name="seatOf"/> resolves an ACTIVE position that has no seat of its own the way its launch would; null resolves none, and the
    /// page says so instead of naming a runtime.
    /// </summary>
    /// <para><paramref name="scorecards"/> computes each position's scorecard at read (<c>U-org-scorecards-a</c>); null computes none, and no
    /// calibration line is drawn.</para>
    public static OrganisationRead From(OrgStore org, AiAttemptStore attempts, bool proofHeld, Func<string, ResolvedSeat>? seatOf = null,
        Scorecards? scorecards = null)
    {
        var positions = org.Positions();
        var resolved = seatOf is null
            ? null
            : positions.Where(p => p.SeatRuntime is null && p.Status == OrgPositionStatus.Active)
                .ToDictionary(p => p.Id, p => seatOf(p.Id), StringComparer.Ordinal);
        var calibration = scorecards is null ? null : positions.ToDictionary(p => p.Id, p => scorecards.Of(p.Id), StringComparer.Ordinal);
        return new OrganisationRead(org.Units(), positions, attempts.RecordedSeats(), proofHeld, resolved, calibration);
    }
}

/// <summary>
/// One position as the page says it: where it sits, whether it is active, what it is set to run on, what it last ran on, who paid, its
/// calibration as the app scores it at read (empty where no scorecard was read) and, where its home folder is refused, the refusal's sentence.
/// </summary>
sealed record OrgPositionWords(string Id, string Role, string Status, string Seat, string Cell, string Billing, string Calibration = "", string? HomeRefusal = null,
    string? Model = null);

/// <summary>One unit as the page says it, with the depth it is indented to, the positions in it and its head's id (the root's: the chief).</summary>
sealed record OrgUnitWords(string Id, int Depth, string Title, string Line, IReadOnlyList<OrgPositionWords> Positions, string? HeadId = null);

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
            var positions = byUnit[unit.Id].Select(p => PositionWords(unit, p, read.Seats, read.Resolved) with
            {
                Calibration = read.Calibration is { } cards && cards.TryGetValue(p.Id, out var card) ? Labels.CalibrationWords(card) : "",
                HomeRefusal = homeRefusal?.Invoke(p.Id)
            }).ToList();
            return new OrgUnitWords(unit.Id, depth, OrgText.UnitTitle(unit), UnitLine(unit, byUnit[unit.Id]), positions, unit.HeadPositionId);
        }).ToList();

        return new OrganisationWords(
            units.Count == 0 ? Labels.OrganisationNoUnits : "",
            read.ProofHeld ? Labels.CellProofHeld : Labels.CellProofNone,
            units);
    }

    static string UnitLine(OrgUnitRow unit, IEnumerable<OrgPositionRow> positions)
    {
        // THE ROOT'S HEAD IS THE CHIEF (U-org-chief); vacant, it is yours, in-process.
        var head = unit.IsRoot && unit.HeadPositionId is { } chief ? $"headed by the chief {chief}"
            : unit.IsRoot ? "headed by you: no chief is appointed (vacant)"
            : unit.HeadPositionId is { } id ? $"headed by {id}"
            : "its head is vacant";
        var active = positions.Count(p => p.Status == OrgPositionStatus.Active);
        var closed = unit.ClosedAt is { } at ? $", closed {OrgText.When(at)}" : "";
        return $"{unit.Kind}, {unit.Status}{closed}; {head}; {active} active {(active == 1 ? "position" : "positions")}.";
    }

    static OrgPositionWords PositionWords(OrgUnitRow unit, OrgPositionRow p, IReadOnlyDictionary<string, RecordedSeat> seats,
        IReadOnlyDictionary<string, ResolvedSeat>? resolved)
    {
        var role = unit.HeadPositionId == p.Id ? (unit.IsRoot ? "chief of the organisation" : $"head of {OrgText.UnitTitle(unit)}")
            : unit.Kind == OrgUnitKind.Staff ? $"staff of {OrgText.UnitTitle(unit)}"
            : $"member of {OrgText.UnitTitle(unit)}";

        // THE MODEL IT RUNS ON, as its seat or its launch's resolution names it (U-seat-models): a model of the verified catalog is named with
        // its standing — "(the default)", "(the fallback)" — so the owner's choice shows on the card.
        string? model = null;
        string set;
        if (p.SeatRuntime is not null)
        {
            model = p.SeatModel;
            set = p.SeatModel is null ? $"Set to run on: {p.SeatRuntime}, no model named." : $"Set to run on: {p.SeatRuntime}, model {ModelWords(p.SeatModel)}.";
        }
        else if (resolved is not null && resolved.TryGetValue(p.Id, out var r))
        {
            model = r.Refused is { Length: > 0 } ? null : r.Model;
            set = r.Refused is { Length: > 0 } why
                ? $"Its seat cannot be resolved: {why}."
                : r.Runtime is null
                    ? "Resolves to: nothing yet — no runtime is chosen in the app's settings for its role."
                    : $"Resolves to: {r.Runtime}, {(r.Model is null ? "no model named" : $"model {ModelWords(r.Model)}")} (it has no seat of its own; the app's settings for the role decide).";
        }
        else if (p.Status != OrgPositionStatus.Active)
            set = $"Set to run on: nothing of its own; it is {p.Status}, so no seat is resolved.";
        else
            set = "Set to run on: nothing of its own, and where it runs is not resolved here.";

        if (!seats.TryGetValue(p.Id, out var seat))
            // NEVER LAUNCHED: one sentence, not two that each say "nothing yet" — no seat boundary and no payer are recorded.
            return new OrgPositionWords(p.Id, role, p.Status, set,
                "Has not run yet, so nothing is recorded of where it ran or who paid.", "", Model: model);

        var cell = seat.Assurance is { } assurance
            ? $"Last ran on: {Labels.SeatAssuranceWords(assurance)} ({OrgText.When(seat.StartedAt)})."
            : $"Last ran on: a seat the ledger records as {seat.SeatClass ?? "no class"}, which is not a boundary this build knows ({OrgText.When(seat.StartedAt)}).";
        return new OrgPositionWords(p.Id, role, p.Status, set, cell, $"Paid from: {BillingClasses.Words(seat.BillingClass)}.", Model: model);
    }

    /// <summary>A model as the card names it: a model of the verified catalog with its standing, any other as it is.</summary>
    static string ModelWords(string model) => BrokerSeat.IsCatalogModel(model) ? BrokerSeat.ModelWords(model) : model;
}

/// <summary>
/// THE CHART (<c>U-org-surface-a</c>; R24 § 4.7 "Organisation-first"): each unit with its positions — active or dormant, what each is set to
/// run on, the boundary of the seat it last ran on in plain words and who paid for that launch. <b>It only reads</b>: no box, no pipe op — a
/// position's seat is changed where seats are changed, never from here — and its presses are the owner's repair of a position's folder the
/// last start could not rebuild (<c>U-repair-position-home</c>), two-press, drawn on that row only while the refusal stands, and his presses
/// over the chief on the organisation's card (<see cref="ChiefCard"/>, <c>U-org-chief</c> item 7).
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
                words = OrganisationWords.Of(OrganisationRead.From(org, attempts, _src.ProofHeld(), _src.SeatOf, _src.Scorecards()), null, _src.HomeRefusal);
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

    /// <summary>The organisation's chief card — the same instance on every refresh — or null until the root has been read, or with no presses.</summary>
    internal ChiefCard? Chief { get; private set; }

    (Control Card, Action<OrgUnitWords> Fill) MakeUnit(OrgUnitWords first)
    {
        var title = Ui.With(Ui.Body(""), t => t.FontWeight = FontWeight.SemiBold);
        var line = Ui.With(Ui.Muted(""), t => t.FontSize = Theme.Small);
        var positions = new StackPanel { Spacing = Theme.S3 };
        var list = new LiveList<OrgPositionWords>(positions, Ui.Muted("No position is in this unit."), p => p.Id, MakePosition);
        // THE ORGANISATION'S CARD CARRIES THE OWNER'S PRESSES OVER THE CHIEF (U-org-chief item 7), made once with the card.
        var chief = first.Id == OrgIds.Root && _src.Chief is { } presses ? Chief = new ChiefCard(presses, Update) : null;
        var card = chief is null
            ? Ui.Card(Ui.Col(Theme.S3, title, line, positions))
            : Ui.Card(Ui.Col(Theme.S3, title, line, chief.Root, positions));

        return (card, u =>
        {
            OrgText.Set(title, u.Title);
            OrgText.Set(line, u.Line);
            chief?.Update(u);
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
        var calibration = Small();
        var head = Ui.Row(Theme.S3, id, status);
        head.VerticalAlignment = VerticalAlignment.Center;
        // THE OWNER'S REPAIR (U-repair-position-home): the refusal's sentence and its two-press button, made the first time this row's folder is
        // refused and kept — hidden, and disarmed, once the refusal is gone — and the words of the last repair under them.
        var refusal = Ui.With(Small(), t => t.Foreground = Theme.Caution);
        var repaired = Small();
        var repair = new StackPanel { Spacing = Theme.S1 };
        Button? press = null;
        // THE OWNER'S SWITCH OF ITS MODEL (U-seat-models): one two-step press per model of the verified catalog it is not on, re-laid only
        // when that list changes; the words of the last switch under them.
        var models = new WrapPanel { Orientation = Orientation.Horizontal };
        var switched = Small();
        IReadOnlyList<string> offered = [];
        // THE OWNER'S "REPLACE THE OCCUPANT" (U-worker-replace): made once with the row, shown only while the position is active and beyond the
        // council's pair; TWO presses, press one keeping the preview it armed with and press two handing THAT back — the store refuses as moved,
        // nothing written, a press that would now retire another occupant. Its words (done or refused) under it.
        var replacedSaid = Small();
        OccupantPreview? shown = null;
        Button? replace = null;
        if (_src.Occupant is { } occupant)
        {
            var who = first.Id;
            replace = Ui.ConfirmIf(Labels.ReplaceOccupant,
                () =>
                {
                    shown = occupant.Preview(who);
                    return Labels.ReplaceOccupantConfirm(shown.Words);
                },
                () =>
                {
                    var asShown = shown;
                    shown = null;
                    OrgText.Set(replacedSaid, asShown is not null ? occupant.Replace(asShown) : Labels.ReplaceOccupantUnarmed);
                    replacedSaid.IsVisible = true;
                },
                () => occupant.Preview(who).Refusal,
                why =>
                {
                    OrgText.Set(replacedSaid, why);
                    replacedSaid.IsVisible = true;
                });
            replace.HorizontalAlignment = HorizontalAlignment.Left;
        }
        replacedSaid.IsVisible = false;
        var row = Ui.Col(Theme.S1, head, role, seat, models, switched, cell, billing, calibration, refusal, repair, repaired);
        if (replace is not null) row.Children.Add(replace);
        row.Children.Add(replacedSaid);
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
            if (billing.IsVisible != p.Billing.Length > 0) billing.IsVisible = p.Billing.Length > 0;
            OrgText.Set(calibration, p.Calibration);
            if (calibration.IsVisible != p.Calibration.Length > 0) calibration.IsVisible = p.Calibration.Length > 0;
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

            IReadOnlyList<string> choices = [];
            if (_src.ChooseModel is not null && p.Status != OrgPositionStatus.Ended)
            {
                try { choices = [.. _src.SeatModels(p.Id).Where(m => m != p.Model)]; }
                catch (Exception) { choices = []; }
            }
            if (!choices.SequenceEqual(offered))
            {
                offered = choices;
                models.Children.Clear();
                foreach (var model in choices)
                {
                    var (pos, words) = (p.Id, BrokerSeat.ModelWords(model));
                    var b = Ui.Confirm(Labels.SeatModelSwitch(words), Labels.SeatModelSwitchConfirm(words), () =>
                    {
                        OrgText.Set(switched, _src.ChooseModel!(pos, model));
                        switched.IsVisible = true;
                        Update();
                    });
                    b.Margin = new Thickness(0, 0, Theme.S2, Theme.S2);
                    models.Children.Add(b);
                }
            }
            models.IsVisible = models.Children.Count > 0;
            switched.IsVisible = switched.Text is { Length: > 0 };
            if (replace is not null)
            {
                var occupantOffered = p.Status == OrgPositionStatus.Active && !CouncilRoles.IsKnown(p.Id);
                if (replace.IsVisible != occupantOffered)
                {
                    if (!occupantOffered) Ui.DisarmConfirm(replace);
                    replace.IsVisible = occupantOffered;
                }
            }
        });

        static TextBlock Small() => Ui.With(Ui.Muted(""), t => t.FontSize = Theme.Small);
    }
}

/// <summary>
/// THE OWNER'S PRESSES OVER THE CHIEF (<c>U-org-chief</c> item 7; the orchestrator's Q1), on the organisation's card: while no board seat is active
/// — "yours until the security board is active" — <b>Appoint a chief</b> over a vacant root, <b>Replace the chief</b> and <b>Activate</b> a dormant
/// one; and <b>Remove the chief</b> always. Two presses each (the app's two-step button): the first only arms and writes nothing. The seat a new chief runs on is chosen from a CLOSED list of presses, so no scroll over the page can
/// change it (the owner, 2026-10-22). No veto press: that is <c>U-org-surface-b</c>'s. Built once; <see cref="Update"/> writes text and visibility
/// in place, and disarms a press it hides.
///
/// <para><b>A half-press is about the chief it was armed for</b> (fix pass 1; Codex P1): the card keeps the chief it last showed; a refresh
/// that shows another — the board replaced or removed it, or seated one — disarms every press, so a press armed against A is never
/// completed against B; and Replace, Activate and Remove name that chief to the store, which refuses one who no longer sits as moved, so
/// a change with no refresh between the presses writes nothing either.</para>
/// </summary>
sealed class ChiefCard
{
    readonly ChiefPresses _presses;
    readonly Action _refresh;
    readonly TextBlock _sitting = Ui.Muted("");
    readonly TextBlock _whose = Ui.Muted("");
    readonly TextBlock _done = Ui.Muted("");
    readonly WrapPanel _seats = new() { Orientation = Orientation.Horizontal };
    readonly TextBlock _seatPrompt = Ui.Muted("");
    readonly StackPanel _seatRow;
    readonly List<(ChiefSeat Seat, Button Press)> _choices = [];
    ChiefSeat? _chosen;
    string? _shown;
    readonly TextBlock _wakeWhat = Ui.Muted("");
    ChiefWakePreview? _wakeShown;

    public Control Root { get; }
    internal Button Appoint { get; }
    internal Button Replace { get; }
    internal Button Activate { get; }
    internal Button Remove { get; }
    internal Button WakeOnce { get; }
    internal TextBlock WakeWhat => _wakeWhat;
    internal IReadOnlyList<Button> SeatChoices => [.. _choices.Select(c => c.Press)];
    internal Control SeatRow => _seatRow;
    internal TextBlock Done => _done;

    public ChiefCard(ChiefPresses presses, Action refresh)
    {
        _presses = presses;
        _refresh = refresh;
        foreach (var t in new[] { _sitting, _whose, _done, _seatPrompt, _wakeWhat }) t.FontSize = Theme.Small;
        _done.IsVisible = false;
        _seatRow = Ui.Col(Theme.S1, _seatPrompt, _seats);
        Seats();

        string? NoSeat() => _chosen is null ? Labels.ChiefChooseSeat : null;
        Appoint = Ui.Confirm(Labels.ChiefAppoint, Labels.ChiefAppointConfirm, () => Said(_presses.Appoint(_chosen!)), NoSeat, Said);
        Replace = Ui.Confirm(Labels.ChiefReplace, Labels.ChiefReplaceConfirm, () => Said(_presses.Replace(_shown, _chosen!)), NoSeat, Said);
        Activate = Ui.Confirm(Labels.ChiefActivate, Labels.ChiefActivateConfirm, () => Said(_presses.Activate(_shown)));
        Remove = Ui.Confirm(Labels.ChiefRemove, Labels.ChiefRemoveConfirm, () => Said(_presses.Remove(_shown)));
        // "WAKE THE CHIEF ONCE" (U-chief-wake-once): TWO presses always, as every paid start. Either press is refused in words on this card
        // while the store's preview refuses; press one keeps the preview it showed and arms with its words; press two hands THAT preview
        // back, and the store refuses as moved, nothing written, a press that would now do anything else.
        WakeOnce = Ui.ConfirmIf(Labels.ChiefWakeOnce,
            () =>
            {
                _wakeShown = WakePreview();
                return Labels.ChiefWakeOnceConfirm(_wakeShown.Words);
            },
            () =>
            {
                var shown = _wakeShown;
                _wakeShown = null;
                Said(shown is not null && _presses.WakeOnce is { } wake ? wake(shown) : Labels.ChiefWakeOnceUnarmed);
            },
            () => WakePreview().Refusal,
            Said);

        var title = Ui.With(Ui.Body(Labels.ChiefTitle), t => t.FontWeight = FontWeight.SemiBold);
        var row = Ui.Wrap(Theme.S2, Appoint, Replace, Activate, Remove, WakeOnce);
        Root = Ui.Col(Theme.S2, title, _sitting, _whose, _seatRow, row, _wakeWhat, _done);
    }

    /// <summary>
    /// THE CLOSED LIST, made once and REBUILT ONLY WHEN IT CHANGED (the runtime the setup prepared, or the models priced on it): a choice no
    /// longer offered is forgotten, so a press never seats a chief on a seat the list no longer names.
    /// </summary>
    void Seats()
    {
        IReadOnlyList<ChiefSeat> seats;
        try { seats = _presses.Seats(); }
        catch (Exception) { seats = []; }
        if (seats.SequenceEqual(_choices.Select(c => c.Seat))) return;
        _seats.Children.Clear();
        _choices.Clear();
        foreach (var seat in seats)
        {
            var press = new Button { Content = Labels.ChiefSeatChoice(seat), Classes = { "secondary" }, Margin = new Thickness(0, 0, Theme.S2, Theme.S2) };
            press.Click += (_, _) => Choose(seat);
            _choices.Add((seat, press));
            _seats.Children.Add(press);
        }
        if (_chosen is { } chosen && !seats.Contains(chosen)) _chosen = null;
        else if (_chosen is { } kept) Choose(kept);
        OrgText.Set(_seatPrompt, seats.Count > 0 ? Labels.ChiefSeatPrompt : Labels.ChiefNoSeats);
    }

    /// <summary>The store's preview of the wake-once press now; a source that throws or is absent reads as a refusal, never as a press.</summary>
    ChiefWakePreview WakePreview()
    {
        try
        {
            if (_presses.PreviewWake?.Invoke() is { } preview) return preview;
        }
        catch (Exception ex) { return new ChiefWakePreview(null, null, ex.Message, Labels.ChiefWakeOnceUnreadable(ex.Message)); }
        return new ChiefWakePreview(null, null, Labels.ChiefWakeOnceUnarmed, Labels.ChiefWakeOnceUnarmed);
    }

    void Choose(ChiefSeat seat)
    {
        _chosen = seat;
        foreach (var (s, b) in _choices) Ui.Emphasise(b, s == seat);
    }

    void Said(string words)
    {
        OrgText.Set(_done, words);
        _done.IsVisible = true;
        _refresh();
    }

    /// <summary>
    /// In place: who sits, whose the presses are, and which presses are drawn — a press hidden is disarmed first, and EVERY press is disarmed
    /// when the chief shown changes, since each was armed about the chief it showed.
    /// </summary>
    public void Update(OrgUnitWords root)
    {
        if (root.HeadId != _shown)
        {
            foreach (var press in new[] { Appoint, Replace, Activate, Remove, WakeOnce }) Ui.DisarmConfirm(press);
            _shown = root.HeadId;
        }
        bool board;
        try { board = _presses.BoardActive(); }
        catch (Exception) { board = true; }
        var chief = root.HeadId is { } id ? root.Positions.FirstOrDefault(p => p.Id == id) : null;
        OrgText.Set(_sitting, chief is null ? Labels.ChiefNone : $"{chief.Id}, {chief.Status}. {chief.Seat}");
        OrgText.Set(_whose, board ? Labels.ChiefIsTheBoards : Labels.ChiefYoursUntilBoard);
        Seats();
        Show(_seatRow, !board);
        Show(Appoint, !board && root.HeadId is null);
        Show(Replace, !board && root.HeadId is not null);
        Show(Activate, !board && chief is { Status: OrgPositionStatus.Dormant });
        Show(Remove, root.HeadId is not null);

        // THE WAKE-ONCE PRESS, while an active chief sits — whoever holds the appointment: his word on paid spend is his. A half-press whose
        // preview no longer holds is disarmed here, so the armed sentence on screen is always what press two would do.
        var wakeable = _presses.PreviewWake is not null && chief is { Status: OrgPositionStatus.Active };
        Show(WakeOnce, wakeable);
        Show(_wakeWhat, wakeable);
        if (!wakeable) return;
        var now = WakePreview();
        OrgText.Set(_wakeWhat, $"{Labels.ChiefWakeOnceWhat} {now.After}".TrimEnd());
        if (Ui.IsArmed(WakeOnce) && (_wakeShown is null || _wakeShown.WhatMoved(now) is not null)) Ui.DisarmConfirm(WakeOnce);
    }

    static void Show(Control c, bool visible)
    {
        if (c.IsVisible == visible) return;
        if (!visible && c is Button b) Ui.DisarmConfirm(b);
        c.IsVisible = visible;
    }
}


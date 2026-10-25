using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;

namespace TradeAgent.App;

/// <summary>
/// WHAT THE OWNER'S "FOR YOU" PAGE IS HANDED (<c>U-org-surface-b</c>): the stores it reads, as delegates, and his four in-process
/// presses — a board act's veto preview and veto, a structural veto, an activation — so the page the owner sees and the page a test
/// presses are one control over different sources. Every press is the owner's own, in-process: no pipe op, door verb, <c>trade</c> verb
/// or board act reaches any of them (AUTH-001). A press delegate left null draws no press.
/// </summary>
sealed record OwnerInboxSources
{
    /// <summary>The organisation's ledger, or null while the database is not open.</summary>
    public required Func<OrgStore?> Org { get; init; }

    /// <summary>The wake queue the owner's words are rows of, or null while the database is not open.</summary>
    public required Func<MissionEventStore?> Events { get; init; }

    /// <summary>The security board's one read, or null while the database is not open.</summary>
    public required Func<BoardReader?> Board { get; init; }

    /// <summary>The veto's press one: what vetoing a board act would restore, or why it cannot (reads only).</summary>
    public Func<string, BoardVetoPreview>? PreviewVeto { get; init; }

    /// <summary>
    /// The veto's press two over a board act, handed the preview press one showed: its words, or a refusal thrown with nothing written —
    /// as moved when the veto would now do anything other than that preview said.
    /// </summary>
    public Func<string, BoardVetoPreview, string>? VetoAct { get; init; }

    /// <summary>The owner's veto of one structural change; its words, or a refusal thrown with nothing written.</summary>
    public Func<long, string>? VetoStructure { get; init; }

    /// <summary>The owner's activation of a dormant position; its words, or a refusal thrown with nothing written.</summary>
    public Func<string, string>? Activate { get; init; }

    /// <summary>The clock the page's times are printed against.</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>The sources over the running app: its stores, the gateway's veto, and the host's two in-process writers.</summary>
    public static OwnerInboxSources For(AppHost host) => new()
    {
        Org = () => host.OrgLedger,
        Events = () => host.Db is { } db ? new MissionEventStore(db) : null,
        Board = () => host.Board,
        PreviewVeto = id => host.Gateway.PreviewBoardVeto(id),
        VetoAct = (id, shown) =>
        {
            var result = host.Gateway.VetoBoardAct(id, shown);
            return $"You vetoed the board's act {result.Act.Id}. {result.Words}";
        },
        VetoStructure = host.VetoStructure,
        Activate = host.ActivatePosition,
    };
}

/// <summary>
/// THE OWNER'S "FOR YOU" PAGE (<c>U-org-surface-b</c>; R24 §§ 4.3, 4.4, 4.7; ORCHESTRATION §§ 2.5, 2.9): what reached the root — the
/// escalations nobody below could take, as records — his words still owed a turn and why, the board's proposals of its own power
/// ("applied by nothing"), and his presses: a veto of a board act or of a structural change, each two presses that only ever take
/// away, and — while no board seat is active — the two-press activation of a dormant position that is not the chief.
///
/// <para><b>Claim and measurement apart</b> (INBOX-001): an escalation's reason, a proposal's text, a rationale and his own words are
/// printed in quotation marks after their writer (<see cref="OrgText.Claim"/>); a block reason, a status, a skipped head and a veto's
/// figures are the app's own and never quoted. It raises no wake and holds no answer press: his words go by Chat.</para>
///
/// <para><b>Built once and updated in place</b> (CLAUDE.md): every list is a <see cref="LiveList{T}"/> whose cards are made once per
/// key and refilled; a tick never disarms a half-pressed veto or activation (each press is the two-step Confirm, never the variant that
/// takes one press when its sentence is empty, so both presses are always two) — except a board act's veto whose preview the tick
/// changes: that press was armed about the effect it showed, so it is disarmed and shows the new one (fix pass 1).</para>
/// </summary>
sealed class OwnerInboxPage
{
    readonly OwnerInboxSources _sources;

    readonly TextBlock _unreadable = Ui.With(Ui.Body("", Theme.Caution), t => t.IsVisible = false);

    internal readonly StackPanel EscalationCards = new() { Spacing = Theme.S3 };
    internal readonly StackPanel OwedCards = new() { Spacing = Theme.S3 };
    internal readonly StackPanel PowerCards = new() { Spacing = Theme.S3 };
    internal readonly StackPanel BoardVetoCards = new() { Spacing = Theme.S3 };
    internal readonly StackPanel StructureVetoCards = new() { Spacing = Theme.S3 };
    internal readonly StackPanel ActivateCards = new() { Spacing = Theme.S3 };

    readonly TextBlock _escalationsRefused = Caution();
    readonly TextBlock _structureRefused = Caution();
    readonly TextBlock _boardVetoNote = Ui.With(Ui.Micro(""), t => t.IsVisible = false);
    readonly TextBlock _structureVetoNote = Ui.With(Ui.Micro(""), t => t.IsVisible = false);
    readonly TextBlock _activateNote = Ui.With(Ui.Micro(""), t => t.IsVisible = false);
    readonly TextBlock _activateWhose = Ui.Muted(Labels.ActivateIsYoursUntilTheBoard);

    internal readonly LiveList<EscalationRow> Escalations;
    internal readonly LiveList<OwedOwnerWord> Owed;
    internal readonly LiveList<BoardPowerProposalRow> Power;
    internal readonly LiveList<(BoardActRecord Act, BoardVetoPreview Preview)> BoardVetoes;
    internal readonly LiveList<VetoableEvent> StructureVetoes;
    internal readonly LiveList<OrgPositionRow> Dormant;

    public Control Root { get; }

    public OwnerInboxPage(OwnerInboxSources sources)
    {
        _sources = sources;

        Escalations = new LiveList<EscalationRow>(EscalationCards, Ui.Muted(Labels.ForYouEscalationsNone), e => e.Id, EscalationCard);
        Owed = new LiveList<OwedOwnerWord>(OwedCards, Ui.Muted(Labels.ForYouOwedNone), w => w.Id, OwedCard);
        Power = new LiveList<BoardPowerProposalRow>(PowerCards, Ui.Muted(Labels.ForYouPowerNone), p => p.Id, PowerCard);
        BoardVetoes = new LiveList<(BoardActRecord Act, BoardVetoPreview Preview)>(BoardVetoCards, Ui.Muted(Labels.ForYouBoardVetoesNone),
            a => a.Act.Act.Id, BoardVetoCard);
        StructureVetoes = new LiveList<VetoableEvent>(StructureVetoCards, Ui.Muted(Labels.ForYouStructureVetoesNone),
            e => e.EventId.ToString(System.Globalization.CultureInfo.InvariantCulture), StructureVetoCard);
        Dormant = new LiveList<OrgPositionRow>(ActivateCards, Ui.Muted(Labels.ForYouActivateNone), p => p.Id, DormantCard);

        var left = Ui.Col(Theme.S6,
            _unreadable,
            Ui.Section(Labels.ForYouEscalations, Ui.Col(Theme.S3, Ui.Muted(Labels.ForYouEscalationsAreRecords), _escalationsRefused, EscalationCards)),
            Ui.Section(Labels.ForYouOwed, OwedCards),
            Ui.Section(Labels.ForYouPower, PowerCards));
        var right = Ui.Col(Theme.S6,
            Ui.Section(Labels.ForYouBoardVetoes, Ui.Col(Theme.S3, Ui.Micro(Labels.VetoBoardActWhat), _boardVetoNote, BoardVetoCards)),
            Ui.Section(Labels.ForYouStructureVetoes, Ui.Col(Theme.S3, _structureRefused, _structureVetoNote, StructureVetoCards)),
            Ui.Section(Labels.ForYouActivate, Ui.Col(Theme.S3, _activateWhose, _activateNote, ActivateCards)));
        right.Margin = new Avalonia.Thickness(Theme.S5, 0, 0, 0);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        grid.Children.Add(Pages.Column(0, left));
        grid.Children.Add(Pages.Column(1, right));
        Root = Pages.Scroll(Ui.Col(0, Pages.Header(Labels.NavForYou, Labels.ForYouSubtitle), grid));

        Update();
    }

    static TextBlock Caution() => Ui.With(Ui.Micro(""), t => { t.Foreground = Theme.Caution; t.IsVisible = false; });

    static string When(DateTimeOffset t) => OrgText.When(t);

    static TextBlock Strong(string text) => Ui.With(Ui.Body(text), t => t.FontWeight = FontWeight.SemiBold);

    // ---- the five-second pass ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// THE FIVE-SECOND PASS: every list read afresh and written into the cards it already has — a card is made only for a key it has
    /// never seen, and the panels are re-laid only when the keys or their order change. A read that fails says so in caution colour.
    /// </summary>
    public void Update()
    {
        var failures = new List<string>();
        var org = Try(_sources.Org, failures);
        var events = Try(_sources.Events, failures);
        var board = Try(_sources.Board, failures);

        if (org is not null)
        {
            Read(() =>
            {
                var esc = org.EscalationsToOwner();
                Escalations.Update(esc.Rows);
                Show(_escalationsRefused, esc.Refused);
            }, failures);
            Read(() =>
            {
                var vetoable = org.Vetoable();
                StructureVetoes.Update(_sources.VetoStructure is null ? [] : vetoable.Rows);
                Show(_structureRefused, vetoable.Refused);
            }, failures);
        }
        if (events is not null) Read(() => Owed.Update(events.OwnerWordsOwed()), failures);
        if (board is not null)
        {
            Read(() => Power.Update(board.PowerProposals()), failures);
            Read(() =>
            {
                var preview = _sources.PreviewVeto;
                BoardVetoes.Update(preview is null || _sources.VetoAct is null
                    ? []
                    : [.. board.Acts().Select(a => (a, preview(a.Act.Id))).Where(x => x.Item2.Allowed)]);
            }, failures);
        }

        // THE OWNER'S ACTIVATION IS HIS ONLY WHILE NO BOARD SEAT IS ACTIVE — asked ONLY of BoardReader.AnyActive (the seam ruling of
        // 2026-10-25), and a board that cannot be read answers active: nothing his only until the board is active is offered over a
        // board this build cannot read.
        var boardActive = true;
        if (board is not null)
            try { boardActive = board.AnyActive; }
            catch (Exception ex) { failures.Add(ex.Message); }
        _activateWhose.Text = boardActive ? Labels.ActivateIsTheBoards : Labels.ActivateIsYoursUntilTheBoard;
        ActivateCards.IsVisible = !boardActive;
        if (org is not null && _sources.Activate is not null)
            Read(() =>
            {
                var chief = org.Units().FirstOrDefault(u => u.IsRoot)?.HeadPositionId;
                var open = org.Units().Where(u => u.Status != OrgUnitStatus.Closed).Select(u => u.Id).ToHashSet(StringComparer.Ordinal);
                Dormant.Update([.. org.Positions().Where(p => p.Status == OrgPositionStatus.Dormant && p.Id != chief && open.Contains(p.UnitId))]);
            }, failures);

        _unreadable.IsVisible = failures.Count > 0;
        OrgText.Set(_unreadable, failures.Count > 0 ? Labels.SurfaceUnreadable(string.Join("; ", failures.Distinct())) : "");
    }

    static T? Try<T>(Func<T?> source, List<string> failures) where T : class
    {
        try { return source(); }
        catch (Exception ex) { failures.Add(ex.Message); return null; }
    }

    static void Read(Action read, List<string> failures)
    {
        try { read(); }
        catch (Exception ex) { failures.Add(ex.Message); }
    }

    static void Show(TextBlock line, IReadOnlyList<string> refused)
    {
        line.IsVisible = refused.Count > 0;
        OrgText.Set(line, string.Join(" ", refused));
    }

    // ---- the cards: reads ---------------------------------------------------------------------------------------------------------

    (Control, Action<EscalationRow>) EscalationCard(EscalationRow first)
    {
        var from = Strong("");
        var reason = Ui.Body("");
        var skipped = Ui.Micro("");
        void Fill(EscalationRow e)
        {
            OrgText.Set(from, Labels.EscalationFrom(e.Sender, OrgEnvelopes.NameOf(e.UnitId), e.MandateId, When(e.At)));
            OrgText.Set(reason, OrgText.Claim(e.Sender, e.Reason));
            OrgText.Set(skipped, Labels.EscalationSkipped(e.Skipped));
        }
        return (Ui.Col(Theme.S1, from, reason, skipped), Fill);
    }

    (Control, Action<OwedOwnerWord>) OwedCard(OwedOwnerWord first)
    {
        var to = Strong("");
        var said = Ui.Body("");
        var why = Ui.Micro("");
        void Fill(OwedOwnerWord w)
        {
            OrgText.Set(to, Labels.OwedTo(w.Addressee, When(w.ReceivedAt)));
            OrgText.Set(said, w.Text is { } text ? OrgText.Claim(Labels.ForYouYou, text) : Labels.OwedUnreadable);
            OrgText.Set(why, Labels.OwedWhy(w.Addressee, w.BlockedWhy, w.TakenBy));
            why.Foreground = w.BlockedWhy is null ? Theme.TextFaint : Theme.Caution;
        }
        return (Ui.Col(Theme.S1, to, said, why), Fill);
    }

    (Control, Action<BoardPowerProposalRow>) PowerCard(BoardPowerProposalRow first)
    {
        var from = Strong("");
        var text = Ui.Body("");
        void Fill(BoardPowerProposalRow p)
        {
            OrgText.Set(from, Labels.PowerFrom(p.Seat, p.Target, When(p.At)));
            OrgText.Set(text, OrgText.Claim(p.Seat, p.Text));
        }
        return (Ui.Col(Theme.S1, from, text, Ui.Micro(Labels.PowerAppliedByNothing)), Fill);
    }

    // ---- the cards: the owner's presses ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A BOARD ACT HIS VETO WOULD ACCEPT: the act, its seat's rationale as a claim, and the veto — TWO presses always: the first arms it
    /// and shows each field's value now and what the veto restores (the gateway's own preview, read on the tick); the second vetoes.
    /// <para><b>Press two is about what press one showed</b> (fix pass 1; Codex P2): the card keeps the preview it shows; a tick whose
    /// preview differs from it — the act confirmed, a field moved — disarms the press before showing the new one, and press two hands
    /// the shown preview to the gateway, whose store refuses as moved, nothing written, a veto that would now do anything else.</para>
    /// </summary>
    (Control, Action<(BoardActRecord Act, BoardVetoPreview Preview)>) BoardVetoCard((BoardActRecord Act, BoardVetoPreview Preview) first)
    {
        var id = first.Act.Act.Id;
        var shown = first.Preview;
        var title = Strong("");
        var status = Ui.Micro("");
        var intent = Ui.With(Ui.Mono(""), t => t.TextWrapping = TextWrapping.Wrap);
        var rationale = Ui.Body("");
        var restores = Ui.With(Ui.Body(""), t => t.IsVisible = false);
        var press = Ui.Confirm(Labels.VetoBoardActPress, Labels.VetoBoardActConfirm, () => Pressed(_boardVetoNote, () => _sources.VetoAct!(id, shown)));
        press.HorizontalAlignment = HorizontalAlignment.Left;
        // PRESS ONE SHOWS WHAT RETURNS: the Confirm's own handler arms first, so this one, added after it, sees the armed state.
        press.Click += (_, _) => restores.IsVisible = Ui.IsArmed(press);
        void Fill((BoardActRecord Act, BoardVetoPreview Preview) a)
        {
            if (shown.WhatMoved(a.Preview) is not null) Ui.DisarmConfirm(press);
            shown = a.Preview;
            var row = a.Act.Act;
            OrgText.Set(title, Labels.BoardActTitle(row.Id, row.Kind, row.Seat, When(row.At)));
            OrgText.Set(status, Labels.BoardActStatus(a.Act.Status));
            OrgText.Set(intent, row.Intent);
            OrgText.Set(rationale, OrgText.Claim(row.Seat, row.Rationale));
            OrgText.Set(restores, string.Join("\n", a.Preview.Fields.Select(f => Labels.VetoField(f.Field, f.Now, f.Restored)).Prepend(a.Preview.Words)));
            restores.IsVisible = Ui.IsArmed(press);
        }
        return (Ui.Col(Theme.S1, title, status, intent, rationale, restores, press), Fill);
    }

    /// <summary>A STRUCTURAL CHANGE HIS VETO WOULD ACCEPT: what was decided, by whom, what the veto would do — and two presses.</summary>
    (Control, Action<VetoableEvent>) StructureVetoCard(VetoableEvent first)
    {
        var eventId = first.EventId;
        var title = Strong("");
        var words = Ui.Micro("");
        var press = Ui.Confirm(Labels.VetoStructurePress, Labels.VetoStructureConfirm,
            () => Pressed(_structureVetoNote, () => _sources.VetoStructure!(eventId)));
        press.HorizontalAlignment = HorizontalAlignment.Left;
        void Fill(VetoableEvent e)
        {
            OrgText.Set(title, Labels.StructureEventTitle(e.EventId, e.Kind, OrgEnvelopes.NameOf(e.UnitId), e.PositionId, e.Decider, When(e.At)));
            OrgText.Set(words, e.Words);
        }
        return (Ui.Col(Theme.S1, title, words, press), Fill);
    }

    /// <summary>A DORMANT POSITION, NOT THE CHIEF: his two-press activation, while no board seat is active.</summary>
    (Control, Action<OrgPositionRow>) DormantCard(OrgPositionRow first)
    {
        var positionId = first.Id;
        var title = Strong("");
        var press = Ui.Confirm(Labels.ActivatePositionPress, Labels.ActivatePositionConfirm,
            () => Pressed(_activateNote, () => _sources.Activate!(positionId)));
        press.HorizontalAlignment = HorizontalAlignment.Left;
        void Fill(OrgPositionRow p) => OrgText.Set(title, Labels.DormantPosition(p.Id, OrgEnvelopes.NameOf(p.UnitId)));
        return (Ui.Col(Theme.S1, title, press), Fill);
    }

    /// <summary>
    /// A SECOND PRESS: the act's words on the section's note, or its refusal in the store's own words in caution colour — nothing
    /// written then — and the page read again at once, so what changed shows without waiting for the tick.
    /// </summary>
    void Pressed(TextBlock note, Func<string> act)
    {
        try { Ui.Said(note, act()); }
        catch (Exception ex)
        {
            Ui.Said(note, ex.Message);
            note.Foreground = Theme.Caution;
        }
        Update();
    }
}

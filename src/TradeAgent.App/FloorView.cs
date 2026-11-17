using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;

namespace TradeAgent.App;

/// <summary>
/// WHAT THE FLOOR IS HANDED (<c>U-ui-alive</c>): the ledgers it reads, as delegates, so the page the owner sees and the page a test draws
/// are the same control over different sources. <b>Reads only</b>: nothing here writes, and none of it reaches the agent-facing pipe.
/// </summary>
sealed record FloorSources
{
    /// <summary>The organisation's ledger, or null while the database is not open.</summary>
    public required Func<OrgStore?> Org { get; init; }

    /// <summary>The floor's own reader over the ledgers, or null while the database is not open.</summary>
    public required Func<FloorStore?> Floor { get; init; }

    /// <summary>The security board's seats as they stand.</summary>
    public Func<IReadOnlyList<BoardSeat>> Board { get; init; } = () => [];

    /// <summary>"At a glance", the read every other page shows: the loop's state, today's spend, what waits on whom, the queued wakes.</summary>
    public Func<GlanceRead?> Glance { get; init; } = () => null;

    /// <summary>Whether an AI is running at all: while it is not, the floor offers the same start the Chat page does.</summary>
    public Func<bool> AiRunning { get; init; } = () => false;

    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>The sources over the running app.</summary>
    public static FloorSources For(AppHost host) => new()
    {
        Org = () => host.OrgLedger,
        Floor = () => host.OrgLedger is null ? null : new FloorStore(host.Db),
        Board = () => host.Board.Seats(),
        Glance = () => GlanceRead.From(host, DateTimeOffset.UtcNow),
        AiRunning = () => host.Agent.Running
    };
}

/// <summary>One line of what happened: when, the sentence, whose doing it was and who it reached, and whether it is something to look at.</summary>
sealed record FloorLine(DateTimeOffset At, string Text, string? About, FloorTone Tone)
{
    public string? To { get; init; }
}

/// <summary>
/// WHAT HAPPENED, IN WORDS (<c>U-ui-alive</c>): one sentence for one recorded event, in the names every other page uses. Separated from the
/// controls so a test reads the words. A tool call has no line of its own — there are too many, and each is shown where it happens, under
/// the name of the cell that made it.
/// </summary>
static class FloorWords
{
    /// <summary>Who an event names: a position by its chart name, a board seat by its number, and the app's own parts by theirs.</summary>
    public static string Who(string id, OrgNames names) => id switch
    {
        FloorEvent.Owner => Labels.FloorOwner,
        FloorEvent.App => "TradeAgent",
        FloorEvent.Referee => Labels.FloorReferee,
        FloorEvent.Venue => "The venue",
        "board" => "The security board",
        _ when BoardSeats.IsBoardId(id) => Labels.FloorBoardSeat(id),
        _ => names.Position(id)
    };

    public static FloorLine? Line(FloorEvent e, OrgNames names, string currency)
    {
        string N(string? id) => id is null ? "somebody" : Who(id, names);
        var actor = N(e.Actor);
        (string? Text, FloorTone Tone) said = e.Kind switch
        {
            FloorEventKind.TurnStart => (Labels.FloorTurnStarted(actor), FloorTone.Work),
            FloorEventKind.TurnEnd when e.Detail == "LOST" => (Labels.FloorTurnLost(actor), FloorTone.Caution),
            FloorEventKind.TurnEnd when e.Ok == false => (Labels.FloorTurnFailed(actor, e.Took ?? TimeSpan.Zero), FloorTone.Failure),
            FloorEventKind.TurnEnd => (Labels.FloorTurnEnded(actor, e.Took ?? TimeSpan.Zero, Money(e.Amount, currency)), FloorTone.Work),
            FloorEventKind.Mandate => Mandate(e, names),
            FloorEventKind.Decision => (Labels.FloorDecision(actor, e.Text ?? ""), FloorTone.Work),
            FloorEventKind.Escalation when e.Target is null or FloorEvent.Owner => (Labels.FloorEscalationToOwner(actor, e.Text ?? ""), FloorTone.Caution),
            FloorEventKind.Escalation => (Labels.FloorEscalation(actor, N(e.Target), e.Text ?? ""), FloorTone.Caution),
            FloorEventKind.Lateral => (Labels.FloorLateral(actor, N(e.Target), e.Text ?? ""), FloorTone.Work),
            FloorEventKind.Chart => (Chart(e, names), FloorTone.Work),
            FloorEventKind.Board => (Labels.FloorBoardAct(actor, e.Detail ?? "", e.Text ?? ""), e.Detail is "refused" or "vetoed" or "stale" ? FloorTone.Caution : FloorTone.Work),
            FloorEventKind.Backtest => (Labels.FloorBacktest(actor, e.Count, e.Detail), FloorTone.Work),
            FloorEventKind.VerdictAsked => (Labels.FloorVerdictAsked(actor), FloorTone.Work),
            FloorEventKind.Verdict => (Labels.FloorVerdict(e.Detail ?? "", e.Text), FloorTone.Work),
            FloorEventKind.PaperRun => (Labels.FloorPaperRun(e.Detail ?? ""), FloorTone.Work),
            FloorEventKind.Fill when e is { Amount: { } price, Quantity: { } size } => (Labels.FloorFill(e.Detail ?? "", e.Text ?? "", size, price), FloorTone.Work),
            FloorEventKind.OwnerWords => (Labels.FloorOwnerWords(N(e.Target)), FloorTone.Work),
            _ => (null, FloorTone.Work)
        };
        if (said.Text is null) return null;
        var about = e.Kind is FloorEventKind.Verdict or FloorEventKind.PaperRun or FloorEventKind.Fill ? null
            : e.Actor is FloorEvent.Owner or FloorEvent.App ? e.Target : e.Actor;
        return new FloorLine(e.At, said.Text, about, said.Tone) { To = e.Target };
    }

    static (string?, FloorTone) Mandate(FloorEvent e, OrgNames names)
    {
        string N(string? id) => id is null ? "somebody" : Who(id, names);
        var holder = N(e.Ref);
        return e.Detail switch
        {
            MandateEventKind.Issued => (Labels.FloorMandateIssued(N(e.Actor), N(e.Target), false, e.Text ?? ""), FloorTone.Work),
            MandateEventKind.Delivered => (Labels.FloorMandateDelivered(N(e.Actor), N(e.Target), e.Text), FloorTone.Work),
            MandateEventKind.Accepted => (Labels.FloorMandateAccepted(N(e.Actor), N(e.Target)), FloorTone.Work),
            MandateEventKind.Rejected => (Labels.FloorMandateRejected(N(e.Actor), N(e.Target), e.Text), FloorTone.Caution),
            MandateEventKind.Retry => (Labels.FloorMandateRetry(holder), FloorTone.Work),
            MandateEventKind.Declined => (Labels.FloorMandateDeclined(holder, e.Text), FloorTone.Caution),
            MandateEventKind.Cancelled => (Labels.FloorMandateCancelled(holder), FloorTone.Caution),
            MandateEventKind.Expired => (Labels.FloorMandateExpired(holder), FloorTone.Caution),
            MandateEventKind.Rescoped => (Labels.FloorMandateRescoped(holder), FloorTone.Work),
            MandateEventKind.Proposed => (Labels.FloorMandateProposed(holder, e.Text), FloorTone.Work),
            // An escalated mandate is said once, by the escalation's own row.
            _ => (null, FloorTone.Work)
        };
    }

    static string? Chart(FloorEvent e, OrgNames names)
    {
        var by = e.Actor is "owner" or "app" or "board" ? Labels.FloorDecider(e.Actor) : Who(e.Actor, names);
        var unit = e.Unit is { } u ? names.Unit(u) : "the chart";
        var position = e.Target is { } p ? names.Position(p) : "a position";
        return e.Detail switch
        {
            OrgEventKind.Chartered => Labels.FloorChartered(by, unit),
            OrgEventKind.Hired => Labels.FloorHired(by, unit),
            OrgEventKind.Activated => Labels.FloorActivated(by, position),
            OrgEventKind.Retired => Labels.FloorRetired(by, position),
            OrgEventKind.Closed => Labels.FloorClosed(by, unit),
            OrgEventKind.ChiefAppointed => Labels.FloorChiefAppointed(by),
            OrgEventKind.ChiefReplaced => Labels.FloorChiefReplaced(by),
            OrgEventKind.ChiefVacated => Labels.FloorChiefVacated(by),
            // The chart's housekeeping — a seat seeded, a fence moved, a review noted — is on the Organisation page; it is not a thing that happened on the floor.
            _ => null
        };
    }

    /// <summary>A cost with its currency, or null where nothing priced the turn: unknown is left unsaid, never written as zero.</summary>
    public static string? Money(decimal? amount, string currency) =>
        amount is { } a ? (a.ToString("0.00", CultureInfo.InvariantCulture) + " " + currency).TrimEnd() : null;
}

/// <summary>
/// THE FLOOR (<c>U-ui-alive</c>): the organisation drawn as a place, lit where it is working. Every unit is a slab and every seat a cell
/// on it; a cell is bright while its turn is open; what one seat gives another crosses the floor between them; and beside it, in words,
/// what just happened. It is what the app opens on.
///
/// <para><b>It only reads, and it shows nothing that is not a row.</b> Who is working is the launch ledger's open turns; what crosses the
/// floor is a mandate, a delivery, an escalation as its ledger recorded it; the words are those rows said in the names the other pages
/// use. When the AI is not running the floor is dark, and says so. The one press on it is the Chat page's own "Start the AI".</para>
///
/// <para><b>Built once, updated in place</b> like every page. The reads run off the UI thread and are handed back to it.</para>
/// </summary>
sealed partial class FloorPage
{
    /// <summary>How far behind now the floor reads its events: a row whose transaction is still closing is not missed, only seen a moment later.</summary>
    static readonly TimeSpan Lag = TimeSpan.FromSeconds(1.5);

    /// <summary>How often the floor looks for something new while it is on screen.</summary>
    static readonly TimeSpan Look = TimeSpan.FromSeconds(1.5);

    const int FeedLines = 9, Remembered = 240;
    const double FeedWidth = 318;

    public Control Root { get; }

    readonly FloorSources _sources;
    readonly FloorStage _stage = new() { OwnerWord = Labels.FloorOwner };
    readonly DispatcherTimer _look;
    readonly DispatcherTimer _clock;

    // the words over the stage
    readonly TextBlock _state = Ui.Muted("");
    readonly TextBlock _working = Ui.Body("");
    readonly TextBlock _next = Ui.Muted("");
    readonly TextBlock _problem = Ui.Body("", Theme.Caution);
    readonly Control _start;
    readonly StackPanel _feed = new() { Spacing = Theme.S3 };
    readonly TextBlock _feedTitle = Ui.Eyebrow(Labels.FloorFeed);
    readonly Button _feedAll;
    readonly TextBlock _feedEmpty = Ui.Muted(Labels.FloorNothingYet);
    readonly Canvas _captions = new() { IsHitTestVisible = false };
    readonly TextBlock _turns = Figure(), _open = Figure(), _judge = Figure();
    readonly TextBlock _spend = Ui.Body("");
    readonly TextBlock _spendLabel = Ui.Micro(Labels.FloorSpendToday);
    readonly Border _feedColumn;
    readonly Control _figures;

    // what the last read said
    OrgNames _names = OrgNames.None;
    string _chartSignature = "\0";
    string _currency = "";
    DateTimeOffset? _seen;
    bool _reading, _shown, _laid;
    readonly Dictionary<string, DateTimeOffset> _openSince = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _lastTool = new(StringComparer.Ordinal);
    readonly List<FloorLine> _lines = [];
    readonly Dictionary<string, Caption> _captionOf = new(StringComparer.Ordinal);
    string? _chosen, _pointed;
    /// <summary>The AI is stopped or paused: no turn opens, so an active seat's dim light means only that it has a place.</summary>
    bool _asleep;
    FloorFrame? _frame;

    sealed class Caption
    {
        public required StackPanel Panel;
        public required TextBlock Name, Clock, Line;
    }

    public FloorPage(FloorSources sources, Func<Task> startOrStop, Action<MainWindow.Page> go)
    {
        _sources = sources;
        _feedAll = Ui.With(Ui.Ghost(Labels.FloorFeedAll, () => { _chosen = null; Mark(); Feed(); }), b =>
        {
            b.FontSize = Theme.Small;
            b.Padding = new Thickness(Theme.S2, 5);
            b.HorizontalAlignment = HorizontalAlignment.Left;
            b.Margin = new Thickness(44 - Theme.S2, 0, 0, 0);
        });

        // The Chat page's own start, here too: while no AI is running, the app still opens on an answer to "so how do I start?".
        _start = Ui.With(Ui.Primary(Labels.FloorStart, startOrStop), b => b.HorizontalAlignment = HorizontalAlignment.Left);
        _start.IsVisible = false;
        _problem.IsVisible = false;
        _next.FontSize = Theme.Small;

        var title = Ui.Col(Theme.S1,
            Ui.Display(Labels.NavFloor),
            Ui.With(_state, t => t.MaxWidth = 460),
            Ui.With(_working, t => { t.MaxWidth = 460; t.Margin = new Thickness(0, Theme.S2, 0, 0); }),
            Ui.With(_next, t => t.MaxWidth = 460),
            Ui.With(_problem, t => t.MaxWidth = 460),
            Ui.With(_start, c => c.Margin = new Thickness(0, Theme.S3, 0, 0)),
            Ui.With(BuildReplay(), c => c.Margin = new Thickness(-Theme.S2, Theme.S2, 0, 0)));
        title.HorizontalAlignment = HorizontalAlignment.Left;
        title.VerticalAlignment = VerticalAlignment.Top;
        title.Margin = new Thickness(Theme.S8, Theme.S6, 0, 0);

        // WHAT JUST HAPPENED, down the right: sentences on the dark of the stage, no card round them, each fainter than the one above it.
        _feedColumn = new Border
        {
            Width = FeedWidth, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(Theme.S4, Theme.S6, Theme.S5, Theme.S6),
            Background = Theme.FloorScrim,
            Child = Ui.Col(Theme.S4, _feedTitle, _feedEmpty, _feed, _feedAll)
        };
        _feedAll.IsVisible = false;

        _figures = Ui.Row(Theme.S8,
            Stat(_turns, Labels.FloorTurnsToday),
            Stat(_open, Labels.FloorOpenWork),
            Stat(_judge, Labels.FloorToJudge),
            Ui.With(Ui.Col(2, Ui.With(_spend, t => { t.FontFeatures = Theme.Tabular; t.MaxWidth = 300; }), _spendLabel),
                c => c.VerticalAlignment = VerticalAlignment.Bottom));
        _figures.Name = "floor-figures";
        _figures.HorizontalAlignment = HorizontalAlignment.Left;
        _figures.VerticalAlignment = VerticalAlignment.Bottom;
        _figures.Margin = new Thickness(Theme.S8, 0, 0, Theme.S6);

        Root = new Grid { Children = { _stage, _captions, _feedColumn, title, _figures }, ClipToBounds = true };

        // THE FLOOR STANDS BETWEEN THE TITLE AND THE FIGURES, and runs a little way under the sentences on the right, where they are darkest.
        _stage.Clear = size =>
        {
            var top = title.Bounds.Height > 0 ? title.Bounds.Bottom + 6 : 150;
            var bottom = _figures.IsVisible && _figures.Bounds.Height > 0 ? _figures.Bounds.Top - 14 : size.Height - 28;
            return (28, (float)top, (float)(size.Width - (_feedColumn.IsVisible ? FeedWidth - 14 : 28)), (float)bottom);
        };
        _stage.Framed += frame => Safely(() => Place(frame));
        _stage.Failed += why =>
        {
            _problem.Text = Labels.FloorNotDrawn + " " + why;
            _problem.IsVisible = true;
        };
        _stage.Pointed += id => { _pointed = id; Mark(); };
        _stage.Chosen += id => { _chosen = id == _chosen ? null : id; Mark(); Feed(); };
        Root.PropertyChanged += (_, e) =>
        {
            if (e.Property != Visual.BoundsProperty) return;
            // On a narrow window the stage keeps the room: the sentences go, the floor stays.
            _feedColumn.IsVisible = Root.Bounds.Width >= 860;
            _figures.IsVisible = Root.Bounds.Height >= 520;
        };

        _look = new DispatcherTimer(Look, DispatcherPriority.Background, (_, _) => Read());
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Safely(Tick));
    }

    /// <summary>
    /// A timer's or a frame's work, with whatever it throws said on the page and never let out: a handler nobody awaits that throws ends
    /// the program, and this page is a picture — it does not get to take the owner's controls down with it.
    /// </summary>
    void Safely(Action work)
    {
        try { work(); }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            _problem.Text = Labels.FloorNotRead + " " + (ex is TradeAgentException t ? t.Info.UserMessage : ex.Message);
            _problem.IsVisible = true;
        }
    }

    static TextBlock Figure() => new()
    {
        FontSize = Theme.H1, LineHeight = Theme.H1Line, FontWeight = FontWeight.SemiBold, FontFamily = Theme.SansDisplay,
        Foreground = Theme.Text, FontFeatures = Theme.Tabular, LetterSpacing = Theme.H1Tracking, Text = "–"
    };

    static Control Stat(TextBlock figure, string label) => Ui.Col(2, figure, Ui.Micro(label));

    /// <summary>The stage, for a host that steps the floor itself (the render rows).</summary>
    internal FloorStage Stage => _stage;

    /// <summary>
    /// Set by a host that drives the page itself — the render rows: no timer runs and no read leaves the calling thread, so what the page
    /// shows is exactly what the host last asked it to read (<see cref="ReadNow"/>) and stepped.
    /// </summary>
    internal bool Manual { get; init; }

    /// <summary>One read of the ledgers and its showing, on the calling thread.</summary>
    internal void ReadNow() => Apply(Take(_seen));

    /// <summary>The page is on screen or it is not. Off screen it reads nothing and draws nothing.</summary>
    public void Shown(bool shown)
    {
        if (_shown == shown) return;
        _shown = shown;
        _stage.Show(shown);
        if (Manual) return;
        if (shown)
        {
            _look.Start();
            _clock.Start();
            Read();
        }
        else
        {
            // Left for another page, a replay is over: coming back, the floor is the present's.
            StopReplay();
            _look.Stop();
            _clock.Stop();
        }
    }

    /// <summary>The shell's tick, while the page is shown: the figures the status carries, and a look at the ledgers now rather than at the next beat.</summary>
    public void Update(GatewayStatus status)
    {
        _turns.Text = status.AiTurnsToday.ToString(CultureInfo.InvariantCulture);
        Read();
    }

    // ---- the read, off the UI thread --------------------------------------------------------------------------------------------

    sealed record Reading(FloorChart Chart, OrgNames Names, IReadOnlyList<FloorTurn> Open, IReadOnlyList<FloorEvent> Events, bool Backfill,
        GlanceRead? Glance, bool Running, DateTimeOffset Now, DateTimeOffset Until, string? Problem);

    void Read()
    {
        if (_reading || !_shown || Manual || _replay is not null) return;
        _reading = true;
        var since = _seen;
        _ = Task.Run(() =>
        {
            Reading? reading = null;
            try { reading = Take(since); }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
            {
                reading = new Reading(FloorChart.Empty, OrgNames.None, [], [], false, null, false, _sources.Now(), since ?? _sources.Now(),
                    ex is TradeAgentException t ? t.Info.UserMessage : ex.Message);
            }
            Dispatcher.UIThread.Post(() =>
            {
                _reading = false;
                if (reading is not null) Safely(() => Apply(reading));
            });
        });
    }

    Reading Take(DateTimeOffset? since)
    {
        var now = _sources.Now();
        var until = now - Lag;
        var org = _sources.Org();
        var floor = _sources.Floor();
        if (org is null || floor is null)
            return new Reading(FloorChart.Empty, OrgNames.None, [], [], false, null, _sources.AiRunning(), now, until, null);

        var units = org.Units();
        var positions = org.Positions();
        var names = OrgNames.Of(units, positions);
        var chart = FloorChart.From(units, positions, names, _sources.Board());
        // OPEN NOW, by the rule "At a glance" uses: launched in the last day and not ended.
        var open = floor.Turns(now, now).Where(t => t.EndedAt is null && t.StartedAt >= now.AddDays(-1)).ToList();
        // The first look fills the sentences from the last day, with nothing moving; every later one takes only what is new.
        var events = since is { } from ? floor.Events(from, until) : floor.Events(until.AddDays(-1), until, 400, tools: false);
        return new Reading(chart, names, open, events, since is null, _sources.Glance(), _sources.AiRunning(), now, until, null);
    }

    // ---- what the read said, on the UI thread -----------------------------------------------------------------------------------

    void Apply(Reading r)
    {
        // A read that was on its way when a replay began is not shown over it.
        if (_replay is not null) return;
        _problem.IsVisible = r.Problem is not null;
        if (r.Problem is { } problem)
        {
            _problem.Text = Labels.FloorNotRead + " " + problem;
            return;
        }

        _names = r.Names;
        _currency = r.Glance?.Spend?.Currency ?? _currency;
        if (r.Chart.Signature != _chartSignature)
        {
            _chartSignature = r.Chart.Signature;
            // The first floor stands at once; a change to it afterwards grows in where it stands.
            _stage.Scene.Lay(new FloorPlan(r.Chart), atOnce: !_laid);
            _laid = true;
        }

        var loop = r.Glance?.Loop ?? "";
        var asleep = _asleep = loop.StartsWith("stopped", StringComparison.Ordinal) || loop.StartsWith("paused", StringComparison.Ordinal);
        _openSince.Clear();
        foreach (var turn in r.Open) _openSince[turn.Role] = turn.StartedAt;
        foreach (var gone in _lastTool.Keys.Where(k => !_openSince.ContainsKey(k)).ToList()) _lastTool.Remove(gone);
        _stage.Scene.Lit(_openSince.Keys.ToHashSet(StringComparer.Ordinal), running: !asleep || _openSince.Count > 0);

        // A seat whose task is past its deadline carries a small amber light: the one thing on the floor that asks to be looked at.
        var late = r.Glance?.Mandates?.Rows.Where(o => o.Status != MandateEventKind.Delivered && r.Now > o.Mandate.Deadline)
            .Select(o => o.Mandate.Holder).ToHashSet(StringComparer.Ordinal) ?? [];
        _stage.Scene.Flag(late);

        foreach (var e in r.Events) Happened(e, moving: !r.Backfill);
        _seen = r.Until;

        Say(r, loop);
        Feed();
        _stage.Wake();
    }

    /// <summary>One recorded event: its sentence is kept, and — unless it is only history being filled in — it is shown happening.</summary>
    internal void Happened(FloorEvent e, bool moving)
    {
        if (e.Kind == FloorEventKind.Tool)
        {
            _lastTool[e.Actor] = Labels.FloorTool(e.Detail ?? "", e.Text, e.Ok != false);
            if (moving) _stage.Scene.Spark(e.Actor, e.Ok == false ? FloorTone.Caution : FloorTone.Work);
            return;
        }
        if (FloorWords.Line(e, _names, _currency) is { } line)
        {
            _lines.Add(line);
            if (_lines.Count > Remembered) _lines.RemoveRange(0, _lines.Count - Remembered);
        }
        if (moving) Show(e);
    }

    /// <summary>How one event looks on the floor. Nothing here decides what happened; it only decides what the row looks like.</summary>
    void Show(FloorEvent e)
    {
        var scene = _stage.Scene;
        switch (e.Kind)
        {
            case FloorEventKind.TurnStart:
                scene.Flash(e.Actor);
                break;
            case FloorEventKind.TurnEnd:
                scene.Ring(e.Actor, e.Detail == "LOST" ? FloorTone.Caution : e.Ok == false ? FloorTone.Failure : FloorTone.Work, 1.1f);
                break;
            case FloorEventKind.Mandate when e.Target is { } to:
                switch (e.Detail)
                {
                    case MandateEventKind.Issued:
                    case MandateEventKind.Delivered:
                    case MandateEventKind.Rescoped:
                    case MandateEventKind.Proposed:
                        scene.Send(e.Actor, to, FloorTone.Work, e.Detail == MandateEventKind.Issued ? 1.15f : 1, () => scene.Flash(to));
                        break;
                    case MandateEventKind.Accepted:
                        scene.Send(e.Actor, to, FloorTone.Work, 0.85f, () => scene.Ring(to));
                        break;
                    case MandateEventKind.Rejected:
                    case MandateEventKind.Declined:
                        scene.Send(e.Actor, to, FloorTone.Caution, 0.9f, () => scene.Ring(to, FloorTone.Caution));
                        break;
                    case MandateEventKind.Retry:
                        if (e.Ref is { } again) scene.Flash(again, FloorTone.Work, 0.7f);
                        break;
                    case MandateEventKind.Cancelled:
                    case MandateEventKind.Expired:
                        if (e.Ref is { } held) scene.Ring(held, FloorTone.Caution);
                        break;
                }
                break;
            case FloorEventKind.Decision:
                scene.Ring(e.Actor, FloorTone.Work, 1.25f);
                break;
            case FloorEventKind.Escalation:
                var up = e.Target ?? FloorEvent.Owner;
                if (up == FloorEvent.Owner) scene.Send(e.Actor, FloorScene.OwnerId, FloorTone.Caution, 1.1f, scene.OwnerLit);
                else scene.Send(e.Actor, up, FloorTone.Caution, 1.1f, () => scene.Flash(up, FloorTone.Caution));
                break;
            case FloorEventKind.Lateral when e.Target is { } peer:
                scene.Send(e.Actor, peer, FloorTone.Work, 0.8f, () => scene.Flash(peer, FloorTone.Work, 0.6f));
                break;
            case FloorEventKind.Chart:
                if (e.Unit is { } unit) scene.Rim(unit);
                if (e.Target is { } seat)
                {
                    if (e.Actor == FloorEvent.Owner) scene.Send(FloorScene.OwnerId, seat, FloorTone.Work, 1, () => scene.Flash(seat));
                    else if (scene.Plan.ById.ContainsKey(e.Actor)) scene.Send(e.Actor, seat, FloorTone.Work, 1, () => scene.Flash(seat));
                    else scene.Flash(seat);
                }
                break;
            case FloorEventKind.Board:
                scene.Ring(e.Actor, e.Detail is "refused" or "vetoed" ? FloorTone.Caution : FloorTone.Work);
                // What the board decides binds the whole organisation: its slab shows its edge when an act takes effect.
                if (e.Detail is "applied" or "confirmed") scene.Rim(OrgIds.Root);
                break;
            case FloorEventKind.Backtest:
            case FloorEventKind.VerdictAsked:
                scene.Ring(e.Actor, FloorTone.Work, 0.9f);
                break;
            case FloorEventKind.OwnerWords when e.Target is { } told:
                scene.OwnerLit();
                scene.Send(FloorScene.OwnerId, told, FloorTone.Work, 1, () => scene.Flash(told));
                break;
        }
    }

    void Say(Reading r, string loop)
    {
        _state.Text = r.Chart.Units.Count == 0 ? Labels.FloorEmpty : loop.Length == 0 ? "" : char.ToUpperInvariant(loop[0]) + loop[1..] + ".";
        _working.Text = Labels.FloorWorking([.. _openSince.OrderBy(o => o.Value).Select(o => FloorWords.Who(o.Key, _names))]);
        _working.IsVisible = r.Chart.Units.Count > 0;

        // THE NEXT TURN: the soonest queued wake of an active position — said only while the AI runs, since a stopped one serves none.
        var next = r.Glance is { Wakes: { } wakes, Active: { } active }
            ? active.Where(wakes.ContainsKey).OrderBy(p => wakes[p]).Select(p => (Position: p, At: wakes[p])).FirstOrDefault()
            : default;
        _next.IsVisible = next.Position is not null && !_asleep && _openSince.Count == 0;
        if (next.Position is not null) _next.Text = Labels.FloorNext(_names.Position(next.Position), next.At - r.Now);

        _start.IsVisible = !r.Running;

        var lines = r.Glance is { } glance ? GlanceWords.Of(glance) : [];
        var spend = lines.FirstOrDefault(l => l.Key == GlanceWords.Spend);
        _spend.Text = spend?.Line ?? Labels.FloorUnknown;
        _spend.Foreground = spend is { Caution: true } ? Theme.Caution : Theme.Text;
        _open.Text = r.Glance?.Mandates is { } m ? m.Rows.Count.ToString(CultureInfo.InvariantCulture) : "–";
        _judge.Text = r.Glance?.Deliveries is { } d ? d.Rows.Count.ToString(CultureInfo.InvariantCulture) : "–";
    }

    // ---- the sentences down the right -------------------------------------------------------------------------------------------

    string _feedSignature = "";

    void Feed()
    {
        // A cell was chosen: the sentences are its own — what it did and what reached it — until "everything" is asked for again.
        var chosen = _chosen is { } id && _stage.Scene.Plan.ById.TryGetValue(id, out var cell) ? cell : null;
        var newest = _lines.AsEnumerable().Reverse().Where(l => chosen is null || l.About == chosen.Id || l.To == chosen.Id).Take(FeedLines).ToList();
        _feedTitle.Text = (chosen is null ? Labels.FloorFeed : chosen.Name).ToUpperInvariant();
        _feedAll.IsVisible = chosen is not null;
        _feedEmpty.Text = chosen is null ? Labels.FloorNothingYet : Labels.FloorNothingOfIts;
        var signature = chosen?.Id + string.Join('\n', newest.Select(l => l.At.Ticks + l.Text));
        if (signature == _feedSignature) return;
        var arrived = _feedSignature.Length > 0 && newest.Count > 0 && !_feedSignature.Contains(newest[0].At.Ticks + newest[0].Text, StringComparison.Ordinal);
        _feedSignature = signature;
        _feedEmpty.IsVisible = newest.Count == 0;

        _feed.Children.Clear();
        for (var i = 0; i < newest.Count; i++)
        {
            var line = newest[i];
            var time = new TextBlock
            {
                Text = line.At.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture), FontSize = Theme.Micro,
                Foreground = Theme.TextFaint, FontFeatures = Theme.Tabular, Margin = new Thickness(0, 3, 0, 0)
            };
            var text = new TextBlock
            {
                Text = line.Text, FontSize = Theme.Small, LineHeight = 18, TextWrapping = TextWrapping.Wrap, MaxLines = 4,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = line.Tone switch { FloorTone.Caution => Theme.Caution, FloorTone.Failure => Theme.Danger, _ => Theme.Text },
                [Grid.ColumnProperty] = 1
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("44,*"), Children = { time, text } };
            // Each fainter than the one above it: the newest is what is being said, the rest is what was.
            row.Opacity = Math.Max(0.28, 1 - i * 0.095);
            if (line.About is { } about)
            {
                row.Background = Brushes.Transparent;
                row.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
                row.PointerPressed += (_, _) => { _chosen = about; Mark(); Feed(); };
            }
            if (i == 0 && arrived) ComeIn(row);
            _feed.Children.Add(row);
        }
    }

    static void ComeIn(Control row)
    {
        var settled = row.Opacity;
        row.Opacity = 0;
        row.RenderTransform = new TranslateTransform(0, -8);
        row.Transitions =
        [
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(380) },
            new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(380) }
        ];
        Dispatcher.UIThread.Post(() =>
        {
            row.Opacity = settled;
            row.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("translateY(0px)");
        }, DispatcherPriority.Background);
    }

    // ---- the captions on the stage ----------------------------------------------------------------------------------------------

    void Mark()
    {
        _stage.Scene.Marked = _pointed ?? _chosen;
        _stage.Wake();
    }

    void Tick()
    {
        // The clocks under the working cells run by the second; nothing else on the page needs the second.
        if (_openSince.Count > 0 && _frame is { } frame) Place(frame);
    }

    /// <summary>
    /// A frame was made: each caption goes beside its cell, where the cell is this instant. A working cell is named and timed; the cell
    /// being pointed at or chosen is named and says what it is. A caption stands hard against its own cell — to its right if it can, else
    /// wherever round it nothing else is — so no line is needed to say whose it is, and it never lies over another cell that is lit.
    /// </summary>
    void Place(FloorFrame frame)
    {
        _frame = frame;
        var now = Clock();
        var marked = _pointed ?? _chosen;
        var wanted = new List<string>(_openSince.OrderBy(o => o.Value).Select(o => o.Key));
        if (marked is not null && !wanted.Contains(marked)) wanted.Insert(0, marked);

        foreach (var (id, caption) in _captionOf)
            if (!wanted.Contains(id)) caption.Panel.IsVisible = false;

        // What a caption must not cover: every cell that is lit or marked, as the box it is drawn in.
        var cells = new Dictionary<string, Rect>(StringComparer.Ordinal);
        foreach (var cell in frame.Cells)
        {
            if (!wanted.Contains(cell.Id)) continue;
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (var p in cell.Foot.Concat(cell.Top))
            {
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
            cells[cell.Id] = new Rect(minX, minY, maxX - minX, maxY - minY).Inflate(9);
        }

        var stage = new Rect(_stage.Bounds.Size).Deflate(8);
        var placed = new List<Rect>();
        foreach (var id in wanted)
        {
            if (!cells.TryGetValue(id, out var box)) continue;
            if (!_captionOf.TryGetValue(id, out var caption))
            {
                var name = new TextBlock { FontSize = Theme.Small, FontWeight = FontWeight.SemiBold, Foreground = Theme.Text };
                var clock = new TextBlock { FontSize = Theme.Small, Foreground = Theme.TextMuted, FontFeatures = Theme.Tabular };
                var line = new TextBlock
                {
                    FontSize = Theme.Micro, Foreground = Theme.TextMuted, FontFeatures = Theme.Tabular, MaxWidth = 230,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                caption = new Caption
                {
                    Name = name, Clock = clock, Line = line,
                    Panel = new StackPanel
                    {
                        Spacing = 0, Children = { Ui.Row(Theme.S2, name, clock), line },
                        // Read over whatever light is behind it: the window's own dark, soft, round every letter.
                        Effect = new DropShadowEffect { BlurRadius = 8, Color = Color.Parse("#0B0D12"), Opacity = 1, OffsetX = 0, OffsetY = 0 }
                    }
                };
                _captionOf[id] = caption;
                _captions.Children.Add(caption.Panel);
            }

            var planned = _stage.Scene.Plan.ById.GetValueOrDefault(id);
            var working = _openSince.TryGetValue(id, out var since);
            // Beside a cell, its short name: the unit's is on the slab under it. Pointed at or chosen, its whole name.
            caption.Name.Text = planned is null ? id : id == marked ? planned.Name : planned.Short;
            caption.Clock.Text = working ? Labels.FloorClock(now - since) : "";
            caption.Clock.IsVisible = working;
            var says = Says(id, planned, working);
            caption.Line.Text = says;
            caption.Line.IsVisible = says.Length > 0;
            caption.Panel.IsVisible = true;
            caption.Panel.Measure(Size.Infinity);
            var size = caption.Panel.DesiredSize;

            // Round its cell, in the order a reader would look: right, left, above to the right, above to the left, below.
            Rect[] tries =
            [
                new(box.Right + 4, box.Center.Y - size.Height / 2, size.Width, size.Height),
                new(box.Left - 4 - size.Width, box.Center.Y - size.Height / 2, size.Width, size.Height),
                new(box.Center.X + 6, box.Top - size.Height - 2, size.Width, size.Height),
                new(box.Center.X - 6 - size.Width, box.Top - size.Height - 2, size.Width, size.Height),
                new(box.Center.X - size.Width / 2, box.Bottom + 2, size.Width, size.Height)
            ];
            var at = tries[0];
            foreach (var candidate in tries)
            {
                if (!stage.Contains(candidate)) continue;
                if (placed.Any(p => p.Intersects(candidate.Inflate(3)))) continue;
                if (cells.Any(c => c.Key != id && c.Value.Intersects(candidate))) continue;
                at = candidate;
                break;
            }
            placed.Add(at);
            Canvas.SetLeft(caption.Panel, Math.Round(at.X));
            Canvas.SetTop(caption.Panel, Math.Round(at.Y));
        }
    }

    /// <summary>What a cell's caption says under its name: for a working cell its last tool call, if it has made one; for any other, what it is.</summary>
    string Says(string id, FloorPlan.Cell? cell, bool working)
    {
        if (working) return _lastTool.GetValueOrDefault(id, "");
        return cell?.Kind switch
        {
            FloorCellKind.Vacant => Labels.FloorVacantLine,
            FloorCellKind.Board when cell.Active => Labels.FloorBoardSeatLine,
            _ when cell is { Active: false } => Labels.FloorDormantLine,
            _ when _asleep => Labels.FloorAsleepLine,
            _ => Labels.FloorIdleLine
        };
    }
}

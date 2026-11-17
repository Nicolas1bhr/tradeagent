using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;

namespace TradeAgent.App;

/// <summary>The minutes of one recorded series as the floor's tape shows them: whose, and each minute's opening time and close, oldest first.</summary>
sealed record FloorTapeRead(string Symbol, IReadOnlyList<(DateTimeOffset Open, decimal Close)> Bars);

/// <summary>
/// THE TAPE'S READER (<c>U-ui-alive</c>): the last minutes of the series this installation recorded most lately — the collector's own rows,
/// read in process as the paper runner reads them. Which series that is is looked up once and kept for ten minutes: finding it counts
/// every series' rows, and a floor that did that on every look would be paying for a question whose answer does not change.
/// One reader at a time: the page's reads never overlap.
/// </summary>
sealed class FloorTapeReader(Func<ForwardBarStore?> store)
{
    string? _symbol;
    DateTimeOffset _chosen;

    public FloorTapeRead? Read(DateTimeOffset until, TimeSpan stretch)
    {
        if (store() is not { } bars) return null;
        var now = DateTimeOffset.UtcNow;
        if (_symbol is null || now - _chosen > TimeSpan.FromMinutes(10))
        {
            _symbol = bars.All().Where(s => s.Bars > 0).OrderByDescending(s => s.LastBar).FirstOrDefault()?.Symbol;
            _chosen = now;
        }
        if (_symbol is null) return null;
        var rows = bars.Since(_symbol, until - stretch, 2000).Where(b => b.OpenTime <= until).ToList();
        return rows.Count < 2 ? null : new FloorTapeRead(_symbol, [.. rows.Select(b => (b.OpenTime, b.Close))]);
    }
}

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

    /// <summary>
    /// The tape: the minutes recorded in the stretch ending at a moment, of the series recorded most lately — or null where none is
    /// recorded. The market's own prices as this installation collected them; nothing of the AI's.
    /// </summary>
    public Func<DateTimeOffset, TimeSpan, FloorTapeRead?> Tape { get; init; } = (_, _) => null;

    /// <summary>Whether an AI is running at all: while it is not, the floor offers the same start the Chat page does.</summary>
    public Func<bool> AiRunning { get; init; } = () => false;

    /// <summary>
    /// WHETHER TURNS ARE BEING OPENED NOW: the loop is working or waiting for its next turn, and nothing is holding it — not the
    /// vendor's limit, not a full disk, not the wait after a start that did not close properly. Read off the loop itself, never off
    /// the sentence that describes it: "waiting until 14:00 for your plan's limit" is waiting, and nothing will open.
    /// </summary>
    public Func<bool> TurnsOpen { get; init; } = () => false;

    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// Today's spend by unit and by who paid — the Costs page's own read (<see cref="AiAttemptStore.UnitTotalsByClass"/> over the
    /// owner's day) — or null where the launch ledger is not open. For the floor's money lens; read only while that lens is on.
    /// </summary>
    public Func<DateTimeOffset, IReadOnlyList<UnitClassTotals>?> Costs { get; init; } = _ => null;

    /// <summary>The sources over the running app.</summary>
    public static FloorSources For(AppHost host)
    {
        var tape = new FloorTapeReader(() => host.OrgLedger is null ? null : new ForwardBarStore(host.Db));
        return Over(host) with { Tape = tape.Read };
    }

    static FloorSources Over(AppHost host) => new()
    {
        Org = () => host.OrgLedger,
        Floor = () => host.OrgLedger is null ? null : new FloorStore(host.Db),
        Board = () => host.Board.Seats(),
        Glance = () => GlanceRead.From(host, DateTimeOffset.UtcNow),
        AiRunning = () => host.Agent.Running,
        Costs = now => host.AttemptLedger is { } ledger && OwnerDay.Window(now) is var (from, to) ? ledger.UnitTotalsByClass(from, to) : null,
        TurnsOpen = () => host.Mission.Status is { State: MissionState.Working or MissionState.Waiting, Held: null }
                          && host.DiskPauseWords is null && host.CrashWaitWords is null
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

    /// <summary>
    /// The floor's one large line and the sentence under it, from the loop's own words: what stands before its dash, capitalised, and
    /// what stands after it, as a sentence. Words with no dash, or too long to be set large, are said whole under the floor's own name.
    /// Nothing is added and nothing is left out: the two lines together are the sentence the Dashboard shows.
    /// </summary>
    public static (string Headline, string State) Headline(string loop)
    {
        static string Sentence(string words) => words.Length == 0 ? "" : char.ToUpperInvariant(words[0]) + words[1..] + (words[^1] is '.' or '!' or '?' ? "" : ".");
        loop = loop.Trim();
        if (loop.Length == 0) return (Labels.NavFloor, "");
        var dash = loop.IndexOf(" — ", StringComparison.Ordinal);
        var head = dash > 0 ? loop[..dash] : loop;
        if (head.Length > HeadlineMost) return (Labels.NavFloor, Sentence(loop));
        return (char.ToUpperInvariant(head[0]) + head[1..], dash > 0 ? Sentence(loop[(dash + 3)..]) : "");
    }

    /// <summary>The most letters the large line is set in.</summary>
    public const int HeadlineMost = 22;

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
    static readonly TimeSpan Look = TimeSpan.FromSeconds(2);

    const int FeedLines = 9, Remembered = 240;
    const double FeedWidth = 318;

    public Control Root { get; }

    readonly FloorSources _sources;
    readonly FloorStage _stage = new() { OwnerWord = Labels.FloorOwner };
    readonly DispatcherTimer _look;
    readonly DispatcherTimer _clock;

    // the words over the stage
    /// <summary>The one large line: the state the organisation is in, in a word or two.</summary>
    readonly TextBlock _headline = Ui.Hero("");
    readonly TextBlock _state = Ui.Muted("");
    readonly TextBlock _working = Ui.Body("");
    readonly TextBlock _next = Ui.Muted("");
    readonly TextBlock _problem = Ui.Body("", Theme.Caution);
    /// <summary>That the floor cannot be drawn, and why: its own line, which no later read takes away — the floor stays dark for the rest of the run.</summary>
    readonly TextBlock _notDrawn = Ui.Body("", Theme.Caution);
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
    /// <summary>No turn is being opened now — the AI is stopped, paused or held: an active seat's dim light means only that it has a place.</summary>
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
        _feedAll = Ui.With(Ui.Ghost(Labels.FloorFeedAll, () => { _chosen = null; Mark(); Feed(); Aim(); }), b =>
        {
            b.FontSize = Theme.Small;
            b.Padding = new Thickness(Theme.S2, 5);
            b.HorizontalAlignment = HorizontalAlignment.Left;
            b.Margin = new Thickness(44 - Theme.S2, 0, 0, 0);
        });

        // The Chat page's own start, here too: while no AI is running, the app still opens on an answer to "so how do I start?".
        // GUARDED AT THE PRESS: the shell's own action starts the AI when none is running and STOPS it when one is, and whether this
        // button is shown is as old as the page's last read. So the press asks again, now, and does nothing if an AI is already running.
        _start = Ui.With(Ui.Primary(Labels.FloorStart, async () =>
        {
            if (_sources.AiRunning()) return;
            await startOrStop();
        }), b => b.HorizontalAlignment = HorizontalAlignment.Left);
        _start.IsVisible = false;
        _problem.IsVisible = false;
        _next.FontSize = Theme.Small;

        // THE APP'S NAME, SMALL, AND THE ORGANISATION'S STATE, LARGE: what the window says before anything on it is read.
        var brand = Ui.Row(Theme.S2, Icons.Mark(18), new TextBlock
        {
            Text = "TradeAgent", FontSize = Theme.Small, FontWeight = FontWeight.SemiBold, Foreground = Theme.TextMuted,
            VerticalAlignment = VerticalAlignment.Center
        });
        brand.Margin = new Thickness(0, 0, 0, Theme.S3);
        var title = Ui.Col(Theme.S1,
            brand,
            Ui.With(_headline, t => t.MaxWidth = 520),
            Ui.With(_state, t => t.MaxWidth = 460),
            Ui.With(_working, t => { t.MaxWidth = 460; t.Margin = new Thickness(0, Theme.S2, 0, 0); }),
            Ui.With(_next, t => t.MaxWidth = 460),
            Ui.With(_problem, t => t.MaxWidth = 460),
            Ui.With(_notDrawn, t => { t.MaxWidth = 460; t.IsVisible = false; }),
            Ui.With(_start, c => c.Margin = new Thickness(0, Theme.S3, 0, 0)),
            _replayRow = Ui.With(BuildReplay(), c => c.Margin = new Thickness(-Theme.S2, Theme.S2, 0, 0)));
        title.HorizontalAlignment = HorizontalAlignment.Left;
        title.VerticalAlignment = VerticalAlignment.Top;
        title.Margin = new Thickness(Theme.S8, Theme.S6, 0, 0);
        _title = title;

        // WHAT JUST HAPPENED, down the right: sentences on the dark of the stage, no card round them, each fainter than the one above it.
        _feedColumn = new Border
        {
            Width = FeedWidth, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(Theme.S4, Theme.S6, Theme.S5, Theme.S6),
            Background = Theme.FloorScrim,
            Child = Ui.Col(Theme.S4, _feedTitle, _feedEmpty, _feed, _feedAll)
        };
        _feedAll.IsVisible = false;

        // A ROW THAT WRAPS: beside a sheet the floor's share of the window is narrow, and the spend's sentence goes under the counts.
        _figures = Ui.Wrap(Theme.S8,
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

        // THE FLOOR STANDS BETWEEN THE TITLE AND THE FIGURES, and runs a little way under the sentences on the right, where they are darkest
        // — or, while the window has a sheet over its right side, in what the sheet leaves.
        _stage.Clear = size =>
        {
            // The floor is looked at from a corner, so the corner of its box nearest the title is empty: it may run up beside the
            // title's first lines, and is kept only from under the lines below them.
            // Beside a sheet there is no beside: the floor stands under the title.
            var top = _reserveRight > 0 ? title.Bounds.Bottom + 6 : _reserveTop + (title.Bounds.Height > 0 ? title.Bounds.Height * 0.42 : 90);
            var bottom = _figures.IsVisible && _figures.Bounds.Height > 0 ? _figures.Bounds.Top - 14 : size.Height - 28 - _reserveBottom;
            var right = _reserveRight > 0 ? size.Width - _reserveRight - 24 : size.Width - (_feedColumn.IsVisible ? FeedWidth - 14 : 28);
            // A stage too small to stand a floor in still gets a box with room in it: the camera fits what it can.
            return (28, (float)top, (float)Math.Max(right, 28 + 120), (float)Math.Max(bottom, top + 120));
        };
        _stage.Framed += frame => Safely(() => Place(frame));
        _stage.Failed += NotDrawn;
        _stage.Pointed += id => { _pointed = id; Mark(); };
        _stage.Chosen += id => { _chosen = id == _chosen ? null : id; Mark(); Feed(); Aim(); };
        Root.PropertyChanged += (_, e) =>
        {
            if (e.Property != Visual.BoundsProperty) return;
            Fit();
        };

        _look = new DispatcherTimer(Look, DispatcherPriority.Background, (_, _) => Read());
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Safely(Tick));
    }

    readonly StackPanel _title;
    readonly Control _replayRow;
    double _reserveTop, _reserveRight, _reserveBottom;

    /// <summary>
    /// THE WINDOW LAYS ITS GLASS OVER THE FLOOR (<c>U-ui-world</c>): how much of the top, the right and the foot of the stage it takes.
    /// The floor's own words step out of the way — the sentences down the right give their place to a sheet — and the organisation
    /// stands in what is left, the camera travelling there.
    /// </summary>
    public void Reserve(double top, double right, double bottom)
    {
        if (top == _reserveTop && right == _reserveRight && bottom == _reserveBottom) return;
        var moved = right != _reserveRight;
        _reserveTop = top;
        _reserveRight = right;
        _reserveBottom = bottom;
        _feedColumn.Margin = new Thickness(0, top, 0, bottom);
        _figures.Margin = new Thickness(Theme.S8, 0, 0, Theme.S4 + bottom);
        Fit();
        // A sheet arriving or leaving is a move of the camera's, not a window being resized: it travels.
        if (moved) _stage.Scene.Glide();
        _stage.Wake();
    }

    void Fit()
    {
        // On a narrow window, or under a sheet, the stage keeps the room: the sentences go, the floor stays.
        _feedColumn.IsVisible = _reserveRight <= 0 && Root.Bounds.Width >= 860;
        _figures.IsVisible = Root.Bounds.Height >= 520;
        // The floor's own words keep to the floor's share of the window. A replay is the floor's alone: beside a sheet — a page of
        // the present — its presses are put away with it.
        var share = Math.Max(240, Root.Bounds.Width - _reserveRight - 2 * Theme.S8);
        _title.MaxWidth = Math.Min(560, share);
        _figures.MaxWidth = _reserveRight > 0 ? share : double.PositiveInfinity;
        _replayRow.IsVisible = _reserveRight <= 0;
    }

    MainWindow.Page _place = MainWindow.Page.Floor;

    /// <summary>
    /// THE CAMERA GOES WHERE THE PAGE IS ABOUT (<c>U-ui-world</c>): the whole floor for the organisation's own pages, the owner's place
    /// and the chief for the conversation, the tape for the desk, the security board's slab for the board, the owner's place for what
    /// is his, and the floor seen low from the front — what it stands on — for the machine. Nothing on the floor moves; only where it
    /// is looked at from.
    /// </summary>
    public void Go(MainWindow.Page page)
    {
        var lens = page switch
        {
            MainWindow.Page.Work => FloorLens.Work,
            MainWindow.Page.Costs => FloorLens.Money,
            _ => FloorLens.Structure
        };
        var read = lens == FloorLens.Money && _stage.Scene.Reading != FloorLens.Money;
        _place = page;
        _stage.Scene.Lens(lens);
        // The money lens has its own read: asked for now, not at the next look.
        if (read) { _costs = null; Read(); }
        if (page != MainWindow.Page.Floor && _chosen is not null)
        {
            _chosen = null;
            Mark();
            Feed();
        }
        Aim();
    }

    /// <summary>Points the camera at the cell the owner chose, or else at what the page shown is about.</summary>
    void Aim()
    {
        var scene = _stage.Scene;
        var plan = scene.Plan;
        FloorLook? look = null;
        if (_chosen is { } chosen && plan.ById.ContainsKey(chosen)) look = scene.About(chosen);
        else if (plan.Slabs.Count > 0)
        {
            look = Places.Of(_place).Place switch
            {
                App.Place.Talk when plan.Chief is { } chief && plan.ById.TryGetValue(chief, out var cell) => new FloorLook(
                    Vector3.Min(plan.Owner, cell.Foot) - new Vector3(1.6f, 1.2f, 0), Vector3.Max(plan.Owner, cell.Foot) + new Vector3(1.6f, 0.9f, cell.Height + 0.5f),
                    FloorScene.RestYaw - 0.16f, FloorScene.RestPitch - 0.1f),
                App.Place.Desk => scene.AboutTape(),
                App.Place.Board => scene.AboutUnit(FloorChart.BoardUnit),
                App.Place.Yours => scene.About(FloorScene.OwnerId),
                App.Place.Machine => new FloorLook(plan.Min, plan.Max, FloorScene.RestYaw - 0.26f, FloorScene.RestPitch - 0.24f),
                _ => null
            };
        }
        scene.Look(look);
        _stage.Wake();
    }

    /// <summary>Whatever the floor was replaying, it shows the present again.</summary>
    public void Present() => StopReplay();

    /// <summary>The floor could not be drawn: said on the page, on a line of its own that stays.</summary>
    internal void NotDrawn(string why)
    {
        _notDrawn.Text = Labels.FloorNotDrawn + " " + why;
        _notDrawn.IsVisible = true;
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
            NotDrawn(ex is TradeAgentException t ? t.Info.UserMessage : ex.Message);
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

    /// <summary>Whether the floor builds itself the first time it is shown: as the window runs it, yes; stepped by a host, only if that host asks.</summary>
    internal bool Opening { get; init; }

    bool Opens => !Manual || Opening;

    /// <summary>One read of the ledgers and its showing, on the calling thread.</summary>
    internal void ReadNow() => Apply(Take(_seen));

    /// <summary>The page is on screen or it is not. Off screen it reads nothing and draws nothing.</summary>
    public void Shown(bool shown)
    {
        if (_shown == shown) return;
        _shown = shown;
        _stage.Stepped = Manual;
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
        GlanceRead? Glance, bool Running, DateTimeOffset Now, DateTimeOffset Until, string? Problem)
    {
        public FloorTapeRead? Tape { get; init; }

        /// <summary>Whether turns are being opened now (<see cref="FloorSources.TurnsOpen"/>).</summary>
        public bool Opening { get; init; }

        /// <summary>Today's spend by unit and class, read only while the money lens is on; null otherwise or where it could not be read.</summary>
        public IReadOnlyList<UnitClassTotals>? Costs { get; init; }
    }

    // What the reads keep between looks. Touched only by the read itself, which never overlaps another.
    GlanceRead? _glance;
    DateTimeOffset _glanceAt, _tapeAt;
    FloorTapeRead? _tape;
    IReadOnlyList<UnitClassTotals>? _costs;
    DateTimeOffset _costsAt;
    static readonly TimeSpan TapeEvery = TimeSpan.FromSeconds(15);
    readonly List<FloorMarks> _marks = [];

    /// <summary>"At a glance" is the Dashboard's whole read — the chart, the day's launches, every open mandate, the board, the campaigns, the paper runs. The floor takes it as often as the Dashboard does, not on every look.</summary>
    static readonly TimeSpan GlanceEvery = TimeSpan.FromSeconds(5);

    /// <summary>How often the ledgers are marked, how many marks are kept, and how old a mark must be before a look trusts it: a row is written within moments of the time it carries, never this long after.</summary>
    static readonly TimeSpan MarkEvery = TimeSpan.FromMinutes(2), MarkTrusted = TimeSpan.FromSeconds(45);

    /// <summary>A look that finds the floor was last read longer ago than this fills the sentences in again rather than playing what it missed as if it were happening.</summary>
    static readonly TimeSpan Missed = TimeSpan.FromSeconds(12);

    /// <summary>How much of the tape the floor shows: the last two hours of minutes.</summary>
    static readonly TimeSpan TapeStretch = TimeSpan.FromHours(2);

    /// <summary>A newest minute older than this is said to be old: the collector looks every minute, so a tape being recorded is never this far behind.</summary>
    static readonly TimeSpan TapeStale = TimeSpan.FromMinutes(6);

    /// <summary>The tape, or none — and none too where it could not be read: a strip of prices is not worth losing the floor for.</summary>
    FloorTapeRead? TapeAt(DateTimeOffset until, TimeSpan stretch)
    {
        try { return _sources.Tape(until, stretch); }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException)) { return null; }
    }

    /// <summary>The minutes as the scene draws them: each close as a height between the stretch's least and its most, and the words beside the newest.</summary>
    static FloorTape? Drawn(string symbol, IReadOnlyList<(DateTimeOffset Open, decimal Close)> bars, DateTimeOffset? now)
    {
        if (bars.Count < 2) return null;
        decimal least = bars.Min(b => b.Close), most = bars.Max(b => b.Close), span = most - least;
        var heights = bars.Select(b => span == 0 ? 0.5f : (float)((b.Close - least) / span)).ToArray();
        var (opened, close) = bars[^1];
        // A minute's bar closes a minute after it opens; how old the tape is is counted from there.
        var age = now is { } at ? at - opened.AddMinutes(1) : TimeSpan.Zero;
        var stale = age > TapeStale;
        return new FloorTape(symbol, heights, stale ? Labels.FloorTapeStale(symbol, close, age) : Labels.FloorTapeCaption(symbol, close), stale);
    }

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

        // WHAT WAS MISSED IS NOT PLAYED AS IF IT WERE HAPPENING: a floor last read a while ago — it was off screen, a read failed, the
        // computer slept — is filled in again from the last day, with nothing moving. And the clock going back never moves the read back.
        if (since is { } last && (until - last > Missed || until <= last)) since = until <= last ? last : null;

        var units = org.Units();
        var positions = org.Positions();
        var names = OrgNames.Of(units, positions);
        var chart = FloorChart.From(units, positions, names, _sources.Board());
        // OPEN NOW, by the rule "At a glance" uses: launched in the last day and not ended.
        var open = floor.Turns(now, now).Where(t => t.EndedAt is null && t.StartedAt >= now.AddDays(-1)).ToList();

        // The first look fills the sentences from the last day, with nothing moving; every later one takes only what is new — and of the
        // ledgers that have no index on their time, only the rows written since a mark old enough to be trusted.
        IReadOnlyList<FloorEvent> events;
        if (since is { } from)
        {
            var mark = _marks.LastOrDefault(m => m.TakenAt <= from - MarkTrusted);
            events = until > from ? floor.Events(from, until, after: mark) : [];
        }
        else events = floor.Events(until.AddDays(-1), until, 400, tools: false);
        if (_marks.Count == 0 || now - _marks[^1].TakenAt >= MarkEvery)
        {
            _marks.Add(floor.Marks(now));
            if (_marks.Count > 3) _marks.RemoveAt(0);
        }

        if (_glance is null || now - _glanceAt >= GlanceEvery)
        {
            _glance = _sources.Glance();
            _glanceAt = now;
        }
        // The tape moves once a minute: it is read a few times a minute, not on every look.
        if (_tape is null || now - _tapeAt >= TapeEvery)
        {
            _tape = TapeAt(now, TapeStretch);
            _tapeAt = now;
        }
        // THE MONEY LENS'S READ, only while that lens is on and as often as "At a glance": a figure nobody is looking at is not read.
        if (_place == MainWindow.Page.Costs && (_costs is null || now - _costsAt >= GlanceEvery))
        {
            try { _costs = _sources.Costs(now); }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException)) { _costs = null; }
            _costsAt = now;
        }
        return new Reading(chart, names, open, events, since is null, _glance, _sources.AiRunning(), now, until, null)
        {
            Tape = _tape, Opening = _sources.TurnsOpen(), Costs = _place == MainWindow.Page.Costs ? _costs : null
        };
    }

    // ---- what the read said, on the UI thread -----------------------------------------------------------------------------------

    void Apply(Reading r)
    {
        // A read that was on its way when a replay began is not shown over it.
        if (_replay is not null) return;
        // The read's own trouble comes and goes with the reads. That the floor cannot be DRAWN is another line, and stays.
        _problem.IsVisible = r.Problem is not null;
        if (r.Problem is { } problem)
        {
            _problem.Text = Labels.FloorNotRead + " " + problem;
            return;
        }
        if (r.Backfill)
        {
            // Filled in again from the ledgers: the sentences are whatever the last day holds, and nothing of before is left crossing.
            _lines.Clear();
            _lastTool.Clear();
        }

        _names = r.Names;
        _currency = r.Glance?.Spend?.Currency ?? _currency;
        if (r.Chart.Signature != _chartSignature)
        {
            _chartSignature = r.Chart.Signature;
            // The first floor builds itself as the page opens; a change to it afterwards grows in where it stands. A host that steps
            // the page itself is shown it standing.
            _stage.Scene.Lay(new FloorPlan(r.Chart), atOnce: !_laid && !Opens, assembling: !_laid && Opens);
            _laid = true;
            // What the camera was on may stand somewhere else now, or stand for the first time.
            Aim();
        }

        var loop = r.Glance?.Loop ?? "";
        var asleep = _asleep = !r.Opening;
        _openSince.Clear();
        foreach (var turn in r.Open) _openSince[turn.Role] = turn.StartedAt;
        foreach (var gone in _lastTool.Keys.Where(k => !_openSince.ContainsKey(k)).ToList()) _lastTool.Remove(gone);
        _stage.Scene.Lit(_openSince.Keys.ToHashSet(StringComparer.Ordinal), running: !asleep || _openSince.Count > 0);

        // A seat whose task is past its deadline carries a small amber light: the one thing on the floor that asks to be looked at.
        var late = r.Glance?.Mandates?.Rows.Where(o => o.Status != MandateEventKind.Delivered && r.Now > o.Mandate.Deadline)
            .Select(o => o.Mandate.Holder).ToHashSet(StringComparer.Ordinal) ?? [];
        _stage.Scene.Flag(late);

        Lenses(r);
        foreach (var e in r.Events) Happened(e, moving: !r.Backfill);
        if (_seen is null || r.Until > _seen) _seen = r.Until;
        _stage.Scene.Tape(r.Tape is { } tape ? Drawn(tape.Symbol, tape.Bars, r.Now) : null);

        Say(r, loop);
        Feed();
        // The scene was told what the look found; if that was nothing new, no frame is drawn for it.
        _stage.Nudge();
    }

    /// <summary>
    /// WHAT THE LENSES DRAW, from the same read every page shows (<c>U-ui-world</c>). The work lens: each open task a thread from the
    /// seat that gave it to the seat that holds it, its bead as far along as its time is — amber once it is past its deadline — and a
    /// delivery not yet judged a thread back to its giver, waiting at the giver's end. The money lens: each unit a column as tall as
    /// its day's spend beside the tallest, with its figure written at its top.
    /// </summary>
    void Lenses(Reading r)
    {
        var threads = new List<FloorThread>();
        if (r.Glance is { } glance)
        {
            var judged = glance.Deliveries?.Rows.Select(d => d.Mandate.Id).ToHashSet(StringComparer.Ordinal) ?? [];
            foreach (var open in glance.Mandates?.Rows ?? [])
            {
                var m = open.Mandate;
                if (judged.Contains(m.Id)) continue;
                var span = (m.Deadline - m.CreatedAt).TotalSeconds;
                var along = span <= 0 ? 1 : (float)Math.Clamp((r.Now - m.CreatedAt).TotalSeconds / span, 0, 1);
                var late = open.Status != MandateEventKind.Delivered && r.Now > m.Deadline;
                // Its bead moves by the minute, not by the second: told the same thread again, the floor draws nothing.
                threads.Add(new FloorThread(m.Decider, m.Holder, MathF.Round(along * 200) / 200, late ? FloorTone.Caution : FloorTone.Work, false));
            }
            foreach (var waits in glance.Deliveries?.Rows ?? [])
                threads.Add(new FloorThread(waits.Mandate.Holder, waits.Mandate.Decider, 1, FloorTone.Work, true));
        }
        _stage.Scene.Threads(threads);

        var columns = new Dictionary<string, FloorColumn>(StringComparer.Ordinal);
        if (r.Costs is { } costs)
        {
            // REAL MONEY IS WHAT A COLUMN'S HEIGHT IS — an API key's spend — and where no unit spent any, the plan's capacity at list
            // price, said as that. The two are never added and never drawn on one scale.
            var basis = costs.Any(c => c.BillingClass == BillingClasses.Key && c.Totals.Spent > 0) ? BillingClasses.Key
                : costs.Any(c => c.BillingClass == BillingClasses.Plan && c.Totals.Spent > 0) ? BillingClasses.Plan : null;
            var most = basis is null ? 0 : costs.Where(c => c.BillingClass == basis).Max(c => c.Totals.Spent);
            foreach (var unit in costs.GroupBy(c => c.UnitId, StringComparer.Ordinal))
            {
                var of = basis is null ? null : unit.FirstOrDefault(c => c.BillingClass == basis)?.Totals;
                var unpriced = unit.Sum(c => c.Totals.Unpriced);
                if (of is { Turns: > 0 } t)
                {
                    columns[unit.Key] = new FloorColumn(most <= 0 ? 0 : (float)(t.Spent / most), false)
                    {
                        Figure = Labels.Money(t.Spent, _currency), Of = Labels.FloorCostOf(basis!)
                    };
                }
                else if (unpriced > 0)
                {
                    // Turns ran here and nobody knows what they cost: an outline and the word, never a column of nothing.
                    columns[unit.Key] = new FloorColumn(0, true) { Figure = Labels.FloorUnknown, Of = Labels.FloorCostUnpriced(unpriced) };
                }
            }
        }
        _stage.Scene.Columns(columns);
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
        // THE STATE, LARGE, AND ITS REASON UNDER IT: the loop's own sentence, cut at its dash — "stopped — the AI has not been started"
        // is "Stopped" and, under it, "The AI has not been started." A sentence with no dash is said whole under the floor's name.
        var (headline, state) = FloorWords.Headline(r.Chart.Units.Count == 0 ? Labels.FloorEmpty : loop);
        _headline.Text = headline;
        _state.Text = state;
        _state.IsVisible = state.Length > 0;
        _working.Text = Labels.FloorWorking([.. _openSince.OrderBy(o => o.Value).Select(o => FloorWords.Who(o.Key, _names))]);
        _working.IsVisible = r.Chart.Units.Count > 0;

        // QUEUED NEXT: the soonest queued wake of an active position, as the queue has it — said only while turns are being opened,
        // since a loop that is stopped, paused or held serves none of them.
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
                row.PointerPressed += (_, _) => { _chosen = about; Mark(); Feed(); Aim(); };
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

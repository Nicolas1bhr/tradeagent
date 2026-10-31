using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;

namespace TradeAgent.App;

/// <summary>One open research campaign's budgets as its ledger counts them: trials and final verdicts charged against what it opened with.</summary>
sealed record GlanceCampaign(string Name, int TrialsUsed, int TrialBudget, int VerdictsUsed, int VerdictBudget);

/// <summary>One paper run as the deployment ledger reads it: which version, where, its state and its own book (a simulation).</summary>
sealed record GlancePaperRun(string Version, string Symbol, string State, RunBook? Book);

/// <summary>
/// ONE READ OF THE ORGANISATION FOR "AT A GLANCE" (<c>U-ui-glance</c>), every figure from a store a page already reads: the chart and its
/// work (the Organisation and Work pages), the launch ledger's open turns and today's spend (Costs, the Dashboard), the campaigns' budgets
/// (Settings' holdout card), the paper runs (the Owner page), each position's queued wakes (the mission queue the loop serves) and the
/// board's open acts (the Board page). <b>Nothing is estimated:</b> a part that could not be read is null with its reason, and the words say
/// so instead of drawing a zero.
/// </summary>
sealed record GlanceRead
{
    public required OrgNames Names { get; init; }
    public required DateTimeOffset Now { get; init; }

    /// <summary>The AI loop in the Dashboard's own words ("working", "stopped — …"), or null where no loop is read.</summary>
    public string? Loop { get; init; }

    /// <summary>Turns open now: the position and when its turn started. Null: the launch ledger could not be read (<see cref="Unread"/>).</summary>
    public IReadOnlyList<(string Position, DateTimeOffset Since)>? Working { get; init; }

    public OrgList<OpenMandate>? Mandates { get; init; }
    public OrgList<DeliveryToJudge>? Deliveries { get; init; }

    /// <summary>Board acts proposed by one seat and waiting for a second (the Board page's "open"), or null where none was read.</summary>
    public int? BoardOpen { get; init; }

    /// <summary>Today's AI spend against the day's ceilings, as the Owner page and the Dashboard read it.</summary>
    public AiSpendToday? Spend { get; init; }

    public IReadOnlyList<GlanceCampaign>? Campaigns { get; init; }
    public IReadOnlyList<GlancePaperRun>? PaperRuns { get; init; }

    /// <summary>Every active position, and each one's earliest unconsumed wake (absent: nothing queued).</summary>
    public IReadOnlyList<string>? Active { get; init; }
    public IReadOnlyDictionary<string, DateTimeOffset>? Wakes { get; init; }

    /// <summary>Why a part was not read, by part ("chart", "turns", "work", "board", "campaigns", "paper", "wakes").</summary>
    public IReadOnlyDictionary<string, string> Unread { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// The read over the running app. Each part in its own try, so one ledger that will not read leaves the others' lines intact — and
    /// says why on its own line.
    /// </summary>
    public static GlanceRead From(AppHost host, DateTimeOffset now)
    {
        var unread = new Dictionary<string, string>(StringComparer.Ordinal);
        T? Part<T>(string part, Func<T?> read) where T : class
        {
            try { return read(); }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
            {
                unread[part] = ex.Message;
                return null;
            }
        }

        var org = host.OrgLedger;
        var attempts = host.AttemptLedger;
        var chart = Part("chart", () => org is null ? null : new { Units = org.Units(), Positions = org.Positions() });
        var names = chart is null ? OrgNames.None : OrgNames.Of(chart.Units, chart.Positions);
        var open = Part("turns", () => attempts?.Between(now.AddDays(-1), now.AddMinutes(1))
            .Where(a => a.EndedAt is null && a.Role is { Length: > 0 })
            .Select(a => (a.Role!, a.StartedAt)).ToList());
        var mandates = Part("work", () => org?.MandatesOpen());
        var deliveries = Part("work", () => org?.DeliveriesAwaiting());
        int? board;
        try { board = host.Board.Acts().Count(a => a.Status == TradeAgent.Core.Board.BoardActStatus.Open); }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            unread["board"] = ex.Message;
            board = null;
        }
        // THE OWNER PAGE'S OWN READER of each pair's campaign (U-evidence-breadth item 4), so the two pages cannot count differently.
        var campaigns = Part("campaigns", () => host.Evidence.Standings()
            .Select(c => new GlanceCampaign($"{c.Pair}, campaign {c.Campaign.Id}", c.Trials, c.Campaign.TrialBudget, c.Judgements, c.Campaign.VerdictBudget))
            .ToList());
        var paper = Part("paper", () => host.Gateway.DeploymentReadings().Where(d => d.Outstanding)
            .Select(d => new GlancePaperRun(StrategyDeploymentRow.Short(d.VersionId), d.Symbol, d.State, d.Book)).ToList());
        var wakes = Part("wakes", () => host.Db is { } db ? new MissionEventStore(db).NextDueByRole() : null);

        string? loop;
        try { loop = DashboardPage.MissionSentence(host.Mission.Status, host.AiNotStarted); }
        catch (Exception) { loop = null; }

        return new GlanceRead
        {
            Names = names,
            Now = now,
            Loop = loop,
            Working = open,
            Mandates = mandates,
            Deliveries = deliveries,
            BoardOpen = board,
            Spend = host.SpendToday,
            Campaigns = campaigns,
            PaperRuns = paper,
            Active = chart?.Positions.Where(p => p.Status == OrgPositionStatus.Active).Select(p => p.Id).ToList(),
            Wakes = wakes,
            Unread = unread
        };
    }
}

/// <summary>One line of the card: its label, the line itself, and the details under it (empty: none). <see cref="Caution"/> draws it in caution.</summary>
sealed record GlanceLine(string Key, string Label, string Line, string Details = "", bool Caution = false);

/// <summary>
/// "AT A GLANCE" IN WORDS (<c>U-ui-glance</c>; PRINCIPLES "The app explains what is working, waiting, blocked or paused, why, what it costs, and
/// what happens next"): who is working, what waits on whom, today's spend against the day's ceilings, each campaign's trials and verdicts
/// left, the paper runs and their results — labelled simulations — and each active position's next wake. Separated from the controls so a
/// test reads the words. <b>Every figure is the read's</b>; unknown is said as unknown, never as zero.
/// </summary>
static class GlanceWords
{
    public const string Loop = "loop", Working = "working", Waiting = "waiting", Spend = "spend", Campaigns = "campaigns", Paper = "paper",
        Wakes = "wakes";

    /// <summary>The lines the compact card on the Dashboard shows, in order: the Dashboard already says the AI's state and its cost.</summary>
    public static readonly string[] Compact = [Working, Waiting, Campaigns, Paper, Wakes];

    public static IReadOnlyList<GlanceLine> Of(GlanceRead r) =>
    [
        new(Loop, "The AI", r.Loop is { Length: > 0 } loop ? Cap(loop) + "." : "Its state is not read here."),
        WorkingLine(r),
        WaitingLine(r),
        SpendLine(r),
        CampaignsLine(r),
        PaperLine(r),
        WakesLine(r)
    ];

    static string Unread(GlanceRead r, string part, string what) =>
        r.Unread.TryGetValue(part, out var why) ? $"{what} could not be read: {why}" : $"{what} is not read here.";

    static GlanceLine WorkingLine(GlanceRead r)
    {
        if (r.Working is not { } open) return new(Working, "Working now", Unread(r, "turns", "Who is working"), Caution: true);
        if (open.Count == 0) return new(Working, "Working now", "Nobody: no AI turn is open right now.");
        var who = open.OrderBy(o => o.Since).Select(o => $"{r.Names.Position(o.Position)} (a turn open since {OrgText.When(o.Since)})");
        return new(Working, "Working now", string.Join("; ", who) + ".");
    }

    static GlanceLine WaitingLine(GlanceRead r)
    {
        if (r.Mandates is not { } mandates || r.Deliveries is not { } deliveries)
            return new(Waiting, "Waiting", Unread(r, "work", "What waits on whom"), Caution: true);
        var n = r.Names;
        var owed = mandates.Rows.Where(o => o.Status != MandateEventKind.Delivered).ToList();
        var late = owed.Where(o => r.Now > o.Mandate.Deadline).OrderBy(o => o.Mandate.Deadline).ToList();
        var due = owed.Where(o => r.Now <= o.Mandate.Deadline).OrderBy(o => o.Mandate.Deadline).ToList();

        var details = new List<string>();
        foreach (var o in late)
            details.Add($"{n.Position(o.Mandate.Holder)} owes {n.Position(o.Mandate.Decider)} a delivery — due {OrgText.When(o.Mandate.Deadline)}, past its deadline.");
        foreach (var d in deliveries.Rows.OrderBy(d => d.Delivery.At))
            details.Add($"{n.Position(d.Mandate.Decider)} owes a judgement on {n.Position(d.Mandate.Holder)}'s delivery of {OrgText.When(d.Delivery.At)}.");
        if (due.Count > 0)
            details.Add($"{Count(due.Count, "mandate is", "mandates are")} not yet due; the next, {n.Position(due[0].Mandate.Holder)}'s, is due {OrgText.When(due[0].Mandate.Deadline)}.");
        var board = r.BoardOpen switch
        {
            null => Unread(r, "board", "The board's open proposals"),
            0 => "",
            1 => "1 board proposal waits for a second seat to confirm it.",
            var k => $"{k} board proposals wait for a second seat to confirm them."
        };
        if (board.Length > 0) details.Add(board);
        foreach (var refused in mandates.Refused.Concat(deliveries.Refused)) details.Add(refused);

        var parts = new List<string>();
        if (late.Count > 0) parts.Add($"{Count(late.Count, "mandate is", "mandates are")} past its deadline");
        if (deliveries.Rows.Count > 0) parts.Add($"{Count(deliveries.Rows.Count, "delivery waits", "deliveries wait")} to be judged");
        if (r.BoardOpen is > 0) parts.Add($"{Count(r.BoardOpen.Value, "board proposal waits", "board proposals wait")} for a second seat");
        var line = parts.Count > 0 ? Cap(string.Join("; ", parts)) + "."
            : due.Count > 0 ? $"Nothing is overdue: {Count(due.Count, "mandate is", "mandates are")} open and on time."
            : "Nothing waits on anyone: no mandate is open and no delivery waits to be judged.";
        // A PART NOT READ IS NOT "NOTHING" (Codex review): the board unread or a row refused, and the line says it may not be all.
        var incomplete = r.BoardOpen is null || mandates.Refused.Count + deliveries.Refused.Count > 0;
        if (incomplete) line += " Not everything could be read, so this may not be all of it.";
        return new(Waiting, "Waiting", line, string.Join("\n", details), late.Count > 0 || incomplete);
    }

    static GlanceLine SpendLine(GlanceRead r)
    {
        if (r.Spend is not { } s) return new(Spend, "AI spend today", "Not read here.");
        if (!s.Metered) return new(Spend, "AI spend today", "Unknown: nothing is metering this installation's AI turns.", Caution: true);
        var (money, plan) = OwnerPage.TodayLines(s);
        var line = money.StartsWith("Today: ", StringComparison.Ordinal) ? Cap(money["Today: ".Length..]) : money;
        var details = plan.StartsWith("Today: ", StringComparison.Ordinal) ? "Plan: " + plan["Today: ".Length..] : plan;
        if (!s.CanPrice)
            details += $"\nNeither figure is complete: TradeAgent cannot price this AI's turns ({s.WhyNoPrice ?? "no reason recorded"}).";
        // TURNS NOT IN THE FIGURE ARE NAMED BESIDE IT (Codex review): unknown is never folded into a total as zero.
        if (s.UnpricedTurns > 0)
            details += $"\n{Count(s.UnpricedTurns, "turn", "turns")} reported no cost and {(s.UnpricedTurns == 1 ? "is" : "are")} not in that figure.";
        if (s.UnreportedTurns > 0)
            details += $"\n{Count(s.UnreportedTurns, "turn", "turns")} reported no usage and {(s.UnreportedTurns == 1 ? "is" : "are")} charged "
                       + "the reservation, so that figure is not a bill.";
        return new(Spend, "AI spend today", line, details, !s.AdmitsAnotherTurn || !s.CanPrice || s.UnpricedTurns > 0);
    }

    static GlanceLine CampaignsLine(GlanceRead r)
    {
        if (r.Campaigns is not { } campaigns) return new(Campaigns, "Research campaigns", Unread(r, "campaigns", "The campaigns"), Caution: true);
        if (campaigns.Count == 0)
            return new(Campaigns, "Research campaigns", "None is open over real history, so no research run or final judgement is being spent.");
        var lines = campaigns.Select(c =>
            $"{c.Name}: {Left(c.TrialBudget - c.TrialsUsed, "research run")} of {c.TrialBudget} ({c.TrialsUsed} used); "
            + $"{Left(c.VerdictBudget - c.VerdictsUsed, "final judgement")} of {c.VerdictBudget} ({c.VerdictsUsed} used).").ToList();
        return new(Campaigns, "Research campaigns", lines[0], string.Join("\n", lines.Skip(1)),
            campaigns.Any(c => c.TrialsUsed >= c.TrialBudget || c.VerdictsUsed >= c.VerdictBudget));

        static string Left(int n, string what) => $"{Math.Max(0, n).ToString(CultureInfo.InvariantCulture)} {what}{(Math.Max(0, n) == 1 ? "" : "s")} left";
    }

    static GlanceLine PaperLine(GlanceRead r)
    {
        const string label = "Paper runs (simulations)";
        if (r.PaperRuns is not { } runs) return new(Paper, label, Unread(r, "paper", "The paper runs"), Caution: true);
        if (runs.Count == 0) return new(Paper, label, "None is running: nothing is being tried on paper.");
        var lines = runs.Select(p => $"{p.Version} in {p.Symbol}, {p.State}: {Result(p.Book)}").ToList();
        return new(Paper, label, lines[0], string.Join("\n", lines.Skip(1).Append("A paper result is a simulation: no real money was made or lost.")));

        static string Result(RunBook? book) => book switch
        {
            null => "its result is unknown — its book was not read.",
            { Incomplete: { } why } => $"its result is unknown — its book is incomplete ({why}).",
            { Fills: 0 } => "no fill yet, so no result.",
            var b => $"realised {AllocationRow.Num(b.RealisedAfterCosts)} after {AllocationRow.Num(b.Fees)} in reported costs over "
                     + $"{Count(b.Fills, "fill", "fills")}"
                     + (b.FillsWithNoFee > 0 ? $" ({b.FillsWithNoFee} reported no fee, so the real figure is a little worse)" : "")
                     + (b.Held > 0m ? $"; holds {AllocationRow.Num(b.Held)}" : "") + " — simulated."
        };
    }

    static GlanceLine WakesLine(GlanceRead r)
    {
        if (r.Active is not { } active) return new(Wakes, "Next wakes", Unread(r, "chart", "The positions"), Caution: true);
        if (r.Wakes is not { } wakes) return new(Wakes, "Next wakes", Unread(r, "wakes", "The wakes"), Caution: true);
        if (active.Count == 0) return new(Wakes, "Next wakes", "No position is active, so none will wake.");
        var queued = active.Where(wakes.ContainsKey).OrderBy(p => wakes[p]).ToList();
        var details = active.OrderBy(p => wakes.TryGetValue(p, out var at) ? at : DateTimeOffset.MaxValue)
            .Select(p => $"{r.Names.Position(p)}: " + (wakes.TryGetValue(p, out var at)
                ? at <= r.Now ? $"a wake is due since {OrgText.When(at)}" : $"next wake {OrgText.When(at)}"
                : "nothing queued")).ToList();
        var line = queued.Count == 0
            ? $"Nothing is queued for any of the {Count(active.Count, "active position", "active positions")}."
            : $"{queued.Count} of {Count(active.Count, "active position has", "active positions have")} a wake queued; the soonest, "
              + $"{r.Names.Position(queued[0])}'s, is {(wakes[queued[0]] <= r.Now ? $"due since {OrgText.When(wakes[queued[0]])}" : OrgText.When(wakes[queued[0]]))}.";
        // A WAKE ONLY RUNS WHILE THE AI DOES: a stopped loop is said beside the queue, so a due wake is not read as one being served.
        if (r.Loop is { } loop && loop.StartsWith("stopped", StringComparison.Ordinal))
            line += " The AI is stopped, so none runs until it is started.";
        return new(Wakes, "Next wakes", line, string.Join("\n", details));
    }

    static string Count(int n, string one, string many) => $"{n.ToString(CultureInfo.InvariantCulture)} {(n == 1 ? one : many)}";

    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

/// <summary>
/// THE "AT A GLANCE" CARD (<c>U-ui-glance</c>): the full form at the top of the Organisation page, the compact form — each line without its
/// details, and without the AI's state and cost the Dashboard already shows — on the Dashboard. <b>It only reads</b>: no press.
/// <b>Built once, updated in place</b>: a row per line made in the constructor; <see cref="Update"/> writes text, tone and visibility.
/// </summary>
sealed class GlanceCard
{
    readonly Func<GlanceRead?> _read;
    readonly bool _compact;
    readonly Dictionary<string, (TextBlock Line, TextBlock Details)> _rows = new(StringComparer.Ordinal);
    readonly TextBlock _unread = Ui.With(Ui.Muted(""), t => { t.FontSize = Theme.Small; t.IsVisible = false; });

    public Control Root { get; }

    /// <summary>Every line's words as last written, for a test.</summary>
    internal IReadOnlyList<GlanceLine> Shown { get; private set; } = [];

    public GlanceCard(Func<GlanceRead?> read, bool compact)
    {
        _read = read;
        _compact = compact;
        var keys = compact ? GlanceWords.Compact
            : [GlanceWords.Loop, GlanceWords.Working, GlanceWords.Waiting, GlanceWords.Spend, GlanceWords.Campaigns, GlanceWords.Paper, GlanceWords.Wakes];
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{Theme.GlanceLabelWidth},*") };
        var row = 0;
        foreach (var key in keys)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = Ui.With(Ui.Muted(""), t => { t.FontSize = Theme.Small; t.Margin = new Avalonia.Thickness(0, 0, Theme.S3, Theme.S3); });
            label[Grid.RowProperty] = row;
            label.Tag = key;
            var line = Ui.With(Ui.Body(""), t => t.FontSize = Theme.Small);
            var details = Ui.With(Ui.Muted(""), t => { t.FontSize = Theme.Small; t.IsVisible = false; });
            var cell = Ui.Col(Theme.S1, line, details);
            cell.Margin = new Avalonia.Thickness(0, 0, 0, Theme.S3);
            cell[Grid.RowProperty] = row;
            cell[Grid.ColumnProperty] = 1;
            grid.Children.Add(label);
            grid.Children.Add(cell);
            _rows[key] = (line, details);
            _labels[key] = label;
            row++;
        }
        var body = compact ? Ui.Col(Theme.S2, grid, Ui.Micro(Labels.GlanceCompactMore), _unread) : Ui.Col(Theme.S2, grid, _unread);
        Root = Ui.Section(Labels.GlanceTitle, body);
    }

    readonly Dictionary<string, TextBlock> _labels = new(StringComparer.Ordinal);

    /// <summary>In place on every tick: text, tone and visibility only.</summary>
    public void Update()
    {
        GlanceRead? read;
        try { read = _read(); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { return; }   // the quit closed the file under this tick
        catch (Exception ex)
        {
            OrgText.Set(_unread, Labels.SurfaceUnreadable(ex.Message));
            _unread.IsVisible = true;
            return;
        }
        if (read is null)
        {
            OrgText.Set(_unread, Labels.SurfaceNotRead);
            _unread.IsVisible = true;
            return;
        }
        _unread.IsVisible = false;

        var lines = GlanceWords.Of(read);
        Shown = [.. lines.Where(l => _rows.ContainsKey(l.Key))];
        foreach (var l in Shown)
        {
            var (line, details) = _rows[l.Key];
            OrgText.Set(_labels[l.Key], l.Label);
            OrgText.Set(line, l.Line);
            var tone = l.Caution ? Theme.Caution : Theme.Text;
            if (!ReferenceEquals(line.Foreground, tone)) line.Foreground = tone;
            var showDetails = !_compact && l.Details.Length > 0;
            OrgText.Set(details, showDetails ? l.Details : "");
            if (details.IsVisible != showDetails) details.IsVisible = showDetails;
        }
    }
}

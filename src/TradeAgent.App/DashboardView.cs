using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TradeAgent.AgentRuntime;
using TradeAgent.ConnectorSdk;
using TradeAgent.Core;
using TradeAgent.Gateway;

namespace TradeAgent.App;

/// <summary>
/// Everything in the shell that is not the conversation: the live picture, the safety controls, the
/// activity log and the self-checks.
///
/// Every page here is built once and updated in place. Where a list has to change shape — health
/// rows, activity lines, pending approvals — the new contents are compared against a signature
/// string first, so a five-second background tick that changed nothing touches nothing. That is not
/// an optimisation: rebuilding the approvals list disarms a half-pressed "Confirm: place this
/// order", and rebuilding the activity list throws away the user's scroll position.
/// </summary>
static class Pages
{
    public static Control Header(string title, string subtitle) =>
        Ui.With(Ui.Col(Theme.S1, Ui.H1(title), Ui.Muted(subtitle)),
            c => c.Margin = new Thickness(0, 0, 0, Theme.S6));

    /// <summary>A scrolling page body with room for the scrollbar to sit outside the content.</summary>
    public static ScrollViewer Scroll(Control content) => new()
    {
        Content = content,
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        Padding = new Thickness(0, 0, Theme.S3, 0)
    };

    public static Control Column(int index, Control content)
    {
        content[Grid.ColumnProperty] = index;
        return content;
    }
}

/// <summary>
/// The one repair the owner can perform on a bridge that is refused or missing, as a card.
///
/// Built here rather than on either page because BOTH pages carry it — Checks when the bridge row
/// calls for it, Settings always — and two hand-written copies of a control the app's sentences name
/// by label is how the label and the sentence come apart. This is the control those sentences mean.
///
/// Two presses, because replacing the file stops trading through ATAS until the strategy is started
/// again, and the armed label says exactly that rather than the word "Confirm". Nothing on it names
/// a folder, a command or a window outside this one.
/// </summary>
static class BridgeRepair
{
    /// <summary>
    /// The words and the button, with no framing of their own: Checks puts them in a card under its
    /// own heading, Settings puts them in one of its sections.
    /// </summary>
    public static Control Body(AppHost host)
    {
        var note = Ui.Body("");
        note.IsVisible = false;

        var button = Ui.Confirm(Labels.ReinstallBridge,
            "Confirm: replace the bridge — trading through ATAS stops until it is started again",
            async () =>
            {
                note.IsVisible = true;
                note.Foreground = Theme.TextMuted;
                note.Text = "Putting the bridge back…";

                var result = await host.ReinstallBridgeAsync();
                note.Text = result.Sentence;
                note.Foreground = result.Ok ? Theme.Positive : Theme.Caution;
            });
        button.HorizontalAlignment = HorizontalAlignment.Left;

        return Ui.Col(Theme.S3,
            Ui.Body("The bridge is the small piece TradeAgent puts inside ATAS so the two can talk to each other. " +
                    "If it is missing, or ATAS is running an older one than this version of TradeAgent expects, " +
                    "putting it back is the repair."),
            Ui.Muted("Close ATAS first if it is open. The bridge cannot be replaced while ATAS is using it."),
            button,
            note);
    }
}

// =================================================================================================

/// <summary>The live picture, plus anything the AI is currently waiting on the user to answer.</summary>
sealed class DashboardPage
{
    static readonly string[] FactKeys =
        ["Trading mode", "Platform", "Account", "AI trading", "Open orders / unconfirmed"];

    readonly AppHost _host;
    readonly Func<Task> _toggleAgent;

    readonly Dictionary<string, TextBlock> _values = new();
    readonly StackPanel _healthRows = new() { Spacing = 2 };
    readonly StackPanel _approvals = new() { Spacing = Theme.S3 };
    readonly Border _approvalsCard;
    readonly Button _agentButton;

    readonly PerformanceCard _performance = new();
    readonly StackPanel _unconfirmed = new() { Spacing = Theme.S5 };
    readonly Border _unconfirmedCard;
    readonly List<UnconfirmedRow> _unconfirmedRows = [];

    readonly Button _workButton;
    readonly TextBlock _missionState = Ui.With(Ui.Body("—"), t => t.FontWeight = FontWeight.SemiBold);
    readonly TextBlock _missionCounts = Ui.Micro("");
    readonly TextBlock _missionCost = Ui.Micro("");
    readonly TextBlock _missionLast = Ui.Muted("");
    readonly TextBox _guidance;

    string _healthSignature = "";
    string _approvalSignature = "";
    string _unconfirmedSignature = "";

    /// <summary>
    /// THE ARMED SENTENCES OF THE ONE CONTROL ON THIS PAGE THAT GRANTS. Spelled here rather than at
    /// the widget so a test and the guide can quote the words the owner actually reads — the same
    /// arrangement the Safety page's controls have in <see cref="Labels"/>.
    /// </summary>
    public const string LetTheAiWork = "Let the AI work on its own";

    /// <summary>
    /// WHAT THE SECOND PRESS DOES, IN FULL — including what it will cost. Never the bare word
    /// "Confirm".
    ///
    /// The press is a grant of a great deal of room, and the cap is the whole of what bounds it. An
    /// armed sentence that named the autonomy but not the money would be describing half the grant;
    /// a sentence that names a limit which cannot actually be applied would be worse than that, so
    /// the unpriced case says so instead of quoting a ceiling that is holding nothing back.
    /// </summary>
    public static string LetTheAiWorkArmed(AiSpendToday spend)
    {
        const string head = "Confirm: let the AI keep working without being asked";

        if (!spend.Metered) return head;

        return spend.CanPrice
            ? head + $", up to {MissionSituation.Money(spend.Cap, spend.Currency)} a day"
            : head + " — TradeAgent cannot price this AI, so your daily limit will not stop it";
    }

    /// <summary>The other direction. One press: it only ever takes work away.</summary>
    public const string PauseTheAi = "Pause the AI";

    public Control Root { get; }

    public DashboardPage(AppHost host, Func<Task> toggleAgent)
    {
        _host = host;
        _toggleAgent = toggleAgent;

        var facts = new StackPanel { Spacing = 2 };
        foreach (var key in FactKeys)
        {
            // Counts and account numbers are compared with other numbers, so they are set in the
            // mono face; the two prose values are not.
            var value = key is "Open orders / unconfirmed" or "Account"
                ? Ui.Mono("—")
                : Ui.With(Ui.Body("—"), t => t.FontSize = Theme.Small);
            _values[key] = value;
            facts.Children.Add(Ui.KeyValueLive(key, value));
        }

        _agentButton = Ui.Primary("Start the AI", async () => await _toggleAgent());

        var actions = Ui.Row(Theme.S2,
            _agentButton,
            Ui.Ghost("Open ATAS", () => MainWindow.OpenAtasOrExplain(m => Ui.ReportError?.Invoke(m))),
            Ui.Ghost("Open the AI's folder", () => MainWindow.OpenPath(Paths.Workspace, m => Ui.ReportError?.Invoke(m))));
        actions.Margin = new Thickness(0, Theme.S4, 0, 0);

        // Orders waiting on the user. In "Real, ask me first" the AI proposes and stops; without a
        // way to say yes here, that mode had no exit and the only usable real-money setting was the
        // fully automatic one. The card hides itself when there is nothing to answer.
        _approvalsCard = new Border
        {
            Background = Theme.CautionSoft,
            BorderBrush = Theme.Caution,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.Radius,
            Padding = new Thickness(Theme.S5),
            IsVisible = false,
            Child = Ui.Col(Theme.S4,
                Ui.With(Ui.Eyebrow("The AI is asking permission"), t => t.Foreground = Theme.Caution),
                _approvals)
        };

        // Orders TradeAgent could not confirm. This is the ONLY route into
        // TradingGateway.ForceResolve anywhere in the product: operator authority is deliberately
        // absent from the agent-facing pipe and from the trade CLI, so an agent that wants this
        // permission has nowhere to ask. Without this card, on a backend that cannot prove its own
        // order history — which is ATAS, permanently — the first ambiguous order pauses trading and
        // nothing in the app can ever start it again.
        //
        // It sits above the approvals card because it outranks it: while anything is unconfirmed,
        // TryAuthorizeExecution refuses, so approving an order below would fail anyway.
        _unconfirmedCard = new Border
        {
            Background = Theme.DangerSoft,
            BorderBrush = Theme.Danger,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.Radius,
            Padding = new Thickness(Theme.S5),
            IsVisible = false,
            Child = Ui.Col(Theme.S3,
                Ui.With(Ui.Eyebrow("Orders TradeAgent could not confirm"), t => t.Foreground = Theme.Danger),
                Ui.Body("An order was sent and no answer came back, so TradeAgent does not know whether it reached your broker. "
                        + "It will not let the AI trade until that is settled."),
                Ui.Body("Open ATAS, find the order, and tell TradeAgent what you see there. You are asserting something "
                        + "TradeAgent could not check for itself, and AI trading resumes on your word — so look before you press.",
                    Theme.Caution),
                // Named no button: a record that is already in a terminal state gets only one, so
                // pointing at "no order exists" would send that reader looking for a control that
                // is not on their screen.
                Ui.Micro("If the order is still working in ATAS, cancel it there first — then what you tell TradeAgent "
                         + "below is true. Every answer here is written into the activity log with your note."),
                Ui.With(_unconfirmed, p => p.Margin = new Thickness(0, Theme.S2, 0, 0)))
        };

        // The AI's own work. Built once, like everything else on this page: the five-second tick
        // rewrites the text of these four controls and touches nothing else, so a half-pressed
        // "Confirm: let the AI keep working…" and a half-typed guidance box both survive it.
        _workButton = BuildWorkOnItsOwn(
            () => _host.Mission.Running,
            () => _host.LetTheAiWorkOnItsOwn(),
            () => _ = PauseMissionAsync(),
            // Asked at ARM time, not at build time: the cap the second press names has to be the one
            // in force when the owner reads it, and they may have changed it on the Safety page since
            // this window opened.
            () => _host.SpendToday);

        _guidance = Ui.TextField(host.Gateway.Settings.Guidance,
            "e.g. focus on ES during the US session; keep positions small until the journal shows three good days");
        _guidance.AcceptsReturn = true;
        _guidance.TextWrapping = TextWrapping.Wrap;
        _guidance.MinHeight = 72;

        var aiCard = Ui.Col(Theme.S3,
            _missionState,
            _missionCounts,
            _missionCost,
            Ui.With(_missionLast, t => t.FontSize = Theme.Small),
            Ui.With(Ui.Row(Theme.S2, _workButton), r => r.Margin = new Thickness(0, Theme.S2, 0, 0)),
            Ui.With(Ui.Divider(), d => d.Margin = new Thickness(0, Theme.S2, 0, 0)),
            Ui.Label("Guidance"),
            // Words, not authority, and the box says so. Nothing typed here can widen a limit,
            // change a mode or lift the kill switch — the AI is told the same thing in its own file.
            Ui.Micro("Included in everything the AI is told, every turn. It steers what it works on; "
                     + "it cannot give it permission to do anything the Safety page has not."),
            _guidance,
            Ui.With(Ui.Row(Theme.S2, Ui.Secondary("Save guidance", SaveGuidance)),
                r => r.Margin = new Thickness(0, Theme.S2, 0, 0)));

        var left = Ui.Col(Theme.S6,
            _unconfirmedCard,
            _approvalsCard,
            Ui.Section("Right now", Ui.Col(0, facts, actions)),
            Ui.Section("The AI's own work", aiCard),
            _performance.Root);

        var right = Ui.Section("System health", _healthRows);
        right.Margin = new Thickness(Theme.S5, 0, 0, 0);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,340") };
        grid.Children.Add(Pages.Column(0, left));
        grid.Children.Add(Pages.Column(1, right));

        Root = Pages.Scroll(Ui.Col(0,
            Pages.Header("Dashboard", "What TradeAgent and the AI are doing right now."),
            grid));
    }

    public void Update(GatewayStatus status, IReadOnlyList<ExecutionRequest> waiting)
    {
        _values["Trading mode"].Text = Ui.ModeLabel(status.Mode);
        _values["Platform"].Text = Ui.PlatformLabel(status);
        _values["Account"].Text = status.AccountId ?? "not selected";
        _values["AI trading"].Text = status.AiTradingStopped ? "STOPPED"
            : status.ExecutionAvailable ? "allowed"
            : $"paused — {status.ExecutionBlockedReason}";
        _values["AI trading"].Foreground = status.AiTradingStopped ? Theme.Danger
            : status.ExecutionAvailable ? Theme.Positive
            : Theme.Caution;
        _values["Open orders / unconfirmed"].Text = $"{status.OpenRequests} / {status.UnreconciledRequests}";

        _agentButton.Content = _host.Agent.Running ? "Stop the AI" : "Start the AI";
        MainWindow.SetVariant(_agentButton, _host.Agent.Running ? "secondary" : "primary");

        RefreshMission(_host.Mission.Status, _host.Mission.Running, _host.SpendToday);
        RefreshApprovals(waiting);
        // Read straight from the gateway rather than from GatewayStatus, which carries only a count.
        // Unreconciled() is the same question TryAuthorizeExecution refuses on — the flag AND a
        // request left stranded in DISPATCHING — so the card shows exactly the records that are
        // pausing trading. Reading the raw flag here left the card empty while the banner said
        // paused, which reads as the software being broken rather than careful.
        RefreshUnconfirmed(_host.Gateway.Unreconciled());
        _performance.Update(_host.Gateway.LedgerPnl(TradingGateway.StartOfDay(DateTimeOffset.UtcNow), "today"), _host.Gateway.LedgerPnl(null, "all"));

        var health = _host.Health.Snapshot();
        var hs = string.Join('|', health.Select(h => $"{h.Component}:{h.State}:{h.Detail}"));
        if (hs != _healthSignature)
        {
            _healthSignature = hs;
            _healthRows.Children.Clear();
            foreach (var h in health) _healthRows.Children.Add(Ui.StatusRow(h));
        }
    }

    // ---- the AI's own work -----------------------------------------------------------------------

    /// <summary>
    /// LETTING THE AI WORK ON ITS OWN IS TWO PRESSES; PAUSING IT IS ONE.
    ///
    /// It is the same rule as every other control in this product (REVIEW 2026-09-05b finding 3):
    /// a press that gives the AI room asks twice and says what the second press will do, in full; a
    /// press that takes room away happens at once, because hesitating on the way down costs money
    /// and an owner trying to stop should not have to argue with the software.
    ///
    /// This one gives it a great deal of room. It is the difference between an AI that answers when
    /// spoken to and one that starts a new turn the moment the last one ends, for as long as the
    /// machine is on — which is the product, and is not something to arrive at by a mis-click.
    ///
    /// A factory, like the Safety page's three, so that what a test presses is the control the owner
    /// sees rather than a reconstruction of it.
    /// </summary>
    internal static Button BuildWorkOnItsOwn(Func<bool> working, Action letItWork, Action pause,
        Func<AiSpendToday>? spend = null) =>
        Ui.ConfirmIf(working() ? PauseTheAi : LetTheAiWork,
            () => working() ? null : LetTheAiWorkArmed(spend?.Invoke() ?? AiSpendToday.NotMetered),
            () => { if (working()) pause(); else letItWork(); },
            working() ? "secondary" : "primary");

    /// <summary>
    /// The four words the card can say, and the two numbers beside them. Every value is read from the
    /// loop rather than inferred here, so the card cannot claim a state the loop is not in.
    ///
    /// <c>Ui.SetResting</c> rather than assigning the content: the label changes when the loop starts
    /// or stops, and a plain assignment on the five-second tick would wipe a half-pressed confirm off
    /// the screen while the owner was reading it.
    /// </summary>
    void RefreshMission(MissionStatus status, bool running, AiSpendToday spend)
    {
        Ui.SetResting(_workButton, running ? PauseTheAi : LetTheAiWork, running ? "secondary" : "primary");

        _missionCost.Text = MissionCost(spend);
        _missionCost.IsVisible = _missionCost.Text.Length > 0;
        _missionCost.Foreground = spend.CapReached ? Theme.Caution
            : !spend.CanPrice ? Theme.Caution
            : Theme.TextFaint;

        _missionState.Text = MissionSentence(status);
        _missionState.Foreground = status.State switch
        {
            MissionState.Working => Theme.Positive,
            MissionState.Waiting => Theme.TextMuted,
            MissionState.Paused => Theme.Caution,
            _ => Theme.TextFaint
        };

        _missionCounts.Text = MissionCounts(status);
        _missionCounts.Foreground = status.ConsecutiveErrors > 0 ? Theme.Danger : Theme.TextFaint;

        _missionLast.Text = status.LastTurnFirstLine is { Length: > 0 } line ? Shorten(line, 160) : "";
        _missionLast.IsVisible = _missionLast.Text.Length > 0;
    }

    /// <summary>
    /// THE ONE LINE THE CARD SAYS THE AI IS DOING. Pulled out of the repaint so the four words can be
    /// read back without a running app — the layout and the colours cannot be, and are not claimed.
    /// A waiting loop names the minute it will start again, because "waiting" alone is what an owner
    /// reads as "stuck".
    /// </summary>
    internal static string MissionSentence(MissionStatus status) => status.State switch
    {
        MissionState.Working => "working",
        MissionState.Waiting => status.NextTurnAt is { } at ? $"waiting until {at.ToLocalTime():HH:mm}" : "waiting",
        MissionState.Paused => "paused",
        _ => "stopped — the AI has not been started"
    };

    /// <summary>
    /// How much it has done, and whether it is getting anywhere. The error count is spelled rather
    /// than shown as a bare number, because "3" beside "12 turns" reads as a second turn count.
    /// </summary>
    internal static string MissionCounts(MissionStatus status)
    {
        var counts = status.Turns == 1 ? "1 turn" : $"{status.Turns} turns";
        return status.ConsecutiveErrors switch
        {
            0 => counts,
            1 => counts + " — the last one ended in an error",
            var n => counts + $" — {n} errors in a row"
        };
    }

    /// <summary>
    /// WHAT THE AI HAS COST TODAY, AGAINST THE CEILING IT STOPS AT. Pulled out of the repaint so the
    /// sentence can be read back without a running app, exactly as the four state words are.
    ///
    /// Three readings, and the third is the one that has to be loud. An installation TradeAgent
    /// cannot price shows no figure at all — not "0.00 of 5.00", which would be a limit that is
    /// holding nothing back drawn as one that is — and says what to correct.
    /// </summary>
    internal static string MissionCost(AiSpendToday spend)
    {
        if (!spend.Metered) return "";

        var cap = MissionSituation.Money(spend.Cap, spend.Currency);

        if (!spend.CanPrice)
            return $"Cost today: unknown — {spend.WhyNoPrice}. Your {cap} daily limit cannot stop it.";

        var line = $"Cost today: {MissionSituation.Money(spend.Spent, spend.Currency)} of {cap}";
        // Where the figure came from, beside the figure. An owner cannot tell an upper bound from a
        // bill by looking at it, and the difference decides whether they should go and correct it —
        // so the third reading is the one where they already have: their own rate, named as theirs,
        // because a surprising total priced by them is a different thing to go and look at.
        if (spend.PricedByOwner) line += $" — {Labels.PricedByYou}";
        else if (spend.Estimated is { } estimated) line += $" — {estimated}";
        if (spend.UnpricedTurns > 0) line += $" — {spend.UnpricedTurns} turns could not be priced, so it is at least that";
        if (spend.CapReached) line += $". The limit is reached; the AI starts again at {spend.ResumesAt:HH:mm}";
        return line + ".";
    }

    static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    /// <summary>Stops the loop, and says why if it will not stop.</summary>
    async Task PauseMissionAsync()
    {
        try { await _host.PauseTheAiAsync(); }
        catch (Exception ex) { Ui.ReportError?.Invoke(ex.Message); }
    }

    /// <summary>
    /// Saves the guidance. ONE press: text that steers what the AI spends its time on takes no
    /// authority and gives none, so asking twice here would teach the owner to click through the
    /// confirmations that do matter.
    /// </summary>
    void SaveGuidance()
    {
        var text = _guidance.Text?.Trim() ?? "";
        _host.Gateway.Update(s => s.Guidance = text);
        _host.Gateway.Log.Activity(text.Length == 0
            ? "Your guidance for the AI was cleared"
            : "Your guidance for the AI was saved");
    }

    void RefreshApprovals(IReadOnlyList<ExecutionRequest> waiting)
    {
        var signature = string.Join('|', waiting.Select(w => w.RequestId));
        if (signature == _approvalSignature) return;
        _approvalSignature = signature;

        _approvals.Children.Clear();
        _approvalsCard.IsVisible = waiting.Count > 0;

        var first = true;
        foreach (var w in waiting)
        {
            var id = w.RequestId;
            var row = Ui.Col(Theme.S2,
                new TextBlock
                {
                    Text = TryDescribe(w), FontFamily = Theme.Mono, FontSize = Theme.Base,
                    FontWeight = FontWeight.SemiBold, Foreground = Theme.Text, TextWrapping = TextWrapping.Wrap
                },
                // The limit is inclusive and nothing sweeps: at the deadline the order is already too
                // old to approve, and pressing Approve from then on declines it rather than sending it.
                // "approve by HH:mm, after that it is declined" got both halves slightly wrong.
                Ui.Micro($"asked at {w.CreatedAt.ToLocalTime():HH:mm} — approve before "
                         + $"{(w.CreatedAt + _host.Gateway.ApprovalTtl).ToLocalTime():HH:mm}; from then, approving declines it instead"),
                Ui.With(Ui.Row(Theme.S2,
                        Ui.Confirm("Approve", "Confirm: place this order",
                            () => ApproveAsync(id)),
                        Ui.Secondary("Decline", () => _host.Gateway.Decline(id))),
                    r => r.Margin = new Thickness(0, Theme.S2, 0, 0)));

            if (!first)
            {
                row.Margin = new Thickness(0, Theme.S3, 0, 0);
                _approvals.Children.Add(new Border { Height = 1, Background = Theme.Caution, Opacity = 0.35 });
            }
            first = false;
            _approvals.Children.Add(row);
        }
    }

    /// <summary>
    /// The gateway authorizes an approval at the moment it is given, so this press can be refused
    /// for a reason that did not exist when the AI asked — the kill switch, a mode change, a dead
    /// connection, a stale price, a limit used up, or the request simply being too old. The
    /// two-step button's own catch shows only <c>ex.Message</c>, because GatewayDeniedException is
    /// not a TradeAgentException; the plain-language explanation and the repair are what a
    /// nontechnical owner actually needs, so they are reported here, with the detail after them.
    /// </summary>
    async Task ApproveAsync(string requestId)
    {
        try { await _host.Gateway.ApproveAsync(requestId); }
        catch (GatewayDeniedException ex)
        {
            Ui.ReportError?.Invoke($"{ex.Info.UserMessage} {ex.Info.Repair} ({ex.Message})");
        }
    }

    // ---- orders TradeAgent could not confirm ---------------------------------------------------

    /// <summary>
    /// One unconfirmed request on screen. The controls that CHANGE are held here so the card can be
    /// updated in place: the background loop reconciles every five seconds while anything is
    /// flagged, and a rebuild on that tick would wipe the note the user is halfway through typing
    /// and silently disarm a half-pressed confirmation. Only the set of request ids can force a
    /// rebuild — everything else is written into these fields.
    /// </summary>
    sealed class UnconfirmedRow
    {
        public required string RequestId { get; init; }
        public required TextBlock State { get; init; }
        public required TextBlock BrokerId { get; init; }
        public required TextBlock LastCheck { get; init; }
        /// <summary>Only on a press row: which press this came from, and what is on the account now.</summary>
        public TextBlock? Press { get; init; }

        /// <summary>
        /// The two halves of the bottom of the row, and exactly one of them is on screen.
        ///
        /// <see cref="Answer"/> is the note box and the two assertions. <see cref="OnTheWire"/> is
        /// the sentence that replaces them while a dispatcher of this process is still inside the
        /// connector call for this request: there is nothing for the owner to have seen yet, so the
        /// buttons would be asserting an outcome that does not exist. The lease is asked every tick
        /// rather than at build time because it EXPIRES — the row's id does not change when the
        /// dispatch ends, so nothing else would rebuild this row, and the owner would be left
        /// looking at a refusal that had stopped being true.
        /// </summary>
        public required Control Answer { get; init; }
        public required TextBlock OnTheWire { get; init; }
        public required IReadOnlyList<Button> Buttons { get; init; }
    }

    /// <summary>
    /// THE OTHER HALF OF WHAT A PRESS DID, and it is not in the record.
    ///
    /// An emergency press writes one flagged row per target and pauses trading until the owner has
    /// read them. A row says what the platform answered about the ORDER — "the platform filled it",
    /// "it is still working" — and the question the owner actually has is whether the position is
    /// gone. Those are different facts and a close that filled over a position that is still 2 long
    /// is exactly the case worth seeing. So the account is read alongside, off the account the
    /// RECORDS carry, and written into the row in place.
    ///
    /// Off the UI thread and fire-and-forget: it is a connector round trip on a five-second tick,
    /// and a card that blocks the dashboard to draw a line about a position is worse than one that
    /// fills the line in a moment later. Until it lands the row simply says nothing extra.
    /// </summary>
    void RefreshPressFacts(IReadOnlyList<ExecutionRequest> pending)
    {
        var kinds = pending.Where(p => TradingGateway.IsPressRecord(p.RequestId))
            .Select(p => TradingGateway.PressKindOf(p.RequestId)).Distinct().ToList();
        if (kinds.Count == 0) return;

        _ = Task.Run(async () =>
        {
            var lines = new Dictionary<string, string>();
            foreach (var kind in kinds)
            {
                try
                {
                    if (await _host.Gateway.OpenPressAsync(kind) is not { } press) continue;
                    foreach (var t in press.Targets)
                        lines[t.RequestId] =
                            $"{(kind == TradingGateway.ClosePress ? "Close all positions" : "Cancel all working orders")}"
                            + $" — pressed {press.SentAt.ToLocalTime():HH:mm} — {t.Outcome}"
                            + (t.PositionNow is { } q ? $"; {t.Target} is now {(q == 0m ? "flat" : q.ToString())}" : "");
                }
                catch (Exception) { /* the line is decoration; the flag is what pauses trading */ }
            }
            if (lines.Count == 0) return;
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var row in _unconfirmedRows)
                    if (row.Press is { } p && lines.TryGetValue(row.RequestId, out var text)) p.Text = text;
            });
        });
    }

    void RefreshUnconfirmed(IReadOnlyList<ExecutionRequest> pending)
    {
        var signature = string.Join('|', pending.Select(p => p.RequestId));
        if (signature != _unconfirmedSignature)
        {
            _unconfirmedSignature = signature;
            _unconfirmed.Children.Clear();
            _unconfirmedRows.Clear();
            _unconfirmedCard.IsVisible = pending.Count > 0;

            var first = true;
            foreach (var p in pending)
            {
                if (!first) _unconfirmed.Children.Add(new Border { Height = 1, Background = Theme.Danger, Opacity = 0.35 });
                first = false;
                _unconfirmed.Children.Add(BuildUnconfirmedRow(p));
            }
        }

        // In place, every tick: a reconcile pass moves UNKNOWN to RECONCILING and a late stream
        // event can fill in the broker's id, and the user watching the card should see that happen.
        foreach (var row in _unconfirmedRows)
        {
            var r = pending.FirstOrDefault(p => p.RequestId == row.RequestId);
            if (r is null) continue;
            row.State.Text = StateSentence(r.State);
            row.BrokerId.Text = r.ConnectorOrderId ?? "none — the broker never sent one back";
            row.LastCheck.Text = LastCheckSentence(r);

            // THE CARD ASKS THE LEASE, and it is the only surface that has to. `Unreconciled()`
            // deliberately still lists a row a dispatcher is inside the connector call for — it is
            // unconfirmed work and it keeps trading paused — but the two buttons on it assert what
            // the owner saw in ATAS, and while the order can still reach the broker there is nothing
            // there to have seen. `ForceResolve` refuses such a row for the same reason; this is
            // what stops the owner meeting that refusal as an error after pressing twice.
            var wire = _host.Gateway.StillOnTheWire(row.RequestId);
            row.OnTheWire.Text = wire is null ? "" :
                $"TradeAgent is still sending this order — {wire}. Wait for it to answer: it can still "
                + "reach the broker, so what ATAS shows right now is not its outcome.";
            row.OnTheWire.IsVisible = wire is not null;
            row.Answer.IsVisible = wire is null;

            // A confirmation half-pressed before the lease appeared must not survive to be completed
            // under it. The same reasoning as editing the note: what the press meant has changed.
            if (wire is not null) foreach (var b in row.Buttons) Ui.DisarmConfirm(b);
        }

        RefreshPressFacts(pending);
    }

    Control BuildUnconfirmedRow(ExecutionRequest r)
    {
        var id = r.RequestId;
        var isPress = TradingGateway.IsPressRecord(id);
        var state = Fact("TradeAgent thinks", StateSentence(r.State), mono: false);
        var brokerId = Fact("Broker reference", r.ConnectorOrderId ?? "none — the broker never sent one back", mono: true);
        var lastCheck = Fact("Last check", LastCheckSentence(r), mono: false);
        var press = isPress ? Fact("Emergency press", "reading the account…", mono: false) : default;

        // What stands where the note and the buttons stand while a dispatcher of this process is
        // still inside the connector call for this request. Written by the tick below, never here:
        // the lease can expire while this row is on screen, and this row is built once.
        var onTheWire = new TextBlock
        {
            FontSize = Theme.Small, Foreground = Theme.Caution, TextWrapping = TextWrapping.Wrap,
            IsVisible = false, Margin = new Thickness(0, Theme.S2, 0, 0)
        };

        // Required. ForceResolve writes the note onto the record and logs it at warn, which is the
        // only durable trace of a human overriding the machine — an empty one turns a loud log into
        // a blank one, so the confirmations below stay disabled until something is typed.
        var note = new TextBox
        {
            PlaceholderText = "What you saw in ATAS — required",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, Theme.S2, 0, 0)
        };

        var buttons = new List<Button>();
        if (SpokenByThePlatform(r.State))
        {
            // A record the event stream already settled, flagged afterwards because the dispatch
            // that wrote it never got an answer. Terminal states have no outgoing edges, so the
            // ONLY answer that can be given about one is whether the state it already holds is
            // true — ForceResolve takes that as finalState == current state and clears the flag
            // without rewriting the record. Asserting a DIFFERENT outcome is refused there on
            // purpose, and rightly: that is the stream and the platform disagreeing, which is
            // something to investigate rather than to overwrite. So one button, not two.
            var settled = r.State;
            var tense = OrderStateMachine.IsTerminal(settled) ? "was" : "is";
            buttons.Add(Ui.Confirm($"Our record is right — it {tense} {Word(settled)}",
                $"Confirm: I checked in ATAS and this order {tense} {Word(settled)}",
                () => ResolveAsync(id, settled, note)));
        }
        else
        {
            // FILLED and CANCELLED, and nothing else, because they are the only two outcomes
            // OrderStateMachine lets ForceResolve reach from EVERY state a flagged request can hold.
            // "Still working" is the obvious third answer and is unreachable from WORKING,
            // PARTIALLY_FILLED and CANCEL_PENDING — a button that throws on the states where it is
            // most likely to be the true answer is worse than no button, so the card asks the user
            // to cancel it in ATAS first instead.
            buttons.Add(Ui.Confirm("It was filled", "Confirm: I checked in ATAS and this order was filled",
                () => ResolveAsync(id, ExecutionState.FILLED, note)));
            buttons.Add(Ui.Confirm("No order exists", "Confirm: I checked in ATAS and no such order exists",
                () => ResolveAsync(id, ExecutionState.CANCELLED, note)));
        }

        // Stacked, not in a row. An armed two-step button carries its whole sentence — "Confirm: I
        // checked in ATAS and this order was filled" is about 340px — and two of those beside each
        // other overflow a card that shares its page with the 340px health column. A horizontal
        // StackPanel does not wrap, it clips, so the second choice would simply not be there.
        foreach (var b in buttons)
        {
            b.IsEnabled = false;
            b.HorizontalAlignment = HorizontalAlignment.Left;
        }
        note.TextChanged += (_, _) =>
        {
            var armed = !string.IsNullOrWhiteSpace(note.Text);
            // Editing the note changes the assertion, so a confirmation armed against the old words
            // must not survive to be completed against the new ones.
            foreach (var b in buttons) { Ui.DisarmConfirm(b); b.IsEnabled = armed; }
        };

        var answer = Ui.Col(Theme.S2,
            note,
            Ui.With(Ui.Col(Theme.S2, [.. buttons]), c => c.Margin = new Thickness(0, Theme.S2, 0, 0)));

        _unconfirmedRows.Add(new UnconfirmedRow
        {
            RequestId = id, State = state.Value, BrokerId = brokerId.Value, LastCheck = lastCheck.Value,
            Press = isPress ? press.Value : null,
            Answer = answer, OnTheWire = onTheWire, Buttons = buttons
        });

        var row = Ui.Col(Theme.S2,
            new TextBlock
            {
                Text = TryDescribe(r), FontFamily = Theme.Mono, FontSize = Theme.Base,
                FontWeight = FontWeight.SemiBold, Foreground = Theme.Text, TextWrapping = TextWrapping.Wrap
            },
            Ui.With(Ui.Col(0, [
                    .. isPress ? new[] { press.Root } : [],
                    state.Root,
                    Fact("Sent", (r.DispatchedAt ?? r.CreatedAt).ToLocalTime().ToString("d MMM, HH:mm:ss"), mono: true).Root,
                    Fact("Our reference", r.ClientOrderId, mono: true).Root,
                    brokerId.Root,
                    lastCheck.Root]),
                c => c.Margin = new Thickness(0, Theme.S2, 0, 0)),
            onTheWire,
            answer);

        if (_unconfirmed.Children.Count > 0) row.Margin = new Thickness(0, Theme.S3, 0, 0);
        return row;
    }

    /// <summary>
    /// The override itself. RefreshHealthAsync is not decoration: ForceResolve clears the
    /// needs-reconciliation flag and nothing else, while TryAuthorizeExecution ALSO requires the
    /// ExecutionCapability health row to be READY — and that row was set PAUSED by the failed
    /// dispatch and by the reconciler. Without this second call the user presses the button, sees
    /// "AI trading — paused" stay on screen for up to five seconds until the background tick
    /// recomputes health, and reasonably concludes the button does nothing.
    /// </summary>
    async Task ResolveAsync(string requestId, ExecutionState outcome, TextBox note)
    {
        var text = (note.Text ?? "").Trim();
        if (text.Length == 0) { Ui.ReportError?.Invoke("Say what you saw in ATAS before confirming."); return; }

        _host.Gateway.ForceResolve(requestId, outcome, text);
        await _host.Gateway.RefreshHealthAsync();
    }

    static string StateSentence(ExecutionState s) => s switch
    {
        ExecutionState.UNKNOWN => "it does not know — the answer never came back",
        ExecutionState.RECONCILING => "it does not know — it is still trying to find out",
        ExecutionState.DISPATCHING => "the order was being sent when the connection went",
        ExecutionState.FILLED => "filled, but never confirmed with the broker",
        ExecutionState.CANCELLED => "cancelled, but never confirmed with the broker",
        ExecutionState.REJECTED => "refused, but never confirmed with the broker",
        _ => $"{s.ToString().ToLowerInvariant().Replace('_', ' ')}, but never confirmed with the broker"
    };

    /// <summary>
    /// Why this is still on screen. On a backend that cannot prove its own order history — ATAS,
    /// permanently, because it exposes no order lookup — the reconciler says so and stops, and the
    /// user needs to read that rather than wait for a check that is never going to conclude.
    /// </summary>
    string LastCheckSentence(ExecutionRequest r)
    {
        var why = _host.Gateway.Connector.Capabilities.ReconciliationProvable
            ? "TradeAgent is still asking the broker about this order."
            : $"{_host.Gateway.Connector.DisplayName} cannot prove order state; this needs a human to look.";
        return string.IsNullOrWhiteSpace(r.LastError) ? why : $"{why} What went wrong: {r.LastError}";
    }

    /// <summary>
    /// A labelled fact whose value the caller keeps, so the card can be updated without rebuilding.
    /// NOTHING HERE TRIMS. A client order id, a broker id and a reconcile message are all long, and
    /// an ellipsis through the middle of the reference the user is about to search for in ATAS is
    /// the failure StatusRow already carries a comment about.
    /// </summary>
    static (Control Root, TextBlock Value) Fact(string key, string value, bool mono)
    {
        var v = new TextBlock
        {
            Text = value, FontSize = Theme.Small, Foreground = Theme.Text,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top,
            [Grid.ColumnProperty] = 1
        };
        if (mono) v.FontFamily = Theme.Mono;

        // 140 matches StatusRow: the longest label here is "Broker reference", and the pixels saved
        // go to the half of the row that actually varies.
        var root = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("140,*"),
            Margin = new Thickness(0, 3),
            Children =
            {
                new TextBlock
                {
                    Text = key, FontSize = Theme.Small, Foreground = Theme.TextMuted,
                    TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top
                },
                v
            }
        };
        return (root, v);
    }

    /// <summary>
    /// A STATE ONLY THE PLATFORM'S OWN ANSWER CAN HAVE PRODUCED — so the honest thing to ask about
    /// it is whether it is still true, not what it "really" was.
    ///
    /// This used to be <c>OrderStateMachine.IsTerminal</c>, and that was right while the only
    /// flagged records were failures. An emergency press flags EVERY row it writes, including the
    /// ones where nothing went wrong, so the card now regularly holds a close the platform answered
    /// WORKING — and the two buttons offered for a non-terminal record ("It was filled", "No order
    /// exists") are both false about it. <c>ForceResolve</c> takes an assertion equal to the stored
    /// state on any state at all, clearing the flag without rewriting the record, so agreeing with
    /// the platform is the one answer that is always available and always true.
    /// </summary>
    static bool SpokenByThePlatform(ExecutionState s) => s is
        ExecutionState.FILLED or ExecutionState.CANCELLED or ExecutionState.REJECTED or
        ExecutionState.WORKING or ExecutionState.ACKNOWLEDGED or
        ExecutionState.PARTIALLY_FILLED or ExecutionState.CANCEL_PENDING;

    static string Word(ExecutionState s) => s switch
    {
        ExecutionState.FILLED => "filled",
        ExecutionState.CANCELLED => "cancelled",
        ExecutionState.REJECTED => "refused by the broker",
        _ => s.ToString().ToLowerInvariant().Replace('_', ' ')
    };

    static string TryDescribe(ExecutionRequest r)
    {
        try
        {
            // Only a PLACE carries a PlaceIntent. A cancel or a modify stores a different shape, and
            // reading one as the other produces a confident sentence about an order nobody asked for.
            if (r.Intent != RequestIntent.PLACE) return $"{r.Intent} {r.Instrument}".Trim();

            var i = Json.Read<PlaceIntent>(r.ParametersJson);
            if (i is null) return $"{r.Intent} {r.Instrument}";
            var price = i.LimitPrice is { } lp ? $" at {lp}" : " at market";
            return $"{i.Side} {i.Quantity} {i.Symbol}{(i.Type == OrderType.Market ? " at market" : price)}";
        }
        catch (Exception) { return $"{r.Intent} {r.Instrument}"; }
    }
}

// =================================================================================================

/// <summary>
/// Mode, limits and the emergency controls. Everything that can widen the AI's authority is a
/// deliberate two-step; everything that narrows it is one press.
/// </summary>
sealed class SafetyPage
{
    readonly AppHost _host;

    readonly Panel _modeRow;
    readonly TextBlock _modeNote = Ui.Muted("");
    readonly TextBlock _liveNote = Ui.Body("");
    readonly Button _liveButton;
    readonly Button _stopButton;
    readonly NumericUpDown _maxQty, _maxNotional, _maxPositions, _maxPerMinute;
    readonly NumericUpDown _maxLossPerTrade, _maxDailyLoss;

    /// <summary>
    /// The two loss hints, kept because they name the ACCOUNT'S currency and the account has not
    /// answered when this page is built. <see cref="Update"/> writes it in when it has.
    /// </summary>
    readonly TextBlock _tradeLossHint = Ui.Micro(Labels.LossBudgetHint());
    readonly TextBlock _dailyLossHint = Ui.Micro(Labels.LossBudgetHint());
    readonly NumericUpDown _dailyCap;
    readonly TextBlock _capNote = Ui.Micro("");
    readonly NumericUpDown _priceIn, _priceOut;
    readonly TextBlock _priceNote = Ui.Micro("");
    readonly TextBox _allowlist;
    readonly TextBlock _limitsNote = Ui.Micro("");
    readonly Border _unreadableCard;

    public Control Root { get; }


    /// <summary>
    /// Runs one press of an emergency control. ONE SHOT, AND THE OWNER IS TOLD WHAT IT DID.
    ///
    /// There is no press object here any more and no retry. The gateway writes one flagged record
    /// per target before it touches the wire, sends the calls, and from that moment refuses to let
    /// the AI trade until the owner has resolved those records on the Dashboard; a second press
    /// while one is open is REFUSED by the gateway, and its refusal — "close-all sent at HH:MM;
    /// resolve it first" — is the sentence shown here.
    ///
    /// The screen holding the press was the source of six separate faults, and every one of them
    /// came from the same idea: that a button could keep track of an emergency across a restart, a
    /// second window and a definite failure. It cannot, and the records already do.
    /// </summary>
    async Task PressAsync(Func<Task<TradingGateway.PressOutcome>> run, string what)
    {
        try
        {
            var outcome = await run();
            Ui.ReportError?.Invoke(outcome.Complete
                ? $"{what}: {outcome.Summary}"
                : $"{what}: {outcome.Summary} AI trading is paused until you confirm each line on the Dashboard.");
        }
        catch (GatewayDeniedException ex)
        {
            Ui.ReportError?.Invoke($"{ex.Info.UserMessage} {ex.Message}. {ex.Info.Repair}");
        }
        catch (Exception ex)
        {
            // The gateway latches the pause in memory before it writes anything, so an exception on
            // the way out does not mean nothing was sent — it means the Dashboard is the place to
            // find out what was.
            Ui.ReportError?.Invoke($"{what} did not finish: {ex.Message}. Check the Dashboard for what it did send.");
        }
    }

    /// <summary>
    /// THE MODE ROW, AND WHICH OF ITS BUTTONS ASK TWICE.
    ///
    /// Static and handed the one thing it does, so the row a test presses is the row the page
    /// builds. One press here used to move a live-activated installation from "Real, ask me first"
    /// to "Real, fully automatic" — the AI's next order reached the broker with nobody approving
    /// it, from a button sitting beside two that already asked twice (REVIEW 2026-09-05b finding 3,
    /// probe P1). Watch only and Practice stay one press: they only ever reduce.
    /// </summary>
    internal static Panel BuildModeRow(Action<TradingMode> setMode)
    {
        // A ROW THAT WRAPS, NOT A HORIZONTAL STACK. An armed real-money button carries a whole
        // sentence — "Confirm: let the AI place real orders without asking" — three times the width
        // of the label it replaces. In a StackPanel the row ran past its card and the sentence was
        // clipped mid-word on the running app ("Confirm: let the AI place real or"), so the one
        // control whose entire purpose is to say what the second press does could not be read.
        var row = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = Theme.S2,
            LineSpacing = Theme.S2
        };
        foreach (var mode in Enum.GetValues<TradingMode>())
        {
            var m = mode;
            row.Children.Add(Ui.ModeButton(m, () => setMode(m)));
        }
        return row;
    }

    /// <summary>
    /// The emergency toggle as this page wears it: the one saturated fill in the app, red while the
    /// AI may trade, green while it may not, and red again the moment RESUME is half-pressed. The
    /// fill is a local brush, so it is registered rather than assigned — see <see cref="Ui.Repaints"/>.
    /// </summary>
    internal static Button BuildKillSwitch(Func<bool> stopped, Action stop, Action resume)
    {
        var b = Ui.KillSwitch("emergency", stopped, stop, resume);
        Ui.Repaints(b, (btn, armed) =>
        {
            // THE FOREGROUND IS PART OF THE REPAINT, NOT JUST THE FILL. Arming swaps the control to
            // the "danger" class, whose foreground is red — and this button's fill is red too, so
            // the armed sentence was painted red on red and the half-pressed RESUME read as a blank
            // red block on the running app. The emergency variant keeps its own look while armed.
            btn.Background = armed || !stopped() ? Theme.Danger : Theme.Positive;
            btn.Foreground = Theme.TextOnEmergency;
        });
        return b;
    }

    /// <summary>
    /// The limits save. A cap RAISED is a grant — the same act as choosing a real-money mode, done
    /// with a number — so a save that widens anything asks twice and names what it widens, while a
    /// save that only narrows stays one press (Codex F12). The comparison itself is
    /// <see cref="RiskPolicy.Widenings"/>, in Core, where "wider" is not "larger" on every field.
    /// </summary>
    internal static Button BuildSaveLimits(Func<RiskPolicy> current, Func<RiskPolicy> pending, Action save)
    {
        var b = Ui.ConfirmIf(Labels.SaveLimits, () =>
        {
            var wider = RiskPolicy.Widenings(current(), pending());
            return wider.Count == 0 ? null : Labels.WidenLimitsArmed(wider);
        }, save, "primary");
        b.HorizontalAlignment = HorizontalAlignment.Left;
        return b;
    }

    /// <summary>
    /// THE AI'S OWN DAILY CEILING. Raising it asks twice; lowering it saves in one press.
    ///
    /// It is the Safety page's rule for a widened limit, applied to the one limit on this page that
    /// is not about orders: turns run back to back for as long as the machine is on, so raising this
    /// number is granting the AI more of the owner's money, and that is the same act as raising a
    /// quantity cap even though nothing here reaches a broker.
    ///
    /// A factory, like the three beside it, so a test presses the control the owner sees.
    /// </summary>
    internal static Button BuildSaveDailyCap(Func<decimal> current, Func<decimal> pending,
        Func<string> currency, Action save)
    {
        var b = Ui.ConfirmIf(Labels.SaveDailyCap,
            () => pending() > current()
                ? Labels.RaiseDailyCapArmed(MissionSituation.Money(pending(), currency()))
                : null,
            save, "primary");
        b.HorizontalAlignment = HorizontalAlignment.Left;
        return b;
    }

    /// <summary>
    /// THE PRESS THAT WRITES WHAT THE AI'S WORK IS PRICED AT.
    ///
    /// Pulled out of the page for the same reason <see cref="BuildSaveDailyCap"/> is: the rule about
    /// which direction asks twice is the whole of what this control is, and a rule that can only be
    /// exercised by running the app is a rule nobody is checking.
    /// </summary>
    internal static Button BuildSaveAiPrice(Func<(decimal In, decimal Out)> current,
        Func<(decimal In, decimal Out)> pending, Func<string> currency, Action save)
    {
        var b = Ui.ConfirmIf(Labels.SaveAiPrice,
            // EITHER half going down, not both: a rate that halves only the output price buys the AI
            // more turns under the same ceiling just as surely as one that halves both, and editing
            // one box is the ordinary way this is used.
            () => pending() is var p && (p.In < current().In || p.Out < current().Out)
                ? Labels.LowerAiPriceArmed(Rate(p, currency()))
                : null,
            save, "primary");
        b.HorizontalAlignment = HorizontalAlignment.Left;
        return b;
    }

    /// <summary>Both halves of the rate, as one phrase for the armed sentence to name.</summary>
    static string Rate((decimal In, decimal Out) rate, string currency) =>
        $"{MissionSituation.Money(rate.In, currency)} in and {MissionSituation.Money(rate.Out, currency)} out";

    public SafetyPage(AppHost host)
    {
        _host = host;

        _modeRow = BuildModeRow(m => _host.Gateway.SetMode(m));

        _liveButton = Ui.Confirm("Switch real-money trading ON", "Confirm: allow real money",
            () => _host.Gateway.ActivateLive(!_host.Gateway.Settings.LiveActivated));
        _liveButton.HorizontalAlignment = HorizontalAlignment.Left;

        var modeCard = Ui.Section("Trading mode", Ui.Col(Theme.S4,
            _modeRow,
            _modeNote,
            Ui.Divider(),
            _liveNote,
            _liveButton));

        // The emergency block. STOP is one press because a mis-press that removes the AI's
        // permission costs nothing and hesitation here costs money; RESUME is two, because that
        // direction gives the permission back — to something that trades real money.
        _stopButton = BuildKillSwitch(
            () => _host.Gateway.Settings.AiTradingStopped,
            () => _host.Gateway.StopAiTrading($"you pressed {Labels.StopAiTrading}"),
            () => _host.Gateway.EnableAiTrading());

        var emergency = Ui.Section("Emergency", Ui.Col(Theme.S4,
            _stopButton,
            Ui.Muted("Stopping the AI removes its permission to trade. It does not touch your orders or positions. "
                + "Stopping takes one press; letting it trade again takes two."),
            Ui.Divider(),
            // ONE PRESS, ONE SET OF RECORDS, AND THEN A PERSON. The screen holds nothing about the
            // press: the gateway writes a flagged row per target before the wire, pauses trading on
            // them, and refuses the next press until they are resolved on the Dashboard. Holding a
            // nonce here so the button could "retry" is what this replaces — it made a definite
            // failure unpressable-past, and it survived a restart into a position that was not flat.
            Ui.With(Ui.Confirm("Cancel all working orders", "Confirm: cancel all working orders",
                    () => PressAsync(() => _host.Gateway.OperatorCancelAllAsync(), "Cancel all working orders")),
                b => b.HorizontalAlignment = HorizontalAlignment.Stretch),
            Ui.With(Ui.Confirm("Close all positions", "Confirm: close all positions with market orders",
                    () => PressAsync(() => _host.Gateway.OperatorCloseAllAsync(), "Close all positions")),
                b => b.HorizontalAlignment = HorizontalAlignment.Stretch)));
        emergency.Margin = new Thickness(Theme.S5, 0, 0, 0);

        var r = _host.Gateway.Settings.Risk;
        _maxQty = Ui.NumberField(r.MaxOrderQuantity, 0m, 1m);
        _maxNotional = Ui.NumberField(r.MaxNotionalPerOrder, 0m, 1000m);
        _maxPositions = Ui.NumberField(r.MaxOpenPositions);
        _maxPerMinute = Ui.NumberField(r.MaxOrdersPerMinute);
        _maxLossPerTrade = Ui.NumberField(r.MaxLossPerTrade, 0m, 50m);
        _maxDailyLoss = Ui.NumberField(r.MaxDailyLoss, 0m, 50m);
        // The placeholder is what an empty box MEANS, and an empty box now means nothing is allowed
        // rather than everything is. It said "any".
        _allowlist = Ui.TextField(string.Join(", ", r.InstrumentAllowlist), "none");

        _dailyCap = Ui.NumberField(_host.Gateway.Settings.AiDailyCostCap, 0m, 0.5m);

        // THE BOXES OPEN ON WHAT THE AI IS ACTUALLY BEING CHARGED, not on empty. An owner correcting
        // a rate has to be able to see the one in force to know whether it needs correcting, and the
        // rate in force with nothing saved is the dearest model in the running runtime's catalogue —
        // the same figure the estimate uses, from the same place, so the box and the bill agree.
        var shipped = _host.ShippedRate;
        var rate = ShownRate();
        _priceIn = Ui.NumberField(rate.In, 0m, 0.25m);
        _priceOut = Ui.NumberField(rate.Out, 0m, 1m);

        // ITS OWN SECTION, AND ITS OWN PRESS. This is not a risk limit: nothing here reaches a
        // broker, and RiskPolicy.Widenings — which decides whether the Save limits press asks twice
        // — has nothing to say about it. Folding it into that button would put a money ceiling
        // behind a comparison that cannot see it.
        var spending = Ui.Section("What the AI costs", Ui.Col(Theme.S2,
            Ui.Muted("The AI works non-stop, and every turn it takes is charged to whoever pays for its "
                + "AI tool. This is the most it may spend on itself in a day. Reaching it stops the AI "
                + "taking new turns until midnight; it does not touch your orders or positions."),
            Ui.Spacer(Theme.S2),
            Ui.FieldRow(Labels.DailyCostCap, _dailyCap,
                "0 stops it working at all. Raising this lets the AI spend more of your money, so it asks again first."),
            Ui.Spacer(Theme.S2),
            BuildSaveDailyCap(() => _host.Gateway.Settings.AiDailyCostCap, () => PendingCap(),
                () => _host.SpendToday.Currency, SaveDailyCap),
            _capNote,
            Ui.Divider(),
            // BESIDE THE CAP, because the two numbers are one arithmetic: the limit above is only
            // worth what the rate below says a turn costs. The owner never edits a file to set it —
            // costs.json is still there for an engineer and is not on any screen.
            Ui.Muted("TradeAgent works out what each turn cost from the tokens your AI tool reports. "
                + "If you know what you are actually charged, put it here and it is used instead."),
            Ui.Spacer(Theme.S2),
            Ui.FieldRow(Labels.AiPriceIn, _priceIn),
            Ui.FieldRow(Labels.AiPriceOut, _priceOut, ShippedRateHint(shipped)),
            Ui.Spacer(Theme.S2),
            BuildSaveAiPrice(ShownRate, PendingRate, () => _host.SpendToday.Currency, SaveAiPrice),
            _priceNote));

        var limits = Ui.Section("Safety limits", Ui.Col(Theme.S2,
            Ui.Muted("The AI cannot change these and has no command to ask. Small numbers are the point. "
                + "Lowering one saves in a press; raising one is a grant, so it asks again first."),
            Ui.Spacer(Theme.S2),
            Ui.FieldRow(Labels.MaxOrderQuantity, _maxQty),
            Ui.FieldRow(Labels.MaxNotionalPerOrder, _maxNotional,
                "0 means not enforced. For futures this is the right default — one contract is worth far more on paper than it costs to trade."),
            Ui.FieldRow(Labels.MaxOpenPositions, _maxPositions),
            Ui.FieldRow(Labels.MaxOrdersPerMinute, _maxPerMinute),
            // THE TWO THAT ARE ABOUT WHAT IS LOST RATHER THAN WHAT IS SENT. Every limit above them
            // bounds one order and none of them bounds a day: an agent may lose the account one
            // permitted order at a time and break nothing above this line.
            Ui.FieldRow(Labels.MaxLossPerTrade, _maxLossPerTrade, _tradeLossHint),
            Ui.FieldRow(Labels.MaxDailyLoss, _maxDailyLoss, _dailyLossHint),
            Ui.FieldRow(Labels.InstrumentAllowlist, _allowlist,
                "Comma separated. " + Labels.NoInstrumentAllowed),
            Ui.Spacer(Theme.S2),
            BuildSaveLimits(() => _host.Gateway.Settings.Risk, PendingLimits, SaveLimits),
            _limitsNote));

        // THE ONE SCREEN THAT REPAIRS AN UNREADABLE SETTINGS ROW SAYS SO, ABOVE EVERYTHING ELSE.
        //
        // The failure is invisible without this. The gateway refuses everything and the health row on
        // the Dashboard says why, but this page shows the boxes it is refusing on — zeros and an
        // empty allowlist — as though the owner had typed them, and the button that fixes it looks
        // like an ordinary save. It hides itself the moment the row is written again; see Update and
        // SaveLimits, which both ask the gateway rather than remembering an answer.
        _unreadableCard = new Border
        {
            Background = Theme.CautionSoft,
            BorderBrush = Theme.Caution,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.Radius,
            Padding = new Thickness(Theme.S5),
            IsVisible = false,
            Child = Ui.Col(Theme.S3,
                Ui.With(Ui.Eyebrow(Labels.SettingsCouldNotBeReadTitle), t => t.Foreground = Theme.Caution),
                Ui.Body(Labels.SettingsCouldNotBeReadBanner),
                Ui.Body(Labels.SettingsCouldNotBeReadNext, Theme.Caution))
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,340") };
        grid.Children.Add(Pages.Column(0, Ui.Col(Theme.S6, _unreadableCard, modeCard, limits, spending)));
        grid.Children.Add(Pages.Column(1, emergency));

        Root = Pages.Scroll(Ui.Col(0,
            Pages.Header("Safety", "What the AI is allowed to do, and how to take it away instantly."),
            grid));
    }

    public void Update(GatewayStatus status)
    {
        // Read from the gateway, not from `status`: GatewayStatus is the agent-facing shape and this
        // is a fact about the row this build read, not about what the AI may do.
        _unreadableCard.IsVisible = _host.Gateway.Settings.CouldNotBeRead;

        var i = 0;
        foreach (var mode in Enum.GetValues<TradingMode>())
            if (_modeRow.Children[i++] is Button b) Ui.Emphasise(b, status.Mode == mode);

        _modeNote.Text = status.Mode switch
        {
            TradingMode.OBSERVE => "The AI can read prices, positions and your account. It cannot place anything.",
            TradingMode.PAPER => "The AI trades against the practice simulator. No real money is involved.",
            TradingMode.LIVE_CONFIRM => "The AI proposes real orders and stops. Nothing reaches your broker until you approve it on the Dashboard.",
            TradingMode.LIVE_AUTONOMOUS => "The AI places real orders by itself, inside the safety limits below.",
            _ => ""
        };

        _liveNote.Text = status.LiveActivated
            ? "Real-money trading is switched ON."
            : "Real-money trading is switched OFF. The two real-money modes cannot reach your broker until you switch it on.";
        _liveNote.Foreground = status.LiveActivated ? Theme.Caution : Theme.TextMuted;

        Ui.Relabel(_liveButton,
            status.LiveActivated ? "Switch real-money trading OFF" : "Switch real-money trading ON",
            status.LiveActivated ? "Confirm: switch real money off" : "Confirm: allow real money");

        // The unit of the two loss budgets, once the platform has said what it is. Read from the
        // gateway rather than from `status`, which carries the limits but not the account's currency.
        _tradeLossHint.Text = _dailyLossHint.Text = Labels.LossBudgetHint(_host.Gateway.AccountCurrency);

        // Through SetResting, never by assigning Content: a half-pressed RESUME must survive the
        // five-second tick, and the fill it wears while armed is registered with the control.
        Ui.SetResting(_stopButton,
            status.AiTradingStopped ? Labels.ResumeAiTrading : Labels.StopAiTrading, "emergency");
    }

    /// <summary>
    /// The user guide and the agent's own AGENTS.md both said these were set in this window; until
    /// they were editable here they could only be changed by editing the database by hand.
    /// </summary>
    /// <summary>
    /// What the boxes on this page currently say, as the policy they would be saved as. The button
    /// asks this to decide whether the press is a grant, and <see cref="SaveLimits"/> writes it, so
    /// the values compared and the values written cannot be two different readings of the boxes.
    /// </summary>
    /// <summary>What the box says, as the number it would be saved as. The button compares this.</summary>
    decimal PendingCap() => _dailyCap.Value ?? _host.Gateway.Settings.AiDailyCostCap;

    /// <summary>
    /// THE RATE IN FORCE: the owner's own numbers where they have set them, and otherwise the
    /// shipped list price the estimate is charging.
    ///
    /// Zero on a runtime this build ships no list price for, because that is the truth about that
    /// installation — nothing is priced. Saving that pair back does NOT price the AI at nothing:
    /// <see cref="OwnerPrice.From"/> refuses a zero, so the one press an owner could make without
    /// touching either box leaves the cap exactly as it was.
    /// </summary>
    (decimal In, decimal Out) ShownRate()
    {
        var s = _host.Gateway.Settings;
        if (OwnerPrice.From(s) is { } mine) return (mine.InputPerMillion, mine.OutputPerMillion);
        return _host.ShippedRate is { } shipped
            ? (shipped.InputPerMillion, shipped.OutputPerMillion)
            : (0m, 0m);
    }

    /// <summary>What the two boxes say, as the rate they would be saved as. The button compares this.</summary>
    (decimal In, decimal Out) PendingRate()
    {
        var shown = ShownRate();
        return (_priceIn.Value ?? shown.In, _priceOut.Value ?? shown.Out);
    }

    /// <summary>
    /// The shipped default under the boxes, WITH ITS DATE AND THE PAGE IT CAME FROM. A price with no
    /// date is a price nobody can check, and the owner is the person who would check it.
    /// </summary>
    internal static string ShippedRateHint(ModelPrice? shipped) =>
        shipped is null
            ? "TradeAgent ships no list price for this AI tool, so nothing is assumed about what it costs."
            : $"TradeAgent uses {shipped.InputPerMillion} in and {shipped.OutputPerMillion} out — the "
              + $"dearest model {shipped.Model} on {shipped.Source}, read on {shipped.PricedAt}. A LOWER "
              + "price lets the AI take more turns before your daily limit stops it, so it asks again first.";

    /// <summary>
    /// Writes the rate. Both numbers together or neither: half a price applied to a whole turn would
    /// under-charge it, which is the one direction that lets the daily limit be walked past.
    /// </summary>
    void SaveAiPrice()
    {
        var (input, output) = PendingRate();
        _host.Gateway.Update(s =>
        {
            s.AiPriceInputPerMillion = input;
            s.AiPriceOutputPerMillion = output;
        });

        var currency = _host.SpendToday.Currency;
        _host.Gateway.Log.Activity($"The AI's work is now priced at {Rate((input, output), currency)} per million tokens");
        _priceNote.Text = $"Saved. Each turn is priced at {Rate((input, output), currency)} per million tokens.";
    }

    /// <summary>
    /// Writes the ceiling. The activity line is in the owner's words and names the figure, because
    /// this is the one setting whose effect they will meet later as an AI that stopped.
    /// </summary>
    void SaveDailyCap()
    {
        var cap = PendingCap();
        _host.Gateway.Update(s => s.AiDailyCostCap = cap);
        var money = MissionSituation.Money(cap, _host.SpendToday.Currency);
        _host.Gateway.Log.Activity($"The AI may now spend up to {money} a day");
        _capNote.Text = $"Saved. The AI may spend up to {money} a day.";
    }

    RiskPolicy PendingLimits()
    {
        var now = _host.Gateway.Settings.Risk;
        return new RiskPolicy
        {
            MaxOrderQuantity = _maxQty.Value ?? now.MaxOrderQuantity,
            MaxNotionalPerOrder = _maxNotional.Value ?? now.MaxNotionalPerOrder,
            MaxOpenPositions = (int)(_maxPositions.Value ?? now.MaxOpenPositions),
            MaxOrdersPerMinute = (int)(_maxPerMinute.Value ?? now.MaxOrdersPerMinute),
            MaxLossPerTrade = _maxLossPerTrade.Value ?? now.MaxLossPerTrade,
            MaxDailyLoss = _maxDailyLoss.Value ?? now.MaxDailyLoss,
            InstrumentAllowlist = (_allowlist.Text ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList()
        };
    }

    void SaveLimits()
    {
        var pending = PendingLimits();
        _host.Gateway.Update(s =>
        {
            s.Risk.MaxOrderQuantity = pending.MaxOrderQuantity;
            s.Risk.MaxNotionalPerOrder = pending.MaxNotionalPerOrder;
            s.Risk.MaxOpenPositions = pending.MaxOpenPositions;
            s.Risk.MaxOrdersPerMinute = pending.MaxOrdersPerMinute;
            s.Risk.MaxLossPerTrade = pending.MaxLossPerTrade;
            s.Risk.MaxDailyLoss = pending.MaxDailyLoss;
            s.Risk.InstrumentAllowlist = pending.InstrumentAllowlist;
        });
        _host.Gateway.Log.Activity("You changed the safety limits");

        // AN EMPTY BOX IS A DECISION AND IT IS SAID BACK. Clearing the list used to widen the AI's
        // authority to every instrument the platform offers, silently; it now removes all of it,
        // just as silently, unless this sentence says which one happened.
        var nothingAllowed = _host.Gateway.Settings.Risk.InstrumentAllowlist.Count == 0;
        _limitsNote.Text = nothingAllowed
            ? $"Saved. {Labels.NoInstrumentAllowed}"
            : "Saved. New orders are checked against these immediately.";
        _limitsNote.Foreground = nothingAllowed ? Theme.Caution : Theme.Positive;

        // The save above rewrote the row, so the warning stops HERE rather than up to five seconds
        // later on the refresh tick. Pressing the only button a warning names and watching nothing
        // change is how an owner concludes the software is broken and stops trying.
        _unreadableCard.IsVisible = _host.Gateway.Settings.CouldNotBeRead;
    }
}

// =================================================================================================

/// <summary>Everything that has happened, newest last, in one scroller that keeps its position.</summary>
sealed class ActivityPage
{
    readonly AppHost _host;
    readonly StackPanel _rows = new() { Spacing = Theme.S1 };
    readonly TextBlock _empty = Ui.Muted("Nothing has happened yet. Activity appears here as TradeAgent and the AI work.");
    readonly ScrollViewer _scroll;
    string _signature = "";

    public Control Root { get; }

    public ActivityPage(AppHost host)
    {
        _host = host;

        _scroll = new ScrollViewer
        {
            Content = Ui.Col(0, _empty, _rows),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };

        var card = Ui.Card(_scroll);
        card.ClipToBounds = true;
        card[Grid.RowProperty] = 1;

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(Pages.Header("Activity", "A plain-language record of what TradeAgent, the AI and you have done."));
        root.Children.Add(card);
        Root = root;
    }

    public void Update()
    {
        var activity = _host.Gateway.Log.RecentActivity(60);
        var signature = string.Join('|', activity.Select(a => $"{a.At.Ticks}:{a.Text}"));
        if (signature == _signature) return;
        _signature = signature;

        // The log is oldest-first, so the interesting end is the bottom. Follow it — but only for a
        // reader who was already there, so scrolling back through the morning is not undone by a
        // routine five-second refresh.
        var follow = _rows.Children.Count == 0
            || _scroll.Extent.Height <= _scroll.Viewport.Height
            || _scroll.Offset.Y >= _scroll.Extent.Height - _scroll.Viewport.Height - 32;

        _empty.IsVisible = activity.Count == 0;
        _rows.Children.Clear();
        DateTime? lastDay = null;
        foreach (var (at, level, text) in activity)
        {
            // A DAY SEPARATOR, BECAUSE EVERY ROW SHOWS A TIME AND NOTHING ELSE. This log spans days,
            // and on the one screen whose whole job is "what happened, and when", `16:00 Cancelled
            // order 12021602` could be an hour ago or last week. Seen on Windows 2026-09-01 with
            // three days of entries running together. A separator is preferred to a date on every
            // row: it keeps the narrow mono time column that makes the list scannable, and it puts
            // the anchor where the reader's eye already stops.
            var day = at.ToLocalTime().Date;
            if (lastDay != day)
            {
                if (lastDay is not null) _rows.Children.Add(Ui.With(Ui.Divider(),
                    d => d.Margin = new Thickness(0, Theme.S3, 0, Theme.S2)));
                _rows.Children.Add(Ui.With(Ui.Micro(DayLabel(day)),
                    t => t.Margin = new Thickness(0, lastDay is null ? 0 : 0, 0, Theme.S2)));
                lastDay = day;
            }

            var warn = level == "warn";
            _rows.Children.Add(new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("56,*"),
                Children =
                {
                    Ui.With(Ui.Mono(at.ToLocalTime().ToString("HH:mm"), Theme.TextFaint),
                        t => t.VerticalAlignment = VerticalAlignment.Top),
                    new TextBlock
                    {
                        Text = text, FontSize = Theme.Small, TextWrapping = TextWrapping.Wrap,
                        Foreground = warn ? Theme.Caution : Theme.TextMuted,
                        [Grid.ColumnProperty] = 1
                    }
                }
            });
        }

        if (follow) Dispatcher.UIThread.Post(_scroll.ScrollToEnd, DispatcherPriority.Background);
    }

    /// <summary>
    /// Plain language first, because this page is the one a non-technical owner reads. "Today" and
    /// "Yesterday" are what a person actually wants; the full date carries the year only when it is
    /// not the current one, so the common case stays short.
    /// </summary>
    static string DayLabel(DateTime day)
    {
        var today = DateTime.Now.Date;
        if (day == today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";
        return day.Year == today.Year
            ? day.ToString("dddd, d MMMM")
            : day.ToString("dddd, d MMMM yyyy");
    }
}

// =================================================================================================

/// <summary>
/// The self-check and the support package — the two things to do before asking for help — and the
/// one repair this page is allowed to perform.
///
/// The repair is here because this is where the bad news already is. The bridge row's own words
/// ("not installed in ATAS — press Reinstall the bridge on the Checks page") and the protocol
/// refusal both send the owner to this page, and until now the page printed those words and offered
/// nothing to press: the only Install bridge button in the product lives in the setup wizard, which
/// renders solely while onboarding is unfinished. So the sentence was true on the day it was written
/// and false for the entire life of the installation afterwards.
/// </summary>
sealed class ChecksPage
{
    readonly AppHost _host;
    readonly TextBlock _output = Ui.Body("");
    readonly TextBlock _placeholder =
        Ui.Muted("Nothing checked yet. Press Check everything and TradeAgent will test each part in turn.");
    readonly Button _showPackage;
    readonly Control _repair;
    string? _packagePath;

    public Control Root { get; }

    public ChecksPage(AppHost host)
    {
        _host = host;

        _showPackage = Ui.Ghost("Show the file", () =>
        {
            if (_packagePath is null) return;
            MainWindow.OpenPath(Path.GetDirectoryName(_packagePath) ?? Paths.Home, m => Ui.ReportError?.Invoke(m));
        });
        _showPackage.IsVisible = false;

        var buttons = Ui.Row(Theme.S2,
            Ui.Primary("Check everything", RunDoctorAsync),
            Ui.Secondary("Create support package", CreatePackage),
            _showPackage);

        _repair = Ui.Card(Ui.Col(Theme.S3, Ui.H3("The ATAS bridge"), BridgeRepair.Body(_host)));

        // Hidden until the bridge row asks for it. It is built here and only ever shown or hidden
        // afterwards: rebuilding it on the five-second tick would wipe a half-pressed confirmation
        // out from under the hand about to complete it.
        _repair.IsVisible = false;

        var well = new Border
        {
            Background = Theme.BgSunken,
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.Radius,
            Padding = new Thickness(Theme.S4),
            MinHeight = 140,
            ClipToBounds = true,
            Child = new ScrollViewer
            {
                Content = Ui.Col(0, _placeholder, _output),
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            }
        };
        well[Grid.RowProperty] = 1;
        _output.IsVisible = false;

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        body.Children.Add(Ui.With(Ui.Col(Theme.S3,
                Ui.Muted("These checks never change anything. The support package contains logs only — no passwords, no keys."),
                buttons,
                _repair),
            c => c.Margin = new Thickness(0, 0, 0, Theme.S4)));
        body.Children.Add(well);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(Pages.Header("Checks", "Test every part of TradeAgent, or package the logs for support."));
        root.Children.Add(Ui.With(body, c => c[Grid.RowProperty] = 1));
        Root = root;
    }

    /// <summary>
    /// Nothing here polls, and nothing here is rebuilt. The one thing that changes on its own is
    /// whether the bridge needs putting back, and that is a visibility flag on a control that was
    /// built once.
    /// </summary>
    public void Update() => _repair.IsVisible = _host.BridgeRepairOffered;

    async Task RunDoctorAsync()
    {
        Say("Checking…", Theme.TextMuted);
        var report = await _host.RunDoctorAsync();
        if (report.AllHealthy) { Say("Everything looks healthy.", Theme.Positive); return; }

        Say(string.Join('\n', report.Problems.Select(p =>
            $"• {p.Name}: {(string.IsNullOrWhiteSpace(p.Detail) ? StateWords(p.State) : p.Detail)}" +
            (string.IsNullOrWhiteSpace(p.UserAction) ? "" : $"\n    what to do: {p.UserAction}"))), Theme.Text);
    }

    /// <summary>
    /// A row with no detail still has to SAY something. The three agent rows carry no detail until
    /// the AI has been started, so they used to render as a bare "• Agent runtime" followed straight
    /// by "what to do: ...", which names a problem without ever stating one — worse than the
    /// dashboard, which at least prints "unknown". Seen on Windows 2026-09-01.
    ///
    /// This is the wording half only. The proper fix is a NOT_APPLICABLE health state so a component
    /// nobody is using stops being counted as a fault at all, and that touches `Doctor.AllHealthy`
    /// and the `trade status` wire — its own piece of work, still in the queue.
    /// </summary>
    static string StateWords(HealthState state) => state switch
    {
        HealthState.UNKNOWN  => "not checked yet",
        HealthState.STARTING => "starting up",
        HealthState.DEGRADED => "working, but not fully",
        HealthState.FAILED   => "not working",
        HealthState.PAUSED   => "paused",
        _                    => "ready"
    };

    void CreatePackage()
    {
        _packagePath = Diagnostics.Doctor.CreateSupportPackage(_host.Db);
        Say($"Saved to {_packagePath}", Theme.Text);
        _showPackage.IsVisible = true;
    }

    void Say(string text, IBrush brush)
    {
        _placeholder.IsVisible = false;
        _output.IsVisible = true;
        _output.Text = text;
        _output.Foreground = brush;
    }
}

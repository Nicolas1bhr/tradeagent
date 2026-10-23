using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TradeAgent.AgentRuntime;
using TradeAgent.ConnectorSdk;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;

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
    readonly Button _agentButton;

    readonly PerformanceCard _performance = new();
    readonly SeatSignInCard _signIn;

    /// <summary>The "AI sign-in" card on this page (<c>U-seat-signin</c>), for a test to read and press.</summary>
    internal SeatSignInCard SignIn => _signIn;
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

        if (!spend.CanPrice) return head + " — TradeAgent cannot price this AI, so your daily limit will not stop it";
        // BOTH GRANTS, EACH IN ITS OWN WORDS (U-billing-classes): real money, and the plan's capacity — which is not money.
        var line = head + $", up to {MissionSituation.Money(spend.Cap, spend.Currency)} a day"
                   + (spend.Class == BillingClasses.Plan ? " of your ChatGPT plan's capacity" : " of real money");
        return spend.PlanReading is { } plan
            ? line + $" and {MissionSituation.Money(plan.Cap, plan.Currency)} a day of your ChatGPT plan's capacity, counted at list price"
            : line;
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

        // NO CARD OF ORDERS WAITING ON THE OWNER (U-settings-shed item 4; R24 § 4.2, Q9): "Real, ask me first" was a press per live
        // order, and a press per order cannot become a board act — a board seat approving each order would be model judgement timing
        // it (SIGNAL-001). The live modes are the security board's, closed until it is active.

        // Orders TradeAgent could not confirm. This is the ONLY route into
        // TradingGateway.AnswerFromTheCardAsync, and through it into ForceResolve, anywhere in the
        // product: operator authority is deliberately absent from the agent-facing pipe, from the
        // trade CLI and from the GatewayHost console, so an agent that wants this
        // permission has nowhere to ask. Without this card, on a backend that cannot prove its own
        // order history — which is ATAS, permanently — the first ambiguous order pauses trading and
        // nothing in the app can ever start it again.
        //
        // It is the first card on the page because it outranks everything below it: while anything is unconfirmed,
        // TryAuthorizeExecution refuses.
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

        // THE SEAT HOME'S SIGN-IN, any time after onboarding (U-seat-signin): built once here, updated in place on the tick.
        _signIn = new SeatSignInCard(new SeatSignInSources
        {
            Service = host.SeatSignIn,
            Open = url => MainWindow.OpenPath(url, m => Ui.ReportError?.Invoke(m)),
            Activity = line => host.Gateway.Log.Activity(line),
            Ui = work => Dispatcher.UIThread.Post(work)
        });

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
                     + "it cannot give it permission to do anything the security board and you have not."),
            _guidance,
            Ui.With(Ui.Row(Theme.S2, Ui.Secondary("Save guidance", SaveGuidance)),
                r => r.Margin = new Thickness(0, Theme.S2, 0, 0)),
            Ui.With(_signIn.SignedIn, c => c.Margin = new Thickness(0, Theme.S4, 0, 0)));

        var left = Ui.Col(Theme.S6,
            _unconfirmedCard,
            _signIn.Root,
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

    public void Update(GatewayStatus status)
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
        _signIn.Update();
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

        _missionState.Text = MissionSentence(status, _host.AiNotStarted);
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
    ///
    /// AND IT NAMES WHAT IT IS WAITING FOR. A turn now happens because something happened, so a
    /// quiet card is the ordinary state of a quiet day — and an owner cannot tell that from a broken
    /// one without being told which. "waiting until 14:32 for the next scheduled look" is the whole
    /// difference.
    ///
    /// AND A STOPPED AI SAYS WHY, when a start was refused (<see cref="AppHost.AiNotStarted"/>). A
    /// restart that could not start the AI pressed nothing, so there is no strip under the header to
    /// show the refusal in — this line and the activity log are the only places the owner reads it.
    /// </summary>
    internal static string MissionSentence(MissionStatus status, string? notStarted = null) =>
        Whose(status) + status.State switch
        {
            MissionState.Working => "working",
            // THE VENDOR'S LIMIT OUTRANKS THE NEXT LOOK. The look is only when the loop checks the hold
            // again; the hold is when the AI can work, and it is a fact about the owner's plan rather
            // than about this app — so the card names whose limit it is.
            MissionState.Waiting when status.Held is { } held => held.Sentence(DateTimeOffset.Now),
            MissionState.Waiting => (status.NextTurnAt, status.WaitingFor) switch
            {
                ({ } at, { } why) => $"waiting until {at.ToLocalTime():HH:mm} for {why}",
                ({ } at, null) => $"waiting until {at.ToLocalTime():HH:mm}",
                (null, { } why) => $"waiting for {why}",
                _ => "waiting"
            },
            MissionState.Paused => "paused",
            _ => notStarted is { Length: > 0 } refused
                ? $"stopped — {refused}"
                : "stopped — the AI has not been started"
        };

    /// <summary>
    /// WHICH ROLES THE REST OF THE LINE IS ABOUT, as a prefix, or nothing at all where no council is
    /// behind the loop.
    ///
    /// It is a prefix rather than a suffix because the sentence already ends with the thing the loop
    /// is waiting for, and that is the half an owner reads first. "Working" with two roles is
    /// otherwise an ambiguous word: a council in which Operations takes every turn and Research
    /// never runs looks exactly like a healthy one, and the repair for the two is different — the
    /// share on the Safety page, not the daily limit.
    ///
    /// EVERY ROLE THAT IS TURNING, because their turns may overlap: a line naming one of two working
    /// roles is a line that is right half the time, and the half it gets wrong is the one an owner is
    /// looking at when they wonder why the other role is quiet.
    /// </summary>
    static string Whose(MissionStatus status)
    {
        if (status.State is not (MissionState.Working or MissionState.Waiting)) return "";

        var roles = status.Roles.Count > 0 ? status.Roles
            : status.Role is { Length: > 0 } one ? [one]
            : (IReadOnlyList<string>)[];

        return roles.Count == 0 ? "" : $"{string.Join(" and ", roles.Select(CouncilRoles.Title))}: ";
    }

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
        // BOTH CEILINGS, EACH IN ITS OWN WORDS (U-billing-classes): real money, and the plan's capacity — never shown as money.
        return spend.PlanReading is { } plan
            ? MissionCostOf(spend with { PlanReading = null }) + " " + MissionCostOf(plan)
            : MissionCostOf(spend);
    }

    static string MissionCostOf(AiSpendToday spend)
    {
        var cap = MissionSituation.Money(spend.Cap, spend.Currency) + " " + spend.CeilingWords("your");
        var head = spend.Class == BillingClasses.Plan ? "Plan capacity used today" : "Cost today";

        if (!spend.CanPrice)
            return $"{head}: unknown — {spend.WhyNoPrice}. Your {cap} cannot stop it.";

        // THE ONE READING MIDNIGHT DOES NOT REPAIR. A ceiling smaller than one turn's worst case
        // admits no turn today and none tomorrow, so the card must not say "starts again at 00:00".
        if (spend.CapCannotFundATurn)
            return $"{head}: {MissionSituation.Money(spend.Spent, spend.Currency)} of {cap} — but one turn "
                   + $"can cost up to {MissionSituation.Money(spend.NextTurnReservation, spend.Currency)}, which is "
                   + "more than the whole limit, so the AI cannot start a turn at all. Raise the limit, or choose a "
                   + "cheaper model above.";

        var line = $"{head}: {MissionSituation.Money(spend.Spent, spend.Currency)} of {cap}";
        // WHICH MODEL IS SPENDING IT, beside what it has spent. The same turn on gpt-6-astra and on
        // gpt-5.6-luna differs by a factor of fifty, so a total with no model beside it is a number
        // the owner cannot act on — and this is the choice they make one press away, on this page.
        if (spend.Model is { Length: > 0 } model) line += $", running {model}";
        // Where the figure came from, beside the figure. An owner cannot tell an upper bound from a
        // bill by looking at it, and the difference decides whether they should go and correct it —
        // so the third reading is the one where they already have: their own rate, named as theirs,
        // because a surprising total priced by them is a different thing to go and look at.
        if (spend.PricedByOwner) line += $" — {Labels.PricedByYou}";
        else if (spend.Estimated is { } estimated) line += $" — {estimated}";
        if (spend.UnpricedTurns > 0) line += $" — {spend.UnpricedTurns} turns could not be priced, so it is at least that";
        // The correction the other way: a turn that never reported what it used is charged what it
        // reserved, so that part of the figure is the most it could have cost rather than a bill.
        if (spend.UnreportedTurns > 0)
            line += $" — {spend.UnreportedTurns} never reported what they used and are charged what they reserved";
        if (spend.Reserved > 0m)
            line += $" — including {MissionSituation.Money(spend.Reserved, spend.Currency)} committed to a turn still running";
        // Not CapReached: the ceiling is applied BEFORE a turn now, so the card has to say the limit
        // is reached at the moment the next turn is refused rather than one turn later.
        if (!spend.AdmitsAnotherTurn) line += $". The limit is reached; the AI starts again at {spend.ResumesAt:HH:mm}";
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
        /// <see cref="Answer"/> is the note box and the answers. <see cref="OnTheWire"/> is
        /// the sentence that replaces them while a dispatcher of this process is still inside the
        /// connector call for this request: there is nothing for the owner to have seen yet, so the
        /// buttons would be asserting an outcome that does not exist. The lease is asked every tick
        /// rather than at build time because it EXPIRES — the row's id does not change when the
        /// dispatch ends, so nothing else would rebuild this row, and the owner would be left
        /// looking at a refusal that had stopped being true.
        /// </summary>
        public required Control Answer { get; init; }
        public required TextBlock OnTheWire { get; init; }

        /// <summary>
        /// The answers, one button each, in <see cref="Answers"/>' order for <see cref="BuiltAs"/> and <see cref="IsPress"/>.
        /// Their words follow the row's reference every tick: a reference can arrive after the row was built, and "No
        /// order exists" is false of an order the platform has answered (<see cref="CancelledAnswer"/>).
        /// </summary>
        public required IReadOnlyList<Button> Buttons { get; init; }

        /// <summary>The state the row's answers were built for: they are what the owner is offered until the row is rebuilt.</summary>
        public required ExecutionState BuiltAs { get; init; }

        /// <summary>Whether the row is a press's own record (<see cref="TradingGateway.IsPressRecord"/>).</summary>
        public required bool IsPress { get; init; }
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

            // The answers' words follow the reference, in place; Relabel disarms only when they change. Only the
            // reference can change them here — the state and the press are the ones the row was built for.
            var answers = Answers(row.BuiltAs, row.IsPress, r.ConnectorOrderId);
            if (answers.Count == row.Buttons.Count)
                for (var i = 0; i < answers.Count; i++) Ui.Relabel(row.Buttons[i], answers[i].Label, answers[i].Armed);

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

        // THE ANSWERS, FROM THE ONE FUNCTION THAT SAYS WHICH ARE TRUE OF THIS ROW (Answers), each a two-press
        // button behind the same note and the same route.
        var buttons = new List<Button>();
        foreach (var (label, armed, outcome) in Answers(r.State, isPress, r.ConnectorOrderId))
            buttons.Add(Ui.Confirm(label, armed, () => ResolveAsync(id, outcome, note)));

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
            Answer = answer, OnTheWire = onTheWire, Buttons = buttons, BuiltAs = r.State, IsPress = isPress
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
    /// The override itself, through the gateway's one route for it,
    /// <see cref="TradingGateway.AnswerFromTheCardAsync"/>: <c>ForceResolve</c>, then the health pass —
    /// which is not decoration: ForceResolve clears the needs-reconciliation flag and nothing else, while
    /// TryAuthorizeExecution ALSO requires the ExecutionCapability health row to be READY, and that row was
    /// set PAUSED by the failed dispatch and by the reconciler. Without the second call the user presses the
    /// button, sees "AI trading — paused" stay on screen for up to five seconds until the background tick
    /// recomputes health, and reasonably concludes the button does nothing. A refusal is thrown in its own
    /// words, and the two-step button shows it.
    /// </summary>
    async Task ResolveAsync(string requestId, ExecutionState outcome, TextBox note)
    {
        var text = (note.Text ?? "").Trim();
        if (text.Length == 0) { Ui.ReportError?.Invoke("Say what you saw in ATAS before confirming."); return; }

        await _host.Gateway.AnswerFromTheCardAsync(requestId, outcome, text);
    }

    /// <summary>
    /// WHAT THE CARD OFFERS ONE ROW, AND WHAT EACH ANSWER RECORDS (<c>U-press-row-answer</c>): every button's label, the
    /// sentence its first press arms it with, and the state its second press asks
    /// <see cref="TradingGateway.AnswerFromTheCardAsync"/> to write — in the order they stand on the row. One pure function:
    /// the card builds the row's buttons from it once, and relabels them from it on every tick, because a reference can
    /// arrive after the row was built.
    ///
    /// <para><b>A state only the platform's own answer can have produced</b> (<see cref="SpokenByThePlatform"/>) is offered
    /// one answer: that it is still true. A terminal one has no outgoing edge, so whether the state it holds is right is the
    /// only answer there is — <c>ForceResolve</c> takes an assertion equal to the stored state as a flag-clear, and refuses a
    /// DIFFERENT outcome on purpose: that is the stream and the platform disagreeing, something to investigate rather than to
    /// overwrite. A live one the platform answered WORKING is the same agreement.</para>
    ///
    /// <para><b>Except a press's own close the platform answered and has not finished</b>
    /// (<see cref="TradingGateway.IsPressRecord"/>, <see cref="TradingGateway.AnsweredAndUnfinished"/>): it is offered "It is
    /// no longer working at your platform" (CANCELLED) and "It was filled" (FILLED) beside its own answer. Nothing but the
    /// owner settles a press's row — the reconciler, the in-flight sweep and the press's own settle all leave it to him — so
    /// a close whose platform update was lost after he answered "it is working" had no answer that could end it, and every
    /// Close all waited on it for good. Not "It did not fill": a partly filled close did, and the fill it carries stays on
    /// the record. Both outcomes are reachable from all four states, and the route puts them under the platform's live-order
    /// veto, so a close the platform still lists live is refused there rather than answered away. Every other row in those
    /// states keeps its one answer.</para>
    ///
    /// <para><b>Anything else</b> — UNKNOWN, RECONCILING, a stranded DISPATCHING, a refused leg's CREATED — is offered FILLED
    /// and CANCELLED, and nothing else, because they are the only two outcomes <c>OrderStateMachine</c> lets
    /// <c>ForceResolve</c> reach from EVERY state a flagged request can hold. "Still working" is the obvious third answer and
    /// is unreachable from WORKING, PARTIALLY_FILLED and CANCEL_PENDING — a button that throws on the states where it is most
    /// likely to be the true answer is worse than no button. The CANCELLED answer's words are the row's own
    /// (<see cref="CancelledAnswer"/>): an order the platform answered existed, and did not fill.</para>
    /// </summary>
    public static IReadOnlyList<(string Label, string Armed, ExecutionState Outcome)> Answers(ExecutionState state,
        bool isPress, string? reference)
    {
        if (SpokenByThePlatform(state))
        {
            var tense = OrderStateMachine.IsTerminal(state) ? "was" : "is";
            (string, string, ExecutionState) itsOwn = ($"Our record is right — it {tense} {Word(state)}",
                $"Confirm: I checked in ATAS and this order {tense} {Word(state)}", state);
            if (!isPress || !TradingGateway.AnsweredAndUnfinished(state)) return [itsOwn];

            return
            [
                itsOwn,
                ("It is no longer working at your platform",
                    "Confirm: I checked in ATAS and this order is no longer working there", ExecutionState.CANCELLED),
                ("It was filled", "Confirm: I checked in ATAS and this order was filled", ExecutionState.FILLED)
            ];
        }

        var (label, armed) = CancelledAnswer(reference);
        return
        [
            ("It was filled", "Confirm: I checked in ATAS and this order was filled", ExecutionState.FILLED),
            (label, armed, ExecutionState.CANCELLED)
        ];
    }

    /// <summary>
    /// THE CARD'S CANCELLED ANSWER, IN WORDS THAT ARE TRUE OF THE ROW (<c>U-inflight-owner</c>): the button's label and
    /// its armed sentence. A row carrying the platform's own reference is an order the platform answered — it existed —
    /// so "No order exists" would be false of it, and what the owner can truly assert is that it did not fill. A row
    /// without one may never have reached the platform, and the old words stand. Both record the same state,
    /// <see cref="ExecutionState.CANCELLED"/>; only what the owner is asked to assert differs.
    /// </summary>
    public static (string Label, string Armed) CancelledAnswer(string? connectorOrderId) =>
        string.IsNullOrEmpty(connectorOrderId)
            ? ("No order exists", "Confirm: I checked in ATAS and no such order exists")
            : ("It did not fill", "Confirm: I checked in ATAS and this order did not fill");

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
    /// the platform is the one answer that is always available and always true. It stops being
    /// true of a press's close once the platform's update about it is lost, and that close alone is
    /// also offered the two that can end it (<see cref="Answers"/>, <c>U-press-row-answer</c>).
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

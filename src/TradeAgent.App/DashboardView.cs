using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
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

    readonly StackPanel _unconfirmed = new() { Spacing = Theme.S5 };
    readonly Border _unconfirmedCard;
    readonly List<UnconfirmedRow> _unconfirmedRows = [];

    string _healthSignature = "";
    string _approvalSignature = "";
    string _unconfirmedSignature = "";

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

        var left = Ui.Col(Theme.S6,
            _unconfirmedCard,
            _approvalsCard,
            Ui.Section("Right now", Ui.Col(0, facts, actions)));

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

        RefreshApprovals(waiting);
        // Read straight from the gateway rather than from GatewayStatus, which carries only a count.
        // Unreconciled() is the same question TryAuthorizeExecution refuses on — the flag AND a
        // request left stranded in DISPATCHING — so the card shows exactly the records that are
        // pausing trading. Reading the raw flag here left the card empty while the banner said
        // paused, which reads as the software being broken rather than careful.
        RefreshUnconfirmed(_host.Gateway.Unreconciled());

        var health = _host.Health.Snapshot();
        var hs = string.Join('|', health.Select(h => $"{h.Component}:{h.State}:{h.Detail}"));
        if (hs != _healthSignature)
        {
            _healthSignature = hs;
            _healthRows.Children.Clear();
            foreach (var h in health) _healthRows.Children.Add(Ui.StatusRow(h));
        }
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

        _unconfirmedRows.Add(new UnconfirmedRow
        {
            RequestId = id, State = state.Value, BrokerId = brokerId.Value, LastCheck = lastCheck.Value,
            Press = isPress ? press.Value : null
        });

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
            note,
            Ui.With(Ui.Col(Theme.S2, [.. buttons]), c => c.Margin = new Thickness(0, Theme.S2, 0, 0)));

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

    readonly StackPanel _modeRow = new() { Orientation = Orientation.Horizontal, Spacing = Theme.S2 };
    readonly TextBlock _modeNote = Ui.Muted("");
    readonly TextBlock _liveNote = Ui.Body("");
    readonly Button _liveButton;
    readonly Button _stopButton;
    readonly NumericUpDown _maxQty, _maxNotional, _maxPositions, _maxPerMinute;
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

    public SafetyPage(AppHost host)
    {
        _host = host;

        foreach (var mode in Enum.GetValues<TradingMode>())
        {
            var m = mode;
            _modeRow.Children.Add(Ui.Secondary(Ui.ModeLabel(m), () => _host.Gateway.SetMode(m)));
        }

        _liveButton = Ui.Confirm("Switch real-money trading ON", "Confirm: allow real money",
            () => _host.Gateway.ActivateLive(!_host.Gateway.Settings.LiveActivated));
        _liveButton.HorizontalAlignment = HorizontalAlignment.Left;

        var modeCard = Ui.Section("Trading mode", Ui.Col(Theme.S4,
            _modeRow,
            _modeNote,
            Ui.Divider(),
            _liveNote,
            _liveButton));

        // The emergency block. The stop is one press in both directions because a mis-press that
        // removes the AI's permission to trade costs nothing, and hesitation here costs money.
        _stopButton = Ui.Big("STOP AI TRADING", Theme.Danger, () =>
        {
            if (_host.Gateway.Settings.AiTradingStopped) _host.Gateway.EnableAiTrading();
            else _host.Gateway.StopAiTrading("you pressed STOP AI TRADING");
        });

        var emergency = Ui.Section("Emergency", Ui.Col(Theme.S4,
            _stopButton,
            Ui.Muted("Stopping the AI removes its permission to trade. It does not touch your orders or positions."),
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
        // The placeholder is what an empty box MEANS, and an empty box now means nothing is allowed
        // rather than everything is. It said "any".
        _allowlist = Ui.TextField(string.Join(", ", r.InstrumentAllowlist), "none");

        var limits = Ui.Section("Safety limits", Ui.Col(Theme.S2,
            Ui.Muted("The AI cannot change these and has no command to ask. Small numbers are the point."),
            Ui.Spacer(Theme.S2),
            Ui.FieldRow("Most it may buy or sell in one order", _maxQty),
            Ui.FieldRow("Most money one order may be worth", _maxNotional,
                "0 means not enforced. For futures this is the right default — one contract is worth far more on paper than it costs to trade."),
            Ui.FieldRow("Most positions it may hold at once", _maxPositions),
            Ui.FieldRow("Most orders per minute", _maxPerMinute),
            Ui.FieldRow("Instruments it may touch", _allowlist,
                "Comma separated. " + Labels.NoInstrumentAllowed),
            Ui.Spacer(Theme.S2),
            Ui.With(Ui.Primary(Labels.SaveLimits, SaveLimits), b => b.HorizontalAlignment = HorizontalAlignment.Left),
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
        grid.Children.Add(Pages.Column(0, Ui.Col(Theme.S6, _unreadableCard, modeCard, limits)));
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

        _stopButton.Content = status.AiTradingStopped ? "RESUME AI TRADING" : "STOP AI TRADING";
        _stopButton.Background = status.AiTradingStopped ? Theme.Positive : Theme.Danger;
    }

    /// <summary>
    /// The user guide and the agent's own AGENTS.md both said these were set in this window; until
    /// they were editable here they could only be changed by editing the database by hand.
    /// </summary>
    void SaveLimits()
    {
        _host.Gateway.Update(s =>
        {
            s.Risk.MaxOrderQuantity = _maxQty.Value ?? s.Risk.MaxOrderQuantity;
            s.Risk.MaxNotionalPerOrder = _maxNotional.Value ?? s.Risk.MaxNotionalPerOrder;
            s.Risk.MaxOpenPositions = (int)(_maxPositions.Value ?? s.Risk.MaxOpenPositions);
            s.Risk.MaxOrdersPerMinute = (int)(_maxPerMinute.Value ?? s.Risk.MaxOrdersPerMinute);
            s.Risk.InstrumentAllowlist = (_allowlist.Text ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
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

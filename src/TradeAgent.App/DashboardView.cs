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
    }

    Control BuildUnconfirmedRow(ExecutionRequest r)
    {
        var id = r.RequestId;
        var state = Fact("TradeAgent thinks", StateSentence(r.State), mono: false);
        var brokerId = Fact("Broker reference", r.ConnectorOrderId ?? "none — the broker never sent one back", mono: true);
        var lastCheck = Fact("Last check", LastCheckSentence(r), mono: false);

        _unconfirmedRows.Add(new UnconfirmedRow
        {
            RequestId = id, State = state.Value, BrokerId = brokerId.Value, LastCheck = lastCheck.Value
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
        if (OrderStateMachine.IsTerminal(r.State))
        {
            // A record the event stream already settled, flagged afterwards because the dispatch
            // that wrote it never got an answer. Terminal states have no outgoing edges, so the
            // ONLY answer that can be given about one is whether the state it already holds is
            // true — ForceResolve takes that as finalState == current state and clears the flag
            // without rewriting the record. Asserting a DIFFERENT outcome is refused there on
            // purpose, and rightly: that is the stream and the platform disagreeing, which is
            // something to investigate rather than to overwrite. So one button, not two.
            var settled = r.State;
            buttons.Add(Ui.Confirm($"Our record is right — it was {Word(settled)}",
                $"Confirm: I checked in ATAS and this order was {Word(settled)}",
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
            Ui.With(Ui.Col(0,
                    state.Root,
                    Fact("Sent", (r.DispatchedAt ?? r.CreatedAt).ToLocalTime().ToString("d MMM, HH:mm:ss"), mono: true).Root,
                    Fact("Our reference", r.ClientOrderId, mono: true).Root,
                    brokerId.Root,
                    lastCheck.Root),
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
    readonly OperatorPress _cancelAllPress = new(), _closeAllPress = new();
    readonly NumericUpDown _maxQty, _maxNotional, _maxPositions, _maxPerMinute;
    readonly TextBox _allowlist;
    readonly TextBlock _limitsNote = Ui.Micro("");

    public Control Root { get; }

    /// <summary>
    /// Runs one press of an emergency control, and refuses to pretend it is over when it is not.
    ///
    /// A press that leaves the gateway with unconfirmed work stays outstanding: the next press
    /// repeats it (sending nothing) and the person is told, in the same words the Dashboard card
    /// uses, that the previous one has to be resolved first. Anything that goes wrong while deciding
    /// counts as "not finished" — the safe direction for a control that moves money.
    /// </summary>
    async Task PressAsync(OperatorPress press, string kind, Func<string, Task> run, string what)
    {
        var repeat = press.Outstanding;
        var nonce = press.Begin();
        var summary = "";
        try { await run(nonce); }
        finally
        {
            // Judged on THIS press's own records — not on whether the gateway has unconfirmed work
            // from something else, which both locked the control over unrelated orders and released
            // it while this press's own close was still unconfirmed.
            try
            {
                var outcome = await _host.Gateway.PressOutcomeAsync(kind, nonce);
                summary = outcome.Summary;
                press.Finish(outcome.Complete);
            }
            catch (Exception)
            {
                summary = "TradeAgent could not check what the press did.";
                press.Finish(false);
            }
        }

        if (press.Outstanding)
            Ui.ReportError?.Invoke(repeat
                ? $"Still unconfirmed, so nothing further was sent — pressing {what} again repeats the same press. {summary} Confirm it on the Dashboard first."
                : $"TradeAgent could not confirm the result of {what}. {summary} Nothing more will be sent until you confirm it on the Dashboard.");
    }

    public SafetyPage(AppHost host)
    {
        _host = host;

        // A press the store still cannot account for survives a restart: without this, closing the
        // app was a way to mint a fresh nonce over an unresolved close and send a second one.
        _closeAllPress.Restore(_host.Gateway.OutstandingPressNonce(TradingGateway.ClosePress));
        _cancelAllPress.Restore(_host.Gateway.OutstandingPressNonce(TradingGateway.CancelPress));

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
            // THE PRESS, NOT THE CLICK, IS THE UNIT. OperatorPress hands back the SAME nonce while
            // the last press is unfinished, so "it failed, press it again" repeats that press and
            // the gateway sends nothing twice. Minting a fresh nonce here — which is what this did
            // until 2026-09-03 — made the retry a new decision and closed the position twice.
            Ui.With(Ui.Confirm("Cancel all working orders", "Confirm: cancel all working orders",
                    () => PressAsync(_cancelAllPress, TradingGateway.CancelPress,
                        p => _host.Gateway.OperatorCancelAllAsync(p), "Cancel all working orders")),
                b => b.HorizontalAlignment = HorizontalAlignment.Stretch),
            Ui.With(Ui.Confirm("Close all positions", "Confirm: close all positions with market orders",
                    () => PressAsync(_closeAllPress, TradingGateway.ClosePress,
                        p => _host.Gateway.OperatorCloseAllAsync(p), "Close all positions")),
                b => b.HorizontalAlignment = HorizontalAlignment.Stretch)));
        emergency.Margin = new Thickness(Theme.S5, 0, 0, 0);

        var r = _host.Gateway.Settings.Risk;
        _maxQty = Ui.NumberField(r.MaxOrderQuantity, 0m, 1m);
        _maxNotional = Ui.NumberField(r.MaxNotionalPerOrder, 0m, 1000m);
        _maxPositions = Ui.NumberField(r.MaxOpenPositions);
        _maxPerMinute = Ui.NumberField(r.MaxOrdersPerMinute);
        _allowlist = Ui.TextField(string.Join(", ", r.InstrumentAllowlist), "any");

        var limits = Ui.Section("Safety limits", Ui.Col(Theme.S2,
            Ui.Muted("The AI cannot change these and has no command to ask. Small numbers are the point."),
            Ui.Spacer(Theme.S2),
            Ui.FieldRow("Most it may buy or sell in one order", _maxQty),
            Ui.FieldRow("Most money one order may be worth", _maxNotional,
                "0 means not enforced. For futures this is the right default — one contract is worth far more on paper than it costs to trade."),
            Ui.FieldRow("Most positions it may hold at once", _maxPositions),
            Ui.FieldRow("Most orders per minute", _maxPerMinute),
            Ui.FieldRow("Instruments it may touch", _allowlist,
                "Comma separated. Leave empty to allow any the platform offers."),
            Ui.Spacer(Theme.S2),
            Ui.With(Ui.Primary("Save limits", SaveLimits), b => b.HorizontalAlignment = HorizontalAlignment.Left),
            _limitsNote));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,340") };
        grid.Children.Add(Pages.Column(0, Ui.Col(Theme.S6, modeCard, limits)));
        grid.Children.Add(Pages.Column(1, emergency));

        Root = Pages.Scroll(Ui.Col(0,
            Pages.Header("Safety", "What the AI is allowed to do, and how to take it away instantly."),
            grid));
    }

    public void Update(GatewayStatus status)
    {
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
        _limitsNote.Text = "Saved. New orders are checked against these immediately.";
        _limitsNote.Foreground = Theme.Positive;
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

/// <summary>The self-check and the support package — the two things to do before asking for help.</summary>
sealed class ChecksPage
{
    readonly AppHost _host;
    readonly TextBlock _output = Ui.Body("");
    readonly TextBlock _placeholder =
        Ui.Muted("Nothing checked yet. Press Check everything and TradeAgent will test each part in turn.");
    readonly Button _showPackage;
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
                buttons),
            c => c.Margin = new Thickness(0, 0, 0, Theme.S4)));
        body.Children.Add(well);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(Pages.Header("Checks", "Test every part of TradeAgent, or package the logs for support."));
        root.Children.Add(Ui.With(body, c => c[Grid.RowProperty] = 1));
        Root = root;
    }

    /// <summary>Nothing here polls; this page only redraws when the user presses something.</summary>
    public void Update() { }

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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;

namespace TradeAgent.App;

/// <summary>
/// THE OWNER'S PAGE (<c>U-settings-shed</c>; R24 §§ 4.3, 4.7): what stays his and nothing else — the emergency family, his connections and
/// keys, the real money a paid key may spend with perception's share of it, the evidence settings and the narrowing presses over what is
/// running. Every limit, allowance, lane, allocation and envelope grant is the security board's: the Board page shows each one in force
/// as the app's founding value until the board decides, and no press here sets one.
///
/// <para><b>The rule that sorts every press</b> (seat B's ruling on R24 § 4.3): the owner keeps each press that only TAKES AWAY — stop,
/// cancel all, close all, withdraw an envelope, stop a deployment — and loses each press that grants or widens. Every press that stays
/// keeps its count: stop in one, resume in two; cancel all and close all in two; the real-money cap, perception's budget and a lower AI
/// price in two on the loosening side. Each one is in-process: no pipe op and no CLI verb reaches any of them (AUTH-001).</para>
///
/// <para><b>Two exceptions, until the board is active</b> (the orchestrator's rulings of 2026-10-23, his to overrule): "Reopen after
/// review" on a PAPER scope — never any other — is a widening, and it stays his only while no board seat is ACTIVE, so a held paper scope
/// does not stall paper runs while the board waits for a proven cell; and the seat choices — which model and runtime each seat runs on —
/// stay his settings for the same span. Both are labelled "yours until the security board is active"; from the first active seat they
/// are the board's, and this page stops offering them.</para>
///
/// <para>The tree is built once and updated in place (CLAUDE.md): <see cref="Update"/> writes text and visibility, and rebuilds nothing.</para>
/// </summary>
sealed class OwnerPage
{
    readonly AppHost _host;

    readonly Button _stopButton;

    /// <summary>WHETHER EITHER LOSS BUDGET HAS FIRED TODAY, and what TradeAgent did about it: facts beside the emergency presses, hidden while nothing is closed.</summary>
    readonly TextBlock _lossClosedNote = new()
    {
        Text = "", FontSize = Theme.Micro, Foreground = Theme.Caution,
        TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false
    };

    readonly TextBlock _lossFlattenNote = new()
    {
        Text = "", FontSize = Theme.Micro, Foreground = Theme.Caution,
        TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false
    };

    // ---- the real money a paid key may spend (BUDGET-001: the owner's) ----
    readonly AmountField _dailyCap;
    readonly TextBlock _capNote = Ui.Micro("");
    readonly TextBlock _moneyToday = Ui.Body("");

    /// <summary>The plan's day today — a reading only: its day-total is the security board's (R24 § 4.5, Q5).</summary>
    readonly TextBlock _planToday = Ui.Body("");
    readonly TextBlock _seatClasses = Ui.Micro("");

    // ---- the evidence: what a turn is priced at (EVID-001: the owner's) ----
    readonly AmountField _priceIn, _priceOut;
    readonly TextBlock _priceNote = Ui.Micro("");

    // ---- the key for TradeAgent's own worker (CRED-001: the owner's) ----

    /// <summary>
    /// THE KEY BOX FOR THE APP-OWNED HARNESS, MASKED, and the note that says what became of the paste. <c>PasswordChar</c> because the
    /// owner may be sharing a screen; the value is read on the press that takes it and the box is emptied.
    /// </summary>
    readonly TextBox _harnessKey = BuildHarnessKeyField();

    /// <summary>The masked box itself, from a factory, so what a test checks is the control the owner is looking at.</summary>
    internal static TextBox BuildHarnessKeyField() =>
        Ui.With(Ui.TextField(placeholder: Labels.HarnessKey), t =>
        {
            t.PasswordChar = '•';
            t.Width = double.NaN;
            t.HorizontalAlignment = HorizontalAlignment.Stretch;
        });

    readonly TextBlock _harnessKeyNote = Ui.Micro("");

    /// <summary>WHERE A KEY PASTED INTO THE BOX WILL GO (<c>U-key-host-pin</c>), updated in place on the five-second pass.</summary>
    readonly TextBlock _harnessKeyOrigin = Ui.Muted("");

    /// <summary>The press that takes the paste. Kept so the pass can disarm it if the address moves.</summary>
    readonly Button _saveHarnessKey;

    /// <summary>The address <see cref="_harnessKeyOrigin"/> last showed; a change disarms a half-made press.</summary>
    string? _harnessKeyOriginShown;

    /// <summary>Why the last press did not take the key, until the next press — held, so the tick does not wipe it mid-read.</summary>
    string? _harnessKeyNotTaken;

    /// <summary>
    /// AN EARLIER ARMED HOME, IN THE APP (<c>U-settings-shed</c> item 6; R24 § 4.5): while the gateway reads a settings row an earlier build
    /// left armed for real money, a caution card carries the gateway's own refusal — both versions and the repair — and ONE press that
    /// sets the earlier setting aside. One press because it only takes away, like stop; the gateway keeps the row as it was under its own
    /// name and converts nothing (<c>TradingGateway.SetAsideEarlierLiveSetting</c>). Nothing calls it but the press: the five-second pass
    /// only reads. Built once; <see cref="Update"/> writes text and visibility. Since fix pass 1 (Codex P2) the WINDOW shows it, as a strip
    /// above every page (<c>MainWindow.EarlierArmedStrip</c>), because the app opens on Chat; this page builds none of its own.
    /// </summary>
    internal sealed class EarlierArmedHome
    {
        readonly Func<EarlierLiveSetting?> _earlier;
        readonly Action _setAside;

        public Control Root { get; }
        public Border Card { get; }
        public TextBlock Sentence { get; } = Ui.Body("");
        public Button Press { get; }
        public TextBlock Note { get; } = Ui.Micro("");

        public EarlierArmedHome(Func<EarlierLiveSetting?> earlier, Action setAside)
        {
            _earlier = earlier;
            _setAside = setAside;
            Press = Ui.Primary(Labels.SetAsideEarlierLiveSetting, SetAside);
            Press.HorizontalAlignment = HorizontalAlignment.Left;
            Note.IsVisible = false;
            Card = new Border
            {
                Background = Theme.CautionSoft,
                BorderBrush = Theme.Caution,
                BorderThickness = new Thickness(1),
                CornerRadius = Theme.Radius,
                Padding = new Thickness(Theme.S5),
                IsVisible = false,
                Child = Ui.Col(Theme.S3,
                    Ui.With(Ui.Eyebrow(Labels.EarlierLiveSettingTitle), t => t.Foreground = Theme.Caution),
                    Sentence,
                    Press)
            };
            Root = Ui.Col(Theme.S2, Card, Note);
        }

        void SetAside()
        {
            try
            {
                _setAside();
                Note.Text = Labels.EarlierLiveSettingSetAside;
                Note.Foreground = Theme.Positive;
            }
            catch (Exception ex)
            {
                Note.Text = Labels.EarlierLiveSettingNotSetAside(ex.Message);
                Note.Foreground = Theme.Caution;
            }
            Note.IsVisible = true;
            Update();
        }

        /// <summary>The five-second pass: reads the gateway's reading and writes it in place. It never presses.</summary>
        public void Update()
        {
            EarlierLiveSetting? earlier;
            try { earlier = _earlier(); }
            catch (Exception ex)
            {
                Sentence.Text = Labels.EarlierLiveSettingNotSetAside(ex.Message);
                Card.IsVisible = true;
                Press.IsEnabled = false;
                return;
            }
            Card.IsVisible = earlier is not null;
            Press.IsEnabled = earlier is not null;
            Sentence.Text = earlier?.Sentence ?? "";
        }
    }

    /// <summary>
    /// WHAT EACH SEAT RUNS ON — HIS UNTIL A BOARD SEAT IS ACTIVE (the orchestrator's ruling for the board pair, 2026-10-23: seat choices
    /// "stay settings until a board seat is ACTIVE"; the settings page labels them like Reopen). The model the AI runs on, what the Research
    /// Director runs on and its model: one press each, as they always were — choosing a model grants nothing, a dearer one reaches the
    /// day's limit sooner, and moving a role onto TradeAgent's own worker only narrows what it may do. While any board seat is active — or
    /// the board cannot be read — the card is hidden and a press that arrives anyway writes nothing and says so.
    ///
    /// <para>Built once; <see cref="Update"/> writes visibility and emphasis, and rebuilds the model row ONLY when the runtime it was
    /// built for changed (rebuilding a tree is not a refresh).</para>
    /// </summary>
    internal sealed class SeatChoices
    {
        readonly AppHost _host;
        readonly Func<bool> _boardActive;
        readonly Action _changed;
        string? _modelRowRuntime;

        public Control Root { get; }
        public Panel ModelRow { get; }
        public Panel ResearchRuntimeRow { get; }
        public Panel ResearchModelRow { get; }
        public TextBlock ModelNote { get; } = Ui.Micro("");
        public TextBlock Note { get; } = Ui.Micro("");

        public SeatChoices(AppHost host, Func<bool> boardActive, Action changed)
        {
            _host = host;
            _boardActive = boardActive;
            _changed = changed;
            ModelRow = BuildModelRow(_host.ModelChoices, _host.SpendToday.Currency, ChooseModel);
            _modelRowRuntime = _host.Gateway.Settings.SelectedRuntimeId;
            ResearchRuntimeRow = BuildRuntimeRow(id => ChooseRoleRuntime(CouncilRoles.Research, id));
            ResearchModelRow = BuildModelRow(_host.ModelChoices, _host.SpendToday.Currency, m => ChooseRoleModel(CouncilRoles.Research, m));
            Note.IsVisible = false;
            Root = Ui.Section(Labels.SeatChoicesTitle, Ui.Col(Theme.S2,
                Ui.Muted(Labels.SeatChoicesAreYours),
                Ui.Spacer(Theme.S2),
                Ui.FieldRow(Labels.AiModel, ModelRow),
                ModelNote,
                Ui.Spacer(Theme.S2),
                Ui.FieldRow(Labels.ResearchRuntime, ResearchRuntimeRow),
                Ui.FieldRow(Labels.ResearchModel, ResearchModelRow),
                Note));
        }

        bool Refused()
        {
            if (!_boardActive()) return false;
            Note.Text = Labels.SeatChoicesAreTheBoards;
            Note.Foreground = Theme.Caution;
            Note.IsVisible = true;
            Update();
            return true;
        }

        /// <summary>The app-wide model. Null is the button that means "TradeAgent's choice".</summary>
        void ChooseModel(string? model)
        {
            if (Refused()) return;
            _host.Gateway.Update(s => s.SelectedModelId = model);
            _host.Gateway.Log.Activity(_host.RequestedModel is { Length: > 0 } m
                ? $"The AI now runs on {m}"
                : "The AI now runs on whatever its AI tool is configured for");
            Update();
            _changed();
        }

        /// <summary>What a role runs on: the harness, or the AI tool chosen during setup, written as this role's own choice.</summary>
        void ChooseRoleRuntime(string role, string? runtime)
        {
            if (Refused()) return;
            _host.Gateway.Update(s =>
            {
                // THE CLI PRESS NAMES THE CLI (Codex re-review of fix pass 1, P2): clearing the override would fall back to the harness
                // the moment a worker key is held, so the press writes the app-wide runtime as this role's own choice.
                var chosen = runtime is { Length: > 0 } ? runtime : s.SelectedRuntimeId;
                if (chosen is { Length: > 0 }) s.RoleRuntime[role] = chosen;
                else s.RoleRuntime.Remove(role);
            });
            _host.Gateway.Log.Activity($"The {CouncilRoles.Title(role)} now runs on {_host.RuntimeForRole(role) ?? "its AI tool"}");
            Update();
            _changed();
        }

        /// <summary>One role's model. Clearing it falls back to the app-wide row above rather than to nothing.</summary>
        void ChooseRoleModel(string role, string? model)
        {
            if (Refused()) return;
            _host.Gateway.Update(s =>
            {
                if (model is { Length: > 0 }) s.RoleModel[role] = model;
                else s.RoleModel.Remove(role);
            });
            _host.Gateway.Log.Activity(_host.RequestedModelFor(role) is { Length: > 0 } m
                ? $"The {CouncilRoles.Title(role)} now runs on {m}"
                : $"The {CouncilRoles.Title(role)} now runs on whatever its AI tool is configured for");
            Update();
            _changed();
        }

        /// <summary>The five-second pass: whether the card is offered, and which choice is in force. It never presses.</summary>
        public void Update()
        {
            Root.IsVisible = !_boardActive();
            if (!Root.IsVisible) return;

            var runtime = _host.Gateway.Settings.SelectedRuntimeId;
            if (!string.Equals(runtime, _modelRowRuntime, StringComparison.Ordinal))
            {
                // BOTH MODEL ROWS, because which models exist is a fact about the runtime, and the Research Director's row names the same list.
                _modelRowRuntime = runtime;
                Rebuild(ModelRow, ChooseModel);
                Rebuild(ResearchModelRow, m => ChooseRoleModel(CouncilRoles.Research, m));
            }

            Emphasise(ModelRow, _host.Gateway.Settings.SelectedModelId);
            Emphasise(ResearchModelRow, _host.Gateway.Settings.RoleModel.TryGetValue(CouncilRoles.Research, out var r) ? r : null);
            var harness = RuntimeCatalog.IsHarness(_host.RuntimeForRole(CouncilRoles.Research));
            if (ResearchRuntimeRow.Children.Count == 2)
            {
                if (ResearchRuntimeRow.Children[0] is Button cli) Ui.Emphasise(cli, !harness);
                if (ResearchRuntimeRow.Children[1] is Button own) Ui.Emphasise(own, harness);
            }

            ModelNote.Text = _host.RequestedModel is { Length: > 0 } model
                ? $"Every turn runs on {model}."
                : "This AI tool has no model TradeAgent can choose, so it runs on whatever it is configured for.";
        }

        void Rebuild(Panel row, Action<string?> choose)
        {
            row.Children.Clear();
            foreach (var c in BuildModelRow(_host.ModelChoices, _host.SpendToday.Currency, choose).Children.ToArray())
            {
                // Detached first: a control cannot be in two trees, and the row it came from is thrown away.
                ((Panel)c.Parent!).Children.Remove(c);
                row.Children.Add(c);
            }
        }

        void Emphasise(Panel row, string? chosen)
        {
            var i = 0;
            if (row.Children.Count > 0 && row.Children[i++] is Button d) Ui.Emphasise(d, chosen is null);
            foreach (var m in _host.ModelChoices)
            {
                if (i >= row.Children.Count) break;
                if (row.Children[i++] is Button b) Ui.Emphasise(b, string.Equals(m.Model, chosen, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    /// <summary>
    /// THE ROW THAT CHOOSES WHICH MODEL A SEAT RUNS ON, one button per model this build ships a price for on the runtime in force, plus the
    /// one that means "whatever TradeAgent ships". The price is on the button because the choice IS the price. Static and handed what it
    /// does, so the row a test presses is the row the owner sees.
    /// </summary>
    internal static Panel BuildModelRow(IReadOnlyList<ModelPrice> models, string currency, Action<string?> choose)
    {
        // A WrapPanel: nine models at two prices each is far wider than the card.
        var row = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = Theme.S2, LineSpacing = Theme.S2 };
        row.Children.Add(Ui.Button(Labels.AiModelDefault, () => choose(null)));
        foreach (var m in models)
        {
            var id = m.Model;
            row.Children.Add(Ui.Button(ModelLabel(m, currency), () => choose(id)));
        }
        return row;
    }

    /// <summary>One model button's words: the model, then what it costs per million in and out.</summary>
    internal static string ModelLabel(ModelPrice m, string currency) =>
        $"{m.Model} — {MissionSituation.Money(m.InputPerMillion, currency)} in / "
        + $"{MissionSituation.Money(m.OutputPerMillion, currency)} out per million";

    /// <summary>
    /// THE ROW THAT CHOOSES WHAT A ROLE RUNS ON: the AI tool on this computer, or TradeAgent's own worker — the two KINDS of thing. One
    /// press: moving a role onto the harness only narrows what it may do, and moving it back changes no limit the gateway applies.
    /// </summary>
    internal static Panel BuildRuntimeRow(Action<string?> choose)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = Theme.S2, LineSpacing = Theme.S2 };
        row.Children.Add(Ui.Button(Labels.RuntimeIsTheCli, () => choose(null)));
        row.Children.Add(Ui.Button(Labels.RuntimeIsTheHarness, () => choose(ApiAgentRuntime.RuntimeId)));
        return row;
    }

    /// <summary>THE ONE SCREEN THAT REPAIRS AN UNREADABLE SETTINGS ROW SAYS SO, ABOVE EVERYTHING ELSE.</summary>
    readonly Border _unreadableCard;

    // ---- what TradeAgent is running, and the two presses that take it away ----
    readonly TextBlock _allocationValue = Ui.Body("");
    readonly TextBlock _envelopeValue = Ui.Body("");
    readonly TextBlock _deploymentValue = Ui.Body("");
    readonly TextBlock _envelopeNote = Ui.Micro("");
    readonly Button _withdrawEnvelope;
    readonly Button _stopDeployment;

    /// <summary>
    /// THE REVIEW-HOLD CARD: a REQUIRED note, a two-press button disabled until something is typed, an edit of the note disarming a
    /// half-made press. Shown only while something is held AND no board seat is active (see the class summary).
    /// </summary>
    readonly Border _releaseCard;

    readonly TextBox _releaseNote = Ui.With(Ui.TextField(placeholder: Labels.ReopenAfterReviewNote),
        t => { t.Width = double.NaN; t.HorizontalAlignment = HorizontalAlignment.Stretch; });

    readonly TextBlock _releaseHeld = Ui.Body("");
    readonly TextBlock _releaseNoteResult = Ui.Micro("");
    readonly Button _release;

    /// <summary>
    /// WHAT A RESEARCH CAMPAIGN MAY SPEND, AND THE JUDGE'S CAPITAL — evidence settings (EVID-001), shown read-only: no model sets them, the
    /// security board cannot either, and this build has no box for them (seat B's ruling Q6: no new owner control).
    /// </summary>
    readonly TextBlock _trialBudget = Ui.Body(""), _verdictBudget = Ui.Body(""), _explorationBudget = Ui.Body(""), _judgeCapital = Ui.Body("");

    /// <summary>THE OWNER'S PERCEPTION CARD (<c>U-decision-card</c>): the decision model's key, budget inside the cap, price, limits and Test press.</summary>
    readonly PerceptionCard _perception;

    /// <summary>What each seat runs on — his until a board seat is active (the orchestrator's ruling).</summary>
    readonly SeatChoices _seatChoices;

    public Control Root { get; }

    /// <summary>
    /// Runs one press of an emergency control. ONE SHOT, AND THE OWNER IS TOLD WHAT IT DID. The gateway writes one flagged record per
    /// target before it touches the wire and refuses a second press while one is open; its refusal is the sentence shown here.
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
            // The gateway latches the pause in memory before it writes anything, so an exception on the way out does not mean nothing
            // was sent — it means the Dashboard is the place to find out what was.
            Ui.ReportError?.Invoke($"{what} did not finish: {ex.Message}. Check the Dashboard for what it did send.");
        }
    }

    /// <summary>
    /// The emergency toggle as this page wears it: the one saturated fill in the app, red while the AI may trade, green while it may not,
    /// and red again the moment RESUME is half-pressed. The fill is a local brush, so it is registered rather than assigned.
    /// </summary>
    internal static Button BuildKillSwitch(Func<bool> stopped, Action stop, Action resume)
    {
        var b = Ui.KillSwitch("emergency", stopped, stop, resume);
        Ui.Repaints(b, (btn, armed) =>
        {
            // THE FOREGROUND IS PART OF THE REPAINT, NOT JUST THE FILL: arming swaps to the "danger" class, whose red foreground on this
            // red fill read as a blank red block on the running app.
            btn.Background = armed || !stopped() ? Theme.Danger : Theme.Positive;
            btn.Foreground = Theme.TextOnEmergency;
        });
        return b;
    }

    /// <summary>
    /// A CONFIRMATION COMPLETES ONLY THE FIGURES IT NAMED (<c>U-number-entry-culture</c>, fix pass 1): an edit of any box the press reads
    /// disarms it, so the next press asks again over what is typed now.
    /// </summary>
    internal static Button DisarmedByEdits(Button b, IEnumerable<AmountField> boxes, params TextBox[] texts)
    {
        foreach (var box in boxes) box.ValueChanged += (_, _) => Ui.DisarmConfirm(b);
        foreach (var text in texts)
            text.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty) Ui.DisarmConfirm(b);
            };
        return b;
    }

    /// <summary>
    /// THE REAL-MONEY CAP (the owner's: a paid key's spend, R24 § 4.3). Raising it asks twice; lowering it saves in one press — turns run
    /// back to back while the machine is on, so raising this number is granting the AI more of the owner's money.
    /// </summary>
    internal static Button BuildSaveDailyCap(Func<decimal> current, Func<decimal> pending,
        Func<string> currency, Action save)
    {
        var b = Ui.ConfirmIf(Labels.SaveDailyCap,
            () => pending() > current()
                ? Labels.RaiseDailyCapArmed(Amount.ExactMoney(pending(), currency()))
                : null,
            save, "primary");
        b.HorizontalAlignment = HorizontalAlignment.Left;
        return b;
    }

    /// <summary>
    /// THE CAP'S SAVE OVER ITS OWN BOX: editing the box disarms a raise armed for another figure, and a box that reads as nothing refuses
    /// either press (<c>U-number-entry-culture</c>) — it never saves 0 or the figure in force.
    /// </summary>
    internal static Button BuildSaveDailyCap(AmountField box, Func<decimal> current, Func<string> currency, Action save,
        Action<string>? refuse = null)
    {
        var b = Ui.ConfirmIf(Labels.SaveDailyCap,
            () => box.Figure > current()
                ? Labels.RaiseDailyCapArmed(Amount.ExactMoney(box.Figure, currency()))
                : null,
            save, () => Ui.Refusal((Labels.DailyCostCap, box)), refuse ?? (_ => { }), "primary");
        b.HorizontalAlignment = HorizontalAlignment.Left;
        box.ValueChanged += (_, _) => Ui.DisarmConfirm(b);
        return b;
    }

    /// <summary>
    /// TODAY AGAINST EACH CEILING (<c>U-billing-classes</c>): the money cap's figure, and the plan's — a list-price equivalent, never
    /// called a cost. A reached line promises midnight ONLY when a fresh day can fund one turn of its kind; when one turn needs more than
    /// the whole day allows — or the day allows nothing — it says midnight will not help and names what would: the owner's own limit for
    /// real money, the security board's day-total for the plan, or a cheaper model (U-billing-classes' review, P2).
    /// </summary>
    internal static (string Money, string Plan) TodayLines(AiSpendToday today)
    {
        if (!today.Metered) return ("Today: not metered.", "Today: not metered.");
        string Line(AiSpendToday r, string what, string repair)
        {
            var head = $"Today: {MissionSituation.Money(r.Spent + r.Reserved, r.Currency)} of {MissionSituation.Money(r.Cap, r.Currency)} {what}";
            if (r.AdmitsAnotherTurn) return head + ".";
            if (r.Cap > 0m && r.NextTurnReservation <= r.Cap) return head + " — reached; the next turn of this kind waits for midnight.";
            return head + " — " + (r.Cap <= 0m
                ? Labels.TodayAdmitsNothing(repair)
                : Labels.TodayCannotFundATurn(MissionSituation.Money(r.NextTurnReservation, r.Currency), repair));
        }
        var plan = today.PlanReading ?? today;
        // EACH LINE ITS OWN CEILING (fix pass 1): the money reading without the plan's it carries — whose AdmitsAnotherTurn asks both —
        // so the real-money line says "reached" only when the money cap is.
        return (Line(today with { PlanReading = null }, "in real money", Labels.TodayMoneyRepair),
            Line(plan, "of plan capacity, counted at list price — not money", Labels.TodayPlanRepair));
    }

    /// <summary>
    /// EVERY SEAT'S CLASS IN WORDS (<c>U-billing-classes</c>): who pays for each role's turns, as its last admitted launch was read — or
    /// that nobody has read it yet, and so it counts against both limits — and perception, always its own key.
    /// </summary>
    internal static string SeatClassesLine(IEnumerable<(string Title, string Class)> seats) =>
        "Who pays for each seat's turns: "
        + string.Join("; ", seats.Select(s => $"{s.Title} — " + (s.Class == BillingClasses.Unknown
            ? "not read yet, so its turns count against both limits"
            : BillingClasses.Words(s.Class))))
        + "; perception — real money (its own API key).";

    /// <summary>
    /// THE PRESS THAT WRITES WHAT THE AI'S WORK IS PRICED AT (EVID-001: the owner's, never a model's). Either half going DOWN asks twice —
    /// a lower price lets the same ceiling buy more turns; raising saves in one.
    /// </summary>
    internal static Button BuildSaveAiPrice(Func<(decimal In, decimal Out)> current,
        Func<(decimal In, decimal Out)> pending, Func<string> currency, Action save,
        Func<string?>? refusal = null, Action<string>? refuse = null)
    {
        var b = Ui.ConfirmIf(Labels.SaveAiPrice,
            () => pending() is var p && (p.In < current().In || p.Out < current().Out)
                ? Labels.LowerAiPriceArmed(Rate(p, currency()))
                : null,
            save, refusal ?? (() => null), refuse ?? (_ => { }), "primary");
        b.HorizontalAlignment = HorizontalAlignment.Left;
        return b;
    }

    /// <summary>
    /// THE PRESS THAT LIFTS A REVIEW HOLD. Two presses, a required note, and the armed sentence names the scopes it is about to let
    /// TradeAgent reopen (<c>U-reopen-2</c>). The note is refused here as well as by the page, on either press.
    /// </summary>
    internal static Button BuildReleaseConfirm(Func<IReadOnlyList<string>> heldScopes,
        Func<string> note, Action<string> release)
    {
        var b = Ui.Confirm(Labels.ReopenAfterReview, Labels.ReleaseHoldArmed(heldScopes()), () =>
        {
            var typed = (note() ?? "").Trim();
            if (typed.Length == 0) return;
            release(typed);
        });
        b.IsEnabled = false;
        b.HorizontalAlignment = HorizontalAlignment.Left;
        return b;
    }

    /// <summary>
    /// WHETHER THE RELEASE IS OFFERED: something is held, and no board seat is active — from the first active seat a release is the
    /// board's act, never this page's (the orchestrator's ruling of 2026-10-23).
    /// </summary>
    internal static bool ReleaseOffered(int held, bool boardActive) => held > 0 && !boardActive;

    /// <summary>
    /// THE PRESS THAT TAKES ONE PAPER ENVELOPE BACK. ONE press plus a confirm — the kill switch's rule: this only ever removes authority,
    /// and every paper allocation written under the grant stops authorising anything the moment it lands.
    /// </summary>
    internal static Button BuildEnvelopeWithdrawConfirm(Func<string?> standing, Action<string> apply)
    {
        var b = Ui.Confirm(Labels.WithdrawEnvelope, Labels.WithdrawEnvelopeArmed, () =>
        {
            if (standing() is { Length: > 0 } id) apply(id);
        });
        b.HorizontalAlignment = HorizontalAlignment.Left;
        return b;
    }

    /// <summary>
    /// THE PRESS THAT ENDS ONE PAPER RUN. ONE press plus a confirm: this only ever removes exposure — the run's working orders are
    /// cancelled and whatever it has open is closed through the gateway's own close. It ends the RUN and not the grant.
    /// </summary>
    internal static Button BuildDeploymentStopConfirm(Func<string?> running, Action<string> apply)
    {
        var b = Ui.Confirm(Labels.StopDeployment, Labels.StopDeploymentArmed, () =>
        {
            if (running() is { Length: > 0 } id) apply(id);
        });
        b.HorizontalAlignment = HorizontalAlignment.Left;
        return b;
    }

    /// <summary>Both halves of the rate, as one phrase for the armed sentence and the saved note to name — exactly, never rounded.</summary>
    static string Rate((decimal In, decimal Out) rate, string currency) =>
        $"{Amount.ExactMoney(rate.In, currency)} in and {Amount.ExactMoney(rate.Out, currency)} out";

    public OwnerPage(AppHost host, SettingsPage settings)
    {
        _host = host;

        // THE EMERGENCY BLOCK. STOP is one press because a mis-press that removes the AI's permission costs nothing and hesitation here
        // costs money; RESUME is two, because that direction gives the permission back.
        _stopButton = BuildKillSwitch(
            () => _host.Gateway.Settings.AiTradingStopped,
            () => _host.Gateway.StopAiTrading($"you pressed {Labels.StopAiTrading}"),
            () => _host.Gateway.EnableAiTrading());

        var emergency = Ui.Section("Emergency", Ui.Col(Theme.S4,
            _stopButton,
            Ui.Muted("Stopping the AI removes its permission to trade. It does not touch your orders or positions. "
                + "Stopping takes one press; letting it trade again takes two."),
            _lossClosedNote,
            _lossFlattenNote,
            Ui.Divider(),
            // ONE PRESS, ONE SET OF RECORDS, AND THEN A PERSON: the gateway writes a flagged row per target before the wire, pauses
            // trading on them, and refuses the next press until they are resolved on the Dashboard.
            Ui.With(Ui.Confirm(Labels.CancelAllWorkingOrders, "Confirm: cancel all working orders",
                    () => PressAsync(() => _host.Gateway.OperatorCancelAllAsync(), Labels.CancelAllWorkingOrders)),
                b => b.HorizontalAlignment = HorizontalAlignment.Stretch),
            Ui.With(Ui.Confirm(Labels.CloseAllPositions, "Confirm: close all positions with market orders",
                    () => PressAsync(() => _host.Gateway.OperatorCloseAllAsync(), Labels.CloseAllPositions)),
                b => b.HorizontalAlignment = HorizontalAlignment.Stretch)));

        _dailyCap = Ui.AmountField(_host.Gateway.Settings.AiDailyCostCap, AmountKind.Decimal);

        // THE PRICE BOXES OPEN ON WHAT THE AI IS ACTUALLY BEING CHARGED — the owner's numbers where set, else the shipped list price the
        // estimate uses, from the same place, so the box and the bill agree.
        var shipped = _host.ShippedRate;
        var rate = ShownRate();
        _priceIn = Ui.AmountField(rate.In, AmountKind.Decimal);
        _priceOut = Ui.AmountField(rate.Out, AmountKind.Decimal);

        // THE HARNESS'S OWN MANIFEST, through the host's one harness object — never a fresh read of the catalogue, which is a file an
        // agent can write (U-key-host-pin).
        _saveHarnessKey = BuildSaveHarnessKey(_host.HarnessKey, () => _harnessKey.Text,
            () => HarnessKeyDestination(_host.Harness?.Manifest), AfterHarnessKeyPress);

        var spending = Ui.Section("Your real money for the AI", Ui.Col(Theme.S2,
            Ui.Muted("Every turn the AI takes is paid for one of two ways: with real money, through an API key, or out of your ChatGPT "
                + "plan. The real-money limit is yours. How much of your plan the AI may use is the security board's decision — the "
                + Labels.BoardPage + " page shows it. Reaching either stops the AI taking new turns of that kind; it does not touch "
                + "your orders or positions. A turn TradeAgent cannot tell the kind of counts against both."),
            Ui.Spacer(Theme.S2),
            Ui.Label("Real money (API keys)"),
            _moneyToday,
            Ui.FieldRow(Labels.DailyCostCap, _dailyCap,
                "0 stops every paid turn. Raising this lets the AI spend more of your money, so it asks again first."),
            Ui.Spacer(Theme.S2),
            BuildSaveDailyCap(_dailyCap, () => _host.Gateway.Settings.AiDailyCostCap,
                () => _host.SpendToday.Currency, SaveDailyCap, why => Ui.Refuse(_capNote, why)),
            _capNote,
            Ui.Spacer(Theme.S3),
            // THE PLAN'S DAY, A READING (Q5): its day-total is the board's, so there is no box here.
            Ui.Label("Your ChatGPT plan (counted at list price, not money)"),
            _planToday,
            Ui.Micro(Labels.PlanDayTotalIsTheBoards),
            Ui.Micro(Labels.PlanVendorLimit),
            Ui.Micro(Labels.PlanSharedWithYou),
            Ui.Spacer(Theme.S2),
            _seatClasses));

        var evidence = Ui.Section("What a turn is priced at", Ui.Col(Theme.S2,
            Ui.Muted("What TradeAgent counts a turn as costing. It is evidence, not a limit: no model sets it, and the security board "
                + "cannot either. TradeAgent works out what each turn cost from the tokens your AI tool reports; if you know what you "
                + "are actually charged, put it here and it is used instead."),
            Ui.Spacer(Theme.S2),
            Ui.FieldRow(Labels.AiPriceIn, _priceIn),
            Ui.FieldRow(Labels.AiPriceOut, _priceOut, ShippedRateHint(shipped)),
            Ui.Spacer(Theme.S2),
            DisarmedByEdits(BuildSaveAiPrice(ShownRate, PendingRate, () => _host.SpendToday.Currency, SaveAiPrice,
                () => Ui.Refusal((Labels.AiPriceIn, _priceIn), (Labels.AiPriceOut, _priceOut)), why => Ui.Refuse(_priceNote, why)),
                [_priceIn, _priceOut]),
            _priceNote));

        var campaigns = Ui.Section("What a research campaign may spend", Ui.Col(Theme.S2,
            Ui.KeyValueLive("Trials per campaign", _trialBudget),
            Ui.KeyValueLive("Of them, for exploration", _explorationBudget),
            Ui.KeyValueLive("Final verdicts per lineage", _verdictBudget),
            Ui.KeyValueLive("The judge's capital", _judgeCapital),
            Ui.Spacer(Theme.S2),
            Ui.Muted(Labels.CampaignBudgetsAreEvidence)));

        var connections = Ui.Section("Key for TradeAgent's own worker", Ui.Col(Theme.S2,
            Ui.Muted("TradeAgent's own worker calls the AI provider directly, with the tools TradeAgent chose and a budget it enforces on "
                + "every request. It cannot run a shell, install anything or reach the internet. It needs a key, which is yours."),
            Ui.Spacer(Theme.S2),
            Ui.FieldRow(Labels.HarnessKey, _harnessKey, Labels.HarnessKeyHint),
            _harnessKeyOrigin,
            Ui.Spacer(Theme.S2),
            // A ROW THAT WRAPS: armed, the press says the whole address, and a plain row would push the button beside it off the card.
            Ui.Wrap(Theme.S3,
                _saveHarnessKey,
                Ui.Button(Labels.ForgetHarnessKey, ForgetHarnessKey)),
            _harnessKeyNote));

        // WHAT IS RUNNING, AND THE TWO PRESSES THAT TAKE IT AWAY (Q3: narrowings stay his). The grant is the board's; what stands is read.
        _envelopeNote.IsVisible = false;
        _withdrawEnvelope = BuildEnvelopeWithdrawConfirm(StandingEnvelopeId, ApplyEnvelopeWithdrawal);
        _stopDeployment = BuildDeploymentStopConfirm(RunningDeploymentId, ApplyDeploymentStop);

        var running = Ui.Section("What TradeAgent is running", Ui.Col(Theme.S2,
            Ui.KeyValueLive("Capital allocated now", _allocationValue),
            Ui.KeyValueLive("Paper experiments allowed", _envelopeValue),
            Ui.KeyValueLive("Running now", _deploymentValue),
            Ui.Spacer(Theme.S2),
            Ui.Muted("Allocating capital and granting paper experiments are the security board's decisions. Taking either away is "
                + "yours: withdrawing stops every experiment under the envelope at once, and stopping a run cancels its working "
                + "orders and closes what it holds."),
            Ui.Spacer(Theme.S2),
            Ui.Wrap(Theme.S3, _withdrawEnvelope, _stopDeployment),
            _envelopeNote));

        // THE REVIEW-HOLD CARD. Shown only while something is held and no board seat is active (see the class summary).
        _release = BuildReleaseConfirm(HeldScopes, () => _releaseNote.Text ?? "", ApplyRelease);
        var release = _release;
        _releaseNote.TextChanged += (_, _) =>
        {
            // Editing the note changes what the press is asserting: a confirmation armed against the old words must not survive.
            Ui.DisarmConfirm(release);
            release.IsEnabled = !string.IsNullOrWhiteSpace(_releaseNote.Text);
        };
        _releaseNoteResult.IsVisible = false;

        _releaseCard = new Border
        {
            Background = Theme.CautionSoft,
            BorderBrush = Theme.Caution,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.Radius,
            Padding = new Thickness(Theme.S5),
            IsVisible = false,
            Child = Ui.Col(Theme.S3,
                Ui.With(Ui.Eyebrow("Held for you to look at"), t => t.Foreground = Theme.Caution),
                _releaseHeld,
                Ui.Muted(Labels.ReopenAfterReviewHint),
                Ui.Micro(Labels.ReopenAfterReviewIsYoursUntilTheBoard),
                _releaseNote,
                _release,
                _releaseNoteResult)
        };

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

        // AFTER THE REAL MONEY, because perception's budget is a slice of that cap.
        _perception = PerceptionCard.For(_host);

        // WHAT EACH SEAT RUNS ON, beside the key its worker needs: his while no board seat is active, then gone.
        _seatChoices = new SeatChoices(_host, BoardActive, Refresh);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,340") };
        grid.Children.Add(Pages.Column(0, Ui.Col(Theme.S6,
            _unreadableCard, _releaseCard, running,
            Ui.H2(Labels.OwnerGroupConnections), settings.Platform, settings.Account, connections, _seatChoices.Root, settings.Bridge,
            Ui.H2(Labels.OwnerGroupMoney), spending, _perception.Root,
            Ui.H2(Labels.OwnerGroupEvidence), evidence, settings.Friction, settings.Holdout, campaigns,
            Ui.H2(Labels.OwnerGroupUpdates), settings.Updates)));
        grid.Children.Add(Pages.Column(1, Ui.With(Ui.Col(Theme.S6, emergency, settings.WhatTheseChange),
            c => c.Margin = new Thickness(Theme.S5, 0, 0, 0))));

        Root = Pages.Scroll(Ui.Col(0,
            Pages.Header(Labels.OwnerPage, Labels.OwnerPageSubtitle),
            grid));
    }

    public void Update(GatewayStatus status)
    {
        // Read from the gateway, not from `status`: GatewayStatus is the agent-facing shape and this is a fact about the row this build read.
        _unreadableCard.IsVisible = _host.Gateway.Settings.CouldNotBeRead;

        var (moneyToday, planToday) = TodayLines(_host.SpendToday);
        _moneyToday.Text = moneyToday;
        _planToday.Text = planToday;
        _seatClasses.Text = SeatClassesLine(CouncilRoles.All.Select(r => (CouncilRoles.Title(r), _host.BillingClassOf(r))));

        // THE CLOSURE AND WHAT WAS DONE ABOUT IT, off the records on the same pass and updated in place.
        var closed = _host.Gateway.ClosureToday();
        var reopen = _host.Gateway.ReopenReading();
        _lossClosedNote.Text = new LossToday
        {
            DayClosedAt = closed.At, DayClosedWhy = closed.Why, SymbolsClosed = closed.Symbols,
            ReopensAt = reopen.At, ReopenHeld = reopen.Held,
            ReopenedAt = reopen.ReopenedAt, ReopenedWhy = reopen.ReopenedWhy
        }.ClosedLine() ?? "";
        _lossClosedNote.IsVisible = _lossClosedNote.Text.Length > 0;
        var flattened = _host.Gateway.FlattenStateToday();
        _lossFlattenNote.Text = flattened.Why ?? "";
        _lossFlattenNote.IsVisible = _lossFlattenNote.Text.Length > 0;

        ShowHeldForReview();

        // Through SetResting, never by assigning Content: a half-pressed RESUME must survive the five-second tick.
        Ui.SetResting(_stopButton,
            status.AiTradingStopped ? Labels.ResumeAiTrading : Labels.StopAiTrading, "emergency");

        ShowAllocations();
        ShowEnvelopes();
        ShowCampaignBudgets();
        _perception.Update();
        _seatChoices.Update();

        Refresh();
    }

    /// <summary>The evidence settings a campaign copies at open, read off the settings row in force — never typed here.</summary>
    void ShowCampaignBudgets()
    {
        // AS A CAMPAIGN READS THEM: a negative number is zero, exploration is at most the trials, and a capital of zero or less is the
        // judge's ten thousand (TradeAgentSettings; VenueCostModel.CapitalOf) — the figures a campaign opened now would copy.
        var s = _host.Gateway.Settings;
        var trials = Math.Max(0, s.CampaignTrialBudget);
        static string N(decimal n) => Amount.Exact(n);
        _trialBudget.Text = N(trials);
        _explorationBudget.Text = N(Math.Min(trials, Math.Max(0, s.CampaignExplorationBudget)));
        _verdictBudget.Text = N(Math.Max(0, s.CampaignVerdictBudget));
        _judgeCapital.Text = N(Core.Strategy.VenueCostModel.CapitalOf(s.JudgeCapital)) + ", in the units of the data's prices";
    }

    // ---- the key for TradeAgent's own worker (U-key-host-pin) ---------------------------------------

    /// <summary>WHERE A PASTED KEY WILL GO, AND WHETHER THAT IS TRADEAGENT'S OWN ADDRESS (<c>U-key-host-pin</c>).</summary>
    internal sealed record KeyDestination(string? Origin, bool BuiltIn, string? BuiltInOrigin);

    /// <summary>
    /// THE ADDRESS THE BOX BINDS A KEY TO, read off the manifest the harness was BUILT from, compared with the built-in row compiled into
    /// this build — never with anything on disk, which an agent's own program can write.
    /// </summary>
    internal static KeyDestination HarnessKeyDestination(RuntimeManifest? harness)
    {
        var origin = harness is null ? null : KeyOrigin.Of(harness.Endpoint);
        var row = RuntimeCatalog.BuiltIn().FirstOrDefault(m => m.Id == (harness?.Id ?? ApiAgentRuntime.RuntimeId));
        var shipped = row is null ? null : KeyOrigin.Of(row.Endpoint);
        return new(origin, origin is not null && string.Equals(origin, shipped, StringComparison.Ordinal), shipped);
    }

    /// <summary>
    /// THE PRESS THAT TAKES THE PASTED KEY, FOR THE ADDRESS THE OWNER WAS SHOWN (<c>U-key-host-pin</c>). One press for TradeAgent's
    /// built-in address; two for any other, the armed sentence naming the address. Nothing about the confirmation is persisted. An empty
    /// box is a clear, in one press.
    /// </summary>
    internal static Button BuildSaveHarnessKey(HarnessKey holder, Func<string?> typed,
        Func<KeyDestination> destination, Action<bool> pressed)
    {
        // THE ADDRESS THE ARMED SENTENCE NAMED, and only for the press that follows it.
        string? confirmed = null;

        var b = Ui.ConfirmIf(Labels.SaveHarnessKey,
            () =>
            {
                confirmed = null;
                var to = destination();
                if (string.IsNullOrWhiteSpace(typed()) || to.Origin is null || to.BuiltIn) return null;
                confirmed = to.Origin;
                return Labels.SendHarnessKeyArmed(to.Origin);
            },
            () =>
            {
                var to = destination();
                var shown = to.Origin is not null
                            && (to.BuiltIn || string.Equals(to.Origin, confirmed, StringComparison.Ordinal));
                confirmed = null;

                var pasted = typed();
                if (string.IsNullOrWhiteSpace(pasted)) { holder.Clear(); pressed(true); return; }
                if (!shown) { pressed(false); return; }

                holder.Set(pasted, to.Origin);
                pressed(true);
            },
            "primary");
        b.HorizontalAlignment = HorizontalAlignment.Left;
        return b;
    }

    /// <summary>Empties the box and says what happened WITHOUT naming any part of the key.</summary>
    void AfterHarnessKeyPress(bool taken)
    {
        _harnessKey.Text = "";
        _harnessKeyNotTaken = taken ? null : Labels.HarnessKeyNotTaken;
        if (taken)
            _host.Gateway.Log.Activity(_host.HarnessKey.Held
                ? "A key for TradeAgent's own worker is held for this session"
                : "No key for TradeAgent's own worker is held");
        Refresh();
    }

    void ForgetHarnessKey()
    {
        _host.HarnessKey.Clear();
        _harnessKey.Text = "";
        _harnessKeyNotTaken = null;
        _host.Gateway.Log.Activity("The key for TradeAgent's own worker was forgotten");
        Refresh();
    }

    /// <summary>
    /// Redraws the parts of this page a press changed, WITHOUT rebuilding the tree: where a paste would go, and what the key is. A press
    /// half-made against one address is disarmed the moment the page shows another.
    /// </summary>
    void Refresh()
    {
        var harness = RuntimeCatalog.IsHarness(_host.RuntimeForRole(CouncilRoles.Research));
        var to = HarnessKeyDestination(_host.Harness?.Manifest);
        if (!string.Equals(to.Origin, _harnessKeyOriginShown, StringComparison.Ordinal))
        {
            Ui.DisarmConfirm(_saveHarnessKey);
            _harnessKeyOriginShown = to.Origin;
        }
        _harnessKeyOrigin.Text = Labels.HarnessKeyDestination(to.Origin, to.BuiltIn, to.BuiltInOrigin);
        _harnessKeyOrigin.Foreground = to.BuiltIn ? Theme.TextMuted : Theme.Caution;

        var held = _host.HarnessKey.Held;
        _harnessKeyNote.Text = _harnessKeyNotTaken ?? Labels.HarnessKeyState(held, _host.HarnessKey.Origin);
        _harnessKeyNote.Foreground = _harnessKeyNotTaken is null && (held || !harness) ? Theme.TextMuted : Theme.Caution;
    }

    // ---- the real money and the price ---------------------------------------------------------------

    /// <summary>
    /// THE RATE IN FORCE: the owner's own numbers where set, and otherwise the shipped list price the estimate is charging. Zero on a
    /// runtime this build ships no list price for; <see cref="OwnerPrice.From"/> refuses a zero, so saving that pair back prices nothing.
    /// </summary>
    (decimal In, decimal Out) ShownRate()
    {
        var s = _host.Gateway.Settings;
        if (OwnerPrice.From(s) is { } mine) return (mine.InputPerMillion, mine.OutputPerMillion);
        return _host.ShippedRate is { } shipped
            ? (shipped.InputPerMillion, shipped.OutputPerMillion)
            : (0m, 0m);
    }

    (decimal In, decimal Out) PendingRate() => (_priceIn.Figure, _priceOut.Figure);

    /// <summary>The shipped default under the boxes, WITH ITS DATE AND THE PAGE IT CAME FROM — a price with no date is one nobody can check.</summary>
    internal static string ShippedRateHint(ModelPrice? shipped) =>
        shipped is null
            ? "TradeAgent ships no list price for this AI tool, so nothing is assumed about what it costs."
            : $"TradeAgent uses {shipped.InputPerMillion} in and {shipped.OutputPerMillion} out — the "
              + $"dearest model {shipped.Model} on {shipped.Source}, read on {shipped.PricedAt}. A LOWER "
              + "price lets the AI take more turns before your daily limit stops it, so it asks again first.";

    /// <summary>Writes the rate. Both numbers together or neither: half a price applied to a whole turn would under-charge it.</summary>
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
        Ui.Said(_priceNote, $"Saved. Each turn is priced at {Rate((input, output), currency)} per million tokens.");
        _unreadableCard.IsVisible = _host.Gateway.Settings.CouldNotBeRead;
    }

    /// <summary>
    /// Writes the cap. The activity line names the figure, because this is the one setting whose effect the owner meets later as an AI
    /// that stopped. The save rewrites the settings row, so an unreadable-row warning stops HERE rather than on the next tick.
    /// </summary>
    void SaveDailyCap()
    {
        var cap = _dailyCap.Figure;
        _host.Gateway.Update(s => s.AiDailyCostCap = cap);
        var money = Amount.ExactMoney(cap, _host.SpendToday.Currency);
        _host.Gateway.Log.Activity($"The AI may now spend up to {money} a day");
        Ui.Said(_capNote, $"Saved. The AI may spend up to {money} a day.");
        _unreadableCard.IsVisible = _host.Gateway.Settings.CouldNotBeRead;
    }

    // ---- the review hold and its release (U-reopen-2) -------------------------------------------

    /// <summary>
    /// WHETHER ANY BOARD SEAT IS ACTIVE. A BOARD THAT CANNOT BE READ IS NOT "NO BOARD": it answers active, so nothing that is his only
    /// until the board is active is offered or taken over a board this build cannot read.
    /// </summary>
    bool BoardActive()
    {
        try { return _host.Board.AnyActive; }
        catch (Exception) { return true; }
    }

    /// <summary>WHAT IS BEING HELD FOR REVIEW ON PAPER RIGHT NOW, in the owner's words — "your account" and the instruments.</summary>
    IReadOnlyList<string> HeldScopes() => [.. HoldsByPaper().Paper.Select(j => j.Scope)];

    /// <summary>The words for one closure's scope — "your account" or the instrument — taken off the BREACH, which is what a release lifts.</summary>
    static string ScopeWords(LossBreachRecord b) => b.Symbol ?? "your account";

    /// <summary>What the page makes of one closure's hold row: a paper hold he may release, a hold that is not paper, or a row that does not match.</summary>
    internal enum HoldJudgement { Paper, NotPaper, Mismatch }

    /// <summary>One held closure, judged: the breach the row is FILED under, the row as read (null when unreadable), and the verdict.</summary>
    internal sealed record JudgedHold(LossBreachRecord FiledUnder, LossHoldRecord? Hold, HoldJudgement Judgement)
    {
        public string Scope => ScopeWords(FiledUnder);
    }

    /// <summary>
    /// WHETHER ONE HOLD IS HIS TO RELEASE (Codex P1 on <c>U-settings-shed</c> and both re-reviews): judged against the breach the row is
    /// FILED under — the breach the gateway's release lifts — never against the breach the row's own text names. The row must name exactly
    /// that breach (its key, account, instrument and day) and carry that breach's mode; any difference is a <see cref="HoldJudgement.Mismatch"/>,
    /// refused in words whether it was tampered with or not. Only then is it paper, and only when that breach's mode is
    /// <see cref="TradingMode.PAPER"/>. A row this page cannot read is not paper (refuse, never guess): the gateway answers an unreadable row as
    /// HELD and stamps it with the mode in force NOW, so its stamp is not evidence. Before an active board only paper can be held; a retained
    /// live hold is the board's, never this page's.
    /// </summary>
    internal static HoldJudgement Judge(LossBreachRecord filedUnder, LossHoldRecord? hold)
    {
        if (hold is null) return HoldJudgement.NotPaper;
        if (hold.BreachKey != LossBreach.KeyFor(filedUnder) || hold.Account != filedUnder.Account
            || hold.Symbol != filedUnder.Symbol || hold.Day != filedUnder.Day || hold.Mode != filedUnder.Mode)
            return HoldJudgement.Mismatch;
        return filedUnder.Mode == TradingMode.PAPER ? HoldJudgement.Paper : HoldJudgement.NotPaper;
    }

    /// <summary>
    /// Every closure on the selected account that the gateway's release would lift — a hold row filed under it and no release row beside
    /// it — each judged by <see cref="Judge"/>. The page reads the rows by the breach they are filed under, the key the release writes by,
    /// so what it judges is exactly what a press would release. Closures or rows it cannot read leave nothing judged paper.
    /// </summary>
    (List<JudgedHold> Paper, List<JudgedHold> NotPaper) HoldsByPaper()
    {
        var paper = new List<JudgedHold>();
        var notPaper = new List<JudgedHold>();
        try
        {
            var connector = _host.Gateway.Connector.Id;
            var account = _host.Gateway.ClosureAccountId;
            if (account.Length == 0) return (paper, notPaper);
            foreach (var b in _host.Gateway.OpenClosures(account))
            {
                if (_host.Db.GetKv(LossHold.HoldKey(connector, b)) is not { } json) continue;
                if (_host.Db.GetKv(LossHold.ReleaseKey(connector, b)) is not null) continue;
                LossHoldRecord? hold;
                try { hold = Json.Read<LossHoldRecord>(json); }
                catch (Exception) { hold = null; }
                var judged = new JudgedHold(b, hold, Judge(b, hold));
                (judged.Judgement == HoldJudgement.Paper ? paper : notPaper).Add(judged);
            }
        }
        catch (Exception)
        {
            // Closures or rows it cannot read: nothing is his to release, and a press is refused in words.
            paper.Clear();
            notPaper.Clear();
            notPaper.Add(new JudgedHold(new LossBreachRecord(), null, HoldJudgement.NotPaper));
        }
        return (paper, notPaper);
    }

    /// <summary>
    /// The card, on the five-second pass and in place: whether it is offered at all, what it says is held, and what the second press
    /// will do. Relabelled, never rebuilt, so a half-made press and a half-typed note survive the tick.
    /// </summary>
    void ShowHeldForReview()
    {
        var holds = HoldsByPaper().Paper;
        _releaseCard.IsVisible = ReleaseOffered(holds.Count, BoardActive());
        if (!_releaseCard.IsVisible) return;

        _releaseHeld.Text = string.Join(" ", holds.Select(h => h.Hold!.Why));
        Ui.Relabel(_release, Labels.ReopenAfterReview,
            Labels.ReleaseHoldArmed([.. holds.Select(h => h.Scope)]));
    }

    /// <summary>
    /// Takes the press. The note box is emptied only on a release that actually happened, so a refusal leaves the owner's words where
    /// they can press again.
    /// </summary>
    void ApplyRelease(string note)
    {
        // PAPER ONLY, READ AGAIN AT THE PRESS: the gateway's release lifts every hold on the account at once, so while any of them is not
        // paper it is never called — refused in words, nothing written. And never once a board seat is active (a board that cannot be
        // read counts as active), whatever the card showed a tick ago.
        var notPaper = HoldsByPaper().NotPaper;
        var mismatched = notPaper.Where(h => h.Judgement == HoldJudgement.Mismatch).Select(h => h.Scope).ToList();
        if (notPaper.Count > 0 || BoardActive())
        {
            _releaseNoteResult.Text = mismatched.Count > 0 ? Labels.ReleaseRefusedMismatch(mismatched)
                : notPaper.Count > 0 ? Labels.ReleaseRefusedNotPaper([.. notPaper.Select(h => h.Scope)])
                : Labels.ReleaseRefusedBoardActive;
            _releaseNoteResult.Foreground = Theme.Caution;
            _releaseNoteResult.IsVisible = true;
            ShowHeldForReview();
            return;
        }

        var result = _host.Gateway.ReleaseHold(note);
        _releaseNoteResult.Text = result.Why;
        _releaseNoteResult.Foreground = result.Ok ? Theme.Positive : Theme.Caution;
        _releaseNoteResult.IsVisible = true;

        if (result.Ok)
        {
            _releaseNote.Text = "";
            _release.IsEnabled = false;
        }

        ShowHeldForReview();
    }

    // ---- what is running ----------------------------------------------------------------------

    /// <summary>
    /// WHAT STANDS ALLOCATED RIGHT NOW, a reading. An allocation whose promotion no longer stands is shown and MARKED: hiding it would read
    /// as "nothing is allocated" while the row is still on the table.
    /// </summary>
    void ShowAllocations()
    {
        try
        {
            var standing = _host.Gateway.Allocations.Standing(DateTimeOffset.UtcNow);
            _allocationValue.Text = standing.Count == 0
                ? "nothing — no version has been given capital"
                : string.Join("; ", standing.Select(s =>
                    $"{s.Allocation.VersionId[..Math.Min(12, s.Allocation.VersionId.Length)]} "
                    + $"up to {AllocationRow.Num(s.Allocation.MaxQuantity)}"
                    + (s.Authorises ? ""
                        : s.RefusedForLive is { } refused ? $" — REFUSED FOR LIVE: {refused}"
                        : " — its promotion no longer stands, so it may trade nothing")));
        }
        catch (Exception) { _allocationValue.Text = "could not be read"; }
    }

    /// <summary>
    /// The envelope the withdrawal press is about: the one standing on the platform and account connected now, or null. A ledger this
    /// build cannot read answers null, so the press does nothing rather than guessing.
    /// </summary>
    string? StandingEnvelopeId()
    {
        try
        {
            return _host.Gateway.Envelopes.Standing(
                _host.Gateway.Connector.Id, _host.Gateway.ClosureAccountId, DateTimeOffset.UtcNow)?.Id;
        }
        catch (Exception) { return null; }
    }

    /// <summary>The run the stop press is about: the one deployment on the platform and account connected now that is not over, or null.</summary>
    string? RunningDeploymentId()
    {
        try
        {
            return _host.Gateway.DeploymentReadings()
                .FirstOrDefault(d => d.State != DeploymentState.Ended
                                     && d.ConnectorId == _host.Gateway.Connector.Id
                                     && d.AccountId == _host.Gateway.ClosureAccountId)?.Id;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Takes the stop press, AWAITED: ending a run cancels orders and closes a position, and the owner is told the outcome.</summary>
    async void ApplyDeploymentStop(string id)
    {
        string said;
        try
        {
            var ended = await _host.Gateway.EndPaperDeploymentAsync(id, "stopped by the account owner");
            said = ended is null
                ? "that deployment is no longer on this installation, so nothing was ended."
                : _host.Gateway.DeploymentReadings().FirstOrDefault(d => d.Id == ended.Id)?.Line
                  ?? $"the run is {ended.State}.";
        }
        catch (Exception ex) { said = ex.Message; }

        _envelopeNote.Text = said;
        _envelopeNote.Foreground = Theme.Caution;
        _envelopeNote.IsVisible = true;
        ShowEnvelopes();
    }

    void ApplyEnvelopeWithdrawal(string id)
    {
        var result = _host.Gateway.WithdrawPaperEnvelope(id);
        _envelopeNote.Text = result.Why;
        _envelopeNote.Foreground = result.Ok ? Theme.Positive : Theme.Caution;
        _envelopeNote.IsVisible = true;
        ShowEnvelopes();
    }

    /// <summary>
    /// WHAT STANDS GRANTED AND WHAT IS RUNNING, in the live lines rather than a rebuilt tree — each in its own try, so a ledger that would
    /// not read leaves the other line intact.
    /// </summary>
    void ShowEnvelopes()
    {
        try
        {
            var standing = _host.Gateway.Envelopes.All()
                .Where(e => e.StandsAt(DateTimeOffset.UtcNow))
                .ToList();

            _envelopeValue.Text = standing.Count == 0
                ? "nothing — TradeAgent may start no paper experiments of its own"
                : string.Join("; ", standing.Select(e =>
                    $"{e.Symbol} on {e.AccountId} at {e.ConnectorId}, up to "
                    + $"{AllocationRow.Num(e.MaxQuantity)}, until {e.ExpiresAt:yyyy-MM-dd} — "
                    + "no capital, no live authority"));

            _withdrawEnvelope.IsEnabled = standing.Count > 0;
        }
        catch (Exception) { _envelopeValue.Text = "could not be read"; }

        try
        {
            var running = _host.Gateway.DeploymentReadings()
                .Where(d => d.Outstanding)
                .ToList();

            _deploymentValue.Text = running.Count == 0
                ? "nothing — TradeAgent is running no paper experiment right now"
                : string.Join("; ", running.Select(d => d.Line));

            _stopDeployment.IsEnabled = RunningDeploymentId() is not null;
        }
        catch (Exception)
        {
            _deploymentValue.Text = "could not be read";
            _stopDeployment.IsEnabled = false;
        }
    }
}

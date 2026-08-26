using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TradeAgent.AgentRuntime;
using TradeAgent.Connectors.Atas;
using TradeAgent.Core;
using TradeAgent.Provisioning;

// Aliased rather than imported: Avalonia.Controls.Shapes also defines Path, and this file needs
// System.IO.Path far more often than it needs a vector shape.
using Ellipse = Avalonia.Controls.Shapes.Ellipse;

namespace TradeAgent.App;

/// <summary>
/// Setup: the fifteen minutes that decide whether a nontechnical trader ever gets this working.
///
/// Two rules shape the machine underneath, and neither changed here:
///   - a step the software can verify for itself never asks the user to confirm they did it;
///   - progress lives in the database, so a crash or a Windows restart resumes where it stopped.
///
/// Two rules shape what is on the screen:
///   - one frame, one middle. Every step is a step rail, a title, a sentence, a body and a footer,
///     so moving between steps never re-teaches the user where to look;
///   - exactly one primary button per screen, and never one on a screen whose answer is a choice —
///     a row of identical buttons is a screen that refuses to say what to press.
/// </summary>
public sealed class OnboardingView
{
    /// <summary>
    /// A reading measure, not a window width. Setup is prose; full-bleed paragraphs on a 760px
    /// window are what makes a wizard feel like a form to be endured.
    /// </summary>
    const double ContentWidth = 620;

    /// <summary>Keeps the footer at the bottom of a frame instead of tight under a short body.</summary>
    const double FrameHeight = 430;

    readonly AppHost _host;
    readonly Action _rerender;
    readonly DispatcherTimer _poll;
    string _note = "";
    bool _busy;
    IReadOnlyList<ConnectorSdk.AccountInfo>? _accounts;
    bool _loadingAccounts;

    // System check: the probe already runs the doctor every two seconds, so the screen reads its
    // result rather than running a second one of its own.
    IReadOnlyList<Diagnostics.CheckResult>? _checks;
    string _checksSignature = "";

    // Install: started automatically on entry, reported stage by stage.
    bool _installStarted, _installing;
    readonly List<string> _installStages = [];
    string? _installProblem, _installDetail;

    // ATAS installs itself too, but on a button rather than on arrival: it is a 459 MB third-party
    // trading platform, and downloading that unasked the moment a screen appears is not confidence,
    // it is presumption.
    bool _atasInstalling;
    readonly List<string> _atasStages = [];
    string? _atasProblem;

    // Sign-in: what the runtime handed back for the user to do.
    AuthChallenge? _auth;

    public OnboardingView(AppHost host, Action rerender)
    {
        _host = host;
        _rerender = rerender;
        // Slow poll: setup steps resolve in seconds, and this must not spin a laptop's fan.
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _poll.Tick += async (_, _) => await CheckAsync();
        _poll.Start();
    }

    /// <summary>
    /// One step's contribution to the frame. The frame itself — rail, title, footer, note — is built
    /// once in <see cref="Build"/>, so a new step cannot accidentally invent its own layout.
    /// </summary>
    sealed record Screen(
        string Lede,
        Control? Body = null,
        Button? Primary = null,
        Button[]? Alternatives = null,
        bool HideBack = false);

    // ---- the frame -----------------------------------------------------------------------------

    public Control Build()
    {
        var step = _host.Onboarding.Current();
        var here = Math.Max(0, Array.IndexOf(OnboardingSteps.Order, step));
        var total = OnboardingSteps.Order.Length;
        var screen = Compose(step);

        var head = Ui.Col(Theme.S6,
            Ui.Col(Theme.S3, Ui.Eyebrow($"Step {here + 1} of {total}"), Rail(here, total)),
            Ui.Col(Theme.S2, Ui.Display(step.Title()), Ui.Muted(screen.Lede)));

        var body = Ui.Col(Theme.S5);
        body.Margin = new Thickness(0, Theme.S8, 0, Theme.S6);
        body.VerticalAlignment = VerticalAlignment.Top;
        if (screen.Body is not null) body.Children.Add(screen.Body);
        // Busy and the problem note are mutually exclusive on purpose: an old failure sitting next to
        // a live spinner reads as a screen that has failed and is pretending otherwise.
        if (_busy) body.Children.Add(Ui.Busy("Working."));
        else if (!string.IsNullOrWhiteSpace(_note)) body.Children.Add(Note(_note, Theme.Caution));

        var frame = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            MaxWidth = ContentWidth,
            MinHeight = FrameHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, Theme.S10, 0, Theme.S4)
        };
        frame.Children.Add(head);
        body[Grid.RowProperty] = 1;
        frame.Children.Add(body);
        var footer = Footer(screen);
        footer[Grid.RowProperty] = 2;
        frame.Children.Add(footer);
        return frame;
    }

    /// <summary>
    /// The step rail. Sixteen steps is a long way, and a 4px bar filling by seven percent tells a
    /// worried user nothing; discrete marks say "there are this many, you are here".
    /// </summary>
    static Control Rail(int here, int total)
    {
        var columns = new ColumnDefinitions();
        for (var i = 0; i < total; i++)
        {
            if (i > 0) columns.Add(new ColumnDefinition(GridLength.Star));
            columns.Add(new ColumnDefinition(GridLength.Auto));
        }

        var rail = new Grid { ColumnDefinitions = columns, Height = 12 };
        for (var i = 0; i < total; i++)
        {
            var column = i * 2;
            if (i > 0)
                rail.Children.Add(new Border
                {
                    Height = 2,
                    Background = i <= here ? Theme.Accent : Theme.Line,
                    VerticalAlignment = VerticalAlignment.Center,
                    [Grid.ColumnProperty] = column - 1
                });

            var size = i == here ? 11d : 7d;
            rail.Children.Add(new Ellipse
            {
                Width = size,
                Height = size,
                Fill = i <= here ? Theme.Accent : Theme.Line,
                VerticalAlignment = VerticalAlignment.Center,
                [Grid.ColumnProperty] = column
            });
        }
        return rail;
    }

    Control Footer(Screen screen)
    {
        var right = Ui.Row(Theme.S2);
        right.HorizontalAlignment = HorizontalAlignment.Right;
        foreach (var alternative in screen.Alternatives ?? []) right.Children.Add(alternative);
        if (screen.Primary is not null) right.Children.Add(screen.Primary);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        // Every screen after the first needs a way out. Setup waits on conditions the user may not
        // be able to meet yet — ATAS not installed, the AI tool not signed in — and without this a
        // wrong choice on an earlier screen is unrecoverable except by deleting the database.
        if (!screen.HideBack && PreviousDecision() is { } back)
            footer.Children.Add(Ui.With(Ui.Ghost($"← Back to “{back.Title()}”", GoBack),
                b => b.HorizontalAlignment = HorizontalAlignment.Left));

        right[Grid.ColumnProperty] = 1;
        footer.Children.Add(right);
        return footer;
    }

    // ---- shared pieces -------------------------------------------------------------------------

    /// <summary>
    /// A note or a problem. A tinted left edge rather than a full border: it marks the paragraph
    /// without drawing a box around it, which is the difference between an aside and an alarm.
    /// </summary>
    static Control Note(string text, IBrush tone) => new Border
    {
        Background = tone == Theme.Caution ? Theme.CautionSoft
            : tone == Theme.Positive ? Theme.PositiveSoft
            : tone == Theme.Danger ? Theme.DangerSoft
            : Theme.AccentSoft,
        BorderBrush = tone,
        BorderThickness = new Thickness(2, 0, 0, 0),
        CornerRadius = Theme.RadiusSm,
        Padding = new Thickness(Theme.S4, Theme.S3),
        Child = Ui.Body(text)
    };

    /// <summary>
    /// One option, as a card. Built on the secondary button variant so hover, press, focus and
    /// keyboard activation come from the theme rather than from hand-wired pointer events.
    /// </summary>
    Button Choice(string title, Control detail, Control? badge, Func<Task> choose)
    {
        var heading = Ui.Row(Theme.S3, Ui.H2(title));
        if (badge is not null) heading.Children.Add(badge);

        var card = new Button
        {
            Classes = { "secondary" },
            Content = Ui.Col(Theme.S1, heading, detail),
            Padding = new Thickness(Theme.S4, Theme.S4),
            CornerRadius = Theme.Radius,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        card.Click += async (_, _) =>
        {
            card.IsEnabled = false;
            try { await choose(); }
            catch (Exception ex) { Ui.ReportError?.Invoke(ex.Message); }
            finally { card.IsEnabled = true; }
        };
        return card;
    }

    /// <summary>
    /// A step the user performs somewhere else. The numbered badge exists because these are followed
    /// one at a time by someone alt-tabbing to another program, and a paragraph loses their place.
    /// </summary>
    static Control Numbered(int n, string title, string? detail = null)
    {
        var badge = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = Theme.Pill,
            Background = Theme.AccentSoft,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = n.ToString(),
                FontSize = Theme.Micro,
                FontWeight = FontWeight.SemiBold,
                Foreground = Theme.Accent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var text = Ui.Col(2, Ui.Body(title));
        if (!string.IsNullOrWhiteSpace(detail)) text.Children.Add(Ui.Micro(detail!));
        text.Margin = new Thickness(Theme.S3, 1, 0, 0);
        text[Grid.ColumnProperty] = 1;

        return new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { badge, text } };
    }

    /// <summary>One thing that is or is not true yet, with the colour carrying the verdict.</summary>
    static Control StateRow(IBrush tone, string label, string? detail = null)
    {
        var text = Ui.Col(2, Ui.Body(label));
        if (!string.IsNullOrWhiteSpace(detail)) text.Children.Add(Ui.Micro(detail!));
        text.Margin = new Thickness(Theme.S3, 0, 0, 0);
        text[Grid.ColumnProperty] = 1;

        var dot = new Ellipse
        {
            Width = 7, Height = 7, Fill = tone,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 7, 0, 0)
        };
        return new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { dot, text } };
    }

    /// <summary>
    /// Wraps a step's action in the busy/report/re-check cycle every action needs. Without it a
    /// failure inside an async click handler is an unobserved fault, which ends the process.
    /// </summary>
    Func<Task> Act(Func<Task> action) => async () =>
    {
        _busy = true;
        _note = "";
        _rerender();
        try { await action(); _note = ""; }
        catch (TradeAgentException ex) { _note = $"{ex.Info.UserMessage}\n{ex.Info.Repair}"; }
        catch (Exception ex) { _note = ex.Message; }
        finally { _busy = false; }
        await CheckAsync();
        _rerender();
    };

    Func<Task> Act(Action action) => Act(() => { action(); return Task.CompletedTask; });

    // ---- navigation ----------------------------------------------------------------------------

    /// <summary>
    /// The steps where the user actually chooses something, and therefore the steps it is meaningful
    /// to send them back to. This is exactly the set the screen definitions used to select with
    /// "has actions and has no automatic check", stated directly so that walking backwards costs a
    /// comparison instead of building — and side-effecting — every earlier screen.
    /// </summary>
    static bool IsDecision(OnboardingStep step) => step is
        OnboardingStep.WELCOME or
        OnboardingStep.AI_RUNTIME_SELECTED or
        OnboardingStep.TRADING_PLATFORM_SELECTED or
        OnboardingStep.AGENT_READY or
        OnboardingStep.SETUP_COMPLETE;

    /// <summary>The last screen before this one where the user actually chose something.</summary>
    OnboardingStep? PreviousDecision()
    {
        var current = _host.Onboarding.Current();
        var here = Array.IndexOf(OnboardingSteps.Order, current);
        for (var i = here - 1; i >= 0; i--)
        {
            var s = OnboardingSteps.Order[i];
            if (IsDecision(s)) return s;
        }
        return null;
    }

    void GoBack()
    {
        if (PreviousDecision() is not { } target) return;
        var from = Array.IndexOf(OnboardingSteps.Order, target);
        for (var i = from; i < OnboardingSteps.Order.Length; i++)
            _host.Onboarding.Clear(OnboardingSteps.Order[i]);
        _note = "";
        _accounts = null;
        ResetStepState();
        if (!_poll.IsEnabled) _poll.Start();
        _rerender();
    }

    /// <summary>
    /// Everything a screen accumulated while it was on show. Going back to choose a different AI
    /// assistant must not leave the previous one's install log on the next attempt.
    /// </summary>
    void ResetStepState()
    {
        _installStarted = false;
        _installing = false;
        _installStages.Clear();
        _installProblem = null;
        _installDetail = null;
        _auth = null;
        _checks = null;
        _checksSignature = "";
    }

    public void ShowProblem(string message)
    {
        _note = message;
        _rerender();
    }

    // ---- the automatic advance -----------------------------------------------------------------

    /// <summary>Advances by itself the moment the condition it is waiting for becomes true.</summary>
    async Task CheckAsync()
    {
        if (_busy) return;
        var step = _host.Onboarding.Current();
        var probe = Probe(step);
        if (probe is null) return;
        try
        {
            if (!await probe()) return;
            _host.Onboarding.Complete(step);
            _host.Gateway.Log.Activity($"Setup: {step.Title()} — done");
            if (_host.Onboarding.IsComplete()) _poll.Stop();
            _rerender();
        }
        catch (Exception) { /* a probe that fails is just "not yet" */ }
    }

    /// <summary>
    /// What each waiting step is waiting for. Deliberately separate from the screens: this runs
    /// every two seconds, and it must not build controls to answer a yes/no question.
    /// </summary>
    Func<Task<bool>>? Probe(OnboardingStep step) => step switch
    {
        OnboardingStep.SYSTEM_CHECK => async () =>
        {
            var report = await _host.RunDoctorAsync();
            RecordChecks(report.Checks);
            // Only blockers stop setup. A missing AI tool or ATAS is handled by its own step.
            return !report.Checks.Any(c => c.State == HealthState.FAILED &&
                c.Name is "Processor" or "Free disk space" or "Folder permissions");
        },

        OnboardingStep.AI_RUNTIME_INSTALLED => async () =>
            Runtime() is { } rt && (await rt.DetectAsync()).Installed,

        OnboardingStep.AI_AUTHENTICATED => async () =>
            Runtime() is { } rt && await rt.GetAuthenticationStateAsync() == AuthState.Authenticated,

        OnboardingStep.ATAS_INSTALLED =>
            () => Task.FromResult(!UsingAtas || AtasInstallation.Detect().Installed),

        OnboardingStep.ATAS_BRIDGE_INSTALLED =>
            () => Task.FromResult(!UsingAtas || AtasInstallation.Detect().BridgeInstalled),

        OnboardingStep.ATAS_BRIDGE_CONNECTED => async () =>
            !UsingAtas || await _host.Connector.IsConnectedAsync(),

        OnboardingStep.TRADING_CONNECTION_FOUND => async () =>
            await _host.Connector.GetHealthAsync() == HealthState.READY,

        OnboardingStep.ACCOUNT_SELECTED =>
            () => Task.FromResult(_host.Gateway.Settings.SelectedAccountId is not null),

        OnboardingStep.MARKET_DATA_VERIFIED => async () =>
        {
            var first = (await _host.Gateway.InstrumentsAsync()).FirstOrDefault();
            if (first is null) return false;
            var q = await _host.Gateway.QuoteAsync(first.Symbol);
            return q is not null && !q.IsStale(TimeSpan.FromMinutes(1));
        },

        OnboardingStep.ORDER_ACCESS_VERIFIED => async () =>
            (await _host.Gateway.AccountAsync())?.TradingEnabled == true,

        OnboardingStep.WORKSPACE_CREATED =>
            () => Task.FromResult(File.Exists(Path.Combine(Paths.Workspace, "AGENTS.md"))),

        _ => null
    };

    void Done(OnboardingStep s) => _host.Onboarding.Complete(s);

    IAgentRuntime? Runtime()
    {
        var id = _host.Gateway.Settings.SelectedRuntimeId;
        var manifest = id is null ? null : RuntimeCatalog.Find(id);
        return manifest is null ? null : new CliAgentRuntime(manifest);
    }

    RuntimeManifest? Manifest() =>
        RuntimeCatalog.Find(_host.Gateway.Settings.SelectedRuntimeId ?? "opencode");

    bool UsingAtas => (_host.Db.GetKv("connector") ?? "fake") == "atas";

    // ---- the screens ---------------------------------------------------------------------------

    Screen Compose(OnboardingStep step) => step switch
    {
        OnboardingStep.WELCOME => Welcome(),
        OnboardingStep.SYSTEM_CHECK => SystemCheck(),
        OnboardingStep.AI_RUNTIME_SELECTED => ChooseRuntime(),
        OnboardingStep.AI_RUNTIME_INSTALLED => InstallRuntime(),
        OnboardingStep.AI_AUTHENTICATED => SignIn(),
        OnboardingStep.TRADING_PLATFORM_SELECTED => ChoosePlatform(),
        OnboardingStep.ATAS_INSTALLED => FindAtas(),
        OnboardingStep.ATAS_BRIDGE_INSTALLED => InstallBridge(),
        OnboardingStep.ATAS_BRIDGE_CONNECTED => ConnectBridge(),
        OnboardingStep.TRADING_CONNECTION_FOUND => new Screen(
            "Making sure your trading platform is logged in and reachable.",
            Ui.Busy("Waiting for the trading connection.")),
        OnboardingStep.ACCOUNT_SELECTED => ChooseAccount(),
        OnboardingStep.MARKET_DATA_VERIFIED => new Screen(
            "Checking that live prices are arriving. The AI must never size a trade from a stale price.",
            Ui.Busy("Waiting for prices.")),
        OnboardingStep.ORDER_ACCESS_VERIFIED => new Screen(
            "Checking that this account is allowed to place orders. This only checks permission — no order is placed.",
            Ui.Busy("Checking trading access.")),
        OnboardingStep.WORKSPACE_CREATED => CreateWorkspace(),
        OnboardingStep.AGENT_READY => StartAgent(),
        _ => new Screen(
            "Setup is finished. TradeAgent will now show you the main screen.",
            Note("Real money stays switched off until you turn it on yourself. Nothing you do next can " +
                 "change that by accident.", Theme.Positive),
            Ui.Primary("Finish", Act(() => Done(OnboardingStep.SETUP_COMPLETE)))),
    };

    Screen Welcome() => new(
        "TradeAgent gives an AI assistant a safe, supervised way to work with your ATAS trading platform.",
        Ui.Card(Ui.Col(Theme.S4,
            Numbered(1, "Set up the AI",
                "TradeAgent downloads and installs everything it needs, by itself. You will never be asked to type anything technical."),
            Numbered(2, "Connect to your trading platform",
                "Start with the built-in practice simulator. Move to ATAS whenever you are ready."),
            Numbered(3, "Decide what the AI may do",
                "Watch only, suggest and ask, or trade. You choose, and you can change your mind at any time."))),
        Ui.Primary("Get started", Act(() => Done(OnboardingStep.WELCOME))));

    Screen SystemCheck()
    {
        Control body;
        if (_checks is null)
        {
            body = Ui.Card(Ui.Busy("Looking at this computer."));
        }
        else
        {
            var rows = Ui.Col(Theme.S3);
            foreach (var c in _checks)
                rows.Children.Add(StateRow(Ui.Tone(c.State), c.Name,
                    c.State == HealthState.READY
                        ? (string.IsNullOrWhiteSpace(c.Detail) ? "Ready." : c.Detail)
                        : string.IsNullOrWhiteSpace(c.UserAction) ? c.Detail : c.UserAction));
            body = Ui.Card(rows);
        }

        return new Screen(
            "TradeAgent is making sure this computer has what it needs. Anything missing is named below, " +
            "with what to do about it.",
            body,
            Ui.Primary("Check again", Act(async () => RecordChecks((await _host.RunDoctorAsync()).Checks))));
    }

    /// <summary>
    /// Keeps the doctor's last answer for the screen to draw, and redraws only when the answer
    /// actually changed — this arrives every two seconds and a rebuild every two seconds is a screen
    /// that flickers while the user is trying to read it.
    /// </summary>
    void RecordChecks(IReadOnlyList<Diagnostics.CheckResult> checks)
    {
        var signature = string.Join('|', checks.Select(c => $"{c.Name}:{c.State}:{c.Detail}"));
        if (_checksSignature == signature) return;
        _checksSignature = signature;
        _checks = checks;
        _rerender();
    }

    Screen ChooseRuntime()
    {
        var cards = Ui.Col(Theme.S3);
        // Recommended first. The order is not cosmetic: the recommended runtime is the one whose
        // sign-in finishes without leaving this window, and a user who picks by reading top-to-bottom
        // should land on it.
        foreach (var m in RuntimeCatalog.Load().Where(m => m.Id != "custom").OrderByDescending(m => m.Recommended))
        {
            var manifest = m;
            cards.Children.Add(Choice(manifest.DisplayName, Ui.Muted(manifest.Description),
                manifest.Recommended ? Ui.Pill("RECOMMENDED", Theme.Positive) : null,
                Act(() =>
                {
                    _host.Gateway.Update(s => s.SelectedRuntimeId = manifest.Id);
                    ResetStepState();
                    Done(OnboardingStep.AI_RUNTIME_SELECTED);
                })));
        }

        // No primary button: the answer to this screen is one of the cards, and a primary alongside
        // them would be a fourth thing to press that does not answer the question.
        return new Screen(
            "Both run on this computer and talk to an AI service over the internet. You can change this later.",
            cards);
    }

    Screen InstallRuntime()
    {
        var name = Manifest()?.DisplayName ?? "the AI assistant";
        EnsureInstallStarted();

        if (_installProblem is not null)
        {
            var explanation = Ui.Col(Theme.S3,
                Note(_installProblem, Theme.Caution));
            if (!string.IsNullOrWhiteSpace(_installDetail))
                explanation.Children.Add(Ui.With(Ui.Micro(_installDetail!), t => t.Margin = new Thickness(2, 0, 0, 0)));

            var alternatives = new List<Button>
            {
                Ui.Ghost("Choose a different AI assistant", GoBack)
            };
            if (DownloadPage() is { } page)
                alternatives.Add(Ui.Ghost("Open the download page in your browser",
                    () => MainWindow.OpenPath(page, ShowProblem)));

            return new Screen(
                $"TradeAgent could not finish installing {name}.",
                explanation,
                Ui.Primary("Try again", Act(RestartInstall)),
                [.. alternatives],
                // The ghost above already goes back, and two controls doing the same thing on the
                // same screen is how a user learns to distrust both.
                HideBack: true);
        }

        var stages = Ui.Col(Theme.S3);
        for (var i = 0; i < _installStages.Count; i++)
        {
            var last = i == _installStages.Count - 1;
            if (last && _installing) stages.Children.Add(Ui.Busy(_installStages[i]));
            else stages.Children.Add(StateRow(Theme.Positive, _installStages[i]));
        }
        if (_installStages.Count == 0) stages.Children.Add(Ui.Busy("Getting ready."));
        else if (!_installing) stages.Children.Add(Ui.Busy("Finishing up."));

        return new Screen(
            $"TradeAgent is installing {name} for you. There is nothing to do here — this usually takes a " +
            "minute or two, and the next step opens by itself.",
            Ui.Card(stages));
    }

    string? DownloadPage()
    {
        var m = Manifest();
        return m?.Install.ManualUrl ?? m?.DocsUrl;
    }

    /// <summary>
    /// Starts the install the moment the screen appears. The product's promise is that it installs
    /// what it needs itself; a button labelled "install it for me" is that promise asking permission.
    /// </summary>
    void EnsureInstallStarted()
    {
        if (_installStarted) return;
        _installStarted = true;
        _installProblem = null;
        _installDetail = null;
        _installStages.Clear();

        if (Runtime() is not { } rt)
        {
            _installing = false;
            _installProblem = "TradeAgent does not know which AI assistant to install. Go back one step and choose one.";
            return;
        }

        _installing = true;
        _ = Task.Run(async () =>
        {
            try
            {
                Stage("Checking whether it is already on this computer.");
                var found = await rt.DetectAsync();
                if (!found.Installed) found = await rt.InstallAsync(new Progress<string>(Stage));

                if (found.Installed)
                {
                    Stage($"{rt.DisplayName} is ready.");
                    Settle(null, null);
                }
                else
                {
                    Settle($"TradeAgent finished the download but could not find {rt.DisplayName} afterwards.", null);
                }
            }
            catch (TradeAgentException ex)
            {
                Settle(null, $"{ex.Info.UserMessage} {ex.Info.Repair}".Trim());
            }
            catch (Exception ex)
            {
                Settle(null, ex.Message);
            }
        });
    }

    /// <summary>Adds one stage to the visible list. Always marshalled: the install runs off the UI thread.</summary>
    void Stage(string message) => Dispatcher.UIThread.Post(() =>
    {
        var text = message.Trim().TrimEnd('.', '…') + ".";
        if (_installStages.Count > 0 && _installStages[^1] == text) return;
        _installStages.Add(text);
        if (_host.Onboarding.Current() == OnboardingStep.AI_RUNTIME_INSTALLED) _rerender();
    });

    void Settle(string? problem, string? detail) => Dispatcher.UIThread.Post(() =>
    {
        _installing = false;
        _installProblem = problem ?? (detail is null
            ? null
            : "The install did not finish. This is usually a temporary problem with the internet connection.");
        _installDetail = detail;
        _rerender();
    });

    Task RestartInstall()
    {
        _installStarted = false;
        EnsureInstallStarted();
        return Task.CompletedTask;
    }

    Screen SignIn()
    {
        var manifest = Manifest();
        // The reassurance is only true of the browser flow. On the key path TradeAgent does handle
        // the key, briefly, and saying otherwise two inches above the field would be a lie the user
        // can see through.
        var lede = manifest is { AuthArgs.Length: > 0 }
            ? $"{manifest.SignInDescription} TradeAgent never sees your password — the AI service handles sign-in itself.".Trim()
            : (manifest?.SignInDescription ?? "").Trim();

        var key = manifest?.ApiKey;
        var browser = manifest is { AuthArgs.Length: > 0 };

        if (_auth is null)
        {
            // A runtime with no browser flow — OpenCode — would otherwise have a Sign in button that
            // cannot work, and the only honest instruction left would send the user to a terminal.
            if (!browser && key is not null)
                return new Screen(lede, KeyEntry(key), _keyButton, [], HideBack: false);

            var intro = Ui.Col(Theme.S5,
                Note("Press Sign in. TradeAgent opens the sign-in page in your web browser, and this screen " +
                     "continues on its own as soon as you are signed in.", Theme.Info));
            if (key is not null) intro.Children.Add(KeyEntry(key, alternative: true));

            return new Screen(lede, intro, Ui.Primary("Sign in", Act(BeginSignInAsync)));
        }

        var body = Ui.Col(Theme.S5);
        if (!string.IsNullOrWhiteSpace(_auth.Message)) body.Children.Add(Ui.Body(_auth.Message));
        if (!string.IsNullOrWhiteSpace(_auth.Code)) body.Children.Add(CodeWell(_auth.Code!));
        if (!string.IsNullOrWhiteSpace(_auth.Url)) body.Children.Add(LinkWell(_auth.Url!));
        body.Children.Add(Ui.Busy("Waiting for you to finish signing in."));

        var primary = _auth.Url is { } url
            ? Ui.Primary("Open the sign-in page again", () => MainWindow.OpenPath(url, ShowProblem))
            : Ui.Primary("Start sign-in again", Act(BeginSignInAsync));

        return new Screen(lede, Ui.Card(body), primary,
            [Ui.Ghost("I have signed in", Act(() => Done(OnboardingStep.AI_AUTHENTICATED)))]);
    }

    Button? _keyButton;

    /// <summary>
    /// A field for a key the user pastes in. TradeAgent hands it straight to the AI tool and keeps
    /// no copy — this is the one place a credential passes through the product, and it exists so
    /// that no version of "open a terminal and run the login command" ever has to be printed.
    /// </summary>
    Control KeyEntry(ApiKeyPlan plan, bool alternative = false)
    {
        var field = new TextBox
        {
            PasswordChar = '\u2022',
            PlaceholderText = plan.Label,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Width = double.NaN
        };

        _keyButton = Ui.Primary(alternative ? "Use this key instead" : "Sign in with this key", async () =>
        {
            if (Runtime() is not { } rt) throw new TradeAgentException(ErrorCode.AI_RUNTIME_NOT_FOUND);
            await rt.SignInWithApiKeyAsync(field.Text ?? "");
            field.Text = "";
            _note = "";
            await CheckAsync();
            _rerender();
        });

        var row = Ui.Col(Theme.S3,
            Ui.Eyebrow(alternative ? "Or use a key" : "Paste your key"),
            field);

        if (plan.HelpUrl is { } help)
            // Pulled left by the ghost button's own padding so its text lines up with the field
            // above it rather than sitting indented from it.
            row.Children.Add(Ui.With(Ui.Ghost("Where do I find this?", () => MainWindow.OpenPath(help, ShowProblem)),
                b => { b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(-Theme.S4, 0, 0, 0); }));

        row.Children.Add(Ui.Micro("TradeAgent passes the key straight to the AI tool and keeps no copy of it."));
        return Ui.Card(row);
    }

    async Task BeginSignInAsync()
    {
        if (Runtime() is not { } rt) throw new TradeAgentException(ErrorCode.AI_RUNTIME_NOT_FOUND);
        _auth = await rt.BeginAuthenticationAsync();
        // The app opens the browser, so the user never has to find and click a link somewhere else.
        if (!string.IsNullOrWhiteSpace(_auth.Url)) MainWindow.OpenPath(_auth.Url!, ShowProblem);
    }

    /// <summary>A device code, sized to be read off the screen and typed into a phone.</summary>
    static Control CodeWell(string code) => Ui.Col(Theme.S2,
        Ui.Eyebrow("Enter this code on the sign-in page"),
        new Border
        {
            Background = Theme.BgSunken,
            CornerRadius = Theme.RadiusSm,
            Padding = new Thickness(Theme.S5, Theme.S4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = code,
                FontFamily = Theme.Mono,
                FontSize = Theme.Display,
                FontWeight = FontWeight.SemiBold,
                Foreground = Theme.Text,
                LetterSpacing = 3
            }
        });

    /// <summary>The address, in full, for the case where the browser did not open by itself.</summary>
    Control LinkWell(string url)
    {
        Button copy = null!;
        copy = Ui.Secondary("Copy link", async () =>
        {
            var clipboard = TopLevel.GetTopLevel(copy)?.Clipboard;
            if (clipboard is null) { ShowProblem("TradeAgent could not reach the Windows clipboard."); return; }
            await clipboard.SetTextAsync(url);
            copy.Content = "Link copied";
        });
        copy[Grid.ColumnProperty] = 1;
        copy.Margin = new Thickness(Theme.S2, 0, 0, 0);
        copy.VerticalAlignment = VerticalAlignment.Top;

        var well = new Border
        {
            Background = Theme.BgSunken,
            CornerRadius = Theme.RadiusSm,
            Padding = new Thickness(Theme.S4, Theme.S3),
            Child = Ui.With(Ui.Mono(url), t => t.TextWrapping = TextWrapping.Wrap)
        };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { well, copy } };
        return Ui.Col(Theme.S2, Ui.Eyebrow("If your browser did not open, go to this address"), row);
    }

    Screen ChoosePlatform() => new(
        "You can switch later, and you will still choose separately whether real money is allowed.",
        Ui.Col(Theme.S3,
            Choice("Practice simulator",
                Ui.Muted("A built-in fake account. Nothing here is real and nothing can be lost. Start here."),
                Ui.Pill("RECOMMENDED", Theme.Positive),
                Act(async () =>
                {
                    await _host.SwitchConnectorAsync("fake");
                    Done(OnboardingStep.TRADING_PLATFORM_SELECTED);
                })),
            Choice("ATAS",
                Ui.Muted("Your real trading platform. TradeAgent connects to it and stays inside the limits you set."),
                null,
                Act(async () =>
                {
                    await _host.SwitchConnectorAsync("atas");
                    Done(OnboardingStep.TRADING_PLATFORM_SELECTED);
                }))));

    Screen FindAtas()
    {
        if (!UsingAtas) return Skipped("You chose the practice simulator, so ATAS is not needed.");

        // The account note is on every variant of this screen on purpose. It is the one part of the
        // journey TradeAgent genuinely cannot do for the user, and finding that out after a 459 MB
        // download would be the worst possible moment to learn it.
        var accountNote = Note(
            "ATAS is free to install, but it needs a free ATAS account to run — sign up at atas.net and " +
            "the password arrives by email. That part is yours; TradeAgent cannot create an account for you.",
            Theme.Info);

        if (_atasInstalling || _atasStages.Count > 0)
        {
            var stages = Ui.Col(Theme.S3);
            for (var i = 0; i < _atasStages.Count; i++)
            {
                var last = i == _atasStages.Count - 1;
                if (last && _atasInstalling) stages.Children.Add(Ui.Busy(_atasStages[i]));
                else stages.Children.Add(StateRow(Theme.Positive, _atasStages[i]));
            }
            if (_atasStages.Count == 0) stages.Children.Add(Ui.Busy("Getting ready."));

            if (_atasProblem is not null)
                return new Screen(
                    "TradeAgent could not finish installing ATAS.",
                    Ui.Col(Theme.S4, Note(_atasProblem, Theme.Caution), accountNote),
                    Ui.Primary("Try again", Act(InstallAtasAsync)),
                    [Ui.Ghost("Install it myself from atas.net", () => MainWindow.OpenPath(AtasPrerequisite.DownloadPageUrl, ShowProblem))],
                    HideBack: true);

            return new Screen(
                "TradeAgent is installing ATAS for you. Windows will ask you for permission once — that is " +
                "the only thing you need to do. This one is a large download.",
                Ui.Col(Theme.S4, Ui.Card(stages), accountNote));
        }

        return new Screen(
            "ATAS is not on this computer yet. TradeAgent can fetch it from atas.net and install it for you.",
            Ui.Col(Theme.S4, Ui.Card(Ui.Busy("Looking for ATAS.")), accountNote),
            Ui.Primary("Install ATAS for me", Act(InstallAtasAsync)),
            [Ui.Ghost("I will install it myself", () => MainWindow.OpenPath(AtasPrerequisite.DownloadPageUrl, ShowProblem))]);
    }

    /// <summary>
    /// Runs the vendor's own installer, silently, behind one Windows permission prompt. The step's
    /// auto-check notices ATAS appearing and moves on by itself, so nothing here has to advance it.
    /// </summary>
    async Task InstallAtasAsync()
    {
        _atasProblem = null;
        _atasStages.Clear();
        _atasInstalling = true;
        _rerender();

        var progress = new Progress<ProvisionProgress>(p => Dispatcher.UIThread.Post(() =>
        {
            // Download progress arrives many times a second; replacing the last line keeps the list
            // a list of stages rather than a thousand-line log.
            if (_atasStages.Count > 0 && _atasStages[^1].StartsWith("Downloading", StringComparison.Ordinal)
                                      && p.Message.StartsWith("Downloading", StringComparison.Ordinal))
                _atasStages[^1] = p.Message;
            else if (_atasStages.Count == 0 || _atasStages[^1] != p.Message)
                _atasStages.Add(p.Message);
            _rerender();
        }));

        try
        {
            await new AtasPrerequisite().InstallAsync(progress);
            _atasStages.Add("ATAS is installed.");
        }
        catch (TradeAgentException ex) { _atasProblem = $"{ex.Info.UserMessage} {ex.Info.Repair}".Trim(); }
        catch (Exception ex) { _atasProblem = ex.Message; }
        finally { _atasInstalling = false; _rerender(); }
    }

    Screen InstallBridge() => UsingAtas
        ? new Screen(
            "TradeAgent needs to place a small add-on inside ATAS so the two can talk to each other.",
            Note("Close ATAS first if it is open. The add-on cannot be placed while ATAS is using the folder.",
                Theme.Caution),
            Ui.Primary("Install the add-on", Act(() =>
            {
                var dir = AtasInstallation.InstallBridge(Path.Combine(AppContext.BaseDirectory, "bridge"));
                _note = $"The add-on is in place, in {dir}.";
            })))
        : Skipped("Not needed for the practice simulator.");

    Screen ConnectBridge() => UsingAtas
        ? new Screen(
            "One last thing to do inside ATAS. You do not need to tell TradeAgent when you are done — it notices by itself.",
            Ui.Col(Theme.S4,
                Ui.Card(Ui.Col(Theme.S4,
                    Numbered(1, "Open ATAS"),
                    Numbered(2, "Open a chart"),
                    Numbered(3, "Open Strategies for that chart"),
                    Numbered(4, "Choose TradeAgent Bridge"),
                    Numbered(5, "Press Add, then press Start"))),
                Ui.Busy("Waiting for ATAS to connect.")),
            Ui.Primary("Open ATAS", () => MainWindow.OpenAtasOrExplain(ShowProblem)))
        : Skipped("Not needed for the practice simulator.");

    /// <summary>A step that does not apply to this setup. It still gets a designed screen.</summary>
    static Screen Skipped(string why) => new(why, Ui.Busy("Moving on."));

    Screen ChooseAccount()
    {
        var cards = AccountCards();

        Control body;
        if (cards is null) body = Ui.Card(Ui.Busy("Looking for accounts."));
        else if (cards.Length == 0)
            body = Ui.Col(Theme.S4,
                Note("TradeAgent could not find any accounts on this trading connection. If you have just " +
                     "logged in, give it a moment and look again.", Theme.Caution));
        else
        {
            var list = Ui.Col(Theme.S3);
            foreach (var c in cards) list.Children.Add(c);
            body = list;
        }

        Button[] alternatives = cards is { Length: 0 }
            ? [Ui.Secondary("Look again", () => { _accounts = null; _rerender(); })]
            : [];

        return new Screen(
            "Choose the account the AI is allowed to see and trade. It will never touch any other account.",
            body, null, alternatives);
    }

    /// <summary>
    /// The account list, fetched in the background. It used to be pulled with GetAwaiter().GetResult()
    /// while building the screen, which blocks the UI thread on a broker round trip — a frozen window
    /// on any connection slower than the simulator. Null means "still looking".
    /// </summary>
    Button[]? AccountCards()
    {
        if (_accounts is null)
        {
            if (!_loadingAccounts)
            {
                _loadingAccounts = true;
                _ = Task.Run(async () =>
                {
                    try { _accounts = await _host.Gateway.AccountsAsync(); }
                    catch (Exception) { _accounts = []; }
                    finally
                    {
                        _loadingAccounts = false;
                        Dispatcher.UIThread.Post(() => _rerender());
                    }
                });
            }
            return null;
        }

        return _accounts.Select(a =>
        {
            var account = a;
            // This is the screen where someone picks a live account by accident, so the two kinds
            // never look alike: the pill is the loudest thing on the card.
            var badge = account.IsSimulated
                ? Ui.Pill("SIMULATION", Theme.Info)
                : Ui.Pill("REAL MONEY", Theme.Danger);

            var detail = Ui.Row(Theme.S3,
                Ui.Mono($"{account.Balance:N2} {account.Currency}", Theme.TextMuted),
                Ui.Mono(account.Id, Theme.TextFaint));

            return Choice(account.Name, detail, badge, Act(() =>
            {
                _host.Gateway.Update(s => s.SelectedAccountId = account.Id);
                Done(OnboardingStep.ACCOUNT_SELECTED);
            }));
        }).ToArray();
    }

    Screen CreateWorkspace() => new(
        "TradeAgent is about to create a folder for the AI to work in, with written rules about how it must " +
        "behave and what it is not allowed to do.",
        Note("The rules are a plain-text file you can read and change at any time. The AI is given them every " +
             "time it starts.", Theme.Info),
        Ui.Primary("Create it", Act(() => WorkspaceBuilder.Build(_host.WorkspaceContext()))));

    Screen StartAgent() => new(
        "Everything is ready. TradeAgent will start the AI and take you to the main screen, where you can talk to it.",
        Note("It starts in practice mode. Real money stays switched off until you allow it, deliberately, later.",
            Theme.Positive),
        Ui.Primary("Start the AI", Act(async () =>
        {
            var manifest = Manifest() ?? throw new TradeAgentException(ErrorCode.AI_RUNTIME_NOT_FOUND);
            await _host.Agent.PrepareAsync(manifest, _host.WorkspaceContext());
            await _host.Agent.StartAsync();
            Done(OnboardingStep.AGENT_READY);
        })));
}

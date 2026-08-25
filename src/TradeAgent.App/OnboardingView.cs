using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TradeAgent.AgentRuntime;
using TradeAgent.Connectors.Atas;
using TradeAgent.Core;

namespace TradeAgent.App;

/// <summary>
/// Setup, driven by the durable onboarding state machine rather than by which screen happens to be
/// showing. Two rules shape it:
///
///   - a step that the software can verify for itself never asks the user to confirm they did it;
///   - progress lives in the database, so a crash or a Windows restart resumes where it stopped.
/// </summary>
public sealed class OnboardingView
{
    readonly AppHost _host;
    readonly Action _rerender;
    readonly DispatcherTimer _poll;
    string _note = "";
    bool _busy;

    public OnboardingView(AppHost host, Action rerender)
    {
        _host = host;
        _rerender = rerender;
        // Slow poll: setup steps resolve in seconds, and this must not spin a laptop's fan.
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _poll.Tick += async (_, _) => await CheckAsync();
        _poll.Start();
    }

    sealed record Step(string Explain, (string Label, Func<Task> Run)[] Actions, Func<Task<bool>>? AutoCheck, string? Waiting);

    public Control Build()
    {
        var step = _host.Onboarding.Current();
        var def = Define(step);
        var done = _host.Onboarding.Completed().Count;
        var total = OnboardingSteps.Order.Length;

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(Ui.Label($"Setup — step {done + 1} of {total}"));
        panel.Children.Add(Ui.H1(step.Title()));
        panel.Children.Add(new ProgressBar { Minimum = 0, Maximum = total, Value = done, Height = 4 });
        panel.Children.Add(Ui.Body(def.Explain));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (label, run) in def.Actions)
        {
            var action = run;
            buttons.Children.Add(Ui.Button(label, async () =>
            {
                _busy = true;
                _note = "Working...";
                _rerender();
                try { await action(); _note = ""; }
                catch (TradeAgentException ex) { _note = $"{ex.Info.UserMessage}\n{ex.Info.Repair}"; }
                catch (Exception ex) { _note = ex.Message; }
                finally { _busy = false; }
                await CheckAsync();
                _rerender();
            }));
        }
        if (def.Actions.Length > 0) panel.Children.Add(buttons);

        if (def.Waiting is not null && !_busy)
            panel.Children.Add(Ui.Body(def.Waiting, Brushes.SteelBlue));
        if (!string.IsNullOrWhiteSpace(_note))
            panel.Children.Add(Ui.Card(Ui.Body(_note, Brushes.DarkOrange)));

        return panel;
    }

    /// <summary>Advances by itself the moment the condition it is waiting for becomes true.</summary>
    async Task CheckAsync()
    {
        if (_busy) return;
        var step = _host.Onboarding.Current();
        var def = Define(step);
        if (def.AutoCheck is null) return;
        try
        {
            if (!await def.AutoCheck()) return;
            _host.Onboarding.Complete(step);
            _host.Gateway.Log.Activity($"Setup: {step.Title()} — done");
            if (_host.Onboarding.IsComplete()) _poll.Stop();
            _rerender();
        }
        catch (Exception) { /* a probe that fails is just "not yet" */ }
    }

    void Done(OnboardingStep s) => _host.Onboarding.Complete(s);

    IAgentRuntime? Runtime()
    {
        var id = _host.Gateway.Settings.SelectedRuntimeId;
        var manifest = id is null ? null : RuntimeCatalog.Find(id);
        return manifest is null ? null : new CliAgentRuntime(manifest);
    }

    bool UsingAtas => (_host.Db.GetKv("connector") ?? "fake") == "atas";

    Step Define(OnboardingStep step) => step switch
    {
        OnboardingStep.WELCOME => new Step(
            "TradeAgent connects an AI assistant to your ATAS trading platform.\n\n" +
            "It will set up the AI, connect to ATAS, and give the AI a safe way to see your account and place orders. " +
            "You will never need to use a command prompt.\n\n" +
            "Nothing can trade with real money until you switch that on yourself, later and deliberately.",
            [("Get started", () => { Done(OnboardingStep.WELCOME); return Task.CompletedTask; })], null, null),

        OnboardingStep.SYSTEM_CHECK => new Step(
            "Checking that this computer has what TradeAgent needs.",
            [("Check again", async () =>
            {
                var report = await _host.RunDoctorAsync();
                var bad = report.Checks.Where(c => c.State == HealthState.FAILED).ToList();
                _note = bad.Count == 0
                    ? "Everything needed is present."
                    : string.Join('\n', bad.Select(b => $"• {b.Name}: {b.UserAction}"));
            })],
            async () =>
            {
                var report = await _host.RunDoctorAsync();
                // Only blockers stop setup. A missing AI tool or ATAS is handled by its own step.
                return !report.Checks.Any(c => c.State == HealthState.FAILED &&
                    c.Name is "Processor" or "Free disk space" or "Folder permissions");
            },
            "Checking..."),

        OnboardingStep.AI_RUNTIME_SELECTED => new Step(
            "Which AI assistant would you like to use? Both run on this computer and talk to an AI service over the internet.\n\n" +
            "You can change this later.",
            RuntimeCatalog.Load().Where(m => m.Id != "custom").Select(m =>
                (m.DisplayName, (Func<Task>)(() =>
                {
                    _host.Gateway.Update(s => s.SelectedRuntimeId = m.Id);
                    Done(OnboardingStep.AI_RUNTIME_SELECTED);
                    return Task.CompletedTask;
                }))).ToArray(),
            null, null),

        OnboardingStep.AI_RUNTIME_INSTALLED => new Step(
            $"{Runtime()?.DisplayName ?? "The AI assistant"} needs to be on this computer.\n\n" +
            "If TradeAgent cannot install it automatically, it will show you exactly where to get it.",
            [
                ("Install it for me", async () =>
                {
                    var rt = Runtime() ?? throw new TradeAgentException(ErrorCode.AI_RUNTIME_NOT_FOUND);
                    await rt.InstallAsync(new Progress<string>(s => _note = s));
                }),
                ("Open the download page", () =>
                {
                    var m = RuntimeCatalog.Find(_host.Gateway.Settings.SelectedRuntimeId ?? "opencode");
                    var url = m?.Install.ManualUrl ?? m?.DocsUrl;
                    if (url is not null) MainWindow.OpenPath(url);
                    return Task.CompletedTask;
                })
            ],
            async () => Runtime() is { } rt && (await rt.DetectAsync()).Installed,
            "Waiting for the program to appear..."),

        OnboardingStep.AI_AUTHENTICATED => new Step(
            $"Sign in to your AI account. {RuntimeCatalog.Find(_host.Gateway.Settings.SelectedRuntimeId ?? "opencode")?.SignInDescription}\n\n" +
            "TradeAgent never sees your password. The AI program handles sign-in itself.",
            [
                ("Sign in", async () => { if (Runtime() is { } rt) await rt.BeginAuthenticationAsync(); }),
                ("I have signed in", () => { Done(OnboardingStep.AI_AUTHENTICATED); return Task.CompletedTask; })
            ],
            async () => Runtime() is { } rt && await rt.GetAuthenticationStateAsync() == AuthState.Authenticated,
            "Waiting for sign-in to finish. This page continues on its own once it does."),

        OnboardingStep.TRADING_PLATFORM_SELECTED => new Step(
            "Where should trading happen?\n\n" +
            "Practice simulator: a built-in fake account. Nothing is real, nothing can be lost. Start here.\n\n" +
            "ATAS: your real trading platform. You will still choose later whether real money is allowed.",
            [
                ("Practice simulator", () => { _host.SwitchConnector("fake"); Done(OnboardingStep.TRADING_PLATFORM_SELECTED); return Task.CompletedTask; }),
                ("ATAS", () => { _host.SwitchConnector("atas"); Done(OnboardingStep.TRADING_PLATFORM_SELECTED); return Task.CompletedTask; })
            ],
            null, null),

        OnboardingStep.ATAS_INSTALLED => new Step(
            UsingAtas
                ? "Looking for ATAS on this computer."
                : "You chose the practice simulator, so ATAS is not needed.",
            UsingAtas
                ? [("Where do I get ATAS?", () => { MainWindow.OpenPath("https://atas.net/"); return Task.CompletedTask; })]
                : [],
            () => Task.FromResult(!UsingAtas || AtasInstallation.Detect().Installed),
            UsingAtas ? "Looking for ATAS..." : null),

        OnboardingStep.ATAS_BRIDGE_INSTALLED => new Step(
            UsingAtas
                ? "TradeAgent needs to place a small add-on inside ATAS so the two can talk."
                : "Not needed for the practice simulator.",
            UsingAtas
                ? [("Install the add-on", () =>
                {
                    var dir = AtasInstallation.InstallBridge(Path.Combine(AppContext.BaseDirectory, "bridge"));
                    _note = $"Installed into {dir}";
                    return Task.CompletedTask;
                })]
                : [],
            () => Task.FromResult(!UsingAtas || AtasInstallation.Detect().BridgeInstalled),
            null),

        // The one step ATAS may genuinely require a human to perform inside its own interface.
        OnboardingStep.ATAS_BRIDGE_CONNECTED => new Step(
            UsingAtas
                ? "One last thing to do inside ATAS:\n\n" +
                  "   1.  Open ATAS\n" +
                  "   2.  Open a chart\n" +
                  "   3.  Open Strategies for that chart\n" +
                  "   4.  Choose TradeAgent Bridge\n" +
                  "   5.  Press Add, then press Start\n\n" +
                  "You do not need to tell TradeAgent when you are done — it will notice by itself."
                : "Not needed for the practice simulator.",
            UsingAtas ? [("Open ATAS", () => { MainWindow.OpenPath("atas://"); return Task.CompletedTask; })] : [],
            async () => !UsingAtas || await _host.Connector.IsConnectedAsync(),
            UsingAtas ? "Waiting for ATAS to connect..." : null),

        OnboardingStep.TRADING_CONNECTION_FOUND => new Step(
            "Checking that the trading platform is logged in and reachable.",
            [], async () => await _host.Connector.GetHealthAsync() == HealthState.READY,
            "Waiting for the trading connection..."),

        OnboardingStep.ACCOUNT_SELECTED => new Step(
            "Which account should the AI be allowed to see and trade?",
            AccountButtons(),
            () => Task.FromResult(_host.Gateway.Settings.SelectedAccountId is not null),
            "Looking for accounts..."),

        OnboardingStep.MARKET_DATA_VERIFIED => new Step(
            "Checking that live prices are arriving.",
            [], async () =>
            {
                var first = (await _host.Gateway.InstrumentsAsync()).FirstOrDefault();
                if (first is null) return false;
                var q = await _host.Gateway.QuoteAsync(first.Symbol);
                return q is not null && !q.IsStale(TimeSpan.FromMinutes(1));
            },
            "Waiting for prices..."),

        OnboardingStep.ORDER_ACCESS_VERIFIED => new Step(
            "Checking that this account is allowed to place orders.\n\n" +
            "This only checks permission. No order is placed.",
            [], async () => (await _host.Gateway.AccountAsync())?.TradingEnabled == true,
            "Checking trading access..."),

        OnboardingStep.WORKSPACE_CREATED => new Step(
            "Creating a folder for the AI to work in, with written instructions about how it must behave " +
            "and what it is not allowed to do.",
            [("Create it", () =>
            {
                WorkspaceBuilder.Build(_host.WorkspaceContext());
                return Task.CompletedTask;
            })],
            () => Task.FromResult(File.Exists(Path.Combine(Paths.Workspace, "AGENTS.md"))),
            null),

        OnboardingStep.AGENT_READY => new Step(
            "Ready to start the AI. It will open in its own window, already able to see the account " +
            "through the trade command.\n\n" +
            "It starts in practice mode. Real money stays switched off until you allow it.",
            [("Start the AI", async () =>
            {
                var manifest = RuntimeCatalog.Find(_host.Gateway.Settings.SelectedRuntimeId ?? "opencode")
                    ?? throw new TradeAgentException(ErrorCode.AI_RUNTIME_NOT_FOUND);
                await _host.Agent.PrepareAsync(manifest, _host.WorkspaceContext());
                await _host.Agent.StartAsync();
                Done(OnboardingStep.AGENT_READY);
            })],
            null, null),

        _ => new Step(
            "Setup is finished. TradeAgent will now show you the main screen.",
            [("Finish", () => { Done(OnboardingStep.SETUP_COMPLETE); return Task.CompletedTask; })], null, null),
    };

    (string, Func<Task>)[] AccountButtons()
    {
        try
        {
            return _host.Gateway.AccountsAsync().GetAwaiter().GetResult()
                .Select(a => ($"{a.Name} ({a.Id}){(a.IsSimulated ? " — simulation" : " — real money")}",
                    (Func<Task>)(() =>
                    {
                        _host.Gateway.Update(s => s.SelectedAccountId = a.Id);
                        Done(OnboardingStep.ACCOUNT_SELECTED);
                        return Task.CompletedTask;
                    })))
                .ToArray();
        }
        catch (Exception) { return []; }
    }
}

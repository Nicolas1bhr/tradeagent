using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TradeAgent.Core;
using TradeAgent.Gateway;

namespace TradeAgent.App;

/// <summary>
/// The whole product's window. Small on purpose: ATAS stays the trading screen and the agent CLI
/// stays the place you talk to the AI. This is the control panel between them.
/// </summary>
public sealed class MainWindow : Window
{
    readonly AppHost _host;
    readonly StackPanel _root = new() { Spacing = 14, Margin = new Thickness(22) };
    OnboardingView? _wizard;

    public MainWindow(AppHost host)
    {
        _host = host;
        Title = "TradeAgent";
        Width = 760;
        Height = 620;
        Content = new ScrollViewer { Content = _root };
    }

    public async Task InitialiseAsync()
    {
        var started = await _host.StartAsync();
        if (!started)
        {
            Render(Fatal(_host.StartupProblem ?? "TradeAgent could not start."));
            return;
        }
        _host.Changed += () => Dispatcher.UIThread.Post(RenderCurrent);
        RenderCurrent();
    }

    void RenderCurrent()
    {
        if (!_host.Onboarding.IsComplete())
        {
            _wizard ??= new OnboardingView(_host, RenderCurrent);
            Render(_wizard.Build());
            return;
        }
        Render(BuildDashboard());
    }

    void Render(Control content)
    {
        _root.Children.Clear();
        _root.Children.Add(content);
    }

    static Control Fatal(string message) => new StackPanel
    {
        Spacing = 10,
        Children =
        {
            Ui.H1("TradeAgent cannot start"),
            Ui.Body(message),
            Ui.Body("If this keeps happening, use Create support package from the Diagnostics screen once TradeAgent does open.")
        }
    };

    // ---------------------------------------------------------------- dashboard

    Control BuildDashboard()
    {
        var gw = _host.Gateway;
        var status = gw.StatusAsync().GetAwaiter().GetResult();
        var panel = new StackPanel { Spacing = 12 };

        panel.Children.Add(Ui.H1("TradeAgent"));

        var grid = new StackPanel { Spacing = 4 };
        foreach (var h in _host.Health.Snapshot()) grid.Children.Add(Ui.StatusRow(h));
        panel.Children.Add(Ui.Card(grid));

        panel.Children.Add(Ui.Card(new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Ui.KeyValue("Trading mode", status.Mode.ToString()),
                Ui.KeyValue("Platform", $"{status.ConnectorName}{(status.ConnectorIsPaper ? " (simulation)" : " (real money)")}"),
                Ui.KeyValue("Account", status.AccountId ?? "not selected"),
                Ui.KeyValue("AI trading", status.AiTradingStopped ? "STOPPED" : status.ExecutionAvailable ? "allowed" : $"paused — {status.ExecutionBlockedReason}"),
                Ui.KeyValue("Open orders / unconfirmed", $"{status.OpenRequests} / {status.UnreconciledRequests}")
            }
        }));

        // Agent controls
        var agentRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        agentRow.Children.Add(Ui.Button(_host.Agent.Running ? "Stop AI" : "Start AI", async () =>
        {
            if (_host.Agent.Running) await _host.Agent.StopAsync();
            else
            {
                var manifest = RuntimeCatalogFor();
                if (manifest is null) return;
                await _host.Agent.PrepareAsync(manifest, _host.WorkspaceContext());
                await _host.Agent.StartAsync();
            }
            RenderCurrent();
        }));
        agentRow.Children.Add(Ui.Button("Open the AI's folder", () => OpenPath(Paths.Workspace)));
        agentRow.Children.Add(Ui.Button("Open ATAS", OpenAtas));
        panel.Children.Add(agentRow);

        // Mode and the real-money switch
        var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var mode in Enum.GetValues<TradingMode>())
        {
            var m = mode;
            modeRow.Children.Add(Ui.Button(Ui.ModeLabel(m), () => { _host.Gateway.SetMode(m); RenderCurrent(); },
                emphasised: status.Mode == m));
        }
        panel.Children.Add(Ui.Card(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Ui.Label("Mode"),
                modeRow,
                Ui.Confirm(
                    status.LiveActivated ? "Switch real-money trading OFF" : "Switch real-money trading ON",
                    status.LiveActivated ? "Confirm: switch real money off" : "Confirm: allow real money",
                    () => { _host.Gateway.ActivateLive(!status.LiveActivated); RenderCurrent(); })
            }
        }));

        // Emergency controls: three separate buttons, three separate effects.
        panel.Children.Add(Ui.Card(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Ui.Label("Emergency"),
                Ui.Big(status.AiTradingStopped ? "ENABLE AI TRADING" : "STOP AI TRADING",
                    status.AiTradingStopped ? Brushes.SeaGreen : Brushes.Firebrick,
                    () =>
                    {
                        if (status.AiTradingStopped) _host.Gateway.EnableAiTrading();
                        else _host.Gateway.StopAiTrading("you pressed STOP AI TRADING");
                        RenderCurrent();
                    }),
                Ui.Body("Stopping the AI removes its permission to trade. It does not touch your orders or positions."),
                Ui.Confirm("Cancel all working orders", "Confirm: cancel all working orders",
                    async () => { await _host.Gateway.OperatorCancelAllAsync(); RenderCurrent(); }),
                Ui.Confirm("Close all positions", "Confirm: close all positions with market orders",
                    async () => { await _host.Gateway.OperatorCloseAllAsync(); RenderCurrent(); })
            }
        }));

        // Diagnostics
        var diagOutput = Ui.Body("");
        panel.Children.Add(Ui.Card(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Ui.Label("Diagnostics"),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children =
                    {
                        Ui.Button("Check everything", async () =>
                        {
                            diagOutput.Text = "Checking...";
                            var report = await _host.RunDoctorAsync();
                            diagOutput.Text = report.AllHealthy
                                ? "Everything looks healthy."
                                : string.Join('\n', report.Problems.Select(p => $"• {p.Name}: {p.Detail}" +
                                    (string.IsNullOrWhiteSpace(p.UserAction) ? "" : $"\n    what to do: {p.UserAction}")));
                        }),
                        Ui.Button("Create support package", () =>
                        {
                            var path = Diagnostics.Doctor.CreateSupportPackage(_host.Db);
                            diagOutput.Text = $"Saved to {path}";
                        })
                    }
                },
                diagOutput
            }
        }));

        // Activity history in plain language
        var history = new StackPanel { Spacing = 2 };
        foreach (var (at, level, text) in _host.Gateway.Log.RecentActivity(12))
            history.Children.Add(Ui.Body($"{at.ToLocalTime():HH:mm}  {text}", level == "warn" ? Brushes.DarkOrange : null));
        panel.Children.Add(Ui.Card(new StackPanel { Spacing = 6, Children = { Ui.Label("Recent activity"), history } }));

        return panel;
    }

    AgentRuntime.RuntimeManifest? RuntimeCatalogFor() =>
        AgentRuntime.RuntimeCatalog.Find(_host.Gateway.Settings.SelectedRuntimeId ?? "opencode");

    void OpenAtas()
    {
        var d = Connectors.Atas.AtasInstallation.Detect();
        if (d.InstallDir is null) return;
        foreach (var exe in new[] { "ATAS.exe", "OFT.Platform.exe" })
        {
            var full = Path.Combine(d.InstallDir, exe);
            if (!File.Exists(full)) continue;
            try { Process.Start(new ProcessStartInfo(full) { UseShellExecute = true }); } catch (Exception) { }
            return;
        }
    }

    internal static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception) { /* nothing useful to tell the user if the shell refuses */ }
    }
}

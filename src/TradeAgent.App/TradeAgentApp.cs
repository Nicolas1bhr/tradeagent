using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace TradeAgent.App;

public sealed class TradeAgentApp : Application
{
    AppHost? _host;

    /// <summary>
    /// Fluent supplies the control templates; <see cref="Tokens"/> supplies every colour, size and
    /// gap on top of them. The variant is pinned to Dark rather than following Windows: this window
    /// lives beside ATAS charts, and a light panel between dark charts is the thing that looks
    /// broken. Following the system here would mean shipping a second palette nobody asked for.
    /// </summary>
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(Theme.Build());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _host = new AppHost();
            var window = new MainWindow(_host);
            desktop.MainWindow = window;
            desktop.ShutdownRequested += async (_, _) =>
            {
                if (_host is not null) await _host.DisposeAsync();
            };
            // Last line of defence. Anything that escapes a handler would otherwise end the process
            // with no window and no message, which for this audience is indistinguishable from the
            // computer having eaten their trading software.
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                e.SetObserved();
                Ui.ReportError?.Invoke(e.Exception.GetBaseException().Message);
            };

            _ = window.InitialiseAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;

namespace TradeAgent.App;

public sealed class TradeAgentApp : Application
{
    AppHost? _host;

    public override void Initialize() => Styles.Add(new FluentTheme());

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
            _ = window.InitialiseAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

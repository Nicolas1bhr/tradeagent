using Avalonia;

namespace TradeAgent.App;

static class Program
{
    [STAThread]
    public static int Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TradeAgentApp>()
            .UsePlatformDetect()
            .LogToTrace();
}

using Avalonia;

namespace TradeAgent.App;

static class Program
{
    /// <summary>
    /// THE APP'S ENTRY. One exception to opening the window: started with the cell web helper's argument
    /// (<see cref="Core.WindowsCellEgress.Verb"/>) — by the app itself, through Windows' permission prompt — it is the one fixed elevated helper and
    /// nothing else: exactly that argument alone places the cells' egress rule and exits; anything beside it is refused. It touches nothing
    /// before this line, so the elevated process creates nothing in the owner's home.
    /// </summary>
    [STAThread]
    public static int Main(string[] args) =>
        Core.WindowsCellEgress.Named(args)
            ? Core.WindowsCellEgress.RunHelper(args)
            : BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TradeAgentApp>()
            .UsePlatformDetect()
            .LogToTrace();
}

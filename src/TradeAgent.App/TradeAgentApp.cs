using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;

namespace TradeAgent.App;

public sealed class TradeAgentApp : Application
{
    AppHost? _host;

    static int _interRegistered;

    /// <summary>The SIGTERM registration, held for the app's life: a registration nobody holds can be collected.</summary>
    PosixSignalRegistration? _stopSignal;

    /// <summary>
    /// Fluent supplies the control templates; <see cref="Tokens"/> supplies every colour, size and
    /// gap on top of them. The variant is pinned to Dark rather than following Windows: this window
    /// lives beside ATAS charts, and a light panel between dark charts is the thing that looks
    /// broken. Following the system here would mean shipping a second palette nobody asked for.
    /// </summary>
    public override void Initialize()
    {
        // THE APP'S TYPE, registered here and not on the builder, so every way this application is started carries it: the window's
        // own start and the headless one the render rows use. Once per process: the font manager is the process's, and a second
        // application object (a test host builds several) must not add the collection twice.
        if (Interlocked.Exchange(ref _interRegistered, 1) == 0)
            Avalonia.Media.FontManager.Current.AddFontCollection(new Avalonia.Fonts.Inter.InterFontCollection());

        // FLUENT'S OWN ACCENT IS THE WINDOW'S LIGHT, not a blue: what Fluent paints by itself — a ticked box, a chosen row of a list,
        // a focus ring — is then the same near-white as every primary press, and indigo is left meaning the AI alone (Theme).
        var fluent = new FluentTheme();
        fluent.Palettes[ThemeVariant.Dark] = new ColorPaletteResources { Accent = Theme.FluentAccent };
        Styles.Add(fluent);
        // A tick on that light is the window's dark, in every state Fluent paints it: a white tick on a near-white box is no tick.
        foreach (var state in new[] { "Checked", "CheckedPointerOver", "CheckedPressed" })
            Resources["CheckBoxCheckGlyphForeground" + state] = Theme.TextOnAccent;
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

            // THE QUIT IS HELD UNTIL THE AI HAS STOPPED (U-agent-tree item 4). Avalonia 12.1.1 raises
            // ShutdownRequested for the OS's quit and for the last window closing, and the request can be
            // cancelled: it is, the AI is stopped and the host disposed, and then the app shuts down for
            // real. The handler used to be an async void that returned at its first await, so the app went
            // on exiting while the dispose ran. Exit is the other half: a forced Shutdown() — the update's —
            // raises no ShutdownRequested at all (read from the 12.1.1 IL: Shutdown passes force), only Exit.
            var host = _host;
            var quit = new Quit(() => host.DisposeAsync().AsTask(), AppHost.QuitBound);
            desktop.ShutdownRequested += (_, e) =>
            {
                if (quit.Hold(() => Dispatcher.UIThread.Post(() => desktop.Shutdown()))) e.Cancel = true;
            };
            desktop.Exit += (_, _) => quit.Finish();
            // AND THE SYSTEM'S STOP IS THE SAME QUIT (U-linux-host item 2). systemd stops the app with SIGTERM, and
            // the runtime's default for it is to exit — past the held quit above. Off Windows only: a Windows app
            // is ended through its window and the OS's quit, which ShutdownRequested already holds.
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    _stopSignal = PosixSignalRegistration.Create(PosixSignal.SIGTERM,
                        context => quit.Signal(context, () => Dispatcher.UIThread.Post(() => desktop.Shutdown())));
                }
                catch (PlatformNotSupportedException) { /* a platform with no such signal has nobody sending it */ }
            }
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

/// <summary>
/// THE QUIT, HELD UNTIL ITS STOP HAS RUN, AND BOUNDED (<c>U-agent-tree</c> item 4). Kept apart from the
/// lifetime so the rule can be driven without a window: a request to quit is held — cancelled — while the
/// stop runs, and <c>then</c> is called once it has finished or the bound has passed, whichever is first;
/// a second request while the first is being honoured is held too, and starts nothing. <see cref="Finish"/>
/// is the way out that cannot be held — the lifetime's Exit — and it runs the same stop, once, bounded,
/// on a thread of its own, so the thread raising Exit is never what the stop is waiting for.
/// </summary>
internal sealed class Quit(Func<Task> stop, TimeSpan bound)
{
    readonly Lock _gate = new();
    Task? _stopping;
    bool _released;

    Task Stopping()
    {
        lock (_gate) return _stopping ??= Task.Run(stop);
    }

    /// <summary>Whether to cancel this request to quit: true until the stop has run and the quit was let go.</summary>
    public bool Hold(Action then)
    {
        Task stopping;
        lock (_gate)
        {
            if (_released) return false;
            if (_stopping is not null) return true;
            stopping = _stopping = Task.Run(stop);
        }

        _ = Task.WhenAny(stopping, Task.Delay(bound)).ContinueWith(_ =>
        {
            lock (_gate) _released = true;
            then();
        }, TaskScheduler.Default);
        return true;
    }

    /// <summary>
    /// THE SYSTEM'S REQUEST TO STOP — SIGTERM, which is how systemd stops a unit (<c>U-linux-host</c> item 2). Its
    /// default ending is the runtime's own exit, and that skipped this quit: the AI's tree, the ledgers' close and
    /// the stop line all went with it. So the default is cancelled, every time, and the quit is held exactly as
    /// the OS's quit is: the stop run once, bounded, then <paramref name="then"/>. A second signal while the first
    /// is being honoured is cancelled too, and starts nothing.
    /// </summary>
    public void Signal(PosixSignalContext context, Action then)
    {
        context.Cancel = true;
        Hold(then);
    }

    /// <summary>The way out that cannot be held: runs the stop if nothing has, and waits for it, bounded.</summary>
    public void Finish()
    {
        try { Stopping().Wait(bound); }
        catch (Exception) { /* the stop says why where it can; the quit goes on */ }
    }
}

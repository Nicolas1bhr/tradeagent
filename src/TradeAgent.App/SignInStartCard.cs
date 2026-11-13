using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Layout;
using TradeAgent.Core;

namespace TradeAgent.App;

/// <summary>
/// WHETHER WINDOWS OPENS TRADEAGENT WHEN THE OWNER SIGNS IN (<c>U-windows-awake</c> item 2), on the owner's page. Off by default. Turning it on is
/// TWO presses, because it changes what runs at his logon; turning it off is one, because it only takes a start away. Built once and rewritten
/// on the tick, so a half-pressed confirm survives it; hidden where there is no Windows sign-in to start at.
/// </summary>
internal sealed class SignInStartCard
{
    readonly AppHost _host;
    readonly TextBlock _state = Ui.With(Ui.Body(""), t => t.TextWrapping = Avalonia.Media.TextWrapping.Wrap);
    readonly TextBlock _note = Ui.Micro("");
    readonly Button _press;
    readonly Button _startupApps;

    public Control Root { get; }

    /// <summary>The fold that holds this card on the owner's page, hidden with it.</summary>
    public Control? Fold
    {
        get => _fold;
        set { _fold = value; Update(); }
    }
    Control? _fold;

    public SignInStartCard(AppHost host)
    {
        _host = host;
        _press = Ui.ConfirmIf(AppHost.StartAtSignInLabel, ArmedSentence, Press);
        // A FIXED WINDOWS PAGE, never data: the one place the owner switches an entry of Windows' own startup list back on.
        _startupApps = Ui.Ghost(AppHost.OpenStartupApps, () =>
        {
            try { Process.Start(new ProcessStartInfo(AppHost.StartupAppsPage) { UseShellExecute = true }); }
            catch (Exception ex) { Ui.ReportError?.Invoke($"Windows could not open its startup apps: {ex.Message}"); }
        });
        _note.IsVisible = false;
        Root = Ui.Section("Opening when you sign in", Ui.Col(Theme.S3,
            _state,
            Ui.Muted(AppHost.SignInNeedsSignIn),
            Ui.With(Ui.Row(Theme.S2, _press, _startupApps), r => r.HorizontalAlignment = HorizontalAlignment.Left),
            _note));
        Update();
    }

    /// <summary>Asked at the press: two presses only in the direction that adds a start at his logon.</summary>
    string? ArmedSentence() => _host.SignInStartNow() switch
    {
        SignInReading.Off => AppHost.StartAtSignInArmed,
        SignInReading.AnotherCopy => AppHost.StartThisCopyArmed,
        _ => null
    };

    void Press()
    {
        var reading = _host.SignInStartNow();
        _note.Text = reading is SignInReading.Off or SignInReading.AnotherCopy ? _host.StartAtSignIn() : _host.StopStartingAtSignIn();
        _note.IsVisible = _note.Text.Length > 0;
        Update();
    }

    /// <summary>The five-second pass: reads Windows' value and writes the words in place. It never presses.</summary>
    public void Update()
    {
        var reading = _host.SignInStartNow();
        var shown = reading != SignInReading.NotThisMachine;
        // THE FOLD, NOT THE CARD, once there is one: the card is the fold's body, and its visibility is the fold's open or closed.
        if (_fold is not null) _fold.IsVisible = shown;
        else Root.IsVisible = shown;
        if (!shown) return;
        _state.Text = AppHost.SignInWords(reading);
        _state.Foreground = reading is SignInReading.OnButOffInWindows or SignInReading.AnotherCopy ? Theme.Caution : Theme.Text;
        Ui.SetResting(_press, reading switch
        {
            SignInReading.Off => AppHost.StartAtSignInLabel,
            SignInReading.AnotherCopy => AppHost.StartThisCopyLabel,
            _ => AppHost.StopAtSignInLabel
        }, "secondary");
        _startupApps.IsVisible = reading == SignInReading.OnButOffInWindows;
    }
}

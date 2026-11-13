namespace TradeAgent.App;

/// <summary>What the Dashboard says about sleep (<see cref="AppHost.AwakeNow"/>).</summary>
internal enum AwakeReading
{
    /// <summary>Not Windows: nothing is asked and nothing is said.</summary>
    NotThisMachine,
    /// <summary>The owner's setting is on and the AI is not working on its own: nothing is held yet.</summary>
    WhenItWorks,
    /// <summary>Windows holds the request: the PC does not idle to sleep.</summary>
    Holding,
    /// <summary>The AI works on its own, the setting is on, and Windows did not take the request.</summary>
    WindowsRefused,
    /// <summary>The owner let the PC sleep.</summary>
    OwnerLetsItSleep
}

/// <summary>What the owner's page says about starting at sign-in (<see cref="AppHost.SignInStartNow"/>).</summary>
internal enum SignInReading
{
    NotThisMachine,
    Off,
    On,
    /// <summary>The value is there and Windows' own startup list has it switched off: Windows will not open it.</summary>
    OnButOffInWindows,
    /// <summary>The value names another program than this one: that copy opens, not this one.</summary>
    AnotherCopy
}

/// <summary>
/// A LAPTOP THAT RUNS UNATTENDED (<c>U-windows-awake</c>; r31 D3 F5, MVP-W). Two things only the owner decides and nothing else may change at
/// his logon: whether TradeAgent keeps the PC awake while the AI works on its own, and whether Windows opens TradeAgent when he signs in. No
/// power plan, no system-wide setting, no administrator, no service: a power request this process holds and gives back, and this account's
/// own Run value under TradeAgent's name.
/// </summary>
public sealed partial class AppHost
{
    /// <summary>The words Windows shows beside the request in its own list of what keeps a PC awake.</summary>
    internal const string AwakeReason = "TradeAgent: the AI is working on its own";

    /// <summary>The platform behind the request. In-process only; a test stands a double in.</summary>
    internal IKeepAwake KeepAwake { get; set; } = KeepAwakes.ForThisMachine();

    /// <summary>The platform behind the sign-in start. In-process only; a test stands a double in.</summary>
    internal ISignInStart SignInStart { get; set; } = SignInStarts.ForThisMachine();

    /// <summary>This program's own path, the one command a sign-in start may run.</summary>
    internal Func<string?> ThisProgram { get; set; } = static () => Environment.ProcessPath;

    readonly object _awakeGate = new();
    bool _awakeHeld;
    bool _awakeRefused;

    /// <summary>
    /// WHEN THE PC IS KEPT AWAKE: the owner's setting on, the owner's word that the AI works on its own, the loop running, and the app not
    /// closing. The loop running alone is not enough (a pause still unwinding a turn reads Running with the word already off), and the word
    /// alone is not enough (a restart that did not resume it leaves nothing to stay awake for).
    /// </summary>
    internal bool AwakeWanted()
    {
        if (Stopped) return false;
        try
        {
            return Gateway?.Settings is { KeepAwakeWhileAiWorks: true, AiWorksOnItsOwn: true } && Mission is { Running: true };
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// THE REQUEST MADE TO MATCH <see cref="AwakeWanted"/>, on every press that changes it and on every background pass, so a loop that stops by
    /// any road gives the request back within one pass. Under one gate, so the quit's last pass cannot be overtaken by a pass that read the app
    /// still open. Only a change is written to the activity log.
    /// </summary>
    internal void AwakePass()
    {
        lock (_awakeGate)
        {
            var want = AwakeWanted();
            if (want && !_awakeHeld)
            {
                bool held;
                try { held = KeepAwake.Hold(AwakeReason); }
                catch (Exception) { held = false; }
                _awakeHeld = held;
                if (held)
                {
                    _awakeRefused = false;
                    Gateway?.Log.TryActivity("TradeAgent is keeping this PC awake while the AI works on its own");
                }
                else if (KeepAwake.Supported && !_awakeRefused)
                {
                    _awakeRefused = true;
                    Gateway?.Log.TryActivity(AwakeWords(AwakeReading.WindowsRefused), "warn");
                }
            }
            else if (!want)
            {
                _awakeRefused = false;
                if (!_awakeHeld) return;
                try { KeepAwake.Release(); }
                catch (Exception) { /* the process ending gives it back in any case */ }
                _awakeHeld = false;
                Gateway?.Log.TryActivity("TradeAgent no longer keeps this PC awake");
            }
        }
    }

    internal AwakeReading AwakeNow()
    {
        if (!KeepAwake.Supported) return AwakeReading.NotThisMachine;
        bool on;
        try { on = Gateway.Settings.KeepAwakeWhileAiWorks; }
        catch (Exception) { on = true; }
        if (!on) return AwakeReading.OwnerLetsItSleep;
        lock (_awakeGate)
        {
            if (_awakeHeld) return AwakeReading.Holding;
            return _awakeRefused ? AwakeReading.WindowsRefused : AwakeReading.WhenItWorks;
        }
    }

    /// <summary>The Dashboard's sentence for each reading. Plain words: what holds, and what Windows still does on its own.</summary>
    internal static string AwakeWords(AwakeReading reading) => reading switch
    {
        AwakeReading.Holding => "TradeAgent is keeping this PC awake while the AI works; the screen can still turn off. Closing the lid, or "
                                + "running on battery, can still put it to sleep, so keep it plugged in with the lid open.",
        AwakeReading.WhenItWorks => "While the AI works on its own, TradeAgent keeps this PC awake. Closing the lid, or running on battery, "
                                    + "can still put it to sleep.",
        AwakeReading.WindowsRefused => "Windows did not let TradeAgent keep this PC awake, so it can go to sleep while the AI works, and the AI "
                                       + "stops until it wakes.",
        AwakeReading.OwnerLetsItSleep => "You let this PC sleep while the AI works. If it sleeps, the AI stops until it wakes.",
        _ => ""
    };

    /// <summary>The Dashboard's one press, either way: it moves no money and grants nothing.</summary>
    internal const string LetItSleep = "Let this PC sleep";
    internal const string KeepItAwake = "Keep this PC awake while the AI works";

    /// <summary>THE OWNER'S PRESS. One press each way; the request follows at once.</summary>
    internal void SetKeepAwake(bool on)
    {
        Gateway.Update(s => s.KeepAwakeWhileAiWorks = on);
        Gateway.Log.TryActivity(on
            ? "You set TradeAgent to keep this PC awake while the AI works"
            : "You let this PC sleep while the AI works");
        AwakePass();
        Changed?.Invoke();
    }

    // ---- starting when the owner signs in to Windows ---------------------------------------------------------------------------------

    /// <summary>This program, when it is an installed TradeAgent; null when it is anything else (a test host, a run from source).</summary>
    string? InstalledProgram()
    {
        var exe = ThisProgram();
        if (exe is not { Length: > 0 }) return null;
        return string.Equals(Path.GetFileName(exe), "TradeAgent.exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFullPath(exe) : null;
    }

    internal SignInReading SignInStartNow()
    {
        if (!SignInStart.Supported) return SignInReading.NotThisMachine;
        string? command;
        bool offInWindows;
        try
        {
            command = SignInStart.Command();
            offInWindows = command is not null && SignInStart.TurnedOffInWindows();
        }
        catch (Exception) { return SignInReading.Off; }
        if (command is not { Length: > 0 }) return SignInReading.Off;
        var mine = InstalledProgram();
        string program;
        try { program = Path.GetFullPath(SignInStarts.ProgramOf(command)); }
        catch (Exception) { return SignInReading.AnotherCopy; }
        if (mine is null || !string.Equals(program, mine, StringComparison.OrdinalIgnoreCase)) return SignInReading.AnotherCopy;
        return offInWindows ? SignInReading.OnButOffInWindows : SignInReading.On;
    }

    internal static string SignInWords(SignInReading reading) => reading switch
    {
        SignInReading.Off => "TradeAgent does not open when you sign in to Windows. After a restart, the AI does nothing until you open it.",
        SignInReading.On => "TradeAgent opens when you sign in to Windows, as it was when it closed: if you had paused the AI, it stays paused.",
        SignInReading.OnButOffInWindows => "TradeAgent is set to open when you sign in, but it is switched off in Windows' own list of startup "
                                           + "apps, so Windows will not open it. Switch it back on there.",
        SignInReading.AnotherCopy => "Windows opens another copy of TradeAgent when you sign in, not this one.",
        _ => ""
    };

    /// <summary>What a restart still needs from the owner, said beside the press in every state.</summary>
    internal const string SignInNeedsSignIn =
        "Windows has to be signed in for TradeAgent to open: after a restart, it opens when you sign in again.";

    internal const string StartAtSignInLabel = "Open TradeAgent when I sign in to Windows";
    internal const string StartAtSignInArmed = "Confirm: open TradeAgent each time you sign in to Windows";
    internal const string StartThisCopyLabel = "Open this copy when I sign in";
    internal const string StartThisCopyArmed = "Confirm: open this copy of TradeAgent each time you sign in to Windows";
    internal const string StopAtSignInLabel = "Stop opening TradeAgent when I sign in";
    internal const string OpenStartupApps = "Open Windows' startup apps";
    internal const string StartupAppsPage = "ms-settings:startupapps";

    /// <summary>
    /// THE SECOND PRESS: Windows runs this program when this account signs in. Two presses, because it changes what runs at his logon. It
    /// writes one value, this account's, under TradeAgent's name, and the command is this program alone: the start it makes is the ordinary
    /// start, which keeps whatever the owner had paused paused.
    /// </summary>
    /// <returns>The owner's words for what was done or why nothing was.</returns>
    internal string StartAtSignIn()
    {
        if (!SignInStart.Supported) return "Opening TradeAgent when you sign in is a Windows setting.";
        if (InstalledProgram() is not { } exe) return "Only an installed TradeAgent can be opened when you sign in to Windows.";
        SignInStart.Register(SignInStarts.CommandFor(exe));
        Gateway.Log.TryActivity("You set TradeAgent to open when you sign in to Windows");
        Changed?.Invoke();
        return SignInWords(SignInStartNow());
    }

    /// <summary>ONE PRESS: it only takes a start away. Removes TradeAgent's value and nothing else.</summary>
    internal string StopStartingAtSignIn()
    {
        if (!SignInStart.Supported) return "";
        SignInStart.Remove();
        Gateway.Log.TryActivity("You stopped TradeAgent opening when you sign in to Windows");
        Changed?.Invoke();
        return SignInWords(SignInStartNow());
    }
}

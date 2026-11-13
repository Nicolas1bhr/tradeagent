using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace TradeAgent.App;

/// <summary>
/// THE APP'S ONE ASK OF WINDOWS TO STAY AWAKE (<c>U-windows-awake</c> item 1; r31 D3 F5). An organisation that runs on a laptop stops when the
/// laptop sleeps, and a consumer laptop's idle sleep is minutes. While the AI works on its own the app holds a system-required power request:
/// the machine does not idle to sleep, the display may still turn off, and nothing about the power plan or any other setting of Windows changes.
///
/// <para>Behind this seam so the rule of WHEN it is held is driven without Windows (<see cref="AppHost.AwakePass"/>); the Windows call is
/// <see cref="WindowsKeepAwake"/>, and off Windows nothing is asked of anything (<see cref="KeepAwakes.None"/>).</para>
/// </summary>
internal interface IKeepAwake
{
    /// <summary>Whether this machine has anything to ask. False off Windows: the dashboard says nothing about sleep there.</summary>
    bool Supported { get; }

    /// <summary>Asks for the machine to stay awake. True when Windows holds the request; false when it refused or there is nothing to ask.</summary>
    bool Hold(string reason);

    /// <summary>Gives the request back. Safe when nothing is held.</summary>
    void Release();
}

internal static class KeepAwakes
{
    /// <summary>This machine's: Windows' power request on Windows, nothing anywhere else.</summary>
    public static IKeepAwake ForThisMachine() => OperatingSystem.IsWindows() ? new WindowsKeepAwake() : None;

    /// <summary>Nothing to ask, and nothing said: a Linux server and the dev Mac.</summary>
    public static IKeepAwake None { get; } = new NoKeepAwake();

    sealed class NoKeepAwake : IKeepAwake
    {
        public bool Supported => false;
        public bool Hold(string reason) => false;
        public void Release() { }
    }
}

/// <summary>
/// WINDOWS' POWER REQUEST, NOT <c>SetThreadExecutionState</c>. The thread call's state belongs to the calling thread and ends when that thread
/// does, and the passes that decide it here run on pool threads that come and go; a request is a handle the process owns until it is cleared
/// or the process ends, whichever thread asks. It is also the one Windows names, with its reason, in its own list of what keeps a PC awake.
/// <c>PowerRequestSystemRequired</c> only: the system stays up, the display is not held. Windows still sleeps on a closed lid or the power
/// button, and may still sleep a PC in modern standby on battery: the dashboard says so.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsKeepAwake : IKeepAwake
{
    readonly object _gate = new();
    IntPtr _request = IntPtr.Zero;

    public bool Supported => true;

    public bool Hold(string reason)
    {
        lock (_gate)
        {
            if (_request != IntPtr.Zero) return true;
            var context = new ReasonContext { Version = PowerRequestContextVersion, Flags = PowerRequestContextSimpleString, SimpleReasonString = reason };
            var request = PowerCreateRequest(ref context);
            if (request == IntPtr.Zero || request == InvalidHandle) return false;
            if (!PowerSetRequest(request, PowerRequestSystemRequired))
            {
                CloseHandle(request);
                return false;
            }
            _request = request;
            return true;
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            if (_request == IntPtr.Zero) return;
            PowerClearRequest(_request, PowerRequestSystemRequired);
            CloseHandle(_request);
            _request = IntPtr.Zero;
        }
    }

    const uint PowerRequestContextVersion = 0;
    const uint PowerRequestContextSimpleString = 0x1;
    const int PowerRequestSystemRequired = 1;
    static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>REASON_CONTEXT with its union at its native size: the simple string, then the detailed form's remaining two pointer-sized words.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string SimpleReasonString;
        public IntPtr Reserved1;
        public IntPtr Reserved2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PowerSetRequest(IntPtr request, int type);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PowerClearRequest(IntPtr request, int type);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// WHAT WINDOWS RUNS WHEN THIS ACCOUNT SIGNS IN, FOR TRADEAGENT ALONE (<c>U-windows-awake</c> item 2): this account's own Run value, no
/// administrator, no service, no scheduled task. The value itself is the record: the app keeps no copy of it, so the owner's page can never
/// say "on" over a Windows that will not open it, or the other way round.
/// </summary>
internal interface ISignInStart
{
    /// <summary>Whether this machine has a sign-in to start at. False off Windows.</summary>
    bool Supported { get; }

    /// <summary>The command Windows runs at sign-in under TradeAgent's name, or null when there is none.</summary>
    string? Command();

    /// <summary>
    /// Whether Windows' own list of startup apps has TradeAgent's entry switched off (Task Manager, or Settings, Apps, Startup). The app never
    /// changes that switch: it is the owner's, made in Windows.
    /// </summary>
    bool TurnedOffInWindows();

    void Register(string command);

    /// <summary>Removes TradeAgent's value and nothing else. Safe when there is none.</summary>
    void Remove();
}

internal static class SignInStarts
{
    /// <summary>The value's name, in this account's Run key and in the uninstaller's removal (packaging/TradeAgent.iss).</summary>
    public const string ValueName = "TradeAgent";

    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Where Windows records an entry switched off in its startup list, under the same name.</summary>
    public const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public static ISignInStart ForThisMachine() =>
        OperatingSystem.IsWindows() ? new WindowsRunValue(RunKey, ApprovedKey, ValueName) : None;

    public static ISignInStart None { get; } = new NoSignInStart();

    /// <summary>
    /// THE ONE COMMAND: this program's own path, quoted, and nothing after it. No argument, so a start at sign-in is the ordinary start —
    /// the window opens as it always does, and whatever the owner had paused stays paused, because the start decides that and only that.
    /// </summary>
    public static string CommandFor(string exe) => $"\"{exe}\"";

    /// <summary>The program a command runs: the quoted path, or the whole command when it is not quoted.</summary>
    public static string ProgramOf(string command)
    {
        var c = command.Trim();
        if (c.StartsWith('"'))
        {
            var close = c.IndexOf('"', 1);
            return close > 0 ? c[1..close] : c[1..];
        }
        return c;
    }

    sealed class NoSignInStart : ISignInStart
    {
        public bool Supported => false;
        public string? Command() => null;
        public bool TurnedOffInWindows() => false;
        public void Register(string command) => throw new PlatformNotSupportedException("Starting at sign-in is a Windows setting.");
        public void Remove() { }
    }
}

/// <summary>
/// A per-user Run value. The keys are parameters so the Windows rows of the tests prove the registry calls on a key of their own, never on
/// the account's real Run key.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsRunValue(string runKey, string approvedKey, string name) : ISignInStart
{
    public bool Supported => true;

    public string? Command()
    {
        using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: false);
        return key?.GetValue(name) as string;
    }

    /// <summary>
    /// Windows' record of the switch: 12 bytes, the first of them even when the entry is on and odd when it is off. No record is on.
    /// </summary>
    public bool TurnedOffInWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(approvedKey, writable: false);
        return key?.GetValue(name) is byte[] { Length: > 0 } state && (state[0] & 1) == 1;
    }

    public void Register(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(runKey, writable: true);
        key.SetValue(name, command, RegistryValueKind.String);
    }

    public void Remove()
    {
        using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

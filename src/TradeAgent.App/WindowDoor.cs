using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia.Controls;

namespace TradeAgent.App;

/// <summary>
/// THE WINDOW'S OWN DOOR, ON A LINUX HOST (<c>U-window-door</c> item 4; R31 D10 F5). On the owner's server the app's window is reached
/// with a VNC viewer — on his Mac, Screen Sharing — and a password he chose (tools/linux-host.sh asks him for it in a dialog). The kit
/// tells the app where that door is, in four Environment= lines of tradeagent.service; without all four (Windows, macOS, a Linux
/// without the kit) there is no door to describe and nothing is shown.
///
/// <para><b>Changing the password is the owner's press, and nothing else's.</b> Only <see cref="WindowDoorCard"/> calls
/// <see cref="ChangePassword"/>, behind two presses; no pipe op, verb, door call or setting reaches it. The password goes to TigerVNC's
/// own tool on its stdin (never an argument), its obfuscated answer is written beside the file (0600) and renamed over it, so the
/// file is always whole: the old password until the rename, the new one after. Xvnc reads the file at each login (CI's linux-host
/// job logs in to show it), so nothing restarts: the next connection uses the new password and anyone connected stays connected.
/// The password is never kept, shown, logged or put in the activity line.</para>
/// </summary>
sealed record WindowDoor(string Address, int Port, string PasswordFile, string PasswordTool)
{
    public const string AddressVariable = "TA_WINDOW_ADDRESS";
    public const string PortVariable = "TA_WINDOW_PORT";
    public const string FileVariable = "TA_WINDOW_PASSWORD_FILE";
    public const string ToolVariable = "TA_WINDOW_PASSWORD_TOOL";

    /// <summary>The door the kit rendered into the unit, or null: Linux only, all four lines, each a value it can be.</summary>
    public static WindowDoor? From(Func<string, string?> env, bool linux)
    {
        if (!linux) return null;
        var (address, port, file, tool) = (env(AddressVariable), env(PortVariable), env(FileVariable), env(ToolVariable));
        if (string.IsNullOrWhiteSpace(address) || !int.TryParse(port, out var p) || p is < 1 or > 65535) return null;
        if (file is null || !Path.IsPathFullyQualified(file) || tool is null || !Path.IsPathFullyQualified(tool)) return null;
        return new WindowDoor(address.Trim(), p, file, tool);
    }

    /// <summary>The door of this process, as the kit's unit started it.</summary>
    public static WindowDoor? Current() => From(Environment.GetEnvironmentVariable, OperatingSystem.IsLinux());

    /// <summary>Whether the window listens on this server's own loopback: then only an administrator's connection reaches it.</summary>
    public bool Loopback => Address.StartsWith("127.", StringComparison.Ordinal) || Address == "::1";

    static readonly Regex Shape = new(@"^[\x21-\x7E]{6,8}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// A new password for the window, typed twice by the owner: refused in words — the old password still working — or written. Never
    /// holds on to either string, never says what it was given.
    /// </summary>
    public (bool Done, string Words) ChangePassword(string first, string second)
    {
        const string Unchanged = " The old password still works.";
        if (!string.Equals(first, second, StringComparison.Ordinal))
            return (false, "The two passwords are not the same." + Unchanged);
        if (!Shape.IsMatch(first))
            return (false, "The password must be 6 to 8 letters, digits or symbols, with no spaces (VNC reads eight)." + Unchanged);
        if (!File.Exists(PasswordTool))
            return (false, $"TigerVNC's password tool is not at {PasswordTool}, where TradeAgent was installed to find it." + Unchanged);

        byte[] obfuscated;
        try
        {
            var psi = new ProcessStartInfo(PasswordTool)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-f");
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("the password tool did not start");
            var err = p.StandardError.ReadToEndAsync();
            using var output = new MemoryStream();
            var copy = p.StandardOutput.BaseStream.CopyToAsync(output);
            // THE PASSWORD ON STDIN ONLY: never an argument (a process's arguments are readable by every account on the host).
            p.StandardInput.Write(first + "\n");
            p.StandardInput.Close();
            if (!p.WaitForExit(15_000))
            {
                try { p.Kill(true); } catch (InvalidOperationException) { }
                return (false, "TigerVNC's password tool did not answer within 15 seconds." + Unchanged);
            }
            copy.Wait(5_000);
            _ = err.Wait(5_000);
            obfuscated = output.ToArray();
            if (p.ExitCode != 0 || obfuscated.Length < 8)
                return (false, $"TigerVNC's password tool refused it (exit {p.ExitCode})." + Unchanged);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (false, $"TigerVNC's password tool could not be run: {ex.Message}." + Unchanged);
        }

        var dir = Path.GetDirectoryName(PasswordFile)!;
        var next = Path.Combine(dir, $"{Path.GetFileName(PasswordFile)}.new.{Environment.ProcessId}");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var f = new FileStream(next, options))
            {
                f.Write(obfuscated);
                f.Flush(true);
            }
            // THE FILE IS ALWAYS WHOLE: a rename over the old one, never a write into it.
            File.Move(next, PasswordFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(next); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return (false, $"TradeAgent could not write {PasswordFile}: {ex.Message}." + Unchanged);
        }
        return (true, "Changed. Your next connection uses the new password; anyone connected now stays connected.");
    }
}

/// <summary>
/// "Reaching this window" on Settings (<c>U-window-door</c> item 4): on a Linux host whose kit told the app its door, where the owner's
/// devices reach the window, that it has the password he chose (never shown), and the two-press change. Absent everywhere else.
/// Built once; the fields are emptied after every press, whatever it answered.
/// </summary>
sealed class WindowDoorCard
{
    public const string Change = "Change this window's password";
    public const string ChangeArmed = "Confirm: change this window's password";

    readonly WindowDoor _door;
    readonly TextBox _first = Field("New password");
    readonly TextBox _second = Field("The same again");
    readonly TextBlock _note = Ui.Muted("");

    public WindowDoorCard(WindowDoor door)
    {
        _door = door;
        _note.IsVisible = false;
        _note.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        ChangeButton = Ui.Confirm(Change, ChangeArmed, Press);
        var where = door.Loopback
            ? $"This window listens on {door.Address}, port {door.Port}: this server's own loopback. Only an administrator's connection " +
              "to the server reaches it; your own devices cannot open it directly."
            : $"Your devices reach this window with a VNC viewer — on a Mac, Screen Sharing — at {door.Address}, port {door.Port}, and the " +
              "password you chose.";
        Root = Ui.Section("Reaching this window", Ui.Col(Theme.S4,
            Ui.Body(where),
            Ui.Muted("It has the password you chose. TradeAgent never shows it, never logs it and never puts it in the activity line."),
            Ui.Col(Theme.S2, Ui.Eyebrow("A new password, typed twice"), _first, _second),
            Ui.Wrap(Theme.S2, ChangeButton),
            _note,
            Ui.Micro("6 to 8 letters, digits or symbols, with no spaces. Nothing restarts: your next connection uses the new password, and anyone " +
                     "connected now stays connected.")));
    }

    public Control Root { get; }

    /// <summary>The two-press change itself, so a test presses what the owner presses.</summary>
    public Button ChangeButton { get; }

    /// <summary>The two masked fields, for a test to type into as the owner types.</summary>
    public (TextBox First, TextBox Second) Fields => (_first, _second);

    /// <summary>What the last press answered, or null before the first.</summary>
    public string? Said => _note.IsVisible ? _note.Text : null;

    static TextBox Field(string placeholder) => new()
    {
        PasswordChar = '•',
        MaxLength = 8,
        PlaceholderText = placeholder,
        Width = 220,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
    };

    void Press()
    {
        var (done, words) = _door.ChangePassword(_first.Text ?? "", _second.Text ?? "");
        _first.Text = "";
        _second.Text = "";
        _note.Text = words;
        _note.Foreground = done ? Theme.TextMuted : Theme.Caution;
        _note.IsVisible = true;
    }
}

using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using TradeAgent.App;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE WINDOW KNOWS ITS OWN DOOR (<c>U-window-door</c> item 4; R31 D10 F5, F8). On the owner's Linux server the app's window is reached
/// with a VNC viewer and the password he chose, and before this nothing in the app knew it: it could not say where it is reached, or
/// let him change the password. Credentials and operator authority: red first (none of these types existed at aa126ada), one mutant
/// per guard watched red (BUILD-STATUS's landing entry quotes them).
///
/// <para>The password tool is a copy of the test puppet named fake-vncpasswd-*, which keeps what it read on stdin and its arguments
/// beside itself and answers eight bytes, as <c>tigervncpasswd -f</c> answers its obfuscated form. Chosen by its name, so no other
/// test's puppet can turn into it.</para>
/// </summary>
public class WindowDoorTests : IDisposable
{
    readonly string _dir = Path.Combine(TestEnv.Home, "window-door-" + Guid.NewGuid().ToString("n")[..8]);
    readonly List<string> _copies = [];

    public WindowDoorTests() => Directory.CreateDirectory(Path.Combine(_dir, "acct", ".vnc"));

    public void Dispose()
    {
        foreach (var c in _copies)
            foreach (var f in new[] { c, c + ".stdin", c + ".argv" })
                try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    string PasswordFile => Path.Combine(_dir, "acct", ".vnc", "vncauth");

    /// <summary>The puppet's own launcher, copied under a fake-vncpasswd name beside it (it finds its program by its folder).</summary>
    string FakeTool(bool fails = false)
    {
        var dll = Path.GetFullPath(typeof(WindowDoorTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "PuppetDll").Value!);
        var host = Path.ChangeExtension(dll, OperatingSystem.IsWindows() ? ".exe" : null)!.TrimEnd('.');
        Assert.True(File.Exists(host), $"no puppet launcher at {host}");
        var copy = Path.Combine(Path.GetDirectoryName(host)!, $"fake-vncpasswd-{(fails ? "fails-" : "")}{Guid.NewGuid():n}"[..40] +
                                                              (OperatingSystem.IsWindows() ? ".exe" : ""));
        File.Copy(host, copy);
        _copies.Add(copy);
        return copy;
    }

    WindowDoor Door(string? tool = null, string address = "198.51.100.9") =>
        new(address, 5901, PasswordFile, tool ?? FakeTool());

    static string? Env(Dictionary<string, string> d, string name) => d.TryGetValue(name, out var v) ? v : null;

    [Fact]
    public void The_door_is_the_kits_four_lines_on_Linux_and_nothing_anywhere_else()
    {
        var lines = new Dictionary<string, string>
        {
            [WindowDoor.AddressVariable] = "198.51.100.9", [WindowDoor.PortVariable] = "5901",
            [WindowDoor.FileVariable] = "/var/lib/tradeagent/.vnc/vncauth", [WindowDoor.ToolVariable] = "/usr/bin/tigervncpasswd"
        };
        Assert.Equal(new WindowDoor("198.51.100.9", 5901, "/var/lib/tradeagent/.vnc/vncauth", "/usr/bin/tigervncpasswd"),
            WindowDoor.From(n => Env(lines, n), linux: true));
        Assert.Null(WindowDoor.From(n => Env(lines, n), linux: false));
        foreach (var missing in lines.Keys)
            Assert.Null(WindowDoor.From(n => n == missing ? null : Env(lines, n), linux: true));
        Assert.Null(WindowDoor.From(n => n == WindowDoor.ToolVariable ? "tigervncpasswd" : Env(lines, n), linux: true));
        // THIS PROCESS was not started by the kit's unit, on any platform CI runs: Settings shows no door.
        Assert.Null(WindowDoor.Current());
    }

    [Fact]
    public void A_new_password_goes_to_the_tool_on_stdin_only_and_replaces_the_file_whole()
    {
        File.WriteAllBytes(PasswordFile, [1, 2, 3, 4, 5, 6, 7, 8]);
        var tool = FakeTool();
        var (done, words) = Door(tool).ChangePassword("Ab3$xY9z", "Ab3$xY9z");
        Assert.True(done, words);
        Assert.Equal("Changed. Your next connection uses the new password; anyone connected now stays connected.", words);
        Assert.Equal("Ab3$xY9z\n", File.ReadAllText(tool + ".stdin"));
        Assert.Equal(["-f"], File.ReadAllLines(tool + ".argv"));
        Assert.Equal(System.Security.Cryptography.SHA256.HashData("Ab3$xY9z\n"u8.ToArray())[..8], File.ReadAllBytes(PasswordFile));
        Assert.Equal(["vncauth"], Directory.GetFiles(Path.GetDirectoryName(PasswordFile)!).Select(Path.GetFileName));
        Assert.DoesNotContain("Ab3$xY9z", words, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(PasswordFile));
    }

    [Fact]
    public void The_file_is_renamed_over_never_written_into()
    {
        if (OperatingSystem.IsWindows()) return;   // a rename over an open file is POSIX's: the owner's host is Linux
        File.WriteAllBytes(PasswordFile, [1, 2, 3, 4, 5, 6, 7, 8]);
        using var before = new FileStream(PasswordFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var (done, words) = Door().ChangePassword("Ab3$xY9z", "Ab3$xY9z");
        Assert.True(done, words);
        var old = new byte[8];
        before.ReadExactly(old);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], old);   // the file Xvnc may hold open is whole: the old one, never half of each
        Assert.NotEqual(old, File.ReadAllBytes(PasswordFile));
    }

    [Theory]
    [InlineData("Ab3$xY9z", "Ab3$xY9Z", "The two passwords are not the same.")]
    [InlineData("Ab3$x", "Ab3$x", "6 to 8 letters, digits or symbols")]
    [InlineData("Ab3$xY9zq", "Ab3$xY9zq", "6 to 8 letters, digits or symbols")]
    [InlineData("Ab3 xY9z", "Ab3 xY9z", "6 to 8 letters, digits or symbols")]
    public void A_refused_password_changes_nothing_and_says_why_without_repeating_it(string first, string second, string why)
    {
        File.WriteAllBytes(PasswordFile, [1, 2, 3, 4, 5, 6, 7, 8]);
        var tool = FakeTool();
        var (done, words) = Door(tool).ChangePassword(first, second);
        Assert.False(done);
        Assert.Contains(why, words, StringComparison.Ordinal);
        Assert.EndsWith("The old password still works.", words, StringComparison.Ordinal);
        Assert.DoesNotContain(first, words, StringComparison.Ordinal);
        Assert.False(File.Exists(tool + ".stdin"), "a refused password reached the tool");
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], File.ReadAllBytes(PasswordFile));
    }

    [Fact]
    public void A_missing_tool_a_failing_tool_and_an_unwritable_folder_each_leave_the_old_password()
    {
        File.WriteAllBytes(PasswordFile, [1, 2, 3, 4, 5, 6, 7, 8]);
        var (done, words) = Door(Path.Combine(_dir, "no-such-tool")).ChangePassword("Ab3$xY9z", "Ab3$xY9z");
        Assert.False(done);
        Assert.Contains("is not at", words, StringComparison.Ordinal);

        (done, words) = Door(FakeTool(fails: true)).ChangePassword("Ab3$xY9z", "Ab3$xY9z");
        Assert.False(done);
        Assert.Contains("refused it (exit 1)", words, StringComparison.Ordinal);

        var gone = new WindowDoor("198.51.100.9", 5901, Path.Combine(_dir, "no-such-folder", "vncauth"), FakeTool());
        (done, words) = gone.ChangePassword("Ab3$xY9z", "Ab3$xY9z");
        Assert.False(done);
        Assert.Contains("could not write", words, StringComparison.Ordinal);

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], File.ReadAllBytes(PasswordFile));
        Assert.All(new[] { words }, w => Assert.EndsWith("The old password still works.", w, StringComparison.Ordinal));
    }

    static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    static List<string> Words(Control root) =>
        root.GetLogicalDescendants().Prepend(root).OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text ?? "").Where(s => s.Length > 0).ToList();

    [Fact]
    public void The_card_says_where_the_window_is_reached_and_changes_the_password_on_the_second_press_only()
    {
        File.WriteAllBytes(PasswordFile, [1, 2, 3, 4, 5, 6, 7, 8]);
        var tool = FakeTool();
        var card = new WindowDoorCard(Door(tool));
        var words = string.Join(" | ", Words(card.Root));
        Assert.Contains("Reaching this window", words);
        Assert.Contains("on a Mac, Screen Sharing — at 198.51.100.9, port 5901, and the password you chose", words);
        Assert.Contains("TradeAgent never shows it, never logs it and never puts it in the activity line", words);
        var (first, second) = card.Fields;
        Assert.Equal('•', first.PasswordChar);
        Assert.Equal('•', second.PasswordChar);

        first.Text = "Ab3$xY9z";
        second.Text = "Ab3$xY9z";
        Click(card.ChangeButton);   // the first press arms it, and nothing else
        Assert.False(File.Exists(tool + ".stdin"), "one press reached the password tool");
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], File.ReadAllBytes(PasswordFile));
        Assert.Equal(WindowDoorCard.ChangeArmed, card.ChangeButton.Content as string);

        Click(card.ChangeButton);
        Assert.Equal("Ab3$xY9z\n", File.ReadAllText(tool + ".stdin"));
        Assert.Equal("", first.Text);
        Assert.Equal("", second.Text);
        Assert.Equal("Changed. Your next connection uses the new password; anyone connected now stays connected.", card.Said);
        Assert.DoesNotContain(Words(card.Root), w => w.Contains("Ab3$xY9z", StringComparison.Ordinal));
    }

    [Fact]
    public void On_loopback_the_card_says_only_an_administrators_connection_reaches_it()
    {
        var words = string.Join(" | ", Words(new WindowDoorCard(Door(address: "127.0.0.1")).Root));
        Assert.Contains("this server's own loopback. Only an administrator's connection to the server reaches it", words);
        Assert.DoesNotContain("Screen Sharing", words);
    }

    [Fact]
    public void No_cell_grant_may_be_lie_inside_or_hold_the_windows_password_folder_in_any_spelling()
    {
        var home = Path.Combine(_dir, "acct", ".local", "share", "TradeAgent");
        Directory.CreateDirectory(home);
        var vnc = Path.GetDirectoryName(PasswordFile)!;
        var guarded = CellPolicy.Guarded.Of(home, PasswordFile);
        foreach (var form in new[] { vnc, PasswordFile })
            Assert.Contains("the window's password", guarded.Refuses(Path.GetFullPath(form)) ?? "(granted)", StringComparison.Ordinal);
        // The account's home holds the door AND the app's home: refused, in the home's words first.
        Assert.NotNull(guarded.Refuses(Path.GetFullPath(Path.Combine(_dir, "acct"))));
        // A grant beside it, in the account's home, is still the policy's to judge: this guard names the door's folder only.
        Assert.Null(CellPolicy.Guarded.Of(home, PasswordFile).Refuses(Path.GetFullPath(Path.Combine(_dir, "acct", "elsewhere"))));
        // Without the kit's line there is no door, and nothing is guarded for it.
        Assert.Null(CellPolicy.Guarded.Of(home, null).Refuses(Path.GetFullPath(vnc)));

        if (OperatingSystem.IsWindows()) return;   // a symbolic link made in a test is POSIX's here
        // THE DOOR NAMED THROUGH A LINK is guarded where it really is.
        var alias = Path.Combine(_dir, "alias");
        Directory.CreateSymbolicLink(alias, Path.Combine(_dir, "acct"));
        var throughLink = CellPolicy.Guarded.Of(home, Path.Combine(alias, ".vnc", "vncauth"));
        Assert.Contains("the window's password", throughLink.Refuses(Path.GetFullPath(vnc)) ?? "(granted)", StringComparison.Ordinal);
        Assert.Contains("the window's password", throughLink.Refuses(Path.Combine(alias, ".vnc")) ?? "(granted)", StringComparison.Ordinal);
    }
}

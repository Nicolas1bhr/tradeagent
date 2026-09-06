using TradeAgent.App;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// WHAT THE APP IS ALLOWED TO HAND TO THE SHELL.
///
/// Milestone review 2026-09-05b, UNVERIFIED 4. <c>MainWindow.OpenPath</c> started every target with
/// <c>UseShellExecute = true</c> and checked nothing, and one of its callers passes a string this
/// build did not choose: the sign-in address is whatever the AI CLI printed, matched by
/// <c>RuntimeManifest.AuthUrlPattern</c>, and that pattern — like the help and download addresses
/// beside it — can be replaced from <c>runtimes.json</c>. Both built-in patterns are anchored on
/// <c>https?://</c>. An override is anchored on nothing at all, so the value reaching the shell could
/// be <c>file:</c>, a UNC share, or a bare path, and the shell would do whatever the association for
/// it says.
///
/// The tests below never launch anything: <c>Open</c> takes the shell-out as an argument, and every
/// case asserts on both halves — what the owner is told, and whether the launcher was reached at all.
/// The second is the one that matters, because a refusal that still starts the process is not one.
/// </summary>
public class OpenPathTests
{
    static (string? Said, List<string> Launched) Open(string target)
    {
        string? said = null;
        var launched = new List<string>();
        MainWindow.Open(target, m => said = m, launched.Add);
        return (said, launched);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ta-not-a-real-scheme://sign-in")]
    [InlineData("file:///etc/passwd")]
    [InlineData(@"file:///C:/Windows/System32/cmd.exe")]
    [InlineData(@"\\evil-share\payload.exe")]
    [InlineData("sign-in-here")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:windowsupdate")]
    [InlineData("ftp://example.com/x")]
    public void Nothing_but_a_web_address_or_one_of_our_own_folders_reaches_the_shell(string target)
    {
        var (said, launched) = Open(target);

        Assert.Empty(launched);
        Assert.NotNull(said);
        Assert.Contains("only opens web addresses", said);
        // The SENTENCE, not the whole message: what follows it is the refused target quoted back,
        // which is the vendor's text and may say anything at all — including, as one case here
        // does, the name of a console program. What must never send anybody to a command prompt is
        // TradeAgent's own half.
        foreach (var forbidden in new[] { "cmd", "terminal", "PowerShell", "Exception" })
            Assert.DoesNotContain(forbidden, MainWindow.OnlyWebPagesAndOurFolders);
    }

    /// <summary>
    /// The boundary in the other direction: this method is how the browser gets opened at all, and a
    /// check that refused a real sign-in URL would take the whole no-terminal sign-in with it.
    /// </summary>
    [Theory]
    [InlineData("https://auth.openai.com/oauth/authorize?response_type=code&client_id=x")]
    [InlineData("http://localhost:1455/callback")]
    [InlineData("https://platform.openai.com/api-keys")]
    [InlineData("https://opencode.ai/docs/")]
    [InlineData("https://atas.net/download")]
    public void A_real_sign_in_or_help_address_still_opens(string url)
    {
        var (said, launched) = Open(url);

        Assert.Null(said);
        Assert.Equal(url, Assert.Single(launched));
    }

    /// <summary>
    /// The folders the buttons on the Dashboard, the Inbox and the Checks page open, exactly as
    /// those call sites pass them.
    /// </summary>
    [Fact]
    public void Our_own_folders_still_open_and_no_others_do()
    {
        foreach (var ours in new[] { Paths.Workspace, Paths.Inbox, Paths.Home })
        {
            var (said, launched) = Open(ours);
            Assert.Null(said);
            Assert.Equal(ours, Assert.Single(launched));
        }

        // A real, existing directory that is not ours. Nothing here opens the rest of the disk.
        var elsewhere = Path.GetTempPath();
        var (why, none) = Open(elsewhere);
        Assert.Empty(none);
        Assert.NotNull(why);
    }

    /// <summary>
    /// A refused target is quoted back so the owner can see WHICH address was turned down — and it
    /// is a vendor program's output, so it is bounded before it reaches a label.
    /// </summary>
    [Fact]
    public void A_refused_target_is_quoted_back_and_bounded()
    {
        var (said, launched) = Open("ta-scheme://" + new string('x', 4000));

        Assert.Empty(launched);
        Assert.NotNull(said);
        Assert.Contains("ta-scheme://xxx", said);
        Assert.True(said!.Length < 300, $"the refusal ran to {said.Length} characters");
    }
}

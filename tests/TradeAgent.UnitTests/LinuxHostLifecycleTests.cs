using System.Diagnostics;
using System.Runtime.InteropServices;
using TradeAgent.App;
using TradeAgent.Provisioning;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE APP'S LIFE ON A LINUX HOST (<c>U-linux-host</c> item 2): how it ends when the system stops it, and what its
/// updater does where the only installable file is a Windows installer.
///
/// <para>systemd stops a unit with SIGTERM. Nothing in the app heard it, so .NET's default ending ran instead of the
/// quit the OS's own quit is held for — the AI's tree stopped, the ledgers closed, the stop written down — and the
/// app's ending was a death the record could not tell from a crash. And the updater, asked on a Linux host, offered
/// TradeAgent-Setup-x64.exe, downloaded 90 MB of it and only then refused to run it.</para>
/// </summary>
public class LinuxHostLifecycleTests
{
    /// <summary>
    /// (d) SIGTERM RUNS THE HELD QUIT ONCE, WITHIN ITS BOUND. The signal's default ending is cancelled — left in
    /// place, the runtime exits and the held quit never runs — and the quit is held exactly as the OS's quit is: the
    /// stop runs once, and a stop that does not finish holds it for the bound and no longer. A second SIGTERM while
    /// the first is honoured is cancelled too and starts nothing. Driven through the seam the app's registration
    /// maps the signal to (<c>TradeAgentApp</c>, off Windows), with the context the runtime would hand it.
    /// </summary>
    [Fact]
    public async Task Sigterm_runs_the_held_quit_once_within_its_bound()
    {
        var stops = 0;
        var lets = 0;
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var quit = new Quit(() => { Interlocked.Increment(ref stops); return new TaskCompletionSource().Task; },
            TimeSpan.FromMilliseconds(300));

        var watch = Stopwatch.StartNew();
        var first = new PosixSignalContext(PosixSignal.SIGTERM);
        quit.Signal(first, () => { Interlocked.Increment(ref lets); released.TrySetResult(); });
        Assert.True(first.Cancel,
            "SIGTERM's default ending was left in place: the runtime exits and the held quit never runs");

        var second = new PosixSignalContext(PosixSignal.SIGTERM);
        quit.Signal(second, () => Interlocked.Increment(ref lets));
        Assert.True(second.Cancel, "a second SIGTERM, while the first was being honoured, was let through to the default ending");

        await released.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(250), $"the quit was let go after {watch.Elapsed}, before its bound");

        quit.Finish();
        Assert.Equal(1, Volatile.Read(ref stops));
        Assert.Equal(1, Volatile.Read(ref lets));
    }

    const string Asset = "TradeAgent-Setup-x64.exe";
    const string Hash = "b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9";

    /// <summary>A newer release whose one installable file is the Windows installer, with its checksum file.</summary>
    static string NewerRelease() =>
        $$"""
        {"tag_name":"v0.9.0","draft":false,"prerelease":false,
         "html_url":"https://github.com/owner/repo/releases/tag/v0.9.0","body":"notes",
         "assets":[{"name":"{{Asset}}","size":90000000,"browser_download_url":"https://example.invalid/{{Asset}}"},
                   {"name":"SHA256SUMS.txt","size":100,"browser_download_url":"https://example.invalid/SHA256SUMS.txt"}]}
        """;

    /// <summary>
    /// (e) OFF WINDOWS THE UPDATER OFFERS AND DOWNLOADS NO WINDOWS INSTALLER. A Linux host's versions arrive by
    /// deploy, on the owner's word; the updater there asks GitHub nothing, offers nothing, downloads nothing and
    /// starts nothing, and both of its surfaces say why. Operator authority over updating stays where it was —
    /// in-process, out of the agent's reach — and on this host there is simply nothing for it to press.
    /// </summary>
    [Fact]
    public async Task Off_windows_the_updater_offers_and_downloads_no_windows_installer()
    {
        var asked = 0;
        var downloads = 0;
        var launches = 0;
        var sources = new UpdateSources(
            _ => { Interlocked.Increment(ref asked); return Task.FromResult<string?>(NewerRelease()); },
            (_, _) => Task.FromResult<string?>($"{Hash}  artifacts/{Asset}\n"),
            (_, _, _, _) => { Interlocked.Increment(ref downloads); return Task.FromResult("/updates/0.9.0/" + Asset); },
            _ => Interlocked.Increment(ref launches),
            (_, _) => Task.FromResult(Hash));

        var updates = new UpdateService("0.1.2", "owner/repo", UpdateService.DefaultAssetPattern, sources, byDeploy: true)
        {
            UnconfirmedWork = () => 0
        };

        await updates.CheckAsync();
        Assert.Null(updates.Available);
        Assert.False(updates.ShouldPrompt, "the banner offers a Windows installer to a Linux host");
        Assert.Equal(0, asked);
        Assert.Equal(UpdateStage.ByDeploy, updates.Stage);
        Assert.Equal(UpdateService.DeployedHere, updates.Message);

        Assert.False(await updates.InstallAsync());
        Assert.Equal(0, downloads);
        Assert.Equal(0, launches);
        Assert.False(updates.InstallInProgress, "the trading latch went up for an install that cannot happen here");
    }
}

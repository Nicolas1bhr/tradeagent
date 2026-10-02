using TradeAgent.AgentRuntime;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A VERSION IS A VERSION ONLY FROM AN EXIT-0 RUN, and a program that cannot start is not an
/// installed runtime.
///
/// <para>Where this came from: on the dev Mac on 2026-09-20 the observed run stalled at "Sign in to
/// your AI account" although the vendor CLI answered "Logged in using ChatGPT", exit 0, in a shell.
/// Every agent process is relaunched through TradeAgent's own framework-dependent <c>trade</c>, and
/// the child's whitelist dropped the runtime location — so the LAUNCHER printed "You must install
/// .NET to run this application" and exited 131 before the CLI ran. The step before it,
/// "Installing the AI assistant", had already PASSED: detection asked only whether a file existed
/// at the resolved path, so a runtime nothing on this computer can start was reported as ready and
/// the launcher's complaint was never shown to anybody.</para>
///
/// <para>The stub here is that failure with nothing else in it: a program that prints the apphost's
/// message on stderr and exits 131.</para>
/// </summary>
public class RuntimeDetectionTests
{
    /// <summary>
    /// The apphost's own words, from the run quoted above. Only the first line is asserted on — the
    /// rest is here because a reason that arrives stripped of its context is a reason nobody can act
    /// on, and the product shows the first line of exactly this.
    /// </summary>
    static readonly string[] ApphostFailure =
    [
        "You must install .NET to run this application.",
        "",
        "App: /the/app/bin/trade",
        "Architecture: arm64",
        ".NET location: Not found",
        "",
        "The following locations were searched:",
        "  Environment variable:",
        "    DOTNET_ROOT_ARM64 = <not set>",
        "    DOTNET_ROOT = <not set>"
    ];

    [Fact]
    public async Task A_launcher_that_cannot_start_is_not_an_installed_runtime()
    {
        var detection = await Runtime(Stub("apphost-fails", ApphostFailure, exit: 131)).DetectAsync();

        Assert.False(detection.Installed,
            "a program that exits 131 without running was reported as an installed runtime; setup " +
            "then advances past the install step and the owner is never told what the program said");
        Assert.Null(detection.Version);
        Assert.StartsWith("You must install .NET", detection.Reason);
    }

    /// <summary>
    /// The other half, so that the rule above cannot be satisfied by calling everything broken: a
    /// program that runs and says what it is IS installed, and nothing is reported against it.
    /// </summary>
    [Fact]
    public async Task An_exit_zero_run_is_the_version()
    {
        var detection = await Runtime(Stub("prints-a-version", ["codex-cli 0.153.4"], exit: 0)).DetectAsync();

        Assert.True(detection.Installed, "a runtime that ran and printed its version was called not installed");
        Assert.Equal("0.153.4", detection.Version);
        Assert.Null(detection.Reason);
    }

    /// <summary>
    /// A PROGRAM SLOWER THAN THE VERSION PROBE'S DEADLINE DID NOT ANSWER; NOTHING TIMED OUT SIGNING IN.
    ///
    /// <para>On windows-latest (run 37017805967) PowerShell's first launch beside the full suite did not
    /// answer within the 20 s deadline, and the deadline left the probe as <c>AI_AUTH_TIMEOUT</c> — out of
    /// <c>DetectAsync</c>, so out of every caller that reads a detection: the start (refused, the card
    /// reading "Signing in took too long and was cancelled. Press Sign in again."), the Doctor's runtime
    /// rows and setup's install step. A program that did not answer is a runtime that did not show it
    /// runs, which this class already says how to report: not installed, found where it is, and why.</para>
    /// </summary>
    [Fact]
    public async Task A_program_slower_than_the_version_deadline_is_not_installed_and_says_it_did_not_answer()
    {
        var stub = Sleeper("slow-to-answer");
        var detection = await Runtime(stub, TimeSpan.FromSeconds(1)).DetectAsync();

        Assert.False(detection.Installed, "a program that never answered its version was reported as installed");
        Assert.Equal(stub, detection.Path);
        Assert.Null(detection.Version);
        Assert.Contains("did not answer within 1 second", detection.Reason, StringComparison.Ordinal);
    }

    // ---- the stub ----------------------------------------------------------------------------

    /// <summary>
    /// The manifest names a ROOTED path, so <c>ResolveExecutable</c> takes it as given and looks
    /// nowhere else, and the stub is the whole of what the version probe runs. On Windows the stub is
    /// a batch file, which <c>SetCommand</c> routes through the command interpreter exactly as it
    /// routes an npm shim — the product's own path, not one the test invented.
    /// </summary>
    static CliAgentRuntime Runtime(string executable, TimeSpan? deadline = null) =>
        new(new RuntimeManifest
        {
            Id = "detect-test",
            DisplayName = "detection probe",
            Executable = executable,
            VersionArgs = []
        })
        { VersionDeadline = deadline ?? CliAgentRuntime.DefaultVersionDeadline };

    /// <summary>A program that prints those lines on stderr, as the apphost does, and exits.</summary>
    static string Stub(string name, IReadOnlyList<string> says, int exit)
    {
        var dir = Path.Combine(TestEnv.Home, "detect", $"{name}-{Guid.NewGuid():n}"[..24]);
        Directory.CreateDirectory(dir);

        if (OperatingSystem.IsWindows())
        {
            var bat = Path.Combine(dir, name + ".cmd");
            File.WriteAllText(bat, "@echo off\r\n"
                + string.Concat(says.Select(l => $"echo {(l.Length == 0 ? "." : l)} 1>&2\r\n"))
                + $"exit /b {exit}\r\n");
            return bat;
        }

        var sh = Path.Combine(dir, name + ".sh");
        File.WriteAllText(sh, "#!/bin/sh\ncat >&2 <<'THE_PROGRAM_SAID'\n"
            + string.Join("\n", says) + $"\nTHE_PROGRAM_SAID\nexit {exit}\n");
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return sh;
    }

    /// <summary>
    /// A program that answers its version after half a minute, so that no machine is fast enough to beat
    /// a deadline of a second. ping rather than timeout on Windows: <c>timeout</c> refuses to run with
    /// its input redirected, which is how every child here is started.
    /// </summary>
    static string Sleeper(string name)
    {
        var dir = Path.Combine(TestEnv.Home, "detect", $"{name}-{Guid.NewGuid():n}"[..24]);
        Directory.CreateDirectory(dir);

        if (OperatingSystem.IsWindows())
        {
            var bat = Path.Combine(dir, name + ".cmd");
            File.WriteAllText(bat, "@echo off\r\nping -n 31 127.0.0.1 > nul\r\necho slow 1.0.0\r\n");
            return bat;
        }

        var sh = Path.Combine(dir, name + ".sh");
        File.WriteAllText(sh, "#!/bin/sh\nsleep 30\necho 'slow 1.0.0'\n");
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return sh;
    }
}

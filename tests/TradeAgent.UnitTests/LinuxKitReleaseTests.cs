using System.Diagnostics;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>A fact about the Linux kit's own shell: run on macOS and Linux; skipped on Windows, where the kit is never run and a
/// symbolic link made by Git's bash is a copy, not a link.</summary>
public sealed class LinuxKitFactAttribute : FactAttribute
{
    public LinuxKitFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "the Linux kit runs on Linux only, and its `current` link is a real symbolic link";
    }
}

/// <summary>
/// THE RELEASE THAT WAS RUNNING STAYS INSTALLED (<c>U-install-rollback</c>). <c>packaging/linux/install.sh</c> keeps two
/// releases under /opt/tradeagent — the one it installs and the one before it — so going back is a move of the
/// <c>current</c> link, with no tarball to fetch from the Mac. Its folder loop, <c>"$root_dir"/*/</c>, matched the
/// <c>current</c> link itself: read as a release, with the just-touched folder's time, it was "the one before", and the
/// release that had been running was removed. Seen twice on the owner's host on 2026-11-07.
///
/// <para>These run the kit's own <c>remove_old_releases</c>, cut from install.sh as it ships, under install.sh's own
/// <c>set -euo pipefail</c>, over a scratch folder laid out the way the kit lays out /opt/tradeagent. CI's linux-host job
/// walks the same thing through the real kit as root (two further installs, then a rollback that starts the app).</para>
/// </summary>
public class LinuxKitReleaseTests
{
    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "src"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("could not find the repository root");
    }

    /// <summary>The function exactly as install.sh carries it: from its first line to the first closing brace in column 0.</summary>
    static string Function()
    {
        var lines = File.ReadAllLines(Path.Combine(RepoRoot(), "packaging", "linux", "install.sh"));
        var start = Array.FindIndex(lines, l => l.StartsWith("remove_old_releases() {", StringComparison.Ordinal));
        Assert.True(start >= 0, "install.sh carries no remove_old_releases function");
        var end = Array.FindIndex(lines, start, l => l == "}");
        Assert.True(end > start, "remove_old_releases has no closing brace in column 0");
        return string.Join('\n', lines[start..(end + 1)]);
    }

    /// <summary>/opt/tradeagent in a scratch folder: <c>kit/</c>, and each release a folder holding an executable TradeAgent,
    /// installed in the order given (each folder's time one hour after the one before, as install.sh's touch leaves them).</summary>
    static string Root(params string[] releases)
    {
        var root = Path.Combine(TestEnv.Home, "linux-kit-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "kit"));
        File.WriteAllText(Path.Combine(root, "kit", "display-prepare"), "#!/bin/sh\n");
        var at = new DateTime(2026, 11, 7, 6, 0, 0, DateTimeKind.Utc);
        foreach (var r in releases)
        {
            var dir = Path.Combine(root, r);
            Directory.CreateDirectory(dir);
            var exe = Path.Combine(dir, "TradeAgent");
            File.WriteAllText(exe, "#!/bin/sh\n");
            if (!OperatingSystem.IsWindows())   // skipped there before it gets here: the analyzer reads only this
                File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead
                    | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            Directory.SetLastWriteTimeUtc(dir, at);
            at = at.AddHours(1);
        }
        return root;
    }

    /// <summary>The link as install.sh moves it: relative, the release's folder name.</summary>
    static void PointCurrentAt(string root, string release)
    {
        File.CreateSymbolicLink(Path.Combine(root, "current"), release);
    }

    static (int Exit, string Out, string Err) Remove(string root, string release, string wasRunning)
    {
        var script = Path.Combine(root, "..", Path.GetFileName(root) + "-remove.sh");
        File.WriteAllText(script, "set -euo pipefail\n" + Function() + "\nremove_old_releases \"$@\"\n");
        var psi = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { script, root, release, wasRunning }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var @out = p.StandardOutput.ReadToEndAsync();
        Assert.True(p.WaitForExit(60_000), "remove_old_releases did not finish in a minute");
        return (p.ExitCode, @out.Result, err.Result);
    }

    static string[] Left(string root) =>
        [.. Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    static string? Current(string root) => new FileInfo(Path.Combine(root, "current")).LinkTarget;

    /// <summary>
    /// A NEW RELEASE OVER THE ONE RUNNING: the one running stays, beside the new one, and only the one before it goes. The
    /// host had z, then a (running), and the kit installs b: the link moves to b, then the old ones are removed.
    /// </summary>
    [LinuxKitFact]
    public void A_new_release_keeps_the_one_that_was_running()
    {
        var root = Root("0.2.0-zzzzzzzzzzzz", "0.2.0-aaaaaaaaaaaa", "0.2.0-bbbbbbbbbbbb");
        PointCurrentAt(root, "0.2.0-bbbbbbbbbbbb");

        var (exit, _, err) = Remove(root, "0.2.0-bbbbbbbbbbbb", "0.2.0-aaaaaaaaaaaa");

        Assert.True(exit == 0, $"exit {exit}: {err}");
        Assert.Equal(["0.2.0-aaaaaaaaaaaa", "0.2.0-bbbbbbbbbbbb", "current", "kit"], Left(root));
        Assert.True(File.Exists(Path.Combine(root, "0.2.0-aaaaaaaaaaaa", "TradeAgent")),
            "the release that was running lost its TradeAgent: a rollback would need its tarball again");
        Assert.Equal("0.2.0-bbbbbbbbbbbb", Current(root));
    }

    /// <summary>
    /// THE SAME RELEASE AGAIN (install.sh is idempotent): nothing moved, so "the one before" is the newest release that is not
    /// the one installed — read from the release folders only. The <c>current</c> link points at the newest folder of all,
    /// and read as a release it was that one.
    /// </summary>
    [LinuxKitFact]
    public void A_second_run_of_the_same_release_keeps_the_one_installed_before_it()
    {
        var root = Root("0.2.0-zzzzzzzzzzzz", "0.2.0-aaaaaaaaaaaa", "0.2.0-bbbbbbbbbbbb");
        PointCurrentAt(root, "0.2.0-bbbbbbbbbbbb");

        var (exit, _, err) = Remove(root, "0.2.0-bbbbbbbbbbbb", "0.2.0-bbbbbbbbbbbb");

        Assert.True(exit == 0, $"exit {exit}: {err}");
        Assert.Equal(["0.2.0-aaaaaaaaaaaa", "0.2.0-bbbbbbbbbbbb", "current", "kit"], Left(root));
        Assert.Equal("0.2.0-bbbbbbbbbbbb", Current(root));
    }

    /// <summary>
    /// ONLY A RELEASE IS EVER REMOVED, AND A LINK IS NEVER ONE. A folder with no TradeAgent in it is not the kit's to
    /// remove; a <c>current.new</c> an interrupted run left behind is a link, not a release; and a link that named a release
    /// no longer there falls back to the newest release that is.
    /// </summary>
    [LinuxKitFact]
    public void Only_release_folders_are_removed_and_a_link_is_never_one()
    {
        var root = Root("0.2.0-zzzzzzzzzzzz", "0.2.0-aaaaaaaaaaaa", "0.2.0-bbbbbbbbbbbb", "0.2.0-cccccccccccc");
        Directory.CreateDirectory(Path.Combine(root, "kept-by-hand"));
        File.CreateSymbolicLink(Path.Combine(root, "current.new"), "0.2.0-aaaaaaaaaaaa");
        PointCurrentAt(root, "0.2.0-cccccccccccc");

        var (exit, _, err) = Remove(root, "0.2.0-cccccccccccc", "0.2.0-gonegonegone");

        Assert.True(exit == 0, $"exit {exit}: {err}");
        Assert.Equal(["0.2.0-bbbbbbbbbbbb", "0.2.0-cccccccccccc", "current", "current.new", "kept-by-hand", "kit"], Left(root));
        Assert.Equal("0.2.0-cccccccccccc", Current(root));
    }
}

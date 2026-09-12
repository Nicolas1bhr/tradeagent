using System.Diagnostics;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE TURN IS HELD BY SOMETHING THAT IS NOT A PARENT LINK.
///
/// The property under test is one sentence: a process the turn started, which then arranged to be
/// nobody's child, is still dead after <c>CancelAsync</c>. It is the whole of item 1 of
/// <c>U-containment</c>, and before that item it was false on every platform — <c>Kill(entireProcess
/// Tree: true)</c> walks parent links, and an orphan has none left to walk.
///
/// The grandchild here is deliberately a real orphan rather than a nested child: the middle process
/// exits before the assertion, so the kernel has already reparented the survivor away from anything
/// the app could reach by walking down from its own child.
/// </summary>
[Collection("containment")]
public class ContainmentTests
{
    /// <summary>Long enough that a survivor is unmistakable, short enough that a failed run cleans itself up.</summary>
    const int SurvivorSeconds = 45;

    [Fact]
    public async Task A_detached_grandchild_does_not_survive_the_cancel()
    {
        var dir = Path.Combine(TestEnv.Home, "contain-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        var pidFile = Path.Combine(dir, "grandchild.pid");

        DeployLauncher();

        var manifest = new RuntimeManifest
        {
            Id = "containment-probe",
            DisplayName = "containment probe",
            Executable = Shell,
            // The message IS the script: one template, one substitution, nothing else on the command
            // line. AgentArgs.For then builds exactly what a real turn builds.
            ExecArgs = [ShellFlag, "{prompt}"],
            TaskArgs = [ShellFlag, "{prompt}"],
            ResumeArgs = [],
            UnattendedArgs = [],
            ModelArgs = [],
            JsonFlag = null
        };

        var session = new AgentSession(manifest, () => Shell, () => dir, () => new Dictionary<string, string>(),
            new AgentPresence());

        var turn = session.SendAsync(Script(dir, pidFile));
        var grandchild = await WaitForOrphan(pidFile);

        try
        {
            Assert.True(Alive(grandchild), "the probe never got a running grandchild to detach");

            await session.CancelAsync();
            await turn;

            // The kernel does not take a whole process group down synchronously with the call.
            for (var i = 0; i < 100 && Alive(grandchild); i++) await Task.Delay(100);

            Assert.False(Alive(grandchild),
                $"the turn's detached grandchild (pid {grandchild}) was still running after CancelAsync; " +
                "nothing but a parent link was holding the turn");
        }
        finally { Reap(grandchild); }
    }

    /// <summary>
    /// The refusal at the LAUNCH, not in the pure function: a turn in the armed live configuration
    /// must start no process at all, and the owner must be told why in the window they armed it in.
    /// </summary>
    [Fact]
    public async Task The_armed_live_configuration_starts_no_vendor_process()
    {
        var dir = Path.Combine(TestEnv.Home, "refuse-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        var marker = Path.Combine(dir, "the-cli-ran");

        var manifest = new RuntimeManifest
        {
            Id = "containment-refusal-probe",
            DisplayName = "containment refusal probe",
            Executable = Shell,
            ExecArgs = [ShellFlag, "{prompt}"],
            TaskArgs = [ShellFlag, "{prompt}"],
            ResumeArgs = [], UnattendedArgs = [], ModelArgs = [], JsonFlag = null
        };

        var refusal = "Real-money trading is switched on, and nothing confines the AI here.";
        var armed = true;

        var session = new AgentSession(manifest, () => Shell, () => dir,
            () => new Dictionary<string, string>(), new AgentPresence(),
            launchRefusal: () => armed ? refusal : null);

        await session.SendAsync(Touch(marker));

        Assert.False(File.Exists(marker),
            "the vendor CLI ran in the armed live configuration: a program nothing on this computer " +
            "confines was started beside a switch that spends money");
        Assert.Contains(session.History, t => t.Role == ChatRole.System && t.Text.Contains(refusal));

        // And the same session runs normally the moment real money is switched off — the refusal is
        // read at every launch, not captured once.
        armed = false;
        await session.SendAsync(Touch(marker));
        for (var i = 0; i < 100 && !File.Exists(marker); i++) await Task.Delay(50);
        Assert.True(File.Exists(marker), "the AI did not run once real-money trading was switched off");
    }

    static string Touch(string marker) => OperatingSystem.IsWindows()
        ? $"echo ran > \"{marker}\""
        : $"printf ran > \"{marker}\"";

    // ---- the probe ------------------------------------------------------------------------------

    static string Shell => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")
        : "/bin/sh";

    static string ShellFlag => OperatingSystem.IsWindows() ? "/c" : "-c";

    /// <summary>
    /// A turn that leaves one process behind on purpose.
    ///
    /// Unix: an inner shell backgrounds the survivor and exits at once, so the survivor's parent is
    /// gone before anything looks at it; the outer shell then sleeps, which is what keeps the turn in
    /// flight until the test cancels it.
    ///
    /// Windows: the same shape through a detached <c>cmd</c> — <c>start /b</c> gives the middle
    /// process, which writes the survivor's id and exits, and the outer <c>cmd</c> waits.
    /// </summary>
    static string Script(string dir, string pidFile)
    {
        if (!OperatingSystem.IsWindows())
            return $"/bin/sh -c 'sleep {SurvivorSeconds} & echo $! > \"{pidFile}\"' & sleep {SurvivorSeconds}";

        var inner = Path.Combine(dir, "inner.cmd");
        File.WriteAllText(inner,
            "@echo off\r\n" +
            "powershell -NoProfile -NonInteractive -Command " +
            $"\"$p = Start-Process -FilePath '{PingExe}' -ArgumentList '-n','{SurvivorSeconds * 2}','127.0.0.1' " +
            $"-PassThru -WindowStyle Hidden; Set-Content -Path '{pidFile}' -Value $p.Id -Encoding ascii\"\r\n");
        return $"start \"\" /b \"{inner}\" & {PingExe} -n {SurvivorSeconds * 2} 127.0.0.1 > nul";
    }

    static string PingExe =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe");

    /// <summary>
    /// Waits until the probe has written its survivor's id AND that survivor has actually been
    /// orphaned. Asserting before the middle process has exited would test the tree walk, not the
    /// containment.
    /// </summary>
    static async Task<int> WaitForOrphan(string pidFile)
    {
        for (var i = 0; i < 300; i++)
        {
            if (File.Exists(pidFile))
            {
                var text = Read(pidFile);
                if (int.TryParse(text.Trim(), out var pid) && pid > 1 && Alive(pid) && Orphaned(pid))
                    return pid;
            }
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException($"the containment probe never orphaned a grandchild ({pidFile})");
    }

    static string Read(string file)
    {
        try { using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); return new StreamReader(s).ReadToEnd(); }
        catch (IOException) { return ""; }
    }

    static bool Alive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Whether the process's parent is gone. On Windows a dead parent is one that is not there.</summary>
    static bool Orphaned(int pid)
    {
        if (!OperatingSystem.IsWindows())
            return ParentUnix(pid) is 1 or 0 or -1;

        var parent = ParentWindows(pid);
        return parent is null || !Alive(parent.Value);
    }

    static int ParentUnix(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo("/bin/ps") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ppid=");
            psi.ArgumentList.Add("-p"); psi.ArgumentList.Add(pid.ToString());
            using var p = Process.Start(psi)!;
            var text = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return int.TryParse(text, out var ppid) ? ppid : -1;
        }
        catch (Exception) { return -1; }
    }

    static int? ParentWindows(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add($"(Get-CimInstance Win32_Process -Filter 'ProcessId={pid}').ParentProcessId");
            using var p = Process.Start(psi)!;
            var text = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return int.TryParse(text, out var ppid) ? ppid : null;
        }
        catch (Exception) { return null; }
    }

    static void Reap(int pid)
    {
        try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); }
        catch (Exception) { /* the point of the test is that it is already gone */ }
    }

    /// <summary>
    /// Puts TradeAgent's own trade command where the containment looks for it, because on macOS and
    /// Linux that binary IS the containment: it is the process that calls setsid before becoming the
    /// agent's runtime. The product deploys it at start (ToolDeployer.EnsureTradeCli); a test
    /// assembly has a build output instead, so it writes a shim that reaches the same CLI.
    /// </summary>
    static void DeployLauncher()
    {
        if (OperatingSystem.IsWindows()) return;    // the job object needs no launcher

        var exe = Path.Combine(Paths.Bin, ToolDeployer.TradeCliName);
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(host) || !File.Exists(host)) host = Environment.ProcessPath;
        File.WriteAllText(exe, $"#!/bin/sh\nexec \"{host}\" \"{Build.TradeCliDll}\" \"$@\"\n");
        File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

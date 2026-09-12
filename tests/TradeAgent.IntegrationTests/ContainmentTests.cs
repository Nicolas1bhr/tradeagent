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
/// THE PROBE IS THREE SCRIPT FILES AND TWO FILES IT WRITES, on both platforms, and none of that is
/// decoration. The first version passed the whole probe as one quoted argument to <c>cmd /c</c> and
/// never ran at all on the hosted windows runner — .NET escapes an embedded quote as <c>\"</c> and
/// cmd.exe reads only <c>""</c> — so the test failed while the product was fine. A file has no command
/// line to mangle. And liveness is a HEARTBEAT rather than a process id, because that is one mechanism
/// for both platforms instead of `ps` on one and `Get-CimInstance` on the other: the survivor appends a
/// line a second, and "the survivor is dead" is the file going quiet.
/// </summary>
[Collection("containment")]
public class ContainmentTests
{
    /// <summary>Long enough that a survivor is unmistakable, short enough that a failed run cleans itself up.</summary>
    const int SurvivorSeconds = 60;

    /// <summary>Ticks the survivor must have written before the cancel, so a probe that never ran cannot pass.</summary>
    const int TicksBeforeCancel = 2;

    [Fact]
    public async Task A_detached_grandchild_does_not_survive_the_cancel()
    {
        var probe = Probe.Write(Path.Combine(TestEnv.Home, "contain-" + Guid.NewGuid().ToString("n")[..8]));
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

        var session = new AgentSession(manifest, () => Shell, () => probe.Dir,
            () => new Dictionary<string, string>(), new AgentPresence());

        var turn = session.SendAsync(probe.Command);
        try
        {
            await probe.WaitForOrphanedSurvivor();

            await session.CancelAsync();
            await turn;

            var quiet = await probe.WentQuiet();
            Assert.True(quiet,
                "the turn's detached grandchild was still writing after CancelAsync; nothing but a " +
                $"parent link was holding the turn.{probe.Trace()}");
        }
        finally { probe.Reap(); }
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
            () => new Dictionary<string, string> { [MarkerVariable] = marker }, new AgentPresence(),
            launchRefusal: () => armed ? refusal : null);

        await session.SendAsync(MakeMarker());

        Assert.False(Directory.Exists(marker),
            "the vendor CLI ran in the armed live configuration: a program nothing on this computer " +
            "confines was started beside a switch that spends money");
        Assert.Contains(session.History, t => t.Role == ChatRole.System && t.Text.Contains(refusal));

        // And the same session runs normally the moment real money is switched off — the refusal is
        // read at every launch, not captured once.
        armed = false;
        await session.SendAsync(MakeMarker());
        for (var i = 0; i < 100 && !Directory.Exists(marker); i++) await Task.Delay(50);
        Assert.True(Directory.Exists(marker), "the AI did not run once real-money trading was switched off");
    }

    /// <summary>
    /// "The AI ran", written so that no quote and no redirect ever reaches a command line: the path
    /// travels in the environment — which also proves TradeAgent's own additions still cross the
    /// whitelist — and the command makes a DIRECTORY, because <c>mkdir</c> and <c>md</c> both take one
    /// argument and need no shell.
    /// </summary>
    const string MarkerVariable = "TA_TEST_MARKER";

    static string MakeMarker() => OperatingSystem.IsWindows()
        ? $"md %{MarkerVariable}%"
        : $"mkdir \"${MarkerVariable}\"";

    static string Shell => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")
        : "/bin/sh";

    static string ShellFlag => OperatingSystem.IsWindows() ? "/c" : "-c";

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

    // ---- the probe ------------------------------------------------------------------------------

    /// <summary>
    /// A turn that leaves one process behind on purpose, as files on disk.
    ///
    /// Three processes and two facts. The TURN's own process starts the MIDDLE one and then waits, so
    /// the turn is still in flight when the test cancels it. The middle one starts the SURVIVOR and
    /// then exits — which is what makes the survivor an orphan, with no parent link left for a tree
    /// walk to follow — recording that it got that far in <c>middle.done</c>. The survivor appends a
    /// line to <c>heartbeat</c> once a second for as long as it lives.
    ///
    /// So "the survivor was alive and already orphaned" is: the heartbeat grew, and middle.done
    /// exists. And "the survivor is dead" is: the heartbeat stopped growing. No process ids, no `ps`,
    /// no WMI, and the same three sentences on both platforms.
    /// </summary>
    sealed class Probe
    {
        public string Dir { get; private init; } = "";

        /// <summary>What the turn is asked to run. A path on Windows, a short script on Unix.</summary>
        public string Command { get; private init; } = "";

        string Heartbeat => Path.Combine(Dir, "heartbeat");
        string MiddleDone => Path.Combine(Dir, "middle.done");
        string TraceFile => Path.Combine(Dir, "trace");

        public static Probe Write(string dir)
        {
            Directory.CreateDirectory(dir);
            var heartbeat = Path.Combine(dir, "heartbeat");
            var done = Path.Combine(dir, "middle.done");
            var trace = Path.Combine(dir, "trace");

            if (OperatingSystem.IsWindows())
            {
                var ping = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe");
                var survivor = Path.Combine(dir, "survivor.cmd");
                var middle = Path.Combine(dir, "middle.cmd");
                var outer = Path.Combine(dir, "probe.cmd");

                File.WriteAllText(survivor,
                    "@echo off\r\n" +
                    ":loop\r\n" +
                    $"echo tick >> \"{heartbeat}\"\r\n" +
                    $"\"{ping}\" -n 2 127.0.0.1 > nul\r\n" +
                    "goto loop\r\n");

                File.WriteAllText(middle,
                    "@echo off\r\n" +
                    $"echo middle running >> \"{trace}\"\r\n" +
                    $"start \"\" /b cmd /c \"{survivor}\"\r\n" +
                    $"echo middle started the survivor >> \"{trace}\"\r\n" +
                    $"echo done > \"{done}\"\r\n");

                File.WriteAllText(outer,
                    "@echo off\r\n" +
                    $"echo turn running >> \"{trace}\"\r\n" +
                    $"start \"\" /b cmd /c \"{middle}\"\r\n" +
                    $"echo turn started the middle >> \"{trace}\"\r\n" +
                    $"\"{ping}\" -n {SurvivorSeconds} 127.0.0.1 > nul\r\n");

                return new Probe { Dir = dir, Command = outer };
            }

            var survivorSh = Path.Combine(dir, "survivor.sh");
            var middleSh = Path.Combine(dir, "middle.sh");
            File.WriteAllText(survivorSh,
                $"while true; do echo tick >> \"{heartbeat}\"; sleep 1; done\n");
            File.WriteAllText(middleSh,
                $"echo middle running >> \"{trace}\"\n" +
                $"/bin/sh \"{survivorSh}\" &\n" +
                $"echo middle started the survivor >> \"{trace}\"\n" +
                $"echo done > \"{done}\"\n");

            return new Probe
            {
                Dir = dir,
                Command = $"echo turn running >> \"{trace}\"; /bin/sh \"{middleSh}\"; sleep {SurvivorSeconds}"
            };
        }

        /// <summary>
        /// Waits until the survivor has written <see cref="TicksBeforeCancel"/> lines AND its parent
        /// has exited. Asserting before the middle process is gone would test the tree walk rather
        /// than the containment, and asserting before the survivor has written anything would let a
        /// probe that never ran pass.
        /// </summary>
        public async Task WaitForOrphanedSurvivor()
        {
            for (var i = 0; i < 400; i++)
            {
                if (File.Exists(MiddleDone) && Ticks() >= TicksBeforeCancel) return;
                await Task.Delay(100);
            }
            throw new Xunit.Sdk.XunitException(
                $"the containment probe never produced an orphaned survivor.{Trace()}");
        }

        /// <summary>
        /// Whether the heartbeat has stopped. Two readings a few seconds apart, because the kernel
        /// does not take a whole job or process group down synchronously with the call, and the
        /// survivor's own tick is a second wide.
        /// </summary>
        public async Task<bool> WentQuiet()
        {
            for (var i = 0; i < 20; i++)
            {
                var before = Ticks();
                await Task.Delay(1500);
                if (Ticks() == before)
                {
                    await Task.Delay(2500);
                    if (Ticks() == before) return true;
                }
            }
            return false;
        }

        /// <summary>Everything the probe wrote about itself, for a failure that would otherwise be a guess.</summary>
        public string Trace()
        {
            var parts = new List<string>
            {
                $"ticks={Ticks()}",
                $"middle.done={File.Exists(MiddleDone)}",
                "trace=[" + Read(TraceFile).Replace("\r", "").Replace("\n", " | ").Trim() + "]"
            };
            foreach (var f in Directory.Exists(Dir) ? Directory.GetFiles(Dir) : [])
                parts.Add(Path.GetFileName(f));
            return " Probe: " + string.Join(", ", parts);
        }

        int Ticks() => Read(Heartbeat).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

        static string Read(string file)
        {
            try
            {
                using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return new StreamReader(s).ReadToEnd();
            }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }

        /// <summary>
        /// Leaves nothing behind when the test fails. A survivor that outlived its cancel is the
        /// failure, and it must not also become an orphan that outlives the whole test run.
        /// </summary>
        public void Reap()
        {
            try { File.Delete(Path.Combine(Dir, OperatingSystem.IsWindows() ? "survivor.cmd" : "survivor.sh")); }
            catch (IOException) { }

            // The loop re-reads its own script every iteration on Windows and not at all on Unix, so
            // deleting it is enough there and nothing here can be enough on Unix: the group kill is
            // the only thing that reaches it, and if that failed the test has already said so.
            if (!OperatingSystem.IsWindows()) return;
            try { File.Delete(Path.Combine(Dir, "middle.cmd")); }
            catch (IOException) { }
        }
    }
}

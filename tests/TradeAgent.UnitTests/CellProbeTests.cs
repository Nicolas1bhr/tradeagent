using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using TradeAgent.AgentRuntime;
using TradeAgent.CellProbe;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE WINDOWS CELL PROBE (<c>U-cell-probe</c>): the compiled program two Windows proof rows run in a cell instead of PowerShell 5.1 (R31 D5: under an enforced
/// App Control policy, Smart App Control On included, PowerShell runs ConstrainedLanguage, both scripts lose their .NET calls and the proof cannot be written).
/// Its code is compiled in here from the program's own source, so these rows drive the code the program ships. A line says DENIED only on Windows' own error
/// number 5 (ERROR_ACCESS_DENIED), never on words, which Windows writes in the system's language; any other failure is FAILED with its number, and
/// <see cref="CellDenial.Judge"/> fails such a row. The real runs in a cell are the benchmark's (<c>CellWindowsProofTests</c>, CI's Windows job).
/// </summary>
public class CellProbeTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "cell-probe-" + Guid.NewGuid().ToString("n")[..8]);

    public CellProbeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp */ }
    }

    static IReadOnlyList<string> Lines(params string[] args)
    {
        var o = new StringWriter();
        Assert.Equal(0, Probe.Run(args, o));
        return [.. o.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r'))];
    }

    /// <summary>ONLY WINDOWS' ERROR 5 IS A DENIAL: a success opens; any other number, or an error that carries no Windows number, fails, and says which.</summary>
    [Theory]
    [InlineData(null, "LIST-OPEN")]
    [InlineData(5, "LIST-DENIED")]
    [InlineData(2, "LIST-FAILED 2")]
    [InlineData(3, "LIST-FAILED 3")]
    [InlineData(87, "LIST-FAILED 87")]
    [InlineData(1314, "LIST-FAILED 1314")]
    [InlineData(unchecked((int)0x80131500), "LIST-FAILED 0x80131500")]
    public void Only_windows_error_5_is_a_denial(int? error, string line) => Assert.Equal(line, Probe.Line("LIST", error));

    /// <summary>
    /// A FOLDER THAT IS NOT THERE IS NEVER A DENIAL: the PowerShell listing caught EVERY failure as DENIED, so a missing folder, or a script ConstrainedLanguage
    /// would not run, read like the cell's refusal. The probe reads the error's number off the same .NET call, and a row given this output fails.
    /// </summary>
    [Fact]
    public void A_folder_that_is_not_there_is_never_a_denial()
    {
        var lines = Lines("list", _root, Path.Combine(_root, "not-there"));
        Assert.Equal(["CTL-LIST-OPEN", "LIST-FAILED 3"], lines);
        var (held, seen) = CellDenial.Judge(["LIST"], string.Join('\n', lines), "CTL-LIST-OPEN\nLIST-OPEN");
        Assert.False(held, seen);
        Assert.Contains("LIST: not denied", seen, StringComparison.Ordinal);
    }

    /// <summary>
    /// A FOLDER THIS ACCOUNT MAY NOT LIST IS A DENIAL, Windows' own refusal and nothing else: on Windows a folder whose access list grants nobody anything (its owner
    /// keeps the right to put it back); elsewhere mode 000.
    /// </summary>
    [NotAsRootFact("root lists a folder of mode 000")]
    public void A_folder_this_account_may_not_list_is_a_denial()
    {
        var shut = Directory.CreateDirectory(Path.Combine(_root, "shut")).FullName;
        File.WriteAllText(Path.Combine(shut, "inside.txt"), "x");
        Shut(shut, true);
        try { Assert.Equal(["CTL-LIST-OPEN", "LIST-DENIED"], Lines("list", _root, shut)); }
        finally { Shut(shut, false); }
    }

    static void Shut(string folder, bool shut)
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new DirectoryInfo(folder);
            var acl = info.GetAccessControl();
            if (shut)
            {
                acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                foreach (FileSystemAccessRule r in acl.GetAccessRules(true, false, typeof(SecurityIdentifier))) acl.RemoveAccessRuleSpecific(r);
            }
            else acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(acl);
        }
        else File.SetUnixFileMode(folder, shut ? UnixFileMode.None : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>
    /// THE KILL PROBE ENDS A PROCESS OF ITS OWN FIRST (its control: the call works from where it runs), then tries the target, by Windows' own OpenProcess and
    /// TerminateProcess. Here, outside any cell, both end. A process id Windows has no process for is error 87, a FAILED, never a denial.
    /// </summary>
    [OnlyOnFact("the probe asks Windows itself for a process", "windows")]
    public void The_kill_probe_ends_its_own_process_first_then_the_target_and_a_missing_one_is_no_denial()
    {
        using var helper = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 30 127.0.0.1")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        try
        {
            Assert.Equal(["CTL-KILL-OPEN", "KILL-OPEN"], Lines("kill", helper.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Assert.True(helper.WaitForExit(5000), "the target still runs after KILL-OPEN");
            // PID 0 IS THE IDLE PROCESS, WHICH OpenProcess NEVER OPENS: ERROR_INVALID_PARAMETER.
            Assert.Equal(["CTL-KILL-OPEN", "KILL-FAILED 87"], Lines("kill", "0"));
        }
        finally { try { helper.Kill(); } catch (Exception) { /* ended */ } }
    }

    [OnlyOnFact("off Windows the kill verb refuses to run", "linux", "macos")]
    public void Off_windows_the_kill_probe_refuses_rather_than_answer()
    {
        var o = new StringWriter();
        Assert.Equal(2, Probe.Run(["kill", "1"], o));
        Assert.DoesNotContain("-OPEN", o.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("-DENIED", o.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData]
    [InlineData("list")]
    [InlineData("list", "a")]
    [InlineData("kill")]
    [InlineData("kill", "x")]
    [InlineData("kill", "-1")]
    [InlineData("read", "a", "b")]
    public void A_call_it_does_not_know_prints_no_verdict_and_exits_2(params string[] args)
    {
        var o = new StringWriter();
        Assert.Equal(2, Probe.Run(args, o));
        Assert.DoesNotContain("-OPEN", o.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("-DENIED", o.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// NO WINDOWS PROOF ROW STARTS POWERSHELL (R31 D5): every row that cmd's own lines cannot make runs the probe, a compiled program judged by App Control like
    /// the app itself. Comment lines excepted.
    /// </summary>
    [Fact]
    public void The_windows_proof_rows_start_no_powershell()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var hits = new List<string>();
        foreach (var name in new[] { "WindowsCellBenchmark.cs", "CellDenial.cs" })
        {
            var lines = File.ReadAllLines(Path.Combine(dir.FullName, "src", "TradeAgent.AgentRuntime", name));
            for (var i = 0; i < lines.Length; i++)
                if (!lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal) && lines[i].Contains("powershell", StringComparison.OrdinalIgnoreCase))
                    hits.Add($"{name}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(hits.Count == 0, "a Windows proof row starts PowerShell:\n" + string.Join("\n", hits));
    }
}

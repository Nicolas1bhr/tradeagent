using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Security;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE WINDOWS CELLS' WEB, AND WHO MAY SPEAK TO THE DOOR (<c>U-cell-windows-egress</c>). Through the product's own path — the built
/// <c>trade.dll</c> as the launcher, the app's job, the cell stage, a slot's AppContainer:
/// <list type="bullet">
/// <item>the one fixed elevated helper places the 443 rule on EVERY slot's container, idempotently, refuses anything but its one argument, and
/// creates nothing in the account's home (CI's elevated, disposable runner only — it changes the machine; the UAC prompt itself cannot be shown
/// there);</item>
/// <item>the benchmark's web rows hold only when every slot was measured under the rule: a rule on seven slots of eight is NOT web;</item>
/// <item>the grant's job container refuses a peer whose process runs in an AppContainer though it is in the job (card 2);</item>
/// <item>the attempt's processes are Windows' own list of its job, never a name: a process outside the cell is never one of them (card 3).</item>
/// </list>
/// </summary>
[Trait("Category", "Cell"), Trait("CellScope", "Windows")]
[SupportedOSPlatform("windows")]
public sealed class CellWindowsEgressTests : IDisposable
{
    readonly ITestOutputHelper _out;
    readonly string _root;

    public CellWindowsEgressTests(ITestOutputHelper output)
    {
        _out = output;
        _root = Path.Combine(TestEnv.Home, "wegress-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        _out.WriteLine($"slot profiles deleted: {WindowsCellStage.DeleteProfiles()}");
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { /* swept with the test home */ }
    }

    static IReadOnlyList<string> Launcher()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(host) || !File.Exists(host)) host = Environment.ProcessPath!;
        return [host, Build.TradeCliDll];
    }

    static string Cmd => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    /// <summary>
    /// THE HELPER ON EVERY SLOT, AND EVERY SLOT MEASURED UNDER IT: sixteen rules (TCP all-but-443 and all UDP, per slot) absent, placed, read
    /// back exactly, placed again unchanged; then the benchmark's two web rows hold, naming all eight slots.
    /// </summary>
    [WindowsAdminFact]
    public async Task The_helper_places_the_443_rule_on_every_slot_and_the_benchmark_measures_every_slot_under_it()
    {
        RequireElevated();
        var rules = WindowsCellEgress.RulesFor(Paths.Home);
        Assert.Equal(2 * WindowsCellSlots.Count, rules.Count);
        Assert.Equal(WindowsCellStage.SlotSids(), [.. rules.Select(r => r.Sid).Distinct()]);
        try
        {
            WindowsCellEgress.RemoveForTests(rules);
            Assert.Equal(rules.Count, WindowsCellEgress.Missing(rules).Count);
            WindowsCellEgress.Place(rules);
            Assert.Empty(WindowsCellEgress.Missing(rules));
            WindowsCellEgress.Place(rules);   // idempotent: already exactly right, left alone
            Assert.Empty(WindowsCellEgress.Missing(rules));

            var rows = await WebRows();
            foreach (var r in rows) _out.WriteLine($"  {(r.Held ? "held" : "FAIL")}  {r.Name}: {r.Seen}");
            Assert.All(rows, r => Assert.True(r.Held, $"{r.Name}: {r.Seen}"));
            for (var slot = 1; slot <= WindowsCellSlots.Count; slot++)
                Assert.Contains($"slot {slot} reached", rows.Single(r => r.Name == "web-443-granted").Seen, StringComparison.Ordinal);
        }
        finally { WindowsCellEgress.RemoveForTests(rules); }
    }

    /// <summary>
    /// A RULE ON SEVEN SLOTS OF EIGHT IS NOT WEB: the benchmark measures every slot, so slot 8 — whose container no rule names — reaches port 80
    /// and the proof's web row does not hold, naming slot 8.
    /// </summary>
    [WindowsAdminFact]
    public async Task A_rule_on_seven_slots_of_eight_is_not_web()
    {
        RequireElevated();
        var rules = WindowsCellEgress.RulesFor(Paths.Home);
        var seven = rules.Where(r => r.Slot != WindowsCellSlots.Count).ToList();
        try
        {
            WindowsCellEgress.RemoveForTests(rules);
            WindowsCellEgress.Place(seven);
            Assert.Empty(WindowsCellEgress.Missing(seven));
            Assert.Equal(2, WindowsCellEgress.Missing(rules).Count);

            var rows = await WebRows();
            foreach (var r in rows) _out.WriteLine($"  {(r.Held ? "held" : "FAIL")}  {r.Name}: {r.Seen}");
            var nothingElse = rows.Single(r => r.Name == "web-nothing-else");
            Assert.False(nothingElse.Held, "web held although slot 8's container has no rule: " + nothingElse.Seen);
            Assert.Contains($".{WindowsCellSlots.Count} web:", nothingElse.Seen, StringComparison.Ordinal);   // the firewall's read names slot 8's rule
        }
        finally { WindowsCellEgress.RemoveForTests(rules); }
    }

    /// <summary>
    /// RULES THAT BLOCK ONLY WHAT THE PROBES TRY ARE NOT WEB (Codex): every slot blocked on TCP 80 alone and on UDP, under the helper's own
    /// names — each slot's probes (443 reached, 80 and direct DNS refused) all pass, and the firewall's read still refuses web, since every
    /// other TCP port stays open.
    /// </summary>
    [WindowsAdminFact]
    public async Task Rules_that_block_only_what_the_probes_try_are_not_web()
    {
        RequireElevated();
        var rules = WindowsCellEgress.RulesFor(Paths.Home);
        var narrow = rules.Select(r => r.RemotePorts == WindowsCellEgress.BlockedTcp ? r with { RemotePorts = "80" } : r).ToList();
        try
        {
            WindowsCellEgress.RemoveForTests(rules);
            WindowsCellEgress.Place(narrow);
            Assert.Empty(WindowsCellEgress.Missing(narrow));

            var rows = await WebRows();
            foreach (var r in rows) _out.WriteLine($"  {(r.Held ? "held" : "FAIL")}  {r.Name}: {r.Seen}");
            var nothingElse = rows.Single(r => r.Name == "web-nothing-else");
            Assert.False(nothingElse.Held, "web held under rules that block TCP 80 alone: " + nothingElse.Seen);
            Assert.Contains("the firewall does not hold the cells' rules exactly", nothingElse.Seen, StringComparison.Ordinal);
        }
        finally { WindowsCellEgress.RemoveForTests(rules); }
    }

    /// <summary>
    /// A SLOT ANOTHER CELL HOLDS IS NEVER COUNTED AS MEASURED: with the whole rule set in place, a cell of the test's holds slot 1 throughout;
    /// slot 1 cannot be measured, so web does not hold and the row names slot 1 — no launch that landed elsewhere stands in for it.
    /// </summary>
    [WindowsAdminFact]
    public async Task A_slot_another_cell_holds_is_not_counted_as_measured()
    {
        RequireElevated();
        var rules = WindowsCellEgress.RulesFor(Paths.Home);
        try
        {
            WindowsCellEgress.RemoveForTests(rules);
            WindowsCellEgress.Place(rules);
            Assert.Empty(WindowsCellEgress.Missing(rules));
            using var holder = StartCell("findstr x");
            await CellChild(holder);
            Assert.Equal(WindowsCellStage.SlotSids()[0], WindowsCellBenchmark.CellMembers(holder)!.Select(m => m.Sid).Distinct().Single());

            var rows = await WebRows();
            foreach (var r in rows) _out.WriteLine($"  {(r.Held ? "held" : "FAIL")}  {r.Name}: {r.Seen}");
            Assert.All(rows, r => Assert.False(r.Held, $"{r.Name} held although slot 1 was never measured: {r.Seen}"));
            Assert.Contains("slot 1:", rows.Single(r => r.Name == "web-443-granted").Seen, StringComparison.Ordinal);
        }
        finally { WindowsCellEgress.RemoveForTests(rules); }
    }

    /// <summary>
    /// THE HELPER'S OWN ENTRY, AS WINDOWS STARTS IT: anything but its one argument is refused and places nothing; exactly that argument places
    /// the account's default home's sixteen rules, read back — and creates nothing in that home.
    /// </summary>
    [WindowsAdminFact]
    public void The_helpers_entry_refuses_anything_but_its_one_argument_and_places_the_default_homes_rules()
    {
        RequireElevated();
        var home = AccountHome.Default();
        var existed = Directory.Exists(home);
        var rules = WindowsCellEgress.RulesFor(home);
        try
        {
            WindowsCellEgress.RemoveForTests(rules);
            Assert.Equal(WindowsCellEgress.Refused, WindowsCellEgress.RunHelper([WindowsCellEgress.Verb, "--open-every-port"]));
            Assert.Equal(WindowsCellEgress.Refused, WindowsCellEgress.RunHelper([]));
            Assert.Equal(rules.Count, WindowsCellEgress.Missing(rules).Count);

            Assert.Equal(WindowsCellEgress.Done, WindowsCellEgress.RunHelper([WindowsCellEgress.Verb]));
            Assert.Empty(WindowsCellEgress.Missing(rules));
            Assert.Equal(WindowsCellEgress.Done, WindowsCellEgress.RunHelper([WindowsCellEgress.Verb]));
            if (!existed) Assert.False(Directory.Exists(home), $"the helper created {home}");
            _out.WriteLine($"default home {home} existed before: {existed}; rules placed and read back: {rules.Count}");
        }
        finally { WindowsCellEgress.RemoveForTests(rules); }
    }

    /// <summary>
    /// CARD 2 — A CELL'S PROCESS IS NEVER A GRANT'S PEER: the cell's shell is in the attempt's job (the stage's job is nested in it), and the
    /// grant's container still answers Outside for it, naming the AppContainer; the stage itself, in the same job and outside any container,
    /// is a Member (the control).
    /// </summary>
    [WindowsFact]
    public async Task The_grant_container_refuses_a_cell_process_though_it_is_in_the_attempts_job()
    {
        using var held = StartCell("findstr x");
        var (cell, cellStart) = await CellChild(held);
        var stageStart = WindowsProcessTable.StartOf(held.Process.Id)!.Value;
        Assert.NotNull(held.Container);

        var stage = held.Container!.Contains(new PeerFacts(held.Process.Id, stageStart, DateTimeOffset.UtcNow));
        Assert.True(stage.Is == Membership.Member, "the stage (control) is not a member: " + stage.Why);
        var verdict = held.Container.Contains(new PeerFacts(cell, cellStart, DateTimeOffset.UtcNow));
        _out.WriteLine($"cell process {cell}: {verdict.Is} — {verdict.Why}");
        Assert.True(verdict.Is == Membership.Outside, $"a cell's process was answered {verdict.Is}: {verdict.Why}");
        Assert.Contains("AppContainer", verdict.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// CARD 3 — THE ATTEMPT'S PROCESSES ARE ITS JOB'S: a reader of the same name started OUTSIDE the cell after it is never listed; the job
    /// lists the stage and the cell's reader, and the benchmark's cell members are the cell's alone (each an AppContainer's); after the cancel
    /// the cell's reader is gone and the outside one still runs.
    /// </summary>
    [WindowsFact]
    public async Task The_attempts_processes_are_its_jobs_and_never_a_process_outside()
    {
        Process? outside = null;
        try
        {
            var held = StartCell("findstr x");
            int cell;
            using (held)
            {
                (cell, _) = await CellChild(held);
                var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "findstr.exe"), "x")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
                outside = Process.Start(psi)!;

                var members = held.JobMembers();
                Assert.NotNull(members);
                _out.WriteLine($"job members: {string.Join(", ", members!)}; stage {held.Process.Id}, cell {cell}, outside {outside.Id}");
                Assert.Contains(held.Process.Id, members);
                Assert.Contains(cell, members);
                Assert.DoesNotContain(outside.Id, members);

                var cellMembers = WindowsCellBenchmark.CellMembers(held)!;
                Assert.Contains(cellMembers, m => m.Pid == cell && m.Name == "findstr");
                Assert.DoesNotContain(cellMembers, m => m.Pid == outside.Id || m.Pid == held.Process.Id);
                Assert.All(cellMembers, m => Assert.StartsWith("S-1-15-2-", m.Sid, StringComparison.Ordinal));
            }
            Assert.Null(held.JobMembers());
            Assert.True(await Gone(cell), $"the cell's reader {cell} outlived the cancel");
            Assert.False(outside.HasExited, "the reader outside the cell was ended with the cell");
        }
        finally
        {
            try { outside?.Kill(); } catch (Exception) { /* gone */ }
            outside?.Dispose();
        }
    }

    /// <summary>
    /// THE LINE MAIN'S ROW RAN (u-kit-socket-2's box run printed "La syntaxe du nom de fichier…" and counted it held): a quoted path on a cell's cmd line reaches cmd as the C
    /// runtime's <c>\"</c>, which it cannot parse, so the command never ran and the row printed DENIED. Through a real cell and the same line outside one, this is no denial.
    /// </summary>
    [WindowsFact]
    public async Task A_row_whose_line_cannot_run_fails_the_proof_though_it_printed_denied()
    {
        var ws = Directory.CreateDirectory(Path.Combine(_root, "broken-ws")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_root, "broken-outside")).FullName;
        var target = Path.Combine(_root, "target.txt");
        File.WriteAllText(target, "target");
        var (held, seen) = await WindowsCellBenchmark.DenialRowAsync(Policy(ws, CellEgress.None), [new DenialProbe("READ", $"type \"{target}\" > {{sink}}", target, target)],
            outside, Launcher(), CellPlatform.Read());
        _out.WriteLine(seen);
        Assert.False(held, "a line cmd cannot parse passed for a denial: " + seen);
        Assert.Contains("the same line fails outside a cell too", seen, StringComparison.Ordinal);
    }

    /// <summary>
    /// A FORBIDDEN LINE, MEASURED ALL THREE WAYS: the workspace target opens in the cell, the forbidden one is denied there, and the same line opens it outside. A path holding a
    /// space goes through as one argument. The control run of the row above: the same engine, a line that runs.
    /// </summary>
    [WindowsFact]
    public async Task A_row_whose_line_runs_is_held_only_by_the_cell()
    {
        var ws = Directory.CreateDirectory(Path.Combine(_root, "ok-ws")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_root, "ok-outside")).FullName;
        var control = Path.Combine(ws, "control.txt");
        File.WriteAllText(control, "control");
        var folder = Directory.CreateDirectory(Path.Combine(_root, "a folder")).FullName;
        var target = Path.Combine(folder, "target.txt");
        File.WriteAllText(target, "target");
        var (held, seen) = await WindowsCellBenchmark.DenialRowAsync(Policy(ws, CellEgress.None), [new DenialProbe("READ", "type {path} > {sink}", target, control)],
            outside, Launcher(), CellPlatform.Read());
        _out.WriteLine(seen);
        Assert.True(held, seen);
    }

    /// <summary>
    /// R29 F-D2, THE RULE AT EVERY LAUNCH: a proof names web and the rule stands: web. The rule removed since, the proof untouched: the firewall is read and the launch runs under none.
    /// Read for real here, every slot's two rules; the decision itself is <c>CellWindowsProviderTests</c>' on every platform.
    /// </summary>
    [WindowsAdminFact]
    public void A_rule_removed_since_the_proof_sends_the_next_launch_to_none()
    {
        RequireElevated();
        var rules = WindowsCellEgress.RulesFor(Paths.Home);
        using var db = TestEnv.NewDb();
        Directory.CreateDirectory(Paths.State);
        File.WriteAllText(Path.Combine(Paths.State, "trade.sha256"), new string('a', 64));
        var platform = CellPlatform.Read();
        new CellProofStore(db).Record(RouteA.WindowsProvider, CellBenchmark.Profile(RouteA.WindowsProvider, platform, "pin"), [CellEgress.None.Name, CellEgress.Web.Name]);
        try
        {
            WindowsCellEgress.RemoveForTests(rules);
            WindowsCellEgress.Place(rules);
            Assert.True(WindowsCellEgress.Holds(Paths.Home));
            Assert.Equal(CellEgress.Web.Name, RouteA.AdmissionOn(new CellRoute(db), "pin", platform, CellEgress.Web).Egress.Name);

            WindowsCellEgress.RemoveForTests([rules[^1]]);
            Assert.False(WindowsCellEgress.Holds(Paths.Home));
            var (proof, egress, refusal) = RouteA.AdmissionOn(new CellRoute(db), "pin", platform, CellEgress.Web);
            Assert.Null(refusal);
            Assert.NotNull(proof);
            Assert.Equal(CellEgress.None.Name, egress.Name);
        }
        finally { WindowsCellEgress.RemoveForTests(rules); }
    }

    /// <summary>
    /// R29 F-D3: THE SWEEP DECIDES "NOT A LINK" FROM WHAT IT ENUMERATED, THEN GOES IN AS THE OWNER. With a process of the slot's container alive, the sweep refuses and the storage is
    /// untouched; with it gone, the same sweep empties it.
    /// </summary>
    [WindowsFact]
    public async Task A_slots_storage_is_left_alone_while_a_process_of_its_container_runs()
    {
        var profile = WindowsCellStage.ProfileName(WindowsCellSlots.Count);
        var sid = AppContainerCell.Profile(profile, "A TradeAgent cell: an AI's programs, confined");
        var temp = Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", profile, "AC", "Temp")).FullName;
        var left = Path.Combine(temp, "left-behind.txt");
        File.WriteAllText(left, "left");
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        using var cell = AppContainerCell.Start(Cmd, ["/d", "/c", "findstr x"], temp,
            [new("SystemRoot", windows), new("PATH", Path.GetDirectoryName(Cmd)!)], sid, []);
        Assert.Contains(cell.Id, await Until(() => WindowsCellStage.ProcessesOf(sid), p => p.Count > 0));

        var refused = Assert.Throws<TradeAgentException>(() => WindowsCellStage.SweepSlot(WindowsCellSlots.Count));
        _out.WriteLine(refused.Message);
        Assert.Contains("still has", refused.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(left), "the sweep walked the storage of a container with a process alive");

        cell.Kill();
        await Until(() => WindowsCellStage.ProcessesOf(sid), p => p.Count == 0);
        Assert.True(WindowsCellStage.SweepSlot(WindowsCellSlots.Count));
        Assert.False(File.Exists(left), "the sweep did not empty the storage once nothing ran in it");
    }

    /// <summary>
    /// R29 F-D4: A STAGE IN A JOB THAT WOULD NOT END IT IS NO STAGE OF TRADEAGENT'S. <c>IsProcessInJob</c> with no job answers true for any job at all. In a job that is not kill-on-close
    /// the stage refuses and starts nothing; the attempt's own job admits it, which every other cell row here measures.
    /// </summary>
    [WindowsFact]
    public async Task A_stage_in_a_job_that_would_not_end_it_starts_nothing()
    {
        var ws = Directory.CreateDirectory(Path.Combine(_root, "job-ws")).FullName;
        var launcher = Launcher();
        var psi = new ProcessStartInfo(launcher[0]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in (string[])[.. launcher.Skip(1), ContainedLaunch.Flag, ContainedLaunch.CellFlag, Policy(ws, CellEgress.None).Write(), Cmd, "/d", "/c", "echo", "ENTERED"])
            psi.ArgumentList.Add(a);
        psi.Environment["TRADEAGENT_HOME"] = Paths.Home;
        var job = CreateJobObject(IntPtr.Zero, null);
        Assert.NotEqual(IntPtr.Zero, job);
        try
        {
            using var stage = Process.Start(psi)!;
            Assert.True(AssignProcessToJobObject(job, stage.Handle), "the test could not put the stage in its job");
            var so = stage.StandardOutput.ReadToEndAsync();
            var se = stage.StandardError.ReadToEndAsync();
            await stage.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            var (printed, said) = (await so, await se);
            _out.WriteLine($"exit {stage.ExitCode}: {printed} | {said}");
            Assert.Equal(WindowsCellStage.Failed, stage.ExitCode);
            Assert.DoesNotContain("ENTERED", printed, StringComparison.Ordinal);
            Assert.Contains("job", said, StringComparison.Ordinal);
        }
        finally { CloseHandle(job); }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateJobObjectW")]
    static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr handle);

    static async Task<T> Until<T>(Func<T> read, Func<T, bool> done)
    {
        var seen = read();
        for (var i = 0; i < 300 && !done(seen); i++)
        {
            await Task.Delay(50);
            seen = read();
        }
        return seen;
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------------------

    static void RequireElevated() =>
        Assert.True(WindowsCellEgress.Elevated(), $"missing prerequisite: an elevated session ({WindowsAdminFactAttribute.Variable}=1 says this runner is CI's own)");

    /// <summary>A web policy over a fresh workspace, and the benchmark's per-slot web rows for it.</summary>
    async Task<IReadOnlyList<BenchRow>> WebRows()
    {
        var ws = Directory.CreateDirectory(Path.Combine(_root, "web-ws")).FullName;
        return await WindowsCellBenchmark.WebRowsAsync(Policy(ws, CellEgress.Web), ws, Launcher(), CellPlatform.Read());
    }

    /// <summary>
    /// A cell's policy over <paramref name="ws"/>, as <c>CellWindowsTests</c> makes it: the system's own programs run without a grant (Windows
    /// grants every AppContainer its system folders), so no program root is named — the stage refuses to write an ACE on System32.
    /// </summary>
    CellPolicy Policy(string ws, CellEgress egress)
    {
        var name = Path.GetFileName(ws);
        var p = RouteA.WindowsPolicy(ws, Cmd, Path.GetDirectoryName(Cmd)!, Directory.CreateDirectory(Path.Combine(_root, "c-" + name)).FullName,
            Directory.CreateDirectory(Path.Combine(_root, "h-" + name)).FullName, egress);
        return new CellPolicy(p.Workspace, p.ReadOnly, [], p.Environment, p.Egress);
    }

    ContainedProcess StartCell(string script)
    {
        var ws = Directory.CreateDirectory(Path.Combine(_root, "ws-" + Guid.NewGuid().ToString("n")[..6])).FullName;
        var policy = Policy(ws, CellEgress.None);
        var psi = new ProcessStartInfo(Cmd) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "/d", "/c", script }) psi.ArgumentList.Add(a);
        var held = ProcessContainment.Start(psi, policy, Launcher());
        _out.WriteLine($"started: {held.Mechanism}");
        return held;
    }

    /// <summary>The cell's reader: the process of the attempt's job named findstr whose token is an AppContainer's.</summary>
    static async Task<(int Pid, ulong Start)> CellChild(ContainedProcess held)
    {
        IReadOnlyList<WindowsCellBenchmark.CellMember>? seen = null;
        for (var i = 0; i < 200; i++)
        {
            seen = WindowsCellBenchmark.CellMembers(held);
            if (seen?.FirstOrDefault(m => m.Name == "findstr") is { } reader) return (reader.Pid, reader.Start);
            await Task.Delay(50);
        }
        var members = held.JobMembers();
        throw new Xunit.Sdk.XunitException($"the cell's reader never appeared in the attempt's job: job members {(members is null ? "unlisted" : string.Join(", ", members))}; " +
            $"cell members {(seen is null ? "unlisted" : string.Join(", ", seen.Select(m => $"{m.Name} {m.Pid}")))}; stage {(held.Process.HasExited ? $"exited {held.Process.ExitCode}: {held.Process.StandardError.ReadToEnd()}" : "running")}");
    }

    static async Task<bool> Gone(int pid)
    {
        for (var i = 0; i < 100; i++)
        {
            try { using var p = Process.GetProcessById(pid); if (p.HasExited) return true; }
            catch (ArgumentException) { return true; }
            await Task.Delay(50);
        }
        return false;
    }
}
